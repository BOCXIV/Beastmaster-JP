using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Beastmaster;

public sealed unsafe class BeastmasterNotebookSyncService
{
    private const string AddonName = "XBMMonsterNotebook";
    private const int PageValueIndex = 10;
    private const int NumberTextIndex = 229;
    private const int IconIndex = 231;
    private const int LevelIndex = 258;
    private const int ExperienceIndex = 261;
    private const int ExperienceRequiredIndex = 262;
    private const int EntriesPerPage = 25;
    private const int PageCount = 2;
    private const uint IconBase = 242000;
    private const long TimeoutMs = 20000;

    private readonly BeastmasterProgressService progressService;
    private readonly Dictionary<int, (int Level, int Experience, int ExperienceRequired)> results = new();
    private bool scanning;
    private long scanStartedAt;
    private long nextActionAt;
    private int memberIndex;
    private bool selectSentForCurrent;
    private string pendingSnapshot = string.Empty;
    private string stableSnapshot = string.Empty;
    private int invalidLevelFrames;
    private string status = "未同期。";

    public BeastmasterNotebookSyncService(BeastmasterProgressService progressService)
    {
        this.progressService = progressService;
    }

    public string Status => status;
    public bool IsScanning => scanning;
    public string Diagnostic { get; private set; } = string.Empty;
    public int ProgressCount => results.Count;
    public int TotalCount => EntriesPerPage * PageCount;

    public void RequestSync()
    {
        if (progressService.CurrentCharacterKey.Length == 0)
        {
            status = "キャラクターにログインしてください。";
            return;
        }

        results.Clear();
        memberIndex = 0;
        selectSentForCurrent = false;
        pendingSnapshot = string.Empty;
        stableSnapshot = string.Empty;
        invalidLevelFrames = 0;
        scanStartedAt = Environment.TickCount64;
        nextActionAt = 0;
        Diagnostic = string.Empty;
        scanning = true;
        status = "労妲と会話して魔獣手帳を開いた後、同期を開始してください…";
    }

    public void Update()
    {
        if (!scanning)
        {
            return;
        }

        try
        {
            if (progressService.CurrentCharacterKey.Length == 0)
            {
                Stop("キャラクターがログアウトしたため、同期を中止しました。");
                return;
            }

            if (Environment.TickCount64 - scanStartedAt >= TimeoutMs)
            {
                throw new InvalidOperationException($"同期タイムアウト：{DescribeStuckState()}");
            }

            var addon = GetAddon();
            if (addon == null)
            {
                status = "労妲と会話して魔獣手帳を開いてから同期してください。";
                return;
            }

            var page = GetPage(addon);
            if (page == null)
            {
                status = "手帳のページ番号の更新待機中…";
                return;
            }

            if (memberIndex >= EntriesPerPage * PageCount)
            {
                Finish();
                return;
            }

            var targetPage = memberIndex / EntriesPerPage;
            if (page != targetPage)
            {
                selectSentForCurrent = false;
                pendingSnapshot = string.Empty;
                stableSnapshot = string.Empty;
                if (Environment.TickCount64 >= nextActionAt)
                {
                    if (RequestPage(addon, (uint)targetPage))
                    {
                        status = $"第 {targetPage + 1} ページへ移動中…";
                    }
                    else
                    {
                        status = "手帳のページ移動が受け付けられませんでした。再試行中…";
                    }

                    nextActionAt = Environment.TickCount64 + 200;
                }

                return;
            }

            var pageIndex = memberIndex % EntriesPerPage;
            if (!selectSentForCurrent && Environment.TickCount64 >= nextActionAt)
            {
                if (!SendSelectEvent((uint)pageIndex))
                {
                    throw new InvalidOperationException("手帳の選択イベント送信に失敗しました。");
                }

                selectSentForCurrent = true;
                nextActionAt = Environment.TickCount64 + 100;
            }

            var detail = ReadDetail(addon, memberIndex + 1);
            if (detail == null)
            {
                return;
            }

            results[memberIndex + 1] = detail.Value;
            memberIndex++;
            selectSentForCurrent = false;
            pendingSnapshot = string.Empty;
            stableSnapshot = string.Empty;
            nextActionAt = 0;
            status = $"読み込み済み {memberIndex}/{EntriesPerPage * PageCount} 体…";
        }
        catch (Exception ex)
        {
            Diagnostic = ex.Message;
            Stop($"同期失敗：{ex.Message}");
            DalamudApi.ChatGui.Print($"[魔獣使いアシスト] 獣レベル・経験値の同期に失敗しました：{ex.Message}");
        }
    }

    public void Start()
    {
        DalamudApi.Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        DalamudApi.Framework.Update -= OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        Update();
    }

    private unsafe AtkUnitBase* GetAddon()
    {
        var address = (AtkUnitBase*)DalamudApi.GameGui.GetAddonByName(AddonName, 1).Address;
        return address != null && address->IsVisible ? address : null;
    }

