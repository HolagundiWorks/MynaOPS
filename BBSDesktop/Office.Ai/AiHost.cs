// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

using Office.Shared;

namespace Office.Ai;

/// <summary>
/// The running AI host, as <c>%LOCALAPPDATA%\O-Studio\ai\host.json</c> (beside <c>auth.token</c>): which process serves the AI agent on loopback,
/// on which port, and how it was started. MynaOps writes it when it starts serving (as its window, or headless with <c>--ai-host</c>) and removes it
/// when it stops. MynaCAD reads <c>exe</c> to find MynaOps when nothing answers. The wire contract itself (port 8742, the token file, the
/// <c>OSTUDIO_AI_*</c> variables) is unchanged; this file only adds information.
/// <code>{ "format": "myna-ai-host", "version": 1, "port": 8742, "pid": 1234, "mode": "headless", "exe": "C:\\...\\O-CoreOp.exe", "started_utc": "2026-11-11T09:00:00Z" }</code>
/// </summary>
public sealed record AiHostRecord(int Port, int Pid, string Mode, string Exe, DateTime StartedUtc)
{
    public const string Format = "myna-ai-host";

    public static string FilePath => Path.Combine(AiCoreListener.StateDirectory, "host.json");

    public string ToJson() => new JsonObject
    {
        ["format"] = Format,
        ["version"] = 1,
        ["port"] = Port,
        ["pid"] = Pid,
        ["mode"] = Mode,
        ["exe"] = Exe,
        ["started_utc"] = StartedUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
    }.ToJsonString();

