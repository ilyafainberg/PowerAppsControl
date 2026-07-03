---
name: "PowerAppsControl"
description: "Drive the PowerAppsControl MCP server to UX-test a Power App end to end: open and verify an app URL, let the user choose a mode (smoke test / explore & propose a plan / bring your own plan), then run it in a recorded session and produce a video + HTML report. Handles canvas apps, model-driven apps, the maker portal, and Power Pages. Triggers: 'test power app', 'test my power app', 'UX test', 'smoke test my app', 'record a test of my app', 'PowerAppsControl', '/PowerAppsControl', 'run a test plan on my app', 'load test my power app'. Use whenever the user wants to exercise, validate, record, or load-test a Power App's UI."
---

# PowerAppsControl — Power Apps UX testing playbook

This skill tells you how to drive the **PowerAppsControl** MCP server. That server exposes
the tools; this skill is the *workflow* that uses them. If the tools below are not
available, the server is not registered — tell the user to install/register it
(`PowerAppsControl.exe --register`) and restart the host, then stop.

**Golden rule: always follow the three steps in order — never skip verification, never
skip the mode choice, always end the session.**

## Clickable choices — use the host's picker, not server elicitation

At every decision point (mode choice, plan approval, re-run vs. fix, "which window?"),
present the options as **clickable buttons using the host's own picker tool**:

- In **Microsoft Scout**, call **`m_ask_user`** with 2–5 discrete options. Put any context
  or recommendation in your assistant message *before* the call, and don't end that message
  with your own question — the tool's `question` is the only prompt shown.
- If the host has no native picker, ask the question in chat text with a short numbered
  list.

Do **not** rely on the MCP server's built-in elicitation for buttons: most hosts
(Scout included) don't advertise the MCP `elicitation` capability, so the server tools
(`open_power_app`'s mode prompt, `ask_user_choice`) fall back to plain text and **no
buttons appear**. Treat those tools' output as text guidance and render the actual buttons
yourself with `m_ask_user`. The user can always type a freeform answer instead of clicking.

The server drives the user's **real desktop and real Power App** — there is no sandbox.
Default to **read-only** (navigate, sort, filter, open records, assert fields). Never
create/update/delete records, submit forms, send messages, or run destructive command-bar
actions without **explicit** user confirmation, and never do anything destructive during a
multi-window load run.

---

## Step 1 — Open & verify the app

1. Get the app URL. If the user didn't give one, **ask for it** (offer likely candidates as
   buttons with `m_ask_user` if you have any). Accept canvas play links
   (`apps.powerapps.com/play/...`), model-driven apps (`*.dynamics.com/main.aspx?appid=...`),
   the maker portal (`make.powerapps.com`), and Power Pages (`*.powerappsportals.com`).
2. Call **`open_power_app(url)`**. It opens the URL in a **new dedicated browser window**
   (clean recording), reads the address bar, and verifies it really is a Power App.
   - If it returns **not a Power App**, tell the user and ask for the correct URL. **Do
     not proceed.**
   - On success, note the **window title** it reports — you'll pass a distinctive chunk of
     it as `windowQuery` in Step 3.
3. Give a model-driven / canvas app a few seconds to load before testing.

If the user wants video and you're unsure FFmpeg is present, call
**`ensure_ffmpeg(checkOnly=true)`** — and `ensure_ffmpeg()` (no args) to install it if
missing. Sessions still run without it, just with no video.

---

## Step 2 — Choose the mode (clickable buttons)

Present the three modes as buttons with **`m_ask_user`** (see "Clickable choices" above) and
wait for the user's pick. `open_power_app` also returns a mode prompt as text, but on Scout
it will NOT have rendered buttons — so you render them. Offer:

1. **Smoke test** — you explore the app yourself and produce a report + a suggested
   repeatable script.
2. **Explore & propose a plan** — you recon the app, then present a step-by-step plan and
   **wait for the user's approval** before running anything.
3. **Provide your own plan** — the user gives you the steps (or a saved script name).

Do **not** assume a mode. Wait for the choice.

---

## Step 3 — Execute in a recorded session, then report

Always: **`start_test_session` → do the work → `end_test_session`.** The testing tools are
gated on an active session, so nothing runs (and nothing is recorded/reported) until you
start one. Always end the session even if a run fails — that's what writes the report.

1. **`start_test_session(appName, windowQuery)`** — pins the app window under agent control
   (crimson "Under Agent Control" frame, works for maximized windows too), starts the
   video, shows the live HUD. Use the app label for `appName` and a distinctive part of the
   window title from Step 1 for `windowQuery` (e.g. `"PowerCAT BVA"`, `"Warehouses Active"`).
2. Then, by mode:

   **A) Smoke test** — recon with `screenshot_window`, then walk the primary flows with
   repeated **`smoke_step(action, description, ...)`** (each call performs ONE action and
   records it to a draft script). Cover: open menus/nav, fill an input, submit/navigate,
   go back. Screenshot to VERIFY after meaningful actions. When done, call
   **`get_suggested_script`** to hand the user the repeatable script, and optionally
   **`save_test_script(name, fromDraft=true)`**.

   **B) Explore & propose** — recon with `screenshot_window` + `find_element`, then present
   a concrete step table and get approval via **`m_ask_user`** ("Approve / Change / Cancel").
   After approval, run it with **`run_test_script(scriptJson=..., runs=1)`**.

   **C) Own plan** — build the script from the user's steps and call
   **`run_test_script(scriptJson=...)`** (or `scriptName=...` for a saved one). Offer
   `runs=N` (repeat) and `parallelWindows=M` (load) if they want.

