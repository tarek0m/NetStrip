# NetStrip — Design Spec

**Date:** 2026-10-23
**Status:** Approved design, pending spec review
**Working name:** NetStrip (rename freely before v1.0)

## 1. Goal

A minimal, good-looking Windows network speed monitor that shows live upload/download speeds **inline on the taskbar**, with a Fluent flyout for details and daily/monthly usage totals. Released publicly on GitHub.

### Success criteria

- Strip shows ↑/↓ speeds next to the system tray, updated every 1 s, and never makes taskbar content jitter.
- Survives Explorer restarts, DPI changes, theme changes, taskbar alignment changes, and auto-hide without user action.
- Idle CPU < 0.5 % on a modern CPU; working set ≤ 60 MB.
- Runs on Windows 10 22H2 and Windows 11 (x64, arm64), no admin rights required.

### Non-goals (v1)

Taskbar sparkline, per-app bandwidth, CPU/RAM meters, skins/plugins, speed test, CSV export, code signing, winget.

## 2. Platform & stack

- C# / .NET 10 (`net10.0-windows`; no versioned Windows TFM, which would ship the WinRT projection), WPF with the built-in Fluent theme (`ThemeMode`), Mica backdrop via `DwmSetWindowAttribute`.
- The Fluent theme loads on first use: `Application.ThemeMode` is set (from the `theme` setting) just before the flyout, the settings window, or the strip's context menu or tooltip first appears, and from then on theme changes apply live. The strip draws with its own brushes (and keeps the classic `Window` style), so an idle NetStrip that has shown only the strip never loads `PresentationFramework.Fluent`.
- Win32 interop via `CsWin32` source generator (no hand-written P/Invoke where avoidable).
- Tests: xUnit.
- Packaging: Inno Setup (per-user installer) + self-contained portable zip; built by GitHub Actions on version tags.
- License: MIT.

## 3. Components

Data flows one way: **NetSampler → UsageStore, StripView, Flyout**. Win32-touching components sit behind interfaces so logic is unit-testable.

| Component | Responsibility | Depends on |
|---|---|---|
| `NetSampler` | Poll interface counters every 1 s, compute deltas, choose adapter, publish `Sample` | `IInterfaceTable` (Win32 `GetIfTable2`) |
| `UsageStore` | Accumulate daily totals per adapter, persist, compute monthly totals, raise cap events | `NetSampler`, `IClock`, `IFileSystem` |
| `TaskbarHost` | Locate taskbars, attach/position strip windows, react to shell changes, fall back to floating mode | Win32 |
| `StripView` | Render the two-line readout, theme colors, idle fade, tooltip, click/right-click | `NetSampler`, `TaskbarHost`, `UsageStore` |
| `Flyout` | Details popup: adapter, IP, 60 s graph, totals, cap bar, update notice | `NetSampler`, `UsageStore`, `UpdateChecker` |
| `FullscreenWatcher` | Hide strip on a monitor while a fullscreen app occupies it | Win32 |
| `Settings` | Load/save/migrate `settings.json`; settings window | `IFileSystem` |
| `UpdateChecker` | Check GitHub Releases ≤ once/day; expose "update available" | `HttpClient` |

### Core types

```csharp
record Sample(DateTimeOffset Time, string AdapterId, long UpBytesPerSec, long DownBytesPerSec);
record AdapterInfo(string Id, string Name, AdapterKind Kind, string? IPv4);   // Kind: Ethernet, WiFi, Cellular, Other
```

## 4. NetSampler

- Reads `MIB_IF_ROW2` via `GetIfTable2` once per second (System.Threading.PeriodicTimer on a background thread).
- Uses 64-bit `InOctets` / `OutOctets`. Speed = delta / elapsed seconds (elapsed measured with `Stopwatch`, not assumed to be 1 s).
- Excludes: loopback, tunnel, `IfType` not in {Ethernet, IEEE 802.11, WWAN/PPP}, and adapters flagged as filter/virtual (`InterfaceAndOperStatusFlags.FilterInterface`, `HardwareInterface == false`).
- Adapter modes (setting `adapter`):
  - `auto` (default): interface returned by `GetBestInterfaceEx` toward a public address (`1.1.1.1`); re-evaluated every 10 s and on `NotifyIpInterfaceChange`.
  - a specific adapter id (persisted by `InterfaceGuid`).
  - `all`: sum of all included adapters.
