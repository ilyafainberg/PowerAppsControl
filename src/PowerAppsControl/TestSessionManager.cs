// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Ilya Fainberg
// -----------------------------------------------------------------------------
//  PowerAppsControl — Test session lifecycle + state
//
//  Licensed under the GNU General Public License v3.0 or later (GPL-3.0-or-later).
//  See the LICENSE file in the project root for the full text.
//
//  Author: Ilya Fainberg <ifain@microsoft.com>
//
//  Owns the single active test session: the app window(s) under control, the
//  background video recording, the live HUD, the accumulated run results and the
//  draft script built up during a smoke test. The testing tools are GATED on an
//  active session so the user always gets the visual cues (crimson frames + HUD)
//  and a recording for anything the agent does.
// -----------------------------------------------------------------------------

using System.IO;

namespace PowerAppsControl;

/// <summary>All mutable state for one running test session.</summary>
internal sealed class TestSession
{
    public required string Id { get; init; }
    public required string AppName { get; init; }
    public required string WindowQuery { get; init; }
    public required List<DesktopTools.AppWindow> Windows { get; init; }
    public required string SessionDir { get; init; }
    public required DateTime StartedAt { get; init; }
    public string Mode { get; set; } = "scripted";

    public SessionRecorder? Recorder { get; set; }

    /// <summary>True while a video recording is active (drives the REC dot on the frame pill).</summary>
    public bool IsRecording => Recorder is not null;

    /// <summary>Update the integrated status pill on the primary window's frame.</summary>
    public void SetStatus(string? status) => WindowControl.SetStatus(PrimaryHwnd, status, IsRecording);

    /// <summary>Steps captured live during a smoke test → the raw exploration log.</summary>
    public List<TestStep> DraftSteps { get; } = new();

    /// <summary>The authored natural-language test plan (Markdown) — the smoke-test deliverable, shown in the report.</summary>
    public string? SuggestedPlan { get; set; }

    /// <summary>All run results accumulated across run_test_script calls this session.</summary>
    public List<RunResult> Results { get; } = new();

    public int RunCounter { get; set; }
    public int ScreenshotCounter { get; set; }

    public string ScreenshotsDir => Path.Combine(SessionDir, "screenshots");
    public string VideoFile => Path.Combine(SessionDir, "session.mp4");
    public string ReportHtml => Path.Combine(SessionDir, "report.html");
    public string ReportJson => Path.Combine(SessionDir, "report.json");

    /// <summary>The primary (first) window's handle — the recording + HUD anchor.</summary>
    public IntPtr PrimaryHwnd => Windows[0].Hwnd;
}

internal static class TestSessionManager
{
    private static readonly object Gate = new();
    private static TestSession? active;

    public static bool IsActive { get { lock (Gate) return active is not null; } }

    public static TestSession Current => active ?? throw new InvalidOperationException(
        "No test session is active. Call start_test_session first.");

