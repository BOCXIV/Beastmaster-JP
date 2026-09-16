using System.Numerics;

namespace Beastmaster;

public sealed record BeastmasterQuestDefinition(uint RowId, string Name, string Summary);

public sealed record BeastmasterQuestTarget(
    uint QuestRowId,
    byte Sequence,
    uint TerritoryType,
    uint MapRowId,
    Vector3 Position,
    string Zone,
    string Name);

public static class BeastmasterQuestGuide
{
    public static IReadOnlyList<BeastmasterQuestDefinition> Quests { get; } =
    [
        new(71026, "魔獣の使い手たち", "グリダニア：新街で受注する魔獣使いの開放クエスト。"),
        new(71027, "初めての相棒", "魔獣使いのジョブクエスト。"),
        new(71028, "魔獣と心通わせて", "魔獣使いのジョブクエスト。"),
        new(71029, "ルー派の使役術", "魔獣使いのジョブクエスト。"),
        new(71030, "盤上の戦い、闘獣練", "魔獣使いのジョブクエスト。"),
        new(71031, "姉弟子と勉強ゴブ！", "魔獣使いのジョブクエスト。"),
        new(71032, "同門対決、第二盤", "魔獣使いのジョブクエスト。"),
        new(71033, "贖罪の魔獣使い", "魔獣使いのジョブクエスト。"),
        new(71034, "貪食のガトラー", "魔獣使いのジョブクエスト。"),
        new(71035, "人と獣が結ぶ絆", "魔獣使いのジョブクエスト。"),
        new(71036, "猛者の試練、特一盤", "魔獣使いのジョブクエスト。"),
        new(71037, "盤上の王者、特二盤", "魔獣使いのジョブクエスト。"),
        new(71045, "極めるは獣の道", "魔獣使いのジョブクエスト。"),
    ];

    public static IReadOnlyList<BeastmasterQuestTarget> Targets { get; } =
    [
        new(
            71026,
            1,
            148,
            4,
            new Vector3(-318.654f, 60.947f, -129.382f),
            "黒衣森：中央森林",
            "クエスト第1目標"),
    ];
}
