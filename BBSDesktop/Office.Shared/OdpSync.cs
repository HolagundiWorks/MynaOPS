// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Office.Shared;

/// <summary>What the user is told about the shared project file (MynaCAD <c>docs/live-sync.md</c>; the same words in both programs).</summary>
public enum SyncState { None, Synced, Updated, OutOfDate, Locked, Replaced, Unreadable, Missing }

/// <summary>The take-off stamp MynaCAD writes at every record (<c>meta.sync.takeoff.&lt;slot&gt;</c>).</summary>
public sealed record SyncTakeoffStamp(uint Slot, long Revision, string Recorded, IReadOnlyList<(string LevelId, long Revision)> Levels);

/// <summary>What a read-only look at the file found.</summary>
public sealed record SyncProbe(string ProjectId, long Revision, string? LastApp, long? LastRevision, string? LastTime, long? DrawingRevision, IReadOnlyList<SyncTakeoffStamp> Takeoff)
{
    /// <summary>The slots whose take-off is older than the drawing: the drawing was saved after it was recorded.</summary>
    public IReadOnlyList<uint> StaleSlots =>
        DrawingRevision is { } d ? Takeoff.Where(t => d > t.Revision).Select(t => t.Slot).ToList() : Array.Empty<uint>();
}

/// <summary>The result of a probe: a probe, or the reason there is none.</summary>
public sealed record SyncPeek(SyncState Failure, SyncProbe? Probe, string? Reason)
{
    public static SyncPeek Ok(SyncProbe p) => new(SyncState.Synced, p, null);
    public bool IsOk => Probe is not null;
}

/// <summary>The revision a program has accepted for a project file: the one it read when it opened the file, or wrote itself.</summary>
public sealed record SyncAccepted(long Revision, string ProjectId);

/// <summary>The status shown to the user.</summary>
public sealed record SyncStatus(SyncState State, string Text, SyncProbe? Probe, string? Reason)
{
    public static readonly SyncStatus None = new(SyncState.None, "", null, null);
}

/// <summary>
/// Live sync, the read-only side (MynaCAD <c>docs/live-sync.md</c>; the Rust twin is <c>aad_odp::probe</c> and <c>aad_engine::projectsync</c>). It opens the
/// project file read-only, waits a quarter of a second at most for a writer, reads a few <c>meta</c> keys and never changes anything. Every problem is a
/// <see cref="SyncPeek"/>, never an exception.
/// </summary>
public static class OdpSync
{
    /// <summary>The name this program writes into <c>meta.sync.last</c>.</summary>
    public const string App = "mynaops";

    const int MaxValue = 4096;
    const int MaxStamps = 64;

    public static SyncPeek Probe(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return new(SyncState.Missing, null, null);
        try
        {
            var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1 }.ToString();
            using var db = new SqliteConnection(cs);
            db.Open();
            using (var busy = db.CreateCommand()) { busy.CommandText = "PRAGMA busy_timeout = 250"; busy.ExecuteNonQuery(); }
            return Read(db);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6) { return new(SyncState.Locked, null, null); }
        catch (SqliteException ex) { return new(SyncState.Unreadable, null, ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return new(SyncState.Unreadable, null, ex.Message); }
    }

    static string? Meta(SqliteConnection db, string key)
    {
        using var c = db.CreateCommand();
        c.CommandText = "SELECT substr(value, 1, $n) FROM meta WHERE key = $k";
        c.Parameters.AddWithValue("$n", MaxValue);
        c.Parameters.AddWithValue("$k", key);
        return c.ExecuteScalar() as string;
    }

