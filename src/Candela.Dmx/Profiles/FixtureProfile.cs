using Candela.Gdtf;

namespace Candela.Dmx;

/// <summary>A master/follower relation of a profile, resolved to channel instances.</summary>
/// <param name="Master">Master channel index.</param>
/// <param name="Follower">Follower channel index.</param>
/// <param name="FollowerFunction">Follower function index (the relation applies while it is active).</param>
/// <param name="Type">Multiply (virtual dimmer) or Override.</param>
public readonly record struct ProfileRelation(int Master, int Follower, int FollowerFunction, RelationType Type);

/// <summary>Thrown when a fixture type cannot be compiled for the requested mode (e.g. the mode does not exist).</summary>
public sealed class FixtureProfileException : Exception
{
    /// <summary>Creates the exception.</summary>
    public FixtureProfileException(string message) : base(message) { }

    /// <summary>Creates the exception with an inner cause.</summary>
    public FixtureProfileException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// The runtime layout of one GDTF fixture type in one DMX mode, compiled once (see <see cref="FixtureProfileCache"/>) and
/// shared by every fixture of that type: DMX breaks and footprints; one <see cref="ProfileChannel"/> per channel
/// instance (GeometryReferences expanded, so each pixel or head has its own channels and offsets); channel functions
/// with DMX ranges at channel resolution, mode-master conditions and pre-interpreted channel sets; relations; the
/// geometry tree (<see cref="Geometry"/>) with the axes and beams that channels control; wheels, emitters and the
/// photometric reference of every beam. Immutable after compilation and safe to share between threads.
/// </summary>
public sealed partial class FixtureProfile
{
    private readonly Dictionary<string, int[]> _channelsByAttribute;
    private readonly Dictionary<string, int> _channelsByName;

    /// <summary>Cache key: <c>{GDTF file name}#{mode name}</c>.</summary>
    public string Key { get; }

    /// <summary>The parsed fixture type.</summary>
    public FixtureType FixtureType { get; }

    /// <summary>The DMX mode.</summary>
    public DmxMode Mode { get; }

    /// <summary>Researched physical data (<c>_specs</c>), if known.</summary>
    public FixtureSpecs? Specs { get; }

    /// <summary>Fixture category (from the specs, else guessed from the name).</summary>
    public FixtureCategory Category { get; }

    /// <summary>The mode's geometry tree flattened into parts (no meshes loaded); axes and beams refer to its part indices.</summary>
    public FixtureGeometry Geometry { get; }

    /// <summary>DMX breaks in ascending break order.</summary>
    public IReadOnlyList<DmxBreakLayout> Breaks { get; }

    /// <summary>Total number of DMX slots (sum of the break footprints).</summary>
    public int Footprint { get; }

    /// <summary>All channel instances (addressed and virtual).</summary>
    public IReadOnlyList<ProfileChannel> Channels { get; }

    /// <summary>All channel functions of all channels; a channel's are contiguous from <see cref="ProfileChannel.FirstFunction"/>.</summary>
    public IReadOnlyList<ProfileFunction> Functions { get; }

    /// <summary>Relations resolved to channel instances.</summary>
    public IReadOnlyList<ProfileRelation> Relations { get; }

    /// <summary>Wheels used by the mode.</summary>
    public IReadOnlyList<ProfileWheel> Wheels { get; }

    /// <summary>Additive emitters referenced by ColorAdd functions.</summary>
    public IReadOnlyList<ProfileEmitter> Emitters { get; }

    /// <summary>Light-emitting Beam geometry instances.</summary>
    public IReadOnlyList<ProfileBeam> Beams { get; }

    /// <summary>Axis geometry instances moved by DMX (Pan, Tilt, rotations, translations).</summary>
    public IReadOnlyList<ProfileAxis> Axes { get; }

    /// <summary>
    /// True for atmospheric and effect devices (HAZE, FOG, FAN, GENERATOR FIRE, or no light output but Fog/Haze/Fan/Flame
    /// channels): only for them do Fan channels drive <see cref="DeviceOutputs.FanSpeed"/>; in lights they are cooling fans.
    /// </summary>
    public bool IsAtmospheric { get; }

    /// <summary>Problems found while compiling (unresolved mode masters, channels outside the geometry …).</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>The functions as an array (fast indexed access on the per-frame path).</summary>
    internal ProfileFunction[] FunctionArray { get; }

    /// <summary>Channels whose functions affect every beam (evaluated once per frame, not per beam).</summary>
    internal int[] SharedBeamChannels { get; }

