using System.Security.Cryptography;
using System.Text;
using CoaMcp.Core;
using CoaMcp.Pipeline.Knowledge;
using CoaMcp.Pipeline.Publish;
using CoaMcp.Server;
using CoaMcp.Server.Admin;
using Microsoft.Data.Sqlite;

namespace CoaMcp.Pipeline.Tests;

public sealed class KnowledgeTests : IDisposable
{
    private const string Token = "tok-9f8e7d6c5b4a39281706f5e4d3c2b1a0-SECRET";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "coamcp-knowledge-" + Guid.NewGuid().ToString("N"));
    private string Snap => Path.Combine(_dir, "snap.sqlite");
    private string Out => Path.Combine(_dir, "knowledge");
    private string Entries => Path.Combine(Out, "entries");

    public KnowledgeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public async Task Export_EntriesFolderBrieflyLocked_RetriesAndSucceeds()
    {
        using (var s = Store()) Add(s, content: "first");
        KnowledgeExporter.Export(Snap, Out);
        SqliteConnection.ClearAllPools();
        using (var s = Store()) Add(s, content: "second");
        SqliteConnection.ClearAllPools();

        // On Windows an open file inside a folder stops the folder being renamed, as a virus scanner does for a moment.
        var held = new FileStream(Path.Combine(Entries, "00000001.md"), FileMode.Open, FileAccess.Read, FileShare.Read);
        var released = Task.Delay(400).ContinueWith(_ => held.Dispose());

        var r = KnowledgeExporter.Export(Snap, Out);
        await released;

        Assert.Equal(1, r.Added);
        Assert.Equal("00000001.md,00000002.md", Names(Entries));
    }

    [SkippableFact]
    public void Export_EntriesFolderStaysLocked_FailsLoudly_AndChangesNothing()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "An open file blocks a folder rename only on Windows.");
        using (var s = Store()) Add(s, content: "first");
        KnowledgeExporter.Export(Snap, Out);
        SqliteConnection.ClearAllPools();
        var before = Tree(Entries);
        using (var s = Store()) Add(s, content: "second");
        SqliteConnection.ClearAllPools();

        using (new FileStream(Path.Combine(Entries, "00000001.md"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.ThrowsAny<Exception>(() => KnowledgeExporter.Export(Snap, Out));

        AssertSameTree(before, Tree(Entries));
        Assert.Equal(new[] { "entries" }, Directory.GetDirectories(Out).Select(Path.GetFileName));
    }

    // ---- helpers ----

    private static long Add(WriteStore s, string subject = "item:1", string kind = "note", string content = "text", string author = "Ann",
        Trust trust = Trust.Memory, string? field = null, string? value = null, string? position = null) =>
        s.Add(new NewEntry(subject, kind, new[] { "coa" }, trust, content, "{\"description\":\"seen in game\"}", field, value, position, author)).Id;

    private WriteStore Store(string? path = null) => new(path ?? Snap);

    private static string Names(string dir) => string.Join(",", Directory.GetFiles(dir).Select(Path.GetFileName).Order(StringComparer.Ordinal));

    private static Dictionary<string, byte[]> Tree(string dir) =>
        !Directory.Exists(dir) ? new() : Directory.GetFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(dir, f), File.ReadAllBytes);

    private static void AssertSameTree(Dictionary<string, byte[]> a, Dictionary<string, byte[]> b)
    {
        Assert.Equal(a.Keys.Order(), b.Keys.Order());
        foreach (var (k, v) in a) Assert.Equal(v, b[k]);
    }

    private static List<object?[]> Rows(string path, string sql)
    {
        using var c = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var rows = new List<object?[]>();
        while (r.Read()) rows.Add(Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? null : r.GetValue(i)).ToArray());
        return rows;
    }

    private const string AllColumns = "SELECT id, subject_id, kind, requested_kind, realm_json, trust, content_md, evidence_json, field, value, position_json, author, created_at, supersedes_id, undone_at, undone_by FROM entries ORDER BY id";

    // ---- export ----

    [Fact]
    public void Export_WritesOneFilePerEntry()
    {
        using (var s = Store()) { Add(s, "item:1"); Add(s, "spell:2", content: "second"); Add(s, "npc:3", author: "Bob"); }

        var r = KnowledgeExporter.Export(Snap, Out);

        Assert.Equal("00000001.md,00000002.md,00000003.md", Names(Entries));
        Assert.Equal(3, r.Added);
        Assert.Equal(0, r.Changed);
        var text = File.ReadAllText(Path.Combine(Entries, "00000002.md"));
        Assert.StartsWith("---\nid: 2\n", text);
        Assert.EndsWith("---\nsecond", text);
        Assert.Contains("\nsubject_id: \"spell:2\"\n", text);
        // No stray folders are left next to entries.
        Assert.Equal(new[] { "entries" }, Directory.GetDirectories(Out).Select(Path.GetFileName));
    }

    [Fact]
    public void Export_Rerun_NoChanges()
    {
        using (var s = Store()) { Add(s, content: "a\r\nb"); Add(s, "note:x", "page", "# T\n"); Add(s, field: "level", value: "10", subject: "spell:9"); }
        KnowledgeExporter.Export(Snap, Out);
        var before = Tree(Entries);

        var again = KnowledgeExporter.Export(Snap, Out);

        AssertSameTree(before, Tree(Entries));
        Assert.Equal(0, again.Added);
        Assert.Equal(0, again.Changed);
        Assert.Equal(3, again.Unchanged);
        // Stable field order, LF in the front matter, UTF-8 without a byte-order mark.
        foreach (var bytes in before.Values)
        {
            Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
            var head = Encoding.UTF8.GetString(bytes).Split("\n---\n")[0];
            Assert.DoesNotContain('\r', head);
            var keys = head.Split('\n').Skip(1).Select(l => l[..l.IndexOf(':')]).ToArray();
            Assert.Equal(new[] { "id", "status", "subject_id", "kind", "requested_kind", "trust", "realm_json", "field", "value", "position_json", "evidence_json", "author", "created_at", "supersedes_id", "undone_at", "undone_by" }, keys);
        }
    }

    [Fact]
    public void Export_ReportsChangedEntries()
    {
        long id;
        using (var s = Store()) { id = Add(s); Add(s, "item:2"); }
        KnowledgeExporter.Export(Snap, Out);
        using (var s = Store()) { s.Undo(id, "owner"); Add(s, "item:3"); }

        var r = KnowledgeExporter.Export(Snap, Out);

        Assert.Equal(1, r.Added);
        Assert.Equal(1, r.Changed);
        Assert.Equal(1, r.Unchanged);
    }

    [Fact]
    public void Export_KeepsUndoneAndSuperseded_Marked()
    {
        long undone, oldPage, newPage;
        using (var s = Store())
        {
            undone = Add(s, "item:1");
            oldPage = Add(s, "note:x", "page", "v1");
            newPage = Add(s, "note:x", "page", "v2");
            Add(s, "item:2");
            s.Undo(undone, "owner");
        }

        KnowledgeExporter.Export(Snap, Out);

        string Read(long id) => File.ReadAllText(Path.Combine(Entries, $"{id:D8}.md"));
        Assert.Contains("\nstatus: \"undone\"\n", Read(undone));
        Assert.Contains("\nundone_by: \"owner\"\n", Read(undone));
        Assert.Matches("undone_at: \"20\\d\\d-", Read(undone));
        Assert.Contains("\nstatus: \"superseded\"\n", Read(oldPage));
        Assert.Contains("\nstatus: \"live\"\n", Read(newPage));
        Assert.Contains($"\nsupersedes_id: {oldPage}\n", Read(newPage));
        Assert.Contains("\nstatus: \"live\"\n", Read(4));
    }

    public static IEnumerable<object[]> HostileContents() => new[]
    {
        new object[] { "plain" },
        new object[] { "" },
        new object[] { "---\nid: 99\nstatus: \"live\"\n---\nbody" },
        new object[] { "---" },
        new object[] { "\n---\n---\n" },
        new object[] { "line1\r\nline2\rline3\n" },
        new object[] { "trailing space \t \n\n\n" },
        new object[] { "\ufeffstarts with a mark" },
        new object[] { "unicode \u00e9 \u4e2d \U0001F600 and \"quotes\" and \\ backslash" },
        new object[] { "id: 5\nauthor: \"x\"\n" },
    };

    [Theory]
    [MemberData(nameof(HostileContents))]
    public void Export_PreservesContentExactly(string content)
    {
        using (var s = Store()) Add(s, content: content);

        KnowledgeExporter.Export(Snap, Out);

        var bytes = File.ReadAllBytes(Path.Combine(Entries, "00000001.md"));
        var contentBytes = new UTF8Encoding(false).GetBytes(content);
        Assert.True(bytes.AsSpan(bytes.Length - contentBytes.Length).SequenceEqual(contentBytes), "the body is not the content, byte for byte");
        var parsed = KnowledgeFormat.Parse(bytes, "00000001.md");
        Assert.Equal(content, parsed.ContentMd);
        Assert.Equal(1, parsed.Id);
        Assert.Equal("item:1", parsed.SubjectId);
    }

    [Fact]
    public void Export_StoresRawJsonColumnsExactly()
    {
        using (var s = Store()) Add(s, position: "{\"map\": \"A\",\"leftPct\": 1.5,\"topPct\":2}");

        KnowledgeExporter.Export(Snap, Out);

        var e = KnowledgeFormat.Parse(File.ReadAllBytes(Path.Combine(Entries, "00000001.md")), "x");
        Assert.Equal("{\"map\": \"A\",\"leftPct\": 1.5,\"topPct\":2}", e.PositionJson);
        Assert.Equal("{\"description\":\"seen in game\"}", e.EvidenceJson);
        Assert.Equal("[\"coa\"]", e.RealmJson);
    }

    private void HostileStore(object id)
    {
        using var c = new SqliteConnection($"Data Source={Snap};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        // Not the server's schema: a plain column holds anything, as a damaged or hostile file could.
        cmd.CommandText = """
            CREATE TABLE entries(id, subject_id, kind, requested_kind, realm_json, trust, content_md, evidence_json, field, value, position_json, author, created_at, supersedes_id, undone_at, undone_by);
            INSERT INTO entries VALUES ($id, 's', 'note', 'note', '[]', 5, 'c', '{}', NULL, NULL, NULL, 'a', '2026-01-01T00:00:00Z', NULL, NULL, NULL);
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    [Theory]
    [InlineData("../../escaped")]
    [InlineData("..\\..\\escaped")]
    [InlineData("/etc/passwd")]
    [InlineData("1; DROP")]
    public void Export_HostileTextId_CannotEscapeFolder(string id)
    {
        HostileStore(id);
        Directory.CreateDirectory(Entries);
        File.WriteAllText(Path.Combine(Entries, "00000007.md"), "keep");
        var before = Tree(Out);

        var ex = Assert.Throws<KnowledgeException>(() => KnowledgeExporter.Export(Snap, Out));

        Assert.Contains("id", ex.Message);
        AssertSameTree(before, Tree(Out));
        Assert.Empty(Directory.GetFiles(_dir, "escaped*", SearchOption.AllDirectories));
        Assert.Equal(new[] { "entries" }, Directory.GetDirectories(Out).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData(-5L)]
    [InlineData(0L)]
    public void Export_NonPositiveId_Refused(long id)
    {
        HostileStore(id);

        Assert.Throws<KnowledgeException>(() => KnowledgeExporter.Export(Snap, Out));

        Assert.False(Directory.Exists(Entries));
    }

    [Fact]
    public void Export_BadSnapshot_Refused_LeavesFilesAlone()
    {
        Directory.CreateDirectory(Entries);
        File.WriteAllText(Path.Combine(Entries, "00000001.md"), "keep");
        File.WriteAllText(Snap, new string('x', 9000));   // not a database
        var before = Tree(Out);

        Assert.Throws<KnowledgeException>(() => KnowledgeExporter.Export(Snap, Out));
        Assert.Throws<KnowledgeException>(() => KnowledgeExporter.Export(Path.Combine(_dir, "missing.sqlite"), Out));

        AssertSameTree(before, Tree(Out));
    }

    [Fact]
    public void Export_ScanWarnsOnSecretOrLocalPath()
    {
        const string secret = "Bearer abcdefghijklmnop1234567890";
        using (var s = Store())
        {
            Add(s, "item:1", content: "fine");
            Add(s, "item:2", content: "the file is at C:\\Users\\Admin\\notes.txt");
            Add(s, "item:3", content: "header " + secret);
        }

        var r = KnowledgeExporter.Export(Snap, Out);

        Assert.Equal(2, r.Warnings.Count);
        Assert.Contains(r.Warnings, w => w.Id == 2 && w.Kind == "local drive path" && w.Column == "content_md");
        Assert.Contains(r.Warnings, w => w.Id == 3 && w.Kind == "bearer token");
        var o = new StringWriter();
        r.WriteSummary(o);
        Assert.Contains("entry 3", o.ToString());
        Assert.Contains("bearer token", o.ToString());
        Assert.DoesNotContain("abcdefghijklmnop", o.ToString());
        Assert.DoesNotContain("Users", o.ToString());
        // Warnings only: the entry is still exported as written.
        Assert.Equal(3, Directory.GetFiles(Entries).Length);
    }

    [Fact]
    public void Export_TokenNeverInOutput()
    {
        var old = Environment.GetEnvironmentVariable("COA_MCP_BEARER_TOKEN");
        Environment.SetEnvironmentVariable("COA_MCP_BEARER_TOKEN", Token);
        try
        {
            using (var s = Store()) Add(s, content: "api_key = " + Token + " and Bearer " + Token);

            var r = KnowledgeExporter.Export(Snap, Out);
            var o = new StringWriter();
            r.WriteSummary(o);

            Assert.NotEmpty(r.Warnings);
            Assert.DoesNotContain(Token, o.ToString());
            Assert.DoesNotContain(Token, string.Join("|", r.Warnings.Select(w => w.ToString())));
        }
        finally { Environment.SetEnvironmentVariable("COA_MCP_BEARER_TOKEN", old); }
    }

    [Fact]
    public void Export_SkipsTestSubjects_ByDefault_ListsAuthors()
    {
        using (var s = Store())
        {
            Add(s, "item:1", author: "Ann"); Add(s, "item:2", author: "Ann"); Add(s, "item:3", author: "Bob\u001b[31m");
            Add(s, "test:probe", author: "Ann"); Add(s, "test:other", author: "Eve");
        }

        var r = KnowledgeExporter.Export(Snap, Out);

        Assert.Equal(3, Directory.GetFiles(Entries).Length);
        Assert.Equal(2, r.SkippedTest);
        var o = new StringWriter();
        r.WriteSummary(o);
        var text = o.ToString();
        Assert.Contains("Ann: 2", text);
        Assert.Contains("Skipped 2 entries", text);
        Assert.Contains("test:", text);
        Assert.DoesNotContain('\u001b', text);   // an author name can't drive the terminal

        var all = KnowledgeExporter.Export(Snap, Out, includeTest: true);
        Assert.Equal(5, Directory.GetFiles(Entries).Length);
        Assert.Equal(0, all.SkippedTest);
    }

    [Fact]
    public void Export_EmptySnapshot_RefusedAndNeverWipes()
    {
        using (var s = Store()) { Add(s); Add(s, "item:2"); }
        KnowledgeExporter.Export(Snap, Out);
        var before = Tree(Out);
        var empty = Path.Combine(_dir, "empty.sqlite");
        using (var s = Store(empty)) Add(s);
        using (var c = new SqliteConnection($"Data Source={empty};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM entries";
            cmd.ExecuteNonQuery();
        }

        var ex = Assert.Throws<KnowledgeException>(() => KnowledgeExporter.Export(empty, Out));

        Assert.Contains("empty", ex.Message);
        AssertSameTree(before, Tree(Out));
    }

    [Fact]
    public void Export_EmptySnapshot_NothingExported_IsNotAnError()
    {
        var empty = Path.Combine(_dir, "empty.sqlite");
        using (var s = Store(empty)) Add(s);
        using (var c = new SqliteConnection($"Data Source={empty};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM entries";
            cmd.ExecuteNonQuery();
        }

        var r = KnowledgeExporter.Export(empty, Out);

        Assert.Equal(0, r.Added);
        Assert.False(Directory.Exists(Entries));   // nothing to keep, and nothing invented
    }

    [Fact]
    public void Export_FilesNoLongerInStore_AreKeptAndReported()
    {
        using (var s = Store()) { Add(s); Add(s, "item:2"); }
        KnowledgeExporter.Export(Snap, Out);
        File.WriteAllText(Path.Combine(Entries, "00000099.md"), "from a store that has since been replaced");
        File.WriteAllText(Path.Combine(Entries, "notes.txt"), "not an entry");

        var r = KnowledgeExporter.Export(Snap, Out);

        Assert.Equal(new[] { "00000099.md", "notes.txt" }, r.Kept.Order());
        Assert.Equal("from a store that has since been replaced", File.ReadAllText(Path.Combine(Entries, "00000099.md")));
        Assert.True(File.Exists(Path.Combine(Entries, "notes.txt")));
        var o = new StringWriter();
        r.WriteSummary(o);
        Assert.Contains("00000099.md", o.ToString());
    }

    [Fact]
    public void Export_Failure_LeavesEntriesUntouched_AndNoTempFolder()
    {
        using (var s = Store()) { Add(s); Add(s, "item:2"); }
        KnowledgeExporter.Export(Snap, Out);
        var before = Tree(Out);
        using (var c = new SqliteConnection($"Data Source={Snap};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE entries SET content_md = 'changed' WHERE id = 1; UPDATE entries SET id = -3 WHERE id = 2";
            cmd.ExecuteNonQuery();
        }

        Assert.Throws<KnowledgeException>(() => KnowledgeExporter.Export(Snap, Out));

        AssertSameTree(before, Tree(Out));
        Assert.Equal(new[] { "entries" }, Directory.GetDirectories(Out).Select(Path.GetFileName));
    }

    // ---- restore ----

    [Fact]
    public void Restore_RoundTrip_MatchesStore()
    {
        long a, page1;
        using (var s = Store())
        {
            a = Add(s, "item:1", content: "---\nid: 7\n---\r\nweird");
            page1 = Add(s, "note:x", "page", "v1", trust: Trust.Observed);
            Add(s, "note:x", "page", "v2", trust: Trust.Observed);
            Add(s, "spell:5", field: "level", value: "10", position: "{\"map\":\"Azeroth\",\"leftPct\":10,\"topPct\":20}", author: "Zed_1");
            Add(s, "test:probe");
            s.Undo(a, "owner");
        }
        KnowledgeExporter.Export(Snap, Out, includeTest: true);
        var restored = Path.Combine(_dir, "restored", "store.sqlite");

        var n = KnowledgeRestorer.Restore(Out, restored);

        Assert.Equal(5, n);
        var want = Rows(Snap, AllColumns);
        var got = Rows(restored, AllColumns);
        Assert.Equal(want.Count, got.Count);
        for (var i = 0; i < want.Count; i++) Assert.Equal(want[i], got[i]);   // every column of every row
        Assert.Equal(page1, (long)Rows(restored, "SELECT supersedes_id FROM entries WHERE id = 3")[0][0]!);

        // The search index is rebuilt, and the server opens the restored file and keeps working with it.
        Assert.Equal(1, (long)Rows(restored, "SELECT COUNT(*) FROM entries_fts WHERE entries_fts MATCH 'weird'")[0][0]!);
        using (var s = Store(restored))
        {
            Assert.Equal(4, s.Get(4, out _)!.Id);
            Assert.Equal(6, Add(s, "item:9"));   // ids continue after the highest restored one
            Assert.Equal("owner", s.Get(1, out _)!.UndoneBy);
        }
    }

    [Fact]
    public void Restore_Schema_IsTheServersSchema()
    {
        using (var s = Store()) Add(s);
        KnowledgeExporter.Export(Snap, Out);
        var restored = Path.Combine(_dir, "restored.sqlite");

        KnowledgeRestorer.Restore(Out, restored);

        const string schema = "SELECT type, name, tbl_name, sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY name";
        var server = Rows(Snap, schema);
        Assert.NotEmpty(server);
        // The server file may be checked out with CRLF; the SQL is the same text either way.
        static List<string?[]> Flat(List<object?[]> rows) => rows.Select(r => r.Select(c => (c as string)?.Replace("\r\n", "\n")).ToArray()).ToList();
        Assert.Equal(Flat(server), Flat(Rows(restored, schema)));
        Assert.Equal(Rows(Snap, "SELECT version FROM schema_version"), Rows(restored, "SELECT version FROM schema_version"));
    }

    [Fact]
    public void Restore_NonEmptyStore_Refused()
    {
        using (var s = Store()) Add(s, content: "production");
        KnowledgeExporter.Export(Snap, Out);
        var target = Path.Combine(_dir, "live.sqlite");
        using (var s = Store(target)) Add(s, content: "already here");
        var before = File.ReadAllBytes(target);

        var ex = Assert.Throws<KnowledgeException>(() => KnowledgeRestorer.Restore(Out, target));

        Assert.Contains("already has", ex.Message);
        Assert.Equal(before, File.ReadAllBytes(target));
        Assert.Equal(1, (long)Rows(target, "SELECT COUNT(*) FROM entries")[0][0]!);
    }

    [Fact]
    public void Restore_UnreadableExistingFile_Refused()
    {
        using (var s = Store()) Add(s);
        KnowledgeExporter.Export(Snap, Out);
        var target = Path.Combine(_dir, "junk.sqlite");
        File.WriteAllText(target, new string('x', 9000));

        Assert.Throws<KnowledgeException>(() => KnowledgeRestorer.Restore(Out, target));

        Assert.Equal(new string('x', 9000), File.ReadAllText(target));
    }

    [Theory]
    [InlineData("swap-keys")]
    [InlineData("extra-key")]
    [InlineData("bad-value")]
    [InlineData("wrong-id")]
    [InlineData("no-fence")]
    [InlineData("bad-status")]
    [InlineData("truncated")]
    public void Restore_DamagedFile_RefusedAndCreatesNothing(string damage)
    {
        using (var s = Store()) { Add(s); Add(s, "item:2"); }
        KnowledgeExporter.Export(Snap, Out);
        var file = Path.Combine(Entries, "00000002.md");
        var t = File.ReadAllText(file);
        File.WriteAllText(file, damage switch
        {
            "swap-keys" => t.Replace("kind: \"note\"\nrequested_kind: \"note\"", "requested_kind: \"note\"\nkind: \"note\""),
            "extra-key" => t.Replace("\nauthor:", "\nsneaky: \"x\"\nauthor:"),
            "bad-value" => t.Replace("trust: 5", "trust: five"),
            "wrong-id" => t.Replace("id: 2\n", "id: 3\n"),
            "no-fence" => t.Replace("---\ntext", "text"),
            "bad-status" => t.Replace("status: \"live\"", "status: \"deleted\""),
            _ => t[..30],
        });
        var target = Path.Combine(_dir, "restored.sqlite");

        Assert.Throws<KnowledgeException>(() => KnowledgeRestorer.Restore(Out, target));

        Assert.False(File.Exists(target));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp*"));
    }

    [Fact]
    public void Restore_NothingToRestore_Refused()
    {
        Directory.CreateDirectory(Entries);
        var target = Path.Combine(_dir, "restored.sqlite");

        Assert.Throws<KnowledgeException>(() => KnowledgeRestorer.Restore(Out, target));
        Assert.Throws<KnowledgeException>(() => KnowledgeRestorer.Restore(Path.Combine(_dir, "nope"), target));

        Assert.False(File.Exists(target));
    }

    // ---- restore applies the server's write rules ----

    private const string PositionJson = "{\"map\":\"Azeroth\",\"leftPct\":10,\"topPct\":20}";

    private string RestoreTampered(string file, Func<string, string> tamper)
    {
        using (var s = Store()) { Add(s, "item:1"); Add(s, "item:2", position: PositionJson); }
        KnowledgeExporter.Export(Snap, Out);
        var path = Path.Combine(Entries, file);
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(tamper(File.ReadAllText(path))));
        var target = Path.Combine(_dir, "restored.sqlite");

        var ex = Assert.Throws<KnowledgeException>(() => KnowledgeRestorer.Restore(Out, target));

        Assert.False(File.Exists(target), "nothing may be written");
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp*"));
        return ex.Message;
    }

    // Each case replaces one front matter line of entry 2 (written by export, so the text is exactly as below).
    [Theory]
    [InlineData("trust: 5", "trust: 1", "trust")]
    [InlineData("kind: \"note\"", "kind: \"bogus\"", "kind")]
    [InlineData("requested_kind: \"note\"", "requested_kind: \"bogus\"", "kind")]
    [InlineData("author: \"Ann\"", "author: \"A\\u0001nn\"", "author")]
    [InlineData("author: \"Ann\"", "author: \" Ann\"", "author")]
    [InlineData("subject_id: \"item:2\"", "subject_id: \"path:bad|char\"", "subject")]
    [InlineData("subject_id: \"item:2\"", "subject_id: \"item:2\\u200b\"", "subject")]
    [InlineData("realm_json: \"[\\\"coa\\\"]\"", "realm_json: \"[\\\"mars\\\"]\"", "realm")]
    [InlineData("realm_json: \"[\\\"coa\\\"]\"", "realm_json: \"not json\"", "realm")]
    [InlineData("evidence_json: \"{\\\"description\\\":\\\"seen in game\\\"}\"", "evidence_json: \"{\\\"url\\\":\\\"ftp://x.org/a\\\"}\"", "url")]
    [InlineData("evidence_json: \"{\\\"description\\\":\\\"seen in game\\\"}\"", "evidence_json: \"[1,2\"", "evidence")]
    [InlineData("field: null\nvalue: null", "field: \"level\"\nvalue: null", "field")]
    [InlineData("position_json: \"{\\\"map\\\":\\\"Azeroth\\\",\\\"leftPct\\\":10,\\\"topPct\\\":20}\"", "position_json: \"{\\\"map\\\":\\\"Azeroth\\\",\\\"leftPct\\\":150,\\\"topPct\\\":20}\"", "position")]
    [InlineData("position_json: \"{\\\"map\\\":\\\"Azeroth\\\",\\\"leftPct\\\":10,\\\"topPct\\\":20}\"", "position_json: \"{\\\"leftPct\\\":10,\\\"topPct\\\":20}\"", "position")]
    [InlineData("position_json: \"{\\\"map\\\":\\\"Azeroth\\\",\\\"leftPct\\\":10,\\\"topPct\\\":20}\"", "position_json: \"null\"", "position")]
    [InlineData("created_at: \"20", "created_at: \"yesterday 20", "created_at")]
    public void Restore_BreaksServerRule_RefusedNamingFileAndRule(string from, string to, string rule)
    {
        var message = RestoreTampered("00000002.md", t =>
        {
            Assert.Contains(from, t);
            return t.Replace(from, to);
        });

        Assert.Contains("00000002.md", message);
        Assert.Contains(rule, message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Restore_ObservedWithoutEvidence_Refused()
    {
        var message = RestoreTampered("00000002.md", t => t.Replace("trust: 5", "trust: 4").Replace("{\\\"description\\\":\\\"seen in game\\\"}", "{}"));

        Assert.Contains("00000002.md", message);
        Assert.Contains("observed needs evidence", message);
    }

    [Fact]
    public void Restore_OversizeOrInvisibleContent_Refused()
    {
        var big = RestoreTampered("00000002.md", t => t + new string('a', 40_000));
        Assert.Contains("00000002.md", big);
        Assert.Contains("content", big);
        Assert.Contains("invisible", RestoreTampered("00000002.md", t => t + "ab​cd"));
        Assert.Contains("control", RestoreTampered("00000002.md", t => t + "a\u0001b"));
    }

    // Ids a live write accepts because a layer holds them, not because they are new <type>:<slug> ids.
    [Theory]
    [InlineData("guide:arcane-mage-guide")]
    [InlineData("marker:12345")]
    [InlineData("project:coa-notes")]
    [InlineData("dbc:Spell")]
    [InlineData("class:coa:barbarian")]
    [InlineData("talents:coa:barbarian")]
    [InlineData("spec:coa:barbarian:1")]
    [InlineData("rules:coa:general")]
    [InlineData("item:wotlk:19019")]
    [InlineData("Weird:Mixed.Case_id-1")]
    public void Restore_GeneratedSubjectIds_RoundTripThroughExport(string subject)
    {
        using (var s = Store()) Add(s, subject, content: "about " + subject);
        KnowledgeExporter.Export(Snap, Out);
        var target = Path.Combine(_dir, "restored.sqlite");

        Assert.Equal(1, KnowledgeRestorer.Restore(Out, target));

        Assert.Equal(Rows(Snap, AllColumns)[0], Rows(target, AllColumns)[0]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("noseparator")]
    [InlineData(":rest")]
    [InlineData("type:")]
    [InlineData("a:b​c")]
    [InlineData("a:bc")]
    public void Restore_SubjectWithoutTypeRestOrWithBadText_Refused(string subject)
    {
        var message = RestoreTampered("00000002.md", t => t.Replace("subject_id: \"item:2\"", "subject_id: " + System.Text.Json.JsonSerializer.Serialize(subject)));

        Assert.Contains("subject", message);
    }

    [Fact]
    public void Restore_PathSubject_ShapeOnly_IsAccepted()
    {
        using (var s = Store()) { Add(s, "path:Interface/Icons/Foo.blp"); }
        KnowledgeExporter.Export(Snap, Out);
        var target = Path.Combine(_dir, "restored.sqlite");

        Assert.Equal(1, KnowledgeRestorer.Restore(Out, target));
        Assert.Equal("path:Interface/Icons/Foo.blp", Rows(target, "SELECT subject_id FROM entries")[0][0]);
    }

    // ---- restore never skips a file silently ----

    [Fact]
    public void Restore_NineDigitIds_AreRestored()
    {
        using (var s = Store()) { Add(s); Add(s, "item:2"); }
        using (var c = new SqliteConnection($"Data Source={Snap};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE entries SET id = 123456789 WHERE id = 2";
            cmd.ExecuteNonQuery();
        }
        KnowledgeExporter.Export(Snap, Out);
        Assert.True(File.Exists(Path.Combine(Entries, "123456789.md")));
        var target = Path.Combine(_dir, "restored.sqlite");

        Assert.Equal(2, KnowledgeRestorer.Restore(Out, target));

        Assert.Equal(new object?[] { 1L, 123456789L }, Rows(target, "SELECT id FROM entries ORDER BY id").Select(r => r[0]).ToArray());
    }

    [Theory]
    [InlineData("notes.txt", false)]
    [InlineData("1.md", false)]
    [InlineData("0000000001.md", false)]   // padded wider than the canonical form
    [InlineData("00000002.MD", false)]
    [InlineData("00000003.md.bak", false)]
    [InlineData("sub", true)]
    public void Restore_OtherFileInEntries_FailsNamingIt(string name, bool folder)
    {
        using (var s = Store()) { Add(s); }
        KnowledgeExporter.Export(Snap, Out);
        if (folder) Directory.CreateDirectory(Path.Combine(Entries, name));
        else File.Copy(Path.Combine(Entries, "00000001.md"), Path.Combine(Entries, name));
        var target = Path.Combine(_dir, "restored.sqlite");

        var ex = Assert.Throws<KnowledgeException>(() => KnowledgeRestorer.Restore(Out, target));

        Assert.Contains(name, ex.Message);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task Cli_MissingSshOrScp_IsACleanMessage()
    {
        Directory.CreateDirectory(TempRoot);
        var e = new StringWriter();

        var code = await KnowledgeCli.RunAsync("pull-writes", new[] { "--host", "BledtIT", "--out", Out }, new StringWriter(), e, _ => new MissingTool(), TempRoot);

        Assert.Equal(1, code);
        Assert.Contains("ssh", e.ToString());
        Assert.DoesNotContain("   at ", e.ToString());
    }

    private sealed class MissingTool : IRemote
    {
        public Task UploadAsync(string localPath, string remotePath, CancellationToken ct) => throw new InvalidOperationException();
        public Task DownloadAsync(string remotePath, string localPath, CancellationToken ct) => throw new InvalidOperationException();
        public Task<RemoteResult> RunAsync(string command, string? stdin, CancellationToken ct) =>
            throw new System.ComponentModel.Win32Exception(2, "An error occurred trying to start process 'ssh'");
    }

    // ---- pull ----

    /// <summary>A server in a folder: the host's /data/coamcp, a container seeing it as /data, and the real admin command running inside it.</summary>
    private sealed class HostFake : IRemote
    {
        public const string Container = "server-ukfeegudmo0pfyq1nxe7tadz-081412345678";
        private readonly string _root;
        public List<string> Commands { get; } = new();
        public List<string> Downloads { get; } = new();
        public string DockerPs { get; set; } = Container + "\n";
        public bool FailDockerExec { get; set; }
        public bool FailDownload { get; set; }
        public bool CorruptDownload { get; set; }
        public bool FailRemove { get; set; }
        public bool NeverReachable { get; set; }
        public int Execs { get; private set; }

        public HostFake(string root) => _root = root;

        public string Host(string remote) => Path.Combine(_root, remote.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        public string StorePath => Host("/data/coamcp/writable/store.sqlite");
        public IEnumerable<string> Leftovers() =>
            Directory.Exists(Host("/data/coamcp/writable")) ? Directory.GetFiles(Host("/data/coamcp/writable")).Select(f => Path.GetFileName(f)).Where(n => n != "store.sqlite" && !n.StartsWith("store.sqlite-")) : Array.Empty<string>();

        public Task UploadAsync(string localPath, string remotePath, CancellationToken ct) => throw new InvalidOperationException("pull-writes must never upload");

        public Task DownloadAsync(string remotePath, string localPath, CancellationToken ct)
        {
            if (NeverReachable) throw new InvalidOperationException("must not be contacted");
            Downloads.Add(remotePath);
            if (FailDownload) throw new IOException("scp: connection lost");
            File.Copy(Host(remotePath), localPath, overwrite: false);
            if (CorruptDownload) { var b = File.ReadAllBytes(localPath); b[b.Length / 2] ^= 0xFF; File.WriteAllBytes(localPath, b); }
            return Task.CompletedTask;
        }

        public Task<RemoteResult> RunAsync(string command, string? stdin, CancellationToken ct)
        {
            if (NeverReachable) throw new InvalidOperationException("must not be contacted");
            Commands.Add(command);
            var w = FakeRemote.Words(command);
            switch (w)
            {
                case ["if", "[", "-f", var p, "]", ";", "then", "echo", "present", ";", "fi"]:
                    return Ok(File.Exists(Host(p)) ? "present\n" : "");
                case ["if", "[", "-f", var p, "]", ";", "then", "sha256sum", var p2, ";", "fi"] when p == p2:
                    return Ok(File.Exists(Host(p)) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Host(p)))) + "  " + p + "\n" : "");
                case ["rm", "-f", var p]:
                    if (FailRemove) return Task.FromResult(new RemoteResult(1, "", "rm: permission denied"));
                    File.Delete(Host(p));
                    return Ok("");
                case ["docker", "ps", "--filter", var f, "--format", var fmt]:
                    Assert.Equal("name=ukfeegudmo0pfyq1nxe7tadz", f);
                    Assert.Equal("{{.Names}}", fmt);
                    return Ok(DockerPs);
                case ["docker", "exec", var c, "dotnet", "/app/CoaMcp.Server.dll", "admin", "snapshot", var target]:
                    Execs++;
                    if (c != Container) return Fail($"Error: No such container: {c}");
                    if (FailDockerExec) return Fail("boom\u001b[31m");
                    Assert.StartsWith("/data/writable/pull-", target);
                    var o = new StringWriter();
                    var e = new StringWriter();
                    var code = AdminCli.Run(new[] { "admin", "snapshot", Host("/data/coamcp" + target[5..]) }, Host("/data/coamcp"), o, e);
                    return Task.FromResult(new RemoteResult(code, o.ToString(), e.ToString()));
            }
            return Fail("unrecognised command: " + command);
        }

        private static Task<RemoteResult> Ok(string s) => Task.FromResult(new RemoteResult(0, s, ""));
        private static Task<RemoteResult> Fail(string s) => Task.FromResult(new RemoteResult(1, "", s));
    }

    private HostFake Host(bool withStore = true, int entries = 2)
    {
        var h = new HostFake(Path.Combine(_dir, "host"));
        Directory.CreateDirectory(Path.GetDirectoryName(h.StorePath)!);
        if (withStore)
            using (var s = Store(h.StorePath)) for (var i = 0; i < entries; i++) Add(s, "item:" + i, content: "entry " + i);
        return h;
    }

    private string TempRoot => Path.Combine(_dir, "tmp");

    private async Task<(int Code, string Out, string Err)> Pull(IRemote host, params string[] extra)
    {
        Directory.CreateDirectory(TempRoot);
        var o = new StringWriter();
        var e = new StringWriter();
        var args = new[] { "--host", "BledtIT", "--out", Out }.Concat(extra).ToArray();
        var code = await KnowledgeCli.RunAsync("pull-writes", args, o, e, _ => host, TempRoot);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public async Task Pull_ExportsProductionEntries_AndSummarises()
    {
        var host = Host(entries: 3);

        var r = await Pull(host);

        Assert.Equal(0, r.Code);
        Assert.Equal("00000001.md,00000002.md,00000003.md", Names(Entries));
        Assert.Contains("Added 3", r.Out);
        Assert.Contains("git add knowledge", r.Out);   // suggested, never run
        Assert.Contains("Ann: 3", r.Out);
        Assert.Equal(1, host.Execs);
    }

    [Fact]
    public async Task Pull_RemovesTempSnapshot()
    {
        var host = Host();

        var r = await Pull(host);

        Assert.Equal(0, r.Code);
        Assert.Empty(host.Leftovers());   // the remote copy is gone
        Assert.Empty(Directory.GetFileSystemEntries(TempRoot));   // and so is the local one
        Assert.Contains(host.Commands, c => c.StartsWith("rm -f '/data/coamcp/writable/pull-"));
        Assert.Single(host.Downloads);
        Assert.Matches(@"^/data/coamcp/writable/pull-[0-9a-f]{32}\.sqlite$", host.Downloads[0]);
    }

    [Theory]
    [InlineData("exec")]
    [InlineData("download")]
    [InlineData("corrupt")]
    [InlineData("ps-none")]
    [InlineData("ps-many")]
    public async Task Pull_RemoteFailure_LeavesKnowledgeUntouched(string failure)
    {
        var host = Host();
        Directory.CreateDirectory(Entries);
        File.WriteAllText(Path.Combine(Entries, "00000001.md"), "keep");
        var before = Tree(Out);
        switch (failure)
        {
            case "exec": host.FailDockerExec = true; break;
            case "download": host.FailDownload = true; break;
            case "corrupt": host.CorruptDownload = true; break;
            case "ps-none": host.DockerPs = ""; break;
            case "ps-many": host.DockerPs = HostFake.Container + "\nserver-ukfeegudmo0pfyq1nxe7tadz-999\n"; break;
        }

        var r = await Pull(host);

        Assert.Equal(1, r.Code);
        Assert.NotEmpty(r.Err);
        Assert.DoesNotContain('\u001b', r.Err);   // text from the server can't drive the terminal
        AssertSameTree(before, Tree(Out));
        Assert.Empty(host.Leftovers());
        Assert.Empty(Directory.GetFileSystemEntries(TempRoot));
    }

    [Fact]
    public async Task Pull_ExportRefused_StillCleansUp()
    {
        var host = Host();
        using (var c = new SqliteConnection($"Data Source={host.StorePath};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM entries";
            cmd.ExecuteNonQuery();
        }
        Directory.CreateDirectory(Entries);
        File.WriteAllText(Path.Combine(Entries, "00000001.md"), "keep");

        var r = await Pull(host);

        Assert.Equal(1, r.Code);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(Entries, "00000001.md")));
        Assert.Empty(host.Leftovers());
        Assert.Empty(Directory.GetFileSystemEntries(TempRoot));
    }

    [Fact]
    public async Task Pull_RemoteCopyCannotBeRemoved_IsReported()
    {
        var host = Host();
        host.FailRemove = true;

        var r = await Pull(host);

        Assert.Equal(0, r.Code);   // the export itself worked
        Assert.Contains("could not remove", r.Err);
        Assert.Contains("pull-", r.Err);
    }

    [Fact]
    public async Task Pull_MissingStore_SaysNothingToMerge_Exit0()
    {
        var host = Host(withStore: false);

        var r = await Pull(host);

        Assert.Equal(0, r.Code);
        Assert.Contains("nothing to merge", r.Out);
        Assert.False(Directory.Exists(Out));
        Assert.Equal(0, host.Execs);
        Assert.Empty(host.Downloads);
    }

    [Fact]
    public async Task Pull_ContainerOverride_IsUsed_AndMustBeAPlainName()
    {
        var host = Host();
        host.DockerPs = "";   // lookup would find nothing

        var ok = await Pull(host, "--container", HostFake.Container);
        Assert.Equal(0, ok.Code);
        Assert.DoesNotContain(host.Commands, c => c.StartsWith("docker ps"));

        foreach (var bad in new[] { "x; rm -rf /", "$(id)", "a b", "-v", "", "x'y" })
        {
            var h2 = Host();
            h2.NeverReachable = true;
            var r = await Pull(h2, "--container", bad);
            Assert.True(r.Code == 2 || r.Code == 1, bad);
            Assert.Empty(h2.Commands);
        }
    }

    [Fact]
    public async Task Pull_ContactsOnlyTheGivenHost_AndNeverUploads()
    {
        var hosts = new List<string>();
        var host = Host();
        Directory.CreateDirectory(TempRoot);

        var code = await KnowledgeCli.RunAsync("pull-writes", new[] { "--host", "BledtIT", "--out", Out }, new StringWriter(), new StringWriter(), h => { hosts.Add(h); return host; }, TempRoot);

        Assert.Equal(0, code);
        Assert.Equal(new[] { "BledtIT" }, hosts);
        Assert.All(host.Commands, c => Assert.DoesNotContain("Bearer", c));
    }

    [Fact]
    public async Task Pull_TestSubjectsOnlyInProduction_AreNotExported()
    {
        var host = Host(entries: 1);
        using (var s = Store(host.StorePath)) Add(s, "test:probe");

        var r = await Pull(host);

        Assert.Equal(0, r.Code);
        Assert.Equal("00000001.md", Names(Entries));
        Assert.Contains("Skipped 1 entry", r.Out);
    }

    // ---- command line ----

    [Theory]
    [InlineData("pull-writes")]
    [InlineData("restore-writes")]
    public async Task Cli_MissingOrUnknownArguments_IsUsageError(string command)
    {
        var e = new StringWriter();
        Assert.Equal(2, await KnowledgeCli.RunAsync(command, Array.Empty<string>(), new StringWriter(), e, _ => new HostFake(_dir) { NeverReachable = true }, TempRoot));
        Assert.Contains("Usage", e.ToString());
        Assert.Equal(2, await KnowledgeCli.RunAsync(command, new[] { "--bogus", "x" }, new StringWriter(), new StringWriter(), _ => new HostFake(_dir) { NeverReachable = true }, TempRoot));
    }

    [Fact]
    public async Task Cli_RestoreWrites_RunsAndRefuses()
    {
        using (var s = Store()) Add(s, content: "hello");
        KnowledgeExporter.Export(Snap, Out);
        var target = Path.Combine(_dir, "restored.sqlite");
        var o = new StringWriter();
        var e = new StringWriter();

        var code = await KnowledgeCli.RunAsync("restore-writes", new[] { "--from", Out, "--store", target }, o, e, null, TempRoot);

        Assert.Equal(0, code);
        Assert.Contains("Restored 1 entry", o.ToString());
        Assert.Equal(1, (long)Rows(target, "SELECT COUNT(*) FROM entries")[0][0]!);

        var again = await KnowledgeCli.RunAsync("restore-writes", new[] { "--from", Out, "--store", target }, new StringWriter(), e, null, TempRoot);
        Assert.Equal(1, again);
        Assert.Contains("already has", e.ToString());
    }
}
