using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CoaMcp.Core;
using CoaMcp.Server.Tools;
using ModelContextProtocol;

namespace CoaMcp.Server;

/// <summary>Everything a write must satisfy. Errors name the rule and never echo what the caller sent.</summary>
public static partial class WriteRules
{
    public const int MaxContentBytes = 32_768;
    public const int MaxAuthorChars = 64;
    public const int MaxFieldChars = 64;
    public const int MaxValueChars = 256;
    public const int MaxDescriptionChars = 1_024;
    public const int MaxUrlChars = 2_048;
    public const int MaxTimestampChars = 32;
    public const double MaxCoordinate = 100_000;

    public static readonly string[] Kinds = { "observation", "note", "correction", "page" };
    public static readonly string[] SubjectTypes = { "npc", "dungeon", "encounter", "mechanic", "spell", "item", "class", "quest", "note", "test", PathTools.SubjectType };
    public static readonly string[] KnownRealms = { "coa", "wotlk", "octo", "classic", "s10" };
    public const string UnknownRealm = "unknown";

    private static readonly string[] VideoHosts = { "youtube.com", "youtu.be", "youtube-nocookie.com", "twitch.tv", "vimeo.com" };
    private static readonly string[] VideoExtensions = { ".mp4", ".webm", ".mov", ".mkv", ".avi", ".m3u8" };
    private static readonly string[] TimeParameters = { "t", "time", "time_continue", "start" };

    [GeneratedRegex(@"\A[A-Za-z0-9 ._-]{1,64}\z")] private static partial Regex AuthorShape();
    [GeneratedRegex(@"\A[a-z-]+:[a-z0-9][a-z0-9._:-]*\z")] private static partial Regex SubjectShape();
    [GeneratedRegex(@"\A[\p{L}\p{N} ._'-]{1,64}\z")] private static partial Regex MapShape();
    /// <summary>Every character found in the 639,153 real client paths, plus the backslash they can be written with.</summary>
    [GeneratedRegex(@"\Apath:[A-Za-z0-9 !&'()+,\-./\\\[\]_]+\z")] private static partial Regex PathShape();
    [GeneratedRegex(@"\A(?=.)(\d{1,5}h)?(\d{1,5}m)?(\d{1,7}s?)?\z")] private static partial Regex DurationShape();
    [GeneratedRegex(@"\A\d{1,3}:\d{2}(:\d{2})?\z")] private static partial Regex ClockShape();

    private static McpException Refuse(string why) => new(why + " Nothing was written.");

    public static string Author(string? author)
    {
        if (author is null || !AuthorShape().IsMatch(author) || author.Trim() != author)
            throw Refuse($"author must be 1 to {MaxAuthorChars} characters: letters, digits, space and . _ - with no space at either end.");
        return author;
    }

    public static string Kind(string? kind) => kind is not null && Kinds.Contains(kind, StringComparer.Ordinal)
        ? kind
        : throw Refuse($"kind must be one of: {string.Join(", ", Kinds)}.");

    /// <summary>Only the two levels a token can claim. The others come only from the pipeline.</summary>
    public static Trust TrustLevel(string? trust) => trust switch
    {
        "observed" => Trust.Observed,
        "memory" => Trust.Memory,
        _ => throw Refuse("trust must be observed or memory. client, derived and cache come only from the pipeline."),
    };

    public static IReadOnlyList<string> Realms(string? realm)
    {
        var names = (realm ?? UnknownRealm).Split(',').Select(r => r.Trim()).Distinct(StringComparer.Ordinal).ToList();
        if (names.SequenceEqual(new[] { UnknownRealm })) return names;
        if (!names.All(n => KnownRealms.Contains(n, StringComparer.Ordinal)))
            throw Refuse($"realm must be {UnknownRealm}, or one or more of {string.Join(", ", KnownRealms)} separated by commas.");
        return names;
    }

