using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace Beastmaster;

public sealed class BeastmasterAutoCaptureService : IDisposable
{
    private const uint BeastmasterClassJobId = 43;
    private const uint BeastmasterUltimateActionId = 47093;
    private const uint BeastmasterReleaseBaseActionId = 44890;
    private const uint DrumActionId = 44905;
    private const uint CheerActionId = 44904;
    private const uint WhitePhysicalThirdFormActionId = 44931;
    private const uint PurplePhysicalThirdFormActionId = 44930;
    private const uint WhiteMagicalThirdFormActionId = 44933;
    private const uint PurpleMagicalThirdFormActionId = 44932;
    private const uint WhistleOneActionId = 44881;
    private const uint WhistleTwoActionId = 44892;
    private const uint WhistleThreeActionId = 44894;
    private const uint FinalStrikeActionId = 44891;
    private const uint SmashActionId = 44879;
    private const uint BiteActionId = 44883;
    private const uint ShieldActionId = 44885;
    private const uint CaptureActionId = 44880;
    private const uint CaptureStatusId = 4626;
    private readonly BeastmasterConfiguration configuration;
    private readonly BeastmasterSequenceService sequenceService;
    private readonly BeastmasterRuleService ruleService;
    private readonly uint smashActionId;
    private readonly uint biteActionId;
    private readonly uint shieldActionId;
    private readonly uint captureActionId;
    private readonly uint captureStatusId;
    private DateTime nextCheckUtc = DateTime.MinValue;
    private DateTime nextActionUtc = DateTime.MinValue;
    private DateTime capturePendingUntilUtc = DateTime.MinValue;
    private ulong captureTargetId;
    private ulong activeTargetId;
    private uint pendingCooperationActionId;
    private uint pendingCooperationStatusId;
    private DateTime pendingCooperationUntilUtc = DateTime.MinValue;
    private DateTime nextReleaseAttemptUtc = DateTime.MinValue;
    private uint pendingWhistleActionId;
    private DateTime pendingWhistleUntilUtc = DateTime.MinValue;
    private DateTime nextWhistleAttemptUtc = DateTime.MinValue;
    private DateTime nextFinalStrikeAttemptUtc = DateTime.MinValue;
    private DateTime resurrectionProtectionUntilUtc = DateTime.MinValue;
    private bool playerWasDead;
    private bool reportedMissingData;
    private int whistleRotationStage = -1;
    private bool whistleRotationWaitingForCooldown;
    private DateTime whistleRotationNextActionUtc = DateTime.MinValue;
    private DateTime lastSuccessfulActionUtc = DateTime.MinValue;
    private string lastAutoOutputSummary = string.Empty;
    private readonly List<string> currentBattleLog = [];
    private readonly List<(string Label, string Text)> battleLogs = [];
    private bool wasInCombat;
    private readonly Dictionary<string, string> battleLogModuleStates = new(StringComparer.Ordinal);

    public string StatusText { get; private set; } = "ターゲット待機中";

    public string NextActionName { get; private set; } = "-";

    public string NextActionReason { get; private set; } = "";

    public string ResourceStatus { get; private set; } = "HUD未取得";

    public string AdvancedActionStatus { get; private set; } = "未評価";

    public string TargetStatus { get; private set; } = "有効ターゲットなし";

    public float TargetHpPercent { get; private set; }

    public string CaptureState { get; private set; } = "未開始";

    public string ManualActionStatus { get; private set; } = "未実行";

    private void ReportAutoOutputDiagnostic(string actionName, string reason, string reasonKey)
    {
        _ = actionName;
        _ = reason;
        _ = reasonKey;
    }

    private void ReportAutoOutputSuccess(string actionName, uint actionId)
    {
        _ = actionName;
        _ = actionId;
        lastSuccessfulActionUtc = DateTime.UtcNow;
        RecordBattleLog($"{actionName}（アクションID: {actionId}）を実行要求しました");
    }

    private void RecordBattleLog(string message)
    {
        if (!configuration.AutoOutputDiagnosticsEnabled || !wasInCombat)
        {
            return;
        }

        if (currentBattleLog.Count >= 500)
        {
            currentBattleLog.RemoveAt(0);
        }

        currentBattleLog.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
    }

    private void UpdateBattleLogState(bool inCombat)
    {
        if (!wasInCombat && inCombat)
        {
            currentBattleLog.Clear();
            battleLogModuleStates.Clear();
            currentBattleLog.Add($"[{DateTime.Now:HH:mm:ss}] 戦闘開始");
        }
        else if (wasInCombat && !inCombat)
        {
            currentBattleLog.Add($"[{DateTime.Now:HH:mm:ss}] 戦闘終了");
            battleLogs.Insert(0, ($"戦闘 {currentBattleLog[0][1..9]}", string.Join(Environment.NewLine, currentBattleLog)));
            if (battleLogs.Count > 10) battleLogs.RemoveAt(battleLogs.Count - 1);
            currentBattleLog.Clear();
        }

        wasInCombat = inCombat;
    }

    public string WhistleRotationStatus { get; private set; } = "無効";

    public BeastmasterAutoCaptureService(
        BeastmasterConfiguration configuration,
        BeastmasterSequenceService sequenceService,
        BeastmasterRuleService ruleService)
    {
        this.configuration = configuration;
        this.sequenceService = sequenceService;
        this.ruleService = ruleService;
        // Action and status RowId are language-independent; names differ by client locale.
        smashActionId = SmashActionId;
        biteActionId = BiteActionId;
        shieldActionId = ShieldActionId;
        captureActionId = CaptureActionId;
        captureStatusId = CaptureStatusId;
        DalamudApi.Framework.Update += OnFrameworkUpdate;
    }

    public bool IsEnabled => configuration.AutoCaptureEnabled;

    public bool IsPaused => configuration.AutoOutputPaused;

    public bool TryCapture => configuration.AutoCaptureTryCapture;

    public bool ForceCapture => configuration.ForceCaptureEnabled;

    public bool ActiveAttack => configuration.ActiveAttackEnabled;

    public bool FinalStrikeEnabled => configuration.AutoFinalStrikeEnabled;

    public bool BasicComboEnabled => configuration.BasicComboEnabled;

    public IReadOnlyList<string> BattleLogLabels => battleLogs.Select(log => log.Label).ToArray();

    public string GetBattleLog(int index)
        => index >= 0 && index < battleLogs.Count ? battleLogs[index].Text : "戦闘ログはありません。";

    public void ClearBattleLogs() => battleLogs.Clear();

    public void SetEnabled(bool enabled)
    {
        configuration.AutoCaptureEnabled = enabled;
        configuration.Save();
        if (enabled)
        {
            DalamudApi.ChatGui.Print("[Beastmaster] 自動戦闘（とらえる優先）を開始しました。手動でターゲット中の敵にのみ有効です。");
        }
        else
        {
            sequenceService.Abort("自動出力が無効化されました");
            nextActionUtc = DateTime.MinValue;
            nextReleaseAttemptUtc = DateTime.MinValue;
            nextFinalStrikeAttemptUtc = DateTime.MinValue;
            ResetCaptureState();
            ResetCooperationState();
            ResetAutoWhistle();
            DalamudApi.ChatGui.Print("[Beastmaster] 自動戦闘を終了しました。");
        }
    }

    public void SetTryCapture(bool enabled)
    {
        configuration.AutoCaptureTryCapture = enabled;
        if (!enabled)
        {
            configuration.ForceCaptureEnabled = false;
        }
        configuration.Save();
    }

    public void SetForceCapture(bool enabled)
    {
        configuration.ForceCaptureEnabled = enabled;
        if (enabled)
        {
            configuration.AutoCaptureTryCapture = true;
        }
        configuration.Save();
    }

    public void SetBasicComboEnabled(bool enabled)
    {
        configuration.BasicComboEnabled = enabled;
        configuration.Save();
    }

