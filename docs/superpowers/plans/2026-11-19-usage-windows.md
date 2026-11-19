# Usage Windows Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show every usage window each service actually returns (Claude 5-hour only when present, Claude weekly per model such as Fable, Codex 5-hour on Plus), name Claude plans Ultra/Max, and keep alerts, forecasts and every display working with a variable number of windows.

**Architecture:** `UsageWindow` gains a span (5 hours / week) and an optional model scope; labels derive from them. Parsers build only the windows the response contains (Claude from the `limits` array, filled in from `five_hour`/`seven_day`). Alerts and forecasts choose rules by span. The shared snapshot becomes schemaVersion 2 with 0–4 windows per service, consumed by Obsidian and Stream Deck through `obsidian/src/usage.js`. Forecast history moves to a new file that is seeded from the old one.

**Tech Stack:** .NET Framework 4.8 / C# 5 (`build.ps1`, csc), WinForms, Node.js built-ins (`node --test`), PowerShell 5.1.

**Spec:** `docs/superpowers/specs/2026-11-19-usage-windows-design.md`

## Global Constraints

- C# 5 only (no `$""`, `?.`, `nameof`, expression-bodied members); `build.ps1` compiles with `/warnaserror`. No npm dependencies.
- Labels (exact): `5時間`, `週`, `週（<model>）`; short labels: `5h`, `週`, `<model>`. Unnamed model fallback label `週（モデル別）`, short `モデル別`.
- Model name cleaning: keep only `[A-Za-z0-9 .+\-()]`, collapse runs of spaces, trim, cut to 24 characters; empty result → unnamed fallback. A window is never dropped because of its name.
- Window order: 5 hours, week, then scoped weeks in response order. At most 4 windows per service. Stream Deck draws at most 3.
- Claude `limits` kinds: `session` → 5 hours; `weekly_all` → week; `weekly_scoped` → week with `scope.model.display_name`. Unknown kinds, invalid `percent`, and `is_active`/`group`/`severity` are ignored. Duplicate (span, scope) → keep the higher percent.
- Claude legacy fill-in: if `limits` gave no 5-hour window and `five_hour` has a valid percent, add it; same for week with `seven_day`. Percent fields: `percent`, else `utilization`, else `used_percentage`; range 0–100.
- Codex: `limit_window_seconds` 18000 → 5 hours, 604800 → week (online); `window_minutes` 300 → 5 hours, 10080 → week (session log). Other windows and `code_review_rate_limit`/`additional_rate_limits` are ignored.
- Plan names: `max` + tier containing `20x` → `Ultra`; other `max` → `Max`; `ultra` → `Ultra`. History migration: `Max 20x` → `Ultra`, `Max 5x` → `Max`.
- Forecast history: new file `%LOCALAPPDATA%\AIUsageManager\forecast-history-2.dat` (version 2, ≤ 12 series). If it does not exist, load the old `forecast-history.dat` (version 1, ≤ 4 series) read-only. Never write the old file. Clear deletes both.
- Snapshot: `schemaVersion` 2; each window `{label, shortLabel, span: "5h"|"week", percent, resetsAt, pace, alert}`; ≤ 64 KiB. Clients accept only version 2.
- `ServiceUsage.Empty(name, status)` creates exactly one placeholder window: `週`, percent null (see Ruling in the ledger; every service has a weekly limit).
- No real usage values in tests or docs. Commits only with the user's approval; messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. Ultra account (no `session`, `five_hour` null) with an old forecast history containing `Claude/5時間` → no 5時間 window appears anywhere, and the old `Claude/週` series keeps learning. Test in Task 3.
2. A response where `limits` is present but has only unknown kinds → legacy `five_hour`/`seven_day` still produce windows. Test in Task 1.
3. Window labels change between refreshes (e.g. Fable appears after a reset) → Obsidian rebuilds that row's window slots instead of writing into stale ones. Test in Task 4.
4. The old build runs after the new one has saved history → the old `forecast-history.dat` is untouched (byte-identical). Test in Task 3.
5. A Claude key with three windows (Max: 5時間, 週, Fable) → all three readable on Stream Deck in every style; a fourth window is not drawn. Test in Task 6.

