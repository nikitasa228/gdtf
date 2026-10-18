using System.Collections.Concurrent;
using Candela.Gdtf;

namespace Candela.Dmx;

/// <summary>Outcome of resolving a fixture's GDTF file and mode to a compiled profile.</summary>
/// <param name="Profile">The profile, or null when it could not be built.</param>
/// <param name="Entry">The library entry that was used, or null when no file was found.</param>
/// <param name="MatchKind">How the file was found (exact file name, or matched by manufacturer/model).</param>
/// <param name="Error">Why there is no profile: unknown file, unreadable file, unknown mode.</param>
/// <param name="ErrorKind">Machine-readable error class.</param>
public sealed record ProfileResolution(FixtureProfile? Profile, FixtureLibraryEntry? Entry, FixtureMatchKind? MatchKind, string? Error,
    ProfileErrorKind ErrorKind)
{
    /// <summary>True when a profile is available.</summary>
    public bool Success => Profile is not null;

    /// <summary>
    /// For a <see cref="FixtureMatchKind.Substitute"/>: what the fixture asked for, the stand-in, and the patch spacing its
    /// mode was chosen for (<see cref="SubstituteModes.Choose"/>); null otherwise.
    /// </summary>
    public FixtureSubstitution? Substitution { get; init; }
}

/// <summary>Why a fixture has no profile.</summary>
public enum ProfileErrorKind
{
    /// <summary>No error.</summary>
    None,

    /// <summary>The GDTF file is not in any library folder (and no same-model file matched).</summary>
    UnknownFile,

    /// <summary>The GDTF file exists but cannot be read.</summary>
    UnreadableFile,

    /// <summary>The fixture type has no such DMX mode.</summary>
    UnknownMode,
}

/// <summary>
/// Compiled <see cref="FixtureProfile"/>s per GDTF file + mode, shared by every fixture of the same type. GDTF files are
/// found in a <see cref="FixtureLibrary"/> by file name (as stored in <c>FixtureComponent.GdtfSpec</c>), or for MVR stub
/// names (<c>Manufacturer@Model</c>) by manufacturer and model. Thread-safe; concurrent requests for the same key share
/// one compilation. Results (including failures) are cached until <see cref="Clear"/> or until the file changes on disk.
/// </summary>
public sealed class FixtureProfileCache
{
    private readonly ConcurrentDictionary<string, Lazy<ProfileResolution>> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a cache over <paramref name="library"/>.</summary>
    public FixtureProfileCache(FixtureLibrary library) => Library = library ?? throw new ArgumentNullException(nameof(library));

    /// <summary>The fixture library GDTF files are taken from.</summary>
    public FixtureLibrary Library { get; }

    /// <summary>Number of cached resolutions.</summary>
    public int Count => _cache.Count;

    /// <summary>Drops every cached profile.</summary>
    public void Clear() => _cache.Clear();

    /// <summary>
    /// Resolves <paramref name="gdtfSpec"/> (a GDTF file name, or <c>Manufacturer@Model</c>) and <paramref name="mode"/>
    /// (null = first mode) to a compiled profile. Never throws for bad input; failures are described in the result.
    /// </summary>
    public ProfileResolution Resolve(string? gdtfSpec, string? mode) => Resolve(gdtfSpec, mode, null);

    /// <summary>
    /// <see cref="Resolve(string?, string?)"/> for a fixture whose type is patched <paramref name="patchSpacing"/> channels
    /// apart (<see cref="PatchSpacing"/>): when the library has only a similar model (a substitute), its mode is chosen
    /// to fit that spacing (<see cref="SubstituteModes.Choose"/>) — the requested mode name belongs to the missing model —
    /// and <see cref="ProfileResolution.Substitution"/> says so. Exact matches ignore the spacing.
    /// </summary>
    public ProfileResolution Resolve(string? gdtfSpec, string? mode, int? patchSpacing)
    {
        if (string.IsNullOrWhiteSpace(gdtfSpec))
            return new ProfileResolution(null, null, null, "No GDTF file is set.", ProfileErrorKind.UnknownFile);
        var entry = Library.FindByFileName(gdtfSpec);
        FixtureMatchKind kind = FixtureMatchKind.ExactFileName;
        if (entry is null && Library.FindBySpec(gdtfSpec) is { } match)
        {
            entry = match.Entry;
            kind = match.Kind;
        }
        if (entry is null)
            return new ProfileResolution(null, null, null, $"GDTF file '{gdtfSpec}' is not in the fixture library.", ProfileErrorKind.UnknownFile);
        if (kind == FixtureMatchKind.Substitute)
        {
            string? chosen = mode;
            try
            {
                chosen = SubstituteModes.Choose(Library.GetFixtureType(entry), mode, patchSpacing)?.Name ?? mode;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // unreadable: Get reports it
            }
            return Get(entry, chosen) with
            {
                MatchKind = kind,
                Substitution = new FixtureSubstitution(gdtfSpec.Trim(), mode, entry.FileName, patchSpacing),
            };
        }
        var resolved = Get(entry, mode);
        return resolved with { MatchKind = kind };
    }

    /// <summary>Compiled profile of a library entry in <paramref name="mode"/> (null = first mode).</summary>
    public ProfileResolution Get(FixtureLibraryEntry entry, string? mode)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string key = $"{entry.FilePath}|{entry.LastWriteTimeUtc.Ticks}|{entry.FileSize}|{mode ?? ""}";
        var lazy = _cache.GetOrAdd(key, _ => new Lazy<ProfileResolution>(() => Compile(entry, mode), LazyThreadSafetyMode.ExecutionAndPublication));
        return lazy.Value;
    }

    private ProfileResolution Compile(FixtureLibraryEntry entry, string? mode)
    {
        FixtureType type;
        try
        {
            type = Library.GetFixtureType(entry);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new ProfileResolution(null, entry, FixtureMatchKind.ExactFileName, $"GDTF file '{entry.FileName}' cannot be read: {e.Message}",
                ProfileErrorKind.UnreadableFile);
        }
        try
        {
            var profile = FixtureProfile.Compile(type, mode, entry.Specs);
            return new ProfileResolution(profile, entry, FixtureMatchKind.ExactFileName, null, ProfileErrorKind.None);
        }
        catch (FixtureProfileException e)
        {
            var kind = type.FindMode(mode) is null ? ProfileErrorKind.UnknownMode : ProfileErrorKind.UnreadableFile;
            return new ProfileResolution(null, entry, FixtureMatchKind.ExactFileName, e.Message, kind);
        }
    }
}
