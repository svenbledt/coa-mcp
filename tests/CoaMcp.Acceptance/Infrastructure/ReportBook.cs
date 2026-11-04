using System.Globalization;
using System.Text;

namespace CoaMcp.Acceptance;

public enum Outcome { Pass, Fail, NotRun }

public sealed record TestResult(string Area, string Test, Outcome Outcome, int Passed, int Failed, TimeSpan Elapsed, string? Reason);
public sealed record Failure(string Area, string Test, string What, string Detail);
public sealed record Capture(string Tool, string Label, string Arguments, string Response, bool IsError, long Ms);

/// <summary>Everything one run found. The report is rendered only from this, and rewritten whole after every test, so results are never written by hand and an aborted run still leaves a report.</summary>
public sealed class ReportBook
{
    /// <summary>Longest response shown in full. Longer ones are cut with a marker that states the full size.</summary>
    public const int MaxShownChars = 3_000;

    /// <summary>Areas every full run should report. One with no results shows as NOT RUN, so a filtered run can't look complete.</summary>
    public static readonly string[] Areas =
    {
        "Protocol", "Read tools", "Accuracy: items", "Accuracy: spells", "Accuracy: scaling", "Accuracy: DBC", "Accuracy: paths", "Accuracy: status counts", "Accuracy: spot checks",
        "Hostile input", "Limits", "Writes: refused", "Writes: with token", "Merge", "Live update", "Restart", "Load", "Real client", "Final state", "Suite self-checks",
    };

    /// <summary>The nine tools the design's anonymous list holds, then the two hidden write tools. Sets the order of the Tool responses section.</summary>
    public static readonly string[] ToolOrder =
        { "search", "get", "status", "spell_compare", "decode_flags", "spell_scaling", "client_paths", "dbc_tables", "dbc_rows", "write_entry", "undo_entry" };

    private readonly object _lock = new();
    private readonly Scrubber _scrubber;
    private readonly List<TestResult> _results = new();
    private readonly List<Failure> _failures = new();
    private readonly List<Capture> _captures = new();
    private readonly List<string> _observations = new();
    private readonly List<string> _productionChanges = new();
    private readonly List<(string Title, string Text)> _transcripts = new();
    private readonly List<(string Title, string Text)> _load = new();
    private readonly Dictionary<string, List<long>> _timings = new();
    private readonly List<string> _header = new();
    private string? _layersAtStart, _layersAtEnd;
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;

    public long RateLimitWaits;
    public long RequestsSent;

    public ReportBook(Scrubber scrubber) => _scrubber = scrubber;

    /// <summary>Text is scrubbed the moment it arrives. Cutting it first could leave part of a token behind, past the reach of any later scrub.</summary>
    public string Scrub(string? text) => _scrubber.Scrub(text);

    public void Add(TestResult r) { lock (_lock) _results.Add(r with { Reason = r.Reason is null ? null : Scrub(r.Reason) }); }
    public void Add(Failure f) { lock (_lock) _failures.Add(new Failure(f.Area, Scrub(f.Test), Scrub(f.What), Scrub(f.Detail))); }
    public void Add(Capture c) { lock (_lock) _captures.Add(c with { Label = Scrub(c.Label), Arguments = Scrub(c.Arguments), Response = Scrub(c.Response) }); }
    public void Observe(string text) { var clean = Scrub(text); lock (_lock) if (!_observations.Contains(clean)) _observations.Add(clean); }
    public void ProductionChange(string text) { lock (_lock) _productionChanges.Add(Scrub(text)); }
    public void Transcript(string title, string text) { lock (_lock) _transcripts.Add((Scrub(title), Scrub(text))); }
    public void Load(string title, string text) { lock (_lock) _load.Add((Scrub(title), Scrub(text))); }
    public void Header(string line) { lock (_lock) _header.Add(Scrub(line)); }
    public void LayersAtStart(string text) { lock (_lock) _layersAtStart ??= Scrub(text); }
    public void LayersAtEnd(string text) { lock (_lock) _layersAtEnd = Scrub(text); }

