# 対応待ちの一覧 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Claude Code と Codex の対応待ち・完了を、待ち時間の長い順にトレイと Stream Deck の「対応待ち」キーに並べ、待ちが続いたら Windows 通知で知らせ直す。Grok Bot は通知を読めるかの確認までをこの計画に含め、読み取りの実装（必要ならパッケージ化）は確認結果をもとに別の計画にする。

**Architecture:** トレイアプリが5秒ごとに `~/.claude/sessions/*.json` と `~/.codex/sessions/**/*.jsonl` を読み取り専用で読み、`WaitingTracker` が一覧を作る。一覧はトレイの見出しの下に出し、共有データ（名前付きパイプ）の `waiting` で Stream Deck プラグインに渡す。アプリはリンク（`claude://` など）で開く。

**Tech Stack:** C# 5 / .NET Framework 4.8 / WinForms（`build.ps1` の csc）、依存なしの Node.js（Stream Deck プラグイン、`obsidian/src/usage.js` の解析を共有）。

**Spec:** `docs/superpowers/specs/2026-11-20-waiting-list-design.md`

## Global Constraints

- 各AIの設定ファイル・記録には書き込まない。読むのは状態・時刻・名前・フォルダー名だけで、会話の内容は読まない・保存しない・表示しない。
- 新しい通信はしない。Stream Deck プラグインはこれまでどおりどこにも通信しない。
- 開けるリンクは `claude://`・`codex://`・`vscode://`・`grokbot://` の4つだけ（完全一致）。
- 読み取りの上限：Claude のファイルは1つ64 KiB・最大300ファイル。Codex は最後の書き込みが12時間以内のファイルだけ、1ファイルにつき末尾から最大2000行。
- 時間の値：知らせ直しは「3分後、その後10分ごと」（初期）／「1分後、その後5分ごと」／「通知しない」。待ちが60分を超えたら知らせ直さない。完了は30分で一覧から消す。Stream Deck のキーは待ち10分以上で赤。
- 共有データは schemaVersion 2 のまま、トップレベルに `waiting` を足す。項目は最大8、`place` は40文字まで・制御文字を除く。
- プラグインの版は 1.3.0.0。共有データの読み取り間隔は5秒。
- C# 5 の構文だけ（文字列補間・式本体メンバーなし）。新しい依存なし。テストを先に書く。コミットの末尾は `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`。
- 画面の言葉：対応待ち・質問・確認・完了・「対応待ちなし」・「ほか N件」・「◯◯の記録を読めません」。

## Review Focus

1. 書き込み途中・壊れた・JSONでない Claude のセッションファイル → そのファイルだけ飛ばし、ほかのセッションは出る（Task 2 のテスト）。
2. 書き込み途中で最後の行が途切れた Codex の記録 → 例外を出さず、途切れる前の行で判定する（Task 3 のテスト）。
3. `statusUpdatedAt` や `timestamp` がPCの時計より未来 → 待ち時間は0分として扱い、負の値を出さない（Task 4 のテスト）。
4. セッション名や作業フォルダー名に制御文字・長い文字列 → 40文字までに切り、制御文字を除いて表示・共有する（Task 2・Task 5 のテスト）。
5. 共有データの `waiting` に知らない `app` や `reason` が入っている → プラグインはその項目を捨て、キーから決まった4つ以外のリンクを開かない（Task 6 のテスト）。

---

### Task 1: 現物の確認（利用者と一緒に）

**Files:** なし（結果は spec の「未確認」の行に書き足してコミット）

- [ ] **Step 1:** `scratchpad\watch_sessions.py` で `~/.claude/sessions` の状態変化を記録しながら、利用者に Claude デスクトップアプリで操作の許可の画面を1回出してもらう（許可を求める設定のセッションでコマンドの実行を頼む）。Expected: `status` が `waiting` になり、`waitingFor` の値が分かる。VS Code で Claude Code を使えるなら同じことをして `entrypoint` の値を記録する。
- [ ] **Step 2:** 利用者の前で `Start-Process 'claude://'`、`'codex://'`、`'grokbot://'`、`'vscode://'` を順に実行し、各アプリが前に出るかを見てもらう。Expected: 4つとも前に出る。出ないものがあれば止めて利用者に相談する（spec の「アプリを開く」）。
- [ ] **Step 3:** 結果を spec の Claude Code・アプリを開くの節に書き足し、コミットする。

### Task 2: Claude Code のセッションの読み取りと名前

