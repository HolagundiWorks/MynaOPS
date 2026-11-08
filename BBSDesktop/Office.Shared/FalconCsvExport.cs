// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Office.Shared;

/// <summary>One line of the estimate sheet as the CSV takes it. <see cref="Source"/> is "falcon" for a line typed or priced in Falcon, "tren:&lt;kind&gt;" for one taken from the drawing.</summary>
public sealed record FalconCsvEstimateLine(string Package, string ItemCode, string Description, string Unit, double Quantity, double Rate, string Source = "falcon");

/// <summary>Quantity measured so far for an item, for a running bill. <c>MeasuredToDate = Previous + ThisPeriod</c>; Previous is 0 when Falcon knows of no earlier bill.</summary>
public sealed record FalconCsvMeasured(string ItemCode, string Description, string Unit, double ThisPeriod, double Previous, string Source = "falcon");

public sealed class FalconCsvException : Exception
{
    public FalconCsvException(string message) : base(message) { }
}

/// <summary>
/// The Falcon CSV for Kivo (contract v1, <c>osas-falcon-csv/1</c>): meta, estimate, measured, takeoff and total rows in one RFC 4180 file.
/// Pure: it takes the project's estimate lines, bills and the <c>.osp</c> take-off rows and returns the bytes; the page only picks a file.
/// </summary>
public static class FalconCsvExport
{
    public const string Schema = "osas-falcon-csv/1";

    public static readonly string[] Header =
    {
        "schema", "row_type", "project_id", "drawing_revision", "package", "item_code", "description", "unit", "quantity", "rate", "amount",
        "measured_to_date", "this_period", "previous", "source", "kind", "tag", "size", "level", "note",
    };

    /// <summary>Take-off kind number to the name the contract uses. 1 (header) and 9 (annotated drawing part) are not rows.</summary>
    public static string? KindName(int kind) => kind switch
    {
        3 => "wall", 4 => "opening", 5 => "column", 6 => "lintel", 7 => "room", 8 => "slab",
        10 => "board", 11 => "circuit", 12 => "plumbing", 13 => "plumbing_fixtures",
        _ => null,
    };

