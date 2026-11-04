using System.Globalization;
using System.Text.RegularExpressions;

namespace CoaMcp.Acceptance;

/// <summary>Paths and winners against the manifest, with the winner worked out by this suite, and every name against the rebuilt listfile and the archive's own hashes.</summary>
[Collection("07 Accuracy paths")]
public class AccuracyPathTests
{
    private const string Area = "Accuracy: paths";

    private static string Bytes(long n) => n.ToString("N0", CultureInfo.InvariantCulture) + " bytes";

    private static void CheckAnswer(Checks c, ClientPath p, string text)
    {
        var who = "path " + p.Path;
        c.Has(text, "\n" + AccuracyItemTests.OneLine(p.Path, 200) + "\n", who + ": named on the page");
        c.Has(text, "trust client", who + ": trust");
        c.Has(text, "the CoA client (unproven)", who + ": realm");
        c.Has(text, "source date", who + ": date");

        if (p.Winner is null)
        {
            c.Has(text, "not in the default client", who + ": no archive the default client opens holds it");
        }
        else if (p.Deleted)
        {
            c.Has(text, $"removed by {p.Winner.Archive} (a delete marker)", who + ": removed by the right archive");
            c.Lacks(text, "winner:", who + ": a removed file has no winner");
            foreach (var older in p.Holders.Where(h => !h.Marker && h.Realm is null && h.Priority >= 0).Take(12))
                c.Has(text, older.Archive, who + ": older copy is listed");
        }
        else if (p.ProjectWinner)
        {
            c.Has(text, $"winner: {p.Winner.Archive}, {Bytes(p.Winner.Size)}, project-built archive (project files are not evidence about Ascension)", who + ": project-built winner");
            if (p.NextNonProject is { Marker: false } b)
                c.Has(text, $"base copy: {b.Archive}, {Bytes(b.Size)}, origin unknown", who + ": the base copy below it");
            else if (p.NextNonProject is { Marker: true } m)
                c.Has(text, $"The next copy that isn't project-built is a delete marker in {m.Archive}", who + ": a delete marker below it");
            else
                c.Has(text, "base copy: none", who + ": says there's no base copy");
        }
        else
        {
            c.Has(text, $"winner: {p.Winner.Archive}, {Bytes(p.Winner.Size)}, origin {p.Winner.Origin}", who + ": winner, size and origin");
        }

        if (!p.Deleted)
        {
            var m = Regex.Match(text, @"^holders, highest priority first: (.+)$", RegexOptions.Multiline);
            if (c.That(m.Success, who + ": holders are listed", text))
            {
                var shown = m.Groups[1].Value.Split("; ");
                for (var i = 0; i < Math.Min(p.Holders.Count, 12); i++)
                {
                    var h = p.Holders[i];
                    var expected = h.Archive + " " + (h.Marker ? "delete marker" : Bytes(h.Size));
                    c.That(i < shown.Length && shown[i].StartsWith(expected, StringComparison.Ordinal), $"{who}: holder {i} in order", $"manifest: {expected}\nserver:   {(i < shown.Length ? shown[i] : "(missing)")}");
                    if (h.Realm is not null) c.Has(i < shown.Length ? shown[i] : "", "realm only", $"{who}: realm-only holder says so");
                    else if (h.Priority < 0) c.Has(i < shown.Length ? shown[i] : "", "never opens", $"{who}: an archive the client never opens says so");
                }
                if (p.Holders.Count > 12) c.Has(text, $"and {p.Holders.Count - 12} more", who + ": says how many holders aren't listed");
            }
        }
    }

