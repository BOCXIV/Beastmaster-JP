using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Beastmaster;

public enum BeastmasterSequenceState
{
    Idle,
    Countdown,
    WaitingForCombat,
    Combat,
}

public sealed class BeastmasterSequenceService
{
    private const uint SmashActionId = 44879;
    private const uint BiteActionId = 44883;
    private const uint ShieldActionId = 44885;
    private const uint WhistleOneActionId = 44881;
    private const uint BeastSkillActionId = 44886;
    private const uint ReleaseActionId = 44890;
    private const uint FinalStrikeActionId = 44891;
    private const uint WhistleTwoActionId = 44892;
    private const uint ShieldChargeActionId = 44893;
    private const uint WhistleThreeActionId = 44894;
    private const uint BorrowActionId = 44895;
    private const uint DrumActionId = 44905;

    private readonly BeastmasterConfiguration configuration;
    private readonly BeastmasterCountdownService countdownService;
    private int countdownStep;
    private int combatStep;
    private byte pendingWhistle;
    private uint pendingBorrowedAction;
    private int pendingBorrowedActionStep = -1;
    private DateTime nextAttemptUtc = DateTime.MinValue;
    private DateTime combatDeadlineUtc = DateTime.MinValue;
    private DateTime stepDeadlineUtc = DateTime.MinValue;
    private DateTime finalCountdownAttemptDeadlineUtc = DateTime.MinValue;
    private DateTime lastHandledStartUtc = DateTime.MinValue;
    private DateTime lastChatUtc = DateTime.MinValue;
    private string lastChatMessage = string.Empty;
    private string combatStepFailure = "未試行";
    private int combatStepAttempts;

    public BeastmasterSequenceService(
        BeastmasterConfiguration configuration,
        BeastmasterCountdownService countdownService)
    {
        this.configuration = configuration;
        this.countdownService = countdownService;
        lastHandledStartUtc = countdownService.LastTransitionUtc;
    }

    public bool Enabled => configuration.WaterOpenerSequenceEnabled;

    public bool ChatMessagesEnabled => configuration.SequenceChatMessagesEnabled;
    public string CurrentSequenceName
        => configuration.Sequences.Count == 0
            ? "利用可能なシーケンスなし"
            : configuration.Sequences[Math.Clamp(configuration.SelectedSequenceIndex, 0, configuration.Sequences.Count - 1)].Name;

    private BeastmasterSequenceDefinition? CurrentSequence
        => configuration.Sequences.Count == 0
            ? null
            : configuration.Sequences[Math.Clamp(configuration.SelectedSequenceIndex, 0, configuration.Sequences.Count - 1)];
    public BeastmasterSequenceState State { get; private set; }
    public bool IsControlling => State != BeastmasterSequenceState.Idle;
    public string Status { get; private set; } = "無効";

    public void SetEnabled(bool enabled)
    {
        configuration.WaterOpenerSequenceEnabled = enabled;
        configuration.Save();
        if (!enabled)
        {
            Abort("停止");
        }
        else if (CurrentSequence is not { } sequence)
        {
            configuration.WaterOpenerSequenceEnabled = false;
            configuration.Save();
            Status = "有効化不可：利用可能なシーケンスがありません";
        }
        else if (!sequence.TryValidate(out var error))
        {
            configuration.WaterOpenerSequenceEnabled = false;
            configuration.Save();
            Status = $"有効化不可：{error}";
        }
        else
        {
            lastHandledStartUtc = countdownService.LastTransitionUtc;
            Status = "次回のチームカウントダウン待機中";
        }
    }

    public void SetChatMessagesEnabled(bool enabled)
    {
        configuration.SequenceChatMessagesEnabled = enabled;
        configuration.Save();
    }

    private void PrintChat(string message)
    {
        if (!configuration.SequenceChatMessagesEnabled)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (message == lastChatMessage && now - lastChatUtc < TimeSpan.FromSeconds(2))
        {
            return;
        }

        lastChatMessage = message;
        lastChatUtc = now;
        DalamudApi.ChatGui.Print($"[Beastmaster シーケンス診断 {DateTime.Now:HH:mm:ss}] {message}");
    }

