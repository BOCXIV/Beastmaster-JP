using System.Globalization;
using System.Text;

namespace Beastmaster;

public enum BeastmasterRuleConditionType
{
    SelfStatus,
    TargetStatus,
    DataIdStatus,
    DataIdCast,
    TargetCast,
    TargetDataId,
    SelfHp,
    TargetHp,
    TargetIsBoss,
    CurrentWhistle,
}

public enum BeastmasterRuleActionType
{
    Skill,
    CrucibleItem,
}

public enum BeastmasterCrucibleItemType
{
    Recovery,
    Fang,
    DodgeBook,
    ReflectBook,
    TimeSand,
    StrengthMedicine,
    VampireFang,
    StarSand,
    VampireMedicine,
    RecoverySet,
    Specific,
}

public enum BeastmasterRuleStatusCondition
{
    Present,
    Missing,
}

public enum BeastmasterRuleHpCondition
{
    Above,
    Below,
}

public enum BeastmasterRuleConditionJoinMode
{
    All,
    Any,
}

public enum BeastmasterRuleAreaMode
{
    All,
    Include,
    Exclude,
}

public enum BeastmasterRuleDiagnosticMode
{
    Off,
    Failures,
    Full,
}

[Serializable]
public sealed class BeastmasterRuleDefinition
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "新規ルール";
    public BeastmasterRuleConditionType ConditionType { get; set; }
    public BeastmasterRuleStatusCondition StatusCondition { get; set; } = BeastmasterRuleStatusCondition.Missing;
    public BeastmasterRuleConditionJoinMode ConditionJoinMode { get; set; } = BeastmasterRuleConditionJoinMode.All;
    public List<BeastmasterRuleCondition> Conditions { get; set; } = [];
    public BeastmasterRuleActionType ActionType { get; set; } = BeastmasterRuleActionType.Skill;
    public BeastmasterCrucibleItemType CrucibleItemType { get; set; } = BeastmasterCrucibleItemType.Recovery;
    public uint DataId { get; set; }
    public uint ConditionId { get; set; }
    public BeastmasterRuleHpCondition HpCondition { get; set; }
    public float HpThreshold { get; set; } = 50f;
    public byte WhistleIndex { get; set; } = 1;
    public uint ActionId { get; set; } = 44879;
    public uint CrucibleItemId { get; set; }

    public bool TryValidate(out string error)
    {
        error = string.Empty;
        Conditions ??= [];
        if (string.IsNullOrWhiteSpace(Name))
        {
            error = "ルール名を入力してください。";
            return false;
        }

        if (!Enum.IsDefined(ConditionType)
            || !Enum.IsDefined(StatusCondition)
            || !Enum.IsDefined(HpCondition)
            || !Enum.IsDefined(ConditionJoinMode))
        {
            error = "ルールの判定タイプが無効です。";
            return false;
        }

        if (Conditions.Count > 10)
        {
            error = "各ルールには最大 10 個の条件のみ設定できます。";
            return false;
        }

        if (Conditions.Count > 0)
        {
            foreach (var condition in Conditions)
            {
                if (!condition.TryValidate(out error))
                {
                    return false;
                }
            }
        }
        else if (!IsTargetDataIdRule && !IsHealthRule && !IsBossRule && !IsWhistleRule && ConditionId == 0)
        {
            error = IsStatusRule ? "バフIDは 0 より大きい必要があります。" : "詠唱IDは 0 より大きい必要があります。";
            return false;
        }

        if (Conditions.Count == 0 && IsHealthRule && (!float.IsFinite(HpThreshold) || HpThreshold is < 1f or > 100f))
        {
            error = "HP閾値は 1%〜100% の間である必要があります。";
            return false;
        }

        if (Conditions.Count == 0 && RequiresDataId && DataId == 0)
        {
            error = "DataIDは 0 より大きい必要があります。";
            return false;
        }

        if (Conditions.Count == 0 && IsWhistleRule && WhistleIndex is < 1 or > 3)
        {
            error = "呼び笛番号は 1、2、または 3 である必要があります。";
            return false;
        }

        if (ActionType == BeastmasterRuleActionType.Skill && !BeastmasterRuleActions.IsSupported(ActionId))
        {
            error = $"未対応のルールアクションです（{ActionId}）。";
            return false;
        }

        if (ActionType == BeastmasterRuleActionType.CrucibleItem
            && CrucibleItemType == BeastmasterCrucibleItemType.Specific
            && !BeastmasterRuleActions.IsCrucibleItemId(CrucibleItemId))
        {
            error = "有効なクルーシブルアイテムを選択してください。";
            return false;
        }

        return true;
    }

    public bool IsStatusRule
        => ConditionType is BeastmasterRuleConditionType.SelfStatus
            or BeastmasterRuleConditionType.TargetStatus
            or BeastmasterRuleConditionType.DataIdStatus;

    public bool RequiresDataId
        => ConditionType is BeastmasterRuleConditionType.DataIdStatus
            or BeastmasterRuleConditionType.DataIdCast
            or BeastmasterRuleConditionType.TargetDataId;

    public bool IsTargetDataIdRule
        => ConditionType is BeastmasterRuleConditionType.TargetDataId;

    public bool IsBossRule
        => ConditionType is BeastmasterRuleConditionType.TargetIsBoss;

    public bool IsWhistleRule
        => ConditionType is BeastmasterRuleConditionType.CurrentWhistle;

    public bool IsHealthRule
        => ConditionType is BeastmasterRuleConditionType.SelfHp
            or BeastmasterRuleConditionType.TargetHp;

    public void EnsureConditions()
    {
        Conditions ??= [];
        if (Conditions.Count == 0)
        {
            Conditions.Add(new BeastmasterRuleCondition
            {
                Type = ConditionType,
                StatusCondition = StatusCondition,
                DataId = DataId,
                ConditionId = ConditionId,
                HpCondition = HpCondition,
                HpThreshold = HpThreshold,
                WhistleIndex = WhistleIndex,
            });
        }
    }

    public void SyncLegacyFieldsFromFirstCondition()
    {
        if (Conditions is not { Count: > 0 }) return;
        var first = Conditions[0];
        ConditionType = first.Type;
        StatusCondition = first.StatusCondition;
        DataId = first.DataId;
        ConditionId = first.ConditionId;
        HpCondition = first.HpCondition;
        HpThreshold = first.HpThreshold;
        WhistleIndex = first.WhistleIndex;
    }
}

