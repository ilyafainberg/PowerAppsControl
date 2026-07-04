// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Ilya Fainberg
// -----------------------------------------------------------------------------
//  PowerAppsControl — UX testing MCP tools
//
//  Licensed under the GNU General Public License v3.0 or later (GPL-3.0-or-later).
//  See the LICENSE file in the project root for the full text.
//
//  Author: Ilya Fainberg <ifain@microsoft.com>
//
//  The testing layer exposed to the agent, on top of the TotalControl desktop
//  engine:
//    start_test_session / test_session_status / end_test_session  — lifecycle
//    run_test_script                                              — scripted runs (× runs × windows)
//    smoke_step / get_suggested_script                            — agent-led exploration → script
//    save_test_script / load_test_script / list_test_scripts      — script library
//
//  All testing tools require an active session (RequireSession gate) so every
//  action is framed (crimson), recorded (video) and reported (HTML).
// -----------------------------------------------------------------------------

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using ModelContextProtocol.Server;

namespace PowerAppsControl;

[McpServerToolType]
public static class TestingTools
{
    // =========================================================================
    //  Step 1 — open the URL and verify it is a Power App
    // =========================================================================

    [McpServerTool(Name = "open_power_app")]
    [Description(
        "STEP 1 of the testing workflow. Opens a URL in the browser and VERIFIES it is actually a Power App " +
        "(a canvas app, model-driven app, the maker portal, or a Power Pages site) before any testing begins.\n" +
        "It navigates the default browser to the URL, waits for it to load, reads the browser address bar via " +
        "UI Automation, and checks the host against known Power Platform domains (apps.powerapps.com, " +
        "make.powerapps.com, *.dynamics.com model-driven apps, *.powerappsportals.com / *.powerpages.microsoft.com, " +
        "etc.). By default it opens the app in a NEW, dedicated browser window (cleaner recording — the window " +
        "title is just the app, not '… and N more pages'). Returns whether it is a Power App, the detected URL, " +
        "and the browser window to target with start_test_session.\n" +
        "IF IT IS NOT A POWER APP: tell the user the page does not look like a Power App and ask for the correct " +
        "app URL — do NOT proceed to testing.\n" +
        "IF NO URL WAS GIVEN by the user: ask them for the Power App URL first; do not call this without one.\n" +
        "After a successful verification this tool ALSO asks the user (via clickable buttons, when the client " +
        "supports it) WHICH MODE to run — Smoke test / Explore & propose a plan / Provide their own plan — and " +
        "returns their choice so you can proceed straight to STEP 3.")]
    public static async Task<string> OpenPowerApp(
        [Description("The Power App URL to open and verify, e.g. 'https://apps.powerapps.com/play/e/.../a/...' or a make.powerapps.com link.")]
        string url,
        [Description("Seconds to wait for the page to load and the URL to resolve before giving up. Default 20. Range 3..90.")]
        int timeoutSeconds = 20,
        [Description("Optional browser process/title hint to locate the window (e.g. 'msedge', 'chrome'). Default: auto-detect the foreground browser.")]
        string? browserQuery = null,
        [Description("Open the app in a NEW, dedicated browser window (recommended — gives a clean recording). Default true. Set false to open a tab in the existing window.")]
        bool newWindow = true,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "No URL provided. Ask the user for the Power App URL, then call open_power_app(url=...).";
        if (!url.Contains("://")) url = "https://" + url;
        timeoutSeconds = Math.Clamp(timeoutSeconds, 3, 90);

        // Snapshot existing browser windows so we can prefer the NEW window we're about to open.
        var preExisting = SnapshotBrowserWindows(browserQuery);

        // Launch the URL. Prefer a NEW dedicated browser window (cleaner recording); a Chromium
        // '--new-window' opens a separate top-level window with its own single tab, so the window
        // title is just the app (not "… and N more pages"). Falls back to the shell (new tab) if
        // no Chromium browser is found.
        try
        {
            var browser = FindBrowser(browserQuery);
            if (newWindow && browser is not null)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = browser,
                    Arguments = $"--new-window \"{url}\"",
                    UseShellExecute = false,
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
        }
        catch (Exception ex) { return $"Failed to open the browser for '{url}': {ex.Message}"; }

        // Poll: find the browser window and read its address bar until the host resolves.
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        DesktopTools.AppWindow? win = null;
        string? seenUrl = null;
        var browserHints = string.IsNullOrWhiteSpace(browserQuery)
            ? new[] { "msedge", "chrome", "firefox" }
            : new[] { browserQuery! };

        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(600);

            // 1) Prefer a NEW browser window (handle not present before we launched).
            if (newWindow)
            {
                var fresh = FindNewBrowserWindow(browserHints, preExisting);
                if (fresh is not null) win = fresh;
            }

            // 2) Otherwise prefer the foreground window if it is a browser (the tab we just opened).
            if (win is null)
            {
                var fg = DesktopTools.Describe(DesktopTools.ForegroundWindow());
                if (IsBrowser(fg.ProcessName)) win = fg;
            }

            // 3) Last resort: first browser window matching a hint.
            if (win is null)
            {
                foreach (var h in browserHints)
                {
                    try { win = DesktopTools.ResolveMany(h, 1).FirstOrDefault(); } catch { win = null; }
                    if (win is not null) break;
                }
            }
            if (win is null) continue;

            seenUrl = DesktopTools.ReadBrowserUrl(win.Value.Hwnd);
            var (isApp, _) = DesktopTools.LooksLikePowerApp(seenUrl, win.Value.Title);

            // Only finish once it's a Power App AND the tab title has resolved past a generic
            // placeholder ("Untitled"/"Loading"), so the suggested windowQuery is meaningful —
            // unless we're near the deadline, in which case accept whatever we have.
            bool titleReady = !IsGenericTitle(win.Value.Title);
            bool nearDeadline = DateTime.UtcNow > deadline.AddSeconds(-2);
            if (isApp && (titleReady || nearDeadline)) break;
        }

