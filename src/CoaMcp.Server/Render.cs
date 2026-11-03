using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CoaMcp.Core;

namespace CoaMcp.Server;

/// <summary>Turns layer data into the short plain text agents read. Every fact carries its realm, trust and source.</summary>
public static class Render
{
    private const int MaxLinks = 20;
    private const int MaxListedSections = 100;
    private const int PreambleChars = 600;
    private const int SnippetChars = 160;

    public static string Trust(Trust t) => t.ToString().ToLowerInvariant();

    public static string RealmText(RealmSet r) => $"{r.Label()} ({(r.Proven ? "proven" : "unproven")})";

    /// <summary>One line, no control characters, for text that came from a caller or from stored data.</summary>
    public static string OneLine(string s, int max)
    {
        var flat = Regex.Replace(s, @"[\p{C}\s]+", " ").Trim();
        return flat.Length <= max ? flat : flat[..max] + "...";
    }

    public static string Header(Entity e, LayerMeta layer) =>
        $"# {OneLine(e.Title, 200)}\n" +
        $"id: {e.Id} | type: {e.Type} | layer: {layer.Name} v{layer.Version} (built {layer.BuiltAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)})\n" +
        $"realm: {RealmText(e.Realm)} | trust: {Trust(e.Trust)} | source: {e.Source} | source date: {e.SourceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";

    /// <summary>The page for <paramref name="e"/>. With sections: just those. Without: the whole page, unless it is large and has sections, then the section list.</summary>
    public static string Entity(Entity e, LayerMeta layer, IReadOnlyList<string>? sections)
    {
        var sb = new StringBuilder(Header(e, layer)).Append("\n\n");
        var (preamble, parts) = PageSections.Split(e.BodyMd);

        if (sections is { Count: > 0 })
        {
            if (parts.Count == 0)
                sb.Append("This page has no sections, so the whole page follows.\n\n").Append(e.BodyMd.TrimEnd());
            else
                AppendRequested(sb, parts, sections);
        }
        else if (Encoding.UTF8.GetByteCount(e.BodyMd) > Limits.LargePageBytes && parts.Count > 0)
        {
            sb.Append($"This page is large ({Encoding.UTF8.GetByteCount(e.BodyMd) / 1024} KB). Ask for sections to read it: get(id, sections=[names]).\n");
            if (preamble.Length > 0) sb.Append('\n').Append(preamble.Length > PreambleChars ? preamble[..PreambleChars] + "..." : preamble).Append('\n');
            sb.Append("\nsections:\n");
            foreach (var p in parts.Take(MaxListedSections))
                sb.Append($"- {OneLine(p.Name, 100)} ({Encoding.UTF8.GetByteCount(p.Text) / 1024.0:0.#} KB)\n");
            if (parts.Count > MaxListedSections) sb.Append($"- and {parts.Count - MaxListedSections} more\n");
        }
        else
        {
            sb.Append(e.BodyMd.TrimEnd());
        }

        if (e.Links.Count > 0)
        {
            sb.Append($"\n\nlinks ({e.Links.Count}): ")
              .Append(string.Join(", ", e.Links.Take(MaxLinks).Select(l => $"{l.Kind} {l.ToId}")));
            if (e.Links.Count > MaxLinks) sb.Append($", and {e.Links.Count - MaxLinks} more");
        }
        return sb.ToString().TrimEnd();
    }

    private static void AppendRequested(StringBuilder sb, IReadOnlyList<PageSection> parts, IReadOnlyList<string> requested)
    {
        var available = string.Join(", ", parts.Select(p => p.Name).Distinct().Take(MaxListedSections).Select(n => OneLine(n, 100)));
        var first = true;
        foreach (var name in requested.Distinct())
        {
            var found = parts.Where(p => p.Name == name).ToList();
            if (!first) sb.Append("\n\n");
            first = false;
            if (found.Count == 0)
                sb.Append($"No section named '{OneLine(name, 100)}'. Available sections: {available}.");
            else
                sb.Append(string.Join("\n\n", found.Select(f => f.Text)));
        }
    }

    private const int MaxZones = 6;

    /// <summary>One search hit: realm, trust and id up front, then title, layer, source and source date.</summary>
    public static string Hit(int number, LayerHit h)
    {
        var sb = new StringBuilder($"{number}. [{h.Realm.Label()} | {Trust(h.Trust)} | {h.Id}] {OneLine(h.Title, 120)} " +
            $"({h.LayerName}; src: {OneLine(h.Source, 40)}, {h.SourceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)})");
        if (h.Count > 1)
        {
            var zones = (h.Zones ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            sb.Append($" - {h.Count} {h.Type}s share this title (id shown is the first)");
            if (zones.Length > 0)
                sb.Append("; zones: ").Append(string.Join(", ", zones.Take(MaxZones).Select(z => OneLine(z, 40))))
                  .Append(zones.Length > MaxZones ? $" and {zones.Length - MaxZones} more" : "");
        }
        else if (h.Snippet.Length > 0)
            sb.Append(" - ").Append(OneLine(h.Snippet, SnippetChars));
        return sb.ToString();
    }
}