**Files:**
- Create: `src/Waiting.cs`
- Test: `tests/WaitingTests.cs`（`build.ps1` のテスト一覧に追加、`tests/Tests.cs` の Main で `RunWaitingTests()` を `RunStatusTests()` の後に呼ぶ）

**Interfaces:**
- Produces:
  - `public sealed class ClaudeSession { public string SessionId, Status, WaitingFor, Cwd, Name, Entrypoint; public int Pid; public DateTimeOffset? StatusUpdatedAt; }`
  - `public static class ClaudeSessions { public static ClaudeSession Parse(string json); public static ClaudeSession[] Read(string profile, Func<int,bool> isClaudeProcess, out bool readable); }` — `Parse` は形が合わなければ null。`Read` は `*.json` だけ、64 KiB超・読めないファイルは飛ばす、最大300ファイル、`isClaudeProcess(pid)` が false のものを除く。フォルダーがない・一覧を取れないときは `readable=false`。
  - `public static class WaitingNames { public static string Place(string name, string cwd); public static string Clean(string text); public static string ClaudeApp(string entrypoint); }` — `Place` はセッション名を優先、なければ `cwd` から（`…\<project>\.claude\worktrees\<x>` なら `<project>`、それ以外は最後のフォルダー名）。`Clean` は制御文字を除き、空白を1つにまとめ、40文字まで。`ClaudeApp` は `entrypoint` に `vscode` を含めば `"vscode"`、それ以外は `"claude"`。
  - 既定の `isClaudeProcess`：`Process.GetProcessById(pid)` が存在し、`ProcessName` が大文字小文字を区別せず `claude`。

- [ ] **Step 1: 失敗するテストを書く**（`tests/WaitingTests.cs`）
  - `Check("Claude session files give status, reason and start time")`：`ClaudeSessions.Parse(J("{'pid':12,'sessionId':'s1','status':'waiting','waitingFor':'input needed','statusUpdatedAt':1763600000000,'cwd':'C:\\\\src\\\\Proj','name':'Fix login','entrypoint':'claude-desktop'}"))` の各項目が一致し、`StatusUpdatedAt == DateTimeOffset.FromUnixTimeMilliseconds(1763600000000)`。
  - `Check("Broken or partial Claude session files are skipped")`：一時フォルダーに正しいファイル1つ、`{"pid":` だけのファイル、`not json`、65 KiB のファイルを置き、`Read(temp, pid=>true, out ok)` が1件だけ返し `ok` が true。`isClaudeProcess` が false の pid は除かれる。フォルダーがないと `ok` が false。
  - `Check("Places prefer the session name, then the project of a worktree")`：`Place("Fix login", ...)=="Fix login"`、`Place("", @"C:\src\Project\.claude\worktrees\api-x")=="Project"`、`Place(null, @"C:\src\web-app")=="web-app"`、`Place(null,null)==""`。
  - `Check("Place text is cleaned and cut to 40 characters")`：`Clean("a\u0007b  \n c" + new string('x',60))` に制御文字がなく、連続空白が1つ、長さ40以下。
  - `Check("Claude sessions open the desktop app unless they run in VS Code")`：`ClaudeApp("claude-desktop")=="claude"`、`ClaudeApp("claude-vscode")=="vscode"`、`ClaudeApp(null)=="claude"`。
- [ ] **Step 2:** `powershell -File build.ps1 -TestsOnly` を実行。Expected: コンパイル失敗（`ClaudeSessions` がない）。
- [ ] **Step 3:** `src/Waiting.cs` に上の型を実装する。JSON は既存の `Json.Parse`、読み取りは `FileShare.ReadWrite | FileShare.Delete` で開く。
- [ ] **Step 4:** `build.ps1 -TestsOnly`。Expected: `RESULT … 0 failed`。
- [ ] **Step 5:** コミット。

### Task 3: Codex の記録の読み取り

**Files:** Modify `src/Waiting.cs`、Test `tests/WaitingTests.cs`

