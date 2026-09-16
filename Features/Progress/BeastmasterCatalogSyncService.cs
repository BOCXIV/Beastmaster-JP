using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Beastmaster;

public sealed unsafe class BeastmasterCatalogSyncService
{
    private const string AddonName = "XBMMonsterNotebook";
    private const int PageCount = 2;
    private const int EntriesPerPage = 25;
    private const int MaxAtkValues = 4096;
    private const int PageValueIndex = 10;
    private const int TotalValueIndex = 7;
    private const int FirstEntryValueIndex = 24;
    private const int EntryStride = 8;
    private const uint CapturedIconBase = 242000;
    private const uint MissingIcon = 242051;

    private readonly BeastmasterProgressService progressService;
    private readonly Dictionary<int, bool> states = new();
    private int requestedPage = -1;
    private int pageRequestAttempts;
    private long nextActionAt;
    private long scanStartedAt;
    private bool scanning;
    private string status = "未同期。";
    private int? expectedCapturedTotal;

    public BeastmasterCatalogSyncService(BeastmasterProgressService progressService)
    {
        this.progressService = progressService;
    }

    public string Status => status;
    public bool IsScanning => scanning;
    public string Diagnostic { get; private set; } = string.Empty;

    public void RequestSync()
    {
        if (progressService.CurrentCharacterKey.Length == 0)
        {
            status = "キャラクターでログインしてください。";
            return;
        }

        states.Clear();
        requestedPage = -1;
        pageRequestAttempts = 0;
        nextActionAt = 0;
        scanStartedAt = Environment.TickCount64;
        expectedCapturedTotal = null;
        Diagnostic = string.Empty;
        scanning = true;
        status = "魔獣手帳を読み込み中…";
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
                Stop("ログアウトしたため、同期をキャンセルしました。", clearStates: true);
                return;
            }

            var addon = GetAddon();
            if (addon == null)
            {
                if (Environment.TickCount64 >= nextActionAt)
                {
                    OpenNotebook();
                    nextActionAt = Environment.TickCount64 + 500;
                }

                return;
            }

            var page = ReadPage(addon);
            if (expectedCapturedTotal is null)
            {
                expectedCapturedTotal = page.CapturedTotal;
            }
            else if (expectedCapturedTotal != page.CapturedTotal)
            {
                throw new InvalidOperationException("魔獣手帳のページ間で捕獲総数が一致しません。");
            }

            foreach (var entry in page.Entries)
            {
                states[entry.Number] = entry.Captured;
            }

            if (states.Count == BeastmasterCatalog.Entries.Count)
            {
                var unlocked = states.Where(pair => pair.Value).Select(pair => pair.Key).ToHashSet();
                if (expectedCapturedTotal != unlocked.Count)
                {
                    throw new InvalidOperationException("魔獣手帳の状態と捕獲総数が一致しません。");
                }

                var changed = progressService.ReplaceCatalogProgress(unlocked);
                Stop($"同期完了：登録済み {unlocked.Count}/{BeastmasterCatalog.Entries.Count} 体、更新 {changed} 件。", clearStates: false);
                DalamudApi.ChatGui.Print($"[魔獣使いアシスト] 登録済み魔獣の同期が完了しました：登録済み {unlocked.Count}/{BeastmasterCatalog.Entries.Count} 体、更新 {changed} 件。");
                return;
            }

            var currentPageValue = GetPage(addon);
            if (currentPageValue is not (0 or 1))
            {
                status = "魔獣手帳のページデータ更新待ち…";
                return;
            }

            var currentPage = currentPageValue.Value;

            if (requestedPage >= 0)
            {
                if (currentPage == requestedPage)
                {
                    requestedPage = -1;
                    pageRequestAttempts = 0;
                }
                else if (Environment.TickCount64 >= nextActionAt)
                {
                    if (pageRequestAttempts >= 3)
                    {
                        throw new InvalidOperationException("魔獣手帳のページめくりに応答がありません。");
                    }

                    pageRequestAttempts++;
                    nextActionAt = Environment.TickCount64 + 500;
                    if (!RequestPage(addon, (uint)requestedPage))
                    {
                        status = $"ページめくり再試行中 ({pageRequestAttempts}/3)…";
                        return;
                    }

                    status = $"{requestedPage + 1} ページの更新待ち…";
                    return;
                }
            }

            if (Environment.TickCount64 < nextActionAt)
            {
                return;
            }

            requestedPage = currentPage == 0 ? 1 : 0;
            pageRequestAttempts = 1;
            nextActionAt = Environment.TickCount64 + 500;
            if (!RequestPage(addon, (uint)requestedPage))
            {
                status = "ページめくりを再試行します…";
                return;
            }

