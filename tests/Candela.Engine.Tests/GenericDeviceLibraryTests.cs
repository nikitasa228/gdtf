using Candela.Core.Diagnostics;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Electrical;
using Candela.Engine;
using Candela.Gdtf;

namespace Candela.Engine.Tests;

/// <summary>
/// <see cref="GenericDeviceLibrary"/>: the generic cabling devices are written into the engine's cache folder once,
/// byte-identical on every run, added to its library (and resolved by the DMX engine and the electrical model like any
/// fixture type), repaired when a file was damaged, left alone when the library already has them, and a cache folder
/// that cannot be written is reported instead of throwing.
/// </summary>
public sealed class GenericDeviceLibraryTests
{
    private static StageEngine CreateEngine(string cache) => new(new StageEngineOptions
    {
        // A private library: registering adds files to it, and the shared one must stay as the other tests expect.
        Library = new FixtureLibrary(),
        CacheDirectory = cache,
        DmxParallelism = 1,
        SimulationParallelism = 1,
    });

    [Fact]
    public void Generic_devices_are_written_once_added_to_the_library_and_repaired_when_damaged()
    {
        string cache = TestEnvironment.NewTempDirectory("generic-devices");
        using (var engine = CreateEngine(cache))
        {
            Assert.Equal(GenericDevices.All.Count, GenericDeviceLibrary.Register(engine));
            foreach (var device in GenericDevices.All)
                Assert.NotNull(engine.Library.FindByFileName(device.FileName));
            // Registering again finds them in the library: nothing more is added.
            Assert.Equal(0, GenericDeviceLibrary.Register(engine));
        }

        string folder = Path.Combine(cache, GenericDeviceLibrary.FolderName);
        var splitter = GenericDevices.All[0];
        string path = Path.Combine(folder, splitter.FileName);
        Assert.Equal(GenericDevices.GdtfBytes(splitter), File.ReadAllBytes(path));
        var written = File.GetLastWriteTimeUtc(path);

        // A second engine over the same cache reuses the identical files; a damaged one is written again.
        string damaged = Path.Combine(folder, GenericDevices.All[^1].FileName);
        File.WriteAllText(damaged, "not a gdtf");
        using (var engine = CreateEngine(cache))
        {
            Assert.Equal(GenericDevices.All.Count, GenericDeviceLibrary.Register(engine));
            Assert.Equal(written, File.GetLastWriteTimeUtc(path));
            Assert.Equal(GenericDevices.GdtfBytes(GenericDevices.All[^1]), File.ReadAllBytes(damaged));
            Assert.Empty(Directory.GetFiles(folder, "*.tmp"));

            // The DMX engine and the electrical model resolve a placed node like any fixture type.
            var node = GenericDevices.All.First(d => d.Kind == GenericDeviceKind.NetworkNode);
            var entity = new Entity("Node 1");
            entity.Add(new FixtureComponent { GdtfSpec = node.FileName, GdtfMode = node.ModeName });
            engine.Scene.Add(entity);
            var ports = engine.Network.Ports.GetPorts(entity);
            Assert.Contains(ports, p => p.Signal == SignalKind.Ethernet && p.Direction == PortDirection.Input);
            Assert.Contains(ports, p => p.Signal == SignalKind.Dmx512 && p.Direction == PortDirection.Output);
        }
    }

    [Fact]
    public void A_cache_folder_that_cannot_be_written_is_reported_and_the_library_is_unchanged()
    {
        string cache = TestEnvironment.NewTempDirectory("generic-devices-blocked");
        // A file where the folder should be: creating the folder fails.
        File.WriteAllText(Path.Combine(cache, GenericDeviceLibrary.FolderName), "in the way");
        using var engine = CreateEngine(cache);
        int before = engine.Library.Entries.Count;
        Assert.Equal(0, GenericDeviceLibrary.Register(engine));
        Assert.Equal(before, engine.Library.Entries.Count);
        var issue = Assert.Single(engine.Issues.Current, i => i.Code == GenericDeviceLibrary.IssueCode);
        Assert.Equal(IssueSeverity.Warning, issue.Severity);
        Assert.Contains(GenericDeviceLibrary.FolderName, issue.Message, StringComparison.Ordinal);
    }
}
