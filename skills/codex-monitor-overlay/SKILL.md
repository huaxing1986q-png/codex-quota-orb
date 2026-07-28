---
name: codex-monitor-overlay
description: Operate the native macOS or Windows Codex Quota Orb, including its authoritative weekly quota display, current-session context capacity, draggable high-DPI floating window, and local Codex token-history details page.
---

# Codex Quota Orb

The monitor is a local native floating window for macOS or Windows. It reads the existing Codex Desktop login state and sends that access token only to `https://chatgpt.com/backend-api/wham/usage`. The service response is authoritative: local session files may trigger a refresh but never provide displayed quota values.

## Start the orb

Detect the host platform first.

### macOS

Prefer the installed app at `~/Applications/Codex Quota Orb.app`. Start it without a Terminal window:

```bash
open "$HOME/Applications/Codex Quota Orb.app"
```

When working from source, use `macos/CodexQuotaOrb/scripts/build-app.sh` on a Mac, then open the generated `.app`. Do not claim a Windows host can compile AppKit. The login-start installer is `macos/CodexQuotaOrb/scripts/install-launch-agent.sh`; it writes to the user's Applications and LaunchAgents directories, so request permission before executing it.

Preferences and numeric cache are stored in `~/Library/Application Support/CodexQuotaOrb/`.

### Windows

Locate this plugin's `scripts/CodexMonitor.ps1` and start it without creating a PowerShell console. Do not set `WindowStyle` to `Hidden`, because that startup flag also suppresses the floating WinForms surface:

```powershell
$startInfo = [Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = 'powershell.exe'
$startInfo.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "<plugin-root>\scripts\CodexMonitor.ps1" -Mode Start'
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
[Diagnostics.Process]::Start($startInfo) | Out-Null
```

Replace `<plugin-root>` with the resolved plugin directory. Do not invent a path. Report in commentary before starting the console-free process.

When always-on-top is disabled, the orb appears while Codex or the empty desktop is foreground and restored; other foreground apps hide it. When always-on-top is enabled, it remains visible independently of Codex focus, minimization, or visibility. Hovering never expands the 48 logical-pixel circular state; a single click expands it to the full quota card. Collapse the card after the pointer leaves it or the user clicks the empty desktop, while keeping a short click-stability delay so the opening click is never mistaken for an exit. It starts at the lower-right of the active screen and can be dragged; the custom position persists locally. Treat the stored position as the collapsed orb anchor and never overwrite it with the clamped expanded-card coordinates.

## Controls

- `EN` / `中`: switch language.
- `↑` / `·`: enable independent persistent always-on-top or return to Codex-following mode.
- `L`: language shortcut while the window has keyboard focus.
- `T`: always-on-top shortcut while the window has keyboard focus.
- Single-click the collapsed orb to expand the weekly quota card.
- Right-click the collapsed orb to show a native menu with `退出插件` / `Exit plugin`; selecting it must terminate the current orb process without triggering expansion or drag.
- Click the expanded weekly quota region, or press `Enter` / `Space`, to open and foreground the Codex Token usage details page. Keep the details form in the monitor's existing process so no PowerShell console is created; reuse the same form on later clicks. Do not add a separate icon for this action.
- `Esc`: collapse the card; from the collapsed state, close the current monitor process.

The details page leads with local current-calendar-month Token usage and its share of the local cumulative Token history. Do not show a monthly reset time or account plan in that card. Show the local cumulative Token value instead. The same card shows the active project's share of local cumulative history, the active conversation's share of local cumulative history, and that conversation's share within its project. Official weekly quota remains only in the orb and expanded quota card. The page also shows local-history total, today, current month, current week, a daily heatmap, weekly trend, and cumulative trend. Clicking the monthly usage summary opens current-session context capacity plus project and conversation details. Resolve the foreground conversation ID from Codex Desktop's local `thread_stream_view_activity_changed active=true` route line, reading only the ID in memory; if the log is unavailable, fall back to the session with the newest non-compaction input activity. Context capacity uses the newest numeric `token_count` event in that session: `model_context_window` is capacity and the latest `last_token_usage.input_tokens` is occupied input context. At a Codex compaction boundary, the input components may be zero while `last_token_usage.total_tokens` still carries the compacted context size; only in that internally inconsistent shape, derive occupied input as `last_token_usage.total_tokens - last_token_usage.output_tokens`. Never substitute cumulative `total_token_usage` for context occupancy. A compaction boundary does not expose a reliable cached/fresh split: render both values as unavailable instead of assigning all occupied tokens to fresh input. Show the source context event's sample time separately from the local-history scan time. Cached input is a subset of input; reasoning output is a subset of output. Never add those subsets twice.