    [SkippableFact]
    public Task RandomPaths_AgainstTheManifestAndListfile() => Suite.Run(Area, "100 random paths and winners, plus delete-marker, project-archive and unavailable paths", async c =>
    {
        var client = await Suite.Anonymous();
        var manifest = ClientManifest.Instance;
        var rng = Suite.Rng("paths");

        var listfile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(Settings.Listfile)) if (line.Length > 0) listfile.Add(line.Replace('\\', '/'));

        var all = manifest.Paths.Values.ToList();
        var sample = all.OrderBy(_ => rng.Next()).Take(70)
            .Concat(all.Where(p => p.Deleted).OrderBy(_ => rng.Next()).Take(15))
            .Concat(all.Where(p => p.ProjectWinner).OrderBy(_ => rng.Next()).Take(15))
            .Concat(all.Where(p => p.Winner is null).OrderBy(_ => rng.Next()).Take(10)).ToList();

        var viaGet = 0;
        foreach (var (p, i) in sample.Select((p, i) => (p, i)))
        {
            c.That(listfile.Contains(p.Path), $"path {p.Path} is in the rebuilt listfile");
            var r = await client.Call("client_paths", new { prefix = p.Path });
            if (!c.That(!r.IsError, $"path {p.Path} answers", r.All)) continue;
            CheckAnswer(c, p, r.Text);
            if (i % 5 == 0)
            {
                var page = await client.Call("get", new { id = "path:" + p.Path });
                CheckAnswer(c, p, page.Text);
                viaGet++;
            }
        }
        c.Book.Observe($"Paths: {sample.Count} answers from client_paths and {viaGet} from get(path:...) compared with the manifest.");
    });

    [SkippableFact]
    public Task Names_MatchBothArchiveHashes() => Suite.Run(Area, "every listfile name matches both MPQ hashes", c =>
    {
        var manifest = ClientManifest.Instance;
        c.That(manifest.HashesChecked > 700_000, "the manifest's named entries were all hashed", manifest.HashesChecked.ToString(CultureInfo.InvariantCulture));
        c.That(manifest.HashMismatches.Count == 0, "no name fails its two hashes", $"{manifest.HashMismatches.Count} mismatches, first: {string.Join("\n", manifest.HashMismatches.Take(20))}");
        c.Book.Observe($"{manifest.HashesChecked:N0} archive entries had their names re-hashed locally with this suite's MPQ hash.");
        return Task.CompletedTask;
    });

    [SkippableFact]
    public Task Folders_CountsMatch() => Suite.Run(Area, "folder listings count what the manifest holds", async c =>
    {
        var client = await Suite.Anonymous();
        var manifest = ClientManifest.Instance;
        var rng = Suite.Rng("folders");
        var folders = manifest.Paths.Keys.Where(k => k.Contains('/', StringComparison.Ordinal)).Select(k => k[..k.LastIndexOf('/')]).Distinct().OrderBy(_ => rng.Next()).Take(4)
            .Prepend("dbfilesclient").ToList();
        foreach (var folder in folders)
        {
            var slash = folder + "/";
            var files = manifest.Paths.Keys.Count(k => k.StartsWith(slash, StringComparison.Ordinal) && k.IndexOf('/', slash.Length) < 0);
            var subs = manifest.Paths.Keys.Where(k => k.StartsWith(slash, StringComparison.Ordinal) && k.IndexOf('/', slash.Length) > 0).Select(k => k[slash.Length..k.IndexOf('/', slash.Length)]).Distinct().Count();
            var r = await client.Call("client_paths", new { prefix = slash, limit = 1 });
            c.Has(r.Text, $"{subs} {(subs == 1 ? "folder" : "folders")}, {files} {(files == 1 ? "file" : "files")} directly inside", $"folder {folder}: counts");
        }
    });
}

[Collection("08 Accuracy status")]
public class AccuracyStatusTests
{
    private const string Area = "Accuracy: status counts";

    private static string Line(string status, string layer) => status.Split('\n').FirstOrDefault(l => l.StartsWith($"- {layer}: ", StringComparison.Ordinal)) ?? "";

