// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Text.Json.Nodes;

namespace BBSApp.Services;

/// <summary>Project maps and the layouts composed on them. Stored in the project document, not in the drawing.</summary>
public sealed class SheetLayoutBook
{
    public const int MaxMaps = 32;
    public const int MaxLayouts = 64;
    public List<ProjectMapSheet> Maps { get; } = new();

    public void Clear() => Maps.Clear();

    public JsonObject ToJson()
    {
        var maps = new JsonArray();
        foreach (var map in Maps)
        {
            var layouts = new JsonArray();
            foreach (var layout in map.Layouts)
            {
                layouts.Add(new JsonObject
                {
                    ["id"] = layout.Id,
                    ["name"] = layout.Name,
                    ["print_block"] = layout.PrintBlock,
                    ["print_description"] = layout.PrintDescription,
                    ["print_drawing"] = layout.PrintDrawing,
                    ["number"] = layout.Number,
                    ["scale"] = layout.Scale,
                    ["drawn_by"] = layout.DrawnBy,
                    ["date"] = layout.Date,
                    ["title"] = layout.Title,
                });
            }
            maps.Add(new JsonObject
            {
                ["id"] = map.Id,
                ["name"] = map.Name,
                ["layouts"] = layouts,
            });
        }
        return new JsonObject { ["maps"] = maps };
    }

    public void LoadFrom(JsonObject? root)
    {
        Maps.Clear();
        if (root?["maps"] is not JsonArray maps) return;
        foreach (var item in maps)
        {
            if (Maps.Count >= MaxMaps) break;
            if (item is not JsonObject o) continue;
            var map = new ProjectMapSheet
            {
                Id = Clip(Text(o["id"]), 40, NewId()),
                Name = Clip(Text(o["name"]), 80, "Project map"),
            };
            if (o["layouts"] is JsonArray layouts)
            {
                foreach (var layoutItem in layouts)
                {
                    if (map.Layouts.Count >= MaxLayouts) break;
                    if (layoutItem is not JsonObject lo) continue;
                    map.Layouts.Add(new PlacedLayout
                    {
                        Id = Clip(Text(lo["id"]), 40, NewId()),
                        Name = Clip(Text(lo["name"]), 80, "Layout"),
                        PrintBlock = Clip(Text(lo["print_block"]), 80, ""),
                        PrintDescription = Clip(Text(lo["print_description"]), 200, ""),
                        PrintDrawing = Clip(Text(lo["print_drawing"]), 120, ""),
                        Number = Clip(Text(lo["number"]), 40, ""),
                        Scale = Clip(Text(lo["scale"]), 40, ""),
                        DrawnBy = Clip(Text(lo["drawn_by"]), 80, ""),
                        Date = Clip(Text(lo["date"]), 40, ""),
                        Title = Clip(Text(lo["title"]), 120, ""),
                    });
                }
            }
            Maps.Add(map);
        }
    }

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    static string Clip(string? value, int max, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}

public sealed class ProjectMapSheet
{
    public string Id { get; set; } = SheetLayoutBook.NewId();
    public string Name { get; set; } = "";
    public List<PlacedLayout> Layouts { get; } = new();
}

/// <summary>One sheet on a project map: a title, the print block placed on it, and the nameplate.</summary>
public sealed class PlacedLayout
{
    public string Id { get; set; } = SheetLayoutBook.NewId();
    public string Name { get; set; } = "";
    public string PrintBlock { get; set; } = "";
    public string PrintDescription { get; set; } = "";
    public string PrintDrawing { get; set; } = "";
    public string Number { get; set; } = "";
    public string Scale { get; set; } = "";
    public string DrawnBy { get; set; } = "";
    public string Date { get; set; } = "";
    public string Title { get; set; } = "";
}
