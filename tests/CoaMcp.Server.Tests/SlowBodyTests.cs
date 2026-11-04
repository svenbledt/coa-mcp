using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CoaMcp.Server.Tests;

/// <summary>
/// Requests that hold /mcp's capacity without finishing: a body sent a few bytes at a time, as an anonymous caller can at will.
/// These run on a real socket, because only Kestrel reads a body slowly. The client key comes from the test peer hook.
/// </summary>
public sealed class SlowBodyTests
{
    private const string ToolsList = """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""";
    private const string SlowPeer = "203.0.113.10";
    private const string OtherPeer = "203.0.113.20";

    /// <summary>A POST that claims a body under the size limit and sends a little of it, fast enough that Kestrel's minimum data rate doesn't end it.</summary>
    private sealed class SlowPost : IAsyncDisposable
    {
        private readonly TcpClient _tcp = new();
        private readonly CancellationTokenSource _stop = new();
        private Task _dribble = Task.CompletedTask;
        private NetworkStream Stream => _tcp.GetStream();

        public static async Task<SlowPost> Start(Uri server, string peer)
        {
            var post = new SlowPost();
            await post._tcp.ConnectAsync(server.Host, server.Port);
            var head = $"POST /mcp HTTP/1.1\r\nHost: localhost\r\nX-Test-Peer: {peer}\r\nContent-Type: application/json\r\nAccept: application/json, text/event-stream\r\nContent-Length: 60000\r\n\r\n";
            await post.Stream.WriteAsync(Encoding.ASCII.GetBytes(head));
            post._dribble = post.Dribble();
            return post;
        }

