using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
[assembly: TestCollectionOrderer("CoaMcp.Acceptance.ByNameOrderer", "CoaMcp.Acceptance")]

namespace CoaMcp.Acceptance;

/// <summary>Areas run in the order of their collection names, so the rate limit test and the live update don't land in the middle of another area.</summary>
public sealed class ByNameOrderer : ITestCollectionOrderer
{
    public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections) =>
        testCollections.OrderBy(c => c.DisplayName, StringComparer.Ordinal);
}

/// <summary>Everything the suite reads from the environment, and where the local sources live. All local sources are opened read-only.</summary>
public static class Settings
{
    public const int MinTokenChars = 32;

    public static string? Url => Environment.GetEnvironmentVariable("COA_MCP_URL") is { Length: > 0 } u ? u : null;

    public static Uri Mcp => new(Url!);
    public static string Host => Mcp.Host;
    public static Uri Health => new(Mcp, "/healthz");

    public static string RepoRoot { get; } = FindRepo();

    private static string FindRepo()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "CoaMcp.sln"))) return d.FullName;
        throw new InvalidOperationException("Run the suite from inside the repository.");
    }

    /// <summary>The token, from the environment or the local file. Never printed. Null when absent or too short for the server to accept.</summary>
    public static string? Token { get; } = ReadToken();

    private static string? ReadToken()
    {
        var value = Environment.GetEnvironmentVariable("COA_MCP_BEARER_TOKEN");
        if (string.IsNullOrWhiteSpace(value))
        {
            var file = Path.Combine(RepoRoot, ".superpowers", "acceptance-token.txt");
            if (File.Exists(file)) value = File.ReadAllText(file);
        }
        value = value?.Trim();
        return value is { Length: >= MinTokenChars } ? value : null;
    }

    public const string TokenAbsent = "token not set: neither COA_MCP_BEARER_TOKEN nor .superpowers/acceptance-token.txt holds one";

    public static int Seed { get; } = int.TryParse(Environment.GetEnvironmentVariable("COA_ACCEPT_SEED"), out var s) ? s : Random.Shared.Next(1, 1_000_000);

    public static string ReportPath => Environment.GetEnvironmentVariable("COA_ACCEPT_REPORT") is { Length: > 0 } p ? p : Path.Combine(RepoRoot, "docs", "acceptance-report.md");

    public static string SshHost => Environment.GetEnvironmentVariable("COA_ACCEPT_SSH_HOST") is { Length: > 0 } h ? h : "BledtIT";

    /// <summary>The server's direct address, for the test that goes past Cloudflare. It stays out of the repository. Without it that test is NOT RUN.</summary>
    public static string? OriginIp => Environment.GetEnvironmentVariable("COA_ACCEPT_ORIGIN_IP") is { Length: > 0 } ip ? ip : null;

    public const string CoolifyAppId = "ukfeegudmo0pfyq1nxe7tadz";

    /// <summary>The one test that changes production publishes and rolls back a layer. It runs only when asked for.</summary>
    public static bool AllowLiveUpdate => Environment.GetEnvironmentVariable("COA_ACCEPT_LIVE_UPDATE") == "1";

    /// <summary>Restarting the container interrupts everyone, so it needs its own switch as well as the token.</summary>
    public static bool AllowRestart => Environment.GetEnvironmentVariable("COA_ACCEPT_RESTART") == "1";

    /// <summary>The real client test spends model usage, so it can be turned off.</summary>
    public static bool AllowRealClient => Environment.GetEnvironmentVariable("COA_ACCEPT_REAL_CLIENT") != "0";

    public static string RealClientModel => Environment.GetEnvironmentVariable("COA_ACCEPT_REAL_CLIENT_MODEL") is { Length: > 0 } m ? m : "claude-haiku-5-5";

    public static string ItemsJson => Path.Combine(DataCoa, "api", "items_all.json");
    public static string RealmItems(string realm) => Path.Combine(DataCoa, "realms", realm, "api", "items_all.json");
    private static string DataCoa => Env("COA_ACCEPT_DATACOA", @"D:\WoW\Coa\DataCOA\data");
    public static string RestoredRoot => Env("COA_ACCEPT_RESTORED", @"F:\WorkDir\CoaRestored");
    public static string ManifestFile => Path.Combine(RestoredRoot, "manifest.sqlite");

    /// <summary>What the launcher's own state file says the client is, read here so answers are checked against the file, not against a copy of the wording.</summary>
    public static string ClientClaim
    {
        get
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Env("COA_ACCEPT_LAUNCHER_STATE", @"D:\WoW\Coa\.coa-launcher\state.json")));
            return $"client {doc.RootElement.GetProperty("version").GetString()} (launcher)";
        }
    }

    /// <summary>Origin ascension as answers must word it. Written out here so a change to the server's wording fails the suite.</summary>
    public const string AscensionWords = "Ascension's archive per project records; this copy may lag live";
    public static string Listfile => Env("COA_ACCEPT_LISTFILE", @"D:\WoW\Coa\Listfiles\client-listfile.txt");
    public static string LocalLayers => Env("COA_ACCEPT_LAYERS", Path.Combine(RepoRoot, ".superpowers", "layers"));
    public static string SpellworkDll => Env("COA_ACCEPT_SPELLWORK_DLL", @"F:\WorkDir\SpellWork\out\mcpcoa\SpellWork.Mcp.dll");
    public static string SpellworkDbc => Env("COA_ACCEPT_SPELLWORK_DBC", @"F:\WorkDir\azerothcore-wotlk-coa\env\dist\data\dbc");

    private static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

    /// <summary>Roots replaced in everything the report shows, so it doesn't describe this machine.</summary>
    public static IReadOnlyList<string> LocalRoots { get; } = new[]
    {
        RepoRoot, RestoredRoot, @"F:\WorkDir", @"D:\WoW", Path.GetTempPath().TrimEnd('\\', '/'), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), OriginIp ?? "",
    }.Where(r => !string.IsNullOrEmpty(r)).Distinct().ToList();
}
