namespace CoaMcp.Server;

/// <summary>Caps for everything a caller can size. A negative SQLite LIMIT means unlimited, so nothing reaches SQL unclamped.</summary>
public static class Limits
{
    public const int DefaultLimit = 10;
    public const int MaxLimit = 100;
    public const int MaxOffset = 1_000;
    public const int MaxQueryChars = 200;
    public const int MaxIdChars = 200;
    public const int MaxSections = 20;
    public const int LargePageBytes = 8 * 1024;

    /// <summary>How long one DBC row query may run before SQLite stops it. Config Dbc:QueryBudgetMs overrides it.</summary>
    public const int DbcQueryBudgetMs = 2000;

    /// <returns>The clamped values and a note describing any cap that was applied (null when none).</returns>
    public static (int Limit, int Offset, string? Note) Page(long limit, long offset) => Page(limit, offset, MaxOffset);

    /// <summary>As <see cref="Page(long, long)"/>, for lists that can run to many thousands of rows and need a deeper offset.</summary>
    public static (int Limit, int Offset, string? Note) Page(long limit, long offset, int maxOffset)
    {
        var notes = new List<string>();
        var l = limit;
        if (l < 1) { l = 1; notes.Add($"limit {limit} raised to 1"); }
        else if (l > MaxLimit) { l = MaxLimit; notes.Add($"limit {limit} capped at {MaxLimit}"); }
        var o = offset;
        if (o < 0) { o = 0; notes.Add($"offset {offset} raised to 0"); }
        else if (o > maxOffset) { o = maxOffset; notes.Add($"offset {offset} capped at {maxOffset}"); }
        return ((int)l, (int)o, notes.Count == 0 ? null : "Note: " + string.Join("; ", notes) + ".");
    }
}
