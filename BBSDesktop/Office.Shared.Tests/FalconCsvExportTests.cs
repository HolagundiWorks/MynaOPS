// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Text;
using Xunit;

namespace Office.Shared.Tests;

public class FalconCsvExportTests
{
    const string ProjectId = "3f2b8c1e-5a47-4d9b-8e21-7c6a90d4b512";
    static readonly DateTimeOffset At = new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);

    static OdpMeta Meta => new(ProjectId, "Demo Villa", "2026-10-01T00:00:00Z", 12);

    static List<FalconCsvEstimateLine> Estimate() => new()
    {
        new("Substructure", "EXC-001", "Excavation in ordinary soil", "m3", 42.5, 310),
        new("Substructure", "PCC-001", "PCC 1:4:8 bed", "m3", 6.25, 5400),
        new("Superstructure", "BRK-010", "Brick masonry 230 mm", "m3", 28.4, 6200),
        new("Plumbing", "PLM-CW20", "CW pipe 20 mm, fixed", "m", 86, 210, "tren:12"),
        new("Plumbing", "PLM-WC01", "Water closet, European", "nos", 3, 8500, "tren:12"),
    };

    static List<FalconCsvMeasured> Measured() => new()
    {
        new("EXC-001", "Excavation in ordinary soil", "m3", ThisPeriod: 12.5, Previous: 30),
        new("BRK-010", "Brick masonry 230 mm", "m3", ThisPeriod: 9.2, Previous: 0),
    };

    static List<OdpTakeoffRow> Takeoff() => new()
    {
        new(1, 12, 1, "{\"ref\":\"PSabc-CW\",\"tag\":\"CW-01\",\"source\":\"TK-01\",\"service\":\"CW\",\"size\":\"20\",\"pipe_m\":86.0,\"level\":\"GF\"}"),
        new(1, 13, 2, "{\"ref\":\"PFabc\",\"tag\":\"WC-01\",\"fixture\":\"WC-01\",\"kind\":\"WC\",\"cold\":\"TK-01\",\"level\":\"GF\"}"),
        new(1, 13, 3, "{\"ref\":\"PFdef\",\"tag\":\"WC-02\",\"fixture\":\"WC-02\",\"kind\":\"WC\",\"cold\":\"TK-01\",\"level\":\"FF\"}"),
    };

    static byte[] Build(List<FalconCsvEstimateLine>? est = null, List<OdpTakeoffRow>? rows = null) =>
        FalconCsvExport.Build(Meta, est ?? Estimate(), Measured(), rows ?? Takeoff(), At, "1.4.1");

    [Fact]
    public void Sample_project_produces_the_shared_fixture_byte_for_byte()
    {
        var expected = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "falcon-csv.sample.csv"));
        Assert.Equal(expected, Build());
    }

    [Fact]
    public void File_has_a_BOM_crlf_and_the_exact_header()
    {
        var b = Build();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, b[..3]);
        var text = new UTF8Encoding(false).GetString(b, 3, b.Length - 3);
        Assert.StartsWith(string.Join(",", FalconCsvExport.Header) + "\r\n", text);
        Assert.EndsWith("\r\n", text);
        Assert.DoesNotContain("\n", text.Replace("\r\n", ""));
    }

    [Fact]
    public void Round_trips_through_a_strict_csv_reader_even_with_hard_text()
    {
        var est = Estimate();
        est[0] = est[0] with { Description = "Excavation, \"deep\"\r\nsecond line", Package = "Sub, structure" };
        var rows = ReadStrict(Build(est));
        Assert.All(rows, r => Assert.Equal(20, r.Count));
        Assert.Equal("Excavation, \"deep\"\r\nsecond line", rows[2][6]);
        Assert.Equal("Sub, structure", rows[2][4]);
        Assert.Equal(new[] { "meta", "estimate", "estimate", "estimate", "estimate", "estimate", "measured", "measured", "takeoff", "takeoff", "takeoff", "total" },
            rows.Skip(1).Select(r => r[1]).ToArray());
    }

    [Fact]
    public void Amount_is_quantity_times_rate_rounded_and_total_matches()
    {
        var rows = ReadStrict(Build(new() { new("P", "A-1", "x", "m", 1.2345, 10.005) }));
        Assert.Equal("1.235", rows[2][8]);   // 1.2345 -> 3 places, away from zero
        Assert.Equal("10.01", rows[2][9]);
        Assert.Equal("12.36", rows[2][10]);  // round(1.235 * 10.01, 2)
        Assert.Equal("12.36", rows[^1][10]);
    }

    [Fact]
    public void Plumbing_and_plumbing_fixture_rows_are_present_with_tren_sources()
    {
        var rows = ReadStrict(Build()).Where(r => r[1] == "takeoff").ToList();
        Assert.Contains(rows, r => r[15] == "plumbing" && r[14] == "tren:12" && r[8] == "86.000");
        Assert.Equal(2, rows.Count(r => r[15] == "plumbing_fixtures" && r[14] == "tren:13"));
    }

    [Theory]
    [InlineData(3, "wall")]
    [InlineData(4, "opening")]
    [InlineData(5, "column")]
    [InlineData(6, "lintel")]
    [InlineData(7, "room")]
    [InlineData(8, "slab")]
    [InlineData(10, "board")]
    [InlineData(11, "circuit")]
    [InlineData(12, "plumbing")]
    [InlineData(13, "plumbing_fixtures")]
    public void Every_take_off_kind_has_its_name_and_a_source(int kind, string name)
    {
        var rows = ReadStrict(Build(rows: new() { new(1, kind, 1, "{\"ref\":\"R1\",\"level\":\"GF\",\"length_mm\":2500,\"area_m2\":4,\"pipe_m\":3}") }))
            .Where(r => r[1] == "takeoff").ToList();
        var r = Assert.Single(rows);
        Assert.Equal(name, r[15]);
        Assert.Equal($"tren:{kind}", r[14]);
        Assert.Equal("R1", r[16]);
    }

    [Fact]
    public void Header_and_annotated_pdf_records_are_not_take_off_rows()
    {
        var rows = ReadStrict(Build(rows: new() { new(1, 1, 0, "{}"), new(1, 9, 0, "{}") }));
        Assert.DoesNotContain(rows, r => r[1] == "takeoff");
        Assert.Equal("7", rows[^1][8]); // 5 estimate + 2 measured
    }

    [Fact]
    public void Refuses_an_empty_estimate()
    {
        var e = Assert.Throws<FalconCsvException>(() => Build(new()));
        Assert.Contains("no lines", e.Message);
    }

    [Fact]
    public void Refuses_an_item_with_no_code_and_says_which()
    {
        var est = Estimate();
        est[1] = est[1] with { ItemCode = " " };
        var e = Assert.Throws<FalconCsvException>(() => Build(est));
        Assert.Contains("no item code", e.Message);
        Assert.Contains("PCC 1:4:8 bed", e.Message);
    }

    [Fact]
    public void Refuses_a_repeated_item_code()
    {
        var est = Estimate();
        est[1] = est[1] with { ItemCode = "exc-001" };
        Assert.Contains("more than one", Assert.Throws<FalconCsvException>(() => Build(est)).Message);
    }

    [Fact]
    public void Refuses_a_project_with_no_osp()
    {
        Assert.Contains("no project file", Assert.Throws<FalconCsvException>(() => FalconCsvExport.ReadOsp(null)).Message);
        Assert.Contains("no project file", Assert.Throws<FalconCsvException>(() => FalconCsvExport.ReadOsp(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid() + ".osp"))).Message);
    }

    [Fact]
    public void Measured_comes_from_certified_bills_last_bill_is_this_period()
    {
        var bills = new List<IReadOnlyList<(string, string, double)>>
        {
            new List<(string, string, double)> { ("Excavation in ordinary soil", "m3", 30) },
            new List<(string, string, double)> { ("excavation in ordinary soil ", "M3", 12.5), ("Unknown work", "m", 4) },
        };
        var m = Assert.Single(FalconCsvExport.MeasuredFromBills(Estimate(), bills));
        Assert.Equal(("EXC-001", 12.5, 30.0), (m.ItemCode, m.ThisPeriod, m.Previous));
        Assert.Empty(FalconCsvExport.MeasuredFromBills(Estimate(), new List<IReadOnlyList<(string, string, double)>>()));
    }

    [Fact]
    public void File_name_follows_the_contract()
    {
        Assert.Equal("Demo Villa-falcon-20261108-r12.csv", FalconCsvExport.FileName("Demo Villa", At, 12));
        Assert.Equal("a_b-falcon-20261108-r0.csv", FalconCsvExport.FileName("a/b", At, 0));
    }

    [Fact]
    public void Reads_meta_from_a_real_osp()
    {
        var path = Path.Combine(Path.GetTempPath(), "falcon-csv-" + Guid.NewGuid().ToString("N") + ".osp");
        try
        {
            using (OdpFile.Create(path, "Demo Villa")) { }
            var (meta, rows) = FalconCsvExport.ReadOsp(path);
            Assert.False(string.IsNullOrEmpty(meta.ProjectId));
            Assert.Equal("Demo Villa", meta.Name);
            Assert.Empty(rows);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    /// <summary>A deliberately strict RFC 4180 reader: CRLF record ends, quotes only around a whole field, "" for a quote; anything else throws.</summary>
    static List<List<string>> ReadStrict(byte[] bytes)
    {
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var s = new UTF8Encoding(false, true).GetString(bytes, 3, bytes.Length - 3);
        var rows = new List<List<string>>();
        var row = new List<string>();
        var f = new StringBuilder();
        int i = 0;
        while (i < s.Length)
        {
            if (s[i] == '"')
            {
                i++;
                while (true)
                {
                    if (i >= s.Length) throw new FormatException("unterminated quote");
                    if (s[i] == '"') { if (i + 1 < s.Length && s[i + 1] == '"') { f.Append('"'); i += 2; continue; } i++; break; }
                    f.Append(s[i++]);
                }
                if (i < s.Length && s[i] != ',' && s[i] != '\r') throw new FormatException("text after closing quote");
            }
            else
            {
                while (i < s.Length && s[i] != ',' && s[i] != '\r')
                {
                    if (s[i] == '"' || s[i] == '\n') throw new FormatException("bare quote or LF in unquoted field");
                    f.Append(s[i++]);
                }
            }
            if (i < s.Length && s[i] == ',') { row.Add(f.ToString()); f.Clear(); i++; if (i >= s.Length) throw new FormatException("ends with comma"); continue; }
            if (i + 1 < s.Length && s[i] == '\r' && s[i + 1] == '\n') { row.Add(f.ToString()); f.Clear(); rows.Add(row); row = new(); i += 2; continue; }
            throw new FormatException("bad record end at " + i);
        }
        if (row.Count > 0 || f.Length > 0) throw new FormatException("missing final CRLF");
        return rows;
    }
}
