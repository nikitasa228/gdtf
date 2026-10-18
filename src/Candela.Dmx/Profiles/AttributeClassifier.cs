using System.Globalization;

namespace Candela.Dmx;

/// <summary>
/// What a GDTF attribute does to the physical fixture, as far as the DMX layer models it. Attributes that only switch
/// fixture-internal behaviour (resets, display, fan modes, …) are <see cref="Ignored"/>; see <c>docs/dmx.md</c> for the
/// full list and the reasons.
/// </summary>
public enum AttributeKind
{
    /// <summary>Not modelled: the attribute does not change light output, position or a device output.</summary>
    Ignored,

    /// <summary><c>Dimmer</c>: intensity 0 … 1 (multiplies along the geometry tree).</summary>
    Dimmer,

    /// <summary><c>Shutter(n)</c>: shutter open/closed (0 … 1).</summary>
    Shutter,

    /// <summary><c>Shutter(n)Strobe…</c>: periodic shutter with a <see cref="Waveform"/>, physical value in Hz.</summary>
    Strobe,

    /// <summary><c>StrobeRate</c>/<c>StrobeFrequency</c>: strobe frequency in Hz for fixtures with a separate rate channel.</summary>
    StrobeRate,

    /// <summary><c>StrobeDuration</c>: flash length in seconds.</summary>
    StrobeDuration,

    /// <summary><c>StrobeMode…</c>: selects the strobe waveform used with <see cref="StrobeRate"/>.</summary>
    StrobeMode,

    /// <summary><c>Pan</c>: rotation of an axis about its local Z, degrees.</summary>
    Pan,

    /// <summary><c>Tilt</c>: rotation of an axis about its local X, degrees.</summary>
    Tilt,

    /// <summary><c>PanRotate</c>: continuous pan rotation, degrees per second.</summary>
    PanRotate,

    /// <summary><c>TiltRotate</c>: continuous tilt rotation, degrees per second.</summary>
    TiltRotate,

    /// <summary><c>XYZ_X/Y/Z</c>: translation of an axis in metres (component in <see cref="AttributeClass.Number"/>: 1 = X).</summary>
    Translation,

    /// <summary><c>Rot_X/Y/Z</c>: rotation of an axis in degrees (component in <see cref="AttributeClass.Number"/>: 1 = X).</summary>
    Rotation,

    /// <summary><c>ColorAdd_*</c>: level 0 … 1 of an additive emitter (code in <see cref="AttributeClass.Code"/>).</summary>
    ColorAdd,

    /// <summary><c>ColorSub_*</c>: insertion 0 … 1 of a subtractive filter (C, M, Y; R, G, B as their complements).</summary>
    ColorSub,

    /// <summary><c>ColorRGB_*</c>: indirect RGB in the fixture's colour space (0 … 1).</summary>
    ColorRgb,

    /// <summary><c>HSB_Hue</c>, degrees.</summary>
    HsbHue,

    /// <summary><c>HSB_Saturation</c>.</summary>
    HsbSaturation,

    /// <summary><c>HSB_Brightness</c>.</summary>
    HsbBrightness,

    /// <summary><c>CIE_X</c>: chromaticity x.</summary>
    CieX,

    /// <summary><c>CIE_Y</c>: chromaticity y.</summary>
    CieY,

    /// <summary><c>CIE_Brightness</c>.</summary>
    CieBrightness,

    /// <summary><c>CTO</c>: colour temperature orange (resulting CCT in K, or a 0 … 1 insertion).</summary>
    Cto,

    /// <summary><c>CTB</c>: colour temperature blue (resulting CCT in K, or a 0 … 1 insertion).</summary>
    Ctb,

    /// <summary><c>CTC</c>: colour temperature correction (white point in K, or a 0 … 1 position).</summary>
    Ctc,

    /// <summary><c>Tint</c>: green/magenta shift of the white point.</summary>
    Tint,

    /// <summary><c>ColorMacro(n)</c>: built-in colours, interpreted by channel set name where meaningful.</summary>
    ColorMacro,

    /// <summary>Wheel slot selection (<c>Color(n)</c>, <c>Gobo(n)</c>, <c>Prism(n)</c>, <c>AnimationWheel(n)</c>, <c>Effects(n)</c>, …).</summary>
    WheelSelect,

