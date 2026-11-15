// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Office.Shared;

/// <summary>Who holds a lease, from the small JSON body in the lease file (display only: the lock is the truth).</summary>
public sealed record LeaseInfo(string App, int Pid, DateTime StartedUtc, string Project);

/// <summary>
/// The same-PC lease of one project file (docs/ops-first-workflow.md section 2.3). Each app, when it opens the project, creates
/// <c>%LOCALAPPDATA%\O-Studio\leases\&lt;path hash&gt;.ops.lock</c> (MynaOps) or <c>.cad.lock</c> (MynaCAD) and keeps it open until it closes the project or ends.
/// The lock is the open handle itself: it goes with the process, so a crash never leaves a stale lease. The path hash is the first 16 hex digits of
/// SHA-256 of the lower-case full path (the same hash names MynaCAD's mutex and activation pipe, <see cref="MynaCadLauncher"/>).
/// <para>
/// How the handle is held: opened for read and write, shared for reading only, delete on close. A second holder (it asks for write) and a probe
/// (<see cref="IsHeld"/>, which asks for write too) fail with a sharing violation, whichever sharing mode the holder used; a reader of the body
/// (read access, sharing everything) succeeds against this holder and fails against one that shares nothing, which then shows no detail.
/// </para>
/// </summary>
public sealed class ProjectLease : IDisposable
{
    public const string AppOps = "ops";
    public const string AppCad = "cad";

    /// <summary>The leases folder. <c>OSTUDIO_LEASE_DIR</c> moves it (tests, and a stub standing in for MynaCAD).</summary>
    public static string Directory =>
        Environment.GetEnvironmentVariable("OSTUDIO_LEASE_DIR") is { Length: > 0 } d ? d : SuiteWire.LocalPath("leases");

    /// <summary>First 16 hex digits of SHA-256 of the lower-case full path (UTF-8, backslash separators as <see cref="Path.GetFullPath(string)"/> gives them).</summary>
    public static string PathHash(string project)
    {
        var full = Path.GetFullPath(project).ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..16].ToLowerInvariant();
    }

    public static string LockPath(string project, string app) => Path.Combine(Directory, $"{PathHash(project)}.{app}.lock");

    // The leases this process holds, so "held by another process" can leave our own out.
    static readonly ConcurrentDictionary<string, ProjectLease> Own = new(StringComparer.OrdinalIgnoreCase);

    readonly FileStream _stream;
    readonly string _key;

    public string Project { get; }
    public string App { get; }
    public string LockFile { get; }

    ProjectLease(string project, string app, string lockFile, FileStream stream)
    {
        Project = project;
        App = app;
        LockFile = lockFile;
        _stream = stream;
        _key = lockFile;
    }

    /// <summary>
    /// Take the lease of <paramref name="project"/> for <paramref name="app"/>. Returns <c>null</c> when another process (or this one) holds it already.
    /// </summary>
    public static ProjectLease? TryAcquire(string project, string app = AppOps)
    {
        project = Path.GetFullPath(project);
        var file = LockPath(project, app);
        if (Own.ContainsKey(file)) return null;
        System.IO.Directory.CreateDirectory(Directory);
        FileStream stream;
        try
        {
            stream = new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.DeleteOnClose);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        var lease = new ProjectLease(project, app, file, stream);
        try
        {
            var body = new JsonObject
            {
                ["app"] = app,
                ["pid"] = Environment.ProcessId,
                ["started_utc"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
                ["project"] = project,
            }.ToJsonString();
            var bytes = Encoding.UTF8.GetBytes(body);
            stream.SetLength(0);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }
        catch (IOException) { /* the body is for display only */ }
        if (!Own.TryAdd(file, lease)) { lease.Dispose(); return null; }
        return lease;
    }

    /// <summary>True when some process holds the lease (this one included).</summary>
    public static bool IsHeld(string project, string app)
    {
        var file = LockPath(project, app);
        if (Own.ContainsKey(file)) return true;
        return HeldByOther(file);
    }

    /// <summary>True when a process other than this one holds the lease.</summary>
    public static bool IsHeldByOther(string project, string app)
    {
        var file = LockPath(project, app);
        if (Own.ContainsKey(file)) return false;
        return HeldByOther(file);
    }

    static bool HeldByOther(string file)
    {
        if (!File.Exists(file)) return false;
        try
        {
            using var probe = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            return false;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    /// <summary>What the lease file says about its holder, or <c>null</c> when there is no lease, it cannot be read, or it is not a lease body.</summary>
    public static LeaseInfo? Describe(string project, string app)
    {
        var file = LockPath(project, app);
        try
        {
            using var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (s.Length is 0 or > 4096) return null;
            using var r = new StreamReader(s, Encoding.UTF8);
            return ParseBody(r.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static LeaseInfo? ParseBody(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o) return null;
            var pid = o["pid"]?.GetValue<int>() ?? 0;
            if (pid <= 0) return null;
            DateTime.TryParse(o["started_utc"]?.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var started);
            return new LeaseInfo(o["app"]?.GetValue<string>() ?? "", pid, started, o["project"]?.GetValue<string>() ?? "");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { return null; }
    }

    public void Dispose()
    {
        Own.TryRemove(_key, out _);
        _stream.Dispose();     // delete on close
    }
}

/// <summary>
/// The advisory session record of a project file on another machine or a crashed MynaOps (section 2.3): the <b>local</b> meta key
/// <c>session.holder</c>, which MynaOps writes on open, refreshes every 30 seconds and clears on close. A heartbeat younger than
/// <see cref="StaleAfter"/> means somebody has the project open. It makes no revision (a local key, like MynaCAD's <c>native.*</c>), so no sync sees it.
/// </summary>
public sealed record SessionHolder(string Machine, string User, int OpsPid, DateTime StartedUtc, DateTime HeartbeatUtc)
{
    public const string Key = "session.holder";
    public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(120);

    public static SessionHolder ForThisProcess(DateTime? started = null) =>
        new(Environment.MachineName, Environment.UserName, Environment.ProcessId, started ?? DateTime.UtcNow, DateTime.UtcNow);

    public string ToJson() => new JsonObject
    {
        ["machine"] = Machine,
        ["user"] = User,
        ["ops_pid"] = OpsPid,
        ["started_utc"] = Stamp(StartedUtc),
        ["heartbeat_utc"] = Stamp(HeartbeatUtc),
    }.ToJsonString();

    static string Stamp(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Null when the text is not a holder record (never throws).</summary>
    public static SessionHolder? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 4096) return null;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o) return null;
            var machine = o["machine"]?.GetValue<string>() ?? "";
            var user = o["user"]?.GetValue<string>() ?? "";
            var pid = o["ops_pid"]?.GetValue<int>() ?? 0;
            var style = System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal;
            if (!DateTime.TryParse(o["heartbeat_utc"]?.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture, style, out var beat)) return null;
            DateTime.TryParse(o["started_utc"]?.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture, style, out var started);
            return new SessionHolder(machine, user, pid, started, beat);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { return null; }
    }

    /// <summary>True when the record shows a live session that is not this process: a heartbeat within <see cref="StaleAfter"/> of <paramref name="now"/>.</summary>
    public bool IsLiveElsewhere(DateTime now, string? machine = null, int? pid = null)
    {
        machine ??= Environment.MachineName;
        pid ??= Environment.ProcessId;
        var own = string.Equals(Machine, machine, StringComparison.OrdinalIgnoreCase) && OpsPid == pid;
        return !own && now.ToUniversalTime() - HeartbeatUtc.ToUniversalTime() < StaleAfter;
    }
}
