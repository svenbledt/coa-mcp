using CoaMcp.Core;

namespace CoaMcp.Pipeline.Dbc;

/// <summary>How a run of bytes in a record is read.</summary>
public enum DbcCell { Int32, UInt32, Float, String, Byte, Guess32, GuessByte }

/// <summary>A slot in a record: its name, where it is, and how it is read. <paramref name="Proven"/> means the name comes from a config, not from position.</summary>
public sealed record DbcSlot(string Name, int Offset, DbcCell Cell, bool Proven);

/// <summary>Slots for a whole record, how many have proven names, and anything worth reporting about the choice.</summary>
public sealed record DbcLayout(IReadOnlyList<DbcSlot> Slots, int Named, string? NamesSource, string? Problem);

/// <summary>
/// Chooses a layout for a file. A config only applies if its field count and byte total match the file exactly: a misaligned name is the worst thing this layer could show.
/// Without one, positions are named <c>f0..fn</c> when records are clearly four bytes per field, or all single bytes.
/// Any other record size means field boundaries are unknown, so the record is shown as <c>w&lt;n&gt;</c> four-byte words and trailing <c>b&lt;offset&gt;</c> bytes, and reported.
/// </summary>
public static class DbcLayouts
{
    public static DbcLayout For(string table, DbcReader file, DbcTableConfig? config)
    {
        if (config is null) return Positions(file, null);

        var bytes = config.Fields.Sum(f => f.Width);
        if (config.Fields.Count != file.FieldCount || bytes != file.RecordSize)
            return Positions(file, $"The field config for {table} ({config.Fields.Count} fields, {bytes} bytes) does not match the file ({file.FieldCount} fields, {file.RecordSize} bytes), so its names are not used.");

        var slots = new List<DbcSlot>(config.Fields.Count);
        var offset = 0;
        foreach (var f in config.Fields)
        {
            slots.Add(new DbcSlot(f.Name, offset, f.Type switch
            {
                "int" => DbcCell.Int32,
                "uint" => DbcCell.UInt32,
                "float" => DbcCell.Float,
                "string" => DbcCell.String,
                "byte" => DbcCell.Byte,
                DbcConfigField.UnknownByte => DbcCell.GuessByte,
                _ => DbcCell.Guess32,
            }, f.Proven));
            offset += f.Width;
        }
        return new DbcLayout(slots, slots.Count(s => s.Proven), config.Source, null);
    }

    /// <summary>The layout with no proven names. <paramref name="why"/> says why a config was not used, when one was refused.</summary>
    public static DbcLayout Positions(DbcReader file, string? why)
    {
        var fields = file.FieldCount;
        var size = file.RecordSize;
        if (size == fields * 4L)
            return new DbcLayout(Enumerable.Range(0, fields).Select(i => new DbcSlot("f" + i, i * 4, DbcCell.Guess32, false)).ToList(), 0, null, why);
        if (size == fields)
            return new DbcLayout(Enumerable.Range(0, fields).Select(i => new DbcSlot("f" + i, i, DbcCell.GuessByte, false)).ToList(), 0, null, why);

        var slots = new List<DbcSlot>();
        for (var i = 0; i < size / 4; i++) slots.Add(new DbcSlot("w" + i, i * 4, DbcCell.Guess32, false));
        for (var o = size / 4 * 4; o < size; o++) slots.Add(new DbcSlot("b" + o, o, DbcCell.GuessByte, false));
        var problem = $"The header says {fields} fields, but records are {size} bytes, not {fields * 4L}: some fields are not four bytes wide and where each starts is unknown. " +
                      "Shown as four-byte words (w0, w1, ...) and trailing bytes (b<offset>), which may split or span real fields.";
        return new DbcLayout(slots, 0, null, why is null ? problem : why + " " + problem);
    }

    public static string Type(DbcCell cell) => cell switch
    {
        DbcCell.UInt32 => DbcTypes.UInt,
        DbcCell.Float => DbcTypes.Float,
        DbcCell.String => DbcTypes.String,
        DbcCell.Byte or DbcCell.GuessByte => DbcTypes.Byte,
        _ => DbcTypes.Int,
    };
}
