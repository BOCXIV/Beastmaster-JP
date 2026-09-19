using Dalamud.Game.Command;
using Dalamud.Plugin;

namespace Beastmaster;

public sealed class BeastmasterPlugin : IDalamudPlugin
{
    private const string CommandName = "/beastmaster";
    private const string ChineseCommandName = "/\u9A6F\u517D\u5E08";
    private const string JapaneseCommandName = "/魔獣使い";
    private readonly PluginUI ui;
    private readonly BeastmasterNavigationService navigationService;
    private readonly BeastmasterCatalogChatTracker catalogChatTracker;
    private readonly BeastmasterAutoCaptureService autoCaptureService;
    private readonly BeastmasterCatalogSyncService catalogSyncService;
    private readonly BeastmasterAchievementSyncService achievementSyncService;
    private readonly BeastmasterResultProgressService resultProgressService;
    private readonly BeastmasterNotebookProgressService notebookProgressService;
    private readonly BeastmasterNotebookSyncService notebookSyncService;
    private readonly BeastmasterPetPartyService petPartyService;
    private readonly BeastmasterCountdownService countdownService;
    private readonly BeastmasterSequenceService sequenceService;
    private readonly BeastmasterRuleService ruleService;

    public string Name => "Beastmaster";

    public BeastmasterConfiguration Configuration { get; }

