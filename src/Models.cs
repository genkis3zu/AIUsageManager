using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AIUsageManager
{
    public sealed class UsageWindow
    {
        public string Label;
        public double? Percent;
        public DateTimeOffset? ResetsAt;
        public UsageWindow(string label, double? percent, DateTimeOffset? resetsAt)
        { Label = label; Percent = percent; ResetsAt = resetsAt; }
    }
    public sealed class ServiceUsage
    {
        public string Name;
        public string Plan = "プラン未取得";
        public string Source = "";
        public string PlanNote = "";
        public string Status = "";
        public DateTimeOffset? RecordedAt;
        public readonly List<UsageWindow> Windows = new List<UsageWindow>();
        public bool HasData { get { return Windows.Any(w => w.Percent.HasValue); } }
        public static ServiceUsage Empty(string name, string status)
        {
            var value = new ServiceUsage { Name = name, Status = status };
            if (name == "Claude") value.Windows.Add(new UsageWindow("5時間", null, null));
            value.Windows.Add(new UsageWindow("週", null, null));
            return value;
        }
    }
    public static class UsageText
    {
        public static string Percent(double? value)
        { return value.HasValue ? value.Value.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "未取得"; }
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
