using System.Text.RegularExpressions;

namespace CoaMcp.Acceptance;

/// <summary>Every read tool: normal use, paging, caps, sections, filters and not-found. These captures fill the report's Tool responses section.</summary>
[Collection("02 Read tools")]
public class ReadToolTests
{
    private const string Area = "Read tools";

    private static readonly Regex HitLine = new(@"^\d+\. \[[^|\]]+ \| (client|derived|cache|observed|memory) \| [^\]]+\] .+\(\w+; src: .+, \d{4}-\d\d-\d\d\)", RegexOptions.Compiled);

    private static List<string> Hits(string text) => text.Split('\n').Where(l => Regex.IsMatch(l, @"^\d+\. \[")).ToList();

    private static string HitId(string line) => Regex.Match(line, @"^\d+\. \[[^|]+\| \w+ \| ([^\]]+)\]").Groups[1].Value;

    [SkippableFact]
    public Task Search() => Suite.Run(Area, "search", async c =>
    {
        var client = await Suite.Anonymous();

        var normal = await client.Call("search", new { query = "fireball", limit = 3 }, "normal use");
        c.That(!normal.IsError, "normal search works", normal.All);
        c.Equal(3, Hits(normal.Text).Count, "limit 3 gives 3 hits");
        foreach (var h in Hits(normal.Text)) c.That(HitLine.IsMatch(h), "a hit shows realm, trust, id, layer, source and date", h);

        var first = await client.Call("search", new { query = "fire", limit = 5, offset = 0 }, "paging: first page");
        var second = await client.Call("search", new { query = "fire", limit = 5, offset = 5 }, "paging: second page");
        var a = Hits(first.Text).Select(HitId).ToList();
        var b = Hits(second.Text).Select(HitId).ToList();
        c.Equal(5, a.Count, "first page size");
        c.Equal(5, b.Count, "second page size");
        c.That(!a.Intersect(b).Any(), "pages don't overlap", $"{string.Join(",", a)} / {string.Join(",", b)}");
        c.Has(first.Text, "use offset=5", "a full page says how to get the next");
        c.That(Hits(second.Text)[0].StartsWith("6. ", StringComparison.Ordinal), "numbering continues across pages", Hits(second.Text)[0]);

        var capped = await client.Call("search", new { query = "fire", limit = 5000 });
        c.Has(capped.Text, "limit 5000 capped at 100", "an oversized limit says it was capped");
        c.That(Hits(capped.Text).Count <= 100, "never more than 100 hits", Hits(capped.Text).Count.ToString());
        var low = await client.Call("search", new { query = "fire", limit = -5, offset = -3 }, "limit and offset out of range");
        c.Has(low.Text, "limit -5 raised to 1", "a negative limit is raised and says so");
        c.Has(low.Text, "offset -3 raised to 0", "a negative offset is raised and says so");
        c.Equal(1, Hits(low.Text).Count, "and a negative limit gives one hit, not everything");

        var wotlk = await client.Call("search", new { query = "sword", realm = "wotlk", limit = 10 }, "realm filter");
        c.That(Hits(wotlk.Text).Count > 0, "realm wotlk finds something", wotlk.Text);
        foreach (var h in Hits(wotlk.Text)) c.That(Regex.IsMatch(h, @"^\d+\. \[[^\]]*wotlk", RegexOptions.IgnoreCase), "every wotlk-filtered hit is labeled wotlk", h);

        var coa = await client.Call("search", new { query = "fireball", realm = "coa", limit = 10 });
        foreach (var h in Hits(coa.Text)) c.That(Regex.IsMatch(h, @"^\d+\. \[[^\]]*CoA"), "every coa-filtered hit names CoA or the CoA client", h);

        foreach (var (topic, prefix) in new[] { ("spells", "spell:"), ("items", "item:"), ("notes", "") , ("dbc", "dbc:") })
        {
            var r = await client.Call("search", new { query = topic == "notes" ? "client" : topic == "dbc" ? "spell" : "fire", topic, limit = 5 }, $"topic {topic}");
            c.That(Hits(r.Text).Count > 0, $"topic {topic} finds something", r.Text);
            if (prefix.Length > 0) foreach (var h in Hits(r.Text)) c.That(HitId(h).StartsWith(prefix, StringComparison.Ordinal), $"topic {topic} returns only its own type", h);
        }
        var classes = await client.Call("search", new { query = "barbarian", topic = "classes", limit = 5 }, "topic classes");
        c.That(Hits(classes.Text).Count > 0, "topic classes finds barbarian", classes.Text);

        var dungeons = await client.Call("search", new { query = "molten", topic = "dungeons" }, "topic with no data yet");
        c.Has(dungeons.Text, "has no data yet", "a topic with no data says so");
        var npcs = await client.Call("search", new { query = "defias", topic = "npcs" });
        c.That(!npcs.IsError, "topic npcs answers without a fault", npcs.All);

        var unknown = await client.Call("search", new { query = "fire", topic = "bananas" }, "unknown topic");
        c.That(unknown.IsError, "an unknown topic is an error");
        c.Has(unknown.All, "Valid topics", "and it lists the valid ones");

        var none = await client.Call("search", new { query = "zzzqqqxxxnotaword" }, "not found");
        c.That(!none.IsError, "no hits isn't an error", none.All);
        c.Has(none.Text, ": 0 hits", "it says 0 hits");

        var empty = await client.Call("search", new { query = "   " }, "empty query");
        c.That(empty.IsError, "an empty query is an error");
    });