**Interfaces:**
- Produces:
  - `public enum CodexState { Unknown, Working, Question, Finished }`
  - `public sealed class CodexThread { public string Id, Cwd, Originator, Source; public CodexState State; public DateTimeOffset? Since; }`
  - `public static class CodexSessions { public static CodexThread Classify(string firstLine, IEnumerable<string> linesNewestFirst); public static CodexThread[] Read(string profile, DateTimeOffset now, Func<bool> appRunning, out bool readable); public static string App(CodexThread thread); }`
  - `Classify`：`firstLine` の `session_meta` から `Id`（`payload.id`）・`Cwd`・`Originator`・`Source`（文字列のときだけ）。末尾から最大2000行を見て、最初に当たったもので決める：`function_call_output` の `call_id` は「答え済み」として覚える／`function_call` で `name` が `request_user_input_async` かつ答え済みでない → `Question`（`Since` はその行の `timestamp`）／`event_msg` の `task_complete` → `Finished`／`task_started` → `Working`。JSONでない行は飛ばす。
  - `Read`：`appRunning()` が false なら空配列。`sessions` 以下で最後の書き込みが12時間以内の `*.jsonl` だけ。ファイルの更新時刻が前回と同じならキャッシュを返す（静的な辞書）。フォルダーがない・読めないときは `readable=false`。
  - `App`：`Source == "vscode"` なら `"vscode"`、それ以外は `"codex"`。
  - 既定の `appRunning`：大文字小文字を区別せず `ChatGPT`・`Codex`・`Code` のどれかのプロセスがある。

- [ ] **Step 1: 失敗するテストを書く**
  - `Check("A pending Codex question is a question; an answered one is not")`：meta 行＋`task_started`＋`function_call request_user_input_async (call_id c1)` → `Question`、`Since` はその行の時刻。さらに `function_call_output c1` と `task_complete` を足すと `Finished`。
  - `Check("Codex working, finished and unknown threads")`：最後が `task_started` → `Working`、イベントがない → `Unknown`。
  - `Check("A cut-off last Codex line does not break classification")`：最後の行が `{"timestamp":"2026-` で途切れていても、その前の `task_complete` で `Finished`。
  - `Check("Codex meta gives folder and app")`：`source` が `vscode` → `App=="vscode"`、`source` がオブジェクト → `Source==null`・`App=="codex"`、`Cwd` が取れる。
  - `Check("Codex threads are read only while a Codex app runs and only from recent files")`：一時フォルダーに新しいファイルと13時間前に更新したファイルを置き、`appRunning=()=>false` で空、`true` で新しい方だけ。
- [ ] **Step 2:** `build.ps1 -TestsOnly`。Expected: コンパイル失敗。
- [ ] **Step 3:** 実装。末尾からの読み取りは既存の `ReadOnlyFiles.ReverseLines`、先頭行は同じ共有モードで1行だけ読む（1 MiB を超える行は諦めて `Unknown`）。
- [ ] **Step 4:** `build.ps1 -TestsOnly`。Expected: 全件成功。
- [ ] **Step 5:** コミット。

### Task 4: 一覧の組み立て・知らせ直し・設定

**Files:** Modify `src/Waiting.cs`、`src/Theme.cs`、Test `tests/WaitingTests.cs`

**Interfaces:**
- Consumes: Task 2・3 の型。
- Produces:
  - `public enum WaitReason { Question, Confirm, Finished }`
  - `public sealed class WaitingItem { public string Key, Service, Place, App; public WaitReason Reason; public DateTimeOffset Since; }`（`Service` は `"Claude"`・`"Codex"`）
  - `public sealed class WaitingUnreadable { public string Service, Reason; }`
  - `public sealed class WaitingList { public WaitingItem[] Items = new WaitingItem[0]; public WaitingUnreadable[] Unreadable = new WaitingUnreadable[0]; }`
  - `public sealed class WaitingTracker { public WaitingList Observe(ClaudeSession[] claude, bool claudeReadable, CodexThread[] codex, bool codexReadable, DateTimeOffset now); }`
    - Claude：`waiting` → `waitingFor=="input needed"` なら `Question`、それ以外は `Confirm`、`Since` は `StatusUpdatedAt`（なければ初めて見た時刻）。`idle` で、このトラッカーが前回 `busy` を見ていた → `Finished`。最初に見た `idle` は出さない。
    - Codex：`Question` → `Question`。`Finished` は、このトラッカーが同じスレッドの `Working` か `Question` を見ていたときだけ。
    - `Since` が `now` より未来なら `now` として扱う。完了は `Since` から30分で消す。
    - 並べ方：対応待ち（`Question`・`Confirm`）を `Since` の古い順、その後に完了を新しい順。
    - 読めないとき：`Unreadable` に `{Service:"Claude", Reason:"記録を読めません"}` など。
  - `public enum WaitingNoticeMode { Standard, Frequent, Off }`
  - `public sealed class WaitingReminders { public string[] Due(WaitingItem[] items, DateTimeOffset now, WaitingNoticeMode mode); }` — Standard は3分後・その後10分ごと、Frequent は1分後・その後5分ごと、Off は何も返さない。待ち60分超・完了は返さない。同じ項目に同じ回を2度出さない。文：`"{Service}（{Place}）が{質問の答え|確認}を{N}分待っています"`、`Place` が空なら `"{Service}が…"`。
  - `ThemeSettings`：`public WaitingNoticeMode LoadWaiting()`（`waiting=frequent`／`waiting=off`、それ以外は Standard）、`public bool SaveWaiting(WaitingNoticeMode mode)`。保存はテーマ・通知・対応待ちの3行を書く（既存の2つの Save も3行を書く）。

