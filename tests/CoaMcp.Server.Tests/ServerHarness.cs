using Microsoft.AspNetCore.Builder;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace CoaMcp.Server.Tests;

/// <summary>Sets the connection's remote address from the X-Test-Peer header, the way a real socket would.</summary>
internal sealed class PeerFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((ctx, n) =>
        {
            if (ctx.Request.Headers.TryGetValue("X-Test-Peer", out var peer))
                ctx.Connection.RemoteIpAddress = IPAddress.Parse(peer.ToString());
            return n();
        });
        next(app);
    };
}

/// <summary>The real server on an in-memory test host, reading fixture layers.</summary>
public sealed class ServerHarness : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    /// <summary>The flat folder the fixture layers were built in.</summary>
    public string LayerDir { get; }
    /// <summary>The data root the server reads: layers/&lt;name&gt;/&lt;version&gt;.sqlite and current.json.</summary>
    public string Root { get; }

    public ServerHarness(string? layerDir = null, IDictionary<string, string?>? settings = null, Action<ILoggingBuilder>? logging = null,
        string? token = null, bool realTokenEnvironment = false)
    {
        LayerDir = layerDir ?? TestLayers.Build();
        Root = TestLayers.Publish(LayerDir);
        // Fixture folders are the tests' own temp data; remove them when the harness goes.
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Layers:Root", Root);
            b.UseSetting("RateLimit:PerMinute", "100000");
            foreach (var (k, v) in settings ?? new Dictionary<string, string?>()) b.UseSetting(k, v);
            b.ConfigureServices(s => s.AddSingleton<IStartupFilter, PeerFilter>());
            if (logging is not null) b.ConfigureLogging(logging);
            // The server reads its token from the process environment, which tests running side by side share. So each test gets its own as a service.
            if (!realTokenEnvironment) b.ConfigureTestServices(t => t.AddSingleton(new TokenAuth(token)));
        });
    }

    public HttpClient Http() => _factory.CreateClient();

    public IServiceProvider Services => _factory.Services;

    /// <summary>Starts the server on a real socket, because only Kestrel enforces body size limits. Returns its address.</summary>
    public Uri StartOnKestrel()
    {
        _factory.UseKestrel(0);
        _factory.StartServer();
        return new Uri(_factory.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.First());
    }

    /// <summary>Where the writable store lives under this harness's data root.</summary>
    public string StorePath => Path.Combine(Root, "writable", "store.sqlite");

    /// <summary>A client that sends this exact Authorization value (null for none), as this peer address.</summary>
    public async Task<McpClient> ConnectAsync(string? authorization, string? peer = null)
    {
        var http = _factory.CreateClient();
        if (authorization is not null) http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authorization);
        if (peer is not null) http.DefaultRequestHeaders.TryAddWithoutValidation("X-Test-Peer", peer);
        return await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
        }, http, loggerFactory: null, ownsHttpClient: true));
    }

    public async Task<McpClient> ConnectAsync() =>
        await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
        }, _factory.CreateClient(), loggerFactory: null, ownsHttpClient: true));

    public static async Task<(string Text, bool IsError)> CallAsync(McpClient client, string tool, Dictionary<string, object?>? args = null)
    {
        var r = await client.CallToolAsync(tool, args ?? new Dictionary<string, object?>());
        return (string.Join("\n", r.Content.OfType<TextContentBlock>().Select(c => c.Text)), r.IsError == true);
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var d in new[] { LayerDir, Root })
        {
            try { Directory.Delete(d, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* temp data; the OS cleans it eventually */ }
        }
    }
}
