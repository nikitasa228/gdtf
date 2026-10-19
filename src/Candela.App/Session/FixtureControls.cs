using System.Globalization;
using System.Numerics;
using Candela.App.Ui;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Dmx;
using Candela.Engine;
using Candela.Gdtf;

namespace Candela.App.Session;

/// <summary>Groups of the manual fixture controls in the properties panel, in display order.</summary>
internal enum ControlGroup
{
    /// <summary>Dimmer, shutter, strobe.</summary>
    Intensity,

    /// <summary>Pan, tilt, rotations, translations.</summary>
    Position,

    /// <summary>Emitters, CMY, colour wheels, CTO/CTC, HSB, CIE.</summary>
    Colour,

    /// <summary>Zoom, focus, iris, frost.</summary>
    Beam,

    /// <summary>Gobo, prism, animation and effects wheels.</summary>
    GoboEffects,

    /// <summary>Framing blades and beam shapers.</summary>
    Shapers,

    /// <summary>Fog, haze, fans, flames.</summary>
    Atmosphere,

    /// <summary>Everything else of the DMX mode (control channels, speeds, modes).</summary>
    Other,
}

/// <summary>
/// One manual control of a fixture: a GDTF attribute of its DMX mode with the range of its control value (physical
/// unit of the GDTF file; wheel selection: 1-based slot position) and how the panel shows it. Live and manual values
/// are refreshed every frame by <see cref="FixtureControls.Refresh"/> without allocating.
/// </summary>
internal sealed class FixtureControl
{
    /// <summary>GDTF attribute name (the key of <see cref="FixtureComponent.ManualAttributes"/>).</summary>
    public required string Attribute { get; init; }

    /// <summary>What the attribute is (dimmer, pan, wheel, …).</summary>
    public required AttributeClass Class { get; init; }

    /// <summary>Panel group.</summary>
    public required ControlGroup Group { get; init; }

    /// <summary>Localised label (rebuilt with the language).</summary>
    public string Label { get; set; } = "";

    /// <summary>Smallest control value the mode can produce, in the attribute's unit.</summary>
    public float Min { get; init; }

    /// <summary>Largest control value the mode can produce, in the attribute's unit.</summary>
    public float Max { get; init; }

    /// <summary>GDTF unit of the control value.</summary>
    public PhysicalUnit Unit { get; init; }

    /// <summary>Factor from the control value to the displayed number (100 for 0 … 1 levels shown in percent).</summary>
    public float DisplayScale { get; init; } = 1f;

    /// <summary>printf format of the slider (displayed number).</summary>
    public string Format { get; init; } = "%.2f";

    /// <summary>Displayed numbers smaller than this (half the last digit of <see cref="Format"/>) are shown as 0, never "-0.0".</summary>
    public float ZeroBand { get; init; } = 0.005f;

    /// <summary>Wheel slot names for slot selection (combo box), else null.</summary>
    public string[]? Choices { get; init; }

    /// <summary>Control value (slot position) of each choice.</summary>
    public float[]? ChoiceValues { get; init; }

    /// <summary>ImGui id of the control ("##ctl-Attribute").</summary>
    public required string Id { get; init; }

    /// <summary>Current control value of the fixture (NaN when the attribute's channel runs another function).</summary>
    public float Live { get; set; } = float.NaN;

    /// <summary>The manual value stored in the fixture, or null.</summary>
    public float? Manual { get; set; }

    /// <summary>The value the control shows: the manual value, else the live value, else the minimum.</summary>
    public float Shown => Manual ?? (float.IsFinite(Live) ? Live : Min);

    /// <summary>True when the control has neither a manual value nor a live value (its channel runs another function).</summary>
    public bool IsUnknown => Manual is null && !float.IsFinite(Live);

    /// <summary>True when the control's range is a single value (nothing to set).</summary>
    public bool IsConstant => !(Max > Min);

    /// <summary>Index of the choice nearest to <paramref name="value"/> (−1 without choices).</summary>
    public int ChoiceIndex(float value)
    {
        if (ChoiceValues is not { Length: > 0 } values || !float.IsFinite(value))
            return -1;
        int best = 0;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < values.Length; i++)
        {
            float d = MathF.Abs(values[i] - value);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = i;
            }
        }

        return best;
    }
}

