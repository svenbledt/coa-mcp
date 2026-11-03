using CoaMcp.Core;

namespace CoaMcp.Pipeline.Spells;

public sealed record SpellsImportResult(int Spells, int Enums, int FlagKinds, int ScalingInputs, int GoldenRows);

/// <summary>Builds the spells layer from a client DBC folder by running Spellwork's spell logic at build time.</summary>
public static class SpellsImport
{
    public static SpellsImportResult Run(string dbcDir, string outPath, Action<int, int>? progress = null, string? version = null) =>
        Run(SpellSource.Open(dbcDir), outPath, progress, version);

    public static SpellsImportResult Run(SpellSource source, string outPath, Action<int, int>? progress = null, string? version = null)
    {
        version = LayerVersion.Resolve(version);
        using var w = new LayerWriter(outPath, "spells", version);
        var spells = SpellExporter.Export(source, w, progress);
        var (members, kinds) = EnumExporter.Export(source, w);
        var (inputs, golden) = ScalingExporter.Export(source, source.DbcDir, w);
        w.Finish();
        return new SpellsImportResult(spells, members, kinds, inputs, golden);
    }
}
