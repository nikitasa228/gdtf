using System.Numerics;
using Candela.Assets;
using Candela.Core.Geometry;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Rendering.Vulkan;

namespace Candela.Engine.Tests;

/// <summary>
/// The engine's side of the editor's lighting baseline: the stage floor for floorless scenes (<see cref="StageFloor"/>,
/// shared by the CLI's <c>--floor auto</c> and the editor), the scene's ambient luminance on its way to the renderer, and
/// reference drawings in CLI renders (real-time and path-traced).
/// </summary>
public sealed class StageFloorTests
{
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

    private static Entity AddFloorWall(Scene scene, bool hidden = false)
    {
        var floor = new Entity("Venue floor") { Visible = !hidden };
        floor.Add(new WallComponent { Kind = WallKind.Floor, Width = 10f, Height = 8f, Thickness = 0.2f });
        return scene.Add(floor);
    }

    /// <summary>An OBJ box (Y-up file: <paramref name="x"/> wide, <paramref name="height"/> tall, <paramref name="depth"/> deep) on Z = 0.</summary>
    private static string WriteBox(string dir, string name, float x, float height, float depth)
    {
        string path = Path.Combine(dir, name + ".obj");
        float hx = 0.5f * x, hz = 0.5f * depth;
        string V(float a, float b, float c) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"v {a} {b} {c}\n");
        File.WriteAllText(path,
            V(-hx, 0, -hz) + V(hx, 0, -hz) + V(hx, height, -hz) + V(-hx, height, -hz) +
            V(-hx, 0, hz) + V(hx, 0, hz) + V(hx, height, hz) + V(-hx, height, hz) +
            "f 1 2 3 4\nf 5 6 7 8\nf 1 2 6 5\nf 2 3 7 6\nf 3 4 8 7\nf 4 1 5 8\n");
        return path;
    }

    [Fact]
    public void Fokina_gets_a_wooden_floor_under_every_fixture_with_the_margin()
    {
        using var engine = TestEnvironment.CreateEngine("floor-fokina");
        Assert.True(engine.Open(TestEnvironment.Fokina).Success);
        Assert.False(StageFloor.HasFloor(engine.Scene, engine.Models.GetBounds));
        var content = SceneBounds.Compute(engine.Scene, engine.Models.GetBounds);

        var floor = StageFloor.CreateIfMissing(engine.Scene, engine.Models.GetBounds);
        Assert.NotNull(floor);
        Assert.Null(floor!.Parent);
        Assert.Empty(Floors(engine.Scene)); // created, not added
        Assert.Equal(StageFloor.DefaultName, floor.Name);
        var wall = floor.Get<WallComponent>()!;
        Assert.Equal(WallKind.Floor, wall.Kind);
        Assert.Equal(MaterialIds.WoodStage, wall.MaterialId);
        Assert.Equal(StageFloor.Thickness, wall.Thickness);
        var size = StageFloor.SizeOf(floor);
        Assert.Equal(content.Size.X + 2f * StageFloor.Margin, size.X, 3);
        Assert.Equal(content.Size.Y + 2f * StageFloor.Margin, size.Y, 3);
        var position = floor.Local.Position;
        Assert.Equal(content.Center.X, position.X, 3);
        Assert.Equal(content.Center.Y, position.Y, 3);
        Assert.Equal(MathF.Min(0f, content.Min.Z), position.Z, 3);

        // AddIfMissing adds it (the CLI's path); afterwards the scene has a floor and nothing more is added.
        var added = StageFloor.AddIfMissing(engine, "Stage floor (render)");
        Assert.NotNull(added);
        Assert.Equal("Stage floor (render)", added!.Name);
        Assert.Same(added, Assert.Single(Floors(engine.Scene)));
        Assert.True(StageFloor.HasFloor(engine.Scene));
        Assert.Null(StageFloor.CreateIfMissing(engine.Scene, engine.Models.GetBounds));
        Assert.Null(StageFloor.AddIfMissing(engine));
    }

    [Fact]
    public void A_floor_wall_hidden_or_not_counts_and_an_empty_scene_gets_none()
    {
        var empty = new Scene("empty");
        Assert.Null(StageFloor.CreateIfMissing(empty));
        Assert.False(StageFloor.HasFloor(empty));

        var scene = new Scene("rig");
        var spot = new Entity("Spot") { Local = new Transform(new Vector3(2f, 3f, 6f), Quaternion.Identity, Vector3.One) };
        spot.Add(new FixtureComponent());
        scene.Add(spot);
        var floor = StageFloor.CreateIfMissing(scene, name: "  ");
        Assert.NotNull(floor);
        Assert.Equal(StageFloor.DefaultName, floor!.Name); // a blank name falls back
        Assert.Equal(0f, floor.Local.Position.Z); // stage level: content above Z = 0 stands on it
        Assert.Equal(new Vector2(2f * (SceneBounds.DefaultHalfExtent + StageFloor.Margin)), StageFloor.SizeOf(floor));

        AddFloorWall(scene, hidden: true);
        Assert.True(StageFloor.HasFloor(scene));
        Assert.Null(StageFloor.CreateIfMissing(scene));

        // A wall that is not a floor does not count; SizeOf of anything else is zero.
        var other = new Scene("walls");
        var lamp = new Entity("Lamp") { Local = new Transform(new Vector3(0f, 0f, 3f), Quaternion.Identity, Vector3.One) };
        lamp.Add(new FixtureComponent());
        other.Add(lamp);
        var backdrop = new Entity("Backdrop");
        backdrop.Add(new WallComponent { Kind = WallKind.Wall, Width = 10f, Height = 4f });
        other.Add(backdrop);
        Assert.False(StageFloor.HasFloor(other));
        Assert.Equal(Vector2.Zero, StageFloor.SizeOf(backdrop));
    }

    [Fact]
    public void Content_below_the_stage_level_puts_the_floor_under_it_and_damaged_positions_are_ignored()
    {
        var scene = new Scene("pit");
        var low = new Entity("Pit light") { Local = new Transform(new Vector3(0f, 0f, -2f), Quaternion.Identity, Vector3.One) };
        low.Add(new FixtureComponent());
        scene.Add(low);
        var broken = new Entity("Broken") { Local = new Transform(new Vector3(float.NaN, 0f, 0f), Quaternion.Identity, Vector3.One) };
        broken.Add(new FixtureComponent());
        scene.Add(broken);
        var floor = StageFloor.CreateIfMissing(scene)!;
        Assert.Equal(-2f - SceneBounds.DefaultHalfExtent, floor.Local.Position.Z, 4);
        Assert.True(float.IsFinite(floor.Local.Position.X) && float.IsFinite(StageFloor.SizeOf(floor).X));
    }

    [Fact]
    public void A_venue_model_on_the_ground_is_the_floor_a_small_prop_is_not()
    {
        string dir = TestEnvironment.NewTempDirectory("floor-models");
        using var engine = TestEnvironment.CreateEngine("floor-models", isolateNativeImports: false);
        // A rig over a 10 × 8 m stage.
        foreach (var at in new[] { new Vector3(-5f, -4f, 6f), new Vector3(5f, -4f, 6f), new Vector3(-5f, 4f, 6f), new Vector3(5f, 4f, 6f) })
        {
            var spot = new Entity("Spot") { Local = new Transform(at, Quaternion.Identity, Vector3.One) };
            spot.Add(new FixtureComponent());
            engine.Scene.Add(spot);
        }

        // A 1 m prop on the ground: still floorless.
        var prop = engine.Import(WriteBox(dir, "prop", 1f, 1f, 1f));
        Assert.True(prop.Success, prop.Summary);
        Assert.False(StageFloor.HasFloor(engine.Scene, engine.Models.GetBounds));
        Assert.NotNull(StageFloor.CreateIfMissing(engine.Scene, engine.Models.GetBounds));

        // A 20 × 15 m venue slab under everything: the scene has its floor (a second one would z-fight with it).
        var venue = engine.Import(WriteBox(dir, "venue", 20f, 0.3f, 15f));
        Assert.True(venue.Success, venue.Summary);
        Assert.True(StageFloor.HasFloor(engine.Scene, engine.Models.GetBounds));
        Assert.Null(StageFloor.CreateIfMissing(engine.Scene, engine.Models.GetBounds));
        Assert.Null(StageFloor.AddIfMissing(engine));

        // Hidden, it is not the floor any more; without model bounds it is unknown and does not count either.
        var venueEntity = engine.Scene.Find(venue.Entities[0])!;
        venueEntity.Visible = false;
        Assert.False(StageFloor.HasFloor(engine.Scene, engine.Models.GetBounds));
        venueEntity.Visible = true;
        Assert.False(StageFloor.HasFloor(engine.Scene));
    }

    [Fact]
    public void The_scene_ambient_luminance_reaches_the_render_world_clamped()
    {
        using var engine = TestEnvironment.CreateEngine("ambient");
        var world = new RecordingRenderWorld();
        var sync = engine.AttachRenderWorld(world, loadFixtureModelsInBackground: false);
        engine.Scene.Settings.AmbientLuminance = 30f;
        sync.Update();
        Assert.Equal(30f, sync.AmbientLuminance);
        Assert.Equal(30f, world.Atmosphere!.AmbientLuminance);
        int updates = world.AtmosphereUpdates;

        // Unchanged: no new atmosphere is sent.
        sync.Update();
        Assert.Equal(updates, world.AtmosphereUpdates);

        foreach (var (value, expected) in new[] { (float.NaN, 0f), (-3f, 0f), (float.PositiveInfinity, 0f), (1e9f, RenderSync.MaxAmbientLuminance), (0.05f, 0.05f) })
        {
            engine.Scene.Settings.AmbientLuminance = value;
            sync.Update();
            Assert.Equal(expected, world.Atmosphere!.AmbientLuminance);
            Assert.Equal(expected, sync.AmbientLuminance);
        }
    }
}

