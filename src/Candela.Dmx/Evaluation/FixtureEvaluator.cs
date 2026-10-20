using System.Numerics;
using System.Runtime.CompilerServices;
using Candela.Photometry;

namespace Candela.Dmx;

/// <summary>
/// Turns DMX into the physical state of a fixture (see <see cref="FixtureState"/>): selects every channel's active
/// function by DMX value and mode-master ranges, maps DMX → physical (linear between PhysicalFrom and PhysicalTo, DMX
/// profile curves, channel sets with their own physical ranges), applies manual overrides and relations, moves
/// motorised attributes with <see cref="MotorDynamics"/>, integrates continuous rotations, evaluates strobes at the
/// given time and finally builds axis rotations, beam photometry/colour/optics and device outputs.
/// </summary>
/// <remarks>
/// The per-frame path (<see cref="Evaluate(FixtureState, FixtureDmxBuffer, double, AttributeOverrides?, bool)"/>) does
/// not allocate. Channels that affect every beam are evaluated once and copied into each beam, so multi-pixel fixtures
/// cost little more than their own pixel channels.
/// </remarks>
public static class FixtureEvaluator
{
    private const float DegToRad = MathF.PI / 180f;

    /// <summary>Widest field angle of a beam (179°, rad): a beam lights at most a hemisphere.</summary>
    private const float MaxFieldAngle = 179f * DegToRad;

    /// <summary>
    /// Evaluates a fixture once (no motor dynamics; continuous rotations are integrated as if their speed had been
    /// constant since time 0) and returns a new state. Convenient for tests and tools; allocates.
    /// </summary>
    /// <param name="profile">Compiled profile.</param>
    /// <param name="dmx">DMX input (from <see cref="FixtureDmxBuffer"/>, <see cref="DmxEncoder"/> or a universe).</param>
    /// <param name="time">Time in seconds (strobes, rotations).</param>
    /// <param name="overrides">Optional manual values: attribute name (optionally <c>"Attribute@Instance"</c>) → control value.</param>
    public static FixtureState Evaluate(FixtureProfile profile, FixtureDmxBuffer dmx, double time = 0,
        IEnumerable<KeyValuePair<string, float>>? overrides = null)
    {
        var state = new FixtureState(profile);
        Evaluate(state, dmx, time, overrides is null ? null : AttributeOverrides.Compile(profile, overrides));
        return state;
    }

    /// <summary>Evaluates a fixture whose DMX is given as one byte array per break (slot 1 at index 0, profile break order).</summary>
    public static FixtureState Evaluate(FixtureProfile profile, IReadOnlyList<byte[]> breaks, double time = 0,
        IEnumerable<KeyValuePair<string, float>>? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(breaks);
        var buffer = new FixtureDmxBuffer(profile);
        for (int i = 0; i < Math.Min(breaks.Count, buffer.BreakCount); i++)
        {
            var dest = buffer.GetBreak(i);
            var src = breaks[i];
            int n = Math.Min(dest.Length, src.Length);
            src.AsSpan(0, n).CopyTo(dest);
        }
        return Evaluate(profile, buffer, time, overrides);
    }

    /// <summary>
    /// Evaluates <paramref name="state"/> in place for DMX <paramref name="dmx"/> at <paramref name="time"/> seconds.
    /// With <paramref name="applyDynamics"/>, motorised attributes move toward their DMX targets at their physical speed
    /// since the previous evaluation; otherwise they jump. Allocation-free. A caller that skips resting states calls
    /// <see cref="FixtureState.AdvanceIdleClock"/> with the previous frame's time first (as <see cref="DmxEngine"/> does).
    /// </summary>
    public static void Evaluate(FixtureState state, FixtureDmxBuffer dmx, double time, AttributeOverrides? overrides = null,
        bool applyDynamics = false)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(dmx);
        var p = state.Profile;
        if (!ReferenceEquals(dmx.Profile, p)) throw new ArgumentException("The DMX buffer belongs to another profile.", nameof(dmx));
        if (overrides is not null && !ReferenceEquals(overrides.Profile, p))
            throw new ArgumentException("The overrides belong to another profile.", nameof(overrides));
        if (!double.IsFinite(time)) time = 0;

        bool first = double.IsNaN(state.Time);
        double elapsed = first ? 0 : time - state.Time;
        bool restart = first || elapsed < 0;
        double rotationDt = restart ? Math.Max(time, 0) : elapsed;
        double motorDt = applyDynamics && !restart ? Math.Min(elapsed, 60.0) : 0;

        var fns = p.FunctionArray;
        var channels = p.ChannelArray;
        var rt = state.Runtime;
        bool animating = false;

        // 1–2: raw values (manual values and the mode masters they need replace DMX first, so channels that depend on an
        // overridden master are decoded against it) and active functions
        dmx.ReadAll(rt);
        overrides?.ApplyDmx(rt);
        for (int c = 0; c < rt.Length; c++) Select(channels[c], fns, rt, c);

        // 3: manual overrides take their exact function and control value
        overrides?.Apply(rt);

        // 4: relations (virtual dimmers, group masters, zone overrides); followers come after their masters, so a
        // master's own relations (grand master → pixel dimmer → pixel colour) are part of its level
        var relations = p.RelationArray;
        foreach (int c in p.FollowerChannels)
        {
            ref var follower = ref rt[c];
            if (follower.Function < 0) continue;
            float overrideLevel = float.NaN;
            foreach (int ri in p.FollowerRelations[c])
            {
                var r = relations[ri];
                if (follower.Function != r.FollowerFunction) continue;
                ref var m = ref rt[r.Master];
                float masterLevel = m.Function >= 0 ? Level(fns[m.Function], m.Target) * m.Relation : m.Relation;
                if (r.Type == Gdtf.RelationType.Multiply) follower.Relation *= masterLevel;
                else if (m.Dmx != channels[r.Master].DefaultValue) overrideLevel = masterLevel; // a master in use (away from its default)
            }
            if (!float.IsNaN(overrideLevel))
            {
                // Override: the master's level (with its own relations) replaces the follower's level and its multipliers,
                // whatever order the file lists the relations in
                follower.Target = overrideLevel * fns[follower.Function].LevelScale;
                follower.Relation = 1f;
            }
        }

        // 5: fixture-wide modifiers
        var curve = DimmerCurve.Linear;
        bool invertPan = false, invertTilt = false;
        var speed = state.SpeedFactors;
        speed.AsSpan().Fill(1f);
        foreach (int c in p.ModifierChannels)
        {
            ref var r = ref rt[c];
            if (r.Function < 0) continue;
            var f = fns[r.Function];
            var set = r.SetIndex >= 0 ? f.SetArray[r.SetIndex] : null;
            if (set is not null)
            {
                invertPan |= (set.Flags & ChannelSetFlags.InvertPan) != 0;
                invertTilt |= (set.Flags & ChannelSetFlags.InvertTilt) != 0;
            }
            switch (f.Class.Kind)
            {
                case AttributeKind.DimmerCurve when set is not null:
                    curve = set.DimmerCurve;
                    break;
                case AttributeKind.MotionSpeed:
                    {
                        float factor = SpeedFactor(f, r, set);
                        int g = f.SpeedGroup;
                        if (g < 0) speed.AsSpan().Fill(factor);
                        else speed[g] = factor;
                        break;
                    }
            }
        }

        // 6: dynamics and rotations
        var motorSlot = p.MotorSlot;
        var motors = state.Motors;
        for (int c = 0; c < rt.Length; c++)
        {
            ref var r = ref rt[c];
            int slot = motorSlot[c];
            if (r.Function < 0 || slot < 0)
            {
                r.Value = r.Target; // electronic: follows DMX
                continue;
            }
            ref var m = ref motors[slot];
            var f = fns[r.Function];
            var qty = f.MotorQuantity;
            if (applyDynamics && f.Motion == MotionKind.Position)
            {
                if (m.Kind != qty) ContinueMotor(ref m, r.Target, f, p, qty);
                MotorDynamics.Step(ref m.Motor, r.Target, motorDt, f.LimitsRef, speed[f.MotionGroup]);
                r.Value = (float)m.Motor.Position;
                animating |= m.Motor.IsMoving;
            }
            else
            {
                r.Value = r.Target;
                m.Motor.Position = r.Target;
                m.Motor.Velocity = 0;
                m.Motor.Initialized = true;
            }
            m.Kind = qty;

            if (f.IsRotation)
            {
                float spinSpeed = (r.SetFlags & ChannelSetFlags.NoFunction) != 0 ? 0f : SpinSpeed(f, r.Physical);
                if (restart)
                {
                    m.Spin.Initialized = true;
                    m.Spin.Velocity = spinSpeed;
                    m.Spin.Position = spinSpeed * rotationDt;
                }
                else
                {
                    MotorDynamics.StepRotation(ref m.Spin, spinSpeed, rotationDt, applyDynamics ? f.SpinAcceleration : double.PositiveInfinity);
                }
                animating |= m.Spin.Velocity != 0;
            }
            else if (m.Spin.Velocity != 0)
            {
                m.Spin.Velocity = 0;
            }
        }

