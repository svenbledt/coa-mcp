using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CoaMcp.Acceptance;

/// <summary>DBC rows against the restored files, read by this suite's own WDBC parser, using the copy this suite works out from the manifest.</summary>
[Collection("06 Accuracy DBC")]
public class AccuracyDbcTests
{
    private const string Area = "Accuracy: DBC";
    private const int MaxColumnsPerCall = 99;   // the server's cap is 100, and the id comes along

    private sealed record ServerField(string Name, string Type, bool Guessed)
    {
        public string Shown => Guessed ? Name + "?" : Name;
        public bool IsTwin => Name.EndsWith("__str", StringComparison.Ordinal);
    }

    private static readonly Regex FieldEntry = new(@"^(\S+) (\S+)( \(guessed\))?( \[indexed\])?$", RegexOptions.Compiled);

    private static List<ServerField> ParseFields(string text, string table)
    {
        var lines = text.Split('\n');
        var at = Array.FindIndex(lines, l => l.StartsWith("Fields of " + table, StringComparison.OrdinalIgnoreCase));
        if (at < 0 || at + 1 >= lines.Length) throw new InvalidDataException("no field list for " + table + " in: " + ReportBook.Cut(text, 400));
        return lines[at + 1].Split(", ").Select(e =>
        {
            var m = FieldEntry.Match(e);
            if (!m.Success) throw new InvalidDataException($"can't read field entry '{e}' of {table}");
            return new ServerField(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Success);
        }).ToList();
    }

    /// <summary>The value the file holds for this field, in the form dbc_rows shows it.</summary>
    private static string Expected(Wdbc file, int record, int offset, ServerField f, bool hasTwin)
    {
        string Quoted(string? s) => s is null ? "null" : "\"" + AccuracyItemTests.OneLine(s, 200) + "\"";
        switch (f.Type)
        {
            case "int": return file.Int(record, offset).ToString(CultureInfo.InvariantCulture);
            case "uint": return file.UInt(record, offset).ToString(CultureInfo.InvariantCulture);
            case "byte": return file.Byte(record, offset).ToString(CultureInfo.InvariantCulture);
            case "float": return file.Float(record, offset).ToString("R", CultureInfo.InvariantCulture);
            case "string": return Quoted(file.Text(file.UInt(record, offset)));
            case "int_or_string":
                var n = file.Int(record, offset).ToString(CultureInfo.InvariantCulture);
                var text = hasTwin ? file.Text(file.UInt(record, offset)) : null;
                return text is { Length: > 0 } ? n + " " + Quoted(text) : n;
            default: throw new InvalidDataException("unknown field type " + f.Type);
        }
    }

    /// <summary>Splits a row line by the names the server was asked for, in order, so a value holding " | " stays whole.</summary>
    private static Dictionary<string, string> ParseRow(string line, IReadOnlyList<ServerField> shown)
    {
        var result = new Dictionary<string, string>();
        var at = 2;   // after "- "
        for (var i = 0; i < shown.Count; i++)
        {
            var start = shown[i].Shown + "=";
            if (string.CompareOrdinal(line, at, start, 0, start.Length) != 0) throw new InvalidDataException($"expected {start} at {at} in: {ReportBook.Cut(line, 400)}");
            at += start.Length;
            int end;
            if (i + 1 < shown.Count)
            {
                end = line.IndexOf($" | {shown[i + 1].Shown}=", at, StringComparison.Ordinal);
                if (end < 0) throw new InvalidDataException($"no field {shown[i + 1].Shown} after {shown[i].Name} in: {ReportBook.Cut(line, 400)}");
            }
            else end = line.Length;
            result[shown[i].Name] = line[at..end];
            at = end + 3;
        }
        return result;
    }

