using System.Collections.Concurrent;
using CoaMcp.Core;

namespace CoaMcp.Server;

/// <summary>
/// One layer file with a small pool of read-only connections, so a slow search never holds up other requests.
/// At most <see cref="SearchSlots"/> searches run at once per layer, which leaves connections free for lookups.
/// A layer that has been swapped out is <see cref="Retire"/>d: its connections close once the requests running on it finish.
/// </summary>
public sealed class Layer : IDisposable
{
    private const int PoolSize = 4;
    private const int SearchSlots = 2;

    private readonly string _path;
    private readonly SemaphoreSlim _pool = new(PoolSize);
    private readonly SemaphoreSlim _searches = new(SearchSlots);
    private readonly ConcurrentBag<LayerReader> _idle = new();
    private readonly List<LayerReader> _all = new();
    private readonly object _gate = new();
    private int _active;
    private bool _retired;
    private bool _disposed;

    public LayerMeta Meta { get; }
    public IReadOnlyDictionary<string, long> CountsByType { get; }

    public Layer(string path)
    {
        _path = path;
        var first = new LayerReader(path);
        try
        {
            _all.Add(first);
            _idle.Add(first);
            Meta = first.Meta;
            CountsByType = first.Query("SELECT type, COUNT(*) AS n FROM entities GROUP BY type ORDER BY type")
                .ToDictionary(r => (string)r["type"]!, r => (long)r["n"]!);
        }
        catch
        {
            first.Dispose();
            throw;
        }
    }

    /// <summary>Queries running right now. A retired layer stays open until this is zero.</summary>
    public int ActiveRequests { get { lock (_gate) return _active; } }

    public bool IsDisposed { get { lock (_gate) return _disposed; } }

    /// <summary>
    /// Marks the layer as replaced. After <paramref name="grace"/> (time for a request that already fetched this layer to start its query)
    /// the connections close as soon as no query is running.
    /// </summary>
    public void Retire(TimeSpan grace)
    {
        if (grace <= TimeSpan.Zero) { RetireNow(); return; }
        _ = Task.Delay(grace).ContinueWith(_ => RetireNow(), TaskScheduler.Default);
    }

    private void RetireNow()
    {
        bool close;
        lock (_gate)
        {
            _retired = true;
            close = _active == 0 && !_disposed;
        }
        if (close) Dispose();
    }

    private T Use<T>(Func<LayerReader, T> work)
    {
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Layer), "This layer version was replaced and released.");
            _active++;
        }
        try
        {
            _pool.Wait();
            try
            {
                if (!_idle.TryTake(out var reader))
                {
                    reader = new LayerReader(_path);
                    lock (_all) _all.Add(reader);
                }
                try { return work(reader); }
                finally { _idle.Add(reader); }
            }
            finally { _pool.Release(); }
        }
        finally
        {
            bool close;
            lock (_gate)
            {
                _active--;
                close = _retired && _active == 0 && !_disposed;
            }
            if (close) Dispose();
        }
    }

    public Entity? Get(string id) => Use(r => r.Get(id));

    /// <summary>Code-defined SQL only, never user text.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(string sql, params object?[] args) =>
        Use(r => r.Query(sql, args));

    /// <summary>Runs a search under the per-layer search limit. The work calls <see cref="Query"/> for each statement.</summary>
    public T Searching<T>(Func<T> work)
    {
        _searches.Wait();
        try { return work(); }
        finally { _searches.Release(); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        lock (_all) foreach (var r in _all) r.Dispose();
    }
}
