using System.Text.Json;
using CoaMcp.Core;

namespace CoaMcp.Server;

public sealed record LayerHit(
    string LayerName, string Id, string Type, string Title, string Snippet, RealmSet Realm, Trust Trust,
    string Source, DateOnly SourceDate, int Count, string? Zones);

internal sealed record Candidate(
    Layer Layer, string LayerName, string Id, string Type, string Title, RealmSet Realm, Trust Trust,
    string Source, DateOnly SourceDate, double Rank, bool Exact, int Count, string? Zones);

/// <summary>
/// Per-layer candidate search. Order is: exact title match, then entity type priority, then bm25 within a type.
/// The work is bounded: layers without a wanted type are skipped, ids are chosen before any snippet is built,
/// and identical markers are grouped into one hit.
/// </summary>
internal static class LayerSearch
{
    public static readonly string[] TypePriority = { "spell", "item", "class", "talents", "spec", "rules", "guide", "marker" };

    private static readonly HashSet<string> Collapsed = new() { "marker" };

    private const string Columns = "e.id, e.type, e.title, e.realm_json, e.realm_proven, e.trust, e.source, e.source_date";

    private const string RealmClause = """
         AND ($1 IS NULL OR EXISTS (SELECT 1 FROM json_each(e.realm_json) j
                                    WHERE j.value = $1 OR ($1 = 'coa' AND j.value = 'coa-client')))
        """;

    public static int Priority(string type)
    {
        var i = Array.IndexOf(TypePriority, type);
        return i < 0 ? TypePriority.Length : i;
    }

    /// <summary>The best <paramref name="n"/> candidates of one layer, best first.</summary>
    public static List<Candidate> Run(Layer layer, string layerName, FtsQuery query, string lowerText,
        IReadOnlyList<string>? types, string? realm, int n, TimeSpan? slotWait = null)
    {
        var wanted = (types ?? layer.CountsByType.Keys.ToList()).Where(layer.CountsByType.ContainsKey)
            .OrderBy(Priority).ThenBy(t => t, StringComparer.Ordinal).ToList();
        if (wanted.Count == 0) return new List<Candidate>();

        return layer.Searching(() =>
        {
            var exact = ExactTitle(layer, layerName, query, lowerText, wanted.Where(t => !Collapsed.Contains(t)).ToList(), realm, n);
            var byId = exact.ToDictionary(c => c.Id);
            var others = 0;
            foreach (var type in wanted)
            {
                if (others >= n) break;
                var rows = Collapsed.Contains(type)
                    ? Grouped(layer, layerName, query, lowerText, type, realm, n)
                    : OfType(layer, layerName, query, type, realm, n + exact.Count);
                foreach (var c in rows)
                    if (byId.TryAdd(c.Id, c)) others++;
            }
            return byId.Values.ToList();
        }, slotWait);
    }

    private static List<Candidate> ExactTitle(Layer layer, string layerName, FtsQuery q, string lowerText,
        IReadOnlyList<string> types, string? realm, int n)
    {
        if (types.Count == 0 || lowerText.Length == 0) return new();
        var marks = string.Join(",", types.Select((_, i) => "$" + (4 + i)));
        var args = new List<object?> { "title : (" + q.Match + ")", realm, lowerText, (long)n };
        args.AddRange(types);
        var sql = $"""
            SELECT {Columns} FROM entities_fts f JOIN entities e ON e.rowid = f.rowid
            WHERE f.entities_fts MATCH $0 AND lower(e.title) = $2 AND e.type IN ({marks}){RealmClause}
            LIMIT $3
            """;
        return layer.Query(sql, args.ToArray()).Select(r => ToCandidate(layer, layerName, r, 0, true, 1, null)).ToList();
    }

    private static List<Candidate> OfType(Layer layer, string layerName, FtsQuery q, string type, string? realm, int n) =>
        layer.Query($"""
            SELECT {Columns}, f.rank AS rank FROM entities_fts f JOIN entities e ON e.rowid = f.rowid
            WHERE f.entities_fts MATCH $0 AND e.type = $2{RealmClause}
            ORDER BY f.rank LIMIT $3
            """, q.Match, realm, type, (long)n)
            .Select(r => ToCandidate(layer, layerName, r, Convert.ToDouble(r["rank"]), false, 1, null)).ToList();

    private static List<Candidate> Grouped(Layer layer, string layerName, FtsQuery q, string lowerText, string type, string? realm, int n) =>
        layer.Query($"""
            SELECT {Columns.Replace("e.id", "MIN(e.id) AS id")}, MIN(f.rank) AS rank, COUNT(*) AS n,
                   lower(e.title) = $4 AS exact,
                   group_concat(DISTINCT json_extract(e.data_json, '$.zoneName')) AS zones
            FROM entities_fts f JOIN entities e ON e.rowid = f.rowid
            WHERE f.entities_fts MATCH $0 AND e.type = $2{RealmClause}
            GROUP BY e.title, e.realm_json, e.realm_proven, e.trust, e.source
            ORDER BY MIN(f.rank) LIMIT $3
            """, q.Match, realm, type, (long)n, lowerText)
            .Select(r => ToCandidate(layer, layerName, r, Convert.ToDouble(r["rank"]), (long)r["exact"]! != 0, (int)(long)r["n"]!, (string?)r["zones"])).ToList();

    private static Candidate ToCandidate(Layer layer, string layerName, IReadOnlyDictionary<string, object?> r,
        double rank, bool exact, int count, string? zones)
    {
        var realms = JsonSerializer.Deserialize<string[]>((string)r["realm_json"]!)!;
        return new Candidate(layer, layerName, (string)r["id"]!, (string)r["type"]!, (string)r["title"]!,
            new RealmSet(realms, (long)r["realm_proven"]! != 0), (Trust)(long)r["trust"]!, (string)r["source"]!,
            DateOnly.ParseExact((string)r["source_date"]!, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            rank, exact, count, zones);
    }

    /// <summary>Snippet for the few hits on the final page only.</summary>
    public static string Snippet(Candidate c, FtsQuery q)
    {
        var rows = c.Layer.Query("""
            SELECT snippet(entities_fts, 1, '[', ']', '...', 12) AS s FROM entities_fts
            WHERE entities_fts MATCH $0 AND rowid = (SELECT rowid FROM entities WHERE id = $1)
            """, q.Match, c.Id);
        return rows.Count == 0 ? "" : (string?)rows[0]["s"] ?? "";
    }
}
