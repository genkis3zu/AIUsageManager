# Windows Widgets Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Add an optional, functioning Windows 11 Widgets Board provider to AI Usage Manager.

**Architecture:** A native C++/WinRT packaged COM provider consumes sanitized Adaptive Card JSON from the existing .NET Framework executable through a bounded anonymous pipe. The tray app remains portable; headless widget mode shares providers and parsers without sharing secrets or writing usage history.

**Tech Stack:** .NET Framework 4.8/C# 5, C++20 / static CRT, Windows SDK, Microsoft.WindowsAppSDK.Widgets 2.0.5, Adaptive Cards 1.5, AppxManifest registration.

**Spec:** docs/windows-widgets.md

## Global Constraints

- Existing provider files remain read-only. Credentials never enter widget JSON, command lines, files or diagnostics.
- The portable tray app keeps its inbox .NET Framework 4.8 architecture.
- One widget definition, medium and large sizes, containing all three services.
- Refresh on activation and every five minutes while active. No network polling while inactive.
- Native child process has a 30-second deadline and bounded output.
- Require Microsoft.WindowsAppRuntime.2 >=2.5.1.0 and a current Windows Web Experience Pack.
- Local registration requires Windows Developer Mode already enabled; scripts check it and never enable it or install trust certificates.

## Review Focus

1. A child that hangs or emits oversized/invalid JSON must terminate without a UI hang or leaking raw output.
2. A pinned widget survives provider restart; deleted widgets must not continue polling.
3. 0%, missing, over-limit and expired observations stay distinct at both widget sizes.
4. Install/uninstall affects only this current user's package and never changes OS trust/developer settings.
5. Public archives and diagnostics must not contain credentials, local paths, actual usage data or build tools.

## Task 1 — Safe display cards and headless entry point

**Files:** create src/WidgetCards.cs, tests/WidgetTests.cs; modify src/Program.cs and build.ps1.

**Interfaces:** `WidgetCards.Create(ServiceUsage[] values, DateTimeOffset now)` returns an envelope with `medium` and `large` Adaptive Card objects. `Program --widget-data` writes its UTF-8 JSON to standard output, exits, and never constructs TrayContext. `--widget-sample` uses synthetic values for offline contract testing.

- [x] Add tests for envelope sizes, four usage windows, absent vs zero, >=80% styling, reset expiry, original timestamps, source attribution, escaped strings and only known actions.

```csharp
var values = new[]{ServiceUsage.Empty("Claude",""),ServiceUsage.Empty("Codex",""),ServiceUsage.Empty("Grok","")};
values[1].Windows[0].Percent=0;
string cards=WidgetCards.Create(values,DateTimeOffset.Now);
Assert(cards.Contains("未取得") && cards.Contains("0%"));
```

- [x] Run the new tests and observe their failure before implementing the renderer.
- [x] Build dictionaries/arrays with JavaScriptSerializer, local data-URI bar images and host text colors. Use only model display fields. Do not interpolate unescaped JSON.
- [x] Dispatch the two exact widget arguments before GUI initialization and the instance mutex. Fetch with UsageService only for `--widget-data`; output a safe unavailable envelope on failure.
- [x] Run core tests and launch `--widget-sample` with redirected stdout to validate the actual process contract and size bound.

## Task 2 — Native provider and bounded child transport

**Files:** create widgets/Provider.cpp, widgets/ChildProcess.h, widgets/ProviderState.h, widgets/NativeTests.cpp, widgets/AppxManifest.xml, tools/build-widgets.ps1.

**Interfaces:** `ReadCards()` returns validated UTF-8 JSON from the fixed sibling executable; `ProviderState` copies widget ID/size/active state and owns serialized refresh scheduling; COM CLSID and manifest use one fixed project GUID.

- [x] Add native state tests: duplicate activation, deactivation, deletion, restart restoration, refresh overlap and five-minute scheduling. Add child transport tests with a test helper for success, timeout, oversize and malformed output.

```cpp
state.activate(L"one", Size::Medium);
state.activate(L"one", Size::Medium);
assert(state.active_count() == 1);
state.remove(L"one");
assert(state.active_count() == 0);
```

