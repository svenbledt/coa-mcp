using System.Text.Json;
using System.Text.RegularExpressions;

namespace CoaMcp.Pipeline.Dbc;

/// <summary>One field in a table's config. Type <c>unknown</c> or <c>unknown_byte</c> means the position is known but not the name.</summary>
public sealed record DbcConfigField(string Name, string Type)
{
    public const string Unknown = "unknown", UnknownByte = "unknown_byte";
    public bool Proven => Type is not (Unknown or UnknownByte);
    public int Width => Type is "byte" or UnknownByte ? 1 : 4;
}

/// <summary>A file of <c>config/dbc-fields</c>: the proven layout of one table, and where the proof came from.</summary>
public sealed record DbcTableConfig(string Table, string Source, IReadOnlyList<DbcConfigField> Fields);

public static partial class DbcFieldConfig
{
    private static readonly string[] Types = ["int", "uint", "float", "string", "byte", DbcConfigField.Unknown, DbcConfigField.UnknownByte];

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}$")]
    private static partial Regex ColumnName();

    /// <summary>Loads every table's config. The config is ours, so anything wrong in it stops the export rather than being worked around.</summary>
    public static IReadOnlyDictionary<string, DbcTableConfig> LoadAll(string dir)
    {
        if (!Directory.Exists(dir)) throw new InvalidDataException($"no field config folder at {dir}");
        var all = new Dictionary<string, DbcTableConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.GetFiles(dir, "*.json").Order(StringComparer.Ordinal))
        {
            var t = Load(file);
            all.Add(t.Table, t);
        }
        return all;
    }

    private static DbcTableConfig Load(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        DbcTableConfig? t;
        try
        {
            t = JsonSerializer.Deserialize<DbcTableConfig>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex) { throw new InvalidDataException($"{file}: {ex.Message}", ex); }

        if (t?.Fields is null || t.Fields.Count == 0) throw new InvalidDataException($"{file}: needs fields");
        if (t.Table != name) throw new InvalidDataException($"{file}: says it is table '{t.Table}', but the file name says '{name}'");
        if (string.IsNullOrWhiteSpace(t.Source)) throw new InvalidDataException($"{file}: needs a source for its names");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in t.Fields)
        {
            if (f.Name is null || !ColumnName().IsMatch(f.Name)) throw new InvalidDataException($"{file}: '{f.Name}' is not a usable field name");
            if (!Types.Contains(f.Type)) throw new InvalidDataException($"{file}: field {f.Name} has unknown type '{f.Type}'");
            if (!seen.Add(f.Name)) throw new InvalidDataException($"{file}: field {f.Name} is listed twice");
        }
        return t;
    }
}
