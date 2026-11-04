// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Globalization;
using System.Text.Json;

namespace Office.Shared.SmartBlocks;

/// <summary>A number or a formula, as the definition wrote it.</summary>
public sealed record SmartNum(double? Value, SmartFormula? Formula)
{
    public double Eval(IReadOnlyDictionary<string, double> vars) => Value ?? Formula!.Evaluate(vars);
}

public sealed record SmartChoice(string Label, double Value);

public sealed record SmartParam(string Name, string Label, string Unit, double Default, double? Min, double? Max, bool Integer, IReadOnlyList<SmartChoice> Choices);

public sealed record SmartVar(string Name, SmartNum Expr);

/// <summary>One drawing item of a definition: a shape, shown only when <see cref="When"/> is above zero.</summary>
public abstract record SmartItem(SmartNum? When)
{
    /// <summary>An AutoCAD colour index for this item (bars by diameter, ties, dimensions); null for the reference's colour.</summary>
    public int? Color { get; init; }
}

public sealed record LineItem(SmartNum? When, SmartNum[] A) : SmartItem(When);
public sealed record RectItem(SmartNum? When, SmartNum[] A, bool Centred) : SmartItem(When);
public sealed record PolyItem(SmartNum? When, SmartNum[][] Pts, bool Closed) : SmartItem(When);
public sealed record CircleItem(SmartNum? When, SmartNum[] A) : SmartItem(When);
public sealed record DotItem(SmartNum? When, SmartNum[] A) : SmartItem(When);
public sealed record ArcItem(SmartNum? When, SmartNum[] A) : SmartItem(When);
public sealed record TextItem(SmartNum? When, SmartNum[] At, SmartNum Height, string Value, bool Centred, SmartNum? Rotation) : SmartItem(When);
public sealed record HatchItem(SmartNum? When, SmartNum[][] Pts, string Pattern, SmartNum? Scale) : SmartItem(When);
public sealed record RingItem(SmartNum? When, SmartNum[] C, SmartNum R, SmartNum Count, SmartNum Dot) : SmartItem(When);
public sealed record DimItem(SmartNum? When, SmartNum[] From, SmartNum[] To, SmartNum Off, SmartNum Height, string? Text) : SmartItem(When);
public sealed record LeaderItem(SmartNum? When, SmartNum[] From, SmartNum[] To, SmartNum Height, string Text) : SmartItem(When);
public sealed record RepeatItem(SmartNum? When, string Var, SmartNum Count, IReadOnlyList<SmartItem> Items) : SmartItem(When);

/// <summary>A parametric block: parameters, derived values and the items that draw it (format <c>osas-smartblocks</c> version 1, as O-Draft reads it).</summary>
public sealed record SmartBlockDef(string Id, string Title, string Layer, IReadOnlyList<SmartParam> Params, IReadOnlyList<SmartVar> Vars, IReadOnlyList<SmartItem> Items)
{
    private static readonly string[] Shapes = ["line", "rect", "crect", "poly", "circle", "dot", "arc", "text", "hatch", "ring", "dim", "leader", "repeat"];

    public SmartParam? Param(string name) => Params.FirstOrDefault(p => p.Name == name);

    /// <summary>The parameters of a block: defaults, then <paramref name="given"/>. A value is made whole, snapped to the nearest choice, and kept in range.</summary>
    public Dictionary<string, double> Resolve(IReadOnlyDictionary<string, double>? given = null)
    {
        var values = new Dictionary<string, double>();
        foreach (var p in Params)
        {
            var v = p.Default;
            if (given is not null && given.TryGetValue(p.Name, out var g) && double.IsFinite(g))
                v = g;
            if (p.Integer)
                v = Math.Round(v);
            if (p.Choices.Count > 0)
                v = p.Choices.OrderBy(c => Math.Abs(c.Value - v)).First().Value;
            else
            {
                if (p.Min is { } lo && v < lo)
                    v = lo;
                if (p.Max is { } hi && v > hi)
                    v = hi;
            }
            values[p.Name] = v;
        }
        return values;
    }

