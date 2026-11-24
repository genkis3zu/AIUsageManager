# AI の完了の記録に中身を入れる 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `now_doing_ai_done_detail=on` のとき、🤖 の記録を「時刻の幅・太字の題・畳める詳細（頼んだこと・できたこと・成果物）」にし、ひと続きの作業ごとに1つ書く。

**Architecture:** 会話の記録の読み取り（Claude Code・Codex）は、行の並びを受け取って「ターン」の一覧を返す関数にする（ファイルを開くのは呼ぶ側）。ひと続きの作業のまとめと書く時期は `AiDoneDetailLog` が持ち、ターンの読み取りは差し込み口（`Func`）で受け取るので、時刻を進めるテストで確かめられる。日誌へは今の `NowDoingNote` に複数行の記録を差し込む口を足して書く。オフのときは今の `AiDoneLog` のまま（Codex のヘルパーのスレッドを除くことだけ足す）。

**Tech Stack:** C# 5 / .NET Framework 4.8 / WinForms（`build.ps1` の csc）。新しい依存なし。git はコミットを読むときだけ、今ある `NowDoingScript.RunAsync` で動かす。

**Spec:** `docs/superpowers/specs/2026-11-24-ai-done-detail-design.md`

## Global Constraints

- 会話の記録を開くのは `now_doing_ai_done_detail=on` のときだけ。オフのときは読む関数が一度も呼ばれない。
- 通信しない。会話の中身を日誌以外に残さない。除くフォルダーの作業は読まない・書かない。
- ひと続きの作業: 利用者の作業が終わってから10分以内に次の依頼が来たらつなぐ。最後の完了から10分たったら書く。トレイアプリが終わるとき（終了・`SessionEnding`）は待たずに書く（日誌の静けさを待たない・全体で5秒まで）。
- 利用者の作業: Claude Code は `turnOrigin` が `human` のターン。Codex は `task_started.payload.turn_attribution.turn_trigger` が `composer` か `send_user_message_async_question` のターン。ほかは数えず、つなぎもしない。
- Codex のヘルパーのスレッド（`session_meta.payload.source` が `subagent` を持つ）は、オン・オフとも記録しない。
- 書く形: 1行目 `[HH:MM-HH:MM]`（同じ分なら `[HH:MM]`）、2行目 `🤖 **<Claude Code|Codex>: <名前>**（<プロジェクト>・作業 <N>分）`（名前とプロジェクトが同じならプロジェクトを省く・1分未満は「作業 1分未満」）、その下に `> [!info]- 詳細`・`> **🗣️ 頼んだこと:**`・依頼（最大3件・それより多いと3件目の後ろに「（ほか N件）」）・`>`・`> **🤖 <Claude|Codex> の処理・出力:**`・返事（最大3行）・`> - 📦 成果物: コミット N件（…）`（なければ書かない）。詳細は、`[!info]` の行と空の `>` を除いて8行まで。超えたら返事を後ろから減らす（1行は残す）。依頼は1件以上残す。
- 写す文: 1つ100文字まで（長いと「…」）。改行・タブ・制御文字は空白1つ。`#` → `＃`。`[[A|B]]` → B、`[[A]]` → A、`![[A]]` は外す。`**` と `` ` `` は外す。返事の初めの行は、空行・`#` で始まる行・```` ``` ````・`|` で始まる行・区切り線を飛ばし、行頭の `- `・`* `・`> `・`1. ` を外す。
- 会話の記録は末尾から最大20000行まで読む（Claude Code はツールの記録が多く、2000行では1時間の作業に足りないため。設計書の2000行をこの計画で直す）。
- コミット: `git -C <作業フォルダー> log --since=<始まり> --until=<終わり> --format=%h %s -n 6`、5秒まで。5件まで書き、6件目があれば「ほか」。
- C# 5 の構文だけ。テストを先に書く。コミットの作者は noreply（このリポジトリの設定）。末尾は `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`。テストやコメントに個人の情報（保管庫の場所・健康の記録・プロジェクト名）を書かない。

