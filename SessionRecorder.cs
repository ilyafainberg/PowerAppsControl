// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Ilya Fainberg
// -----------------------------------------------------------------------------
//  PowerAppsControl — Session video recorder (start / stop wrapper)
//
//  Licensed under the GNU General Public License v3.0 or later (GPL-3.0-or-later).
//  See the LICENSE file in the project root for the full text.
//
//  Author: Ilya Fainberg <ifain@microsoft.com>
//
//  A test SESSION runs for an unknown length, so we cannot use the fixed-duration
//  record_window tool directly. This wraps the same DWM-aware ScreenRecorder on a
//  background thread with a hard safety cap, and lets the session manager Stop()
//  it at any time — the token is cancelled, the capture loop exits and the MP4 is
//  finalized cleanly. The recorder also registers with AgentSession, so the user
//  clicking ✕ on a control frame stops the recording too.
// -----------------------------------------------------------------------------

namespace PowerAppsControl;

internal sealed class SessionRecorder
{
    private readonly IntPtr hwnd;
    private readonly string title;
    private readonly string outputPath;
    private readonly int fps;
    private readonly CancellationTokenSource cts = new();
    private IDisposable? registration;
    private Thread? worker;
    private volatile string summary = "(recording not finalized)";

    // Safety cap: even if end_test_session is never called, the recorder stops
    // itself after this long instead of running forever.
    private const int MaxSeconds = 3600;

    public string OutputPath => outputPath;

    private SessionRecorder(IntPtr hwnd, string title, string outputPath, int fps)
    {
        this.hwnd = hwnd;
        this.title = title;
        this.outputPath = outputPath;
        this.fps = fps;
    }

    /// <summary>
    /// Begin recording <paramref name="hwnd"/> to <paramref name="outputPath"/> in the background.
    /// Returns null (and a reason via <paramref name="error"/>) if FFmpeg is unavailable, so the
    /// session can still run without video instead of failing outright.
    /// </summary>
    public static SessionRecorder? TryStart(IntPtr hwnd, string title, string outputPath, int fps, out string? error)
    {
        if (ScreenRecorder.FindFfmpeg() is null)
        {
            error = "FFmpeg was not found — the session will run WITHOUT video. Install it with " +
                    "'winget install Gyan.FFmpeg' (or set POWERAPPSCONTROL_FFMPEG) and start a new session to record.";
            return null;
        }

        var rec = new SessionRecorder(hwnd, title, outputPath, fps);
        rec.registration = AgentSession.RegisterOperation(rec.cts);
        rec.worker = new Thread(rec.Run) { IsBackground = true, Name = "PowerAppsControl-SessionRecorder" };
        rec.worker.Start();
        error = null;
        return rec;
    }

    private void Run()
    {
        try
        {
            summary = ScreenRecorder.Record(hwnd, title, MaxSeconds, fps, outputPath, cts.Token);
        }
        catch (Exception ex)
        {
            summary = $"Recording error: {ex.Message}";
        }
    }

    /// <summary>Stop the recording and wait for the MP4 to be finalized. Returns the recorder summary.</summary>
    public string Stop()
    {
        try { cts.Cancel(); } catch { /* already cancelled */ }
        try { worker?.Join(20000); } catch { /* best effort */ }
        try { registration?.Dispose(); } catch { /* already disposed */ }
        return summary;
    }
}
