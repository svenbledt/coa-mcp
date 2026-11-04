using System.Text.RegularExpressions;

namespace CoaMcp.Pipeline.Notes;

public sealed record SecretHit(int Line, string Kind);

/// <summary>
/// Looks for things that must never be published in a note or project page: credentials, keys, local paths and email addresses.
/// A hit fails the build and names the file, line and kind. The matched text is never reported.
/// </summary>
public static partial class SecretScan
{
    private static readonly (string Kind, Regex Pattern)[] Patterns =
    {
        ("bearer token", BearerToken()),
        ("API key or access token", ApiKey()),
        ("private key header", PrivateKey()),
        ("password in an assignment", Password()),
        ("local drive path", DrivePath()),
        ("network (UNC) path", UncPath()),
        ("email address", Email()),
    };

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9._~+/=-]{16,}", RegexOptions.IgnoreCase)]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"(?:\b(?:api[_-]?key|apikey|access[_-]?token|auth[_-]?token|secret[_-]?key|client[_-]?secret)\b\s*[:=]\s*\S{8,})|(?:\b(?:sk|pk|rk)-[A-Za-z0-9_-]{16,})|(?:\b(?:ghp|gho|ghs|ghu|github_pat)_[A-Za-z0-9_]{16,})|(?:\bxox[abprs]-[A-Za-z0-9-]{10,})|(?:\bAKIA[0-9A-Z]{16}\b)", RegexOptions.IgnoreCase)]
    private static partial Regex ApiKey();

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY( BLOCK)?-----")]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"\b(?:[A-Za-z_]*pass(?:word|wd)?|pwd|secret)\b\s*[:=]\s*[^\s,;)]{4,}", RegexOptions.IgnoreCase)]
    private static partial Regex Password();

    [GeneratedRegex(@"(?<![A-Za-z0-9_/\.])[A-Za-z]:(?:\\|/(?=[A-Za-z0-9_.$ -]))")]
    private static partial Regex DrivePath();

    [GeneratedRegex(@"(?<![\\\w:.])\\\\[A-Za-z0-9_.$-]+\\[A-Za-z0-9_.$-]+")]
    private static partial Regex UncPath();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    public static IReadOnlyList<SecretHit> Find(string text)
    {
        var hits = new List<SecretHit>();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            foreach (var (kind, pattern) in Patterns)
                if (pattern.IsMatch(lines[i])) hits.Add(new SecretHit(i + 1, kind));
        return hits;
    }

    /// <summary>Throws naming <paramref name="name"/>, the line and the kind of the first hit. Never includes the text itself.</summary>
    public static void ThrowIfAny(string name, string text)
    {
        var hits = Find(text);
        if (hits.Count == 0) return;
        var first = hits[0];
        var more = hits.Count > 1 ? $" ({hits.Count - 1} more hit{(hits.Count > 2 ? "s" : "")})" : "";
        throw new InvalidDataException($"{name} line {first.Line} looks like a {first.Kind}{more}. Remove it from the reviewed text; the matched text is not shown.");
    }
}
