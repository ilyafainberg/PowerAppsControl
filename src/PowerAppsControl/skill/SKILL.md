---
name: "PowerAppsControl"
description: "Drive the PowerAppsControl MCP server to UX-test a Power App end to end: open and verify an app URL, let the user choose a mode (smoke test = in-depth read-only exploration that produces a repeatable natural-language test plan; run my test plan; or dogfood = systematic bug hunt that produces a severity-ranked issue report with repro evidence), then run it in a recorded session and produce a video + report. Handles canvas apps, model-driven apps, the maker portal, and Power Pages. Triggers: 'test power app', 'test my power app', 'UX test', 'smoke test my app', 'record a test of my app', 'PowerAppsControl', '/PowerAppsControl', 'run a test plan on my app', 'load test my power app', 'dogfood', 'QA my app', 'bug hunt', 'find issues in my app', 'exploratory test'. Use whenever the user wants to exercise, validate, record, load-test, or find bugs in a Power App's UI."
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

Present the **three** modes as buttons with **`m_ask_user`** (see "Clickable choices" above)
and wait for the user's pick. `open_power_app` also returns a mode prompt as text, but on
Scout it will NOT have rendered buttons — so you render them. Offer:

1. **Smoke test** — you explore the app **in depth and read-only**, then produce a
   **repeatable, plain-English test plan** (a natural-language script both a human and an
   agent can read and re-run). Recommended when the goal is a reusable regression asset.
2. **Run my test plan** — the user tells you what to test in plain language (or names a
   saved plan) and you run it, optionally N times or across M windows.
3. **Dogfood (bug hunt)** — you systematically explore the app to **find bugs, UX issues,
   and polish problems**, and produce a **severity-ranked issue report** (`findings.md`)
   with full repro evidence for each finding. Recommended when the goal is *quality
   feedback to hand to the owning team*, not a re-runnable script.

Do **not** assume a mode. Wait for the choice.

> Smoke test vs. Dogfood: both explore read-only, but they produce **different
> deliverables**. Smoke test's artifact is a *test plan* (what the app does, re-runnable).
> Dogfood's artifact is an *issue report* (what's wrong with the app, with proof). If the
> user says "find bugs / QA / what's broken", pick Dogfood; if they say "give me a test I
> can rerun", pick Smoke test.

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

   **C) Dogfood (bug hunt).** Explore like a real user, find issues, and document each one
   with evidence **as you find it**. See the full **Dogfood mode** section below.

3. **`end_test_session()`** — stops the video, releases the window, writes `report.html`
   + `report.json` + screenshots into the session folder. Give the user the paths. For a
   dogfood run, also point them at the `findings.md` you authored (see below).

---

## Dogfood mode (bug hunt) — the deep dive

Dogfood mode encodes a QA analyst's judgment: **what counts as a bug, how severe it is, how
to prove it, and when to stop.** The driving is the easy part; the discipline below is the
value. Do this **inside an active session** (Step 3) so the whole run is recorded to
`session.mp4` and every screenshot lands in the session folder.

### D0. Calibrate

At the start of the run, read **Appendix A — Issue taxonomy** (below) to load the severity
levels, issue categories, and the Power-Platform-specific things to watch (canvas
non-response, model-driven grid/subgrid/lookup failures, delegation limits, permission
banners). This is what turns "click around" into "recognize a real finding and rate it."

### D1. Prepare the findings report (write early)

Immediately create **`findings.md`** in the session folder using the template in
**Appendix B — Dogfood report template** (below), filling the header (app, date, session,
scope, environment). Prefer a **non-production / developer environment** for anything that
could mutate data. Write this file **before** you start hunting — sessions end by
abandonment, and a report that only exists "at the end" is a report that gets lost.

### D2. Orient

`screenshot_window` for an initial capture, then map the app: main navigation, the primary
areas/screens, and the core end-to-end workflows a real user would run. Note which surface
you're on (canvas vs. model-driven vs. maker portal vs. Power Pages) — it dictates how you
drive and verify (see "Canvas vs model-driven" below).

### D3. Explore + document in one pass (repro-first)

Work through the app **systematically**: top-level nav first, then each area; test
interactive elements, forms (submit-*intent* only, non-destructive), navigation, and the
state matrix (empty / loading / error / overflow). Check for slow screens and error banners.
Drive with **`smoke_step`** (it also records to the exploration log) and free-form desktop
tools for recon.

**The instant you find an issue, stop and document it before moving on** — do not explore
the whole app and write up later. Match the evidence to the issue type:

