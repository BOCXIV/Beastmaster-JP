using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Beastmaster;

public sealed unsafe class BeastmasterCrucibleItemService
{
    private const string UseStatusSignature = "48 89 5C 24 08 48 89 74 24 10 57 48 83 EC 20 41 8B F8 48 8B D9 83 FA 0A 0F 83 ?? ?? ?? ?? 8B C2 48 8D 14 40 48 8D 34 91 0F B7 86 84 23 00 00";
    private const string RefreshMappingSignature = "48 89 5C 24 18 57 48 83 EC 30 48 8B D9 E8 ?? ?? ?? ?? 48 8B C8 E8 ?? ?? ?? ?? 48 8B F8 48 85 C0 0F 84 ?? ?? ?? ?? 48 89 6C 24 40 33 ED";
    private const int SlotCount = 10;
    private const int MappingOffset = 80;
    private const int MappingStride = 8;
    private const int InventoryOffset = 9092;
    private const int InventoryStride = 12;
    private const uint FirstRecoveryActionId = 46959;
    private const ushort FirstRecoveryItemId = 76;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan RecoveryUseInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FangUseInterval = TimeSpan.FromSeconds(3);
    private static readonly ushort[] RecoveryItemPriority = [140, 79, 78, 77, 76, 82, 81, 80, 135];
    private static readonly ushort[] FangItemPriority = [134, 133, 132, 131, 130, 129, 128, 139];

    private DateTime nextRequestUtc = DateTime.MinValue;
    private DateTime nextRecoveryUseUtc = DateTime.MinValue;
    private DateTime nextFangUseUtc = DateTime.MinValue;
    private DateTime itemDetailCleanupNotBeforeUtc = DateTime.MinValue;
    private DateTime itemDetailCleanupDeadlineUtc = DateTime.MinValue;
    private PendingRequest? pendingRequest;
    private string pendingRequestLastFailure = string.Empty;
    private ushort dispatchedRecoveryItemId;
    private ushort failedRecoveryItemId;
    private string recoveryDispatchFailure = string.Empty;
    private delegate* unmanaged<byte*, uint, byte, uint> getUseStatus;
    private delegate* unmanaged<byte*, void> refreshMapping;
    private bool nativeInitializationAttempted;
    private nint mappingDirector;
    private uint mappingTerritory;
    private readonly ushort[] mappingInventoryItemIds = new ushort[SlotCount];
    private readonly List<string> executionProbes = [];
    private readonly Queue<RuleDispatchResult> ruleDispatchResults = [];

    public string LastFailureReason { get; private set; } = "未知の理由";

    public string LastDiagnostic { get; private set; } = string.Empty;

    public bool HasPendingRequest => pendingRequest != null;

    public bool TryTakeExecutionProbe(out string probe)
    {
        if (executionProbes.Count == 0)
        {
            probe = string.Empty;
            return false;
        }

        probe = executionProbes[0];
        executionProbes.RemoveAt(0);
        return true;
    }