3. **`end_test_session()`** — stops the video, releases the window, writes
   `report.html` + `report.json` + screenshots into the session folder. Give the user the
   report path and offer to open it.

---

## The see → act → verify loop

After every meaningful action, **screenshot_window to confirm** the app did what you
expected before continuing. Skipping the verify screenshot is the #1 cause of cascading
failure — Power Apps screens can take 300–1500 ms to render (popup, focus shift, slow load).
Prefer `waitForElement` steps in scripts to synchronize on load.

---

## Test script format (JSON)

```json
{
  "name": "Submit a referral",
  "appName": "Contoso Referrals",
  "steps": [
    { "action": "waitForElement", "description": "app loaded",  "name": "New referral", "timeoutMs": 8000 },
    { "action": "clickElement",   "description": "open form",   "name": "New referral" },
    { "action": "type",           "description": "patient",     "keys": "Jane Doe{Tab}" },
    { "action": "clickElement",   "description": "submit",      "name": "Submit" },
    { "action": "assertElement",  "description": "confirmation","name": "Thank you", "shouldExist": true }
  ]
}
```

Step actions and their fields:

| action | fields | notes |
|--------|--------|-------|
| `click` | `x`, `y`, `button?`, `clicks?` | WINDOW-relative pixels (same coords as a `screenshot_window` PNG; (0,0) = window top-left). |
| `clickElement` | `name?`, `automationId?`, `controlType?` | **Preferred** — invoke a control via UI Automation. No pixel guessing; survives layout/DPI changes. |
| `type` | `keys` | `send_keys` syntax: text + `{Enter}`, `{Tab}`, `{Ctrl+A}`, `{Key N}`. |
| `scroll` | `amount`, `horizontal?`, `x?`, `y?` | Negative = down/left. |
| `wait` | `ms` | Fixed pause. |
| `waitForElement` | `name?`/`automationId?`/`controlType?`, `timeoutMs?` | Poll until a control appears (sync gate). Start scripts with one. |
| `assertElement` | `name?`/`automationId?`/`controlType?`, `shouldExist?` | Pass/fail presence (or absence) check. |
| `screenshot` | `description?` | Captures the app into the report. |

---

## Canvas vs model-driven — pick your interaction style

- **Model-driven apps & the maker portal** expose rich UI Automation: controls have stable
  **names/automationIds**. Prefer **`clickElement` / `waitForElement` / `assertElement`** —
  they're robust and mostly go green.
- **Canvas apps** render onto a canvas and expose almost nothing to UI Automation beyond the
  Power Apps *player* chrome. You'll mostly use **`screenshot_window` + coordinate `click`
  steps** (read the pixel positions from the screenshot). "Green" for a coordinate click only
  means the click fired — **always screenshot afterward and verify the app actually
  responded**, and report honestly if it didn't (e.g. a dropdown that didn't open is a real
  finding, not a tool failure).

---

## Repeat & load testing

- `run_test_script(runs=N)` replays the whole script N times in the same window (consistency).
- `run_test_script(parallelWindows=M)` drives M windows together to simulate concurrent
  users. Open M copies of the app first and pass `maxWindows>=M` to `start_test_session`.
  The report breaks results down per window and per run. Never load-test destructive actions.

---

## User abort

The user can STOP everything by clicking the ✕ on the crimson control frame. When that
happens your next tool call returns `⛔ ABORTED BY USER`. Stop immediately, call
`end_test_session`, and ask how to proceed. Do not re-acquire the window automatically.

---

## Tool quick reference

Workflow (this server):
- `open_power_app(url, timeoutSeconds?, browserQuery?, newWindow?)` — Step 1.
- `ask_user_choice(question, options[])` — server-side elicitation; **only renders buttons
  if the host supports MCP elicitation (Scout does not).** Prefer the host's `m_ask_user`.
- `ensure_ffmpeg(checkOnly?)` — install FFmpeg for recording if missing.
- `start_test_session(appName, windowQuery?, maxWindows?, fps?, recordVideo?)` — Step 3 start.
- `smoke_step(action, description?, ...)` — one exploratory action + record to draft.
- `get_suggested_script(name?)` — draft script from smoke steps.
- `run_test_script(scriptJson? | scriptName?, runs?, parallelWindows?)` — run a plan.
- `save_test_script / load_test_script / list_test_scripts` — script library.
- `test_session_status()` — inspect the active session.
- `end_test_session()` — stop, write report, release window. **Always call this.**

Low-level desktop tools (for recon / free-form driving):
`find_window`, `control_window`, `release_window`, `screenshot_window`, `screenshot_screen`,
`crop_screenshot`, `click_in_window`, `send_keys`, `scroll_mouse`, `find_element`,
`hover_preview`, `record_window`, plus absolute `move_mouse` / `click_mouse` / `drag_mouse`.

---

## Definition of done

- App URL verified as a real Power App (Step 1).
- The user picked a mode via buttons (Step 2) — you did not assume.
- Work ran inside a session that was **started and ended** (Step 3); a `report.html`
  (+ `report.json`, screenshots, and video if FFmpeg is present) was produced and the path
  given to the user.
- Findings reported honestly, including any coordinate clicks that didn't visibly take
  effect. Nothing destructive was done without explicit confirmation.
