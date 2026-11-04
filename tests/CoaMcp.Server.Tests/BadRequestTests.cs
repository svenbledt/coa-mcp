using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace CoaMcp.Server.Tests;

/// <summary>
/// Requests that never become a tool call. They run on a real socket, because only Kestrel turns an unhandled exception into a 500 and logs it,
/// and an anonymous caller can send them at will, so each must get a clean refusal and write no error to the log.
/// </summary>
public sealed class BadRequestFixture : IDisposable
{
    internal CaptureLogs Logs { get; } = new();
    public ServerHarness Harness { get; }
    public HttpClient Http { get; }

    public BadRequestFixture()
    {
        Harness = new ServerHarness(logging: b => b.AddProvider(Logs));
        Http = new HttpClient { BaseAddress = Harness.StartOnKestrel() };
    }

    public void Dispose()
    {
        Http.Dispose();
        Harness.Dispose();
    }
}

public class BadRequestTests : IClassFixture<BadRequestFixture>
{
    private const string ToolsList = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{}}";

    private readonly BadRequestFixture _f;
    public BadRequestTests(BadRequestFixture f) => _f = f;

    private async Task<(HttpStatusCode Code, string Body, string? ContentType)> Post(byte[]? body, string? contentType)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        if (body is not null)
        {
            req.Content = new ByteArrayContent(body);
            if (contentType is not null) req.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }
        using var resp = await _f.Http.SendAsync(req);
        return (resp.StatusCode, await resp.Content.ReadAsStringAsync(), resp.Content.Headers.ContentType?.MediaType);
    }

    private static void AssertClean(string body)
    {
        foreach (var bad in new[] { "Exception", "   at ", "/src/", "CoaMcp.", "Microsoft." })
            Assert.DoesNotContain(bad, body);
    }

    private static void AssertJsonRpcError(string body)
    {
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("2.0", doc.RootElement.GetProperty("jsonrpc").GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("id").ValueKind);
        Assert.True(doc.RootElement.GetProperty("error").GetProperty("code").GetInt32() < 0);
        Assert.InRange(doc.RootElement.GetProperty("error").GetProperty("message").GetString()!.Length, 1, 200);
    }

    private async Task AssertNoErrorLogged(Func<Task> act)
    {
        var before = _f.Logs.Lines.Count(l => l.Level >= LogLevel.Error);
        await act();
        await Task.Delay(50);   // Kestrel logs after the response is finished
        var errors = _f.Logs.Lines.Where(l => l.Level >= LogLevel.Error).Skip(before).Select(l => l.Text).ToList();
        Assert.True(errors.Count == 0, "logged at error level:\n" + string.Join("\n", errors));
    }

    /// <summary>The SDK reads these bodies itself, and throws for them instead of answering.</summary>
    public static TheoryData<string, string?, string?> NoUsableContentType => new()
    {
        { "empty body, no content type", null, null },
        { "valid body, no content type", ToolsList, null },
        { "unknown charset", ToolsList, "application/json; charset=no-such-charset" },
        { "wrong content type", ToolsList, "text/plain" },
        { "XML", "<?xml version=\"1.0\"?><a/>", "application/xml" },
        { "unparseable content type", ToolsList, "application/json; ===" },
        { "starts like JSON", ToolsList, "application/jsonx" },
        { "not a content type", ToolsList, "===" },
    };

    [Theory]
    [MemberData(nameof(NoUsableContentType))]
    public async Task NoUsableContentType_Gets400WithAShortJsonRpcError_AndNoErrorLog(string label, string? body, string? contentType)
    {
        await AssertNoErrorLogged(async () =>
        {
            var r = await Post(body is null ? null : Encoding.UTF8.GetBytes(body), contentType);
            Assert.True(r.Code == HttpStatusCode.BadRequest, $"{label}: status {(int)r.Code}, body: {r.Body}");
            Assert.Equal("application/json", r.ContentType);
            AssertJsonRpcError(r.Body);
            AssertClean(r.Body);
        });
    }

    public static TheoryData<string, string, string> BadBodies => new()
    {
        { "malformed JSON", "this is not json", "application/json" },
        { "truncated JSON", "{\"jsonrpc\":\"2.0\",\"id\":1,", "application/json" },
        { "empty body with a JSON content type", "", "application/json" },
        { "empty JSON array", "[]", "application/json" },
        { "JSON null", "null", "application/json" },
        { "JSON number", "123", "application/json" },
        { "empty charset", ToolsList, "application/json; charset=\"\"" },
        { "UTF-16 charset on UTF-8 bytes", ToolsList, "application/json; charset=utf-16" },
    };

    [Theory]
    [MemberData(nameof(BadBodies))]
    public async Task BadBodies_GetAClean4xx_AndNoErrorLog(string label, string body, string contentType)
    {
        await AssertNoErrorLogged(async () =>
        {
            var r = await Post(Encoding.UTF8.GetBytes(body), contentType);
            Assert.True((int)r.Code is >= 400 and < 500, $"{label}: status {(int)r.Code}, body: {r.Body}");
            AssertClean(r.Body);
        });
    }

    [Fact]
    public async Task InvalidUtf8_GetsAClean4xx_AndNoErrorLog()
    {
        await AssertNoErrorLogged(async () =>
        {
            var r = await Post(new byte[] { 0x7B, 0x22, 0xC3, 0x28, 0xFF, 0xFE, 0x22, 0x7D }, "application/json");
            Assert.True((int)r.Code is >= 400 and < 500, $"status {(int)r.Code}, body: {r.Body}");
            AssertClean(r.Body);
        });
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("APPLICATION/JSON")]
    [InlineData("application/vnd.test+json")]
    public async Task NormalRequests_StillWork(string contentType)
    {
        var r = await Post(Encoding.UTF8.GetBytes(ToolsList), contentType);
        Assert.Equal(HttpStatusCode.OK, r.Code);
        Assert.Contains("\"search\"", r.Body);
    }

    [Fact]
    public async Task RealClient_StillWorks()
    {
        await using var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(_f.Http.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
        }));
        Assert.NotEmpty(await client.ListToolsAsync());
    }

    /// <summary>The check for a missing content type mustn't become a way round the body limit.</summary>
    [Theory]
    [InlineData("application/json")]
    [InlineData(null)]
    public async Task OversizedBody_IsStillRefused(string? contentType)
    {
        var body = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"search\",\"arguments\":{\"query\":\"" + new string('o', 70_000) + "\"}}}");
        var r = await Post(body, contentType);
        Assert.True((int)r.Code is 400 or 413, $"status {(int)r.Code}");
        Assert.DoesNotContain("\"result\"", r.Body);
    }

    [Fact]
    public async Task OtherPaths_AreNotTouched()
    {
        using var get = await _f.Http.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        using var post = await _f.Http.PostAsync("/nothing-here", null);
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
    }
}

