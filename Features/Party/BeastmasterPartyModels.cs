namespace Beastmaster;

[Serializable]
public sealed class BeastmasterPartyPreset
{
    private const string FormatHeader = "BSTPARTY|1";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "10スロット編成";
    public int SlotCount { get; set; } = 10;
    public List<int> Members { get; set; } = [];

    public BeastmasterPartyPreset Clone()
        => new()
        {
            Name = Name + " (コピー)",
            SlotCount = SlotCount,
            Members = [.. Members],
        };

    public bool TryValidate(out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(Name))
        {
            error = "プリセット名を入力してください。";
            return false;
        }

        if (SlotCount is not (10 or 12 or 14 or 15))
        {
            error = "プリセットのスロット数は 10、12、14、15 のいずれかである必要があります。";
            return false;
        }

        if (Members.Count > SlotCount)
        {
            error = $"現在のプリセットには最大 {SlotCount} 体の魔獣を配置できます。";
            return false;
        }

        if (Members.Any(number => number is < 1 or > 50))
        {
            error = "魔獣図鑑番号は 1〜50 の範囲で指定してください。";
            return false;
        }

        if (Members.Distinct().Count() != Members.Count)
        {
            error = "同一の魔獣をプリセット内で重複して設定することはできません。";
            return false;
        }

        return true;
    }

    public string Export()
        => string.Join(Environment.NewLine,
            FormatHeader,
            $"名前|{Name.Replace('\r', ' ').Replace('\n', ' ').Replace('|', ' ')}",
            $"スロット|{SlotCount}",
            $"メンバー|{string.Join(',', Members)}");

    public static bool TryImport(string text, out BeastmasterPartyPreset? preset, out string error)
    {
        preset = null;
        error = "編成プリセットの内容が無効です。";
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4096)
        {
            return false;
        }

        var lines = text.Replace("\r", string.Empty).Split('\n');
        if (lines.Length < 4 || lines[0].Trim() != FormatHeader)
        {
            error = $"1行目は {FormatHeader} である必要があります。";
            return false;
        }

        var result = new BeastmasterPartyPreset();
        foreach (var line in lines.Skip(1).Where(line => !string.IsNullOrWhiteSpace(line)))
        {
            var separator = line.IndexOf('|');
            if (separator <= 0)
            {
                return false;
            }

            var key = line[..separator];
            var value = line[(separator + 1)..];
            switch (key)
            {
                case "名前":
                    result.Name = value;
                    break;
                case "スロット" when int.TryParse(value, out var slotCount):
                case "枠数" when int.TryParse(value, out slotCount):
                    result.SlotCount = slotCount;
                    break;
                case "メンバー":
                    result.Members.Clear();
                    if (value.Length > 0)
                    {
                        foreach (var member in value.Split(','))
                        {
                            if (!int.TryParse(member, out var number)) return false;
                            result.Members.Add(number);
                        }
                    }
                    break;
                default:
                    return false;
            }
        }

        if (!result.TryValidate(out error)) return false;
        result.Id = Guid.NewGuid().ToString("N");
        preset = result;
        return true;
    }
}

public sealed record BeastmasterPetPartyMember(int Position, int CatalogNumber, string Name);

public sealed record BeastmasterPetPartySnapshot(
    bool Available,
    int MemberCount,
    int Capacity,
    IReadOnlyList<BeastmasterPetPartyMember> Members,
    string Reason)
{
    public static readonly int[] SupportedCapacities = [10, 12, 14, 15];

    public static BeastmasterPetPartySnapshot Unavailable(string reason)
        => new(false, 0, 0, [], reason);
}

