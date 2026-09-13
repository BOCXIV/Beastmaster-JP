using System.Text.RegularExpressions;

namespace Beastmaster;

public sealed partial class BeastmasterCatalogChatTracker : IDisposable
{
    private static readonly IReadOnlyDictionary<string, string> CaptureNameAliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["羊羔"] = "ロスト・ラム",
            ["迷途羊羔"] = "ロスト・ラム",
            ["幽灵"] = "スペクター",
            ["妖魂"] = "スペクター",
            ["库西"] = "クーシー",
            ["松鼠"] = "スクウィレル",
            ["陆鱼"] = "クラウドフィッシュ",
            ["奥猴"] = "オポオポ",
            ["渡渡鸟"] = "ドードー",
            ["矿爬虫"] = "コブラン",
            ["凶蛛蝎"] = "ダイアマイト",
            ["巨型陆蟹"] = "メガロクラブ",
            ["胡蜂"] = "ホーネット",
            ["兀鹫"] = "ヴァルチャー",
            ["蔓德拉"] = "マンドラゴラ",
            ["死魂"] = "ウィスプ",
            ["跳蜥"] = "アガマ",
            ["壳蟹"] = "スニッパー",
            ["螳螂"] = "マンティス",
            ["粘液怪"] = "フラン",
            ["无头骑士"] = "デュラハン",
            ["蝙蝠"] = "バット",
            ["陷阱草"] = "スウォーム",
            ["席兹"] = "シズ",
            ["仙人刺"] = "サボテンダー",
            ["巨像"] = "クレイゴーレム",
            ["碧企鹅"] = "アプカル",
            ["精金龟"] = "アダマンタス",
            ["大水牛"] = "バッファロー",
            ["乌菊石"] = "アンモナイト",
            ["巨虫"] = "サンドウォーム",
            ["魔石精"] = "スプリガン",
            ["古菩猩猩"] = "グゥーブー",
            ["巨蟾蜍"] = "ギガントード",
            ["蜂鸟"] = "ハミングバード",
            ["长须豹"] = "クァール",
            ["盗龙"] = "ラプトル",
            ["烈阳火蛟"] = "サラマンダー",
            ["树精"] = "トレント",
            ["灵蚁"] = "アントリング",
            ["奇美拉"] = "キマイラ",
            ["魔界花"] = "モルボル",
            ["蝾螈"] = "ニューツ",
            ["眼镜蛇"] = "コブラ",
            ["海德拉"] = "ハイドラ",
            ["灯心蜻蛉"] = "ダンシング・コテージ",
            ["腐坏古菩猩猩"] = "ディケイ・グゥーブー",
            ["祖"] = "ズー",
            ["寒冰巨像"] = "アイスゴーレム",
            ["真红龙虾"] = "クリムゾン・ロブスター",
            ["大王花"] = "ラフレシア",
            ["贝希摩斯"] = "ベヒーモス",
        };

    private readonly BeastmasterConfiguration configuration;
    private readonly BeastmasterProgressService progressService;

    public BeastmasterCatalogChatTracker(BeastmasterConfiguration configuration, BeastmasterProgressService progressService)
    {
        this.configuration = configuration;
        this.progressService = progressService;
        DalamudApi.ChatGui.ChatMessage += OnChatMessage;
    }

    public void Dispose()
    {
        DalamudApi.ChatGui.ChatMessage -= OnChatMessage;
    }

    private void OnChatMessage(object message)
    {
        if (!configuration.AutoCompleteCatalogFromChat)
        {
            return;
        }

        var text = ExtractChatMessageText(message);
        var match = CaptureMessageRegex().Match(text);
        if (!match.Success)
        {
            return;
        }

        var capturedName = match.Groups["name"].Value.Trim();
        var catalogName = CaptureNameAliases.GetValueOrDefault(capturedName, capturedName);
        var entry = BeastmasterCatalog.Entries.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, catalogName, StringComparison.Ordinal));
        if (entry == null || progressService.IsCompleted(entry.Key))
        {
            return;
        }

        progressService.SetCompleted(entry.Key, true);
        DalamudApi.Log.Information(
            "Auto-marked Beastmaster catalog entry {Number} ({Name}) from capture message.",
            entry.Number,
            entry.Name);
    }

    private static string ExtractChatMessageText(object message)
    {
        try
        {
            var messageProperty = message.GetType().GetProperty("Message");
            var value = messageProperty?.GetValue(message);
            var textValueProperty = value?.GetType().GetProperty("TextValue");
            return textValueProperty?.GetValue(value) as string
                   ?? value?.ToString()
                   ?? message.ToString()
                   ?? string.Empty;
        }
        catch
        {
            return message.ToString() ?? string.Empty;
        }
    }

    [GeneratedRegex(@"(?:成功结识了(?<name>.+?)种的魔兽|(?<name>.+?)と(?:心を通わせた|契約した|仲良くなった))[！!]", RegexOptions.CultureInvariant)]
    private static partial Regex CaptureMessageRegex();
}
