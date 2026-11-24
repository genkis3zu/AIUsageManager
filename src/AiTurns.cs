using System;
using System.Collections.Generic;
using System.Linq;

namespace AIUsageManager
{
    // One request and the work that followed it, read from a Claude Code or Codex record on this PC.
    // End is null while the AI is still working. WorkMs is how long the AI worked on it.
    internal sealed class AiTurn
    {
        public DateTimeOffset Start;
        public DateTimeOffset? End;
        public bool ByUser;
        public List<string> Requests = new List<string>();
        public string Reply;
        public double WorkMs;
    }

    static class AiRecordText
    {
        // The text parts of a message's content: the string itself, or the "text"/"input_text" parts joined.
        public static string Of(object content, string partType)
        {
            if (content is string) return (string)content;
            var parts = content as object[];
            if (parts == null) return null;
            var texts = new List<string>();
            foreach (var part in parts.OfType<Dictionary<string, object>>())
                if (Json.Str(part, "type") == partType && Json.Str(part, "text") != null) texts.Add(Json.Str(part, "text"));
            return texts.Count == 0 ? null : string.Join("\n", texts);
        }
        public static bool Has(object content, string partType)
        {
            var parts = content as object[];
            return parts != null && parts.OfType<Dictionary<string, object>>().Any(p => Json.Str(p, "type") == partType);
        }
        // Text the apps add on their own (command tags, context blocks) starts with "<" and is not a request.
        public static void AddRequest(List<string> requests, string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0 || text.StartsWith("<")) return;
            requests.Add(text);
        }
        public static void Finish(AiTurn turn, DateTimeOffset? end)
        {
            turn.End = end;
            if (end.HasValue && turn.WorkMs == 0) turn.WorkMs = Math.Max(0, (end.Value - turn.Start).TotalMilliseconds);
        }
    }

    // ~/.claude/projects/<folder>/<session>.jsonl. A user record with turnOrigin (or promptSource) starts a turn;
    // tool results, helper agents (isSidechain) and the records Claude Code writes around the conversation are not turns.
    internal static class ClaudeTranscript
    {
        public static List<AiTurn> Turns(IEnumerable<string> linesNewestFirst, DateTimeOffset after, int maxLines = 20000)
        {
            var records = new List<Dictionary<string, object>>(); int seen = 0;
            foreach (string line in linesNewestFirst ?? new string[0])
            {
                if (++seen > maxLines) break;
                var row = Json.Parse(line ?? "");
                if (row == null) continue;
                string type = Json.Str(row, "type");
                if (type != "user" && type != "assistant") continue;
                if (Json.Get(row, "isSidechain") as bool? == true) continue;
                var at = Json.Date(Json.Get(row, "timestamp"));
                if (!at.HasValue) continue;
                records.Add(row);
                if (IsStart(row) && at.Value <= after) break;
            }
            records.Reverse();
            var turns = new List<AiTurn>(); AiTurn current = null;
            DateTimeOffset last = DateTimeOffset.MinValue; bool lastIsReply = false;
            foreach (var row in records)
            {
                DateTimeOffset at = Json.Date(Json.Get(row, "timestamp")).Value;
                var content = Json.Get(Json.Obj(row, "message"), "content");
                if (IsStart(row))
                {
                    if (current != null) AiRecordText.Finish(current, last);
                    current = new AiTurn { Start = at, ByUser = Json.Str(row, "turnOrigin") == "human" };
                    AiRecordText.AddRequest(current.Requests, AiRecordText.Of(content, "text"));
                    turns.Add(current); last = at; lastIsReply = false;
                    continue;
                }
                if (current == null) continue;
                last = at; lastIsReply = false;
                if (Json.Str(row, "type") != "assistant") continue;
                string text = AiRecordText.Of(content, "text");
                if (text == null || text.Trim().Length == 0) continue;
                current.Reply = text;
                lastIsReply = !AiRecordText.Has(content, "tool_use");
            }
            if (current != null) AiRecordText.Finish(current, lastIsReply ? last : (DateTimeOffset?)null);
            return turns.Where(t => t.End == null || t.End.Value > after).ToList();
        }
        static bool IsStart(Dictionary<string, object> row)
        {
            return Json.Str(row, "type") == "user" && (Json.Str(row, "turnOrigin") != null || Json.Str(row, "promptSource") != null);
        }
    }

    // ~/.codex/sessions/**/rollout-*.jsonl. event_msg task_started starts a turn and task_complete ends it.
    internal static class CodexRollout
    {
        static readonly string[] UserTriggers = { "composer", "send_user_message_async_question" };
        public static List<AiTurn> Turns(IEnumerable<string> linesNewestFirst, DateTimeOffset after, int maxLines = 20000)
        {
            var records = new List<Dictionary<string, object>>(); int seen = 0;
            foreach (string line in linesNewestFirst ?? new string[0])
            {
                if (++seen > maxLines) break;
                var row = Json.Parse(line ?? "");
                string kind = Kind(row);
                if (kind == null) continue;
                records.Add(row);
                if (kind == "task_started" && Started(row) <= after) break;
            }
            records.Reverse();
            var turns = new List<AiTurn>(); AiTurn current = null; DateTimeOffset? last = null;
            foreach (var row in records)
            {
                var payload = Json.Obj(row, "payload");
                var at = Json.Date(Json.Get(row, "timestamp"));
                switch (Kind(row))
                {
                    case "task_started":
                        // A turn the user stopped has no task_complete; it ends with its last record.
                        if (current != null && current.End == null && last.HasValue) AiRecordText.Finish(current, last);
                        current = new AiTurn { Start = Started(row), ByUser = UserTriggers.Contains(Json.Str(Json.Obj(payload, "turn_attribution"), "turn_trigger")) };
                        turns.Add(current); last = current.Start;
                        break;
                    case "message":
                        if (current == null) break;
                        if (at.HasValue) last = at;
                        AiRecordText.AddRequest(current.Requests, Request(AiRecordText.Of(Json.Get(payload, "content"), "input_text")));
                        break;
                    case "turn_aborted":
                        if (current != null && current.End == null) AiRecordText.Finish(current, at ?? last);
                        break;
                    case "task_complete":
                        if (current == null) break;
                        var duration = Json.Number(Json.Get(payload, "duration_ms"));
                        if (duration.HasValue) current.WorkMs = duration.Value;
                        AiRecordText.Finish(current, Json.Date(Json.Get(payload, "completed_at")) ?? at);
                        current.Reply = Json.Str(payload, "last_agent_message");
                        break;
                }
            }
            return turns.Where(t => t.End == null || t.End.Value > after).ToList();
        }
        static string Kind(Dictionary<string, object> row)
        {
            var payload = Json.Obj(row, "payload");
            string type = Json.Str(row, "type"), kind = Json.Str(payload, "type");
            if (type == "event_msg" && (kind == "task_started" || kind == "task_complete" || kind == "turn_aborted")) return kind;
            if (type == "response_item" && kind == "message" && Json.Str(payload, "role") == "user") return "message";
            return null;
        }
        static DateTimeOffset Started(Dictionary<string, object> row)
        {
            return Json.Date(Json.Get(Json.Obj(row, "payload"), "started_at")) ?? Json.Date(Json.Get(row, "timestamp")) ?? DateTimeOffset.MinValue;
        }
        // Codex puts the folder's AGENTS.md and the mentioned files in front of a request as "# ..." blocks;
        // the request itself is what follows "## My request". A "# ..." message without it is not a request.
        static string Request(string text)
        {
            if (text == null || !text.TrimStart().StartsWith("#")) return text;
            var lines = text.Replace("\r", "").Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
                if (lines[i].StartsWith("## My request")) return string.Join("\n", lines.Skip(i + 1));
            return null;
        }
    }
}