    [SkippableFact]
    public Task Status_CountsMatchTheSources() => Suite.Run(Area, "all row counts in status", async c =>
    {
        var client = await Suite.Anonymous();
        var status = (await client.Call("status")).Text;
        var manifest = ClientManifest.Instance;

        // Paths.
        var all = manifest.Paths.Values.ToList();
        var paths = Line(status, "paths");
        c.Has(paths, $"{all.Count} client paths", "paths: total");
        var folders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in manifest.Paths.Keys)
            for (var i = k.IndexOf('/'); i >= 0; i = k.IndexOf('/', i + 1)) folders.Add(k[..i]);
        c.Has(paths, $"in {folders.Count} folders", "paths: folders");
        c.Has(paths, $"{all.Count(p => p.Deleted)} removed by a later patch", "paths: removed");
        c.Has(paths, $"{all.Count(p => p.Winner is null)} not in the default client", "paths: not in the default client");
        c.Has(paths, $"{all.Count(p => p.ProjectWinner)} won by a project-built archive", "paths: project-built winners");
        c.Has(paths, $"{manifest.Unnamed} unnamed", "paths: unnamed");
        c.Has(paths, $"{manifest.InternalSkipped} archive internals left out", "paths: internals");

        var listfile = File.ReadLines(Settings.Listfile).Count(l => l.Length > 0);
        var internals = File.ReadLines(Settings.Listfile).Count(l => l is "(listfile)" or "(attributes)" or "(signature)");
        c.Equal(listfile - internals, all.Count, "the rebuilt listfile, less archive internals, has as many names as paths");

        // DBC: read each winner's header, and each base copy's.
        var tables = manifest.DbcTables.ToList();
        long rows = 0;
        var bases = 0;
        foreach (var t in tables)
        {
            if (t.Winner is null || t.Deleted) continue;
            rows += Wdbc.ReadHeader(manifest.Restored(t.Winner, t.Path)).Records;
            if (t.ProjectWinner && t.NextNonProject is { Marker: false } next)
            {
                rows += Wdbc.ReadHeader(manifest.Restored(next, t.Path)).Records;
                bases++;
            }
        }
        var dbc = Line(status, "dbc");
        c.Has(dbc, $"{tables.Count} tables", "dbc: tables");
        c.Has(dbc, $"{rows} rows", "dbc: rows, summed from the files' headers");
        c.Has(dbc, $"{bases} base copies", "dbc: base copies");
        c.Has(dbc, $"{tables.Count(t => t.Deleted)} removed by a delete marker", "dbc: removed");
        c.Has(dbc, "0 unreadable", "dbc: unreadable");

        var fieldConfigs = Directory.GetFiles(Path.Combine(Settings.RepoRoot, "config", "dbc-fields"), "*.json").Length;
        c.Book.Observe($"{fieldConfigs} field config files exist. Status: \"{Regex.Match(dbc, @"\d+ tables with proven field names").Value}\".");

        // Spells: Spell.dbc's records.
        var spell = manifest.Dbc("Spell")!;
        var spellRows = Wdbc.ReadHeader(manifest.Restored(spell.Winner!, spell.Path)).Records;
        c.Has(Line(status, "spells"), $"spell {spellRows}", "spells: Spell.dbc's rows");

        // Items: the three BisBeard lists.
        using var main = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Settings.ItemsJson));
        using var wotlk = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Settings.RealmItems("wotlk")));
        using var octo = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Settings.RealmItems("octo")));
        var items = main.RootElement.GetArrayLength() + wotlk.RootElement.GetArrayLength() + octo.RootElement.GetArrayLength();
        c.Has(Line(status, "bisbeard"), $"item {items}", "bisbeard: items from the three lists");

        // Notes: the reviewed list in the repo, plus the two public project pages.
        using var notes = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Settings.RepoRoot, "config", "notes.json")));
        c.Has(Line(status, "notes"), $"note {notes.RootElement.GetProperty("notes").GetArrayLength()}", "notes: the reviewed list");

        // The rest of BisBeard against the local copy of that very version, which is the best source this machine has for them.
        CheckAgainstLocalLayer(c, status, "bisbeard", new[] { "class", "guide", "marker", "rules", "spec", "talents" });
        CheckAgainstLocalLayer(c, status, "notes", new[] { "project" });
    });

    private static void CheckAgainstLocalLayer(Checks c, string status, string layer, string[] types)
    {
        var file = Path.Combine(Settings.LocalLayers, layer + ".sqlite");
        using var db = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = file, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        using var v = db.CreateCommand();
        v.CommandText = "SELECT version FROM layer_meta";
        var localVersion = v.ExecuteScalar()?.ToString();
        var liveVersion = Suite.LayerVersions(status).GetValueOrDefault(layer);
        if (localVersion != liveVersion)
        {
            c.Book.Observe($"Local {layer} layer is v{localVersion}, production serves v{liveVersion}: its other type counts were checked anyway, since a rebuild changes dates, not counts.");
        }
        foreach (var type in types)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM entities WHERE type = $t";
            cmd.Parameters.AddWithValue("$t", type);
            c.Has(Line(status, layer), $"{type} {cmd.ExecuteScalar()}", $"{layer}: {type} count");
        }
    }
}

[Collection("09 Accuracy spot checks")]
public class SpotCheckTests
{
    private const string Area = "Accuracy: spot checks";

    [SkippableFact]
    public Task Fireball_FindsFireballFirst() => Suite.Run(Area, "fireball finds Fireball first", async c =>
    {
        var client = await Suite.Anonymous();
        var r = await client.Call("search", new { query = "fireball", limit = 5 }, "spot check: fireball finds Fireball first");
        var first = r.Text.Split('\n').FirstOrDefault(l => l.StartsWith("1. ", StringComparison.Ordinal)) ?? "";
        c.That(Regex.IsMatch(first, @"^1\. \[[^\]]+\] Fireball \("), "the first hit is titled exactly Fireball", first);
        c.Has(first, "spell:", "and it's a spell");
    });

