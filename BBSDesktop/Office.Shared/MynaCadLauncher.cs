// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace Office.Shared;

/// <summary>One request on MynaCAD's activation pipe (docs/ops-first-workflow.md section 2.1): a JSON line, <c>cmd</c> and its arguments side by side.</summary>
public static class CadRequest
{
    public static string Activate() => Line("activate");
    public static string Import(string path) => Line("import", ("path", path));
    /// <param name="save"><c>ask</c>, <c>save</c> or <c>discard</c>.</param>
    public static string Close(string save = "ask") => Line("close", ("save", save is "ask" or "save" or "discard" ? save : throw new ArgumentException("save is ask, save or discard", nameof(save))));
    public static string SyncSet(bool on) => new JsonObject { ["cmd"] = "sync.set", ["on"] = on }.ToJsonString();
    public static string SyncNow() => Line("sync.now");
    public static string Status() => Line("status");

    static string Line(string cmd, params (string Key, string Value)[] args)
    {
        var o = new JsonObject { ["cmd"] = cmd };
        foreach (var (k, v) in args) o[k] = v;
        return o.ToJsonString();
    }
}

/// <summary>MynaCAD's realtime sync state as its <c>status</c>, <c>sync.set</c> and <c>sync.now</c> replies report it (<c>sync: {on, waiting, text}</c>).</summary>
public sealed record CadSyncState(bool On, long Waiting, string Text)
{
    /// <summary>The <c>sync</c> object of a reply line; null when the line has none or is not JSON.</summary>
    public static CadSyncState? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            if (JsonNode.Parse(raw) is not JsonObject o || o["sync"] is not JsonObject s) return null;
            var on = s["on"] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : true;
            var waiting = s["waiting"] is JsonValue w && w.TryGetValue<long>(out var n) ? Math.Max(0, n) : 0;
            return new CadSyncState(on, waiting, s["text"] is JsonValue t && t.TryGetValue<string>(out var text) ? text : "");
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException) { return null; }
    }
}

/// <summary>Sends one request to a MynaCAD instance (the real pipe, or a fake in tests).</summary>
public interface ICadPipe
{
    /// <summary>Connect, send <paramref name="jsonLine"/> and read the reply line if one comes within <paramref name="replyWait"/>. Returns null when nothing is listening.</summary>
    Task<CadReply?> SendAsync(string project, string jsonLine, TimeSpan replyWait, CancellationToken ct = default);
}

/// <summary>What came back on the pipe: <c>Delivered</c> is always true (the request was written); <c>Raw</c> is the reply line or empty.</summary>
public sealed record CadReply(bool Delivered, string Raw)
{
    /// <summary>The reply is optional in the contract; an unreadable or missing one counts as ok when the request was delivered.</summary>
    public bool Ok
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Raw)) return Delivered;
            try { return JsonNode.Parse(Raw) is JsonObject o ? o["ok"]?.GetValue<bool>() ?? true : Delivered; }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException) { return Delivered; }
        }
    }
}

/// <summary>The activation pipe of a running MynaCAD: <c>\\.\pipe\MynaCAD.&lt;path hash&gt;</c> (the hash of <see cref="ProjectLease.PathHash"/>).</summary>
public sealed class NamedCadPipe : ICadPipe
{
    public static string PipeName(string project) => "MynaCAD." + ProjectLease.PathHash(project);

    public async Task<CadReply?> SendAsync(string project, string jsonLine, TimeSpan replyWait, CancellationToken ct = default)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", PipeName(project), PipeDirection.InOut, PipeOptions.Asynchronous);
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connect.CancelAfter(TimeSpan.FromSeconds(2));
            await client.ConnectAsync(connect.Token).ConfigureAwait(false);
            var bytes = Encoding.UTF8.GetBytes(jsonLine + "\n");
            await client.WriteAsync(bytes, ct).ConfigureAwait(false);
            await client.FlushAsync(ct).ConfigureAwait(false);
            var reply = "";
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(replyWait);
                var buf = new byte[4096];
                var sb = new StringBuilder();
                while (sb.Length < 16 * 1024)
                {
                    var n = await client.ReadAsync(buf, wait.Token).ConfigureAwait(false);
                    if (n == 0) break;
                    sb.Append(Encoding.UTF8.GetString(buf, 0, n));
                    if (sb.ToString().Contains('\n')) break;
                }
                reply = sb.ToString().Split('\n')[0].Trim();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* no reply in time: the request was delivered */ }
            catch (IOException) { /* the other end closed after reading */ }
            return new CadReply(true, reply);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (TimeoutException) { return null; }
    }
}

