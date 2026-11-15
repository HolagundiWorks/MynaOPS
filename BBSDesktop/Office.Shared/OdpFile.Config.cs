// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Office.Shared;

// The meta access the project configuration needs (ProjectConfig). Kept in its own file so the project file's core stays as it is.
public sealed partial class OdpFile
{
    /// <summary>The keys that describe the file itself: never written through <see cref="WriteMeta"/>.</summary>
    static readonly string[] CoreMeta = { "format", "version", "project_id", "name", "created", "revision" };

    /// <summary>The size in bytes of a meta value without reading it; <c>null</c> when the key is absent. A reader of a value whose size a file states checks it first.</summary>
    public long? MetaLength(string key)
    {
        using var cmd = Command("SELECT length(CAST(value AS BLOB)) FROM meta WHERE key = $k", ("$k", key));
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? null : Convert.ToInt64(v, CultureInfo.InvariantCulture);
    }

    /// <summary>A meta value by key (<c>null</c> when the key is absent).</summary>
    public string? MetaText(string key) => MetaValue(key);

    /// <summary>
    /// Write shared meta values (the <c>config.*</c> keys MynaOps owns) as <b>one</b> revision: every value, one <c>change</c> row per key, and
    /// <c>sync.last</c> naming <paramref name="app"/>, in one transaction. Returns the new revision. The keys that describe the file itself are refused.
    /// </summary>
    public long WriteMeta(IReadOnlyList<(string Key, string Value)> pairs, string app)
    {
        foreach (var (k, _) in pairs)
            if (Array.IndexOf(CoreMeta, k) >= 0) throw new InvalidDataException($"`{k}` is not a meta key to write.");
        return Write(rev =>
        {
            foreach (var (k, v) in pairs)
            {
                Exec("INSERT INTO meta (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = $v", ("$k", k), ("$v", v));
                Log(rev, "meta", k, "put");
            }
            var last = new JsonObject { ["app"] = app, ["revision"] = rev, ["time"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) }.ToJsonString();
            Exec("INSERT INTO meta (key, value) VALUES ('sync.last', $v) ON CONFLICT(key) DO UPDATE SET value = $v", ("$v", last));
        });
    }
}
