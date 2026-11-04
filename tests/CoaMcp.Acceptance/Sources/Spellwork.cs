using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace CoaMcp.Acceptance;

/// <summary>The local Spellwork CoA tool over stdio, with no database settings: the reference the server's spell answers are compared with.</summary>
public sealed class Spellwork : IAsyncDisposable
{
    /// <summary>The sections the spell export asks for. "items" needs the emulator's world database, which the server leaves out by design.</summary>
    public static readonly string[] Sections =
    {
        "misc", "attributes", "effects", "powers", "targets", "reagents", "aura", "procs", "context",
        "cooldowns", "levels", "interrupts", "shapeshift", "equippedItems", "triggers", "totems", "visual",
    };

    private readonly McpClient _client;

    private Spellwork(McpClient client) => _client = client;

    public static async Task<Spellwork> Start()
    {
        var env = new Dictionary<string, string?>
        {
            ["SPELLWORK_DBC_PATH"] = Settings.SpellworkDbc,
            ["SPELLWORK_MYSQL_HOST"] = null, ["SPELLWORK_MYSQL_PORT"] = null, ["SPELLWORK_MYSQL_USER"] = null,
            ["SPELLWORK_MYSQL_PASS"] = null, ["SPELLWORK_MYSQL_WORLD_DB"] = null,
        };
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "spellwork-coa",
            Command = "dotnet",
            Arguments = new[] { Settings.SpellworkDll },
            EnvironmentVariables = env,
        });
        return new Spellwork(await McpClient.CreateAsync(transport));
    }

    private async Task<JsonElement> Call(string tool, Dictionary<string, object?> args)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var r = await _client.CallToolAsync(tool, args, cancellationToken: timeout.Token);
        var text = string.Join("", r.Content.OfType<TextContentBlock>().Select(c => c.Text));
        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.GetProperty("ok").GetBoolean()) throw new InvalidOperationException($"{tool} failed locally: {text}");
        return doc.RootElement.GetProperty("result").Clone();
    }

    public Task<JsonElement> GetSpell(int id) =>
        Call("get_spell", new Dictionary<string, object?> { ["id"] = id, ["format"] = "json", ["sections"] = Sections });

    public Task<JsonElement> Scaling(int id, int level) =>
        Call("calculate_spell_scaling", new Dictionary<string, object?> { ["id"] = id, ["level"] = level, ["itemLevel"] = 0 });

    public Task<JsonElement> DecodeFlags(string kind, ulong value) =>
        Call("decode_flags", new Dictionary<string, object?> { ["kind"] = kind, ["value"] = value });

    public async ValueTask DisposeAsync() => await _client.DisposeAsync();
}