        private async Task Dribble()
        {
            var chunk = Encoding.ASCII.GetBytes(new string(' ', 100));
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    await Stream.WriteAsync(chunk, _stop.Token);
                    await Task.Delay(100, _stop.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { /* refused, or the test is over */ }
        }

        /// <summary>The response's status line and body. Fails if none arrives in time.</summary>
        public async Task<(string Status, string Body)> Response(TimeSpan within)
        {
            using var timeout = new CancellationTokenSource(within);
            var text = new StringBuilder();
            var buffer = new byte[4096];
            while (true)
            {
                var n = await Stream.ReadAsync(buffer, timeout.Token);
                text.Append(Encoding.UTF8.GetString(buffer, 0, n));
                var s = text.ToString();
                var end = s.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (end >= 0)
                {
                    var length = s[..end].Split("\r\n").First(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1].Trim();
                    if (s.Length >= end + 4 + int.Parse(length)) return (s[..s.IndexOf("\r\n", StringComparison.Ordinal)], s[(end + 4)..]);
                }
                if (n == 0) throw new IOException("the connection closed before a whole response: " + s);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _dribble;
            _tcp.Dispose();
            _stop.Dispose();
        }
    }

    private static async Task<(HttpStatusCode Code, string Body)> Call(HttpClient http, string peer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(ToolsList, Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        request.Headers.TryAddWithoutValidation("X-Test-Peer", peer);
        using var response = await http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static void AssertJsonRpcError(string body, string says)
    {
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("2.0", doc.RootElement.GetProperty("jsonrpc").GetString());
        var error = doc.RootElement.GetProperty("error");
        Assert.True(error.GetProperty("code").GetInt32() < 0);
        var message = error.GetProperty("message").GetString()!;
        Assert.InRange(message.Length, 1, 200);
        Assert.Contains(says, message);
    }

    private static (ServerHarness Harness, HttpClient Http, Uri Server, CaptureLogs Logs) Start(params (string Key, string Value)[] settings)
    {
        var logs = new CaptureLogs();
        var harness = new ServerHarness(settings: settings.ToDictionary(s => s.Key, s => (string?)s.Value), logging: b => b.AddProvider(logs));
        var server = harness.StartOnKestrel();
        return (harness, new HttpClient { BaseAddress = server }, server, logs);
    }

    private static void AssertNoErrorLogged(CaptureLogs logs)
    {
        var errors = logs.Lines.Where(l => l.Level >= LogLevel.Error).Select(l => l.Text).ToList();
        Assert.True(errors.Count == 0, "logged at error level:\n" + string.Join("\n", errors));
    }

    [Fact]
    public async Task SlowBodies_FromOneClient_DontBlockAnother()
    {
        var (h, http, server, logs) = Start(("Concurrency:MaxRunning", "4"), ("Concurrency:MaxQueued", "0"), ("Concurrency:MaxPerClient", "2"), ("Request:BodySeconds", "60"));
        using (h)
        using (http)
        {
            var slow = new List<SlowPost>();
            for (var i = 0; i < 6; i++)
            {
                slow.Add(await SlowPost.Start(server, SlowPeer));
                await Task.Delay(100);
            }

            // The first two hold the client's permits. The rest are refused at once, without taking anything from other clients.
            foreach (var extra in slow.Skip(2))
            {
                var (status, body) = await extra.Response(TimeSpan.FromSeconds(3));
                Assert.Contains(" 429 ", status);
                AssertJsonRpcError(body, "Too many");
            }

            var (code, other) = await Call(http, OtherPeer);
            Assert.Equal(HttpStatusCode.OK, code);
            Assert.Contains("\"tools\"", other);

            foreach (var s in slow) await s.DisposeAsync();
            await Task.Delay(200);
            AssertNoErrorLogged(logs);
        }
    }

    [Fact]
    public async Task PerClientCap_DefaultsToEight()
    {
        var (h, http, server, _) = Start(("Request:BodySeconds", "60"));
        using (h)
        using (http)
        {
            var slow = new List<SlowPost>();
            for (var i = 0; i < 8; i++) slow.Add(await SlowPost.Start(server, SlowPeer));
            await Task.Delay(300);

            await using var ninth = await SlowPost.Start(server, SlowPeer);
            var (status, _) = await ninth.Response(TimeSpan.FromSeconds(3));
            Assert.Contains(" 429 ", status);

            foreach (var s in slow) await s.DisposeAsync();
        }
    }

    [Fact]
    public async Task SlowBody_IsCutOffCleanly_NoErrorLog()
    {
        var (h, http, server, logs) = Start(("Request:BodySeconds", "1"), ("Concurrency:MaxPerClient", "1"));
        using (h)
        using (http)
        {
            var clock = Stopwatch.StartNew();
            await using (var slow = await SlowPost.Start(server, SlowPeer))
            {
                var (status, body) = await slow.Response(TimeSpan.FromSeconds(4));
                Assert.Contains(" 408 ", status);
                AssertJsonRpcError(body, "too long");
                Assert.InRange(clock.Elapsed.TotalSeconds, 0.8, 4);
            }

            await Task.Delay(200);
            AssertNoErrorLogged(logs);

            var (code, _) = await Call(http, SlowPeer);   // its one permit came back
            Assert.Equal(HttpStatusCode.OK, code);
        }
    }

    [Fact]
    public async Task QueuedRequest_GetsCleanBusy_WithinFiveSeconds()
    {
        var (h, http, server, logs) = Start(("Concurrency:MaxRunning", "1"), ("Concurrency:MaxQueued", "5"), ("Request:BodySeconds", "60"));
        using (h)
        using (http)
        {
            await using var slow = await SlowPost.Start(server, SlowPeer);
            await Task.Delay(300);

            var clock = Stopwatch.StartNew();
            using var cut = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var (code, body) = await Call(http, OtherPeer).WaitAsync(cut.Token);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, code);
            AssertJsonRpcError(body, "busy");
            Assert.InRange(clock.Elapsed.TotalSeconds, 4, 7);
            AssertNoErrorLogged(logs);
        }
    }

    [Fact]
    public async Task OrdinaryRequest_IsUntouched_ByTheBodyLimit()
    {
        var (h, http, _, logs) = Start(("Request:BodySeconds", "1"));
        using (h)
        using (http)
        {
            var (code, body) = await Call(http, OtherPeer);
            Assert.Equal(HttpStatusCode.OK, code);
            Assert.Contains("\"tools\"", body);
            AssertNoErrorLogged(logs);
        }
    }
}
