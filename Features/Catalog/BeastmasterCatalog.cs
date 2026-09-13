namespace Beastmaster;

public enum BeastmasterCatalogLocationType
{
    Starting,
    Field,
    Duty,
    Unknown,
}

public enum BeastmasterAttribute
{
    Unknown,
    猛,
    堅,
    魔,
    翔,
    坚 = 堅,
}

public sealed record BeastmasterCatalogEntry(
    int Number,
    string Name,
    BeastmasterCatalogLocationType LocationType,
    string Location,
    ushort TerritoryType = 0,
    float? MapX = null,
    float? MapY = null,
    string Level = "",
    ushort MapRowId = 0,
    float? WorldX = null,
    float? WorldY = null,
    float? WorldZ = null,
    uint ContentFinderConditionId = 0)
{
    public string Key => $"catalog-{Number:00}";

    public BeastmasterAttribute Attribute => BeastmasterCatalog.GetAttribute(Number);

    public uint UltimateActionId => (uint)(44933 + Number * 2);

    public uint ReleaseActionId => UltimateActionId + 1;

    public uint SummonDataId => (uint)(18915 + Number);

    public BeastmasterSkillProfile SkillProfile
        => new(Number, Name, SummonDataId, Attribute, UltimateActionId, ReleaseActionId);
}

public static class BeastmasterCatalog
{
    private static readonly BeastmasterAttribute[] Attributes =
    [
        BeastmasterAttribute.猛, BeastmasterAttribute.猛, BeastmasterAttribute.猛, BeastmasterAttribute.堅, BeastmasterAttribute.猛,
        BeastmasterAttribute.魔, BeastmasterAttribute.魔, BeastmasterAttribute.猛, BeastmasterAttribute.堅, BeastmasterAttribute.翔,
        BeastmasterAttribute.翔, BeastmasterAttribute.猛, BeastmasterAttribute.魔, BeastmasterAttribute.猛, BeastmasterAttribute.堅,
        BeastmasterAttribute.堅, BeastmasterAttribute.魔, BeastmasterAttribute.堅, BeastmasterAttribute.翔, BeastmasterAttribute.翔,
        BeastmasterAttribute.堅, BeastmasterAttribute.猛, BeastmasterAttribute.魔, BeastmasterAttribute.堅, BeastmasterAttribute.魔,
        BeastmasterAttribute.猛, BeastmasterAttribute.堅, BeastmasterAttribute.魔, BeastmasterAttribute.猛, BeastmasterAttribute.猛,
        BeastmasterAttribute.魔, BeastmasterAttribute.翔, BeastmasterAttribute.魔, BeastmasterAttribute.堅, BeastmasterAttribute.猛,
        BeastmasterAttribute.魔, BeastmasterAttribute.猛, BeastmasterAttribute.猛, BeastmasterAttribute.猛, BeastmasterAttribute.翔,
        BeastmasterAttribute.堅, BeastmasterAttribute.堅, BeastmasterAttribute.堅, BeastmasterAttribute.翔, BeastmasterAttribute.魔,
        BeastmasterAttribute.翔, BeastmasterAttribute.堅, BeastmasterAttribute.堅, BeastmasterAttribute.魔, BeastmasterAttribute.魔,
    ];

