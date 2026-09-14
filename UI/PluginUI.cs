using Dalamud.Bindings.ImGui;
using Dalamud.Game.Inventory;
using Lumina.Excel.Sheets;
using System.Diagnostics;
using System.Numerics;

namespace Beastmaster;

public sealed class PluginUI
{
    private static readonly (string Key, string Label)[] MainSections =
    [
        ("quests", "ジョブクエスト"),
        ("catalog", "魔獣図鑑"),
        ("equipment", "おすすめ装備"),
        ("combinations", "おすすめ編成"),
        ("sequences", "スキルシーケンス"),
        ("rules", "ルールモード"),
        ("commands", "便利コマンド"),
        ("auto-output", "自動戦闘(ACR)"),
        ("settings", "設定"),
        ("debug", "DEBUG"),
        ("arena-navigation", "闘獣塔へ移動"),
    ];

    private static readonly (uint ActionId, string Name)[] SequenceActions =
    [
        (44879, "砕き割り"),
        (44883, "噛み砕き"),
        (44885, "裂盾斧"),
        (44893, "シールドチャージ"),
        (44890, "はなつ"),
        (44891, "最後の一撃"),
        (44881, "一号呼笛"),
        (44892, "二号呼笛"),
        (44894, "三号呼笛"),
        (44895, "かりる"),
        (44896, "百獣の皮"),
        (44897, "百虫の皮"),
        (44898, "有翼飛掠"),
        (44899, "草木播種"),
        (44900, "水棲波"),
        (44901, "甲鱗の皮"),
        (44902, "呪具砕魂"),
        (44903, "死屍浄化"),
        (44904, "声援"),
        (44905, "鼓舞"),
    ];

    private readonly BeastmasterConfiguration configuration;
    private readonly BeastmasterProgressService progressService;
    private readonly BeastmasterQuestService questService;
    private readonly BeastmasterNavigationService navigationService;
    private readonly BeastmasterDebugDataService debugDataService;
    private readonly BeastmasterAutoCaptureService autoCaptureService;
    private readonly BeastmasterCatalogSyncService catalogSyncService;
    private readonly BeastmasterSequenceService sequenceService;
    private readonly BeastmasterRuleService ruleService;
    private string debugQuery = "魔獣";
    private string debugActionId = "44890";
    private string debugResult = "ボタンをクリックしてデータを読み込みます。";
    private bool debugUseAdjustedActionId = true;
    private int debugSearchType;
    private int debugProjectDataType;
    private int debugCurrentStateType;
    private DateTime nextQuestStatusRefreshUtc = DateTime.MinValue;
    private DateTime nextGaugeRefreshUtc = DateTime.MinValue;
    private BeastmasterGaugeSnapshot gaugeSnapshot = BeastmasterGaugeSnapshot.Unavailable("待機中");
    private int autoOutputCollapseState;
    private int selectedEquipmentSet;
    private int selectedBattleLogIndex;
    private DateTime nextEquipmentRefreshUtc = DateTime.MinValue;
    private readonly Dictionary<string, (uint ItemId, uint EquippedCount, uint InventoryCount, uint ArmoryCount)> equipmentOwnership = new(StringComparer.Ordinal);
    private bool isMainWindowOpen;
    private int newRuleTerritoryId;
    private string ruleImportStatus = string.Empty;

    public PluginUI(
        BeastmasterConfiguration configuration,
        BeastmasterProgressService progressService,
        BeastmasterQuestService questService,
        BeastmasterNavigationService navigationService,
        BeastmasterDebugDataService debugDataService,
        BeastmasterAutoCaptureService autoCaptureService,
        BeastmasterCatalogSyncService catalogSyncService,
        BeastmasterSequenceService sequenceService,
        BeastmasterRuleService ruleService)
    {
        this.configuration = configuration;
        this.progressService = progressService;
        this.questService = questService;
        this.navigationService = navigationService;
        this.debugDataService = debugDataService;
        this.autoCaptureService = autoCaptureService;
        this.catalogSyncService = catalogSyncService;
        this.sequenceService = sequenceService;
        this.ruleService = ruleService;
    }

    public void OpenMainWindow()
    {
        isMainWindowOpen = true;
    }

    public void Draw()
    {
        RefreshGaugeSnapshot();
        DrawAutoCaptureOverlay();
        if (!isMainWindowOpen)
        {
            return;
        }

        ImGui.SetNextWindowSize(new Vector2(900f, 600f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(700f, 460f), new Vector2(float.MaxValue, float.MaxValue));
        if (!ImGui.Begin($"Beastmaster v{GetType().Assembly.GetName().Version}", ref isMainWindowOpen))
        {
            ImGui.End();
            return;
        }

        DrawMainShell();
        ImGui.End();
    }

    private void DrawAutoCaptureOverlay()
    {
        const uint beastmasterClassJobId = 43;
        if (!autoCaptureService.IsEnabled
            || !DalamudApi.ClientState.IsLoggedIn
            || DalamudApi.ObjectTable.LocalPlayer == null
            || DalamudApi.PlayerState.ClassJob.RowId != beastmasterClassJobId)
        {
            return;
        }

        ImGui.SetNextWindowPos(new Vector2(20f, 180f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowBgAlpha(0.9f);
        if (!ImGui.Begin(
                "##BeastmasterAutoCaptureOverlay",
                ImGuiWindowFlags.NoTitleBar
                | ImGuiWindowFlags.AlwaysAutoResize
                | ImGuiWindowFlags.NoScrollbar
                | ImGuiWindowFlags.NoScrollWithMouse
                | ImGuiWindowFlags.NoFocusOnAppearing
                | ImGuiWindowFlags.NoNav))
        {
            ImGui.End();
            return;
        }

        DrawAutoOutputHeader();
        if (ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows)
            && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            configuration.SelectedMainSection = "settings";
            configuration.Save();
            isMainWindowOpen = true;
        }

        if (autoOutputCollapseState == 2)
        {
            ImGui.End();
            return;
        }

        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.35f, 0.85f, 0.55f, 1f), autoCaptureService.StatusText);
        if (configuration.ShowGaugeInOverlay)
        {
            ImGui.Text($"次のアクション：{autoCaptureService.NextActionName}");
            if (!string.IsNullOrWhiteSpace(autoCaptureService.NextActionReason))
            {
                ImGui.TextDisabled($"理由：{autoCaptureService.NextActionReason}");
            }
        }
        if (configuration.ShowGaugeInOverlay)
        {
            DrawOverlayGaugeSummary();
        }
        if (configuration.ShowGaugeInOverlay)
        {
            DrawOverlayTargetStatus();
        }
        if (configuration.ShowGaugeInOverlay)
        {
            DrawOverlayAdvancedCandidates();
        }
        ImGui.Separator();
        var tryCapture = autoCaptureService.TryCapture;
        if (ImGui.Checkbox("捕獲閾値", ref tryCapture))
        {
            autoCaptureService.SetTryCapture(tryCapture);
        }
        ImGui.SameLine();
        DrawCompactCaptureHpThreshold();
        if (configuration.ShowGaugeInOverlay)
        {
            var basicComboEnabled = autoCaptureService.BasicComboEnabled;
            if (ImGui.Checkbox("基本コンボ（1→2→3）", ref basicComboEnabled))
            {
                autoCaptureService.SetBasicComboEnabled(basicComboEnabled);
            }
        }
        if (autoOutputCollapseState == 0)
        {
            DrawAdvancedActionToggles(compactFinalStrike: true);

            var sequenceEnabled = sequenceService.Enabled;
            if (ImGui.Checkbox("##overlay-sequence-enabled", ref sequenceEnabled))
            {
                sequenceService.SetEnabled(sequenceEnabled);
            }
            ImGui.SameLine();
            DrawSequenceSelector(string.Empty, "##overlay-sequence-selector");
            ImGui.TextDisabled($"状態：{sequenceService.Status}");
            if (sequenceService.IsControlling && ImGui.Button("スキルシーケンス中断"))
            {
                sequenceService.Abort("手動中断されました。次のカウントダウンをお待ちください。");
            }
        }

        if (ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows)
            && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            configuration.SelectedMainSection = "settings";
            configuration.Save();
            isMainWindowOpen = true;
        }

