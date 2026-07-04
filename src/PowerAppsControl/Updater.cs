// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Ilya Fainberg
// -----------------------------------------------------------------------------
//  PowerAppsControl — self-updater (GitHub Releases API as the update server)
//
//  Licensed under the GNU General Public License v3.0 or later (GPL-3.0-or-later).
//  See the LICENSE file in the project root for the full text.
//
//  Uses the public GitHub Releases API — no backend to run:
//    1. GET /repos/{owner}/{repo}/releases/latest
//    2. Compare the release tag (e.g. "v1.3.1") to this build's own version.
//    3. If newer, pick the asset matching how we were installed:
//         PORTABLE  build → *-portable-win-x64.zip   (extract over the folder)
//         INSTALLER build → *-setup.zip              (run setup silently)
//    4. Download it WITH A PROGRESS BAR, then hand off to apply-update.cmd — a
//       separate process that waits for THIS (locked) exe to exit, applies the
//       update, and re-registers the server.
//
//  Exposed as: `--check-update`, `--update` (CLI, with console progress) and the
//  `check_for_update` / `update_server` MCP tools (so the agent can self-heal).
// -----------------------------------------------------------------------------

using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace PowerAppsControl;

internal enum InstallKind { Portable, Installer }

internal sealed record UpdateInfo(
    bool UpdateAvailable, Version Current, Version Latest,
    string? AssetName, string? AssetUrl, string Notes, InstallKind Kind);

