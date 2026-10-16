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
}

/// <summary>Result of a pick query: the instance pick id under a pixel, world position and surface normal.</summary>
public readonly record struct PickResult(uint PickId, Vector3 WorldPosition, Vector3 Normal, float Depth);

/// <summary>Progress of a running final (path-traced) render.</summary>
public interface IFinalRender : IDisposable
{
    int SamplesDone { get; }
    int SamplesTotal { get; }
    bool IsComplete { get; }

    /// <summary>Renders more samples for at most <paramref name="budget"/> (keeps the UI responsive).</summary>
    void Advance(TimeSpan budget);

    /// <summary>Tone-mapped preview of the current accumulation (8-bit sRGB RGBA).</summary>
    ImageData Preview();

    /// <summary>Linear HDR result (RGBA float, cd/m² scaled by exposure) for EXR export.</summary>
    ImageData ResultHdr();
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
}