- [ ] **Step 1: 失敗するテストを書く**
  - `Check("Waiting items come first, oldest first; finished items follow, newest first")`
  - `Check("Claude idle counts as finished only after the tracker saw it busy")`：1回目 `idle` → 出ない、`busy` → 出ない、`idle` → `Finished`。
  - `Check("Codex finished threads appear only after they were seen working")`
  - `Check("Finished items disappear after 30 minutes")`
  - `Check("A start time in the future counts as now")`：`Since == now`。
  - `Check("Unreadable sources are named, never shown as nothing waiting")`
  - `Check("Reminders: 3 minutes, then every 10, never past 60 or for finished items")`：経過2分→なし、3分→1件、5分→なし、13分→1件、61分→なし、`Off`→なし、`Frequent` は1分・6分。
  - `Check("Reminder text names the service, place and reason")`：`"Claude（AIUsageManager）が質問の答えを3分待っています"`、`Place` 空で `"Codexが確認を3分待っています"`。
  - `Check("The waiting notice choice is saved with the other settings")`：`SaveWaiting(Off)` 後に `Load()`・`LoadNotifications()` が変わらず `LoadWaiting()==Off`。
- [ ] **Step 2:** `build.ps1 -TestsOnly`。Expected: コンパイル失敗。
- [ ] **Step 3:** 実装。
- [ ] **Step 4:** `build.ps1 -TestsOnly`。Expected: 全件成功（既存のテーマ・通知の保存テストも通る）。
- [ ] **Step 5:** コミット。

### Task 5: 共有データ・トレイの表示・アプリを開く

**Files:** Modify `src/Snapshot.cs`、`src/TrayContext.cs`、`src/PopupForm.cs`、`src/Waiting.cs`、Test `tests/SnapshotTests.cs`・`tests/UiTests.cs`・`tests/WaitingTests.cs`

**Interfaces:**
- Consumes: Task 4 の `WaitingList`・`WaitingTracker`・`WaitingReminders`・`ThemeSettings.LoadWaiting/SaveWaiting`。
- Produces:
  - `UsageSnapshot.Create(ServiceUsage[] values, DateTimeOffset now, bool refreshing, StatusSummary status, WaitingList waiting)`（既存の多重定義は `waiting=null` で呼ぶ）。`waiting` が null でなければトップレベルに `"waiting":{"items":[{"service","place","reason":"question|confirm|finished","since","app"}],"unreadable":[{"service","reason"}]}`。項目は最大8、`place` は `WaitingNames.Clean` 済み。
  - `public interface IWaitingSource { WaitingSnapshotInput Read(DateTimeOffset now); }`、`public sealed class WaitingSnapshotInput { public ClaudeSession[] Claude; public bool ClaudeReadable; public CodexThread[] Codex; public bool CodexReadable; }`、既定の実装 `LocalWaitingSource(string profile)`（Task 2・3 の `Read` を呼ぶ）。
  - `public static class AppLinks { public static string For(string app); public static bool Open(string app); }` — `For` は4つの決まったリンクか null。`Open` は `Process.Start(new ProcessStartInfo(link){UseShellExecute=true})`、失敗・null で false。
  - `TrayContext` の最後のコンストラクターに省略可能な引数 `IWaitingSource waitingSource=null` を足す。`source==null`（本番）なら既定は `LocalWaitingSource`。5秒の `System.Windows.Forms.Timer` で、読み取りは `Task.Run`、結果を UI スレッドで `WaitingTracker.Observe` → 前回と変わったら `popup.SetWaiting` と `PublishSnapshot`。毎回 `WaitingReminders.Due` の結果を、省略可能な引数 `Action<string> waitingNotification=null`（既定は `tray.ShowBalloonTip(8000,"対応待ち",text,ToolTipIcon.Info)`）で出す。使用量の警告の `notification` とは別にする。
  - 右クリックに「対応待ちの通知」：「3分後、その後10分ごと」「1分後、その後5分ごと」「通知しない」。選ぶと `SaveWaiting`、チェックを付ける（既存の通知の選択と同じ作り）。
  - `PopupForm.SetWaiting(WaitingList list, DateTimeOffset now)`：見出しの下・使用量の一覧の上に区画を出す。項目がなければ区画を出さない。最大4行＋「ほか N件」、読めないAIは「Claudeの記録を読めません」の1行。各行は AIの名前・場所・理由（質問・確認・完了）・待ち時間（`N分`、60分以上は `N時間M分`）。行のクリックで `WaitingOpenRequested(string app)` を出し、`TrayContext` が `AppLinks.Open` を呼ぶ。行の高さを `LayoutRows` の合計に足す。描いた行は `internal string WaitingText`（1行ずつ改行でつないだもの。例 `Claude Fix login 質問 4分`）でも返す。

