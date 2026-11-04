using System.Text;
using System.Text.Json;

namespace CoaMcp.Acceptance;

/// <summary>Claude Code itself, headless, connected to this server and nothing else. Each answer is checked against the local sources, and saved unedited in the report.</summary>
[Collection("18 Real client")]
public class RealClientTests
{
    private const string Area = "Real client";

    /// <summary>What a nested run mustn't inherit from a Claude Code session the suite may itself be started from.</summary>
    private static readonly Dictionary<string, string?> CleanEnv = new()
    {
        ["CLAUDECODE"] = null, ["CLAUDE_CODE_ENTRYPOINT"] = null, ["CLAUDE_CODE_SSE_PORT"] = null,
    };

    private static bool _waited;

    private static async Task Ask(Checks c, string title, string question, IEnumerable<string> mustContain)
    {
        // Claude Code shows a server as failed if its first request meets a 429, so start on a clean rate limit window.
        if (!_waited) await Task.Delay(TimeSpan.FromSeconds(62));
        _waited = true;

        var folder = Directory.CreateTempSubdirectory("coa-acceptance-claude-").FullName;
        try
        {
            var config = Path.Combine(folder, "mcp.json");
            await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new { mcpServers = new { coa = new { type = "http", url = Settings.Url } } }));

            var r = await Shell.Run("claude", new[]
            {
                "-p", question,
                "--mcp-config", config, "--strict-mcp-config",
                "--model", Settings.RealClientModel,
                "--allowedTools", "mcp__coa__*",
                "--tools", "",
                "--setting-sources", "local",
                "--no-session-persistence",
                "--output-format", "stream-json", "--verbose",
            }, workingDir: folder, timeout: TimeSpan.FromMinutes(5), env: CleanEnv);

            var answer = new StringBuilder();
            var calls = new List<string>();
            var transcript = new StringBuilder($"Question: {question}\n\n");
            foreach (var line in r.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); }
                catch (JsonException) { continue; }
                using (doc)
                {
                    var root = doc.RootElement;
                    var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (type == "assistant")
                        foreach (var block in root.GetProperty("message").GetProperty("content").EnumerateArray())
                        {
                            var kind = block.GetProperty("type").GetString();
                            if (kind == "tool_use")
                            {
                                var name = block.GetProperty("name").GetString() ?? "";
                                calls.Add(name);
                                transcript.Append($"[tool call] {name} {block.GetProperty("input").GetRawText()}\n");
                            }
                            else if (kind == "text") transcript.Append($"[assistant] {block.GetProperty("text").GetString()}\n");
                        }
                    else if (type == "user" && root.TryGetProperty("message", out var message) && message.GetProperty("content").ValueKind == JsonValueKind.Array)
                        foreach (var block in message.GetProperty("content").EnumerateArray())
                            if (block.GetProperty("type").GetString() == "tool_result")
                                transcript.Append($"[tool result] {ReportBook.Cut(block.TryGetProperty("content", out var content) ? content.ValueKind == JsonValueKind.String ? content.GetString() ?? "" : string.Join("\n", content.EnumerateArray().Select(x => x.TryGetProperty("text", out var tx) ? tx.GetString() : "")) : "", 800)}\n");
                    if (type == "result" && root.TryGetProperty("result", out var result))
                    {
                        answer.Append(result.GetString());
                        transcript.Append($"[final answer] {result.GetString()}\n");
                    }
                }
            }
            if (r.Exit != 0 || answer.Length == 0)
                transcript.Append($"[exit {r.Exit}]\n{ReportBook.Cut(r.Err + "\n" + r.Out, 1500)}\n");
            c.Book.Transcript(title, transcript.ToString());

            c.Equal(0, r.Exit, title + ": Claude Code exits 0");
            c.That(calls.Count > 0, title + ": it called this server", transcript.ToString());
            c.That(calls.All(n => n.StartsWith("mcp__coa__", StringComparison.Ordinal)), title + ": it used only this server's tools", string.Join(", ", calls));
            foreach (var value in mustContain)
                c.That(answer.ToString().Contains(value, StringComparison.OrdinalIgnoreCase), $"{title}: the answer holds the value from the local source: {value}", answer.ToString());
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static void RunOrNotRun(string test)
    {
        if (Settings.AllowRealClient) return;
        Suite.NotRun(Area, test, "COA_ACCEPT_REAL_CLIENT is 0: this run was asked not to spend model usage");
    }

    [SkippableFact]
    public async Task Spell()
    {
        const string test = "Claude Code answers a spell question from this server";
        RunOrNotRun(test);
        await Suite.Run(Area, test, async c =>
        {
            await using var spellwork = await Spellwork.Start();
            var s = await spellwork.GetSpell(133);
            await Ask(c, "A spell", "Using only the coa MCP server's tools, not your own knowledge: what are the name, the rank and the spell family of spell id 133, and how far should that answer be trusted?",
                new[] { s.GetProperty("name").GetString()!, s.GetProperty("rank").GetString()!, "mage" });
        });
    }

    [SkippableFact]
    public async Task Item()
    {
        const string test = "Claude Code answers an item question from this server";
        RunOrNotRun(test);
        await Suite.Run(Area, test, async c =>
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(Settings.ItemsJson));
            var item = doc.RootElement.EnumerateArray().First(i => i.GetProperty("id").GetString() == "10531");
            await Ask(c, "An item", "Using only the coa MCP server's tools, not your own knowledge: what is item 10531 called, what is its item level, and which realms does the answer apply to?",
                new[] { item.GetProperty("name").GetString()!, item.GetProperty("itemLevel").GetRawText() });
        });
    }

    [SkippableFact]
    public async Task ClientPath()
    {
        const string test = "Claude Code answers a client path question from this server";
        RunOrNotRun(test);
        await Suite.Run(Area, test, async c =>
        {
            var spell = ClientManifest.Instance.Dbc("Spell")!;
            await Ask(c, "A client path", "Using only the coa MCP server's tools, not your own knowledge: which archive does the client read DBFilesClient/Spell.dbc from, and what origin does the server give for that archive?",
                new[] { spell.Winner!.Archive, "unknown" });
        });
    }
}

