using System.Text.Json.Nodes;
using Aqc.CadImport;
using Xunit;

namespace AQC.CadImport.Tests;

/// <summary>
/// What the level UI stands on (docs/bridge/LEVEL-IDS.md, "Per-level summary, level picker, status"): the header's <c>level_status</c>, the per-level summary lines,
/// the AQC level each CAD level maps to, importing a filtered file, and the double-count warning comparing floors by elevation when both sides state one.
/// </summary>
public class TakeoffLevelUiTests
{
    const string DrawingA = "0b7d9c34-1e6f-4f0b-8a13-9b2c7d5e41aa";
    static string Fixture(string n) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", n));

    /// <summary>The small house with Ground (id 1, 0 m) and a First level (id 2, 3.15 m) holding one wall, one column and one room.</summary>
    static JsonObject TwoStoreys()
    {
        var root = (JsonObject)JsonNode.Parse(Fixture("small-house.json"))!;
        var g = (JsonObject)((JsonArray)root["levels"]!)[0]!;
        g["id"] = 1; g["elevation_mm"] = 0;
        ((JsonArray)root["levels"]!).Add(new JsonObject { ["name"] = "First", ["height_mm"] = 3000, ["slab_thickness_mm"] = 150, ["beam_depth_mm"] = 450, ["id"] = 2, ["elevation_mm"] = 3150 });
        foreach (var k in new[] { "walls", "openings", "columns", "lintels", "rooms", "slabs", "opening_schedule" })
            foreach (var r in (JsonArray)root[k]!) ((JsonObject)r!)["level_id"] = 1;
        ((JsonArray)root["walls"]!).Add(new JsonObject { ["ref"] = "W1001", ["level"] = "First", ["level_id"] = 2, ["length_mm"] = 4000, ["height_mm"] = 2850, ["thickness_mm"] = 230 });
        ((JsonArray)root["columns"]!).Add(new JsonObject { ["ref"] = "C1001", ["mark"] = "C9", ["level"] = "First", ["level_id"] = 2, ["width_mm"] = 230, ["depth_mm"] = 450 });
        ((JsonArray)root["rooms"]!).Add(new JsonObject { ["ref"] = "R1001", ["name"] = "Bed", ["level"] = "First", ["level_id"] = 2, ["area_m2"] = 12.5 });
        return root;
    }

    static ImportModel NewModel(params (string Id, string Name)[] levels)
    {
        var m = new ImportModel();
        m.Levels.Add(new LevelRec { Id = "Lvl0", Name = "Plinth", HeightMm = 3200, SlabThicknessMm = 150, BeamDepthMm = 450 });
        foreach (var (id, name) in levels) m.Levels.Add(new LevelRec { Id = id, Name = name, HeightMm = 3000, SlabThicknessMm = 150, BeamDepthMm = 450 });
        m.Project["name"] = "Untitled Project";
        return m;
    }

