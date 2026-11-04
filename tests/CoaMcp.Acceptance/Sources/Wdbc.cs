using System.Text;

namespace CoaMcp.Acceptance;

/// <summary>A WDBC file read directly. Written for this suite and sharing nothing with the pipeline's reader, so the two can disagree. Read-only.</summary>
public sealed class Wdbc
{
    private const int Header = 20;
    private readonly byte[] _data;
    private readonly int _strings;

    public int Records { get; }
    public int Fields { get; }
    public int RecordSize { get; }
    public int StringSize { get; }

    public Wdbc(string path)
    {
        _data = File.ReadAllBytes(path);
        if (_data.Length < Header || Encoding.ASCII.GetString(_data, 0, 4) != "WDBC") throw new InvalidDataException("not a WDBC file: " + path);
        Records = BitConverter.ToInt32(_data, 4);
        Fields = BitConverter.ToInt32(_data, 8);
        RecordSize = BitConverter.ToInt32(_data, 12);
        StringSize = BitConverter.ToInt32(_data, 16);
        _strings = Header + Records * RecordSize;
        if (_strings + StringSize != _data.Length) throw new InvalidDataException("WDBC header doesn't add up: " + path);
    }

    /// <summary>Only the header, for all 368 files without holding them in memory.</summary>
    public static (int Records, int Fields, int RecordSize) ReadHeader(string path)
    {
        using var f = File.OpenRead(path);
        var head = new byte[Header];
        f.ReadExactly(head);
        return (BitConverter.ToInt32(head, 4), BitConverter.ToInt32(head, 8), BitConverter.ToInt32(head, 12));
    }

    private int At(int record, int offset) => Header + record * RecordSize + offset;
    public int Int(int record, int offset) => BitConverter.ToInt32(_data, At(record, offset));
    public uint UInt(int record, int offset) => BitConverter.ToUInt32(_data, At(record, offset));
    public float Float(int record, int offset) => BitConverter.ToSingle(_data, At(record, offset));
    public byte Byte(int record, int offset) => _data[At(record, offset)];

    /// <summary>The text at this offset of the string block, or null when the offset falls outside it.</summary>
    public string? Text(uint offset)
    {
        if (offset == 0 && StringSize == 0) return "";
        if (offset >= StringSize) return null;
        var start = _strings + (int)offset;
        var end = start;
        while (end < _data.Length && _data[end] != 0) end++;
        return Encoding.UTF8.GetString(_data, start, end - start);
    }
}
