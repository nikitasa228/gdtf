using System.Numerics;
using System.Runtime.InteropServices;
using Candela.App.Diagnostics;
using Candela.App.Session;
using Candela.App.Settings;
using Candela.App.Thumbnails;
using Candela.App.Ui;
using Candela.App.Viewport;
using Candela.Assets;
using Candela.Core.Geometry;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Rendering;
using Candela.Rendering.Vulkan;

namespace Candela.App.Tests;

/// <summary>
/// The viewports over the real engine scene on a (headless) Vulkan renderer: Фокина drawn by the engine's render
/// binding, picks mapped back to fixtures, real bounds, the selection outline flags, tight framing; and the library's
/// background thumbnail renderer. Skipped without a Vulkan device.
/// </summary>
[Collection(UiStateCollection.Name)]
public sealed class EngineViewportTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly AppLog _log = TestLog.Create();
    private readonly SettingsStore _settings;

    public EngineViewportTests()
    {
        Loc.SetLanguage("en");
        _settings = new SettingsStore(_log, _dir.File("settings.json"));
        _settings.Load();
    }

    public void Dispose()
    {
        _log.Dispose();
        _dir.Dispose();
    }

    private static VulkanRenderer CreateOrSkip()
    {
        try
        {
            return VulkanRenderer.Create(new VulkanContextOptions { CacheDirectory = Path.Combine(AppContext.BaseDirectory, "vk-cache") });
        }
        catch (VulkanUnavailableException e)
        {
            Skip.If(true, $"No Vulkan device available: {e.Message}");
            throw;
        }
    }

    [SkippableFact]
    public void Fokina_is_drawn_picked_bounded_outlined_and_framed()
    {
        using var renderer = CreateOrSkip();
        using var session = EngineTestSupport.CreateSession(_log, _settings, _dir);
        Assert.True(session.Open(EngineTestSupport.Fokina).Succeeded);
        session.AttachRenderer(renderer);
        var content = Assert.IsType<EngineViewportContent>(session.Content);
        session.Update(1 / 30.0);
        session.SyncRender();
        Assert.True(content.IsSettled);
        Assert.True(content.World.InstanceCount > 91);

        // Framing: every point of the content is inside the picture.
        var size = new Vector2(640, 360);
        var camera = new ViewportCamera();
        var points = new List<Vector3>();
        content.CollectFramingPoints(points);
        Assert.True(points.Count > 91 * 8);
        Assert.True(camera.FramePoints(CollectionsMarshal.AsSpan(points), size.X / size.Y));
        foreach (var p in points)
        {
            Assert.True(camera.WorldToScreen(p, size, out var s));
            Assert.InRange(s.X, -1f, size.X + 1f);
            Assert.InRange(s.Y, -1f, size.Y + 1f);
        }

        using var target = renderer.CreateTarget((int)size.X, (int)size.Y);
        void Render()
        {
            session.SyncRender();
            renderer.Render(target, camera.ToRenderCamera(), new ViewOptions { Shading = ViewShading.Colorless },
                new PreviewSettings { Volumetrics = false, TemporalAccumulation = false, Bloom = false }, 1f / 30f);
        }

        Render();
        // Clicking any part of a fixture selects the fixture entity.
        var fixtures = session.Scene.Query<FixtureComponent>().Select(q => q.Entity).ToList();
        int hits = 0, tried = 0;
        foreach (var fixture in fixtures)
        {
            Assert.True(content.TryGetBounds(fixture.Id, out var bounds));
            Assert.True(bounds.Contains(fixture.WorldPosition) || Vector3.Distance(bounds.Center, fixture.WorldPosition) < 1.5f);
            if (!camera.WorldToScreen(bounds.Center, size, out var pixel))
                continue;
            tried++;
            var hit = renderer.Pick(target, (int)pixel.X, (int)pixel.Y);
            if (hit is { } h && content.EntityForPick(h.PickId) is { } id)
            {
                Assert.NotNull(session.Scene.Find(id)!.Get<FixtureComponent>()); // never a part entity
                if (id == fixture.Id)
                    hits++;
            }
        }

        Assert.True(hits > tried / 2, $"{hits} of {tried} fixture centres picked their fixture");

        // Selection outline: the selected fixture's instances carry the Selected flag; a selected group outlines its members.
        var first = fixtures[0];
        uint pick = content.Binding.PickIdOf(first.Id);
        session.Selection.Set(first.Id);
        Render();
        Assert.True((content.World.EffectiveFlags(pick)!.Value & InstanceFlags.Selected) != 0);
        session.Selection.Clear();
        Render();
        Assert.Equal((InstanceFlags)0, content.World.EffectiveFlags(pick)!.Value & InstanceFlags.Selected);

        var group = session.Scene.Add(new Entity("group"));
        session.Scene.Reparent(first, group);
        session.Selection.Set(group.Id);
        Render();
        Assert.True((content.World.EffectiveFlags(content.Binding.PickIdOf(first.Id))!.Value & InstanceFlags.Selected) != 0);
        Assert.True(content.TryGetBounds(group.Id, out var groupBounds) && !groupBounds.IsEmpty);
    }

    [SkippableFact]
    public void The_thumbnail_worker_renders_fixture_pictures_and_image_previews_in_the_background()
    {
        using var probe = CreateOrSkip();
        string device = probe.Info.DeviceName;
        var entry = EngineTestSupport.Library.Entries.First(e => e.FileName.Contains("Robin_T1", StringComparison.OrdinalIgnoreCase));
        var store = new ThumbnailStore(_dir.File("thumbs"));
        string png = _dir.File("picture.png");
        var pixels = new byte[64 * 32 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)(i % 251);
            pixels[i + 3] = 255;
        }

        ImageFiles.WritePng(new ImageData { Width = 64, Height = 32, Format = PixelFormat.Rgba8, Pixels = pixels }, png);
        using var worker = new ThumbnailWorker(EngineTestSupport.Library, store, _log, device, Path.Combine(AppContext.BaseDirectory, "vk-cache"));
        string still = ThumbnailWorker.StillKey(entry, 96);
        string orbit = ThumbnailWorker.OrbitKey(entry, 96, 6);
        string image = ThumbnailWorker.ImageKey(png, 48);
        worker.SetWanted(new[]
        {
            new ThumbnailRequest(still, ThumbnailKind.FixtureStill, entry, null, 96),
            new ThumbnailRequest(orbit, ThumbnailKind.FixtureOrbit, entry, null, 96, MathF.PI / 2),
            new ThumbnailRequest(image, ThumbnailKind.Image, null, png, 48),
        });
        var results = new Dictionary<string, ImageData?>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
        while (results.Count < 3 && DateTime.UtcNow < deadline)
        {
            while (worker.TryTake(out var r))
                results[r.Key] = r.Image;
            Thread.Sleep(20);
        }

        Assert.Null(worker.Error);
        var stillImage = results[still]!;
        Assert.Equal((96, 96), (stillImage.Width, stillImage.Height));
        Assert.True(Distinct(stillImage) > 20, "the thumbnail shows the fixture, not a flat colour");
        Assert.False(stillImage.Pixels.AsSpan().SequenceEqual(results[orbit]!.Pixels), "another angle gives another picture");
        Assert.Equal(48, results[image]!.Width);
        // The still picture went to the disk cache; asking again after it was made is not needed.
        Assert.NotEmpty(Directory.GetFiles(_dir.File("thumbs"), "*", SearchOption.AllDirectories));
        Assert.Equal(0, worker.Pending);
    }

    private static int Distinct(ImageData image)
    {
        var set = new HashSet<int>();
        for (int i = 0; i < image.Pixels.Length; i += 4)
            set.Add(image.Pixels[i] >> 3 << 10 | image.Pixels[i + 1] >> 3 << 5 | image.Pixels[i + 2] >> 3);
        return set.Count;
    }
}
