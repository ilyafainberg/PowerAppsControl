# Changelog

All notable changes to **PowerAppsControl** are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.1.0] — 2026-07-02

### Added
- **Self-registration with MCP hosts.** `PowerAppsControl.exe --register` /
  `--unregister` add or remove the server from **Microsoft Scout**
  (`m-mcp-servers.json`) and the **GitHub Copilot CLI** (`mcp-config.json`) under
  `%USERPROFILE%\.copilot`, merging into existing config without disturbing other
  servers. The registered tool list is discovered by reflection so it never drifts.
- **The installer now auto-registers** the server on install (as the original
  non-elevated user) and **unregisters** it on uninstall.
- `--help` output describing the run / register / unregister modes.

## [1.0.0] — 2026-07-02

Initial public release.

### Added
- **Three-step UX-testing workflow** for Power Apps: `open_power_app` (open a URL and
  verify it is a canvas app, model-driven app, maker portal, or Power Pages site) →
  choose a mode → execute and report.
- **Three test modes:** scripted (bring your own test script), agent-led **smoke test**
  (explore and auto-generate a repeatable script), and explore-and-propose-a-plan.
- **Session lifecycle** (`start_test_session` / `end_test_session`) that pins the app
  window(s) under agent control (crimson "Under Agent Control" frame — works for
  maximized windows), records the session to **video** (FFmpeg), and shows a live status
  **HUD** pill.
- **Test runner** with per-step pass/fail, timing, `waitForElement` / `assertElement`
  synchronization, repeat runs (`runs=N`), and multi-window fan-out (`parallelWindows=M`)
  for light load testing.
- **HTML + JSON report** per session with KPI tiles, embedded video, and expandable
  per-step tables with inline screenshots.
- **Script library:** `save_test_script` / `load_test_script` / `list_test_scripts`.
- **Clickable choices via MCP elicitation** (`ask_user_choice`, and the mode prompt in
  `open_power_app`), with graceful text fallback when the client lacks the capability.
- **Opens the app in a dedicated new browser window** for clean recordings.
- Full desktop-control tool surface inherited from the TotalControl engine
  (`find_window`, `control_window`, `screenshot_window`, `click_in_window`, `send_keys`,
  `find_element`, `record_window`, and more).

[1.1.0]: https://github.com/ilyafainberg/PowerAppsControl/releases/tag/v1.1.0
[1.0.0]: https://github.com/ilyafainberg/PowerAppsControl/releases/tag/v1.0.0
