// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Net;
using System.Security.Cryptography;
using Office.Ai;
using Xunit;

namespace Office.Shared.Tests;

/// <summary>The AI host that MynaOps runs for itself and MynaCAD (the launcher that used to host it is gone).</summary>
public sealed class AiHostTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ai-host-" + Guid.NewGuid().ToString("N"));

    public AiHostTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void The_host_record_round_trips_and_only_its_owner_removes_it()
    {
        var file = Path.Combine(_dir, "host.json");
        var mine = AiHostRecord.ForThisProcess(8742, "headless");
        mine.Write(file);
        var read = AiHostRecord.Read(file);
        Assert.NotNull(read);
        Assert.Equal(8742, read!.Port);
        Assert.Equal(Environment.ProcessId, read.Pid);
        Assert.Equal("headless", read.Mode);
        Assert.Equal(Environment.ProcessPath ?? "", read.Exe);

        // a record another process wrote is left alone
        (mine with { Pid = Environment.ProcessId + 1 }).Write(file);
        AiHostRecord.RemoveOwn(file);
        Assert.True(File.Exists(file));
        mine.Write(file);
        AiHostRecord.RemoveOwn(file);
        Assert.False(File.Exists(file));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"format\":\"something-else\",\"port\":8742,\"pid\":5}")]
    [InlineData("{\"format\":\"myna-ai-host\",\"port\":0,\"pid\":5}")]
    [InlineData("not json")]
    public void Anything_else_is_not_a_host_record(string json) => Assert.Null(AiHostRecord.Parse(json));

    [Fact]
    public void The_headless_host_stays_while_a_client_runs_or_a_request_came_recently()
    {
        var t0 = new DateTime(2026, 11, 11, 9, 0, 0, DateTimeKind.Utc);
        var idle = TimeSpan.FromMinutes(3);
        // just started, nothing asked yet
        Assert.False(AiHostIdle.ShouldExit(t0.AddMinutes(1), t0, DateTime.MinValue, clientRunning: false, idle));
        // MynaCAD open: never idle
        Assert.False(AiHostIdle.ShouldExit(t0.AddHours(5), t0, DateTime.MinValue, clientRunning: true, idle));
        // no client, last request two minutes ago
        Assert.False(AiHostIdle.ShouldExit(t0.AddMinutes(10), t0, t0.AddMinutes(8), clientRunning: false, idle));
        // no client and quiet for the whole idle time
        Assert.True(AiHostIdle.ShouldExit(t0.AddMinutes(11), t0, t0.AddMinutes(8), clientRunning: false, idle));
        Assert.True(AiHostIdle.ShouldExit(t0.AddMinutes(3), t0, DateTime.MinValue, clientRunning: false, idle));
    }

    [Fact]
    public void The_host_arguments_are_recognised_in_any_case()
    {
        Assert.True(HeadlessAiHost.IsHostArgument(new[] { "--AI-HOST" }));
        Assert.False(HeadlessAiHost.IsHostArgument(new[] { "C:\\p\\House.osp" }));
        Assert.True(HeadlessAiHost.IsStopArgument(new[] { "--ai-host-stop" }));
        // with no headless host running, asking one to stop does nothing
        Assert.False(HeadlessAiHost.RequestStop());
    }

    [Fact]
    public void The_status_bar_names_the_missing_part_or_who_serves()
    {
        static string S(bool here, bool loaded, bool remote, bool model, bool runtime, string? mode = null) =>
            AiStatus.Describe(new AiStatus.Snapshot(here, loaded, remote, model, runtime, mode)).Short;
        Assert.Equal("AI: ready", S(here: true, loaded: false, remote: false, model: true, runtime: true));
        Assert.Equal("AI: model not installed", S(here: true, loaded: false, remote: false, model: false, runtime: true));
        Assert.Equal("AI: runtime missing", S(here: true, loaded: false, remote: false, model: true, runtime: false));
        Assert.Equal("AI: ready", S(here: false, loaded: false, remote: true, model: false, runtime: false, "headless"));
        Assert.Equal("AI: model not installed", S(here: false, loaded: false, remote: false, model: false, runtime: true));
        Assert.Equal("AI: off", S(here: false, loaded: false, remote: false, model: true, runtime: true));
        Assert.Contains("background", AiStatus.Describe(new AiStatus.Snapshot(false, false, true, true, true, "headless")).Detail);
    }

    [Fact]
    public void A_located_model_is_remembered_in_the_pointer_file()
    {
        var pointer = Path.Combine(_dir, "model-path.txt");
        Assert.Null(LocalModel.ReadPointer(pointer));
        var model = Path.Combine(_dir, "custom.gguf");
        File.WriteAllBytes(model, "GGUF0000"u8.ToArray());
        LocalModel.SetLocated(model, pointer);
        Assert.Equal(model, LocalModel.ReadPointer(pointer));
        LocalModel.SetLocated(null, pointer);
        Assert.Null(LocalModel.ReadPointer(pointer));
        // a file that is not a model is refused
        var text = Path.Combine(_dir, "notes.gguf");
        File.WriteAllText(text, "hello");
        Assert.Throws<InvalidOperationException>(() => LocalModel.SetLocated(text, pointer));
        Assert.False(File.Exists(pointer));
    }

    sealed class FakeServer(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }

    [Fact]
    public async Task The_model_download_is_checked_before_it_is_kept()
    {
        var body = RandomNumberGenerator.GetBytes(3 << 20);
        var sha = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        var target = Path.Combine(_dir, "models", "m.gguf");
        var seen = new List<(long, long)>();
        var path = await ModelDownload.RunAsync(new Progress<(long Done, long Total)>(p => seen.Add(p)), default, new FakeServer(body),
            target, "http://example.invalid/m.gguf", body.Length, sha);
        Assert.Equal(target, path);
        Assert.Equal(body, File.ReadAllBytes(target));
        Assert.False(File.Exists(target + ".part"));

        // a wrong hash leaves nothing behind
        var other = Path.Combine(_dir, "models", "bad.gguf");
        await Assert.ThrowsAsync<InvalidOperationException>(() => ModelDownload.RunAsync(null, default, new FakeServer(body),
            other, "http://example.invalid/m.gguf", body.Length, new string('0', 64)));
        Assert.False(File.Exists(other));
        Assert.False(File.Exists(other + ".part"));
    }
}
