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

    /// <summary>
    /// Insertion 0 … 1: 0 when the mechanism is out of the beam (an open slot, or a range named "Open", "Off",
    /// "Removed" …), 1 when a slot is in; in between for animation systems and for ranges that move a single-slot
    /// mechanism gradually into the beam ("Open" → "Animation In").
    /// </summary>
    public float Insertion;

    /// <summary>Effect rate (effects wheels, built-in effects), Hz or the GDTF physical value.</summary>
    public float Rate;

    /// <summary>True when a channel set <see cref="Insertion"/> to a level this frame (ramp, cycle, animation-system insertion).</summary>
    internal bool InsertionSet;

    /// <summary>
    /// True when a range named "Open", "Off", "Removed" … of a channel that selects, indexes or spins the mechanism said
    /// it is out of the beam this frame. It takes the mechanism out unless another channel selected it (<see cref="InVote"/>).
    /// </summary>
    internal bool OutVote;

    /// <summary>
    /// True when a selection channel put a slot of the mechanism's wheel in place this frame. Such a selection wins over an
    /// "out" range of another channel addressing the same mechanism (files that give a second prism channel the first
    /// prism's attribute, with "No Effect" at DMX 0: ADJ Protege XS, Vizi Beam 12RX, LIGHT_SKY IP520).
    /// </summary>
    internal bool InVote;

    /// <summary>A mechanism that is not used.</summary>
    public static WheelState Unused => UnusedState;

    internal static readonly WheelState UnusedState = new() { Wheel = -1, Position = float.NaN, IsOpen = true, Insertion = 0f };
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
        Blades = blades == 0 ? Array.Empty<BladeState>() : new BladeState[blades];
        Color = LightColor.White;
        Iris = 1f;
        FocusDistance = float.NaN;
    }

    private static WheelState[] Filled(int n)
    {
        if (n == 0) return Array.Empty<WheelState>(); // shared: beams without a mechanism do not touch memory of their own
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

    /// <summary>
    /// True when <see cref="FieldAngle"/> is derived from the beam angle with the beam type's typical field ratio
    /// (<see cref="ProfileBeam.FieldRatioEstimated"/>) rather than from the file's field angle: an estimate, which a
    /// display should mark as such (it can exceed the file's FieldAngle when that is the top of the zoom range).
    /// </summary>
    public bool FieldAngleEstimated => Beam.FieldRatioEstimated;

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

    /// <summary>
    /// Elongation of the beam by an anamorphic beam shaper (long axis / short axis, ≥ 1; 1 = round): the lens of a beam
    /// shaper slot without an image, scaled by its insertion. <see cref="BeamAngle"/> and <see cref="FieldAngle"/> are the
    /// short axis; the long axis is <see cref="ShaperStretch"/> times wider, and <see cref="PeakCandela"/> is lower by the
    /// same factor (the flux is spread over the wider beam).
    /// </summary>
    public float ShaperStretch { get; internal set; } = 1f;

    /// <summary>Direction of the stretched (long) axis about the beam axis, rad (the beam shaper's indexing and rotation).</summary>
    public float ShaperStretchRotation { get; internal set; }

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
    internal readonly MotorRuntime[] Motors;
    internal readonly float[] EmitterLevels;
    internal readonly float[] OwnEmitterLevels;
    internal readonly BeamState SharedScratch;
    internal readonly float[] SpeedFactors = [1f, 1f, 1f, 1f, 1f, 1f];
    internal readonly EvaluationCache Cache = new();

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
        Motors = new MotorRuntime[profile.MotorCount];
        Axes = new AxisState[profile.Axes.Count];
        for (int i = 0; i < Axes.Length; i++) Axes[i].PartIndex = profile.Axes[i].PartIndex;
        EmitterLevels = new float[profile.Emitters.Count];
        OwnEmitterLevels = new float[profile.Emitters.Count];
        var counts = new int[7];
        var beams = new BeamState[profile.Beams.Count];
        for (int b = 0; b < beams.Length; b++)
        {
            var pb = profile.Beams[b];
            var c = FamilyCounts(profile, pb);
            for (int k = 0; k < counts.Length; k++) counts[k] = Math.Max(counts[k], c[k]);
            beams[b] = new BeamState(pb, c[0], c[1], c[2], c[3], c[4], c[5], c[6]);
        }
        BeamArray = beams;
        Beams = beams;
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

    /// <summary>The gel set with <see cref="SetGel"/> (CIE 1931 x, y and Y = transmission in percent), or null.</summary>
    public Candela.Core.SceneGraph.Components.CieColor? Gel { get; private set; }

    /// <summary>Per-channel (linear Rec.2020) transmission of <see cref="Gel"/>; (1, 1, 1) without a gel.</summary>
    internal Vector3 GelTransmission { get; private set; } = Vector3.One;

    /// <summary>
    /// Puts a gel in front of the fixture's white beams (MVR <c>&lt;Color&gt;</c> of a fixture, <c>FixtureComponent.Color</c>):
    /// a filter of chromaticity (x, y) passing Y % of the light, applied per channel in linear Rec.2020 like a colour-wheel
    /// slot (exact for a D65 source; a filter whose Y exceeds what its chromaticity allows is scaled down to a physical
    /// one). It colours only beams without a colour system of their own (<see cref="ProfileBeam.HasColorSystem"/>):
    /// conventional and white fixtures. Null, or a colour without a valid chromaticity, removes the gel; a Y of 0 or
    /// above 100 counts as 100 %. Takes effect at the next evaluation.
    /// </summary>
    public void SetGel(Candela.Core.SceneGraph.Components.CieColor? gel)
    {
        if (gel is { } g && float.IsFinite(g.X) && float.IsFinite(g.Y) && g.X > 0f && g.Y > 0f && g.X + g.Y < 1f)
        {
            float y = float.IsFinite(g.BigY) && g.BigY > 0f ? Math.Min(g.BigY, 100f) / 100f : 1f;
            Gel = g;
            GelTransmission = Filters.PhysicalChannelTransmission(LightColor.FromXy(g.X, g.Y), y);
        }
        else
        {
            Gel = null;
            GelTransmission = Vector3.One;
        }
    }

    /// <summary>Time of the last evaluation in seconds (NaN before the first).</summary>
    public double Time { get; internal set; }

    /// <summary>Incremented by every evaluation; compare with a stored value to detect changes.</summary>
    public long Version { get; internal set; }

    /// <summary>True when the state changes with time even without new DMX: strobes, rotations, shakes, moving motors.</summary>
    public bool IsAnimating { get; internal set; }

    /// <summary>
    /// For a caller that skips evaluating a state while it rests (not <see cref="IsAnimating"/>, DMX unchanged): moves
    /// the state's clock forward to <paramref name="time"/> (s) without evaluating. Nothing of a resting state depends on
    /// time, so this changes no output; it makes the next evaluation with motor dynamics start the motors from rest at
    /// <paramref name="time"/> instead of integrating over the whole pause (which would complete any move at once).
    /// Does nothing for an animating or never-evaluated state, or when <paramref name="time"/> is not later.
    /// </summary>
    public void AdvanceIdleClock(double time)
    {
        if (!IsAnimating && double.IsFinite(Time) && double.IsFinite(time) && time > Time) Time = time;
    }

    /// <summary>
    /// Continues the mechanics of <paramref name="previous"/>, a state of the same fixture under an earlier profile (the
    /// GDTF was re-resolved after a library change, a substitute was replaced by the real file, the mode changed): the
    /// clock and the motor and rotation state of every motorised channel found in both profiles — by channel name and
    /// geometry instance, else by attribute and instance, else (modes and files that name their geometry differently,
    /// e.g. "YOKE Std" / "YOKE Ext", or a stand-in replaced by the real GDTF) by attribute and its rank among the
    /// motorised channels of that attribute — so the next evaluation moves the heads, zooms and wheels on from where
    /// they physically are instead of jumping. Channels without a counterpart start where their DMX puts them.
    /// Call it before the first evaluation of this state.
    /// </summary>
    public void ContinueFrom(FixtureState previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        if (ReferenceEquals(previous, this) || !double.IsFinite(previous.Time)) return;
        var old = previous.Profile;
        var byName = new Dictionary<(string, string), int>();
        var byAttribute = new Dictionary<(string, string), int>();
        var byRank = new Dictionary<(string, int), int>();
        var rankOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var ch in old.Channels)
        {
            if (old.MotorSlot[ch.Index] < 0) continue;
            byName.TryAdd((ch.Name, ch.InstanceName), ch.Index);
            byAttribute.TryAdd((ch.Attribute, ch.InstanceName), ch.Index);
            int rank = rankOf.GetValueOrDefault(ch.Attribute);
            rankOf[ch.Attribute] = rank + 1;
            byRank[(ch.Attribute.ToUpperInvariant(), rank)] = ch.Index;
        }
        rankOf.Clear();
        foreach (var ch in Profile.Channels)
        {
            int slot = Profile.MotorSlot[ch.Index];
            if (slot < 0) continue;
            int rank = rankOf.GetValueOrDefault(ch.Attribute);
            rankOf[ch.Attribute] = rank + 1;
            if (!byName.TryGetValue((ch.Name, ch.InstanceName), out int match) &&
                !byAttribute.TryGetValue((ch.Attribute, ch.InstanceName), out match) &&
                !byRank.TryGetValue((ch.Attribute.ToUpperInvariant(), rank), out match)) continue;
            Motors[slot] = previous.Motors[old.MotorSlot[match]];
        }
        Time = previous.Time;
        IsAnimating = previous.IsAnimating;
    }

    /// <summary>Axis animations (one per <see cref="FixtureProfile.Axes"/> entry).</summary>
    public AxisState[] Axes { get; }

    /// <summary>Beam states (one per <see cref="FixtureProfile.Beams"/> entry).</summary>
    public IReadOnlyList<BeamState> Beams { get; }

    /// <summary>The beam states as an array (per-frame path).</summary>
    internal BeamState[] BeamArray { get; }

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

