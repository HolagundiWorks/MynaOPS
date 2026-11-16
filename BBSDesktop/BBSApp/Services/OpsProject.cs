// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using Office.Shared;

namespace BBSApp.Services;

/// <summary>
/// The Ops-first state of the open project (docs/ops-first-workflow.md): MynaOps' lease and session (<see cref="ProjectSession"/>), MynaCAD's
/// launcher, the recent list, and the glue between the Settings and Levels pages and <c>config.*</c>. One project is open at a time (Q6: a second
/// MynaOps window opens a second project).
/// </summary>
public static class OpsProject
{
    public static ProjectSession? Session { get; private set; }
    public static MynaCadLauncher Launcher { get; set; } = new();
    public static RecentProjects Recent { get; } = new();

    /// <summary>Raised (on any thread) when the session, its online state or MynaCAD's state may have changed.</summary>
    public static event Action? Changed;

    public static string? Path => Session?.Path;
    public static bool HasProject => Session is not null;
    public static bool Online => Session?.Online ?? false;

    public static void NotifyChanged() => Changed?.Invoke();

    /// <summary>
    /// Take the lease of <paramref name="path"/> without changing the project this MynaOps holds. Returns the refusal and no session when another
    /// MynaOps has it, or, unless <paramref name="takeOver"/>, when the file shows a live session of another MynaOps. The caller calls <see cref="Commit"/>
    /// once the project has been read, or disposes the session when it has not.
    /// </summary>
    public static SessionOpen TryOpen(string path, bool takeOver = false)
    {
        // The same project again: the session already held.
        if (Session is { } cur && string.Equals(cur.Path, System.IO.Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            return new SessionOpen(cur, SessionRefusal.None);
        return ProjectSession.Open(path, takeOver);
    }

    /// <summary>Make <paramref name="opened"/> the held project (the one held before is released) and put it first in the recent list.</summary>
    public static void Commit(ProjectSession opened)
    {
        if (!ReferenceEquals(Session, opened))
        {
            Session?.Dispose();
            Session = opened;
        }
        Recent.Add(opened.Path);
        Changed?.Invoke();
    }

    /// <summary>Let go of the project (the lease, the holder record). MynaCAD is not touched.</summary>
    public static void Detach()
    {
        Session?.Dispose();
        Session = null;
        Changed?.Invoke();
    }

    /// <summary>Ask a running MynaCAD to close and wait until its lease is gone (the user may be asked about unsaved work). True when it is gone.</summary>
    public static async Task<bool> CloseCadAsync(string path, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!ProjectLease.IsHeld(path, ProjectLease.AppCad)) return true;
        await Launcher.RequestCloseAsync(path, "ask", ct).ConfigureAwait(false);
        var until = DateTime.UtcNow + timeout;
        while (ProjectLease.IsHeld(path, ProjectLease.AppCad))
        {
            if (ct.IsCancellationRequested || DateTime.UtcNow >= until) return false;
            await Task.Delay(200, CancellationToken.None).ConfigureAwait(false);
        }
        return true;
    }

    public static async Task<OfflineResult> GoOfflineAsync(CancellationToken ct = default)
    {
        if (Session is not { } s) return new OfflineResult(false, "No project is open.");
        var r = await s.GoOfflineAsync(c => Launcher.RequestCloseAsync(s.Path, "ask", c), TimeSpan.FromSeconds(120), ct).ConfigureAwait(false);
        Changed?.Invoke();
        return r;
    }

    public static bool GoOnline(out string message)
    {
        if (Session is not { } s) { message = "No project is open."; return false; }
        var ok = s.GoOnline(out message);
        Changed?.Invoke();
        return ok;
    }

    // ---- the Settings and Levels pages <-> config.*

    /// <summary>The storeys the Levels page holds, without the plinth (<c>Lvl0</c> is AQC's own and is not configuration).</summary>
    public static IReadOnlyList<StoreLevel> StoreLevels(ProjectStore store) =>
        store.Levels.Where(l => l.Id != "Lvl0").Select(l => new StoreLevel(l.Name, l.HeightMm, l.SlabThicknessMm, l.BeamDepthMm, l.ConfigCid)).ToList();

    /// <summary>After a publish: the page's storeys learn the <c>cid</c> the configuration gave them (a new storey gets one on its first publish).</summary>
    private static void RememberCids(ProjectStore store, OdpFile odp)
    {
        var items = ProjectConfig.Read(odp).Read.Config.Levels?.Items;
        if (items is null) return;
        foreach (var l in store.Levels.Where(l => l.Id != "Lvl0" && l.ConfigCid is null))
            if (items.FirstOrDefault(i => string.Equals(i.Name, l.Name.Trim(), StringComparison.OrdinalIgnoreCase)) is { } item) l.ConfigCid = item.Cid;
    }

    public static StoreDetails StoreDetailsOf(ProjectStore store) =>
        new(store.Name, store.Info.ClientName, store.Info.Location, store.Info.Address, store.Info.Summary,
            store.Parties.Active == PartyRole.Contractor ? "Contractor" : "Project Manager");

    /// <summary>
    /// Save's second half: put the page values into <c>config.*</c> (one revision, nothing when nothing differs). Returns null for a file without
    /// configuration or a project that is not a project file.
    /// </summary>
    public static ConfigWritten? PublishConfig(ProjectStore store, string path)
    {
        if (!ProjectFiles.IsOdp(path)) return null;
        using var odp = OdpFile.Open(path);
        var written = ConfigPublisher.Publish(odp, StoreDetailsOf(store), StoreLevels(store));
        if (written is not null) RememberCids(store, odp);
        if (written is { Changed.Count: > 0 }) Services.Odp.ServedProject.NoteOwnRevision(path, written.Revision);
        return written;
    }

    /// <summary>
    /// A new project's in-memory project from its configuration: the details, the persona, and the storeys (the plinth, then every level from the
    /// ground floor up; basements are not part of the quantities model).
    /// </summary>
    public static void SeedStoreFromConfig(ProjectStore store, ProjectConfig cfg)
    {
        store.Reset();
        var p = cfg.Project;
        if (p is not null)
        {
            store.Name = p.Title;
            store.Info.ClientName = p.Client;
            store.Info.Location = p.Location;
            store.Info.Address = p.Address;
            store.Info.Summary = p.Summary;
            store.Parties.Active = string.Equals(p.Persona, "Contractor", StringComparison.OrdinalIgnoreCase) ? PartyRole.Contractor : PartyRole.PM;
        }
        if (cfg.Team is { } t) store.Info.PreparedByName = string.IsNullOrWhiteSpace(t.Engineer) ? t.Architect : t.Engineer;
        store.Levels.Clear();
        var d = cfg.Levels?.Defaults;
        store.Levels.Add(new LevelDef { Id = "Lvl0", Name = "Plinth", HeightMm = 3200, SlabThicknessMm = d?.SlabMm ?? 150, BeamDepthMm = d?.BeamMm ?? 450 });
        var n = 1;
        foreach (var l in (cfg.Levels?.Items ?? Array.Empty<ConfigLevelItem>()).Where(i => i.ElevationMm >= 0).OrderBy(i => i.ElevationMm))
            store.Levels.Add(new LevelDef { Id = "Lvl" + n++, Name = l.Name, HeightMm = l.HeightMm, SlabThicknessMm = l.SlabMm ?? d?.SlabMm ?? 150, BeamDepthMm = l.BeamMm ?? d?.BeamMm ?? 450, ConfigCid = l.Cid });
        store.EnsureDefaultLevels();
    }
}
