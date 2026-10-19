using System.Numerics;
using Candela.Core.SceneGraph.Components;
using Candela.Rendering;

namespace Candela.Engine.Tests;

/// <summary>
/// Review of the engine follow-ups: the work light is deterministic (the same scene gets the same rig, whatever the
/// heads did before), and the other findings of the review.
/// </summary>
public sealed class FollowUpRevisionTests
{
    private const double Frame = 1.0 / 30.0;

    /// <summary>The work-light rig of a scene with one moving head tilted to <paramref name="tilt"/>, reached by <paramref name="path"/>.</summary>
    private static SpotLight[] RigAfter(float tilt, bool settleFirst, string name)
    {
        using var engine = TestEnvironment.CreateEngine(name);
        var head = SceneBuilder.Fixture(engine.Scene, SceneBuilder.MacViper, "Basic", new Vector3(0, 0, 6));
        var world = new RecordingRenderWorld();
        var sync = engine.AttachRenderWorld(world, loadFixtureModelsInBackground: false);
        sync.WorkLight = WorkLightMode.On;
        engine.Tick(0);
        var fixture = head.Get<FixtureComponent>()!;
        fixture.ManualAttributes["Tilt"] = tilt;
        fixture.NotifyChanged();
        // the motors take their time: the head is still travelling when the edit is drawn
        for (int i = 0; i < (settleFirst ? 150 : 1); i++) engine.Tick(Frame);
        if (!settleFirst)
            for (int i = 0; i < 150; i++) engine.Tick(Frame);
        Assert.True(sync.IsWorkLightOn);
        return world.Lights.ToArray();
    }

    [Fact]
    public void The_work_light_rig_does_not_depend_on_where_the_heads_pointed_when_it_was_placed()
    {
        // the same scene: one engine drew the edit while the head was still moving, the other renamed the head after it
        // head arrived (an unrelated edit that makes the binding place the rig again)
        var travelling = RigAfter(110f, settleFirst: false, "work-light-travelling");
        SpotLight[] arrived;
        {
            using var engine = TestEnvironment.CreateEngine("work-light-arrived");
            var head = SceneBuilder.Fixture(engine.Scene, SceneBuilder.MacViper, "Basic", new Vector3(0, 0, 6));
            var world = new RecordingRenderWorld();
            var sync = engine.AttachRenderWorld(world, loadFixtureModelsInBackground: false);
            sync.WorkLight = WorkLightMode.On;
            engine.Tick(0);
            var fixture = head.Get<FixtureComponent>()!;
            fixture.ManualAttributes["Tilt"] = 110f;
            fixture.NotifyChanged();
            for (int i = 0; i < 150; i++) engine.Tick(Frame);
            head.Name = "Spot 1";
            engine.Tick(Frame);
            arrived = world.Lights.ToArray();
        }
        Assert.Equal(WorkLightRig.LightCount, travelling.Length);
        Assert.Equal(travelling, arrived);

        // and a head pointing elsewhere does not move the rig either
        Assert.Equal(travelling, RigAfter(-60f, settleFirst: true, "work-light-other-tilt"));
    }
}

/// <summary>The CLI's work light follows the overlay of the picture it renders, not the one saved with the project.</summary>
[Collection(VulkanCollection.Name)]
public sealed class CliWorkLightTests
{
    [SkippableFact]
    public void The_cli_work_light_follows_the_rendered_overlay_not_the_projects_saved_one()
    {
        try
        {
            using var probe = Rendering.Vulkan.VulkanRenderer.Create(new Rendering.Vulkan.VulkanContextOptions
            {
                CacheDirectory = Path.Combine(AppContext.BaseDirectory, "vk-cache"),
                MinimumLogLevel = Rendering.Vulkan.VulkanLogLevel.Error,
            });
        }
        catch (Rendering.Vulkan.VulkanUnavailableException e)
        {
            Skip.If(true, $"No Vulkan device available: {e.Message}");
        }

        string dir = TestEnvironment.NewTempDirectory("cli-work-light");
        string project = Path.Combine(dir, "dark.candela");
        using (var engine = TestEnvironment.CreateEngine("cli-work-light-project"))
        {
            SceneBuilder.Floor(engine.Scene, 6f);
            // the project was saved while its view showed the lux overlay
            engine.View.View.Overlay = OverlayMode.Lux;
            Assert.True(engine.SaveAs(project).Success);
        }

        string plain = Path.Combine(dir, "plain.candela");
        using (var engine = TestEnvironment.CreateEngine("cli-work-light-plain"))
        {
            SceneBuilder.Floor(engine.Scene, 6f);
            Assert.True(engine.SaveAs(plain).Success);
        }

        string Render(params string[] extra) => RenderFile(project, extra);

        string RenderFile(string file, params string[] extra)
        {
            var output = new StringWriter();
            var error = new StringWriter();
            string[] args = ["render", file, "--out", Path.Combine(dir, $"r{extra.Length}.png"), "--width", "64", "--height", "36",
                "--look", "show", "--ev", "0", "--cache", Path.Combine(dir, "cache"), "--library", TestEnvironment.Root, .. extra];
            int code = Cli.Program.Run(args, output, error);
            Assert.True(code == Cli.Program.ExitOk, $"exit {code}\n{output}\n{error}");
            return output.ToString();
        }

        // a plain picture of the project: asked for, the work light is there although the saved view showed lux
        Assert.Contains("work light", Render("--work-light", "on"), StringComparison.Ordinal);
        // a lux picture measures stage light only
        Assert.DoesNotContain("work light", Render("--work-light", "on", "--overlay", "lux"), StringComparison.Ordinal);
        Assert.DoesNotContain("work light", Render(), StringComparison.Ordinal);
        Assert.DoesNotContain("work light", RenderFile(plain, "--work-light", "on", "--overlay", "lux"), StringComparison.Ordinal);
        Assert.Contains("work light", RenderFile(plain, "--work-light", "auto"), StringComparison.Ordinal);
    }
}
