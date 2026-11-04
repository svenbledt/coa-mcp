using System.Globalization;
using System.Text;
using CoaMcp.Core;

namespace CoaMcp.Server;

/// <summary>A row of dbc_tables, as the server reads it.</summary>
public sealed record DbcTableRef(string Name, long Rows, int Fields, int Named, string? Archive, string? Origin, string? BaseOf, string Status, string? Problem, string? NamesSource, string? IdColumn, DateOnly SourceDate)
{
    public string DataTable => "\"" + DbcLayerBuilder.TablePrefix + Name.Replace("\"", "\"\"") + "\"";

    public static DbcTableRef From(IReadOnlyDictionary<string, object?> r) => new(
        (string)r["name"]!, (long)r["rows"]!, (int)(long)r["fields"]!, (int)(long)r["named"]!, r["winner_archive"] as string, r["origin"] as string, r["base_of"] as string,
        (string)r["status"]!, r["problem"] as string, r["names_source"] as string, r["id_column"] as string,
        DateOnly.ParseExact((string)r["source_date"]!, "yyyy-MM-dd", CultureInfo.InvariantCulture));
}

/// <summary>A row of dbc_fields.</summary>
public sealed record DbcFieldRef(int Index, string Name, string Type, bool Guessed, bool Indexed, string? Note)
{
    public string Quoted => "\"" + Name.Replace("\"", "\"\"") + "\"";
    public bool IsText => Type == DbcTypes.String;
    public bool IsMaybeStringText => IsText && Guessed && Name.EndsWith(DbcColumn.StringSuffix, StringComparison.Ordinal);
    public string Shown => Guessed ? Name + "?" : Name;
}

/// <summary>Reads the layer's own lists of tables and fields, and renders DBC rows. Table and field names reach SQL only from these lists, never from a caller.</summary>
public static class DbcPages
{
    public const string LayerName = "dbc";
    private const int MaxTextChars = 2000;
    private const int MaxListTextChars = 200;
    private const int MaxRowsPerId = 5;
    private static readonly RealmSet Realm = new(new[] { "coa-client" }, false);

    public static DbcTableRef? Find(Layer layer, string table) =>
        layer.Query("SELECT * FROM dbc_tables WHERE name = $0", table) is [var r] ? DbcTableRef.From(r) : null;

    public static IReadOnlyList<DbcFieldRef> Fields(Layer layer, string table) =>
        layer.Query("SELECT idx, name, type, guessed, indexed, note FROM dbc_fields WHERE \"table\" = $0 ORDER BY idx, name", table)
            .Select(f => new DbcFieldRef((int)(long)f["idx"]!, (string)f["name"]!, (string)f["type"]!, (long)f["guessed"]! != 0, (long)f["indexed"]! != 0, f["note"] as string)).ToList();

    public static string UnknownTable(string table) => $"Unknown DBC table '{Render.OneLine(table, 80)}'. List them with dbc_tables.";

    /// <summary>Why a listed table has no rows to read, or null when it has.</summary>
    public static string? NotReadable(DbcTableRef t) => t.Status == DbcStatus.Ok ? null : $"DBC table {t.Name} has no rows here ({t.Status}): {Render.OneLine(t.Problem ?? "no reason recorded", 300)}";

    /// <summary>Realm, trust, source and date, then which copy this is and what it is worth as evidence.</summary>
    public static string Provenance(DbcTableRef t, Layer layer)
    {
        var sb = new StringBuilder($"DBC {t.Name} (realm {Render.RealmText(Realm)}, trust {Render.Trust(Trust.Client)}, source {ClientRelease.Source(DbcLayerBuilder.Source, layer.Meta.Client)}, {Render.Dated(layer.Meta, t.SourceDate)}; layer {LayerName} v{layer.Meta.Version}).\n");
        sb.Append(CopyLine(t)).Append('\n');
        if (layer.Meta.Client?.MismatchLine(t.Archive) is { } mismatch) sb.Append(mismatch).Append('\n');
        if (BaseOf(layer, t) is { } b) sb.Append($"Base copy: DBC table {b.Name}.\n");
        if (t.Problem is not null) sb.Append("Note: ").Append(Render.OneLine(t.Problem, 400)).Append('\n');
        return sb.ToString();
    }

    private static string CopyLine(DbcTableRef t) => (t.BaseOf is null ? "Winning copy: " : $"Base copy of {t.BaseOf}, which the client does not read: ") + DbcLayerBuilder.OriginText(t.Origin, t.Archive) + ".";

    private static DbcTableRef? BaseOf(Layer layer, DbcTableRef t) =>
        t.BaseOf is null && layer.Query("SELECT * FROM dbc_tables WHERE base_of = $0", t.Name) is [var r] ? DbcTableRef.From(r) : null;


    // ---- values ----

    private static string Value(object? v, int maxText) => v switch
    {
        null => "null",
        string s => "\"" + Render.OneLine(s, maxText) + "\"",
        double d => ((float)d).ToString("R", CultureInfo.InvariantCulture),
        IFormattable n => n.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "null",
    };

