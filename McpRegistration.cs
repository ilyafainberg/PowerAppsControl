// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Ilya Fainberg
// -----------------------------------------------------------------------------
//  PowerAppsControl — MCP client registration (Scout + GitHub Copilot CLI)
//
//  Licensed under the GNU General Public License v3.0 or later (GPL-3.0-or-later).
//  See the LICENSE file in the project root for the full text.
//
//  Runs when the exe is invoked with `--register` / `--unregister` (the installer
//  calls these post-install / pre-uninstall, and portable users can run them by
//  hand). Registration MERGES this server into the two MCP client config files
//  under %USERPROFILE%\.copilot, preserving every other entry:
//
//    • GitHub Copilot CLI  → mcp-config.json      key: mcpServers["PowerAppsControl"]
//    • Microsoft Scout     → m-mcp-servers.json   key: servers["powerappscontrol"]
//
//  The two hosts use slightly different schemas (see below), so we write each in
//  its own shape. The tool list is discovered by REFLECTION over this assembly's
//  [McpServerTool] methods, so it can never drift from what the server exposes.
//
//  Path handling: the installer runs these as the ORIGINAL (non-elevated) user
//  (Inno `runasoriginaluser`) so %USERPROFILE% resolves to the real user's
//  profile, not the elevated admin's.
// -----------------------------------------------------------------------------

using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace PowerAppsControl;

