// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Office.Shared;
using Xunit;

namespace Office.Shared.Tests;

[Collection("ops-first")]
public sealed class ProjectAdoptionTests : OpsFirstTestBase
{
    const string Proposal = """
        {"v":1,"time":"2026-11-15T09:00:00Z","has_content":true,"drawing_unit":"m",
         "levels":[{"cid":3,"name":"Ground","elevation_mm":0,"height_mm":3200},{"cid":4,"name":"First","elevation_mm":3200,"height_mm":3000}],
         "tabs":[{"tid":7,"name":"Working drawing","layer_prefix":"W-","layers":["A-*"],"blocks":["DOOR*"]}],
         "layer_sets":["standard"],
         "project":{"title":"Hospet House","client":"R. Rao","architect":"A. Kulkarni"}}
        """;

    const string Header = """
        {"format":"hcw-takeoff-shilpi","version":1,"levels":[
          {"name":"Ground","id":3,"elevation_mm":0,"height_mm":3200,"slab_thickness_mm":160},
          {"name":"Roof","id":5,"elevation_mm":6200,"height_mm":1000}]}
        """;

    const string Doc = """
        {"name":"Hospet House","project":{"name":"Hospet House","location":"Hospet","client_name":"R. Rao"},
         "parties":{"active":"contractor"},
         "levels":[{"id":"Lvl0","name":"Plinth","height_mm":3200},
                   {"id":"Lvl1","name":"ground","height_mm":3000,"slab_thickness_mm":150,"beam_depth_mm":450},
                   {"id":"Lvl2","name":"Second floor","height_mm":3000}]}
        """;

    /// <summary>A project file as MynaCAD leaves it: a drawing, take-off rows, a header with levels, a proposal. No configuration.</summary>
    string Legacy(bool withProposal = true, bool withDoc = true, bool withHeader = true)
    {
        var path = Project("Legacy.osp", drawing: "AC1027 the drawing bytes");
        using (var odp = OdpFile.Open(path))
        {
            if (withDoc) odp.SetProjectDoc(Doc);
        }
        if (withHeader) RawSql(path, "INSERT INTO takeoff_header (slot, state, json) VALUES (0, 'recorded', $j)", ("$j", Header));
        if (withProposal) RawMeta(path, "config.proposal", Proposal);
        return path;
    }

    static string DrawingFingerprint(string path)
    {
        // every column of every drawing row, hashed: the drawing is what adopting must not touch
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT name, ext, sha256, bytes, revision, created FROM drawing ORDER BY name";
        using var r = cmd.ExecuteReader();
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (r.Read())
        {
            h.AppendData(System.Text.Encoding.UTF8.GetBytes($"{r.GetString(0)}|{r.GetString(1)}|{r.GetString(2)}|{r.GetInt64(4)}|{r.GetString(5)}|"));
            h.AppendData((byte[])r["bytes"]);
        }
        return Convert.ToHexString(h.GetHashAndReset());
    }

    [Fact]
    public void A_legacy_file_needs_adoption_and_a_configured_one_does_not()
    {
        var p = Legacy();
        Assert.True(ProjectAdoption.NeedsAdoption(p));
        ProjectAdoption.Adopt(p, ProjectAdoption.Propose(p).Config);
        Assert.False(ProjectAdoption.NeedsAdoption(p));
    }

    [Fact]
    public void Propose_writes_nothing()
    {
        var p = Legacy();
        long rev; using (var odp = OdpFile.Open(p)) rev = odp.Revision();
        var before = File.ReadAllBytes(p);
        _ = ProjectAdoption.Propose(p);
        using var after = OdpFile.Open(p);
        Assert.Equal(rev, after.Revision());
        Assert.Null(after.MetaText("config.schema"));
    }

