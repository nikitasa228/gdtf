using Candela.Core.SceneGraph.Components;

namespace Candela.Gdtf;

/// <summary>GDTF <c>PhysicalDescriptions</c>: what the light is physically made of.</summary>
public sealed class PhysicalDescriptions
{
    /// <summary>Additive light sources (LED dies, lamps) with colour and spectral measurements.</summary>
    public List<Emitter> Emitters { get; } = new();

    /// <summary>Subtractive filters (CMY flags, colour wheel slots) with transmission measurements.</summary>
    public List<Filter> Filters { get; } = new();

    /// <summary>Colour space of RGB control channels (defaults to sRGB when the file has none).</summary>
    public ColorSpace ColorSpace { get; set; } = ColorSpace.Create(ColorSpaceMode.Srgb);

    /// <summary>Further named colour spaces that channel functions can select (GDTF 1.2).</summary>
    public List<ColorSpace> AdditionalColorSpaces { get; } = new();

    /// <summary>Colour gamuts (GDTF 1.2).</summary>
    public List<Gamut> Gamuts { get; } = new();

    /// <summary>DMX-to-output curves (GDTF 1.2).</summary>
    public List<DmxProfile> DmxProfiles { get; } = new();

    /// <summary>Colour rendering indices per colour temperature.</summary>
    public List<CriGroup> CriGroups { get; } = new();

    /// <summary>Connectors as described in GDTF 1.0/1.1 (superseded by WiringObject geometries in 1.2).</summary>
    public List<Connector> Connectors { get; } = new();

    /// <summary>Operating temperature, weight, leg height and power consumption.</summary>
    public FixtureProperties Properties { get; } = new();

    /// <summary>Colour space by name: the main one or one of the additional ones, else null.</summary>
    public ColorSpace? FindColorSpace(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (string.Equals(ColorSpace.Name, name, StringComparison.Ordinal)) return ColorSpace;
        foreach (var cs in AdditionalColorSpaces)
            if (string.Equals(cs.Name, name, StringComparison.Ordinal)) return cs;
        return null;
    }
}

/// <summary>An additive light source.</summary>
public sealed class Emitter
{
    /// <summary>Unique name, referenced by channel functions and beams.</summary>
    public string Name { get; set; } = "";

    /// <summary>Colour (CIE 1931 xyY) for colour mixing; null for white emitters described only by a spectrum.</summary>
    public CieColor? Color { get; set; }

    /// <summary>Dominant wavelength in nm, if given (0 in many files means "not set" and is stored as null).</summary>
    public float? DominantWaveLength { get; set; }

    /// <summary>Manufacturer part number of the diode.</summary>
    public string? DiodePart { get; set; }

    /// <summary>Spectral / intensity measurements at given output levels.</summary>
    public List<Measurement> Measurements { get; } = new();

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>A subtractive colour filter.</summary>
public sealed class Filter
{
    /// <summary>Unique name, referenced by channel functions and wheel slots.</summary>
    public string Name { get; set; } = "";

    /// <summary>Approximate colour of the filtered white light, CIE 1931 xyY.</summary>
    public CieColor Color { get; set; } = WheelSlot.DefaultColor;

    /// <summary>Transmission measurements at given insertion levels.</summary>
    public List<Measurement> Measurements { get; } = new();

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>How values between two measurements are interpolated.</summary>
public enum InterpolationTo
{
    /// <summary>Linear interpolation (default).</summary>
    Linear,

    /// <summary>Hold the value until the next measurement.</summary>
    Step,

    /// <summary>Logarithmic interpolation.</summary>
    Log,
}

/// <summary>One measurement of an emitter or filter at a given level.</summary>
public sealed class Measurement
{
    /// <summary>Emitter output (additive) or filter insertion (subtractive) in percent at which it was measured.</summary>
    public float Physical { get; set; }

    /// <summary>Luminous intensity in cd at this level (emitters only).</summary>
    public float? LuminousIntensity { get; set; }

    /// <summary>Total transmission 0 … 1 at this level (filters only).</summary>
    public float? Transmission { get; set; }

    /// <summary>Interpolation towards the next measurement.</summary>
    public InterpolationTo InterpolationTo { get; set; }

    /// <summary>Spectral samples, ascending wavelength as written.</summary>
    public List<MeasurementPoint> Points { get; } = new();
}

/// <summary>One spectral sample: wavelength in nm, spectral energy in W/m²/nm (or transmission for filters).</summary>
public readonly record struct MeasurementPoint(float WaveLength, float Energy);

/// <summary>Kind of a GDTF colour space definition.</summary>
public enum ColorSpaceMode
{
    /// <summary>Primaries and white point are given in the file.</summary>
    Custom,

    /// <summary>sRGB / Rec.709 primaries, D65 white (IEC 61966-2-1).</summary>
    Srgb,

    /// <summary>Kodak ProPhoto ROMM RGB, D50 white (ISO 22028-2).</summary>
    ProPhoto,

