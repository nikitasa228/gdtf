using Candela.App.Input;

namespace Candela.App.Tests;

public sealed class AppOptionsTests
{
    [Fact]
    public void Defaults()
    {
        var o = AppOptions.Parse(Array.Empty<string>());
        Assert.False(o.IsScreenshotMode);
        Assert.Equal(AppOptions.DefaultScreenshotFrames, o.Frames);
        Assert.Equal((1600, 900), (o.Width, o.Height));
        Assert.Null(o.OpenPath);
        Assert.Null(o.UiScale);
    }

    [Fact]
    public void Headless_screenshot_options()
    {
        var o = AppOptions.Parse(new[]
        {
            "--screenshot", "out.png", "--frames", "40", "--layout", "viewports-4", "--open", "stages/Фокина.mvr",
            "--script", "actions.json", "--width", "1280", "--height", "720", "--scale", "1.5", "--language", "RU",
            "--device", "llvmpipe", "--validation", "--config-dir", "/tmp/cfg", "--reset-layout", "--single-window",
        });
        Assert.True(o.IsScreenshotMode);
        Assert.Equal("out.png", o.ScreenshotPath);
        Assert.Equal(40, o.Frames);
        Assert.Equal("viewports-4", o.Layout);
        Assert.Equal("stages/Фокина.mvr", o.OpenPath);
        Assert.Equal("actions.json", o.ScriptPath);
        Assert.Equal((1280, 720), (o.Width, o.Height));
        Assert.Equal(1.5f, o.UiScale);
        Assert.Equal("ru", o.Language);
        Assert.Equal("llvmpipe", o.Device);
        Assert.True(o.Validation && o.ResetLayout && o.SingleWindow);
        Assert.Equal("/tmp/cfg", o.ConfigDirectory);
    }

    [Fact]
    public void A_positional_argument_is_the_scene_to_open()
    {
        Assert.Equal("show.candela", AppOptions.Parse(new[] { "show.candela" }).OpenPath);
        Assert.True(AppOptions.Parse(new[] { "-h" }).Help);
    }

    [Theory]
    [InlineData("--frames")]
    [InlineData("--frames", "0")]
    [InlineData("--frames", "many")]
    [InlineData("--scale", "9")]
    [InlineData("--scale", "x")]
    [InlineData("--width", "10")]
    [InlineData("--language", "de")]
    [InlineData("--screenshot", "")]
    [InlineData("--bogus")]
    [InlineData("a.mvr", "b.mvr")]
    public void Invalid_command_lines_are_usage_errors(params string[] args) =>
        Assert.Throws<AppUsageException>(() => AppOptions.Parse(args));

    [Fact]
    public void Usage_mentions_every_option()
    {
        foreach (var option in new[] { "--screenshot", "--frames", "--layout", "--open", "--script", "--scale", "--language", "--config-dir", "--single-window" })
            Assert.Contains(option, AppOptions.Usage);
    }
}

public sealed class InputScriptTests
{
    [Fact]
    public void Parses_every_action_type()
    {
        var script = InputScript.Parse("""
            {
              // comments and trailing commas are fine
              "actions": [
                { "type": "wait", "frames": 3 },
                { "type": "move", "x": 10, "y": 20 },
                { "type": "click", "x": 700, "y": 400, "button": "right", "modifiers": "ctrl+shift", "double": true },
                { "type": "drag", "x": 1, "y": 2, "toX": 30, "toY": 40, "steps": 6 },
                { "type": "scroll", "x": 5, "y": 5, "delta": -2.5 },
                { "type": "key", "chord": "Ctrl+Z" },
                { "type": "text", "text": "Фокина" },
                { "type": "menu", "path": "File/Import/MVR Scene" },
                { "type": "action", "id": "view.frameSelection" },
                { "type": "panel", "panel": "Issues", "name": "Issues", "open": false },
                { "type": "undock", "panel": "Console", "x": 100, "y": 50, "width": 500, "height": 300 },
                { "type": "tabStyle", "panel": "Properties", "style": "LeftIcons" },
                { "type": "viewport", "viewport": "Viewport2", "view": "Top", "shading": "Wireframe", "grid": false },
                { "type": "select", "names": ["Gold sphere", "Steel column"] },
                { "type": "layout", "name": "viewports-4" },
                { "type": "setting", "name": "uiScale", "value": "1.5" },
                { "type": "screenshot", "path": "shot.png" },
              ]
            }
            """);
        Assert.Equal(17, script.Actions.Count);
        var click = script.Actions[2];
        Assert.Equal(ScriptActionType.Click, click.Type);
        Assert.Equal((700f, 400f, 1), (click.X, click.Y, click.Button));
        Assert.Equal("ctrl+shift", click.Modifiers);
        Assert.True(click.Double);
        Assert.Equal(6, script.Actions[3].Frames);
        Assert.Equal(-2.5f, script.Actions[4].Delta);
        Assert.Equal("Ctrl+Z", script.Actions[5].Text);
        Assert.Equal("File/Import/MVR Scene", script.Actions[7].Text);
        Assert.False(script.Actions[9].Flag);
        Assert.Equal(("Viewport2", "Top", "Wireframe", false), (script.Actions[12].ViewportId, script.Actions[12].View, script.Actions[12].Shading, script.Actions[12].Flag));
        Assert.Equal(new[] { "Gold sphere", "Steel column" }, script.Actions[13].Names);
        Assert.Equal(("uiScale", "1.5"), (script.Actions[15].Text, script.Actions[15].Value));
    }

