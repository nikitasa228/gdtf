using System.Numerics;
using Candela.App.Thumbnails;
using Candela.Core.Geometry;
using Hexa.NET.ImGui;

namespace Candela.App.Tests;

/// <summary>
/// Library thumbnails and hover previews as Dear ImGui user textures, with a stand-in for the Vulkan backend that
/// follows <c>ImGui_ImplVulkan_UpdateTexture</c>: it uploads a texture that wants to be created and destroys a released
/// one only once its <c>UnusedFrames</c> reached the swapchain's image count. Dear ImGui itself never counts
/// <c>UnusedFrames</c> for user textures, so before the fix no released thumbnail was ever destroyed: hovering the
/// library leaked GPU images and descriptor sets until the backend's descriptor pool ran out and the driver crashed.
/// </summary>
[Collection(UiStateCollection.Name)]
public sealed unsafe class UiTextureLifecycleTests : IDisposable
{
    /// <summary>Swapchain images of the stand-in backend (the editor's swapchain has 2 or 3).</summary>
    private const int ImageCount = 3;

    private readonly HeadlessImGui _imgui = new(new Vector2(800, 600));
    private ulong _nextId = 1;

    public void Dispose() => _imgui.Dispose();

    private static ImageData Picture(int size, byte shade)
    {
        var pixels = new byte[size * size * 4];
        Array.Fill(pixels, shade);
        return new ImageData { Width = size, Height = size, Format = PixelFormat.Rgba8, Pixels = pixels };
    }

    /// <summary>One editor frame: housekeeping, the UI, then the backend's texture updates (as RenderDrawData does).</summary>
    private void Frame(UiTextures textures, IEnumerable<UiTexture>? drawn = null)
    {
        textures.Update();
        _imgui.Frame(() =>
        {
            ImGui.Begin("library");
            foreach (var t in drawn ?? [])
                if (!t.IsReleased)
                    ImGui.Image(t.Ref, new Vector2(t.Width, t.Height));
            ImGui.End();
        });
        var list = ImGui.GetPlatformIO().Textures;
        for (int i = 0; i < list.Size; i++)
        {
            ImTextureData* tex = list.Data[i];
            switch (tex->Status)
            {
                case ImTextureStatus.WantCreate:
                case ImTextureStatus.WantUpdates:
                    if (tex->TexID == new ImTextureID(0UL))
                        tex->TexID = new ImTextureID(_nextId++);
                    tex->Status = ImTextureStatus.Ok;
                    break;
                case ImTextureStatus.WantDestroy when tex->UnusedFrames >= ImageCount:
                    tex->TexID = new ImTextureID(0UL);
                    tex->Status = ImTextureStatus.Destroyed;
                    break;
            }
        }
    }

    [Fact]
    public void Released_thumbnails_are_destroyed_by_the_backend_and_freed()
    {
        Assert.True(UiTextures.IsAvailable);
        int before = UiTextures.TotalExisting;
        using var textures = new UiTextures();
        var created = Enumerable.Range(0, 60).Select(i => textures.Create(Picture(32, (byte)i))!).ToList();
        Assert.All(created, Assert.NotNull);
        Frame(textures, created);
        Frame(textures, created); // uploaded and drawn

        foreach (var t in created)
            textures.Release(t);
        Assert.Equal(60, textures.Statistics.Releasing);

        // the backend waits ImageCount frames (frames in flight), then destroys them; the next Update frees them
        for (int frame = 0; frame < ImageCount + 3; frame++)
            Frame(textures);
        var stats = textures.Statistics;
        Assert.Equal(60, stats.Created);
        Assert.Equal(60, stats.Freed);
        Assert.Equal(0, stats.Releasing);
        Assert.Equal(0, stats.Alive);
        Assert.Equal(before, UiTextures.TotalExisting);
    }

    [Fact]
    public void Hovering_many_tiles_keeps_the_texture_count_bounded()
    {
        int before = UiTextures.TotalExisting;
        using var textures = new UiTextures();
        int peak = 0;
        // 300 hovers of 24-frame orbit previews: each preview lives a few frames, then the next tile's replaces it
        for (int hover = 0; hover < 300; hover++)
        {
            var preview = Enumerable.Range(0, 24).Select(i => textures.Create(Picture(16, (byte)(hover + i)))).ToList();
            Assert.All(preview, Assert.NotNull);
            for (int frame = 0; frame < 4; frame++)
                Frame(textures, preview!);
            foreach (var t in preview)
                textures.Release(t);
            peak = Math.Max(peak, UiTextures.TotalExisting - before);
        }

        for (int frame = 0; frame < ImageCount + 3; frame++)
            Frame(textures);
        Assert.Equal(300 * 24, textures.Statistics.Freed);
        Assert.Equal(0, textures.Statistics.Refused);
        Assert.InRange(peak, 24, 24 * (ImageCount + 2));
        Assert.Equal(before, UiTextures.TotalExisting);
    }

    [Fact]
    public void Creation_stops_at_the_budget_and_resumes_once_textures_are_freed()
    {
        int before = UiTextures.TotalExisting;
        using var textures = new UiTextures();
        var created = new List<UiTexture>();
        while (textures.Create(Picture(4, 1)) is { } t)
            created.Add(t);
        Assert.Equal(UiTextures.Budget - before, created.Count);
        Assert.Equal(1, textures.Statistics.Refused);

        Frame(textures, created);
        foreach (var t in created)
            textures.Release(t);
        // still waiting for the frames in flight: the budget counts them
        Assert.Null(textures.Create(Picture(4, 1)));
        for (int frame = 0; frame < ImageCount + 3; frame++)
            Frame(textures);
        var again = textures.Create(Picture(4, 1));
        Assert.NotNull(again);
        textures.Release(again);
    }

    [Fact]
    public void A_texture_released_before_its_upload_is_freed_at_once()
    {
        int before = UiTextures.TotalExisting;
        using var textures = new UiTextures();
        var t = textures.Create(Picture(8, 7));
        Assert.NotNull(t);
        textures.Release(t);
        Assert.True(t!.IsReleased);
        Assert.Equal(1, textures.Statistics.Freed);
        Assert.Equal(before, UiTextures.TotalExisting);
        textures.Release(t); // twice is harmless
        Assert.Equal(1, textures.Statistics.Freed);
    }
}
