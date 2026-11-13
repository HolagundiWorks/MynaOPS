using System.Text.Json.Nodes;
using Aqc.CadImport;
using Xunit;

namespace AQC.CadImport.Tests;

/// <summary>Optional level ids and elevations on take-off records (docs/bridge/LEVEL-IDS.md): reading, grouping, filtering, and that older files behave as before.</summary>
public class TakeoffLevelsTests
{
    static string Fixture(string n) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", n));

    /// <summary>The small house on Ground (id 1), plus a First floor (id 2) holding a wall, a column and a room; rows carry level_id.</summary>
    static JsonObject Leveled(Action<JsonObject>? tweak = null)
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
        tweak?.Invoke(root);
        return root;
    }

    static BridgeFile Parse(JsonObject o) => BridgeFile.Parse(o.ToJsonString());

    [Fact]
    public void A_file_without_ids_reads_and_groups_by_name_as_before()
    {
        var f = BridgeFile.Parse(Fixture("small-house.json"));
        Assert.All(f.Levels, l => { Assert.Null(l.Id); Assert.Null(l.ElevationMm); });
        Assert.Equal(3150, f.Levels[0].HeightMm);
        var s = TakeoffLevels.Summarise(f);
        var ground = Assert.Single(s);
        Assert.Equal("Ground", ground.Name);
        Assert.Null(ground.Id);
        Assert.Equal(f.Walls.Count, ground.Walls);
        Assert.Equal(f.Openings.Count, ground.Openings);
        Assert.Equal(f.Rooms.Count, ground.Rooms);
        Assert.Equal(f.Walls.Sum(w => (double)w["length_mm"]!), ground.WallLengthMm);
        Assert.Same(f, TakeoffLevels.Filter(f, null));
        Assert.Same(f, TakeoffLevels.Filter(f, Array.Empty<string>()));
    }

    [Fact]
    public void Level_ids_elevations_and_row_ids_are_read_and_the_old_fields_keep_their_meaning()
    {
        var f = Parse(Leveled());
        Assert.Equal(new[] { 1, 2 }, f.Levels.Select(l => l.Id!.Value));
        Assert.Equal(new double?[] { 0, 3150 }, f.Levels.Select(l => l.ElevationMm));
        Assert.Equal(3150, f.Levels[0].HeightMm);
        Assert.Equal(150, f.Levels[0].SlabThicknessMm);
        Assert.Equal(2, BridgeFile.LevelIdOf(f.Walls.Last()));
        Assert.Null(BridgeFile.LevelIdOf(new JsonObject { ["level"] = "Ground" }));
    }

    [Fact]
    public void Quantities_group_by_level_and_a_filter_keeps_one_level()
    {
        var f = Parse(Leveled());
        var s = TakeoffLevels.Summarise(f);
        Assert.Equal(new[] { "Ground", "First" }, s.Select(l => l.Name));
        var first = s[1];
        Assert.Equal((2, 1, 1), (first.Id, first.Walls, first.Columns));
        Assert.Equal(4000, first.WallLengthMm);
        Assert.Equal(11.4, first.WallAreaM2);
        Assert.Equal(12.5, first.RoomAreaM2);
        Assert.Equal(3150, first.ElevationMm);
        Assert.Equal(f.Walls.Count - 1, s[0].Walls);

        var only = TakeoffLevels.Filter(f, new[] { "first" });
        Assert.Single(only.Walls);
        Assert.Single(only.Columns);
        Assert.Single(only.Rooms);
        Assert.Empty(only.Openings);
        Assert.Empty(only.OpeningSchedule);
        Assert.Equal("First", Assert.Single(only.Levels).Name);
        Assert.Equal(f.DrawingId, only.DrawingId);
    }

    [Fact]
    public void A_level_id_wins_over_a_stale_name_and_a_bad_id_falls_back_to_the_name()
    {
        var f = Parse(Leveled(root =>
        {
            var w = (JsonObject)((JsonArray)root["walls"]!).Last()!;
            w["level"] = "Old name";   // renamed since: the id still says First
            ((JsonArray)root["columns"]!).Add(new JsonObject { ["ref"] = "C1002", ["level"] = "First", ["level_id"] = 1.5, ["width_mm"] = 230, ["depth_mm"] = 230 });
            ((JsonArray)root["columns"]!).Add(new JsonObject { ["ref"] = "C1003", ["level"] = "First", ["level_id"] = "x", ["width_mm"] = 230, ["depth_mm"] = 230 });
            ((JsonArray)root["columns"]!).Add(new JsonObject { ["ref"] = "C1004", ["level"] = "Ground", ["level_id"] = 99, ["width_mm"] = 230, ["depth_mm"] = 230 });
        }));
        Assert.Equal("First", TakeoffLevels.LevelNameOf(f, f.Walls.Last()));
        var s = TakeoffLevels.Summarise(f);
        Assert.Equal(new[] { "Ground", "First" }, s.Select(l => l.Name));   // no level made from the stale name
        Assert.Equal(3, s[1].Columns);   // C9 and the two with an unusable id, by name
        Assert.Equal(3, s[0].Columns);   // the two fixture columns plus C1004: id 99 is unknown, its name is Ground
    }

    [Fact]
    public void A_row_whose_level_the_file_does_not_list_gets_a_level_of_its_own()
    {
        var f = Parse(Leveled(root => ((JsonArray)root["walls"]!).Add(new JsonObject { ["ref"] = "W2001", ["level"] = "Terrace", ["length_mm"] = 1000, ["height_mm"] = 1000 })));
        var s = TakeoffLevels.Summarise(f);
        Assert.Equal("Terrace", s[^1].Name);
        Assert.Null(s[^1].Id);
        Assert.Equal(1, s[^1].Walls);
    }

    [Fact]
    public void Ids_survive_the_project_file_records_and_the_importer_groups_by_level()
    {
        var root = Leveled();
        var s = OspTakeoff.SlotFor((string)root["source"]!["drawing_id"]!);
        var header = new JsonObject { ["format"] = OspTakeoff.HeaderFormatWire, ["version"] = 1, ["units"] = "mm", ["state"] = "complete", ["source"] = root["source"]!.DeepClone(), ["levels"] = root["levels"]!.DeepClone() };
        var recs = new List<TakeoffRecord> { new(OspTakeoff.HeaderId(s), 0, 0, 0, 0, header.ToJsonString()) };
        uint n = 1;
        foreach (var (section, kind) in new[] { ("walls", OspTakeoff.KindWall), ("columns", OspTakeoff.KindColumn), ("rooms", OspTakeoff.KindRoom) })
            foreach (var row in (JsonArray)root[section]!)
                recs.Add(new TakeoffRecord(OspTakeoff.MakeId(kind, s, n++), 0, 0, 1000, 1000, row!.ToJsonString()));

        var read = OspTakeoff.Read(recs).File;
        Assert.Equal(new[] { 1, 2 }, read.Levels.Select(l => l.Id!.Value));
        var sums = TakeoffLevels.Summarise(read);
        Assert.Equal(new[] { 3, 1 }, sums.Select(q => q.Walls));

        // the importer places the First rows on the AQC level that matches by name, using the id-resolved name
        var model = new ImportModel();
        model.Levels.Add(new LevelRec { Id = "Lvl0", Name = "Plinth", HeightMm = 3200, SlabThicknessMm = 150, BeamDepthMm = 450 });
        model.Project["name"] = "P";
        var plan = CadImporter.Plan(TakeoffLevels.Filter(read, new[] { "First" }), model);
        var wall = Assert.Single(plan.Items, i => i.Section == "masonry" && i.Action == PlanAction.Add);
        Assert.Equal("W1001", wall.Ref);
        Assert.Contains(plan.Items, i => i.Section == "level" && i.Ref == "First");
        Assert.DoesNotContain(plan.Items, i => i.Section == "level" && i.Ref == "Ground");
    }
}
