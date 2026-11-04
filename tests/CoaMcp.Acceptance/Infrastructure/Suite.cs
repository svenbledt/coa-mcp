using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit.Sdk;

namespace CoaMcp.Acceptance;

/// <summary>A tool's answer. <paramref name="ProtocolError"/> is set when the server answered with a JSON-RPC error rather than a tool result.</summary>
public sealed record Reply(string Text, bool IsError, long Ms, string? ProtocolError = null)
{
    public bool Refused => IsError || ProtocolError is not null;
    public string All => ProtocolError ?? Text;
}

/// <summary>A real MCP client on the live server. Every call is timed, and captured for the report when given a label.</summary>
public sealed class LiveClient : IAsyncDisposable
{
    private readonly McpClient _client;
    private readonly ReportBook _book;
    private readonly bool _timed;

    public static readonly JsonSerializerOptions ArgsJson = new() { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private LiveClient(McpClient client, ReportBook book, bool timed) { _client = client; _book = book; _timed = timed; }

    public McpClient Mcp => _client;

    public static async Task<LiveClient> Connect(ReportBook book, HttpClient http, string? authorization = null, bool timed = true)
    {
        if (authorization is not null) http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authorization);
        var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = Settings.Mcp,
            TransportMode = HttpTransportMode.StreamableHttp,
        }, http, loggerFactory: null, ownsHttpClient: true));
        return new LiveClient(client, book, timed);
    }

    public static async Task<LiveClient> Connect(ReportBook book, string? authorization = null, bool timed = true) =>
        await Connect(book, Raw.Paced(book), authorization, timed);

    public async Task<Reply> Call(string tool, object? args = null, string? capture = null, string? captureAs = null)
    {
        var map = args is null ? new Dictionary<string, object?>() : A.Map(args);
        var wait = new PaceWait();
        PaceWait.Current.Value = wait;
        var watch = Stopwatch.StartNew();
        Reply reply;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(240));
        try
        {
            var r = await _client.CallToolAsync(tool, map, cancellationToken: timeout.Token);
            var text = string.Join("\n", r.Content.OfType<TextContentBlock>().Select(c => c.Text));
            reply = new Reply(text, r.IsError == true, Math.Max(0, watch.ElapsedMilliseconds - wait.Ms));
        }
        catch (McpException ex)
        {
            reply = new Reply("", true, Math.Max(0, watch.ElapsedMilliseconds - wait.Ms), "JSON-RPC error: " + ex.Message);
        }
        if (_timed) _book.Timing(tool, reply.Ms);
        if (capture is not null)
            _book.Add(new Capture(captureAs ?? tool, capture, JsonSerializer.Serialize(map, ArgsJson), reply.All, reply.Refused, reply.Ms));
        return reply;
    }

    public async ValueTask DisposeAsync() => await _client.DisposeAsync();
}

/// <summary>Small helpers for tool arguments.</summary>
public static class A
{
    public static Dictionary<string, object?> Map(object args)
    {
        if (args is Dictionary<string, object?> d) return d;
        return args.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(args));
    }
}

/// <summary>Soft assertions for one test: everything gets checked and every difference recorded, rather than stopping at the first.</summary>
public sealed class Checks
{
    private readonly ReportBook _book;
    private readonly string _area, _test;
    public int Passed { get; private set; }
    public int Failed { get; private set; }
    public List<string> FailedTitles { get; } = new();

    public Checks(ReportBook book, string area, string test) { _book = book; _area = area; _test = test; }

    public ReportBook Book => _book;

    public void Fail(string what, string detail = "")
    {
        Failed++;
        FailedTitles.Add(what);
        _book.Add(new Failure(_area, _test, what, detail));
    }

    public bool That(bool ok, string what, string? detail = null)
    {
        if (ok) Passed++;
        else Fail(what, detail ?? "");
        return ok;
    }