## File Structure

| File | Change |
|---|---|
| `src/Models.cs` | `UsageSpan`, window span/scope/labels, factory methods, `Empty` placeholder |
| `src/Parsers.cs` | Claude `limits` + legacy fill-in, Codex 5h+week (online, log), plan names |
| `src/Providers.cs`, `src/GrokWebBilling.cs` | adapt to `Empty` change only if needed |
| `src/Alerts.cs`, `src/Forecast.cs`, `src/Pace.cs` | rules by span |
| `src/ForecastHistory.cs` | v2 file, legacy seed, key pattern, plan migration |
| `src/Snapshot.cs` | schemaVersion 2 fields |
| `src/PopupForm.cs`, `src/WidgetCards.cs` | variable window layout, short labels |
| `obsidian/src/usage.js`, `obsidian/src/toolbar.js` | parse v2, dynamic slots |
| `streamdeck/src/render.js` | short labels from data, up to 3 bars |
| `tests/*.cs`, `tests/Diagnostics.cs`, `obsidian/tests/usage.test.cjs`, `streamdeck/tests/*.test.cjs` | updated fixtures and new tests |
| `README.md`, `docs/*.md` | window descriptions, Grok Bot pool, sources |

---

### Task 1: Window model and Claude windows

**Files:** Modify `src/Models.cs`, `src/Parsers.cs`, `src/WidgetCards.cs` (`Sample` only), `tests/Diagnostics.cs`; Test `tests/Tests.cs` (+ fix existing Claude index users in `tests/AlertTests.cs`, `tests/WidgetTests.cs`, `tests/SnapshotTests.cs` as needed to compile).

**Interfaces:**
- Produces (`src/Models.cs`):
  - `public enum UsageSpan { FiveHour, Week }`
  - `UsageWindow` fields `Span`, `Scope` (string or null); read-only properties `Label`, `ShortLabel`; factories `static UsageWindow FiveHour(double? percent, DateTimeOffset? resetsAt)`, `static UsageWindow Week(double? percent, DateTimeOffset? resetsAt)`, `static UsageWindow Scoped(string rawModelName, double? percent, DateTimeOffset? resetsAt)`; `static string CleanModelName(string raw)` (returns `null` for the unnamed fallback).
  - `ServiceUsage.Empty(name, status)` → one `Week(null, null)` window.
- Produces (`src/Parsers.cs`): `UsageParser.Claude(json, plan, time, source)` with the constraints' window rules; `Json.Plan` with Ultra/Max.

- [ ] **Step 1: Write failing tests in `tests/Tests.cs`** (fixture JSON is fictional):
  - `Claude Ultra limits give 週 and 週（Fable） without 5時間` — `{"five_hour":null,"seven_day":{"utilization":40,"resets_at":"2026-11-22T23:00:00.000+00:00"},"limits":[{"kind":"weekly_all","percent":40,"resets_at":"…"},{"kind":"weekly_scoped","percent":70,"resets_at":"…","scope":{"model":{"display_name":"Fable"}}}]}` → labels `["週","週（Fable）"]`, short labels `["週","Fable"]`, percents 40/70, spans Week/Week.
  - `Claude Max limits give 5時間, 週 and 週（Fable） in order` — session listed last in the array → order still 5時間, 週, 週（Fable）.
  - `Claude limits with only unknown kinds fall back to legacy fields` — `limits:[{"kind":"future_kind","percent":5}]`, `five_hour` 10, `seven_day` 20 → `["5時間","週"]`.
  - `Claude legacy five_hour fills a missing session window` — limits has `weekly_all` only, `five_hour.utilization` 12 → `["5時間","週"]`, 5時間 = 12.
  - `Claude duplicate windows keep the higher percent` — two `weekly_all` 30 and 55 → one 週 = 55.
  - `Claude model names are cleaned, never dropped` — `"Fable\u0007 (Preview) <b>"` → `週（Fable (Preview) b）`; `"\u0001\u0002"` → `週（モデル別）`; 40-char name → cut to 24.
  - `Claude more than four windows keep the first four` — session, weekly_all, three scoped → 4 windows.
  - `Claude statusline cache keeps working` — existing `rate_limits.five_hour.used_percentage` test still gives 5時間 (keep test, adjust indices to labels).
  - `Claude response without any window keeps one unknown weekly placeholder` — `{}` → one window `週`, percent null, `HasData == false`.
  - `Claude plan names follow Ultra and Max` — `Json.Plan("max","default_claude_max_20x")=="Ultra"`, `Json.Plan("max","default_claude_max_5x")=="Max"`, `Json.Plan("max",null)=="Max"`, `Json.Plan("ultra",null)=="Ultra"`.
