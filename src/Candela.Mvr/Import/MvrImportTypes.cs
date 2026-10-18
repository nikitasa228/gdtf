using System.Globalization;
using Candela.Core.Diagnostics;
using Candela.Core.SceneGraph;
using Candela.Gdtf;

namespace Candela.Mvr;

/// <summary>Options of <see cref="MvrImporter"/>.</summary>
public sealed class MvrImportOptions
{
    /// <summary>
    /// Fixture library used to resolve GDTF references that are not embedded or are stubs (null: embedded files only).
    /// </summary>
    public FixtureLibrary? Library { get; init; }

    /// <summary>Decode every referenced 3D file during import so unreadable geometry is reported (default true).</summary>
    public bool ValidateGeometry { get; init; } = true;

    /// <summary>Create a cable entity for every MVR 1.6 <c>Connection</c> (default true).</summary>
    public bool CreateCablesFromConnections { get; init; } = true;

    /// <summary>Name of the new scene (default: the MVR file name without extension).</summary>
    public string? SceneName { get; init; }

    /// <summary>Name of the group entity that holds a merged import (default: the MVR file name without extension).</summary>
    public string? GroupName { get; init; }
}

/// <summary>Where the GDTF of an MVR <c>GDTFSpec</c> came from.</summary>
public enum MvrGdtfSource
{
    /// <summary>A complete GDTF (DMX modes and geometry) embedded in the MVR archive.</summary>
    Embedded,

    /// <summary>A file of the fixture library (see <see cref="MvrGdtfReference.MatchKind"/> for how certain).</summary>
    Library,

    /// <summary>Only an embedded stub was available (no geometry); it is used as is.</summary>
    EmbeddedStub,

    /// <summary>Neither embedded nor in the library.</summary>
    Unresolved,
}

/// <summary>How the DMX mode of a group of objects was resolved against the GDTF that was used.</summary>
/// <param name="Requested">Mode name written in the MVR (<c>GDTFMode</c>), or null.</param>
/// <param name="Resolved">Mode used (stored in <c>FixtureComponent.GdtfMode</c>), or null when the GDTF has no modes.</param>
/// <param name="Footprint">DMX footprint of the resolved mode's first break in slots, or null.</param>
/// <param name="InferredFootprint">Footprint inferred from the address spacing of these objects in the MVR, or null.</param>
/// <param name="Exact">True when the GDTF has the requested mode.</param>
/// <param name="ObjectCount">Number of objects using this mode.</param>
public sealed record MvrModeResolution(string? Requested, string? Resolved, int? Footprint, int? InferredFootprint, bool Exact, int ObjectCount);

/// <summary>Resolution of one distinct <c>GDTFSpec</c> of the imported MVR.</summary>
public sealed class MvrGdtfReference
{
    internal MvrGdtfReference(string specName) => SpecName = specName;

    /// <summary>The reference as written in the MVR (<c>GDTFSpec</c>).</summary>
    public string SpecName { get; }

    /// <summary>GDTF file name stored in <c>FixtureComponent.GdtfSpec</c> (library or embedded file name).</summary>
    public string FileName { get; internal set; } = "";

    /// <summary>Where the GDTF came from.</summary>
    public MvrGdtfSource Source { get; internal set; } = MvrGdtfSource.Unresolved;

    /// <summary>Library match certainty when <see cref="Source"/> is <see cref="MvrGdtfSource.Library"/>.</summary>
    public FixtureMatchKind? MatchKind { get; internal set; }

    /// <summary>Library match score (1 for exact matches, name similarity for substitutes).</summary>
    public double MatchScore { get; internal set; }

    /// <summary>The library file used, or null.</summary>
    public FixtureLibraryEntry? LibraryEntry { get; internal set; }

    /// <summary>Full path of the library file used, or null.</summary>
    public string? LibraryFilePath => LibraryEntry?.FilePath;

    /// <summary>Bytes of the GDTF embedded in the MVR (complete or stub), or null when none was embedded.</summary>
    public byte[]? EmbeddedBytes { get; internal set; }

    /// <summary>Archive path of the embedded GDTF, or null.</summary>
    public string? EmbeddedFileName { get; internal set; }

    /// <summary>True when the embedded GDTF has no geometry or no DMX modes (exporters such as Depence write such stubs).</summary>
    public bool EmbeddedIsStub { get; internal set; }

    /// <summary>The fixture type used, or null when unresolved.</summary>
    public FixtureType? FixtureType { get; internal set; }

