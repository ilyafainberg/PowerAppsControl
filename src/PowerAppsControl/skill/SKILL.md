---
name: "PowerAppsControl"
description: "Drive the PowerAppsControl MCP server to UX-test a Power App end to end: open and verify an app URL, let the user choose a mode (smoke test = in-depth read-only exploration that produces a repeatable natural-language test plan; or run my test plan), then run it in a recorded session and produce a video + HTML report with a plain-English test plan. Handles canvas apps, model-driven apps, the maker portal, and Power Pages. Triggers: 'test power app', 'test my power app', 'UX test', 'smoke test my app', 'record a test of my app', 'PowerAppsControl', '/PowerAppsControl', 'run a test plan on my app', 'load test my power app'. Use whenever the user wants to exercise, validate, record, or load-test a Power App's UI."
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

Present the **two** modes as buttons with **`m_ask_user`** (see "Clickable choices" above) and
wait for the user's pick. `open_power_app` also returns a mode prompt as text, but on Scout it
will NOT have rendered buttons — so you render them. Offer:

1. **Smoke test** — you explore the app **in depth and read-only**, then produce a **repeatable,
   plain-English test plan** (a natural-language script both a human and an agent can read and
   re-run). This is the default recommendation.
2. **Run my test plan** — the user tells you what to test in plain language (or names a saved
   plan) and you run it.

Do **not** assume a mode. Wait for the choice.

---

## Step 3 — Execute in a recorded session, then report

Always: **`start_test_session` → do the work → `end_test_session`.** The testing tools are
gated on an active session, so nothing runs (and nothing is recorded/reported) until you
start one. Always end the session even if a run fails — that's what writes the report.

1. **`start_test_session(appName, windowQuery)`** — pins the app window under agent control
   (a crimson rounded frame that hugs the window border, works for maximized windows too),
   starts the video, and shows an **integrated status pill on the frame's top edge** with a
   pulsing REC dot + the live step. Use the app label for `appName` and a distinctive part of
   the window title from Step 1 for `windowQuery` (e.g. `"PowerCAT BVA"`, `"Warehouses Active"`).
2. Then, by mode:

   **A) Smoke test — in-depth, non-destructive exploration → a natural-language plan.**
   Recon with `screenshot_window`, then explore the primary flows with repeated
   **`smoke_step(action, description, ...)`**: open menus & navigation, inspect forms and
   fields, sort/filter grids, open records, move between screens. Stay **strictly read-only** —
   do **not** save, submit, delete, or send anything. Screenshot to VERIFY after meaningful
   actions. When done, call **`get_exploration_log`** (a plain-English log of what you did),
   then **author a natural-language test plan** from it (see "Writing the test plan" below) and
   save it with **`save_test_plan(name, plan)`** — that puts the plan in the report and the
   library. Present the plan to the user.

   **B) Run my test plan.** Ask the user what to test (plain language) or load a saved plan
   with **`load_test_plan(name)`**. Compile their plan into `run_test_script` steps yourself and
   run it with **`run_test_script(scriptJson=...)`**. Offer `runs=N` (repeat) and
   `parallelWindows=M` (load) if they want.

3. **`end_test_session()`** — stops the video, releases the window, writes
   `report.html` + `report.json` + screenshots into the session folder. The report shows the
   **natural-language plan** (not JSON). Give the user the report path and offer to open it.

---

## The see → act → verify loop

After every meaningful action, **screenshot_window to confirm** the app did what you
expected before continuing. Skipping the verify screenshot is the #1 cause of cascading
failure — Power Apps screens can take 300–1500 ms to render (popup, focus shift, slow load).
Prefer `waitForElement` steps in scripts to synchronize on load.

---

## Writing the test plan (natural language — this is the deliverable)

**The test plan is a plain-English document, and the user never writes JSON.** For a smoke
test, `get_exploration_log` returns what you observed; you turn that into a **detailed,
natural-language, numbered list of steps**. Never a list of pixel coordinates — a coordinate
plan breaks the instant the layout, resolution, theme, or data changes; a natural-language
plan stays valid because an agent re-resolves each step to a concrete control when it runs.

For each step, describe **the intent, the target control by its visible name/label, and the
expected outcome**:

```
# Contoso Referrals — submit a referral

1. Wait for the app to finish loading — the "New referral" button should be visible.
2. Click the "New referral" button — the referral form opens.
3. In the "Patient name" field, enter "Jane Doe", then Tab to the next field.
4. Click "Submit".
5. Verify a "Thank you" confirmation message appears.
```

Rules for good steps: one action each; name the control the way the user sees it; state what
should happen; add explicit "wait for … to appear" steps around anything that loads; note any
read-only vs. data-changing step. **Save the plan with `save_test_plan(name, plan)`** — it goes
into the HTML report (rendered as readable text, not JSON) and the library so it can be re-run.

### Running a plan (how JSON fits in — internal only)

When you *execute* a plan, you translate each plain-language line into `run_test_script`'s JSON
steps **at run time**. This JSON is an internal execution detail — **never show it to the user,
never save it as the artifact.** Prefer `clickElement` / `waitForElement` / `assertElement` by
name; fall back to a coordinate `click` only for canvas controls UI Automation can't see. The
saved, reportable, human-readable artifact is always the natural-language plan.

<details>
<summary>Run-time JSON shape (for your reference when calling run_test_script)</summary>

```json
{
  "name": "Submit a referral",
  "steps": [
    { "action": "waitForElement", "description": "app loaded",  "name": "New referral", "timeoutMs": 8000 },
    { "action": "clickElement",   "description": "open form",   "name": "New referral" },
    { "action": "type",           "description": "patient",     "keys": "Jane Doe{Tab}" },
    { "action": "clickElement",   "description": "submit",      "name": "Submit" },
    { "action": "assertElement",  "description": "confirmation","name": "Thank you", "shouldExist": true }
  ]
}
```

</details>

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
- `smoke_step(action, description?, ...)` — one read-only exploratory action + record to the log.
- `get_exploration_log(name?)` — plain-English log of the smoke exploration (author your plan from it).
- `run_test_script(scriptJson, runs?, parallelWindows?)` — run a plan (you compile the NL plan to JSON steps).
- `save_test_plan(name, plan)` / `load_test_plan(name)` / `list_test_plans()` — natural-language plan library.
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
