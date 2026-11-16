// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

namespace Office.Shared;

/// <summary>
/// The other half of <see cref="ConfigPublisher"/>: what the Levels page holds after <c>config.levels</c> has been read back into it
/// (docs/ops-first-workflow.md section 15, L4). Pure: the caller applies the result to its storeys.
/// </summary>
public static class LevelReadback
{
    /// <summary>
    /// The storeys the page should hold (without the plinth), from the page's own storeys and the configuration. A storey is matched by the
    /// <c>cid</c> it remembers, else by name; configuration levels below ground (negative elevation) are not storeys of the page.
    /// </summary>
    public static IReadOnlyList<StoreLevel> Merge(IReadOnlyList<StoreLevel> page, ProjectConfig config)
    {
        var items = (config.Levels?.Items ?? new List<ConfigLevelItem>()).Where(i => i.ElevationMm >= 0).OrderBy(i => i.ElevationMm).ThenBy(i => i.Cid).ToList();
        var defaults = config.Levels?.Defaults ?? new ConfigLevelDefaults();
        var used = new HashSet<int>();
        var result = new List<StoreLevel>();
        foreach (var item in items)
        {
            var at = IndexOf(page, used, p => p.Cid == item.Cid);
            if (at < 0) at = IndexOf(page, used, p => p.Cid is null && string.Equals(p.Name.Trim(), item.Name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (at >= 0) used.Add(at);
            var m = at >= 0 ? page[at] : null;
            result.Add(new StoreLevel(item.Name, item.HeightMm, item.SlabMm ?? m?.SlabMm ?? defaults.SlabMm ?? 150, item.BeamMm ?? m?.BeamMm ?? defaults.BeamMm ?? 450, item.Cid));
        }
        for (var i = 0; i < page.Count; i++)
            if (!used.Contains(i) && page[i].Cid is null) result.Add(page[i]);       // an unsaved addition stays
        return result;
    }

    static int IndexOf(IReadOnlyList<StoreLevel> page, HashSet<int> used, Func<StoreLevel, bool> match)
    {
        for (var i = 0; i < page.Count; i++)
            if (!used.Contains(i) && match(page[i])) return i;
        return -1;
    }

    /// <summary>True when two lists describe the same storeys (so the page need not change).</summary>
    public static bool Same(IReadOnlyList<StoreLevel> a, IReadOnlyList<StoreLevel> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.Cid == p.Second.Cid && p.First.Name == p.Second.Name
            && Math.Abs(p.First.HeightMm - p.Second.HeightMm) < 0.01 && Math.Abs(p.First.SlabMm - p.Second.SlabMm) < 0.01 && Math.Abs(p.First.BeamMm - p.Second.BeamMm) < 0.01);
}