    public void SetActiveAttack(bool enabled)
    {
        configuration.ActiveAttackEnabled = enabled;
        configuration.Save();
    }

    public void SetFinalStrikeEnabled(bool enabled)
    {
        configuration.AutoFinalStrikeEnabled = enabled;
        configuration.Save();
    }

    public void SetPaused(bool paused)
    {
        configuration.AutoOutputPaused = paused;
        configuration.Save();
        if (paused)
        {
            sequenceService.Abort("自動出力が一時停止されました");
            nextActionUtc = DateTime.MinValue;
            nextReleaseAttemptUtc = DateTime.MinValue;
            nextFinalStrikeAttemptUtc = DateTime.MinValue;
            ResetCaptureState();
            ResetCooperationState();
            ResetAutoWhistle();
        }
    }

    public unsafe bool TryUseUltimate()
    {
        var gauge = BeastmasterGaugeSnapshot.Read();
        var entry = gauge.SummonEntry;
        if (!gauge.Available || entry == null)
        {
            ManualActionStatus = "ジョブHUDまたは現在の魔獣が利用不可";
            return false;
        }

        if (gauge.Tp < BeastmasterGaugeSnapshot.ComboGaugeRequirement
            || gauge.BeastPower < BeastmasterGaugeSnapshot.ComboGaugeRequirement)
        {
            ManualActionStatus = $"リソース不足：技力 {gauge.Tp}/250、魔獣技力 {gauge.BeastPower}/250";
            return false;
        }

        if (DalamudApi.TargetManager.Target is not IBattleChara target
            || target.ObjectKind != ObjectKind.BattleNpc
            || !target.IsTargetable
            || target.IsDead
            || target.CurrentHp == 0)
        {
            ManualActionStatus = "有効なターゲットではありません";
            return false;
        }

        var actionManager = ActionManager.Instance();
        if (actionManager == null)
        {
            ManualActionStatus = "ActionManagerが利用不可";
            return false;
        }

        var actionId = BeastmasterUltimateActionId;
        if (!BeastmasterActionHelper.IsPlayerInActionRange(
                DalamudApi.ObjectTable.LocalPlayer!,
                target,
                actionId,
                out var playerDistance,
                out var actionRange))
        {
            ManualActionStatus = $"リミットブレイク射程内への移動待機中（現在 {playerDistance:0.##}/{actionRange:0.##} ヤルム）";
            return false;
        }
        var actionStatus = actionManager->GetActionStatus(ActionType.Action, actionId, target.GameObjectId);
        if (actionStatus != 0)
        {
            ManualActionStatus = $"リミットブレイク現在使用不可（コード {actionStatus}）";
            return false;
        }

        if (!actionManager->UseAction(ActionType.Action, actionId, target.GameObjectId))
        {
            ManualActionStatus = "リミットブレイク実行要求失敗";
            return false;
        }

        nextActionUtc = DateTime.UtcNow.AddMilliseconds(700);
        if ((configuration.BeastHeartCooperationEnabled || configuration.BeastSoulCooperationEnabled)
            && TryGetCooperationAction(gauge, configuration.BeastHeartCooperationEnabled, out var firstCooperationActionId, out var followUpActionId, out var requiredStatusId)
            && firstCooperationActionId == BeastmasterUltimateActionId)
        {
            pendingCooperationActionId = followUpActionId;
            pendingCooperationStatusId = requiredStatusId;
            pendingCooperationUntilUtc = DateTime.UtcNow.AddSeconds(7);
        }

        ManualActionStatus = $"実行要求送信: {GetActionName(actionId)}";
        return true;
    }

    private static unsafe bool TryUseAdvancedAction(
        ActionManager* actionManager,
        uint actionId,
        ulong targetId,
        uint actionStatus)
    {
        return actionStatus == 0
            && actionManager->UseAction(ActionType.Action, actionId, targetId);
    }

    public void Dispose()
        => DalamudApi.Framework.Update -= OnFrameworkUpdate;