    /// <summary>The drawing of the block for the parameter values (millimetres, about the insertion point).</summary>
    public SmartGeometry Evaluate(IReadOnlyDictionary<string, double>? given = null)
    {
        var vars = Resolve(given);
        foreach (var v in Vars)
            vars[v.Name] = v.Expr.Eval(vars);
        var cx = new Eval(this, vars);
        cx.Items(Items);
        return new SmartGeometry(Id, vars, cx.Out);
    }

    private sealed class Eval(SmartBlockDef def, Dictionary<string, double> vars)
    {
        private const int MaxObjects = 5000;
        private const double MaxRepeat = 1000;

        public List<SmartPrim> Out { get; } = [];

        private double N(SmartNum n)
        {
            var v = n.Eval(vars);
            return double.IsFinite(v) && Math.Abs(v) <= 1e9 ? v : throw new SmartBlockException($"`{def.Id}`: a value is out of range");
        }

        private (double X, double Y) Pt(SmartNum[] p) => (N(p[0]), N(p[1]));

        private int? color;

        private void Add(SmartPrim p)
        {
            if (Out.Count >= MaxObjects)
                throw new SmartBlockException($"`{def.Id}` makes more than {MaxObjects} objects");
            Out.Add(p with { Color = color });
        }

        /// <summary>Whether lengths are shown in feet and inches (the <c>ftin</c> parameter above zero) rather than millimetres.</summary>
        private string Length(double mm) => vars.TryGetValue("ftin", out var f) && f > 0 ? FeetInches(mm) : FormatNumber(mm);

        private void Dim(DimItem d)
        {
            const int orange = 30;
            color = d.Color ?? orange;
            var (a, b, off, h) = (Pt(d.From), Pt(d.To), N(d.Off), N(d.Height));
            if (h <= 0)
                throw new SmartBlockException($"`{def.Id}`: dimension text height must be positive");
            var horizontal = Math.Abs(b.X - a.X) >= Math.Abs(b.Y - a.Y);
            var len = horizontal ? Math.Abs(b.X - a.X) : Math.Abs(b.Y - a.Y);
            if (len < 1e-6)
                return;
            // m runs along the dimension and n across it; the dimension line is at off across.
            (double X, double Y) At(double m, double n) => horizontal ? (m, n) : (n, m);
            var (m0, m1) = horizontal ? (a.X, b.X) : (a.Y, b.Y);
            var (n0, n1) = horizontal ? (a.Y, b.Y) : (a.X, b.X);
            var side = off < 0 ? -1.0 : 1.0;
            var lineN = n0 + off;
            void Seg((double X, double Y) p, (double X, double Y) q) => Add(new LinePrim(p.X, p.Y, q.X, q.Y));
            Seg(At(m0, lineN), At(m1, lineN));
            foreach (var (m, n) in new[] { (m0, n0), (m1, n1) })
            {
                Seg(At(m, n + side * h * 0.3), At(m, lineN + side * h * 0.5));
                var t = h * 0.45;
                Seg(At(m - t, lineN - t), At(m + t, lineN + t));
            }
            var value = d.Text is { } text ? Substitute(text) : Length(len);
            var mid = (m0 + m1) / 2;
            if (horizontal)
            {
                var (x, y) = side > 0 ? (mid, lineN + h * 0.35) : (mid, lineN - h * 0.35);
                Add(new TextPrim(x, y, h, value, false, 0, "c", side > 0 ? "b" : "t"));
            }
            else
            {
                var (x, y) = At(mid, lineN + side * h * 0.35);
                Add(new TextPrim(x, y, h, value, false, 90, "c", side > 0 ? "t" : "b"));
            }
        }

        private void Leader(LeaderItem l)
        {
            const int orange = 30;
            color = l.Color ?? orange;
            var (a, b, h) = (Pt(l.From), Pt(l.To), N(l.Height));
            if (h <= 0)
                throw new SmartBlockException($"`{def.Id}`: leader text height must be positive");
            var side = b.X < a.X ? -1.0 : 1.0;
            var land = (X: b.X + side * h * 1.5, Y: b.Y);
            Add(new PolyPrim([a, b, land], false));
            Add(new TextPrim(land.X + side * h * 0.3, land.Y - h * 0.35, h, Substitute(l.Text), false, 0, side < 0 ? "r" : "l", "b"));
        }

