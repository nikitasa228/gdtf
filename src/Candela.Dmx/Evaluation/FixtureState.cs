using System.Numerics;
using Candela.Photometry;

namespace Candela.Dmx;

/// <summary>Current value of one profile channel: raw DMX, active function, GDTF physical value and control value.</summary>
public readonly record struct ChannelValue
{
    /// <summary>Raw DMX value at the channel's resolution (after manual overrides).</summary>
    public uint Dmx { get; init; }

    /// <summary>Active function (index into <see cref="FixtureProfile.Functions"/>), −1 when none.</summary>
    public int Function { get; init; }

    /// <summary>GDTF physical value of the active function (degrees, Hz, K, 0 … 1 …) before motor dynamics.</summary>
    public float Physical { get; init; }

    /// <summary>
    /// Control value after motor dynamics: the physical value, except for wheel slot selection where it is the
    /// 1-based (fractional) slot position. Manual values (<c>FixtureComponent.ManualAttributes</c>) are given in this unit.
    /// </summary>
    public float Value { get; init; }
}

/// <summary>Animation of one Axis geometry part: rotations in radians (local axes), translation in metres.</summary>
public struct AxisState
{
    /// <summary>Geometry part (<see cref="FixtureProfile.Geometry"/>) that this state animates.</summary>
    public int PartIndex;

    /// <summary>Pan angle about the axis' local Z (right-handed), rad; includes continuous rotation and the invert flag.</summary>
    public float Pan;

    /// <summary>Tilt angle about the axis' local X (right-handed), rad; includes continuous rotation and the invert flag.</summary>
    public float Tilt;

    /// <summary>Additional rotations from <c>Rot_X/Y/Z</c>, rad.</summary>
    public Vector3 Rotation;

    /// <summary>Translation from <c>XYZ_X/Y/Z</c>, m.</summary>
    public Vector3 Translation;

    /// <summary>True when pan runs inverted (a pan/tilt mode channel says so).</summary>
    public bool PanInverted;

    /// <summary>True when tilt runs inverted.</summary>
    public bool TiltInverted;

    /// <summary>
    /// The part's animation matrix (row-vector convention, applied before its rest transform as in
    /// <c>FixtureGeometry.ComputeWorldTransforms</c>): rotation about X (tilt), Y, then Z (pan), then translation.
    /// </summary>
    public readonly Matrix4x4 Transform =>
        Matrix4x4.CreateRotationX(Tilt + Rotation.X) * Matrix4x4.CreateRotationY(Rotation.Y) *
        Matrix4x4.CreateRotationZ(Pan + Rotation.Z) * Matrix4x4.CreateTranslation(Translation);
}

/// <summary>
/// State of one wheel-like mechanism in a beam (colour wheel, gobo wheel, prism, animation wheel, effects wheel,
/// beam shaper). Positions are 1-based slot positions that may be fractional while the wheel moves between slots or
/// sits on a split position; angles are radians.
/// </summary>
public struct WheelState
{
    /// <summary>Index into <see cref="FixtureProfile.Wheels"/>, −1 when this mechanism is not in use.</summary>
    public int Wheel;

    /// <summary>1-based slot position (fractional between slots; wraps around the wheel), NaN when unknown.</summary>
    public float Position;

    /// <summary>Slot at <see cref="Position"/> (1-based, wrapped), 0 when unknown.</summary>
    public int Slot;

    /// <summary>Slot after <see cref="Slot"/> (1-based, wrapped) for split positions.</summary>
    public int NextSlot;

    /// <summary>0 … 1: share of <see cref="NextSlot"/> in the beam (0 = fully on <see cref="Slot"/>).</summary>
    public float Blend;

    /// <summary>Angle of the whole wheel (index or spin), rad.</summary>
    public float WheelAngle;

    /// <summary>Rotation of the slot item about the beam axis (gobo/prism indexing or continuous rotation), rad.</summary>
    public float Rotation;