[Serializable]
public sealed class BeastmasterRuleCondition
{
    public BeastmasterRuleConditionType Type { get; set; }
    public BeastmasterRuleStatusCondition StatusCondition { get; set; } = BeastmasterRuleStatusCondition.Missing;
    public uint DataId { get; set; }
    public uint ConditionId { get; set; }
    public BeastmasterRuleHpCondition HpCondition { get; set; }
    public float HpThreshold { get; set; } = 50f;
    public byte WhistleIndex { get; set; } = 1;

    public bool IsStatusRule => Type is BeastmasterRuleConditionType.SelfStatus
        or BeastmasterRuleConditionType.TargetStatus
        or BeastmasterRuleConditionType.DataIdStatus;

    public bool RequiresDataId => Type is BeastmasterRuleConditionType.DataIdStatus
        or BeastmasterRuleConditionType.DataIdCast
        or BeastmasterRuleConditionType.TargetDataId;

    public bool IsHealthRule => Type is BeastmasterRuleConditionType.SelfHp
        or BeastmasterRuleConditionType.TargetHp;

    public bool TryValidate(out string error)
    {
        error = string.Empty;
        if (!Enum.IsDefined(Type) || !Enum.IsDefined(StatusCondition) || !Enum.IsDefined(HpCondition))
        {
            error = "条件タイプが無効です。";
            return false;
        }

        if (!IsHealthRule
            && Type != BeastmasterRuleConditionType.TargetDataId
            && Type != BeastmasterRuleConditionType.TargetIsBoss
            && Type != BeastmasterRuleConditionType.CurrentWhistle
            && ConditionId == 0)
        {
            error = IsStatusRule ? "バフIDは 0 より大きい必要があります。" : "詠唱IDは 0 より大きい必要があります。";
            return false;
        }

        if (RequiresDataId && DataId == 0)
        {
            error = "DataIDは 0 より大きい必要があります。";
            return false;
        }

        if (IsHealthRule && (!float.IsFinite(HpThreshold) || HpThreshold is < 1f or > 100f))
        {
            error = "HP閾値は 1%〜100% の間である必要があります。";
            return false;
        }

        if (Type == BeastmasterRuleConditionType.CurrentWhistle && WhistleIndex is < 1 or > 3)
        {
            error = "呼び笛番号は 1、2、または 3 である必要があります。";
            return false;
        }

        return true;
    }
}

