using System.Security.Cryptography;
using System.Text;
using CoaMcp.Core;
using CoaMcp.Pipeline.Publish;
using Microsoft.Data.Sqlite;

namespace CoaMcp.Pipeline.Tests;

public class PublisherTests : IDisposable
{
    private const string Root = "/data/coamcp";
    private const string Current = Root + "/current.json";
    private readonly TempDir _dir = new();
    private readonly FakeRemote _remote = new();
    private readonly StringWriter _out = new();
    public void Dispose() => _dir.Dispose();

    private string Layer(string name, string version, string body = "A plain item.\n", Action<LayerWriter>? extra = null)
    {
        var path = _dir.File($"{name}-{version}-{Guid.NewGuid():N}.sqlite");
        using (var w = new LayerWriter(path, name, version))
        {
            w.Add(new Entity("item:1", "item", "Thing", new RealmSet(new[] { "coa" }, true), Trust.Derived, "BisBeard",
                new DateOnly(2026, 8, 27), body, null, Array.Empty<Link>()));
            extra?.Invoke(w);
            w.Finish();
        }
        SqliteConnection.ClearAllPools();
        return path;
    }

    private Publisher Pub(string root = Root) => new(_remote, _out, root);

    private static string Sha(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private Manifest Current_() => Manifest.Parse(Encoding.UTF8.GetString(_remote.Files[Current]));

    [Fact]
    public async Task Publish_UploadsVerifiesRenamesAndWritesCurrentJson()
    {
        var file = Layer("bisbeard", "7");
        await Pub().PublishAsync(file, dryRun: false, default);

        Assert.Equal(File.ReadAllBytes(file), _remote.Files[Root + "/layers/bisbeard/7.sqlite"]);
        Assert.DoesNotContain(_remote.Files.Keys, k => k.EndsWith(".tmp"));
        var entry = Current_().Layers["bisbeard"];
        Assert.Equal("7", entry.Version);
        Assert.Equal("layers/bisbeard/7.sqlite", entry.File);
        Assert.Equal(Sha(file), entry.Sha256);
        // The upload goes to a .tmp name first.
        Assert.Equal(Root + "/layers/bisbeard/7.sqlite.tmp", Assert.Single(_remote.Uploads));
    }

    [Fact]
    public async Task Publish_SetsModesExplicitly_AndCreatesTheWritableStoreForTheServerUid()
    {
        await Pub().PublishAsync(Layer("bisbeard", "7"), false, default);

        Assert.Contains(_remote.Commands, c => c.StartsWith("mkdir -p '/data/coamcp/layers/bisbeard' && chmod 755 '/data/coamcp' '/data/coamcp/layers' '/data/coamcp/layers/bisbeard'"));
        Assert.Contains(_remote.Commands, c => c.StartsWith("chmod 644 '/data/coamcp/layers/bisbeard/7.sqlite.tmp' && mv"));
        Assert.Contains(_remote.Commands, c => c.Contains("chmod 644 '/data/coamcp/current.json.tmp'"));
        Assert.Equal("10001:10001", _remote.Owners["/data/coamcp/writable"]);
        Assert.Contains(_remote.Commands, c => c == "mkdir -p '/data/coamcp/writable' && chown 10001:10001 '/data/coamcp/writable' && chmod 750 '/data/coamcp/writable'");
        // Layer files are never handed to the server's UID.
        Assert.DoesNotContain(_remote.Commands, c => c.Contains("chown") && c.Contains("layers"));
    }

    [Fact]
    public async Task Publish_KeepsOtherLayersAndThePreviousVersionFile()
    {
        var b7 = Layer("bisbeard", "7");
        var s3 = Layer("spells", "3");
        var s4 = Layer("spells", "4", "A changed body.\n");
        await Pub().PublishAsync(b7, false, default);
        await Pub().PublishAsync(s3, false, default);
        await Pub().PublishAsync(s4, false, default);

        var m = Current_();
        Assert.Equal("7", m.Layers["bisbeard"].Version);
        Assert.Equal("4", m.Layers["spells"].Version);
        Assert.True(_remote.Files.ContainsKey(Root + "/layers/spells/3.sqlite"), "rollback needs the previous version on the server");
        Assert.True(_remote.Files.ContainsKey(Root + "/layers/spells/4.sqlite"));
    }

    [Fact]
    public async Task Publish_SameVersionSameContent_IsIdempotentAndSkipsUpload()
    {
        var file = Layer("bisbeard", "7");
        await Pub().PublishAsync(file, false, default);
        _remote.Uploads.Clear();
        await Pub().PublishAsync(file, false, default);
        Assert.Empty(_remote.Uploads);
        Assert.Equal("7", Current_().Layers["bisbeard"].Version);
    }

    [Fact]
    public async Task Publish_SameVersionDifferentContent_IsRefused()
    {
        await Pub().PublishAsync(Layer("bisbeard", "7"), false, default);
        var before = _remote.Files[Current].ToArray();
        var other = Layer("bisbeard", "7", "Different content under the same version.\n");
        var ex = await Assert.ThrowsAsync<PublishException>(() => Pub().PublishAsync(other, false, default));
        Assert.Contains("already", ex.Message);
        Assert.Equal(before, _remote.Files[Current]);
    }

    [Fact]
    public async Task FailedUpload_LeavesCurrentJsonUntouched()
    {
        await Pub().PublishAsync(Layer("bisbeard", "7"), false, default);
        var before = _remote.Files[Current].ToArray();
        _remote.Commands.Clear();
        _remote.FailUpload = true;

        await Assert.ThrowsAsync<PublishException>(() => Pub().PublishAsync(Layer("spells", "3"), false, default));

        Assert.Equal(before, _remote.Files[Current]);
        Assert.DoesNotContain(_remote.Commands, c => c.Contains("current.json") && c.Contains("mv"));
        Assert.False(_remote.Files.ContainsKey(Root + "/layers/spells/3.sqlite"));
    }

    [Fact]
    public async Task ChecksumMismatch_LeavesCurrentJsonUntouched_AndRemovesTheTempFile()
    {
        await Pub().PublishAsync(Layer("bisbeard", "7"), false, default);
        var before = _remote.Files[Current].ToArray();
        _remote.CorruptUpload = true;

        var ex = await Assert.ThrowsAsync<PublishException>(() => Pub().PublishAsync(Layer("spells", "3"), false, default));

        Assert.Contains("sha256", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, _remote.Files[Current]);
        Assert.False(_remote.Files.ContainsKey(Root + "/layers/spells/3.sqlite"), "a corrupt file must never get its final name");
        Assert.DoesNotContain(_remote.Files.Keys, k => k.EndsWith(".tmp"));
    }

    [Fact]
    public async Task GuardFailure_StopsBeforeAnyRemoteCall()
    {
        _remote.ThrowOnAnyCall = true;
        var bad = Layer("bisbeard", "7", extra: w => w.Execute("CREATE TABLE dbc_dump(x TEXT)"));
        var ex = await Assert.ThrowsAsync<PublishException>(() => Pub().PublishAsync(bad, false, default));
        Assert.Contains("dbc_dump", ex.Message);
    }

    [Fact]
    public async Task DryRun_RunsTheGuard_PrintsThePlan_AndTouchesNothing()
    {
        _remote.ThrowOnAnyCall = true;
        var file = Layer("spells", "3");
        await Pub().PublishAsync(file, dryRun: true, default);
        var text = _out.ToString();
        Assert.Contains("/data/coamcp/layers/spells/3.sqlite", text);
        Assert.Contains(Sha(file), text);
        Assert.Contains("dry run", text, StringComparison.OrdinalIgnoreCase);

        var bad = Layer("bisbeard", "7", extra: w => w.Execute("CREATE TABLE dbc_dump(x TEXT)"));
        await Assert.ThrowsAsync<PublishException>(() => Pub().PublishAsync(bad, dryRun: true, default));
    }

    [Fact]
    public async Task MalformedRemoteCurrentJson_IsNotOverwritten()
    {
        _remote.Files[Current] = Encoding.UTF8.GetBytes("{ this is not json");
        await Assert.ThrowsAsync<PublishException>(() => Pub().PublishAsync(Layer("bisbeard", "7"), false, default));
        Assert.Equal("{ this is not json", Encoding.UTF8.GetString(_remote.Files[Current]));
        Assert.Empty(_remote.Uploads);
    }

    [Fact]
    public async Task RemoteCommands_QuotePaths()
    {
        // A root with a space and a quote only works if every command quotes it.
        const string odd = "/data/co amcp's";
        var file = Layer("bisbeard", "7");
        await Pub(odd).PublishAsync(file, false, default);
        Assert.True(_remote.Files.ContainsKey(odd + "/layers/bisbeard/7.sqlite"));
        Assert.True(_remote.Files.ContainsKey(odd + "/current.json"));
    }

    [Fact]
    public async Task Cli_DryRunNeedsNoHost_AndPublishNeedsOne()
    {
        var file = Layer("bisbeard", "7");
        var o = new StringWriter();
        var e = new StringWriter();
        Assert.Equal(0, await PublishCli.RunAsync(new[] { "--layer", file, "--dry-run" }, o, e));
        Assert.Contains("Dry run", o.ToString());
        Assert.Equal(2, await PublishCli.RunAsync(new[] { "--layer", file }, o, e));
        Assert.Equal(2, await PublishCli.RunAsync(new[] { "--bogus" }, o, e));
        Assert.Equal(1, await PublishCli.RunAsync(new[] { "--layer", file, "--host", "-oBad" }, o, e));
    }

    [Fact]
    public async Task Cli_PublishesThroughTheRemoteItIsGiven()
    {
        var file = Layer("spells", "3");
        var code = await PublishCli.RunAsync(new[] { "--layer", file, "--host", "BledtIT" }, _out, new StringWriter(), host =>
        {
            Assert.Equal("BledtIT", host);
            return _remote;
        });
        Assert.Equal(0, code);
        Assert.Equal("3", Current_().Layers["spells"].Version);
    }

    [Fact]
    public void Shell_Quote_HandlesQuotesAndSpaces()
    {
        Assert.Equal("'a b'", Shell.Quote("a b"));
        Assert.Equal(@"'it'\''s'", Shell.Quote("it's"));
        Assert.Equal("''", Shell.Quote(""));
        Assert.Equal("'$(rm -rf /)'", Shell.Quote("$(rm -rf /)"));
    }

    [Theory]
    [InlineData("-oProxyCommand=evil")]
    [InlineData("")]
    [InlineData("host name")]
    [InlineData("a;b")]
    public void SshRemote_RejectsHostsThatCouldBeOptionsOrCommands(string host) =>
        Assert.Throws<ArgumentException>(() => new SshRemote(host));

    [Fact]
    public void SshRemote_BuildsArgumentListsWithoutALocalShell()
    {
        var r = new SshRemote("BledtIT");
        // scp runs in the file's folder with a bare file name, so a Windows drive letter is never read as a host.
        var scp = r.ScpArguments("a b.sqlite", "/data/coamcp/layers/spells/3.sqlite.tmp");
        Assert.Equal("--", scp[^3]);
        Assert.Equal("a b.sqlite", scp[^2]);
        Assert.Equal("BledtIT:/data/coamcp/layers/spells/3.sqlite.tmp", scp[^1]);
        Assert.Equal(new[] { "-o", "BatchMode=yes", "--", "BledtIT", "sh -s" }, r.SshArguments());
    }

    [Fact]
    public void SshRemote_RemoteCommandIsSafeForAnyLoginShell()
    {
        // The login shell on the server may be fish. The only text it parses is the ssh argument, so that argument must contain
        // nothing either shell treats specially: no quotes, backslashes, $, ;, &, |, (, ), <, >, *, ?, or ~. The real script travels on stdin to sh.
        foreach (var arg in new SshRemote("BledtIT").SshArguments().Skip(4))
            Assert.Matches(@"^[A-Za-z0-9 -]+$", arg);
    }

    [Fact]
    public void SshRemote_Script_QuotesPathsForSh_AndHasNoCarriageReturns()
    {
        const string odd = @"/data/co amcp's/back\slash";
        var script = SshRemote.BuildScript(RemoteCommands.Sha256IfPresent(odd + "/x"), null);
        Assert.Contains(Shell.Quote(odd + "/x"), script);
        Assert.DoesNotContain('\r', script);
        Assert.EndsWith("\n", script);

        var withData = SshRemote.BuildScript(RemoteCommands.ReplaceFromStdin("/d/t", "/d/c"), "{\r\n  \"layers\": {}\r\n}");
        Assert.DoesNotContain('\r', withData);
        Assert.Contains("<<'COAMCP_DATA_END'", withData);
        Assert.Contains("{\n  \"layers\": {}\n}\nCOAMCP_DATA_END\n", withData);
    }

    [Fact]
    public void SshRemote_Script_RefusesDataThatCouldEndTheHeredocEarly() =>
        Assert.Throws<ArgumentException>(() => SshRemote.BuildScript("cat", "a\nCOAMCP_DATA_END\nrm -rf /"));

    [SkippableFact]
    public async Task SshRemote_Script_RunsUnderRealSh_WithAwkwardPathsAndData()
    {
        var sh = FindSh();
        Skip.If(sh is null, "No POSIX sh on this machine, so the script can't run. The argument tests still apply.");
        using var dir = new TempDir();
        const string odd = "d o'ir";      // relative to the working folder, so no Windows drive letters get in the way
        var tmp = $"{odd}/current.json.tmp";
        var target = $"{odd}/current.json";
        var script = SshRemote.BuildScript(RemoteCommands.MakeLayerDirectory(odd), null)
            + SshRemote.BuildScript(RemoteCommands.ReplaceFromStdin(tmp, target), "{\r\n \"k\": \"it's\"\r\n}\r\n")
            + SshRemote.BuildScript(RemoteCommands.CatIfPresent(target), null);
        var psi = new System.Diagnostics.ProcessStartInfo(sh, "-s") { WorkingDirectory = dir.Path, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var p = System.Diagnostics.Process.Start(psi)!;
        await p.StandardInput.WriteAsync(script);
        p.StandardInput.Close();
        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        Assert.True(p.ExitCode == 0, stderr);
        Assert.Equal("{\n \"k\": \"it's\"\n}\n", File.ReadAllText(Path.Combine(dir.Path, odd, "current.json")));
        Assert.Equal("{\n \"k\": \"it's\"\n}\n", stdout);
    }

    private static string? FindSh()
    {
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var f = Path.Combine(d, OperatingSystem.IsWindows() ? "sh.exe" : "sh");
            if (File.Exists(f)) return f;
        }
        return null;
    }
}
