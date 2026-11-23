using System;
using AIUsageManager;

internal static partial class Tests
{
    static DateTimeOffset At(int hour, int minute, int second = 0)
    {
        return new DateTimeOffset(new DateTime(2026, 11, 24, hour, minute, second, DateTimeKind.Local));
    }
    static WaitingItem Finished(string key, string service, string place, string cwd, DateTimeOffset since, WaitReason reason = WaitReason.Finished)
    {
        return new WaitingItem { Key = key, Service = service, Place = place, Cwd = cwd, Reason = reason, Since = since };
    }

    static void RunAiDoneTests()
    {
        Check("A finished session is written once, two minutes after it was seen, with the time it finished", () => {
            var log = new AiDoneLog(new string[0]);
            WaitingItem done = Finished("Claude/a", "Claude", "AIUsageManager", @"D:\Dev\AIUsageManager", At(10, 0, 30));
            log.Observe(new[] { done }, At(10, 0, 35));
            Assert(log.Due(At(10, 2, 34)) == null);
            AiDoneEntry entry = log.Due(At(10, 2, 35));
            Assert(entry != null && entry.Time == new DateTime(2026, 11, 24, 10, 0, 0) && entry.Text == "Claude 完了: AIUsageManager");
            Assert(log.Due(At(10, 2, 40)) == null);
            log.Observe(new[] { done }, At(10, 3, 0));
            Assert(log.Due(At(10, 9, 0)) == null);
        });
        Check("Finishes within two minutes become one line", () => {
            var log = new AiDoneLog(null);
            log.Observe(new[] { Finished("Claude/a", "Claude", "A", @"C:\a", At(10, 0)) }, At(10, 0, 5));
            log.Observe(new[] { Finished("Codex/b", "Codex", "B", @"C:\b", At(10, 1)), Finished("Claude/a", "Claude", "A", @"C:\a", At(10, 0)) }, At(10, 1, 5));
            AiDoneEntry entry = log.Due(At(10, 2, 5));
            Assert(entry.Text == "完了: Claude「A」・Codex「B」" && entry.Time == new DateTime(2026, 11, 24, 10, 0, 0));
            log.Observe(new[] { Finished("Claude/c", "Claude", "", null, At(11, 0)) }, At(11, 0, 1));
            Assert(log.Due(At(11, 3, 0)).Text == "Claude 完了");
            log.Observe(new[] { Finished("Claude/d", "Claude", "", null, At(12, 0)), Finished("Codex/e", "Codex", "E", null, At(12, 1)) }, At(12, 1, 1));
            Assert(log.Due(At(12, 4, 0)).Text == "完了: Claude・Codex「E」");
        });
        Check("The same session is written at most once in 30 minutes", () => {
            var log = new AiDoneLog(new string[0]);
            log.Observe(new[] { Finished("Claude/a", "Claude", "A", null, At(10, 0)) }, At(10, 0, 5));
            Assert(log.Due(At(10, 3, 0)) != null);
            log.Observe(new[] { Finished("Claude/a", "Claude", "A", null, At(10, 20)) }, At(10, 20, 5));
            Assert(log.Due(At(10, 23, 0)) == null);
            log.Observe(new[] { Finished("Claude/a", "Claude", "A", null, At(10, 31)) }, At(10, 31, 5));
            Assert(log.Due(At(10, 34, 0)).Time == new DateTime(2026, 11, 24, 10, 31, 0));
        });
        Check("Waits and questions are not written", () => {
            var log = new AiDoneLog(new string[0]);
            log.Observe(new[] { Finished("Claude/q", "Claude", "Q", null, At(10, 0), WaitReason.Question), Finished("Claude/c", "Claude", "C", null, At(10, 0), WaitReason.Confirm), null }, At(10, 0, 5));
            Assert(log.Due(At(10, 9, 0)) == null);
            log.Observe(null, At(10, 10, 0));
            Assert(log.Due(At(10, 19, 0)) == null);
        });
        Check("Sessions in excluded folders are not written", () => {
            var ex = new[] { @"C:\Notes\Vault" };
            Assert(AiDoneLog.Excluded(@"C:\Notes\Vault\.claude\worktrees\x", ex) && AiDoneLog.Excluded(@"C:\Notes\Vault", ex));
            Assert(AiDoneLog.Excluded(@"c:\notes\vault\", ex) && AiDoneLog.Excluded(@"\\?\C:\Notes\Vault\crews", ex));
            Assert(!AiDoneLog.Excluded(@"C:\Notes\Vault2", ex) && !AiDoneLog.Excluded(null, ex) && !AiDoneLog.Excluded("", ex) && !AiDoneLog.Excluded(@"D:\Dev", null));
            Assert(AiDoneLog.Excluded(@"C:\Notes\Vault\x", new[] { @"C:\Notes\Vault\" }));
            var log = new AiDoneLog(ex);
            log.Observe(new[] { Finished("Claude/v", "Claude", "Vault", @"C:\Notes\Vault", At(10, 0)) }, At(10, 0, 5));
            Assert(log.Due(At(10, 9, 0)) == null);
        });
        Check("The diary is written only when it has been quiet for 10 seconds, and not after 5 minutes", () => {
            DateTime now = new DateTime(2026, 11, 24, 1, 0, 0, DateTimeKind.Utc);
            Assert(AiDoneLog.Decide(now.AddSeconds(-9), now, now.AddMinutes(-1)) == AiDoneWait.Wait);
            Assert(AiDoneLog.Decide(now.AddSeconds(-10), now, now.AddMinutes(-1)) == AiDoneWait.Write);
            Assert(AiDoneLog.Decide(now.AddSeconds(-1), now, now.AddMinutes(-5).AddSeconds(-1)) == AiDoneWait.GiveUp);
            Assert(AiDoneLog.Decide(now.AddSeconds(-60), now, now.AddMinutes(-5)) == AiDoneWait.Write);
        });
        Check("The waiting list keeps each session's working folder", () => {
            var tracker = new WaitingTracker();
            var claude = new ClaudeSession { Pid = 1, SessionId = "s", Status = "busy", Cwd = @"D:\Dev\X" };
            var codex = new CodexThread { Id = "t", Cwd = @"D:\Dev\Y", State = CodexState.Working };
            tracker.Observe(new[] { claude }, true, new[] { codex }, true, At(10, 0));
            claude.Status = "idle"; claude.StatusUpdatedAt = At(10, 1); codex.State = CodexState.Finished; codex.Since = At(10, 1);
            WaitingList list = tracker.Observe(new[] { claude }, true, new[] { codex }, true, At(10, 1, 5));
            Assert(list.Items.Length == 2 && Array.Exists(list.Items, i => i.Cwd == @"D:\Dev\X") && Array.Exists(list.Items, i => i.Cwd == @"D:\Dev\Y"));
        });
    }
}
