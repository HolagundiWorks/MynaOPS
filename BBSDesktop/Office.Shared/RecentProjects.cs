// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Text.Json.Nodes;

namespace Office.Shared;

/// <summary>The recent projects MynaOps lists on the Project home (<c>%LOCALAPPDATA%\O-Studio\recent-projects.json</c>, newest first, at most 8).</summary>
public sealed class RecentProjects
{
    public const int Max = 8;

    readonly string _file;

    public RecentProjects(string? file = null) => _file = file ?? SuiteWire.LocalPath("recent-projects.json");

    public IReadOnlyList<string> Read()
    {
        try
        {
            if (!File.Exists(_file) || new FileInfo(_file).Length > 64 * 1024) return Array.Empty<string>();
            if (JsonNode.Parse(File.ReadAllText(_file)) is not JsonObject o || o["projects"] is not JsonArray a) return Array.Empty<string>();
            var list = new List<string>();
            foreach (var n in a)
            {
                var s = n is JsonValue v && v.TryGetValue<string>(out var t) ? t : null;
                if (!string.IsNullOrWhiteSpace(s) && Path.IsPathRooted(s) && !list.Contains(s, StringComparer.OrdinalIgnoreCase)) list.Add(s);
                if (list.Count == Max) break;
            }
            return list;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException) { return Array.Empty<string>(); }
    }

    /// <summary>Put <paramref name="path"/> first. Failing to write the list never fails the open.</summary>
    public void Add(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var list = Read().Where(p => !string.Equals(p, full, StringComparison.OrdinalIgnoreCase)).Prepend(full).Take(Max).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, new JsonObject { ["format"] = "myna-recent-projects", ["version"] = 1, ["projects"] = new JsonArray(list.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray()) }.ToJsonString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    public void Remove(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var list = Read().Where(p => !string.Equals(p, full, StringComparison.OrdinalIgnoreCase)).ToList();
            File.WriteAllText(_file, new JsonObject { ["format"] = "myna-recent-projects", ["version"] = 1, ["projects"] = new JsonArray(list.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray()) }.ToJsonString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }
}
