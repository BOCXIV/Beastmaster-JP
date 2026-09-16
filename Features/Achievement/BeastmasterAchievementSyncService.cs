using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace Beastmaster;

public sealed unsafe class BeastmasterAchievementSyncService
{
    private const long TimeoutMs = 15000;
    private readonly BeastmasterProgressService progressService;
    private bool scanning;
    private long scanStartedAt;
    private long nextActionAt;
    private string status = "未同期。";

    public BeastmasterAchievementSyncService(BeastmasterProgressService progressService)
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
            status = "キャラクターにログインしてください。";
            return;
        }

        scanning = true;
        scanStartedAt = Environment.TickCount64;
        nextActionAt = 0;
        Diagnostic = string.Empty;
        status = "現在のキャラクターのアチーブメント達成状況を読み取り中…";
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
                throw new InvalidOperationException("同期タイムアウト：アチーブメントデータが時間内に読み込めませんでした。");
            }

            var achievement = Achievement.Instance();
            if (achievement == null)
            {
                throw new InvalidOperationException("アチーブメントシステムが利用できません。");
            }

            if (!achievement->IsLoaded())
            {
                if (Environment.TickCount64 >= nextActionAt)
                {
                    achievement->RequestCompletedAchievements();
                    nextActionAt = Environment.TickCount64 + 500;
                }

                status = "アチーブメントデータの読み込み待機中…";
                return;
            }

            var completedIds = new HashSet<int>();
            foreach (var group in BeastmasterAchievementCatalog.Groups)
            {
                foreach (var achievementId in group.AchievementIds)
                {
                    if (achievement->IsComplete(achievementId))
                    {
                        completedIds.Add(achievementId);
                    }
                }
            }

            var changed = progressService.ReplaceAchievementProgress(completedIds);
            Stop($"同期完了：達成済み {completedIds.Count}/{BeastmasterAchievementCatalog.AchievementCount} 件、更新 {changed} 件。");
        }
        catch (Exception ex)
        {
            Diagnostic = ex.Message;
            Stop($"同期失敗、進捗は変更されませんでした：{ex.Message}");
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

    private void Stop(string message)
    {
        scanning = false;
        scanStartedAt = 0;
        status = message;
    }
}

