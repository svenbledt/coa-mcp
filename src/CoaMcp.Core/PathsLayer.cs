using System.Globalization;

namespace CoaMcp.Core;

/// <summary>
/// One client file path as the paths layer holds it. Only the path and facts about it: no file content.
/// <paramref name="Size"/> is the winning copy's size in bytes (null when removed or when no loaded archive has it).
/// <paramref name="HoldersJson"/> lists every archive that has the path, highest priority first.
/// </summary>
public sealed record PathRecord(string Path, long? Size, string? WinnerArchive, string? WinnerOrigin, bool Deleted,
    string? BaseWinner, long? BaseSize, string HoldersJson);

/// <summary>
/// Writes the paths layer: <c>paths</c> (one row per path, with a case-insensitive unique key), <c>dirs</c> (the folder tree with
/// counts), <c>path_stats</c> (counts and restore facts) and <c>paths_fts</c> (a trigram index for substring search).
/// Add paths in alphabetical order, so row order is browse order.
/// </summary>
public sealed class PathsLayerBuilder
{
    private const int MaxExtChars = 16;

    private sealed class Dir(long id, string name)
    {
        public long Id { get; } = id;
        public string Name { get; } = name;
        public long Files, Removed;
    }

    private readonly LayerWriter _w;
    private readonly Dictionary<(long Parent, string NameLc), (long ParentId, Dir Dir)> _dirs = new();
    private long _nextDir = 1;
    private long _paths, _deleted, _unavailable, _project;

    public PathsLayerBuilder(LayerWriter writer)
    {
        _w = writer;
        _w.Execute("""
            CREATE TABLE paths(
              id INTEGER PRIMARY KEY, path TEXT NOT NULL COLLATE NOCASE, dir_id INTEGER NOT NULL, ext TEXT, size INTEGER,
              winner_archive TEXT, winner_origin TEXT, deleted INTEGER NOT NULL, base_winner TEXT, base_size INTEGER, holders_json TEXT NOT NULL);
            CREATE UNIQUE INDEX paths_path ON paths(path);
            CREATE INDEX paths_dir ON paths(dir_id);
            CREATE INDEX paths_ext ON paths(ext);
            CREATE TABLE dirs(id INTEGER PRIMARY KEY, parent_id INTEGER NOT NULL, name TEXT NOT NULL, name_lc TEXT NOT NULL, files INTEGER NOT NULL, removed INTEGER NOT NULL);
            CREATE UNIQUE INDEX dirs_child ON dirs(parent_id, name_lc);
            CREATE TABLE path_stats(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE VIRTUAL TABLE paths_fts USING fts5(path, content='paths', content_rowid='id', tokenize='trigram');
            """);
    }

    public void Add(PathRecord r)
    {
        var segments = r.Path.Split('/');
        if (r.Path.Length == 0 || segments.Any(s => s.Length == 0) || r.Path.Contains('\\'))
            throw new ArgumentException($"'{r.Path}' is not a clean path: it needs '/' between non-empty parts.", nameof(r));

        long parent = 0;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var key = (parent, segments[i].ToLowerInvariant());
            if (!_dirs.TryGetValue(key, out var node))
            {
                node = (parent, new Dir(_nextDir++, segments[i]));
                _dirs[key] = node;
            }
            node.Dir.Files++;
            if (r.Deleted) node.Dir.Removed++;
            parent = node.Dir.Id;
        }

        _w.Execute("INSERT INTO paths(path, dir_id, ext, size, winner_archive, winner_origin, deleted, base_winner, base_size, holders_json) VALUES ($0,$1,$2,$3,$4,$5,$6,$7,$8,$9)",
            r.Path, parent, Extension(segments[^1]), r.Size, r.WinnerArchive, r.WinnerOrigin, r.Deleted ? 1L : 0L, r.BaseWinner, r.BaseSize, r.HoldersJson);
        _paths++;
        if (r.Deleted) _deleted++;
        else if (r.WinnerArchive is null) _unavailable++;
        else if (r.WinnerOrigin == "project") _project++;
    }

    /// <summary>Lowercase text after the last dot of the file name, or null when there is none worth keeping.</summary>
    public static string? Extension(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        if (dot < 0 || dot == fileName.Length - 1) return null;
        var ext = fileName[(dot + 1)..].ToLowerInvariant();
        return ext.Length <= MaxExtChars ? ext : null;
    }

    /// <summary>Writes folders, stats and the search index, then finishes the layer. <paramref name="stats"/> adds to the counts this builder keeps itself.</summary>
    public void Finish(IReadOnlyDictionary<string, string> stats, DateOnly sourceDate)
    {
        foreach (var ((_, nameLc), (parentId, dir)) in _dirs)
            _w.Execute("INSERT INTO dirs VALUES ($0,$1,$2,$3,$4,$5)", dir.Id, parentId, dir.Name, nameLc, dir.Files, dir.Removed);

        var all = new Dictionary<string, string>(stats)
        {
            ["paths"] = _paths.ToString(CultureInfo.InvariantCulture),
            ["deleted"] = _deleted.ToString(CultureInfo.InvariantCulture),
            ["unavailable"] = _unavailable.ToString(CultureInfo.InvariantCulture),
            ["project_winner"] = _project.ToString(CultureInfo.InvariantCulture),
            ["folders"] = _dirs.Count.ToString(CultureInfo.InvariantCulture),
        };
        foreach (var (k, v) in all) _w.Execute("INSERT INTO path_stats VALUES ($0,$1)", k, v);

        _w.Execute("INSERT INTO paths_fts(paths_fts) VALUES('rebuild')");
        _w.Finish();
        // LayerWriter dates a layer by its entities; this one has none, so it carries the client's date instead.
        _w.Execute("UPDATE layer_meta SET source_date = $0", sourceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }
}