    /// <summary>Fog/Haze/Fan/Flame channels.</summary>
    internal int[] OutputChannels { get; }

    /// <summary>Channels with fixture-wide modifiers: motor speed, dimmer curve, pan/tilt invert switches.</summary>
    internal int[] ModifierChannels { get; }

    /// <summary>For GENERATOR FIRE devices without Flame attributes: the dimmer/fog channel that fires the flame, else −1.</summary>
    internal int FlameChannel { get; }

    /// <summary>XYZ (white Y = 1) of the red, green and blue primaries of the fixture's colour space (ColorRGB, HSB).</summary>
    internal System.Numerics.Vector3[] ColorSpacePrimaries { get; }

    /// <summary>Per channel: indices into <see cref="Relations"/> where the channel is the follower.</summary>
    internal int[][] FollowerRelations { get; }

    /// <summary>Per beam-affecting channel index: true when the channel is a Multiply relation master.</summary>
    internal bool[] IsRelationMaster { get; }

    /// <summary>Compiles <paramref name="fixtureType"/> in mode <paramref name="modeName"/> (null or empty: the first mode).</summary>
    /// <exception cref="FixtureProfileException">The fixture type has no such mode (or no modes at all).</exception>
    public static FixtureProfile Compile(FixtureType fixtureType, string? modeName = null, FixtureSpecs? specs = null)
    {
        ArgumentNullException.ThrowIfNull(fixtureType);
        var mode = fixtureType.FindMode(modeName)
            ?? throw new FixtureProfileException(fixtureType.DmxModes.Count == 0
                ? $"'{fixtureType}' has no DMX modes."
                : $"'{fixtureType}' has no DMX mode '{modeName}'. Modes: {string.Join(", ", fixtureType.DmxModes.Select(m => m.Name))}.");
        return new Builder(fixtureType, mode, specs).Build();
    }

    /// <summary>Indices of the channels that have a function with <paramref name="attribute"/> (case-insensitive); empty when none.</summary>
    public IReadOnlyList<int> FindChannels(string attribute) =>
        attribute is not null && _channelsByAttribute.TryGetValue(attribute, out var list) ? list : Array.Empty<int>();

    /// <summary>
    /// Channel by GDTF channel name, optionally qualified with an instance (<c>"Pixel_ColorAdd_R@Pixel 3"</c>); −1 when
    /// not found. Unqualified names of instanced channels return the first instance.
    /// </summary>
    public int FindChannel(string name) => name is not null && _channelsByName.TryGetValue(name, out int i) ? i : -1;

    /// <summary>The functions of channel <paramref name="channel"/>.</summary>
    public ReadOnlySpan<ProfileFunction> FunctionsOf(int channel)
    {
        var c = Channels[channel];
        return FunctionArray.AsSpan(c.FirstFunction, c.FunctionCount);
    }

    /// <summary>Index of the break with GDTF number <paramref name="dmxBreak"/>, or −1.</summary>
    public int BreakIndexOf(int dmxBreak)
    {
        for (int i = 0; i < Breaks.Count; i++)
            if (Breaks[i].Break == dmxBreak) return i;
        return -1;
    }

    /// <inheritdoc />
    public override string ToString() => $"{FixtureType} [{Mode.Name}] {Footprint} ch";

    private FixtureProfile(Builder b)
    {
        Key = b.Key;
        FixtureType = b.FixtureType;
        Mode = b.Mode;
        Specs = b.Specs;
        Category = b.Category;
        Geometry = b.Geometry;
        Breaks = b.Breaks;
        Footprint = b.Breaks.Sum(x => x.Footprint);
        Channels = b.Channels.ToArray();
        FunctionArray = b.Functions.ToArray();
        Functions = FunctionArray;
        Relations = b.Relations.ToArray();
        Wheels = b.Wheels.ToArray();
        Emitters = b.Emitters.ToArray();
        Beams = b.Beams.ToArray();
        Axes = b.Axes.ToArray();
        IsAtmospheric = b.IsAtmospheric;
        Warnings = b.Warnings.ToArray();
        SharedBeamChannels = b.SharedBeamChannels;
        OutputChannels = b.OutputChannels;
        ModifierChannels = b.ModifierChannels;
        FollowerRelations = b.FollowerRelations;
        FlameChannel = b.FlameChannel;
        ColorSpacePrimaries = b.ColorSpacePrimaries;
        IsRelationMaster = b.IsRelationMaster;
        _channelsByAttribute = b.ChannelsByAttribute;
        _channelsByName = b.ChannelsByName;
    }
}
