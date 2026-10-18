using System.Numerics;
using Candela.Gdtf;
using PhotometryEmitter = Candela.Photometry.Emitter;

namespace Candela.Dmx;

/// <summary>
/// Speed and acceleration limits of a motorised attribute, in the attribute's control units (degrees for angles, slot
/// positions for wheels, 0 … 1 for normalised levels) per second and per second². Infinite values mean "no limit";
/// <see cref="Instant"/> marks electronic attributes that follow DMX immediately.
/// </summary>
public readonly record struct MotorLimits
{
    private readonly float _maxAcceleration;

    /// <summary>Creates limits.</summary>
    /// <param name="maxSpeed">Largest speed, units/s (∞ = jumps).</param>
    /// <param name="maxAcceleration">Largest acceleration, units/s² (∞ = speed changes instantly).</param>
    public MotorLimits(float maxSpeed, float maxAcceleration)
    {
        MaxSpeed = maxSpeed;
        MaxAcceleration = maxAcceleration;
    }

    /// <summary>Largest speed, units/s (∞ = jumps).</summary>
    public float MaxSpeed { get; init; }

    /// <summary>Largest acceleration, units/s² (∞ = speed changes instantly).</summary>
    public float MaxAcceleration
    {
        get => _maxAcceleration;
        init
        {
            _maxAcceleration = value;
            InverseAcceleration = value > 0f ? 1.0 / value : double.PositiveInfinity;
        }
    }

    /// <summary>1 / <see cref="MaxAcceleration"/> (s²/unit), precomputed for the per-frame integration.</summary>
    internal double InverseAcceleration { get; init; }

    /// <summary>No dynamics: the value follows its target immediately.</summary>
    public static MotorLimits Instant => new(float.PositiveInfinity, float.PositiveInfinity);

    /// <summary>True when the value follows its target immediately.</summary>
    public bool IsInstant => float.IsPositiveInfinity(MaxSpeed);

    /// <summary>The limits scaled by a speed factor (MSpeed channels): speed × f, acceleration × f².</summary>
    public MotorLimits Scaled(float factor) =>
        IsInstant || !(factor > 0f) || factor == 1f ? this : new MotorLimits(MaxSpeed * factor, MaxAcceleration * factor * factor);
}

/// <summary>How a motorised channel's control value moves between DMX frames.</summary>
public enum MotionKind
{
    /// <summary>Electronic: follows DMX immediately.</summary>
    None,

    /// <summary>A position (angle, slot, insertion) that travels to its target with speed and acceleration limits.</summary>
    Position,

    /// <summary>A rotation speed (deg/s) that ramps with an angular acceleration limit; the angle is integrated.</summary>
    Rotation,
}

/// <summary>One DMX break of a profile: its GDTF break number and how many slots the fixture occupies in it.</summary>
/// <param name="Break">1-based GDTF DMX break.</param>
/// <param name="Footprint">Highest slot offset used in this break (the number of slots from the break's start address).</param>
public readonly record struct DmxBreakLayout(int Break, int Footprint);

/// <summary>A named DMX sub-range of a channel function, pre-interpreted (see <see cref="NameParsing"/>).</summary>
public sealed class ProfileChannelSet
{
    internal ProfileChannelSet(ChannelSet source, uint from, uint to, float physicalFrom, float physicalTo, ChannelSetFlags flags)
    {
        Source = source;
        DmxFrom = from;
        DmxTo = to;
        PhysicalFrom = physicalFrom;
        PhysicalTo = physicalTo;
        Flags = flags;
    }

    /// <summary>The GDTF channel set.</summary>
    public ChannelSet Source { get; }

    /// <summary>Name as written.</summary>
    public string Name => Source.Name;

    /// <summary>First DMX value (channel resolution).</summary>
    public uint DmxFrom { get; }

    /// <summary>Last DMX value (channel resolution).</summary>
    public uint DmxTo { get; }

