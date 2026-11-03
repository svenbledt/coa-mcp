using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace CoaMcp.Pipeline.Mpq;

/// <summary>One used slot of an archive's hash table, joined with its block table record.</summary>
public readonly record struct MpqEntry(int HashIndex, uint Name1, uint Name2, ushort Locale, uint BlockIndex, uint Size, uint Flags, bool BlockValid);

/// <summary>Where an entry's stored bytes sit in the archive file, and the sector shift the header claims.</summary>
public readonly record struct MpqLocation(long Offset, uint CompressedSize, int HeaderShift);

/// <summary>Read-only handle on one MPQ. Not thread-safe: use one handle per thread.</summary>
public sealed class MpqArchive : IDisposable
{
    public const uint FlagEncrypted = 0x00010000, FlagPatchFile = 0x00100000, FlagDeleteMarker = 0x02000000, FlagExists = 0x80000000;

    // MPQ_OPEN_READ_ONLY and STREAM_FLAG_READ_ONLY are the same bit. This wrapper exposes no write call at all.
    private const uint OpenReadOnly = 0x00000100, OpenNoListfile = 0x00010000, OpenCheckSectorCrc = 0x00100000;
    private const uint HashFree = 0xFFFFFFFF, HashDeleted = 0xFFFFFFFE;
    private const int ErrorHandleEof = 38;

    // SFileSetLocale sets a process-wide value that SFileOpenFileEx reads, so the pair must not interleave.
    // Opening archives shares this lock too: StormLib builds its crypt table on first use.
    private static readonly Lock OpenLock = new();

    private IntPtr _mpq;

    private MpqArchive(IntPtr mpq, string path)
    {
        _mpq = mpq;
        FilePath = path;
    }

    public string FilePath { get; }

    public static MpqArchive Open(string path)
    {
        lock (OpenLock)
        {
            if (!SFileOpenArchive(path, 0, OpenReadOnly | OpenNoListfile | OpenCheckSectorCrc, out var mpq))
                throw new IOException($"cannot open archive: {Message(Marshal.GetLastPInvokeError())}");
            return new MpqArchive(mpq, path);
        }
    }

    /// <summary>Reads the hash and block tables as StormLib loaded them. Free and deleted hash slots are left out.</summary>
    public (List<MpqEntry> Entries, int HashSlots, int BlockSlots) ReadEntries()
    {
        var hashSlots = (int)InfoUInt32(InfoClass.HashTableSize);
        var blockSlots = (int)InfoUInt32(InfoClass.BlockTableSize);
        var hash = InfoBytes(InfoClass.HashTable, hashSlots * 16);
        var block = InfoBytes(InfoClass.BlockTable, blockSlots * 16);
        var entries = new List<MpqEntry>();
        for (var i = 0; i < hashSlots; i++)
        {
            var at = i * 16;
            var blockIndex = BitConverter.ToUInt32(hash, at + 12);
            if (blockIndex is HashFree or HashDeleted) continue;
            var valid = blockIndex < blockSlots;
            var size = valid ? BitConverter.ToUInt32(block, (int)blockIndex * 16 + 8) : 0;
            var flags = valid ? BitConverter.ToUInt32(block, (int)blockIndex * 16 + 12) : 0;
            entries.Add(new MpqEntry(i, BitConverter.ToUInt32(hash, at), BitConverter.ToUInt32(hash, at + 4),
                BitConverter.ToUInt16(hash, at + 8), blockIndex, size, flags, valid));
        }
        return (entries, hashSlots, blockSlots);
    }

    /// <summary>The bytes of an internal or listed file, or null if no entry has this name.</summary>
    public byte[]? TryReadAll(string name, ushort locale)
    {
        if (!TryOpen(name, locale, out var file)) return null;
        try
        {
            using var mem = new MemoryStream();
            Copy(file, mem, null);
            return mem.ToArray();
        }
        finally { SFileCloseFile(file); }
    }

    /// <summary>
    /// Writes one entry's bytes to <paramref name="destination"/>, opening it by name when known and by block
    /// index otherwise. Fails if the open lands on any other entry. Returns the (attributes) CRC, null if none.
    /// </summary>
    public uint? Extract(MpqEntry entry, string? name, Stream destination, Action<ReadOnlySpan<byte>>? onData = null)
    {
        var open = name ?? $"File{entry.BlockIndex:D8}.xxx";
        if (!TryOpen(open, entry.Locale, out var file))
            throw new IOException($"open failed: {Message(Marshal.GetLastPInvokeError())}");
        try
        {
            var index = FileInfoUInt32(file, InfoClass.FileIndex);
            if (index != entry.BlockIndex) throw new IOException($"open reached block {index}, expected {entry.BlockIndex}");
            var total = Copy(file, destination, onData);
            if (total != entry.Size) throw new IOException($"read {total} bytes, block table says {entry.Size}");
            var crc = FileInfoUInt32(file, InfoClass.FileCrc32);
            return crc == 0 ? null : crc;
        }
        finally { SFileCloseFile(file); }
    }

