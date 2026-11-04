using CoaMcp.Core;
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

    /// <summary>What each spell page gives as its source. From a winners folder it names the client and the archive that won Spell.dbc.</summary>
    public string SourceText { get; }

    public ClientRelease? Client { get; }

    /// <summary>A line for each page when the archive that won Spell.dbc differs from the release file.</summary>
    public string? SourceWarning { get; }

    private SpellSource(McpServerConfig config, SpellDataService service, string dbcDir, DateOnly sourceDate, string sourceText, ClientRelease? client, string? sourceWarning)
    {
        Config = config;
        Spells = new SpellTools(service);
        Decoder = new DecoderTools(service);
        DbcDir = dbcDir;
        SourceDate = sourceDate;
        SourceText = sourceText;
        Client = client;
        SourceWarning = sourceWarning;
    }

    /// <summary>All spell ids in the loaded data, ascending.</summary>
    public IReadOnlyList<int> SpellIds => SpellWork.DBC.DBC.SpellInfoStore.Keys.Order().ToList();

    public string? SpellName(int id) =>
        SpellWork.DBC.DBC.SpellInfoStore.TryGetValue(id, out var s) ? s.Name : null;

    public SpellWork.Spell.SpellInfo? Info(int id) =>
        SpellWork.DBC.DBC.SpellInfoStore.TryGetValue(id, out var s) ? s : null;

    /// <summary>
    /// A winners folder's date for the spell layer: the client's one date, the same as every other client layer.
    /// The copy's own time says only when the folder was made. A folder that isn't a winners folder is refused.
    /// </summary>
    public static DateOnly SourceDateOf(string winnersDir)
    {
        var list = CoaMcp.Pipeline.Dbc.DbcWinnerList.Read(winnersDir);
        if (list.ClientDate is null)
            throw new InvalidOperationException($"The winners list in '{winnersDir}' has no client date. Run the dbc command again.");
        return DateOnly.ParseExact(list.ClientDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Loads a winners folder made by the dbc command, dated by its winners list.</summary>
    public static SpellSource OpenWinners(string winnersDir, string locale = "enUS")
    {
        var date = SourceDateOf(winnersDir);
        var list = CoaMcp.Pipeline.Dbc.DbcWinnerList.Read(winnersDir);
        if (!list.Files.TryGetValue("Spell.dbc", out var spell))
            throw new InvalidOperationException($"The winners list in '{winnersDir}' has no Spell.dbc.");
        var origin = spell.Origin == "project" ? "project-built archive" : $"origin {spell.Origin}";
        // This layer's data comes from Spell.dbc's winner, so that is the only archive its answers need to flag.
        var client = list.Client is null ? null : list.Client with
        {
            Mismatches = list.Client.Mismatches?.Where(m => string.Equals(m.Archive, spell.Archive, StringComparison.OrdinalIgnoreCase)).ToList() is { Count: > 0 } kept ? kept : null,
        };
        return Open(winnersDir, date, locale, $"{ClientRelease.Source(SpellExporter.Source, list.Client)}, Spell.dbc from {spell.Archive} ({origin})", client, client?.MismatchLine(spell.Archive));
    }

    public static SpellSource Open(string dbcDir, DateOnly sourceDate, string locale = "enUS", string? sourceText = null, ClientRelease? client = null, string? sourceWarning = null)
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

        return new SpellSource(config, service, dbcDir, sourceDate, sourceText ?? SpellExporter.Source, client, sourceWarning);
    }
}
