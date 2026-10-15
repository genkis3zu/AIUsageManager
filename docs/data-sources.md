# データ取得仕様

## Claude

既存Claude Codeログインで使用量を取得します。失敗時は、存在する `usage-cache.json` を読みます。キャッシュ生成やステータスライン登録はしません。

トップレベル `five_hour` / `seven_day` の `utilization`、または `rate_limits.five_hour` / `rate_limits.seven_day` の `used_percentage` を受け付けます。各枠の `resets_at` をリセット日時に使います。キャッシュの観測時刻は `timestamp` / `fetched_at` / `updated_at`。時刻がなければ「取得時刻不明」です。

## Codex

既存ChatGPTログインの `.codex/auth.json` を読み、Codexと同じ `chatgpt.com/backend-api/wham/usage` に問い合わせます。`rate_limit.primary_window` / `secondary_window` のうち `limit_window_seconds = 604800` を週枠として選び、`used_percent` と `reset_at` を表示します。コードレビュー用などの別枠は混ぜません。APIキー認証モードのときは、残っているChatGPTトークンを送信しません。

オンライン応答に週枠・割合がなければ未取得とし、古いログで置き換えません。通信・認証に失敗した場合だけ、以下の保存ログを使います。取得失敗の理由と元の記録時刻を保持します。

セッションの `event_msg` / `token_count` を対象に、最後の有効な `rate_limits` を読みます。`limit_id` が `codex` または省略された記録から、`primary` / `secondary` の `window_minutes = 10080` を週枠として選びます。

更新日時が新しい最大64ファイルを調べ、各ファイル末尾の最大32 MiBを逆順に読みます。各ファイルで見つかった値のうち、観測時刻が最新の記録を採用します。古いセッションへ追記されても、古い使用量に戻りません。1 MiB超の行と壊れたJSON行は読み飛ばします。探索範囲に値がなければ未取得です。

## Grok

既存Grok Build CLIログインで請求情報を取得します。Grok Botの別枠は取得しません。ログイン未設定・期限切れ・HTTP 401や通信失敗時は、使用量を未取得とし、古いCLIログの割合へ戻しません。期限切れ・HTTP 401では「ログイン」ボタンを表示し、HTTP 403はアクセス拒否として区別します。

`config.creditUsagePercent` と `config.currentPeriod` を読み、週の期間であることを確認します。APIから正常な応答があっても週枠がなければ未取得とし、古い週枠を復活させません。JSON応答で割合が省略されただけでは0%とは推定しません。

リセット後など、有効な週の期間が返り、割合の項目だけが省略されている場合は、同じログインで公式画面の `GetGrokCreditsConfig` を読取専用POSTで照会します。追加照会は6秒以内・1回、Cookie不使用です。完結したgRPC-web応答、成功ステータス、現在時刻を含む週の期間、元の応答との開始／終了時刻の一致を検証します。

この追加応答の割合は[公式Webクライアントのデータ定義](https://cdn.grok.com/_next/static/chunks/32g78bk5hhe1q.js)で、proto3の省略時既定値が0になるfloatです。上記検証を通った場合だけ公式画面と同じく0%として扱います。明示された割合もそのまま使用します。失敗・切断・期間の不一致・元のJSONのnullや不正値は0%に変換しません。追加照会で確認できなければ「認証済み・使用率は未報告です」としてバーを隠し、リセット時刻は表示します。再ログインボタンは認証が必要な場合だけ出します。

請求情報の取得に成功した場合、同じログインで `cli-chat-proxy.grok.com/v1/settings` をGETし、`subscription_tier_display` をプラン名として優先します。追加照会は更新ごとに1回、最大2秒です。失敗・省略・不明な名前の場合は請求応答の `subscriptionTier`、それも不明なら `unified.jsonl` の末尾最大32 MiBから既知のプラン名だけを補います。プラン取得の失敗で、成功した使用率や認証状態を上書きしません。CLIログ由来のプラン名には `*` と記録時刻の説明を付けます。

新体系のFree・Plus・Premium・Super・Ultraと、従来のSuperGrok系の名前を区別して表示します。[公式説明](https://docs.x.ai/x-subscription/grok-bot)では新体系への移行は任意なので、Heavyを自動的にUltraへ置換しません。取得先が旧名を返す場合は `*` の説明で契約変更が未反映の場合もあることを示します。新契約の名前が取得できれば、次回更新で表示を切り替えます。

[The X Subscriptionの週枠](https://docs.x.ai/x-subscription/usage-limits)は対象のGrok・Cursor製品間で共有されます。本アプリはCLI請求APIが返す割合・期間を表示し、契約名から使用量を換算したり、別枠を足し合わせたりしません。Ultra移行後の1環境では公式画面と割合・リセット日時の一致を確認しましたが、すべての契約形態の検証ではありません。契約画面と値が異なる場合は、契約画面を優先してください。

## 共通

- 日時はISO 8601またはUnix秒／ミリ秒。画面ではWindowsのローカル時刻を使用します。
- ログを読み取った時刻で観測時刻を上書きしません。
- 各サービスの失敗は独立して処理し、他サービスの表示を止めません。
- 同時更新を抑止し、取得完了から5分後に次回更新します。
- 認証ファイルは毎回読み直します。定期取得でトークンを更新しません。ユーザーがCodex・Grokのログインボタンを押した場合だけ、公式CLIの認証処理を起動します。
- 各サービスの取得先は非公開仕様であり、将来の互換性は保証できません。

ファイルと通信の詳細は [プライバシー文書](privacy.md) を参照してください。

## 警告への利用

取得元・成功状態・元の取得時刻・リセット日時を確認し、新しいオンライン値だけを警告判定に使います。ログへのフォールバックや取得失敗を0%とみなしたり、急増の起点にしたりしません。予測用の観測はこのPCに暗号化して最大90日保持します。判定・通知条件は [使用量の警告](alerts.md) に記載しています。

## 外部の統計との関係

2026-10-15時点で確認した公開資料では、[AnthropicのUsage and Cost API](https://platform.claude.com/docs/en/manage-claude/usage-cost-api)はAPI組織用で個人アカウントには提供されず、[OpenAIのUsage API](https://developers.openai.com/api/reference/resources/admin/subresources/organization/subresources/usage)も組織のAPI使用量を扱います。[xAIの履歴API](https://docs.x.ai/developers/rest-api-reference/management/billing)もAPI使用量の履歴で、[The X Subscriptionの使用量](https://docs.x.ai/x-subscription/manage)はダッシュボードで確認する案内です。

このアプリで表示している個人サブスクの上限率と対応付けられる履歴取得方法は、上記資料では確認できていません。APIのトークン数や費用をサブスクの上限率に換算せず、現在の取得先が返した使用率を継続記録して学習します。追加の管理者キーを求めたり、未確認の取得先へ既存ログインを送信したりはしません。