    private unsafe void OnFrameworkUpdate(IFramework framework)
    {
        _ = framework;
        UpdateBattleLogState(DalamudApi.Condition[ConditionFlag.InCombat]);
        if (!configuration.AutoCaptureEnabled)
        {
            StatusText = "自動戦闘OFF";
            NextActionName = "-";
            NextActionReason = "自動戦闘が無効";
            ResourceStatus = "HUD未取得";
            AdvancedActionStatus = "自動戦闘が無効";
            TargetStatus = "ターゲットなし";
            TargetHpPercent = 0f;
            ResetCaptureState();
            ResetWhistleRotation("自動戦闘が無効");
            ResetAutoWhistle();
            return;
        }

        if (configuration.AutoOutputPaused)
        {
            StatusText = "自動戦闘を一時停止中";
            NextActionName = "-";
            NextActionReason = "一時停止中";
            AdvancedActionStatus = "一時停止中";
            ResetCaptureState();
            ResetCooperationState();
            ResetAutoWhistle();
            return;
        }

        if (!configuration.WhistleRotationEnabled
            && (whistleRotationStage >= 0 || whistleRotationWaitingForCooldown))
        {
            ResetWhistleRotation("無効");
        }

        var now = DateTime.UtcNow;
        if (now < nextCheckUtc)
        {
            return;
        }

        nextCheckUtc = now.AddMilliseconds(100);
        if (now < nextActionUtc)
        {
            StatusText = "実行可能状態の待機中";
            NextActionReason = "アクション待機中";
            return;
        }

        if (DalamudApi.Condition[ConditionFlag.BetweenAreas])
        {
            sequenceService.Abort("シーケンス中止：エリア移動またはテレポ中");
            StatusText = "実行可能状態の待機中";
            NextActionReason = "エリア移動／テレポ中";
            return;
        }

        if (DalamudApi.Condition[ConditionFlag.Mounted])
        {
            StatusText = "実行可能状態の待機中";
            NextActionReason = "マウント騎乗中";
            return;
        }

        if (DalamudApi.Condition[ConditionFlag.OccupiedInCutSceneEvent])
        {
            StatusText = "実行可能状態の待機中";
            NextActionReason = "イベント・カットシーン中";
            return;
        }

        var player = DalamudApi.ObjectTable.LocalPlayer;
        if (player == null)
        {
            StatusText = "キャラクター読み込み待ち";
            NextActionName = "-";
            NextActionReason = "キャラクター未ロード";
            return;
        }

        if (player.ClassJob.RowId != BeastmasterClassJobId)
        {
            sequenceService.Abort("シーケンス中止：現在のジョブが魔獣使いではありません");
            StatusText = "魔獣使いに切り替えてください";
            NextActionName = "-";
            NextActionReason = "現在のクラス・ジョブが魔獣使いではありません";
            ResourceStatus = "魔獣使いのみ利用可能";
            return;
        }

        if (player.CurrentHp == 0)
        {
            playerWasDead = true;
            resurrectionProtectionUntilUtc = DateTime.MinValue;
            sequenceService.Abort("シーケンス中止：戦闘不能");
            StatusText = "実行可能状態の待機中";
            NextActionReason = "戦闘不能";
            return;
        }

        if (playerWasDead)
        {
            playerWasDead = false;
            resurrectionProtectionUntilUtc = now.AddSeconds(3);
            nextActionUtc = DateTime.MinValue;
            nextReleaseAttemptUtc = DateTime.MinValue;
            nextFinalStrikeAttemptUtc = DateTime.MinValue;
            ResetCaptureState();
            ResetCooperationState();
            ResetAutoWhistle();
        }

        if (now < resurrectionProtectionUntilUtc)
        {
            StatusText = "蘇生保護中";
            NextActionName = "-";
            NextActionReason = $"蘇生後待機 {(resurrectionProtectionUntilUtc - now).TotalSeconds:0.0} 秒";
            return;
        }

        if (player.IsCasting)
        {
            StatusText = "実行可能状態の待機中";
            NextActionReason = "キャスト詠唱中";
            return;
        }

        var gauge = BeastmasterGaugeSnapshot.Read();
        ResourceStatus = gauge.Available
            ? $"技力 {gauge.Tp}/250、獣力 {gauge.BeastPower}/250"
            : gauge.Status;
        AdvancedActionStatus = GetAdvancedActionStatus(gauge);

        if (pendingCooperationActionId != 0 && pendingCooperationUntilUtc <= now)
        {
            pendingCooperationActionId = 0;
            pendingCooperationStatusId = 0;
            pendingCooperationUntilUtc = DateTime.MinValue;
        }

        if (smashActionId == 0 || biteActionId == 0 || shieldActionId == 0 || captureActionId == 0 || captureStatusId == 0)
        {
            if (!reportedMissingData)
            {
                reportedMissingData = true;
                StatusText = "アクション・ステータス解析失敗";
                NextActionName = "-";
                NextActionReason = "自動戦闘に必要なアクション・ステータスが見つかりません";
                SetEnabled(false);
                DalamudApi.ChatGui.Print("[Beastmaster] 自動戦闘に必要なアクションまたはステータスを解析できませんでした。DEBUGタブをご確認ください。");
            }

            return;
        }

        var target = DalamudApi.TargetManager.Target as IBattleChara;
        if (target is not null
            && (target.EntityId == player.EntityId
                || target.ObjectKind != ObjectKind.BattleNpc
                || !target.IsTargetable
                || target.IsDead
                || target.CurrentHp == 0))
        {
            target = null;
        }

        if (target == null)
        {
            if (activeTargetId != 0)
            {
                ResetTargetScopedState();
            }
        }
        else if (activeTargetId != target.EntityId)
        {
            if (activeTargetId != 0)
            {
                ResetTargetScopedState();
            }

            activeTargetId = target.EntityId;
        }

        var actionManager = ActionManager.Instance();
        if (actionManager == null)
        {
            StatusText = "ActionManager待機中";
            NextActionReason = "ActionManagerが利用不可";
            return;
        }

        if (sequenceService.TryHandle(actionManager, gauge, target, now))
        {
            StatusText = sequenceService.Status;
            NextActionName = "スキルシーケンス";
            NextActionReason = "スキルシーケンスがルールモードおよび通常ACRを制御中";
            return;
        }

        if (ruleService.TryHandle(actionManager, player, target, now))
        {
            StatusText = "ルールモード実行中...";
            NextActionName = "ルールアクション";
            NextActionReason = ruleService.LastDiagnostic;
            nextActionUtc = now.AddMilliseconds(700);
            return;
        }

        if (!configuration.AutoWhistleEnabled && pendingWhistleActionId != 0)
        {
            ResetAutoWhistle();
        }

        if (configuration.AutoWhistleEnabled
            && TryUseAutoWhistle(actionManager, gauge, now))
        {
            return;
        }

        // 呼笛ローテーションのエントリを一時的に無効化し、後で復元できるように実装を保持。
        // if ((configuration.WhistleRotationEnabled || whistleRotationWaitingForCooldown || whistleRotationStage >= 0)
        //     && TryRunWhistleRotation(actionManager, target, now))
        // {
        //     return;
        // }

        if (target is null)
        {
            StatusText = "敵ターゲット待機中";
            NextActionName = "-";
            NextActionReason = "有効なBattleNpcターゲットがありません";
            TargetStatus = "ターゲットなし";
            TargetHpPercent = 0f;
            ResetCaptureState();
            ResetCooperationState();
            return;
        }

        if (captureTargetId != target.EntityId)
        {
            captureTargetId = target.EntityId;
            capturePendingUntilUtc = DateTime.MinValue;
            CaptureState = "未開始";
        }

        var hasOwnCapture = target.StatusList.Any(status =>
            status.StatusId == captureStatusId && status.SourceId == player.EntityId);
        var hasOtherCapture = target.StatusList.Any(status =>
            status.StatusId == captureStatusId && status.SourceId != player.EntityId);
        var targetHpPercent = target.MaxHp == 0
            ? 100f
            : target.CurrentHp * 100f / target.MaxHp;
        TargetHpPercent = Math.Clamp(targetHpPercent, 0f, 100f);
        TargetStatus = hasOwnCapture
            ? "自身の「とらえる」付与済み"
            : hasOtherCapture
                ? "他人の「とらえる」付与済み"
                : "「とらえる」未付与";

        if (!configuration.ActiveAttackEnabled && !DalamudApi.Condition[ConditionFlag.InCombat])
        {
            StatusText = "戦闘開始待機中";
            NextActionName = "-";
            NextActionReason = "アクティブ攻撃が無効のため、非戦闘時は攻撃・とらえるを行いません";
            ResetCaptureState();
            ResetCooperationState();
            return;
        }

        if (hasOwnCapture)
        {
            CaptureState = "「とらえる」付与確認済み";
            capturePendingUntilUtc = DateTime.MinValue;
        }
        else if (capturePendingUntilUtc > now)
        {
            CaptureState = "結果待機中";
        }
        else if (capturePendingUntilUtc != DateTime.MinValue)
        {
            CaptureState = "付与未確認（再試行可）";
            capturePendingUntilUtc = DateTime.MinValue;
        }

        var canCapture = targetHpPercent <= configuration.CaptureHpThreshold;
        EmitAutoOutputDiagnosticSummary(actionManager, player, target, gauge, targetHpPercent, canCapture, now);
        var capturePending = capturePendingUntilUtc > now;
        if (capturePending)
        {
            StatusText = "「とらえる」結果待機中";
            NextActionName = "-";
            NextActionReason = "自身の「とらえる」ステータス更新待ち";
            return;
        }

        uint actionId;

        if ((configuration.BeastHeartCooperationEnabled || configuration.BeastSoulCooperationEnabled)
            && pendingCooperationActionId != 0)
        {
            actionId = pendingCooperationActionId;
            StatusText = "獣心技連携中...";
            NextActionName = GetActionName(actionId);
            if (pendingCooperationStatusId != 0 && !HasSelfStatus(pendingCooperationStatusId))
            {
                NextActionReason = $"自身への{GetAttributeStatusName(pendingCooperationStatusId)}（{pendingCooperationStatusId}）付与待ち";
                ReportAutoOutputDiagnostic(NextActionName, NextActionReason, $"status-{pendingCooperationStatusId}");
            }
            else
            {
                NextActionReason = $"自身に{GetAttributeStatusName(pendingCooperationStatusId)}が付与されたため、連携2段目を実行";

                if (!BeastmasterActionHelper.IsPlayerInActionRange(
                        player,
                        target,
                        actionId,
                        out var followUpDistance,
                        out var followUpRange))
                {
                    NextActionReason = $"連携技射程内への移動待機中（現在 {followUpDistance:0.##}/{followUpRange:0.##} ヤルム）";
                    ReportAutoOutputDiagnostic(NextActionName, $"射程外（現在 {followUpDistance:0.##}/{followUpRange:0.##} ヤルム）", "range");
                    return;
                }

                var cooperationStatus = actionManager->GetActionStatus(ActionType.Action, actionId, target.GameObjectId);
                var cooperationUsed = TryUseAdvancedAction(actionManager, actionId, target.GameObjectId, cooperationStatus);
                if (!cooperationUsed)
                {
                    ReportAutoOutputDiagnostic(NextActionName,
                        $"スキルステータスコード {cooperationStatus}；技力 {gauge.Tp}/250、獣力 {gauge.BeastPower}/250",
                        $"status-{cooperationStatus}");
                }
                if (cooperationUsed)
                {
                    ReportAutoOutputSuccess(NextActionName, actionId);
                    nextActionUtc = now.AddMilliseconds(700);
                    ResetCooperationState();
                }

                return;
            }
        }

        if (configuration.AutoReleaseEnabled
            && gauge.SummonEntry != null
            && TryUseReleaseAction(actionManager, gauge, target, now))
        {
            return;
        }

        if (TryUseFinalStrike(actionManager, gauge, target.GameObjectId, now))
        {
            return;
        }

        if (configuration.AutoDrumEnabled
            && (gauge.BeastHeartStacks == 0 || IsThirdFormEnabled)
            && TryUseEnabledSelfAction(actionManager, DrumActionId, "鼓舞", now))
        {
            return;
        }

        if (configuration.AutoCheerEnabled
            && (gauge.BeastSoulStacks == 0 || IsThirdFormEnabled)
            && TryUseEnabledSelfAction(actionManager, CheerActionId, "声援", now))
        {
            return;
        }

        if ((configuration.PhysicalThirdFormEnabled || configuration.MagicalThirdFormEnabled)
            && TryUseThirdFormAction(actionManager, gauge, target.GameObjectId, now))
        {
            return;
        }

        if ((configuration.BeastHeartCooperationEnabled || configuration.BeastSoulCooperationEnabled)
            && TryGetCooperationAction(gauge, configuration.BeastHeartCooperationEnabled, out var cooperationActionId, out var cooperationFollowUpId, out var cooperationStatusId))
        {
            actionId = cooperationActionId;
            StatusText = "獣心技連携中...";
            NextActionName = GetActionName(actionId);
            NextActionReason = $"連携1段目実行、次段：{GetActionName(cooperationFollowUpId)}";

            if (!BeastmasterActionHelper.IsPlayerInActionRange(
                    player,
                    target,
                    actionId,
                    out var cooperationDistance,
                    out var cooperationRange))
            {
                NextActionReason = $"連携技射程内への移動待機中（現在 {cooperationDistance:0.##}/{cooperationRange:0.##} ヤルム）";
                ReportAutoOutputDiagnostic(NextActionName, $"射程外（現在 {cooperationDistance:0.##}/{cooperationRange:0.##} ヤルム）", "range");
                return;
            }

            var cooperationStatus = actionManager->GetActionStatus(ActionType.Action, actionId, target.GameObjectId);
            var cooperationUsed = TryUseAdvancedAction(actionManager, actionId, target.GameObjectId, cooperationStatus);
            if (!cooperationUsed)
            {
                NextActionReason = $"連携リクエスト失敗（状態コード {cooperationStatus}）";
                ReportAutoOutputDiagnostic(NextActionName,
                    $"スキルステータスコード {cooperationStatus}；技力 {gauge.Tp}/250、獣力 {gauge.BeastPower}/250",
                    $"status-{cooperationStatus}");
            }
            if (cooperationUsed)
            {
                ReportAutoOutputSuccess(NextActionName, actionId);
                nextActionUtc = now.AddMilliseconds(700);
                pendingCooperationActionId = cooperationFollowUpId;
                pendingCooperationStatusId = cooperationStatusId;
                pendingCooperationUntilUtc = now.AddSeconds(7);
            }

            return;
        }

        if (configuration.AutoCaptureTryCapture
            && canCapture
            && (configuration.ForceCaptureEnabled || !hasOwnCapture))
        {
            actionId = captureActionId;
        }
        else if (!configuration.BasicComboEnabled)
        {
            StatusText = "使用可能アクション待機中";
            NextActionName = "-";
            NextActionReason = "基本コンボ（1→2→3）が無効";
            return;
        }
        else
        {
            actionId = actionManager->Combo.Timer > 0f && actionManager->Combo.Action == biteActionId && player.Level >= 12
                ? shieldActionId
                : actionManager->Combo.Timer > 0f && actionManager->Combo.Action == smashActionId && player.Level >= 2
                    ? biteActionId
                    : smashActionId;
        }

        StatusText = configuration.AutoCaptureTryCapture
            ? configuration.ForceCaptureEnabled ? "強制捕獲中..." : "自動捕獲中..."
            : "自動攻撃中...";
        NextActionName = GetActionName(actionId);
        NextActionReason = configuration.AutoCaptureTryCapture && !configuration.ForceCaptureEnabled && !hasOwnCapture && !canCapture
            ? $"対象HP {targetHpPercent:0.#}%（捕獲閾値 {configuration.CaptureHpThreshold:0.#}% より高い）"
            : configuration.AutoCaptureTryCapture && configuration.ForceCaptureEnabled && !canCapture
                ? $"強制とらえる実行中（HP閾値制限あり）：ターゲットHP {targetHpPercent:0.#}% は閾値 {configuration.CaptureHpThreshold:0.#}% を超えています"
                : configuration.AutoCaptureTryCapture && configuration.ForceCaptureEnabled
                    ? "強制とらえるモード（ターゲットの捕獲バフ状態を無視）"
                : actionId == BeastmasterUltimateActionId
                ? "高度スキル有効：技力・獣力が必殺技条件を満たしています"
            : hasOwnCapture
                ? "対象には既に自身が付与した捕獲ステータスがあります"
                : hasOtherCapture
                    ? "他者が付与した捕獲ステータスがありますが、自身への判定には影響しません"
                    : "アクション使用可能";

        if (!BeastmasterActionHelper.IsPlayerInActionRange(
                player,
                target,
                actionId,
                out var playerDistance,
                out var actionRange))
        {
            if (actionId != captureActionId)
            {
                NextActionReason = $"スキル射程内への移動待機中（現在 {playerDistance:0.##}/{actionRange:0.##} ヤルム）";
                ReportAutoOutputDiagnostic(NextActionName, $"射程外（現在 {playerDistance:0.##}/{actionRange:0.##} ヤルム）", "range");
                return;
            }

            actionId = actionManager->Combo.Timer > 0f && actionManager->Combo.Action == biteActionId && player.Level >= 12
                ? shieldActionId
                : actionManager->Combo.Timer > 0f && actionManager->Combo.Action == smashActionId && player.Level >= 2
                    ? biteActionId
                    : smashActionId;
            NextActionName = GetActionName(actionId);
            if (!BeastmasterActionHelper.IsPlayerInActionRange(
                    player,
                    target,
                    actionId,
                    out playerDistance,
                    out actionRange))
            {
                NextActionReason = $"「とらえる」が射程外で、基本コンボも射程外です（現在 {playerDistance:0.##}/{actionRange:0.##} ヤルム）";
                ReportAutoOutputDiagnostic(NextActionName, $"射程外（現在 {playerDistance:0.##}/{actionRange:0.##} ヤルム）", "range");
                return;
            }
        }

        var availability = BeastmasterActionHelper.GetAvailability(actionId, target.GameObjectId);
        NextActionReason = availability.Reason;
        if (!availability.CanUse && actionId == captureActionId)
        {
            actionId = actionManager->Combo.Timer > 0f && actionManager->Combo.Action == biteActionId && player.Level >= 12
                ? shieldActionId
                : actionManager->Combo.Timer > 0f && actionManager->Combo.Action == smashActionId && player.Level >= 2
                    ? biteActionId
                    : smashActionId;
            NextActionName = GetActionName(actionId);
            if (!BeastmasterActionHelper.IsPlayerInActionRange(
                    player,
                    target,
                    actionId,
                    out playerDistance,
                    out actionRange))
            {
                NextActionReason = $"「とらえる」が使用不可で、基本コンボも射程外です（現在 {playerDistance:0.##}/{actionRange:0.##} ヤルム）";
                ReportAutoOutputDiagnostic(NextActionName, $"射程外（現在 {playerDistance:0.##}/{actionRange:0.##} ヤルム）", "range");
                return;
            }
            availability = BeastmasterActionHelper.GetAvailability(actionId, target.GameObjectId);
            NextActionReason = "「とらえる」が使用不可のため基本コンボにフォールバック：" + availability.Reason;
        }
        if (!availability.CanUse)
        {
            ReportAutoOutputDiagnostic(NextActionName,
                $"{availability.Reason}；技力 {gauge.Tp}/250、獣力 {gauge.BeastPower}/250",
                availability.Reason);
            return;
        }

        var actionStatus = actionManager->GetActionStatus(ActionType.Action, actionId, target.GameObjectId);
        var used = actionStatus == 0
            && actionManager->UseAction(ActionType.Action, actionId, target.GameObjectId);
        if (used)
        {
            ReportAutoOutputSuccess(NextActionName, actionId);
            nextActionUtc = now.AddMilliseconds(actionId == captureActionId ? 700 : actionId == BeastmasterUltimateActionId ? 700 : 250);
            if (actionId == captureActionId)
            {
                capturePendingUntilUtc = now.AddSeconds(7);
                CaptureState = "「とらえる」実行待ち...";
            }
            else
            {
                CaptureState = "未開始";
            }
        }
        else if (actionId == captureActionId)
        {
            CaptureState = "「とらえる」実行要求失敗";
            ReportAutoOutputDiagnostic(NextActionName, "UseActionがfalseを返しました", "use-action-false");
        }
        else if (!used)
        {
            ReportAutoOutputDiagnostic(NextActionName,
                $"スキルステータスコード {actionStatus}；技力 {gauge.Tp}/250、獣力 {gauge.BeastPower}/250",
                $"status-{actionStatus}");
        }
    }

