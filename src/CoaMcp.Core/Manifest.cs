using System.Text.Json;
using System.Text.RegularExpressions;

namespace CoaMcp.Core;

/// <summary>One live layer: its version, its file relative to the data root, and the SHA-256 of that file.</summary>
public sealed record ManifestEntry(string Version, string File, string Sha256)
{
    /// <summary>The entry for <paramref name="name"/> at <paramref name="version"/>; the file name follows from the two.</summary>
    public static ManifestEntry For(string name, string version, string sha256) =>
        new(version, Manifest.RelativeFile(name, version), sha256);
}

/// <summary>
/// current.json: which version of each layer the server should serve.
/// <c>{ "layers": { "name": { "version": "v", "file": "layers/name/v.sqlite", "sha256": "hex" } } }</c>
/// The parser is strict, so a hand-edited or hostile file can never point outside layers/.
/// </summary>
public sealed partial class Manifest
{
    public const string FileName = "current.json";

    public IReadOnlyDictionary<string, ManifestEntry> Layers { get; }

    public Manifest(IReadOnlyDictionary<string, ManifestEntry> layers) => Layers = layers;

    public static string RelativeFile(string name, string version) => $"layers/{name}/{version}.sqlite";

    public static bool IsSafeName(string? name) => name is not null && NameRule().IsMatch(name);
    public static bool IsSafeVersion(string? version) => version is not null && VersionRule().IsMatch(version);

    public Manifest With(string name, ManifestEntry entry)
    {
        var next = new Dictionary<string, ManifestEntry>(Layers, StringComparer.Ordinal) { [name] = entry };
        return new Manifest(next);
    }

    /// <summary>Throws <see cref="InvalidDataException"/> (or <see cref="JsonException"/> for broken JSON) on anything unexpected.</summary>
    public static Manifest Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("layers", out var layers)
            || layers.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("current.json must be an object with a \"layers\" object.");

        var result = new Dictionary<string, ManifestEntry>(StringComparer.Ordinal);
        foreach (var l in layers.EnumerateObject())
        {
            if (!IsSafeName(l.Name)) throw new InvalidDataException($"current.json: '{l.Name}' is not a valid layer name.");
            var version = Text(l.Value, "version", l.Name);
            var file = Text(l.Value, "file", l.Name);
            var sha = Text(l.Value, "sha256", l.Name);
            if (!IsSafeVersion(version)) throw new InvalidDataException($"current.json: layer '{l.Name}' has an invalid version.");
            if (file != RelativeFile(l.Name, version))
                throw new InvalidDataException($"current.json: layer '{l.Name}' must use the file {RelativeFile(l.Name, version)}.");
            if (!Sha256Rule().IsMatch(sha)) throw new InvalidDataException($"current.json: layer '{l.Name}' has an invalid sha256.");
            result[l.Name] = new ManifestEntry(version, file, sha);
        }
        return new Manifest(result);
    }

    private static string Text(JsonElement e, string property, string layer)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(property, out var p) || p.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"current.json: layer '{layer}' needs a text \"{property}\".");
        return p.GetString()!;
    }

    public string ToJson()
    {
        var layers = Layers.OrderBy(l => l.Key, StringComparer.Ordinal).ToDictionary(
            l => l.Key, l => new Dictionary<string, string> { ["version"] = l.Value.Version, ["file"] = l.Value.File, ["sha256"] = l.Value.Sha256 });
        return JsonSerializer.Serialize(new { layers }, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Reads and parses <paramref name="path"/>. The file is opened so that a writer can rename a new file over it meanwhile
    /// (on Windows a plain read would block that rename).
    /// </summary>
    public static Manifest ReadFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>
    /// Writes a temp file in the same folder, then renames it over <paramref name="path"/>. A reader sees the old file or the new one, never half of either.
    /// </summary>
    public static void WriteAtomic(string path, Manifest manifest)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmp, manifest.ToJson());
            // POSIX rename never fails because a reader has the file open. Windows can refuse for a moment, so retry briefly.
            for (var attempt = 0; ; attempt++)
            {
                try { File.Move(tmp, path, overwrite: true); break; }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException && attempt < 200) { Thread.Sleep(5); }
            }
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,31}$")] private static partial Regex NameRule();
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")] private static partial Regex VersionRule();
    [GeneratedRegex("^[0-9a-f]{64}$")] private static partial Regex Sha256Rule();
}
