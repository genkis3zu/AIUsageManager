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

    static void RunAiDoneDetailTests()
    {
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