    /// <summary>ANSI E1.54-2015 primaries and white point.</summary>
    Ansi,
}

/// <summary>An RGB colour space: primaries and white point as CIE 1931 xyY with Y normalised so that white is Y = 1.</summary>
public sealed class ColorSpace
{
    /// <summary>Name (GDTF 1.2; empty for the unnamed main colour space of older files).</summary>
    public string Name { get; set; } = "";

    /// <summary>Which definition the primaries come from.</summary>
    public ColorSpaceMode Mode { get; set; } = ColorSpaceMode.Srgb;

    /// <summary>Red primary.</summary>
    public CieColor Red { get; set; }

    /// <summary>Green primary.</summary>
    public CieColor Green { get; set; }

    /// <summary>Blue primary.</summary>
    public CieColor Blue { get; set; }

    /// <summary>White point.</summary>
    public CieColor WhitePoint { get; set; }

    /// <summary>
    /// A colour space of a predefined <paramref name="mode"/> with primaries from the GDTF spec; the luminance of each
    /// primary is derived so that R + G + B gives the white point with Y = 1. Custom returns sRGB primaries as a start.
    /// </summary>
    public static ColorSpace Create(ColorSpaceMode mode, string name = "")
    {
        (double x, double y)[] p = mode switch
        {
            ColorSpaceMode.ProPhoto => new[] { (0.7347, 0.2653), (0.1596, 0.8404), (0.0366, 0.0001), (0.3457, 0.3585) },
            ColorSpaceMode.Ansi => new[] { (0.7347, 0.2653), (0.1596, 0.8404), (0.0366, 0.001), (0.4254, 0.4044) },
            _ => new[] { (0.64, 0.33), (0.30, 0.60), (0.15, 0.06), (0.3127, 0.3290) },
        };
        var cs = new ColorSpace { Name = name, Mode = mode };
        cs.SetPrimaries(p[0], p[1], p[2], p[3]);
        return cs;
    }

    /// <summary>
    /// Sets primaries and white point from chromaticities and derives each primary's relative luminance (white Y = 1)
    /// with the standard RGB → XYZ construction.
    /// </summary>
    public void SetPrimaries((double x, double y) r, (double x, double y) g, (double x, double y) b, (double x, double y) w)
    {
        var lum = PrimaryLuminances(r, g, b, w);
        Red = new CieColor((float)r.x, (float)r.y, (float)lum.R);
        Green = new CieColor((float)g.x, (float)g.y, (float)lum.G);
        Blue = new CieColor((float)b.x, (float)b.y, (float)lum.B);
        WhitePoint = new CieColor((float)w.x, (float)w.y, 1f);
    }

    /// <summary>Relative luminances of the primaries so that their sum is the white point with Y = 1 (NaN when degenerate).</summary>
    internal static (double R, double G, double B) PrimaryLuminances((double x, double y) r, (double x, double y) g, (double x, double y) b, (double x, double y) w)
    {
        // Columns: XYZ of each primary with Y = 1; solve M * s = W (white XYZ with Y = 1) by Cramer's rule.
        static (double X, double Y, double Z) Xyz((double x, double y) c) =>
            c.y <= 0 ? (double.NaN, 1, double.NaN) : (c.x / c.y, 1, (1 - c.x - c.y) / c.y);
        var (rx, ry, rz) = Xyz(r);
        var (gx, gy, gz) = Xyz(g);
        var (bx, by, bz) = Xyz(b);
        var (wx, wy, wz) = Xyz(w);
        double det = rx * (gy * bz - by * gz) - gx * (ry * bz - by * rz) + bx * (ry * gz - gy * rz);
        if (System.Math.Abs(det) < 1e-12 || double.IsNaN(det)) return (double.NaN, double.NaN, double.NaN);
        double sr = (wx * (gy * bz - by * gz) - gx * (wy * bz - by * wz) + bx * (wy * gz - gy * wz)) / det;
        double sg = (rx * (wy * bz - by * wz) - wx * (ry * bz - by * rz) + bx * (ry * wz - wy * rz)) / det;
        double sb = (rx * (gy * wz - wy * gz) - gx * (ry * wz - wy * rz) + wx * (ry * gz - gy * rz)) / det;
        return (sr * ry, sg * gy, sb * by);
    }

    /// <inheritdoc />
    public override string ToString() => string.IsNullOrEmpty(Name) ? Mode.ToString() : $"{Name} ({Mode})";
}

/// <summary>A colour gamut: polygon of CIE 1931 xyY points (GDTF 1.2).</summary>
public sealed class Gamut
{
    /// <summary>Unique name, referenced by channel functions.</summary>
    public string Name { get; set; } = "";

    /// <summary>Corner points of the gamut.</summary>
    public List<CieColor> Points { get; } = new();
}

/// <summary>A DMX profile: a piecewise cubic curve from DMX percentage to output (GDTF 1.2).</summary>
public sealed class DmxProfile
{
    /// <summary>Unique name, referenced by channel functions and sub channel sets.</summary>
    public string Name { get; set; } = "";

    /// <summary>Points in ascending DMX percentage; each starts a cubic segment that ends at the next point.</summary>
    public List<DmxProfilePoint> Points { get; } = new();

