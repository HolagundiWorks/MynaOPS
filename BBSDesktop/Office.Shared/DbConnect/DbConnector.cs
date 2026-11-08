// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

namespace Office.Shared.DbConnect;

public enum DbState
{
    /// <summary>Off (tests, headless) or no project connected yet.</summary>
    Idle,
    Connecting,
    Connected,
    /// <summary>Nobody is signed in to Kivo, or the sign-in was closed.</summary>
    SignedOut,
    /// <summary>Kivo or Myna is not running, or the service cannot be reached.</summary>
    Offline,
    /// <summary>Something this client cannot fix: the file has no project id, Kivo refused the role.</summary>
    Problem,
}

public sealed record DbSnapshot(DbState State, string? Project, string Detail, long Pending)
{
    public static readonly DbSnapshot Idle = new(DbState.Idle, null, "", 0);

    /// <summary>The short text for the status bar.</summary>
    public string Label => State switch
    {
        DbState.Idle => "",
        DbState.Connecting => "Database: connecting",
        DbState.Connected when Pending > 0 => $"Database: connected, {Pending} to send",
        DbState.Connected => "Database: connected",
        DbState.SignedOut => "Database: sign in to Kivo",
        DbState.Offline => "Database: offline",
        _ => "Database: problem",
    };
}

/// <summary>
/// Falcon's connection to the suite database. Falcon and Tren share one <c>.osp</c> file; Myna hosts the database service and Kivo signs the company in.
/// Falcon asks Kivo for a session when a project file is opened, tells the service about the file, and asks it to push each saved revision.
/// It never blocks the UI, never holds a company password, and never applies a newer shared revision on its own.
/// </summary>
public sealed class DbConnector
{
    readonly IBrokerTransport _kivo;
    readonly DbServiceClient _service;
    readonly Action _launchKivo;
    readonly string _app;
    readonly bool _interactive;
    readonly TimeSpan _kivoWait;
    readonly TimeSpan _pushDelay;
    readonly object _gate = new();

    bool _enabled;
    DbSnapshot _snap = DbSnapshot.Idle;
    string? _token;
    (string Id, string Name, string Path)? _project;
    bool _dirty;
    bool _pushing;

    public DbConnector(IBrokerTransport kivo, DbServiceClient service, Action launchKivo, string app = "falcon", bool interactive = true,
        TimeSpan? kivoWait = null, TimeSpan? pushDelay = null, bool enabled = false)
    {
        _kivo = kivo;
        _service = service;
        _launchKivo = launchKivo;
        _app = app;
        _interactive = interactive;
        _kivoWait = kivoWait ?? TimeSpan.FromSeconds(60);
        _pushDelay = pushDelay ?? TimeSpan.FromMilliseconds(1500);
        _enabled = enabled;
    }

    /// <summary>The connector of this process. Off until Falcon's window turns it on, so tests and headless runs never start Kivo.</summary>
    public static DbConnector Instance { get; } = new(new PipeBrokerTransport(), new DbServiceClient(), SessionBroker.LaunchKivo);

    /// <summary>Raised on a background thread whenever <see cref="Snapshot"/> changes; marshal to the UI thread before touching controls.</summary>
    public event Action<DbSnapshot>? Changed;

    public DbSnapshot Snapshot { get { lock (_gate) return _snap; } }

    /// <summary>Turn the connection on. <c>AAD_DB_CONNECT=0</c> (or <c>OSAS_DB_CONNECT=0</c>) keeps it off.</summary>
    public void Enable()
    {
        if (Environment.GetEnvironmentVariable("OSAS_DB_CONNECT") == "0") return;
        lock (_gate) _enabled = true;
    }

    bool Enabled { get { lock (_gate) return _enabled; } }

    void Set(DbState state, string detail, long? pending = null, string? project = null)
    {
        DbSnapshot snap;
        lock (_gate)
        {
            _snap = _snap with { State = state, Detail = detail, Pending = pending ?? _snap.Pending, Project = project ?? _snap.Project };
            snap = _snap;
        }

        Changed?.Invoke(snap);
    }

    string Token(string projectId, bool fresh)
    {
        lock (_gate)
        {
            if (!fresh && _token is not null) return _token;
        }

        SessionBroker.EnsureBroker(_kivo, _launchKivo, _kivoWait);
        var session = SessionBroker.AskSession(_kivo, _app, "db_connect", projectId, _interactive);
        lock (_gate) _token = session.Token;
        return session.Token;
    }

    void BrokerFailed(BrokerException e)
    {
        switch (e.Failure)
        {
            case BrokerFailure.SignedOut:
            case BrokerFailure.Cancelled:
                Set(DbState.SignedOut, e.Message);
                break;
            case BrokerFailure.Unavailable:
                Set(DbState.Offline, e.Message);
                break;
            default:
                Set(DbState.Problem, e.Message);
                break;
        }
    }