/// <summary>Reference drawings and the stage floor in CLI renders, real-time and path-traced (Vulkan).</summary>
[Collection(VulkanCollection.Name)]
public sealed class ReferenceDrawingCliTests
{
    private static readonly (byte R, byte G, byte B) Red = (220, 40, 30), Green = (40, 190, 50);

    private static void ProbeVulkanOrSkip()
    {
        try
        {
            using var renderer = VulkanRenderer.Create(new VulkanContextOptions
            {
                CacheDirectory = Path.Combine(AppContext.BaseDirectory, "vk-cache"),
                MinimumLogLevel = VulkanLogLevel.Error,
            });
            Skip.IfNot(renderer.Info.SupportsRayTracing, "The Vulkan device has no ray queries (path tracer).");
        }
        catch (VulkanUnavailableException e)
        {
            Skip.If(true, $"No Vulkan device available: {e.Message}");
            throw;
        }
    }

    /// <summary>A project holding one reference drawing, 4 m wide, red left half and green right half, standing at the origin facing −Y.</summary>
    private static string WriteProject(string dir)
    {
        const int w = 32, h = 24;
        var pixels = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                var c = x < w / 2 ? Red : Green;
                int i = 4 * (y * w + x);
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (c.R, c.G, c.B, 255);
            }
        }

        string png = Path.Combine(dir, "plan.png");
        ImageFiles.WritePng(new ImageData { Width = w, Height = h, Pixels = pixels }, png);
        using var engine = TestEnvironment.CreateEngine("drawing-project");
        var imported = engine.Import(png, new EngineImportOptions { ImageMode = ImageMode.Reference, ImageWidth = 4f });
        Assert.True(imported.Success, imported.Summary);
        string project = Path.Combine(dir, "drawing.candela");
        Assert.True(engine.SaveAs(project).Success);
        return project;
    }

    /// <summary>Mean sRGB colour of a 5 × 5 block.</summary>
    private static Vector3 Block(ImageData image, int cx, int cy)
    {
        var sum = Vector3.Zero;
        for (int y = cy - 2; y <= cy + 2; y++)
        {
            for (int x = cx - 2; x <= cx + 2; x++)
            {
                int i = 4 * (y * image.Width + x);
                sum += new Vector3(image.Pixels[i], image.Pixels[i + 1], image.Pixels[i + 2]);
            }
        }

        return sum / 25f;
    }

    private static void AssertDominant(Vector3 rgb, int channel, string what)
    {
        float own = channel == 0 ? rgb.X : rgb.Y;
        float other1 = channel == 0 ? rgb.Y : rgb.X;
        Assert.True(own > 60f && own > 1.5f * other1 && own > 1.5f * rgb.Z, $"{what} shows as {rgb}");
    }

    [SkippableFact]
    public void A_reference_drawing_shows_its_image_in_a_cli_render_and_a_final_render()
    {
        ProbeVulkanOrSkip();
        string dir = TestEnvironment.NewTempDirectory("cli-drawing");
        string project = WriteProject(dir);
        // Camera 8 m in front of the drawing, 30° vertical field of view at 4:3: the drawing's halves (x = ±1 m) land at
        // pixels 52 and 108 of 160 on the middle row.
        string[] common =
        [
            "render", project, "--width", "160", "--height", "120", "--camera", "0,-8,0", "--target", "0,0,0", "--fov", "30",
            "--cache", Path.Combine(dir, "cache"), "--library", TestEnvironment.Root,
        ];
        // The real-time render with its default auto exposure (a drawing is display-referred whatever the exposure); the
        // final render at a fixed exposure (a drawing is display-referred against the exposure the render starts with).
        foreach (var (extra, name) in new[] { (Array.Empty<string>(), "real-time"), (new[] { "--final", "--samples", "16", "--ev", "8" }, "final") })
        {
            string png = Path.Combine(dir, name + ".png");
            var output = new StringWriter();
            var error = new StringWriter();
            int code = Cli.Program.Run([.. common, "--out", png, .. extra], output, error);
            Assert.True(code == Cli.Program.ExitOk, $"{name}: exit {code}\n{output}\n{error}");
            // The drawing is content without a floor: the engine's stage floor goes under it.
            Assert.Contains("Floor:    added a ", output.ToString(), StringComparison.Ordinal);
            Assert.True(ImageImporter.TryDecode(File.ReadAllBytes(png), out var image, out var problem), problem);
            AssertDominant(Block(image!, 52, 60), 0, $"{name}: the drawing's left half (red)");
            AssertDominant(Block(image!, 108, 60), 1, $"{name}: the drawing's right half (green)");
        }
    }
}
