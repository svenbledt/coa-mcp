using System.Text.Json;
using CoaMcp.Core;

namespace CoaMcp.Pipeline.Dbc;

/// <summary>Where one file of the winners folder came from. <paramref name="Date"/> is the archive's modified time, yyyy-MM-dd UTC.</summary>
public sealed record DbcWinnerFile(string Archive, string Origin, string Date);

/// <summary>
/// <c>winners.json</c> in a winners folder: what the file copy can't say, because a copied file's own time is only when it was copied.
/// The folder is private and stays on this machine, like every raw <c>.dbc</c>.
/// </summary>
public sealed record DbcWinnerList(IReadOnlyDictionary<string, DbcWinnerFile> Files, ClientRelease? Client = null)
{
    public const string FileName = "winners.json";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static void Write(string dir, IReadOnlyDictionary<string, DbcWinnerFile> files, ClientRelease? client = null) =>
        File.WriteAllText(Path.Combine(dir, FileName), JsonSerializer.Serialize(new DbcWinnerList(new SortedDictionary<string, DbcWinnerFile>(files.ToDictionary(), StringComparer.Ordinal), client), Options));

    /// <summary>The list in this folder, or an error: a folder without one did not come from the dbc command.</summary>
    public static DbcWinnerList Read(string dir)
    {
        var path = Path.Combine(dir, FileName);
        if (!File.Exists(path)) throw new InvalidOperationException($"No {FileName} in '{dir}': this is not a winners folder. Run the dbc command to make one.");
        try
        {
            var list = JsonSerializer.Deserialize<DbcWinnerList>(File.ReadAllText(path), Options);
            if (list?.Files is null) throw new InvalidOperationException($"{path} lists no files.");
            return new DbcWinnerList(new Dictionary<string, DbcWinnerFile>(list.Files, StringComparer.OrdinalIgnoreCase), list.Client);
        }
        catch (JsonException ex) { throw new InvalidOperationException($"{path}: {ex.Message}", ex); }
    }
}