    public bool TryUseBestRecoveryItem(
        IBattleChara player,
        IBattleChara? target,
        DateTime now,
        out ushort itemId,
        RuleRequestSource? ruleSource = null)
    {
        itemId = 0;
        if (pendingRequest != null || now < nextRecoveryUseUtc || player.IsDead || player.CurrentHp == 0)
        {
            LastFailureReason = pendingRequest != null ? "クルーシブルアイテム待機キュー実行中"
                : now < nextRecoveryUseUtc ? "回復アイテム再使用待機中"
                : "自身が戦闘不能または HP が 0";
            return false;
        }

        if (!InitializeNative())
        {
            LastFailureReason = "クルーシブルアイテムのネイティブシグネチャが利用不可";
            return false;
        }

        var actionManager = ActionManager.Instance();
        var hotbar = RaptureHotbarModule.Instance();
        var targets = TargetSystem.Instance();
        var agentModule = AgentModule.Instance();
        var agent = agentModule == null
            ? null
            : (byte*)agentModule->GetAgentByInternalId((AgentId)497);
        var eventFramework = EventFramework.Instance();
        var director = eventFramework == null
            ? null
            : eventFramework->GetInstanceContentDirector();
        if (actionManager == null || hotbar == null || targets == null || agent == null || director == null
            || (int)director->InstanceContentType != 22)
        {
            LastFailureReason = actionManager == null ? "ActionManager が利用不可"
                : hotbar == null ? "RaptureHotbarModule が利用不可"
                : targets == null ? "TargetSystem が利用不可"
                : agent == null ? "クルーシブルアイテム Agent が利用不可"
                : director == null ? "コンテンツディレクターが利用不可"
                : $"現在のコンテンツ種別が闘獣練ではありません（InstanceContentType={(int)director->InstanceContentType}）";
            return false;
        }

        var self = (GameObject*)player.Address;
        EnsureMapping(agent, director);
        var diagnostic = new System.Text.StringBuilder();
        foreach (var recoveryItemId in RecoveryItemPriority)
        {
            var displaySlot = FindDisplaySlot(agent, (byte*)director, recoveryItemId, out var inventorySlot);
            var useStatus = displaySlot < 0 ? uint.MaxValue : getUseStatus((byte*)director, inventorySlot, 0);
            var canUse = displaySlot >= 0 && useStatus == 0 && CanUseOnTarget(recoveryItemId, self, self);
            diagnostic.Append(
                $"\n  {recoveryItemId}:スロット={displaySlot}/インベントリ={inventorySlot}/ステータス={useStatus}/自身に使用可能={(canUse ? "可" : "不可")}");
            if (displaySlot < 0 || useStatus != 0 || !canUse)
            {
                LastFailureReason = displaySlot < 0 ? $"回復アイテム {recoveryItemId} のクルーシブルスロットが見つかりません"
                    : useStatus != 0 ? $"回復アイテム {recoveryItemId} のゲーム内ステータスが利用不可（コード {useStatus}）"
                    : $"回復アイテム {recoveryItemId} は現在自身に使用できません";
                continue;
            }

            pendingRequest = new PendingRequest(recoveryItemId, player.GameObjectId, true, false, now.Add(RequestTimeout), displaySlot, inventorySlot, ruleSource);
            pendingRequestLastFailure = "未ディスパッチ";
            itemId = recoveryItemId;
            LastDiagnostic = $"選択フェーズ：{diagnostic}";
            return true;
        }

        LastDiagnostic = $"選択フェーズ（全不可）：{diagnostic}";

        var recoveryFailure = "ビーストポーションキット 140、ビーストポーションG4〜G1（79〜76）、ビーストパウダーG3〜G1（82〜80）、魔獣の吸血薬 135 が存在しないか、現在使用できません";
        if (target != null && !target.IsDead && target.CurrentHp > 0)
        {
            if (TryUseCrucibleItemOnTarget(134, target, now, ruleSource))
            {
                pendingRequest = pendingRequest! with { IsRecovery = true };
                itemId = 134;
                return true;
            }

            LastFailureReason = recoveryFailure + $"；吸血鬼の牙 134 が使用不可：{LastFailureReason}";
            return false;
        }

        LastFailureReason = recoveryFailure + "；吸血鬼の牙には有効な敵対ターゲットが必要です";
        return false;
    }

    public void Reset()
    {
        nextRequestUtc = DateTime.MinValue;
        nextRecoveryUseUtc = DateTime.MinValue;
        nextFangUseUtc = DateTime.MinValue;
        itemDetailCleanupNotBeforeUtc = DateTime.MinValue;
        itemDetailCleanupDeadlineUtc = DateTime.MinValue;
        pendingRequest = null;
        pendingRequestLastFailure = string.Empty;
        dispatchedRecoveryItemId = 0;
        failedRecoveryItemId = 0;
        recoveryDispatchFailure = string.Empty;
        mappingDirector = 0;
        mappingTerritory = 0;
        Array.Clear(mappingInventoryItemIds);
        scheduledProbe = default;
        executionProbes.Clear();
        ruleDispatchResults.Clear();
        LastDiagnostic = string.Empty;
    }

    public bool TryTakeRuleDispatchResult(out RuleDispatchResult result)
    {
        if (ruleDispatchResults.Count == 0)
        {
            result = default;
            return false;
        }

        result = ruleDispatchResults.Dequeue();
        return true;
    }

    public bool TryTakeDispatchedRecoveryItem(out ushort itemId)
    {
        itemId = dispatchedRecoveryItemId;
        dispatchedRecoveryItemId = 0;
        return itemId != 0;
    }

