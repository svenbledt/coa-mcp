using CoaMcp.Core;
using CoaMcp.Server.Admin;
using Microsoft.Data.Sqlite;

namespace CoaMcp.Server.Tests;

public sealed class AdminCliTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coamcp-admin-" + Guid.NewGuid().ToString("N"));
    private string StorePath => Path.Combine(_root, "writable", "store.sqlite");

    public AdminCliTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private WriteStore Store() => new(StorePath);

    private static void Add(WriteStore s, string author, string subject = "item:1", string content = "text") =>
        s.Add(new NewEntry(subject, "note", new[] { "ascension-live" }, Trust.Memory, content, "{}", null, null, null, author));

    private (int Code, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = AdminCli.Run(args, _root, o, e);
        return (code, o.ToString(), e.ToString());
    }

    private long Scalar(string sql, string? path = null)
    {
        using var c = new SqliteConnection($"Data Source={path ?? StorePath};Mode=ReadOnly;Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    [Fact]
    public void UndoAuthor_HidesAllItsEntries_KeepsHistory()
    {
        using (var s = Store()) { Add(s, "Mallory"); Add(s, "Mallory"); Add(s, "Ann"); }

        var r = Run("admin", "undo-author", "Mallory", "--by", "owner");

        Assert.Equal(0, r.Code);
        Assert.Contains("2", r.Out);
        Assert.Equal(3, Scalar("SELECT COUNT(*) FROM entries"));   // nothing deleted
        Assert.Equal(2, Scalar("SELECT COUNT(*) FROM entries WHERE author = 'Mallory' AND undone_at IS NOT NULL AND undone_by = 'owner'"));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM entries WHERE author = 'Ann' AND undone_at IS NOT NULL"));

        var again = Run("admin", "undo-author", "Mallory");   // idempotent
        Assert.Equal(0, again.Code);
        Assert.Contains("0", again.Out);
        Assert.Equal(2, Scalar("SELECT COUNT(*) FROM entries WHERE undone_by = 'owner'"));   // first record stays
    }

    [Fact]
    public void UndoAuthor_DefaultsUndoneByToAdmin()
    {
        using (var s = Store()) Add(s, "Mallory");
        Assert.Equal(0, Run("admin", "undo-author", "Mallory").Code);
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM entries WHERE undone_by = 'admin'"));
    }

    [Fact]
    public void UndoAuthor_MatchesExactly_AndBindsValues()
    {
        using (var s = Store()) { Add(s, "Ann"); Add(s, "ann"); Add(s, "x' OR '1'='1"); }
        Assert.Equal(0, Run("admin", "undo-author", "Ann").Code);
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM entries WHERE undone_at IS NOT NULL"));
        Assert.Equal(1, Run("admin", "undo-author", "' OR '1'='1").Code);   // injection text is just an unknown name
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM entries WHERE undone_at IS NOT NULL"));
    }

    [Fact]
    public void UndoAuthor_Unknown_CleanError()
    {
        using (var s = Store()) Add(s, "Ann");
        var r = Run("admin", "undo-author", "Nobody");
        Assert.Equal(1, r.Code);
        Assert.Contains("Nobody", r.Err);
        Assert.DoesNotContain("Exception", r.Err);
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM entries WHERE undone_at IS NOT NULL"));
    }

    [Fact]
    public void ListAuthors_ShowsCounts()
    {
        using (var s = Store()) { Add(s, "Mallory"); Add(s, "Mallory"); Add(s, "Ann"); }
        Run("admin", "undo-author", "Mallory");
        using (var s = Store()) Add(s, "Mallory");

        var r = Run("admin", "list-authors");

        Assert.Equal(0, r.Code);
        var lines = r.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        var mallory = Assert.Single(lines, l => l.Contains("\"Mallory\""));
        Assert.Contains("live 1", mallory);
        Assert.Contains("undone 2", mallory);
        Assert.Contains("total 3", mallory);
        Assert.Matches(@"\d{4}-\d{2}-\d{2}T", mallory);
        Assert.Contains("live 1, undone 0, total 1", Assert.Single(lines, l => l.Contains("\"Ann\"")));
    }

    [Fact]
    public void Authors_HostileName_PrintedSafely()
    {
        var hostile = "Eve\nlive 999, undone 0, total 999 \"Ann\"\u001b[2J";
        using (var s = Store()) { Add(s, hostile); Add(s, "Ann"); }

        var list = Run("admin", "list-authors");
        Assert.Equal(2, list.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);   // one line per author
        Assert.DoesNotContain('\u001b', list.Out);
        Assert.All(list.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries), l => Assert.StartsWith("\"", l));   // each line starts with a quoted name

        var undo = Run("admin", "undo-author", hostile);
        Assert.Equal(0, undo.Code);
        Assert.DoesNotContain('\u001b', undo.Out);
        Assert.Single(undo.Out.TrimEnd().Split('\n'));

        var missing = Run("admin", "undo-author", hostile + "z");
        Assert.Equal(1, missing.Code);
        Assert.DoesNotContain('\u001b', missing.Err);
        Assert.Single(missing.Err.TrimEnd().Split('\n'));
    }

    [Fact]
    public void UndoEntry_UndoesOne_Idempotent_AndMissingIsCleanError()
    {
        using (var s = Store()) { Add(s, "Ann"); Add(s, "Ann"); }
        Assert.Equal(0, Run("admin", "undo-entry", "1", "--by", "owner").Code);
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM entries WHERE undone_at IS NOT NULL AND id = 1 AND undone_by = 'owner'"));
        Assert.Equal(0, Run("admin", "undo-entry", "1").Code);
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM entries WHERE undone_by = 'owner'"));
        Assert.Equal(1, Run("admin", "undo-entry", "99").Code);
        Assert.Equal(2, Run("admin", "undo-entry", "abc").Code);
    }

    [Theory]
    [InlineData("undo-author", "Ann")]
    [InlineData("list-authors")]
    [InlineData("undo-entry", "1")]
    [InlineData("backup", "BACKUPDIR")]
    public void StoreMissing_CleanError_CreatesNothing(params string[] command)
    {
        var args = new[] { "admin" }.Concat(command.Select(a => a == "BACKUPDIR" ? Path.Combine(_root, "backups") : a)).ToArray();
        var r = Run(args);
        Assert.Equal(1, r.Code);
        Assert.Contains("store", r.Err, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(StorePath));
        Assert.False(Directory.Exists(Path.Combine(_root, "writable")));
        Assert.False(Directory.Exists(Path.Combine(_root, "backups")));
    }

    [Theory]
    [InlineData]
    [InlineData("nonsense")]
    [InlineData("undo-author")]
    [InlineData("undo-author", "Ann", "--by")]
    [InlineData("undo-author", "Ann", "--bogus", "x")]
    [InlineData("backup")]
    public void BadUsage_PrintsUsage_Exit2(params string[] command)
    {
        using (var s = Store()) Add(s, "Ann");
        var r = Run(new[] { "admin" }.Concat(command).ToArray());
        Assert.Equal(2, r.Code);
        Assert.Contains("Usage", r.Err);
    }

    [Fact]
    public void Backup_IsValidDatabase_WithSameCounts_WhileStoreInUse()
    {
        var backups = Path.Combine(_root, "backups");
        using var s = Store();
        for (var i = 0; i < 5; i++) Add(s, "Ann", "item:" + i, "alpha beta " + i);
        Run("admin", "undo-entry", "2");

        // Hold a read transaction open on the store while the backup runs, as a busy server would.
        using var reader = new SqliteConnection($"Data Source={StorePath};Mode=ReadWrite;Pooling=False");
        reader.Open();
        using var tx = reader.BeginTransaction();
        using (var q = reader.CreateCommand()) { q.Transaction = tx; q.CommandText = "SELECT COUNT(*) FROM entries"; q.ExecuteScalar(); }

        var r = Run("admin", "backup", backups);

        Assert.Equal(0, r.Code);
        var file = Assert.Single(Directory.GetFiles(backups));
        Assert.Matches(@"^store-\d{8}\.sqlite$", Path.GetFileName(file));
        Assert.Contains(file, r.Out);
        Assert.Equal("ok", Text("PRAGMA integrity_check", file));
        Assert.Equal(Scalar("SELECT COUNT(*) FROM entries"), Scalar("SELECT COUNT(*) FROM entries", file));
        Assert.Equal(Scalar("SELECT COUNT(*) FROM entries WHERE undone_at IS NOT NULL"), Scalar("SELECT COUNT(*) FROM entries WHERE undone_at IS NOT NULL", file));
        Assert.Equal(5, Scalar("SELECT COUNT(*) FROM entries_fts WHERE entries_fts MATCH 'alpha'", file));
        Assert.DoesNotContain(Directory.GetFiles(backups), f => f.EndsWith(".tmp"));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(backups) & (UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file) & (UnixFileMode.UserRead | UnixFileMode.UserWrite));
        }
        if (!OperatingSystem.IsWindows())
            Assert.Equal(0, (int)(File.GetUnixFileMode(file) & (UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)));

        // A second run on the same day replaces the file, and still leaves one.
        Assert.Equal(0, Run("admin", "backup", backups).Code);
        Assert.Single(Directory.GetFiles(backups));
    }

    private static string Text(string sql, string path)
    {
        using var c = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (string)cmd.ExecuteScalar()!;
    }

    [Fact]
    public void Backup_Prunes_OnlyOldFilesWithExactName()
    {
        var backups = Path.Combine(_root, "backups");
        Directory.CreateDirectory(backups);
        using (var s = Store()) Add(s, "Ann");
        var today = DateTime.UtcNow.Date;
        string Name(int daysAgo) => $"store-{today.AddDays(-daysAgo):yyyyMMdd}.sqlite";
        var old = new[] { Name(14), Name(40), "store-20200101.sqlite" };
        var keep = new[] { Name(1), Name(13), "store-20200101.sqlite.bak", "store-2020010.sqlite", "notes.txt", "store-20200101.sqlite.tmp", "xstore-20200101.sqlite", "STORE-20200102.sqlite" };
        foreach (var f in old.Concat(keep)) File.WriteAllText(Path.Combine(backups, f), "x");
        var sub = Path.Combine(backups, "store-20200101.sqlite.d");   // a folder with a matching prefix is left alone
        Directory.CreateDirectory(sub);

        Assert.Equal(0, Run("admin", "backup", backups).Code);

        foreach (var f in old) Assert.False(File.Exists(Path.Combine(backups, f)), f + " should be pruned");
        foreach (var f in keep) Assert.True(File.Exists(Path.Combine(backups, f)), f + " should stay");
        Assert.True(Directory.Exists(sub));
        Assert.True(File.Exists(Path.Combine(backups, $"store-{today:yyyyMMdd}.sqlite")));
    }

    [Fact]
    public void Backup_Failure_PrunesNothing()
    {
        var backups = Path.Combine(_root, "backups");
        Directory.CreateDirectory(backups);
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, new string('x', 8192));   // damaged store
        File.WriteAllText(Path.Combine(backups, "store-20200101.sqlite"), "x");

        var r = Run("admin", "backup", backups);

        Assert.Equal(1, r.Code);
        Assert.True(File.Exists(Path.Combine(backups, "store-20200101.sqlite")));
        Assert.DoesNotContain(Directory.GetFiles(backups), f => f.EndsWith(".tmp"));
    }
}