/// <summary>The outcome of <see cref="MynaCadLauncher.OpenAsync"/>.</summary>
public enum CadLaunch
{
    /// <summary>MynaCAD was not running for this project and was started.</summary>
    Started,
    /// <summary>MynaCAD was already running for this project and was asked to come to the front.</summary>
    Activated,
    /// <summary>MynaCAD was not found.</summary>
    NotInstalled,
    /// <summary>MynaCAD could not be started; see the message.</summary>
    Failed,
}

public sealed record CadLaunchResult(CadLaunch Outcome, string Message, int Pid = 0);

/// <summary>
/// MynaOps starts MynaCAD with a project (docs/ops-first-workflow.md section 2.1) and brings a running one to the front. The command line is
/// <c>ODraft.WinUi.exe --managed --project "&lt;.osp&gt;" --ops-pid &lt;pid&gt; [--import "&lt;file&gt;"] [--control &lt;port&gt;]</c>. Nothing secret is passed.
/// </summary>
public sealed class MynaCadLauncher
{
    public const string ExeName = "ODraft.WinUi.exe";
    public const string EnvExe = "OSTUDIO_CAD_EXE";

    /// <summary>The arguments, one per element (no quoting: <see cref="ProcessStartInfo.ArgumentList"/> quotes them).</summary>
    public static IReadOnlyList<string> BuildArguments(string project, int opsPid, string? import = null, int? controlPort = null)
    {
        if (string.IsNullOrWhiteSpace(project) || !Path.IsPathRooted(project) || project.IndexOf('\0') >= 0)
            throw new ArgumentException("The project is an absolute path.", nameof(project));
        if (!project.EndsWith(OdpFile.Extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("MynaCAD opens a project file (.osp), never a drawing.", nameof(project));
        if (opsPid <= 0) throw new ArgumentOutOfRangeException(nameof(opsPid));
        var args = new List<string> { "--managed", "--project", Path.GetFullPath(project), "--ops-pid", opsPid.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (import is not null)
        {
            if (!Path.IsPathRooted(import) || import.IndexOf('\0') >= 0 ||
                !(import.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase) || import.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("--import is the absolute path of a .dwg or .dxf file.", nameof(import));
            args.Add("--import");
            args.Add(import);
        }
        if (controlPort is { } p)
        {
            if (p is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(controlPort));
            args.Add("--control");
            args.Add(p.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return args;
    }

    /// <summary>The places MynaCAD is looked for, in order: <c>OSTUDIO_CAD_EXE</c>, beside MynaOps, the sibling folder <c>O-Draft</c>, the install folder.</summary>
    public static IReadOnlyList<string> Candidates(string? envValue, string baseDirectory, string localAppData)
    {
        var list = new List<string>();
        void Add(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            var full = Path.GetFullPath(p.Trim().Trim('"'));
            if (!list.Contains(full, StringComparer.OrdinalIgnoreCase)) list.Add(full);
        }
        Add(envValue);
        Add(Path.Combine(baseDirectory, ExeName));
        var parent = Directory.GetParent(baseDirectory.TrimEnd('\\', '/'))?.FullName;
        if (parent is not null) Add(Path.Combine(parent, "O-Draft", ExeName));
        Add(Path.Combine(localAppData, "Programs", "O-Draft", ExeName));
        return list;
    }

    public static string? Find() =>
        Candidates(Environment.GetEnvironmentVariable(EnvExe), AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
            .FirstOrDefault(File.Exists);

    readonly ICadPipe _pipe;
    readonly Func<string?> _find;

    public MynaCadLauncher(ICadPipe? pipe = null, Func<string?>? find = null)
    {
        _pipe = pipe ?? new NamedCadPipe();
        _find = find ?? Find;
    }

    /// <summary>True when a MynaCAD holds the lease of the project (it runs, and has the project open).</summary>
    public static bool IsRunningFor(string project) => ProjectLease.IsHeld(project, ProjectLease.AppCad);

    /// <summary>
    /// <b>Open MynaCAD</b>: when a MynaCAD holds the project's lease, ask it to come to the front over the pipe (and, when it is not reachable, start the
    /// program with the same arguments, which the contract makes a client of the running one); otherwise start it. The project must be a configured one
    /// MynaOps has open; that is the caller's rule.
    /// </summary>
    public async Task<CadLaunchResult> OpenAsync(string project, string? import = null, CancellationToken ct = default)
    {
        project = Path.GetFullPath(project);
        if (IsRunningFor(project))
        {
            var line = import is null ? CadRequest.Activate() : CadRequest.Import(import);
            var reply = await _pipe.SendAsync(project, line, TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            if (reply is { Ok: true })
                return new CadLaunchResult(CadLaunch.Activated, "MynaCAD is already open for this project; it was brought to the front.");
            // The lease says it runs but nothing answered: start the program, which finds the running instance by its mutex and activates it.
        }
        var exe = _find();
        if (exe is null || !File.Exists(exe))
            return new CadLaunchResult(CadLaunch.NotInstalled, "MynaCAD was not found. Install MynaOps and MynaCAD from one installer, or set OSTUDIO_CAD_EXE to ODraft.WinUi.exe.");
        IReadOnlyList<string> args;
        try { args = BuildArguments(project, Environment.ProcessId, import); }
        catch (ArgumentException ex) { return new CadLaunchResult(CadLaunch.Failed, ex.Message); }
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            return new CadLaunchResult(CadLaunch.Started, "MynaCAD is starting with this project.", p?.Id ?? 0);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new CadLaunchResult(CadLaunch.Failed, "MynaCAD could not be started: " + ex.Message);
        }
    }

    /// <summary>MynaCAD's realtime sync state, over the pipe (<c>status</c>); null when no MynaCAD holds the project or nothing answers.</summary>
    public async Task<CadSyncState?> SyncStatusAsync(string project, CancellationToken ct = default)
    {
        project = Path.GetFullPath(project);
        if (!IsRunningFor(project)) return null;
        var reply = await _pipe.SendAsync(project, CadRequest.Status(), TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        return reply is { Ok: true } ? CadSyncState.Parse(reply.Raw) : null;
    }

    /// <summary>Switch MynaCAD's realtime sync (<c>sync.set</c>); returns its new state, or null when it cannot be reached.</summary>
    public async Task<CadSyncState?> SetSyncAsync(string project, bool on, CancellationToken ct = default)
    {
        project = Path.GetFullPath(project);
        if (!IsRunningFor(project)) return null;
        var reply = await _pipe.SendAsync(project, CadRequest.SyncSet(on), TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        return reply is { Ok: true } ? CadSyncState.Parse(reply.Raw) : null;
    }

    /// <summary>MynaCAD's Sync now (<c>sync.now</c>): it saves unsaved work first, so the reply can take a while (60 s at most). Null when it cannot be reached or refused.</summary>
    public async Task<CadSyncState?> SyncNowAsync(string project, CancellationToken ct = default)
    {
        project = Path.GetFullPath(project);
        if (!IsRunningFor(project)) return null;
        var reply = await _pipe.SendAsync(project, CadRequest.SyncNow(), TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        return reply is { Ok: true } ? CadSyncState.Parse(reply.Raw) : null;
    }

    /// <summary>Ask a running MynaCAD to close (it asks the user about unsaved work when <paramref name="save"/> is <c>ask</c>). False when nothing answers.</summary>
    public async Task<bool> RequestCloseAsync(string project, string save = "ask", CancellationToken ct = default)
    {
        var reply = await _pipe.SendAsync(Path.GetFullPath(project), CadRequest.Close(save), TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        return reply is { Ok: true };
    }
}