/// <summary>Runs last: production is left as it was found, and nothing the suite wrote is visible.</summary>
[Collection("99 Final state")]
public class FinalStateTests
{
    private const string Area = "Final state";

    [SkippableFact]
    public Task Production_IsLeftAsItWasFound() => Suite.Run(Area, "production is left on valid layers, with no test entries visible", async c =>
    {
        await using var client = await LiveClient.Connect(Suite.Book);
        var status = await client.Call("status");
        c.Book.LayersAtEnd(Suite.LayerLines(status.Text));
        c.Lacks(status.Text, "failed verification", "no layer failed verification");
        c.Lacks(status.Text, "could not be opened", "no layer failed to open");
        foreach (var layer in new[] { "bisbeard", "spells", "paths", "dbc", "notes" })
            c.That(Suite.LayerVersions(status.Text).ContainsKey(layer), $"{layer} is loaded", status.Text);

        // Search keeps test: subjects out, so it could never show a leftover. These can.
        var live = Suite.LiveEntries(status.Text);
        c.That(live is not null, "status gives a count of written entries", Suite.WrittenLine(status.Text));
        if (live is not null && Suite.LiveAtStart is { } atStart)
            c.Equal(atStart - Suite.SweptAtStart, live, "live written entries are back to where the run began, less what it cleared from an earlier run (someone else writing during a run shows here too)");

        foreach (var id in Suite.WrittenIds.Keys.OrderBy(i => i))
        {
            var entry = await client.Call("get", new { id = $"entry:{id}" });
            c.Has(entry.Text, "status: undone", $"entry:{id}, written by this run, is undone");
        }
        c.Book.Observe($"Final state: {Suite.WrittenIds.Count} entries written by this run were each checked with get(entry:n).");
        if (!Suite.WrittenIds.IsEmpty)
            c.Book.ProductionChange($"Wrote {Suite.WrittenIds.Count} entries by acceptance-test on test: subjects, and undid them. Nothing is deleted, so they stay in the store's history as undone. Status at the start said \"{Suite.WrittenLineAtStart}\" and now says \"{Suite.WrittenLine(status.Text)}\".");

        foreach (var slug in WriteTests.Writer.Slugs)
        {
            var page = await client.Call("get", new { id = WriteTests.Writer.SubjectFor(slug) });
            var left = WriteTests.Writer.LeftoverIds(page.Text);
            c.That(left.Count == 0, $"{WriteTests.Writer.SubjectFor(slug)} shows no live entry by the suite", string.Join(", ", left.Select(i => "entry:" + i)));
        }

        var health = await Raw.Post(Suite.RawPaced, null, null, method: HttpMethod.Get, url: Settings.Health.ToString());
        c.Equal(200, health.Status, "/healthz answers");
    });
}
