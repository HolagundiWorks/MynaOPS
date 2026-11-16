// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using Office.Shared;
using Xunit;

namespace Office.Shared.Tests;

[Collection("ops-first")]
public sealed class ConfigPublishTests : OpsFirstTestBase
{
    NewProjectRequest Req() => new()
    {
        Folder = Dir, FileName = "P", Basements = 1, UpperFloors = 2, Terrace = false, FloorHeightMm = 3000,
        Project = new ConfigProject { Title = "P", Persona = "Project Manager" },
    };

    static StoreDetails Details(string name = "P") => new(name, "Client", "Hospet", "Addr", "Sum", "Contractor");

    [Fact]
    public void Details_and_levels_are_published_in_one_revision_and_cids_stay()
    {
        var made = NewProjectBuilder.Create(Req());       // Basement 1, Ground, First, Second
        var levels = new[]
        {
            new StoreLevel("Ground Floor", 3000, 150, 450),
            new StoreLevel("First Floor", 3300, 200, 500),    // taller, thicker
            new StoreLevel("Second Floor", 3000, 150, 450),
            new StoreLevel("Third Floor", 3000, 150, 450),    // new
        };
        using var odp = OdpFile.Open(made.Path);
        var w = ConfigPublisher.Publish(odp, Details("Renamed"), levels)!;
        Assert.Equal(2UL, w.Rev);
        Assert.Equal(2, odp.Revision());
        var c = ProjectConfig.Read(odp).Read.Config;
        Assert.Equal("Renamed", c.Project!.Title);
        Assert.Equal("Contractor", c.Project.Persona);
        Assert.Equal("Hospet", c.Project.Location);
        var items = c.Levels!.Items;
        Assert.Equal(new[] { "Basement 1", "Ground Floor", "First Floor", "Second Floor", "Third Floor" }, items.Select(i => i.Name).ToArray());
        Assert.Equal(new uint[] { 1, 2, 3, 4, 5 }, items.Select(i => i.Cid).ToArray());      // the old cids stay; the new level gets the next
        Assert.Equal(6u, c.Levels.NextCid);
        Assert.Equal(200, items.Single(i => i.Name == "First Floor").SlabMm);
        // First is 300 mm taller: Second and Third sit 300 higher; Ground and the basement did not move
        Assert.Equal(new double[] { -3000, 0, 3000, 6300, 9300 }, items.Select(i => i.ElevationMm).ToArray());
        Assert.Equal("mm", c.Units!.DrawingUnit);                                            // what the pages do not edit is left alone
        Assert.Equal(5, c.Tabs!.Items.Count);
    }

    [Fact]
    public void A_renamed_level_keeps_its_cid_and_a_new_level_gets_the_next()
    {
        var made = NewProjectBuilder.Create(Req());       // Basement 1 (cid 1), Ground (2), First (3), Second (4)
        using var odp = OdpFile.Open(made.Path);
        var cur = ProjectConfig.Read(odp).Read.Config.Levels!.Items.Where(i => i.ElevationMm >= 0).ToList();
        var levels = new List<StoreLevel>();
        foreach (var i in cur)
            levels.Add(new StoreLevel(i.Name == "First Floor" ? "Level 1" : i.Name, i.HeightMm, i.SlabMm ?? 150, i.BeamMm ?? 450, i.Cid));
        levels.Insert(1, new StoreLevel("Mezzanine", 2500, 150, 450));        // inserted above Ground: no cid yet
        ConfigPublisher.Publish(odp, Details("P"), levels);
        var items = ProjectConfig.Read(odp).Read.Config.Levels!.Items;
        Assert.Equal(cur.Single(i => i.Name == "First Floor").Cid, items.Single(i => i.Name == "Level 1").Cid);
        Assert.DoesNotContain(items, i => i.Name == "First Floor");
        Assert.Equal(cur.Count + 1 + 1, items.Count);                                        // + the basement the page does not model
        Assert.True(items.Single(i => i.Name == "Mezzanine").Cid > cur.Max(i => i.Cid));
        // a document saved before cids were remembered still matches by name
        var byName = items.Where(i => i.ElevationMm >= 0).Select(i => new StoreLevel(i.Name, i.HeightMm, i.SlabMm ?? 150, i.BeamMm ?? 450)).ToList();
        Assert.Empty(ConfigPublisher.Publish(odp, Details("P"), byName)!.Changed);
    }

    [Fact]
    public void Publishing_what_is_there_changes_nothing()
    {
        var made = NewProjectBuilder.Create(Req());
        using var odp = OdpFile.Open(made.Path);
        var same = ProjectConfig.Read(odp).Read.Config.Levels!.Items.Where(i => i.ElevationMm >= 0)
            .Select(i => new StoreLevel(i.Name, i.HeightMm, i.SlabMm ?? 150, i.BeamMm ?? 450)).ToList();
        var first = ConfigPublisher.Publish(odp, Details("P"), same)!;                   // sets client, location etc. (they were empty)
        Assert.True(first.Changed.Count > 0);
        var again = ConfigPublisher.Publish(odp, Details("P"), same)!;
        Assert.Empty(again.Changed);
        Assert.Equal(first.Revision, odp.Revision());
    }

    [Fact]
    public void A_level_dropped_on_the_page_leaves_the_configuration_but_a_basement_stays_and_cids_are_not_reused()
    {
        var made = NewProjectBuilder.Create(Req());
        using var odp = OdpFile.Open(made.Path);
        ConfigPublisher.Publish(odp, Details(), new[] { new StoreLevel("Ground Floor", 3000, 150, 450) });
        var c = ProjectConfig.Read(odp).Read.Config.Levels!;
        Assert.Equal(new[] { "Basement 1", "Ground Floor" }, c.Items.Select(i => i.Name).ToArray());
        Assert.Equal(5u, c.NextCid);                                                      // 3 and 4 are never given out again
        ConfigPublisher.Publish(odp, Details(), new[] { new StoreLevel("Ground Floor", 3000, 150, 450), new StoreLevel("Annexe", 3000, 150, 450) });
        Assert.Equal(5u, ProjectConfig.Read(odp).Read.Config.Levels!.Items.Single(i => i.Name == "Annexe").Cid);
    }

    [Fact]
    public void A_file_without_configuration_is_not_written()
    {
        var p = Project("Legacy.osp");
        using var odp = OdpFile.Open(p);
        var rev = odp.Revision();
        Assert.Null(ConfigPublisher.Publish(odp, Details(), new[] { new StoreLevel("A", 3000, 150, 450) }));
        Assert.Equal(rev, odp.Revision());
        Assert.Null(odp.MetaText("config.schema"));
    }

    [Fact]
    public void Invalid_values_are_refused_and_nothing_is_written()
    {
        var made = NewProjectBuilder.Create(Req());
        using var odp = OdpFile.Open(made.Path);
        var rev = odp.Revision();
        Assert.Throws<ConfigException>(() => ConfigPublisher.Publish(odp, Details(), new[] { new StoreLevel("A", 3000, 150, 450), new StoreLevel("a", 3000, 150, 450) }));
        Assert.Throws<ConfigException>(() => ConfigPublisher.Publish(odp, Details(), new[] { new StoreLevel("A", 3000, 9000, 450) }));    // slab out of range
        Assert.Equal(rev, odp.Revision());
    }
}
