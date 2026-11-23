using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;

namespace AIUsageManager
{
    // One running Claude Code session, from the file Claude Code keeps per session in ~/.claude/sessions.
    // The file is not officially documented; only status, times and names are used, never conversation content.
    public sealed class ClaudeSession
    {
        public string SessionId, Status, WaitingFor, Cwd, Name, Entrypoint;
        public int Pid;
        public DateTimeOffset? StatusUpdatedAt;
        // Names of the processes above the session's process, nearest first (not from the file); null when not looked up.
        public string[] Parents;
    }
    public static class ClaudeSessions
    {
        const int MaxBytes = 65536, MaxFiles = 300;
        public static ClaudeSession Parse(string json)
        {
            var root = Json.Parse(json);
            if (root == null) return null;
            var pid = Json.Number(Json.Get(root, "pid"));
            string id = Json.Str(root, "sessionId"), status = Json.Str(root, "status");
            if (!pid.HasValue || pid.Value <= 0 || pid.Value > int.MaxValue || pid.Value != Math.Floor(pid.Value) || string.IsNullOrEmpty(id) || string.IsNullOrEmpty(status)) return null;
            var updated = Json.Number(Json.Get(root, "statusUpdatedAt"));
            DateTimeOffset? at = null;
            if (updated.HasValue && updated.Value > 0 && updated.Value < 253402300799999) at = DateTimeOffset.FromUnixTimeMilliseconds((long)updated.Value);
            return new ClaudeSession { Pid = (int)pid.Value, SessionId = id, Status = status, WaitingFor = Json.Str(root, "waitingFor"), StatusUpdatedAt = at,
                Cwd = Json.Str(root, "cwd"), Name = Json.Str(root, "name"), Entrypoint = Json.Str(root, "entrypoint") };
        }
        sealed class Cached { public DateTime Written; public long Length; public ClaudeSession Session; }
        static readonly object gate = new object();
        static Dictionary<string, Cached> cache = new Dictionary<string, Cached>();
        // Sessions whose process is still running. `readable` is false when a Claude folder exists but its sessions cannot be read;
        // with no Claude folder at all, Claude Code is not used here and nothing is missing.
        // `parentsOf` (process id, start time) gives the names of the processes above a session's process; without it Parents stays null.
        public static ClaudeSession[] Read(string profile, Func<IDictionary<int, DateTime>> claudeProcesses, out bool readable, Func<int, DateTime, string[]> parentsOf = null)
        {
            readable = false;
            var found = new List<ClaudeSession>();
            try
            {
                string home = Path.Combine(profile, ".claude");
                var directory = new DirectoryInfo(Path.Combine(home, "sessions"));
                if (!directory.Exists) { readable = !Directory.Exists(home); return new ClaudeSession[0]; }
                var files = directory.EnumerateFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).Take(MaxFiles).ToArray();
                var processes = (claudeProcesses ?? ClaudeProcesses)();
                readable = true;
                var next = new Dictionary<string, Cached>();
                foreach (var file in files)
                {
                    // Directory listings can hold old times and sizes; ask the file itself.
                    try { file.Refresh(); if (!file.Exists || file.Length > MaxBytes) continue; }
                    catch (IOException) { continue; }
                    Cached hit;
                    lock (gate) cache.TryGetValue(file.FullName, out hit);
                    if (hit == null || hit.Written != file.LastWriteTimeUtc || hit.Length != file.Length)
                    {
                        try { hit = new Cached { Written = file.LastWriteTimeUtc, Length = file.Length, Session = Parse(ReadSmall(file.FullName)) }; }
                        catch (IOException) { continue; }
                        catch (UnauthorizedAccessException) { continue; }
                    }
                    next[file.FullName] = hit;
                    DateTime started;
                    // Windows reuses process ids: the process must have started before the session file was last written.
                    if (hit.Session != null && processes.TryGetValue(hit.Session.Pid, out started) && started <= hit.Written.AddSeconds(5))
                        found.Add(WithParents(hit.Session, parentsOf == null ? null : parentsOf(hit.Session.Pid, started)));
                }
                lock (gate) cache = next;
            }
            catch (IOException) { readable = false; }
            catch (UnauthorizedAccessException) { readable = false; }
            catch (SecurityException) { readable = false; }
            return found.ToArray();
        }
        // A copy, so the cached session from the file is never changed.
        static ClaudeSession WithParents(ClaudeSession s, string[] parents)
        {
            return new ClaudeSession { SessionId = s.SessionId, Status = s.Status, WaitingFor = s.WaitingFor, Cwd = s.Cwd, Name = s.Name, Entrypoint = s.Entrypoint,
                Pid = s.Pid, StatusUpdatedAt = s.StatusUpdatedAt, Parents = parents };
        }
        static string ReadSmall(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length > MaxBytes) return "";
                var bytes = new byte[stream.Length]; int read = 0, count;
                while (read < bytes.Length && (count = stream.Read(bytes, read, bytes.Length - read)) > 0) read += count;
                return new UTF8Encoding(false).GetString(bytes, 0, read).TrimStart('﻿');
            }
        }
        // Running Claude Code processes and their start times, listed once per read.
        public static IDictionary<int, DateTime> ClaudeProcesses()
        {
            var result = new Dictionary<int, DateTime>();
            foreach (var process in Process.GetProcessesByName("claude"))
                using (process)
                {
                    try { result[process.Id] = process.StartTime.ToUniversalTime(); }
                    catch (System.ComponentModel.Win32Exception) { }
                    catch (InvalidOperationException) { }
                }
            return result;
        }
    }
    // A process and the process that started it, from one listing of all processes.
    public sealed class ProcessEntry { public int Parent; public string Name; }
    public static class ProcessTree
    {
        const int MaxParents = 8, MaxKnown = 200;
        static readonly object gate = new object();
        static readonly Dictionary<int, KeyValuePair<DateTime, string[]>> known = new Dictionary<int, KeyValuePair<DateTime, string[]>>();
        // Names of the processes above `pid`, nearest first, at most 8. Windows reuses process ids, so the walk stops at a "parent"
        // that started after the process below it: the real parent has ended. An unknown start time (no access) does not stop it.
        public static string[] Parents(int pid, DateTime started, IDictionary<int, ProcessEntry> table, Func<int, DateTime?> startedOf)
        {
            var names = new List<string>(); var seen = new HashSet<int> { pid };
            DateTime? below = started;
            ProcessEntry entry, parent;
            while (names.Count < MaxParents && table.TryGetValue(pid, out entry) && seen.Add(entry.Parent) && table.TryGetValue(entry.Parent, out parent))
            {
                var at = startedOf(entry.Parent);
                if (at.HasValue && below.HasValue && at.Value > below.Value) break;
                if (at.HasValue) below = at;
                names.Add(parent.Name ?? "");
                pid = entry.Parent;
            }
            return names.ToArray();
        }
        // For the waiting list: worked out once per Claude Code process (id and start time) and kept while it runs.
        public static string[] Of(int pid, DateTime started)
        {
            KeyValuePair<DateTime, string[]> hit;
            lock (gate) if (known.TryGetValue(pid, out hit) && hit.Key == started) return hit.Value;
            var names = Parents(pid, started, List(), StartedAt);
            lock (gate)
            {
                if (known.Count >= MaxKnown) known.Clear();
                known[pid] = new KeyValuePair<DateTime, string[]>(started, names);
            }
            return names;
        }
        // Every running process with its parent and file name, from one Windows process listing.
        public static IDictionary<int, ProcessEntry> List()
        {
            var result = new Dictionary<int, ProcessEntry>();
            IntPtr snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
            if (snapshot == InvalidHandle) return result;
            try
            {
                var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf(typeof(ProcessEntry32)) };
                for (bool more = Process32First(snapshot, ref entry); more; more = Process32Next(snapshot, ref entry))
                    result[(int)entry.ProcessId] = new ProcessEntry { Parent = (int)entry.ParentProcessId, Name = entry.ExeFile ?? "" };
            }
            finally { CloseHandle(snapshot); }
            return result;
        }
        static DateTime? StartedAt(int pid)
        {
            try { using (var process = Process.GetProcessById(pid)) return process.StartTime.ToUniversalTime(); }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (System.ComponentModel.Win32Exception) { return null; }
        }
        const uint SnapProcess = 2;
        static readonly IntPtr InvalidHandle = new IntPtr(-1);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct ProcessEntry32
        {
            public uint Size, Usage, ProcessId;
            public IntPtr DefaultHeapId;
            public uint ModuleId, Threads, ParentProcessId;
            public int PriorityBase;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
        }
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
        [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode)] static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);
        [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode)] static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    }
    public enum CodexState { Unknown, Working, Question, Finished }
    // One Codex thread (desktop app or VS Code), from its session record in ~/.codex/sessions.
    // Only event types, call ids, times and the working folder are used, never conversation content.
    public sealed class CodexThread
    {
        public string Id, Cwd, Originator, Source;
        public CodexState State;
        public DateTimeOffset? Since;
    }
    public static class CodexSessions
    {
        const int MaxLines = 2000, MaxFirstLine = 1048576;
        sealed class Cached { public DateTime Written, Changed; public long Length; public CodexThread Thread; }
        static readonly object gate = new object();
        static Dictionary<string, Cached> cache = new Dictionary<string, Cached>();
        // The last thing that happened decides: a question not yet answered, a finished task, or a started task.
        public static CodexThread Classify(string firstLine, IEnumerable<string> linesNewestFirst)
        {
            var thread = new CodexThread { State = CodexState.Unknown };
            var meta = Json.Parse(firstLine ?? "");
            if (meta != null && Json.Str(meta, "type") == "session_meta")
            {
                var payload = Json.Obj(meta, "payload");
                thread.Id = Json.Str(payload, "id"); thread.Cwd = Json.Str(payload, "cwd");
                thread.Originator = Json.Str(payload, "originator"); thread.Source = Json.Str(payload, "source");
            }
            var answered = new HashSet<string>(); int seen = 0;
            foreach (string line in linesNewestFirst ?? new string[0])
            {
                if (++seen > MaxLines) break;
                var row = Json.Parse(line);
                var payload = Json.Obj(row, "payload");
                if (payload == null) continue;
                string type = Json.Str(row, "type"), kind = Json.Str(payload, "type");
                var at = Json.Date(Json.Get(row, "timestamp"));
                if (type == "response_item" && kind == "function_call_output") { string id = Json.Str(payload, "call_id"); if (id != null) answered.Add(id); continue; }
                if (type == "response_item" && kind == "function_call" && Json.Str(payload, "name") == "request_user_input_async" && !answered.Contains(Json.Str(payload, "call_id") ?? ""))
                { thread.State = CodexState.Question; thread.Since = at; return thread; }
                if (type == "event_msg" && kind == "task_complete") { thread.State = CodexState.Finished; thread.Since = at; return thread; }
                if (type == "event_msg" && kind == "task_started") { thread.State = CodexState.Working; thread.Since = at; return thread; }
                // The user stopped the turn: an open question is no longer waiting, and nothing finished.
                if (type == "event_msg" && kind == "turn_aborted") { thread.Since = at; return thread; }
            }
            return thread;
        }
        // Threads written in the last 12 hours, while a Codex app (ChatGPT/Codex or VS Code) runs.
        // With no Codex folder at all, Codex is not used here; a Codex folder without sessions cannot be read.
        public static CodexThread[] Read(string profile, DateTimeOffset now, Func<bool> appRunning, out bool readable)
        {
            readable = false;
            try
            {
                string home = Path.Combine(profile, ".codex");
                var directory = new DirectoryInfo(Path.Combine(home, "sessions"));
                if (!directory.Exists) { readable = !Directory.Exists(home); return new CodexThread[0]; }
                readable = true;
                if (!(appRunning ?? AppRunning)()) return new CodexThread[0];
                var oldest = now.UtcDateTime.AddHours(-12);
                var result = new List<CodexThread>();
                var next = new Dictionary<string, Cached>();
                foreach (var file in directory.EnumerateFiles("*.jsonl", SearchOption.AllDirectories))
                {
                    // Codex keeps a thread's file open while it appends, so the directory listing can show an old time and size.
                    try { file.Refresh(); if (!file.Exists) continue; }
                    catch (IOException) { continue; }
                    Cached hit;
                    lock (gate) cache.TryGetValue(file.FullName, out hit);
                    bool changed = hit == null || hit.Written != file.LastWriteTimeUtc || hit.Length != file.Length;
                    var entry = changed ? new Cached { Written = file.LastWriteTimeUtc, Length = file.Length, Changed = hit == null ? file.LastWriteTimeUtc : now.UtcDateTime } : hit;
                    next[file.FullName] = entry;
                    if (entry.Changed < oldest) continue;
                    if (entry.Thread == null)
                    {
                        try { entry.Thread = Classify(FirstLine(file.FullName), ReadOnlyFiles.ReverseLines(file.FullName)); }
                        catch (IOException) { continue; }
                        catch (UnauthorizedAccessException) { continue; }
                    }
                    result.Add(entry.Thread);
                }
                lock (gate) cache = next;
                return result.ToArray();
            }
            catch (IOException) { readable = false; }
            catch (UnauthorizedAccessException) { readable = false; }
            catch (SecurityException) { readable = false; }
            return new CodexThread[0];
        }
        static string FirstLine(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                var line = new StringBuilder(); int c;
                while ((c = reader.Read()) >= 0 && c != '\n') { if (line.Length >= MaxFirstLine) return ""; line.Append((char)c); }
                return line.ToString().TrimEnd('\r');
            }
        }
        public static bool AppRunning()
        {
            foreach (string name in new[] { "ChatGPT", "Codex", "Code" })
            {
                var processes = Process.GetProcessesByName(name);
                bool any = processes.Length > 0;
                foreach (var process in processes) process.Dispose();
                if (any) return true;
            }
            return false;
        }
        public static string App(CodexThread thread) { return thread != null && thread.Source == "vscode" ? "vscode" : "codex"; }
    }
    public enum WaitReason { Question, Confirm, Finished }
    public sealed class WaitingItem
    {
        public string Key, Service, Place, App;
        // The session's working folder, for leaving out work in some folders from the 日中ログ; not shared with other apps.
        public string Cwd;
        public WaitReason Reason;
        public DateTimeOffset Since;
    }
    public sealed class WaitingUnreadable { public string Service, Reason; }
    public sealed class WaitingList
    {
        public WaitingItem[] Items = new WaitingItem[0];
        public WaitingUnreadable[] Unreadable = new WaitingUnreadable[0];
    }
    // Builds the list: waits (question, confirm) oldest first, then finished items newest first for 30 minutes.
    // A finished item needs the tracker to have seen the same session working, so nothing finished before start-up is shown.
    public sealed class WaitingTracker
    {
        static readonly TimeSpan FinishedFor = TimeSpan.FromMinutes(30);
        readonly Dictionary<string, string> lastState = new Dictionary<string, string>();
        readonly Dictionary<string, DateTimeOffset> finished = new Dictionary<string, DateTimeOffset>();
        readonly Dictionary<string, DateTimeOffset> firstSeen = new Dictionary<string, DateTimeOffset>();
        public WaitingList Observe(ClaudeSession[] claude, bool claudeReadable, CodexThread[] codex, bool codexReadable, DateTimeOffset now)
        {
            var items = new List<WaitingItem>(); var present = new HashSet<string>();
            foreach (var session in claude ?? new ClaudeSession[0])
            {
                if (session == null) continue;
                string key = "Claude/" + session.SessionId, state = session.Status == "waiting" ? "waiting" : session.Status == "busy" ? "busy" : session.Status == "idle" ? "idle" : "other";
                present.Add(key);
                var reason = state == "waiting" ? (session.WaitingFor == "input needed" ? WaitReason.Question : WaitReason.Confirm) : WaitReason.Finished;
                Add(items, key, state, "busy", "Claude", WaitingNames.Place(session.Name, session.Cwd), session.Cwd, WaitingNames.ClaudeApp(session.Entrypoint, session.Parents), reason, session.StatusUpdatedAt, now);
            }
            foreach (var thread in codex ?? new CodexThread[0])
            {
                if (thread == null) continue;
                string key = "Codex/" + (thread.Id ?? thread.Cwd ?? ""), state = thread.State == CodexState.Question ? "waiting" : thread.State == CodexState.Finished ? "idle" : thread.State == CodexState.Working ? "busy" : "other";
                present.Add(key);
                Add(items, key, state, "busy", "Codex", WaitingNames.Place(null, thread.Cwd), thread.Cwd, CodexSessions.App(thread), WaitReason.Question, thread.Since, now);
            }
            foreach (var gone in lastState.Keys.Where(k => !present.Contains(k)).ToArray()) { lastState.Remove(gone); finished.Remove(gone); firstSeen.Remove(gone); }
            var unreadable = new List<WaitingUnreadable>();
            if (!claudeReadable) unreadable.Add(new WaitingUnreadable { Service = "Claude", Reason = "記録を読めません" });
            if (!codexReadable) unreadable.Add(new WaitingUnreadable { Service = "Codex", Reason = "記録を読めません" });
            var waits = items.Where(i => i.Reason != WaitReason.Finished).OrderBy(i => i.Since);
            var done = items.Where(i => i.Reason == WaitReason.Finished).OrderByDescending(i => i.Since);
            return new WaitingList { Items = waits.Concat(done).ToArray(), Unreadable = unreadable.ToArray() };
        }
        void Add(List<WaitingItem> items, string key, string state, string working, string service, string place, string cwd, string app, WaitReason waitReason, DateTimeOffset? at, DateTimeOffset now)
        {
            string previous; lastState.TryGetValue(key, out previous); lastState[key] = state;
            if (state != "waiting") firstSeen.Remove(key);
            if (state == "waiting")
            {
                finished.Remove(key);
                DateTimeOffset seen;
                if (!firstSeen.TryGetValue(key, out seen)) { seen = now; firstSeen[key] = now; }
                items.Add(new WaitingItem { Key = key, Service = service, Place = place, Cwd = cwd, App = app, Reason = waitReason, Since = Clamp(at ?? seen, now) });
                return;
            }
            if (state != "idle") { finished.Remove(key); return; }
            DateTimeOffset since;
            if (!finished.TryGetValue(key, out since))
            {
                if (previous != working && previous != "waiting") return;
                since = Clamp(at ?? now, now); finished[key] = since;
            }
            if (now - since <= FinishedFor) items.Add(new WaitingItem { Key = key, Service = service, Place = place, Cwd = cwd, App = app, Reason = WaitReason.Finished, Since = since });
        }
        static DateTimeOffset Clamp(DateTimeOffset at, DateTimeOffset now) { return at > now ? now : at; }
    }
    public enum WaitingNoticeMode { Standard, Frequent, Off }
    // Windows notifications for waits that go on: first after 3 (or 1) minutes, then every 10 (or 5), never after 60 minutes.
    public sealed class WaitingReminders
    {
        static readonly TimeSpan Limit = TimeSpan.FromMinutes(60);
        readonly Dictionary<string, KeyValuePair<DateTimeOffset, DateTimeOffset>> sent = new Dictionary<string, KeyValuePair<DateTimeOffset, DateTimeOffset>>();
        public string[] Due(WaitingItem[] items, DateTimeOffset now, WaitingNoticeMode mode)
        {
            var due = new List<string>(); var current = new HashSet<string>();
            TimeSpan first = TimeSpan.FromMinutes(mode == WaitingNoticeMode.Frequent ? 1 : 3), repeat = TimeSpan.FromMinutes(mode == WaitingNoticeMode.Frequent ? 5 : 10);
            foreach (var item in items ?? new WaitingItem[0])
            {
                if (item == null || item.Reason == WaitReason.Finished) continue;
                current.Add(item.Key);
                var waited = now - item.Since;
                KeyValuePair<DateTimeOffset, DateTimeOffset> last;
                bool known = sent.TryGetValue(item.Key, out last) && last.Key == item.Since;
                if (mode == WaitingNoticeMode.Off || waited > Limit) continue;
                if (known ? now - last.Value < repeat : waited < first) continue;
                sent[item.Key] = new KeyValuePair<DateTimeOffset, DateTimeOffset>(item.Since, now);
                due.Add(Text(item, (int)Math.Floor(waited.TotalMinutes)));
            }
            foreach (var gone in sent.Keys.Where(k => !current.Contains(k)).ToArray()) sent.Remove(gone);
            return due.ToArray();
        }
        static string Text(WaitingItem item, int minutes)
        {
            string who = string.IsNullOrEmpty(item.Place) ? item.Service : item.Service + "（" + item.Place + "）";
            return who + "が" + (item.Reason == WaitReason.Question ? "質問の答え" : "確認") + "を" + minutes + "分待っています";
        }
    }
    public sealed class WaitingSnapshotInput
    {
        public ClaudeSession[] Claude = new ClaudeSession[0];
        public bool ClaudeReadable;
        public CodexThread[] Codex = new CodexThread[0];
        public bool CodexReadable;
    }
    public interface IWaitingSource { WaitingSnapshotInput Read(DateTimeOffset now); }
    // Reads this user's Claude Code and Codex records; never writes to them.
    public sealed class LocalWaitingSource : IWaitingSource
    {
        readonly string profile;
        public LocalWaitingSource(string profile) { this.profile = profile; }
        public WaitingSnapshotInput Read(DateTimeOffset now)
        {
            bool claudeReadable, codexReadable;
            var claude = ClaudeSessions.Read(profile, null, out claudeReadable, ProcessTree.Of);
            var codex = CodexSessions.Read(profile, now, null, out codexReadable);
            return new WaitingSnapshotInput { Claude = claude, ClaudeReadable = claudeReadable, Codex = codex, CodexReadable = codexReadable };
        }
    }
    // Only these four fixed links can be opened from the waiting list; the opened app brings itself to the front.
    public static class AppLinks
    {
        public static string For(string app)
        {
            switch (app)
            {
                case "claude": return "claude://";
                case "codex": return "codex://";
                case "vscode": return "vscode://";
                case "grokbot": return "grokbot://";
                default: return null;
            }
        }
        public static bool Open(string app)
        {
            string link = For(app);
            if (link == null) return false;
            try { using (Process.Start(new ProcessStartInfo(link) { UseShellExecute = true })) { } return true; }
            catch (System.ComponentModel.Win32Exception) { return false; }
            catch (InvalidOperationException) { return false; }
        }
    }
    public static class WaitingNames
    {
        // The session name first; otherwise the project folder (a Claude worktree …\<project>\.claude\worktrees\<x> shows <project>).
        public static string Place(string name, string cwd)
        {
            string cleaned = Clean(name);
            if (cleaned.Length > 0 || string.IsNullOrEmpty(cwd)) return cleaned;
            var parts = cwd.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "";
            for (int i = parts.Length - 2; i >= 1; i--)
                if (parts[i] == ".claude" && parts[i + 1] == "worktrees") return Clean(parts[i - 1]);
            return Clean(parts[parts.Length - 1]);
        }
        // Names come from files other programs write: no control or format characters, single spaces, at most 40 characters.
        public static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var result = new StringBuilder(); bool space = false;
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c)) { space = result.Length > 0; continue; }
                if (char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format) continue;
                if (space) { result.Append(' '); space = false; }
                result.Append(c);
            }
            if (result.Length <= 40) return result.ToString();
            int cut = char.IsHighSurrogate(result[39]) ? 39 : 40;
            return result.ToString(0, cut).TrimEnd();
        }
        // The app a Claude Code session runs in: from how it was started, or else a VS Code window above it (Claude Code in VS Code's terminal).
        // A session another program started (a program using the Claude Agent SDK, a terminal outside VS Code) has no app to open: null.
        public static string ClaudeApp(string entrypoint, IEnumerable<string> parents)
        {
            if (entrypoint != null && entrypoint.IndexOf("vscode", StringComparison.OrdinalIgnoreCase) >= 0) return "vscode";
            if (entrypoint != null && entrypoint.IndexOf("desktop", StringComparison.OrdinalIgnoreCase) >= 0) return "claude";
            if (parents != null && parents.Any(n => string.Equals(n, "Code.exe", StringComparison.OrdinalIgnoreCase))) return "vscode";
            return null;
        }
    }
}
