using System.IO.Compression;
using System.Numerics;
using System.Text;
using Candela.Assets;
using Candela.Core.Geometry;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Core.Serialization;

namespace Candela.Project.Tests;

/// <summary>Repository data, a scene with every component type, archive surgery helpers and a manual clock.</summary>
internal static class TestData
{
    /// <summary>Two small real GDTF files of the repository's fixture library.</summary>
    public const string GdtfA = "Briteq@BT-FOG1500@20240710.gdtf";

    public const string GdtfB = "Cyclops_Lighting@Sparkly_300B@Cyclops_Lighting_Official_Library.gdtf";

    private static readonly Lazy<string> Root = new(FindRoot);

    /// <summary>The folder containing <c>Candela.sln</c> (and the GDTF library).</summary>
    public static string RepoRoot => Root.Value;

    /// <summary><c>stages/Фокина.mvr</c>.</summary>
    public static string FokinaMvr => Path.Combine(RepoRoot, "stages", "Фокина.mvr");

    /// <summary>Bytes of a library GDTF file, or null when the name is not in the repository root.</summary>
    public static byte[]? LibraryGdtf(string name)
    {
        if (name.Contains('/') || name.Contains('\\')) return null;
        var path = Path.Combine(RepoRoot, name);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>A small valid PNG (w × h, a colour gradient).</summary>
    public static byte[] Png(int w = 4, int h = 3, byte seed = 0)
    {
        var pixels = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            pixels[i * 4] = (byte)(i * 17 + seed);
            pixels[i * 4 + 1] = (byte)(i * 5);
            pixels[i * 4 + 2] = (byte)(255 - i);
            pixels[i * 4 + 3] = 255;
        }
        return ImageImporter.EncodePng(new ImageData { Width = w, Height = h, Pixels = pixels });
    }

    public const string ObjText = """
        mtllib chair.mtl
        v 0 0 0
        v 1 0 0
        v 0 1 0
        vt 0 0
        vt 1 0
        vt 0 1
        usemtl wood
        f 1/1 2/2 3/3
        """;

    public const string MtlText = """
        newmtl wood
        Kd 0.8 0.6 0.4
        map_Kd tex/wood.png
        """;

    /// <summary>
    /// A project whose scene uses every component type with non-default values, with a model asset (OBJ + MTL + PNG
    /// companion, custom import options), an image, a texture and a GDTF asset, two embedded GDTF copies and three settings
    /// sections.
    /// </summary>
    public static ProjectData RichProject()
    {
        var scene = new Scene("Rich stage") { Id = Guid.NewGuid() };
        var lightLayer = new Layer { Name = "Light", Locked = true };
        var setLayer = new Layer { Name = "Set", Visible = false };
        scene.Layers.Add(lightLayer);
        scene.Layers.Add(setLayer);
        scene.Settings = new SceneSettings
        {
            DisplayUnits = UnitSystem.Imperial,
            BaseHazeScattering = 0.035f,
            HazeAnisotropy = 0.75f,
            AmbientLuminance = 0.01f,
            GridSpacing = 0.5f,
        };

        var store = new AssetStore();
        var model = store.Add(AssetKind.Model, "chair.obj", Utf8(ObjText),
            new Dictionary<string, byte[]> { ["chair.mtl"] = Utf8(MtlText), ["tex/wood.png"] = Png(2, 2, 7) },
            sourcePath: "/home/designer/models/chair.obj",
            importOptions: ImportOptions.Default with { UpAxis = SourceUpAxis.Z, UnitScale = 0.01f, SmoothingAngleDegrees = 45f });
        var image = store.Add(AssetKind.Image, "plan.png", Png(8, 6, 1));
        var texture = store.Add(AssetKind.Texture, "brick.png", Png(4, 4, 99));
        var gdtfAsset = store.Add(AssetKind.Gdtf, GdtfA, LibraryGdtf(GdtfA)!);

        var project = new ProjectData(scene, store) { Thumbnail = Png(16, 9, 3) };

        var rig = new Entity("Truss group") { LayerId = setLayer.Id };
        rig.Local = new Transform(new Vector3(0, 4, 6), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.3f), new Vector3(1, 1, 1));
        rig.Add(new GroupComponent());
        scene.Add(rig);

