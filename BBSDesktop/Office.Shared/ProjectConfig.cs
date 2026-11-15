// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Office.Shared;

// The project configuration kept in the .osp (meta.config.*): the contract between MynaOps (the only writer) and MynaCAD (reads and applies it).
// This is the .NET side of aad_odp::config (MynaCAD's Rust crate): the same keys, the same rules, tested against the same JSON fixtures
// (Office.Shared.Tests/Fixtures/config). Spec: docs/ops-first-workflow.md sections 4.1 to 4.5; file layout: docs/PROJECT-FILE.md.
//
// Rules every reader follows:
//  * every key's value is JSON text of at most MaxValueBytes; a longer one is an error before any of it is read;
//  * every key carries "v"; a key whose version this reader does not know is treated as absent and says so in the notes;
//  * unknown fields are ignored; counts, lengths, numbers and ids are checked, and a part that breaks a rule rejects the whole configuration;
//  * input is hostile: a bad file gives a ConfigException, never anything else.

/// <summary>A part of the configuration breaks a rule. <see cref="Key"/> is the meta key (or a path such as <c>config.levels.items[2]</c>).</summary>
public class ConfigException : Exception
{
    public string Key { get; }
    public ConfigException(string key, string message) : base($"{key}: {message}") => Key = key;
}

/// <summary>A key's value is longer than <see cref="ProjectConfig.MaxValueBytes"/>.</summary>
public sealed class ConfigTooLargeException : ConfigException
{
    public ConfigTooLargeException(string key, long bytes) : base(key, $"the value is {bytes} bytes; the limit is {ProjectConfig.MaxValueBytes}") { }
}

/// <summary><c>config.schema.min_reader</c> is newer than this reader.</summary>
public sealed class ConfigTooNewException : ConfigException
{
    public long Needs { get; }
    public ConfigTooNewException(long needs) : base("config.schema", $"the project configuration needs a reader of version {needs}; this one reads version {ProjectConfig.ReaderVersion}: update the program") => Needs = needs;
}

/// <summary><c>config.schema</c>: presence means "this project is configured by MynaOps".</summary>
public sealed record ConfigSchema { public long V { get; init; } = 1; public long MinReader { get; init; } = 1; }

/// <summary><c>config.rev</c>: rises by one per configuration change.</summary>
public sealed record ConfigRev { public long V { get; init; } = 1; public ulong Rev { get; init; } public string App { get; init; } = ""; public string Time { get; init; } = ""; }

