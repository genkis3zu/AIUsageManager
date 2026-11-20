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
    }
}
