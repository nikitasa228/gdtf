using System.Numerics;
using Candela.Core.Geometry;

namespace Candela.Rendering;

/// <summary>What the GPU and driver support (shown in the settings and used to pick render paths).</summary>
public sealed record RendererInfo(
    string DeviceName,
    string DriverName,
    string ApiVersion,
    bool IsSoftwareRenderer,
    bool SupportsRayTracing,
    bool SupportsWireframe,
    ulong DeviceMemoryBytes);

/// <summary>An image the renderer draws into (an editor viewport, a thumbnail, a CLI frame).</summary>
public interface IRenderTarget : IDisposable
{
    int Width { get; }
    int Height { get; }

    /// <summary>Changes the size (viewport resized); contents become undefined.</summary>
    void Resize(int width, int height);

    /// <summary>
    /// How completely the per-view light lists of the recent frames of this target held the lights that reach each
    /// part of the view (surface shading, the lux overlay and the volumetric light all read them). When
    /// <see cref="LightListDiagnostics.HasOverflow"/> is true some lights are missing from the image and the lux
    /// overlay reads too low there, so the editor shows a warning. GPU renderers read this back a frame or two late;
    /// renderers without light lists report no overflow.
    /// </summary>
    LightListDiagnostics LightLists => default;
}

/// <summary>
/// Light-list statistics of a target's recent frames (see <see cref="IRenderTarget.LightLists"/>).
/// </summary>
/// <param name="Lights">Lights of the world in the frame.</param>
/// <param name="ListedLights">Lights the renderer could list (the others are not drawn at all).</param>
/// <param name="MaxLightsInCluster">Most lights in one cluster (screen tile × depth slice): the lights a pixel there evaluates.</param>
/// <param name="OverflowedClusters">Clusters reached by at least one light that is not listed: the lights missing in the view.</param>
public readonly record struct LightListDiagnostics(int Lights, int ListedLights, int MaxLightsInCluster, int OverflowedClusters)
{
    /// <summary>Lights the renderer could not list (they are not drawn).</summary>
    public int UnlistedLights => Math.Max(Lights - ListedLights, 0);

    /// <summary>True when lights that reach the view are missing from its light lists (the image and the lux values are incomplete).</summary>
    public bool HasOverflow => OverflowedClusters > 0;
}

/// <summary>Result of a pick query: the instance pick id under a pixel, world position and surface normal.</summary>
public readonly record struct PickResult(uint PickId, Vector3 WorldPosition, Vector3 Normal, float Depth);

/// <summary>Progress of a running final (path-traced) render.</summary>
public interface IFinalRender : IDisposable
{
    /// <summary>Samples per pixel accumulated so far (every pixel has at least this many).</summary>
    int SamplesDone { get; }

    /// <summary>Samples per pixel the render aims for (it may complete earlier when a time limit is reached).</summary>
    int SamplesTotal { get; }

    /// <summary>The render has finished: all samples are done, its time limit was reached or it was cancelled.</summary>
    bool IsComplete { get; }

    /// <summary>True when <see cref="Cancel"/> stopped the render (or a cancellation token passed to <see cref="Advance(TimeSpan, CancellationToken)"/>).</summary>
    bool IsCancelled => false;

    /// <summary>Wall-clock time spent rendering so far.</summary>
    TimeSpan Elapsed => TimeSpan.Zero;

    /// <summary>
    /// Exposure the HDR results are scaled by (HDR value = luminance in cd/m² × exposure): from the camera's EV100 and
    /// the compensation, or metered from the image with auto exposure.
    /// </summary>
    float Exposure => 1f;

    /// <summary>Renders more samples for at most <paramref name="budget"/> (keeps the UI responsive).</summary>
    void Advance(TimeSpan budget);

    /// <summary>
    /// Renders more samples for at most <paramref name="budget"/>; returns early when <paramref name="cancellationToken"/>
    /// is cancelled, which also cancels the render (the samples done so far stay available).
    /// </summary>
    void Advance(TimeSpan budget, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            Cancel();
            return;
        }

        Advance(budget);
    }

    /// <summary>Stops the render: no further samples; the accumulated result stays available.</summary>
    void Cancel()
    {
    }

    /// <summary>Tone-mapped preview of the current accumulation (8-bit sRGB RGBA; denoised when the settings ask for it).</summary>
    ImageData Preview();

    /// <summary>Linear HDR result (RGBA float, cd/m² scaled by exposure) for EXR export; denoised when the settings ask for it.</summary>
    ImageData ResultHdr();

    /// <summary>The raw (not denoised) linear HDR result, RGBA float, cd/m² scaled by <see cref="Exposure"/>.</summary>
    ImageData RawResultHdr() => ResultHdr();
}

/// <summary>
/// A renderer instance (one per GPU device). Real-time views are rendered with <see cref="Render"/>; final frames with
/// <see cref="StartFinalRender"/>. Main thread only.
/// </summary>
public interface IRenderer : IDisposable
{
    RendererInfo Info { get; }
    IRenderWorld World { get; }

    IRenderTarget CreateTarget(int width, int height);

    /// <summary>Renders one frame of a view into <paramref name="target"/>.</summary>
    void Render(IRenderTarget target, RenderCamera camera, ViewOptions view, PreviewSettings settings, float deltaTime);

    /// <summary>Reads the last rendered frame of a target as 8-bit sRGB RGBA (blocking; for thumbnails/tests/CLI).</summary>
    ImageData ReadPixels(IRenderTarget target);

    /// <summary>Entity under a pixel of the last frame of <paramref name="target"/> (null = background).</summary>
    PickResult? Pick(IRenderTarget target, int x, int y);

    /// <summary>Starts a progressive path-traced render of the current world.</summary>
    IFinalRender StartFinalRender(RenderCamera camera, FinalRenderSettings settings);

    /// <summary>
    /// Starts a progressive path-traced render of the current world with <paramref name="options"/> (exposure,
    /// background, seed, time limit, firefly clamp). Renderers without these options ignore them.
    /// </summary>
    IFinalRender StartFinalRender(RenderCamera camera, FinalRenderSettings settings, FinalRenderOptions options) => StartFinalRender(camera, settings);
}
