using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;

namespace AIUsageManager
{
    // One line for the 日中ログ about finished AI work: the minute the first of it finished, and the text after 🤖.
    internal sealed class AiDoneEntry
    {
        public DateTime Time;
        public string Text;
    }

    internal enum AiDoneWait { Write, Wait, GiveUp }

    // now_doing_ai_done=on: Claude Code and Codex work that newly finished (from the waiting list) becomes one 🤖 line in
    // the 日中ログ. Finishes within two minutes of the first become one line; the same session or thread is written at
    // most once in 30 minutes; work in the excluded folders (or below them) is never written.
    internal sealed class AiDoneLog
    {
        public const string Mark = "🤖";
        static readonly TimeSpan Gather = TimeSpan.FromMinutes(2), Again = TimeSpan.FromMinutes(30), Forget = TimeSpan.FromHours(2);
        static readonly TimeSpan Quiet = TimeSpan.FromSeconds(10), Patience = TimeSpan.FromMinutes(5);
        readonly string[] exclude;
        // Finishes already looked at (session and finish time), and when each session was last written.
        readonly Dictionary<string, DateTimeOffset> seen = new Dictionary<string, DateTimeOffset>();
        readonly Dictionary<string, DateTimeOffset> written = new Dictionary<string, DateTimeOffset>();
        readonly List<WaitingItem> batch = new List<WaitingItem>();
        DateTimeOffset batchStart;

        public AiDoneLog(string[] exclude) { this.exclude = exclude ?? new string[0]; }

        public void Observe(WaitingItem[] items, DateTimeOffset now)
        {
            foreach (WaitingItem item in items ?? new WaitingItem[0])
            {
                if (item == null || item.Reason != WaitReason.Finished || item.Helper) continue;
                string finish = item.Key + "|" + item.Since.UtcTicks;
                if (seen.ContainsKey(finish)) continue;
                seen[finish] = now;
                if (Excluded(item.Cwd, exclude)) continue;
                DateTimeOffset last;
                if (written.TryGetValue(item.Key, out last) && item.Since - last < Again) continue;
                written[item.Key] = item.Since;
                if (batch.Count == 0) batchStart = now;
                batch.Add(item);
            }
            // The waiting list shows a finish for 30 minutes, so older entries are not needed any more.
            foreach (string old in seen.Where(s => now - s.Value > Forget).Select(s => s.Key).ToArray()) seen.Remove(old);
            foreach (string old in written.Where(w => now - w.Value > Forget).Select(w => w.Key).ToArray()) written.Remove(old);
        }

        // The line to write once two minutes have passed since the first finish was seen; null before that or when there is none.
        public AiDoneEntry Due(DateTimeOffset now)
        {
            if (batch.Count == 0 || now - batchStart < Gather) return null;
            List<WaitingItem> done = batch.OrderBy(i => i.Since).ToList();
            batch.Clear();
            DateTime first = done[0].Since.LocalDateTime;
            // Sessions or threads of the same app with the same name (several in one folder) are named once, with the
            // count, in the order their first one finished.
            var named = done.GroupBy(i => i.Service + "\n" + WaitingNames.Clean(i.Place))
                .Select(g => new KeyValuePair<WaitingItem, int>(g.First(), g.Count())).ToList();
            string text = named.Count == 1 ? One(named[0].Key) + Count(named[0].Value)
                : "完了: " + string.Join("・", named.Select(n => Named(n.Key) + Count(n.Value)));
            return new AiDoneEntry { Time = new DateTime(first.Year, first.Month, first.Day, first.Hour, first.Minute, 0), Text = text };
        }

        static string Count(int count) { return count > 1 ? "（" + count + "件）" : ""; }

        static string One(WaitingItem item)
        {
            string place = WaitingNames.Clean(item.Place);
            return place.Length == 0 ? item.Service + " 完了" : item.Service + " 完了: " + place;
        }
        static string Named(WaitingItem item)
        {
            string place = WaitingNames.Clean(item.Place);
            return place.Length == 0 ? item.Service : item.Service + "「" + place + "」";
        }

        // True when the working folder is one of the excluded folders or below one. Folders are compared as full paths,
        // without regard to case, a trailing \ or a \\?\ in front.
        public static bool Excluded(string cwd, string[] exclude)
        {
            string place = Normal(cwd);
            if (place == null) return false;
            foreach (string folder in exclude ?? new string[0])
            {
                string root = Normal(folder);
                if (root == null) continue;
                if (string.Equals(place, root, StringComparison.OrdinalIgnoreCase) || place.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        static string Normal(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string p = path.Trim().Replace('/', '\\');
            if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) p = @"\\" + p.Substring(8);
            else if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) p = p.Substring(4);
            try { p = Path.GetFullPath(p); }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (PathTooLongException) { return null; }
            catch (SecurityException) { return null; }
            return p.TrimEnd('\\');
        }

        // Someone may be typing in the diary (in Obsidian): it is written only when it has not changed for 10 seconds,
        // checked again every 10 seconds, and given up 5 minutes after the line was due.
        public static AiDoneWait Decide(DateTime lastWriteUtc, DateTime nowUtc, DateTime dueUtc)
        {
            if (nowUtc - dueUtc > Patience) return AiDoneWait.GiveUp;
            if (nowUtc - lastWriteUtc < Quiet) return AiDoneWait.Wait;
            return AiDoneWait.Write;
        }
    }
}