    /// <summary>Physical value at <see cref="DmxFrom"/>, NaN when the set inherits the function's mapping.</summary>
    public float PhysicalFrom { get; }

    /// <summary>Physical value at <see cref="DmxTo"/>, NaN when the set inherits the function's mapping.</summary>
    public float PhysicalTo { get; }

    /// <summary>True when the set has its own physical range.</summary>
    public bool HasPhysical => !float.IsNaN(PhysicalFrom);

    /// <summary>1-based wheel slot the set selects, 0 when none.</summary>
    public int WheelSlot => Source.WheelSlotIndex;

    /// <summary>Meaning recognised in the name.</summary>
    public ChannelSetFlags Flags { get; }

    /// <summary>Colour temperature named by the set (K), NaN when none.</summary>
    public float Kelvin { get; internal set; } = float.NaN;

    /// <summary>Colour named by the set (CIE 1931 xy), NaN when none.</summary>
    public Vector2 Color { get; internal set; } = new(float.NaN, float.NaN);

    /// <summary>Strobe waveform named by the set, if any.</summary>
    public Waveform? Waveform { get; internal set; }

    /// <summary>Dimmer law named by the set (DimmerCurve channels).</summary>
    public DimmerCurve DimmerCurve { get; internal set; }

    /// <summary>True when <paramref name="value"/> lies in the set's DMX range.</summary>
    public bool Contains(uint value) => value >= DmxFrom && value <= DmxTo;

    /// <inheritdoc />
    public override string ToString() => $"{Name} [{DmxFrom}…{DmxTo}]";
}

/// <summary>
/// A channel function compiled for evaluation: DMX range at the channel's resolution, physical mapping, mode-master
/// condition resolved to a profile channel, classification of its attribute and pre-interpreted channel sets.
/// </summary>
public sealed class ProfileFunction
{
    internal ProfileFunction(int index, int channel, ChannelFunction source, string attribute, AttributeClass @class)
    {
        Index = index;
        Channel = channel;
        Source = source;
        Attribute = attribute;
        Class = @class;
    }

    /// <summary>Index in <see cref="FixtureProfile.Functions"/>.</summary>
    public int Index { get; }

    /// <summary>Index of the owning channel in <see cref="FixtureProfile.Channels"/>.</summary>
    public int Channel { get; }

    /// <summary>The GDTF channel function.</summary>
    public ChannelFunction Source { get; }

    /// <summary>Function name.</summary>
    public string Name => Source.Name;

    /// <summary>GDTF attribute name ("NoFeature" for functions without attribute).</summary>
    public string Attribute { get; }

    /// <summary>What the attribute does.</summary>
    public AttributeClass Class { get; }

    /// <summary>Physical unit of the attribute.</summary>
    public PhysicalUnit Unit { get; internal set; }

    /// <summary>First DMX value (channel resolution).</summary>
    public uint DmxFrom { get; internal set; }

    /// <summary>Last DMX value (channel resolution).</summary>
    public uint DmxTo { get; internal set; }

    /// <summary>Physical value at <see cref="DmxFrom"/> (GDTF units: degrees, Hz, K, …).</summary>
    public float PhysicalFrom { get; internal set; }

    /// <summary>Physical value at <see cref="DmxTo"/>.</summary>
    public float PhysicalTo { get; internal set; }

    /// <summary>DMX-to-physical curve (GDTF 1.2 DMXProfile), applied between From and To; null = linear.</summary>
    public DmxProfile? Curve { get; internal set; }

    /// <summary>Divisor that turns the physical value of a level attribute into 0 … 1 (100 for percent ranges).</summary>
    public float LevelScale { get; internal set; } = 1f;

    /// <summary>Mode-master channel index, or −1 when the function is always available.</summary>
    public int ModeMaster { get; internal set; } = -1;

    /// <summary>First master DMX value (master's resolution) for which the function is active.</summary>
    public uint ModeFrom { get; internal set; }