internal static class McpRegistration
{
    private const string CopilotCliServerName = "PowerAppsControl";   // mcp-config.json key
    private const string ScoutServerKey       = "powerappscontrol";   // m-mcp-servers.json key
    private const string ScoutDisplayName     = "PowerAppsControl";
    private const string SkillName            = "PowerAppsControl";    // m-skills folder + skill name

    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    /// <summary>Register this server into both MCP client configs. Returns a process exit code.</summary>
    public static int Register(bool quiet)
    {
        try
        {
            var exe = Environment.ProcessPath
                      ?? Path.Combine(AppContext.BaseDirectory, "PowerAppsControl.exe");
            var dir = ConfigDir();
            Directory.CreateDirectory(dir);

            var tools = DiscoverToolNames();
            var lines = new List<string>();

            // 1) GitHub Copilot CLI — mcp-config.json (always create-or-update; it's a
            //    plain user MCP-server list, safe to create if the user has no file yet).
            var cliPath = Path.Combine(dir, "mcp-config.json");
            UpdateCopilotCli(cliPath, exe);
            lines.Add($"  ✓ GitHub Copilot CLI  → {cliPath}");

            // 2) Microsoft Scout — m-mcp-servers.json. Only UPDATE an existing file: Scout
            //    owns this file (it also lists builtin servers), so we never create a fresh
            //    partial one on a machine that doesn't run Scout.
            var scoutPath = Path.Combine(dir, "m-mcp-servers.json");
            if (File.Exists(scoutPath))
            {
                UpdateScout(scoutPath, exe, tools);
                lines.Add($"  ✓ Microsoft Scout     → {scoutPath}");
            }
            else
            {
                lines.Add($"  • Microsoft Scout     → skipped (no {scoutPath}; Scout not detected)");
            }

            // 3) Companion skill (/PowerAppsControl) — teaches the agent the workflow.
            var skillMsg = InstallSkill(dir);
            if (skillMsg is not null) lines.Add(skillMsg);

            if (!quiet)
            {
                Console.WriteLine($"PowerAppsControl registered as an MCP server ({tools.Count} tools):");
                foreach (var l in lines) Console.WriteLine(l);
                Console.WriteLine("Restart Scout / the Copilot CLI to pick up the new server.");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Registration failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>Remove this server from both MCP client configs. Returns a process exit code.</summary>
    public static int Unregister(bool quiet)
    {
        try
        {
            var dir = ConfigDir();
            var removed = new List<string>();

            var cliPath = Path.Combine(dir, "mcp-config.json");
            if (RemoveKey(cliPath, "mcpServers", CopilotCliServerName)) removed.Add(cliPath);

            var scoutPath = Path.Combine(dir, "m-mcp-servers.json");
            if (RemoveKey(scoutPath, "servers", ScoutServerKey)) removed.Add(scoutPath);

            if (UninstallSkill(dir)) removed.Add(Path.Combine(dir, "m-skills", SkillName));

            if (!quiet)
            {
                if (removed.Count == 0) Console.WriteLine("PowerAppsControl was not registered (nothing to remove).");
                else { Console.WriteLine("PowerAppsControl unregistered from:"); foreach (var r in removed) Console.WriteLine($"  ✓ {r}"); }
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Unregister failed: {ex.Message}");
            return 1;
        }
    }

    // -------------------------------------------------------------------------

    private static string ConfigDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot");

    /// <summary>Merge our entry into GitHub Copilot CLI's mcp-config.json (mcpServers map).</summary>
    private static void UpdateCopilotCli(string path, string exe)
    {
        var root = LoadObject(path);
        var servers = root["mcpServers"] as JsonObject;
        if (servers is null) { servers = new JsonObject(); root["mcpServers"] = servers; }

        servers[CopilotCliServerName] = new JsonObject
        {
            ["type"] = "local",
            ["command"] = exe,
            ["args"] = new JsonArray(),
            ["tools"] = new JsonArray("*"),
        };
        Save(path, root);
    }

    /// <summary>Merge our entry into Microsoft Scout's m-mcp-servers.json (servers map).</summary>
    private static void UpdateScout(string path, string exe, IReadOnlyList<string> tools)
    {
        var root = LoadObject(path);
        var servers = root["servers"] as JsonObject;
        if (servers is null) { servers = new JsonObject(); root["servers"] = servers; }

        var toolArray = new JsonArray();
        foreach (var t in tools) toolArray.Add(t);

        servers[ScoutServerKey] = new JsonObject
        {
            ["builtin"] = false,
            ["config"] = new JsonObject
            {
                ["name"] = ScoutDisplayName,
                ["type"] = "command",
                ["command"] = exe,
                ["args"] = new JsonArray(),
            },
            ["tools"] = toolArray,
        };
        Save(path, root);
    }

    private static bool RemoveKey(string path, string mapProperty, string serverKey)
    {
        if (!File.Exists(path)) return false;
        var root = LoadObject(path);
        if (root[mapProperty] is JsonObject map && map.ContainsKey(serverKey))
        {
            map.Remove(serverKey);
            Save(path, root);
            return true;
        }
        return false;
    }

    private static JsonObject LoadObject(string path)
    {
        if (!File.Exists(path)) return new JsonObject();
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        return JsonNode.Parse(text) as JsonObject ?? new JsonObject();
    }

    private static void Save(string path, JsonObject root)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(WriteOpts));
        File.Move(tmp, path, overwrite: true); // atomic-ish replace so a crash can't truncate the config
    }

    /// <summary>
    /// Install the companion skill (/PowerAppsControl) so the agent knows the workflow:
    /// copy the bundled skill\SKILL.md into %USERPROFILE%\.copilot\m-skills\PowerAppsControl,
    /// and upsert it into Scout's skills-metadata.json (only if that registry exists — i.e.
    /// Scout is installed). Returns a status line, or null if the bundled skill is missing.
    /// </summary>
    private static string? InstallSkill(string configDir)
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "skill", "SKILL.md");
        if (!File.Exists(bundled)) return null;

        var skillsRoot = Path.Combine(configDir, "m-skills");
        var skillDir = Path.Combine(skillsRoot, SkillName);
        Directory.CreateDirectory(skillDir);
        var destMd = Path.Combine(skillDir, "SKILL.md");
        File.Copy(bundled, destMd, overwrite: true);

        // Upsert into Scout's skill registry, only if Scout already maintains one.
        var registry = Path.Combine(skillsRoot, "skills-metadata.json");
        if (File.Exists(registry))
        {
            try
            {
                UpsertSkillRegistry(registry, File.ReadAllText(destMd));
                return $"  ✓ Companion skill     → {skillDir} (+ registered as /{SkillName})";
            }
            catch
            {
                return $"  ✓ Companion skill     → {skillDir} (registry update skipped; Scout will re-index on restart)";
            }
        }
        return $"  ✓ Companion skill     → {skillDir}";
    }

