using Microsoft.Extensions.Logging.Abstractions;
using SpellWork.Mcp;
using SpellWork.Mcp.Tools;

namespace CoaMcp.Pipeline.Spells;

/// <summary>
/// One Spellwork load of a client DBC folder. Spellwork keeps DBCs in process-wide statics, so only one
/// source is live at a time: opening another replaces the data every earlier source reads.
/// </summary>
public sealed class SpellSource
{
    // Variables that would point Spellwork at the emulator's database or other data. They are cleared while
    // the config is read, so a value inherited from the shell can never reach the export.
    private static readonly string[] Cleared =
    {
        "SPELLWORK_MYSQL_HOST", "SPELLWORK_MYSQL_PORT", "SPELLWORK_MYSQL_USER", "SPELLWORK_MYSQL_PASS",
        "SPELLWORK_MYSQL_WORLD_DB", "SPELLWORK_DB2_PATH", "SPELLWORK_GT_PATH",
    };

    public McpServerConfig Config { get; }
    public SpellTools Spells { get; }
    public DecoderTools Decoder { get; }
    public string DbcDir { get; }
    public DateOnly SourceDate { get; }

    private SpellSource(McpServerConfig config, SpellDataService service, string dbcDir, DateOnly sourceDate)
    {
        Config = config;
        Spells = new SpellTools(service);
        Decoder = new DecoderTools(service);
        DbcDir = dbcDir;
        SourceDate = sourceDate;
    }

    /// <summary>All spell ids in the loaded data, ascending.</summary>
    public IReadOnlyList<int> SpellIds => SpellWork.DBC.DBC.SpellInfoStore.Keys.Order().ToList();

    public string? SpellName(int id) =>
        SpellWork.DBC.DBC.SpellInfoStore.TryGetValue(id, out var s) ? s.Name : null;

    public SpellWork.Spell.SpellInfo? Info(int id) =>
        SpellWork.DBC.DBC.SpellInfoStore.TryGetValue(id, out var s) ? s : null;

    public static SpellSource Open(string dbcDir, string locale = "enUS")
    {
        var spellFile = Path.Combine(dbcDir, "Spell.dbc");
        if (!File.Exists(spellFile)) throw new InvalidOperationException($"No Spell.dbc in '{dbcDir}'.");

        var names = Cleared.Append("SPELLWORK_DBC_PATH").Append("SPELLWORK_LOCALE").ToArray();
        var saved = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        McpServerConfig config;
        try
        {
            foreach (var n in Cleared) Environment.SetEnvironmentVariable(n, null);
            Environment.SetEnvironmentVariable("SPELLWORK_DBC_PATH", dbcDir);
            Environment.SetEnvironmentVariable("SPELLWORK_LOCALE", locale);
            config = McpServerConfig.FromEnvironment();
        }
        finally
        {
            foreach (var (n, v) in saved) Environment.SetEnvironmentVariable(n, v);
        }

        if (config.MySqlConfigured) throw new InvalidOperationException("Spellwork config still has a database; refusing to export.");

        var service = new SpellDataService(config, NullLogger<SpellDataService>.Instance);
        var data = service.GetAsync().GetAwaiter().GetResult();
        if (!data.Loaded) throw new InvalidOperationException($"Spellwork could not load '{dbcDir}': {data.ErrorMessage}");

        return new SpellSource(config, service, dbcDir, DateOnly.FromDateTime(File.GetLastWriteTimeUtc(spellFile)));
    }
}