/// <summary>Null or empty items in a tool's array and object arguments are bad input: a clean refusal, never a fault.</summary>
public class NullArgumentTests : IClassFixture<ReadFixture>
{
    private const string Fault = "An error occurred invoking";

    private readonly ReadFixture _f;
    public NullArgumentTests(ReadFixture f) => _f = f;

    private static Dictionary<string, object?> A(params (string, object?)[] pairs) => pairs.ToDictionary(p => p.Item1, p => p.Item2);

    public static TheoryData<string?[]> EmptySectionNames => new()
    {
        new string?[] { null },
        new string?[] { "" },
        new string?[] { "   " },
        new string?[] { "effects", null },
        new string?[] { "effects", "" },
    };

    [Theory]
    [MemberData(nameof(EmptySectionNames))]
    public async Task Get_NullOrEmptySectionName_IsAnError(string?[] sections)
    {
        foreach (var id in new[] { "spell:133", "entry:1", "no:such", "dbc:Spell", "dbc:Spell:133", "path:DBFilesClient/Spell.dbc" })
        {
            var (text, isError) = await _f.Call("get", A(("id", id), ("sections", sections)));
            Assert.True(isError, $"{id}: {text}");
            Assert.Contains("section name is empty", text);
            Assert.DoesNotContain(Fault, text);
        }
    }

