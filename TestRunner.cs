// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Ilya Fainberg
// -----------------------------------------------------------------------------
//  PowerAppsControl — Test runner (execute a script × runs × windows)
//
//  Licensed under the GNU General Public License v3.0 or later (GPL-3.0-or-later).
//  See the LICENSE file in the project root for the full text.
//
//  Author: Ilya Fainberg <ifain@microsoft.com>
//
//  Replays a TestScript against one or more app windows. A single logical "pass"
//  runs the whole script once; runs=N repeats that N times. parallelWindows=M
//  drives M windows to simulate concurrent users. Because Windows serializes real
//  synthesized input (one cursor, one foreground window), a load pass INTERLEAVES:
//  for each step, the runner executes that step on every window in turn, so all M
//  windows advance through the script together. Every step is timed and marked
//  pass/fail; failures (and explicit screenshot steps) capture a PNG into the
//  session for the report. The whole thing honors the user ✕ abort.
// -----------------------------------------------------------------------------

using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using Bitmap = System.Drawing.Bitmap;

namespace PowerAppsControl;

internal static class TestRunner
{
    private const int SettleMs = 150;       // pause after an action so the UI can react
    private const int DefaultWaitMs = 500;  // default for a bare wait step
    private const int DefaultTimeoutMs = 8000;
    private const int PollMs = 250;

    /// <summary>Thrown internally when the user clicks ✕ to abort. Surfaced to the agent by the tool.</summary>
    internal sealed class AbortedException : Exception
    {
        public AbortedException(string msg) : base(msg) { }
    }

    /// <summary>
    /// Run <paramref name="script"/> <paramref name="runs"/> times across up to
    /// <paramref name="parallelWindows"/> of the session's windows. Appends results to the session and
    /// returns a human summary. Throws <see cref="AbortedException"/> if the user aborts.
    /// </summary>
    public static string Execute(TestSession session, TestScript script, int runs, int parallelWindows)
    {
        runs = Math.Clamp(runs, 1, 100);
        parallelWindows = Math.Clamp(parallelWindows, 1, session.Windows.Count);
        var targets = session.Windows.Take(parallelWindows).ToList();
        var steps = script.Steps;

        var passResults = new List<RunResult>();

        for (int run = 1; run <= runs; run++)
        {
            int passId = ++session.RunCounter;
            var perWindow = targets.Select((w, wi) => new RunResult
            {
                RunIndex = passId,
                WindowIndex = wi,
                WindowTitle = w.Title,
                StartedAt = DateTime.Now,
                Passed = true,
            }).ToList();

            var passSw = Stopwatch.StartNew();

            for (int i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                for (int wi = 0; wi < targets.Count; wi++)
                {
                    ThrowIfAborted();

                    var hud = $"Run {run}/{runs} · step {i + 1}/{steps.Count}: {step.Label()}";
                    if (targets.Count > 1) hud += $" · win {wi + 1}/{targets.Count}";
                    session.Hud.SetStatus(hud);

                    var sr = ExecuteStep(session, targets[wi].Hwnd, step, i, passId, wi);
                    perWindow[wi].Steps.Add(sr);
                    if (!sr.Passed) perWindow[wi].Passed = false;
                }
            }

            passSw.Stop();
            foreach (var rr in perWindow)
            {
                rr.DurationMs = rr.Steps.Sum(s => s.DurationMs);
                session.Results.Add(rr);
                passResults.Add(rr);
            }
        }

        return Summarize(script, passResults, runs, targets.Count, parallelWindows, session);
    }

    /// <summary>Execute a single exploratory step on the primary window (used by smoke_step).</summary>
    internal static StepResult ExecuteSmoke(TestSession session, TestStep step)
    {
        session.Hud.SetStatus($"Smoke · step {session.DraftSteps.Count + 1}: {step.Label()}");
        return ExecuteStep(session, session.PrimaryHwnd, step, session.DraftSteps.Count, 0, 0);
    }