    /// <summary>Current rotation speed of the slot item, rad/s (0 when indexed).</summary>
    public float RotationSpeed;

    /// <summary>Shake frequency, Hz (0 = no shake).</summary>
    public float ShakeFrequency;

    /// <summary>Prism facets of the current slot (0 = no prism in the beam).</summary>
    public int Facets;

    /// <summary>True when the current slot is open (no filter, gobo or prism).</summary>
    public bool IsOpen;

    /// <summary>Insertion 0 … 1 (animation systems; 1 for wheels).</summary>
    public float Insertion;

    /// <summary>Effect rate (effects wheels, built-in effects), Hz or the GDTF physical value.</summary>
    public float Rate;

    /// <summary>A mechanism that is not used.</summary>
    public static WheelState Unused => new() { Wheel = -1, Position = float.NaN, IsOpen = true, Insertion = 0f };
}

/// <summary>One framing blade (shutter) of a beam.</summary>
public struct BladeState
{
    /// <summary>Insertion of side A, 0 (out) … 1 (fully in).</summary>
    public float A;

    /// <summary>Insertion of side B, 0 … 1.</summary>
    public float B;

    /// <summary>Blade rotation, rad.</summary>
    public float Rotation;

    /// <summary>Edge softness of side A, 0 … 1.</summary>
    public float SoftA;

    /// <summary>Edge softness of side B, 0 … 1.</summary>
    public float SoftB;
}

/// <summary>Outputs of non-light devices (hazers, fog machines, fans, flame projectors), normalised 0 … 1.</summary>
public struct DeviceOutputs
{
    /// <summary>Fog output, 0 … 1.</summary>
    public float Fog;

    /// <summary>Haze output, 0 … 1.</summary>
    public float Haze;

    /// <summary>Fan/blower speed, 0 … 1 (atmospheric devices only; cooling fans of lights are not reported).</summary>
    public float FanSpeed;

    /// <summary>Fan direction: +1 forward, −1 reverse.</summary>
    public float FanDirection;

    /// <summary>Flame output, 0 … 1.</summary>
    public float Flame;

    /// <summary>Flame height in metres, NaN when the device does not specify it.</summary>
    public float FlameHeight;

    /// <summary>True when any output is above zero.</summary>
    public readonly bool Any => Fog > 0f || Haze > 0f || FanSpeed > 0f || Flame > 0f;
}

/// <summary>
/// Physical state of one beam: what the renderer draws and the light meter integrates. Intensities and colours are
/// photometric (cd, lm, Rec.2020 chromaticity with luminance 1); angles are radians.
/// </summary>
public sealed class BeamState
{
    internal BeamState(ProfileBeam beam, int colorWheels, int gobos, int prisms, int animations, int effects, int blades, int shapers)
    {
        Beam = beam;
        ColorWheels = Filled(colorWheels);
        Gobos = Filled(gobos);
        Prisms = Filled(prisms);
        AnimationWheels = Filled(animations);
        Effects = Filled(effects);
        BeamShapers = Filled(shapers);
        Blades = new BladeState[blades];
        Color = LightColor.White;
        Iris = 1f;
        FocusDistance = float.NaN;
    }

    private static WheelState[] Filled(int n)
    {
        var a = new WheelState[n];
        Array.Fill(a, WheelState.Unused);
        return a;
    }

    /// <summary>The beam definition (geometry part, photometric reference).</summary>
    public ProfileBeam Beam { get; }

    /// <summary>Geometry part of the beam (light leaves its origin along local −Z).</summary>
    public int PartIndex => Beam.PartIndex;

    /// <summary>Instance name ("Beam", "Pixel 7" …).</summary>
    public string Name => Beam.Name;

    /// <summary>Dimmer × relations × dimmer curve, 0 … 1.</summary>
    public float Dimmer { get; internal set; }

    /// <summary>Shutter and strobe at the evaluation time, 0 (closed) … 1 (open).</summary>
    public float Shutter { get; internal set; }

