using System.Buffers.Binary;
using System.Text;
using Candela.Core.Geometry;
using StbImageWriteSharp;

namespace Candela.Assets.Tests;

public class ImageImporterTests
{
    /// <summary>A 4 × 2 image: red, green, blue, white on top; black, grey, yellow, half-transparent cyan below.</summary>
    private static ImageData Pattern() => TestFiles.Image(4, 2, (x, y) => (y, x) switch
    {
        (0, 0) => (255, 0, 0, 255),
        (0, 1) => (0, 255, 0, 255),
        (0, 2) => (0, 0, 255, 255),
        (0, 3) => (255, 255, 255, 255),
        (1, 0) => (0, 0, 0, 255),
        (1, 1) => (128, 128, 128, 255),
        (1, 2) => (255, 255, 0, 255),
        _ => (0, 255, 255, 128),
    });

    private static (byte R, byte G, byte B, byte A) Pixel(ImageData image, int x, int y)
    {
        int i = (y * image.Width + x) * 4;
        return (image.Pixels[i], image.Pixels[i + 1], image.Pixels[i + 2], image.Pixels[i + 3]);
    }

    private static float PixelFloat(ImageData image, int x, int y, int channel) =>
        BinaryPrimitives.ReadSingleLittleEndian(image.Pixels.AsSpan(((y * image.Width + x) * 4 + channel) * 4, 4));

    private static byte[] Write(Action<ImageWriter, Stream> write)
    {
        using var ms = new MemoryStream();
        write(new ImageWriter(), ms);
        return ms.ToArray();
    }

    [Fact]
    public void PngRoundTripIsLossless()
    {
        var source = Pattern();

        var png = ImageImporter.EncodePng(source);
        var decoded = ImageImporter.Decode(png);

        Assert.Equal("png", ImageImporter.DetectFormat(png));
        Assert.Equal(new ImageInfo(4, 2, false, "png"), ImageImporter.Identify(png));
        Assert.Equal(PixelFormat.Rgba8, decoded.Format);
        Assert.Equal(4, decoded.Width);
        Assert.Equal(2, decoded.Height);
        Assert.Equal(source.Pixels, decoded.Pixels);
    }

    [Fact]
    public void JpegDecodesApproximately()
    {
        var source = TestFiles.Image(16, 16, (x, _) => x < 8 ? ((byte)200, (byte)40, (byte)40, (byte)255) : ((byte)40, (byte)40, (byte)200, (byte)255));
        var jpg = Write((w, s) => w.WriteJpg(source.Pixels, 16, 16, ColorComponents.RedGreenBlueAlpha, s, 95));

        var decoded = ImageImporter.Decode(jpg);

        Assert.Equal("jpeg", ImageImporter.DetectFormat(jpg));
        Assert.Equal(16, decoded.Width);
        Assert.Equal(16, decoded.Height);
        var left = Pixel(decoded, 2, 8);
        var right = Pixel(decoded, 13, 8);
        Assert.InRange(left.R, 180, 220);
        Assert.InRange(left.B, 20, 70);
        Assert.InRange(right.B, 180, 220);
        Assert.Equal(255, left.A);
    }

