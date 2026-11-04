using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace CoaMcp.Server.Tests;

/// <summary>Shared helpers for tests that write. Tokens are random per test, so none exists in the repo.</summary>
internal static class W
{
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    public static ServerHarness Harness(string? token, IDictionary<string, string?>? extra = null, Action<ILoggingBuilder>? logging = null, string? layerDir = null)
    {
        return new ServerHarness(layerDir, extra, logging, token);
    }

    public static Task<McpClient> Writer(ServerHarness h, string token, string? peer = null) => h.ConnectAsync("Bearer " + token, peer);

    public static Dictionary<string, object?> Entry(string subject, string content = "Seen in the field.", string trust = "memory", string author = "Ann",
        string kind = "note", params (string Key, object? Value)[] more)
    {
        var d = new Dictionary<string, object?> { ["author"] = author, ["subject"] = subject, ["kind"] = kind, ["trust"] = trust, ["content"] = content };
        foreach (var (k, v) in more) d[k] = v;
        return d;
    }

    public static Dictionary<string, object?> Desc(string text) => new() { ["description"] = text };

    /// <summary>A tool call that turns every kind of refusal (tool error or protocol error) into text, so tests can check what the caller sees.</summary>
    public static async Task<(string Text, bool IsError)> Call(McpClient c, string tool, Dictionary<string, object?>? args = null)
    {
        try { return await ServerHarness.CallAsync(c, tool, args); }
        catch (McpException ex) { return (ex.Message, true); }
    }

    public static async Task<long> Write(McpClient c, Dictionary<string, object?> args)
    {
        var (text, isError) = await ServerHarness.CallAsync(c, "write_entry", args);
        Assert.False(isError, text);
        var m = System.Text.RegularExpressions.Regex.Match(text, @"entry:(\d+)");
        Assert.True(m.Success, text);
        return long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }
}

internal sealed class CaptureLogs : ILoggerProvider
{
    public ConcurrentQueue<(LogLevel Level, string Text)> Lines { get; } = new();
    public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName);
    public void Dispose() { }
    public string All => string.Join("\n", Lines.Select(l => l.Text));

    private sealed class Sink(CaptureLogs owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Lines.Enqueue((logLevel, $"{category}: {formatter(state, exception)} {exception}"));
    }
}

public class TokenAuthTests
{
    private const string Item = "item:10531";

    [Fact]
    public async Task Read_NoToken_Works()
    {
        using var h = W.Harness(W.NewToken());
        await using var c = await h.ConnectAsync(null);
        var (text, isError) = await W.Call(c, "get", new() { ["id"] = Item });
        Assert.False(isError);
        Assert.Contains("Libram of the Dawn", text);
    }

