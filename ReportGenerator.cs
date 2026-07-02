// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Ilya Fainberg
// -----------------------------------------------------------------------------
//  PowerAppsControl — HTML report generator
//
//  Licensed under the GNU General Public License v3.0 or later (GPL-3.0-or-later).
//  See the LICENSE file in the project root for the full text.
//
//  Author: Ilya Fainberg <ifain@microsoft.com>
//
//  Renders a TestSessionReport into a single self-contained HTML file that sits
//  in the session folder next to the video and screenshots (referenced with
//  relative paths, so the whole folder is portable). No external assets, no JS
//  frameworks — just inline CSS and a tiny bit of vanilla JS for row expansion.
// -----------------------------------------------------------------------------

using System.IO;
using System.Net;
using System.Text;

namespace PowerAppsControl;

internal static class ReportGenerator
{
    public static void Write(TestSessionReport r, string path)
    {
        var sb = new StringBuilder();
        int totalPasses = r.Results.Count;
        int passedPasses = r.Results.Count(x => x.Passed);
        double passRate = totalPasses > 0 ? 100.0 * passedPasses / totalPasses : 0;
        double avgMs = r.Results.Count > 0 ? r.Results.Average(x => x.DurationMs) : 0;
        var duration = r.EndedAt - r.StartedAt;

        int totalSteps = r.Results.Sum(x => x.Steps.Count);
        int failedSteps = r.Results.Sum(x => x.Steps.Count(s => !s.Passed));

        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append($"<title>{Enc(r.AppName)} — UX Test Report</title>");
        sb.Append("<style>").Append(Css()).Append("</style></head><body>");

        // Header
        sb.Append("<header class=\"hero\">");
        sb.Append("<div class=\"hero-in\">");
        sb.Append($"<div class=\"eyebrow\">{(r.Mode == "smoke" ? "SMOKE TEST" : "UX TEST")} REPORT</div>");
        sb.Append($"<h1>{Enc(r.AppName)}</h1>");
        sb.Append("<div class=\"meta\">");
        sb.Append($"<span>Session <b>{Enc(r.SessionId)}</b></span>");
        sb.Append($"<span>{r.StartedAt:yyyy-MM-dd HH:mm:ss} → {r.EndedAt:HH:mm:ss}</span>");
        sb.Append($"<span>{FmtDuration(duration)}</span>");
        sb.Append($"<span>{r.ParallelWindows} window(s)</span>");
        sb.Append("</div></div></header>");

        sb.Append("<main>");

        // KPI tiles
        sb.Append("<section class=\"tiles\">");
        sb.Append(Tile("Runs", totalPasses.ToString(), $"{passedPasses} passed / {totalPasses - passedPasses} failed"));
        sb.Append(Tile("Pass rate", $"{passRate:0}%", passRate >= 100 ? "all green" : $"{failedSteps} step failure(s)", passRate >= 100 ? "good" : (passRate >= 50 ? "warn" : "bad")));
        sb.Append(Tile("Avg run", $"{avgMs:0}<span class=\"u\">ms</span>", $"{totalSteps} step(s) total"));
        sb.Append(Tile("Windows", r.ParallelWindows.ToString(), r.ParallelWindows > 1 ? "load simulation" : "single window"));
        sb.Append("</section>");

        // Video
        if (!string.IsNullOrEmpty(r.VideoFile) && File.Exists(r.VideoFile))
        {
            var rel = Path.GetFileName(r.VideoFile);
            sb.Append("<section class=\"card\"><h2>Session recording</h2>");
            sb.Append($"<video controls preload=\"metadata\" src=\"{Enc(rel)}\"></video>");
            sb.Append($"<div class=\"sub\">{Enc(rel)}</div></section>");
        }

        // Windows
        if (r.WindowTitles.Count > 0)
        {
            sb.Append("<section class=\"card\"><h2>Windows under test</h2><ul class=\"wins\">");
            for (int i = 0; i < r.WindowTitles.Count; i++)
                sb.Append($"<li><span class=\"wi\">#{i + 1}</span>{Enc(r.WindowTitles[i])}</li>");
            sb.Append("</ul></section>");
        }

        // Suggested script (smoke)
        if (r.Script is not null && r.Script.Steps.Count > 0)
        {
            sb.Append("<section class=\"card\"><h2>Suggested repeatable script</h2>");
            sb.Append("<div class=\"sub\">Generated from the smoke-test exploration. Save it and replay it as a regression test.</div>");
            sb.Append($"<pre class=\"code\">{Enc(TestJson.Write(r.Script))}</pre></section>");
        }

        // Runs
        sb.Append("<section class=\"card\"><h2>Run details</h2>");
        if (r.Results.Count == 0)
        {
            sb.Append("<div class=\"sub\">No scripted runs were executed in this session.</div>");
        }
        else
        {
            sb.Append("<table class=\"runs\"><thead><tr>");
            sb.Append("<th></th><th>Run</th><th>Window</th><th>Result</th><th>Steps</th><th>Duration</th></tr></thead><tbody>");
            int rowId = 0;
            foreach (var run in r.Results.OrderBy(x => x.RunIndex).ThenBy(x => x.WindowIndex))
            {
                rowId++;
                int okSteps = run.Steps.Count(s => s.Passed);
                var cls = run.Passed ? "pass" : "fail";
                sb.Append($"<tr class=\"run-row {cls}\" onclick=\"tgl('d{rowId}')\">");
                sb.Append("<td class=\"exp\">▸</td>");
                sb.Append($"<td>#{run.RunIndex}</td>");
                sb.Append($"<td>{run.WindowIndex + 1} — {Enc(Short(run.WindowTitle, 40))}</td>");
                sb.Append($"<td><span class=\"badge {cls}\">{(run.Passed ? "PASS" : "FAIL")}</span></td>");
                sb.Append($"<td>{okSteps}/{run.Steps.Count}</td>");
                sb.Append($"<td>{run.DurationMs}ms</td></tr>");

                sb.Append($"<tr class=\"detail\" id=\"d{rowId}\"><td colspan=\"6\"><div class=\"steps\">");
                foreach (var s in run.Steps)
                {
                    var scls = s.Passed ? "ok" : "no";
                    sb.Append($"<div class=\"step {scls}\">");
                    sb.Append($"<span class=\"dot\"></span>");
                    sb.Append($"<span class=\"sidx\">{s.Index + 1}</span>");
                    sb.Append($"<span class=\"sact\">{Enc(s.Action)}</span>");
                    sb.Append($"<span class=\"slabel\">{Enc(s.Label)}</span>");
                    sb.Append($"<span class=\"smsg\">{Enc(s.Message)}</span>");
                    sb.Append($"<span class=\"sms\">{s.DurationMs}ms</span>");
                    if (!string.IsNullOrEmpty(s.ScreenshotFile))
                        sb.Append($"<a class=\"shot\" href=\"screenshots/{Enc(s.ScreenshotFile)}\" target=\"_blank\">view</a>");
                    sb.Append("</div>");
                }
                sb.Append("</div></td></tr>");
            }
            sb.Append("</tbody></table>");
        }
        sb.Append("</section>");

        if (!string.IsNullOrEmpty(r.Notes))
            sb.Append($"<section class=\"card\"><h2>Notes</h2><div class=\"sub\">{Enc(r.Notes)}</div></section>");

        sb.Append("</main>");
        sb.Append("<footer>Generated by PowerAppsControl · ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm")).Append("</footer>");
        sb.Append("<script>function tgl(id){var e=document.getElementById(id);e.classList.toggle('open');}</script>");
        sb.Append("</body></html>");

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static string Tile(string label, string value, string sub, string tone = "")
    {
        return $"<div class=\"tile {tone}\"><div class=\"tl\">{Enc(label)}</div>" +
               $"<div class=\"tv\">{value}</div><div class=\"ts\">{Enc(sub)}</div></div>";
    }

