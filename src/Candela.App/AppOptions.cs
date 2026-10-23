using System.Globalization;

namespace Candela.App;

/// <summary>A command-line error (the editor prints the usage and exits with <see cref="ExitCodes.Usage"/>).</summary>
internal sealed class AppUsageException(string message) : Exception(message);

/// <summary>Process exit codes of <c>candela-app</c>.</summary>
internal static class ExitCodes
{
    /// <summary>Normal exit (or the screenshot was written).</summary>
    public const int Ok = 0;

    /// <summary>A fatal error (window, GPU or screenshot failure).</summary>
    public const int Failure = 1;

    /// <summary>Bad command line.</summary>
    public const int Usage = 2;

    /// <summary>No usable Vulkan 1.3 device.</summary>
    public const int NoVulkan = 3;
}

/// <summary>
/// Command-line options of the editor. The headless verification path is
/// <c>candela-app --screenshot out.png [--frames N] [--layout name] [--open scene] [--script actions.json]</c>: the real UI
/// runs for <see cref="Frames"/> frames (after replaying the script), the last swapchain image is copied before
/// presentation and written as PNG, and the program exits.
/// </summary>
internal sealed record AppOptions
{
    /// <summary>Frames rendered in screenshot mode when <c>--frames</c> is not given.</summary>
    public const int DefaultScreenshotFrames = 12;

    /// <summary>Scene or project to open at start-up (<c>--open</c> or the first free argument).</summary>
    public string? OpenPath { get; init; }

    /// <summary>PNG to write in screenshot mode; null = interactive.</summary>
    public string? ScreenshotPath { get; init; }

    /// <summary>Frames to render before the screenshot (at least 1; scripts extend it to their length).</summary>
    public int Frames { get; init; } = DefaultScreenshotFrames;

    /// <summary>Named layout to load at start-up (from the user's layout folder, or a built-in preset).</summary>
    public string? Layout { get; init; }

    /// <summary>JSON file with scripted input actions to replay (see <c>Input.InputScript</c>).</summary>
    public string? ScriptPath { get; init; }

    /// <summary>Initial window width in screen coordinates.</summary>
    public int Width { get; init; } = 1600;

    /// <summary>Initial window height in screen coordinates.</summary>
    public int Height { get; init; } = 900;

    /// <summary>UI scale override (1 = 100 %); null = the user setting.</summary>
    public float? UiScale { get; init; }

    /// <summary>UI language override ("en", "ru"); null = the user setting.</summary>
    public string? Language { get; init; }

    /// <summary>Preferred GPU name substring; null = the user setting.</summary>
    public string? Device { get; init; }

    /// <summary>Enables the Vulkan validation layer for this run.</summary>
    public bool Validation { get; init; }

    /// <summary>Folder for settings, layouts and presets of this run (sets <c>CANDELA_CONFIG_DIR</c>).</summary>
    public string? ConfigDirectory { get; init; }

    /// <summary>Starts with the default layout, ignoring the saved workspace.</summary>
    public bool ResetLayout { get; init; }

    /// <summary>Keeps every panel inside the main window (no separate OS windows for torn-off panels).</summary>
    public bool SingleWindow { get; init; }

    /// <summary>Print the usage and exit.</summary>
    public bool Help { get; init; }

    /// <summary>
    /// Diagnostics: the viewports show the renderer's procedural test scene instead of the engine's scene
    /// (<c>--preview-stage</c>).
    /// </summary>
    public bool PreviewStage { get; init; }

    /// <summary>
    /// Opens the fixture library setup window at start-up even when a library is set, during development and in headless
    /// runs (<c>--library-setup</c>; screenshots and tests of the first-run experience).
    /// </summary>
    public bool LibrarySetup { get; init; }

    /// <summary>True when the run ends after writing a screenshot.</summary>
    public bool IsScreenshotMode => ScreenshotPath is not null;

