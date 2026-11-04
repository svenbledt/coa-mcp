namespace CoaMcp.Pipeline.Wdb;

public sealed record CreatureCache(uint Entry, string Name, string SubName, string IconName, uint TypeFlags, uint Type, uint Family, uint Rank, IReadOnlyList<uint> DisplayIds);

/// <summary>The 3.3.5 layout of the creature record. A record that the layout doesn't explain to the last byte fails.</summary>
internal static class WdbRecords
{
    public static CreatureCache Creature(uint entry, ReadOnlyMemory<byte> data)
    {
        var c = new WdbCursor(data.Span);
        var name = c.CString(); c.CString(); c.CString(); c.CString();   // the name in four genders/forms; the first is the name
        var sub = c.CString();
        var icon = c.CString();
        var typeFlags = c.U32();
        var type = c.U32(); var family = c.U32(); var rank = c.U32();
        c.Skip(2);                                                        // kill credits
        var display = new[] { c.U32(), c.U32(), c.U32(), c.U32() };
        c.Skip(2);                                                        // health and mana modifiers
        c.U8();                                                           // racial leader
        c.Skip(6);                                                        // quest items
        c.Skip(1);                                                        // movement id
        c.ExpectEnd();
        return new CreatureCache(entry, name, sub, icon, typeFlags, type, family, rank, display.Where(d => d != 0).ToList());
    }
}
