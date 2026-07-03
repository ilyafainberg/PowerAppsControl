# Changelog

All notable changes to **PowerAppsControl** are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.3.2] — 2026-07-03

### Changed
- **CI: bumped GitHub Actions to Node-24 versions** (`checkout@v5`, `setup-dotnet@v5`,
  `action-gh-release@v3`), clearing the Node 20 deprecation warning. The release-publish
  step now only runs on tag pushes, so a manual `workflow_dispatch` run builds and validates
  without creating a release.

## [1.3.1] — 2026-07-03

### Changed
- **Companion skill now drives clickable choices through the host's own picker**
  (`m_ask_user` in Scout) instead of MCP server elicitation. Scout doesn't advertise the
  MCP `elicitation` capability, so the server's `ask_user_choice` / `open_power_app` mode
  prompt only returned text and no buttons rendered. The skill now instructs the agent to
  render the mode choice, plan approval, and other decisions with `m_ask_user`, and treats
  the server tools' output as text guidance.

## [1.3.1] — 2026-07-03

### Added
- **Self-updater from GitHub Releases.** `--check-update` reports whether a newer release
  exists; `--update` downloads the matching asset (portable zip or installer) **with a
  console progress bar** and applies it via a helper (`apply-update.cmd`) that waits for the
  host to close, extracts over the install (or runs setup silently), and re-registers. Also
  exposed as the `check_for_update` / `update_server` MCP tools.
- **Companion skill now installs for the GitHub Copilot CLI too** (`~/.copilot/skills/`),
  not just Scout — so both hosts get the workflow playbook.

### Changed
- **Clickable choices go through the host's own picker** (`m_ask_user` in Scout) instead of
  MCP server elicitation. Scout doesn't advertise the MCP `elicitation` capability, so the
  server's `ask_user_choice` / `open_power_app` mode prompt only returned text and no
  buttons appeared. The skill now renders mode choice, plan approval, and other decisions
  with `m_ask_user`.

## [1.3.0] — 2026-07-03

### Added
- **Companion skill `/PowerAppsControl`.** A workflow playbook that teaches the agent how
  to sequence the tools (open & verify → choose a mode → recorded session → report). This
  fixes agents seeing the tools but not knowing what to do with them. It ships next to the
  exe (`skill/SKILL.md`) and is installed by `--register` into
  `%USERPROFILE%\.copilot\m-skills\PowerAppsControl` and Scout's skill index (when present);
  `--unregister` removes it.

## [1.2.0] — 2026-07-02

### Added
- **FFmpeg auto-provisioning.** `PowerAppsControl.exe --ensure-ffmpeg` and the new
  `ensure_ffmpeg` MCP tool check for FFmpeg and install it if missing — winget first
  (Gyan.FFmpeg), then a per-user direct download into
  `%LOCALAPPDATA%\PowerAppsControl\ffmpeg` (no admin rights). `FindFfmpeg` now also
  searches that cache.
- **The installer offers to install FFmpeg** via a checked-by-default task on install.
- The "no video" session warning now points at `ensure_ffmpeg`.

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

[1.3.2]: https://github.com/ilyafainberg/PowerAppsControl/releases/tag/v1.3.2
[1.3.1]: https://github.com/ilyafainberg/PowerAppsControl/releases/tag/v1.3.1
[1.3.0]: https://github.com/ilyafainberg/PowerAppsControl/releases/tag/v1.3.0
[1.2.0]: https://github.com/ilyafainberg/PowerAppsControl/releases/tag/v1.2.0
[1.1.0]: https://github.com/ilyafainberg/PowerAppsControl/releases/tag/v1.1.0
[1.0.0]: https://github.com/ilyafainberg/PowerAppsControl/releases/tag/v1.0.0
