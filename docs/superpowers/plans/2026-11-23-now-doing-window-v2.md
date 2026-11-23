# 「いまやってること」の窓 第2版 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 窓に、複数行の入力・ボタン・利用者のスクリプトの実行・「まだのこと」・長い文の送信を足し、Ctrl や Shift の2回押しで窓を開けるようにし、AI の完了を日中ログに1行で残せるようにする。

**Architecture:** 窓（`--now-doing` の別プロセス）は「日中ログに書く・Claudian に送る・settings.ini に書かれた命令を実行して結果を出す」だけをする。日誌の決まり（✅・frontmatter・まだのこと）は利用者のスクリプトに置き、アプリには持たせない。2回押しはトレイアプリが `WH_KEYBOARD_LL` のフックで見つけ、窓を `--now-doing-tap` で開く。AI の完了はトレイアプリが対応待ちの一覧から拾い、窓と同じ書き込みの処理で日中ログに書く。

**Tech Stack:** C# 5 / .NET Framework 4.8 / WinForms（`build.ps1` の csc）、依存なしの Node.js（Obsidian プラグイン）。

**Spec:** `docs/superpowers/specs/2026-11-23-now-doing-window-v2-design.md`（vault 側のスクリプトと設定は、保管庫の中の別の文書で決める。スクリプトの中身はこの計画に含めない）

## Global Constraints

- C# 5 の構文だけ（文字列補間・式本体メンバーなし）。新しい依存なし。テストを先に書く。コミットの末尾は `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`。
- 日誌の作り（見出しの名前・終わりの目印の設定以外）をアプリのコードに書かない。
- スクリプトはシェルを使わずに実行する（`UseShellExecute=false`・`CreateNoWindow=true`）。入れた文は引数1つとして渡し、命令の文字列に埋め込まない。
- 標準出力の上限: 書いたあとのスクリプトと一覧のスクリプトは4096バイト・20行、copy は32KB（32768バイト）・行数の上限なし。時間の上限: 書いたあとのスクリプトと copy は10秒、一覧のスクリプトは3秒。
- Enter の記録は200文字まで。Ctrl+Enter で送る全文は4000文字まで。送るときの日中ログの1行は、1行目の先頭80文字と、2行以上のときの `（Claudian へ・N行）`。
- 入力欄は1行の高さから6行まで伸びる。ボタンは `now_doing_button1`〜`9`、名前は8文字で切る。開けるリンクは `claude://` `codex://` `vscode://` `grokbot://` だけ。
- 2回押し: 押している時間300ミリ秒以内、離してから400ミリ秒以内にもう一度押す。値は `DoubleCtrl`・`DoubleLeftCtrl`・`DoubleRightCtrl`・`DoubleShift`・`DoubleLeftShift`・`DoubleRightShift` だけ（Alt の2回押しは選べない）。
- フックが見るのは「左右の Ctrl・Shift の押し離し」と「それ以外のキーが押された」という事実と時刻だけ。どのキーかは見ない・残さない。何も保存しない。すべて `CallNextHookEx` で渡す。
- キーを送るのは、2回押しで開いた窓が前に出られないときの、何もしないキー1つ（down と up）だけ。
- AI の完了: 最初の完了から2分まとめる、同じセッション・スレッドは30分に1回まで、日誌が10秒以内に変わっていたら10秒おきに待ち直し、5分で諦める。`now_doing_ai_done=on` のときだけ。
- 新しい通信はしない。テーマなどの保存で `now_doing_` の行を消さない（今の決まりのまま）。
- 言葉は spec の「言葉」の表に揃える（記録・送る・ボタン・書いたあとのスクリプト・一覧のスクリプト・AI の完了）。自分で作った言い回しを使わない。

## Review Focus

1. 200文字を超える文で Enter → 黙って切らず、書かずに「日誌に書けるのは200文字までです」と出し、文は残す（入力欄が4000文字以上を受けるようになるため）。Task 4 のテスト。
2. `now_doing_button5=⏱️ 作業|text:⏱️ ` の末尾の空白 → settings.ini の読み取りで消えず、入力欄に `⏱️ ` が入る。Task 1 のテスト。
3. 一覧のスクリプトがまだ動いているうちに Esc で閉じる、または終わらないスクリプト → スクリプトとその子のプロセスを終わらせ、残さない。Task 2・Task 6 のテスト。
4. ブラウザや AI のアプリから LF だけの改行の文を貼る → 入力欄で別の行として見え、改行を保ったまま送られる。Task 4 のテスト。
5. 除くフォルダーと作業フォルダーの書き方の違い（大文字小文字・末尾の `\`・`\\?\` 付き）→ 同じ場所として除く。隣の `…Vault2` は除かない。Task 8 のテスト。

---

### Task 1: 設定の読み取り（ボタン・命令・スクリプト・AI の完了）

**Files:**
- Create: `src/NowDoingCommand.cs`（`NowDoingButton`・`NowDoingCommand`）
- Modify: `src/NowDoing.cs`（`NowDoingSettings`）
- Test: `tests/NowDoingActionTests.cs`（新規。`build.ps1` の Tests.exe の一覧に足し、`tests/Tests.cs` の Main で `RunNowDoingActionTests()` を `RunNowDoingTests()` の後に呼ぶ）

**Interfaces:**
- Produces:
  - `internal enum NowDoingButtonKind { Text, Record, Copy }`
  - `internal sealed class NowDoingButton { public int Number; public string Label; public NowDoingButtonKind Kind; public string Value; public string App; public string ShortLabel { get; } public static NowDoingButton Parse(int number, string value); public static string Problem(IList<int> bad); }`
    - `Parse`: `<名前>|<動き>:<中身>`。名前は最初の `|` の前（前後の空白を除く・空なら null）。動きは `text:`・`record:`・`copy:` だけ。`record`・`copy` の中身が空なら null、`text:` は中身が空白だけでも可（空文字列は null）。`copy` は最後の `|open:` で命令とリンクに分け、リンクは `AppLinks.For` が返す4つのどれかと完全一致のときだけ、`App` にその名前（`"claude"` など）を入れる。ほかは null。命令が `NowDoingCommand.Split` で null なら null。`Value` は text・record では文、copy では命令。
    - `ShortLabel`: `Label` の先頭8文字。文字は `StringInfo` の text element で数える（`🗒️` は1文字）。
    - `Problem`: 空なら null。1つなら「settings.ini の now_doing_button2 の書き方が違います」、複数なら「settings.ini の now_doing_button2・now_doing_button5 の書き方が違います」。
  - `NowDoingSettings` に足す: `public NowDoingButton[] Buttons = new NowDoingButton[9];`（`Number-1` の位置）・`public List<int> BadButtons = new List<int>();`・`public string After, List, Workdir;`・`public bool AiDone;`・`public string[] AiDoneExclude = new string[0];`
    - `now_doing_button1`〜`9` は、値の先頭の空白だけを除き、末尾の空白は残す。`now_doing_button0`・`10` などは無視する。
    - `now_doing_ai_done_exclude` は `;` で分け、前後の空白を除き、空のものを捨てる。
  - `internal static class NowDoingCommand { public static string[] Split(string command); public static string Quote(string argument); public static string Join(IEnumerable<string> arguments); }`
    - `Split`: `"` の外の空白（スペース・タブ）で語を分ける。`"` は囲みの始まりと終わりで、結果には残らない。`\` はそのまま。空・空白だけ・`"` が閉じていないときは null。
    - `Quote`: Windows（`CommandLineToArgvW` と MSVCRT）が元の1つの引数に戻せる形にする。空、または空白・タブ・改行・`"` を含むときだけ `"` で囲む。`"` の前の `\` の並びは2倍にして `\"` にし、囲むときは末尾の `\` の並びも2倍にする。
    - `Join`: 各引数を `Quote` して空白1つでつなぐ。