    public static IReadOnlyList<BeastmasterCatalogEntry> Entries { get; } =
    [
        new(1, "クーシー", BeastmasterCatalogLocationType.Starting, "初期習得", 0, null, null, ""),
        new(2, "スクウィレル", BeastmasterCatalogLocationType.Field, "黒衣森：中央森林", 148, 23.1f, 17f, "1~2", 4),
        new(3, "ロスト・ラム", BeastmasterCatalogLocationType.Field, "中央ラノシア", 134, 23.8f, 25.6f, "3~4", 15),
        new(4, "クラウドフィッシュ", BeastmasterCatalogLocationType.Field, "中央ラノシア", 134, 22f, 22f, "4~6", 15),
        new(5, "オポオポ", BeastmasterCatalogLocationType.Field, "黒衣森：北部森林", 154, 28.5f, 24.3f, "5~9", 7),
        new(6, "ドードー", BeastmasterCatalogLocationType.Field, "低地ラノシア", 135, 30.9f, 18.2f, "4~9", 16),
        new(7, "コブラン", BeastmasterCatalogLocationType.Field, "西ザナラーン", 140, 20.3f, 28.6f, "6~8", 20),
        new(8, "ダイアマイト", BeastmasterCatalogLocationType.Field, "黒衣森：中央森林", 148, 19f, 19f, "10", 4),
        new(9, "メガロクラブ", BeastmasterCatalogLocationType.Field, "中央ラノシア", 134, 15.3f, 14.3f, "10~13", 15),
        new(10, "ホーネット", BeastmasterCatalogLocationType.Field, "中央ラノシア", 134, 16f, 12.5f, "10~13", 15),
        new(11, "ヴァルチャー", BeastmasterCatalogLocationType.Field, "西ザナラーン", 140, 21f, 26f, "6", 20),
        new(12, "マンドラゴラ", BeastmasterCatalogLocationType.Field, "中央ラノシア", 134, 21.4f, 16.3f, "5~7", 15),
        new(13, "ウィスプ", BeastmasterCatalogLocationType.Field, "黒衣森：中央森林", 148, 18.5f, 28.3f, "14", 4),
        new(14, "アガマ", BeastmasterCatalogLocationType.Field, "中央ラノシア", 134, 20.5f, 18.5f, "4~8", 15),
        new(15, "スニッパー", BeastmasterCatalogLocationType.Field, "西ザナラーン", 140, 16.5f, 16.5f, "13", 20),
        new(16, "マンティス", BeastmasterCatalogLocationType.Field, "西ラノシア", 138, 21f, 23f, "16", 18, -11.055f, -22.468f, 50.278f),
        new(17, "フラン", BeastmasterCatalogLocationType.Duty, "封鎖坑道 カッパーベル銅山", 0, null, null, "17", 0, null, null, null, 3),
        new(18, "デュラハン", BeastmasterCatalogLocationType.Duty, "魔獣領域 ハラタリ修練所", 0, null, null, "20", 0, null, null, null, 7),
        new(19, "バット", BeastmasterCatalogLocationType.Field, "低地ラノシア", 135, 26.5f, 15.9f, "7", 16),
        new(20, "スウォーム", BeastmasterCatalogLocationType.Field, "黒衣森：中央森林", 148, 23f, 26f, "10", 4),
        new(21, "シズ", BeastmasterCatalogLocationType.Field, "西ラノシア", 138, 24.1f, 23.6f, "16", 18, 105.702f, -16.159f, 164.859f),
        new(22, "サボテンダー", BeastmasterCatalogLocationType.Field, "西ザナラーン", 140, 27f, 25f, "3~4", 20),
        new(23, "クレイゴーレム", BeastmasterCatalogLocationType.Field, "南ザナラーン", 146, 24f, 12.3f, "29", 25),
        new(24, "アプカル", BeastmasterCatalogLocationType.Field, "東ラノシア", 137, 28.8f, 36.7f, "30", 17),
        new(25, "アダマンタス", BeastmasterCatalogLocationType.Field, "中央ザナラーン", 141, 22f, 30f, "12", 21, -101.513f, 5.07f, 238.304f),
        new(26, "バッファロー", BeastmasterCatalogLocationType.Field, "中央ラノシア", 134, 18.5f, 17.5f, "8", 15),
        new(27, "アンモナイト", BeastmasterCatalogLocationType.Field, "西ザナラーン", 140, 16.8f, 14.5f, "14", 20),
        new(28, "サンドウォーム", BeastmasterCatalogLocationType.Field, "南ザナラーン", 146, 15.2f, 37.5f, "31", 25),
        new(29, "スプリガン", BeastmasterCatalogLocationType.Field, "中央ザナラーン", 141, 17.4f, 23.7f, "7", 21),
        new(30, "グゥーブー", BeastmasterCatalogLocationType.Field, "低地ラノシア", 135, 25.2f, 24.5f, "12", 16),
        new(31, "ギガントード", BeastmasterCatalogLocationType.Field, "低地ラノシア", 135, 24.6f, 23f, "4", 16),
        new(32, "ハミングバード", BeastmasterCatalogLocationType.Field, "東ラノシア", 137, 30.6f, 24f, "33", 17),
        new(33, "クァール", BeastmasterCatalogLocationType.Field, "外地ラノシア", 180, 15.3f, 14.7f, "34", 30, -359.599f, 60.528f, -348.505f),
        new(34, "ラプトル", BeastmasterCatalogLocationType.Field, "黒衣森：中央森林", 148, 31.2f, 20.3f, "9", 4),
        new(35, "サラマンダー", BeastmasterCatalogLocationType.Field, "南ザナラーン", 146, 25f, 39f, "32", 25),
        new(36, "トレント", BeastmasterCatalogLocationType.Field, "黒衣森：中央森林", 148, 28.7f, 19.3f, "12~17", 4, 332.173f, -1.287f, -344.14f),
        new(37, "アントリング", BeastmasterCatalogLocationType.Duty, "流砂迷宮 カッターズクライ", 0, null, null, "38", 0, null, null, null, 12),
        new(38, "キマイラ", BeastmasterCatalogLocationType.Duty, "流砂迷宮 カッターズクライ", 0, null, null, "38", 0, null, null, null, 12),
        new(39, "モルボル", BeastmasterCatalogLocationType.Field, "黒衣森：中央森林", 148, 13.5f, 22.3f, "31", 4, -427.656f, 49f, 33.517f),
        new(40, "スペクター", BeastmasterCatalogLocationType.Field, "中央ラノシア", 134, 20.3f, 19.6f, "7", 15, -57.661f, 34.287f, -84.685f),
        new(41, "ニューツ", BeastmasterCatalogLocationType.Field, "黒衣森：中央森林", 148, 26.5f, 18.9f, "6", 4),
        new(42, "コブラ", BeastmasterCatalogLocationType.Field, "モードゥナ", 156, 26.3f, 12.9f, "45", 14),
        new(43, "ハイドラ", BeastmasterCatalogLocationType.Duty, "ハイドラ討伐戦", 0, null, null, "50", 0, null, null, null, 75),
        new(44, "ダンシング・コテージ", BeastmasterCatalogLocationType.Duty, "腐敗遺跡 古アムダプール市街", 0, null, null, "50", 0, null, null, null, 22),
        new(45, "ディケイ・グゥーブー", BeastmasterCatalogLocationType.Duty, "腐敗遺跡 古アムダプール市街", 0, null, null, "50", 0, null, null, null, 22),
        new(46, "ズー", BeastmasterCatalogLocationType.Duty, "怪鳥巨塔 シリウス大灯台", 0, null, null, "50", 0, null, null, null, 17),
        new(47, "アイスゴーレム", BeastmasterCatalogLocationType.Duty, "氷結潜窟 スノークローク大氷壁", 0, null, null, "50", 0, null, null, null, 27),
        new(48, "クリムゾン・ロブスター", BeastmasterCatalogLocationType.Duty, "逆転要害 サスタシャ浸食洞 (Hard)", 0, null, null, "50", 0, null, null, null, 28),
        new(49, "ラフレシア", BeastmasterCatalogLocationType.Duty, "大迷宮バハムート：侵攻編1", 0, null, null, "50", 0, null, null, null, 98),
        new(50, "ベヒーモス", BeastmasterCatalogLocationType.Duty, "古代の民の迷宮", 0, null, null, "50", 0, null, null, null, 92),
    ];

    public static BeastmasterAttribute GetAttribute(int number)
        => number is >= 1 and <= 50 ? Attributes[number - 1] : BeastmasterAttribute.Unknown;
}
