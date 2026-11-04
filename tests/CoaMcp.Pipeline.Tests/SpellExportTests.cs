using System.Text.Json;
using System.Text.RegularExpressions;
using CoaMcp.Core;
using CoaMcp.Pipeline.Spells;

namespace CoaMcp.Pipeline.Tests;

/// <summary>Spellwork loads DBCs into process-wide statics, so every test that loads them shares this collection and one load.</summary>
[CollectionDefinition(Name)]
public sealed class SpellworkCollection : ICollectionFixture<SpellFixture>
{
    public const string Name = "Spellwork statics";
}

public sealed class SpellFixture : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "coamcp-spells-" + Guid.NewGuid().ToString("N"));
    public string DbcDir { get; }
    public string LayerPath { get; }
    public SpellSource Source { get; }
    public SpellsImportResult Result { get; }
    public LayerReader Reader { get; }

    public SpellFixture()
    {
        DbcDir = FakeDbc.Create(Path.Combine(_dir, "dbc"));
        LayerPath = Path.Combine(_dir, "spells.sqlite");
        Source = SpellSource.Open(DbcDir, new DateOnly(2026, 10, 24));
        Result = SpellsImport.Run(Source, LayerPath);
        Reader = new LayerReader(LayerPath);
    }

    public void Dispose()
    {
        Reader.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}