- **Interactive / behavioral bug** (something that needs interaction to reproduce):
  1. Screenshot **before**, perform the action, screenshot **after** — into the session
     `screenshots\` folder as `issue-{NNN}-step-{k}.png`, ending with an annotated
     `issue-{NNN}-result.png` of the broken state.
  2. Note the **step marker / approximate timestamp into `session.mp4`** so a reviewer can
     scrub to it. You do **not** start a separate per-issue video — PowerAppsControl already
     records the entire session.
  3. Write numbered repro steps in `findings.md`, each referencing its screenshot.
- **Static / visible-on-load bug** (typo, clipped text, misalignment, placeholder text,
  error banner on load): a **single annotated screenshot** is enough. Set "Session video @"
  to `N/A`. No step-by-step.

**Append each issue to `findings.md` immediately**, increment the counter
(`ISSUE-001`, `ISSUE-002`, …), and never delete or rewrite prior screenshots/entries.

### D4. Stop rule

Aim for **5–10 well-documented issues**, then wrap up. **Depth of evidence beats raw
count** — five issues with clean repro are worth more than twenty vague notes. If one area is
a cluster of problems, go deeper there rather than padding breadth.

### D5. Wrap up (on demand — never blocking)

When the user says to wrap up (or you hit the stop rule): re-read `findings.md`, update the
**severity summary counts** so they match the actual `ISSUE-` blocks, then call
**`end_test_session()`** to finalize `session.mp4` + `report.html`. Tell the user the
`findings.md` path, the total issue count, the breakdown by severity, and the most critical
items. Because you wrote findings incrementally, an interrupted session still yields a usable
report.

### Dogfood behavior rules

- **Repro is everything, but sized to the bug.** Interactive bugs get step screenshots +
  a session-video marker; static bugs get one screenshot. Don't over-document a typo.
- **Verify every action.** After each meaningful action, `screenshot_window` to confirm the
  app actually responded. A "green" coordinate click that produced no visible change is a
  **real finding** (log it honestly), not a tool glitch.
- **Type like a human when it matters.** Prefer character-by-character typing during the
  recorded repro so the video is watchable; pace actions so a reviewer can follow at 1×.
- **Test like a user, not a robot.** Run realistic end-to-end workflows; click what a real
  user would click; enter realistic data.
- **Read-only by default.** Never save/submit/delete real records or run destructive
  command-bar actions without explicit user confirmation; prefer a dev environment for
  anything that could mutate data.
- **Don't audit source.** You test what you observe in the running app, not its code.
- **Write forward, never backward.** Don't delete output, don't restart the session mid-run.

---

## The see → act → verify loop

After every meaningful action, **screenshot_window to confirm** the app did what you
expected before continuing. Skipping the verify screenshot is the #1 cause of cascading
failure — Power Apps screens can take 300–1500 ms to render (popup, focus shift, slow load).
Prefer `waitForElement` steps in scripts to synchronize on load.

---

## Writing the test plan (natural language — the Smoke-test deliverable)

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

> Bridge from Dogfood to regression: a confirmed reproducible bug from a dogfood run makes a
> great regression test. Offer to convert its repro steps into a saved natural-language test
> plan (`save_test_plan`) so "Run my test plan" mode can re-check the fix later.

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

**Smoke test / Run my test plan:**
- App URL verified as a real Power App (Step 1).
- The user picked a mode via buttons (Step 2) — you did not assume.
- Work ran inside a session that was **started and ended** (Step 3); a `report.html`
  (+ `report.json`, screenshots, and video if FFmpeg is present) was produced and the path
  given to the user.
- Findings reported honestly, including any coordinate clicks that didn't visibly take
  effect. Nothing destructive was done without explicit confirmation.

**Dogfood (bug hunt):**
- `findings.md` was created **at the start** and appended to **as each issue was found**.
- Every issue has evidence sized to its type (interactive → step screenshots + session-video
  marker; static → one annotated screenshot), a severity, and a category.
- The severity summary counts match the `ISSUE-` blocks.
- The session was **started and ended** (so `session.mp4` + `report.html` exist); the user
  was given the `findings.md` path, the total, the severity breakdown, and the top issues.
- Nothing destructive was done without explicit confirmation; a dev environment was preferred.

---

## Appendix A — Issue taxonomy (Power Apps)

Read this at the start of a **Dogfood** run to calibrate what counts as a finding and how
severe it is. Adapted for Power Platform surfaces (canvas, model-driven, maker portal,
Power Pages) driven through this server.

### Severity levels

| Severity | Definition |
|----------|------------|
| **critical** | Blocks a core workflow, causes data loss, or crashes/hangs the app |
| **high** | Major feature broken or unusable, no workaround |
| **medium** | Feature works but with noticeable problems; a workaround exists |
| **low** | Minor cosmetic or polish issue |

### Categories

**Visual / UI** — broken/misaligned layout; overlapping or clipped text; inconsistent
spacing/padding; missing or broken icons/images; theme or high-contrast rendering issues;
z-index/stacking (elements hidden behind others; modal behind backdrop); font rendering;
color-contrast problems; animation jank.

**Functional** — broken links / navigation to the wrong screen; buttons/controls that **do
nothing** on click; **"green click that didn't take effect"** (a coordinate click reported as
fired but the app didn't visibly respond — a real finding, verify with a follow-up
screenshot); form validation that rejects valid or accepts invalid input; incorrect
redirects; silent failures; state not persisted when expected (lost on refresh / navigation /
back); race conditions (double-submit, stale data); broken search / filter / sort /
pagination; file upload/download / attachment failures.

**UX** — confusing navigation; missing loading indicator or feedback after an action; slow or
unresponsive interactions (>300 ms perceived delay); unclear error messages (raw exception
text, GUIDs, "An error has occurred"); missing confirmation before a destructive action; dead
ends (no way back or forward); inconsistent patterns across similar screens; poor focus
management; unintuitive defaults; missing or unhelpful empty states.

**Content** — typos or grammatical errors; outdated/incorrect text; placeholder / lorem-ipsum
/ "Label" text left in; truncated text without tooltip or expansion; missing or wrong field
labels; inconsistent terminology.

**Performance** — slow screen loads (>3 s, common with large Dataverse views); janky
scrolling/animations; large layout shifts (content jumping); app slows over time within a
session.

**Errors / diagnostics** — visible error banners/toasts/dialogs; "Unexpected error" /
correlation-ID dialogs in model-driven apps; business-rule / plug-in errors surfaced to the
user; permission/privilege errors ("You don't have access").

**Accessibility (best-effort, visual)** — missing alt text or unlabeled controls (where
observable); a control you can't reach by keyboard / focus trap; insufficient color contrast;
focus not visible.

### Power Platform–specific things to watch

- **Canvas apps** render onto a canvas and expose almost nothing to UI Automation beyond the
  player chrome. Expect to verify by screenshot, not by element. A control that looks
  clickable but yields no visible change is a finding.
- **Model-driven apps** expose rich UI Automation (stable names/automationIds). Watch for:
  grid load spinners that never resolve, subgrids that don't refresh after edit, command-bar
  buttons greyed unexpectedly, lookup/quick-find returning nothing for known records, form
  tabs that fail to load.
- **Delegation / data limits** — large datasets silently capped; filters/sorts returning
  partial or wrong results.
- **Environment & permissions** — banner warnings, trial/expired notices, missing connection
  prompts, "add connection" loops.
- **Slow first paint** — give model-driven/canvas apps a few seconds; distinguish a genuine
  hang from normal cold-load latency before filing a critical.

### Exploration checklist (per screen/feature)

1. **Visual scan** — screenshot; look for layout, alignment, rendering issues.
2. **Interactive elements** — click every button/link/control; is there feedback? (Screenshot
   after to confirm the app responded.)
3. **Forms** — fill and submit-*intent* only, in a non-prod/dev environment; test empty
   submission, invalid input, and edge cases. **Never** save/submit/delete real records
   without explicit user confirmation.
4. **Navigation** — follow nav paths; check breadcrumbs, back button, deep links.
5. **States** — empty, loading, error, full/overflow.
6. **Errors** — trigger and read error messages; note raw/unhelpful ones.
7. **Consistency** — compare similar screens for pattern drift.
8. **Performance** — note any screen that takes >3 s or feels janky.

---

## Appendix B — Dogfood report template

Copy this into the session folder as `findings.md` at the **start** of a Dogfood run, fill
the header, then append one `### ISSUE-NNN` block per finding as you go.