    public bool TryTakeRecoveryDispatchFailure(out ushort itemId, out string reason)
    {
        itemId = failedRecoveryItemId;
        reason = recoveryDispatchFailure;
        failedRecoveryItemId = 0;
        recoveryDispatchFailure = string.Empty;
        return itemId != 0;
    }

    public void ProcessPendingRequest(DateTime now)
    {
        UpdateItemDetailCleanup(now);
        if (pendingRequest is not { } request)
        {
            return;
        }

        if (now >= request.DeadlineUtc)
        {
            LastFailureReason = $"待機リクエストがタイムアウトしました。最終理由：{pendingRequestLastFailure}";
            RecordRuleDispatchResult(request, false, LastFailureReason);
            if (request.IsRecovery)
            {
                failedRecoveryItemId = request.ItemId;
                recoveryDispatchFailure = LastFailureReason;
            }
            pendingRequest = null;
            pendingRequestLastFailure = string.Empty;
            return;
        }

        var target = DalamudApi.ObjectTable
            .OfType<IBattleChara>()
            .FirstOrDefault(actor => actor.GameObjectId == request.TargetId);
        if (target == null)
        {
            LastFailureReason = "待機リクエストのターゲットが一時的に利用不可です";
            pendingRequestLastFailure = LastFailureReason;
            return;
        }

        if (!TryVerifyRequestSlot(request, out var verifiedDisplaySlot, out var verifiedInventorySlot, out var slotFailure))
        {
            LastFailureReason = slotFailure;
            RecordRuleDispatchResult(request, false, LastFailureReason);
            if (request.IsRecovery)
            {
                failedRecoveryItemId = request.ItemId;
                recoveryDispatchFailure = LastFailureReason;
            }

            pendingRequest = null;
            pendingRequestLastFailure = string.Empty;
            return;
        }

        if (!TryDispatchCrucibleItem(request.ItemId, verifiedDisplaySlot, verifiedInventorySlot, target, now))
        {
            pendingRequestLastFailure = LastFailureReason;
            return;
        }

        pendingRequest = null;
        pendingRequestLastFailure = string.Empty;
        RecordRuleDispatchResult(request, true,
            $"アイテム {request.ItemId} を実行しました（表示スロット {verifiedDisplaySlot}、インベントリスロット {verifiedInventorySlot}）");
        if (request.IsRecovery)
        {
            dispatchedRecoveryItemId = request.ItemId;
            nextRecoveryUseUtc = now.Add(RecoveryUseInterval);
        }
        if (request.IsFang)
        {
            nextFangUseUtc = now.Add(FangUseInterval);
        }
    }

    private bool TryVerifyRequestSlot(PendingRequest request, out int displaySlot, out uint inventorySlot, out string failure)
    {
        displaySlot = -1;
        inventorySlot = SlotCount;
        failure = string.Empty;
        if (!InitializeNative())
        {
            failure = "クルーシブルアイテムのネイティブシグネチャが利用不可";
            return false;
        }

        var agentModule = AgentModule.Instance();
        var agent = agentModule == null ? null : (byte*)agentModule->GetAgentByInternalId((AgentId)497);
        var eventFramework = EventFramework.Instance();
        var director = eventFramework == null ? null : eventFramework->GetInstanceContentDirector();
        if (agent == null || director == null || (int)director->InstanceContentType != 22)
        {
            failure = agent == null ? "クルーシブルアイテム Agent が利用不可"
                : director == null ? "コンテンツディレクターが利用不可"
                : $"現在のコンテンツ種別が闘獣練ではありません（InstanceContentType={(int)director->InstanceContentType}）";
            return false;
        }

        displaySlot = FindDisplaySlot(agent, (byte*)director, request.ItemId, out inventorySlot);
        failure = $"リクエストスロットが現在のマッピングと不一致（リクエスト: 表示={request.DisplaySlot}/インベントリ={request.InventorySlot}、"
            + $"現在: 表示={displaySlot}/インベントリ={inventorySlot}、アイテム={request.ItemId}）";
        return displaySlot == request.DisplaySlot && inventorySlot == request.InventorySlot;
    }

