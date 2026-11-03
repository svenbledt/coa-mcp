using System.Globalization;
using System.Text.Json;
using CoaMcp.Core;

namespace CoaMcp.Server;

/// <summary>
/// What the server knows about one layer. <paramref name="Notice"/> is set when a newer version was published but failed
/// verification, so the old version is still the one being served.
/// </summary>
public sealed record LayerState(string Name, Layer? Layer, string? Problem, string? Notice = null);

/// <summary>
/// The layers the server answers from, by name. A missing or broken layer is reported, never fatal.
/// <see cref="Reload"/> re-reads current.json and swaps in new versions: operations started afterwards use the new
/// <see cref="Layer"/>; operations that already hold a <see cref="LayerLease"/> on the old one finish on it, and it is released when the last lease ends.
/// </summary>
public sealed class LayerSet : IDisposable
{
    /// <summary>Layers the server expects. Later tasks add to this list.</summary>
    public static readonly string[] ExpectedLayers = { "bisbeard", "spells", "paths" };

    private readonly string _root;
    private readonly ILogger? _log;
    private readonly object _reloadLock = new();
    private readonly Dictionary<string, ManifestEntry> _live = new();
    private readonly Dictionary<string, (string Key, string Notice)> _failed = new();
    private string? _lastManifestError;
    private volatile IReadOnlyList<LayerState> _layers;

    private LayerSet(string root, ILogger? log)
    {
        _root = root;
        _log = log;
        _layers = ExpectedLayers.Select(n => new LayerState(n, null, "missing: no layer file found")).ToList();
    }

    /// <summary>Loads every expected layer named in <c>current.json</c> under <paramref name="root"/>. Nothing here throws for a bad layer.</summary>
    public static LayerSet Open(string root, ILogger? log = null)
    {
        var set = new LayerSet(root, log);
        set.Reload();
        return set;
    }

    /// <summary>
    /// Re-reads current.json and loads any layer whose entry changed. Verification reads whole files, so call this off the request path.
    /// A broken current.json or a layer that fails verification leaves what is loaded as it is and logs an error.
    /// </summary>
    public void Reload()
    {
        lock (_reloadLock)
        {
            Manifest manifest;
            try
            {
                manifest = Manifest.ReadFile(Path.Combine(_root, Manifest.FileName));
                _lastManifestError = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
            {
                // Log a persistent problem once, not every 30 seconds.
                if (_lastManifestError != ex.Message)
                    _log?.LogError(ex, "{File} could not be read; keeping the layers that are loaded.", Manifest.FileName);
                _lastManifestError = ex.Message;
                return;
            }

            var next = _layers.ToList();
            var retired = new List<Layer>();
            for (var i = 0; i < next.Count; i++)
            {
                var name = next[i].Name;
                if (!manifest.Layers.TryGetValue(name, out var entry))
                {
                    if (next[i].Layer is not null) _log?.LogWarning("Layer {Layer} is no longer listed in current.json; keeping the loaded version.", name);
                    continue;
                }
                if (_live.TryGetValue(name, out var live) && live == entry)
                {
                    if (next[i].Notice is not null) next[i] = next[i] with { Notice = null };
                    _failed.Remove(name);
                    continue;
                }
                var key = FailureKey(entry);
                if (_failed.TryGetValue(name, out var failed) && failed.Key == key) continue;   // already reported; the file has not changed

                try
                {
                    var layer = LayerLoader.Load(_root, name, entry);
                    if (next[i].Layer is { } old) retired.Add(old);
                    _live[name] = entry;
                    _failed.Remove(name);
                    next[i] = new LayerState(name, layer, null);
                    _log?.LogInformation("Layer {Layer} is now version {Version}.", name, entry.Version);
                }
                catch (Exception ex)
                {
                    var serving = next[i].Layer is { } cur ? $"still serving v{cur.Meta.Version}" : "no version is being served";
                    _log?.LogError(ex, "Layer {Layer} version {Version} failed verification and was not loaded; {Serving}.", name, entry.Version, serving);
                    var notice = $"version {entry.Version} was published but failed verification and was not loaded (see the server log)";
                    _failed[name] = (key, notice);
                    next[i] = next[i].Layer is not null
                        ? next[i] with { Notice = notice }
                        : new LayerState(name, null, ex is FileNotFoundException ? "missing: no layer file found" : "could not be opened (see the server log)");
                }
            }
            _layers = next;
            foreach (var old in retired) old.Retire();
        }
    }

    private string FailureKey(ManifestEntry e)
    {
        var info = new FileInfo(Path.Combine(_root, e.File));
        return $"{e.Version}|{e.Sha256}|{(info.Exists ? info.Length : -1)}|{(info.Exists ? info.LastWriteTimeUtc.Ticks : 0)}";
    }

    public IReadOnlyList<LayerState> Status() => _layers;

    /// <summary>
    /// Leases the layer currently serving <paramref name="name"/> for one whole operation, or null if none is loaded.
    /// Dispose the lease when the operation is done; a swapped-out layer stays open until then.
    /// </summary>
    public LayerLease? Lease(string name)
    {
        while (true)
        {
            var snapshot = _layers;
            var layer = snapshot.FirstOrDefault(l => l.Name == name)?.Layer;
            if (layer is null) return null;
            var lease = layer.TryLease();
            if (lease is not null) return lease;
            // The layer was swapped out and released between the read and the lease: fetch the new one.
            if (ReferenceEquals(snapshot, _layers)) return null;   // closed for shutdown
        }
    }

    private List<(string Name, LayerLease Lease)> LeaseAll()
    {
        var leases = new List<(string, LayerLease)>();
        foreach (var name in ExpectedLayers)
            if (Lease(name) is { } l) leases.Add((name, l));
        return leases;
    }

    /// <summary>Every layer that holds <paramref name="id"/>. More than one is possible, and none are merged.</summary>
    public IReadOnlyList<(string LayerName, LayerMeta Meta, Entity Entity)> Get(string id)
    {
        var found = new List<(string, LayerMeta, Entity)>();
        foreach (var name in ExpectedLayers)
        {
            using var lease = Lease(name);
            if (lease?.Layer.Get(id) is { } e) found.Add((name, lease.Layer.Meta, e));
        }
        return found;
    }

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
        var leases = LeaseAll();   // held until the hits are built (snippets query the layer too), across any wait for a search slot
        try
        {
            foreach (var (name, lease) in leases)
                all.AddRange(LayerSearch.Run(lease.Layer, name, q, lower, types, realm, limit + offset));

            return all.OrderByDescending(c => c.Exact).ThenBy(c => LayerSearch.Priority(c.Type)).ThenBy(c => c.Rank)
                .ThenBy(c => c.Id, StringComparer.Ordinal)
                .Skip(offset).Take(limit)
                .Select(c => new LayerHit(c.LayerName, c.Id, c.Type, c.Title, c.Count == 1 ? LayerSearch.Snippet(c, q) : "",
                    c.Realm, c.Trust, c.Source, c.SourceDate, c.Count, c.Zones))
                .ToList();
        }
        finally { foreach (var (_, lease) in leases) lease.Dispose(); }
    }

    public void Dispose()
    {
        lock (_reloadLock)
            foreach (var l in _layers) l.Layer?.Dispose();
    }

    public static string Describe(Layer layer) =>
        $"v{layer.Meta.Version}, built {layer.Meta.BuiltAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}, source date {layer.Meta.SourceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
}
