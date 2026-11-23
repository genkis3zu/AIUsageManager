using System;
using System.Linq;
using AIUsageManager;

internal static partial class Tests
{
    static int passed, failed;
    static DateTimeOffset T = DateTimeOffset.Parse("2026-10-10T09:00:00Z");
    static void Check(string name, Action test)
    {
        try { test(); passed++; Console.WriteLine("PASS " + name); }
        catch { failed++; Console.WriteLine("FAIL " + name); }
    }
    static void Assert(bool value) { if (!value) throw new Exception("assertion"); }
    static string Codex(string limits)
    { return "{\"timestamp\":\"2026-10-10T09:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"rate_limits\":" + limits + "}}"; }
    // Fixture JSON written with single quotes for readability.
    static string J(string value) { return value.Replace('\'', '"'); }
    static string Labels(ServiceUsage value) { return string.Join("|", value.Windows.Select(w => w.Label)); }
    static string Limit(string kind, int percent, string model)
    {
        return "{'kind':'" + kind + "','group':'weekly','percent':" + percent + ",'severity':'normal','resets_at':'2026-11-22T23:00:00.123+00:00'," +
            "'scope':" + (model == null ? "null" : "{'model':{'id':null,'display_name':'" + model + "'},'surface':null}") + ",'is_active':false}";
    }
    public static int Main()
    {
        Check("Claude explicit zero is acquired", () => {
            var r = UsageParser.Claude("{\"five_hour\":{\"utilization\":0},\"seven_day\":{\"utilization\":98}}", "max", T, "API");
            Assert(r.Windows[0].Percent == 0 && r.Windows[1].Percent == 98 && r.RecordedAt == T);
        });
        Check("Claude window the service does not return is not shown", () => {
            var r = UsageParser.Claude("{\"five_hour\":null,\"seven_day\":{\"utilization\":20}}", "max", T, "API");
            Assert(Labels(r) == "週" && r.Windows[0].Percent == 20);
        });
        Check("Claude Ultra limits give 週 and 週（Fable） without 5時間", () => {
            var r = UsageParser.Claude(J("{'five_hour':null,'seven_day':{'utilization':40,'resets_at':'2026-11-22T23:00:00.123+00:00'},'limits':[" + Limit("weekly_all", 40, null) + "," + Limit("weekly_scoped", 70, "Fable") + "]}"), "max", T, "API");
            Assert(Labels(r) == "週|週（Fable）" && string.Join("|", r.Windows.Select(w => w.ShortLabel)) == "週|Fable");
            Assert(r.Windows[0].Percent == 40 && r.Windows[1].Percent == 70 && r.Windows.All(w => w.Span == UsageSpan.Week && w.ResetsAt.HasValue));
            Assert(r.Windows[0].Scope == null && r.Windows[1].Scope == "Fable" && r.Status == "");
        });
        Check("Claude Max limits give 5時間, 週 and 週（Fable） in order", () => {
            var r = UsageParser.Claude(J("{'limits':[" + Limit("weekly_scoped", 10, "Fable") + "," + Limit("weekly_all", 20, null) + "," + Limit("session", 30, null) + "]}"), "max", T, "API");
            Assert(Labels(r) == "5時間|週|週（Fable）" && r.Windows[0].Span == UsageSpan.FiveHour && r.Windows[0].ShortLabel == "5h");
            Assert(r.Windows[0].Percent == 30 && r.Windows[1].Percent == 20 && r.Windows[2].Percent == 10);
        });
        Check("Claude limits with only unknown kinds fall back to legacy fields", () => {
            var r = UsageParser.Claude(J("{'limits':[{'kind':'future_kind','percent':5}],'five_hour':{'utilization':10},'seven_day':{'utilization':20}}"), "max", T, "API");
            Assert(Labels(r) == "5時間|週" && r.Windows[0].Percent == 10 && r.Windows[1].Percent == 20);
        });
        Check("Claude legacy five_hour fills a missing session window", () => {
            var r = UsageParser.Claude(J("{'limits':[" + Limit("weekly_all", 25, null) + "],'five_hour':{'utilization':12},'seven_day':{'utilization':99}}"), "max", T, "API");
            Assert(Labels(r) == "5時間|週" && r.Windows[0].Percent == 12 && r.Windows[1].Percent == 25);
        });
        Check("Claude duplicate windows keep the higher percent", () => {
            var r = UsageParser.Claude(J("{'limits':[" + Limit("weekly_all", 30, null) + "," + Limit("weekly_all", 55, null) + "," + Limit("weekly_scoped", 60, "Fable") + "," + Limit("weekly_scoped", 15, "Fable") + "]}"), "max", T, "API");
            Assert(Labels(r) == "週|週（Fable）" && r.Windows[0].Percent == 55 && r.Windows[1].Percent == 60);
        });
        Check("Claude limits with invalid percent are ignored", () => {
            var r = UsageParser.Claude(J("{'limits':[{'kind':'weekly_all','percent':'bad'},{'kind':'session','percent':-3}],'seven_day':{'utilization':33}}"), "max", T, "API");
            Assert(Labels(r) == "週" && r.Windows[0].Percent == 33);
        });
        Check("Claude model names are cleaned, never dropped", () => {
            var r = UsageParser.Claude(J("{'limits':[" + Limit("weekly_scoped", 1, "Fable\\u0007  (Preview) <b>") + "," + Limit("weekly_scoped", 2, "\\u0001\\u0002") + "," + Limit("weekly_scoped", 3, "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmn") + "]}"), "max", T, "API");
            Assert(Labels(r) == "週（Fable (Preview) b）|週（モデル別）|週（ABCDEFGHIJKLMNOPQRSTUVWX）" && r.Windows[1].ShortLabel == "モデル別");
            var missing = UsageParser.Claude(J("{'limits':[{'kind':'weekly_scoped','percent':4}]}"), "max", T, "API");
            Assert(Labels(missing) == "週（モデル別）");
        });
        Check("Claude more than four windows keep the first four", () => {
            var r = UsageParser.Claude(J("{'limits':[" + Limit("session", 1, null) + "," + Limit("weekly_all", 2, null) + "," + Limit("weekly_scoped", 3, "A") + "," + Limit("weekly_scoped", 4, "B") + "," + Limit("weekly_scoped", 5, "C") + "]}"), "max", T, "API");
            Assert(Labels(r) == "5時間|週|週（A）|週（B）");
        });
        Check("Claude response without any window keeps one unknown weekly placeholder", () => {
            var r = UsageParser.Claude("{}", "max", T, "API");
            Assert(Labels(r) == "週" && !r.HasData && r.Status == "使用量を取得できません");
        });
        Check("Compact reset text is short enough for narrow columns", () => {
            Assert(UsageText.CompactReset(UsageWindow.Week(50, T.AddDays(2).AddHours(3)), T) == "2d3h");
            Assert(UsageText.CompactReset(UsageWindow.Week(50, T.AddMinutes(-1)), T) == "期限経過" && UsageText.CompactReset(UsageWindow.Week(50, null), T) == "時刻不明");
        });
        Check("App location has no default when local app data is unknown", () => Assert(AppLocation.DefaultPath("") == null && AppLocation.DefaultPath(null) == null && AppLocation.DefaultPath(@"C:\L").EndsWith(@"AIUsageManager\app-location.json")));
        Check("Tray summary names each window", () => {
            var claude = new ServiceUsage { Name = "Claude" }; claude.Windows.Add(UsageWindow.Week(74, T)); claude.Windows.Add(UsageWindow.Scoped("Fable", 91, T));
            var codex = ServiceUsage.Empty("Codex", ""); codex.Windows[0].Percent = 84;
            var grok = ServiceUsage.Empty("Grok", "");
            Assert(UsageText.Summary(new[] { claude, codex, grok }) == "Claude 週74%/Fable91% | Codex 週84% | Grok 週未取得");
            var max = new ServiceUsage { Name = "Claude" }; max.Windows.Add(UsageWindow.FiveHour(10, T)); max.Windows.Add(UsageWindow.Week(20, T)); max.Windows.Add(UsageWindow.Scoped("ABCDEFGHIJKLMNOPQRSTUVWXYZ", 30, T));
            Assert(UsageText.Summary(new[] { max, codex, grok }).Length <= 63);
        });
        Check("Claude plan names follow Ultra and Max", () => {
            Assert(Json.Plan("max", "default_claude_max_20x") == "Ultra" && Json.Plan("max", "default_claude_max_5x") == "Max");
            Assert(Json.Plan("max", null) == "Max" && Json.Plan("ultra", null) == "Ultra" && Json.Plan("pro", null) == "Pro");
        });
        Check("Claude statusline percentages and Unix reset", () => {
            var r = UsageParser.Claude("{\"rate_limits\":{\"five_hour\":{\"used_percentage\":2,\"resets_at\":1791669600}}}", "max", T, "cache");
            Assert(r.Windows[0].Percent == 2 && r.Windows[0].ResetsAt.HasValue);
        });
        Check("Missing percentage stays unknown", () => Assert(!UsageParser.Claude("{\"five_hour\":{\"resets_at\":1791669600}}", "max", T, "API").HasData));
        Check("Invalid negative and nonnumeric percentages stay unknown", () => Assert(!UsageParser.Claude("{\"five_hour\":{\"utilization\":-1},\"seven_day\":{\"utilization\":\"bad\"}}", "max", T, "API").HasData));
        Check("Invalid JSON cannot become a measurement", () => Assert(!UsageParser.Claude("{partial", "max", T, "API").HasData));
        Check("Codex weekly primary", () => {
            var r=UsageParser.Codex(Codex("{\"limit_id\":\"codex\",\"primary\":{\"window_minutes\":10080,\"used_percent\":50,\"resets_at\":1791952268},\"plan_type\":\"prolite\"}"));
            Assert(r != null && r.Windows.Single().Percent==50 && r.RecordedAt==T && r.Plan=="Pro Lite");
        });
        Check("Codex log 300 and 10080 minutes give 5時間 and 週", () => {
            var r=UsageParser.Codex(Codex("{\"primary\":{\"window_minutes\":300,\"used_percent\":99},\"secondary\":{\"window_minutes\":10080,\"used_percent\":0}}"));
            Assert(r != null && Labels(r)=="5時間|週" && r.Windows[0].Percent==99 && r.Windows[1].Percent==0);
        });
        Check("Codex log other window lengths are ignored", () => {
            var r=UsageParser.Codex(Codex("{\"primary\":{\"window_minutes\":1440,\"used_percent\":10},\"secondary\":{\"window_minutes\":10080,\"used_percent\":5}}"));
            Assert(r != null && Labels(r)=="週" && r.Windows[0].Percent==5);
        });
        Check("Codex other limit IDs ignored", () => Assert(UsageParser.Codex(Codex("{\"limit_id\":\"review\",\"primary\":{\"window_minutes\":10080,\"used_percent\":99}}"))==null));
        Check("Codex five-hour-only log gives 5時間", () => {
            var r=UsageParser.Codex(Codex("{\"primary\":{\"window_minutes\":300,\"used_percent\":99}}"));
            Assert(r != null && Labels(r)=="5時間" && r.Windows[0].Percent==99);
        });
        Check("Codex online Plus gives 5時間 and 週, other lengths ignored", () => {
            var r=UsageParser.CodexOnline(J("{'plan_type':'plus','rate_limit':{'primary_window':{'used_percent':12,'limit_window_seconds':18000,'reset_at':1791669600},'secondary_window':{'used_percent':34,'limit_window_seconds':604800,'reset_at':1791952268}},'code_review_rate_limit':{'primary_window':{'used_percent':80,'limit_window_seconds':604800}}}"),T);
            Assert(Labels(r)=="5時間|週" && r.Windows[0].Percent==12 && r.Windows[1].Percent==34 && r.Status=="" && r.Plan=="Plus");
            var day=UsageParser.CodexOnline(J("{'rate_limit':{'primary_window':{'used_percent':10,'limit_window_seconds':86400}}}"),T);
            Assert(Labels(day)=="週" && !day.HasData && day.Status=="使用量を取得できません");
        });
        Check("Codex online Pro gives only 週", () => {
            var r=UsageParser.CodexOnline(J("{'plan_type':'prolite','rate_limit':{'primary_window':{'used_percent':7,'limit_window_seconds':604800,'reset_at':1791952268},'secondary_window':null}}"),T);
            Assert(Labels(r)=="週" && r.Windows[0].Percent==7 && r.Status=="");
        });
        Check("Codex online 5-hour only is still acquired", () => {
            var r=UsageParser.CodexOnline(J("{'rate_limit':{'primary_window':{'used_percent':20,'limit_window_seconds':18000}}}"),T);
            Assert(Labels(r)=="5時間" && r.HasData && r.Status=="");
        });
        Check("Codex prompt-shaped data ignored", () => Assert(UsageParser.Codex("{\"type\":\"response_item\",\"payload\":{\"rate_limits\":{\"primary\":{\"window_minutes\":10080,\"used_percent\":99}}}}") == null));
        Check("Grok log schema and observation time", () => {
            var r=UsageParser.Grok("{\"ts\":\"2026-10-10T09:00:00Z\",\"ctx\":{\"subscriptionTier\":\"SuperGrok Heavy\",\"config\":{\"creditUsagePercent\":42,\"currentPeriod\":{\"type\":\"USAGE_PERIOD_TYPE_WEEKLY\",\"end\":\"2026-10-13T09:01:54.789425+00:00\"}}}}", null,"CLI 記録");
            Assert(r.Windows[0].Percent==42 && r.RecordedAt==T && r.Plan=="SuperGrok Heavy" && r.Windows[0].ResetsAt.HasValue);
        });
        Check("Grok recognizes current and legacy plan names without inventing migrations", () => {
            foreach(var plan in new[]{"Free","Plus","Premium","Super","Ultra","SuperGrok","SuperGrok Plus","SuperGrok Heavy","SuperGrok Lite","Premium+"}) {
                var value=UsageParser.Grok("{\"subscriptionTier\":\" "+plan.ToLowerInvariant()+" \"}",T,"API");
                Assert(value.Plan==plan);
            }
            foreach(var plan in new[]{"unrecognized-private-value","Ultra\\nprivate-value","Max","ultra@example.test"})
                Assert(UsageParser.Grok("{\"subscriptionTier\":\""+plan+"\"}",T,"API").Plan=="プラン未取得");
        });
        Check("Grok billing explicit zero", () => Assert(UsageParser.Grok("{\"config\":{\"creditUsagePercent\":0,\"currentPeriod\":{\"periodType\":\"weekly\"}}}", T,"API").Windows[0].Percent==0));
        Check("Grok absent percentage is not zero", () => Assert(!UsageParser.Grok("{\"config\":{\"currentPeriod\":{\"type\":\"weekly\",\"end\":\"2026-10-13T09:00:00Z\"}}}", T,"API").HasData));
        Check("Grok successful period-only response distinguishes unreported usage from login failure", () => {
            var r=UsageParser.Grok("{\"config\":{\"currentPeriod\":{\"type\":\"weekly\",\"start\":\"2026-10-10T08:00:00Z\",\"end\":\"2026-10-17T08:00:00Z\"}}}", T,"オンライン");
            Assert(!r.HasData && !r.NeedsLogin && r.Status.Contains("認証済み") && r.Status.Contains("未報告") && r.Windows[0].ResetsAt.HasValue);
            var malformed=UsageParser.Grok("{\"config\":{\"creditUsagePercent\":\"invalid\",\"currentPeriod\":{\"type\":\"weekly\"}}}",T,"オンライン");
            Assert(!malformed.HasData && malformed.Status.Contains("形式"));
        });
        Check("Grok monthly rejected", () => Assert(!UsageParser.Grok("{\"config\":{\"creditUsagePercent\":42,\"currentPeriod\":{\"type\":\"monthly\"}}}", T,"API").HasData));
        Check("Grok no period rejected", () => Assert(!UsageParser.Grok("{\"config\":{\"creditUsagePercent\":42}}", T,"API").HasData));
        Check("Over-limit percent is preserved", () => Assert(UsageParser.Grok("{\"config\":{\"creditUsagePercent\":125,\"currentPeriod\":{\"type\":\"weekly\"}}}", T,"API").Windows[0].Percent==125));
        Check("Unknown plan never echoes arbitrary input", () => Assert(!UsageParser.Claude("{}", "secret-value", T,"API").Plan.Contains("secret")));
        Check("Unknown date never becomes now", () => Assert(!UsageParser.Claude("{\"five_hour\":{\"utilization\":1,\"resets_at\":999999999999999999}}", "max", null,"API").RecordedAt.HasValue));
        Check("Zero and unknown text differ", () => Assert(UsageText.Percent(0)=="0%" && UsageText.Percent(null)=="未取得"));
        Check("Expired data requests fresh observation", () => Assert(UsageText.Reset(UsageWindow.Week(50,T), T.AddMinutes(1)).Contains("再取得待ち")));
        RunProviderTests();
        RunGrokWebTests();
        RunWidgetTests();
        RunAlertTests();
        RunForecastTests();
        RunSnapshotTests();
        RunStatusTests();
        RunWaitingTests();
        RunStartupTests();
        RunNowDoingTests();
        RunNowDoingActionTests();
        Console.WriteLine(string.Format("RESULT {0} passed, {1} failed", passed, failed));
        return failed==0 ? 0 : 1;
    }
}