- [x] Generate C++/WinRT headers from the pinned Microsoft Widgets SDK. Compile C++20 with /MT, warnings as errors, no debug paths or private data.
- [x] Implement IWidgetProvider callbacks, factory registration and shutdown. Never retain callback objects. Marshal only copied strings/enums to the worker.
- [x] Restore GetWidgetInfos state, update through WidgetManager, handle refresh/open actions and safe failures. Poll only active instances. Revoke the factory and stop workers on exit.
- [x] Build and run native tests, validate COM activation and widget package manifest, and inspect generated card output through the real helper.

## Task 3 — Registration, user entry point and packaging

**Files:** create tools/install-widgets.ps1, tools/uninstall-widgets.ps1, tools/package-widgets.ps1; modify src/TrayContext.cs and tests/UiTests.cs; add package artwork under widgets/Assets.

**Interfaces:** installation registers `AIUsageManager.Widgets` for the current user from a stable app-owned directory. The menu opens `ms-widgets:` when registered, otherwise the local/public installation instructions. Uninstallation targets that package name only.

- [x] Add script validation of Developer Mode, runtime version, architecture, required files and exact identity. Verify missing prerequisites fail before changing files/registration.
- [x] Stage only allowlisted package files, register with Add-AppxPackage -Register, and verify the widget app extension and COM class are discoverable.
- [x] Add a tray menu entry without changing refresh, theme or exit semantics; verify existing UI tests.
- [x] Package the widget bundle with explicit filenames and verify ZIP contents and hashes. Keep installation state and credentials out of archives.
- [x] Install locally after checks pass; verify real COM activation and WidgetManager resolution. User confirmed picker discovery and pinning. Remaining manual action/restart checks are explicitly listed in docs/verification.md.

## Task 4 — Review, documentation and delivery

**Files:** README.md, CONTRIBUTING.md, docs/privacy.md, docs/verification.md, .github/workflows/build.yml; update this plan's completion state.

- [x] Document optional runtime/registration requirements, medium/large layouts, update behavior, uninstall and host caching. Preserve accurate claims for the portable build.
- [x] Run original and widget tests, fresh build/package verification, secret scans and repository diff checks.
- [x] Obtain one independent read-only whole-branch review; resolve material findings with regression tests.
- [x] Commit with GitHub noreply identity and deliver the implementation, installation status, test evidence and remaining user pinning instructions.

## Execution ledger

- Planning: official Microsoft packaged COM provider design selected; existing compiler, runtime and Developer Mode were detected without modifying system settings.
- Execution authorization: user asked for planning and implementation together; proceed inline through all tasks and request only genuinely required external-state approvals.

- Task 1: complete. Added failing card-contract tests first, then implementation; final core/card checks 42 and UI checks 30 pass. Actual sample worker validated through the native pipe.
- Task 2: complete. Three native test groups pass; real installed COM activation and WidgetManager creation pass. Native child deadline, output bound, nonzero exit and malformed payload are exercised.
- Task 3: complete. Current-user registration, removal of temporary registration, reinstallation at a stable directory, binary-hash verification, and exact 12-file ZIP inventory. User confirmed picker discovery and pinning.
- Ruling: medium displays compact d/h/m reset values and original observation times; source/status detail is in large. This fits the documented medium dimensions without clipping key usage fields.
- Ruling: the packaged development host virtualizes AppData writes. Added an explicit InstallDirectory override and used a stable project-owned directory locally; normal standalone installation retains the LocalAppData default.
- Ruling: do not continue native GUI automation after user Escape. Button actions, long-duration refresh and reboot restoration remain manual verification items; no claim of full GUI verification.
- Final review: independent read-only reviewer found source paths embedded by C++/WinRT source_location. /DNDEBUG alone did not remove them. Added /DWINRT_NO_SOURCE_LOCATION and a packaging gate that scans UTF8 and both UTF16 alignments. Observed old artifacts fail and rebuilt artifacts pass. Reviewer independently confirmed zero path matches after the fix.
- Final review ruling: compact expiry/unknown text could clip; treated as a display defect and fixed it. Official Adaptive Cards host settings show no text overflow or clipped buttons across normal/unknown/expired, both sizes and both themes (12 cases).
- Final review ruling: runtime lifecycle races/reboot behavior were not independently executed by the reviewer. State/transport tests and real COM activation cover the automated contract; manual board/reboot coverage remains explicitly limited.
- Final review: no unresolved code findings or deferred minor findings.