## Review Focus

1. 自動の見回りが8〜12分おきに動く Codex のスレッドの中の、利用者の依頼 → 自動のターンにまぎれても10分たてば書かれる（自動のターンでつながらない）。Task 6 のテスト。
2. 作業中のまま10分を超える長いターン（前の作業から10分以内に頼んだもの）→ 前の作業と1つにまとまり、途中で書き出されない。Task 6 のテスト。
3. 会話の記録が壊れている・途中まで書かれている・形が違う → その項目を飛ばし、書けるものだけで記録を書く（例外で書くのをやめない）。Task 2 のテスト。
4. 返事が Markdown だらけ（見出し・表・コード・タグ・埋め込み）→ 日誌の見出し・タグ・リンク・埋め込みが増えない。Task 3 のテスト。
5. 日付をまたぐ作業 → 始まった日の日誌に、始まりの時刻の位置で入る。Task 5・Task 7 のテスト。

---

### Task 1: 設定と、Codex のヘルパーのスレッドの見分け

**Files:**
- Modify: `src/NowDoing.cs`（`NowDoingSettings.AiDoneDetail`）、`src/Waiting.cs`（`CodexThread.Path`・`CodexThread.Helper`・`WaitingItem.RecordPath`・`WaitingItem.Helper`）、`src/AiDone.cs`（`AiDoneLog.Observe` がヘルパーを除く）
- Test: `tests/AiDoneTests.cs`、`tests/WaitingTests.cs`、`tests/NowDoingActionTests.cs`

**Interfaces:**
- Produces: `NowDoingSettings.AiDoneDetail`（`now_doing_ai_done_detail=on` で true）。`CodexThread.Path`（記録のファイルのフルパス）・`CodexThread.Helper`（`session_meta.payload.source` が辞書で `subagent` を持つ）。`WaitingItem.RecordPath`（Codex は記録のファイル、Claude は null）・`WaitingItem.Helper`。共有データ（`UsageSnapshot`）には入れない。

- [ ] **Step 1: 失敗するテストを書く**
  - `Check("now_doing_ai_done_detail=on is read")`: `Parse("now_doing_ai_done_detail=on\n").AiDoneDetail` は true、`Parse("")` と `=yes` は false。
  - `Check("Codex helper threads are told apart from the user's threads")`: `CodexSessions.Classify` に1行目 `{"type":"session_meta","payload":{"id":"t","cwd":"C:\\p","source":{"subagent":{"other":"guardian"}}}}` → `Helper` が true。`"source":"vscode"` は false で、`Source=="vscode"` は今のまま。
  - `Check("The waiting list keeps the Codex record file and whether it is a helper")`: `WaitingTracker.Observe` に `Path`・`Helper` 付きの Codex スレッドを作業中→完了で渡すと、完了の項目の `RecordPath`・`Helper` がそのまま。
  - `Check("AI done lines skip Codex helper threads")`: `AiDoneLog.Observe` に `Helper=true` の完了だけを渡すと `Due` は null。
- [ ] **Step 2: 失敗を確かめる** — `.\build.ps1 -TestsOnly -OutputDirectory build\v2` がコンパイルで失敗する。
- [ ] **Step 3: 書く**（`CodexSessions.Read` が `Path=file.FullName` を入れる。`WaitingTracker.Add` に `recordPath`・`helper` を渡す）
- [ ] **Step 4: 通ることを確かめる** — 同じ命令で `RESULT … 0 failed`。
- [ ] **Step 5: コミット**（設計書とこの計画も含める） `git commit -m "Read now_doing_ai_done_detail and leave Codex helper threads out of AI done lines"`

### Task 2: 会話の記録からターンを読む

