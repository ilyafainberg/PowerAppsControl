// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Ilya Fainberg
// -----------------------------------------------------------------------------
//  PowerAppsControl — FFmpeg provisioning (check + install if missing)
//
//  Licensed under the GNU General Public License v3.0 or later (GPL-3.0-or-later).
//  See the LICENSE file in the project root for the full text.
//
//  Video recording needs FFmpeg. Rather than make the user hunt for it, this
//  ensures it is available with a two-tier strategy that needs no admin rights:
//
//    1. Already present?  ScreenRecorder.FindFfmpeg() searches env / cache / PATH
//       / WinGet / common dirs. If found, we're done.
//    2. winget install    Gyan.FFmpeg (silent, agreements accepted). Fast + trusted
//       when winget is available (Windows 10 1809+ / 11). Re-check afterwards.
//    3. Direct download    a static build zip, extracting ONLY ffmpeg.exe into the
//       per-user cache %LOCALAPPDATA%\PowerAppsControl\ffmpeg\ffmpeg.exe. Works even
//       when winget is missing/blocked.
//
//  Invoked by:  `PowerAppsControl.exe --ensure-ffmpeg` (installer + manual),
//  and by the `ensure_ffmpeg` MCP tool so the agent can self-heal before recording.
// -----------------------------------------------------------------------------

using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace PowerAppsControl;

internal static class FfmpegSetup
{
    // A static "essentials" Windows build. Gyan's essentials zip is compact (~40 MB)
    // and lays out ffmpeg.exe under <root>/bin/ffmpeg.exe.
    private const string DownloadUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

    internal readonly record struct Result(bool Available, string Path, string Detail);

    /// <summary>
    /// Ensure ffmpeg.exe is available. <paramref name="allowInstall"/>=false only probes.
    /// Returns where it is (or a reason it isn't). Never throws — recording is optional.
    /// </summary>
    public static Result Ensure(bool allowInstall, Action<string>? log = null)
    {
        void Log(string m) { try { log?.Invoke(m); } catch { /* ignore logging faults */ } }

        var existing = ScreenRecorder.FindFfmpeg();
        if (existing is not null)
            return new Result(true, existing, "FFmpeg already available.");

        if (!allowInstall)
            return new Result(false, "", "FFmpeg not found (install not attempted).");

        // Tier 2 — winget.
        Log("FFmpeg not found. Trying winget (Gyan.FFmpeg)…");
        if (TryWinget(Log))
        {
            var after = ScreenRecorder.FindFfmpeg();
            if (after is not null) return new Result(true, after, "Installed via winget.");
            Log("winget reported success but ffmpeg.exe is not yet on PATH; falling back to direct download.");
        }
        else
        {
            Log("winget unavailable or failed; falling back to direct download.");
        }

        // Tier 3 — direct download into the per-user cache.
        try
        {
            var path = DownloadAndExtract(Log);
            return new Result(true, path, "Downloaded a static FFmpeg build into the per-user cache.");
        }
        catch (Exception ex)
        {
            return new Result(false, "",
                $"Could not provision FFmpeg automatically: {ex.Message}. " +
                "Install it manually with 'winget install Gyan.FFmpeg' or set POWERAPPSCONTROL_FFMPEG to ffmpeg.exe.");
        }
    }

    /// <summary>Run 'winget install Gyan.FFmpeg' silently. Returns true on exit code 0.</summary>
    private static bool TryWinget(Action<string> log)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "winget",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in new[]
            {
                "install", "--id", "Gyan.FFmpeg", "-e",
                "--silent", "--accept-package-agreements", "--accept-source-agreements",
                "--disable-interactivity",
            }) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return false;
            p.OutputDataReceived += (_, _) => { };
            if (!p.WaitForExit(180_000)) { try { p.Kill(true); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            log($"winget invocation failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Download the static build and extract only ffmpeg.exe into the per-user cache.</summary>
    private static string DownloadAndExtract(Action<string> log)
    {
        var dest = ScreenRecorder.CachedFfmpegPath;
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

        var tmpZip = Path.Combine(Path.GetTempPath(), $"pac-ffmpeg-{Guid.NewGuid():N}.zip");
        log($"Downloading FFmpeg from {DownloadUrl} …");
        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("PowerAppsControl-FFmpegSetup");
                using var resp = http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                resp.EnsureSuccessStatusCode();
                using var src = resp.Content.ReadAsStream();
                using var fs = File.Create(tmpZip);
                src.CopyTo(fs);
            }

            log("Extracting ffmpeg.exe …");
            using (var zip = ZipFile.OpenRead(tmpZip))
            {
                var entry = zip.Entries.FirstOrDefault(e =>
                    e.FullName.EndsWith("bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase) ||
                    e.FullName.EndsWith("/ffmpeg.exe", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
                if (entry is null)
                    throw new InvalidOperationException("ffmpeg.exe not found inside the downloaded archive.");
                entry.ExtractToFile(dest, overwrite: true);
            }
        }
        finally
        {
            try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { /* temp cleanup best-effort */ }
        }

        if (!File.Exists(dest))
            throw new InvalidOperationException("Extraction completed but ffmpeg.exe is missing at the cache path.");
        log($"FFmpeg ready at {dest}");
        return dest;
    }

    /// <summary>CLI entry for `--ensure-ffmpeg`. Prints progress; returns a process exit code.</summary>
    public static int RunCli(bool quiet)
    {
        var r = Ensure(allowInstall: true, log: quiet ? null : Console.WriteLine);
        if (r.Available)
        {
            if (!quiet) Console.WriteLine($"✓ FFmpeg available: {r.Path}");
            return 0;
        }
        Console.Error.WriteLine($"✗ {r.Detail}");
        return 1;
    }
}
