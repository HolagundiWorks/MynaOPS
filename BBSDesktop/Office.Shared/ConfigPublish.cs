// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

namespace Office.Shared;

/// <summary>A storey as MynaOps' Levels page holds it (the plinth, <c>Lvl0</c>, is AQC's own and is left out by the caller).</summary>
public sealed record StoreLevel(string Name, double HeightMm, double SlabMm, double BeamMm);

/// <summary>The project details MynaOps' Settings page holds.</summary>
public sealed record StoreDetails(string Name, string Client, string Location, string Address, string Summary, string Persona);

/// <summary>
/// Keeps the project configuration in step with what the Settings and Levels pages edit (package A moves those pages onto <c>config.*</c>): on Save,
/// the page values are merged into the configuration read from the file and written back in one revision (nothing is written when nothing differs).
/// Levels keep their <c>cid</c> by name; a new level gets the next <c>cid</c>; a level the page dropped leaves the configuration unless it is a basement
/// (the page does not model those). The unit, layer sets, tabs, sheet numbering and everything MynaOps does not edit stay as they are.
/// </summary>
public static class ConfigPublisher
{
    /// <summary>The merged configuration (not written).</summary>
    public static ProjectConfig Merge(ProjectConfig current, StoreDetails details, IReadOnlyList<StoreLevel> levels)
    {
        var p = current.Project ?? new ConfigProject();
        var project = p with
        {
            Title = string.IsNullOrWhiteSpace(details.Name) ? p.Title : details.Name.Trim(),
            Client = details.Client, Location = details.Location, Address = details.Address, Summary = details.Summary,
            Persona = string.IsNullOrWhiteSpace(details.Persona) ? p.Persona : details.Persona,
        };

        var cur = current.Levels ?? new ConfigLevels();
        var byName = new Dictionary<string, ConfigLevelItem>();
        foreach (var i in cur.Items) byName.TryAdd(i.Name.Trim().ToLowerInvariant(), i);
        var storeNames = new HashSet<string>(levels.Select(l => l.Name.Trim().ToLowerInvariant()));

        var result = cur.Items.Where(i => i.ElevationMm < 0 && !storeNames.Contains(i.Name.Trim().ToLowerInvariant())).OrderBy(i => i.ElevationMm).ToList();
        var next = cur.NextCid;
        double shift = 0;
        double? top = null;
        foreach (var s in levels)
        {
            ConfigLevelItem item;
            if (byName.TryGetValue(s.Name.Trim().ToLowerInvariant(), out var m))
            {
                item = m with { Name = s.Name.Trim(), HeightMm = s.HeightMm, SlabMm = s.SlabMm, BeamMm = s.BeamMm, ElevationMm = m.ElevationMm + shift };
                shift += s.HeightMm - m.HeightMm;          // a taller (shorter) level pushes the ones above it up (down)
            }
            else
            {
                item = new ConfigLevelItem { Cid = next++, Name = s.Name.Trim(), Code = "", ElevationMm = top ?? 0, HeightMm = s.HeightMm, SlabMm = s.SlabMm, BeamMm = s.BeamMm };
                shift += s.HeightMm;                        // a level inserted below pushes the ones above it up
            }
            result.Add(item);
            top = item.ElevationMm + item.HeightMm;
        }
        var maxCid = result.Count == 0 ? 0u : result.Max(i => i.Cid);
        var levelsCfg = cur with { NextCid = Math.Max(next, result.Count == 0 ? cur.NextCid : maxCid + 1), Items = result };
        return current with { Project = project, Levels = levelsCfg };
    }

    /// <summary>
    /// Merge and write. Returns <c>null</c> when the file has no configuration (an unadopted or legacy file: the Adopt page comes first). Throws
    /// <see cref="ConfigException"/> when the merged values are not valid (two levels with one name, a height out of range).
    /// </summary>
    public static ConfigWritten? Publish(OdpFile odp, StoreDetails details, IReadOnlyList<StoreLevel> levels)
    {
        var loaded = ProjectConfig.Read(odp);
        if (!loaded.Configured) return null;
        var merged = Merge(loaded.Read.Config, details, levels);
        return ProjectConfig.Write(odp, merged);
    }
}
