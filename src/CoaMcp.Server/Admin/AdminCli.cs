using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CoaMcp.Server.Admin;

/// <summary>
/// Owner commands, run inside the container with `docker exec`. They act on the writable store directly, have no network endpoint and need no token.
/// Exit codes: 0 done, 1 refused or failed (message on stderr), 2 bad usage. Nothing here creates the store or deletes an entry.
/// </summary>
public static class AdminCli
{
    public const int KeepDays = 14;
    private const int MaxByLength = 100;
    private const string Usage = """
        Usage: coamcp admin <command>
          list-authors                       each author with live, undone and total counts
          undo-author <name> [--by <who>]    mark all of an author's live entries undone
          undo-entry <id> [--by <who>]       mark one entry undone
          backup <dir>                       write store-YYYYMMDD.sqlite there and keep the last 14 days
        """;

    private static readonly System.Text.RegularExpressions.Regex BackupName = new(@"^store-(\d{8})\.sqlite\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private sealed class UsageException(string message) : Exception(message);
    private sealed class Refused(string message) : Exception(message);

    /// <param name="args">The arguments after the program name, starting with "admin".</param>
    /// <param name="root">The data root; the store is root/writable/store.sqlite.</param>
    public static int Run(string[] args, string root, TextWriter output, TextWriter error)
    {
        var path = Path.Combine(root, "writable", "store.sqlite");
        try
        {
            var rest = args.Skip(1).ToArray();
            if (rest.Length == 0) throw new UsageException("Missing command.");
            return rest[0] switch
            {
                "list-authors" => Exact(rest, 1, () => ListAuthors(path, output)),
                "undo-author" => WithBy(rest, (name, by) => UndoAuthor(path, name, by, output)),
                "undo-entry" => WithBy(rest, (id, by) => UndoEntry(path, id, by, output)),
                "backup" => Exact(rest, 2, () => Backup(path, rest[1], output)),
                _ => throw new UsageException($"Unknown command {Quote(rest[0])}."),
            };
        }
        catch (UsageException ex)
        {
            error.WriteLine(ex.Message);
            error.WriteLine(Usage);
            return 2;
        }
        catch (Refused ex)
        {
            error.WriteLine(ex.Message);
            return 1;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"Failed: {OneLine(ex.Message)}");
            return 1;
        }
    }

    private static int Exact(string[] rest, int length, Func<int> run)
    {
        if (rest.Length != length) throw new UsageException(length == 1 ? "This command takes no arguments." : "Wrong number of arguments.");
        return run();
    }

    /// <summary>One positional argument, then an optional --by value.</summary>
    private static int WithBy(string[] rest, Func<string, string, int> run)
    {
        string? positional = null, by = null;
        for (var i = 1; i < rest.Length; i++)
        {
            if (rest[i] == "--by" && by is null && i + 1 < rest.Length) by = rest[++i];
            else if (positional is null && !rest[i].StartsWith("--", StringComparison.Ordinal)) positional = rest[i];
            else throw new UsageException($"Unexpected argument {Quote(rest[i])}.");
        }
        if (positional is null) throw new UsageException("Missing argument.");
        by ??= "admin";
        if (by.Length is 0 or > MaxByLength || by.Any(char.IsControl)) throw new UsageException("--by must be 1 to 100 characters with no control characters.");
        return run(positional, by);
    }

    // ---- output safety: names are written as JSON strings, so a newline or escape code in one can't fake a line or drive the terminal ----

    private static string Quote(string text) => JsonSerializer.Serialize(text);
    private static string OneLine(string text) => Quote(text)[1..^1];

    // ---- store ----

    private static SqliteConnection OpenStore(string path)
    {
        if (!File.Exists(path)) throw new Refused($"There is no writable store at {path}. Nothing was created.");
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        try
        {
            c.Open();
            Exec(c, null, "PRAGMA busy_timeout = 5000");
            if (Scalar(c, null, "SELECT 1 FROM sqlite_master WHERE name = 'entries'") is null)
                throw new Refused($"The file at {path} is not a writable store (it has no entries table).");
            return c;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26)   // corrupt, or not a database
        {
            c.Dispose();
            throw new Refused($"The file at {path} is not a readable store: {OneLine(ex.Message)}");
        }
        catch
        {
            c.Dispose();
            throw;
        }
    }

    private static object? Scalar(SqliteConnection c, SqliteTransaction? tx, string sql, params object[] args)
    {
        using var cmd = Command(c, tx, sql, args);
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    private static int Exec(SqliteConnection c, SqliteTransaction? tx, string sql, params object[] args)
    {
        using var cmd = Command(c, tx, sql, args);
        return cmd.ExecuteNonQuery();
    }

