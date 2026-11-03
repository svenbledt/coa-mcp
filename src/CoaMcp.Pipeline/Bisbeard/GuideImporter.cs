using System.Globalization;
using System.Text.Json;
using CoaMcp.Core;

namespace CoaMcp.Pipeline.Bisbeard;

/// <summary>Imports guide pages listed in config/bisbeard-guides.json. A page missing from the config fails the build.</summary>
public static class GuideImporter
{
    public sealed record Guide(string Slug, string Text, IReadOnlyList<string> Realms);

    private static readonly JsonSerializerOptions ConfigOptions = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<Guide> Load(string dataDir, string configPath)
    {
        var config = JsonSerializer.Deserialize<Dictionary<string, GuideRule>>(File.ReadAllText(configPath), ConfigOptions)
            ?? throw new InvalidDataException($"Empty guide config: {configPath}");
        var files = Directory.GetFiles(Path.Combine(dataDir, "guides_text"), "*.txt").OrderBy(f => f, StringComparer.Ordinal).ToList();

        var unknown = files.Select(f => Path.GetFileNameWithoutExtension(f)).Where(s => !config.ContainsKey(s)).ToList();
        if (unknown.Count > 0)
            throw new InvalidOperationException("Guide pages not in the guide config (review each, then add as realms or excluded): "
                + string.Join(", ", unknown));

        var guides = new List<Guide>();
        foreach (var file in files)
        {
            var slug = Path.GetFileNameWithoutExtension(file);
            var rule = config[slug];
            if (!string.IsNullOrWhiteSpace(rule.Excluded)) continue;
            if (rule.Realms is not { Count: > 0 })
                throw new InvalidOperationException($"Guide '{slug}' has neither realms nor an exclusion reason.");
            guides.Add(new Guide(slug, File.ReadAllText(file).Replace("\r\n", "\n").Trim(), rule.Realms));
        }
        return guides;
    }

    public static int Import(IReadOnlyList<Guide> guides, LayerWriter w)
    {
        foreach (var g in guides)
        {
            w.Add(new Entity("guide:" + g.Slug, "guide", Title(g.Slug), BisbeardImport.Unproven(g.Realms), Trust.Derived,
                "BisBeard guide page " + g.Slug, BisbeardImport.ArchiveDate,
                g.Text + "\n\n" + BisbeardImport.Credit + "\n", null, Array.Empty<Link>()));
        }
        return guides.Count;
    }

    private static string Title(string slug) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(slug.Replace('-', ' '));

    private sealed class GuideRule
    {
        public List<string>? Realms { get; set; }
        public string? Excluded { get; set; }
    }
}