/// <summary><c>config.project</c>: the project detail keys of the drawing (same names), plus code and persona.</summary>
public sealed record ConfigProject
{
    public string Title { get; init; } = "";
    public string Client { get; init; } = "";
    public string Owner { get; init; } = "";
    public string Location { get; init; } = "";
    public string Address { get; init; } = "";
    public string Pid { get; init; } = "";
    public string SiteArea { get; init; } = "";
    public string PlotUse { get; init; } = "";
    public string Authority { get; init; } = "";
    public string Date { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Code { get; init; } = "";
    public string Persona { get; init; } = "";
}

/// <summary><c>config.team</c>.</summary>
public sealed record ConfigTeam
{
    public string Architect { get; init; } = "";
    public string Engineer { get; init; } = "";
    /// <summary>The imported company pack: text values by name.</summary>
    public IReadOnlyDictionary<string, string> Company { get; init; } = new SortedDictionary<string, string>(StringComparer.Ordinal);
}

/// <summary><c>config.units</c>. <see cref="Locked"/> is set once MynaCAD reports content: the unit can no longer change.</summary>
public sealed record ConfigUnits { public string DrawingUnit { get; init; } = "mm"; public bool Locked { get; init; } }

/// <summary>Defaults for levels, in millimetres.</summary>
public sealed record ConfigLevelDefaults
{
    public double? HeightMm { get; init; }
    public double? SlabMm { get; init; }
    public double? BeamMm { get; init; }
    public double? CeilingMm { get; init; }
    public double? LintelBottomMm { get; init; }
}

/// <summary>One configured level. <see cref="Cid"/> is MynaOps' id and is never reused.</summary>
public sealed record ConfigLevelItem
{
    public uint Cid { get; init; }
    public string Name { get; init; } = "";
    public string Code { get; init; } = "";
    public double ElevationMm { get; init; }
    public double HeightMm { get; init; }
    public double? SlabMm { get; init; }
    public double? BeamMm { get; init; }
    public double? CeilingMm { get; init; }
    public double? LintelBottomMm { get; init; }
}

/// <summary><c>config.levels</c>: the level list, ordered bottom to top.</summary>
public sealed record ConfigLevels
{
    public uint NextCid { get; init; } = 1;
    public bool OnePerFloor { get; init; }
    public ConfigLevelDefaults Defaults { get; init; } = new();
    public IReadOnlyList<ConfigLevelItem> Items { get; init; } = Array.Empty<ConfigLevelItem>();
}

/// <summary><c>config.layer_sets</c>: which hcwlayers sets exist (standard, permit, presentation).</summary>
public sealed record ConfigLayerSets { public IReadOnlyList<string> Sets { get; init; } = Array.Empty<string>(); }

/// <summary>One discipline tab (a sheet set). <see cref="Tid"/> is never reused.</summary>
public sealed record ConfigTabItem
{
    public uint Tid { get; init; }
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string LayerPrefix { get; init; } = "";
    public IReadOnlyList<string> Layers { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Blocks { get; init; } = Array.Empty<string>();
}

/// <summary><c>config.tabs</c>. <see cref="NextTid"/> is optional: when absent it is one more than the largest tid.</summary>
public sealed record ConfigTabs { public uint? NextTid { get; init; } public IReadOnlyList<ConfigTabItem> Items { get; init; } = Array.Empty<ConfigTabItem>(); }

/// <summary>One discipline of the sheet numbering scheme.</summary>
public sealed record ConfigDiscipline { public string Code { get; init; } = ""; public string Name { get; init; } = ""; public uint Start { get; init; } }

/// <summary>The sheet numbering scheme (the arguments of MynaCAD's <c>sheet.scheme</c>).</summary>
public sealed record ConfigScheme
{
    public string Pattern { get; init; } = "{D}-{N}";
    public uint Width { get; init; } = 3;
    public uint Step { get; init; } = 1;
    public IReadOnlyList<ConfigDiscipline> Disciplines { get; init; } = Array.Empty<ConfigDiscipline>();
}

/// <summary><c>config.sheets</c>.</summary>
public sealed record ConfigSheets { public ConfigScheme Scheme { get; init; } = new(); }

/// <summary><c>config.naming</c>: file and drawing number patterns.</summary>
public sealed record ConfigNaming { public string File { get; init; } = ""; public string DrawingNumber { get; init; } = ""; }

/// <summary><c>config.titleblock</c>: project-wide title-block field defaults.</summary>
public sealed record ConfigTitleBlock { public IReadOnlyDictionary<string, string> Fields { get; init; } = new SortedDictionary<string, string>(StringComparer.Ordinal); }

/// <summary>One result line of an apply.</summary>
public sealed record ConfigAppliedItem { public string Key { get; init; } = ""; public string Status { get; init; } = ""; public string Detail { get; init; } = ""; }

/// <summary>
/// <c>config.applied.cad</c>: what MynaCAD has applied (a local key MynaCAD writes, no revision). MynaOps shows "MynaCAD has applied revision N".
/// <see cref="Result"/> is ok, partial or failed.
/// </summary>
public sealed record ConfigApplied
{
    public ulong Rev { get; init; }
    public string Time { get; init; } = "";
    public string Result { get; init; } = "";
    public bool HasContent { get; init; }
    public IReadOnlyList<ConfigAppliedItem> Items { get; init; } = Array.Empty<ConfigAppliedItem>();
}

/// <summary>A level a legacy drawing already has (<see cref="Cid"/> is its LevelId).</summary>
public sealed record ConfigProposalLevel { public uint Cid { get; init; } public string Name { get; init; } = ""; public double ElevationMm { get; init; } public double HeightMm { get; init; } }

/// <summary>A discipline tab a legacy drawing already has.</summary>
public sealed record ConfigProposalTab
{
    /// <summary>The sheet set's own id in MynaCAD: an adopted tab's <c>tid</c>.</summary>
    public uint Tid { get; init; }
    public string Name { get; init; } = "";
    public string LayerPrefix { get; init; } = "";
    public IReadOnlyList<string> Layers { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Blocks { get; init; } = Array.Empty<string>();
}

/// <summary><c>config.proposal</c>: what a legacy drawing already has, written once by MynaCAD (a local key) for the Adopt page.</summary>
public sealed record ConfigProposal
{
    public string Time { get; init; } = "";
    public bool HasContent { get; init; }
    public string? DrawingUnit { get; init; }
    public IReadOnlyList<ConfigProposalLevel> Levels { get; init; } = Array.Empty<ConfigProposalLevel>();
    public IReadOnlyList<ConfigProposalTab> Tabs { get; init; } = Array.Empty<ConfigProposalTab>();
    public IReadOnlyList<string> LayerSets { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> Project { get; init; } = new SortedDictionary<string, string>(StringComparer.Ordinal);
    public ConfigScheme? Scheme { get; init; }
}

/// <summary>The configuration read from its keys, with what was not understood (one line per key left out).</summary>
public sealed record ConfigRead(ProjectConfig Config, IReadOnlyList<string> Notes);

/// <summary>Everything the configuration keys of one file say.</summary>
public sealed record ConfigLoaded(ConfigRead Read, ConfigApplied? Applied, ConfigProposal? Proposal, long Revision)
{
    public bool Configured => Read.Config.Configured;
}

/// <summary>What <see cref="ProjectConfig.Write"/> did.</summary>
public sealed record ConfigWritten(long Revision, ulong Rev, IReadOnlyList<string> Changed);

/// <summary>
/// The configuration of a project: every part MynaOps has written. A part that is <c>null</c> is absent (not written, or a newer version).
/// Read with <see cref="Read(OdpFile)"/> or <see cref="Probe"/>, change and write with <see cref="Write"/> (one revision per write).
/// </summary>
public sealed record ProjectConfig
{
    public const long ReaderVersion = 1;
    public const int MaxValueBytes = 256 * 1024;
    public const string Prefix = "config.";
    /// <summary>The short names of the keys MynaOps writes, in the order they are written. The meta key is <c>config.&lt;name&gt;</c>.</summary>
    public static readonly IReadOnlyList<string> Parts = new[] { "schema", "rev", "project", "team", "units", "levels", "layer_sets", "tabs", "sheets", "naming", "titleblock" };
    public const string KeyApplied = "config.applied.cad";
    public const string KeyProposal = "config.proposal";
    public static readonly IReadOnlyList<string> LayerSetNames = new[] { "standard", "permit", "presentation" };
    public static readonly IReadOnlyList<string> UnitNames = new[] { "mm", "cm", "m", "in", "ft" };

    public const int MaxLevels = 64, MaxTabs = 32, MaxDisciplines = 64, MaxPatterns = 64, MaxPatternLen = 64, MaxName = 64, MaxText = 256, MaxLongText = 4096, MaxMapEntries = 64, MaxItemsReport = 256;

    public ConfigSchema? Schema { get; init; }
    public ConfigRev? RevPart { get; init; }
    public ConfigProject? Project { get; init; }
    public ConfigTeam? Team { get; init; }
    public ConfigUnits? Units { get; init; }
    public ConfigLevels? Levels { get; init; }
    public ConfigLayerSets? LayerSets { get; init; }
    public ConfigTabs? Tabs { get; init; }
    public ConfigSheets? Sheets { get; init; }
    public ConfigNaming? Naming { get; init; }
    public ConfigTitleBlock? TitleBlock { get; init; }

    /// <summary>Whether the project is configured by MynaOps (<c>config.schema</c> is present).</summary>
    public bool Configured => Schema != null;

    /// <summary>The configuration revision (<c>config.rev.rev</c>), 0 when there is none.</summary>
    public ulong Revision => RevPart?.Rev ?? 0;

    /// <summary>A configuration with the schema, for a new project. <c>rev</c> is left for <see cref="Write"/>.</summary>
    public static ProjectConfig New() => new() { Schema = new ConfigSchema() };

    // ------------------------------------------------------------------------------------------------ parse

