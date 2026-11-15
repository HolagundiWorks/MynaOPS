// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Text.Json.Nodes;
using Office.Shared;
using Xunit;

namespace Office.Shared.Tests;

[Collection("ops-first")]
public sealed class MynaCadLauncherTests : OpsFirstTestBase
{
    sealed class FakePipe : ICadPipe
    {
        public readonly List<(string Project, string Line)> Sent = new();
        public CadReply? Reply = new(true, "");
        public Task<CadReply?> SendAsync(string project, string jsonLine, TimeSpan replyWait, CancellationToken ct = default)
        {
            Sent.Add((project, jsonLine));
            return Task.FromResult(Reply);
        }
    }

    [Fact]
    public void Arguments_follow_the_launch_contract()
    {
        var args = MynaCadLauncher.BuildArguments(@"C:\Work\My House\House.osp", 4321);
        Assert.Equal(new[] { "--managed", "--project", @"C:\Work\My House\House.osp", "--ops-pid", "4321" }, args);

        var full = MynaCadLauncher.BuildArguments(@"C:\Work\House.osp", 7, import: @"C:\In\plan.dwg", controlPort: 5050);
        Assert.Equal(new[] { "--managed", "--project", @"C:\Work\House.osp", "--ops-pid", "7", "--import", @"C:\In\plan.dwg", "--control", "5050" }, full);
        Assert.Equal("--managed", args[0]);       // the contract's order: --managed, --project, --ops-pid, then the optional ones
    }

    [Theory]
    [InlineData(@"House.osp")]                // not absolute
    [InlineData(@"C:\Work\plan.dwg")]         // a drawing is never a project
    [InlineData(@"C:\Work\plan.dxf")]
    [InlineData("")]
    public void Arguments_refuse_what_is_not_an_absolute_project_path(string project) =>
        Assert.Throws<ArgumentException>(() => MynaCadLauncher.BuildArguments(project, 1));

