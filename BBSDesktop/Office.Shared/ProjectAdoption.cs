// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Globalization;
using System.Text.Json.Nodes;

namespace Office.Shared;

/// <summary>What the Adopt page shows: the configuration MynaOps would write, and where each part of it came from.</summary>
public sealed record AdoptionProposal(ProjectConfig Config, IReadOnlyList<string> Sources, IReadOnlyList<string> Notes)
{
    /// <summary>True when MynaCAD wrote a <c>config.proposal</c> (it knows the drawing; MynaOps cannot read the native model).</summary>
    public bool FromCad => Sources.Contains("MynaCAD's proposal");
}

/// <summary>What <see cref="ProjectAdoption.Adopt"/> did.</summary>
public sealed record AdoptionResult(ConfigWritten Written, string Backup);

/// <summary>
/// Adopting an existing project file (docs/ops-first-workflow.md section 4.5): a <c>.osp</c> without <c>config.schema</c> (a MynaCAD project, or one
/// MynaOps edited before) gets its configuration written by MynaOps. Nothing is written before <see cref="Adopt"/>; adopting makes a <c>manual</c>
/// backup first, writes only <c>config.*</c> keys (never a drawing table) and so leaves the <c>drawing</c> rows byte for byte as they were.
/// </summary>
public static class ProjectAdoption
{
    /// <summary>True when the file has no configuration yet (it needs the Adopt page). Never writes.</summary>
    public static bool NeedsAdoption(string path) => !ProjectConfig.Probe(path).Configured;