    [Fact]
    public void The_proposal_merges_cad_header_and_project_levels_by_name_and_keeps_the_cad_ids()
    {
        var p = Legacy();
        var prop = ProjectAdoption.Propose(p);
        var items = prop.Config.Levels!.Items;
        // Ground is in the CAD proposal (id 3), the header (id 3) and the project document ("ground", no id): one entry, cid = LevelId.
        Assert.Single(items, l => string.Equals(l.Name, "Ground", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3u, items.Single(l => l.Name == "Ground").Cid);
        Assert.Equal(4u, items.Single(l => l.Name == "First").Cid);
        Assert.Equal(5u, items.Single(l => l.Name == "Roof").Cid);                 // only in the take-off header
        var second = items.Single(l => l.Name == "Second floor");                  // only in the project: gets a cid nobody uses
        Assert.DoesNotContain(items.Where(l => l != second), l => l.Cid == second.Cid);
        Assert.Equal(6u, second.Cid);
        Assert.Equal(7u, prop.Config.Levels.NextCid);                               // above every cid, never reused
        Assert.DoesNotContain(items, l => l.Name == "Plinth");                      // Lvl0 is AQC's own
        Assert.Contains("MynaCAD's proposal", prop.Sources);
        Assert.True(prop.FromCad);
        prop.Config.Validate();
    }

    [Fact]
    public void The_proposal_takes_unit_tabs_layer_sets_and_details_from_mynacad()
    {
        var c = ProjectAdoption.Propose(Legacy()).Config;
        Assert.Equal("m", c.Units!.DrawingUnit);
        Assert.True(c.Units.Locked);                                                // the drawing has content
        Assert.Equal(new[] { "standard" }, c.LayerSets!.Sets);
        var tab = Assert.Single(c.Tabs!.Items);
        Assert.Equal(7u, tab.Tid);                                                  // the sheet set's own id
        Assert.Equal(8u, c.Tabs.NextTid);
        Assert.Equal("Hospet House", c.Project!.Title);
        Assert.Equal("R. Rao", c.Project.Client);
        Assert.Equal("A. Kulkarni", c.Team!.Architect);
        Assert.Equal("Contractor", c.Project.Persona);                              // from the project's active party
        Assert.Equal("Hospet", c.Project.Location);                                 // not in MynaCAD's proposal: the project document fills it
    }

    [Fact]
    public void A_file_only_the_project_document_knows_gets_levels_with_running_elevations()
    {
        var p = Legacy(withProposal: false, withHeader: false);
        var prop = ProjectAdoption.Propose(p);
        var items = prop.Config.Levels!.Items;
        Assert.Equal(new[] { "ground", "Second floor" }, items.Select(l => l.Name).ToArray());
        Assert.Equal(new double[] { 3200, 6200 }, items.Select(l => l.ElevationMm).ToArray());   // Lvl0 (the plinth, 3200) is below the first
        Assert.Equal(new uint[] { 1, 2 }, items.Select(l => l.Cid).ToArray());
        Assert.False(prop.FromCad);
        Assert.Contains(prop.Notes, n => n.Contains("drawing unit"));
        Assert.Equal("mm", prop.Config.Units!.DrawingUnit);
        Assert.False(prop.Config.Units.Locked);
    }

    [Fact]
    public void A_file_with_nothing_still_gets_a_valid_proposal()
    {
        var p = Project("Bare.osp");
        var prop = ProjectAdoption.Propose(p);
        Assert.Empty(prop.Config.Levels!.Items);
        Assert.Equal(new[] { "defaults" }, prop.Sources);
        Assert.Equal("Bare", prop.Config.Project!.Title);                           // the file's own name
        prop.Config.Validate();
        Assert.NotEmpty(prop.Notes);
    }

    [Fact]
    public void Hostile_header_levels_are_skipped_not_fatal()
    {
        var p = Project("Hostile.osp");
        RawSql(p, "INSERT INTO takeoff_header (slot, state, json) VALUES (0, 'x', $j)",
            ("$j", """{"levels":[{"name":"","id":1},{"name":"ok","id":-4,"elevation_mm":"NaN"},{"name":"ok2","id":2,"elevation_mm":1e999},"junk",7]}"""));
        RawSql(p, "INSERT INTO takeoff_header (slot, state, json) VALUES (1, 'x', 'not json')");
        var c = ProjectAdoption.Propose(p).Config;
        c.Validate();
        Assert.All(c.Levels!.Items, l => Assert.True(double.IsFinite(l.ElevationMm) && double.IsFinite(l.HeightMm)));
    }

    [Fact]
    public void Adopting_leaves_the_drawing_rows_unchanged_and_makes_a_manual_backup_first()
    {
        var p = Legacy();
        var before = DrawingFingerprint(p);
        string sha; using (var odp = OdpFile.Open(p)) sha = odp.Drawing("a.dxf")!.Sha256;
        long rev; using (var odp = OdpFile.Open(p)) rev = odp.Revision();

        var result = ProjectAdoption.Adopt(p, ProjectAdoption.Propose(p).Config);

        Assert.Equal(before, DrawingFingerprint(p));                                // every drawing row, byte for byte
        using (var odp = OdpFile.Open(p))
        {
            Assert.Equal(sha, odp.Drawing("a.dxf")!.Sha256);
            Assert.Equal(rev + 1, odp.Revision());                                  // one revision for the whole configuration
            Assert.Contains("\"mynaops\"", odp.MetaText("sync.last"));
            Assert.Equal(3, odp.TakeoffRows(0).Count);                              // the take-off is untouched too
            Assert.Equal(1UL, ProjectConfig.Read(odp).Read.Config.Revision);
        }
        Assert.True(File.Exists(result.Backup));
        Assert.Contains(" manual", Path.GetFileNameWithoutExtension(result.Backup), StringComparison.OrdinalIgnoreCase);
        // the backup is the file as it was: not configured, same revision
        Assert.False(ProjectConfig.Probe(result.Backup).Configured);
        using (var b = OdpFile.Open(result.Backup)) Assert.Equal(rev, b.Revision());
        // the proposal MynaCAD wrote is left where it is (it is MynaCAD's key)
        using (var odp = OdpFile.Open(p)) Assert.NotNull(odp.MetaText("config.proposal"));
    }

    [Fact]
    public void Adopting_twice_changes_nothing_the_second_time()
    {
        var p = Legacy();
        var cfg = ProjectAdoption.Propose(p).Config;
        ProjectAdoption.Adopt(p, cfg);
        using var odp = OdpFile.Open(p);
        var again = ProjectConfig.Write(odp, cfg);
        Assert.Empty(again.Changed);
    }

    [Fact]
    public void An_edited_proposal_is_what_is_written()
    {
        var p = Legacy();
        var prop = ProjectAdoption.Propose(p);
        var edited = prop.Config with { Project = prop.Config.Project! with { Title = "Renamed on the Adopt page", Pid = "HP-77" } };
        ProjectAdoption.Adopt(p, edited);
        using var odp = OdpFile.Open(p);
        Assert.Equal("Renamed on the Adopt page", ProjectConfig.Read(odp).Read.Config.Project!.Title);
    }

    [Fact]
    public void Adoption_is_refused_while_mynacad_holds_the_project()
    {
        var p = Legacy();
        var before = DrawingFingerprint(p);
        using (FakeCad(p))
            Assert.Throws<InvalidOperationException>(() => ProjectAdoption.Adopt(p, ProjectAdoption.Propose(p).Config));
        Assert.True(ProjectAdoption.NeedsAdoption(p));
        Assert.Equal(before, DrawingFingerprint(p));
        Assert.Empty(Directory.GetDirectories(Dir, "*.backups"));                   // refused before the backup
    }

    [Fact]
    public void A_failed_backup_stops_the_adoption_and_writes_nothing()
    {
        var p = Legacy();
        Assert.Throws<IOException>(() => ProjectAdoption.Adopt(p, ProjectAdoption.Propose(p).Config, _ => throw new IOException("disk full")));
        Assert.True(ProjectAdoption.NeedsAdoption(p));
    }
}
