using System.Numerics;

namespace Candela.Rendering;

/// <summary>Viewport shading mode (requirement: Wireframe, Colorless, Full).</summary>
public enum ViewShading
{
    /// <summary>Object edges only.</summary>
    Wireframe,

    /// <summary>Geometry shaded in neutral grey with a camera headlight; no materials, no stage light.</summary>
    Colorless,

    /// <summary>Everything: materials, physically based stage light, volumetrics, indirect light (see <see cref="PreviewSettings"/>).</summary>
    Full,
}

public enum ProjectionKind
{
    Perspective,
    Orthographic,
}

/// <summary>Analysis overlay drawn over the view.</summary>
public enum OverlayMode
{
    None,

    /// <summary>Illuminance (lux) on surfaces mapped through <see cref="ViewOptions.LuxGradient"/>.</summary>
    Lux,

    /// <summary>Sound pressure level on surfaces and as tinted fog in the volume.</summary>
    Noise,

    /// <summary>Scene greyed out; cables coloured by type and the devices they connect highlighted.</summary>
    Cables,

    /// <summary>
    /// Scene greyed out; the density of smoke and haze — the scattering coefficient of the air, 1/m — mapped through
    /// <see cref="ViewOptions.SmokeGradient"/> on surfaces and as tinted fog in the volume; hazers, fog machines, fans
    /// and flames marked with their output and direction (<see cref="IRenderWorld.SetSmokeEmitters"/>).
    /// </summary>
    Smoke,
}

/// <summary>
/// A camera for one view. <see cref="View"/> is world → view space in System.Numerics row-vector convention with the
/// view space looking down −Z, +Y up (Matrix4x4.CreateLookAt). The renderer builds a reverse-Z Vulkan projection.
/// </summary>
public sealed class RenderCamera
{
    public Matrix4x4 View { get; set; } = Matrix4x4.Identity;
    public ProjectionKind Projection { get; set; } = ProjectionKind.Perspective;

    /// <summary>Vertical field of view in radians (perspective).</summary>
    public float FovY { get; set; } = 50f * MathF.PI / 180f;

    /// <summary>Visible height in metres (orthographic).</summary>
    public float OrthoHeight { get; set; } = 10f;

    public float Near { get; set; } = 0.05f;
    public float Far { get; set; } = 2000f;

    /// <summary>Exposure value at ISO 100 of the physical camera (manual exposure).</summary>
    public float Ev100 { get; set; } = 6f;

    public Vector3 Position => Matrix4x4.Invert(View, out var inv) ? inv.Translation : Vector3.Zero;

    /// <summary>Camera at <paramref name="eye"/> looking at <paramref name="target"/> with world Z up.</summary>
    public static RenderCamera LookAt(Vector3 eye, Vector3 target, float fovYDegrees = 50f)
    {
        var up = MathF.Abs(Vector3.Dot(Vector3.Normalize(target - eye), Vector3.UnitZ)) > 0.999f ? Vector3.UnitY : Vector3.UnitZ;
        return new RenderCamera { View = Matrix4x4.CreateLookAt(eye, target, up), FovY = fovYDegrees * MathF.PI / 180f };
    }
}

/// <summary>A colour stop of an overlay gradient: value in the overlay's unit (lux or dB) → colour (sRGB, alpha = opacity).</summary>
public readonly record struct GradientStop(float Value, Vector4 Color);

/// <summary>
/// Gradient of an analysis overlay (lux, noise, smoke). Values between <see cref="RangeMin"/> and <see cref="RangeMax"/> are
/// coloured by interpolating the stops; values outside the range are shown grey (requirement). Stored as presets.
/// </summary>
public sealed class ColorGradient
{
    public List<GradientStop> Stops { get; set; } = new();
    public float RangeMin { get; set; }
    public float RangeMax { get; set; } = 1000f;

    /// <summary>Interpolate in log10 of the value (illuminance spans several decades).</summary>
    public bool Logarithmic { get; set; }

    public Vector4 OutOfRangeColor { get; set; } = new(0.35f, 0.35f, 0.35f, 1f);

    /// <summary>Colour for a value (stops sorted by value; clamped to the first/last stop inside the range).</summary>
    public Vector4 Evaluate(float value)
    {
        if (float.IsNaN(value) || value < RangeMin || value > RangeMax || Stops.Count == 0) return OutOfRangeColor;
        var stops = Stops.OrderBy(s => s.Value).ToList();
        float Map(float v) => Logarithmic ? MathF.Log10(MathF.Max(v, 1e-6f)) : v;
        float x = Map(value);
        if (x <= Map(stops[0].Value)) return stops[0].Color;
        for (int i = 1; i < stops.Count; i++)
        {
            float a = Map(stops[i - 1].Value), b = Map(stops[i].Value);
            if (x <= b) return Vector4.Lerp(stops[i - 1].Color, stops[i].Color, b > a ? (x - a) / (b - a) : 1f);
        }
        return stops[^1].Color;
    }