- [ ] **Step 2: Run** `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -TestsOnly -OutputDirectory build\test` → compile failure (`UsageSpan` missing).
- [ ] **Step 3: Implement** the model and Claude parser; update existing tests/`Diagnostics.cs`/`WidgetCards.Sample` that index Claude windows (build windows explicitly with the factories; `Sample` keeps Claude 5時間 5% + 週 82%).
- [ ] **Step 4: Run** the same command → `RESULT N passed, 0 failed`. Also compile the app: `build.ps1 -NoPublish -OutputDirectory build\verify` → `Built …AIUsageManager.exe`, UI checks pass.
- [ ] **Step 5: Commit** (if approved) — `Show the Claude usage windows the service returns`.

---

### Task 2: Codex 5-hour and weekly windows

**Files:** Modify `src/Parsers.cs` (`Codex`, `CodexOnline`); Test `tests/Tests.cs`, `tests/ProviderTests.cs`.

**Interfaces:** `UsageParser.Codex(line)` returns a result when the record has at least one 300- or 10080-minute window with a valid percent (else `null`); `UsageParser.CodexOnline(json, time)` builds 5時間/週 from `primary_window`/`secondary_window`, status `""` when any window has a percent, else `"使用量を取得できません"`.

- [ ] **Step 1: Write failing tests:** `Codex Plus online gives 5時間 and 週` (18000 + 604800); `Codex Pro online gives only 週` (secondary null); `Codex online 5-hour only is still acquired`; `Codex other window lengths are ignored` (86400 → not shown); `Codex log 300 and 10080 minutes give 5時間 and 週`; change the existing `Codex five-hour-only is not weekly` test to `Codex five-hour-only log gives 5時間`.
- [ ] **Step 2: Run** `build.ps1 -TestsOnly` → the new tests FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** → all pass.
- [ ] **Step 5: Commit** (if approved) — `Show the Codex 5-hour window when the plan has one`.

---

### Task 3: Rules by span and forecast history v2

**Files:** Modify `src/Alerts.cs`, `src/Forecast.cs`, `src/Pace.cs`, `src/ForecastHistory.cs`; Test `tests/ForecastTests.cs`, `tests/AlertTests.cs`.

**Interfaces:**
- `ForecastHistory.DefaultPath()` → `…\forecast-history-2.dat`; `ForecastHistory.LegacyPath()` → `…\forecast-history.dat`; constructor `ForecastHistory(string file, DateTimeOffset now, string legacyFile)` (legacy may be null); `UsageForecaster.Default()` passes both; `UsageForecaster(string path, DateTimeOffset now)` keeps working (no legacy).
- `KnownKey(key)`: `^(Claude|Codex|Grok)/(5時間|週|週（[A-Za-z0-9 .+\-()]{1,24}）|週（モデル別）)$`.
- Save writes `version 2`; Load accepts version 2 (≤ 12 series) from the new file, version 1 (≤ 4 series) from the legacy file; `KnownPlan` maps `Max 20x`→`Ultra`, `Max 5x`→`Max` and includes `Ultra`, `Max`.
- Every `window.Label=="週"` rule check becomes `window.Span==UsageSpan.Week`.

