using System.Numerics;
using Candela.App.Session;
using Candela.App.Settings;
using Candela.App.Ui;
using Candela.App.Viewport;
using Candela.Rendering;
using Candela.Rendering.Vulkan;

namespace Candela.App.Tests;

/// <summary>
/// The editor session on a real (headless) Vulkan renderer: the preview stage is built, rendered and picked exactly as
/// the viewport does it (skipped when no Vulkan device exists).
/// </summary>
[Collection(UiStateCollection.Name)]
public sealed class PreviewRenderTests
{
    private static readonly Vector2 Size = new(480, 270);

    private static VulkanRenderer CreateOrSkip()
    {
        try
        {
            return VulkanRenderer.Create(new VulkanContextOptions
            {
                CacheDirectory = Path.Combine(AppContext.BaseDirectory, "vk-cache"),
            });
        }
        catch (VulkanUnavailableException e)
        {
            Skip.If(true, $"No Vulkan device available: {e.Message}");
            throw;
        }
    }

    private static PickResult? RenderAndPick(VulkanRenderer renderer, EditorSession session, IRenderTarget target, ViewportCamera camera, Vector2 pixel)
    {
        session.SyncRender();
        var settings = new PreviewSettings { Volumetrics = false };
        renderer.Render(target, camera.ToRenderCamera(), new ViewOptions(), settings, 1f / 60f);
        return renderer.Pick(target, (int)pixel.X, (int)pixel.Y);
    }

    [SkippableFact]
    public void Clicking_an_object_picks_its_entity_and_hidden_objects_are_not_drawn()
    {
        using var dir = new TempDir();
        Loc.SetLanguage("en");
        var log = TestLog.Create();
        var settings = new SettingsStore(log, dir.File("settings.json"));
        settings.Load();
        using var renderer = CreateOrSkip();
        using var session = EngineTestSupport.CreateSession(log, settings, dir, previewStage: true);
        session.AttachRenderer(renderer);
        var content = Assert.IsType<PreviewStage>(session.Content);
        Assert.Equal(8, session.Scene.Count);
        Assert.False(session.IsModified, "building the preview is not an edit");
        Assert.False(session.Undo.CanUndo);
        Assert.Equal(3, content.Lights.Length);

        var camera = new ViewportCamera();
        using var target = renderer.CreateTarget((int)Size.X, (int)Size.Y);
        var sphere = session.Scene.All().Single(e => e.Name == "Gold sphere");
        Assert.True(camera.WorldToScreen(sphere.WorldPosition, Size, out var pixel));

        var hit = RenderAndPick(renderer, session, target, camera, pixel);
        Assert.NotNull(hit);
        Assert.Equal(sphere.Id, content.EntityForPick(hit!.Value.PickId));
        Assert.True(Vector3.Distance(hit.Value.WorldPosition, sphere.WorldPosition) < 0.7f);

        Assert.True(content.TryGetBounds(sphere.Id, out var bounds));
        Assert.True(Vector3.Distance(bounds.Center, sphere.WorldPosition) < 1e-3f);
        Assert.False(content.SceneBounds.IsEmpty);

        sphere.Visible = false;
        var behind = RenderAndPick(renderer, session, target, camera, pixel);
        Assert.NotEqual(sphere.Id, behind is { } b ? content.EntityForPick(b.PickId) : null);

        sphere.Visible = true;
        session.Scene.Remove(sphere);
        var removed = RenderAndPick(renderer, session, target, camera, pixel);
        Assert.NotEqual(sphere.Id, removed is { } r ? content.EntityForPick(r.PickId) : null);
        Assert.Equal(7, content.InstanceCount);
    }
}