    /// <summary>Samples the gradient for a GPU lookup texture covering [RangeMin, RangeMax].</summary>
    public Vector4[] Bake(int samples)
    {
        var lut = new Vector4[samples];
        for (int i = 0; i < samples; i++)
        {
            float t = samples == 1 ? 0 : i / (float)(samples - 1);
            float v = Logarithmic
                ? MathF.Pow(10f, float.Lerp(MathF.Log10(MathF.Max(RangeMin, 1e-6f)), MathF.Log10(MathF.Max(RangeMax, 1e-6f)), t))
                : float.Lerp(RangeMin, RangeMax, t);
            lut[i] = Evaluate(System.Math.Clamp(v, RangeMin, RangeMax));
        }
        return lut;
    }

    /// <summary>Default lux gradient: dark blue (dim) → cyan → green → yellow → red (very bright), 0–2000 lx.</summary>
    public static ColorGradient DefaultLux() => new()
    {
        RangeMin = 1f,
        RangeMax = 5000f,
        Logarithmic = true,
        Stops =
        {
            new(1f, new Vector4(0.05f, 0.05f, 0.45f, 1f)),
            new(50f, new Vector4(0.0f, 0.6f, 1.0f, 1f)),
            new(300f, new Vector4(0.1f, 0.85f, 0.2f, 1f)),
            new(1000f, new Vector4(1.0f, 0.9f, 0.1f, 1f)),
            new(5000f, new Vector4(1.0f, 0.1f, 0.05f, 1f)),
        },
    };

    /// <summary>Default noise gradient in dB(A): quiet green → loud red, 25–80 dB.</summary>
    public static ColorGradient DefaultNoise() => new()
    {
        RangeMin = 25f,
        RangeMax = 80f,
        Stops =
        {
            new(25f, new Vector4(0.1f, 0.6f, 0.2f, 1f)),
            new(45f, new Vector4(0.9f, 0.85f, 0.1f, 1f)),
            new(65f, new Vector4(1.0f, 0.45f, 0.05f, 1f)),
            new(80f, new Vector4(0.85f, 0.05f, 0.1f, 1f)),
        },
    };

    /// <summary>
    /// Default smoke gradient: the scattering coefficient of the air (haze plus smoke), 1/m, on a log scale from
    /// 0.005 /m (a trace of haze; visibility ≈ 3 / σ = 600 m) to 2 /m (thick fog, 1.5 m visibility): faint blue →
    /// cyan → green (a thick working haze, 0.1 /m) → yellow → magenta. The thin end is the most transparent, so an even
    /// haze leaves the greyed scene readable; clearer (or denser) air than the range shows no colour.
    /// </summary>
    public static ColorGradient DefaultSmoke() => new()
    {
        RangeMin = 0.005f,
        RangeMax = 2f,
        Logarithmic = true,
        OutOfRangeColor = new Vector4(0.35f, 0.35f, 0.35f, 0f),
        Stops =
        {
            new(0.005f, new Vector4(0.25f, 0.35f, 0.9f, 0.1f)),
            new(0.02f, new Vector4(0.0f, 0.75f, 0.95f, 0.2f)),
            new(0.1f, new Vector4(0.15f, 0.85f, 0.3f, 0.45f)),
            new(0.5f, new Vector4(1.0f, 0.85f, 0.1f, 0.75f)),
            new(2f, new Vector4(1.0f, 0.2f, 0.65f, 0.95f)),
        },
    };
}

public enum ToneMapper
{
    /// <summary>AgX (good hue preservation for saturated stage colours).</summary>
    AgX,
    Aces,
    Neutral,
}

/// <summary>Metering of the auto exposure.</summary>
public enum ExposureMetering
{
    /// <summary>
    /// Mean log luminance of the pixels between the 50th and 95th percentile at middle grey: an evenly lit room or
    /// product view.
    /// </summary>
    Average,

    /// <summary>
    /// Stage looks (the default): a dark venue with bright beams, pools and lenses. Meters the lit part of the image
    /// (mean log luminance between the 85th and 99th percentile, so the hottest 1 % — lenses, specular glints — does
    /// not count) and places it one and a half stops below middle grey (the EV100 is 1.5 above that part's reading):
    /// light pools stay below white, beams in haze stay visible and the dark venue is not lifted to grey. In an evenly
    /// lit view it exposes about 1.5 stops darker than <see cref="Average"/>.
    /// </summary>
    Stage,
}

