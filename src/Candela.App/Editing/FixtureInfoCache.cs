using System.Collections.Concurrent;
using System.Numerics;
using Candela.Core.Math;
using Candela.Dmx;
using Candela.Gdtf;

namespace Candela.App.Editing;

/// <summary>A DMX mode of a fixture type as the placement popup shows it.</summary>
/// <param name="Name">GDTF mode name.</param>
/// <param name="Breaks">Footprint per DMX break (channels).</param>
/// <param name="Footprint">Channels of all breaks together.</param>
internal sealed record FixtureModeInfo(string Name, IReadOnlyList<DmxBreakLayout> Breaks, int Footprint);

/// <summary>What placing a fixture type needs to know: its modes, the default (first) mode and its size.</summary>
/// <param name="Modes">Modes that compile (others are left out).</param>
/// <param name="RestBounds">Bounds of the fixture at rest in its own coordinates (hanging: base up), m.</param>
/// <param name="Error">Why the fixture type could not be read, or null.</param>
internal sealed record FixtureInfo(IReadOnlyList<FixtureModeInfo> Modes, Aabb RestBounds, string? Error)
{
    /// <summary>The mode a dropped fixture gets (the GDTF's first mode).</summary>
    public FixtureModeInfo? DefaultMode => Modes.Count > 0 ? Modes[0] : null;

    /// <summary>The mode called <paramref name="name"/>, else the default.</summary>
    public FixtureModeInfo? Find(string? name) => Modes.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.Ordinal)) ?? DefaultMode;
}

/// <summary>
/// Reads the modes, footprints and rest-pose size of fixture types on thread-pool threads (parsing a GDTF and compiling
/// its modes can take a moment), so the library panel and the drop preview never wait: <see cref="Get"/> answers null
/// until the information is ready. Main thread calls, background work; results are immutable.
/// </summary>
internal sealed class FixtureInfoCache
{
    /// <summary>Box used while the size of a fixture is unknown (a typical moving head), m.</summary>
    public static readonly Aabb DefaultBounds = new(new Vector3(-0.2f, -0.18f, -0.55f), new Vector3(0.2f, 0.18f, 0f));

    private readonly FixtureLibrary _library;
    // Keyed by the entry object (a library entry is immutable; a changed file gives a new entry), so the per-frame
    // lookups of the hover card and the drop preview do not allocate.
    private readonly ConcurrentDictionary<FixtureLibraryEntry, Task<FixtureInfo>> _tasks = new(ReferenceEqualityComparer.Instance);
    private readonly Func<FixtureLibraryEntry, Task<FixtureInfo>> _start;

    /// <summary>Creates a cache over <paramref name="library"/>.</summary>
    public FixtureInfoCache(FixtureLibrary library)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _start = entry => Task.Run(() => Read(_library, entry));
    }

    /// <summary>The information of <paramref name="entry"/>, or null while it is being read (the read starts now).</summary>
    public FixtureInfo? Get(FixtureLibraryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var task = _tasks.GetOrAdd(entry, _start);
        return task.IsCompletedSuccessfully ? task.Result : task.IsFaulted ? Unreadable(task) : null;
    }

    /// <summary>The information of <paramref name="entry"/>, waiting for it (tests, and a drop that needs the modes now).</summary>
    public FixtureInfo GetNow(FixtureLibraryEntry entry, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var task = _tasks.GetOrAdd(entry, _start);
        try
        {
            return task.Wait(timeout) ? task.Result : new FixtureInfo(Array.Empty<FixtureModeInfo>(), DefaultBounds, "timeout");
        }
        catch (AggregateException)
        {
            return Unreadable(task);
        }
    }

    /// <summary>Reads a fixture type now (on the calling thread). Never throws for content problems.</summary>
    public static FixtureInfo Read(FixtureLibrary library, FixtureLibraryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(entry);
        FixtureType type;
        try
        {
            type = library.GetFixtureType(entry);
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            // Any damage of one file (also one the reader did not foresee) only makes that fixture type unreadable.
            return new FixtureInfo(Array.Empty<FixtureModeInfo>(), DefaultBounds, e.Message);
        }

        var modes = new List<FixtureModeInfo>(type.DmxModes.Count);
        var bounds = Aabb.Empty;
        string? error = null;
        foreach (var mode in type.DmxModes)
        {
            try
            {
                var profile = FixtureProfile.Compile(type, mode.Name, entry.Specs);
                modes.Add(new FixtureModeInfo(mode.Name, profile.Breaks.ToArray(), profile.Footprint));
                if (bounds.IsEmpty)
                    bounds = RestBounds(profile);
            }
            catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
            {
                // A mode that does not compile is left out; the others stay placeable.
                error ??= e.Message;
            }
        }

        if (modes.Count == 0)
            error ??= "The fixture type has no DMX modes.";
        return new FixtureInfo(modes, bounds.IsEmpty ? DefaultBounds : bounds, modes.Count == 0 ? error : null);
    }

    private static Aabb RestBounds(FixtureProfile profile)
    {
        var geometry = profile.Geometry;
        if (geometry.Parts.Count == 0 || geometry.Parts.Count > 100_000)
            return Aabb.Empty;
        var world = new Matrix4x4[geometry.Parts.Count];
        geometry.ComputeWorldTransforms(world, Matrix4x4.Identity);
        var bounds = geometry.ComputeBounds(world);
        bool finite = float.IsFinite(bounds.Min.X) && float.IsFinite(bounds.Min.Y) && float.IsFinite(bounds.Min.Z) &&
                      float.IsFinite(bounds.Max.X) && float.IsFinite(bounds.Max.Y) && float.IsFinite(bounds.Max.Z);
        // A fixture larger than 20 m is a broken file: the preview falls back to the default box.
        return finite && !bounds.IsEmpty && bounds.Size.Length() < 20f ? bounds : Aabb.Empty;
    }

    private static FixtureInfo Unreadable(Task<FixtureInfo> task) =>
        new(Array.Empty<FixtureModeInfo>(), DefaultBounds, task.Exception?.GetBaseException().Message ?? "The fixture type could not be read.");

}