        // 7: axes
        var axisChannels = p.AxisChannels;
        for (int a = 0; a < state.Axes.Length; a++)
        {
            float pan = 0, tilt = 0;
            Vector3 rot = default, trans = default;
            foreach (int c in axisChannels[a])
            {
                ref var r = ref rt[c];
                if (r.Function < 0) continue;
                var f = fns[r.Function];
                int n = Math.Clamp(f.Class.Number, 1, 3) - 1;
                switch (f.Class.Kind)
                {
                    case AttributeKind.Pan: pan += r.Value; break;
                    case AttributeKind.Tilt: tilt += r.Value; break;
                    case AttributeKind.PanRotate: pan += Turn(motors[motorSlot[c]].Spin.Position); break;
                    case AttributeKind.TiltRotate: tilt += Turn(motors[motorSlot[c]].Spin.Position); break;
                    case AttributeKind.Rotation: SetComponent(ref rot, n, GetComponent(rot, n) + r.Value * DegToRad); break;
                    case AttributeKind.Translation: SetComponent(ref trans, n, GetComponent(trans, n) + r.Value); break;
                }
            }
            ref var ax = ref state.Axes[a];
            ax.PanInverted = invertPan;
            ax.TiltInverted = invertTilt;
            ax.Pan = Finite(pan * DegToRad * (invertPan ? -1f : 1f));
            ax.Tilt = Finite(tilt * DegToRad * (invertTilt ? -1f : 1f));
            ax.Rotation = rot;
            ax.Translation = trans;
        }

        // 8: beams
        if (p.BeamArray.Length > 0) EvaluateBeams(state, p, fns, rt, time, curve, ref animating);

        // 9: device outputs
        if (p.OutputChannels.Length > 0) state.Outputs = Outputs(p, fns, rt);