    private void ResetCaptureState()
    {
        captureTargetId = 0;
        capturePendingUntilUtc = DateTime.MinValue;
        CaptureState = "未開始";
    }

    private unsafe bool TryUseEnabledSelfAction(
        ActionManager* actionManager,
        uint actionId,
        string actionName,
        DateTime now)
    {
        var actionStatus = actionManager->GetActionStatus(ActionType.Action, actionId, 0);
        if (actionStatus != 0)
        {
            ReportAutoOutputDiagnostic(actionName, $"スキルステータスコード {actionStatus}", $"status-{actionStatus}");
            return false;
        }

        StatusText = $"自動実行：{actionName}...";
        NextActionName = actionName;
        NextActionReason = "高度アクション準備完了";
        if (!actionManager->UseAction(ActionType.Action, actionId, 0))
        {
            ReportAutoOutputDiagnostic(actionName, "UseAction が false を返しました", "use-action-false");
            return false;
        }

        ReportAutoOutputSuccess(actionName, actionId);
        nextActionUtc = now.AddMilliseconds(700);
        return true;
    }

    private bool IsThirdFormEnabled
        => configuration.PhysicalThirdFormEnabled || configuration.MagicalThirdFormEnabled;

    private unsafe void EmitAutoOutputDiagnosticSummary(
        ActionManager* actionManager,
        IBattleChara player,
        IBattleChara target,
        BeastmasterGaugeSnapshot gauge,
        float targetHpPercent,
        bool canCapture,
        DateTime now)
    {
        if (!configuration.AutoOutputDiagnosticsEnabled)
        {
            return;
        }

        var items = new List<(string Name, string StateKey, string Detail)>();
        void Add(string name, bool available, string reason = "")
        {
            var detail = available ? "使用可能" : $"使用不可:{reason}";
            var stateKey = available ? "使用可能" : $"使用不可:{GetDiagnosticReasonKey(reason)}";
            items.Add((name, stateKey, detail));
        }

        if (configuration.BeastHeartCooperationEnabled || configuration.BeastSoulCooperationEnabled)
        {
            if (pendingCooperationActionId != 0)
            {
                var status = actionManager->GetActionStatus(ActionType.Action, pendingCooperationActionId, target.GameObjectId);
                Add("御獣連携", status == 0, status == 0 ? "" : $"コード {status}");
            }
            else if (TryGetCooperationAction(gauge, configuration.BeastHeartCooperationEnabled, out var cooperationId, out _, out _))
            {
                var status = actionManager->GetActionStatus(ActionType.Action, cooperationId, target.GameObjectId);
                Add("御獣連携", status == 0, status == 0 ? "" : gauge.Tp < BeastmasterGaugeSnapshot.ComboGaugeRequirement || gauge.BeastPower < BeastmasterGaugeSnapshot.ComboGaugeRequirement
                    ? $"リソース 技力 {gauge.Tp}/250 獣力 {gauge.BeastPower}/250"
                    : $"コード {status}");
            }
            else
            {
                Add("御獣連携", false, $"リソース 技力 {gauge.Tp}/250 獣力 {gauge.BeastPower}/250");
            }
        }

        if (configuration.AutoFinalStrikeEnabled
            && TryGetFinalStrikeSettings(gauge.WhistleIndex, out var finalEnabled, out var finalThreshold))
        {
            var finalStatus = actionManager->GetActionStatus(ActionType.Action, FinalStrikeActionId, target.GameObjectId);
            Add("最後の一撃", finalEnabled && gauge.SummonMaxHp > 0 && gauge.SummonHpPercent <= finalThreshold && finalStatus == 0,
                !finalEnabled ? "現在の獣笛設定が無効" : gauge.SummonMaxHp == 0 ? "魔獣なし" : gauge.SummonHpPercent > finalThreshold ? $"魔獣HP {gauge.SummonHpPercent:0.#}%/{finalThreshold:0.#}%" : finalStatus == 0 ? "" : $"コード {finalStatus}");
        }

        if (configuration.PhysicalThirdFormEnabled || configuration.MagicalThirdFormEnabled)
        {
            var thirdReady = gauge.BeastHeartStacks >= 3 && (gauge.HasWhiteStatus || gauge.HasPurpleStatus);
            Add("万象流転", thirdReady, thirdReady ? "" : $"リソース/バフ不足（獣心 {gauge.BeastHeartStacks} スタック）");
        }

        if (configuration.AutoReleaseEnabled
            && gauge.SummonEntry != null)
        {
            var releaseId = actionManager->GetAdjustedActionId(BeastmasterReleaseBaseActionId);
            if (releaseId == 0)
            {
                Add("はなつ", false, "ランタイムアクションなし");
            }
            else
            {
                var status = actionManager->GetActionStatus(ActionType.Action, releaseId, target.GameObjectId);
                var inRange = BeastmasterActionHelper.IsSummonInActionRange(
                    target,
                    gauge.SummonEntry.ReleaseActionId,
                    out var distance,
                    out var range);
                Add("はなつ", status == 0 && inRange,
                    status != 0 ? $"コード {status}" : inRange ? "" : $"魔獣距離 {distance:0.#}/{range:0.#}");
            }
        }

        if (configuration.AutoCaptureTryCapture)
        {
            Add("とらえる", canCapture, canCapture ? "" : $"対象HP {targetHpPercent:0.#}%/{configuration.CaptureHpThreshold:0.#}%");
        }

        if (configuration.BasicComboEnabled)
        {
            var comboId = actionManager->Combo.Timer > 0f && actionManager->Combo.Action == biteActionId && player.Level >= 12
                ? shieldActionId
                : actionManager->Combo.Timer > 0f && actionManager->Combo.Action == smashActionId && player.Level >= 2
                    ? biteActionId
                    : smashActionId;
            var comboStatus = actionManager->GetActionStatus(ActionType.Action, comboId, target.GameObjectId);
            var comboInRange = BeastmasterActionHelper.IsPlayerInActionRange(player, target, comboId, out var comboDistance, out var comboRange);
            Add("基本コンボ", comboStatus == 0 && comboInRange,
                comboStatus != 0 ? $"コード {comboStatus}" : comboInRange ? "" : $"射程 {comboDistance:0.#}/{comboRange:0.#}");
        }

        var summary = string.Join(" ", items.Select(item => $"{item.Name}[{item.Detail}]"));
        var summaryKey = string.Join(" ", items.Select(item => $"{item.Name}[{item.StateKey}]"));
        if (items.Count > 0 && summaryKey != lastAutoOutputSummary)
        {
            lastAutoOutputSummary = summaryKey;
            RecordBattleLog($"自動出力診断: {summary}");
        }
    }

