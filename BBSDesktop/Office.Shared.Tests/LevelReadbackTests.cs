// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using Xunit;

namespace Office.Shared.Tests;

public sealed class LevelReadbackTests
{
    static ConfigLevelItem L(uint cid, string name, double elev, double h, double? slab = null) =>
        new() { Cid = cid, Name = name, ElevationMm = elev, HeightMm = h, SlabMm = slab };

    static ProjectConfig Cfg(params ConfigLevelItem[] items) =>
        new() { Levels = new ConfigLevels { NextCid = 20, Items = items.ToList() } };

    [Fact]
    public void A_rename_a_height_and_a_new_level_written_elsewhere_come_into_the_page_in_elevation_order()
    {
        var page = new List<StoreLevel> { new("Ground", 3000, 150, 450, 1), new("First", 3000, 150, 450, 2) };
        var cfg = Cfg(L(1, "Ground", 0, 3000), L(3, "Mezzanine", 3000, 2500), L(2, "Level 1", 5500, 3300, 200));
        var merged = LevelReadback.Merge(page, cfg);
        Assert.Equal(new[] { "Ground", "Mezzanine", "Level 1" }, merged.Select(m => m.Name).ToArray());
        Assert.Equal(new uint?[] { 1, 3, 2 }, merged.Select(m => m.Cid).ToArray());
        Assert.Equal(3300, merged[2].HeightMm);
        Assert.Equal(200, merged[2].SlabMm);
        Assert.Equal(450, merged[0].BeamMm);                      // a value the configuration does not carry keeps the page's
    }

    [Fact]
    public void A_storey_without_a_remembered_cid_matches_by_name_and_a_deleted_one_leaves()
    {
        var page = new List<StoreLevel> { new("ground", 3000, 150, 450), new("Gone", 3000, 150, 450, 9), new("Typing", 3000, 150, 450) };
        var merged = LevelReadback.Merge(page, Cfg(L(1, "Ground", 0, 3200)));
        Assert.Equal(new[] { "Ground", "Typing" }, merged.Select(m => m.Name).ToArray());       // "Gone" (cid 9) is no longer configured; "Typing" is an unsaved addition
        Assert.Equal(1u, merged[0].Cid);
        Assert.Equal(3200, merged[0].HeightMm);
        Assert.Null(merged[1].Cid);
    }

    [Fact]
    public void Basements_stay_out_and_merging_what_is_there_changes_nothing()
    {
        var cfg = Cfg(L(1, "Basement", -3000, 3000), L(2, "Ground", 0, 3000), L(3, "First", 3000, 3000));
        var first = LevelReadback.Merge(new List<StoreLevel>(), cfg);
        Assert.Equal(new[] { "Ground", "First" }, first.Select(m => m.Name).ToArray());
        Assert.True(LevelReadback.Same(first, LevelReadback.Merge(first, cfg)));
    }
}
