using System.Diagnostics;
using System.Globalization;
using CoaMcp.Core;
using ModelContextProtocol.Client;

namespace CoaMcp.Server.Tests;

/// <summary>A dbc layer shaped like the pipeline's output, built with the builder the pipeline uses.</summary>
public static class TestDbc
{
    private static readonly RealmSet Realm = new(new[] { "coa-client" }, false);

    private static DbcColumn C(int i, string name, string type, bool guessed = false, bool pk = false, bool indexed = false, string? note = null) =>
        new(i, name, type, guessed, pk || indexed, pk, note);

    public static void Build(string path)
    {
        using var w = new LayerWriter(path, "dbc", "4");
        var b = new DbcLayerBuilder(w, new ClientRelease(8, "test-release", "the release file's patch-B entry is off", new[]
        {
            new ClientArchiveMismatch("patch-M.MPQ", 8987639, 228738210, null),
            new ClientArchiveMismatch("enUS/patch-enUS-3.MPQ", 1000000, 2000000, 2000000),
        }));

        var spell = new[]
        {
            C(0, "ID", "int", pk: true), C(1, "Name_0", "string", indexed: true), C(2, "Speed", "float"), C(3, "Effect", "int"), C(4, "Unused", "int"),
        };
        using (var ins = b.BeginTable("Spell", spell))
        {
            ins.Add(new object?[] { 133L, "Fireball", 24.0, 2L, 0L });
            ins.Add(new object?[] { 134L, "Fire Shield", 0.0, 6L, 0L });
            ins.Add(new object?[] { 135L, "Frostbolt", 28.0, 2L, 0L });
            for (var i = 0L; i < 150; i++) ins.Add(new object?[] { 1000 + i, $"Filler {i}", 1.0, 3L, 0L });
        }
        b.AddTable(new DbcTableInfo("Spell", 153, 5, 5, "patch-T.MPQ", "project", null, "ok", null, "SpellWork test layout", "ID", true, "2026-10-03"), spell, Realm);

        var spellBase = new[] { C(0, "ID", "int", pk: true), C(1, "Name_0", "string", indexed: true), C(2, "Speed", "float"), C(3, "Effect", "int"), C(4, "Unused", "int") };
        using (var ins = b.BeginTable("Spell__base", spellBase))
            ins.Add(new object?[] { 133L, "Fireball", 20.0, 2L, 0L });
        b.AddTable(new DbcTableInfo("Spell__base", 1, 5, 5, "enUS/patch-enUS-3.MPQ", "unknown", "Spell", "ok", null, "SpellWork test layout", "ID", true, "2026-10-03"), spellBase, Realm);

        var thing = new[]
        {
            C(0, "f0", "int", guessed: true, pk: true), C(1, "f1", "int_or_string", guessed: true, note: "maybe a string offset; see f1__str"),
            C(1, "f1__str", "string", guessed: true, note: "maybe"), C(2, "f2", "float", guessed: true), C(3, "f3", "int", guessed: true, note: "all rows are zero; type unknown"),
        };
        using (var ins = b.BeginTable("Thing", thing))
        {
            ins.Add(new object?[] { 7L, 11L, "Sword", 2.5, 0L });
            ins.Add(new object?[] { 8L, 21L, "Axe", 0.5, 0L });
            ins.Add(new object?[] { 9L, 0L, "Map Trigger", 0.5, 0L });    // offset 0 is a real string when the block has no leading NUL
            ins.Add(new object?[] { 10L, 0L, "", 0.5, 0L });
        }
        b.AddTable(new DbcTableInfo("Thing", 4, 4, 0, "patch-M.MPQ", "unknown", null, "ok", null, null, "f0", true, "2026-10-03"), thing, Realm);

        var dupes = new[] { C(0, "f0", "int", guessed: true, indexed: true), C(1, "f1", "int", guessed: true) };
        using (var ins = b.BeginTable("Dupes", dupes))
        {
            ins.Add(new object?[] { 5L, 1L });
            ins.Add(new object?[] { 5L, 2L });
        }
        b.AddTable(new DbcTableInfo("Dupes", 2, 2, 0, "patch-M.MPQ", "unknown", null, "ok", null, null, "f0", false, "2026-10-03"), dupes, Realm);

        b.AddTable(new DbcTableInfo("Broken", 0, 0, 0, "patch-M.MPQ", "unknown", null, "unreadable", "not a WDBC file", null, null, false, "2026-10-03"), Array.Empty<DbcColumn>(), Realm);
        b.AddTable(new DbcTableInfo("Gone", 0, 0, 0, "patch-M.MPQ", "unknown", null, "deleted", "a delete marker is on top", null, null, false, "2026-10-03"), Array.Empty<DbcColumn>(), Realm);
        b.Finish(new Dictionary<string, string> { ["tables"] = "6" });
    }

