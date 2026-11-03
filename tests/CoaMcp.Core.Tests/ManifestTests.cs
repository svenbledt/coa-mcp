using System.Text.Json;
using CoaMcp.Core;

namespace CoaMcp.Core.Tests;

public class ManifestTests
{
    private static readonly string Sha = new('a', 64);

    private static string Json(string name, string version, string file, string sha) =>
        $$"""{ "layers": { "{{name}}": { "version": "{{version}}", "file": "{{file}}", "sha256": "{{sha}}" } } }""";

    [Fact]
    public void RoundTrip_KeepsEveryEntry()
    {
        var m = new Manifest(new Dictionary<string, ManifestEntry>())
            .With("bisbeard", ManifestEntry.For("bisbeard", "7", Sha))
            .With("spells", ManifestEntry.For("spells", "3", new string('b', 64)));
        var back = Manifest.Parse(m.ToJson());
        Assert.Equal(2, back.Layers.Count);
        Assert.Equal("layers/spells/3.sqlite", back.Layers["spells"].File);
        Assert.Equal(new string('b', 64), back.Layers["spells"].Sha256);
    }

    [Fact]
    public void With_ReplacesOneLayerAndKeepsTheOthers()
    {
        var m = new Manifest(new Dictionary<string, ManifestEntry>())
            .With("bisbeard", ManifestEntry.For("bisbeard", "7", Sha))
            .With("spells", ManifestEntry.For("spells", "3", Sha))
            .With("spells", ManifestEntry.For("spells", "4", Sha));
        Assert.Equal("7", m.Layers["bisbeard"].Version);
        Assert.Equal("4", m.Layers["spells"].Version);
    }

    [Fact]
    public void Parse_AcceptsTheDocumentedShape()
    {
        var m = Manifest.Parse(Json("spells", "3", "layers/spells/3.sqlite", Sha));
        Assert.Equal("3", m.Layers["spells"].Version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"layers": []}""")]
    [InlineData("""{"layers": {"spells": null}}""")]
    [InlineData("""{"layers": {"spells": {"version": 3}}}""")]
    public void Parse_RejectsMalformedDocuments(string text) =>
        Assert.ThrowsAny<Exception>(() => Manifest.Parse(text));

    [Theory]
    [InlineData("spells", "3", "../../etc/passwd")]
    [InlineData("spells", "3", "/etc/passwd")]
    [InlineData("spells", "3", "layers/bisbeard/3.sqlite")]
    [InlineData("spells", "3", "layers/spells/4.sqlite")]
    [InlineData("spells", "../3", "layers/spells/../3.sqlite")]
    [InlineData("..", "3", "layers/../3.sqlite")]
    [InlineData("sp ells", "3", "layers/sp ells/3.sqlite")]
    public void Parse_RejectsPathsThatEscapeTheLayerLayout(string name, string version, string file) =>
        Assert.Throws<InvalidDataException>(() => Manifest.Parse(Json(name, version, file, Sha)));

    [Theory]
    [InlineData("abc")]
    [InlineData("ZZZZ")]
    public void Parse_RejectsBadChecksum(string sha) =>
        Assert.Throws<InvalidDataException>(() => Manifest.Parse(Json("spells", "3", "layers/spells/3.sqlite", sha)));

    [Fact]
    public void NameAndVersionRules()
    {
        Assert.True(Manifest.IsSafeName("bisbeard"));
        Assert.False(Manifest.IsSafeName("a/b"));
        Assert.False(Manifest.IsSafeName(""));
        Assert.True(Manifest.IsSafeVersion("2026.11-03"));
        Assert.False(Manifest.IsSafeVersion("1;rm"));
        Assert.False(Manifest.IsSafeVersion(".."));
    }

    [Fact]
    public async Task ConcurrentReader_NeverSeesAPartialFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "coamcp-manifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "current.json");
            var big = new Manifest(new Dictionary<string, ManifestEntry>());
            for (var i = 0; i < 60; i++) big = big.With($"layer{i}", ManifestEntry.For($"layer{i}", "1", Sha));
            Manifest.WriteAtomic(path, big);

            using var stop = new CancellationTokenSource();
            var failures = new List<string>();
            var reads = 0;
            var reader = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    try { Manifest.ReadFile(path); Interlocked.Increment(ref reads); }
                    catch (Exception ex) when (ex is JsonException or InvalidDataException) { lock (failures) failures.Add(ex.Message); }
                    catch (IOException) { /* Windows may briefly refuse a read mid-rename; the content is never partial */ }
                }
            });
            for (var v = 0; v < 300; v++)
                Manifest.WriteAtomic(path, big.With("layer0", ManifestEntry.For("layer0", v.ToString(), Sha)));
            await stop.CancelAsync();
            await reader;

            Assert.Empty(failures);
            Assert.True(reads > 0, "the reader never got a read in");
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally { Directory.Delete(dir, true); }
    }
}
