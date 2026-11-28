using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using AIUsageManager;

// The shared folder of docs/usage-shared-cache-protocol.md. "Shared n" is row n of the table in its section 8,
// where now is 2026-11-28T12:00:00Z and the app fetches every 5 minutes.
internal static partial class Tests
{
    sealed class SharedHttp : HttpMessageHandler
    {
        public int Claude, Codex;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string RetryAfter;
        public bool Fail;
        public string ClaudeBody = J("{'five_hour':{'utilization':12,'resets_at':'2026-11-28T15:00:00Z'},'seven_day':{'utilization':34,'resets_at':'2026-12-01T00:00:00Z'}}");
        public string CodexBody = J("{'plan_type':'plus','rate_limit':{'primary_window':{'used_percent':21,'limit_window_seconds':18000,'reset_at':1795881600},'secondary_window':{'used_percent':43,'limit_window_seconds':604800,'reset_at':1796227200}}}");
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            bool claude = request.RequestUri.Host == "api.anthropic.com";
            if (claude) Interlocked.Increment(ref Claude);
            else if (request.RequestUri.Host == "chatgpt.com") Interlocked.Increment(ref Codex);
            if (Fail) throw new HttpRequestException("fixture network failure");
            var response = new HttpResponseMessage(Status) { Content = new StringContent(Status == HttpStatusCode.OK ? (claude ? ClaudeBody : CodexBody) : "error") };
            if (RetryAfter != null) response.Headers.TryAddWithoutValidation("Retry-After", RetryAfter);
            return Task.FromResult(response);
        }
    }
    static readonly DateTimeOffset SharedNow = DateTimeOffset.Parse("2026-11-28T12:00:00Z");
    // Another app's result: 50% for 5 hours and 60% for the week, so it can be told apart from SharedHttp's answer.
    const string OtherClaudeBody = "{\"five_hour\":{\"utilization\":50},\"seven_day\":{\"utilization\":60}}";
    const string OtherCodexBody = "{\"plan_type\":\"plus\",\"rate_limit\":{\"secondary_window\":{\"used_percent\":77,\"limit_window_seconds\":604800}}}";
    static string SharedPath(string root, string file) { return Path.Combine(root, ".ai-usage-shared", file); }
    static void ClaudeLogin(string root)
    { File.WriteAllText(Path.Combine(root, ".claude", ".credentials.json"), "{\"claudeAiOauth\":{\"accessToken\":\"fixture-claude\",\"subscriptionType\":\"max\",\"expiresAt\":4102444800000}}"); }
    static void PutShared(string root, string file, string json)
    { Directory.CreateDirectory(Path.Combine(root, ".ai-usage-shared")); File.WriteAllText(SharedPath(root, file), json, new UTF8Encoding(false)); }
    static void PutResult(string root, string provider, string fetchedAt, string body)
    { PutShared(root, provider + ".json", "{\"version\":1,\"provider\":\"" + provider + "\",\"fetchedAt\":\"" + fetchedAt + "\",\"writer\":\"input-assistant-vscode/2.0.1\",\"body\":" + body + "}"); }
    static void PutLimit(string root, string provider, string observedAt, string until)
    { PutShared(root, provider + ".rate-limit.json", "{\"version\":1,\"provider\":\"" + provider + "\",\"observedAt\":\"" + observedAt + "\",\"until\":\"" + until + "\",\"writer\":\"input-assistant-vscode/2.0.1\"}"); }
    static ServiceUsage[] FetchAt(string root, SharedHttp handler, DateTimeOffset now, bool manual)
    {
        using (var http = new HttpClient(handler, false))
            return new UsageService(root, http, () => now).FetchAllAsync(CancellationToken.None, manual).GetAwaiter().GetResult();
    }
    static Dictionary<string, object> ReadShared(string root, string file)
    { return new JavaScriptSerializer().DeserializeObject(File.ReadAllText(SharedPath(root, file))) as Dictionary<string, object>; }
    static string Canonical(object value) { return new JavaScriptSerializer().Serialize(value); }
    static DateTimeOffset At(string time) { return DateTimeOffset.Parse(time); }
    static bool Shows(ServiceUsage value, double percent) { return value.Windows.Any(w => w.Percent == percent); }

    static void RunSharedUsageTests()
    {
        Check("Shared 1: with nothing shared, Claude is fetched and claude.json holds the response", () => {
            var root = Fixture(); ClaudeLogin(root); var handler = new SharedHttp();
            var claude = FetchAt(root, handler, SharedNow, false)[0];
            Assert(handler.Claude == 1 && Shows(claude, 34) && claude.Source == "オンライン" && claude.RecordedAt == SharedNow);
            var file = ReadShared(root, "claude.json");
            Assert(file["version"].Equals(1) && (string)file["provider"] == "claude" && (string)file["fetchedAt"] == "2026-11-28T12:00:00.000Z");
            Assert(((string)file["writer"]).StartsWith("ai-usage-manager/", StringComparison.Ordinal));
            Assert(Canonical(file["body"]) == Canonical(new JavaScriptSerializer().DeserializeObject(handler.ClaudeBody)));
            var bytes = File.ReadAllBytes(SharedPath(root, "claude.json"));
            Assert(bytes[0] == (byte)'{' && !Encoding.UTF8.GetString(bytes).Contains("fixture-claude"));
            Assert(File.ReadAllText(SharedPath(root, "README.txt")).Contains("No login information is stored here."));
            Assert(!Directory.GetFiles(Path.Combine(root, ".ai-usage-shared"), "*.tmp").Any() && !File.Exists(SharedPath(root, "claude.rate-limit.json")));
        });
        Check("Shared 2: claude.json from 11:58 is shown with its own time and left as it is", () => {
            var root = Fixture(); ClaudeLogin(root); PutResult(root, "claude", "2026-11-28T11:58:00Z", OtherClaudeBody);
            var before = File.ReadAllBytes(SharedPath(root, "claude.json")); var handler = new SharedHttp();
            var claude = FetchAt(root, handler, SharedNow, false)[0];
            Assert(handler.Claude == 0 && Shows(claude, 60) && claude.RecordedAt == At("2026-11-28T11:58:00Z") && claude.Source == "オンライン" && claude.Status == "");
            Assert(File.ReadAllBytes(SharedPath(root, "claude.json")).SequenceEqual(before));
        });
        Check("Shared 3: claude.json from 11:55 is older than 270 seconds, so Claude is fetched", () => {
            var root = Fixture(); ClaudeLogin(root); PutResult(root, "claude", "2026-11-28T11:55:00Z", OtherClaudeBody); var handler = new SharedHttp();
            var claude = FetchAt(root, handler, SharedNow, false)[0];
            Assert(handler.Claude == 1 && Shows(claude, 34) && (string)ReadShared(root, "claude.json")["fetchedAt"] == "2026-11-28T12:00:00.000Z");
        });
        Check("Shared 4: a refresh by hand fetches when claude.json is older than 180 seconds", () => {
            var root = Fixture(); ClaudeLogin(root); PutResult(root, "claude", "2026-11-28T11:56:00Z", OtherClaudeBody); var handler = new SharedHttp();
            FetchAt(root, handler, SharedNow, true);
            Assert(handler.Claude == 1);
            var timed = Fixture(); ClaudeLogin(timed); PutResult(timed, "claude", "2026-11-28T11:56:00Z", OtherClaudeBody); var periodic = new SharedHttp();
            Assert(Shows(FetchAt(timed, periodic, SharedNow, false)[0], 60) && periodic.Claude == 0);
        });
        Check("Shared 5: a refresh by hand shows claude.json from 11:58 without fetching", () => {
            var root = Fixture(); ClaudeLogin(root); PutResult(root, "claude", "2026-11-28T11:58:00Z", OtherClaudeBody); var handler = new SharedHttp();
            Assert(Shows(FetchAt(root, handler, SharedNow, true)[0], 60) && handler.Claude == 0);
        });
        Check("Shared 6: claude.rate-limit.json alone stops fetching until 12:04", () => {
            var root = Fixture(); ClaudeLogin(root); PutLimit(root, "claude", "2026-11-28T11:59:00Z", "2026-11-28T12:04:00Z"); var handler = new SharedHttp();
            var claude = FetchAt(root, handler, SharedNow, false)[0];
            Assert(handler.Claude == 0 && !claude.HasData && claude.Status.Contains("取得制限中") && !claude.NeedsLogin);
            FetchAt(root, handler, At("2026-11-28T12:04:01Z"), false);
            Assert(handler.Claude == 1);
        });
        Check("Shared 7: during the limit the newer of claude.json and this app's last result is shown", () => {
            var root = Fixture(); ClaudeLogin(root); PutLimit(root, "claude", "2026-11-28T11:59:00Z", "2026-11-28T12:04:00Z");
            PutResult(root, "claude", "2026-11-28T11:50:00Z", OtherClaudeBody); var handler = new SharedHttp();
            var claude = FetchAt(root, handler, SharedNow, false)[0];
            Assert(handler.Claude == 0 && Shows(claude, 60) && claude.RecordedAt == At("2026-11-28T11:50:00Z") && !claude.Status.Contains("取得制限中"));
            var own = Fixture(); ClaudeLogin(own); var ownHandler = new SharedHttp(); var now = new[] { At("2026-11-28T11:55:00Z") };
            using (var http = new HttpClient(ownHandler, false))
            {
                var service = new UsageService(own, http, () => now[0]);
                service.FetchAllAsync(CancellationToken.None, false).GetAwaiter().GetResult();
                PutResult(own, "claude", "2026-11-28T11:50:00Z", OtherClaudeBody);
                PutLimit(own, "claude", "2026-11-28T11:59:00Z", "2026-11-28T12:04:00Z");
                now[0] = SharedNow;
                var value = service.FetchAllAsync(CancellationToken.None, false).GetAwaiter().GetResult()[0];
                Assert(ownHandler.Claude == 1 && Shows(value, 34) && value.RecordedAt == At("2026-11-28T11:55:00Z"));
            }
        });
        Check("Shared 8: a limit from before the last success is ignored, and claude.json from 11:53 is fetched again", () => {
            var root = Fixture(); ClaudeLogin(root); PutLimit(root, "claude", "2026-11-28T11:50:00Z", "2026-11-28T12:20:00Z");
            PutResult(root, "claude", "2026-11-28T11:53:00Z", OtherClaudeBody); var handler = new SharedHttp();
            Assert(Shows(FetchAt(root, handler, SharedNow, false)[0], 34) && handler.Claude == 1);
        });
        Check("Shared 9: HTTP 429 without Retry-After writes a limit 5 minutes long and leaves claude.json", () => {
            var root = Fixture(); ClaudeLogin(root); PutResult(root, "claude", "2026-11-28T11:00:00Z", OtherClaudeBody);
            var before = File.ReadAllBytes(SharedPath(root, "claude.json")); var handler = new SharedHttp { Status = (HttpStatusCode)429 };
            var claude = FetchAt(root, handler, SharedNow, false)[0];
            var limit = ReadShared(root, "claude.rate-limit.json");
            Assert(handler.Claude == 1 && limit["version"].Equals(1) && (string)limit["provider"] == "claude" && ((string)limit["writer"]).StartsWith("ai-usage-manager/", StringComparison.Ordinal));
            Assert((string)limit["observedAt"] == "2026-11-28T12:00:00.000Z" && (string)limit["until"] == "2026-11-28T12:05:00.000Z");
            Assert(File.ReadAllBytes(SharedPath(root, "claude.json")).SequenceEqual(before));
            // The other app's earlier success is shown instead of an empty row (section 5, last paragraph).
            Assert(Shows(claude, 60) && claude.RecordedAt == At("2026-11-28T11:00:00Z") && !claude.NeedsLogin);
            var none = Fixture(); ClaudeLogin(none);
            var limited = FetchAt(none, new SharedHttp { Status = (HttpStatusCode)429 }, SharedNow, false)[0];
            Assert(!limited.HasData && limited.Status.Contains("取得制限中") && !limited.NeedsLogin);
        });
        Check("Shared 10: Retry-After is followed between 3 and 60 minutes", () => {
            foreach (var pair in new[] { new[] { "30", "2026-11-28T12:03:00.000Z" }, new[] { "600", "2026-11-28T12:10:00.000Z" }, new[] { "7200", "2026-11-28T13:00:00.000Z" },
                new[] { "Sat, 28 Nov 2026 12:20:00 GMT", "2026-11-28T12:20:00.000Z" } })
            {
                var root = Fixture(); ClaudeLogin(root);
                FetchAt(root, new SharedHttp { Status = (HttpStatusCode)429, RetryAfter = pair[0] }, SharedNow, false);
                Assert((string)ReadShared(root, "claude.rate-limit.json")["until"] == pair[1]);
            }
        });
        Check("Shared 11: HTTP 401, 403, 500 and a network failure write nothing", () => {
            foreach (var handler in new[] { new SharedHttp { Status = HttpStatusCode.Unauthorized }, new SharedHttp { Status = HttpStatusCode.Forbidden },
                new SharedHttp { Status = HttpStatusCode.InternalServerError }, new SharedHttp { Fail = true } })
            {
                var root = Fixture(); ClaudeLogin(root);
                FetchAt(root, handler, SharedNow, false);
                Assert(handler.Claude == 1 && !Directory.Exists(Path.Combine(root, ".ai-usage-shared")));
            }
        });
        Check("Shared 12: a broken claude.json is treated as missing and Claude is fetched", () => {
            string good = "\"version\":1,\"provider\":\"claude\",\"writer\":\"x/1\"";
            var broken = new[] {
                "{\"version\":1,\"provider\":\"claude\",\"fetchedAt\":\"2026-11-28T11:58:00Z\",\"body\":",
                "{\"version\":2,\"provider\":\"claude\",\"fetchedAt\":\"2026-11-28T11:58:00Z\",\"body\":" + OtherClaudeBody + "}",
                "{\"version\":1,\"provider\":\"codex\",\"fetchedAt\":\"2026-11-28T11:58:00Z\",\"body\":" + OtherClaudeBody + "}",
                "{" + good + ",\"fetchedAt\":\"2026-11-28T12:05:00Z\",\"body\":" + OtherClaudeBody + "}",
                "{" + good + ",\"fetchedAt\":\"2026-11-28T11:58:00Z\",\"body\":[" + OtherClaudeBody + "]}",
                "{" + good + ",\"fetchedAt\":\"2026-11-28T11:58:00Z\",\"padding\":\"" + new string('x', 2 * 1048576) + "\",\"body\":" + OtherClaudeBody + "}",
                "{" + good + ",\"fetchedAt\":1795867080,\"body\":" + OtherClaudeBody + "}",
                "{" + good + ",\"fetchedAt\":\"not a time\",\"body\":" + OtherClaudeBody + "}",
                "{\"provider\":\"claude\",\"fetchedAt\":\"2026-11-28T11:58:00Z\",\"body\":" + OtherClaudeBody + "}",
                "[]" };
            foreach (string json in broken)
            {
                var root = Fixture(); ClaudeLogin(root); PutShared(root, "claude.json", json); var handler = new SharedHttp();
                Assert(Shows(FetchAt(root, handler, SharedNow, false)[0], 34) && handler.Claude == 1);
            }
        });
        Check("Shared 12: a broken claude.rate-limit.json is treated as missing, and a limit past 60 minutes ends at 60 minutes", () => {
            foreach (string json in new[] {
                "{\"version\":1,\"provider\":\"claude\",\"observedAt\":\"2026-11-28T12:05:00Z\",\"until\":\"2026-11-28T12:30:00Z\"}",
                "{\"version\":1,\"provider\":\"codex\",\"observedAt\":\"2026-11-28T11:59:00Z\",\"until\":\"2026-11-28T12:30:00Z\"}",
                "{\"version\":1,\"provider\":\"claude\",\"observedAt\":\"2026-11-28T11:59:00Z\",\"until\":1795869000}",
                "{\"version\":1,\"provider\":\"claude\",\"observedAt\":\"2026-11-28T11:59:00Z\"}",
                "{\"version\":1,\"provider\":\"claude\",\"observedAt\":\"2026-11-28T11:59:00Z\",\"until\":" })
            {
                var root = Fixture(); ClaudeLogin(root); PutShared(root, "claude.rate-limit.json", json); var handler = new SharedHttp();
                FetchAt(root, handler, SharedNow, false);
                Assert(handler.Claude == 1);
            }
            var longer = Fixture(); ClaudeLogin(longer); PutLimit(longer, "claude", "2026-11-28T11:30:00Z", "2026-11-28T14:00:00Z"); var waiting = new SharedHttp();
            FetchAt(longer, waiting, SharedNow, false); Assert(waiting.Claude == 0);
            FetchAt(longer, waiting, At("2026-11-28T12:30:01Z"), false); Assert(waiting.Claude == 1);
        });
        Check("Shared 13: when the shared folder cannot be written, the fetched usage is still shown", () => {
            var root = Fixture(); ClaudeLogin(root); File.WriteAllText(Path.Combine(root, ".ai-usage-shared"), "a file where the folder would be");
            var handler = new SharedHttp { Status = (HttpStatusCode)429 };
            Assert(FetchAt(root, handler, SharedNow, false)[0].Status.Contains("取得制限中"));
            handler.Status = HttpStatusCode.OK;
            var claude = FetchAt(root, handler, SharedNow, false)[0];
            Assert(handler.Claude == 2 && Shows(claude, 34) && claude.Status == "");
            var locked = Fixture(); ClaudeLogin(locked); PutResult(locked, "claude", "2026-11-28T11:00:00Z", OtherClaudeBody);
            var path = SharedPath(locked, "claude.json"); File.SetAttributes(path, FileAttributes.ReadOnly);
            try
            {
                var value = FetchAt(locked, new SharedHttp(), SharedNow, false)[0];
                Assert(Shows(value, 34) && value.Status == "" && (string)ReadShared(locked, "claude.json")["fetchedAt"] == "2026-11-28T11:00:00Z");
                Assert(!Directory.GetFiles(Path.Combine(locked, ".ai-usage-shared"), "*.tmp").Any());
            }
            finally { File.SetAttributes(path, FileAttributes.Normal); }
        });
        Check("Shared 14: email, user_id and account_id are removed from the shared body at any depth", () => {
            var root = Fixture(); ClaudeLogin(root); CodexAuth(root);
            var handler = new SharedHttp {
                ClaudeBody = J("{'email':'person@example.com','user_id':'u-1','five_hour':{'utilization':12,'account_id':'a-1'},'seven_day':{'utilization':34},'limits':[{'kind':'weekly_all','percent':34,'user_id':'u-2'}],'organization':{'name':'kept','emails':'kept too'}}"),
                CodexBody = J("{'email':'person@example.com','account_id':'a-2','user_id':'u-3','plan_type':'plus','rate_limit':{'secondary_window':{'used_percent':43,'limit_window_seconds':604800,'user_id':'u-4'}}}") };
            var values = FetchAt(root, handler, SharedNow, false);
            Assert(Shows(values[0], 34) && Shows(values[1], 43));
            string claude = Canonical(ReadShared(root, "claude.json")["body"]), codex = Canonical(ReadShared(root, "codex.json")["body"]);
            foreach (string text in new[] { claude, codex })
                Assert(!text.Contains("\"email\"") && !text.Contains("user_id") && !text.Contains("account_id") && !text.Contains("person@example.com"));
            Assert(claude.Contains("\"kept\"") && claude.Contains("\"emails\":\"kept too\"") && claude.Contains("\"utilization\":12") && claude.Contains("weekly_all"));
            Assert(codex.Contains("\"plan_type\":\"plus\"") && codex.Contains("\"used_percent\":43"));
        });
        Check("Shared 15: codex.json is not used for Claude", () => {
            var root = Fixture(); ClaudeLogin(root); PutResult(root, "codex", "2026-11-28T11:58:00Z", OtherClaudeBody); var handler = new SharedHttp();
            Assert(Shows(FetchAt(root, handler, SharedNow, false)[0], 34) && handler.Claude == 1);
        });
        Check("Shared 16: a claude.json from 11:58 this app cannot read is not fetched again and its body is not shown", () => {
            var root = Fixture(); ClaudeLogin(root); PutResult(root, "claude", "2026-11-28T11:58:00Z", "{\"unknown_shape\":true}");
            var before = File.ReadAllBytes(SharedPath(root, "claude.json")); var handler = new SharedHttp();
            var claude = FetchAt(root, handler, SharedNow, false)[0];
            // The same as when this app's own call returns a response it cannot read.
            Assert(handler.Claude == 0 && !claude.HasData && claude.Status.Contains("返りませんでした") && !claude.NeedsLogin);
            Assert(File.ReadAllBytes(SharedPath(root, "claude.json")).SequenceEqual(before));
            var own = Fixture(); ClaudeLogin(own); var ownHandler = new SharedHttp(); var now = new[] { At("2026-11-28T11:55:00Z") };
            using (var http = new HttpClient(ownHandler, false))
            {
                var service = new UsageService(own, http, () => now[0]);
                service.FetchAllAsync(CancellationToken.None, false).GetAwaiter().GetResult();
                PutResult(own, "claude", "2026-11-28T11:58:00Z", "{\"unknown_shape\":true}");
                now[0] = SharedNow;
                var value = service.FetchAllAsync(CancellationToken.None, false).GetAwaiter().GetResult()[0];
                Assert(ownHandler.Claude == 1 && Shows(value, 34) && value.RecordedAt == At("2026-11-28T11:55:00Z") && value.Status == "");
            }
            var codex = Fixture(); CodexAuth(codex); PutResult(codex, "codex", "2026-11-28T11:58:00Z", "{\"unknown_shape\":true}"); var codexHandler = new SharedHttp();
            var codexValue = FetchAt(codex, codexHandler, SharedNow, false)[1];
            Assert(codexHandler.Codex == 0 && !codexValue.HasData && codexValue.Status == "使用量を取得できません");
        });
        Check("Shared 17: a claude.json from 11:53 this app cannot read still ends a limit from 11:50, and is fetched again", () => {
            var root = Fixture(); ClaudeLogin(root); PutLimit(root, "claude", "2026-11-28T11:50:00Z", "2026-11-28T12:20:00Z");
            PutResult(root, "claude", "2026-11-28T11:53:00Z", "{\"unknown_shape\":true}"); var handler = new SharedHttp();
            Assert(Shows(FetchAt(root, handler, SharedNow, false)[0], 34) && handler.Claude == 1);
            var waiting = Fixture(); ClaudeLogin(waiting); PutLimit(waiting, "claude", "2026-11-28T11:59:00Z", "2026-11-28T12:04:00Z");
            PutResult(waiting, "claude", "2026-11-28T11:50:00Z", "{\"unknown_shape\":true}"); var held = new SharedHttp();
            var claude = FetchAt(waiting, held, SharedNow, false)[0];
            Assert(held.Claude == 0 && !claude.HasData && claude.Status.Contains("返りませんでした"));
        });
        Check("A response this app cannot read is still written for the other apps", () => {
            var root = Fixture(); ClaudeLogin(root); var handler = new SharedHttp { ClaudeBody = "{\"future\":{\"shape\":1}}" };
            Assert(!FetchAt(root, handler, SharedNow, false)[0].HasData && Canonical(ReadShared(root, "claude.json")["body"]) == "{\"future\":{\"shape\":1}}");
        });
        Check("Without a Claude login the shared folder is not read and login is asked for", () => {
            var root = Fixture(); PutResult(root, "claude", "2026-11-28T11:58:00Z", OtherClaudeBody); var handler = new SharedHttp();
            var claude = FetchAt(root, handler, SharedNow, false)[0];
            Assert(handler.Claude == 0 && claude.NeedsLogin && !claude.HasData);
        });
        Check("Codex uses a fresh codex.json, writes its own result, and writes codex.rate-limit.json on HTTP 429", () => {
            var root = Fixture(); CodexAuth(root); PutResult(root, "codex", "2026-11-28T11:58:00Z", OtherCodexBody); var handler = new SharedHttp();
            var codex = FetchAt(root, handler, SharedNow, false)[1];
            Assert(handler.Codex == 0 && Shows(codex, 77) && codex.RecordedAt == At("2026-11-28T11:58:00Z") && codex.Source == "オンライン");
            var fresh = Fixture(); CodexAuth(fresh); var calls = new SharedHttp();
            Assert(Shows(FetchAt(fresh, calls, SharedNow, false)[1], 43) && calls.Codex == 1 && (string)ReadShared(fresh, "codex.json")["provider"] == "codex");
            var limited = Fixture(); CodexAuth(limited); var refused = new SharedHttp { Status = (HttpStatusCode)429 };
            var value = FetchAt(limited, refused, SharedNow, false)[1];
            Assert(refused.Codex == 1 && (string)ReadShared(limited, "codex.rate-limit.json")["until"] == "2026-11-28T12:05:00.000Z" && value.Status.Contains("取得制限中"));
            FetchAt(limited, refused, SharedNow.AddMinutes(1), false);
            Assert(refused.Codex == 1 && !File.Exists(SharedPath(limited, "claude.rate-limit.json")));
        });
    }
}
