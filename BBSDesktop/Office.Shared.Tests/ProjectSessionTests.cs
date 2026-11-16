// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using Office.Shared;
using Xunit;

namespace Office.Shared.Tests;

[Collection("ops-first")]
public sealed class ProjectSessionTests : OpsFirstTestBase
{
    static readonly DateTime T0 = new(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Open_takes_the_lease_and_writes_the_holder_without_a_revision()
    {
        var p = Project();
        long before; using (var odp = OdpFile.Open(p)) before = odp.Revision();
        var open = ProjectSession.Open(p, now: () => T0, heartbeat: false);
        Assert.Equal(SessionRefusal.None, open.Refusal);
        using var s = open.Session!;
        Assert.True(s.Online);
        Assert.True(ProjectLease.IsHeld(p, ProjectLease.AppOps));
        var holder = ProjectSession.ReadHolder(p);
        Assert.Equal(Environment.ProcessId, holder?.OpsPid);
        Assert.Equal(Environment.MachineName, holder?.Machine);
        Assert.Equal(T0, holder?.HeartbeatUtc);
        using var after = OdpFile.Open(p);
        Assert.Equal(before, after.Revision());                                   // a local key: no revision, no sync.last
        Assert.Empty(after.ChangesSince(before));
    }

    [Fact]
    public void Dispose_clears_the_holder_and_the_lease()
    {
        var p = Project();
        var s = ProjectSession.Open(p, heartbeat: false).Session!;
        s.Dispose();
        Assert.Null(ProjectSession.ReadHolder(p));
        Assert.False(ProjectLease.IsHeld(p, ProjectLease.AppOps));
    }

    [Fact]
    public void A_second_MynaOps_on_the_same_pc_is_refused_by_the_lease()
    {
        var p = Project();
        using var first = ProjectSession.Open(p, heartbeat: false).Session!;
        var second = ProjectSession.Open(p, heartbeat: false);
        Assert.Equal(SessionRefusal.OtherMynaOps, second.Refusal);
        Assert.Null(second.Session);
        Assert.Equal(Environment.ProcessId, second.Lease?.Pid);
    }

    [Fact]
    public void A_live_holder_on_another_machine_warns_and_take_over_proceeds()
    {
        var p = Project();
        var other = new SessionHolder("OTHER-PC", "someone", 4242, T0.AddMinutes(-5), T0.AddSeconds(-30));
        using (var odp = OdpFile.Open(p)) odp.PutLocalMeta(SessionHolder.Key, other.ToJson());

        var warned = ProjectSession.Open(p, now: () => T0, heartbeat: false);
        Assert.Equal(SessionRefusal.LiveElsewhere, warned.Refusal);
        Assert.Equal("OTHER-PC", warned.Holder?.Machine);
        Assert.False(ProjectLease.IsHeld(p, ProjectLease.AppOps));              // the refusal gave the lease back

        var taken = ProjectSession.Open(p, takeOver: true, now: () => T0, heartbeat: false);
        Assert.Equal(SessionRefusal.None, taken.Refusal);
        using var s = taken.Session!;
        Assert.Equal(Environment.MachineName, ProjectSession.ReadHolder(p)?.Machine);
    }

    [Fact]
    public void A_stale_or_damaged_holder_does_not_block()
    {
        var p = Project();
        var old = new SessionHolder("OTHER-PC", "someone", 4242, T0.AddHours(-3), T0.AddSeconds(-121));
        using (var odp = OdpFile.Open(p)) odp.PutLocalMeta(SessionHolder.Key, old.ToJson());
        using (var s = ProjectSession.Open(p, now: () => T0, heartbeat: false).Session) Assert.NotNull(s);
        using (var odp = OdpFile.Open(p)) odp.PutLocalMeta(SessionHolder.Key, "{ not json");
        using (var s = ProjectSession.Open(p, now: () => T0, heartbeat: false).Session) Assert.NotNull(s);
    }

    [Fact]
    public void Holder_liveness_rule()
    {
        var h = new SessionHolder("PC-A", "u", 10, T0, T0);
        Assert.False(h.IsLiveElsewhere(T0.AddSeconds(10), "PC-A", 10));      // this very process
        Assert.True(h.IsLiveElsewhere(T0.AddSeconds(10), "PC-A", 11));       // another process on the same PC
        Assert.True(h.IsLiveElsewhere(T0.AddSeconds(119), "PC-B", 10));
        Assert.False(h.IsLiveElsewhere(T0.AddSeconds(120), "PC-B", 10));     // two minutes without a beat: gone
        Assert.Null(SessionHolder.Parse("{\"machine\":\"x\"}"));             // no heartbeat
        Assert.Null(SessionHolder.Parse(new string('x', 9000)));
    }

    [Fact]
    public void The_heartbeat_refreshes_the_record()
    {
        var p = Project();
        var clock = T0;
        using var s = ProjectSession.Open(p, now: () => clock, heartbeat: false).Session!;
        clock = T0.AddSeconds(30);
        s.Beat();
        Assert.Equal(T0.AddSeconds(30), ProjectSession.ReadHolder(p)?.HeartbeatUtc);
        Assert.Equal(T0, ProjectSession.ReadHolder(p)?.StartedUtc);
    }

    [Fact]
    public void Local_meta_refuses_keys_that_are_not_session_keys()
    {
        var p = Project();
        using var odp = OdpFile.Open(p);
        Assert.Throws<InvalidDataException>(() => odp.PutLocalMeta("revision", "5"));
        Assert.Throws<InvalidDataException>(() => odp.PutLocalMeta("config.rev", "{}"));
        Assert.Throws<InvalidDataException>(() => odp.DeleteLocalMeta("name"));
    }

    [Fact]
    public async Task Offline_asks_mynacad_to_close_then_releases_the_lease_and_folds_the_log()
    {
        var p = Project();
        using var s = ProjectSession.Open(p, heartbeat: false).Session!;
        var cad = FakeCad(p);
        var asked = 0;
        var result = await s.GoOfflineAsync(_ => { asked++; cad.Dispose(); return Task.FromResult(true); }, TimeSpan.FromSeconds(5));
        Assert.True(result.Offline, result.Message);
        Assert.Equal(1, asked);
        Assert.False(s.Online);
        Assert.False(ProjectLease.IsHeld(p, ProjectLease.AppOps));
        Assert.Null(ProjectSession.ReadHolder(p));
        Assert.False(File.Exists(p + "-wal") && new FileInfo(p + "-wal").Length > 0, "the write-ahead log is folded in");
        // someone else may now take the project
        using var other = ProjectLease.TryAcquire(p, ProjectLease.AppOps);
        Assert.NotNull(other);
    }

    [Fact]
    public async Task Offline_stays_online_when_mynacad_does_not_close()
    {
        var p = Project();
        using var s = ProjectSession.Open(p, heartbeat: false).Session!;
        using var cad = FakeCad(p);
        var result = await s.GoOfflineAsync(_ => Task.FromResult(false), TimeSpan.FromMilliseconds(600));
        Assert.False(result.Offline);
        Assert.True(s.Online);
        Assert.True(ProjectLease.IsHeld(p, ProjectLease.AppOps));
        Assert.Contains("MynaCAD is still open", result.Message);
    }

    [Fact]
    public async Task Offline_waits_for_a_user_who_closes_mynacad_by_hand()
    {
        var p = Project();
        using var s = ProjectSession.Open(p, heartbeat: false).Session!;
        var cad = FakeCad(p);
        _ = Task.Run(async () => { await Task.Delay(500); cad.Dispose(); });
        var result = await s.GoOfflineAsync(null, TimeSpan.FromSeconds(5));   // nothing could be asked over a pipe
        Assert.True(result.Offline, result.Message);
    }

    [Fact]
    public async Task Online_again_takes_the_lease_and_the_holder_back()
    {
        var p = Project();
        using var s = ProjectSession.Open(p, heartbeat: false).Session!;
        await s.GoOfflineAsync(null, TimeSpan.FromSeconds(1));
        Assert.True(s.GoOnline(out var msg), msg);
        Assert.True(s.Online);
        Assert.True(ProjectLease.IsHeld(p, ProjectLease.AppOps));
        Assert.Equal(Environment.ProcessId, ProjectSession.ReadHolder(p)?.OpsPid);
    }

    [Fact]
    public async Task Online_again_fails_when_another_MynaOps_took_the_project()
    {
        var p = Project();
        using var s = ProjectSession.Open(p, heartbeat: false).Session!;
        await s.GoOfflineAsync(null, TimeSpan.FromSeconds(1));
        // Another process takes the lease while this one is offline (an exclusive handle stands in for it).
        using var thief = new FileStream(ProjectLease.LockPath(p, ProjectLease.AppOps), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
        Assert.False(s.GoOnline(out var msg));
        Assert.False(s.Online);
        Assert.Contains("Another MynaOps", msg);
    }
}

[Collection("ops-first")]
public sealed class MaintenanceGateTests : OpsFirstTestBase
{
    [Fact]
    public void Compact_and_restore_refuse_while_the_cad_lease_is_held_and_work_after_it_is_released()
    {
        var p = Project();
        var backup = ProjectDatabase.Backup(p, "manual");
        var cad = FakeCad(p);
        var open = ProjectDatabase.OpenElsewhere(p);
        Assert.True(open.IsOpen);
        Assert.Equal("MynaCAD", open.Who);
        Assert.Throws<InvalidOperationException>(() => ProjectDatabase.Compact(p));
        Assert.Throws<InvalidOperationException>(() => ProjectDatabase.Restore(p, backup));
        cad.Dispose();
        Assert.False(ProjectDatabase.OpenElsewhere(p).IsOpen);
        ProjectDatabase.Compact(p);
        Assert.NotNull(ProjectDatabase.Restore(p, backup));
    }

    [Fact]
    public async Task Compact_works_after_the_database_went_offline()
    {
        var p = Project();
        using var s = ProjectSession.Open(p, heartbeat: false).Session!;
        var cad = FakeCad(p);
        Assert.Throws<InvalidOperationException>(() => ProjectDatabase.Compact(p));            // online, MynaCAD open: refused
        var r = await s.GoOfflineAsync(_ => { cad.Dispose(); return Task.FromResult(true); }, TimeSpan.FromSeconds(5));
        Assert.True(r.Offline, r.Message);
        ProjectDatabase.Compact(p);                                                            // offline: allowed
    }

    [Fact]
    public void Another_MynaOps_holding_the_ops_lease_also_blocks_maintenance()
    {
        var p = Project();
        Directory.CreateDirectory(ProjectLease.Directory);
        using var other = new FileStream(ProjectLease.LockPath(Path.GetFullPath(p), ProjectLease.AppOps), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
        Assert.Equal("another MynaOps", ProjectDatabase.OpenElsewhere(p).Who);
    }

    [Fact]
    public void This_MynaOps_holding_its_own_lease_does_not_block_its_own_maintenance()
    {
        var p = Project();
        using var s = ProjectSession.Open(p, heartbeat: false).Session!;
        Assert.False(ProjectDatabase.OpenElsewhere(p).IsOpen);
        ProjectDatabase.Compact(p);
    }
}
