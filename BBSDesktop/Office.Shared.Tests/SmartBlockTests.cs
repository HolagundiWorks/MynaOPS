// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using Office.Shared;
using Office.Shared.SmartBlocks;
using Xunit;

namespace Office.Shared.Tests;

public sealed class SmartBlockTests
{
    private static readonly Dictionary<string, double> None = new();

    private static double F(string text, params (string, double)[] vars) =>
        SmartFormula.Parse(text).Evaluate(vars.ToDictionary(v => v.Item1, v => v.Item2));

    [Fact]
    public void Formulas_follow_precedence_and_functions()
    {
        Assert.Equal(14, F("2 + 3 * 4"));
        Assert.Equal(20, F("(2 + 3) * 4"));
        Assert.Equal(-6, F("-2 * 3"));
        Assert.Equal(8, F("2^3"));
        Assert.Equal(0.25, F("2^-2"));
        Assert.Equal(3, F("min(5, 3, 9)"));
        Assert.Equal(9, F("max(5, 3, 9)"));
        Assert.Equal(2, F("round(1.5)"));
        Assert.Equal(3, F("sqrt(9)"));
        Assert.Equal(3, F("floor(w / 2)", ("w", 7)));
        Assert.Equal(66, F("cover + tie + bar/2", ("cover", 50), ("tie", 8), ("bar", 16)));
    }

    [Theory]
    [InlineData("1 / 0")]
    [InlineData("sqrt(-1)")]
    [InlineData("nope")]
    [InlineData("2 +")]
    [InlineData("2 ^ x")]
    [InlineData("sin(1)")]
    [InlineData("(1")]
    public void A_bad_formula_is_an_error_not_a_crash(string text)
    {
        Assert.Throws<SmartBlockException>(() => F(text));
    }

    [Fact]
    public void The_library_has_the_eleven_rcc_blocks_with_their_parameters()
    {
        var ids = SmartBlockLibrary.All.Select(b => b.Id).ToList();
        Assert.Equal(
            [
                "rcc.column.rect", "rcc.column.circular", "rcc.beam.section", "rcc.beam.elevation", "rcc.stirrup.detail",
                "rcc.footing.plan", "rcc.footing.section", "rcc.footing.combined", "rcc.raft.lift", "rcc.lintel.section", "rcc.slab.section",
            ],
            ids);
        var column = SmartBlockLibrary.Find("rcc.column.rect")!;
        foreach (var name in new[] { "width", "depth", "cover", "nx", "ny", "bar", "bar2", "tie", "tiesp", "fck", "fy", "exposure", "dims", "ftin" })
            Assert.NotNull(column.Param(name));
    }

    [Fact]
    public void Values_are_made_whole_snapped_to_a_choice_and_kept_in_range()
    {
        var column = SmartBlockLibrary.Find("rcc.column.rect")!;
        var v = column.Resolve(new Dictionary<string, double> { ["width"] = 5000, ["depth"] = 100, ["ny"] = 3.6, ["bar"] = 18, ["fck"] = 27 });
        Assert.Equal(2000, v["width"]);
        Assert.Equal(150, v["depth"]);
        Assert.Equal(4, v["ny"]);
        Assert.Equal(16, v["bar"]); // 18 is equally far from 16 and 20: the first in order wins
        Assert.Equal(25, v["fck"]);
        Assert.Equal(40, v["cover"]); // not given: the default
    }

    [Fact]
    public void A_rectangular_column_draws_its_outline_ties_and_bars()
    {
        var g = SmartBlockLibrary.Find("rcc.column.rect")!.Evaluate();
        Assert.Equal(6, g.Counts()["dot"]); // 2 bars on each width face and 1 more on each depth face
        var outline = g.Prims.OfType<PolyPrim>().First();
        Assert.Equal(230, outline.Pts.Max(p => p.X) - outline.Pts.Min(p => p.X), 6);
        Assert.Equal(450, outline.Pts.Max(p => p.Y) - outline.Pts.Min(p => p.Y), 6);
        var texts = g.Prims.OfType<TextPrim>().Select(t => t.Value).ToList();
        Assert.Contains("6Y16", texts); // the bars, as a schedule writes them
        Assert.Contains("Ties-Y8@6\"", texts); // the ties at 150 mm, in inches
        Assert.Contains("M25, Fe500", texts);
        Assert.Contains("9\"", texts); // the width and depth dimensions, in feet and inches
        Assert.Contains("1'-5.5\"", texts);
        // Bars are coloured by their place: ties and cross-ties green, the dimensions orange.
        Assert.All(g.Prims.OfType<DotPrim>(), d => Assert.Equal("S-REBR-MAIN", d.Layer));
        var more = SmartBlockLibrary.Find("rcc.column.rect")!.Evaluate(new Dictionary<string, double> { ["ny"] = 4, ["bar"] = 20 });
        Assert.Equal(8, more.Counts()["dot"]);
    }

