using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AIUsageManager
{
    public enum StatusLevel { Unknown, Operational, Minor, Major }
    public sealed class ProviderStatus
    {
        public string Company;
        public StatusLevel Level;
        public string Detail = "";
    }
    public sealed class ResetNotice
    {
        public string Service, Label, ShortLabel;
        public DateTimeOffset At;
    }
    // Reads Atlassian Statuspage-compatible summaries (status.claude.com, status.openai.com).
    public static class StatusParser
    {
        public static ProviderStatus Claude(string json)
        {
            // Only the products this app is about; e.g. Claude for Government must not mark Claude as degraded.
            return Parse("Claude", json, name => name.StartsWith("claude.ai", StringComparison.Ordinal) ||
                name.StartsWith("Claude Code", StringComparison.Ordinal) || name.StartsWith("Claude API", StringComparison.Ordinal), false);
        }
        public static ProviderStatus OpenAI(string json)
        {
            // OpenAI's JSON lists only part of the Codex components, so the overall indicator is combined in.
            return Parse("OpenAI", json, name => name.IndexOf("Codex", StringComparison.Ordinal) >= 0 || name.IndexOf("ChatGPT", StringComparison.Ordinal) >= 0, true);
        }
        static ProviderStatus Parse(string company, string json, Func<string, bool> relevant, bool combineOverall)
        {
            var result = new ProviderStatus { Company = company, Level = StatusLevel.Unknown };
            var root = Json.Parse(json);
            var overall = Overall(Json.Str(Json.Obj(root, "status"), "indicator"));
            if (!overall.HasValue) return result;
            var components = StatusLevel.Operational; bool found = false; var names = new List<string>();
            var list = Json.Get(root, "components") as object[];
            if (list != null)
                foreach (var item in list)
                {
                    var component = item as Dictionary<string, object>;
                    string name = Json.Str(component, "name");
                    if (name == null || !relevant(name)) continue;
                    found = true;
                    var level = Component(Json.Str(component, "status"));
                    if (level <= StatusLevel.Operational) continue;
                    if (names.Count < 3) names.Add(Clean(name));
                    if (level > components) components = level;
                }
            result.Level = combineOverall ? (StatusLevel)Math.Max((int)overall.Value, (int)components) : (found ? components : overall.Value);
            result.Detail = string.Join("、", names);
            return result;
        }
        static StatusLevel? Overall(string indicator)
        {
            switch (indicator)
            {
                case "none": return StatusLevel.Operational;
                case "minor": case "maintenance": return StatusLevel.Minor;
                case "major": case "critical": return StatusLevel.Major;
                default: return null;
            }
        }
        static StatusLevel Component(string status)
        {
            switch (status)
            {
                case "major_outage": return StatusLevel.Major;
                case "partial_outage": case "degraded_performance": case "under_maintenance": return StatusLevel.Minor;
                default: return StatusLevel.Operational;
            }
        }
        // Component names come from outside: no control characters, single spaces, at most 40 characters.
        static string Clean(string name)
        {
            var text = new StringBuilder(); bool space = false;
            foreach (char c in name)
            {
                if (char.IsWhiteSpace(c)) { space = text.Length > 0; continue; }
                if (char.IsControl(c)) continue;
                if (space) { text.Append(' '); space = false; }
                text.Append(c);
            }
            return text.Length > 40 ? text.ToString(0, 40).TrimEnd() : text.ToString();
        }
    }
    public static class StatusPages
    {
        public const string ClaudeSummary = "https://status.claude.com/api/v2/summary.json", OpenAISummary = "https://status.openai.com/api/v2/summary.json";
        public static async Task<ProviderStatus[]> FetchAsync(HttpClient http, CancellationToken cancellation)
        {
            var claude = Fetch(http, ClaudeSummary, StatusParser.Claude, "Claude", cancellation);
            var openai = Fetch(http, OpenAISummary, StatusParser.OpenAI, "OpenAI", cancellation);
            return await Task.WhenAll(claude, openai).ConfigureAwait(false);
        }
        // Public pages: plain GET without credentials or cookies; any failure means "could not check".
        static async Task<ProviderStatus> Fetch(HttpClient http, string url, Func<string, ProviderStatus> parse, string company, CancellationToken cancellation)
        {
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    using (var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode) return new ProviderStatus { Company = company, Level = StatusLevel.Unknown };
                        return parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    }
                }
            }
            catch (Exception) { return new ProviderStatus { Company = company, Level = StatusLevel.Unknown }; }
        }
    }
    // Notices that a weekly window has reset, from fresh online values only.
    public sealed class ResetTracker
    {
        readonly Dictionary<string, DateTimeOffset> last = new Dictionary<string, DateTimeOffset>();
        readonly List<ResetNotice> recent = new List<ResetNotice>();
        public ResetNotice[] Observe(IEnumerable<ServiceUsage> values, DateTimeOffset now)
        {
            foreach (var service in values)
            {
                if (service == null || service.Source != "オンライン" || service.Status != "" || service.NeedsLogin) continue;
                foreach (var window in service.Windows)
                {
                    if (window.Span != UsageSpan.Week || !window.ResetsAt.HasValue) continue;
                    string key = service.Name + "/" + window.Label; DateTimeOffset previous;
                    // The first value only records; a reset is the period moving past the previously known reset time.
                    if (last.TryGetValue(key, out previous) && previous <= now && window.ResetsAt.Value > previous.AddMinutes(1) &&
                        !recent.Any(n => n.Service == service.Name && n.Label == window.Label && n.At == previous))
                        recent.Add(new ResetNotice { Service = service.Name, Label = window.Label, ShortLabel = window.ShortLabel, At = previous });
                    last[key] = window.ResetsAt.Value;
                }
            }
            recent.RemoveAll(n => now - n.At > TimeSpan.FromHours(1) || n.At > now);
            return recent.OrderByDescending(n => n.At).ToArray();
        }
    }
    // What the tray line and the Stream Deck key show: outage > partial > recent reset > normal.
    public sealed class StatusSummary
    {
        public const string ClaudePage = "https://status.claude.com/", OpenAIPage = "https://status.openai.com/";
        public string Tone, Title, Detail = "", Line, Page;
        // Each company's own state, so the display can say what is normal rather than only "正常".
        public ProviderStatus[] Providers = new ProviderStatus[0];
        public static StatusSummary Create(ProviderStatus[] providers, ResetNotice[] resets, DateTimeOffset now)
        {
            var summary = Choose(providers ?? new ProviderStatus[0], resets ?? new ResetNotice[0]);
            summary.Providers = (providers ?? new ProviderStatus[0]).Where(p => p != null).ToArray();
            return summary;
        }
        public static string ToneOf(StatusLevel level)
        {
            return level == StatusLevel.Major ? "critical" : level == StatusLevel.Minor ? "warning" : level == StatusLevel.Operational ? "normal" : "unknown";
        }
        static StatusSummary Choose(ProviderStatus[] providers, ResetNotice[] resets)
        {
            // Every company with a problem is named: the worse one leads (Claude first on a tie) and sets the colour and page,
            // so an outage at one company never hides a problem at the other.
            var problems = providers.Where(p => p != null && p.Level >= StatusLevel.Minor).OrderByDescending(p => p.Level).ToArray();
            if (problems.Length > 0)
            {
                var worst = problems[0]; var others = problems.Skip(1).ToArray();
                return new StatusSummary { Tone = worst.Level == StatusLevel.Major ? "critical" : "warning", Title = worst.Company + " " + State(worst),
                    Detail = others.Length == 0 ? worst.Detail ?? "" : string.Join("、", others.Select(p => p.Company + " も" + State(p) + InBrackets(p.Detail))),
                    Page = worst.Company == "OpenAI" ? OpenAIPage : ClaudePage,
                    Line = string.Join("・", problems.Select(p => p.Company + " " + State(p) + InBrackets(p.Detail))) };
            }
            var reset = resets.OrderByDescending(r => r.At).FirstOrDefault();
            if (reset != null)
                return new StatusSummary { Tone = "reset", Title = reset.Service + " " + reset.ShortLabel + " リセット", Detail = reset.Label, Line = reset.Service + " " + reset.Label + " がリセットされました" };
            var unknown = providers.Where(p => p == null || p.Level == StatusLevel.Unknown).Select(p => p == null ? "" : p.Company).ToArray();
            if (providers.Length == 0 || unknown.Length == providers.Length)
                return new StatusSummary { Tone = "unknown", Title = "確認できません", Line = "AIの状況：障害情報を確認できません" };
            string missing = string.Join("、", unknown);
            return new StatusSummary { Tone = "normal", Title = "正常", Detail = unknown.Length > 0 ? missing + " は確認できません" : "",
                Line = "AIの状況：" + string.Join("・", providers.Where(p => p != null).Select(p => p.Company + (p.Level == StatusLevel.Unknown ? " 確認できず" : " 正常"))) };
        }
        static string State(ProviderStatus p) { return p.Level == StatusLevel.Major ? "障害" : "一部不調"; }
        static string InBrackets(string detail) { return string.IsNullOrEmpty(detail) ? "" : "（" + detail + "）"; }
    }
}