    public bool TryUseCrucibleItemOnTarget(
        BeastmasterCrucibleItemType itemType,
        IBattleChara player,
        IBattleChara? target,
        DateTime now,
        out ushort itemId,
        RuleRequestSource? ruleSource = null)
    {
        itemId = 0;
        if (itemType == BeastmasterCrucibleItemType.Recovery)
        {
            return TryUseBestRecoveryItem(player, target, now, out itemId, ruleSource);
        }

        if (itemType == BeastmasterCrucibleItemType.VampireFang)
        {
            if (now < nextFangUseUtc)
            {
                LastFailureReason = "牙アイテム再使用待機中";
                return false;
            }

            if (target != null && TryUseCrucibleItemOnTarget(134, target, now, ruleSource))
            {
                itemId = 134;
                return true;
            }

            if (target == null)
            {
                LastFailureReason = "吸血鬼の牙には有効な敵対ターゲットが必要です";
            }
            return false;
        }

        var selfItemId = itemType switch
        {
            BeastmasterCrucibleItemType.DodgeBook => (ushort)137,
            BeastmasterCrucibleItemType.ReflectBook => (ushort)136,
            BeastmasterCrucibleItemType.TimeSand => (ushort)138,
            BeastmasterCrucibleItemType.StrengthMedicine => (ushort)104,
            _ => (ushort)0,
        };
        if (selfItemId != 0)
        {
            if (TryUseCrucibleItemOnTarget(selfItemId, player, now, ruleSource))
            {
                itemId = selfItemId;
                return true;
            }

            return false;
        }

        if (pendingRequest != null)
        {
            LastFailureReason = "クルーシブルアイテム待機キュー実行中";
            return false;
        }
        if (now < nextFangUseUtc)
        {
            LastFailureReason = "牙アイテム再使用待機中";
            return false;
        }

        var fangFailures = new List<string>(FangItemPriority.Length);
        foreach (var fangItemId in FangItemPriority)
        {
            if (target != null && TryUseCrucibleItemOnTarget(fangItemId, target, now, ruleSource))
            {
                itemId = fangItemId;
                return true;
            }

            fangFailures.Add(target == null
                ? $"{fangItemId}:有効な敵対ターゲットが必要です"
                : $"{fangItemId}:{LastFailureReason}");
        }

        LastFailureReason = fangFailures.Count == 0
            ? "牙のIDが設定されていません"
            : "各種の牙がすべて利用不可（" + string.Join("；", fangFailures) + "）";
        return false;
    }

    public unsafe bool TryUseCrucibleItemOnTarget(
        ushort itemId,
        IBattleChara target,
        DateTime now,
        RuleRequestSource? ruleSource = null)
    {
        if (pendingRequest != null
            || now < nextRequestUtc
            || (IsFangItem(itemId) && now < nextFangUseUtc)
            || target.IsDead
            || target.CurrentHp == 0)
        {
            LastFailureReason = pendingRequest != null ? "クルーシブルアイテム待機キュー実行中"
                : now < nextRequestUtc ? "クルーシブルアイテムリクエスト抑制中"
                : IsFangItem(itemId) && now < nextFangUseUtc ? "牙アイテム再使用待機中"
                : "ターゲットが戦闘不能または HP が 0";
            return false;
        }

        if (!InitializeNative())
        {
            LastFailureReason = "クルーシブルアイテムのネイティブシグネチャが利用不可";
            return false;
        }

        var actionManager = ActionManager.Instance();
        var hotbar = RaptureHotbarModule.Instance();
        var targets = TargetSystem.Instance();
        var agentModule = AgentModule.Instance();
        var agent = agentModule == null
            ? null
            : (byte*)agentModule->GetAgentByInternalId((AgentId)497);
        var eventFramework = EventFramework.Instance();
        var director = eventFramework == null
            ? null
            : eventFramework->GetInstanceContentDirector();
        if (actionManager == null || hotbar == null || targets == null || agent == null || director == null
            || (int)director->InstanceContentType != 22)
        {
            LastFailureReason = actionManager == null ? "ActionManager が利用不可"
                : hotbar == null ? "RaptureHotbarModule が利用不可"
                : targets == null ? "TargetSystem が利用不可"
                : agent == null ? "クルーシブルアイテム Agent が利用不可"
                : director == null ? "コンテンツディレクターが利用不可"
                : $"現在のコンテンツ種別が闘獣練ではありません（InstanceContentType={(int)director->InstanceContentType}）";
            return false;
        }

        var targetObj = (GameObject*)target.Address;
        var player = DalamudApi.ObjectTable.LocalPlayer;
        var self = player == null ? null : (GameObject*)player.Address;
        EnsureMapping(agent, director);
        var displaySlot = FindDisplaySlot(agent, (byte*)director, itemId, out var inventorySlot);
        var useStatus = displaySlot < 0 ? uint.MaxValue : getUseStatus((byte*)director, inventorySlot, 0);
        if (displaySlot < 0
            || useStatus != 0
            || !CanUseOnTarget(itemId, self, targetObj))
        {
            LastFailureReason = displaySlot < 0 ? $"クルーシブルアイテム {itemId} のスロットが見つかりません"
                : useStatus != 0
                    ? $"クルーシブルアイテム {itemId} のゲーム内ステータスが利用不可（コード {useStatus}）"
                    : $"クルーシブルアイテム {itemId} は現在対象に使用できません（射程・視線不足等）";
            return false;
        }

        pendingRequest = new PendingRequest(
            itemId,
            target.GameObjectId,
            false,
            IsFangItem(itemId),
            now.Add(RequestTimeout),
            displaySlot,
            inventorySlot,
            ruleSource);
        pendingRequestLastFailure = "未ディスパッチ";
        return true;
    }