/// <summary>Physical state of a fixture for the "current state" block of the properties panel (SI units, radians).</summary>
internal struct FixtureLiveState
{
    /// <summary>True when the fixture has an evaluated DMX state.</summary>
    public bool Valid;

    /// <summary>Number of beams and of beams giving light.</summary>
    public int Beams, LitBeams;

    /// <summary>Intensity 0 … 1 of the brightest beam (dimmer × shutter × relations).</summary>
    public float Intensity;

    /// <summary>Peak luminous intensity of the brightest beam, cd.</summary>
    public float PeakCandela;

    /// <summary>Luminous flux of all beams, lm.</summary>
    public float Flux;

    /// <summary>Chromaticity of the brightest beam (CIE 1931 xy).</summary>
    public Vector2 Xy;

    /// <summary>Correlated colour temperature, K, and Δuv (NaN when far from the Planckian locus).</summary>
    public float Cct, Duv;

    /// <summary>Display colour (sRGB, gamma encoded, brightest channel 1).</summary>
    public Vector4 Swatch;

    /// <summary>Pan and tilt of the moving axes, rad (NaN without that axis).</summary>
    public float Pan, Tilt;

    /// <summary>Beam and field angle of the brightest beam, rad.</summary>
    public float BeamAngle, FieldAngle;

    /// <summary>Focus 0 … 1, iris 0 … 1 (1 = open), frost 0 … 1.</summary>
    public float Focus, Iris, Frost;

    /// <summary>Strobe frequency, Hz (0 = not strobing).</summary>
    public float Strobe;

    /// <summary>Gobo and prism slots (1-based, 0 = open or none) and prism facets.</summary>
    public int GoboSlot, GoboWheel, PrismFacets;

    /// <summary>Device outputs of atmospheric devices.</summary>
    public DeviceOutputs Outputs;
}

/// <summary>Why a fixture's manual controls are locked (or not).</summary>
internal enum ControlLock
{
    /// <summary>Manual control: the fixture shows its manual values.</summary>
    Manual,

    /// <summary>Cabled to a console in the scene: it follows the DMX of the console's universes.</summary>
    Console,

    /// <summary>No console in the scene; every universe of its patch receives live DMX.</summary>
    LiveDmx,

    /// <summary>The fixture has no DMX state (unknown GDTF, unreadable file, unknown mode).</summary>
    Unresolved,
}

