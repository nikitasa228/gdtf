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

    /// <summary>The channels as an array (per-frame path).</summary>
    internal ProfileChannel[] ChannelArray { get; }

    /// <summary>The relations as an array (per-frame path).</summary>
    internal ProfileRelation[] RelationArray { get; }

    /// <summary>The beams as an array (per-frame path).</summary>
    internal ProfileBeam[] BeamArray { get; }

    /// <summary>The wheels as an array (per-frame path).</summary>
    internal ProfileWheel[] WheelArray { get; }

    /// <summary>Per axis: the channels moving it (same as <see cref="ProfileAxis.Channels"/>, as arrays).</summary>
    internal int[][] AxisChannels { get; }

    /// <summary>Channels that follow at least one relation master, ascending.</summary>
    internal int[] FollowerChannels { get; }

    /// <summary>Per channel: where its bytes are in the fixture's DMX (shared by every buffer of the profile).</summary>
    internal ChannelSlots[] SlotLayout { get; }

    /// <summary>
    /// Per channel: index of its motor state in <see cref="FixtureState"/> when one of its functions is motorised or
    /// rotates (needs motor dynamics), −1 for electronic channels.
    /// </summary>
    internal int[] MotorSlot { get; }

    /// <summary>Number of channels with a motor state.</summary>
    internal int MotorCount { get; }

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
        var c = ChannelArray[channel];
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
        ChannelArray = b.Channels.ToArray();
        Channels = ChannelArray;
        FunctionArray = b.Functions.ToArray();
        Functions = FunctionArray;
        RelationArray = b.Relations.ToArray();
        Relations = RelationArray;
        WheelArray = b.Wheels.ToArray();
        Wheels = WheelArray;
        Emitters = b.Emitters.ToArray();
        BeamArray = b.Beams.ToArray();
        Beams = BeamArray;
        Axes = b.Axes.ToArray();
        AxisChannels = Array.ConvertAll(b.Axes.ToArray(), a => a.Channels.ToArray());
        IsAtmospheric = b.IsAtmospheric;
        Warnings = b.Warnings.ToArray();
        SharedBeamChannels = b.SharedBeamChannels;
        OutputChannels = b.OutputChannels;
        ModifierChannels = b.ModifierChannels;
        FollowerRelations = b.FollowerRelations;
        FollowerChannels = Enumerable.Range(0, b.FollowerRelations.Length).Where(c => b.FollowerRelations[c].Length > 0).ToArray();
        SlotLayout = Array.ConvertAll(ChannelArray, ChannelSlots.Of);
        MotorSlot = new int[ChannelArray.Length];
        foreach (var ch in ChannelArray)
        {
            bool moves = false;
            for (int k = ch.FirstFunction; k < ch.FirstFunction + ch.FunctionCount; k++)
                moves |= FunctionArray[k].Motion != MotionKind.None || FunctionArray[k].IsRotation;
            MotorSlot[ch.Index] = moves ? MotorCount++ : -1;
        }
        FlameChannel = b.FlameChannel;
        ColorSpacePrimaries = b.ColorSpacePrimaries;
        IsRelationMaster = b.IsRelationMaster;
        _channelsByAttribute = b.ChannelsByAttribute;
        _channelsByName = b.ChannelsByName;
    }
}

/// <summary>Where one channel's bytes are: break index (−1 for virtual channels), first 0-based slot, byte count, and whether the bytes are consecutive.</summary>
internal readonly struct ChannelSlots
{
    private ChannelSlots(int breakIndex, int first, int bytes, bool contiguous)
    {
        Break = breakIndex;
        First = first;
        Bytes = bytes;
        Contiguous = contiguous;
    }

    /// <summary>Index into the profile's breaks, −1 for a virtual channel.</summary>
    public readonly int Break;

    /// <summary>0-based slot of the coarse byte.</summary>
    public readonly int First;

    /// <summary>Number of bytes.</summary>
    public readonly int Bytes;

    /// <summary>True when the bytes are at First, First + 1, … (GDTF allows any offsets).</summary>
    public readonly bool Contiguous;

    public static ChannelSlots Of(ProfileChannel ch)
    {
        var offsets = ch.Instance.Offsets;
        if (ch.IsVirtual || ch.BreakIndex < 0) return new ChannelSlots(-1, 0, 0, true);
        bool contiguous = true;
        for (int i = 1; i < offsets.Length; i++) contiguous &= offsets[i] == offsets[0] + i;
        return new ChannelSlots(ch.BreakIndex, offsets[0] - 1, offsets.Length, contiguous);
    }
}
