// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using Microsoft.Data.Sqlite;
using Office.Shared;
using Xunit;

namespace Office.Shared.Tests;

public sealed class ProjectDatabaseTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "osp-db-" + Guid.NewGuid().ToString("N"));

    public ProjectDatabaseTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    static readonly Func<string, OpenElsewhereResult> NotOpen = _ => OpenElsewhereResult.No;

    string Project(string name = "House.osp", int rows = 3, string drawing = "one")
    {
        var path = Path.Combine(_dir, name);
        using var odp = OdpFile.Create(path, Path.GetFileNameWithoutExtension(name));
        odp.SetDrawing("a.dxf", "dxf", System.Text.Encoding.UTF8.GetBytes(drawing));
        for (uint i = 0; i < rows; i++)
            odp.SetTakeoffRow(0, 1, i, "{\"n\":" + i + "}");
        return path;
    }

    static string DrawingText(string path)
    {
        using var odp = OdpFile.Open(path);
        return System.Text.Encoding.UTF8.GetString(odp.Drawing("a.dxf")!.Bytes);
    }

    [Fact]
    public void Info_reports_format_version_revision_and_row_counts()
    {
        var path = Project(rows: 3);
        var info = ProjectDatabase.Info(path);
        Assert.Equal("osp", info.Format);
        Assert.Equal(1, info.Version);
        Assert.Equal(4, info.Revision); // one drawing write and three rows
        Assert.Equal(_dir, info.Folder);
        Assert.True(info.FileBytes > 0);
        Assert.Equal(3, info.Tables.Single(t => t.Table == "takeoff_row").Rows);
        Assert.Equal(1, info.Tables.Single(t => t.Table == "drawing").Rows);
        Assert.Contains(info.Tables, t => t.Table == "change");
        Assert.Equal("wal", info.JournalMode, ignoreCase: true);
    }

    [Fact]
    public void Info_refuses_a_file_that_is_not_a_project()
    {
        var path = Path.Combine(_dir, "notes.osp");
        File.WriteAllText(path, "hello");
        Assert.Throws<InvalidDataException>(() => ProjectDatabase.Info(path));
    }

    [Fact]
    public void A_sound_file_passes_the_integrity_check()
    {
        Assert.Empty(ProjectDatabase.IntegrityCheck(Project()));
    }

    [Fact]
    public void Compact_keeps_contents_and_revision_and_gives_back_free_pages()
    {
        var path = Project(rows: 1, drawing: new string('x', 400_000));
        using (var odp = OdpFile.Open(path))
            odp.SetDrawing("a.dxf", "dxf", System.Text.Encoding.UTF8.GetBytes("small"));
        var before = ProjectDatabase.Info(path);
        ProjectDatabase.Compact(path, NotOpen);
        var after = ProjectDatabase.Info(path);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.Tables, after.Tables);
        Assert.Equal(0, after.FreePages);
        Assert.True(after.FileBytes < 400_000, $"file is {after.FileBytes} bytes after compacting");
        Assert.Equal("small", DrawingText(path));
        Assert.Empty(ProjectDatabase.IntegrityCheck(path));
    }

    [Fact]
    public void Compact_and_restore_are_refused_while_the_file_is_open_elsewhere()
    {
        var path = Project();
        var backup = ProjectDatabase.Backup(path);
        Func<string, OpenElsewhereResult> open = _ => new OpenElsewhereResult(true, "MynaCAD (House.osp)");
        var c = Assert.Throws<InvalidOperationException>(() => ProjectDatabase.Compact(path, open));
        Assert.Contains("MynaCAD", c.Message);
        Assert.Throws<InvalidOperationException>(() => ProjectDatabase.Restore(path, backup, open));
        // nothing was made or replaced
        Assert.Single(ProjectDatabase.ListBackups(path));
    }

    [Fact]
    public void An_open_connection_in_another_handle_is_seen()
    {
        var path = Project();
        Assert.False(ProjectDatabase.OpenElsewhere(path, useMarker: false).IsOpen);
        using var held = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        held.Open();
        using (var cmd = held.CreateCommand()) { cmd.CommandText = "SELECT COUNT(*) FROM meta"; cmd.ExecuteScalar(); }
        var seen = ProjectDatabase.OpenElsewhere(path, useMarker: false);
        Assert.True(seen.IsOpen);
        Assert.Equal("another program", seen.Who);
    }

    [Fact]
    public void MynaCADs_live_sync_marker_counts_only_while_MynaCAD_runs()
    {
        var path = Project();
        var marker = new ActiveProjectInfo("O-Draft", Path.Combine(_dir, "House.osp"), path, DateTime.UtcNow);
        var open = ProjectDatabase.OpenElsewhere(path, marker, mynaCadRunning: true);
        Assert.True(open.IsOpen);
        Assert.StartsWith("MynaCAD", open.Who);
        Assert.False(ProjectDatabase.OpenElsewhere(path, marker, mynaCadRunning: false).IsOpen);
        var other = new ActiveProjectInfo("O-Draft", "", Path.Combine(_dir, "Other.osp"), DateTime.UtcNow);
        Assert.False(ProjectDatabase.OpenElsewhere(path, other, mynaCadRunning: true).IsOpen);
    }

    [Fact]
    public void A_backup_is_a_whole_project_file_named_as_MynaCAD_names_them()
    {
        var path = Project();
        var made = ProjectDatabase.Backup(path, nowUtc: new DateTime(2026, 11, 11, 9, 30, 5, DateTimeKind.Utc));
        Assert.Equal(Path.Combine(_dir, "House.backups", "House 20261111T093005Z manual.osp"), made);
        Assert.Equal("one", DrawingText(made));
        // a second one in the same second gets a counter instead of replacing the first
        var again = ProjectDatabase.Backup(path, nowUtc: new DateTime(2026, 11, 11, 9, 30, 5, DateTimeKind.Utc));
        Assert.EndsWith("manual-2.osp", again);
        Assert.Equal(2, ProjectDatabase.ListBackups(path).Count);
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "House.backups"), "*tmp*"));
    }

    [Fact]
    public void Backups_are_listed_newest_first_and_other_files_are_ignored()
    {
        var path = Project();
        var folder = ProjectDatabase.BackupFolder(path);
        ProjectDatabase.Backup(path, "save", new DateTime(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc));
        ProjectDatabase.Backup(path, nowUtc: new DateTime(2026, 11, 11, 8, 0, 0, DateTimeKind.Utc));
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "keep me");
        File.WriteAllText(Path.Combine(folder, "Other 20261111T080000Z save.osp"), "not ours");
        File.WriteAllText(Path.Combine(folder, "House latest.osp"), "no stamp");
        var list = ProjectDatabase.ListBackups(path);
        Assert.Equal(new[] { "manual", "save" }, list.Select(b => b.Reason).ToArray());
        Assert.Equal(new DateTime(2026, 11, 11, 8, 0, 0, DateTimeKind.Utc), list[0].WhenUtc);
    }

    [Theory]
    [InlineData("House 20261110T174455Z save.osp", "20261110T174455Z", "save")]
    [InlineData("House 20261110T174455Z before-restore.osp", "20261110T174455Z", "before-restore")]
    [InlineData("House 20261110T174455Z.osp", "20261110T174455Z", "")]
    public void Backup_names_parse_like_MynaCADs(string name, string stamp, string reason)
    {
        Assert.Equal((stamp, reason), ProjectDatabase.ParseBackupName("House", name));
    }

    [Theory]
    [InlineData("House 2026-11-10 save.osp")]
    [InlineData("Houses 20261110T174455Z save.osp")]
    [InlineData("House 20261110T174455Z save.osp.bak")]
    public void Other_names_are_not_backups(string name)
    {
        Assert.Null(ProjectDatabase.ParseBackupName("House", name));
    }

    [Fact]
    public void Restore_puts_the_backup_back_and_keeps_what_it_replaced()
    {
        var path = Project(drawing: "first");
        var backup = ProjectDatabase.Backup(path, nowUtc: new DateTime(2026, 11, 11, 8, 0, 0, DateTimeKind.Utc));
        using (var odp = OdpFile.Open(path))
            odp.SetDrawing("a.dxf", "dxf", System.Text.Encoding.UTF8.GetBytes("second"));
        var kept = ProjectDatabase.Restore(path, backup, NotOpen, new DateTime(2026, 11, 11, 9, 0, 0, DateTimeKind.Utc));
        Assert.Equal("first", DrawingText(path));
        Assert.NotNull(kept);
        Assert.Equal("second", DrawingText(kept!));
        Assert.EndsWith("before-restore.osp", kept);
        Assert.False(File.Exists(path + "-wal"));
        Assert.Empty(Directory.GetFiles(_dir, "*tmp*"));
        Assert.Empty(ProjectDatabase.IntegrityCheck(path));
    }

    [Fact]
    public void Restore_accepts_only_a_backup_of_this_file()
    {
        var path = Project();
        var stranger = Project("Other.osp");
        Assert.Throws<InvalidOperationException>(() => ProjectDatabase.Restore(path, stranger, NotOpen));
    }
}