    /// <summary>
    /// The proposal, from (in this order of preference) MynaCAD's <c>config.proposal</c>, the levels in the take-off header, the MynaOps project
    /// document, and defaults. Levels with the same name (compared without case) are one entry; an adopted level's <c>cid</c> is the MynaCAD
    /// <c>LevelId</c> when there is one (so recorded take-off rows keep their <c>level_id</c>).
    /// </summary>
    public static AdoptionProposal Propose(string path, string? projectDocJson = null)
    {
        using var odp = OdpFile.Open(path);
        var loaded = ProjectConfig.Read(odp);
        var notes = new List<string>(loaded.Read.Notes);
        var sources = new List<string>();
        var proposal = loaded.Proposal;
        projectDocJson ??= odp.ProjectDoc();
        JsonObject? doc = null;
        try { doc = projectDocJson is null ? null : JsonNode.Parse(projectDocJson) as JsonObject; }
        catch (System.Text.Json.JsonException) { notes.Add("The project document in the file could not be read; its details are not offered."); }

        // ---- levels: id by name
        var levels = new List<(uint? Id, ConfigLevelItem Item)>();
        var seen = new HashSet<string>();
        void AddLevel(uint? cid, string name, double elevation, double height, double? slab = null, double? beam = null, double? ceiling = null, double? lintel = null)
        {
            name = name.Trim();
            if (name.Length == 0 || name.Length > ProjectConfig.MaxName || !seen.Add(name.ToLowerInvariant())) return;
            levels.Add((cid, new ConfigLevelItem { Name = name, ElevationMm = elevation, HeightMm = height, SlabMm = slab, BeamMm = beam, CeilingMm = ceiling, LintelBottomMm = lintel }));
        }

        if (proposal is { Levels.Count: > 0 })
        {
            sources.Add("MynaCAD's proposal");
            foreach (var l in proposal.Levels) AddLevel(l.Cid, l.Name, l.ElevationMm, l.HeightMm);
        }
        // the take-off header's levels (a recorded take-off carries id, elevation_mm, height_mm and the structural values)
        var headerLevels = 0;
        foreach (var h in SafeHeaders(odp))
        {
            if (h?["levels"] is not JsonArray arr) continue;
            foreach (var e in arr.OfType<JsonObject>())
            {
                var name = Str(e, "name");
                if (name is null) continue;
                var before = levels.Count;
                AddLevel(UInt(e, "id"), name, Num(e, "elevation_mm") ?? 0, Num(e, "height_mm") ?? 0,
                    Num(e, "slab_thickness_mm"), Num(e, "beam_depth_mm"), Num(e, "ceiling_mm"), Num(e, "lintel_bottom_mm"));
                if (levels.Count > before) headerLevels++;
            }
        }
        if (headerLevels > 0) sources.Add("the recorded take-off");

        // the MynaOps project document: its floors, in order, with the running elevation the Levels page shows; Lvl0 (the plinth) is AQC's own
        var docLevels = 0;
        if (doc?["levels"] is JsonArray dl)
        {
            double elev = 0;
            foreach (var e in dl.OfType<JsonObject>())
            {
                var name = Str(e, "name");
                var height = Num(e, "height_mm") ?? 0;
                var id = Str(e, "id");
                var here = elev;
                elev += Math.Max(0, height);
                if (name is null || string.Equals(id, "Lvl0", StringComparison.Ordinal)) continue;
                var before = levels.Count;
                AddLevel(null, name, here, height, Num(e, "slab_thickness_mm"), Num(e, "beam_depth_mm"));
                if (levels.Count > before) docLevels++;
                else Merge(levels, name, Num(e, "slab_thickness_mm"), Num(e, "beam_depth_mm"));
            }
        }
        if (docLevels > 0) sources.Add("the MynaOps project");

        // cids: the id MynaCAD gave a level stays (0 is a real LevelId); a level without one, or whose id is already taken, gets the next free number
        var used = new HashSet<uint>();
        var assigned = new List<ConfigLevelItem>();
        var pending = new List<ConfigLevelItem>();
        foreach (var (id, item) in levels)
        {
            if (id is { } known && used.Add(known)) assigned.Add(item with { Cid = known });
            else pending.Add(item);
        }
        var next = used.Count == 0 ? 1u : used.Max() + 1;
        foreach (var l in pending) assigned.Add(l with { Cid = next++ });
        var fixedLevels = assigned;
        var ordered = fixedLevels.OrderBy(l => l.ElevationMm).ThenBy(l => l.Cid).ToList();
        if (ordered.Count > ProjectConfig.MaxLevels)
        {
            notes.Add($"The project has {ordered.Count} levels; only the lowest {ProjectConfig.MaxLevels} are offered.");
            ordered = ordered.Take(ProjectConfig.MaxLevels).ToList();
        }
        var nextCid = ordered.Count == 0 ? 1u : ordered.Max(l => l.Cid) + 1;
        if (ordered.Count == 0) notes.Add("No level was found in the file; add levels on the Levels page after adopting.");

        // ---- project details
        var project = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (proposal is not null) foreach (var (k, v) in proposal.Project) project[k] = v;
        var fromCadProject = project.Count > 0;
        var fromDocProject = false;
        if (doc?["project"] is JsonObject pi)
        {
            void Fill(string key, string field) { if (!project.ContainsKey(key) && Str(pi, field) is { Length: > 0 } s) { project[key] = s; fromDocProject = true; } }
            Fill("title", "name"); Fill("location", "location"); Fill("summary", "summary"); Fill("client", "client_name"); Fill("address", "address");
        }
        var meta = odp.Meta();
        if (!project.ContainsKey("title") && !string.IsNullOrWhiteSpace(meta.Name)) project["title"] = meta.Name;
        var persona = doc?["parties"]?["active"]?.GetValue<string>() is "contractor" ? "Contractor" : "Project Manager";
        var cfgProject = new ConfigProject
        {
            Title = P(project, "title"), Client = P(project, "client"), Owner = P(project, "owner"), Location = P(project, "location"), Address = P(project, "address"),
            Pid = P(project, "pid"), SiteArea = P(project, "siteArea"), PlotUse = P(project, "plotUse"), Authority = P(project, "authority"), Date = P(project, "date"),
            Summary = P(project, "summary"), Code = P(project, "code"), Persona = persona,
        };
        if (fromCadProject) sources.Add("MynaCAD's proposal");
        if (fromDocProject) sources.Add("the MynaOps project");
        var team = new ConfigTeam { Architect = P(project, "architect"), Engineer = P(project, "engineer") };

        // ---- units, layer sets, tabs, scheme: only MynaCAD knows them
        var unit = proposal?.DrawingUnit is { } u && ProjectConfig.UnitNames.Contains(u) ? u : "mm";
        if (proposal?.DrawingUnit is null) notes.Add("The drawing unit is not known (MynaCAD has not written a proposal): millimetres is offered.");
        var sets = proposal?.LayerSets.Where(s => ProjectConfig.LayerSetNames.Contains(s)).Distinct().ToList() ?? new List<string>();
        var tabs = (proposal?.Tabs ?? Array.Empty<ConfigProposalTab>())
            .Select(t => new ConfigTabItem { Tid = t.Tid, Code = "", Name = t.Name, LayerPrefix = t.LayerPrefix, Layers = t.Layers, Blocks = t.Blocks }).ToList();
        var scheme = proposal?.Scheme ?? NewProjectBuilder.DefaultScheme;

        var cfg = new ProjectConfig
        {
            Schema = new ConfigSchema(),
            Project = cfgProject,
            Team = team,
            Units = new ConfigUnits { DrawingUnit = unit, Locked = proposal?.HasContent == true },
            Levels = new ConfigLevels
            {
                NextCid = nextCid, OnePerFloor = ordered.Count > 1,
                Defaults = new ConfigLevelDefaults { HeightMm = ordered.FirstOrDefault(l => l.HeightMm > 0)?.HeightMm ?? 3000, SlabMm = 150, BeamMm = 450 },
                Items = ordered,
            },
            LayerSets = new ConfigLayerSets { Sets = sets },
            Tabs = new ConfigTabs { NextTid = tabs.Count == 0 ? 1u : tabs.Max(t => t.Tid) + 1, Items = tabs },
            Sheets = new ConfigSheets { Scheme = scheme },
            Naming = new ConfigNaming(),
            TitleBlock = new ConfigTitleBlock(),
        };
        if (sources.Count == 0) sources.Add("defaults");
        return new AdoptionProposal(cfg, sources.Distinct().ToList(), notes);
    }

