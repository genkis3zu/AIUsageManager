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
    public static int Main()
    {
        Check("Claude explicit zero is acquired", () => {
            var r = UsageParser.Claude("{\"five_hour\":{\"utilization\":0},\"seven_day\":{\"utilization\":98}}", "max", T, "API");
            Assert(r.Windows[0].Percent == 0 && r.Windows[1].Percent == 98 && r.RecordedAt == T);
        });
        Check("Claude independently missing window", () => {
            var r = UsageParser.Claude("{\"five_hour\":null,\"seven_day\":{\"utilization\":20}}", "max", T, "API");
            Assert(!r.Windows[0].Percent.HasValue && r.Windows[1].Percent == 20);
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
        Check("Codex weekly secondary ignores 5h primary", () => {
            var r=UsageParser.Codex(Codex("{\"primary\":{\"window_minutes\":300,\"used_percent\":99},\"secondary\":{\"window_minutes\":10080,\"used_percent\":0}}"));
            Assert(r != null && r.Windows[0].Percent==0);
        });
        Check("Codex other limit IDs ignored", () => Assert(UsageParser.Codex(Codex("{\"limit_id\":\"review\",\"primary\":{\"window_minutes\":10080,\"used_percent\":99}}"))==null));
        Check("Codex five-hour-only is not weekly", () => Assert(UsageParser.Codex(Codex("{\"primary\":{\"window_minutes\":300,\"used_percent\":99}}"))==null));
        Check("Codex prompt-shaped data ignored", () => Assert(UsageParser.Codex("{\"type\":\"response_item\",\"payload\":{\"rate_limits\":{\"primary\":{\"window_minutes\":10080,\"used_percent\":99}}}}") == null));
        Check("Grok log schema and observation time", () => {
            var r=UsageParser.Grok("{\"ts\":\"2026-10-10T09:00:00Z\",\"ctx\":{\"subscriptionTier\":\"SuperGrok Heavy\",\"config\":{\"creditUsagePercent\":42,\"currentPeriod\":{\"type\":\"USAGE_PERIOD_TYPE_WEEKLY\",\"end\":\"2026-10-13T09:01:54.789425+00:00\"}}}}", null,"CLI 記録");
            Assert(r.Windows[0].Percent==42 && r.RecordedAt==T && r.Plan=="SuperGrok Heavy" && r.Windows[0].ResetsAt.HasValue);
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
        Check("Expired data requests fresh observation", () => Assert(UsageText.Reset(new UsageWindow("週",50,T), T.AddMinutes(1)).Contains("再取得待ち")));
        RunProviderTests();
        RunWidgetTests();
        RunAlertTests();
        RunSnapshotTests();
        Console.WriteLine(string.Format("RESULT {0} passed, {1} failed", passed, failed));
        return failed==0 ? 0 : 1;
    }
}
