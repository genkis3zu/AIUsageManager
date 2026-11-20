using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
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
        // Sessions whose process is still running. `readable` is false when the folder is missing or cannot be listed.
        public static ClaudeSession[] Read(string profile, Func<int, bool> isClaudeProcess, out bool readable)
        {
            readable = false;
            var found = new List<ClaudeSession>();
            var alive = isClaudeProcess ?? IsClaudeProcess;
            try
            {
                var directory = new DirectoryInfo(Path.Combine(profile, ".claude", "sessions"));
                if (!directory.Exists) return new ClaudeSession[0];
                var files = directory.EnumerateFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).Take(MaxFiles).ToArray();
                readable = true;
                foreach (var file in files)
                {
                    if (file.Length > MaxBytes) continue;
                    ClaudeSession session;
                    try { session = Parse(ReadOnlyFiles.Small(file.FullName)); }
                    catch (IOException) { continue; }
                    catch (UnauthorizedAccessException) { continue; }
                    if (session != null && alive(session.Pid)) found.Add(session);
                }
            }
            catch (IOException) { readable = false; }
            catch (UnauthorizedAccessException) { readable = false; }
            catch (SecurityException) { readable = false; }
            return found.ToArray();
        }
        public static bool IsClaudeProcess(int pid)
        {
            try { using (var process = Process.GetProcessById(pid)) return string.Equals(process.ProcessName, "claude", StringComparison.OrdinalIgnoreCase); }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }
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
        static readonly object gate = new object();
        static Dictionary<string, KeyValuePair<DateTime, CodexThread>> cache = new Dictionary<string, KeyValuePair<DateTime, CodexThread>>();
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
            }
            return thread;
        }
        // Threads written in the last 12 hours, while a Codex app (ChatGPT/Codex or VS Code) runs.
        public static CodexThread[] Read(string profile, DateTimeOffset now, Func<bool> appRunning, out bool readable)
        {
            readable = false;
            try
            {
                var directory = new DirectoryInfo(Path.Combine(profile, ".codex", "sessions"));
                if (!directory.Exists) return new CodexThread[0];
                readable = true;
                if (!(appRunning ?? AppRunning)()) return new CodexThread[0];
                var oldest = now.UtcDateTime.AddHours(-12);
                var result = new List<CodexThread>();
                var next = new Dictionary<string, KeyValuePair<DateTime, CodexThread>>();
                foreach (var file in directory.EnumerateFiles("*.jsonl", SearchOption.AllDirectories))
                {
                    var written = file.LastWriteTimeUtc;
                    if (written < oldest) continue;
                    KeyValuePair<DateTime, CodexThread> hit; CodexThread thread;
                    lock (gate) if (!cache.TryGetValue(file.FullName, out hit) || hit.Key != written) hit = new KeyValuePair<DateTime, CodexThread>(written, null);
                    thread = hit.Value;
                    if (thread == null)
                    {
                        try { thread = Classify(FirstLine(file.FullName), ReadOnlyFiles.ReverseLines(file.FullName)); }
                        catch (IOException) { continue; }
                        catch (UnauthorizedAccessException) { continue; }
                    }
                    next[file.FullName] = new KeyValuePair<DateTime, CodexThread>(written, thread);
                    result.Add(thread);
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
        public static string ClaudeApp(string entrypoint)
        { return entrypoint != null && entrypoint.IndexOf("vscode", StringComparison.OrdinalIgnoreCase) >= 0 ? "vscode" : "claude"; }
    }
}
