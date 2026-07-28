---
name: codex-monitor-overlay
description: Operate the native macOS or Windows Codex Quota Orb, including its authoritative weekly quota display, aggregate latest-context occupancy, draggable high-DPI floating window, and local Codex token-history details page.
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

The details page keeps local Token throughput history in the top metric row and bottom activity area: local cumulative, today, current month, current week, daily heatmap, weekly trend, and cumulative trend. The middle card is a different metric: the aggregate of every readable local conversation's latest context occupancy. It must never use the foreground conversation alone and must never use historical `total_token_usage.input_tokens` as context occupancy.

For each conversation, read its final valid numeric `token_count` event. Occupied context comes from that event's `last_token_usage.input_tokens`; at a compaction boundary where the input components are zero but `last_token_usage.total_tokens` remains positive, derive occupied context as `last total - last output`. Capacity comes from the same event's `model_context_window`. Aggregate occupied context and capacity once per conversation, then calculate aggregate occupancy as `sum occupied / sum capacity`. This is a cross-conversation snapshot, not a cumulative-over-time Token counter. The main context card shows aggregate occupancy percentage, occupied context, total capacity, remaining capacity, project count, conversation count, and update time.

Click the full context region to open the next-level context and usage page. It has three views: occupied-versus-remaining aggregate capacity; per-project occupancy grouped by the `session_meta.cwd` path; and every conversation sorted by its latest occupied context. Project and conversation total-share percentages use aggregate occupied context as the denominator, so both levels reconcile to 100%. Each row also shows its own capacity and occupancy percentage. Conversation labels use date/time plus a short session ID and never derive a title from message text. The page must include a text `Back` / `返回` control; `Esc` also returns to the parent details page. Project paths and session IDs are parsed in memory and must not be written to the numeric history cache.

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
- Keep aggregate context occupancy separate from account quota and from total Token history. Context occupancy is the sum of one latest `last_token_usage` snapshot per local conversation; Token history is the sum of historical `total_token_usage` increments.
- Never use the foreground conversation alone. Use each conversation's latest `model_context_window` as that conversation's capacity, then aggregate all readable conversations.
- Never feed `total_token_usage.input_tokens` into the context card, project context totals, or conversation context totals. It is historical input throughput and repeatedly counts carried context.
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
- Never persist prompt text, message text, Codex Desktop log content, raw session events, raw context events, or raw quota responses. The local history cache contains only file fingerprints, daily numeric totals, and numeric cumulative token-component totals; project paths and session IDs are read in memory and are not cached.
- Do not redeem reset credits or change account settings.
- Do not claim exact real-time synchronization when the upstream service has not published a changed value; report the last verified sample or stale state honestly.
