using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CoaMcp.Core;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace CoaMcp.Server.Tools;

/// <summary>One filter on a DBC field.</summary>
public sealed record DbcCondition(
    [property: Description("Field name, as dbc_tables lists it.")] string Field,
    [property: Description("One of = != < > <= >= contains. contains works on text fields.")] string Op,
    [property: Description("A number, or text for text fields.")] JsonElement Value);

/// <summary>
/// Reads the client's DBC tables. Filters never accept SQL: table and field names must match the layer's own lists, operators come from a fixed set, and values are bound.
/// A scan on an unindexed column shares the layer's search slots and stops at a time budget, so it can't hold up other reads.
/// </summary>
[McpServerToolType]
public sealed class DbcTools
{
    private const int DefaultTableLimit = 50;
    private const int DefaultRowLimit = 20;
    private const int MaxRowOffset = 100_000;
    private const int MaxConditions = 8;
    private const int MaxColumns = 100;
    private const int MaxNameChars = 100;
    private const int MaxValueChars = 100;
    private const int MaxOutputChars = 64 * 1024;

    private static readonly IReadOnlyDictionary<string, string> Ops = new Dictionary<string, string>
    {
        ["="] = "=", ["!="] = "!=", ["<"] = "<", [">"] = ">", ["<="] = "<=", [">="] = ">=",
    };
    private const string Contains = "contains";

    private readonly LayerSet _layers;
    private readonly TimeSpan _budget;

    public DbcTools(LayerSet layers, IConfiguration config)
    {
        _layers = layers;
        _budget = TimeSpan.FromMilliseconds(config.GetValue("Dbc:QueryBudgetMs", Limits.DbcQueryBudgetMs));
    }

    private LayerLease Lease() => _layers.Lease(DbcPages.LayerName) ?? throw new McpException("The dbc layer is not loaded on this server. See the status tool.");