        private int Count(SmartNum n)
        {
            var c = Math.Round(N(n), MidpointRounding.AwayFromZero);
            return c is >= 0 and <= MaxRepeat ? (int)c : throw new SmartBlockException($"`{def.Id}`: a repeat count must be 0 to {MaxRepeat}");
        }

        public void Items(IReadOnlyList<SmartItem> items)
        {
            foreach (var it in items)
            {
                if (it.When is { } w && N(w) <= 0)
                    continue;
                color = it.Color;
                Item(it);
                color = null;
            }
        }

        private void Item(SmartItem it)
        {
            switch (it)
            {
                case LineItem l:
                    Add(new LinePrim(N(l.A[0]), N(l.A[1]), N(l.A[2]), N(l.A[3])));
                    break;
                case RectItem r:
                {
                    var (x, y, w, h) = (N(r.A[0]), N(r.A[1]), N(r.A[2]), N(r.A[3]));
                    if (r.Centred)
                        (x, y) = (x - w / 2, y - h / 2);
                    Add(new PolyPrim([(x, y), (x + w, y), (x + w, y + h), (x, y + h)], true));
                    break;
                }
                case PolyItem p:
                {
                    var pts = p.Pts.Select(Pt).ToList();
                    if (pts.Count < 2)
                        throw new SmartBlockException($"`{def.Id}`: a polyline needs two points");
                    Add(new PolyPrim(pts, p.Closed));
                    break;
                }
                case CircleItem c:
                {
                    var r = N(c.A[2]);
                    if (r > 0)
                        Add(new CirclePrim(N(c.A[0]), N(c.A[1]), r));
                    break;
                }
                case DotItem d:
                {
                    var r = N(d.A[2]);
                    if (r > 0)
                        Add(new DotPrim(N(d.A[0]), N(d.A[1]), r));
                    break;
                }
                case ArcItem a:
                {
                    var r = N(a.A[2]);
                    if (r > 0)
                        Add(new ArcPrim(N(a.A[0]), N(a.A[1]), r, N(a.A[3]), N(a.A[4])));
                    break;
                }
                case TextItem t:
                {
                    var h = N(t.Height);
                    if (h <= 0)
                        throw new SmartBlockException($"`{def.Id}`: text height must be positive");
                    var (x, y) = Pt(t.At);
                    Add(new TextPrim(x, y, h, Substitute(t.Value), t.Centred, t.Rotation is { } rot ? N(rot) : 0));
                    break;
                }
                case HatchItem h:
                {
                    var pts = h.Pts.Select(Pt).ToList();
                    if (pts.Count < 3)
                        throw new SmartBlockException($"`{def.Id}`: a hatch needs three points");
                    var scale = h.Scale is { } s ? N(s) : 1.0;
                    if (scale <= 0)
                        throw new SmartBlockException($"`{def.Id}`: hatch scale must be positive");
                    Add(new HatchPrim(pts, h.Pattern.ToUpperInvariant(), scale));
                    break;
                }
                case RingItem g:
                {
                    var (cx, cy) = Pt(g.C);
                    var (rad, dr) = (N(g.R), N(g.Dot));
                    var n = Count(g.Count);
                    for (var i = 0; i < n; i++)
                    {
                        var a = Math.Tau * i / n + Math.PI / 2;
                        Add(new DotPrim(cx + rad * Math.Cos(a), cy + rad * Math.Sin(a), dr));
                    }
                    break;
                }
                case DimItem d:
                    Dim(d);
                    break;
                case LeaderItem l:
                    Leader(l);
                    break;
                case RepeatItem rep:
                {
                    var n = Count(rep.Count);
                    var had = vars.TryGetValue(rep.Var, out var saved);
                    for (var i = 0; i < n; i++)
                    {
                        vars[rep.Var] = i;
                        Items(rep.Items);
                    }
                    if (had)
                        vars[rep.Var] = saved;
                    else
                        vars.Remove(rep.Var);
                    break;
                }
            }
        }