[Collection(SpellworkCollection.Name)]
public sealed class SpellExportTests(SpellFixture f)
{
    private static List<string> Headings(string md) =>
        Regex.Matches(md, @"^## (.+)$", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();

    private static string Section(string md, string name)
    {
        var m = Regex.Match(md, @"^## " + name + @"\n(.*?)(?=^## |\z)", RegexOptions.Multiline | RegexOptions.Singleline);
        Assert.True(m.Success, "missing section " + name);
        return m.Groups[1].Value;
    }

    [Fact]
    public void SourceDate_ComesFromTheWinnersList_NotTheCopiedFilesTime()
    {
        var dir = Path.Combine(Path.GetTempPath(), "coamcp-winners-" + Guid.NewGuid().ToString("N"));
        FakeDbc.Create(dir);
        try
        {
            CoaMcp.Pipeline.Dbc.DbcWinnerList.Write(dir, new Dictionary<string, CoaMcp.Pipeline.Dbc.DbcWinnerFile>
            {
                ["Spell.dbc"] = new("patch-T.MPQ", "project"),
            }, "2026-10-24");
            Assert.Equal(new DateOnly(2026, 10, 24), SpellSource.SourceDateOf(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("patch-T.MPQ", "project", "CoA client DBC (via Spellwork), per CLIENT-RELEASE.json, client revision 8, Spell.dbc from patch-T.MPQ (project-built archive)")]
    [InlineData("patch-M.MPQ", "unknown", "CoA client DBC (via Spellwork), per CLIENT-RELEASE.json, client revision 8, Spell.dbc from patch-M.MPQ (origin unknown)")]
    [InlineData("patch-T.MPQ", "ascension", "CoA client DBC (via Spellwork), per CLIENT-RELEASE.json, client revision 8, Spell.dbc from patch-T.MPQ (Ascension's archive per project records; this copy may lag live)")]
    public void Pages_FromAWinnersFolder_SayWhichArchiveWonSpellDbc(string archive, string origin, string expected)
    {
        var dir = Path.Combine(Path.GetTempPath(), "coamcp-winners-" + Guid.NewGuid().ToString("N"));
        FakeDbc.Create(dir);
        var layer = Path.Combine(dir, "spells.sqlite");
        try
        {
            CoaMcp.Pipeline.Dbc.DbcWinnerList.Write(dir, new Dictionary<string, CoaMcp.Pipeline.Dbc.DbcWinnerFile>
            {
                ["Spell.dbc"] = new(archive, origin),
            }, "2026-10-24", new ClientRelease(8, "rel-8", null));

            SpellsImport.Run(SpellSource.OpenWinners(dir), layer);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            using var r = new LayerReader(layer);
            var page = r.Get("spell:133")!;
            Assert.Equal(expected, page.Source);
            Assert.DoesNotContain("Source:", page.BodyMd);   // the header and search hit carry it, and every spell's body would match its words
            Assert.DoesNotContain(archive, page.BodyMd);
            Assert.Contains("Raw client data, not proven for any server.", page.BodyMd);
            foreach (var word in new[] { "archive", "records", "lag", "copy", "ascension" })
                Assert.Equal(0L, r.Query("SELECT COUNT(*) AS n FROM entities_fts WHERE entities_fts MATCH $0", word).Single()["n"]);
            Assert.Equal(8, r.Meta.Client!.Revision);
            Assert.Equal("spells", CoaMcp.Pipeline.Publish.PublishGuard.Check(layer).LayerName);
            Assert.True(CoaMcp.Pipeline.Publish.PublishGuard.Check(layer).Ok);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("patch-T.MPQ", true)]
    [InlineData("patch-M.MPQ", false)]
    public void Pages_SayWhenSpellDbcsWinnerMismatches_AndOnlyThen(string winner, bool expected)
    {
        var dir = Path.Combine(Path.GetTempPath(), "coamcp-winners-" + Guid.NewGuid().ToString("N"));
        FakeDbc.Create(dir);
        var layer = Path.Combine(dir, "spells.sqlite");
        const string Line = "patch-T.MPQ on disk (5.0 MB) doesn't match the release file's entry (4.0 MB), so it may be newer than revision 8.";
        try
        {
            CoaMcp.Pipeline.Dbc.DbcWinnerList.Write(dir, new Dictionary<string, CoaMcp.Pipeline.Dbc.DbcWinnerFile>
            {
                ["Spell.dbc"] = new(winner, "project"),
            }, "2026-10-24", new ClientRelease(8, "rel-8", "n", new[]
            {
                new ClientArchiveMismatch("patch-T.MPQ", 4000000, 5000000, null),
                new ClientArchiveMismatch("patch-Other.MPQ", 1, 2, null),
            }));

            SpellsImport.Run(SpellSource.OpenWinners(dir), layer);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            using var r = new LayerReader(layer);
            var body = r.Get("spell:133")!.BodyMd;
            Assert.Equal(expected, body.Contains(Line, StringComparison.Ordinal));
            Assert.DoesNotContain("patch-Other.MPQ", body);    // only the archive this layer's pages come from
            Assert.Equal(expected ? 1 : 0, r.Meta.Client!.Mismatches?.Count ?? 0);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void SourceDate_WinnersListWithoutAClientDate_IsRefused()
    {
        var dir = Path.Combine(Path.GetTempPath(), "coamcp-winners-" + Guid.NewGuid().ToString("N"));
        FakeDbc.Create(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, CoaMcp.Pipeline.Dbc.DbcWinnerList.FileName), """{"files":{"Spell.dbc":{"archive":"patch-T.MPQ","origin":"unknown","date":"2026-10-03"}}}""");
            var ex = Assert.Throws<InvalidOperationException>(() => SpellSource.SourceDateOf(dir));
            Assert.Contains("client date", ex.Message);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void SourceDate_FolderWithoutAWinnersList_IsRefused()
    {
        var dir = Path.Combine(Path.GetTempPath(), "coamcp-winners-" + Guid.NewGuid().ToString("N"));
        FakeDbc.Create(dir);
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => SpellSource.SourceDateOf(dir));
            Assert.Contains("winners", ex.Message);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Spell_HasSections()
    {
        var e = f.Reader.Get("spell:133");

        Assert.NotNull(e);
        Assert.Equal(new[] { "summary", "effects", "triggers", "visuals" }, Headings(e.BodyMd));
        Assert.Equal("Fireball", e.Title);
        Assert.Equal("spell", e.Type);
        Assert.Contains("Rank 1", e.BodyMd);
        Assert.Contains("SPELL_EFFECT_SCHOOL_DAMAGE", e.BodyMd);
    }

    [Fact]
    public async Task Spell_MatchesGetSpellOutput()
    {
        var truth = JsonDocument.Parse(await f.Source.Spells.GetSpell(133)).RootElement.GetProperty("result");
        var e = f.Reader.Get("spell:133")!;
        var data = JsonDocument.Parse(e.DataJson!).RootElement;

        Assert.Equal(truth.GetProperty("name").GetString(), data.GetProperty("name").GetString());
        var tEff = truth.GetProperty("effects")[0];
        var dEff = data.GetProperty("effects")[0];
        Assert.Equal(tEff.GetProperty("basePoints").GetInt32(), dEff.GetProperty("basePoints").GetInt32());
        Assert.Equal(tEff.GetProperty("effect").GetProperty("raw").GetInt32(), dEff.GetProperty("effect").GetInt32());
        Assert.Equal(truth.GetProperty("schoolMask").GetProperty("raw").GetInt32(), data.GetProperty("schoolMask").GetInt32());
        Assert.Equal(9, dEff.GetProperty("dieSides").GetInt32());
        Assert.Contains($"{tEff.GetProperty("basePointsMin").GetInt32()} to {tEff.GetProperty("basePointsMax").GetInt32()}", e.BodyMd);
    }

    [Fact]
    public void Spell_SummaryKeepsMaskPositionsAndSkipsEmptyAttributeWords()
    {
        var summary = Section(f.Reader.Get("spell:133")!.BodyMd, "summary");

        Assert.Contains("spellClassMask: [0, 8, 0]", summary);
        Assert.Contains("word 0: 65536", summary);
        Assert.Contains("word 2: 1024", summary);
        Assert.DoesNotContain("word 1:", summary);
        Assert.DoesNotContain("word=", summary);
    }

    [Fact]
    public void Spell_TrustIsClient()
    {
        var e = f.Reader.Get("spell:133")!;

        Assert.Equal(Trust.Client, e.Trust);
        Assert.Equal("CoA client DBC (via Spellwork)", e.Source);
    }

    [Fact]
    public void Spell_RealmIsClientLabeled()
    {
        var e = f.Reader.Get("spell:133")!;

        Assert.Equal(new[] { "coa-client" }, e.Realm.Realms);
        Assert.False(e.Realm.Proven);
        Assert.Equal("the CoA client", e.Realm.Label());
    }

    [Fact]
    public void Spell_LinksToTriggeredSpells()
    {
        var e = f.Reader.Get("spell:1569630")!;

        Assert.Contains(e.Links, l => l.ToId == "spell:133" && l.Kind == "triggers");
        Assert.Contains("133", Section(e.BodyMd, "triggers"));
        Assert.Empty(f.Reader.Get("spell:133")!.Links);
    }

    [Fact]
    public void Spell_VisualsSectionShowsKitsAndEffects()
    {
        Assert.Contains("Fire Cast Hand", Section(f.Reader.Get("spell:133")!.BodyMd, "visuals"));
        Assert.Contains("Fire_Cast_Hand.mdx", Section(f.Reader.Get("spell:133")!.BodyMd, "visuals"));
        Assert.Contains("none", Section(f.Reader.Get("spell:200")!.BodyMd, "visuals"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Export_OneEntityPerSpell()
    {
        Assert.Equal(3, f.Result.Spells);
        Assert.Equal(3L, f.Reader.Query("SELECT COUNT(*) AS n FROM entities WHERE type='spell'")[0]["n"]);
        Assert.Equal("spells", f.Reader.Meta.Name);
    }

    [Fact]
    public async Task Export_DoesNotContainDbOverrides()
    {
        var names = new[] { "SPELLWORK_MYSQL_HOST", "SPELLWORK_MYSQL_USER", "SPELLWORK_MYSQL_WORLD_DB", "SPELLWORK_MYSQL_PASS", "SPELLWORK_MYSQL_PORT" };
        var saved = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var n in names) Environment.SetEnvironmentVariable(n, "poison");
            var source = SpellSource.Open(f.DbcDir, new DateOnly(2026, 10, 24));

            Assert.False(source.Config.MySqlConfigured);
            Assert.Null(source.Config.MySqlHost);
            var json = await source.Spells.GetSpell(133);
            Assert.DoesNotContain("worldDatabaseError", json);
            // opening the source must leave the caller's variables as they were
            Assert.All(names, n => Assert.Equal("poison", Environment.GetEnvironmentVariable(n)));

            var path = Path.Combine(Path.GetDirectoryName(f.LayerPath)!, "poisoned.sqlite");
            SpellsImport.Run(source, path);
            using var r = new LayerReader(path);
            var e = r.Get("spell:133")!;
            Assert.DoesNotContain("poison", e.BodyMd + e.DataJson);
            Assert.DoesNotContain("itemsNote", e.DataJson);
            Assert.DoesNotContain("worldDatabaseError", e.DataJson);
        }
        finally
        {
            foreach (var (n, v) in saved) Environment.SetEnvironmentVariable(n, v);
        }
    }

    [Fact]
    public void Enums_ExportFlagKinds()
    {
        var school = f.Reader.Query("SELECT value FROM enums WHERE name='SpellSchoolMask' AND label='SPELL_SCHOOL_MASK_FIRE'");
        Assert.Single(school);
        Assert.Equal("4", Convert.ToString(school[0]["value"]));

        var kinds = f.Reader.Query("SELECT enum_name FROM flag_kinds WHERE kind='SpellSchoolMask'");
        Assert.Single(kinds);
        Assert.Equal("SpellSchoolMask", kinds[0]["enum_name"]);
        Assert.Empty(f.Reader.Query("SELECT 1 FROM flag_kinds WHERE kind='SpellEffects'"));
        Assert.True(f.Result.FlagKinds > 5);
    }

    [Fact]
    public void Enums_UlongBackedEnumKeepsExactValue()
    {
        var all = f.Reader.Query("SELECT value_text FROM enums WHERE name='ShapeshiftFormMask' AND label='FORM_ALL'");
        Assert.Single(all);
        Assert.Equal("4294967295", all[0]["value_text"]);
    }

    [Fact]
    public void Scaling_InputsExportedPerEffect()
    {
        var rows = f.Reader.Query("SELECT * FROM spell_scaling_inputs WHERE spell_id=133 ORDER BY effect_index");
        Assert.Equal(2, rows.Count); // empty effect slots have no row, as in Spellwork
        Assert.Equal(2L, rows[0]["effect"]);
        Assert.Equal(14L, rows[0]["base_points"]);
        Assert.Equal(9L, rows[0]["die_sides"]);
        Assert.Equal(1.5, (double)rows[0]["real_points_per_level"]!, 5);
        Assert.Equal(1L, rows[0]["spell_level"]);
        Assert.Equal(1L, rows[0]["base_level"]);
        Assert.Equal(5L, rows[0]["max_level"]);
    }

    [Fact]
    public void Scaling_StatTablesExported()
    {
        Assert.Equal(3L, f.Reader.Query("SELECT COUNT(*) AS n FROM ScalingStatValues")[0]["n"]);
        Assert.Equal(21L, f.Reader.Query("SELECT c16 AS v FROM ScalingStatValues WHERE c0=3")[0]["v"]);
        Assert.Equal(1L, f.Reader.Query("SELECT COUNT(*) AS n FROM ScalingStatDistribution")[0]["n"]);
    }

    [Fact]
    public async Task Scaling_GoldenMatchesTool()
    {
        var rows = f.Reader.Query("SELECT * FROM golden_scaling WHERE spell_id=133 ORDER BY level, effect_index");
        Assert.Equal(new long[] { 1, 40, 80 }, rows.Select(r => (long)r["level"]!).Distinct().ToArray());

        foreach (var level in new uint[] { 1, 40, 80 })
        {
            var tool = JsonDocument.Parse(await f.Source.Decoder.CalculateSpellScaling(133, level, 0))
                .RootElement.GetProperty("result").GetProperty("effects");
            foreach (var eff in tool.EnumerateArray())
            {
                var idx = eff.GetProperty("index").GetInt32();
                var row = rows.Single(r => (long)r["level"]! == level && (long)r["effect_index"]! == idx);
                Assert.Equal(eff.GetProperty("base").GetDouble(), (double)row["base"]!, 3);
                Assert.Equal(eff.GetProperty("min").GetDouble(), (double)row["min"]!, 3);
                Assert.Equal(eff.GetProperty("max").GetDouble(), (double)row["max"]!, 3);
            }
        }
    }
}
