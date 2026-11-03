using System.Text;

namespace CoaMcp.Pipeline.Mpq;

/// <summary>The MPQ name hash: case-insensitive, with / and \ the same. Names are bytes, kept as Latin-1 strings.</summary>
public static class MpqHash
{
    private static readonly uint[] Table = BuildTable();

    public static uint Name1(string name) => Hash(name, 1);
    public static uint Name2(string name) => Hash(name, 2);
    public static uint FileKey(string name) => Hash(name, 3);

    private static uint Hash(string name, int type)
    {
        uint seed1 = 0x7FED7FED, seed2 = 0xEEEEEEEE;
        var offset = type * 0x100;
        Span<byte> stack = stackalloc byte[512];
        var bytes = name.Length <= 512 ? stack[..Encoding.Latin1.GetBytes(name, stack)] : Encoding.Latin1.GetBytes(name);
        foreach (var raw in bytes)
        {
            uint c = raw;
            if (c == '/') c = '\\';
            else if (c is >= 'a' and <= 'z') c -= 0x20;
            seed1 = Table[offset + c] ^ (seed1 + seed2);
            seed2 = c + seed1 + seed2 + (seed2 << 5) + 3;
        }
        return seed1;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[0x500];
        uint seed = 0x00100001;
        for (var i = 0; i < 0x100; i++)
        {
            for (int j = 0, at = i; j < 5; j++, at += 0x100)
            {
                seed = (seed * 125 + 3) % 0x2AAAAB;
                var high = (seed & 0xFFFF) << 16;
                seed = (seed * 125 + 3) % 0x2AAAAB;
                table[at] = high | (seed & 0xFFFF);
            }
        }
        return table;
    }
}