**Files:**
- Create: `src/AiTurns.cs`
- Test: `tests/AiDoneDetailTests.cs`（新規。`build.ps1` の一覧に足し、Main で `RunAiDoneDetailTests()` を `RunAiDoneTests()` の後に呼ぶ）

**Interfaces:**
- Produces:
  - `internal sealed class AiTurn { public DateTimeOffset Start; public DateTimeOffset? End; public bool ByUser; public List<string> Requests = new List<string>(); public string Reply; public double WorkMs; }` — `End` が null なら作業中。`WorkMs` は AI が動いていた時間。
  - `internal static class ClaudeTranscript { public static List<AiTurn> Turns(IEnumerable<string> linesNewestFirst, DateTimeOffset after, int maxLines = 20000); }`
    - 古い順に返す。`type=="user"` で `turnOrigin`（または `promptSource`）を持つ記録がターンの始まり。`ByUser` は `turnOrigin=="human"`。依頼はその記録の文（`content` が文字列ならそれ、配列なら `type=="text"` の `text` をつなぐ）で、`<` で始まるものは入れない。
    - そのターンの終わり＝次のターンの始まりより前の、最後の記録の `timestamp`。最後のターンで、最後の記録が `assistant` の `text` でなければ `End=null`（作業中）。
    - 返事＝そのターンの最後の `assistant`（`isSidechain` が偽）の `text` の部分。`isSidechain` が真の記録は全部無視。`WorkMs` = 終わり − 始まり。
    - `after` 以前に始まったターンの始まりの記録まで読んだら止める（`End>after` のターンだけ返す）。壊れた行・形の違う行は飛ばす。
  - `internal static class CodexRollout { public static List<AiTurn> Turns(IEnumerable<string> linesNewestFirst, DateTimeOffset after, int maxLines = 20000); }`
    - `event_msg`/`task_started` がターンの始まり（`started_at`）。`ByUser` は `payload.turn_attribution.turn_trigger` が `composer` か `send_user_message_async_question`。
    - 依頼＝そのターンの `response_item`/`message` で `role=="user"` の `content` の `input_text` の文（`<` で始まるものは入れない）。
    - `event_msg`/`task_complete` で終わり（`completed_at`）・返事（`last_agent_message`）・`WorkMs`（`duration_ms`）。`task_complete` がなければ `End=null`。

- [ ] **Step 1: 失敗するテストを書く**（JSON の行はテストの中で作る。中身は架空の文）
  - `Check("Claude Code turns: the user's requests, the last reply, and the end")`: 古い順に [human の依頼「表を直して」(10:00) → assistant thinking → assistant tool_use → user tool_result → assistant text「直しました」(10:05) → peer の記録 (10:06) → assistant text (10:07) → human の依頼「次」(10:20) → assistant tool_use (10:21)] を新しい順で渡す → 3ターン。1つ目: `ByUser`・依頼 ["表を直して"]・返事「直しました」・`End==10:05`・`WorkMs==5分`。2つ目: `ByUser==false`。3つ目: `ByUser`・`End==null`。
  - `Check("Claude Code turns skip helper agents, command text and broken lines")`: `isSidechain:true` の assistant text は返事にならない。依頼「<command-name>/x</command-name>」は入らない。`{` だけの行・空の行があっても例外を出さない。
  - `Check("Claude Code turns stop at the given time")`: `after=10:10` なら 10:05 に終わったターンは返さない。
  - `Check("Codex turns: user-started ones, the request and the final reply")`: [task_started(composer, 10:00) → message role user「動かして」 → message role user「<environment_context>…」 → task_complete(10:04, last_agent_message「動きました」, duration_ms 240000) → task_started(automation_heartbeat_scheduled, 10:10) → task_complete(10:11)] → 2ターン。1つ目 `ByUser`・依頼 ["動かして"]・返事「動きました」・`WorkMs==240000`。2つ目 `ByUser==false`。`task_complete` のないターンは `End==null`。
