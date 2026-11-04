using System.Text;

namespace CoaMcp.Acceptance;

/// <summary>The suite's own promises, which need no server: the scrubber works and the report shows what it says it shows. These run even without COA_MCP_URL.</summary>
[Collection("00 Self-checks")]
public class SelfCheckTests
{
    private const string Fake = "FAKE-TOKEN+with/odd=chars&0123456789abcdefghijklmn";

    private static void Record(string test) =>
        Suite.Book.Add(new TestResult("Suite self-checks", test, Outcome.Pass, 1, 0, TimeSpan.Zero, null));

    [Theory]
    [InlineData("plain {0} text")]
    [InlineData("Authorization: Bearer {0}")]
    [InlineData("{{\"args\":\"{0}\"}}")]
    public void Scrubber_RemovesTheToken(string shape)
    {
        var scrub = new Scrubber(new[] { Fake });
        var result = scrub.Scrub(string.Format(shape, Fake));
        Assert.DoesNotContain(Fake, result);
        Assert.Contains(Scrubber.Mask, result);
        Record("scrubber removes the token");
    }

    [Fact]
    public void Scrubber_RemovesEncodedFormsOfTheToken()
    {
        var scrub = new Scrubber(new[] { Fake });
        foreach (var encoded in new[]
                 {
                     Uri.EscapeDataString(Fake), Convert.ToBase64String(Encoding.UTF8.GetBytes(Fake)), Convert.ToHexStringLower(Encoding.UTF8.GetBytes(Fake)),
                     System.Text.Json.JsonSerializer.Serialize(Fake)[1..^1], Fake.ToUpperInvariant(),
                 })
            Assert.DoesNotContain(encoded, scrub.Scrub("before " + encoded + " after"));
        Record("scrubber removes encoded forms of the token");
    }

    [Fact]
    public void Scrubber_RemovesAnyBearerValueInAnAuthorizationHeader()
    {
        var result = new Scrubber(Array.Empty<string>()).Scrub("Authorization: Bearer some-other-credential-value");
        Assert.DoesNotContain("some-other-credential-value", result);
        Record("scrubber removes any bearer value");
    }

    [Fact]
    public void Scrubber_RemovesLocalRootsInEitherSlash()
    {
        var scrub = new Scrubber(Array.Empty<string>(), new[] { @"F:\WorkDir" });
        var result = scrub.Scrub(@"F:\WorkDir\x and F:/WorkDir/y and F:\\WorkDir\\z");
        Assert.DoesNotContain("WorkDir", result);
        Record("scrubber removes local paths");
    }

    /// <summary>The token placed in every kind of text the report can hold must be gone from the rendered report.</summary>
    [Fact]
    public void Report_NeverShowsTheToken_WhereverItWasCaptured()
    {
        var book = new ReportBook(new Scrubber(new[] { Fake }));
        book.Add(new Capture("search", "label " + Fake, $"{{\"q\":\"{Fake}\"}}", "response " + Fake, false, 1));
        book.Add(new Failure("Writes", "test " + Fake, "what " + Fake, "detail " + Fake));
        book.Add(new TestResult("Writes", "t", Outcome.NotRun, 0, 0, TimeSpan.Zero, "reason " + Fake));
        book.Transcript("t " + Fake, "transcript " + Fake);
        book.Load("l " + Fake, "load " + Fake);
        book.Observe("obs " + Fake);
        book.ProductionChange("change " + Fake);
        book.Header("header " + Fake);
        book.LayersAtStart("layers " + Fake);

        var report = book.Render();
        Assert.DoesNotContain(Fake, report);
        Assert.Contains(Scrubber.Mask, report);
        Record("the report never shows the token");
    }

    [Fact]
    public void Report_ShowsNotRunApartFromPassed()
    {
        var book = new ReportBook(new Scrubber(Array.Empty<string>()));
        book.Add(new TestResult("Writes: with token", "write", Outcome.NotRun, 0, 0, TimeSpan.Zero, "token not set"));
        var report = book.Render();
        Assert.Contains("| Writes: with token | NOT RUN |", report);
        Assert.Contains("token not set", report);
        Assert.Contains("0 tests passed", report);
        Assert.Contains("| Merge | NOT RUN |", report);   // no results at all is not a pass either
        Record("NOT RUN stays apart from passed");
    }

    [Fact]
    public void Report_CutsLongResponses_AndStatesTheFullSize()
    {
        var book = new ReportBook(new Scrubber(Array.Empty<string>()));
        book.Add(new Capture("get", "long", "{}", new string('x', ReportBook.MaxShownChars + 5000), false, 1));
        var report = book.Render();
        Assert.Contains("the full response is 8,000 characters", report);
        Assert.DoesNotContain(new string('x', ReportBook.MaxShownChars + 1), report);
        Record("long responses are cut with their full size");
    }

    [Fact]
    public void Report_CodeFenceSurvivesBackticksInTheResponse()
    {
        var fenced = ReportBook.Fence("a ``` b ````` c");
        Assert.StartsWith("``````\n", fenced);
        Record("code fences survive backticks");
    }

    [Fact]
    public void Leak_FindsFaultsAndPaths_ButNotWhatTheCallerSent()
    {
        Assert.NotNull(Suite.Leak("An error occurred invoking 'search'."));
        Assert.NotNull(Suite.Leak("at CoaMcp.Server.Layer.Query(String sql)"));
        Assert.NotNull(Suite.Leak("failed to open /data/layers/x.sqlite"));
        Assert.NotNull(Suite.Leak(@"F:\WorkDir\secret"));
        Assert.Null(Suite.Leak("No entity with id '/data/x'.", new[] { "/data/x" }));
        Assert.Null(Suite.Leak("No entity with id 'fireball'."));
        Record("the fault detector finds faults");
    }

    [Fact]
    public void MpqHash_MatchesAKnownEntry()
    {
        // Name and hashes as the manifest records them for this path in patch-S.MPQ.
        var (a, b) = MpqHash.Name(@"DBFilesClient\SpellVisual.dbc");
        Assert.Equal(2460267514u, a);
        Assert.Equal(418436030u, b);
        Record("the MPQ hash matches a known entry");
    }
}