- [ ] **Step 1: 失敗するテストを書く**（`tests/NowDoingActionTests.cs`）
  - `Check("Buttons are read in their three forms")`: `Parse(1,"🚶 散歩|record:🚶 散歩 ✅")` → `Kind==Record && Label=="🚶 散歩" && Value=="🚶 散歩 ✅"`。`Parse(5,"⏱️ 作業|text:⏱️ ")` → `Kind==Text && Value=="⏱️ "`。`Parse(7,@"📈 依頼文|copy:python C:\Notes\scripts\request.py|open:claude://")` → `Kind==Copy && Value==@"python C:\Notes\scripts\request.py" && App=="claude"`。`|open:` がなければ `App==null`。
  - `Check("Buttons written wrongly are not made")`: `"名前だけ"`・`"|text:x"`・`"x|run:y"`・`"x|record:"`・`"x|text:"`・`"x|copy:"`・`"x|copy:a|open:https://example.com"`・`"x|copy:\"a"` の `Parse` がすべて null。
  - `Check("settings.ini keeps the trailing space of a text button and names the wrong buttons")`: `NowDoingSettings.Parse("now_doing_button5=⏱️ 作業|text:⏱️ \r\nnow_doing_button2=だめ\nnow_doing_button10=x|text:y\n")` → `Buttons[4].Value=="⏱️ "`・`Buttons[1]==null`・`BadButtons` が `[2]` だけ。`NowDoingButton.Problem(new[]{2})=="settings.ini の now_doing_button2 の書き方が違います"`、`Problem(new[]{2,5})=="settings.ini の now_doing_button2・now_doing_button5 の書き方が違います"`、`Problem(new int[0])==null`。
  - `Check("Long button names are cut at 8 characters")`: `Parse(1,"🗒️ 議事の記録をする|text:x").ShortLabel=="🗒️ 議事の記録を"`、`Parse(1,"作業|text:x").ShortLabel=="作業"`。
  - `Check("Script settings and the AI done keys are read")`: `now_doing_after`・`now_doing_list`・`now_doing_workdir` がそのまま入る。`now_doing_ai_done=on` で `AiDone`、それ以外は false。`now_doing_ai_done_exclude= D:\a; D:\b ;` → `["D:\a","D:\b"]`。
  - `Check("Commands are split into the program and its arguments")`: `Split("python -X utf8 \"C:\\My Notes\\after.py\"")` → `["python","-X","utf8",@"C:\My Notes\after.py"]`。`Split(@"C:\x\a.exe")` → `[@"C:\x\a.exe"]`。`Split("  ")`・`Split("")`・`Split(null)`・`Split("\"a b")` は null。
  - `Check("Arguments survive Windows command-line parsing unchanged")`: 引数の組 `""`・`a b`・`a"b`・`a\"b`・`C:\dir\`・`C:\dir with space\`・`一行目\n二行目`・`& | < > ^ %PATH%`・`🚶 散歩 ✅`・`\\server\share` について、`CommandLineToArgvW("x " + Join(args))`（テストで shell32 を P/Invoke）の2つ目以降が元の組と一致する。
- [ ] **Step 2: 失敗を確かめる**  
  Run: `.\build.ps1 -TestsOnly -OutputDirectory build\v2`  
  Expected: コンパイルが `NowDoingButton`・`NowDoingCommand` がないことで失敗する。
- [ ] **Step 3: `src/NowDoingCommand.cs` と `NowDoingSettings` の追加を書く**（上の Interfaces どおり）
- [ ] **Step 4: 通ることを確かめる**  
  Run: `.\build.ps1 -TestsOnly -OutputDirectory build\v2`  
  Expected: すべて PASS（FAIL が0行）。
- [ ] **Step 5: コミット**（spec の写し `docs/superpowers/specs/2026-11-23-now-doing-window-v2-design.md` とこの計画も含める）  
  `git commit -m "Read now-doing buttons, script commands and AI done settings"`

### Task 2: スクリプトの実行

**Files:**
- Create: `src/NowDoingScript.cs`
- Modify: `tests/Tests.cs`（`Main()` を `Main(string[] args)` にし、`args[0]=="--child"` のときはテスト用の子として動いて終わる）
- Test: `tests/NowDoingActionTests.cs`

**Interfaces:**
- Consumes: `NowDoingCommand.Split`・`NowDoingCommand.Join`（Task 1）、`ProcessTree.List()`（`src/Waiting.cs`）。
- Produces:
  - `internal sealed class NowDoingScriptLimits { public int TimeoutMs, MaxBytes, MaxLines; public static readonly NowDoingScriptLimits After /*10000,4096,20*/, List /*3000,4096,20*/, Copy /*10000,32768,int.MaxValue*/; }`
  - `internal sealed class NowDoingScriptResult { public bool Ok; public string Output; public string Reason; public string FirstLine { get; } }`
    - `Output`: 標準出力を UTF-8 として読んだもの（読めないバイトは U+FFFD）。CRLF・CR は LF にし、末尾の改行は除く。`FirstLine` は `Output` の1行目（空なら ""）。
  - `internal static class NowDoingScript { public static Task<NowDoingScriptResult> RunAsync(string command, IList<string> arguments, string workdir, NowDoingScriptLimits limits, CancellationToken cancel); }` — 例外を外に出さない。
    - 命令が `Split` で null →「命令の書き方が違います」。`workdir` がない →「作業フォルダーがありません」。起動できない →「起動できません（<実行ファイル>）」。
    - 標準入力はすぐ閉じる。標準出力はバイトのまま読み、`MaxBytes` を超えたか、行数（`Output` を LF で分けた数）が `MaxLines` を超えたら、プロセスを終わらせて「出力が多すぎます」。
    - 時間切れ → プロセスを終わらせて「時間切れ（N秒）」（N は `TimeoutMs/1000` の切り上げ）。`cancel` → プロセスを終わらせて「中止しました」。
    - 終了コードが0以外 → 標準エラー（4096バイトまで読む）の最初の空でない行を前後の空白を除いて100文字まで。なければ「終了コード N」。
    - 「プロセスを終わらせる」は、そのプロセスと、`ProcessTree.List()` でたどれる子孫（開始がそのプロセスより前でないもの）を終わらせること。

- [ ] **Step 1: テスト用の子を書く**（`tests/Tests.cs`）。`--child args`（受け取った引数を `JavaScriptSerializer` で JSON の配列にして UTF-8 で書く）・`cwd`（`Environment.CurrentDirectory`）・`crlf`（`一行目\r\n二行目\r\n\r\n`）・`badutf8`（バイト `FF` のあと `ok`）・`big N`（`x` を N バイト）・`lines N`（N 行）・`fail`（標準エラーに `\n  理由の1行目\n2行目`、終了コード3）・`exit N`・`sleep`（60秒）・`spawn-sleep <file>`（自分を `--child sleep` で起動し、その pid を `<file>` に書いて60秒待つ）。
- [ ] **Step 2: 失敗するテストを書く**（自分の exe を `NowDoingCommand.Quote(自分のパス) + " --child …"` で命令にする）
  - `Check("Arguments reach the script as they were given")`: 引数 `["--note", @"C:\Notes\2026年_日誌\2026-11-24 - 日誌.md", "--text", "a \"b\" \\ & | %PATH%\n二行目"]` を `--child args` に渡すと `Ok` で、JSON を戻した配列が一致する。
  - `Check("The script runs in the working folder it is given")`: `--child cwd` の `Output` が一時フォルダーと一致する。
  - `Check("Output is read as UTF-8 with LF line breaks and no line break at the end")`: `crlf` → `Output=="一行目\n二行目" && FirstLine=="一行目"`。`badutf8` → `Ok && Output=="\uFFFDok"`。
  - `Check("Too much output fails")`: `After` で `big 4096` は Ok・`big 4097` は「出力が多すぎます」、`lines 20` は Ok・`lines 21` は失敗。`Copy` で `big 32768` は Ok・`big 32769` は失敗・`lines 100` は Ok。
  - `Check("A script that runs too long is ended with its children")`: `TimeoutMs=1000` の上限で `spawn-sleep <file>` → `!Ok && Reason=="時間切れ（1秒）"`、`<file>` の pid のプロセスが2秒以内に終わっている。
  - `Check("Failures say why")`: `fail` →「理由の1行目」。`exit 4` →「終了コード 4」。`no-such-program-x` →「起動できません（no-such-program-x）」。`"\"a` →「命令の書き方が違います」。ない作業フォルダー →「作業フォルダーがありません」。
  - `Check("Cancelling ends the script and its children")`: 上限60秒で `spawn-sleep <file>` を始め、`<file>` ができたら取り消す → `Reason=="中止しました"`、子も2秒以内に終わっている。
