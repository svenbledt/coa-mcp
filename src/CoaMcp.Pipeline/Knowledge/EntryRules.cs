using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CoaMcp.Core;
using CoaMcp.Server;
using ModelContextProtocol;

namespace CoaMcp.Pipeline.Knowledge;

/// <summary>
/// Puts a file from the repository through the same checks a live write goes through (the server's own <see cref="WriteRules"/>), so a
/// tampered file can't restore an entry the server would have refused. Two things can't be checked without the layers and are checked by shape only:
/// whether a <c>path:</c> or DBC row subject exists, and whether it names generated data. Errors name the file and the rule, never the entry's text.
/// </summary>
public static partial class EntryRules
{
    private const int MaxByLength = 100;

    [GeneratedRegex(@"\A\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z\z", RegexOptions.CultureInvariant)]
    private static partial Regex Timestamp();

    public static void Check(KnowledgeEntry e, string file)
    {
        try
        {
            var trust = e.Trust switch
            {
                (long)Trust.Observed => "observed",
                (long)Trust.Memory => "memory",
                _ => null,
            };
            var level = WriteRules.TrustLevel(trust);
            WriteRules.Kind(e.Kind);
            WriteRules.Kind(e.RequestedKind);
            WriteRules.Author(e.Author);
            WriteRules.SubjectShapeOnly(e.SubjectId);
            WriteRules.Content(e.ContentMd);
            Fields(e);
            Realms(e.RealmJson);
            Evidence(e.EvidenceJson, level);
            Position(e.PositionJson);
            Dates(e);
        }
        catch (McpException ex)
        {
            throw new KnowledgeException($"{file}: {ex.Message.Replace(" Nothing was written.", "", StringComparison.Ordinal)} Nothing was restored.", ex);
        }
    }

    private static void Fields(KnowledgeEntry e)
    {
        var (field, value) = WriteRules.FieldValue(e.Field, e.Value);
        if (field != e.Field || value != e.Value) throw new McpException("field and value must not have spaces at either end.");
    }

    private static void Realms(string json)
    {
        string[]? names;
        try { names = JsonSerializer.Deserialize<string[]>(json); }
        catch (JsonException) { throw new McpException("realm_json is not a JSON list of realm names."); }
        if (names is null || names.Length == 0 || names.Any(n => n is null)) throw new McpException("realm_json is not a JSON list of realm names.");
        if (!WriteRules.Realms(string.Join(',', names)).SequenceEqual(names)) throw new McpException("realm_json holds names the realm rule would change.");
    }

    private static void Evidence(string json, Trust level)
    {
        EntryEvidence? evidence;
        try { evidence = JsonSerializer.Deserialize<EntryEvidence>(json); }
        catch (JsonException) { throw new McpException("evidence_json is not valid evidence."); }
        if (evidence is null) throw new McpException("evidence_json is not valid evidence.");
        WriteRules.Evidence(evidence, level);
    }

    private static void Position(string? json)
    {
        if (json is null) return;
        EntryPosition? position;
        try { position = JsonSerializer.Deserialize<EntryPosition>(json); }
        catch (JsonException) { throw new McpException("position_json is not a valid position."); }
        if (position is null) throw new McpException("position_json is not a valid position.");
        WriteRules.Position(position);
    }

    private static void Dates(KnowledgeEntry e)
    {
        if (!Timestamp().IsMatch(e.CreatedAt) || !DateTimeOffset.TryParse(e.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
            throw new McpException("created_at must look like 2026-01-31T12:00:00Z.");
        if ((e.UndoneAt is null) != (e.UndoneBy is null)) throw new McpException("undone_at and undone_by go together.");
        if (e.UndoneAt is not null)
        {
            if (!Timestamp().IsMatch(e.UndoneAt)) throw new McpException("undone_at must look like 2026-01-31T12:00:00Z.");
            if (e.UndoneBy!.Length is 0 or > MaxByLength || e.UndoneBy.Any(char.IsControl)) throw new McpException("undone_by must be 1 to 100 characters with no control characters.");
        }
        if (e.SupersedesId is { } s && (s <= 0 || s >= e.Id)) throw new McpException("supersedes_id must be the id of an earlier entry.");
    }
}