/// <summary>
/// Results of the costlier colour and photometry functions for the last inputs seen, so that the beams of a multi-cell
/// fixture (which usually share CTC/CTO/CMY, zoom and source) compute them once per frame. One per fixture state.
/// </summary>
internal sealed class EvaluationCache
{
    private float _whiteKelvin = float.NaN, _whiteDuv = float.NaN;
    private Vector3 _whiteXyz;

    private SubtractiveColorTable? _filterTable;
    private WhiteEfficiencyTable? _filterWhite;
    private float _filterSource = float.NaN, _filterC = float.NaN, _filterM = float.NaN, _filterY = float.NaN, _filterTarget = float.NaN;
    private bool _filterElectronic;
    private Vector3 _filter;

    private BeamSolidAngleTable? _omegaTable;
    private float _omegaAngle = float.NaN;
    private float _omega;

    /// <summary>XYZ (Y = 1) of colour temperature <paramref name="kelvin"/> offset by <paramref name="duv"/> from the Planckian locus.</summary>
    public Vector3 WhiteXyz(float kelvin, float duv)
    {
        if (kelvin != _whiteKelvin || duv != _whiteDuv)
        {
            _whiteXyz = ColorSpaces.XyToXyz(Cct.XyFromCct(kelvin, duv));
            _whiteKelvin = kelvin;
            _whiteDuv = duv;
        }
        return _whiteXyz;
    }

