using System.Buffers.Binary;
using System.Text;

namespace CoaMcp.Pipeline.Wdb;

/// <summary>A record that does not fit the 3.3.5 layout. The importer adds the file name and offset.</summary>
internal sealed class WdbLayoutException(string message) : Exception(message);

/// <summary>One record: <paramref name="Offset"/> is where its entry field starts in the file.</summary>
public sealed record WdbRecord(uint Entry, int Offset, ReadOnlyMemory<byte> Data);

/// <summary>
/// A WDB cache file in the standard 3.3.5 layout: a 24-byte header (reversed magic, build, reversed locale, record size,
/// record version, cache version), then records of u32 entry, u32 size and that many bytes, ended by entry 0.
/// Only build 12340 is accepted; anything else fails by file name instead of being guessed at.
/// </summary>
public sealed class WdbFile
{
    public const int Build335 = 12340;
    public const int HeaderSize = 24;
    private const int MaxRecordBytes = 1 << 20;

    public string Name { get; }
    public string Magic { get; }
    public uint Build { get; }
    public string Locale { get; }
    public IReadOnlyList<WdbRecord> Records { get; }

    private WdbFile(string name, string magic, uint build, string locale, IReadOnlyList<WdbRecord> records)
    {
        Name = name; Magic = magic; Build = build; Locale = locale; Records = records;
    }

    public static WdbFile Read(string path, string expectedMagic) => Parse(Path.GetFileName(path), File.ReadAllBytes(path), expectedMagic);

    public static WdbFile Parse(string name, byte[] bytes, string expectedMagic)
    {
        if (bytes.Length < HeaderSize) throw new InvalidDataException($"{name}: only {bytes.Length} bytes, too short for a WDB header ({HeaderSize} bytes).");
        var magic = Reversed(bytes.AsSpan(0, 4));
        if (magic != expectedMagic) throw new InvalidDataException($"{name}: header says '{magic}', expected '{expectedMagic}' for this file name.");
        var build = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
        if (build != Build335) throw new InvalidDataException($"{name}: unknown version, build {build}; only the 3.3.5 layout (build {Build335}) is read, and its fields are not guessed.");
        var locale = Reversed(bytes.AsSpan(8, 4));

        var records = new List<WdbRecord>();
        var pos = HeaderSize;
        while (true)
        {
            if (pos + 8 > bytes.Length) throw new InvalidDataException($"{name}: ends at offset {pos} without the end marker (entry 0).");
            var entry = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos + 4));
            if (entry == 0)
            {
                if (size != 0 || pos + 8 != bytes.Length)
                    throw new InvalidDataException($"{name}: end marker at offset {pos} is followed by {bytes.Length - pos - 8} unexplained bytes.");
                break;
            }
            if (size > MaxRecordBytes || pos + 8L + size > bytes.Length)
                throw new InvalidDataException($"{name}: record {entry} at offset {pos} claims {size} bytes, more than the file has left or the {MaxRecordBytes} allowed.");
            records.Add(new WdbRecord(entry, pos, bytes.AsMemory(pos + 8, (int)size)));
            pos += 8 + (int)size;
        }
        return new WdbFile(name, magic, build, locale, records);
    }

    private static string Reversed(ReadOnlySpan<byte> four)
    {
        var chars = new char[four.Length];
        for (var i = 0; i < four.Length; i++) chars[four.Length - 1 - i] = (char)four[i];
        return new string(chars);
    }
}

/// <summary>Reads fields from one record. Every read is bounds-checked; running out is a layout failure, not a crash.</summary>
internal ref struct WdbCursor
{
    private readonly ReadOnlySpan<byte> _data;
    public int Position { get; private set; }

    public WdbCursor(ReadOnlySpan<byte> data) { _data = data; Position = 0; }

    public readonly int Remaining => _data.Length - Position;

    public uint U32()
    {
        if (Remaining < 4) throw new WdbLayoutException($"ran out of bytes at +{Position} reading a 4-byte value");
        var v = BinaryPrimitives.ReadUInt32LittleEndian(_data[Position..]);
        Position += 4;
        return v;
    }

    public int I32() => unchecked((int)U32());

    public float Float() => BitConverter.UInt32BitsToSingle(U32());

    public byte U8()
    {
        if (Remaining < 1) throw new WdbLayoutException($"ran out of bytes at +{Position} reading a byte");
        return _data[Position++];
    }

    public void Skip(int u32s) { for (var i = 0; i < u32s; i++) U32(); }

    public string CString()
    {
        var rest = _data[Position..];
        var end = rest.IndexOf((byte)0);
        if (end < 0) throw new WdbLayoutException($"text at +{Position} has no terminator inside the record");
        var s = Encoding.UTF8.GetString(rest[..end]);
        Position += end + 1;
        return s;
    }

    /// <summary>The 3.3.5 layout must explain every byte of a record.</summary>
    public readonly void ExpectEnd()
    {
        if (Remaining != 0) throw new WdbLayoutException($"{Remaining} bytes left over after the last known field (record has {_data.Length})");
    }
}
