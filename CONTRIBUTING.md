# 開発・貢献ガイド

Windows 11、Windows PowerShell 5.1、標準搭載の.NET Framework 4.8以降を使用します。SDKやNuGetの復元は不要です。

## ビルドとテスト

```powershell
# パーサー・取得処理・UIを検証し、ルートのexeを更新
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1

# 起動中のルートexeに触れず、別フォルダーで検証
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -NoPublish -OutputDirectory build\candidate

# 取得処理のテストだけ
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -TestsOnly
```

警告はコンパイルエラーとして扱います。通常のテストは一時フォルダーに生成する架空のログとHTTP代替応答のみを使用し、実アカウントへ接続しません。UIテストは実際のWindowsメッセージループを動かすため、一時的にウィンドウやトレイアイコンが表示されます。

`-OutputDirectory` を指定した場合、診断ツールなども指定先に生成されます。出力先はGitの対象外である `build/` 配下を使ってください。

## 見た目の確認

```powershell
.\build\Diagnostics.exe
.\build\Diagnostics.exe --dark
```

`build/screenshots/` に架空データの100/150/200%レイアウトと、未取得・0%・期限経過の状態を描画します。`--live` は開発者が明示的に指定した場合だけ実際の使用量を取得します。ライブ診断の出力や画像をIssue・PR・公開資料へ添付しないでください。

## アイコン

`assets/app.svg` が編集元です。角丸矩形だけを使い、次のコマンドでPNGと複数解像度のICOを再生成できます。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\generate-icon.ps1
```

ビルドはICOをWindows実行ファイルとマネージドリソースの両方に埋め込みます。トレイとフォームも同じアイコンを使います。生成にはWindows標準のSystem.Drawingを使用します。

## 変更時の原則

- 認証・ログ・各CLIの設定を変更しないこと。
- 「未取得」と0%、観測時刻と読取時刻、週枠と他の枠を区別すること。
- ログイン情報、API生レスポンス、例外本文を表示・保存しないこと。
- 外部通信を固定のサービス宛HTTPS GETに限定し、タイムアウトと応答サイズ上限を維持すること。
- 実アカウントのデータをテストにコピーしないこと。例は `fixture-*` の架空値を使用すること。
- 不具合修正には、その振る舞いを確認できるテストを追加し、見た目の変更は両テーマで確認すること。

公開Issueには再現手順、Windowsのバージョン、アプリのバージョンを記載してください。認証ファイルや生ログは不要です。機密情報の扱いに関する問題は [SECURITY.md](SECURITY.md) の方法で報告してください。

## リリースZIP

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\package.ps1
```

ビルド・テスト後、指定したファイルだけでZIPとSHA-256を生成します。`build/`、設定、ログ、テストの生成物をフォルダーごと配布しないでください。
