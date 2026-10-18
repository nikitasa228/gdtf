using System.Numerics;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Gdtf;
using Xunit.Abstractions;

namespace Candela.Mvr.Tests;

/// <summary>
/// <c>stages/Фокина.mvr</c>: a Depence export (MVR 1.4) with 91 fixtures on two layers and stub GDTFs (one dimmer
/// channel, no models). Every fixture must come out with a real library GDTF.
/// </summary>
public sealed class FokinaTests
{
    private const string Probe = "B1E1000D-C20E-44B2-922D-7D700AF014AB";

    /// <summary>
    /// The five distinct <c>GDTFSpec</c> values of the file and the library files they resolve to (see docs/gdtf.md:
    /// only the Zonda 3 FX is in the repository; the others get same-brand substitutes until their GDTFs are added).
    /// </summary>
    private static readonly (string Spec, string File, FixtureMatchKind Kind, int Count, string Mode)[] Expected =
    {
        ("Ayrton@MagicBlade R.gdtf", "Ayrton@MagicPanel_602@V2.5_Add_Mode.gdtf", FixtureMatchKind.Substitute, 28, "Extended"),
        ("Martin@Atomic 3000 LED.gdtf", "Martin_Professional@VDO_Atomic_Bold@20260620.gdtf", FixtureMatchKind.Substitute, 4, "Mode 1"),
        ("Ayrton@Zonda 3 Wash.gdtf", "Ayrton@Zonda_3_FX@V1.81_Corrected_Factory_Tilt.gdtf", FixtureMatchKind.Substitute, 28, "Basic"),
        ("Ayrton @Rivale Profile.gdtf", "Ayrton@Rivale_Wash@V1.8_Correct_Gobo_Value.gdtf", FixtureMatchKind.Substitute, 27, "Standard"),
        ("Ayrton@Zonda 3 FX.gdtf", "Ayrton@Zonda_3_FX@V1.81_Corrected_Factory_Tilt.gdtf", FixtureMatchKind.SameModel, 4, "Basic"),
    };

    private readonly ITestOutputHelper _out;

    public FokinaTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Reader_keeps_every_element_of_the_Depence_export()
    {
        var doc = MvrReader.ReadFile(TestData.FokinaMvr);
        Assert.Equal(1, doc.VerMajor);
        Assert.Equal(4, doc.VerMinor);
        Assert.Equal("Фокина.mvr", doc.SourceFileName);
        var userData = Assert.Single(doc.UserData);
        Assert.Equal("Depence", userData.Provider);
        Assert.Equal("4.0", userData.Version);
        Assert.Equal(new[] { "Default", "Light" }, doc.Layers.Select(l => l.Name));
        Assert.Equal(Guid.Parse("6BDF5C24-55DA-4263-A39D-337DE56F7BED"), doc.Layers[0].Uuid);

        var fixtures = doc.AllObjects().OfType<MvrFixture>().ToList();
        Assert.Equal(91, fixtures.Count);
        Assert.Equal(91, fixtures.Select(f => f.Uuid).Distinct().Count());
        Assert.All(fixtures, f =>
        {
            Assert.True(f.HasMatrix);
            Assert.Single(f.Addresses);
            Assert.False(f.CastShadow);
            // Depence writes a <FixtureTypeId> element that MVR does not define: it is kept verbatim.
            Assert.Contains(f.UnknownElements, e => e.Name.LocalName == "FixtureTypeId");
        });
        Assert.Equal(5, doc.Files.Count);
        Assert.All(doc.Files, f => Assert.Equal(MvrFileKind.Gdtf, f.Kind));

        var probe = fixtures.Single(f => f.Uuid == Guid.Parse(Probe));
        Assert.Equal("MagicBlade R", probe.Name);
        Assert.Equal("Ayrton@MagicBlade R.gdtf", probe.GdtfSpec);
        Assert.Equal("Extended", probe.GdtfMode);
        Assert.Equal("121", probe.FixtureId);
        Assert.Equal(121, probe.UnitNumber);
        Assert.Equal(0, probe.CustomId);
        Assert.Equal(new CieColor(0.312712f, 0.329008f, 100f), probe.Color);
        var address = Assert.Single(probe.Addresses);
        Assert.Equal(741, address.Absolute);
        Assert.Equal(0, address.Break);
        Assert.Equal(new DmxAddress(2, 229, 1), address.ToDmxAddress());
        AssertNear(new Vector3(4.485159f, 16.74185f, 1.760395f), probe.Matrix.Translation, 1e-5f);
        Assert.Same(doc.FindFile(probe.GdtfSpec), doc.FindFile("ayrton@magicblade r"));

        // Only the unknown Depence element is worth a note.
        Assert.All(doc.Issues, i => Assert.Equal(MvrIssueCodes.UnknownElement, i.Code));
    }

