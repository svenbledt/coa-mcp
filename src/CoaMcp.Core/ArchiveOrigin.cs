namespace CoaMcp.Core;

/// <summary>What an archive's origin may be said to mean. Every answer words origin ascension through here, so it never reads as checked against live.</summary>
public static class ArchiveOrigin
{
    public const string Ascension = "ascension";
    public const string Project = "project";

    /// <summary>The project's records say the archive is Ascension's. The copy on this client was never compared with live.</summary>
    public const string AscensionNote = "Ascension's archive per project records; this copy may lag live";

    /// <summary>The origin as an answer shows it beside an archive's name.</summary>
    public static string Label(string? origin) => origin == Ascension ? AscensionNote : origin ?? "unknown";
}
