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

    /// <summary>The only full-text table; its shadow tables (<c>entities_fts_data</c> and so on) are allowed with it.</summary>
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

    public static bool IsAllowedTable(string layer, string table) =>
        Layers.TryGetValue(layer, out var tables) && (tables.Contains(table, StringComparer.Ordinal) || IsFtsShadow(table) || IsSqliteInternal(table));

    /// <summary>FTS5 keeps its index in these tables; they hold BLOBs by design and are not content.</summary>
    public static bool IsFtsShadow(string table) => ShadowName().IsMatch(table);

    public static bool IsSqliteInternal(string table) => table.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase);

    public static bool IsFtsTable(string table) => table == FtsTable;

    [GeneratedRegex("^entities_fts_(data|idx|content|docsize|config)$")]
    private static partial Regex ShadowName();
}
