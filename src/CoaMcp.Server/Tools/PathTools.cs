using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CoaMcp.Core;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace CoaMcp.Server.Tools;

/// <summary>Search and browse the paths of the CoA client's files. Paths and sizes only: file content never reaches the server.</summary>
[McpServerToolType]
public sealed class PathTools
{
    public const string Gap = "winning copies come from reconstructed client code and assume HD archives load.";

    private const int DefaultLimit = 50;
    private const int MaxBrowseOffset = 100_000;   // folder and prefix lists read straight from an index
    private const int MaxSearchOffset = 10_000;     // a substring search has to walk every earlier match
    private const int MaxTerms = 8;
    private const int MinTermChars = 3;
    private const int MaxExtChars = 16;
    private const int MaxHolders = 12;
    private const string Source = "CoA client file list";
    private static readonly RealmSet ClientRealm = new(new[] { "coa-client" }, false);

    private readonly LayerSet _layers;

    public PathTools(LayerSet layers) => _layers = layers;

    [McpServerTool(Name = "client_paths", ReadOnly = true), Description(
        "Find or browse file paths in the CoA client (paths and sizes only, no file content). query: words that must all appear in the path, any case, 3+ characters each. " +
        "prefix: a folder such as DBFilesClient/ lists its folders and files; otherwise paths starting with it. Each path shows the archive the client reads it from.")]
    public string ClientPaths(
        [Description("Words to find anywhere in the path, e.g. spell fireball. Each at least 3 characters.")] string? query = null,
        [Description("Folder or path start, e.g. DBFilesClient/ or Interface/Icons. / alone lists the top folders.")] string? prefix = null,
        [Description("File type without the dot, e.g. dbc, blp, m2.")] string? ext = null,
        [Description("Rows to return, 1 to 100.")] long limit = DefaultLimit,
        [Description("Rows to skip, for paging.")] long offset = 0)
    {
        var words = Terms(query);
        var start = Prefix(prefix);
        var type = Ext(ext);
        if (words is null && start is null && type is null)
            throw new McpException("Give at least one of query, prefix or ext. Example: query=\"spellvisual\", prefix=\"DBFilesClient/\", ext=\"dbc\".");

        var (lim, off, note) = Limits.Page(limit, offset, words is null ? MaxBrowseOffset : MaxSearchOffset);
        using var lease = _layers.Lease("paths") ?? throw new McpException("The paths layer is not loaded on this server. See the status tool.");
        var layer = lease.Layer;
        var sb = new StringBuilder(Header(layer)).Append('\n');
        if (note is not null) sb.Append(note).Append('\n');

        // One whole path, in either spelling and any case, gets the full answer.
        var whole = words is null ? start : (type is null && start is null ? Join(query).TrimStart('/') : null);
        if (whole is not null && whole.Length > 0 && !whole.EndsWith('/') && layer.Query("SELECT * FROM paths WHERE path = $0", whole) is [var exact])
            return sb.Append(Detail(exact, layer.Meta.Client)).ToString().TrimEnd();

        if (words is null && type is null && start is not null && Folder(layer, start) is { } folder)
            Browse(layer, folder, lim, off, sb);
        else
            List(layer, words is null ? null : FtsQuery.FromUser(string.Join(' ', words)), words, start, type, lim, off, sb);
        return sb.ToString().TrimEnd();
    }

    // ---- input ----

    private static string Join(string? s) => s is null ? "" : string.Join(' ', Norm(s).Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();

    /// <summary>'\' and '/' mean the same; control characters are refused.</summary>
    private static string Norm(string s)
    {
        if (s.Any(char.IsControl)) throw new McpException("Input contains control characters.");
        return s.Replace('\\', '/');
    }

    private static string[]? Terms(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        if (query.Length > Limits.MaxQueryChars) throw new McpException($"query is too long (max {Limits.MaxQueryChars} characters).");
        var terms = Norm(query).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length > MaxTerms) throw new McpException($"query has too many words (max {MaxTerms}).");
        if (terms.Any(t => t.Length < MinTermChars))
            throw new McpException($"Every word in query needs at least {MinTermChars} characters, because search matches any part of a path.");
        return terms;
    }