    /// <summary>
    /// Per-channel (linear Rec.2020) multiplier of CMY filters and a colour-temperature change to
    /// <paramref name="targetKelvin"/> (NaN = none) for a beam's source. <paramref name="electronic"/>: the change is an
    /// LED engine re-balancing its emitters (chromatic change of a conversion filter, luminous cost of the engine).
    /// </summary>
    public Vector3 Filter(ProfileBeam beam, SubtractiveColorTable table, float c, float m, float y, float targetKelvin, bool electronic)
    {
        if (ReferenceEquals(table, _filterTable) && ReferenceEquals(beam.WhiteTable, _filterWhite) && beam.SourceKelvin == _filterSource &&
            c == _filterC && m == _filterM && y == _filterY && electronic == _filterElectronic &&
            (targetKelvin == _filterTarget || (float.IsNaN(targetKelvin) && float.IsNaN(_filterTarget))))
            return _filter;
        Vector3 result;
        if (electronic)
        {
            var conversion = table.RgbTransmission(0f, 0f, 0f, targetKelvin);
            float filtered = ColorSpaces.LuminanceRec2020(table.SourceRgb * conversion);
            float engine = MathF.Min(1f, beam.WhiteEfficiency(targetKelvin) / MathF.Max(beam.WhiteEfficiency(beam.SourceKelvin), WhiteEfficiencyTable.Floor));
            if (filtered > 1e-4f) conversion *= engine / filtered;
            if (c > 0f || m > 0f || y > 0f) conversion *= table.RgbTransmission(c, m, y, float.NaN);
            result = conversion;
        }
        else
        {
            result = table.RgbTransmission(c, m, y, targetKelvin);
        }
        (_filterTable, _filterWhite, _filterSource, _filterC, _filterM, _filterY, _filterTarget, _filterElectronic, _filter) =
            (table, beam.WhiteTable, beam.SourceKelvin, c, m, y, targetKelvin, electronic, result);
        return result;
    }

    /// <summary>Effective solid angle (sr) of a beam's distribution at full beam angle <paramref name="beamAngle"/> (rad).</summary>
    public float SolidAngle(BeamSolidAngleTable table, float beamAngle)
    {
        if (!ReferenceEquals(table, _omegaTable) || beamAngle != _omegaAngle)
        {
            _omega = table.SolidAngle(beamAngle);
            _omegaTable = table;
            _omegaAngle = beamAngle;
        }
        return _omega;
    }
}

/// <summary>Per-channel evaluation state (internal; one per profile channel; 32 bytes so a frame touches little memory).</summary>
internal struct ChannelRuntime
{
    public uint Dmx;
    public int Function;
    public float Physical;
    public float Target;
    public float Value;
    public float Relation;
    public int SetIndex;
    public ChannelSetFlags SetFlags;
}

/// <summary>Motor state of a channel that moves (only those have one, see <see cref="FixtureProfile.MotorSlot"/>).</summary>
internal struct MotorRuntime
{
    /// <summary>Position motor (control units).</summary>
    public MotorState Motor;

    /// <summary>Continuous rotation: angle (deg) and speed (deg/s).</summary>
    public MotorState Spin;

    /// <summary>The quantity <see cref="Motor"/> currently carries.</summary>
    public AttributeKind Kind;
}