- If a counter decreases (reset/re-plug), that tick reports 0 for that adapter.
- If the selected adapter disappears, publish 0/0 and show "Disconnected" in the flyout; resume automatically when it returns.
- Publishes `Sample` via a C# event; UI subscribers marshal to the dispatcher.

## 5. UsageStore

- Per adapter id: daily buckets `{date (local), upBytes, downBytes}`.
- Each `Sample` carries every included adapter's exact byte delta since the previous tick (`Sample.Traffic`), whatever adapter is displayed; each adapter's bytes go to its own bucket for the current local day. So usage is exact (no rate × 1 s approximation), counts across sleep gaps, and "all" totals and the data cap include every adapter even when the display follows the best route. Day boundary uses `IClock.Now` local date (DST-safe because buckets are keyed by date, not by 24 h spans). Older files may also contain an `all` key (from all-adapters mode before per-adapter recording); it still counts toward "all" totals.
- Persist to `%AppData%\NetStrip\usage.json` every 60 s and on shutdown, via write-to-temp + `File.Replace` (atomic).
- Retention: 13 months; older buckets pruned on load and at day rollover. A date is pruned only if it is also older than the 397th most recent date with data, so a clock that jumps forward can never wipe the history.
- Corrupt file → rename to `usage.json.bad-<timestamp>`, start empty, log, and show a one-time notice in the flyout.
- File that cannot be read (locked by antivirus/backup/sync; read retried briefly first) → leave it untouched, start empty, log; before the next save, read it again and merge it with this session's usage (or quarantine it if it turns out to be corrupt). Never save over a file that has not been read.
- Monthly period: from `capResetDay` (1–28, default 1) of the current month to the day before the next reset.
- Data cap (`capGb`, null = off) applies to the adapter scope set by `capAdapter` (`all` default, or an adapter id). Warnings at 80 % and 100 %, each at most once per period: a Windows notification titled "NetStrip" (`Shell_NotifyIcon` info balloon, which Win10/11 show as a regular notification under the exe's FileDescription; no WinRT, so no 25 MB SDK projection) plus a one-time flyout notice with the same text in case the notification is missed. The notification owns a temporary notification-area icon that is removed when the balloon times out, is dismissed or clicked (clicking opens the flyout), or after a fallback delay (Windows' notification display time + 30 s, at least 1 min).
- Queries: `Today(adapterScope)`, `ThisPeriod(adapterScope)`, `CapProgress()`.

## 6. TaskbarHost

### Attach

- Primary: `FindWindow("Shell_TrayWnd")`; secondary monitors: enumerate `Shell_SecondaryTrayWnd`.
- Create one strip window per enabled taskbar (setting `monitors`: `primary` default, or `all`). Window is a WPF `Window` with `WindowStyle=None`, `AllowsTransparency=True`, `ShowInTaskbar=False`, `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`, then `SetParent` into the taskbar handle.
- Height = taskbar height; width = fixed width measured for `"999.9 MB/s"` in the current font/DPI (+ arrow glyph + padding).
- Position: immediately left of `TrayNotifyWnd` on the primary taskbar; on secondary taskbars, left of the clock area if present, else right edge minus padding.
- Vertical/side taskbars (Windows 10, or Windows 11 with a taskbar tool): vertical mode, upload above download, each speed's unit on its own smaller line under the number (`↑ 2.47` / `Mb/s` / `↓ 9.62` / `Mb/s`), centred just above the tray. If the taskbar is narrower than that, scale it down uniformly to fit; never fall back to the one-line compact mode, which is wider.

### React

- `SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE)` scoped to Explorer's process → reposition on tray width/taskbar movement (debounced 50 ms).
- `RegisterWindowMessage("TaskbarCreated")` → Explorer restarted: drop handles, re-attach.
- `WM_DPICHANGED`, `WM_SETTINGCHANGE` (`ImmersiveColorSet`, taskbar alignment registry key `TaskbarAl`) → re-measure, re-theme, reposition.
- Taskbar auto-hide: strip is a child of the taskbar, so it hides/shows with it; no extra handling beyond reposition.

