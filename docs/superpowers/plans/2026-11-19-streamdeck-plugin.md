# Stream Deck Plugin Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the third-party `kr.co.postgresql.ai-limits` Stream Deck plugin with our own plugin that displays the tray app's shared usage data and never calls any AI service.

**Architecture:** The tray app stays the only process that queries Claude/Codex/Grok. A dependency-free Node.js plugin, run by Stream Deck's bundled Node 24, reads the existing per-user named pipe every 15 s, interprets it with the existing `obsidian/src/usage.js` rules, and draws 144×144 SVG keys. To launch the tray app when it is not running, the plugin reads the executable path from a file the tray app writes under `%LOCALAPPDATA%`, never from the pipe.

**Tech Stack:** Node.js built-ins only (CommonJS, global `WebSocket`, `node:net`, `node:test`), Stream Deck SDK protocol v2 (raw WebSocket), .NET Framework 4.8 / C# 5 for the tray-app change, PowerShell 5.1 for packaging.

**Spec:** `docs/superpowers/specs/2026-11-19-streamdeck-plugin-design.md`

## Global Constraints

- The plugin connects only to `ws://127.0.0.1:<port>` (Stream Deck) and the named pipe. No `http`, `https`, `fetch`, `tls`, DNS, or other hosts. It never reads `.credentials.json`, `.codex/auth.json`, `.grok/auth.json`.
- Nothing received from the named pipe is used to decide what to execute.
- No npm dependencies, no `package.json`. Node built-ins only. Tests run with `node --test <files>` on Node ≥ 20; the plugin runs on Stream Deck's Node 24 (`"Nodejs": {"Version": "24"}`).
- Do not copy code, logos or images from the third-party plugin. No service logos; service names are drawn as text.
- Plugin UUID `io.github.genkis3zu.aiusagemanager`; action UUIDs `io.github.genkis3zu.aiusagemanager.claude`, `.codex`, `.grok`.
- Pipe name `\\.\pipe\AIUsageManager.Snapshot.<user SID>`; max 65536 bytes; read timeout 2000 ms; "absent" retry window 2000 ms with 250 ms between tries; poll interval 15000 ms while ≥ 1 key is visible; 2 consecutive failures → 未接続; `connected:false` or absent → 未接続 immediately.
- Long press: key held > 500 ms.
- Launch: no arguments, detached, no shell; re-poll 2000 ms after launch. "アプリの場所を設定" message shows for 5000 ms.
- Key text (exact): `未接続`, `読み込み中`, `未取得`, `要ログイン`, `アプリの場所を設定`.
- Key defaults: style `vertical`, metric `used`, showReset `true`, palette `orange` for Claude and `green` for Codex/Grok. Palette ids (exact, in cycle order): `green`, `orange`, `blue`, `violet`, `rose`, `slate`. Style cycle order: `ring` → `vertical` → `horizontal`.
- `%LOCALAPPDATA%\AIUsageManager\app-location.json` = `{"schemaVersion":1,"path":"<absolute path of AIUsageManager.exe>"}`; plugin reads at most 4096 bytes.
- Tray app code is C# 5 (no `$""`, `?.`, `nameof`), compiled by `build.ps1` with `/warnaserror`.
- Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Commit only when the user has approved committing for this execution.

## Review Focus

1. Stream Deck delivers `keyUp` without a matching `keyDown` (plugin restarted while a key was held) → treat as a short press, never crash. Test in Task 6.
2. `willAppear` arrives twice for the same context (page/profile switch) → one entry and one timer, not two. Test in Task 6.
3. The tray app just started and is still on its first refresh (`refreshing:true`, all percents null) → every key shows `未取得`, never `0%`. Test in Task 3.
4. Stream Deck closes the WebSocket (app quitting) → the plugin process exits once instead of looping. Test in Task 5.
5. A reset time already passed (PC resumed from sleep with an old snapshot) → the key is muted and shows `期限経過`, not the old percent as current. Test in Task 3.

## File Structure