    public bool Equal(object? expected, object? actual, string what)
    {
        var e = expected?.ToString();
        var a = actual?.ToString();
        return That(e == a, what, $"expected: {e}\nserver:   {a}");
    }

    public bool Has(string text, string needle, string what) =>
        That(text.Contains(needle, StringComparison.Ordinal), what, $"expected to find: {_book.Scrub(needle)}\nin the response:\n{ReportBook.Cut(_book.Scrub(text), 1500)}");

    public bool Lacks(string text, string needle, string what) =>
        That(!text.Contains(needle, StringComparison.Ordinal), what, $"must not contain: {_book.Scrub(needle)}\nin the response:\n{ReportBook.Cut(_book.Scrub(text), 1500)}");
}

/// <summary>What every test shares: the report, one anonymous client, and how a test's outcome reaches the report.</summary>
public static partial class Suite
{
    private static readonly SemaphoreSlim Lock = new(1, 1);
    private static LiveClient? _anonymous;
    private static HttpClient? _rawPaced;
    private static bool _headerDone;

    public static ReportBook Book { get; } = new(Scrubber.ForRun);

    /// <summary>Whether this server has writes on, as its own status says. The local token says nothing about that: either can be there without the other.</summary>
    public static bool ServerWritesEnabled { get; private set; }

    /// <summary>Live written entries when the run began, from status. Null when status gave no count.</summary>
    public static int? LiveAtStart { get; private set; }

    /// <summary>Every entry this run wrote, so the last test can check each one, whatever happened along the way.</summary>
    public static System.Collections.Concurrent.ConcurrentDictionary<long, bool> WrittenIds { get; } = new();

    /// <summary>Live entries an earlier crashed run left, which this run undid. They come off the expected count at the end.</summary>
    public static int SweptAtStart;

    public static HttpClient RawPaced => _rawPaced ??= Raw.Paced(Book);

    public static async Task<LiveClient> Anonymous()
    {
        await Lock.WaitAsync();
        try { return _anonymous ??= await LiveClient.Connect(Book); }
        finally { Lock.Release(); }
    }

    public static Random Rng(string area) => new(HashCode.Combine(Settings.Seed, StableHash(area)));

    private static int StableHash(string s) => s.Aggregate(17, (h, c) => unchecked(h * 31 + c));

    /// <summary>Runs one test: records what it found, writes the report, and fails it in xUnit too when any check failed.</summary>
    public static async Task Run(string area, string test, Func<Checks, Task> body)
    {
        Skip.If(Settings.Url is null, "COA_MCP_URL is not set, so nothing can be tested and the report is left as it is.");
        await Prepare();
        var checks = new Checks(Book, area, test);
        var watch = Stopwatch.StartNew();
        try
        {
            await body(checks);
        }
        catch (Exception ex) when (ex is not Xunit.SkipException)
        {
            checks.Fail("The test stopped on an exception", ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace);
        }
        Book.Add(new TestResult(area, test, checks.Failed > 0 ? Outcome.Fail : Outcome.Pass, checks.Passed, checks.Failed, watch.Elapsed, null));
        Book.WriteTo(Settings.ReportPath);
        if (checks.Failed > 0)
            throw new XunitException(Scrubber.ForRun.Scrub($"{checks.Failed} checks failed in {area}: {test}. First: {string.Join(" / ", checks.FailedTitles.Take(5))}. See the report."));
    }

    /// <summary>Records the test as NOT RUN, with why, and skips it. It never counts as passed.</summary>
    public static void NotRun(string area, string test, string reason)
    {
        Skip.If(Settings.Url is null, "COA_MCP_URL is not set, so nothing can be tested and the report is left as it is.");
        Book.Add(new TestResult(area, test, Outcome.NotRun, 0, 0, TimeSpan.Zero, reason));
        Book.WriteTo(Settings.ReportPath);
        Skip.If(true, "NOT RUN: " + reason);
    }