[Serializable]
public sealed class BeastmasterRuleSetDefinition
{
    private const int MaximumRules = 100;
    private const int MaximumTerritories = 100;

    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "デフォルトルールセット";
    public string Description { get; set; } = string.Empty;
    public BeastmasterRuleAreaMode AreaMode { get; set; }
    public List<ushort> TerritoryIds { get; set; } = [];
    public BeastmasterRuleDiagnosticMode DiagnosticMode { get; set; } = BeastmasterRuleDiagnosticMode.Failures;
    public List<BeastmasterRuleDefinition> Rules { get; set; } = [];

    public static BeastmasterRuleSetDefinition CreateBuiltInArenaRules(bool attentionEnabled = false, bool provokeEnabled = false)
        => new()
        {
            Name = "デフォルトルールセット",
            Description = "闘獣練アクション",
            Enabled = true,
            AreaMode = BeastmasterRuleAreaMode.Include,
            TerritoryIds = [1339, 1340, 1341, 1342, 1343],
            DiagnosticMode = BeastmasterRuleDiagnosticMode.Failures,
            Rules =
            [
                new()
                {
                    Name = "ひきつけろ維持",
                    Enabled = attentionEnabled,
                    ConditionType = BeastmasterRuleConditionType.SelfStatus,
                    StatusCondition = BeastmasterRuleStatusCondition.Missing,
                    ConditionId = 2413,
                    ActionId = 46751,
                },
                new()
                {
                    Name = "ちょうはつ維持",
                    Enabled = provokeEnabled,
                    ConditionType = BeastmasterRuleConditionType.SelfStatus,
                    StatusCondition = BeastmasterRuleStatusCondition.Missing,
                    ConditionId = 5586,
                    ActionId = 46750,
                },
                new()
                {
                    Name = "最終バースト-第一盤",
                    Enabled = true,
                    ConditionType = BeastmasterRuleConditionType.TargetDataId,
                    DataId = 19344,
                    ActionType = BeastmasterRuleActionType.CrucibleItem,
                    CrucibleItemType = BeastmasterCrucibleItemType.Fang,
                },
            ],
        };

    public bool AppliesTo(ushort territoryId)
        => AreaMode switch
        {
            BeastmasterRuleAreaMode.All => true,
            BeastmasterRuleAreaMode.Include => TerritoryIds.Contains(territoryId),
            BeastmasterRuleAreaMode.Exclude => !TerritoryIds.Contains(territoryId),
            _ => false,
        };

    public bool TryValidate(out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(Name))
        {
            error = "ルールセット名を入力してください。";
            return false;
        }

        if (!Enum.IsDefined(AreaMode) || !Enum.IsDefined(DiagnosticMode))
        {
            error = "ルールセットのエリアまたは診断モードが無効です。";
            return false;
        }

        TerritoryIds ??= [];
        Rules ??= [];
        if (TerritoryIds.Count > MaximumTerritories || TerritoryIds.Any(id => id == 0))
        {
            error = $"エリアIDは 0 より大きく、最大 {MaximumTerritories} 個まで設定可能です。";
            return false;
        }

        if (Rules.Count > MaximumRules)
        {
            error = $"各ルールセットには最大 {MaximumRules} 件のルールを設定できます。";
            return false;
        }

        for (var index = 0; index < Rules.Count; index++)
        {
            if (!Rules[index].TryValidate(out var ruleError))
            {
                error = $"第 {index + 1} ルールが無効です：{ruleError}";
                return false;
            }
        }