    private static StepResult ExecuteStep(TestSession session, IntPtr hwnd, TestStep step, int index, int passId, int windowIndex)
    {
        var sr = new StepResult { Index = index, Action = step.Action.ToString(), Label = step.Label() };
        var sw = Stopwatch.StartNew();
        try
        {
            switch (step.Action)
            {
                case StepAction.Click:
                    if (step.X is null || step.Y is null)
                        throw new ArgumentException("click step requires x and y (window-relative pixels).");
                    DesktopTools.ClickWindow(hwnd, step.X.Value, step.Y.Value, step.Button ?? "left", step.Clicks ?? 1);
                    sr.Passed = true;
                    sr.Message = $"clicked ({step.X},{step.Y})";
                    Thread.Sleep(SettleMs);
                    break;

                case StepAction.ClickElement:
                {
                    RequireElementFilter(step);
                    var (ok, info) = DesktopTools.InvokeElementByQuery(hwnd, step.Name, step.AutomationId, step.ControlType);
                    sr.Passed = ok;
                    sr.Message = ok ? info : $"element not found ({Describe(step)})";
                    Thread.Sleep(SettleMs);
                    break;
                }

                case StepAction.Type:
                    if (string.IsNullOrEmpty(step.Keys))
                        throw new ArgumentException("type step requires 'keys'.");
                    var n = DesktopTools.TypeInto(hwnd, step.Keys);
                    sr.Passed = true;
                    sr.Message = $"{n} key event(s)";
                    Thread.Sleep(SettleMs);
                    break;

                case StepAction.Scroll:
                    DesktopTools.ScrollWindow(hwnd, step.Amount ?? -3, step.Horizontal ?? false, step.X, step.Y);
                    sr.Passed = true;
                    sr.Message = $"scrolled {step.Amount ?? -3}";
                    Thread.Sleep(SettleMs);
                    break;

                case StepAction.Wait:
                    var ms = Math.Clamp(step.Ms ?? DefaultWaitMs, 0, 60000);
                    InterruptibleSleep(ms);
                    sr.Passed = true;
                    sr.Message = $"waited {ms}ms";
                    break;

                case StepAction.WaitForElement:
                {
                    RequireElementFilter(step);
                    int timeout = Math.Clamp(step.TimeoutMs ?? DefaultTimeoutMs, 100, 120000);
                    bool found = PollForElement(hwnd, step, timeout);
                    sr.Passed = found;
                    sr.Message = found ? "element appeared" : $"timed out after {timeout}ms waiting for {Describe(step)}";
                    break;
                }

                case StepAction.AssertElement:
                {
                    RequireElementFilter(step);
                    bool exists = DesktopTools.FindElements(hwnd, step.Name, step.AutomationId, step.ControlType, 1).Count > 0;
                    bool shouldExist = step.ShouldExist ?? true;
                    sr.Passed = exists == shouldExist;
                    sr.Message = sr.Passed
                        ? $"assertion held ({Describe(step)} {(shouldExist ? "present" : "absent")})"
                        : $"assertion FAILED — expected {Describe(step)} to be {(shouldExist ? "present" : "absent")} but it was {(exists ? "present" : "absent")}";
                    break;
                }

                case StepAction.Screenshot:
                    sr.Passed = true;
                    sr.Message = step.Description ?? "screenshot";
                    break;

                default:
                    sr.Passed = false;
                    sr.Message = $"unsupported action '{step.Action}'";
                    break;
            }
        }
        catch (AbortedException) { throw; }
        catch (Exception ex)
        {
            sr.Passed = false;
            sr.Message = ex.Message;
        }
        sw.Stop();
        sr.DurationMs = sw.ElapsedMilliseconds;

        // Capture a screenshot for explicit screenshot steps and for any failure (for the report).
        if (step.Action == StepAction.Screenshot || !sr.Passed)
        {
            sr.ScreenshotFile = TrySaveShot(session, hwnd, $"p{passId}_w{windowIndex}_s{index}_{(sr.Passed ? "shot" : "fail")}");
        }
        return sr;
    }