    public static async Task RunWithToken(string area, string test, Func<Checks, string, Task> body)
    {
        Skip.If(Settings.Url is null, "COA_MCP_URL is not set, so nothing can be tested and the report is left as it is.");
        await Prepare();
        if (Settings.Token is not { } token)
        {
            NotRun(area, test, Settings.TokenAbsent + (ServerWritesEnabled ? "; the server has writes enabled, so this needs its token" : "; the server also has writes disabled"));
            return;
        }
        if (!ServerWritesEnabled)
        {
            NotRun(area, test, "the server's status says writes are disabled, so a local token can't be used against it");
            return;
        }
        await Run(area, test, c => body(c, token));
    }

    private static async Task Prepare()
    {
        if (_headerDone) return;
        _headerDone = true;
        Book.Header($"Target: {Settings.Url}");
        Book.Header($"Commit of the suite: {Git("rev-parse --short HEAD")} on {Git("rev-parse --abbrev-ref HEAD")}");
        var image = await Shell.Ssh("docker ps --format '{{.Image}}' | grep " + Settings.CoolifyAppId + " | head -1");
        Book.Header($"Server image: {(image.Exit == 0 && image.Out.Trim().Length > 0 ? image.Out.Trim() : "could not be read over ssh")}");
        Book.Header($"Random seed: {Settings.Seed} (set COA_ACCEPT_SEED to repeat this sample)");
        Book.Header($"Token available to the suite: {(Settings.Token is null ? "no" : "yes")}");
        var client = await Anonymous();
        var status = await client.Call("status");
        Book.LayersAtStart(LayerLines(status.Text));

        ServerWritesEnabled = WritesEnabled(status.Text);
        LiveAtStart = LiveEntries(status.Text);
        var state = status.Text.Split('\n').FirstOrDefault(l => l.StartsWith("Writes:", StringComparison.Ordinal)) ?? "(status has no Writes line)";
        Book.Header($"Write state from `status` at the start: \"{state}\" and \"{WrittenLine(status.Text)}\"");
        Book.Header(ServerWritesEnabled
            ? "Refusal tests ran with writes enabled, so a wrong token was refused by a server holding a real one."
            : "Refusal tests ran with writes DISABLED: with no token set, the server refuses every credential, so \"Writes: refused\" doesn't show a wrong token losing to a real one. That's still untested on this server.");
    }

    public static bool WritesEnabled(string status) => status.Contains("Writes: enabled", StringComparison.Ordinal);

    public static string WrittenLine(string status) =>
        status.Split('\n').FirstOrDefault(l => l.StartsWith("Written entries:", StringComparison.Ordinal)) ?? "";

    /// <summary>How many written entries status counts as live. No store yet means none. Null when status says the store can't be read.</summary>
    public static int? LiveEntries(string status)
    {
        var line = WrittenLine(status);
        if (line.Contains("none yet", StringComparison.Ordinal)) return 0;
        var m = Regex.Match(line, @"Written entries: (\d+) live");
        return m.Success ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    private static string Git(string args)
    {
        var r = Shell.Run("git", args.Split(' '), workingDir: Settings.RepoRoot).GetAwaiter().GetResult();
        return r.Exit == 0 ? r.Out.Trim() : "unknown";
    }

    /// <summary>The per-layer lines of a status answer.</summary>
    public static string LayerLines(string status) =>
        string.Join("\n", status.Split('\n').Where(l => l.StartsWith("- ", StringComparison.Ordinal) || l.StartsWith("  Note:", StringComparison.Ordinal)));

    /// <summary>name to version, for layers status says are loaded.</summary>
    public static Dictionary<string, string> LayerVersions(string status) =>
        LayerLine().Matches(status).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);

    [GeneratedRegex(@"^- (\w+): v([A-Za-z0-9._-]+),", RegexOptions.Multiline)]
    private static partial Regex LayerLine();

