// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Ilya Fainberg
// -----------------------------------------------------------------------------
//  PowerAppsControl — Program entry point and MCP host wiring
//
//  Licensed under the GNU General Public License v3.0 or later (GPL-3.0-or-later).
//  See the LICENSE file in the project root for the full text.
//
//  Author: Ilya Fainberg <ifain@microsoft.com>
// -----------------------------------------------------------------------------

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace PowerAppsControl;

/// <summary>
/// Boots the MCP server over stdio, registers every [McpServerTool] in this
/// assembly, and ships a ServerInstructions block that teaches the agent how to
/// run UX tests against a Power App responsibly.
/// </summary>
internal sealed class Program
{
    private const string ServerInstructions = """
        PowerAppsControl turns hardware-level desktop control into a UX-testing rig for Power Apps. It
        drives a canvas or model-driven app exactly like a real user (synthesized mouse + keyboard, DWM-aware
        screenshots, UI-Automation element lookup), records every test session to video, replays a test
        script any number of times — optionally across several windows at once for light load testing — and
        writes an HTML report that summarizes the runs. Power Apps usually runs inside a browser window
        (Edge/Chrome at make.powerapps.com or apps.powerapps.com) or the Power Apps player.

        THE WORKFLOW — ALWAYS FOLLOW THESE THREE STEPS IN ORDER:

        STEP 1 — OPEN & VERIFY. The user says "test Power App <URL>". Call open_power_app(url) — it opens the
        URL in the browser, reads the address bar and confirms the page really is a Power App (canvas app,
        model-driven app, maker portal, or Power Pages site).
          • If the user gave NO URL, ASK them for the Power App URL first. Do not guess one.
          • If open_power_app says it is NOT a Power App, tell the user and ask for the correct URL. Do NOT
            proceed to testing.

        STEP 2 — CHOOSE THE MODE. Once verified, ASK the user which of these TWO modes they want (via clickable
        buttons; do not assume):
          1) Smoke test — you explore the app IN DEPTH and READ-ONLY, then produce a repeatable, plain-English
             test plan (a natural-language script both a human and an agent can read and re-run).
          2) Run my test plan — the user tells you what to test in plain language (or names a saved plan) and you
             run it.

        STEP 3 — EXECUTE & REPORT. Run the chosen mode inside a session, then end it to produce the video +
        report. Concretely:
          • start_test_session(appName, windowQuery)  — pins the app window(s) under agent control (crimson
            frame), starts the video recording, shows the live status pill. Testing tools are GATED until called.
          • Then, depending on the mode:
              1) Smoke  → explore the app with smoke_step across its primary flows (open menus/nav, inspect
                 forms, sort/filter grids, open records, navigate) — strictly READ-ONLY: do NOT save, submit,
                 delete, or send anything. Then call get_exploration_log, AUTHOR a natural-language test plan
                 from it, and save it with save_test_plan (which puts it in the report and the library).
              2) Run my plan → take the user's plain-language plan (or load_test_plan), compile it to run_test_script
                 steps yourself, and run it.
          • end_test_session()  — stops the video, writes report.html + report.json (the report shows the
            natural-language plan, NOT JSON), releases the windows. ALWAYS end the session, even if a run failed.

        NATURAL LANGUAGE, NOT JSON: the user never writes JSON. A test plan is a plain-English numbered list of
        steps that name controls by their visible label and state the expected outcome. YOU compile that plan
        into run_test_script's JSON steps at run time — that JSON is an internal execution detail, never shown to
        the user and never the saved artifact.

        CLEAR VISUAL CUES (do not defeat them): every window under test wears the crimson 'Under Agent Control'
        frame (rounded, hugging the window; works for MAXIMIZED windows too), with an integrated status pill on
        its top edge showing a REC dot + the current step. The user can STOP everything at any time by clicking
        the ✕ on a control frame — when that happens your next tool call returns '⛔ ABORTED BY USER'; stop
        immediately, end the session, and ask how to proceed.

        RUN-TIME EXECUTION FORMAT (JSON — you author this, the user does not): run_test_script takes
        { "name": "...", "steps": [ ... ] }. Each step has an "action" and a human "description". Actions:
          • clickElement   {name?, automationId?, controlType?} — find + invoke a control via UI Automation
                                                                (PREFERRED: no pixel guessing; survives layout).
          • type           {keys}                            — type text / chords, e.g. "hello{Enter}".
          • waitForElement {name?/automationId?, timeoutMs?} — poll until a control appears (sync gate).
          • assertElement  {name?/automationId?, shouldExist?} — pass/fail check that a control is / isn't present.
          • scroll         {amount, horizontal?, x?, y?}     — wheel scroll (negative = down).
          • wait           {ms}                              — fixed pause for the UI to settle.
          • click          {x, y, button?, clicks?}         — WINDOW-relative pixels (last resort — only for
                                                                canvas controls UI Automation can't see).
          • screenshot     {description}                     — capture the app into the report.
        Prefer clickElement/waitForElement/assertElement by name. Always start with a couple of waitForElement
        steps so it is robust to Power Apps load time (screens can take 300–1500 ms).

        REPEAT & LOAD: run_test_script(runs=N) replays the whole plan N times in the SAME window and aggregates
        timings + pass rate. run_test_script(parallelWindows=M) opens M copies of the app and replays the plan in
        all of them at once to simulate concurrent users — the report breaks results down per window and per run.

        STANDARD SEE→ACT→VERIFY LOOP still applies while exploring: screenshot_window to SEE state, decide,
        act (click_in_window / send_keys / find_element / smoke_step), screenshot again to VERIFY. Skipping
        the verify screenshot is the #1 cause of cascading failure — the UI may have shifted.

        COORDINATES are physical screen pixels; click_in_window and 'click' steps take WINDOW-relative
        pixels and the server does the math. The server is PerMonitor-V2 DPI-aware, so screenshot pixels ARE
        the pixels you pass back.

        SAFETY: this drives the user's REAL desktop and REAL Power App — there is no sandbox. Do not submit
        forms that create/delete real records, send messages, or make purchases without explicit user
        confirmation. Prefer read-only navigation when uncertain, and never test destructive actions during a
        multi-window load run.
        """;