    /// <summary>
    /// Evaluates the curve at <paramref name="dmxPercentage"/> (0 … 100). Within the segment that starts at point
    /// <c>i</c>, the result is <c>CFC0 + CFC1·t + CFC2·t² + CFC3·t³</c> with <c>t</c> the position inside the segment
    /// normalised to 0 … 1. Without points the curve is linear (percentage / 100).
    /// </summary>
    public float Evaluate(float dmxPercentage)
    {
        if (Points.Count == 0) return dmxPercentage / 100f;
        int i = Points.Count - 1;
        while (i > 0 && Points[i].DmxPercentage > dmxPercentage) i--;
        var p = Points[i];
        float end = i + 1 < Points.Count ? Points[i + 1].DmxPercentage : 100f;
        float span = end - p.DmxPercentage;
        float t = span > 1e-6f ? System.Math.Clamp((dmxPercentage - p.DmxPercentage) / span, 0f, 1f) : 0f;
        return p.Cfc0 + t * (p.Cfc1 + t * (p.Cfc2 + t * p.Cfc3));
    }
}

/// <summary>Start point of a cubic segment of a <see cref="DmxProfile"/>.</summary>
/// <param name="DmxPercentage">DMX level in percent where the segment starts.</param>
/// <param name="Cfc0">Constant coefficient.</param>
/// <param name="Cfc1">Linear coefficient.</param>
/// <param name="Cfc2">Quadratic coefficient.</param>
/// <param name="Cfc3">Cubic coefficient.</param>
public readonly record struct DmxProfilePoint(float DmxPercentage, float Cfc0, float Cfc1, float Cfc2, float Cfc3);

/// <summary>Colour rendering indices measured at one colour temperature.</summary>
public sealed class CriGroup
{
    /// <summary>Colour temperature in kelvin the values apply to (default 6000 K).</summary>
    public float ColorTemperature { get; set; } = 6000f;

    /// <summary>Indices per colour evaluation sample.</summary>
    public List<Cri> Cris { get; } = new();
}

/// <summary>Colour rendering index for one colour evaluation sample (CES01 … CES99).</summary>
/// <param name="Ces">Sample name, e.g. <c>CES01</c>.</param>
/// <param name="ColorRenderingIndex">Index 0 … 100.</param>
public readonly record struct Cri(string Ces, int ColorRenderingIndex);

/// <summary>A connector as described by GDTF 1.0/1.1 (GDTF 1.2 uses WiringObject geometries instead).</summary>
public sealed class Connector
{
    /// <summary>Connector name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Connector type name (e.g. XLR5, RJ45, PowerconTRUE1).</summary>
    public string Type { get; set; } = "";

    /// <summary>DMX break the connector carries (0 = not a DMX input).</summary>
    public int DmxBreak { get; set; }

    /// <summary>Gender: -1 male, 0 neutral, 1 female.</summary>
    public int Gender { get; set; }

    /// <summary>Cable length in metres when the connector sits on a pigtail.</summary>
    public float Length { get; set; }
}

/// <summary>Physical properties of the device.</summary>
public sealed class FixtureProperties
{
    /// <summary>Lowest ambient operating temperature in °C (GDTF default 0).</summary>
    public float OperatingTemperatureLow { get; set; }

    /// <summary>Highest ambient operating temperature in °C (GDTF default 40).</summary>
    public float OperatingTemperatureHigh { get; set; } = 40f;

    /// <summary>Weight in kg, or null when not given (0 in the file is stored as null).</summary>
    public float? Weight { get; set; }

    /// <summary>Height of the legs in metres (distance from floor to the fixture's base origin when standing).</summary>
    public float LegHeight { get; set; }

    /// <summary>Power consumption per power connector (GDTF 1.2).</summary>
    public List<PowerConsumption> PowerConsumptions { get; } = new();
}

/// <summary>Power drawn through one connector at full load (GDTF 1.2 <c>PowerConsumption</c>).</summary>
public sealed class PowerConsumption
{
    /// <summary>Power at full load as written (GDTF unit: VA); active power ≈ Value × <see cref="PowerFactor"/>.</summary>
    public float Value { get; set; }

    /// <summary>Power factor 0 … 1 (default 1).</summary>
    public float PowerFactor { get; set; } = 1f;

    /// <summary>Name of the WiringObject the power flows through, as written.</summary>
    public string? ConnectorName { get; set; }

    /// <summary>Resolved WiringObject geometry.</summary>
    public WiringObjectGeometry? Connector { get; set; }

    /// <summary>Lowest supported mains voltage in V (default 90).</summary>
    public float VoltageLow { get; set; } = 90f;

    /// <summary>Highest supported mains voltage in V (default 240).</summary>
    public float VoltageHigh { get; set; } = 240f;

    /// <summary>Lowest supported mains frequency in Hz (default 50).</summary>
    public float FrequencyLow { get; set; } = 50f;

    /// <summary>Highest supported mains frequency in Hz (default 60).</summary>
    public float FrequencyHigh { get; set; } = 60f;
}