    [Fact]
    public void Bars_of_two_diameters_are_named_and_coloured_apart()
    {
        var g = SmartBlockLibrary.Find("rcc.column.rect")!.Evaluate(new Dictionary<string, double> { ["nx"] = 4, ["ny"] = 3, ["bar"] = 20, ["bar2"] = 16 });
        Assert.Contains(g.Prims.OfType<TextPrim>(), t => t.Value == "4Y20 + 6Y16");
        Assert.Equal(4, g.Prims.OfType<DotPrim>().Count(d => d.Layer == "S-REBR-MAIN")); // the corners
        Assert.Equal(6, g.Prims.OfType<DotPrim>().Count(d => d.Layer == "S-REBR-EXTR")); // the others
        // Two intermediate bars on the width face make two cross-ties, one on the depth face makes one more.
        Assert.Equal(2, g.Prims.OfType<LinePrim>().Count(l => l.Layer == "S-REBR-XTRA"));
        Assert.Equal(1, g.Prims.OfType<LinePrim>().Count(l => l.Layer == "S-REBR-LINK" && l.Y1 == l.Y2 && Math.Abs(l.X2 - l.X1) > 100));
    }

    [Theory]
    [InlineData(229, "9\"")]
    [InlineData(450, "1'-5.5\"")]
    [InlineData(1830, "6'-0\"")]
    [InlineData(38, "1.5\"")]
    [InlineData(100, "4\"")]
    public void Lengths_in_feet_and_inches_round_to_a_half_inch(double mm, string want) => Assert.Equal(want, SmartBlockDef.FeetInches(mm));

    [Fact]
    public void Every_layer_a_block_draws_on_is_in_its_table_with_a_pen_and_a_linetype()
    {
        foreach (var b in SmartBlockLibrary.All)
        {
            var g = b.Evaluate();
            Assert.NotEmpty(g.Layers!);
            foreach (var layer in g.Prims.Select(p => p.Layer).Distinct())
            {
                Assert.NotNull(layer);
                Assert.True(g.Layers!.TryGetValue(layer!, out var l), $"{b.Id}: layer {layer} is not in the table");
                Assert.InRange(l!.Lineweight, 9, 100); // the ISO 128 pen widths, 0.09 to 1.00 mm
                Assert.StartsWith("S-", l.Name);
                Assert.Contains(l.Linetype, new[] { "Continuous", "HIDDEN", "CENTER" });
            }
        }
        var column = SmartBlockLibrary.Find("rcc.column.rect")!.Evaluate();
        Assert.Equal("S-CONC-OUTL", column.Prims.OfType<PolyPrim>().First().Layer);
        Assert.Equal(50, column.Layers!["S-CONC-OUTL"].Lineweight);
        Assert.Equal("S-ANNO-DIMS", column.Prims.OfType<TextPrim>().First(t => t.Value == "9\"").Layer);
        var footing = SmartBlockLibrary.Find("rcc.footing.section")!.Evaluate();
        Assert.Equal("HIDDEN", SmartBlockLibrary.Find("rcc.footing.plan")!.Evaluate().Layers!["S-CONC-HIDD"].Linetype); // the PCC projection, concealed
        Assert.Equal("CENTER", footing.Layers!["S-GRND"].Linetype);
        Assert.Contains(footing.Prims.OfType<HatchPrim>(), h => h.Pattern == "AR-CONC" && h.Layer == "S-SOIL-HATCH"); // the PCC
    }

    [Fact]
    public void Dimensions_can_be_shown_in_millimetres()
    {
        var g = SmartBlockLibrary.Find("rcc.column.rect")!.Evaluate(new Dictionary<string, double> { ["ftin"] = 0 });
        var texts = g.Prims.OfType<TextPrim>().Select(t => t.Value).ToList();
        Assert.Contains("230", texts);
        Assert.Contains("450", texts);
        Assert.Contains("Ties-Y8@150", texts);
    }

    [Fact]
    public void A_beam_in_elevation_has_its_stirrup_zones_and_curtailment_dimensions()
    {
        var g = SmartBlockLibrary.Find("rcc.beam.elevation")!.Evaluate();
        var texts = g.Prims.OfType<TextPrim>().Select(t => t.Value).ToList();
        Assert.Contains("5 nos @ 6\"", texts);
        Assert.Contains("@ 7\"", texts);
        Assert.Contains("2'-11.5\"", texts); // 900 mm of extra top bar from each support face
        Assert.Contains("2Y12", texts);
        Assert.Contains("1Y16", texts);
        var vertical = g.Prims.OfType<TextPrim>().Count(t => t.RotationDeg == 90);
        Assert.Equal(0, vertical);
    }

