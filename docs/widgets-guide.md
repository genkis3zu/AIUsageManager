# Windowsウィジェットへの追加

Windows 11のウィジェットボード（Win+W）に、Claude・Codex・Grokの使用量をまとめて表示できます。トレイ版と別のオプション機能です。

## 必要なもの

- Windows 11 x64。
- Microsoft StoreのWindows Web Experience Pack。
- [Microsoft Windows App Runtime](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads) の2.5.1 x64以降（パッケージ名 `Microsoft.WindowsAppRuntime.2`）。
- Windows設定の開発者モード。この配布形態は未署名のローカル開発パッケージ登録を使います。

通常のトレイ版は引き続きWindows標準の.NET Frameworkだけで動きます。スクリプトは開発者モードを有効にしたり、証明書やウイルス対策の除外を登録したりしません。Store配布や署名済みインストーラーはまだありません。

## インストール

ウィジェット用ZIPを展開し、そのフォルダーで次を実行します。管理者権限は不要です。`pwsh`ではなく **Windows PowerShell（powershell.exe）** を使ってください。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\install-widgets.ps1
```

現在のユーザーだけに登録します。既定の配置先は `%LOCALAPPDATA%\AIUsageManager\Widgets\1.1.0.0` です。元のZIP展開先は登録後に移動しても構いませんが、配置先は削除しないでください。

1. **Win+W** でウィジェットボードを開きます。
2. **＋（ウィジェットを追加）** → **AI Usage Manager** を追加します。
3. ウィジェット右上の **…** で中／大を選びます。

中サイズは3サービスの使用率・リセットまでの時間・取得時刻をまとめます。時間の `d` / `h` / `m` は日／時間／分です。大サイズは取得元、エラーや保存された値の状態も表示します。プラン末尾の `*` はCLIログ由来のプラン名です。

新しいオンライン値が90%・95%・100%以上の場合、中サイズにも「残りわずか」「上限目前」「上限到達」、大サイズには警告文を表示します。急増・リセット前の到達予測とWindows通知はトレイ版の常駐中に利用できます。通知範囲はトレイ右クリック →「通知」で変更でき、初期設定はすべて通知です。予測通知には履歴の学習と過去の予測・実績の検証が必要です。学習状態や予測幅はトレイとObsidianで確認できます。

「更新」で再取得、「アプリを開く」でトレイの一覧を開けます。トレイ右クリックの「Windows ウィジェット」からボードとこの手順を開けます。

Codex・Grokの認証が必要な場合は「アプリを開く」の代わりに該当サービスの「ログイン」ボタンが出ます。クリックすると公式CLIのウィンドウとブラウザが開きます。認証完了後にトレイ側で使用量を取り直します。ウィジェットは「更新」または次回の自動更新で反映します。公式CLIをインストール済みである必要があります。

## 更新とテーマ

ボードでウィジェットが有効になったときと、有効中は前回取得完了から5分後に更新します。ボードを閉じている間の定期取得は止まり、Windowsが最後のカードを保持します。表示されている取得時刻は元のデータの時刻です。再取得が失敗しても0%とは表示しません。

ウィジェットのライト／ダーク配色はWindowsのボードが決めます。トレイ版のテーマ設定とは独立しています。ウィジェットとトレイは別々に取得するため、同時に表示したときの更新時刻は一致しない場合があります。

## 削除・更新

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\uninstall-widgets.ps1
```

ウィジェットの登録だけを解除します。トレイ版、各サービスのファイル、テーマ設定は保持します。解除後は配置先フォルダーを必要に応じて手動削除できます。更新時は新しいZIPのインストールスクリプトを実行します。更新のためにパッケージ内のプロセスを終了する場合があります。

## 開発者向け

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\build-widgets.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\install-widgets.ps1 -CheckOnly
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\install-widgets.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\package-widgets.ps1
```

ビルドにはVisual StudioのC++ビルドツール、Windows SDK（cppwinrt.exe）、SDK取得用のネットワーク接続が必要です。Microsoft Widgets SDK 2.0.5を固定のSHA-256で検証します。

パッケージ化された開発ツール内から実行すると、AppDataへの書き込みが仮想化され、Windowsの登録サービスから見えない場合があります。その場合は通常のWindows PowerShellから実行するか、`-InstallDirectory` で書き込み可能な固定の絶対パスを指定します。同じバージョンの登録先を変える場合は、先に登録解除が必要です。

セキュリティソフトが検査中なら完了を待ってください。隔離された場合は検出履歴を確認し、保護を無効化してインストールを続けないでください。

設計は [Microsoftのウィジェットプロバイダー仕様](https://learn.microsoft.com/en-us/windows/apps/develop/widgets/implement-widget-provider-win32) に基づきます。使用量がWindows側のカードキャッシュに保存されることを含め、データの扱いは [privacy.md](privacy.md) を参照してください。
