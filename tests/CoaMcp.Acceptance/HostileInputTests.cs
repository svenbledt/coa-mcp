using System.Text;
using System.Text.Json;

namespace CoaMcp.Acceptance;

/// <summary>Bad input must get a clean answer: no server fault, no stack trace, no path. A tool may refuse or answer, but it must never fall over or leak.</summary>
[Collection("10 Hostile input")]
public class HostileInputTests
{
    private const string Area = "Hostile input";
    private const string Null = "a\0b";
    private static readonly string Long201 = new('x', 201);
    private static readonly string Huge = new('y', 40_000);   // below the body limit, far over any field limit

    private static IEnumerable<string> Strings(object? value) => value switch
    {
        string s => new[] { s },
        System.Collections.IEnumerable list => list.Cast<object?>().SelectMany(Strings),
        null => Array.Empty<string>(),
        _ when value.GetType().IsPrimitive => Array.Empty<string>(),
        _ => value.GetType().GetProperties().SelectMany(p => Strings(p.GetValue(value))),
    };

    private static readonly (string Tool, string Label, Dictionary<string, object?> Args)[] Cases =
    {
        // search: SQL and FTS injection, types, sizes
        C("search", "SQL injection", ("query", "' OR 1=1 --")),
        C("search", "SQL statement", ("query", "\"; DROP TABLE entities; --")),
        C("search", "FTS operators", ("query", "NEAR(fire ball) AND title:* OR ^x")),
        C("search", "FTS column filter", ("query", "{title body}: fire")),
        C("search", "unterminated FTS string", ("query", "\"fire")),
        C("search", "only punctuation", ("query", "*** ''' \"\"\" ---")),
        C("search", "null byte", ("query", Null)),
        C("search", "emoji and right-to-left text", ("query", "🔥 \u202Efireball")),
        C("search", "201 characters", ("query", Long201)),
        C("search", "40 KB query", ("query", Huge)),
        C("search", "wrong type for query", ("query", 12345)),
        C("search", "null query", ("query", null)),
        C("search", "no query", Array.Empty<(string, object?)>()),
        C("search", "wrong type for limit", ("query", "fire"), ("limit", "abc")),
        C("search", "float limit", ("query", "fire"), ("limit", 1.5)),
        C("search", "huge limit and offset", ("query", "fire"), ("limit", long.MaxValue), ("offset", long.MaxValue)),
        C("search", "lowest limit and offset", ("query", "fire"), ("limit", long.MinValue), ("offset", long.MinValue)),
        C("search", "number past 64 bits", ("query", "fire"), ("limit", 1e300)),
        C("search", "injection in topic", ("query", "fire"), ("topic", "' OR 1=1 --")),
        C("search", "injection in realm", ("query", "fire"), ("realm", "' OR 1=1 --")),
        C("search", "long realm", ("query", "fire"), ("realm", new string('r', 51))),
        C("search", "object for realm", ("query", "fire"), ("realm", new { a = 1 })),

        // get: path traversal and malformed ids
        C("get", "path traversal", ("id", "../../../../etc/passwd")),
        C("get", "path traversal in a type", ("id", "item:../../../etc/passwd")),
        C("get", "path traversal in a path id", ("id", "path:../../../data/current.json")),
        C("get", "backslash traversal", ("id", @"path:..\..\..\windows\win.ini")),
        C("get", "encoded traversal", ("id", "path:%2e%2e%2f%2e%2e%2fetc%2fpasswd")),
        C("get", "traversal in a table name", ("id", "dbc:../../x:1")),
        C("get", "SQL in a row id", ("id", "dbc:Spell:1; DROP TABLE t_Spell")),
        C("get", "SQL in a table name", ("id", "dbc:Spell\" WHERE 1=1 --:1")),
        C("get", "internal table name", ("id", "dbc:sqlite_master:1")),
        C("get", "row id past 64 bits", ("id", "dbc:Spell:99999999999999999999999")),
        C("get", "entry id negative", ("id", "entry:-1")),
        C("get", "entry id past 64 bits", ("id", "entry:99999999999999999999999")),
        C("get", "quote in an id", ("id", "spell:1' OR '1'='1")),
        C("get", "null byte", ("id", "spell:1\0")),
        C("get", "only a colon", ("id", ":")),
        C("get", "only colons", ("id", ":::::")),
        C("get", "201 characters", ("id", Long201)),
        C("get", "40 KB id", ("id", Huge)),
        C("get", "number for id", ("id", 133)),
        C("get", "array for id", ("id", new[] { "spell:133" })),
        C("get", "21 sections", ("id", "spell:133"), ("sections", Enumerable.Repeat("x", 21).ToArray())),
        C("get", "101-character section", ("id", "spell:133"), ("sections", new[] { new string('s', 101) })),
        C("get", "string for sections", ("id", "spell:133"), ("sections", "effects")),
        C("get", "null in sections", ("id", "spell:133"), ("sections", new string?[] { null })),
        C("get", "empty string in sections", ("id", "spell:133"), ("sections", new[] { "" })),
        C("get", "huge entries offset", ("id", "spell:133"), ("entries_offset", long.MaxValue)),
        C("get", "negative entries offset", ("id", "spell:133"), ("entries_offset", long.MinValue)),

        // spell helpers
        C("spell_compare", "words", ("idA", "fire"), ("idB", "ball")),
        C("spell_compare", "SQL", ("idA", "1 OR 1=1"), ("idB", "133")),
        C("spell_compare", "past 32 bits", ("idA", "99999999999"), ("idB", "133")),
        C("spell_compare", "negative", ("idA", "-1"), ("idB", "133")),
        C("spell_compare", "null", ("idA", null), ("idB", null)),
        C("spell_compare", "missing", ("idA", "133")),
        C("spell_compare", "number for a string", ("idA", 133), ("idB", 143)),
        C("decode_flags", "SQL in kind", ("kind", "' OR 1=1 --"), ("value", "1")),
        C("decode_flags", "negative mask", ("kind", "ProcFlags"), ("value", "-1")),
        C("decode_flags", "past 64 bits", ("kind", "ProcFlags"), ("value", "18446744073709551616")),
        C("decode_flags", "empty hex", ("kind", "ProcFlags"), ("value", "0x")),
        C("decode_flags", "bad hex", ("kind", "ProcFlags"), ("value", "0xZZ")),
        C("decode_flags", "exponent", ("kind", "ProcFlags"), ("value", "1e5")),
        C("decode_flags", "null kind and value", ("kind", null), ("value", null)),
        C("decode_flags", "long value", ("kind", "ProcFlags"), ("value", new string('1', 31))),
        C("decode_flags", "number for a string", ("kind", "ProcFlags"), ("value", 5)),
        C("spell_scaling", "huge level", ("id", "133"), ("level", long.MaxValue)),
        C("spell_scaling", "lowest level", ("id", "133"), ("level", long.MinValue)),
        C("spell_scaling", "string level", ("id", "133"), ("level", "eighty")),
        C("spell_scaling", "huge item level", ("id", "133"), ("level", 80), ("itemLevel", long.MaxValue)),
        C("spell_scaling", "spell id past 32 bits", ("id", "9999999999"), ("level", 80)),
        C("spell_scaling", "SQL in the id", ("id", "133; DROP TABLE spell_scaling_inputs"), ("level", 80)),

        // paths
        C("client_paths", "path traversal query", ("query", "../../../etc/passwd")),
        C("client_paths", "path traversal prefix", ("prefix", "../../../")),
        C("client_paths", "UNC prefix", ("prefix", @"\\server\share\")),
        C("client_paths", "null byte", ("query", "spell\0")),
        C("client_paths", "null byte in prefix", ("prefix", "DBFiles\0Client/")),
        C("client_paths", "FTS operators", ("query", "NEAR(spell fire) OR \"")),
        C("client_paths", "SQL injection", ("query", "' OR 1=1 --")),
        C("client_paths", "SQL in a prefix", ("prefix", "' OR '1'='1")),
        C("client_paths", "9 words", ("query", "aaa bbb ccc ddd eee fff ggg hhh iii")),
        C("client_paths", "SQL in ext", ("ext", "' OR 1=1 --")),
        C("client_paths", "traversal in ext", ("ext", "../x")),
        C("client_paths", "17-letter ext", ("ext", new string('e', 17))),
        C("client_paths", "40 KB prefix", ("prefix", Huge)),
        C("client_paths", "huge limit and offset", ("ext", "dbc"), ("limit", long.MaxValue), ("offset", long.MaxValue)),
        C("client_paths", "lowest limit and offset", ("ext", "dbc"), ("limit", long.MinValue), ("offset", long.MinValue)),
        C("client_paths", "only slashes", ("prefix", "////")),
        C("client_paths", "only dots", ("prefix", "...")),

        // DBC
        C("dbc_tables", "SQL in filter", ("filter", "' OR 1=1 --")),
        C("dbc_tables", "percent signs", ("filter", "%%%")),
        C("dbc_tables", "101 characters", ("filter", new string('f', 101))),
        C("dbc_tables", "huge offset", ("offset", long.MaxValue)),
        C("dbc_tables", "number for filter", ("filter", 5)),
        C("dbc_rows", "SQL in the table", ("table", "Spell; DROP TABLE t_Spell")),
        C("dbc_rows", "quote in the table", ("table", "t_Spell\"")),
        C("dbc_rows", "internal table", ("table", "sqlite_master")),
        C("dbc_rows", "the layer's own list", ("table", "dbc_fields")),
        C("dbc_rows", "traversal in the table", ("table", "../../etc/passwd")),
        C("dbc_rows", "SQL in a field name", ("table", "Map"), ("where", new[] { new { field = "MapID\" OR 1=1 --", op = "=", value = 1 } })),
        C("dbc_rows", "SQL in an operator", ("table", "Map"), ("where", new[] { new { field = "MapID", op = "= 1 OR 1=1 --", value = 1 } })),
        C("dbc_rows", "SQL in a value", ("table", "Map"), ("where", new[] { new { field = "MapID", op = "=", value = "1 OR 1=1" } })),
        C("dbc_rows", "SQL in a text value", ("table", "Map"), ("where", new[] { new { field = "name_0", op = "=", value = "x' OR '1'='1" } })),
        C("dbc_rows", "object as a value", ("table", "Map"), ("where", new[] { new { field = "MapID", op = "=", value = (object)new { a = 1 } } })),
        C("dbc_rows", "array as a value", ("table", "Map"), ("where", new[] { new { field = "MapID", op = "=", value = (object)new[] { 1, 2 } } })),
        C("dbc_rows", "null as a value", ("table", "Map"), ("where", new[] { new { field = "MapID", op = "=", value = (object?)null } })),
        C("dbc_rows", "boolean as a value", ("table", "Map"), ("where", new[] { new { field = "MapID", op = "=", value = (object)true } })),
        C("dbc_rows", "number past 64 bits", ("table", "Map"), ("where", new[] { new { field = "MapID", op = "=", value = "99999999999999999999999999" } })),
        C("dbc_rows", "NaN", ("table", "Map"), ("where", new[] { new { field = "MapID", op = "=", value = "NaN" } })),
        C("dbc_rows", "null field", ("table", "Map"), ("where", new[] { new { field = (string?)null, op = "=", value = 1 } })),
        C("dbc_rows", "null condition", ("table", "Map"), ("where", new object?[] { null })),
        C("dbc_rows", "string for where", ("table", "Map"), ("where", "MapID = 1")),
        C("dbc_rows", "9 conditions", ("table", "Map"), ("where", Enumerable.Repeat(new { field = "MapID", op = ">", value = 0 }, 9).ToArray())),
        C("dbc_rows", "101 columns", ("table", "Map"), ("columns", Enumerable.Repeat("MapID", 101).ToArray())),
        C("dbc_rows", "SQL in a column", ("table", "Map"), ("columns", new[] { "MapID\", (SELECT 1) --" })),
        C("dbc_rows", "null column", ("table", "Map"), ("columns", new string?[] { null })),
        C("dbc_rows", "huge limit and offset", ("table", "Map"), ("limit", long.MaxValue), ("offset", long.MaxValue)),
        C("dbc_rows", "null byte in a text value", ("table", "Map"), ("where", new[] { new { field = "name_0", op = "contains", value = "a\0b" } })),
        C("dbc_rows", "101-character text value", ("table", "Map"), ("where", new[] { new { field = "name_0", op = "contains", value = new string('v', 101) } })),
    };

    private static (string, string, Dictionary<string, object?>) C(string tool, string label, params (string Key, object? Value)[] args) =>
        (tool, label, args.ToDictionary(a => a.Key, a => a.Value));

    /// <summary>The few cases that show best what happens, in the report's Tool responses section.</summary>
    private static readonly string[] Shown = { "SQL injection", "path traversal", "SQL in the table", "negative mask", "huge level", "words", "SQL in filter" };

    [SkippableFact]
    public Task EveryTool_RefusesCleanly() => Suite.Run(Area, "bad arguments to every read tool get a clean answer", async c =>
    {
        var client = await Suite.Anonymous();
        foreach (var (tool, label, args) in Cases)
        {
            var who = $"{tool}: {label}";
            var sent = Strings(args.Values.ToList()).ToList();
            Reply r;
            try
            {
                r = await client.Call(tool, args, Shown.Contains(label) ? "hostile input: " + label : null);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            {
                c.Fail(who + ": the client call failed instead of getting an answer", ex.GetType().Name + ": " + ex.Message);
                continue;
            }
            var leak = Suite.Leak(r.All, sent);
            c.That(leak is null, who + ": no fault or leak in the answer", $"arguments: {ReportBook.Cut(JsonSerializer.Serialize(args, LiveClient.ArgsJson), 300)}\nleak: {leak}\nresponse: {ReportBook.Cut(r.All, 800)}");
            c.That(r.All.Length < 70_000, who + ": the answer stays bounded", r.All.Length.ToString());
        }
    });

    [SkippableFact]
    public Task Injection_ChangesNothingItCouldReach() => Suite.Run(Area, "injection doesn't widen a result or damage data", async c =>
    {
        var client = await Suite.Anonymous();

        // Bound values match nothing: a row filter that had become SQL would return rows.
        var text = await client.Call("dbc_rows", new { table = "Map", where = new[] { new { field = "name_0", op = "=", value = "x' OR '1'='1" } } });
        c.Has(text.Text, "0 rows of", "a text value holding SQL matches no row");

        var number = await client.Call("dbc_rows", new { table = "Map", where = new[] { new { field = "MapID", op = "=", value = "1 OR 1=1" } } });
        c.That(number.IsError, "a number field refuses text holding SQL", number.All);

        var filter = await client.Call("dbc_tables", new { filter = "' OR 1=1 --" });
        c.Has(filter.Text, "0 tables", "a table filter holding SQL matches no table");

        var search = await client.Call("search", new { query = "' OR 1=1 --", limit = 100 });
        var hits = search.Text.Split('\n').Count(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^\d+\. \["));
        c.Book.Observe($"search for \"' OR 1=1 --\" gives {hits} hits, treated as the plain words it holds.");

        // And everything the statements above tried to destroy is still there.
        var spell = await client.Call("get", new { id = "spell:133" });
        c.Has(spell.Text, "# Fireball", "the spell table is intact");
        var row = await client.Call("dbc_rows", new { table = "Spell", where = new[] { new { field = "ID", op = "=", value = 133 } }, columns = new[] { "ID" } });
        c.Has(row.Text, "1 rows of", "the Spell DBC table is intact");
    });

    private static readonly (string Label, Func<RawCase> Make)[] RawCases =
    {
        ("empty body, no content type", () => new RawCase(null, null)),
        ("empty body with JSON content type", () => new RawCase(Array.Empty<byte>(), "application/json")),
        ("not JSON", () => new RawCase(Encoding.UTF8.GetBytes("this is not json"), "application/json")),
        ("truncated JSON", () => new RawCase(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,"), "application/json")),
        ("JSON null", () => new RawCase(Encoding.UTF8.GetBytes("null"), "application/json")),
        ("a JSON number", () => new RawCase(Encoding.UTF8.GetBytes("123"), "application/json")),
        ("an empty JSON array", () => new RawCase(Encoding.UTF8.GetBytes("[]"), "application/json")),
        ("a JSON-RPC batch", () => new RawCase(Encoding.UTF8.GetBytes("[" + Raw.ToolsList + "," + Raw.ToolsList + "]"), "application/json")),
        ("wrong JSON-RPC version", () => new RawCase(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"1.0\",\"id\":1,\"method\":\"tools/list\"}"), "application/json")),
        ("no method", () => new RawCase(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1}"), "application/json")),
        ("unknown method", () => new RawCase(Encoding.UTF8.GetBytes(Raw.Rpc("no/such/method")), "application/json")),
        ("a 10 KB method name", () => new RawCase(Encoding.UTF8.GetBytes(Raw.Rpc(new string('m', 10_000))), "application/json")),
        ("tools/call with no params", () => new RawCase(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\"}"), "application/json")),
        ("tools/call of an unknown tool", () => new RawCase(Encoding.UTF8.GetBytes(Raw.Rpc("tools/call", "{\"name\":\"no_such_tool\",\"arguments\":{}}")), "application/json")),
        ("tools/call with params of the wrong type", () => new RawCase(Encoding.UTF8.GetBytes(Raw.Rpc("tools/call", "[1,2,3]")), "application/json")),
        ("tools/call with arguments of the wrong type", () => new RawCase(Encoding.UTF8.GetBytes(Raw.Rpc("tools/call", "{\"name\":\"search\",\"arguments\":\"fire\"}")), "application/json")),
        ("an object as id", () => new RawCase(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":{\"a\":1},\"method\":\"tools/list\"}"), "application/json")),
        ("invalid UTF-8", () => new RawCase(new byte[] { 0x7B, 0x22, 0xC3, 0x28, 0xFF, 0xFE, 0x22, 0x7D }, "application/json")),
        ("10,000 levels of nesting", () => new RawCase(Encoding.UTF8.GetBytes(new string('[', 10_000) + new string(']', 10_000)), "application/json")),
        ("valid body, no content type", () => new RawCase(Encoding.UTF8.GetBytes(Raw.ToolsList), null)),
        ("unknown charset", () => new RawCase(Encoding.UTF8.GetBytes(Raw.ToolsList), "application/json; charset=no-such-charset")),
        ("wrong content type", () => new RawCase(Encoding.UTF8.GetBytes(Raw.ToolsList), "text/plain")),
        ("XML", () => new RawCase(Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><a/>"), "application/xml")),
        ("no Accept header", () => new RawCase(Encoding.UTF8.GetBytes(Raw.ToolsList), "application/json", Accept: "")),
        ("wrong Accept header", () => new RawCase(Encoding.UTF8.GetBytes(Raw.ToolsList), "application/json", Accept: "text/html")),
        ("a body just over the 64 KB read limit", () => new RawCase(Encoding.UTF8.GetBytes(Raw.Rpc("tools/call", "{\"name\":\"search\",\"arguments\":{\"query\":\"" + new string('o', 70_000) + "\"}}")), "application/json", ExpectTooLarge: true)),
        ("a 2 MB body", () => new RawCase(new byte[2 * 1024 * 1024], "application/json", ExpectTooLarge: true)),
        ("GET", () => new RawCase(null, null, Method: HttpMethod.Get)),
        ("PUT", () => new RawCase(Encoding.UTF8.GetBytes(Raw.ToolsList), "application/json", Method: HttpMethod.Put)),
        ("DELETE", () => new RawCase(null, null, Method: HttpMethod.Delete)),
    };

    private sealed record RawCase(byte[]? Body, string? ContentType, string Accept = "application/json, text/event-stream", HttpMethod? Method = null, bool ExpectTooLarge = false);

    [SkippableFact]
    public Task RawRequests_GetNoServerFault() => Suite.Run(Area, "malformed HTTP and JSON-RPC, and oversized bodies, get no server fault", async c =>
    {
        foreach (var (label, make) in RawCases)
        {
            var raw = make();
            RawReply r;
            try
            {
                r = await Raw.Post(Suite.RawPaced, raw.Body, raw.ContentType, accept: raw.Accept, method: raw.Method);
            }
            catch (HttpRequestException ex) when (raw.ExpectTooLarge)
            {
                // Refusing a big body can look like a closed connection to the sender.
                c.Book.Observe($"{label}: the connection was closed while sending ({ex.Message}).");
                c.That(true, label + ": refused");
                continue;
            }
            c.That(r.Status < 500, $"{label}: no 5xx", $"status {r.Status}\nbody: {ReportBook.Cut(r.Body, 800)}");
            var leak = Suite.Leak(r.Body);
            c.That(leak is null, $"{label}: no leak", $"status {r.Status}\nleak: {leak}\nbody: {ReportBook.Cut(r.Body, 800)}");
            if (raw.ExpectTooLarge) c.That(r.Status == 413, $"{label}: refused as too large", $"status {r.Status}\nbody: {ReportBook.Cut(r.Body, 400)}");
        }
    });

    [SkippableFact]
    public Task Urls_ServeNoFilesAndNoInternals() => Suite.Run(Area, "URLs that reach for files get nothing", async c =>
    {
        foreach (var path in new[]
                 {
                     "/current.json", "/layers/notes/1.sqlite", "/writable/store.sqlite", "/data/current.json", "/.env", "/app/CoaMcp.Server.dll", "/appsettings.json",
                     "/mcp/../current.json", "/mcp/..%2f..%2fcurrent.json", "/..%2f..%2fetc%2fpasswd", "/mcp/extra", "/mcp%00", "/admin", "/swagger", "/metrics",
                 })
        {
            var url = "https://" + Settings.Host + path;
            RawReply r;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url, new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true }));
                using var response = await Suite.RawPaced.SendAsync(request);
                r = new RawReply((int)response.StatusCode, await response.Content.ReadAsStringAsync(), response.Headers, 0);
            }
            catch (HttpRequestException ex)
            {
                c.That(true, $"{path}: the request didn't get through", ex.Message);
                continue;
            }
            c.That(r.Status >= 400 && r.Status < 500, $"{path}: refused, not served", $"status {r.Status}\nbody: {ReportBook.Cut(r.Body, 400)}");
            c.That(Suite.Leak(r.Body) is null, $"{path}: no leak in the refusal", ReportBook.Cut(r.Body, 400));
        }
    });

    [SkippableFact]
    public Task AfterAllThat_TheServerStillAnswers() => Suite.Run(Area, "the server still answers after all of it", async c =>
    {
        var client = await Suite.Anonymous();
        var r = await client.Call("status");
        c.Has(r.Text, "CoA Knowledge MCP", "status still answers");
        var spell = await client.Call("get", new { id = "spell:133" });
        c.Has(spell.Text, "# Fireball", "reads still work");
    });
}