````markdown
# Dogfood Report: {APP_NAME}

| Field | Value |
|-------|-------|
| **Date** | {DATE} |
| **App URL** | {URL} |
| **Session** | {SESSION_NAME} |
| **Session folder** | {SESSION_FOLDER} |
| **Session video** | session.mp4 |
| **Scope** | {SCOPE} |
| **Environment** | {ENV — prefer a non-production / developer environment} |

## Summary

| Severity | Count |
|----------|-------|
| Critical | 0 |
| High | 0 |
| Medium | 0 |
| Low | 0 |
| **Total** | **0** |

## Issues

<!--
Copy the block below for each issue, and APPEND IT IMMEDIATELY when you find it — never
batch to the end (the session may be abandoned). PowerAppsControl records the WHOLE session
to session.mp4, so interactive bugs reference a timestamp/step marker into it rather than a
separate per-issue video.
-->

### ISSUE-001: {Short title}

| Field | Value |
|-------|-------|
| **Severity** | critical / high / medium / low |
| **Category** | visual / functional / ux / content / performance / errors / accessibility |
| **Surface** | canvas / model-driven / maker portal / Power Pages |
| **Screen / URL** | {where it was found} |
| **Session video @** | {approx timestamp or step # in session.mp4, or N/A for static} |

**Description**

{What is wrong, what was expected, and what actually happened.}

**Repro steps**

1. Navigate to {screen}
   ![Step 1](screenshots/issue-001-step-1.png)

2. {Action — e.g., open the "Warehouses" view and click the first record}
   ![Step 2](screenshots/issue-001-step-2.png)

3. {Action — e.g., edit "Name", then click Save}
   ![Step 3](screenshots/issue-001-step-3.png)

4. **Observe:** {what goes wrong — e.g., the subgrid does not refresh and shows stale data}
   ![Result](screenshots/issue-001-result.png)

---
````
