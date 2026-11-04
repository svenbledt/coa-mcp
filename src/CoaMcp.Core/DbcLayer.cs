using System.Globalization;
using System.Text;

namespace CoaMcp.Core;

/// <summary>
/// One column of a <c>t_&lt;table&gt;</c> data table. <paramref name="Guessed"/> is true for anything not from a proven name.
/// <paramref name="Index"/> is the field's position in the file's record; a maybe-string has two columns at one position (the integer, and <c>&lt;name&gt;__str</c>).
/// </summary>
public sealed record DbcColumn(int Index, string Name, string Type, bool Guessed, bool Indexed, bool PrimaryKey, string? Note)
{
    public bool IsText => Type == DbcTypes.String;
    public bool IsFloat => Type == DbcTypes.Float;

    /// <summary>The column that holds a maybe-string's resolved text.</summary>
    public const string StringSuffix = "__str";
}

public static class DbcTypes
{
    public const string Int = "int", UInt = "uint", Float = "float", String = "string", Byte = "byte", IntOrString = "int_or_string";
}

public static class DbcStatus
{
    public const string Ok = "ok", Unreadable = "unreadable", Deleted = "deleted", NotInDefaultClient = "not_in_default_client";
}

/// <summary>A row of <c>dbc_tables</c>. <paramref name="BaseOf"/> names the table this is the base copy of; <paramref name="Fields"/> is what the file's header says.</summary>
public sealed record DbcTableInfo(
    string Name, long Rows, int Fields, int Named, string? WinnerArchive, string? Origin, string? BaseOf,
    string Status, string? Problem, string? NamesSource, string? IdColumn, bool IdUnique, string SourceDate);

/// <summary>
/// Writes the dbc layer: one <c>t_&lt;name&gt;</c> table per exported DBC copy, <c>dbc_tables</c> and <c>dbc_fields</c> describing them, <c>dbc_meta</c> for run facts,
/// and one <c>dbc:&lt;name&gt;</c> entity per table so search finds tables by name and by field name.
/// </summary>
public sealed class DbcLayerBuilder
{
    public const string TablePrefix = "t_";
    public const string EntityType = "dbc_table";
    public const string Source = "CoA client DBC";
    public const string BaseSuffix = "__base";

    /// <summary>A table holding Area 52's version of a DBC. It is a labeled second version: never the default and never a winner.</summary>
    public const string Area52Suffix = "__area52";
    public const string Area52Realm = "area-52";
    public const string Area52Line = "Area 52 (realm id 11) is a separate realm from CoA (13); the client only loads this archive there.";
    public static readonly RealmSet Area52Realms = new(new[] { Area52Realm }, false);

