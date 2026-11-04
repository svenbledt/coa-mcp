using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CoaMcp.Acceptance;

/// <summary>
/// The owner's commands on the live server, over ssh with docker exec, and the store's file modes on the host. These write one test entry, undone afterward, and one backup file,
/// which stays on the server as a nightly backup would. undo-author is never run here: it isn't needed, and only the owner should run it on real authors.
/// </summary>
[Collection("16a Admin")]
public class AdminTests
{
    private const string Area = "Admin";
    private const string HostRoot = "/data/coamcp";
    private const string ContainerRoot = "/data";

    private static string Admin(string command) =>
        $"C=$(docker ps --format '{{{{.Names}}}}' | grep {Settings.CoolifyAppId} | head -1)\ndocker exec \"$C\" dotnet /app/CoaMcp.Server.dll admin {command}\n";

    private static async Task<(int Live, int Undone, int Total)?> AuthorCounts(Checks c, string step)
    {
        var r = await Shell.Ssh(Admin("list-authors"));
        c.Equal(0, r.Exit, $"{step}: list-authors exits 0");
        c.Book.Transcript($"admin list-authors ({step})", (r.Out + r.Err).Trim());
        var m = Regex.Match(r.Out, @"^""acceptance-test""\s+live (\d+), undone (\d+), total (\d+), newest \S+", RegexOptions.Multiline);
        if (!c.That(m.Success, $"{step}: list-authors shows acceptance-test with its counts", r.Out + r.Err)) return null;
        return (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
    }

    private static (int Live, int Undone, int Superseded)? StatusCounts(string status)
    {
        var m = Regex.Match(status, @"Written entries: (\d+) live \(.*?\), (\d+) undone, (\d+) superseded");
        return m.Success ? (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)) : null;
    }

    /// <summary>Owner may read and write, group may read, and no one else anything: 0640 or tighter.</summary>
    internal static bool AtMost640(string mode) => mode.Length is 3 or 4 && (Convert.ToInt32(mode, 8) & ~Convert.ToInt32("640", 8)) == 0;

    [SkippableFact]
    public Task ListAuthors_Backup_AndFileModes() => Suite.RunWithToken(Area, "list-authors, a backup that opens with the same counts, and the store's file modes", async (c, token) =>
    {
        await using var w = await WriteTests.Writer.Open(token);
        var folder = Directory.CreateTempSubdirectory("coa-acceptance-backup-").FullName;
        try
        {
            var word = "adminword" + w.Run;
            var write = await w.Write(w.Subject("admin"), "An entry for the admin test. " + word);
            var id = WriteTests.Writer.Id(write);
            if (!c.That(id is not null, "the test entry is stored", write.All)) return;

            var before = await AuthorCounts(c, "after a write");
            var status = StatusCounts((await w.Client.Call("status")).Text);
            if (!c.That(status is not null, "status gives counts of written entries")) return;
            c.That(before is { Live: >= 1, Total: >= 1 }, "acceptance-test has at least the entry just written", before?.ToString());

            // A backup, on the host.
            var backup = await Shell.Ssh(Admin($"backup {ContainerRoot}/writable/backups"));
            c.Equal(0, backup.Exit, "admin backup exits 0");
            c.Book.Transcript("admin backup", (backup.Out + backup.Err).Trim());
            var m = Regex.Match(backup.Out, @"Wrote (\S+\.sqlite) \((\d+) bytes\)");
            if (!c.That(m.Success, "it says what it wrote", backup.Out + backup.Err)) return;
            var hostFile = HostRoot + m.Groups[1].Value[ContainerRoot.Length..];
            c.Book.ProductionChange($"Wrote a backup of the store to {hostFile} with admin backup. It stays there, and the owner's nightly backup prunes such files after 14 days.");

            // Modes, on the host.
            var modes = await Shell.Ssh($"cd {HostRoot}/writable && stat -c '%n %a %u' . store.sqlite '{hostFile}'\n");
            c.Equal(0, modes.Exit, "file modes can be read");
            c.Book.Transcript("File modes on the host", modes.Out.Trim());
            var lines = modes.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split(' ')).ToList();
            if (c.Equal(3, lines.Count, "stat gave three lines"))
            {
                c.Equal("750", lines[0][1], "writable/ has mode 0750");
                c.Equal("10001", lines[0][2], "and belongs to the server's user");
                c.That(AtMost640(lines[1][1]), "store.sqlite has mode 0640 or tighter", $"mode {lines[1][1]}");
                c.Equal("10001", lines[1][2], "and belongs to the server's user");
                c.That(AtMost640(lines[2][1]), "the backup has mode 0640 or tighter", $"mode {lines[2][1]}");
            }

            // The backup itself, opened here. A temp folder outside the repository, removed below.
            var copy = await Shell.Run("scp", new[] { "-q", "-o", "BatchMode=yes", $"{Settings.SshHost}:{hostFile}", "backup.sqlite" }, workingDir: folder, timeout: TimeSpan.FromMinutes(2));
            if (!c.Equal(0, copy.Exit, "the backup can be fetched")) { c.Fail("scp output", copy.Err); return; }
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(folder, "backup.sqlite"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            {
                db.Open();
                long Scalar(string sql)
                {
                    using var cmd = db.CreateCommand();
                    cmd.CommandText = sql;
                    return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                }
                using (var check = db.CreateCommand())
                {
                    check.CommandText = "PRAGMA integrity_check";
                    c.Equal("ok", check.ExecuteScalar()?.ToString(), "the backup passes SQLite's integrity check");
                }
                var (live, undone, superseded) = status!.Value;
                c.Equal(live + undone + superseded, Scalar("SELECT COUNT(*) FROM entries"), "entries in the backup against status");
                c.Equal(undone, Scalar("SELECT COUNT(*) FROM entries WHERE undone_at IS NOT NULL"), "undone entries in the backup against status");
                if (before is { } a)
                {
                    c.Equal(a.Total, Scalar("SELECT COUNT(*) FROM entries WHERE author = 'acceptance-test'"), "acceptance-test's total in the backup against list-authors");
                    c.Equal(a.Undone, Scalar("SELECT COUNT(*) FROM entries WHERE author = 'acceptance-test' AND undone_at IS NOT NULL"), "acceptance-test's undone count in the backup against list-authors");
                }
                c.Equal(1L, Scalar($"SELECT COUNT(*) FROM entries WHERE id = {id} AND instr(content_md, '{word}') > 0"), "the backup holds the entry just written");
            }
            SqliteConnection.ClearAllPools();

            await w.Undo(id!.Value);
            var after = await AuthorCounts(c, "after its undo");
            if (before is { } b && after is { } d)
            {
                c.Equal(b.Live - 1, d.Live, "undo shows up in list-authors: one fewer live");
                c.Equal(b.Undone + 1, d.Undone, "and one more undone");
                c.Equal(b.Total, d.Total, "and nothing deleted");
            }
        }
        finally
        {
            await w.UndoAll(c);
            Directory.Delete(folder, recursive: true);
        }
    });
}