| File | Responsibility |
|---|---|
| `src/AppLocation.cs` (new) | Write `app-location.json` atomically |
| `src/Program.cs` (modify) | Call `AppLocation.Save` in the primary instance |
| `streamdeck/src/pipe.js` | User SID, pipe name, bounded pipe read with absent/failed results |
| `streamdeck/src/state.js` | Connection status from consecutive read results |
| `streamdeck/src/settings.js` | Action ids, palettes, key-setting defaults/normalization/cycling |
| `streamdeck/src/render.js` | Key view model from snapshot + settings, SVG drawing |
| `streamdeck/src/streamdeck.js` | Launch-argument parsing, WebSocket registration, outgoing commands |
| `streamdeck/src/location.js` | Read `app-location.json`, choose manual vs recorded path, launch |
| `streamdeck/src/plugin.js` | Event handling, polling lifecycle, key presses, `main()` |
| `streamdeck/manifest.json`, `streamdeck/ui/settings.html`, `streamdeck/ui/settings.js`, `streamdeck/imgs/*.svg` | Stream Deck metadata, settings panel, static icons |
| `streamdeck/tests/*.test.cjs` | Unit tests per module |
| `tools/build-streamdeck.cjs` | Bundle into `build/streamdeck/io.github.genkis3zu.aiusagemanager.sdPlugin/` |
| `tools/package-streamdeck.ps1` | Allowlisted `.streamDeckPlugin` ZIP in `dist/` |

---

### Task 1: Tray app records its location

**Files:**
- Create: `src/AppLocation.cs`
- Modify: `src/Program.cs` (after `if(!created){...return;}`, before `Application.Run`)
- Test: `tests/SnapshotTests.cs` (inside `RunSnapshotTests`; add `using System.IO;`)
- Modify: `docs/privacy.md` (add one paragraph: the tray app saves its own executable path to `%LOCALAPPDATA%\AIUsageManager\app-location.json` so the Stream Deck plugin can start it; no usage data or credentials)

**Interfaces:**
- Produces: `public static class AppLocation { public static string DefaultPath(); public static bool Save(string file, string executablePath); }` — `DefaultPath()` = `Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AIUsageManager","app-location.json")`. `Save` creates the directory, writes `file+".tmp"`, then `File.Replace` / `File.Move` (same pattern as `ForecastHistory.Save`), returns `false` on `IOException`, `UnauthorizedAccessException`, `ArgumentException`, `NotSupportedException`.

- [ ] **Step 1: Write the failing tests**

```csharp
Check("App location is versioned JSON and the latest path wins",()=> {
    string dir=Path.Combine(Path.GetTempPath(),"aum-loc-"+Guid.NewGuid().ToString("N")),file=Path.Combine(dir,"app-location.json");
    try {
        Assert(AppLocation.Save(file,@"C:\Old\AIUsageManager.exe") && AppLocation.Save(file,@"D:\Apps\AIUsageManager.exe"));
        var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(file));
        Assert((int)data["schemaVersion"]==1 && (string)data["path"]==@"D:\Apps\AIUsageManager.exe" && data.Count==2 && !File.Exists(file+".tmp"));
    } finally {try{Directory.Delete(dir,true);}catch{}}
});
Check("App location failure is reported, not thrown",()=> {
    string blocker=Path.GetTempFileName();
    try {Assert(!AppLocation.Save(Path.Combine(blocker,"app-location.json"),@"C:\Apps\AIUsageManager.exe"));}
    finally {File.Delete(blocker);}
});
```

- [ ] **Step 2: Run, expect compile failure** — `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -TestsOnly -OutputDirectory build\test` → `Test compile failed` (`AppLocation` does not exist).
- [ ] **Step 3: Implement `AppLocation`** (serialize with `JavaScriptSerializer`) **and call `AppLocation.Save(AppLocation.DefaultPath(),Application.ExecutablePath);` in `Program.Main`**; ignore the return value.
- [ ] **Step 4: Run the same command** → all `PASS`, no `FAIL`, exit code 0.
- [ ] **Step 5: Commit** — `git add src/AppLocation.cs src/Program.cs tests/SnapshotTests.cs docs/privacy.md` / `git commit -m "Record the tray app location for the Stream Deck plugin"`