    /// <summary>
    /// Waits until MynaCAD's <c>config.proposal</c> is in the file (MynaCAD writes it when it opens an unadopted project, 4.5 step 2). True when it is
    /// there, false after <paramref name="timeout"/> or when cancelled. A file that is briefly busy counts as "not yet".
    /// </summary>
    public static async Task<bool> WaitForCadProposalAsync(string path, TimeSpan timeout, CancellationToken ct = default)
    {
        var until = DateTime.UtcNow + timeout;
        while (true)
        {
            try { if (Propose(path).FromCad) return true; }
            catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException or ConfigException or InvalidOperationException) { }
            if (ct.IsCancellationRequested || DateTime.UtcNow >= until) return false;
            await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Adopt: a <c>manual</c> backup of the file, then the configuration in one revision. Throws (and writes nothing) when the file is held by MynaCAD:
    /// adoption is done from MynaOps with the file closed in MynaCAD, so no drawing is saved under it. Returns what was written and the backup's path.
    /// </summary>
    public static AdoptionResult Adopt(string path, ProjectConfig config, Func<string, string>? backup = null)
    {
        if (ProjectLease.IsHeld(path, ProjectLease.AppCad))
            throw new InvalidOperationException("MynaCAD has this project open. Close it in MynaCAD, then adopt the project.");
        config.Validate();
        var kept = (backup ?? (p => ProjectDatabase.Backup(p, "manual")))(path);
        using var odp = OdpFile.Open(path);
        var written = ProjectConfig.Write(odp, config);
        return new AdoptionResult(written, kept);
    }

    // ---- helpers

    static IEnumerable<JsonObject?> SafeHeaders(OdpFile odp)
    {
        IReadOnlyList<OdpTakeoffHeader> headers;
        try { headers = odp.TakeoffHeaders(); }
        catch (Microsoft.Data.Sqlite.SqliteException) { yield break; }
        foreach (var h in headers)
        {
            JsonObject? o = null;
            if (h.Json.Length <= 8 * 1024 * 1024)
            {
                try { o = JsonNode.Parse(h.Json) as JsonObject; } catch (System.Text.Json.JsonException) { }
            }
            yield return o;
        }
    }

    static void Merge(List<(uint? Id, ConfigLevelItem Item)> levels, string name, double? slab, double? beam)
    {
        var i = levels.FindIndex(l => string.Equals(l.Item.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (i < 0) return;
        var (id, l) = levels[i];
        levels[i] = (id, l with { SlabMm = l.SlabMm ?? slab, BeamMm = l.BeamMm ?? beam });
    }

    static string P(Dictionary<string, string> p, string key) => p.TryGetValue(key, out var v) ? v : "";

    static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static double? Num(JsonObject o, string key)
    {
        if (o[key] is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d) && double.IsFinite(d)) return d;
        if (v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) && double.IsFinite(p)) return p;
        return null;
    }

    static uint? UInt(JsonObject o, string key)
    {
        if (o[key] is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var l) && l >= 0 && l <= uint.MaxValue) return (uint)l;
        if (v.TryGetValue<double>(out var d) && d >= 0 && d <= uint.MaxValue && Math.Abs(d - Math.Round(d)) < 1e-9) return (uint)d;
        return null;
    }
}
