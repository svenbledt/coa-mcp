using System.Text.Json.Serialization;
using CoaMcp.Core;

namespace CoaMcp.Server;

/// <summary>A validated write, ready to store. Only <see cref="WriteRules"/> builds these from caller input.</summary>
public sealed record NewEntry(
    string SubjectId, string Kind, IReadOnlyList<string> Realms, Trust Trust, string ContentMd,
    string EvidenceJson, string? Field, string? Value, string? PositionJson, string Author);

/// <summary>One stored entry. <paramref name="SupersededBy"/> is the next live page version, worked out when read.</summary>
public sealed record WriteEntry(
    long Id, string SubjectId, string Kind, IReadOnlyList<string> Realms, Trust Trust, string ContentMd,
    string EvidenceJson, string? Field, string? Value, string? PositionJson, string Author, string CreatedAt,
    long? SupersedesId, string? UndoneAt, string? UndoneBy, long? SupersededBy = null);

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

/// <summary>What get shows for one subject. <paramref name="Fields"/> are all live entries with a structured field, for conflict checks.</summary>
public sealed record SubjectEntries(
    IReadOnlyList<WriteEntry> Page, int Live, int Undone, int Superseded,
    IReadOnlyList<long> HiddenIds, IReadOnlyList<WriteEntry> Fields);

public sealed record EntryHit(WriteEntry Entry, string Snippet);

public sealed record EntryReceipt(long Id, long? SupersedesId);

public sealed record UndoReceipt(long Id, string UndoneAt, string UndoneBy, bool AlreadyUndone);

public sealed record StoreCounts(int Live, int Observed, int Memory, int Undone, int Superseded, bool Exists, string? Problem, string? WriteProblem);
