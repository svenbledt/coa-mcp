using System.Text.RegularExpressions;

namespace CoaMcp.Pipeline.Publish;

/// <summary>
/// The one place that says which tables each layer may contain. A layer with any other table is not published.
/// Later tasks add their layers (and tables) here.
/// </summary>
public static partial class LayerAllowList
{
    /// <summary>Tables every layer has: what <c>LayerWriter</c> creates.</summary>
    private static readonly string[] Base = { "entities", "links", "entities_fts", "layer_meta" };

    private const string PathsFts = "paths_fts";

    /// <summary>Which client a layer describes. Only layers built from the client have it.</summary>
    private const string Client = "layer_client";

    private static readonly IReadOnlyDictionary<string, string[]> Layers = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["bisbeard"] = Base,
        ["spells"] = Base.Concat(new[]
        {
            "enums", "flag_kinds", "spell_scaling_inputs", "golden_scaling", "ScalingStatValues", "ScalingStatDistribution", Client,
        }).ToArray(),
        ["paths"] = Base.Concat(new[] { "paths", "dirs", "path_stats", PathsFts, Client }).ToArray(),
        ["notes"] = Base,
        ["cache"] = Base,
        ["dbc"] = Base.Concat(new[] { "dbc_tables", "dbc_fields", "dbc_meta", Client }).ToArray(),
    };

    [GeneratedRegex("^t_[A-Za-z0-9_]{1,80}$")]
    private static partial Regex DbcDataTable();

    /// <summary>In the dbc layer, a data table is allowed only if it fits the pattern and the rest of its name is listed in that layer's dbc_tables, spelled exactly the same.</summary>
    public static bool IsDbcDataTable(string table, IReadOnlySet<string> listed) =>
        DbcDataTable().IsMatch(table) && listed.Contains(table["t_".Length..]);

    public static bool IsKnownLayer(string name) => Layers.ContainsKey(name);

    /// <summary>
    /// The full-text tables and their exact declarations, as SQLite stores them. <c>entities_fts</c> is in every layer; <c>paths_fts</c> only in the paths layer.
    /// The shadow tables of each are allowed with it, but only with FTS5's own definitions.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> FtsSql = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["entities_fts"] = "CREATE VIRTUAL TABLE entities_fts USING fts5(title, body_md, content='entities', content_rowid='rowid')",
        [PathsFts] = "CREATE VIRTUAL TABLE paths_fts USING fts5(path, content='paths', content_rowid='id', tokenize='trigram')",
    };

    private static readonly string[] ShadowSuffixes = { "_data", "_idx", "_docsize", "_config" };

    /// <summary>
    /// The exact definitions FTS5 gives the shadow tables of an external-content table. Such a table has no
    /// <c>_content</c> shadow table. Any other table with one of these names is not FTS5's and is refused.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ShadowSql = FtsSql.Keys.SelectMany(fts => ShadowSuffixes.Select(sfx => (Name: fts + sfx, Fts: fts, Sfx: sfx)))
        .ToDictionary(x => x.Name, x => x.Sfx switch
        {
            "_data" => $"CREATE TABLE '{x.Name}'(id INTEGER PRIMARY KEY, block BLOB)",
            "_idx" => $"CREATE TABLE '{x.Name}'(segid, term, pgno, PRIMARY KEY(segid, term)) WITHOUT ROWID",
            "_docsize" => $"CREATE TABLE '{x.Name}'(id INTEGER PRIMARY KEY, sz BLOB)",
            _ => $"CREATE TABLE '{x.Name}'(k PRIMARY KEY, v) WITHOUT ROWID",
        }, StringComparer.Ordinal);

    /// <summary>The full-text table a shadow table belongs to, or null if the name is not a shadow name.</summary>
    public static string? FtsOwner(string table) =>
        ShadowSql.ContainsKey(table) ? FtsSql.Keys.First(f => table.StartsWith(f + "_", StringComparison.Ordinal)) : null;

    /// <summary>A table is allowed by name; the guard then checks the FTS tables against their exact definitions.</summary>
    public static bool IsAllowedTable(string layer, string table) =>
        Layers.TryGetValue(layer, out var tables)
        && (tables.Contains(table, StringComparer.Ordinal)
            || (FtsOwner(table) is { } owner && tables.Contains(owner, StringComparer.Ordinal))   // a shadow table is allowed only where its FTS table is
            || IsSqliteInternal(table));

    /// <summary>FTS5 keeps its index in these tables; they hold BLOBs by design and are not scanned as content.</summary>
    public static bool IsFtsShadow(string table) => ShadowSql.ContainsKey(table);

    public static bool IsSqliteInternal(string table) => table.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase);

    public static bool IsFtsTable(string table) => FtsSql.ContainsKey(table);
}
