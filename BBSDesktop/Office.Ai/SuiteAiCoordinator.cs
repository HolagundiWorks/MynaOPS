// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using Office.Shared;

namespace Office.Ai;

/// <summary>
/// One Olmo host per Windows session on loopback :8742, and it is always MynaOps: a MynaOps window hosts it in-process when the port is free,
/// otherwise MynaOps runs headless (<c>O-CoreOp.exe --ai-host</c>, see <see cref="HeadlessAiHost"/>), which MynaCAD starts when no host answers.
/// Other processes use <see cref="RemoteLlm"/>. The Myna launcher that used to host it was removed on 2026-11-11; MynaHub has no AI.
/// </summary>
public static class SuiteAiCoordinator
{
    static AiCoreListener? _host;

    public static bool IsHost => _host is not null;

    public static bool PreferClient =>
        string.Equals(Environment.GetEnvironmentVariable(SuiteWire.EnvAiRole), "client", StringComparison.OrdinalIgnoreCase);

    public static bool PreferHost =>
        string.Equals(Environment.GetEnvironmentVariable(SuiteWire.EnvAiRole), "host", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Start listening when this process should own the model (a MynaOps window, or the headless host). Writes <see cref="AiHostRecord"/> so
    /// MynaCAD and the status bar can see who serves; <paramref name="mode"/> is <c>window</c> or <c>headless</c>.
    /// </summary>
    public static AiCoreListener? StartHostIfNeeded(int port = AiCoreListener.DefaultPort, string mode = "window")
    {
        if (PreferClient)
            return null;
        if (_host is not null)
            return _host;
        _host = AiCoreListener.TryStart(port);
        if (_host is not null)
            AiHostRecord.ForThisProcess(port, mode).Write();
        return _host;
    }

    public static void StopHost()
    {
        if (_host is not null)
            AiHostRecord.RemoveOwn();
        _host?.Dispose();
        _host = null;
        LocalLlmHost.Shutdown();
    }

    /// <summary>Local in-process llama when this app hosts; remote when another app already serves :8742.</summary>
    public static async Task<(LocalLlm? local, RemoteLlm? remote, string? problem)> ResolveLlmAsync(CancellationToken ct = default)
    {
        if (PreferClient || (!PreferHost && await RemoteLlm.IsAvailableAsync(ct).ConfigureAwait(false)))
        {
            var remote = await RemoteLlm.ConnectAsync(ct).ConfigureAwait(false);
            if (remote is not null)
                return (null, remote, null);
            if (PreferClient)
                return (null, null, "The AI agent is not running. Close and reopen MynaOps: it hosts the assistant model (Olmo 3 7B).");
        }

        if (!LocalLlmHost.Installed)
        {
            return (null, null, LocalModel.Find() is null
                ? $"The assistant model is not installed. Put {LocalModel.FileName} in {LocalModel.SharedDirectory}."
                : $"The model runtime is not installed. Unpack llama.cpp {LocalModel.RuntimeRelease} (Vulkan, Windows x64) into {LocalModel.RuntimeDirectory}.");
        }

        StartHostIfNeeded();
        try
        {
            var local = await LocalLlmHost.GetAsync(ct).ConfigureAwait(false);
            return (local, null, null);
        }
        catch (Exception ex)
        {
            return (null, null, ex.Message);
        }
    }
}
