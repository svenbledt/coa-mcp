using System.IO.Hashing;

namespace CoaMcp.Pipeline.Mpq;

/// <summary>Decides one entry's status and path, and writes its bytes. Shared by restore and by listfile when an entry gains a name.</summary>
public static class EntryWriter
{
    private const string RecoveredPrefix = "recovered:";

    /// <summary>Where an entry belongs under its archive's folder. An unsafe name gets its literal form, which is only ever recorded.</summary>
    public static string PathFor(MpqEntry e, string? name, bool lowestLocale)
    {
        if (name is null) return $"(unnamed)/File{e.BlockIndex:D8}.xxx";
        var path = SafePath.Normalize(name, out _) ?? name.Replace('\\', '/');
        return lowestLocale ? path : $"(locale-{e.Locale:x4})/{path}";
    }

    /// <param name="previous">This entry's row from an earlier run of the same archive contents, if any.</param>
    /// <param name="trustDisk">False re-extracts even a file that looks right: the archive was replaced, so notes from the earlier read don't carry over.</param>
    public static FileRow Write(MpqArchive mpq, string archive, string root, MpqEntry e, string? name, string? source, bool lowestLocale, FileRow? previous, Counters? progress, bool trustDisk = true)
    {
        var path = PathFor(e, name, lowestLocale);
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

        uint? attr = null;
        try
        {
            var full = SafePath.Under(root, path);
            attr = mpq.AttributesCrc(e, name);   // always the archive's current value, never an earlier run's
            if (trustDisk && File.Exists(full))
            {
                // (attributes) outranks an earlier run's CRC: that run could have recorded wrong bytes.
                var expected = attr ?? (previous is { Status: MpqRestorer.StatusOk or MpqRestorer.StatusUnnamed } && previous.Path == path ? previous.Crc : null);
                if (AlreadyCorrect(full, e.Size, expected))
                {
                    progress?.CountSkipped();
                    var kept = previous?.Reason is { } note && note.StartsWith(RecoveredPrefix, StringComparison.Ordinal) && previous.Path == path ? note : null;
                    return Row(unnamedStatus ?? MpqRestorer.StatusOk, kept, expected, attr);
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var part = full + ".part";
            try
            {
                // With no CRC, a wrong sector size would read "fine" and hand back wrong bytes. Only the table shows it.
                if (attr is null && SectorReader.HasCheckableTable(e.Flags, e.Size) && !SectorReader.TableMatchesHeader(mpq.FilePath, mpq.Locate(e, name), e.Size, e.Flags))
                    throw new IOException("sector table doesn't fit the header's sector size, and no CRC in (attributes) can prove another size");

                var failure = ExtractToPart(mpq, e, name, part, attr, out var actual);
                string? recoveredNote = null;
                if (failure is not null)
                {
                    var bytes = SectorReader.Recover(mpq.FilePath, mpq.Locate(e, name), e.Size, e.Flags, attr, out var shift, out var why)
                        ?? throw new IOException($"{failure}; recovery: {why}");
                    File.WriteAllBytes(part, bytes);
                    actual = attr!.Value;   // Recover returned, so the bytes match it
                    recoveredNote = $"{RecoveredPrefix} header sector shift {mpq.Locate(e, name).HeaderShift} wrong, used {shift}";
                }
                File.Move(part, full, overwrite: true);
                progress?.CountExtracted(e.Size);
                return Row(unnamedStatus ?? MpqRestorer.StatusOk, recoveredNote, actual, attr);
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
            return Row(unnamedStatus ?? MpqRestorer.StatusFailed, ex.Message, null, attr);
        }
    }

    /// <summary>StormLib's read of an entry. Returns why it can't be trusted, or null with the bytes in <paramref name="part"/>.</summary>
    private static string? ExtractToPart(MpqArchive mpq, MpqEntry e, string? name, string part, uint? attr, out uint actual)
    {
        actual = 0;
        try
        {
            var crc = new Crc32();
            using (var stream = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                mpq.Extract(e, name, stream, data => crc.Append(data));
            actual = crc.GetCurrentHashAsUInt32();
            // StormLib can read wrong bytes without an error, so a read that "worked" still has to agree.
            return attr is not null && attr != actual ? $"read bytes with CRC {actual:X8}, but (attributes) says {attr:X8}" : null;
        }
        catch (IOException ex)
        {
            return ex.Message;
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