        if (win is null)
            return $"Opened '{url}' but could not locate the browser window. If it opened, tell me the window title " +
                   "or pass browserQuery, then we can continue.";

        var (isPowerApp, reason) = DesktopTools.LooksLikePowerApp(seenUrl, win.Value.Title);

        var sb = new StringBuilder();
        if (isPowerApp)
        {
            sb.AppendLine($"✅ Verified Power App — {reason}.");
            sb.AppendLine($"  URL:    {seenUrl ?? url}");
            sb.AppendLine($"  Window: '{win.Value.Title}' [{win.Value.ProcessName}.exe, PID {win.Value.Pid}]");
            sb.AppendLine();

            // STEP 2 — ask the user which mode to run, as clickable buttons (MCP elicitation).
            var mode = await Elicitation.AskChoiceAsync(server,
                "How do you want to test this Power App?", "mode", TestModeChoices, cancellationToken);

            var windowQuery = ShortTitle(win.Value.Title);
            if (mode is not null)
            {
                var label = TestModeChoices.FirstOrDefault(c => c.Value == mode).Label ?? mode;
                sb.AppendLine($"▶ Mode selected by user: {label}.");
                sb.AppendLine(ModeGuidance(mode, windowQuery));
            }
            else
            {
                // Client didn't render buttons (or user dismissed) → ask in text.
                sb.AppendLine("STEP 2 — ask the user which mode to run (present these as buttons via m_ask_user):");
                sb.AppendLine("  1) Smoke test — I explore the app in depth (read-only) and give you a repeatable, plain-English test plan.");
                sb.AppendLine("  2) Run my test plan — you tell me what to test (in plain language, or a saved plan) and I run it.");
                sb.AppendLine();
                sb.AppendLine($"When ready, start with: start_test_session(appName='<label>', windowQuery='{windowQuery}').");
            }
        }
        else
        {
            sb.AppendLine($"⚠️ This does NOT look like a Power App — {reason}.");
            sb.AppendLine($"  URL:    {seenUrl ?? "(not readable)"}");
            sb.AppendLine($"  Window: '{win.Value.Title}' [{win.Value.ProcessName}.exe]");
            sb.AppendLine();
            sb.AppendLine("Do not proceed. Ask the user to confirm the Power App URL (a make.powerapps.com link, an " +
                          "apps.powerapps.com/play link, a model-driven *.dynamics.com app, or a Power Pages site) and try again.");
        }
        return sb.ToString();
    }

    /// <summary>The three test modes, offered as clickable buttons after a successful verification.</summary>
    private static readonly IReadOnlyList<Choice> TestModeChoices = new[]
    {
        new Choice("smoke", "Smoke test — explore the app in depth (read-only) and give me a repeatable test plan"),
        new Choice("own",   "Run my test plan — I'll tell you what to test"),
    };

    private static string ModeGuidance(string mode, string windowQuery) => mode switch
    {
        "smoke" =>
            $"Proceed: start_test_session(appName='<label>', windowQuery='{windowQuery}'), then explore the app IN DEPTH " +
            "and READ-ONLY with smoke_step across its primary flows (open menus/nav, inspect forms, sort/filter grids, " +
            "open records, navigate — but do NOT save, submit, delete, or send). When done, call get_exploration_log, " +
            "AUTHOR a natural-language test plan from it, save it with save_test_plan, then end_test_session.",
        "own" =>
            "Proceed: ask the user (in plain language) what they want to test, or a saved plan name (load_test_plan). " +
            "Then start_test_session, execute their plan with run_test_script, and end_test_session.",
        _ => $"Proceed with start_test_session(appName='<label>', windowQuery='{windowQuery}').",
    };

    private static bool IsBrowser(string proc) =>
        proc.Contains("edge", StringComparison.OrdinalIgnoreCase) ||
        proc.Contains("chrome", StringComparison.OrdinalIgnoreCase) ||
        proc.Contains("firefox", StringComparison.OrdinalIgnoreCase) ||
        proc.Contains("brave", StringComparison.OrdinalIgnoreCase);

    /// <summary>A still-loading / placeholder browser tab title we should wait past before reporting.</summary>
    private static bool IsGenericTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return true;
        var t = title.ToLowerInvariant();
        return t.StartsWith("untitled") || t.StartsWith("loading") || t.StartsWith("new tab") ||
               t.StartsWith("about:blank");
    }

    /// <summary>
    /// Locate a Chromium browser executable so we can pass '--new-window'. Preference: the caller's
    /// browserQuery hint (edge/chrome), else Edge, else Chrome. Returns null if none is found (caller
    /// then falls back to a shell-launched tab).
    /// </summary>
    private static string? FindBrowser(string? prefer)
    {
        string pf   = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pfx  = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string lad  = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var edge = new[]
        {
            System.IO.Path.Combine(pf,  "Microsoft", "Edge", "Application", "msedge.exe"),
            System.IO.Path.Combine(pfx, "Microsoft", "Edge", "Application", "msedge.exe"),
        };
        var chrome = new[]
        {
            System.IO.Path.Combine(pf,  "Google", "Chrome", "Application", "chrome.exe"),
            System.IO.Path.Combine(pfx, "Google", "Chrome", "Application", "chrome.exe"),
            System.IO.Path.Combine(lad, "Google", "Chrome", "Application", "chrome.exe"),
        };

        bool preferChrome = !string.IsNullOrWhiteSpace(prefer) &&
                            prefer.Contains("chrome", StringComparison.OrdinalIgnoreCase);

        var ordered = preferChrome ? chrome.Concat(edge) : edge.Concat(chrome);
        foreach (var c in ordered)
            if (System.IO.File.Exists(c)) return c;
        return null;
    }

    /// <summary>Handles of all browser windows that exist right now (used to detect the NEW one).</summary>
    private static HashSet<IntPtr> SnapshotBrowserWindows(string? browserQuery)
    {
        var set = new HashSet<IntPtr>();
        var hints = string.IsNullOrWhiteSpace(browserQuery)
            ? new[] { "msedge", "chrome", "firefox", "brave" }
            : new[] { browserQuery! };
        foreach (var h in hints)
        {
            try { foreach (var w in DesktopTools.ResolveMany(h, 40)) set.Add(w.Hwnd); }
            catch { /* no windows for this hint */ }
        }
        return set;
    }

    /// <summary>Find a browser window that did NOT exist in <paramref name="preExisting"/> — i.e. the new one.</summary>
    private static DesktopTools.AppWindow? FindNewBrowserWindow(string[] hints, HashSet<IntPtr> preExisting)
    {
        foreach (var h in hints)
        {
            List<DesktopTools.AppWindow> matches;
            try { matches = DesktopTools.ResolveMany(h, 40); } catch { continue; }
            foreach (var w in matches)
                if (!preExisting.Contains(w.Hwnd)) return w;
        }
        return null;
    }

    private static string ShortTitle(string title)
    {
        // Use a distinctive leading chunk of the title as a window query.
        var cut = title.Split(new[] { " - ", " — " }, StringSplitOptions.None)[0];
        return cut.Length > 40 ? cut[..40] : cut;
    }

    // =========================================================================
    //  Reusable clickable-choice prompt (MCP elicitation)
    // =========================================================================

    [McpServerTool(Name = "ask_user_choice")]
    [Description(
        "Presents the user with a small set of choices as CLICKABLE BUTTONS and returns the one they pick. " +
        "Use this at any decision point in the testing workflow — choosing a mode, approving a proposed plan, " +
        "deciding whether to re-run or fix a failing step, etc. — so the user can just click instead of typing.\n" +
        "The buttons are rendered by the MCP client via elicitation. If the connected client does NOT support " +
        "elicitation, this returns a note saying so; in that case fall back to asking the question in chat text.\n" +
        "Pass 2–6 short options. Returns the selected option (or 'declined'/'unsupported').")]
    public static async Task<string> AskUserChoice(
        [Description("The question to show above the buttons, e.g. 'Approve this test plan?'")]
        string question,
        [Description("The choices to render as buttons (2–6 short labels), e.g. ['Run it','Change steps','Cancel'].")]
        string[] options,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        if (options is null || options.Length == 0)
            return "No options provided. Pass 2–6 short option labels.";
        var choices = options.Select(o => new Choice(o, o)).ToList();

        var picked = await Elicitation.AskChoiceAsync(server, question, "choice", choices, cancellationToken);
        if (picked is not null)
            return $"User selected: {picked}";

        return "⚠️ The client did not render buttons (elicitation unsupported or dismissed). " +
               "Ask the user this question directly in chat instead: " + question +
               "  Options: " + string.Join(" | ", options);
    }

    // =========================================================================
    //  FFmpeg provisioning (needed for session video recording)
    // =========================================================================

    [McpServerTool(Name = "ensure_ffmpeg")]
    [Description(
        "Checks whether FFmpeg (required for session video recording) is available and installs it if it is " +
        "not — first via winget (Gyan.FFmpeg), then, if winget is unavailable, by downloading a static build " +
        "into a per-user cache (no admin rights needed). Call this before start_test_session if you want to be " +
        "sure the session is recorded; sessions still run without it, just with no video. Set checkOnly=true to " +
        "only report status without installing.")]
    public static string EnsureFfmpeg(
        [Description("If true, only check for FFmpeg and report; do not install. Default false (install if missing).")]
        bool checkOnly = false)
    {
        var log = new StringBuilder();
        var r = FfmpegSetup.Ensure(allowInstall: !checkOnly, log: m => log.AppendLine("  " + m));
        var sb = new StringBuilder();
        sb.AppendLine(r.Available ? $"✅ FFmpeg available at: {r.Path}" : $"⚠️ FFmpeg not available. {r.Detail}");
        if (log.Length > 0) { sb.AppendLine("Details:"); sb.Append(log); }
        if (!r.Available && checkOnly)
            sb.AppendLine("Call ensure_ffmpeg (checkOnly=false) to install it automatically.");
        return sb.ToString();
    }

    // =========================================================================
    //  Self-update (GitHub Releases)
    // =========================================================================

    [McpServerTool(Name = "check_for_update")]
    [Description(
        "Checks GitHub Releases for a newer version of the PowerAppsControl MCP server and reports whether an " +
        "update is available (current vs latest version, the asset that would be installed, and release notes). " +
        "Does not install anything. Use update_server to actually apply it.")]
    public static async Task<string> CheckForUpdate(CancellationToken cancellationToken = default)
    {
        var info = await Updater.CheckAsync(cancellationToken);
        var sb = new StringBuilder();
        if (info.UpdateAvailable)
        {
            sb.AppendLine($"⬆️ Update available: v{info.Latest} (installed: v{info.Current}, {info.Kind} install).");
            sb.AppendLine($"  Asset: {info.AssetName}");
            if (!string.IsNullOrWhiteSpace(info.Notes))
                sb.AppendLine("  Notes:\n" + Indent(info.Notes, 4, 12));
            sb.AppendLine("Call update_server to download and install it.");
        }
        else if (info.Latest > new Version(0, 0, 0))
        {
            sb.AppendLine($"✅ Up to date (installed v{info.Current}, latest release v{info.Latest}).");
        }
        else
        {
            sb.AppendLine($"⚠️ Could not determine the latest version. {info.Notes}");
        }
        return sb.ToString();
    }

    [McpServerTool(Name = "update_server")]
    [Description(
        "Downloads and installs the latest PowerAppsControl release from GitHub, then re-registers the server. " +
        "Because the running server executable is locked, the update is applied by a helper process that waits " +
        "for this process (and the host) to exit — so AFTER calling this, tell the user to restart their MCP " +
        "host (Scout / Copilot CLI) to complete the update and load the new version. Returns what it did. Use " +
        "check_for_update first to see if an update exists.")]
    public static async Task<string> UpdateServer(CancellationToken cancellationToken = default)
    {
        var info = await Updater.CheckAsync(cancellationToken);
        if (!info.UpdateAvailable || info.AssetUrl is null || info.AssetName is null)
            return $"No update to install (installed v{info.Current}, latest v{info.Latest}).";

        var tmpDir = Path.Combine(Path.GetTempPath(), $"pac-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        var assetPath = Path.Combine(tmpDir, info.AssetName);

        try
        {
            long lastPct = -1;
            var progress = new StringBuilder();
            await Updater.DownloadAsync(info.AssetUrl, assetPath, (done, total) =>
            {
                if (total > 0)
                {
                    long pct = done * 100 / total;
                    if (pct != lastPct && pct % 20 == 0) { lastPct = pct; progress.Append($"{pct}%… "); }
                }
            }, cancellationToken);

            Updater.SpawnApplyHelper(assetPath, info.Kind);

            return $"⬆️ Downloading v{info.Latest} complete ({progress}100%). Update helper launched.\n" +
                   "The new version will be applied once this server/host exits — **restart Scout / the " +
                   "Copilot CLI now** to finish updating and load PowerAppsControl v" + info.Latest + ".";
        }
        catch (Exception ex)
        {
            return $"Update failed: {ex.Message}. You can also download it manually from " +
                   "https://github.com/ilyafainberg/PowerAppsControl/releases.";
        }
    }

    private static string Indent(string text, int spaces, int maxLines)
    {
        var pad = new string(' ', spaces);
        var lines = text.Replace("\r\n", "\n").Split('\n').Take(maxLines);
        return string.Join("\n", lines.Select(l => pad + l));
    }

    // =========================================================================
    //  Session lifecycle
    // =========================================================================

    [McpServerTool(Name = "start_test_session")]
    [Description(
        "Starts a UX test session against a Power App and MUST be called before any other testing tool.\n" +
        "It (1) resolves the app window(s) by title/process, (2) pins them under agent control so the user " +
        "sees a crimson 'Under Agent Control' frame, (3) starts a video recording of the session, and " +
        "(4) shows a live status HUD at the top of the screen.\n" +
        "The Power App is usually a browser tab — pass the window title you see (e.g. the app name, or " +
        "'Edge' / 'Chrome'), or the player window title. For a light LOAD test, open several copies of the " +
        "app first and set maxWindows>1 to bring them all under test.\n" +
        "Always pair with end_test_session, which stops the recording and writes the report.")]
    public static string StartTestSession(
        [Description("Friendly label for the app under test (used in the HUD, report and file names). e.g. 'Contoso Referrals'.")]
        string appName,
        [Description("Window title or process name to find the app window. Optional — defaults to appName. e.g. 'Referrals - Contoso', 'msedge', 'Power Apps'.")]
        string? windowQuery = null,
        [Description("Maximum number of matching windows to bring under test for parallel/load runs. Default 1. Range 1..8.")]
        int maxWindows = 1,
        [Description("Video frames per second for the session recording. Default 12. Range 1..30. Lower = smaller file.")]
        int fps = 12,
        [Description("Whether to record the session to video. Default true. If FFmpeg is missing the session still runs, without video.")]
        bool recordVideo = true)
    {
        if (string.IsNullOrWhiteSpace(appName)) throw new ArgumentException("appName is required.");
        maxWindows = Math.Clamp(maxWindows, 1, 8);
        fps = Math.Clamp(fps, 1, 30);
        var query = string.IsNullOrWhiteSpace(windowQuery) ? appName : windowQuery!;

        var abort = AgentSession.ConsumeAbort();
        if (abort is not null) return abort;

        var session = TestSessionManager.Start(appName, query, maxWindows, fps, recordVideo, out var warning);

        var sb = new StringBuilder();
        sb.AppendLine($"🎬 Test session STARTED for '{appName}' (id {session.Id}).");
        sb.AppendLine($"  Windows under control ({session.Windows.Count}):");
        foreach (var w in session.Windows)
            sb.AppendLine($"    • '{w.Title}' [{w.ProcessName}.exe, PID {w.Pid}]");
        sb.AppendLine($"  Recording: {(session.Recorder is not null ? "ON → " + session.VideoFile : "OFF")}");
        if (warning is not null) sb.AppendLine($"  ⚠️ {warning}");
        sb.AppendLine($"  Session folder: {session.SessionDir}");
        sb.AppendLine();
        sb.AppendLine("Next: either run_test_script (if the user gave you a script) OR smoke_step repeatedly to " +
                      "explore the app and build a suggested script. Call end_test_session when done.");
        return sb.ToString();
    }

    [McpServerTool(Name = "test_session_status")]
    [Description("Reports the current test session: app, windows, mode (scripted/smoke), runs recorded so far, " +
                 "draft-script step count and the session folder. Returns a note if no session is active.")]
    public static string TestSessionStatus()
    {
        if (!TestSessionManager.IsActive) return "No test session is active. Call start_test_session to begin.";
        var s = TestSessionManager.Current;
        var sb = new StringBuilder();
        sb.AppendLine($"Active session '{s.AppName}' (id {s.Id}) — mode: {s.Mode}.");
        sb.AppendLine($"  Started: {s.StartedAt:HH:mm:ss}  ·  windows: {s.Windows.Count}  ·  recording: {(s.Recorder is not null ? "on" : "off")}");
        sb.AppendLine($"  Runs recorded: {s.Results.Count}  ·  exploration steps: {s.DraftSteps.Count}");
        sb.AppendLine($"  Folder: {s.SessionDir}");
        return sb.ToString();
    }

    [McpServerTool(Name = "end_test_session")]
    [Description("Ends the active test session: stops the video recording, releases the controlled windows, " +
                 "hides the HUD, and writes the report (report.html + report.json) plus any screenshots into " +
                 "the session folder. Returns the report path and a pass/fail summary. ALWAYS call this when " +
                 "you finish testing, even if runs failed.")]
    public static string EndTestSession()
    {
        if (!TestSessionManager.IsActive) return "No test session is active (nothing to end).";
        return TestSessionManager.End();
    }

    // =========================================================================
    //  Scripted runs
    // =========================================================================

    [McpServerTool(Name = "run_test_script")]
    [Description(
        "Executes a test plan against the app for the active session. This is the RUN-TIME EXECUTION form: you " +
        "compile a natural-language test plan (the user's, or one from a saved plan) into the JSON steps below and " +
        "pass them as scriptJson. The user never has to write JSON — that's your job; they describe what to test in " +
        "plain language and you translate it here.\n" +
        "scriptJson shape: { \"name\": \"...\", \"steps\": [ { \"action\": \"...\", \"description\": \"...\", ... } ] }.\n" +
        "Step actions (prefer name-based ones — they survive layout changes; use pixel 'click' only for canvas " +
        "controls UI Automation can't see):\n" +
        "  • clickElement   {name?, automationId?, controlType?}     — find + invoke a control via UI Automation.\n" +
        "  • type           {keys}                                   — type text/chords, e.g. \"jane{Tab}{Enter}\".\n" +
        "  • waitForElement {name?/automationId?/controlType?, timeoutMs?} — poll until a control appears.\n" +
        "  • assertElement  {name?/automationId?/controlType?, shouldExist?}  — pass/fail presence check.\n" +
        "  • scroll         {amount, horizontal?, x?, y?}            — wheel scroll (negative amount = down).\n" +
        "  • wait           {ms}                                     — fixed pause.\n" +
        "  • click          {x, y, button?, clicks?}                 — click at window-relative pixels (last resort).\n" +
        "  • screenshot     {description?}                           — capture the app into the report.\n" +
        "REPEAT: runs=N replays the whole plan N times. LOAD: parallelWindows=M drives M of the session's windows " +
        "together (interleaved per step, since Windows serializes real input) to simulate concurrent users — you " +
        "must have opened M app windows and passed maxWindows>=M to start_test_session.\n" +
        "Each step is timed and marked pass/fail; failures capture a screenshot. The full report is written on " +
        "end_test_session. Returns a run summary (pass counts, timings, first failure).")]
    public static string RunTestScript(
        [Description("The test plan compiled to JSON steps (see shape above). You author this from the user's plain-language plan.")]
        string scriptJson,
        [Description("How many times to replay the whole plan. Default 1. Range 1..100.")]
        int runs = 1,
        [Description("How many of the session's windows to drive at once for load testing. Default 1. Range 1..8 (capped to the number of windows under control).")]
        int parallelWindows = 1)
    {
        var gate = TestSessionManager.RequireSession("run_test_script");
        if (gate is not null) return gate;

        if (string.IsNullOrWhiteSpace(scriptJson))
            return "Provide scriptJson — the user's plain-language plan compiled into JSON steps.";

        TestScript script = TestJson.ParseScript(scriptJson);

        try
        {
            return TestRunner.Execute(TestSessionManager.Current, script, runs, parallelWindows);
        }
        catch (TestRunner.AbortedException ex)
        {
            return ex.Message;
        }
    }

    // =========================================================================
    //  Smoke test (in-depth read-only exploration → natural-language plan)
    // =========================================================================

    [McpServerTool(Name = "smoke_step")]
    [Description(
        "Performs ONE exploratory action on the app AND records it in the session's exploration log. Use this " +
        "during a SMOKE TEST — an IN-DEPTH, READ-ONLY exploration of the app: open menus and navigation, inspect " +
        "forms and fields, sort/filter grids, open records, move between screens. Do NOT save, submit, delete, or " +
        "send anything — smoke testing is non-destructive. When done, get_exploration_log gives you the log to " +
        "author a natural-language test plan from.\n" +
        "Switches the session into 'smoke' mode. The 'action' + its fields mirror run_test_script step actions:\n" +
        "  action='clickElement'   with name / automationId / controlType (preferred — no pixel guessing).\n" +
        "  action='click'          with x,y (window-relative pixels; last resort for canvas controls).\n" +
        "  action='type'           with keys.\n" +
        "  action='scroll'         with amount (negative = down), optional x,y.\n" +
        "  action='wait'           with ms.\n" +
        "  action='waitForElement' with name/automationId and optional timeoutMs.\n" +
        "  action='assertElement'  with name/automationId and optional shouldExist.\n" +
        "  action='screenshot'     to snapshot the app into the report.\n" +
        "Recommended smoke loop: screenshot_window to SEE the app → smoke_step to act → screenshot_window to " +
        "VERIFY → repeat across the primary flows. Returns the step result (pass/fail + detail). Add a clear " +
        "description to each step so the exploration log — and the plan you author from it — reads well.")]
    public static string SmokeStep(
        [Description("The action to perform: click | clickElement | type | scroll | wait | waitForElement | assertElement | screenshot.")]
        string action,
        [Description("Human description of the step (e.g. 'open the New Referral form'). Strongly recommended — it becomes the step's label in the report and script.")]
        string? description = null,
        [Description("click/scroll: X coordinate INSIDE the window (physical pixels from the window's top-left, as in a screenshot_window PNG).")]
        int? x = null,
        [Description("click/scroll: Y coordinate INSIDE the window.")]
        int? y = null,
        [Description("click: mouse button 'left' (default), 'right' or 'middle'.")]
        string? button = null,
        [Description("click: number of clicks (1 = single, 2 = double). Default 1.")]
        int? clicks = null,
        [Description("type: the key sequence, e.g. 'Jane Doe{Tab}' or '{Ctrl+A}{Delete}'.")]
        string? keys = null,
        [Description("scroll: number of wheel notches. Negative = down/left. Default -3.")]
        int? amount = null,
        [Description("scroll: true for horizontal. Default false.")]
        bool? horizontal = null,
        [Description("clickElement/waitForElement/assertElement: user-visible control name (substring ok).")]
        string? name = null,
        [Description("clickElement/waitForElement/assertElement: developer AutomationId (exact).")]
        string? automationId = null,
        [Description("clickElement/waitForElement/assertElement: control type, e.g. 'button', 'edit', 'listitem'.")]
        string? controlType = null,
        [Description("wait: milliseconds to pause.")]
        int? ms = null,
        [Description("waitForElement: timeout in ms. Default 8000.")]
        int? timeoutMs = null,
        [Description("assertElement: whether the element should exist. Default true.")]
        bool? shouldExist = null)
    {
        var gate = TestSessionManager.RequireSession("smoke_step");
        if (gate is not null) return gate;

        if (!TryParseAction(action, out var parsed))
            return $"Unknown action '{action}'. Use one of: click, clickElement, type, scroll, wait, waitForElement, assertElement, screenshot.";

        var step = new TestStep
        {
            Action = parsed,
            Description = description,
            X = x, Y = y, Button = button, Clicks = clicks,
            Keys = keys, Amount = amount, Horizontal = horizontal,
            Name = name, AutomationId = automationId, ControlType = controlType,
            Ms = ms, TimeoutMs = timeoutMs, ShouldExist = shouldExist,
        };

        var session = TestSessionManager.Current;
        session.Mode = "smoke";

        StepResult sr;
        try
        {
            sr = TestRunner.ExecuteSmoke(session, step);
        }
        catch (TestRunner.AbortedException ex)
        {
            return ex.Message;
        }

        // Record the step in the draft only if it did something meaningful.
        session.DraftSteps.Add(step);

        var status = sr.Passed ? "✓" : "✗";
        return $"{status} smoke step {session.DraftSteps.Count} [{sr.Action}] {sr.Label} — {sr.Message} ({sr.DurationMs}ms). " +
               (sr.Passed ? "Recorded to the exploration log." : "Recorded (FAILED) to the exploration log — note it when authoring the plan.");
    }

    [McpServerTool(Name = "get_exploration_log")]
    [Description("Returns a plain-English OBSERVATION LOG of the smoke_step actions taken this session (what was " +
                 "done, which control/element was targeted, and the result of each step) — raw material, NOT the " +
                 "finished plan.\n" +
                 "Use it (plus your understanding of the app) to AUTHOR a detailed, natural-language TEST PLAN: " +
                 "numbered steps in plain language that reference controls by their visible name/label and state the " +
                 "expected outcome of each step. AVOID pixel coordinates — a coordinate plan breaks the moment the " +
                 "layout changes; a natural-language plan stays valid and both a human and an agent can read and " +
                 "re-run it. Then save it with save_test_plan so it appears in the report and can be reused.")]
    public static string GetExplorationLog(
        [Description("Optional name for the test. Default '<app> smoke test'.")]
        string? name = null)
    {
        var gate = TestSessionManager.RequireSession("get_exploration_log");
        if (gate is not null) return gate;

        var s = TestSessionManager.Current;
        if (s.DraftSteps.Count == 0)
            return "No smoke steps have been recorded yet. Use smoke_step to explore the app first.";

        var title = string.IsNullOrWhiteSpace(name) ? $"{s.AppName} smoke test" : name!;
        var sb = new StringBuilder();
        sb.AppendLine($"Exploration log for '{title}' — {s.DraftSteps.Count} action(s) against '{s.AppName}':");
        sb.AppendLine();
        for (int i = 0; i < s.DraftSteps.Count; i++)
            sb.AppendLine($"  {i + 1}. {DescribeObservation(s.DraftSteps[i])}");
        sb.AppendLine();
        sb.AppendLine("NEXT — author a natural-language TEST PLAN from this:");
        sb.AppendLine("  • A numbered list, plain language, no JSON and no pixel coordinates.");
        sb.AppendLine("  • Reference each control by its visible NAME/LABEL and state the EXPECTED OUTCOME");
        sb.AppendLine("    (e.g. \"Click the 'Submit' button — a 'Thank you' confirmation should appear\").");
        sb.AppendLine("  • Begin with a step that waits for the app to finish loading.");
        sb.AppendLine("  • Then call save_test_plan(name, plan) with your plan text — it goes into the report and");
        sb.AppendLine("    the library so it can be re-run later (run_test_script compiles it to steps at run time).");
        return sb.ToString();
    }

    /// <summary>Public wrapper so the session manager can build a readable fallback plan.</summary>
    internal static string DescribeObservationPublic(TestStep step) => DescribeObservation(step);

    /// <summary>Render one recorded smoke step as a plain-English observation (name/label based, not coordinates).</summary>
    private static string DescribeObservation(TestStep step) => step.Action switch
    {
        StepAction.Click          => $"Clicked in the app{(step.Description is not null ? $" to {step.Description}" : " at a location")}.",
        StepAction.ClickElement   => $"Activated the control '{step.Name ?? step.AutomationId ?? step.ControlType}'{Desc(step)}.",
        StepAction.Type           => $"Typed \"{step.Keys}\"{Desc(step)}.",
        StepAction.Scroll         => $"Scrolled the view{Desc(step)}.",
        StepAction.Wait           => $"Waited for the UI to settle{Desc(step)}.",
        StepAction.WaitForElement => $"Waited for '{step.Name ?? step.AutomationId}' to appear{Desc(step)}.",
        StepAction.AssertElement  => $"Checked that '{step.Name ?? step.AutomationId}' was {(step.ShouldExist == false ? "absent" : "present")}{Desc(step)}.",
        StepAction.Screenshot     => $"Captured the screen{Desc(step)}.",
        _ => step.Label(),
    };

    private static string Desc(TestStep step) =>
        string.IsNullOrWhiteSpace(step.Description) ? "" : $" — {step.Description}";

    // =========================================================================
    //  Test-plan library (natural language, human + agent readable)
    // =========================================================================

    [McpServerTool(Name = "save_test_plan")]
    [Description("Saves a natural-language test plan to the library (%USERPROFILE%\\PowerAppsControl\\Plans) as a " +
                 "Markdown file so it can be reused later, and — if a session is active — attaches it to the current " +
                 "session so it appears in the HTML report. The plan is PLAIN LANGUAGE (a numbered list of steps that " +
                 "name controls by their visible label and state the expected outcome), NOT JSON. This is the " +
                 "smoke-test deliverable. Returns the saved file path.")]
    public static string SaveTestPlan(
        [Description("Name to save the plan under (file name). e.g. 'referral-happy-path'.")]
        string name,
        [Description("The natural-language test plan text (Markdown). A numbered list of plain-language steps with expected outcomes.")]
        string plan)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is required.");
        if (string.IsNullOrWhiteSpace(plan)) return "Provide the natural-language plan text.";

        var path = PlanPath(name);
        File.WriteAllText(path, plan, new UTF8Encoding(false));

        // Attach to the active session so end_test_session shows it in the report.
        if (TestSessionManager.IsActive)
            TestSessionManager.Current.SuggestedPlan = plan;

        return $"Saved test plan '{name}' → {path}" +
               (TestSessionManager.IsActive ? " (also attached to the current session's report)." : ".");
    }

    [McpServerTool(Name = "load_test_plan")]
    [Description("Loads a saved natural-language test plan by name and returns its text. Use list_test_plans to see " +
                 "what's available. To run it, read the steps and execute them with run_test_script.")]
    public static string LoadTestPlan(
        [Description("The saved plan name.")] string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is required.");
        var path = PlanPath(name);
        if (!File.Exists(path)) return $"No saved plan named '{name}'. Use list_test_plans to see available plans.";
        return File.ReadAllText(path);
    }

    [McpServerTool(Name = "list_test_plans")]
    [Description("Lists the saved natural-language test plans in the library (%USERPROFILE%\\PowerAppsControl\\Plans).")]
    public static string ListTestPlans()
    {
        var dir = TestSessionManager.PlansDir;
        var files = Directory.GetFiles(dir, "*.md");
        if (files.Length == 0) return $"No saved test plans in {dir}.";
        var sb = new StringBuilder();
        sb.AppendLine($"Saved test plans in {dir}:");
        foreach (var f in files.OrderBy(f => f))
            sb.AppendLine($"  • {Path.GetFileNameWithoutExtension(f)}");
        return sb.ToString();
    }

    // =========================================================================
    //  Helpers
    // =========================================================================

    private static string PlanPath(string name)
    {
        var safe = string.Concat(name.Split(Path.GetInvalidFileNameChars())).Trim();
        if (safe.Length == 0) safe = "plan";
        if (!safe.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) safe += ".md";
        return Path.Combine(TestSessionManager.PlansDir, safe);
    }

    private static bool TryParseAction(string action, out StepAction parsed)
    {
        var norm = action.Replace("_", "").Replace("-", "").Replace(" ", "").Trim();
        return Enum.TryParse(norm, ignoreCase: true, out parsed);
    }
}