    private void RecordRuleDispatchResult(PendingRequest request, bool success, string detail)
    {
        if (request.RuleSource is { } source)
        {
            ruleDispatchResults.Enqueue(new RuleDispatchResult(source, request.ItemId, success, detail));
        }
    }

    private unsafe bool TryDispatchCrucibleItem(ushort itemId, int displaySlot, uint inventorySlot, IBattleChara target, DateTime now)
    {
        var actionManager = ActionManager.Instance();
        if (actionManager == null || actionManager->AnimationLock > 0f)
        {
            LastFailureReason = actionManager == null ? "ActionManager が利用不可" : "アクション実行硬直中";
            return false;
        }

        var hotbar = RaptureHotbarModule.Instance();
        var targets = TargetSystem.Instance();
        var agentModule = AgentModule.Instance();
        var agent = agentModule == null ? null : (byte*)agentModule->GetAgentByInternalId((AgentId)497);
        var eventFramework = EventFramework.Instance();
        var director = eventFramework == null ? null : eventFramework->GetInstanceContentDirector();
        if (hotbar == null || targets == null || agent == null || director == null
            || (int)director->InstanceContentType != 22)
        {
            LastFailureReason = hotbar == null ? "RaptureHotbarModule が利用不可"
                : targets == null ? "TargetSystem が利用不可"
                : agent == null ? "クルーシブルアイテム Agent が利用不可"
                : director == null ? "コンテンツディレクターが利用不可"
                : $"現在のコンテンツ種別が闘獣練ではありません（InstanceContentType={(int)director->InstanceContentType}）";
            return false;
        }

        EnsureMapping(agent, director);
        var useStatus = displaySlot < 0 ? uint.MaxValue : getUseStatus((byte*)director, inventorySlot, 0);
        LastDiagnostic = $"ディスパッチフェーズ：アイテム={itemId}/スロット={displaySlot}/インベントリ={inventorySlot}/ステータス={useStatus}/AnimationLock={actionManager->AnimationLock:0.###}";
        if (displaySlot < 0 || useStatus != 0)
        {
            LastFailureReason = displaySlot < 0
                ? $"クルーシブルアイテム {itemId} のスロットが見つかりません"
                : $"クルーシブルアイテム {itemId} のゲーム内ステータスが利用不可（コード {useStatus}）";
            return false;
        }

        var player = DalamudApi.ObjectTable.LocalPlayer;
        var self = player == null ? null : (GameObject*)player.Address;
        var targetObj = (GameObject*)target.Address;
        if (!CanUseOnTarget(itemId, self, targetObj))
        {
            LastFailureReason = $"クルーシブルアイテム {itemId} は現在対象に使用できません（射程・視線不足等）";
            LastDiagnostic += "/対象に使用可能=不可";
            return false;
        }

        LastDiagnostic += "/対象に使用可能=可";
        var inventoryBefore = *(ushort*)((byte*)director + InventoryOffset + inventorySlot * InventoryStride);
        var lockBefore = actionManager->AnimationLock;
        var hpBefore = self == null ? 0u : ((IBattleChara)target).CurrentHp;
        var previousSoftTarget = targets->SoftTarget;
        byte executed = 0;
        var dispatchPath = string.Empty;
        try
        {
            targets->SoftTarget = targetObj;
            if (TryFindCrucibleHotbarSlot(displaySlot, out var hotbarId, out var hotbarSlotId))
            {
                executed = hotbar->ExecuteSlotById((uint)hotbarId, (uint)hotbarSlotId);
                dispatchPath = $"ExecuteSlotById={executed} ホットバー={hotbarId}/{hotbarSlotId}";
            }
            else if (TryDispatchViaAgent(agent, displaySlot, now))
            {
                executed = 1;
                dispatchPath = "Agent497 ReceiveEvent フォールバックパス";
            }
            else
            {
                LastFailureReason = $"クルーシブルアイテムスロット {displaySlot} に対応するホットバースロットが見つからず、Agentフォールバック実行も失敗しました";
                LastDiagnostic += "/ホットバースロット=未検出/Agentフォールバック=失敗";
                return false;
            }
        }
        finally
        {
            targets->SoftTarget = previousSoftTarget;
        }

        var inventoryAfter = *(ushort*)((byte*)director + InventoryOffset + inventorySlot * InventoryStride);
        executionProbes.Add(
            $"[実行プローブ {DateTime.Now:HH:mm:ss.fff}] アイテム{itemId} 対象={target.GameObjectId} 表示スロット={displaySlot}：{dispatchPath} "
            + $"実行前 AnimationLock={lockBefore:0.###}/インベントリitemId={inventoryBefore}/HP={hpBefore} → "
            + $"実行後 AnimationLock={actionManager->AnimationLock:0.###}/インベントリitemId={inventoryAfter}");
        if (executed == 0)
        {
            LastFailureReason = $"クルーシブルアイテム {itemId} のホットバー実行が 0 を返しました";
            return false;
        }

        ScheduleExecutionProbe(itemId, displaySlot, inventorySlot, now);
        nextRequestUtc = now.AddMilliseconds(500);
        return true;
    }

