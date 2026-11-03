namespace CoaMcp.Pipeline.Dbc;

public enum GuessKind { Int, Float, MaybeString }

public sealed record FieldGuessResult(GuessKind Kind, string? Note);

/// <summary>Watches one four-byte column's values and says what it most resembles. Every answer is only a guess, and the output must say so.</summary>
public sealed class ColumnStats(byte[] strings)
{
    private bool _any, _nonZero, _allStrings = strings.Length > 1, _allFloats = true;

    public void Add(uint value)
    {
        _any = true;
        if (value == 0) return;
        _nonZero = true;
        if (_allStrings && !DbcReader.IsStringStart(strings, value)) _allStrings = false;
        if (_allFloats && !FieldGuess.IsPlausibleFloat(value)) _allFloats = false;
    }

    /// <summary>A named string column may hold only offsets that start a string: anything else means the layout is wrong.</summary>
    public bool AllValidStrings => !_any || !_nonZero || _allStrings;

    public FieldGuessResult Result()
    {
        if (!_any) return new(GuessKind.Int, "no rows, so the type is unknown");
        if (!_nonZero) return new(GuessKind.Int, "every row is zero, so the type is unknown");
        if (_allStrings) return new(GuessKind.MaybeString, "maybe a string: every value is a valid string offset, but small integers can look the same");
        if (_allFloats) return new(GuessKind.Float, "looks like a float; bit patterns of flags can look the same");
        return new(GuessKind.Int, null);
    }
}

public static class FieldGuess
{
    private const float MinFloat = 1e-5f, MaxFloat = 1e8f;

    /// <summary>Whether these bits read as a float a game table would hold. Tiny, huge, NaN, infinite or sign-only values are integers wearing a float's clothes.</summary>
    public static bool IsPlausibleFloat(uint bits)
    {
        var f = BitConverter.UInt32BitsToSingle(bits);
        if (!float.IsFinite(f)) return false;
        var size = Math.Abs(f);
        return size is >= MinFloat and <= MaxFloat;
    }

    public static FieldGuessResult Classify(IEnumerable<uint> values, byte[] strings)
    {
        var stats = new ColumnStats(strings);
        foreach (var v in values) stats.Add(v);
        return stats.Result();
    }
}