    [SkippableFact]
    public Task Realms_ClientAndBisBeardLabelCorrectly() => Suite.Run(Area, "client and BisBeard realms label correctly", async c =>
    {
        var client = await Suite.Anonymous();
        var spell = await client.Call("get", new { id = "spell:133" });
        c.Has(spell.Text, "realm: the CoA client (unproven)", "a client fact says the CoA client, unproven");
        c.Lacks(spell.Text, "realm: CoA", "and isn't claimed for the CoA realm");

        var item = await client.Call("get", new { id = "item:10531" });
        c.Has(item.Text, "realm: CoA, Classic or S10 (unproven)", "a main-list item isn't narrowed to one realm");

        var cls = await client.Call("get", new { id = "class:coa:barbarian" });
        c.Has(cls.Text, "realm: CoA (proven)", "CoA realm data is proven for CoA");

        var path = await client.Call("client_paths", new { prefix = "DBFilesClient/Spell.dbc" });
        c.Has(path.Text, "realm the CoA client (unproven)", "a client path says the CoA client, unproven");

        var row = await client.Call("dbc_rows", new { table = "Map", limit = 1 });
        c.Has(row.Text, "realm the CoA client (unproven)", "a DBC row says the CoA client, unproven");

        var search = await client.Call("search", new { query = "fireball", limit = 20 });
        foreach (var hit in search.Text.Split('\n').Where(l => Regex.IsMatch(l, @"^\d+\. \[")))
            c.That(Regex.IsMatch(hit, @"^\d+\. \[[^|]+\|"), "every search hit starts with its realm", hit);
    });

    [SkippableFact]
    public Task Positions_SayApproximate() => Suite.Run(Area, "positions say approximate", async c =>
    {
        var client = await Suite.Anonymous();
        var r = await client.Call("get", new { id = "marker:worldforge-alterac-mountains-smudge-s-secret-recipe" }, "spot check: a marker's position is approximate");
        c.Has(r.Text, "Approximate location", "a map marker says approximate");
        c.Has(r.Text, "not world coordinates", "and that these aren't world coordinates");

        // Every marker a search turns up, not only the one above.
        var found = await client.Call("search", new { query = "worldforge alterac", limit = 5 });
        foreach (var id in Regex.Matches(found.Text, @"marker:[a-z0-9.-]+").Select(m => m.Value).Distinct().Take(3))
        {
            var page = await client.Call("get", new { id });
            c.Has(page.Text, "pproximate", id + " says approximate");
        }
    });

    [SkippableFact]
    public Task ProjectMaterial_SaysItIsNotEvidence() => Suite.Run(Area, "project archives and project pages aren't presented as Ascension", async c =>
    {
        var client = await Suite.Anonymous();
        var manifest = ClientManifest.Instance;

        var file = manifest.Paths.Values.First(p => p.ProjectWinner);
        var path = await client.Call("client_paths", new { prefix = file.Path }, "spot check: a project-archive file says so");
        c.Has(path.Text, "project-built archive (project files are not evidence about Ascension)", $"{file.Path} says so");

        var list = await client.Call("client_paths", new { prefix = file.Path[..file.Path.LastIndexOf('/')] + "/", limit = 100 });
        var line = list.Text.Split('\n').FirstOrDefault(l => l.StartsWith(file.Path + " |", StringComparison.OrdinalIgnoreCase));
        if (line is not null) c.Has(line, "(project-built archive)", $"{file.Path} says so in a list too");

        var spell = await client.Call("get", new { id = "spell:133" });
        c.Has(spell.Text, "project-built archive", "a spell from a project-built Spell.dbc says so");

        var site = await client.Call("get", new { id = "project:faq" }, "spot check: a project site page says it isn't evidence");
        c.Has(site.Text, "It isn't evidence about Ascension", "a coa-development.org page says it isn't evidence");
        c.Has(site.Text, "the emulator project, not Ascension", "its source says so too");
    });

    [SkippableFact]
    public Task Provenance_EveryAnswerShowsItsSource() => Suite.Run(Area, "every kind of answer shows trust, source, date and realm", async c =>
    {
        var client = await Suite.Anonymous();
        foreach (var (tool, args) in new (string, object)[]
                 {
                     ("get", new { id = "spell:133" }), ("get", new { id = "item:10531" }), ("get", new { id = "note:mpq-listfile" }), ("get", new { id = "dbc:Map:0" }),
                     ("spell_compare", new { idA = "133", idB = "143" }), ("spell_scaling", new { id = "133", level = 80 }),
                     ("client_paths", new { prefix = "DBFilesClient/Spell.dbc" }), ("client_paths", new { prefix = "DBFilesClient/", limit = 1 }),
                     ("dbc_tables", new { limit = 1 }), ("dbc_rows", new { table = "Map", limit = 1 }),
                 })
        {
            var r = await client.Call(tool, args);
            var who = $"{tool} {System.Text.Json.JsonSerializer.Serialize(args)}";
            c.That(Regex.IsMatch(r.Text, @"trust:? (client|derived|cache)"), who + " shows trust", ReportBook.Cut(r.Text, 600));
            c.That(Regex.IsMatch(r.Text, @"source:? "), who + " shows its source", ReportBook.Cut(r.Text, 600));
            c.That(Regex.IsMatch(r.Text, @"(source date:? \d{4}-\d\d-\d\d)"), who + " shows the date", ReportBook.Cut(r.Text, 600));
            c.That(Regex.IsMatch(r.Text, @"realm:? "), who + " shows the realm", ReportBook.Cut(r.Text, 600));
        }
    });
}
