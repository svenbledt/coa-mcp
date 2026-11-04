using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CoaMcp.Acceptance;

/// <summary>
/// The one test that changes production: it rebuilds the small notes layer as a new version, publishes it with the real publisher during concurrent reads,
/// and rolls back the supported way, by pointing current.json at the previous entry. It leaves the new version's file on the server, as the publisher's design does.
/// </summary>
[Collection("15 Live update")]
public class LiveUpdateTests
{
    private const string Area = "Live update";
    private const string Current = "/data/coamcp/current.json";
    private const string Test = "a new notes layer goes live during concurrent reads with no failed request, then rolls back";
    private static readonly TimeSpan ReloadWait = TimeSpan.FromSeconds(120);

    private sealed class Readers
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly List<Task> _tasks = new();
        public int Calls, Failed;
        public readonly List<string> Failures = new();
        public volatile string? Version;
        public readonly List<string> Seen = new();

        public void Start(LiveClient client, int count)
        {
            for (var i = 0; i < count; i++)
            {
                var n = i;
                _tasks.Add(Task.Run(async () =>
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        // Modest: six readers, one call every four seconds each, all of it reading.
                        await Task.Delay(TimeSpan.FromSeconds(4), CancellationToken.None);
                        await One(client, n);
                    }
                }));
            }
        }

        private async Task One(LiveClient client, int n)
        {
            Interlocked.Increment(ref Calls);
            try
            {
                Reply r;
                switch (n % 3)
                {
                    case 0:
                        r = await client.Call("status");
                        if (Suite.LayerVersions(r.Text).TryGetValue("notes", out var v))
                        {
                            Version = v;
                            lock (Seen) if (Seen.Count == 0 || Seen[^1] != v) Seen.Add(v);
                        }
                        else Fail("status lists no notes version: " + r.All);
                        break;
                    case 1:
                        r = await client.Call("get", new { id = "note:mpq-listfile" });
                        if (!r.Text.Contains("trust: derived", StringComparison.Ordinal)) Fail("note page: " + ReportBook.Cut(r.All, 200));
                        break;
                    default:
                        r = await client.Call("search", new { query = "client", topic = "notes", limit = 3 });
                        if (!r.Text.Contains("note:", StringComparison.Ordinal)) Fail("note search: " + ReportBook.Cut(r.All, 200));
                        break;
                }
                if (r.Refused) Fail("refused: " + ReportBook.Cut(r.All, 200));
            }
            catch (Exception ex)
            {
                Fail(ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void Fail(string what)
        {
            Interlocked.Increment(ref Failed);
            lock (Failures) Failures.Add(what);
        }

        public async Task Stop()
        {
            _stop.Cancel();
            await Task.WhenAll(_tasks);
        }
    }

    private static async Task<bool> WaitFor(Readers readers, string version)
    {
        var deadline = DateTime.UtcNow + ReloadWait;
        while (DateTime.UtcNow < deadline)
        {
            if (readers.Version == version) return true;
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
        return false;
    }

    /// <summary>Rewrites current.json atomically with one layer's entry put back. Same temp-then-rename as the publisher's.</summary>
    private static async Task<Shell.Result> PointNotesAt(JsonNode previousEntry)
    {
        var read = await Shell.Ssh($"cat {Current}");
        if (read.Exit != 0) return read;
        var manifest = JsonNode.Parse(read.Out)!;
        manifest["layers"]!["notes"] = previousEntry.DeepClone();
        var json = manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        return await Shell.Ssh($"cat > {Current}.tmp <<'COAMCP_MANIFEST_END'\n{json}\nCOAMCP_MANIFEST_END\nchmod 644 {Current}.tmp && mv {Current}.tmp {Current}\n");
    }

    [SkippableFact]
    public async Task PublishDuringReads_ThenRollBack()
    {
        if (!Settings.AllowLiveUpdate)
        {
            Suite.NotRun(Area, Test, "COA_ACCEPT_LIVE_UPDATE is not 1: this test changes production, so it has to be asked for");
            return;
        }
        await Suite.Run(Area, Test, async c =>
        {
            var read = await Shell.Ssh($"cat {Current}");
            c.Equal(0, read.Exit, "current.json can be read");
            var manifest = JsonNode.Parse(read.Out)!;
            var previous = manifest["layers"]!["notes"]!.DeepClone();
            var previousVersion = previous["version"]!.GetValue<string>();
            var otherLayers = manifest["layers"]!.AsObject().Where(l => l.Key != "notes").ToDictionary(l => l.Key, l => l.Value!.ToJsonString());

            var folder = Directory.CreateTempSubdirectory("coa-acceptance-notes-").FullName;
            var layer = Path.Combine(folder, "notes.sqlite");
            await using var client = await LiveClient.Connect(Suite.Book);
            var live = Suite.LayerVersions((await client.Call("status")).Text)["notes"];
            c.Equal(previousVersion, live, "status agrees with current.json before the update");

            var build = await Shell.Pipeline("notes", "--out", layer);
            if (!c.Equal(0, build.Exit, "the notes layer builds")) { c.Fail("notes build output", build.Out + build.Err); return; }

            string newVersion;
            using (var db = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = layer, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            {
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT version FROM layer_meta";
                newVersion = (string)cmd.ExecuteScalar()!;
            }
            c.That(newVersion != previousVersion, "the rebuild has a new version", newVersion);

            var readers = new Readers();
            readers.Start(client, 6);
            var published = false;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                var publish = await Shell.Pipeline("publish", "--layer", layer, "--host", Settings.SshHost);
                c.Book.Transcript("Publishing the notes layer", (publish.Out + publish.Err).Trim());
                if (!c.Equal(0, publish.Exit, "publish succeeds")) { c.Fail("publish output", publish.Out + publish.Err); return; }
                published = true;
                c.Book.ProductionChange($"Published a rebuilt notes layer v{newVersion} with the real publisher. Live for about a minute; its file stays in /data/coamcp/layers/notes/, as the publisher keeps older versions.");

                c.That(await WaitFor(readers, newVersion), "status moves to the new version within two minutes", $"still on {readers.Version}");
                var page = await client.Call("get", new { id = "note:mpq-listfile" });
                c.Has(page.Text, $"layer: notes v{newVersion}", "pages come from the new version");

                await Task.Delay(TimeSpan.FromSeconds(10));
            }
            finally
            {
                if (published)
                {
                    var rollback = await PointNotesAt(previous);
                    c.Equal(0, rollback.Exit, "the rollback was written");
                    if (rollback.Exit != 0) c.Fail("rollback output", rollback.Out + rollback.Err);
                    c.That(await WaitFor(readers, previousVersion), "status moves back to the previous version within two minutes", $"still on {readers.Version}");
                    c.Book.ProductionChange($"Rolled the notes layer back to v{previousVersion} by pointing current.json at its previous entry, as the README describes. Production ends on the version it started on.");
                    await Task.Delay(TimeSpan.FromSeconds(10));
                }
                await readers.Stop();
                Directory.Delete(folder, recursive: true);
            }

            c.Equal(0, readers.Failed, "no failed request across the update and the rollback");
            if (readers.Failed > 0) c.Fail("failed requests", string.Join("\n", readers.Failures.Take(50)));
            c.That(readers.Calls >= 30, "enough reads ran to mean something", readers.Calls.ToString());
            c.Book.Observe($"Live update: {readers.Calls} concurrent reads ran across the publish and rollback, {readers.Failed} failed. Versions seen by the readers, in order: {string.Join(" -> ", readers.Seen)}.");

            var end = JsonNode.Parse((await Shell.Ssh($"cat {Current}")).Out)!;
            c.Equal(previousVersion, end["layers"]!["notes"]!["version"]!.GetValue<string>(), "current.json names the previous notes version again");
            foreach (var (name, json) in otherLayers)
                c.Equal(json, end["layers"]![name]!.ToJsonString(), $"the {name} layer's entry wasn't touched");
            var finalStatus = await client.Call("status");
            c.Equal(previousVersion, Suite.LayerVersions(finalStatus.Text)["notes"], "status ends on the previous version");
            c.Lacks(finalStatus.Text, "failed verification", "no layer reports a failed verification");
        });
    }
}
