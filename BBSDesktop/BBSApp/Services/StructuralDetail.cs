// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Text.Json;
using Office.Shared;
using Office.Shared.SmartBlocks;

namespace BBSApp.Services;

/// <summary>
/// The structural details MynaCAD drew: reinforced concrete blocks (<c>rcc.beam.section</c>, <c>rcc.column.rect</c> and the others) placed in the
/// drawing and recorded in the project file when MynaCAD saves it. MynaOps reads them and draws each as the detail of its beam, column, slab, footing or
/// lintel. It never writes them and never draws a detail of its own: a drawing is changed in MynaCAD only.
/// </summary>
public static class StructuralDetail
{
    /// <summary>The element pages that show details from MynaCAD.</summary>
    public static bool Supports(string kind) => kind is "beams" or "columns" or "slabs" or "footings" or "lintels";

    /// <summary>The details of one kind recorded in the project file (none for no file, a file that is not a project, or one being written).</summary>
    public static IReadOnlyList<OdpDetail> Load(string? path, string kind)
    {
        if (!ProjectFiles.IsOdp(path) || !File.Exists(path))
            return [];
        try
        {
            using var odp = OdpFile.Open(path);
            return odp.Details(kind);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
        {
            return [];
        }
    }

    /// <summary>The drawing of a recorded detail: its block evaluated with the parameters MynaCAD placed it with, or null when the block or the values are not usable.</summary>
    public static SmartGeometry? Geometry(OdpDetail detail)
    {
        if (SmartBlockLibrary.Find(detail.Block) is not { } block)
            return null;
        try
        {
            var values = Parameters(detail.ParamsJson);
            // A slab is drawn along its span: the whole of a 3 to 6 m span is a hairline beside its thickness, so a 1.2 m strip of it is shown.
            if (detail.Block == "rcc.slab.section" && values.TryGetValue("span", out var span) && span > 1200)
                values["span"] = 1200;
            return block.Evaluate(values);
        }
        catch (Exception ex) when (ex is SmartBlockException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The numbers of a parameter object (<c>{"width":230,...}</c>); anything else in it is ignored.</summary>
    public static Dictionary<string, double> Parameters(string json)
    {
        var values = new Dictionary<string, double>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.Number)
                    values[p.Name] = p.Value.GetDouble();
            }
        }
        return values;
    }
}
