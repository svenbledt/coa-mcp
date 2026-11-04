using System.Globalization;
using System.Text.RegularExpressions;
using CoaMcp.Core;
using CoaMcp.Pipeline.Mpq;
using Microsoft.Data.Sqlite;

namespace CoaMcp.Pipeline.Dbc;

public sealed record DbcExportOptions(string ManifestPath, ArchiveConfig Config, string FieldsDir, string OutPath, string WinnersDir, string? Version = null, Action<string>? Progress = null, ClientRelease? Client = null);

public sealed record DbcExportResult(int Tables, long Rows, int BaseTables, int NamedTables, IReadOnlyList<string> Deleted, IReadOnlyList<string> Unreadable, IReadOnlyList<string> Problems);

/// <summary>
/// Builds the dbc layer from a finished restore: the winning copy of every table under the default client's rules, parsed into rows and resolved strings.
/// Raw bytes never enter the layer. They go only to the private winners folder, which the Spellwork export and the item check read.
/// </summary>
public static partial class DbcExporter
{
    private const string Folder = "dbfilesclient/";

    /// <summary>Above this a table is refused: with maybe-string twins and the guard's two result columns per column, SQLite's column limits come close.</summary>
    public const int MaxFields = 400;

    private static readonly RealmSet ClientRealm = new(new[] { "coa-client" }, false);

    [GeneratedRegex("^[A-Za-z0-9_]{1,80}$")]
    private static partial Regex UsableName();

    private sealed record Source(string Name, string? BaseOf, HeldCopy Held, string File, string Date);

    public static DbcExportResult Run(DbcExportOptions o)
    {
        var version = LayerVersion.Resolve(o.Version);
        var byArchive = o.Config.Archives.ToDictionary(a => a.Path, StringComparer.OrdinalIgnoreCase);
        var fieldConfig = DbcFieldConfig.LoadAll(o.FieldsDir);
        RefuseInsideRepository(o.WinnersDir);

        List<ClientCopies> tables;
        Dictionary<string, DateOnly> dates;
        using (var db = ClientWinners.OpenManifest(o.ManifestPath))
        {
            ClientWinners.CheckStatuses(db);
            ClientWinners.CheckArchives(db, byArchive);
            dates = ClientWinners.ArchiveDates(db);
            tables = ReadTables(db, byArchive);
        }
        if (tables.Count == 0) throw new InvalidDataException($"{o.ManifestPath} lists no tables under DBFilesClient/, so there is nothing to export.");

        var root = Path.GetDirectoryName(Path.GetFullPath(o.ManifestPath))!;
        var run = new ExportRun(fieldConfig, o.Progress);
        var winners = new Dictionary<string, (string File, DbcWinnerFile Info)>(StringComparer.OrdinalIgnoreCase);
        var writer = new LayerWriter(o.OutPath, "dbc", version);
        try
        {
            var builder = new DbcLayerBuilder(writer, o.Client);
            foreach (var copies in tables)
            {
                var name = TableName(copies.Holders[0].Copy.Path);
                if (copies.Winner is not { } win)
                {
                    run.NotExported(builder, name, copies.Holders[0], DbcStatus.NotInDefaultClient, "Only realm-specific archives hold this table, so the default client does not read it.",
                        DateOf(dates, copies.Holders[0].Copy.Archive));
                    continue;
                }
                var date = DateOf(dates, win.Copy.Archive);
                if (win.IsDeleteMarker)
                {
                    run.NotExported(builder, name, win, DbcStatus.Deleted, $"A delete marker in {win.Copy.Archive} is on top, so the client does not see this table.", date);
                    run.Deleted.Add(name);
                    continue;
                }

                var file = RestoredFile(root, win);
                winners[name + ".dbc"] = (file, new DbcWinnerFile(win.Copy.Archive, win.Archive.Origin, date));

                string? baseNote = null;
                Source? baseSource = null;
                if (win.IsProject)
                {
                    if (copies.NextNonProject is not { } next)
                        baseNote = "The winner is project-built and no copy from another archive exists, so there is no base table.";
                    else if (next.IsDeleteMarker)
                        baseNote = $"The winner is project-built, and the next copy is a delete marker in {next.Copy.Archive}, so there is no base table.";
                    else
                        baseSource = new Source(name + DbcLayerBuilder.BaseSuffix, name, next, RestoredFile(root, next), DateOf(dates, next.Copy.Archive));
                }

                run.Export(builder, new Source(name, null, win, file, date), baseNote);
                if (baseSource is not null) run.Export(builder, baseSource, null);
            }

            builder.Finish(new Dictionary<string, string>
            {
                ["tables"] = tables.Count.ToString(CultureInfo.InvariantCulture),
                ["rows"] = run.Rows.ToString(CultureInfo.InvariantCulture),
                ["named_tables"] = run.NamedTables.ToString(CultureInfo.InvariantCulture),
                ["base_tables"] = run.BaseTables.ToString(CultureInfo.InvariantCulture),
                ["deleted"] = run.Deleted.Count.ToString(CultureInfo.InvariantCulture),
                ["unreadable"] = run.Unreadable.Count.ToString(CultureInfo.InvariantCulture),
                ["source_date_basis"] = "each table's date is its copy's archive modified time; the layer's date is the newest",
            });
        }
        catch
        {
            // A half-built layer must not be mistaken for a finished one.
            writer.Dispose();
            SqliteConnection.ClearAllPools();
            try { File.Delete(o.OutPath); } catch (IOException) { /* the original error matters more */ }
            throw;
        }
        writer.Dispose();
        SqliteConnection.ClearAllPools();

        Materialize(o.WinnersDir, winners, o.Client);
        return new DbcExportResult(tables.Count, run.Rows, run.BaseTables, run.NamedTables, run.Deleted, run.Unreadable, run.Problems);
    }

