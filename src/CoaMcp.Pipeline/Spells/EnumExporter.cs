using System.Text.Json;
using CoaMcp.Core;

namespace CoaMcp.Pipeline.Spells;

/// <summary>Exports the enum names Spellwork knows, so the server can decode flags without Spellwork.</summary>
public static class EnumExporter
{
    /// <returns>Member rows written and the number of flag kinds.</returns>
    public static (int Members, int FlagKinds) Export(SpellSource source, LayerWriter w)
    {
        w.Execute("CREATE TABLE enums(name TEXT NOT NULL, value INTEGER NOT NULL, value_text TEXT NOT NULL, label TEXT NOT NULL)");
        w.Execute("CREATE INDEX enums_name ON enums(name, value)");
        w.Execute("CREATE TABLE flag_kinds(kind TEXT PRIMARY KEY, enum_name TEXT NOT NULL)");

        var names = Result(source.Decoder.ListEnum().GetAwaiter().GetResult()).GetProperty("enums")
            .EnumerateArray().Select(e => e.GetString()!).ToList();
        var members = 0;
        var kinds = 0;
        foreach (var name in names)
        {
            var offset = 0;
            while (true)
            {
                var page = Result(source.Decoder.ListEnum(name, null, 500, offset).GetAwaiter().GetResult());
                if (offset == 0 && page.GetProperty("isFlags").GetBoolean())
                {
                    w.Execute("INSERT INTO flag_kinds VALUES ($0,$1)", name, name);
                    kinds++;
                }
                var rows = page.GetProperty("rows");
                foreach (var row in rows.EnumerateArray())
                {
                    // ulong-backed enums can hold values above long.MaxValue: keep the exact text and the bit pattern.
                    var text = row.GetProperty("value").GetRawText();
                    var bits = ulong.TryParse(text, out var u) ? unchecked((long)u) : long.Parse(text);
                    w.Execute("INSERT INTO enums VALUES ($0,$1,$2,$3)", name, bits, text, row.GetProperty("name").GetString()!);
                    members++;
                }
                offset += rows.GetArrayLength();
                if (!page.GetProperty("truncated").GetBoolean() || rows.GetArrayLength() == 0) break;
            }
        }
        return (members, kinds);
    }

    private static JsonElement Result(string json)
    {
        var root = JsonDocument.Parse(json).RootElement;
        if (!root.GetProperty("ok").GetBoolean())
            throw new InvalidOperationException("list_enum failed: " + root.GetProperty("error").GetProperty("message").GetString());
        return root.GetProperty("result");
    }
}