    private static bool Same(string expected, string actual, ServerField f)
    {
        if (f.Type != "float") return expected == actual;
        return expected == actual || (float.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && float.TryParse(actual, NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && (a.Equals(b) || a == b));
    }

    /// <summary>Where each field's bytes start, walking the layout the server lists. A twin text column shares its integer's bytes.</summary>
    private static Dictionary<string, int> Offsets(IReadOnlyList<ServerField> fields)
    {
        var offsets = new Dictionary<string, int>();
        var at = 0;
        foreach (var f in fields.Where(f => !f.IsTwin))
        {
            offsets[f.Name] = at;
            at += f.Type == "byte" ? 1 : 4;
        }
        return offsets;
    }

    private static bool IsNumber(ServerField f) => f.Type is "int" or "uint" or "byte";

    private static long IdOf(Wdbc file, int record, ServerField id) => id.Type switch
    {
        "uint" => file.UInt(record, 0),
        "byte" => file.Byte(record, 0),
        _ => file.Int(record, 0),
    };

    /// <summary>
    /// Where a record comes in dbc_rows' own order, for reading it by offset. A unique numeric first column is the table's key, so rows come in key order;
    /// otherwise they come in the file's order. The tool's description says "in row order", and the report's observations note what that means.
    /// </summary>
    private static int RankOf(Wdbc file, int record, ServerField id)
    {
        if (!IsNumber(id)) return record;
        var ids = new HashSet<long>();
        var mine = IdOf(file, record, id);
        var below = 0;
        for (var r = 0; r < file.Records; r++)
        {
            var v = IdOf(file, r, id);
            if (!ids.Add(v)) return record;
            if (v < mine) below++;
        }
        return below;
    }

    private sealed class Counts { public int Rows, Strings, Fields; public HashSet<string> Tables = new(StringComparer.OrdinalIgnoreCase); public HashSet<string> StringTables = new(StringComparer.OrdinalIgnoreCase); }

    private static async Task CheckRow(Checks c, LiveClient client, string table, Wdbc file, int record, IReadOnlyList<ServerField> fields, Counts counts, int? offset, Dictionary<long, int>? idCounts)
    {
        var offsets = Offsets(fields);
        var plain = fields.Where(f => !f.IsTwin).ToList();
        var names = fields.Select(f => f.Name).ToHashSet();
        var id = plain[0];
        var idValue = Expected(file, record, 0, id, false);

        var who = $"{table} record {record} ({id.Name}={idValue})";
        var rowOk = true;
        foreach (var chunk in plain.Skip(1).Chunk(MaxColumnsPerCall).DefaultIfEmpty(Array.Empty<ServerField>()))
        {
            var shown = new[] { id }.Concat(chunk).ToList();
            var args = new Dictionary<string, object?>
            {
                ["table"] = table,
                ["columns"] = chunk.Select(f => f.Name).ToArray(),
                ["limit"] = 100L,
            };
            if (chunk.Length == 0) args["columns"] = new[] { id.Name };
            if (offset is null) args["where"] = new[] { new { field = id.Name, op = "=", value = long.Parse(idValue, CultureInfo.InvariantCulture) } };
            else { args["limit"] = 1L; args["offset"] = (long)offset.Value; }

            var r = await client.Call("dbc_rows", args);
            if (!c.That(!r.IsError, who + " answers", r.All)) { rowOk = false; continue; }
            var lines = r.Text.Split('\n').Where(l => l.StartsWith("- ", StringComparison.Ordinal)).ToList();
            if (!c.That(lines.Count > 0, who + " is found", r.Text)) { rowOk = false; continue; }

            List<string>? bestDiffs = null;
            foreach (var line in lines)
            {
                Dictionary<string, string> parsed;
                try { parsed = ParseRow(line, shown); }
                catch (InvalidDataException ex) { c.Fail(who + ": the row couldn't be read", ex.Message); rowOk = false; bestDiffs = new List<string>(); break; }
                var diffs = new List<string>();
                foreach (var f in shown)
                {
                    var hasTwin = names.Contains(f.Name + "__str");
                    var expected = Expected(file, record, offsets[f.Name], f, hasTwin);
                    if (!Same(expected, parsed[f.Name], f)) diffs.Add($"{who}, field {f.Name} ({f.Type}{(f.Guessed ? ", guessed" : "")}):\n  file:   {expected}\n  server: {parsed[f.Name]}");
                }
                if (bestDiffs is null || diffs.Count < bestDiffs.Count) bestDiffs = diffs;
                if (diffs.Count == 0) break;
            }
            if (bestDiffs is { Count: > 0 })
            {
                rowOk = false;
                c.Fail($"{who}: {bestDiffs.Count} fields differ from the file", string.Join("\n", bestDiffs.Take(20)) + (bestDiffs.Count > 20 ? $"\n... and {bestDiffs.Count - 20} more" : ""));
            }
            else c.That(true, who + " matches");

            counts.Fields += shown.Count;
            foreach (var f in shown)
                if (f.Type == "string" || f.Type == "int_or_string")
                {
                    var s = file.Text(file.UInt(record, offsets[f.Name]));
                    if (s is { Length: > 0 }) { counts.Strings++; counts.StringTables.Add(table); }
                }
        }
        if (rowOk) counts.Rows++;
        counts.Tables.Add(table);

        // Now the row's own page, for rows with a usable id.
        if (idCounts is not null && IsNumber(id) && idCounts[IdOf(file, record, id)] == 1 && record % 4 == 0)
        {
            var page = await client.Call("get", new { id = $"dbc:{table}:{idValue}" });
            c.Has(page.Text, $"id: dbc:{table}:{idValue}", who + " has a row page");
            c.Has(page.Text, "trust: client", who + " page: trust");
            c.Has(page.Text, "realm: the CoA client (unproven)", who + " page: realm");
            foreach (var f in plain.Take(60))
            {
                var expected = Expected(file, record, offsets[f.Name], f, names.Contains(f.Name + "__str"));
                if (expected is "0" or "\"\"" or "null") continue;
                var label = f.Guessed ? $"{f.Name} (guessed {f.Type}): " : $"{f.Name}: ";
                if (f.Type == "string" && expected.Length > 150) continue;   // this page's text cap differs; the list checks it
                if (f.Type == "float")
                {
                    var m = Regex.Match(page.Text, "^" + Regex.Escape(label) + "(.+)$", RegexOptions.Multiline);
                    c.That(m.Success && Same(expected, m.Groups[1].Value, f), $"{who} page: {f.Name}", $"file: {expected}\nserver: {(m.Success ? m.Groups[1].Value : "(no such line)")}");
                }
                else c.Has(page.Text, "\n" + label + expected, $"{who} page: {f.Name}");
            }
        }
    }

    [SkippableFact]
    public Task RandomRows_AgainstTheFiles() => Suite.Run(Area, "at least 100 random rows across at least 10 tables, including string columns", async c =>
    {
        var client = await Suite.Anonymous();
        var manifest = ClientManifest.Instance;
        var rng = Suite.Rng("dbc");

        var fixedTables = new[] { "Spell", "Item", "Map", "AreaTable", "Faction", "ChrClasses", "SkillLine", "ItemSet", "Talent", "SpellIcon", "ItemStat", "MysticEnchant", "Creature", "Quest", "CharacterAdvancement", "DungeonEncounter" };
        var others = manifest.DbcTables.Select(t => Path.GetFileNameWithoutExtension(t.Path)).Where(t => !fixedTables.Contains(t, StringComparer.OrdinalIgnoreCase)).OrderBy(_ => rng.Next()).Take(10);
        var tables = fixedTables.Concat(others).ToList();

        var counts = new Counts();
        var planned = 0;
        foreach (var table in tables)
        {
            var path = manifest.Dbc(table);
            if (!c.That(path?.Winner is not null, $"{table} is in the manifest", table)) continue;
            var file = new Wdbc(manifest.Restored(path!.Winner!, path.Path));
            if (file.Records == 0) continue;

            var listing = await client.Call("dbc_tables", new { filter = table, limit = 1 });
            List<ServerField> fields;
            try { fields = ParseFields(listing.Text, table); }
            catch (InvalidDataException ex) { c.Fail($"{table}: the field list couldn't be read", ex.Message); continue; }

            var id = fields[0];
            var idCounts = new Dictionary<long, int>();
            if (IsNumber(id))
                for (var r = 0; r < file.Records; r++) { var v = IdOf(file, r, id); idCounts[v] = idCounts.GetValueOrDefault(v) + 1; }

            var taken = 0;
            for (var tries = 0; taken < 4 && tries < 40; tries++)
            {
                var record = rng.Next(file.Records);
                var rank = RankOf(file, record, id);
                var useId = IsNumber(id) && (rank > 100_000 || tries % 2 == 1);
                if (useId && idCounts[IdOf(file, record, id)] > 50) continue;   // too many rows share this id to find this one among them
                if (!useId && rank > 100_000) continue;                         // the deepest offset the tool accepts
                await CheckRow(c, client, table, file, record, fields, counts, useId ? null : rank, idCounts);
                taken++;
                planned++;
            }
        }
        c.That(planned >= 100, "at least 100 rows were compared", planned.ToString(CultureInfo.InvariantCulture));
        c.That(counts.Tables.Count >= 10, "across at least 10 tables", string.Join(", ", counts.Tables));
        c.That(counts.StringTables.Count >= 5, "with string columns in at least 5 tables", string.Join(", ", counts.StringTables));
        c.That(counts.Strings >= 50, "at least 50 non-empty strings were compared", counts.Strings.ToString(CultureInfo.InvariantCulture));
        c.Book.Observe("dbc_rows says \"in row order\", and with a unique numeric first column that means key order, not the file's record order: reading by offset walks the rows sorted by id. No value is wrong, but a reader counting file records would be off.");
        c.Book.Observe($"DBC rows: {planned} rows, {counts.Fields} field values and {counts.Strings} non-empty strings compared across {counts.Tables.Count} tables.");
    });

    [SkippableFact]
    public Task SpecialCases_BaseCopiesAndProjectLabels() => Suite.Run(Area, "project-built tables say so, and their base copies match the file below", async c =>
    {
        var client = await Suite.Anonymous();
        var manifest = ClientManifest.Instance;
        var project = manifest.DbcTables.Where(t => t.ProjectWinner).ToList();
        c.That(project.Count > 0, "the manifest has project-built tables");
        var rng = Suite.Rng("dbc-base");
        var counts = new Counts();
        foreach (var t in project)
        {
            var name = Path.GetFileNameWithoutExtension(t.Path);
            var rows = await client.Call("dbc_rows", new { table = name, limit = 1, columns = Array.Empty<string>() });
            c.Has(rows.Text, "project", $"{name}, won by {t.Winner!.Archive}, says project-built");
            c.Has(rows.Text, "Base copy", $"{name} names its base copy, or says it has none");

            if (t.NextNonProject is { Marker: false } next)
            {
                var baseName = name + "__base";
                var file = new Wdbc(manifest.Restored(next, t.Path));
                var listing = await client.Call("dbc_tables", new { filter = baseName, limit = 1 });
                c.Has(listing.Text, $"base copy of {name}", $"{baseName} is marked as a base copy");
                var fields = ParseFields(listing.Text, baseName);
                if (file.Records > 0)
                {
                    var record = rng.Next(file.Records);
                    var rank = RankOf(file, record, fields[0]);
                    await CheckRow(c, client, baseName, file, record, fields, counts, rank > 100_000 && IsNumber(fields[0]) ? null : rank, null);
                }
            }
        }
    });

    [SkippableFact]
    public Task AllTables_RowsFieldsAndWinners() => Suite.Run(Area, "every table: rows, fields, winner and origin against the files", async c =>
    {
        var client = await Suite.Anonymous();
        var manifest = ClientManifest.Instance;

        var listed = new Dictionary<string, Match>(StringComparer.OrdinalIgnoreCase);
        var line = new Regex(@"^(\S+) \| ([\d,]+) rows \| (\d+) fields, (\d+) named \| (.+?) \((\w+)\)(?: \| base copy of (\S+))?(?: \| (.*))?$", RegexOptions.Multiline);
        for (var offset = 0; ; offset += 100)
        {
            var page = await client.Call("dbc_tables", new { limit = 100, offset });
            var ms = line.Matches(page.Text);
            foreach (Match m in ms) listed[m.Groups[1].Value] = m;
            if (ms.Count < 100) break;
        }

        foreach (var t in manifest.DbcTables)
        {
            var name = Path.GetFileNameWithoutExtension(t.Path);
            if (!c.That(listed.TryGetValue(name, out var m), $"{name} is listed")) continue;
            if (t.Winner is null) { c.Has(m!.Value, "not_in_default_client", $"{name} says the default client doesn't read it"); continue; }
            if (t.Deleted) { c.Has(m!.Value, "deleted", $"{name} says it's deleted"); continue; }
            var (records, fields, _) = Wdbc.ReadHeader(manifest.Restored(t.Winner, t.Path));
            c.Equal(records.ToString("N0", CultureInfo.InvariantCulture), m!.Groups[2].Value, $"{name} rows");
            c.Equal(fields, m.Groups[3].Value, $"{name} fields");
            c.Equal(t.Winner.Archive, m.Groups[5].Value, $"{name} winning archive");
            c.Equal(t.Winner.Origin, m.Groups[6].Value, $"{name} origin");
        }
        var expected = manifest.DbcTables.Count() + manifest.DbcTables.Count(t => t.ProjectWinner && t.NextNonProject is { Marker: false });
        c.Equal(expected, listed.Count, "tables listed, counting base copies");
    });
}