/// <summary>
/// The model behind the fixture part of the properties panel: the manual controls of the fixture's DMX mode (one per
/// attribute, grouped), their live and manual values, the physical state of its beams and axes, and whether the
/// controls are locked because a console drives the fixture (engine status). Rebuilt when the fixture, its GDTF/mode or
/// the language changes; <see cref="Refresh"/> is allocation-free otherwise. Main thread only.
/// </summary>
internal sealed class FixtureControls
{
    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "NoFeature", "Dummy", "Reserved", "Unused", "NoFunction",
    };

    private readonly List<FixtureControl> _controls = new();
    private readonly Dictionary<string, int> _byAttribute = new(StringComparer.OrdinalIgnoreCase);
    private FixtureProfile? _profile;
    private string _language = "";

    /// <summary>The fixture entity shown, or null.</summary>
    public Entity? Entity { get; private set; }

    /// <summary>Its fixture component, or null.</summary>
    public FixtureComponent? Fixture { get; private set; }

    /// <summary>The compiled DMX profile (GDTF + mode), or null when unresolved.</summary>
    public FixtureProfile? Profile => _profile;

    /// <summary>The manual controls in display order (grouped by <see cref="ControlGroup"/>).</summary>
    public IReadOnlyList<FixtureControl> Controls => _controls;

    /// <summary>Lock state of the manual controls.</summary>
    public ControlLock Lock { get; private set; }

    /// <summary>The physical state of the fixture.</summary>
    public FixtureLiveState Live;

    /// <summary>Why the profile could not be resolved (when <see cref="Lock"/> is <see cref="ControlLock.Unresolved"/>).</summary>
    public string? ResolutionError { get; private set; }

    /// <summary>Increases whenever <see cref="Controls"/> was rebuilt (panels drop cached per-control state).</summary>
    public int Generation { get; private set; }

    /// <summary>The control of an attribute, or null.</summary>
    public FixtureControl? Find(string attribute) =>
        attribute is not null && _byAttribute.TryGetValue(attribute, out int i) ? _controls[i] : null;

    /// <summary>Forgets the fixture (selection cleared, scene replaced).</summary>
    public void Clear()
    {
        if (Entity is null && _profile is null)
            return;
        Entity = null;
        Fixture = null;
        _profile = null;
        _controls.Clear();
        _byAttribute.Clear();
        Live = default;
        Generation++;
    }

    /// <summary>
    /// Brings the model up to date for <paramref name="entity"/>: rebuilds the controls when the fixture, its profile or
    /// the UI language changed, then reads the live values, manual values, lock state and physical state.
    /// </summary>
    public void Refresh(StageEngine engine, Entity entity)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(entity);
        var fixture = entity.Get<FixtureComponent>();
        if (fixture is null)
        {
            Clear();
            return;
        }

        var state = engine.Dmx.GetState(entity.Id);
        var profile = state?.Profile;
        if (!ReferenceEquals(entity, Entity) || !ReferenceEquals(fixture, Fixture) || !ReferenceEquals(profile, _profile) ||
            !string.Equals(_language, Loc.Language, StringComparison.Ordinal))
        {
            Entity = entity;
            Fixture = fixture;
            _profile = profile;
            _language = Loc.Language;
            Rebuild(profile);
        }

        if (state is null)
        {
            Lock = ControlLock.Unresolved;
            ResolutionError = engine.Dmx.GetResolution(entity.Id)?.Error;
            Live = default;
            ReadManual(fixture);
            return;
        }

        ResolutionError = null;
        Lock = !engine.Dmx.IsConsoleDriven(entity.Id) ? ControlLock.Manual
            : engine.HasConsoles ? ControlLock.Console
            : ControlLock.LiveDmx;
        ReadManual(fixture);
        for (int i = 0; i < _controls.Count; i++)
        {
            var c = _controls[i];
            c.Live = state.TryGetAttribute(c.Attribute, out float v) ? v : float.NaN;
        }

        ReadLive(state, ref Live);
    }

    private void ReadManual(FixtureComponent fixture)
    {
        var manual = fixture.ManualAttributes;
        for (int i = 0; i < _controls.Count; i++)
        {
            var c = _controls[i];
            c.Manual = manual is not null && manual.TryGetValue(c.Attribute, out float v) && float.IsFinite(v) ? v : null;
        }
    }

    /// <summary>Reads the physical state of the beams and axes (allocation-free).</summary>
    internal static void ReadLive(FixtureState state, ref FixtureLiveState live)
    {
        live = default;
        live.Valid = true;
        live.Pan = float.NaN;
        live.Tilt = float.NaN;
        live.Cct = float.NaN;
        live.Duv = float.NaN;
        var beams = state.Beams;
        live.Beams = beams.Count;
        BeamState? brightest = null;
        for (int i = 0; i < beams.Count; i++)
        {
            var b = beams[i];
            live.Flux += float.IsFinite(b.Flux) ? b.Flux : 0f;
            if (b.PeakCandela > 0f)
                live.LitBeams++;
            if (brightest is null || b.PeakCandela > brightest.PeakCandela)
                brightest = b;
        }

        if (brightest is not null)
        {
            live.Intensity = brightest.Intensity;
            live.PeakCandela = brightest.PeakCandela;
            live.BeamAngle = brightest.BeamAngle;
            live.FieldAngle = brightest.FieldAngle;
            live.Focus = brightest.Focus;
            live.Iris = brightest.Iris;
            live.Frost = brightest.Frost;
            live.Strobe = brightest.Strobing ? brightest.StrobeFrequency : 0f;
            var color = brightest.Color;
            live.Xy = color.ToXy();
            var cct = color.ToCct();
            if (cct.IsMeaningful)
            {
                live.Cct = cct.Kelvin;
                live.Duv = cct.Duv;
            }

            live.Swatch = DisplayColor(color.ToRec709());
            for (int i = 0; i < brightest.Gobos.Length; i++)
            {
                ref readonly var g = ref brightest.Gobos[i];
                if (g.Wheel >= 0 && !g.IsOpen && g.Slot > 0)
                {
                    live.GoboSlot = g.Slot;
                    live.GoboWheel = g.Wheel;
                    break;
                }
            }

            live.PrismFacets = brightest.PrismFacets;
        }

        var axes = state.Axes;
        for (int i = 0; i < axes.Length; i++)
        {
            ref readonly var a = ref axes[i];
            if (a.Pan != 0f || float.IsNaN(live.Pan))
                live.Pan = float.IsNaN(live.Pan) || MathF.Abs(a.Pan) > MathF.Abs(live.Pan) ? a.Pan : live.Pan;
            if (a.Tilt != 0f || float.IsNaN(live.Tilt))
                live.Tilt = float.IsNaN(live.Tilt) || MathF.Abs(a.Tilt) > MathF.Abs(live.Tilt) ? a.Tilt : live.Tilt;
        }

        live.Outputs = state.Outputs;
    }

    /// <summary>A light colour as a display swatch: linear Rec.709 normalised to its brightest channel, sRGB encoded.</summary>
    internal static Vector4 DisplayColor(Vector3 linearRec709)
    {
        var c = Vector3.Max(linearRec709, Vector3.Zero);
        float max = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
        if (!(max > 0f) || !float.IsFinite(max))
            return new Vector4(0f, 0f, 0f, 1f);
        c /= max;
        return new Vector4(Encode(c.X), Encode(c.Y), Encode(c.Z), 1f);

        static float Encode(float v) => v <= 0.0031308f ? 12.92f * v : 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f;
    }

    private void Rebuild(FixtureProfile? profile)
    {
        _controls.Clear();
        _byAttribute.Clear();
        Generation++;
        if (profile is null)
            return;

        // One control per attribute of the mode (instanced channels share it: a plain attribute sets every instance).
        var ranges = new Dictionary<string, (float Min, float Max, PhysicalUnit Unit, int Wheel, AttributeClass Class, string Name)>(
            StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var f in profile.Functions)
        {
            string attribute = f.Attribute;
            if (string.IsNullOrWhiteSpace(attribute) || Placeholders.Contains(attribute))
                continue;
            float lo = MathF.Min(f.MinValue, f.MaxValue), hi = MathF.Max(f.MinValue, f.MaxValue);
            if (!float.IsFinite(lo) || !float.IsFinite(hi))
                continue;
            if (ranges.TryGetValue(attribute, out var r))
            {
                ranges[attribute] = (MathF.Min(r.Min, lo), MathF.Max(r.Max, hi), r.Unit, r.Wheel >= 0 ? r.Wheel : f.UsesSlotPosition ? f.Wheel : -1,
                    r.Class, r.Name);
            }
            else
            {
                ranges[attribute] = (lo, hi, f.Unit, f.UsesSlotPosition ? f.Wheel : -1, f.Class, attribute);
                order.Add(attribute);
            }
        }

        var built = new List<FixtureControl>(order.Count);
        foreach (string attribute in order)
        {
            var (min, max, unit, wheel, cls, name) = ranges[attribute];
            built.Add(CreateControl(profile, name, cls, min, max, unit, wheel));
        }

        // Stable order: by group, the main attribute of each group first (dimmer, pan, tilt, zoom …), then the mode's channel order.
        var indexed = built.Select((c, i) => (c, i)).OrderBy(t => t.c.Group).ThenBy(t => Rank(t.c.Class)).ThenBy(t => t.i).Select(t => t.c);
        foreach (var c in indexed)
        {
            _byAttribute[c.Attribute] = _controls.Count;
            _controls.Add(c);
        }
    }

    private static FixtureControl CreateControl(FixtureProfile profile, string attribute, AttributeClass cls, float min, float max,
        PhysicalUnit unit, int wheel)
    {
        string[]? choices = null;
        float[]? values = null;
        if (wheel >= 0 && wheel < profile.Wheels.Count)
        {
            var w = profile.Wheels[wheel];
            int n = w.SlotCount;
            choices = new string[n];
            values = new float[n];
            var slots = w.Source.Slots;
            for (int i = 0; i < n; i++)
            {
                string slotName = i < slots.Count && !string.IsNullOrWhiteSpace(slots[i].Name) ? slots[i].Name : "";
                choices[i] = string.Create(CultureInfo.InvariantCulture, $"{i + 1}  {slotName}").TrimEnd();
                values[i] = i + 1;
            }
        }

        // Colour-temperature filters often give kelvin physicals without declaring the unit (Rivale: CTO 6500 … 2800).
        if (unit == PhysicalUnit.None && cls.Kind is AttributeKind.Cto or AttributeKind.Ctb or AttributeKind.Ctc && MathF.Min(min, max) >= 1000f)
            unit = PhysicalUnit.Temperature;
        var (scale, levelFormat) = LevelDisplay(cls, unit, min, max);
        var units = StageUnits.Current;
        string format = levelFormat ?? unit switch
        {
            PhysicalUnit.Angle => "%.1f°",
            PhysicalUnit.Percent => PercentFormat(max - min),
            PhysicalUnit.Frequency => units.FormatHertz,
            PhysicalUnit.Temperature => "%.0f K",
            PhysicalUnit.Length => units.FormatMetres,
            PhysicalUnit.Time => units.FormatSeconds,
            PhysicalUnit.AngularSpeed => "%.0f°/s",
            PhysicalUnit.Speed => units.FormatMetresPerSecond,
            PhysicalUnit.WaveLength => units.FormatNanometres,
            PhysicalUnit.LuminousIntensity => units.FormatCandela,
            _ => wheel >= 0 ? "%.2f" : NumberFormat(min, max),
        };

        var control = new FixtureControl
        {
            Attribute = attribute,
            Class = cls,
            Group = GroupOf(cls),
            Min = min,
            Max = max,
            Unit = unit,
            DisplayScale = scale,
            Format = format,
            ZeroBand = ZeroBandOf(format),
            Choices = choices,
            ChoiceValues = values,
            Id = "##ctl-" + attribute,
        };
        control.Label = AttributeLabels.Label(attribute, cls);
        return control;
    }

    /// <summary>Order of attribute kinds inside their group (lower first); everything else keeps the channel order.</summary>
    internal static int Rank(AttributeClass cls) => cls.Kind switch
    {
        // Framing blades by number, A before B, then rotation and softness.
        AttributeKind.BladeA => 100 + cls.Number * 4,
        AttributeKind.BladeB => 101 + cls.Number * 4,
        AttributeKind.BladeRotation => 102 + cls.Number * 4,
        AttributeKind.BladeSoft => 103 + cls.Number * 4,
        AttributeKind.ShaperRotation => 200,
        AttributeKind.Dimmer => 0,
        AttributeKind.Shutter => 1,
        AttributeKind.Pan => 0,
        AttributeKind.Tilt => 1,
        AttributeKind.Zoom => 0,
        AttributeKind.Focus => 1,
        AttributeKind.Iris => 2,
        AttributeKind.Frost => 3,
        AttributeKind.ColorAdd or AttributeKind.ColorSub or AttributeKind.ColorRgb => 0,
        AttributeKind.WheelSelect => 1,
        AttributeKind.Ctc or AttributeKind.Cto or AttributeKind.Ctb => 2,
        _ => 10,
    };

    /// <summary>
    /// How a relative level is shown, or (1, null) when the value is not a level. GDTF files give levels as fractions
    /// 0 … 1 with no unit, as colour components, as <c>Percent</c> or — the GDTF attribute definition of
    /// <c>Dimmer</c> — as <c>LuminousIntensity</c>; none of them is a candela value, so they are shown in percent
    /// (× 100). A dimmer or <c>Percent</c> attribute whose physical range is already 0 … 100 is shown as is.
    /// </summary>
    /// <returns>Factor from the control value to the displayed number, and the printf format (null: not a level).</returns>
    internal static (float Scale, string? Format) LevelDisplay(AttributeClass cls, PhysicalUnit unit, float min, float max)
    {
        if (!(max > min) || !float.IsFinite(min) || !float.IsFinite(max))
            return (1f, null);
        bool unitless = unit is PhysicalUnit.None or PhysicalUnit.ColorComponent or PhysicalUnit.Percent or PhysicalUnit.LuminousIntensity;
        if (unitless && min >= -0.0001f && max <= 1.0001f)
            return (100f, PercentFormat((max - min) * 100f));
        bool percentRange = min >= -0.0001f && max <= 100.0001f;
        if (percentRange && (unit == PhysicalUnit.Percent || unitless && cls.Kind == AttributeKind.Dimmer))
            return (1f, PercentFormat(max - min));
        return (1f, null);
    }

    /// <summary>Half the last displayed digit of a printf format ("%.1f°" → 0.05); 0.5 without decimals.</summary>
    internal static float ZeroBandOf(string format)
    {
        int dot = format.IndexOf("%.", StringComparison.Ordinal);
        int decimals = dot >= 0 && dot + 2 < format.Length && char.IsAsciiDigit(format[dot + 2]) ? format[dot + 2] - '0' : 0;
        return 0.5f * MathF.Pow(10f, -decimals);
    }

    /// <summary>
    /// A displayed number with the values that round to zero made exactly zero: a pan of −0.00004° must read "0.0°",
    /// not "-0.0°".
    /// </summary>
    internal static float CleanZero(float value, float band) => MathF.Abs(value) < band ? 0f : value;

    /// <summary>A percent format with decimals for small spans ("%.0f%%", "%.1f%%", "%.2f%%").</summary>
    internal static string PercentFormat(float span)
    {
        span = MathF.Abs(span);
        return span >= 10f ? "%.0f%%" : span >= 1f ? "%.1f%%" : "%.2f%%";
    }

    /// <summary>A printf format with a sensible number of decimals for the range (no exponent notation).</summary>
    internal static string NumberFormat(float min, float max)
    {
        float span = MathF.Max(MathF.Abs(max - min), MathF.Max(MathF.Abs(min), MathF.Abs(max)) * 0.01f);
        return span >= 100f ? "%.0f" : span >= 10f ? "%.1f" : span >= 1f ? "%.2f" : "%.3f";
    }

    /// <summary>The panel group of an attribute class.</summary>
    internal static ControlGroup GroupOf(AttributeClass cls) => cls.Kind switch
    {
        AttributeKind.Dimmer or AttributeKind.Shutter or AttributeKind.Strobe or AttributeKind.StrobeRate or AttributeKind.StrobeDuration
            or AttributeKind.StrobeMode or AttributeKind.DimmerCurve => ControlGroup.Intensity,
        AttributeKind.Pan or AttributeKind.Tilt or AttributeKind.PanRotate or AttributeKind.TiltRotate or AttributeKind.Translation
            or AttributeKind.Rotation => ControlGroup.Position,
        AttributeKind.ColorAdd or AttributeKind.ColorSub or AttributeKind.ColorRgb or AttributeKind.HsbHue or AttributeKind.HsbSaturation
            or AttributeKind.HsbBrightness or AttributeKind.CieX or AttributeKind.CieY or AttributeKind.CieBrightness or AttributeKind.Cto
            or AttributeKind.Ctb or AttributeKind.Ctc or AttributeKind.Tint or AttributeKind.ColorCrossfade or AttributeKind.ColorMacro
            => ControlGroup.Colour,
        AttributeKind.Zoom or AttributeKind.Focus or AttributeKind.FocusDistance or AttributeKind.Iris or AttributeKind.IrisPulse
            or AttributeKind.Frost or AttributeKind.FrostPulse => ControlGroup.Beam,
        AttributeKind.BladeA or AttributeKind.BladeB or AttributeKind.BladeRotation or AttributeKind.BladeSoft
            or AttributeKind.ShaperRotation => ControlGroup.Shapers,
        AttributeKind.Fog or AttributeKind.Haze or AttributeKind.Fan or AttributeKind.FanDirection or AttributeKind.Flame
            or AttributeKind.FlameHeight => ControlGroup.Atmosphere,
        AttributeKind.WheelSelect or AttributeKind.WheelSelectSpin or AttributeKind.WheelSelectShake or AttributeKind.WheelIndex
            or AttributeKind.WheelSpin or AttributeKind.WheelShake or AttributeKind.WheelRandom or AttributeKind.SlotIndex
            or AttributeKind.SlotRotate or AttributeKind.SlotShake or AttributeKind.EffectRate or AttributeKind.AnimationInsertion
            => cls.Wheel == WheelFamily.Color ? ControlGroup.Colour
                : cls.Wheel == WheelFamily.BeamShaper ? ControlGroup.Shapers
                : ControlGroup.GoboEffects,
        _ => ControlGroup.Other,
    };
}
