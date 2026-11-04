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

/// <summary>An archive whose size on the client's disk differs from what the release file lists. <paramref name="PreviousBytes"/> is the release file's size for the previous package, if it gives one.</summary>
public sealed record ClientArchiveMismatch(string Archive, long ListedBytes, long ActualBytes, long? PreviousBytes);

/// <summary>
/// Which client a layer describes, as the client's release file claims it: nothing here is checked against the archives except the sizes in <paramref name="Mismatches"/>.
/// <paramref name="Note"/> says where that file and the client disagree.
/// </summary>
public sealed record ClientRelease(int Revision, string ReleaseId, string? Note, IReadOnlyList<ClientArchiveMismatch>? Mismatches = null)
{
    /// <summary>The revision, worded as the release file's claim: "per CLIENT-RELEASE.json, client revision 8".</summary>
    public string Claim => $"per CLIENT-RELEASE.json, client revision {Revision}";

    /// <summary>Source text that also names the client: "CoA client DBC, per CLIENT-RELEASE.json, client revision 8".</summary>
    public static string Source(string source, ClientRelease? client) => client is null ? source : $"{source}, {client.Claim}";

    /// <summary>The line an answer adds when this archive differs from the release file, or null when it doesn't.</summary>
    public string? MismatchLine(string? archive)
    {
        var m = archive is null ? null : Mismatches?.FirstOrDefault(x => string.Equals(x.Archive, archive, StringComparison.OrdinalIgnoreCase));
        if (m is null) return null;
        var head = $"{m.Archive} on disk ({Mb(m.ActualBytes)}) doesn't match the release file's entry ({Mb(m.ListedBytes)})";
        return m.PreviousBytes == m.ActualBytes
            ? head + "; it matches the previous package's entry."
            : $"{head}, so it may be newer than revision {Revision}.";
    }

    private static string Mb(long bytes) => (bytes / 1_000_000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB";
}
