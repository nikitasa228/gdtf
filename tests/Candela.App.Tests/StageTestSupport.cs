using System.Numerics;
using Candela.App.Session;
using Candela.Core.Math;
using Candela.Core.Presets;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Engine;
using Candela.Gdtf;
using Candela.Rendering;

namespace Candela.App.Tests;

/// <summary>
/// Headless stage engines for the engine-backed panel tests: the repository's fixture library is scanned once and
/// shared; every engine gets its own cache folder, so no test touches the user's folders.
/// </summary>
internal static class StageTestSupport
{
    /// <summary>A moving head with pan/tilt, zoom, colour and gobo wheels (MAC Viper Profile).</summary>
    public const string Viper = "Martin_Professional@MAC_Viper_Profile@20230516NoMeas.gdtf";

    private static readonly Lazy<string> s_root = new(() =>
        DevelopmentLibrary.FindRepositoryRoot() ?? throw new InvalidOperationException("Tests must run inside the repository (Candela.sln not found)."));

    private static readonly Lazy<FixtureLibrary> s_library = new(() =>
        FixtureLibrary.Scan(Root, FixtureSpecsIndex.Load(Path.Combine(Root, FixtureSpecsIndex.DefaultRelativePath))));

    /// <summary>The repository root.</summary>
    public static string Root => s_root.Value;

    /// <summary>The shared fixture library.</summary>
    public static FixtureLibrary Library => s_library.Value;

    /// <summary><c>stages/Фокина.mvr</c>.</summary>
    public static string Fokina => Path.Combine(Root, "stages", "Фокина.mvr");

    /// <summary>A new engine over the shared library with a private cache folder in <paramref name="dir"/>.</summary>
    public static StageEngine CreateEngine(TempDir dir) => new(new StageEngineOptions
    {
        Library = Library,
        CacheDirectory = dir.File("cache"),
        DmxParallelism = 2,
        SimulationParallelism = 2,
    });

    /// <summary>Stage tool state with presets in memory.</summary>
    public static StageToolState CreateTools() => new(new PreviewSettings(), new FinalRenderSettings(), new MemoryPresetStore());

    /// <summary>Adds a fixture entity patched at <paramref name="universe"/>.<paramref name="address"/>.</summary>
    public static Entity AddFixture(StageEngine engine, string name, int fixtureId, Vector3 position, int universe = 1, int address = 1,
        string spec = Viper)
    {
        var e = new Entity(name) { Local = new Transform(position) };
        var f = new FixtureComponent { GdtfSpec = spec, FixtureId = fixtureId };
        if (universe > 0)
            f.Addresses.Add(new DmxAddress(universe, address));
        e.Add(f);
        engine.Scene.Add(e);
        return e;
    }

    /// <summary>Adds a wall (or floor) entity.</summary>
    public static Entity AddWall(StageEngine engine, string name, float width, float height, WallKind kind = WallKind.Wall)
    {
        var e = new Entity(name);
        e.Add(new WallComponent { Kind = kind, Width = width, Height = height });
        engine.Scene.Add(e);
        return e;
    }

    /// <summary>A console with one DMX output.</summary>
    public static Entity AddConsole(StageEngine engine, string name, string port, int universe)
    {
        var e = new Entity(name);
        var c = new ConsoleComponent();
        c.Outputs.Clear();
        c.Outputs.Add(new ConsoleOutput { PortName = port, Signal = "DMX512", FirstUniverse = universe });
        e.Add(c);
        engine.Scene.Add(e);
        return e;
    }

    /// <summary>A cable from one port to another, both ends anchored to their devices.</summary>
    public static Entity AddCable(StageEngine engine, string typeId, string label, (Entity Entity, string Port) from, (Entity Entity, string Port) to)
    {
        var e = new Entity(label);
        var c = new CableComponent { CableTypeId = typeId, Label = label };
        Guid a = Plug(c, from), b = Plug(c, to);
        c.Segments.Add(new CableSegment(a, b));
        e.Add(c);
        engine.Scene.Add(e);
        return e;
    }

    private static Guid Plug(CableComponent c, (Entity Entity, string Port) end)
    {
        var node = new CableNode { Anchor = new CableAnchor(end.Entity.Id, Vector3.Zero) };
        c.Nodes.Add(node);
        c.Endpoints.Add(new CableEndpoint { NodeId = node.Id, EntityId = end.Entity.Id, PortName = end.Port });
        return node.Id;
    }

    /// <summary>A free UDP port on the loopback interface (bound and released at once).</summary>
    public static int FreeUdpPort()
    {
        using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram,
            System.Net.Sockets.ProtocolType.Udp);
        socket.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        return ((System.Net.IPEndPoint)socket.LocalEndPoint!).Port;
    }

    /// <summary>Ticks the engine until <paramref name="condition"/> holds or <paramref name="timeout"/> passes.</summary>
    public static bool TickUntil(StageEngine engine, Func<bool> condition, TimeSpan timeout, double step = 1.0 / 30)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            engine.Tick(step);
            if (condition())
                return true;
            Thread.Sleep(10);
        }

        return condition();
    }
}
