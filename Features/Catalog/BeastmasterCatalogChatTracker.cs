using System.Text.RegularExpressions;

namespace Beastmaster;

public sealed partial class BeastmasterCatalogChatTracker : IDisposable
{
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
        var entry = BeastmasterCatalog.Entries.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, capturedName, StringComparison.Ordinal));
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

    [GeneratedRegex(@"(?:(?<name>.+?)と(?:心を通わせた|契約した|仲良くなった)|(?<name>.+?)を(?:獣図鑑に記録した|手なずけた))[！!]", RegexOptions.CultureInvariant)]
    private static partial Regex CaptureMessageRegex();
}