        var truss = new Entity("Truss 1");
        truss.Add(new TrussComponent
        {
            Kind = TrussKind.Ladder,
            Length = 8f,
            Profile = "F44",
            WeightKg = 42.5f,
            MaxUniformLoadKg = 800f,
            MaxPointLoadKg = 350f,
            ModelAssetId = model.Id,
        });
        scene.Add(truss, rig);

        var hoist = new Entity("Hoist A");
        hoist.Add(new RiggingPointComponent { CapacityKg = 1000f, Carries = { truss.Id } });
        scene.Add(hoist, rig);

        var spot = new Entity("Spot 1") { LayerId = lightLayer.Id };
        spot.Local = new Transform(new Vector3(1.5f, 0, -0.3f));
        spot.Add(new FixtureComponent
        {
            GdtfSpec = GdtfB,
            GdtfMode = "Mode 1",
            Addresses = { new DmxAddress(2, 101), new DmxAddress(2, 120, 2) },
            FixtureId = 101,
            UnitNumber = "U1",
            CustomId = 7,
            Color = new CieColor(0.3127f, 0.329f, 100f),
            CastShadow = false,
            MountedOn = truss.Id,
            ManualAttributes = { ["Dimmer"] = 0.75f, ["Pan"] = -45f },
            FocusPoint = new Vector3(0, 2, 0),
        });
        scene.Add(spot, truss);

        var hazer = new Entity("Hazer");
        hazer.Add(new FixtureComponent { GdtfSpec = GdtfA, FixtureId = 900, Addresses = { new DmxAddress(5, 1) } });
        scene.Add(hazer);

        var chair = new Entity("Chair");
        chair.Add(new MeshComponent { ModelAssetId = model.Id, MaterialOverrides = { [0] = MaterialIds.WoodStage, [1] = texture.ImageKey }, CastShadow = false });
        scene.Add(chair);

        var wall = new Entity("Back wall");
        wall.Add(new WallComponent { Kind = WallKind.Floor, Width = 12f, Height = 8f, Thickness = 0.2f, MaterialId = MaterialIds.StageFloorBlack, UvScale = 2f });
        scene.Add(wall);

        var poster = new Entity("Plan");
        poster.Add(new ImageComponent { ImageAssetId = image.Id, Mode = ImageMode.Poster, Width = 3f, Height = 2f, Opacity = 1f, DoubleSided = false });
        scene.Add(poster);

        var console = new Entity("Console");
        console.Add(new ConsoleComponent
        {
            Model = "grandMA3 light",
            ProtocolId = "sacn",
            Outputs = { new ConsoleOutput { PortName = "Net", Signal = "Ethernet", FirstUniverse = 1, UniverseCount = 8 } },
        });
        scene.Add(console);

        var power = new Entity("Distro");
        power.Add(new PowerSourceComponent
        {
            VoltageV = 400f,
            Phases = 3,
            Outlets = { new PowerOutlet { PortName = "CEE 32", Connector = "CEE 32A", BreakerA = 32f, Phase = 2 } },
        });
        scene.Add(power);

        var cableEntity = new Entity("DMX run");
        var n1 = new CableNode { Position = new Vector3(0, 0, 0.1f), Anchor = new CableAnchor(console.Id, new Vector3(0.1f, 0, 0)) };
        var n2 = new CableNode { Position = new Vector3(2, 3, 5) };
        var n3 = new CableNode { Position = new Vector3(1.5f, 4, 6), Anchor = new CableAnchor(truss.Id, new Vector3(1, 0, 0)) };
        var n4 = new CableNode { Position = new Vector3(4, 4, 6) };
        cableEntity.Add(new CableComponent
        {
            CableTypeId = "dmx-xlr5",
            Label = "U2-01",
            SlackFactor = 1.1f,
            Nodes = { n1, n2, n3, n4 },
            Segments = { new CableSegment(n1.Id, n2.Id), new CableSegment(n2.Id, n3.Id), new CableSegment(n2.Id, n4.Id) },
            Endpoints =
            {
                new CableEndpoint { NodeId = n1.Id, EntityId = console.Id, PortName = "Net" },
                new CableEndpoint { NodeId = n3.Id, EntityId = spot.Id, PortName = "DMX In" },
                new CableEndpoint { NodeId = n4.Id, EntityId = hazer.Id, PortName = "DMX In" },
            },
        });
        scene.Add(cableEntity);

