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

public sealed record LayerMeta(string Name, string Version, DateTimeOffset BuiltAt, DateOnly SourceDate, ClientRelease? Client = null);

/// <summary>Which client a layer describes, as the client's release file states it. <paramref name="Note"/> says where that file and the client disagree.</summary>
public sealed record ClientRelease(int Revision, string ReleaseId, string? Note)
{
    /// <summary>Source text that also names the client: "CoA client DBC, client revision 8".</summary>
    public static string Source(string source, ClientRelease? client) => client is null ? source : $"{source}, client revision {client.Revision}";
}
