// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using Microsoft.Data.Sqlite;
using Xunit;

namespace Office.Shared.Tests;

/// <summary>Live sync, the read-only side (MynaCAD <c>docs/live-sync.md</c>): the probe, the state rules, the accepted revision and the file watcher.</summary>
public class OdpSyncTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "odpsync-" + Guid.NewGuid().ToString("N"));

    public OdpSyncTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    string NewFile(string name = "p.osp")
    {
        var path = Path.Combine(_dir, name);
        using var odp = OdpFile.Create(path, "House");
        return path;
    }

    static void Sql(string path, string sql, params (string, object)[] args)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        using var c = db.CreateCommand();
        c.CommandText = sql;
        foreach (var (n, v) in args) c.Parameters.AddWithValue(n, v);
        c.ExecuteNonQuery();
    }

    static SyncProbe Ok(string path) => Assert.IsType<SyncProbe>(OdpSync.Probe(path).Probe);

    [Fact]
    public void Every_write_says_who_wrote_the_revision()
    {
        var path = NewFile();
        long rev;
        using (var odp = OdpFile.Open(path)) rev = odp.SetProjectDoc("{\"name\":\"x\"}");
        var p = Ok(path);
        Assert.Equal((rev, "mynaops", rev), (p.Revision, p.LastApp, p.LastRevision));
        Assert.EndsWith("Z", p.LastTime);
    }

    [Fact]
    public void A_probe_never_writes()
    {
        var path = NewFile();
        using (var odp = OdpFile.Open(path)) odp.SetProjectDoc("{}");
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString())) { db.Open(); new SqliteCommand("PRAGMA wal_checkpoint(TRUNCATE)", db).ExecuteNonQuery(); }
        var before = File.ReadAllBytes(path);
        Ok(path);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Rust_style_stamps_are_read_and_a_later_drawing_save_makes_the_takeoff_stale()
    {
        var path = NewFile();
        // The values aad-odp writes (JSON text in meta).
        Sql(path, "INSERT INTO meta (key, value) VALUES ('sync.last', $v)", ("$v", "{\"app\":\"mynacad\",\"revision\":3,\"time\":\"2026-11-15T09:30:12Z\"}"));
        Sql(path, "INSERT INTO meta (key, value) VALUES ('sync.takeoff.7', $v)", ("$v", "{\"revision\":3,\"recorded\":\"2026-11-15T09:30:12Z\",\"levels\":{\"2\":{\"revision\":3,\"recorded\":\"x\"},\"3\":{\"revision\":3}}}"));
        var p = Ok(path);
        Assert.Equal("mynacad", p.LastApp);
        var t = Assert.Single(p.Takeoff);
        Assert.Equal((7u, 3L), (t.Slot, t.Revision));
        Assert.Equal(new[] { "2", "3" }, t.Levels.Select(l => l.LevelId).OrderBy(x => x));
        Assert.Empty(p.StaleSlots);
        using (var odp = OdpFile.Open(path)) { odp.SetDrawing("a.dxf", "dxf", new byte[] { 1, 2, 3 }); odp.SetProjectDoc("{}"); }   // drawing saved at revision 1..2, take-off says 3
        Assert.Empty(Ok(path).StaleSlots);
        using (var odp = OdpFile.Open(path)) { odp.SetProjectDoc("{}"); odp.SetDrawing("a.dxf", "dxf", new byte[] { 1, 2, 3, 4 }); }   // revision 5 > 3
        Assert.Equal(new[] { 7u }, Ok(path).StaleSlots);
    }

    [Fact]
    public void Missing_empty_foreign_cut_and_hostile_files_are_results_not_exceptions()
    {
        Assert.Equal(SyncState.Missing, OdpSync.Probe(Path.Combine(_dir, "nope.osp")).Failure);
        Assert.Equal(SyncState.Missing, OdpSync.Probe(null).Failure);
        var empty = Path.Combine(_dir, "empty.osp"); File.WriteAllBytes(empty, Array.Empty<byte>());
        Assert.Equal(SyncState.Unreadable, OdpSync.Probe(empty).Failure);
        var text = Path.Combine(_dir, "text.osp"); File.WriteAllText(text, "this is not a database at all, but it is long enough to look like a file header");
        Assert.Equal(SyncState.Unreadable, OdpSync.Probe(text).Failure);
        var good = NewFile("good.osp");
        using (var odp = OdpFile.Open(good)) for (var i = 0; i < 40; i++) odp.SetTakeoffRow(1, 3, (uint)i, "{\"n\":" + i + "}");
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = good, Pooling = false }.ToString())) { db.Open(); new SqliteCommand("PRAGMA wal_checkpoint(TRUNCATE)", db).ExecuteNonQuery(); }
        var bytes = File.ReadAllBytes(good);
        var cut = Path.Combine(_dir, "cut.osp"); File.WriteAllBytes(cut, bytes[..(bytes.Length / 2)]);
        Assert.Equal(SyncState.Unreadable, OdpSync.Probe(cut).Failure);
        // Hostile sync values: ignored, capped, never thrown.
        Sql(good, "INSERT INTO meta (key, value) VALUES ('sync.last', $v) ON CONFLICT(key) DO UPDATE SET value = $v", ("$v", new string('x', 100_000)));
        Sql(good, "INSERT INTO meta (key, value) VALUES ('sync.takeoff.abc', '{}')");
        Sql(good, "INSERT INTO meta (key, value) VALUES ('sync.takeoff.1', 'not json')");
        Sql(good, "INSERT INTO meta (key, value) VALUES ('sync.takeoff.2', $v)", ("$v", "{\"revision\":\"text\"}"));
        Sql(good, "INSERT INTO meta (key, value) VALUES ('sync.takeoff.3', $v)", ("$v", "{\"revision\":-5,\"levels\":{\"L\":{\"revision\":null}}}"));
        var p = Ok(good);
        Assert.Null(p.LastApp);
        Assert.Equal(3u, Assert.Single(p.Takeoff).Slot);
    }

    [Fact]
    public void A_newer_version_or_another_format_is_unreadable()
    {
        var path = NewFile();
        Sql(path, "UPDATE meta SET value = '2' WHERE key = 'version'");
        var r = OdpSync.Probe(path);
        Assert.Equal(SyncState.Unreadable, r.Failure);
        Assert.Contains("version 2", r.Reason);
        Sql(path, "UPDATE meta SET value = '1' WHERE key = 'version'");
        Sql(path, "UPDATE meta SET value = 'other' WHERE key = 'format'");
        Assert.Contains("format", OdpSync.Probe(path).Reason);
    }

    [Fact]
    public void A_file_another_program_holds_exclusively_is_locked_and_the_probe_gives_up_quickly()
    {
        var path = NewFile();
        using var holder = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        holder.Open();
        new SqliteCommand("PRAGMA locking_mode = EXCLUSIVE", holder).ExecuteNonQuery();
        new SqliteCommand("BEGIN EXCLUSIVE", holder).ExecuteNonQuery();
        var t = System.Diagnostics.Stopwatch.StartNew();
        var r = OdpSync.Probe(path);
        Assert.Equal(SyncState.Locked, r.Failure);
        Assert.True(t.Elapsed < TimeSpan.FromSeconds(3), $"took {t.Elapsed}");
        new SqliteCommand("ROLLBACK", holder).ExecuteNonQuery();
    }

    static SyncPeek Peek(long rev, string id = "a", string app = "mynacad", bool stale = false) =>
        SyncPeek.Ok(new SyncProbe(id, rev, app, rev, "2026-11-15T09:30:12Z", stale ? 9 : 1, new[] { new SyncTakeoffStamp(1, 5, "t", Array.Empty<(string, long)>()) }));

    [Fact]
    public void The_state_follows_the_revision_the_project_and_the_stamps()
    {
        SyncState S(SyncAccepted? a, SyncPeek p) => OdpSync.Evaluate(a, p).State;
        var acc = new SyncAccepted(5, "a");
        Assert.Equal(SyncState.Synced, S(acc, Peek(5)));
        Assert.Equal(SyncState.Updated, S(acc, Peek(6)));
        Assert.Equal(SyncState.Replaced, S(acc, Peek(4)));
        Assert.Equal(SyncState.Replaced, S(acc, Peek(5, "b")));
        Assert.Equal(SyncState.OutOfDate, S(acc, Peek(5, stale: true)));
        Assert.Equal(SyncState.Updated, S(acc, Peek(7, stale: true)));
        Assert.Equal(SyncState.Locked, S(null, new SyncPeek(SyncState.Locked, null, null)));
        Assert.Equal(SyncState.Missing, S(null, new SyncPeek(SyncState.Missing, null, null)));
    }

    [Fact]
    public void The_text_names_the_writer_and_the_local_time()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("t", TimeSpan.FromHours(5.5), "t", "t");
        var p = ((SyncProbe)Peek(6).Probe!);
        Assert.Equal("Updated by MynaCAD at 15:00", OdpSync.Describe(SyncState.Updated, p, zone));
        Assert.Equal("Updated elsewhere", OdpSync.Describe(SyncState.Updated, p with { LastApp = "x" }, zone));
        Assert.Equal("Updated by MynaOps", OdpSync.Describe(SyncState.Updated, p with { LastApp = "mynaops", LastTime = "garbage" }, zone));
        Assert.Equal("Synced", OdpSync.Describe(SyncState.Synced, null));
        Assert.Equal("Locked", OdpSync.Describe(SyncState.Locked, null));
    }

    [Fact]
    public async Task The_service_reports_another_programs_write_until_the_file_is_read_and_ignores_its_own()
    {
        var path = NewFile();
        var seen = new List<SyncStatus>();
        void On(SyncStatus s) { lock (seen) seen.Add(s); }
        SyncStatusService.Changed += On;
        try
        {
            await SyncStatusService.RefreshAsync(path);                 // first sight: the baseline
            Assert.Equal(SyncState.Synced, SyncStatusService.Current.State);
            using (var odp = OdpFile.Open(path)) { var rev = odp.SetProjectDoc("{}"); SyncStatusService.NoteOwnWrite(path, rev); }
            await SyncStatusService.RefreshAsync(path);
            Assert.Equal(SyncState.Synced, SyncStatusService.Current.State);   // our own write
            using (var odp = OdpFile.Open(path)) odp.SetTakeoffRow(1, 3, 0, "{}");                 // "MynaCAD" writes
            await SyncStatusService.RefreshAsync(path);
            Assert.Equal(SyncState.Updated, SyncStatusService.Current.State);
            Assert.StartsWith("Updated by ", SyncStatusService.Current.Text);
            await SyncStatusService.AcceptAsync(path);                  // the take-off page read it
            Assert.Equal(SyncState.Synced, SyncStatusService.Current.State);
            File.Delete(path);
            await SyncStatusService.RefreshAsync(path);
            Assert.Equal(SyncState.Missing, SyncStatusService.Current.State);
            await SyncStatusService.RefreshAsync(null);
            Assert.Equal(SyncState.None, SyncStatusService.Current.State);
        }
        finally { SyncStatusService.Changed -= On; }
        Assert.Contains(seen, s => s.State == SyncState.Updated);
    }

    [Fact]
    public async Task An_outside_write_between_our_read_and_our_write_is_not_swallowed()
    {
        var path = NewFile();
        await SyncStatusService.RefreshAsync(path);
        long ours;
        using (var odp = OdpFile.Open(path)) { odp.SetTakeoffRow(1, 3, 0, "{}"); ours = odp.SetProjectDoc("{}"); }   // revisions 1 and 2; only 2 reported as ours
        SyncStatusService.NoteOwnWrite(path, ours);
        await SyncStatusService.RefreshAsync(path);
        Assert.Equal(SyncState.Updated, SyncStatusService.Current.State);
    }

    [Fact]
    public async Task The_watcher_brings_the_poll_forward_and_a_burst_of_writes_is_one_poll()
    {
        var path = NewFile();
        var polls = 0;
        var got = new TaskCompletionSource();
        using var w = new ProjectFileWatcher(_ => { if (Interlocked.Increment(ref polls) >= 1) got.TrySetResult(); return Task.CompletedTask; });
        w.Watch(path);
        for (var i = 0; i < 20; i++) { using var odp = OdpFile.Open(path); odp.SetTakeoffRow(1, 3, (uint)i, "{}"); }
        await Task.WhenAny(got.Task, Task.Delay(5000));
        Assert.True(got.Task.IsCompleted, "the watcher saw the writes");
        await Task.Delay(ProjectFileWatcher.DebounceMs * 3);
        var burst = Volatile.Read(ref polls);
        Assert.InRange(burst, 1, 6);          // debounced: far fewer than the 20 writes and their log files
        w.Watch(null);                        // stops watching; nothing is raised any more
        await Task.Delay(100);
        var after = Volatile.Read(ref polls);
        using (var odp = OdpFile.Open(path)) odp.SetTakeoffRow(1, 3, 99, "{}");
        await Task.Delay(ProjectFileWatcher.DebounceMs * 3);
        Assert.Equal(after, Volatile.Read(ref polls));
    }

    [Fact]
    public async Task Realtime_off_pauses_the_detector_counts_waiting_with_one_probe_and_sync_now_reads_once()
    {
        var path = NewFile("rt.osp");
        await SyncStatusService.SetRealtimeAsync(path, true);
        try
        {
            await SyncStatusService.RefreshAsync(path);   // the baseline
            Assert.Equal(SyncState.Synced, SyncStatusService.Current.State);
            var reads = SyncStatusService.DetectorReads;

            await SyncStatusService.SetRealtimeAsync(path, false);
            Assert.Equal(reads + 1, SyncStatusService.DetectorReads);        // switching off: the one probe that counts what waits
            Assert.Equal((SyncState.Paused, "Sync off"), (SyncStatusService.Current.State, SyncStatusService.Current.Text));
            Assert.False(SyncStatusService.AllowDetect());

            // The other program writes twice. With sync off the detector reads nothing and says nothing new.
            using (var odp = OdpFile.Open(path)) { odp.SetProjectDoc("{\"a\":1}"); odp.SetProjectDoc("{\"a\":2}"); }
            reads = SyncStatusService.DetectorReads;
            for (var i = 0; i < 3; i++) await SyncStatusService.RefreshAsync(path);
            Assert.Equal(reads, SyncStatusService.DetectorReads);
            Assert.Equal("Sync off", SyncStatusService.Current.Text);

            // The user looks: one probe, and the text says how many changes wait.
            await SyncStatusService.ShowOffAsync(path);
            Assert.Equal(reads + 1, SyncStatusService.DetectorReads);
            Assert.Equal("Sync off - 2 changes waiting", SyncStatusService.Current.Text);
            Assert.Equal(2, SyncStatusService.Waiting);

            // Sync now: a forced look, then what is in the file is taken; nothing waits any more, and the switch stays off.
            await SyncStatusService.RefreshAsync(path, force: true);
            await SyncStatusService.AcceptAsync(path);
            Assert.Equal("Sync off", SyncStatusService.Current.Text);
            Assert.False(SyncStatusService.RealtimeOn);

            // On again: the normal status returns and the detector reads.
            await SyncStatusService.SetRealtimeAsync(path, true);
            Assert.Equal(SyncState.Synced, SyncStatusService.Current.State);
            reads = SyncStatusService.DetectorReads;
            await SyncStatusService.RefreshAsync(path);
            Assert.True(SyncStatusService.DetectorReads > reads);
        }
        finally { await SyncStatusService.SetRealtimeAsync(path, true); }
    }

    [Fact]
    public void The_off_text_uses_the_words_of_both_apps()
    {
        Assert.Equal("Sync off", SyncStatusService.OffText(0));
        Assert.Equal("Sync off - 1 change waiting", SyncStatusService.OffText(1));
        Assert.Equal("Sync off - 7 changes waiting", SyncStatusService.OffText(7));
        Assert.Equal("Sync off", OdpSync.Describe(SyncState.Paused, null));
    }
}
