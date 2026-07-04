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

        // Natural-language test plan (smoke deliverable) — human-readable, not JSON.
        if (!string.IsNullOrWhiteSpace(r.SuggestedPlan))
        {
            sb.Append("<section class=\"card\"><h2>Repeatable test plan</h2>");
            sb.Append("<div class=\"sub\">A plain-language plan you (or an agent) can re-run. Save it with save_test_plan to keep it.</div>");
            sb.Append($"<div class=\"plan\">{RenderPlan(r.SuggestedPlan!)}</div></section>");
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

    /// <summary>
    /// Render a natural-language plan (light Markdown: # headings, 1. / - lists, blank-line
    /// paragraphs, **bold**, `code`) into clean HTML. Deliberately minimal — the plan is prose,
    /// not a document, so we only handle what a test plan actually uses.
    /// </summary>
    private static string RenderPlan(string md)
    {
        var lines = md.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        string listKind = ""; // "ol" | "ul" | ""
        void CloseList() { if (listKind != "") { sb.Append($"</{listKind}>"); listKind = ""; } }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            var t = line.TrimStart();
            if (t.Length == 0) { CloseList(); continue; }

            // Headings (#, ##, ###)
            var h = System.Text.RegularExpressions.Regex.Match(t, @"^(#{1,4})\s+(.*)$");
            if (h.Success) { CloseList(); int lvl = Math.Min(4, h.Groups[1].Value.Length); sb.Append($"<h{lvl + 2}>{Inline(h.Groups[2].Value)}</h{lvl + 2}>"); continue; }

            // Ordered list item: "1. text"
            var ol = System.Text.RegularExpressions.Regex.Match(t, @"^\d+[.)]\s+(.*)$");
            if (ol.Success) { if (listKind != "ol") { CloseList(); sb.Append("<ol>"); listKind = "ol"; } sb.Append($"<li>{Inline(ol.Groups[1].Value)}</li>"); continue; }

            // Unordered list item: "- text" / "* text" / "• text"
            var ul = System.Text.RegularExpressions.Regex.Match(t, @"^[-*•]\s+(.*)$");
            if (ul.Success) { if (listKind != "ul") { CloseList(); sb.Append("<ul>"); listKind = "ul"; } sb.Append($"<li>{Inline(ul.Groups[1].Value)}</li>"); continue; }

            CloseList();
            sb.Append($"<p>{Inline(t)}</p>");
        }
        CloseList();
        return sb.ToString();
    }

    /// <summary>Encode text and apply inline **bold** and `code`.</summary>
    private static string Inline(string s)
    {
        var e = Enc(s);
        e = System.Text.RegularExpressions.Regex.Replace(e, @"\*\*(.+?)\*\*", "<strong>$1</strong>");
        e = System.Text.RegularExpressions.Regex.Replace(e, @"`(.+?)`", "<code>$1</code>");
        return e;
    }

    private static string Css() => """
        :root{color-scheme:light;
        --bg:#ffffff;--card:#ffffff;--surface-soft:#f6f6f6;--ink:#000000;--sub:#404040;--soft:#6b6b6b;
        --line:#e6e6e6;--line-strong:#cfcfcf;--accent:#D85A86;--accent-hover:#c34772;--accent-soft:rgba(216,90,134,0.10);
        --accent-fg:#ffffff;--good:#16a34a;--warn:#b45309;--bad:#dc2626;
        --goodbg:rgba(22,163,74,0.10);--warnbg:rgba(180,83,9,0.10);--badbg:rgba(220,38,38,0.10);
        --shadow:0 18px 48px rgba(0,0,0,0.08);--highlight:rgba(216,90,134,0.12);--code-bg:#f6f6f6;--code-fg:#111111;}
        @media(prefers-color-scheme:dark){:root{color-scheme:dark;
        --bg:#000000;--card:#121212;--surface-soft:#181818;--ink:#ffffff;--sub:#b8b8b8;--soft:#8a8a8a;
        --line:#262626;--line-strong:#3d3d3d;--accent:#D85A86;--accent-hover:#e87aa0;--accent-soft:rgba(216,90,134,0.16);
        --good:#4ade80;--warn:#fbbf24;--bad:#f87171;
        --goodbg:rgba(74,222,128,0.12);--warnbg:rgba(251,191,36,0.12);--badbg:rgba(248,113,113,0.12);
        --shadow:0 18px 48px rgba(0,0,0,0.6);--highlight:rgba(216,90,134,0.14);--code-bg:#0c0c0c;--code-fg:#e8e8e8;}}
        *{box-sizing:border-box}
        body{margin:0;font-family:'Segoe UI',Aptos,Calibri,-apple-system,BlinkMacSystemFont,sans-serif;background:var(--bg);color:var(--ink);line-height:1.55;-webkit-font-smoothing:antialiased}
        code,pre{font-family:Consolas,'Courier New',monospace}
        .hero{background:radial-gradient(900px 360px at 88% -10%,var(--highlight),transparent 60%),radial-gradient(700px 320px at 0% 0%,var(--accent-soft),transparent 65%);padding:56px 24px 40px;border-bottom:1px solid var(--line)}
        .hero-in{max-width:1120px;margin:0 auto}
        .eyebrow{display:inline-flex;align-items:center;gap:8px;padding:5px 10px;border-radius:3px;background:var(--accent-soft);color:var(--accent);font-size:12px;font-weight:600;letter-spacing:.04em;text-transform:uppercase}
        .eyebrow::before{content:"";width:6px;height:6px;border-radius:3px;background:var(--accent)}
        .hero h1{margin:14px 0 14px;font-size:clamp(30px,4vw,46px);line-height:1.06;letter-spacing:-0.02em;font-weight:700}
        .meta{display:flex;flex-wrap:wrap;gap:18px;font-size:13px;color:var(--sub)}
        .meta b{font-weight:600;color:var(--ink)}
        main{max-width:1120px;margin:0 auto;padding:26px 24px 60px}
        .tiles{display:grid;grid-template-columns:repeat(auto-fit,minmax(190px,1fr));gap:14px;margin-bottom:22px}
        .tile{background:var(--card);border:1px solid var(--line);border-radius:3px;padding:18px 18px;box-shadow:var(--shadow)}
        .tile .tl{font-size:12px;color:var(--sub);text-transform:uppercase;letter-spacing:.06em;font-weight:600}
        .tile .tv{font-size:32px;font-weight:700;margin:6px 0;letter-spacing:-0.02em}
        .tile .tv .u{font-size:14px;font-weight:500;color:var(--sub);margin-left:2px}
        .tile .ts{font-size:12px;color:var(--sub)}
        .tile.good{border-color:var(--good)}.tile.good .tv{color:var(--good)}
        .tile.warn{border-color:var(--warn)}.tile.warn .tv{color:var(--warn)}
        .tile.bad{border-color:var(--bad)}.tile.bad .tv{color:var(--bad)}
        .card{background:var(--card);border:1px solid var(--line);border-radius:3px;padding:20px 22px;margin-bottom:18px}
        .card h2{margin:0 0 14px;font-size:17px;font-weight:700;letter-spacing:-0.01em}
        .sub{color:var(--sub);font-size:13px;margin-bottom:12px}
        video{width:100%;max-height:540px;background:#000;border-radius:3px;border:1px solid var(--line)}
        .wins{list-style:none;margin:0;padding:0;display:flex;flex-wrap:wrap;gap:8px}
        .wins li{background:var(--surface-soft);border:1px solid var(--line);border-radius:3px;padding:6px 12px;font-size:13px}
        .wins .wi{color:var(--accent);font-weight:700;margin-right:8px}
        pre.code{background:var(--code-bg);color:var(--code-fg);padding:16px;border-radius:3px;border:1px solid var(--line);overflow:auto;font-size:12.5px;line-height:1.5;max-height:440px}
        .plan{font-size:14px;line-height:1.6}
        .plan h3,.plan h4,.plan h5{margin:14px 0 6px;font-weight:700;letter-spacing:-0.01em}
        .plan ol,.plan ul{margin:8px 0 8px 4px;padding-left:22px}
        .plan li{margin:5px 0}
        .plan p{margin:8px 0}
        .plan code{background:var(--surface-soft);border:1px solid var(--line);border-radius:3px;padding:1px 5px;font-size:12.5px}
        .plan strong{color:var(--ink)}
        table.runs{width:100%;border-collapse:collapse;font-size:13px}
        table.runs th{text-align:left;color:var(--sub);font-weight:600;padding:9px 10px;border-bottom:2px solid var(--line-strong);text-transform:uppercase;font-size:11.5px;letter-spacing:.04em}
        .run-row{cursor:pointer;border-bottom:1px solid var(--line)}
        .run-row:hover{background:var(--accent-soft)}
        .run-row td{padding:10px 10px}
        .run-row .exp{color:var(--accent);transition:transform .15s;font-size:11px}
        .badge{font-size:11px;font-weight:700;padding:3px 9px;border-radius:3px;letter-spacing:.03em}
        .badge.pass{background:var(--goodbg);color:var(--good)}.badge.fail{background:var(--badbg);color:var(--bad)}
        tr.detail{display:none}tr.detail.open{display:table-row}
        tr.detail>td{background:var(--surface-soft);padding:0}
        .steps{padding:8px 14px}
        .step{display:grid;grid-template-columns:12px 26px 100px 1fr 1.4fr 62px auto;gap:10px;align-items:center;padding:7px 4px;font-size:12.5px;border-bottom:1px solid var(--line)}
        .step:last-child{border-bottom:none}
        .step .dot{width:8px;height:8px;border-radius:2px}
        .step.ok .dot{background:var(--good)}.step.no .dot{background:var(--bad)}
        .step .sidx{color:var(--soft)}.step .sact{font-weight:600;color:var(--accent)}
        .step .slabel{color:var(--ink)}.step .smsg{color:var(--sub)}
        .step.no .smsg{color:var(--bad)}
        .step .sms{color:var(--soft);text-align:right}
        .step .shot{color:var(--accent);text-decoration:none;font-weight:600}
        .step .shot:hover{text-decoration:underline}
        footer{max-width:1120px;margin:0 auto;padding:0 24px 40px;color:var(--soft);font-size:12px}
        """;
}
