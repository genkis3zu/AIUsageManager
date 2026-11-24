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