    /// <summary>Slot selection with continuous rotation of the slot (<c>Gobo(n)SelectSpin</c>), degrees per second.</summary>
    WheelSelectSpin,

    /// <summary>Slot selection with shaking (<c>Gobo(n)SelectShake</c>), Hz.</summary>
    WheelSelectShake,

    /// <summary>Indexed wheel angle (<c>…WheelIndex</c>), degrees of the whole wheel.</summary>
    WheelIndex,

    /// <summary>Continuous wheel rotation (<c>…WheelSpin</c>), degrees per second.</summary>
    WheelSpin,

    /// <summary>Wheel shake (<c>…WheelShake</c>), Hz.</summary>
    WheelShake,

    /// <summary>Random slot changes (<c>…WheelRandom</c>), Hz.</summary>
    WheelRandom,

    /// <summary>Indexed rotation of the slot item (<c>Gobo(n)Pos</c>, <c>Prism(n)Pos</c>, …), degrees.</summary>
    SlotIndex,

    /// <summary>Continuous rotation of the slot item (<c>…PosRotate</c>), degrees per second.</summary>
    SlotRotate,

    /// <summary>Shake of the slot item's rotation (<c>…PosShake</c>), Hz.</summary>
    SlotShake,

    /// <summary>Rate of an effect (<c>Effects(n)Rate</c>), Hz.</summary>
    EffectRate,

    /// <summary>Insertion of an animation system (<c>AnimationSystem(n)</c>, <c>AnimationSystem(n)Insertion</c>), 0 … 1.</summary>
    AnimationInsertion,

    /// <summary><c>Frost(n)</c>: diffusion 0 … 1.</summary>
    Frost,

    /// <summary><c>Frost(n)PulseOpen/PulseClose/Ramp</c>: periodic frost, Hz.</summary>
    FrostPulse,

    /// <summary><c>Iris</c>: opening 0 … 1 (1 = open).</summary>
    Iris,

    /// <summary><c>IrisStrobe…</c>, <c>IrisPulse…</c>: periodic iris, Hz.</summary>
    IrisPulse,

    /// <summary><c>Zoom</c>: beam angle in degrees.</summary>
    Zoom,

    /// <summary><c>Focus(n)</c>: 0 … 1.</summary>
    Focus,

    /// <summary><c>Focus(n)Distance</c>: focus distance in metres.</summary>
    FocusDistance,

    /// <summary><c>Blade(n)A</c>: insertion of side A of blade n.</summary>
    BladeA,

    /// <summary><c>Blade(n)B</c>: insertion of side B of blade n.</summary>
    BladeB,

    /// <summary><c>Blade(n)Rot</c>: rotation of blade n, degrees.</summary>
    BladeRotation,

    /// <summary><c>BladeSoft(n)A/B</c>: edge softness of blade n, 0 … 1.</summary>
    BladeSoft,

    /// <summary><c>ShaperRot</c>: rotation of the whole shaper module, degrees.</summary>
    ShaperRotation,

    /// <summary><c>DimmerCurve</c>: selects the dimmer law by channel set name.</summary>
    DimmerCurve,

    /// <summary><c>…MSpeed</c>: movement speed of a group of motors (scales the motor slew rate).</summary>
    MotionSpeed,

    /// <summary><c>Fog(n)</c>: fog output 0 … 1.</summary>
    Fog,

    /// <summary><c>Haze(n)</c>: haze output 0 … 1.</summary>
    Haze,

    /// <summary><c>Fan(n)</c>, <c>Blower(n)</c>: fan speed 0 … 1 (cooling fans of lights are told apart by the profile).</summary>
    Fan,

    /// <summary>Fan direction (<c>Fan(n)Direction</c>): sign of the physical value.</summary>
    FanDirection,

    /// <summary><c>Flame(n)</c>, <c>Fire(n)</c>: flame output 0 … 1.</summary>
    Flame,

    /// <summary><c>Flame(n)Height</c>: flame height in metres.</summary>
    FlameHeight,
}

/// <summary>Which wheel-like mechanism a wheel attribute belongs to.</summary>
public enum WheelFamily
{
    /// <summary>Not a wheel attribute.</summary>
    None,

    /// <summary>Colour wheel (<c>Color(n)</c>).</summary>
    Color,