    [SkippableFact]
    public Task Get() => Suite.Run(Area, "get", async c =>
    {
        var client = await Suite.Anonymous();

        var item = await client.Call("get", new { id = "item:10531" }, "an item");
        c.Has(item.Text, "# Stoneclad Libram", "item page title");
        c.Has(item.Text, "trust: derived", "item page says its trust");

        var spell = await client.Call("get", new { id = "spell:133" }, "a spell");
        c.Has(spell.Text, "# Fireball", "spell page title");
        c.Has(spell.Text, "trust: client", "spell page says its trust");
        c.Has(spell.Text, "## effects", "spell page has sections");

        var section = await client.Call("get", new { id = "spell:133", sections = new[] { "effects" } }, "one section");
        c.Has(section.Text, "## effects", "the asked section is there");
        c.Lacks(section.Text, "## visuals", "and the others aren't");
        c.That(section.Text.Length < spell.Text.Length, "a section answer is shorter than the page");

        var two = await client.Call("get", new { id = "spell:133", sections = new[] { "summary", "triggers" } });
        c.Has(two.Text, "## summary", "two sections: first");
        c.Has(two.Text, "## triggers", "two sections: second");

        var noSuch = await client.Call("get", new { id = "spell:133", sections = new[] { "nope" } }, "a section that doesn't exist");
        c.Has(noSuch.Text, "No section named 'nope'", "an unknown section says so");
        c.Has(noSuch.Text, "Available sections: summary, effects, triggers, visuals", "and lists what exists");

        var cls = await client.Call("get", new { id = "class:coa:barbarian" }, "a class");
        c.Has(cls.Text, "realm: CoA", "a CoA class page says CoA");

        var table = await client.Call("get", new { id = "dbc:Spell" }, "a DBC table");
        c.That(!table.IsError, "a DBC table page answers", table.All);

        var row = await client.Call("get", new { id = "dbc:Map:0" }, "a DBC row");
        c.Has(row.Text, "dbc:Map:0", "a DBC row page names its id");
        c.Has(row.Text, "Winning copy", "a DBC row says which copy it is");

        var file = await client.Call("get", new { id = "path:DBFilesClient/Spell.dbc" }, "a client path");
        c.Has(file.Text, "winner:", "a path page names the winner");

        var missing = await client.Call("get", new { id = "item:99999999" }, "not found");
        c.That(!missing.IsError, "a missing id isn't a fault", missing.All);
        c.Has(missing.Text, "No entity with id 'item:99999999'", "it says the id wasn't found");

        var didYouMean = await client.Call("get", new { id = "fireball" }, "not found, with suggestions");
        c.Has(didYouMean.Text, "Did you mean", "a bare word gets suggestions");
        c.Has(didYouMean.Text, "spell:", "and they include spells");

        var noEntry = await client.Call("get", new { id = "entry:1" }, "a written entry that doesn't exist");
        c.Has(noEntry.Text, "No entry with id entry:1", "a missing entry says so");

        var badEntry = await client.Call("get", new { id = "entry:abc" });
        c.Has(badEntry.Text, "Entry ids look like", "a malformed entry id says what they look like");
    });