---

### Task 2: Bounded named-pipe reader

**Files:**
- Create: `streamdeck/src/pipe.js`
- Test: `streamdeck/tests/pipe.test.cjs`

**Interfaces:**
- Produces:
  - `userSid(exec = require('node:child_process').execFileSync) → string|null` — runs `exec('whoami.exe',['/user','/fo','csv','/nh'],{encoding:'utf8',windowsHide:true,timeout:5000})`, returns the second CSV field if it matches `/^S-1-\d+(-\d+)+$/`, else `null` (also `null` if `exec` throws).
  - `pipeName(sid) → string|null` — `'\\\\.\\pipe\\AIUsageManager.Snapshot.' + sid`, or `null` when `sid` is null.
  - `readSnapshot(name, options = {}) → Promise<{kind:'ok', json:string} | {kind:'absent'} | {kind:'failed'}>` — options `{connect = net.connect, retryMs = 2000, retryDelayMs = 250, timeoutMs = 2000, maxBytes = 65536}`. Never rejects. `name === null` → `absent`. Error code `ENOENT` → retry until `retryMs` after the first try, then `absent`. Any other error, no `end` within `timeoutMs` of a connect attempt, more than `maxBytes`, empty output, or invalid UTF-8 (`new TextDecoder('utf-8',{fatal:true})`) → `failed`. Always destroys the socket.

- [ ] **Step 1: Write the failing tests** — use a real `net.createServer` listening on `\\.\pipe\AIUsageManager.StreamDeckTest.<pid>.<random>`, closed after each test:
  - `reads one snapshot` → server writes `'{"a":1}'` and ends → `{kind:'ok', json:'{"a":1}'}`.
  - `a pipe that appears within the retry window is read` → start the server 600 ms after calling `readSnapshot` → `kind === 'ok'`.
  - `a pipe that never appears is absent after the window` → `retryMs:500` → `kind === 'absent'` and elapsed ≥ 500 ms.
  - `a server that never ends is failed` → server writes nothing, `timeoutMs:300` → `kind === 'failed'`.
  - `more than 64 KiB is failed` → 65537 bytes → `failed`; `invalid UTF-8 is failed` → `Buffer.from([0xff])` → `failed`; `empty output is failed`.
  - `null name is absent without connecting` → fake `connect` that throws if called.
  - `userSid parses whoami CSV` → fake exec returning `'"pc\\user","S-1-5-21-1-2-3-1001"\r\n'` → `'S-1-5-21-1-2-3-1001'`; returns `null` for `'"pc\\user","bad"'` and when exec throws.
- [ ] **Step 2: Run** `node --test streamdeck/tests/pipe.test.cjs` → FAIL (`Cannot find module '../src/pipe'`).
- [ ] **Step 3: Implement `streamdeck/src/pipe.js`** per the interface.
- [ ] **Step 4: Run** the same command → all pass.
- [ ] **Step 5: Commit** — `git add streamdeck/src/pipe.js streamdeck/tests/pipe.test.cjs` / `git commit -m "Add a bounded snapshot pipe reader for Stream Deck"`

---

### Task 3: Connection status, settings and key rendering

**Files:**
- Create: `streamdeck/src/state.js`, `streamdeck/src/settings.js`, `streamdeck/src/render.js`
- Test: `streamdeck/tests/render.test.cjs`

