// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using Microsoft.Data.Sqlite;

namespace Office.Shared;

// Local meta keys: a MynaOps-side record that is not shared state. Written without a revision, a change row or sync.last, so no
// live-sync detector sees it (like MynaCAD's native.* keys). Only the session.* keys are allowed here.
public sealed partial class OdpFile
{
    const string LocalPrefix = "session.";

    static void CheckLocalKey(string key)
    {
        if (!key.StartsWith(LocalPrefix, StringComparison.Ordinal) || key.Length > 64 || key.Any(char.IsControl))
            throw new InvalidDataException($"`{key}` is not a local meta key (only `{LocalPrefix}*` is).");
    }

    /// <summary>The local meta value of <paramref name="key"/> (<c>null</c> when absent or longer than <paramref name="maxBytes"/>).</summary>
    public string? LocalMeta(string key, int maxBytes = 4096)
    {
        CheckLocalKey(key);
        return MetaLength(key) is { } n && n <= maxBytes ? MetaText(key) : null;
    }

    /// <summary>Write a local meta value: no revision, no change row.</summary>
    public void PutLocalMeta(string key, string value)
    {
        CheckLocalKey(key);
        Exec("INSERT INTO meta (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = $v", ("$k", key), ("$v", value));
    }

    /// <summary>Remove a local meta key (no error when it is absent).</summary>
    public void DeleteLocalMeta(string key)
    {
        CheckLocalKey(key);
        Exec("DELETE FROM meta WHERE key = $k", ("$k", key));
    }

    /// <summary>
    /// Fold the write-ahead log into the file and truncate it (<c>PRAGMA wal_checkpoint(TRUNCATE)</c>). Returns false when another connection kept it
    /// from finishing (a reader is open); the file is still sound.
    /// </summary>
    public bool Checkpoint()
    {
        using var cmd = Command("PRAGMA wal_checkpoint(TRUNCATE)");
        using var r = cmd.ExecuteReader();
        return r.Read() && r.GetInt64(0) == 0;
    }
}