    /// <summary>The file name the contract asks for: <c>&lt;project name&gt;-falcon-&lt;yyyymmdd&gt;-r&lt;drawing revision&gt;.csv</c>.</summary>
    public static string FileName(string projectName, DateTimeOffset exportedAt, long drawingRevision)
    {
        var bad = Path.GetInvalidFileNameChars();
        var name = new string((projectName ?? "").Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
        if (name.Length == 0) name = "project";
        return $"{name}-falcon-{exportedAt.UtcDateTime:yyyyMMdd}-r{drawingRevision.ToString(CultureInfo.InvariantCulture)}.csv";
    }

    /// <summary>Checks everything that makes Kivo refuse the file; returns the problems in plain words (empty when the export may go ahead).</summary>
    public static List<string> Problems(OdpMeta meta, IReadOnlyList<FalconCsvEstimateLine> estimate, IReadOnlyList<FalconCsvMeasured> measured)
    {
        var p = new List<string>();
        if (string.IsNullOrWhiteSpace(meta.ProjectId)) p.Add("The project file has no project id.");
        if (estimate.Count == 0) p.Add("The estimate has no lines. Calculate an estimate first.");
        var noCode = estimate.Where(l => string.IsNullOrWhiteSpace(l.ItemCode)).Select(l => l.Description).ToList();
        if (noCode.Count > 0)
            p.Add($"{noCode.Count} estimate line(s) have no item code: {string.Join("; ", noCode.Take(5))}{(noCode.Count > 5 ? "…" : "")}.");
        var dup = estimate.Where(l => !string.IsNullOrWhiteSpace(l.ItemCode)).GroupBy(l => l.ItemCode.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dup.Count > 0) p.Add($"Item code(s) used on more than one estimate line (Kivo refuses that): {string.Join(", ", dup.Take(8))}.");
        if (measured.Any(m => string.IsNullOrWhiteSpace(m.ItemCode))) p.Add("A measured line has no item code.");
        return p;
    }

    /// <summary>Builds the file. Throws <see cref="FalconCsvException"/> with every problem found, writing nothing.</summary>
    public static byte[] Build(OdpMeta meta, IReadOnlyList<FalconCsvEstimateLine> estimate, IReadOnlyList<FalconCsvMeasured> measured,
        IReadOnlyList<OdpTakeoffRow> takeoff, DateTimeOffset exportedAt, string falconVersion, string currency = "INR")
    {
        var problems = Problems(meta, estimate, measured);
        if (problems.Count > 0) throw new FalconCsvException(string.Join(" ", problems));

        var rev = meta.Revision.ToString(CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        void Row(string type, Action<string[]> fill)
        {
            var c = new string[Header.Length];
            Array.Fill(c, "");
            c[0] = Schema; c[1] = type; c[2] = meta.ProjectId; c[3] = rev;
            fill(c);
            sb.Append(string.Join(",", c.Select(Quote))).Append("\r\n");
        }
        sb.Append(string.Join(",", Header)).Append("\r\n");

        Row("meta", c =>
        {
            c[6] = meta.Name;
            c[19] = $"currency={currency};exported={exportedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)};falcon={falconVersion}";
        });

        decimal total = 0;
        int count = 0;
        foreach (var l in estimate)
        {
            var q = Round(l.Quantity, 3);
            var r = Round(l.Rate, 2);
            var amount = Math.Round(q * r, 2, MidpointRounding.AwayFromZero);
            total += amount; count++;
            Row("estimate", c =>
            {
                c[4] = l.Package; c[5] = l.ItemCode.Trim(); c[6] = l.Description; c[7] = l.Unit;
                c[8] = Fixed(q, 3); c[9] = Fixed(r, 2); c[10] = Fixed(amount, 2); c[14] = l.Source;
            });
        }
        foreach (var m in measured)
        {
            var prev = Round(m.Previous, 3);
            var period = Round(m.ThisPeriod, 3);
            count++;
            Row("measured", c =>
            {
                c[5] = m.ItemCode.Trim(); c[6] = m.Description; c[7] = m.Unit;
                c[11] = Fixed(prev + period, 3); c[12] = Fixed(period, 3); c[13] = Fixed(prev, 3); c[14] = m.Source;
            });
        }
        foreach (var t in takeoff.OrderBy(t => t.Slot).ThenBy(t => t.Kind).ThenBy(t => t.N))
        {
            var row = TakeoffCells(t);
            if (row is null) continue;
            count++;
            Row("takeoff", c =>
            {
                c[7] = row.Value.Unit; c[8] = Fixed(Round(row.Value.Quantity, 3), 3); c[14] = $"tren:{t.Kind}";
                c[15] = row.Value.Kind; c[16] = row.Value.Tag; c[17] = row.Value.Size; c[18] = row.Value.Level;
            });
        }
        Row("total", c => { c[8] = count.ToString(CultureInfo.InvariantCulture); c[10] = Fixed(total, 2); });

        return new UTF8Encoding(true).GetPreamble().Concat(new UTF8Encoding(false).GetBytes(sb.ToString())).ToArray();
    }

    /// <summary>Reads the project file's meta and every take-off row (all drawings). Throws <see cref="FalconCsvException"/> when there is no usable file.</summary>
    public static (OdpMeta Meta, List<OdpTakeoffRow> Rows) ReadOsp(string? ospPath)
    {
        if (string.IsNullOrWhiteSpace(ospPath) || !File.Exists(ospPath))
            throw new FalconCsvException("This project has no project file (.osp). Open or link the .osp that Tren records into, then export again.");
        try
        {
            using var osp = OdpFile.Open(ospPath);
            var rows = new List<OdpTakeoffRow>();
            foreach (var h in osp.TakeoffHeaders()) rows.AddRange(osp.TakeoffRows(h.Slot));
            return (osp.Meta(), rows);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            throw new FalconCsvException($"{Path.GetFileName(ospPath)}: {e.Message}");
        }
    }

    // ---- take-off rows ----

    readonly record struct Cells(string Kind, string Tag, string Size, string Unit, double Quantity, string Level);

    static Cells? TakeoffCells(OdpTakeoffRow r)
    {
        var name = KindName(r.Kind) ?? (r.Kind is 1 or 9 ? null : $"kind{r.Kind}");
        if (name is null) return null;
        JsonObject? o;
        try { o = JsonNode.Parse(r.Json) as JsonObject; } catch (System.Text.Json.JsonException) { o = null; }
        o ??= new JsonObject();

        string S(string k) => o[k] is JsonValue v ? v.ToString() : "";
        double D(string k) => o[k] is JsonValue v && double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
        string N(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);
        string First(params string[] keys) => keys.Select(S).FirstOrDefault(s => s.Length > 0) ?? "";
        string By(string a, string b) => D(a) > 0 && D(b) > 0 ? $"{N(D(a))}x{N(D(b))}" : "";
        string Mm(string k) => D(k) > 0 ? N(D(k)) : "";

        var level = S("level");
        return r.Kind switch
        {
            3 => new(name, First("tag", "mark", "ref"), Mm("thickness_mm"), "m", D("length_mm") / 1000, level),
            4 => new(name, First("tag", "mark", "ref"), By("width_mm", "height_mm"), "nos", 1, level),
            5 => new(name, First("tag", "mark", "ref"), By("width_mm", "depth_mm"), "nos", 1, level),
            6 => new(name, First("tag", "mark", "ref"), By("width_mm", "depth_mm"), "nos", 1, level),
            7 => new(name, First("tag", "name", "ref"), "", "m2", D("area_m2"), level),
            8 => new(name, First("tag", "ref"), Mm("thickness_mm"), "m2", D("area_m2"), level),
            10 => new(name, First("tag", "switchboard", "ref"), "", "nos", 1, level),
            11 => new(name, First("tag", "point", "ref"), "", "nos", 1, level),
            12 => new(name, First("tag", "source", "ref"), First("size", "size_mm"), "m", D("pipe_m"), level),
            13 => new(name, First("tag", "fixture", "ref"), First("size", "size_mm"), "nos", 1, level),
            _ => new(name, First("tag", "ref"), "", "", 0, level),
        };
    }

    // ---- formatting ----

    static decimal Round(double v, int places) => Math.Round((decimal)v, places, MidpointRounding.AwayFromZero);
    static string Fixed(decimal v, int places) => v.ToString("F" + places, CultureInfo.InvariantCulture);

    static string Quote(string s) =>
        s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