        return true;
    }

    public string Export()
    {
        var builder = new StringBuilder()
            .AppendLine("BSTRULESET|1")
            .AppendLine($"名前|{Sanitize(Name)}")
            .AppendLine($"説明|{Sanitize(Description)}")
            .AppendLine($"有効|{Enabled}")
            .AppendLine($"エリアモード|{AreaMode}")
            .AppendLine($"エリア|{string.Join(',', TerritoryIds.Distinct())}")
            .AppendLine($"診断|{DiagnosticMode}");

        foreach (var rule in Rules)
        {
            rule.EnsureConditions();
            builder.AppendLine()
                .AppendLine("[ルール]")
                .AppendLine($"名前|{Sanitize(rule.Name)}")
                .AppendLine($"有効|{rule.Enabled}")
                .AppendLine($"判定|{rule.ConditionType}")
                .AppendLine($"条件|{rule.StatusCondition}")
                .AppendLine($"実行|{rule.ActionType}")
                .AppendLine($"クルーシブルアイテム種別|{rule.CrucibleItemType}")
                .AppendLine($"条件関係|{rule.ConditionJoinMode}")
                .AppendLine($"DataId|{rule.DataId}")
                .AppendLine($"判定ID|{rule.ConditionId}")
                .AppendLine($"HP条件|{rule.HpCondition}")
                .AppendLine($"HP閾値|{rule.HpThreshold.ToString(CultureInfo.InvariantCulture)}")
                .AppendLine($"呼び笛|{rule.WhistleIndex}")
                .AppendLine($"アクション|{rule.ActionId}")
                .AppendLine($"クルーシブルアイテム|{rule.CrucibleItemId}");
            for (var conditionIndex = 0; conditionIndex < rule.Conditions.Count; conditionIndex++)
            {
                var condition = rule.Conditions[conditionIndex];
                builder.AppendLine($"条件{conditionIndex}タイプ|{condition.Type}")
                    .AppendLine($"条件{conditionIndex}条件|{condition.StatusCondition}")
                    .AppendLine($"条件{conditionIndex}DataId|{condition.DataId}")
                    .AppendLine($"条件{conditionIndex}判定ID|{condition.ConditionId}")
                    .AppendLine($"条件{conditionIndex}HP条件|{condition.HpCondition}")
                    .AppendLine($"条件{conditionIndex}HP閾値|{condition.HpThreshold.ToString(CultureInfo.InvariantCulture)}")
                    .AppendLine($"条件{conditionIndex}呼び笛|{condition.WhistleIndex}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    public static bool TryImport(string text, out BeastmasterRuleSetDefinition? ruleSet, out string error)
    {
        ruleSet = null;
        error = string.Empty;
        var lines = text.Replace("\r", string.Empty).Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "BSTRULESET|1")
        {
            error = "1行目は BSTRULESET|1 である必要があります。";
            return false;
        }

        var result = new BeastmasterRuleSetDefinition();
        BeastmasterRuleDefinition? currentRule = null;
        for (var index = 1; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0) continue;
            if (line is "[ルール]" or "[\u89C4\u5219]")
            {
                currentRule = new BeastmasterRuleDefinition();
                result.Rules.Add(currentRule);
                continue;
            }

            var separator = line.IndexOf('|');
            if (separator <= 0)
            {
                error = $"第 {index + 1} 行のフォーマットが不正です。";
                return false;
            }

            var key = line[..separator];
            var value = line[(separator + 1)..];
            if (!TryAssign(result, currentRule, key, value))
            {
                error = $"第 {index + 1} 行のフィールドまたは値が無効です：{key}";
                return false;
            }
        }

        result.TerritoryIds = result.TerritoryIds.Distinct().ToList();
        result.EnsureImportedConditions();
        if (!result.TryValidate(out error)) return false;
        ruleSet = result;
        return true;
    }

    private static bool TryAssign(BeastmasterRuleSetDefinition ruleSet, BeastmasterRuleDefinition? rule, string key, string value)
    {
        if (rule == null)
        {
            switch (key)
            {
                case "名前": ruleSet.Name = value; return true;
                case "説明": ruleSet.Description = value; return true;
                case "有効" when bool.TryParse(value, out var enabled): ruleSet.Enabled = enabled; return true;
                case "エリアモード" when Enum.TryParse<BeastmasterRuleAreaMode>(value, out var areaMode): ruleSet.AreaMode = areaMode; return true;
                case "診断" when Enum.TryParse<BeastmasterRuleDiagnosticMode>(value, out var diagnostic): ruleSet.DiagnosticMode = diagnostic; return true;
                case "エリア":
                    if (value.Length == 0) return true;
                    foreach (var item in value.Split(','))
                    {
                        if (!ushort.TryParse(item, NumberStyles.None, CultureInfo.InvariantCulture, out var territory)) return false;
                        ruleSet.TerritoryIds.Add(territory);
                    }
                    return true;
                default: return false;
            }
        }

        if (key.StartsWith("条件", StringComparison.Ordinal)
            && key.Length > 2
            && char.IsDigit(key[2])
            && TryAssignIndexedCondition(rule, key, value))
        {
            return true;
        }

        switch (key)
        {
            case "名前": rule.Name = value; return true;
            case "有効" when bool.TryParse(value, out var enabled): rule.Enabled = enabled; return true;
            case "判定" when Enum.TryParse<BeastmasterRuleConditionType>(value, out var condition): rule.ConditionType = condition; return true;
            case "条件" when Enum.TryParse<BeastmasterRuleStatusCondition>(value, out var status): rule.StatusCondition = status; return true;
            case "条件関係" when Enum.TryParse<BeastmasterRuleConditionJoinMode>(value, out var joinMode): rule.ConditionJoinMode = joinMode; return true;
            case "実行" when Enum.TryParse<BeastmasterRuleActionType>(value, out var actionType): rule.ActionType = actionType; return true;
            case "クルーシブルアイテム種別" when Enum.TryParse<BeastmasterCrucibleItemType>(value, out var itemType): rule.CrucibleItemType = itemType; return true;
            case "DataId" when uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var dataId): rule.DataId = dataId; return true;
            case "判定ID" when uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var conditionId): rule.ConditionId = conditionId; return true;
            case "HP条件" when Enum.TryParse<BeastmasterRuleHpCondition>(value, out var hpCondition): rule.HpCondition = hpCondition; return true;
            case "HP閾値" when float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var hpThreshold): rule.HpThreshold = hpThreshold; return true;
            case "呼び笛" or "獣笛" when byte.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var whistleIndex): rule.WhistleIndex = whistleIndex; return true;
            case "アクション" when uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var actionId): rule.ActionId = actionId; return true;
            case "クルーシブルアイテム" when uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var itemId): rule.CrucibleItemId = itemId; return true;
            default: return false;
        }
    }

    private static bool TryAssignIndexedCondition(BeastmasterRuleDefinition rule, string key, string value)
    {
        var fieldStart = 2;
        while (fieldStart < key.Length && char.IsDigit(key[fieldStart])) fieldStart++;
        if (fieldStart == 2
            || !int.TryParse(key[2..fieldStart], NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            || index is < 0 or >= 10)
        {
            return false;
        }

        while (rule.Conditions.Count <= index) rule.Conditions.Add(new BeastmasterRuleCondition());
        var condition = rule.Conditions[index];
        var field = key[fieldStart..];
        return field switch
        {
            "タイプ" when Enum.TryParse<BeastmasterRuleConditionType>(value, out var type) => Set(() => condition.Type = type),
            "条件" when Enum.TryParse<BeastmasterRuleStatusCondition>(value, out var status) => Set(() => condition.StatusCondition = status),
            "DataId" when uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var dataId) => Set(() => condition.DataId = dataId),
            "判定ID" when uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) => Set(() => condition.ConditionId = id),
            "HP条件" when Enum.TryParse<BeastmasterRuleHpCondition>(value, out var hpCondition) => Set(() => condition.HpCondition = hpCondition),
            "HP閾値" when float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold) => Set(() => condition.HpThreshold = threshold),
            "呼び笛" or "獣笛" when byte.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var whistleIndex) => Set(() => condition.WhistleIndex = whistleIndex),
            _ => false,
        };

        static bool Set(Action action) { action(); return true; }
    }

    private void EnsureImportedConditions()
    {
        foreach (var rule in Rules) rule.EnsureConditions();
    }

    private static string Sanitize(string value)
        => value.Replace("\r", " ").Replace("\n", " ").Replace('|', ' ');
}