    [Fact]
    public void Import_resolves_every_fixture_to_a_library_gdtf()
    {
        var result = MvrImporter.ImportFile(TestData.FokinaMvr, new MvrImportOptions { Library = TestData.Library });
        var scene = result.Scene;

        Assert.Equal("Фокина", scene.Name);
        Assert.Equal(91, scene.Count);
        Assert.Equal(91, result.RootEntities.Count);
        Assert.Null(result.Group);
        Assert.Equal(new[] { "Default", "Light" }, scene.Layers.Select(l => l.Name));
        Assert.Equal(Guid.Parse("134C89D9-06DE-4F7C-9FBD-7DCFE36B5B82"), scene.Layers[1].Id);
        Assert.Empty(result.Assets);

        var fixtures = scene.Query<FixtureComponent>().ToList();
        Assert.Equal(91, fixtures.Count);
        foreach (var i in result.Issues) _out.WriteLine($"{i.Severity} {i.Code}: {i.Message}");

        // Resolution per spec name.
        foreach (var g in result.Gdtfs)
        {
            _out.WriteLine(g.Describe());
            foreach (var m in g.Modes) _out.WriteLine($"    mode '{m.Requested}' → '{m.Resolved}' ({m.Footprint} ch, patch spacing {m.InferredFootprint}, exact {m.Exact})");
        }
        Assert.Equal(Expected.Length, result.Gdtfs.Count);
        foreach (var (spec, file, kind, count, mode) in Expected)
        {
            var r = Assert.Single(result.Gdtfs, g => g.SpecName == spec);
            Assert.Equal(MvrGdtfSource.Library, r.Source);
            Assert.True(r.IsResolved);
            Assert.Equal(file, r.FileName);
            Assert.Equal(kind, r.MatchKind);
            Assert.Equal(kind != FixtureMatchKind.Substitute, r.IsExactModel);
            Assert.Equal(count, r.ObjectCount);
            Assert.True(r.EmbeddedIsStub, $"{spec} is a Depence stub");
            Assert.NotNull(r.EmbeddedBytes);
            Assert.NotNull(r.FixtureType);
            Assert.NotEmpty(r.FixtureType!.DmxModes);
            Assert.Equal(Path.Combine(TestData.RepoRoot, file), r.LibraryFilePath);
            var modeResolution = Assert.Single(r.Modes);
            Assert.Equal(mode, modeResolution.Requested);
            Assert.NotNull(r.FixtureType.FindMode(modeResolution.Resolved!));
            Assert.Equal(count, modeResolution.ObjectCount);
            Assert.Equal(count, fixtures.Count(f => f.Component.GdtfSpec == file && f.Entity.Name.Length > 0 &&
                result.Document.AllObjects().OfType<MvrFixture>().Single(o => o.Uuid == f.Entity.Id).GdtfSpec == spec));
        }
        Assert.Equal(4, fixtures.Select(f => f.Component.GdtfSpec).Distinct().Count());
        Assert.All(fixtures, f =>
        {
            Assert.NotNull(TestData.Library.FindByFileName(f.Component.GdtfSpec));
            Assert.Single(f.Component.Addresses);
            Assert.False(f.Component.CastShadow);
            Assert.NotEqual(0, f.Component.FixtureId);
        });

        // Substitutes are reported (one warning per spec), the exact model with a stub note; nothing is an error.
        Assert.Equal(4, result.Issues.Count(i => i.Code == MvrIssueCodes.GdtfSubstitute));
        Assert.Single(result.Issues, i => i.Code == MvrIssueCodes.GdtfStub);
        Assert.DoesNotContain(result.Issues, i => i.Code == MvrIssueCodes.GdtfUnresolved);
        Assert.DoesNotContain(result.Issues, i => i.Severity == Core.Diagnostics.IssueSeverity.Error);
        var substitute = result.Issues.First(i => i.Code == MvrIssueCodes.GdtfSubstitute);
        Assert.Equal(28, substitute.Entities.Count);

        // Modes missing from a substitute are matched by name and patch spacing and reported.
        var atomic = result.Gdtfs.Single(g => g.SpecName.StartsWith("Martin", StringComparison.Ordinal)).Modes.Single();
        Assert.False(atomic.Exact);
        Assert.Equal(3, atomic.InferredFootprint);
        Assert.Equal(3, atomic.Footprint);
        Assert.Contains(result.Issues, i => i.Code == MvrIssueCodes.ModeUnknown && i.Message.Contains("Mode 1", StringComparison.Ordinal));
        Assert.Contains(result.Issues, i => i.Code == MvrIssueCodes.ModeUnknown && i.Message.Contains("'Standard'", StringComparison.Ordinal));
        Assert.Equal(2, result.Issues.Count(i => i.Code == MvrIssueCodes.ModeUnknown));

        // The MagicBlade R substitute keeps its 'Extended' mode name, but that mode is far wider than the 44-channel
        // spacing of the MagicBlade patch: the overlap is reported (only the substitute causes it).
        var overlap = Assert.Single(result.Issues, i => i.Code == MvrIssueCodes.ModeFootprint);
        Assert.Contains("MagicBlade R", overlap.Message, StringComparison.Ordinal);
        Assert.Equal(28, overlap.Entities.Count);
        var blade = result.Gdtfs.Single(g => g.SpecName.Contains("MagicBlade", StringComparison.Ordinal)).Modes.Single();
        Assert.True(blade.Exact);
        Assert.Equal(44, blade.InferredFootprint);
        Assert.True(blade.Footprint > blade.InferredFootprint);
    }