    static JsonObject Status(params object[] levels) => new()
    {
        ["mode"] = "all", ["recorded_at"] = "2026-11-15T10:00:00Z",
        ["levels"] = new JsonArray(levels.Select(l => (JsonNode?)JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(l))).ToArray()),
    };

    // ---- level_status in the header ----

    [Fact]
    public void A_header_without_level_status_lists_with_none()
    {
        var recs = new List<TakeoffRecord> { new(OspTakeoff.HeaderId(7), 0, 0, 0, 0, new JsonObject { ["format"] = OspTakeoff.HeaderFormatWire, ["version"] = 1, ["units"] = "mm", ["source"] = new JsonObject { ["drawing_id"] = "d" } }.ToJsonString()) };
        Assert.Null(Assert.Single(OspTakeoff.ListDrawings(recs)).LevelStatus);
    }

    [Fact]
    public void Level_status_is_read_from_the_header_with_its_flags()
    {
        var header = new JsonObject
        {
            ["format"] = OspTakeoff.HeaderFormatWire, ["version"] = 1, ["units"] = "mm", ["source"] = new JsonObject { ["drawing_id"] = "d" },
            ["level_status"] = Status(
                new { id = 1, name = "Ground", recorded = true, rows = 12, stale = false, fingerprint = "00ab" },
                new { id = 2, name = "First", recorded = true, rows = 4, stale = true },
                new { id = 3, name = "Roof", recorded = false, rows = 0, stale = false }),
        };
        var d = Assert.Single(OspTakeoff.ListDrawings(new[] { new TakeoffRecord(OspTakeoff.HeaderId(7), 0, 0, 0, 0, header.ToJsonString()) }));
        var st = d.LevelStatus!;
        Assert.Equal(("all", "2026-11-15T10:00:00Z"), (st.Mode, st.RecordedAt));
        Assert.Equal(new[] { true, true, false }, st.Levels.Select(l => l.Recorded));
        Assert.Equal(new[] { false, true, false }, st.Levels.Select(l => l.Stale));
        Assert.Equal(12, st.Levels[0].Rows);
        Assert.Equal("First", st.For(2, "whatever")!.Name);      // the id wins
        Assert.Equal(3, st.For(null, "roof")!.Id);               // else the name, case-insensitive
        Assert.Null(st.For(null, "Cellar"));
    }

    [Fact]
    public void Hostile_level_status_never_throws()
    {
        foreach (var bad in new JsonNode?[]
                 {
                     null, JsonValue.Create("x"), JsonValue.Create(5), new JsonArray(1, 2), new JsonObject(), new JsonObject { ["levels"] = "no" },
                     new JsonObject { ["levels"] = new JsonArray(1, "a", null, new JsonObject()) },
                     new JsonObject { ["levels"] = new JsonArray(new JsonObject { ["name"] = "A", ["id"] = -1, ["rows"] = "many", ["recorded"] = "yes", ["stale"] = 1 }) },
                     new JsonObject { ["levels"] = new JsonArray(new JsonObject { ["name"] = "A", ["id"] = 1e30, ["rows"] = 1e30 }) },
                 })
        {
            var st = OspTakeoff.ParseLevelStatus(bad);
            if (st is null) continue;
            Assert.All(st.Levels, l => { Assert.True(l.Rows >= 0); Assert.True(l.Id is null or >= 0); Assert.False(l.Recorded && l.Name.Length == 0); });
        }
        var weird = OspTakeoff.ParseLevelStatus(new JsonObject { ["levels"] = new JsonArray(new JsonObject { ["name"] = "A", ["id"] = -1, ["rows"] = "many", ["recorded"] = "yes", ["stale"] = 1 }) })!;
        var a = Assert.Single(weird.Levels);
        Assert.Equal((null, 0, false, false), (a.Id, a.Rows, a.Recorded, a.Stale));
        // more entries than any drawing has are cut, not trusted
        var many = new JsonArray(Enumerable.Range(0, 1000).Select(i => (JsonNode?)new JsonObject { ["name"] = "L" + i, ["recorded"] = true }).ToArray());
        Assert.Equal(256, OspTakeoff.ParseLevelStatus(new JsonObject { ["levels"] = many })!.Levels.Count);
    }

    // ---- the per-level summary ----

    [Fact]
    public void Summary_lines_carry_quantities_status_and_the_aqc_level()
    {
        var file = BridgeFile.Parse(TwoStoreys().ToJsonString());
        var status = OspTakeoff.ParseLevelStatus(Status(
            new { id = 1, name = "Ground", recorded = true, rows = 20, stale = false },
            new { id = 2, name = "First", recorded = true, rows = 3, stale = true }));
        var aqc = TakeoffLevels.AqcLevels(file, NewModel(("Lvl1", "Ground")));
        var lines = TakeoffLevels.Lines(file, status, aqc);
        Assert.Equal(new[] { "Ground", "First" }, lines.Select(l => l.Level));
        Assert.Equal(new[] { "Lvl1", "Lvl2 (new)" }, lines.Select(l => l.Aqc));
        Assert.Equal(new[] { "recorded", "changed in MynaCAD since recorded" }, lines.Select(l => l.Status));
        var first = lines[1];
        Assert.Equal(("3.15 m", "1", "4", "11.4", "1", "12.5"), (first.Elevation, first.Walls, first.WallLength, first.WallArea, first.Rooms, first.RoomArea));
        // without a status or a mapping the columns are empty, not wrong
        var plain = TakeoffLevels.Lines(file);
        Assert.All(plain, l => { Assert.Equal("", l.Status); Assert.Equal("", l.Aqc); });
    }

    [Fact]
    public void A_level_the_recording_left_out_is_labelled()
    {
        var file = BridgeFile.Parse(TwoStoreys().ToJsonString());
        var only = OspTakeoff.ParseLevelStatus(Status(new { id = 1, name = "Ground", recorded = true, rows = 20, stale = false }));
        Assert.Equal("not in the recording", TakeoffLevels.Lines(file, only)[1].Status);
        var notRec = OspTakeoff.ParseLevelStatus(Status(new { id = 1, name = "Ground", recorded = true, rows = 20, stale = false }, new { id = 2, name = "First", recorded = false, rows = 0, stale = false }));
        Assert.Equal("not recorded", TakeoffLevels.Lines(file, notRec)[1].Status);
    }

    [Fact]
    public void The_aqc_level_follows_the_importers_mapping_by_name_then_position_and_never_lvl0()
    {
        var file = BridgeFile.Parse(TwoStoreys().ToJsonString());
        var byName = TakeoffLevels.AqcLevels(file, NewModel(("Lvl1", "First"), ("Lvl2", "Ground")));
        Assert.Equal(("Lvl2", false), (byName["Ground"].Id, byName["Ground"].IsNew));
        Assert.Equal("Lvl1", byName["First"].Id);
        var empty = TakeoffLevels.AqcLevels(file, NewModel());
        Assert.Equal(new[] { "Lvl1", "Lvl2" }, new[] { empty["Ground"].Id, empty["First"].Id });
        Assert.All(empty.Values, v => Assert.True(v.IsNew));
        Assert.DoesNotContain(empty.Values, v => v.Id == "Lvl0");
    }

    // ---- the picker: importing a filtered file; the BOQ by level ----

    [Fact]
    public void Importing_one_chosen_level_adds_only_its_rows_and_levels_and_the_rows_sit_on_its_aqc_level()
    {
        var file = BridgeFile.Parse(TwoStoreys().ToJsonString());
        var m = NewModel();
        var plan = CadImporter.Plan(TakeoffLevels.Filter(file, new[] { "First" }), m);
        CadImporter.Apply(plan, m);
        Assert.Single(m.Levels, l => l.Name == "First");
        Assert.DoesNotContain(m.Levels, l => l.Name == "Ground");
        Assert.All(m.Sheet("masonry"), r => Assert.Equal("Lvl1", r["level"]));
        Assert.Single(m.Sheet("masonry"));
        Assert.Single(m.Sheet("columns"));
        // the rest can follow later: the second import adds the other level and leaves the first alone
        var rest = CadImporter.Plan(TakeoffLevels.Filter(file, new[] { "Ground" }), m);
        Assert.DoesNotContain(rest.Items, i => i.Action == PlanAction.Update && i.Section == "masonry");
        CadImporter.Apply(rest, m);
        Assert.Equal(new[] { "First", "Ground" }, m.Levels.Skip(1).Select(l => l.Name).OrderBy(n => n));
    }

    [Fact]
    public void A_chosen_level_is_matched_by_name_only_never_by_position()
    {
        var file = BridgeFile.Parse(TwoStoreys().ToJsonString());
        // The whole file matches Ground to the AQC level at its position when the names differ; a chosen level must not
        var m = NewModel(("Lvl1", "Ground floor"));
        Assert.Equal("Lvl1", TakeoffLevels.AqcLevels(file, m)["Ground"].Id);
        var chosen = TakeoffLevels.Filter(file, new[] { "First" });
        Assert.True(chosen.IsLevelFiltered);
        Assert.Equal("Lvl2", TakeoffLevels.AqcLevels(chosen, m)["First"].Id);   // a new level, not the one at position 0
        Assert.False(file.IsLevelFiltered);
    }

    [Fact]
    public void Importing_every_level_gives_a_per_level_split_of_what_the_boq_reads()
    {
        var file = BridgeFile.Parse(TwoStoreys().ToJsonString());
        var m = NewModel();
        CadImporter.Apply(CadImporter.Plan(file, m), m);
        var byLevel = m.Sheet("masonry").GroupBy(r => r["level"]).ToDictionary(g => g.Key, g => g.Count());
        var summary = TakeoffLevels.Summarise(file);
        Assert.Equal(summary[0].Walls, byLevel["Lvl1"]);
        Assert.Equal(summary[1].Walls, byLevel["Lvl2"]);
        Assert.All(m.Sheet("masonry"), r => Assert.True(r.ContainsKey("source_elevation_mm")));
    }

    // ---- the double-count warning ----

    static BridgeFile Drawing(string id, string levelName, double? elevation)
    {
        var json = Fixture("small-house.json").Replace(DrawingA, id);
        foreach (var r in new[] { "W0001", "W0002", "W0003", "2A1", "2A2", "2A3", "2B1", "2B2", "3C1", "3C2", "4D1", "5E1", "5E2", "6F1" })
            json = json.Replace($"\"{r}\"", $"\"{r}{id}\"");
        json = json.Replace("\"Ground\"", $"\"{levelName}\"");
        var o = (JsonObject)JsonNode.Parse(json)!;
        if (elevation is { } e) ((JsonObject)((JsonArray)o["levels"]!)[0]!)["elevation_mm"] = e;
        return BridgeFile.Parse(o.ToJsonString());
    }

    [Fact]
    public void The_same_name_at_another_elevation_is_another_floor_and_does_not_warn()
    {
        var m = NewModel();
        CadImporter.Apply(CadImporter.Plan(Drawing("a", "Ground", 0), m), m);
        // both land on Lvl1 by name, but the second "Ground" is 3.15 m up
        Assert.Empty(CadImporter.Plan(Drawing("b", "Ground", 3150), m).Warnings);
    }

    [Fact]
    public void Another_name_at_the_same_elevation_is_the_same_floor_and_warns_even_on_another_aqc_level()
    {
        var m = NewModel(("Lvl1", "Ground"), ("Lvl2", "Slab level"));
        CadImporter.Apply(CadImporter.Plan(Drawing("a", "Ground", 0), m), m);
        var plan = CadImporter.Plan(Drawing("b", "Slab level", 0.5), m);   // 0.5 mm: within the 1 mm tolerance
        Assert.Contains(plan.Warnings, w => w.Contains("Drawing a") && w.Contains("twice"));
    }

    [Fact]
    public void Without_elevations_on_both_sides_the_old_rule_holds()
    {
        var m = NewModel();
        CadImporter.Apply(CadImporter.Plan(Drawing("a", "Ground", null), m), m);
        Assert.NotEmpty(CadImporter.Plan(Drawing("b", "Ground", 3150), m).Warnings);   // an old row has no elevation: compared by AQC level
        var m2 = NewModel();
        CadImporter.Apply(CadImporter.Plan(Drawing("a", "Ground", 0), m2), m2);
        Assert.NotEmpty(CadImporter.Plan(Drawing("b", "Ground", null), m2).Warnings);  // the incoming file states none
    }

    [Fact]
    public void A_non_finite_or_missing_elevation_is_not_stored()
    {
        var m = NewModel();
        var f = Drawing("a", "Ground", null);
        CadImporter.Apply(CadImporter.Plan(f, m), m);
        Assert.All(m.Sheet("masonry"), r => Assert.False(r.ContainsKey("source_elevation_mm")));
    }
}