- [ ] **Step 3: 失敗を確かめる**  
  Run: `.\build.ps1 -TestsOnly -OutputDirectory build\v2`  
  Expected: `NowDoingScript` がないことでコンパイルが失敗する。
- [ ] **Step 4: `src/NowDoingScript.cs` を書く**（標準出力・標準エラーは `BaseStream` を別々に非同期で読み、待つのは `Task`。UI スレッドを止めない）
- [ ] **Step 5: 通ることを確かめる**（Step 3 と同じ。FAIL が0行）
- [ ] **Step 6: コミット** `git commit -m "Run the user's now-doing scripts without a shell, with output and time limits"`

### Task 3: 送るときの日中ログの1行と、Claudian への受け渡し（アプリとプラグイン）

**Files:**
- Modify: `src/NowDoing.cs`、`obsidian/src/claudian.js`、`obsidian/src/plugin.js`、`obsidian/manifest.json`（`1.3.0`）
- Test: `tests/NowDoingTests.cs`、`obsidian/tests/claudian.test.cjs`

**Interfaces:**
- Produces（C#、`NowDoingNote`）:
  - `public const int MaxSendLength = 4000, SendHeadLength = 80;`
  - `public static string ForClaudian(string typed)` — CRLF・CR を LF にし、外側の空白を除く。
  - `public static string SendLine(string text)` — `text` は `ForClaudian` の結果。最初の空でない行の前後の空白を除いて80文字まで（サロゲートの途中で切らない）。`text` を LF で分けて2行以上なら、後ろに `（Claudian へ・N行）`（N はその行数）。
  - `Insert(..., out NowDoingFailure failure, string mark = Mark)`・`Write(NowDoingSettings settings, DateTime openedAt, string text, string backupPath, Action<int> sleep = null, string mark = Mark)` — 2行目は `mark + " " + text`。
  - `public static string BackupPath(string localAppData, string name = "now-doing-last.md")`
  - `HandToClaudian(string text, string messagePath, string vault, Func<string,bool> open, string token = null, long? now = null, string note = null)` — `note` があれば JSON に `"note"` を入れる。
