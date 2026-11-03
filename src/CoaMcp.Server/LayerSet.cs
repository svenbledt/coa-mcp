using System.Globalization;
using System.Text.Json;
using CoaMcp.Core;

namespace CoaMcp.Server;

/// <summary>One open layer. SQLite connections are not safe for concurrent use, so every call takes the layer's lock.</summary>
public sealed class Layer : IDisposable
{
    private readonly LayerReader _reader;
    private readonly object _gate = new();

    public LayerMeta Meta => _reader.Meta;
    public IReadOnlyDictionary<string, long> CountsByType { get; }

    public Layer(LayerReader reader)
    {
        _reader = reader;
        CountsByType = _reader.Query("SELECT type, COUNT(*) AS n FROM entities GROUP BY type ORDER BY type")
            .ToDictionary(r => (string)r["type"]!, r => (long)r["n"]!);
    }

    public Entity? Get(string id) { lock (_gate) return _reader.Get(id); }

    public IReadOnlyList<SearchHit> Search(FtsQuery q, string? type, string? realm, int limit, int offset)
    { lock (_gate) return _reader.Search(q, type, realm, limit, offset); }

    /// <summary>Code-defined SQL only, never user text.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(string sql, params object?[] args)
    { lock (_gate) return _reader.Query(sql, args); }

    public void Dispose() => _reader.Dispose();
}

public sealed record LayerHit(string LayerName, SearchHit Hit, RealmSet Realm, Trust Trust);

public sealed record LayerState(string Name, Layer? Layer, string? Problem);

/// <summary>The layers the server answers from, by name. A missing or broken layer is reported, never fatal.</summary>
public sealed class LayerSet : IDisposable
{
    /// <summary>Layers the server expects. Later tasks add to this list.</summary>
    public static readonly string[] ExpectedLayers = { "bisbeard", "spells" };

    private readonly IReadOnlyList<LayerState> _layers;

    private LayerSet(IReadOnlyList<LayerState> layers) => _layers = layers;

    public static LayerSet Open(string dir, ILogger? log = null)
    {
        var states = new List<LayerState>();
        foreach (var name in ExpectedLayers)
        {
            var path = Path.Combine(dir, name + ".sqlite");
            if (!File.Exists(path))
            {
                log?.LogWarning("Layer {Layer} is missing: {Path} does not exist.", name, path);
                states.Add(new LayerState(name, null, "missing: no layer file found"));
                continue;
            }
            try
            {
                states.Add(new LayerState(name, new Layer(new LayerReader(path)), null));
            }
            catch (Exception ex)
            {
                log?.LogError(ex, "Layer {Layer} could not be opened from {Path}.", name, path);
                states.Add(new LayerState(name, null, "could not be opened (see the server log)"));
            }
        }
        return new LayerSet(states);
    }

    public IReadOnlyList<LayerState> Status() => _layers;

    public Layer? Current(string name) => _layers.FirstOrDefault(l => l.Name == name)?.Layer;

    /// <summary>Every layer that holds <paramref name="id"/>. More than one is possible, and none are merged.</summary>
    public IReadOnlyList<(string LayerName, Layer Layer, Entity Entity)> Get(string id) =>
        _layers.Where(l => l.Layer is not null)
            .Select(l => (l.Name, Layer: l.Layer!, Entity: l.Layer!.Get(id)))
            .Where(x => x.Entity is not null)
            .Select(x => (x.Name, x.Layer, x.Entity!)).ToList();

    /// <summary>Searches all layers (each entity type separately when several are given) and merges by rank, best first.</summary>
    public IReadOnlyList<LayerHit> Search(FtsQuery query, IReadOnlyList<string?> types, string? realm, int limit, int offset)
    {
        var hits = new List<LayerHit>();
        foreach (var state in _layers.Where(l => l.Layer is not null))
            foreach (var type in types)
                foreach (var h in state.Layer!.Search(query, type, realm, limit + offset, 0))
                {
                    var meta = state.Layer.Query("SELECT realm_json, realm_proven, trust FROM entities WHERE id = $0", h.Id)[0];
                    var realms = JsonSerializer.Deserialize<string[]>((string)meta["realm_json"]!)!;
                    hits.Add(new LayerHit(state.Name, h, new RealmSet(realms, (long)meta["realm_proven"]! != 0),
                        (Trust)(long)meta["trust"]!));
                }
        return hits.OrderBy(h => h.Hit.Rank).ThenBy(h => h.Hit.Id, StringComparer.Ordinal)
            .Skip(offset).Take(limit).ToList();
    }

    public void Dispose()
    {
        foreach (var l in _layers) l.Layer?.Dispose();
    }

    public static string Describe(Layer layer) =>
        $"v{layer.Meta.Version}, built {layer.Meta.BuiltAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}, source date {layer.Meta.SourceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
}
