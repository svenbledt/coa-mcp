using System.Globalization;

namespace CoaMcp.Acceptance;

/// <summary>
/// The per-client rate limit, through the real proxy, from outside. The only tests that deliberately go over 120 a minute, and they stop at the first refusals.
/// They never try to get past the limit: each proves the limit holds.
/// </summary>
[Collection("11 Limits")]
public class LimitTests
{
    private const string Area = "Limits";
    private const int PerMinute = 120;

    /// <summary>Enough for one window, with some left for a window that rolls over partway through.</summary>
    private const int MaxTries = PerMinute * 2 + 20;

    /// <summary>Sends until refused, a few at a time. Returns how many were allowed and the first refusal.</summary>
    private static async Task<(int Allowed, RawReply? Refused, TimeSpan Took)> Exhaust(HttpClient http, Func<int, (string, string)[]>? headers = null)
    {
        var allowed = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var sent = 0; sent < MaxTries;)
        {
            var batch = await Task.WhenAll(Enumerable.Range(sent, 6).Select(i => Raw.Post(http, Raw.ToolsList, headers?.Invoke(i))));
            sent += batch.Length;
            allowed += batch.Count(r => r.Status == 200);
            var refused = batch.FirstOrDefault(r => r.Status == 429);
            if (refused is not null) return (allowed, refused, watch.Elapsed);
        }
        return (allowed, null, watch.Elapsed);
    }

    private static string Forged(int i) => $"{(i % 200) + 11}.{(i * 7) % 250}.{(i * 13) % 250}.{(i % 250) + 1}";

    private static (string, string)[] Spoof(int i) => new[]
    {
        ("X-Forwarded-For", Forged(i)),
        ("CF-Connecting-IP", Forged(i + 1)),
        ("X-Real-IP", Forged(i + 2)),
        ("True-Client-IP", Forged(i + 3)),
    };

    /// <summary>Waits out the current window, so a burst starts with a clean count.</summary>
    private static Task NextWindow() => Task.Delay(TimeSpan.FromSeconds(62));

    [SkippableFact]
    public Task ThroughCloudflare_LimitTriggers_AndSpoofingDoesNotEscape_AndOtherClientsAreUnaffected() => Suite.Run(Area, "the limit triggers through Cloudflare, spoofed headers don't escape it, another client stays free", async c =>
    {
        await NextWindow();
        using var http = Raw.Unpaced();

        // 1. It triggers.
        var (allowed, refused, took) = await Exhaust(http);
        c.That(refused is not null, $"the limit triggers within {MaxTries} requests", $"{allowed} requests were allowed with none refused");
        if (refused is null) return;
        c.That(allowed <= PerMinute * 2, "no more than two windows' worth were allowed", allowed.ToString(CultureInfo.InvariantCulture));
        c.Book.Observe($"Through Cloudflare: {allowed} requests were allowed in {took.TotalSeconds:F1} s before the first 429 (limit {PerMinute} a minute).");
        c.That(allowed >= PerMinute - 20, "the limit isn't much lower than the documented 120", allowed.ToString(CultureInfo.InvariantCulture));
        c.That(Suite.Leak(refused.Body) is null, "a 429 leaks nothing", refused.Body);
        var retry = refused.Headers.RetryAfter?.Delta;
        c.Book.Observe(retry is null
            ? "A 429 from the server has no Retry-After header, so a client has to guess when to try again (a minute is always enough)."
            : $"A 429 carries Retry-After: {retry.Value.TotalSeconds:F0} s.");

        // 2. Spoofing inside one window: refused before, refused after, so anything in between must be refused too.
        var before = await Raw.Post(http, Raw.ToolsList);
        var spoofed = new List<int>();
        var oneHeader = new[] { "X-Forwarded-For", "X-Real-IP", "True-Client-IP", "CF-Connecting-IP" };
        for (var i = 0; i < 12; i++)
        {
            // Each header alone and all together, since Cloudflare treats some of them differently.
            var headers = i % 5 == 4 ? Spoof(i * 5) : new[] { (oneHeader[i % 5], Forged(i * 5)) };
            spoofed.Add((await Raw.Post(http, Raw.ToolsList, headers)).Status);
        }
        var after = await Raw.Post(http, Raw.ToolsList);
        c.Equal(429, before.Status, "still limited before the spoofed requests");
        c.Equal(429, after.Status, "still limited after them");
        if (before.Status == 429 && after.Status == 429)
        {
            // Cloudflare itself may answer a forged CF-Connecting-IP with a 403. Either way the request gets nowhere: what must never happen is a 200.
            c.That(spoofed.All(s => s is 429 or 403), "forged X-Forwarded-For, CF-Connecting-IP, X-Real-IP and True-Client-IP through Cloudflare are all refused", string.Join(",", spoofed));
            c.Book.Observe($"Spoofed headers through Cloudflare while limited, one header at a time and all at once: statuses {string.Join(",", spoofed)} (403 is Cloudflare refusing a forged header, 429 is the server's limit).");
        }
        else c.Book.Observe("A window rolled over during the spoofing check through Cloudflare, so its statuses don't prove anything alone: " + string.Join(",", spoofed));

        // 3. /healthz is free, so monitoring still works while a client is limited.
        var health = await Raw.Post(http, null, null, method: HttpMethod.Get, url: Settings.Health.ToString());
        c.Equal(200, health.Status, "/healthz isn't limited");

        // 4. Limited here, not limited elsewhere: BledtIT is another client, also through Cloudflare.
        var other = await Shell.Ssh(RemoteCurl(10, 5));
        c.Equal(0, other.Exit, "the second client ran");
        var statuses = other.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split(' ')[0]).ToList();
        c.Equal(10, statuses.Count, "the second client made its 10 requests");
        c.That(statuses.All(s => s == "200"), "a second client through Cloudflare is unaffected while this one is limited", other.Out + other.Err);
        var still = await Raw.Post(http, Raw.ToolsList);
        c.Book.Observe($"This machine's status right after the second client's run: {still.Status}. Second client: {string.Join(",", statuses)}.");
    });

    [SkippableFact]
    public Task DirectToOrigin_ForgedHeadersStillLimited() => Suite.Run(Area, "direct to the origin with forged headers, it's still limited", async c =>
    {
        await NextWindow();
        using var direct = Raw.PinnedToOrigin();

        RawReply first;
        try
        {
            first = await Raw.Post(direct, Raw.ToolsList, Spoof(0));
        }
        catch (HttpRequestException ex)
        {
            c.That(true, "the origin refuses direct connections", ex.Message);
            c.Book.Observe("Direct connections to the origin are refused: " + ex.Message);
            return;
        }
        c.Book.Observe($"A direct request to the origin, under the public host name, gets status {first.Status}. The proxy serves it, so the limit must hold here too.");
        c.That(first.Status is 200 or 429, "the origin answers a direct request cleanly", $"status {first.Status}");

        var (allowed, refused, took) = await Exhaust(direct, Spoof);
        c.That(refused is not null, $"forged headers don't escape the limit: refused within {MaxTries} requests", $"{allowed} requests were allowed, none refused");
        if (refused is null) return;
        c.That(allowed <= PerMinute * 2, "no more than two windows' worth were allowed", allowed.ToString(CultureInfo.InvariantCulture));
        c.Book.Observe($"Direct to the origin with a different forged CF-Connecting-IP and X-Forwarded-For on every request: {allowed} requests were allowed in {took.TotalSeconds:F1} s before the first 429.");

        // Inside one window: refused with forged addresses, including one in Cloudflare's published range.
        var before = await Raw.Post(direct, Raw.ToolsList);
        var cloudflareForged = await Raw.Post(direct, Raw.ToolsList, new[] { ("X-Forwarded-For", "9.9.9.9, 173.245.48.1"), ("CF-Connecting-IP", "9.9.9.9") });
        var after = await Raw.Post(direct, Raw.ToolsList);
        if (before.Status == 429 && after.Status == 429)
            c.Equal(429, cloudflareForged.Status, "claiming to come through a Cloudflare address doesn't help");
        else c.Book.Observe("A window rolled over during the direct check; its last statuses prove nothing alone.");
    });

    [SkippableFact]
    public Task SmallConcurrentRun_FromTheOtherClient() => Suite.Run(Area, "a small concurrent run from the other client", async c =>
    {
        await NextWindow();
        var r = await Shell.Ssh(RemoteCurl(30, 10));
        c.Equal(0, r.Exit, "the run finished");
        var lines = r.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split(' ')).ToList();
        c.Equal(30, lines.Count, "30 requests were made");
        c.That(lines.All(l => l[0] == "200"), "all of them worked", r.Out);
        var times = lines.Select(l => (long)(double.Parse(l[1], CultureInfo.InvariantCulture) * 1000)).ToList();
        c.Book.Load("From BledtIT through Cloudflare", $"30 `status` calls with `curl`, 10 at a time: p50 {ReportBook.Percentile(times, 50)} ms, p95 {ReportBook.Percentile(times, 95)} ms, {lines.Count(l => l[0] != "200")} errors.");
    });

    /// <summary>Read-only curl calls from the server itself, out through Cloudflare, printing each status and total time.</summary>
    internal static string RemoteCurl(int total, int parallel)
    {
        var body = Raw.StatusCall.Replace("\"", "\\\"");
        return $"seq 1 {total} | xargs -P {parallel} -I{{}} curl -s -m 60 -o /dev/null -w '%{{http_code}} %{{time_total}}\\n' -A '{Raw.UserAgent}' " +
               $"-H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream' -d \"{body}\" '{Settings.Url}'\n";
    }
}