### Centered-icon overlap (Windows 11)

- Use UI Automation to get the bounding rectangle of the taskbar's app-button area (`MSTaskListWClass` on Win10; the `Taskbar.TaskList` UIA element on Win11).
- If the full strip would overlap it, switch to **compact mode**: two short lines in a smaller font without "/s" (`↑ 209 Kb` / `↓ 0.4 Mb`), about 35–40 % narrower than full. If compact still overlaps, shrink it into the gap between the app buttons and the tray (content scales down uniformly) as long as that keeps at least 70 % of its width; with no usable gap, keep compact and log; do not hide.
- Probe off the UI thread: right after each attach pass and reposition; 1 s after a window that gets a taskbar button is created, destroyed or replaced (shell hook, `RegisterShellHookWindow`), which is when the app-button row grows or shrinks; and as a safety net every 2 min while attached (not in fallback) for changes the hook can't see (pinning, search box, widgets). Never two at once. The Win11 app buttons are XAML, so centred icons growing toward the strip raise no location event. It only looks at taskbars whose layout uses the result (horizontal ones for the app-button area; secondary ones, any edge, for the XAML clock area), skips strips hidden for fullscreen, and repositions only when a result changed.

### Fallback

- If attach fails 3 consecutive times (handle missing, `SetParent` fails, or window ends up off-taskbar), switch to **floating pill mode**: always-on-top, draggable, rounded capsule placed just above the tray; remember its position. Show a one-time flyout notice explaining why. Retry attach every 60 s; return to inline mode on success.

## 7. StripView

- Font: Segoe UI Variable Small (fallback Segoe UI), ~9 pt at 100 % DPI, tabular figures; right-aligned text so changing digits never shift layout.
- Line 1: `↑ <upload>`; line 2: `↓ <download>`. Arrow glyphs tinted (upload soft teal `#2BB5A8` / dark-mode `#4FD1C5`; download = system accent). Numbers use taskbar foreground (white on dark, near-black on light).
- Data cap: when a cap is set, every arrow glyph (all layouts) turns amber once this cycle reaches 80 % of the cap (`#9D5D00` light / `#FCE100` dark) and red at 100 % (`#C42B1C` light / `#FF99A4` dark), the same thresholds and Fluent caution/critical colours as the flyout's cap bar; normal tints below 80 % or with no cap. Colours follow the taskbar's light/dark theme like the others.
- Formatting (`SpeedFormatter`): 3 significant digits, units KB/s, MB/s, GB/s (1024 base) or Kb/s, Mb/s, Gb/s (1000 base) per `units` setting (`bits` default | `bytes`). Values below 1 KB/s (or 1 Kb/s) show as `0 KB/s` / `0 Kb/s` to reduce noise; there is no B/s unit.
- Idle fade: if both directions < 1 KB/s for 5 consecutive seconds, animate opacity to 0.45 over 200 ms; restore to 1.0 over 120 ms on activity.
- Hover (600 ms delay): tooltip `Today ↓ 1.24 GB ↑ 210 MB`, plus a second line `This cycle: 62% of 50.0 GB` when a cap is set (percentage rounded down, so it never reads 80 % or 100 % before the tint changes).
- Left-click: toggle Flyout. Right-click: context menu — Settings, Pause speed display, Exit. Pausing freezes the strip and the flyout's live speeds and graph only; usage totals and the data cap keep counting.
- Hidden while `FullscreenWatcher` reports fullscreen on that monitor.

## 8. Flyout

