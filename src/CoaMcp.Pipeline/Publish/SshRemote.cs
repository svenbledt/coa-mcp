using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace CoaMcp.Pipeline.Publish;

/// <summary>
/// Talks to the server with the system's scp and ssh programs, started directly (no local shell). Authentication is whatever ssh already has set up; it never prompts.
/// <para>
/// The server's login shell may not be POSIX (BledtIT uses fish), and ssh hands its command argument to that shell. So the argument is
/// always the plain text <c>sh -s</c>, which means the same in every shell, and the real script goes over stdin to <c>sh</c>.
/// Paths in the script are quoted for sh only; no other shell ever parses them.
/// </para>
/// </summary>
public sealed partial class SshRemote : IRemote
{
    private const string Heredoc = "COAMCP_DATA_END";
    private readonly string _host;

    /// <param name="host">An ssh host name or alias, such as BledtIT or user@example.org.</param>
    public SshRemote(string host)
    {
        if (!HostRule().IsMatch(host)) throw new ArgumentException($"'{host}' is not a plain host name.", nameof(host));
        _host = host;
    }

    // "--" ends option parsing, so neither a file name nor a host can be read as an option.
    // The scp path is not a shell command: OpenSSH 9+ copies over SFTP, which takes it as given.
    public IReadOnlyList<string> ScpArguments(string fileName, string remotePath) =>
        new[] { "-q", "-o", "BatchMode=yes", "--", fileName, $"{_host}:{remotePath}" };

    /// <summary>The scp arguments for a download: the remote file first, then a bare local file name (the process runs in its folder).</summary>
    public IReadOnlyList<string> ScpDownloadArguments(string remotePath, string fileName) =>
        new[] { "-q", "-o", "BatchMode=yes", "--", $"{_host}:{remotePath}", fileName };

    /// <summary>The ssh arguments. The remote command is only letters, a space and a dash, so fish, sh and every other shell read it alike.</summary>
    public IReadOnlyList<string> SshArguments() =>
        new[] { "-o", "BatchMode=yes", "--", _host, "sh -s" };

    /// <summary>
    /// The text sent to <c>sh -s</c> for one command. Data for the command's standard input travels in the same script as a quoted here-document
    /// (no expansion inside it). Line endings are always LF, whatever the platform.
    /// </summary>
    public static string BuildScript(string command, string? stdin)
    {
        if (command.Contains('\r')) throw new ArgumentException("A command must not contain a carriage return.", nameof(command));
        if (stdin is null) return command + "\n";

        var data = stdin.Replace("\r\n", "\n").Replace('\r', '\n');
        if (!data.EndsWith('\n')) data += "\n";
        if (data.Split('\n').Contains(Heredoc)) throw new ArgumentException("The data contains the here-document end marker.", nameof(stdin));
        // The braces make the whole command list read the here-document, not only its last command.
        return $"{{ {command} ; }} <<'{Heredoc}'\n{data}{Heredoc}\n";
    }

    public async Task UploadAsync(string localPath, string remotePath, CancellationToken ct)
    {
        // Run in the file's folder with a bare file name: scp reads "C:\..." as host "C".
        var full = Path.GetFullPath(localPath);
        var r = await RunProcessAsync("scp", ScpArguments(Path.GetFileName(full), remotePath), Path.GetDirectoryName(full)!, null, ct);
        if (r.ExitCode != 0) throw new IOException($"scp failed with exit code {r.ExitCode}: {r.Stderr.Trim()}");
    }

    public async Task DownloadAsync(string remotePath, string localPath, CancellationToken ct)
    {
        var full = Path.GetFullPath(localPath);
        if (File.Exists(full)) throw new IOException($"{full} already exists.");
        var r = await RunProcessAsync("scp", ScpDownloadArguments(remotePath, Path.GetFileName(full)), Path.GetDirectoryName(full)!, null, ct);
        if (r.ExitCode != 0) throw new IOException($"scp failed with exit code {r.ExitCode}: {r.Stderr.Trim()}");
    }

    public Task<RemoteResult> RunAsync(string command, string? stdin, CancellationToken ct) =>
        RunProcessAsync("ssh", SshArguments(), Environment.CurrentDirectory, BuildScript(command, stdin), ct);

    private static async Task<RemoteResult> RunProcessAsync(string program, IReadOnlyList<string> args, string workDir, string? stdin, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(program)
        {
            WorkingDirectory = workDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardInputEncoding = new UTF8Encoding(false),   // no byte-order mark in front of the script
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