    /// <summary>Gobo wheel (<c>Gobo(n)</c>).</summary>
    Gobo,

    /// <summary>Prism (<c>Prism(n)</c>).</summary>
    Prism,

    /// <summary>Animation wheel (<c>AnimationWheel(n)</c>).</summary>
    Animation,

    /// <summary>Animation system (<c>AnimationSystem(n)</c>, GDTF 1.2).</summary>
    AnimationSystem,

    /// <summary>Effects wheel or built-in effect (<c>Effects(n)</c>).</summary>
    Effects,

    /// <summary>Beam shaper (<c>BeamShaper</c>).</summary>
    BeamShaper,
}

/// <summary>Time shape of strobe, pulse and random effects (shutter, iris, frost).</summary>
public enum Waveform
{
    /// <summary>No periodic effect (open).</summary>
    None,

    /// <summary>Rectangular flashes (<c>Strobe</c>).</summary>
    Strobe,

    /// <summary>Symmetric fade in and out (<c>StrobePulse</c>).</summary>
    Pulse,

    /// <summary>Opening pulse: fades open, snaps closed (<c>StrobePulseOpen</c>, a ramp up).</summary>
    PulseOpen,

    /// <summary>Closing pulse: snaps open, fades closed (<c>StrobePulseClose</c>, a ramp down).</summary>
    PulseClose,

    /// <summary>Rectangular flashes at random times (<c>StrobeRandom</c>).</summary>
    Random,

    /// <summary>Random symmetric pulses.</summary>
    RandomPulse,

    /// <summary>Opening pulses (ramp up) at random times.</summary>
    RandomPulseOpen,

    /// <summary>Closing pulses (ramp down) at random times.</summary>
    RandomPulseClose,

    /// <summary>Sinusoidal modulation.</summary>
    Sine,

    /// <summary>Bursts of rapid flashes.</summary>
    Burst,
}

/// <summary>
/// Classification of a GDTF attribute name: what it does (<see cref="Kind"/>), which numbered instance it is
/// (<see cref="Number"/>: Gobo<b>2</b>, Blade<b>3</b>A, Shutter<b>1</b>), which wheel family, which emitter/filter code
/// and which waveform.
/// </summary>
/// <param name="Kind">What the attribute controls.</param>
/// <param name="Number">Instance number (1 when the name has none); for translations/rotations the axis (1 = X).</param>
/// <param name="Wheel">Wheel family for wheel attributes.</param>
/// <param name="Waveform">Waveform of strobe/pulse attributes.</param>
/// <param name="Code">Emitter or filter code of colour attributes (<c>R</c>, <c>RY</c>, <c>WW</c>, <c>C</c> …), or the motion group of an MSpeed attribute.</param>
public readonly record struct AttributeClass(AttributeKind Kind, int Number = 1, WheelFamily Wheel = WheelFamily.None,
    Waveform Waveform = Waveform.None, string? Code = null)
{
    /// <summary>The class of attributes that are not modelled.</summary>
    public static AttributeClass Ignored => new(AttributeKind.Ignored);

    /// <summary>True when the attribute changes the fixture's physical state.</summary>
    public bool IsModelled => Kind != AttributeKind.Ignored;
}

/// <summary>
/// Maps GDTF attribute names (DIN SPEC 15800 Annex A naming, including the numbered forms <c>Gobo(n)…</c>) to an
/// <see cref="AttributeClass"/>. Matching ignores case; a few common non-standard spellings found in real files are
/// accepted (e.g. <c>Shutter(n)StrobeBurst</c>, <c>Green / Magenta</c> as tint).
/// </summary>
public static class AttributeClassifier
{
    private static readonly string[] _strobeSuffixes =
    [
        "StrobeRandomPulseOpen", "StrobeRandomPulseClose", "StrobeRandomPulse", "StrobeRandomBurstPulse", "StrobeRandom",
        "StrobePulseOpen", "StrobePulseClose", "StrobePulse", "StrobeEffect", "StrobeBurstPulse", "StrobeBurst",
        "StrobeSineWave", "Strobe",
    ];

    private static readonly Dictionary<string, AttributeClass> _cache = new(StringComparer.Ordinal);
    private static readonly object _cacheLock = new();