public static class BeastmasterRuleActions
{
    public static readonly uint[] SupportedActionIds =
    [
        44879, 44880, 44881, 44883, 44884, 44885, 44886, 44887,
        44888, 44889, 44890, 44891, 44892, 44893, 44894, 44895,
        44896, 44897, 44898, 44899, 44900, 44901, 44902, 44903,
        44904, 44905, 44930, 44931, 44932, 44933, 46750, 46751,
        47093,
    ];

    public static string GetActionName(uint actionId)
    {
        try
        {
            if (DalamudApi.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>()
                .TryGetRow(actionId, out var action))
            {
                var name = action.Name.ExtractText();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
            }
        }
        catch
        {
            // fallback
        }

        return actionId switch
        {
            44879 => "スマッシュ",
            44880 => "とらえる",
            44881 => "一号呼び笛",
            44883 => "アクスバイト",
            44884 => "アバランチアクス",
            44885 => "シールドスプリッター",
            44886 => "魔獣技",
            44887 => "ミストラルアクス",
            44888 => "スピニングアクス",
            44889 => "ラファールアクス",
            44890 => "はなつ",
            44891 => "さいごのいちげき",
            44892 => "二号呼び笛",
            44893 => "シールドチャージ",
            44894 => "三号呼び笛",
            44895 => "かりる",
            44896 => "ビーストスキン",
            44897 => "ヴァイルスキン",
            44898 => "クラウドスキム",
            44899 => "シードサワー",
            44900 => "クエリングウェーブ",
            44901 => "スケイルスキン",
            44902 => "ソウルクラッシュ",
            44903 => "アッシュクレンズ",
            44904 => "おうえん",
            44905 => "きあい",
            44930 => "ブルータルレイジ",
            44931 => "ホークスパイク",
            44932 => "ライジングフォール",
            44933 => "カラミティ",
            46750 => "ちょうはつ",
            46751 => "ひきつけろ",
            47093 => "おおわざ",
            _ => $"アクション {actionId}",
        };
    }

