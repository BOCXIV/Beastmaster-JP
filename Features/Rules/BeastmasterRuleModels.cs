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
}

public enum BeastmasterRuleStatusCondition
{
    Present,
    Missing,
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
    public BeastmasterRuleActionType ActionType { get; set; } = BeastmasterRuleActionType.Skill;
    public BeastmasterCrucibleItemType CrucibleItemType { get; set; } = BeastmasterCrucibleItemType.Recovery;
    public uint DataId { get; set; }
    public uint ConditionId { get; set; }
    public uint ActionId { get; set; } = 44879;
    public uint CrucibleItemId { get; set; }

    public bool TryValidate(out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(Name))
        {
            error = "ルール名を入力してください。";
            return false;
        }

        if (!Enum.IsDefined(ConditionType) || !Enum.IsDefined(StatusCondition))
        {
            error = "ルールの判定タイプが無効です。";
            return false;
        }

        if (ConditionType != BeastmasterRuleConditionType.TargetDataId && ConditionId == 0)
        {
            error = IsStatusRule ? "バフIDは 0 より大きい必要があります。" : "詠唱IDは 0 より大きい必要があります。";
            return false;
        }

        if (RequiresDataId && DataId == 0)
        {
            error = "DataIDは 0 より大きい必要があります。";
            return false;
        }

        if (ActionType == BeastmasterRuleActionType.Skill && !BeastmasterRuleActions.IsSupported(ActionId))
        {
            error = $"未対応のルールアクションです（{ActionId}）。";
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
                    Name = "最終バースト-1層",
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
            builder.AppendLine()
                .AppendLine("[ルール]")
                .AppendLine($"名前|{Sanitize(rule.Name)}")
                .AppendLine($"有効|{rule.Enabled}")
                .AppendLine($"判定|{rule.ConditionType}")
                .AppendLine($"条件|{rule.StatusCondition}")
                .AppendLine($"実行|{rule.ActionType}")
                .AppendLine($"クルーシブルアイテム種別|{rule.CrucibleItemType}")
                .AppendLine($"DataId|{rule.DataId}")
                .AppendLine($"判定ID|{rule.ConditionId}")
                .AppendLine($"アクション|{rule.ActionId}")
                .AppendLine($"クルーシブルアイテム|{rule.CrucibleItemId}");
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
            if (line is "[ルール]" or "[规则]")
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

        switch (key)
        {
            case "名前": rule.Name = value; return true;
            case "有効" when bool.TryParse(value, out var enabled): rule.Enabled = enabled; return true;
            case "判定" when Enum.TryParse<BeastmasterRuleConditionType>(value, out var condition): rule.ConditionType = condition; return true;
            case "条件" when Enum.TryParse<BeastmasterRuleStatusCondition>(value, out var status): rule.StatusCondition = status; return true;
            case "実行" when Enum.TryParse<BeastmasterRuleActionType>(value, out var actionType): rule.ActionType = actionType; return true;
            case "クルーシブルアイテム種別" when Enum.TryParse<BeastmasterCrucibleItemType>(value, out var itemType): rule.CrucibleItemType = itemType; return true;
            case "DataId" when uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var dataId): rule.DataId = dataId; return true;
            case "判定ID" when uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var conditionId): rule.ConditionId = conditionId; return true;
            case "アクション" when uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var actionId): rule.ActionId = actionId; return true;
            case "クルーシブルアイテム" when uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var itemId): rule.CrucibleItemId = itemId; return true;
            default: return false;
        }
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
            44891 => "最後の一撃",
            44892 => "二号呼び笛",
            44893 => "シールドチャージ",
            44894 => "三号呼び笛",
            44895 => "かりる",
            44896 => "ビーストスキン",
            44897 => "ヴァイルスキン",
            44898 => "クラウドスキム",
            44899 => "シードサワー",
            44900 => "クェリングウェーブ",
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
            or 44890 or 44891 or 44893 or 44930 or 44931 or 44932 or 44933 or 47093;

    public static bool IsCrucibleItemId(uint itemId)
        => itemId is >= 76 and <= 143;

    public static bool IsCrucibleItemFriendly(uint itemId)
        => itemId is 76 or 77 or 78;

    public static string GetCrucibleItemTypeName(BeastmasterCrucibleItemType itemType)
        => itemType == BeastmasterCrucibleItemType.Recovery ? "魔獣回復薬" : "各種牙";

    public static string GetCrucibleItemName(uint itemId)
        => itemId switch
        {
            76 => "1級魔獣回復薬",
            77 => "2級魔獣回復薬",
            78 => "3級魔獣回復薬",
            128 => "火の牙",
            129 => "氷の牙",
            131 => "雷の牙",
            133 => "風の牙",
            138 => "時の砂",
            _ => $"クルーシブルアイテム {itemId}",
        };

    public static readonly uint[] KnownCrucibleItemIds =
    [
        76, 77, 78,
        128, 129, 131, 133,
        138,
    ];
}