    private static string GetDiagnosticReasonKey(string reason)
    {
        if (reason.StartsWith("コード", StringComparison.Ordinal)) return reason;
        if (reason.StartsWith("射程", StringComparison.Ordinal)
            || reason.StartsWith("魔獣距離", StringComparison.Ordinal)) return "射程";
        if (reason.StartsWith("リソース", StringComparison.Ordinal)) return "リソース不足";
        if (reason.StartsWith("魔獣HP", StringComparison.Ordinal)) return "魔獣HP";
        if (reason.StartsWith("リソース/バフ不足", StringComparison.Ordinal)) return "リソース/バフ不足";
        return reason;
    }

    private void ResetTargetScopedState()
    {
        activeTargetId = 0;
        lastAutoOutputSummary = string.Empty;
        lastSuccessfulActionUtc = DateTime.MinValue;
        nextActionUtc = DateTime.MinValue;
        nextReleaseAttemptUtc = DateTime.MinValue;
        nextFinalStrikeAttemptUtc = DateTime.MinValue;
        ResetCaptureState();
        ResetCooperationState();
    }

    private unsafe bool TryUseReleaseAction(
        ActionManager* actionManager,
        BeastmasterGaugeSnapshot gauge,
        IBattleChara target,
        DateTime now)
    {
        if (now < nextReleaseAttemptUtc)
        {
            return false;
        }

        var availability = BeastmasterActionHelper.GetAvailability(
            BeastmasterReleaseBaseActionId,
            target.GameObjectId,
            useAdjustedActionId: true);
        if (!availability.CanUse)
        {
            NextActionName = availability.ActionName;
            NextActionReason = availability.Reason;
            ReportAutoOutputDiagnostic(NextActionName,
                $"{availability.Reason}；技力 {gauge.Tp}/250、獣力 {gauge.BeastPower}/250",
                availability.Reason);
            nextReleaseAttemptUtc = now.AddMilliseconds(500);
            return false;
        }

        if (!BeastmasterActionHelper.IsSummonInActionRange(
                target,
                gauge.SummonEntry?.ReleaseActionId ?? availability.ActionId,
                out var summonDistance,
                out var actionRange))
        {
            NextActionName = availability.ActionName;
            NextActionReason = actionRange <= 0f
                ? "使役魔獣と「はなつ」の射程を識別中"
                : $"使役魔獣が「はなつ」の射程内に入るのを待機中（現在 {summonDistance:0.##}/{actionRange:0.##} ヤルム）";
            ReportAutoOutputDiagnostic(NextActionName,
                actionRange <= 0f ? NextActionReason : $"使役魔獣が射程外（現在 {summonDistance:0.##}/{actionRange:0.##} ヤルム）",
                "summon-range");
            nextReleaseAttemptUtc = now.AddMilliseconds(250);
            return false;
        }

        StatusText = "自動「はなつ」実行中...";
        NextActionName = availability.ActionName;
        NextActionReason = "「はなつ」リキャスト完了";
        if (!actionManager->UseAction(ActionType.Action, availability.ActionId, target.GameObjectId))
        {
            NextActionReason = "「はなつ」実行要求失敗";
            ReportAutoOutputDiagnostic(NextActionName, "UseActionがfalseを返しました", "use-action-false");
            nextReleaseAttemptUtc = now.AddSeconds(1);
            return false;
        }

        ReportAutoOutputSuccess(NextActionName, availability.ActionId);
        nextReleaseAttemptUtc = now.AddMilliseconds(500);
        nextActionUtc = now.AddMilliseconds(700);
        return true;
    }