    private static SqliteCommand Command(SqliteConnection c, SqliteTransaction? tx, string sql, object[] args)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        for (var i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue("$" + i, args[i]);
        return cmd;
    }

    private static string Now() => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    // ---- commands ----

    private static int ListAuthors(string path, TextWriter output)
    {
        using var c = OpenStore(path);
        using var cmd = Command(c, null, """
            SELECT author, SUM(undone_at IS NULL), SUM(undone_at IS NOT NULL), COUNT(*), MAX(created_at)
            FROM entries GROUP BY author ORDER BY MAX(id) DESC
            """, Array.Empty<object>());
        using var r = cmd.ExecuteReader();
        while (r.Read())
            output.WriteLine($"{Quote(r.GetString(0))}  live {r.GetInt64(1)}, undone {r.GetInt64(2)}, total {r.GetInt64(3)}, newest {r.GetString(4)}");
        return 0;
    }

    private static int UndoAuthor(string path, string name, string by, TextWriter output)
    {
        using var c = OpenStore(path);
        using var tx = c.BeginTransaction();
        var total = (long)Scalar(c, tx, "SELECT COUNT(*) FROM entries WHERE author = $0", name)!;
        if (total == 0) throw new Refused($"No entries by author {Quote(name)}. Run list-authors to see the names.");
        var changed = Exec(c, tx, "UPDATE entries SET undone_at = $1, undone_by = $2 WHERE author = $0 AND undone_at IS NULL", name, Now(), by);
        tx.Commit();
        output.WriteLine($"Undid {changed} live {(changed == 1 ? "entry" : "entries")} by {Quote(name)} ({total - changed} were already undone). Nothing was deleted.");
        return 0;
    }

    private static int UndoEntry(string path, string idText, string by, TextWriter output)
    {
        if (!long.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) throw new UsageException($"{Quote(idText)} is not an entry id.");
        using var c = OpenStore(path);
        using var tx = c.BeginTransaction();
        if (Scalar(c, tx, "SELECT 1 FROM entries WHERE id = $0", id) is null) throw new Refused($"No entry with id {id}.");
        var changed = Exec(c, tx, "UPDATE entries SET undone_at = $1, undone_by = $2 WHERE id = $0 AND undone_at IS NULL", id, Now(), by);
        tx.Commit();
        output.WriteLine(changed == 1 ? $"Undid entry {id}. Nothing was deleted." : $"Entry {id} was already undone. Nothing changed.");
        return 0;
    }

    /// <summary>
    /// A consistent copy made with VACUUM INTO while the server keeps working. It is written under a temporary name and renamed into place,
    /// so a half-written file never has the real name. Old files are pruned only after a good backup, and only if their name is exactly store-YYYYMMDD.sqlite.
    /// </summary>
    private static int Backup(string path, string dir, TextWriter output)
    {
        using (var probe = OpenStore(path)) { }   // refuse before creating anything
        var today = DateTime.UtcNow.Date;
        var name = $"store-{today:yyyyMMdd}.sqlite";
        var target = Path.Combine(dir, name);
        var temp = Path.Combine(dir, "." + name + ".tmp");

        CreateDirectory(dir);
        File.Delete(temp);
        CreatePrivateFile(temp);   // VACUUM INTO accepts an existing empty file, so the copy never has wider access than the file made here
        try
        {
            using (var c = OpenStore(path)) Exec(c, null, "VACUUM INTO $0", temp);
            using (var check = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            {
                check.Open();
                if (Scalar(check, null, "PRAGMA integrity_check") as string != "ok") throw new Refused("The new backup failed its integrity check, so it was discarded.");
            }
            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }

        var pruned = Prune(dir, today);
        output.WriteLine($"Wrote {target} ({new FileInfo(target).Length} bytes). Pruned {pruned} backup{(pruned == 1 ? "" : "s")} older than {KeepDays} days.");
        return 0;
    }

    private static void CreateDirectory(string dir)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
        else Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
    }

    private static void CreatePrivateFile(string file)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        new FileStream(file, options).Dispose();
    }

    private static int Prune(string dir, DateTime today)
    {
        var pruned = 0;
        foreach (var file in Directory.GetFiles(dir))
        {
            var m = BackupName.Match(Path.GetFileName(file));
            if (!m.Success || !DateTime.TryParseExact(m.Groups[1].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
            if ((today - day).TotalDays < KeepDays) continue;
            File.Delete(file);
            pruned++;
        }
        return pruned;
    }
}