- [ ] **Step 1: 失敗するテストを書く**
  - `SnapshotTests`：`Check("Snapshot carries the waiting list, cut to 8 items and cleaned")`：10項目と制御文字入りの `place` を渡し、`"waiting":{` があり、項目が8つ、`reason` が `question`、`app` が `claude`、`place` に制御文字がない。`waiting` が null なら `"waiting"` がない。
  - `WaitingTests`：`Check("Only the four app links can be opened")`：`AppLinks.For("claude")=="claude://"`、`"codex"`→`"codex://"`、`"vscode"`→`"vscode://"`、`"grokbot"`→`"grokbot://"`、`"https"`・`null` → null。
  - `UiTests`：偽の `IWaitingSource`（Claude の `waiting`・`input needed`、`StatusUpdatedAt` は4分前）を渡し、`Pump` 後に (a) `popup.WaitingText` に `Claude`・`質問`・`4分` が入る、(b) 共有データに `"waiting":{` と `"reason":"question"` がある、(c) 差し替えた `waitingNotification` が1回呼ばれ、文が `"が質問の答えを4分待っています"` を含む、(d) 「対応待ちの通知」→「通知しない」を選ぶと以後通知が出ない。
- [ ] **Step 2:** `build.ps1 -NoPublish`。Expected: コンパイル失敗。
- [ ] **Step 3:** 実装。
- [ ] **Step 4:** `build.ps1 -NoPublish`。Expected: `RESULT … 0 failed` と `RESULT … UI checks passed`。区画の見た目を一度画像にして確かめる（`PopupForm` を反射で描画し、`scratchpad` に保存して見る）。
- [ ] **Step 5:** コミット。

### Task 6: Stream Deck の「対応待ち」キー

**Files:** Modify `obsidian/src/usage.js`、`streamdeck/src/settings.js`・`render.js`・`plugin.js`、`streamdeck/manifest.json`、Test `obsidian/tests/usage.test.cjs`、`streamdeck/tests/render.test.cjs`・`plugin.test.cjs`・`build.test.cjs`

**Interfaces:**
- Consumes: Task 5 の共有データの `waiting`。
- Produces:
  - `parseSnapshot` が `waiting: { items: [{service, place, reason, since, app}], unreadable: [{service, reason}] }` を返す（接続中で、形が合うときだけ。合わない項目は捨てる。`service` は `Claude`・`Codex`・`Grok Bot`、`reason` は `question`・`confirm`・`finished`、`app` は `claude`・`codex`・`vscode`・`grokbot`、`since` は日付、`place` は40文字まで。項目は最大8、`unreadable` は最大3）。ないときは `{items:[],unreadable:[]}`。
  - `settings.js`：`WAITING_ACTION = PLUGIN + '.waiting'`、`APP_LINKS = { claude: 'claude://', codex: 'codex://', vscode: 'vscode://', grokbot: 'grokbot://' }`。
  - `render.js`：`waitingView(connection, now)` → `{ kind: 'waiting', title: '対応待ち', count, top: {service, place, reason, minutes} | null, finished, unreadable, tone }`。`tone` は待ち10分以上で `critical`、それ以外の対応待ちは `warning`、なければ `muted`。`renderKey` はこの種類を描く（件数、AIの名前、場所、待ち時間。対応待ちがなければ「対応待ちなし」と「完了 N件」）。未接続・読み込み中はほかのキーと同じ。
  - `plugin.js`：`WAITING_ACTION` のキー（設定なし、`didReceiveSettings` は無視）。押すと、対応待ちの先頭（なければ最新の完了）の `app` を `APP_LINKS` で引いて `deck.openUrl`。引けなければ何もしない。未接続なら既存どおりトレイアプリを起動。`POLL_MS` を 5000 に。
  - `manifest.json`：アクション「対応待ち」（`PropertyInspectorPath` なし）、`Version` は `1.3.0.0`。

