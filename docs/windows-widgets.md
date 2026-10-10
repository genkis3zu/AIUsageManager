# Windows Widgets specification

## Goal and boundaries

Expose Claude (5h and weekly), Codex (weekly) and Grok (weekly) in the Windows 11 Widgets Board, alongside the existing portable tray app. Existing provider files remain read-only. Credentials never enter widget JSON, command lines, files or diagnostics.

The portable tray app keeps its inbox .NET Framework 4.8 architecture. An optional native C++/WinRT COM provider supplies Adaptive Cards to Windows. The provider launches the packaged copy of the existing executable with a fixed `--widget-data` argument. That headless mode reuses `UsageService`, outputs only allowlisted display cards to an anonymous pipe, and exits. It bypasses tray initialization and the tray single-instance mutex. No widget usage cache is written by the app; Windows may retain submitted card content.

## Behavior

- One widget definition, medium and large sizes, containing all three services.
- Name, plan, per-window percentage/bar/reset time and original observation time. Large also shows source and status; medium uses compact d/h/m reset durations.
- Missing data remains 未取得, acquired zero remains 0%, >=80% is attention/red.
- Manual refresh action and an action opening the tray popup.
- Refresh on activation and every five minutes while active. No network polling while inactive; reopening refreshes the card. Requests are serialized and identical data is fetched once for all instances/sizes.
- Keep the most recent card in memory while refreshing. After a failed fetch, publish an explicit unavailable state, not invented zero or silently fresh timestamps.
- Adaptive Cards use host colors; the Widgets Board controls its own light/dark theme.
- Restore pinned instances on provider start; copy callback values rather than retaining callback objects.
- Native child process has a 30-second deadline and bounded output; malformed output cannot become a widget template.

## Delivery

The optional x64 package is registered for the current user using an AppxManifest and the supported packaged COM/app-extension contracts. Local registration requires Windows Developer Mode already enabled; scripts check it and never enable it or install trust certificates. Require Microsoft.WindowsAppRuntime.2 >=2.5.1.0 and a current Windows Web Experience Pack. Missing prerequisites produce actionable messages.

Provide build/install/uninstall scripts, a tray menu entry that opens the registration instructions or Widgets Board, and an allowlisted widget ZIP. Package only compiled executables/config, manifest, known artwork, license and instructions. No SDK caches, keys, credentials, user settings or real-data screenshots. Keep the original portable ZIP available.

## References

- https://learn.microsoft.com/en-us/windows/apps/develop/widgets/implement-widget-provider-win32
- https://learn.microsoft.com/en-us/windows/apps/develop/widgets/widget-provider-manifest
- https://learn.microsoft.com/en-us/windows/apps/develop/widgets/widgets-create-a-template
- https://github.com/microsoft/WindowsAppSDK-Samples/tree/main/Samples/Widgets