    /// <summary>A wide, long table with no useful index: scans on f39 have to walk all of it.</summary>
    public static void BuildBig(string path, int rows)
    {
        using var w = new LayerWriter(path, "dbc", "9");
        var b = new DbcLayerBuilder(w);
        var cols = Enumerable.Range(0, 40).Select(i => C(i, "f" + i, i == 39 ? "string" : "int", guessed: true, pk: i == 0)).ToArray();
        using (var ins = b.BeginTable("ItemStat", cols))
        {
            var values = new object?[40];
            for (var r = 0L; r < rows; r++)
            {
                values[0] = r;
                for (var i = 1; i < 39; i++) values[i] = r % (i + 2);
                values[39] = "some long text value that takes a moment to scan " + r;
                ins.Add(values);
            }
        }
        b.AddTable(new DbcTableInfo("ItemStat", rows, 40, 0, "patch-M.MPQ", "unknown", null, "ok", null, null, "f0", true, "2026-10-03"), cols, Realm);
        b.Finish(new Dictionary<string, string>());
    }
}

public sealed class DbcFixture : IAsyncLifetime
{
    public ServerHarness Harness { get; private set; } = null!;
    public McpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var dir = TestLayers.Build();
        TestDbc.Build(Path.Combine(dir, "dbc.sqlite"));
        Harness = new ServerHarness(dir);
        Client = await Harness.ConnectAsync();
    }

    public async Task DisposeAsync()
    {
        await Client.DisposeAsync();
        Harness.Dispose();
    }

    public Task<(string Text, bool IsError)> Call(string tool, Dictionary<string, object?> args) => ServerHarness.CallAsync(Client, tool, args);
}

public class DbcToolTests : IClassFixture<DbcFixture>
{
    private readonly DbcFixture _f;
    public DbcToolTests(DbcFixture f) => _f = f;

    private static Dictionary<string, object?> A(params (string, object?)[] pairs) => pairs.ToDictionary(p => p.Item1, p => p.Item2);

    private static object W(string field, string op, object? value) => new Dictionary<string, object?> { ["field"] = field, ["op"] = op, ["value"] = value };

    private Task<(string Text, bool IsError)> Rows(params (string, object?)[] args) => _f.Call("dbc_rows", A(args));

    [Theory]
    [InlineData("dbc_tables")]
    [InlineData("dbc_rows")]
    public async Task Tools_AreReadOnly_AndNeedNoLogin(string name)
    {
        var tools = await _f.Client.ListToolsAsync();
        Assert.True(tools.Single(t => t.Name == name).ProtocolTool.Annotations?.ReadOnlyHint);
        var (_, isError) = await _f.Call(name, name == "dbc_rows" ? A(("table", "Spell")) : A());   // the harness sends no credentials
        Assert.False(isError);
    }

    [Fact]
    public async Task Tables_ListsEveryTable_WithOriginAndStatus()
    {
        var (text, isError) = await _f.Call("dbc_tables", A());

        Assert.False(isError);
        Assert.Contains("Spell", text);
        Assert.Contains("patch-T.MPQ", text);
        Assert.Contains("project", text);
        Assert.Contains("Broken", text);
        Assert.Contains("unreadable", text);
        Assert.Contains("Gone", text);
        Assert.Contains("deleted", text);
        Assert.Contains("trust client", text);
        Assert.Contains("source CoA client DBC, per CLIENT-RELEASE.json, client revision 8", text);
    }

    [Fact]
    public async Task Rows_SourceNamesTheClientRevision()
    {
        var (text, _) = await Rows(("table", "Spell"), ("limit", 1));
        Assert.Contains("source CoA client DBC, per CLIENT-RELEASE.json, client revision 8", text);
    }

    [Fact]
    public async Task Tables_Filter_IsSubstringAnyCase_AndHostileTextIsJustText()
    {
        var (text, _) = await _f.Call("dbc_tables", A(("filter", "SPEL")));
        Assert.Contains("Spell", text);
        Assert.DoesNotContain("Thing", text);

        var (none, isError) = await _f.Call("dbc_tables", A(("filter", "'; DROP TABLE dbc_tables; --")));
        Assert.False(isError);
        Assert.Contains("0 tables", none);
    }