    private static unsafe int? GetPage(AtkUnitBase* addon)
    {
        if (addon == null || addon->AtkValues == null || addon->AtkValuesCount <= PageValueIndex)
        {
            return null;
        }

        var value = addon->AtkValues[PageValueIndex];
        return ((int)value.Type & 0xF) == 5 && value.UInt <= 1 ? (int)value.UInt : null;
    }

    private static unsafe bool RequestPage(AtkUnitBase* addon, uint page)
    {
        var values = stackalloc AtkValue[2];
        values[0] = new AtkValue { Type = (AtkValueType)3, Int = 3 };
        values[1] = new AtkValue { Type = (AtkValueType)5, UInt = page };
        return addon->FireCallback(2, values, true);
    }

    private static unsafe bool SendSelectEvent(uint pageIndex)
    {
        var module = AgentModule.Instance();
        var agent = module == null ? null : module->GetAgentByInternalId((AgentId)500);
        if (agent == null)
        {
            return false;
        }

        var args = stackalloc AtkValue[2];
        args[0] = new AtkValue { Type = (AtkValueType)3, Int = 5 };
        args[1] = new AtkValue { Type = (AtkValueType)3, Int = (int)pageIndex };
        var result = new AtkValue();
        agent->ReceiveEvent(&result, args, 2, 0);
        return true;
    }

    private unsafe (int Level, int Experience, int ExperienceRequired)? ReadDetail(AtkUnitBase* addon, int expectedNumber)
    {
        if (addon->AtkValues == null || addon->AtkValuesCount <= ExperienceRequiredIndex)
        {
            return null;
        }

        var number = ReadNumber(addon->AtkValues[NumberTextIndex]);
        var icon = ReadNumber(addon->AtkValues[IconIndex]);
        if (number != expectedNumber || icon != IconBase + (uint)number)
        {
            pendingSnapshot = string.Empty;
            stableSnapshot = string.Empty;
            return null;
        }

        var level = ReadNumber(addon->AtkValues[LevelIndex]);
        var experience = ReadNumber(addon->AtkValues[ExperienceIndex]);
        var required = ReadNumber(addon->AtkValues[ExperienceRequiredIndex]);
        if (level is < 1 or > 25)
        {
            pendingSnapshot = string.Empty;
            stableSnapshot = string.Empty;
            invalidLevelFrames++;
            if (invalidLevelFrames >= 40)
            {
                throw new InvalidOperationException("現在開いている画面はゲーム内の魔獣手帳ではありません（獣レベルが無効です）。労妲と会話して魔獣手帳を開いてから同期してください。");
            }

            return null;
        }

        invalidLevelFrames = 0;

        if (level == 25)
        {
            experience = 0;
            required = 0;
        }
        else if (required is < 1 or > 999999 || experience >= required)
        {
            pendingSnapshot = string.Empty;
            stableSnapshot = string.Empty;
            return null;
        }

        var snapshot = $"{number}:{level}:{experience}:{required}";
        if (snapshot != pendingSnapshot)
        {
            pendingSnapshot = snapshot;
            stableSnapshot = string.Empty;
            return null;
        }

        if (snapshot == stableSnapshot)
        {
            return null;
        }

        stableSnapshot = snapshot;
        return ((int)level, (int)experience, (int)required);
    }

    private void Finish()
    {
        var changed = progressService.UpdateBeastProgress(results);
        Stop($"同期完了：読み込み済み {results.Count}/{EntriesPerPage * PageCount} 体、更新 {changed} 件。");
        DalamudApi.ChatGui.Print($"[魔獣使いアシスト] 獣レベル・経験値の同期が完了しました：読み込み済み {results.Count}/{EntriesPerPage * PageCount} 体、更新 {changed} 件。");
    }

    private unsafe string DescribeStuckState()
    {
        var addon = GetAddon();
        if (addon == null)
        {
            return $"手帳が開かれていません（読み込み済み {memberIndex}/50 体）。";
        }

        var page = GetPage(addon);
        var targetPage = memberIndex / EntriesPerPage;
        var number = addon->AtkValues != null && addon->AtkValuesCount > NumberTextIndex
            ? ReadNumber(addon->AtkValues[NumberTextIndex])
            : uint.MaxValue;
        var level = addon->AtkValues != null && addon->AtkValuesCount > LevelIndex
            ? ReadNumber(addon->AtkValues[LevelIndex])
            : uint.MaxValue;
        return $"読み込み済み {memberIndex}/50 体、現在ページ={page?.ToString() ?? "?"}、目標第 {memberIndex + 1} 体（目標ページ {targetPage}）、詳細番号={number}、獣レベル={level}。";
    }

    private void Stop(string message)
    {
        scanning = false;
        scanStartedAt = 0;
        status = message;
    }

    private static uint ReadNumber(AtkValue value)
        => value.TypeCode() switch
        {
            3 when value.Int >= 0 => (uint)value.Int,
            4 or 5 => value.UInt,
            8 or 10 when uint.TryParse(value.String.ToString(), out var parsed) => parsed,
            _ => uint.MaxValue,
        };
}