    /// <returns>The id to store under, and whether it names generated data.</returns>
    public static (string Id, bool Generated) Subject(string? subject, LayerSet layers)
    {
        var allowed = $"subject must be an existing id, or a new <type>:<slug> in lower case, with type one of: {string.Join(", ", SubjectTypes)}.";
        if (string.IsNullOrEmpty(subject) || subject.Length > Limits.MaxIdChars || TextProblem(subject, multiline: false) is not null) throw Refuse(allowed);

        if (PathTools.IsSubject(subject))
            return PathShape().IsMatch(subject) && PathTools.Find(layers, subject) is { } file
                ? (file.Id, true)
                : throw Refuse("subject names a client file that does not exist here, or has characters no client path has. Check it with client_paths.");
        if (DbcPages.IsRowId(subject))
            return DbcPages.FindRow(layers, subject) is { } row
                ? (row.Id, true)
                : throw Refuse("subject names a DBC row that does not exist here. Check it with get or dbc_rows.");
        if (layers.Get(subject).Count > 0) return (subject, true);

        if (!SubjectShape().IsMatch(subject) || !SubjectTypes.Contains(subject.Split(':')[0], StringComparer.Ordinal)) throw Refuse(allowed);
        return (subject, false);
    }

    /// <summary>
    /// The subject rule without the layers, for restoring entries from the repository: the same limits and shapes, but whether a client file,
    /// DBC row or generated id exists is not checked, because the layers are not there. Returns the id; throws if the shape is wrong.
    /// </summary>
    public static string SubjectShapeOnly(string? subject)
    {
        var allowed = $"subject must be an existing id, or a <type>:<slug> in lower case, with type one of: {string.Join(", ", SubjectTypes)}.";
        if (string.IsNullOrEmpty(subject) || subject.Length > Limits.MaxIdChars || TextProblem(subject, multiline: false) is not null) throw Refuse(allowed);
        if (PathTools.IsSubject(subject))
            return PathShape().IsMatch(subject) ? subject : throw Refuse("subject has characters no client path has.");
        if (DbcPages.IsRowId(subject)) return subject;
        if (!SubjectShape().IsMatch(subject) || !SubjectTypes.Contains(subject.Split(':')[0], StringComparer.Ordinal)) throw Refuse(allowed);
        return subject;
    }

    public static string Content(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) throw Refuse("content is empty.");
        if (TextProblem(content, multiline: true) is { } problem) throw Refuse($"content {problem}.");
        if (Encoding.UTF8.GetByteCount(content) > MaxContentBytes) throw Refuse($"content is over {MaxContentBytes} bytes.");
        return content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    public static (string? Field, string? Value) FieldValue(string? field, string? value)
    {
        if (field is null && value is null) return (null, null);
        if (string.IsNullOrWhiteSpace(field) || string.IsNullOrWhiteSpace(value)) throw Refuse("field and value go together: give both, or neither.");
        if (field.Length > MaxFieldChars) throw Refuse($"field is over {MaxFieldChars} characters.");
        if (value.Length > MaxValueChars) throw Refuse($"value is over {MaxValueChars} characters.");
        if (TextProblem(field, multiline: false) is { } f) throw Refuse($"field {f}.");
        if (TextProblem(value, multiline: false) is { } v) throw Refuse($"value {v}.");
        return (field.Trim(), value.Trim());
    }

    public static string Evidence(EntryEvidence? evidence, Trust trust)
    {
        evidence ??= new EntryEvidence();
        var hasDescription = !string.IsNullOrWhiteSpace(evidence.Description);
        var hasUrl = !string.IsNullOrWhiteSpace(evidence.Url);
        if (trust == Trust.Observed && !hasDescription && !hasUrl)
            throw Refuse("observed needs evidence: a description or an http(s) URL.");

        if (evidence.Description is { } d)
        {
            if (d.Length > MaxDescriptionChars) throw Refuse($"evidence description is over {MaxDescriptionChars} characters.");
            if (TextProblem(d, multiline: false) is { } p) throw Refuse($"evidence description {p}.");
        }
        Uri? url = null;
        if (hasUrl)
        {
            if (evidence.Url!.Length > MaxUrlChars) throw Refuse($"evidence url is over {MaxUrlChars} characters.");
            url = ParseUrl(evidence.Url);
        }
        if (evidence.Timestamp is { } t && (t.Length > MaxTimestampChars || !IsTime(t)))
            throw Refuse("evidence timestamp must look like 1:02:03, 12:34 or 90s.");
        if ((evidence.Video == true || (url is not null && IsVideoUrl(url))) && evidence.Timestamp is null && (url is null || !HasTimeInUrl(url)))
            throw Refuse("A video as evidence needs a timestamp: in the timestamp field, or in the URL as t=90.");

        return JsonSerializer.Serialize(evidence with
        {
            Description = hasDescription ? evidence.Description!.Trim() : null,
            Url = hasUrl ? evidence.Url : null,
        });
    }

