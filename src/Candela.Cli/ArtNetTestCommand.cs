using System.Diagnostics;
using System.Net;
using Candela.Engine;
using Candela.Protocols.ArtNet;

namespace Candela.Cli;

/// <summary>Options of <c>candela artnet-test</c>.</summary>
internal sealed class ArtNetTestOptions
{
    public IPEndPoint Target { get; private set; } = new(IPAddress.Loopback, 6454);
    public IReadOnlyList<int> Universes { get; private set; } = new[] { 1, 2, 3, 4 };
    public double Seconds { get; private set; } = 30;
    public double FrameRate { get; private set; } = 40;
    public int Offset { get; private set; } = 1;
    public string? Scene { get; private set; }
    public List<string> Libraries { get; } = new();

    public static ArtNetTestOptions Parse(string[] args)
    {
        var o = new ArtNetTestOptions();
        bool hasTarget = false;
        for (int i = 0; i < args.Length; i++)
        {
            string name = args[i];
            switch (name)
            {
                case "--to":
                    o.Target = ParseEndPoint(CliArgs.Value(args, ref i, name));
                    hasTarget = true;
                    break;
                case "--universes":
                    o.Universes = CliArgs.Universes(name, CliArgs.Value(args, ref i, name));
                    break;
                case "--seconds":
                    o.Seconds = CliArgs.Number(name, CliArgs.Value(args, ref i, name), 0.1f, 86400f);
                    break;
                case "--fps":
                    o.FrameRate = CliArgs.Number(name, CliArgs.Value(args, ref i, name), 1f, 44f);
                    break;
                case "--offset":
                    o.Offset = CliArgs.Integer(name, CliArgs.Value(args, ref i, name), 0, 1);
                    break;
                case "--scene":
                    o.Scene = CliArgs.Value(args, ref i, name);
                    break;
                case "--library":
                    o.Libraries.Add(CliArgs.Value(args, ref i, name));
                    break;
                default:
                    throw new UsageException($"Unknown option '{name}'.");
            }
        }
        if (!hasTarget) throw new UsageException("artnet-test needs --to <ip> (a node's IP, a directed broadcast such as 2.255.255.255, or 127.0.0.1).");
        return o;
    }

    private static IPEndPoint ParseEndPoint(string text)
    {
        if (IPEndPoint.TryParse(text, out var ep) && ep.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            return ep.Port == 0 ? new IPEndPoint(ep.Address, 6454) : ep;
        throw new UsageException($"--to must be an IPv4 address with an optional port (got '{text}').");
    }
}

/// <summary>
/// <c>candela artnet-test</c>: plays the Art-Net test show (<see cref="ArtNetTestConsole"/>) to a node or to Candela
/// itself, so Art-Net reception can be checked without a console.
/// </summary>
internal static class ArtNetTestCommand
{
    public static int Run(ArtNetTestOptions options, TextWriter output, TextWriter error)
    {
        ArtNetTestPattern pattern;
        if (options.Scene is { } scene)
        {
            using var engine = new StageEngine(CliArgs.EngineOptions(options.Libraries, null));
            var opened = engine.Open(scene);
            if (!opened.Success)
            {
                error.WriteLine(opened.Summary);
                return Program.ExitFailure;
            }
            pattern = GdtfTestPatternEncoder.ForScene(engine);
            if (pattern.FixtureCount == 0)
            {
                error.WriteLine($"'{scene}' has no patched fixture with a known GDTF.");
                return Program.ExitFailure;
            }
        }
        else
        {
            var fixtures = new List<TestPatternFixture>();
            foreach (int universe in options.Universes)
            {
                int address = 1;
                for (int i = 0; i < 8; i++, address += 12) fixtures.Add(TestPatternFixture.MovingHead(universe, address));
                for (int i = 0; i < 8; i++, address += 5) fixtures.Add(TestPatternFixture.RgbwPar(universe, address));
            }
            pattern = ArtNetTestPattern.ForFixtures(fixtures);
        }

        using var console = new ArtNetTestConsole(pattern, options.Target, options.Offset) { FrameRate = options.FrameRate };
        output.WriteLine($"Sending the Art-Net test show to {options.Target}: {pattern.FixtureCount} fixtures in universe(s) " +
                         $"{string.Join(", ", pattern.Universes)} (port-address = universe - {options.Offset}), {CliArgs.F(options.FrameRate)} fps, " +
                         $"ArtSync and ArtTimeCode, for {CliArgs.F(options.Seconds)} s. Press Ctrl+C to stop.");
        using var stop = new ManualResetEventSlim();
        ConsoleCancelEventHandler cancel = (_, e) =>
        {
            e.Cancel = true;
            stop.Set();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            var watch = Stopwatch.StartNew();
            console.Start();
            long lastReport = 0;
            while (!stop.Wait(250) && watch.Elapsed.TotalSeconds < options.Seconds)
            {
                long second = (long)watch.Elapsed.TotalSeconds;
                if (second >= lastReport + 5)
                {
                    lastReport = second;
                    output.WriteLine($"  {second,4} s: {console.FramesSent} frames sent{(console.LastError is { } e ? $" (last error: {e})" : "")}");
                }
            }
            console.Stop();
            output.WriteLine($"Sent {console.FramesSent} frames in {CliArgs.F(watch.Elapsed.TotalSeconds, "0.0")} s.");
            if (console.LastError is { } last)
            {
                error.WriteLine($"Last network error: {last}");
                return console.FramesSent > 0 ? Program.ExitOk : Program.ExitFailure;
            }
            return Program.ExitOk;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }
}
