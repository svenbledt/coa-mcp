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
    public void UnreadableCurrentJson_WarnsAtFirst_AndLogsOneErrorWhenItPersistsAcrossThreeReloads()
    {
        var log = new ListLogger();
        using var layers = LayerSet.Open(_root, log);
        var path = Path.Combine(_root, "current.json");
        var good = File.ReadAllText(path);
        void Unreadable() { File.Delete(path); Directory.CreateDirectory(path); }   // refuses a read on every OS, like a replace in progress
        void Readable() { Directory.Delete(path); File.WriteAllText(path, good); }
        int Warnings() => log.Lines.Count(l => l.Level == Microsoft.Extensions.Logging.LogLevel.Warning);

        Unreadable();
        layers.Reload();
        layers.Reload();
        Assert.Empty(log.Errors);
        Assert.Equal(2, Warnings());

        layers.Reload();
        Assert.Single(log.Errors);
        layers.Reload();
        layers.Reload();
        Assert.Single(log.Errors);   // one error for one outage, not one every 30 seconds
        Assert.Equal(2, Warnings());

        Readable();
        layers.Reload();
        Unreadable();
        layers.Reload();
        layers.Reload();
        Assert.Single(log.Errors);   // a good read starts the count again
        Assert.Equal(4, Warnings());
        Readable();
    }

    [Fact]
    public void DamagedCurrentJson_IsAnErrorAtOnce()
    {
        var log = new ListLogger();
        using var layers = LayerSet.Open(_root, log);
        File.WriteAllText(Path.Combine(_root, "current.json"), "{ not json");

        layers.Reload();

        Assert.Single(log.Errors);   // a rename never leaves this, so waiting would only hide it
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