- [ ] **Step 2: 失敗を確かめる**（コンパイル失敗）
- [ ] **Step 3: `src/AiTurns.cs` を書く**（`Json.Parse`・`Json.Date` を使う）
- [ ] **Step 4: 通ることを確かめる**
- [ ] **Step 5: コミット** `git commit -m "Read requests and replies from Claude Code and Codex records"`

### Task 3: 写す文の整え方と、記録の形

**Files:**
- Create: `src/AiDoneDetail.cs`（このタスクでは `AiDoneText` と `AiDoneFormat`）
- Test: `tests/AiDoneDetailTests.cs`

**Interfaces:**
- Consumes: `AiTurn`（Task 2）。
- Produces:
  - `internal static class AiDoneText { public static string Clean(string text, int max = 100); public static List<string> ReplyLines(string reply, int max = 3); }`
  - `internal sealed class AiStretch { public string Key, Service, Place, Project, Cwd; public List<AiTurn> Turns = new List<AiTurn>(); public DateTimeOffset Start { get; } public DateTimeOffset End { get; } public double WorkMs { get; } }`（Start＝最初のターンの始まり、End＝最後のターンの終わり、WorkMs＝合計）
  - `internal static class AiDoneFormat { public static List<string> Entry(AiStretch stretch, IList<string> commits, bool moreCommits); }` — 日誌に入れる行（1行目が時刻の行）。

- [ ] **Step 1: 失敗するテストを書く**
  - `Check("Copied text cannot become tags, links, embeds or headings")`: `Clean("## 直し #tag [[A|B]] [[C]] ![[D.png]] **太字** `x`")=="直し ＃tag B C 太字 x"`（行頭の `## ` は外す）。改行とタブは空白1つ。101文字は100文字＋「…」に（サロゲートの途中で切らない）。
  - `Check("The first lines of a reply skip blank lines, headings, code, tables and rules")`: 返事「## まとめ\n\n- 表を直しました\n```\ncode\n```\n| a | b |\n---\n1. テストが通りました\n> 注意: なし\n4行目」→ `ReplyLines(...,3)==["表を直しました","テストが通りました","注意: なし"]`。
  - `Check("An entry has the time range, the title and the details")`: 10:00〜10:15・作業 9分・Claude Code・名前「表の直し」・プロジェクト「App」・依頼2件・返事2行・コミット2件 → 行が `["[10:00-10:15]","🤖 **Claude Code: 表の直し**（App・作業 9分）","> [!info]- 詳細","> **🗣️ 頼んだこと:**","> - 一つ目","> - 二つ目",">","> **🤖 Claude の処理・出力:**","> - 返事1","> - 返事2","> - 📦 成果物: コミット 2件（`abc1234` 一つ目 ／ `def5678` 二つ目）"]`。
  - `Check("Entry details: same minute, same name, under a minute, more requests, more commits, no commits")`: 同じ分 → `[10:00]`。名前＝プロジェクト → `（作業 9分）`。30秒 → `作業 1分未満`。依頼5件 → 3件目が「三つ目（ほか 2件）」。コミット5件で `moreCommits` → 「コミット 5件（… ／ ほか）」（設計書の「6件目があれば ほか」）。コミットなし → 📦 の行なし。Codex → `🤖 **Codex: …**` と `🤖 Codex の処理・出力`。
  - `Check("Entry details are at most eight lines, cutting the reply first")`: 依頼3件・返事3行・コミットあり → 返事が2行になる（見出し2＋3＋2＋📦1＝8）。依頼が空（読めなかった）なら「頼んだこと」の見出しごと書かない。
- [ ] **Step 2〜4:** 失敗 → `src/AiDoneDetail.cs` に書く → 通る（Task 2 と同じ命令）
- [ ] **Step 5: コミット** `git commit -m "Format AI done entries the way the diary's log rules do"`