    /// <summary>Last master DMX value (master's resolution) for which the function is active.</summary>
    public uint ModeTo { get; internal set; }

    /// <summary>Index in <see cref="FixtureProfile.Wheels"/> of the wheel the function uses, or −1.</summary>
    public int Wheel { get; internal set; } = -1;

    /// <summary>Index in <see cref="FixtureProfile.Emitters"/> of the additive emitter (ColorAdd functions), or −1.</summary>
    public int Emitter { get; internal set; } = -1;

    /// <summary>Channel sets ordered by DMX value.</summary>
    public IReadOnlyList<ProfileChannelSet> Sets => SetArray;

    /// <summary>The channel sets as an array (per-frame path).</summary>
    internal ProfileChannelSet[] SetArray { get; private set; } = Array.Empty<ProfileChannelSet>();

    /// <summary>First DMX value of every set (same order as <see cref="SetArray"/>), for the binary search on the per-frame path.</summary>
    internal uint[] SetDmxFrom { get; private set; } = Array.Empty<uint>();

    /// <summary>Flags of every set (same order as <see cref="SetArray"/>), so the per-frame path need not visit the set objects.</summary>
    internal ChannelSetFlags[] SetFlagArray { get; private set; } = Array.Empty<ChannelSetFlags>();

    /// <summary>Installs the channel sets (sorted by DMX value).</summary>
    internal void SetSets(ProfileChannelSet[] sets)
    {
        SetArray = sets;
        SetDmxFrom = Array.ConvertAll(sets, s => s.DmxFrom);
        SetFlagArray = Array.ConvertAll(sets, s => s.Flags);
    }

    /// <summary>Flags of set <paramref name="setIndex"/> (−1: outside every set, the function's own <see cref="NameFlags"/>).</summary>
    internal ChannelSetFlags FlagsOf(int setIndex) => setIndex >= 0 ? SetFlagArray[setIndex] : NameFlags;

    /// <summary>
    /// Open/closed meaning of a shutter function's own name ("Open", "Closed"), applied where no named channel set says
    /// otherwise; none for other attributes.
    /// </summary>
    internal ChannelSetFlags NameFlags { get; set; }

    /// <summary>
    /// True when the function's physical range is GDTF's default 0 … 1 (or 1 … 0): for rates (strobe and pulse
    /// frequencies) it is then a normalised slow … fast, not 0 … 1 Hz.
    /// </summary>
    internal bool DefaultPhysicalRange { get; set; }

    /// <summary>Motion group (MSpeed channels scale it): 0 position, 1 colour, 2 gobo, 3 beam, 4 shaper, 5 other.</summary>
    internal int MotionGroup { get; set; } = 5;

    /// <summary>True for continuous rotations (endless pan/tilt, wheel spin, slot rotation) whose angle is integrated.</summary>
    internal bool IsRotation { get; set; }

    /// <summary>
    /// The physical quantity the function's motor carries: wheel slot selections share one so a wheel moves
    /// continuously from one selection function to another.
    /// </summary>
    internal AttributeKind MotorQuantity { get; set; }

    /// <summary>For MSpeed functions: the motion group they scale (−1 = all groups).</summary>
    internal int SpeedGroup { get; set; } = -1;

    /// <summary>Angular acceleration of a rotation's speed ramp, deg/s² (∞ = instant).</summary>
    internal double SpinAcceleration { get; set; } = double.PositiveInfinity;

    /// <summary>Colour code of ColorRgb/ColorSub/BladeSoft functions: 0 R, 1 G, 2 B, 3 C, 4 M, 5 Y, 6 side A, 7 side B; −1 otherwise.</summary>
    internal int ColorCode { get; set; } = -1;

    /// <summary>Time to travel the full physical range at full speed, s (GDTF RealFade; 0 = unknown).</summary>
    public float RealFade => Source.RealFade;