    [Fact]
    public void Probe_fixture_has_the_expected_position_patch_and_an_orthonormal_rotation()
    {
        var result = MvrImporter.ImportFile(TestData.FokinaMvr, new MvrImportOptions { Library = TestData.Library });
        var entity = result.Scene.Find(Guid.Parse(Probe));
        Assert.NotNull(entity);
        Assert.Same(entity, result.FindEntity(Guid.Parse(Probe)));
        Assert.Equal("MagicBlade R", entity!.Name);
        Assert.Equal(result.Scene.Layers[0].Id, entity.LayerId);
        AssertNear(new Vector3(4.485159f, 16.74185f, 1.760395f), entity.WorldPosition, 1e-5f);

        var fc = entity.Get<FixtureComponent>()!;
        Assert.Equal(new DmxAddress(2, 229, 1), Assert.Single(fc.Addresses));
        Assert.Equal(741, fc.Addresses[0].Absolute);
        Assert.Equal(121, fc.FixtureId);
        Assert.Equal("121", fc.UnitNumber);
        Assert.Equal("Extended", fc.GdtfMode);
        Assert.Equal("Ayrton@MagicPanel_602@V2.5_Add_Mode.gdtf", fc.GdtfSpec);
        Assert.Equal(new CieColor(0.312712f, 0.329008f, 100f), fc.Color);
        Assert.False(fc.CastShadow);

        AssertProperRotation(entity.Local);
        // The fixture hangs: its local Z (GDTF beam axis is −Z) points down.
        var down = Vector3.Transform(Vector3.UnitZ, entity.Local.Rotation);
        Assert.True(down.Z < -0.999f, $"local Z maps to {down}");
    }