### Task 4: その間のコミットを読む

**Files:**
- Modify: `src/AiDoneDetail.cs`（`GitCommits`）
- Test: `tests/AiDoneDetailTests.cs`

**Interfaces:**
- Consumes: `NowDoingScript.RunAsync`・`NowDoingScriptLimits`（既存）。
- Produces: `internal static class GitCommits { public static List<string> Between(string cwd, DateTimeOffset start, DateTimeOffset end); }` — 新しい順に最大6件の「`<短いハッシュ> <題>`」。Git でないフォルダー・git がない・失敗・5秒超えは空。

- [ ] **Step 1: 失敗するテストを書く**
  - `Check("Commits made in the folder during the work are found")`: 一時フォルダーで `git init`・名前とメールをそのフォルダーだけに設定・`GIT_COMMITTER_DATE`/`--date` で 09:00・10:05・10:10・11:00 のコミットを作る → `Between(folder, 10:00, 10:30)` が2件（10:10 が先）で、各要素が `^[0-9a-f]{7,} .+$`。
  - `Check("A folder that is not in Git gives no commits")`: 空の一時フォルダー → 空。存在しないフォルダー → 空。
- [ ] **Step 2〜4:** 失敗 → 書く（`NowDoingScript.RunAsync("git", new[]{"-C",cwd,"log","--since="+start.ToString("o"),"--until="+end.ToString("o"),"--format=%h %s","-n","6"}, cwd, new NowDoingScriptLimits{TimeoutMs=5000,MaxBytes=16384,MaxLines=10}, CancellationToken.None).Result`。`.git` がフォルダーかファイルで在るときだけ動かす）→ 通る
- [ ] **Step 5: コミット** `git commit -m "Find the commits made during a piece of AI work"`

### Task 5: 日中ログに複数行の記録を差し込む

**Files:**
- Modify: `src/NowDoing.cs`
- Test: `tests/NowDoingTests.cs`

**Interfaces:**
- Produces: `NowDoingNote.InsertEntry(string content, DateTime time, IList<string> lines, NowDoingSettings settings, out NowDoingFailure failure)` と `NowDoingNote.WriteEntry(NowDoingSettings settings, DateTime time, IList<string> lines, string backupPath, Action<int> sleep = null)`。位置は今の `Insert` と同じ決め方（`time` より後に始まる最初の記録の前）。今の `Insert`・`Write` はこれを使う形にする（結果は変わらない）。

- [ ] **Step 1: 失敗するテストを書く**
  - `Check("A multi-line entry goes where a two-line record would")`: 既存の `note` に `InsertEntry(note, 10:40, ["[10:40-10:55]","🤖 **Claude Code: x**（作業 9分）","> [!info]- 詳細","> - y"])` → `[11:05]` の前に4行と空行1つ。CRLF の日誌は CRLF で入る。今の `Insert` のテストはすべてそのまま通る。
  - `Check("A multi-line entry is written to the day it started")`: `WriteEntry` を 23:50 の時刻で呼ぶと、その日の日誌（`{yyyy-MM-dd}` がその日）に入る。
- [ ] **Step 2〜4:** 失敗 → 書く → 通る
- [ ] **Step 5: コミット** `git commit -m "Insert a multi-line entry into the 日中ログ"`

### Task 6: ひと続きの作業のまとめと書く時期

**Files:**
- Modify: `src/AiDoneDetail.cs`（`AiDoneDetailLog`）
- Test: `tests/AiDoneDetailTests.cs`

