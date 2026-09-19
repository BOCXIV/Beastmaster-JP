using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Beastmaster;

public sealed class BeastmasterRuleService
{
    private readonly BeastmasterConfiguration configuration;
    private readonly BeastmasterCrucibleItemService crucibleItemService;
    private DateTime lastChatUtc = DateTime.MinValue;
    private string lastChatKey = string.Empty;
    private readonly Dictionary<string, string> lastFailureMessages = new(StringComparer.Ordinal);

    public BeastmasterRuleService(BeastmasterConfiguration configuration, BeastmasterCrucibleItemService crucibleItemService)
    {
        this.configuration = configuration;
        this.crucibleItemService = crucibleItemService;
    }

    public bool Enabled => configuration.RuleModeEnabled;
    public string LastDiagnostic { get; private set; } = "ルールモード無効";
    public DateTime LastDiagnosticUtc { get; private set; } = DateTime.MinValue;

    public void SetEnabled(bool enabled)
    {
        configuration.RuleModeEnabled = enabled;
        configuration.Save();
        RecordDiagnostic(enabled ? "ルールモード有効、条件合致待機中" : "ルールモード無効");
    }

    public unsafe bool TryHandle(ActionManager* actionManager, IBattleChara player, IBattleChara? target, DateTime now)
    {
        if (!Enabled || !DalamudApi.Condition[ConditionFlag.InCombat])
        {
            return false;
        }

        var territoryId = (ushort)Math.Clamp(DalamudApi.ClientState.TerritoryType, 0u, ushort.MaxValue);
        var validRuleCount = 0;
        var matchedRuleCount = 0;
        foreach (var ruleSet in configuration.RuleSets)
        {
            if (!ruleSet.Enabled || !ruleSet.AppliesTo(territoryId))
            {
                continue;
            }

            for (var ruleIndex = 0; ruleIndex < ruleSet.Rules.Count; ruleIndex++)
            {
                var rule = ruleSet.Rules[ruleIndex];
                if (!rule.Enabled || !rule.TryValidate(out _))
                {
                    continue;
                }

                validRuleCount++;

                if (!MatchesRule(rule, player, target, out var matchReason))
                {
                    continue;
                }

                matchedRuleCount++;
                if (TryExecute(actionManager, ruleSet, rule, ruleIndex, player, target, matchReason, now))
                {
                    return true;
                }
            }
        }

        if (configuration.RuleDiagnosticsEnabled)
        {
            var targetSnapshot = target == null
                ? "現在有効なターゲットなし"
                : $"現在ターゲット BaseId={target.BaseId}、HP={target.CurrentHp}/{target.MaxHp}";
            var message = validRuleCount == 0
                ? $"ルール診断：現在のエリア {territoryId} に実行可能な有効ルールがありません"
                : matchedRuleCount == 0
                    ? $"ルール診断：{validRuleCount} 件のルールをチェックしましたが、条件に一致するものはありませんでした；{targetSnapshot}"
                    : $"ルール診断：{matchedRuleCount}/{validRuleCount} 件のルールが一致しましたがすべて実行に失敗し、通常ACRへフォールバック；{targetSnapshot}";
            RecordDiagnostic(message);
            if (validRuleCount > 0)
            {
                var diagnosticRuleSet = configuration.RuleSets.FirstOrDefault(set => set.Enabled
                    && set.AppliesTo(territoryId)
                    && set.DiagnosticMode == BeastmasterRuleDiagnosticMode.Full);
                if (diagnosticRuleSet != null)
                {
                    PrintChat($"{diagnosticRuleSet.Name}|未命中", message, now, TimeSpan.FromSeconds(2));
                }
            }
        }

        return false;
    }

