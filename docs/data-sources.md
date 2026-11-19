# データ取得仕様

## Claude

既存Claude Codeログインで使用量を取得します。失敗時は、存在する `usage-cache.json` を読みます。キャッシュ生成やステータスライン登録はしません。

応答の `limits` 配列から枠を作ります。`kind` が `session` の項目は「5時間」、`weekly_all` は「週」、`weekly_scoped` は「週（モデル名）」（例「週（Fable）」、モデル名は `scope.model.display_name`）です。割合は `percent`、リセット日時は `resets_at` を使います。知らない `kind`、割合が不正な項目、`is_active`・`group`・`severity` は使いません。同じ枠が2つあるときは割合の高いほうを使います。

`limits` に5時間または週の枠がないときは、トップレベル `five_hour` / `seven_day` の `utilization`、または `rate_limits.five_hour` / `rate_limits.seven_day` の `used_percentage` で補います。取得先が返さなかった枠は表示しません。モデル名は英数字と一部の記号だけを残し、24文字までにします。

プラン名は、内部名 `max` と `20x` を含む契約を「Ultra」（旧Max 20x）、それ以外の `max` を「Max」と表示します。キャッシュの観測時刻は `timestamp` / `fetched_at` / `updated_at`。時刻がなければ「取得時刻不明」です。

## Codex

既存ChatGPTログインの `.codex/auth.json` を読み、Codexと同じ `chatgpt.com/backend-api/wham/usage` に問い合わせます。`rate_limit.primary_window` / `secondary_window` のうち、`limit_window_seconds = 18000` を5時間、`604800` を週の枠として、`used_percent` と `reset_at` を表示します。Pro系のプランは週の枠だけ、Plusなどは5時間と週の枠が返ります。コードレビュー用などの別枠は混ぜません。APIキー認証モードのときは、残っているChatGPTトークンを送信しません。

オンライン応答にどちらの枠の割合もなければ未取得とし、古いログで置き換えません。通信・認証に失敗した場合だけ、以下の保存ログを使います。取得失敗の理由と元の記録時刻を保持します。

セッションの `event_msg` / `token_count` を対象に、最後の有効な `rate_limits` を読みます。`limit_id` が `codex` または省略された記録から、`primary` / `secondary` の `window_minutes = 300` を5時間、`10080` を週の枠として読みます。

更新日時が新しい最大64ファイルを調べ、各ファイル末尾の最大32 MiBを逆順に読みます。各ファイルで見つかった値のうち、観測時刻が最新の記録を採用します。古いセッションへ追記されても、古い使用量に戻りません。1 MiB超の行と壊れたJSON行は読み飛ばします。探索範囲に値がなければ未取得です。

## Grok

既存Grok Build CLIログインで請求情報を取得します。Grok Bot（Premium以上）も同じ週の枠を使うため、割合にはGrok Botの利用も含まれます。ログイン未設定・期限切れ・HTTP 401や通信失敗時は、使用量を未取得とし、古いCLIログの割合へ戻しません。期限切れ・HTTP 401では「ログイン」ボタンを表示し、HTTP 403はアクセス拒否として区別します。

`config.creditUsagePercent` と `config.currentPeriod` を読み、週の期間であることを確認します。APIから正常な応答があっても週枠がなければ未取得とし、古い週枠を復活させません。JSON応答で割合が省略されただけでは0%とは推定しません。

リセット後など、有効な週の期間が返り、割合の項目だけが省略されている場合は、同じログインで公式画面の `GetGrokCreditsConfig` を読取専用POSTで照会します。追加照会は6秒以内・1回、Cookie不使用です。完結したgRPC-web応答、成功ステータス、現在時刻を含む週の期間、元の応答との開始／終了時刻の一致を検証します。