    /// <summary>Where an entry's stored bytes are in the archive file, and the sector size the header claims.</summary>
    public MpqLocation Locate(MpqEntry entry, string? name)
    {
        if (!TryOpen(name ?? $"File{entry.BlockIndex:D8}.xxx", entry.Locale, out var file))
            throw new IOException($"open failed: {Message(Marshal.GetLastPInvokeError())}");
        try
        {
            var relative = FileInfoUInt64(file, InfoClass.FileByteOffset);
            var sectorSize = InfoUInt32(InfoClass.SectorSize);
            return new MpqLocation((long)(FileInfoUInt64(_mpq, InfoClass.HeaderOffset) + relative),
                FileInfoUInt32(file, InfoClass.FileCompressedSize), System.Numerics.BitOperations.Log2(sectorSize) - 9);
        }
        finally { SFileCloseFile(file); }
    }

    /// <summary>The (attributes) CRC of an entry, or null if none. Does not read file data.</summary>
    public uint? AttributesCrc(MpqEntry entry, string? name)
    {
        if (!TryOpen(name ?? $"File{entry.BlockIndex:D8}.xxx", entry.Locale, out var file)) return null;
        try
        {
            if (FileInfoUInt32(file, InfoClass.FileIndex) != entry.BlockIndex) return null;
            var crc = FileInfoUInt32(file, InfoClass.FileCrc32);
            return crc == 0 ? null : crc;
        }
        finally { SFileCloseFile(file); }
    }

    public void Dispose()
    {
        if (_mpq == IntPtr.Zero) return;
        SFileCloseArchive(_mpq);
        _mpq = IntPtr.Zero;
    }

    private bool TryOpen(string name, ushort locale, out IntPtr file)
    {
        var bytes = Encoding.Latin1.GetBytes(name + "\0");
        lock (OpenLock)
        {
            SFileSetLocale(locale);
            return SFileOpenFileEx(_mpq, bytes, 0, out file);
        }
    }

    private static long Copy(IntPtr file, Stream destination, Action<ReadOnlySpan<byte>>? onData)
    {
        var buffer = new byte[1 << 20];
        long total = 0;
        while (true)
        {
            var ok = SFileReadFile(file, buffer, (uint)buffer.Length, out var read, IntPtr.Zero);
            var error = ok ? 0 : Marshal.GetLastPInvokeError();
            if (read > 0)
            {
                destination.Write(buffer, 0, (int)read);
                onData?.Invoke(buffer.AsSpan(0, (int)read));
                total += read;
            }
            if (error == ErrorHandleEof || (ok && read == 0)) return total;
            if (!ok) throw new IOException($"read failed: {Message(error)}");
        }
    }

    private uint InfoUInt32(InfoClass info) => FileInfoUInt32(_mpq, info);

    private static uint FileInfoUInt32(IntPtr handle, InfoClass info)
    {
        var bytes = new byte[4];
        if (!SFileGetFileInfo(handle, info, bytes, 4, out _))
            throw new IOException($"file info {info} failed: {Message(Marshal.GetLastPInvokeError())}");
        return BitConverter.ToUInt32(bytes);
    }

    private static ulong FileInfoUInt64(IntPtr handle, InfoClass info)
    {
        var bytes = new byte[8];
        if (!SFileGetFileInfo(handle, info, bytes, 8, out _))
            throw new IOException($"file info {info} failed: {Message(Marshal.GetLastPInvokeError())}");
        return BitConverter.ToUInt64(bytes);
    }

    private byte[] InfoBytes(InfoClass info, int size)
    {
        var bytes = new byte[size];
        if (size > 0 && !SFileGetFileInfo(_mpq, info, bytes, (uint)size, out _))
            throw new IOException($"file info {info} failed: {Message(Marshal.GetLastPInvokeError())}");
        return bytes;
    }

    private static string Message(int error) => $"{new Win32Exception(error).Message} (error {error})";

    /// <summary>SFileInfoClass values from StormLib v9.40's StormLib.h, which build-stormlib.ps1 pins.</summary>
    private enum InfoClass
    {
        HeaderOffset = 5, HashTableSize = 18, HashTable = 19, BlockTableSize = 22, BlockTable = 23, SectorSize = 35,
        FileIndex = 48, FileByteOffset = 49, FileCompressedSize = 52, FileCrc32 = 56,
    }

    [DllImport("StormLib", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SFileOpenArchive(string name, uint priority, uint flags, out IntPtr mpq);

    [DllImport("StormLib", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SFileCloseArchive(IntPtr mpq);

    [DllImport("StormLib")]
    private static extern uint SFileSetLocale(uint locale);

    [DllImport("StormLib", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SFileOpenFileEx(IntPtr mpq, byte[] name, uint scope, out IntPtr file);

    [DllImport("StormLib", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SFileReadFile(IntPtr file, byte[] buffer, uint toRead, out uint read, IntPtr overlapped);

    [DllImport("StormLib", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SFileCloseFile(IntPtr file);

    [DllImport("StormLib", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SFileGetFileInfo(IntPtr handle, InfoClass info, byte[] buffer, uint size, out uint needed);
}
