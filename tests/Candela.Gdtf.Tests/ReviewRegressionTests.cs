using System.Text;

namespace Candela.Gdtf.Tests;

/// <summary>Regression tests for defects found in the adversarial review of the module.</summary>
public sealed class ReviewRegressionTests
{
    private static byte[] Mutate(byte[] source, Random rng, int flips)
    {
        var copy = (byte[])source.Clone();
        for (int i = 0; i < flips; i++) copy[rng.Next(copy.Length)] = (byte)rng.Next(256);
        return copy;
    }

    /// <summary>
    /// A damaged model file is one bad element: loading it must end in a warning and the primitive / placeholder
    /// fallback, never in an exception that loses the whole fixture (SharpGLTF and the 3DS reader throw many exception
    /// types on corrupt data).
    /// </summary>
    [Theory]
    [InlineData("models/gltf/head.glb", "Robe_Lighting@Robin_MegaPointe@", 40)]
    [InlineData("models/3ds/head.3ds", "Martin_Professional@MAC_Aura@", 40)]
    public void Corrupt_model_files_fall_back_with_a_warning(string entry, string fixture, int variants)
    {
        byte[] original;
        string description;
        string modelName;
        using (var archive = GdtfArchive.Open(TestData.Fixture(fixture)))
        {
            string? actual = archive.ResolveEntryName(entry) ?? archive.EntryNames.First(n => n.StartsWith(Path.GetDirectoryName(entry)!.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
            entry = actual!;
            original = archive.ReadBytes(entry);
            description = Encoding.UTF8.GetString(archive.ReadDescription());
            var ft = GdtfReader.Read(archive);
            string file = Path.GetFileNameWithoutExtension(entry);
            modelName = ft.Models.First(m => string.Equals(m.File, file, StringComparison.OrdinalIgnoreCase)).Name;
        }

        var rng = new Random(1234);
        for (int v = 0; v < variants; v++)
        {
            byte[] damaged = v switch
            {
                0 => Array.Empty<byte>(),
                1 => original[..(original.Length / 2)],
                2 => original[..16],
                _ => Mutate(original, rng, 1 + v % 7),
            };
            var bytes = TestData.Zip(("description.xml", Encoding.UTF8.GetBytes(description)), (entry, damaged));
            using var archive = GdtfArchive.FromBytes(bytes, "damaged.gdtf");
            var ft = GdtfReader.Read(archive);
            var loader = new GdtfModelLoader(archive);
            var model = ft.FindModel(modelName)!;
            var loaded = loader.Load(model); // must not throw
            if (loaded is not null)
                foreach (var part in loaded.Model.Parts)
                    Assert.All(part.Mesh.Positions, p => Assert.True(float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z)));
        }
    }

    [Fact]
    public void Corrupt_3ds_data_only_throws_invalid_data()
    {
        using var archive = GdtfArchive.Open(TestData.Fixture("Martin_Professional@MAC_Aura@"));
        var entry = archive.EntryNames.First(n => n.EndsWith(".3ds", StringComparison.OrdinalIgnoreCase));
        var original = archive.ReadBytes(entry);
        var rng = new Random(99);
        for (int i = 0; i < 300; i++)
        {
            var damaged = Mutate(original, rng, 1 + i % 13);
            try
            {
                ThreeDsReader.Read(damaged, "x");
            }
            catch (InvalidDataException)
            {
            }
        }
    }

    /// <summary>A projective last row would make the geometry tree's composition projective; it is dropped with a warning.</summary>
    [Fact]
    public void Non_affine_geometry_matrices_are_made_affine()
    {
        var ft = TestData.ReadXml("""
            <GDTF DataVersion="1.2"><FixtureType Name="M">
              <Geometries><Geometry Name="Body" Position="{1,0,0,0.5}{0,1,0,0}{0,0,1,0}{0.1,0,0,2}"/></Geometries>
            </FixtureType></GDTF>
            """);
        var m = ft.Geometries[0].Position;
        Assert.Equal((0f, 0f, 0f, 1f), (m.M14, m.M24, m.M34, m.M44));
        Assert.Equal(0.5f, m.M41);
        Assert.Contains(ft.Warnings, w => w.Message.Contains("not affine", StringComparison.Ordinal));
    }

    /// <summary>
    /// GDTF has no XML namespace, but a namespaced description (written by a generic XML tool) is still a GDTF: its
    /// elements are read by local name instead of failing the whole file.
    /// </summary>
    [Fact]
    public void Namespaced_descriptions_are_read_by_local_name()
    {
        var ft = TestData.ReadXml("""
            <GDTF xmlns="urn:example:gdtf" xmlns:x="urn:x" DataVersion="1.1">
              <FixtureType Name="Ns" Manufacturer="M" x:Extra="1">
                <Geometries><Geometry Name="Body"><Beam Name="Beam" LuminousFlux="2000"/></Geometry></Geometries>
                <DMXModes><DMXMode Name="Mode" Geometry="Body"><DMXChannels>
                  <DMXChannel Offset="1" Geometry="Beam"><LogicalChannel Attribute="Dimmer"><ChannelFunction Name="D"/></LogicalChannel></DMXChannel>
                </DMXChannels></DMXMode></DMXModes>
              </FixtureType>
            </GDTF>
            """);
        Assert.Equal("Ns", ft.Name);
        Assert.Equal("1.1", ft.DataVersion);
        Assert.Equal(2000f, ((BeamGeometry)ft.Geometries[0].Children[0]).LuminousFlux);
        Assert.Equal(1, ft.DmxModes.Single().Footprint);
        Assert.Contains(ft.Warnings, w => w.Message.Contains("namespace", StringComparison.Ordinal));
    }
}