    private unsafe bool TryUseFinalStrike(
        ActionManager* actionManager,
        BeastmasterGaugeSnapshot gauge,
        ulong targetId,
        DateTime now)
    {
        if (!configuration.AutoFinalStrikeEnabled)
        {
            return false;
        }

        if (!TryGetFinalStrikeSettings(gauge.WhistleIndex, out var enabled, out var hpThreshold))
        {
            ReportAutoOutputDiagnostic("最後の一撃", $"現在の獣笛 {gauge.WhistleIndex} に対応する1/2/3笛設定がありません", "whistle");
            return false;
        }

        if (!enabled)
        {
            ReportAutoOutputDiagnostic("最後の一撃", $"現在の第{gauge.WhistleIndex}笛の個別設定が無効です", "disabled");
            return false;
        }

        if (gauge.SummonDataId == 0 || gauge.SummonMaxHp == 0)
        {
            ReportAutoOutputDiagnostic("最後の一撃", "有効な使役魔獣または魔獣HPが認識されていません", "summon");
            return false;
        }

        if (gauge.SummonHpPercent > hpThreshold)
        {
            ReportAutoOutputDiagnostic(
                "最後の一撃",
                $"魔獣HP {gauge.SummonHpPercent:0.#}% が閾値 {hpThreshold:0.#}% より上です",
                "hp");
            return false;
        }

        if (now < nextFinalStrikeAttemptUtc)
        {
            return false;
        }

        NextActionName = GetActionName(FinalStrikeActionId);
        if (DalamudApi.ObjectTable.LocalPlayer is { } player
            && DalamudApi.TargetManager.Target is IBattleChara target
            && !BeastmasterActionHelper.IsPlayerInActionRange(
                player,
                target,
                FinalStrikeActionId,
                out var playerDistance,
                out var actionRange))
        {
            NextActionReason = $"スキル射程内への移動待機中（現在 {playerDistance:0.##}/{actionRange:0.##} ヤルム）";
            ReportAutoOutputDiagnostic(NextActionName,
                $"射程外（現在 {playerDistance:0.##}/{actionRange:0.##} ヤルム）",
                "range");
            nextFinalStrikeAttemptUtc = now.AddMilliseconds(250);
            return false;
        }

        if (configuration.AutoFinalStrikeWaitForRelease)
        {
            var releaseActionId = actionManager->GetAdjustedActionId(BeastmasterReleaseBaseActionId);
            var releaseReady = releaseActionId != 0
                && actionManager->GetActionStatus(ActionType.Action, releaseActionId, targetId) == 0;
            if (releaseReady)
            {
                var releaseInRange = DalamudApi.TargetManager.Target is IBattleChara releaseTarget
                    && gauge.SummonEntry is { } summonEntry
                    && BeastmasterActionHelper.IsSummonInActionRange(
                        releaseTarget,
                        summonEntry.ReleaseActionId,
                        out _,
                        out _);
                NextActionReason = releaseInRange
                    ? "現在の魔獣が先に「はなつ」を実行するのを待機中"
                    : "「はなつ」は使用可能ですが使役魔獣が射程外のため、「はなつ」実行完了まで「最後の一撃」を待機します";
                ReportAutoOutputDiagnostic(NextActionName, NextActionReason, "wait-release");
                return false;
            }

            ReportAutoOutputDiagnostic(
                NextActionName,
                $"「はなつ」は使用不可のため「最後の一撃」の確認を続行（はなつ ActionId {releaseActionId}、コード {actionManager->GetActionStatus(ActionType.Action, releaseActionId, targetId)}）",
                "release-complete");
        }

        var actionStatus = actionManager->GetActionStatus(ActionType.Action, FinalStrikeActionId, targetId);
        if (actionStatus != 0)
        {
            NextActionReason = $"スキル現在使用不可（コード {actionStatus}）";
            ReportAutoOutputDiagnostic(NextActionName,
                $"スキルステータスコード {actionStatus}；技力 {gauge.Tp}/250、獣力 {gauge.BeastPower}/250",
                $"status-{actionStatus}");
            nextFinalStrikeAttemptUtc = now.AddMilliseconds(250);
            return false;
        }

        StatusText = "自動「最後の一撃」実行中...";
        NextActionReason = $"第{gauge.WhistleIndex}笛 魔獣HP {gauge.SummonHpPercent:0.#}% が閾値 {hpThreshold:0.#}% 以下に到達";
        if (!actionManager->UseAction(ActionType.Action, FinalStrikeActionId, targetId))
        {
            ReportAutoOutputDiagnostic(NextActionName, "UseActionがfalseを返しました", "use-action-false");
            nextFinalStrikeAttemptUtc = now.AddMilliseconds(500);
            return false;
        }

        ReportAutoOutputSuccess(NextActionName, FinalStrikeActionId);
        nextFinalStrikeAttemptUtc = now.AddMilliseconds(700);
        nextActionUtc = now.AddMilliseconds(700);
        return true;
    }