    /// <summary>Time to reach full speed, s (GDTF RealAcceleration; 0 = unknown).</summary>
    public float RealAcceleration => Source.RealAcceleration;

    /// <summary>Colour temperature at <see cref="DmxFrom"/> for CTO/CTB/CTC functions, K (NaN when not known).</summary>
    public float KelvinFrom { get; internal set; } = float.NaN;

    /// <summary>Colour temperature at <see cref="DmxTo"/> for CTO/CTB/CTC functions, K (NaN when not known).</summary>
    public float KelvinTo { get; internal set; } = float.NaN;

    /// <summary>
    /// Colour temperature at which a CTO/CTB function inserts no filter, K: the warm-converting CTO is neutral at its
    /// highest kelvin, CTB at its lowest. NaN for CTC (relative to the source) and for functions without kelvin data.
    /// </summary>
    public float NeutralKelvin { get; internal set; } = float.NaN;

    /// <summary>Strobe waveform of the function (shutter, iris, frost pulses, StrobeMode).</summary>
    public Waveform Waveform { get; internal set; }

    /// <summary>
    /// For wheel slot selection functions: DMX anchors (ascending) and the 1-based slot position at each, interpolated
    /// linearly between adjacent slots (split colours) and held otherwise. Empty for other functions.
    /// </summary>
    internal uint[] SlotDmx { get; set; } = Array.Empty<uint>();

    internal float[] SlotPosition { get; set; } = Array.Empty<float>();

    internal bool[] SlotInterpolate { get; set; } = Array.Empty<bool>();

    /// <summary>True when the control value of this function is a wheel slot position rather than a GDTF physical value.</summary>
    public bool UsesSlotPosition => SlotDmx.Length > 0;

    /// <summary>How the control value of the function moves (see <see cref="MotionKind"/>).</summary>
    public MotionKind Motion { get; internal set; }

    /// <summary>Speed/acceleration limits for <see cref="Motion"/>.</summary>
    public MotorLimits Limits
    {
        get => _limits;
        internal set => _limits = value;
    }

    private MotorLimits _limits = MotorLimits.Instant;

    /// <summary>The limits by reference (per-frame path; no struct copy).</summary>
    internal ref readonly MotorLimits LimitsRef => ref _limits;

    /// <summary>Smallest control value the function produces.</summary>
    public float MinValue { get; internal set; }

    /// <summary>Largest control value the function produces.</summary>
    public float MaxValue { get; internal set; }

    /// <summary>True when the function maps every DMX value to the same control value.</summary>
    public bool IsConstant => MinValue == MaxValue;

    /// <summary>Physical value at a DMX value inside the function's range (linear or DMX-profile curve, clamped).</summary>
    public float PhysicalAt(uint dmx)
    {
        if (DmxTo <= DmxFrom) return PhysicalFrom;
        double t = Math.Clamp(((double)dmx - DmxFrom) / ((double)DmxTo - DmxFrom), 0.0, 1.0);
        if (Curve is { } curve)
        {
            float c = curve.Evaluate((float)(t * 100.0));
            t = float.IsFinite(c) ? Math.Clamp(c > 1.5f ? c / 100f : c, 0f, 1f) : t;
        }
        return (float)(PhysicalFrom + (PhysicalTo - PhysicalFrom) * t);
    }

    /// <summary>Normalised position 0 … 1 of a DMX value within the function's range.</summary>
    public float Fraction(uint dmx) =>
        DmxTo <= DmxFrom ? 0f : (float)Math.Clamp(((double)dmx - DmxFrom) / ((double)DmxTo - DmxFrom), 0.0, 1.0);

    /// <summary>The channel set containing <paramref name="dmx"/>, or null.</summary>
    public ProfileChannelSet? SetAt(uint dmx)
    {
        int i = SetIndexAt(dmx);
        return i >= 0 ? SetArray[i] : null;
    }