    public void Abort(string reason)
    {
        State = BeastmasterSequenceState.Idle;
        countdownStep = 0;
        combatStep = 0;
        pendingWhistle = 0;
        pendingBorrowedAction = 0;
        pendingBorrowedActionStep = -1;
        nextAttemptUtc = DateTime.MinValue;
        combatDeadlineUtc = DateTime.MinValue;
        stepDeadlineUtc = DateTime.MinValue;
        finalCountdownAttemptDeadlineUtc = DateTime.MinValue;
        combatStepFailure = "未試行";
        combatStepAttempts = 0;
        Status = reason;
        PrintChat(reason);
    }

    public unsafe bool TryHandle(
        ActionManager* actionManager,
        BeastmasterGaugeSnapshot gauge,
        IBattleChara? target,
        DateTime now)
    {
        if (!Enabled)
        {
            return false;
        }

        var countdown = countdownService.Snapshot;
        var sequence = CurrentSequence;
        if (sequence == null)
        {
            Status = "シーケンス無効：利用可能なシーケンスがありません";
            return false;
        }
        if (!sequence.TryValidate(out var validationError))
        {
            Status = $"シーケンス無効：{validationError}";
            return false;
        }
        if (State == BeastmasterSequenceState.Idle)
        {
            if (countdownService.LastTransition != BeastmasterCountdownTransition.Started
                || countdownService.LastTransitionUtc <= lastHandledStartUtc
                || !countdown.Active)
            {
                Status = "次回のチームカウントダウン待機中";
                return false;
            }

            lastHandledStartUtc = countdownService.LastTransitionUtc;
            State = BeastmasterSequenceState.Countdown;
            countdownStep = 0;
            Status = $"カウントダウン：T-{Math.Abs(sequence.CountdownSteps[0].TimeSeconds ?? 0f):0.###}「{sequence.CountdownSteps[0].Label}」待機中";
            PrintChat($"シーケンス「{CurrentSequenceName}」開始、待機中：「{sequence.CountdownSteps[0].Label}」");
        }

        if (State == BeastmasterSequenceState.Countdown)
        {
            if (!DalamudApi.Condition[ConditionFlag.InCombat]
                && countdownService.LastTransition == BeastmasterCountdownTransition.Cancelled
                && countdownService.LastTransitionUtc >= lastHandledStartUtc)
            {
                Abort("シーケンス中止：カウントダウンがキャンセルされました");
                return true;
            }

            return RunCountdown(actionManager, gauge, target, countdown, sequence, now);
        }

        if (State == BeastmasterSequenceState.WaitingForCombat)
        {
            if (DalamudApi.Condition[ConditionFlag.InCombat])
            {
                State = BeastmasterSequenceState.Combat;
                combatStep = 0;
                nextAttemptUtc = DateTime.MinValue;
                stepDeadlineUtc = now.AddSeconds(8);
                ResetCombatStepFailure();
                Status = "戦闘シーケンス開始";
                PrintChat("戦闘開始、戦闘シーケンスを実行します");
                return true;
            }

            if (now >= combatDeadlineUtc)
            {
                Abort("シーケンス中止：突入アクション後に戦闘状態へ移行しませんでした");
            }
            return true;
        }

        return RunCombat(actionManager, gauge, target, sequence, now);
    }

