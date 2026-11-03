namespace CoaMcp.Pipeline.Publish;

/// <summary>
/// The one place that says which tables each layer may contain. A layer with any other table is not published.
/// Later tasks add their layers (and tables) here.
/// </summary>
public static class LayerAllowList
{
    /// <summary>Tables every layer has: what <c>LayerWriter</c> creates.</summary>
    private static readonly string[] Base = { "entities", "links", "entities_fts", "layer_meta" };

    /// <summary>The only full-text table; its shadow tables are allowed with it, but only with FTS5's own definitions.</summary>
    private const string FtsTable = "entities_fts";

    private static readonly IReadOnlyDictionary<string, string[]> Layers = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["bisbeard"] = Base,
        ["spells"] = Base.Concat(new[]
        {
            "enums", "flag_kinds", "spell_scaling_inputs", "golden_scaling", "ScalingStatValues", "ScalingStatDistribution",
        }).ToArray(),
    };

    public static bool IsKnownLayer(string name) => Layers.ContainsKey(name);

    /// <summary>A table is allowed by name; the guard then checks the FTS tables against their exact definitions.</summary>
    public static bool IsAllowedTable(string layer, string table) =>
        Layers.TryGetValue(layer, out var tables) && (tables.Contains(table, StringComparer.Ordinal) || IsFtsShadow(table) || IsSqliteInternal(table));

    /// <summary>The exact declaration of the one full-text table, as SQLite stores it.</summary>
    public const string FtsSql = "CREATE VIRTUAL TABLE entities_fts USING fts5(title, body_md, content='entities', content_rowid='rowid')";

    /// <summary>
    /// The exact definitions FTS5 gives the shadow tables of an external-content table (<c>content='entities'</c>). Such a table has no
    /// <c>_content</c> shadow table. Any other table with one of these names is not FTS5's and is refused.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ShadowSql = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["entities_fts_data"] = "CREATE TABLE 'entities_fts_data'(id INTEGER PRIMARY KEY, block BLOB)",
        ["entities_fts_idx"] = "CREATE TABLE 'entities_fts_idx'(segid, term, pgno, PRIMARY KEY(segid, term)) WITHOUT ROWID",
        ["entities_fts_docsize"] = "CREATE TABLE 'entities_fts_docsize'(id INTEGER PRIMARY KEY, sz BLOB)",
        ["entities_fts_config"] = "CREATE TABLE 'entities_fts_config'(k PRIMARY KEY, v) WITHOUT ROWID",
    };

    /// <summary>FTS5 keeps its index in these tables; they hold BLOBs by design and are not scanned as content.</summary>
    public static bool IsFtsShadow(string table) => ShadowSql.ContainsKey(table);

    public static bool IsSqliteInternal(string table) => table.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase);

    public static bool IsFtsTable(string table) => table == FtsTable;
}
