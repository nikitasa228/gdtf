using System.Numerics;
using Candela.Core.Geometry;

namespace Candela.Rendering.Vulkan.Tests;

/// <summary>The OpenEXR writer and reader: round trips for every compression and sample type, layers, damaged input.</summary>
public sealed class OpenExrTests
{
    private static float[] Pattern(int width, int height, int seed)
    {
        var random = new Random(seed);
        var values = new float[width * height];
        for (int i = 0; i < values.Length; i++)
            values[i] = (float)(Math.Exp(random.NextDouble() * 20 - 8) * (i % 7 == 0 ? 0 : 1)); // HDR values over decades, some zeros
        return values;
    }

    [Theory]
    [InlineData(ExrCompression.None, ExrPixelType.Float)]
    [InlineData(ExrCompression.None, ExrPixelType.Half)]
    [InlineData(ExrCompression.ZipSingleLine, ExrPixelType.Half)]
    [InlineData(ExrCompression.Zip, ExrPixelType.Half)]
    [InlineData(ExrCompression.Zip, ExrPixelType.Float)]
    [InlineData(ExrCompression.Zip, ExrPixelType.UInt)]
    public void RoundTripKeepsEverySample(ExrCompression compression, ExrPixelType type)
    {
        const int width = 37, height = 21; // not a multiple of the 16-line ZIP blocks
        var channels = new[]
        {
            new ExrChannel("R", Pattern(width, height, 1), type),
            new ExrChannel("G", Pattern(width, height, 2), type),
            new ExrChannel("B", Pattern(width, height, 3), type),
            new ExrChannel("raw.R", Pattern(width, height, 4), type),
            new ExrChannel("a.very.long.layer.name.that.needs.the.long.names.flag.R", Pattern(width, height, 5), type),
        };
        using var stream = new MemoryStream();
        OpenExr.Write(stream, width, height, channels, compression);
        stream.Position = 0;
        var image = OpenExr.Read(stream);
        Assert.Equal(width, image.Width);
        Assert.Equal(height, image.Height);
        Assert.Equal(compression, image.Compression);
        Assert.Equal(channels.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal), image.Channels.Select(c => c.Name));
        foreach (var expected in channels)
        {
            var actual = image.Channel(expected.Name)!;
            Assert.Equal(type, actual.Type);
            for (int i = 0; i < expected.Values.Length; i++)
            {
                float e = type switch
                {
                    ExrPixelType.Half => (float)(Half)Math.Clamp(expected.Values[i], -65504f, 65504f), // (no infinities)
                    ExrPixelType.UInt => (float)(uint)Math.Clamp(expected.Values[i], 0f, uint.MaxValue),
                    _ => expected.Values[i],
                };
                Assert.Equal(e, actual.Values[i]);
            }
        }
    }

    /// <summary>
    /// A half channel cannot hold more than 65504: brighter values (a lens at cd/m² × exposure) keep the largest finite
    /// half instead of becoming infinite, and NaN becomes 0, so a reader never meets inf or NaN.
    /// </summary>
    [Fact]
    public void HalfChannelsHoldNoInfinitiesOrNaN()
    {
        var values = new[] { 1e6f, -1e6f, float.PositiveInfinity, float.NegativeInfinity, float.NaN, 65504f, 70000f, 1.5f };
        using var stream = new MemoryStream();
        OpenExr.Write(stream, values.Length, 1, new[] { new ExrChannel("Y", values, ExrPixelType.Half) }, ExrCompression.None);
        stream.Position = 0;
        var read = OpenExr.Read(stream).Channel("Y")!.Values;
        Assert.Equal(new[] { 65504f, -65504f, 65504f, -65504f, 0f, 65504f, 65504f, 1.5f }, read);
    }

    [Fact]
    public void ZipCompressesSmoothImages()
    {
        const int width = 256, height = 64;
        var smooth = new float[width * height];
        for (int i = 0; i < smooth.Length; i++)
            smooth[i] = 0.25f + (i % width) / (float)width;
        using var none = new MemoryStream();
        using var zip = new MemoryStream();
        OpenExr.Write(none, width, height, new[] { new ExrChannel("Y", smooth) }, ExrCompression.None);
        OpenExr.Write(zip, width, height, new[] { new ExrChannel("Y", smooth) }, ExrCompression.Zip);
        Assert.True(zip.Length < none.Length / 4, $"zip {zip.Length} B, uncompressed {none.Length} B");
    }

    [Fact]
    public void RgbaImagesAreWrittenAsChannelsWithLayers()
    {
        const int width = 4, height = 3;
        var floats = new float[width * height * 4];
        for (int i = 0; i < floats.Length; i++)
            floats[i] = i * 0.5f;
        var bytes = new byte[floats.Length * 4];
        Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
        var hdr = new ImageData { Width = width, Height = height, Format = PixelFormat.RgbaFloat, Pixels = bytes };
        string path = Path.Combine(TestRenderers.OutputDirectory, "exr-rgba-layers.exr");
        OpenExr.WriteRgba(path, hdr, ExrPixelType.Float, ExrCompression.Zip, OpenExr.SplitRgba(hdr, "raw."));
        var image = OpenExr.Read(path);
        Assert.Equal(new[] { "A", "B", "G", "R", "raw.B", "raw.G", "raw.R" }, image.Channels.Select(c => c.Name));
        Assert.Equal(floats[4 * 5 + 1], image.Channel("G")!.Values[5]);
        Assert.Equal(floats[4 * 7 + 2], image.Channel("raw.B")!.Values[7]);
        Assert.Equal(floats[4 * 11 + 3], image.Channel("A")!.Values[11]);
    }

    [Fact]
    public void ChromaticitiesAndWhiteLuminanceRoundTrip()
    {
        var colorimetry = ExrColorimetry.Rec2020 with { WhiteLuminance = 312.5f };
        string path = Path.Combine(TestRenderers.OutputDirectory, "exr-colorimetry.exr");
        OpenExr.Write(path, 3, 2, new[] { new ExrChannel("Y", Pattern(3, 2, 11), ExrPixelType.Float) }, ExrCompression.None, colorimetry);
        var image = OpenExr.Read(path);
        Assert.Equal(colorimetry, image.Colorimetry);
        Assert.Equal(new Vector2(0.708f, 0.292f), image.Colorimetry!.Red);

        // Without the attributes nothing is stated (readers assume Rec.709).
        using var plain = new MemoryStream();
        OpenExr.Write(plain, 3, 2, new[] { new ExrChannel("Y", Pattern(3, 2, 12)) });
        plain.Position = 0;
        Assert.Null(OpenExr.Read(plain).Colorimetry);
        Assert.Throws<ArgumentException>(() => OpenExr.Write(new MemoryStream(), 2, 2, new[] { new ExrChannel("Y", new float[4]) }, ExrCompression.Zip,
            ExrColorimetry.Rec709 with { WhiteLuminance = -1f }));
    }

    [Fact]
    public void DamagedFilesAreRejectedWithInvalidData()
    {
        using var stream = new MemoryStream();
        OpenExr.Write(stream, 8, 8, new[] { new ExrChannel("Y", Pattern(8, 8, 9)) }, ExrCompression.Zip);
        var good = stream.ToArray();
        Assert.Throws<InvalidDataException>(() => OpenExr.Read(new MemoryStream(new byte[] { 1, 2, 3 })));
        Assert.Throws<InvalidDataException>(() => OpenExr.Read(new MemoryStream(good.AsSpan(0, good.Length / 2).ToArray())));
        // One chunk of 8 lines stored raw (random data does not compress): the offset table entry sits right before
        // the chunk (8 bytes y + size, 128 bytes of half floats); pointing it past the end of the file is detected.
        int tableAt = good.Length - 128 - 8 - 8;
        var corrupt = (byte[])good.Clone();
        BitConverter.GetBytes((ulong)good.Length + 100).CopyTo(corrupt, tableAt);
        Assert.Throws<InvalidDataException>(() => OpenExr.Read(new MemoryStream(corrupt)));
        var badSize = (byte[])good.Clone();
        BitConverter.GetBytes(-5).CopyTo(badSize, tableAt + 8 + 4);
        Assert.Throws<InvalidDataException>(() => OpenExr.Read(new MemoryStream(badSize)));
        var tiled = (byte[])good.Clone();
        tiled[5] |= 0x02; // version flags: tiled
        Assert.Throws<InvalidDataException>(() => OpenExr.Read(new MemoryStream(tiled)));
    }

    [Fact]
    public void InvalidChannelsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => OpenExr.Write(new MemoryStream(), 2, 2, Array.Empty<ExrChannel>()));
        Assert.Throws<ArgumentException>(() => OpenExr.Write(new MemoryStream(), 2, 2, new[] { new ExrChannel("Y", new float[3]) }));
        Assert.Throws<ArgumentException>(() =>
            OpenExr.Write(new MemoryStream(), 2, 2, new[] { new ExrChannel("Y", new float[4]), new ExrChannel("Y", new float[4]) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => OpenExr.Write(new MemoryStream(), 0, 2, new[] { new ExrChannel("Y", new float[4]) }));
    }
}