    [Fact]
    public void A_circular_column_has_its_bars_on_a_ring()
    {
        var g = SmartBlockLibrary.Find("rcc.column.circular")!.Evaluate();
        Assert.Equal(8, g.Counts()["dot"]);
        var d = SmartBlockLibrary.Find("rcc.column.circular")!.Evaluate(new Dictionary<string, double> { ["dia"] = 600, ["bars"] = 10 });
        Assert.Equal(10, d.Counts()["dot"]);
        var (x0, _, x1, _) = d.Bounds()!.Value;
        Assert.Equal(600, x1 - x0, 6);
    }

    [Fact]
    public void Every_block_evaluates_with_its_defaults_and_hostile_values_do_not_crash()
    {
        foreach (var b in SmartBlockLibrary.All)
        {
            var g = b.Evaluate();
            Assert.NotEmpty(g.Prims);
            Assert.NotNull(g.Bounds());
            // every value at its lowest, then at its highest
            foreach (var pick in new Func<SmartParam, double>[] { p => p.Min ?? p.Choices.FirstOrDefault()?.Value ?? p.Default, p => p.Max ?? p.Choices.LastOrDefault()?.Value ?? p.Default })
            {
                var given = b.Params.ToDictionary(p => p.Name, pick);
                var e = Record.Exception(() => b.Evaluate(given));
                Assert.True(e is null or SmartBlockException, $"{b.Id}: {e}");
            }
        }
    }

    /// <summary>
    /// <c>Fixtures/rcc_counts.json</c> is written by MynaCAD's engine (<c>crates/engine/tests/rcc_parity.rs</c>): how many lines, polylines, dots, circles, arcs,
    /// texts and hatches each block has for some parameter values. This reader must draw the same, from its copy of the same <c>rcc.json</c>.
    /// </summary>
    [Fact]
    public void Every_block_has_the_same_parts_as_the_engine_draws()
    {
        var cases = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rcc_counts.json")))!.AsArray();
        Assert.NotEmpty(cases);
        foreach (var c in cases)
        {
            var id = c!["id"]!.GetValue<string>();
            var given = new Dictionary<string, double>();
            foreach (var part in c["params"]!.GetValue<string>().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var kv = part.Split('=');
                given[kv[0].Trim()] = double.Parse(kv[1], System.Globalization.CultureInfo.InvariantCulture);
            }
            var want = c["counts"]!.AsObject().ToDictionary(k => k.Key, k => k.Value!.GetValue<int>());
            var got = SmartBlockLibrary.Find(id)!.Evaluate(given).Counts();
            Assert.True(
                want.OrderBy(k => k.Key).SequenceEqual(got.OrderBy(k => k.Key)),
                $"{id} [{c["params"]}]: engine {string.Join(", ", want.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}"))}; here {string.Join(", ", got.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}"))}");
        }
    }

    /// <summary><c>Fixtures/details.osp</c> was saved by MynaCAD's host (a beam, a column and a slab placed, the beam's bars then changed).</summary>
    [Fact]
    public void The_details_O_Draft_recorded_are_read_and_drawn()
    {
        var dir = Path.Combine(Path.GetTempPath(), "details-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "details.osp");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "details.osp"), path);
            using var odp = OdpFile.Open(path);
            var all = odp.Details();
            Assert.Equal(["beams", "columns", "slabs"], all.Select(d => d.Kind).Order().ToArray());
            var beam = Assert.Single(odp.Details("beams"));
            Assert.Equal("rcc.beam.section", beam.Block);
            Assert.Equal("S-RCC", beam.Layer);
            Assert.StartsWith("RCC beam cross-section (300 x 450)", beam.Label);
            Assert.StartsWith(beam.Drawing + "/", beam.Id);
            Assert.Empty(odp.Details("footings"));
            foreach (var d in all)
            {
                using var json = System.Text.Json.JsonDocument.Parse(d.ParamsJson);
                var values = json.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == System.Text.Json.JsonValueKind.Number).ToDictionary(p => p.Name, p => p.Value.GetDouble());
                var g = SmartBlockLibrary.Find(d.Block)!.Evaluate(values);
                Assert.NotEmpty(g.Prims);
                if (d.Kind == "beams")
                {
                    Assert.Equal(300, values["width"]);
                    Assert.Equal(2, values["nbot"]); // the bars were changed after the first save, and the details followed
                    Assert.Equal(2 + 2, g.Counts()["dot"]); // 2 top bars and 2 bottom bars
                }
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Text_names_a_choice_by_its_label()
    {
        var g = SmartBlockLibrary.Find("rcc.slab.section")!.Evaluate(new Dictionary<string, double> { ["fy"] = 415, ["fck"] = 30 });
        Assert.Contains(g.Prims.OfType<TextPrim>(), t => t.Value.Contains("M30, Fe415"));
    }
}
