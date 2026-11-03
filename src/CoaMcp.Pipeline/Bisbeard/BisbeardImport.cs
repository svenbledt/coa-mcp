using CoaMcp.Core;

namespace CoaMcp.Pipeline.Bisbeard;

/// <summary>Builds the bisbeard layer from the archived BisBeard data on disk. Never touches the network.</summary>
public static class BisbeardImport
{
    public const string Credit = "Source: BisBeard (structured from Conquest of AzerothCore project data).";

    /// <summary>Date the site archive was captured (README in the data folder).</summary>
    public static readonly DateOnly ArchiveDate = new(2026, 11, 1);

    public static readonly IReadOnlyList<string> MainRealms = new[] { "coa", "classic", "s10" };

    public static RealmSet Unproven(IEnumerable<string> realms) => new(realms.ToList(), false);

    public static RealmSet Proven(string realm) => new(new[] { realm }, true);

    public static (int Items, int Classes, int Specs, int Markers, int Guides, int Talents, int Rules) Run(string dataDir, string guidesConfig, string outPath)
    {
        // Validate the guide config before touching the output file.
        var guides = GuideImporter.Load(dataDir, guidesConfig);

        using var w = new LayerWriter(outPath, "bisbeard", "1");
        var items = ItemImporter.Import(dataDir, w);
        var (classes, specs, talents, rules) = RealmImporter.Import(dataDir, w);
        var markers = MarkerImporter.Import(dataDir, w);
        var guideCount = GuideImporter.Import(guides, w);
        w.Finish();
        return (items, classes, specs, markers, guideCount, talents, rules);
    }
}

internal static class StringBuilderExtensions
{
    /// <summary>Appends a line ending in "\n" on every platform, so the layer does not depend on where it was built.</summary>
    public static System.Text.StringBuilder Line(this System.Text.StringBuilder sb, string text = "") => sb.Append(text).Append('\n');
}