    /// <summary>Inserts an APP1/Exif segment with the given orientation right after the SOI marker.</summary>
    private static byte[] WithExifOrientation(byte[] jpeg, ushort orientation, bool bigEndian)
    {
        var tiff = new byte[26];
        if (bigEndian)
        {
            "MM"u8.CopyTo(tiff);
            BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(2), 42);
            BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(4), 8);
            BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(8), 1);
            BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(10), 0x0112);
            BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(12), 3);
            BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(14), 1);
            BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(18), orientation);
        }
        else
        {
            "II"u8.CopyTo(tiff);
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(2), 42);
            BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), 8);
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(8), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(10), 0x0112);
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(12), 3);
            BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(14), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(18), orientation);
        }
        var payload = "Exif\0\0"u8.ToArray().Concat(tiff).ToArray();
        var segment = new byte[4 + payload.Length];
        segment[0] = 0xFF;
        segment[1] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)(payload.Length + 2));
        payload.CopyTo(segment, 4);
        return jpeg[..2].Concat(segment).Concat(jpeg[2..]).ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JpegExifOrientationIsApplied(bool bigEndian)
    {
        // 32 × 16 landscape photo, left half red. Orientation 6 ("rotate 90° clockwise to display") makes it 16 × 32 with
        // the red half on top.
        var source = TestFiles.Image(32, 16, (x, _) => x < 16 ? ((byte)220, (byte)20, (byte)20, (byte)255) : ((byte)20, (byte)20, (byte)220, (byte)255));
        var plain = Write((w, s) => w.WriteJpg(source.Pixels, 32, 16, ColorComponents.RedGreenBlueAlpha, s, 95));
        var rotated = WithExifOrientation(plain, 6, bigEndian);

        Assert.Equal(1, ExifOrientation.Read(plain));
        Assert.Equal(6, ExifOrientation.Read(rotated));
        var upright = ImageImporter.Decode(rotated);
        var raw = ImageImporter.Decode(rotated, applyExifOrientation: false);

        Assert.Equal((16, 32), (upright.Width, upright.Height));
        Assert.Equal((32, 16), (raw.Width, raw.Height));
        Assert.True(Pixel(upright, 8, 4).R > 150, "top of the upright image is red");
        Assert.True(Pixel(upright, 8, 28).B > 150, "bottom of the upright image is blue");
    }

    [Theory]
    [InlineData(1, 3, 2, 0)]
    [InlineData(2, 3, 2, 2)]
    [InlineData(3, 3, 2, 5)]
    [InlineData(4, 3, 2, 3)]
    [InlineData(5, 2, 3, 0)]
    [InlineData(6, 2, 3, 3)]
    [InlineData(7, 2, 3, 5)]
    [InlineData(8, 2, 3, 2)]
    public void ExifOrientationsMapPixelsLikeTheSpecification(int orientation, int width, int height, int expectedTopLeft)
    {
        // A 3 × 2 image whose pixel values are their index (0 1 2 / 3 4 5) in the red channel.
        var image = TestFiles.Image(3, 2, (x, y) => ((byte)(y * 3 + x), 0, 0, 255));

        var result = ExifOrientation.Apply(image, orientation);

        Assert.Equal((width, height), (result.Width, result.Height));
        Assert.Equal(expectedTopLeft, Pixel(result, 0, 0).R);
        // Every source pixel appears exactly once.
        Assert.Equal(Enumerable.Range(0, 6), Enumerable.Range(0, 6).Select(i => (int)result.Pixels[i * 4]).Order());
    }

    [Fact]
    public void TgaAndBmpDecodeExactly()
    {
        var source = Pattern();
        var tga = Write((w, s) => w.WriteTga(source.Pixels, 4, 2, ColorComponents.RedGreenBlueAlpha, s));
        var bmp = Write((w, s) => w.WriteBmp(source.Pixels, 4, 2, ColorComponents.RedGreenBlueAlpha, s));

        var fromTga = ImageImporter.Decode(tga);
        var fromBmp = ImageImporter.Decode(bmp);

        Assert.Equal("tga", ImageImporter.DetectFormat(tga));
        Assert.Equal("bmp", ImageImporter.DetectFormat(bmp));
        Assert.Equal(source.Pixels, fromTga.Pixels);
        // stb writes 24-bit BMPs: colour exact, alpha opaque.
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 4; x++)
            {
                var e = Pixel(source, x, y);
                var a = Pixel(fromBmp, x, y);
                Assert.Equal((e.R, e.G, e.B), (a.R, a.G, a.B));
            }
    }

    /// <summary>
    /// Uncompressed Radiance HDR (RGBE) from linear RGB floats, written by hand: stb's writer only accepts 8-bit input.
    /// </summary>
    private static byte[] WriteHdr(float[] rgb, int width, int height)
    {
        var data = new List<byte>(Encoding.ASCII.GetBytes($"#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y {height} +X {width}\n"));
        for (int i = 0; i < width * height; i++)
        {
            float r = rgb[3 * i], g = rgb[3 * i + 1], b = rgb[3 * i + 2];
            float max = MathF.Max(r, MathF.Max(g, b));
            if (max < 1e-32f)
            {
                data.AddRange(new byte[4]);
                continue;
            }
            int exponent = (int)MathF.Floor(MathF.Log2(max)) + 1; // max = m · 2^exponent, m in [0.5, 1)
            float scale = 256f / MathF.Pow(2f, exponent);
            data.AddRange(new[] { (byte)(r * scale), (byte)(g * scale), (byte)(b * scale), (byte)(exponent + 128) });
        }
        return data.ToArray();
    }

    [Fact]
    public void HdrDecodesToLinearFloat()
    {
        // 2 × 1: a bright 4.0 white and a dim 0.25 red.
        var hdr = WriteHdr(new float[] { 4f, 4f, 4f, 0.25f, 0f, 0f }, 2, 1);

        var image = ImageImporter.Decode(hdr);

        Assert.Equal("hdr", ImageImporter.DetectFormat(hdr));
        Assert.True(ImageImporter.Identify(hdr)!.Value.IsHdr);
        Assert.Equal(PixelFormat.RgbaFloat, image.Format);
        Assert.Equal(2 * 16, image.Pixels.Length);
        Assert.Equal(4f, PixelFloat(image, 0, 0, 0), 2);
        Assert.Equal(4f, PixelFloat(image, 0, 0, 2), 2);
        Assert.Equal(0.25f, PixelFloat(image, 1, 0, 0), 2);
        Assert.Equal(0f, PixelFloat(image, 1, 0, 1), 3);
        Assert.Equal(1f, PixelFloat(image, 1, 0, 3), 3);

        // PNG output of a float image: clamped and sRGB-encoded.
        var rgba = ImageImporter.ToRgba8(image);
        Assert.Equal(255, rgba[0]);
        Assert.Equal(ColorEncoding.LinearToSrgbByte(0.25f), rgba[4]);
        Assert.Equal(137, rgba[4]);
    }

    [Fact]
    public void GifFirstFrameDecodes()
    {
        // Two-frame GIF89a (2 × 2, global palette black/red/green/blue): frame 1 red, frame 2 green.
        var gif = new List<byte>();
        gif.AddRange("GIF89a"u8.ToArray());
        gif.AddRange(new byte[] { 2, 0, 2, 0, 0b1000_0001, 0, 0 }); // 2 × 2, global colour table of 4 entries
        gif.AddRange(new byte[] { 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255 });
        void Frame(byte colour)
        {
            gif.AddRange(new byte[] { 0x21, 0xF9, 4, 0, 10, 0, 0, 0 }); // graphic control: 0.1 s
            gif.AddRange(new byte[] { 0x2C, 0, 0, 0, 0, 2, 0, 2, 0, 0 }); // image descriptor
            // LZW, minimum code size 2: four literal pixels of `colour`, with a clear code (4) before every pair so the
            // code width stays at 3 bits, then end (5); codes packed LSB first.
            int[] codes = { 4, colour, colour, 4, colour, colour, 5 };
            var bits = new List<byte>();
            int acc = 0, n = 0;
            foreach (var c in codes)
            {
                acc |= c << n;
                n += 3;
                while (n >= 8)
                {
                    bits.Add((byte)(acc & 0xFF));
                    acc >>= 8;
                    n -= 8;
                }
            }
            if (n > 0) bits.Add((byte)acc);
            gif.Add(2);
            gif.Add((byte)bits.Count);
            gif.AddRange(bits);
            gif.Add(0);
        }
        Frame(1);
        Frame(2);
        gif.Add(0x3B);

        var image = ImageImporter.Decode(gif.ToArray());

        Assert.Equal("gif", ImageImporter.DetectFormat(gif.ToArray()));
        Assert.Equal((2, 2), (image.Width, image.Height));
        Assert.All(Enumerable.Range(0, 4), i => Assert.Equal((255, 0, 0), (image.Pixels[i * 4], image.Pixels[i * 4 + 1], image.Pixels[i * 4 + 2])));
    }

    [Fact]
    public void FlatPsdDecodes()
    {
        // Minimal PSD: 3 channels, 2 × 1, 8 bit RGB, raw composite (planar R, G, B).
        var psd = new List<byte>();
        psd.AddRange("8BPS"u8.ToArray());
        psd.AddRange(new byte[] { 0, 1, 0, 0, 0, 0, 0, 0, 0, 3 }); // version 1, reserved, 3 channels
        psd.AddRange(new byte[] { 0, 0, 0, 1, 0, 0, 0, 2, 0, 8, 0, 3 }); // height 1, width 2, depth 8, RGB
        psd.AddRange(new byte[12]); // empty colour mode data, image resources, layer and mask info
        psd.AddRange(new byte[] { 0, 0 }); // raw data
        psd.AddRange(new byte[] { 255, 10, 0, 20, 0, 30 }); // R plane, G plane, B plane

        var image = ImageImporter.Decode(psd.ToArray());

        Assert.Equal("psd", ImageImporter.DetectFormat(psd.ToArray()));
        Assert.Equal((2, 1), (image.Width, image.Height));
        Assert.Equal((255, 0, 0, 255), Pixel(image, 0, 0));
        Assert.Equal((10, 20, 30, 255), Pixel(image, 1, 0));
    }

    [Theory]
    [InlineData(new byte[] { 0x49, 0x49, 0x2A, 0x00, 1, 2, 3, 4 }, "TIFF")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50, 0, 0 }, "WebP")]
    [InlineData(new byte[] { 0x44, 0x44, 0x53, 0x20, 0, 0, 0, 0 }, "DDS")]
    [InlineData(new byte[] { 0x76, 0x2F, 0x31, 0x01, 0, 0, 0, 0 }, "OpenEXR")]
    [InlineData(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, "Unsupported")]
    public void UnsupportedFormatsAreExplained(byte[] data, string fragment)
    {
        Assert.Null(ImageImporter.Identify(data));
        var e = Assert.Throws<AssetImportException>(() => ImageImporter.Decode(data));
        Assert.Contains(fragment, e.Message, StringComparison.Ordinal);
        Assert.False(ImageImporter.TryDecode(data, out var image, out var error));
        Assert.Null(image);
        Assert.Contains(fragment, error, StringComparison.Ordinal);
    }

    [Fact]
    public void TruncatedAndCorruptImagesFailCleanly()
    {
        var png = ImageImporter.EncodePng(TestFiles.Image(64, 64, (x, y) => ((byte)x, (byte)y, 0, 255)));
        var truncated = png[..(png.Length / 2)];
        var corrupt = (byte[])png.Clone();
        for (int i = 40; i < corrupt.Length - 20; i++) corrupt[i] ^= 0x5A;

        Assert.False(ImageImporter.TryDecode(truncated, out _, out var e1));
        Assert.False(ImageImporter.TryDecode(corrupt, out _, out var e2));
        Assert.False(string.IsNullOrEmpty(e1));
        Assert.False(string.IsNullOrEmpty(e2));
        Assert.False(ImageImporter.TryDecode(Array.Empty<byte>(), out _, out _));
    }

    [Fact]
    public void OversizedHeaderIsRejectedBeforeDecoding()
    {
        // A PNG header claiming 40 000 × 10 pixels (wider than MaxDimension, small enough for stb's own limits).
        var png = ImageImporter.EncodePng(TestFiles.Image(1, 1, (_, _) => (0, 0, 0, 255)));
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), 40_000);
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), 10);

        var e = Assert.Throws<AssetImportException>(() => ImageImporter.Decode(png));
        Assert.Contains("too large", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SavePngWritesAtomicallyAndLoadReadsBack()
    {
        using var dir = new TempDir();
        var path = dir.File("out/render.png");
        var image = Pattern();

        ImageImporter.SavePng(image, path);
        ImageImporter.SavePng(image, path); // overwrite

        Assert.Equal(image.Pixels, ImageImporter.Load(path).Pixels);
        Assert.Single(Directory.GetFiles(dir.File("out")));
        Assert.Throws<AssetImportException>(() => ImageImporter.Load(dir.File("missing.png")));
    }

    [Fact]
    public void ImageFormatsAreRecognisedByExtension()
    {
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".psd", ".gif", ".hdr" })
        {
            Assert.True(AssetFormats.IsImageFile("picture" + ext.ToUpperInvariant()));
            Assert.Equal(AssetKind.Image, AssetFormats.GuessKind("picture" + ext));
        }
        Assert.False(AssetFormats.IsImageFile("picture.tif"));
        Assert.Null(AssetFormats.GuessKind("notes.txt"));
        Assert.Equal(AssetKind.Gdtf, AssetFormats.GuessKind("fixture.GDTF"));
    }

    [Fact]
    public void ColorEncodingRoundTrips()
    {
        for (int i = 0; i < 256; i++)
            Assert.Equal(i, ColorEncoding.LinearToSrgbByte(ColorEncoding.SrgbByteToLinear((byte)i)));
        Assert.Equal(0.2140f, ColorEncoding.SrgbToLinear(0.5f), 3);
        Assert.Equal(0.5f, ColorEncoding.LinearToSrgb(0.2140f), 3);
        Assert.Equal(0f, ColorEncoding.LinearToSrgb(float.NaN));
    }

    [Fact]
    public void TextDataIsNotMistakenForTga()
    {
        Assert.Null(ImageImporter.DetectFormat(Encoding.ASCII.GetBytes("solid box\nfacet normal 0 0 1\n")));
    }

    /// <summary>Decodes on a worker thread so a regression (endless loop) fails the test instead of hanging the run.</summary>
    private static bool DecodesOrRejectsQuickly(byte[] data)
    {
        var task = Task.Run(() => ImageImporter.TryDecode(data, out _, out _));
        return task.Wait(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void TruncatedJpegsAreRejectedInsteadOfHanging()
    {
        // StbImageSharp loops forever when a marker segment runs past the end of the data (a partial download).
        var source = TestFiles.Image(16, 12, (x, y) => ((byte)(x * 16), (byte)(y * 20), (byte)(x ^ y), 255));
        var jpg = Write((w, s) => w.WriteJpg(source.Pixels, 16, 12, ColorComponents.RedGreenBlueAlpha, s, 90));
        for (int length = 0; length < jpg.Length; length++)
            Assert.True(DecodesOrRejectsQuickly(jpg[..length]), $"decoding the first {length} bytes did not finish");

        // A header segment whose length points past the end: rejected up front.
        var bad = (byte[])jpg.Clone();
        bad[4] = 0xF3; // APP0 length high byte
        Assert.Null(ImageImporter.Identify(bad));
        var e = Assert.Throws<AssetImportException>(() => ImageImporter.Decode(bad));
        Assert.Contains("JPEG", e.Message, StringComparison.Ordinal);

        // Truncated entropy-coded data still decodes (as far as it goes), as does a file without its end marker.
        Assert.NotNull(ImageImporter.Identify(jpg[..(jpg.Length - 40)]));
        Assert.True(ImageImporter.TryDecode(jpg[..^2], out var image, out _));
        Assert.Equal(16, image!.Width);
    }

    [Fact]
    public void CorruptedImagesOfEveryFormatDecodeOrRejectQuickly()
    {
        var source = TestFiles.Image(16, 12, (x, y) => ((byte)(x * 16), (byte)(y * 20), (byte)(x ^ y), 255));
        var seeds = new[]
        {
            ImageImporter.EncodePng(source),
            Write((w, s) => w.WriteJpg(source.Pixels, 16, 12, ColorComponents.RedGreenBlueAlpha, s, 90)),
            Write((w, s) => w.WriteTga(source.Pixels, 16, 12, ColorComponents.RedGreenBlueAlpha, s)),
            Write((w, s) => w.WriteBmp(source.Pixels, 16, 12, ColorComponents.RedGreenBlueAlpha, s)),
            WriteHdr(Enumerable.Range(0, 16 * 12 * 3).Select(i => i * 0.01f).ToArray(), 16, 12),
        };
        var rng = new Random(11);
        foreach (var seed in seeds)
        {
            for (int k = 0; k < 150; k++)
            {
                var copy = (byte[])seed.Clone();
                for (int f = 0; f < 1 + rng.Next(8); f++) copy[rng.Next(copy.Length)] = (byte)rng.Next(256);
                Assert.True(DecodesOrRejectsQuickly(copy), $"a corrupted {ImageImporter.DetectFormat(seed)} did not finish decoding");
            }
        }
    }
}
