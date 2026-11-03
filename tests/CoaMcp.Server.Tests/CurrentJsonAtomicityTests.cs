using System.Text.Json;
using CoaMcp.Core;

namespace CoaMcp.Server.Tests;

/// <summary>
/// The publisher replaces current.json by rename. These tests read it the way the server does (<see cref="Manifest.ReadFile"/>
/// and <see cref="LayerSet.Reload"/>) while it is replaced over and over, and require that no read ever sees a partial file.
/// </summary>
public class CurrentJsonAtomicityTests : IDisposable
{
    private readonly string _root = TestLayers.Publish(TestLayers.Build());
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { /* temp data */ }
    }

    private static Manifest Big(string version)
    {
        var m = new Manifest(new Dictionary<string, ManifestEntry>());
        for (var i = 0; i < 60; i++) m = m.With($"layer{i}", ManifestEntry.For($"layer{i}", version, new string('a', 64)));
        return m;
    }

    [Fact]
    public async Task ConcurrentReader_NeverSeesAPartialFile()
    {
        var path = Path.Combine(_root, "big.json");
        TestLayers.WriteManifestAtomic(path, Big("0"));
        using var stop = new CancellationTokenSource();
        var failures = new List<string>();
        var reads = 0;
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try { Manifest.ReadFile(path); Interlocked.Increment(ref reads); }
                catch (Exception ex) when (ex is JsonException or InvalidDataException) { lock (failures) failures.Add(ex.Message); }
                catch (IOException) { /* Windows may briefly refuse a read mid-rename; the content is never partial */ }
            }
        });
        for (var v = 1; v <= 300; v++) TestLayers.WriteManifestAtomic(path, Big(v.ToString()));
        await stop.CancelAsync();
        await reader;

        Assert.Empty(failures);
        Assert.True(reads > 0, "the reader never got a read in");
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task ReloadRunningWhileCurrentJsonIsReplaced_LogsNoError()
    {
        var log = new ListLogger();
        using var layers = LayerSet.Open(_root, log);
        var path = Path.Combine(_root, "current.json");
        var same = Manifest.ReadFile(path);
        using var stop = new CancellationTokenSource();
        var reloader = Task.Run(() => { while (!stop.IsCancellationRequested) layers.Reload(); });
        for (var i = 0; i < 300; i++) TestLayers.WriteManifestAtomic(path, same);
        await stop.CancelAsync();
        await reloader;
        Assert.Empty(log.Errors);
    }
}
