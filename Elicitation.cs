// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Ilya Fainberg
// -----------------------------------------------------------------------------
//  PowerAppsControl — MCP elicitation helper (server-driven clickable choices)
//
//  Licensed under the GNU General Public License v3.0 or later (GPL-3.0-or-later).
//  See the LICENSE file in the project root for the full text.
//
//  Author: Ilya Fainberg <ifain@microsoft.com>
//
//  Lets a TOOL ask the user to pick from a small set of options and have the MCP
//  CLIENT render them as buttons / a picker — the choice logic lives in the
//  server, not in the agent's prompt. Uses the MCP "elicitation" capability
//  (server → client structured input request). If the connected client does not
//  support elicitation, AskAsync returns null and the caller falls back to
//  returning the options as text so the agent can present them itself.
// -----------------------------------------------------------------------------

using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace PowerAppsControl;

/// <summary>One selectable option: a stable value plus a human label and blurb.</summary>
internal readonly record struct Choice(string Value, string Label, string? Description = null);

internal static class Elicitation
{
    /// <summary>
    /// Ask the user to choose ONE of <paramref name="options"/> via MCP elicitation. Returns the
    /// chosen option's Value, or null if the client declined/cancelled or does not support
    /// elicitation (the caller should then fall back to a text prompt).
    /// </summary>
    public static async Task<string?> AskChoiceAsync(
        McpServer? server, string message, string propertyName,
        IReadOnlyList<Choice> options, CancellationToken ct)
    {
        if (server is null || options.Count == 0) return null;

        try
        {
            var schema = new ElicitRequestParams.TitledSingleSelectEnumSchema
            {
                Title = message,
                OneOf = options
                    .Select(o => new ElicitRequestParams.EnumSchemaOption { Const = o.Value, Title = o.Label })
                    .ToList(),
            };

            var request = new ElicitRequestParams
            {
                Message = message,
                RequestedSchema = new ElicitRequestParams.RequestSchema
                {
                    Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                    {
                        [propertyName] = schema,
                    },
                    Required = new List<string> { propertyName },
                },
            };

            var result = await server.ElicitAsync(request, ct).ConfigureAwait(false);

            if (!string.Equals(result.Action, "accept", StringComparison.OrdinalIgnoreCase)) return null;
            if (result.Content is null || !result.Content.TryGetValue(propertyName, out var value)) return null;

            var chosen = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
            return string.IsNullOrEmpty(chosen) ? null : chosen;
        }
        catch
        {
            // Client doesn't support elicitation (or the request failed) → caller falls back to text.
            return null;
        }
    }
}