    private bool TryGetFinalStrikeSettings(byte whistleIndex, out bool enabled, out float hpThreshold)
    {
        (enabled, hpThreshold) = whistleIndex switch
        {
            1 => (configuration.AutoFinalStrikeWhistleOneEnabled, configuration.AutoFinalStrikeWhistleOneHpThreshold),
            2 => (configuration.AutoFinalStrikeWhistleTwoEnabled, configuration.AutoFinalStrikeWhistleTwoHpThreshold),
            3 => (configuration.AutoFinalStrikeWhistleThreeEnabled, configuration.AutoFinalStrikeWhistleThreeHpThreshold),
            _ => (false, 0f),
        };
        return whistleIndex is >= 1 and <= 3;
    }

    private unsafe bool TryUseThirdFormAction(
        ActionManager* actionManager,
        BeastmasterGaugeSnapshot gauge,
        ulong targetId,
        DateTime now)
    {
        uint actionId;
        ulong actionTargetId;
        string reason;
        if (gauge.BeastHeartStacks >= 3 && (gauge.HasWhiteStatus || gauge.HasPurpleStatus))
        {
            actionId = DrumActionId;
            actionTargetId = 0;
            reason = $"ビーストハート {gauge.BeastHeartStacks} スタック、三式へ移行";
        }
        else if (gauge.HasWhiteStatus || gauge.HasPurpleStatus)
        {
            actionId = (gauge.HasWhiteStatus, configuration.PhysicalThirdFormEnabled) switch
            {
                (true, true) => WhitePhysicalThirdFormActionId,
                (true, false) => WhiteMagicalThirdFormActionId,
                (false, true) => PurplePhysicalThirdFormActionId,
                (false, false) => PurpleMagicalThirdFormActionId,
            };
            actionTargetId = targetId;
            reason = $"{(gauge.HasWhiteStatus ? "白（生息）" : "黒/紫（死滅）")} + {(configuration.PhysicalThirdFormEnabled ? "万象流転（物理）" : "万象流転（魔法）")}";
        }
        else
        {
            return false;
        }

        StatusText = "万象流転（三式）実行中...";
        NextActionName = GetActionName(actionId);
        if (actionTargetId != 0
            && DalamudApi.ObjectTable.LocalPlayer is { } player
            && DalamudApi.TargetManager.Target is IBattleChara target
            && !BeastmasterActionHelper.IsPlayerInActionRange(
                player,
                target,
                actionId,
                out var playerDistance,
                out var actionRange))
        {
            NextActionReason = $"スキル射程内への移動待機中（現在 {playerDistance:0.##}/{actionRange:0.##} ヤルム）";
            ReportAutoOutputDiagnostic(NextActionName, $"射程外（現在 {playerDistance:0.##}/{actionRange:0.##} ヤルム）", "range");
            return false;
        }
        var actionStatus = actionManager->GetActionStatus(ActionType.Action, actionId, actionTargetId);
        NextActionReason = actionStatus == 0 ? reason : $"{reason}、スキル現在使用不可（コード {actionStatus}）";
        if (actionStatus != 0 || !actionManager->UseAction(ActionType.Action, actionId, actionTargetId))
        {
            ReportAutoOutputDiagnostic(NextActionName,
                $"スキルステータスコード {actionStatus}；技力 {gauge.Tp}/250、獣力 {gauge.BeastPower}/250",
                $"status-{actionStatus}");
            return false;
        }

        ReportAutoOutputSuccess(NextActionName, actionId);
        nextActionUtc = now.AddMilliseconds(700);
        return true;
    }

    private unsafe bool TryUseAutoWhistle(
        ActionManager* actionManager,
        BeastmasterGaugeSnapshot gauge,
        DateTime now)
    {
        var hasSummon = gauge.SummonEntry != null || gauge.WhistleIndex is >= 1 and <= 3;
        if (hasSummon)
        {
            ResetAutoWhistle();
            return false;
        }

        if (pendingWhistleActionId != 0 && now < pendingWhistleUntilUtc)
        {
            StatusText = "魔獣召喚待ち...";
            NextActionName = GetActionName(pendingWhistleActionId);
            NextActionReason = "獣笛使用要求送信済み、召喚確認待ち";
            return true;
        }

        if (pendingWhistleActionId != 0)
        {
            pendingWhistleActionId = 0;
            pendingWhistleUntilUtc = DateTime.MinValue;
        }

        if (now < nextWhistleAttemptUtc)
        {
            return false;
        }

        foreach (var actionId in new[] { WhistleOneActionId, WhistleTwoActionId, WhistleThreeActionId })
        {
            var actionStatus = actionManager->GetActionStatus(ActionType.Action, actionId, 0);
            if (actionStatus != 0)
            {
                continue;
            }

            StatusText = "魔獣自動召喚中...";
            NextActionName = GetActionName(actionId);
            NextActionReason = "魔獣未召喚のため、一号→二号→三号の順に使用可能な呼笛を選択";
            if (!actionManager->UseAction(ActionType.Action, actionId, 0))
            {
                nextWhistleAttemptUtc = now.AddMilliseconds(250);
                return false;
            }

            pendingWhistleActionId = actionId;
            pendingWhistleUntilUtc = now.AddSeconds(1);
            return true;
        }

        nextWhistleAttemptUtc = now.AddMilliseconds(500);
        return false;
    }

    private void ResetAutoWhistle()
    {
        pendingWhistleActionId = 0;
        pendingWhistleUntilUtc = DateTime.MinValue;
        nextWhistleAttemptUtc = DateTime.MinValue;
    }