internal static class Updater
{
    private const string Owner = "ilyafainberg";
    private const string Repo  = "PowerAppsControl";
    private const string ExeName = "PowerAppsControl.exe";

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PowerAppsControl-Updater", CurrentVersion().ToString()));
        h.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return h;
    }

    // ---- version -------------------------------------------------------------

    public static Version CurrentVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var clean = info.Split('+', '-')[0];
            if (Version.TryParse(clean, out var v)) return Normalize(v);
        }
        return Normalize(asm.GetName().Version ?? new Version(0, 0, 0));
    }

    private static Version Normalize(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    private static Version ParseTag(string tag)
    {
        var t = tag.TrimStart('v', 'V').Split('+', '-')[0];
        return Version.TryParse(t, out var v) ? Normalize(v) : new Version(0, 0, 0);
    }

    // ---- check ---------------------------------------------------------------

    /// <summary>Query GitHub for the latest release and decide whether an update applies. Never throws.</summary>
    public static async Task<UpdateInfo> CheckAsync(CancellationToken ct = default)
    {
        var current = CurrentVersion();
        var kind = DetectInstallKind();
        try
        {
            var url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
            var notes = root.TryGetProperty("body", out var b) ? (b.GetString() ?? "") : "";
            var latest = ParseTag(tag);

            if (latest <= current)
                return new UpdateInfo(false, current, latest, null, null, notes, kind);

            var (name, asset) = PickAsset(root, kind);
            return new UpdateInfo(asset is not null, current, latest, name, asset, notes, kind);
        }
        catch (Exception ex)
        {
            return new UpdateInfo(false, current, current, null, null, $"(update check failed: {ex.Message})", kind);
        }
    }

    private static (string? name, string? url) PickAsset(JsonElement release, InstallKind kind)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return (null, null);
        string needle = kind == InstallKind.Installer ? "-setup.zip" : "-portable-win-x64.zip";
        foreach (var a in assets.EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            if (name.EndsWith(needle, StringComparison.OrdinalIgnoreCase))
                return (name, a.GetProperty("browser_download_url").GetString());
        }
        return (null, null);
    }

    private static InstallKind DetectInstallKind()
    {
        var baseDir = AppContext.BaseDirectory;
        var pf  = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pfx = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        bool underPf =
            (!string.IsNullOrEmpty(pf)  && baseDir.StartsWith(pf,  StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(pfx) && baseDir.StartsWith(pfx, StringComparison.OrdinalIgnoreCase));
        return underPf ? InstallKind.Installer : InstallKind.Portable;
    }

    // ---- download (with progress) -------------------------------------------

    /// <summary>Download <paramref name="url"/> to <paramref name="destPath"/>, invoking
    /// <paramref name="onProgress"/>(downloadedBytes, totalBytesOrMinusOne) as it streams.</summary>
    public static async Task DownloadAsync(string url, string destPath, Action<long, long> onProgress, CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? -1;

        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = File.Create(destPath);
        var buffer = new byte[81920];
        long done = 0;
        int read;
        long lastReport = 0;
        while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            // Throttle progress callbacks to ~1 per 256 KB so a console bar isn't spammed.
            if (done - lastReport >= 262_144 || (total > 0 && done == total))
            {
                lastReport = done;
                onProgress(done, total);
            }
        }
        onProgress(done, total);
    }

    // ---- apply ---------------------------------------------------------------

    /// <summary>
    /// Stage the downloaded asset and spawn apply-update.cmd, which waits for THIS process to exit,
    /// applies the update (extract-over for portable, silent setup for installer), and re-registers.
    /// Returns the helper path. The caller should then let the process/host exit.
    /// </summary>
    public static string SpawnApplyHelper(string assetPath, InstallKind kind)
    {
        var installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var helper = Path.Combine(installDir, "apply-update.cmd");
        if (!File.Exists(helper))
        {
            // Portable single-file builds may not ship the helper next to the exe; write our own.
            helper = Path.Combine(Path.GetTempPath(), $"pac-apply-{Guid.NewGuid():N}.cmd");
            File.WriteAllText(helper, EmbeddedHelperScript, new UTF8Encoding(false));
        }

        var psi = new ProcessStartInfo
        {
            FileName = helper,
            UseShellExecute = true,      // allow the installer path to elevate via UAC
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
        };
        foreach (var a in new[]
        {
            Environment.ProcessId.ToString(),
            kind == InstallKind.Installer ? "installer" : "portable",
            assetPath,
            installDir,
            ExeName,
        }) psi.ArgumentList.Add(a);

        Process.Start(psi);
        return helper;
    }

    // ---- CLI entry points ----------------------------------------------------

    public static int RunCheckCli()
    {
        var info = CheckAsync().GetAwaiter().GetResult();
        if (info.UpdateAvailable)
        {
            Console.WriteLine($"Update available: v{info.Latest} (you have v{info.Current}).");
            Console.WriteLine($"  Asset: {info.AssetName}  [{info.Kind} install]");
            Console.WriteLine("Run 'PowerAppsControl.exe --update' to install it.");
            return 0;
        }
        if (info.Latest > new Version(0, 0, 0))
            Console.WriteLine($"You're up to date (installed v{info.Current}, latest release v{info.Latest}).");
        else
            Console.WriteLine($"Could not determine the latest version. {info.Notes}");
        return 0;
    }

    public static int RunUpdateCli()
    {
        var info = CheckAsync().GetAwaiter().GetResult();
        if (!info.UpdateAvailable || info.AssetUrl is null || info.AssetName is null)
        {
            Console.WriteLine($"No update to install (current v{info.Current}, latest v{info.Latest}).");
            return 0;
        }

        Console.WriteLine($"Updating v{info.Current} → v{info.Latest} ({info.Kind} install)…");
        var tmpDir = Path.Combine(Path.GetTempPath(), $"pac-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        var assetPath = Path.Combine(tmpDir, info.AssetName);

        try
        {
            DownloadAsync(info.AssetUrl, assetPath, ConsoleProgress).GetAwaiter().GetResult();
            Console.WriteLine();  // finish the progress line
            Console.WriteLine("Download complete. Applying update (this app/host will restart)…");
            Updater.SpawnApplyHelper(assetPath, info.Kind);
            Console.WriteLine("Update helper launched. Close any running host, and PowerAppsControl will be updated.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Update failed: {ex.Message}");
            return 1;
        }
    }

    private static int lastPct = -1;
    private static void ConsoleProgress(long done, long total)
    {
        if (total <= 0)
        {
            Console.Write($"\r  downloaded {done / 1_048_576.0:0.0} MB");
            return;
        }
        int pct = (int)(done * 100 / total);
        if (pct == lastPct) return;
        lastPct = pct;
        int width = 30;
        int filled = pct * width / 100;
        var bar = new string('█', filled) + new string('░', width - filled);
        Console.Write($"\r  [{bar}] {pct,3}%  {done / 1_048_576.0:0.0}/{total / 1_048_576.0:0.0} MB");
    }

    // A minimal helper script written to TEMP for portable single-file builds that don't
    // ship apply-update.cmd beside the exe. Mirrors the repo's installer/apply-update.cmd.
    private const string EmbeddedHelperScript = """
        @echo off
        setlocal EnableExtensions EnableDelayedExpansion
        set "APP_PID=%~1"
        set "KIND=%~2"
        set "ASSET=%~3"
        set "INSTALL_DIR=%~4"
        set "APP_EXE=%~5"
        set /a _tries=0
        :waitloop
        tasklist /FI "PID eq %APP_PID%" 2>nul | find "%APP_PID%" >nul
        if not errorlevel 1 (
          set /a _tries+=1
          if !_tries! geq 120 goto :giveup
          ping -n 2 127.0.0.1 >nul
          goto :waitloop
        )
        if /I "%KIND%"=="installer" goto :installer
        set "STAGE=%TEMP%\pac-stage-%RANDOM%"
        mkdir "%STAGE%" >nul 2>&1
        powershell -NoProfile -ExecutionPolicy Bypass -Command "Expand-Archive -LiteralPath '%ASSET%' -DestinationPath '%STAGE%' -Force"
        if errorlevel 1 goto :fail
        robocopy "%STAGE%" "%INSTALL_DIR%" /E /IS /IT /NFL /NDL /NJH /NJS /R:2 /W:1 >nul
        if %ERRORLEVEL% GEQ 8 goto :fail
        "%INSTALL_DIR%\%APP_EXE%" --register --quiet
        goto :cleanup
        :installer
        set "STAGE=%TEMP%\pac-stage-%RANDOM%"
        mkdir "%STAGE%" >nul 2>&1
        powershell -NoProfile -ExecutionPolicy Bypass -Command "Expand-Archive -LiteralPath '%ASSET%' -DestinationPath '%STAGE%' -Force"
        if errorlevel 1 goto :fail
        for %%F in ("%STAGE%\*setup*.exe") do ( "%%~fF" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART & goto :cleanup )
        goto :fail
        :cleanup
        if defined STAGE rmdir /S /Q "%STAGE%" >nul 2>&1
        del /Q "%ASSET%" >nul 2>&1
        endlocal
        exit /b 0
        :giveup
        endlocal
        exit /b 1
        :fail
        if defined STAGE rmdir /S /Q "%STAGE%" >nul 2>&1
        endlocal
        exit /b 1
        """;
}
