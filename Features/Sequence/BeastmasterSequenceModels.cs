using System.Text;

namespace Beastmaster;

[Serializable]
public sealed class BeastmasterSequenceDefinition
{
    private static readonly HashSet<uint> SupportedActionIds =
    [
        44879, 44881, 44883, 44885, 44886, 44890, 44891, 44892, 44893, 44894,
        44895, 44896, 44897, 44898, 44899, 44900, 44901, 44902, 44903, 44904, 44905,
    ];

    public string Name { get; set; } = "水棲・虫PT開幕テンプレート";
    public string Description { get; set; } = "水棲・虫PT開幕：三号呼笛→かりる→百獣の皮";
    public List<BeastmasterSequenceStep> CountdownSteps { get; set; } = [];
    public List<BeastmasterSequenceStep> CombatSteps { get; set; } = [];

    public bool TryValidate(out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(Name))
        {
            error = "シーケンス名を入力してください。";
            return false;
        }

        if (CountdownSteps.Count == 0 || CombatSteps.Count == 0)
        {
            error = "カウントダウン段階と戦闘段階の両方に少なくとも1つのステップが必要です。";
            return false;
        }

        if (CountdownSteps.Count > 100 || CombatSteps.Count > 100)
        {
            error = "各段階のステップ数は最大 100 個までです。";
            return false;
        }

        var previousTime = float.PositiveInfinity;
        foreach (var step in CountdownSteps)
        {
            var time = step.TimeSeconds ?? float.NaN;
            if (!float.IsFinite(time) || time > 0f || time < -60f || Math.Abs(time) >= previousTime)
            {
                error = "カウントダウンステップは T-60 から T-0 の範囲で時間の降順に並べ、時間が重複しないようにしてください。";
                return false;
            }

            previousTime = Math.Abs(time);
            if (!SupportedActionIds.Contains(step.ActionId))
            {
                error = $"カウントダウン段階に未対応のアクションが含まれています（{step.ActionId}）。";
                return false;
            }
        }

        foreach (var step in CombatSteps)
        {
            if (!SupportedActionIds.Contains(step.ActionId))
            {
                error = $"戦闘段階に未対応のアクションが含まれています（{step.ActionId}）。";
                return false;
            }
        }

        return true;
    }

    public static BeastmasterSequenceDefinition CreateWaterOpener()
        => new()
        {
            CountdownSteps =
            [
                new(-9, 44894, "三号呼び笛"),
                new(-5, 44895, "かりる"),
                new(-4, 44881, "一号呼び笛"),
                new(-2, 44896, "ビーストスキン"),
                new(0, 44893, "シールドチャージ"),
            ],
            CombatSteps =
            [
                new(null, 44879, "スマッシュ"),
                new(null, 44905, "きあい"),
                new(null, 44890, "はなつ"),
                new(null, 44883, "アクスバイト"),
                new(null, 44904, "おうえん"),
                new(null, 44891, "最後の一撃"),
                new(null, 44885, "シールドスプリッター"),
                new(null, 44892, "二号呼び笛"),
                new(null, 44890, "はなつ"),
                new(null, 44894, "三号呼び笛"),
            ],
        };

    public static BeastmasterSequenceDefinition CreateTestSequence()
        => new()
        {
            Name = "テストシーケンス",
            Description = "カウントダウン、呼笛確認、T-0対象アクション、戦闘突入後の動作検証用。",
            CountdownSteps =
            [
                new(-3, 44881, "一号呼笛"),
                new(0, 44893, "シールドチャージ"),
            ],
            CombatSteps =
            [
                new(null, 44879, "砕き割り"),
            ],
        };

    public string Export()
    {
        var builder = new StringBuilder()
            .AppendLine("BSTSEQ|1")
            .AppendLine($"名前|{Name}")
            .AppendLine($"説明|{Description}")
            .AppendLine()
            .AppendLine("[カウントダウン]");
        foreach (var step in CountdownSteps)
        {
            builder.AppendLine($"{step.TimeSeconds:0.###}|{step.ActionId}|{step.Label}");
        }

        builder.AppendLine().AppendLine("[戦闘]");
        foreach (var step in CombatSteps)
        {
            builder.AppendLine($"{step.ActionId}|{step.Label}");
        }

        return builder.ToString().TrimEnd();
    }

    public static bool TryImport(string text, out BeastmasterSequenceDefinition? sequence, out string error)
    {
        sequence = null;
        error = string.Empty;
        var lines = text.Replace("\r", string.Empty).Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "BSTSEQ|1")
        {
            error = "1行目は BSTSEQ|1 である必要があります。";
            return false;
        }

        var result = new BeastmasterSequenceDefinition();
        var section = string.Empty;
        for (var index = 1; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0) continue;
            if (line is "[カウントダウン]" or "[戦闘]")
            {
                section = line;
                continue;
            }
            if (line.StartsWith("名前|", StringComparison.Ordinal))
            {
                var idx = line.IndexOf('|');
                result.Name = line[(idx + 1)..];
                continue;
            }
            if (line.StartsWith("説明|", StringComparison.Ordinal))
            {
                var idx = line.IndexOf('|');
                result.Description = line[(idx + 1)..];
                continue;
            }

            var parts = line.Split('|');
            if (section == "[カウントダウン]" && parts.Length >= 3
                && float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var time)
                && uint.TryParse(parts[1], out var countdownAction))
            {
                result.CountdownSteps.Add(new(time, countdownAction, parts[2]));
                continue;
            }
            if (section == "[戦闘]" && parts.Length >= 2 && uint.TryParse(parts[0], out var combatAction))
            {
                result.CombatSteps.Add(new(null, combatAction, parts[1]));
                continue;
            }

            error = $"第 {index + 1} 行のフォーマットが不正です。";
            return false;
        }

        if (result.CountdownSteps.Count == 0 && result.CombatSteps.Count == 0)
        {
            error = "シーケンスにステップが含まれていません。";
            return false;
        }

        if (!result.TryValidate(out error))
        {
            return false;
        }

        sequence = result;
        return true;
    }
}

[Serializable]
public sealed class BeastmasterSequenceStep
{
    public BeastmasterSequenceStep() { }

    public BeastmasterSequenceStep(float? timeSeconds, uint actionId, string label)
    {
        TimeSeconds = timeSeconds;
        ActionId = actionId;
        Label = label;
    }

    public float? TimeSeconds { get; set; }
    public uint ActionId { get; set; }
    public string Label { get; set; } = string.Empty;
}