    private static async Task<int> Main(string[] args)
    {
        // CLI sub-commands: register / unregister this server with the MCP client
        // configs (used by the installer and available to portable users), plus help.
        if (args.Length > 0)
        {
            bool quiet = args.Contains("--quiet", StringComparer.OrdinalIgnoreCase);
            switch (args[0].ToLowerInvariant())
            {
                case "--register":
                case "register":
                    return McpRegistration.Register(quiet);
                case "--unregister":
                case "unregister":
                    return McpRegistration.Unregister(quiet);
                case "--ensure-ffmpeg":
                case "ensure-ffmpeg":
                    return FfmpegSetup.RunCli(quiet);
                case "--check-update":
                case "check-update":
                    return Updater.RunCheckCli();
                case "--update":
                case "update":
                    return Updater.RunUpdateCli();
                case "--help":
                case "-h":
                case "/?":
                    PrintHelp();
                    return 0;
            }
        }

        var builder = Host.CreateApplicationBuilder(args);

        // MCP transports JSON-RPC over stdout. Anything written to stdout that
        // isn't a valid JSON-RPC frame breaks the protocol, so every logger MUST
        // emit to stderr.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        builder.Services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new() { Name = "PowerAppsControl", Version = "1.5.0" };
                o.ServerInstructions = ServerInstructions;
            })
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        await builder.Build().RunAsync();
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            PowerAppsControl — MCP server for UX testing of Power Apps.

            USAGE:
              PowerAppsControl                Run as an MCP stdio server (default; launched by an MCP host).
              PowerAppsControl --register     Register this server with Microsoft Scout and the GitHub
                                              Copilot CLI (writes %USERPROFILE%\.copilot configs).
              PowerAppsControl --unregister   Remove this server from those configs.
              PowerAppsControl --ensure-ffmpeg  Check for FFmpeg (needed for video recording) and
                                              install it if missing (winget, else direct download).
              PowerAppsControl --check-update  Check GitHub Releases for a newer version.
              PowerAppsControl --update        Download and install the latest version (shows progress).
              PowerAppsControl --help         Show this help.

            Add --quiet to suppress output on register/unregister.
            Restart Scout / the Copilot CLI after (un)registering for changes to take effect.
            """);
    }
}
