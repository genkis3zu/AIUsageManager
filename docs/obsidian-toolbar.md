# Obsidian usage toolbar

## Agreed behavior

The user approved a toolbar fixed above the note, visible while its body scrolls. Compact mode is one horizontal line. Standard mode is three rows, one for Claude, Codex and Grok, and is the default. A button switches modes and persists that preference. No block or template is inserted into notes.

Both reading and editing views receive the toolbar before the Markdown view's public `contentEl`. Only our own element is added/removed. Existing editor state, note text, selection and scroll position are retained. Workspace changes attach and remove toolbars for Markdown panes, including secondary windows. CSS uses Obsidian theme variables, bounded heights and horizontal overflow for narrow panes.

Each row presents usage, bars, warning state and original observation time. Tooltips provide plan, reset times, source, and full warnings. Compact mode retains all four usage windows and a warning marker. Unknown is distinct from zero; stale/offline data cannot appear fresh. Controls switch display mode, request a refresh, and open the existing app's detail popup.

## Local integration

The tray remains the single owner of fetching, alert history and Windows notifications. A read-only named pipe, secured to the current Windows user and denied to network logons, serves a versioned JSON snapshot of display fields from memory. A fixed `--obsidian-data` helper reads this pipe with time/output bounds. It does not read credentials or fetch providers. `--refresh` signals the existing tray instance; normal startup still performs the first fetch if the app was not running.

The desktop-only Obsidian plugin invokes the configured absolute app executable with fixed argument arrays and no shell. Every 15 seconds it reads the in-memory snapshot, without initiating provider requests. It does not access service credentials, persist usage, read note content, or use a local web server. Unload stops polling, kills any helper still running, and removes owned elements.

## Implementation and verification

1. Add `UsageSnapshot` serialization, current-user pipe server/client, and fixture tests for zero/missing values, preserved timestamps, fresh warnings, ACLs, connection failure, size/time bounds and disposal. Connect publication and refresh signaling to Program/TrayContext.
2. Add the self-contained Obsidian plugin: lifecycle, strict snapshot parsing, display helpers, fixed toolbar, theme-aware styles, settings, bounded process calls. Test data semantics, fixed argument invocation, mode persistence, multi-pane teardown, scroll anchoring and both display sizes.
3. Package only runtime plugin files and user documentation. Verify the installed app and plugin on this PC using official CLI and user confirmation. Public fixtures and screenshots use invented values only. Scan public source and distributions, then update the existing draft PR.

## References

- https://github.com/obsidianmd/obsidian-api/blob/master/obsidian.d.ts (ItemView.contentEl, MarkdownView, workspace events)
- https://obsidian.md/help/cli (plugin enable/reload and developer inspection)
