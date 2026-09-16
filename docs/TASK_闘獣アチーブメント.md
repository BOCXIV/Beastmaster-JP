# 闘獣アチーブメント 実施設計書

## 目的

「闘獣練」タブ内の「闘獣アチーブメント」サブタブにおいて、魔獣使いに関連する44項目のアチーブメントの達成状況を表示し、「現在のキャラクターの達成状況を同期」ボタンによってキャラクター別に進捗を保存します。

基本原則：**アチーブメントは ID（Lumina の `Achievement` シートの RowId）を一意のキーとし**、表示文字列に依存しません。

## データソース

### 1. アチーブメント達成状況（クライアントメモリ、FFXIVClientStructs）

FFXIVClientStructs の `FFXIVClientStructs.FFXIV.Client.Game.UI.Achievement` を使用します：

| API | 説明 |
|---|---|
| `Achievement.Instance()` | 静的シングルトンを返す |
| `State` (`+0x08`) | `AchievementState` 列挙型：`Invalid=0` / `Requested=1` / `Loaded=2` |
| `IsLoaded()` | `State == Loaded` と等価。サーバーからデータがロード済みか判定 |
| `RequestCompletedAchievements()` | サーバーへアチーブメント完了データを能動的にリクエスト |
| `IsComplete(int achievementId)` | 指定したアチーブメントIDが達成済みか判定（`bool`） |

アチーブメント完了データはリクエスト後にロードされるため、未ロード時は `RequestCompletedAchievements()` を呼び出して待機します。

### 2. アチーブメント静的データ（Lumina `Achievement` シート）

Lumina の `Achievement` シートから各フィールドを取得します：
- `RowId`：アチーブメント ID
- `Name`：アチーブメント名
- `Description`：説明文
- `Points`：アチーブメントポイント（5 / 10 / 20）
- `Title`：報酬称号

これらを Lumina から直接読み取ることで、クライアント言語（日本語等）に完全準拠します。

## アチーブメント分類一覧（全44項目）

| グループKey | 分類名 | アチーブメントID |
|---|---|---|
| `rate` | 魔獣率舞 | 4028, 4029, 4030, 4031, 4032 |
| `clear` | 闘獣争奇 | 4033, 4034, 4035 |
| `high-clear` | 出奇制勝 | 4036, 4037 |
| `beast-iii` | 不足為奇 | 4038, 4039, 4040 |
| `high-beast-iii` | 奇高一着 | 4041, 4042 |
| `rating` | 盤評価 | 4043, 4044, 4045, 4046, 4047, 4048 |
| `high-rating` | 高段評価 | 4049, 4050, 4051, 4052 |
| `overall` | 全盤総合 | 4053, 4054, 4055 |
| `training` | 訓練有素 | 4056, 4057, 4058, 4059, 4060 |
| `high-reward` | 高段報酬 | 4061, 4062, 4063, 4064, 4065, 4066, 4067, 4068 |
| `ranking` | ランキング | 4075, 4076, 4077 |

## 同期フロー

1. `RequestSync()`：ログイン済みか検証し、タイムアウト監視を開始。
2. `Update()`（フレーム毎）：
   - 未ログイン時は中断。
   - `Achievement.Instance()` が null の場合は「アチーブメントシステム利用不可」を出力。
   - `!IsLoaded()` の場合は `RequestCompletedAchievements()` を呼び出して待機。
   - `IsLoaded()` となったら全44件を走査し、`IsComplete(id)` を集計して `ReplaceAchievementProgress` で保存。
   - 「同期完了：達成済み X/44」を通知。
   - 15秒のタイムアウト保護を実装。

