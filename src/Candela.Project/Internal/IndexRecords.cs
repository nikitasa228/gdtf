using Candela.Assets;

namespace Candela.Project.Internal;

/// <summary><c>assets/index.json</c>: one record per asset, in the asset store's order.</summary>
internal sealed class AssetIndexFile
{
    public List<AssetRecord> Assets { get; set; } = new();
}

/// <summary>One asset: identity, kind, its stored files and the import options needed to decode it identically.</summary>
internal sealed class AssetRecord
{
    public Guid Id { get; set; }

    public AssetKind Kind { get; set; }

    /// <summary>Original file name (AssetEntry.OriginalFileName).</summary>
    public string FileName { get; set; } = "";

    /// <summary>Archive entry of the main file.</summary>
    public string Entry { get; set; } = "";

    /// <summary>Size of the main file in bytes.</summary>
    public long Size { get; set; }

    /// <summary>Lower-case hex SHA-256 of the main file.</summary>
    public string? Sha256 { get; set; }

    /// <summary>AssetEntry.ContentHash (main file and companions), informational (thumbnail cache key).</summary>
    public string? ContentHash { get; set; }

    public string? SourcePath { get; set; }

    public ImportOptionsRecord? ImportOptions { get; set; }

    public List<CompanionRecord>? Companions { get; set; }
}

/// <summary>A companion file of a model asset.</summary>
internal sealed class CompanionRecord
{
    /// <summary>Relative path the model refers to (key of AssetEntry.CompanionFiles).</summary>
    public string Path { get; set; } = "";

    public string Entry { get; set; } = "";

    public long Size { get; set; }

    public string? Sha256 { get; set; }
}

/// <summary>The persistent part of <see cref="ImportOptions"/> (the texture key prefix is derived from the asset id).</summary>
internal sealed class ImportOptionsRecord
{
    public SourceUpAxis UpAxis { get; set; } = SourceUpAxis.Auto;

    public float? UnitScale { get; set; }

    public bool GuessUnits { get; set; } = true;

    public float SmoothingAngleDegrees { get; set; } = ImportOptions.Default.SmoothingAngleDegrees;

    public int MaxParts { get; set; } = ImportOptions.Default.MaxParts;

    public static ImportOptionsRecord From(ImportOptions o) => new()
    {
        UpAxis = o.UpAxis,
        UnitScale = o.UnitScale,
        GuessUnits = o.GuessUnits,
        SmoothingAngleDegrees = o.SmoothingAngleDegrees,
        MaxParts = o.MaxParts,
    };

    public ImportOptions ToOptions()
    {
        var d = ImportOptions.Default;
        return d with
        {
            UpAxis = Enum.IsDefined(UpAxis) ? UpAxis : SourceUpAxis.Auto,
            UnitScale = UnitScale is { } s && float.IsFinite(s) && s > 0f ? s : null,
            GuessUnits = GuessUnits,
            SmoothingAngleDegrees = float.IsFinite(SmoothingAngleDegrees) ? System.Math.Clamp(SmoothingAngleDegrees, 0f, 180f) : d.SmoothingAngleDegrees,
            MaxParts = MaxParts > 0 ? MaxParts : d.MaxParts,
        };
    }
}

/// <summary><c>fixtures/index.json</c>: maps GDTF names used by the scene to archive entries.</summary>
internal sealed class FixtureIndexFile
{
    public List<FixtureRecord> Fixtures { get; set; } = new();
}

/// <summary>One stored GDTF file.</summary>
internal sealed class FixtureRecord
{
    /// <summary>Name as FixtureComponent.GdtfSpec uses it (may differ from the entry's file name after sanitising).</summary>
    public string Name { get; set; } = "";

    public string Entry { get; set; } = "";

    public long Size { get; set; }

    public string? Sha256 { get; set; }
}
