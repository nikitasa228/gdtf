using System.Globalization;
using System.Text;
using Candela.Dmx;

namespace Candela.App.Ui;

/// <summary>
/// Readable, localised labels for GDTF attribute names in the fixture controls: "Dimmer" → "Intensity",
/// "Gobo2" → "Gobo wheel 2", "ColorAdd_RY" → "Amber", "Shutter1StrobeRandom" → "Strobe (random)". Attributes Candela does not
/// model keep their GDTF name with spaces between words ("Control1" → "Control 1").
/// </summary>
internal static class AttributeLabels
{
    private static readonly Dictionary<string, StageStr> EmitterCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["R"] = StageStr.EmitterRed, ["G"] = StageStr.EmitterGreen, ["B"] = StageStr.EmitterBlue, ["W"] = StageStr.EmitterWhite,
        ["WW"] = StageStr.EmitterWarmWhite, ["CW"] = StageStr.EmitterCoolWhite, ["A"] = StageStr.EmitterAmber, ["RY"] = StageStr.EmitterAmber,
        ["L"] = StageStr.EmitterLime, ["GY"] = StageStr.EmitterLime, ["UV"] = StageStr.EmitterUv, ["C"] = StageStr.EmitterCyan,
        ["M"] = StageStr.EmitterMagenta, ["Y"] = StageStr.EmitterYellow, ["IR"] = StageStr.EmitterInfrared, ["P"] = StageStr.EmitterPurple,
        ["O"] = StageStr.EmitterOrange, ["I"] = StageStr.EmitterIndigo, ["BC"] = StageStr.EmitterCyan, ["GC"] = StageStr.EmitterCyan,
    };

    /// <summary>The label of <paramref name="attribute"/> (classified as <paramref name="cls"/>) in the current language.</summary>
    public static string Label(string attribute, AttributeClass cls)
    {
        string n = cls.Number > 1 ? " " + cls.Number.ToString(CultureInfo.InvariantCulture) : "";
        string wheel = WheelName(cls.Wheel);
        return cls.Kind switch
        {
            AttributeKind.Dimmer => StageLoc.Get(StageStr.AttrDimmer),
            AttributeKind.Shutter => StageLoc.Get(StageStr.AttrShutter) + n,
            AttributeKind.Strobe => StageLoc.Get(StageStr.AttrStrobe) + Waveform(cls.Waveform),
            AttributeKind.StrobeRate => StageLoc.Get(StageStr.AttrStrobeRate),
            AttributeKind.StrobeDuration => StageLoc.Get(StageStr.AttrStrobeDuration),
            AttributeKind.StrobeMode => StageLoc.Get(StageStr.AttrStrobeMode) + Waveform(cls.Waveform),
            AttributeKind.Pan => StageLoc.Get(StageStr.LivePan),
            AttributeKind.Tilt => StageLoc.Get(StageStr.LiveTilt),
            AttributeKind.PanRotate => StageLoc.Get(StageStr.AttrPanRotate),
            AttributeKind.TiltRotate => StageLoc.Get(StageStr.AttrTiltRotate),
            AttributeKind.Translation => StageLoc.Get(StageStr.AttrTranslation) + " " + Axis(cls.Number),
            AttributeKind.Rotation => StageLoc.Get(StageStr.AttrRotation) + " " + Axis(cls.Number),
            AttributeKind.ColorAdd => cls.Code is { } code && EmitterCodes.TryGetValue(code, out var emitter)
                ? StageLoc.Get(emitter)
                : StageLoc.Get(StageStr.AttrEmitter) + " " + cls.Code,
            AttributeKind.ColorSub => StageLoc.Get(StageStr.AttrColorSub) + " " + (cls.Code ?? ""),
            AttributeKind.ColorRgb => "RGB " + (cls.Code ?? ""),
            AttributeKind.HsbHue => StageLoc.Get(StageStr.AttrHue),
            AttributeKind.HsbSaturation => StageLoc.Get(StageStr.AttrSaturation),
            AttributeKind.HsbBrightness => StageLoc.Get(StageStr.AttrBrightness),
            AttributeKind.CieX => "CIE x",
            AttributeKind.CieY => "CIE y",
            AttributeKind.CieBrightness => StageLoc.Get(StageStr.AttrBrightness) + " (CIE)",
            AttributeKind.Cto => "CTO",
            AttributeKind.Ctb => "CTB",
            AttributeKind.Ctc => StageLoc.Get(StageStr.AttrCtc),
            AttributeKind.Tint => StageLoc.Get(StageStr.AttrTint),
            AttributeKind.ColorCrossfade => StageLoc.Get(StageStr.AttrColorCrossfade),
            AttributeKind.ColorMacro => StageLoc.Get(StageStr.AttrColorMacro) + n,
            AttributeKind.WheelSelect => wheel + n,
            AttributeKind.WheelSelectSpin => wheel + n + " · " + StageLoc.Get(StageStr.AttrSpin),
            AttributeKind.WheelSelectShake => wheel + n + " · " + StageLoc.Get(StageStr.AttrShake),
            AttributeKind.WheelIndex => wheel + n + " · " + StageLoc.Get(StageStr.AttrIndex),
            AttributeKind.WheelSpin => wheel + n + " · " + StageLoc.Get(StageStr.AttrSpin),
            AttributeKind.WheelShake => wheel + n + " · " + StageLoc.Get(StageStr.AttrShake),
            AttributeKind.WheelRandom => wheel + n + " · " + StageLoc.Get(StageStr.AttrRandom),
            AttributeKind.SlotIndex => wheel + n + " · " + StageLoc.Get(StageStr.AttrSlotRotation),
            AttributeKind.SlotRotate => wheel + n + " · " + StageLoc.Get(StageStr.AttrSlotSpin),
            AttributeKind.SlotShake => wheel + n + " · " + StageLoc.Get(StageStr.AttrShake),
            AttributeKind.EffectRate => wheel + n + " · " + StageLoc.Get(StageStr.AttrRate),
            AttributeKind.AnimationInsertion => StageLoc.Get(StageStr.WheelAnimation) + n,
            AttributeKind.Frost => StageLoc.Get(StageStr.LiveFrost) + n,
            AttributeKind.FrostPulse => StageLoc.Get(StageStr.LiveFrost) + n + Waveform(cls.Waveform),
            AttributeKind.Iris => StageLoc.Get(StageStr.LiveIris),
            AttributeKind.IrisPulse => StageLoc.Get(StageStr.LiveIris) + Waveform(cls.Waveform),
            AttributeKind.Zoom => StageLoc.Get(StageStr.AttrZoom),
            AttributeKind.Focus => StageLoc.Get(StageStr.LiveFocus) + n,
            AttributeKind.FocusDistance => StageLoc.Get(StageStr.AttrFocusDistance) + n,
            AttributeKind.BladeA => StageLoc.Get(StageStr.AttrBlade) + " " + cls.Number.ToString(CultureInfo.InvariantCulture) + " A",
            AttributeKind.BladeB => StageLoc.Get(StageStr.AttrBlade) + " " + cls.Number.ToString(CultureInfo.InvariantCulture) + " B",
            AttributeKind.BladeRotation => StageLoc.Get(StageStr.AttrBlade) + " " + cls.Number.ToString(CultureInfo.InvariantCulture) + " · " +
                                           StageLoc.Get(StageStr.AttrRotation),
            AttributeKind.BladeSoft => StageLoc.Get(StageStr.AttrBlade) + " " + cls.Number.ToString(CultureInfo.InvariantCulture) + " · " +
                                       StageLoc.Get(StageStr.AttrSoftness),
            AttributeKind.ShaperRotation => StageLoc.Get(StageStr.AttrShaperRotation),
            AttributeKind.DimmerCurve => StageLoc.Get(StageStr.AttrDimmerCurve),
            AttributeKind.MotionSpeed => StageLoc.Get(StageStr.AttrMotionSpeed) + (cls.Code is { Length: > 0 } g ? " (" + g + ")" : ""),
            AttributeKind.Fog => StageLoc.Get(StageStr.AttrFog) + n,
            AttributeKind.Haze => StageLoc.Get(StageStr.AttrHaze) + n,
            AttributeKind.Fan => StageLoc.Get(StageStr.AttrFan) + n,
            AttributeKind.FanDirection => StageLoc.Get(StageStr.AttrFan) + n + " · " + StageLoc.Get(StageStr.AttrDirection),
            AttributeKind.Flame => StageLoc.Get(StageStr.AttrFlame) + n,
            AttributeKind.FlameHeight => StageLoc.Get(StageStr.AttrFlame) + n + " · " + StageLoc.Get(StageStr.AttrHeight),
            _ => SplitWords(attribute),
        };
    }

    private static string WheelName(WheelFamily family) => StageLoc.Get(family switch
    {
        WheelFamily.Color => StageStr.WheelColor,
        WheelFamily.Gobo => StageStr.WheelGobo,
        WheelFamily.Prism => StageStr.WheelPrism,
        WheelFamily.Animation or WheelFamily.AnimationSystem => StageStr.WheelAnimation,
        WheelFamily.BeamShaper => StageStr.WheelBeamShaper,
        _ => StageStr.WheelEffects,
    });

    private static string Waveform(Waveform waveform) => waveform switch
    {
        Dmx.Waveform.None or Dmx.Waveform.Strobe => "",
        Dmx.Waveform.Pulse => " (" + StageLoc.Get(StageStr.WavePulse) + ")",
        Dmx.Waveform.PulseOpen => " (" + StageLoc.Get(StageStr.WavePulseOpen) + ")",
        Dmx.Waveform.PulseClose => " (" + StageLoc.Get(StageStr.WavePulseClose) + ")",
        Dmx.Waveform.Random or Dmx.Waveform.RandomPulse or Dmx.Waveform.RandomPulseOpen
            or Dmx.Waveform.RandomPulseClose => " (" + StageLoc.Get(StageStr.AttrRandom) + ")",
        Dmx.Waveform.Sine => " (" + StageLoc.Get(StageStr.WaveSine) + ")",
        Dmx.Waveform.Burst => " (" + StageLoc.Get(StageStr.WaveBurst) + ")",
        _ => "",
    };

    private static string Axis(int number) => number switch { 1 => "X", 2 => "Y", _ => "Z" };

    /// <summary>"Control1" → "Control 1", "LEDFrequency" → "LED Frequency", "Color_Macro" → "Color Macro".</summary>
    internal static string SplitWords(string attribute)
    {
        if (string.IsNullOrEmpty(attribute))
            return attribute;
        var sb = new StringBuilder(attribute.Length + 8);
        for (int i = 0; i < attribute.Length; i++)
        {
            char c = attribute[i];
            if (c == '_')
            {
                sb.Append(' ');
                continue;
            }

            if (i > 0)
            {
                char prev = attribute[i - 1];
                bool boundary = (char.IsUpper(c) && char.IsLower(prev)) || (char.IsDigit(c) && char.IsLetter(prev)) ||
                                (char.IsUpper(c) && char.IsUpper(prev) && i + 1 < attribute.Length && char.IsLower(attribute[i + 1]));
                if (boundary && sb.Length > 0 && sb[^1] != ' ')
                    sb.Append(' ');
            }

            sb.Append(c);
        }

        return sb.ToString();
    }
}
