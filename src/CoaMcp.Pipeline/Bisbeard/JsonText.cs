using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace CoaMcp.Pipeline.Bisbeard;

/// <summary>JSON helpers shared by the realm-data importers: placeholder stripping and plain Markdown rendering.</summary>
internal static class JsonText
{
    /// <summary>A deep copy with every "[function ...]" placeholder removed.</summary>
    public static JsonNode Clean(JsonNode node)
    {
        var copy = node.DeepClone();
        Strip(copy);
        return copy;
    }

    private static bool IsPlaceholder(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) && s.StartsWith("[function", StringComparison.Ordinal);

    private static void Strip(JsonNode? n)
    {
        if (n is JsonObject o)
        {
            foreach (var key in o.Where(p => IsPlaceholder(p.Value)).Select(p => p.Key).ToList()) o.Remove(key);
            foreach (var p in o) Strip(p.Value);
        }
        else if (n is JsonArray a)
        {
            for (var i = a.Count - 1; i >= 0; i--)
                if (IsPlaceholder(a[i])) a.RemoveAt(i);
            foreach (var x in a) Strip(x);
        }
    }

    public static string Scalar(JsonNode? n) => n switch
    {
        null => "none",
        JsonValue v when v.TryGetValue<double>(out var d) => d.ToString("0.###", CultureInfo.InvariantCulture),
        JsonValue v when v.TryGetValue<string>(out var s) => s.Replace("\r", "").Replace("\n", " ").Trim(),
        _ => n.ToJsonString(),
    };

    public static string? Str(JsonNode? n, string key) => n is JsonObject o && o[key] is { } v ? Scalar(v) : null;

    /// <summary>Renders any JSON as a nested bullet list: "- key: value".</summary>
    public static string Bullets(JsonNode? node)
    {
        var sb = new StringBuilder();
        Walk(node, 0, sb);
        return sb.ToString();
    }

    private static bool IsScalar(JsonNode? n) => n is null or JsonValue;

    private static void Walk(JsonNode? node, int indent, StringBuilder sb)
    {
        var pad = new string(' ', indent);
        switch (node)
        {
            case JsonObject o:
                foreach (var (k, v) in o)
                {
                    if (IsScalar(v)) sb.Append(pad).Append("- ").Append(k).Append(": ").Append(Scalar(v)).Append('\n');
                    else if (v is JsonArray a && a.All(IsScalar))
                        sb.Append(pad).Append("- ").Append(k).Append(": ").Append(string.Join(", ", a.Select(Scalar))).Append('\n');
                    else
                    {
                        sb.Append(pad).Append("- ").Append(k).Append(":\n");
                        Walk(v, indent + 2, sb);
                    }
                }
                break;
            case JsonArray arr:
                foreach (var x in arr)
                {
                    sb.Append(pad).Append("-\n");
                    Walk(x, indent + 2, sb);
                }
                break;
        }
    }
}
