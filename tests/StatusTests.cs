using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIUsageManager;

internal static partial class Tests
{
    // Statuspage-style summary with the given overall indicator and (name, status) components.
    static string Summary(string indicator, params string[] components)
    {
        var parts = new List<string>();
        for (int i = 0; i + 1 < components.Length; i += 2)
            parts.Add("{'id':'c" + i + "','name':'" + components[i] + "','status':'" + components[i + 1] + "'}");
        return J("{'page':{'id':'p'},'status':{'indicator':'" + indicator + "','description':'x'},'components':[" + string.Join(",", parts) + "],'incidents':[]}");
    }
    sealed class StatusHttp : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = new List<HttpRequestMessage>();
        public string ClaudeBody = "", OpenAIBody = "";
        public HttpStatusCode ClaudeStatus = HttpStatusCode.OK, OpenAIStatus = HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(request);
            bool claude = request.RequestUri.Host == "status.claude.com";
            return Task.FromResult(new HttpResponseMessage(claude ? ClaudeStatus : OpenAIStatus) { Content = new StringContent((claude ? ClaudeBody : OpenAIBody) ?? "") });
        }
    }
    static ServiceUsage Fresh(string name, DateTimeOffset at, params UsageWindow[] windows)
    {
        var value = new ServiceUsage { Name = name, Source = "オンライン", RecordedAt = at };
        value.Windows.AddRange(windows); return value;
    }
    static void RunStatusTests()
    {
        Check("Claude status follows claude.ai, Claude Code and Claude API only", () => {
            var down = StatusParser.Claude(Summary("major", "claude.ai", "operational", "Claude Code", "major_outage", "Claude API (api.anthropic.com)", "operational"));
            Assert(down.Level == StatusLevel.Major && down.Detail == "Claude Code" && down.Company == "Claude");
            var slow = StatusParser.Claude(Summary("minor", "claude.ai", "degraded_performance", "Claude Code", "partial_outage", "Claude API (api.anthropic.com)", "under_maintenance"));
            Assert(slow.Level == StatusLevel.Minor && slow.Detail == "claude.ai、Claude Code、Claude API (api.anthropic.com)");
            var unrelated = StatusParser.Claude(Summary("minor", "claude.ai", "operational", "Claude Code", "operational", "Claude for Government", "major_outage"));
            Assert(unrelated.Level == StatusLevel.Operational && unrelated.Detail == "");
        });
        Check("Claude status without the known components falls back to the overall indicator", () => {
            Assert(StatusParser.Claude(Summary("critical", "Something else", "operational")).Level == StatusLevel.Major);
            Assert(StatusParser.Claude(Summary("maintenance")).Level == StatusLevel.Minor);
            Assert(StatusParser.Claude(Summary("none")).Level == StatusLevel.Operational);
        });
        Check("OpenAI status combines the overall indicator with Codex and ChatGPT components", () => {
            Assert(StatusParser.OpenAI(Summary("minor", "Image Generation", "partial_outage")).Level == StatusLevel.Minor);
            var codex = StatusParser.OpenAI(Summary("none", "Codex in ChatGPT Desktop", "major_outage", "Responses", "operational"));
            Assert(codex.Level == StatusLevel.Major && codex.Detail == "Codex in ChatGPT Desktop" && codex.Company == "OpenAI");
            Assert(StatusParser.OpenAI(Summary("none", "ChatGPT Work", "operational")).Level == StatusLevel.Operational);
        });
        Check("Unreadable status pages are unknown, never operational", () => {
            foreach (string body in new[] { "", "{partial", "<html>Forbidden</html>", "{}", J("{'status':{'indicator':'purple'}}") }) {
                Assert(StatusParser.Claude(body).Level == StatusLevel.Unknown && StatusParser.OpenAI(body).Level == StatusLevel.Unknown);
            }
        });
        Check("Component names are cleaned and at most three are listed", () => {
            var value = StatusParser.Claude(Summary("major", "claude.ai\\u0007 web", "major_outage", "Claude Code " + new string('x', 60), "major_outage", "Claude API a", "major_outage", "Claude API b", "major_outage"));
            var names = value.Detail.Split('、');
            Assert(names.Length == 3 && names[0] == "claude.ai web" && names.All(n => n.Length <= 40));
        });
        Check("Status pages are read with plain GET requests to the two fixed addresses", () => {
            var handler = new StatusHttp { ClaudeBody = Summary("none", "Claude Code", "operational"), OpenAIStatus = HttpStatusCode.Forbidden };
            using (var http = new HttpClient(handler)) {
                var result = StatusPages.FetchAsync(http, CancellationToken.None).GetAwaiter().GetResult();
                Assert(result.Length == 2 && result[0].Company == "Claude" && result[0].Level == StatusLevel.Operational && result[1].Company == "OpenAI" && result[1].Level == StatusLevel.Unknown);
            }
            Assert(handler.Requests.Select(r => r.RequestUri.AbsoluteUri).OrderBy(u => u).SequenceEqual(new[] { "https://status.claude.com/api/v2/summary.json", "https://status.openai.com/api/v2/summary.json" }));
            Assert(handler.Requests.All(r => r.Method == HttpMethod.Get && r.Headers.Authorization == null && !r.Headers.Contains("Cookie")));
        });
        Check("A weekly reset is announced for one hour; the first observation and 5-hour windows are not", () => {
            var tracker = new ResetTracker();
            var reset = T.AddHours(2);
            Assert(tracker.Observe(new[] { Fresh("Claude", T, UsageWindow.Week(90, reset), UsageWindow.FiveHour(50, T.AddMinutes(30))) }, T).Length == 0);
            var after = T.AddHours(2).AddMinutes(10);
            var notices = tracker.Observe(new[] { Fresh("Claude", after, UsageWindow.Week(1, reset.AddDays(7)), UsageWindow.FiveHour(1, T.AddHours(5))) }, after);
            Assert(notices.Length == 1 && notices[0].Service == "Claude" && notices[0].Label == "週" && notices[0].At == reset);
            Assert(tracker.Observe(new[] { Fresh("Claude", after.AddMinutes(30), UsageWindow.Week(2, reset.AddDays(7))) }, after.AddMinutes(30)).Length == 1);
            Assert(tracker.Observe(new[] { Fresh("Claude", reset.AddMinutes(61), UsageWindow.Week(3, reset.AddDays(7))) }, reset.AddMinutes(61)).Length == 0);
        });
        Check("Saved or failed values never produce a reset notice", () => {
            var tracker = new ResetTracker();var reset = T.AddHours(1);
            tracker.Observe(new[] { Fresh("Codex", T, UsageWindow.Week(50, reset)) }, T);
            var cached = Fresh("Codex", reset.AddMinutes(5), UsageWindow.Week(1, reset.AddDays(7)));cached.Source = "CLI 記録";
            var failed = Fresh("Codex", reset.AddMinutes(5), UsageWindow.Week(1, reset.AddDays(7)));failed.Status = "接続できません";
            Assert(tracker.Observe(new[] { cached }, reset.AddMinutes(5)).Length == 0 && tracker.Observe(new[] { failed }, reset.AddMinutes(5)).Length == 0);
        });
        Check("The summary shows outages first, then resets, then normal", () => {
            var major = new ProviderStatus { Company = "OpenAI", Level = StatusLevel.Major, Detail = "Codex in ChatGPT Desktop" };
            var minor = new ProviderStatus { Company = "Claude", Level = StatusLevel.Minor, Detail = "Claude Code" };
            var ok = new ProviderStatus { Company = "Claude", Level = StatusLevel.Operational, Detail = "" };
            var okOpenAI = new ProviderStatus { Company = "OpenAI", Level = StatusLevel.Operational, Detail = "" };
            var unknown = new ProviderStatus { Company = "OpenAI", Level = StatusLevel.Unknown, Detail = "" };
            var reset = new[] { new ResetNotice { Service = "Claude", Label = "週（Fable）", ShortLabel = "Fable", At = T } };
            var a = StatusSummary.Create(new[] { minor, major }, reset, T);
            Assert(a.Tone == "critical" && a.Title == "OpenAI 障害" && a.Detail == "Codex in ChatGPT Desktop" && a.Page == "https://status.openai.com/" && a.Line == "OpenAI 障害（Codex in ChatGPT Desktop）");
            var b = StatusSummary.Create(new[] { minor, okOpenAI }, reset, T);
            Assert(b.Tone == "warning" && b.Title == "Claude 一部不調" && b.Page == "https://status.claude.com/");
            var c = StatusSummary.Create(new[] { ok, okOpenAI }, reset, T);
            Assert(c.Tone == "reset" && c.Title == "Claude Fable リセット" && c.Page == null && c.Line == "Claude 週（Fable） がリセットされました");
            var d = StatusSummary.Create(new[] { ok, okOpenAI }, new ResetNotice[0], T);
            Assert(d.Tone == "normal" && d.Title == "正常" && d.Line == "AIの状況：正常");
            var e = StatusSummary.Create(new[] { ok, unknown }, new ResetNotice[0], T);
            Assert(e.Tone == "normal" && e.Line == "AIの状況：正常（OpenAI は確認できません）");
            var f = StatusSummary.Create(new[] { new ProviderStatus { Company = "Claude", Level = StatusLevel.Unknown, Detail = "" }, unknown }, new ResetNotice[0], T);
            Assert(f.Tone == "unknown" && f.Title == "確認できません" && f.Line == "AIの状況：障害情報を確認できません");
        });
    }
}