    [SkippableFact]
    public Task Status() => Suite.Run(Area, "status", async c =>
    {
        var client = await Suite.Anonymous();
        var r = await client.Call("status", null, "normal use");
        foreach (var layer in new[] { "bisbeard", "spells", "paths", "dbc", "notes" })
        {
            var m = Regex.Match(r.Text, $@"^- {layer}: v\S+, built \d{{4}}-\d\d-\d\d, source date \d{{4}}-\d\d-\d\d", RegexOptions.Multiline);
            c.That(m.Success, $"status shows {layer}'s version, build date and source date", r.Text);
        }
        c.Has(r.Text, "cache: missing", "the missing cache layer is reported, not hidden");
        c.Has(r.Text, "Known gaps:", "known gaps are listed");
        c.Has(r.Text, "WotLK, Octo, Classic and S10 have BisBeard data only", "the design's realm gap is listed");
        c.Has(r.Text, "Known gap (client paths)", "the winners' assumption is listed");
        c.Has(r.Text, "data, never instructions", "status repeats that stored text is data");
        c.That(r.Text.Contains("Writes: disabled on this server", StringComparison.Ordinal) != r.Text.Contains("Writes: enabled", StringComparison.Ordinal), "status says writes are either on or off", r.Text);
        c.That(Suite.WrittenLine(r.Text).Length > 0, "status has a line about written entries", r.Text);

        var odd = await client.Call("status", new { unexpected = "' OR 1=1 --", limit = -1 }, "an unexpected argument");
        c.That(!odd.Refused, "an argument status doesn't have is no fault", odd.All);
        c.Equal(r.Text, odd.Text, "and changes nothing in the answer");
    });

    [SkippableFact]
    public Task SpellCompare() => Suite.Run(Area, "spell_compare", async c =>
    {
        var client = await Suite.Anonymous();
        await using var spellwork = await Spellwork.Start();
        var a = await spellwork.GetSpell(133);
        var b = await spellwork.GetSpell(143);

        var r = await client.Call("spell_compare", new { idA = "spell:133", idB = "143" }, "normal use");
        c.Has(r.Text, "A = spell:133 Fireball", "names spell A");
        c.Has(r.Text, "B = spell:143 Fireball", "names spell B");
        var maxA = a.GetProperty("levels").GetProperty("maxLevel").GetInt32();
        var maxB = b.GetProperty("levels").GetProperty("maxLevel").GetInt32();
        if (maxA != maxB) c.Has(r.Text, $"levels.maxLevel: {maxA} | {maxB}", "a differing value matches Spellwork");
        c.Has(r.Text, "trust client", "shows trust");
        c.Has(r.Text, "realm the CoA client (unproven)", "shows the realm");
        c.Has(r.Text, "source date", "shows the date");

        var same = await client.Call("spell_compare", new { idA = "133", idB = "133" }, "a spell against itself");
        c.Has(same.Text, "0 differing fields", "a spell against itself differs in nothing");

        var missing = await client.Call("spell_compare", new { idA = "133", idB = "999999999" }, "not found");
        c.That(missing.IsError, "an unknown spell is an error");
        c.Has(missing.All, "No spell with id", "and says so");

        var bad = await client.Call("spell_compare", new { idA = "fireball", idB = "133" }, "not a spell id");
        c.That(bad.IsError, "a word isn't a spell id");
        c.Has(bad.All, "is not a spell id", "and says so");
    });