    /// <summary>Whether a field has nothing to show. A maybe-string of 0 can still have text: offset 0 is a real string when the block has no leading NUL.</summary>
    private static bool IsNothing(DbcFieldRef f, IReadOnlyDictionary<string, object?> row) =>
        row[f.Name] is null or 0L or 0.0 or ""
        && !(f.Type == DbcTypes.IntOrString && row.TryGetValue(f.Name + DbcColumn.StringSuffix, out var s) && s is string { Length: > 0 });

    /// <summary>A value, joined with its maybe-string text when the row has that too, so an integer and what it might mean are read side by side.</summary>
    private static string Shown(DbcFieldRef f, IReadOnlyDictionary<string, object?> row, int maxText)
    {
        var text = Value(row[f.Name], maxText);
        return f.Type == DbcTypes.IntOrString && row.TryGetValue(f.Name + DbcColumn.StringSuffix, out var s) && s is string { Length: > 0 }
            ? text + " " + Value(s, maxText)
            : text;
    }

    /// <summary>One line of a row list. Zeros and empty values are left out unless the caller named the columns.</summary>
    public static string ListLine(IReadOnlyList<DbcFieldRef> fields, IReadOnlyDictionary<string, object?> row, bool showAll)
    {
        var parts = new List<string>();
        foreach (var f in fields)
        {
            if (!row.ContainsKey(f.Name)) continue;
            if (f.IsMaybeStringText && row.ContainsKey(f.Name[..^DbcColumn.StringSuffix.Length])) continue;   // shown with its integer
            if (!showAll && IsNothing(f, row) && parts.Count > 0) continue;
            parts.Add($"{f.Shown}={Shown(f, row, MaxListTextChars)}");
        }
        return "- " + string.Join(" | ", parts);
    }

    /// <summary>The line that explains the '?' marks, or null when no shown field is guessed.</summary>
    public static string? GuessedLine(IEnumerable<DbcFieldRef> shown)
    {
        const int MaxListed = 40;
        var guessed = shown.Where(f => f.Guessed).ToList();
        if (guessed.Count == 0) return null;
        var list = string.Join(", ", guessed.Take(MaxListed).Select(f => $"{f.Name} {f.Type}"));
        return $"A name ending in ? is guessed: its name and type are inferred, not proven. Guessed types: {list}{(guessed.Count > MaxListed ? $", and {guessed.Count - MaxListed} more" : "")}.";
    }

    // ---- get("dbc:Table:id") ----

    public static bool IsRowId(string id) => id.StartsWith("dbc:", StringComparison.Ordinal) && id.Count(c => c == ':') == 2;