    /// <summary>Index in <see cref="Sets"/> of the set containing <paramref name="dmx"/>, or −1 (binary search).</summary>
    internal int SetIndexAt(uint dmx)
    {
        var from = SetDmxFrom;
        int lo = 0, hi = from.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (from[mid] <= dmx) lo = mid + 1; else hi = mid;
        }
        int i = lo - 1; // last set starting at or below dmx
        return i >= 0 && dmx <= SetArray[i].DmxTo ? i : -1;
    }

    /// <summary>Wheel slot position (1-based, fractional between adjacent slots) at a DMX value, for slot selection functions.</summary>
    public float SlotPositionAt(uint dmx)
    {
        var d = SlotDmx;
        if (d.Length == 0) return 0f;
        if (dmx <= d[0]) return SlotPosition[0];
        if (dmx >= d[^1]) return SlotPosition[^1];
        int lo = 0, hi = d.Length - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (d[mid] <= dmx) lo = mid; else hi = mid;
        }
        if (!SlotInterpolate[lo] || d[hi] == d[lo]) return dmx - d[lo] <= d[hi] - dmx ? SlotPosition[lo] : SlotPosition[hi];
        float f = (float)(dmx - d[lo]) / (d[hi] - d[lo]);
        return SlotPosition[lo] + (SlotPosition[hi] - SlotPosition[lo]) * f;
    }

    /// <inheritdoc />
    public override string ToString() => $"{Source.FullName} ({Attribute}) [{DmxFrom}…{DmxTo}]";
}

/// <summary>
/// One addressable DMX channel of a profile: a <see cref="DmxChannelInstance"/> (so every pixel/head has its own
/// channels) with byte layout, default and highlight values, the geometry part it controls and its functions.
/// </summary>
public sealed class ProfileChannel
{
    internal ProfileChannel(int index, DmxChannelInstance instance, int breakIndex, int partIndex, AttributeClass @class)
    {
        Index = index;
        Instance = instance;
        BreakIndex = breakIndex;
        PartIndex = partIndex;
        Class = @class;
        ByteCount = Math.Clamp(instance.Channel.Resolution, 1, DmxValue.MaxByteCount);
    }

    /// <summary>Index in <see cref="FixtureProfile.Channels"/>.</summary>
    public int Index { get; }

    /// <summary>The GDTF channel instance (template channel + reference chain + absolute offsets).</summary>
    public DmxChannelInstance Instance { get; }

    /// <summary>The GDTF channel definition.</summary>
    public DmxChannel Source => Instance.Channel;

    /// <summary>GDTF channel name (<c>Geometry_Attribute</c>).</summary>
    public string Name => Source.Name;

    /// <summary>Instance name (e.g. "Pixel 3"), the geometry name for direct channels.</summary>
    public string InstanceName => Instance.InstanceName;

    /// <summary>Attribute of the channel's logical channel.</summary>
    public string Attribute { get; internal set; } = "";

    /// <summary>Classification of <see cref="Attribute"/>.</summary>
    public AttributeClass Class { get; }

    /// <summary>GDTF DMX break (1-based).</summary>
    public int Break => Instance.Break;

    /// <summary>Index into <see cref="FixtureProfile.Breaks"/>, −1 for virtual channels.</summary>
    public int BreakIndex { get; }

    /// <summary>1-based offsets within the break, coarse first; empty for virtual channels.</summary>
    public IReadOnlyList<int> Offsets => Instance.Offsets;

    /// <summary>Bytes per value (1 = 8 bit … 4 = 32 bit).</summary>
    public int ByteCount { get; }

    /// <summary>Largest DMX value at this resolution.</summary>
    public uint MaxValue => DmxValue.MaxFor(ByteCount);

    /// <summary>True when the channel has no DMX address (its value is the default, a manual value or set by relations).</summary>
    public bool IsVirtual => Instance.Offsets.Length == 0;