    private bool TryDispatchViaAgent(byte* agent, int displaySlot, DateTime now)
    {
        if (agent == null || displaySlot is < 0 or >= SlotCount)
        {
            return false;
        }

        var agentModule = AgentModule.Instance();
        var itemDetail = agentModule == null
            ? null
            : agentModule->GetAgentByInternalId((AgentId)498);
        var itemDetailWasActive = itemDetail != null && ((AgentInterface*)itemDetail)->IsAgentActive();

        var selectArgs = stackalloc AtkValue[3];
        selectArgs[0] = new AtkValue { Type = (AtkValueType)3, Int = 6 };
        selectArgs[1] = new AtkValue { Type = (AtkValueType)3, Int = displaySlot };
        selectArgs[2] = new AtkValue { Type = AtkValueType.Undefined };
        var result = new AtkValue();
        ((AgentInterface*)agent)->ReceiveEvent(&result, selectArgs, 3, 0);

        var useArgs = stackalloc AtkValue[5];
        useArgs[0] = new AtkValue { Type = (AtkValueType)3, Int = 0 };
        useArgs[1] = new AtkValue { Type = (AtkValueType)3, Int = 0 };
        useArgs[2] = new AtkValue { Type = (AtkValueType)5, UInt = 0 };
        useArgs[3] = new AtkValue { Type = AtkValueType.Undefined };
        useArgs[4] = new AtkValue { Type = AtkValueType.Undefined };
        ((AgentInterface*)agent)->ReceiveEvent(&result, useArgs, 5, 3);
        if (!itemDetailWasActive)
        {
            itemDetailCleanupNotBeforeUtc = now.AddMilliseconds(100);
            itemDetailCleanupDeadlineUtc = now.AddSeconds(1);
        }
        return true;
    }