- Produces（プラグイン、`claudian.js`）:
  - `MAX_TEXT=4000`、`MAX_FILE=65536`。
  - `takeMessage(token,file,now,files)` → `{text, note}` か null。`note` は1024文字以下の文字列のときだけ、ほかは null。
  - `async function openNote(app, note)` → 開けたら true。`app.vault.adapter.getBasePath()` を基に `path.win32.relative` で保管庫の中かを調べ、空・`..` で始まる・絶対パスなら false。中なら `\` を `/` にした保管庫の中の場所で `app.vault.getAbstractFileByPath` を引き、`extension` のあるファイルだけ `await app.workspace.getLeaf(false).openFile(file)`。例外は false。
  - `plugin.js` の `sendToClaudian`: `takeMessage` の結果で、`await claudian.openNote(this.app, message.note)` のあとに `claudian.sendToClaudian(this.app, message.text)`。

- [ ] **Step 1: 失敗するテストを書く**
  - C# `Check("The line for the 日中ログ when sending is the first line, cut at 80, with the line count")`: `SendLine("見積の表")=="見積の表"`。`SendLine(ForClaudian("\n\n  一行目  \n二行目\n\n四行目"))=="一行目（Claudian へ・4行）"`。`SendLine(new string('あ',100))==new string('あ',80)`。`SendLine(new string('a',79)+"💊")==new string('a',79)`。
  - C# `Check("Text for Claudian keeps its line breaks as LF")`: `ForClaudian(" a\r\nb\rc \n")=="a\nb\nc"`。
  - C# `Check("A line with another mark goes where a 📝 line would, and is not one of today's records")`: 既存の `note` に `Insert(note, 10:40, "Claude 完了: x", settings, out f, "🤖")` → `[11:05]` の前に `[10:40]\n🤖 Claude 完了: x\n\n`。その結果の `Records` に入らない。
  - C# `Check("The message for Claudian carries the diary page")`: `HandToClaudian(..., note: @"C:\V\日誌.md")` で書かれた JSON の `note` がその場所。`note` なしのときは `note` のキーがない。
  - JS `the text and the page are taken together; 4000 characters is the limit`: `{token,text:'あ'.repeat(4000),note:'C:\\V\\a.md',created}` → `{text, note:'C:\\V\\a.md'}`。4001文字は null。`note:5` → `note:null`。
  - JS `the page is opened only when it is a file in this vault`: 偽の `app`（`getBasePath()` が `'C:\\Vault'`、`getAbstractFileByPath` が `'日誌/2026-11-24 - 日誌.md'` にだけ `{extension:'md'}` を返し、`getLeaf(false).openFile` を記録する）で、`openNote(app,'c:\\vault\\日誌\\2026-11-24 - 日誌.md')` が true で1回開く。`'C:\\Other\\a.md'`・`'C:\\Vault\\..\\a.md'`・保管庫にないファイル・`null` は false で開かない。
- [ ] **Step 2: 失敗を確かめる**  
  Run: `.\build.ps1 -TestsOnly -OutputDirectory build\v2` と `node --test obsidian/tests/claudian.test.cjs`  
  Expected: C# はコンパイル失敗、JS は新しいテストが失敗。
- [ ] **Step 3: 上の Interfaces どおりに書く**（既存の `takeMessage` のテストは `{text}` の形に直す）
- [ ] **Step 4: 通ることを確かめる**  
  Run: Step 2 と同じ、加えて `node --test obsidian/tests/usage.test.cjs obsidian/tests/bridge.test.cjs obsidian/tests/toolbar.test.cjs obsidian/tests/claudian.test.cjs`  
  Expected: すべて PASS。
- [ ] **Step 5: コミット** `git commit -m "Send the first line to the diary and the whole text with its page to Claudian"`

### Task 4: 窓 — 複数行の入力・キー・書いたあとのスクリプト

**Files:**
- Modify: `src/NowDoingForm.cs`
- Test: `tests/UiTests.cs`

**Interfaces:**
- Consumes: `NowDoingScriptResult`・`NowDoingScript.RunAsync`・`NowDoingScriptLimits.After`（Task 2）、`ForClaudian`・`SendLine`・`MaxSendLength`・`HandToClaudian(..., note:)`（Task 3）、`NowDoingSettings.After`・`Workdir`（Task 1）。
- Produces:
  - `internal sealed class NowDoingActions { public Func<string, NowDoingFailure> Write; public Func<NowDoingFailure, string> Describe; public Func<string, bool> Send; public Func<string, string, Task<NowDoingScriptResult>> After; public Func<CancellationToken, Task<NowDoingScriptResult>> List; public Func<string, Task<NowDoingScriptResult>> Copy; public Func<string, bool> SetClipboard; public Func<string, bool> OpenApp; public NowDoingButton[] Buttons = new NowDoingButton[9]; public string Notice; public int ShowOutputMs = 1500; }`（`After` の引数は `(mode, text)`、mode は `"record"` か `"send"`。`Copy` の引数は命令。`List`・`Copy`・`Buttons`・`Notice` は Task 5・6 で使う）
  - `public NowDoingForm(DateTime openedAt, NowDoingView view, NowDoingActions actions, ThemePalette palette)`。今の6引数のコンストラクターは残し、`NowDoingActions` を作って渡す。
  - テスト用: `public bool Busy { get; }`・`public int InputLines { get; }`（見えている行数 1〜6）・`internal bool Press(Keys keyData)`（`ProcessCmdKey` と同じ処理）。
  - 窓に出す文（`public const string`）: `RecordHint="Enter で日誌へ ／ Shift+Enter で改行 ／ Esc でやめる"`・`ClaudianHint="Enter で日誌へ ／ Ctrl+Enter で Claudian へも ／ Shift+Enter で改行"`・`TooLongToRecord="日誌に書けるのは200文字までです"`・`TooLongToSend="4000文字までです"`・`Working="日誌に書きました。そのあとの処理をしています…"`・`public static string AfterFailed(string reason)`＝「日誌には書きました。そのあとの処理に失敗しました: 」＋reason。
- 動き:
  - 入力欄は `Multiline`・`WordWrap`・`AcceptsReturn=false`・`MaxLength=32767`。見えている行数（`GetLineFromCharIndex(TextLength)+1`、1〜6）に合わせて欄と窓の高さを変える（1行ぶん22、96-DPI の単位）。6行を超えたら縦のスクロールバーを出す。
  - 貼り付け（WM_PASTE）は、クリップボードの文字の改行を CRLF にそろえて入れる（入力欄を `TextBox` の小さな派生クラスにして `WndProc` で受ける）。文字でないものは何もしない。Ctrl+A は全部を選ぶ。
  - Enter: `Clean` した文が空なら閉じる。200文字を超えたら書かずに `TooLongToRecord`（文は残す）。書けたら、`After` があれば `("record", 書いた文)` で実行。
  - Shift+Enter: カーソルの位置に改行を入れる。
  - Ctrl+Enter（`Send` があるとき。ないときは Enter と同じ）: `ForClaudian` が空なら閉じる。4000文字を超えたら書かずも送らずも `TooLongToSend`。`SendLine` を日中ログに書き、`After` があれば `("send", 全文)` で実行し、終わってから（失敗しても）`Send(全文)`。
  - `After` を待つ間は `Busy`。状態の行に `Working`。キー・ボタン・× には反応しない（10秒で必ず終わる）。
  - 結果: `Send` が false なら `ClaudianFailed`（今の文）。そうでなく `After` が失敗なら `AfterFailed(理由)`。どちらも赤で出し、入力欄を空にして窓は残す。どちらでもなければ、`After` の `FirstLine` が空でないときは赤でない色で出して `ShowOutputMs` ミリ秒後に閉じ、空なら（`After` がないときも）すぐ閉じる。
  - `Run()`: `After = settings.After == null ? null : (mode, text) => NowDoingScript.RunAsync(settings.After, new[]{"--note", view.Path, "--time", HH:mm, "--mode", mode, "--text", text}, workdir, NowDoingScriptLimits.After, CancellationToken.None)`。`workdir` は `settings.Workdir`、なければアプリのフォルダー。`view.Path` が null なら `After` も null。`Send` は `HandToClaudian(text, message, vault, OpenLink, note: view.Path)`。

- [ ] **Step 1: 失敗するテストを書く**（`tests/UiTests.cs` の `CheckNowDoingWindow` の後に `CheckNowDoingInput()` を足して Main から呼ぶ。`After`・`Send` は記録する偽物、待たせるときは `TaskCompletionSource`）
  - `"Shift+Enter starts a new line and the box grows up to six lines"`: `Input="a"`・カーソルを末尾・`Press(Shift|Enter)` → `Input=="a\r\n"`。3行で `InputLines==3` かつ窓の高さが1行のときより高い。10行で `InputLines==6`。
  - `"Pasted text keeps its line breaks"`: クリップボードに `"一\n二\r\n三"` を入れ、入力欄に WM_PASTE を送る → `Input=="一\r\n二\r\n三"`。（クリップボードは STA でしか使えないため、UiTests の `Main` に `[STAThread]` を付ける。今 `CheckNowDoingWindow` に付いているものは効いていないので外す）
  - `"Enter writes the lines as one line, and more than 200 characters is not written"`: `"a\r\nb"` で Enter → 書いた文 `"a b"`。201文字で Enter → 書かない・`Status==TooLongToRecord`・`StatusIsFailure`・文が残る。
  - `"Ctrl+Enter writes the first line and sends the whole text"`: `"一行目\r\n二行目\r\n三行目"` → 書いた文 `"一行目（Claudian へ・3行）"`、送った文 `"一行目\n二行目\n三行目"`。4001文字 → 書かない・送らない・`Status==TooLongToSend`。
  - `"The after script gets the mode and the text, and its first line shows before the window closes"`: `After` が `Ok`・`"🚶 散歩 ✅\n2行目"` を返し、`ShowOutputMs=50`。`"🚶 散歩"` で Enter → `After` の呼び出しが `("record","🚶 散歩")`、直後は `Visible && Status=="🚶 散歩 ✅" && !StatusIsFailure`、`DoEvents` を回して300ミリ秒以内に閉じる。
  - `"While the after script runs the window takes no keys, and Ctrl+Enter sends after it ends"`: `After` を `TaskCompletionSource` で待たせて Ctrl+Enter → `Busy`・`Status==Working`・送った数0・`Press(Escape)` でも `Visible`。結果を `Ok`・空で終わらせる → 送った文が全文で、窓が閉じる。`After` の mode は `"send"`、text は全文。
  - `"A failed after script keeps the window, empties the box and says the diary was written"`: `After` が「見出しが見つかりません」で失敗 → `Status=="日誌には書きました。そのあとの処理に失敗しました: 見出しが見つかりません"`・`StatusIsFailure`・`Input==""`・`Visible`・書いた数1。Ctrl+Enter で失敗させたときも `Send` は呼ばれる。
  - 今の UiTests のヒントの文を `RecordHint`・`ClaudianHint` に直し、`MaxLength==200` の Assert を `32767` に直す（200文字の決まりは Enter のときに確かめる）。スクリーンショット `now-doing-multiline-light.png`（3行の入力）を足す。
- [ ] **Step 2: 失敗を確かめる**  
  Run: `.\build.ps1 -NoPublish -OutputDirectory build\v2`  
  Expected: UiTests のコンパイルか新しい Assert が失敗する。
- [ ] **Step 3: 上の Interfaces と動きのとおりに書く**
- [ ] **Step 4: 通ることを確かめる**（Step 2 と同じ。最後に `Built …\AIUsageManager.exe`）。`build\v2\screenshots\now-doing-multiline-light.png` を開いて、欄が3行ぶんに伸び、文字が欄からはみ出していないことを見る。
- [ ] **Step 5: コミット** `git commit -m "Take several lines in the now-doing window and run the after script"`

### Task 5: 窓 — ボタン

**Files:**
- Modify: `src/NowDoingForm.cs`
- Test: `tests/UiTests.cs`

**Interfaces:**
- Consumes: `NowDoingButton`（Task 1）、`NowDoingActions.Buttons`・`Copy`・`SetClipboard`・`OpenApp`・`Notice`（Task 4）、`NowDoingScriptLimits.Copy`（Task 2）、`AppLinks.Open`。
- Produces:
  - テスト用: `public IList<Button> ButtonControls { get; }`（番号の順）・`public IList<string> Footer { get; }`（窓の下の小さな行）・`public void PressButton(int number)`。
  - 窓に出す文: `Copied="クリップボードに入れました"`・`ClipboardBusy="クリップボードに入れられませんでした"`・`public static string CopyFailed(string reason)`＝「コピーできませんでした: 」＋reason（出力が空なら reason は「出力が空です」）。
- 動き:
  - ボタンは入力欄の下に、番号の順に左から並べる。高さ28、間6、幅は `ShortLabel` の幅＋20（最小56）。中に入らない分は次の行（1行ぶん34）。`TabStop=false`、押したあとは入力欄に戻る。ツールチップと `AccessibleName` は `Label` と「（Alt+N）」。
  - Alt+1〜Alt+9 は、その番号のボタンがあれば `PressButton(N)`、なければ何もしない。`Busy` の間は何もしない。
  - text: 入力欄を `Value` にし、カーソルを末尾に置く。record: 入力欄を `Value` にして Enter と同じ（書いたあとのスクリプトも動く）。
  - copy: `Busy` にして `Copy(Value)`。`Ok` で出力が空でなければ、LF を CRLF にして `SetClipboard`。false なら `ClipboardBusy`（赤）。true なら、`App` があれば `OpenApp(App)`、`Copied` を出して `ShowOutputMs` 後に閉じる。失敗・空ならクリップボードを変えず、開かず、`CopyFailed(理由)` を赤で出して窓を残す。
  - `Notice` は窓の下の行に赤で出す。
  - `Run()`: `Buttons = settings.Buttons`、`Notice = NowDoingButton.Problem(settings.BadButtons)`、`Copy = command => NowDoingScript.RunAsync(command, view.Path == null ? new string[0] : new[]{"--note", view.Path}, workdir, NowDoingScriptLimits.Copy, CancellationToken.None)`、`SetClipboard` は `Clipboard.SetDataObject(text, true, 5, 100)` で例外なら false、`OpenApp = AppLinks.Open`。

- [ ] **Step 1: 失敗するテストを書く**（`CheckNowDoingButtons()` を足して Main から呼ぶ）
  - `"Buttons sit under the box in their order and wrap to the next row"`: 名前 `🚶 散歩`・`☕ 休憩`・`📚 読書`・`🧹 片付け`・`⏱️ 作業`・`🗒️ 議事`・`📈 依頼文`・`🧘 ストレッチ`・`📞 電話の記録をする` の9つ → `ButtonControls` が番号の順、`Top` が2種類以上、すべての `Right` が窓の右の余白の内側、9つ目の `Text=="📞 電話の記録をす"`。スクリーンショット `now-doing-buttons-light.png`。
  - `"Alt+number presses that button, and a missing number does nothing"`: ボタン5が `text:⏱️ ` → `Press(Alt|D5)` で `Input=="⏱️ "` かつ `SelectionStart==Input.Length`。ボタン8がないとき `Press(Alt|D8)` で何も変わらない。
  - `"A record button writes its text and runs the after script"`: ボタン1が `record:🚶 散歩 ✅` → Alt+1 で書いた文 `"🚶 散歩 ✅"`、`After` が `("record","🚶 散歩 ✅")`。
  - `"A copy button puts the output in the clipboard and opens the app"`: `Copy` が `Ok`・`"依頼文\n2行目"` → `SetClipboard` に `"依頼文\r\n2行目"`、`OpenApp` に `"claude"`、`Status==Copied`。
  - `"A failed copy changes nothing and says why"`: `Copy` が「今日の依頼文がありません」で失敗 → `SetClipboard`・`OpenApp` は呼ばれない、`Status=="コピーできませんでした: 今日の依頼文がありません"`、`Visible`。出力が空のときは「コピーできませんでした: 出力が空です」。
  - `"Wrong buttons are named at the bottom of the window"`: `Notice` を渡すと `Footer` にその文がある。
- [ ] **Step 2: 失敗を確かめる**（Task 4 の Step 2 と同じ）
- [ ] **Step 3: 上の動きのとおりに書く**
- [ ] **Step 4: 通ることを確かめる**（同じ）。`now-doing-buttons-light.png` で、ボタンが2行に分かれ、名前が欠けずに読めることを見る。
- [ ] **Step 5: コミット** `git commit -m "Add the now-doing buttons: text, record and copy"`

### Task 6: 窓 — まだのこと と 今日の記録の件数

**Files:**
- Modify: `src/NowDoingForm.cs`
- Test: `tests/UiTests.cs`

**Interfaces:**
- Consumes: `NowDoingActions.List`（Task 4）、`NowDoingScriptLimits.List`（Task 2）、`Footer`（Task 5）。
- Produces:
  - テスト用: `public IList<string> Todo { get; }`。
  - `internal static int RecordRows(int records, float available, float other, float row)` — `min(records, 6)` から始め、`other + 行数*row <= available` になるまで減らす。ただし `min(records, 3)` より減らさない。`records` が0なら1（「まだありません」の行）。
  - 窓に出す文: `TodoFailed="まだのことを出せませんでした"`。
- 動き:
  - 窓を出したら（`OnShown`）、`List` があれば裏で実行する。窓を閉じたら（`OnFormClosed`）取り消す。
  - `Ok` なら、出力の空でない行の前後の空白を除いたものを最大5行、入力欄の上に「まだのこと」の見出し（18）と1行22で出す。長い行は末尾を「…」にして1行に収める。表示だけで、押しても何も起きない。出力が空なら欄を出さない。
  - 失敗なら欄を出さず、`Footer` に `TodoFailed` を灰色で出す。
  - 窓の高さが画面の作業領域の高さ−32（96-DPI）を超えるときは、`RecordRows` で今日の記録の件数を減らす。
  - `Run()`: `List = settings.List == null || view.Path == null ? null : token => NowDoingScript.RunAsync(settings.List, new[]{"--note", view.Path}, workdir, NowDoingScriptLimits.List, token)`。

- [ ] **Step 1: 失敗するテストを書く**（`CheckNowDoingTodo()` を足して Main から呼ぶ）
  - `"The list script's lines show above the box, at most five"`: `List` が `"🚶 散歩がまだ\nDW#1 見積\n\n⚠️ 注意\n📈 今日の1%\n六行目\n七行目"` を返す → `Todo` が `["🚶 散歩がまだ","DW#1 見積","⚠️ 注意","📈 今日の1%","六行目"]`、入力欄の `Top` が `List` なしの窓より下。スクリーンショット `now-doing-todo-light.png`。
  - `"A failed list script shows no list and a small note at the bottom"`: 失敗 → `Todo.Count==0`、`Footer` に `TodoFailed`。
  - `"Closing the window stops a list script that is still running"`: `List` が受け取った token を記録し、終わらない `Task` を返す → `Close()` のあと `token.IsCancellationRequested`。
  - `"Fewer of today's records are shown on a short screen, never fewer than three"`: `RecordRows(8,600,400,26)==6`、`RecordRows(8,500,400,26)==3`、`RecordRows(8,420,400,26)==3`、`RecordRows(2,100,400,26)==2`、`RecordRows(0,600,400,26)==1`。
- [ ] **Step 2: 失敗を確かめる**（Task 4 の Step 2 と同じ）
- [ ] **Step 3: 上の動きのとおりに書く**
- [ ] **Step 4: 通ることを確かめる**（同じ）。`now-doing-todo-light.png` で、まだのことが入力欄の上に並び、今日の記録とボタンが重ならないことを見る。
- [ ] **Step 5: コミット** `git commit -m "Show the list script's まだのこと above the now-doing box"`

### Task 7: 2回押しのショートカット

**Files:**
- Create: `src/DoubleTap.cs`（`DoubleTapKey`・`TapKey`・`DoubleTapDetector`・`DoubleTapHook`・`ForegroundWindow`）
- Modify: `src/Hotkey.cs`、`src/NowDoing.cs`（`TapArgument`）、`src/NowDoingForm.cs`（`Run(bool fromTap)`・前に出す）、`src/Program.cs`、`src/TrayContext.cs`
- Test: `tests/NowDoingActionTests.cs`、`tests/NowDoingTests.cs`（ショートカットの読み取りの既存テストの横）、`tests/UiTests.cs`

**Interfaces:**
- Produces:
  - `internal enum DoubleTapKey { None, Ctrl, LeftCtrl, RightCtrl, Shift, LeftShift, RightShift }`、`internal enum TapKey { LeftCtrl, RightCtrl, LeftShift, RightShift, Other }`
  - `NowDoingHotkey` に `public DoubleTapKey Double;`。`Parse` は、文全体が6つの名前のどれか（大文字小文字は問わない）なら `Double` を入れ、`Modifiers=0`・`Key=Keys.None`・`Text` は書かれたままで返す。`DoubleAlt`・`DoubleWin`・`Double`・`DoubleCtrl+N` は null。
  - `NowDoingHotkey.Problem(text, parsed, registered)`: 書き方が違う →「settings.ini の now_doing_hotkey「X」の書き方が違います（例: Ctrl+Alt+N、DoubleCtrl）」。2回押しで `registered` が false →「「DoubleCtrl」を使えませんでした（キーボードの入力を受け取れません）」。普通の登録で false → 今の文。
  - `internal sealed class DoubleTapDetector { public const int MaxHoldMs = 300, MaxGapMs = 400; public DoubleTapDetector(DoubleTapKey key); public bool Observe(TapKey key, bool down, uint time); }` — 2回目を離したときに true。時刻の差は `unchecked(uint)` の引き算（約49日で一周しても正しい）。
    - 選んだキー（`Ctrl` は左右どちらも、`LeftCtrl` は左だけ…）以外のキーが押されたら数え直す。押している間に押されたら、その1回も数えない。選んだキー以外が離されたのは見ない。
    - 押している間の同じキーの押下（押しっぱなしの繰り返し）は数えない。選んだキーを押している間に、選んだもう一方のキー（左右）が押されたら、ほかのキーとして数え直す。
    - 1回目: 押してから離すまで300ミリ秒以内。2回目: 1回目を離してから押すまで400ミリ秒以内、押してから離すまで300ミリ秒以内。どちらも境目の値は含む。見つけたら数え直す（3回目は新しい1回目）。
  - `internal sealed class DoubleTapHook : IDisposable { public DoubleTapHook(DoubleTapKey key, Action pressed); public bool Installed { get; } public static TapKey Classify(int vk); }`
    - `Classify`: `0xA2`→LeftCtrl、`0xA3`→RightCtrl、`0xA0`→LeftShift、`0xA1`→RightShift、ほかは Other。
    - `SetWindowsHookEx(13, …, GetModuleHandle(null), 0)`。手続きの delegate はフィールドに持つ。`wParam` が `0x100`/`0x104` なら押下、`0x101`/`0x105` なら離し。`KBDLLHOOKSTRUCT` から読むのは `vkCode`（先頭）と `time`（12バイト目）だけ。見つけたら、作ったときの `SynchronizationContext` に `Post` して `pressed` を呼ぶ（フックの中で窓を開かない）。必ず `CallNextHookEx` の値を返す。
  - `internal static class ForegroundWindow { public static string Bring(IntPtr window, bool mayPressKey); }` — 1. `SetForegroundWindow`、前に出たら `"SetForegroundWindow"`。2. 前の窓のスレッドに `AttachThreadInput` して `SetForegroundWindow`・`BringWindowToTop`、外して確かめ、出たら `"AttachThreadInput"`。3. `mayPressKey` のときだけ、仮想キー `0xE8`（Microsoft の仮想キーの表で割り当てのないもの）の押下と離しを `SendInput` で送ってから `SetForegroundWindow`、出たら `"SendInput"`。どれでもだめなら `"none"`。結果を `OutputDebugString("AIUsageManager now-doing foreground: " + 結果)` で出す（ファイルには残さない）。
  - `NowDoingNote.TapArgument = "--now-doing-tap"`。`Program.Main` はどちらの引数でも `NowDoingForm.Run(args[0] == NowDoingNote.TapArgument)`。窓は `OnShown` で `ForegroundWindow.Bring(Handle, fromTap)` のあと入力欄にフォーカスする。
  - `TrayContext`: `hotkey.Double != None` なら `DoubleTapHook` を作り（`RegisterHotKey` は使わない）、`registered` は `Installed`。見つけたら `OpenNowDoing(true)` で `--now-doing-tap` を付けて起動する。終わるときに `Dispose`。

- [ ] **Step 1: 失敗するテストを書く**
  - 判定のテストは、`"L+0 L-100 L+500 L-600"` の形（L=左Ctrl・R=右Ctrl・S=左Shift・T=右Shift・X=ほかのキー、`+` は押下・`-` は離し、数字はミリ秒）を `Observe` に順に入れ、true になった回数を返す補助を作る。
  - `Check("Each double-press setting finds its own key pressed twice")`: `Ctrl`・`LeftCtrl` は `L+0 L-100 L+500 L-600` で1、`RightCtrl` は R で1、`Shift`・`LeftShift` は S で1、`RightShift` は T で1。`Ctrl` は S の列で0。
  - `Check("Presses up to 300 ms and a gap up to 400 ms count")`: `L+0 L-300 L+700 L-1000` は1。`L+0 L-301 L+701 L-801` は0。`L+0 L-100 L+501 L-600` は0。
  - `Check("Another key in between or while held starts again")`: `L+0 L-100 X+200 L+300 L-400` は0。`L+0 X+50 L-100 L+200 L-300 L+400 L-500` は1（Ctrl+C の1回は数えず、続く2回で見つける）。`DoubleShift` で `S+0 X+20 S-60 S+100 S-150` は0。
  - `Check("Key repeat while held counts as one press")`: `L+0 L+30 L+60 L-100 L+300 L+330 L-400` は1。
  - `Check("Left and right are told apart only when the setting says so")`: `Ctrl` で `L+0 L-100 R+200 R-300` は1。`LeftCtrl` で同じ列は0、`R+0 R-100 R+200 R-300` も0。
  - `Check("Three presses find it once, and the clock wrapping around does not matter")`: `L+0 L-50 L+100 L-150 L+200 L-250` は1。`L+4294967200 L-4294967250 L+4294967290 L-44` は1。
  - `Check("Only the left and right Ctrl and Shift keys are told apart")`: `Classify(0xA2)==LeftCtrl`・`0xA3`・`0xA0`・`0xA1`、`Classify(0x41)`・`Classify(0x11)`・`Classify(0x12)` は Other。
  - `tests/NowDoingTests.cs` の `Check("The shortcut key is read …")` の横に `Check("Double-press settings are read, and Alt or Win double presses are not")`: 6つの値（`doubleshift` も）で `Double` が入る。`DoubleAlt`・`DoubleWin`・`Double`・`DoubleCtrl+N` は null で、`Problem` が例に `DoubleCtrl` を含む文。`Problem("DoubleCtrl", parsed, false)=="「DoubleCtrl」を使えませんでした（キーボードの入力を受け取れません）"`。
  - UiTests `CheckHotkeyAndDrag` に: `new DoubleTapHook(DoubleTapKey.RightShift, () => {})` が `Installed`、`Dispose` のあと `Installed==false`、もう一度作れる。
- [ ] **Step 2: 失敗を確かめる**（`.\build.ps1 -NoPublish -OutputDirectory build\v2`。コンパイルが失敗する）
- [ ] **Step 3: 上の Interfaces どおりに書く**
- [ ] **Step 4: 通ることを確かめる**（同じ。FAIL が0行・最後に `Built …`）
- [ ] **Step 5: コミット** `git commit -m "Open the now-doing window with a double press of Ctrl or Shift"`

### Task 8: AI の完了を日中ログへ

**Files:**
- Create: `src/AiDone.cs`
- Modify: `src/Waiting.cs`（`WaitingItem.Cwd`）、`src/TrayContext.cs`、`src/Program.cs`
- Test: `tests/AiDoneTests.cs`（新規。`build.ps1` の一覧に足し、Main で `RunAiDoneTests()` を呼ぶ）

**Interfaces:**
- Consumes: `NowDoingNote.Write(..., mark:)`・`BackupPath(localAppData, name)`・`NotePath`（Task 3・既存）、`NowDoingSettings.AiDone`・`AiDoneExclude`（Task 1）、`WaitingList`・`WaitingItem`（既存）。
- Produces:
  - `WaitingItem` に `public string Cwd;`（`WaitingTracker.Add` で Claude は `session.Cwd`、Codex は `thread.Cwd`）。共有データ（`UsageSnapshot`）には入れない。
  - `internal sealed class AiDoneEntry { public DateTime Time; public string Text; }` — `Time` は地方時の分まで。`Text` は 🤖 を除いた本文。
  - `internal enum AiDoneWait { Write, Wait, GiveUp }`
  - `internal sealed class AiDoneLog { public const string Mark = "🤖"; public AiDoneLog(string[] exclude); public void Observe(WaitingItem[] items, DateTimeOffset now); public AiDoneEntry Due(DateTimeOffset now); public static bool Excluded(string cwd, string[] exclude); public static AiDoneWait Decide(DateTime lastWriteUtc, DateTime nowUtc, DateTime dueUtc); }`
    - `Observe`: `Reason==Finished` の項目のうち、(`Key`, `Since`) を初めて見たもの・除くフォルダーでないもの・同じ `Key` を前に書いた完了の時刻から30分以上たったもの（初めての `Key` も）を、まとめに入れ、その `Key` の時刻を `Since` にする。まとめが空だったら、その `now` をまとめの始まりにする。
    - `Due`: まとめの始まりから2分以上たっていたら、まとめを返して空にする。ほかは null。`Time` はまとめの中でいちばん早い `Since`。`Text` は1つなら「Claude 完了: <Place>」（`Place` が空なら「Claude 完了」）、2つ以上なら `Since` の順に「完了: Claude「A」・Codex「B」」（`Place` が空ならサービス名だけ）。
    - `Excluded`: `cwd` と各フォルダーを、`\\?\` を除き `Path.GetFullPath` で整え、末尾の `\` を除いて、大文字小文字を区別せずに、同じか `フォルダー + "\"` で始まるなら true。`cwd` が空なら false。
    - `Decide`: `nowUtc - dueUtc > 5分` なら GiveUp、`nowUtc - lastWriteUtc < 10秒` なら Wait、ほかは Write。
  - `TrayContext` のコンストラクターに `NowDoingSettings nowDoing = null` を足す（`Program` が `settings.ini` を1回読んで渡す。今の `nowDoingHotkey` もそこから取る）。`nowDoing.AiDone` のときだけ `AiDoneLog` を作る。`ReadWaiting` で一覧を作ったあと `Observe`。待っている書き込みがなければ `Due` を取り、`dueUtc` と「次に試す時刻」を持つ。次に試す時刻になったら: 日誌の場所（`NotePath(nowDoing.Note, entry.Time)`）がない・ファイルがない → 捨てる。`Decide` が Wait → 次に試す時刻を10秒後に。GiveUp → 捨てる。Write → `Task.Run` で `NowDoingNote.Write(nowDoing, entry.Time, entry.Text, BackupPath(localAppData, "ai-done-last.md"), null, AiDoneLog.Mark)`、結果にかかわらず捨てる。通知はしない。書いたあとのスクリプトは実行しない。

