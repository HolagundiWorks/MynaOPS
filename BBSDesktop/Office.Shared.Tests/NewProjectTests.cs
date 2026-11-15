// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using Office.Shared;
using Xunit;

namespace Office.Shared.Tests;

[Collection("ops-first")]
public sealed class NewProjectTests : OpsFirstTestBase
{
    NewProjectRequest Request(string name = "Hospet House") => new()
    {
        Folder = Dir,
        FileName = name,
        Project = new ConfigProject { Title = name, Client = "R. Rao", Location = "Hospet", Address = "12 Station Road", Pid = "HP-1", Persona = "Contractor" },
        Team = new ConfigTeam { Architect = "A. Kulkarni", Engineer = "S. Naik" },
        DrawingUnit = "mm",
        Basements = 1,
        UpperFloors = 2,
        Terrace = true,
        FloorHeightMm = 3000,
        LayerSets = new[] { "standard", "permit" },
    };

    [Fact]
    public void Create_makes_a_valid_project_file_with_the_configuration_in_one_revision()
    {
        var r = Request();
        var made = NewProjectBuilder.Create(r);
        Assert.Equal(Path.Combine(Dir, "Hospet House.osp"), made.Path);
        Assert.True(OdpFile.LooksLikeOdp(made.Path));
        Assert.Equal(1UL, made.Written.Rev);
        Assert.Equal(1, made.Written.Revision);                           // OdpFile.Create is revision 0; the configuration is one revision

        using var odp = OdpFile.Open(made.Path);
        var meta = odp.Meta();
        Assert.Equal("Hospet House", meta.Name);
        Assert.Equal(1, meta.Revision);
        var loaded = ProjectConfig.Read(odp);
        Assert.True(loaded.Configured);
        Assert.Empty(loaded.Read.Notes);
        var c = loaded.Read.Config;
        Assert.Equal("Hospet House", c.Project!.Title);
        Assert.Equal("R. Rao", c.Project.Client);
        Assert.Equal("Contractor", c.Project.Persona);
        Assert.Equal("A. Kulkarni", c.Team!.Architect);
        Assert.Equal("mm", c.Units!.DrawingUnit);
        Assert.False(c.Units.Locked);
        Assert.Equal(new[] { "standard", "permit" }, c.LayerSets!.Sets);
        Assert.Equal(5, c.Tabs!.Items.Count);
        Assert.Equal("Working drawing", c.Tabs.Items[0].Name);
        Assert.Equal(7, c.Sheets!.Scheme.Disciplines.Count);
        // sync.last names MynaOps (live-sync shows "Updated by MynaOps" to a running MynaCAD)
        Assert.Contains("\"mynaops\"", odp.MetaText("sync.last"));
    }

    [Fact]
    public void Levels_run_from_the_basement_to_the_terrace_with_the_running_elevation()
    {
        var levels = NewProjectBuilder.Build(Request()).Levels!;
        Assert.Equal(new[] { "Basement 1", "Ground Floor", "First Floor", "Second Floor", "Terrace" }, levels.Items.Select(l => l.Name).ToArray());
        Assert.Equal(new double[] { -3000, 0, 3000, 6000, 9000 }, levels.Items.Select(l => l.ElevationMm).ToArray());
        Assert.Equal(new uint[] { 1, 2, 3, 4, 5 }, levels.Items.Select(l => l.Cid).ToArray());
        Assert.Equal(6u, levels.NextCid);
        Assert.Equal(3000, levels.Defaults.HeightMm);
        Assert.True(levels.OnePerFloor);
    }

    [Fact]
    public void Without_one_level_per_floor_there_is_the_ground_floor_only()
    {
        var r = Request();
        r.OneLevelPerFloor = false;
        var levels = NewProjectBuilder.Build(r).Levels!;
        Assert.Equal("Ground Floor", Assert.Single(levels.Items).Name);
        Assert.False(levels.OnePerFloor);
    }

