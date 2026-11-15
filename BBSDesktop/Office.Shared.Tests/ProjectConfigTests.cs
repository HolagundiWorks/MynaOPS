// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Office.Shared.Tests;

/// <summary>
/// The project configuration contract (<see cref="ProjectConfig"/>). <c>Fixtures/config</c> is committed identically in MynaCAD
/// (<c>crates/odp/tests/fixtures/config</c>) and read there by <c>aad_odp::config</c>; <c>config-rust.osp</c> was written by the Rust writer and
/// <c>config-csharp.osp</c> by <see cref="ProjectConfig.Write"/>, and each side reads the other's file.
/// </summary>
public class ProjectConfigTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "cfg-" + Guid.NewGuid().ToString("N"));

    public ProjectConfigTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    static string Fixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures", "config");

    static JsonElement Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.Clone();
    }

    /// <summary>The key texts of a fixture: an object is the key's JSON text, a string is the raw text.</summary>
    static SortedDictionary<string, string> Texts(JsonElement doc)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in doc.GetProperty("keys").EnumerateObject())
            map[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText();
        return map;
    }

    static IEnumerable<(string Name, JsonElement Doc)> AllFixtures()
    {
        foreach (var dir in new[] { "valid", "newer", "hostile" })
            foreach (var f in Directory.GetFiles(Path.Combine(Fixtures, dir), "*.json").OrderBy(x => x, StringComparer.Ordinal))
                yield return ($"{dir}/{Path.GetFileName(f)}", Load(f));
    }

    static ProjectConfig Full() => ProjectConfig.FromTexts(Texts(Load(Path.Combine(Fixtures, "valid", "full.json")))).Config;

    string Copy(string fixture)
    {
        var path = Path.Combine(_dir, fixture);
        File.Copy(Path.Combine(Fixtures, fixture), path, true);
        return path;
    }

    [Fact]
    public void The_shared_fixtures_read_as_they_say()
    {
        var all = AllFixtures().ToList();
        Assert.True(all.Count >= 30, $"the fixture set is missing files: {all.Count}");
        foreach (var (name, doc) in all)
        {
            var expect = doc.GetProperty("expect").GetString();
            switch (expect)
            {
                case "ok":
                    var r = ProjectConfig.FromTexts(Texts(doc));
                    Assert.True((r.Config.Levels?.Items.Count ?? 0) == doc.GetProperty("levels").GetInt32(), $"{name}: levels");
                    Assert.True((r.Config.Tabs?.Items.Count ?? 0) == doc.GetProperty("tabs").GetInt32(), $"{name}: tabs");
                    Assert.True(r.Config.Revision == doc.GetProperty("rev").GetUInt64(), $"{name}: rev");
                    Assert.True(r.Notes.Count == doc.GetProperty("notes").GetInt32(), $"{name}: notes {string.Join("|", r.Notes)}");
                    break;
                case "too_new":
                    Assert.Throws<ConfigTooNewException>(() => ProjectConfig.FromTexts(Texts(doc)));
                    break;
                case "error":
                    var key = doc.GetProperty("error_key").GetString()!;
                    var e = Assert.ThrowsAny<ConfigException>(() => ProjectConfig.FromTexts(Texts(doc)));
                    Assert.True(e.Key.StartsWith(key, StringComparison.Ordinal), $"{name}: the error names {e.Key}, expected {key}");
                    break;
                default:
                    Assert.Fail($"{name}: unknown expectation {expect}");
                    break;
            }
        }
    }

    [Fact]
    public void The_configuration_round_trips_through_its_json_forms()
    {
        var c = Full();
        var again = ProjectConfig.FromJson(c.ToJson()).Config;
        Assert.Equal(c.ToTexts(), again.ToTexts());
        Assert.Equal(c.ToTexts(), ProjectConfig.FromTexts(c.ToTexts()).Config.ToTexts());
        c.Validate();
        Assert.Equal(3, c.Levels!.Items.Count);
        Assert.Equal(-3000, c.Levels.Items[0].ElevationMm);
        Assert.Equal("Hospet House", c.Project!.Title);
        Assert.Equal(4UL, c.Revision);
    }

    [Fact]
    public void One_write_is_one_revision_with_sync_last_and_an_identical_second_write_is_none()
    {
        var path = Path.Combine(_dir, "p.osp");
        using var odp = OdpFile.Create(path, "House");
        Assert.False(ProjectConfig.Read(odp).Configured);
        var c = Full() with { RevPart = null };
        var w = ProjectConfig.Write(odp, c);
        Assert.Equal((1L, 1UL), (w.Revision, w.Rev));
        Assert.Contains("rev", w.Changed);
        Assert.Contains("levels", w.Changed);
        Assert.Equal(1, odp.Revision());
        var changes = odp.ChangesSince(0);
        Assert.All(changes, ch => { Assert.Equal(1, ch.Revision); Assert.Equal("meta", ch.Table); });
        Assert.Contains(changes, ch => ch.Key == "config.rev");
        var last = JsonNode.Parse(odp.MetaText("sync.last")!)!;
        Assert.Equal("mynaops", last["app"]!.GetValue<string>());
        Assert.Equal(1, last["revision"]!.GetValue<long>());

        var loaded = ProjectConfig.Read(odp);
        Assert.True(loaded.Configured);
        Assert.Equal(c.Levels!.Items.Count, loaded.Read.Config.Levels!.Items.Count);
        Assert.Equal(1UL, loaded.Read.Config.Revision);
        Assert.Equal(1, loaded.Revision);

        var again = ProjectConfig.Write(odp, c);
        Assert.Equal((1L, 1UL, 0), (again.Revision, again.Rev, again.Changed.Count));

        var c2 = c with { Units = new ConfigUnits { DrawingUnit = "cm", Locked = true } };
        var w2 = ProjectConfig.Write(odp, c2);
        Assert.Equal((2L, 2UL), (w2.Revision, w2.Rev));
        Assert.Equal(new[] { "units", "rev" }, w2.Changed);
        Assert.Equal("cm", ProjectConfig.Probe(path).Read.Config.Units!.DrawingUnit);
    }

    [Fact]
    public void A_write_never_touches_the_keys_of_the_file_itself_or_the_other_tables()
    {
        var path = Path.Combine(_dir, "p.osp");
        using var odp = OdpFile.Create(path, "House");
        odp.SetProjectDoc("{\"project\":{\"name\":\"x\"}}");
        var before = odp.ProjectDoc();
        ProjectConfig.Write(odp, Full());
        Assert.Equal(before, odp.ProjectDoc());
        Assert.Equal("House", odp.Meta().Name);
        Assert.Throws<InvalidDataException>(() => odp.WriteMeta(new[] { ("revision", "99") }, "mynaops"));
        Assert.Throws<InvalidDataException>(() => odp.WriteMeta(new[] { ("name", "x") }, "mynaops"));
    }

    [Fact]
    public void A_too_new_schema_or_an_oversize_value_in_a_file_is_an_error_not_a_crash()
    {
        var path = Path.Combine(_dir, "p.osp");
        using (var odp = OdpFile.Create(path, "House"))
            odp.WriteMeta(new[] { ("config.schema", "{\"v\":1,\"min_reader\":7}") }, "mynaops");
        Assert.Throws<ConfigTooNewException>(() => ProjectConfig.Probe(path));
        using (var odp = OdpFile.Open(path))
        {
            Assert.Throws<ConfigTooNewException>(() => ProjectConfig.Read(odp));
            var huge = "{\"v\":1,\"drawing_unit\":\"mm\",\"pad\":\"" + new string('x', ProjectConfig.MaxValueBytes + 10) + "\"}";
            odp.WriteMeta(new[] { ("config.schema", "{\"v\":1,\"min_reader\":1}"), ("config.units", huge) }, "mynaops");
            Assert.Throws<ConfigTooLargeException>(() => ProjectConfig.Read(odp));
        }
        Assert.Throws<ConfigTooLargeException>(() => ProjectConfig.Probe(path));
        Assert.Throws<ConfigException>(() => ProjectConfig.Probe(Path.Combine(_dir, "missing.osp")));
        var junk = Path.Combine(_dir, "junk.osp");
        File.WriteAllText(junk, "this is not sqlite");
        Assert.Throws<ConfigException>(() => ProjectConfig.Probe(junk));
    }

    [Fact]
    public void The_files_written_by_either_language_read_the_same()
    {
        var expect = Full().ToTexts();
        foreach (var name in new[] { "config-rust.osp", "config-csharp.osp" })
        {
            var l = ProjectConfig.Probe(Copy(name));
            Assert.True(l.Configured, name);
            Assert.True(l.Read.Config.Revision == 1, name);
            Assert.True(l.Revision == 1, $"{name}: one revision for one write");
            var got = l.Read.Config.ToTexts();
            foreach (var part in ProjectConfig.Parts.Where(p => p != "rev"))
                Assert.True(expect[part] == got[part], $"{name}: {part}");
            if (name == "config-rust.osp")
            {
                var a = l.Applied!;
                Assert.Equal((1UL, "partial", true), (a.Rev, a.Result, a.HasContent));
                Assert.Equal("orphans", a.Items.Single().Status);
            }
        }
    }

    [Fact]
    public void Rewriting_the_same_configuration_over_a_file_the_other_language_wrote_changes_nothing()
    {
        var c = Full() with { RevPart = null };
        foreach (var name in new[] { "config-rust.osp", "config-csharp.osp" })
        {
            using var odp = OdpFile.Open(Copy(name));
            var w = ProjectConfig.Write(odp, c);
            Assert.True(w.Changed.Count == 0, $"{name}: {string.Join(",", w.Changed)}");
            Assert.Equal((1L, 1UL), (w.Revision, w.Rev));
        }
    }

    [Fact]
    public void The_report_keys_parse_and_a_damaged_one_is_left_out_with_a_note()
    {
        var a = ProjectConfig.ParseApplied("{\"v\":1,\"rev\":3,\"time\":\"2026-11-15T10:00:00Z\",\"result\":\"ok\",\"has_content\":true,\"items\":[{\"key\":\"levels\",\"status\":\"ok\"}]}")!;
        Assert.Equal(3UL, a.Rev);
        Assert.Null(ProjectConfig.ParseApplied("{\"v\":4}"));
        Assert.Throws<ConfigException>(() => ProjectConfig.ParseApplied("{\"v\":1,\"rev\":3,\"result\":\"fine\"}"));
        var p = ProjectConfig.ParseProposal("{\"v\":1,\"has_content\":true,\"drawing_unit\":\"mm\",\"levels\":[{\"cid\":3,\"name\":\"Ground\",\"elevation_mm\":0,\"height_mm\":3000}],\"layer_sets\":[\"standard\"]}")!;
        Assert.Equal(3u, p.Levels.Single().Cid);

        var path = Path.Combine(_dir, "p.osp");
        using var odp = OdpFile.Create(path, "House");
        odp.WriteMeta(new[] { ("config.applied.cad", "{not json"), ("config.proposal", "{\"v\":3}") }, "mynaops");
        var l = ProjectConfig.Read(odp);
        Assert.True(l.Applied is null && l.Proposal is null && l.Read.Notes.Count == 2, string.Join("|", l.Read.Notes));
    }

    sealed class Lcg
    {
        ulong _s;
        public Lcg(ulong seed) => _s = seed;
        public ulong Next() { _s = unchecked(_s * 6364136223846793005UL + 1442695040888963407UL); return _s >> 33; }
    }

    /// <summary>Every result is a configuration or a <see cref="ConfigException"/>: nothing else escapes.</summary>
    static void Tolerated(Action a)
    {
        try { a(); } catch (ConfigException) { }
    }

    [Fact]
    public void Hostile_config_never_throws_anything_but_a_config_exception()
    {
        var big = new string('x', ProjectConfig.MaxValueBytes + 1);
        Assert.Throws<ConfigTooLargeException>(() => ProjectConfig.FromTexts(new Dictionary<string, string> { ["units"] = $"{{\"v\":1,\"drawing_unit\":\"{big}\"}}" }));

        var items = string.Join(",", Enumerable.Range(0, 5000).Select(i => $"{{\"cid\":{i},\"name\":\"L{i}\",\"elevation_mm\":0,\"height_mm\":3000}}"));
        Assert.ThrowsAny<ConfigException>(() => ProjectConfig.FromTexts(new Dictionary<string, string> { ["levels"] = $"{{\"v\":1,\"next_cid\":6000,\"items\":[{items}]}}" }));

        var deep = new string('[', 100_000) + new string(']', 100_000);
        foreach (var name in ProjectConfig.Parts)
        {
            Assert.ThrowsAny<ConfigException>(() => ProjectConfig.FromTexts(new Dictionary<string, string> { [name] = deep }));
            Assert.ThrowsAny<ConfigException>(() => ProjectConfig.FromTexts(new Dictionary<string, string> { [name] = $"{{\"v\":1,\"x\":{deep}}}" }));
        }
        foreach (var n in new[] { "1e999", "-1e999", "18446744073709551616", "-9223372036854775809", "NaN", "Infinity", "null", "true", "\"\"", "[]", "{}" })
            foreach (var name in ProjectConfig.Parts)
            {
                var text = $"{{\"v\":1,\"next_cid\":{n},\"cid\":{n},\"rev\":{n},\"min_reader\":{n},\"elevation_mm\":{n},\"width\":{n},\"items\":[{{\"cid\":{n},\"tid\":{n},\"name\":{n},\"elevation_mm\":{n},\"height_mm\":{n}}}]}}";
                Tolerated(() => ProjectConfig.FromTexts(new Dictionary<string, string> { [name] = text }));
            }

        // Single-byte mutations of the full fixture.
        var baseTexts = Texts(Load(Path.Combine(Fixtures, "valid", "full.json")));
        var rng = new Lcg(7);
        var alphabet = " {}[]\":,0123456789-.eE\\ntuf";
        for (var i = 0; i < 3000; i++)
        {
            var p = new SortedDictionary<string, string>(baseTexts, StringComparer.Ordinal);
            var names = p.Keys.ToList();
            var name = names[(int)(rng.Next() % (ulong)names.Count)];
            var chars = p[name].ToCharArray();
            for (ulong k = 0; k < 1 + rng.Next() % 3; k++)
                chars[(int)(rng.Next() % (ulong)chars.Length)] = alphabet[(int)(rng.Next() % (ulong)alphabet.Length)];
            p[name] = new string(chars);
            Tolerated(() => ProjectConfig.FromTexts(p));
        }
        foreach (var t in new[] { "", "{", "{\"v\":1}", deep, "{\"v\":1,\"result\":\"ok\",\"items\":5}" })
        {
            Tolerated(() => ProjectConfig.ParseApplied(t));
            Tolerated(() => ProjectConfig.ParseProposal(t));
        }
        Tolerated(() => ProjectConfig.FromJson(JsonNode.Parse("[]")!));
    }

    [Fact]
    public void Hostile_values_in_a_file_never_throw_anything_but_a_config_exception()
    {
        var path = Path.Combine(_dir, "p.osp");
        using var odp = OdpFile.Create(path, "House");
        foreach (var (k, v) in new[] { ("config.schema", "{\"v\":1}"), ("config.levels", "\u0001\u0002"), ("config.tabs", "{\"v\":1,\"items\":[null]}"), ("config.rev", "[]") })
        {
            odp.WriteMeta(new[] { (k, v) }, "mynaops");
            Tolerated(() => ProjectConfig.Read(odp));
            Tolerated(() => ProjectConfig.Probe(path));
        }
    }

    /// <summary>Regenerates <c>config-csharp.osp</c> when <c>CONFIG_FIXTURE_OUT</c> names the folder to write it to (the source Fixtures/config folder).</summary>
    [Fact]
    public void Regenerate_the_csharp_fixture_when_asked()
    {
        var outDir = Environment.GetEnvironmentVariable("CONFIG_FIXTURE_OUT");
        if (string.IsNullOrEmpty(outDir)) return;
        var path = Path.Combine(outDir, "config-csharp.osp");
        if (File.Exists(path)) File.Delete(path);
        using (var odp = OdpFile.Create(path, "Config fixture"))
        {
            ProjectConfig.Write(odp, Full() with { RevPart = null });
        }
    }
}
