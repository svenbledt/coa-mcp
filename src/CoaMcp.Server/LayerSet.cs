using System.Globalization;
using System.Text.Json;
using CoaMcp.Core;

namespace CoaMcp.Server;

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
                states.Add(new LayerState(name, new Layer(path), null));
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

    /// <summary>
    /// Searches all layers and orders the hits: exact title match, then entity type priority, then bm25 within a type.
    /// Identical markers collapse into one hit. <paramref name="types"/> null means every type.
    /// </summary>
    public IReadOnlyList<LayerHit> Search(string text, IReadOnlyList<string>? types, string? realm, int limit, int offset)
    {
        var q = FtsQuery.FromUser(text);
        if (q.IsEmpty) return Array.Empty<LayerHit>();
        var lower = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

        var all = new List<Candidate>();
        foreach (var state in _layers.Where(l => l.Layer is not null))
            all.AddRange(LayerSearch.Run(state.Layer!, state.Name, q, lower, types, realm, limit + offset));

        return all.OrderByDescending(c => c.Exact).ThenBy(c => LayerSearch.Priority(c.Type)).ThenBy(c => c.Rank)
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .Skip(offset).Take(limit)
            .Select(c => new LayerHit(c.LayerName, c.Id, c.Type, c.Title, c.Count == 1 ? LayerSearch.Snippet(c, q) : "",
                c.Realm, c.Trust, c.Source, c.SourceDate, c.Count, c.Zones))
            .ToList();
    }

    public void Dispose()
    {
        foreach (var l in _layers) l.Layer?.Dispose();
    }

    public static string Describe(Layer layer) =>
        $"v{layer.Meta.Version}, built {layer.Meta.BuiltAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}, source date {layer.Meta.SourceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
}
