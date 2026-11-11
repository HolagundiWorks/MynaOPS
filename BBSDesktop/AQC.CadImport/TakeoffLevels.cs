// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Text.Json.Nodes;

namespace Aqc.CadImport;

/// <summary>Measured quantities of one level of a take-off. <c>Id</c> is MynaCAD's stable level id when the file carries it.</summary>
public sealed record LevelQuantities(
    int? Id, string Name, double? ElevationMm, double? HeightMm,
    int Walls, double WallLengthMm, double WallAreaM2,
    int Openings, int Columns, int Lintels,
    int Rooms, double RoomAreaM2,
    int Slabs, double SlabAreaM2)
{
    /// <summary>Electrical and plumbing rows on the level.</summary>
    public int Services { get; init; }
}

/// <summary>
/// Groups and filters a take-off by level. A row belongs to the level named by its <c>level_id</c> when the file's
/// <c>levels</c> list has that id, else to the level of the same name (case-insensitive), else to a level made from
/// the name alone. A row with no level belongs to the file's only level when there is one, else to "" (no level).
/// A file without ids (older MynaCAD) groups by name exactly as before.
/// </summary>
public static class TakeoffLevels
{
    /// <summary>The level name a row belongs to ("" when it cannot be told).</summary>
    public static string LevelNameOf(BridgeFile file, JsonObject row)
    {
        if (BridgeFile.LevelIdOf(row) is { } id && file.Levels.FirstOrDefault(l => l.Id == id) is { Name.Length: > 0 } byId)
            return byId.Name;
        var n = BridgeFile.Str(row, "level");
        if (n.Length == 0) return file.Levels.Count == 1 ? file.Levels[0].Name : "";
        return file.Levels.FirstOrDefault(l => l.Name.Equals(n, StringComparison.OrdinalIgnoreCase))?.Name ?? n;
    }

    static IEnumerable<(string Section, List<JsonObject> Rows)> Sections(BridgeFile f)
    {
        yield return ("walls", f.Walls); yield return ("openings", f.Openings); yield return ("columns", f.Columns);
        yield return ("lintels", f.Lintels); yield return ("rooms", f.Rooms); yield return ("slabs", f.Slabs);
        yield return ("electrical", f.Electrical); yield return ("electrical_circuits", f.ElectricalCircuits);
        yield return ("plumbing", f.Plumbing); yield return ("plumbing_fixtures", f.PlumbingFixtures);
    }

    /// <summary>Quantities per level: the file's levels in their order first (even when empty), then any level only the rows name.</summary>
    public static IReadOnlyList<LevelQuantities> Summarise(BridgeFile file)
    {
        var order = new List<string>();
        var info = new Dictionary<string, BridgeLevel?>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in file.Levels)
            if (l.Name.Length > 0 && info.TryAdd(l.Name, l)) order.Add(l.Name);
        var acc = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        // 0 walls, 1 wall length, 2 wall area, 3 openings, 4 columns, 5 lintels, 6 rooms, 7 room area, 8 slabs, 9 slab area, 10 services
        foreach (var (section, rows) in Sections(file))
            foreach (var row in rows)
            {
                var lvl = LevelNameOf(file, row);
                if (info.TryAdd(lvl, null)) order.Add(lvl);
                if (!acc.TryGetValue(lvl, out var a)) acc[lvl] = a = new double[11];
                switch (section)
                {
                    case "walls":
                        var len = Math.Max(0, BridgeFile.Num(row, "length_mm") ?? 0);
                        a[0]++; a[1] += len; a[2] += len * Math.Max(0, BridgeFile.Num(row, "height_mm") ?? 0) / 1e6;
                        break;
                    case "openings": a[3]++; break;
                    case "columns": a[4]++; break;
                    case "lintels": a[5]++; break;
                    case "rooms": a[6]++; a[7] += Math.Max(0, BridgeFile.Num(row, "area_m2") ?? 0); break;
                    case "slabs": a[8]++; a[9] += Math.Max(0, BridgeFile.Num(row, "area_m2") ?? 0); break;
                    default: a[10]++; break;
                }
            }
        var list = new List<LevelQuantities>();
        foreach (var name in order)
        {
            var l = info[name];
            var a = acc.GetValueOrDefault(name) ?? new double[11];
            list.Add(new LevelQuantities(l?.Id, name, l?.ElevationMm, l?.HeightMm,
                (int)a[0], Math.Round(a[1], 1), Math.Round(a[2], 3), (int)a[3], (int)a[4], (int)a[5],
                (int)a[6], Math.Round(a[7], 3), (int)a[8], Math.Round(a[9], 3)) { Services = (int)a[10] });
        }
        return list;
    }

    /// <summary>
    /// A copy of the file holding only the rows of the named levels (case-insensitive) and only those levels in its
    /// <c>levels</c> list. An empty or null selection keeps everything. Rows are shared with the source, not copied.
    /// </summary>
    public static BridgeFile Filter(BridgeFile file, IEnumerable<string>? levelNames)
    {
        var keep = levelNames?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (keep is null || keep.Count == 0) return file;
        List<JsonObject> Pick(List<JsonObject> rows) => rows.Where(r => keep.Contains(LevelNameOf(file, r))).ToList();
        return new BridgeFile
        {
            DrawingId = file.DrawingId, Drawing = file.Drawing, ProjectRefCompany = file.ProjectRefCompany, ProjectRefId = file.ProjectRefId,
            Project = file.Project,
            Levels = file.Levels.Where(l => keep.Contains(l.Name)).ToList(),
            Walls = Pick(file.Walls), Openings = Pick(file.Openings), OpeningSchedule = Pick(file.OpeningSchedule),
            Columns = Pick(file.Columns), Lintels = Pick(file.Lintels), Rooms = Pick(file.Rooms), Slabs = Pick(file.Slabs),
            Electrical = Pick(file.Electrical), ElectricalCircuits = Pick(file.ElectricalCircuits),
            Plumbing = Pick(file.Plumbing), PlumbingFixtures = Pick(file.PlumbingFixtures),
        };
    }
}