    [Theory]
    [InlineData("Bearer wrong")]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("Bearer")]
    [InlineData("garbage")]
    public async Task Read_InvalidToken_Works(string authorization)
    {
        using var h = W.Harness(W.NewToken());
        await using var c = await h.ConnectAsync(authorization);
        var (text, isError) = await W.Call(c, "get", new() { ["id"] = Item });
        Assert.False(isError);
        Assert.Contains("Libram of the Dawn", text);
    }

    [Fact]
    public async Task ReadWithAValidToken_SeesTheSamePage()
    {
        var token = W.NewToken();
        using var h = W.Harness(token);
        await using var anon = await h.ConnectAsync(null);
        await using var writer = await W.Writer(h, token);
        Assert.Equal((await W.Call(anon, "get", new() { ["id"] = Item })).Text, (await W.Call(writer, "get", new() { ["id"] = Item })).Text);
    }

    [Fact]
    public async Task AnonymousToolList_HidesWriteTools_TokenHolderSeesThem()
    {
        var token = W.NewToken();
        using var h = W.Harness(token);
        await using var anon = await h.ConnectAsync(null);
        await using var bad = await h.ConnectAsync("Bearer " + W.NewToken());
        await using var writer = await W.Writer(h, token);

        foreach (var c in new[] { anon, bad })
        {
            var names = (await c.ListToolsAsync()).Select(t => t.Name).ToList();
            Assert.DoesNotContain("write_entry", names);
            Assert.DoesNotContain("undo_entry", names);
            Assert.Contains("get", names);
        }
        var mine = (await writer.ListToolsAsync()).ToList();
        Assert.Contains(mine, t => t.Name == "write_entry");
        Assert.Contains(mine, t => t.Name == "undo_entry");
        // The agent needs the rules in the description, and the list stays short.
        var desc = mine.Single(t => t.Name == "write_entry").Description;
        Assert.Contains("observed", desc);
        Assert.Contains("memory", desc);
        Assert.Contains("evidence", desc);
        Assert.True(desc.Length < 500, $"description is {desc.Length} chars");
    }

    private static async Task AssertRefused(ServerHarness h, string? authorization)
    {
        await using var c = await h.ConnectAsync(authorization);
        var (text, isError) = await W.Call(c, "write_entry", W.Entry("test:auth"));
        Assert.True(isError, text);
        Assert.DoesNotContain("   at ", text);
        var (undo, undoError) = await W.Call(c, "undo_entry", new() { ["id"] = 1L, ["author"] = "Ann" });
        Assert.True(undoError, undo);
        Assert.False(File.Exists(h.StorePath), "A refused write must not touch the store.");
    }

    [Fact]
    public async Task Write_NoToken_Refused()
    {
        using var h = W.Harness(W.NewToken());
        await AssertRefused(h, null);
    }

    [Fact]
    public async Task Write_WrongToken_Refused()
    {
        using var h = W.Harness(W.NewToken());
        await AssertRefused(h, "Bearer " + W.NewToken());
    }

    [Theory]
    [InlineData("Bearer ")]
    [InlineData("bearer")]
    [InlineData("Token {0}")]
    [InlineData("Bearer {0}x")]
    [InlineData("Bearer x{0}")]
    [InlineData("{0}")]
    public async Task Write_MalformedToken_Refused_NoTokenInError(string shape)
    {
        var token = W.NewToken();
        using var h = W.Harness(token);
        await using var c = await h.ConnectAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, shape, token));
        var (text, isError) = await W.Call(c, "write_entry", W.Entry("test:auth"));
        Assert.True(isError, text);
        Assert.DoesNotContain(token, text);
        Assert.False(File.Exists(h.StorePath));
    }

    /// <summary>HttpClient tidies some header shapes on the way out, so the comparison itself is checked directly too.</summary>
    [Fact]
    public void Matches_IsExact()
    {
        var token = W.NewToken();
        var auth = new TokenAuth(token);
        Assert.True(auth.Enabled);
        Assert.True(auth.Matches(token));
        foreach (var bad in new[] { "", " " + token, token + " ", token.ToLowerInvariant(), token[..^1], token + "x" })
            Assert.False(auth.Matches(bad), bad);
        Assert.False(new TokenAuth(null).Matches(token));
        Assert.False(new TokenAuth(token[..31]).Matches(token[..31]));
    }

    [Fact]
    public async Task BearerScheme_IsCaseInsensitive()
    {
        var token = W.NewToken();
        using var h = W.Harness(token);
        await using var c = await h.ConnectAsync("bearer " + token);
        await W.Write(c, W.Entry("test:auth"));
    }

    [Fact]
    public async Task Logs_NeverContainToken()
    {
        var logs = new CaptureLogs();
        var token = W.NewToken();
        var secret = "secretcontent" + Guid.NewGuid().ToString("N");
        using var h = W.Harness(token, logging: b => { b.SetMinimumLevel(LogLevel.Trace); b.AddProvider(logs); });
        await using var c = await W.Writer(h, token);
        await W.Write(c, W.Entry("test:logs", secret));
        await W.Call(c, "get", new() { ["id"] = "test:logs" });
        await W.Call(c, "search", new() { ["query"] = secret });
        await using var wrong = await h.ConnectAsync("Bearer " + token + "x");
        await W.Call(wrong, "write_entry", W.Entry("test:logs", secret));

        Assert.NotEmpty(logs.Lines);
        Assert.Contains("/mcp", logs.All);   // the request log still works
        Assert.DoesNotContain(token, logs.All);
        Assert.DoesNotContain(secret, logs.All);
    }

    [Fact]
    public async Task Token_NeverInTheDatabaseOrResponses()
    {
        var token = W.NewToken();
        using var h = W.Harness(token);
        await using var c = await W.Writer(h, token);
        var id = await W.Write(c, W.Entry("test:db"));
        var (status, _) = await W.Call(c, "status");
        Assert.DoesNotContain(token, status);
        var (page, _) = await W.Call(c, "get", new() { ["id"] = "entry:" + id });
        Assert.DoesNotContain(token, page);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(h.StorePath)!))
        {
            using var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(s, System.Text.Encoding.Latin1);
            Assert.DoesNotContain(token, await r.ReadToEndAsync());
        }
    }

    [Fact]
    public async Task Write_ContainingTheToken_Refused_NothingStored()
    {
        var token = W.NewToken();
        using var h = W.Harness(token);
        await using var c = await W.Writer(h, token);
        foreach (var args in new[]
        {
            W.Entry("test:leak", "pasted by mistake: " + token),
            W.Entry("test:leak", more: new (string, object?)[] { ("field", "x"), ("value", token) }),
            W.Entry("test:leak", more: ("evidence", W.Desc("my token is " + token))),
        })
        {
            var (text, isError) = await W.Call(c, "write_entry", args);
            Assert.True(isError, text);
            Assert.DoesNotContain(token, text);
        }
        Assert.Contains("No entity", (await W.Call(c, "get", new() { ["id"] = "test:leak" })).Text);
    }

    /// <summary>JSON escapes + and / in stored evidence, so the check has to look at the text as sent.</summary>
    [Fact]
    public async Task Write_TokenWithJsonEscapedCharacters_StillRefused()
    {
        var token = "abc+def/ghi+" + W.NewToken();
        using var h = W.Harness(token);
        await using var c = await W.Writer(h, token);
        foreach (var args in new[]
        {
            W.Entry("test:leak", more: ("evidence", W.Desc("mine is " + token))),
            W.Entry("test:leak", more: ("evidence", new Dictionary<string, object?> { ["url"] = "https://example.org/?k=" + token })),
        })
        {
            var (text, isError) = await W.Call(c, "write_entry", args);
            Assert.True(isError, text);
            Assert.Contains("bearer token", text);
        }
    }

    [Fact]
    public async Task EnvUnset_WritesDisabled_ReadsWork()
    {
        var logs = new CaptureLogs();
        using var h = W.Harness("", logging: b => b.AddProvider(logs));
        await using var c = await h.ConnectAsync("Bearer " + new string('a', 40));
        var (text, isError) = await W.Call(c, "get", new() { ["id"] = Item });
        Assert.False(isError);
        Assert.Contains("Libram", text);
        Assert.True((await W.Call(c, "write_entry", W.Entry("test:auth"))).IsError);
        Assert.Contains("Writes: disabled", (await W.Call(c, "status")).Text);
        Assert.Contains(logs.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains("COA_MCP_BEARER_TOKEN"));
    }

    [Fact]
    public async Task EnvUnderThirtyTwoChars_WritesDisabled_LogsWarning()
    {
        var logs = new CaptureLogs();
        var shortToken = new string('k', 31);
        using var h = W.Harness(shortToken, logging: b => b.AddProvider(logs));
        await using var c = await W.Writer(h, shortToken);
        Assert.True((await W.Call(c, "write_entry", W.Entry("test:auth"))).IsError);
        Assert.Contains("Libram", (await W.Call(c, "get", new() { ["id"] = Item })).Text);
        Assert.Contains(logs.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains("COA_MCP_BEARER_TOKEN"));
        Assert.DoesNotContain(shortToken, logs.All);
    }

    [Fact]
    public async Task EnvExactlyThirtyTwoChars_Enabled()
    {
        var token = new string('k', 16) + new string('z', 16);
        using var h = W.Harness(token);
        await using var c = await W.Writer(h, token);
        await W.Write(c, W.Entry("test:auth"));
        Assert.Contains("Writes: enabled", (await W.Call(c, "status")).Text);
    }

    [Fact]
    public async Task Write_RateLimitedPerToken_NotPerIp()
    {
        var token = W.NewToken();
        using var h = W.Harness(token, new Dictionary<string, string?> { ["RateLimit:WritesPerMinute"] = "3" });
        for (var i = 0; i < 3; i++)
        {
            await using var c = await W.Writer(h, token, $"198.51.100.{i + 1}");
            await W.Write(c, W.Entry("test:rate"));
        }
        // A new address does not reset the token's allowance, and the refusal is a clean tool error.
        await using var other = await W.Writer(h, token, "198.51.100.77");
        var (text, isError) = await W.Call(other, "write_entry", W.Entry("test:rate"));
        Assert.True(isError);
        Assert.Contains("rate limit", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("per minute", text);
        // Reads are not on the write allowance.
        Assert.False((await W.Call(other, "get", new() { ["id"] = "test:rate" })).IsError);
    }

    /// <summary>Undo is the remedy for bad writes, so a runaway writer using up its allowance must not block it.</summary>
    [Fact]
    public async Task Undo_HasItsOwnAllowance()
    {
        var token = W.NewToken();
        using var h = W.Harness(token, new Dictionary<string, string?> { ["RateLimit:WritesPerMinute"] = "1", ["RateLimit:UndosPerMinute"] = "2" });
        await using var c = await W.Writer(h, token);
        var id = await W.Write(c, W.Entry("test:rate"));
        Assert.True((await W.Call(c, "write_entry", W.Entry("test:rate"))).IsError);

        var first = await W.Call(c, "undo_entry", new() { ["id"] = id, ["author"] = "Ann" });
        Assert.False(first.IsError, first.Text);
        Assert.False((await W.Call(c, "undo_entry", new() { ["id"] = id, ["author"] = "Ann" })).IsError);
        var (text, isError) = await W.Call(c, "undo_entry", new() { ["id"] = id, ["author"] = "Ann" });
        Assert.True(isError);
        Assert.Contains("rate limit", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The one test that uses the real environment variable. Every other harness injects its own token, so changing it here affects nothing else.</summary>
    [Fact]
    public async Task Token_ComesFromTheEnvironmentVariableOnly()
    {
        var token = W.NewToken();
        var settings = new Dictionary<string, string?> { [TokenAuth.EnvVar] = token };
        var before = Environment.GetEnvironmentVariable(TokenAuth.EnvVar);
        try
        {
            Environment.SetEnvironmentVariable(TokenAuth.EnvVar, null);
            using (var viaConfig = new ServerHarness(settings: settings, realTokenEnvironment: true))
            {
                await using var c = await W.Writer(viaConfig, token);
                Assert.True((await W.Call(c, "write_entry", W.Entry("test:env"))).IsError);
            }

            Environment.SetEnvironmentVariable(TokenAuth.EnvVar, token);
            using var viaEnv = new ServerHarness(realTokenEnvironment: true);
            await using var writer = await W.Writer(viaEnv, token);
            await W.Write(writer, W.Entry("test:env"));
        }
        finally { Environment.SetEnvironmentVariable(TokenAuth.EnvVar, before); }
    }

    /// <summary>The endpoint is public: a refused or malformed call is the caller's mistake, and must not fill the log with stack traces.</summary>
    [Fact]
    public async Task RefusedCalls_LogNoStackTrace_AndStayRefused()
    {
        var logs = new CaptureLogs();
        var token = W.NewToken();
        using var h = W.Harness(token, logging: b => b.AddProvider(logs));
        await using var anon = await h.ConnectAsync(null);
        await using var writer = await W.Writer(h, token);

        Assert.True((await W.Call(anon, "write_entry", W.Entry("test:quiet"))).IsError);
        Assert.True((await W.Call(anon, "undo_entry", new() { ["id"] = 1L, ["author"] = "Ann" })).IsError);
        Assert.True((await W.Call(anon, "no_such_tool", new())).IsError);
        Assert.True((await W.Call(anon, "get", new() { ["id"] = "" })).IsError);
        Assert.True((await W.Call(anon, "get", new())).IsError);
        Assert.True((await W.Call(anon, "get", new() { ["id"] = "item:1", ["entries_offset"] = "soon" })).IsError);
        Assert.True((await W.Call(writer, "write_entry", W.Entry("test:quiet", trust: "client"))).IsError);
        Assert.True((await W.Call(writer, "write_entry", W.Entry("test:quiet", more: ("position", "somewhere")))).IsError);

        Assert.Empty(logs.Lines.Where(l => l.Text.Contains("   at ", StringComparison.Ordinal)).Select(l => $"{l.Level} {l.Text[..Math.Min(200, l.Text.Length)]}"));
        var (refusal, refused) = await W.Call(anon, "write_entry", W.Entry("test:quiet"));
        Assert.True(refused);
        Assert.Contains("authorization", refusal);
        Assert.False(File.Exists(h.StorePath));
    }

    [Fact]
    public async Task Write_RateLimitPerDay_Applies()
    {
        var token = W.NewToken();
        using var h = W.Harness(token, new Dictionary<string, string?> { ["RateLimit:WritesPerDay"] = "2" });
        await using var c = await W.Writer(h, token);
        await W.Write(c, W.Entry("test:rate"));
        await W.Write(c, W.Entry("test:rate"));
        var (text, isError) = await W.Call(c, "write_entry", W.Entry("test:rate"));
        Assert.True(isError);
        Assert.Contains("per day", text);
    }

    [Fact]
    public async Task IpRateLimit_StillAppliesToTokenGuessing()
    {
        using var h = W.Harness(W.NewToken(), new Dictionary<string, string?> { ["RateLimit:PerMinute"] = "2" });
        var http = h.Http();
        var last = System.Net.HttpStatusCode.OK;
        for (var i = 0; i < 4; i++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer guess" + i);
            req.Headers.Add("X-Test-Peer", "203.0.113.50");
            req.Headers.Add("Accept", "application/json, text/event-stream");
            using var resp = await http.SendAsync(req);
            last = resp.StatusCode;
        }
        Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, last);
    }
}
