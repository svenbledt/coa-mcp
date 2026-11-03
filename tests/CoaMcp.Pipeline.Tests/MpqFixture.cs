using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace CoaMcp.Pipeline.Tests;

/// <summary>
/// Builds small real MPQs for tests. With listfile: true StormLib writes the archive. With listfile: false this
/// class lays out a v1 MPQ itself, because StormLib refuses a hand-written (listfile) and tests need one that
/// leaves entries unnamed or lists names with no entry. The writing code lives only in tests.
/// </summary>
public sealed class MpqFixture : IDisposable
{
    public const uint Compress = 0x00000200, Encrypted = 0x00010000, PatchFile = 0x00100000,
        DeleteMarker = 0x02000000, Exists = 0x80000000, SingleUnit = 0x01000000, KeyV2 = 0x00020000;
    private const uint CreateListfile = 0x00100000, CreateAttributes = 0x00200000, CompressionZlib = 0x02;

    private const uint SectorCrc = 0x04000000;

    private readonly string _path;
    private readonly int _headerShift;
    private readonly List<(string Name, byte[] Data, uint Flags, ushort Locale)>? _raw;
    private readonly List<(string Name, byte[] Stored, uint Size, uint? AttributesCrc)> _sectored = [];
    private IntPtr _mpq;