- [ ] **Step 1: 失敗するテストを書く**（`tests/AiDoneTests.cs`。`Finished(key, service, place, cwd, since)` の補助で `WaitingItem` を作る）
  - `Check("A finished session is written once, two minutes after it was seen, with the time it finished")`: 10:00:30 に終わった `Claude/a`（Place `AIUsageManager`）を 10:00:35 に `Observe` → `Due(10:02:34)` は null、`Due(10:02:35)` は `Time==10:00`・`Text=="Claude 完了: AIUsageManager"`、もう一度 `Due` は null。同じ項目をまた `Observe` しても増えない。
  - `Check("Finishes within two minutes become one line")`: Claude「A」が10:00、Codex「B」が10:01 → `"完了: Claude「A」・Codex「B」"`、`Time==10:00`。`Place` が空の Claude だけなら `"Claude 完了"`。
  - `Check("The same session is written at most once in 30 minutes")`: `Claude/a` が10:00に終わって書かれたあと、10:20（`Since` が変わる）は書かれない、10:31 は書かれる。
  - `Check("Waits and questions are not written")`: `Reason` が Question・Confirm の項目は `Due` が null のまま。
  - `Check("Sessions in excluded folders are not written")`: `Excluded(@"C:\Notes\Vault\.claude\worktrees\x", ex)`・`Excluded(@"C:\Notes\Vault", ex)`・`Excluded(@"c:\notes\vault\", ex)`・`Excluded(@"\\?\C:\Notes\Vault\crews", ex)` は true（`ex = {@"C:\Notes\Vault"}`）。`@"C:\Notes\Vault2"`・`null`・`""` は false。除くフォルダーの項目は `Observe` しても `Due` が null。
  - `Check("The diary is written only when it has been quiet for 10 seconds, and not after 5 minutes")`: `Decide(now-9s, now, now-1m)==Wait`、`Decide(now-10s, now, now-1m)==Write`、`Decide(now-1s, now, now-5m-1s)==GiveUp`、`Decide(now-60s, now, now-5m)==Write`。