    /// <summary>Default DMX value (channel resolution).</summary>
    public uint DefaultValue { get; internal set; }

    /// <summary>Highlight DMX value, or null.</summary>
    public uint? HighlightValue { get; internal set; }

    /// <summary>Index of the geometry part the channel controls in <see cref="FixtureProfile.Geometry"/>, −1 when unresolved.</summary>
    public int PartIndex { get; }

    /// <summary>Index of the first function in <see cref="FixtureProfile.Functions"/>.</summary>
    public int FirstFunction { get; internal set; }

    /// <summary>Number of functions.</summary>
    public int FunctionCount { get; internal set; }

    /// <summary>Function active after power-up (global index), −1 when the channel has no functions.</summary>
    public int InitialFunction { get; internal set; } = -1;

    /// <summary>Snap behaviour of the logical channel.</summary>
    public SnapMode Snap { get; internal set; }

    /// <summary>Indices of the beams this channel affects (empty for position/control channels).</summary>
    public IReadOnlyList<int> Beams { get; internal set; } = Array.Empty<int>();

    /// <summary>Index of the axis this channel moves (Pan, Tilt, rotation, translation channels), or −1.</summary>
    public int Axis { get; internal set; } = -1;

    /// <summary>
    /// Pre-decoded DMX values of an 8-bit channel whose functions do not depend on a mode master (256 entries, shared
    /// by all instances of the same GDTF channel); null when the channel is decoded on the fly.
    /// </summary>
    internal DecodeEntry[]? Decode { get; set; }

    /// <inheritdoc />
    public override string ToString() => $"{Name} @ {InstanceName} [{Break}:{string.Join(",", Instance.Offsets)}]";
}

/// <summary>A wheel used by a profile with per-slot data pre-computed for evaluation.</summary>
public sealed class ProfileWheel
{
    internal ProfileWheel(int index, Wheel source, Vector3[] slotTransmission, int[] facets, bool[] open)
    {
        Index = index;
        Source = source;
        SlotTransmissionArray = slotTransmission;
        SlotFacetArray = facets;
        SlotOpenArray = open;
    }

    internal Vector3[] SlotTransmissionArray { get; }

    internal int[] SlotFacetArray { get; }

    internal bool[] SlotOpenArray { get; }

    /// <summary>Index in <see cref="FixtureProfile.Wheels"/>.</summary>
    public int Index { get; }

    /// <summary>The GDTF wheel.</summary>
    public Wheel Source { get; }

    /// <summary>Wheel name.</summary>
    public string Name => Source.Name;

    /// <summary>Number of slots (≥ 1).</summary>
    public int SlotCount => Math.Max(1, Source.Slots.Count);

    /// <summary>Per slot (0-based): linear Rec.2020 transmission of the slot's colour filter for a D65-white source.</summary>
    public IReadOnlyList<Vector3> SlotTransmission => SlotTransmissionArray;

    /// <summary>Per slot: number of prism facets (0 = not a prism).</summary>
    public IReadOnlyList<int> SlotFacets => SlotFacetArray;

    /// <summary>Per slot: true for open/empty slots.</summary>
    public IReadOnlyList<bool> SlotOpen => SlotOpenArray;

    /// <summary>Slot by 1-based position (rounded and wrapped onto the wheel).</summary>
    public WheelSlot? SlotAt(float position)
    {
        if (Source.Slots.Count == 0 || !float.IsFinite(position)) return null;
        return Source.Slots[WrapIndex((int)MathF.Round(position) - 1)];
    }

    internal int WrapIndex(int zeroBased)
    {
        int n = SlotCount;
        int i = zeroBased % n;
        return i < 0 ? i + n : i;
    }

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>An additive emitter of a profile (one LED colour), with its relative flux share.</summary>
public sealed class ProfileEmitter
{
    internal ProfileEmitter(int index, string key, string name, Vector2 chromaticity, float weight, Gdtf.Emitter? source)
    {
        Index = index;
        Key = key;
        Name = name;
        Chromaticity = chromaticity;
        Weight = weight;
        Source = source;
    }