    /// <summary>Classifies an attribute name (null or empty: <see cref="AttributeClass.Ignored"/>). Results are cached.</summary>
    public static AttributeClass Classify(string? attributeName)
    {
        if (string.IsNullOrWhiteSpace(attributeName)) return AttributeClass.Ignored;
        lock (_cacheLock)
            if (_cache.TryGetValue(attributeName, out var cached)) return cached;
        var result = ClassifyCore(attributeName.Trim());
        lock (_cacheLock) _cache[attributeName] = result;
        return result;
    }

    private static AttributeClass ClassifyCore(string name)
    {
        var s = name.AsSpan();

        // ---- motor speed (before the wheel prefixes: "GoboWheel1MSpeed", "ColorMixMSpeed")
        if (s.EndsWith("MSpeed", StringComparison.OrdinalIgnoreCase) && s.Length > "MSpeed".Length)
            return new(AttributeKind.MotionSpeed, Code: s[..^"MSpeed".Length].ToString());

        if (IsPanTiltSpeedAlias(s)) return new(AttributeKind.MotionSpeed, Code: "Position");

        // ---- intensity
        if (Is(s, "Dimmer") || Is(s, "Intensity")) return new(AttributeKind.Dimmer);
        if (Is(s, "DimmerCurve")) return new(AttributeKind.DimmerCurve);
        if (Is(s, "StrobeDuration") || Is(s, "ShutterStrobeDuration")) return new(AttributeKind.StrobeDuration);
        if (Is(s, "StrobeRate") || Is(s, "StrobeFrequency")) return new(AttributeKind.StrobeRate);
        if (StartsWith(s, "StrobeMode"))
        {
            var rest = s["StrobeMode".Length..];
            if (Is(rest, "Shutter")) return new(AttributeKind.StrobeMode, Waveform: Waveform.None);
            var w = WaveformOf(rest.ToString(), Waveform.Strobe);
            return new(AttributeKind.StrobeMode, Waveform: w);
        }
        if (TryNumbered(s, "Shutter", out int shutterN, out var shutterRest))
        {
            if (shutterRest.IsEmpty) return new(AttributeKind.Shutter, shutterN);
            foreach (var suffix in _strobeSuffixes)
                if (Is(shutterRest, suffix)) return new(AttributeKind.Strobe, shutterN, Waveform: WaveformOf(suffix[6..], Waveform.Strobe));
            return AttributeClass.Ignored;
        }

        if (TryNumbered(s, "Strobe", out int strobeN, out var strobeRest) && strobeRest.IsEmpty)
            return new(AttributeKind.Strobe, strobeN, Waveform: Waveform.Strobe);

        // ---- position
        if (Is(s, "Pan")) return new(AttributeKind.Pan);
        if (Is(s, "Tilt")) return new(AttributeKind.Tilt);
        if (Is(s, "PanRotate")) return new(AttributeKind.PanRotate);
        if (Is(s, "TiltRotate")) return new(AttributeKind.TiltRotate);
        if (StartsWith(s, "XYZ_") && s.Length == 5) return Axis(AttributeKind.Translation, s[4]);
        if (StartsWith(s, "Rot_") && s.Length == 5) return Axis(AttributeKind.Rotation, s[4]);

        // ---- colour
        if (StartsWith(s, "ColorAdd_")) return new(AttributeKind.ColorAdd, Code: s["ColorAdd_".Length..].ToString().ToUpperInvariant());
        if (StartsWith(s, "ColorSub_")) return new(AttributeKind.ColorSub, Code: s["ColorSub_".Length..].ToString().ToUpperInvariant());
        if (StartsWith(s, "ColorRGB_"))
        {
            string? code = RgbCode(s["ColorRGB_".Length..]);
            return code is null ? AttributeClass.Ignored : new(AttributeKind.ColorRgb, Code: code);
        }
        if (Is(s, "HSB_Hue")) return new(AttributeKind.HsbHue);
        if (Is(s, "HSB_Saturation")) return new(AttributeKind.HsbSaturation);
        if (Is(s, "HSB_Brightness")) return new(AttributeKind.HsbBrightness);
        if (Is(s, "CIE_X")) return new(AttributeKind.CieX);
        if (Is(s, "CIE_Y")) return new(AttributeKind.CieY);
        if (Is(s, "CIE_Brightness")) return new(AttributeKind.CieBrightness);
        if (Is(s, "CTO")) return new(AttributeKind.Cto);
        if (Is(s, "CTB")) return new(AttributeKind.Ctb);
        if (Is(s, "CTC") || IsCctAlias(s)) return new(AttributeKind.Ctc);
        if (Is(s, "Tint") || IsTintAlias(s)) return new(AttributeKind.Tint);
        if (TryNumbered(s, "ColorMacro", out int macroN, out var macroRest) && macroRest.IsEmpty) return new(AttributeKind.ColorMacro, macroN);

        // ---- wheels (longer prefixes first)
        if (TryNumbered(s, "AnimationWheel", out int awN, out var awRest)) return Wheel(WheelFamily.Animation, awN, awRest);
        if (TryNumbered(s, "AnimationSystem", out int asN, out var asRest))
        {
            if (asRest.IsEmpty || Is(asRest, "Insertion")) return new(AttributeKind.AnimationInsertion, asN, WheelFamily.AnimationSystem);
            if (Is(asRest, "Shake")) return new(AttributeKind.WheelShake, asN, WheelFamily.AnimationSystem);
            return Wheel(WheelFamily.AnimationSystem, asN, asRest);
        }
        if (TryNumbered(s, "Gobo", out int goboN, out var goboRest)) return Wheel(WheelFamily.Gobo, goboN, goboRest);
        if (TryNumbered(s, "Color", out int colorN, out var colorRest))
        {
            if (colorRest.IsEmpty || !StartsWith(colorRest, "Mode")) return Wheel(WheelFamily.Color, colorN, colorRest);
            return AttributeClass.Ignored;
        }
        if (TryNumbered(s, "Prism", out int prismN, out var prismRest)) return Wheel(WheelFamily.Prism, prismN, prismRest);
        if (TryNumbered(s, "Effects", out int fxN, out var fxRest))
        {
            if (Is(fxRest, "Rate")) return new(AttributeKind.EffectRate, fxN, WheelFamily.Effects);
            return Wheel(WheelFamily.Effects, fxN, fxRest);
        }
        if (StartsWith(s, "BeamShaper"))
        {
            var rest = s["BeamShaper".Length..];
            return Wheel(WheelFamily.BeamShaper, 1, rest);
        }

        // ---- beam
        if (TryNumbered(s, "Frost", out int frostN, out var frostRest))
        {
            if (frostRest.IsEmpty) return new(AttributeKind.Frost, frostN);
            if (Is(frostRest, "PulseOpen")) return new(AttributeKind.FrostPulse, frostN, Waveform: Waveform.PulseOpen);
            if (Is(frostRest, "PulseClose")) return new(AttributeKind.FrostPulse, frostN, Waveform: Waveform.PulseClose);
            if (Is(frostRest, "Ramp")) return new(AttributeKind.FrostPulse, frostN, Waveform: Waveform.Pulse);
            return AttributeClass.Ignored;
        }
        if (Is(s, "Iris")) return new(AttributeKind.Iris);
        if (StartsWith(s, "Iris"))
        {
            var rest = s["Iris".Length..].ToString();
            return rest switch
            {
                _ when Eq(rest, "Strobe") => new(AttributeKind.IrisPulse, Waveform: Waveform.Strobe),
                _ when Eq(rest, "StrobeRandom") => new(AttributeKind.IrisPulse, Waveform: Waveform.Random),
                _ when Eq(rest, "PulseOpen") => new(AttributeKind.IrisPulse, Waveform: Waveform.PulseOpen),
                _ when Eq(rest, "PulseClose") => new(AttributeKind.IrisPulse, Waveform: Waveform.PulseClose),
                _ when Eq(rest, "RandomPulseOpen") => new(AttributeKind.IrisPulse, Waveform: Waveform.RandomPulseOpen),
                _ when Eq(rest, "RandomPulseClose") => new(AttributeKind.IrisPulse, Waveform: Waveform.RandomPulseClose),
                _ => AttributeClass.Ignored,
            };
        }
        if (Is(s, "Zoom") || Is(s, "ZoomModeSpot") || Is(s, "ZoomModeBeam")) return new(AttributeKind.Zoom);
        if (TryNumbered(s, "Focus", out int focusN, out var focusRest))
        {
            if (focusRest.IsEmpty) return new(AttributeKind.Focus, focusN);
            if (Is(focusRest, "Distance")) return new(AttributeKind.FocusDistance, focusN);
            return AttributeClass.Ignored;
        }

        // ---- shapers
        if (TryNumbered(s, "BladeSoft", out int softN, out var softRest) && (Is(softRest, "A") || Is(softRest, "B")))
            return new(AttributeKind.BladeSoft, softN, Code: softRest.ToString().ToUpperInvariant());
        if (TryNumbered(s, "Blade", out int bladeN, out var bladeRest))
        {
            if (Is(bladeRest, "A")) return new(AttributeKind.BladeA, bladeN);
            if (Is(bladeRest, "B")) return new(AttributeKind.BladeB, bladeN);
            if (Is(bladeRest, "Rot")) return new(AttributeKind.BladeRotation, bladeN);
            return AttributeClass.Ignored;
        }
        if (Is(s, "ShaperRot")) return new(AttributeKind.ShaperRotation);

        // ---- device outputs
        if (TryNumbered(s, "Fog", out int fogN, out var fogRest) && fogRest.IsEmpty) return new(AttributeKind.Fog, fogN);
        if (TryNumbered(s, "Haze", out int hazeN, out var hazeRest) && hazeRest.IsEmpty) return new(AttributeKind.Haze, hazeN);
        if (TryNumbered(s, "Fan", out int fanN, out var fanRest))
        {
            if (fanRest.IsEmpty) return new(AttributeKind.Fan, fanN);
            if (Is(fanRest, "Direction")) return new(AttributeKind.FanDirection, fanN);
            return AttributeClass.Ignored;
        }
        if (TryNumbered(s, "Blower", out int blowerN, out var blowerRest) && blowerRest.IsEmpty) return new(AttributeKind.Fan, blowerN, Code: "Blower");
        foreach (var prefix in (ReadOnlySpan<string>)["Flame", "Fire"])
        {
            if (!TryNumbered(s, prefix, out int flameN, out var flameRest)) continue;
            if (flameRest.IsEmpty || Is(flameRest, "Output") || Is(flameRest, "Intensity")) return new(AttributeKind.Flame, flameN);
            if (Is(flameRest, "Height")) return new(AttributeKind.FlameHeight, flameN);
        }
        return AttributeClass.Ignored;
    }

