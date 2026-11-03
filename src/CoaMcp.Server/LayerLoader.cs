using System.Security.Cryptography;
using CoaMcp.Core;

namespace CoaMcp.Server;

/// <summary>
/// Opens a layer version named in current.json only after proving it is the file that was published:
/// the SHA-256 matches, the file's own metadata agrees, and SQLite's quick_check passes.
/// The checks read the whole file, so callers run this off the request path.
/// </summary>
public static class LayerLoader
{
    public static Layer Load(string root, string name, ManifestEntry entry)
    {
        // The manifest parser only accepts layers/<name>/<version>.sqlite, so this stays under the root.
        var path = Path.GetFullPath(Path.Combine(root, entry.File));
        if (!File.Exists(path)) throw new FileNotFoundException($"layer file {entry.File} does not exist");

        var sha = Sha256Hex(path);
        if (!string.Equals(sha, entry.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException($"sha256 mismatch for {entry.File}: current.json says {entry.Sha256}, the file is {sha}");

        using (var reader = new LayerReader(path))
        {
            if (reader.Meta.Name != name)
                throw new InvalidDataException($"{entry.File} is the '{reader.Meta.Name}' layer, not '{name}'");
            if (reader.Meta.Version != entry.Version)
                throw new InvalidDataException($"{entry.File} is version '{reader.Meta.Version}' inside, current.json says '{entry.Version}'");
            var check = reader.Query("PRAGMA quick_check");
            if (check.Count != 1 || !Equals(check[0]["quick_check"], "ok"))
                throw new InvalidDataException($"{entry.File} failed PRAGMA quick_check: " + string.Join("; ", check.Take(3).Select(r => r["quick_check"])));
        }
        return new Layer(path);
    }

    public static string Sha256Hex(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(fs));
    }
}
