<div align="center">

# PowerAppsControl

### UX-test your Power Apps like a real user — driven, recorded, and reported by an AI agent.

[![Release](https://img.shields.io/github/v/release/ilyafainberg/PowerAppsControl?sort=semver)](https://github.com/ilyafainberg/PowerAppsControl/releases)
[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6)



<video src="https://github.com/user-attachments/assets/d6599636-906b-47ab-88ad-ddcf99eb32a5" controls muted autoplay loop playsinline width="880"></video>

</div>

---

An **MCP server for UX testing of Power Apps**. It drives a canvas or model-driven
app exactly like a real user — synthesized mouse + keyboard, DWM-aware screenshots,
UI-Automation element lookup — then **records the session to video**, **replays a
test script** any number of times (optionally across several windows at once for
light load testing), and writes a **self-contained HTML report** summarizing the
runs.

It is a specialization of the [TotalControl](https://github.com/ilyafainberg/TotalControl-MCP-Server) desktop-control
engine: the whole low-level interaction layer is reused, with a UX-testing layer
(sessions, scripts, smoke tests, recording, reporting) built on top.

Built for **Microsoft Scout**, **GitHub Copilot CLI** and any MCP-compatible host. Windows only
(.NET 10, PerMonitor-V2 DPI-aware).

---

## Quick start (first-time users)

1. **Install.** Download the latest **[Release](https://github.com/ilyafainberg/PowerAppsControl/releases)**:
   - **Installer (recommended):** grab `PowerAppsControl-<ver>-setup.zip`, unzip, run the
     `.exe`, follow the wizard. It auto-registers the server (and the `/PowerAppsControl`
     skill) with **Microsoft Scout** and the **GitHub Copilot CLI**, and offers to install
     **FFmpeg** (needed for the video recording).
   - **Portable:** grab `PowerAppsControl-<ver>-portable-win-x64.zip`, unzip, then run
     `PowerAppsControl.exe --register` once.
   - Unsigned build → Windows SmartScreen may warn: **More info → Run anyway**.
2. **Restart your host** (Scout / Copilot CLI) so it loads the new server + skill.
3. **Just ask.** In the chat, say:
   > **Test my Power App https://apps.powerapps.com/play/…**

   The agent will open and verify the app, ask **how** you want to test it
   (smoke test / propose a plan / your own plan) with clickable buttons, then run it in a
   recorded session and hand you an HTML report.

That's it — you don't need to learn the tools; the companion skill drives the workflow.

---

## ⚠️ Please read — safety & liability

**This tool controls your REAL mouse, keyboard, and screen, and drives your REAL,
signed-in Power App. There is no sandbox.** By using it you accept the following:

- **It acts on live data.** Runs happen against whatever tenant/app/records you point it at.
  It defaults to **read-only** navigation, but any test that clicks *Save*, *Submit*,
  *Delete*, *Send*, or a command-bar action **can create, modify, delete, or send real
  data**. Only approve such steps when you are certain, and prefer a **non-production /
  developer environment** for anything destructive.
- **It takes over input.** While a session is active the tool moves your cursor and types.
  Don't use the machine for other work during a run. You can **stop instantly** by clicking
  the **✕** on the crimson "Under Agent Control" frame around the app window.
- **It records your screen.** Session videos and screenshots are written to
  `%USERPROFILE%\PowerAppsControl\Sessions\…`. They may capture **sensitive on-screen data**
  (records, names, tokens visible in the app). Review before sharing; nothing is uploaded
  anywhere by this tool.
- **It downloads from the internet.** The updater and FFmpeg installer fetch from GitHub /
  winget / a static FFmpeg build. Only run `--update` if you trust the source repo.
- **No warranty.** This software is provided **"AS IS", without warranty of any kind**, and
  is licensed under the GPL-3.0 (see [Sections 15–16 of the LICENSE](LICENSE) — Disclaimer of
  Warranty and Limitation of Liability). **You are solely responsible** for what you test,
  in which environment, and for any consequences. The authors accept no liability for data
  loss, unintended changes, downtime, or any other damages.
- **Use only where you're authorised.** Test apps and tenants you own or have explicit
  permission to test. Respect your organisation's policies and any applicable regulations.

If you don't accept these terms, don't run it.

---

## Two ways to test

You describe what you want in **plain language** — you never write JSON or scripts. Both modes
produce a recorded session and an HTML report.

### 1. Smoke test — let the agent explore (recommended)
Point the agent at an app and say *"smoke test it"*. It explores the app **in depth and
read-only** — opening menus, inspecting forms, sorting/filtering grids, opening records,
navigating screens — without saving, submitting, or deleting anything. It then hands you a
**repeatable, plain-English test plan** (a natural-language script both you and an agent can
read and re-run), included in the report and saveable to reuse as a regression test.

### 2. Run your test plan — you say what to test
Tell the agent what to test in plain language (e.g. *"open the first warehouse, check the
Name field, go back, sort by Created On"*), or point it at a saved plan. It runs the plan —
optionally **N times** or across **several windows at once** for a light load test — with
per-step pass/fail and timing.

> Under the hood the agent compiles your plain-language plan into precise UI steps at run
> time. That machine form is an internal detail — you only ever read and write natural language.

---

## Clear visual cues (low friction)

While a session is live the user always sees what's happening:

- **Crimson "Under Agent Control" frame** around every window being driven — a rounded frame
  that hugs the window border (works for **maximized windows** too), with an **integrated
  status pill** on its top edge showing a pulsing REC dot and the current step.
- **Instant abort:** click the ✕ on any control frame to stop everything. The next
  tool call returns `⛔ ABORTED BY USER`, the recording finalizes, and the agent
  stops.

## Clickable choices (MCP elicitation)

The server drives the workflow's decision points as **clickable buttons** using the MCP
**elicitation** capability — the choice logic lives in the server, not the agent prompt:

- `open_power_app` verifies the URL, then asks **which mode** to run (Smoke test / Run my
  plan) as buttons and returns the user's pick.
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
| `start_test_session(appName, windowQuery?, maxWindows?, fps?, recordVideo?)` | Resolve + control the app window(s), start recording, show the frame status pill. |
| `test_session_status()` | Current session: app, windows, mode, runs so far, exploration steps. |
| `end_test_session()` | Stop recording, release windows, write `report.html` + `report.json`. |
| `smoke_step(action, description?, …)` | Perform one read-only exploratory action **and** record it to the exploration log. |
| `get_exploration_log(name?)` | Return the plain-English exploration log (author your test plan from it). |
| `save_test_plan(name, plan)` | Save a natural-language plan to the library and attach it to the report. |
| `load_test_plan(name)` / `list_test_plans()` | Read / list saved natural-language plans. |
| `run_test_script(scriptJson, runs?, parallelWindows?)` | Execute a plan (the agent compiles the plan to steps) `runs` times across `parallelWindows` windows. |

The desktop engine tools are also available for free-form exploration:
`find_window`, `control_window`, `release_window`, `screenshot_window`,
`click_in_window`, `send_keys`, `scroll_mouse`, `find_element`, `hover_preview`,
`record_window`, `crop_screenshot`, and the absolute mouse/keyboard tools.

---

## Test plans are natural language

You describe a test in **plain language** — a numbered list of steps that name each control
by its visible label and state the expected outcome. The agent (or a smoke test) produces
plans in exactly this form, and the report shows them this way too:

```
# Contoso Referrals — submit a referral

1. Wait for the app to finish loading — the "New referral" button should be visible.
2. Click "New referral" — the referral form opens.
3. Enter "Jane Doe" in the "Patient name" field, then Tab.
4. Click "Submit".
5. Verify a "Thank you" confirmation appears.
```

That's the whole authoring model — **no JSON, no coordinates.** Behind the scenes the agent
compiles each line into precise UI-Automation actions at run time (preferring controls by
name so the plan survives layout, DPI, and theme changes); that machine form is never shown
to you and never the saved artifact.

---

## Repeat & load testing

- **Repeat:** ask to run a plan **N times** in the same window to check consistency.
- **Load:** ask to run it across **M windows at once** to simulate concurrent users (open M
  copies of the app first).

Because Windows serializes real synthesized input (one cursor, one foreground window), a load
pass **interleaves**: for each step the runner executes it on every window in turn, so all M
windows advance through the plan together. The report breaks results down **per window and
per run**.

---

## Outputs

Everything for a session lands in one portable folder:

```
%USERPROFILE%\PowerAppsControl\
  ├─ Plans\                        saved natural-language test plans (*.md)
  └─ Sessions\<app>_<timestamp>\
       ├─ session.mp4              the session recording
       ├─ report.html             self-contained HTML report (open in any browser)
       ├─ report.json             the raw result data
       └─ screenshots\            per-step captures (failures + screenshot steps)
```

The HTML report includes KPI tiles (runs, pass rate, avg duration, windows), the
embedded video, per-run expandable step tables with inline screenshots, and — for
smoke sessions — the **repeatable, plain-English test plan**.

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
PowerAppsControl.exe --register       # add server + skill to Scout + GitHub Copilot CLI
PowerAppsControl.exe --unregister     # remove from both
PowerAppsControl.exe --ensure-ffmpeg  # install FFmpeg if missing (video recording)
PowerAppsControl.exe --check-update   # check GitHub Releases for a newer version
PowerAppsControl.exe --update         # download + install the latest (with a progress bar)
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

### Companion skill (`/PowerAppsControl`)

Registering also installs a **companion skill** that teaches the agent the end-to-end
workflow (open & verify → choose a mode → recorded session → report). Without it, agents
see the tools but don't know how to sequence them. It's installed for **both hosts** —
`%USERPROFILE%\.copilot\m-skills\PowerAppsControl\` (Scout, plus a skills-metadata entry)
and `%USERPROFILE%\.copilot\skills\PowerAppsControl\` (GitHub Copilot CLI). Invoke it
explicitly with `/PowerAppsControl`, or just say "test my power app <url>". `--unregister`
removes it from both.

The skill also drives all clickable decisions (mode choice, plan approval) through the
**host's native picker** (`m_ask_user` in Scout), because most hosts don't advertise the
MCP `elicitation` capability that the server's own `ask_user_choice` relies on.

## Updating

PowerAppsControl updates itself from **GitHub Releases** (no backend). `--check-update`
reports whether a newer version exists; `--update` downloads the right asset (portable zip
or installer, matching how you installed) with a progress bar and applies it via a helper
that waits for the host to close, then re-registers. Agents can also call the
`check_for_update` / `update_server` MCP tools. After an update, **restart your MCP host**
to load the new version.

## Build & run

```powershell
dotnet build PowerAppsControl.sln -c Release
# run directly as an MCP stdio server:
dotnet run --project src/PowerAppsControl -c Release
```

### Repository layout

```
PowerAppsControl.sln            solution (root)
global.json                     .NET SDK pin
src/PowerAppsControl/           the MCP server project (source + skill/)
installer/                      Inno Setup script + update helper
.github/workflows/release.yml   tag-triggered release pipeline
docs/                           demo video + assets
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
