using System.Numerics;
using Candela.App.Diagnostics;
using Candela.App.Session;
using Candela.App.Settings;
using Candela.App.Ui;
using Candela.App.Viewport;
using Candela.Core.Geometry;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Engine;
using Candela.Rendering;
using Candela.Rendering.Vulkan;

namespace Candela.App.Tests;

/// <summary>
/// The editor's lighting baseline: the default Full view of a freshly opened MVR, an imported model, a poster or a
/// reference drawing is never black. The work light (Auto by default) lights a dark rig, is saved with the project's
/// view settings and leaves the stage dark when Off; a floorless MVR gets the engine's stage floor as one undoable step;
/// the scene's ambient luminance reaches the renderer; a reference drawing shows its image.
/// </summary>
[Collection(UiStateCollection.Name)]
public sealed class LightingBaselineTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly AppLog _log = TestLog.Create();
    private readonly SettingsStore _settings;

    public LightingBaselineTests()
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

    private static List<Entity> Floors(Scene scene)
    {
        var floors = new List<Entity>();
        foreach (var (entity, wall) in scene.Query<WallComponent>())
        {
            if (wall.Kind == WallKind.Floor)
                floors.Add(entity);
        }

        return floors;
    }

    private static void Frame(EditorSession session)
    {
        session.Update(1 / 30.0);
        session.SyncRender();
    }

    [Fact]
    public void Opening_Fokina_turns_the_work_light_on_over_a_stage_floor_that_Undo_removes()
    {
        using var session = EngineTestSupport.CreateSession(_log, _settings, _dir);
        using var renderer = new MemoryRenderer();
        session.AttachRenderer(renderer);
        var opened = session.Open(EngineTestSupport.Fokina);
        Assert.True(opened.Succeeded, opened.Message);
        var engine = session.Engine!;
        var content = Assert.IsType<EngineViewportContent>(session.Content);
        Frame(session);

        // The rig is dark when the file opens: the default Auto work light lights it.
        Assert.Equal(WorkLightMode.Auto, engine.View.WorkLight);
        Assert.Equal(WorkLightMode.Auto, content.Binding.WorkLight);
        Assert.True(content.Binding.IsWorkLightOn);
        Assert.Equal(WorkLightRig.LightCount, renderer.MemoryWorld.Lights.Length - engine.Lights.Length);

        // The export holds only the rig: the session added the engine's stage floor under all 91 fixtures.
        var floor = Assert.Single(Floors(session.Scene));
        Assert.Equal(Loc.Get(Str.StageFloorName), floor.Name);
        Assert.Contains(Loc.Get(Str.MsgStageFloorAdded), opened.Message, StringComparison.Ordinal);
        var size = StageFloor.SizeOf(floor);
        var centre = floor.WorldPosition;
        Assert.True(centre.Z <= 0f);
        var fixtures = session.Scene.Query<FixtureComponent>().Select(q => q.Entity).ToList();
        Assert.Equal(91, fixtures.Count);
        foreach (var fixture in fixtures)
        {
            var p = fixture.WorldPosition;
            Assert.InRange(p.X, centre.X - 0.5f * size.X + StageFloor.Margin - 0.5f, centre.X + 0.5f * size.X - StageFloor.Margin + 0.5f);
            Assert.InRange(p.Y, centre.Y - 0.5f * size.Y + StageFloor.Margin - 0.5f, centre.Y + 0.5f * size.Y - StageFloor.Margin + 0.5f);
            Assert.True(p.Z >= centre.Z);
        }

        Assert.True(content.Binding.TryGetWorldBounds(floor.Id, out _), "the floor is drawn");

        // One undo step removes it (and the render world drops it); redo brings it back.
        Assert.True(session.Undo.CanUndo);
        session.Undo.Undo();
        Assert.Empty(Floors(session.Scene));
        Assert.False(session.Undo.CanUndo);
        Frame(session);
        Assert.False(content.Binding.TryGetWorldBounds(floor.Id, out _));
        session.Undo.Redo();
        Assert.Same(floor, Assert.Single(Floors(session.Scene)));

        // A project opens as it was saved: without the floor the user removed, nothing is added.
        session.Undo.Undo();
        string project = _dir.File("fokina.candela");
        Assert.True(session.SaveAs(project).Succeeded);
        var reopened = session.Open(project);
        Assert.True(reopened.Succeeded, reopened.Message);
        Assert.Empty(Floors(session.Scene));
        Assert.False(session.Undo.CanUndo);
        Assert.DoesNotContain(Loc.Get(Str.MsgStageFloorAdded), reopened.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_MVR_with_a_floor_keeps_it_and_gets_no_second_one()
    {
        using var session = EngineTestSupport.CreateSession(_log, _settings, _dir);
        var engine = session.Engine!;
        StageTestSupport.AddWall(engine, "Venue floor", 12f, 8f, WallKind.Floor);
        StageTestSupport.AddFixture(engine, "Spot", 1, new Vector3(0f, 0f, 5f));
        string mvr = _dir.File("with-floor.mvr");
        var exported = session.Export(mvr, ExportKind.Mvr);
        Assert.True(exported.Succeeded, exported.Message);

        var opened = session.Open(mvr);
        Assert.True(opened.Succeeded, opened.Message);
        Assert.Single(Floors(session.Scene));
        Assert.False(session.Undo.CanUndo);
        Assert.Null(session.AddStageFloorIfMissing());
    }

    [Fact]
    public void The_work_light_is_saved_with_the_project_and_Off_leaves_the_dark_stage_dark()
    {
        using var session = EngineTestSupport.CreateSession(_log, _settings, _dir);
        using var renderer = new MemoryRenderer();
        session.AttachRenderer(renderer);
        Assert.True(session.Open(EngineTestSupport.Fokina).Succeeded);
        var engine = session.Engine!;
        var content = Assert.IsType<EngineViewportContent>(session.Content);
        Frame(session);
        Assert.True(content.Binding.IsWorkLightOn);

        // Off: the stage light only, as before the work light existed — no fixture emits, so no light at all.
        Assert.True(ViewSettingsEdits.SetWorkLight(engine, WorkLightMode.Off));
        Frame(session);
        Assert.False(content.Binding.IsWorkLightOn);
        Assert.Empty(engine.Lights.ToArray());
        Assert.Empty(renderer.MemoryWorld.Lights);

        // Saved with the project's view settings and restored on reopen (a new scene starts from the default Auto).
        string project = _dir.File("dark.candela");
        Assert.True(session.SaveAs(project).Succeeded);
        Assert.True(session.New().Succeeded);
        Assert.Equal(WorkLightMode.Auto, engine.View.WorkLight);
        Assert.True(session.Open(project).Succeeded);
        Assert.Equal(WorkLightMode.Off, engine.View.WorkLight);
        Frame(session);
        Assert.Equal(WorkLightMode.Off, content.Binding.WorkLight);
        Assert.False(content.Binding.IsWorkLightOn);
        Assert.Empty(renderer.MemoryWorld.Lights);
        Assert.False(engine.IsModified);

        // On is restored the same way.
        Assert.True(ViewSettingsEdits.SetWorkLight(engine, WorkLightMode.On));
        Assert.True(session.Save().Succeeded);
        Assert.True(session.Open(project).Succeeded);
        Assert.Equal(WorkLightMode.On, engine.View.WorkLight);
        Frame(session);
        Assert.True(content.Binding.IsWorkLightOn);
        Assert.Equal(WorkLightRig.LightCount, renderer.MemoryWorld.Lights.Length);
    }

    [Fact]
    public void The_ambient_row_reaches_the_renderer()
    {
        using var session = EngineTestSupport.CreateSession(_log, _settings, _dir);
        using var renderer = new MemoryRenderer();
        session.AttachRenderer(renderer);
        var content = Assert.IsType<EngineViewportContent>(session.Content);
        StageTestSupport.AddWall(session.Engine!, "Floor", 8f, 6f, WallKind.Floor);
        Frame(session);
        // A new scene's surroundings: a dark venue (SceneSettings' default), passed on as it is.
        Assert.Equal(session.Scene.Settings.AmbientLuminance, renderer.MemoryWorld.Atmosphere!.AmbientLuminance);
        Assert.InRange(renderer.MemoryWorld.Atmosphere!.AmbientLuminance, 0f, 0.1f);

        session.Scene.Settings.AmbientLuminance = 30f;
        session.SyncRender();
        Assert.Equal(30f, content.Binding.AmbientLuminance);
        Assert.Equal(30f, renderer.MemoryWorld.Atmosphere!.AmbientLuminance);

        // Damaged values light nothing.
        session.Scene.Settings.AmbientLuminance = float.NaN;
        session.SyncRender();
        Assert.Equal(0f, renderer.MemoryWorld.Atmosphere!.AmbientLuminance);
        session.Scene.Settings.AmbientLuminance = -4f;
        session.SyncRender();
        Assert.Equal(0f, renderer.MemoryWorld.Atmosphere!.AmbientLuminance);
    }

    // ------------------------------------------------------------------ GPU (headless screenshots)

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

    /// <summary>sRGB colours of the texture's halves: red on the left, green on the right.</summary>
    private static readonly (byte R, byte G, byte B) Left = (220, 40, 30), Right = (40, 190, 50);

    /// <summary>
    /// Writes a textured OBJ: a 2 m × 2 m two-sided panel standing on Z = 0 (OBJ is Y-up), with an MTL whose
    /// <c>map_Kd</c> is a PNG of two colour halves.
    /// </summary>
    private string WriteTexturedObj()
    {
        string folder = _dir.File("model");
        Directory.CreateDirectory(folder);
        const int n = 32;
        var pixels = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                var c = x < n / 2 ? Left : Right;
                int i = 4 * (y * n + x);
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (c.R, c.G, c.B, 255);
            }
        }

        ImageFiles.WritePng(new ImageData { Width = n, Height = n, Pixels = pixels }, Path.Combine(folder, "print.png"));
        File.WriteAllText(Path.Combine(folder, "panel.mtl"), "newmtl print\nKd 1 1 1\nmap_Kd print.png\n");
        string obj = Path.Combine(folder, "panel.obj");
        File.WriteAllText(obj, "mtllib panel.mtl\n" +
                               "v -1 0 0\nv 1 0 0\nv 1 2 0\nv -1 2 0\n" +
                               "vt 0 0\nvt 1 0\nvt 1 1\nvt 0 1\n" +
                               "vn 0 0 1\nvn 0 0 -1\n" +
                               "usemtl print\n" +
                               "f 1/1/1 2/2/1 3/3/1 4/4/1\n" +
                               "f 4/4/2 3/3/2 2/2/2 1/1/2\n");
        return obj;
    }

    /// <summary>The sRGB display pixel at a world point.</summary>
    private static (int R, int G, int B) PixelAt(ImageData display, ViewportCamera camera, Vector3 world)
    {
        var size = new Vector2(display.Width, display.Height);
        Assert.True(camera.WorldToScreen(world, size, out var p));
        int x = Math.Clamp((int)p.X, 0, display.Width - 1), y = Math.Clamp((int)p.Y, 0, display.Height - 1);
        int i = 4 * (y * display.Width + x);
        return (display.Pixels[i], display.Pixels[i + 1], display.Pixels[i + 2]);
    }

    /// <summary>Mean display luminance (0 … 1, Rec.709 weights on the sRGB values) over the panel's projection.</summary>
    private static float MeanLuminance(ImageData display, ViewportCamera camera)
    {
        double sum = 0;
        int count = 0;
        for (float u = -0.9f; u <= 0.9f; u += 0.1f)
        {
            for (float v = 0.1f; v <= 1.9f; v += 0.1f)
            {
                var (r, g, b) = PixelAt(display, camera, new Vector3(u, 0f, v));
                sum += (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
                count++;
            }
        }

        return (float)(sum / count);
    }

    [SkippableFact]
    public void A_textured_model_dropped_into_an_empty_scene_shows_its_texture_in_Full_shading()
    {
        using var renderer = CreateOrSkip();
        using var session = EngineTestSupport.CreateSession(_log, _settings, _dir);
        session.AttachRenderer(renderer);
        var engine = session.Engine!;
        var content = Assert.IsType<EngineViewportContent>(session.Content);
        var imported = session.Import(WriteTexturedObj(), ImportKind.Model);
        Assert.True(imported.Succeeded, imported.Message);
        Frame(session);
        Assert.True(content.Binding.IsWorkLightOn);

        // The viewport's own camera (EV 8) from the audience side, a little above.
        var camera = new ViewportCamera { Target = new Vector3(0f, 0f, 1f), Yaw = 0f, Pitch = 0.15f, Distance = 5f };
        using var target = renderer.CreateTarget(160, 120);
        var view = new ViewOptions { ShowGrid = false };
        var preview = new PreviewSettings { Bloom = false };
        ImageData Render(string name)
        {
            session.SyncRender();
            for (int frame = 0; frame < 4; frame++)
                renderer.Render(target, camera.ToRenderCamera(), view, preview, 1f / 30f);
            var pixels = renderer.ReadPixels(target);
            ImageFiles.WritePng(pixels, Path.Combine(AppContext.BaseDirectory, "lighting-baseline-" + name + ".png"));
            return pixels;
        }

        // Full shading with the Auto work light (a dim room light, at the viewport's default EV 8): lit, and the
        // texture's two halves show their colours.
        var lit = Render("work-light");
        Assert.True(MeanLuminance(lit, camera) > 0.03f, $"the model is (nearly) black: {MeanLuminance(lit, camera)}");
        var left = PixelAt(lit, camera, new Vector3(-0.5f, 0f, 1f));
        var right = PixelAt(lit, camera, new Vector3(0.5f, 0f, 1f));
        AssertDominant(left, 0, 20, "the left half (red)");
        AssertDominant(right, 1, 20, "the right half (green)");

        // Off: the old behaviour — no stage light, so the model is black.
        ViewSettingsEdits.SetWorkLight(engine, WorkLightMode.Off);
        var dark = Render("off");
        Assert.False(content.Binding.IsWorkLightOn);
        Assert.True(MeanLuminance(dark, camera) < 0.02f, $"the model is lit without any light: {MeanLuminance(dark, camera)}");

        // Ambient 30 cd/m² (Preview → Ambient light) lights it again without any light: the surroundings of the venue.
        session.Scene.Settings.AmbientLuminance = 30f;
        var ambient = Render("ambient");
        Assert.True(MeanLuminance(ambient, camera) > 0.03f, $"the ambient does not light the model: {MeanLuminance(ambient, camera)}");
        AssertDominant(PixelAt(ambient, camera, new Vector3(-0.5f, 0f, 1f)), 0, 30, "ambient, the left half (red)");
        AssertDominant(PixelAt(ambient, camera, new Vector3(0.5f, 0f, 1f)), 1, 30, "ambient, the right half (green)");
    }

    [SkippableFact]
    public void A_reference_drawing_shows_its_image_in_Full_and_Colorless_and_a_poster_stays_lit()
    {
        using var renderer = CreateOrSkip();
        using var session = EngineTestSupport.CreateSession(_log, _settings, _dir);
        session.AttachRenderer(renderer);
        var engine = session.Engine!;
        ViewSettingsEdits.SetWorkLight(engine, WorkLightMode.Off); // no light at all: a drawing needs none
        string folder = _dir.File("images");
        Directory.CreateDirectory(folder);
        string png = Path.Combine(folder, "plan.png");
        WriteHalves(png);

        // A drawing standing at the origin, facing the audience (−Y), 4 m wide.
        var drawing = session.Import(png, ImportKind.Image, new EngineImportOptions { ImageMode = ImageMode.Reference, ImageWidth = 4f });
        Assert.True(drawing.Succeeded, drawing.Message);
        var entity = session.Scene.Find(Assert.Single(drawing.Entities))!;
        Assert.Equal(ImageMode.Reference, entity.Get<ImageComponent>()!.Mode);
        Frame(session);

        var camera = new ViewportCamera { Target = entity.WorldPosition, Yaw = 0f, Pitch = 0f, Distance = 6f };
        using var target = renderer.CreateTarget(128, 128);
        var full = new ViewOptions { ShowGrid = false };
        var colorless = new ViewOptions { ShowGrid = false, Shading = ViewShading.Colorless };
        var preview = new PreviewSettings { Bloom = false };
        ImageData Render(ViewOptions view)
        {
            session.SyncRender();
            for (int frame = 0; frame < 3; frame++)
                renderer.Render(target, camera.ToRenderCamera(), view, preview, 1f / 30f);
            return renderer.ReadPixels(target);
        }

        // Where the image's left and right halves lie: entity space ±1 m along the image's width.
        var world = entity.WorldMatrix;
        var leftPoint = Vector3.Transform(new Vector3(-1f, 0f, 0f), world);
        var rightPoint = Vector3.Transform(new Vector3(1f, 0f, 0f), world);
        foreach (var (view, name) in new[] { (full, "Full"), (colorless, "Colorless") })
        {
            var display = Render(view);
            var left = PixelAt(display, camera, leftPoint);
            var right = PixelAt(display, camera, rightPoint);
            AssertDominant(left, 0, 80, $"{name}: the drawing's left half (red)");
            AssertDominant(right, 1, 80, $"{name}: the drawing's right half (green)");
        }

        // A poster is a lit print: dark without light, its image under the work light.
        Assert.True(session.Undo.CanUndo);
        session.Undo.Undo();
        var poster = session.Import(png, ImportKind.Image, new EngineImportOptions { ImageMode = ImageMode.Poster, ImageWidth = 4f });
        Assert.True(poster.Succeeded, poster.Message);
        var posterEntity = session.Scene.Find(Assert.Single(poster.Entities))!;
        Frame(session);
        world = posterEntity.WorldMatrix;
        leftPoint = Vector3.Transform(new Vector3(-1f, 0f, 0f), world);
        rightPoint = Vector3.Transform(new Vector3(1f, 0f, 0f), world);
        var unlit = Render(full);
        var (r, g, b) = PixelAt(unlit, camera, leftPoint);
        Assert.True(r + g + b < 15, $"an unlit poster glows: {(r, g, b)}");
        ViewSettingsEdits.SetWorkLight(engine, WorkLightMode.On);
        var shown = Render(full);
        var posterLeft = PixelAt(shown, camera, leftPoint);
        var posterRight = PixelAt(shown, camera, rightPoint);
        AssertDominant(posterLeft, 0, 20, "the poster's left half (red)");
        AssertDominant(posterRight, 1, 20, "the poster's right half (green)");
    }

    /// <summary>
    /// The display pixel shows a colour whose channel <paramref name="channel"/> dominates: at least
    /// <paramref name="minimum"/> (of 255) and 1.5 × each other channel (the tone mapper desaturates a little).
    /// </summary>
    private static void AssertDominant((int R, int G, int B) pixel, int channel, int minimum, string what)
    {
        int own = channel == 0 ? pixel.R : channel == 1 ? pixel.G : pixel.B;
        int other1 = channel == 0 ? pixel.G : pixel.R;
        int other2 = channel == 2 ? pixel.G : pixel.B;
        Assert.True(own >= minimum && own > 1.5f * other1 && own > 1.5f * other2, $"{what} shows as {pixel}");
    }

    private static void WriteHalves(string path)
    {
        const int w = 32, h = 16;
        var pixels = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                var c = x < w / 2 ? Left : Right;
                int i = 4 * (y * w + x);
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (c.R, c.G, c.B, 255);
            }
        }

        ImageFiles.WritePng(new ImageData { Width = w, Height = h, Pixels = pixels }, path);
    }
}
