using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AIUsageManager
{
    internal static class ReadOnlyFiles
    {
        public static string Small(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length > 1048576) return "";
                using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                {
                    char[] chars = new char[1048577]; int read = 0, count;
                    while (read < chars.Length && (count = reader.Read(chars, read, chars.Length - read)) > 0) read += count;
                    return read > 1048576 ? "" : new string(chars, 0, read);
                }
            }
        }
        // Reading from the tail keeps work bounded even for very large CLI conversations.
        public static IEnumerable<string> ReverseLines(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long position = stream.Length;
                long floor = Math.Max(0, position - 32L * 1024 * 1024);
                byte[] buffer = new byte[65536];
                var line = new List<byte>(); bool oversized = false;
                while (position > floor)
                {
                    int size = (int)Math.Min(buffer.Length, position - floor); position -= size;
                    stream.Position = position; int read = 0, n;
                    while (read < size && (n = stream.Read(buffer, read, size - read)) > 0) read += n;
                    for (int i = read - 1; i >= 0; i--)
                    {
                        if (buffer[i] == 10)
                        {
                            if (!oversized && line.Count > 0) { line.Reverse(); yield return Encoding.UTF8.GetString(line.ToArray()).TrimEnd('\r'); }
                            line.Clear(); oversized = false;
                        }
                        else if (!oversized)
                        {
                            if (line.Count >= 1048576) { oversized = true; line.Clear(); }
                            else line.Add(buffer[i]);
                        }
                    }
                }
                if (floor == 0 && !oversized && line.Count > 0) { line.Reverse(); yield return Encoding.UTF8.GetString(line.ToArray()).TrimEnd('\r').TrimStart('\uFEFF'); }
            }
        }
    }
    public static class FileSources
    {
        public static ServiceUsage Codex(string profile)
        {
            try
            {
                string directory = Path.Combine(profile, ".codex", "sessions");
                if (!Directory.Exists(directory)) return ServiceUsage.Empty("Codex", "Codex の利用記録がありません");
                var files = new DirectoryInfo(directory).EnumerateFiles("*.jsonl", SearchOption.AllDirectories).OrderByDescending(f => f.LastWriteTimeUtc).Take(64);
                ServiceUsage newest = null;
                foreach (var file in files)
                {
                    try
                    {
                        foreach (string line in ReadOnlyFiles.ReverseLines(file.FullName))
                        {
                            if (!line.Contains("\"rate_limits\"")) continue;
                            var result = UsageParser.Codex(line);
                            if (result != null)
                            {
                                if (newest == null || (result.RecordedAt ?? DateTimeOffset.MinValue) > (newest.RecordedAt ?? DateTimeOffset.MinValue)) newest = result;
                                break;
                            }
                        }
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                return newest ?? ServiceUsage.Empty("Codex", "利用記録がありません");
            }
            catch (IOException) { return ServiceUsage.Empty("Codex", "ログを読み取れません"); }
            catch (UnauthorizedAccessException) { return ServiceUsage.Empty("Codex", "ログを読み取れません"); }
        }
        public static ServiceUsage Grok(string profile)
        {
            try
            {
                foreach (string line in ReadOnlyFiles.ReverseLines(Path.Combine(profile, ".grok", "logs", "unified.jsonl")))
                {
                    if (!line.Contains("\"currentPeriod\"")) continue;
                    var result = UsageParser.Grok(line, null, "CLI 記録");
                    // A new billing record without weekly data must not resurrect an older period.
                    if (Json.Obj(Json.Obj(Json.Parse(line), "ctx"), "config") != null) return result;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return ServiceUsage.Empty("Grok", "週の利用記録がありません");
        }
    }
    internal sealed class FetchResult
    {
        public string Body;
        public string Error;
        public bool NeedsLogin;
        public int Status;
        public bool RateLimited;
        public TimeSpan? RetryAfter;
        public DateTimeOffset ReceivedAt;
    }
    // What the shared folder says before a call: do not call, and show Use; with no Use, wait until Until.
    internal sealed class SharedDecision
    {
        public SharedResult Use;
        public DateTimeOffset Until;
    }
    public interface IUsageSource
    {
        // manual: the user asked for new values (a refresh button, a key, after a login) rather than the 5-minute timer.
        Task<ServiceUsage[]> FetchAllAsync(CancellationToken cancellation, bool manual);
    }
    public sealed class UsageService : IUsageSource
    {
        readonly string profile;
        readonly HttpClient http;
        readonly Func<DateTimeOffset> clock;
        readonly SharedUsageFolder shared;
        // Per service, the last result this app showed, fetched here or read from the shared folder; only results it could read.
        readonly Dictionary<string, SharedResult> last = new Dictionary<string, SharedResult>();
        // The tray app and the widget both fetch this often on their own.
        public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
        public UsageService(string profile, HttpClient http) : this(profile, http, () => DateTimeOffset.UtcNow) { }
        internal UsageService(string profile, HttpClient http, Func<DateTimeOffset> clock)
        { this.profile = profile; this.http = http; this.clock = clock; shared = new SharedUsageFolder(SharedUsageFolder.DefaultPath(profile)); }
        public Task<ServiceUsage[]> FetchAllAsync(CancellationToken cancellation) { return FetchAllAsync(cancellation, false); }
        public static HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 1048576 };
        }
        public async Task<ServiceUsage[]> FetchAllAsync(CancellationToken cancellation, bool manual)
        {
            // Isolate disk enumeration and network work from the UI thread.
            return await Task.WhenAll(
                Task.Run(() => SafeProvider("Claude", () => Claude(cancellation, manual))),
                Task.Run(() => SafeProvider("Codex", () => Codex(cancellation, manual))),
                Task.Run(() => SafeProvider("Grok", () => Grok(cancellation)))).ConfigureAwait(false);
        }
        static async Task<ServiceUsage> SafeProvider(string name, Func<Task<ServiceUsage>> fetch)
        {
            try { return await fetch().ConfigureAwait(false); }
            catch (IOException) { return ServiceUsage.Empty(name, "ファイルを読み取れません"); }
            catch (UnauthorizedAccessException) { return ServiceUsage.Empty(name, "ファイルを読み取れません"); }
            catch (ArgumentException) { return ServiceUsage.Empty(name, "ログイン情報を読み取れません"); }
            catch (InvalidOperationException) { return ServiceUsage.Empty(name, "使用量を取得できません"); }
            catch (OperationCanceledException) { return ServiceUsage.Empty(name, "通信がタイムアウトしました"); }
            catch (Exception) { return ServiceUsage.Empty(name, "使用量を取得できません"); }
        }
        async Task<FetchResult> Get(HttpRequestMessage request, CancellationToken cancellation)
        {
            using (request)
            {
                try
                {
                    using (var response = await http.SendAsync(request, cancellation).ConfigureAwait(false))
                    {
                        int status = (int)response.StatusCode;
                        var received = clock();
                        if (status == 401) return new FetchResult { Error = "再ログインが必要です", NeedsLogin = true, Status = status };
                        if (status == 403) return new FetchResult { Error = "取得先がアクセスを拒否しました", Status = status };
                        if (status == 429) return new FetchResult { Error = "取得制限中・5分後に再試行", Status = status, RateLimited = true, ReceivedAt = received, RetryAfter = RetryAfter(response.Headers.RetryAfter, received) };
                        if (!response.IsSuccessStatusCode) return new FetchResult { Error = "使用量の取得先に接続できません", Status = status };
                        return new FetchResult { Body = await response.Content.ReadAsStringAsync().ConfigureAwait(false), Status = status, ReceivedAt = received };
                    }
                }
                catch (HttpRequestException) { return new FetchResult { Error = "接続できません・次回更新で再試行" }; }
                catch (OperationCanceledException) { return new FetchResult { Error = "通信がタイムアウトしました" }; }
            }
        }
        static TimeSpan? RetryAfter(RetryConditionHeaderValue header, DateTimeOffset received)
        {
            if (header == null) return null;
            if (header.Delta.HasValue) return header.Delta;
            if (header.Date.HasValue) return header.Date.Value - received;
            return null;
        }
        static string Limited(DateTimeOffset until) { return "取得制限中・" + until.ToLocalTime().ToString("HH:mm") + " 以降に再試行"; }
        // A result more than 60 seconds ahead of the clock (the clock was set back) is not used, the same as a shared file (section 6).
        SharedResult Last(string provider, DateTimeOffset now)
        {
            SharedResult value;
            lock (last) if (!last.TryGetValue(provider, out value)) return null;
            return value.FetchedAt > now.AddSeconds(60) ? null : value;
        }
        void Remember(string provider, SharedResult value) { lock (last) last[provider] = value; }
        // Section 5 of docs/usage-shared-cache-protocol.md. Returns null when the service should be called.
        SharedDecision Decide(string provider, bool manual, DateTimeOffset now, Func<Dictionary<string, object>, DateTimeOffset, ServiceUsage> parse)
        {
            var file = shared.ReadResult(provider, now);
            var own = Last(provider, now);
            // The last successful fetch: the shared file's, even when this app cannot read its body, or this app's own.
            DateTimeOffset? success = null;
            if (own != null) success = own.FetchedAt;
            if (file != null && (!success.HasValue || file.FetchedAt > success.Value)) success = file.FetchedAt;
            var reuse = manual ? TimeSpan.FromSeconds(180) : TimeSpan.FromSeconds(Math.Max(180, (RefreshInterval - TimeSpan.FromSeconds(30)).TotalSeconds));
            if (success.HasValue && now - success.Value < reuse) return new SharedDecision { Use = Newest(file, own, parse) };
            var limit = shared.ReadRateLimit(provider, now);
            if (limit == null || limit.Until <= now) return null;
            // A success after the HTTP 429 shows the limit is over.
            if (success.HasValue && success.Value >= limit.ObservedAt) return null;
            return new SharedDecision { Use = Newest(file, own, parse), Until = limit.Until };
        }
        // The result to show: the newer of the shared file and this app's own last result, using the file only when
        // this app can read its body. A file it cannot read is shown only when there is nothing else, and then looks
        // the same as an unreadable answer to this app's own call (section 6).
        static SharedResult Newest(SharedResult file, SharedResult own, Func<Dictionary<string, object>, DateTimeOffset, ServiceUsage> parse)
        {
            if (file != null && (own == null || file.FetchedAt > own.FetchedAt) && parse(file.Body, file.FetchedAt).HasData) return file;
            return own ?? file;
        }
        // Calls the service only when the shared folder has no recent result and no limit, and leaves what it got there.
        // Returns the usage to show, or null with the reason put in failure.
        async Task<ServiceUsage> Online(string provider, bool manual, Func<Dictionary<string, object>, DateTimeOffset, ServiceUsage> parse, Func<HttpRequestMessage> request, CancellationToken cancellation, FetchResult failure)
        {
            var decision = Decide(provider, manual, clock(), parse);
            if (decision != null && decision.Use != null) return Show(provider, decision.Use, parse);
            if (decision != null) { failure.Error = Limited(decision.Until); return null; }
            var response = await Get(request(), cancellation).ConfigureAwait(false);
            if (response.RateLimited)
            {
                var until = SharedUsageFolder.Until(response.ReceivedAt, response.RetryAfter);
                shared.WriteRateLimit(provider, response.ReceivedAt, until);
                // Another app may have fetched just before this call was refused: show the newest result there is.
                var now = clock();
                var newest = Newest(shared.ReadResult(provider, now), Last(provider, now), parse);
                if (newest != null) return Show(provider, newest, parse);
                failure.Error = Limited(until); return null;
            }
            if (response.Body == null) { failure.Error = response.Error; failure.NeedsLogin = response.NeedsLogin; return null; }
            if (response.Status == 200) shared.WriteResult(provider, response.Body, response.ReceivedAt);
            var root = Json.Parse(response.Body);
            var usage = parse(root, response.ReceivedAt);
            if (usage.HasData) Remember(provider, new SharedResult { Body = root, FetchedAt = response.ReceivedAt });
            return usage;
        }
        ServiceUsage Show(string provider, SharedResult value, Func<Dictionary<string, object>, DateTimeOffset, ServiceUsage> parse)
        {
            var usage = parse(value.Body, value.FetchedAt);
            if (usage.HasData) Remember(provider, value);
            return usage;
        }
        static bool ValidHeader(string value)
        { return !string.IsNullOrWhiteSpace(value) && !value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)); }
        async Task<ServiceUsage> Codex(CancellationToken cancellation, bool manual)
        {
            Dictionary<string, object> auth = null;
            try { auth = Json.Parse(ReadOnlyFiles.Small(Path.Combine(profile, ".codex", "auth.json"))); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            var tokens = Json.Obj(auth, "tokens");
            string mode = Json.Str(auth, "auth_mode");
            string token = Json.Str(tokens, "access_token");
            string account = Json.Str(tokens, "account_id");
            string error = "ChatGPT ログインが必要です";
            bool login = true;
            // Never reuse leftover subscription credentials when API-key mode was selected.
            if ((mode == null || mode == "chatgpt") && string.IsNullOrEmpty(Json.Str(auth, "OPENAI_API_KEY")) && ValidHeader(token) && (account == null || ValidHeader(account)))
            {
                var failure = new FetchResult();
                var usage = await Online("codex", manual, (body, time) => UsageParser.CodexOnline(body, time), () => {
                    var request = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    if (account != null) request.Headers.Add("ChatGPT-Account-Id", account);
                    return request;
                }, cancellation, failure).ConfigureAwait(false);
                // A successful current response must not be replaced with stale log data.
                if (usage != null) return usage;
                error = failure.Error; login = failure.NeedsLogin;
            }
            var fallback = FileSources.Codex(profile);
            fallback.NeedsLogin = login;
            fallback.Status = error + (fallback.HasData ? "・CLI 記録を表示" : "");
            return fallback;
        }
        async Task<ServiceUsage> Claude(CancellationToken cancellation, bool manual)
        {
            Dictionary<string, object> oauth = null;
            try { oauth = Json.Obj(Json.Parse(ReadOnlyFiles.Small(Path.Combine(profile, ".claude", ".credentials.json"))), "claudeAiOauth"); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            string token = Json.Str(oauth, "accessToken");
            if (token != null && token.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) token = null;
            string plan = Json.Str(oauth, "subscriptionType");
            string tier = Json.Str(oauth, "rateLimitTier");
            string error = "Claude Code のログインが必要です";
            // Only a missing or expired login, or HTTP 401, offers the login button; network errors, 429 and 403 do not.
            bool login = true;
            var expiry = Json.Date(Json.Get(oauth, "expiresAt"));
            if (!string.IsNullOrWhiteSpace(token) && (!expiry.HasValue || expiry > clock()))
            {
                var failure = new FetchResult();
                var result = await Online("claude", manual, (body, time) => {
                    var value = UsageParser.Claude(body, plan, time, "オンライン");
                    value.Plan = Json.Plan(plan, tier);
                    return value;
                }, () => {
                    var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
                    return request;
                }, cancellation, failure).ConfigureAwait(false);
                if (result != null && result.HasData) return result;
                if (result != null) { error = "取得先から使用量が返りませんでした"; login = false; }
                else { error = failure.Error; login = failure.NeedsLogin; }
            }
            else if (!string.IsNullOrWhiteSpace(token)) error = "Claude Code で再ログインが必要です";
            try
            {
                string cache = ReadOnlyFiles.Small(Path.Combine(profile, ".claude", "usage-cache.json"));
                var data = Json.Parse(cache);
                var time = Json.Date(Json.Get(data, "timestamp") ?? Json.Get(data, "fetched_at") ?? Json.Get(data, "updated_at"));
                var result = UsageParser.Claude(cache, plan, time, "既存キャッシュ");
                result.Plan = Json.Plan(plan, Json.Str(oauth, "rateLimitTier"));
                // When login is needed, say why next to the login button instead of a generic message.
                if (result.HasData) { result.Status = login ? error + "・保存値を表示" : "オンライン未取得・保存値を表示"; result.NeedsLogin = login; return result; }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            var missing = ServiceUsage.Empty("Claude", error); missing.Plan = Json.Plan(plan, Json.Str(oauth, "rateLimitTier")); missing.NeedsLogin = login; return missing;
        }
        static HttpRequestMessage GrokRequest(Dictionary<string, object> login, bool settings)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, settings ?
                "https://cli-chat-proxy.grok.com/v1/settings" : "https://cli-chat-proxy.grok.com/v1/billing?format=credits");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Json.Str(login, "key"));
            request.Headers.Add("X-XAI-Token-Auth", "xai-grok-cli");
            request.Headers.Add("x-grok-client-mode", "cli");
            request.Headers.Add("x-grok-client-version", "1.0.0");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            string userId = Json.Str(login, "user_id");
            if (!string.IsNullOrEmpty(userId)) request.Headers.Add("x-userid", userId);
            return request;
        }
        async Task<string> GrokCurrentPlan(Dictionary<string, object> login, CancellationToken cancellation)
        {
            // Optional metadata must not hold a successful usage result for the full HTTP timeout.
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                var response = await Get(GrokRequest(login, true), timeout.Token).ConfigureAwait(false);
                return Json.GrokPlan(Json.Str(Json.Parse(response.Body), "subscription_tier_display"));
            }
        }
        async Task<ServiceUsage> Grok(CancellationToken cancellation)
        {
            Dictionary<string, object> selected = null;
            try
            {
                var entries = Json.Parse(ReadOnlyFiles.Small(Path.Combine(profile, ".grok", "auth.json")));
                if (entries != null)
                    foreach (var pair in entries)
                    {
                        if (pair.Key != "https://accounts.x.ai/sign-in" && !(pair.Key.StartsWith("https://auth.x.ai::", StringComparison.Ordinal) && pair.Key.Length > "https://auth.x.ai::".Length)) continue;
                        var entry = pair.Value as Dictionary<string, object>;
                        string key = Json.Str(entry, "key");
                        if (string.IsNullOrWhiteSpace(key) || key.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) continue;
                        var expiry = Json.Date(Json.Get(entry, "expires_at"));
                        if (selected == null || (expiry ?? DateTimeOffset.MinValue) > (Json.Date(Json.Get(selected, "expires_at")) ?? DateTimeOffset.MinValue)) selected = entry;
                    }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            string error = "Grok CLI のログインが必要です";
            bool login = true;
            var end = Json.Date(Json.Get(selected, "expires_at"));
            if (selected != null && (!end.HasValue || end > DateTimeOffset.UtcNow))
            {
                var response = await Get(GrokRequest(selected, false), cancellation).ConfigureAwait(false);
                if (response.Body != null)
                {
                    var result = UsageParser.Grok(response.Body, DateTimeOffset.UtcNow, "オンライン");
                    if (!result.HasData) {
                        var confirmed=await GrokWebBilling.ConfirmUnreportedAsync(http,Json.Str(selected,"key"),response.Body,cancellation).ConfigureAwait(false);
                        if(confirmed.HasValue) {result.Windows[0].Percent=confirmed;result.Status="";}
                    }
                    string currentPlan = await GrokCurrentPlan(selected, cancellation).ConfigureAwait(false);
                    if (currentPlan != "プラン未取得") result.Plan = currentPlan;
                    if (result.Plan.StartsWith("SuperGrok", StringComparison.Ordinal))
                        result.PlanNote = "取得先が返したプラン名です。契約変更が未反映の場合は旧名が表示されます。";
                    if (result.Plan == "プラン未取得")
                    {
                        var log = FileSources.Grok(profile);
                        if (log.Plan != "プラン未取得")
                        {
                            result.Plan = log.Plan;
                            result.PlanNote = "プラン名は CLI 記録" + (log.RecordedAt.HasValue ? "（" + log.RecordedAt.Value.ToLocalTime().ToString("MM/dd HH:mm") + "）" : "") + "の値です";
                        }
                    }
                    // An authoritative response without weekly usage must not revive an old period.
                    return result;
                }
                else { error = response.Error; login = response.NeedsLogin; }
            }
            else if (selected != null) error = "Grok CLI で再ログインが必要です";
            // Billing failures must never resurrect an unrelated, older CLI percentage.
            // In particular, a timeout after HTTP 401 is not evidence of a new login.
            var missing=ServiceUsage.Empty("Grok",error);missing.NeedsLogin=login;return missing;
        }
    }
}