    [Fact]
    public void Arguments_refuse_a_bad_pid_import_or_port()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MynaCadLauncher.BuildArguments(@"C:\a.osp", 0));
        Assert.Throws<ArgumentException>(() => MynaCadLauncher.BuildArguments(@"C:\a.osp", 1, import: @"C:\a.txt"));
        Assert.Throws<ArgumentException>(() => MynaCadLauncher.BuildArguments(@"C:\a.osp", 1, import: "plan.dwg"));
        Assert.Throws<ArgumentOutOfRangeException>(() => MynaCadLauncher.BuildArguments(@"C:\a.osp", 1, controlPort: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => MynaCadLauncher.BuildArguments(@"C:\a.osp", 1, controlPort: 70000));
    }

    [Fact]
    public void No_secret_goes_on_the_command_line()
    {
        var all = string.Join(" ", MynaCadLauncher.BuildArguments(@"C:\a.osp", 1, @"C:\b.dwg", 5000));
        foreach (var bad in new[] { "token", "password", "key", "ticket" }) Assert.DoesNotContain(bad, all, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_executable_is_looked_for_in_the_documented_places_in_order()
    {
        var c = MynaCadLauncher.Candidates(@" ""D:\dev\ODraft.WinUi.exe"" ", @"C:\Users\a\AppData\Local\Programs\O-CoreOp\", @"C:\Users\a\AppData\Local");
        Assert.Equal(new[]
        {
            @"D:\dev\ODraft.WinUi.exe",
            @"C:\Users\a\AppData\Local\Programs\O-CoreOp\ODraft.WinUi.exe",
            @"C:\Users\a\AppData\Local\Programs\O-Draft\ODraft.WinUi.exe",   // the sibling folder and the install folder are the same place: listed once
        }, c);
        Assert.DoesNotContain(MynaCadLauncher.Candidates(null, @"C:\x\", @"C:\l"), s => s.Length == 0);
    }

    [Fact]
    public void Requests_are_json_lines_with_cmd_and_flat_arguments()
    {
        Assert.Equal("activate", (string?)JsonNode.Parse(CadRequest.Activate())!["cmd"]);
        var imp = JsonNode.Parse(CadRequest.Import(@"C:\a b\x.dwg"))!;
        Assert.Equal("import", (string?)imp["cmd"]);
        Assert.Equal(@"C:\a b\x.dwg", (string?)imp["path"]);
        Assert.Equal("ask", (string?)JsonNode.Parse(CadRequest.Close())!["save"]);
        Assert.Equal("discard", (string?)JsonNode.Parse(CadRequest.Close("discard"))!["save"]);
        Assert.Throws<ArgumentException>(() => CadRequest.Close("whatever"));
        Assert.True((bool?)JsonNode.Parse(CadRequest.SyncSet(true))!["on"]);
        Assert.Equal("sync.now", (string?)JsonNode.Parse(CadRequest.SyncNow())!["cmd"]);
        Assert.DoesNotContain('\n', CadRequest.Import("x\ny.dwg"));      // one line, whatever the path holds
    }

    [Fact]
    public void A_reply_is_optional_and_not_ok_only_when_it_says_so()
    {
        Assert.True(new CadReply(true, "").Ok);
        Assert.True(new CadReply(true, "{\"ok\":true}").Ok);
        Assert.False(new CadReply(true, "{\"ok\":false,\"error\":\"busy\"}").Ok);
        Assert.True(new CadReply(true, "garbage").Ok);
    }

    [Fact]
    public async Task Second_press_activates_the_running_mynacad_and_starts_nothing()
    {
        var p = Project();
        var pipe = new FakePipe();
        var started = 0;
        var launcher = new MynaCadLauncher(pipe, () => { started++; return null; });
        using var cad = FakeCad(p);
        var r = await launcher.OpenAsync(p);
        Assert.Equal(CadLaunch.Activated, r.Outcome);
        Assert.Equal(0, started);
        var sent = Assert.Single(pipe.Sent);
        Assert.Equal(Path.GetFullPath(p), sent.Project);
        Assert.Equal("activate", (string?)JsonNode.Parse(sent.Line)!["cmd"]);
    }

    [Fact]
    public async Task Without_a_running_mynacad_it_looks_for_the_program()
    {
        var p = Project();
        var pipe = new FakePipe();
        var launcher = new MynaCadLauncher(pipe, () => null);
        var r = await launcher.OpenAsync(p);
        Assert.Equal(CadLaunch.NotInstalled, r.Outcome);
        Assert.Empty(pipe.Sent);
    }

    [Fact]
    public async Task A_running_mynacad_that_does_not_answer_gets_the_program_started_with_the_same_arguments()
    {
        var p = Project();
        var pipe = new FakePipe { Reply = null };
        var asked = 0;
        var launcher = new MynaCadLauncher(pipe, () => { asked++; return null; });
        using var cad = FakeCad(p);
        var r = await launcher.OpenAsync(p);
        Assert.Equal(1, asked);                       // it fell through to starting the program
        Assert.Equal(CadLaunch.NotInstalled, r.Outcome);
    }

    [Fact]
    public async Task Start_runs_the_program_with_the_contract_arguments()
    {
        // A batch file stands in for MynaCAD and records what it was started with.
        var p = Project("My House.osp");
        var log = Path.Combine(Dir, "args.txt");
        var stub = Path.Combine(Dir, "stub.cmd");
        File.WriteAllText(stub, $"@echo off\r\necho %*> \"{log}\"\r\n");
        var launcher = new MynaCadLauncher(new FakePipe(), () => stub);
        var r = await launcher.OpenAsync(p);
        Assert.Equal(CadLaunch.Started, r.Outcome);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!File.Exists(log) && DateTime.UtcNow < deadline) await Task.Delay(100);
        await Task.Delay(300);
        var line = File.ReadAllText(log).Trim();
        Assert.Equal($"--managed --project \"{Path.GetFullPath(p)}\" --ops-pid {Environment.ProcessId}", line);
    }

    [Fact]
    public async Task Close_request_goes_over_the_pipe()
    {
        var pipe = new FakePipe();
        var ok = await new MynaCadLauncher(pipe, () => null).RequestCloseAsync(@"C:\a.osp", "ask");
        Assert.True(ok);
        Assert.Equal("close", (string?)JsonNode.Parse(pipe.Sent[0].Line)!["cmd"]);
        pipe.Reply = null;
        Assert.False(await new MynaCadLauncher(pipe, () => null).RequestCloseAsync(@"C:\a.osp"));
    }

    [Fact]
    public void The_pipe_name_uses_the_lease_hash()
    {
        var p = @"C:\Work\House.osp";
        Assert.Equal("MynaCAD." + ProjectLease.PathHash(p), NamedCadPipe.PipeName(p));
    }
}
