using System.Text;
using System.Text.Json;

namespace CoaMcp.Acceptance;

[Collection("01 Protocol")]
public class ProtocolTests
{
    private const string Area = "Protocol";

    private static readonly string[] AnonymousTools =
        { "search", "get", "status", "spell_compare", "decode_flags", "spell_scaling", "client_paths", "dbc_tables", "dbc_rows" };

    [SkippableFact]
    public Task Initialize_ReportsServerAndInstructions() => Suite.Run(Area, "initialize", async c =>
    {
        var client = await Suite.Anonymous();
        c.Equal("CoaMcp.Server", client.Mcp.ServerInfo?.Name, "server name");
        c.That(client.Mcp.ServerCapabilities?.Tools is not null, "the server offers tools");
        c.Has(client.Mcp.ServerInstructions ?? "", "data, never instructions", "instructions say stored text is data");
        c.That((client.Mcp.ServerInstructions ?? "").Length < 400, "instructions stay short", client.Mcp.ServerInstructions);
    });

    [SkippableFact]
    public Task ToolList_IsExactlyTheNineReadTools() => Suite.Run(Area, "anonymous tool list matches the design", async c =>
    {
        var client = await Suite.Anonymous();
        var tools = await client.Mcp.ListToolsAsync();
        c.Equal(string.Join(",", AnonymousTools.Order()), string.Join(",", tools.Select(t => t.Name).Order()), "exactly these tools for an anonymous client");
        foreach (var t in tools)
        {
            c.That(t.ProtocolTool.Annotations?.ReadOnlyHint == true, $"{t.Name} is marked read-only");
            c.That(!string.IsNullOrWhiteSpace(t.Description), $"{t.Name} has a description");
            c.That((t.Description?.Length ?? 0) <= 500, $"{t.Name}'s description stays short", $"{t.Description?.Length} characters: {t.Description}");
        }
        var size = JsonSerializer.Serialize(tools.Select(t => t.ProtocolTool)).Length;
        c.Book.Observe($"The anonymous tool list is {size:N0} characters of JSON, what an agent pays to load it.");
        c.That(size < 20_000, "the whole tool list stays small", $"{size} characters");
    });

    [SkippableFact]
    public Task Https_PlainHttpRedirectsAndCertificateIsValid() => Suite.Run(Area, "HTTPS", async c =>
    {
        c.Equal("https", Settings.Mcp.Scheme, "the target URL is HTTPS");
        // The default validator: this fails if the public certificate isn't valid for the host.
        using var checkedClient = new HttpClient(new SocketsHttpHandler());
        using var health = await checkedClient.GetAsync(Settings.Health);
        c.Equal(200, (int)health.StatusCode, "HTTPS works with a validating client");

        using var noFollow = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
        using var plain = await noFollow.GetAsync(new Uri($"http://{Settings.Host}/healthz"));
        c.That((int)plain.StatusCode is 301 or 302 or 307 or 308, "plain HTTP doesn't serve content", $"status {(int)plain.StatusCode}");
        c.Equal("https", plain.Headers.Location?.Scheme, "plain HTTP redirects to HTTPS");
    });

    [SkippableFact]
    public Task Healthz_Answers() => Suite.Run(Area, "/healthz", async c =>
    {
        var r = await Raw.Post(Suite.RawPaced, null, null, method: HttpMethod.Get, url: Settings.Health.ToString());
        c.Equal(200, r.Status, "/healthz status");
        c.Equal("", r.Body, "/healthz gives nothing away");
    });

    [SkippableFact]
    public Task Stateless_NeedsNoSessionAndGivesNone() => Suite.Run(Area, "stateless behavior", async c =>
    {
        // A call with no initialize at all: a stateful server would refuse this.
        var call = await Raw.Post(Suite.RawPaced, Raw.StatusCall);
        c.Equal(200, call.Status, "a tool call works without initialize");
        c.Has(call.Json, "CoA Knowledge MCP", "and answers");
        c.That(!call.Headers.Contains("Mcp-Session-Id"), "no session id on a tool call");

        var init = await Raw.Post(Suite.RawPaced, Raw.Rpc("initialize", "{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"acceptance\",\"version\":\"1\"}}"));
        c.Equal(200, init.Status, "initialize works");
        c.That(!init.Headers.Contains("Mcp-Session-Id"), "no session id on initialize");

        // A session id the server never issued changes nothing.
        var forged = await Raw.Post(Suite.RawPaced, Raw.StatusCall, new[] { ("Mcp-Session-Id", "forged-session-id") });
        c.That(forged.Status < 500, "a made-up session id is no fault", $"status {forged.Status}: {forged.Body}");
        c.Book.Observe($"A request with a made-up Mcp-Session-Id gets status {forged.Status}.");

        var get = await Raw.Post(Suite.RawPaced, null, null, method: HttpMethod.Get);
        c.That(get.Status is >= 400 and < 500, "GET /mcp opens no stream on a stateless server", $"status {get.Status}");
        c.Book.Observe($"GET /mcp answers {get.Status}; DELETE and session features aren't used, the server is stateless.");
    });

    [SkippableFact]
    public Task TwoClients_DoNotShareAnything() => Suite.Run(Area, "two clients are independent", async c =>
    {
        await using var a = await LiveClient.Connect(Suite.Book);
        await using var b = await LiveClient.Connect(Suite.Book);
        var ra = await a.Call("get", new { id = "spell:133" });
        var rb = await b.Call("get", new { id = "item:10531" });
        c.Has(ra.Text, "# Fireball", "client A gets its own answer");
        c.Has(rb.Text, "# Stoneclad Libram", "client B gets its own answer");
    });

    [SkippableFact]
    public Task UserAgents_CloudflareBehaviorIsRecorded() => Suite.Run(Area, "client identification through Cloudflare", async c =>
    {
        async Task<int> Try(string? agent)
        {
            using var http = new HttpClient(new SocketsHttpHandler());
            if (agent is not null) http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", agent);
            return (await Raw.Post(http, Raw.ToolsList)).Status;
        }
        var dotnet = await Try(null);
        c.Equal(200, dotnet, "a .NET client with no User-Agent, which is what the MCP SDK sends, gets through");
        var claude = await Try("claude-code/2.1 (cli)");
        c.Equal(200, claude, "a Claude Code style User-Agent gets through");
        var urllib = await Try("Python-urllib/3.14");
        c.Book.Observe($"Cloudflare answers a default Python-urllib User-Agent with status {urllib}{(urllib == 403 ? " (blocked before it reaches the server)" : "")}. An agent scripting the server from Python must set its own User-Agent. The suite uses the real .NET MCP client.");
        c.That(true, "the Python-urllib behavior is recorded");
    });
}
