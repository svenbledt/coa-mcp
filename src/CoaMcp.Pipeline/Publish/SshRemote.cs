using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace CoaMcp.Pipeline.Publish;

/// <summary>Talks to the server with the system's scp and ssh programs, started directly (no local shell). Authentication is whatever ssh already has set up; it never prompts.</summary>
public sealed partial class SshRemote : IRemote
{
    private readonly string _host;

    /// <param name="host">An ssh host name or alias, such as BledtIT or user@example.org.</param>
    public SshRemote(string host)
    {
        if (!HostRule().IsMatch(host)) throw new ArgumentException($"'{host}' is not a plain host name.", nameof(host));
        _host = host;
    }

    // "--" ends option parsing, so neither a file name nor a host can be read as an option.
    public IReadOnlyList<string> ScpArguments(string fileName, string remotePath) =>
        new[] { "-q", "-o", "BatchMode=yes", "--", fileName, $"{_host}:{remotePath}" };

    public IReadOnlyList<string> SshArguments(string command) =>
        new[] { "-o", "BatchMode=yes", "--", _host, command };

    public async Task UploadAsync(string localPath, string remotePath, CancellationToken ct)
    {
        // Run in the file's folder with a bare file name: scp reads "C:\..." as host "C".
        var full = Path.GetFullPath(localPath);
        var r = await RunProcessAsync("scp", ScpArguments(Path.GetFileName(full), remotePath), Path.GetDirectoryName(full)!, null, ct);
        if (r.ExitCode != 0) throw new IOException($"scp failed with exit code {r.ExitCode}: {r.Stderr.Trim()}");
    }

    public Task<RemoteResult> RunAsync(string command, string? stdin, CancellationToken ct) =>
        RunProcessAsync("ssh", SshArguments(command), Environment.CurrentDirectory, stdin, ct);

    private static async Task<RemoteResult> RunProcessAsync(string program, IReadOnlyList<string> args, string workDir, string? stdin, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(program)
        {
            WorkingDirectory = workDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new IOException($"could not start {program}");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        if (stdin is not null) await p.StandardInput.WriteAsync(stdin.AsMemory(), ct);
        p.StandardInput.Close();
        await p.WaitForExitAsync(ct);
        return new RemoteResult(p.ExitCode, await stdout, await stderr);
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._@-]*$")]
    private static partial Regex HostRule();
}