    private unsafe bool RunCountdown(
        ActionManager* actionManager,
        BeastmasterGaugeSnapshot gauge,
        IBattleChara? target,
        BeastmasterCountdownSnapshot countdown,
        BeastmasterSequenceDefinition sequence,
        DateTime now)
    {
        if (DalamudApi.Condition[ConditionFlag.InCombat])
        {
            pendingWhistle = 0;
            pendingBorrowedAction = 0;
            pendingBorrowedActionStep = -1;
            State = BeastmasterSequenceState.Combat;
            combatStep = 0;
            nextAttemptUtc = now;
            stepDeadlineUtc = now.AddSeconds(8);
            ResetCombatStepFailure();
            Status = "戦闘突入、戦闘シーケンスへ移行";
            PrintChat("戦闘開始、戦闘シーケンスを実行します");
            return true;
        }

        var currentStep = countdownStep < sequence.CountdownSteps.Count
            ? sequence.CountdownSteps[countdownStep]
            : null;
        var isFinalZeroStep = currentStep is not null
            && Math.Abs(currentStep.TimeSeconds ?? 0f) <= 0.001f;
        var isFinalStep = countdownStep >= sequence.CountdownSteps.Count - 1;
        if (!countdown.Active
            && !isFinalStep)
        {
            Abort(countdownService.LastTransition == BeastmasterCountdownTransition.Cancelled
                ? "シーケンス中止：カウントダウンがキャンセルされました"
                : "シーケンス中止：カウントダウンが中断されました");
            return true;
        }

        if (!countdown.Active && isFinalStep)
        {
            Status = "戦闘未突入（カウントダウン終了）";
        }

        if (pendingWhistle != 0)
        {
            if (gauge.WhistleIndex == pendingWhistle)
            {
                pendingWhistle = 0;
                countdownStep++;
            }
            else if (MissedCountdownDeadline(countdown.TimeRemaining, sequence, countdownStep))
            {
                Abort($"シーケンス中止：{pendingWhistle}号呼び笛が確認できませんでした");
            }
            return true;
        }

        if (pendingBorrowedAction != 0)
        {
            if (actionManager->GetAdjustedActionId(BeastSkillActionId) == pendingBorrowedAction)
            {
                var actionName = GetActionName(pendingBorrowedAction);
                pendingBorrowedAction = 0;
                pendingBorrowedActionStep = -1;
                countdownStep++;
                Status = $"かりるアクション確認完了：「{actionName}」";
                PrintChat($"かりるアクション確認完了：「{actionName}」");
            }
            else if (pendingBorrowedActionStep >= 0
                     && countdown.TimeRemaining <= Math.Abs(sequence.CountdownSteps[pendingBorrowedActionStep].TimeSeconds ?? 0f))
            {
                Abort($"シーケンス中止：かりる後の「{GetActionName(pendingBorrowedAction)}」が準備できませんでした");
            }
            return true;
        }

        if (countdownStep >= sequence.CountdownSteps.Count)
        {
            Abort("シーケンス中止：カウントダウンの全ステップが終了しました");
            return true;
        }

        var step = sequence.CountdownSteps[countdownStep];
        if (TryGetWhistleIndex(step.ActionId, out var requiredWhistle)
            && gauge.WhistleIndex == requiredWhistle)
        {
            countdownStep++;
            Status = $"現在{requiredWhistle}号呼び笛を確認、次のステップへ";
            PrintChat($"現在{requiredWhistle}号呼び笛を確認、重複リクエストをスキップ");
            return true;
        }

        var trigger = Math.Abs(step.TimeSeconds ?? 0f);
        if (isFinalZeroStep)
        {
            trigger = 0.1f;
        }
        if (countdown.TimeRemaining > trigger)
        {
            Status = $"カウントダウン：T-{trigger:0.###}「{step.Label}」待機中";
            return true;
        }

        if (now < nextAttemptUtc)
        {
            return true;
        }

        if (isFinalStep && finalCountdownAttemptDeadlineUtc == DateTime.MinValue)
        {
            finalCountdownAttemptDeadlineUtc = now.AddSeconds(2);
        }

        var baseActionId = step.ActionId;
        var adjustedActionId = baseActionId is BorrowActionId or BeastSkillActionId
            ? actionManager->GetAdjustedActionId(baseActionId)
            : baseActionId;
        if (adjustedActionId != 0
            && BeastmasterActionHelper.TryGetActionLevel(adjustedActionId, out var requiredLevel)
            && DalamudApi.ObjectTable.LocalPlayer is { } player
            && player.Level < requiredLevel)
        {
            PrintChat($"カウントダウン手順をスキップ: {step.Label}（Lv.{player.Level} / 要求Lv.{requiredLevel}）");
            countdownStep++;
            if (countdownStep >= sequence.CountdownSteps.Count)
            {
                State = BeastmasterSequenceState.WaitingForCombat;
                combatDeadlineUtc = now.AddSeconds(2);
                Status = "戦闘開始待機中";
            }
            else
            {
                Status = "カウントダウン: 次の手順待機中";
            }
            return true;
        }

        var requiresTarget = IsTargetAction(baseActionId);
        if (requiresTarget && !IsValidTarget(target))
        {
            Abort("シーケンス中止：T-0に有効な敵対ターゲットが存在しません");
            return true;
        }

        var targetId = requiresTarget ? target!.GameObjectId : 0UL;
        if (BeastmasterFinalStrikeLock.IsBlocked(baseActionId, now))
        {
            Status = $"アクション待機中：「{step.Label}」（{BeastmasterFinalStrikeLock.GetBlockReason(baseActionId, now)}）";
            nextAttemptUtc = now.AddMilliseconds(100);
            return true;
        }

        if (adjustedActionId == 0
            || actionManager->GetActionStatus(ActionType.Action, adjustedActionId, targetId) != 0
            || !actionManager->UseAction(ActionType.Action, adjustedActionId, targetId))
        {
            Status = $"アクション使用可能待機中：「{step.Label}」";
            nextAttemptUtc = now.AddMilliseconds(100);
            if ((isFinalZeroStep && now >= finalCountdownAttemptDeadlineUtc)
                || (!isFinalZeroStep && MissedCountdownDeadline(countdown.TimeRemaining, sequence, countdownStep)))
            {
                Abort($"シーケンス中止：「{GetActionName(adjustedActionId)}」を実行猶予時間内に使用できませんでした");
            }
            return true;
        }

        nextAttemptUtc = now.AddMilliseconds(250);
        if (baseActionId == ReleaseActionId)
        {
            BeastmasterFinalStrikeLock.RecordRelease(now);
        }
        else if (baseActionId == FinalStrikeActionId)
        {
            BeastmasterFinalStrikeLock.RecordFinalStrike(now);
        }
        if (baseActionId is WhistleOneActionId or WhistleTwoActionId or WhistleThreeActionId)
        {
            pendingWhistle = baseActionId == WhistleOneActionId ? (byte)1 : baseActionId == WhistleTwoActionId ? (byte)2 : (byte)3;
            Status = $"カウントダウン：{pendingWhistle}号呼び笛の確認待機中";
            PrintChat($"カウントダウンアクションをリクエスト：「{step.Label}」、{pendingWhistle}号呼び笛の確認待機中");
        }
        else if (baseActionId == BorrowActionId)
        {
            pendingBorrowedActionStep = FindBorrowedActionStep(sequence, countdownStep + 1);
            if (pendingBorrowedActionStep >= 0)
            {
                pendingBorrowedAction = sequence.CountdownSteps[pendingBorrowedActionStep].ActionId;
                var actionName = GetActionName(pendingBorrowedAction);
                Status = $"カウントダウン：「{actionName}」の準備待機中";
                PrintChat($"「かりる」をリクエスト、「{actionName}」の準備待機中");
            }
            else
            {
                PrintChat("カウントダウンアクションをリクエスト：かりる");
                countdownStep++;
                Status = "カウントダウン：次のステップ待機中";
            }
        }
        else
        {
            PrintChat($"カウントダウンアクションをリクエスト：「{step.Label}」");
            countdownStep++;
            if (countdownStep >= sequence.CountdownSteps.Count)
            {
                State = BeastmasterSequenceState.WaitingForCombat;
                combatDeadlineUtc = now.AddSeconds(2);
                Status = "戦闘開始待機中";
            }
            else
            {
                Status = "カウントダウン：次のステップ待機中";
            }
        }
        return true;
    }