    /// <summary>Overall intensity 0 … 1: <see cref="Dimmer"/> × <see cref="Shutter"/>.</summary>
    public float Intensity { get; internal set; }

    /// <summary>
    /// Luminous output of the current colour relative to full white, 0 … 1: emitter mix level, CMY/CTO and colour
    /// wheel transmission.
    /// </summary>
    public float ColorTransmission { get; internal set; }

    /// <summary>
    /// On-axis luminous intensity, cd: reference peak at the current zoom × <see cref="Intensity"/> ×
    /// <see cref="ColorTransmission"/>. Gobo, iris, frost, prism and blades are applied by the renderer's cookie.
    /// </summary>
    public float PeakCandela { get; internal set; }

    /// <summary>Luminous flux leaving the beam, lm (same factors as <see cref="PeakCandela"/>).</summary>
    public float Flux { get; internal set; }

    /// <summary>Light colour (chromaticity only; magnitude in <see cref="PeakCandela"/>).</summary>
    public LightColor Color { get; internal set; }

    /// <summary>Full beam angle (50 % intensity), rad.</summary>
    public float BeamAngle { get; internal set; }

    /// <summary>Full field angle (10 % intensity), rad.</summary>
    public float FieldAngle { get; internal set; }

    /// <summary>Optical beam type for the intensity distribution.</summary>
    public Photometry.BeamType BeamType => Beam.BeamType;

    /// <summary>Focus position 0 … 1 within the fixture's focus range.</summary>
    public float Focus { get; internal set; }

    /// <summary>Focus distance in metres, NaN when the fixture has no distance-calibrated focus.</summary>
    public float FocusDistance { get; internal set; }

    /// <summary>Iris opening 0 … 1 (1 = open), including iris pulses.</summary>
    public float Iris { get; internal set; }

    /// <summary>Frost/diffusion 0 … 1 (all frost filters combined: 1 − Π(1 − fᵢ)), including frost pulses.</summary>
    public float Frost { get; internal set; }

    /// <summary>True while a strobe or pulse effect modulates the shutter.</summary>
    public bool Strobing { get; internal set; }

    /// <summary>Strobe frequency, Hz (0 when not strobing).</summary>
    public float StrobeFrequency { get; internal set; }

    /// <summary>Colour wheels (index = wheel number − 1: Color1, Color2 …).</summary>
    public WheelState[] ColorWheels { get; }

    /// <summary>Gobo wheels (index = wheel number − 1).</summary>
    public WheelState[] Gobos { get; }

    /// <summary>Prisms (index = prism number − 1); <see cref="WheelState.Facets"/> &gt; 0 when a prism is in the beam.</summary>
    public WheelState[] Prisms { get; }

    /// <summary>Animation wheels and animation systems (index = number − 1).</summary>
    public WheelState[] AnimationWheels { get; }

    /// <summary>Effects wheels / built-in effects (index = number − 1).</summary>
    public WheelState[] Effects { get; }

    /// <summary>Beam shapers (index 0).</summary>
    public WheelState[] BeamShapers { get; }

    /// <summary>Framing blades (index = blade number − 1).</summary>
    public BladeState[] Blades { get; }

    /// <summary>Rotation of the whole framing module, rad.</summary>
    public float ShaperRotation { get; internal set; }

    /// <summary>Number of prism facets currently in the beam (product of all inserted prisms; 0 = none).</summary>
    public int PrismFacets
    {
        get
        {
            int n = 0;
            foreach (var p in Prisms)
                if (p.Facets > 0) n = n == 0 ? p.Facets : n * p.Facets;
            return n;
        }
    }

    /// <inheritdoc />
    public override string ToString() => $"{Name}: {PeakCandela:F0} cd, I={Intensity:F2}, {BeamAngle * 180f / MathF.PI:F1}°";
}