    /// <summary>
    /// Build the configuration from key texts (<paramref name="parts"/> maps the short name, <c>levels</c>, to the key's JSON text; names that are not parts
    /// are ignored). Any part that breaks a rule rejects the whole; a part with a newer <c>v</c> is left out and noted.
    /// </summary>
    public static ConfigRead FromTexts(IReadOnlyDictionary<string, string> parts)
    {
        var notes = new List<string>();
        T? Take<T>(string name, Func<JsonElement, string, T> read) where T : class
        {
            if (!parts.TryGetValue(name, out var text)) return null;
            var key = Prefix + name;
            using var doc = Open(key, text, out var newer);
            if (newer is { } v) { notes.Add($"{key} is version {v}; this reader knows {ReaderVersion}: left out"); return null; }
            return read(doc!.RootElement, key);
        }
        // The schema first: a project that needs a newer reader is refused before anything else is looked at.
        var cfg = new ProjectConfig
        {
            Schema = Take("schema", ReadSchema),
            RevPart = Take("rev", ReadRev),
            Project = Take("project", ReadProject),
            Team = Take("team", ReadTeam),
            Units = Take("units", ReadUnits),
            Levels = Take("levels", ReadLevels),
            LayerSets = Take("layer_sets", ReadLayerSets),
            Tabs = Take("tabs", ReadTabs),
            Sheets = Take("sheets", ReadSheets),
            Naming = Take("naming", ReadNaming),
            TitleBlock = Take("titleblock", ReadTitleBlock),
        };
        return new ConfigRead(cfg, notes);
    }

    /// <summary>Parse <c>config.applied.cad</c> (written by MynaCAD). <c>null</c> when its version is newer than this reader knows.</summary>
    public static ConfigApplied? ParseApplied(string text)
    {
        using var doc = Open(KeyApplied, text, out var newer);
        return newer is null ? ReadApplied(doc!.RootElement, KeyApplied) : null;
    }

    /// <summary>Parse <c>config.proposal</c> (written by MynaCAD). <c>null</c> when its version is newer than this reader knows.</summary>
    public static ConfigProposal? ParseProposal(string text)
    {
        using var doc = Open(KeyProposal, text, out var newer);
        return newer is null ? ReadProposal(doc!.RootElement, KeyProposal) : null;
    }

