using System.Collections.Concurrent;
using Candela.Gdtf;

namespace Candela.Electrical;

/// <summary>
/// Supplies <see cref="FixtureProfile"/>s for <c>FixtureComponent.GdtfSpec</c> values. Implementations must be safe to
/// call from several threads and should cache: the network solver asks once per fixture per solve.
/// </summary>
public interface IFixtureProfileProvider
{
    /// <summary>Profile of a GDTF spec in a DMX mode (null = first mode), or null when the fixture type is unknown.</summary>
    FixtureProfile? GetProfile(string gdtfSpec, string? mode);
}

/// <summary>
/// Profiles from a <see cref="FixtureLibrary"/>: the spec is resolved with <see cref="FixtureLibrary.FindBySpec"/>
/// (so MVR stub names find library files and substitutes), the GDTF is parsed through the library cache and the
/// specs record comes from the library's specs index (or, for the cable tool's generic devices, from
/// <see cref="GenericDevices"/>). A GDTF that cannot be read still gets a profile from its specs
/// record (with a note); a spec unknown to both gives null. Profiles are cached per spec + mode.
/// </summary>
public sealed class LibraryFixtureProfileProvider : IFixtureProfileProvider
{
    private readonly FixtureLibrary _library;
    private readonly ConcurrentDictionary<string, Lazy<FixtureProfile?>> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a provider over a library.</summary>
    public LibraryFixtureProfileProvider(FixtureLibrary library)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
    }

    /// <summary>The library profiles are read from.</summary>
    public FixtureLibrary Library => _library;

    /// <inheritdoc />
    public FixtureProfile? GetProfile(string gdtfSpec, string? mode)
    {
        if (string.IsNullOrWhiteSpace(gdtfSpec)) return null;
        string key = gdtfSpec.Trim() + "\u0001" + (mode ?? "");
        return _cache.GetOrAdd(key, _ => new Lazy<FixtureProfile?>(() => Build(gdtfSpec.Trim(), mode))).Value;
    }

    /// <summary>Drops all cached profiles (after library folders or files changed).</summary>
    public void Invalidate() => _cache.Clear();

    private FixtureProfile? Build(string spec, string? mode)
    {
        var match = _library.FindBySpec(spec);
        if (match is null)
        {
            var specsOnly = _library.Specs.Find(spec) ?? GenericDevices.FindSpecs(spec);
            return specsOnly is null ? null : FixtureProfile.Build(spec, mode, null, specsOnly, specsOnly.File);
        }
        var entry = match.Entry;
        // Generic devices of the cable tool are not in the researched specs index; their typical values are built in.
        var specs = entry.Specs ?? _library.Specs.Find(entry.FileName) ?? GenericDevices.FindSpecs(entry.FileName);
        FixtureType? type = null;
        var notes = new List<string>();
        try
        {
            type = _library.GetFixtureType(entry);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // One unreadable file must not break the analysis of the scene: fall back to the specs record.
            notes.Add($"GDTF '{entry.FileName}' could not be read ({e.Message}); ports and power come from _specs only.");
        }
        if (type is not null && mode is not null && type.FindMode(mode) is null)
            notes.Add($"DMX mode '{mode}' is not in '{entry.FileName}'; the first mode's ports are used.");
        bool substitute = match.Kind == FixtureMatchKind.Substitute;
        if (substitute)
            notes.Add($"'{spec}' is not in the fixture library; its ports, power draw and weight are those of the similar model {entry.DisplayName} ({entry.FileName}).");
        try
        {
            return FixtureProfile.Build(spec, mode, type, specs, entry.FileName, substitute, notes);
        }
        catch (Exception e) when (e is not OutOfMemoryException && type is not null)
        {
            // Malformed GDTF content that the builder cannot interpret: keep what the specs record knows.
            notes.Add($"GDTF '{entry.FileName}' could not be interpreted ({e.Message}); ports and power come from _specs only.");
            return FixtureProfile.Build(spec, mode, null, specs, entry.FileName, substitute, notes);
        }
    }
}

/// <summary>Profiles registered in code (tests, procedural scenes, devices that have no GDTF).</summary>
public sealed class InMemoryFixtureProfileProvider : IFixtureProfileProvider
{
    private readonly ConcurrentDictionary<string, FixtureProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers (or replaces) the profile of a spec; it is returned for every mode.</summary>
    public InMemoryFixtureProfileProvider Add(string gdtfSpec, FixtureProfile profile)
    {
        ArgumentNullException.ThrowIfNull(gdtfSpec);
        _profiles[gdtfSpec] = profile ?? throw new ArgumentNullException(nameof(profile));
        return this;
    }

    /// <inheritdoc />
    public FixtureProfile? GetProfile(string gdtfSpec, string? mode) =>
        gdtfSpec is not null && _profiles.TryGetValue(gdtfSpec, out var p) ? p : null;
}