    [SkippableFact]
    public Task DecodeFlags() => Suite.Run(Area, "decode_flags", async c =>
    {
        var client = await Suite.Anonymous();
        await using var spellwork = await Spellwork.Start();

        var normal = await client.Call("decode_flags", new { kind = "SpellSchoolMask", value = "20" }, "normal use");
        c.Has(normal.Text, "SPELL_SCHOOL_MASK_FIRE (0x4)", "bit 4");
        c.Has(normal.Text, "SPELL_SCHOOL_MASK_FROST (0x10)", "bit 16");
        c.Has(normal.Text, "trust client", "shows trust");
        c.That(Regex.IsMatch(normal.Text, @"source date \d{4}-\d\d-\d\d"), "shows the date", normal.Text);
        c.Has(normal.Text, "realm the CoA client (unproven)", "the accuracy rule: every fact shows its realm");
        c.Has(normal.Text, "flag names from Spellwork's CoA enums", "says where flag names really come from");
        c.That(!Regex.IsMatch(normal.Text, @"\.MPQ|patch-", RegexOptions.IgnoreCase), "names no archive, since flag names don't come from one", normal.Text);

        var hex = await client.Call("decode_flags", new { kind = "SpellSchoolMask", value = "0x14" }, "hex value");
        c.Has(hex.Text, "SPELL_SCHOOL_MASK_FIRE", "hex means the same");

        var zero = await client.Call("decode_flags", new { kind = "SpellSchoolMask", value = "0" });
        c.Has(zero.Text, "no bits set", "zero says no bits set");

        var unmatched = await client.Call("decode_flags", new { kind = "SpellSchoolMask", value = "0x80000000" }, "bits with no name");
        c.Has(unmatched.Text, "unmatchedBits: 0x80000000", "unknown bits are reported");

        var kind = await client.Call("decode_flags", new { kind = "Bananas", value = "1" }, "unknown kind");
        c.That(kind.IsError, "an unknown kind is an error");
        c.Has(kind.All, "Valid kinds", "and lists the valid ones");

        var bad = await client.Call("decode_flags", new { kind = "ProcFlags", value = "lots" }, "bad value");
        c.That(bad.IsError, "a word isn't a mask");

        // Against Spellwork: the same names and leftovers for random masks of real kinds.
        var rng = Suite.Rng("decode_flags");
        foreach (var k in new[] { "SpellSchoolMask", "ProcFlags", "SpellAtribute", "ProcFlagsEx", "CreatureTypeMask" })
        {
            var mask = (ulong)rng.NextInt64(1, uint.MaxValue);
            var local = await spellwork.DecodeFlags(k, mask);
            var server = await client.Call("decode_flags", new { kind = k, value = mask.ToString() });
            foreach (var f in local.GetProperty("flags").EnumerateArray())
                c.Has(server.Text, $"- {f.GetProperty("name").GetString()} (0x{f.GetProperty("value").GetUInt64():X})", $"{k} {mask}: a flag Spellwork names");
            var localCount = local.GetProperty("flags").GetArrayLength();
            var serverCount = server.Text.Split('\n').Count(l => l.StartsWith("- ", StringComparison.Ordinal));
            c.Equal(localCount, serverCount, $"{k} {mask}: same number of flags as Spellwork");
        }
    });

    [SkippableFact]
    public Task SpellScaling() => Suite.Run(Area, "spell_scaling", async c =>
    {
        var client = await Suite.Anonymous();
        var normal = await client.Call("spell_scaling", new { id = "spell:133", level = 80 }, "normal use");
        c.Has(normal.Text, "spell:133 Fireball at level 80", "names spell and level");
        c.Has(normal.Text, "Effect 0", "lists effects");
        c.Has(normal.Text, "trust client", "shows trust");

        var high = await client.Call("spell_scaling", new { id = "133", level = 5000, itemLevel = 200 }, "level above the cap, with item level");
        c.Has(high.Text, "level 5000 capped at 100", "a high level is capped and says so");
        c.Has(high.Text, "item level is not applied", "item level says it does nothing");

        var low = await client.Call("spell_scaling", new { id = "133", level = -4 }, "level below 1");
        c.Has(low.Text, "level -4 raised to 1", "a low level is raised and says so");

        var missing = await client.Call("spell_scaling", new { id = "spell:999999999", level = 80 }, "not found");
        c.That(missing.IsError, "an unknown spell is an error");
        c.Has(missing.All, "No spell with id", "and says so");

        var noLevel = await client.Call("spell_scaling", new { id = "133" }, "level left out");
        c.That(noLevel.Refused, "a missing level is refused, not guessed", noLevel.All);
    });

