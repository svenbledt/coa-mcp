namespace CoaMcp.Pipeline.Publish;

public static class PublishCli
{
    public const string Usage = "Usage: coamcp-pipeline publish --layer <layer.sqlite> --host <ssh host> [--dry-run]\n" +
                                "       --dry-run runs the guard and prints the plan without contacting the server (--host is then optional).";

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, Func<string, IRemote>? remoteFor = null)
    {
        string? layer = null, host = null;
        var dryRun = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dry-run": dryRun = true; break;
                case "--layer" when i + 1 < args.Length: layer = args[++i]; break;
                case "--host" when i + 1 < args.Length: host = args[++i]; break;
                default: error.WriteLine(Usage); return 2;
            }
        }
        if (layer is null || (host is null && !dryRun)) { error.WriteLine(Usage); return 2; }

        try
        {
            IRemote remote = host is null ? new UnreachableRemote() : (remoteFor ?? (h => new SshRemote(h)))(host);
            await new Publisher(remote, output).PublishAsync(layer, dryRun, CancellationToken.None);
            return 0;
        }
        catch (Exception ex) when (ex is PublishException or ArgumentException or IOException)
        {
            error.WriteLine("publish failed: " + ex.Message);
            return 1;
        }
    }

    /// <summary>Stands in for the server on a dry run without a host; any use is a bug.</summary>
    private sealed class UnreachableRemote : IRemote
    {
        public Task UploadAsync(string localPath, string remotePath, CancellationToken ct) => throw new InvalidOperationException("dry run contacted the server");
        public Task DownloadAsync(string remotePath, string localPath, CancellationToken ct) => throw new InvalidOperationException("dry run contacted the server");
        public Task<RemoteResult> RunAsync(string command, string? stdin, CancellationToken ct) => throw new InvalidOperationException("dry run contacted the server");
    }
}
