using Candela.Engine;
using Candela.Rendering.Vulkan;

namespace Candela.Cli;

/// <summary>
/// Headless entry point (<c>candela</c>). Exit codes: 0 success; 1 failure (file not readable, render or I/O error);
/// 2 usage error; 3 no usable Vulkan device; 4 the input has errors (<c>validate</c> found error-level issues,
/// <c>import-worker</c> rejected the model).
/// </summary>
internal static class Program
{
    internal const int ExitOk = 0;
    internal const int ExitFailure = 1;
    internal const int ExitUsage = 2;
    internal const int ExitNoVulkan = 3;
    internal const int ExitInputErrors = 4;

    private static int Main(string[] args) => Run(args, Console.Out, Console.Error);

    /// <summary>Runs a command line (also used by tests in-process).</summary>
    internal static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage(output);
            return args.Length == 0 ? ExitUsage : ExitOk;
        }

        try
        {
            switch (args[0])
            {
                case "render":
                    return RenderCommand.Run(RenderOptions.Parse(args[1..]), output, error);
                case "render-test":
                    return RenderTestCommand.Run(RenderTestOptions.Parse(args[1..]), output);
                case "artnet-test":
                    return ArtNetTestCommand.Run(ArtNetTestOptions.Parse(args[1..]), output, error);
                case "info":
                    return InfoCommand.Run(InfoOptions.Parse(args[1..]), output, error);
                case "validate":
                    return ValidateCommand.Run(ValidateOptions.Parse(args[1..]), output, error);
                case ImportWorker.CommandName:
                    return ImportWorkerCommand.Run(args[1..], error);
                default:
                    error.WriteLine($"Unknown command '{args[0]}'.");
                    PrintUsage(error);
                    return ExitUsage;
            }
        }
        catch (UsageException e)
        {
            error.WriteLine(e.Message);
            PrintUsage(error);
            return ExitUsage;
        }
        catch (VulkanUnavailableException e)
        {
            error.WriteLine($"No usable Vulkan device: {e.Message}");
            error.WriteLine("Install a Vulkan 1.3 driver (GPU vendor driver, or Mesa lavapipe for software rendering).");
            return ExitNoVulkan;
        }
        catch (Exception e) when (e is VulkanException or ShaderCompilationException or IOException or UnauthorizedAccessException or DllNotFoundException)
        {
            error.WriteLine($"{args[0]} failed: {e.Message}");
            return ExitFailure;
        }
    }

    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Candela command-line tool");
        writer.WriteLine();
        writer.WriteLine("Usage:");
        writer.WriteLine("  candela render <scene.mvr|scene.candela> --out <file.png> [--width W] [--height H]");
        writer.WriteLine("                 [--camera x,y,z --target x,y,z | --view front|top|side|auto] [--fov <deg>]");
        writer.WriteLine("                 [--mode full|colorless|wireframe] [--overlay none|lux|noise|cables] [--haze <1/m>]");
        writer.WriteLine("                 [--look open-white|demo|show|dmx-file <recording.cdmx>] [--time <s>] [--ev auto|floor|<EV100>]");
        writer.WriteLine("                 [--auto-exposure] [--frames N] [--grid] [--floor auto|none] [--work-light off|auto|on]");
        writer.WriteLine("                 [--device <name>] [--library <dir>]... [--cache <dir>]");
        writer.WriteLine("      Loads the scene through the engine, applies the look, simulates --time seconds (DMX, motors, smoke,");
        writer.WriteLine("      fire), renders it and writes the PNG. Prints issues and timings. Defaults: 1280 x 720, auto view,");
        writer.WriteLine("      full shading, no overlay, the scene's haze, look open-white, time 0, auto exposure (--ev auto: the renderer adapts");
        writer.WriteLine("      to the picture, starting from an incident-light reading of the floor; at least 12 frames). --ev floor fixes the");
        writer.WriteLine("      exposure at that floor reading, --ev <EV100> at a value; --auto-exposure adapts from the --ev value.");
        writer.WriteLine("      Looks: open-white = every light at full in white, heads aimed at the stage; demo = a fixed show-like state for");
        writer.WriteLine("      judging the picture (moving heads in symmetric fans and crossings over the stage and the audience, saturated");
        writer.WriteLine("      colours and white, narrow beams, gobos on some spots, washes colouring the floor, hazers running);");
        writer.WriteLine("      show = the manual values saved with the scene; dmx-file = a DMX recording played from its start.");
        writer.WriteLine("      --work-light auto adds a dim neutral key and ambient light when no fixture emits, on always; a render");
        writer.WriteLine("      never gets it unless asked (default off).");
        writer.WriteLine();
        writer.WriteLine("  candela artnet-test --to <ip[:port]> [--universes 1-4] [--seconds N] [--fps F] [--offset O]");
        writer.WriteLine("                      [--scene <file>] [--library <dir>]...");
        writer.WriteLine("      Sends the Art-Net test show (pan/tilt sweep, colour chase, dimmer wave, zoom) with ArtSync and");
        writer.WriteLine("      ArtTimeCode, to check Art-Net reception without a console. Default rig: 8 moving heads (12 ch) and");
        writer.WriteLine("      8 RGBW pars per universe; with --scene the fixtures patched in that scene (their GDTF modes).");
        writer.WriteLine("      Defaults: universes 1-4, 30 s, 40 fps, offset 1 (port-address = universe - offset).");
        writer.WriteLine();
        writer.WriteLine("  candela info <file.gdtf|file.mvr|file.candela> [--library <dir>]...");
        writer.WriteLine("      Prints a summary: fixture type, modes and physical data; or the scene's layers, fixtures and GDTFs.");
        writer.WriteLine();
        writer.WriteLine("  candela validate <scene.mvr|scene.candela> [--library <dir>]... [--plot]");
        writer.WriteLine("      Prints every issue of the scene (patch, power, data, rigging, assets); --plot adds the light plot.");
        writer.WriteLine();
        writer.WriteLine("  candela render-test --out <dir> [options]   (renderer self-test with a procedural scene; see --help of the");
        writer.WriteLine("                                               renderer documentation)");
        writer.WriteLine();
        writer.WriteLine("Exit codes: 0 ok, 1 failure, 2 usage error, 3 no Vulkan device, 4 the input has errors.");
    }
}

/// <summary>A command-line argument error (exit code 2).</summary>
internal sealed class UsageException : Exception
{
    public UsageException(string message)
        : base(message)
    {
    }
}