    [McpServerTool(Name = "dbc_tables", ReadOnly = true), Description(
        "List the CoA client's DBC tables: rows, fields, how many fields have proven names, winning archive and its origin. filter: part of a table name. An exact name also lists its fields.")]
    public string DbcTables(
        [Description("Part of a table name, any case.")] string? filter = null,
        [Description("Tables to return, 1 to 100.")] long limit = DefaultTableLimit,
        [Description("Tables to skip, for paging.")] long offset = 0)
    {
        if (filter is { Length: > MaxNameChars }) throw new McpException($"filter is too long (max {MaxNameChars} characters).");
        var (lim, off, note) = Limits.Page(limit, offset);
        using var lease = Lease();
        var layer = lease.Layer;

        var text = string.IsNullOrWhiteSpace(filter) ? null : filter.Trim();
        var rows = text is null
            ? layer.Query("SELECT * FROM dbc_tables ORDER BY name LIMIT $0 OFFSET $1", (long)lim + 1, (long)off)
            : layer.Query("SELECT * FROM dbc_tables WHERE instr(lower(name), lower($2)) > 0 ORDER BY name LIMIT $0 OFFSET $1", (long)lim + 1, (long)off, text);

        var sb = new StringBuilder($"DBC tables (realm the CoA client (unproven), trust {Render.Trust(Trust.Client)}, source {ClientRelease.Source(DbcLayerBuilder.Source, layer.Meta.Client)}; layer {DbcPages.LayerName} v{layer.Meta.Version}, source date {layer.Meta.SourceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}).\n");
        if (note is not null) sb.Append(note).Append('\n');
        sb.Append($"{Math.Min(rows.Count, lim)} tables (offset {off}, limit {lim}). A project-built winner is not evidence about Ascension. A base table is not claimed as Ascension either.\n");
        var listed = rows.Take(lim).Select(DbcTableRef.From).ToList();
        foreach (var t in listed) sb.Append(TableLine(t)).Append('\n');
        foreach (var archive in listed.Where(t => t.Status == DbcStatus.Ok).Select(t => t.Archive).Distinct())
            if (layer.Meta.Client?.MismatchLine(archive) is { } mismatch) sb.Append(mismatch).Append('\n');
        if (rows.Count > lim) sb.Append($"More results may exist: use offset={off + lim}.\n");

        if (text is not null && DbcPages.Find(layer, text) is { Status: DbcStatus.Ok } exact)
        {
            sb.Append($"Fields of {exact.Name}");
            if (exact.NamesSource is not null) sb.Append($" (names from: {Render.OneLine(exact.NamesSource, 200)})");
            sb.Append(":\n").Append(string.Join(", ", DbcPages.Fields(layer, exact.Name).Select(f =>
                (f.Guessed ? $"{f.Name} {f.Type} (guessed)" : $"{f.Name} {f.Type}") + (f.Indexed ? " [indexed]" : "")))).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    private static string TableLine(DbcTableRef t)
    {
        var sb = new StringBuilder($"{t.Name} | {t.Rows.ToString("N0", CultureInfo.InvariantCulture)} rows | {t.Fields} fields, {t.Named} named | {Render.OneLine(t.Archive ?? "no archive", 60)} ({t.Origin ?? "unknown"})");
        if (t.BaseOf is not null) sb.Append($" | base copy of {t.BaseOf}");
        if (t.Status != DbcStatus.Ok) sb.Append(" | ").Append(t.Status);
        if (t.Problem is not null) sb.Append(" | ").Append(Render.OneLine(t.Problem, 160));
        return sb.ToString();
    }

    [McpServerTool(Name = "dbc_rows", ReadOnly = true), Description(
        "Read rows of a CoA client DBC table, in row order. where: up to 8 conditions, all must hold, as {field, op, value}, op one of = != < > <= >= contains. " +
        "A name ending in ? is guessed. Filter on an indexed field; a slow scan stops with a message. One row: get(\"dbc:Spell:133\").")]
    public string DbcRows(
        [Description("Table name from dbc_tables, e.g. Spell.")] string table,
        [Description("Conditions; all must hold.")] DbcCondition[]? where = null,
        [Description("Fields to show. Omit for all non-zero fields.")] string[]? columns = null,
        [Description("Rows to return, 1 to 100.")] long limit = DefaultRowLimit,
        [Description("Rows to skip, for paging.")] long offset = 0)
    {
        if (string.IsNullOrWhiteSpace(table)) throw new McpException("table is empty. List tables with dbc_tables.");
        if (table.Length > MaxNameChars) throw new McpException(DbcPages.UnknownTable(table));
        if (where is { Length: > MaxConditions }) throw new McpException($"Too many conditions: at most {MaxConditions}.");
        if (columns is { Length: > MaxColumns }) throw new McpException($"Too many columns: at most {MaxColumns}.");

        var (lim, off, note) = Limits.Page(limit, offset, MaxRowOffset);
        using var lease = Lease();
        var layer = lease.Layer;
        if (DbcPages.Find(layer, table.Trim()) is not { } t) throw new McpException(DbcPages.UnknownTable(table));
        if (DbcPages.NotReadable(t) is { } why) throw new McpException(why);

        var fields = DbcPages.Fields(layer, t.Name);
        var byName = fields.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

        var args = new List<object?>();
        var clauses = (where ?? Array.Empty<DbcCondition>()).Select(c => Clause(c, t, byName, args)).ToList();
        var shown = Shown(t, fields, byName, columns);

        var sql = new StringBuilder($"SELECT {string.Join(", ", shown.Select(f => f.Quoted))} FROM {t.DataTable}");
        if (clauses.Count > 0) sql.Append(" WHERE ").Append(string.Join(" AND ", clauses));
        args.Add((long)lim + 1);
        args.Add((long)off);
        sql.Append($" ORDER BY rowid LIMIT ${args.Count - 2} OFFSET ${args.Count - 1}");

        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows;
        try
        {
            rows = layer.Searching(() => layer.QueryWithin(_budget, sql.ToString(), args.ToArray()));
        }
        catch (QueryBudgetExceededException)
        {
            var indexed = fields.Where(f => f.Indexed).Select(f => f.Name).ToList();
            throw new McpException($"This query is too slow (stopped after {_budget.TotalSeconds:0.###} s): filter on an indexed field. " +
                (indexed.Count > 0 ? $"Indexed in {t.Name}: {string.Join(", ", indexed)}." : $"{t.Name} has no indexed field."));
        }

        var sb = new StringBuilder(DbcPages.Provenance(t, layer));
        if (note is not null) sb.Append(note).Append('\n');
        if (DbcPages.GuessedLine(shown) is { } guessed) sb.Append(guessed).Append('\n');
        sb.Append($"{Math.Min(rows.Count, lim)} rows of {t.Rows.ToString("N0", CultureInfo.InvariantCulture)} (offset {off}, limit {lim}), in row order.");
        sb.Append(columns is null ? " Zero and empty fields are left out.\n" : "\n");

        var written = 0;
        foreach (var r in rows.Take(lim))
        {
            var line = DbcPages.ListLine(shown, r, columns is not null);
            if (sb.Length + line.Length > MaxOutputChars)
            {
                sb.Append($"Output is capped: {written} rows shown. Ask for fewer rows or name the columns; use offset={off + written} to continue.\n");
                return sb.ToString().TrimEnd();
            }
            sb.Append(line).Append('\n');
            written++;
        }
        if (rows.Count > lim) sb.Append($"More results may exist: use offset={off + lim}.\n");
        return sb.ToString().TrimEnd();
    }

    /// <summary>The fields to select: all of them, or the ones asked for, with the id first and a maybe-string's text with its integer.</summary>
    private static List<DbcFieldRef> Shown(DbcTableRef t, IReadOnlyList<DbcFieldRef> fields, Dictionary<string, DbcFieldRef> byName, string[]? columns)
    {
        if (columns is null) return fields.ToList();
        var wanted = new List<DbcFieldRef>();
        void Add(DbcFieldRef f) { if (!wanted.Contains(f)) wanted.Add(f); }
        if (t.IdColumn is not null && byName.TryGetValue(t.IdColumn, out var id)) Add(id);
        foreach (var name in columns)
        {
            var f = Field(name, t, byName);
            Add(f);
            if (f.Type == DbcTypes.IntOrString && byName.TryGetValue(f.Name + DbcColumn.StringSuffix, out var s)) Add(s);
        }
        return wanted;
    }

    private static DbcFieldRef Field(string? name, DbcTableRef t, Dictionary<string, DbcFieldRef> byName) =>
        name is not null && name.Length <= MaxNameChars && byName.TryGetValue(name.Trim(), out var f)
            ? f
            : throw new McpException($"Unknown field '{Render.OneLine(name ?? "", 60)}' in {t.Name}. List its fields with dbc_tables(filter=\"{t.Name}\").");

    /// <summary>One condition as SQL. Only names from the layer's lists and operators from the fixed set are written into it; the value is bound.</summary>
    private static string Clause(DbcCondition c, DbcTableRef t, Dictionary<string, DbcFieldRef> byName, List<object?> args)
    {
        if (c is null) throw new McpException("A condition is empty.");
        var f = Field(c.Field, t, byName);
        if (c.Op != Contains && (c.Op is null || !Ops.ContainsKey(c.Op)))
            throw new McpException($"Unknown op '{Render.OneLine(c.Op ?? "", 20)}'. Valid op values: {string.Join(" ", Ops.Keys)} {Contains}.");
        if (c.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            throw new McpException($"The condition on {f.Name} needs a value.");

        var value = f.IsText ? TextValue(c.Value, f) : NumberValue(c, f, byName);
        args.Add(value);
        var mark = "$" + (args.Count - 1);
        return c.Op == Contains ? $"instr(lower({f.Quoted}), lower({mark})) > 0" : $"{f.Quoted} {Ops[c.Op!]} {mark}";
    }

    private static string TextValue(JsonElement value, DbcFieldRef f)
    {
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            _ => throw new McpException($"{f.Name} holds text, so its value must be text."),
        };
        return text.Length <= MaxValueChars ? text : throw new McpException($"The value for {f.Name} is too long (max {MaxValueChars} characters).");
    }

    private static object NumberValue(DbcCondition c, DbcFieldRef f, Dictionary<string, DbcFieldRef> byName)
    {
        if (c.Op == Contains)
        {
            var hint = byName.ContainsKey(f.Name + DbcColumn.StringSuffix) ? $" If it is a string offset, use {f.Name}{DbcColumn.StringSuffix}." : "";
            throw new McpException($"contains works on text fields, and {f.Name} holds numbers.{hint}");
        }
        var raw = c.Value.ValueKind switch
        {
            JsonValueKind.Number => c.Value.GetRawText(),
            JsonValueKind.String => c.Value.GetString() ?? "",
            _ => "",
        };
        if (raw.Length > MaxValueChars) throw new McpException($"The value for {f.Name} is too long (max {MaxValueChars} characters).");
        if (long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole)) return f.Type == DbcTypes.Float ? AsStoredFloat(whole) : whole;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var real) && double.IsFinite(real)) return f.Type == DbcTypes.Float ? AsStoredFloat(real) : real;
        throw new McpException($"{f.Name} holds numbers, so its value must be a number.");
    }

    /// <summary>Floats are stored as the client's single-precision value, so 1.1 must be compared as that value, not as the nearest double.</summary>
    private static double AsStoredFloat(double value) => (float)value;

    /// <summary>The status line for the dbc layer.</summary>
    public static string Summary(Layer layer)
    {
        var meta = layer.Query("SELECT key, value FROM dbc_meta").ToDictionary(r => (string)r["key"]!, r => (string)r["value"]!);
        string S(string key) => meta.TryGetValue(key, out var v) ? v : "?";
        return $"{S("tables")} tables, {S("rows")} rows ({S("named_tables")} tables with proven field names, the rest guessed; {S("base_tables")} base copies; {S("deleted")} removed by a delete marker; {S("unreadable")} unreadable)";
    }
}
