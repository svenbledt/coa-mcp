using CoaMcp.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace CoaMcp.Server.Tests;

/// <summary>One damaged row in the store must cost that row, not the page around it.</summary>
public class CorruptEntryTests
{
    private static void Damage(ServerHarness h, long id, string column)
    {
        using var db = new SqliteConnection($"Data Source={h.StorePath};Mode=ReadWrite;Pooling=False");
        db.Open();
        using var cmd = db.CreateCommand();
        // The column name comes from the test's own list below.
        cmd.CommandText = $"UPDATE entries SET {column} = '{{not json' WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    [Theory]
    [InlineData("evidence_json")]
    [InlineData("realm_json")]
    [InlineData("position_json")]
    public async Task Get_CorruptEntry_StillShowsPage_AndLogsError(string column)
    {
        var logs = new CaptureLogs();
        var token = W.NewToken();
        using var h = W.Harness(token, logging: b => b.AddProvider(logs));
        await using var c = await W.Writer(h, token);
        await W.Write(c, W.Entry("item:10531", "GOODTEXT"));
        var bad = await W.Write(c, W.Entry("item:10531", "BADTEXT quokka"));
        Damage(h, bad, column);
        var name = "entry:" + bad;

        var (page, isError) = await W.Call(c, "get", new() { ["id"] = "item:10531" });
        Assert.False(isError, page);
        Assert.Contains("Libram of the Dawn", page);
        Assert.Contains("GOODTEXT", page);
        Assert.Contains(name + " could not be read", page);
        Assert.DoesNotContain("BADTEXT", page);
        Assert.DoesNotContain("unavailable", page);

        Assert.Contains(logs.Lines, l => l.Level == LogLevel.Error && l.Text.Contains(name, StringComparison.Ordinal));

        var (hits, hitsError) = await W.Call(c, "search", new() { ["query"] = "quokka" });
        Assert.False(hitsError, hits);
        Assert.Contains(name + " could not be read", hits);

        var (one, oneError) = await W.Call(c, "get", new() { ["id"] = name });
        Assert.False(oneError, one);
        Assert.Contains(name + " could not be read", one);

        var (status, _) = await W.Call(c, "status");
        Assert.Contains("1 damaged", status);
        Assert.DoesNotContain("unavailable", status);

        // The remedy still works, and is still idempotent.
        Assert.False((await W.Call(c, "undo_entry", new() { ["id"] = bad, ["author"] = "Carol" })).IsError);
        Assert.Contains("already", (await W.Call(c, "undo_entry", new() { ["id"] = bad, ["author"] = "Carol" })).Text);
        Assert.DoesNotContain(name + " could not be read", (await W.Call(c, "get", new() { ["id"] = "item:10531" })).Text);
    }

    [Fact]
    public async Task CorruptEntry_LogsOnce_NotOnEveryRead()
    {
        var logs = new CaptureLogs();
        var token = W.NewToken();
        using var h = W.Harness(token, logging: b => b.AddProvider(logs));
        await using var c = await W.Writer(h, token);
        var bad = await W.Write(c, W.Entry("npc:one", "text"));
        Damage(h, bad, "evidence_json");
        for (var i = 0; i < 3; i++) await W.Call(c, "get", new() { ["id"] = "npc:one" });
        Assert.Single(logs.Lines, l => l.Level == LogLevel.Error && l.Text.Contains("entry:" + bad, StringComparison.Ordinal));
    }

    [Fact]
    public async Task CorruptRealm_InAConflict_DoesNotBreakThePage()
    {
        var token = W.NewToken();
        using var h = W.Harness(token);
        await using var c = await W.Writer(h, token);
        var bad = await W.Write(c, W.Entry("npc:two", more: new[] { ("field", (object?)"hp"), ("value", (object?)"1") }));
        await W.Write(c, W.Entry("npc:two", more: new[] { ("field", (object?)"hp"), ("value", (object?)"2") }));
        Damage(h, bad, "realm_json");
        var (page, isError) = await W.Call(c, "get", new() { ["id"] = "npc:two" });
        Assert.False(isError, page);
        Assert.Contains("Conflict", page);
    }

    [Fact]
    public async Task UnreadableStore_IsStillAStoreProblem_NotADamagedEntry()
    {
        var token = W.NewToken();
        using var h = W.Harness(token);
        Directory.CreateDirectory(Path.GetDirectoryName(h.StorePath)!);
        await File.WriteAllTextAsync(h.StorePath, new string('x', 8192));
        await using var c = await W.Writer(h, token);
        Assert.Contains("unavailable", (await W.Call(c, "status")).Text);
    }
}
