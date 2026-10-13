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
                case "max": return (tier ?? "").Contains("20x") ? "Max 20x" : ((tier ?? "").Contains("5x") ? "Max 5x" : "Max");
                case "pro": return "Pro";
                case "prolite": case "pro_lite": return "Pro Lite";
                case "proplus": case "pro_plus": return "Pro Plus";
                case "plus": return "Plus";
                case "free": return "Free";
                case "team": return "Team";
                case "business": return "Business";
                case "enterprise": return "Enterprise";
                case "edu": return "Edu";
                case "supergrok": return "SuperGrok";
                case "supergrok heavy": return "SuperGrok Heavy";
                case "supergrok lite": return "SuperGrok Lite";
                case "premium": return "Premium";
                case "premium+": return "Premium+";
                default: return "プラン未取得";
            }
        }
    }
    public static class UsageParser
    {
        public static ServiceUsage Claude(string json, string plan, DateTimeOffset? time, string source)
        {
            var result = ServiceUsage.Empty("Claude", "");
            result.Plan = Json.Plan(plan, null); result.RecordedAt = time; result.Source = source;
            var root = Json.Parse(json);
            var usage = Json.Obj(root, "rate_limits") ?? root;
            string[] keys = { "five_hour", "seven_day" };
            for (int i = 0; i < keys.Length; i++)
            {
                var window = Json.Obj(usage, keys[i]);
                result.Windows[i].Percent = Json.Percent(Json.Get(window, "utilization") ?? Json.Get(window, "used_percentage"));
                result.Windows[i].ResetsAt = Json.Date(Json.Get(window, "resets_at"));
            }
            if (!result.HasData) result.Status = "使用量を取得できません";
            return result;
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
            result.Plan = Json.Plan(Json.Str(body, "subscriptionTier"), null);
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
