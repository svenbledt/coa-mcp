using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;

namespace CoaMcp.Acceptance;

/// <summary>
/// 20 concurrent clients for 2 minutes of mixed reads. The real per-client limit applies, by design, so they run in bursts that stay inside
/// 120 a minute from this one address, plus a small run from a second address at the same time. This shows how the server answers concurrent reads, not its ceiling.
/// </summary>
[Collection("17 Load")]
public class LoadTests
{
    private const string Area = "Load";
    private const int Clients = 20;
    private static readonly TimeSpan Duration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan BurstEvery = TimeSpan.FromSeconds(11);   // 20 requests every 11 s is 109 a minute

    /// <summary>Counts what comes back; waits for nothing. A 429 here would be a real result, so it's reported as such.</summary>
    private sealed class CountingHandler : DelegatingHandler
    {
        public static long Sent, Limited, Faults;

        public CountingHandler() : base(new SocketsHttpHandler()) { }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Sent);
            var response = await base.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests) Interlocked.Increment(ref Limited);
            if ((int)response.StatusCode >= 500) Interlocked.Increment(ref Faults);
            return response;
        }
    }

    private static readonly (string Tool, Func<Random, object> Args)[] Mix =
    {
        ("search", r => new { query = new[] { "fireball", "sword", "barbarian", "molten", "frost" }[r.Next(5)], limit = 10 }),
        ("get", r => new { id = "spell:" + new[] { 133, 116, 1449, 2136, 5143 }[r.Next(5)] }),
        ("get", r => new { id = "item:" + new[] { 10531, 10012 }[r.Next(2)] }),
        ("status", _ => new { }),
        ("spell_compare", r => new { idA = "133", idB = new[] { "143", "145", "3140" }[r.Next(3)] }),
        ("decode_flags", r => new { kind = "ProcFlags", value = r.Next(1, 1 << 20).ToString(CultureInfo.InvariantCulture) }),
        ("spell_scaling", r => new { id = "133", level = (long)r.Next(1, 101) }),
        ("client_paths", r => new { prefix = new[] { "DBFilesClient/", "Interface/Icons/", "Spells/" }[r.Next(3)], limit = 20 }),
        ("client_paths", _ => new { query = "fireball", limit = 20 }),
        ("dbc_tables", r => new { filter = new[] { "spell", "item", "map" }[r.Next(3)] }),
        ("dbc_rows", r => new { table = "Spell", where = new[] { new { field = "ID", op = "=", value = (object)new[] { 133, 116 }[r.Next(2)] } }, columns = new[] { "ID", "NameLang_0" } }),
        ("dbc_rows", _ => new { table = "Map", limit = 20 }),
    };

    private static Task<LiveClient> Connect()
    {
        var http = new HttpClient(new CountingHandler()) { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(Raw.UserAgent);
        return LiveClient.Connect(Suite.Book, http, timed: false);
    }

    [SkippableFact]
    public Task TwentyClients_MixedReads_TwoMinutes() => Suite.Run(Area, "20 concurrent clients for 2 minutes of mixed reads", async c =>
    {
        // Connecting costs requests of its own, so do that first and start the bursts on a clean window.
        var clients = new List<LiveClient>();
        for (var i = 0; i < Clients; i++)
        {
            if (CountingHandler.Sent > 90) { await Task.Delay(TimeSpan.FromSeconds(62)); CountingHandler.Sent = 0; }
            clients.Add(await Connect());
        }
        await Task.Delay(TimeSpan.FromSeconds(62));
        Interlocked.Exchange(ref CountingHandler.Sent, 0);
        Interlocked.Exchange(ref CountingHandler.Limited, 0);
        Interlocked.Exchange(ref CountingHandler.Faults, 0);

        var rng = Suite.Rng("load");
        var results = new System.Collections.Concurrent.ConcurrentBag<(string Tool, long Ms, string? Error)>();
        var perMinuteOk = true;
        var bursts = 0;
        var watch = Stopwatch.StartNew();
        var remote = Shell.Ssh(LimitTests.RemoteCurl(30, 10));

        while (watch.Elapsed < Duration)
        {
            var started = watch.Elapsed;
            bursts++;
            await Task.WhenAll(clients.Select(async client =>
            {
                (string Tool, Func<Random, object> Args) pick;
                object args;
                lock (rng)
                {
                    pick = Mix[rng.Next(Mix.Length)];
                    args = pick.Args(rng);
                }
                try
                {
                    var r = await client.Call(pick.Tool, args);
                    results.Add((pick.Tool, r.Ms, r.Refused ? ReportBook.Cut(r.All, 300) : null));
                }
                catch (Exception ex)
                {
                    results.Add((pick.Tool, -1, ex.GetType().Name + ": " + ex.Message));
                }
            }));
            var wait = started + BurstEvery - watch.Elapsed;
            if (wait > TimeSpan.Zero) await Task.Delay(wait);
            else perMinuteOk = false;
        }
        var elapsed = watch.Elapsed;
        foreach (var client in clients) await client.DisposeAsync();
        var other = await remote;

        var all = results.ToList();
        var ok = all.Where(r => r.Error is null).ToList();
        var times = ok.Select(r => r.Ms).ToList();
        var errors = all.Where(r => r.Error is not null).ToList();
        var limited = Interlocked.Read(ref CountingHandler.Limited);

        var sb = new StringBuilder();
        sb.Append($"{Clients} real MCP clients, one burst of {Clients} concurrent mixed reads every {BurstEvery.TotalSeconds:F0} s, for {elapsed.TotalSeconds:F0} s: {bursts} bursts, {all.Count} calls, {errors.Count} errors, {limited} rate limited, {Interlocked.Read(ref CountingHandler.Faults)} HTTP 5xx.\n\n");
        sb.Append($"Overall: p50 {ReportBook.Percentile(times, 50)} ms, p95 {ReportBook.Percentile(times, 95)} ms, max {(times.Count > 0 ? times.Max() : 0)} ms.\n\n");
        sb.Append("| Tool | Calls | p50 | p95 | Max |\n|---|---:|---:|---:|---:|\n");
        foreach (var g in ok.GroupBy(r => r.Tool).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var ms = g.Select(r => r.Ms).ToList();
            sb.Append($"| {g.Key} | {ms.Count} | {ReportBook.Percentile(ms, 50)} ms | {ReportBook.Percentile(ms, 95)} ms | {ms.Max()} ms |\n");
        }
        c.Book.Load("20 clients from this machine", sb.ToString());

        var otherLines = other.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split(' ')).ToList();
        var otherTimes = otherLines.Where(l => l.Length == 2).Select(l => (long)(double.Parse(l[1], CultureInfo.InvariantCulture) * 1000)).ToList();
        c.Book.Load("At the same time, from BledtIT through Cloudflare", $"{otherLines.Count} `status` calls with `curl`, 10 at a time, statuses {string.Join(", ", otherLines.GroupBy(l => l[0]).Select(g => $"{g.Key} x{g.Count()}"))}: p50 {ReportBook.Percentile(otherTimes, 50)} ms, p95 {ReportBook.Percentile(otherTimes, 95)} ms.");

        c.That(all.Count >= 200, "a meaningful number of calls ran", all.Count.ToString(CultureInfo.InvariantCulture));
        c.Equal(0, errors.Count, "no call failed");
        if (errors.Count > 0) c.Fail("failed calls", string.Join("\n", errors.Take(20).Select(e => $"{e.Tool}: {e.Error}")));
        c.Equal(0L, limited, "the bursts stayed inside the limit");
        c.Equal(0L, Interlocked.Read(ref CountingHandler.Faults), "no 5xx");
        c.That(perMinuteOk, "every burst finished before the next was due", "a burst took longer than its 11 s slot");
        c.That(otherLines.Count == 30 && otherLines.All(l => l[0] == "200"), "the second client's concurrent run all worked", other.Out + other.Err);
        c.That(ReportBook.Percentile(times, 95) < 5_000, "p95 stays under 5 s", ReportBook.Percentile(times, 95) + " ms");
    });
}
