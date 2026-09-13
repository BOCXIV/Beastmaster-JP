using Dalamud.Game.ClientState.Conditions;
using Lumina.Excel.Sheets;
using Dalamud.Game.ClientState.Objects.Types;
using System.Globalization;
using System.Numerics;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.Game;
using Dalamud.Game.NativeWrapper;

namespace Beastmaster;

public sealed class BeastmasterDebugDataService
{
    private const int ResultLimit = 200;
    private readonly BeastmasterCountdownService countdownService;

    public BeastmasterDebugDataService(BeastmasterCountdownService countdownService)
    {
        this.countdownService = countdownService;
    }

    public string GetCharacter()
    {
        var contentId = DalamudApi.PlayerState.ContentId;
        var player = DalamudApi.ObjectTable.LocalPlayer;
        if (!DalamudApi.ClientState.IsLoggedIn || player == null)
        {
            return "未ログインのため、キャラクター情報を取得できません。";
        }

        var world = player.HomeWorld.Value.Name.ExtractText();
        return JoinLines(
            "種別: 現在のキャラクター",
            $"ContentId: {contentId.ToString(CultureInfo.InvariantCulture)}",
            $"名前: {player.Name.TextValue}",
            $"ワールド: {world}");
    }

    public string GetLocation()
    {
        var territoryType = DalamudApi.ClientState.TerritoryType;
        var player = DalamudApi.ObjectTable.LocalPlayer;
        var position = player?.Position ?? Vector3.Zero;
        var territoryName = string.Empty;
        uint mapRowId = 0;

        if (DalamudApi.DataManager.GetExcelSheet<TerritoryType>().TryGetRow(territoryType, out var territory))
        {
            territoryName = territory.PlaceName.Value.Name.ExtractText();
            mapRowId = territory.Map.RowId;
        }

        return JoinLines(
            "種別: 現在地",
            $"TerritoryType: {territoryType}",
            $"エリア: {territoryName}",
            $"Map.RowId: {mapRowId}",
            player == null
                ? "座標: キャラクター未ロード"
                : $"ワールド座標: X={position.X:0.###}, Y={position.Y:0.###}, Z={position.Z:0.###}");
    }

    public string FindClassJobs(string query)
        => FormatMatches(
            "クラス・ジョブ ClassJob",
            query,
            DalamudApi.DataManager.GetExcelSheet<ClassJob>()
                .Select(row => (row.RowId, Name: row.Name.ExtractText())));