/// <summary>
/// The physical state of one fixture at one moment, produced by <see cref="FixtureEvaluator"/>: axis rotations for the
/// geometry tree, one <see cref="BeamState"/> per beam, device outputs, and per-channel DMX/physical values for the
/// properties panel. Allocated once per fixture (and per profile change) and overwritten every frame without
/// allocating. Not thread-safe: one thread evaluates and reads it at a time.
/// </summary>
public sealed class FixtureState
{
    internal readonly ChannelRuntime[] Runtime;
    internal readonly float[] EmitterLevels;
    internal readonly float[] OwnEmitterLevels;
    internal readonly float[] BeamLevels;
    internal readonly BeamState SharedScratch;
    internal readonly float[] SpeedFactors = [1f, 1f, 1f, 1f, 1f, 1f];

    /// <summary>Creates the state of a fixture of <paramref name="profile"/>.</summary>
    /// <param name="profile">The compiled profile.</param>
    /// <param name="seed">Seed of the fixture's random effects (random strobe, random wheel), e.g. a hash of the entity id.</param>
    public FixtureState(FixtureProfile profile, uint seed = 0)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Profile = profile;
        Seed = seed;
        Runtime = new ChannelRuntime[profile.Channels.Count];
        for (int i = 0; i < Runtime.Length; i++) Runtime[i].Function = -1;
        Axes = new AxisState[profile.Axes.Count];
        for (int i = 0; i < Axes.Length; i++) Axes[i].PartIndex = profile.Axes[i].PartIndex;
        EmitterLevels = new float[profile.Emitters.Count];
        OwnEmitterLevels = new float[profile.Emitters.Count];
        int maxEmitters = 0;
        var counts = new int[7];
        var beams = new BeamState[profile.Beams.Count];
        for (int b = 0; b < beams.Length; b++)
        {
            var pb = profile.Beams[b];
            maxEmitters = Math.Max(maxEmitters, pb.Emitters.Length);
            var c = FamilyCounts(profile, pb);
            for (int k = 0; k < counts.Length; k++) counts[k] = Math.Max(counts[k], c[k]);
            beams[b] = new BeamState(pb, c[0], c[1], c[2], c[3], c[4], c[5], c[6]);
        }
        Beams = beams;
        BeamLevels = new float[maxEmitters];
        SharedScratch = new BeamState(profile.Beams.Count > 0 ? profile.Beams[0] : DummyBeam, counts[0], counts[1], counts[2], counts[3],
            counts[4], counts[5], counts[6]);
        Outputs = new DeviceOutputs { FanDirection = 1f, FlameHeight = float.NaN };
        Time = double.NaN;
    }

    private static readonly ProfileBeam DummyBeam = new(0, -1, new Gdtf.BeamGeometry(), "");

    /// <summary>Highest wheel/blade numbers used by a beam's channels: colour, gobo, prism, animation, effects, blades, shapers.</summary>
    private static int[] FamilyCounts(FixtureProfile profile, ProfileBeam beam)
    {
        var c = new int[7];
        void Scan(int channel)
        {
            foreach (var f in profile.FunctionsOf(channel))
            {
                int n = Math.Clamp(f.Class.Number, 1, 16);
                switch (f.Class.Kind)
                {
                    case AttributeKind.BladeA or AttributeKind.BladeB or AttributeKind.BladeRotation or AttributeKind.BladeSoft:
                        c[5] = Math.Max(c[5], n);
                        continue;
                }
                int slot = f.Class.Wheel switch
                {
                    WheelFamily.Color => 0,
                    WheelFamily.Gobo => 1,
                    WheelFamily.Prism => 2,
                    WheelFamily.Animation or WheelFamily.AnimationSystem => 3,
                    WheelFamily.Effects => 4,
                    WheelFamily.BeamShaper => 6,
                    _ => -1,
                };
                if (slot >= 0) c[slot] = Math.Max(c[slot], slot == 6 ? 1 : n);
            }
        }
        foreach (int ch in profile.SharedBeamChannels) Scan(ch);
        foreach (int ch in beam.OwnChannels) Scan(ch);
        return c;
    }

    /// <summary>The fixture's compiled profile.</summary>
    public FixtureProfile Profile { get; }

    /// <summary>Seed for random effects, so that identical fixtures do not flash in lockstep.</summary>
    public uint Seed { get; set; }

    /// <summary>Time of the last evaluation in seconds (NaN before the first).</summary>
    public double Time { get; internal set; }

    /// <summary>Incremented by every evaluation; compare with a stored value to detect changes.</summary>
    public long Version { get; internal set; }

    /// <summary>True when the state changes with time even without new DMX: strobes, rotations, shakes, moving motors.</summary>
    public bool IsAnimating { get; internal set; }

    /// <summary>Axis animations (one per <see cref="FixtureProfile.Axes"/> entry).</summary>
    public AxisState[] Axes { get; }

    /// <summary>Beam states (one per <see cref="FixtureProfile.Beams"/> entry).</summary>
    public IReadOnlyList<BeamState> Beams { get; }

    /// <summary>Outputs of fog/haze/fan/flame devices.</summary>
    public DeviceOutputs Outputs { get; internal set; }

    /// <summary>Value of channel <paramref name="channel"/>.</summary>
    public ChannelValue GetChannel(int channel)
    {
        ref readonly var r = ref Runtime[channel];
        return new ChannelValue { Dmx = r.Dmx, Function = r.Function, Physical = r.Physical, Value = r.Value };
    }

    /// <summary>Number of channels.</summary>
    public int ChannelCount => Runtime.Length;

    /// <summary>
    /// Current control value of <paramref name="attribute"/> (physical value; slot position for wheel selection) on the
    /// first channel instance whose active function has that attribute, optionally restricted to an instance name.
    /// </summary>
    public bool TryGetAttribute(string attribute, out float value, string? instance = null)
    {
        foreach (int c in Profile.FindChannels(attribute))
        {
            if (instance is not null && !string.Equals(Profile.Channels[c].InstanceName, instance, StringComparison.OrdinalIgnoreCase)) continue;
            ref readonly var r = ref Runtime[c];
            if (r.Function >= 0 && string.Equals(Profile.Functions[r.Function].Attribute, attribute, StringComparison.OrdinalIgnoreCase))
            {
                value = r.Value;
                return true;
            }
        }
        value = float.NaN;
        return false;
    }

    /// <summary>
    /// Fills <paramref name="destination"/> with attribute → control value of every active function (instanced channels
    /// as <c>"Attribute@Instance"</c>), for the properties panel. Allocates; not for per-frame use.
    /// </summary>
    public void CopyAttributes(IDictionary<string, float> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        for (int c = 0; c < Runtime.Length; c++)
        {
            ref readonly var r = ref Runtime[c];
            if (r.Function < 0) continue;
            var ch = Profile.Channels[c];
            string a = Profile.Functions[r.Function].Attribute;
            string key = ch.Instance.References.Count > 0 ? $"{a}@{ch.InstanceName}" : a;
            destination[key] = r.Value;
        }
    }

    /// <summary>
    /// Writes one animation matrix per geometry part (identity for parts that do not move) for
    /// <c>FixtureGeometry.ComputeWorldTransforms</c>.
    /// </summary>
    public void WriteAnimation(Span<Matrix4x4> perPart)
    {
        int n = Profile.Geometry.Parts.Count;
        if (perPart.Length < n) throw new ArgumentException($"Need {n} matrices.", nameof(perPart));
        perPart[..n].Fill(Matrix4x4.Identity);
        foreach (ref readonly var a in Axes.AsSpan())
            if ((uint)a.PartIndex < (uint)n) perPart[a.PartIndex] = a.Transform;
    }
}

/// <summary>Per-channel evaluation state (internal; one per profile channel).</summary>
internal struct ChannelRuntime
{
    public uint Dmx;
    public int Function;
    public float Physical;
    public float Target;
    public float Value;
    public float Relation;
    public int SetIndex;
    public MotorState Motor;
    public MotorState Spin;
    public AttributeKind MotorKind;
}
