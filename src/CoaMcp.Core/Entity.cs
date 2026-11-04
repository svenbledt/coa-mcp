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

/// <summary>An archive whose size on the client's disk differs from what the launcher or release file lists. <paramref name="PreviousBytes"/> is the release file's size for the previous package, if it gives one.</summary>
public sealed record ClientArchiveMismatch(string Archive, long ListedBytes, long ActualBytes, long? PreviousBytes);

/// <summary>
/// Which client a layer describes, as the launcher's state file or, without one, the client's release file claims it: nothing here is checked against the archives except the sizes in <paramref name="Mismatches"/>.
/// <paramref name="Note"/> says where that file and the client disagree. <paramref name="Launcher"/> is the launcher's version, when that is the source.
/// </summary>
public sealed record ClientRelease(int? Revision, string ReleaseId, string? Note, IReadOnlyList<ClientArchiveMismatch>? Mismatches = null, string? Launcher = null)
{
    /// <summary>The client, worded as the claim it is: "client 2026-10-30-01 (launcher)", or "per CLIENT-RELEASE.json, client revision 8" without a launcher state file.</summary>
    public string Claim => Launcher is not null ? $"client {Launcher} (launcher)" : $"per CLIENT-RELEASE.json, client revision {Revision}";

    /// <summary>Source text that also names the client: "CoA client DBC, client 2026-10-30-01 (launcher)".</summary>
    public static string Source(string source, ClientRelease? client) => client is null ? source : $"{source}, {client.Claim}";

    /// <summary>The line an answer adds when this archive differs from the launcher's or release file's entry, or null when it doesn't.</summary>
    public string? MismatchLine(string? archive)
    {
        var m = archive is null ? null : Mismatches?.FirstOrDefault(x => string.Equals(x.Archive, archive, StringComparison.OrdinalIgnoreCase));
        if (m is null) return null;
        if (Launcher is not null) return $"{m.Archive} on disk ({Mb(m.ActualBytes)}) doesn't match the launcher's entry ({Mb(m.ListedBytes)}), so this copy isn't exactly client {Launcher}.";
        var head = $"{m.Archive} on disk ({Mb(m.ActualBytes)}) doesn't match the release file's entry ({Mb(m.ListedBytes)})";
        return m.PreviousBytes == m.ActualBytes
            ? head + "; it matches the previous package's entry."
            : $"{head}, so it may be newer than revision {Revision}.";
    }

    private static string Mb(long bytes) => (bytes / 1_000_000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB";
}
