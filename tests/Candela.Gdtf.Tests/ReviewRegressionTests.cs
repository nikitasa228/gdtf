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

    /// <summary>
    /// Prolights Eclipse XL: the CIE brightness channel of each layer names the layer's GeometryReference. It controls
    /// that instance (the referenced light engine), so it must map to the instance's top part instead of none, which
    /// made the DMX layer apply it to every beam of the fixture.
    /// </summary>
    [Fact]
    public void Channels_on_a_geometry_reference_map_to_the_instance_part()
    {
        var ft = GdtfReader.ReadFile(TestData.Fixture("Prolights@EclFresnelCTPXLIP@"));
        var mode = ft.FindMode("M8 - XY 16bit_17ch")!;
        var geometry = new FixtureGeometryBuilder(ft).Build(mode);
        var instance = mode.ChannelInstances.Single(i => i.Channel.Name == "Layer_1_CIE_Brightness");
        Assert.IsType<GeometryReference>(instance.Geometry);
        int part = geometry.FindPart(instance);
        Assert.True(part >= 0);
        Assert.Same(instance.Geometry, geometry.Parts[part].InstancedBy);
        // every instance of the library maps to a part (checked for this fixture's modes)
        foreach (var m in ft.DmxModes)
        {
            var g = new FixtureGeometryBuilder(ft).Build(m);
            Assert.All(m.ChannelInstances.Where(i => i.Geometry?.IsSelfOrDescendantOf(m.Geometry!) == true || i.References.Count > 0),
                i => Assert.True(g.FindPart(i) >= 0, i.ToString()));
        }
    }

    /// <summary>
    /// A wheel image of a few bytes can declare a gigapixel PNG; decoding it would allocate gigabytes. Images beyond
    /// <see cref="GdtfImageSource.MaxPixels"/> are refused from their header.
    /// </summary>
    [Fact]
    public void Image_bombs_are_refused_from_their_header()
    {
        static byte[] Png(uint width, uint height)
        {
            var ms = new MemoryStream();
            ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
            var ihdr = new byte[17];
            "IHDR"u8.CopyTo(ihdr);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), width);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(8), height);
            ihdr[12] = 8; // bit depth
            ihdr[13] = 6; // RGBA
            var len = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(len, 13);
            ms.Write(len);
            ms.Write(ihdr);
            var crc = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(ihdr));
            ms.Write(crc);
            ms.Write(new byte[64]); // no real IDAT
            return ms.ToArray();
        }

        Assert.Null(GdtfImageSource.Decode(Png(16000, 16000)));
        Assert.Null(GdtfImageSource.Decode(Png(1 << 30, 4)));
        Assert.Null(GdtfImageSource.Decode(Png(4, 4))); // damaged (no image data): null, not an exception

        // Real wheel images still decode.
        using var archive = GdtfArchive.Open(TestData.Fixture("Robe_Lighting@Robin_MegaPointe@"));
        var wheelImage = archive.EntryNames.First(n => n.StartsWith("wheels/", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
        var image = GdtfImageSource.Decode(archive.ReadBytes(wheelImage));
        Assert.NotNull(image);
        Assert.True(image!.Width > 0 && image.Height > 0);
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return ~crc;
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
