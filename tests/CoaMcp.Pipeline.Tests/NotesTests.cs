using System.Text.Json;
using CoaMcp.Core;
using CoaMcp.Pipeline.Notes;
using CoaMcp.Pipeline.Publish;

namespace CoaMcp.Pipeline.Tests;

public class NotesTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private const string Evidence = "This page describes the Conquest of AzerothCore emulator project. It isn't evidence about Ascension.";

    private sealed class FakePages(Dictionary<string, string> html) : IPageSource
    {
        public List<string> Asked { get; } = new();
        public string Fetch(string url)
        {
            Asked.Add(url);
            return html.TryGetValue(url, out var h) ? h : throw new InvalidOperationException("no fixture for " + url);
        }
    }

    private static readonly FakePages NoPages = new(new());

    private const string HomeHtml = """
        <html><body><nav><a href="/account">Account</a></nav><main>
        <section id="register"><h2>Create account</h2><p>Email and password go here.</p></section>
        <section id="about"><span>ABOUT THIS SERVER</span><h2><span>Testing only</span><span hidden>Play on the live realm</span></h2>
        <p>It is a public <b>bug-test</b> realm &amp; nothing more. See the <a href="/faq">FAQ</a>.</p>
        <p hidden><strong>Rexxar</strong> is <span>our live realm</span>.</p>
        <ul><li>Accounts can be reset</li><li>Test only</li></ul>
        <div><a href="https://discord.example/x" target="_blank">JOIN DISCORD</a></div></section>
        <section id="realm"><p>12 online now</p></section>
        </main><script>var x = 1;</script></body></html>
        """;

    /// <summary>Writes the notes folder and config, then runs the import. <paramref name="files"/> are name to text in the notes folder.</summary>
    private (string Layer, NoteImportResult Result) Run(Dictionary<string, string> files, object[] notes, object[]? pages = null, IPageSource? source = null)
    {
        var notesDir = Path.Combine(_dir.Path, "notes");
        Directory.CreateDirectory(notesDir);
        foreach (var (name, text) in files) File.WriteAllText(Path.Combine(notesDir, name), text);
        var config = _dir.File("notes.json");
        File.WriteAllText(config, JsonSerializer.Serialize(new { notes, projectPages = pages ?? Array.Empty<object>() }));
        var layer = _dir.File($"notes-{Guid.NewGuid():N}.sqlite");
        var result = NoteImporter.Run(notesDir, config, layer, source ?? NoReaching(), new DateOnly(2026, 11, 4), "t1");
        return (layer, result);
    }

    private static IPageSource NoReaching() => NoPages;

    private static object Note(string id, string file, string date = "2026-10-29") =>
        new { id, file, title = "Title of " + id, date };

    [Fact]
    public void Note_NotInManifest_Skipped()
    {
        var (layer, r) = Run(new() { ["listed.md"] = "# Listed\nText.\n", ["extra.md"] = "# Extra\nF:\\WorkDir\\Secret\\thing\n" },
            new[] { Note("listed", "listed.md") });
        Assert.Equal(1, r.Notes);
        Assert.Equal(new[] { "extra.md" }, r.Skipped);
        using var reader = new LayerReader(layer);
        Assert.NotNull(reader.Get("note:listed"));
        Assert.Null(reader.Get("note:extra"));
    }

    [Fact]
    public void Note_ListedButMissing_FailsBuild()
    {
        var ex = Assert.Throws<InvalidDataException>(() => Run(new(), new[] { Note("gone", "gone.md") }));
        Assert.Contains("gone.md", ex.Message);
    }

    [Fact]
    public void Note_WithLocalPath_FailsBuild()
    {
        var ex = Assert.Throws<InvalidDataException>(() => Run(new() { ["n.md"] = "Fine line.\nSee F:\\WorkDir\\CoA64\\out\\log.txt for it.\n" },
            new[] { Note("n", "n.md") }));
        Assert.Contains("n.md", ex.Message);
        Assert.Contains("line 2", ex.Message);
        Assert.DoesNotContain("CoA64", ex.Message);
    }

    [Theory]
    [InlineData("Authorization: Bearer abcdefghijklmnopqrstuvwxyz0123456789", "Q7bearerQ7")]
    [InlineData("api_key = ZXCVBNMASDFGHJKLQWERTYUIOP1234", "ZXCVBNMASDFGHJKL")]
    [InlineData("key sk-live-ABCDEFGHIJKLMNOPQRSTUV123456 here", "ABCDEFGHIJKLMNOPQRSTUV")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----", "RSA PRIVATE")]
    [InlineData("password: hunter2hunter2", "hunter2")]
    [InlineData("DB_PASSWORD=hunter2hunter2", "hunter2")]
    [InlineData("see D:\\WoW\\Coa\\Data now", "WoW")]
    [InlineData("see C:/Games/Wow now", "Games")]
    [InlineData("share \\\\fileserver\\public\\notes", "fileserver")]
    [InlineData("write to someone.name@example.org please", "someone.name")]
    public void Note_WithSecretPattern_FailsBuild(string line, string secret)
    {
        var ex = Assert.Throws<InvalidDataException>(() => Run(new() { ["n.md"] = "First.\nSecond.\n" + line + "\n" }, new[] { Note("n", "n.md") }));
        Assert.Contains("n.md", ex.Message);
        Assert.Contains("line 3", ex.Message);
        Assert.DoesNotContain(secret, ex.Message);
    }

    [Theory]
    [InlineData("A url https://coa-development.org/faq and http://example.org/a:/b is fine.")]
    [InlineData("Registers like 0x1021EA50, ids 12:30, key: value and Interface\\FrameXML\\Foo.lua are fine.")]
    [InlineData("A password field is mentioned in prose, and a token is a word.")]
    public void SecretScan_PlainText_IsClean(string line) => Assert.Empty(SecretScan.Find(line));

    [Fact]
    public void Note_TrustIsDerived()
    {
        var (layer, _) = Run(new() { ["n.md"] = "Intro.\n\n## Part one\nBody.\n" }, new[] { Note("n", "n.md", "2026-10-29") });
        using var reader = new LayerReader(layer);
        var e = reader.Get("note:n")!;
        Assert.Equal(Trust.Derived, e.Trust);
        Assert.Equal("note", e.Type);
        Assert.Equal(new[] { "coa-client" }, e.Realm.Realms);
        Assert.Equal("CoA64 project notes", e.Source);
        Assert.Equal(new DateOnly(2026, 10, 29), e.SourceDate);
        Assert.Equal("Title of n", e.Title);
        Assert.Contains("## Part one", e.BodyMd);
        Assert.Equal("notes", reader.Meta.Name);
    }

    [Fact]
    public void Note_DuplicateId_FailsBuild()
    {
        Assert.Throws<InvalidDataException>(() => Run(new() { ["a.md"] = "A.\n", ["b.md"] = "B.\n" }, new[] { Note("same", "a.md"), Note("same", "b.md") }));
    }

    [Fact]
    public void Note_FileOutsideNotesFolder_FailsBuild()
    {
        Assert.Throws<InvalidDataException>(() => Run(new() { ["a.md"] = "A.\n" }, new[] { Note("a", "..\\a.md") }));
        Assert.Throws<InvalidDataException>(() => Run(new() { ["a.md"] = "A.\n" }, new[] { Note("a", "sub/a.md") }));
    }

    private static object Page(string id = "home-about", string url = "https://coa-development.org/", string[]? sections = null) =>
        new { id, url, title = "Project home: about", sections = sections ?? new[] { "about" } };

    [Fact]
    public void ProjectPage_LabeledNotEvidence()
    {
        var pages = new FakePages(new() { ["https://coa-development.org/"] = HomeHtml });
        var (layer, r) = Run(new(), Array.Empty<object>(), new[] { Page() }, pages);
        Assert.Equal(1, r.Pages);
        using var reader = new LayerReader(layer);
        var e = reader.Get("project:home-about")!;
        Assert.Equal("project", e.Type);
        Assert.Equal(Trust.Derived, e.Trust);
        Assert.Equal(new[] { "coa-development" }, e.Realm.Realms);
        Assert.StartsWith(Evidence, e.BodyMd);
        Assert.Contains("coa-development.org", e.Source);
        Assert.Contains("emulator project", e.Source);
        Assert.Equal(new DateOnly(2026, 11, 4), e.SourceDate);
        // Only the listed section, in plain Markdown, with no markup, scripts or other sections.
        Assert.Contains("## Testing only", e.BodyMd);
        Assert.Contains("See the FAQ.", e.BodyMd);
        // What the page keeps hidden is not what it says, and button labels are not text.
        Assert.DoesNotContain("live realm", e.BodyMd);
        Assert.DoesNotContain("Rexxar", e.BodyMd);
        Assert.DoesNotContain("JOIN DISCORD", e.BodyMd);
        Assert.Contains("public bug-test realm & nothing more.", e.BodyMd);
        Assert.Contains("- Accounts can be reset", e.BodyMd);
        Assert.DoesNotContain("Create account", e.BodyMd);
        Assert.DoesNotContain("online now", e.BodyMd);
        Assert.DoesNotContain("<", e.BodyMd);
        Assert.DoesNotContain("var x", e.BodyMd);
        Assert.Equal(new[] { "https://coa-development.org/" }, pages.Asked);
    }

    [Fact]
    public void ProjectPage_FoundThroughSearch_ShowsNotEvidenceInSource()
    {
        var pages = new FakePages(new() { ["https://coa-development.org/"] = HomeHtml });
        var (layer, _) = Run(new(), Array.Empty<object>(), new[] { Page() }, pages);
        using var reader = new LayerReader(layer);
        var hit = Assert.Single(reader.Search(FtsQuery.FromUser("bug-test realm"), "project", null, 10, 0));
        Assert.Equal("project:home-about", hit.Id);
        Assert.Contains("isn't evidence about Ascension", reader.Get(hit.Id)!.BodyMd);
    }

    [Fact]
    public void ProjectPage_MissingSection_FailsBuild()
    {
        var pages = new FakePages(new() { ["https://coa-development.org/"] = HomeHtml });
        var ex = Assert.Throws<InvalidDataException>(() => Run(new(), Array.Empty<object>(), new[] { Page(sections: new[] { "about", "nope" }) }, pages));
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public void ProjectPage_WithSecret_FailsBuild()
    {
        var html = "<main><section id=\"about\"><p>Mail me at a.b@example.org</p></section></main>";
        var pages = new FakePages(new() { ["https://coa-development.org/"] = html });
        var ex = Assert.Throws<InvalidDataException>(() => Run(new(), Array.Empty<object>(), new[] { Page() }, pages));
        Assert.Contains("home-about", ex.Message);
        Assert.DoesNotContain("a.b@", ex.Message);
    }

    [Fact]
    public void ProjectPage_TitleWithSecret_FailsBuild()
    {
        var pages = new FakePages(new() { ["https://coa-development.org/"] = HomeHtml });
        var page = new { id = "home-about", url = "https://coa-development.org/", title = "Write to a.b@example.org", sections = new[] { "about" } };
        var ex = Assert.Throws<InvalidDataException>(() => Run(new(), Array.Empty<object>(), new object[] { page }, pages));
        Assert.Contains("title", ex.Message);
        Assert.DoesNotContain("a.b@", ex.Message);
    }

    [Theory]
    [InlineData("https://coa-development.org/account")]
    [InlineData("https://coa-development.org/account#apply")]
    [InlineData("https://coa-development.org/download")]
    [InlineData("https://coa-development.org/downloads/launcher.zip")]
    [InlineData("https://coa-development.org/login")]
    [InlineData("https://coa-development.org/register")]
    [InlineData("https://coa-development.org/api/account/me")]
    [InlineData("https://coa-development.org/armory")]
    [InlineData("https://coa-development.org/database/npcs")]
    [InlineData("https://coa-development.org/bugs")]
    [InlineData("https://coa-development.org/status")]
    [InlineData("https://coa-development.org/statistics")]
    [InlineData("http://coa-development.org/faq")]
    [InlineData("https://evil.example.org/faq")]
    [InlineData("https://coa-development.org.evil.example/faq")]
    [InlineData("https://user@coa-development.org/faq")]
    [InlineData("https://coa-development.org:8443/faq")]
    public void ProjectPage_UrlNotAllowed_FailsBuildWithoutFetching(string url)
    {
        var pages = new FakePages(new());
        Assert.Throws<InvalidDataException>(() => Run(new(), Array.Empty<object>(), new[] { Page(url: url) }, pages));
        Assert.Empty(pages.Asked);
    }

    [Theory]
    [InlineData("https://coa-development.org/")]
    [InlineData("https://coa-development.org/faq")]
    public void ProjectPage_PublicUrl_Allowed(string url) => Assert.True(ProjectPages.IsAllowedUrl(url));

    [Fact]
    public void Notes_Layer_PassesPublishGuard()
    {
        var pages = new FakePages(new() { ["https://coa-development.org/"] = HomeHtml });
        var (layer, _) = Run(new() { ["n.md"] = "Text.\n" }, new[] { Note("n", "n.md") }, new[] { Page() }, pages);
        var r = PublishGuard.Check(layer);
        Assert.True(r.Ok, string.Join("; ", r.Problems));
        Assert.Equal("notes", r.LayerName);
    }

    [Fact]
    public void Config_InRepository_ListsFilesThatExistAndHaveNoSecrets()
    {
        var root = RepoRoot();
        var config = NoteConfig.Load(Path.Combine(root, "config", "notes.json"));
        Assert.NotEmpty(config.Notes);
        foreach (var n in config.Notes)
        {
            var path = Path.Combine(root, "notes", n.File);
            Assert.True(File.Exists(path), path);
            SecretScan.ThrowIfAny(n.File, File.ReadAllText(path));
        }
        foreach (var p in config.ProjectPages) Assert.True(ProjectPages.IsAllowedUrl(p.Url), p.Url);
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "CoaMcp.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
