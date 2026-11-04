using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace CoaMcp.Pipeline.Knowledge;

/// <summary>One row of the store's entries table, exactly as stored. Nothing is parsed or normalised, so a restore gets back what was exported.</summary>
public sealed record KnowledgeEntry(
    long Id, string SubjectId, string Kind, string RequestedKind, long Trust, string RealmJson, string? Field, string? Value, string? PositionJson,
    string EvidenceJson, string Author, string CreatedAt, long? SupersedesId, string? UndoneAt, string? UndoneBy, string ContentMd);

/// <summary>
/// The file format: a front matter block, then the entry's content byte for byte.
/// Every front matter line is <c>key: value</c> in a fixed order. A value is <c>null</c>, a whole number, or a double-quoted JSON string
/// (which is valid YAML too). The reader is strict and small on purpose: it accepts exactly what <see cref="Render"/> writes and nothing else,
/// so nothing in a file (a stray key, a look-alike fence inside the content, odd quoting) can change what is restored. The closing fence is found
/// by position after the last key, never by searching, so content may hold anything, including a line of three dashes.
/// Files are UTF-8 without a byte-order mark, with LF between front matter lines; the content keeps whatever line endings it had.
/// </summary>
public static class KnowledgeFormat
{
    public const string Live = "live", Undone = "undone", Superseded = "superseded";

    private static readonly string[] Keys =
    {
        "id", "status", "subject_id", "kind", "requested_kind", "trust", "realm_json", "field", "value", "position_json", "evidence_json",
        "author", "created_at", "supersedes_id", "undone_at", "undone_by",
    };

    private static readonly HashSet<string> Numbers = new() { "id", "trust", "supersedes_id" };
    private static readonly HashSet<string> Optional = new() { "field", "value", "position_json", "supersedes_id", "undone_at", "undone_by" };

    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly UTF8Encoding Utf8 = new(false, throwOnInvalidBytes: true);

    public static string FileName(long id) => id.ToString("D8", CultureInfo.InvariantCulture) + ".md";

    /// <summary>Text made safe for one line of terminal output: quotes, backslashes and control characters are escaped.</summary>
    public static string Escape(string text) => JsonSerializer.Serialize(text, Json)[1..^1];

    public static byte[] Render(KnowledgeEntry e, string status)
    {
        var sb = new StringBuilder("---\n");
        void Line(string key, string value) => sb.Append(key).Append(": ").Append(value).Append('\n');
        string Text(string? s) => s is null ? "null" : JsonSerializer.Serialize(s, Json);
        string Num(long? n) => n is null ? "null" : n.Value.ToString(CultureInfo.InvariantCulture);

        Line("id", Num(e.Id));
        Line("status", Text(status));
        Line("subject_id", Text(e.SubjectId));
        Line("kind", Text(e.Kind));
        Line("requested_kind", Text(e.RequestedKind));
        Line("trust", Num(e.Trust));
        Line("realm_json", Text(e.RealmJson));
        Line("field", Text(e.Field));
        Line("value", Text(e.Value));
        Line("position_json", Text(e.PositionJson));
        Line("evidence_json", Text(e.EvidenceJson));
        Line("author", Text(e.Author));
        Line("created_at", Text(e.CreatedAt));
        Line("supersedes_id", Num(e.SupersedesId));
        Line("undone_at", Text(e.UndoneAt));
        Line("undone_by", Text(e.UndoneBy));
        sb.Append("---\n").Append(e.ContentMd);
        return Utf8.GetBytes(sb.ToString());
    }

    /// <param name="name">The file name, for messages only.</param>
    public static KnowledgeEntry Parse(byte[] bytes, string name)
    {
        string text;
        try { text = Utf8.GetString(bytes); }
        catch (DecoderFallbackException ex) { throw new KnowledgeException($"{name}: not valid UTF-8.", ex); }

        const string Fence = "---\n";
        if (!text.StartsWith(Fence, StringComparison.Ordinal)) throw new KnowledgeException($"{name}: does not start with the front matter fence.");
        var pos = Fence.Length;
        var values = new Dictionary<string, string>();
        foreach (var key in Keys)
        {
            var end = text.IndexOf('\n', pos);
            if (end < 0) throw new KnowledgeException($"{name}: front matter ends early, before '{key}'.");
            var line = text.Substring(pos, end - pos);
            var prefix = key + ": ";
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) throw new KnowledgeException($"{name}: expected the line '{key}: ...' here.");
            values[key] = line[prefix.Length..];
            pos = end + 1;
        }
        if (string.CompareOrdinal(text, pos, Fence, 0, Fence.Length) != 0) throw new KnowledgeException($"{name}: the closing front matter fence is missing.");
        var content = text[(pos + Fence.Length)..];

        string? Str(string key)
        {
            var raw = values[key];
            if (raw == "null")
                return Optional.Contains(key) ? null : throw new KnowledgeException($"{name}: '{key}' can't be null.");
            string value;
            try { value = JsonSerializer.Deserialize<string>(raw, Json) ?? throw new JsonException(); }
            catch (JsonException ex) { throw new KnowledgeException($"{name}: '{key}' is not a quoted string.", ex); }
            // Only the exact form export writes is accepted, so there is one way to say a value.
            if (JsonSerializer.Serialize(value, Json) != raw) throw new KnowledgeException($"{name}: '{key}' is not written the way export writes it.");
            return value;
        }

        long? Num(string key)
        {
            var raw = values[key];
            if (raw == "null") return Optional.Contains(key) ? null : throw new KnowledgeException($"{name}: '{key}' can't be null.");
            if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n.ToString(CultureInfo.InvariantCulture) != raw)
                throw new KnowledgeException($"{name}: '{key}' is not a whole number.");
            return n;
        }

        var id = Num("id")!.Value;
        if (id <= 0) throw new KnowledgeException($"{name}: the id must be positive.");
        if (Str("status") is not (Live or Undone or Superseded)) throw new KnowledgeException($"{name}: 'status' must be live, undone or superseded.");
        foreach (var key in Keys)   // every value is checked, even the ones not used below
            if (Numbers.Contains(key)) _ = Num(key); else _ = Str(key);

        return new KnowledgeEntry(id, Str("subject_id")!, Str("kind")!, Str("requested_kind")!, Num("trust")!.Value, Str("realm_json")!, Str("field"), Str("value"),
            Str("position_json"), Str("evidence_json")!, Str("author")!, Str("created_at")!, Num("supersedes_id"), Str("undone_at"), Str("undone_by"), content);
    }
}