    public void Timing(string tool, long ms)
    {
        lock (_lock)
        {
            if (!_timings.TryGetValue(tool, out var list)) _timings[tool] = list = new List<long>();
            list.Add(ms);
        }
    }

    public static long Percentile(IReadOnlyCollection<long> values, double p)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[Math.Clamp((int)Math.Ceiling(p / 100 * sorted.Count) - 1, 0, sorted.Count - 1)];
    }

    /// <summary>A code block no text can close early: its fence is longer than any run of backticks inside.</summary>
    public static string Fence(string text, string lang = "")
    {
        var longest = 0;
        var run = 0;
        foreach (var c in text)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        var fence = new string('`', Math.Max(3, longest + 1));
        return $"{fence}{lang}\n{text.TrimEnd('\n')}\n{fence}\n";
    }

    public static string Cut(string text, int max = MaxShownChars)
    {
        // Tests cut text for failure details before the book sees it, so the run's token goes first here too.
        var clean = Scrubber.ForRun.Scrub(text);
        return clean.Length <= max ? clean : clean[..max] + $"\n[... cut for the report: the full response is {clean.Length.ToString("N0", CultureInfo.InvariantCulture)} characters, {Encoding.UTF8.GetByteCount(clean).ToString("N0", CultureInfo.InvariantCulture)} bytes ...]";
    }

    private static string AreaResult(IReadOnlyList<TestResult> rs)
    {
        if (rs.Count == 0) return "NOT RUN";
        if (rs.Any(r => r.Outcome == Outcome.Fail)) return "FAIL";
        if (rs.All(r => r.Outcome == Outcome.NotRun)) return "NOT RUN";
        return rs.Any(r => r.Outcome == Outcome.NotRun) ? "PASS (part NOT RUN)" : "PASS";
    }

    /// <summary>The whole report as Markdown, scrubbed.</summary>
    public string Render()
    {
        lock (_lock) return _scrubber.Scrub(Build());
    }

    private string Build()
    {
        var sb = new StringBuilder("# Production acceptance report\n\n");
        sb.Append("Generated by `tests/CoaMcp.Acceptance` from the live server. Never edited by hand: re-running the suite replaces this file.\n\n");
        sb.Append($"- Started: {_started:yyyy-MM-dd HH:mm:ss} UTC; last written: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC\n");
        foreach (var h in _header) sb.Append("- ").Append(h).Append('\n');
        sb.Append('\n');

        sb.Append("## Layer versions from `status`\n\n");
        sb.Append(Fence(_layersAtStart ?? "(status was not read)"));
        if (_layersAtEnd is not null) sb.Append(_layersAtEnd == _layersAtStart ? "\nThe same at the end of the run.\n" : "\nAt the end of the run:\n\n" + Fence(_layersAtEnd));
        sb.Append('\n');

        sb.Append("## Results per area\n\n| Area | Result | Tests pass / fail / NOT RUN | Checks passed | Checks failed | Time |\n|---|---|---:|---:|---:|---:|\n");
        foreach (var area in Areas.Concat(_results.Select(r => r.Area)).Distinct())
        {
            var rs = _results.Where(r => r.Area == area).ToList();
            var time = TimeSpan.FromTicks(rs.Sum(r => r.Elapsed.Ticks));
            sb.Append($"| {area} | {AreaResult(rs)} | {rs.Count(r => r.Outcome == Outcome.Pass)} / {rs.Count(r => r.Outcome == Outcome.Fail)} / {rs.Count(r => r.Outcome == Outcome.NotRun)} | {rs.Sum(r => r.Passed)} | {rs.Sum(r => r.Failed)} | {time.TotalSeconds:F0} s |\n");
        }
        sb.Append($"\nTotals: {_results.Count(r => r.Outcome == Outcome.Pass)} tests passed, {_results.Count(r => r.Outcome == Outcome.Fail)} failed, {_results.Count(r => r.Outcome == Outcome.NotRun)} NOT RUN; {_results.Sum(r => r.Passed)} checks passed, {_results.Sum(r => r.Failed)} failed. NOT RUN never counts as passed.\n\n");

        sb.Append($"## Failures ({_failures.Count})\n\n");
        if (_failures.Count == 0) sb.Append("None in this run.\n\n");
        foreach (var g in _failures.GroupBy(f => (f.Area, f.Test)))
        {
            sb.Append($"### {g.Key.Area}: {g.Key.Test} ({g.Count()})\n\n");
            foreach (var f in g) sb.Append($"**{f.What}**\n\n").Append(Fence(f.Detail)).Append('\n');
        }

        var notRun = _results.Where(r => r.Outcome == Outcome.NotRun).ToList();
        sb.Append($"## NOT RUN ({notRun.Count})\n\n");
        if (notRun.Count == 0) sb.Append("Nothing.\n");
        foreach (var r in notRun) sb.Append($"- **{r.Area}: {r.Test}**: {r.Reason}\n");
        foreach (var area in Areas.Where(a => _results.All(r => r.Area != a))) sb.Append($"- **{area}**: no test of this area ran (filtered or aborted run).\n");
        sb.Append('\n');

        sb.Append("## Changes made to production by this run\n\n");
        if (_productionChanges.Count == 0) sb.Append("None.\n");
        foreach (var c in _productionChanges) sb.Append("- ").Append(c).Append('\n');
        sb.Append('\n');

        sb.Append("## Timings\n\nTime per tool call as the client saw it, through Cloudflare from the test machine, without the suite's own pacing waits. Load test calls are listed under Load instead.\n\n");
        sb.Append("| Tool | Calls | p50 | p95 | Max |\n|---|---:|---:|---:|---:|\n");
        foreach (var (tool, list) in _timings.OrderBy(t => t.Key, StringComparer.Ordinal))
            sb.Append($"| {tool} | {list.Count} | {Percentile(list, 50)} ms | {Percentile(list, 95)} ms | {list.Max()} ms |\n");
        sb.Append($"\nHTTP requests sent through the paced client: {RequestsSent}. Rate limit waits outside the limit test: {RateLimitWaits} (not counted as failures).\n\n");

        sb.Append("## Load\n\n");
        if (_load.Count == 0) sb.Append("Not run.\n\n");
        foreach (var (title, text) in _load) sb.Append($"### {title}\n\n").Append(text.TrimEnd()).Append("\n\n");

        sb.Append("## Observations\n\n");
        if (_observations.Count == 0) sb.Append("None.\n");
        foreach (var o in _observations) sb.Append("- ").Append(o).Append('\n');
        sb.Append('\n');

        sb.Append("## Real client transcripts\n\n");
        if (_transcripts.Count == 0) sb.Append("None in this run.\n\n");
        foreach (var (title, text) in _transcripts) sb.Append($"### {title}\n\n").Append(Fence(text)).Append('\n');

        sb.Append("## Tool responses\n\nReal requests and responses, captured by the suite from the live server. The token and this machine's paths are scrubbed.\n\n");
        foreach (var tool in ToolOrder.Concat(_captures.Select(c => c.Tool)).Distinct())
        {
            var list = _captures.Where(c => c.Tool == tool).ToList();
            sb.Append($"### `{tool}` ({list.Count})\n\n");
            if (list.Count == 0) sb.Append("Nothing captured in this run.\n\n");
            foreach (var c in list)
            {
                sb.Append($"**{c.Label}** ({(c.IsError ? "isError: true" : "isError: false")}, {c.Ms} ms)\n\nRequest arguments:\n\n").Append(Fence(c.Arguments, "json"));
                sb.Append("\nResponse:\n\n").Append(Fence(Cut(c.Response))).Append('\n');
            }
        }
        return sb.ToString();
    }

    public void WriteTo(string path)
    {
        var text = Render();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        // On Windows the rename fails while anything else has the report open. That says nothing about the server, so it mustn't fail the test that happened to be writing.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tmp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < 10 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(500);
            }
        }
    }
}