    /// <summary>Null when the text is not a host record.</summary>
    public static AiHostRecord? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o || o["format"]?.GetValue<string>() != Format) return null;
            int port = o["port"]?.GetValue<int>() ?? 0;
            int pid = o["pid"]?.GetValue<int>() ?? 0;
            if (port <= 0 || pid <= 0) return null;
            DateTime.TryParse(o["started_utc"]?.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var started);
            return new AiHostRecord(port, pid, o["mode"]?.GetValue<string>() ?? "", o["exe"]?.GetValue<string>() ?? "", started);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { return null; }
    }

    public static AiHostRecord? Read(string? path = null)
    {
        try
        {
            path ??= FilePath;
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public void Write(string? path = null)
    {
        try
        {
            path ??= FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, ToJson());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Remove the record if it is this process's (another host may have written its own since).</summary>
    public static void RemoveOwn(string? path = null)
    {
        try
        {
            path ??= FilePath;
            if (Read(path) is { } r && r.Pid == Environment.ProcessId)
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>A record for this process.</summary>
    public static AiHostRecord ForThisProcess(int port, string mode) =>
        new(port, Environment.ProcessId, mode, Environment.ProcessPath ?? "", DateTime.UtcNow);
}

/// <summary>
/// When the headless host (<c>O-CoreOp.exe --ai-host</c>) ends by itself: when no client app is running (MynaCAD, or a MynaOps window) and no
/// request has come for <see cref="Idle"/>. A MynaCAD that is still open simply starts it again when it next needs the agent.
/// </summary>
public static class AiHostIdle
{
    /// <summary>Process names of the apps that use the host: MynaCAD's shell and MynaOps (whose window may be a client of this host).</summary>
    public static readonly string[] ClientProcessNames = ["ODraft.WinUi", "O-CoreOp"];

    /// <summary>How long the host waits with no client and no request; <c>OSTUDIO_AI_IDLE_SECONDS</c> changes it (at least 10 s).</summary>
    public static TimeSpan Idle =>
        int.TryParse(Environment.GetEnvironmentVariable("OSTUDIO_AI_IDLE_SECONDS"), out var s) && s >= 10 ? TimeSpan.FromSeconds(s) : TimeSpan.FromMinutes(3);

    /// <summary>True when the host should end now.</summary>
    public static bool ShouldExit(DateTime nowUtc, DateTime startedUtc, DateTime lastRequestUtc, bool clientRunning, TimeSpan idle)
    {
        if (clientRunning) return false;
        var last = lastRequestUtc > startedUtc ? lastRequestUtc : startedUtc;
        return nowUtc - last >= idle;
    }

    /// <summary>True when a client app runs (any process with a client name other than this one).</summary>
    public static bool ClientRunning()
    {
        foreach (var name in ClientProcessNames)
        {
            Process[] list;
            try { list = Process.GetProcessesByName(name); }
            catch (InvalidOperationException) { continue; }
            foreach (var p in list)
            {
                using (p)
                {
                    if (p.Id != Environment.ProcessId) return true;
                }
            }
        }
        return false;
    }
}

/// <summary>
/// MynaOps without a window: <c>O-CoreOp.exe --ai-host</c>. It serves the AI agent on 127.0.0.1:8742 for MynaCAD (and for a MynaOps window
/// that is a client), loads the model on the first question, and ends by itself when idle (<see cref="AiHostIdle"/>) or when asked to
/// (<c>O-CoreOp.exe --ai-host-stop</c>, the installer uses it). One per Windows session: a second copy, or one started while another program
/// already serves the port, ends at once.
/// </summary>
public static class HeadlessAiHost
{
    public const string Argument = "--ai-host";
    public const string StopArgument = "--ai-host-stop";
    const string MutexName = "MynaOps.AiHost";
    const string StopEventName = "MynaOps.AiHost.Stop";

    public static bool IsHostArgument(IEnumerable<string> args) => args.Any(a => string.Equals(a, Argument, StringComparison.OrdinalIgnoreCase));

    public static bool IsStopArgument(IEnumerable<string> args) => args.Any(a => string.Equals(a, StopArgument, StringComparison.OrdinalIgnoreCase));

    static string LogPath => Path.Combine(AiCoreListener.StateDirectory, "host.log");

    static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 256 * 1024) File.Delete(LogPath);
            File.AppendAllText(LogPath, $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)} [{Environment.ProcessId}] {line}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Ask a running headless host to end (returns at once; it ends within a second).</summary>
    public static bool RequestStop()
    {
        try
        {
            using var stop = EventWaitHandle.OpenExisting(StopEventName);
            return stop.Set();
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException) { return false; }
    }

    /// <summary>Run the host until it is idle or asked to stop. Returns the process exit code.</summary>
    public static int Run()
    {
        using var single = new Mutex(true, MutexName, out var first);
        if (!first)
        {
            Log("another headless host is running; leaving");
            return 0;
        }
        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName);
        Environment.SetEnvironmentVariable(SuiteWire.EnvAiRole, "host");
        // The port clients use (8742 unless OSTUDIO_AI_PORT says otherwise).
        int port = RemoteLlm.Port;
        var listener = SuiteAiCoordinator.StartHostIfNeeded(port, mode: "headless");
        if (listener is null)
        {
            // The port is taken: a MynaOps window (or another program) already serves the agent.
            Log($"port {port} is already served; leaving");
            return 0;
        }
        Log($"serving on 127.0.0.1:{port} (model installed: {LocalLlmHost.Installed})");
        var started = DateTime.UtcNow;
        var idle = AiHostIdle.Idle;
        var tick = idle < TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(15);
        try
        {
            while (!stop.WaitOne(tick))
            {
                if (AiHostIdle.ShouldExit(DateTime.UtcNow, started, AiCoreListener.LastRequestUtc, AiHostIdle.ClientRunning(), idle))
                {
                    Log($"idle for {idle.TotalSeconds:0} s with no client; ending");
                    break;
                }
            }
        }
        finally
        {
            SuiteAiCoordinator.StopHost();
            Log("stopped");
        }
        return 0;
    }
}

/// <summary>What MynaOps' status bar says about the AI agent, in a few words, from what can be seen without waiting on the model.</summary>
public static class AiStatus
{
    public sealed record Snapshot(bool HostedHere, bool ModelLoaded, bool RemoteAnswers, bool ModelInstalled, bool RuntimeInstalled, string? RemoteMode);

    /// <summary>Short text for the status bar and a longer one for its tooltip.</summary>
    public static (string Short, string Detail) Describe(Snapshot s)
    {
        if (s.HostedHere)
        {
            if (!s.ModelInstalled)
                return ("AI: model not installed", $"The AI agent runs here, but the model is missing. Edit > Assistant model downloads it ({LocalModel.FileName}).");
            if (!s.RuntimeInstalled)
                return ("AI: runtime missing", $"The AI agent runs here, but llama-server is missing. Reinstall MynaOps and MynaCAD, or unpack llama.cpp {LocalModel.RuntimeRelease} into {LocalModel.RuntimeDirectory}.");
            return s.ModelLoaded
                ? ("AI: ready", "The AI agent runs in this MynaOps window (Olmo 3 7B on 127.0.0.1:8742) and serves MynaCAD too.")
                : ("AI: ready", "The AI agent runs in this MynaOps window and serves MynaCAD too. The model loads on the first question.");
        }
        if (s.RemoteAnswers)
            return ("AI: ready", s.RemoteMode == "headless"
                ? "The AI agent runs in MynaOps in the background (it was started for MynaCAD) on 127.0.0.1:8742."
                : "The AI agent is served on 127.0.0.1:8742 by another MynaOps.");
        if (!s.ModelInstalled)
            return ("AI: model not installed", $"The assistant model is not installed. Edit > Assistant model downloads it to {LocalModel.SharedDirectory}.");
        return ("AI: off", "The AI agent is not running; it starts with the first question.");
    }

    /// <summary>Look now (one short health request when this process does not host).</summary>
    public static async Task<(string Short, string Detail)> CheckAsync(CancellationToken ct = default)
    {
        bool here = SuiteAiCoordinator.IsHost;
        bool remote = !here && await RemoteLlm.IsAvailableAsync(ct).ConfigureAwait(false);
        var record = AiHostRecord.Read();
        return Describe(new Snapshot(here, LocalLlmHost.Loaded, remote, LocalModel.Find() is not null, LocalModel.FindServer() is not null, record?.Mode));
    }
}
