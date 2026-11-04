using CoaMcp.Core;

namespace CoaMcp.Pipeline.Notes;

public sealed record NoteImportResult(int Notes, int Pages, IReadOnlyList<string> Skipped);

/// <summary>
/// Builds the notes layer from reviewed copies of notes (never the originals) and from listed public project pages.
/// Anything in the notes folder that config/notes.json doesn't list is skipped. Everything published passes <see cref="SecretScan"/>.
/// </summary>
public static class NoteImporter
{
    public const string NoteSource = "CoA64 project notes";

    private static readonly RealmSet ClientRealm = new(new[] { "coa-client" }, false);
    private static readonly RealmSet ProjectRealm = new(new[] { "coa-development" }, true);

    public static NoteImportResult Run(string notesDir, string configPath, string outPath, IPageSource pages, DateOnly today, string? version = null)
    {
        version = LayerVersion.Resolve(version);
        var config = NoteConfig.Load(configPath);
        var entities = new List<Entity>();

        foreach (var n in config.Notes)
        {
            var path = Path.Combine(notesDir, n.File);
            if (!File.Exists(path)) throw new InvalidDataException($"Note '{n.Id}' lists {n.File}, which is not in the notes folder.");
            var text = File.ReadAllText(path).Replace("\r\n", "\n").Trim() + "\n";
            SecretScan.ThrowIfAny(n.File, text);
            SecretScan.ThrowIfAny($"title of note '{n.Id}'", n.Title);
            entities.Add(new Entity("note:" + n.Id, "note", n.Title, ClientRealm, Trust.Derived, NoteSource, n.ParsedDate, text, null, Array.Empty<Link>()));
        }

        var listed = config.Notes.Select(n => n.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var skipped = Directory.GetFiles(notesDir, "*.md").Select(f => Path.GetFileName(f)!)
            .Where(f => !listed.Contains(f)).OrderBy(f => f, StringComparer.Ordinal).ToList();

        foreach (var p in config.ProjectPages)
            if (!ProjectPages.IsAllowedUrl(p.Url))
                throw new InvalidDataException($"Project page '{p.Id}': {p.Url} is not a public project page (https://{ProjectPages.Host}/, no account, download, armory, database or status pages).");

        foreach (var p in config.ProjectPages)
        {
            SecretScan.ThrowIfAny($"title of project page '{p.Id}'", p.Title);
            var markdown = ProjectPages.Parse(pages.Fetch(p.Url), p.Sections, p.Id);
            var body = ProjectPages.NotEvidence + "\n\n" + markdown + $"\n\nPage: {p.Url}, read {today:yyyy-MM-dd}.\n";
            SecretScan.ThrowIfAny($"project page '{p.Id}'", body);
            var path = new Uri(p.Url).AbsolutePath;
            entities.Add(new Entity("project:" + p.Id, "project", p.Title, ProjectRealm, Trust.Derived,
                $"{ProjectPages.Host} public page {path} (the emulator project, not Ascension)", today, body, null, Array.Empty<Link>()));
        }

        using var w = new LayerWriter(outPath, "notes", version);
        foreach (var e in entities) w.Add(e);
        w.Finish();
        return new NoteImportResult(config.Notes.Count, config.ProjectPages.Count, skipped);
    }
}
