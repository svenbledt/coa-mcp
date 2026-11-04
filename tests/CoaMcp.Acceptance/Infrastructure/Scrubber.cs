using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CoaMcp.Acceptance;

/// <summary>Removes secrets and this machine's paths from anything that goes in the report. Every text the report holds passes through here when it is written.</summary>
public sealed partial class Scrubber
{
    public const string Mask = "[TOKEN SCRUBBED]";

    /// <summary>This run's token and this machine's paths.</summary>
    public static Scrubber ForRun { get; } = new(new[] { Settings.Token }, Settings.LocalRoots);

    private readonly List<string> _secrets = new();
    private readonly IReadOnlyList<string> _roots;

    public Scrubber(IEnumerable<string?> secrets, IEnumerable<string>? localRoots = null)
    {
        foreach (var s in secrets.OfType<string>().Where(s => s.Length > 0))
        {
            _secrets.Add(s);
            _secrets.Add(Uri.EscapeDataString(s));
            _secrets.Add(JsonSerializer.Serialize(s)[1..^1]);
            _secrets.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(s)));
            _secrets.Add(Convert.ToHexStringLower(Encoding.UTF8.GetBytes(s)));
        }
        _secrets.Sort((a, b) => b.Length.CompareTo(a.Length));
        _roots = (localRoots ?? Array.Empty<string>()).OrderByDescending(r => r.Length).ToList();
    }

    public string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var result = text;
        foreach (var s in _secrets) result = result.Replace(s, Mask, StringComparison.OrdinalIgnoreCase);
        // Whatever follows a Bearer scheme in a captured header is a credential, right or wrong.
        result = BearerValue().Replace(result, "$1" + Mask);
        foreach (var root in _roots)
        {
            result = result.Replace(root, "<local>", StringComparison.OrdinalIgnoreCase);
            result = result.Replace(root.Replace('\\', '/'), "<local>", StringComparison.OrdinalIgnoreCase);
            result = result.Replace(root.Replace(@"\", @"\\"), "<local>", StringComparison.OrdinalIgnoreCase);
        }
        return result;
    }

    [GeneratedRegex(@"(Authorization\W{1,4}Bearer\s+)[^\s""',}\]]+", RegexOptions.IgnoreCase)]
    private static partial Regex BearerValue();
}
