using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CoaMcp.Pipeline.Publish;

namespace CoaMcp.Pipeline.Knowledge;

/// <summary>
/// Takes a consistent snapshot of the production store, downloads it to a temp folder outside the repository, hands it to the caller, and deletes
/// both copies whatever happens. Production is only read: the one thing run there is the server's own `admin snapshot` (VACUUM INTO), which
/// writes a new file inside the writable folder and nothing else.
/// </summary>
public sealed partial class SnapshotPuller
{
    /// <summary>The Coolify application id; the running container's name contains it.</summary>
    public const string AppId = "ukfeegudmo0pfyq1nxe7tadz";

    /// <summary>The same folder, as the host sees it and as the container sees it.</summary>
    public const string HostWritable = "/data/coamcp/writable", ContainerWritable = "/data/writable";

    private readonly IRemote _remote;
    private readonly string _tempRoot;
    private readonly TextWriter _warnings;

    public SnapshotPuller(IRemote remote, string tempRoot, TextWriter warnings)
    {
        _remote = remote;
        _tempRoot = tempRoot;
        _warnings = warnings;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ContainerName();

    public static bool IsValidContainerName(string name) => ContainerName().IsMatch(name);

    /// <returns>False if production has no store yet (nothing was copied and <paramref name="use"/> was not called).</returns>
    public async Task<bool> PullAsync(string? container, Action<string> use, CancellationToken ct)
    {
        if (container is not null && !IsValidContainerName(container)) throw new KnowledgeException("The container name may only hold letters, digits, dots, dashes and underscores.");

        var store = HostWritable + "/store.sqlite";
        var present = await RunAsync(RemoteCommands.PrintIfPresent(store), "check for the store", ct);
        if (!present.Stdout.Contains("present", StringComparison.Ordinal)) return false;

        var name = "pull-" + RandomNumberGenerator.GetHexString(32, lowercase: true) + ".sqlite";
        var hostPath = $"{HostWritable}/{name}";
        var work = Path.Combine(_tempRoot, "coamcp-pull-" + RandomNumberGenerator.GetHexString(12, lowercase: true));
        try
        {
            container ??= await FindContainerAsync(ct);
            await RunAsync($"docker exec {Shell.Quote(container)} dotnet /app/CoaMcp.Server.dll admin snapshot {Shell.Quote($"{ContainerWritable}/{name}")}", "take the snapshot", ct);

            var hashLine = (await RunAsync(RemoteCommands.Sha256IfPresent(hostPath), "check the snapshot", ct)).Stdout.Trim();
            var remoteHash = hashLine.Length >= 64 ? hashLine[..64].ToLowerInvariant() : "";
            if (remoteHash.Length != 64 || !remoteHash.All(Uri.IsHexDigit)) throw new KnowledgeException("The server did not report a checksum for the snapshot.");

            Directory.CreateDirectory(work);
            var local = Path.Combine(work, "snapshot.sqlite");
            try { await _remote.DownloadAsync(hostPath, local, ct); }
            catch (IOException ex) { throw new KnowledgeException("Downloading the snapshot failed: " + KnowledgeFormat.Escape(ex.Message), ex); }
            var localHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(local, ct)));
            if (localHash != remoteHash) throw new KnowledgeException("The downloaded snapshot does not match its checksum on the server, so it was discarded.");

            use(local);
            return true;
        }
        finally
        {
            await RemoveRemoteAsync(hostPath);
            try { if (Directory.Exists(work)) Directory.Delete(work, true); }
            catch (IOException ex) { _warnings.WriteLine($"Warning: could not remove the local temp folder {work} ({KnowledgeFormat.Escape(ex.Message)}). Delete it by hand."); }
        }
    }

    private async Task<string> FindContainerAsync(CancellationToken ct)
    {
        var r = await RunAsync($"docker ps --filter {Shell.Quote("name=" + AppId)} --format {Shell.Quote("{{.Names}}")}", "look for the running container", ct);
        var names = r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
        if (names.Count == 0) throw new KnowledgeException($"No running container has {AppId} in its name. Pass the name with --container.");
        if (names.Count > 1)
            throw new KnowledgeException($"{names.Count} running containers match {AppId} ({string.Join(", ", names.Take(5).Select(KnowledgeFormat.Escape))}). Pass the right one with --container.");
        if (!IsValidContainerName(names[0])) throw new KnowledgeException("The server reported a container name with unexpected characters, so it was not used.");
        return names[0];
    }

    /// <summary>Runs a command and fails with the server's own message if it exits non-zero. That text is escaped, since it came from outside.</summary>
    private async Task<RemoteResult> RunAsync(string command, string what, CancellationToken ct)
    {
        RemoteResult r;
        try { r = await _remote.RunAsync(command, null, ct); }
        catch (IOException ex) { throw new KnowledgeException($"Could not {what}: {KnowledgeFormat.Escape(ex.Message)}", ex); }
        if (r.ExitCode == 0) return r;
        var detail = (r.Stderr.Length > 0 ? r.Stderr : r.Stdout).Trim();
        if (detail.Length > 300) detail = detail[..300] + "...";
        throw new KnowledgeException($"Could not {what} (exit code {r.ExitCode}): {KnowledgeFormat.Escape(detail)}");
    }

    private async Task RemoveRemoteAsync(string hostPath)
    {
        try
        {
            var r = await _remote.RunAsync(RemoteCommands.Remove(hostPath), null, CancellationToken.None);
            if (r.ExitCode != 0) throw new IOException($"exit code {r.ExitCode}: {r.Stderr.Trim()}");
        }
        catch (Exception ex)
        {
            _warnings.WriteLine($"Warning: could not remove the snapshot {hostPath} on the server ({KnowledgeFormat.Escape(ex.Message)}). Delete it by hand.");
        }
    }
}