    private static AttributeClass Wheel(WheelFamily family, int n, ReadOnlySpan<char> rest)
    {
        if (rest.IsEmpty) return new(AttributeKind.WheelSelect, n, family);
        string r = rest.ToString();
        return r switch
        {
            _ when Eq(r, "SelectSpin") => new(AttributeKind.WheelSelectSpin, n, family),
            _ when Eq(r, "SelectShake") => new(AttributeKind.WheelSelectShake, n, family),
            _ when Eq(r, "SelectEffects") => new(AttributeKind.WheelSelect, n, family),
            _ when Eq(r, "WheelIndex") => new(AttributeKind.WheelIndex, n, family),
            _ when Eq(r, "WheelSpin") => new(AttributeKind.WheelSpin, n, family),
            _ when Eq(r, "WheelShake") => new(AttributeKind.WheelShake, n, family),
            _ when Eq(r, "WheelRandom") => new(AttributeKind.WheelRandom, n, family),
            _ when Eq(r, "Pos") => new(AttributeKind.SlotIndex, n, family),
            _ when Eq(r, "PosRotate") => new(AttributeKind.SlotRotate, n, family),
            _ when Eq(r, "PosShake") => new(AttributeKind.SlotShake, n, family),
            _ => AttributeClass.Ignored,
        };
    }

