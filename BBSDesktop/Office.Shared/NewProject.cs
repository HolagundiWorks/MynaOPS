// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Globalization;

namespace Office.Shared;

/// <summary>One level of the wizard's editable list (millimetres; the configuration is in millimetres whatever the drawing unit).</summary>
public sealed record LevelSpec(string Name, string Code, double ElevationMm, double HeightMm);

/// <summary>What the create-project wizard collects (docs/ops-first-workflow.md section 1.2).</summary>
public sealed class NewProjectRequest
{
    /// <summary>The folder of the new <c>.osp</c> and its file name (with or without the extension).</summary>
    public string Folder { get; set; } = "";
    public string FileName { get; set; } = "";

    public ConfigProject Project { get; set; } = new();
    public ConfigTeam Team { get; set; } = new();

    /// <summary>mm, cm, m, in or ft.</summary>
    public string DrawingUnit { get; set; } = "mm";

    public int Basements { get; set; }
    /// <summary>Floors above the ground floor (G + n).</summary>
    public int UpperFloors { get; set; } = 1;
    public bool Terrace { get; set; } = true;
    public double FloorHeightMm { get; set; } = 3000;
    public double SlabMm { get; set; } = 150;
    public double BeamMm { get; set; } = 450;
    /// <summary>True: one level for each floor. False: one level, the ground floor.</summary>
    public bool OneLevelPerFloor { get; set; } = true;
    /// <summary>The wizard's edited list. When set it is used as it is (bottom to top) instead of the floors above.</summary>
    public IReadOnlyList<LevelSpec>? Levels { get; set; }

    /// <summary>standard, permit, presentation.</summary>
    public IReadOnlyList<string> LayerSets { get; set; } = new[] { "standard" };
    /// <summary>The five discipline tabs (Working drawing, Electrical, Plumbing, Interior, False ceiling) with their layer and block patterns.</summary>
    public bool StandardTabs { get; set; } = true;
    public ConfigScheme Scheme { get; set; } = NewProjectBuilder.DefaultScheme;
}

/// <summary>The result of <see cref="NewProjectBuilder.Create"/>.</summary>
public sealed record NewProjectResult(string Path, ConfigWritten Written);

/// <summary>
/// The create-project wizard's work (package A): the request becomes a configuration, which <see cref="ProjectConfig.Write"/> puts in a new project
/// file in one revision. The wizard window only collects the request; everything that can go wrong is checked here, where it is tested.
/// </summary>
public static class NewProjectBuilder
{
    public const int MaxFloors = 60;

    /// <summary>MynaCAD's own default sheet numbering (<c>sheetindex</c>), so a new project starts where MynaCAD would.</summary>
    public static readonly ConfigScheme DefaultScheme = new()
    {
        Pattern = "{D}-{N}", Width = 3, Step = 1,
        Disciplines = new[]
        {
            new ConfigDiscipline { Code = "G", Name = "General", Start = 1 },
            new ConfigDiscipline { Code = "A", Name = "Architectural", Start = 101 },
            new ConfigDiscipline { Code = "S", Name = "Structural", Start = 201 },
            new ConfigDiscipline { Code = "E", Name = "Electrical", Start = 301 },
            new ConfigDiscipline { Code = "P", Name = "Plumbing", Start = 401 },
            new ConfigDiscipline { Code = "M", Name = "Mechanical", Start = 501 },
            new ConfigDiscipline { Code = "I", Name = "Interior", Start = 601 },
        },
    };

    /// <summary>The five standard discipline tabs, as MynaCAD's <c>sheetset.standard</c> maps them (layer and block name patterns).</summary>
    public static IReadOnlyList<ConfigTabItem> StandardTabItems { get; } = new[]
    {
        new ConfigTabItem { Tid = 1, Code = "W", Name = "Working drawing", LayerPrefix = "W-",
            Layers = new[] { "A-*", "AN-*", "W-*", "ROOM-LABELS", "MEASURE-*", "S-*" },
            Blocks = new[] { "AADOPEN-*", "DOOR*", "WIN*", "STAIR*", "NORTH*", "A-*", "SB_STRUCT_*", "SB_RCC_*" } },
        new ConfigTabItem { Tid = 2, Code = "E", Name = "Electrical", LayerPrefix = "E-",
            Layers = new[] { "E-*" }, Blocks = new[] { "E-*", "SW*", "SB*", "LP*", "FP*", "EF*", "SOCK*", "SB_ELEC_*" } },
        new ConfigTabItem { Tid = 3, Code = "P", Name = "Plumbing", LayerPrefix = "P-",
            Layers = new[] { "P-*" }, Blocks = new[] { "P-*", "WC*", "WB*", "SINK*", "SHOWER*", "TAP*", "SB_PLUMB_*" } },
        new ConfigTabItem { Tid = 4, Code = "I", Name = "Interior", LayerPrefix = "I-",
            Layers = new[] { "I-*", "A-FLR" }, Blocks = new[] { "FURN-*", "I-*", "SB_INT_*" } },
        new ConfigTabItem { Tid = 5, Code = "FC", Name = "False ceiling", LayerPrefix = "FC-",
            Layers = new[] { "FC-*", "A-CLNG", "E-CEIL", "M-*" },
            Blocks = new[] { "FC-*", "SB_HVAC_*", "SB_ELEC_LIGHT_*", "SB_ELEC_FAN_CEILING_*", "SB_ELEC_DOWNLIGHT_*" } },
    };

    static readonly string[] Ordinals = { "First", "Second", "Third", "Fourth", "Fifth", "Sixth", "Seventh", "Eighth", "Ninth", "Tenth" };

