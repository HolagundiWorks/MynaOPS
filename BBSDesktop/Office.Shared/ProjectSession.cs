// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using Microsoft.Data.Sqlite;

namespace Office.Shared;

/// <summary>Why <see cref="ProjectSession.Open"/> did not give a session.</summary>
public enum SessionRefusal
{
    None,
    /// <summary>Another MynaOps on this PC holds the project's lease (<see cref="SessionOpen.Holder"/>).</summary>
    OtherMynaOps,
    /// <summary>The file's <c>session.holder</c> shows a MynaOps that beat within two minutes on another machine or process: warn, offer Take over.</summary>
    LiveElsewhere,
}

public sealed record SessionOpen(ProjectSession? Session, SessionRefusal Refusal, LeaseInfo? Lease = null, SessionHolder? Holder = null);

/// <summary>What <see cref="ProjectSession.GoOfflineAsync"/> did.</summary>
public sealed record OfflineResult(bool Offline, string Message);

/// <summary>
/// MynaOps' hold on one project (docs/ops-first-workflow.md sections 1.3 and 2.3): its <c>.ops.lock</c> lease and the advisory <c>session.holder</c>
/// heartbeat. <b>Online</b> means MynaOps holds both. <b>Offline</b> means MynaCAD was asked to close and has, the write-ahead log was folded in
/// (checkpoint), the holder record was cleared and the lease released; Compact, Restore and copying the file are then safe. Going online takes the
/// lease and the record again.
/// </summary>
public sealed class ProjectSession : IDisposable
{
    readonly Func<DateTime> _now;
    ProjectLease? _lease;
    Timer? _heartbeat;
    DateTime _started;
    readonly object _gate = new();

    public string Path { get; }
    public bool Online { get; private set; }

    /// <summary>The last problem writing the holder record (null when it is fine). A project on a read-only share still opens.</summary>
    public string? HolderProblem { get; private set; }

    ProjectSession(string path, ProjectLease lease, Func<DateTime> now)
    {
        Path = path;
        _lease = lease;
        _now = now;
    }

    /// <summary>
    /// Take the lease and write the holder record. Returns a refusal (and no session) when another MynaOps on this PC holds the lease, or, unless
    /// <paramref name="takeOver"/>, when the file shows a live session of another MynaOps.
    /// </summary>
    public static SessionOpen Open(string path, bool takeOver = false, Func<DateTime>? now = null, bool heartbeat = true)
    {
        path = System.IO.Path.GetFullPath(path);
        now ??= () => DateTime.UtcNow;
        var lease = ProjectLease.TryAcquire(path, ProjectLease.AppOps);
        if (lease is null) return new SessionOpen(null, SessionRefusal.OtherMynaOps, ProjectLease.Describe(path, ProjectLease.AppOps));
        try
        {
            var holder = ReadHolder(path);
            if (!takeOver && holder is not null && holder.IsLiveElsewhere(now()))
            {
                lease.Dispose();
                return new SessionOpen(null, SessionRefusal.LiveElsewhere, null, holder);
            }
        }
        catch { lease.Dispose(); throw; }
        var s = new ProjectSession(path, lease, now) { Online = true, _started = now() };
        s.WriteHolder();
        if (heartbeat) s.StartHeartbeat();
        return new SessionOpen(s, SessionRefusal.None);
    }

    /// <summary>The <c>session.holder</c> record of a file (null when there is none, it is damaged, or the file cannot be read).</summary>
    public static SessionHolder? ReadHolder(string path)
    {
        try
        {
            using var odp = OdpFile.Open(path);
            return SessionHolder.Parse(odp.LocalMeta(SessionHolder.Key));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or SqliteException or UnauthorizedAccessException) { return null; }
    }

    void StartHeartbeat() => _heartbeat = new Timer(_ => Beat(), null, SessionHolder.Heartbeat, SessionHolder.Heartbeat);

    /// <summary>Refresh the heartbeat now (the timer does it every 30 s).</summary>
    public void Beat()
    {
        lock (_gate)
        {
            if (!Online) return;
            WriteHolder();
        }
    }

    void WriteHolder()
    {
        try
        {
            using var odp = OdpFile.Open(Path);
            odp.PutLocalMeta(SessionHolder.Key, new SessionHolder(Environment.MachineName, Environment.UserName, Environment.ProcessId, _started, _now()).ToJson());
            HolderProblem = null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or SqliteException or UnauthorizedAccessException)
        {
            HolderProblem = ex.Message;      // a busy or read-only file: the lease still holds on this PC
        }
    }

    void ClearHolder()
    {
        try
        {
            using var odp = OdpFile.Open(Path);
            if (SessionHolder.Parse(odp.LocalMeta(SessionHolder.Key)) is { } h && h.OpsPid == Environment.ProcessId && string.Equals(h.Machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                odp.DeleteLocalMeta(SessionHolder.Key);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or SqliteException or UnauthorizedAccessException) { }
    }

    /// <summary>True when a MynaCAD holds this project's lease.</summary>
    public bool CadRunning => ProjectLease.IsHeld(Path, ProjectLease.AppCad);

    /// <summary>
    /// Database offline: ask MynaCAD to close (<paramref name="requestClose"/> sends the pipe request; a MynaCAD that cannot be reached is waited for
    /// too, in case the user closes it), wait until its lease is gone or <paramref name="timeout"/>, fold the log into the file, clear the holder record
    /// and release the lease. When MynaCAD is still there after the wait nothing changes and the session stays online.
    /// </summary>
    public async Task<OfflineResult> GoOfflineAsync(Func<CancellationToken, Task<bool>>? requestClose, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!Online) return new OfflineResult(true, "The project database is already offline.");
        if (CadRunning)
        {
            if (requestClose is not null)
            {
                try { await requestClose(ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException) { }
            }
            var until = DateTime.UtcNow + timeout;
            while (CadRunning)
            {
                if (ct.IsCancellationRequested) return new OfflineResult(false, "Cancelled; the project database is still online.");
                if (DateTime.UtcNow >= until)
                    return new OfflineResult(false, "MynaCAD is still open for this project (it may be asking about unsaved work). The project database stays online.");
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
        }
        lock (_gate)
        {
            _heartbeat?.Dispose();
            _heartbeat = null;
            var folded = Fold();
            ClearHolder();
            _lease?.Dispose();
            _lease = null;
            Online = false;
            return new OfflineResult(true, folded ? "The project database is offline: the write-ahead log is folded into the file." : "The project database is offline. The log could not be folded in completely (another program is reading the file).");
        }
    }

    bool Fold()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            using var odp = OdpFile.Open(Path);
            return odp.Checkpoint();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or SqliteException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Database online: take the lease and the holder record again. Fails (false, with the reason) when someone else took the project meanwhile.</summary>
    public bool GoOnline(out string message)
    {
        lock (_gate)
        {
            if (Online) { message = "The project database is already online."; return true; }
            var lease = ProjectLease.TryAcquire(Path, ProjectLease.AppOps);
            if (lease is null) { message = "Another MynaOps took this project while it was offline."; return false; }
            _lease = lease;
            Online = true;
            _started = _now();
            WriteHolder();
            StartHeartbeat();
            message = "The project database is online.";
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _heartbeat?.Dispose();
            _heartbeat = null;
            if (Online) ClearHolder();
            _lease?.Dispose();
            _lease = null;
            Online = false;
        }
    }
}
