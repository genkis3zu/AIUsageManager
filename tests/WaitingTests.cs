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
    static ClaudeSession ClaudeAt(string id, string status, string waitingFor, DateTimeOffset? at)
    { return new ClaudeSession { Pid = 1, SessionId = id, Status = status, WaitingFor = waitingFor, StatusUpdatedAt = at, Name = id + "-name", Cwd = @"C:\src\x", Entrypoint = "claude-desktop" }; }
    static CodexThread CodexAt(string id, CodexState state, DateTimeOffset? at)
    { return new CodexThread { Id = id, State = state, Since = at, Cwd = @"C:\src\web-app", Source = "vscode" }; }
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
                var old = DateTime.UtcNow.AddHours(-1);
                var found = ClaudeSessions.Read(profile, () => new Dictionary<int, DateTime> { { 1, old }, { 2, old }, { 3, old }, { 4, old }, { 6, old } }, out ok);
                Assert(ok && found.Length == 1 && found[0].SessionId == "a");
            }
            finally { Directory.Delete(profile, true); }
        });
        Check("No Claude folder means Claude is not used; a Claude folder without sessions cannot be read", () => {
            bool ok;
            string none = Profile("x.txt", "x"), claude = Profile(Path.Combine(".claude", "settings.json"), "{}");
            try
            {
                Assert(ClaudeSessions.Read(none, () => new Dictionary<int, DateTime>(), out ok).Length == 0 && ok);
                ClaudeSessions.Read(claude, () => new Dictionary<int, DateTime>(), out ok);
                Assert(!ok);
            }
            finally { Directory.Delete(none, true); Directory.Delete(claude, true); }
        });
        Check("A reused process id does not bring back an old session", () => {
            string file = Path.Combine(".claude", "sessions", "1.json");
            string profile = Profile(file, J("{'pid':1,'sessionId':'a','status':'waiting','waitingFor':'input needed'}"));
            try
            {
                var written = DateTime.UtcNow.AddHours(-1);
                File.SetLastWriteTimeUtc(Path.Combine(profile, file), written);
                bool ok;
                Assert(ClaudeSessions.Read(profile, () => new Dictionary<int, DateTime> { { 1, DateTime.UtcNow } }, out ok).Length == 0);
                Assert(ClaudeSessions.Read(profile, () => new Dictionary<int, DateTime> { { 1, written.AddHours(-1) } }, out ok).Length == 1);
            }
            finally { Directory.Delete(profile, true); }
        });
        Check("Claude processes are listed once per read", () => {
            string dir = Path.Combine(".claude", "sessions");
            string profile = Profile(Path.Combine(dir, "1.json"), J("{'pid':1,'sessionId':'a','status':'busy'}"), Path.Combine(dir, "2.json"), J("{'pid':2,'sessionId':'b','status':'busy'}"),
                Path.Combine(dir, "3.json"), J("{'pid':3,'sessionId':'c','status':'busy'}"));
            try
            {
                int calls = 0; bool ok; var old = DateTime.UtcNow.AddHours(-1);
                var found = ClaudeSessions.Read(profile, () => { calls++; return new Dictionary<int, DateTime> { { 1, old }, { 2, old }, { 3, old } }; }, out ok);
                Assert(found.Length == 3 && calls == 1);
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
        Check("A Codex question the user stopped is no longer a question", () => {
            var stopped = Classify(CodexMeta("'vscode'"), CodexEvent("01", "task_started"), CodexAsk("02", "c1"), CodexEvent("03", "turn_aborted"));
            Assert(stopped.State != CodexState.Question && stopped.State != CodexState.Finished);
        });
        Check("A Codex record that grows without a new write time is read again", () => {
            string file = Path.Combine(".codex", "sessions", "2026", "11", "20", "grow.jsonl");
            string profile = Profile(file, CodexMeta("'vscode'") + "\n" + CodexEvent("01", "task_started") + "\n");
            try
            {
                string path = Path.Combine(profile, file); bool ok;
                var written = DateTime.UtcNow.AddMinutes(-1);
                File.SetLastWriteTimeUtc(path, written);
                Assert(CodexSessions.Read(profile, DateTimeOffset.UtcNow, () => true, out ok)[0].State == CodexState.Working);
                File.AppendAllText(path, CodexAsk("02", "c1") + "\n");
                File.SetLastWriteTimeUtc(path, written);
                Assert(CodexSessions.Read(profile, DateTimeOffset.UtcNow, () => true, out ok)[0].State == CodexState.Question);
            }
            finally { Directory.Delete(profile, true); }
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
                Assert(CodexSessions.Read(Path.Combine(profile, "missing"), DateTimeOffset.UtcNow, () => true, out ok).Length == 0 && ok);
                string codexHome = Profile(Path.Combine(".codex", "config.toml"), "");
                try { CodexSessions.Read(codexHome, DateTimeOffset.UtcNow, () => true, out ok); Assert(!ok); }
                finally { Directory.Delete(codexHome, true); }
            }
            finally { Directory.Delete(profile, true); }
        });
        Check("Waiting items come first, oldest first; finished items follow, newest first", () => {
            var tracker = new WaitingTracker();
            var busy = new[] { ClaudeAt("a", "busy", null, T), ClaudeAt("b", "busy", null, T) };
            tracker.Observe(busy, true, new CodexThread[0], true, T);
            var list = tracker.Observe(new[] { ClaudeAt("a", "idle", null, T.AddMinutes(5)), ClaudeAt("b", "idle", null, T.AddMinutes(8)), ClaudeAt("c", "waiting", "input needed", T.AddMinutes(9)), ClaudeAt("d", "waiting", "dialog open", T.AddMinutes(2)) },
                true, new CodexThread[0], true, T.AddMinutes(10));
            Assert(string.Join(",", list.Items.Select(i => i.Key)) == "Claude/d,Claude/c,Claude/b,Claude/a");
            Assert(list.Items[0].Reason == WaitReason.Confirm && list.Items[1].Reason == WaitReason.Question && list.Items[2].Reason == WaitReason.Finished);
            Assert(list.Items[1].Service == "Claude" && list.Items[1].Place == "c-name" && list.Items[1].App == "claude" && list.Items[1].Since == T.AddMinutes(9));
        });
        Check("Claude idle counts as finished only after the tracker saw it busy", () => {
            var tracker = new WaitingTracker();
            Assert(tracker.Observe(new[] { ClaudeAt("a", "idle", null, T) }, true, new CodexThread[0], true, T).Items.Length == 0);
            Assert(tracker.Observe(new[] { ClaudeAt("a", "busy", null, T.AddMinutes(1)) }, true, new CodexThread[0], true, T.AddMinutes(1)).Items.Length == 0);
            var list = tracker.Observe(new[] { ClaudeAt("a", "idle", null, T.AddMinutes(2)) }, true, new CodexThread[0], true, T.AddMinutes(2));
            Assert(list.Items.Length == 1 && list.Items[0].Reason == WaitReason.Finished && list.Items[0].Since == T.AddMinutes(2));
            Assert(tracker.Observe(new[] { ClaudeAt("a", "idle", null, T.AddMinutes(2)) }, true, new CodexThread[0], true, T.AddMinutes(3)).Items.Length == 1);
        });
        Check("Codex finished threads appear only after they were seen working", () => {
            var tracker = new WaitingTracker();
            Assert(tracker.Observe(new ClaudeSession[0], true, new[] { CodexAt("t1", CodexState.Finished, T) }, true, T).Items.Length == 0);
            tracker.Observe(new ClaudeSession[0], true, new[] { CodexAt("t2", CodexState.Working, T) }, true, T);
            var list = tracker.Observe(new ClaudeSession[0], true, new[] { CodexAt("t1", CodexState.Finished, T), CodexAt("t2", CodexState.Finished, T.AddMinutes(1)) }, true, T.AddMinutes(1));
            Assert(list.Items.Length == 1 && list.Items[0].Key == "Codex/t2" && list.Items[0].Service == "Codex" && list.Items[0].Place == "web-app" && list.Items[0].App == "vscode");
            var asked = tracker.Observe(new ClaudeSession[0], true, new[] { CodexAt("t3", CodexState.Question, T) }, true, T.AddMinutes(2));
            Assert(asked.Items.Length == 1 && asked.Items[0].Reason == WaitReason.Question);
        });
        Check("Finished items disappear after 30 minutes", () => {
            var tracker = new WaitingTracker();
            tracker.Observe(new[] { ClaudeAt("a", "busy", null, T) }, true, new CodexThread[0], true, T);
            Assert(tracker.Observe(new[] { ClaudeAt("a", "idle", null, T) }, true, new CodexThread[0], true, T.AddMinutes(30)).Items.Length == 1);
            Assert(tracker.Observe(new[] { ClaudeAt("a", "idle", null, T) }, true, new CodexThread[0], true, T.AddMinutes(31)).Items.Length == 0);
        });
        Check("A start time in the future counts as now", () => {
            var list = new WaitingTracker().Observe(new[] { ClaudeAt("a", "waiting", "input needed", T.AddMinutes(5)) }, true, new CodexThread[0], true, T);
            Assert(list.Items[0].Since == T);
            Assert(new WaitingTracker().Observe(new[] { ClaudeAt("b", "waiting", "input needed", null) }, true, new CodexThread[0], true, T).Items[0].Since == T);
        });
        Check("Unreadable sources are named, never shown as nothing waiting", () => {
            var list = new WaitingTracker().Observe(new ClaudeSession[0], false, new CodexThread[0], false, T);
            Assert(list.Unreadable.Length == 2 && list.Unreadable[0].Service == "Claude" && list.Unreadable[1].Service == "Codex" && list.Unreadable[0].Reason == "記録を読めません");
            Assert(new WaitingTracker().Observe(new ClaudeSession[0], true, new CodexThread[0], true, T).Unreadable.Length == 0);
        });
        Check("Reminders: 3 minutes, then every 10, never past 60 or for finished items", () => {
            var item = new WaitingItem { Key = "Claude/a", Service = "Claude", Place = "P", App = "claude", Reason = WaitReason.Question, Since = T };
            var done = new WaitingItem { Key = "Claude/b", Service = "Claude", Place = "P", App = "claude", Reason = WaitReason.Finished, Since = T };
            var reminders = new WaitingReminders();
            Func<int, int> at = m => reminders.Due(new[] { item, done }, T.AddMinutes(m), WaitingNoticeMode.Standard).Length;
            Assert(at(2) == 0 && at(3) == 1 && at(5) == 0 && at(13) == 1 && at(14) == 0 && at(61) == 0);
            Assert(new WaitingReminders().Due(new[] { item }, T.AddMinutes(30), WaitingNoticeMode.Off).Length == 0);
            var frequent = new WaitingReminders();
            Func<int, int> often = m => frequent.Due(new[] { item }, T.AddMinutes(m), WaitingNoticeMode.Frequent).Length;
            Assert(often(1) == 1 && often(3) == 0 && often(6) == 1);
        });
        Check("Reminder text names the service, place and reason", () => {
            var item = new WaitingItem { Key = "Claude/a", Service = "Claude", Place = "AIUsageManager", App = "claude", Reason = WaitReason.Question, Since = T };
            var codex = new WaitingItem { Key = "Codex/t", Service = "Codex", Place = "", App = "codex", Reason = WaitReason.Confirm, Since = T };
            var texts = new WaitingReminders().Due(new[] { item, codex }, T.AddMinutes(3), WaitingNoticeMode.Standard);
            Assert(texts.Length == 2 && texts[0] == "Claude（AIUsageManager）が質問の答えを3分待っています" && texts[1] == "Codexが確認を3分待っています");
        });
        Check("The waiting notice choice is saved with the other settings", () => {
            string path = Path.Combine(Path.GetTempPath(), "aum-wait-" + Guid.NewGuid().ToString("N") + ".ini");
            try
            {
                var settings = new ThemeSettings(path);
                Assert(settings.Save(ThemeMode.Dark) && settings.SaveNotifications(NotificationMode.Thresholds));
                Assert(settings.LoadWaiting() == WaitingNoticeMode.Standard && settings.SaveWaiting(WaitingNoticeMode.Off));
                Assert(settings.Load() == ThemeMode.Dark && settings.LoadNotifications() == NotificationMode.Thresholds && settings.LoadWaiting() == WaitingNoticeMode.Off);
                Assert(settings.Save(ThemeMode.Light) && settings.LoadWaiting() == WaitingNoticeMode.Off && settings.SaveWaiting(WaitingNoticeMode.Frequent) && settings.LoadWaiting() == WaitingNoticeMode.Frequent);
            }
            finally { File.Delete(path); }
        });
        Check("Only the four app links can be opened", () => {
            Assert(AppLinks.For("claude") == "claude://" && AppLinks.For("codex") == "codex://" && AppLinks.For("vscode") == "vscode://" && AppLinks.For("grokbot") == "grokbot://");
            Assert(AppLinks.For("https") == null && AppLinks.For(null) == null && AppLinks.For("Claude") == null);
        });
    }
}