Click the full monthly-total-usage region to open the next-level capacity and usage page. It has three views: capacity structure; per-project share grouped by the `session_meta.cwd` path; and every conversation sorted by its numeric Token total. Conversation labels use date/time plus a short session ID and never derive a title from message text. The page must include a text `Back` / `返回` control; `Esc` also returns to the parent details page. Project paths and session IDs are parsed in memory and must not be written to the numeric history cache.

`Left` / `Right` switches history views, `F5` or `Ctrl+R` refreshes, and `Esc` closes the details page.

On Windows, preferences are stored in `%LOCALAPPDATA%\CodexMonitorOverlay\preferences.json` and numeric history cache in `%LOCALAPPDATA%\CodexMonitorOverlay\token-history-cache.json`.

## Accuracy and refresh policy

- Display only the weekly **remaining** percentage and weekly reset time, never raw used percentage or any 5-hour block.
- Classify the weekly window by its declared 604,800-second duration, not by misleading `primary` or `secondary` field names. Render an unavailable weekly window as an em dash; never infer it from another period.
- Remaining `>= 50%` is Healthy, `10–49%` is Caution, and `< 10%` is Critical.
- UI and foreground detection update every 250ms.
- Normal service calibration is every 30 seconds; within 15 minutes of reset it is every 10 seconds.
- Local session changes trigger a debounced service refresh after 750ms.
- Focus and click-to-expand trigger a refresh with a 2-second minimum request spacing.
- On a transient failure, retain the last successful values for at most 30 minutes and mark them stale. Never fabricate values or silently fall back to local token estimates.
- Keep quota and token-history semantics separate. The floating percentage is official account quota. The details page reads only numeric `token_count` events from local Codex JSONL sessions and must never be described as account quota.
- Keep context capacity separate from both account quota and cumulative history. Context usage is latest input divided by `model_context_window`; cumulative session tokens must never be divided by the context window.
- Context remaining `>= 50%` is Healthy with no cleanup prompt, `10–49%` is Caution with a prompt to trim unrelated context, and `< 10%` is Critical with a prompt to summarize and start a new task.
- Aggregate token increments by each event's local calendar date. Reuse cache only when the source file length and last-write timestamp are unchanged.
- macOS uses native AppKit/Retina drawing; Windows uses Per-Monitor DPI V2. Both must re-render at the destination monitor's native scale and never upscale a fixed bitmap for 4K displays.

## Inspect and validate

On macOS, run the packaged offline parser self-test:

```bash
"<app-path>/Contents/MacOS/CodexQuotaOrb" --self-test
```

On Windows, use `-Mode Usage` to request the current authoritative quota as JSON:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "<plugin-root>\scripts\CodexMonitor.ps1" -Mode Usage
```

Use `-Mode SelfTest` for offline response-shape and remaining-percent tests:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "<plugin-root>\scripts\CodexMonitor.ps1" -Mode SelfTest
```

Use `-Mode Details` for a temporary direct details-page validation:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "<plugin-root>\scripts\CodexMonitor.ps1" -Mode Details -AutoCloseSeconds 15
```

## Boundaries

- Never print, persist, or expose access tokens, account IDs, request headers, or raw quota responses.
- Do not send the Codex token anywhere except the ChatGPT quota endpoint named above.
- Never persist prompt text, message text, Codex Desktop log content, raw session events, raw context events, or raw quota responses. The local history cache contains only file fingerprints and daily numeric totals; foreground route IDs and context details are read in memory and are not cached.
- Do not redeem reset credits or change account settings.
- Do not claim exact real-time synchronization when the upstream service has not published a changed value; report the last verified sample or stale state honestly.