    /// <summary>Null when not given; otherwise a path start with single '/' and no leading '/'. "/" alone is the root, as "".</summary>
    private static string? Prefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return null;
        if (prefix.Length > Limits.MaxQueryChars) throw new McpException($"prefix is too long (max {Limits.MaxQueryChars} characters).");
        var p = Norm(prefix.Trim());
        while (p.Contains("//")) p = p.Replace("//", "/");
        return p.TrimStart('/');
    }

    private static string? Ext(string? ext)
    {
        if (string.IsNullOrWhiteSpace(ext)) return null;
        var e = ext.Trim().TrimStart('.').ToLowerInvariant();
        if (e.Length is 0 or > MaxExtChars || !e.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_'))
            throw new McpException($"ext must be a file type of up to {MaxExtChars} letters or digits, such as dbc or blp.");
        return e;
    }

    // ---- output ----

    private static string Header(Layer layer) =>
        $"Client paths (realm {Render.RealmText(ClientRealm)}, trust {Render.Trust(Trust.Client)}, source {ClientRelease.Source(Source, layer.Meta.Client)}, source date {layer.Meta.SourceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}; layer paths v{layer.Meta.Version}). " +
        "Paths only, no file content.";

    private static string Bytes(long n) => n.ToString("N0", CultureInfo.InvariantCulture) + " bytes";

    private static string Path(IReadOnlyDictionary<string, object?> r) => Render.OneLine((string)r["path"]!, 200);

    private static bool IsDeleted(IReadOnlyDictionary<string, object?> r) => (long)r["deleted"]! != 0;

    /// <summary>One list line: path, then what the client reads.</summary>
    private static string Line(IReadOnlyDictionary<string, object?> r)
    {
        var win = r["winner_archive"] as string;
        if (IsDeleted(r)) return $"{Path(r)} | removed by {Render.OneLine(win!, 60)}";
        if (win is null) return $"{Path(r)} | not in the default client";
        var size = r["size"] is long s ? Bytes(s) : "size unknown";
        return $"{Path(r)} | {size} | {Render.OneLine(win, 60)}" + (r["winner_origin"] as string == "project" ? " (project-built archive)" : "");
    }

    private sealed record Holder(string Archive, string Origin, long? Size, bool Deleted, string? Realm, bool NotOpened);

    private static List<Holder> Holders(string? json)
    {
        var result = new List<Holder>();
        if (string.IsNullOrEmpty(json)) return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var h in doc.RootElement.EnumerateArray())
                result.Add(new Holder(h.GetProperty("a").GetString() ?? "?", h.TryGetProperty("o", out var o) ? o.GetString() ?? "unknown" : "unknown",
                    h.TryGetProperty("s", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : null,
                    h.TryGetProperty("d", out _), h.TryGetProperty("r", out var r) ? r.GetString() : null, h.TryGetProperty("n", out _)));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { /* a damaged cell shows no holders; the row itself is still answered */ }
        return result;
    }

    /// <summary>One line per archive whose copy differs from the release file, among those winning these rows.</summary>
    private static void AppendMismatches(StringBuilder sb, Layer layer, IEnumerable<IReadOnlyDictionary<string, object?>> rows)
    {
        if (layer.Meta.Client is not { } client) return;
        foreach (var archive in rows.Where(r => !IsDeleted(r)).Select(r => r["winner_archive"] as string).Distinct())
            if (client.MismatchLine(archive) is { } line) sb.Append(line).Append('\n');
    }

    private static string Detail(IReadOnlyDictionary<string, object?> r, ClientRelease? client)
    {
        var sb = new StringBuilder(Path(r)).Append('\n');
        var holders = Holders(r["holders_json"] as string);
        var win = r["winner_archive"] as string;

        if (IsDeleted(r))
        {
            var older = holders.Where(h => !h.Deleted && h.Realm is null && !h.NotOpened).Select(h => Render.OneLine(h.Archive, 60)).ToList();
            sb.Append($"removed by {Render.OneLine(win!, 60)} (a delete marker): the client does not see this file");
            sb.Append(older.Count > 0 ? $"; older copies exist in {string.Join(", ", older.Take(MaxHolders))}." : ".").Append('\n');
        }
        else if (win is null)
        {
            sb.Append("not in the default client: no archive the client opens for every realm holds it.\n");
        }
        else
        {
            var size = r["size"] is long s ? Bytes(s) : "size unknown";
            var project = r["winner_origin"] as string == "project";
            sb.Append($"winner: {Render.OneLine(win, 60)}, {size}, ");
            sb.Append(project ? "project-built archive (project files are not evidence about Ascension)" : $"origin {r["winner_origin"]}").Append('\n');
            if (client?.MismatchLine(win) is { } mismatch) sb.Append(mismatch).Append('\n');
            if (project)
                sb.Append(r["base_winner"] is string b
                    ? $"base copy: {Render.OneLine(b, 60)}, {(r["base_size"] is long bs ? Bytes(bs) : "size unknown")}, origin unknown\n"
                    : NoBaseCopy(holders, win));
        }

        if (!IsDeleted(r) && holders.Count > 0)
        {
            sb.Append("holders, highest priority first: ").Append(string.Join("; ", holders.Take(MaxHolders).Select(h =>
                $"{Render.OneLine(h.Archive, 60)} {(h.Deleted ? "delete marker" : h.Size is { } z ? Bytes(z) : "size unknown")}" +
                (h.Realm is not null ? $" ({Render.OneLine(h.Realm, 30)} realm only, not in the default client)" : h.NotOpened ? " (the client never opens this archive)" : ""))));
            if (holders.Count > MaxHolders) sb.Append($"; and {holders.Count - MaxHolders} more");
            sb.Append('\n');
        }
        sb.Append("Winners follow the client's archive order; ").Append(Gap);
        return sb.ToString();
    }

    /// <summary>Why a project-built winner has no base copy. The copy that would be it may be a delete marker: then the file was removed below the project's copy.</summary>
    private static string NoBaseCopy(List<Holder> holders, string winner)
    {
        var next = holders.Where(h => h.Realm is null && !h.NotOpened && h.Archive != winner).FirstOrDefault(h => h.Origin != "project");
        return next is { Deleted: true }
            ? $"base copy: none. The next copy that isn't project-built is a delete marker in {Render.OneLine(next.Archive, 60)}, so the file was removed below this one.\n"
            : "base copy: none in an archive that is not project-built.\n";
    }

    // ---- folders ----

    private sealed record FolderRef(long Id, string Display);

    /// <summary>The folder a prefix names exactly (with or without a trailing '/'), or null when it is not a folder.</summary>
    private static FolderRef? Folder(Layer layer, string prefix)
    {
        long id = 0;
        var shown = new StringBuilder();
        foreach (var part in prefix.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var rows = layer.Query("SELECT id, name FROM dirs WHERE parent_id = $0 AND name_lc = $1", id, part.ToLowerInvariant());
            if (rows.Count == 0) return null;
            id = (long)rows[0]["id"]!;
            shown.Append((string)rows[0]["name"]!).Append('/');
        }
        return new FolderRef(id, shown.ToString());
    }

    /// <summary>Immediate children only: folders first (with counts), then files, paged as one list.</summary>
    private static void Browse(Layer layer, FolderRef folder, int limit, int offset, StringBuilder sb)
    {
        var folders = (long)layer.Query("SELECT COUNT(*) AS n FROM dirs WHERE parent_id = $0", folder.Id)[0]["n"]!;
        var files = (long)layer.Query("SELECT COUNT(*) AS n FROM paths WHERE dir_id = $0", folder.Id)[0]["n"]!;
        sb.Append($"{(folder.Display.Length == 0 ? "Top level" : folder.Display)}: {folders} {(folders == 1 ? "folder" : "folders")}, {files} {(files == 1 ? "file" : "files")} directly inside; showing from {offset + 1}.\n");

        var shown = 0;
        if (offset < folders)
        {
            foreach (var d in layer.Query("SELECT name, files, removed FROM dirs WHERE parent_id = $0 ORDER BY name_lc LIMIT $1 OFFSET $2", folder.Id, (long)limit, (long)offset))
            {
                var n = (long)d["files"]!;
                var gone = (long)d["removed"]!;
                sb.Append($"folder {Render.OneLine((string)d["name"]!, 100)}/ ({n} {(n == 1 ? "file" : "files")}{(gone > 0 ? $", {gone} removed" : "")})\n");
                shown++;
            }
        }
        var fileOffset = Math.Max(0, offset - folders);
        var fileRows = layer.Query("SELECT * FROM paths WHERE dir_id = $0 ORDER BY id LIMIT $1 OFFSET $2", folder.Id, (long)(limit - shown), fileOffset);
        foreach (var r in fileRows)
        {
            sb.Append(Line(r)).Append('\n');
            shown++;
        }
        AppendMismatches(sb, layer, fileRows);
        if (shown == 0) sb.Append("Nothing at this offset.\n");
        if (offset + shown < folders + files) sb.Append($"More below: use offset={offset + shown}.\n");
    }

    // ---- flat lists ----

    private static void List(Layer layer, FtsQuery? fts, string[]? words, string? prefix, string? ext, int limit, int offset, StringBuilder sb)
    {
        var args = new List<object?>();
        string P(object? v) { args.Add(v); return "$" + (args.Count - 1); }

        var where = new List<string>();
        var sql = new StringBuilder();
        if (fts is not null)
        {
            sql.Append("SELECT p.* FROM paths_fts f JOIN paths p ON p.id = f.rowid");
            where.Add($"paths_fts MATCH {P(fts.Match)}");
        }
        else sql.Append("SELECT p.* FROM paths p");
        if (ext is not null) where.Add($"p.ext = {P(ext)}");
        if (!string.IsNullOrEmpty(prefix))
        {
            // The column compares without regard to case. Everything that starts with the prefix sorts between it and the prefix plus the highest character.
            where.Add($"p.path >= {P(prefix)} AND p.path < {P(prefix + "\U0010FFFF")}");
        }
        sql.Append(" WHERE ").Append(string.Join(" AND ", where));
        sql.Append(" ORDER BY p.id LIMIT ").Append(P((long)limit + 1)).Append(" OFFSET ").Append(P((long)offset));

        var rows = fts is not null
            ? layer.Searching(() => layer.Query(sql.ToString(), args.ToArray()))
            : layer.Query(sql.ToString(), args.ToArray());

        var what = new List<string>();
        if (words is not null) what.Add($"containing {string.Join(" and ", words.Select(w => "\"" + Render.OneLine(w, 60) + "\""))}");
        if (prefix is not null) what.Add($"starting with \"{Render.OneLine(prefix, 100)}\"");
        if (ext is not null) what.Add($"of type {ext}");
        sb.Append($"{Math.Min(rows.Count, limit)} paths {string.Join(", ", what)} (offset {offset}, limit {limit}), in path order.\n");
        foreach (var r in rows.Take(limit)) sb.Append(Line(r)).Append('\n');
        AppendMismatches(sb, layer, rows.Take(limit));
        if (rows.Count > limit) sb.Append($"More results may exist: use offset={offset + limit}.\n");
    }

    /// <summary>The status line for the paths layer: how many paths, and what the restore left out.</summary>
    public static string Summary(Layer layer)
    {
        var stats = layer.Query("SELECT key, value FROM path_stats").ToDictionary(r => (string)r["key"]!, r => (string)r["value"]!);
        string S(string key) => stats.TryGetValue(key, out var v) ? v : "?";
        return $"{S("paths")} client paths in {S("folders")} folders ({S("deleted")} removed by a later patch, {S("unavailable")} not in the default client, {S("project_winner")} won by a project-built archive); " +
               $"restore manifest: {S("unnamed")} unnamed, {S("failed")} failed, {S("internal_skipped")} archive internals left out";
    }
}