    /// <summary>A project file was opened: connect it. Returns at once; the work runs on a background thread.</summary>
    public void ProjectOpened(string ospPath)
    {
        if (!Enabled) return;
        _ = Task.Run(() => ConnectBlocking(ospPath));
    }

    /// <summary>The work of <see cref="ProjectOpened"/>, on the calling thread.</summary>
    public void ConnectBlocking(string ospPath)
    {
        if (!ospPath.EndsWith(OdpFile.Extension, StringComparison.OrdinalIgnoreCase)) return;
        OdpMeta meta;
        try
        {
            using var odp = OdpFile.Open(ospPath);
            meta = odp.Meta();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            Set(DbState.Problem, "The project file could not be read: " + ex.Message);
            return;
        }

        if (meta.ProjectId.Length == 0)
        {
            Set(DbState.Problem, "The project file has no project id.");
            return;
        }

        lock (_gate) _project = (meta.ProjectId, meta.Name, ospPath);
        Set(DbState.Connecting, "Asking Kivo for a session", project: meta.Name);

        var fresh = false;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            string token;
            try
            {
                token = Token(meta.ProjectId, fresh);
            }
            catch (BrokerException e)
            {
                BrokerFailed(e);
                return;
            }

            try
            {
                var done = _service.Connect(token, meta.ProjectId, meta.Name, ospPath, meta.Revision).GetAwaiter().GetResult();
                var detail = done.Action switch
                {
                    "pull" => "The shared copy has a newer revision; it is not applied until you choose to.",
                    "conflict" => "The shared copy and this file both changed; nothing was overwritten.",
                    _ => "Connected to the suite database.",
                };
                Set(DbState.Connected, detail);
                if (done.Action == "push") PushNow(meta.Revision);
                return;
            }
            catch (ServiceException e) when (e.Failure == ServiceFailure.Unauthorized && !fresh)
            {
                fresh = true;
            }
            catch (ServiceException e)
            {
                ServiceFailed(e);
                return;
            }
        }
    }

    void ServiceFailed(ServiceException e)
    {
        switch (e.Failure)
        {
            case ServiceFailure.Unreachable:
                Set(DbState.Offline, e.Message + " Start Myna to sync.");
                break;
            case ServiceFailure.Unauthorized:
                Set(DbState.SignedOut, "Kivo's session was refused.");
                break;
            case ServiceFailure.Conflict:
                Set(DbState.Connected, "The shared copy has a newer revision; this save was not sent. Nothing was overwritten.");
                break;
            default:
                Set(DbState.Problem, e.Message);
                break;
        }
    }

    /// <summary>A revision was saved into the project file: send it. Several saves in a row go as one push.</summary>
    public void ProjectSaved(string ospPath)
    {
        if (!Enabled) return;
        lock (_gate)
        {
            var same = _project is { } p && string.Equals(p.Path, ospPath, StringComparison.OrdinalIgnoreCase);
            if (!same || _snap.State == DbState.Idle)
            {
                // Not connected yet (a new project file, or Kivo was not there at open): try to connect instead.
                _ = Task.Run(() => ConnectBlocking(ospPath));
                return;
            }

            _dirty = true;
            if (_pushing) return;
            _pushing = true;
        }

        _ = Task.Run(PushLoop);
    }

    void PushLoop()
    {
        while (true)
        {
            Thread.Sleep(_pushDelay);
            string? path;
            lock (_gate)
            {
                if (!_dirty)
                {
                    _pushing = false;
                    return;
                }

                _dirty = false;
                path = _project?.Path;
            }

            if (path is null)
            {
                lock (_gate) _pushing = false;
                return;
            }

            try
            {
                using var odp = OdpFile.Open(path);
                PushNow(odp.Revision());
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                Set(DbState.Problem, "The project file could not be read: " + ex.Message);
            }
        }
    }

    /// <summary>Send <paramref name="revision"/> of the connected project, on the calling thread.</summary>
    public void PushNow(long revision)
    {
        (string Id, string Name, string Path) project;
        lock (_gate)
        {
            if (_project is not { } p) return;
            project = p;
        }

        var fresh = false;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            string token;
            try
            {
                token = Token(project.Id, fresh);
            }
            catch (BrokerException e)
            {
                BrokerFailed(e);
                return;
            }

            try
            {
                var remote = _service.Push(token, project.Id, revision).GetAwaiter().GetResult();
                long pending = 0;
                try { pending = _service.Project(token, project.Id).GetAwaiter().GetResult().Pending; } catch (ServiceException) { }
                Set(DbState.Connected, $"{project.Name}: revision {remote} sent to the suite database.", pending);
                return;
            }
            catch (ServiceException e) when (e.Failure == ServiceFailure.Unauthorized && !fresh)
            {
                fresh = true;
            }
            catch (ServiceException e)
            {
                ServiceFailed(e);
                return;
            }
        }
    }
}
