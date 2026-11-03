namespace CoaMcp.Server;

/// <summary>Re-reads current.json on a timer (default every 30 seconds) so a published layer goes live without a restart.</summary>
public sealed class LayerReloader : BackgroundService
{
    private readonly LayerSet _layers;
    private readonly TimeSpan _interval;
    private readonly ILogger<LayerReloader> _log;

    public LayerReloader(LayerSet layers, IConfiguration config, ILogger<LayerReloader> log)
    {
        _layers = layers;
        _log = log;
        _interval = TimeSpan.FromSeconds(Math.Max(1, config.GetValue("Layers:ReloadSeconds", 30)));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    // Verifying a new layer reads the whole file; keep that on a pool thread.
                    await Task.Run(_layers.Reload, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogError(ex, "Reloading the layers failed; the loaded layers stay in use.");
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}