この追加応答の割合は[公式Webクライアントのデータ定義](https://cdn.grok.com/_next/static/chunks/32g78bk5hhe1q.js)で、proto3の省略時既定値が0になるfloatです。上記検証を通った場合だけ公式画面と同じく0%として扱います。明示された割合もそのまま使用します。失敗・切断・期間の不一致・元のJSONのnullや不正値は0%に変換しません。追加照会で確認できなければ「認証済み・使用率は未報告です」としてバーを隠し、リセット時刻は表示します。再ログインボタンは認証が必要な場合だけ出します。

請求情報の取得に成功した場合、同じログインで `cli-chat-proxy.grok.com/v1/settings` をGETし、`subscription_tier_display` をプラン名として優先します。追加照会は更新ごとに1回、最大2秒です。失敗・省略・不明な名前の場合は請求応答の `subscriptionTier`、それも不明なら `unified.jsonl` の末尾最大32 MiBから既知のプラン名だけを補います。プラン取得の失敗で、成功した使用率や認証状態を上書きしません。CLIログ由来のプラン名には `*` と記録時刻の説明を付けます。

新体系のFree・Plus・Premium・Super・Ultraと、従来のSuperGrok系の名前を区別して表示します。[公式説明](https://docs.x.ai/x-subscription/grok-bot)では新体系への移行は任意なので、Heavyを自動的にUltraへ置換しません。取得先が旧名を返す場合は `*` の説明で契約変更が未反映の場合もあることを示します。新契約の名前が取得できれば、次回更新で表示を切り替えます。

[The X Subscriptionの週枠](https://docs.x.ai/x-subscription/usage-limits)は対象のGrok・Cursor製品間で共有されます。本アプリはCLI請求APIが返す割合・期間を表示し、契約名から使用量を換算したり、別枠を足し合わせたりしません。Ultra移行後の1環境では公式画面と割合・リセット日時の一致を確認しましたが、すべての契約形態の検証ではありません。契約画面と値が異なる場合は、契約画面を優先してください。

## 障害情報（AIの状況）

| 会社 | 取得先 | 見る項目 |
|---|---|---|
| Anthropic | GET `https://status.claude.com/api/v2/summary.json` | 名前が `claude.ai`・`Claude Code`・`Claude API` で始まる項目。どれも見つからないときはページ全体の状態 |
| OpenAI | GET `https://status.openai.com/api/v2/summary.json` | ページ全体の状態と、名前に `Codex`・`ChatGPT` を含む項目のうち、悪いほう |

- 使用量と同じ更新（5分ごと）で読みます。認証情報とCookieは送りません。タイムアウトは10秒です。
- 項目が「重大な障害（major_outage）」、またはページ全体が major・critical のときは「障害」（赤）です。一部の障害・性能低下・メンテナンスは「一部不調」（黄）です。
- 読めなかった会社（通信の失敗、HTTPエラー、JSONでない応答）は「確認できません」とし、正常とはみなしません。両社とも読めなかったときは「確認できません」と表示します。
- 同じページにある他の製品（例：Claude for Government）の障害は、Claudeの状態に含めません。
- xAI（Grok）の障害情報ページは自動では読めない（アクセスを拒否される）ため、対象外です。

### 週の使用枠のリセット

- 新しいオンライン値の週の枠で、前回のリセット予定時刻を過ぎ、次の予定時刻に変わったとき、「リセットされました」と表示します。表示はリセット時刻から1時間です。
- アプリを起動して最初に受け取った値は記録するだけです。起動する前に起きたリセットは表示しません。
- 5時間の枠、保存された値、取得に失敗した値は対象外です。

表示は、障害、一部不調、週のリセット、正常の順に優先します。

## 共通

- 日時はISO 8601またはUnix秒／ミリ秒。画面ではWindowsのローカル時刻を使用します。
- ログを読み取った時刻で観測時刻を上書きしません。
- 各サービスの失敗は独立して処理し、他サービスの表示を止めません。
- 同時更新を抑止し、取得完了から5分後に次回更新します。
- 認証ファイルは毎回読み直します。定期取得でトークンを更新しません。ユーザーがClaude・Codex・Grokのログインボタン（トレイ・ウィジェット・Stream Deck）を押した場合だけ、公式CLIの認証処理を起動します。
- Claudeは、ログイン情報がない、トークンの期限が切れている、または取得先が HTTP 401 を返したときに「ログインが必要」とします。通信の失敗・取得制限（429）・アクセス拒否（403）では、ログインを求めません。
- 各サービスの取得先は非公開仕様であり、将来の互換性は保証できません。

ファイルと通信の詳細は [プライバシー文書](privacy.md) を参照してください。

## 警告への利用

取得元・成功状態・元の取得時刻・リセット日時を確認し、新しいオンライン値だけを警告判定に使います。ログへのフォールバックや取得失敗を0%とみなしたり、急増の起点にしたりしません。予測用の観測はこのPCに暗号化して最大90日保持します。判定・通知条件は [使用量の警告](alerts.md) に記載しています。

## 外部の統計との関係

2026-10-15時点で確認した公開資料では、[AnthropicのUsage and Cost API](https://platform.claude.com/docs/en/manage-claude/usage-cost-api)はAPI組織用で個人アカウントには提供されず、[OpenAIのUsage API](https://developers.openai.com/api/reference/resources/admin/subresources/organization/subresources/usage)も組織のAPI使用量を扱います。[xAIの履歴API](https://docs.x.ai/developers/rest-api-reference/management/billing)もAPI使用量の履歴で、[The X Subscriptionの使用量](https://docs.x.ai/x-subscription/manage)はダッシュボードで確認する案内です。

このアプリで表示している個人サブスクの上限率と対応付けられる履歴取得方法は、上記資料では確認できていません。APIのトークン数や費用をサブスクの上限率に換算せず、現在の取得先が返した使用率を継続記録して学習します。追加の管理者キーを求めたり、未確認の取得先へ既存ログインを送信したりはしません。

## 各サービスの使用枠（2026-11-19確認）

- Claude：Max 20xは「Ultra」に名前が変わり、認証済みのUltraには5時間の制限がありません。Ultraには全モデル共通の週の制限と、Fable専用の週の制限があります。Maxは5時間・週・Fableの週、Proは5時間と週の制限です（[Ultraプラン](https://support.claude.com/en/articles/17728952-what-is-the-ultra-plan)、[Maxプラン](https://support.claude.com/en/articles/11049741-what-is-the-max-plan)、[料金](https://claude.com/pricing)）。取得先の `limits` 配列の形は、利用者の許可を得て実データで確認しました。
- Codex：Pro系のプランには5時間の制限がなく、Plusには5時間の制限があります（[Codex Pricing](https://developers.openai.com/codex/pricing)）。
- Grok：有料契約ごとに週の枠が1つあり、Grokの各製品・Cursor・Grok Botで共有します（[Usage limits](https://docs.x.ai/x-subscription/usage-limits)、[Grok Bot](https://docs.x.ai/x-subscription/grok-bot)）。
- 各社の仕様は予告なく変わります。表示と契約画面が違う場合は、契約画面を優先してください。