    private unsafe bool TryRunWhistleRotation(ActionManager* actionManager, IBattleChara? target, DateTime now)
    {
        if (whistleRotationWaitingForCooldown)
        {
            var cooldownStatus = actionManager->GetActionStatus(ActionType.Action, WhistleOneActionId, 0);
            if (cooldownStatus != 0)
            {
                WhistleRotationStatus = $"完了、一号呼笛のリキャスト待機中（状態コード {cooldownStatus}）";
                NextActionName = GetActionName(WhistleOneActionId);
                NextActionReason = "一号呼笛のリキャスト完了後に再実行可能";
                if (configuration.WhistleRotationEnabled)
                {
                    configuration.WhistleRotationEnabled = false;
                    configuration.Save();
                }

                return true;
            }

            whistleRotationWaitingForCooldown = false;
            WhistleRotationStatus = "一号呼笛の準備完了、ローテーション開始可能";
        }

        if (whistleRotationStage < 0)
        {
            if (!configuration.WhistleRotationEnabled)
            {
                WhistleRotationStatus = "無効";
                return false;
            }

            if (DalamudApi.Condition[ConditionFlag.InCombat])
            {
                WhistleRotationStatus = "非戦闘状態への移行待ち";
                NextActionName = GetActionName(WhistleOneActionId);
                NextActionReason = "一号呼笛は非戦闘時のみ開始可能";
                return true;
            }

            var whistleStatus = actionManager->GetActionStatus(ActionType.Action, WhistleOneActionId, 0);
            if (whistleStatus != 0)
            {
                WhistleRotationStatus = $"一号呼笛の準備待ち（状態コード {whistleStatus}）";
                NextActionName = GetActionName(WhistleOneActionId);
                NextActionReason = "ローテーション開始には一号呼笛が使用可能である必要があります";
                return true;
            }

            whistleRotationStage = 0;
            WhistleRotationStatus = "ローテーション実行中";
        }

        if (now < whistleRotationNextActionUtc)
        {
            return true;
        }

        var actionId = whistleRotationStage switch
        {
            0 => WhistleOneActionId,
            1 => BeastmasterReleaseBaseActionId,
            2 => FinalStrikeActionId,
            3 => WhistleTwoActionId,
            4 => BeastmasterReleaseBaseActionId,
            5 => FinalStrikeActionId,
            6 => WhistleThreeActionId,
            7 => BeastmasterReleaseBaseActionId,
            _ => 0u,
        };
        var requiresTarget = actionId is not (WhistleOneActionId or WhistleTwoActionId or WhistleThreeActionId);
        if (requiresTarget && target is null)
        {
            WhistleRotationStatus = "有効ターゲット待機中";
            NextActionName = GetActionName(actionId);
            NextActionReason = "「はなつ」および「最後の一撃」にはターゲットが必要です";
            return true;
        }

        var targetId = actionId is WhistleOneActionId or WhistleTwoActionId or WhistleThreeActionId
            ? 0UL
            : target!.GameObjectId;
        var adjustedActionId = actionId == BeastmasterReleaseBaseActionId
            ? actionManager->GetAdjustedActionId(BeastmasterReleaseBaseActionId)
            : actionId;
        if (adjustedActionId == 0)
        {
            WhistleRotationStatus = "魔獣アクションの実行時ID取得待ち";
            NextActionName = GetActionName(BeastmasterReleaseBaseActionId);
            NextActionReason = "現在の魔獣の「はなつ」アクションIDを取得できません";
            return true;
        }

        var actionStatus = actionManager->GetActionStatus(ActionType.Action, adjustedActionId, targetId);
        NextActionName = GetActionName(adjustedActionId);
        NextActionReason = actionStatus == 0 ? "呼笛ローテーション" : $"アクションが現在使用不可（状態コード {actionStatus}）";
        if (actionStatus != 0 || !actionManager->UseAction(ActionType.Action, adjustedActionId, targetId))
        {
            WhistleRotationStatus = $"待機中：{GetActionName(adjustedActionId)}";
            whistleRotationNextActionUtc = now.AddMilliseconds(250);
            return true;
        }

        StatusText = "呼笛ローテーション実行中...";
        whistleRotationNextActionUtc = now.AddMilliseconds(actionId == BeastmasterReleaseBaseActionId ? 700 : 350);
        if (whistleRotationStage == 7)
        {
            configuration.WhistleRotationEnabled = false;
            configuration.Save();
            whistleRotationStage = -1;
            whistleRotationWaitingForCooldown = true;
            WhistleRotationStatus = "ローテーション完了、一号呼笛のリキャスト待機中";
            return true;
        }

        whistleRotationStage++;
        return true;
    }

    private void ResetWhistleRotation(string reason)
    {
        whistleRotationStage = -1;
        whistleRotationWaitingForCooldown = false;
        whistleRotationNextActionUtc = DateTime.MinValue;
        WhistleRotationStatus = reason;
    }

    private void ResetCooperationState()
    {
        pendingCooperationActionId = 0;
        pendingCooperationStatusId = 0;
        pendingCooperationUntilUtc = DateTime.MinValue;
    }


    private static bool HasSelfStatus(uint statusId)
    {
        var player = DalamudApi.ObjectTable.LocalPlayer;
        return player != null && player.StatusList.Any(status => status.StatusId == statusId);
    }

    private static string GetAttributeStatusName(uint statusId)
        => statusId switch
        {
            4595 => "獣心一式・翔",
            4596 => "獣心一式・猛",
            4597 => "獣心一式・堅",
            4598 => "獣心一式・魔",
            _ => "対応する獣心一式ステータス",
        };

    private static bool TryGetCooperationAction(
        BeastmasterGaugeSnapshot gauge,
        bool ultimateFirst,
        out uint firstActionId,
        out uint secondActionId,
        out uint requiredStatusId)
    {
        firstActionId = 0;
        secondActionId = 0;
        requiredStatusId = 0;
        var entry = gauge.SummonEntry;
        if (entry == null
            || gauge.Tp < BeastmasterGaugeSnapshot.ComboGaugeRequirement
            || gauge.BeastPower < BeastmasterGaugeSnapshot.ComboGaugeRequirement)
        {
            return false;
        }

        var nextAttribute = entry.Attribute switch
        {
            BeastmasterAttribute.魔 => BeastmasterAttribute.翔,
            BeastmasterAttribute.翔 => BeastmasterAttribute.猛,
            BeastmasterAttribute.猛 => BeastmasterAttribute.坚,
            BeastmasterAttribute.坚 => BeastmasterAttribute.魔,
            _ => BeastmasterAttribute.Unknown,
        };
        var nextAxeActionId = nextAttribute switch
        {
            BeastmasterAttribute.猛 => 44884u,
            BeastmasterAttribute.坚 => 44887u,
            BeastmasterAttribute.魔 => 44888u,
            BeastmasterAttribute.翔 => 44889u,
            _ => 0u,
        };
        var attributeStatusId = entry.Attribute switch
        {
            BeastmasterAttribute.猛 => 4596u,
            BeastmasterAttribute.坚 => 4597u,
            BeastmasterAttribute.魔 => 4598u,
            BeastmasterAttribute.翔 => 4595u,
            _ => 0u,
        };
        var nextAttributeStatusId = nextAttribute switch
        {
            BeastmasterAttribute.猛 => 4596u,
            BeastmasterAttribute.坚 => 4597u,
            BeastmasterAttribute.魔 => 4598u,
            BeastmasterAttribute.翔 => 4595u,
            _ => 0u,
        };

        if (ultimateFirst)
        {
            firstActionId = BeastmasterUltimateActionId;
            secondActionId = nextAxeActionId;
            requiredStatusId = attributeStatusId;
            return firstActionId != 0 && secondActionId != 0;
        }

        firstActionId = nextAxeActionId;
        secondActionId = BeastmasterUltimateActionId;
        requiredStatusId = nextAttributeStatusId;
        return firstActionId != 0 && secondActionId != 0;
    }

    private string GetAdvancedActionStatus(BeastmasterGaugeSnapshot gauge)
    {
        var entry = gauge.SummonEntry;
        if (entry == null)
        {
            return "魔獣召喚待ち";
        }

        if (configuration.BeastHeartCooperationEnabled || configuration.BeastSoulCooperationEnabled)
        {
            return $"{(configuration.BeastHeartCooperationEnabled ? "ビーストハート連携（黄）" : "ビーストソウル連携（青）")}有効";
        }

        if (configuration.PhysicalThirdFormEnabled || configuration.MagicalThirdFormEnabled)
        {
            return $"{(configuration.PhysicalThirdFormEnabled ? "万象流転（物理）" : "万象流転（魔法）")}有効";
        }

        var anyFinalStrikeEnabled = configuration.AutoFinalStrikeWhistleOneEnabled
            || configuration.AutoFinalStrikeWhistleTwoEnabled
            || configuration.AutoFinalStrikeWhistleThreeEnabled;
        if (configuration.AutoWhistleEnabled || anyFinalStrikeEnabled || configuration.AutoReleaseEnabled)
        {
            return $"自動呼笛:{(configuration.AutoWhistleEnabled ? "有効" : "無効")}, 最後の一撃:{(anyFinalStrikeEnabled ? "有効" : "無効")}, はなつ:{(configuration.AutoReleaseEnabled ? "有効" : "無効")}";
        }

        return "高度スキルはすべて無効";
    }

    private static string GetActionName(uint actionId)
        => DalamudApi.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>()
            .TryGetRow(actionId, out var action)
                ? action.Name.ExtractText()
                : $"アクション {actionId}";
}