    [SkippableFact]
    public Task ClientPaths() => Suite.Run(Area, "client_paths", async c =>
    {
        var client = await Suite.Anonymous();

        var whole = await client.Call("client_paths", new { prefix = "DBFilesClient/Spell.dbc" }, "one whole path");
        c.Has(whole.Text, "winner:", "a whole path gets its winner");
        c.Has(whole.Text, "holders, highest priority first", "and its holders");
        c.Has(whole.Text, "Paths only, no file content", "and says it serves no content");

        var anyCase = await client.Call("client_paths", new { prefix = @"dbfilesclient\spell.DBC" }, "any case, either slash");
        c.Has(anyCase.Text, "DBFilesClient/Spell.dbc", "case and slash don't matter");

        var folder = await client.Call("client_paths", new { prefix = "DBFilesClient/", limit = 5 }, "browse a folder");
        c.Has(folder.Text, "directly inside", "a folder lists its contents");
        c.Has(folder.Text, "More below: use offset=5", "and how to page");

        var top = await client.Call("client_paths", new { prefix = "/" }, "the top level");
        c.Has(top.Text, "Top level:", "/ lists the top");

        var query = await client.Call("client_paths", new { query = "spell fireball", ext = "mdx", limit = 5 }, "search by words and type");
        c.Has(query.Text, "containing \"spell\" and \"fireball\"", "a word search says what it looked for");
        foreach (var line in query.Text.Split('\n').Where(l => l.Contains(" | ", StringComparison.Ordinal)))
            c.That(line.Contains("fireball", StringComparison.OrdinalIgnoreCase) && line.Contains("spell", StringComparison.OrdinalIgnoreCase), "every result holds both words", line);

        var p1 = await client.Call("client_paths", new { ext = "dbc", limit = 3, offset = 0 }, "paging by type");
        var p2 = await client.Call("client_paths", new { ext = "dbc", limit = 3, offset = 3 });
        List<string> Lines(string t) => t.Split('\n').Where(l => l.Contains(" | ", StringComparison.Ordinal)).ToList();
        c.Equal(3, Lines(p1.Text).Count, "page size");
        c.That(!Lines(p1.Text).Intersect(Lines(p2.Text)).Any(), "pages don't overlap");

        var capped = await client.Call("client_paths", new { ext = "dbc", limit = 100000 }, "oversized limit");
        c.Has(capped.Text, "limit 100000 capped at 100", "a limit over the cap says so");
        c.That(Lines(capped.Text).Count <= 100, "never more than 100 rows", Lines(capped.Text).Count.ToString());

        var missing = await client.Call("client_paths", new { prefix = "No/Such/File.blp" }, "not found");
        c.Has(missing.Text, "0 paths", "a path that doesn't exist gives no rows");

        var nothing = await client.Call("client_paths", new { }, "no arguments");
        c.That(nothing.IsError, "no arguments is an error");
        c.Has(nothing.All, "Give at least one of", "and says what's needed");

        var shortWord = await client.Call("client_paths", new { query = "ab" }, "too short a word");
        c.That(shortWord.IsError, "a two-letter word is refused");
    });

    [SkippableFact]
    public Task DbcTables() => Suite.Run(Area, "dbc_tables", async c =>
    {
        var client = await Suite.Anonymous();
        var all = await client.Call("dbc_tables", new { limit = 5 }, "normal use");
        c.Has(all.Text, "realm the CoA client (unproven), trust client", "shows realm and trust");
        c.Has(all.Text, "source date", "shows the date");
        c.Has(all.Text, "More results may exist: use offset=5", "and how to page");

        var filter = await client.Call("dbc_tables", new { filter = "spellicon" }, "filter, any case");
        c.Has(filter.Text, "SpellIcon |", "a filter finds its table");

        var exact = await client.Call("dbc_tables", new { filter = "Map" }, "an exact name also lists its fields");
        c.Has(exact.Text, "Fields of Map", "an exact name lists fields");
        c.Has(exact.Text, "MapID uint", "with types");

        var guessed = await client.Call("dbc_tables", new { filter = "MysticEnchant" }, "a table with no proven names");
        c.Has(guessed.Text, "(guessed)", "inferred fields say guessed");

        var capped = await client.Call("dbc_tables", new { limit = 100000 });
        c.Has(capped.Text, "limit 100000 capped at 100", "a limit over the cap says so");

        var deep = await client.Call("dbc_tables", new { limit = 5, offset = 5000 }, "offset past the end");
        c.Has(deep.Text, "offset 5000 capped at 1000", "a deep offset is capped and says so");
        c.Has(deep.Text, "0 tables", "and gives no rows");

        var none = await client.Call("dbc_tables", new { filter = "NoSuchTable" }, "not found");
        c.Has(none.Text, "0 tables", "an unknown filter gives no tables");
    });