        private string Substitute(string t)
        {
            var sb = new System.Text.StringBuilder();
            var rest = t.AsSpan();
            while (true)
            {
                var i = rest.IndexOf('{');
                if (i < 0)
                    break;
                sb.Append(rest[..i]);
                var after = rest[(i + 1)..];
                var j = after.IndexOf('}');
                if (j < 0)
                {
                    sb.Append(rest[i..]);
                    rest = [];
                    break;
                }
                var name = after[..j].ToString();
                var shown = name.EndsWith(":len", StringComparison.Ordinal) ? (vars.TryGetValue(name[..^4], out var mm) ? Length(mm) : null) : Label(name);
                sb.Append(shown ?? "{" + name + "}");
                rest = after[(j + 1)..];
            }
            sb.Append(rest);
            return sb.ToString();
        }

        private string? Label(string name)
        {
            if (!vars.TryGetValue(name, out var v))
                return null;
            var p = def.Param(name);
            var c = p?.Choices.FirstOrDefault(c => c.Value == v);
            return c is not null ? c.Label : FormatNumber(v);
        }
    }

    /// <summary>A length in feet and inches to the nearest half inch: <c>1'-6"</c>, <c>9"</c>, <c>1.5"</c>.</summary>
    public static string FeetInches(double mm)
    {
        var inches = Math.Round(mm / 25.4 * 2, MidpointRounding.AwayFromZero) / 2;
        var feet = Math.Floor(inches / 12);
        var rest = FormatNumber(inches - feet * 12);
        return feet >= 1 ? $"{FormatNumber(feet)}'-{rest}\"" : $"{rest}\"";
    }

    public static string FormatNumber(double v)
    {
        var r = Math.Round(v * 100) / 100;
        return r == Math.Floor(r) ? ((long)r).ToString(CultureInfo.InvariantCulture) : r.ToString(CultureInfo.InvariantCulture);
    }

    // ---- reading ----

