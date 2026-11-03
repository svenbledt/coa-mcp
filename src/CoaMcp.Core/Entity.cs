namespace CoaMcp.Core;

public sealed record Link(string ToId, string Kind);

public sealed record Entity(
    string Id,
    string Type,
    string Title,
    RealmSet Realm,
    Trust Trust,
    string Source,
    DateOnly SourceDate,
    string BodyMd,
    string? DataJson,
    IReadOnlyList<Link> Links);

public sealed record SearchHit(string Id, string Type, string Title, string Snippet, double Rank);

public sealed record LayerMeta(string Name, string Version, DateTimeOffset BuiltAt, DateOnly SourceDate);