    /// <summary>Parse the text of one key: size, JSON, an object with a whole-number <c>v</c> of 1 or more. <paramref name="newer"/> is set when <c>v</c> is newer than known.</summary>
    static JsonDocument? Open(string key, string text, out long? newer)
    {
        newer = null;
        var bytes = Encoding.UTF8.GetByteCount(text);
        if (bytes > MaxValueBytes) throw new ConfigTooLargeException(key, bytes);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 }); }
        catch (JsonException e) { throw new ConfigException(key, $"not valid JSON: {e.Message}"); }
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) { doc.Dispose(); throw new ConfigException(key, "the value is not a JSON object"); }
        if (!root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out var ver)) { doc.Dispose(); throw new ConfigException(key, "`v` (a whole number) is missing"); }
        if (ver < 1) { doc.Dispose(); throw new ConfigException(key, "`v` is 1 or more"); }
        if (ver > ReaderVersion) { newer = ver; return doc; }
        return doc;
    }

    // ---- field readers: a missing field is its default, a field of the wrong type (null included) is an error, unknown fields are ignored.

    static bool Has(JsonElement o, string name, out JsonElement v) => o.TryGetProperty(name, out v);

    static string Str(JsonElement o, string key, string name)
    {
        if (!Has(o, name, out var v)) return "";
        if (v.ValueKind != JsonValueKind.String) throw new ConfigException(key, $"`{name}` must be text");
        return v.GetString() ?? "";
    }

    static bool Bool(JsonElement o, string key, string name)
    {
        if (!Has(o, name, out var v)) return false;
        return v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw new ConfigException(key, $"`{name}` must be true or false") };
    }

    static double RequiredNum(JsonElement o, string key, string name)
    {
        if (!Has(o, name, out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var d)) throw new ConfigException(key, $"`{name}` must be a number");
        return d;
    }

    static double? OptNum(JsonElement o, string key, string name)
    {
        if (!Has(o, name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var d)) throw new ConfigException(key, $"`{name}` must be a number");
        return d;
    }

    static uint RequiredU32(JsonElement o, string key, string name)
    {
        if (!Has(o, name, out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetUInt32(out var n)) throw new ConfigException(key, $"`{name}` must be a whole number from 0 to 4294967295");
        return n;
    }

    static uint? OptU32(JsonElement o, string key, string name)
    {
        if (!Has(o, name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetUInt32(out var n)) throw new ConfigException(key, $"`{name}` must be a whole number from 0 to 4294967295");
        return n;
    }

    static JsonElement? Obj(JsonElement o, string key, string name)
    {
        if (!Has(o, name, out var v)) return null;
        if (v.ValueKind != JsonValueKind.Object) throw new ConfigException(key, $"`{name}` must be an object");
        return v;
    }

    static List<JsonElement> Arr(JsonElement o, string key, string name)
    {
        var list = new List<JsonElement>();
        if (!Has(o, name, out var v)) return list;
        if (v.ValueKind != JsonValueKind.Array) throw new ConfigException(key, $"`{name}` must be a list");
        foreach (var e in v.EnumerateArray()) list.Add(e);
        return list;
    }

    static List<string> StrList(JsonElement o, string key, string name)
    {
        var list = new List<string>();
        foreach (var e in Arr(o, key, name))
        {
            if (e.ValueKind != JsonValueKind.String) throw new ConfigException(key, $"`{name}` must be a list of text");
            list.Add(e.GetString() ?? "");
        }
        return list;
    }

    static SortedDictionary<string, string> StrMap(JsonElement o, string key, string name)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (!Has(o, name, out var v)) return map;
        if (v.ValueKind != JsonValueKind.Object) throw new ConfigException(key, $"`{name}` must be an object of text");
        foreach (var p in v.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.String) throw new ConfigException(key, $"`{name}` must be an object of text");
            map[p.Name] = p.Value.GetString() ?? "";
        }
        return map;
    }

    // ---- value checks (the same rules as aad_odp::config)

    static int Chars(string s) => s.EnumerateRunes().Count();
    static bool AnyControl(string s) => s.EnumerateRunes().Any(Rune.IsControl);

    static void TextOk(string key, string field, string s, int max)
    {
        if (Chars(s) > max) throw new ConfigException(key, $"`{field}` is longer than {max} characters");
        if (s.EnumerateRunes().Any(r => Rune.IsControl(r) && r.Value != '\n' && r.Value != '\r' && r.Value != '\t')) throw new ConfigException(key, $"`{field}` has a control character");
    }

    static void NameOk(string key, string field, string s)
    {
        var n = s.Trim();
        if (n.Length == 0 || Chars(n) > MaxName || AnyControl(n)) throw new ConfigException(key, $"`{field}` is 1 to {MaxName} characters without control characters");
    }

    static void MmOk(string key, string field, double v, double lo, double hi)
    {
        if (!double.IsFinite(v) || v < lo || v > hi) throw new ConfigException(key, $"`{field}` must be a number from {lo} to {hi} millimetres");
    }

    static void OptMm(string key, string field, double? v, double lo, double hi) { if (v is { } x) MmOk(key, field, x, lo, hi); }

    static void PatternsOk(string key, string field, IReadOnlyList<string> list)
    {
        if (list.Count > MaxPatterns) throw new ConfigException(key, $"`{field}` has more than {MaxPatterns} entries");
        foreach (var p in list)
            if (p.Length == 0 || Chars(p) > MaxPatternLen || AnyControl(p)) throw new ConfigException(key, $"a `{field}` pattern is 1 to {MaxPatternLen} characters without control characters");
    }

    static void StringMapOk(string key, string field, IReadOnlyDictionary<string, string> m)
    {
        if (m.Count > MaxMapEntries) throw new ConfigException(key, $"`{field}` has more than {MaxMapEntries} entries");
        foreach (var (k, v) in m) { TextOk(key, $"{field} name", k, 64); TextOk(key, field, v, MaxText); }
    }

    static void SchemeOk(string key, ConfigScheme sc)
    {
        var p = sc.Pattern;
        if (p.Length == 0 || Chars(p) > 24 || CountOf(p, "{N}") != 1 || CountOf(p, "{D}") > 1) throw new ConfigException(key, "`scheme.pattern` is 1 to 24 characters with {N} once and {D} at most once");
        var rest = p.Replace("{N}", "").Replace("{D}", "");
        if (!rest.All(c => c < 128 && (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ' '))) throw new ConfigException(key, "`scheme.pattern` may only add letters, digits and - _ . space around {D} and {N}");
        if (sc.Width is < 1 or > 8 || sc.Step is < 1 or > 1000) throw new ConfigException(key, "`scheme.width` is 1 to 8 and `scheme.step` is 1 to 1000");
        if (sc.Disciplines.Count is 0 or > MaxDisciplines) throw new ConfigException(key, $"`scheme.disciplines` has 1 to {MaxDisciplines} entries");
        var seen = new HashSet<string>();
        foreach (var d in sc.Disciplines)
        {
            var code = d.Code.Trim().ToUpperInvariant();
            if (code.Length == 0 || Encoding.UTF8.GetByteCount(code) > 4 || !code.All(c => c < 128 && char.IsAsciiLetterOrDigit(c))) throw new ConfigException(key, "a discipline `code` is 1 to 4 letters or digits");
            TextOk(key, "discipline name", d.Name, 40);
            if (d.Start > 999_999) throw new ConfigException(key, "a discipline `start` is 0 to 999999");
            if (!seen.Add(code)) throw new ConfigException(key, "a discipline code is listed twice");
        }
    }

    static int CountOf(string s, string what)
    {
        int n = 0, i = 0;
        while ((i = s.IndexOf(what, i, StringComparison.Ordinal)) >= 0) { n++; i += what.Length; }
        return n;
    }

    // ---- the parts

    static ConfigSchema ReadSchema(JsonElement o, string key)
    {
        var min = 1L;
        if (Has(o, "min_reader", out var v))
        {
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out min)) throw new ConfigException(key, "`min_reader` must be a whole number");
        }
        if (min < 1) throw new ConfigException(key, "`min_reader` is 1 or more");
        if (min > ReaderVersion) throw new ConfigTooNewException(min);
        return new ConfigSchema { V = 1, MinReader = min };
    }

    static ConfigRev ReadRev(JsonElement o, string key)
    {
        if (!Has(o, "rev", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetUInt64(out var rev)) throw new ConfigException(key, "`rev` must be a whole number from 0");
        var r = new ConfigRev { V = 1, Rev = rev, App = Str(o, key, "app"), Time = Str(o, key, "time") };
        TextOk(key, "app", r.App, 64);
        TextOk(key, "time", r.Time, 40);
        return r;
    }

    static ConfigProject ReadProject(JsonElement o, string key)
    {
        var p = new ConfigProject
        {
            Title = Str(o, key, "title"), Client = Str(o, key, "client"), Owner = Str(o, key, "owner"), Location = Str(o, key, "location"), Address = Str(o, key, "address"),
            Pid = Str(o, key, "pid"), SiteArea = Str(o, key, "siteArea"), PlotUse = Str(o, key, "plotUse"), Authority = Str(o, key, "authority"), Date = Str(o, key, "date"),
            Summary = Str(o, key, "summary"), Code = Str(o, key, "code"), Persona = Str(o, key, "persona"),
        };
        foreach (var (f, s, max) in new[]
        {
            ("title", p.Title, MaxText), ("client", p.Client, MaxText), ("owner", p.Owner, MaxText), ("location", p.Location, MaxText), ("address", p.Address, MaxText * 2),
            ("pid", p.Pid, MaxText), ("siteArea", p.SiteArea, MaxText), ("plotUse", p.PlotUse, MaxText), ("authority", p.Authority, MaxText), ("date", p.Date, MaxText),
            ("summary", p.Summary, MaxLongText), ("code", p.Code, 32), ("persona", p.Persona, 64),
        }) TextOk(key, f, s, max);
        return p;
    }

    static ConfigTeam ReadTeam(JsonElement o, string key)
    {
        var t = new ConfigTeam { Architect = Str(o, key, "architect"), Engineer = Str(o, key, "engineer"), Company = StrMap(o, key, "company") };
        TextOk(key, "architect", t.Architect, MaxText);
        TextOk(key, "engineer", t.Engineer, MaxText);
        StringMapOk(key, "company", t.Company);
        return t;
    }

    static ConfigUnits ReadUnits(JsonElement o, string key)
    {
        if (!Has(o, "drawing_unit", out var v) || v.ValueKind != JsonValueKind.String) throw new ConfigException(key, "`drawing_unit` must be text");
        var unit = v.GetString() ?? "";
        if (!UnitNames.Contains(unit)) throw new ConfigException(key, "`drawing_unit` is mm, cm, m, in or ft");
        return new ConfigUnits { DrawingUnit = unit, Locked = Bool(o, key, "locked") };
    }

    static ConfigLevels ReadLevels(JsonElement o, string key)
    {
        var next = RequiredU32(o, key, "next_cid");
        var d = new ConfigLevelDefaults();
        if (Obj(o, key, "defaults") is { } de)
            d = new ConfigLevelDefaults
            {
                HeightMm = OptNum(de, key, "height_mm"), SlabMm = OptNum(de, key, "slab_mm"), BeamMm = OptNum(de, key, "beam_mm"),
                CeilingMm = OptNum(de, key, "ceiling_mm"), LintelBottomMm = OptNum(de, key, "lintel_bottom_mm"),
            };
        var items = new List<ConfigLevelItem>();
        foreach (var (e, i) in Arr(o, key, "items").Select((e, i) => (e, i)))
        {
            var at = $"{key}.items[{i}]";
            if (e.ValueKind != JsonValueKind.Object) throw new ConfigException(at, "a level is an object");
            items.Add(new ConfigLevelItem
            {
                Cid = RequiredU32(e, at, "cid"), Name = RequiredStr(e, at, "name"), Code = Str(e, at, "code"), ElevationMm = RequiredNum(e, at, "elevation_mm"),
                HeightMm = RequiredNum(e, at, "height_mm"), SlabMm = OptNum(e, at, "slab_mm"), BeamMm = OptNum(e, at, "beam_mm"),
                CeilingMm = OptNum(e, at, "ceiling_mm"), LintelBottomMm = OptNum(e, at, "lintel_bottom_mm"),
            });
        }
        var lv = new ConfigLevels { NextCid = next, OnePerFloor = Bool(o, key, "one_per_floor"), Defaults = d, Items = items };
        CheckLevels(lv, key);
        return lv;
    }

    static string RequiredStr(JsonElement o, string key, string name)
    {
        if (!Has(o, name, out var v) || v.ValueKind != JsonValueKind.String) throw new ConfigException(key, $"`{name}` must be text");
        return v.GetString() ?? "";
    }

    static void CheckLevels(ConfigLevels l, string key)
    {
        if (l.Items.Count > MaxLevels) throw new ConfigException(key, $"more than {MaxLevels} levels");
        var d = l.Defaults;
        OptMm(key, "defaults.height_mm", d.HeightMm, 1, 100_000);
        OptMm(key, "defaults.slab_mm", d.SlabMm, 1, 5000);
        OptMm(key, "defaults.beam_mm", d.BeamMm, 1, 5000);
        OptMm(key, "defaults.ceiling_mm", d.CeilingMm, 1, 100_000);
        OptMm(key, "defaults.lintel_bottom_mm", d.LintelBottomMm, 1, 100_000);
        var cids = new HashSet<uint>();
        var names = new HashSet<string>();
        uint? top = null;
        for (var i = 0; i < l.Items.Count; i++)
        {
            var x = l.Items[i];
            var at = $"{key}.items[{i}]";
            NameOk(at, "name", x.Name);
            TextOk(at, "code", x.Code, 16);
            MmOk(at, "elevation_mm", x.ElevationMm, -1_000_000, 1_000_000);
            MmOk(at, "height_mm", x.HeightMm, 0, 100_000);
            OptMm(at, "slab_mm", x.SlabMm, 1, 5000);
            OptMm(at, "beam_mm", x.BeamMm, 1, 5000);
            OptMm(at, "ceiling_mm", x.CeilingMm, 1, 100_000);
            OptMm(at, "lintel_bottom_mm", x.LintelBottomMm, 1, 100_000);
            if (!cids.Add(x.Cid)) throw new ConfigException(at, $"`cid` {x.Cid} is used twice");
            if (!names.Add(x.Name.Trim().ToLowerInvariant())) throw new ConfigException(at, $"the name `{x.Name.Trim()}` is used twice (names are compared without case)");
            top = top is { } t ? Math.Max(t, x.Cid) : x.Cid;
        }
        if (top is { } m && l.NextCid <= m) throw new ConfigException(key, "`next_cid` must be larger than every `cid` (ids are never reused)");
    }

    static ConfigLayerSets ReadLayerSets(JsonElement o, string key)
    {
        var ls = new ConfigLayerSets { Sets = StrList(o, key, "sets") };
        if (ls.Sets.Count > LayerSetNames.Count) throw new ConfigException(key, "more layer sets than exist");
        var seen = new HashSet<string>();
        foreach (var s in ls.Sets)
        {
            var n = s.Trim().ToLowerInvariant();
            if (!LayerSetNames.Contains(n)) throw new ConfigException(key, "layer sets are standard, permit and presentation");
            if (!seen.Add(n)) throw new ConfigException(key, "a layer set is listed twice");
        }
        return ls;
    }

    static ConfigTabs ReadTabs(JsonElement o, string key)
    {
        var next = OptU32(o, key, "next_tid");
        var items = new List<ConfigTabItem>();
        foreach (var (e, i) in Arr(o, key, "items").Select((e, i) => (e, i)))
        {
            var at = $"{key}.items[{i}]";
            if (e.ValueKind != JsonValueKind.Object) throw new ConfigException(at, "a tab is an object");
            items.Add(new ConfigTabItem
            {
                Tid = RequiredU32(e, at, "tid"), Code = Str(e, at, "code"), Name = RequiredStr(e, at, "name"), LayerPrefix = Str(e, at, "layer_prefix"),
                Layers = StrList(e, at, "layers"), Blocks = StrList(e, at, "blocks"),
            });
        }
        var t = new ConfigTabs { NextTid = next, Items = items };
        if (items.Count > MaxTabs) throw new ConfigException(key, $"more than {MaxTabs} tabs");
        var tids = new HashSet<uint>();
        var names = new HashSet<string>();
        uint? top = null;
        for (var i = 0; i < items.Count; i++)
        {
            var x = items[i];
            var at = $"{key}.items[{i}]";
            NameOk(at, "name", x.Name);
            TextOk(at, "code", x.Code, 16);
            if (Chars(x.LayerPrefix) > 8 || x.LayerPrefix.EnumerateRunes().Any(r => Rune.IsControl(r) || Rune.IsWhiteSpace(r))) throw new ConfigException(at, "`layer_prefix` is at most 8 characters without spaces");
            PatternsOk(at, "layers", x.Layers);
            PatternsOk(at, "blocks", x.Blocks);
            if (!tids.Add(x.Tid)) throw new ConfigException(at, $"`tid` {x.Tid} is used twice");
            if (!names.Add(x.Name.Trim().ToLowerInvariant())) throw new ConfigException(at, $"the name `{x.Name.Trim()}` is used twice (names are compared without case)");
            top = top is { } m ? Math.Max(m, x.Tid) : x.Tid;
        }
        if (next is { } n && top is { } t2 && n <= t2) throw new ConfigException(key, "`next_tid` must be larger than every `tid`");
        return t;
    }

    static ConfigScheme ReadScheme(JsonElement o, string key)
    {
        var ds = new List<ConfigDiscipline>();
        foreach (var e in Arr(o, key, "disciplines"))
        {
            if (e.ValueKind != JsonValueKind.Object) throw new ConfigException(key, "a discipline is an object");
            ds.Add(new ConfigDiscipline { Code = RequiredStr(e, key, "code"), Name = RequiredStr(e, key, "name"), Start = RequiredU32(e, key, "start") });
        }
        return new ConfigScheme { Pattern = RequiredStr(o, key, "pattern"), Width = RequiredU32(o, key, "width"), Step = RequiredU32(o, key, "step"), Disciplines = ds };
    }

    static ConfigSheets ReadSheets(JsonElement o, string key)
    {
        if (!Has(o, "scheme", out var sc) || sc.ValueKind != JsonValueKind.Object) throw new ConfigException(key, "`scheme` must be an object");
        var s = new ConfigSheets { Scheme = ReadScheme(sc, key) };
        SchemeOk(key, s.Scheme);
        return s;
    }

    static ConfigNaming ReadNaming(JsonElement o, string key)
    {
        var n = new ConfigNaming();
        if (Obj(o, key, "patterns") is { } p) n = new ConfigNaming { File = Str(p, key, "file"), DrawingNumber = Str(p, key, "drawing_number") };
        TextOk(key, "patterns.file", n.File, 128);
        // A file-name pattern is a name, never a path: no separators, no drive, no `..`.
        if (n.File.Contains("..") || n.File.Contains('/') || n.File.Contains('\\') || n.File.Contains(':')) throw new ConfigException(key, "`patterns.file` is a file name pattern: no `..`, `/`, `\\` or `:`");
        TextOk(key, "patterns.drawing_number", n.DrawingNumber, 128);
        return n;
    }

    static ConfigTitleBlock ReadTitleBlock(JsonElement o, string key)
    {
        var t = new ConfigTitleBlock { Fields = StrMap(o, key, "fields") };
        StringMapOk(key, "fields", t.Fields);
        return t;
    }

    static ConfigApplied ReadApplied(JsonElement o, string key)
    {
        if (!Has(o, "rev", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetUInt64(out var rev)) throw new ConfigException(key, "`rev` must be a whole number from 0");
        var items = new List<ConfigAppliedItem>();
        foreach (var e in Arr(o, key, "items"))
        {
            if (e.ValueKind != JsonValueKind.Object) throw new ConfigException(key, "an item is an object");
            items.Add(new ConfigAppliedItem { Key = RequiredStr(e, key, "key"), Status = RequiredStr(e, key, "status"), Detail = Str(e, key, "detail") });
        }
        var a = new ConfigApplied { Rev = rev, Time = Str(o, key, "time"), Result = RequiredStr(o, key, "result"), HasContent = Bool(o, key, "has_content"), Items = items };
        if (a.Result is not ("ok" or "partial" or "failed")) throw new ConfigException(key, "`result` is ok, partial or failed");
        TextOk(key, "time", a.Time, 40);
        if (items.Count > MaxItemsReport) throw new ConfigException(key, "too many items");
        foreach (var it in items) { TextOk(key, "item key", it.Key, 64); TextOk(key, "item status", it.Status, 16); TextOk(key, "item detail", it.Detail, MaxLongText); }
        return a;
    }

    static ConfigProposal ReadProposal(JsonElement o, string key)
    {
        var levels = new List<ConfigProposalLevel>();
        foreach (var e in Arr(o, key, "levels"))
        {
            if (e.ValueKind != JsonValueKind.Object) throw new ConfigException(key, "a level is an object");
            levels.Add(new ConfigProposalLevel { Cid = RequiredU32(e, key, "cid"), Name = RequiredStr(e, key, "name"), ElevationMm = RequiredNum(e, key, "elevation_mm"), HeightMm = RequiredNum(e, key, "height_mm") });
        }
        var tabs = new List<ConfigProposalTab>();
        foreach (var e in Arr(o, key, "tabs"))
        {
            if (e.ValueKind != JsonValueKind.Object) throw new ConfigException(key, "a tab is an object");
            tabs.Add(new ConfigProposalTab { Tid = OptU32(e, key, "tid") ?? 0, Name = RequiredStr(e, key, "name"), LayerPrefix = Str(e, key, "layer_prefix"), Layers = StrList(e, key, "layers"), Blocks = StrList(e, key, "blocks") });
        }
        string? unit = null;
        if (Has(o, "drawing_unit", out var du) && du.ValueKind != JsonValueKind.Null)
        {
            if (du.ValueKind != JsonValueKind.String) throw new ConfigException(key, "`drawing_unit` must be text");
            unit = du.GetString();
        }
        ConfigScheme? scheme = null;
        if (Has(o, "scheme", out var sc) && sc.ValueKind != JsonValueKind.Null)
        {
            if (sc.ValueKind != JsonValueKind.Object) throw new ConfigException(key, "`scheme` must be an object");
            scheme = ReadScheme(sc, key);
        }
        var p = new ConfigProposal
        {
            Time = Str(o, key, "time"), HasContent = Bool(o, key, "has_content"), DrawingUnit = unit, Levels = levels, Tabs = tabs,
            LayerSets = StrList(o, key, "layer_sets"), Project = StrMap(o, key, "project"), Scheme = scheme,
        };
        TextOk(key, "time", p.Time, 40);
        if (levels.Count > MaxLevels || tabs.Count > MaxTabs || p.LayerSets.Count > LayerSetNames.Count) throw new ConfigException(key, "too many levels, tabs or layer sets");
        if (unit is not null && !UnitNames.Contains(unit)) throw new ConfigException(key, "`drawing_unit` is mm, cm, m, in or ft");
        for (var i = 0; i < levels.Count; i++)
        {
            var at = $"{key}.levels[{i}]";
            NameOk(at, "name", levels[i].Name);
            MmOk(at, "elevation_mm", levels[i].ElevationMm, -1_000_000, 1_000_000);
            MmOk(at, "height_mm", levels[i].HeightMm, 0, 100_000);
        }
        for (var i = 0; i < tabs.Count; i++)
        {
            var at = $"{key}.tabs[{i}]";
            NameOk(at, "name", tabs[i].Name);
            PatternsOk(at, "layers", tabs[i].Layers);
            PatternsOk(at, "blocks", tabs[i].Blocks);
        }
        StringMapOk(key, "project", p.Project);
        if (scheme is not null) SchemeOk(key, scheme);
        return p;
    }

    // ------------------------------------------------------------------------------------------------ serialize

    /// <summary>The JSON text of every part that is present, by short name. <c>schema</c> and <c>rev</c> are included as they are.</summary>
    public IReadOnlyDictionary<string, string> ToTexts()
    {
        var o = ToJson();
        var texts = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in o) if (v is not null) texts[k] = v.ToJsonString();
        return texts;
    }

    /// <summary>The JSON form (also what MynaCAD's <c>project.config.apply</c> takes): an object with a member per part, each carrying its <c>v</c>.</summary>
    public JsonObject ToJson()
    {
        var o = new JsonObject();
        if (Schema is { } sc) o["schema"] = new JsonObject { ["v"] = 1, ["min_reader"] = sc.MinReader };
        if (RevPart is { } r) o["rev"] = new JsonObject { ["v"] = 1, ["rev"] = r.Rev, ["app"] = r.App, ["time"] = r.Time };
        if (Project is { } p)
            o["project"] = new JsonObject
            {
                ["v"] = 1, ["title"] = p.Title, ["client"] = p.Client, ["owner"] = p.Owner, ["location"] = p.Location, ["address"] = p.Address, ["pid"] = p.Pid,
                ["siteArea"] = p.SiteArea, ["plotUse"] = p.PlotUse, ["authority"] = p.Authority, ["date"] = p.Date, ["summary"] = p.Summary, ["code"] = p.Code, ["persona"] = p.Persona,
            };
        if (Team is { } t) o["team"] = new JsonObject { ["v"] = 1, ["architect"] = t.Architect, ["engineer"] = t.Engineer, ["company"] = MapNode(t.Company) };
        if (Units is { } u) o["units"] = new JsonObject { ["v"] = 1, ["drawing_unit"] = u.DrawingUnit, ["locked"] = u.Locked };
        if (Levels is { } l)
        {
            var d = new JsonObject();
            AddNum(d, "height_mm", l.Defaults.HeightMm); AddNum(d, "slab_mm", l.Defaults.SlabMm); AddNum(d, "beam_mm", l.Defaults.BeamMm);
            AddNum(d, "ceiling_mm", l.Defaults.CeilingMm); AddNum(d, "lintel_bottom_mm", l.Defaults.LintelBottomMm);
            var items = new JsonArray();
            foreach (var x in l.Items)
            {
                var n = new JsonObject { ["cid"] = x.Cid, ["name"] = x.Name, ["code"] = x.Code, ["elevation_mm"] = x.ElevationMm, ["height_mm"] = x.HeightMm };
                AddNum(n, "slab_mm", x.SlabMm); AddNum(n, "beam_mm", x.BeamMm); AddNum(n, "ceiling_mm", x.CeilingMm); AddNum(n, "lintel_bottom_mm", x.LintelBottomMm);
                items.Add(n);
            }
            o["levels"] = new JsonObject { ["v"] = 1, ["next_cid"] = l.NextCid, ["one_per_floor"] = l.OnePerFloor, ["defaults"] = d, ["items"] = items };
        }
        if (LayerSets is { } ls) o["layer_sets"] = new JsonObject { ["v"] = 1, ["sets"] = ListNode(ls.Sets) };
        if (Tabs is { } tb)
        {
            var items = new JsonArray();
            foreach (var x in tb.Items)
                items.Add(new JsonObject { ["tid"] = x.Tid, ["code"] = x.Code, ["name"] = x.Name, ["layer_prefix"] = x.LayerPrefix, ["layers"] = ListNode(x.Layers), ["blocks"] = ListNode(x.Blocks) });
            var n = new JsonObject { ["v"] = 1 };
            if (tb.NextTid is { } nt) n["next_tid"] = nt;
            n["items"] = items;
            o["tabs"] = n;
        }
        if (Sheets is { } sh) o["sheets"] = new JsonObject { ["v"] = 1, ["scheme"] = SchemeNode(sh.Scheme) };
        if (Naming is { } nm) o["naming"] = new JsonObject { ["v"] = 1, ["patterns"] = new JsonObject { ["file"] = nm.File, ["drawing_number"] = nm.DrawingNumber } };
        if (TitleBlock is { } tbk) o["titleblock"] = new JsonObject { ["v"] = 1, ["fields"] = MapNode(tbk.Fields) };
        return o;
    }

    static void AddNum(JsonObject o, string name, double? v) { if (v is { } x) o[name] = x; }

    static JsonNode MapNode(IReadOnlyDictionary<string, string> m)
    {
        var o = new JsonObject();
        foreach (var (k, v) in m) o[k] = v;
        return o;
    }

    static JsonNode ListNode(IReadOnlyList<string> l)
    {
        var a = new JsonArray();
        foreach (var s in l) a.Add(s);
        return a;
    }

    static JsonNode SchemeNode(ConfigScheme s)
    {
        var ds = new JsonArray();
        foreach (var d in s.Disciplines) ds.Add(new JsonObject { ["code"] = d.Code, ["name"] = d.Name, ["start"] = d.Start });
        return new JsonObject { ["pattern"] = s.Pattern, ["width"] = s.Width, ["step"] = s.Step, ["disciplines"] = ds };
    }

    /// <summary>Read the JSON form (see <see cref="ToJson"/>); the same checks as a file read.</summary>
    public static ConfigRead FromJson(JsonNode node)
    {
        if (node is not JsonObject o) throw new ConfigException("config", "the configuration is not a JSON object");
        var parts = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in Parts)
            if (o[name] is { } p) parts[name] = p.ToJsonString();
        return FromTexts(parts);
    }

    /// <summary>Check every part (the same rules a reader applies) by writing it out and parsing it back.</summary>
    public void Validate() => FromTexts(ToTexts());

    // ------------------------------------------------------------------------------------------------ files

    static ConfigLoaded Load(Func<string, long?> length, Func<string, string?> get, long revision)
    {
        string? Fetch(string key)
        {
            var n = length(key);
            if (n is null) return null;
            if (n > MaxValueBytes) throw new ConfigTooLargeException(key, n.Value);
            return get(key);
        }
        var parts = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in Parts)
            if (Fetch(Prefix + name) is { } t) parts[name] = t;
        var read = FromTexts(parts);
        var notes = read.Notes.ToList();
        var config = read.Config;
        if (!config.Configured && parts.Count > 0)
        {
            notes.Add("config keys are present without config.schema: the project counts as not configured");
            config = config with { Schema = null };
        }
        ConfigApplied? applied = null;
        ConfigProposal? proposal = null;
        // The two keys MynaCAD writes are reports: a damaged one reads as absent and is noted, it never stops the configuration from being read.
        if (Fetch(KeyApplied) is { } at)
        {
            try { applied = ParseApplied(at); if (applied is null) notes.Add($"{KeyApplied} is a newer version: left out"); }
            catch (ConfigException e) { notes.Add($"{KeyApplied} is damaged ({e.Message}): left out"); }
        }
        if (Fetch(KeyProposal) is { } pt)
        {
            try { proposal = ParseProposal(pt); if (proposal is null) notes.Add($"{KeyProposal} is a newer version: left out"); }
            catch (ConfigException e) { notes.Add($"{KeyProposal} is damaged ({e.Message}): left out"); }
        }
        return new ConfigLoaded(new ConfigRead(config, notes), applied, proposal, revision);
    }

    /// <summary>Read the configuration of an open project file.</summary>
    public static ConfigLoaded Read(OdpFile odp) => Load(odp.MetaLength, odp.MetaText, odp.Revision());

    /// <summary>
    /// Read the configuration of a project file without taking any lock a writer would wait for: the file is opened read-only, a few meta keys are read, and it
    /// is closed. Never creates or changes the file.
    /// </summary>
    public static ConfigLoaded Probe(string path)
    {
        if (!File.Exists(path)) throw new ConfigException("config", $"{path} does not exist");
        var cs = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1 }.ToString();
        try
        {
            using var db = new Microsoft.Data.Sqlite.SqliteConnection(cs);
            db.Open();
            string? One(string sql, string key)
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("$k", key);
                var v = cmd.ExecuteScalar();
                return v is null or DBNull ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
            }
            long? Len(string k) => One("SELECT length(CAST(value AS BLOB)) FROM meta WHERE key = $k", k) is { } s && long.TryParse(s, out var n) ? n : null;
            string? Get(string k) => One("SELECT value FROM meta WHERE key = $k", k);
            var rev = long.TryParse(Get("revision"), out var r) ? r : 0;
            return Load(Len, Get, rev);
        }
        catch (Microsoft.Data.Sqlite.SqliteException e) { throw new ConfigException("config", $"{path}: {e.Message}"); }
    }

    /// <summary>
    /// Write <paramref name="cfg"/> into the file: every part that differs from the file, and the bumped <c>config.rev</c>, in <b>one</b> transaction, so
    /// <c>meta.revision</c> rises once and <c>sync.last</c> names MynaOps. A part that is <c>null</c> is left as it is (a writer never deletes a key);
    /// <c>schema</c> is added when missing; <c>cfg.RevPart</c> is ignored (the writer sets it). Nothing is written, and no revision made, when no part differs.
    /// </summary>
    public static ConfigWritten Write(OdpFile odp, ProjectConfig cfg, string app = "mynaops")
    {
        cfg.Validate();
        var texts = new SortedDictionary<string, string>(cfg.ToTexts().ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal);
        texts.Remove("rev");
        if (!texts.ContainsKey("schema")) texts["schema"] = new JsonObject { ["v"] = 1, ["min_reader"] = 1 }.ToJsonString();
        var changed = new List<string>();
        foreach (var (name, text) in texts)
        {
            var key = Prefix + name;
            var have = odp.MetaLength(key) is { } n && n <= MaxValueBytes ? odp.MetaText(key) : null;
            if (have is null || Normalized(name, have) != Normalized(name, text)) changed.Add(name);
        }
        var current = 0UL;
        if (odp.MetaLength(Prefix + "rev") is { } rl && rl <= MaxValueBytes && odp.MetaText(Prefix + "rev") is { } rt)
        {
            try { current = ReadRevOf(rt); } catch (ConfigException) { current = 0; }
        }
        if (changed.Count == 0) return new ConfigWritten(odp.Revision(), current, changed);
        var rev = current + 1;
        var revText = new JsonObject { ["v"] = 1, ["rev"] = rev, ["app"] = app, ["time"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) }.ToJsonString();
        var pairs = changed.Select(c => (Prefix + c, texts[c])).Append((Prefix + "rev", revText)).ToList();
        var revision = odp.WriteMeta(pairs, app);
        changed.Add("rev");
        return new ConfigWritten(revision, rev, changed);
    }

    static ulong ReadRevOf(string text)
    {
        using var doc = Open("config.rev", text, out var newer);
        if (newer is not null) return 0;
        return ReadRev(doc!.RootElement, "config.rev").Rev;
    }

    /// <summary>A part's text read and written out again, so two texts that mean the same compare equal (a writer in another language may omit a null).</summary>
    static string Normalized(string name, string text)
    {
        try
        {
            var read = FromTexts(new SortedDictionary<string, string> { [name] = text });
            return read.Config.ToTexts().TryGetValue(name, out var t) ? t : "";
        }
        catch (ConfigException) { return ""; }
    }
}