    /// <summary>Signs of a server fault or a leak, in a response to bad input. The caller removes what it sent first, since echoing it back is fine.</summary>
    public static string? Leak(string response, IEnumerable<string>? sent = null)
    {
        var text = response;
        foreach (var s in (sent ?? Array.Empty<string>()).Where(s => s.Length >= 2).OrderByDescending(s => s.Length))
            text = text.Replace(s, "", StringComparison.OrdinalIgnoreCase);
        foreach (var pattern in LeakPatterns)
        {
            var m = Regex.Match(text, pattern);
            if (m.Success) return $"matched /{pattern}/ at: {text.Substring(Math.Max(0, m.Index - 60), Math.Min(text.Length - Math.Max(0, m.Index - 60), 200))}";
        }
        return null;
    }

    private static readonly string[] LeakPatterns =
    {
        @"An error occurred invoking", @"\bat [A-Z][\w.]+\.[\w<>]+\(", @"[A-Za-z]+Exception\b", @"SQLite Error", @"\bsqlite3_", @"no such (column|table)", @"syntax error", @"fts5:", @"System\.[A-Z][a-z]+\.", @"Microsoft\.[A-Z][a-z]+\.",
        @"/app/", @"/data/", @"/root/", @"/home/", @"/usr/", @"[A-Za-z]:\\", @"CoaMcp\.",
    };
}

/// <summary>Runs local commands. Output never reaches the report without passing the scrubber.</summary>
public static class Shell
{
    public sealed record Result(int Exit, string Out, string Err);

    public static async Task<Result> Run(string file, IEnumerable<string> args, string? stdin = null, string? workingDir = null, TimeSpan? timeout = null,
        IDictionary<string, string?>? env = null)
    {
        var info = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workingDir ?? Settings.RepoRoot,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        foreach (var (k, v) in env ?? new Dictionary<string, string?>())
        {
            if (v is null) info.Environment.Remove(k);
            else info.Environment[k] = v;
        }
        using var p = Process.Start(info)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (stdin is not null) await p.StandardInput.WriteAsync(stdin.Replace("\r\n", "\n"));
        p.StandardInput.Close();
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(5));
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            p.Kill(entireProcessTree: true);
            return new Result(-1, await stdout, await stderr + "\n(timed out and was killed)");
        }

        // A child left running (a build server, say) can hold the pipes open long after the process itself has exited.
        await Task.WhenAny(Task.WhenAll(stdout, stderr), Task.Delay(TimeSpan.FromSeconds(15)));
        return new Result(p.ExitCode, stdout.IsCompletedSuccessfully ? stdout.Result : "(output not complete)", stderr.IsCompletedSuccessfully ? stderr.Result : "(output not complete)");
    }

    /// <summary>A script on the server, sent on stdin so the login shell can't reinterpret it.</summary>
    public static Task<Result> Ssh(string script, TimeSpan? timeout = null) =>
        Run("ssh", new[] { "-o", "BatchMode=yes", "-o", "ConnectTimeout=15", Settings.SshHost, "sh -s" }, script, timeout: timeout ?? TimeSpan.FromMinutes(3));

    private static Task<Result>? _pipelineBuild;

    /// <summary>The real pipeline, built once per run with no build servers left behind, then run from its own output folder.</summary>
    public static async Task<Result> Pipeline(params string[] args)
    {
        var project = Path.Combine(Settings.RepoRoot, "src", "CoaMcp.Pipeline");
        var built = await (_pipelineBuild ??= Run("dotnet", new[] { "build", project, "-nodeReuse:false", "-p:UseSharedCompilation=false", "-v:q", "-nologo" }, timeout: TimeSpan.FromMinutes(10),
            env: new Dictionary<string, string?> { ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0" }));
        if (built.Exit != 0) return built;
        var dll = Path.Combine(project, "bin", "Debug", "net10.0", "coamcp-pipeline.dll");
        return await Run("dotnet", new[] { dll }.Concat(args), timeout: TimeSpan.FromMinutes(10));
    }
}
