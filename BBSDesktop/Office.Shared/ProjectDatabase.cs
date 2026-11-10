// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Office.Shared;

/// <summary>What <see cref="ProjectDatabase.Info"/> found.</summary>
public sealed record ProjectDatabaseInfo(
    string Path,
    string Folder,
    string Format,
    int Version,
    string ProjectId,
    string Name,
    long Revision,
    long FileBytes,
    long WalBytes,
    long PageSize,
    long PageCount,
    long FreePages,
    string JournalMode,
    IReadOnlyList<(string Table, long Rows)> Tables)
{
    /// <summary>Bytes a compact would give back (pages on the free list).</summary>
    public long ReclaimableBytes => FreePages * PageSize;
}

/// <summary>One backup of a project file in its <c>&lt;name&gt;.backups</c> folder.</summary>
public sealed record ProjectBackup(string Path, string Name, string Stamp, string Reason, long Bytes)
{
    /// <summary>The UTC time in the name (<c>20261110T174455Z</c>), or MinValue.</summary>
    public DateTime WhenUtc => DateTime.TryParseExact(Stamp, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : DateTime.MinValue;
}

/// <summary>Who else has the project file open, if anyone (<see cref="ProjectDatabase.OpenElsewhere"/>).</summary>
public sealed record OpenElsewhereResult(bool IsOpen, string? Who)
{
    public static readonly OpenElsewhereResult No = new(false, null);
}

/// <summary>
/// Looks after a project file (<c>.osp</c>) as a database: what is in it, an integrity check, compacting, and backups. MynaOps shows it as the
/// Project database page. MynaCAD reads and writes the same file through its own engine and never needs this.
///
/// Rules: nothing here changes the format (no table or column is altered or added; <c>meta.revision</c> does not move). Backups use the folder and
/// name MynaCAD's engine uses (<c>crates/engine/src/backup.rs</c>, <c>docs/AUTOSAVE-BACKUP.md</c> in MynaCAD): <c>&lt;name&gt;.backups\&lt;name&gt;
/// &lt;UTC yyyyMMddTHHmmssZ&gt; &lt;reason&gt;.osp</c>, each a whole project file made with <c>VACUUM INTO</c> under a temporary name and renamed.
/// A backup made here has the reason <c>manual</c>; MynaCAD counts it with its own when it prunes. Compacting and restoring replace or rewrite the
/// file, so they are refused while another program has it open.
/// </summary>
public static class ProjectDatabase
{
    static SqliteConnection Connect(string path, SqliteOpenMode mode) =>
        new(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false, DefaultTimeout = 5 }.ToString());

    static long Scalar(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }

