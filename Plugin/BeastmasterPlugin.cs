using Dalamud.Game.Command;
using Dalamud.Plugin;

namespace Beastmaster;

public sealed class BeastmasterPlugin : IDalamudPlugin
{
    private const string CommandName = "/beastmaster";
    private const string ChineseCommandName = "/驯兽师";
    private const string JapaneseCommandName = "/魔獣使い";
    private readonly PluginUI ui;
    private readonly BeastmasterNavigationService navigationService;
    private readonly BeastmasterCatalogChatTracker catalogChatTracker;
    private readonly BeastmasterAutoCaptureService autoCaptureService;
    private readonly BeastmasterCatalogSyncService catalogSyncService;
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
        catalogSyncService = new BeastmasterCatalogSyncService(progressService);
        catalogSyncService.Start();
        var questService = new BeastmasterQuestService();
        countdownService = new BeastmasterCountdownService(Configuration);
        sequenceService = new BeastmasterSequenceService(Configuration, countdownService);
        ruleService = new BeastmasterRuleService(Configuration);
        var debugDataService = new BeastmasterDebugDataService(countdownService);
        navigationService = new BeastmasterNavigationService(pluginInterface, Configuration);
        catalogChatTracker = new BeastmasterCatalogChatTracker(Configuration, progressService);
        autoCaptureService = new BeastmasterAutoCaptureService(Configuration, sequenceService, ruleService);
        ui = new PluginUI(Configuration, progressService, questService, navigationService, debugDataService, autoCaptureService, catalogSyncService, sequenceService, ruleService);

        DalamudApi.Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Beastmasterを開きます。サブコマンド：出力、一時停止、再開、停止。",
        });
        DalamudApi.Commands.AddHandler(ChineseCommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Beastmasterを開きます。サブコマンド：出力、一時停止、再開、停止。",
        });
        DalamudApi.Commands.AddHandler(JapaneseCommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Beastmasterを開きます。サブコマンド：出力、一時停止、再開、停止。",
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
        navigationService.Dispose();
        Configuration.Save();
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim())
        {
            case "出力":
            case "输出":
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
            case "暂停":
            case "pause":
                autoCaptureService.SetPaused(true);
                DalamudApi.ChatGui.Print("[Beastmaster] 自動出力を一時停止しました。/魔獣使い 再開 で復帰できます。");
                return;
            case "再開":
            case "恢复":
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
            case "关闭":
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