    /// <summary>Index in <see cref="FixtureProfile.Emitters"/>.</summary>
    public int Index { get; }

    /// <summary>Identity key (GDTF emitter name or "code:…" for standard emitters).</summary>
    public string Key { get; }

    /// <summary>Display name.</summary>
    public string Name { get; }

    /// <summary>CIE 1931 chromaticity.</summary>
    public Vector2 Chromaticity { get; }

    /// <summary>Relative luminous flux share at full level (any scale; normalised per beam).</summary>
    public float Weight { get; }

    /// <summary>The GDTF emitter, when the colour comes from the file.</summary>
    public Gdtf.Emitter? Source { get; }

    /// <inheritdoc />
    public override string ToString() => $"{Name} ({Chromaticity.X:F3}, {Chromaticity.Y:F3}) w={Weight:G3}";
}

/// <summary>A Beam geometry instance of a profile with its photometric reference data.</summary>
public sealed class ProfileBeam
{
    internal ProfileBeam(int index, int partIndex, BeamGeometry geometry, string name)
    {
        Index = index;
        PartIndex = partIndex;
        Geometry = geometry;
        Name = name;
    }

    /// <summary>Index in <see cref="FixtureProfile.Beams"/>.</summary>
    public int Index { get; }

    /// <summary>Geometry part of the beam.</summary>
    public int PartIndex { get; }

    /// <summary>The GDTF beam.</summary>
    public BeamGeometry Geometry { get; }

    /// <summary>Instance name ("Beam", "Pixel 12", …).</summary>
    public string Name { get; }

    /// <summary>Luminous flux at full output with white light (all emitters at full), lm.</summary>
    public float ReferenceFlux { get; internal set; }

    /// <summary>Where <see cref="ReferenceFlux"/> comes from.</summary>
    public PhotometrySource PhotometrySource { get; internal set; }

    /// <summary>Full beam angle without zoom, rad.</summary>
    public float NativeBeamAngle { get; internal set; }

    /// <summary>Field angle / beam angle of the optics.</summary>
    public float FieldRatio { get; internal set; }

    /// <summary>Ω_eff as a function of the beam angle.</summary>
    public BeamSolidAngleTable SolidAngles { get; internal set; } = null!;

    /// <summary>Optical type for <see cref="Photometry.BeamDistribution"/>.</summary>
    public Photometry.BeamType BeamType { get; internal set; }

    /// <summary>CCT of the white source, K.</summary>
    public float SourceKelvin { get; internal set; }

    /// <summary>CIE XYZ of the white source with Y = 1.</summary>
    public Vector3 SourceXyz { get; internal set; } = new(0.9505f, 1f, 1.089f);

    /// <summary>Subtractive model of the white source; null when no CMY/CTO/CTB/CTC channel affects the beam.</summary>
    public SubtractiveColorTable? Subtractive { get; internal set; }

    /// <summary>Emitters of the beam's additive channels with their full-level flux (lm); empty for white-only sources.</summary>
    public PhotometryEmitter[] Emitters { get; internal set; } = Array.Empty<PhotometryEmitter>();

    /// <summary>Maps <see cref="FixtureProfile.Emitters"/> index → index in <see cref="Emitters"/> (−1 when absent).</summary>
    internal int[] EmitterSlot { get; set; } = Array.Empty<int>();

    /// <summary>
    /// Per <see cref="FixtureProfile.Emitters"/> index: CIE XYZ (in lumen) of the emitter at full level in this beam, zero
    /// for emitters the beam does not have. Mixing is the level-weighted sum (additive in XYZ).
    /// </summary>
    internal Vector3[] EmitterFullXyz { get; set; } = Array.Empty<Vector3>();