    private static List<ClientCopies> ReadTables(SqliteConnection db, Dictionary<string, ArchiveEntry> config)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"""
            SELECT path_lc, path, archive, size, status, locale FROM files
            WHERE path_lc LIKE '{Folder}%.dbc' AND path_lc NOT LIKE '{Folder}%/%' AND name IS NOT NULL
            ORDER BY path_lc, locale
            """;
        using var rd = cmd.ExecuteReader();
        var result = new List<ClientCopies>();
        var group = new List<ManifestCopy>();
        string? current = null;
        while (rd.Read())
        {
            var key = rd.GetString(0);
            if (current is not null && key != current)
            {
                result.Add(ClientWinners.Resolve(group, config));
                group = new List<ManifestCopy>();
            }
            current = key;
            group.Add(new ManifestCopy(rd.GetString(2), rd.GetString(1).Replace('\\', '/'), rd.GetInt64(3), rd.GetString(4), (ushort)rd.GetInt32(5)));
        }
        if (group.Count > 0) result.Add(ClientWinners.Resolve(group, config));
        return result;
    }

    private static string TableName(string path) => Path.GetFileNameWithoutExtension(path);

    private static string RestoredFile(string root, HeldCopy h) => SafePath.Under(Path.Combine(root, h.Copy.Archive), h.Copy.Path);

    private static string DateOf(Dictionary<string, DateOnly> dates, string archive) =>
        dates.TryGetValue(archive, out var d)
            ? ClientWinners.Text(d)
            : throw new InvalidDataException($"the manifest records no modified time for archive '{archive}', so its tables have no source date; re-run the restore.");

    /// <summary>The folder holds raw client files. A .gitignore rule is not enough of a promise, so it may not live in a repository at all.</summary>
    private static void RefuseInsideRepository(string dir)
    {
        for (var d = new DirectoryInfo(Path.GetFullPath(dir)); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git")))
                throw new InvalidDataException($"The winners folder '{dir}' is inside the repository at {d.FullName}. Raw client files may never be there; choose a folder outside it.");
    }

    /// <summary>Writes the winning files and their list, and removes files left from an earlier run.</summary>
    private static void Materialize(string dir, Dictionary<string, (string File, DbcWinnerFile Info)> winners, ClientRelease? client)
    {
        Directory.CreateDirectory(dir);
        foreach (var stale in Directory.GetFiles(dir, "*.dbc"))
            if (!winners.ContainsKey(Path.GetFileName(stale))) File.Delete(stale);
        foreach (var (name, (file, _)) in winners)
            File.Copy(file, Path.Combine(dir, name), overwrite: true);
        DbcWinnerList.Write(dir, winners.ToDictionary(w => w.Key, w => w.Value.Info), client);
    }

    /// <summary>What one export keeps while it walks the tables.</summary>
    private sealed class ExportRun(IReadOnlyDictionary<string, DbcTableConfig> config, Action<string>? progress)
    {
        public long Rows;
        public int BaseTables, NamedTables;
        public readonly List<string> Deleted = new(), Unreadable = new(), Problems = new();

        public void NotExported(DbcLayerBuilder b, string name, HeldCopy h, string status, string problem, string date)
        {
            Problems.Add($"{name}: {problem}");
            b.AddTable(new DbcTableInfo(name, 0, 0, 0, h.Copy.Archive, h.Archive.Origin, null, status, problem, null, null, false, date), Array.Empty<DbcColumn>(), ClientRealm);
        }

        public void Export(DbcLayerBuilder b, Source s, string? extraNote)
        {
            try
            {
                if (s.Held.Copy.Size != new FileInfo(s.File).Length)
                    throw new DbcFormatException($"the restored file is {new FileInfo(s.File).Length} bytes, but the manifest says {s.Held.Copy.Size}.");
                using var reader = DbcReader.Open(s.File);
                if (!UsableName().IsMatch(s.Name)) throw new DbcFormatException("the file's name cannot be a table name: only letters, digits and underscores are.");
                if (reader.FieldCount == 0) throw new DbcFormatException("the header says the table has no fields.");
                if (reader.FieldCount > MaxFields || reader.RecordSize > MaxFields * 4) throw new DbcFormatException($"the table has more than {MaxFields} fields, which this layer does not hold.");
                Table(b, s, reader, extraNote);
            }
            catch (Exception ex) when (ex is DbcFormatException or FileNotFoundException or DirectoryNotFoundException)
            {
                var why = "Unreadable: " + ex.Message + (extraNote is null ? "" : " " + extraNote);
                Unreadable.Add(s.Name);
                Problems.Add($"{s.Name}: {why}");
                b.AddTable(new DbcTableInfo(s.Name, 0, 0, 0, s.Held.Copy.Archive, s.Held.Archive.Origin, s.BaseOf, DbcStatus.Unreadable, why, null, null, false, s.Date), Array.Empty<DbcColumn>(), ClientRealm);
            }
        }

        private void Table(DbcLayerBuilder b, Source s, DbcReader reader, string? extraNote)
        {
            config.TryGetValue(s.BaseOf ?? s.Name, out var tableConfig);
            var layout = DbcLayouts.For(s.Name, reader, tableConfig);
            var scan = Scan(reader, layout);

            // A config's string column must hold real string offsets. If it doesn't, the layout is wrong, whatever its counts say.
            var bad = layout.Slots.Where(slot => slot.Cell == DbcCell.String && !scan.Stats[slot.Name].AllValidStrings).Select(slot => slot.Name).ToList();
            if (bad.Count > 0)
            {
                layout = DbcLayouts.Positions(reader, $"The field config for {s.Name} calls {string.Join(", ", bad.Take(5))} strings, but they hold offsets outside the string table, so its names are not used.");
                scan = Scan(reader, layout);
            }

            var problem = string.Join(" ", new[] { layout.Problem, extraNote }.Where(p => p is not null));
            if (problem.Length > 0) Problems.Add($"{s.Name}: {problem}");

            var columns = Columns(layout, scan);
            using (var insert = b.BeginTable(s.Name, columns.Select(c => c.Column).ToList()))
            {
                var values = new object?[columns.Count];
                foreach (var record in reader.Records())
                {
                    for (var i = 0; i < columns.Count; i++) values[i] = columns[i].Read(record, reader);
                    insert.Add(values);
                }
            }

            var id = columns[0].Column;
            b.AddTable(new DbcTableInfo(s.Name, reader.RecordCount, reader.FieldCount, layout.Named, s.Held.Copy.Archive, s.Held.Archive.Origin, s.BaseOf, DbcStatus.Ok,
                problem.Length > 0 ? problem : null, layout.NamesSource, id.Type == DbcTypes.String || id.IsFloat ? null : id.Name, scan.FirstColumnUnique, s.Date),
                columns.Select(c => c.Column).ToList(), ClientRealm);

            Rows += reader.RecordCount;
            if (s.BaseOf is not null) BaseTables++;
            if (layout.Named > 0 && s.BaseOf is null) NamedTables++;
            progress?.Invoke($"{s.Name}: {reader.RecordCount:N0} rows, {columns.Count} columns, {layout.Named} named");
        }

        private sealed record ScanResult(Dictionary<string, ColumnStats> Stats, bool FirstColumnUnique);

        /// <summary>One pass over the file: what each four-byte column looks like, and whether the first column could be a key.</summary>
        private static ScanResult Scan(DbcReader reader, DbcLayout layout)
        {
            var watched = layout.Slots.Where(s => s.Cell is DbcCell.Guess32 or DbcCell.String).ToList();
            var stats = watched.ToDictionary(s => s.Name, _ => new ColumnStats(reader.StringBlock));
            var array = watched.Select(s => stats[s.Name]).ToArray();
            var offsets = watched.Select(s => s.Offset).ToArray();
            var first = layout.Slots[0];
            var firstIsFour = first.Cell is not (DbcCell.Byte or DbcCell.GuessByte);
            var seen = new HashSet<uint>();
            var unique = true;
            foreach (var record in reader.Records())
            {
                for (var i = 0; i < array.Length; i++) array[i].Add(BitConverter.ToUInt32(record, offsets[i]));
                if (unique)
                    unique = seen.Add(firstIsFour ? BitConverter.ToUInt32(record, first.Offset) : record[first.Offset]);
            }
            return new ScanResult(stats, unique);
        }

        private sealed record OutColumn(DbcColumn Column, Func<byte[], DbcReader, object?> Read);

        /// <summary>
        /// The layer's columns for a layout. A column with a proven name has the config's type and isn't guessed. Otherwise the type is a guess and says so.
        /// A maybe-string keeps its integer in its own column and adds the resolved text beside it, so neither reading hides the other.
        /// The first column is a table's id when it can be one: unique integers become the primary key, others get an index. It's never guessed to be text or a float.
        /// </summary>
        private static List<OutColumn> Columns(DbcLayout layout, ScanResult scan)
        {
            var result = new List<OutColumn>();
            for (var i = 0; i < layout.Slots.Count; i++)
            {
                var slot = layout.Slots[i];
                var offset = slot.Offset;
                var isFirst = i == 0;
                var firstKey = isFirst && slot.Cell is not (DbcCell.String or DbcCell.Float);
                var primary = firstKey && scan.FirstColumnUnique;
                var type = DbcLayouts.Type(slot.Cell);

                switch (slot.Cell)
                {
                    case DbcCell.Int32:
                        result.Add(new OutColumn(new DbcColumn(i, slot.Name, type, !slot.Proven, firstKey, primary, null), (r, _) => (long)BitConverter.ToInt32(r, offset)));
                        break;
                    case DbcCell.UInt32:
                        result.Add(new OutColumn(new DbcColumn(i, slot.Name, type, !slot.Proven, firstKey, primary, null), (r, _) => (long)BitConverter.ToUInt32(r, offset)));
                        break;
                    case DbcCell.Float:
                        result.Add(new OutColumn(new DbcColumn(i, slot.Name, type, !slot.Proven, false, false, null), (r, _) => (double)BitConverter.ToSingle(r, offset)));
                        break;
                    case DbcCell.String:
                        result.Add(new OutColumn(new DbcColumn(i, slot.Name, type, !slot.Proven, IsIndexedName(slot.Name), false, null), (r, f) => f.GetString(BitConverter.ToUInt32(r, offset))));
                        break;
                    case DbcCell.Byte:
                    case DbcCell.GuessByte:
                        var byteNote = slot.Proven ? null : "single byte";
                        result.Add(new OutColumn(new DbcColumn(i, slot.Name, type, !slot.Proven, firstKey, primary, byteNote), (r, _) => (long)r[offset]));
                        break;
                    default:
                        AddGuessed(result, i, slot, scan.Stats[slot.Name].Result(), firstKey, primary);
                        break;
                }
            }
            return result;
        }

        private static void AddGuessed(List<OutColumn> result, int i, DbcSlot slot, FieldGuessResult guess, bool firstKey, bool primary)
        {
            var offset = slot.Offset;
            if (firstKey)
            {
                result.Add(new OutColumn(new DbcColumn(i, slot.Name, DbcTypes.Int, true, true, primary, "first column, taken to be the id"), (r, _) => (long)BitConverter.ToInt32(r, offset)));
                return;
            }
            switch (guess.Kind)
            {
                case GuessKind.Float:
                    result.Add(new OutColumn(new DbcColumn(i, slot.Name, DbcTypes.Float, true, false, false, guess.Note), (r, _) => (double)BitConverter.ToSingle(r, offset)));
                    break;
                case GuessKind.MaybeString:
                    result.Add(new OutColumn(new DbcColumn(i, slot.Name, DbcTypes.IntOrString, true, false, false, guess.Note), (r, _) => (long)BitConverter.ToInt32(r, offset)));
                    result.Add(new OutColumn(new DbcColumn(i, slot.Name + DbcColumn.StringSuffix, DbcTypes.String, true, false, false, guess.Note), (r, f) => f.GetString(BitConverter.ToUInt32(r, offset))));
                    break;
                default:
                    result.Add(new OutColumn(new DbcColumn(i, slot.Name, DbcTypes.Int, true, false, false, guess.Note), (r, _) => (long)BitConverter.ToInt32(r, offset)));
                    break;
            }
        }

        /// <summary>A Name field gets an index on its first locale slot only: the others are empty in an enUS client.</summary>
        private static bool IsIndexedName(string name)
        {
            if (!name.Contains("name", StringComparison.OrdinalIgnoreCase)) return false;
            var slot = name.LastIndexOf('_');
            return slot < 0 || !int.TryParse(name.AsSpan(slot + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n == 0;
        }
    }
}
