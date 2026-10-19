namespace Candela.Rendering;

/// <summary>
/// Options of a final (path-traced) render beyond the stored <see cref="FinalRenderSettings"/> preset: the exposure, the
/// background, the seed, a time budget and firefly control. The defaults give a reference render: camera exposure,
/// unclamped, deterministic.
/// </summary>
public sealed class FinalRenderOptions
{
    /// <summary>Largest firefly clamp accepted, cd/m² (larger values are clamped).</summary>
    public const float MaxFireflyClamp = 1e9f;

    /// <summary>
    /// Seed of the random sequences (deterministic: the same scene, settings and seed give the same image on the same
    /// device). Change it to render statistically independent images.
    /// </summary>
    public uint Seed { get; set; }

    /// <summary>
    /// Wall-clock budget of the whole render; null = none. The render completes when
    /// <see cref="FinalRenderSettings.SamplesPerPixel"/> samples are done or when the time is up, whichever comes first
    /// (the sample pass in progress is finished, so every pixel has the same number of samples).
    /// </summary>
    public TimeSpan? TimeLimit { get; set; }

    /// <summary>
    /// Firefly clamp, cd/m²: the luminance of each light-path contribution that has bounced at least once (indirect
    /// light, light seen through haze scattering, glossy reflections of lights) is limited to this value. Removes rare
    /// very bright samples at the cost of a small bias (energy loss). 0 = off (default, for reference renders).
    /// </summary>
    public float FireflyClamp { get; set; }

    /// <summary>Added to the exposure in stops (+1 doubles it), like <see cref="PreviewSettings.ExposureCompensation"/>.</summary>
    public float ExposureCompensation { get; set; }

    /// <summary>
    /// When true the exposure is metered from the rendered image (the same metering as the real-time auto exposure,
    /// <see cref="Metering"/>); otherwise the camera's <see cref="RenderCamera.Ev100"/> is used.
    /// </summary>
    public bool AutoExposure { get; set; }

    /// <summary>How <see cref="AutoExposure"/> meters the image (stage metering by default).</summary>
    public ExposureMetering Metering { get; set; } = ExposureMetering.Stage;

    /// <summary>
    /// Luminance of the surroundings (environment seen by rays that leave the scene), cd/m², like
    /// <see cref="ViewOptions.BackgroundLuminance"/> (a dark venue: 0.02). It also lights the scene.
    /// </summary>
    public float BackgroundLuminance { get; set; } = 0.02f;

    /// <summary>Iterations of the edge-aware denoiser (à-trous, 1–8) when <see cref="FinalRenderSettings.Denoise"/> is on.</summary>
    public int DenoiseIterations { get; set; } = 5;
}