    [Fact]
    public void A_bare_array_is_accepted()
    {
        var script = InputScript.Parse("""[ { "type": "Wait" } ]""");
        Assert.Equal(ScriptActionType.Wait, Assert.Single(script.Actions).Type);
        Assert.Equal(1, script.Actions[0].Frames);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "steps": [] }""")]
    [InlineData("""{ "actions": {} }""")]
    [InlineData("""[ { "x": 1 } ]""")]
    [InlineData("""[ { "type": "teleport" } ]""")]
    [InlineData("""[ { "type": "key" } ]""")]
    [InlineData("""[ { "type": "key", "chord": "Ctrl+" } ]""")]
    [InlineData("""[ { "type": "menu" } ]""")]
    [InlineData("""[ { "type": "undock" } ]""")]
    [InlineData("""[ { "type": "tabStyle", "panel": "Issues" } ]""")]
    [InlineData("""[ { "type": "select", "names": [] } ]""")]
    [InlineData("""[ { "type": "click", "button": "fourth" } ]""")]
    [InlineData("""[ 5 ]""")]
    public void Invalid_scripts_are_rejected_with_a_format_error(string json) =>
        Assert.Throws<FormatException>(() => InputScript.Parse(json));

    [Fact]
    public void The_error_names_the_action()
    {
        var e = Assert.Throws<FormatException>(() => InputScript.Parse("""[ { "type": "wait" }, { "type": "fly" } ]"""));
        Assert.StartsWith("Action 1:", e.Message);
    }

    [Fact]
    public void Player_schedules_frames_and_screenshots()
    {
        var script = InputScript.Parse("""
            [
              { "type": "wait", "frames": 5 },
              { "type": "click", "x": 10, "y": 10 },
              { "type": "screenshot", "path": "a.png" },
              { "type": "key", "chord": "Delete" },
              { "type": "screenshot", "path": "b.png" }
            ]
            """);
        var player = new ScriptPlayer(script, TestLog.Create());
        // Frame 0 lays the UI out; wait 5 → 6; click takes 4 frames → 10; screenshot at 10; key 4 frames → 15.
        Assert.True(player.WantsScreenshot(10));
        Assert.Equal("a.png", player.ScreenshotPath(10));
        Assert.True(player.WantsScreenshot(15));
        Assert.Equal("b.png", player.ScreenshotPath(15));
        Assert.False(player.WantsScreenshot(11));
        Assert.Null(player.ScreenshotPath(3));
        Assert.True(player.FrameCount > 15);
    }

    [Fact]
    public void Unknown_keys_and_modifiers_fail_when_the_script_is_scheduled()
    {
        Assert.Throws<FormatException>(() => new ScriptPlayer(InputScript.Parse("""[ { "type": "key", "chord": "Ctrl+NoSuchKey" } ]"""), TestLog.Create()));
        Assert.Throws<FormatException>(() => new ScriptPlayer(InputScript.Parse("""[ { "type": "click", "modifiers": "meta" } ]"""), TestLog.Create()));
    }

    [Fact]
    public void Load_reads_a_file()
    {
        using var dir = new TempDir();
        string path = dir.Write("s.json", """{ "actions": [ { "type": "layout", "name": "default" } ] }""");
        Assert.Equal(ScriptActionType.Layout, Assert.Single(InputScript.Load(path).Actions).Type);
    }
}