**Interfaces:**
- Consumes: `AiTurn`・`AiStretch`（Task 2・3）、`AiDoneLog.Excluded`（既存）、`WaitingItem.Helper`（Task 1）。
- Produces: `internal sealed class AiDoneDetailLog { public static readonly TimeSpan Quiet = TimeSpan.FromMinutes(10); public AiDoneDetailLog(string[] exclude, DateTimeOffset since, Func<WaitingItem, DateTimeOffset, List<AiTurn>> readTurns); public void Observe(WaitingItem[] items, DateTimeOffset now); public List<AiStretch> Due(DateTimeOffset now); public List<AiStretch> All(); }`
  - `Observe`: `Reason==Finished` で、ヘルパーでも除くフォルダーでもない項目を、キーごとに「待っている」に入れる（最新の項目で名前・場所を上書き）。
  - `Due`: 待っているキーごとに、前の読みから1分以上たっていれば `readTurns(item, 書いた終わり)` で読む（`since` が最初の「書いた終わり」）。利用者のターンだけを見て、作業中の利用者のターンがあれば書かない。最後の利用者のターンの終わりから10分たっていなければ書かない。書くときは、利用者のターンを「前の終わりから次の始まりまでが10分以内」でつないだまとまりを全部返し、「書いた終わり」を読んだターン全部の最後の終わりにして、待っているから外す。利用者のターンが1つもなければ何も返さず、「書いた終わり」だけ進める。
  - `All`: トレイアプリが終わるとき用。待っている全部を、10分も作業中も見ずに（終わったターンだけで）まとめて返す。
  - 1回の `readTurns` が例外を出したら、そのキーはその回は飛ばす。
  - 返す `AiStretch` の `Service`・`Place`・`Cwd` は最新の項目から、`Project` は `WaitingNames.Place(null, item.Cwd)`（フォルダーから出したプロジェクト名）。

- [ ] **Step 1: 失敗するテストを書く**（`readTurns` はテストの中の偽物。時刻は `At(h,m)` を進める）
  - `Check("A piece of work is written ten minutes after the user's last turn ended")`: 10:00〜10:05 の利用者のターン → `Due(10:14)` は空、`Due(10:15)` は1つ（Start 10:00・End 10:05）、そのあとの `Due` は空。
  - `Check("Turns ten minutes apart or closer are one piece of work; farther apart are two")`: 10:00〜10:05 と 10:15〜10:20 → 1つ。10:00〜10:05 と 10:16〜10:20 → 2つ。
  - `Check("Turns the AI started by itself neither count nor keep the work going")`: 利用者 10:00〜10:05、自動 10:12〜10:13・10:22〜10:23 → `Due(10:15)` で1つ（自動のターンは入らない）。自動のターンだけなら何も返さない。（Review Focus 1）
  - `Check("A long turn asked within ten minutes keeps the work open")`: 利用者 10:00〜10:05、利用者 10:10〜作業中 → `Due(10:30)` は空。10:40 に終わった後の `Due(10:50)` で1つ（10:00〜10:40）。（Review Focus 2）
  - `Check("Helper threads and excluded folders are never read")`: `Helper` の項目と除くフォルダーの項目では `readTurns` が呼ばれない。
  - `Check("When the app ends, waiting work is given at once")`: 10:00〜10:05 の直後に `All()` → 1つ。
  - `Check("Records are read at most once a minute per session")`: 10秒おきに `Due` を呼んでも、1分に1回しか `readTurns` が呼ばれない。
- [ ] **Step 2〜4:** 失敗 → 書く → 通る
- [ ] **Step 5: コミット** `git commit -m "Group a session's turns into pieces of work and write them ten minutes after"`

### Task 7: トレイアプリにつなぐ（終わるときも書く）

**Files:**
- Modify: `src/TrayContext.cs`、`src/AiDoneDetail.cs`（`AiDoneDetailLog` を読む関数を作る `AiTurnReader`）
- Test: `tests/UiTests.cs`、`tests/AiDoneDetailTests.cs`