    [Fact]
    public async Task Tables_ExactName_ShowsItsFieldsAndGuesses()
    {
        var (text, _) = await _f.Call("dbc_tables", A(("filter", "thing")));
        Assert.Contains("f1 int_or_string", text);
        Assert.Contains("guessed", text);
    }

    [Fact]
    public async Task Tables_OversizedFilter_CleanError()
    {
        var (text, isError) = await _f.Call("dbc_tables", A(("filter", new string('x', 5000))));
        Assert.True(isError);
        Assert.Contains("too long", text);
    }

    [Fact]
    public async Task Rows_FilterAndPage()
    {
        var (first, isError) = await Rows(("table", "Spell"), ("where", new[] { W("Effect", "=", 3) }), ("limit", 2));
        Assert.False(isError);
        Assert.Contains("Filler 0", first);
        Assert.Contains("Filler 1", first);
        Assert.DoesNotContain("Filler 2", first);
        Assert.Contains("offset=2", first);

        var (second, _) = await Rows(("table", "Spell"), ("where", new[] { W("Effect", "=", 3) }), ("limit", 2), ("offset", 2));
        Assert.Contains("Filler 2", second);
        Assert.DoesNotContain("Filler 1\"", second);
    }

    [Theory]
    [InlineData("=", 2, new[] { "Fireball", "Frostbolt" })]
    [InlineData("!=", 2, new[] { "Fire Shield" })]
    [InlineData(">", 5, new[] { "Fire Shield" })]
    [InlineData(">=", 6, new[] { "Fire Shield" })]
    [InlineData("<", 3, new[] { "Fireball" })]
    [InlineData("<=", 2, new[] { "Fireball" })]
    public async Task Rows_EveryOp_Works(string op, int value, string[] expected)
    {
        var (text, isError) = await Rows(("table", "Spell"), ("where", new[] { W("Effect", op, value), W("ID", "<", 1000) }));
        Assert.False(isError, text);
        foreach (var e in expected) Assert.Contains(e, text);
    }

    [Fact]
    public async Task Rows_Contains_IsLiteralAndAnyCase()
    {
        var (text, _) = await Rows(("table", "Spell"), ("where", new[] { W("Name_0", "contains", "FIRE") }));
        Assert.Contains("Fireball", text);
        Assert.Contains("Fire Shield", text);
        Assert.DoesNotContain("Frostbolt", text);

        var (wild, _) = await Rows(("table", "Spell"), ("where", new[] { W("Name_0", "contains", "%") }));
        Assert.Contains("0 rows", wild);    // % is a plain character, not a wildcard
    }

    [Fact]
    public async Task Rows_Equals_OnName_IgnoresCase()
    {
        var (text, _) = await Rows(("table", "Spell"), ("where", new[] { W("Name_0", "=", "fireball") }));
        Assert.Contains("Fireball", text);
    }

    [Fact]
    public async Task Rows_Float_EqualsTheValueAsWritten()
    {
        var (text, _) = await Rows(("table", "Thing"), ("where", new[] { W("f2", "=", 2.5) }));
        Assert.Contains("Sword", text);
    }

    [Fact]
    public async Task Rows_Contains_OnNumberField_CleanError()
    {
        var (text, isError) = await Rows(("table", "Spell"), ("where", new[] { W("Effect", "contains", "2") }));
        Assert.True(isError);
        Assert.Contains("text", text);
    }

    [Fact]
    public async Task Rows_TextValue_OnNumberField_CleanError()
    {
        var (text, isError) = await Rows(("table", "Spell"), ("where", new[] { W("Effect", "=", "two") }));
        Assert.True(isError);
        Assert.Contains("number", text);
    }

    [Fact]
    public async Task Rows_UnknownField_CleanError()
    {
        var (text, isError) = await Rows(("table", "Spell"), ("where", new[] { W("Nope", "=", 1) }));
        Assert.True(isError);
        Assert.Contains("Nope", text);
        Assert.Contains("dbc_tables", text);
    }

    [Fact]
    public async Task Rows_UnknownTable_CleanError()
    {
        var (text, isError) = await Rows(("table", "Nope"));
        Assert.True(isError);
        Assert.Contains("Unknown", text);
    }

