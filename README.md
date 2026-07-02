# PowerAppsControl

[![Release](https://img.shields.io/github/v/release/ilyafainberg/PowerAppsControl?sort=semver)](https://github.com/ilyafainberg/PowerAppsControl/releases)
[![Build](https://github.com/ilyafainberg/PowerAppsControl/actions/workflows/release.yml/badge.svg)](https://github.com/ilyafainberg/PowerAppsControl/actions/workflows/release.yml)
[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6)

An **MCP server for UX testing of Power Apps**. It drives a canvas or model-driven
app exactly like a real user — synthesized mouse + keyboard, DWM-aware screenshots,
UI-Automation element lookup — then **records the session to video**, **replays a
test script** any number of times (optionally across several windows at once for
light load testing), and writes a **self-contained HTML report** summarizing the
runs.

It is a specialization of the [TotalControl](../TotalControl) desktop-control
engine: the whole low-level interaction layer is reused, with a UX-testing layer
(sessions, scripts, smoke tests, recording, reporting) built on top.

Built for **Microsoft Scout** and any MCP-compatible host. Windows only
(.NET 10, PerMonitor-V2 DPI-aware).

---

## Two ways to test

Both produce a recorded session and an HTML report.

### 1. Scripted — you already know the flow
Give the agent a **test script** (inline JSON or a saved script name) and it replays
it, step by step, with per-step pass/fail and timing.

### 2. Smoke test — let the agent explore
Point the agent at an app and say *"smoke test it"*. It walks the primary flows on
its own using `smoke_step` (each call **performs one action and records it**), then
hands you a **clean, repeatable script** via `get_suggested_script` that you can
save and rerun as a regression test.

---

## Clear visual cues (low friction)

While a session is live the user always sees what's happening:

- **Crimson "Under Agent Control" frame** around every window being driven — works for **maximized windows** too (it insets and clamps to the monitor so the frame and the ✕ tag stay on-screen).
- **Live HUD pill** at the top-center of the screen — a pulsing red REC dot, the app
  name, the mode (SMOKE / UX TEST), the current run (e.g. `Run 2/5`) and the current
  step.
- **Instant abort:** click the ✕ on any control frame to stop everything. The next
  tool call returns `⛔ ABORTED BY USER`, the recording finalizes, and the agent
  stops.

## Clickable choices (MCP elicitation)

The server drives the workflow's decision points as **clickable buttons** using the MCP
**elicitation** capability — the choice logic lives in the server, not the agent prompt:

- `open_power_app` verifies the URL, then asks **which mode** to run (Smoke / Explore &
  propose / Own plan) as buttons and returns the user's pick.
- `ask_user_choice(question, options[])` is a reusable prompt for any decision — approve a
  plan, re-run vs. fix, etc.

If the connected MCP client doesn't support elicitation, these gracefully fall back to
returning the options as text so the agent can ask in chat.

---

## Session lifecycle (enforced)

Testing tools are gated on an active session, so every action is framed, recorded
and reported.

```
start_test_session  ──►  run_test_script / smoke_step  ──►  end_test_session
   (control + REC + HUD)        (do the testing)          (stop REC, write report)
```

| Tool | Purpose |
|------|---------|
| `start_test_session(appName, windowQuery?, maxWindows?, fps?, recordVideo?)` | Resolve + control the app window(s), start recording, show the HUD. |
| `test_session_status()` | Current session: app, windows, mode, runs so far, draft-script size. |
| `end_test_session()` | Stop recording, release windows, write `report.html` + `report.json`. |
| `run_test_script(scriptJson? \| scriptName?, runs?, parallelWindows?)` | Replay a script `runs` times across `parallelWindows` windows. |
| `smoke_step(action, description?, …)` | Perform one exploratory action **and** append it to the draft script. |
| `get_suggested_script(name?)` | Return the draft script assembled from the smoke steps. |
| `save_test_script(name, scriptJson? \| fromDraft?)` | Persist a script to the library. |
| `load_test_script(name)` / `list_test_scripts()` | Read / list saved scripts. |

The desktop engine tools are also available for free-form exploration:
`find_window`, `control_window`, `release_window`, `screenshot_window`,
`click_in_window`, `send_keys`, `scroll_mouse`, `find_element`, `hover_preview`,
`record_window`, `crop_screenshot`, and the absolute mouse/keyboard tools.

---

## Test script format

```json
{
  "name": "Submit a referral",
  "appName": "Contoso Referrals",
  "steps": [
    { "action": "waitForElement", "description": "app loaded",  "name": "New referral", "timeoutMs": 8000 },
    { "action": "clickElement",   "description": "open form",   "name": "New referral" },
    { "action": "type",           "description": "patient name","keys": "Jane Doe{Tab}" },
    { "action": "clickElement",   "description": "submit",      "name": "Submit" },
    { "action": "assertElement",  "description": "confirmation","name": "Thank you", "shouldExist": true }
  ]
}
```

### Step actions

| Action | Fields | Notes |
|--------|--------|-------|
| `click` | `x`, `y`, `button?`, `clicks?` | Window-relative pixels — the same coordinate space as a `screenshot_window` PNG (`(0,0)` = window top-left). |
| `clickElement` | `name?`, `automationId?`, `controlType?` | **Preferred.** Finds + invokes a control via UI Automation (no pixel guessing; survives layout/DPI changes). |
| `type` | `keys` | `send_keys` syntax: text plus `{Enter}`, `{Tab}`, `{Ctrl+A}`, `{Key N}`, … |
| `scroll` | `amount`, `horizontal?`, `x?`, `y?` | Negative amount = down/left. |
| `wait` | `ms` | Fixed pause. |
| `waitForElement` | `name?`/`automationId?`/`controlType?`, `timeoutMs?` | Polls until the control appears (a synchronization gate). |
| `assertElement` | `name?`/`automationId?`/`controlType?`, `shouldExist?` | Pass/fail presence (or absence) check. |
| `screenshot` | `description?` | Captures the app window into the report. |

**Tip:** start scripts with a `waitForElement` so they're robust to Power Apps load
time (screens can take 300–1500 ms). Prefer `clickElement` over `click` whenever the
control has a stable name or automationId.

---

## Repeat & load testing

- `run_test_script(runs = N)` replays the whole script **N times** in the same window
  and aggregates pass rate + timings.
- `run_test_script(parallelWindows = M)` drives **M windows together** to simulate
  concurrent users. Open M copies of the app first and pass `maxWindows >= M` to
  `start_test_session`.

Because Windows serializes real synthesized input (one cursor, one foreground
window), a load pass **interleaves**: for each step the runner executes it on every
window in turn, so all M windows advance through the script together. The report
breaks results down **per window and per run**.

---

## Outputs

Everything for a session lands in one portable folder:

```
%USERPROFILE%\PowerAppsControl\
  ├─ Scripts\                      saved test scripts (*.json)
  └─ Sessions\<app>_<timestamp>\
       ├─ session.mp4              the session recording
       ├─ report.html             self-contained HTML report (open in any browser)
       ├─ report.json             the raw result data
       └─ screenshots\            per-step captures (failures + screenshot steps)
```

The HTML report includes KPI tiles (runs, pass rate, avg duration, windows), the
embedded video, per-run expandable step tables with inline screenshots, and — for
smoke sessions — the suggested repeatable script.

---

## Requirements

- **Windows 10/11**, **.NET 10 SDK** (`net10.0-windows`).
- **FFmpeg** for video recording. You don't have to install it by hand — the installer
  offers to fetch it, and you can run `PowerAppsControl.exe --ensure-ffmpeg` or call the
  `ensure_ffmpeg` MCP tool at any time (winget first, then a per-user direct download).
  Without FFmpeg, sessions still run and report — they just have no video. To point at an
  existing binary, set `POWERAPPSCONTROL_FFMPEG` to `ffmpeg.exe`.

## Install (from a Release)

Grab the latest build from the [Releases page](https://github.com/ilyafainberg/PowerAppsControl/releases):

- **Installer (recommended):** download `PowerAppsControl-<ver>-setup.zip`, unzip, run
  the `.exe`, follow the wizard. The build is unsigned, so Windows SmartScreen may show
  "unknown publisher" — choose **More info → Run anyway**. The installer **automatically
  registers** PowerAppsControl as an MCP server with **Microsoft Scout** and the
  **GitHub Copilot CLI** (see below) and unregisters it on uninstall.
- **Portable:** download `PowerAppsControl-<ver>-portable-win-x64.zip`, unzip anywhere,
  run `PowerAppsControl.exe`. No admin rights, no installer. It's self-contained (bundles
  the .NET runtime), so it runs on a clean machine. Register it with one command:
  ```powershell
  .\PowerAppsControl.exe --register
  ```

Verify your download against `SHA256SUMS.txt` attached to the release.

## MCP client registration

PowerAppsControl can register itself with the two supported MCP hosts — no manual JSON
editing required:

```powershell
PowerAppsControl.exe --register       # add to Scout + GitHub Copilot CLI
PowerAppsControl.exe --unregister     # remove from both
PowerAppsControl.exe --ensure-ffmpeg  # install FFmpeg if missing (video recording)
PowerAppsControl.exe --help           # usage
```

`--register` merges an entry into (preserving everything else already there):

| Host | Config file | Entry |
|------|-------------|-------|
| GitHub Copilot CLI | `%USERPROFILE%\.copilot\mcp-config.json` | `mcpServers["PowerAppsControl"]` |
| Microsoft Scout | `%USERPROFILE%\.copilot\m-mcp-servers.json` | `servers["powerappscontrol"]` |

The Scout entry is only written if that file already exists (i.e. Scout is installed).
The tool list is discovered by reflection, so it always matches what the server exposes.
**Restart Scout / the Copilot CLI after registering** for the change to take effect.

## Build & run

```powershell
dotnet build -c Release
# run directly as an MCP stdio server:
dotnet run -c Release
```

### Manual registration (if you prefer to edit config yourself)

```json
{
  "mcpServers": {
    "PowerAppsControl": {
      "command": "C:\\path\\to\\PowerAppsControl.exe"
    }
  }
}
```

Point `command` at the installed/portable `PowerAppsControl.exe` (or the `bin\Release\net10.0-windows\` build output during development).

## Environment variables

| Variable | Effect |
|----------|--------|
| `POWERAPPSCONTROL_FFMPEG` | Explicit path to `ffmpeg.exe` for recording. |
| `POWERAPPSCONTROL_REQUIRE_WINDOW_SELECTION` | `0`/`false`/`off` disables the desktop-engine window-selection gate (on by default). |

## Safety

These tools drive the user's **real desktop and real Power App** — there is no
sandbox. The agent is instructed not to submit forms that create/delete real
records, send messages, or make purchases without explicit confirmation, and to
avoid destructive actions during multi-window load runs.

## License

Licensed under the **GNU General Public License v3.0 or later (GPL-3.0-or-later)** —
see [LICENSE](LICENSE). Author: Ilya Fainberg.
