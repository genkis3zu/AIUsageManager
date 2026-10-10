<img src="assets/app.png" width="72" height="72" alt="AI Usage Manager icon">

# AI Usage Manager

Claude・Codex・Grok の使用量をまとめて確認する、Windows 11用の軽量タスクトレイアプリです。

Windows標準の **.NET Framework 4.8 / 4.8.1 と Windows Forms** で動作します。追加ランタイム、WebView2、Node.js、Python、NuGetは不要です。表示言語は日本語です。

| ライト | ダーク |
|---|---|
| ![ライト表示](docs/images/light.png) | ![ダーク表示](docs/images/dark.png) |

画像は架空のプラン・使用率によるサンプルです。

## ダウンロード・起動

1. [最新版のRelease](https://github.com/genkis3zu/AIUsageManager/releases/latest) から `AIUsageManager-1.0.0-windows.zip` をダウンロードします。
2. ZIPを、書き込み可能な任意のフォルダーに展開します。
3. **AIUsageManager.exe** を起動します。`AIUsageManager.exe.config` は同じフォルダーに置いてください。
4. タスクトレイの棒グラフのアイコンをクリックします。見当たらなければ `^` 内を確認してください。

対応するCLIの既存ログインや利用記録を参照します。利用できるログイン情報も保存された使用量もないサービスは「未取得」になります。Codexはログイン情報を参照せず、利用記録だけを読みます。管理者権限は不要です。インストーラーと自動起動登録はありません。

配布ファイルはコード署名されていません。ダウンロード元とReleaseのSHA-256を確認してください。保護機能の無効化は必要ありません。

## 表示するもの

| サービス | 使用枠 | データ元 |
|---|---|---|
| Claude | 5時間・週 | 既存Claude Codeログインによる使用量取得。失敗時は既存キャッシュ |
| Codex | 週 | ローカルのCodexセッションに最後に記録された値 |
| Grok | 週 | 既存Grok CLIログインによる請求情報取得。通信失敗時はローカルログ |

- プラン名、使用率バー、リセットまでの時間、取得時刻を表示します。
- **80%以上は赤**。値がない「未取得」と、取得済みの **0%** を区別します。
- ログを今読み直しても取得時刻を現在時刻に置き換えません。15分以上前の観測には「保存された値」と表示します。
- リセット期限を過ぎても、再取得できるまでは0%に戻しません。
- Grokのプラン名に付く `*` は、プラン名がCLIログ由来である印です。マウスを重ねると詳細を確認できます。

## 操作

| 操作 | 動作 |
|---|---|
| トレイを左クリック | 一覧を開く／閉じる |
| 「更新」または F5 | 手動で再取得 |
| 自動更新 | 前回の取得完了から5分後 |
| 外側をクリック、Esc、×、Alt+F4 | ポップアップを閉じて常駐を継続 |
| トレイ右クリック → テーマ | Windowsに合わせる／ライト／ダーク |
| トレイ右クリック → 終了 | 常駐を終了 |
| 起動済みのexeを再実行 | 既存のポップアップを表示 |

初期テーマは「Windowsに合わせる」。Windowsのテーマ変更にも追従します。コントラストテーマが有効ならその配色を優先します。選択したテーマだけを、exeの横の `settings.ini` に保存します。

## プライバシー

サービスのログ・認証・設定ファイルは**読み取り専用**です。トークンや生のAPIレスポンスをログ・画面・アプリの設定ファイルへ出力しません。使用履歴も保存しません。テレメトリー、自動更新通信、独自サーバーへの送信はありません。

ClaudeとGrokの取得時は、そのサービスの固定HTTPSエンドポイントに認証情報を送ります。Codexはローカルファイルだけを参照します。読み取り対象・通信先・保存する内容は [プライバシー文書](docs/privacy.md) に記載しています。

## 制限・トラブルシューティング

- ClaudeとGrokの取得先は非公開仕様のため、将来変更される可能性があります。サービス各社の公式アプリではありません。
- ログインが期限切れなら、各CLIで通常の再ログインを行ってから更新してください。このアプリはログイン画面の操作やトークン更新をしません。
- Codexの値は最後のCLI利用時点の記録です。アプリの更新ボタンだけではサービス上の最新値にはなりません。
- カスタムCLIホーム、複数アカウント切り替え、Windowsウィジェットボードへの追加は未対応です。
- 詳細な読取形式・上限は [データ取得仕様](docs/data-sources.md) を参照してください。

## ビルド・開発

Windows PowerShell 5.1 とWindows同梱C#コンパイラだけでビルドできます。実行中のアプリは先に終了してください。

```powershell
git clone https://github.com/genkis3zu/AIUsageManager.git
cd AIUsageManager
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

テストの成功後、ルートにexeとconfigを生成します。`ExecutionPolicy Bypass` はこのプロセスだけに適用されます。詳しくは [開発・貢献ガイド](CONTRIBUTING.md)、[設計](docs/design.md)、[検証範囲](docs/verification.md) を参照してください。

## ライセンス・参考

[MIT License](LICENSE)。アプリアイコンもこのリポジトリのMITライセンスで利用できます。各サービスの名称・商標はそれぞれの権利者に帰属します。

取得プロトコルと項目名の確認に [CodeZeno/Claude-Code-Usage-Monitor](https://github.com/CodeZeno/Claude-Code-Usage-Monitor)（MIT）を参考にしました。本体コードとアイコンは新規実装です。

- [Claude Code status line](https://code.claude.com/docs/en/statusline)
- [Windows標準搭載 .NET Framework](https://learn.microsoft.com/en-us/dotnet/framework/install/on-windows-and-server)
