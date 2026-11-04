using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CoaMcp.Pipeline.Notes;

/// <summary>A reviewed note: <paramref name="File"/> is its reviewed copy in the notes folder, never the original. <paramref name="Origin"/> says which original it was copied from.</summary>
public sealed record NoteSpec(string Id, string File, string Title, string Date, string? Origin = null)
{
    public DateOnly ParsedDate => DateOnly.ParseExact(Date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>A public page of the project site. <paramref name="Sections"/> are the ids of the page's sections to keep; nothing else is read.</summary>
public sealed record PageSpec(string Id, string Url, string Title, IReadOnlyList<string> Sections);

public sealed partial class NoteConfig
{
    public IReadOnlyList<NoteSpec> Notes { get; }
    public IReadOnlyList<PageSpec> ProjectPages { get; }

    private NoteConfig(IReadOnlyList<NoteSpec> notes, IReadOnlyList<PageSpec> pages) { Notes = notes; ProjectPages = pages; }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,80}$")]
    private static partial Regex SafeId();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,60}$")]
    private static partial Regex SafeSection();

    private sealed record Raw(List<NoteSpec>? Notes, List<PageSpec>? ProjectPages);

    public static NoteConfig Load(string path)
    {
        Raw? raw;
        try
        {
            raw = JsonSerializer.Deserialize<Raw>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex) { throw new InvalidDataException($"{Path.GetFileName(path)} is not valid: {ex.Message}", ex); }
        if (raw is null) throw new InvalidDataException($"{Path.GetFileName(path)} is empty.");

        var notes = raw.Notes ?? new();
        var pages = raw.ProjectPages ?? new();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in notes)
        {
            if (n.Id is null || !SafeId().IsMatch(n.Id)) throw new InvalidDataException($"Note id '{n.Id}' is not usable: lowercase letters, digits and '-'.");
            if (!ids.Add("note:" + n.Id)) throw new InvalidDataException($"Note id '{n.Id}' is listed twice.");
            if (string.IsNullOrWhiteSpace(n.File) || n.File.AsSpan().IndexOfAny('/', '\\', ':') >= 0 || n.File.Contains("..", StringComparison.Ordinal))
                throw new InvalidDataException($"Note '{n.Id}': file '{n.File}' must be a plain file name inside the notes folder.");
            if (string.IsNullOrWhiteSpace(n.Title)) throw new InvalidDataException($"Note '{n.Id}' has no title.");
            if (!DateOnly.TryParseExact(n.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                throw new InvalidDataException($"Note '{n.Id}': date '{n.Date}' must look like 2026-10-29.");
        }
        foreach (var p in pages)
        {
            if (p.Id is null || !SafeId().IsMatch(p.Id)) throw new InvalidDataException($"Project page id '{p.Id}' is not usable: lowercase letters, digits and '-'.");
            if (!ids.Add("project:" + p.Id)) throw new InvalidDataException($"Project page id '{p.Id}' is listed twice.");
            if (string.IsNullOrWhiteSpace(p.Title)) throw new InvalidDataException($"Project page '{p.Id}' has no title.");
            if (p.Sections is null || p.Sections.Count == 0 || p.Sections.Any(s => s is null || !SafeSection().IsMatch(s)))
                throw new InvalidDataException($"Project page '{p.Id}' must list the ids of the sections to keep.");
        }
        return new NoteConfig(notes, pages);
    }
}