    private void UpdateItemDetailCleanup(DateTime now)
    {
        if (itemDetailCleanupDeadlineUtc == DateTime.MinValue
            || now < itemDetailCleanupNotBeforeUtc)
        {
            return;
        }

        if (now >= itemDetailCleanupDeadlineUtc)
        {
            itemDetailCleanupNotBeforeUtc = DateTime.MinValue;
            itemDetailCleanupDeadlineUtc = DateTime.MinValue;
            return;
        }

        var agentModule = AgentModule.Instance();
        var itemDetail = agentModule == null
            ? null
            : agentModule->GetAgentByInternalId((AgentId)498);
        if (itemDetail != null && ((AgentInterface*)itemDetail)->IsAgentActive())
        {
            ((AgentInterface*)itemDetail)->Hide();
            itemDetailCleanupNotBeforeUtc = DateTime.MinValue;
            itemDetailCleanupDeadlineUtc = DateTime.MinValue;
        }
    }

    private (ushort ItemId, int DisplaySlot, uint InventorySlot, DateTime DeadlineUtc) scheduledProbe;

    private void ScheduleExecutionProbe(ushort itemId, int displaySlot, uint inventorySlot, DateTime now)
        => scheduledProbe = (itemId, displaySlot, inventorySlot, now.AddSeconds(1));

    public void UpdateExecutionProbe(DateTime now)
    {
        if (scheduledProbe.DeadlineUtc == DateTime.MinValue)
        {
            return;
        }

        if (now >= scheduledProbe.DeadlineUtc)
        {
            var (itemId, displaySlot, inventorySlot, _) = scheduledProbe;
            scheduledProbe = default;
            var agentModule = AgentModule.Instance();
            var agent = agentModule == null ? null : (byte*)agentModule->GetAgentByInternalId((AgentId)497);
            var eventFramework = EventFramework.Instance();
            var director = eventFramework == null ? null : eventFramework->GetInstanceContentDirector();
            var actionManager = ActionManager.Instance();
            var player = DalamudApi.ObjectTable.LocalPlayer;
            if (agent != null && director != null && actionManager != null && (int)director->InstanceContentType == 22)
            {
                var inventoryItemId = *(ushort*)((byte*)director + InventoryOffset + inventorySlot * InventoryStride);
                var foundDisplaySlot = FindDisplaySlot(agent, (byte*)director, itemId, out _);
                executionProbes.Add(
                    $"[実行プローブ +1s] アイテム{itemId}：AnimationLock={actionManager->AnimationLock:0.###}"
                    + $"/元インベントリスロットitemId={inventoryItemId}/現在検出表示スロット={foundDisplaySlot}"
                    + $"/HP={(player == null ? 0 : player.CurrentHp)}");
            }
            else
            {
                executionProbes.Add($"[実行プローブ +1s] アイテム{itemId}：コンテキスト利用不可");
            }
        }
    }

    private bool TryFindCrucibleHotbarSlot(int displaySlot, out int hotbarId, out int slotId)
    {
        hotbarId = -1;
        slotId = -1;
        var hotbar = RaptureHotbarModule.Instance();
        if (hotbar == null)
        {
            return false;
        }

        const int hotbarCount = 18;
        for (var candidateHotbar = 0; candidateHotbar < hotbarCount; candidateHotbar++)
        {
            for (var candidateSlot = 0; candidateSlot < 16; candidateSlot++)
            {
                var slot = hotbar->GetSlotById((uint)candidateHotbar, (uint)candidateSlot);
                if (slot != null
                    && (byte)slot->CommandType == 36
                    && slot->CommandId == (uint)displaySlot)
                {
                    hotbarId = candidateHotbar;
                    slotId = candidateSlot;
                    return true;
                }
            }
        }

        return false;
    }

    private void EnsureMapping(byte* agent, InstanceContentDirector* director)
    {
        var directorAddress = (nint)director;
        var territory = DalamudApi.ClientState.TerritoryType;
        var changed = mappingDirector != directorAddress || mappingTerritory != territory;
        for (var inventorySlot = 0; inventorySlot < SlotCount; inventorySlot++)
        {
            var itemId = *(ushort*)((byte*)director + InventoryOffset + inventorySlot * InventoryStride);
            if (mappingInventoryItemIds[inventorySlot] != itemId)
            {
                changed = true;
            }

            mappingInventoryItemIds[inventorySlot] = itemId;
        }

        mappingDirector = directorAddress;
        mappingTerritory = territory;
        if (changed)
        {
            refreshMapping(agent);
        }
    }

