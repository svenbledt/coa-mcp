using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace CoaMcp.Pipeline.Notes;

/// <summary>Where page HTML comes from. The import fetches; parsing is separate so tests need no network.</summary>
public interface IPageSource
{
    string Fetch(string url);
}

/// <summary>Reads public pages of the project site over HTTPS. Redirects are not followed, so a page can't send the import elsewhere.</summary>
public sealed class HttpPageSource : IPageSource, IDisposable
{
    private const int MaxBytes = 2 * 1024 * 1024;
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(30) };

    public HttpPageSource() => _http.DefaultRequestHeaders.UserAgent.ParseAdd("CoaMcp-notes-import/1.0");

    public string Fetch(string url)
    {
        if (!ProjectPages.IsAllowedUrl(url)) throw new InvalidDataException($"Refusing to fetch '{url}': not a public project page.");
        using var response = _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        if (response.StatusCode != HttpStatusCode.OK) throw new InvalidDataException($"{url} answered {(int)response.StatusCode}; expected 200 (redirects are not followed).");
        if (response.Content.Headers.ContentLength > MaxBytes) throw new InvalidDataException($"{url} is larger than {MaxBytes / 1024} KB.");
        using var stream = response.Content.ReadAsStream();
        var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int n;
        while ((n = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, n);
            if (buffer.Length > MaxBytes) throw new InvalidDataException($"{url} is larger than {MaxBytes / 1024} KB.");
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Turns the listed sections of a project page into plain Markdown, and decides which pages may be read at all.</summary>
public static class ProjectPages
{
    public const string Host = "coa-development.org";

    public const string NotEvidence = "This page describes the Conquest of AzerothCore emulator project. It isn't evidence about Ascension.";

    /// <summary>First path segments that are never read: accounts, downloads, character data, the emulator's world database, live counters, bug reports.</summary>
    private static readonly HashSet<string> Denied = new(StringComparer.OrdinalIgnoreCase)
    {
        "account", "login", "logout", "register", "signup", "download", "downloads", "api", "armory", "database", "bugs", "bug-media",
        "status", "statistics", "shop", "admin", "profile", "u", "user", "users", "character", "characters", "guild", "guilds",
    };

    public static bool IsAllowedUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.Query)) return false;
        var first = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first is null || !Denied.Contains(first);
    }

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(2);
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant;

    private static readonly Regex Dropped = new(@"<(script|style|noscript|svg|template|button|form|select|textarea|nav|footer)\b.*?</\1\s*>|<!--.*?-->", Opts, Limit);
    private static readonly Regex Heading = new(@"<h([1-6])\b[^>]*>", Opts, Limit);
    private static readonly Regex HeadingEnd = new(@"</h[1-6]\s*>", Opts, Limit);
    private static readonly Regex Summary = new(@"<summary\b[^>]*>", Opts, Limit);
    private static readonly Regex SummaryEnd = new(@"</summary\s*>", Opts, Limit);
    private static readonly Regex ListItem = new(@"<li\b[^>]*>", Opts, Limit);
    private static readonly Regex Block = new(@"</?(p|div|section|ul|ol|details|tr|table|dl|dt|dd|li|br|article|header)\b[^>]*>", Opts, Limit);
    private static readonly Regex AnyTag = new(@"<[^>]*>", Opts, Limit);
    private static readonly Regex Spaces = new(@"[ \t ]+", Opts, Limit);
    private static readonly Regex BlankLines = new(@"\n{3,}", Opts, Limit);
    private static readonly Regex HiddenOpen = new(@"<([a-z][a-z0-9]*)\b[^>]*\shidden(?=[\s=>/])[^>]*>", Opts, Limit);
    private static readonly Regex ButtonLink = new(@"<a\b[^>]*>([^<]*)</a\s*>", Opts, Limit);
    private static readonly Regex SectionTag = new(@"<(/?)section\b[^>]*>", Opts, Limit);

    /// <summary>The Markdown of the sections with these ids, in the order listed. A missing section fails: the page changed and the config needs a look.</summary>
    public static string Parse(string html, IReadOnlyList<string> sections, string pageName)
    {
        var parts = new List<string>();
        foreach (var id in sections)
        {
            var inner = Section(html, id) ?? throw new InvalidDataException($"Project page '{pageName}' has no section with id '{id}'; the page changed, review it and update config/notes.json.");
            parts.Add(ToMarkdown(inner));
        }
        return string.Join("\n\n", parts.Where(p => p.Length > 0)).Trim();
    }

    private static string? Section(string html, string id)
    {
        var open = new Regex(@"<section\b[^>]*\bid\s*=\s*[""']" + Regex.Escape(id) + @"[""'][^>]*>", Opts, Limit).Match(html);
        if (!open.Success) return null;
        var depth = 1;
        for (var m = SectionTag.Match(html, open.Index + open.Length); m.Success; m = m.NextMatch())
        {
            depth += m.Groups[1].Value.Length == 0 ? 1 : -1;
            if (depth == 0) return html[(open.Index + open.Length)..m.Index];
        }
        return null;
    }

    /// <summary>Removes elements the page itself hides (the hidden attribute); the site shows one wording per realm kind and hides the other.</summary>
    private static string RemoveHidden(string html)
    {
        while (true)
        {
            var open = HiddenOpen.Match(html);
            if (!open.Success) return html;
            var tag = open.Groups[1].Value;
            var openEnd = open.Index + open.Length;
            var end = openEnd;
            if (!open.Value.EndsWith("/>", StringComparison.Ordinal) && !VoidTags.Contains(tag.ToLowerInvariant()))
            {
                end = html.Length;   // an element that never closes is dropped to the end of the section
                var depth = 1;
                var same = new Regex(@"<(/?)" + Regex.Escape(tag) + @"\b[^>]*>", Opts, Limit);
                for (var m = same.Match(html, openEnd); m.Success; m = m.NextMatch())
                {
                    depth += m.Groups[1].Value.Length == 0 ? 1 : -1;
                    if (depth == 0) { end = m.Index + m.Length; break; }
                }
            }
            html = html[..open.Index] + " " + html[end..];
        }
    }

    private static bool IsButtonLabel(string text) => text.Trim().Contains(' ') && !text.Any(char.IsLower);

    private static readonly HashSet<string> VoidTags = new() { "br", "img", "input", "hr", "meta", "link", "source" };

    private static string ToMarkdown(string html)
    {
        var s = Dropped.Replace(RemoveHidden(html), " ");
        // A link whose text is several words in capitals is a button label ("JOIN DISCORD"), not something the page says. A lone acronym ("FAQ") stays.
        s = ButtonLink.Replace(s, m => IsButtonLabel(m.Groups[1].Value) ? " " : m.Groups[1].Value);
        s = Heading.Replace(s, m => "\n\n" + new string('#', Math.Max(2, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))) + " ");
        s = HeadingEnd.Replace(s, "\n\n");
        s = Summary.Replace(s, "\n\n### ");
        s = SummaryEnd.Replace(s, "\n");
        s = ListItem.Replace(s, "\n- ");
        s = Block.Replace(s, "\n");
        s = AnyTag.Replace(s, "");
        s = WebUtility.HtmlDecode(s).Replace("\r", "");
        var lines = s.Split('\n').Select(l => Spaces.Replace(l, " ").Trim());
        return BlankLines.Replace(string.Join("\n", lines), "\n\n").Trim();
    }
}
