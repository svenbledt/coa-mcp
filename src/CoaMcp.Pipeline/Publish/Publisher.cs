using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CoaMcp.Core;

namespace CoaMcp.Pipeline.Publish;

/// <summary>
/// Puts a layer on the server safely. Order matters: the guard runs first; the file lands under a .tmp name; its SHA-256 is checked
/// on the server; only then does it get its final name; and current.json, the only thing the server follows, changes last and atomically.
/// Anything that fails before that last step leaves current.json exactly as it was. Older version files stay, so a rollback is one edit of current.json.
/// </summary>
public sealed partial class Publisher
{
    public const string DefaultRemoteRoot = "/data/coamcp";

    private readonly IRemote _remote;
    private readonly TextWriter _out;
    private readonly string _root;

    public Publisher(IRemote remote, TextWriter output, string remoteRoot = DefaultRemoteRoot)
    {
        _remote = remote;
        _out = output;
        _root = remoteRoot.TrimEnd('/');
    }

    public async Task PublishAsync(string layerPath, bool dryRun, CancellationToken ct)
    {
        var guard = PublishGuard.Check(layerPath);
        if (!guard.Ok)
            throw new PublishException("The publish guard refused this layer:\n" + string.Join("\n", guard.Problems.Select(p => "  - " + p)));
        _out.WriteLine($"Guard passed: {guard.LayerName} v{guard.Version}, {guard.RowsScanned} rows scanned in {guard.Elapsed.TotalSeconds:F1} s.");

        var (name, version) = (guard.LayerName, guard.Version);
        if (!Manifest.IsSafeName(name) || !Manifest.IsSafeVersion(version))
            throw new PublishException($"Layer name '{name}' or version '{version}' is not usable in a file name.");

        var sha = await Task.Run(() => Sha256Hex(layerPath), ct);
        var dir = $"{_root}/layers/{name}";
        var final = $"{dir}/{version}.sqlite";
        var tmp = final + ".tmp";
        var current = $"{_root}/{Manifest.FileName}";

        _out.WriteLine($"Plan:\n  upload   {layerPath} -> {tmp}\n  sha256   {sha}\n  rename   {tmp} -> {final} (read-only)\n  point    {current} at {name} v{version}; other layers and older versions stay");
        if (dryRun)
        {
            _out.WriteLine("Dry run: nothing was sent.");
            return;
        }

        // Read current.json first: if it is broken, stop before anything is uploaded.
        var existing = await RunOk(RemoteCommands.CatIfPresent(current), null, ct);
        Manifest manifest;
        try
        {
            manifest = string.IsNullOrWhiteSpace(existing.Stdout) ? new Manifest(new Dictionary<string, ManifestEntry>()) : Manifest.Parse(existing.Stdout);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException)
        {
            throw new PublishException($"{current} on the server is not valid; refusing to overwrite it. Fix it by hand first: {ex.Message}", ex);
        }

        await RunOk(RemoteCommands.MakeDirectory(dir), null, ct);

        var already = ParseSha((await RunOk(RemoteCommands.Sha256IfPresent(final), null, ct)).Stdout);
        if (already is not null && already != sha)
            throw new PublishException($"{final} already exists with different content (sha256 {already}). A version is immutable; build a new version.");
        if (already is null)
        {
            await UploadAndVerify(layerPath, tmp, final, sha, ct);
            _out.WriteLine($"Uploaded and verified {final}.");
        }
        else
        {
            _out.WriteLine($"{final} is already on the server with the same checksum; skipping the upload.");
        }

        var updated = manifest.With(name, ManifestEntry.For(name, version, sha));
        await RunOk(RemoteCommands.ReplaceFromStdin(current + ".tmp", current), updated.ToJson(), ct);
        _out.WriteLine($"{current} now points {name} at v{version}.");
    }

    private async Task UploadAndVerify(string layerPath, string tmp, string final, string sha, CancellationToken ct)
    {
        try
        {
            try
            {
                await _remote.UploadAsync(layerPath, tmp, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new PublishException($"The upload failed: {ex.Message}", ex);
            }
            var remoteSha = ParseSha((await RunOk(RemoteCommands.Sha256IfPresent(tmp), null, ct)).Stdout);
            if (remoteSha != sha)
                throw new PublishException($"sha256 mismatch after upload: local {sha}, server {remoteSha ?? "(no file)"}. The upload was discarded.");
            await RunOk(RemoteCommands.LockAndRename(tmp, final), null, ct);
        }
        catch
        {
            // Best effort: never leave a half-uploaded file behind. The original error is the one that matters.
            try { await _remote.RunAsync(RemoteCommands.Remove(tmp), null, CancellationToken.None); } catch (Exception) { /* reported error stands */ }
            throw;
        }
    }

    private async Task<RemoteResult> RunOk(string command, string? stdin, CancellationToken ct)
    {
        RemoteResult r;
        try
        {
            r = await _remote.RunAsync(command, stdin, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not PublishException)
        {
            throw new PublishException($"Could not run a command on the server: {ex.Message}", ex);
        }
        if (r.ExitCode != 0) throw new PublishException($"A command on the server failed (exit {r.ExitCode}): {r.Stderr.Trim()}\n  command: {command}");
        return r;
    }

    private static string? ParseSha(string output)
    {
        var m = ShaLine().Match(output);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string Sha256Hex(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(fs));
    }

    [GeneratedRegex(@"^([0-9a-f]{64})\b", RegexOptions.Multiline)]
    private static partial Regex ShaLine();
}