    public BeastmasterPlugin(IDalamudPluginInterface pluginInterface)
    {
        DalamudApi.Initialize(pluginInterface);

        Configuration = pluginInterface.GetPluginConfig() as BeastmasterConfiguration
            ?? new BeastmasterConfiguration();
        Configuration.Initialize(pluginInterface);
        var progressService = new BeastmasterProgressService(Configuration);
        petPartyService = new BeastmasterPetPartyService();
        petPartyService.Start();
        notebookProgressService = new BeastmasterNotebookProgressService(progressService);
        notebookProgressService.Start();
        notebookSyncService = new BeastmasterNotebookSyncService(progressService);
        notebookSyncService.Start();
        resultProgressService = new BeastmasterResultProgressService(progressService);
        resultProgressService.Start();
        catalogSyncService = new BeastmasterCatalogSyncService(progressService);
        catalogSyncService.Start();
        achievementSyncService = new BeastmasterAchievementSyncService(progressService);
        achievementSyncService.Start();
        var questService = new BeastmasterQuestService();
        countdownService = new BeastmasterCountdownService(Configuration);
        sequenceService = new BeastmasterSequenceService(Configuration, countdownService);
        var crucibleItemService = new BeastmasterCrucibleItemService();
        ruleService = new BeastmasterRuleService(Configuration, crucibleItemService);
        var debugDataService = new BeastmasterDebugDataService(countdownService);
        navigationService = new BeastmasterNavigationService(pluginInterface, Configuration);
        catalogChatTracker = new BeastmasterCatalogChatTracker(Configuration, progressService);
        autoCaptureService = new BeastmasterAutoCaptureService(Configuration, sequenceService, ruleService, crucibleItemService);
        ui = new PluginUI(Configuration, progressService, questService, navigationService, debugDataService, autoCaptureService, catalogSyncService, achievementSyncService, notebookSyncService, sequenceService, ruleService, petPartyService);

        DalamudApi.Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Beastmasterを開きます。サブコマンド：出力、一時停止、再開、停止、カウントダウン [秒数]、カウントダウン中止。",
        });
        DalamudApi.Commands.AddHandler(ChineseCommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Beastmasterを開きます。サブコマンド：出力、一時停止、再開、停止、カウントダウン [秒数]、カウントダウン中止。",
        });
        DalamudApi.Commands.AddHandler(JapaneseCommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Beastmasterを開きます。サブコマンド：出力、一時停止、再開、停止、カウントダウン [秒数]、カウントダウン中止。",
        });

        pluginInterface.UiBuilder.Draw += ui.Draw;
        pluginInterface.UiBuilder.OpenMainUi += ui.OpenMainWindow;
        pluginInterface.UiBuilder.OpenConfigUi += ui.OpenMainWindow;

        DalamudApi.Log.Information("Beastmaster assistant loaded.");
    }

    public void Dispose()
    {
        DalamudApi.PluginInterface.UiBuilder.Draw -= ui.Draw;
        DalamudApi.PluginInterface.UiBuilder.OpenMainUi -= ui.OpenMainWindow;
        DalamudApi.PluginInterface.UiBuilder.OpenConfigUi -= ui.OpenMainWindow;
        DalamudApi.Commands.RemoveHandler(CommandName);
        DalamudApi.Commands.RemoveHandler(ChineseCommandName);
        DalamudApi.Commands.RemoveHandler(JapaneseCommandName);
        catalogChatTracker.Dispose();
        autoCaptureService.Dispose();
        countdownService.Dispose();
        catalogSyncService.Dispose();
        achievementSyncService.Dispose();
        resultProgressService.Dispose();
        notebookProgressService.Dispose();
        notebookSyncService.Dispose();
        petPartyService.Dispose();
        navigationService.Dispose();
        Configuration.FlushPendingSaves();
        Configuration.Save();
    }

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            ui.OpenMainWindow();
            return;
        }

        if (trimmed.StartsWith("カウントダウン", StringComparison.Ordinal)
            || trimmed.StartsWith("\u5012\u8BA1\u65F6", StringComparison.Ordinal)
            || trimmed.StartsWith("countdown", StringComparison.OrdinalIgnoreCase))
        {
            var prefixLength = trimmed.StartsWith("カウントダウン", StringComparison.Ordinal) ? 7
                : trimmed.StartsWith("\u5012\u8BA1\u65F6", StringComparison.Ordinal) ? 3
                : 9;
            var remainder = trimmed.Length > prefixLength ? trimmed[prefixLength..].Trim() : string.Empty;
            if (string.IsNullOrEmpty(remainder))
            {
                countdownService.StartCustomCountdown(10f);
            }
            else if (float.TryParse(remainder, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
            {
                countdownService.StartCustomCountdown(seconds);
            }
            else
            {
                DalamudApi.ChatGui.Print("[Beastmaster] 使用法：/魔獣使い カウントダウン [秒数]（デフォルト 10 秒）");
            }

            return;
        }

        if (trimmed.StartsWith("カウントダウン中止", StringComparison.Ordinal)
            || trimmed.StartsWith("\u53D6\u6D88\u5012\u8BA1\u65F6", StringComparison.Ordinal)
            || trimmed.StartsWith("cancelcountdown", StringComparison.OrdinalIgnoreCase))
        {
            countdownService.CancelCustomCountdown();
            return;
        }

        switch (trimmed)
        {
            case "出力":
            case "\u8F93\u51FA":
            case "output":
                if (!autoCaptureService.IsEnabled)
                {
                    autoCaptureService.SetEnabled(true);
                    autoCaptureService.SetPaused(false);
                }
                else
                {
                    autoCaptureService.SetPaused(!autoCaptureService.IsPaused);
                    DalamudApi.ChatGui.Print(autoCaptureService.IsPaused
                        ? "[Beastmaster] 自動出力を一時停止しました。"
                        : "[Beastmaster] 自動出力を再開しました。");
                }

                return;
            case "一時停止":
            case "pause":
                autoCaptureService.SetPaused(true);
                DalamudApi.ChatGui.Print("[Beastmaster] 自動出力を一時停止しました。/魔獣使い 再開 で復帰できます。");
                return;
            case "再開":
            case "resume":
                if (!autoCaptureService.IsEnabled)
                {
                    DalamudApi.ChatGui.Print("[Beastmaster] 自動出力が有効になっていません。先に /魔獣使い 出力 を実行してください。");
                    return;
                }

                autoCaptureService.SetPaused(false);
                DalamudApi.ChatGui.Print("[Beastmaster] 自動出力を再開しました。");
                return;
            case "停止":
            case "off":
                autoCaptureService.SetEnabled(false);
                DalamudApi.ChatGui.Print("[Beastmaster] 自動出力を停止しました。");
                return;
            default:
                ui.OpenMainWindow();
                return;
        }
    }
}