    public static bool IsArea52(string table) => table.EndsWith(Area52Suffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The source of a table: the client's DBCs, or for an Area 52 table the archive it came from. A table won by an archive of origin ascension carries what that means,
    /// because a source is shown whole and a search snippet is cut.
    /// </summary>
    public static string SourceOf(string table, string? archive, string? origin, ClientRelease? client)
    {
        var source = ClientRelease.Source(IsArea52(table) ? $"Area 52 DBC from {archive}" : Source, client);
        return origin == ArchiveOrigin.Ascension ? $"{source}, from {archive} ({ArchiveOrigin.AscensionNote})" : source;
    }

    /// <summary>The line a default table's answer adds when Area 52 has its own version.</summary>
    public static string Area52VersionLine(string area52Table) => $"Area 52 has its own version of this table: {area52Table}.";

    private readonly LayerWriter _w;
    private readonly ClientRelease? _client;

    public DbcLayerBuilder(LayerWriter writer, ClientRelease? client = null)
    {
        _w = writer;
        _client = client;
        if (client is not null) _w.SetClient(client);
        _w.Execute("""
            CREATE TABLE dbc_tables(
              name TEXT PRIMARY KEY COLLATE NOCASE, rows INTEGER NOT NULL, fields INTEGER NOT NULL, named INTEGER NOT NULL,
              winner_archive TEXT, origin TEXT, base_of TEXT, status TEXT NOT NULL, problem TEXT, names_source TEXT,
              id_column TEXT, id_unique INTEGER NOT NULL, source_date TEXT NOT NULL);
            CREATE TABLE dbc_fields(
              "table" TEXT NOT NULL COLLATE NOCASE, idx INTEGER NOT NULL, name TEXT NOT NULL COLLATE NOCASE, type TEXT NOT NULL,
              guessed INTEGER NOT NULL, indexed INTEGER NOT NULL, note TEXT, PRIMARY KEY("table", name)) WITHOUT ROWID;
            CREATE TABLE dbc_meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """);
    }

    /// <summary>Creates the data table. Text compares without regard to case, so a Name lookup and its index agree.</summary>
    public BulkInsert BeginTable(string name, IReadOnlyList<DbcColumn> columns)
    {
        var defs = columns.Select(c => $"{Quote(c.Name)} {SqlType(c)}{(c.PrimaryKey ? " PRIMARY KEY" : "")}");
        _w.Execute($"CREATE TABLE {Quote(TablePrefix + name)}({string.Join(", ", defs)})");
        return _w.BeginInsert(TablePrefix + name, columns.Select(c => c.Name).ToList());
    }

    /// <summary>Records a table, exported or not. Call after its rows are in, so indexes are built last. <paramref name="area52Version"/> names Area 52's version of it, if there is one.</summary>
    public void AddTable(DbcTableInfo t, IReadOnlyList<DbcColumn> columns, RealmSet realm, string? area52Version = null)
    {
        foreach (var c in columns.Where(c => c.Indexed && !c.PrimaryKey))
            _w.Execute($"CREATE INDEX {Quote($"ix_{t.Name}_{c.Name}")} ON {Quote(TablePrefix + t.Name)}({Quote(c.Name)})");

        _w.Execute("INSERT INTO dbc_tables VALUES ($0,$1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12)",
            t.Name, t.Rows, (long)t.Fields, (long)t.Named, t.WinnerArchive, t.Origin, t.BaseOf, t.Status, t.Problem, t.NamesSource, t.IdColumn, t.IdUnique ? 1L : 0L, t.SourceDate);
        foreach (var c in columns)
            _w.Execute("INSERT INTO dbc_fields VALUES ($0,$1,$2,$3,$4,$5,$6)", t.Name, (long)c.Index, c.Name, c.Type, c.Guessed ? 1L : 0L, c.Indexed ? 1L : 0L, c.Note);

        _w.Add(new Entity("dbc:" + t.Name, EntityType, t.Name, realm, Trust.Client, SourceOf(t.Name, t.WinnerArchive, t.Origin, _client),
            DateOnly.ParseExact(t.SourceDate, "yyyy-MM-dd", CultureInfo.InvariantCulture), Body(t, columns, _client, area52Version), null, Array.Empty<Link>()));
    }

    /// <summary>Writes the run facts and finishes the layer.</summary>
    public void Finish(IReadOnlyDictionary<string, string> meta)
    {
        foreach (var (k, v) in meta) _w.Execute("INSERT INTO dbc_meta VALUES ($0,$1)", k, v);
        _w.Finish();
    }

    public static string OriginText(string? origin, string? archive) => origin switch
    {
        ArchiveOrigin.Project => $"{archive}, a project-built archive (project files are not evidence about Ascension)",
        ArchiveOrigin.Ascension => $"{archive} ({ArchiveOrigin.AscensionNote})",
        _ => $"{archive}, origin {origin ?? "unknown"} (not claimed as Ascension)",
    };

    private static string Body(DbcTableInfo t, IReadOnlyList<DbcColumn> columns, ClientRelease? client, string? area52Version)
    {
        var sb = new StringBuilder();
        var area52 = IsArea52(t.Name);
        sb.Append(t.BaseOf is null ? $"DBC table {t.Name}" : $"Base copy of DBC table {t.BaseOf}, not the copy the client reads");
        sb.Append($": {t.Status}, {t.Rows.ToString("N0", CultureInfo.InvariantCulture)} rows, {t.Fields} fields, {t.Named} named.\n");
        if (t.WinnerArchive is not null) sb.Append($"{(t.BaseOf is null && !area52 ? "Winning copy" : "From")}: {OriginText(t.Origin, t.WinnerArchive)}.\n");
        if (area52) sb.Append(Area52Line).Append('\n');
        if (area52Version is not null) sb.Append(Area52VersionLine(area52Version)).Append('\n');
        if (t.Status == DbcStatus.Ok && client?.MismatchLine(t.WinnerArchive) is { } mismatch) sb.Append(mismatch).Append('\n');
        if (t.NamesSource is not null) sb.Append($"Field names from: {t.NamesSource}.\n");
        if (t.Problem is not null) sb.Append($"Note: {t.Problem}\n");
        if (columns.Count > 0)
        {
            sb.Append("Fields: ").Append(string.Join(", ", columns.Select(c => c.Guessed ? $"{c.Name} (guessed {c.Type})" : $"{c.Name} {c.Type}"))).Append(".\n");
            sb.Append($"Read rows with dbc_rows(table=\"{t.Name}\") or get(\"dbc:{t.Name}:<id>\").\n");
        }
        return sb.ToString();
    }

    private static string SqlType(DbcColumn c) => c.Type switch
    {
        DbcTypes.String => "TEXT COLLATE NOCASE",
        DbcTypes.Float => "REAL",
        _ => "INTEGER",
    };

    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
}