    /// <summary>The level list the floors describe: basements below, the ground floor at 0, the floors above, then the terrace.</summary>
    public static IReadOnlyList<LevelSpec> GenerateLevels(NewProjectRequest r)
    {
        var h = r.FloorHeightMm;
        var list = new List<LevelSpec>();
        if (!r.OneLevelPerFloor)
        {
            list.Add(new LevelSpec("Ground Floor", "G", 0, h));
            return list;
        }
        for (var i = r.Basements; i >= 1; i--)
            list.Add(new LevelSpec($"Basement {i}", $"B{i}", -i * h, h));
        list.Add(new LevelSpec("Ground Floor", "G", 0, h));
        for (var k = 1; k <= r.UpperFloors; k++)
            list.Add(new LevelSpec(k <= Ordinals.Length ? Ordinals[k - 1] + " Floor" : $"Floor {k}", k.ToString(CultureInfo.InvariantCulture), k * h, h));
        if (r.Terrace)
            list.Add(new LevelSpec("Terrace", "T", (r.UpperFloors + 1) * h, 1000));   // the top level is the parapet height
        return list;
    }

    /// <summary>The configuration of a request: not written anywhere. Throws <see cref="ConfigException"/> when a value is not valid.</summary>
    public static ProjectConfig Build(NewProjectRequest r)
    {
        var specs = r.Levels ?? GenerateLevels(r);
        uint cid = 1;
        var items = specs.Select(s => new ConfigLevelItem { Cid = cid++, Name = s.Name.Trim(), Code = s.Code.Trim(), ElevationMm = s.ElevationMm, HeightMm = s.HeightMm }).ToList();
        var cfg = new ProjectConfig
        {
            Schema = new ConfigSchema(),
            Project = r.Project,
            Team = r.Team,
            Units = new ConfigUnits { DrawingUnit = r.DrawingUnit, Locked = false },
            Levels = new ConfigLevels
            {
                NextCid = cid, OnePerFloor = r.OneLevelPerFloor,
                Defaults = new ConfigLevelDefaults { HeightMm = r.FloorHeightMm, SlabMm = r.SlabMm, BeamMm = r.BeamMm },
                Items = items,
            },
            LayerSets = new ConfigLayerSets { Sets = r.LayerSets.Select(s => s.Trim().ToLowerInvariant()).Distinct().ToList() },
            Tabs = r.StandardTabs ? new ConfigTabs { NextTid = (uint)StandardTabItems.Count + 1, Items = StandardTabItems } : new ConfigTabs { NextTid = 1, Items = Array.Empty<ConfigTabItem>() },
            Sheets = new ConfigSheets { Scheme = r.Scheme },
            Naming = new ConfigNaming(),
            TitleBlock = new ConfigTitleBlock(),
        };
        cfg.Validate();
        return cfg;
    }

    /// <summary>The path the request would create.</summary>
    public static string PathOf(NewProjectRequest r)
    {
        var name = r.FileName.Trim();
        if (!name.EndsWith(OdpFile.Extension, StringComparison.OrdinalIgnoreCase)) name += OdpFile.Extension;
        return Path.Combine(r.Folder.Trim(), name);
    }

    /// <summary>What is wrong with the request, in words for the wizard (empty when it can be created).</summary>
    public static IReadOnlyList<string> Problems(NewProjectRequest r)
    {
        var p = new List<string>();
        if (string.IsNullOrWhiteSpace(r.Project.Title)) p.Add("Give the project a name.");
        if (string.IsNullOrWhiteSpace(r.FileName)) p.Add("Give the project file a name.");
        else if (r.FileName.Trim().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) p.Add("The file name has a character a file cannot have.");
        if (string.IsNullOrWhiteSpace(r.Folder)) p.Add("Choose the folder of the project file.");
        else if (!Directory.Exists(r.Folder.Trim())) p.Add("The folder does not exist.");
        else if (p.Count == 0 && File.Exists(PathOf(r))) p.Add($"{Path.GetFileName(PathOf(r))} already exists in that folder; open it instead or choose another name.");
        if (!ProjectConfig.UnitNames.Contains(r.DrawingUnit)) p.Add("The drawing unit is mm, cm, m, in or ft.");
        if (r.Basements is < 0 or > 10) p.Add("Basements are 0 to 10.");
        if (r.UpperFloors is < 0 or > MaxFloors) p.Add($"Floors above the ground floor are 0 to {MaxFloors}.");
        if (!double.IsFinite(r.FloorHeightMm) || r.FloorHeightMm is < 1000 or > 20000) p.Add("The floor-to-floor height is 1000 to 20000 mm.");
        if (r.Levels is { Count: 0 }) p.Add("The level list is empty.");
        if (r.Levels is not null && r.Levels.Any(l => !double.IsFinite(l.ElevationMm) || !double.IsFinite(l.HeightMm))) p.Add("A level's elevation or height is not a number.");
        if (p.Count == 0)
        {
            try { Build(r); }
            catch (Exception ex) when (ex is ConfigException or ArgumentException) { p.Add(ex.Message); }
        }
        return p;
    }

    /// <summary>
    /// Create the project file and write its configuration (one revision, <c>config.rev</c> 1). The file is removed again when anything fails, so a
    /// failed wizard leaves nothing behind. Returns the path and what was written.
    /// </summary>
    public static NewProjectResult Create(NewProjectRequest r)
    {
        var problems = Problems(r);
        if (problems.Count > 0) throw new InvalidOperationException(problems[0]);
        var path = PathOf(r);
        var cfg = Build(r);
        var created = false;
        try
        {
            using var odp = OdpFile.Create(path, r.Project.Title.Trim());
            created = true;
            var written = ProjectConfig.Write(odp, cfg);
            return new NewProjectResult(path, written);
        }
        catch
        {
            if (created)
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                foreach (var f in new[] { path, path + "-wal", path + "-shm" })
                    try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            throw;
        }
    }
}