    private unsafe bool RunCombat(
        ActionManager* actionManager,
        BeastmasterGaugeSnapshot gauge,
        IBattleChara? target,
        BeastmasterSequenceDefinition sequence,
        DateTime now)
    {
        if (!DalamudApi.Condition[ConditionFlag.InCombat])
        {
            Abort("シーケンス中止：非戦闘状態になりました");
            return true;
        }

        if (combatStep >= sequence.CombatSteps.Count)
        {
            Status = "シーケンス完了、次回のチームカウントダウン待機中";
            PrintChat("スキルシーケンスが完了しました");
            State = BeastmasterSequenceState.Idle;
            return true;
        }

        var step = sequence.CombatSteps[combatStep];
        if (now >= stepDeadlineUtc)
        {
            Abort($"シーケンス中断: ステップ {combatStep + 1}/{sequence.CombatSteps.Count}「{step.Label}」タイムアウト: {combatStepFailure}（再試行 {combatStepAttempts} 回）");
            return true;
        }

        if (pendingWhistle != 0)
        {
            if (gauge.WhistleIndex == pendingWhistle)
            {
                pendingWhistle = 0;
                combatStep++;
                stepDeadlineUtc = now.AddSeconds(8);
                ResetCombatStepFailure();
            }
            else
            {
                combatStepFailure = $"{pendingWhistle}号呼び笛の確認待機中（現在: {gauge.WhistleIndex}号呼び笛）";
            }
            return true;
        }

        if (now < nextAttemptUtc)
        {
            return true;
        }

        var baseActionId = step.ActionId;
        if (TryGetWhistleIndex(baseActionId, out var requiredWhistle)
            && gauge.WhistleIndex == requiredWhistle)
        {
            combatStep++;
            stepDeadlineUtc = now.AddSeconds(8);
            ResetCombatStepFailure();
            Status = $"現在の呼び笛が{requiredWhistle}号であることを確認済み、次の手順へ移行";
            PrintChat($"現在の呼び笛が{requiredWhistle}号であることを確認したため重複リクエストをスキップ");
            return true;
        }

        var adjustedActionId = baseActionId is ReleaseActionId or BeastSkillActionId or BorrowActionId
            ? actionManager->GetAdjustedActionId(baseActionId)
            : baseActionId;
        var requiresTarget = baseActionId is SmashActionId or ReleaseActionId or FinalStrikeActionId;
        if (requiresTarget && !IsValidTarget(target))
        {
            Abort($"シーケンス中止：「{GetActionName(adjustedActionId)}」の対象ターゲットが存在しません");
            return true;
        }

        var targetId = requiresTarget ? target!.GameObjectId : 0UL;
        Status = $"戦闘シーケンス {combatStep + 1}/{sequence.CombatSteps.Count}: {step.Label}";
        combatStepAttempts++;
        if (adjustedActionId == 0)
        {
            combatStepFailure = $"調整後のアクションIDが 0 です（基本 ActionId {baseActionId}）";
            nextAttemptUtc = now.AddMilliseconds(100);
            return true;
        }

        if (BeastmasterActionHelper.TryGetActionLevel(adjustedActionId, out var requiredLevel)
            && DalamudApi.ObjectTable.LocalPlayer is { } player
            && player.Level < requiredLevel)
        {
            PrintChat($"戦闘手順をスキップ: {step.Label}（Lv.{player.Level} / 要求Lv.{requiredLevel}）");
            combatStep++;
            stepDeadlineUtc = now.AddSeconds(8);
            ResetCombatStepFailure();
            return true;
        }

        if (baseActionId == ReleaseActionId
            && target is not null
            && !BeastmasterActionHelper.IsSummonInActionRange(
                target,
                gauge.SummonEntry?.ReleaseActionId ?? adjustedActionId,
                out var summonDistance,
                out var actionRange))
        {
            combatStepFailure = $"使役魔獣が「はなつ」の射程内に入るのを待機中（現在 {summonDistance:0.##}/{actionRange:0.##} ヤルム）";
            nextAttemptUtc = now.AddMilliseconds(250);
            return true;
        }

        if (BeastmasterFinalStrikeLock.IsBlocked(baseActionId, now))
        {
            combatStepFailure = BeastmasterFinalStrikeLock.GetBlockReason(baseActionId, now);
            nextAttemptUtc = now.AddMilliseconds(100);
            return true;
        }

        var actionStatus = actionManager->GetActionStatus(ActionType.Action, adjustedActionId, targetId);
        if (actionStatus != 0)
        {
            combatStepFailure = $"GetActionStatus={actionStatus}（Action {baseActionId}→{adjustedActionId}、ターゲット {targetId}）";
            nextAttemptUtc = now.AddMilliseconds(100);
            return true;
        }

        if (!actionManager->UseAction(ActionType.Action, adjustedActionId, targetId))
        {
            combatStepFailure = $"UseActionがfalseを返しました（Action {baseActionId}→{adjustedActionId}、Status=0、ターゲット {targetId}）";
            nextAttemptUtc = now.AddMilliseconds(100);
            return true;
        }

        PrintChat($"戦闘アクションをリクエスト：{combatStep + 1}/{sequence.CombatSteps.Count}「{step.Label}」");
        if (baseActionId == ReleaseActionId)
        {
            BeastmasterFinalStrikeLock.RecordRelease(now);
        }
        else if (baseActionId == FinalStrikeActionId)
        {
            BeastmasterFinalStrikeLock.RecordFinalStrike(now);
        }

        nextAttemptUtc = now.AddMilliseconds(baseActionId is WhistleOneActionId or WhistleTwoActionId or WhistleThreeActionId ? 250 : 350);
        if (baseActionId == WhistleTwoActionId)
        {
            pendingWhistle = 2;
            combatStepFailure = "二号呼び笛の確認待機中";
        }
        else if (baseActionId == WhistleThreeActionId)
        {
            pendingWhistle = 3;
            combatStepFailure = "三号呼び笛の確認待機中";
        }
        else
        {
            combatStep++;
            stepDeadlineUtc = now.AddSeconds(8);
            ResetCombatStepFailure();
        }
        return true;
    }