        state.Time = time;
        state.Version++;
        state.IsAnimating = animating;
    }

    /// <summary>Step 8: every beam's intensity, colour, photometry, optics and mechanics.</summary>
    private static void EvaluateBeams(FixtureState state, FixtureProfile p, ProfileFunction[] fns, ChannelRuntime[] rt, double time, DimmerCurve curve,
        ref bool animating)
    {
        var beams = p.BeamArray;
        // a single beam (most spots and washes) takes the shared channels directly; several beams start from a scratch copy
        bool single = beams.Length == 1;
        var shared = single ? state.BeamArray[0] : state.SharedScratch;
        var acc = BeamAcc.Create();
        ResetWheels(shared);
        Array.Clear(state.EmitterLevels);
        var ctx = new ApplyContext(p, fns, rt, state.Motors, time, state.Seed, state.Cache, state.GelTransmission);
        foreach (int c in p.SharedBeamChannels) Apply(ref acc, shared, state.EmitterLevels, c, in ctx, ref animating);
        if (single)
        {
            var pb = beams[0];
            ApplyOwn(ref acc, shared, state.EmitterLevels, pb.OwnChannels, in ctx, ref animating);
            FinalizeAllWheels(p, shared);
            Finalize(pb, shared, ref acc, state.EmitterLevels, curve, in ctx, ref animating);
            return;
        }
        var ownLevels = state.OwnEmitterLevels;
        var beamStates = state.BeamArray;
        bool sharedFinalized = false;
        for (int b = 0; b < beams.Length; b++)
        {
            var pb = beams[b];
            var bs = beamStates[b];
            var beamAcc = acc;
            state.EmitterLevels.AsSpan().CopyTo(ownLevels);
            if (pb.OwnMechanics)
            {
                // the beam has its own wheels, prisms or blades: start from the shared ones, then resolve
                CopyWheels(shared, bs);
                ApplyOwn(ref beamAcc, bs, ownLevels, pb.OwnChannels, in ctx, ref animating);
                FinalizeAllWheels(p, bs);
            }
            else
            {
                // the mechanics are the fixture's: resolve them once and copy the result into every beam
                if (!sharedFinalized)
                {
                    FinalizeAllWheels(p, shared);
                    sharedFinalized = true;
                }
                ApplyOwn(ref beamAcc, bs, ownLevels, pb.OwnChannels, in ctx, ref animating);
                CopyWheels(shared, bs);
            }
            Finalize(pb, bs, ref beamAcc, ownLevels, curve, in ctx, ref animating);
        }
    }

    // ------------------------------------------------------------------ channel decoding

    private static void Select(ProfileChannel ch, ProfileFunction[] fns, ChannelRuntime[] rt, int c)
    {
        ref var r = ref rt[c];
        r.Relation = 1f;
        uint v = r.Dmx;
        if (ch.Decode is { } table)
        {
            ref readonly var e = ref table[Math.Min(v, 255u)];
            r.Function = ch.FirstFunction + e.Function;
            r.SetIndex = e.Set;
            r.SetFlags = e.Flags;
            r.Physical = e.Physical;
            r.Target = e.Target;
            return;
        }
        int best = SelectFunction(ch, fns, rt, v);
        r.Function = best;
        if (best < 0)
        {
            r.Physical = 0f;
            r.Target = 0f;
            r.SetIndex = -1;
            r.SetFlags = ChannelSetFlags.None;
            return;
        }
        var fn = fns[best];
        r.SetIndex = fn.SetIndexAt(v);
        r.SetFlags = fn.FlagsOf(r.SetIndex);
        r.Physical = PhysicalAt(fn, v, r.SetIndex);
        r.Target = fn.UsesSlotPosition ? fn.SlotPositionAt(v) : r.Physical;
    }

    /// <summary>
    /// The active function of a channel at DMX value <paramref name="v"/>: the first function (in GDTF order) whose
    /// mode-master condition holds and whose range contains the value; else the available function starting closest
    /// below the value; else the first available one; else the initial function. Mode masters are read from
    /// <paramref name="rt"/> (null: no conditions are checked). −1 when the channel has no functions.
    /// </summary>
    internal static int SelectFunction(ProfileChannel ch, ProfileFunction[] fns, ChannelRuntime[]? rt, uint v)
    {
        int end = ch.FirstFunction + ch.FunctionCount;
        int fallback = -1, available = -1;
        uint fallbackFrom = 0;
        for (int k = ch.FirstFunction; k < end; k++)
        {
            var f = fns[k];
            int master = f.ModeMaster;
            if (master >= 0 && rt is not null)
            {
                uint mv = rt[master].Dmx;
                if (mv < f.ModeFrom || mv > f.ModeTo) continue;
            }
            if (available < 0) available = k;
            uint from = f.DmxFrom;
            if (v < from) continue;
            if (v <= f.DmxTo) return k;
            if (fallback < 0 || from >= fallbackFrom)
            {
                fallback = k;
                fallbackFrom = from;
            }
        }
        return fallback >= 0 ? fallback : available >= 0 ? available : ch.InitialFunction;
    }

    internal static int SetIndexAt(ProfileFunction f, uint v) => f.SetIndexAt(v);

    internal static float PhysicalAt(ProfileFunction f, uint v, int setIndex)
    {
        if (setIndex >= 0)
        {
            var s = f.SetArray[setIndex];
            if (s.HasPhysical)
            {
                if (s.DmxTo <= s.DmxFrom) return s.PhysicalFrom;
                float t = (float)((double)(v - s.DmxFrom) / (s.DmxTo - s.DmxFrom));
                return s.PhysicalFrom + (s.PhysicalTo - s.PhysicalFrom) * t;
            }
        }
        return f.PhysicalAt(v);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Level(ProfileFunction f, float value) => Math.Clamp(value / f.LevelScale, 0f, 1f);

    private static float Finite(float v) => float.IsFinite(v) ? v : 0f;

    /// <summary>An angle in degrees reduced to one turn with the sign of the input (as <c>angle % 360</c>, without a libm call).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Turn(double degrees) => (float)(degrees - 360.0 * Math.Truncate(degrees / 360.0));

    private static float GetComponent(Vector3 v, int i) => i switch { 0 => v.X, 1 => v.Y, _ => v.Z };

    private static void SetComponent(ref Vector3 v, int i, float value)
    {
        switch (i)
        {
            case 0: v.X = value; break;
            case 1: v.Y = value; break;
            default: v.Z = value; break;
        }
    }

    // ------------------------------------------------------------------ dynamics helpers

    /// <summary>The physical quantity a motor carries: wheel slot selections share one so a wheel moves continuously between them.</summary>
    internal static AttributeKind Quantity(AttributeKind kind) => kind switch
    {
        AttributeKind.WheelSelectSpin or AttributeKind.WheelSelectShake => AttributeKind.WheelSelect,
        _ => kind,
    };

    internal static bool IsRotation(AttributeKind kind) =>
        kind is AttributeKind.PanRotate or AttributeKind.TiltRotate or AttributeKind.WheelSpin or AttributeKind.SlotRotate
            or AttributeKind.WheelSelectSpin;

    /// <summary>Starts a motor for a new quantity from where the mechanism physically is (e.g. a spinning gobo being indexed).</summary>
    private static void ContinueMotor(ref MotorRuntime m, float target, ProfileFunction f, FixtureProfile p, AttributeKind qty)
    {
        var previous = m.Kind;
        if (qty == AttributeKind.SlotIndex && previous == AttributeKind.SlotRotate && m.Spin.Initialized)
        {
            double pos = m.Spin.Position;
            pos += 360.0 * Math.Round((target - pos) / 360.0);
            m.Motor = new MotorState { Position = pos, Velocity = m.Spin.Velocity, Initialized = true };
            return;
        }
        if (qty == AttributeKind.WheelSelect && previous == AttributeKind.WheelSpin && m.Spin.Initialized && f.Wheel >= 0)
        {
            int slots = p.WheelArray[f.Wheel].SlotCount;
            double pos = 1.0 + m.Spin.Position / 360.0 * slots;
            pos += slots * Math.Round((target - pos) / slots);
            m.Motor = new MotorState { Position = pos, Velocity = m.Spin.Velocity / 360.0 * slots, Initialized = true };
            return;
        }
        if (previous == AttributeKind.Ignored || previous == qty) return;
        m.Motor = default; // different quantity: the first step jumps
    }

    /// <summary>
    /// Rotation speed in deg/s of a spin function's physical value (0 … 1 ranges without an angular-speed unit, and GDTF's
    /// default range 0 … 1, are read as turns per second).
    /// </summary>
    private static float SpinSpeed(ProfileFunction f, float physical)
    {
        if (!float.IsFinite(physical)) return 0f;
        if ((f.Unit != Gdtf.PhysicalUnit.AngularSpeed || f.DefaultPhysicalRange) && f.LevelScale <= 1f) return physical * 360f;
        return Math.Clamp(physical, -36000f, 36000f);
    }

    /// <summary>
    /// Shake frequency in Hz of a shake function (wheel, slot or select shake): its physical value, 2 Hz when it gives none,
    /// and 0.5 … 10 Hz over GDTF's default range 0 … 1 (a placeholder, not 0 … 1 Hz).
    /// </summary>
    private static float ShakeFrequency(ProfileFunction f, float physical)
    {
        if (!float.IsFinite(physical)) return 2f;
        if (f.DefaultPhysicalRange) return 0.5f + 9.5f * Math.Clamp(physical, 0f, 1f);
        return MathF.Abs(physical) > 0.01f ? MathF.Abs(physical) : 2f;
    }

    /// <summary>Motion groups for MSpeed channels: 0 position, 1 colour, 2 gobo, 3 beam (zoom/focus/iris/frost/prism), 4 shaper, 5 other.</summary>
    internal static int MotionGroup(AttributeClass c) => c.Kind switch
    {
        AttributeKind.Pan or AttributeKind.Tilt or AttributeKind.Rotation or AttributeKind.Translation => 0,
        AttributeKind.ColorSub or AttributeKind.Cto or AttributeKind.Ctb or AttributeKind.Ctc => 1,
        AttributeKind.BladeA or AttributeKind.BladeB or AttributeKind.BladeRotation or AttributeKind.BladeSoft
            or AttributeKind.ShaperRotation => 4,
        AttributeKind.Zoom or AttributeKind.Focus or AttributeKind.FocusDistance or AttributeKind.Iris or AttributeKind.Frost => 3,
        _ => c.Wheel switch
        {
            WheelFamily.Color => 1,
            WheelFamily.Gobo or WheelFamily.Animation or WheelFamily.AnimationSystem or WheelFamily.Effects => 2,
            WheelFamily.Prism or WheelFamily.BeamShaper => 3,
            _ => 5,
        },
    };

    /// <summary>Motion group named by an MSpeed attribute prefix (<c>PositionMSpeed</c> → position …); −1 = all groups.</summary>
    internal static int MotionGroupOf(string? code)
    {
        if (string.IsNullOrEmpty(code)) return -1;
        if (code.StartsWith("Position", StringComparison.OrdinalIgnoreCase) || code.StartsWith("Pan", StringComparison.OrdinalIgnoreCase) ||
            code.StartsWith("Tilt", StringComparison.OrdinalIgnoreCase))
            return 0;
        if (code.StartsWith("Color", StringComparison.OrdinalIgnoreCase) || code.StartsWith("CTO", StringComparison.OrdinalIgnoreCase)) return 1;
        if (code.StartsWith("Gobo", StringComparison.OrdinalIgnoreCase) || code.StartsWith("Anim", StringComparison.OrdinalIgnoreCase)) return 2;
        if (code.StartsWith("Zoom", StringComparison.OrdinalIgnoreCase) || code.StartsWith("Focus", StringComparison.OrdinalIgnoreCase) ||
            code.StartsWith("Iris", StringComparison.OrdinalIgnoreCase) || code.StartsWith("Frost", StringComparison.OrdinalIgnoreCase) ||
            code.StartsWith("Prism", StringComparison.OrdinalIgnoreCase) || code.StartsWith("Beam", StringComparison.OrdinalIgnoreCase))
            return 3;
        if (code.StartsWith("Frame", StringComparison.OrdinalIgnoreCase) || code.StartsWith("Shaper", StringComparison.OrdinalIgnoreCase) ||
            code.StartsWith("Blade", StringComparison.OrdinalIgnoreCase))
            return 4;
        return -1;
    }

    /// <summary>Speed factor (0.02 … 1) of an MSpeed function: DMX low = fastest unless the physical range says otherwise.</summary>
    private static float SpeedFactor(ProfileFunction f, in ChannelRuntime r, ProfileChannelSet? set)
    {
        if (f.IsConstant || f.DmxTo - f.DmxFrom < 4) return 1f;
        if (set is not null && (set.Flags & (ChannelSetFlags.NoFunction | ChannelSetFlags.Open)) != 0) return 1f;
        float n = f.Fraction(r.Dmx);
        float factor = f.PhysicalFrom > f.PhysicalTo && f.PhysicalFrom > 0f
            ? r.Physical / f.PhysicalFrom
            : 1f - 0.98f * n;
        return float.IsFinite(factor) ? Math.Clamp(factor, 0.02f, 1f) : 1f;
    }

    // ------------------------------------------------------------------ beams

    private readonly struct ApplyContext
    {
        public ApplyContext(FixtureProfile p, ProfileFunction[] fns, ChannelRuntime[] rt, MotorRuntime[] motors, double time, uint seed,
            EvaluationCache cache, Vector3 gel)
        {
            Profile = p;
            Functions = fns;
            Runtime = rt;
            Motors = motors;
            Time = time;
            Seed = seed;
            Cache = cache;
            Gel = gel;
        }

        /// <summary>Per-channel transmission of the fixture's gel (<see cref="FixtureState.SetGel"/>), (1, 1, 1) without one.</summary>
        public Vector3 Gel { get; }

        public MotorRuntime[] Motors { get; }

        /// <summary>Rotation state of a rotating channel (angle in deg, speed in deg/s).</summary>
        public ref readonly MotorState SpinOf(int channel) => ref Motors[Profile.MotorSlot[channel]].Spin;

        public EvaluationCache Cache { get; }

        public FixtureProfile Profile { get; }
        public ProfileFunction[] Functions { get; }
        public ChannelRuntime[] Runtime { get; }
        public double Time { get; }
        public uint Seed { get; }
    }

    /// <summary>Accumulated beam inputs from the channels of one beam.</summary>
    private struct BeamAcc
    {
        public float Dimmer;
        public float Shutter;
        public Waveform Wave1, Wave2;
        public float Freq1, Freq2;
        public uint Seed1, Seed2;
        public float StrobeRate;
        public bool HasStrobeMode;
        public Waveform StrobeMode;
        public float StrobeDuration;
        public bool Emitters;
        public float C, M, Y;
        public float MiredShift;
        public float CtcKelvin;
        public float Crossfade;
        public bool Rgb, Hsb, Cie, Macro;
        public Vector3 RgbValue;
        public float Hue, Saturation, Brightness;
        public bool HasSaturation;
        public float CieX, CieY, CieBrightness;
        public Vector2 MacroXy;
        public float Tint;
        public float Zoom;
        public float Focus;
        public float FocusDistance;
        public float Iris;
        public float FrostKeep;
        public float ShaperRotation;

        public static BeamAcc Create() => new()
        {
            Dimmer = 1f,
            Shutter = 1f,
            StrobeDuration = float.NaN,
            CtcKelvin = float.NaN,
            Crossfade = float.NaN,
            Saturation = 1f,
            Brightness = 1f,
            CieBrightness = 1f,
            Zoom = float.NaN,
            Focus = float.NaN,
            FocusDistance = float.NaN,
            Iris = 1f,
            FrostKeep = 1f,
        };

        public void AddStrobe(Waveform w, float f, uint seed, double time)
        {
            if (Wave1 == Waveform.None) (Wave1, Freq1, Seed1) = (w, f, seed);
            else if (Wave2 == Waveform.None) (Wave2, Freq2, Seed2) = (w, f, seed);
            else Shutter *= Waveforms.Evaluate(w, time, f, float.NaN, seed);
        }
    }

    /// <summary>Applies a beam's own channels; emitter levels (most pixel channels) take a short path.</summary>
    private static void ApplyOwn(ref BeamAcc acc, BeamState bs, float[] levels, int[] channels, in ApplyContext ctx, ref bool animating)
    {
        var rt = ctx.Runtime;
        var fns = ctx.Functions;
        foreach (int c in channels)
        {
            ref var r = ref rt[c];
            if (r.Function < 0) continue;
            var f = fns[r.Function];
            if (f.Class.Kind == AttributeKind.ColorAdd)
            {
                if (f.Emitter >= 0)
                {
                    levels[f.Emitter] = MathF.Max(levels[f.Emitter], Level(f, r.Value) * r.Relation);
                    acc.Emitters = true;
                }
                continue;
            }
            Apply(ref acc, bs, levels, c, in ctx, ref animating);
        }
    }

    private static void Apply(ref BeamAcc acc, BeamState bs, float[] levels, int c, in ApplyContext ctx, ref bool animating)
    {
        ref var r = ref ctx.Runtime[c];
        if (r.Function < 0) return;
        var f = ctx.Functions[r.Function];
        var flags = r.SetFlags;
        var cls = f.Class;
        // the set object itself only for the attributes that read its pre-parsed meaning (waveform, kelvin, colour, name)
        var set = r.SetIndex >= 0 && ReadsSet(cls.Kind) ? f.SetArray[r.SetIndex] : null;
        float v = r.Value;
        float level = Level(f, v) * r.Relation;
        int n = Math.Max(cls.Number, 1) - 1;
        uint seed = ctx.Seed * 2654435761u + (uint)c;
        switch (cls.Kind)
        {
            case AttributeKind.Dimmer:
                acc.Dimmer *= (flags & ChannelSetFlags.Closed) != 0 ? 0f : level;
                break;
            case AttributeKind.Shutter:
                if ((flags & ChannelSetFlags.Closed) != 0) acc.Shutter = 0f;
                else if ((flags & (ChannelSetFlags.Open | ChannelSetFlags.NoFunction)) == 0 && f.LevelScale <= 1f)
                    acc.Shutter *= Math.Clamp(r.Physical, 0f, 1f);
                break;
            case AttributeKind.Strobe:
                if ((flags & ChannelSetFlags.Closed) != 0) acc.Shutter = 0f;
                else if ((flags & (ChannelSetFlags.Open | ChannelSetFlags.NoFunction)) == 0)
                {
                    var wave = set?.Waveform is { } sw && sw != Waveform.None ? sw : f.Waveform;
                    acc.AddStrobe(wave, StrobeFrequency(f, r.Physical), seed, ctx.Time);
                    animating = true;
                }
                break;
            case AttributeKind.StrobeRate:
                acc.StrobeRate = (flags & (ChannelSetFlags.Open | ChannelSetFlags.NoFunction | ChannelSetFlags.Closed)) != 0
                    ? 0f
                    : StrobeFrequency(f, r.Physical);
                if (set?.Waveform is { } rw)
                {
                    acc.HasStrobeMode = true;
                    acc.StrobeMode = rw;
                }
                break;
            case AttributeKind.StrobeMode:
                acc.HasStrobeMode = true;
                acc.StrobeMode = (flags & (ChannelSetFlags.Open | ChannelSetFlags.NoFunction)) != 0
                    ? Waveform.None
                    : set?.Waveform ?? (f.Waveform == Waveform.None ? Waveform.None : f.Waveform);
                break;
            case AttributeKind.StrobeDuration:
                acc.StrobeDuration = StrobeDuration(f, r.Physical, r.Dmx);
                break;
            case AttributeKind.ColorAdd:
                if (f.Emitter >= 0)
                {
                    levels[f.Emitter] = MathF.Max(levels[f.Emitter], level);
                    acc.Emitters = true;
                }
                break;
            case AttributeKind.ColorSub:
                ApplySubtractive(ref acc, f.ColorCode, level);
                break;
            case AttributeKind.ColorRgb:
                acc.Rgb = true;
                switch (f.ColorCode)
                {
                    case 0: acc.RgbValue.X = level; break;
                    case 1: acc.RgbValue.Y = level; break;
                    case 2: acc.RgbValue.Z = level; break;
                    case 3: acc.RgbValue.X = 1f - level; break;
                    case 4: acc.RgbValue.Y = 1f - level; break;
                    case 5: acc.RgbValue.Z = 1f - level; break;
                }
                break;
            case AttributeKind.HsbHue:
                acc.Hsb = true;
                acc.Hue = f.LevelScale <= 1f ? v * 360f : f.Unit == Gdtf.PhysicalUnit.Percent ? v * 3.6f : v;
                break;
            case AttributeKind.HsbSaturation:
                acc.HasSaturation = true;
                acc.Saturation = level;
                break;
            case AttributeKind.HsbBrightness:
                acc.Brightness = level;
                break;
            case AttributeKind.CieX:
                acc.Cie = true;
                acc.CieX = MathF.Abs(v) > 1.5f ? v / 10000f : v;
                break;
            case AttributeKind.CieY:
                acc.Cie = true;
                acc.CieY = MathF.Abs(v) > 1.5f ? v / 10000f : v;
                break;
            case AttributeKind.CieBrightness:
                acc.CieBrightness = level;
                break;
            case AttributeKind.Cto or AttributeKind.Ctb:
                acc.MiredShift += MiredShift(f, set, r, ctx.Profile, cls.Kind);
                break;
            case AttributeKind.Ctc:
                if ((flags & (ChannelSetFlags.Open | ChannelSetFlags.NoFunction)) == 0 && KelvinTarget(f, set, r) is var ctc && !float.IsNaN(ctc))
                    acc.CtcKelvin = ctc;
                break;
            case AttributeKind.ColorCrossfade:
                acc.Crossfade = level;
                break;
            case AttributeKind.Tint:
                if ((flags & ChannelSetFlags.NoFunction) == 0) acc.Tint += TintDuv(f, v);
                break;
            case AttributeKind.ColorMacro:
                if (set is not null && (flags & (ChannelSetFlags.Open | ChannelSetFlags.NoFunction | ChannelSetFlags.Closed)) == 0)
                {
                    if (!float.IsNaN(set.Color.X))
                    {
                        acc.Macro = true;
                        acc.MacroXy = set.Color;
                    }
                }
                break;
            case AttributeKind.WheelSelect or AttributeKind.WheelSelectSpin or AttributeKind.WheelSelectShake or AttributeKind.WheelIndex
                or AttributeKind.WheelSpin or AttributeKind.WheelShake or AttributeKind.WheelRandom or AttributeKind.SlotIndex
                or AttributeKind.SlotRotate or AttributeKind.SlotShake or AttributeKind.EffectRate or AttributeKind.AnimationInsertion:
                ApplyWheel(bs, f, c, ref r, set, n, in ctx, ref animating);
                break;
            case AttributeKind.Frost:
                acc.FrostKeep *= 1f - level;
                break;
            case AttributeKind.FrostPulse:
                if ((flags & (ChannelSetFlags.Open | ChannelSetFlags.NoFunction)) == 0)
                {
                    acc.FrostKeep *= 1f - Waveforms.Evaluate(f.Waveform, ctx.Time, StrobeFrequency(f, r.Physical), float.NaN, seed);
                    animating = true;
                }
                break;
            case AttributeKind.Iris:
                {
                    float opening = (flags & ChannelSetFlags.Open) != 0 ? 1f
                        : f.LevelScale <= 1f ? Math.Clamp(v, 0f, 1f)
                        : 1f - f.Fraction(r.Dmx);
                    acc.Iris = MathF.Min(acc.Iris, opening);
                    break;
                }
            case AttributeKind.IrisPulse:
                if ((flags & (ChannelSetFlags.Open | ChannelSetFlags.NoFunction)) == 0)
                {
                    float w = Waveforms.Evaluate(f.Waveform, ctx.Time, StrobeFrequency(f, r.Physical), float.NaN, seed);
                    acc.Iris *= 0.1f + 0.9f * w;
                    animating = true;
                }
                break;
            case AttributeKind.Zoom:
                if ((flags & (ChannelSetFlags.NoFunction | ChannelSetFlags.Open | ChannelSetFlags.Closed)) == 0 && MathF.Abs(v) > 0.05f)
                    acc.Zoom = MathF.Abs(v);
                break;
            case AttributeKind.Focus:
                acc.Focus = f.MaxValue > f.MinValue ? Math.Clamp((v - f.MinValue) / (f.MaxValue - f.MinValue), 0f, 1f) : 0.5f;
                break;
            case AttributeKind.FocusDistance:
                acc.FocusDistance = v;
                break;
            case AttributeKind.BladeA or AttributeKind.BladeB or AttributeKind.BladeRotation or AttributeKind.BladeSoft:
                if (n < bs.Blades.Length)
                {
                    ref var blade = ref bs.Blades[n];
                    switch (cls.Kind)
                    {
                        case AttributeKind.BladeA: blade.A = level; break;
                        case AttributeKind.BladeB: blade.B = level; break;
                        case AttributeKind.BladeRotation: blade.Rotation = v * DegToRad; break;
                        default:
                            if (f.ColorCode == 7) blade.SoftB = level; else blade.SoftA = level;
                            break;
                    }
                }
                break;
            case AttributeKind.ShaperRotation:
                acc.ShaperRotation = v * DegToRad;
                break;
        }
    }

    private static bool ReadsSet(AttributeKind kind) => kind is AttributeKind.Strobe or AttributeKind.StrobeRate or AttributeKind.StrobeMode
        or AttributeKind.Cto or AttributeKind.Ctb or AttributeKind.Ctc or AttributeKind.ColorMacro or AttributeKind.WheelSelect
        or AttributeKind.WheelSelectSpin or AttributeKind.WheelSelectShake;

    /// <summary>Adds a subtractive filter (code 0 R, 1 G, 2 B, 3 C, 4 M, 5 Y; see <see cref="ProfileFunction.ColorCode"/>).</summary>
    private static void ApplySubtractive(ref BeamAcc acc, int code, float level)
    {
        switch (code)
        {
            case 3: acc.C = MathF.Max(acc.C, level); break;
            case 4: acc.M = MathF.Max(acc.M, level); break;
            case 5: acc.Y = MathF.Max(acc.Y, level); break;
            case 0: acc.M = MathF.Max(acc.M, level); acc.Y = MathF.Max(acc.Y, level); break; // a red filter = magenta + yellow
            case 1: acc.C = MathF.Max(acc.C, level); acc.Y = MathF.Max(acc.Y, level); break;
            case 2: acc.C = MathF.Max(acc.C, level); acc.M = MathF.Max(acc.M, level); break;
        }
    }

    /// <summary>
    /// Strobe/pulse frequency in Hz; 0 … 1 ranges without a frequency unit, and GDTF's default range 0 … 1 (a placeholder
    /// left by the author, not a 1 Hz maximum), span 0.5 … 25 Hz.
    /// </summary>
    internal static float StrobeFrequency(ProfileFunction f, float physical)
    {
        if (!float.IsFinite(physical)) return 0f;
        if ((f.Unit != Gdtf.PhysicalUnit.Frequency || f.DefaultPhysicalRange) && f.LevelScale <= 1f) return 0.5f + 24.5f * Math.Clamp(physical, 0f, 1f);
        return Math.Clamp(MathF.Abs(physical), 0f, 100f);
    }

    /// <summary>Flash length in s from a StrobeDuration function (time units, milliseconds for large values, else a fraction of 1 s).</summary>
    private static float StrobeDuration(ProfileFunction f, float physical, uint dmx)
    {
        if (!float.IsFinite(physical)) return float.NaN;
        float d = MathF.Abs(physical);
        if (f.Unit == Gdtf.PhysicalUnit.Time) return d > 5f ? d / 1000f : d;
        if (d > 5f) return d / 1000f;
        if (f.LevelScale <= 1f) return 0.005f + 0.5f * f.Fraction(dmx);
        return d;
    }

    /// <summary>
    /// Mired shift of a CTO/CTB function (from kelvin physicals, channel-set names or 0 … 1), relative to its neutral end
    /// (no filter); a colour-temperature selector (no neutral end, see <see cref="ProfileFunction.NeutralKelvin"/>) is
    /// relative to the beam's source, i.e. its value is the resulting CCT.
    /// </summary>
    private static float MiredShift(ProfileFunction f, ProfileChannelSet? set, in ChannelRuntime r, FixtureProfile p, AttributeKind kind)
    {
        if (set is not null && (set.Flags & (ChannelSetFlags.Open | ChannelSetFlags.NoFunction)) != 0) return 0f;
        float target = KelvinTarget(f, set, r);
        if (float.IsNaN(target))
        {
            if (!float.IsNaN(f.KelvinFrom) || f.LevelScale > 1f || f.IsConstant) return 0f;
            // a 0 … 1 insertion: a full conversion filter 5600 → 3200 K (CTB the reverse)
            float level = Level(f, r.Value);
            return kind == AttributeKind.Cto ? level * (1e6f / 3200f - 1e6f / 5600f) : -level * (1e6f / 3200f - 1e6f / 5600f);
        }
        float source = p.BeamArray.Length > 0 ? p.BeamArray[0].SourceKelvin : 6500f;
        float neutral = float.IsNaN(f.NeutralKelvin) ? source : f.NeutralKelvin;
        return 1e6f / target - 1e6f / neutral;
    }

    /// <summary>
    /// Colour temperature (K) a CTO/CTB/CTC function names at its current value — its kelvin physical value, or the kelvin
    /// of the current channel set (interpolated in mired towards the next set) — or NaN when it names none.
    /// </summary>
    private static float KelvinTarget(ProfileFunction f, ProfileChannelSet? set, in ChannelRuntime r)
    {
        float target = float.NaN;
        if (!float.IsNaN(f.KelvinFrom)) target = r.Value;
        else if (set is not null && !float.IsNaN(set.Kelvin))
        {
            target = set.Kelvin;
            var sets = f.SetArray;
            int i = r.SetIndex;
            if (i + 1 < sets.Length && !float.IsNaN(sets[i + 1].Kelvin) && set.DmxTo > set.DmxFrom)
            {
                float t = (float)(r.Dmx - set.DmxFrom) / (set.DmxTo - set.DmxFrom + 1);
                target = 1e6f / (1e6f / set.Kelvin + t * (1e6f / sets[i + 1].Kelvin - 1e6f / set.Kelvin));
            }
        }
        return target is >= 1000f and <= 25000f ? target : float.NaN;
    }

    /// <summary>Δuv of a Tint function (positive = greener): small absolute ranges are Δuv, symmetric ranges ±0.02, 0 … 1 centred at 0.5.</summary>
    private static float TintDuv(ProfileFunction f, float v)
    {
        float maxAbs = MathF.Max(MathF.Abs(f.MinValue), MathF.Abs(f.MaxValue));
        if (maxAbs <= 0f || !float.IsFinite(v)) return 0f;
        if (maxAbs <= 0.06f) return v;
        if (f.MinValue < 0f) return v / maxAbs * 0.02f;
        return (v / maxAbs - 0.5f) * 0.04f;
    }

    private static void ApplyWheel(BeamState bs, ProfileFunction f, int c, ref ChannelRuntime r, ProfileChannelSet? set, int n, in ApplyContext ctx,
        ref bool animating)
    {
        var arr = f.Class.Wheel switch
        {
            WheelFamily.Color => bs.ColorWheels,
            WheelFamily.Gobo => bs.Gobos,
            WheelFamily.Prism => bs.Prisms,
            WheelFamily.Animation or WheelFamily.AnimationSystem => bs.AnimationWheels,
            WheelFamily.Effects => bs.Effects,
            WheelFamily.BeamShaper => bs.BeamShapers,
            _ => null,
        };
        if (arr is null || arr.Length == 0) return;
        var flags = r.SetFlags;
        if (n >= arr.Length) n = arr.Length - 1;
        ref var w = ref arr[n];
        bool selection = f.Class.Kind is AttributeKind.WheelSelect or AttributeKind.WheelSelectSpin or AttributeKind.WheelSelectShake;
        if (selection && f.SecondarySelection)
        {
            // a selection attribute on a channel that selects nothing of the mechanism (its rotation channel, a second
            // prism channel written with the first prism's attribute): the selection channel decides what is in the
            // beam; a selection with spin still turns the item
            if (f.Class.Kind == AttributeKind.WheelSelectSpin && (flags & ChannelSetFlags.WheelOut) == 0)
            {
                ref readonly var spin = ref ctx.SpinOf(c);
                w.Rotation = Turn(spin.Position) * DegToRad;
                w.RotationSpeed = (float)spin.Velocity * DegToRad;
            }
            return;
        }
        if ((flags & ChannelSetFlags.WheelOut) != 0 && !selection && f.Class.Kind != AttributeKind.AnimationInsertion)
        {
            // an item rotation, index or rate function in a range named "No function", "Off" … does nothing to the
            // mechanism — it does not put it in the beam — unless its channel is the mechanism's selection channel, where
            // the range takes the mechanism out (Robe Esprite "EffectWheelIndex" / "No function"); a whole-wheel index or
            // spin positions the wheel, so its "Open" range is the open position (ADJ Hydro Profile "Gobo 1 - 7 Index")
            if ((f.OnSelectionChannel || f.Class.Kind is AttributeKind.WheelIndex or AttributeKind.WheelSpin or AttributeKind.WheelRandom
                    or AttributeKind.WheelShake) && !w.InVote)
            {
                if (f.Wheel >= 0) w.Wheel = f.Wheel;
                w.OutVote = true;
            }
            return;
        }
        // a selection of a slot holding an item overrides an "out" range — or a selection of an open slot — of another
        // channel on the same mechanism, whichever came first (CLF Poseidon Wash XS: "Background Color" also written as
        // Color1, at "ColorMix Activ"); a selection that names no slot (a wheel-less prism, an index written as selection:
        // DLW Dream L1400P "AnimationWheel1" 'Zero' … 'max Index' next to its rotation channel's 'Open') does not
        bool selectOut = selection && ((flags & ChannelSetFlags.WheelOut) != 0 || RestsOnOpenSlot(ctx.Profile, f, r.Value, flags));
        bool selectsSlot = selection && !selectOut && f.Wheel >= 0 && f.UsesSlotPosition;
        if (selectsSlot && w.OutVote && !w.InVote)
        {
            w.Wheel = -1;
            w.Position = float.NaN;
        }
        int slots = f.Wheel >= 0 ? ctx.Profile.WheelArray[f.Wheel].SlotCount : 1;
        // the wheel a selection put in place stays the mechanism's wheel: an index or rotation function naming another
        // wheel (Vari-Lite VL3600 "Gobo1Pos" on a second copy of the gobo wheel) turns the item, it does not swap wheels
        if (f.Wheel >= 0 && f.ImpliesInsertion && (selectsSlot || !w.InVote)) w.Wheel = f.Wheel;
        if (f.IndexesSelectedItem)
        {
            // an item index or rotation written as a wheel index or spin: turns the item, and moves the wheel only when
            // no channel selects a slot
            if (f.Class.Kind == AttributeKind.WheelSpin)
            {
                ref readonly var spin = ref ctx.SpinOf(c);
                w.Rotation = Turn(spin.Position) * DegToRad;
                w.RotationSpeed = (float)spin.Velocity * DegToRad;
            }
            else
            {
                w.Rotation = r.Value * DegToRad;
                w.RotationSpeed = 0f;
            }
            if (w.InVote) return;
        }
        switch (f.Class.Kind)
        {
            case AttributeKind.WheelSelect or AttributeKind.WheelSelectSpin or AttributeKind.WheelSelectShake:
                {
                    // a range named "Open", "Off", "Removed" … (WheelOut) takes the mechanism out of the beam — unless
                    // another channel selects a slot of it; a ramp after it moves a single-item mechanism in gradually
                    // (both found while the profile is compiled)
                    bool isOut = selectOut;
                    if (isOut && w.InVote) break; // an item another channel selects stays in
                    float insertion = float.NaN;
                    if (isOut)
                    {
                        w.OutVote = true;
                    }
                    else if ((flags & ChannelSetFlags.InsertionCycle) != 0 && set is not null)
                    {
                        insertion = set.CycleInsertion(r.Dmx, ctx.Time);
                        animating = true;
                    }
                    else if ((flags & ChannelSetFlags.InsertionRamp) != 0 && set is not null && set.DmxTo > set.DmxFrom)
                    {
                        insertion = Math.Clamp((float)(r.Dmx - set.DmxFrom) / (set.DmxTo - set.DmxFrom), 0f, 1f);
                    }
                    if (f.Wheel >= 0)
                    {
                        // a ramp or cycle moves the wheel's single item in and out (not the wheel from slot to slot)
                        w.Position = f.RampSlot > 0 && (flags & (ChannelSetFlags.InsertionRamp | ChannelSetFlags.InsertionCycle)) != 0
                            ? f.RampSlot
                            : f.UsesSlotPosition ? r.Value : 1f;
                    }
                    else
                    {
                        // a prism or effect without wheel data: in unless the range says it is out
                        w.Position = float.NaN;
                        w.IsOpen = isOut;
                        w.Facets = isOut || f.Class.Wheel != WheelFamily.Prism ? 0 : Math.Max(NameParsing.ParseFacetCount(set?.Name ?? f.Name), 3);
                        w.Rate = r.Physical;
                        if (float.IsNaN(insertion) && !isOut) insertion = 1f;
                    }
                    if (selectsSlot) w.InVote = true;
                    if (!float.IsNaN(insertion)) SetInsertion(ref w, insertion);
                }
                if (f.Class.Kind == AttributeKind.WheelSelectSpin)
                {
                    ref readonly var spin = ref ctx.SpinOf(c);
                    w.Rotation = Turn(spin.Position) * DegToRad;
                    w.RotationSpeed = (float)spin.Velocity * DegToRad;
                }
                else if (f.Class.Kind == AttributeKind.WheelSelectShake && (flags & ChannelSetFlags.NoFunction) == 0)
                {
                    float hz = ShakeFrequency(f, r.Physical);
                    w.ShakeFrequency = hz;
                    if (float.IsFinite(w.Position)) w.Position += 0.12f * Waveforms.Oscillation(ctx.Time, hz);
                    animating = true;
                }
                break;
            case AttributeKind.WheelIndex:
                w.WheelAngle = r.Value * DegToRad;
                w.Position = 1f + r.Value / 360f * slots;
                break;
            case AttributeKind.WheelSpin:
                {
                    double angle = ctx.SpinOf(c).Position;
                    float turn = Turn(angle);
                    w.WheelAngle = turn * DegToRad;
                    w.Position = 1f + turn / 360f * slots;
                    break;
                }
            case AttributeKind.WheelShake:
                {
                    float hz = ShakeFrequency(f, r.Physical);
                    if (!float.IsFinite(w.Position)) w.Position = 1f;
                    w.Position += 0.25f * Waveforms.Oscillation(ctx.Time, hz);
                    w.ShakeFrequency = hz;
                    animating = true;
                    break;
                }
            case AttributeKind.WheelRandom:
                {
                    float hz = f.DefaultPhysicalRange ? 0.2f + 4.8f * Math.Clamp(r.Physical, 0f, 1f) : Math.Clamp(MathF.Abs(r.Physical), 0.05f, 50f);
                    long k = (long)Math.Floor(ctx.Time * hz);
                    w.Position = 1f + MathF.Floor(Waveforms.Hash01(ctx.Seed, k, 0x77u + (uint)n) * slots);
                    animating = true;
                    break;
                }
            case AttributeKind.SlotIndex:
                w.Rotation = r.Value * DegToRad;
                w.RotationSpeed = 0f;
                break;
            case AttributeKind.SlotRotate:
                {
                    ref readonly var spin = ref ctx.SpinOf(c);
                    w.Rotation = Turn(spin.Position) * DegToRad;
                    w.RotationSpeed = (float)spin.Velocity * DegToRad;
                }
                break;
            case AttributeKind.SlotShake:
                {
                    float hz = ShakeFrequency(f, r.Physical);
                    w.Rotation += 15f * DegToRad * Waveforms.Oscillation(ctx.Time, hz);
                    w.ShakeFrequency = hz;
                    animating = true;
                    break;
                }
            case AttributeKind.EffectRate:
                w.Rate = r.Physical;
                break;
            case AttributeKind.AnimationInsertion:
                SetInsertion(ref w, (flags & ChannelSetFlags.WheelOut) != 0 ? 0f : Level(f, r.Value));
                break;
        }
    }

    /// <summary>
    /// True when slot selection <paramref name="f"/> rests exactly on an open slot of its wheel at slot position
    /// <paramref name="position"/> (not between slots, not on a ramp or cycle, which move a single item instead).
    /// </summary>
    private static bool RestsOnOpenSlot(FixtureProfile p, ProfileFunction f, float position, ChannelSetFlags flags)
    {
        if (f.Wheel < 0 || !f.UsesSlotPosition || !float.IsFinite(position) ||
            (flags & (ChannelSetFlags.InsertionRamp | ChannelSetFlags.InsertionCycle)) != 0)
            return false;
        float k = MathF.Round(position);
        if (MathF.Abs(position - k) > 1e-3f || MathF.Abs(k) > 1e6f) return false;
        var wheel = p.WheelArray[f.Wheel];
        return wheel.SlotOpenArray[wheel.WrapIndex((int)k - 1)];
    }

    /// <summary>
    /// Sets a mechanism's insertion from a channel that states a level (a ramp, a cycle, an animation-system insertion
    /// channel); several such channels on one mechanism combine to the smallest insertion.
    /// </summary>
    private static void SetInsertion(ref WheelState w, float insertion)
    {
        insertion = float.IsFinite(insertion) ? Math.Clamp(insertion, 0f, 1f) : 0f;
        w.Insertion = w.InsertionSet ? MathF.Min(w.Insertion, insertion) : insertion;
        w.InsertionSet = true;
    }

    private static void ResetWheels(BeamState s)
    {
        Reset(s.ColorWheels);
        Reset(s.Gobos);
        Reset(s.Prisms);
        Reset(s.AnimationWheels);
        Reset(s.Effects);
        Reset(s.BeamShapers);
        var blades = s.Blades;
        for (int i = 0; i < blades.Length; i++) blades[i] = default;

        static void Reset(WheelState[] a)
        {
            for (int i = 0; i < a.Length; i++) a[i] = WheelState.UnusedState;
        }
    }

    private static void CopyWheels(BeamState from, BeamState to)
    {
        Copy(from.ColorWheels, to.ColorWheels);
        Copy(from.Gobos, to.Gobos);
        Copy(from.Prisms, to.Prisms);
        Copy(from.AnimationWheels, to.AnimationWheels);
        Copy(from.Effects, to.Effects);
        Copy(from.BeamShapers, to.BeamShapers);
        from.Blades.AsSpan(0, Math.Min(from.Blades.Length, to.Blades.Length)).CopyTo(to.Blades);
        if (to.Blades.Length > from.Blades.Length) to.Blades.AsSpan(from.Blades.Length).Clear();

        static void Copy(WheelState[] a, WheelState[] b)
        {
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i < n; i++) b[i] = a[i];
            for (int i = n; i < b.Length; i++) b[i] = WheelState.UnusedState;
        }
    }

    /// <summary>Resolves slot, blend, facets and insertion of every wheel-like mechanism of a beam from its position.</summary>
    private static void FinalizeAllWheels(FixtureProfile p, BeamState bs)
    {
        if (bs.ColorWheels.Length > 0) FinalizeWheels(p, bs.ColorWheels, prisms: false);
        if (bs.Gobos.Length > 0) FinalizeWheels(p, bs.Gobos, prisms: false);
        if (bs.Prisms.Length > 0) FinalizeWheels(p, bs.Prisms, prisms: true);
        if (bs.AnimationWheels.Length > 0) FinalizeWheels(p, bs.AnimationWheels, prisms: false);
        if (bs.Effects.Length > 0) FinalizeWheels(p, bs.Effects, prisms: false);
        if (bs.BeamShapers.Length > 0) FinalizeWheels(p, bs.BeamShapers, prisms: false);
    }

    private static void FinalizeWheels(FixtureProfile p, WheelState[] arr, bool prisms)
    {
        for (int i = 0; i < arr.Length; i++)
        {
            ref var w = ref arr[i];
            // A wheel named only by a rotation/rate/insertion function (no slot selection channel, or the selection
            // function of this mode has no wheel) rests on its home slot.
            if (w.Wheel >= 0 && !float.IsFinite(w.Position)) w.Position = 1f;
            // taken out of the beam by an "out" range, and no channel selects it
            bool outOfBeam = w.OutVote && !w.InVote;
            if (w.Wheel < 0)
            {
                if (outOfBeam || (w.InsertionSet && !(w.Insertion > 0f)))
                {
                    // a wheel-less prism/effect taken out by its range
                    w.IsOpen = true;
                    w.Facets = 0;
                    w.Insertion = 0f;
                }
                if (float.IsNaN(w.Position) && w.Insertion > 0f) continue; // wheel-less prism/effect set in ApplyWheel
                w.Slot = 0;
                w.NextSlot = 0;
                w.Blend = 0f;
                continue;
            }
            var wheel = p.WheelArray[w.Wheel];
            int n = wheel.SlotCount;
            float q = w.Position - 1f;
            float fl = MathF.Floor(q);
            float blend = q - fl;
            int i0 = wheel.WrapIndex((int)fl);
            if (blend < 1e-4f) blend = 0f;
            else if (blend > 1f - 1e-4f)
            {
                blend = 0f;
                i0 = wheel.WrapIndex(i0 + 1);
            }
            int i1 = wheel.WrapIndex(i0 + 1);
            w.Slot = i0 + 1;
            w.NextSlot = i1 + 1;
            w.Blend = blend;
            bool open0 = wheel.SlotOpenArray[i0], open1 = wheel.SlotOpenArray[i1];
            w.IsOpen = open0 && (blend == 0f || open1);
            int facets = blend > 0.5f ? wheel.SlotFacetArray[i1] : wheel.SlotFacetArray[i0];
            w.Facets = prisms ? (facets > 0 ? facets : (blend > 0.5f ? open1 : open0) ? 0 : 3) : facets;
            if (outOfBeam)
            {
                // taken out of the beam by its range ("Open", "Off", "Removed" …), whatever slot the wheel rests on
                w.IsOpen = true;
                w.Facets = 0;
                w.Insertion = 0f;
            }
            else if (!w.InsertionSet)
            {
                w.Insertion = w.IsOpen ? 0f : 1f;
            }
            else if (!(w.Insertion > 0f))
            {
                // at insertion 0 (a ramp at its start, an insertion channel at 0): out, whatever slot the wheel rests on
                w.IsOpen = true;
                w.Facets = 0;
                w.Insertion = 0f;
            }
            else if (w.IsOpen)
            {
                w.Insertion = 0f; // an open slot is a hole however far it is inserted
            }
            _ = n;
        }
    }

    private static void Finalize(ProfileBeam pb, BeamState bs, ref BeamAcc acc, float[] levels, DimmerCurve curve,
        in ApplyContext ctx, ref bool animating)
    {
        var p = ctx.Profile;

        // intensity
        float dimmer = ApplyCurve(Math.Clamp(acc.Dimmer, 0f, 1f), curve);
        float shutter = Math.Clamp(acc.Shutter, 0f, 1f);
        float strobeFreq = 0f;
        if (acc.Wave1 != Waveform.None)
        {
            shutter *= Waveforms.Evaluate(acc.Wave1, ctx.Time, acc.Freq1, acc.StrobeDuration, acc.Seed1);
            strobeFreq = acc.Freq1;
        }
        if (acc.Wave2 != Waveform.None) shutter *= Waveforms.Evaluate(acc.Wave2, ctx.Time, acc.Freq2, acc.StrobeDuration, acc.Seed2);
        if (acc.StrobeRate > 0.01f)
        {
            var wave = acc.HasStrobeMode ? acc.StrobeMode : Waveform.Strobe;
            if (wave != Waveform.None)
            {
                shutter *= Waveforms.Evaluate(wave, ctx.Time, acc.StrobeRate, acc.StrobeDuration, ctx.Seed * 31u + 7u);
                strobeFreq = acc.StrobeRate;
                animating = true;
            }
        }
        bs.Dimmer = dimmer;
        bs.Shutter = shutter;
        bs.Intensity = dimmer * shutter;
        bs.Strobing = strobeFreq > 0f;
        bs.StrobeFrequency = strobeFreq;

        // colour: additive emitters, indirect colour systems or the white source
        float reference = pb.ReferenceFlux;
        Vector3 xyz = default;
        bool any = false;
        if (pb.Emitters.Length > 0 && acc.Emitters)
        {
            // additive mixing by luminous flux in XYZ (as ColorMixing.Mix, without building a colour per emitter)
            var full = pb.EmitterFullXyz;
            int n = Math.Min(full.Length, levels.Length);
            for (int e = 0; e < n; e++)
            {
                float level = levels[e];
                if (level > 0f) xyz += full[e] * MathF.Min(level, 1f);
            }
            any = true;
        }
        if (acc.Cie && acc.CieY > 1e-4f)
        {
            float x = Math.Clamp(acc.CieX, 0f, 1f), y = Math.Clamp(acc.CieY, 1e-4f, 1f);
            xyz += new Vector3(x / y, 1f, MathF.Max(1f - x - y, 0f) / y) * (acc.CieBrightness * reference);
            any = true;
        }
        else if (acc.Hsb)
        {
            xyz += RgbToXyz(p, HsbToRgb(acc.Hue, acc.Saturation, acc.Brightness)) * reference;
            any = true;
        }
        else if (acc.Rgb)
        {
            xyz += RgbToXyz(p, acc.RgbValue * acc.Brightness) * reference;
            any = true;
        }
        float tint = acc.Tint, ctcKelvin = acc.CtcKelvin;
        if (float.IsFinite(acc.Crossfade) && !float.IsNaN(ctcKelvin))
        {
            // two engines: the white one (CTC + tint, at the emitters' best output for that CCT) cross-faded with the colour one
            float f = Math.Clamp(acc.Crossfade, 0f, 1f);
            var white = ctx.Cache.WhiteXyz(ctcKelvin, tint) * (reference * pb.WhiteEfficiency(ctcKelvin));
            var colour = any ? xyz : pb.SourceXyz * reference;
            xyz = white * (1f - f) + colour * f;
            any = true;
            tint = 0f;
            ctcKelvin = float.NaN; // consumed by the white engine
        }
        if (!any) xyz = pb.SourceXyz * reference;
        var rgb = ColorSpaces.XyzToRec2020(xyz);

        // a saturation channel without hue desaturates the fixture's (virtual) gels: colour macros and colour wheels
        float gelSaturation = acc.HasSaturation && !acc.Hsb ? Math.Clamp(acc.Saturation, 0f, 1f) : 1f;
        if (acc.Macro)
        {
            var sourceRgb = ColorSpaces.XyzToRec2020(pb.SourceXyz * reference);
            var gel = Filters.PhysicalChannelTransmission(LightColor.FromXy(acc.MacroXy), 1f);
            rgb = sourceRgb * Vector3.Lerp(Vector3.One, gel, gelSaturation);
        }

        // subtractive: CMY flags and colour temperature (CTC absolute, CTO/CTB shifts), then colour wheels
        bool cmy = acc.C > 0f || acc.M > 0f || acc.Y > 0f;
        bool temperature = !float.IsNaN(ctcKelvin) || acc.MiredShift != 0f;
        if (pb.Subtractive is { } table && (cmy || temperature))
        {
            float targetKelvin = float.NaN;
            if (temperature)
            {
                float mired = (float.IsNaN(ctcKelvin) ? 1e6f / pb.SourceKelvin : 1e6f / ctcKelvin) + acc.MiredShift;
                targetKelvin = 1e6f / Math.Clamp(mired, 40f, 1000f);
            }
            // an LED engine re-balances its emitters: the chromatic change of a conversion filter, but the luminous cost of
            // the engine at the new white point instead of the filter's (re-balancing never adds light)
            rgb *= ctx.Cache.Filter(pb, table, acc.C, acc.M, acc.Y, targetKelvin, electronic: pb.ElectronicColor && temperature);
        }
        var presets = pb.PresetOutput;
        foreach (ref readonly var w in bs.ColorWheels.AsSpan())
        {
            if (w.Wheel < 0 || w.Slot <= 0 || w.IsOpen) continue;
            if (presets is not null && presets[w.Wheel] is { } preset && TryPreset(p.WheelArray[w.Wheel], preset, in w, rgb, gelSaturation, out var rendered))
            {
                rgb = rendered;
                continue;
            }
            var t = p.WheelArray[w.Wheel].SlotTransmissionArray;
            var tr = t[w.Slot - 1];
            if (w.Blend > 0f) tr = Vector3.Lerp(tr, t[w.NextSlot - 1], w.Blend);
            rgb *= gelSaturation < 1f ? Vector3.Lerp(Vector3.One, tr, gelSaturation) : tr;
        }
        if (tint != 0f) rgb = ApplyTint(rgb, tint);
        // a gel in front of a white fixture (MVR <Color>); fixtures with a colour system of their own make their colour
        if (!pb.HasColorSystem && ctx.Gel != Vector3.One) rgb *= ctx.Gel;

        float flux = MathF.Max(ColorSpaces.LuminanceRec2020(rgb), 0f);
        if (!float.IsFinite(flux) || pb.Unaddressed) flux = 0f; // an unaddressed LED ring has no light of its own
        float transmission = reference > 0f ? Math.Clamp(flux / reference, 0f, 1f) : 0f;
        bs.ColorTransmission = transmission;
        if (flux > 0f) bs.Color = LightColor.FromRec2020(rgb);
        else if (!any) bs.Color = LightColor.FromXyz(pb.SourceXyz);

        // optics
        float beamAngle = float.IsFinite(acc.Zoom) ? Math.Clamp(acc.Zoom, 0.1f, 179f) * DegToRad : pb.NativeBeamAngle;
        bs.BeamAngle = beamAngle;
        bs.FieldAngle = MathF.Max(beamAngle, MathF.Min(beamAngle * pb.FieldRatio, MaxFieldAngle));
        float stretch = ShaperStretch(p, bs, out float stretchRotation);
        bs.ShaperStretch = stretch;
        bs.ShaperStretchRotation = stretchRotation;
        float peak = pb.ReferenceFlux / ctx.Cache.SolidAngle(pb.SolidAngles, beamAngle) * transmission * bs.Intensity / stretch;
        bs.PeakCandela = float.IsFinite(peak) ? peak : 0f;
        bs.Flux = flux * bs.Intensity;
        bs.Focus = float.IsFinite(acc.Focus) ? acc.Focus : 0.5f;
        bs.FocusDistance = acc.FocusDistance;
        bs.Iris = Math.Clamp(acc.Iris, 0f, 1f);
        bs.Frost = Math.Clamp(1f - acc.FrostKeep, 0f, 1f);
        bs.ShaperRotation = acc.ShaperRotation;
    }

    /// <summary>
    /// A colour-wheel slot of an LED engine rendered as a preset (see <see cref="ProfileBeam.PresetOutput"/>): the light
    /// takes the slot's chromaticity, and its luminance is the current mix's times the share of the reference flux the
    /// emitters make at that colour — dark when the emitters are at 0, the engine's saturated preset at full. A split
    /// position blends the two slots (an open slot keeps the mix's colour); <paramref name="saturation"/> &lt; 1 (an
    /// HSB saturation of virtual gels) blends back towards the mix. False when a slot involved is outside the
    /// emitters' gamut, which leaves the filter model to the caller.
    /// </summary>
    private static bool TryPreset(ProfileWheel wheel, float[] output, in WheelState w, Vector3 rgb, float saturation, out Vector3 result)
    {
        result = rgb;
        int i0 = w.Slot - 1, i1 = w.Blend > 0f && w.NextSlot > 0 ? w.NextSlot - 1 : i0;
        var open = wheel.SlotOpenArray;
        float o0 = output[i0], o1 = output[i1];
        if ((!open[i0] && float.IsNaN(o0)) || (!open[i1] && float.IsNaN(o1))) return false;
        var mix = ColorSpaces.Rec2020ToXyz(rgb);
        float luminance = mix.Y;
        if (!(luminance > 0f) || !float.IsFinite(luminance))
        {
            result = Vector3.Zero;
            return true;
        }
        var mixUnit = mix / luminance;
        var xyz = wheel.SlotColorXyzArray;
        var c0 = open[i0] ? mixUnit : xyz[i0] * o0;
        var c1 = open[i1] ? mixUnit : xyz[i1] * o1;
        var preset = (i1 == i0 ? c0 : Vector3.Lerp(c0, c1, w.Blend)) * luminance;
        if (saturation < 1f) preset = Vector3.Lerp(mix, preset, saturation);
        result = ColorSpaces.XyzToRec2020(preset);
        return true;
    }

    /// <summary>
    /// Elongation (≥ 1) of the beam by its beam shapers' anamorphic lenses — the slot's stretch scaled by the insertion,
    /// blended between slots on split positions — and the direction of the long axis (slot indexing/rotation plus the
    /// wheel angle), rad.
    /// </summary>
    private static float ShaperStretch(FixtureProfile p, BeamState bs, out float rotation)
    {
        float stretch = 1f;
        rotation = 0f;
        foreach (ref readonly var w in bs.BeamShapers.AsSpan())
        {
            if (w.Wheel < 0 || w.Slot <= 0 || w.IsOpen || !(w.Insertion > 0f)) continue;
            var s = p.WheelArray[w.Wheel].SlotStretchArray;
            float a = s[w.Slot - 1];
            if (w.Blend > 0f && w.NextSlot > 0) a += (s[w.NextSlot - 1] - a) * w.Blend;
            a = 1f + (a - 1f) * Math.Clamp(w.Insertion, 0f, 1f);
            if (!(a > stretch) || !float.IsFinite(a)) continue;
            stretch = a;
            float r = (float.IsFinite(w.Rotation) ? w.Rotation : 0f) + (float.IsFinite(w.WheelAngle) ? w.WheelAngle : 0f);
            rotation = r;
        }
        return stretch;
    }

    private static float ApplyCurve(float v, DimmerCurve curve) => curve switch
    {
        DimmerCurve.Square => v * v,
        DimmerCurve.InverseSquare => MathF.Sqrt(v),
        DimmerCurve.SCurve => v * v * (3f - 2f * v),
        _ => v,
    };

    private static Vector3 RgbToXyz(FixtureProfile p, Vector3 rgb)
    {
        var prim = p.ColorSpacePrimaries;
        return prim[0] * Math.Clamp(rgb.X, 0f, 1f) + prim[1] * Math.Clamp(rgb.Y, 0f, 1f) + prim[2] * Math.Clamp(rgb.Z, 0f, 1f);
    }

    /// <summary>HSB (hue in degrees, saturation and brightness 0 … 1) to RGB 0 … 1.</summary>
    internal static Vector3 HsbToRgb(float hue, float saturation, float brightness)
    {
        float h = float.IsFinite(hue) ? hue - 360f * MathF.Floor(hue / 360f) : 0f;
        float s = Math.Clamp(saturation, 0f, 1f), b = Math.Clamp(brightness, 0f, 1f);
        float c = b * s;
        float hp = h / 60f;
        float x = c * (1f - MathF.Abs(hp % 2f - 1f));
        Vector3 rgb = hp switch
        {
            < 1f => new(c, x, 0f),
            < 2f => new(x, c, 0f),
            < 3f => new(0f, c, x),
            < 4f => new(0f, x, c),
            < 5f => new(x, 0f, c),
            _ => new(c, 0f, x),
        };
        return rgb + new Vector3(b - c);
    }

    /// <summary>Shifts a colour by Δuv perpendicular to the Planckian locus (approximated by the CIE 1960 v axis), keeping luminance.</summary>
    private static Vector3 ApplyTint(Vector3 rgb, float duv)
    {
        var xyz = ColorSpaces.Rec2020ToXyz(rgb);
        float sum = xyz.X + xyz.Y + xyz.Z;
        if (!(xyz.Y > 0f) || !(sum > 0f)) return rgb;
        var uv = ColorSpaces.XyToUv1960(new Vector2(xyz.X / sum, xyz.Y / sum));
        uv.Y += Math.Clamp(duv, -0.05f, 0.05f);
        var xy = ColorSpaces.Uv1960ToXy(uv);
        if (!(xy.Y > 1e-4f)) return rgb;
        var shifted = new Vector3(xy.X / xy.Y, 1f, (1f - xy.X - xy.Y) / xy.Y) * xyz.Y;
        return ColorSpaces.XyzToRec2020(shifted);
    }

    // ------------------------------------------------------------------ device outputs

    private static DeviceOutputs Outputs(FixtureProfile p, ProfileFunction[] fns, ChannelRuntime[] rt)
    {
        var o = new DeviceOutputs { FanDirection = 1f, FlameHeight = float.NaN };
        foreach (int c in p.OutputChannels)
        {
            ref var r = ref rt[c];
            if (r.Function < 0) continue;
            var f = fns[r.Function];
            var set = r.SetIndex >= 0 ? f.SetArray[r.SetIndex] : null;
            bool off = set is not null && (set.Flags & (ChannelSetFlags.Closed | ChannelSetFlags.NoFunction)) != 0;
            float level = off ? 0f : Level(f, MathF.Abs(r.Value)) * r.Relation;
            if (c == p.FlameChannel)
            {
                o.Flame = MathF.Max(o.Flame, level);
                continue;
            }
            switch (f.Class.Kind)
            {
                case AttributeKind.Fog: o.Fog = MathF.Max(o.Fog, level); break;
                case AttributeKind.Haze: o.Haze = MathF.Max(o.Haze, level); break;
                case AttributeKind.Fan:
                    o.FanSpeed = MathF.Max(o.FanSpeed, level);
                    if (r.Physical < 0f) o.FanDirection = -1f;
                    break;
                case AttributeKind.FanDirection:
                    if (r.Physical < 0f || (set is not null && set.Name.Contains("rev", StringComparison.OrdinalIgnoreCase))) o.FanDirection = -1f;
                    break;
                case AttributeKind.Flame: o.Flame = MathF.Max(o.Flame, level); break;
                case AttributeKind.FlameHeight: o.FlameHeight = r.Value; break;
            }
        }
        return o;
    }
}
