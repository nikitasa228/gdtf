namespace Candela.Gdtf;

/// <summary>Where the <see cref="FixtureLibraryEntry.Category"/> of an entry came from.</summary>
public enum CategorySource
{
    /// <summary>From the researched record in <c>_specs/fixtures_index.json</c>.</summary>
    Specs,

    /// <summary>Guessed from words of the model name (no specs record).</summary>
    NameHeuristic,
}

/// <summary>
/// One <c>.gdtf</c> file of a library folder, described from its file name (<c>Manufacturer@Model@Revision.gdtf</c>)
/// and the specs index alone, so thousands of files can be listed without opening them. Parse it with
/// <see cref="FixtureLibrary.GetFixtureType"/>.
/// </summary>
public sealed class FixtureLibraryEntry
{
    internal FixtureLibraryEntry(string filePath, long fileSize, DateTime lastWriteTimeUtc, FixtureSpecs? specs)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        FileSize = fileSize;
        LastWriteTimeUtc = lastWriteTimeUtc;
        Specs = specs;

        string stem = Path.GetFileNameWithoutExtension(FileName);
        var parts = stem.Split('@');
        if (parts.Length >= 2)
        {
            FileManufacturer = parts[0].Trim();
            FileModel = parts[1].Trim();
            RevisionText = string.Join("@", parts, 2, parts.Length - 2).Trim();
            FollowsNamingPattern = parts.Length >= 3 && FileManufacturer.Length > 0 && FileModel.Length > 0;
        }
        else
        {
            FileManufacturer = "";
            FileModel = stem.Trim();
            RevisionText = "";
        }

        Manufacturer = !string.IsNullOrWhiteSpace(specs?.Manufacturer) ? specs!.Manufacturer.Trim() : Readable(FileManufacturer);
        ModelName = !string.IsNullOrWhiteSpace(specs?.LongName) ? specs!.LongName.Trim() : Readable(FileModel);
        if (specs is not null)
        {
            Category = specs.Category;
            CategorySource = CategorySource.Specs;
        }
        else
        {
            Category = FixtureCategories.Guess(FileModel);
            CategorySource = CategorySource.NameHeuristic;
        }
        Revision = FixtureRevision.Parse(RevisionText, lastWriteTimeUtc);

        ManufacturerKey = FixtureNames.ManufacturerKey(FileManufacturer.Length > 0 ? FileManufacturer : Manufacturer);
        var keys = new List<string>(4);
        void AddKey(string? name)
        {
            var k = FixtureNames.Key(name);
            if (k.Length == 0) return;
            if (!keys.Contains(k)) keys.Add(k);
            // Model parts that repeat the brand ("ACME_(FACE_600)") are also known without it.
            if (ManufacturerKey.Length > 0 && k.Length > ManufacturerKey.Length && k.StartsWith(ManufacturerKey, StringComparison.Ordinal))
            {
                var stripped = k[ManufacturerKey.Length..];
                if (!keys.Contains(stripped)) keys.Add(stripped);
            }
        }
        AddKey(FileModel);
        AddKey(specs?.LongName);
        AddKey(specs?.GdtfName);
        if (keys.Count == 0) keys.Add(FixtureNames.Key(stem) is { Length: > 0 } k0 ? k0 : stem);
        ModelKeys = keys;
    }

    /// <summary>Full path of the file.</summary>
    public string FilePath { get; }

    /// <summary>File name with extension; the identity used by MVR <c>GDTFSpec</c> and <c>FixtureComponent.GdtfSpec</c>.</summary>
    public string FileName { get; }

    /// <summary>File size in bytes at scan time.</summary>
    public long FileSize { get; }

    /// <summary>Last write time (UTC) at scan time.</summary>
    public DateTime LastWriteTimeUtc { get; }

    /// <summary>Researched data for this file, or null when the specs index has no record.</summary>
    public FixtureSpecs? Specs { get; }

    /// <summary>Manufacturer part of the file name as written (e.g. <c>Robe_Lighting</c>); empty when the name has no '@'.</summary>
    public string FileManufacturer { get; }

    /// <summary>Model part of the file name as written (e.g. <c>Robin_T1_Profile</c>).</summary>
    public string FileModel { get; }

    /// <summary>Revision part of the file name as written (may be empty).</summary>
    public string RevisionText { get; }

    /// <summary>True when the file name has all three parts <c>Manufacturer@Model@Revision</c>.</summary>
    public bool FollowsNamingPattern { get; }

    /// <summary>Manufacturer for display: the specs name, else the file part with underscores as spaces.</summary>
    public string Manufacturer { get; }

    /// <summary>Model name for display (without the brand): the specs long name, else the file part.</summary>
    public string ModelName { get; }

    /// <summary>Brand plus model for display (e.g. "Robe Lighting Robin T1 Profile").</summary>
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Specs?.FullName) ? Specs!.FullName : Manufacturer.Length > 0 ? $"{Manufacturer} {ModelName}" : ModelName;

    /// <summary>Library category (UI tab).</summary>
    public FixtureCategory Category { get; }

    /// <summary>Whether <see cref="Category"/> comes from the specs or from the model name.</summary>
    public CategorySource CategorySource { get; }

    /// <summary>Date/version information of the revision part, used to pick the newest revision.</summary>
    public FixtureRevision Revision { get; }

    /// <summary>Normalised manufacturer core (see <see cref="FixtureNames.ManufacturerKey"/>).</summary>
    internal string ManufacturerKey { get; }

    /// <summary>Normalised model names this entry answers to (file model part, specs long name, GDTF name).</summary>
    internal IReadOnlyList<string> ModelKeys { get; }

    /// <summary>Same manufacturer and model as another entry (i.e. another revision of the same fixture).</summary>
    public bool IsSameModel(FixtureLibraryEntry other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return string.Equals(ManufacturerKey, other.ManufacturerKey, StringComparison.Ordinal) &&
               string.Equals(ModelKeys[0], other.ModelKeys[0], StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public override string ToString() => FileName;

    private static string Readable(string filePart) => filePart.Replace('_', ' ').Trim();
}