    /// <summary>The canonical id of a DBC row, and the values of its first row, or null when there is no such row here.</summary>
    public static (string Id, IReadOnlyList<GeneratedValue> Values)? FindRow(LayerSet layers, string id)
    {
        var parts = id.Split(':');
        using var lease = layers.Lease(LayerName);
        if (lease is null || parts.Length != 3 || Find(lease.Layer, parts[1]) is not { } t || NotReadable(t) is not null || t.IdColumn is null) return null;
        if (!long.TryParse(parts[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var rowId)) return null;
        var idField = Fields(lease.Layer, t.Name).First(f => f.Name.Equals(t.IdColumn, StringComparison.OrdinalIgnoreCase));
        var rows = lease.Layer.Query($"SELECT * FROM {t.DataTable} WHERE {idField.Quoted} = $0 ORDER BY rowid LIMIT 1", rowId);
        if (rows.Count == 0) return null;
        var provenance = $"trust {Render.Trust(Trust.Client)}, realm {Render.RealmText(Realm)}, source {ClientRelease.Source(DbcLayerBuilder.Source, lease.Layer.Meta.Client)}, {Render.Dated(lease.Layer.Meta, t.SourceDate)}, layer {LayerName} v{lease.Layer.Meta.Version}";
        var mark = CopyLine(t) + (lease.Layer.Meta.Client?.MismatchLine(t.Archive) is { } mismatch ? " " + mismatch : "");
        return ($"dbc:{t.Name}:{rowId.ToString(CultureInfo.InvariantCulture)}", GeneratedValue.FromRow(rows[0], provenance, mark));
    }

    /// <summary>A stored value as plain text, for comparing with a written value: floats read the way they are shown.</summary>
    public static string? PlainValue(object? v) => v switch
    {
        null => null,
        string s => s,
        double d => ((float)d).ToString("R", CultureInfo.InvariantCulture),
        IFormattable n => n.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString(),
    };

    /// <summary>The page for a DBC row, or a plain explanation. Called with a shape <see cref="IsRowId"/> accepted.</summary>
    public static string Row(LayerSet layers, string id)
    {
        var parts = id.Split(':');
        var (table, key) = (parts[1], parts[2]);
        using var lease = layers.Lease(LayerName);
        if (lease is null) return "The dbc layer is not loaded on this server. See the status tool.";
        var layer = lease.Layer;

        if (Find(layer, table) is not { } t) return UnknownTable(table);
        if (NotReadable(t) is { } why) return why;
        if (t.IdColumn is null) return $"DBC table {t.Name} has no id column. Use dbc_rows to filter it.";
        if (!long.TryParse(key, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var rowId))
            return $"The row id '{Render.OneLine(key, 40)}' must be a whole number, as in dbc:{t.Name}:133.";

        var fields = Fields(layer, t.Name);
        var idField = fields.First(f => f.Name.Equals(t.IdColumn, StringComparison.OrdinalIgnoreCase));
        var rows = layer.Query($"SELECT * FROM {t.DataTable} WHERE {idField.Quoted} = $0 ORDER BY rowid LIMIT {MaxRowsPerId + 1}", rowId);
        if (rows.Count == 0) return $"No row with {idField.Name} = {rowId} in DBC table {t.Name} ({t.Rows.ToString("N0", CultureInfo.InvariantCulture)} rows). Use dbc_rows to look around.";

        var page = new Entity($"dbc:{t.Name}:{rowId}", "dbc_row", $"{t.Name} row {rowId}", Realm, Trust.Client, ClientRelease.Source(DbcLayerBuilder.Source, layer.Meta.Client), t.SourceDate, "", null, Array.Empty<Link>());
        var sb = new StringBuilder(Render.Header(page, layer.Meta)).Append("\n\n");
        sb.Append(CopyLine(t)).Append('\n');
        if (layer.Meta.Client?.MismatchLine(t.Archive) is { } mismatch) sb.Append(mismatch).Append('\n');
        if (t.NamesSource is not null) sb.Append("Field names from: ").Append(Render.OneLine(t.NamesSource, 200)).Append('\n');
        if (t.Problem is not null) sb.Append("Note: ").Append(Render.OneLine(t.Problem, 400)).Append('\n');
        if (idField.Guessed) sb.Append($"{idField.Name} is taken to be the id because it is the first column; that is itself a guess.\n");

        if (rows.Count > 1)
            sb.Append($"{(rows.Count > MaxRowsPerId ? $"More than {MaxRowsPerId}" : rows.Count.ToString(CultureInfo.InvariantCulture))} rows have {idField.Name} = {rowId}: this column isn't unique.\n");

        foreach (var row in rows.Take(MaxRowsPerId))
        {
            sb.Append('\n');
            AppendFields(sb, fields, row);
        }

        if (t.BaseOf is null) AppendBase(sb, layer, t, idField, rowId, fields, rows[0]);
        return sb.ToString().TrimEnd();
    }

    private static void AppendFields(StringBuilder sb, IReadOnlyList<DbcFieldRef> fields, IReadOnlyDictionary<string, object?> row)
    {
        var omitted = 0;
        foreach (var f in fields)
        {
            if (f.IsMaybeStringText) continue;   // shown with its integer
            if (IsNothing(f, row)) { omitted++; continue; }
            sb.Append(f.Guessed ? $"{f.Name} (guessed {f.Type})" : f.Name).Append(": ").Append(Shown(f, row, MaxTextChars)).Append('\n');
        }
        if (omitted > 0) sb.Append($"{omitted} zero or empty fields omitted; name them in dbc_rows(columns) to see them.\n");
    }

    /// <summary>The next non-project copy of a project-built row: what differs, side by side, never merged.</summary>
    private static void AppendBase(StringBuilder sb, Layer layer, DbcTableRef t, DbcFieldRef idField, long rowId, IReadOnlyList<DbcFieldRef> fields, IReadOnlyDictionary<string, object?> row)
    {
        if (BaseOf(layer, t) is not { } b) return;
        sb.Append($"\nBase copy {b.Name}: {DbcLayerBuilder.OriginText(b.Origin, b.Archive)}.\n");
        if (layer.Meta.Client?.MismatchLine(b.Archive) is { } mismatch) sb.Append(mismatch).Append('\n');
        if (NotReadable(b) is { } why) { sb.Append(why).Append('\n'); return; }

        var baseFields = Fields(layer, b.Name);
        var baseId = baseFields.FirstOrDefault(f => f.Name.Equals(idField.Name, StringComparison.OrdinalIgnoreCase));
        if (baseId is null) { sb.Append("Its layout differs, so rows can't be matched by id; read it with dbc_rows.\n"); return; }
        var baseRows = layer.Query($"SELECT * FROM {b.DataTable} WHERE {baseId.Quoted} = $0 ORDER BY rowid LIMIT 1", rowId);
        if (baseRows.Count == 0) { sb.Append("It has no row with this id, so this row exists only in the project-built copy.\n"); return; }

        var differs = new List<string>();
        foreach (var f in baseFields)
        {
            if (f.IsMaybeStringText) continue;
            if (row.TryGetValue(f.Name, out var mine) && Equals(mine, baseRows[0][f.Name])) continue;
            differs.Add((f.Guessed ? $"{f.Name} (guessed {f.Type})" : f.Name) + ": " + Shown(f, baseRows[0], MaxTextChars));
        }
        if (differs.Count == 0) sb.Append("Its row is identical.\n");
        else sb.Append("Its values where they differ:\n").Append(string.Join("\n", differs)).Append('\n');
    }
}
