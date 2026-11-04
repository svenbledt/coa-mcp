namespace CoaMcp.Pipeline.Mpq;

/// <summary>Removes restored files. Paths come from the manifest, so each one must prove it stays inside its archive's folder.</summary>
public static class RestoredFiles
{
    /// <summary>Deletes these paths under <paramref name="root"/>, and any folders that leaves empty. A path that isn't safe is skipped: nothing was ever written for it.</summary>
    public static void Remove(string root, IEnumerable<string> relativePaths)
    {
        foreach (var relative in relativePaths)
        {
            if (SafePath.Normalize(relative, out _) is null) continue;
            var full = SafePath.Under(root, relative);
            if (!File.Exists(full)) continue;
            File.Delete(full);
            PruneEmptyFolders(Path.GetDirectoryName(full)!, root);
        }
    }

    /// <summary>Removes <paramref name="from"/> and its parents while they're empty, stopping at <paramref name="stopAt"/>, which stays.</summary>
    public static void PruneEmptyFolders(string from, string stopAt)
    {
        var stop = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stopAt));
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(from));
        while (!folder.Equals(stop, StringComparison.OrdinalIgnoreCase) && SafePath.IsInside(folder, stop)
               && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
        {
            Directory.Delete(folder);
            folder = Path.GetDirectoryName(folder)!;
        }
    }
}
