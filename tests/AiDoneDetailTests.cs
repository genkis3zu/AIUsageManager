using System;
using System.Collections.Generic;
using System.Linq;
using AIUsageManager;

internal static partial class Tests
{
    // Made-up Claude Code and Codex records. Single quotes stand for double quotes.
    static string Rec(string text) { return text.Replace('\'', '"'); }
    static string Stamp(DateTimeOffset at) { return at.ToString("o"); }
    static string CPrompt(DateTimeOffset at, string origin, string text)
    {
        return Rec("{'type':'user','timestamp':'" + Stamp(at) + "','isSidechain':false,'promptSource':'sdk','turnOrigin':'" + origin + "','message':{'role':'user','content':'" + text + "'}}");
    }
    static string CToolResult(DateTimeOffset at)
    {
        return Rec("{'type':'user','timestamp':'" + Stamp(at) + "','isSidechain':false,'message':{'role':'user','content':[{'type':'tool_result','tool_use_id':'u1','content':'ok'}]}}");
    }
    static string CAssistant(DateTimeOffset at, string part, bool sidechain = false)
    {
        return Rec("{'type':'assistant','timestamp':'" + Stamp(at) + "','isSidechain':" + (sidechain ? "true" : "false") + ",'message':{'role':'assistant','content':[" + part + "]}}");
    }
    static string CText(DateTimeOffset at, string text, bool sidechain = false) { return CAssistant(at, "{'type':'text','text':'" + text + "'}", sidechain); }
    static string CThinking(DateTimeOffset at) { return CAssistant(at, "{'type':'thinking','thinking':'考え中'}"); }
    static string CTool(DateTimeOffset at) { return CAssistant(at, "{'type':'tool_use','id':'u1','name':'Edit','input':{}}"); }
    static string CSystem(DateTimeOffset at) { return Rec("{'type':'system','subtype':'stop_hook_summary','timestamp':'" + Stamp(at) + "','isSidechain':false}"); }
    static string[] Claude1()
    {
        return new[] {
            CPrompt(At(10, 0), "human", "表を直して"), CThinking(At(10, 1)), CTool(At(10, 2)), CToolResult(At(10, 3)), CText(At(10, 5), "直しました"),
            CPrompt(At(10, 6), "peer", "相手からの知らせ"), CText(At(10, 7), "受け取りました"),
            CPrompt(At(10, 20), "human", "次"), CTool(At(10, 21)),
        };
    }
    static List<AiTurn> ClaudeTurns(string[] oldestFirst, DateTimeOffset after) { return ClaudeTranscript.Turns(oldestFirst.Reverse(), after); }

    static string XStarted(DateTimeOffset at, string trigger)
    {
        return Rec("{'timestamp':'" + Stamp(at) + "','type':'event_msg','payload':{'type':'task_started','turn_attribution':{'turn_trigger':'" + trigger + "'},'started_at':" + at.ToUnixTimeSeconds() + "}}");
    }
    static string XUser(DateTimeOffset at, string text)
    {
        return Rec("{'timestamp':'" + Stamp(at) + "','type':'response_item','payload':{'type':'message','role':'user','content':[{'type':'input_text','text':'" + text + "'}]}}");
    }
    static string XComplete(DateTimeOffset started, DateTimeOffset at, string message, long durationMs)
    {
        return Rec("{'timestamp':'" + Stamp(at) + "','type':'event_msg','payload':{'type':'task_complete','last_agent_message':'" + message + "','started_at':" + started.ToUnixTimeSeconds() + ",'completed_at':" + at.ToUnixTimeSeconds() + ",'duration_ms':" + durationMs + "}}");
    }

    static AiTurn Turn(DateTimeOffset start, DateTimeOffset end, double workMs, string reply, params string[] requests)
    {
        var turn = new AiTurn { Start = start, End = end, ByUser = true, Reply = reply, WorkMs = workMs };
        turn.Requests.AddRange(requests);
        return turn;
    }
    static AiStretch Stretch(string service, string place, string project, params AiTurn[] turns)
    {
        var stretch = new AiStretch { Key = service + "/s", Service = service, Place = place, Project = project, Cwd = @"C:\app" };
        stretch.Turns.AddRange(turns);
        return stretch;
    }
    static List<string> Details(List<string> entry) { return entry.Skip(3).Where(l => l != ">").ToList(); }

