namespace Beastmaster;

public sealed record BeastmasterAchievementGroup(string Key, string Name, int[] AchievementIds);

public static class BeastmasterAchievementCatalog
{
    public static IReadOnlyList<BeastmasterAchievementGroup> Groups { get; } =
    [
        new("rate", "ビーストマニア", [4028, 4029, 4030, 4031, 4032]),
        new("clear", "盤面制覇", [4033, 4034, 4035]),
        new("high-clear", "特盤制覇", [4036, 4037]),
        new("beast-iii", "盤面完全制覇", [4038, 4039, 4040]),
        new("high-beast-iii", "特盤完全制覇", [4041, 4042]),
        new("rating", "盤面勝利評価", [4043, 4044, 4045, 4046, 4047, 4048]),
        new("high-rating", "特盤勝利評価", [4049, 4050, 4051, 4052]),
        new("overall", "闘獣練総合", [4053, 4054, 4055]),
        new("training", "ビーストトレーナー", [4056, 4057, 4058, 4059, 4060]),
        new("high-reward", "種族統覇", [4061, 4062, 4063, 4064, 4065, 4066, 4067, 4068]),
        new("ranking", "ランキング", [4075, 4076, 4077]),
    ];

    public static int AchievementCount => Groups.Sum(group => group.AchievementIds.Length);
}