- [ ] **Step 1: Write failing tests:**
  - `週（Fable） uses weekly forecast rules` — a scoped week series with 2 h of data stays learning (needs 24 h); a 5時間 series with 31 min and 3 samples does not.
  - `Alerts for 週（Fable） are titled with the full label` — 96% → title starts `週（Fable）：`.
  - `History seeds from the legacy file and keeps learning after the Ultra rename` (Review Focus 1) — legacy v1 file with `Claude/週` plan `Max 20x` and `Claude/5時間`; new file absent; record a sample with plan `Ultra` → series continues (sample count grows, not reset).
  - `Saving never writes the legacy file` (Review Focus 4) — legacy bytes identical after `Save`; new file exists with version 2.
  - `New keys are kept across save and load` — `Claude/週（Fable）`, `Codex/5時間` survive a round trip; `Claude/週（<script>）` is rejected.
  - `Clear deletes both history files`.
- [ ] **Step 2: Run** `build.ps1 -TestsOnly` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** → all pass.
- [ ] **Step 5: Commit** (if approved) — `Learn every usage window and keep history across the plan rename`.

---

### Task 4: Snapshot v2 and Obsidian

**Files:** Modify `src/Snapshot.cs`, `obsidian/src/usage.js`, `obsidian/src/toolbar.js`, `obsidian/styles.css` (only if the 3–4 slot layout needs it); Test `tests/SnapshotTests.cs`, `obsidian/tests/usage.test.cjs`, new `obsidian/tests/toolbar.test.cjs`.

**Interfaces:**
- `UsageSnapshot.Create` / `Disconnected` emit `schemaVersion` 2 with the window fields in Global Constraints.
- `parseSnapshot(json)`: version 2 only; exactly one service per name; `windows` array of 0–4; each `label` matches `/^(5時間|週|週（.{1,24}）)$/u`, labels unique; `shortLabel` string 1–24; `span` `"5h"`/`"week"`; returns windows with `{label, shortLabel, span, percent, resetsAt, pace, alert}`. `emptyServices()` returns `windows: []`.
- `UsageToolbar.update` rebuilds a row's slots when its label list differs from the current slots; captions use `shortLabel`.

- [ ] **Step 1: Write failing tests:**
  - C#: `Snapshot v2 carries span and short labels` — Claude with 週 and 週（Fable） → JSON has `"schemaVersion":2`, `"span":"week"`, `"shortLabel":"Fable"`, no `5時間`.
  - Node (`usage.test.cjs`): update `payload()` to v2; `version 1 is rejected`; `0 to 4 windows are accepted, 5 rejected`; `duplicate labels rejected`; `unknown span rejected`; `presentService works with three Claude windows`.
  - Node (`toolbar.test.cjs`, minimal fake DOM with `createElement`/`append`/`classList`/`dataset`/`setAttribute`): `slots follow the window list` (Review Focus 3) — update with Claude `[週]`, then `[週, 週（Fable）]` → second update shows two slots captioned `週`, `Fable`.
- [ ] **Step 2: Run** `build.ps1 -TestsOnly` and `node --test obsidian/tests/usage.test.cjs obsidian/tests/bridge.test.cjs obsidian/tests/toolbar.test.cjs` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** both → all pass; `node tools/build-obsidian.cjs` succeeds.
- [ ] **Step 5: Commit** (if approved) — `Share a variable list of usage windows with Obsidian`.

---

### Task 5: Tray popup and Windows widget

**Files:** Modify `src/PopupForm.cs`, `src/WidgetCards.cs`; Test `tests/UiTests.cs`, `tests/WidgetTests.cs`.

**Interfaces:** popup columns = `max(1, Windows.Count)`; the visible caption is `Label` when ≤ 2 windows, else `ShortLabel`; accessible names always use `Label`. Widget bar width = `360 / max(1, count)`; caption `large ? Label : ShortLabel`.

