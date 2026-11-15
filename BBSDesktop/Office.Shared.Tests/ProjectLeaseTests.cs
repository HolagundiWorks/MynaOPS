// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Office.Shared;
using Xunit;

namespace Office.Shared.Tests;

/// <summary>Shared scratch folder and lease folder for the Ops-first tests (the lease folder is moved with OSTUDIO_LEASE_DIR; the tests of this collection run one at a time).</summary>
[CollectionDefinition("ops-first", DisableParallelization = true)]
public sealed class OpsFirstCollection { }

public abstract class OpsFirstTestBase : IDisposable
{
    protected readonly string Dir = Path.Combine(Path.GetTempPath(), "ops-first-" + Guid.NewGuid().ToString("N"));
    readonly string? _oldLeaseDir = Environment.GetEnvironmentVariable("OSTUDIO_LEASE_DIR");

    protected OpsFirstTestBase()
    {
        Directory.CreateDirectory(Dir);
        Environment.SetEnvironmentVariable("OSTUDIO_LEASE_DIR", Path.Combine(Dir, "leases"));
    }

    public virtual void Dispose()
    {
        Environment.SetEnvironmentVariable("OSTUDIO_LEASE_DIR", _oldLeaseDir);
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A project file with a drawing and a few take-off rows (what MynaCAD would have recorded).</summary>
    protected string Project(string name = "House.osp", string drawing = "one")
    {
        var path = Path.Combine(Dir, name);
        using var odp = OdpFile.Create(path, Path.GetFileNameWithoutExtension(name));
        odp.SetDrawing("a.dxf", "dxf", System.Text.Encoding.UTF8.GetBytes(drawing));
        for (uint i = 0; i < 3; i++) odp.SetTakeoffRow(0, 1, i, "{\"n\":" + i + "}");
        return path;
    }

    /// <summary>Stands in for a MynaCAD in another process: holds <c>&lt;hash&gt;.cad.lock</c> with no sharing until disposed.</summary>
    protected static FileStream FakeCad(string project)
    {
        Directory.CreateDirectory(ProjectLease.Directory);
        return new FileStream(ProjectLease.LockPath(project, ProjectLease.AppCad), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
    }

    protected static void RawMeta(string path, string key, string value)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO meta (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = $v";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    protected static void RawSql(string path, string sql, params (string, object)[] args)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        cmd.ExecuteNonQuery();
    }
}

[Collection("ops-first")]
public sealed class ProjectLeaseTests : OpsFirstTestBase
{
    [Fact]
    public void Path_hash_is_16_hex_digits_of_the_lower_case_full_path()
    {
        var a = ProjectLease.PathHash(@"C:\Work\House.osp");
        Assert.Matches("^[0-9a-f]{16}$", a);
        Assert.Equal(a, ProjectLease.PathHash(@"c:\work\HOUSE.OSP"));
        Assert.NotEqual(a, ProjectLease.PathHash(@"C:\Work\Other.osp"));
        // The value the contract names (SHA-256 of the lower-case UTF-8 path, first 16 hex digits), fixed here so MynaCAD's side can be checked against it.
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(@"c:\work\house.osp")))[..16].ToLowerInvariant();
        Assert.Equal(expected, a);
    }

    [Fact]
    public void A_lease_is_exclusive_and_goes_when_released()
    {
        var p = Project();
        using (var lease = ProjectLease.TryAcquire(p))
        {
            Assert.NotNull(lease);
            Assert.True(File.Exists(lease!.LockFile));
            Assert.Null(ProjectLease.TryAcquire(p));                         // the same process cannot take it twice either
            Assert.True(ProjectLease.IsHeld(p, ProjectLease.AppOps));
            Assert.False(ProjectLease.IsHeldByOther(p, ProjectLease.AppOps)); // it is ours
            var info = ProjectLease.Describe(p, ProjectLease.AppOps);
            Assert.Equal(Environment.ProcessId, info?.Pid);
            Assert.Equal("ops", info?.App);
            Assert.Equal(Path.GetFullPath(p), info?.Project);
        }
        Assert.False(File.Exists(ProjectLease.LockPath(p, ProjectLease.AppOps)));
        Assert.False(ProjectLease.IsHeld(p, ProjectLease.AppOps));
        using var again = ProjectLease.TryAcquire(p);
        Assert.NotNull(again);
    }