    private int FindDisplaySlot(byte* agent, byte* director, ushort wantedItemId, out uint inventorySlot)
    {
        inventorySlot = SlotCount;
        for (var displaySlot = 0; displaySlot < SlotCount; displaySlot++)
        {
            var entry = agent + MappingOffset + displaySlot * MappingStride;
            var candidateInventorySlot = *(uint*)entry;
            var itemId = ((ushort*)entry)[2];
            if (candidateInventorySlot < SlotCount
                && itemId == wantedItemId
                && *(ushort*)(director + InventoryOffset + candidateInventorySlot * InventoryStride) == itemId)
            {
                inventorySlot = candidateInventorySlot;
                return displaySlot;
            }
        }

        return -1;
    }

    public string DescribeRecoveryItemSlot(ushort itemId)
    {
        if (!InitializeNative())
        {
            return "ネイティブシグネチャが利用不可";
        }

        var agentModule = AgentModule.Instance();
        var agent = agentModule == null ? null : (byte*)agentModule->GetAgentByInternalId((AgentId)497);
        var eventFramework = EventFramework.Instance();
        var director = eventFramework == null ? null : eventFramework->GetInstanceContentDirector();
        if (agent == null || director == null || (int)director->InstanceContentType != 22)
        {
            return "闘獣練エリア外";
        }

        var displaySlot = FindDisplaySlot(agent, (byte*)director, itemId, out var inventorySlot);
        if (displaySlot < 0)
        {
            return $"アイテム {itemId} は表示スロットに存在しません（消費済みまたは並び替え）";
        }

        var inventoryItemId = *(ushort*)((byte*)director + InventoryOffset + inventorySlot * InventoryStride);
        return $"アイテム {itemId} は表示スロット {displaySlot}/インベントリ {inventorySlot} に存在します（インベントリ内 itemId={inventoryItemId}、未消費）";
    }

    private bool InitializeNative()
    {
        if (getUseStatus != null && refreshMapping != null)
        {
            return true;
        }

        if (nativeInitializationAttempted)
        {
            return false;
        }

        nativeInitializationAttempted = true;
        if (!DalamudApi.SigScanner.TryScanText(UseStatusSignature, out var statusAddress)
            || !DalamudApi.SigScanner.TryScanText(RefreshMappingSignature, out var refreshAddress))
        {
            DalamudApi.Log.Warning("クルーシブル回復アイテムのネイティブシグネチャが見つからないため、回復アイテムの自動使用を停止しました。");
            return false;
        }

        getUseStatus = (delegate* unmanaged<byte*, uint, byte, uint>)statusAddress;
        refreshMapping = (delegate* unmanaged<byte*, void>)refreshAddress;
        return true;
    }

    private static bool CanUseOnTarget(ushort itemId, GameObject* self, GameObject* target)
    {
        var actionId = GetTargetCheckAction(itemId);
        return actionId != 0
            && self != null
            && target != null
            && ActionManager.CanUseActionOnTarget(actionId, target)
            && ActionManager.GetActionInRangeOrLoS(actionId, self, target) == 0;
    }

    private static uint GetTargetCheckAction(ushort itemId)
        => itemId switch
        {
            >= 76 and <= 104 => FirstRecoveryActionId + itemId - FirstRecoveryItemId,
            >= 105 and <= 113 => 46987u + (uint)(itemId - 104) / 2u,
            >= 114 and <= 141 => 46992u + itemId - 114u,
            _ => 0,
        };

    private static bool IsFangItem(ushort itemId)
        => itemId is 128 or 129 or 130 or 131 or 132 or 133 or 134 or 139;

    public readonly record struct RuleRequestSource(string RuleSetName, string RuleName, int RuleIndex);

    public readonly record struct RuleDispatchResult(RuleRequestSource Source, ushort ItemId, bool Success, string Detail);

    private sealed record PendingRequest(
        ushort ItemId,
        ulong TargetId,
        bool IsRecovery,
        bool IsFang,
        DateTime DeadlineUtc,
        int DisplaySlot,
        uint InventorySlot,
        RuleRequestSource? RuleSource);
}