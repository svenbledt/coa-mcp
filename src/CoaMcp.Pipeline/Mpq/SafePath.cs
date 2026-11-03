namespace CoaMcp.Pipeline.Mpq;

/// <summary>Turns a name from inside an archive into a relative path that can't leave its folder, and guards output folders.</summary>
public static class SafePath
{
    private const int MaxSegment = 255;

    private static readonly HashSet<string> Reserved = new(
        new[] { "CON", "PRN", "AUX", "NUL" }
            .Concat(Enumerable.Range(1, 9).SelectMany(i => new[] { "COM" + i, "LPT" + i })),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the path with / separators, or null with the reason this name can't be written.</summary>
    public static string? Normalize(string name, out string? reason)
    {
        reason = null;
        if (name.Length == 0) { reason = "empty name"; return null; }
        var unified = name.Replace('\\', '/');
        if (unified[0] == '/') { reason = "absolute path"; return null; }
        if (unified.Length >= 2 && char.IsAsciiLetter(unified[0]) && unified[1] == ':') { reason = "drive letter"; return null; }

        var segments = new List<string>();
        foreach (var segment in unified.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..") { reason = "path traversal"; return null; }
            foreach (var c in segment)
                if (c < 32 || c is '<' or '>' or ':' or '"' or '|' or '?' or '*') { reason = $"invalid character 0x{(int)c:X2}"; return null; }
            if (segment[^1] is '.' or ' ') { reason = "trailing dot or space"; return null; }
            if (Reserved.Contains(segment.Split('.')[0])) { reason = "reserved device name"; return null; }
            if (segment.Length > MaxSegment) { reason = "segment too long"; return null; }
            segments.Add(segment);
        }
        if (segments.Count == 0) { reason = "empty name"; return null; }
        return string.Join('/', segments);
    }

    /// <summary>The file name for an archive's listfile: its relative path with / flattened.</summary>
    public static string ListfileName(string archive) => archive.Replace("/", "__") + ".listfile.txt";

    /// <summary>The full path of <paramref name="relative"/> under <paramref name="root"/>, as a last check that it stays inside.</summary>
    public static string Under(string root, string relative)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"path escapes the output folder: {relative}");
        return full;
    }

    public static bool IsInside(string path, string folder)
    {
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var f = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return p.Equals(f, StringComparison.OrdinalIgnoreCase) ||
               p.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Refuses an output folder inside the repo: client files must never sit where git could pick them up.</summary>
    public static void RefuseRepo(string output)
    {
        for (var dir = new DirectoryInfo(Path.GetFullPath(output)); dir is not null; dir = dir.Parent)
            if (dir.Exists && File.Exists(Path.Combine(dir.FullName, "CoaMcp.sln")))
                throw new InvalidOperationException($"--out must be outside the repo: {output} is inside {dir.FullName}");
    }
}