    static WaitingItem DoneItem(string key, DateTimeOffset since, string cwd = @"C:\src\app")
    {
        return new WaitingItem { Key = key, Service = "Claude", Place = "表の直し", Cwd = cwd, Reason = WaitReason.Finished, Since = since };
    }
    static AiTurn Ran(DateTimeOffset start, DateTimeOffset? end, bool byUser = true)
    {
        return new AiTurn { Start = start, End = end, ByUser = byUser, WorkMs = end.HasValue ? (end.Value - start).TotalMilliseconds : 0 };
    }
    // A stand-in for reading the records: the given turns that ended after `after`, counting the reads.
    sealed class FakeRecords
    {
        public List<AiTurn> Turns = new List<AiTurn>();
        public int Reads;
        public List<AiTurn> Read(WaitingItem item, DateTimeOffset after) { Reads++; return Turns.Where(t => t.End == null || t.End.Value > after).ToList(); }
    }
    static AiDoneDetailLog DetailLog(FakeRecords records, params string[] exclude) { return new AiDoneDetailLog(exclude, At(9, 0), records.Read); }

    // A throwaway Git repository for the commit tests; its name and e-mail are set in it only.
    static void RunGit(string folder, DateTimeOffset? at, params string[] arguments)
    {
        var info = new System.Diagnostics.ProcessStartInfo("git", string.Join(" ", arguments.Select(a => "\"" + a + "\"")))
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true };
        if (at.HasValue) { info.EnvironmentVariables["GIT_COMMITTER_DATE"] = at.Value.ToString("yyyy-MM-ddTHH:mm:sszzz"); info.EnvironmentVariables["GIT_AUTHOR_DATE"] = at.Value.ToString("yyyy-MM-ddTHH:mm:sszzz"); }
        using (var process = System.Diagnostics.Process.Start(info))
        {
            process.StandardOutput.ReadToEnd(); process.StandardError.ReadToEnd(); process.WaitForExit();
            if (process.ExitCode != 0) throw new Exception("git " + arguments[0] + " failed");
        }
    }
    static string TempFolder()
    {
        string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aiusage-test-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(folder);
        return folder;
    }
    static void RemoveFolder(string folder)
    {
        // Git writes its objects read-only.
        foreach (string file in System.IO.Directory.GetFiles(folder, "*", System.IO.SearchOption.AllDirectories)) System.IO.File.SetAttributes(file, System.IO.FileAttributes.Normal);
        System.IO.Directory.Delete(folder, true);
    }

    static void RunAiDoneDetailTests()
    {
        Check("A piece of work is written ten minutes after the user's last turn ended", () => {
            var records = new FakeRecords(); records.Turns.Add(Ran(At(10, 0), At(10, 5)));
            AiDoneDetailLog log = DetailLog(records);
            log.Observe(new[] { DoneItem("Claude/a", At(10, 5)) }, At(10, 5, 10));
            Assert(log.Due(At(10, 14)).Count == 0);
            List<AiStretch> due = log.Due(At(10, 15));
            Assert(due.Count == 1 && due[0].Start == At(10, 0) && due[0].End == At(10, 5) && due[0].Key == "Claude/a");
            Assert(due[0].Service == "Claude" && due[0].Place == "表の直し" && due[0].Cwd == @"C:\src\app" && due[0].Project == WaitingNames.Place(null, @"C:\src\app"));
            Assert(log.Due(At(10, 16)).Count == 0);
            // The same finished session, still on the waiting list, is not read again.
            int reads = records.Reads;
            log.Observe(new[] { DoneItem("Claude/a", At(10, 5)) }, At(10, 17));
            Assert(log.Due(At(10, 30)).Count == 0 && records.Reads == reads);
        });
        Check("Work that ended before the app started is not written", () => {
            var records = new FakeRecords(); records.Turns.Add(Ran(At(8, 0), At(8, 30)));
            AiDoneDetailLog log = DetailLog(records);
            log.Observe(new[] { DoneItem("Claude/a", At(9, 5)) }, At(9, 5));
            Assert(log.Due(At(9, 30)).Count == 0);
        });
        Check("Turns ten minutes apart or closer are one piece of work; farther apart are two", () => {
            var records = new FakeRecords(); records.Turns.AddRange(new[] { Ran(At(10, 0), At(10, 5)), Ran(At(10, 15), At(10, 20)) });
            AiDoneDetailLog log = DetailLog(records);
            log.Observe(new[] { DoneItem("Claude/a", At(10, 20)) }, At(10, 20));
            List<AiStretch> due = log.Due(At(10, 30));
            Assert(due.Count == 1 && due[0].Turns.Count == 2 && due[0].Start == At(10, 0) && due[0].End == At(10, 20));
            records = new FakeRecords(); records.Turns.AddRange(new[] { Ran(At(10, 0), At(10, 5)), Ran(At(10, 16), At(10, 20)) });
            log = DetailLog(records);
            log.Observe(new[] { DoneItem("Claude/a", At(10, 20)) }, At(10, 20));
            due = log.Due(At(10, 30));
            Assert(due.Count == 2 && due[0].End == At(10, 5) && due[1].Start == At(10, 16));
        });
        Check("Turns the AI started by itself neither count nor keep the work going", () => {
            var records = new FakeRecords();
            records.Turns.AddRange(new[] { Ran(At(10, 0), At(10, 5)), Ran(At(10, 12), At(10, 13), false), Ran(At(10, 22), null, false) });
            AiDoneDetailLog log = DetailLog(records);
            log.Observe(new[] { DoneItem("Claude/a", At(10, 5)) }, At(10, 5));
            List<AiStretch> due = log.Due(At(10, 15));
            Assert(due.Count == 1 && due[0].Turns.Count == 1 && due[0].End == At(10, 5));
            records = new FakeRecords(); records.Turns.AddRange(new[] { Ran(At(10, 12), At(10, 13), false) });
            log = DetailLog(records);
            log.Observe(new[] { DoneItem("Claude/a", At(10, 13)) }, At(10, 13));
            Assert(log.Due(At(10, 30)).Count == 0 && log.Due(At(10, 40)).Count == 0 && records.Reads == 1);
        });
        Check("A long turn asked within ten minutes keeps the work open", () => {
            var records = new FakeRecords(); AiTurn second = Ran(At(10, 10), null);
            records.Turns.AddRange(new[] { Ran(At(10, 0), At(10, 5)), second });
            AiDoneDetailLog log = DetailLog(records);
            log.Observe(new[] { DoneItem("Claude/a", At(10, 5)) }, At(10, 5));
            Assert(log.Due(At(10, 30)).Count == 0);
            second.End = At(10, 40); second.WorkMs = 30 * 60000;
            List<AiStretch> due = log.Due(At(10, 50));
            Assert(due.Count == 1 && due[0].Start == At(10, 0) && due[0].End == At(10, 40));
        });
        Check("A turn stopped with Esc ends when the waiting list saw the session finish", () => {
            // Claude Code writes no reply after an interrupt, so the record alone never shows the end.
            var records = new FakeRecords(); records.Turns.Add(Ran(At(10, 0), null));
            AiDoneDetailLog log = DetailLog(records);
            log.Observe(new[] { DoneItem("Claude/a", At(10, 3)) }, At(10, 3));
            Assert(log.Due(At(10, 12)).Count == 0);
            List<AiStretch> due = log.Due(At(10, 13));
            Assert(due.Count == 1 && due[0].End == At(10, 3) && due[0].WorkMs == 3 * 60000 && records.Turns[0].End == null);
        });
        Check("Helper threads and excluded folders are never read", () => {
            var records = new FakeRecords(); records.Turns.Add(Ran(At(10, 0), At(10, 5)));
            AiDoneDetailLog log = DetailLog(records, @"C:\Notes");
            WaitingItem helper = DoneItem("Codex/h", At(10, 5)); helper.Helper = true;
            log.Observe(new[] { helper, DoneItem("Claude/n", At(10, 5), @"C:\Notes\day") }, At(10, 5));
            Assert(log.Due(At(10, 30)).Count == 0 && log.All().Count == 0 && records.Reads == 0);
        });
        Check("A record that cannot be read is skipped this time", () => {
            int calls = 0;
            var log = new AiDoneDetailLog(new string[0], At(9, 0), (item, after) => {
                calls++;
                if (calls == 1) throw new System.IO.IOException("busy");
                return new List<AiTurn> { Ran(At(10, 0), At(10, 5)) };
            });
            log.Observe(new[] { DoneItem("Claude/a", At(10, 5)) }, At(10, 5));
            Assert(log.Due(At(10, 15)).Count == 0 && log.Due(At(10, 16)).Count == 1);
        });
        Check("When the app ends, waiting work is given at once", () => {
            var records = new FakeRecords(); records.Turns.AddRange(new[] { Ran(At(10, 0), At(10, 5)), Ran(At(10, 8), null) });
            AiDoneDetailLog log = DetailLog(records);
            log.Observe(new[] { DoneItem("Claude/a", At(10, 5)) }, At(10, 5));
            log.Due(At(10, 5, 30));
            List<AiStretch> all = log.All();
            Assert(all.Count == 1 && all[0].Turns.Count == 1 && all[0].End == At(10, 5));
            Assert(log.All().Count == 0 && log.Due(At(10, 30)).Count == 0);
        });
        Check("Records are read at most once a minute per session", () => {
            var records = new FakeRecords(); records.Turns.Add(Ran(At(10, 0), At(10, 5)));
            AiDoneDetailLog log = DetailLog(records);
            log.Observe(new[] { DoneItem("Claude/a", At(10, 5)) }, At(10, 5));
            for (int s = 0; s < 60; s += 10) log.Due(At(10, 5, s));
            Assert(records.Reads == 1);
            log.Due(At(10, 6, 0));
            Assert(records.Reads == 2);
        });
        Check("Commits made in the folder during the work are found", () => {
            string folder = TempFolder();
            try
            {
                RunGit(folder, null, "init", "-q");
                RunGit(folder, null, "config", "user.name", "Test");
                RunGit(folder, null, "config", "user.email", "test@example.invalid");
                RunGit(folder, null, "config", "commit.gpgsign", "false");
                foreach (var at in new[] { At(9, 0), At(10, 5), At(10, 10), At(11, 0) })
                    RunGit(folder, at, "commit", "-q", "--allow-empty", "-m", "made at " + at.ToString("HH:mm"));
                List<string> commits = GitCommits.Between(folder, At(10, 0), At(10, 30));
                Assert(commits.Count == 2 && commits[0].EndsWith(" made at 10:10") && commits[1].EndsWith(" made at 10:05"));
                Assert(commits.All(c => System.Text.RegularExpressions.Regex.IsMatch(c, "^[0-9a-f]{7,} .+$")));
                // A folder inside the repository is in Git too.
                string inner = System.IO.Path.Combine(folder, "src");
                System.IO.Directory.CreateDirectory(inner);
                Assert(GitCommits.Between(inner, At(10, 0), At(10, 30)).Count == 2);
            }
            finally { RemoveFolder(folder); }
        });
        Check("A folder that is not in Git gives no commits", () => {
            string folder = TempFolder();
            try { Assert(GitCommits.Between(folder, At(10, 0), At(10, 30)).Count == 0); }
            finally { RemoveFolder(folder); }
            Assert(GitCommits.Between(folder, At(10, 0), At(10, 30)).Count == 0);
            Assert(GitCommits.Between(null, At(10, 0), At(10, 30)).Count == 0 && GitCommits.Between("", At(10, 0), At(10, 30)).Count == 0);
        });
        Check("Copied text cannot become tags, links, embeds or headings", () => {
            Assert(AiDoneText.Clean("## 直し #tag [[A|B]] [[C]] ![[D.png]] **太字** `x`") == "直し ＃tag B C 太字 x");
            Assert(AiDoneText.Clean("一\r\n二\t三") == "一 二 三");
            string hundred = new string('あ', 100);
            Assert(AiDoneText.Clean(hundred) == hundred && AiDoneText.Clean(hundred + "い") == hundred + "…");
            string emoji = new string('あ', 99) + "😀";
            Assert(AiDoneText.Clean(emoji + "い") == new string('あ', 99) + "…");
            Assert(AiDoneText.Clean(null) == "" && AiDoneText.Clean("  ") == "");
            // Written after "> - ", a leading [ ] would become a task in the diary.
            Assert(AiDoneText.Clean("[ ] やること") == "やること" && AiDoneText.Clean("[x] 済み") == "済み");
            Assert(AiDoneText.ReplyLines("- [ ] 確かめる", 3).SequenceEqual(new[] { "確かめる" }));
        });
        Check("The first lines of a reply skip blank lines, headings, code, tables and rules", () => {
            string reply = "## まとめ\n\n- 表を直しました\n```\ncode\n```\n| a | b |\n---\n1. テストが通りました\n> 注意: なし\n4行目";
            Assert(AiDoneText.ReplyLines(reply, 3).SequenceEqual(new[] { "表を直しました", "テストが通りました", "注意: なし" }));
            Assert(AiDoneText.ReplyLines(reply, 5).SequenceEqual(new[] { "表を直しました", "テストが通りました", "注意: なし", "4行目" }));
            Assert(AiDoneText.ReplyLines(null, 3).Count == 0 && AiDoneText.ReplyLines("```\n# x\n```", 3).Count == 0);
        });
        Check("An entry has the time range, the title and the details", () => {
            AiStretch stretch = Stretch("Claude", "表の直し", "App", Turn(At(10, 0), At(10, 15), 9 * 60000, "返事1\n返事2", "一つ目", "二つ目"));
            List<string> entry = AiDoneFormat.Entry(stretch, new[] { "abc1234 一つ目", "def5678 二つ目" }, false);
            Assert(entry.SequenceEqual(new[] {
                "[10:00-10:15]", "🤖 **Claude Code: 表の直し**（App・作業 9分）", "> [!info]- 詳細",
                "> **🗣️ 頼んだこと:**", "> - 一つ目", "> - 二つ目", ">",
                "> **🤖 Claude の処理・出力:**", "> - 返事1", "> - 返事2",
                "> - 📦 成果物: コミット 2件（`abc1234` 一つ目 ／ `def5678` 二つ目）",
            }));
        });
        Check("Entry details: same minute, same name, under a minute, more requests, more commits, no commits", () => {
            List<string> entry = AiDoneFormat.Entry(Stretch("Claude", "表の直し", "App", Turn(At(10, 0, 5), At(10, 0, 50), 30000, "済み", "直して")), new string[0], false);
            Assert(entry[0] == "[10:00]" && entry[1] == "🤖 **Claude Code: 表の直し**（App・作業 1分未満）");
            Assert(!entry.Any(l => l.Contains("📦")));
            entry = AiDoneFormat.Entry(Stretch("Claude", "App", "App", Turn(At(10, 0), At(10, 9), 9 * 60000, "済み", "直して")), new string[0], false);
            Assert(entry[1] == "🤖 **Claude Code: App**（作業 9分）");
            AiStretch two = Stretch("Codex", "Web", "Web",
                Turn(At(10, 0), At(10, 5), 5 * 60000, "前の返事", "一つ目\n続きの行", "二つ目"),
                Turn(At(10, 20), At(10, 24), 4 * 60000, "最後の返事", "三つ目", "四つ目", "五つ目"));
            entry = AiDoneFormat.Entry(two, new[] { "1111111 a", "2222222 b", "3333333 c", "4444444 d", "5555555 e" }, true);
            Assert(entry[0] == "[10:00-10:24]" && entry[1] == "🤖 **Codex: Web**（作業 9分）");
            Assert(entry.Contains("> - 一つ目") && entry.Contains("> - 三つ目（ほか 2件）") && !entry.Any(l => l.Contains("四つ目") || l.Contains("続きの行")));
            Assert(entry.Contains("> **🤖 Codex の処理・出力:**") && entry.Contains("> - 最後の返事") && !entry.Any(l => l.Contains("前の返事")));
            // git log stops at six, so only "six or more" is known.
            Assert(entry.Contains("> - 📦 成果物: コミット 6件以上（`1111111` a ／ `2222222` b ／ `3333333` c ／ `4444444` d ／ `5555555` e ／ ほか）"));
        });
        Check("Entry details are at most eight lines, cutting the reply first", () => {
            List<string> entry = AiDoneFormat.Entry(Stretch("Claude", "N", "P", Turn(At(10, 0), At(10, 9), 9 * 60000, "返事1\n返事2\n返事3", "一", "二", "三")), new[] { "abc1234 直し" }, false);
            Assert(Details(entry).Count == 8 && entry.Contains("> - 返事2") && !entry.Contains("> - 返事3"));
            entry = AiDoneFormat.Entry(Stretch("Claude", "N", "P", Turn(At(10, 0), At(10, 9), 9 * 60000, "返事1\n返事2\n返事3")), new string[0], false);
            Assert(!entry.Any(l => l.Contains("頼んだこと")) && !entry.Contains(">") && entry.Contains("> - 返事3"));
            Assert(entry[2] == "> [!info]- 詳細" && entry[3] == "> **🤖 Claude の処理・出力:**");
        });
        Check("Claude Code turns: the user's requests, the last reply, and the end", () => {
            List<AiTurn> turns = ClaudeTurns(Claude1(), At(9, 0));
            Assert(turns.Count == 3);
            Assert(turns[0].ByUser && turns[0].Requests.SequenceEqual(new[] { "表を直して" }) && turns[0].Reply == "直しました");
            Assert(turns[0].Start == At(10, 0) && turns[0].End == At(10, 5) && turns[0].WorkMs == 300000);
            Assert(!turns[1].ByUser);
            Assert(turns[2].ByUser && turns[2].End == null && turns[2].Requests.SequenceEqual(new[] { "次" }));
        });
        Check("Claude Code turns skip helper agents, command text and broken lines", () => {
            var lines = new[] {
                CPrompt(At(10, 0), "human", "<command-name>/x</command-name>"), CText(At(10, 1), "本当の返事"),
                CText(At(10, 2), "下請けの返事", true), "{", "", CSystem(At(10, 3)),
            };
            List<AiTurn> turns = ClaudeTurns(lines, At(9, 0));
            Assert(turns.Count == 1 && turns[0].ByUser && turns[0].Requests.Count == 0 && turns[0].Reply == "本当の返事");
            // Records Claude Code writes after the reply (hooks, titles) neither move the end nor make the turn look unfinished.
            Assert(turns[0].End == At(10, 1));
        });
        Check("Claude Code turns stop at the given time", () => {
            List<AiTurn> turns = ClaudeTurns(Claude1(), At(10, 10));
            Assert(turns.Count == 1 && turns[0].Start == At(10, 20));
        });
        Check("Codex turns: user-started ones, the request and the final reply", () => {
            var lines = new[] {
                XStarted(At(10, 0), "composer"), XUser(At(10, 0, 1), "動かして"), XUser(At(10, 0, 2), "<environment_context>C:\\\\x</environment_context>"),
                XComplete(At(10, 0), At(10, 4), "動きました", 240000),
                XStarted(At(10, 10), "automation_heartbeat_scheduled"), XComplete(At(10, 10), At(10, 11), "見回りました", 60000),
                XStarted(At(10, 20), "send_user_message_async_question"),
                XUser(At(10, 20, 1), "# Files mentioned by the user:\\n\\n## a.md: C:/x/a.md\\n\\n## My request:\\n資料を読んで"),
                XUser(At(10, 20, 2), "# AGENTS.md instructions for C:\\\\x\\n\\n<INSTRUCTIONS>決まり</INSTRUCTIONS>"),
            };
            List<AiTurn> turns = CodexRollout.Turns(lines.Reverse(), At(9, 0));
            Assert(turns.Count == 3);
            Assert(turns[0].ByUser && turns[0].Requests.SequenceEqual(new[] { "動かして" }) && turns[0].Reply == "動きました");
            Assert(turns[0].WorkMs == 240000 && turns[0].Start == At(10, 0) && turns[0].End == At(10, 4));
            Assert(!turns[1].ByUser);
            Assert(turns[2].ByUser && turns[2].End == null && turns[2].Requests.SequenceEqual(new[] { "資料を読んで" }));
            Assert(CodexRollout.Turns(lines.Reverse(), At(10, 5)).Count == 2);
        });
    }
}