    public static (uint ActionId, string Name)[] Supported
        => SupportedActionIds.Select(id => (id, GetActionName(id))).ToArray();

    public static bool IsSupported(uint actionId)
        => SupportedActionIds.Contains(actionId);

    public static bool UsesAdjustedActionId(uint actionId)
        => actionId is 44886 or 44890 or 44895;

    public static bool RequiresTarget(uint actionId)
        => actionId is 44879 or 44880 or 44883 or 44884 or 44885 or 44887 or 44888 or 44889
            or 44890 or 44891 or 44893 or 44930 or 44931 or 44932 or 44933 or 46750 or 46751 or 47093;

    public static bool IsCrucibleItemId(uint itemId)
        => itemId is >= 76 and <= 143;

    public static bool IsCrucibleItemFriendly(uint itemId)
        => !RequiresCrucibleItemTarget(itemId);

    public static bool RequiresCrucibleItemTarget(uint itemId)
        => itemId is 84 or 96 or 128 or 129 or 130 or 131 or 132 or 133 or 134 or 139;

    public static string GetCrucibleItemTypeName(BeastmasterCrucibleItemType itemType)
        => itemType switch
        {
            BeastmasterCrucibleItemType.Recovery => "回復アイテム",
            BeastmasterCrucibleItemType.Fang => "各種の牙",
            BeastmasterCrucibleItemType.DodgeBook => "ブリンクの書",
            BeastmasterCrucibleItemType.ReflectBook => "リフレクの書",
            BeastmasterCrucibleItemType.TimeSand => "時の砂",
            BeastmasterCrucibleItemType.StrengthMedicine => "魔獣の剛力薬",
            BeastmasterCrucibleItemType.VampireFang => "吸血鬼の牙",
            BeastmasterCrucibleItemType.StarSand => "星の砂",
            BeastmasterCrucibleItemType.VampireMedicine => "魔獣の吸血薬",
            BeastmasterCrucibleItemType.RecoverySet => "ビーストポーションキット",
            BeastmasterCrucibleItemType.Specific => "特定アイテム指定",
            _ => itemType.ToString(),
        };

    public static string GetCrucibleItemTypeDescription(BeastmasterCrucibleItemType itemType)
        => itemType switch
        {
            BeastmasterCrucibleItemType.Recovery => "回復アイテムの優先度順に使用可能アイテムを選択し、自身のHPを回復します。",
            BeastmasterCrucibleItemType.Fang => "火、氷、水、雷、土、風の牙および星の砂の優先度順に、現在の敵視対象へ属性攻撃を行います。",
            BeastmasterCrucibleItemType.DodgeBook => "自身にブリンクの書を使用し、ブリンク効果を獲得します。",
            BeastmasterCrucibleItemType.ReflectBook => "自身にリフレクの書を使用し、リフレク効果を獲得します。",
            BeastmasterCrucibleItemType.TimeSand => "自身に時の砂を使用し、砂戻し効果を獲得します。",
            BeastmasterCrucibleItemType.StrengthMedicine => "自身に魔獣の剛力薬を使用し、攻撃強化効果を獲得します。",
            BeastmasterCrucibleItemType.VampireFang => "現在の敵視対象に吸血鬼の牙を使用し、ダメージを与えて自身のHPを回復します。",
            BeastmasterCrucibleItemType.StarSand => "現在の敵視対象に星の砂を使用し、ダメージを与えて火属性耐性低下を付与します。",
            BeastmasterCrucibleItemType.VampireMedicine => "自身に魔獣の吸血薬を使用し、吸血効果を獲得します。",
            BeastmasterCrucibleItemType.RecoverySet => "自身にビーストポーションキットを使用し、HP低下時の自動回復効果を獲得します。",
            BeastmasterCrucibleItemType.Specific => "全カタログからクルーシブルアイテムを1つ選択します。攻撃アイテムは現在の敵視対象、魔獣の金針と呼び戻しの笛は現在のターゲット、その他は自身に使用します。",
            _ => string.Empty,
        };

