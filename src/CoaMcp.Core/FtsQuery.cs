namespace CoaMcp.Core;

/// <summary>A safe FTS5 MATCH expression. Only built from user text via <see cref="FromUser"/>.</summary>
public sealed record FtsQuery
{
    public string Match { get; }
    public bool IsEmpty => Match.Length == 0;

    private FtsQuery(string match) => Match = match;

    /// <summary>Quotes every whitespace-separated term as an FTS5 phrase (implicit AND), so operators lose their meaning.</summary>
    public static FtsQuery FromUser(string? text)
    {
        var terms = (text ?? "")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => "\"" + t.Replace("\"", "\"\"") + "\"");
        return new FtsQuery(string.Join(' ', terms));
    }
}