- WPF window, Fluent theme, Mica backdrop (Win11) / solid themed background (Win10), 320 px wide, 8 px corner radius, anchored above (or beside, for side taskbars) the strip, clamped to the monitor work area.
- Closes on deactivation or Esc.
- Sections:
  1. **Header:** adapter kind icon, adapter name, IPv4 address (or "Disconnected"). An adapter whose link is down (operational status not up, or media disconnected, read every tick) shows a disconnected icon, its name and "Disconnected", never a stale address. With the adapter setting on "All adapters": a network icon, "All adapters" and "N connected" (adapters with a non-link-local IPv4 address), or "None connected".
     - **Adapter switcher:** ◀ ▶ buttons at the right of the header ("Previous adapter" / "Next adapter"; Left/Right arrow keys while the flyout has focus) page through views, wrapping at the ends: first the configured view (what the strip shows, as above), then "All adapters" (unless the configured view already shows every adapter), then each included adapter, connected ones first, otherwise in the sampler's order. A view that would repeat the configured one is left out (the configured adapter, the current best route in `auto`, and with a single adapter both "All adapters" and that adapter), so the arrows are hidden when only one view remains. A view changes only the flyout: header, graph, live speeds and Today / This cycle totals follow it (an adapter view uses that adapter's rates from `Sample.Traffic` and `Today(id)` / `ThisPeriod(id)`; "All adapters" sums them and uses `all`); the cap bar keeps the cap's own scope, and the strip and settings are never changed. Each open starts on the configured view; if the viewed adapter disappears (or becomes the configured view's adapter), the flyout falls back to the configured view. The header's accessible name includes "k of n" and the header is a polite live region, announced on each switch. View logic: `AdapterViewSwitcher` (Core).
  2. **Graph:** last 60 samples of the selected view's rates (projected from the same buffer); download as filled area (accent, 30 % fill), upload as 1.5 px line (teal); y-axis auto-scales to that view's max in window (floor 1024 B/s) with a single max label. Custom-drawn `Polyline`/`Path`, no chart library.
  3. **Live:** current ↑ / ↓ in larger type.
  4. **Totals:** Today ↓/↑ and This cycle ↓/↑ for every adapter, or the selected adapter view's own (the data-cap cycle, starting on the "Cycle resets on day" setting); cap progress bar + "x of y GB" when a cap is set (bar turns warning color ≥ 80 %, critical ≥ 100 %).
  5. **Footer:** settings gear; "New version available — vX.Y" link (opens release page in browser) when applicable; one-time notices (fallback mode, usage reset, data cap warnings).

## 9. FullscreenWatcher

- Every 1 s (piggybacks on sampler tick): get foreground window; if its rect equals its monitor's full bounds and it is not the desktop/shell window, mark that monitor fullscreen. Also honor `SHQueryUserNotificationState` (`QUNS_BUSY`, `QUNS_RUNNING_D3D_FULL_SCREEN`, `QUNS_PRESENTATION_MODE`).
- Setting `hideInFullscreen` (default true).

## 10. Settings

`%AppData%\NetStrip\settings.json`, with `version` field for migrations; unknown fields preserved.

| Key | Type / default |
|---|---|
| `version` | int, 1 |
| `units` | `bytes` \| `bits`, `bits` |
| `adapter` | `auto` \| `all` \| adapter GUID, `auto` |
| `monitors` | `primary` \| `all`, `primary` |
| `startWithWindows` | bool, mirrors the Run key (which is the source of truth) |
| `hideInFullscreen` | bool, true |
| `idleFade` | bool, true |
| `theme` | `system` \| `light` \| `dark`, `system` |
| `capGb` | number \| null, null |
| `capResetDay` | 1–28, 1 |
| `capAdapter` | `all` \| adapter GUID, `all` |
| `checkForUpdates` | bool, true |
| `floatingPosition` | `{x,y}` \| null |

- Invalid file → back up as `.bad-<timestamp>`, use defaults, log.
- File that cannot be read (read retried briefly first) → use defaults for this session, log, and do not save over it.
- Start with Windows: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\NetStrip`, written by the installer's task or the settings checkbox. The checkbox shows the Run key, ticked only when it starts this copy's exe; the app never registers itself without the user asking.
- Settings window: Fluent `Window` with grouped controls for the keys above; changes apply live.

## 11. UpdateChecker

- On startup + every 24 h if `checkForUpdates`: GET `https://api.github.com/repos/{RepoSlug}/releases/latest`, where `RepoSlug` is an MSBuild property (`<NetStripRepoSlug>` in `Directory.Build.props`, embedded as assembly metadata) set once the GitHub repo exists; empty slug disables the check. Compare semver to assembly version, ignore prereleases.
- Failures are silent (logged). Never downloads or installs.

## 12. Cross-cutting

