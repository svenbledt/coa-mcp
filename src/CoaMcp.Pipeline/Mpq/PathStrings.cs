namespace CoaMcp.Pipeline.Mpq;

/// <summary>Finds file path strings in client files, and the names those paths imply.</summary>
public static class PathStrings
{
    public const string SourceStrings = "restored_strings", SourceVariant = "extension_variant", SourceSkin = "skin_pattern", SourceTocRelative = "toc_relative";

    /// <summary>Files worth scanning, from the brief.</summary>
    public static readonly string[] ScanExtensions = ["dbc", "m2", "wmo", "adt", "wdt", "lua", "xml", "toc"];

    private static readonly string[] FileExtensions =
        ["blp", "m2", "mdx", "mdl", "skin", "anim", "phys", "wmo", "adt", "wdt", "wdl", "dbc", "lua", "xml", "toc", "ogg", "wav", "mp3", "ttf", "tga", "bls", "zmp", "sbt", "txt", "html"];

    private static readonly bool[] PathChar = BuildPathChars();

    /// <summary>Printable runs in the bytes that end in a known file extension.</summary>
    public static IEnumerable<string> Extract(byte[] bytes)
    {
        var start = -1;
        for (var i = 0; i <= bytes.Length; i++)
        {
            if (i < bytes.Length && PathChar[bytes[i]])
            {
                if (start < 0) start = i;
                continue;
            }
            if (start >= 0)
            {
                foreach (var token in FromRun(bytes, start, i - start)) yield return token;
                start = -1;
            }
        }
    }

    /// <summary>Other names a path implies. Each must still match both hashes before it counts.</summary>
    public static IEnumerable<(string Name, string Source)> Variants(string name)
    {
        var dot = name.LastIndexOf('.');
        if (dot < 0) yield break;
        var stem = name[..dot];
        var ext = name[(dot + 1)..].ToLowerInvariant();
        if (ext is "mdx" or "mdl") yield return (stem + ".m2", SourceVariant);
        if (ext is "mdx" or "mdl" or "m2")
            for (var i = 0; i < 4; i++) yield return ($"{stem}{i:D2}.skin", SourceSkin);
    }

    private static IEnumerable<string> FromRun(byte[] bytes, int start, int length)
    {
        if (length < 4 || length > 512) yield break;
        var run = System.Text.Encoding.Latin1.GetString(bytes, start, length);
        var dot = run.LastIndexOf('.');
        if (dot < 0 || !FileExtensions.Contains(run[(dot + 1)..].ToLowerInvariant())) yield break;
        var token = run.Trim().Replace(@"\\", @"\");
        if (token.Length == 0) yield break;
        yield return token;
        var space = token.LastIndexOf(' ');   // "file Foo\Bar.lua": the path may start after the last word
        if (space >= 0 && space + 1 < token.Length) yield return token[(space + 1)..];
    }

    private static bool[] BuildPathChars()
    {
        var chars = new bool[256];
        foreach (var c in "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-. \\/()'&,+!#~[]") chars[c] = true;
        return chars;
    }
}
