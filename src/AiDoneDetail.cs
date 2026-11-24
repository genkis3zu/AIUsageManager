using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace AIUsageManager
{
    // Text copied from a conversation or a commit into the diary must not turn into a tag, link, embed,
    // heading or task there.
    internal static class AiDoneText
    {
        public static string Clean(string text, int max = 100)
        {
            if (text == null) return "";
            var plain = new StringBuilder(text.Length);
            foreach (char c in text) plain.Append(char.IsControl(c) ? ' ' : c);
            string s = plain.ToString();
            s = Regex.Replace(s, @"!\[\[[^\]]*\]\]", "");
            s = Regex.Replace(s, @"\[\[[^\]|]*\|([^\]]*)\]\]", "$1");
            s = Regex.Replace(s, @"\[\[([^\]]*)\]\]", "$1");
            s = s.Replace("**", "").Replace("`", "");
            s = Regex.Replace(s, @"\s+", " ").Trim();
            s = Regex.Replace(s, @"^#{1,6} ", "");
            s = Regex.Replace(s, @"^\[.\] ", "");
            s = s.Replace("#", "＃");
            if (s.Length > max)
            {
                int cut = max;
                if (char.IsHighSurrogate(s[cut - 1])) cut--;
                s = s.Substring(0, cut) + "…";
            }
            return s;
        }
        // The first lines of a reply, without blank lines, headings, code, tables, rules and list or quote marks.
        public static List<string> ReplyLines(string reply, int max = 3)
        {
            var result = new List<string>(); bool code = false;
            foreach (string raw in (reply ?? "").Replace("\r", "").Split('\n'))
            {
                if (result.Count >= max) break;
                string line = raw.Trim();
                if (line.StartsWith("```") || line.StartsWith("~~~")) { code = !code; continue; }
                if (code || line.Length == 0 || line.StartsWith("#") || line.StartsWith("|") || Regex.IsMatch(line, @"^([-*_])(\s*\1){2,}$")) continue;
                line = Regex.Replace(line, @"^((?:[-*+>]|\d+[.)])(\s+|$))+", "");
                string clean = Clean(line);
                if (clean.Length > 0) result.Add(clean);
            }
            return result;
        }
        public static string FirstLine(string text)
        {
            return (text ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        }
    }

    // The commits made in the working folder's repository during a piece of work: "<short hash> <subject>", newest first,
    // at most six (the diary shows five and "ほか" for the sixth). Nothing when the folder is not in Git, git is not
    // installed, or git fails or takes more than 5 seconds. git runs without a shell and without a window.
    internal static class GitCommits
    {
        static readonly NowDoingScriptLimits Limits = new NowDoingScriptLimits { TimeoutMs = 5000, MaxBytes = 16384, MaxLines = 10 };
        public static List<string> Between(string cwd, DateTimeOffset start, DateTimeOffset end)
        {
            var none = new List<string>();
            if (!InGit(cwd)) return none;
            var arguments = new[] { "-C", cwd, "log", "--since=" + GitDate(start), "--until=" + GitDate(end), "--format=%h %s", "-n", "6" };
            NowDoingScriptResult result;
            try { result = NowDoingScript.RunAsync("git", arguments, cwd, Limits, CancellationToken.None).Result; }
            catch (AggregateException) { return none; }
            if (!result.Ok) return none;
            return result.Output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(6).ToList();
        }
        static string GitDate(DateTimeOffset at) { return at.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture); }
        // The folder or one above it has .git (a folder, or a file in a worktree).
        static bool InGit(string cwd)
        {
            if (string.IsNullOrEmpty(cwd)) return false;
            try
            {
                for (var folder = new DirectoryInfo(cwd); folder != null; folder = folder.Parent)
                {
                    if (!folder.Exists) return false;
                    string git = Path.Combine(folder.FullName, ".git");
                    if (Directory.Exists(git) || File.Exists(git)) return true;
                }
            }
            catch (ArgumentException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (SecurityException) { }
            catch (NotSupportedException) { }
            return false;
        }
    }

    // Opens the record of a finished session or thread on this PC: Claude Code's <profile>\.claude\projects\<folder>\<id>.jsonl,
    // Codex's rollout file from the waiting list. Nothing when there is no record. A record that has not changed since the
    // last read is not read again.
    internal static class AiTurnReader
    {
        sealed class Cached { public long Length; public DateTime Written; public DateTimeOffset After; public List<AiTurn> Turns; }
        static readonly object gate = new object();
        static readonly Dictionary<string, Cached> cache = new Dictionary<string, Cached>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, string> claudePaths = new Dictionary<string, string>();

        public static List<AiTurn> Read(WaitingItem item, DateTimeOffset after, string profile)
        {
            var none = new List<AiTurn>();
            if (item == null || item.Key == null) return none;
            bool codex = item.Service == "Codex";
            string path = codex ? item.RecordPath : item.Key.StartsWith("Claude/") ? ClaudeTranscript(profile, item.Key.Substring("Claude/".Length)) : null;
            if (string.IsNullOrEmpty(path)) return none;
            var file = new FileInfo(path);
            if (!file.Exists) return none;
            lock (gate)
            {
                Cached hit;
                if (cache.TryGetValue(path, out hit) && hit.Length == file.Length && hit.Written == file.LastWriteTimeUtc && hit.After == after) return hit.Turns;
            }
            var lines = ReadOnlyFiles.ReverseLines(path);
            List<AiTurn> turns = codex ? CodexRollout.Turns(lines, after) : global::AIUsageManager.ClaudeTranscript.Turns(lines, after);
            lock (gate) cache[path] = new Cached { Length = file.Length, Written = file.LastWriteTimeUtc, After = after, Turns = turns };
            return turns;
        }

        // Only the folders right under projects are looked in; the session id must be a plain name.
        public static string ClaudeTranscript(string profile, string sessionId)
        {
            if (string.IsNullOrEmpty(profile) || string.IsNullOrEmpty(sessionId) || !Regex.IsMatch(sessionId, "^[A-Za-z0-9_-]+$")) return null;
            string known;
            lock (gate) claudePaths.TryGetValue(sessionId, out known);
            try
            {
                if (known != null && File.Exists(known)) return known;
                string root = Path.Combine(profile, ".claude", "projects");
                if (!Directory.Exists(root)) return null;
                foreach (string folder in Directory.EnumerateDirectories(root))
                {
                    string path = Path.Combine(folder, sessionId + ".jsonl");
                    if (!File.Exists(path)) continue;
                    lock (gate) claudePaths[sessionId] = path;
                    return path;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (SecurityException) { }
            catch (ArgumentException) { }
            return null;
        }
    }

    // Decides when finished AI work goes to the diary (now_doing_ai_done_detail=on). Each finished session or thread on
    // the waiting list is read from its record at most once a minute. Only the user's own turns count. The user's turns
    // with ten minutes or less between them are one piece of work, written ten minutes after the last one ended.
    internal sealed class AiDoneDetailLog
    {
        public static readonly TimeSpan Quiet = TimeSpan.FromMinutes(10);
        static readonly TimeSpan ReadEvery = TimeSpan.FromMinutes(1);
        sealed class Pending { public WaitingItem Item; public DateTimeOffset? LastRead; }
        readonly object gate = new object();
        readonly string[] exclude;
        readonly DateTimeOffset since;
        readonly Func<WaitingItem, DateTimeOffset, List<AiTurn>> readTurns;
        readonly Dictionary<string, Pending> pending = new Dictionary<string, Pending>();
        // Per session: the end of the last turn read when its work was written, and the finish already dealt with.
        readonly Dictionary<string, DateTimeOffset> writtenEnd = new Dictionary<string, DateTimeOffset>();
        readonly Dictionary<string, DateTimeOffset> handled = new Dictionary<string, DateTimeOffset>();

        // `since`: work that ended before this (the app's start) is not written.
        public AiDoneDetailLog(string[] exclude, DateTimeOffset since, Func<WaitingItem, DateTimeOffset, List<AiTurn>> readTurns)
        {
            this.exclude = exclude ?? new string[0]; this.since = since; this.readTurns = readTurns;
        }

        public void Observe(WaitingItem[] items, DateTimeOffset now)
        {
            lock (gate)
                foreach (var item in items ?? new WaitingItem[0])
                {
                    if (item == null || item.Reason != WaitReason.Finished || item.Helper || string.IsNullOrEmpty(item.Key) || AiDoneLog.Excluded(item.Cwd, exclude)) continue;
                    DateTimeOffset done;
                    if (handled.TryGetValue(item.Key, out done) && item.Since <= done) continue;
                    Pending entry;
                    if (!pending.TryGetValue(item.Key, out entry)) pending[item.Key] = entry = new Pending();
                    entry.Item = item;
                }
        }

        public List<AiStretch> Due(DateTimeOffset now)
        {
            var result = new List<AiStretch>();
            lock (gate)
                foreach (string key in pending.Keys.ToList())
                {
                    Pending entry = pending[key];
                    if (entry.LastRead.HasValue && now - entry.LastRead.Value < ReadEvery) continue;
                    entry.LastRead = now;
                    List<AiTurn> turns = Read(entry.Item);
                    if (turns == null) continue;
                    var mine = turns.Where(t => t.ByUser).ToList();
                    if (mine.Any(t => t.End == null)) continue;
                    if (mine.Count > 0 && now - mine.Max(t => t.End.Value) < Quiet) continue;
                    result.AddRange(Finish(key, entry, turns, mine));
                }
            return result;
        }

        // When the app ends: all waiting work at once, from the turns that have ended, without waiting ten minutes.
        public List<AiStretch> All()
        {
            var result = new List<AiStretch>();
            lock (gate)
                foreach (string key in pending.Keys.ToList())
                {
                    Pending entry = pending[key];
                    List<AiTurn> turns = Read(entry.Item);
                    if (turns == null) continue;
                    result.AddRange(Finish(key, entry, turns, turns.Where(t => t.ByUser && t.End != null).ToList()));
                }
            return result;
        }

        // The turns that ended after the last written work, or null when the record cannot be read this time.
        // Claude Code writes no reply after the user stops a turn (Esc), so a turn without an end that started before the
        // waiting list saw the session finish is taken to have ended then.
        List<AiTurn> Read(WaitingItem item)
        {
            DateTimeOffset after;
            if (!writtenEnd.TryGetValue(item.Key, out after)) after = since;
            List<AiTurn> turns;
            try { turns = readTurns(item, after) ?? new List<AiTurn>(); }
            catch (Exception) { return null; }
            return turns.Where(t => t != null).Select(t => t.End == null && t.Start < item.Since
                ? new AiTurn { Start = t.Start, End = item.Since, ByUser = t.ByUser, Requests = t.Requests, Reply = t.Reply, WorkMs = (item.Since - t.Start).TotalMilliseconds }
                : t).ToList();
        }

        List<AiStretch> Finish(string key, Pending entry, List<AiTurn> turns, List<AiTurn> mine)
        {
            DateTimeOffset end;
            if (!writtenEnd.TryGetValue(key, out end)) end = since;
            foreach (var turn in turns) if (turn.End.HasValue && turn.End.Value > end) end = turn.End.Value;
            writtenEnd[key] = end;
            handled[key] = entry.Item.Since;
            pending.Remove(key);
            var stretches = new List<AiStretch>();
            AiStretch current = null; DateTimeOffset last = DateTimeOffset.MinValue;
            foreach (var turn in mine.OrderBy(t => t.Start))
            {
                if (current == null || turn.Start - last > Quiet)
                {
                    WaitingItem item = entry.Item;
                    current = new AiStretch { Key = key, Service = item.Service, Place = item.Place, Cwd = item.Cwd, Project = WaitingNames.Place(null, item.Cwd) };
                    stretches.Add(current);
                }
                current.Turns.Add(turn);
                if (turn.End.Value > last) last = turn.End.Value;
            }
            return stretches;
        }
    }

    // The user's turns in one session or thread that follow each other (the next request within 10 minutes).
    internal sealed class AiStretch
    {
        public string Key, Service, Place, Project, Cwd;
        public List<AiTurn> Turns = new List<AiTurn>();
        public DateTimeOffset Start { get { return Turns.Count == 0 ? DateTimeOffset.MinValue : Turns.Min(t => t.Start); } }
        public DateTimeOffset End { get { return Turns.Count == 0 ? DateTimeOffset.MinValue : Turns.Max(t => t.End ?? t.Start); } }
        public double WorkMs { get { return Turns.Sum(t => t.WorkMs); } }
    }

    // One diary entry in the shape of the diary's log rules: the time range, a bold title, and folded details
    // of at most eight lines (not counting the callout line and the blank line between the two parts).
    internal static class AiDoneFormat
    {
        const int MaxRequests = 3, MaxReplyLines = 3, MaxCommits = 5, MaxDetails = 8;
        public static List<string> Entry(AiStretch stretch, IList<string> commits, bool moreCommits)
        {
            var lines = new List<string>();
            string from = stretch.Start.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);
            string to = stretch.End.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);
            lines.Add(from == to ? "[" + from + "]" : "[" + from + "-" + to + "]");

            bool codex = stretch.Service == "Codex";
            string name = AiDoneText.Clean(stretch.Place), project = AiDoneText.Clean(stretch.Project);
            if (name.Length == 0) name = project;
            string work = stretch.WorkMs < 60000 ? "作業 1分未満" : "作業 " + (long)(stretch.WorkMs / 60000) + "分";
            string about = project.Length > 0 && project != name ? project + "・" + work : work;
            lines.Add("🤖 **" + (codex ? "Codex" : "Claude Code") + (name.Length > 0 ? ": " + name : "") + "**（" + about + "）");

            var asked = stretch.Turns.SelectMany(t => t.Requests).Select(r => AiDoneText.Clean(AiDoneText.FirstLine(r))).Where(r => r.Length > 0).ToList();
            var requests = asked.Take(MaxRequests).ToList();
            if (asked.Count > MaxRequests) requests[MaxRequests - 1] += "（ほか " + (asked.Count - MaxRequests) + "件）";

            string reply = stretch.Turns.Select(t => t.Reply).LastOrDefault(r => !string.IsNullOrWhiteSpace(r));
            var replies = AiDoneText.ReplyLines(reply, MaxReplyLines);

            string made = null;
            if (commits != null && commits.Count > 0)
            {
                var shown = commits.Take(MaxCommits).Select(Commit).ToList();
                string count = moreCommits ? (shown.Count + 1) + "件以上" : shown.Count + "件";
                if (moreCommits) shown.Add("ほか");
                made = "📦 成果物: コミット " + count + "（" + string.Join(" ／ ", shown) + "）";
            }

            bool output = replies.Count > 0 || made != null;
            int room = MaxDetails - (requests.Count > 0 ? 1 + requests.Count : 0) - (output ? 1 : 0) - (made != null ? 1 : 0);
            if (replies.Count > room) replies = replies.Take(Math.Max(1, room)).ToList();

            var details = new List<string>();
            if (requests.Count > 0)
            {
                details.Add("> **🗣️ 頼んだこと:**");
                details.AddRange(requests.Select(r => "> - " + r));
            }
            if (output)
            {
                if (requests.Count > 0) details.Add(">");
                details.Add("> **🤖 " + (codex ? "Codex" : "Claude") + " の処理・出力:**");
                details.AddRange(replies.Select(r => "> - " + r));
                if (made != null) details.Add("> - " + made);
            }
            if (details.Count > 0) { lines.Add("> [!info]- 詳細"); lines.AddRange(details); }
            return lines;
        }
        // "<hash> <subject>" from git log.
        static string Commit(string line)
        {
            string text = (line ?? "").Trim();
            int space = text.IndexOf(' ');
            string hash = space > 0 ? text.Substring(0, space) : text;
            if (!Regex.IsMatch(hash, "^[0-9a-f]{4,40}$")) return AiDoneText.Clean(text);
            return "`" + hash + "` " + AiDoneText.Clean(space > 0 ? text.Substring(space + 1) : "");
        }
    }
}