    private static AttributeClass Axis(AttributeKind kind, char axis) => char.ToUpperInvariant(axis) switch
    {
        'X' => new(kind, 1),
        'Y' => new(kind, 2),
        'Z' => new(kind, 3),
        _ => AttributeClass.Ignored,
    };

    private static Waveform WaveformOf(string suffix, Waveform plain) => suffix switch
    {
        _ when Eq(suffix, "") => plain,
        _ when Eq(suffix, "Strobe") => Waveform.Strobe,
        _ when Eq(suffix, "Pulse") => Waveform.Pulse,
        _ when Eq(suffix, "PulseOpen") => Waveform.PulseOpen,
        _ when Eq(suffix, "PulseClose") => Waveform.PulseClose,
        _ when Eq(suffix, "Random") => Waveform.Random,
        _ when Eq(suffix, "RandomPulse") => Waveform.RandomPulse,
        _ when Eq(suffix, "RandomPulseOpen") => Waveform.RandomPulseOpen,
        _ when Eq(suffix, "RandomPulseClose") => Waveform.RandomPulseClose,
        _ when Eq(suffix, "RandomBurstPulse") => Waveform.RandomPulse,
        _ when Eq(suffix, "Effect") => Waveform.Strobe,
        _ when Eq(suffix, "Burst") => Waveform.Burst,
        _ when Eq(suffix, "BurstPulse") => Waveform.Burst,
        _ when Eq(suffix, "SineWave") => Waveform.Sine,
        _ => plain,
    };