    [Theory]
    [InlineData("ID = 133 OR 1=1 --")]
    [InlineData("ID\" = 133; DROP TABLE t_Spell; --")]
    [InlineData("name FROM sqlite_master --")]
    public async Task Rows_InjectionInFieldName_Refused(string field)
    {
        var (text, isError) = await Rows(("table", "Spell"), ("where", new[] { W(field, "=", 1) }));
        Assert.True(isError);
        Assert.Contains("Unknown field", text);
        Assert.Contains("Fireball", (await Rows(("table", "Spell"))).Text);
    }

    [Theory]
    [InlineData("Spell\"; DROP TABLE t_Spell; --")]
    [InlineData("sqlite_master")]
    [InlineData("t_Spell")]
    [InlineData("dbc_tables")]
    [InlineData("../../etc/passwd")]
    public async Task Rows_InjectionInTableName_Refused(string table)
    {
        var (text, isError) = await Rows(("table", table));
        Assert.True(isError);
        Assert.Contains("Unknown DBC table", text);
    }

    [Fact]
    public async Task Rows_InjectionInColumns_Refused()
    {
        var (text, isError) = await Rows(("table", "Spell"), ("columns", new[] { "ID, (SELECT sql FROM sqlite_master)" }));
        Assert.True(isError);
        Assert.Contains("Unknown field", text);
    }

    [Fact]
    public async Task Rows_InjectionInValue_IsOnlyAValue()
    {
        var (text, isError) = await Rows(("table", "Spell"), ("where", new[] { W("Name_0", "=", "x' OR '1'='1") }));
        Assert.False(isError);
        Assert.Contains("0 rows", text);
    }

    [Theory]
    [InlineData("LIKE")]
    [InlineData("= 1 OR 1=")]
    [InlineData("")]
    public async Task Rows_UnknownOp_CleanError(string op)
    {
        var (text, isError) = await Rows(("table", "Spell"), ("where", new[] { W("ID", op, 1) }));
        Assert.True(isError);
        Assert.Contains("op", text);
    }

    [Fact]
    public async Task Rows_ConditionMissingItsParts_CleanError()
    {
        var (field, e1) = await Rows(("table", "Spell"), ("where", new object[] { new Dictionary<string, object?> { ["op"] = "=", ["value"] = 1 } }));
        Assert.True(e1);
        Assert.Contains("Unknown field", field);

        var (op, e2) = await Rows(("table", "Spell"), ("where", new object[] { new Dictionary<string, object?> { ["field"] = "ID", ["value"] = 1 } }));
        Assert.True(e2);
        Assert.Contains("op", op);

        var (value, e3) = await Rows(("table", "Spell"), ("where", new object[] { new Dictionary<string, object?> { ["field"] = "ID", ["op"] = "=" } }));
        Assert.True(e3);
        Assert.Contains("value", value);
    }