    [SkippableFact]
    public Task DbcRows() => Suite.Run(Area, "dbc_rows", async c =>
    {
        var client = await Suite.Anonymous();
        var normal = await client.Call("dbc_rows", new { table = "Map", limit = 2 }, "normal use");
        c.Has(normal.Text, "DBC Map (realm the CoA client (unproven), trust client", "shows realm and trust");
        c.Has(normal.Text, "Winning copy", "says which copy");
        c.Has(normal.Text, "2 rows of 374", "says how many of how many");
        c.Has(normal.Text, "More results may exist: use offset=2", "and how to page");

        var where = await client.Call("dbc_rows", new { table = "Spell", where = new[] { new { field = "ID", op = "=", value = 133 } }, columns = new[] { "ID", "NameLang_0" } }, "filter on an id, chosen columns");
        c.Has(where.Text, "1 rows of", "one row matches");
        c.Has(where.Text, "\"Fireball\"", "and it's Fireball");

        var contains = await client.Call("dbc_rows", new { table = "Map", where = new[] { new { field = "name_0", op = "contains", value = "azeroth" } }, limit = 3 }, "contains on text");
        c.That(!contains.IsError, "contains works on text", contains.All);
        c.Has(contains.Text, "Azeroth", "and finds it");

        var p1 = await client.Call("dbc_rows", new { table = "Map", columns = new[] { "MapID" }, limit = 2, offset = 0 }, "paging");
        var p2 = await client.Call("dbc_rows", new { table = "Map", columns = new[] { "MapID" }, limit = 2, offset = 2 });
        List<string> Rows(string t) => t.Split('\n').Where(l => l.StartsWith("- ", StringComparison.Ordinal)).ToList();
        c.That(Rows(p1.Text).Count == 2 && Rows(p2.Text).Count == 2 && !Rows(p1.Text).Intersect(Rows(p2.Text)).Any(), "pages are full and don't overlap", p1.Text + "\n" + p2.Text);

        var capped = await client.Call("dbc_rows", new { table = "Item", limit = 100000, columns = new[] { "ID" } }, "oversized limit");
        c.Has(capped.Text, "limit 100000 capped at 100", "a limit over the cap says so");
        c.That(Rows(capped.Text).Count <= 100, "never more than 100 rows", Rows(capped.Text).Count.ToString());

        var unknown = await client.Call("dbc_rows", new { table = "NoSuchTable" }, "unknown table");
        c.That(unknown.IsError, "an unknown table is an error");
        c.Has(unknown.All, "Unknown DBC table", "and says so");

        var field = await client.Call("dbc_rows", new { table = "Map", where = new[] { new { field = "Nope", op = "=", value = 1 } } }, "unknown field");
        c.That(field.IsError, "an unknown field is an error");
        c.Has(field.All, "Unknown field", "and says so");

        var op = await client.Call("dbc_rows", new { table = "Map", where = new[] { new { field = "MapID", op = "LIKE", value = 1 } } }, "unknown operator");
        c.That(op.IsError, "an unknown operator is an error");

        var number = await client.Call("dbc_rows", new { table = "Map", where = new[] { new { field = "MapID", op = "contains", value = "1" } } }, "contains on a number");
        c.That(number.IsError, "contains on a number is an error");

        var none = await client.Call("dbc_rows", new { table = "Map", where = new[] { new { field = "MapID", op = "=", value = 987654 } } }, "no row matches");
        c.Has(none.Text, "0 rows of", "no match gives zero rows, not a fault");

        // One condition on an unindexed column of the largest table must end cleanly, whichever way it goes.
        var slow = await client.Call("dbc_rows", new { table = "ItemStat", where = new[] { new { field = "f3", op = "=", value = 987654321 } }, limit = 1 }, "a slow scan on the largest table");
        c.That(!slow.IsError || slow.All.Contains("too slow", StringComparison.Ordinal), "a slow scan answers or says it's too slow", slow.All);
        c.That(slow.Ms < 30_000, "and doesn't hang", slow.Ms + " ms");
        c.Book.Observe($"An unindexed scan of ItemStat took {slow.Ms} ms and {(slow.IsError ? "stopped with the 'too slow' message" : "finished")}.");
    });
}