    [Fact]
    public async Task Get_RealSections_StillWork()
    {
        var (text, isError) = await _f.Call("get", A(("id", "spell:133"), ("sections", Array.Empty<string>())));
        Assert.False(isError, text);
        var (named, namedError) = await _f.Call("get", A(("id", "item:10531"), ("sections", new[] { "nope" })));
        Assert.False(namedError, named);
    }

    public static TheoryData<string, object?> DbcRowsArguments => new()
    {
        { "where", new object?[] { null } },
        { "where", new object?[] { new { } } },
        { "where", new object?[] { new { field = (string?)null, op = "=", value = 1 } } },
        { "where", new object?[] { new { field = "", op = "=", value = 1 } } },
        { "where", new object?[] { new { field = "ID", op = (string?)null, value = 1 } } },
        { "where", new object?[] { new { field = "ID", op = "", value = 1 } } },
        { "where", new object?[] { new { field = "ID", op = "=" } } },
        { "where", new object?[] { new { field = "ID", op = "=", value = (object?)null } } },
        { "where", new object?[] { 5 } },
        { "where", new object?[] { "ID = 1" } },
        { "where", new object?[] { new object?[] { null } } },
        { "columns", new string?[] { null } },
        { "columns", new string?[] { "" } },
        { "columns", new string?[] { "ID", null } },
        { "columns", new object?[] { 5 } },
    };

    [Theory]
    [MemberData(nameof(DbcRowsArguments))]
    public async Task DbcRows_NullOrEmptyItems_AreAnError(string name, object? value)
    {
        var (text, isError) = await _f.Call("dbc_rows", A(("table", "Spell"), (name, value)));
        Assert.True(isError, text);
        Assert.DoesNotContain(Fault, text);
    }

    /// <summary>A token holder's object arguments, behind the same care: each is refused cleanly and stores nothing.</summary>
    [Fact]
    public async Task WriteEntry_NullOrEmptyObjectArguments_AreAnError()
    {
        var token = W.NewToken();
        using var h = W.Harness(token);
        await using var c = await W.Writer(h, token);

        Dictionary<string, object?> Write(string name, object? value)
        {
            var args = A(("author", "Ann"), ("subject", "test:nulls"), ("kind", "note"), ("trust", "observed"), ("content", "x"), ("evidence", new { description = "seen" }));
            args[name] = value;
            return args;
        }

        foreach (var (name, value) in new (string, object?)[]
                 {
                     ("evidence", new { }), ("evidence", new { description = (string?)null, url = (string?)null }), ("evidence", new[] { "x" }), ("evidence", "x"),
                     ("position", new { }), ("position", new { map = (string?)null }), ("position", new { map = "", x = 1 }), ("position", new[] { 1 }),
                     ("field", new string?[] { null }), ("value", new string?[] { null }), ("realm", new string?[] { null }),
                 })
        {
            var (text, isError) = await ServerHarness.CallAsync(c, "write_entry", Write(name, value));
            Assert.True(isError, $"{name}: {text}");
            Assert.DoesNotContain(Fault, text);
            Assert.DoesNotContain("Stored entry", text);
        }
    }

    [Theory]
    [InlineData("search", "query")]
    [InlineData("dbc_tables", "filter")]
    [InlineData("client_paths", "prefix")]
    public async Task ArrayWhereAStringBelongs_IsAnError(string tool, string name)
    {
        var (text, isError) = await _f.Call(tool, A((name, new string?[] { null })));
        Assert.True(isError, text);
        Assert.DoesNotContain(Fault, text);
    }
}