    /// <summary>The blocks of a library file (<c>format</c> <c>osas-smartblocks</c>, <c>version</c> 1).</summary>
    public static IReadOnlyList<SmartBlockDef> ReadLibrary(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.GetProperty("format").GetString() != "osas-smartblocks" || root.GetProperty("version").GetInt32() != 1)
            throw new SmartBlockException("not an osas-smartblocks version 1 file");
        return root.GetProperty("blocks").EnumerateArray().Select(Block).ToList();
    }

    private static SmartBlockDef Block(JsonElement e)
    {
        var id = e.GetProperty("id").GetString() ?? "";
        try
        {
            var ps = e.TryGetProperty("params", out var pa) ? pa.EnumerateArray().Select(Param).ToList() : [];
            var vs = e.TryGetProperty("vars", out var va)
                ? va.EnumerateArray().Select(v => new SmartVar(v.GetProperty("name").GetString() ?? "", Num(v.GetProperty("expr")))).ToList()
                : [];
            return new SmartBlockDef(
                id,
                e.TryGetProperty("title", out var t) ? t.GetString() ?? id : id,
                e.TryGetProperty("layer", out var l) ? l.GetString() ?? "" : "",
                ps,
                vs,
                e.GetProperty("items").EnumerateArray().Select(Item).ToList());
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new SmartBlockException($"smart block `{id}`: {ex.Message}");
        }
    }

    private static double? Opt(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static SmartParam Param(JsonElement e) => new(
        e.GetProperty("name").GetString() ?? "",
        e.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "",
        e.TryGetProperty("unit", out var u) ? u.GetString() ?? "" : "",
        e.GetProperty("default").GetDouble(),
        Opt(e, "min"),
        Opt(e, "max"),
        e.TryGetProperty("integer", out var i) && i.GetBoolean(),
        e.TryGetProperty("choices", out var c)
            ? c.EnumerateArray().Select(x => new SmartChoice(x.GetProperty("label").GetString() ?? "", x.GetProperty("value").GetDouble())).ToList()
            : []);

    private static SmartNum Num(JsonElement e) => e.ValueKind == JsonValueKind.Number
        ? new SmartNum(e.GetDouble(), null)
        : new SmartNum(null, SmartFormula.Parse(e.GetString() ?? ""));

    private static SmartNum[] Nums(JsonElement e) => e.EnumerateArray().Select(Num).ToArray();

    private static SmartNum[][] Pts(JsonElement e) => e.EnumerateArray().Select(Nums).ToArray();

    private static SmartItem Item(JsonElement e)
    {
        var when = e.TryGetProperty("when", out var w) ? Num(w) : null;
        var shape = Shapes.FirstOrDefault(s => e.TryGetProperty(s, out _)) ?? throw new SmartBlockException("an item has no shape");
        var v = e.GetProperty(shape);
        var color = e.TryGetProperty("color", out var cc) && cc.ValueKind == JsonValueKind.Number ? cc.GetInt32() : (int?)null;
        return (Shaped(shape, when, v, e)) with { Color = color };
    }

    private static SmartItem Shaped(string shape, SmartNum? when, JsonElement v, JsonElement e)
    {
        switch (shape)
        {
            case "line": return new LineItem(when, Nums(v));
            case "rect": return new RectItem(when, Nums(v), false);
            case "crect": return new RectItem(when, Nums(v), true);
            case "poly": return new PolyItem(when, Pts(v.GetProperty("pts")), v.TryGetProperty("closed", out var c) && c.GetBoolean());
            case "circle": return new CircleItem(when, Nums(v));
            case "dot": return new DotItem(when, Nums(v));
            case "arc": return new ArcItem(when, Nums(v));
            case "text":
                return new TextItem(
                    when,
                    Nums(v.GetProperty("at")),
                    Num(v.GetProperty("height")),
                    v.GetProperty("value").GetString() ?? "",
                    v.TryGetProperty("align", out var a) && a.GetString() == "c",
                    v.TryGetProperty("rotation", out var r) ? Num(r) : null);
            case "hatch":
                return new HatchItem(when, Pts(v.GetProperty("pts")), v.GetProperty("pattern").GetString() ?? "SOLID", v.TryGetProperty("scale", out var s) ? Num(s) : null);
            case "dim":
                return new DimItem(when, Nums(v.GetProperty("from")), Nums(v.GetProperty("to")), Num(v.GetProperty("off")), Num(v.GetProperty("height")), v.TryGetProperty("text", out var dt) ? dt.GetString() : null);
            case "leader":
                return new LeaderItem(when, Nums(v.GetProperty("from")), Nums(v.GetProperty("to")), Num(v.GetProperty("height")), v.GetProperty("text").GetString() ?? "");
            case "ring":
                return new RingItem(when, Nums(v.GetProperty("c")), Num(v.GetProperty("r")), Num(v.GetProperty("count")), Num(v.GetProperty("dot")));
            default:
                return new RepeatItem(when, v.GetProperty("var").GetString() ?? "i", Num(v.GetProperty("count")), v.GetProperty("items").EnumerateArray().Select(Item).ToList());
        }
    }
}

/// <summary>A drawn element of an evaluated block.</summary>
public abstract record SmartPrim
{
    /// <summary>An AutoCAD colour index, or null for the colour of the reference.</summary>
    public int? Color { get; init; }
}

public sealed record LinePrim(double X1, double Y1, double X2, double Y2) : SmartPrim;

public sealed record PolyPrim(IReadOnlyList<(double X, double Y)> Pts, bool Closed) : SmartPrim;

public sealed record CirclePrim(double X, double Y, double R) : SmartPrim;

/// <summary>A filled dot: a bar seen in section.</summary>
public sealed record DotPrim(double X, double Y, double R) : SmartPrim;

public sealed record ArcPrim(double X, double Y, double R, double StartDeg, double EndDeg) : SmartPrim;

/// <summary>Text at a point. <see cref="Centred"/> is the older middle-centre anchor; otherwise <see cref="H"/> (l, c, r) and <see cref="V"/> (b baseline, t top, m middle) say where the point is.</summary>
public sealed record TextPrim(double X, double Y, double Height, string Value, bool Centred, double RotationDeg, string H = "l", string V = "b") : SmartPrim;