    private static string FmtDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" :
        t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds}s" : $"{t.Seconds}s";

    private static string Short(string s, int n) => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");

    private static string Enc(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static string Css() => """
        :root{--bg:#f3f5f9;--card:#fff;--ink:#1a2233;--sub:#5a6b85;--line:#e4e9f2;--accent:#2b6cb0;
        --good:#1f9d55;--warn:#c9820a;--bad:#d64545;--goodbg:#e9f8ef;--warnbg:#fdf4e3;--badbg:#fbeaea;}
        *{box-sizing:border-box}body{margin:0;font-family:'Segoe UI',system-ui,sans-serif;background:var(--bg);color:var(--ink)}
        .hero{background:linear-gradient(120deg,#11203a,#1f3d63 60%,#2b6cb0);color:#fff;padding:34px 24px}
        .hero-in{max-width:1080px;margin:0 auto}
        .eyebrow{letter-spacing:.14em;font-size:12px;opacity:.8;font-weight:600}
        .hero h1{margin:6px 0 12px;font-size:30px}
        .meta{display:flex;flex-wrap:wrap;gap:18px;font-size:13px;opacity:.9}
        .meta b{font-weight:600}
        main{max-width:1080px;margin:0 auto;padding:22px 24px 60px}
        .tiles{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:14px;margin-bottom:20px}
        .tile{background:var(--card);border:1px solid var(--line);border-radius:14px;padding:16px 18px}
        .tile .tl{font-size:12px;color:var(--sub);text-transform:uppercase;letter-spacing:.06em}
        .tile .tv{font-size:30px;font-weight:700;margin:4px 0}
        .tile .tv .u{font-size:14px;font-weight:500;color:var(--sub);margin-left:2px}
        .tile .ts{font-size:12px;color:var(--sub)}
        .tile.good{background:var(--goodbg);border-color:#bde9cd}.tile.good .tv{color:var(--good)}
        .tile.warn{background:var(--warnbg);border-color:#f0dcae}.tile.warn .tv{color:var(--warn)}
        .tile.bad{background:var(--badbg);border-color:#eec4c4}.tile.bad .tv{color:var(--bad)}
        .card{background:var(--card);border:1px solid var(--line);border-radius:14px;padding:18px 20px;margin-bottom:18px}
        .card h2{margin:0 0 12px;font-size:17px}
        .sub{color:var(--sub);font-size:13px;margin-bottom:10px}
        video{width:100%;max-height:520px;background:#000;border-radius:10px}
        .wins{list-style:none;margin:0;padding:0;display:flex;flex-wrap:wrap;gap:8px}
        .wins li{background:#f5f7fb;border:1px solid var(--line);border-radius:8px;padding:6px 12px;font-size:13px}
        .wins .wi{color:var(--accent);font-weight:700;margin-right:8px}
        pre.code{background:#0f1729;color:#d6e2ff;padding:16px;border-radius:10px;overflow:auto;font-size:12.5px;line-height:1.5;max-height:420px}
        table.runs{width:100%;border-collapse:collapse;font-size:13px}
        table.runs th{text-align:left;color:var(--sub);font-weight:600;padding:8px 10px;border-bottom:2px solid var(--line)}
        .run-row{cursor:pointer;border-bottom:1px solid var(--line)}
        .run-row:hover{background:#f7f9fd}
        .run-row td{padding:9px 10px}
        .run-row .exp{color:var(--sub);transition:transform .15s}
        .badge{font-size:11px;font-weight:700;padding:3px 9px;border-radius:20px}
        .badge.pass{background:var(--goodbg);color:var(--good)}.badge.fail{background:var(--badbg);color:var(--bad)}
        tr.detail{display:none}tr.detail.open{display:table-row}
        tr.detail>td{background:#f7f9fd;padding:0}
        .steps{padding:8px 14px}
        .step{display:grid;grid-template-columns:14px 26px 96px 1fr 1.4fr 62px auto;gap:10px;align-items:center;padding:6px 4px;font-size:12.5px;border-bottom:1px dashed var(--line)}
        .step:last-child{border-bottom:none}
        .step .dot{width:9px;height:9px;border-radius:50%}
        .step.ok .dot{background:var(--good)}.step.no .dot{background:var(--bad)}
        .step .sidx{color:var(--sub)}.step .sact{font-weight:600;color:var(--accent)}
        .step .slabel{color:var(--ink)}.step .smsg{color:var(--sub)}
        .step.no .smsg{color:var(--bad)}
        .step .sms{color:var(--sub);text-align:right}
        .step .shot{color:var(--accent);text-decoration:none;font-weight:600}
        footer{max-width:1080px;margin:0 auto;padding:0 24px 40px;color:var(--sub);font-size:12px}
        """;
}
