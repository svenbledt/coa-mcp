namespace CoaMcp.Pipeline.Publish;

public sealed record RemoteResult(int ExitCode, string Stdout, string Stderr);

/// <summary>The server as the publisher sees it. Tests use a fake; the real one runs scp and ssh.</summary>
public interface IRemote
{
    Task UploadAsync(string localPath, string remotePath, CancellationToken ct);

    /// <summary>Runs one shell command line on the server, with <paramref name="stdin"/> as its input.</summary>
    Task<RemoteResult> RunAsync(string command, string? stdin, CancellationToken ct);
}

public sealed class PublishException : Exception
{
    public PublishException(string message, Exception? inner = null) : base(message, inner) { }
}
