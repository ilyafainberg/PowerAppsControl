// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Ilya Fainberg
// -----------------------------------------------------------------------------
//  PowerAppsControl — Test data models (scripts, steps, run results, reports)
//
//  Licensed under the GNU General Public License v3.0 or later (GPL-3.0-or-later).
//  See the LICENSE file in the project root for the full text.
//
//  Author: Ilya Fainberg <ifain@microsoft.com>
//
//  Plain DTOs shared by the testing tools, the runner and the report generator,
//  plus a single JSON facade (camelCase, case-insensitive, enum-as-string) so a
//  test script authored by a user reads naturally:
//
//    { "name": "Submit a referral",
//      "appName": "Contoso Referrals",
//      "steps": [
//        { "action": "waitForElement", "description": "app loaded", "name": "New referral", "timeoutMs": 8000 },
//        { "action": "clickElement",   "description": "open form",  "name": "New referral" },
//        { "action": "type",           "description": "patient",    "keys": "Jane Doe{Tab}" },
//        { "action": "clickElement",   "description": "submit",     "name": "Submit" },
//        { "action": "assertElement",  "description": "confirmed",  "name": "Thank you", "shouldExist": true }
//      ] }
// -----------------------------------------------------------------------------

using System.Text.Json;
using System.Text.Json.Serialization;

namespace PowerAppsControl;

/// <summary>The kind of action a single test step performs.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum StepAction
{
    /// <summary>Click at window-relative pixel coordinates (x, y).</summary>
    Click,
    /// <summary>Find + invoke a control through UI Automation (name / automationId / controlType).</summary>
    ClickElement,
    /// <summary>Type text and/or key chords into the focused app (send_keys syntax).</summary>
    Type,
    /// <summary>Scroll the mouse wheel over the app.</summary>
    Scroll,
    /// <summary>Fixed pause (ms) to let the UI settle.</summary>
    Wait,
    /// <summary>Poll until a control appears (or timeout) — a synchronization gate.</summary>
    WaitForElement,
    /// <summary>Assert a control is present (or absent). Fails the step when the expectation is not met.</summary>
    AssertElement,
    /// <summary>Capture the app window into the report.</summary>
    Screenshot,
}

/// <summary>One step of a test script. Only the fields relevant to the action are used.</summary>
internal sealed class TestStep
{
    [JsonPropertyName("action")] public StepAction Action { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }

    // click / scroll coordinates (window-relative)
    [JsonPropertyName("x")] public int? X { get; set; }
    [JsonPropertyName("y")] public int? Y { get; set; }
    [JsonPropertyName("button")] public string? Button { get; set; }
    [JsonPropertyName("clicks")] public int? Clicks { get; set; }

    // type
    [JsonPropertyName("keys")] public string? Keys { get; set; }

    // scroll
    [JsonPropertyName("amount")] public int? Amount { get; set; }
    [JsonPropertyName("horizontal")] public bool? Horizontal { get; set; }

    // element lookup (clickElement / waitForElement / assertElement)
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("automationId")] public string? AutomationId { get; set; }
    [JsonPropertyName("controlType")] public string? ControlType { get; set; }

    // wait / waitForElement
    [JsonPropertyName("ms")] public int? Ms { get; set; }
    [JsonPropertyName("timeoutMs")] public int? TimeoutMs { get; set; }

    // assertElement
    [JsonPropertyName("shouldExist")] public bool? ShouldExist { get; set; }

    /// <summary>A short human label for logs / HUD / report, derived when no description is supplied.</summary>
    public string Label()
    {
        if (!string.IsNullOrWhiteSpace(Description)) return Description!;
        return Action switch
        {
            StepAction.Click          => $"click ({X},{Y})",
            StepAction.ClickElement   => $"click '{Name ?? AutomationId ?? ControlType}'",
            StepAction.Type           => $"type \"{Trim(Keys)}\"",
            StepAction.Scroll         => $"scroll {Amount}",
            StepAction.Wait           => $"wait {Ms}ms",
            StepAction.WaitForElement => $"wait for '{Name ?? AutomationId}'",
            StepAction.AssertElement  => $"assert '{Name ?? AutomationId}' {(ShouldExist == false ? "absent" : "present")}",
            StepAction.Screenshot     => "screenshot",
            _ => Action.ToString(),
        };
    }

    private static string Trim(string? s) => string.IsNullOrEmpty(s) ? "" : (s.Length <= 24 ? s : s[..24] + "…");
}

/// <summary>A repeatable test script: an ordered list of steps against a named app.</summary>
internal sealed class TestScript
{
    [JsonPropertyName("name")] public string Name { get; set; } = "Untitled test";
    [JsonPropertyName("appName")] public string? AppName { get; set; }
    [JsonPropertyName("steps")] public List<TestStep> Steps { get; set; } = new();
}

/// <summary>Outcome of executing one step.</summary>
internal sealed class StepResult
{
    public int Index { get; set; }
    public string Action { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Passed { get; set; }
    public string Message { get; set; } = "";
    public long DurationMs { get; set; }
    public string? ScreenshotFile { get; set; }
}

/// <summary>Outcome of one full pass of the script in one window.</summary>
internal sealed class RunResult
{
    public int RunIndex { get; set; }
    public int WindowIndex { get; set; }
    public string WindowTitle { get; set; } = "";
    public bool Passed { get; set; }
    public long DurationMs { get; set; }
    public DateTime StartedAt { get; set; }
    public List<StepResult> Steps { get; set; } = new();
}

/// <summary>The full record of a test session — everything the report is built from.</summary>
internal sealed class TestSessionReport
{
    public string SessionId { get; set; } = "";
    public string AppName { get; set; } = "";
    public string Mode { get; set; } = "scripted"; // "scripted" | "smoke"
    public DateTime StartedAt { get; set; }
    public DateTime EndedAt { get; set; }
    public string? VideoFile { get; set; }
    public int Runs { get; set; }
    public int ParallelWindows { get; set; }
    public List<string> WindowTitles { get; set; } = new();
    /// <summary>The human-readable, natural-language test plan (Markdown) — the smoke-test deliverable.</summary>
    public string? SuggestedPlan { get; set; }
    public List<RunResult> Results { get; set; } = new();
    public string? Notes { get; set; }
}

/// <summary>Shared JSON options: camelCase, case-insensitive, indented, enums as strings.</summary>
internal static class TestJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static TestScript ParseScript(string json)
    {
        var s = JsonSerializer.Deserialize<TestScript>(json, Options)
                ?? throw new ArgumentException("Test script JSON deserialized to null.");
        if (s.Steps is null || s.Steps.Count == 0)
            throw new ArgumentException("Test script has no steps.");
        return s;
    }

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
}