- [ ] **Step 2: 失敗を確かめる**（`.\build.ps1 -TestsOnly -OutputDirectory build\v2`。コンパイルが失敗する）
- [ ] **Step 3: 上の Interfaces どおりに書く**
- [ ] **Step 4: 通ることを確かめる**（`.\build.ps1 -NoPublish -OutputDirectory build\v2`。トレイの UiTests も通る）
- [ ] **Step 5: コミット** `git commit -m "Write finished Claude Code and Codex work to the 日中ログ"`

### Task 9: 案内・プライバシーの文書と、全部の確認

**Files:**
- Modify: `docs/streamdeck-guide.md`（「いまやってること」キーの節と設定の表）、`docs/obsidian-guide.md`（Claudian に送る）、`docs/privacy.md`

- [ ] **Step 1: `docs/streamdeck-guide.md` を直す**: 入力欄（6行まで伸びる・貼り付け）、キーの表（Enter・Shift+Enter・Ctrl+Enter・Alt+1〜9・Esc・Ctrl+A）、200文字と4000文字、ボタンの書き方と3つの動きと例（spec の表）、書いたあとのスクリプト・一覧のスクリプト・copy に窓が足す引数と上限と失敗の表示、まだのこと、2回押しの6つの値と PowerToys の「マウスを探す」（左 Ctrl の2回押し）と重ならないものを選ぶこと、管理者として動いている窓が前にあると届かないこと、前に出せないときだけ割り当てのないキーを1つ送ること、AI の完了（形・2分・30分・除くフォルダー・10秒と5分）、設定の表に spec の7つのキー、窓に出る文の表に Task 4・5・6 の文。最後の「画面・キー入力…は読みません」の行は、2回押しの設定のときだけトレイアプリが左右の Ctrl・Shift の押し離しと時刻を見ること（ほかのキーは押されたことだけ）に直す。Obsidian プラグインは「1.3.0 以降」。
- [ ] **Step 2: `docs/obsidian-guide.md` を直す**: 送る前に、書いた日誌を今のタブで開く（保管庫の外・見つからないときは開かずに送る）。受け取る文は4000文字まで。
- [ ] **Step 3: `docs/privacy.md` を直す**: 新しい節「キーボードのフック（2回押しのショートカット）」— 見るもの（左右の Ctrl・Shift の押し離しと時刻、ほかのキーは押されたという事実だけ）、見ないもの（どのキーか・文字）、残さないこと（保存しない・送らない）、止めない・変えないこと、送るのは前に出せないときの割り当てのないキー1つだけ。節「いまやってること」— settings.ini に書かれた命令を、シェルを使わずに実行し、日誌の場所・時刻・入れた文を引数で渡すこと、出力は窓に出すかクリップボードに入れるだけで保存しないこと、AI の完了は対応待ちの一覧のサービス名・名前・時刻だけを日誌に書くこと（会話の内容は書かない）、`ai-done-last.md`。Obsidian の節の `claudian-message.json` に日誌の場所（`note`）が入ること。
- [ ] **Step 4: 全部を確かめる**  
  Run: `.\build.ps1 -NoPublish -OutputDirectory build\v2`、`node --test obsidian/tests/usage.test.cjs obsidian/tests/bridge.test.cjs obsidian/tests/toolbar.test.cjs obsidian/tests/claudian.test.cjs`、`node tools/build-obsidian.cjs`、`.\tools\package-obsidian.ps1 -SkipBuild`、`node --test streamdeck/tests/pipe.test.cjs streamdeck/tests/render.test.cjs streamdeck/tests/location.test.cjs streamdeck/tests/streamdeck.test.cjs streamdeck/tests/plugin.test.cjs streamdeck/tests/build.test.cjs`  
  Expected: すべて失敗0。`build\v2\screenshots\now-doing-*.png` をライト・ダークの両方で見て、重なり・はみ出しがないこと。
