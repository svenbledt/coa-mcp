namespace CoaMcp.Pipeline.Mpq;

/// <summary>
/// Spots an archive that's damaged or half-written yet still opens. Such an archive's entries can't be trusted to say
/// what it holds, so nothing may be removed because of them.
/// </summary>
public static class ArchiveIntegrity
{
    /// <summary>Opens the archive read-only and returns what's wrong with it, or null if it's sound.</summary>
    public static string? Check(string path)
    {
        try
        {
            using var mpq = MpqArchive.Open(path);
            return Problem(mpq, mpq.ReadEntries().Entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    public static string? Problem(MpqArchive mpq, IReadOnlyList<MpqEntry> entries)
    {
        var pastTable = entries.Count(e => !e.BlockValid);
        if (pastTable > 0) return $"{pastTable} of {entries.Count} entries point past the block table";
        var freed = entries.Count(e => (e.Flags & MpqArchive.FlagExists) == 0);
        if (freed > 0) return $"{freed} of {entries.Count} entries point at blocks marked free";

        var offset = mpq.HeaderOffset;
        using var file = new FileStream(mpq.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Span<byte> header = stackalloc byte[0x34];
        file.Seek(offset, SeekOrigin.Begin);
        var read = file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        if (read < 16) return "too short to hold an MPQ header";
        var format = BitConverter.ToUInt16(header[12..]);
        // Formats 3 and 4 keep a 64-bit size, because the 32-bit one can't hold their sizes.
        var claimed = format >= 2 && read >= 0x34 ? BitConverter.ToInt64(header[0x2C..]) : BitConverter.ToUInt32(header[8..]);
        var actual = file.Length - offset;
        return claimed == actual ? null : $"header says the archive is {claimed:N0} bytes, the file has {actual:N0}";
    }
}
