using System.IO.Hashing;

namespace CoaMcp.Pipeline.Mpq;

/// <summary>Decides one entry's status and path, and writes its bytes. Shared by restore and by listfile when an entry gains a name.</summary>
public static class EntryWriter
{
    public static FileRow Write(MpqArchive mpq, string archive, string root, MpqEntry e, string? name, string? source, bool lowestLocale, FileRow? previous, Counters? progress)
    {
        var path = name is null
            ? $"(unnamed)/File{e.BlockIndex:D8}.xxx"
            : SafePath.Normalize(name, out _) ?? name.Replace('\\', '/');
        if (name is not null && !lowestLocale) path = $"(locale-{e.Locale:x4})/{path}";
        var unnamedStatus = name is null ? MpqRestorer.StatusUnnamed : null;

        FileRow Row(string status, string? reason, uint? crc = null, uint? attr = null) => new(archive, e.HashIndex, e.BlockIndex, path, e.Size, crc, status, reason,
            e.Locale, e.Flags, e.Name1, e.Name2, name, source, attr);

        if (!e.BlockValid) return Row(MpqRestorer.StatusFailed, $"hash entry points at block {e.BlockIndex}, past the block table");
        var byFlags = MpqRestorer.StatusFromFlags(e.Flags);
        if (byFlags is MpqRestorer.StatusDeleteMarker or MpqRestorer.StatusPatchFile) return Row(byFlags, null);
        if (byFlags == MpqRestorer.StatusFailed) return Row(unnamedStatus ?? byFlags, "block is marked free: no file data");
        if (name is not null && SafePath.Normalize(name, out var bad) is null) return Row(MpqRestorer.StatusFailed, "unsafe name: " + bad);
        if (name is null && (e.Flags & MpqArchive.FlagEncrypted) != 0)
            return Row(MpqRestorer.StatusUnnamed, "encrypted: its key depends on the name, so the bytes can't be read yet");

        string full;
        try
        {
            full = SafePath.Under(root, path);
            var attr = previous?.AttrCrc;
            if (File.Exists(full))
            {
                attr ??= mpq.AttributesCrc(e, name);
                // (attributes) outranks an earlier run's CRC: that run could have recorded wrong bytes.
                var expected = attr ?? (previous is { Status: MpqRestorer.StatusOk or MpqRestorer.StatusUnnamed } && previous.Path == path ? previous.Crc : null);
                if (AlreadyCorrect(full, e.Size, expected))
                {
                    progress?.CountSkipped();
                    return Row(unnamedStatus ?? MpqRestorer.StatusOk, null, expected, attr);
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var part = full + ".part";
            try
            {
                var crc = new Crc32();
                using (var stream = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                    attr = mpq.Extract(e, name, stream, data => crc.Append(data)) ?? attr;
                var actual = crc.GetCurrentHashAsUInt32();
                // StormLib can read wrong bytes without an error, so a read that "worked" still has to agree.
                if (attr is not null && attr != actual)
                    throw new IOException($"read bytes with CRC {actual:X8}, but (attributes) says {attr:X8}");
                File.Move(part, full, overwrite: true);
                progress?.CountExtracted(e.Size);
                return Row(unnamedStatus ?? MpqRestorer.StatusOk, null, actual, attr);
            }
            catch
            {
                TryDelete(part);
                TryDelete(full);   // an older file here is known wrong, or we wouldn't be re-extracting
                throw;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Row(unnamedStatus ?? MpqRestorer.StatusFailed, ex.Message);
        }
    }

    private static bool AlreadyCorrect(string full, long size, uint? expectedCrc)
    {
        if (expectedCrc is null) return false;
        var info = new FileInfo(full);
        if (!info.Exists || info.Length != size) return false;
        var crc = new Crc32();
        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        crc.Append(stream);
        return crc.GetCurrentHashAsUInt32() == expectedCrc;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the failure being reported matters more; a re-run replaces it */ }
    }
}
