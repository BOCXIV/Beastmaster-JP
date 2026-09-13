namespace Beastmaster;

public static class BeastmasterGuide
{
    public static IReadOnlyList<BeastmasterStage> Stages { get; } =
    [
        new(
            "sample-introduction",
            "サンプルフェーズ1：魔獣使い入門",
            "フェーズ機能および手動進捗機能の検証用であり、実際のゲーム内進行とは異なります。",
            [
                new("sample-introduction-read", "サンプル説明を読む", BeastmasterObjectiveType.Explore, "一時的なサンプルデータであることを確認します。"),
                new("sample-introduction-npc", "サンプルNPCと話す", BeastmasterObjectiveType.TalkToNpc, "チェックボックスを手動で切り替えて個別目標の進捗をテストします。"),
                new("sample-introduction-quest", "サンプルクエストを完了する", BeastmasterObjectiveType.Quest, "このクエスト名および内容はプレースホルダーです。"),
            ]),
        new(
            "sample-training",
            "サンプルフェーズ2：基礎訓練",
            "フェーズ間の切り替えと達成度の個別管理を検証するためのものです。",
            [
                new("sample-training-monster", "サンプルモンスターを討伐", BeastmasterObjectiveType.Monster, "自動追跡は行われません。手動でチェックを入れてください。"),
                new("sample-training-item", "サンプルアイテムを入手", BeastmasterObjectiveType.Item, "所持品の自動検出は行われません。手動でチェックを入れてください。"),
                new("sample-training-duty", "サンプルコンテンツを攻略", BeastmasterObjectiveType.Duty, "コンテンツ突入状態の自動判定は行われません。手動でチェックを入れてください。"),
            ]),
    ];
}