    [Fact]
    public void Every_fixture_transform_reproduces_its_mvr_matrix()
    {
        var result = MvrImporter.ImportFile(TestData.FokinaMvr, new MvrImportOptions { Library = TestData.Library });
        int mirrored = 0;
        foreach (var o in result.Document.AllObjects())
        {
            var e = result.FindEntity(o.Uuid)!;
            if (MvrMatrix.Determinant3(o.Matrix) < 0) mirrored++;
            AssertProperRotation(e.Local, allowMirror: true);
            var m = e.Local.ToMatrix();
            AssertMatrixNear(o.Matrix, m, 2e-5f);
        }
        Assert.Equal(0, mirrored); // Depence writes proper rotations; mirrors are covered by the synthetic tests.
    }

    [Fact]
    public void Export_and_reimport_keeps_entities_positions_patch_and_gdtfs()
    {
        var first = MvrImporter.ImportFile(TestData.FokinaMvr, new MvrImportOptions { Library = TestData.Library });
        var options = new MvrExportOptions { GdtfProvider = MvrExportOptions.GdtfFromLibrary(TestData.Library), ProviderVersion = "test" };

        using var ms = new MemoryStream();
        var export = MvrWriter.Write(first.Scene, ms, options);
        foreach (var i in export.Issues) _out.WriteLine($"export {i.Severity} {i.Code}: {i.Message}");
        Assert.Equal(91, export.ObjectCount);
        Assert.Equal(0, export.ConnectionCount);
        Assert.DoesNotContain(export.Issues, i => i.Code == MvrIssueCodes.ExportResourceMissing);
        Assert.Equal(4, export.Document.Files.Count); // the four distinct library files, embedded once each
        byte[] bytes = ms.ToArray();

        // Deterministic: the same scene gives the same bytes.
        using (var again = new MemoryStream())
        {
            MvrWriter.Write(first.Scene, again, options);
            Assert.Equal(bytes, again.ToArray());
        }

        var doc = MvrReader.Read(bytes, "roundtrip.mvr");
        Assert.Equal(1, doc.VerMajor);
        Assert.Equal(6, doc.VerMinor);
        Assert.Equal("Candela", doc.Provider);
        Assert.Equal("test", doc.ProviderVersion);
        Assert.Empty(doc.Issues);

        var second = MvrImporter.Import(doc, new MvrImportOptions { Library = TestData.Library });
        Assert.Equal(first.Scene.Count, second.Scene.Count);
        Assert.Equal(first.Scene.Layers.Select(l => (l.Id, l.Name)), second.Scene.Layers.Select(l => (l.Id, l.Name)));
        // The embedded library GDTFs are complete now, so they are used directly.
        Assert.All(second.Gdtfs, g => Assert.Equal(MvrGdtfSource.Embedded, g.Source));
        Assert.DoesNotContain(second.Issues, i => i.Severity != Core.Diagnostics.IssueSeverity.Note);

        foreach (var a in first.Scene.All())
        {
            var b = second.Scene.Find(a.Id);
            Assert.NotNull(b);
            Assert.Equal(a.Name, b!.Name);
            Assert.Equal(a.LayerId, b.LayerId);
            Assert.True(Vector3.Distance(a.WorldPosition, b.WorldPosition) <= 1e-4f, $"{a.Name}: {a.WorldPosition} vs {b.WorldPosition}");
            AssertMatrixNear(a.WorldMatrix, b.WorldMatrix, 1e-4f);
            var fa = a.Get<FixtureComponent>()!;
            var fb = b.Get<FixtureComponent>()!;
            Assert.Equal(fa.GdtfSpec, fb.GdtfSpec);
            Assert.Equal(fa.GdtfMode, fb.GdtfMode);
            Assert.Equal(fa.Addresses, fb.Addresses);
            Assert.Equal(fa.FixtureId, fb.FixtureId);
            Assert.Equal(fa.UnitNumber, fb.UnitNumber);
            Assert.Equal(fa.Color, fb.Color);
            Assert.Equal(fa.CastShadow, fb.CastShadow);
        }
    }

