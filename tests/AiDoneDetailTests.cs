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

    static void RunAiDoneDetailTests()
    {
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