    public string FindTerritories(string query)
    {
        query = query.Trim();
        if (query.Length == 0)
        {
            return "エリア TerritoryType\nTerritoryType IDまたはエリア名のキーワードを入力してください。";
        }

        var territories = DalamudApi.DataManager.GetExcelSheet<TerritoryType>();
        var matches = uint.TryParse(query, out var territoryTypeId)
            ? territories.Where(row => row.RowId == territoryTypeId).ToArray()
            : territories
                .Where(row => row.RowId != 0
                    && row.PlaceName.Value.Name.ExtractText().Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(row => row.RowId)
                .Take(ResultLimit + 1)
                .ToArray();
        var duties = DalamudApi.DataManager.GetExcelSheet<ContentFinderCondition>();
        var builder = new StringBuilder()
            .AppendLine("種別: エリア TerritoryType")
            .AppendLine($"検索: {query}");

        foreach (var territory in matches.Take(ResultLimit))
        {
            var name = territory.PlaceName.Value.Name.ExtractText();
            builder.AppendLine($"TerritoryType={territory.RowId} | エリア={name} | Map.RowId={territory.Map.RowId}");

            var dutyMatches = duties
                .Where(duty => duty.RowId != 0 && duty.TerritoryType.RowId == territory.RowId)
                .Select(duty => $"{duty.RowId} {duty.Name.ExtractText()}")
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
            if (dutyMatches.Length > 0)
            {
                builder.AppendLine($"  コンテンツ: {string.Join(" | ", dutyMatches)}");
            }
        }

        if (matches.Length == 0)
        {
            builder.AppendLine("一致する項目が見つかりませんでした。");
        }
        else if (matches.Length > ResultLimit)
        {
            builder.AppendLine($"検索結果が {ResultLimit} 件を超えました。より具体的なキーワードを指定してください。");
        }

        return builder.ToString().TrimEnd();
    }

    public string FindQuests(string query)
    {
        query = query.Trim();
        if (query.Length == 0)
        {
            return "クエスト Quest\n名称キーワードを入力してください。";
        }

        var matches = DalamudApi.DataManager.GetExcelSheet<Quest>()
            .Where(row => row.RowId != 0
                && row.Name.ExtractText().Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(row => row.RowId)
            .Take(ResultLimit + 1)
            .ToArray();
        var builder = new StringBuilder()
            .AppendLine("種別: クエスト Quest")
            .AppendLine($"キーワード: {query}");

        foreach (var quest in matches.Take(ResultLimit))
        {
            var name = quest.Name.ExtractText();
            builder.AppendLine($"RowId={quest.RowId} | {name}");
            builder.AppendLine($"  IssuerStart={quest.IssuerStart.RowId} | IssuerLocation={quest.IssuerLocation.RowId}");
            builder.AppendLine($"  JournalGenre={quest.JournalGenre.RowId} | ClassJobCategory={quest.ClassJobCategory0.RowId}");
            builder.AppendLine($"  PreviousQuest={string.Join(',', quest.PreviousQuest.Select(previous => previous.RowId).Where(rowId => rowId != 0))}");

            if (quest.IssuerLocation.RowId == 0)
            {
                continue;
            }

            var level = quest.IssuerLocation.Value;
            var npcName = DalamudApi.DataManager.GetExcelSheet<ENpcResident>()
                .TryGetRow(quest.IssuerStart.RowId, out var npc)
                    ? npc.Singular.ExtractText()
                    : string.Empty;
            var zone = level.Territory.Value.PlaceName.Value.Name.ExtractText();
            builder.AppendLine($"  開始NPC={npcName} | TerritoryType={level.Territory.RowId} | Map.RowId={level.Map.RowId}");
            builder.AppendLine($"  ワールド座標: X={level.X:0.###}, Y={level.Y:0.###}, Z={level.Z:0.###} | エリア={zone}");
        }

        if (matches.Length == 0)
        {
            builder.AppendLine("一致する項目が見つかりませんでした。");
        }
        else if (matches.Length > ResultLimit)
        {
            builder.AppendLine($"検索結果が {ResultLimit} 件を超えました。より具体的なキーワードを指定してください。");
        }

        return builder.ToString().TrimEnd();
    }

    public string FindBeastmasterQuestChain()
    {
        const uint beastmasterJournalGenre = 198;
        const uint beastmasterClassJobCategory = 203;
        var matches = DalamudApi.DataManager.GetExcelSheet<Quest>()
            .Where(quest => quest.RowId != 0
                && (quest.JournalGenre.RowId == beastmasterJournalGenre
                    || quest.ClassJobCategory0.RowId == beastmasterClassJobCategory))
            .OrderBy(quest => quest.RowId)
            .ToArray();
        var builder = new StringBuilder()
            .AppendLine("種別: 魔獣使いクエスト候補")
            .AppendLine($"条件: JournalGenre={beastmasterJournalGenre} または ClassJobCategory={beastmasterClassJobCategory}");

        foreach (var quest in matches)
        {
            builder.AppendLine($"RowId={quest.RowId} | {quest.Name.ExtractText()}");
            builder.AppendLine($"  JournalGenre={quest.JournalGenre.RowId} | ClassJobCategory={quest.ClassJobCategory0.RowId}");
            builder.AppendLine($"  PreviousQuest={string.Join(',', quest.PreviousQuest.Select(previous => previous.RowId).Where(rowId => rowId != 0))}");
        }

        if (matches.Length == 0)
        {
            builder.AppendLine("一致する項目が見つかりませんでした。");
        }

        return builder.ToString().TrimEnd();
    }

    public string FindItems(string query)
        => FormatMatches(
            "アイテム Item",
            query,
            DalamudApi.DataManager.GetExcelSheet<Item>()
                .Select(row => (row.RowId, Name: row.Name.ExtractText())));

    public string FindNpcs(string query)
        => FormatMatches(
            "NPC ENpcResident",
            query,
            DalamudApi.DataManager.GetExcelSheet<ENpcResident>()
                .Select(row => (row.RowId, Name: row.Singular.ExtractText())));

    public string FindMonsters(string query)
        => FormatMatches(
            "モンスター BNpcName",
            query,
            DalamudApi.DataManager.GetExcelSheet<BNpcName>()
                .Select(row => (row.RowId, Name: row.Singular.ExtractText())));

    public string FindDuties(string query)
        => FormatMatches(
            "コンテンツ ContentFinderCondition",
            query,
            DalamudApi.DataManager.GetExcelSheet<ContentFinderCondition>()
                .Select(row => (row.RowId, Name: row.Name.ExtractText())));

    public string FindCatalogDuties()
    {
        var duties = DalamudApi.DataManager.GetExcelSheet<ContentFinderCondition>();
        var builder = new StringBuilder()
            .AppendLine("種別: 魔獣図鑑コンテンツID")
            .AppendLine("情報元: BeastmasterCatalog.Duty");

        foreach (var entry in BeastmasterCatalog.Entries.Where(entry => entry.LocationType == BeastmasterCatalogLocationType.Duty))
        {
            var normalizedName = NormalizeDutyName(entry.Location);
            var matches = duties
                .Where(duty => duty.RowId != 0
                    && duty.TerritoryType.RowId != 0
                    && NormalizeDutyName(duty.Name.ExtractText()).Equals(normalizedName, StringComparison.Ordinal))
                .OrderBy(duty => duty.RowId)
                .ToArray();

            builder.AppendLine($"図鑑 {entry.Number}. {entry.Name} | コンテンツ={entry.Location}");
            if (matches.Length == 0)
            {
                builder.AppendLine("  一致する ContentFinderCondition が見つかりませんでした。");
                continue;
            }

            foreach (var duty in matches)
            {
                var territoryName = DalamudApi.DataManager.GetExcelSheet<TerritoryType>()
                    .TryGetRow(duty.TerritoryType.RowId, out var territory)
                    ? territory.PlaceName.Value.Name.ExtractText()
                    : string.Empty;
                builder.AppendLine($"  ContentFinderCondition.RowId={duty.RowId} | TerritoryType={duty.TerritoryType.RowId} | Map.RowId={duty.TerritoryType.Value.Map.RowId} | エリア={territoryName}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    public string FindAutoCaptureData()
    {
        uint[] actionIds = [44879, 44883, 44885, 44880];
        var actions = DalamudApi.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
        var statuses = DalamudApi.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Status>();
        var builder = new StringBuilder()
            .AppendLine("種別: 「とらえる」アクション・ステータスID")
            .AppendLine("アクション:");

        foreach (var actionId in actionIds)
        {
            if (!actions.TryGetRow(actionId, out var action))
            {
                builder.AppendLine($"  Action.RowId={actionId}: 未検出");
                continue;
            }

            builder.AppendLine($"  Action.RowId={action.RowId} | {action.Name.ExtractText()} | ClassJob={action.ClassJob.RowId} | Lv={action.ClassJobLevel} | 射程={action.Range}");
        }

        builder.AppendLine("ステータス Status:");
        if (!statuses.TryGetRow(4626, out var captureStatus))
        {
            builder.AppendLine("  Status.RowId=4626: 未検出");
        }
        else
        {
            builder.AppendLine($"  Status.RowId={captureStatus.RowId} | {captureStatus.Name.ExtractText()}");
        }

        builder.AppendLine("現在のターゲットステータス:");
        if (DalamudApi.TargetManager.Target is not IBattleChara target)
        {
            builder.AppendLine("  戦闘ターゲットが選択されていません。");
        }
        else if (!target.StatusList.Any())
        {
            builder.AppendLine($"  {target.Name.TextValue}: ステータスなし。");
        }
        else
        {
            foreach (var status in target.StatusList.OrderBy(status => status.StatusId))
            {
                var statusName = statuses.TryGetRow(status.StatusId, out var statusRow)
                    ? statusRow.Name.ExtractText()
                    : string.Empty;
                builder.AppendLine($"  StatusId={status.StatusId} | {statusName} | 残り={status.RemainingTime:0.0}s | SourceId={status.SourceId}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    public string FindBeastmasterAttributes()
    {
        var actions = DalamudApi.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
        var builder = new StringBuilder()
            .AppendLine("種別: 魔獣使い魔獣属性マッピング")
            .AppendLine("情報元: 魔獣図鑑番号 + 使役獣 DataId + 獣心技 Action IconId")
            .AppendLine("属性 IconId: 3906=猛、3907=堅、3908=魔、3909=翔")
            .AppendLine();

        for (var number = 1; number <= BeastmasterCatalog.Entries.Count; number++)
        {
            var entry = BeastmasterCatalog.Entries[number - 1];
            var dataId = (uint)(18915 + number);
            var hasSkills = TryGetSummonSkills(dataId, out var ultimateId, out var releaseId);
            var ultimate = hasSkills && actions.TryGetRow(ultimateId, out var ultimateAction)
                ? ultimateAction
                : default;
            var release = hasSkills && actions.TryGetRow(releaseId, out var releaseAction)
                ? releaseAction
                : default;
            var iconId = hasSkills ? ultimate.Icon : 0;
            var attribute = iconId switch
            {
                3906 => "猛",
                3907 => "堅",
                3908 => "魔",
                3909 => "翔",
                _ => "不明",
            };

            builder.AppendLine($"図鑑 {entry.Number:00} | {entry.Name} | DataId={dataId}");
            builder.AppendLine($"  獣心技 ActionId={ultimateId} | {GetActionName(ultimate)} | IconId={iconId} | 属性={attribute}");
            builder.AppendLine($"  はなつ ActionId={releaseId} | {GetActionName(release)}");
        }

        return builder.ToString().TrimEnd();
    }

    public string GetBeastmasterCatalogProbe()
    {
        var builder = new StringBuilder()
            .AppendLine("種別: 魔獣図鑑クライアントプローブ")
            .AppendLine("モード: 読み取り専用（ウィンドウ非展開・コールバック不発生・メモリ非書き込み）")
            .AppendLine("説明: 現在存在するネイティブAddonのみ確認します。事前にゲーム内で魔獣図鑑を開いておく必要があります。")
            .AppendLine();

        var addonNames = new[] { "MonsterNote", "MobHunt", "MinionNotebook" };
        foreach (var addonName in addonNames)
        {
            try
            {
                var addon = DalamudApi.GameGui.GetAddonByName(addonName);
                builder.AppendLine($"Addon={addonName}");
                if (addon.IsNull)
                {
                    builder.AppendLine("  状態: なし");
                    continue;
                }

                builder.AppendLine($"  Address=0x{addon.Address.ToInt64():X}");
                builder.AppendLine($"  Name={addon.Name}");
                builder.AppendLine($"  Id={addon.Id} | ParentId={addon.ParentId} | HostId={addon.HostId}");
                builder.AppendLine($"  Ready={addon.IsReady} | Visible={addon.IsVisible}");
                builder.AppendLine($"  AtkValuesCount={addon.AtkValuesCount}");

                if (!addon.IsReady)
                {
                    continue;
                }

                var index = 0;
                foreach (var value in addon.AtkValues)
                {
                    string renderedValue;
                    try
                    {
                        renderedValue = value.GetValue()?.ToString() ?? "<null>";
                    }
                    catch (Exception ex)
                    {
                        renderedValue = $"<読み取り失敗: {ex.GetType().Name}>";
                    }

                    builder.AppendLine($"  Value[{index++}] Type={value.ValueType} Value={renderedValue}");
                }
            }
            catch (Exception ex)
            {
                builder.AppendLine($"  読み取り失敗: {ex.GetType().Name}: {ex.Message}");
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    public string FindRecommendedEquipmentIds()
    {
        var items = DalamudApi.DataManager.GetExcelSheet<Item>();
        var builder = new StringBuilder()
            .AppendLine("種別: おすすめ装備アイテムID")
            .AppendLine("説明: 装備画面は固定 ItemId により所持状態を検出します")
            .AppendLine();

        foreach (var plan in new[]
        {
            (Name: "攻略用装備", Entries: BeastmasterEquipmentGuide.Level50Starter),
            (Name: "BIS", Entries: BeastmasterEquipmentGuide.Level50BestInSlot),
        })
        {
            builder.AppendLine($"[{plan.Name}]");
            foreach (var equipment in plan.Entries.Where(entry => entry.Name != "装備なし" && entry.Name != "无装备"))
            {
                var exactMatches = items
                    .Where(item => item.RowId != 0 && item.Name.ExtractText().Equals(equipment.Name, StringComparison.Ordinal))
                    .OrderBy(item => item.RowId)
                    .ToArray();
                builder.AppendLine($"{equipment.Slot} | {equipment.Name}");
                if (exactMatches.Length > 0)
                {
                    foreach (var item in exactMatches)
                    {
                        builder.AppendLine($"  完全一致: ItemId={item.RowId} | {item.Name.ExtractText()}");
                    }
                }
                else
                {
                    var candidates = items
                        .Where(item => item.RowId != 0 && item.Name.ExtractText().Contains(equipment.Name, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(item => item.RowId)
                        .Take(10)
                        .ToArray();
                    if (candidates.Length == 0)
                    {
                        builder.AppendLine("  完全一致または候補が見つかりませんでした");
                    }
                    else
                    {
                        foreach (var item in candidates)
                        {
                            builder.AppendLine($"  候補: ItemId={item.RowId} | {item.Name.ExtractText()}");
                        }
                    }
                }
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    public string GetCurrentTargetDebug()
    {
        var target = DalamudApi.TargetManager.Target;
        if (target is not IBattleChara battleTarget)
        {
            return "種別: 現在のターゲット\n有効な BattleNpc ターゲットがありません。";
        }

        var player = DalamudApi.ObjectTable.LocalPlayer;
        var statuses = DalamudApi.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Status>();
        var builder = new StringBuilder()
            .AppendLine("種別: 現在のターゲット")
            .AppendLine($"名前: {battleTarget.Name.TextValue}")
            .AppendLine($"EntityId: {battleTarget.EntityId}")
            .AppendLine($"BaseId: {battleTarget.BaseId}")
            .AppendLine($"HP: {battleTarget.CurrentHp} / {battleTarget.MaxHp}")
            .AppendLine($"HP割合: {(battleTarget.MaxHp == 0 ? 0 : battleTarget.CurrentHp * 100f / battleTarget.MaxHp):0.##}%")
            .AppendLine($"ターゲット可能: {battleTarget.IsTargetable}")
            .AppendLine($"戦闘不能: {battleTarget.IsDead}")
            .AppendLine("ステータス:");

        foreach (var status in battleTarget.StatusList.OrderBy(status => status.StatusId))
        {
            var name = statuses.TryGetRow(status.StatusId, out var row) ? row.Name.ExtractText() : "";
            var sourceType = player != null && status.SourceId == player.EntityId ? "自身" : "他人/不明";
            builder.AppendLine($"  StatusId={status.StatusId} | {name} | SourceId={status.SourceId} | 付与者={sourceType} | 残り={status.RemainingTime:0.0}s");
        }

        return builder.ToString().TrimEnd();
    }

    public unsafe string GetComboDebug()
    {
        var manager = ActionManager.Instance();
        if (manager == null)
        {
            return "種別: 現在のコンボ\nActionManagerが利用不可。";
        }

        return new StringBuilder()
            .AppendLine("種別: 現在のコンボ")
            .AppendLine("モード: 読み取り専用")
            .AppendLine($"Combo.Action: {manager->Combo.Action}")
            .AppendLine($"Combo.Timer: {manager->Combo.Timer:0.000}s")
            .AppendLine("説明: Timer が 0 より大きい場合、コンボ受付時間が有効です。")
            .ToString()
            .TrimEnd();
    }

    public unsafe string GetActionStatusDebug(uint actionId, bool useAdjustedActionId)
    {
        var manager = ActionManager.Instance();
        if (manager == null)
        {
            return "種別: アクション状態\nActionManagerが利用不可。";
        }

        var target = DalamudApi.TargetManager.Target;
        var targetId = target?.GameObjectId ?? 0xE0000000UL;
        var availability = BeastmasterActionHelper.GetAvailability(actionId, targetId, useAdjustedActionId);
        if (availability.ActionId == 0)
        {
            return new StringBuilder()
                .AppendLine("種別: アクション状態")
                .AppendLine($"入力 ActionId: {actionId}")
                .AppendLine($"GetAdjustedActionId を使用: {(useAdjustedActionId ? "はい" : "いいえ")}")
                .AppendLine($"アクション: {availability.ActionName}")
                .AppendLine("使用可能: いいえ")
                .AppendLine($"判定: {availability.Reason}")
                .ToString()
                .TrimEnd();
        }

        var resolvedActionId = availability.ActionId;
        var player = DalamudApi.ObjectTable.LocalPlayer;
        var targetDistance = player != null && target != null
            ? Vector3.Distance(player.Position, target.Position)
            : (float?)null;
        var gauge = BeastmasterGaugeSnapshot.Read();
        var summon = gauge.SummonEntry;

        var recastTotal = 0f;
        var recastElapsed = 0f;
        var recastActive = false;
        var recastRemaining = 0f;
        var actionRange = 0f;
        try
        {
            recastTotal = manager->GetRecastTime(ActionType.Action, resolvedActionId);
            recastElapsed = manager->GetRecastTimeElapsed(ActionType.Action, resolvedActionId);
            recastActive = manager->IsRecastTimerActive(ActionType.Action, resolvedActionId);
            recastRemaining = recastActive ? Math.Max(0f, recastTotal - recastElapsed) : 0f;
            actionRange = ActionManager.GetActionRange(resolvedActionId);
        }
        catch
        {
            return new StringBuilder()
                .AppendLine("種別: アクション状態")
                .AppendLine($"入力 ActionId: {actionId}")
                .AppendLine($"GetAdjustedActionId を使用: {(useAdjustedActionId ? "はい" : "いいえ")}")
                .AppendLine($"実行 ActionId: {resolvedActionId}")
                .AppendLine($"アクション: {availability.ActionName}")
                .AppendLine("使用可能: いいえ")
                .AppendLine("判定: ネイティブAPI呼び出し時に例外が発生しました。ActionIdが現在の状態に適していない可能性があります")
                .ToString()
                .TrimEnd();
        }

        return new StringBuilder()
            .AppendLine("種別: アクション状態")
            .AppendLine($"入力 ActionId: {actionId}")
            .AppendLine($"GetAdjustedActionId を使用: {(useAdjustedActionId ? "はい" : "いいえ")}")
            .AppendLine($"実行 ActionId: {resolvedActionId}")
            .AppendLine($"アクション: {availability.ActionName}")
            .AppendLine($"使用可能: {(availability.CanUse ? "はい" : "いいえ")}")
            .AppendLine($"判定: {availability.Reason}")
            .AppendLine($"現在リキャスト: {recastRemaining:0.###}s / {recastTotal:0.###}s（経過 {recastElapsed:0.###}s）")
            .AppendLine($"射程: {actionRange:0.###} yalms")
            .AppendLine(targetDistance.HasValue
                ? $"ターゲット距離: {targetDistance.Value:0.###} yalms | {target!.Name.TextValue}"
                : "ターゲット距離: ターゲットまたはプレイヤーなし")
            .AppendLine(summon != null
                ? $"使役獣: {summon.Name} | 図鑑 {summon.Number:00} | DataId={gauge.SummonDataId}"
                : $"使役獣: {(string.IsNullOrWhiteSpace(gauge.SummonName) ? "未認識/未召喚" : gauge.SummonName)}")
            .ToString()
            .TrimEnd();
    }

    public unsafe string GetCooperationValidationDebug()
    {
        var gauge = BeastmasterGaugeSnapshot.ReadRaw();
        var target = DalamudApi.TargetManager.Target as IBattleChara;
        var manager = ActionManager.Instance();
        var builder = new StringBuilder()
            .AppendLine("種別: 魔獣使い連携検証データ")
            .AppendLine("モード: 読み取り専用（アクション非実行）")
            .AppendLine($"ジョブHUD: {(gauge.Available ? "利用可能" : gauge.Status)}")
            .AppendLine($"TP: {gauge.Tp}/250")
            .AppendLine($"魔獣技力: {gauge.BeastPower}/250")
            .AppendLine($"ビーストハート: {gauge.BeastHeartStacks} スタック")
            .AppendLine($"ビーストソウル: {gauge.BeastSoulStacks} スタック")
            .AppendLine($"現在の呼笛: {(gauge.WhistleIndex is >= 1 and <= 3 ? $"{gauge.WhistleIndex}号" : "未召喚")}");

        var entry = gauge.SummonEntry;
        if (entry == null)
        {
            builder.AppendLine("現在の使役獣: 未認識");
        }
        else
        {
            builder.AppendLine($"現在の使役獣: {entry.Name}");
            builder.AppendLine($"属性: {entry.Attribute}");
            builder.AppendLine($"獣心技データ: {entry.UltimateActionId} | {GetActionNameById(entry.UltimateActionId)}");
            builder.AppendLine($"はなつデータ: {entry.ReleaseActionId} | {GetActionNameById(entry.ReleaseActionId)}");
        }

        if (target == null)
        {
            builder.AppendLine("ターゲット: 有効な BattleNpc なし");
        }
        else
        {
            builder.AppendLine($"ターゲット: {target.Name.TextValue} | EntityId={target.EntityId} | BaseId={target.BaseId}");
            builder.AppendLine($"ターゲット HP: {target.CurrentHp}/{target.MaxHp} | ターゲット可能={target.IsTargetable} | 戦闘不能={target.IsDead}");
        }

        if (manager == null || target == null)
        {
            builder.AppendLine("アクション状態: ActionManagerまたはターゲットが利用不可");
        }
        else
        {
            builder.AppendLine("アクション状態:");
            foreach (var actionId in new uint[] { 47093, 44884, 44887, 44888, 44889 })
            {
                var status = manager->GetActionStatus(ActionType.Action, actionId, target.GameObjectId);
                builder.AppendLine($"  ActionId={actionId} | {GetActionNameById(actionId)} | ステータスコード={status}");
            }
        }

        builder.AppendLine("プレイヤー属性ステータス:");
        var player = DalamudApi.ObjectTable.LocalPlayer;
        if (player == null)
        {
            builder.AppendLine("  プレイヤーキャラクターが利用不可");
        }
        else
        {
            foreach (var status in player.StatusList.Where(status => status.StatusId is >= 4595 and <= 4600))
            {
                builder.AppendLine($"  StatusId={status.StatusId} | SourceId={status.SourceId} | 残り={status.RemainingTime:0.0}s");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static bool TryGetSummonSkills(uint dataId, out uint ultimateId, out uint releaseId)
    {
        var index = (int)dataId - 18915;
        if (index is < 1 or > 50)
        {
            ultimateId = 0;
            releaseId = 0;
            return false;
        }

        ultimateId = (uint)(44933 + index * 2);
        releaseId = ultimateId + 1;
        return true;
    }

    private static string GetActionName(Lumina.Excel.Sheets.Action action)
        => action.RowId == 0 ? "未検出" : action.Name.ExtractText();

    private static string GetActionNameById(uint actionId)
        => DalamudApi.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>().TryGetRow(actionId, out var action)
            ? GetActionName(action)
            : "未検出";

    public unsafe string GetBeastmasterGaugeRaw()
    {
        const uint beastmasterClassJobId = 43;
        if (DalamudApi.PlayerState.ClassJob.RowId != beastmasterClassJobId)
        {
            return "種別: 魔獣使いジョブHUD生データ\n先に魔獣使いにジョブチェンジしてください。";
        }

        var snapshot = BeastmasterGaugeSnapshot.ReadRaw();
        if (!snapshot.Available)
        {
            return $"種別: 魔獣使いジョブHUD生データ\n{snapshot.Status}。";
        }

        var address = snapshot.Address;
        var bytes = snapshot.Bytes.AsSpan();
        var length = bytes.Length;
        var uint16Values = new ushort[length / 2];
        var uint32Values = new uint[length / 4];
        for (var index = 0; index < uint16Values.Length; index++)
        {
            uint16Values[index] = BitConverter.ToUInt16(bytes.Slice(index * 2, 2));
        }

        for (var index = 0; index < uint32Values.Length; index++)
        {
            uint32Values[index] = BitConverter.ToUInt32(bytes.Slice(index * 4, 4));
        }

        var builder = new StringBuilder()
            .AppendLine("種別: 魔獣使いジョブHUD生データ")
            .AppendLine("モード: 読み取り専用（メモリ非書き込み）")
            .AppendLine($"ClassJob: {beastmasterClassJobId}")
            .AppendLine($"Address: 0x{address.ToInt64():X}")
            .AppendLine($"Length: {length} bytes")
            .AppendLine($"Hex: {string.Join(' ', bytes.ToArray().Select(value => value.ToString("X2")))}")
            .AppendLine($"UInt16: {string.Join(' ', uint16Values)}")
            .AppendLine($"UInt32: {string.Join(' ', uint32Values)}");
        return builder.ToString().TrimEnd();
    }

    private static string FormatMatches(
        string category,
        string query,
        IEnumerable<(uint RowId, string Name)> rows)
    {
        query = query.Trim();
        if (query.Length == 0)
        {
            return $"{category}\n名称キーワードを入力してください。";
        }

        var matches = rows
            .Where(row => row.RowId != 0
                && !string.IsNullOrWhiteSpace(row.Name)
                && row.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(row => row.RowId)
            .Take(ResultLimit + 1)
            .ToArray();
        var truncated = matches.Length > ResultLimit;

        var builder = new StringBuilder()
            .AppendLine($"種別: {category}")
            .AppendLine($"キーワード: {query}");
        foreach (var match in matches.Take(ResultLimit))
        {
            builder.Append("RowId=")
                .Append(match.RowId)
                .Append(" | ")
                .AppendLine(match.Name);
        }

        if (matches.Length == 0)
        {
            builder.AppendLine("一致する項目が見つかりませんでした。");
        }
        else if (truncated)
        {
            builder.AppendLine($"検索結果が {ResultLimit} 件を超えました。より具体的なキーワードを指定してください。");
        }

        return builder.ToString().TrimEnd();
    }

    public unsafe string GetCaptureCheckDebug()
    {
        var player = DalamudApi.ObjectTable.LocalPlayer;
        if (player == null)
        {
            return "種別: 「とらえる」判定\nキャラクター未ロード。";
        }

        if (player.ClassJob.RowId != 43)
        {
            return "種別: 「とらえる」判定\n現在のクラス・ジョブが魔獣使いではありません。";
        }

        if (DalamudApi.TargetManager.Target is not IBattleChara target)
        {
            return "種別: 「とらえる」判定\n有効なターゲットが選択されていません。";
        }

        var manager = ActionManager.Instance();
        if (manager == null)
        {
            return "種別: 「とらえる」判定\nActionManagerが利用不可。";
        }

        var captureActionId = 44880u;

        var actionStatus = captureActionId != 0
            ? manager->GetActionStatus(ActionType.Action, captureActionId, target.GameObjectId)
            : 0xFFFFFFFF;

        var statuses = DalamudApi.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Status>();
        var hpPercent = target.MaxHp == 0
            ? 100f
            : target.CurrentHp * 100f / target.MaxHp;

        var builder = new StringBuilder()
            .AppendLine("種別: 「とらえる」判定")
            .AppendLine($"ターゲット: {target.Name.TextValue} | BaseId={target.BaseId}")
            .AppendLine($"HP: {target.CurrentHp}/{target.MaxHp} ({hpPercent:0.#}%)")
            .AppendLine($"ターゲット可能: {target.IsTargetable} | 戦闘不能: {(target.IsDead || target.CurrentHp == 0)}")
            .AppendLine($"「とらえる」ActionId: {(captureActionId != 0 ? captureActionId.ToString() : "未検出")}")
            .AppendLine($"GetActionStatus ステータスコード: {(actionStatus == 0 ? "0（実行可能）" : actionStatus.ToString())}")
            .AppendLine($"ゲーム内実行判定: {(actionStatus == 0 ? "可能" : "不可")}")
            .AppendLine()
            .AppendLine("ターゲットの現在のステータス一覧:");

        if (!target.StatusList.Any())
        {
            builder.AppendLine("  （ステータスなし）");
        }
        else
        {
            foreach (var s in target.StatusList.OrderBy(s => s.StatusId))
            {
                var statusName = statuses?.TryGetRow(s.StatusId, out var row) == true
                    ? row.Name.ExtractText()
                    : "";
                var isSource = s.SourceId == player.EntityId;
                builder.AppendLine($"  StatusId={s.StatusId} | {statusName} | 残り={s.RemainingTime:0.0}s | 付与者={(isSource ? "自身" : $"他人({s.SourceId})")}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    public string GetAutoOutputConditionDebug()
    {
        var player = DalamudApi.ObjectTable.LocalPlayer;
        var builder = new StringBuilder()
            .AppendLine("種別: 自動出力キャラクター状態診断")
            .AppendLine($"ログイン済み: {DalamudApi.ClientState.IsLoggedIn}")
            .AppendLine($"キャラクターロード済み: {player != null}")
            .AppendLine($"クラス・ジョブ ID: {DalamudApi.PlayerState.ClassJob.RowId}")
            .AppendLine($"BetweenAreas（エリア移動中）: {DalamudApi.Condition[ConditionFlag.BetweenAreas]}")
            .AppendLine($"Mounted（騎乗中）: {DalamudApi.Condition[ConditionFlag.Mounted]}")
            .AppendLine($"OccupiedInCutSceneEvent（イベント中）: {DalamudApi.Condition[ConditionFlag.OccupiedInCutSceneEvent]}")
            .AppendLine($"InCombat（戦闘中）: {DalamudApi.Condition[ConditionFlag.InCombat]}");

        if (player == null)
        {
            return builder.ToString().TrimEnd();
        }

        builder.AppendLine($"キャラクター HP: {player.CurrentHp}/{player.MaxHp}")
            .AppendLine($"詠唱中: {player.IsCasting}")
            .AppendLine($"オブジェクト種別: {player.ObjectKind}")
            .AppendLine($"現在のターゲット: {DalamudApi.TargetManager.Target?.Name.TextValue ?? "なし"}");
        return builder.ToString().TrimEnd();
    }

    public unsafe string GetSkillSequenceValidationDebug()
    {
        const uint borrowActionId = 44895;
        const uint beastSkillActionId = 44886;
        const uint releaseActionId = 44890;
        const uint beastHideActionId = 44896;
        const uint shieldChargeActionId = 44893;

        var gauge = BeastmasterGaugeSnapshot.Read();
        var countdown = countdownService.Snapshot;
        var player = DalamudApi.ObjectTable.LocalPlayer;
        var target = DalamudApi.TargetManager.Target;
        var manager = ActionManager.Instance();
        var builder = new StringBuilder()
            .AppendLine("種別: スキルシーケンス検証データ")
            .AppendLine("モード: 読み取り専用（アクション非実行）")
            .AppendLine($"ログイン状態: {DalamudApi.ClientState.IsLoggedIn}")
            .AppendLine($"ジョブID: {DalamudApi.PlayerState.ClassJob.RowId}")
            .AppendLine($"戦闘中（InCombat）: {DalamudApi.Condition[ConditionFlag.InCombat]}")
            .AppendLine($"エリア移動中（BetweenAreas）: {DalamudApi.Condition[ConditionFlag.BetweenAreas]}")
            .AppendLine($"プレイヤーHP: {(player == null ? "未ロード" : $"{player.CurrentHp}/{player.MaxHp}")}")
            .AppendLine($"プレイヤー詠唱中: {player?.IsCasting}")
            .AppendLine($"現在のターゲット: {target?.Name.TextValue ?? "なし"} | GameObjectId={(target?.GameObjectId.ToString() ?? "0")}")
            .AppendLine($"現在の呼笛: {(gauge.WhistleIndex is >= 1 and <= 3 ? $"{gauge.WhistleIndex}号呼笛" : "未召喚")}")
            .AppendLine($"現在の使役獣: {(gauge.SummonEntry?.Name ?? gauge.SummonName)} | DataId={gauge.SummonDataId}")
            .AppendLine($"ゲーム内カウントダウン利用可能: {countdown.Available} | 状態={countdown.Status}")
            .AppendLine($"ゲーム内カウントダウン作動中: {countdown.Active} | 残り={countdown.TimeRemaining:0.000}s | 開始者={countdown.Initiator}")
            .AppendLine($"キャッシュサンプリング時間 UTC: {(countdownService.LastPolledUtc == DateTime.MinValue ? "未サンプリング" : countdownService.LastPolledUtc.ToString("O"))}")
            .AppendLine($"直近のカウントダウン遷移: {countdownService.LastTransition} | 時間 UTC={(countdownService.LastTransitionUtc == DateTime.MinValue ? "なし" : countdownService.LastTransitionUtc.ToString("O"))}")
            .AppendLine();

        if (manager == null)
        {
            builder.AppendLine("ActionManager: 利用不可");
        }
        else
        {
            AppendSequenceAction(builder, manager, "かりる", borrowActionId, 0);
            AppendSequenceAction(builder, manager, "魔獣技", beastSkillActionId, 0);
            AppendSequenceAction(builder, manager, "はなつ", releaseActionId, target?.GameObjectId ?? 0);
            AppendSequenceAction(builder, manager, "百獣の皮 期待値", beastHideActionId, 0, adjust: false);
            AppendSequenceAction(builder, manager, "シールドチャージ", shieldChargeActionId, target?.GameObjectId ?? 0, adjust: false);
        }

        return builder.ToString().TrimEnd();
    }

    private static unsafe void AppendSequenceAction(
        StringBuilder builder,
        ActionManager* manager,
        string label,
        uint actionId,
        ulong targetId,
        bool adjust = true)
    {
        var adjustedActionId = adjust ? manager->GetAdjustedActionId(actionId) : actionId;
        var status = adjustedActionId == 0
            ? uint.MaxValue
            : manager->GetActionStatus(ActionType.Action, adjustedActionId, targetId);
        builder.AppendLine(
            $"{label}: Base={actionId} | Adjusted={adjustedActionId} | {GetActionNameById(adjustedActionId)} | Target={targetId} | Status={status}");
    }

    private static string JoinLines(params string[] lines)
        => string.Join(Environment.NewLine, lines);

    private static string NormalizeDutyName(string name)
        => new(name.Where(character => !char.IsWhiteSpace(character)
            && character is not '·' and not '：' and not ':').ToArray());
}
