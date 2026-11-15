// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Office.Ai;
using Xunit;

namespace Office.Shared.Tests;

/// <summary>AI Stop and Start in MynaOps (Project home): the agent's port closes and opens, the health reply follows, and a stopped agent is not started by a question.</summary>
[Collection("ops-first")]
public sealed class AiStopStartTests : OpsFirstTestBase
{
    readonly string? _oldAiDir = Environment.GetEnvironmentVariable("OSTUDIO_AI_DIR");

    public AiStopStartTests() => Environment.SetEnvironmentVariable("OSTUDIO_AI_DIR", Path.Combine(Dir, "ai"));

    public override void Dispose()
    {
        SuiteAiCoordinator.StartByUser(FreePort());      // leave the static state as it was found: not stopped
        SuiteAiCoordinator.StopHost();
        Environment.SetEnvironmentVariable("OSTUDIO_AI_DIR", _oldAiDir);
        base.Dispose();
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    static async Task<HttpStatusCode?> Health(int port)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", File.Exists(AiCoreListener.TokenPath) ? File.ReadAllText(AiCoreListener.TokenPath).Trim() : "none");
        try { return (await http.GetAsync($"http://127.0.0.1:{port}/v1/health")).StatusCode; }
        catch (HttpRequestException) { return null; }
    }

    [Fact]
    public async Task Stop_closes_the_agent_and_Start_opens_it_again_and_the_health_reply_follows()
    {
        var port = FreePort();
        Assert.NotNull(SuiteAiCoordinator.StartByUser(port));
        Assert.True(SuiteAiCoordinator.IsHost);
        Assert.Equal(HttpStatusCode.OK, await Health(port));
        Assert.Equal(port, AiHostRecord.Read()?.Port);                      // MynaCAD finds the host through its record

        Assert.True(SuiteAiCoordinator.StopByUser());
        Assert.True(SuiteAiCoordinator.StoppedByUser);
        Assert.False(SuiteAiCoordinator.IsHost);
        Assert.Null(await Health(port));                                    // nothing answers any more
        Assert.Null(AiHostRecord.Read());                                   // and the record is gone

        Assert.NotNull(SuiteAiCoordinator.StartByUser(port));
        Assert.False(SuiteAiCoordinator.StoppedByUser);
        Assert.Equal(HttpStatusCode.OK, await Health(port));
    }

    [Fact]
    public async Task A_stopped_agent_is_not_started_by_a_question_or_by_the_automatic_start()
    {
        var port = FreePort();
        SuiteAiCoordinator.StopByUser();
        Assert.Null(SuiteAiCoordinator.StartHostIfNeeded(port));
        Assert.Null(await Health(port));
        var (local, remote, problem) = await SuiteAiCoordinator.ResolveLlmAsync();
        Assert.Null(local);
        Assert.Null(remote);
        Assert.Contains("stopped", problem);
        Assert.Contains("Start", problem);
    }

    [Fact]
    public void The_status_line_says_off_when_the_user_stopped_it()
    {
        var stopped = AiStatus.Describe(new AiStatus.Snapshot(false, false, false, true, true, null, StoppedByUser: true));
        Assert.Equal("AI: off", stopped.Short);
        Assert.Contains("stopped", stopped.Detail);
        // an unchanged snapshot keeps its old text
        Assert.Equal("AI: ready", AiStatus.Describe(new AiStatus.Snapshot(true, false, false, true, true, null)).Short);
    }
}
