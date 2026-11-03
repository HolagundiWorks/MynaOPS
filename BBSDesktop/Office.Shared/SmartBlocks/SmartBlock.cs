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
public abstract record SmartItem(SmartNum? When);

public sealed record LineItem(SmartNum? When, SmartNum[] A) : SmartItem(When);
public sealed record RectItem(SmartNum? When, SmartNum[] A, bool Centred) : SmartItem(When);
public sealed record PolyItem(SmartNum? When, SmartNum[][] Pts, bool Closed) : SmartItem(When);
public sealed record CircleItem(SmartNum? When, SmartNum[] A) : SmartItem(When);
public sealed record DotItem(SmartNum? When, SmartNum[] A) : SmartItem(When);
public sealed record ArcItem(SmartNum? When, SmartNum[] A) : SmartItem(When);
public sealed record TextItem(SmartNum? When, SmartNum[] At, SmartNum Height, string Value, bool Centred, SmartNum? Rotation) : SmartItem(When);
public sealed record HatchItem(SmartNum? When, SmartNum[][] Pts, string Pattern, SmartNum? Scale) : SmartItem(When);
public sealed record RingItem(SmartNum? When, SmartNum[] C, SmartNum R, SmartNum Count, SmartNum Dot) : SmartItem(When);
public sealed record RepeatItem(SmartNum? When, string Var, SmartNum Count, IReadOnlyList<SmartItem> Items) : SmartItem(When);

/// <summary>A parametric block: parameters, derived values and the items that draw it (format <c>osas-smartblocks</c> version 1, as O-Draft reads it).</summary>
public sealed record SmartBlockDef(string Id, string Title, string Layer, IReadOnlyList<SmartParam> Params, IReadOnlyList<SmartVar> Vars, IReadOnlyList<SmartItem> Items)
{
    private static readonly string[] Shapes = ["line", "rect", "crect", "poly", "circle", "dot", "arc", "text", "hatch", "ring", "repeat"];

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

        private void Add(SmartPrim p)
        {
            if (Out.Count >= MaxObjects)
                throw new SmartBlockException($"`{def.Id}` makes more than {MaxObjects} objects");
            Out.Add(p);
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
                Item(it);
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
                sb.Append(Label(name) ?? "{" + name + "}");
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
            case "ring":
                return new RingItem(when, Nums(v.GetProperty("c")), Num(v.GetProperty("r")), Num(v.GetProperty("count")), Num(v.GetProperty("dot")));
            default:
                return new RepeatItem(when, v.GetProperty("var").GetString() ?? "i", Num(v.GetProperty("count")), v.GetProperty("items").EnumerateArray().Select(Item).ToList());
        }
    }
}

/// <summary>A drawn element of an evaluated block.</summary>
public abstract record SmartPrim;

public sealed record LinePrim(double X1, double Y1, double X2, double Y2) : SmartPrim;

public sealed record PolyPrim(IReadOnlyList<(double X, double Y)> Pts, bool Closed) : SmartPrim;

public sealed record CirclePrim(double X, double Y, double R) : SmartPrim;

/// <summary>A filled dot: a bar seen in section.</summary>
public sealed record DotPrim(double X, double Y, double R) : SmartPrim;

public sealed record ArcPrim(double X, double Y, double R, double StartDeg, double EndDeg) : SmartPrim;

public sealed record TextPrim(double X, double Y, double Height, string Value, bool Centred, double RotationDeg) : SmartPrim;

public sealed record HatchPrim(IReadOnlyList<(double X, double Y)> Pts, string Pattern, double Scale) : SmartPrim;

/// <summary>An evaluated block: its parameter and derived values, and what it draws.</summary>
public sealed record SmartGeometry(string Id, IReadOnlyDictionary<string, double> Values, IReadOnlyList<SmartPrim> Prims)
{
    /// <summary>The extents of the lines, polylines, circles, dots and arcs (not text or hatch), or null when there are none.</summary>
    public (double MinX, double MinY, double MaxX, double MaxY)? Bounds()
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