    private unsafe bool TryExecute(
        ActionManager* actionManager,
        BeastmasterRuleSetDefinition ruleSet,
        BeastmasterRuleDefinition rule,
        int ruleIndex,
        IBattleChara player,
        IBattleChara? target,
        string matchReason,
        DateTime now)
    {
        if (rule.ActionType == BeastmasterRuleActionType.CrucibleItem)
        {
            return TryExecuteCrucibleItem(ruleSet, rule, ruleIndex, target, matchReason, now);
        }

        var targetId = BeastmasterRuleActions.RequiresTarget(rule.ActionId)
            ? target?.GameObjectId ?? 0UL
            : 0UL;
        if (BeastmasterRuleActions.RequiresTarget(rule.ActionId) && targetId == 0)
        {
            Fail(ruleSet, rule, ruleIndex, matchReason, "選択したアクションには有効なターゲットが必要です", now);
            return false;
        }

        if (BeastmasterFinalStrikeLock.IsBlocked(rule.ActionId, now))
        {
            Fail(ruleSet, rule, ruleIndex, matchReason, BeastmasterFinalStrikeLock.GetBlockReason(rule.ActionId, now), now);
            return false;
        }

        var availability = BeastmasterActionHelper.GetAvailability(
            rule.ActionId,
            targetId,
            BeastmasterRuleActions.UsesAdjustedActionId(rule.ActionId));
        if (!availability.CanUse)
        {
            Fail(ruleSet, rule, ruleIndex, matchReason, availability.Reason, now);
            return false;
        }

        if (target != null
            && !BeastmasterActionHelper.IsPlayerInActionRange(
                player,
                target,
                availability.ActionId,
                out var distance,
                out var actionRange))
        {
            var rangeReason = $"スキル射程内への移動待機中（現在 {distance:0.##}/{actionRange:0.##} ヤルム）";
            Fail(ruleSet, rule, ruleIndex, matchReason,
                rangeReason,
                now);
            return false;
        }

        if (!actionManager->UseAction(ActionType.Action, availability.ActionId, targetId))
        {
            Fail(ruleSet, rule, ruleIndex, matchReason, "アクションリクエスト失敗", now);
            return false;
        }

        if (rule.ActionId == 44890)
        {
            BeastmasterFinalStrikeLock.RecordRelease(now);
        }
        else if (rule.ActionId == 44891)
        {
            BeastmasterFinalStrikeLock.RecordFinalStrike(now);
        }

        var message = $"ルールセット「{ruleSet.Name}」第 {ruleIndex + 1} 条「{rule.Name}」条件一致：{matchReason}；{availability.ActionName}（{availability.ActionId}）を実行リクエスト";
        RecordDiagnostic(message);
        lastFailureMessages.Remove($"{ruleSet.Name}|{rule.Name}");
        if (configuration.RuleDiagnosticsEnabled
            && ruleSet.DiagnosticMode == BeastmasterRuleDiagnosticMode.Full)
        {
            PrintChat($"{ruleSet.Name}|{rule.Name}|成功", message, now, TimeSpan.FromSeconds(2));
        }
        return true;
    }

    private bool TryExecuteCrucibleItem(
        BeastmasterRuleSetDefinition ruleSet,
        BeastmasterRuleDefinition rule,
        int ruleIndex,
        IBattleChara? target,
        string matchReason,
        DateTime now)
    {
        var requiresTarget = rule.CrucibleItemType is BeastmasterCrucibleItemType.Fang
            or BeastmasterCrucibleItemType.VampireFang;
        if (requiresTarget && target == null)
        {
            Fail(ruleSet, rule, ruleIndex, matchReason, "クルーシブルアイテムには有効なターゲットが必要です", now);
            return false;
        }

        var player = DalamudApi.ObjectTable.LocalPlayer as IBattleChara;
        var requestSource = new BeastmasterCrucibleItemService.RuleRequestSource(ruleSet.Name, rule.Name, ruleIndex);
        if (player == null || !crucibleItemService.TryUseCrucibleItemOnTarget(
                rule.CrucibleItemType, player, target, now, out var itemId, requestSource))
        {
            Fail(ruleSet, rule, ruleIndex, matchReason, crucibleItemService.LastFailureReason, now);
            return false;
        }

        var itemName = BeastmasterRuleActions.GetCrucibleItemName(itemId);
        var message = $"ルールセット「{ruleSet.Name}」第 {ruleIndex + 1} 条「{rule.Name}」条件一致：{matchReason}；{itemName}（{itemId}）のリクエストを送信しました";
        RecordDiagnostic(message);
        return true;
    }

