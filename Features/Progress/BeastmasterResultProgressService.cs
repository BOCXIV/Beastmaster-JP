using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Beastmaster;

public sealed unsafe class BeastmasterResultProgressService : IDisposable
{
    private const string AddonName = "XBMResult";
    private const int MaximumSlotCount = 15;
    private const int IconStartIndex = 73;
    private const int ExperienceAfterStartIndex = 105;
    private const int LevelAfterStartIndex = 137;
    private const uint IconBase = 242000;

    private readonly BeastmasterProgressService progressService;
    private long nextReadAt;
    private long pendingSince;
    private string pendingSnapshot = string.Empty;
    private string lastAppliedSnapshot = string.Empty;
    private Dictionary<int, (int Level, int Experience, int ExperienceRequired)> pendingUpdates = [];

    public BeastmasterResultProgressService(BeastmasterProgressService progressService)
    {
        this.progressService = progressService;
    }

    public void Start() => DalamudApi.Framework.Update += OnFrameworkUpdate;

    public void Dispose()
    {
        DalamudApi.Framework.Update -= OnFrameworkUpdate;
        ApplyPendingSnapshot("プラグインアンロード前");
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        try
        {
            if (Environment.TickCount64 < nextReadAt)
            {
                return;
            }

            nextReadAt = Environment.TickCount64 + 250;
            var characterKey = progressService.CurrentCharacterKey;
            var addon = (AtkUnitBase*)DalamudApi.GameGui.GetAddonByName(AddonName, 1).Address;
            if (characterKey.Length == 0)
            {
                ClearPendingSnapshot();
                return;
            }

            if (addon == null || !addon->IsVisible || addon->AtkValues == null
                || addon->AtkValuesCount <= LevelAfterStartIndex)
            {
                ApplyPendingSnapshot("リザルト画面終了時");
                return;
            }

            var updates = new Dictionary<int, (int Level, int Experience, int ExperienceRequired)>();
            var slotCount = Math.Min(
                MaximumSlotCount,
                Math.Min(
                    addon->AtkValuesCount - IconStartIndex,
                    Math.Min(
                        addon->AtkValuesCount - ExperienceAfterStartIndex,
                        addon->AtkValuesCount - LevelAfterStartIndex)));
            for (var slot = 0; slot < slotCount; slot++)
            {
                var icon = ReadNumber(addon->AtkValues[IconStartIndex + slot]);
                var level = ReadNumber(addon->AtkValues[LevelAfterStartIndex + slot]);
                var experience = ReadNumber(addon->AtkValues[ExperienceAfterStartIndex + slot]);
                if (icon is > IconBase and <= IconBase + 50
                    && level is >= 1 and <= 25
                    && experience <= 99)
                {
                    updates[(int)(icon - IconBase)] = level == 25
                        ? ((int)level, 0, 0)
                        : ((int)level, (int)experience, 100);
                }
            }

            if (updates.Count == 0)
            {
                return;
            }

            var snapshot = characterKey + '|' + string.Join(';', updates.OrderBy(pair => pair.Key)
                .Select(pair => $"{pair.Key}:{pair.Value.Level}:{pair.Value.Experience}:{pair.Value.ExperienceRequired}"));
            if (snapshot != pendingSnapshot)
            {
                pendingSnapshot = snapshot;
                pendingUpdates = new Dictionary<int, (int Level, int Experience, int ExperienceRequired)>(updates);
                pendingSince = Environment.TickCount64;
                return;
            }

            if (Environment.TickCount64 - pendingSince < 500 || snapshot == lastAppliedSnapshot)
            {
                return;
            }

            ApplyPendingSnapshot("500ms安定後");
        }
        catch (Exception ex)
        {
            ClearPendingSnapshot();
            DalamudApi.Log.Warning(ex, "闘獣リザルトのレベル・経験値の読み取りに失敗しました。");
        }
    }

    private void ApplyPendingSnapshot(string reason)
    {
        if (pendingSnapshot.Length == 0
            || pendingUpdates.Count == 0
            || pendingSnapshot == lastAppliedSnapshot)
        {
            ClearPendingSnapshot();
            return;
        }

        lastAppliedSnapshot = pendingSnapshot;
        var changed = progressService.UpdateBeastProgress(pendingUpdates, saveInBackground: true);
        if (changed > 0)
        {
            DalamudApi.Log.Information(
                "闘獣リザルトは{Reason}に {Count} 体の魔獣のレベル・経験値を同期しました。",
                reason,
                changed);
        }

        ClearPendingSnapshot();
    }

    private void ClearPendingSnapshot()
    {
        pendingSnapshot = string.Empty;
        pendingUpdates.Clear();
        pendingSince = 0;
    }

    private static uint ReadNumber(AtkValue value)
        => value.TypeCode() switch
        {
            3 when value.Int >= 0 => (uint)value.Int,
            4 or 5 => value.UInt,
            _ => uint.MaxValue,
        };
}

