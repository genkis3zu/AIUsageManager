# Status Key Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax.

**Goal:** One "AIの状況" display (Stream Deck key + one tray line) combining Anthropic/OpenAI outage status and recent weekly resets.

**Architecture:** The tray app fetches two public Statuspage-compatible JSON summaries on each 5-minute refresh, tracks weekly resets from fresh usage data, and publishes a computed `status` object (tone, title, detail, page) in the snapshot. The tray header and a new Stream Deck action render it; pressing the key opens the page through Stream Deck's `openUrl`.

**Spec:** `docs/superpowers/specs/2026-11-19-status-key-design.md`

## Global Constraints

- Status URLs (exact): `https://status.claude.com/api/v2/summary.json`, `https://status.openai.com/api/v2/summary.json`; pages: `https://status.claude.com/`, `https://status.openai.com/`. GET, no Authorization/Cookie, no redirects, 10 s, 1 MiB.
- Tones: `critical` (障害), `warning` (一部不調), `reset`, `normal` (正常), `unknown` (確認できません). Priority: critical > warning > reset > normal; `unknown` only when no company could be read and no reset.
- Claude relevant components: names starting `claude.ai`, `Claude Code`, `Claude API`. OpenAI relevant: names containing `Codex` or `ChatGPT`, combined with the overall indicator.
- Reset notices: weekly windows only, fresh online values only, shown for 1 hour from the reset time.
- Snapshot stays schemaVersion 2 with an added optional `status`. Plugin version 1.2.0.0. C# 5; tests first; commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. A status page that fails or returns non-JSON → that company is `unknown`; the display never says 正常 when nothing could be read.
2. An unrelated Anthropic component (e.g. Claude for Government) degraded → Claude stays normal.
3. The app starts after a weekly reset happened → no false reset notice (first observation only records).
4. A key press can only open the two fixed pages.

### Task 1: Status parsing, fetching and reset tracking (C#)
`src/Status.cs` (new): `StatusLevel`, `ProviderStatus`, `StatusParser.Claude(json)` / `StatusParser.OpenAI(json)`, `StatusPages.FetchAsync(HttpClient, CancellationToken)`, `ResetTracker.Observe(ServiceUsage[], DateTimeOffset)`, `StatusSummary.Create(ProviderStatus[], ResetNotice[], DateTimeOffset)` with `Tone`, `Title`, `Detail`, `Page`, `Line`. Tests in new `tests/StatusTests.cs` (added to `build.ps1`).

### Task 2: Snapshot, tray line and refresh wiring
`src/Snapshot.cs` (`Create(..., StatusSummary)`), `src/TrayContext.cs` (fetch status alongside usage, track resets), `src/PopupForm.cs` (`SetStatus`, header line with tone colour); tests in `tests/SnapshotTests.cs`, `tests/UiTests.cs`.

### Task 3: Stream Deck key
`obsidian/src/usage.js` (`parseSnapshot` keeps a validated `status`), `streamdeck/src/streamdeck.js` (`openUrl`), `streamdeck/src/settings.js` (status action id), `streamdeck/src/render.js` (`statusView`, rendering), `streamdeck/src/plugin.js` (status keys, press opens page), `streamdeck/manifest.json` (action, 1.2.0.0); tests in `obsidian/tests/usage.test.cjs` and `streamdeck/tests/*`.

### Task 4: Docs, verification, review, merge
README, `docs/data-sources.md`, `docs/privacy.md`, `docs/streamdeck-guide.md`; full suite; fresh review; fix; merge into main.
