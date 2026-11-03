using System.Text;

namespace CoaMcp.Server;

public sealed record PageSection(string Name, string Text);

/// <summary>Splits a page body on <c>## </c> headings. Only a heading at the very start of a line counts: indented lines
/// and anything inside a fenced code block are content.</summary>
public static class PageSections
{
    /// <returns>The text before the first heading, and the sections in page order (a repeated name yields several).</returns>
    public static (string Preamble, IReadOnlyList<PageSection> Sections) Split(string body)
    {
        var preamble = new StringBuilder();
        var sections = new List<(string Name, StringBuilder Text)>();
        string? fence = null;
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var marker = line.StartsWith("```", StringComparison.Ordinal) ? "```"
                : line.StartsWith("~~~", StringComparison.Ordinal) ? "~~~" : null;
            if (marker is not null && (fence is null || fence == marker))
                fence = fence is null ? marker : null;

            if (marker is null && fence is null && line.StartsWith("## ", StringComparison.Ordinal))
                sections.Add((line[3..].Trim(), new StringBuilder().Append(line).Append('\n')));
            else if (sections.Count == 0)
                preamble.Append(line).Append('\n');
            else
                sections[^1].Text.Append(line).Append('\n');
        }
        return (preamble.ToString().TrimEnd('\n'),
            sections.Select(s => new PageSection(s.Name, s.Text.ToString().TrimEnd('\n'))).ToList());
    }
}