    /// <summary>
    /// True for LED engines that mix their colour electronically (emitters, no CMY flags): colour-temperature channels
    /// move the white point by re-balancing emitters instead of filtering, so their luminous cost is the engine's
    /// (<see cref="WhiteEfficiency"/>), not a filter's.
    /// </summary>
    public bool ElectronicColor { get; internal set; }

    /// <summary>Largest white output of the beam's emitters per colour temperature, null when unknown (efficiency 1).</summary>
    internal WhiteEfficiencyTable? WhiteTable { get; set; }

    /// <summary>
    /// Relative white output (0 … 1, 1 = the engine's brightest white) of the beam's emitters at <paramref name="kelvin"/>;
    /// 1 when the beam has no emitter data.
    /// </summary>
    public float WhiteEfficiency(float kelvin) => WhiteTable?.At(kelvin) ?? 1f;

    /// <summary>True when one of <see cref="OwnChannels"/> drives a wheel, prism, animation, effect, beam shaper or blade.</summary>
    internal bool OwnMechanics { get; set; }

    /// <summary>Channels affecting only this beam (or a subset of beams), evaluated per beam.</summary>
    internal int[] OwnChannels { get; set; } = Array.Empty<int>();

    /// <summary>Relation-master dimmer channels whose Multiply already scales this beam's followers (not multiplied again).</summary>
    internal int[] ExcludedDimmers { get; set; } = Array.Empty<int>();

    /// <summary>Peak intensity (cd) at full white output and the current beam angle (rad).</summary>
    public float PeakCandelaAt(float beamAngle) => ReferenceFlux / SolidAngles.SolidAngle(beamAngle);

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>Where a beam's reference output comes from.</summary>
public enum PhotometrySource
{
    /// <summary><c>_specs</c> maxLuminousIntensityCd (shared between the fixture's beams).</summary>
    SpecsCandela,

    /// <summary>The GDTF Beam's LuminousFlux (GDTF's default value on a multi-beam fixture is split between its beams).</summary>
    GdtfFlux,

    /// <summary><c>_specs</c> luminousFluxLm (shared between the fixture's beams), when the GDTF flux is missing or the default.</summary>
    SpecsFlux,

    /// <summary>Estimated from the beam's power and a typical efficacy of its lamp type.</summary>
    Estimated,
}

/// <summary>An Axis geometry instance of a profile and the channels that move it.</summary>
public sealed class ProfileAxis
{
    internal ProfileAxis(int index, int partIndex, string name)
    {
        Index = index;
        PartIndex = partIndex;
        Name = name;
    }

    /// <summary>Index in <see cref="FixtureProfile.Axes"/>.</summary>
    public int Index { get; }

    /// <summary>Geometry part of the axis.</summary>
    public int PartIndex { get; }

    /// <summary>Instance name ("Yoke", "Head", "Head 3" …).</summary>
    public string Name { get; }

    /// <summary>Channels moving this axis.</summary>
    public IReadOnlyList<int> Channels { get; internal set; } = Array.Empty<int>();

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>A pre-decoded DMX value: active function (relative to the channel's first), channel set, physical and control value.</summary>
internal readonly struct DecodeEntry
{
    public DecodeEntry(int function, int set, ChannelSetFlags flags, float physical, float target)
    {
        Function = (short)function;
        Set = (short)set;
        Flags = flags;
        Physical = physical;
        Target = target;
    }

    /// <summary>Function index relative to <see cref="ProfileChannel.FirstFunction"/>.</summary>
    public readonly short Function;

    /// <summary>Channel set index in the function, −1 when none.</summary>
    public readonly short Set;

    /// <summary>Flags of that channel set.</summary>
    public readonly ChannelSetFlags Flags;

    /// <summary>GDTF physical value.</summary>
    public readonly float Physical;

    /// <summary>Control value before dynamics (slot position for wheel selection).</summary>
    public readonly float Target;
}