**Interfaces:**
- Consumes: `parseSnapshot`, `presentService` from `require('../../obsidian/src/usage')` (use exactly this module id; the build maps it).
- Produces:
  - `state.js`: `initialConnection() → {status:'loading', snapshot:null, failures:0}`; `nextConnection(prev, result) → {status:'loading'|'connected'|'disconnected', snapshot, failures}`. `ok` + `parseSnapshot` succeeds + `connected:true` → `connected`, failures 0. `ok` + `connected:false`, or `absent` → `disconnected`, snapshot `null`, failures 0. `failed`, or `parseSnapshot` throws → failures+1; at 2 → `disconnected`/`null`; otherwise keep `prev.status` and `prev.snapshot`.
  - `settings.js`: `ACTIONS` (`{ '<uuid>.claude':'Claude', '<uuid>.codex':'Codex', '<uuid>.grok':'Grok' }`), `PALETTES` (array of `{id, label, background, track, text, muted, accent, warning, critical}` in the constraint order; `label`s: 緑, オレンジ, 青, 紫, ピンク, グレー; colour values are the implementer's), `STYLES`, `defaultSettings(service)`, `normalizeKeySettings(service, raw) → {style, metric, showReset, palette}` (unknown values → defaults; `showReset` accepts booleans and `'true'`/`'false'`), `nextStyle(style)`, `nextPalette(id)`, `paletteFor(id)`.
  - `render.js`: `keyView(name, connection, settings, now) → {title, kind:'loading'|'message'|'usage', message, caption, tone:'normal'|'muted'|'warning'|'critical', bars:[{label, value, text, reset}]}` where `name` is `'Claude'|'Codex'|'Grok'` and the service is `connection.snapshot.services.find(s => s.name === name)`; and `renderKey(view, settings) → string` (144×144 SVG, `xmlns` set, all text XML-escaped, no `<image>`, no `href`).
    - `loading` → `kind:'loading'`, message `読み込み中`. `disconnected` → `kind:'message'`, message `未接続`.
    - Connected: `p = presentService(service, true, now)`. Every percent `null` → `kind:'message'`, message `未取得` (or `要ログイン` when `needsLogin`). Otherwise `kind:'usage'`, one bar per `p.windows` (Claude `5時間` then `週`; Codex/Grok `週`); `value` = percent (`used`) or `Math.max(0, 100 - percent)` (`remaining`), `null` stays `null`; `text` = `未取得` for null, else `Math.round(value) + '%'`; `reset` = `p.windows[i].reset` when `showReset`, else `''`. `tone` = `p.severity`. `caption` = `要ログイン` when `needsLogin`, else (tone `muted`) `p.state` cut to 9 characters plus `…` when longer, else `''`.
    - `renderKey` draws `ring`, `vertical` or `horizontal` with the palette from `settings.palette`; `tone` selects `accent`/`muted`/`warning`/`critical` for bars and percent text.

- [ ] **Step 1: Write the failing tests** — helper `snap({claude:[p5, pw], codex:pw, grok:pw, recordedAt, source:'オンライン', status:'', needsLogin:false, refreshing:false})` builds schemaVersion-1 JSON that `parseSnapshot` accepts (exactly one service per name; window labels `5時間`+`週` for Claude, `週` for the others); `now = Date.parse(recordedAt) + 60000`.
  - `missing and zero stay distinct` → Codex percent `null` → bar text `未取得`; `0` → `0%`.
  - `remaining inverts used and never goes negative` → 30 → `70%`; 120 → `0%`.
  - `Claude has 5時間 and 週 bars, Codex and Grok have 週` (labels in that order).
  - `first refresh shows 未取得 on every key` (Review Focus 3) → `refreshing:true`, all null → each service `kind:'message'`, message `未取得`.
  - `value older than 15 minutes is muted with a caption` → `now = recordedAt + 20 min` → tone `muted`, caption `保存された値`.
  - `passed reset is muted and shows 期限経過` (Review Focus 5) → `resetsAt` 1 min before `now` → tone `muted`, bar `reset === '期限経過'`.
  - `fresh 92% is warning and 96% is critical`.
  - `needsLogin Grok shows 要ログイン`.
  - `showReset false hides reset text`.
  - `loading and disconnected messages` → `読み込み中`, `未接続`.
  - `state: one failure keeps last values; two disconnect; success resets; invalid JSON counts as failure; connected:false and absent disconnect at once`.
  - `settings: unknown values fall back to defaults; Claude defaults to orange, others green; style and palette cycle in the constraint order and wrap`.
  - `renderKey: every style returns a 144x144 svg without image or href; caption '<b>&' is escaped as '&lt;b&gt;&amp;'`.
- [ ] **Step 2: Run** `node --test streamdeck/tests/render.test.cjs` → FAIL (modules missing).
- [ ] **Step 3: Implement `state.js`, `settings.js`, `render.js`** per the interfaces. Layout for `vertical`: service name top-left, bars side by side in the middle, percent text under each bar, reset text at the bottom; `ring` and `horizontal` show the same fields.
- [ ] **Step 4: Run** the same command → all pass.
- [ ] **Step 5: Commit** — `git add streamdeck/src/state.js streamdeck/src/settings.js streamdeck/src/render.js streamdeck/tests/render.test.cjs` / `git commit -m "Render Stream Deck keys from the shared snapshot"`

---

### Task 4: App location and launch

**Files:**
- Create: `streamdeck/src/location.js`
- Test: `streamdeck/tests/location.test.cjs`

**Interfaces:**
- Consumes: `validPath` from `require('../../obsidian/src/usage')`.
- Produces:
  - `locationFile(env = process.env) → string|null` — `path.win32.join(env.LOCALAPPDATA,'AIUsageManager','app-location.json')`; `null` unless `LOCALAPPDATA` is a drive-absolute path.
  - `readAppLocation(file, fs = require('node:fs')) → string|null` — `null` when the file is missing, larger than 4096 bytes, not JSON, `schemaVersion !== 1`, `!validPath(path)`, or `path` is not an existing file.
  - `resolveAppPath(manualPath, file, fs) → string|null` — manual path when `validPath` and an existing file; otherwise `readAppLocation(file, fs)`.
  - `launchApp(file, spawn = require('node:child_process').spawn) → Promise<void>` — rejects without spawning unless `validPath(file)`; `spawn(file, [], {shell:false, detached:true, stdio:'ignore', windowsHide:false})`, `unref()` on `'spawn'` then resolve; reject on `'error'`.

- [ ] **Step 1: Write the failing tests** (temp dir with a real `AIUsageManager.exe` placeholder file):
  - `recorded path is used when no manual path is set`.
  - `manual path wins over the recorded path`; `invalid or missing manual path falls back to the recorded path`.
  - `missing, oversized (4097 bytes), non-JSON, schemaVersion 2, wrong basename (C:\\x\\cmd.exe), and nonexistent paths give null`.
  - `launch passes no arguments, detached, no shell` (fake spawn records arguments, emits `'spawn'`).
  - `invalid paths never spawn` (`'cmd /c x'`, `'\\\\server\\share\\AIUsageManager.exe'`).
- [ ] **Step 2: Run** `node --test streamdeck/tests/location.test.cjs` → FAIL.
- [ ] **Step 3: Implement `streamdeck/src/location.js`**.
- [ ] **Step 4: Run** → all pass.
- [ ] **Step 5: Commit** — `git add streamdeck/src/location.js streamdeck/tests/location.test.cjs` / `git commit -m "Resolve and launch the tray app from the Stream Deck plugin"`

---

### Task 5: Stream Deck connection

**Files:**
- Create: `streamdeck/src/streamdeck.js`
- Test: `streamdeck/tests/streamdeck.test.cjs`

**Interfaces:**
- Produces:
  - `parseArgs(argv) → {port, pluginUUID, registerEvent, info}` — reads `-port`, `-pluginUUID`, `-registerEvent`, `-info` (JSON); throws unless port is an integer 1–65535 and the strings are non-empty.
  - `class StreamDeck extends EventEmitter` — `constructor(args, {createSocket = url => new WebSocket(url), exit = () => process.exit(0)} = {})`; `connect()` opens `ws://127.0.0.1:<port>`, on open sends `{event: registerEvent, uuid: pluginUUID}` then flushes queued commands; each incoming message is JSON-parsed and emitted under its `event` name (invalid JSON ignored); on close calls `exit` once.
  - Commands: `setImage(context, svg)` → `{event:'setImage', context, payload:{image:'data:image/svg+xml;base64,' + base64(svg), target:0}}`; `setSettings(context, settings)`; `getGlobalSettings()` → `{event:'getGlobalSettings', context: pluginUUID}`; `showAlert(context)`.

- [ ] **Step 1: Write the failing tests** (fake socket with `send`, `onopen`, `onmessage`, `onclose`):
  - `parseArgs reads the four launch arguments` from `['-port','28196','-pluginUUID','abc','-registerEvent','registerPlugin','-info','{"application":{}}']`; throws for port `'0'`, `'x'`, missing `-pluginUUID`.
  - `registers first, then flushes commands sent before open`.
  - `setImage sends a base64 SVG data URL`.
  - `incoming events are emitted by name and bad JSON is ignored`.
  - `socket close exits once` (Review Focus 4) — trigger `onclose` twice → `exit` called once.
- [ ] **Step 2: Run** `node --test streamdeck/tests/streamdeck.test.cjs` → FAIL.
- [ ] **Step 3: Implement `streamdeck/src/streamdeck.js`**.
- [ ] **Step 4: Run** → all pass.
- [ ] **Step 5: Commit** — `git add streamdeck/src/streamdeck.js streamdeck/tests/streamdeck.test.cjs` / `git commit -m "Connect the plugin to Stream Deck without the SDK package"`

---

### Task 6: Plugin behaviour, manifest, settings panel and build

**Files:**
- Create: `streamdeck/src/plugin.js`, `streamdeck/manifest.json`, `streamdeck/ui/settings.html`, `streamdeck/ui/settings.js`, `streamdeck/imgs/category.svg`, `streamdeck/imgs/action.svg`, `streamdeck/imgs/key.svg`, `tools/build-streamdeck.cjs`
- Test: `streamdeck/tests/plugin.test.cjs`, `streamdeck/tests/build.test.cjs`

**Interfaces:**
- Consumes: everything from Tasks 2–5.
- Produces:
  - `createPlugin({deck, pipe, readSnapshot, resolveAppPath, launchApp, now, timers}) → {handle(message), poll()}` — `timers = {setInterval, clearInterval, setTimeout}`; `deck` provides `setImage`, `setSettings`, `showAlert`; the injected `resolveAppPath` takes only `(manualPath)` (`main` binds it as `manual => location.resolveAppPath(manual, location.locationFile())`).
    - `willAppear`: store `{service: ACTIONS[action], settings: normalizeKeySettings(...)}` by `context` (replace if present); render; if it is the first visible key, `poll()` and start a 15000 ms interval.
    - `willDisappear`: remove; when none remain, clear the interval.
    - `didReceiveSettings`: replace settings, render. `didReceiveGlobalSettings`: keep `payload.settings.appPath` as the manual path.
    - `keyDown`: record time. `keyUp`: held > 500 ms = long, missing `keyDown` = short. Status not `disconnected`: short → `nextStyle`, long → `nextPalette`, then `setSettings` and render. Status `disconnected`: `resolveAppPath(manualPath)`; `null` or launch rejected → show `アプリの場所を設定` on that key for 5000 ms and `showAlert`; success → `setTimeout(poll, 2000)`.
    - `poll()`: one read at a time (concurrent calls share it); `connection = nextConnection(connection, await readSnapshot(pipe))`; render every key.
    - Render sends `setImage` only when the SVG differs from the last one sent for that context.
  - `main()`: `parseArgs(process.argv.slice(2))`, `pipeName(userSid())`, `new StreamDeck(args)`, forward every event to `handle`, `connect()`, `getGlobalSettings()`. `plugin.js` exports `{createPlugin, main}` and never calls `main` itself; the bundle wrapper calls it (see build script), because inside the bundle each module gets a stand-in `module` object.
  - `manifest.json`: `Name` "AI Usage Manager", `Version` "1.1.0.0", `Author` "AI Usage Manager contributors", `UUID` per constraints, `CodePath` "bin/plugin.cjs", `Nodejs` `{"Version":"24"}`, `SDKVersion` 2, `Software.MinimumVersion` "7.6", `OS` windows ≥ 10, `Category` "AI Usage Manager", `CategoryIcon` "imgs/category", `Icon` "imgs/plugin". Three actions: `Name` "Claude 使用量" / "Codex 使用量" / "Grok 使用量", `Icon` "imgs/action", `Controllers` ["Keypad"], `PropertyInspectorPath` "ui/settings.html", `SupportedInMultiActions` false, `UserTitleEnabled` false, `States` [{"Image":"imgs/key","ShowTitle":false}].
  - Settings panel: defines `window.connectElgatoStreamDeckSocket(port, uuid, registerEvent, info, actionInfo)`; controls 表示スタイル (リング／縦バー／横バー), 数値 (使用率／残り), リセットまでの時間 (checkbox), 色 (palette labels), トレイアプリの場所 (text, all keys). Key settings → `setSettings`; app path → `setGlobalSettings` after `getGlobalSettings` on load. No external resources.
  - `tools/build-streamdeck.cjs`: same bundling pattern as `tools/build-obsidian.cjs`; module ids `./pipe`, `./state`, `./settings`, `./render`, `./location`, `./streamdeck`, `./plugin` from `streamdeck/src`, plus `../../obsidian/src/usage` from `obsidian/src/usage.js`; the wrapper ends with `const plugin = localRequire('./plugin'); module.exports = plugin; if (require.main === module) plugin.main();` using the real outer `require`/`module`. Output `build/streamdeck/io.github.genkis3zu.aiusagemanager.sdPlugin/` with `bin/plugin.cjs`, `manifest.json`, `ui/`, `imgs/`, `imgs/plugin.svg` (copied from `assets/app.svg`), `LICENSE`.

- [ ] **Step 1: Write the failing tests** — `plugin.test.cjs` with fake deck, fake timers and a scripted `readSnapshot`:
  - `first key polls immediately and starts one 15 s timer`; `a second willAppear for the same context keeps one entry and one timer` (Review Focus 2); `last key disappearing clears the timer`.
  - `overlapping polls share one read`.
  - `short press cycles style and saves; long press (600 ms) cycles palette`; `keyUp without keyDown is a short press` (Review Focus 1).
  - `disconnected press launches the resolved path and polls again after 2 s`; `disconnected press without a path shows アプリの場所を設定 and alerts, without launching`; `manual appPath from global settings reaches resolveAppPath`.
  - `identical images are not resent`.
  - `build.test.cjs`: runs `node tools/build-streamdeck.cjs`, then asserts the output contains the files above; `manifest.json` parses with the constraint values; requiring the bundle exports `createPlugin` without connecting; the bundle text contains no `http://`, `https://`, `node:http`, `node:https`, `node:tls`, `fetch(`, `.credentials.json`, `auth.json`.
- [ ] **Step 2: Run** `node --test streamdeck/tests/plugin.test.cjs streamdeck/tests/build.test.cjs` → FAIL.
- [ ] **Step 3: Implement `plugin.js`, manifest, settings panel, icons and `tools/build-streamdeck.cjs`.** Icons are simple original SVGs (text or bars, no service logos).
- [ ] **Step 4: Run** `node --test streamdeck/tests/pipe.test.cjs streamdeck/tests/render.test.cjs streamdeck/tests/location.test.cjs streamdeck/tests/streamdeck.test.cjs streamdeck/tests/plugin.test.cjs streamdeck/tests/build.test.cjs` → all pass.
- [ ] **Step 5: Commit** — `git add streamdeck tools/build-streamdeck.cjs` / `git commit -m "Add the Stream Deck plugin that shows tray app usage"`

---

### Task 7: Package, CI and user guide

**Files:**
- Create: `tools/package-streamdeck.ps1`, `docs/streamdeck-guide.md`
- Modify: `.github/workflows/build.yml` (`build/` and `dist/` are already in `.gitignore`), `README.md` (できること bullet, スクリーンショット-free short section linking the guide, はじめる table row for main・1.1開発版), `docs/privacy.md` (Stream Deck section like the Obsidian one: reads the pipe every 15 s while keys are visible, no service queries, stores only key display settings and the optional manual app path in Stream Deck's settings), `docs/design.md` (table rows for `AppLocation.cs` and `streamdeck/src/`)

**Interfaces:**
- Consumes: `build/streamdeck/io.github.genkis3zu.aiusagemanager.sdPlugin/` from Task 6.
- Produces: `dist/AIUsageManager-1.1.0-streamdeck.streamDeckPlugin` — ZIP whose entries all start with `io.github.genkis3zu.aiusagemanager.sdPlugin/` and use `/` separators (write entries with `System.IO.Compression.ZipArchive`, not `Compress-Archive`). `param([switch]$SkipBuild)`; version from `manifest.json` must match `^\d+\.\d+\.\d+\.\d+$` and the file name uses its first three parts. Explicit allowlist: `manifest.json`, `bin/plugin.cjs`, `ui/settings.html`, `ui/settings.js`, `imgs/plugin.svg`, `imgs/category.svg`, `imgs/action.svg`, `imgs/key.svg`, `LICENSE`. After writing, re-open the ZIP and throw unless the entry list equals the allowlist exactly.

- [ ] **Step 1: Run the packaging check before the script exists** — `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\package-streamdeck.ps1` → fails (script not found).
- [ ] **Step 2: Write `tools/package-streamdeck.ps1`.**
- [ ] **Step 3: Run it** → prints the package path; exit 0. Then add an unexpected file to the build folder, run with `-SkipBuild` → throws; remove the file.
- [ ] **Step 4: Add a CI step** after the Obsidian steps:

```yaml
      - name: Test, build and package Stream Deck plugin
        shell: powershell
        run: |
          node --test streamdeck/tests/pipe.test.cjs streamdeck/tests/render.test.cjs streamdeck/tests/location.test.cjs streamdeck/tests/streamdeck.test.cjs streamdeck/tests/plugin.test.cjs streamdeck/tests/build.test.cjs
          if ($LASTEXITCODE -ne 0) { throw 'Stream Deck tests failed' }
          .\tools\package-streamdeck.ps1
```

- [ ] **Step 5: Write `docs/streamdeck-guide.md`** (Japanese): 必要なもの (Windows, Stream Deck 7.6以降, same-source tray app build), 導入 (double-click the package), 既存プラグインの削除 (why: the retry problem and HTTP 429, in plain words), キーの置き直し, 表示と操作 (styles, metric, reset time, colours, short/long press), 未接続のとき (about 17 s after the tray app exits; press to start; アプリの場所を設定), プライバシー link. Update README, privacy, design as listed above. Follow the user's writing rule: no invented terms, one wording per concept.
- [ ] **Step 6: Run** `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -NoPublish -OutputDirectory build\verify` and the CI step's commands locally → all pass.
- [ ] **Step 7: Commit** — `git add tools/package-streamdeck.ps1 .github/workflows/build.yml docs README.md` / `git commit -m "Package the Stream Deck plugin and document setup"`

---

### Task 8: Check on the real Stream Deck

No code. Record results in the final report; fix failures in the task that owns the code.

- [ ] **Step 1:** Build the tray app (`build.ps1`) and start it. Confirm `%LOCALAPPDATA%\AIUsageManager\app-location.json` holds its path.
- [ ] **Step 2:** Ask the user to remove `kr.co.postgresql.ai-limits` in the Stream Deck app (do not delete it yourself), then install `dist/AIUsageManager-1.1.0-streamdeck.streamDeckPlugin` and place the three keys.
- [ ] **Step 3:** Keys show values matching the tray popup; the default look is vertical bars / used / reset time / orange Claude, green Codex and Grok; plugin and key icons are visible (if the SVG plugin icon is blank, switch `Icon` to a PNG made from `assets/app.png` and note it).
- [ ] **Step 4:** Short and long presses cycle style and colour; settings survive a Stream Deck restart; the settings panel changes apply.
- [ ] **Step 5:** Exit the tray app → `未接続` within about 17 s. Press → the tray app starts and values return. Quit Stream Deck → the started tray app keeps running.
- [ ] **Step 6:** With Obsidian also running, watch for 10 minutes: no `未接続`.
- [ ] **Step 7:** After the old plugin is gone, the tray app fetches Claude again (the rate limit may take time to clear; report the time observed).
