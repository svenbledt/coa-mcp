using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace CoaMcp.Acceptance;

/// <summary>Time spent waiting on the suite's own pacing during one call, so timings show the server and not the pacer.</summary>
public sealed class PaceWait
{
    public long Ms;
    public static readonly AsyncLocal<PaceWait?> Current = new();
}

/// <summary>
/// Keeps this process under the server's limit of 120 requests a minute, across every paced client. A 429 is waited out and retried:
/// outside the limit test it isn't a failure, only a count in the report. The limit test uses an unpaced client on purpose.
/// </summary>
public sealed class PacingHandler : DelegatingHandler
{
    public const int PerMinute = 100;
    private const int MaxRetries = 6;
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(61);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Queue<DateTime> Sent = new();

    private readonly ReportBook _book;

    public PacingHandler(ReportBook book, HttpMessageHandler inner) : base(inner) => _book = book;

    private static async Task Acquire(CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        await Gate.WaitAsync(ct);
        try
        {
            while (true)
            {
                var now = DateTime.UtcNow;
                while (Sent.Count > 0 && now - Sent.Peek() > Window) Sent.Dequeue();
                if (Sent.Count < PerMinute) { Sent.Enqueue(now); break; }
                await Task.Delay(Sent.Peek() + Window - now + TimeSpan.FromMilliseconds(50), ct);
            }
        }
        finally
        {
            Gate.Release();
            if (PaceWait.Current.Value is { } w) Interlocked.Add(ref w.Ms, watch.ElapsedMilliseconds);
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(ct);
        var contentHeaders = request.Content?.Headers.ToList();
        for (var attempt = 0; ; attempt++)
        {
            await Acquire(ct);
            Interlocked.Increment(ref _book.RequestsSent);
            using var copy = Copy(request, body, contentHeaders);
            var response = await base.SendAsync(copy, ct);
            if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt >= MaxRetries) return response;

            Interlocked.Increment(ref _book.RateLimitWaits);
            var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(61);
            response.Dispose();
            if (PaceWait.Current.Value is { } w) Interlocked.Add(ref w.Ms, (long)wait.TotalMilliseconds);
            await Task.Delay(wait, ct);
        }
    }

    private static HttpRequestMessage Copy(HttpRequestMessage r, byte[]? body, List<KeyValuePair<string, IEnumerable<string>>>? contentHeaders)
    {
        var copy = new HttpRequestMessage(r.Method, r.RequestUri) { Version = r.Version, VersionPolicy = r.VersionPolicy };
        foreach (var h in r.Headers) copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
        if (body is not null)
        {
            copy.Content = new ByteArrayContent(body);
            foreach (var h in contentHeaders!) copy.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        return copy;
    }
}

public sealed record RawReply(int Status, string Body, HttpResponseHeaders Headers, long Ms)
{
    /// <summary>The JSON text of an SSE or plain JSON answer.</summary>
    public string Json
    {
        get
        {
            var data = Body.Split('\n').Where(l => l.StartsWith("data:", StringComparison.Ordinal)).Select(l => l[5..].Trim()).ToList();
            return data.Count > 0 ? data[^1] : Body;
        }
    }
}

/// <summary>Plain HTTP against the server, for what a well-behaved MCP client would never send.</summary>
public static class Raw
{
    public const string UserAgent = "CoaMcp.Acceptance/1.0";

    public static HttpClient Paced(ReportBook book, TimeSpan? timeout = null)
    {
        // Latin-1 headers, so a credential with non-ASCII characters really reaches the server rather than stopping in the client.
        var http = new HttpClient(new PacingHandler(book, new SocketsHttpHandler { RequestHeaderEncodingSelector = (_, _) => System.Text.Encoding.Latin1 })) { Timeout = timeout ?? TimeSpan.FromSeconds(120) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return http;
    }

    /// <summary>For the limit test: no pacing and no retry.</summary>
    public static HttpClient Unpaced()
    {
        var http = new HttpClient(new SocketsHttpHandler()) { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return http;
    }

    /// <summary>Connects straight to the origin, past Cloudflare, under the public host name. The origin's certificate isn't one a client would trust, which only this pinned test ignores.</summary>
    public static HttpClient PinnedToOrigin()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, ct) =>
            {
                var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(IPAddress.Parse(Settings.OriginIp), context.DnsEndPoint.Port, ct);
                    return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return http;
    }

    public static string Rpc(string method, string paramsJson = "{}", int id = 1) =>
        $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"{method}\",\"params\":{paramsJson}}}";

    public static string StatusCall => Rpc("tools/call", "{\"name\":\"status\",\"arguments\":{}}");
    public static string ToolsList => Rpc("tools/list");

    public static async Task<RawReply> Post(HttpClient http, byte[]? body, string? contentType = "application/json", IEnumerable<(string, string)>? headers = null, string accept = "application/json, text/event-stream", HttpMethod? method = null, string? url = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, url ?? Settings.Url);
        if (accept.Length > 0) request.Headers.TryAddWithoutValidation("Accept", accept);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            if (contentType is not null) request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }
        foreach (var (k, v) in headers ?? Array.Empty<(string, string)>()) request.Headers.TryAddWithoutValidation(k, v);
        var watch = Stopwatch.StartNew();
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return new RawReply((int)response.StatusCode, text, response.Headers, watch.ElapsedMilliseconds);
    }

    public static Task<RawReply> Post(HttpClient http, string json, IEnumerable<(string, string)>? headers = null) =>
        Post(http, System.Text.Encoding.UTF8.GetBytes(json), "application/json", headers);
}