    [Fact]
    public void An_edited_level_list_is_used_as_it_is()
    {
        var r = Request();
        r.Levels = new[] { new LevelSpec("Plinth", "P", 0, 600), new LevelSpec("Stilt", "S", 600, 2700) };
        var c = NewProjectBuilder.Build(r);
        Assert.Equal(new[] { "Plinth", "Stilt" }, c.Levels!.Items.Select(l => l.Name).ToArray());
        Assert.Equal(600, c.Levels.Items[1].ElevationMm);
    }

    [Fact]
    public void Problems_are_found_before_anything_is_written()
    {
        var r = Request();
        r.Project = new ConfigProject();
        Assert.Contains("Give the project a name.", NewProjectBuilder.Problems(r));

        r = Request(); r.FileName = "a:b";
        Assert.NotEmpty(NewProjectBuilder.Problems(r));
        r = Request(); r.Folder = Path.Combine(Dir, "nope");
        Assert.Contains("The folder does not exist.", NewProjectBuilder.Problems(r));
        r = Request(); r.DrawingUnit = "furlong";
        Assert.NotEmpty(NewProjectBuilder.Problems(r));
        r = Request(); r.UpperFloors = 500;
        Assert.NotEmpty(NewProjectBuilder.Problems(r));
        r = Request(); r.Levels = new[] { new LevelSpec("A", "", 0, 3000), new LevelSpec("a", "", 3000, 3000) };   // names are compared without case
        Assert.Contains(NewProjectBuilder.Problems(r), s => s.Contains("used twice"));
        r = Request(); r.Levels = new[] { new LevelSpec("A", "", double.NaN, 3000) };
        Assert.NotEmpty(NewProjectBuilder.Problems(r));
        Assert.Empty(Directory.GetFiles(Dir, "*.osp"));
    }

    [Fact]
    public void A_project_that_exists_is_never_overwritten()
    {
        NewProjectBuilder.Create(Request());
        var again = NewProjectBuilder.Problems(Request());
        Assert.Contains(again, s => s.Contains("already exists"));
        Assert.Throws<InvalidOperationException>(() => NewProjectBuilder.Create(Request()));
    }

    [Fact]
    public void The_extension_is_added_once()
    {
        var r = Request("Plan.osp");
        Assert.Equal(Path.Combine(Dir, "Plan.osp"), NewProjectBuilder.PathOf(r));
        r.FileName = "Plan";
        Assert.Equal(Path.Combine(Dir, "Plan.osp"), NewProjectBuilder.PathOf(r));
    }

    [Fact]
    public void Writing_the_same_configuration_again_makes_no_revision()
    {
        var made = NewProjectBuilder.Create(Request());
        using var odp = OdpFile.Open(made.Path);
        var again = ProjectConfig.Write(odp, NewProjectBuilder.Build(Request()));
        Assert.Empty(again.Changed);
        Assert.Equal(1, odp.Revision());
    }
}

[Collection("ops-first")]
public sealed class RecentProjectsTests : OpsFirstTestBase
{
    [Fact]
    public void Newest_first_without_duplicates_and_capped()
    {
        var r = new RecentProjects(Path.Combine(Dir, "recent.json"));
        Assert.Empty(r.Read());
        for (var i = 0; i < 12; i++) r.Add(Path.Combine(Dir, $"p{i}.osp"));
        r.Add(Path.Combine(Dir, "P5.OSP"));
        var list = r.Read();
        Assert.Equal(RecentProjects.Max, list.Count);
        Assert.Equal(Path.Combine(Dir, "P5.OSP"), list[0]);
        Assert.Equal(1, list.Count(p => p.EndsWith("p5.osp", StringComparison.OrdinalIgnoreCase)));
        r.Remove(Path.Combine(Dir, "p11.osp"));
        Assert.DoesNotContain(r.Read(), p => p.EndsWith("p11.osp"));
    }

    [Fact]
    public void A_damaged_list_reads_as_empty()
    {
        var f = Path.Combine(Dir, "recent.json");
        File.WriteAllText(f, "{ nope");
        Assert.Empty(new RecentProjects(f).Read());
        File.WriteAllText(f, "{\"projects\":[1,null,\"relative.osp\"]}");
        Assert.Empty(new RecentProjects(f).Read());
    }
}
