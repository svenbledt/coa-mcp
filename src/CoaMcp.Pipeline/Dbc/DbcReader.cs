using System.Text;

namespace CoaMcp.Pipeline.Dbc;

/// <summary>A file that is not a WDBC table, or whose header disagrees with its length.</summary>
public sealed class DbcFormatException(string message) : IOException(message);

/// <summary>
/// Reads a WDBC file. Records are streamed, so a table with over a million rows never sits in memory whole; only the string block does.
/// Never trusts the header: its sizes must add up to the file's length before anything else is read.
/// </summary>
public sealed class DbcReader : IDisposable
{
    private const int HeaderSize = 20;

    private readonly FileStream _file;
    private readonly byte[] _strings;

    public int RecordCount { get; }
    public int FieldCount { get; }
    public int RecordSize { get; }
    public int StringBlockSize => _strings.Length;
    public byte[] StringBlock => _strings;

    private DbcReader(FileStream file, int records, int fields, int recordSize, byte[] strings)
    {
        _file = file;
        RecordCount = records;
        FieldCount = fields;
        RecordSize = recordSize;
        _strings = strings;
    }

    public static DbcReader Open(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        try
        {
            if (file.Length < HeaderSize) throw new DbcFormatException($"not a WDBC file: only {file.Length} bytes, shorter than a header.");
            var head = new byte[HeaderSize];
            file.ReadExactly(head);
            if (!head.AsSpan(0, 4).SequenceEqual("WDBC"u8)) throw new DbcFormatException("not a WDBC file: the first four bytes are not WDBC.");

            var records = BitConverter.ToUInt32(head, 4);
            var fields = BitConverter.ToUInt32(head, 8);
            var recordSize = BitConverter.ToUInt32(head, 12);
            var stringSize = BitConverter.ToUInt32(head, 16);
            var expected = (decimal)HeaderSize + (decimal)records * recordSize + stringSize;   // decimal: hostile values cannot overflow it
            if (expected != file.Length)
                throw new DbcFormatException($"the header ({records} records of {recordSize} bytes, {stringSize} bytes of strings) does not match the file length of {file.Length} bytes.");
            if (records > int.MaxValue || fields > int.MaxValue || recordSize > int.MaxValue || stringSize > int.MaxValue)
                throw new DbcFormatException("the header's sizes are too large for a table.");
            if (records > 0 && recordSize == 0) throw new DbcFormatException("the header says records have no bytes.");

            file.Seek(HeaderSize + (long)records * recordSize, SeekOrigin.Begin);
            var strings = new byte[stringSize];
            file.ReadExactly(strings);
            return new DbcReader(file, (int)records, (int)fields, (int)recordSize, strings);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>Each record in file order. The same buffer is reused for every record, so copy what must outlive the step. Can be read more than once.</summary>
    public IEnumerable<byte[]> Records()
    {
        _file.Seek(HeaderSize, SeekOrigin.Begin);
        var buffer = new byte[RecordSize];
        for (var i = 0; i < RecordCount; i++)
        {
            _file.ReadExactly(buffer);
            yield return buffer;
        }
    }

    /// <summary>The string at this offset, or null when the offset is outside the string block. Offset 0 is the empty string.</summary>
    public string? GetString(uint offset)
    {
        if (offset == 0 && _strings.Length == 0) return "";
        if (offset >= _strings.Length) return null;
        var rest = _strings.AsSpan((int)offset);
        var end = rest.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? rest : rest[..end]);
    }

    /// <summary>True when a string begins at this offset: it is inside the block and follows a terminator.</summary>
    public bool IsStringStart(uint offset) => IsStringStart(_strings, offset);

    public static bool IsStringStart(byte[] strings, uint offset) =>
        offset < strings.Length && (offset == 0 || strings[offset - 1] == 0);

    public void Dispose() => _file.Dispose();
}