- [ ] **Step 5: コミット** `git commit -m "Document the second now-doing window: buttons, scripts, double press and AI done"`

### Task 10: 実機での確認（利用者と一緒に）

**Files:** spec の「未確認」と「実機での確認」に結果を書き足す。`docs/streamdeck-guide.md` に、前に出す方法の結果を書く。

- [ ] **Step 1:** 利用者の PC の、アプリのフォルダーの `settings.ini` に vault 側の仕様の設定（`now_doing_hotkey=DoubleCtrl` など）が入っていること、vault の `scripts/now_doing/` があることを利用者に確かめてもらう。ないものは、その項目を飛ばして結果に書く。
- [ ] **Step 2:** `OutputDebugString` を読む小さなプログラム（スクラッチパッドに置く。リポジトリには入れない）を動かしながら、spec の「実機での確認」1〜4を利用者に試してもらい、どの方法（`SetForegroundWindow`・`AttachThreadInput`・`SendInput`）で前に出たかを記録する。`DoubleShift` でかな・英数が変わらないかも見てもらう。
- [ ] **Step 3:** spec の 5〜9 を試してもらう。9 は Claude Code で短い作業を終わらせ、約2分後に 🤖 の行が入ること、除くフォルダーで動いたものは書かれないことを見る。
- [ ] **Step 4:** spec の「未確認」の4項目に結果を書き、案内に前に出す方法を書いて、コミットする。
- [ ] **Step 5:** PR を作り、いつもの手順（別エージェントのレビュー → 「マージ前レビュー」コミット → CI 緑 → `--merge`、ブランチは消さない）で進める。