    [Theory]
    [InlineData("just text")]
    [InlineData(5)]
    public async Task Rows_WhereOfTheWrongShape_IsAnErrorNotAFault(object where)
    {
        var (_, isError) = await Rows(("table", "Spell"), ("where", where));
        Assert.True(isError);
        Assert.Contains("Fireball", (await Rows(("table", "Spell"))).Text);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e9999")]
    public async Task Rows_NonFiniteNumbers_CleanError(string value)
    {
        var (text, isError) = await Rows(("table", "Spell"), ("where", new[] { W("Speed", "=", value) }));
        Assert.True(isError);
        Assert.Contains("number", text);
    }

    [Fact]
    public async Task Rows_TooManyConditions_CleanError()
    {
        var (text, isError) = await Rows(("table", "Spell"), ("where", Enumerable.Range(0, 9).Select(i => W("ID", ">", i)).ToArray()));
        Assert.True(isError);
        Assert.Contains("8", text);
    }

    [Fact]
    public async Task Rows_OversizedValue_CleanError()
    {
        var (text, isError) = await Rows(("table", "Spell"), ("where", new[] { W("Name_0", "contains", new string('x', 5000)) }));
        Assert.True(isError);
        Assert.Contains("too long", text);
    }

    [Fact]
    public async Task Rows_NullOrMissingValue_CleanError()
    {
        var (text, isError) = await Rows(("table", "Spell"), ("where", new[] { W("ID", "=", null) }));
        Assert.True(isError);
        Assert.Contains("value", text);
    }

    [Fact]
    public async Task Rows_CapsAt100()
    {
        var (text, isError) = await Rows(("table", "Spell"), ("limit", 5000), ("columns", new[] { "ID" }));
        Assert.False(isError);
        Assert.Contains("capped at 100", text);
        Assert.Equal(100, text.Split('\n').Count(l => l.StartsWith("- ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Rows_HugeOrNegativePaging_IsClamped()
    {
        var (text, isError) = await Rows(("table", "Spell"), ("limit", -1), ("offset", -5));
        Assert.False(isError);
        Assert.Equal(1, text.Split('\n').Count(l => l.StartsWith("- ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Rows_Columns_LimitWhatIsShown()
    {
        var (text, _) = await Rows(("table", "Spell"), ("columns", new[] { "Speed" }), ("where", new[] { W("ID", "=", 133) }));
        Assert.Contains("Speed=24", text);
        Assert.Contains("ID=133", text);   // the row's id always comes along
        Assert.DoesNotContain("Fireball", text);
    }

    [Fact]
    public async Task Rows_ShowGuessed_AndTheOriginalInteger()
    {
        var (text, _) = await Rows(("table", "Thing"));
        Assert.Contains("guessed", text);
        Assert.Contains("f1?=11 \"Sword\"", text);    // the integer and the maybe-string, both visible
        Assert.Contains("f2?=2.5", text);
    }

    [Fact]
    public async Task Rows_CanFilterOnMaybeStringAsEitherInterpretation()
    {
        var (byInt, e1) = await Rows(("table", "Thing"), ("where", new[] { W("f1", "=", 21) }));
        Assert.False(e1);
        Assert.Contains("Axe", byInt);
        var (byText, e2) = await Rows(("table", "Thing"), ("where", new[] { W("f1__str", "contains", "wor") }));
        Assert.False(e2);
        Assert.Contains("Sword", byText);
    }

    [Fact]
    public async Task Rows_Named_NotMarkedGuessed()
    {
        var (text, _) = await Rows(("table", "Spell"), ("where", new[] { W("ID", "=", 133) }));
        Assert.Contains("Name_0=\"Fireball\"", text);
        Assert.DoesNotContain("Name_0?", text);
    }

    [Fact]
    public async Task Rows_UnreadableOrDeletedTable_CleanErrorWithReason()
    {
        var (broken, e1) = await Rows(("table", "Broken"));
        Assert.True(e1);
        Assert.Contains("not a WDBC file", broken);
        var (gone, e2) = await Rows(("table", "Gone"));
        Assert.True(e2);
        Assert.Contains("delete marker", gone);
    }

    [Fact]
    public async Task Rows_TableName_AnyCase()
    {
        var (text, isError) = await Rows(("table", "spell"), ("where", new[] { W("id", "=", 133) }));
        Assert.False(isError);
        Assert.Contains("Fireball", text);
    }

    [Fact]
    public async Task Get_Row_ShowsNamesOriginWinnerAndSource()
    {
        var (text, isError) = await _f.Call("get", A(("id", "dbc:Spell:133")));
        Assert.False(isError);
        Assert.Contains("Name_0: \"Fireball\"", text);
        Assert.Contains("Speed: 24", text);
        Assert.Contains("patch-T.MPQ", text);
        Assert.Contains("project", text);
        Assert.Contains("trust: client", text);
        Assert.Contains("source: CoA client DBC, per CLIENT-RELEASE.json, client revision 8", text);
        Assert.Contains("source date: 2026-10-03", text);
        Assert.Contains("the CoA client", text);
        Assert.DoesNotContain("Unused", text.Split("omitted")[0]);
    }

    [Fact]
    public async Task Row_FromProjectArchive_ShowsBaseRow()
    {
        var (text, _) = await _f.Call("get", A(("id", "dbc:Spell:133")));

        Assert.Contains("Spell__base", text);
        Assert.Contains("enUS/patch-enUS-3.MPQ", text);
        Assert.Contains("Speed: 20", text);                      // the value that differs
        Assert.Contains("not evidence about Ascension", text);
        Assert.Contains("not claimed as Ascension", text);
        Assert.DoesNotContain("__ascension", text);
    }

    [Fact]
    public async Task Row_FromProjectArchive_NoBaseRow_SaysSo()
    {
        var (text, _) = await _f.Call("get", A(("id", "dbc:Spell:134")));
        Assert.Contains("no row with this id", text);
    }

    [Fact]
    public async Task Get_GuessedRow_SaysGuessed_AndKeepsTheInteger()
    {
        var (text, _) = await _f.Call("get", A(("id", "dbc:Thing:7")));
        Assert.Contains("guessed", text);
        Assert.Contains("f1 (guessed int_or_string): 11 \"Sword\"", text);
        Assert.Contains("f2 (guessed float): 2.5", text);
    }

    private const string MismatchM = "patch-M.MPQ on disk (228.7 MB) doesn't match the release file's entry (9.0 MB), so it may be newer than revision 8.";

    [Fact]
    public async Task Get_RowWinnerSizeMismatch_SaysSo()
    {
        var (text, _) = await _f.Call("get", A(("id", "dbc:Thing:7")));
        Assert.Contains(MismatchM, text);
    }

    [Fact]
    public async Task Get_RowWinnerMatches_NoLineForThatArchive()
    {
        var (text, _) = await _f.Call("get", A(("id", "dbc:Spell:134")));   // patch-T has no mismatch; its base, patch-enUS-3, does
        Assert.DoesNotContain("patch-T.MPQ on disk", text);
        Assert.DoesNotContain("patch-M.MPQ on disk", text);
    }

    [Fact]
    public async Task Get_BaseCopyMismatch_SaysSo_AndMatchingThePreviousPackageIsNotCalledNewer()
    {
        var (text, _) = await _f.Call("get", A(("id", "dbc:Spell:133")));
        Assert.Contains("enUS/patch-enUS-3.MPQ on disk (2.0 MB) doesn't match the release file's entry (1.0 MB); it matches the previous package's entry.", text);
    }

    [Fact]
    public async Task Rows_And_Tables_SayWhenAWinnerMismatches()
    {
        var (rows, _) = await Rows(("table", "Thing"));
        Assert.Contains(MismatchM, rows);
        var (spellRows, _) = await Rows(("table", "Spell"), ("limit", 1));
        Assert.DoesNotContain("patch-M.MPQ on disk", spellRows);

        var (tables, _) = await _f.Call("dbc_tables", A(("filter", "Thing")));
        Assert.Contains(MismatchM, tables);
        var (spellTables, _) = await _f.Call("dbc_tables", A(("filter", "Spell")));
        Assert.DoesNotContain("patch-M.MPQ on disk", spellTables);

        var (page, _) = await _f.Call("get", A(("id", "dbc:Thing")));
        Assert.Contains(MismatchM, page);
    }

    [Fact]
    public async Task Get_MaybeStringAtOffsetZero_ShowsItsText()
    {
        var (text, _) = await _f.Call("get", A(("id", "dbc:Thing:9")));
        Assert.Contains("f1 (guessed int_or_string): 0 \"Map Trigger\"", text);
    }

    [Fact]
    public async Task Get_MaybeStringZeroWithEmptyText_IsLeftOut()
    {
        var (text, _) = await _f.Call("get", A(("id", "dbc:Thing:10")));
        Assert.DoesNotContain("f1 (guessed", text);
    }

    [Fact]
    public async Task Rows_MaybeStringAtOffsetZero_ShowsItsText()
    {
        var (text, _) = await Rows(("table", "Thing"), ("where", new[] { W("f0", "=", 9) }));
        Assert.Contains("f1?=0 \"Map Trigger\"", text);

        var (empty, _) = await Rows(("table", "Thing"), ("where", new[] { W("f0", "=", 10) }));
        Assert.DoesNotContain("f1?", empty.Split("left out.")[1]);
    }

    [Fact]
    public async Task Get_RowWithDuplicateIds_ShowsThemAll_AndSaysSo()
    {
        var (text, _) = await _f.Call("get", A(("id", "dbc:Dupes:5")));
        Assert.Contains("2 rows", text);
    }

    [Theory]
    [InlineData("dbc:Spell:999999", "No row")]
    [InlineData("dbc:Spell:abc", "whole number")]
    [InlineData("dbc:Nope:1", "Unknown DBC table")]
    [InlineData("dbc:Spell\";DROP TABLE t_Spell;--:1", "Unknown DBC table")]
    [InlineData("dbc:Broken:1", "not a WDBC file")]
    [InlineData("dbc:Spell:99999999999999999999999999", "whole number")]
    public async Task Get_BadRowIds_AnswerCleanly(string id, string expected)
    {
        var (text, isError) = await _f.Call("get", A(("id", id)));
        Assert.False(isError);
        Assert.Contains(expected, text);
    }

    [Fact]
    public async Task Get_TablePage_Works()
    {
        var (text, isError) = await _f.Call("get", A(("id", "dbc:Spell")));
        Assert.False(isError);
        Assert.Contains("type: dbc_table", text);
        Assert.Contains("Name_0", text);
    }

    [Fact]
    public async Task Search_FindsATableByName_AndByFieldName()
    {
        var (text, _) = await _f.Call("search", A(("query", "Thing"), ("topic", "dbc")));
        Assert.Contains("dbc:Thing", text);

        var (byField, _) = await _f.Call("search", A(("query", "Speed"), ("topic", "dbc")));
        Assert.Contains("dbc:Spell", byField);
    }

    [Fact]
    public async Task Status_ShowsTheDbcLayer()
    {
        var (text, _) = await _f.Call("status", A());
        Assert.Contains("dbc:", text);
        Assert.Contains("6 tables", text);
        Assert.Contains("guessed", text);
        Assert.Contains("per CLIENT-RELEASE.json, client revision 8 (test-release)", text);
        Assert.Contains("the release file's patch-B entry is off", text);
    }

    [Fact]
    public async Task Rows_LayerNotLoaded_CleanError()
    {
        using var h = new ServerHarness(TestLayers.Build(withSpells: false, withPaths: false));
        var client = await h.ConnectAsync();
        await using (client)
        {
            var (text, isError) = await ServerHarness.CallAsync(client, "dbc_rows", A(("table", "Spell")));
            Assert.True(isError);
            Assert.Contains("not loaded", text);
        }
    }
}

/// <summary>The spell tools repeat a page's source, so each must carry which archive won Spell.dbc.</summary>
public class SpellSourceToolTests
{
    private static Dictionary<string, object?> A(params (string, object?)[] pairs) => pairs.ToDictionary(p => p.Item1, p => p.Item2);

    [Theory]
    [InlineData("CoA client DBC (via Spellwork), per CLIENT-RELEASE.json, client revision 8, Spell.dbc from patch-T.MPQ (project-built archive)")]
    [InlineData("CoA client DBC (via Spellwork), per CLIENT-RELEASE.json, client revision 8, Spell.dbc from enUS/patch-enUS-3.MPQ (origin unknown)")]
    public async Task EverySpellAnswer_CarriesTheWinningArchive(string source)
    {
        var dir = TestLayers.Build(withSpells: false);
        TestLayers.BuildSpells(Path.Combine(dir, "spells.sqlite"), source);
        using var h = new ServerHarness(dir);
        var client = await h.ConnectAsync();
        await using (client)
        {
            foreach (var (tool, args) in new (string, Dictionary<string, object?>)[]
            {
                ("get", A(("id", "spell:133"))),
                ("search", A(("query", "Fireball"), ("topic", "spells"))),
                ("spell_compare", A(("idA", "spell:1"), ("idB", "spell:2"))),
                ("spell_scaling", A(("id", "spell:10"), ("level", 80))),
                ("decode_flags", A(("kind", "SpellSchoolMask"), ("value", "4"))),
            })
            {
                var (text, isError) = await ServerHarness.CallAsync(client, tool, args);
                Assert.False(isError, text);
                Assert.True(text.Contains(source, StringComparison.Ordinal), $"{tool} did not carry the source: {text}");
            }
        }
    }
}

public class SpellMismatchToolTests
{
    private static Dictionary<string, object?> A(params (string, object?)[] pairs) => pairs.ToDictionary(p => p.Item1, p => p.Item2);

    private const string Line = "patch-T.MPQ on disk (5.0 MB) doesn't match the release file's entry (4.0 MB), so it may be newer than revision 8.";

    [Fact]
    public async Task SpellTools_SayWhenSpellDbcsWinnerMismatches()
    {
        var dir = TestLayers.Build(withSpells: false);
        TestLayers.BuildSpells(Path.Combine(dir, "spells.sqlite"), client: new ClientRelease(8, "r", null, new[] { new ClientArchiveMismatch("patch-T.MPQ", 4000000, 5000000, null) }));
        using var h = new ServerHarness(dir);
        var client = await h.ConnectAsync();
        await using (client)
        {
            foreach (var (tool, args) in new (string, Dictionary<string, object?>)[]
            {
                ("spell_compare", A(("idA", "spell:1"), ("idB", "spell:2"))),
                ("spell_scaling", A(("id", "spell:10"), ("level", 80))),
                ("decode_flags", A(("kind", "SpellSchoolMask"), ("value", "4"))),
            })
            {
                var (text, isError) = await ServerHarness.CallAsync(client, tool, args);
                Assert.False(isError, text);
                Assert.Contains(Line, text);
            }
        }
    }

    [Fact]
    public async Task SpellTools_NoMismatch_NoLine()
    {
        var dir = TestLayers.Build(withSpells: false);
        TestLayers.BuildSpells(Path.Combine(dir, "spells.sqlite"), client: new ClientRelease(8, "r", null));
        using var h = new ServerHarness(dir);
        var client = await h.ConnectAsync();
        await using (client)
        {
            var (text, _) = await ServerHarness.CallAsync(client, "spell_scaling", A(("id", "spell:10"), ("level", 80)));
            Assert.DoesNotContain("doesn't match the release file", text);
        }
    }
}

/// <summary>A long table and a tiny time budget, so the limit can be hit without a slow test.</summary>
public sealed class BigDbcFixture : IAsyncLifetime
{
    public const int Rows = 150_000;
    public ServerHarness Harness { get; private set; } = null!;
    public McpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var dir = TestLayers.Build(withSpells: false, withPaths: false);
        TestDbc.BuildBig(Path.Combine(dir, "dbc.sqlite"), Rows);
        Harness = new ServerHarness(dir, new Dictionary<string, string?> { ["Dbc:QueryBudgetMs"] = "5" });
        Client = await Harness.ConnectAsync();
    }

    public async Task DisposeAsync()
    {
        await Client.DisposeAsync();
        Harness.Dispose();
    }
}

public class DbcBudgetTests : IClassFixture<BigDbcFixture>
{
    private readonly BigDbcFixture _f;
    public DbcBudgetTests(BigDbcFixture f) => _f = f;

    private static Dictionary<string, object?> A(params (string, object?)[] pairs) => pairs.ToDictionary(p => p.Item1, p => p.Item2);

    private static object W(string field, string op, object? value) => new Dictionary<string, object?> { ["field"] = field, ["op"] = op, ["value"] = value };

    [Fact]
    public async Task Rows_SlowScan_StopsWithCleanMessage_NamingIndexedFields()
    {
        var sw = Stopwatch.StartNew();
        var (text, isError) = await ServerHarness.CallAsync(_f.Client, "dbc_rows",
            A(("table", "ItemStat"), ("where", new[] { W("f39", "contains", "no such text anywhere") })));
        sw.Stop();

        Assert.True(isError);
        Assert.Contains("too slow", text);
        Assert.Contains("filter on an indexed field", text);
        Assert.Contains("f0", text);
        Assert.True(sw.ElapsedMilliseconds < 3000, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Rows_IndexedLookup_IsFine()
    {
        var (text, isError) = await ServerHarness.CallAsync(_f.Client, "dbc_rows", A(("table", "ItemStat"), ("where", new[] { W("f0", "=", 149_000) })));
        Assert.False(isError, text);
        Assert.Contains(149_000.ToString(CultureInfo.InvariantCulture), text);
    }

    [Fact]
    public async Task Get_StaysResponsive_WhileSlowScansRun()
    {
        using var stop = new CancellationTokenSource();
        var scans = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
                await ServerHarness.CallAsync(_f.Client, "dbc_rows", A(("table", "ItemStat"), ("where", new[] { W("f39", "contains", "nothing like this") })));
        })).ToList();

        await Task.Delay(50);
        var worst = 0L;
        for (var i = 0; i < 20; i++)
        {
            var sw = Stopwatch.StartNew();
            var (text, _) = await ServerHarness.CallAsync(_f.Client, "get", A(("id", "dbc:ItemStat:1")));
            worst = Math.Max(worst, sw.ElapsedMilliseconds);
            Assert.Contains("f1", text);
        }
        stop.Cancel();
        await Task.WhenAll(scans);
        Assert.True(worst < 1000, $"slowest get took {worst} ms while scans ran");
    }

    [Fact]
    public void LayerBudget_InterruptsAndConnectionStaysUsable()
    {
        var dir = Path.Combine(Path.GetTempPath(), "coamcp-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "dbc.sqlite");
        TestDbc.BuildBig(file, 50_000);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            using var r = new LayerReader(file);
            Assert.Throws<QueryBudgetExceededException>(() => r.QueryWithin(TimeSpan.Zero,
                "SELECT COUNT(*) AS n FROM t_ItemStat a, t_ItemStat b WHERE a.f39 = b.f39"));
            Assert.Equal(50_000L, r.Query("SELECT COUNT(*) AS n FROM t_ItemStat")[0]["n"]);   // the budget is gone once the query ends
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch (IOException) { /* temp data */ }
        }
    }
}