public enum IndirectLighting
{
    Off,

    /// <summary>Reflective shadow maps of spot lights (works on every GPU).</summary>
    ReflectiveShadowMaps,

    /// <summary>Ray-traced one-bounce GI and reflections (needs ray-query support).</summary>
    RayTraced,
}

/// <summary>Real-time quality settings (the "Preview" panel). Stored as presets.</summary>
public sealed class PreviewSettings
{
    /// <summary>Render resolution relative to the viewport (0.5–1).</summary>
    public float ResolutionScale { get; set; } = 1f;

    public bool Shadows { get; set; } = true;
    public int ShadowMapResolution { get; set; } = 1024;

    /// <summary>Lights beyond this count (sorted by contribution) render without shadows.</summary>
    public int MaxShadowedLights { get; set; } = 64;

    public bool Volumetrics { get; set; } = true;

    /// <summary>Froxel grid depth slices (width/height follow the viewport at 8 px per froxel).</summary>
    public int VolumetricSlices { get; set; } = 128;

    /// <summary>
    /// Most spot lights rendered as volumetric beams: per-pixel single scattering inside each beam (sharp shafts, soft
    /// photometric edges, gobos, shadows in the air), the most important lights of the view first. Lights beyond it
    /// (and very wide or diffuse emitters) go into the froxel volume. 0 = every light in the froxels. At most 8192.
    /// </summary>
    public int MaxRaymarchedBeams { get; set; } = 4096;

    public bool TemporalAccumulation { get; set; } = true;
    public IndirectLighting Indirect { get; set; } = IndirectLighting.ReflectiveShadowMaps;
    public bool Bloom { get; set; } = true;
    public float BloomIntensity { get; set; } = 0.04f;
    public ToneMapper ToneMapper { get; set; } = ToneMapper.AgX;

    /// <summary>When true the exposure adapts to the image; otherwise the camera EV100 is used.</summary>
    public bool AutoExposure { get; set; }

    /// <summary>How <see cref="AutoExposure"/> meters the image (stage metering by default).</summary>
    public ExposureMetering Metering { get; set; } = ExposureMetering.Stage;

    /// <summary>Added to the exposure in stops (user brightness trim).</summary>
    public float ExposureCompensation { get; set; }
}

public enum ImageFileFormat
{
    Png,
    Exr,
}

/// <summary>Final (offline, path-traced) render settings (the "Render" panel). Stored as presets.</summary>
public sealed class FinalRenderSettings
{
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public int SamplesPerPixel { get; set; } = 256;
    public int MaxBounces { get; set; } = 6;

    /// <summary>Scattering events traced inside haze/smoke per path.</summary>
    public int MaxVolumeBounces { get; set; } = 2;

    public bool Denoise { get; set; } = true;
    public ToneMapper ToneMapper { get; set; } = ToneMapper.AgX;
    public ImageFileFormat Format { get; set; } = ImageFileFormat.Png;
    public string OutputPath { get; set; } = "render.png";

    /// <summary>Frames per second when rendering a timecode range.</summary>
    public double FrameRate { get; set; } = 25;
}

/// <summary>What the editor wants drawn on top of the shaded view.</summary>
public sealed class ViewOptions
{
    public ViewShading Shading { get; set; } = ViewShading.Full;
    public OverlayMode Overlay { get; set; } = OverlayMode.None;
    public ColorGradient LuxGradient { get; set; } = ColorGradient.DefaultLux();
    public ColorGradient NoiseGradient { get; set; } = ColorGradient.DefaultNoise();

    /// <summary>Gradient of the smoke overlay: scattering coefficient of the air (haze plus smoke), 1/m.</summary>
    public ColorGradient SmokeGradient { get; set; } = ColorGradient.DefaultSmoke();

    public bool ShowGrid { get; set; } = true;
    public float GridSpacing { get; set; } = 1f;
    public bool ShowSelectionOutline { get; set; } = true;

    /// <summary>Vivid orange required for selection outlines (#FF7A00).</summary>
    public Vector4 SelectionColor { get; set; } = new(1f, 0.478f, 0f, 1f);

    /// <summary>Show light cones of fixtures as outlines (helps in Wireframe/Colorless).</summary>
    public bool ShowBeamOutlines { get; set; }

    /// <summary>Background luminance in cd/m² for the Full mode (a dark venue).</summary>
    public float BackgroundLuminance { get; set; } = 0.02f;
}