    /// <summary>Base folder: %USERPROFILE%\PowerAppsControl.</summary>
    public static string BaseDir
    {
        get
        {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "PowerAppsControl");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    /// <summary>Library folder for saved natural-language test plans (.md): %USERPROFILE%\PowerAppsControl\Plans.</summary>
    public static string PlansDir
    {
        get { var d = Path.Combine(BaseDir, "Plans"); Directory.CreateDirectory(d); return d; }
    }

    /// <summary>Return a gate instruction string if no session is active, else null.</summary>
    public static string? RequireSession(string tool)
    {
        var abort = AgentSession.ConsumeAbort();
        if (abort is not null) return abort;
        if (IsActive) return null;
        return
            $"⛔ NO TEST SESSION — '{tool}' needs an active session so the work is framed, recorded and reported.\n" +
            "Start one first:\n" +
            "  start_test_session(appName='<label>', windowQuery='<title or process of the Power App window>')\n" +
            "That pins the app window(s) under agent control (crimson frame), starts the video recording and\n" +
            "shows the live HUD. When you finish, call end_test_session to write the report.";
    }

    /// <summary>
    /// Begin a session: resolve + control up to <paramref name="maxWindows"/> windows, start the HUD and
    /// (best-effort) the video recording. Returns a human summary; sets <paramref name="warning"/> when
    /// video could not start.
    /// </summary>
    public static TestSession Start(string appName, string windowQuery, int maxWindows, int fps, bool recordVideo, out string? warning)
    {
        lock (Gate)
        {
            if (active is not null)
                throw new InvalidOperationException(
                    $"A test session for '{active.AppName}' is already active (id {active.Id}). Call end_test_session first.");

            var windows = DesktopTools.ResolveMany(windowQuery, Math.Max(1, maxWindows));

            var id = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var safeApp = string.Concat(appName.Split(Path.GetInvalidFileNameChars())).Trim();
            if (safeApp.Length == 0) safeApp = "app";
            var sessionDir = Path.Combine(BaseDir, "Sessions", $"{safeApp}_{id}");
            Directory.CreateDirectory(sessionDir);

            var session = new TestSession
            {
                Id = id,
                AppName = appName,
                WindowQuery = windowQuery,
                Windows = windows,
                SessionDir = sessionDir,
                StartedAt = DateTime.Now,
            };
            Directory.CreateDirectory(session.ScreenshotsDir);

            // Pin every window under agent control (crimson frame = clear visual cue).
            foreach (var w in windows)
                WindowControl.Acquire(w.Hwnd, w.Title);

            warning = null;
            if (recordVideo)
            {
                var rec = SessionRecorder.TryStart(session.PrimaryHwnd, windows[0].Title, session.VideoFile, fps, out var recErr);
                session.Recorder = rec;
                warning = recErr;
            }

            // Show the integrated status pill (REC dot lights up if we're recording).
            foreach (var w in windows)
                WindowControl.SetStatus(w.Hwnd, "Starting session…", session.IsRecording);

            active = session;
            return session;
        }
    }

    /// <summary>
    /// End the active session: stop the recording, finalize the HUD, release the windows and write the
    /// JSON + HTML report. Returns a summary string with the report path.
    /// </summary>
    public static string End()
    {
        lock (Gate)
        {
            if (active is null) return "No test session was active.";
            var s = active;

            string recSummary = "(no video)";
            if (s.Recorder is not null)
            {
                recSummary = s.Recorder.Stop();
            }

            // Build the report model.
            int totalRuns = s.Results.Count;
            int passedRuns = s.Results.Count(r => r.Passed);
            var report = new TestSessionReport
            {
                SessionId = s.Id,
                AppName = s.AppName,
                Mode = s.Mode,
                StartedAt = s.StartedAt,
                EndedAt = DateTime.Now,
                VideoFile = File.Exists(s.VideoFile) ? s.VideoFile : null,
                Runs = s.Results.Select(r => r.RunIndex).DefaultIfEmpty(0).Max(),
                ParallelWindows = s.Windows.Count,
                WindowTitles = s.Windows.Select(w => w.Title).ToList(),
                // The human-readable deliverable: the agent-authored NL plan if provided, else a
                // readable summary of what the smoke exploration did (never raw JSON).
                SuggestedPlan = s.SuggestedPlan
                    ?? (s.Mode == "smoke" && s.DraftSteps.Count > 0 ? BuildFallbackPlan(s) : null),
                Results = s.Results.ToList(),
            };

            try { File.WriteAllText(s.ReportJson, TestJson.Write(report)); } catch { /* best effort */ }
            try { ReportGenerator.Write(report, s.ReportHtml); } catch { /* best effort */ }

            // Recording has stopped; drop the REC dot and show the final tally briefly on the frame.
            foreach (var w in s.Windows)
                WindowControl.SetStatus(w.Hwnd, totalRuns == 0 ? "Session complete" : $"Done · {passedRuns}/{totalRuns} runs passed", recording: false);
            Thread.Sleep(1200); // let the user see the final state

            WindowControl.ReleaseAll();

            active = null;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"✅ Test session '{s.AppName}' ended (id {s.Id}).");
            sb.AppendLine($"  Runs: {passedRuns}/{totalRuns} passed across {s.Windows.Count} window(s).");
            sb.AppendLine($"  Video:  {(File.Exists(s.VideoFile) ? s.VideoFile : "none — " + recSummary)}");
            sb.AppendLine($"  Report: {s.ReportHtml}");
            sb.AppendLine($"  Data:   {s.ReportJson}");
            if (s.Mode == "smoke")
                sb.AppendLine(s.SuggestedPlan is not null
                    ? "  A natural-language test plan is included in the report; save it with save_test_plan to reuse it."
                    : $"  Explored {s.DraftSteps.Count} action(s) — author a natural-language plan from get_exploration_log, then save_test_plan.");
            return sb.ToString();
        }
    }

    /// <summary>
    /// When the agent didn't hand us an authored plan, produce a readable numbered list from the
    /// recorded exploration so the report is still human-readable (never raw JSON).
    /// </summary>
    private static string BuildFallbackPlan(TestSession s)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# {s.AppName} — smoke test");
        sb.AppendLine();
        sb.AppendLine("_Auto-summarized from the exploration (no authored plan was supplied)._");
        sb.AppendLine();
        int n = 1;
        foreach (var step in s.DraftSteps)
            sb.AppendLine($"{n++}. {TestingTools.DescribeObservationPublic(step)}");
        return sb.ToString();
    }
}