        var atmosphere = new Entity("Atmosphere") { Visible = false };
        atmosphere.Add(new AtmosphereVolumeComponent { Size = new Vector3(30, 20, 12), CellSize = 0.5f, Dissipation = 0.02f, Airflow = new Vector3(0.1f, 0, 0), Enabled = false });
        scene.Add(atmosphere);

        var camera = new Entity("FOH view") { Locked = true };
        camera.Add(new CameraComponent { Orthographic = true, FovYDegrees = 35f, OrthoHeight = 12f, Ev100 = 4.5f, Target = new Vector3(0, 5, 1.5f) });
        scene.Add(camera);

        project.GdtfFiles[GdtfB] = LibraryGdtf(GdtfB)!;
        project.Settings.Set("view", new ViewSection { Mode = "Full", Exposure = 7.5f, Grid = true });
        project.Settings.SetJson("luxOverlay", """{"min":0,"max":2000,"stops":[[0,"#000000"],[1,"#ffffff"]]}""");
        project.Settings.SetJson("layout", """{"panels":["viewport","library"],"split":0.25}""");
        _ = gdtfAsset;
        return project;
    }

    /// <summary>A typed settings section used by tests.</summary>
    public sealed class ViewSection
    {
        public string Mode { get; set; } = "";
        public float Exposure { get; set; }
        public bool Grid { get; set; }
    }

    /// <summary>Scene as Core JSON: equal JSON means equal scenes (ids, tree, layers, settings, every component value).</summary>
    public static string SceneJson(Scene scene) => SceneSerializer.ToJson(scene);

    /// <summary>Rewrites one archive: <paramref name="change"/> gets each entry's name and bytes and returns new bytes or null to drop it.</summary>
    public static void RewriteArchive(string path, Func<string, byte[], byte[]?> change, params (string Name, byte[] Data)[] add)
    {
        var entries = new List<(string Name, byte[] Data)>();
        using (var zip = ZipFile.OpenRead(path))
        {
            foreach (var e in zip.Entries)
            {
                using var s = e.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                var data = change(e.FullName, ms.ToArray());
                if (data is not null) entries.Add((e.FullName, data));
            }
        }
        entries.AddRange(add);
        File.WriteAllBytes(path, Zip(entries.ToArray()));
    }

    public static byte[] Zip(params (string Name, byte[] Data)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                var e = zip.CreateEntry(name, CompressionLevel.Fastest);
                using var s = e.Open();
                s.Write(data);
            }
        }
        return ms.ToArray();
    }

    public static byte[] ReadEntry(string path, string name)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry(name) ?? throw new FileNotFoundException(name);
        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public static IReadOnlyList<string> EntryNames(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.Select(e => e.FullName).ToArray();
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Candela.sln"))) return dir.FullName;
        throw new InvalidOperationException("Candela.sln not found above " + AppContext.BaseDirectory);
    }
}

/// <summary>A temporary folder deleted at the end of a test.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "candela-project-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>A clock that only moves when told to.</summary>
internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
    private long _ticks;

    public override DateTimeOffset GetUtcNow() => _now;

    public override long GetTimestamp() => _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan by)
    {
        _now += by;
        _ticks += by.Ticks;
    }
}

/// <summary>Every concrete component type registered with Core's polymorphic JSON (the file format's discriminators).</summary>
internal static class ComponentTypes
{
    public static IReadOnlyList<Type> All { get; } = typeof(Component)
        .GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonDerivedTypeAttribute), inherit: false)
        .Cast<System.Text.Json.Serialization.JsonDerivedTypeAttribute>()
        .Select(a => a.DerivedType)
        .ToArray();
}