    /// <summary>Number of MVR objects that reference this spec.</summary>
    public int ObjectCount { get; internal set; }

    /// <summary>Mode resolution per requested mode name, in order of first use.</summary>
    public IReadOnlyList<MvrModeResolution> Modes { get; internal set; } = Array.Empty<MvrModeResolution>();

    /// <summary>True when a GDTF with geometry and DMX modes is available (embedded or from the library).</summary>
    public bool IsResolved => Source is MvrGdtfSource.Embedded or MvrGdtfSource.Library;

    /// <summary>True when the library file is the requested model (not a substitute).</summary>
    public bool IsExactModel => Source == MvrGdtfSource.Embedded || (Source == MvrGdtfSource.Library && MatchKind is not FixtureMatchKind.Substitute);

    /// <summary>The GDTF file content to store with the project: the embedded bytes or the library file (null when unresolved).</summary>
    public byte[]? ReadBytes()
    {
        if (Source is MvrGdtfSource.Embedded or MvrGdtfSource.EmbeddedStub) return EmbeddedBytes;
        if (Source == MvrGdtfSource.Library && LibraryFilePath is { } path) return File.ReadAllBytes(path);
        return null;
    }

    /// <summary>One-line description for reports, e.g. "Ayrton@MagicBlade R.gdtf → … (library substitute 0.61, stub embedded)".</summary>
    public string Describe()
    {
        string how = Source switch
        {
            MvrGdtfSource.Embedded => "embedded",
            MvrGdtfSource.Library => MatchKind == FixtureMatchKind.Substitute
                ? string.Create(CultureInfo.InvariantCulture, $"library substitute, similarity {MatchScore:0.00}")
                : $"library {MatchKind}",
            MvrGdtfSource.EmbeddedStub => "embedded stub only",
            _ => "unresolved",
        };
        if (EmbeddedIsStub && Source == MvrGdtfSource.Library) how += ", embedded file is a stub";
        return $"{SpecName} → {FileName} ({how}; {ObjectCount} objects)";
    }

    /// <inheritdoc />
    public override string ToString() => Describe();
}

/// <summary>Result of an MVR import.</summary>
public sealed class MvrImportResult
{
    internal MvrImportResult(
        Scene scene, Entity? group, IReadOnlyList<Entity> roots, IReadOnlyList<ImportedAsset> assets,
        IReadOnlyList<MvrGdtfReference> gdtfs, IReadOnlyList<Issue> issues, MvrDocument document,
        IReadOnlyDictionary<Guid, Entity> entities, IReadOnlyList<Layer> layers)
    {
        Scene = scene;
        Group = group;
        RootEntities = roots;
        Assets = assets;
        Gdtfs = gdtfs;
        Issues = issues;
        Document = document;
        EntitiesByUuid = entities;
        Layers = layers;
    }

    /// <summary>The new scene, or the scene the import was merged into.</summary>
    public Scene Scene { get; }

    /// <summary>The group entity holding a merged import (null for a new scene).</summary>
    public Entity? Group { get; }

    /// <summary>Entities added at the scene root: the layers' top-level objects and cables (new scene), or the group (merge).</summary>
    public IReadOnlyList<Entity> RootEntities { get; }

    /// <summary>3D files the scene now references (to be stored in the project), in order of first use.</summary>
    public IReadOnlyList<ImportedAsset> Assets { get; }

    /// <summary>Resolution of every distinct <c>GDTFSpec</c>, in order of first use.</summary>
    public IReadOnlyList<MvrGdtfReference> Gdtfs { get; }

    /// <summary>Everything worth telling the user: reader problems first, then import problems.</summary>
    public IReadOnlyList<Issue> Issues { get; }

    /// <summary>The MVR object model that was imported.</summary>
    public MvrDocument Document { get; }

    /// <summary>Entity created for each MVR object, keyed by the object's uuid in the file.</summary>
    public IReadOnlyDictionary<Guid, Entity> EntitiesByUuid { get; }

    /// <summary>Scene layers the imported objects were put on (created or reused).</summary>
    public IReadOnlyList<Layer> Layers { get; }

    /// <summary>The entity created for an MVR object uuid (ids may differ after a merge), or null.</summary>
    public Entity? FindEntity(Guid mvrUuid) => EntitiesByUuid.GetValueOrDefault(mvrUuid);

    /// <summary>All entities added by the import (depth first).</summary>
    public IEnumerable<Entity> AllEntities() => RootEntities.SelectMany(r => r.SelfAndDescendants());
}