    /// <summary>Parses the command line.</summary>
    /// <exception cref="AppUsageException">Unknown option, missing or invalid value.</exception>
    public static AppOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var o = new AppOptions();
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "-h" or "--help":
                    o = o with { Help = true };
                    break;
                case "--screenshot":
                    o = o with { ScreenshotPath = Value(args, ref i, a) };
                    break;
                case "--frames":
                    o = o with { Frames = Int(args, ref i, a, 1, 100_000) };
                    break;
                case "--layout":
                    o = o with { Layout = Value(args, ref i, a) };
                    break;
                case "--open":
                    o = o with { OpenPath = Value(args, ref i, a) };
                    break;
                case "--script":
                    o = o with { ScriptPath = Value(args, ref i, a) };
                    break;
                case "--width":
                    o = o with { Width = Int(args, ref i, a, 320, 16384) };
                    break;
                case "--height":
                    o = o with { Height = Int(args, ref i, a, 240, 16384) };
                    break;
                case "--scale":
                    o = o with { UiScale = Float(args, ref i, a, Ui.UiScale.Min, Ui.UiScale.Max) };
                    break;
                case "--language":
                    {
                        string lang = Value(args, ref i, a).ToLowerInvariant();
                        if (lang is not ("en" or "ru"))
                            throw new AppUsageException($"--language must be 'en' or 'ru' (got '{lang}').");
                        o = o with { Language = lang };
                        break;
                    }

                case "--device":
                    o = o with { Device = Value(args, ref i, a) };
                    break;
                case "--validation":
                    o = o with { Validation = true };
                    break;
                case "--config-dir":
                    o = o with { ConfigDirectory = Value(args, ref i, a) };
                    break;
                case "--reset-layout":
                    o = o with { ResetLayout = true };
                    break;
                case "--single-window":
                    o = o with { SingleWindow = true };
                    break;
                case "--preview-stage":
                    o = o with { PreviewStage = true };
                    break;
                case "--library-setup":
                    o = o with { LibrarySetup = true };
                    break;
                default:
                    if (a.StartsWith('-'))
                        throw new AppUsageException($"Unknown option '{a}'.");
                    if (o.OpenPath is not null)
                        throw new AppUsageException($"Only one scene can be opened (got '{o.OpenPath}' and '{a}').");
                    o = o with { OpenPath = a };
                    break;
            }
        }

        return o;
    }

    /// <summary>Usage text printed by <c>--help</c> and on errors.</summary>
    public static string Usage =>
        """
        Candela editor

        Usage:
          candela-app [scene] [options]

        Options:
          --open <file>          scene or project to open (.candela, .mvr)
          --layout <name>        start with a named layout (saved layouts or: default, viewports-4)
          --reset-layout         ignore the saved workspace and start with the default layout
          --scale <1-3>          UI scale (1 = 100 %), overrides the setting
          --language <en|ru>     UI language, overrides the setting
          --device <name>        preferred GPU (name substring)
          --validation           enable the Vulkan validation layer
          --config-dir <dir>     folder for settings, layouts and presets of this run
          --single-window        keep torn-off panels inside the main window
          --preview-stage        diagnostics: show the renderer's test scene instead of the stage
          --library-setup        open the fixture library setup window at start-up
          --width <px>           initial window width  (default 1600)
          --height <px>          initial window height (default 900)

        Headless verification (e.g. under Xvfb):
          --screenshot <png>     render the UI, save the last frame as PNG and exit
          --frames <n>           frames to render before the screenshot (default 12)
          --script <json>        replay scripted input (mouse moves/clicks/drags, keys, menu items) first

        Settings live in %APPDATA%\Candela (~/.config/Candela), logs and crash reports in
        %LOCALAPPDATA%\Candela\logs (~/.config/Candela/logs).

        Exit codes: 0 ok, 1 failure, 2 usage, 3 no usable Vulkan device.
        """;

    private static string Value(IReadOnlyList<string> args, ref int i, string name)
    {
        if (i + 1 >= args.Count || args[i + 1].Length == 0)
            throw new AppUsageException($"{name} needs a value.");
        return args[++i];
    }

    private static int Int(IReadOnlyList<string> args, ref int i, string name, int min, int max)
    {
        string v = Value(args, ref i, name);
        if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < min || n > max)
            throw new AppUsageException(string.Create(CultureInfo.InvariantCulture, $"{name} must be a whole number from {min} to {max} (got '{v}')."));
        return n;
    }

    private static float Float(IReadOnlyList<string> args, ref int i, string name, float min, float max)
    {
        string v = Value(args, ref i, name);
        if (!float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) || !float.IsFinite(f) || f < min || f > max)
            throw new AppUsageException(string.Create(CultureInfo.InvariantCulture, $"{name} must be a number from {min} to {max} (got '{v}')."));
        return f;
    }
}