    private static bool PollForElement(IntPtr hwnd, TestStep step, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            ThrowIfAborted();
            if (DesktopTools.FindElements(hwnd, step.Name, step.AutomationId, step.ControlType, 1).Count > 0)
                return true;
            Thread.Sleep(PollMs);
        }
        return false;
    }

    private static void InterruptibleSleep(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            ThrowIfAborted();
            Thread.Sleep(Math.Min(100, ms - (int)sw.ElapsedMilliseconds));
        }
    }

    private static string? TrySaveShot(TestSession session, IntPtr hwnd, string tag)
    {
        try
        {
            using var bmp = DesktopTools.CaptureWindow(hwnd);
            var file = $"{tag}.png";
            bmp.Save(Path.Combine(session.ScreenshotsDir, file), ImageFormat.Png);
            return file;
        }
        catch { return null; }
    }

    private static void RequireElementFilter(TestStep step)
    {
        if (string.IsNullOrEmpty(step.Name) && string.IsNullOrEmpty(step.AutomationId) && string.IsNullOrEmpty(step.ControlType))
            throw new ArgumentException($"{step.Action} step requires at least one of: name, automationId, controlType.");
    }

    private static string Describe(TestStep step)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(step.Name)) parts.Add($"name='{step.Name}'");
        if (!string.IsNullOrEmpty(step.AutomationId)) parts.Add($"id='{step.AutomationId}'");
        if (!string.IsNullOrEmpty(step.ControlType)) parts.Add($"type='{step.ControlType}'");
        return parts.Count == 0 ? "element" : string.Join(" ", parts);
    }

    private static void ThrowIfAborted()
    {
        var abort = AgentSession.ConsumeAbort();
        if (abort is not null) throw new AbortedException(abort);
    }

    private static string Summarize(TestScript script, List<RunResult> passResults, int runs, int windowsUsed, int requested, TestSession session)
    {
        var sb = new System.Text.StringBuilder();
        int total = passResults.Count;
        int passed = passResults.Count(r => r.Passed);
        double avg = passResults.Count > 0 ? passResults.Average(r => r.DurationMs) : 0;

        sb.AppendLine($"▶ Ran '{script.Name}' — {runs} run(s) × {windowsUsed} window(s) = {total} pass(es).");
        if (requested > windowsUsed)
            sb.AppendLine($"  ⚠️ Requested {requested} parallel windows but only {windowsUsed} matching window(s) were open; " +
                          "open more copies of the app and start a new session for higher concurrency.");
        sb.AppendLine($"  Result: {passed}/{total} passes fully green. Avg pass duration {avg:0}ms.");

        // Per-window breakdown for load runs.
        if (windowsUsed > 1)
        {
            foreach (var grp in passResults.GroupBy(r => r.WindowIndex).OrderBy(g => g.Key))
            {
                int wp = grp.Count(r => r.Passed);
                double wa = grp.Average(r => r.DurationMs);
                sb.AppendLine($"    • window {grp.Key + 1} ('{grp.First().WindowTitle}'): {wp}/{grp.Count()} passed, avg {wa:0}ms.");
            }
        }

        // Surface the first failing step across the batch, if any.
        var firstFail = passResults.SelectMany(r => r.Steps.Select(s => (r, s)))
                                    .FirstOrDefault(t => !t.s.Passed);
        if (firstFail.s is not null)
            sb.AppendLine($"  First failure — run {firstFail.r.RunIndex} win {firstFail.r.WindowIndex + 1}, step {firstFail.s.Index + 1} ({firstFail.s.Label}): {firstFail.s.Message}");

        sb.AppendLine($"  Recording + full report will be written on end_test_session → {session.ReportHtml}");
        return sb.ToString();
    }
}
