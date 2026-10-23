using Candela.Core.Geometry;
using Hexa.NET.ImGui;
using Hexa.NET.ImGui.Backends.Vulkan;

namespace Candela.App.Thumbnails;

/// <summary>
/// A small RGBA8 picture shown by Dear ImGui (a library thumbnail, a frame of a hover preview). The pixels are handed to
/// Dear ImGui as a registered user texture; the renderer backend creates and uploads the GPU image the first time it is
/// drawn, so creating one never touches the GPU directly. Released through <see cref="UiTextures.Release"/>.
/// </summary>
internal sealed unsafe class UiTexture
{
    internal UiTexture(ImTextureData* data, int width, int height)
    {
        Data = data;
        Width = width;
        Height = height;
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>The reference for <c>ImGui.Image</c> / <c>ImDrawList.AddImage</c> (valid until released).</summary>
    public ImTextureRef Ref => new(Data, default);

    /// <summary>True once released (must not be drawn any more).</summary>
    public bool IsReleased => Data == null;

    internal ImTextureData* Data { get; set; }
}

/// <summary>Counters of a <see cref="UiTextures"/> (diagnostics and tests).</summary>
/// <param name="Created">Textures created so far.</param>
/// <param name="Freed">Textures destroyed by the backend (or never uploaded) and freed.</param>
/// <param name="Alive">Textures created and not released.</param>
/// <param name="Releasing">Released textures the backend has not destroyed yet (frames in flight may still sample them).</param>
/// <param name="Refused">Creations refused because <see cref="UiTextures.Budget"/> textures existed already.</param>
internal readonly record struct UiTextureStatistics(long Created, long Freed, int Alive, int Releasing, long Refused);

/// <summary>
/// Creates and releases <see cref="UiTexture"/>s. Textures are registered with Dear ImGui 1.92's texture system
/// (<c>RegisterUserTexture</c>): the Vulkan backend uploads them during the next frame and destroys a released one only
/// once it has been unused for as many frames as the swapchain has images (<c>UnusedFrames</c> ≥ image count), so
/// releasing one in the middle of a frame is safe. Dear ImGui counts <c>UnusedFrames</c> only for its own font atlas
/// textures, never for user textures: <see cref="Update"/> counts it for the released ones — without that, no released
/// thumbnail was ever destroyed, every hover preview leaked 24 GPU images and descriptor sets, and once the backend's
/// descriptor pool was exhausted the next texture got an invalid descriptor set and the driver crashed. All instances
/// together never hold more than <see cref="Budget"/> textures (alive or waiting for the GPU); beyond it
/// <see cref="Create"/> returns null and the caller shows its fallback icon. Without a renderer that manages textures
/// (headless tests) <see cref="IsAvailable"/> is false and nothing is created. Main thread only.
/// </summary>
internal sealed unsafe class UiTextures : IDisposable
{
    /// <summary>
    /// Most textures of all <see cref="UiTextures"/> instances together, alive or waiting to be destroyed: well inside the
    /// combined image samplers of the Dear ImGui backend's descriptor pool (512, shared with the font atlas and the
    /// viewports' render targets).
    /// </summary>
    public const int Budget = 400;

    private static int s_total;

    private readonly List<nint> _live = new();
    private readonly List<nint> _releasing = new();
    private long _created;
    private long _freed;
    private long _refused;
    private bool _disposed;

    /// <summary>True when the current Dear ImGui backend can create user textures.</summary>
    public static bool IsAvailable =>
        !ImGui.GetCurrentContext().IsNull && (ImGui.GetIO().BackendFlags & ImGuiBackendFlags.RendererHasTextures) != 0;

    /// <summary>Textures of every instance that exist now (alive or waiting for the backend to destroy them).</summary>
    public static int TotalExisting => s_total;

    /// <summary>Number of textures alive (created, not yet released).</summary>
    public int Count => _live.Count;

    /// <summary>Counters of this instance.</summary>
    public UiTextureStatistics Statistics => new(_created, _freed, _live.Count, _releasing.Count, _refused);