        ImGui.End();
    }

    private void DrawAutoOutputHeader()
    {
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.85f, 0.35f, 1f));
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("魔獣ACR");
        ImGui.PopStyleColor();

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(autoOutputCollapseState switch
            {
                0 => "左クリックで半折りたたみ：高度アクションボタンとシーケンスを非表示",
                1 => "左クリックで完全折りたたみ",
                _ => "左クリックでオーバーレイ展開",
            });
        }

        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            autoOutputCollapseState = (autoOutputCollapseState + 1) % 3;
        }

        ImGui.SameLine();
        var headerStatus = !autoCaptureService.IsEnabled
            ? "停止"
            : autoCaptureService.IsPaused ? "一時停止" : "自動";
        if (DrawOverlayStatusBadge(
            headerStatus,
            autoCaptureService.IsEnabled && !autoCaptureService.IsPaused
                ? new Vector4(0.2f, 0.42f, 0.28f, 1f)
                : new Vector4(0.3f, 0.3f, 0.34f, 1f),
            autoCaptureService.IsEnabled && !autoCaptureService.IsPaused
                ? new Vector4(0.45f, 1f, 0.58f, 1f)
                : new Vector4(0.7f, 0.7f, 0.75f, 1f)))
        {
            if (autoCaptureService.IsEnabled)
            {
                autoCaptureService.SetPaused(!autoCaptureService.IsPaused);
            }
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(!autoCaptureService.IsEnabled
                ? "先に「自動戦闘(ACR)」タブで有効化してください"
                : autoCaptureService.IsPaused ? "クリックで自動出力を再開" : "クリックで自動出力を一時停止");
        }

        ImGui.SameLine();
        var tryCapture = autoCaptureService.TryCapture;
        var forceCapture = autoCaptureService.ForceCapture;
        if (DrawOverlayStatusBadge(
            "とらえる",
            forceCapture
                ? new Vector4(0.48f, 0.12f, 0.12f, 1f)
                : tryCapture
                    ? new Vector4(0.2f, 0.42f, 0.28f, 1f)
                    : new Vector4(0.3f, 0.3f, 0.34f, 1f),
            forceCapture
                ? new Vector4(1f, 0.42f, 0.42f, 1f)
                : tryCapture
                    ? new Vector4(0.45f, 1f, 0.58f, 1f)
                    : new Vector4(0.7f, 0.7f, 0.75f, 1f)))
        {
            if (forceCapture)
            {
                autoCaptureService.SetTryCapture(false);
            }
            else if (tryCapture)
            {
                autoCaptureService.SetForceCapture(true);
            }
            else
            {
                autoCaptureService.SetTryCapture(true);
            }
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(forceCapture
                ? "強制：対象HPやバフ状態を無視して即実行（クリックでOFF）"
                : tryCapture
                    ? "通常：HP閾値とバフ状態を判定して実行（クリックで強制へ切替）"
                    : "OFF（クリックで「とらえる」ON）");
        }

        ImGui.SameLine();
        var activeAttack = autoCaptureService.ActiveAttack;
        if (DrawOverlayStatusBadge(
            "アクティブ",
            activeAttack
                ? new Vector4(0.2f, 0.42f, 0.28f, 1f)
                : new Vector4(0.3f, 0.3f, 0.34f, 1f),
            activeAttack
                ? new Vector4(0.45f, 1f, 0.58f, 1f)
                : new Vector4(0.7f, 0.7f, 0.75f, 1f)))
        {
            autoCaptureService.SetActiveAttack(!activeAttack);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(activeAttack
                ? "非戦闘時でも通常のACRによる攻撃・捕獲を許可します"
                : "非戦闘時は攻撃や捕獲を行いません（戦闘突入後に自動開始）");
        }
    }

    private static bool DrawOverlayStatusBadge(string label, Vector4 background, Vector4 textColor)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 8f);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(7f, 2f));
        ImGui.PushStyleColor(ImGuiCol.Button, background);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, background);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, background);
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.Text, textColor);
        var clicked = ImGui.Button(label);
        ImGui.PopStyleColor(5);
        ImGui.PopStyleVar(2);
        return clicked;
    }

    private void DrawMainShell()
    {
        if (!ImGui.BeginTable(
                "BeastmasterMainShell",
                2,
                ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
        {
            return;
        }

        ImGui.TableSetupColumn("ナビゲーション", ImGuiTableColumnFlags.WidthFixed, 190f);
        ImGui.TableSetupColumn("メイン", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        DrawSidebar();
        ImGui.TableNextColumn();
        DrawContent();
        ImGui.EndTable();
    }

    private void RefreshGaugeSnapshot()
    {
        if (DateTime.UtcNow < nextGaugeRefreshUtc)
        {
            return;
        }

        gaugeSnapshot = BeastmasterGaugeSnapshot.Read();
        nextGaugeRefreshUtc = DateTime.UtcNow.AddMilliseconds(100);
    }

    private void DrawOverlayGaugeSummary()
    {
        if (!gaugeSnapshot.Available)
        {
            return;
        }

        var entry = gaugeSnapshot.SummonEntry;
        ImGui.Text(entry == null
            ? $"現在の魔獣：{(gaugeSnapshot.SummonDataId == 0 ? "未召喚" : gaugeSnapshot.SummonName)}"
            : $"現在の魔獣：{entry.Name} [{entry.Attribute}]");
        DrawOverlayGaugeBar("技力", gaugeSnapshot.Tp, BeastmasterGaugeSnapshot.MaximumGauge, new Vector4(0.95f, 0.75f, 0.2f, 1f));
        DrawOverlayGaugeBar("魔獣技力", gaugeSnapshot.BeastPower, BeastmasterGaugeSnapshot.MaximumGauge, new Vector4(0.35f, 0.7f, 1f, 1f));
        ImGui.Text($"ビーストハート：{gaugeSnapshot.BeastHeartStacks} | ビーストソウル：{gaugeSnapshot.BeastSoulStacks}");
    }

    private void DrawOverlayTargetStatus()
    {
        ImGui.Text("現在のターゲット");
        var target = DalamudApi.TargetManager.Target;
        if (target is not Dalamud.Game.ClientState.Objects.Types.IBattleChara)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("ターゲットなし");
            return;
        }

        ImGui.SameLine();
        ImGui.Text(target.Name.TextValue);
        ImGui.SameLine();
        ImGui.TextDisabled($"{autoCaptureService.TargetHpPercent:0.#}% · {autoCaptureService.TargetStatus}");
        if (configuration.ShowGaugeInOverlay)
        {
            ImGui.TextDisabled($"とらえる：{autoCaptureService.CaptureState}");
        }
    }

    private static void DrawOverlayGaugeBar(string label, float value, float maximum, Vector4 color)
    {
        var fraction = Math.Clamp(value / maximum, 0f, 1f);
        ImGui.PushStyleColor(ImGuiCol.PlotHistogram, color);
        ImGui.ProgressBar(fraction, new Vector2(-1f, 14f), $"{label} {value:0}/{maximum:0}");
        ImGui.PopStyleColor();
    }

    private void DrawCompactCaptureHpThreshold()
    {
        var threshold = Math.Clamp(configuration.CaptureHpThreshold, 1f, 100f);
        ImGui.SetNextItemWidth(70f);
        if (ImGui.InputFloat("##overlay-capture-threshold", ref threshold, 0f, 0f, "%.0f%%"))
        {
            threshold = Math.Clamp(threshold, 1f, 100f);
            configuration.CaptureHpThreshold = threshold;
            configuration.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("「とらえる」HP閾値：対象のHPがこの割合以下になったら実行");
        }
    }

    private void DrawOverlayAdvancedCandidates()
    {
        if (!ImGui.CollapsingHeader("高度スキル候補##BeastmasterAdvancedCandidates"))
        {
            return;
        }

        var entry = gaugeSnapshot.SummonEntry;
        if (entry == null)
        {
            ImGui.TextDisabled("現在の魔獣を認識できていないため、高度スキル候補を生成できません。");
            return;
        }

        ImGui.TextColored(GetAttributeColor(entry.Attribute), $"属性：{entry.Attribute}");
        DrawAdvancedCandidate(
            configuration.BeastHeartCooperationEnabled ? "ビーストハート連携（黄）" : "ビーストソウル連携（青）",
            configuration.BeastHeartCooperationEnabled
                ? $"{GetActionName(47093)} → 属性技（ウェポンスキル）"
                : $"属性技（ウェポンスキル） → {GetActionName(47093)}",
            configuration.BeastHeartCooperationEnabled || configuration.BeastSoulCooperationEnabled,
            gaugeSnapshot.Tp >= 100 && gaugeSnapshot.BeastPower >= 100
                ? "技力・魔獣技力が発動条件を満たしています"
                : $"リソース不足：技力 {gaugeSnapshot.Tp}/100、魔獣技力 {gaugeSnapshot.BeastPower}/100");
        var thirdFormEnabled = configuration.PhysicalThirdFormEnabled || configuration.MagicalThirdFormEnabled;
        DrawAdvancedCandidate(
            configuration.PhysicalThirdFormEnabled ? "万象流転（物理）" : "万象流転（魔法）",
            GetThirdFormActionName(gaugeSnapshot),
            thirdFormEnabled,
            GetThirdFormReason(gaugeSnapshot));
        ImGui.TextDisabled($"はなつ：固有アクション（{(configuration.AutoReleaseEnabled ? "ON" : "OFF")}）");
        if (ImGui.Button($"手動で獣心技を実行##manual-ultimate-overlay"))
        {
            autoCaptureService.TryUseUltimate();
        }
        ImGui.SameLine();
        ImGui.TextDisabled(autoCaptureService.ManualActionStatus);
    }

    private static void DrawAdvancedCandidate(string type, string actionName, bool enabled, string reason)
    {
        ImGui.Text($"{type}：{actionName}");
        ImGui.SameLine();
        ImGui.TextDisabled(enabled ? reason : "無効");
    }

    private void DrawSidebar()
    {
        ImGui.Text("Beastmaster");
        ImGui.TextDisabled("Beastmaster Progress Hub");
        ImGui.Separator();

        DrawSidebarLabel("コンテンツ");
        DrawSidebarButton(MainSections[0]);
        DrawSidebarButton(MainSections[1]);
        DrawSidebarButton(MainSections[2]);
        DrawSidebarButton(MainSections[3]);
        DrawSidebarButton(MainSections[4]);
        DrawSidebarButton(MainSections[5]);
        DrawSidebarButton(MainSections[6]);
        DrawSidebarButton(MainSections[7]);

        ImGui.Separator();
        DrawSidebarLabel("ツール");
        DrawSidebarButton(MainSections[8]);

        if (ImGui.Button("フィードバック・要望", new Vector2(ImGui.GetContentRegionAvail().X, 30f)))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "https://discord.com/channels/1258981591124938762/1546882305686118420")
            {
                UseShellExecute = true,
            });
        }

        DrawSidebarButton(MainSections[9]);
        DrawSidebarButton(MainSections[10]);
    }

    private void DrawSidebarButton((string Key, string Label) section)
    {
        var selected = configuration.SelectedMainSection == section.Key;
        if (selected)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.12f, 0.23f, 0.25f, 1f));
        }

        if (ImGui.Button($"{section.Label}##section-{section.Key}", new Vector2(ImGui.GetContentRegionAvail().X, 34f)))
        {
            configuration.SelectedMainSection = section.Key;
            configuration.Save();
            if (section.Key == "arena-navigation")
            {
                navigationService.Navigate(new BeastmasterQuestLocation(
                    148,
                    4,
                    new Vector3(24.238f, -6.003f, 65.809f),
                    "黒衣森：中央森林",
                    "闘獣塔"));
            }
        }

        if (selected)
        {
            ImGui.PopStyleColor();
        }
    }

    private static void DrawSidebarLabel(string label)
    {
        ImGui.Spacing();
        ImGui.TextDisabled(label);
    }

    private void DrawArenaNavigation()
    {
        ImGui.Text("闘獣塔へ移動");
        ImGui.TextDisabled("黒衣森：中央森林にある闘獣塔の入口へ自動ナビゲーションします。");
        ImGui.Separator();
        ImGui.Text("種別：現在地");
        ImGui.TextDisabled("TerritoryType: 148");
        ImGui.TextDisabled("エリア：黒衣森：中央森林");
        ImGui.TextDisabled("Map.RowId: 4");
        ImGui.TextDisabled("ワールド座標：X=24.238, Y=-6.003, Z=65.809");
        ImGui.TextDisabled("vnavmesh が必要です（エリア間移動には Lifestream が必要）");
    }

    private void DrawContent()
    {
        switch (configuration.SelectedMainSection)
        {
            case "quests":
                DrawQuests();
                break;
            case "catalog":
                DrawCatalog();
                break;
            case "equipment":
                DrawEquipment();
                break;
            case "combinations":
                DrawRecommendedCombinations();
                break;
            case "sequences":
                DrawSequenceEditor();
                break;
            case "rules":
                DrawRuleEditor();
                break;
            case "commands":
                DrawCommands();
                break;
            case "auto-output":
                DrawAutoOutput();
                break;
            case "settings":
                DrawSettings();
                break;
            case "debug":
                DrawDebug();
                break;
            case "arena-navigation":
                DrawArenaNavigation();
                break;
            default:
                configuration.SelectedMainSection = "quests";
                DrawQuests();
                break;
        }
    }

    private static void DrawRecommendedCombinations()
    {
        ImGui.TextColored(new Vector4(1f, 0.35f, 0.25f, 1f), "まずはジョブクエストを進めて補正装備を入手すると、レベリングが大幅に快適になります！");
        ImGui.Text("おすすめ編成"); ImGui.SameLine();
        ImGui.TextDisabled("攻略ガイド（自動戦闘には連動しません）");
        ImGui.Separator();

        ImGui.Text("昆虫PT · 闘獣練 1・2層 高速周回");
        ImGui.TextColored(new Vector4(0.95f, 0.82f, 0.25f, 1f), "推奨構成：マンティス ＋ ホーネット ＋ クーシー");
        ImGui.TextWrapped("最もシンプルかつ手軽な周回構成。マンティスの被ダメージ上昇バフを確実に当てるのがポイントです。");
        DrawCombinationStep("一号呼笛でマンティスを召喚、物理被ダメージ上昇付与後に", true, "を使用。");
        DrawCombinationStep("二号呼笛でホーネットを召喚、", false, "で自爆。");
        DrawCombinationStep("三号呼笛でクーシーを召喚、", false, "を使用して通常攻撃。");

        ImGui.Separator();
        ImGui.Text("水属性PT · 参考構成");
        ImGui.TextColored(new Vector4(0.35f, 0.75f, 1f, 1f), "サラマンダー ＋ メガロクラブ ＋ スニッパー");
        ImGui.TextWrapped("サラマンダーとメガロクラブで魔法被ダメージ上昇および水属性被ダメージ上昇を付与し、3番手のスニッパーで大ダメージを狙う構成です。");
        ImGui.TextDisabled("戦闘開始前に三号呼笛に切り替えて「かりる」を準備。水棲波は被ダメージ上昇中に合わせ、敵バフ解除にも活用できます。");
        DrawCombinationStep("一号呼笛でサラマンダーを召喚、魔法被ダメージ上昇付与後に", true, "を使用。");
        DrawCombinationStep("二号呼笛でメガロクラブを召喚、水属性被ダメージ上昇を付与。連携完了後に", true, "を使用。");
        DrawCombinationStep("三号呼笛でスニッパーを召喚、", false, "を使用して通常攻撃。");

        ImGui.Text("水属性PT · 1・2層 高速周回アレンジ");
        ImGui.TextColored(new Vector4(0.35f, 0.75f, 1f, 1f), "サラマンダー ＋ メガロクラブ ＋ アプカル");
        ImGui.TextDisabled("戦闘開始前に三号呼笛に切り替えてアプカルの「かりる」を準備。メガロクラブはフル連携を待たずに即移行可能です。");
        DrawCombinationStep("一号呼笛でサラマンダーを召喚、魔法被ダメージ上昇付与後に", true, "を使用。");
        DrawCombinationStep("二号呼笛でメガロクラブを召喚、水属性被ダメージ上昇を付与し、即座に", true, "を使用。");
        DrawCombinationStep("三号呼笛でアプカルを召喚、", false, "を使用して通常攻撃。");

        ImGui.Separator();
        ImGui.Text("情報提供・参考元");
        ImGui.Text("提供元：大三元 氏");
        ImGui.Text("水PT参考動画：夜風 氏");
        if (ImGui.Button("Bilibili 参考動画を開く##recommended-combination-bilibili"))
        {
            Process.Start(new ProcessStartInfo(
                "https://www.bilibili.com/video/BV1c1Y46AEnN/?spm_id_from=333.788.videopod.sections&vd_source=e8d743edd1edd93f4c56cdcf6833f6ff&p=2")
            {
                UseShellExecute = true,
            });
        }
        ImGui.SameLine();
        ImGui.TextDisabled("ブラウザで動画を開きます");
    }

    private static void DrawCombinationStep(string prefix, bool finalStrike, string suffix)
    {
        ImGui.Bullet();
        ImGui.SameLine();
        ImGui.TextUnformatted(prefix);
        ImGui.SameLine(0f, 3f);
        DrawCombinationAction("[はなつ]", 44890, new Vector4(1f, 0.82f, 0.25f, 1f));
        if (finalStrike)
        {
            ImGui.SameLine(0f, 3f);
            ImGui.TextUnformatted("、さらに");
            ImGui.SameLine(0f, 3f);
            DrawCombinationAction("[最後の一撃]", 44891, new Vector4(1f, 0.4f, 0.3f, 1f));
            ImGui.SameLine(0f, 3f);
            ImGui.TextUnformatted(suffix);
        }
        else
        {
            ImGui.SameLine(0f, 3f);
            ImGui.TextUnformatted(suffix);
        }
    }

    private static void DrawCombinationAction(string label, uint actionId, Vector4 color)
    {
        ImGui.TextColored(color, label);
        if (!ImGui.IsItemHovered())
        {
            return;
        }

        ImGui.BeginTooltip();
        DrawActionTooltip("アクション", actionId);
        var description = actionId switch
        {
            44890u => "ペットに対象への固有アクションを実行させます（呼び出している使役獣に応じて技が変化します）。",
            44891u => "ペットに対象への最後の一撃を実行させます。",
            _ => string.Empty,
        };
        if (!string.IsNullOrWhiteSpace(description))
        {
            ImGui.Separator();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 28f);
            ImGui.TextWrapped(description);
            ImGui.PopTextWrapPos();
        }
        ImGui.EndTooltip();
    }

    private void DrawSequenceEditor()
    {
        ImGui.Text("スキルシーケンス");
        ImGui.TextDisabled("カウントダウンおよび戦闘ステップを編集します。自動戦闘で有効化されている場合のみ実行されます。");
        ImGui.Separator();

        var sequences = configuration.Sequences;
        if (sequences.Count == 0)
        {
            sequences.Add(BeastmasterSequenceDefinition.CreateWaterOpener());
        }

        var selected = Math.Clamp(configuration.SelectedSequenceIndex, 0, sequences.Count - 1);
        var names = string.Join('\0', sequences.Select(sequence => sequence.Name)) + '\0';
        if (ImGui.Combo("現在のシーケンス", ref selected, names))
        {
            configuration.SelectedSequenceIndex = selected;
            configuration.Save();
        }

        ImGui.SameLine();
        var sequenceChat = sequenceService.ChatMessagesEnabled;
        if (ImGui.Checkbox("シーケンス診断", ref sequenceChat))
        {
            sequenceService.SetChatMessagesEnabled(sequenceChat);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("シーケンス開始、アクション要求、完了、中断の診断メッセージをチャットに出力します（失敗時の再試行は非表示）。");
        }

        var sequence = sequences[selected];
        var name = sequence.Name;
        ImGui.SetNextItemWidth(260f);
        if (ImGui.InputText("シーケンス名", ref name, 80))
        {
            sequence.Name = string.IsNullOrWhiteSpace(name) ? "未設定シーケンス" : name;
            configuration.Save();
        }
        var description = sequence.Description;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputText("説明", ref description, 200))
        {
            sequence.Description = description;
            configuration.Save();
        }

        if (ImGui.Button("新規シーケンス"))
        {
            sequences.Add(new BeastmasterSequenceDefinition { Name = "新規シーケンス", Description = "" });
            configuration.SelectedSequenceIndex = sequences.Count - 1;
            configuration.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button("シーケンス複製"))
        {
            var copy = BeastmasterSequenceDefinition.TryImport(sequence.Export(), out var imported, out _)
                ? imported!
                : BeastmasterSequenceDefinition.CreateWaterOpener();
            copy.Name += " 複製";
            sequences.Add(copy);
            configuration.SelectedSequenceIndex = sequences.Count - 1;
            configuration.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button("シーケンス削除") && sequences.Count > 1)
        {
            sequences.RemoveAt(selected);
            configuration.SelectedSequenceIndex = Math.Clamp(selected, 0, sequences.Count - 1);
            configuration.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button("虫PTテンプレート復元"))
        {
            sequences[selected] = BeastmasterSequenceDefinition.CreateWaterOpener();
            configuration.Save();
        }

        DrawSequenceStepList("カウントダウン", sequence.CountdownSteps, true);
        DrawSequenceStepList("戦闘突入後", sequence.CombatSteps, false);

        if (ImGui.Button("エクスポート（クリップボード）")) ImGui.SetClipboardText(sequence.Export());
        ImGui.SameLine();
        if (ImGui.Button("インポート（クリップボード）"))
        {
            if (BeastmasterSequenceDefinition.TryImport(ImGui.GetClipboardText(), out var imported, out var error) && imported != null)
            {
                sequences[selected] = imported;
                configuration.Save();
            }
            else debugResult = $"スキルシーケンスのインポートに失敗しました：{error}";
        }
    }

    private void DrawSequenceStepList(string title, List<BeastmasterSequenceStep> steps, bool countdown)
    {
        if (!ImGui.CollapsingHeader($"{title}ステップ##sequence-{title}")) return;
        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            ImGui.PushID($"{title}-{index}");
            ImGui.Text($"{index + 1}.");
            ImGui.SameLine();
            if (countdown)
            {
                var time = step.TimeSeconds ?? 0f;
                ImGui.SetNextItemWidth(110f);
                if (ImGui.InputFloat("秒数", ref time, 1f, 5f, "T-%.1f")) step.TimeSeconds = time;
                ImGui.SameLine();
            }
            var actionIndex = Array.FindIndex(SequenceActions, action => action.ActionId == step.ActionId);
            var actionOptions = string.Join('\0', SequenceActions.Select(action => action.Name)) + '\0';
            if (actionIndex < 0)
            {
                ImGui.TextDisabled($"未確認アクション ({step.ActionId})");
                ImGui.SameLine();
            }
            else
            {
                ImGui.SetNextItemWidth(180f);
                if (ImGui.Combo("アクション", ref actionIndex, actionOptions))
                {
                    step.ActionId = SequenceActions[actionIndex].ActionId;
                    step.Label = SequenceActions[actionIndex].Name;
                }
                ImGui.SameLine();
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("上へ") && index > 0) (steps[index - 1], steps[index]) = (steps[index], steps[index - 1]);
            ImGui.SameLine();
            if (ImGui.SmallButton("下へ") && index < steps.Count - 1) (steps[index + 1], steps[index]) = (steps[index], steps[index + 1]);
            ImGui.SameLine();
            if (ImGui.SmallButton("削除")) { steps.RemoveAt(index); ImGui.PopID(); break; }
            ImGui.PopID();
        }
        if (ImGui.Button($"{title}ステップを追加")) steps.Add(new(countdown ? 0f : null, 44879, "砕き割り"));
    }

    private void DrawRuleEditor()
    {
        ImGui.Text("ルールモード");
        ImGui.TextDisabled("ルールは戦闘中のみ実行されます。優先度はスキルシーケンスの下、通常ACRの上となり、表示順に最初に合致したルールを処理します。");
        ImGui.Separator();

        var enabled = ruleService.Enabled;
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.82f, 0.25f, 1f));
        if (ImGui.Checkbox("ルールモードを有効化", ref enabled)) ruleService.SetEnabled(enabled);
        ImGui.PopStyleColor();
        var diagnosticsEnabled = configuration.RuleDiagnosticsEnabled;
        if (ImGui.Checkbox("ルール診断", ref diagnosticsEnabled))
        {
            configuration.RuleDiagnosticsEnabled = diagnosticsEnabled;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("有効化すると現在のルールセットの診断レベルに従ってルールの成功・失敗情報を出力します。デフォルトは無効です。");
        }

        var ruleSets = configuration.RuleSets;
        if (ruleSets.Count == 0)
        {
            ruleSets.Add(new BeastmasterRuleSetDefinition());
        }
        configuration.SelectedRuleSetIndex = Math.Clamp(configuration.SelectedRuleSetIndex, 0, ruleSets.Count - 1);

        ImGui.BeginChild("RuleSetList", new Vector2(220f, 0f), true);
        ImGui.Text("ルールセット");
        for (var index = 0; index < ruleSets.Count; index++)
        {
            var label = $"{index + 1:00} {(ruleSets[index].Enabled ? "[有効]" : "[無効]")} {ruleSets[index].Name}";
            if (ImGui.Selectable($"{label}##rule-set-{index}", configuration.SelectedRuleSetIndex == index))
            {
                configuration.SelectedRuleSetIndex = index;
                configuration.SelectedRuleIndex = 0;
                configuration.Save();
            }
        }
        ImGui.Separator();
        if (ImGui.Button("新規ルールセット", new Vector2(-1f, 0f)))
        {
            ruleSets.Add(new BeastmasterRuleSetDefinition { Name = "新規ルールセット" });
            configuration.SelectedRuleSetIndex = ruleSets.Count - 1;
            configuration.SelectedRuleIndex = 0;
            configuration.Save();
        }
        if (ImGui.Button("ルールセット複製", new Vector2(-1f, 0f)))
        {
            var source = ruleSets[configuration.SelectedRuleSetIndex];
            if (BeastmasterRuleSetDefinition.TryImport(source.Export(), out var copy, out _) && copy != null)
            {
                copy.Name += " 複製";
                ruleSets.Add(copy);
                configuration.SelectedRuleSetIndex = ruleSets.Count - 1;
                configuration.SelectedRuleIndex = 0;
                configuration.Save();
            }
        }
        if (ImGui.Button("ルールセット削除", new Vector2(-1f, 0f)) && ruleSets.Count > 1)
        {
            ruleSets.RemoveAt(configuration.SelectedRuleSetIndex);
            configuration.SelectedRuleSetIndex = Math.Clamp(configuration.SelectedRuleSetIndex, 0, ruleSets.Count - 1);
            configuration.SelectedRuleIndex = 0;
            configuration.Save();
        }
        ImGui.EndChild();

        ImGui.SameLine();
        ImGui.BeginChild("RuleSetEditor", Vector2.Zero, true);
        var ruleSetIndex = configuration.SelectedRuleSetIndex;
        var ruleSet = ruleSets[ruleSetIndex];
        DrawRuleSetSettings(ruleSet, ruleSetIndex);
        ImGui.Separator();
        DrawRuleList(ruleSet);
        ImGui.Separator();
        ImGui.Text("最新の診断情報");
        ImGui.TextWrapped(ruleService.LastDiagnostic);
        ImGui.TextDisabled(ruleService.LastDiagnosticUtc == DateTime.MinValue
            ? "実行履歴なし"
            : ruleService.LastDiagnosticUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"));
        if (!string.IsNullOrWhiteSpace(ruleImportStatus)) ImGui.TextWrapped(ruleImportStatus);
        ImGui.EndChild();
    }

    private void DrawRuleSetSettings(BeastmasterRuleSetDefinition ruleSet, int ruleSetIndex)
    {
        var enabled = ruleSet.Enabled;
        if (ImGui.Checkbox("このルールセットを有効化", ref enabled))
        {
            ruleSet.Enabled = enabled;
            configuration.Save();
        }
        var name = ruleSet.Name;
        ImGui.SetNextItemWidth(260f);
        if (ImGui.InputText("ルールセット名", ref name, 80))
        {
            ruleSet.Name = string.IsNullOrWhiteSpace(name) ? "未設定ルールセット" : name;
            configuration.Save();
        }
        var description = ruleSet.Description;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputText("説明", ref description, 200))
        {
            ruleSet.Description = description;
            configuration.Save();
        }

        var areaMode = (int)ruleSet.AreaMode;
        ImGui.SetNextItemWidth(180f);
        if (ImGui.Combo("エリア制限", ref areaMode, "全エリア\0指定エリアのみ\0指定エリアを除外\0"))
        {
            ruleSet.AreaMode = (BeastmasterRuleAreaMode)areaMode;
            configuration.Save();
        }
        if (ruleSet.AreaMode != BeastmasterRuleAreaMode.All)
        {
            ImGui.TextDisabled(ruleSet.TerritoryIds.Count == 0 ? "TerritoryType ID が設定されていません" : $"TerritoryType：{string.Join(", ", ruleSet.TerritoryIds)}");
            ImGui.SetNextItemWidth(130f);
            if (ImGui.InputInt("エリアID追加", ref newRuleTerritoryId, 1, 10)) newRuleTerritoryId = Math.Max(0, newRuleTerritoryId);
            ImGui.SameLine();
            if (ImGui.Button("エリア追加") && newRuleTerritoryId is > 0 and <= ushort.MaxValue)
            {
                var territoryId = (ushort)newRuleTerritoryId;
                if (!ruleSet.TerritoryIds.Contains(territoryId)) ruleSet.TerritoryIds.Add(territoryId);
                newRuleTerritoryId = 0;
                configuration.Save();
            }
            for (var index = 0; index < ruleSet.TerritoryIds.Count; index++)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton($"削除 {ruleSet.TerritoryIds[index]}##territory-{index}"))
                {
                    ruleSet.TerritoryIds.RemoveAt(index);
                    configuration.Save();
                    break;
                }
            }
        }

        var diagnosticMode = (int)ruleSet.DiagnosticMode;
        ImGui.SetNextItemWidth(180f);
        if (ImGui.Combo("ルール診断レベル", ref diagnosticMode, "OFF\0失敗時のみ\0常時（完全）\0"))
        {
            ruleSet.DiagnosticMode = (BeastmasterRuleDiagnosticMode)diagnosticMode;
            configuration.Save();
        }

        if (ImGui.Button("エクスポート（クリップボード）"))
        {
            ImGui.SetClipboardText(ruleSet.Export());
            ruleImportStatus = "現在のルールセットをクリップボードにコピーしました。";
        }
        ImGui.SameLine();
        if (ImGui.Button("インポート（クリップボード）"))
        {
            if (BeastmasterRuleSetDefinition.TryImport(ImGui.GetClipboardText(), out var imported, out var error) && imported != null)
            {
                configuration.RuleSets.Add(imported);
                configuration.SelectedRuleSetIndex = configuration.RuleSets.Count - 1;
                configuration.SelectedRuleIndex = 0;
                configuration.Save();
                ruleImportStatus = $"ルールセット「{imported.Name}」をインポートしました。";
            }
            else
            {
                ruleImportStatus = $"ルールセットのインポートに失敗しました：{error}";
            }
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("ルールセットを上へ") && ruleSetIndex > 0)
        {
            (configuration.RuleSets[ruleSetIndex - 1], configuration.RuleSets[ruleSetIndex]) = (configuration.RuleSets[ruleSetIndex], configuration.RuleSets[ruleSetIndex - 1]);
            configuration.SelectedRuleSetIndex--;
            configuration.Save();
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("ルールセットを下へ") && ruleSetIndex < configuration.RuleSets.Count - 1)
        {
            (configuration.RuleSets[ruleSetIndex + 1], configuration.RuleSets[ruleSetIndex]) = (configuration.RuleSets[ruleSetIndex], configuration.RuleSets[ruleSetIndex + 1]);
            configuration.SelectedRuleSetIndex++;
            configuration.Save();
        }
    }

    private void DrawRuleList(BeastmasterRuleSetDefinition ruleSet)
    {
        ImGui.Text($"ルール（{ruleSet.Rules.Count}/100）");
        if (ImGui.Button("ルール追加") && ruleSet.Rules.Count < 100)
        {
            ruleSet.Rules.Add(new BeastmasterRuleDefinition());
            configuration.SelectedRuleIndex = ruleSet.Rules.Count - 1;
            configuration.Save();
        }

        if (ruleSet.Rules.Count == 0)
        {
            ImGui.TextDisabled("ルールがありません。追加されたルールは上から順に判定されます。");
            return;
        }

        configuration.SelectedRuleIndex = Math.Clamp(configuration.SelectedRuleIndex, 0, ruleSet.Rules.Count - 1);
        for (var index = 0; index < ruleSet.Rules.Count; index++)
        {
            var rule = ruleSet.Rules[index];
            var selected = configuration.SelectedRuleIndex == index;
            if (ImGui.Selectable($"{index + 1:00} {(rule.Enabled ? "[有効]" : "[無効]")} {rule.Name}  |  {GetRuleSummary(rule)}##rule-{index}", selected))
            {
                configuration.SelectedRuleIndex = index;
                configuration.Save();
            }
        }

        var selectedIndex = configuration.SelectedRuleIndex;
        var selectedRule = ruleSet.Rules[selectedIndex];
        ImGui.Spacing();
        ImGui.Text($"ルール {selectedIndex + 1} の編集");
        DrawRuleFields(selectedRule);

        if (ImGui.Button("ルール複製") && ruleSet.Rules.Count < 100)
        {
            ruleSet.Rules.Insert(selectedIndex + 1, CloneRule(selectedRule));
            configuration.SelectedRuleIndex++;
            configuration.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button("上へ") && selectedIndex > 0)
        {
            (ruleSet.Rules[selectedIndex - 1], ruleSet.Rules[selectedIndex]) = (ruleSet.Rules[selectedIndex], ruleSet.Rules[selectedIndex - 1]);
            configuration.SelectedRuleIndex--;
            configuration.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button("下へ") && selectedIndex < ruleSet.Rules.Count - 1)
        {
            (ruleSet.Rules[selectedIndex + 1], ruleSet.Rules[selectedIndex]) = (ruleSet.Rules[selectedIndex], ruleSet.Rules[selectedIndex + 1]);
            configuration.SelectedRuleIndex++;
            configuration.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button("ルール削除"))
        {
            ruleSet.Rules.RemoveAt(selectedIndex);
            configuration.SelectedRuleIndex = Math.Clamp(selectedIndex, 0, Math.Max(0, ruleSet.Rules.Count - 1));
            configuration.Save();
        }
    }

    private void DrawRuleFields(BeastmasterRuleDefinition rule)
    {
        var enabled = rule.Enabled;
        if (ImGui.Checkbox("有効##selected-rule", ref enabled))
        {
            rule.Enabled = enabled;
            configuration.Save();
        }
        var name = rule.Name;
        ImGui.SetNextItemWidth(260f);
        if (ImGui.InputText("ルール名", ref name, 80))
        {
            rule.Name = string.IsNullOrWhiteSpace(name) ? "未設定ルール" : name;
            configuration.Save();
        }

        var conditionType = (int)rule.ConditionType;
        ImGui.SetNextItemWidth(190f);
        if (ImGui.Combo("判定種別", ref conditionType, "自身のバフ\0対象のバフ\0DataIDのバフ\0DataIDの詠唱\0対象の詠唱\0"))
        {
            rule.ConditionType = (BeastmasterRuleConditionType)conditionType;
            configuration.Save();
        }
        if (rule.IsStatusRule)
        {
            var statusCondition = (int)rule.StatusCondition;
            ImGui.SetNextItemWidth(190f);
            if (ImGui.Combo("バフ条件", ref statusCondition, "付与中\0未付与\0"))
            {
                rule.StatusCondition = (BeastmasterRuleStatusCondition)statusCondition;
                configuration.Save();
            }
        }
        if (rule.RequiresDataId)
        {
            var dataId = (int)Math.Min(rule.DataId, int.MaxValue);
            ImGui.SetNextItemWidth(190f);
            if (ImGui.InputInt("DataID", ref dataId, 1, 100))
            {
                rule.DataId = (uint)Math.Max(0, dataId);
                configuration.Save();
            }
        }
        var conditionId = (int)Math.Min(rule.ConditionId, int.MaxValue);
        ImGui.SetNextItemWidth(190f);
        if (ImGui.InputInt(rule.IsStatusRule ? "バフID" : "詠唱アクションID", ref conditionId, 1, 100))
        {
            rule.ConditionId = (uint)Math.Max(0, conditionId);
            configuration.Save();
        }

        var supportedActions = BeastmasterRuleActions.Supported;
        var actionIndex = Array.FindIndex(supportedActions, action => action.ActionId == rule.ActionId);
        if (actionIndex < 0) actionIndex = 0;
        var actionNames = string.Join('\0', supportedActions.Select(action => $"{action.Name} ({action.ActionId})")) + '\0';
        ImGui.SetNextItemWidth(260f);
        if (ImGui.Combo("実行アクション", ref actionIndex, actionNames))
        {
            rule.ActionId = supportedActions[actionIndex].ActionId;
            configuration.Save();
        }

        if (!rule.TryValidate(out var error)) ImGui.TextColored(new Vector4(1f, 0.4f, 0.3f, 1f), error);
        ImGui.TextDisabled("対象指定アクションは現在ターゲットしている敵に実行されます（DataIDはトリガー判定のみ）。実行不可時はACRへ戻ります。");
    }

    private static BeastmasterRuleDefinition CloneRule(BeastmasterRuleDefinition source)
        => new()
        {
            Enabled = source.Enabled,
            Name = source.Name + " 複製",
            ConditionType = source.ConditionType,
            StatusCondition = source.StatusCondition,
            DataId = source.DataId,
            ConditionId = source.ConditionId,
            ActionId = source.ActionId,
        };

    private static string GetRuleSummary(BeastmasterRuleDefinition rule)
    {
        var actor = rule.ConditionType switch
        {
            BeastmasterRuleConditionType.SelfStatus => "自身",
            BeastmasterRuleConditionType.TargetStatus => "対象",
            BeastmasterRuleConditionType.DataIdStatus => $"DataID {rule.DataId}",
            BeastmasterRuleConditionType.DataIdCast => $"DataID {rule.DataId}",
            BeastmasterRuleConditionType.TargetCast => "対象",
            _ => "未知",
        };
        var condition = rule.IsStatusRule
            ? $"{(rule.StatusCondition == BeastmasterRuleStatusCondition.Present ? "付与中" : "未付与")} バフ {rule.ConditionId}"
            : $"詠唱 {rule.ConditionId}";
        var actionName = BeastmasterRuleActions.GetActionName(rule.ActionId);
        return $"{actor}{condition} -> {actionName}";
    }

    private void DrawQuests()
    {
        ImGui.Text("ジョブクエスト");
        ImGui.SameLine();
        if (ImGui.Button("WIKI##quest-wiki"))
        {
            Process.Start(new ProcessStartInfo("https://ff14.huijiwiki.com/wiki/%E9%A9%AF%E5%85%BD%E5%B8%88#%E7%89%B9%E8%81%8C%E4%BB%BB%E5%8A%A1")
            {
                UseShellExecute = true,
            });
        }

        ImGui.SameLine();
        var stopButtonWidth = ImGui.CalcTextSize("ナビ停止").X + ImGui.GetStyle().FramePadding.X * 2f;
        var stopButtonX = ImGui.GetWindowContentRegionMax().X - stopButtonWidth;
        if (ImGui.GetCursorPosX() < stopButtonX)
        {
            ImGui.SetCursorPosX(stopButtonX);
        }

        if (ImGui.Button("ナビ停止"))
        {
            navigationService.Stop();
        }

        ImGui.TextDisabled("ゲーム内のクエスト進行状況を表示し、未完了クエストの受注場所へナビゲートします。");
        ImGui.Separator();

        var quests = BeastmasterQuestGuide.Quests;
        var identifiedQuests = quests.Where(quest => quest.RowId != 0).ToArray();
        var statusRowIds = identifiedQuests
            .SelectMany(quest => questService.GetPrerequisites(quest.RowId).Select(previous => previous.RowId).Append(quest.RowId))
            .Distinct()
            .ToArray();
        if (DateTime.UtcNow >= nextQuestStatusRefreshUtc)
        {
            questService.RefreshStatuses(statusRowIds);
            nextQuestStatusRefreshUtc = DateTime.UtcNow.AddSeconds(2);
        }

        var completedCount = identifiedQuests.Count(quest => questService.GetStatus(quest.RowId) == BeastmasterQuestStatus.Completed);
        ImGui.ProgressBar((float)completedCount / quests.Count, new Vector2(-1f, 0f), $"{completedCount}/{quests.Count}");
        ImGui.Spacing();

        var hideCompleted = configuration.HideCompletedQuests;
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.82f, 0.25f, 1f));
        if (ImGui.Checkbox("完了済みクエストを非表示", ref hideCompleted))
        {
            configuration.HideCompletedQuests = hideCompleted;
            configuration.Save();
        }
        ImGui.PopStyleColor();
        ImGui.Separator();

        foreach (var quest in quests)
        {
            if (quest.RowId == 0)
            {
                ImGui.Text($"{quest.Name}  [待機中]");
                ImGui.TextDisabled(quest.Summary);
                ImGui.BeginDisabled();
                ImGui.Button("受注NPCへナビ##issuer-pending");
                ImGui.SameLine();
                ImGui.Button("目標地点へナビ##target-pending");
                ImGui.EndDisabled();
                ImGui.Separator();
                continue;
            }

            var questStatus = questService.GetStatus(quest.RowId);
            var completed = questStatus == BeastmasterQuestStatus.Completed;
            if (completed && configuration.HideCompletedQuests)
            {
                continue;
            }

            var status = completed
                ? "完了"
                : questStatus == BeastmasterQuestStatus.Accepted
                    ? "進行中"
                    : "未受注";
            ImGui.Text($"{quest.Name}  [{status}]");

            foreach (var prerequisite in questService.GetPrerequisites(quest.RowId))
            {
                var prerequisiteComplete = questService.GetStatus(prerequisite.RowId) == BeastmasterQuestStatus.Completed;
                ImGui.TextDisabled($"前提クエスト：{prerequisite.Name}");
                ImGui.SameLine();
                ImGui.TextColored(
                    prerequisiteComplete
                        ? new Vector4(0.35f, 0.8f, 0.48f, 1f)
                        : new Vector4(0.9f, 0.32f, 0.3f, 1f),
                    prerequisiteComplete ? "[完了]" : "[未完了]");
            }

            if (questService.TryGetIssuerLocation(quest.RowId, out var issuer))
            {
                ImGui.TextDisabled($"受注NPC：{issuer.NpcName} · {issuer.Zone}");
                if (completed)
                {
                    ImGui.BeginDisabled();
                }

                if (ImGui.Button($"受注NPCへナビ##issuer-{quest.RowId}"))
                {
                    navigationService.Navigate(issuer);
                }

                if (completed)
                {
                    ImGui.EndDisabled();
                }
            }
            else
            {
                ImGui.TextDisabled("受注NPC：有効な座標が取得できませんでした");
            }

            ImGui.SameLine();
            BeastmasterQuestLocation? target = null;
            var hasTarget = questStatus == BeastmasterQuestStatus.Accepted
                && questService.TryGetCurrentTarget(quest.RowId, out target);
            if (!hasTarget)
            {
                ImGui.BeginDisabled();
            }

            if (ImGui.Button($"目標地点へナビ##target-{quest.RowId}") && hasTarget)
            {
                navigationService.NavigateQuestTarget(target!);
            }

            if (!hasTarget)
            {
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.SetTooltip("現在のクエスト段階の目標座標は未対応です。 ");
                }
            }

            ImGui.Separator();
        }

    }

    private static void DrawReserved(string title)
    {
        ImGui.Text(title);
        ImGui.Separator();
        ImGui.TextDisabled("準備中");
    }

    private static void DrawCommands()
    {
        ImGui.Text("便利コマンド");
        ImGui.Separator();
        DrawGameCommandButton("魔獣図鑑を開く", "/bestiary");
        DrawGameCommandButton("使役獣サイズ：小", "/beastpetsize all small");
        DrawGameCommandButton("使役獣サイズ：中", "/beastpetsize all medium");
        DrawGameCommandButton("使役獣サイズ：大", "/beastpetsize all large");
    }

    private void DrawEquipment()
    {
        RefreshEquipmentOwnership();
        ImGui.Text("おすすめ装備");
        ImGui.TextDisabled("魔獣使い Lv50 攻略用装備および最終装備(BIS)構成。");
        var equipmentSet = selectedEquipmentSet;
        ImGui.SetNextItemWidth(220f);
        if (ImGui.Combo("装備構成", ref equipmentSet, "攻略用装備\0最終装備(BIS)\0"))
        {
            selectedEquipmentSet = equipmentSet;
        }
        ImGui.Separator();

        if (!ImGui.BeginTable(
                "BeastmasterEquipmentTable",
                4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
        {
            return;
        }

        ImGui.TableSetupColumn("部位", ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableSetupColumn("装備名", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("分類", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("所持状況", ImGuiTableColumnFlags.WidthFixed, 250f);
        ImGui.TableHeadersRow();

        var entries = selectedEquipmentSet == 0
            ? BeastmasterEquipmentGuide.Level50Starter
            : BeastmasterEquipmentGuide.Level50BestInSlot;
        foreach (var equipment in entries)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text(equipment.Slot);
            ImGui.TableNextColumn();
            if (equipment.Name == "装備なし" || equipment.Name == "无装备")
            {
                ImGui.TextDisabled(equipment.Name);
            }
            else
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.45f, 0.75f, 1f, 1f));
                ImGui.Text(equipment.Name);
                ImGui.PopStyleColor();
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("クリックでWikiの詳細を開く");
                }

                if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                {
                    OpenEquipmentWiki(equipment.Name);
                }
            }
            ImGui.TableNextColumn();
            ImGui.TextDisabled(equipment.Category);
            ImGui.TableNextColumn();
            DrawEquipmentOwnership(equipment);
        }

        ImGui.EndTable();
        ImGui.Spacing();
        ImGui.TextDisabled(selectedEquipmentSet == 0
            ? "攻略用装備：入手難度の低さと即戦力重視。"
            : "最終装備(BIS)：Lv50時点での最強ステータス重視。");
    }

    private static void OpenEquipmentWiki(string equipmentName)
    {
        Process.Start(new ProcessStartInfo(
            $"https://ff14.huijiwiki.com/wiki/{Uri.EscapeDataString($"物品:{equipmentName}")}")
        {
            UseShellExecute = true,
        });
    }

    private void DrawEquipmentOwnership(BeastmasterEquipmentEntry equipment)
    {
        if (equipment.Name == "装備なし" || equipment.Name == "无装备")
        {
            ImGui.TextDisabled("-");
            return;
        }

        if (!equipmentOwnership.TryGetValue(equipment.Name, out var ownership) || ownership.ItemId == 0)
        {
            ImGui.TextDisabled("未設定");
            return;
        }

        var equippedCount = ownership.EquippedCount;
        var inventoryCount = ownership.InventoryCount;
        var armoryCount = ownership.ArmoryCount;
        if (equippedCount > 0)
        {
            ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.5f, 1f), $"装備中 ({equippedCount})");
            ImGui.SameLine();
        }
        ImGui.TextColored(
            inventoryCount > 0 ? new Vector4(0.4f, 0.9f, 0.5f, 1f) : new Vector4(0.65f, 0.65f, 0.7f, 1f),
            inventoryCount > 0 ? $"所持品 あり ({inventoryCount})" : "所持品 なし");
        ImGui.SameLine();
        ImGui.TextColored(
            armoryCount > 0 ? new Vector4(0.4f, 0.8f, 1f, 1f) : new Vector4(0.65f, 0.65f, 0.7f, 1f),
            armoryCount > 0 ? $"アーマリーチェスト あり ({armoryCount})" : "アーマリーチェスト なし");
    }

    private void RefreshEquipmentOwnership()
    {
        if (DateTime.UtcNow < nextEquipmentRefreshUtc)
        {
            return;
        }

        equipmentOwnership.Clear();
        var selectedEntries = selectedEquipmentSet == 0
            ? BeastmasterEquipmentGuide.Level50Starter
            : BeastmasterEquipmentGuide.Level50BestInSlot;
        foreach (var equipment in selectedEntries.Where(item => item.Name != "装備なし" && item.Name != "无装备"))
        {
            if (equipment.ItemId == 0)
            {
                equipmentOwnership[equipment.Name] = (0, 0, 0, 0);
                continue;
            }

            equipmentOwnership[equipment.Name] = (
                equipment.ItemId,
                CountItems(equipment.ItemId, [GameInventoryType.EquippedItems]),
                CountItems(equipment.ItemId, InventoryTypes()),
                CountItems(equipment.ItemId, ArmoryTypes(equipment.Slot)));
        }

        nextEquipmentRefreshUtc = DateTime.UtcNow.AddSeconds(1);
    }

    private static uint CountItems(uint itemId, IEnumerable<GameInventoryType> types)
    {
        var count = 0u;
        foreach (var type in types)
        {
            foreach (var inventoryItem in DalamudApi.GameInventory.GetInventoryItems(type))
            {
                if (!inventoryItem.IsEmpty && inventoryItem.BaseItemId == itemId)
                {
                    count += (uint)inventoryItem.Quantity;
                }
            }
        }

        return count;
    }

    private static IEnumerable<GameInventoryType> InventoryTypes()
    {
        yield return GameInventoryType.Inventory1;
        yield return GameInventoryType.Inventory2;
        yield return GameInventoryType.Inventory3;
        yield return GameInventoryType.Inventory4;
    }

    private static IEnumerable<GameInventoryType> ArmoryTypes(string slot)
    {
        if (slot == "主武器" || slot == "主手") yield return GameInventoryType.ArmoryMainHand;
        else if (slot == "盾" || slot == "副手") yield return GameInventoryType.ArmoryOffHand;
        else if (slot == "頭防具" || slot == "头部") yield return GameInventoryType.ArmoryHead;
        else if (slot == "胴防具" || slot == "身体") yield return GameInventoryType.ArmoryBody;
        else if (slot == "手防具" || slot == "手部") yield return GameInventoryType.ArmoryHands;
        else if (slot == "脚防具" || slot == "腿部") yield return GameInventoryType.ArmoryLegs;
        else if (slot == "足防具" || slot == "脚部") yield return GameInventoryType.ArmoryFeets;
        else if (slot == "耳飾り" || slot == "耳饰") yield return GameInventoryType.ArmoryEar;
        else if (slot == "首飾り" || slot == "项链") yield return GameInventoryType.ArmoryNeck;
        else if (slot == "腕輪" || slot == "手镯") yield return GameInventoryType.ArmoryWrist;
        else if (slot.StartsWith("指輪", StringComparison.Ordinal) || slot.StartsWith("戒指", StringComparison.Ordinal)) yield return GameInventoryType.ArmoryRings;
        else if (slot == "証" || slot == "职业证") yield return GameInventoryType.ArmorySoulCrystal;
    }

    private static void DrawGameCommandButton(string label, string command)
    {
        if (ImGui.Button(label) && !GameCommandService.Execute(command))
        {
            DalamudApi.ChatGui.Print($"[Beastmaster] {command} を実行できませんでした。");
        }
    }

    private void DrawCatalog()
    {
        ImGui.Text("魔獣図鑑");
        ImGui.SameLine();
        if (ImGui.Button("WIKI"))
        {
            Process.Start(new ProcessStartInfo("https://ff14.huijiwiki.com/wiki/%E9%AD%94%E5%85%BD%E5%9B%BE%E9%89%B4")
            {
                UseShellExecute = true,
            });
        }

        ImGui.SameLine();
        var stopButtonWidth = ImGui.CalcTextSize("ナビ停止").X + ImGui.GetStyle().FramePadding.X * 2f;
        var stopButtonX = ImGui.GetWindowContentRegionMax().X - stopButtonWidth;
        if (ImGui.GetCursorPosX() < stopButtonX)
        {
            ImGui.SetCursorPosX(stopButtonX);
        }

        if (ImGui.Button("ナビ停止##catalog-stop-navigation"))
        {
            navigationService.Stop();
        }

        ImGui.TextDisabled("「とらえる」成功時に自動記録されます。手動でチェックを切り替えることも可能です。");
        if (ImGui.Button("魔獣図鑑の解放状況を同期"))
        {
            catalogSyncService.RequestSync();
        }

        if (!string.IsNullOrWhiteSpace(catalogSyncService.Diagnostic))
        {
            ImGui.SameLine();
            if (ImGui.Button("同期診断ログをコピー"))
            {
                ImGui.SetClipboardText(catalogSyncService.Diagnostic);
            }
        }

        ImGui.SameLine();
        ImGui.TextDisabled(catalogSyncService.Status);
        ImGui.Separator();

        var entries = BeastmasterCatalog.Entries;
        var sortByLocation = configuration.SortCatalogByLocation;
        var hideCaptured = configuration.HideCapturedBeasts;
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.82f, 0.25f, 1f));
        if (ImGui.Checkbox("マップ順で並び替え", ref sortByLocation))
        {
            configuration.SortCatalogByLocation = sortByLocation;
            configuration.Save();
        }
        ImGui.SameLine();
        var sortByLevel = configuration.SortCatalogByLevel;
        if (ImGui.Checkbox("レベル順で並び替え", ref sortByLevel))
        {
            configuration.SortCatalogByLevel = sortByLevel;
            configuration.Save();
        }
        ImGui.SameLine();
        if (ImGui.Checkbox("仲間にした魔獣を非表示", ref hideCaptured))
        {
            configuration.HideCapturedBeasts = hideCaptured;
            configuration.Save();
        }
        ImGui.PopStyleColor();

        var displayedEntries = entries.AsEnumerable();
        if (configuration.HideCapturedBeasts)
        {
            displayedEntries = displayedEntries.Where(e => !progressService.IsCompleted(e.Key));
        }

        var currentTerritory = DalamudApi.ClientState.TerritoryType;
        var currentMapRowId = DalamudApi.DataManager.GetExcelSheet<TerritoryType>()
            .TryGetRow(currentTerritory, out var currentTerritoryRow)
            ? currentTerritoryRow.Map.RowId
            : (ushort)0;
        var completedCount = entries.Count(entry => progressService.IsCompleted(entry.Key));
        ImGui.ProgressBar((float)completedCount / entries.Count, new Vector2(-1f, 0f), $"{completedCount}/{entries.Count}");

        ImGui.Spacing();

        if (!ImGui.BeginTable(
                "BeastmasterCatalogTable",
                8,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp,
                new Vector2(0f, 0f)))
        {
            return;
        }

        ImGui.TableSetupColumn("解放", ImGuiTableColumnFlags.WidthFixed, 46f);
        ImGui.TableSetupColumn("No. / 魔獣名", ImGuiTableColumnFlags.WidthFixed, 150f);
        ImGui.TableSetupColumn("属性", ImGuiTableColumnFlags.WidthFixed, 48f);
        ImGui.TableSetupColumn("アクション", ImGuiTableColumnFlags.WidthFixed, 118f);
        ImGui.TableSetupColumn("Lv", ImGuiTableColumnFlags.WidthFixed, 50f);
        ImGui.TableSetupColumn("エリア / コンテンツ", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("座標", ImGuiTableColumnFlags.WidthFixed, 112f);
        ImGui.TableSetupColumn("ナビ", ImGuiTableColumnFlags.WidthFixed, 62f);
        ImGui.TableHeadersRow();

        var canEdit = progressService.CurrentCharacterKey.Length > 0;
        IEnumerable<BeastmasterCatalogEntry> sortedEntries;
        if (configuration.SortCatalogByLocation)
        {
            sortedEntries = displayedEntries
                .OrderBy(entry => !(entry.TerritoryType == currentTerritory
                    && (entry.MapRowId == 0 || entry.MapRowId == currentMapRowId)))
                .ThenBy(entry => entry.Location, StringComparer.Ordinal)
                .ThenBy(entry => configuration.SortCatalogByLevel ? GetCatalogMinimumLevel(entry.Level) : int.MaxValue)
                .ThenBy(entry => entry.Number);
        }
        else if (configuration.SortCatalogByLevel)
        {
            sortedEntries = displayedEntries
                .OrderBy(entry => GetCatalogMinimumLevel(entry.Level))
                .ThenBy(entry => entry.Number);
        }
        else
        {
            sortedEntries = displayedEntries.OrderBy(entry => entry.Number);
        }
        foreach (var entry in sortedEntries)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var completed = progressService.IsCompleted(entry.Key);
            if (!canEdit)
            {
                ImGui.BeginDisabled();
            }

            if (ImGui.Checkbox($"##catalog-complete-{entry.Number}", ref completed))
            {
                progressService.SetCompleted(entry.Key, completed);
            }

            if (!canEdit)
            {
                ImGui.EndDisabled();
            }

            ImGui.TableNextColumn();
            ImGui.Text($"{entry.Number}. {entry.Name}");
            ImGui.TableNextColumn();
            ImGui.TextColored(GetAttributeColor(entry.Attribute), entry.Attribute.ToString());
            ImGui.TableNextColumn();
            ImGui.TextWrapped($"{GetActionName(entry.UltimateActionId)} /\n{GetActionName(entry.ReleaseActionId)}");
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                DrawActionTooltip("獣心技", entry.UltimateActionId);
                DrawActionTooltip("はなつ", entry.ReleaseActionId);
                ImGui.EndTooltip();
            }
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.Level);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.Location);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(GetCatalogCoordinate(entry));
            ImGui.TableNextColumn();
            if (entry.LocationType == BeastmasterCatalogLocationType.Field
                && entry.MapX.HasValue
                && entry.MapY.HasValue)
            {
                if (ImGui.SmallButton($"ナビ##catalog-nav-{entry.Number}"))
                {
                    navigationService.Navigate(entry);
                }
            }
            else if (entry.LocationType == BeastmasterCatalogLocationType.Duty
                && ImGui.SmallButton($"突入##catalog-duty-{entry.Number}"))
            {
                navigationService.OpenDutyFinder(entry);
            }
        }

        ImGui.EndTable();
    }

    private static string GetCatalogCoordinate(BeastmasterCatalogEntry entry)
        => entry.LocationType switch
        {
            BeastmasterCatalogLocationType.Field when entry.MapX.HasValue && entry.MapY.HasValue
                => $"X:{entry.MapX:0.#}, Y:{entry.MapY:0.#}",
            BeastmasterCatalogLocationType.Duty => "コンテンツ",
            BeastmasterCatalogLocationType.Starting => "初期習得",
            _ => "不明",
        };

    private static int GetCatalogMinimumLevel(string level)
    {
        var digits = new string(level.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var value) ? value : int.MaxValue;
    }

    private static Vector4 GetAttributeColor(BeastmasterAttribute attribute)
        => attribute switch
        {
            BeastmasterAttribute.猛 => new Vector4(0.95f, 0.35f, 0.3f, 1f),
            BeastmasterAttribute.坚 => new Vector4(0.35f, 0.65f, 1f, 1f),
            BeastmasterAttribute.魔 => new Vector4(1f, 0.82f, 0.25f, 1f),
            BeastmasterAttribute.翔 => new Vector4(0.4f, 0.9f, 0.5f, 1f),
            _ => new Vector4(0.6f, 0.6f, 0.6f, 1f),
        };

    private static string GetActionName(uint actionId)
        => DalamudApi.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>().TryGetRow(actionId, out var action)
            ? action.Name.ExtractText()
            : actionId.ToString();

    private static void DrawActionTooltip(string type, uint actionId)
    {
        var actions = DalamudApi.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
        if (!actions.TryGetRow(actionId, out var action))
        {
            ImGui.TextDisabled($"{type}：ActionId {actionId} が見つかりません");
            return;
        }

        ImGui.Text($"{type}：{action.Name.ExtractText()}");
        ImGui.TextDisabled($"ActionId：{actionId} · 習得Lv：{action.ClassJobLevel} · 射程：{action.Range}m · 効果範囲：{action.EffectRange}m");
    }

    private void DrawSettings()
    {
        ImGui.Text("設定");
        ImGui.Separator();

        ImGui.Text("連携プラグイン");
        DrawDependency("vnavmesh", navigationService.IsVnavmeshInstalled, "同マップ内ナビゲーション・移動");
        DrawDependency("Lifestream", navigationService.IsLifestreamInstalled, "エリア間テレポ・移動");
        ImGui.Spacing();

        ImGui.Text("一般設定");
        ImGui.TextDisabled($"現在のキャラクター：{progressService.CurrentCharacterLabel}");
        DrawSettingCheckbox("完了済みクエストを非表示", "クエスト一覧に未完了の魔獣使いクエストのみを表示します。", nameof(configuration.HideCompletedQuests), configuration.HideCompletedQuests);
        DrawSettingCheckbox("「とらえる」ログ自動記録", "「～と心を通わせた！」等のメッセージ受信時に魔獣図鑑を自動記録します。", nameof(configuration.AutoCompleteCatalogFromChat), configuration.AutoCompleteCatalogFromChat);
        ImGui.Spacing();

        ImGui.Text("ナビゲーション設定");
        DrawSettingCheckbox("フライングナビ", "vnavmeshによる飛行ルート探索を許可します。", nameof(configuration.UseFlightNavigation), configuration.UseFlightNavigation);
        DrawSettingCheckbox("マップピン設定", "ナビ実行時にゲーム内マップへ旗（Flag）を設定します。", nameof(configuration.SetFlagOnNavigation), configuration.SetFlagOnNavigation);
        DrawSettingCheckbox("ナビログ表示", "チャット欄にナビゲーションの開始・失敗ログを出力します。", nameof(configuration.ShowNavigationLogs), configuration.ShowNavigationLogs);
    }

    private void DrawAutoOutput()
    {
        ImGui.Text("自動戦闘(ACR)");
        ImGui.TextDisabled("自動「とらえる」および自動コンボ攻撃の設定を行います。");
        ImGui.Separator();

        var enabled = autoCaptureService.IsEnabled;
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.82f, 0.25f, 1f));
        if (ImGui.Checkbox("自動戦闘", ref enabled))
        {
            autoCaptureService.SetEnabled(enabled);
        }
        ImGui.PopStyleColor();
        var paused = autoCaptureService.IsPaused;
        if (ImGui.Checkbox("一時停止", ref paused))
        {
            autoCaptureService.SetPaused(paused);
        }
        ImGui.SameLine();
        ImGui.TextDisabled("一時停止中は自動戦闘の主設定を維持したまま、アクション実行を停止します");
        ImGui.TextDisabled("手動でターゲットしている敵対対象にのみ実行されます。「とらえる」未実行時は「とらえる」を優先し、その後 1→2→3 コンボを実行します。");
        var activeAttack = autoCaptureService.ActiveAttack;
        if (ImGui.Checkbox("アクティブ攻撃", ref activeAttack))
        {
            autoCaptureService.SetActiveAttack(activeAttack);
        }
        ImGui.SameLine();
        ImGui.TextDisabled("非戦闘時でも通常のACRによる攻撃や捕獲を開始します");

        var forceCapture = autoCaptureService.ForceCapture;
        if (ImGui.Checkbox("強制実行", ref forceCapture))
        {
            autoCaptureService.SetForceCapture(forceCapture);
        }
        ImGui.SameLine();
        ImGui.TextDisabled("対象HP割合やバフ状態を無視して即座に「とらえる」を実行します");
        DrawCaptureHpThreshold();
        DrawSettingCheckbox(
            "詳細オーバーレイ表示",
            "オーバーレイ上に理由、状態、現在の魔獣、ジョブHUD、高度スキル候補などの詳細を表示します（デフォルトOFF）。",
            nameof(configuration.ShowGaugeInOverlay),
            configuration.ShowGaugeInOverlay);
        DrawAutoOutputDiagnosticsSettings();
        DrawSettingCheckbox(
            "オーバーレイ3列モード",
            "オーバーレイの高度アクションボタンを3列で配置します。デフォルトは無効です。",
            nameof(configuration.OverlayThreeColumnMode),
            configuration.OverlayThreeColumnMode);
        ImGui.Spacing();
        DrawAdvancedActionToggles();
        ImGui.TextDisabled("優先順位：スキルシーケンス → 魔獣回復薬 → ルールモード → 連携技2段目 → はなつ → 最後の一撃 → 鼓舞 → 声援 → 万象流転 → 連携技1段目 → 安全シールド → とらえる → 基本コンボ");

        ImGui.Spacing();
        DrawSequenceSettings();

        ImGui.Spacing();
        ImGui.Text("現在の動作モード");
        ImGui.Text(autoCaptureService.IsEnabled
            ? autoCaptureService.IsPaused
                ? "一時停止中"
                : autoCaptureService.ForceCapture
                    ? "強制実行中..."
                    : autoCaptureService.TryCapture ? "自動実行中..." : "自動攻撃中..."
            : "未稼働");
        ImGui.TextDisabled("有効化するとオーバーレイ上で「とらえる」を切り替え可能。オーバーレイを右クリックで設定を開きます。");

        ImGui.Spacing();
        DrawBeastmasterGauge();
    }

    private void DrawAutoOutputDiagnosticsSettings()
    {
        if (!ImGui.CollapsingHeader("自動出力診断##AutoOutputDiagnostics"))
        {
            return;
        }

        ImGui.Indent();
        DrawSettingCheckbox(
            "自動出力診断を有効化",
            "すべての診断モジュールの状態または理由が変化した際にサマリーを出力し、戦闘ログに記録します。",
            nameof(configuration.AutoOutputDiagnosticsEnabled),
            configuration.AutoOutputDiagnosticsEnabled);
        ImGui.Spacing();
        if (ImGui.Button("選択した戦闘ログをコピー"))
        {
            ImGui.SetClipboardText(autoCaptureService.GetBattleLog(selectedBattleLogIndex));
        }
        ImGui.SameLine();
        if (ImGui.Button("戦闘ログをクリア")) autoCaptureService.ClearBattleLogs();
        var battleLogs = autoCaptureService.BattleLogLabels;
        if (battleLogs.Count > 0)
        {
            selectedBattleLogIndex = Math.Clamp(selectedBattleLogIndex, 0, battleLogs.Count - 1);
            var labels = string.Join('\0', battleLogs) + '\0';
            ImGui.SetNextItemWidth(260f);
            ImGui.Combo("戦闘ログ", ref selectedBattleLogIndex, labels);
        }
        else
        {
            selectedBattleLogIndex = 0;
            ImGui.TextDisabled("戦闘ログはありません");
        }
        ImGui.Unindent();
    }

    private void DrawAdvancedActionToggles(bool compactFinalStrike = false)
    {
        if (!compactFinalStrike && !ImGui.CollapsingHeader("高度アクション##BeastmasterAdvancedActions"))
        {
            return;
        }

        if (!compactFinalStrike) ImGui.Indent();
        if (compactFinalStrike)
        {
            if (configuration.OverlayThreeColumnMode)
            {
                var threeColumn = 0;
                DrawOverlayAdvancedToggle("獣心連携", configuration.BeastHeartCooperationEnabled, () => ToggleCooperation(true), "ビーストハート連携（黄）", ref threeColumn, columnCount: 3);
                DrawOverlayAdvancedToggle("獣霊連携", configuration.BeastSoulCooperationEnabled, () => ToggleCooperation(false), "ビーストソウル連携（青）", ref threeColumn, columnCount: 3);
                DrawOverlayAdvancedToggle("鼓舞", configuration.AutoDrumEnabled, () => ToggleBoolean(nameof(configuration.AutoDrumEnabled)), "鼓舞・リキャスト毎", ref threeColumn, columnCount: 3);
                DrawOverlayAdvancedToggle("万象・物理", configuration.PhysicalThirdFormEnabled, () => ToggleThirdForm(true), "万象流転（物理）", ref threeColumn, columnCount: 3);
                DrawOverlayAdvancedToggle("万象・魔法", configuration.MagicalThirdFormEnabled, () => ToggleThirdForm(false), "万象流転（魔法）", ref threeColumn, columnCount: 3);
                DrawOverlayAdvancedToggle("声援", configuration.AutoCheerEnabled, () => ToggleBoolean(nameof(configuration.AutoCheerEnabled)), "声援・リキャスト毎", ref threeColumn, columnCount: 3);
                DrawOverlayAdvancedToggle("自動呼笛", configuration.AutoWhistleEnabled, () => ToggleBoolean(nameof(configuration.AutoWhistleEnabled)), "自動呼笛", ref threeColumn, columnCount: 3);
                DrawOverlayAdvancedToggle("最後の一撃", configuration.AutoFinalStrikeEnabled, () => autoCaptureService.SetFinalStrikeEnabled(!configuration.AutoFinalStrikeEnabled), "最後の一撃", ref threeColumn, columnCount: 3);
                DrawOverlayAdvancedToggle("はなつ", configuration.AutoReleaseEnabled, () => ToggleBoolean(nameof(configuration.AutoReleaseEnabled)), "はなつ・リキャスト毎", ref threeColumn, columnCount: 3);
                DrawOverlayAdvancedToggle("継続ひきつけ", IsArenaRuleEnabled(46751, 2413), () => ToggleArenaRule(46751, 2413), "継続ひきつけ", ref threeColumn, columnCount: 3, yellowWhenEnabled: true);
                DrawOverlayAdvancedToggle("継続ちょうはつ", IsArenaRuleEnabled(46750, 5586), () => ToggleArenaRule(46750, 5586), "継続ちょうはつ", ref threeColumn, columnCount: 3, yellowWhenEnabled: true);
                DrawOverlayAdvancedToggle("安全シールド", configuration.AutoSafeShieldEnabled, () => ToggleBoolean(nameof(configuration.AutoSafeShieldEnabled)), "安全シールド：ターゲットとの距離が 3 yalms 以内で、シールドチャージが使用可能な場合に自動使用します。", ref threeColumn, columnCount: 3, yellowWhenEnabled: true);
                return;
            }

            var column = 0;
            DrawOverlayAdvancedToggle("獣心連携", configuration.BeastHeartCooperationEnabled,
                () =>
                {
                    configuration.BeastHeartCooperationEnabled = !configuration.BeastHeartCooperationEnabled;
                    if (configuration.BeastHeartCooperationEnabled) configuration.BeastSoulCooperationEnabled = false;
                    configuration.Save();
                }, "ビーストハート連携（黄）", ref column);
            DrawOverlayAdvancedToggle("獣霊連携", configuration.BeastSoulCooperationEnabled,
                () =>
                {
                    configuration.BeastSoulCooperationEnabled = !configuration.BeastSoulCooperationEnabled;
                    if (configuration.BeastSoulCooperationEnabled) configuration.BeastHeartCooperationEnabled = false;
                    configuration.Save();
                }, "ビーストソウル連携（青）", ref column);
            DrawOverlayAdvancedToggle("万象・物理", configuration.PhysicalThirdFormEnabled,
                () =>
                {
                    configuration.PhysicalThirdFormEnabled = !configuration.PhysicalThirdFormEnabled;
                    if (configuration.PhysicalThirdFormEnabled) configuration.MagicalThirdFormEnabled = false;
                    configuration.Save();
                }, "万象流転（物理）", ref column);
            DrawOverlayAdvancedToggle("万象・魔法", configuration.MagicalThirdFormEnabled,
                () =>
                {
                    configuration.MagicalThirdFormEnabled = !configuration.MagicalThirdFormEnabled;
                    if (configuration.MagicalThirdFormEnabled) configuration.PhysicalThirdFormEnabled = false;
                    configuration.Save();
                }, "万象流転（魔法）", ref column);
            DrawOverlayAdvancedToggle("鼓舞", configuration.AutoDrumEnabled,
                () =>
                {
                    configuration.AutoDrumEnabled = !configuration.AutoDrumEnabled;
                    configuration.Save();
                }, "鼓舞・リキャスト毎：獣心が0の時は通常判定、獣心が0より大きい時は万象流転（物理または魔法）有効時のみ判定。スキルシステム許可時に自動で鼓舞（44905）を使用します。", ref column);
            DrawOverlayAdvancedToggle("声援", configuration.AutoCheerEnabled,
                () =>
                {
                    configuration.AutoCheerEnabled = !configuration.AutoCheerEnabled;
                    configuration.Save();
                }, "声援・リキャスト毎：獣霊が0の時は通常判定、獣霊が0より大きい時は万象流転（物理または魔法）有効時のみ判定。スキルシステム許可時に自動で声援（44904）を使用します。", ref column);
            DrawOverlayAdvancedToggle("自動呼笛", configuration.AutoWhistleEnabled,
                () =>
                {
                    configuration.AutoWhistleEnabled = !configuration.AutoWhistleEnabled;
                    configuration.Save();
                }, "使役獣がいない時、呼笛 1→2→3 の順で使用可能なものを自動召喚します。リクエスト後1秒待機して召喚を確認し、連続使用を防止します。", ref column);
            DrawOverlayAdvancedToggle("安全シールド", configuration.AutoSafeShieldEnabled, () => ToggleBoolean(nameof(configuration.AutoSafeShieldEnabled)), "安全シールド：ターゲットとの距離が 3 yalms 以内で、シールドチャージが使用可能な場合に自動使用します。", ref column, yellowWhenEnabled: true);
            DrawOverlayAdvancedToggle("はなつ", configuration.AutoReleaseEnabled,
                () =>
                {
                    configuration.AutoReleaseEnabled = !configuration.AutoReleaseEnabled;
                    configuration.Save();
                }, "はなつ・リキャスト毎：スキルシステムが許可し、かつ召喚獣が射程内の時に自動ではなつを使用します。", ref column);
            DrawOverlayAdvancedToggle("最後の一撃", configuration.AutoFinalStrikeEnabled,
                () => autoCaptureService.SetFinalStrikeEnabled(!configuration.AutoFinalStrikeEnabled),
                "最後の一撃のマスター切り替え。呼笛ごとの個別設定やHP閾値は自動戦闘設定ページで行います。「はなつ待機」有効時は、魔獣がはなつを使用するまで待機します。",
                ref column);
            DrawOverlayAdvancedToggle("継続ひきつけ", IsArenaRuleEnabled(46751, 2413),
                () => ToggleArenaRule(46751, 2413),
                "ルールモード内の継続ひきつけルールを切り替えます。闘獣塔エリア（1339〜1343）でのみ有効です。",
                ref column,
                yellowWhenEnabled: true);
            DrawOverlayAdvancedToggle("継続ちょうはつ", IsArenaRuleEnabled(46750, 5586),
                () => ToggleArenaRule(46750, 5586),
                "ルールモード内の継続ちょうはつルールを切り替えます。闘獣塔エリア（1339〜1343）でのみ有効です。",
                ref column,
                yellowWhenEnabled: true);
            return;
        }

        var beastHeartEnabled = configuration.BeastHeartCooperationEnabled;
        if (ImGui.Checkbox("ビーストハート連携（黄）", ref beastHeartEnabled))
        {
            configuration.BeastHeartCooperationEnabled = beastHeartEnabled;
            if (beastHeartEnabled)
            {
                configuration.BeastSoulCooperationEnabled = false;
            }
            configuration.Save();
        }

        var beastSoulEnabled = configuration.BeastSoulCooperationEnabled;
        if (ImGui.Checkbox("ビーストソウル連携（青）", ref beastSoulEnabled))
        {
            configuration.BeastSoulCooperationEnabled = beastSoulEnabled;
            if (beastSoulEnabled)
            {
                configuration.BeastHeartCooperationEnabled = false;
            }
            configuration.Save();
        }

        var physicalThirdFormEnabled = configuration.PhysicalThirdFormEnabled;
        if (ImGui.Checkbox("万象流転（物理）", ref physicalThirdFormEnabled))
        {
            configuration.PhysicalThirdFormEnabled = physicalThirdFormEnabled;
            if (physicalThirdFormEnabled)
            {
                configuration.MagicalThirdFormEnabled = false;
            }
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("万象流転・物理");
        }

        var magicalThirdFormEnabled = configuration.MagicalThirdFormEnabled;
        if (ImGui.Checkbox("万象流転（魔法）", ref magicalThirdFormEnabled))
        {
            configuration.MagicalThirdFormEnabled = magicalThirdFormEnabled;
            if (magicalThirdFormEnabled)
            {
                configuration.PhysicalThirdFormEnabled = false;
            }
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("万象流転・魔法");
        }

        var autoWhistleEnabled = configuration.AutoWhistleEnabled;
        if (ImGui.Checkbox("自動呼笛", ref autoWhistleEnabled))
        {
            configuration.AutoWhistleEnabled = autoWhistleEnabled;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("使役獣がいない時、呼笛 1→2→3 の順で使用可能なものを自動召喚します（召喚後1秒待機）。");
        }

        DrawCompactSettingCheckbox("鼓舞", "鼓舞・リキャスト毎：獣心が0の時は通常判定、獣心が0より大きい時は万象流転（物理または魔法）有効時のみ判定。スキルシステム許可時に自動で鼓舞（44905）を使用します。", nameof(configuration.AutoDrumEnabled), configuration.AutoDrumEnabled);
        DrawCompactSettingCheckbox("声援", "声援・リキャスト毎：獣霊が0の時は通常判定、獣霊が0より大きい時は万象流転（物理または魔法）有効時のみ判定。スキルシステム許可時に自動で声援（44904）を使用します。", nameof(configuration.AutoCheerEnabled), configuration.AutoCheerEnabled);

        var autoRecoveryItemEnabled = configuration.AutoRecoveryItemEnabled;
        if (ImGui.Checkbox("低HP時に自動で魔獣回復薬を使用", ref autoRecoveryItemEnabled))
        {
            configuration.AutoRecoveryItemEnabled = autoRecoveryItemEnabled;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("闘獣塔での戦闘中のみ有効。自身のHPが閾値未満になった際、3級 → 2級 → 1級の優先度で魔獣回復薬を使用します（デフォルトOFF）。");
        }
        if (configuration.AutoRecoveryItemEnabled)
        {
            ImGui.SetNextItemWidth(70f);
            var recoveryThreshold = configuration.AutoRecoveryItemHpThreshold;
            if (ImGui.InputFloat("回復薬のHP閾値", ref recoveryThreshold, 0f, 0f, "%.0f%%"))
            {
                configuration.AutoRecoveryItemHpThreshold = Math.Clamp(recoveryThreshold, 1f, 100f);
                configuration.Save();
            }
        }

        if (compactFinalStrike)
        {
            DrawCompactFinalStrikeToggle();
        }
        else
        {
            DrawFinalStrikeSettings();
        }

        var releaseEnabled = configuration.AutoReleaseEnabled;
        if (ImGui.Checkbox("はなつ（固有技）", ref releaseEnabled))
        {
            configuration.AutoReleaseEnabled = releaseEnabled;
            configuration.Save();
        }

        ImGui.Unindent();
    }

    private static void DrawOverlayAdvancedToggle(
        string label,
        bool enabled,
        System.Action toggle,
        string tooltip,
        ref int column,
        bool yellowWhenEnabled = false,
        int columnCount = 2)
    {
        if (column % columnCount != 0) ImGui.SameLine();
        var background = enabled
            ? yellowWhenEnabled
                ? new Vector4(0.62f, 0.52f, 0.22f, 1f)
                : new Vector4(0.12f, 0.35f, 0.28f, 1f)
            : new Vector4(0.18f, 0.2f, 0.23f, 1f);
        var textColor = enabled
            ? yellowWhenEnabled
                ? new Vector4(1f, 0.95f, 0.68f, 1f)
                : new Vector4(0.55f, 1f, 0.72f, 1f)
            : new Vector4(0.7f, 0.72f, 0.76f, 1f);
        ImGui.PushStyleColor(ImGuiCol.Button, background);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, enabled
            ? yellowWhenEnabled
                ? new Vector4(0.72f, 0.61f, 0.28f, 1f)
                : new Vector4(0.16f, 0.45f, 0.35f, 1f)
            : new Vector4(0.25f, 0.28f, 0.33f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, background);
        ImGui.PushStyleColor(ImGuiCol.Text, textColor);
        if (ImGui.Button($"{label}##overlay-advanced-{label}", new Vector2(96f, 28f))) toggle();
        ImGui.PopStyleColor(4);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        column = (column + 1) % columnCount;
    }

    private static void DrawOverlayAdvancedPlaceholder(string label, ref int column, int columnCount = 2)
    {
        if (column % columnCount != 0) ImGui.SameLine();
        ImGui.BeginDisabled();
        ImGui.Button($"{label}##overlay-advanced-placeholder", new Vector2(96f, 28f));
        ImGui.EndDisabled();
        column = (column + 1) % columnCount;
    }

    private void ToggleBoolean(string propertyName)
    {
        switch (propertyName)
        {
            case nameof(configuration.AutoDrumEnabled): configuration.AutoDrumEnabled = !configuration.AutoDrumEnabled; break;
            case nameof(configuration.AutoCheerEnabled): configuration.AutoCheerEnabled = !configuration.AutoCheerEnabled; break;
            case nameof(configuration.AutoWhistleEnabled): configuration.AutoWhistleEnabled = !configuration.AutoWhistleEnabled; break;
            case nameof(configuration.AutoReleaseEnabled): configuration.AutoReleaseEnabled = !configuration.AutoReleaseEnabled; break;
            case nameof(configuration.AutoSafeShieldEnabled): configuration.AutoSafeShieldEnabled = !configuration.AutoSafeShieldEnabled; break;
            case nameof(configuration.AutoRecoveryItemEnabled): configuration.AutoRecoveryItemEnabled = !configuration.AutoRecoveryItemEnabled; break;
        }
        configuration.Save();
    }

    private void ToggleCooperation(bool heart)
    {
        if (heart)
        {
            configuration.BeastHeartCooperationEnabled = !configuration.BeastHeartCooperationEnabled;
            if (configuration.BeastHeartCooperationEnabled) configuration.BeastSoulCooperationEnabled = false;
        }
        else
        {
            configuration.BeastSoulCooperationEnabled = !configuration.BeastSoulCooperationEnabled;
            if (configuration.BeastSoulCooperationEnabled) configuration.BeastHeartCooperationEnabled = false;
        }
        configuration.Save();
    }

    private void ToggleThirdForm(bool physical)
    {
        if (physical)
        {
            configuration.PhysicalThirdFormEnabled = !configuration.PhysicalThirdFormEnabled;
            if (configuration.PhysicalThirdFormEnabled) configuration.MagicalThirdFormEnabled = false;
        }
        else
        {
            configuration.MagicalThirdFormEnabled = !configuration.MagicalThirdFormEnabled;
            if (configuration.MagicalThirdFormEnabled) configuration.PhysicalThirdFormEnabled = false;
        }
        configuration.Save();
    }

    private bool IsArenaRuleEnabled(uint actionId, uint conditionId)
        => FindArenaRule(actionId, conditionId)?.Enabled == true;

    private void ToggleArenaRule(uint actionId, uint conditionId)
    {
        var rule = FindArenaRule(actionId, conditionId);
        if (rule == null)
        {
            return;
        }

        rule.Enabled = !rule.Enabled;
        configuration.Save();
    }

    private BeastmasterRuleDefinition? FindArenaRule(uint actionId, uint conditionId)
        => configuration.RuleSets
            .SelectMany(ruleSet => ruleSet.Rules)
            .FirstOrDefault(rule => rule.ConditionType == BeastmasterRuleConditionType.SelfStatus
                && rule.StatusCondition == BeastmasterRuleStatusCondition.Missing
                && rule.ActionId == actionId
                && rule.ConditionId == conditionId);

    private void DrawSequenceSettings()
    {
        if (!ImGui.CollapsingHeader("スキルシーケンス##SequenceSettings"))
        {
            return;
        }

        ImGui.Indent();
        var sequenceEnabled = sequenceService.Enabled;
        if (ImGui.Checkbox("スキルシーケンスを有効化", ref sequenceEnabled))
        {
            sequenceService.SetEnabled(sequenceEnabled);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("カウントダウン開始時に指定のスキル回しを自動実行します。");
        }

        DrawSequenceSelector("シーケンス選択", "##sequence-settings-selector");
        ImGui.TextDisabled($"状態：{sequenceService.Status}");
        if (sequenceService.IsControlling && ImGui.Button("シーケンス中断"))
        {
            sequenceService.Abort("手動中断されました。");
        }
        ImGui.Unindent();
    }

    private void DrawSequenceSelector(string label, string comboId)
    {
        var sequences = configuration.Sequences;
        if (sequences.Count == 0)
        {
            sequences.Add(BeastmasterSequenceDefinition.CreateWaterOpener());
        }

        var selected = Math.Clamp(configuration.SelectedSequenceIndex, 0, sequences.Count - 1);
        var names = string.Join('\0', sequences.Select(sequence => sequence.Name)) + '\0';
        ImGui.SetNextItemWidth(220f);
        if (ImGui.Combo($"{label}{comboId}", ref selected, names))
        {
            configuration.SelectedSequenceIndex = selected;
            configuration.Save();
        }
    }

    private void DrawCompactFinalStrikeToggle()
    {
        var anyFinalStrike = configuration.AutoFinalStrikeWhistleOneEnabled
            || configuration.AutoFinalStrikeWhistleTwoEnabled
            || configuration.AutoFinalStrikeWhistleThreeEnabled;
        if (ImGui.Checkbox("最後の一撃", ref anyFinalStrike))
        {
            configuration.AutoFinalStrikeWhistleOneEnabled = anyFinalStrike;
            configuration.AutoFinalStrikeWhistleTwoEnabled = anyFinalStrike;
            configuration.AutoFinalStrikeWhistleThreeEnabled = anyFinalStrike;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("1/2/3号呼笛の「最後の一撃」を一括で切り替えます。詳細な閾値設定は「自動戦闘(ACR)」タブで行えます。");
        }
    }

    private void DrawFinalStrikeSettings()
    {
        DrawFinalStrikeWhistleToggle(
            "一号呼笛：最後の一撃",
            "AutoFinalStrikeWhistleOneEnabled",
            "AutoFinalStrikeWhistleOneHpThreshold",
            configuration.AutoFinalStrikeWhistleOneEnabled,
            configuration.AutoFinalStrikeWhistleOneHpThreshold);

        DrawFinalStrikeWhistleToggle(
            "二号呼笛：最後の一撃",
            "AutoFinalStrikeWhistleTwoEnabled",
            "AutoFinalStrikeWhistleTwoHpThreshold",
            configuration.AutoFinalStrikeWhistleTwoEnabled,
            configuration.AutoFinalStrikeWhistleTwoHpThreshold);

        DrawFinalStrikeWhistleToggle(
            "三号呼笛：最後の一撃",
            "AutoFinalStrikeWhistleThreeEnabled",
            "AutoFinalStrikeWhistleThreeHpThreshold",
            configuration.AutoFinalStrikeWhistleThreeEnabled,
            configuration.AutoFinalStrikeWhistleThreeHpThreshold);

        var waitForRelease = configuration.AutoFinalStrikeWaitForRelease;
        if (ImGui.Checkbox("最後の一撃：はなつ待機", ref waitForRelease))
        {
            configuration.AutoFinalStrikeWaitForRelease = waitForRelease;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("現在の魔獣が「はなつ」を使用済み、かつ「はなつ」がリキャスト待ちの場合にのみ「最後の一撃」を実行します。");
        }
    }

    private void DrawFinalStrikeWhistleToggle(
        string label,
        string enabledProperty,
        string thresholdProperty,
        bool enabled,
        float threshold)
    {
        if (ImGui.Checkbox(label, ref enabled))
        {
            SetFinalStrikeEnabled(enabledProperty, enabled);
        }
        ImGui.SameLine();
        var clampedThreshold = Math.Clamp(threshold, 1f, 100f);
        ImGui.SetNextItemWidth(120f);
        if (ImGui.InputFloat($"##{thresholdProperty}", ref clampedThreshold, 1f, 5f, "%.0f%%"))
        {
            SetFinalStrikeThreshold(thresholdProperty, clampedThreshold);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"{label}：魔獣HPがこの閾値以下のときに「最後の一撃」を実行します（範囲: 1%〜100%）。");
        }
    }

    private void SetFinalStrikeEnabled(string propertyName, bool value)
    {
        switch (propertyName)
        {
            case nameof(configuration.AutoFinalStrikeWhistleOneEnabled):
                configuration.AutoFinalStrikeWhistleOneEnabled = value;
                break;
            case nameof(configuration.AutoFinalStrikeWhistleTwoEnabled):
                configuration.AutoFinalStrikeWhistleTwoEnabled = value;
                break;
            case nameof(configuration.AutoFinalStrikeWhistleThreeEnabled):
                configuration.AutoFinalStrikeWhistleThreeEnabled = value;
                break;
        }
        configuration.Save();
    }

    private void SetFinalStrikeThreshold(string propertyName, float value)
    {
        var clamped = Math.Clamp(value, 1f, 100f);
        switch (propertyName)
        {
            case nameof(configuration.AutoFinalStrikeWhistleOneHpThreshold):
                configuration.AutoFinalStrikeWhistleOneHpThreshold = clamped;
                break;
            case nameof(configuration.AutoFinalStrikeWhistleTwoHpThreshold):
                configuration.AutoFinalStrikeWhistleTwoHpThreshold = clamped;
                break;
            case nameof(configuration.AutoFinalStrikeWhistleThreeHpThreshold):
                configuration.AutoFinalStrikeWhistleThreeHpThreshold = clamped;
                break;
        }
        configuration.Save();
    }

    private void DrawCaptureHpThreshold()
    {
        var threshold = Math.Clamp(configuration.CaptureHpThreshold, 1f, 100f);
        ImGui.SetNextItemWidth(180f);
        if (ImGui.InputFloat("「とらえる」HP閾値", ref threshold, 1f, 5f, "%.0f%%"))
        {
            threshold = Math.Clamp(threshold, 1f, 100f);
            configuration.CaptureHpThreshold = threshold;
            configuration.Save();
        }

        ImGui.SameLine();
        ImGui.TextDisabled("対象のHPがこの割合以下になったら実行");
    }

    private void DrawBeastmasterGauge()
    {
        ImGui.Separator();
        ImGui.Text("ジョブHUD（量譜）");
        ImGui.TextDisabled("魔獣使いのジョブHUD状態を読み取り表示します（JobGaugeManager.CurrentGauge）。");

        var snapshot = gaugeSnapshot;
        ImGui.TextColored(
            snapshot.Available
                ? new Vector4(0.35f, 0.85f, 0.55f, 1f)
                : new Vector4(0.9f, 0.55f, 0.35f, 1f),
            snapshot.Status);

        if (!snapshot.Available)
        {
            return;
        }

        DrawCurrentSummon(snapshot);

        if (ImGui.BeginTable(
                "BeastmasterGaugeState",
                2,
                ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("項目", ImGuiTableColumnFlags.WidthFixed, 150f);
            ImGui.TableSetupColumn("現在値", ImGuiTableColumnFlags.WidthStretch);
            DrawGaugeRow("技力", $"{snapshot.Tp} / {BeastmasterGaugeSnapshot.MaximumGauge}", "基本ウェポンスキルリソース（TP）");
            DrawGaugeRow("魔獣技力", $"{snapshot.BeastPower} / {BeastmasterGaugeSnapshot.MaximumGauge}", "獣心技リソース");
            DrawGaugeRow("現在の呼笛", snapshot.WhistleIndex switch
            {
                1 => "一号呼笛",
                2 => "二号呼笛",
                3 => "三号呼笛",
                _ => "未召喚",
            }, "発動中の呼笛");
            DrawGaugeRow("ペットHP", snapshot.SummonMaxHp > 0
                ? $"{snapshot.SummonHpPercent:0.#}%（{snapshot.SummonCurrentHp}/{snapshot.SummonMaxHp}）"
                : "取得不可", "自動最後の一撃の判定用");
            DrawGaugeRow("ビーストハート", $"{snapshot.BeastHeartStacks} スタック", "連携シンボル");
            DrawGaugeRow("ビーストソウル", $"{snapshot.BeastSoulStacks} スタック", "連携シンボル");
            DrawGaugeRow("連携II属性", GetBlackWhiteStatus(snapshot), "活命撃 4599 / 滅命撃 4600");
            ImGui.EndTable();
        }

        ImGui.TextDisabled($"推奨行動：{GetGaugeDecision(snapshot)}");
        ImGui.TextDisabled($"高度スキル判定：{autoCaptureService.AdvancedActionStatus}");
    }

    private static void DrawCurrentSummon(BeastmasterGaugeSnapshot snapshot)
    {
        if (snapshot.SummonDataId == 0)
        {
            ImGui.TextDisabled("現在の使役獣：未召喚");
            return;
        }

        var entry = BeastmasterCatalog.Entries.FirstOrDefault(
            item => item.Number == snapshot.SummonDataId - 18915);
        if (entry == null)
        {
            ImGui.TextDisabled($"現在の使役獣：{snapshot.SummonName}（DataId {snapshot.SummonDataId}）");
            return;
        }

        ImGui.Text("現在の使役獣");
        ImGui.SameLine();
        ImGui.Text(entry.Name);
        ImGui.SameLine();
        ImGui.TextColored(GetAttributeColor(entry.Attribute), $"[{entry.Attribute}]");
        ImGui.TextDisabled($"獣心技：{GetActionName(entry.UltimateActionId)} | はなつ：{GetActionName(entry.ReleaseActionId)}");
    }

    private static string GetGaugeDecision(BeastmasterGaugeSnapshot snapshot)
    {
        if (snapshot.SummonDataId == 0)
        {
            return "使役獣の召喚待ち";
        }

        if (snapshot.Tp < BeastmasterGaugeSnapshot.ComboGaugeRequirement)
        {
            return $"技力蓄積中（{snapshot.Tp}/{BeastmasterGaugeSnapshot.MaximumGauge}）";
        }

        if (snapshot.BeastPower < BeastmasterGaugeSnapshot.ComboGaugeRequirement)
        {
            return $"魔獣技力蓄積中（{snapshot.BeastPower}/{BeastmasterGaugeSnapshot.MaximumGauge}）";
        }

        return snapshot.BeastHeartStacks > 0
            ? "技力・魔獣技力が充足、ビーストハートと連携可能"
            : "技力・魔獣技力が充足、コンボ実行可能";
    }

    private static string GetBlackWhiteStatus(BeastmasterGaugeSnapshot snapshot)
        => (snapshot.HasWhiteStatus, snapshot.HasPurpleStatus) switch
        {
            (true, true) => "活命撃（白）と滅命撃（黒/紫）が同時に有効",
            (true, false) => "活命撃（白）",
            (false, true) => "滅命撃（黒/紫）",
            _ => "なし",
        };

    private string GetThirdFormActionName(BeastmasterGaugeSnapshot snapshot)
    {
        if (!configuration.PhysicalThirdFormEnabled && !configuration.MagicalThirdFormEnabled)
        {
            return "-";
        }

        var actionId = snapshot.BeastHeartStacks >= 3 && (snapshot.HasWhiteStatus || snapshot.HasPurpleStatus)
            ? 44905u
            : (snapshot.HasWhiteStatus, snapshot.HasPurpleStatus) switch
        {
            (true, _) => configuration.PhysicalThirdFormEnabled ? 44931u : 44933u,
            (false, true) => configuration.PhysicalThirdFormEnabled ? 44930u : 44932u,
            _ => 44905u,
        };
        return GetActionName(actionId);
    }

    private static string GetThirdFormReason(BeastmasterGaugeSnapshot snapshot)
        => snapshot.BeastHeartStacks >= 3 && (snapshot.HasWhiteStatus || snapshot.HasPurpleStatus)
            ? $"{GetBlackWhiteStatus(snapshot)}、ビーストハート {snapshot.BeastHeartStacks} スタック、鼓舞使用可能"
            : snapshot.HasWhiteStatus || snapshot.HasPurpleStatus
                ? $"{GetBlackWhiteStatus(snapshot)}、ビーストハート 3スタック待ち（現在 {snapshot.BeastHeartStacks}）"
                : "活命撃（白）または滅命撃（黒/紫）の付与待ち";

    private static void DrawGaugeRow(string name, object value, string description)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.Text(name);
        ImGui.TableNextColumn();
        ImGui.Text(value.ToString());
        ImGui.SameLine();
        ImGui.TextDisabled(description);
    }

    private static void DrawBeastmasterGaugeGuide()
    {
        if (!ImGui.CollapsingHeader("ジョブHUD解説##BeastmasterGaugeGuide"))
        {
            return;
        }

        ImGui.TextDisabled("以下の説明は公式ジョブガイドおよびゲーム内データに基づいて整理されたものです。本機能は状態の読み取り専用です。");
        ImGui.Separator();

        DrawGuideTitle("技力 (TP) / 魔獣技力");
        ImGui.TextWrapped("魔獣使い専用のゲージで確認できるリソースです。攻撃やアクション実行で蓄積され、100以上になると強力な「獣心技ウェポンスキル」が使用可能になります。");

        ImGui.Spacing();
        DrawGuideTitle("ビーストハート / ビーストソウル");
        ImGui.TextWrapped("獣心技連携を成立させることでスタックを獲得し、スタックを消費してさらなる強化技へと繋げます。");

        ImGui.Spacing();
        DrawGuideTitle("連携シンボル（獣心技連携）");
        ImGui.TextWrapped("連携シンボルには翔・猛・堅・魔の4属性が表示されます。魔獣使いとペットの一方が獣心技を実行してから7秒以内にもう一方を実行することで「獣心技連携」が発生します。");

        ImGui.Spacing();
        DrawGuideTitle("活命撃 / 滅命撃（獣心技連携II）");
        ImGui.TextWrapped("「翔→猛→堅→魔→翔」の時計回りの順番で連携させることで、上位の「獣心技連携II」が発生し、魔獣使いに「活命撃」または「滅命撃」の属性が付与されます。");

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1f, 0.9f, 0.25f, 1f), "ジョブHUDの完全な仕様は公式ジョブガイドでも確認できます。");
    }

    private static void DrawGuideTitle(string title)
    {
        ImGui.TextColored(new Vector4(1f, 0.65f, 0.2f, 1f), title);
    }

    private static void DrawDependency(string name, bool available, string purpose)
    {
        ImGui.TextColored(available ? new Vector4(0.35f, 0.8f, 0.48f, 1f) : new Vector4(0.9f, 0.42f, 0.38f, 1f), available ? "利用可能" : "未ロード");
        ImGui.SameLine();
        ImGui.Text(name);
        ImGui.SameLine();
        ImGui.TextDisabled(purpose);
    }

    private void DrawSettingCheckbox(string label, string description, string key, bool value)
    {
        if (ImGui.Checkbox($"{label}##{key}", ref value))
        {
            switch (key)
            {
                case nameof(configuration.HideCompletedQuests):
                    configuration.HideCompletedQuests = value;
                    break;
                case nameof(configuration.AutoCompleteCatalogFromChat):
                    configuration.AutoCompleteCatalogFromChat = value;
                    break;
                case nameof(configuration.UseFlightNavigation):
                    configuration.UseFlightNavigation = value;
                    break;
                case nameof(configuration.SetFlagOnNavigation):
                    configuration.SetFlagOnNavigation = value;
                    break;
                case nameof(configuration.ShowNavigationLogs):
                    configuration.ShowNavigationLogs = value;
                    break;
                case nameof(configuration.BeastHeartCooperationEnabled):
                    configuration.BeastHeartCooperationEnabled = value;
                    if (value) configuration.BeastSoulCooperationEnabled = false;
                    break;
                case nameof(configuration.BeastSoulCooperationEnabled):
                    configuration.BeastSoulCooperationEnabled = value;
                    if (value) configuration.BeastHeartCooperationEnabled = false;
                    break;
                case nameof(configuration.AutoReleaseEnabled):
                    configuration.AutoReleaseEnabled = value;
                    break;
                case nameof(configuration.AutoDrumEnabled):
                    configuration.AutoDrumEnabled = value;
                    break;
                case nameof(configuration.AutoCheerEnabled):
                    configuration.AutoCheerEnabled = value;
                    break;
                case nameof(configuration.AutoSafeShieldEnabled):
                    configuration.AutoSafeShieldEnabled = value;
                    break;
                case nameof(configuration.ShowGaugeInOverlay):
                    configuration.ShowGaugeInOverlay = value;
                    break;
                case nameof(configuration.OverlayThreeColumnMode):
                    configuration.OverlayThreeColumnMode = value;
                    break;
                case nameof(configuration.AutoOutputDiagnosticsEnabled):
                    configuration.AutoOutputDiagnosticsEnabled = value;
                    break;
            }

            configuration.Save();
        }

        ImGui.TextDisabled(description);
    }

    private void DrawCompactSettingCheckbox(string label, string description, string key, bool value)
    {
        if (ImGui.Checkbox($"{label}##{key}", ref value))
        {
            switch (key)
            {
                case nameof(configuration.AutoDrumEnabled):
                    configuration.AutoDrumEnabled = value;
                    break;
                case nameof(configuration.AutoCheerEnabled):
                    configuration.AutoCheerEnabled = value;
                    break;
            }
            configuration.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(description);
        }
    }

    private void DrawDebug()
    {
        ImGui.Text("DEBUG");
        ImGui.TextDisabled("データ種別を選択して読み込みます。取得結果は自動的にクリップボードへコピーされます。");
        ImGui.Separator();

        ImGui.Text("アクション状態");
        ImGui.SetNextItemWidth(Math.Max(120f, ImGui.GetContentRegionAvail().X - 72f));
        ImGui.InputText("##DebugActionId", ref debugActionId, 10);
        ImGui.SameLine();
        if (ImGui.Button("照会##DebugActionStatus"))
        {
            try
            {
                SetDebugResult(uint.TryParse(debugActionId, out var actionId)
                    ? debugDataService.GetActionStatusDebug(actionId, debugUseAdjustedActionId)
                    : "アクション状態\n有効な数値の ActionId を入力してください。");
            }
            catch (Exception ex)
            {
                SetDebugResult($"アクション状態\n照会エラー: {ex.Message}");
            }
        }

        ImGui.Checkbox("GetAdjustedActionId を使用", ref debugUseAdjustedActionId);
        ImGui.Spacing();

        ImGui.Text("キーワード検索");
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("##DebugQuery", ref debugQuery, 128);

        DrawDebugActionRow(
            "##DebugSearchType",
            ref debugSearchType,
            "クラス・ジョブ\0エリア\0クエスト\0アイテム\0NPC\0モンスター\0コンテンツ\0",
            "検索##DebugSearch",
            RunDebugSearch);

        ImGui.Spacing();
        ImGui.Text("プロジェクトデータ");
        DrawDebugActionRow(
            "##DebugProjectDataType",
            ref debugProjectDataType,
            "魔獣を調教せし者\0現在の全クエスト状態\0魔獣使いクエスト一覧\0魔獣図鑑コンテンツID\0自動「とらえる」ID\0魔獣属性マップ\0魔獣図鑑クライアントデータ\0おすすめ装備アイテムID\0魔獣回復薬スキャン\0コンテンツ専用アイテムコンテナスキャン\0XBM画面スキャン\0XBMアイテム構造\0",
            "読み込み##DebugProjectData",
            RunDebugProjectData);

        ImGui.Spacing();
        ImGui.Text("リアルタイム状態");
        DrawDebugActionRow(
            "##DebugCurrentStateType",
            ref debugCurrentStateType,
            "魔獣使いジョブHUD生データ\0現在のターゲット状態\0現在のコンボ状態\0連携検証データ\0現在のキャラクター\0現在の座標\0「とらえる」判定\0自動戦闘状態\0スキルシーケンス検証データ\0",
            "読み込み##DebugCurrentState",
            RunDebugCurrentState);

        ImGui.Separator();
        if (ImGui.BeginChild("DebugResult", Vector2.Zero, true))
        {
            ImGui.TextUnformatted(debugResult);
        }

        ImGui.EndChild();
    }

    private static void DrawDebugActionRow(
        string comboId,
        ref int selectedIndex,
        string options,
        string buttonLabel,
        System.Action action)
    {
        ImGui.SetNextItemWidth(Math.Max(120f, ImGui.GetContentRegionAvail().X - 72f));
        ImGui.Combo(comboId, ref selectedIndex, options);
        ImGui.SameLine();
        if (ImGui.Button(buttonLabel))
        {
            action();
        }
    }

    private void RunDebugSearch()
    {
        SetDebugResult(debugSearchType switch
        {
            0 => debugDataService.FindClassJobs(debugQuery),
            1 => debugDataService.FindTerritories(debugQuery),
            2 => debugDataService.FindQuests(debugQuery),
            3 => debugDataService.FindItems(debugQuery),
            4 => debugDataService.FindNpcs(debugQuery),
            5 => debugDataService.FindMonsters(debugQuery),
            6 => debugDataService.FindDuties(debugQuery),
            _ => "未知の検索種別。",
        });
    }

    private void RunDebugProjectData()
    {
        SetDebugResult(debugProjectDataType switch
        {
            0 => debugDataService.FindQuests("魔獣を調教せし者"),
            1 => questService.GetActiveQuestsDebug(),
            2 => debugDataService.FindBeastmasterQuestChain(),
            3 => debugDataService.FindCatalogDuties(),
            4 => debugDataService.FindAutoCaptureData(),
            5 => debugDataService.FindBeastmasterAttributes(),
            6 => debugDataService.GetBeastmasterCatalogProbe(),
            7 => debugDataService.FindRecommendedEquipmentIds(),
            8 => debugDataService.FindBeastmasterRecoveryItems(),
            9 => debugDataService.FindContentInventoryContainers(),
            10 => debugDataService.GetXbmAddonProbe(),
            11 => debugDataService.GetXbmItemStructureProbe(),
            _ => "未知のプロジェクトデータ種別。",
        });
    }

    private void RunDebugCurrentState()
    {
        SetDebugResult(debugCurrentStateType switch
        {
            0 => debugDataService.GetBeastmasterGaugeRaw(),
            1 => debugDataService.GetCurrentTargetDebug(),
            2 => debugDataService.GetComboDebug(),
            3 => debugDataService.GetCooperationValidationDebug(),
            4 => debugDataService.GetCharacter(),
            5 => debugDataService.GetLocation(),
            6 => debugDataService.GetCaptureCheckDebug(),
            7 => debugDataService.GetAutoOutputConditionDebug(),
            8 => debugDataService.GetSkillSequenceValidationDebug(),
            _ => "未知の状態種別。",
        });
    }

    private void SetDebugResult(string result)
    {
        debugResult = result;
        ImGui.SetClipboardText(result);
    }
}