    private static string? RgbCode(ReadOnlySpan<char> colour)
    {
        string c = colour.ToString();
        return c switch
        {
            _ when Eq(c, "Red") || Eq(c, "R") => "R",
            _ when Eq(c, "Green") || Eq(c, "G") => "G",
            _ when Eq(c, "Blue") || Eq(c, "B") => "B",
            _ when Eq(c, "Cyan") || Eq(c, "C") => "C",
            _ when Eq(c, "Magenta") || Eq(c, "M") => "M",
            _ when Eq(c, "Yellow") || Eq(c, "Y") => "Y",
            _ => null,
        };
    }

    private static bool IsPanTiltSpeedAlias(ReadOnlySpan<char> s)
    {
        Span<char> buffer = stackalloc char[32];
        int n = Letters(s, buffer);
        if (n < 0) return false;
        var letters = buffer[..n];
        return letters.SequenceEqual("ptspeed") || letters.SequenceEqual("pantiltspeed");
    }

    private static bool IsCctAlias(ReadOnlySpan<char> s)
    {
        Span<char> buffer = stackalloc char[32];
        int n = Letters(s, buffer);
        if (n < 0) return false;
        var letters = buffer[..n];
        return letters.SequenceEqual("cct") || letters.SequenceEqual("colortemperature") || letters.SequenceEqual("colourtemperature");
    }

    private static int Letters(ReadOnlySpan<char> s, Span<char> buffer)
    {
        int n = 0;
        foreach (char ch in s)
        {
            if (!char.IsLetter(ch)) continue;
            if (n == buffer.Length) return -1;
            buffer[n++] = char.ToLowerInvariant(ch);
        }
        return n;
    }

    private static bool IsTintAlias(ReadOnlySpan<char> s)
    {
        Span<char> buffer = stackalloc char[32];
        int n = Letters(s, buffer);
        if (n < 0) return false;
        var letters = buffer[..n];
        return letters.SequenceEqual("greenmagenta") || letters.SequenceEqual("magentagreen") || letters.SequenceEqual("greenmagentapoint");
    }

    /// <summary>
    /// Matches <c>{prefix}{digits}{rest}</c> (digits optional, default 1; a literal <c>(n)</c> also counts as 1).
    /// </summary>
    private static bool TryNumbered(ReadOnlySpan<char> s, string prefix, out int number, out ReadOnlySpan<char> rest)
    {
        number = 1;
        rest = default;
        if (!StartsWith(s, prefix)) return false;
        var tail = s[prefix.Length..];
        int digits = 0;
        while (digits < tail.Length && char.IsAsciiDigit(tail[digits])) digits++;
        if (digits > 0)
        {
            if (!int.TryParse(tail[..digits], NumberStyles.None, CultureInfo.InvariantCulture, out number)) number = 1;
            tail = tail[digits..];
        }
        else if (tail.StartsWith("(n)", StringComparison.OrdinalIgnoreCase))
        {
            tail = tail[3..];
        }
        else if (!tail.IsEmpty && char.IsLower(tail[0]))
        {
            return false; // "Gobos", "Colorful": a different word that merely starts with the prefix
        }
        rest = tail;
        return true;
    }

    private static bool Is(ReadOnlySpan<char> s, string value) => s.Equals(value, StringComparison.OrdinalIgnoreCase);

    private static bool Eq(string s, string value) => string.Equals(s, value, StringComparison.OrdinalIgnoreCase);

    private static bool StartsWith(ReadOnlySpan<char> s, string value) => s.StartsWith(value, StringComparison.OrdinalIgnoreCase);
}
