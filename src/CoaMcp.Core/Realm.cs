namespace CoaMcp.Core;

/// <summary>The realms an entity applies to. Unproven means "one of these", proven means "all of these".</summary>
public sealed record RealmSet(IReadOnlyList<string> Realms, bool Proven)
{
    private static string Name(string realm) => realm switch
    {
        "coa" => "CoA",
        "coa-client" => "the CoA client",
        "classic" => "Classic",
        "s10" => "S10",
        _ => realm,
    };

    public string Label()
    {
        var names = Realms.Select(Name).ToList();
        if (names.Count == 0) return "unknown";
        if (names.Count == 1) return names[0];
        var joiner = Proven ? " and " : " or ";
        return string.Join(", ", names.Take(names.Count - 1)) + joiner + names[^1];
    }
}
