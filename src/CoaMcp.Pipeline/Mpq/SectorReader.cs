using System.IO.Compression;
using System.IO.Hashing;

namespace CoaMcp.Pipeline.Mpq;

/// <summary>
/// Reads a compressed file's sectors directly from an archive when the header's sector size is wrong for it, which
/// StormLib has no way to override. A result counts only if its length and its (attributes) CRC both match.
/// Handles what such files use: stored and zlib sectors, with or without a sector CRC table.
/// </summary>
public static class SectorReader
{
    public const uint FlagCompress = 0x00000200;
    private const uint FlagImplode = 0x00000100, FlagSingleUnit = 0x01000000, FlagSectorCrc = 0x04000000;
    private const int MaxShift = 12;       // 2 MiB sectors; the format's real archives stay far below this
    private const byte MaskZlib = 0x02;
    private const uint MaxFileBytes = 1u << 30;

    /// <summary>Whether the file's sector table is the length this sector size implies. A table's first offset is its own length.</summary>
    public static bool TableFits(uint firstOffset, uint size, uint flags, int shift)
    {
        var sector = 512L << shift;
        var count = (size + sector - 1) / sector;
        var entries = count + ((flags & FlagSectorCrc) != 0 ? 2 : 1);
        return firstOffset == entries * 4;
    }

    /// <summary>Whether a file keeps a sector table that can be checked: compressed, in sectors, not encrypted, not empty.</summary>
    public static bool HasCheckableTable(uint flags, uint size) =>
        size > 0 && (flags & (FlagCompress | FlagImplode)) != 0 && (flags & (FlagSingleUnit | MpqArchive.FlagEncrypted)) == 0;

    /// <summary>Whether the file's sector table fits the sector size the header claims.</summary>
    public static bool TableMatchesHeader(string archivePath, MpqLocation at, uint size, uint flags)
    {
        using var archive = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        archive.Seek(at.Offset, SeekOrigin.Begin);
        Span<byte> first = stackalloc byte[4];
        archive.ReadExactly(first);
        return TableFits(BitConverter.ToUInt32(first), size, flags, at.HeaderShift);
    }

    /// <summary>
    /// Tries the other sector sizes. Returns the bytes and the shift used, or null with why not.
    /// With no CRC to check against, nothing can be accepted.
    /// </summary>
    public static byte[]? Recover(string archivePath, MpqLocation at, uint size, uint flags, uint? attributesCrc, out int usedShift, out string why)
    {
        usedShift = -1;
        if (attributesCrc is null) { why = "no CRC in (attributes) to prove a recovered read"; return null; }
        if ((flags & FlagCompress) == 0 || (flags & (FlagImplode | FlagSingleUnit | MpqArchive.FlagEncrypted | MpqArchive.FlagPatchFile)) != 0)
        {
            why = $"file layout not supported by the sector reader (flags 0x{flags:X8})";
            return null;
        }

        if (size > MaxFileBytes || at.CompressedSize > MaxFileBytes) { why = "file is too large for the sector reader"; return null; }

        var stored = new byte[at.CompressedSize];
        using (var archive = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            archive.Seek(at.Offset, SeekOrigin.Begin);
            archive.ReadExactly(stored);
        }
        if (stored.Length < 4) { why = "stored data is too short to hold a sector table"; return null; }

        var first = BitConverter.ToUInt32(stored, 0);
        why = "no other sector size fits the file's sector table";
        for (var shift = 0; shift <= MaxShift; shift++)
        {
            if (shift == at.HeaderShift || !TableFits(first, size, flags, shift)) continue;
            var decoded = Decode(stored, size, flags, shift, out var problem);
            if (decoded is null) { why = $"sector shift {shift}: {problem}"; continue; }
            var crc = Crc32.HashToUInt32(decoded);
            if (crc != attributesCrc)
            {
                why = $"sector shift {shift} gave the right length but CRC {crc:X8}, and (attributes) says {attributesCrc:X8}";
                continue;
            }
            usedShift = shift;
            return decoded;
        }
        return null;
    }

    /// <summary>Decodes all sectors at one sector size. Returns null with a reason on anything inconsistent, including the wrong total length.</summary>
    public static byte[]? Decode(byte[] stored, uint size, uint flags, int shift, out string why)
    {
        if (size > MaxFileBytes) { why = "file is too large for the sector reader"; return null; }
        var sector = 512L << shift;
        var count = (int)((size + sector - 1) / sector);
        var entries = count + ((flags & FlagSectorCrc) != 0 ? 2 : 1);
        if (entries * 4L > stored.Length) { why = "sector table runs past the file's data"; return null; }

        var table = new uint[entries];
        Buffer.BlockCopy(stored, 0, table, 0, entries * 4);
        if (table[0] != entries * 4) { why = $"sector table starts at {table[0]}, expected {entries * 4}"; return null; }
        for (var i = 0; i < count; i++)
            if (table[i + 1] < table[i] || table[i + 1] > stored.Length) { why = $"sector {i} has bad offsets"; return null; }

        var output = new byte[size];
        var written = 0;
        for (var i = 0; i < count; i++)
        {
            var want = (int)Math.Min(sector, size - written);
            var raw = stored.AsSpan((int)table[i], (int)(table[i + 1] - table[i]));
            if (raw.Length == want) raw.CopyTo(output.AsSpan(written));
            else if (raw.Length == 0) { why = $"sector {i} is empty"; return null; }
            else if (raw[0] != MaskZlib) { why = $"sector {i} uses compression 0x{raw[0]:X2}, which the sector reader doesn't support"; return null; }
            else if (!Inflate(raw[1..], output.AsSpan(written, want))) { why = $"sector {i} did not decompress to {want} bytes"; return null; }
            written += want;
        }
        why = "";
        return output;
    }

    private static bool Inflate(ReadOnlySpan<byte> packed, Span<byte> into)
    {
        try
        {
            using var z = new ZLibStream(new MemoryStream(packed.ToArray()), CompressionMode.Decompress);
            var total = z.ReadAtLeast(into, into.Length, throwOnEndOfStream: false);
            return total == into.Length && z.ReadByte() < 0;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
