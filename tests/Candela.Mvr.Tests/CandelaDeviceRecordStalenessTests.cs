using System.Numerics;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Gdtf;

namespace Candela.Mvr.Tests;

/// <summary>
/// A generic-device record in Candela's <c>UserData</c> describes the object as Candela wrote it. Another program that
/// keeps the block but changes the object's fixture type (a splitter replaced by a real fixture) must not have its
/// change undone on import, nor have the new GDTF filed under the generic device's name; a renamed copy of the same
/// device is still brought back.
/// </summary>
public sealed class CandelaDeviceRecordStalenessTests
{
    private const string SplitterFile = "Candela@Generic_Stale_Splitter@v1.gdtf";
    private static readonly byte[] SplitterGdtf = ConformanceTests.ModellessGdtf("Candela", "Generic Stale Splitter", 0);

    /// <summary>A real fixture of the repository library (complete GDTF: geometry, models, DMX modes).</summary>
    private const string RealFile = "Briteq@BT-FOG1500@20240710.gdtf";

    private static MvrDocument ExportSplitter(out Entity splitter)
    {
        var scene = new Scene("Stale");
        splitter = scene.Add(new Entity("Splitter") { Local = new Transform(new Vector3(1, 2, 0)) });
        splitter.Add(new FixtureComponent { GdtfSpec = SplitterFile, GdtfMode = "Mode A" });
        var options = new MvrExportOptions
        {
            GdtfProvider = spec => spec == SplitterFile ? SplitterGdtf : null,
            IsCandelaDevice = spec => spec == SplitterFile,
        };
        using var ms = new MemoryStream();
        MvrWriter.Write(scene, ms, options);
        return MvrReader.Read(ms.ToArray(), "stale.mvr");
    }

    /// <summary>Another program changed the splitter's object into <see cref="RealFile"/> (embedded or only referenced).</summary>
    private static string ReplaceWithRealFixture(MvrDocument document, Guid objectId, bool embed)
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(TestData.RepoRoot, RealFile));
        string mode;
        using (var archive = GdtfArchive.FromBytes(bytes, RealFile)) mode = GdtfReader.Read(archive).DmxModes[0].Name;
        var fixture = (MvrFixture)document.AllObjects().Single(o => o.Uuid == objectId);
        fixture.GdtfSpec = RealFile;
        fixture.GdtfMode = mode;
        document.Files.RemoveAll(f => f.Name == SplitterFile);
        if (embed) document.Files.Add(new MvrEmbeddedFile(RealFile, bytes));
        Assert.Contains(document.UserData, u => u.Provider == CandelaUserData.Provider); // the block was kept
        return mode;
    }

    private static FixtureLibrary LibraryWith(string folder, params (string Name, byte[] Bytes)[] files)
    {
        Directory.CreateDirectory(folder);
        foreach (var (name, bytes) in files) File.WriteAllBytes(Path.Combine(folder, name), bytes);
        return FixtureLibrary.Scan(folder);
    }

    [Fact]
    public void The_record_carries_the_device_s_fixture_type_and_a_damaged_type_is_reported()
    {
        var document = ExportSplitter(out var splitter);
        Guid expected;
        using (var archive = GdtfArchive.FromBytes(SplitterGdtf, SplitterFile)) expected = GdtfReader.Read(archive).FixtureTypeId;
        var record = CandelaUserData.ReadDevices(document)[splitter.Id];
        Assert.Equal(expected, record.FixtureTypeId);

        var element = document.UserData.Single(u => u.Provider == CandelaUserData.Provider).Element!.Elements("Device").Single();
        element.SetAttributeValue("type", "not-a-uuid");
        var problems = new List<Core.Diagnostics.Issue>();
        Assert.Empty(CandelaUserData.ReadDevices(document, problems));
        Assert.Equal(MvrIssueCodes.ValueInvalid, Assert.Single(problems).Code);
        element.SetAttributeValue("type", null); // a record without a type (nothing embedded at export) is still read
        Assert.Null(CandelaUserData.ReadDevices(document)[splitter.Id].FixtureTypeId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_object_changed_to_another_fixture_by_another_program_keeps_the_new_fixture(bool libraryHasTheDevice)
    {
        var document = ExportSplitter(out var splitter);
        string mode = ReplaceWithRealFixture(document, splitter.Id, embed: true);
        string folder = Path.Combine(Path.GetTempPath(), "candela-mvr-stale-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = libraryHasTheDevice
                ? new MvrImportOptions { Library = LibraryWith(folder, (SplitterFile, SplitterGdtf)) }
                : new MvrImportOptions();
            var imported = MvrImporter.Import(document, options);
            var fixture = imported.AllEntities().Single(e => e.Name == "Splitter").Get<FixtureComponent>()!;
            Assert.Equal(RealFile, fixture.GdtfSpec); // not forced back to the splitter, nor the real GDTF filed under its name
            Assert.Equal(mode, fixture.GdtfMode);
            var reference = imported.Gdtfs.Single(g => g.SpecName == RealFile);
            Assert.False(reference.IsCandelaDevice);
            Assert.Equal(RealFile, reference.FileName);
            Assert.Equal(MvrGdtfSource.Embedded, reference.Source);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void An_object_re_referenced_to_a_library_fixture_without_embedding_is_not_forced_back()
    {
        var document = ExportSplitter(out var splitter);
        ReplaceWithRealFixture(document, splitter.Id, embed: false);
        string folder = Path.Combine(Path.GetTempPath(), "candela-mvr-stale-" + Guid.NewGuid().ToString("N"));
        try
        {
            var library = LibraryWith(folder, (SplitterFile, SplitterGdtf), (RealFile, File.ReadAllBytes(Path.Combine(TestData.RepoRoot, RealFile))));
            var imported = MvrImporter.Import(document, new MvrImportOptions { Library = library });
            var fixture = imported.AllEntities().Single(e => e.Name == "Splitter").Get<FixtureComponent>()!;
            Assert.Equal(RealFile, fixture.GdtfSpec);
            Assert.False(imported.Gdtfs.Single(g => g.SpecName == RealFile).IsCandelaDevice);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void The_same_device_renamed_and_not_embedded_still_resolves_from_the_library()
    {
        // a program that renames files and drops the embedded copy: the spec names the device's own file, so the
        // library's copy is used as before
        var document = ExportSplitter(out var splitter);
        var obj = (MvrFixture)document.AllObjects().Single(o => o.Uuid == splitter.Id);
        document.Files.RemoveAll(f => f.Name == SplitterFile);
        obj.GdtfSpec = "Candela@Generic_Stale_Splitter@v1"; // without the extension, as some programs write it
        string folder = Path.Combine(Path.GetTempPath(), "candela-mvr-stale-" + Guid.NewGuid().ToString("N"));
        try
        {
            var imported = MvrImporter.Import(document, new MvrImportOptions { Library = LibraryWith(folder, (SplitterFile, SplitterGdtf)) });
            var fixture = imported.AllEntities().Single(e => e.Name == "Splitter").Get<FixtureComponent>()!;
            Assert.Equal(SplitterFile, fixture.GdtfSpec);
            var reference = Assert.Single(imported.Gdtfs);
            Assert.True(reference.IsCandelaDevice);
            Assert.Equal(MvrGdtfSource.Library, reference.Source);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