    /// <summary>Remove the companion skill's folder and its skills-metadata.json entry.</summary>
    private static bool UninstallSkill(string configDir)
    {
        bool removed = false;
        var skillsRoot = Path.Combine(configDir, "m-skills");
        var skillDir = Path.Combine(skillsRoot, SkillName);
        try { if (Directory.Exists(skillDir)) { Directory.Delete(skillDir, recursive: true); removed = true; } }
        catch { /* best effort */ }

        var registry = Path.Combine(skillsRoot, "skills-metadata.json");
        if (File.Exists(registry))
        {
            try
            {
                var arr = JsonNode.Parse(File.ReadAllText(registry)) as JsonArray;
                if (arr is not null)
                {
                    var id = "local-" + SkillName;
                    for (int i = arr.Count - 1; i >= 0; i--)
                        if (arr[i]?["id"]?.GetValue<string>() == id) { arr.RemoveAt(i); removed = true; }
                    File.WriteAllText(registry, arr.ToJsonString(WriteOpts));
                }
            }
            catch { /* best effort */ }
        }
        return removed;
    }

    /// <summary>
    /// Parse a SKILL.md (YAML frontmatter + body) and upsert it into Scout's skills-metadata.json
    /// array as { id, name, description, instructions=body, enabled, createdAt, scope=local }.
    /// </summary>
    private static void UpsertSkillRegistry(string registryPath, string skillMd)
    {
        var (name, description, body) = ParseSkillMd(skillMd);
        if (string.IsNullOrEmpty(name)) name = SkillName;

        var arr = JsonNode.Parse(File.ReadAllText(registryPath)) as JsonArray ?? new JsonArray();
        var id = "local-" + name;

        // Remove any existing entry with this id.
        for (int i = arr.Count - 1; i >= 0; i--)
            if (arr[i]?["id"]?.GetValue<string>() == id) arr.RemoveAt(i);

        arr.Add(new JsonObject
        {
            ["id"] = id,
            ["name"] = name,
            ["description"] = description,
            ["instructions"] = body,
            ["enabled"] = true,
            ["createdAt"] = "",
            ["scope"] = "local",
        });
        File.WriteAllText(registryPath, arr.ToJsonString(WriteOpts));
    }

    /// <summary>Split a SKILL.md into (name, description, body) — name/description from the YAML frontmatter.</summary>
    private static (string name, string description, string body) ParseSkillMd(string md)
    {
        md = md.Replace("\r\n", "\n");
        string name = "", description = "", body = md.Trim();
        if (md.StartsWith("---\n"))
        {
            int end = md.IndexOf("\n---", 4, StringComparison.Ordinal);
            if (end > 0)
            {
                var fm = md.Substring(4, end - 4);
                body = md[(end + 4)..].TrimStart('\n').Trim();
                name = ExtractFrontmatter(fm, "name");
                description = ExtractFrontmatter(fm, "description");
            }
        }
        return (name, description, body);
    }

    private static string ExtractFrontmatter(string fm, string key)
    {
        foreach (var line in fm.Split('\n'))
        {
            var t = line.TrimStart();
            if (t.StartsWith(key + ":", StringComparison.Ordinal))
            {
                var v = t[(key.Length + 1)..].Trim();
                if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') v = v[1..^1];
                return v;
            }
        }
        return "";
    }

    /// <summary>
    /// Discover every tool this server exposes by reflecting over [McpServerToolType]
    /// classes and their [McpServerTool] methods — so the registered tool list is always
    /// exactly what the server implements.
    /// </summary>
    private static List<string> DiscoverToolNames()
    {
        var names = new List<string>();
        foreach (var type in Assembly.GetExecutingAssembly().GetTypes())
        {
            if (type.GetCustomAttribute<McpServerToolTypeAttribute>() is null) continue;
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            {
                var attr = m.GetCustomAttribute<McpServerToolAttribute>();
                if (attr is null) continue;
                var name = attr.Name;
                if (string.IsNullOrEmpty(name))
                    name = m.Name; // fall back to method name if no explicit Name= was given
                if (!names.Contains(name)) names.Add(name);
            }
        }
        names.Sort(StringComparer.Ordinal);
        return names;
    }
}