- **Single instance:** named mutex `Local\NetStrip`; second launch signals the first to open its flyout, then exits.
- **Logging:** rolling file, `%LocalAppData%\NetStrip\logs\netstrip-<date>.log`, 7 files kept; Info by default.
- **Error handling:** global `DispatcherUnhandledException` / `AppDomain.UnhandledException` / `TaskScheduler.UnobservedTaskException` handlers log and keep running where safe. Win32 failures are logged and retried; never crash the shell integration loop.
- **Performance:** no allocations per tick in sampler hot path beyond the `Sample` record; UI updates only when formatted text changes.
- **Accessibility:** strip and flyout expose UIA names (e.g., "Upload 1.2 megabytes per second"); flyout keyboard navigable; respects high-contrast theme by using system colors.

## 13. Testing

**Unit (xUnit):**
- `SpeedFormatter`: unit boundaries, 3 significant digits, bits vs bytes.
- `NetSampler` with fake `IInterfaceTable`: delta math, variable elapsed time, counter reset → 0, adapter exclusion rules, `auto`/specific/`all` selection, adapter disappearance.
- `UsageStore` with fake clock/filesystem: midnight rollover, DST transitions, reset-day periods (incl. day 28 and short months), cap thresholds firing once per period, retention pruning, corrupt-file recovery, atomic save.
- `Settings`: defaults, migration, unknown-field preservation, invalid-file recovery.
- `UpdateChecker`: semver comparison, prerelease ignored.
- `AdapterViewSwitcher`: view order and de-duplication per adapter setting, wrap-around, selection kept by id or falling back, per-view rates and graph peak.
- `DeferredTheme`: the theme is only remembered until first use, applied once then, and live afterwards.

**Manual checklist (per release, recorded in `docs/manual-test-checklist.md`):**
- Win11: left vs centered alignment, many tray icons, auto-hide, 100/150/200 % DPI, multi-monitor (`monitors=all`), Explorer restart (`taskkill /f /im explorer.exe`), light/dark switch, fullscreen game + fullscreen video, compact mode trigger, fallback pill (simulate by blocking attach).
- Win10 22H2 VM: bottom/top/side taskbar, small taskbar buttons.
- Sleep/resume and Wi-Fi ↔ Ethernet switch.
- Flyout adapter arrows with Wi-Fi + Ethernet (and hidden with one adapter), keyboard and Narrator.

## 14. Release

- GitHub Actions on tag `v*`: build, run tests, publish
  - framework-dependent build packaged by Inno Setup (per-user install to `%LocalAppData%\Programs\NetStrip`, no admin; installs .NET 10 Desktop Runtime if missing; optional "Start with Windows" checkbox; clean uninstall incl. Run key),
  - self-contained portable zips for `win-x64` and `win-arm64`.
- Release notes from the tag annotation.
- Unsigned in v1 (SmartScreen warning documented in README). Post-v1: Azure Trusted Signing, winget manifest.

## 15. Project layout

```
NetStrip.sln
src/NetStrip/             WPF app (StripView, Flyout, Settings window, App bootstrap)
src/NetStrip.Core/        NetSampler, UsageStore, SpeedFormatter, Settings model, UpdateChecker, interfaces
src/NetStrip.Shell/       TaskbarHost, FullscreenWatcher, Win32 interop (CsWin32)
tests/NetStrip.Core.Tests/
installer/netstrip.iss
.github/workflows/release.yml
docs/manual-test-checklist.md
```

`NetStrip.Core` has no WPF or Win32 dependency (interfaces only), so it builds and tests anywhere.

## 16. Work split for implementation

Independent tracks after a shared skeleton (solution, projects, core interfaces/types):

1. **Core data:** `NetSampler` + Win32 `IInterfaceTable` impl, `SpeedFormatter`, `UsageStore`, `Settings`, `UpdateChecker`, all unit tests.
2. **Shell integration:** `TaskbarHost` (attach, react, compact, fallback), `FullscreenWatcher`.
3. **UI:** `StripView`, `Flyout`, settings window, context menu, tooltip (against fake sample stream until tracks 1–2 land).
4. **Release:** Inno Setup script, GitHub Actions workflow, README, manual checklist.

Integration step wires tracks together in `App` bootstrap, then runs the manual checklist.
