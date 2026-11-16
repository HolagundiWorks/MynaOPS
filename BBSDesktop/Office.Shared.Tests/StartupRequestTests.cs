// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using Office.Shared;
using Xunit;

namespace Office.Shared.Tests;

/// <summary>Package E (docs/ops-first-workflow.md 16.4 to 16.6): what MynaOps is asked to do at start, the pipe that forwards it, the removed pointer file.</summary>
public sealed class StartupRequestTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "startup-" + Guid.NewGuid().ToString("N")[..8]);

    public StartupRequestTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    string Touch(string name) { var p = Path.Combine(_dir, name); File.WriteAllText(p, "x"); return p; }

    [Fact]
    public void A_project_path_opens_and_a_drawing_is_offered_for_import()
    {
        var osp = Touch("House.osp");
        var dxf = Touch("plan.DXF");
        Assert.Equal(new StartupRequest(StartupKind.OpenProject, osp), StartupRequest.Parse(new[] { osp }));
        Assert.Equal(new StartupRequest(StartupKind.OpenProject, osp), StartupRequest.Parse(new[] { "\"" + osp + "\"" }));
        Assert.Equal(new StartupRequest(StartupKind.ImportPick, dxf), StartupRequest.Parse(new[] { "--import-pick", dxf }));
        Assert.Equal(StartupRequest.None, StartupRequest.Parse(Array.Empty<string>()));
        Assert.Equal(StartupRequest.None, StartupRequest.Parse(null));
    }

    [Fact]
    public void Other_files_missing_files_and_wrong_kinds_are_not_requests()
    {
        var txt = Touch("notes.txt");
        var osp = Touch("A.osp");
        Assert.Equal(StartupRequest.None, StartupRequest.Parse(new[] { txt }));
        Assert.Equal(StartupRequest.None, StartupRequest.Parse(new[] { Path.Combine(_dir, "missing.osp") }));
        Assert.Equal(StartupRequest.None, StartupRequest.Parse(new[] { "--import-pick", osp }));           // a project is never import-picked
        Assert.Null(StartupRequest.Make(StartupKind.OpenProject, Touch("B.dxf")));                         // a drawing is never opened as a project
        Assert.Equal(StartupRequest.None, StartupRequest.Parse(new[] { "--ai-host" }));
    }

    [Fact]
    public void Hostile_text_never_throws()
    {
        foreach (var bad in new[] { "", " ", "\0.osp", "a\0b.osp", new string('x', 5000) + ".osp", "C:\\a|b?.osp", "CON:.osp", "::", "\"", "--import-pick" })
        {
            Assert.Equal(StartupRequest.None, StartupRequest.Parse(new[] { bad }));
            Assert.Equal(StartupRequest.None, StartupRequest.Parse(new[] { "--import-pick", bad }));
            Assert.Null(StartupRequest.Make(StartupKind.OpenProject, bad));
        }
        Assert.Equal(StartupRequest.None, StartupRequest.Parse(Enumerable.Repeat("--x", 100000)));
    }

    [Fact]
    public async Task A_second_start_forwards_its_request_to_the_running_MynaOps()
    {
        var osp = Touch("House.osp");
        var dwg = Touch("plan.dwg");
        var got = new List<StartupRequest>();
        var name = "MynaOps.test." + Guid.NewGuid().ToString("N");
        using var server = new OpsInstance(r => { lock (got) got.Add(r); return true; }, name);
        server.Start();
        Assert.True(OpsInstance.TryForward(new StartupRequest(StartupKind.OpenProject, osp), TimeSpan.FromSeconds(5), name));
        Assert.True(OpsInstance.TryForward(new StartupRequest(StartupKind.ImportPick, dwg), TimeSpan.FromSeconds(5), name));
        Assert.True(OpsInstance.TryForward(StartupRequest.None, TimeSpan.FromSeconds(5), name));
        await Task.Delay(50);
        lock (got) Assert.Equal(new[] { StartupKind.OpenProject, StartupKind.ImportPick, StartupKind.None }, got.Select(r => r.Kind).ToArray());
    }

    [Fact]
    public void Nobody_running_means_no_forward()
    {
        var osp = Touch("House.osp");
        Assert.False(OpsInstance.TryForward(new StartupRequest(StartupKind.OpenProject, osp), TimeSpan.FromMilliseconds(200), "MynaOps.test.none." + Guid.NewGuid().ToString("N")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1]")]
    [InlineData("{\"cmd\":5}")]
    [InlineData("{\"cmd\":\"open\"}")]
    [InlineData("{\"cmd\":\"open\",\"path\":5}")]
    [InlineData("{\"cmd\":\"open\",\"path\":\"relative\\\\x.osp\"}")]
    [InlineData("{\"cmd\":\"import-pick\",\"path\":\"C:\\\\no\\\\such.dxf\"}")]
    [InlineData("{\"cmd\":\"launch-missiles\"}")]
    public void A_bad_request_is_answered_not_ok_and_never_reaches_the_handler(string line)
    {
        var calls = 0;
        using var server = new OpsInstance(_ => { calls++; return true; }, "MynaOps.test.hostile");
        var reply = server.Handle(line);
        Assert.Contains("\"ok\":false", reply);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void A_handler_that_cannot_take_the_request_makes_the_reply_not_ok()
    {
        var osp = Touch("House.osp");
        using var server = new OpsInstance(_ => false, "MynaOps.test.busy");
        Assert.Contains("\"ok\":false", server.Handle("{\"cmd\":\"open\",\"path\":" + System.Text.Json.JsonSerializer.Serialize(osp) + "}"));
    }

    [Fact]
    public void The_pipe_name_is_per_user_and_stable()
    {
        Assert.Equal(OpsInstance.PipeName("A\\ana"), OpsInstance.PipeName("a\\ANA"));
        Assert.NotEqual(OpsInstance.PipeName("A\\ana"), OpsInstance.PipeName("A\\bob"));
        Assert.Matches("^MynaOps\\.[0-9a-f]{16}$", OpsInstance.PipeName("A\\ana"));
    }

    [Fact]
    public void A_stale_active_project_pointer_is_removed_and_nothing_else_is()
    {
        var pointer = Path.Combine(_dir, "active-project.json");
        var other = Touch("suite.json");
        File.WriteAllText(pointer, "{}");
        Assert.True(LegacyFiles.RemoveActiveProjectPointer(_dir));
        Assert.False(File.Exists(pointer));
        Assert.True(File.Exists(other));
        Assert.False(LegacyFiles.RemoveActiveProjectPointer(_dir), "nothing left to remove");
        Assert.False(LegacyFiles.RemoveActiveProjectPointer(Path.Combine(_dir, "no", "such", "folder")));
    }

    [Fact]
    public void The_pointer_removal_keeps_a_directory_that_has_that_name()
    {
        var asDir = Path.Combine(_dir, "active-project.json");
        Directory.CreateDirectory(asDir);
        Assert.False(LegacyFiles.RemoveActiveProjectPointer(_dir));
        Assert.True(Directory.Exists(asDir));
    }
}
