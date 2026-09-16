# リリース手順

## 開発リファレンス

Dalamud 公式 API ドキュメント：

```text
https://dalamud.dev/api/
```

Dalamud プラグインサービス、クライアント API、インターフェース署名、およびバージョンの互換性の確認用です。開発時は現在の最新 SDK と公式ドキュメントを最優先とし、旧プロジェクトのコードや推測に基づく実装は避けてください。

## 初回リリース準備

公開 GitHub リポジトリを作成し `main` へプッシュした後、カスタムプラグインリポジトリの URL は以下のようになります：

```text
https://raw.githubusercontent.com/BOCXIV/Beastmaster-JP/main/repo.json
```

初期バージョンは `0.1.0.0` に設定されています。初回ソースコードをプッシュ後、クリーンかつ `origin/main` と同期された `main` ブランチから以下を実行します：

```powershell
.\scripts\release.ps1 0.1.0.0
```

## 通常リリース

バージョン形式は `メジャー.マイナー.リビジョン.ビルド` とし、タグには `v` プレフィックスを付けません。リリースごとに未使用の新しいバージョンを指定します。例：

```powershell
.\scripts\release.ps1 0.1.29.0
```

スクリプトはブランチとリモートの状態を検査し、`Beastmaster.csproj`、`Beastmaster.json`、および `repo.json` を更新して Release ビルドを実行し、コミット・プッシュしてタグを作成します。GitHub Actions が以下のファイルをパッケージングして Release を作成します：

```text
Beastmaster.dll
Beastmaster.json
Beastmaster.deps.json
```

バージョンファイルの変更プレビューのみを行う場合：

```powershell
.\scripts\release.ps1 0.1.29.0 -DryRun
```

タグプッシュ後に CI 完了を待機しない場合：

```powershell
.\scripts\release.ps1 0.1.29.0 -NoWait
```

## リリース後チェック

1. GitHub Release に `Beastmaster.zip` が存在すること。
2. zip 内に DLL、プラグインマニフェスト、deps ファイルのみが含まれていること。
3. `repo.json` のバージョンおよびダウンロードリンクが今回の Release を正しく指していること。
4. Dalamud のカスタムリポジトリ設定に登録し、インストール・アイコン・起動・更新が正常に機能することを検証すること。