    public static string GetCrucibleItemName(uint itemId)
        => itemId switch
        {
            76 => "ビーストポーションG1",
            77 => "ビーストポーションG2",
            78 => "ビーストポーションG3",
            79 => "ビーストポーションG4",
            80 => "ビーストパウダーG1",
            81 => "ビーストパウダーG2",
            82 => "ビーストパウダーG3",
            83 => "魔獣の毒消し",
            84 => "魔獣の金針",
            85 => "魔獣の目薬",
            86 => "魔獣の抗毒薬G1",
            87 => "魔獣の抗毒薬G2",
            88 => "魔獣の抗麻痺薬G1",
            89 => "魔獣の抗麻痺薬G2",
            90 => "魔獣の抗暗闇薬G1",
            91 => "魔獣の抗暗闇薬G2",
            92 => "魔獣の抗石化薬G1",
            93 => "魔獣の抗石化薬G2",
            94 => "魔獣の抗睡眠薬G1",
            95 => "魔獣の抗睡眠薬G2",
            96 => "呼び戻しの笛",
            97 => "魔獣の煙玉",
            98 => "ビーストリレイザーG1",
            99 => "ビーストリレイザーG2",
            100 => "盗賊の目",
            101 => "商人の目",
            102 => "魔獣の硬皮薬",
            103 => "魔獣の短縮薬",
            104 => "魔獣の剛力薬",
            105 => "魔獣の剛力劇薬",
            106 => "魔獣の魔力薬",
            107 => "魔獣の魔力劇薬",
            108 => "魔獣の特攻薬",
            109 => "魔獣の特攻劇薬",
            110 => "魔獣の敏捷薬",
            111 => "魔獣の敏捷劇薬",
            112 => "魔獣の体力薬",
            113 => "魔獣の体力劇薬",
            114 => "魔獣の俊足薬",
            115 => "魔獣の羽根",
            116 => "魔獣の耐火薬G1",
            117 => "魔獣の耐火薬G2",
            118 => "魔獣の耐水薬G1",
            119 => "魔獣の耐水薬G2",
            120 => "魔獣の耐地薬G1",
            121 => "魔獣の耐地薬G2",
            122 => "魔獣の耐雷薬G1",
            123 => "魔獣の耐雷薬G2",
            124 => "魔獣の耐風薬G1",
            125 => "魔獣の耐風薬G2",
            126 => "魔獣の耐氷薬G1",
            127 => "魔獣の耐氷薬G2",
            128 => "火の牙",
            129 => "氷の牙",
            130 => "水の牙",
            131 => "雷の牙",
            132 => "土の牙",
            133 => "風の牙",
            134 => "吸血鬼の牙",
            135 => "魔獣の吸血薬",
            136 => "リフレクの書",
            137 => "ブリンクの書",
            138 => "時の砂",
            139 => "星の砂",
            140 => "ビーストポーションキット",
            141 => "ビーストレメディキット",
            142 => "スペルフォージの書",
            143 => "スチールスティングの書",
            _ => $"クルーシブルアイテム {itemId}",
        };