    static SyncPeek Read(SqliteConnection db)
    {
        var format = Meta(db, "format");
        if (!SuiteWire.IsProjectFormat(format)) return new(SyncState.Unreadable, null, $"its format is {format ?? "missing"}");
        if (long.TryParse(Meta(db, "version"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > OdpFile.Version)
            return new(SyncState.Unreadable, null, $"the file is version {v}; this program reads version {OdpFile.Version}");
        var projectId = Meta(db, "project_id") ?? "";
        var revision = long.TryParse(Meta(db, "revision"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : 0;
        string? app = null, time = null; long? lastRev = null;
        if (Meta(db, "sync.last") is { } last)
        {
            try
            {
                using var doc = JsonDocument.Parse(last);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    app = Text(root, "app"); time = Text(root, "time");
                    if (root.TryGetProperty("revision", out var lr) && lr.ValueKind == JsonValueKind.Number && lr.TryGetInt64(out var l)) lastRev = l;
                }
            }
            catch (JsonException) { }
        }
        long? drawingRevision = null;
        try
        {
            using var c = db.CreateCommand();
            c.CommandText = "SELECT max(revision) FROM drawing";
            if (c.ExecuteScalar() is long d) drawingRevision = d;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1) { }   // a file from an older program may lack the table
        var stamps = new List<SyncTakeoffStamp>();
        using (var c = db.CreateCommand())
        {
            c.CommandText = "SELECT key, substr(value, 1, $n) FROM meta WHERE key LIKE 'sync.takeoff.%' LIMIT $m";
            c.Parameters.AddWithValue("$n", MaxValue);
            c.Parameters.AddWithValue("$m", MaxStamps);
            using var rd = c.ExecuteReader();
            while (rd.Read())
            {
                if (!uint.TryParse(rd.GetString(0)["sync.takeoff.".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var slot)) continue;
                var stamp = Stamp(slot, rd.GetString(1));
                if (stamp is not null) stamps.Add(stamp);
            }
        }
        return SyncPeek.Ok(new SyncProbe(projectId, revision, app, lastRev, time, drawingRevision, stamps));
    }

    static string? Text(JsonElement o, string name) =>
        o.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? Clip(e.GetString()) : null;

    static string? Clip(string? s) => s is null ? null : s.Length <= 64 ? s : s[..64];

    static SyncTakeoffStamp? Stamp(uint slot, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var o = doc.RootElement;
            if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty("revision", out var rv) || rv.ValueKind != JsonValueKind.Number || !rv.TryGetInt64(out var rev)) return null;
            var levels = new List<(string, long)>();
            if (o.TryGetProperty("levels", out var lv) && lv.ValueKind == JsonValueKind.Object)
                foreach (var p in lv.EnumerateObject().Take(256))
                    levels.Add((p.Name, p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("revision", out var x) && x.ValueKind == JsonValueKind.Number && x.TryGetInt64(out var xr) ? xr : rev));
            return new SyncTakeoffStamp(slot, rev, Text(o, "recorded") ?? "", levels);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Decide the state from what was accepted and what a probe found. Pure.</summary>
    public static SyncStatus Evaluate(SyncAccepted? accepted, SyncPeek peek)
    {
        if (peek.Probe is not { } p)
        {
            var s = peek.Failure;
            return new SyncStatus(s, Describe(s, null), null, peek.Reason);
        }
        var state = accepted switch
        {
            { } a when a.ProjectId != p.ProjectId || p.Revision < a.Revision => SyncState.Replaced,
            { } a when p.Revision != a.Revision => SyncState.Updated,
            _ when p.StaleSlots.Count > 0 => SyncState.OutOfDate,
            _ => SyncState.Synced,
        };
        return new SyncStatus(state, Describe(state, p), p, null);
    }

    /// <summary>The words shown in both programs. <paramref name="localTime"/> converts the UTC text of <c>sync.last</c> for display (tests pass a fixed zone).</summary>
    public static string Describe(SyncState state, SyncProbe? p, TimeZoneInfo? localTime = null) => state switch
    {
        SyncState.None => "",
        SyncState.Synced => "Synced",
        SyncState.Updated => Updated(p, localTime ?? TimeZoneInfo.Local),
        SyncState.OutOfDate => "Take-off out of date",
        SyncState.Locked => "Locked",
        SyncState.Replaced => "Project file was replaced",
        SyncState.Unreadable => "Project file unreadable",
        SyncState.Missing => "Project file missing",
        _ => "",
    };

    static string Updated(SyncProbe? p, TimeZoneInfo zone)
    {
        var who = p?.LastApp switch { "mynacad" => "MynaCAD", "mynaops" => "MynaOps", _ => "" };
        if (who.Length == 0) return "Updated elsewhere";
        if (p?.LastTime is { } t && DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var utc))
            return $"Updated by {who} at {TimeZoneInfo.ConvertTime(utc, zone).ToString("HH:mm", CultureInfo.InvariantCulture)}";
        return $"Updated by {who}";
    }
}
