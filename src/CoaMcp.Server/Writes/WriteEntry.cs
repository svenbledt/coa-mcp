using System.Text.Json.Serialization;
using CoaMcp.Core;

namespace CoaMcp.Server;

/// <summary>A validated write, ready to store. Only <see cref="WriteRules"/> builds these from caller input.</summary>
public sealed record NewEntry(
    string SubjectId, string Kind, IReadOnlyList<string> Realms, Trust Trust, string ContentMd,
    string EvidenceJson, string? Field, string? Value, string? PositionJson, string Author, string? RequestedKind = null);

/// <summary>One stored entry. <paramref name="SupersededBy"/> is the next live page version, worked out when read. <paramref name="Unreadable"/> means its stored data is damaged: only its id and undo state are real.</summary>
public sealed record WriteEntry(
    long Id, string SubjectId, string Kind, IReadOnlyList<string> Realms, Trust Trust, string ContentMd,
    string EvidenceJson, string? Field, string? Value, string? PositionJson, string Author, string CreatedAt,
    long? SupersedesId, string? UndoneAt, string? UndoneBy, long? SupersededBy = null, string? RequestedKind = null, bool Unreadable = false);

/// <summary>The evidence argument of write_entry, and the shape of evidence_json.</summary>
public sealed record EntryEvidence(
    [property: JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description = null,
    [property: JsonPropertyName("url"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Url = null,
    [property: JsonPropertyName("timestamp"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Timestamp = null,
    [property: JsonPropertyName("video"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Video = null);

/// <summary>The position argument of write_entry, and the shape of position_json. Always shown as approximate.</summary>
public sealed record EntryPosition(
    [property: JsonPropertyName("map")] string? Map = null,
    [property: JsonPropertyName("x"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? X = null,
    [property: JsonPropertyName("y"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Y = null,
    [property: JsonPropertyName("z"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Z = null,
    [property: JsonPropertyName("leftPct"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? LeftPct = null,
    [property: JsonPropertyName("topPct"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? TopPct = null);

/// <summary>
/// One distinct value of a structured field on a subject, with the best entry that gives it: the shape conflict checks work from, without any entry's content.
/// <paramref name="SameValue"/> entries give this value and <paramref name="DistinctValues"/> values exist for the field, of which only the first few are read.
/// </summary>
public sealed record FieldClaim(long Id, string Field, string Value, Trust Trust, string Author, IReadOnlyList<string> Realms, int SameValue, int DistinctValues);

/// <summary>What get shows for one subject. <paramref name="ClaimsTruncated"/> says some structured values were not read, so a conflict could be missing.</summary>
public sealed record SubjectEntries(
    IReadOnlyList<WriteEntry> Page, int Live, int Undone, int Superseded,
    IReadOnlyList<long> HiddenIds, IReadOnlyList<FieldClaim> Claims, bool ClaimsTruncated);

public sealed record EntryHit(WriteEntry Entry, string Snippet);

public sealed record EntryReceipt(long Id, long? SupersedesId);

public sealed record UndoReceipt(long Id, string UndoneAt, string UndoneBy, bool AlreadyUndone);

public sealed record StoreCounts(int Live, int Observed, int Memory, int Undone, int Superseded, bool Exists, string? Problem, string? WriteProblem, int Damaged = 0);