    /// <param name="listfile">True: StormLib writes a full (listfile). False: names are whatever the test adds as "(listfile)".</param>
    /// <param name="headerShift">Sector shift the header claims. Only for listfile: false.</param>
    public MpqFixture(string path, bool listfile = true, bool attributes = true, int headerShift = 3)
    {
        _path = path;
        _headerShift = headerShift;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!listfile) { _raw = []; return; }
        var flags = CreateListfile | (attributes ? CreateAttributes : 0);
        if (!SFileCreateArchive(path, flags, 1024, out _mpq)) throw new Win32Exception(Marshal.GetLastWin32Error(), "create " + path);
    }

    public MpqFixture Add(string name, byte[] data, uint flags = Compress, ushort locale = 0)
    {
        if (_raw is not null) { _raw.Add((name, data, flags, locale)); return this; }
        var bytes = Encoding.Latin1.GetBytes(name + "\0");
        if (!SFileCreateFile(_mpq, bytes, 0, (uint)data.Length, locale, flags | Exists, out var file))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "add " + name);
        if (data.Length > 0 && !SFileWriteFile(file, data, (uint)data.Length, (flags & Compress) != 0 ? CompressionZlib : 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "write " + name);
        if (!SFileFinishFile(file)) throw new Win32Exception(Marshal.GetLastWin32Error(), "finish " + name);
        return this;
    }

    public MpqFixture Add(string name, string text, uint flags = Compress, ushort locale = 0) =>
        Add(name, Encoding.Latin1.GetBytes(text), flags, locale);

    /// <summary>
    /// A compressed multi-sector file with a sector CRC table, laid out the way real archives do it, using
    /// <paramref name="storedShift"/> whatever the header says. Listfile: false only. <paramref name="encodeFrom"/>
    /// stores these bytes instead of <paramref name="data"/>; <paramref name="mask"/> overrides the compression byte.
    /// </summary>
    public MpqFixture AddSectored(string name, byte[] data, int storedShift, uint? attributesCrc, byte[]? encodeFrom = null, byte? mask = null)
    {
        var sector = 512 << storedShift;
        var count = (data.Length + sector - 1) / sector;
        var source = encodeFrom ?? data;
        var parts = new List<byte[]>();
        for (var i = 0; i < count; i++)
        {
            var start = Math.Min(i * sector, source.Length);
            var plain = source[start..Math.Min(start + sector, source.Length)];
            using var packed = new MemoryStream();
            packed.WriteByte(mask ?? 0x02);
            using (var z = new System.IO.Compression.ZLibStream(packed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true)) z.Write(plain);
            parts.Add(packed.ToArray());
        }
        var offsets = new uint[count + 2];
        var at = (uint)offsets.Length * 4;
        for (var i = 0; i < count; i++) { offsets[i] = at; at += (uint)parts[i].Length; }
        offsets[count] = at;
        offsets[count + 1] = at + (uint)count * 4;   // the sector CRC table: zeros mean "no check"
        using var stored = new MemoryStream();
        stored.Write(ToBytes(offsets));
        foreach (var part in parts) stored.Write(part);
        stored.Write(new byte[count * 4]);
        _sectored.Add((name, stored.ToArray(), (uint)data.Length, attributesCrc));
        return this;
    }

    public void Dispose()
    {
        if (_raw is not null) WriteRaw();
        if (_mpq != IntPtr.Zero && !SFileCloseArchive(_mpq)) throw new Win32Exception(Marshal.GetLastWin32Error(), "close");
        _mpq = IntPtr.Zero;
    }

    private void WriteRaw()
    {
        const int HeaderSize = 32;
        var slots = 64;
        byte[]? attributes = null;
        if (_sectored.Any(f => f.AttributesCrc is not null))
        {
            // (attributes): version 100, CRC32 flag, one CRC per block in block order, itself last.
            var crcs = new uint[_raw!.Count + _sectored.Count + 1];
            for (var i = 0; i < _sectored.Count; i++) crcs[_raw.Count + i] = _sectored[i].AttributesCrc ?? 0;
            attributes = ToBytes([100, 1, .. crcs]);
        }
        var blocks = _raw!.Count + _sectored.Count + (attributes is null ? 0 : 1);
        while (slots <= blocks) slots *= 2;
        var hash = new uint[slots * 4];
        Array.Fill(hash, 0xFFFFFFFF);
        var block = new uint[blocks * 4];
        using var body = new MemoryStream();
        void Place(int index, string name, ushort locale)
        {
            var slot = (int)(Hash(name, 0) & (uint)(slots - 1));
            while (hash[slot * 4 + 3] != 0xFFFFFFFF) slot = (slot + 1) % slots;
            hash[slot * 4] = Hash(name, 1);
            hash[slot * 4 + 1] = Hash(name, 2);
            hash[slot * 4 + 2] = locale;
            hash[slot * 4 + 3] = (uint)index;
        }
        for (var i = 0; i < _raw.Count; i++)
        {
            var (name, data, flags, locale) = _raw[i];
            var stored = (byte[])data.Clone();
            var storedFlags = (flags & Encrypted) | SingleUnit | Exists;
            if ((flags & Encrypted) != 0)
                Encrypt(stored, Hash(name[(name.LastIndexOf('\\') + 1)..], 3));
            block[i * 4] = (uint)(HeaderSize + body.Position);
            block[i * 4 + 1] = (uint)stored.Length;
            block[i * 4 + 2] = (uint)data.Length;
            block[i * 4 + 3] = storedFlags;
            body.Write(stored);

            Place(i, name, locale);
        }
        for (var i = 0; i < _sectored.Count; i++)
        {
            var (name, stored, size, _) = _sectored[i];
            var index = _raw.Count + i;
            block[index * 4] = (uint)(HeaderSize + body.Position);
            block[index * 4 + 1] = (uint)stored.Length;
            block[index * 4 + 2] = size;
            block[index * 4 + 3] = Exists | Compress | SectorCrc;
            body.Write(stored);
            Place(index, name, 0);
        }
        if (attributes is not null)
        {
            var index = blocks - 1;
            block[index * 4] = (uint)(HeaderSize + body.Position);
            block[index * 4 + 1] = (uint)attributes.Length;
            block[index * 4 + 2] = (uint)attributes.Length;
            block[index * 4 + 3] = SingleUnit | Exists;
            body.Write(attributes);
            Place(index, "(attributes)", 0);
        }

        var hashBytes = ToBytes(hash);
        var blockBytes = ToBytes(block);
        Encrypt(hashBytes, Hash("(hash table)", 3));
        Encrypt(blockBytes, Hash("(block table)", 3));
        var hashPos = (uint)(HeaderSize + body.Length);
        var blockPos = hashPos + (uint)hashBytes.Length;

        using var file = new FileStream(_path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(file);
        w.Write(Encoding.ASCII.GetBytes("MPQ\u001a"));
        w.Write((uint)HeaderSize);
        w.Write(blockPos + (uint)blockBytes.Length);   // archive size
        w.Write((ushort)0);                            // format version
        w.Write((ushort)_headerShift);                 // sector size shift
        w.Write(hashPos);
        w.Write(blockPos);
        w.Write((uint)slots);
        w.Write((uint)blocks);
        w.Write(body.ToArray());
        w.Write(hashBytes);
        w.Write(blockBytes);
    }

    private static byte[] ToBytes(uint[] values)
    {
        var bytes = new byte[values.Length * 4];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[0x500];
        uint seed = 0x00100001;
        for (var i = 0; i < 0x100; i++)
            for (int j = 0, at = i; j < 5; j++, at += 0x100)
            {
                seed = (seed * 125 + 3) % 0x2AAAAB;
                var high = (seed & 0xFFFF) << 16;
                seed = (seed * 125 + 3) % 0x2AAAAB;
                table[at] = high | (seed & 0xFFFF);
            }
        return table;
    }

    private static uint Hash(string name, int type)
    {
        uint a = 0x7FED7FED, b = 0xEEEEEEEE;
        foreach (var raw in Encoding.Latin1.GetBytes(name))
        {
            uint c = raw == '/' ? '\\' : raw is >= (byte)'a' and <= (byte)'z' ? raw - 0x20u : raw;
            a = Table[type * 0x100 + c] ^ (a + b);
            b = c + a + b + (b << 5) + 3;
        }
        return a;
    }

    private static void Encrypt(byte[] data, uint key)
    {
        var seed = 0xEEEEEEEEu;
        for (var i = 0; i + 4 <= data.Length; i += 4)
        {
            seed += Table[0x400 + (key & 0xFF)];
            var plain = BitConverter.ToUInt32(data, i);
            BitConverter.TryWriteBytes(data.AsSpan(i), plain ^ (key + seed));
            key = ((~key << 21) + 0x11111111) | (key >> 11);
            seed = plain + seed + (seed << 5) + 3;
        }
    }

    [DllImport("StormLib", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SFileCreateArchive(string name, uint flags, uint maxFiles, out IntPtr mpq);

    [DllImport("StormLib", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SFileCreateFile(IntPtr mpq, byte[] name, ulong time, uint size, uint locale, uint flags, out IntPtr file);

    [DllImport("StormLib", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SFileWriteFile(IntPtr file, byte[] data, uint size, uint compression);

    [DllImport("StormLib", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SFileFinishFile(IntPtr file);

    [DllImport("StormLib", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SFileCloseArchive(IntPtr mpq);
}
