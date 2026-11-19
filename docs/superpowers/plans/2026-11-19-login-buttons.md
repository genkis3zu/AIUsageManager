# Login Buttons Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax.

**Goal:** One-click re-login for Claude, Codex and Grok from the tray, the Windows widget and Stream Deck.

**Architecture:** Reuse the existing official-CLI launcher (`LoginLauncher`) and the `--login-<service>` start arguments. Claude gains a launcher entry (`claude.exe auth login --claudeai`) and a needs-login state; the widget provider and the Stream Deck plugin route a press to `AIUsageManager.exe --login-<service>`.

**Tech Stack:** C# 5 / .NET Framework 4.8, C++ widget provider, Node.js Stream Deck plugin.

**Spec:** `docs/superpowers/specs/2026-11-19-login-buttons-design.md`

## Global Constraints

- No shell, fixed arguments, no output capture; executables only from `%USERPROFILE%\.local\bin\claude.exe` or drive-absolute PATH entries.
- Claude `NeedsLogin` = no usable token, expired token, or HTTP 401 — never for network errors, 429 or 403.
- Plugin login arguments allowlist: `--login-claude`, `--login-codex`, `--login-grok`.
- Stream Deck plugin version 1.1.2.0. C# 5 only; tests first; commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. A Claude 401 with a saved cache still shows the login button (values from the cache, NeedsLogin true). Test in Task 1.
2. The tray app already running when Stream Deck asks for a login → the running app performs it (second instance only signals). Covered by the existing Program flow; checked manually in Task 4.
3. A key that needs login but where the tray app is not running → the app starts and performs the login (the event is set before the first instance registers its wait). Checked manually in Task 4.

---

### Task 1: Claude login launcher and needs-login state
**Files:** `src/Login.cs`, `src/Providers.cs`; tests in `tests/ProviderTests.cs`.
- [ ] Tests: `LoginLauncher.StartInfo("Claude", profile, local, path)` → `.local\bin\claude.exe`, arguments `auth login --claudeai`, working directory = profile, `UseShellExecute` true; PATH fallback to an absolute `claude.exe`; null when absent; relative PATH entries ignored. Provider: no credentials → `NeedsLogin`; expired token → `NeedsLogin`; HTTP 401 → `NeedsLogin`; 401 with a usable `usage-cache.json` → cached values and `NeedsLogin`; HTTP 429 and 403 → not `NeedsLogin`.
- [ ] RED (`build.ps1 -TestsOnly`), implement, GREEN, commit.

### Task 2: Tray, start argument and widget
**Files:** `src/PopupForm.cs` (button visible for Claude too), `src/TrayContext.cs` (menu and login signals for three services), `src/Program.cs` (`--login-claude`, event `Local\AIUsageManager.Login.Claude.<sid>`), `src/WidgetCards.cs` (Claude login action), `widgets/Provider.cpp` (`login-claude` verb); tests in `tests/UiTests.cs`, `tests/WidgetTests.cs`.
- [ ] Tests: a Claude row with `NeedsLogin` shows the login button; the login menu lists Claude, Codex and Grok; the widget offers `login-claude` only when Claude needs login.
- [ ] RED (`build.ps1 -NoPublish`), implement, GREEN; `tools/build-widgets.ps1` builds; commit.

### Task 3: Stream Deck login on press
**Files:** `streamdeck/src/render.js` (`needsLogin` in the key view), `streamdeck/src/location.js` (`launchApp(file, args)` with the allowlist), `streamdeck/src/plugin.js` (short press on a needs-login key launches `--login-<service>`), `streamdeck/manifest.json` (1.1.2.0); tests in `streamdeck/tests/*.test.cjs`.
- [ ] Tests: views report `needsLogin`; launch passes only allowed arguments and rejects others without spawning; short press on a 要ログイン key launches `--login-codex` and does not change the style; long press still changes the colour; a failed launch shows アプリの場所を設定; manifest version 1.1.2.0.
- [ ] RED, implement, GREEN, package, commit.

### Task 4: Docs, verification and review
- [ ] Update README, `docs/data-sources.md`, `docs/privacy.md`, `docs/streamdeck-guide.md`, `docs/widgets-guide.md`.
- [ ] Full suite: `build.ps1 -NoPublish`, Obsidian and Stream Deck tests, packages, `tools/build-widgets.ps1`.
- [ ] Final review by a fresh reviewer; fix Critical/Important with tests.
- [ ] Manual: the Stream Deck key press with the tray running and not running (Review Focus 2 and 3) — only if the user wants to try a real logout.
