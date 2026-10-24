# Manual test checklist

Run this checklist for every release (spec §13). Record the build version, date, and tester below, and note any failures against the item.

- Version tested: ____________
- Date: ____________
- Tester: ____________

## Windows 11

- [ ] Left-aligned taskbar
- [ ] Centered taskbar
- [ ] Many tray icons (taskbar overflow present)
- [ ] Auto-hide taskbar
- [ ] 100 % display scaling
- [ ] 150 % display scaling
- [ ] 200 % display scaling
- [ ] Multiple monitors (`monitors=all`)
- [ ] Explorer restart (`taskkill /f /im explorer.exe`, then let Explorer restart)
- [ ] Switch between light and dark mode
- [ ] Fullscreen game
- [ ] Fullscreen video
- [ ] Compact mode trigger
- [ ] Fallback pill (simulate by blocking the taskbar attach)

## Windows 10 22H2 (VM)

- [ ] Bottom taskbar
- [ ] Top taskbar
- [ ] Left or right (side) taskbar
- [ ] Small taskbar buttons

## Power and network

- [ ] Sleep and resume
- [ ] Switch from Wi-Fi to Ethernet
- [ ] Switch from Ethernet to Wi-Fi
- [ ] Flyout adapter arrows: with Wi-Fi + Ethernet, ◀ ▶ (and Left/Right keys) page through the views and the header, speeds, graph and totals follow; reopening starts on the configured view; with one adapter the arrows are hidden; Narrator reads the arrows and the new view
