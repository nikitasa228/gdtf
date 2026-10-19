using Candela.Rendering;

namespace Candela.App.Session;

/// <summary>
/// Field-by-field copies of the render settings the editor keeps as long-lived objects (the viewports, the presets and
/// the project all hold references to the same live instances, so a value is copied into them instead of replacing
/// them). Explicit assignments: no reflection, no allocation (a gradient's stop list is refilled in place). A test
/// checks that every public property is covered.
/// </summary>
internal static class SettingsCopy
{
    /// <summary>Copies every value of <paramref name="from"/> into <paramref name="to"/>.</summary>
    public static void Copy(PreviewSettings from, PreviewSettings to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (ReferenceEquals(from, to))
            return;
        to.ResolutionScale = from.ResolutionScale;
        to.Shadows = from.Shadows;
        to.ShadowMapResolution = from.ShadowMapResolution;
        to.MaxShadowedLights = from.MaxShadowedLights;
        to.Volumetrics = from.Volumetrics;
        to.VolumetricSlices = from.VolumetricSlices;
        to.MaxRaymarchedBeams = from.MaxRaymarchedBeams;
        to.TemporalAccumulation = from.TemporalAccumulation;
        to.Indirect = from.Indirect;
        to.Bloom = from.Bloom;
        to.BloomIntensity = from.BloomIntensity;
        to.ToneMapper = from.ToneMapper;
        to.AutoExposure = from.AutoExposure;
        to.ExposureCompensation = from.ExposureCompensation;
    }

    /// <summary>Copies every value of <paramref name="from"/> into <paramref name="to"/>.</summary>
    public static void Copy(FinalRenderSettings from, FinalRenderSettings to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (ReferenceEquals(from, to))
            return;
        to.Width = from.Width;
        to.Height = from.Height;
        to.SamplesPerPixel = from.SamplesPerPixel;
        to.MaxBounces = from.MaxBounces;
        to.MaxVolumeBounces = from.MaxVolumeBounces;
        to.Denoise = from.Denoise;
        to.ToneMapper = from.ToneMapper;
        to.Format = from.Format;
        to.OutputPath = from.OutputPath;
        to.FrameRate = from.FrameRate;
    }

    /// <summary>Copies every value of <paramref name="from"/> into <paramref name="to"/> (the stop list is refilled in place).</summary>
    public static void Copy(ColorGradient from, ColorGradient to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (ReferenceEquals(from, to))
            return;
        to.RangeMin = from.RangeMin;
        to.RangeMax = from.RangeMax;
        to.Logarithmic = from.Logarithmic;
        to.OutOfRangeColor = from.OutOfRangeColor;
        to.Stops ??= new List<GradientStop>();
        to.Stops.Clear();
        if (from.Stops is { } stops)
            to.Stops.AddRange(stops);
    }

    /// <summary>
    /// A copy of <paramref name="value"/> made safe to render: finite values in sensible ranges (settings read from a
    /// project or preset file written by hand or by another version). Null gives the defaults.
    /// </summary>
    public static PreviewSettings Sanitized(PreviewSettings? value)
    {
        var s = new PreviewSettings();
        if (value is null)
            return s;
        Copy(value, s);
        s.ResolutionScale = Finite(s.ResolutionScale, 1f, 0.25f, 1f);
        s.ShadowMapResolution = Math.Clamp(s.ShadowMapResolution, 256, 8192);
        s.MaxShadowedLights = Math.Clamp(s.MaxShadowedLights, 0, 256);
        s.VolumetricSlices = Math.Clamp(s.VolumetricSlices, 16, 256);
        s.MaxRaymarchedBeams = Math.Clamp(s.MaxRaymarchedBeams, 0, 128);
        s.BloomIntensity = Finite(s.BloomIntensity, 0.04f, 0f, 1f);
        s.ExposureCompensation = Finite(s.ExposureCompensation, 0f, -10f, 10f);
        if (!Enum.IsDefined(s.Indirect))
            s.Indirect = IndirectLighting.ReflectiveShadowMaps;
        if (!Enum.IsDefined(s.ToneMapper))
            s.ToneMapper = ToneMapper.AgX;
        return s;
    }

    /// <summary>A copy of <paramref name="value"/> with every number finite and in range (null gives the defaults).</summary>
    public static FinalRenderSettings Sanitized(FinalRenderSettings? value)
    {
        var s = new FinalRenderSettings();
        if (value is null)
            return s;
        Copy(value, s);
        s.Width = Math.Clamp(s.Width, 16, 16384);
        s.Height = Math.Clamp(s.Height, 16, 16384);
        s.SamplesPerPixel = Math.Clamp(s.SamplesPerPixel, 1, 65536);
        s.MaxBounces = Math.Clamp(s.MaxBounces, 1, 64);
        s.MaxVolumeBounces = Math.Clamp(s.MaxVolumeBounces, 0, 64);
        s.FrameRate = double.IsFinite(s.FrameRate) ? Math.Clamp(s.FrameRate, 1, 240) : 25;
        s.OutputPath ??= "render.png";
        if (!Enum.IsDefined(s.Format))
            s.Format = ImageFileFormat.Png;
        if (!Enum.IsDefined(s.ToneMapper))
            s.ToneMapper = ToneMapper.AgX;
        return s;
    }

    /// <summary>
    /// A copy of <paramref name="value"/> made safe to draw: finite range with min &lt; max, finite stops sorted by value,
    /// colours clamped to 0…1. Null or an empty gradient gives <paramref name="fallback"/>.
    /// </summary>
    public static ColorGradient Sanitized(ColorGradient? value, Func<ColorGradient> fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        if (value?.Stops is not { Count: > 0 } stops)
            return fallback();
        var result = new ColorGradient
        {
            Logarithmic = value.Logarithmic,
            OutOfRangeColor = Clamp01(value.OutOfRangeColor, new System.Numerics.Vector4(0.35f, 0.35f, 0.35f, 1f)),
        };
        foreach (var stop in stops)
        {
            if (!float.IsFinite(stop.Value) || (value.Logarithmic && stop.Value <= 0f))
                continue;
            result.Stops.Add(new GradientStop(stop.Value, Clamp01(stop.Color, System.Numerics.Vector4.One)));
        }

        if (result.Stops.Count == 0)
            return fallback();
        result.Stops.Sort(static (a, b) => a.Value.CompareTo(b.Value));
        float lo = result.Stops[0].Value, hi = result.Stops[^1].Value;
        float min = float.IsFinite(value.RangeMin) ? value.RangeMin : lo;
        float max = float.IsFinite(value.RangeMax) ? value.RangeMax : hi;
        if (value.Logarithmic)
        {
            min = MathF.Max(min, 1e-4f);
            max = MathF.Max(max, 1e-4f);
        }

        if (!(max > min))
        {
            min = MathF.Min(lo, hi);
            max = MathF.Max(MathF.Max(lo, hi), min + (value.Logarithmic ? min : 1f));
        }

        result.RangeMin = min;
        result.RangeMax = max;
        return result;
    }

    private static float Finite(float value, float fallback, float min, float max) =>
        float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    private static System.Numerics.Vector4 Clamp01(System.Numerics.Vector4 v, System.Numerics.Vector4 fallback) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) && float.IsFinite(v.W)
            ? System.Numerics.Vector4.Clamp(v, System.Numerics.Vector4.Zero, System.Numerics.Vector4.One)
            : fallback;
}
