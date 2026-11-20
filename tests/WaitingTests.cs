using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AIUsageManager;

internal static partial class Tests
{
    // A throwaway user profile folder with the given relative files.
    static string Profile(params string[] pathAndContent)
    {
        string profile = Path.Combine(Path.GetTempPath(), "aum-wait-" + Guid.NewGuid().ToString("N"));
        for (int i = 0; i + 1 < pathAndContent.Length; i += 2)
        {
            string file = Path.Combine(profile, pathAndContent[i]);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, pathAndContent[i + 1]);
        }
        return profile;
    }
    static string CodexMeta(string source)
    { return J("{'timestamp':'2026-11-20T01:00:00Z','type':'session_meta','payload':{'id':'t1','cwd':'C:\\\\src\\\\web-app','originator':'Codex Desktop','source':" + source + "}}"); }
    static string CodexEvent(string minute, string kind)
    { return J("{'timestamp':'2026-11-20T01:" + minute + ":00Z','type':'event_msg','payload':{'type':'" + kind + "'}}"); }
    static string CodexAsk(string minute, string id)
    { return J("{'timestamp':'2026-11-20T01:" + minute + ":00Z','type':'response_item','payload':{'type':'function_call','name':'request_user_input_async','call_id':'" + id + "','arguments':'{}'}}"); }
    static string CodexAnswer(string minute, string id)
    { return J("{'timestamp':'2026-11-20T01:" + minute + ":00Z','type':'response_item','payload':{'type':'function_call_output','call_id':'" + id + "','output':'x'}}"); }
    // Lines as written (oldest first) are handed to Classify newest first.
    static CodexThread Classify(params string[] lines)
    { return CodexSessions.Classify(lines[0], lines.Reverse()); }
    static void RunWaitingTests()
    {
        Check("Claude session files give status, reason and start time", () => {
            var s = ClaudeSessions.Parse(J("{'pid':12,'sessionId':'s1','status':'waiting','waitingFor':'input needed','statusUpdatedAt':1763600000000,'cwd':'C:\\\\src\\\\Proj','name':'Fix login','entrypoint':'claude-desktop'}"));
            Assert(s != null && s.Pid == 12 && s.SessionId == "s1" && s.Status == "waiting" && s.WaitingFor == "input needed");
            Assert(s.Cwd == @"C:\src\Proj" && s.Name == "Fix login" && s.Entrypoint == "claude-desktop");
            Assert(s.StatusUpdatedAt == DateTimeOffset.FromUnixTimeMilliseconds(1763600000000));
            Assert(ClaudeSessions.Parse("not json") == null && ClaudeSessions.Parse(J("{'status':'idle'}")) == null);
        });
        Check("Broken or partial Claude session files are skipped", () => {
            string dir = Path.Combine(".claude", "sessions");
            string profile = Profile(
                Path.Combine(dir, "1.json"), J("{'pid':1,'sessionId':'a','status':'busy'}"),
                Path.Combine(dir, "2.json"), "{\"pid\":",
                Path.Combine(dir, "3.json"), "not json",
                Path.Combine(dir, "4.json"), J("{'pid':4,'sessionId':'d','status':'busy','name':'") + new string('x', 66000) + "\"}",
                Path.Combine(dir, "5.json"), J("{'pid':5,'sessionId':'e','status':'busy'}"),
                Path.Combine(dir, "6.txt"), J("{'pid':6,'sessionId':'f','status':'busy'}"));
            try
            {
                bool ok;
                var found = ClaudeSessions.Read(profile, pid => pid != 5, out ok);
                Assert(ok && found.Length == 1 && found[0].SessionId == "a");
                ClaudeSessions.Read(Path.Combine(profile, "missing"), pid => true, out ok);
                Assert(!ok);
            }
            finally { Directory.Delete(profile, true); }
        });
        Check("Places prefer the session name, then the project of a worktree", () => {
            Assert(WaitingNames.Place("Fix login", @"C:\src\web") == "Fix login");
            Assert(WaitingNames.Place("", @"C:\src\Project\.claude\worktrees\api-x") == "Project");
            Assert(WaitingNames.Place(null, @"C:\src\web-app") == "web-app" && WaitingNames.Place(null, @"C:\src\web-app\") == "web-app");
            Assert(WaitingNames.Place(null, null) == "");
        });
        Check("Place text is cleaned and cut to 40 characters", () => {
            string c = WaitingNames.Clean("a\u0007b  \n c" + new string('x', 60));
            Assert(c.Length <= 40 && !c.Any(char.IsControl) && !c.Contains("  ") && c.StartsWith("ab c"));
            Assert(WaitingNames.Place("  Fix\u0007  login ", null) == "Fix login");
        });
        Check("Claude sessions open the desktop app unless they run in VS Code", () => {
            Assert(WaitingNames.ClaudeApp("claude-desktop") == "claude" && WaitingNames.ClaudeApp("claude-vscode") == "vscode" && WaitingNames.ClaudeApp(null) == "claude");
        });
         Check("A pending Codex question is a question; an answered one is not", () => {
            var asked = Classify(CodexMeta("'vscode'"), CodexEvent("01", "task_started"), CodexAsk("02", "c1"));
            Assert(asked.State == CodexState.Question && asked.Since == DateTimeOffset.Parse("2026-11-20T01:02:00Z"));
            var done = Classify(CodexMeta("'vscode'"), CodexEvent("01", "task_started"), CodexAsk("02", "c1"), CodexAnswer("03", "c1"), CodexEvent("04", "task_complete"));
            Assert(done.State == CodexState.Finished && done.Since == DateTimeOffset.Parse("2026-11-20T01:04:00Z"));
            Assert(Classify(CodexMeta("'vscode'"), CodexAsk("02", "c1"), CodexAnswer("03", "c1")).State == CodexState.Unknown);
        });
        Check("Codex working, finished and unknown threads", () => {
            Assert(Classify(CodexMeta("'vscode'"), CodexEvent("01", "task_complete"), CodexEvent("05", "task_started")).State == CodexState.Working);
            Assert(Classify(CodexMeta("'vscode'")).State == CodexState.Unknown);
        });
        Check("A cut-off last Codex line does not break classification", () => {
            var t = Classify(CodexMeta("'vscode'"), CodexEvent("04", "task_complete"), "{\"timestamp\":\"2026-");
            Assert(t.State == CodexState.Finished);
        });
        Check("Codex meta gives folder and app", () => {
            var vscode = Classify(CodexMeta("'vscode'"), CodexEvent("01", "task_started"));
            Assert(vscode.Id == "t1" && vscode.Cwd == @"C:\src\web-app" && vscode.Originator == "Codex Desktop" && CodexSessions.App(vscode) == "vscode");
            var desktop = Classify(CodexMeta("{'kind':'app'}"), CodexEvent("01", "task_started"));
            Assert(desktop.Source == null && CodexSessions.App(desktop) == "codex");
        });
        Check("Codex threads are read only while a Codex app runs and only from recent files", () => {
            string dir = Path.Combine(".codex", "sessions", "2026", "11", "20");
            string profile = Profile(Path.Combine(dir, "new.jsonl"), CodexMeta("'vscode'") + "\n" + CodexEvent("01", "task_started") + "\n",
                Path.Combine(dir, "old.jsonl"), CodexMeta("'vscode'") + "\n" + CodexEvent("01", "task_started") + "\n");
            try
            {
                File.SetLastWriteTimeUtc(Path.Combine(profile, dir, "old.jsonl"), DateTime.UtcNow.AddHours(-13));
                bool ok;
                Assert(CodexSessions.Read(profile, DateTimeOffset.UtcNow, () => false, out ok).Length == 0 && ok);
                var threads = CodexSessions.Read(profile, DateTimeOffset.UtcNow, () => true, out ok);
                Assert(ok && threads.Length == 1 && threads[0].State == CodexState.Working);
                CodexSessions.Read(Path.Combine(profile, "missing"), DateTimeOffset.UtcNow, () => true, out ok);
                Assert(!ok);
            }
            finally { Directory.Delete(profile, true); }
        });
   }
}
