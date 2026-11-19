using System;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace AIUsageManager
{
    internal static class Json
    {
        public static Dictionary<string, object> Parse(string value)
        {
            try { return new JavaScriptSerializer { MaxJsonLength = 2097152, RecursionLimit = 64 }.DeserializeObject(value) as Dictionary<string, object>; }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
        }
        public static object Get(Dictionary<string, object> data, string key)
        { object value; return data != null && data.TryGetValue(key, out value) ? value : null; }
        public static Dictionary<string, object> Obj(Dictionary<string, object> data, string key)
        { return Get(data, key) as Dictionary<string, object>; }
        public static string Str(Dictionary<string, object> data, string key)
        { return Get(data, key) as string; }
        public static double? Number(object value)
        {
            if (!(value is int || value is long || value is decimal || value is double || value is float)) return null;
            double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return double.IsNaN(number) || double.IsInfinity(number) ? (double?)null : number;
        }
        public static double? Percent(object value)
        { var n = Number(value); return n.HasValue && n.Value >= 0 && n.Value <= 100000 ? n : null; }
        public static DateTimeOffset? Date(object value)
        {
            var n = Number(value);
            if (n.HasValue)
            {
                try { return new DateTimeOffset(1970,1,1,0,0,0,TimeSpan.Zero).AddSeconds(n.Value > 100000000000 ? n.Value / 1000 : n.Value); }
                catch (ArgumentOutOfRangeException) { return null; }
            }
            DateTimeOffset result;
            return value is string && DateTimeOffset.TryParse((string)value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out result) ? result : (DateTimeOffset?)null;
        }
        public static string Plan(string name, string tier)
        {
            switch ((name ?? "").Trim().ToLowerInvariant())
            {
                // Anthropic renamed Max 20x to Ultra; the $100 plan is now just Max. The internal tier still says max_20x.
                case "max": return (tier ?? "").Contains("20x") ? "Ultra" : "Max";
                case "ultra": return "Ultra";
                case "pro": return "Pro";
                case "prolite": case "pro_lite": return "Pro Lite";
                case "proplus": case "pro_plus": return "Pro Plus";
                case "plus": return "Plus";
                case "free": return "Free";
                case "team": return "Team";
                case "business": return "Business";
                case "enterprise": return "Enterprise";
                case "edu": return "Edu";
                case "premium": return "Premium";
                case "premium+": return "Premium+";
                default: return "プラン未取得";
            }
        }
        public static string GrokPlan(string name)
        {
            // Legacy subscriptions remain valid; Heavy does not prove an Ultra migration.
            switch ((name ?? "").Trim().ToLowerInvariant())
            {
                case "free": return "Free";
                case "plus": return "Plus";
                case "premium": return "Premium";
                case "super": return "Super";
                case "ultra": return "Ultra";
                case "supergrok": return "SuperGrok";
                case "supergrok plus": return "SuperGrok Plus";
                case "supergrok heavy": return "SuperGrok Heavy";
                case "supergrok lite": return "SuperGrok Lite";
                case "premium+": return "Premium+";
                default: return "プラン未取得";
            }
        }
    }
    public static class UsageParser
    {
        // Claude reports its limits in a `limits` list (session = 5 hours, weekly_all, weekly_scoped per model).
        // The older five_hour / seven_day fields fill in a 5-hour or weekly window the list does not contain.
        public static ServiceUsage Claude(string json, string plan, DateTimeOffset? time, string source)
        {
            var result = new ServiceUsage { Name = "Claude", Plan = Json.Plan(plan, null), RecordedAt = time, Source = source };
            var root = Json.Parse(json);
            var usage = Json.Obj(root, "rate_limits") ?? root;
            UsageWindow session = null, weekly = null;
            var scoped = new List<UsageWindow>();
            var limits = Json.Get(root, "limits") as object[];
            if (limits != null)
                foreach (var item in limits)
                {
                    var entry = item as Dictionary<string, object>;
                    var percent = Json.Percent(Json.Get(entry, "percent") ?? Json.Get(entry, "utilization"));
                    if (!percent.HasValue) continue;
                    var reset = Json.Date(Json.Get(entry, "resets_at"));
                    switch (Json.Str(entry, "kind"))
                    {
                        case "session": session = Higher(session, UsageWindow.FiveHour(percent, reset)); break;
                        case "weekly_all": weekly = Higher(weekly, UsageWindow.Week(percent, reset)); break;
                        case "weekly_scoped":
                            var window = UsageWindow.Scoped(Json.Str(Json.Obj(Json.Obj(entry, "scope"), "model"), "display_name"), percent, reset);
                            int index = scoped.FindIndex(w => w.Scope == window.Scope);
                            if (index < 0) scoped.Add(window); else scoped[index] = Higher(scoped[index], window);
                            break;
                    }
                }
            if (session == null) session = LegacyWindow(usage, "five_hour", UsageSpan.FiveHour);
            if (weekly == null) weekly = LegacyWindow(usage, "seven_day", UsageSpan.Week);
            if (session != null) result.Windows.Add(session);
            if (weekly != null) result.Windows.Add(weekly);
            result.Windows.AddRange(scoped);
            if (result.Windows.Count > 4) result.Windows.RemoveRange(4, result.Windows.Count - 4);
            if (result.Windows.Count == 0) result.Windows.Add(UsageWindow.Week(null, null));
            if (!result.HasData) result.Status = "使用量を取得できません";
            return result;
        }
        // Duplicates keep the higher percent so a warning is never delayed by the lower copy.
        static UsageWindow Higher(UsageWindow current, UsageWindow candidate)
        { return current == null || candidate.Percent > current.Percent ? candidate : current; }
        static UsageWindow LegacyWindow(Dictionary<string, object> usage, string key, UsageSpan span)
        {
            var window = Json.Obj(usage, key);
            var percent = Json.Percent(Json.Get(window, "utilization") ?? Json.Get(window, "used_percentage"));
            if (!percent.HasValue) return null;
            var reset = Json.Date(Json.Get(window, "resets_at"));
            return span == UsageSpan.FiveHour ? UsageWindow.FiveHour(percent, reset) : UsageWindow.Week(percent, reset);
        }
        public static ServiceUsage Codex(string line)
        {
            var root = Json.Parse(line);
            var payload = Json.Obj(root, "payload");
            if (Json.Str(root, "type") != "event_msg" || Json.Str(payload, "type") != "token_count") return null;
            var limits = Json.Obj(payload, "rate_limits");
            var id = Json.Str(limits, "limit_id");
            if (limits == null || (!string.IsNullOrEmpty(id) && id != "codex")) return null;
            foreach (string key in new[] { "primary", "secondary" })
            {
                var window = Json.Obj(limits, key);
                if (Json.Number(Json.Get(window, "window_minutes")) != 10080) continue;
                var percent = Json.Percent(Json.Get(window, "used_percent"));
                if (!percent.HasValue) continue;
                var result = ServiceUsage.Empty("Codex", "");
                result.Plan = Json.Plan(Json.Str(limits, "plan_type"), null);
                result.Source = "CLI 記録"; result.RecordedAt = Json.Date(Json.Get(root, "timestamp"));
                result.Windows[0].Percent = percent;
                result.Windows[0].ResetsAt = Json.Date(Json.Get(window, "resets_at"));
                return result;
            }
            return null;
        }
        public static ServiceUsage CodexOnline(string json, DateTimeOffset time)
        {
            var result = ServiceUsage.Empty("Codex", "週の使用量を取得できません");
            var root = Json.Parse(json);
            result.Plan = Json.Plan(Json.Str(root, "plan_type"), null);
            result.Source = "オンライン"; result.RecordedAt = time;
            var limits = Json.Obj(root, "rate_limit");
            foreach (string key in new[] { "primary_window", "secondary_window" })
            {
                var window = Json.Obj(limits, key);
                if (Json.Number(Json.Get(window, "limit_window_seconds")) != 604800) continue;
                result.Windows[0].Percent = Json.Percent(Json.Get(window, "used_percent"));
                result.Windows[0].ResetsAt = Json.Date(Json.Get(window, "reset_at"));
                if (result.HasData) result.Status = "";
                break;
            }
            return result;
        }
        public static ServiceUsage Grok(string json, DateTimeOffset? time, string source)
        {
            var result = ServiceUsage.Empty("Grok", "週の使用量を取得できません");
            var root = Json.Parse(json);
            var body = Json.Obj(root, "ctx") ?? root;
            result.Plan = Json.GrokPlan(Json.Str(body, "subscriptionTier"));
            result.Source = source; result.RecordedAt = time ?? Json.Date(Json.Get(root, "ts"));
            var config = Json.Obj(body, "config");
            var period = Json.Obj(config, "currentPeriod");
            string kind = Json.Str(period, "type") ?? Json.Str(period, "periodType");
            var start = Json.Date(Json.Get(period, "start"));
            var end = Json.Date(Json.Get(period, "end"));
            bool weekly = kind != null ? kind.IndexOf("weekly", StringComparison.OrdinalIgnoreCase) >= 0 :
                start.HasValue && end.HasValue && Math.Abs((end.Value - start.Value).TotalDays - 7) < 0.1;
            if (!weekly) return result;
            result.Windows[0].Percent = Json.Percent(Json.Get(config, "creditUsagePercent"));
            result.Windows[0].ResetsAt = end;
            if (result.HasData) result.Status = "";
            else if (source == "オンライン") result.Status = config.ContainsKey("creditUsagePercent") ? "使用率の形式を確認できません" : "認証済み・使用率は未報告です";
            return result;
        }
    }
}