    private void ResetCombatStepFailure()
    {
        combatStepFailure = "未試行";
        combatStepAttempts = 0;
    }

    private static bool MissedCountdownDeadline(float remaining, BeastmasterSequenceDefinition sequence, int stepIndex = 0)
    {
        var deadline = stepIndex + 1 < sequence.CountdownSteps.Count
            ? Math.Abs(sequence.CountdownSteps[stepIndex + 1].TimeSeconds ?? 0f)
            : 0f;
        return remaining <= deadline;
    }

    private static int FindBorrowedActionStep(BeastmasterSequenceDefinition sequence, int startIndex)
    {
        for (var index = startIndex; index < sequence.CountdownSteps.Count; index++)
        {
            if (sequence.CountdownSteps[index].ActionId is >= 44896 and <= 44903)
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsTargetAction(uint actionId)
        => actionId is SmashActionId or BiteActionId or ShieldActionId or ShieldChargeActionId or ReleaseActionId or FinalStrikeActionId;

    private static bool TryGetWhistleIndex(uint actionId, out byte whistleIndex)
    {
        whistleIndex = actionId switch
        {
            WhistleOneActionId => 1,
            WhistleTwoActionId => 2,
            WhistleThreeActionId => 3,
            _ => 0,
        };
        return whistleIndex != 0;
    }

    private static bool IsValidTarget(IBattleChara? target)
        => target is not null
            && target.ObjectKind == ObjectKind.BattleNpc
            && target.IsTargetable
            && !target.IsDead
            && target.CurrentHp > 0;

    private static string GetActionName(uint actionId)
        => DalamudApi.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>()
            .TryGetRow(actionId, out var action)
                ? action.Name.ExtractText()
                : BeastmasterRuleActions.GetActionName(actionId);
}
