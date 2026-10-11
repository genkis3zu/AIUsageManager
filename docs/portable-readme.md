# AI Usage Manager 1.1.0

Claude・Codex・Grokの使用量を表示するWindows 11用トレイアプリです。

1. ZIP内のファイルを、書き込み可能なフォルダーに展開します。
2. AIUsageManager.exeを起動します。exe.configは同じ場所に置いてください。
3. タスクトレイの棒グラフアイコンをクリックします。見当たらなければ ^ 内を確認してください。

更新は5分ごと。「更新」またはF5でも再取得できます。
トレイ右クリック → テーマで、Windowsに合わせる／ライト／ダークを選べます。
終了はトレイ右クリック → 終了です。

対応CLIの既存ログインや利用記録が必要です。値がなければ「未取得」になります。

Codex・Grokの認証が必要な場合は「ログイン」ボタンで公式CLIの認証を開始できます。ブラウザで完了すると使用量を再取得します。公式CLIがインストールされている必要があります。
Windows標準の.NET Frameworkを使用し、追加ランタイムや管理者権限は不要です。
Windowsウィジェットは別のオプションパッケージです。追加手順はGitHubのREADMEを参照してください。
配布ファイルはコード署名されていません。公式ReleaseとSHA-256を確認してください。

ログ・認証ファイルは読み取り専用です。テーマ選択だけをsettings.iniへ保存します。
ログインボタンで起動する公式CLIは、通常の認証と同じように認証ファイルを保存・更新します。アプリはその出力やトークンを保存しません。
通信先とデータの扱いは同梱のPRIVACY.md、ライセンスはLICENSEを参照してください。

ソース・詳細な操作・最新版:
https://github.com/genkis3zu/AIUsageManager

MIT License / Copyright (c) 2026 AI Usage Manager contributors