            status = $"{requestedPage + 1} ページの更新待ち…";
        }
        catch (InvalidOperationException ex) when (Environment.TickCount64 - scanStartedAt < 15000)
        {
            Diagnostic = ex.Message;
            nextActionAt = Environment.TickCount64 + 250;
            status = $"手帳データ更新待ち…{ex.Message}";
        }
        catch (Exception ex)
        {
            Diagnostic = ex.Message;
            Stop($"同期失敗（進捗は変更されませんでした）：{ex.Message}", clearStates: true);
            DalamudApi.ChatGui.Print($"[魔獣使いアシスト] 登録済み魔獣の同期に失敗しました：{ex.Message}");
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

    private static unsafe CatalogPage ReadPage(AtkUnitBase* addon)
    {
        if (addon->AtkValues == null || addon->AtkValuesCount > MaxAtkValues || addon->AtkValuesCount <= FirstEntryValueIndex + (EntriesPerPage - 1) * EntryStride + 5)
        {
            throw new InvalidOperationException("魔獣手帳のフィールド数検証に失敗しました。");
        }

        var page = GetPage(addon) ?? throw new InvalidOperationException("魔獣手帳のページ番号が更新されていません。");
        var totalText = ReadString(addon->AtkValues[TotalValueIndex]);
        var parts = totalText.Split('/');
        if (parts.Length != 2 || parts[1] != "50" || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var capturedTotal))
        {
            throw new InvalidOperationException("魔獣手帳の総数検証に失敗しました。");
        }

        var result = new List<(int Number, bool Captured)>(EntriesPerPage);
        for (var index = 0; index < EntriesPerPage; index++)
        {
            var valueIndex = FirstEntryValueIndex + index * EntryStride;
            var numberValue = addon->AtkValues[valueIndex];
            var capturedValue = addon->AtkValues[valueIndex + 2];
            var iconValue = addon->AtkValues[valueIndex + 4];
            var textValue = addon->AtkValues[valueIndex + 5];
            var number = 1 + page * EntriesPerPage + index;
            var captured = capturedValue.TypeCode() == 2 && capturedValue.Bool;

            var expectedIcon = captured ? CapturedIconBase + (uint)number : MissingIcon;
            if (numberValue.TypeCode() != 5 || numberValue.UInt != number
                || capturedValue.TypeCode() != 2
                || iconValue.TypeCode() != 5
                || iconValue.UInt != expectedIcon)
            {
                throw new InvalidOperationException(
                    $"魔獣手帳 No.{number:00} の構造検証に失敗しました。\n"
                    + $"ページ={page}、AtkValuesCount={addon->AtkValuesCount}\n"
                    + $"番号フィールド：{FormatValue(numberValue)}、期待値 TypeCode=5 UInt={number}\n"
                    + $"捕獲フィールド：{FormatValue(capturedValue)}、期待値 TypeCode=2 Bool={captured}\n"
                    + $"アイコンフィールド：{FormatValue(iconValue)}、期待値 TypeCode=5 UInt={expectedIcon}\n"
                    + $"テキストフィールド（診断用）：{FormatValue(textValue)}");
            }

            result.Add((number, captured));
        }

        var pageCaptured = result.Count(entry => entry.Captured);
        if (pageCaptured > capturedTotal || pageCaptured > EntriesPerPage)
        {
            throw new InvalidOperationException("魔獣手帳の捕獲数検証に失敗しました。");
        }

        return new CatalogPage(result, capturedTotal);
    }

    private unsafe void OpenNotebook()
    {
        var module = AgentModule.Instance();
        if (module == null)
        {
            throw new InvalidOperationException("ゲームUIがロードされていません。");
        }

        var agent = module->GetAgentByInternalId((AgentId)500);
        if (agent == null)
        {
            throw new InvalidOperationException("魔獣手帳UIが利用不可です。");
        }

        agent->Show();
    }

    private static unsafe bool RequestPage(AtkUnitBase* addon, uint page)
    {
        var values = stackalloc AtkValue[2];

        var callback = new AtkValue();
        callback.Type = (AtkValueType)3;
        callback.Int = 3;
        Unsafe.Write(values, callback);

        var pageValue = new AtkValue();
        pageValue.Type = (AtkValueType)5;
        pageValue.UInt = page;
        Unsafe.Write(values + 1, pageValue);

        return addon->FireCallback(2, values, true);
    }

    private static unsafe string ReadString(AtkValue value)
    {
        var type = value.TypeCode();
        if (type is not (8 or 10))
        {
            return string.Empty;
        }

        return value.String.ToString() ?? string.Empty;
    }

    private static unsafe string FormatValue(AtkValue value)
    {
        var text = ReadString(value).Replace("\r", "\\r").Replace("\n", "\\n");
        return $"Type=0x{(int)value.Type:X}, TypeCode={value.TypeCode()}, UInt={value.UInt}, Int={value.Int}, Bool={value.Bool}, String=\"{text}\"";
    }

    private void Stop(string message, bool clearStates)
    {
        scanning = false;
        requestedPage = -1;
        pageRequestAttempts = 0;
        scanStartedAt = 0;
        if (clearStates)
        {
            states.Clear();
        }

        status = message;
    }

    private sealed record CatalogPage(IReadOnlyList<(int Number, bool Captured)> Entries, int CapturedTotal);
}

internal static class BeastmasterAtkValueExtensions
{
    public static unsafe int TypeCode(this AtkValue value) => (int)value.Type & 0xF;
}