    public void ProcessCrucibleDispatchResults(DateTime now)
    {
        while (crucibleItemService.TryTakeRuleDispatchResult(out var result))
        {
            var ruleKey = $"{result.Source.RuleSetName}|{result.Source.RuleName}";
            var itemName = BeastmasterRuleActions.GetCrucibleItemName(result.ItemId);
            if (result.Success)
            {
                var message = $"ルールセット「{result.Source.RuleSetName}」第 {result.Source.RuleIndex + 1} 条「{result.Source.RuleName}」：{itemName}（{result.ItemId}）を使用しました";
                RecordDiagnostic(message);
                lastFailureMessages.Remove(ruleKey);
                var ruleSet = configuration.RuleSets.FirstOrDefault(set => string.Equals(set.Name, result.Source.RuleSetName, StringComparison.Ordinal));
                if (configuration.RuleDiagnosticsEnabled && ruleSet?.DiagnosticMode == BeastmasterRuleDiagnosticMode.Full)
                {
                    PrintChat($"{ruleKey}|成功", message, now, TimeSpan.FromSeconds(2));
                }
            }
            else
            {
                var ruleSet = configuration.RuleSets.FirstOrDefault(set => string.Equals(set.Name, result.Source.RuleSetName, StringComparison.Ordinal));
                if (ruleSet != null && result.Source.RuleIndex >= 0 && result.Source.RuleIndex < ruleSet.Rules.Count)
                {
                    Fail(ruleSet, ruleSet.Rules[result.Source.RuleIndex], result.Source.RuleIndex, "ディスパッチ失敗", result.Detail, now);
                }
                else
                {
                    RecordDiagnostic($"ルールディスパッチ失敗：{itemName}（{result.ItemId}）- {result.Detail}");
                }
            }
        }
    }

    private void Fail(
        BeastmasterRuleSetDefinition ruleSet,
        BeastmasterRuleDefinition rule,
        int ruleIndex,
        string matchReason,
        string failureReason,
        DateTime now)
    {
        var message = $"ルールセット「{ruleSet.Name}」第 {ruleIndex + 1} 条「{rule.Name}」条件一致：{matchReason}；{failureReason}のため通常ACRへフォールバック";
        RecordDiagnostic(message);
        if (configuration.RuleDiagnosticsEnabled
            && ruleSet.DiagnosticMode != BeastmasterRuleDiagnosticMode.Off)
        {
            PrintFailureChat($"{ruleSet.Name}|{rule.Name}", failureReason, message);
        }
    }

    private static bool Matches(
        BeastmasterRuleDefinition rule,
        IBattleChara player,
        IBattleChara? target,
        out string reason)
    {
        reason = string.Empty;
        switch (rule.ConditionType)
        {
            case BeastmasterRuleConditionType.SelfStatus:
                return MatchesStatus(rule, player.StatusList.Any(status => status.StatusId == rule.ConditionId), "自身", out reason);
            case BeastmasterRuleConditionType.TargetStatus:
                return target != null
                    && MatchesStatus(rule, target.StatusList.Any(status => status.StatusId == rule.ConditionId), "現在のターゲット", out reason);
            case BeastmasterRuleConditionType.TargetCast:
                if (target is { IsCasting: true } && target.CastActionId == rule.ConditionId)
                {
                    reason = $"現在のターゲットが詠唱中（{rule.ConditionId}）";
                    return true;
                }
                return false;
            case BeastmasterRuleConditionType.DataIdStatus:
            {
                var actors = FindDataIdActors(rule.DataId);
                var matched = rule.StatusCondition == BeastmasterRuleStatusCondition.Present
                    ? actors.Any(actor => actor.StatusList.Any(status => status.StatusId == rule.ConditionId))
                    : actors.Any(actor => actor.StatusList.All(status => status.StatusId != rule.ConditionId));
                if (matched)
                {
                    reason = $"{actors.Count}体のDataID {rule.DataId}対象にバフ {rule.ConditionId} が{GetStatusConditionText(rule.StatusCondition)}";
                }
                return matched;
            }
            case BeastmasterRuleConditionType.DataIdCast:
            {
                var actors = FindDataIdActors(rule.DataId);
                if (actors.Any(actor => actor.IsCasting && actor.CastActionId == rule.ConditionId))
                {
                    reason = $"{actors.Count}体のDataID {rule.DataId}対象が詠唱中（{rule.ConditionId}）";
                    return true;
                }
                return false;
            }
            case BeastmasterRuleConditionType.TargetDataId:
            {
                if (target == null)
                {
                    return false;
                }

                if (target.BaseId == rule.DataId)
                {
                    reason = $"現在のターゲットの DataID が {rule.DataId} に一致";
                    return true;
                }
                return false;
            }
            case BeastmasterRuleConditionType.SelfHp:
            {
                if (player.MaxHp == 0)
                {
                    return false;
                }

                var hpPercent = player.CurrentHp * 100f / player.MaxHp;
                var matched = rule.HpCondition == BeastmasterRuleHpCondition.Above
                    ? hpPercent > rule.HpThreshold
                    : hpPercent < rule.HpThreshold;
                if (matched)
                {
                    reason = $"自身HP {hpPercent:0.#}% {(rule.HpCondition == BeastmasterRuleHpCondition.Above ? ">" : "<")} {rule.HpThreshold:0.#}%";
                }
                return matched;
            }
            case BeastmasterRuleConditionType.TargetHp:
            {
                if (target == null || target.MaxHp == 0)
                {
                    return false;
                }

                var hpPercent = target.CurrentHp * 100f / target.MaxHp;
                var matched = rule.HpCondition == BeastmasterRuleHpCondition.Above
                    ? hpPercent > rule.HpThreshold
                    : hpPercent < rule.HpThreshold;
                if (matched)
                {
                    reason = $"ターゲットHP {hpPercent:0.#}% {(rule.HpCondition == BeastmasterRuleHpCondition.Above ? ">" : "<")} {rule.HpThreshold:0.#}%";
                }
                return matched;
            }
            case BeastmasterRuleConditionType.TargetIsBoss:
            {
                if (target == null || player.MaxHp == 0 || target.MaxHp == 0)
                {
                    return false;
                }

                var threshold = (double)player.MaxHp * 5d;
                var matched = target.MaxHp > threshold;
                if (matched)
                {
                    reason = $"ターゲット最大HP {target.MaxHp} > 自身最大HP {player.MaxHp} × 5";
                }
                return matched;
            }
            default:
                return false;
        }
    }