- [ ] **Step 1: 失敗するテストを書く**
  - `usage.test.cjs`：`'waiting list is kept when valid; unknown apps, reasons and services are dropped'`（`app:'https'`、`reason:'other'`、`service:'X'` の項目が落ち、9項目は8に、`place` は40文字に）。`waiting` がないと空の形。
  - `render.test.cjs`：`'waiting key shows the longest wait first'`（2件、12分の Claude と4分の Codex → `top.service==='Claude'`、`minutes===12`、`tone==='critical'`）、`'waiting key without waits says 対応待ちなし and counts finished'`、`'waiting key text is escaped and fits'`（`place` に `<b>` と長い名前 → 描画に生の `<b>` がなく、文字の大きさは10px以上）。
  - `plugin.test.cjs`：`'pressing the waiting key opens the app of the longest wait'`（`openUrl` が `'claude://'`）、`'a waiting key never opens a link outside the four apps'`（共有データの `app:'https'` → 何も開かない）、`'a disconnected waiting key starts the tray app'`、`'polls every 5 seconds'`（`timers.intervals[0].ms===5000`）。既存の `[15000]` を期待するテストは5000に直す。
  - `build.test.cjs`：アクションが5つで最後が `[WAITING_ACTION, '対応待ち']`、PI なし、版 `1.3.0.0`。バンドルに4つのリンクが文字列として入っていてよい（通信の禁止の確認から除く）。
- [ ] **Step 2:** `node --test obsidian/tests/*.test.cjs streamdeck/tests/*.test.cjs`。Expected: 新しいテストが失敗。
- [ ] **Step 3:** 実装。
- [ ] **Step 4:** 同じコマンド。Expected: 全件成功。`tools/package-streamdeck.ps1` で 1.3.0 を作り、Stream Deck 同梱の Node 24 で疑似の Stream Deck を相手に「対応待ち」キーを出して押す確認をする（`scratchpad\ws-smoke-status.cjs` をこのアクション用に変えたもの）。キーの見た目を画像にして確かめる。
- [ ] **Step 5:** コミット。

### Task 7: 文書・全体の確認・レビュー

**Files:** `README.md`、`docs/data-sources.md`、`docs/privacy.md`、`docs/streamdeck-guide.md`

- [ ] **Step 1:** 文書を書く。README のできること、データ取得仕様に読む記録と判定（Claude の状態ファイルは公式の説明がなく更新で変わりうること、Codex の承認待ちは出ないこと）、プライバシーに新しく読むファイル・内容を読まないこと・開けるリンク・集中モードで通知が止まりうること、Stream Deck ガイドに「対応待ち」キーと例のファイル名 1.3.0。
- [ ] **Step 2:** `build.ps1 -NoPublish`、`node --test obsidian/tests/*.test.cjs streamdeck/tests/*.test.cjs`、`tools/package-streamdeck.ps1`、`tools/build-widgets.ps1`。Expected: すべて成功。
- [ ] **Step 3:** 全体のレビュー（新しい担当者に、この計画・spec・Review Focus を渡す）。重要な指摘はテストを先に書いて直す。
- [ ] **Step 4:** コミット。main へのマージ・トレイアプリの入れ替え・push は利用者に聞いてから。

### Task 8: Grok Bot の通知を読めるかの確認

**Files:** `scratchpad` の使い捨てのプログラムだけ。結果は spec の Grok Bot の節に書く。

- [ ] **Step 1:** .NET Framework 4.8 の単体の exe（`Windows.winmd` と `System.Runtime.WindowsRuntime.dll` を参照して csc でコンパイル）から `UserNotificationListener.Current.RequestAccessAsync()` と `GetNotificationsAsync(NotificationKinds.Toast)` を呼ぶ。利用者に、許可の画面が出たら許可してもらう。
- [ ] **Step 2:** Expected: 許可が `Allowed` になり、Grok Bot（`electron.app.Grok Bot`）の通知を数だけ数えられる。本文は表示しない。失敗したら、エラーの内容と、パッケージが要るかどうかを記録する。
- [ ] **Step 3:** 結果を spec に書き、Grok Bot の読み取り（とパッケージ化）の計画を別に作るための材料を利用者に報告する。
