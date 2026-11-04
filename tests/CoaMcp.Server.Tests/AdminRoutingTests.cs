using System.Diagnostics;

namespace CoaMcp.Server.Tests;

/// <summary>Runs the built server itself: `admin ...` must act and exit without ever starting the web host.</summary>
public class AdminRoutingTests
{
    private static (int Code, string Out, string Err, TimeSpan Took) RunServer(string[] args, string? root = null)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "CoaMcp.Server.dll");
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(dll);
        foreach (var a in args) info.ArgumentList.Add(a);
        info.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";   // if a host did start, it would announce this
        if (root is not null) info.Environment["Layers__Root"] = root;
        info.Environment.Remove("COA_MCP_BEARER_TOKEN");
        var clock = Stopwatch.StartNew();
        using var p = Process.Start(info)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(20_000))
        {
            p.Kill(true);
            Assert.Fail("The server did not exit: `admin` must not start the web host.");
        }
        return (p.ExitCode, stdout.Result, stderr.Result, clock.Elapsed);
    }

    [Fact]
    public void Admin_UnknownCommand_ExitsTwo_WithoutStartingTheWebHost()
    {
        var r = RunServer(new[] { "admin", "nonsense" });
        Assert.Equal(2, r.Code);
        Assert.Contains("Usage", r.Err);
        Assert.DoesNotContain("Now listening", r.Out + r.Err);
        Assert.DoesNotContain("Application started", r.Out + r.Err);
    }

    [Fact]
    public void Admin_ReadsRootFromTheSameConfigurationAsTheWebPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "coamcp-route-" + Guid.NewGuid().ToString("N"));
        var r = RunServer(new[] { "admin", "list-authors" }, root);
        Assert.Equal(1, r.Code);
        Assert.Contains(Path.Combine(root, "writable", "store.sqlite"), r.Err);
        Assert.False(Directory.Exists(root));
        Assert.DoesNotContain("Now listening", r.Out + r.Err);
    }
}