public sealed record HatchPrim(IReadOnlyList<(double X, double Y)> Pts, string Pattern, double Scale) : SmartPrim;

/// <summary>An evaluated block: its parameter and derived values, and what it draws.</summary>
public sealed record SmartGeometry(string Id, IReadOnlyDictionary<string, double> Values, IReadOnlyList<SmartPrim> Prims)
{
    /// <summary>The box a text takes, from its anchor and an average character width of 0.6 of its height.</summary>
    public static (double MinX, double MinY, double MaxX, double MaxY) TextBox(TextPrim t)
    {
        var w = t.Value.Length * t.Height * 0.6;
        var (h, v) = t.Centred ? ("c", "m") : (t.H, t.V);
        var x0 = h switch { "c" => -w / 2, "r" => -w, _ => 0 };
        var y0 = v switch { "m" => -t.Height / 2, "t" => -t.Height, _ => 0 };
        var (x1, y1) = (x0 + w, y0 + t.Height);
        if (Math.Abs(t.RotationDeg - 90) < 1e-6)
            (x0, y0, x1, y1) = (-y1, x0, -y0, x1);
        return (t.X + x0, t.Y + y0, t.X + x1, t.Y + y1);
    }

    /// <summary>The extents of the lines, polylines, circles, dots and arcs (and the boxes of the texts when <paramref name="withText"/>), or null when there are none.</summary>
    public (double MinX, double MinY, double MaxX, double MaxY)? Bounds(bool withText = false)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        void Grow(double x, double y)
        {
            x0 = Math.Min(x0, x);
            y0 = Math.Min(y0, y);
            x1 = Math.Max(x1, x);
            y1 = Math.Max(y1, y);
        }
        foreach (var p in Prims)
        {
            switch (p)
            {
                case LinePrim l:
                    Grow(l.X1, l.Y1);
                    Grow(l.X2, l.Y2);
                    break;
                case PolyPrim q:
                    foreach (var (x, y) in q.Pts)
                        Grow(x, y);
                    break;
                case CirclePrim c:
                    Grow(c.X - c.R, c.Y - c.R);
                    Grow(c.X + c.R, c.Y + c.R);
                    break;
                case DotPrim d:
                    Grow(d.X - d.R, d.Y - d.R);
                    Grow(d.X + d.R, d.Y + d.R);
                    break;
                case ArcPrim a:
                    Grow(a.X - a.R, a.Y - a.R);
                    Grow(a.X + a.R, a.Y + a.R);
                    break;
                case TextPrim t when withText:
                {
                    var b = TextBox(t);
                    Grow(b.MinX, b.MinY);
                    Grow(b.MaxX, b.MaxY);
                    break;
                }
            }
        }
        return x0 <= x1 ? (x0, y0, x1, y1) : null;
    }

    /// <summary>How many primitives there are of each kind, by name (line, poly, circle, dot, arc, text, hatch).</summary>
    public IReadOnlyDictionary<string, int> Counts() => Prims
        .GroupBy(p => p switch
        {
            LinePrim => "line",
            PolyPrim => "poly",
            CirclePrim => "circle",
            DotPrim => "dot",
            ArcPrim => "arc",
            TextPrim => "text",
            _ => "hatch",
        })
        .ToDictionary(g => g.Key, g => g.Count());
}

/// <summary>The built-in reinforced concrete blocks (a copy of O-Draft's <c>assets/smartblocks/rcc.json</c>), read once.</summary>
public static class SmartBlockLibrary
{
    private static readonly Lazy<IReadOnlyList<SmartBlockDef>> Rcc = new(() =>
    {
        var asm = typeof(SmartBlockLibrary).Assembly;
        using var s = asm.GetManifestResourceStream("Office.Shared.SmartBlocks.rcc.json")
            ?? throw new SmartBlockException("the RCC block library is missing");
        using var r = new StreamReader(s);
        return SmartBlockDef.ReadLibrary(r.ReadToEnd());
    });

    public static IReadOnlyList<SmartBlockDef> All => Rcc.Value;

    public static SmartBlockDef? Find(string id) => Rcc.Value.FirstOrDefault(b => b.Id == id);
}
