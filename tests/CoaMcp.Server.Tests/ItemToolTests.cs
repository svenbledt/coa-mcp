using CoaMcp.Core;
using ModelContextProtocol.Client;

namespace CoaMcp.Server.Tests;

public sealed class ItemFixture : IAsyncLifetime
{
    public ServerHarness Harness { get; private set; } = null!;
    public McpClient Client { get; private set; } = null!;

    private static void AddItem(BulkInsert items, BulkInsert stats, BulkInsert classes, string list, long id, string name, string realms, bool proven,
        string slot, string quality, long level, string[] itemClasses, params (string Stat, double Value)[] values)
    {
        var entity = list == "main" ? $"item:{id}" : $"item:{list}:{id}";
        items.Add(new object?[] { list, id, entity, name, realms, proven ? 1L : 0L, list == "main" ? 1L : null, slot, "Cloth", quality, level, 60L, "raid", "Test Boss", "Normal", 2L, null, null, null, null, null, null });
        foreach (var (stat, value) in values) stats.Add(new object?[] { list, id, stat, value });
        foreach (var c in itemClasses) classes.Add(new object?[] { list, id, c });
    }

    public async Task InitializeAsync()
    {
        var dir = TestLayers.Build(withBisbeard: false);
        TestLayers.BuildBisbeard(Path.Combine(dir, "bisbeard.sqlite"), extra: w =>
        {
            ItemTables.Create(w);
            using var items = w.BeginInsert(ItemTables.Items, ItemTables.ItemColumns);
            using var stats = w.BeginInsert(ItemTables.Stats, ItemTables.StatColumns);
            using var classes = w.BeginInsert(ItemTables.Classes, ItemTables.ClassColumns);
            AddItem(items, stats, classes, "main", 1, "Crown of Tests", "coa,classic,s10", false, "Head", "Epic", 70, new[] { "Mage" }, ("stamina", 10), ("spellPower", 30));
            AddItem(items, stats, classes, "main", 2, "Hat of Everyone", "coa,classic,s10", false, "Head", "Rare", 50, new[] { "All" }, ("stamina", 20));
            AddItem(items, stats, classes, "main", 3, "Druid Belt", "coa,classic,s10", false, "Waist", "Epic", 60, new[] { "Druid" }, ("stamina", 5), ("agility", 9));
            AddItem(items, stats, classes, "wotlk", 1, "Crown of Tests", "wotlk", true, "Head", "Epic", 80, new[] { "Mage" }, ("spellPower", 99));
            for (var i = 0; i < 130; i++)
                AddItem(items, stats, classes, "main", 1000 + i, $"Filler {i}", "coa,classic,s10", false, "Feet", "Rare", 10, new[] { "All" }, ("stamina", 1));
        });
        Harness = new ServerHarness(dir);
        Client = await Harness.ConnectAsync();
    }

    public async Task DisposeAsync()
    {
        await Client.DisposeAsync();
        Harness.Dispose();
    }

    public Task<(string Text, bool IsError)> Call(params (string, object?)[] args) =>
        ServerHarness.CallAsync(Client, "items_find", args.ToDictionary(a => a.Item1, a => a.Item2));
}

public class ItemToolTests : IClassFixture<ItemFixture>
{
    private readonly ItemFixture _f;
    public ItemToolTests(ItemFixture f) => _f = f;

    [Fact]
    public async Task Tool_IsReadOnly_AndNeedsNoLogin()
    {
        var tools = await _f.Client.ListToolsAsync();
        Assert.True(tools.Single(t => t.Name == "items_find").ProtocolTool.Annotations?.ReadOnlyHint);
        var (_, isError) = await _f.Call(("slot", "Head"));   // the harness sends no credentials
        Assert.False(isError);
    }

    [Fact]
    public async Task Realm_Coa_ReturnsSharedListAsUnproven_NotWotlk()
    {
        var (text, isError) = await _f.Call(("realm", "coa"), ("slot", "Head"));
        Assert.False(isError);
        Assert.Contains("item:1 | Crown of Tests", text);
        Assert.Contains("unproven", text);
        Assert.DoesNotContain("item:wotlk:1", text);
    }

    [Fact]
    public async Task Realm_Wotlk_IsProven()
    {
        var (text, _) = await _f.Call(("realm", "wotlk"));
        Assert.Contains("item:wotlk:1 | Crown of Tests", text);
        Assert.Contains("(proven)", text);
        Assert.DoesNotContain("in_client", text);   // only the shared list says anything about the client
    }

    [Fact]
    public async Task Class_IncludesAllClassItems()
    {
        var (text, _) = await _f.Call(("realm", "coa"), ("item_class", "mage"), ("slot", "Head"));
        Assert.Contains("Crown of Tests", text);
        Assert.Contains("Hat of Everyone", text);
        Assert.DoesNotContain("Druid Belt", text);
    }

    [Fact]
    public async Task Stat_SortsDescending_AndMinValueFilters()
    {
        var (text, _) = await _f.Call(("realm", "coa"), ("stat", "STAMINA"), ("min_value", 6));
        Assert.True(text.IndexOf("Hat of Everyone", StringComparison.Ordinal) < text.IndexOf("Crown of Tests", StringComparison.Ordinal));
        Assert.DoesNotContain("Druid Belt", text);
    }

    [Fact]
    public async Task Results_AreCappedAndPageable()
    {
        var (text, _) = await _f.Call(("slot", "Feet"), ("limit", 100000));
        Assert.Contains("capped at 100", text);
        Assert.Equal(100, text.Split('\n').Count(l => l.StartsWith("item:1", StringComparison.Ordinal)));
        Assert.Contains("use offset=100", text);
    }

    [Theory]
    [InlineData("stat", "nope")]
    [InlineData("min_value", 5.0)]
    [InlineData("name", "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
    public async Task BadInput_GivesCleanError(string arg, object value)
    {
        var (text, isError) = await _f.Call((arg, value));
        Assert.True(isError);
        Assert.DoesNotContain("SQLite", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("name", "'; DROP TABLE items; --")]
    [InlineData("slot", "Head' OR '1'='1")]
    [InlineData("realm", "coa%")]
    public async Task HostileText_IsDataNotSql(string arg, string value)
    {
        var (text, isError) = await _f.Call((arg, value));
        Assert.False(isError);
        Assert.Contains("0 items", text);
        var (after, _) = await _f.Call(("realm", "coa"));
        Assert.Contains("Crown of Tests", after);
    }
}
