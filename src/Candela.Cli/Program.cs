using Candela.Rendering.Vulkan;

namespace Candela.Cli;

/// <summary>
/// Headless entry point. Exit codes: 0 success, 1 failure, 2 usage error, 3 no usable Vulkan device.
/// </summary>
internal static class Program
{
    internal const int ExitOk = 0;
    internal const int ExitFailure = 1;
    internal const int ExitUsage = 2;
    internal const int ExitNoVulkan = 3;

    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage(Console.Out);
            return args.Length == 0 ? ExitUsage : ExitOk;
        }

        try
        {
            switch (args[0])
            {
                case "render-test":
                    return RenderTestCommand.Run(RenderTestOptions.Parse(args[1..]), Console.Out);
                default:
                    Console.Error.WriteLine($"Unknown command '{args[0]}'.");
                    PrintUsage(Console.Error);
                    return ExitUsage;
            }
        }
        catch (UsageException e)
        {
            Console.Error.WriteLine(e.Message);
            PrintUsage(Console.Error);
            return ExitUsage;
        }
        catch (VulkanUnavailableException e)
        {
            Console.Error.WriteLine($"No usable Vulkan device: {e.Message}");
            Console.Error.WriteLine("Install a Vulkan 1.3 driver (GPU vendor driver, or Mesa lavapipe for software rendering).");
            return ExitNoVulkan;
        }
        catch (Exception e) when (e is VulkanException or ShaderCompilationException or IOException or UnauthorizedAccessException or DllNotFoundException)
        {
            Console.Error.WriteLine($"render-test failed: {e.Message}");
            return ExitFailure;
        }
    }

    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Candela command-line tool");
        writer.WriteLine();
        writer.WriteLine("Usage:");
        writer.WriteLine("  candela render-test --out <dir> [--width W] [--height H] [--frames N] [--tonemapper agx|aces|neutral]");
        writer.WriteLine("                      [--device <name>] [--validation] [--haze <1/m>] [--no-shadows] [--shadow-resolution N]");
        writer.WriteLine("                      [--indirect off|rsm|raytraced] [--no-bloom] [--bloom <k>] [--auto-exposure]");
        writer.WriteLine("                      [--exposure-compensation <stops>] [--scale <0.5-1>] [--volumetric-slices N] [--overlays]");
        writer.WriteLine();
        writer.WriteLine("  Renders the built-in procedural test scene (stage floor, wall, boxes, spheres, three coloured shadow-casting");
        writer.WriteLine("  spot lights, an emissive panel) and writes full.png, colorless.png and wireframe.png to <dir> (with");
        writer.WriteLine("  --overlays also lux.png, noise.png and cables.png). Prints the renderer information, frame timings and");
        writer.WriteLine("  shadow / volumetric / indirect statistics. Defaults: 1280 x 720, 5 timed frames per view, AgX, shadows");
        writer.WriteLine("  1024, reflective shadow maps, bloom 0.04, camera exposure, no haze.");
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