    public static string GetCrucibleItemDescription(uint itemId)
        => itemId switch
        {
            76 => "自身のHPを10%回復します。",
            77 => "自身のHPを23%回復します。",
            78 => "自身のHPを36%回復します。",
            79 => "自身のHPを50%回復します。",
            80 => "自身および周囲のパーティメンバーのHPを10%回復します。",
            81 => "自身および周囲のパーティメンバーのHPを25%回復します。",
            82 => "自身および周囲のパーティメンバーのHPを40%回復します。",
            83 => "毒を解除し、解除成功時に最大HPの25%を回復します。",
            84 => "現在の魔獣対象の石化を解除し、最大HPの50%を回復してストンスキンを付与します。",
            85 => "暗闇を解除し、解除成功時に最大HPの25%を回復します。",
            86 or 87 => "毒耐性を高めます。G2は自身および周囲のパーティメンバーに効果があります。",
            88 or 89 => "麻痺耐性を高めます。G2は自身および周囲のパーティメンバーに効果があります。",
            90 or 91 => "暗闇耐性を高めます。G2は自身および周囲のパーティメンバーに効果があります。",
            92 or 93 => "石化耐性を高めます。G2は自身および周囲のパーティメンバーに効果があります。",
            94 or 95 => "睡眠耐性を高めます。G2は自身および周囲のパーティメンバーに効果があります。",
            96 => "現在のターゲットで戦闘不能状態の魔獣を蘇生します。",
            97 => "戦闘開始前に戦闘を回避します。強敵やボスには無効です。",
            98 => "リレイズを付与し、戦闘不能時に70%の確率で自動蘇生します。",
            99 => "リレイズを付与し、戦闘不能時に95%の確率で自動蘇生します。",
            100 => "盗賊の目を付与し、次の戦闘での戦利品ドロップ率を3倍にします。",
            101 => "商人の目を付与し、次の戦闘でのビーストコイン獲得量を2倍にします。",
            102 => "魔獣の硬皮薬を付与し、被ダメージを20%軽減します。",
            103 => "魔獣の短縮薬を付与し、ウェポンスキルの詠唱・リキャスト時間、魔法の詠唱・リキャスト時間、オートアタック周期を15%短縮します。",
            104 => "魔獣の剛力薬を付与し、物理与ダメージを30%上昇させます。",
            105 => "自身にスタンを付与する代わりに、物理与ダメージを45%上昇させます。",
            106 => "魔獣の魔力薬を付与し、魔法与ダメージを30%上昇させます。",
            107 => "自身に悪夢を付与する代わりに、魔法与ダメージを50%上昇させます。",
            108 => "魔獣の特攻薬を付与し、クリティカル発動率を30%上昇させます。",
            109 => "自身に石化を付与する代わりに、クリティカル発動率を50%上昇させます。",
            110 => "魔獣の敏捷薬を付与し、回避率を25%上昇させます。",
            111 => "自身に暗闇を付与する代わりに、回避率を25%上昇させます。",
            112 => "HPを10%回復し、最大HPを20%上昇させます。",
            113 => "自身に猛毒を付与する代わりに、HPを10%回復し最大HPを30%上昇させます。",
            114 => "移動速度、回避率、毒耐性を上昇させます。",
            115 => "TPを100増加させます。高揚の指輪所持時は250増加します。",
            >= 116 and <= 127 => "対応する属性ダメージの無効化および吸収確率を上昇させます。G2は効果が高くなります。",
            >= 128 and <= 133 => "現在の敵視対象およびその周囲の敵に対応する属性の範囲魔法ダメージを与えます。",
            134 => "現在の敵視対象およびその周囲の敵に貫通物理ダメージを与え、最大HPに応じて威力が上昇し一部をHPとして吸収します。",
            135 => "吸血効果を付与し、与えたダメージの10%分のHPを回復します。",
            136 => "リフレクを付与し、特定の攻撃を除く魔法攻撃を反射します。",
            137 => "ブリンクを5スタック付与し、物理攻撃を無効化します。",
            138 => "砂戻しを付与し、戦闘不能時に戦闘開始時の状態に戻ります。",
            139 => "現在の敵視対象およびその周囲の敵にスターストームを放ち、火属性耐性を低下させます。",
            140 => "HPが50%を下回ったときに自動でHPを40%回復します。",
            141 => "オートレメディを付与し、次に受ける弱体効果を自動で解除します。",
            142 => "スペルフォージを付与し、自身と魔獣のすべての攻撃を魔法属性にします。",
            143 => "スチールスティングを付与し、自身と魔獣のすべての攻撃を物理属性にします。",
            _ => string.Empty,
        };

    public static readonly uint[] KnownCrucibleItemIds =
    [
        76, 77, 78, 79, 80, 81, 82, 83, 84, 85, 86, 87, 88, 89, 90, 91,
        92, 93, 94, 95, 96, 97, 98, 99, 100, 101, 102, 103, 104, 105, 106, 107,
        108, 109, 110, 111, 112, 113, 114, 115, 116, 117, 118, 119, 120, 121, 122, 123,
        124, 125, 126, 127, 128, 129, 130, 131, 132, 133, 134, 135, 136, 137, 138, 139,
        140, 141, 142, 143,
    ];
}