**Interfaces:**
- Consumes: Task 1〜6 のすべて。
- Produces:
  - `internal static class AiTurnReader { public static List<AiTurn> Read(WaitingItem item, DateTimeOffset after, string profile); public static string ClaudeTranscript(string profile, string sessionId); }` — Claude は `<profile>\.claude\projects\*\<セッションID>.jsonl`（直下のフォルダーだけを探す）、Codex は `item.RecordPath`。ファイルがなければ空。
  - `TrayContext`: `nowDoing.AiDone && nowDoing.AiDoneDetail` なら `AiDoneDetailLog`（`since`＝起動の時刻・読む関数＝`AiTurnReader.Read`）を作り、`AiDoneLog` は作らない。`ReadWaiting` で `Observe`→`Due`。返ったまとまりごとに `AiDoneFormat.Entry`（コミットは `GitCommits.Between`）→ 今の「日誌の静けさを待つ」経路で `WriteEntry`（時刻＝まとまりの Start の地方時の分）。読む・コミット・書くは UI のスレッドの外（`Task.Run`）。
  - 終わるとき: `ExitThreadCore` と `SystemEvents.SessionEnding` で `All()` を、静けさを待たずに書く。全体で5秒まで（`Task.Run(...).Wait(5000)`）。2回書かないように一度だけ。

- [ ] **Step 1: 失敗するテストを書く**
  - `Check("Claude Code records are found by the session id")`: 一時フォルダーを profile にして `.claude\projects\A\s1.jsonl` を置く → `ClaudeTranscript(profile,"s1")` がそのパス、`"s2"` は null。
  - UiTests `"With the detail setting the tray app uses the detailed AI done lines"`: `nowDoing:Parse("now_doing_ai_done=on\nnow_doing_ai_done_detail=on\n")` → `aiDoneDetail` が null でなく `aiDone` は null。`now_doing_ai_done=on` だけ → 逆。どちらも off → 両方 null（今のテストを広げる）。
- [ ] **Step 2〜4:** 失敗 → 書く → 通る（`.\build.ps1 -NoPublish -OutputDirectory build\v2`）
- [ ] **Step 5: コミット** `git commit -m "Write detailed AI done entries from the tray app, also when it ends"`

### Task 8: 案内・プライバシー・設計書の直し、全部の確認

**Files:**
- Modify: `docs/streamdeck-guide.md`（「AI の完了を日中ログに残す」・設定の表・「使わない機能は動きません」の表）、`docs/privacy.md`（「AI の完了の記録」）、`docs/superpowers/specs/2026-11-24-ai-done-detail-design.md`（読む行数 2000 → 20000）

- [ ] **Step 1:** 案内に、オンのときの形（設計書の例）・10分・終わるときはすぐ書く・自動の作業とヘルパーのスレッドは書かない・作業 N分の意味を書く。設定の表に `now_doing_ai_done_detail` を足す。
- [ ] **Step 2:** privacy.md に、オンのときだけ会話の記録（依頼と最後の返事）を読み、初めの行だけを日誌に書くこと・ほかに残さず送らないこと・作業フォルダーで `git log` を読むことを書く。
- [ ] **Step 3: 全部を確かめる** — `.\build.ps1 -NoPublish -OutputDirectory build\v2` と、Obsidian・Stream Deck の node のテスト（前の計画の Task 9 と同じ命令）。Expected: すべて失敗0。
- [ ] **Step 4: コミット** `git commit -m "Document the detailed AI done entries"`

### Task 9: 実機での確認（利用者と一緒に）

- [ ] **Step 1:** main を早送りして exe を入れ替え、settings.ini に `now_doing_ai_done_detail=on` を足してトレイアプリを起動し直す（利用者の了承のうえ）。
- [ ] **Step 2:** Claude Code で短い作業を1つ頼んで終わらせ、約10分後に日中ログに設計書の形の記録が入ることを確かめる（頼んだこと・返事・コミット）。Codex の見回りのスレッドとレビューのスレッドが書かれないことも見る。
- [ ] **Step 3:** 設計書の「未確認」の3項目に結果を書き、コミットする。push は利用者に聞いてから。