    [Fact]
    public void Ops_and_cad_leases_are_separate_files()
    {
        var p = Project();
        using var ops = ProjectLease.TryAcquire(p, ProjectLease.AppOps);
        Assert.NotEqual(ProjectLease.LockPath(p, "ops"), ProjectLease.LockPath(p, "cad"));
        Assert.False(ProjectLease.IsHeld(p, ProjectLease.AppCad));
        using var cad = FakeCad(p);
        Assert.True(ProjectLease.IsHeld(p, ProjectLease.AppCad));
        Assert.True(ProjectLease.IsHeldByOther(p, ProjectLease.AppCad));
    }

    [Fact]
    public void A_lease_held_by_another_handle_is_seen_and_refused()
    {
        var p = Project();
        using (FakeCad(p))
        {
            Assert.True(ProjectLease.IsHeld(p, ProjectLease.AppCad));
            Assert.Null(ProjectLease.TryAcquire(p, ProjectLease.AppCad));
        }
        Assert.False(ProjectLease.IsHeld(p, ProjectLease.AppCad));
    }

    [Fact]
    public void A_stale_lock_file_left_by_a_crash_is_not_a_lease()
    {
        var p = Project();
        Directory.CreateDirectory(ProjectLease.Directory);
        File.WriteAllText(ProjectLease.LockPath(p, ProjectLease.AppCad), "{\"app\":\"cad\",\"pid\":1}");   // nobody holds it open
        Assert.False(ProjectLease.IsHeld(p, ProjectLease.AppCad));
        using var lease = ProjectLease.TryAcquire(p, ProjectLease.AppCad);
        Assert.NotNull(lease);
    }

    [Fact]
    public void Body_parsing_is_defensive()
    {
        Assert.Null(ProjectLease.ParseBody(null));
        Assert.Null(ProjectLease.ParseBody("not json"));
        Assert.Null(ProjectLease.ParseBody("[1,2]"));
        Assert.Null(ProjectLease.ParseBody("{\"pid\":-3}"));
        Assert.Null(ProjectLease.ParseBody("{\"pid\":\"x\"}"));
        Assert.Equal(7, ProjectLease.ParseBody("{\"app\":\"cad\",\"pid\":7,\"started_utc\":\"2026-11-15T10:00:00Z\",\"project\":\"C:\\\\a.osp\"}")?.Pid);
    }

    [Fact]
    public void A_lease_held_by_a_process_goes_when_the_process_is_killed()
    {
        var p = Project();
        var lockFile = ProjectLease.LockPath(p, ProjectLease.AppCad);
        Directory.CreateDirectory(ProjectLease.Directory);
        // A real second process (PowerShell) opens the lock file with no sharing and delete-on-close, like MynaCAD does.
        var script = $"$f = [System.IO.File]::Open('{lockFile.Replace("'", "''")}', 'OpenOrCreate', 'ReadWrite', 'None'); Start-Sleep -Seconds 60";
        var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command \"" + script + "\"") { UseShellExecute = false, CreateNoWindow = true };
        using var proc = Process.Start(psi);
        if (proc is null) return;
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!ProjectLease.IsHeld(p, ProjectLease.AppCad) && DateTime.UtcNow < deadline) Thread.Sleep(100);
            Assert.True(ProjectLease.IsHeld(p, ProjectLease.AppCad), "the second process should hold the lease");
            Assert.True(ProjectLease.IsHeldByOther(p, ProjectLease.AppCad));
            Assert.Null(ProjectLease.TryAcquire(p, ProjectLease.AppCad));
        }
        finally
        {
            try { proc.Kill(true); } catch (InvalidOperationException) { }
            proc.WaitForExit(10_000);
        }
        // The handle went with the process: the lease is gone and can be taken. (The file itself stays, since this holder did not ask for delete on close.)
        Assert.False(ProjectLease.IsHeld(p, ProjectLease.AppCad));
        using var lease = ProjectLease.TryAcquire(p, ProjectLease.AppCad);
        Assert.NotNull(lease);
    }
}
