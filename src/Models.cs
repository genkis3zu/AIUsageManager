using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AIUsageManager
{
    public enum UsageSpan { FiveHour, Week }
    // One usage limit as the service reports it: a 5-hour or weekly window, optionally limited to one model.
    public sealed class UsageWindow
    {
        public const string UnnamedModel = "モデル別";
        public readonly UsageSpan Span;
        public readonly string Scope;
        public double? Percent;
        public DateTimeOffset? ResetsAt;
        public UsageAlert Alert;
        public UsagePace Pace;
        UsageWindow(UsageSpan span, string scope, double? percent, DateTimeOffset? resetsAt)
        { Span = span; Scope = scope; Percent = percent; ResetsAt = resetsAt; }
        public static UsageWindow FiveHour(double? percent, DateTimeOffset? resetsAt) { return new UsageWindow(UsageSpan.FiveHour, null, percent, resetsAt); }
        public static UsageWindow Week(double? percent, DateTimeOffset? resetsAt) { return new UsageWindow(UsageSpan.Week, null, percent, resetsAt); }
        public static UsageWindow Scoped(string rawModelName, double? percent, DateTimeOffset? resetsAt)
        { return new UsageWindow(UsageSpan.Week, CleanModelName(rawModelName) ?? UnnamedModel, percent, resetsAt); }
        public string Label { get { return Span == UsageSpan.FiveHour ? "5時間" : Scope == null ? "週" : "週（" + Scope + "）"; } }
        public string ShortLabel { get { return Span == UsageSpan.FiveHour ? "5h" : Scope ?? "週"; } }
        // Model names come from the service: keep a small safe character set, at most 24 characters.
        public static string CleanModelName(string raw)
        {
            if (raw == null) return null;
            var text = new StringBuilder(); bool space = false;
            foreach (char c in raw)
            {
                if (char.IsWhiteSpace(c)) { space = text.Length > 0; continue; }
                bool allowed = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '+' || c == '-' || c == '(' || c == ')';
                if (!allowed) continue;
                if (space) { text.Append(' '); space = false; }
                text.Append(c);
            }
            string value = text.Length > 24 ? text.ToString(0, 24).TrimEnd() : text.ToString();
            return value.Length == 0 ? null : value;
        }
    }
    public sealed class ServiceUsage
    {
        public string Name;
        public string Plan = "プラン未取得";
        public string Source = "";
        public string PlanNote = "";
        public string Status = "";
        public bool NeedsLogin;
        public DateTimeOffset? RecordedAt;
        public readonly List<UsageWindow> Windows = new List<UsageWindow>();
        public bool HasData { get { return Windows.Any(w => w.Percent.HasValue); } }
        public static ServiceUsage Empty(string name, string status)
        {
            // Every service has a weekly limit; other windows appear only when the service returns them.
            var value = new ServiceUsage { Name = name, Status = status };
            value.Windows.Add(UsageWindow.Week(null, null));
            return value;
        }
    }
    public static class UsageText
    {
        // Tray icon tooltip (Windows cuts it at 63 characters). Each value carries its window name,
        // because the set of windows depends on the plan (e.g. Ultra: 週 and Fable).
        public static string Summary(IEnumerable<ServiceUsage> values)
        {
            string text = string.Join(" | ", values.Select(v => v.Name + " " + string.Join("/", v.Windows.Select(w => w.ShortLabel + Percent(w.Percent)))));
            return text.Length > 63 ? text.Substring(0, 63) : text;
        }
        public static string Percent(double? value)
        { return value.HasValue ? value.Value.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "未取得"; }
        // Short form for narrow places (widget medium card, popup with four windows), e.g. "2d3h".
        public static string CompactReset(UsageWindow window, DateTimeOffset now)
        {
            return Reset(window, now).Replace("リセットまで ", "").Replace("期限経過・再取得待ち", "期限経過").Replace("リセット時刻不明", "時刻不明")
                .Replace("時間", "h").Replace("日", "d").Replace("分", "m").Replace(" ", "");
        }
        public static string Reset(UsageWindow window, DateTimeOffset now)
        {
            if (!window.ResetsAt.HasValue) return "リセット時刻不明";
            var remaining = window.ResetsAt.Value - now;
            if (remaining <= TimeSpan.Zero) return "期限経過・再取得待ち";
            if (remaining.TotalDays >= 1) return string.Format("リセットまで {0}日 {1}時間", (int)remaining.TotalDays, remaining.Hours);
            if (remaining.TotalHours >= 1) return string.Format("リセットまで {0}時間 {1}分", (int)remaining.TotalHours, remaining.Minutes);
            return string.Format("リセットまで {0}分", Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes)));
        }
    }
}