    /// <summary>
    /// A texture with the pixels of <paramref name="image"/> (RGBA8), or null when no texture can be created (headless,
    /// another pixel format, an empty or oversized image, or <see cref="Budget"/> textures exist already).
    /// </summary>
    public UiTexture? Create(ImageData image)
    {
        ArgumentNullException.ThrowIfNull(image);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsAvailable || image.Format != PixelFormat.Rgba8 || image.Width <= 0 || image.Height <= 0 || image.Width > 4096 || image.Height > 4096 ||
            image.Pixels.Length < (long)image.Width * image.Height * 4)
            return null;
        if (s_total >= Budget)
        {
            _refused++;
            return null;
        }

        ImTextureDataPtr tex = ImGui.ImTextureData();
        tex.Create(ImTextureFormat.Rgba32, image.Width, image.Height);
        byte* pixels = (byte*)tex.GetPixels();
        if (pixels == null)
        {
            ImGui.Destroy(tex);
            return null;
        }

        fixed (byte* source = image.Pixels)
            Buffer.MemoryCopy(source, pixels, (long)image.Width * image.Height * 4, (long)image.Width * image.Height * 4);
        tex.UseColors = true;
        tex.Status = ImTextureStatus.WantCreate;
        ImGuiP.RegisterUserTexture(tex);
        _live.Add((nint)tex.Handle);
        _created++;
        s_total++;
        return new UiTexture(tex.Handle, image.Width, image.Height);
    }

    /// <summary>Releases a texture: the backend destroys it once no frame in flight uses it.</summary>
    public void Release(UiTexture? texture)
    {
        if (texture is null || texture.IsReleased)
            return;
        var data = texture.Data;
        texture.Data = null;
        _live.Remove((nint)data);
        if (_disposed)
            return;
        if (data->Status == ImTextureStatus.WantCreate || data->Status == ImTextureStatus.Destroyed)
        {
            // Never uploaded (or already gone): nothing on the GPU to wait for.
            Free(data);
            return;
        }

        data->Status = ImTextureStatus.WantDestroy;
        // Counted up by Update once per frame from here on; the backend destroys the texture when it reaches its image count.
        data->UnusedFrames = 0;
        _releasing.Add((nint)data);
    }

    /// <summary>
    /// Per-frame housekeeping (UI thread, once per frame, before the frame is drawn): counts the frames each released
    /// texture has been unused for (what the backend waits on before destroying it) and frees the textures the backend
    /// has destroyed. The CPU copy of the pixels is kept while a texture lives (a backend that recreates its device
    /// objects uploads it again).
    /// </summary>
    public void Update()
    {
        for (int i = _releasing.Count - 1; i >= 0; i--)
        {
            var data = (ImTextureData*)_releasing[i];
            if (data->Status == ImTextureStatus.WantDestroy)
            {
                if (data->UnusedFrames < int.MaxValue / 2)
                    data->UnusedFrames++;
                continue;
            }

            if (data->Status != ImTextureStatus.Destroyed)
                continue;
            _releasing.RemoveAt(i);
            Free(data);
        }
    }

    /// <summary>
    /// Destroys every texture now (the GPU must be idle and the backend alive: called before the Dear ImGui backend shuts
    /// down). Without a renderer backend (headless) only the CPU side is freed.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        bool backend = !ImGui.GetCurrentContext().IsNull && ImGui.GetIO().BackendRendererUserData != null;
        foreach (var handle in _live.Concat(_releasing))
        {
            var data = (ImTextureData*)handle;
            if (backend && data->Status is ImTextureStatus.Ok or ImTextureStatus.WantUpdates or ImTextureStatus.WantDestroy &&
                data->TexID != new ImTextureID(0UL))
            {
                data->Status = ImTextureStatus.WantDestroy;
                data->UnusedFrames = int.MaxValue / 2;
                ImGuiImplVulkan.UpdateTexture(data);
            }

            Free(data);
        }

        _live.Clear();
        _releasing.Clear();
    }

    private void Free(ImTextureData* data)
    {
        if (data->RefCount > 0)
            ImGuiP.UnregisterUserTexture(data);
        ImGui.Destroy(data);
        _freed++;
        s_total = Math.Max(0, s_total - 1);
    }
}