- [ ] **Step 1: Write failing tests:** UI: `popup row with three Claude windows paints and names all three` (accessible name contains `5時間`, `週`, `週（Fable）`); `popup row with one window`. Widget: `widget card shows 週（Fable） (large) and Fable (medium)`; `bar width follows window count`.
- [ ] **Step 2: Run** `build.ps1 -NoPublish -OutputDirectory build\verify` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** → `RESULT … 0 failed`, `RESULT N UI checks passed`.
- [ ] **Step 5: Commit** (if approved) — `Lay out any number of usage windows in the tray and widget`.

---

### Task 6: Stream Deck keys

**Files:** Modify `streamdeck/src/render.js`; Test `streamdeck/tests/render.test.cjs`, `streamdeck/tests/plugin.test.cjs` (fixtures to v2).

**Interfaces:** bars use `w.shortLabel`; `bars.slice(0, 3)`; vertical/horizontal/ring layouts for 1, 2 and 3 bars.

- [ ] **Step 1: Write failing tests:** fixtures → v2; `Claude Max shows 5h, 週 and Fable bars` (Review Focus 5) — 3 bars, every style renders 144×144 with all three percent texts; `a fourth window is not drawn`; `short labels come from the snapshot`.
- [ ] **Step 2: Run** `node --test streamdeck/tests/render.test.cjs streamdeck/tests/plugin.test.cjs` → FAIL.
- [ ] **Step 3: Implement**; render a preview PNG of the 3-bar keys (headless Edge, as before) and check legibility.
- [ ] **Step 4: Run** the six Stream Deck test files and `tools/package-streamdeck.ps1` → all pass, package written.
- [ ] **Step 5: Commit** (if approved) — `Draw up to three usage windows on Stream Deck keys`.

---

### Task 7: Documentation and full verification

**Files:** Modify `README.md`, `docs/data-sources.md`, `docs/alerts.md`, `docs/obsidian-guide.md`, `docs/widgets-guide.md`, `docs/streamdeck-guide.md`, `docs/privacy.md` (history file name).

- [ ] **Step 1:** Update window descriptions (Claude: 5時間 only on plans that have it, 週, 週（Fable）; Codex: 5時間 on Plus etc., 週), Ultra/Max names, Grok Bot shares the weekly pool, the new history file, old/new version mismatch messages (`連携できません · 設定を確認`, `未接続`), and dated sources in `docs/data-sources.md`. Plain wording, no invented terms.
- [ ] **Step 2: Run** the full set: `build.ps1 -NoPublish -OutputDirectory build\verify`; `node --test obsidian/tests/usage.test.cjs obsidian/tests/bridge.test.cjs obsidian/tests/toolbar.test.cjs`; the six Stream Deck test files; `node tools/build-obsidian.cjs`; `tools/package-obsidian.ps1 -SkipBuild`; `tools/package-streamdeck.ps1`; `tools/build-widgets.ps1 -SkipCoreBuild` if the native toolchain is present (else record it as not run).
- [ ] **Step 3:** Add the new Obsidian test file to `.github/workflows/build.yml`.
- [ ] **Step 4: Commit** (if approved) — `Document the current usage limits of each service`.

---

### Task 8: Check with the real account

- [ ] **Step 1:** The user quits the running tray app, then starts `build\verify\AIUsageManager.exe` from the working copy.
- [ ] **Step 2:** Read the pipe snapshot: Claude windows are `週` and `週（Fable）`, no `5時間`; Codex `週`; Grok `週`; plan name matches claude.ai.
- [ ] **Step 3:** The user reinstalls the Stream Deck package; keys show Claude 2 bars (週, Fable).
- [ ] **Step 4:** The user compares the Claude values with claude.ai Settings > Usage (Current week all models / Fable).
- [ ] **Step 5:** `%LOCALAPPDATA%\AIUsageManager\forecast-history-2.dat` exists; the old file's timestamp is unchanged.