    static string? Text(SqliteConnection db, string sql, params (string, object)[] args)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        return cmd.ExecuteScalar() as string;
    }

    static void Exec(SqliteConnection db, string sql, params (string, object)[] args)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        cmd.ExecuteNonQuery();
    }

    static long Length(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    /// <summary>Open read-only and check it is a project file this program reads. Throws <see cref="InvalidDataException"/> otherwise.</summary>
    static SqliteConnection OpenChecked(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("The project file was not found.", path);
        var db = Connect(path, SqliteOpenMode.ReadOnly);
        try
        {
            db.Open();
            string? format;
            try { format = Text(db, "SELECT value FROM meta WHERE key = 'format'"); }
            catch (SqliteException) { throw new InvalidDataException($"{System.IO.Path.GetFileName(path)} is not a project file (no meta table)."); }
            if (!SuiteWire.IsProjectFormat(format))
                throw new InvalidDataException($"{System.IO.Path.GetFileName(path)} is not a project file (its format is {format ?? "missing"}).");
            return db;
        }
        catch (SqliteException ex)
        {
            db.Dispose();
            throw new InvalidDataException($"{System.IO.Path.GetFileName(path)} is not a project file: {ex.Message}");
        }
        catch { db.Dispose(); throw; }
    }

    /// <summary>The format, version, revision, size and row counts of every table. Only reads.</summary>
    public static ProjectDatabaseInfo Info(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        using var db = OpenChecked(path);
        var tables = new List<(string, long)>();
        var names = new List<string>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            using var r = cmd.ExecuteReader();
            while (r.Read()) names.Add(r.GetString(0));
        }
        foreach (var name in names)
            tables.Add((name, Scalar(db, $"SELECT COUNT(*) FROM \"{name.Replace("\"", "\"\"")}\"")));
        int.TryParse(Text(db, "SELECT value FROM meta WHERE key = 'version'"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var version);
        long.TryParse(Text(db, "SELECT value FROM meta WHERE key = 'revision'"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var revision);
        string journal;
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode";
            journal = cmd.ExecuteScalar() as string ?? "";
        }
        return new ProjectDatabaseInfo(
            path,
            System.IO.Path.GetDirectoryName(path) ?? "",
            Text(db, "SELECT value FROM meta WHERE key = 'format'") ?? "",
            version,
            Text(db, "SELECT value FROM meta WHERE key = 'project_id'") ?? "",
            Text(db, "SELECT value FROM meta WHERE key = 'name'") ?? "",
            revision,
            Length(path),
            Length(path + "-wal"),
            Scalar(db, "PRAGMA page_size"),
            Scalar(db, "PRAGMA page_count"),
            Scalar(db, "PRAGMA freelist_count"),
            journal,
            tables);
    }

    /// <summary>SQLite's full integrity check. An empty list means the file is sound; otherwise the problems it reported (at most 100).</summary>
    public static IReadOnlyList<string> IntegrityCheck(string path)
    {
        using var db = OpenChecked(path);
        var problems = new List<string>();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check(100)";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var line = r.GetString(0);
            if (!string.Equals(line, "ok", StringComparison.OrdinalIgnoreCase)) problems.Add(line);
        }
        return problems;
    }

    // ---- who has it open ----

    /// <summary>True when some process holds a handle on the file (SQLite opens it shared; asking for no sharing then fails).</summary>
    public static bool HeldByAnyProcess(string path)
    {
        if (!File.Exists(path)) return false;
        // A MynaOps page polls the revision through a short-lived connection; a busy moment is tried again.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return false;
            }
            catch (IOException) { Thread.Sleep(150); }
            catch (UnauthorizedAccessException) { return true; }
        }
        return true;
    }

    /// <summary>True when MynaCAD's shell is running (its process is <c>ODraft.WinUi</c>).</summary>
    public static bool MynaCadRunning()
    {
        try
        {
            var list = Process.GetProcessesByName("ODraft.WinUi");
            foreach (var p in list) p.Dispose();
            return list.Length > 0;
        }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>
    /// Who else has <paramref name="path"/> open. MynaCAD's live-sync marker (<see cref="ActiveProject"/>, <c>active-project.json</c>) names the
    /// project file it has open; it counts while MynaCAD runs. Any other program is seen by its open handle on the file.
    /// <paramref name="marker"/> and <paramref name="mynaCadRunning"/> replace the real marker and process check (tests).
    /// </summary>
    public static OpenElsewhereResult OpenElsewhere(string path, ActiveProjectInfo? marker = null, bool? mynaCadRunning = null, bool useMarker = true)
    {
        path = System.IO.Path.GetFullPath(path);
        if (useMarker)
        {
            marker ??= ActiveProject.Read();
            if (marker is not null && ActiveProject.SamePath(marker.Project, path) && (mynaCadRunning ?? MynaCadRunning()))
            {
                var drawing = string.IsNullOrWhiteSpace(marker.Drawing) ? "" : $" ({System.IO.Path.GetFileName(marker.Drawing)})";
                return new OpenElsewhereResult(true, "MynaCAD" + drawing);
            }
        }
        if (HeldByAnyProcess(path))
            return new OpenElsewhereResult(true, "another program");
        return OpenElsewhereResult.No;
    }

    static void RefuseWhileOpen(string path, string action, Func<string, OpenElsewhereResult>? openCheck)
    {
        var open = (openCheck ?? (p => OpenElsewhere(p)))(path);
        if (open.IsOpen)
            throw new InvalidOperationException($"Cannot {action}: the project file is open in {open.Who}. Close it there first.");
    }

    // ---- compact ----

    /// <summary>
    /// Rewrite the file without its free pages (<c>VACUUM</c>), then fold the write-ahead log into it. Contents, tables and revision are unchanged.
    /// Refused while another program has the file open. Returns the bytes saved.
    /// </summary>
    public static long Compact(string path, Func<string, OpenElsewhereResult>? openCheck = null)
    {
        path = System.IO.Path.GetFullPath(path);
        OpenChecked(path).Dispose();
        RefuseWhileOpen(path, "compact", openCheck);
        long before = Length(path) + Length(path + "-wal");
        using (var db = Connect(path, SqliteOpenMode.ReadWrite))
        {
            db.Open();
            Exec(db, "VACUUM");
            using var cmd = db.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            cmd.ExecuteNonQuery();
        }
        long after = Length(path) + Length(path + "-wal");
        return Math.Max(0, before - after);
    }

    // ---- backups ----

    /// <summary>The backups folder of a project file: <c>&lt;name&gt;.backups</c> beside it.</summary>
    public static string BackupFolder(string path) =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) ?? "", Stem(path) + ".backups");

    static string Stem(string path)
    {
        var s = System.IO.Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrEmpty(s) ? "Drawing" : s;
    }

    /// <summary>The compact UTC stamp used in backup names.</summary>
    public static string Stamp(DateTime utc) => utc.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>The stamp and reason in a backup's file name, or null when the name is not a backup of a file with this stem (as MynaCAD parses it).</summary>
    public static (string Stamp, string Reason)? ParseBackupName(string stem, string name)
    {
        if (!name.StartsWith(stem + " ", StringComparison.Ordinal) || !name.EndsWith(".osp", StringComparison.Ordinal)) return null;
        var rest = name[(stem.Length + 1)..^4];
        var space = rest.IndexOf(' ');
        var stamp = space < 0 ? rest : rest[..space];
        var reason = space < 0 ? "" : rest[(space + 1)..];
        bool ok = stamp.Length == 16 && stamp.EndsWith('Z') && stamp.Count(char.IsAsciiDigit) == 14;
        return ok ? (stamp, reason) : null;
    }

    /// <summary>The backups of the file, newest first. Other files in the folder are ignored, never touched.</summary>
    public static IReadOnlyList<ProjectBackup> ListBackups(string path)
    {
        var folder = BackupFolder(path);
        if (!Directory.Exists(folder)) return Array.Empty<ProjectBackup>();
        var stem = Stem(path);
        var list = new List<(ProjectBackup Backup, DateTime Written)>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.osp"))
        {
            var name = System.IO.Path.GetFileName(file);
            if (ParseBackupName(stem, name) is not { } parsed) continue;
            var info = new FileInfo(file);
            list.Add((new ProjectBackup(file, name, parsed.Stamp, parsed.Reason, info.Length), info.LastWriteTimeUtc));
        }
        return list
            .OrderByDescending(x => x.Backup.Stamp, StringComparer.Ordinal)
            .ThenByDescending(x => x.Written)
            .ThenByDescending(x => x.Backup.Name, StringComparer.Ordinal)
            .Select(x => x.Backup)
            .ToList();
    }

    /// <summary>
    /// Keep a copy of the file as it is now in its backups folder (safe while MynaCAD has it open: <c>VACUUM INTO</c> reads one consistent state).
    /// Nothing is pruned here; MynaCAD prunes by its own count when it next saves. Returns the backup's path.
    /// </summary>
    public static string Backup(string path, string reason = "manual", DateTime? nowUtc = null)
    {
        path = System.IO.Path.GetFullPath(path);
        var folder = BackupFolder(path);
        Directory.CreateDirectory(folder);
        reason = new string(reason.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').Take(32).ToArray());
        var stem = Stem(path);
        var stamp = Stamp(nowUtc ?? DateTime.UtcNow);
        var target = System.IO.Path.Combine(folder, $"{stem} {stamp} {reason}.osp");
        for (int n = 2; File.Exists(target) && n < 100; n++)
            target = System.IO.Path.Combine(folder, $"{stem} {stamp} {reason}-{n}.osp");
        var tmp = System.IO.Path.Combine(folder, $".{stem}.{Environment.ProcessId}.backup-tmp");
        TryDelete(tmp);
        try
        {
            using (var db = OpenChecked(path))
                Exec(db, "VACUUM INTO $to", ("$to", tmp));
            File.Move(tmp, target);
            return target;
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    /// <summary>
    /// Put a backup back. Only a backup listed for this file is accepted; it must be a project file. The file as it is now is first kept as a
    /// <c>before-restore</c> backup, then replaced by a whole copy of the backup (a rename, so it is the old file or the restored one, never a mix).
    /// Refused while another program has the file open. Returns the path of the <c>before-restore</c> backup (or null when the file was missing).
    /// </summary>
    public static string? Restore(string path, string backupPath, Func<string, OpenElsewhereResult>? openCheck = null, DateTime? nowUtc = null)
    {
        path = System.IO.Path.GetFullPath(path);
        backupPath = System.IO.Path.GetFullPath(backupPath);
        if (!ListBackups(path).Any(b => string.Equals(System.IO.Path.GetFullPath(b.Path), backupPath, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"{System.IO.Path.GetFileName(backupPath)} is not a backup of {System.IO.Path.GetFileName(path)}.");
        OpenChecked(backupPath).Dispose();
        if (File.Exists(path))
            RefuseWhileOpen(path, "restore a backup", openCheck);
        var kept = File.Exists(path) ? Backup(path, "before-restore", nowUtc) : null;
        var tmp = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, $".{Stem(path)}.{Environment.ProcessId}.restore-tmp");
        TryDelete(tmp);
        try
        {
            using (var db = OpenChecked(backupPath))
                Exec(db, "VACUUM INTO $to", ("$to", tmp));
            if (File.Exists(path))
            {
                // The write-ahead log belongs to the file being replaced: fold it in, so nothing of it can be applied to the restored file.
                using var db = Connect(path, SqliteOpenMode.ReadWrite);
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                cmd.ExecuteNonQuery();
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
        TryDelete(path + "-wal");
        TryDelete(path + "-shm");
        return kept;
    }

    static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