    private static Uri ParseUrl(string raw)
    {
        if (raw.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) || TextProblem(raw, multiline: false) is not null
            || !Uri.TryCreate(raw, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || url.Host.Length == 0 || url.UserInfo.Length > 0)
            throw Refuse("evidence url must be a plain http or https URL with no user name or password.");
        return url;
    }

    private static bool IsTime(string t) => DurationShape().IsMatch(t) || ClockShape().IsMatch(t);

    private static bool IsVideoUrl(Uri url)
    {
        var host = url.IdnHost.TrimEnd('.').ToLowerInvariant();
        return VideoHosts.Any(h => host == h || host.EndsWith("." + h, StringComparison.Ordinal))
            || VideoExtensions.Any(e => url.AbsolutePath.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasTimeInUrl(Uri url) =>
        url.Query.TrimStart('?').Split('&').Concat(url.Fragment.TrimStart('#').Split('&')).Any(part =>
        {
            var pair = part.Split('=', 2);
            return pair.Length == 2 && TimeParameters.Contains(pair[0], StringComparer.OrdinalIgnoreCase) && DurationShape().IsMatch(pair[1]);
        });

    public static string? Position(EntryPosition? p)
    {
        if (p is null) return null;
        if (string.IsNullOrWhiteSpace(p.Map) || !MapShape().IsMatch(p.Map) || TextProblem(p.Map, multiline: false) is not null)
            throw Refuse("position needs a map: 1 to 64 letters, digits, spaces and . _ ' -.");
        var hasCoordinates = p.X is not null || p.Y is not null || p.Z is not null;
        var hasPercents = p.LeftPct is not null || p.TopPct is not null;
        if (hasCoordinates == hasPercents || (hasCoordinates && (p.X is null || p.Y is null || p.Z is null)) || (hasPercents && (p.LeftPct is null || p.TopPct is null)))
            throw Refuse("position needs a map and either x, y and z, or both leftPct and topPct.");
        if (hasCoordinates && new[] { p.X!.Value, p.Y!.Value, p.Z!.Value }.Any(v => !double.IsFinite(v) || Math.Abs(v) > MaxCoordinate))
            throw Refuse($"position x, y and z must be finite and within {MaxCoordinate.ToString(CultureInfo.InvariantCulture)} of zero.");
        if (hasPercents && new[] { p.LeftPct!.Value, p.TopPct!.Value }.Any(v => !double.IsFinite(v) || v is < 0 or > 100))
            throw Refuse("position leftPct and topPct must be from 0 to 100.");
        return JsonSerializer.Serialize(p);
    }

    /// <summary>
    /// Text that shows nothing can make what a person reads differ from what an agent reads: all of Unicode's format characters (category Cf,
    /// which includes bidi controls, zero width characters, soft hyphen and tags) and its other default-ignorable characters (variation selectors, fillers).
    /// Zero width joiners go too. Allowing them only where a script needs them isn't worth the complexity here, at the cost of some emoji sequences and of Indic and Persian spellings.
    /// </summary>
    private static bool IsInvisible(Rune r) =>
        Rune.GetUnicodeCategory(r) == UnicodeCategory.Format
        || r.Value is 0x034F or 0x115F or 0x1160 or 0x17B4 or 0x17B5 or 0x3164 or 0xFFA0
            or (>= 0x180B and <= 0x180F) or (>= 0x2060 and <= 0x206F) or (>= 0xFE00 and <= 0xFE0F) or (>= 0xFFF0 and <= 0xFFF8) or (>= 0xE0000 and <= 0xE0FFF);

    /// <summary>Why this text can't be stored, or null. Refused rather than stripped, so what is stored is what was sent.</summary>
    private static string? TextProblem(string text, bool multiline)
    {
        try { new UTF8Encoding(false, true).GetByteCount(text); }
        catch (EncoderFallbackException) { return "is not valid UTF-8 text"; }

        foreach (var r in text.EnumerateRunes())
        {
            if (multiline && r.Value is '\n' or '\r' or '\t') continue;
            if (Rune.IsControl(r)) return "has a control character";
            if (Rune.GetUnicodeCategory(r) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) return "has a line separator character";
            if (IsInvisible(r)) return "has an invisible character";
        }
        return null;
    }
}