    [Fact]
    public void Merge_puts_the_scene_under_one_group_and_remaps_repeated_ids()
    {
        var target = new Scene("Venue");
        var stage = target.Add(new Entity("Stage floor"));
        int before = target.Count;

        var first = MvrImporter.MergeFile(target, TestData.FokinaMvr, new MvrImportOptions { Library = TestData.Library });
        Assert.NotNull(first.Group);
        Assert.Equal("Фокина", first.Group!.Name);
        Assert.NotNull(first.Group.Get<GroupComponent>());
        Assert.Same(first.Group, Assert.Single(first.RootEntities));
        Assert.Equal(before + 1 + 91, target.Count);
        Assert.Equal(new[] { stage, first.Group }, target.Roots);
        // "Default" exists in the target and is reused by name; "Light" is added with the MVR uuid.
        Assert.Equal(new[] { "Default", "Light" }, target.Layers.Select(l => l.Name));
        Assert.Equal(target.Layers[0].Id, first.Scene.Find(Guid.Parse(Probe))!.LayerId);
        Assert.DoesNotContain(first.Issues, i => i.Code == MvrIssueCodes.IdRemapped);

        var second = MvrImporter.MergeFile(target, TestData.FokinaMvr, new MvrImportOptions { Library = TestData.Library, GroupName = "Copy" });
        Assert.Equal(before + 2 * (1 + 91), target.Count);
        Assert.Contains(second.Issues, i => i.Code == MvrIssueCodes.IdRemapped);
        var copy = second.FindEntity(Guid.Parse(Probe))!;
        Assert.NotEqual(Guid.Parse(Probe), copy.Id);
        Assert.Same(second.Group, copy.Parent);
        AssertNear(first.FindEntity(Guid.Parse(Probe))!.WorldPosition, copy.WorldPosition, 1e-6f);
        Assert.Equal(2, target.Layers.Count);
    }

    internal static void AssertNear(Vector3 expected, Vector3 actual, float tolerance) =>
        Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"expected {expected}, got {actual}");

    internal static void AssertMatrixNear(Matrix4x4 expected, Matrix4x4 actual, float tolerance)
    {
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
                Assert.True(MathF.Abs(expected[r, c] - actual[r, c]) <= tolerance, $"M{r + 1}{c + 1}: expected {expected[r, c]}, got {actual[r, c]}\n{expected}\n{actual}");
    }

    /// <summary>The rotation part is orthonormal with determinant +1 and the scale is ±1 per axis (−1 only for mirrors).</summary>
    internal static void AssertProperRotation(Core.Math.Transform t, bool allowMirror = false)
    {
        Assert.InRange(t.Rotation.Length(), 0.99999f, 1.00001f);
        var r = Matrix4x4.CreateFromQuaternion(t.Rotation);
        var x = new Vector3(r.M11, r.M12, r.M13);
        var y = new Vector3(r.M21, r.M22, r.M23);
        var z = new Vector3(r.M31, r.M32, r.M33);
        Assert.InRange(x.Length(), 0.9999f, 1.0001f);
        Assert.InRange(y.Length(), 0.9999f, 1.0001f);
        Assert.InRange(z.Length(), 0.9999f, 1.0001f);
        Assert.InRange(Vector3.Dot(x, y), -1e-4f, 1e-4f);
        Assert.InRange(Vector3.Dot(x, z), -1e-4f, 1e-4f);
        Assert.InRange(Vector3.Dot(y, z), -1e-4f, 1e-4f);
        Assert.InRange(Vector3.Dot(Vector3.Cross(x, y), z), 0.9999f, 1.0001f);
        Assert.Equal(1f, MathF.Abs(t.Scale.Y));
        Assert.Equal(1f, MathF.Abs(t.Scale.Z));
        Assert.Equal(1f, t.Scale.Y);
        Assert.Equal(1f, t.Scale.Z);
        Assert.Equal(1f, allowMirror ? MathF.Abs(t.Scale.X) : t.Scale.X);
    }
}
