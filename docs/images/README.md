# 公開画像について

READMEの画面は、アプリの見た目や使い方を紹介するための**架空データ**です。実アカウントの使用率・認証情報・個人のノート・デスクトップは含みません。画像中のプラン名や使用率は契約内容の証拠ではありません。

| ファイル | 作成方法 |
|---|---|
| `light.png` / `dark.png` | 実際のWinForms画面を`tests/Diagnostics.cs`の架空値で描画 |
| `alerts-dark.png` | 同診断ツールの`--alerts --dark`による警告表示 |
| `forecast-light.png` | 同診断ツールの`--forecast`で架空の時系列を学習させた表示 |
| `obsidian-note-light.png` / `obsidian-note-dark.png` | `obsidian/src/toolbar.js`と`obsidian/styles.css`を架空のノート領域に配置したブラウザープレビュー。周囲のノート領域は説明用のサンプルで、Obsidian本体の画面キャプチャではありません |
| `obsidian-standard.png` / `obsidian-compact.png` | ツールバーの実コードと架空値による標準／縮小プレビュー |
| `obsidian-forecast.png` | 従来の予測文を使ったツールバーのレイアウト参考。現在の予測文とは異なります |
| `widget-large-dark.png` / `../../widgets/Assets/Preview.png` | アプリが生成した架空のAdaptive Cardを公式ホスト設定で描画したプレビュー。Windows側の描画とは差が出る場合があります |

トレイ画面の再生成例です。起動中のアプリを更新せず、別フォルダーへビルドできます。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -NoPublish -OutputDirectory build\showcase
.\build\showcase\Diagnostics.exe
.\build\showcase\Diagnostics.exe --dark
.\build\showcase\Diagnostics.exe --alerts --dark
.\build\showcase\Diagnostics.exe --forecast
```

生成先は`build/showcase/screenshots/`です。公開用には明示的に選んだ架空データ画像だけをコピーします。`--live`は使用せず、出力フォルダー全体を一括公開しないでください。予測画像は仕組みの説明用であり、実利用での精度を保証するものではありません。
