using System.Numerics;
using Candela.Core.Geometry;
using Candela.Rendering;

namespace Candela.App.Tests;

/// <summary>
/// A renderer without a GPU for tests of what the editor sends to the renderer: its world keeps handles, instances
/// and the light list in memory, and every <see cref="Render"/> records the light list the frame was rendered with.
/// <see cref="ReadPixels"/> returns a grey picture of the target's size.
/// </summary>
internal sealed class MemoryRenderer : IRenderer
{
    /// <summary>The world the engine's render binding fills.</summary>
    public MemoryRenderWorld MemoryWorld { get; } = new();

    /// <summary>The light lists of the rendered frames, oldest first.</summary>
    public List<SpotLight[]> RenderedLights { get; } = new();

    /// <inheritdoc/>
    public RendererInfo Info { get; } = new("memory", "none", "1.3", true, false, true, 0);

    /// <inheritdoc/>
    public IRenderWorld World => MemoryWorld;

    /// <inheritdoc/>
    public IRenderTarget CreateTarget(int width, int height) => new Target(width, height);

    /// <inheritdoc/>
    public void Render(IRenderTarget target, RenderCamera camera, ViewOptions view, PreviewSettings settings, float deltaTime) =>
        RenderedLights.Add(MemoryWorld.Lights.ToArray());

    /// <inheritdoc/>
    public ImageData ReadPixels(IRenderTarget target)
    {
        var pixels = new byte[target.Width * target.Height * 4];
        Array.Fill(pixels, (byte)128);
        return new ImageData { Width = target.Width, Height = target.Height, Format = PixelFormat.Rgba8, Pixels = pixels };
    }

    /// <inheritdoc/>
    public PickResult? Pick(IRenderTarget target, int x, int y) => null;

    /// <inheritdoc/>
    public IFinalRender StartFinalRender(RenderCamera camera, FinalRenderSettings settings) =>
        throw new NotSupportedException("The memory renderer has no path tracer.");

    /// <inheritdoc/>
    public void Dispose()
    {
    }

    private sealed class Target(int width, int height) : IRenderTarget
    {
        public int Width { get; private set; } = width;

        public int Height { get; private set; } = height;

        public void Resize(int width, int height)
        {
            Width = width;
            Height = height;
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>An <see cref="IRenderWorld"/> that only keeps handles, instance flags and the current light list.</summary>
internal sealed class MemoryRenderWorld : IRenderWorld
{
    private int _next = 1;

    /// <summary>Flags of every live instance by handle value.</summary>
    public Dictionary<int, InstanceFlags> Instances { get; } = new();

    /// <summary>The light list last sent.</summary>
    public SpotLight[] Lights { get; private set; } = Array.Empty<SpotLight>();

    public MeshHandle CreateMesh(MeshData mesh) => new(_next++);

    public void DestroyMesh(MeshHandle mesh)
    {
    }

    public TextureHandle CreateTexture(ImageData image, bool srgb) => new(_next++);

    public void DestroyTexture(TextureHandle texture)
    {
    }

    public MaterialHandle CreateMaterial(MaterialData material, IImageSource? images) => new(_next++);

    public void UpdateMaterial(MaterialHandle handle, MaterialData material, IImageSource? images)
    {
    }

    public void DestroyMaterial(MaterialHandle material)
    {
    }

    public InstanceHandle CreateInstance(MeshHandle mesh, ReadOnlySpan<MaterialHandle> materials, Matrix4x4 world, InstanceFlags flags, uint pickId)
    {
        int h = _next++;
        Instances[h] = flags;
        return new InstanceHandle(h);
    }

    public void UpdateInstance(InstanceHandle instance, Matrix4x4 world, InstanceFlags flags) => Instances[instance.Value] = flags;

    public void SetInstanceMaterials(InstanceHandle instance, ReadOnlySpan<MaterialHandle> materials)
    {
    }

    public void DestroyInstance(InstanceHandle instance) => Instances.Remove(instance.Value);

    public void SetInstanceEmission(InstanceHandle instance, Vector3 luminance)
    {
    }

    public void SetLights(ReadOnlySpan<SpotLight> lights) => Lights = lights.ToArray();

    public void SetLines(ReadOnlySpan<LineVertex> segments)
    {
    }

    public void SetAtmosphere(AtmosphereState atmosphere)
    {
    }

    public void SetNoiseSources(ReadOnlySpan<NoiseSource> sources)
    {
    }
}