    private static bool MatchesRule(
        BeastmasterRuleDefinition rule,
        IBattleChara player,
        IBattleChara? target,
        out string reason)
    {
        rule.EnsureConditions();
        var results = new List<string>(rule.Conditions.Count);
        foreach (var condition in rule.Conditions)
        {
            var conditionRule = new BeastmasterRuleDefinition
            {
                ConditionType = condition.Type,
                StatusCondition = condition.StatusCondition,
                DataId = condition.DataId,
                ConditionId = condition.ConditionId,
                HpCondition = condition.HpCondition,
                HpThreshold = condition.HpThreshold,
            };
            if (Matches(conditionRule, player, target, out var conditionReason))
            {
                results.Add(conditionReason);
            }
            else if (rule.ConditionJoinMode == BeastmasterRuleConditionJoinMode.All)
            {
                reason = string.Empty;
                return false;
            }
        }

        var matched = rule.ConditionJoinMode == BeastmasterRuleConditionJoinMode.All
            ? results.Count == rule.Conditions.Count
            : results.Count > 0;
        reason = matched ? string.Join(rule.ConditionJoinMode == BeastmasterRuleConditionJoinMode.All ? " かつ " : " または ", results) : string.Empty;
        return matched;
    }

    private static bool MatchesStatus(BeastmasterRuleDefinition rule, bool hasStatus, string actor, out string reason)
    {
        var matched = rule.StatusCondition == BeastmasterRuleStatusCondition.Present ? hasStatus : !hasStatus;
        reason = matched ? $"{actor}にバフ {rule.ConditionId} が{GetStatusConditionText(rule.StatusCondition)}" : string.Empty;
        return matched;
    }

    private static List<IBattleChara> FindDataIdActors(uint dataId)
        => DalamudApi.ObjectTable
            .OfType<IBattleChara>()
            .Where(actor => actor.ObjectKind == ObjectKind.BattleNpc
                && actor.BaseId == dataId
                && !actor.IsDead
                && actor.CurrentHp > 0)
            .ToList();

    private static string GetStatusConditionText(BeastmasterRuleStatusCondition condition)
        => condition == BeastmasterRuleStatusCondition.Present ? "付与されている" : "付与されていない";

    private void RecordDiagnostic(string message)
    {
        LastDiagnostic = message;
        LastDiagnosticUtc = DateTime.UtcNow;
    }

    private void PrintChat(
        string key,
        string message,
        DateTime now,
        TimeSpan interval)
    {
        if (key == lastChatKey && now - lastChatUtc < interval)
        {
            return;
        }

        lastChatKey = key;
        lastChatUtc = now;
        DalamudApi.ChatGui.Print($"[魔獣使いアシスト {DateTime.Now:HH:mm:ss}] {message}");
    }

    private void PrintFailureChat(string ruleKey, string failureReason, string message)
    {
        if (lastFailureMessages.TryGetValue(ruleKey, out var previousReason)
            && previousReason == failureReason)
        {
            return;
        }

        lastFailureMessages[ruleKey] = failureReason;
        DalamudApi.ChatGui.Print($"[魔獣使いアシスト {DateTime.Now:HH:mm:ss}] {message}");
    }
}
