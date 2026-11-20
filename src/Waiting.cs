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
