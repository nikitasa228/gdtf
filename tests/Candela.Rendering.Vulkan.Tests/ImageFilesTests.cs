using System.Buffers.Binary;
using System.IO.Compression;
using Candela.Core.Geometry;
using StbImageSharp;

namespace Candela.Rendering.Vulkan.Tests;

/// <summary>
/// <see cref="ImageFiles.WritePng"/>: opaque images are stored as RGB (colour type 2), images with any alpha below 255 as
/// RGBA (colour type 6), and both read back to the same RGBA pixels. Pure CPU, no Vulkan device needed.
/// </summary>
public sealed class ImageFilesTests : IDisposable
{
    private const int ColourTypeRgb = 2;
    private const int ColourTypeRgba = 6;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "candela-imagefiles-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Opaque_images_are_written_as_rgb_and_read_back_identically()
    {
        var image = Noise(37, 23, seed: 1, alpha: _ => 255);
        var path = Write(image, "opaque.png");

        var header = ReadHeader(path);
        Assert.Equal((37, 23, 8, ColourTypeRgb), header);

        var bytes = File.ReadAllBytes(path);
        var asStored = ImageResult.FromMemory(bytes);
        Assert.Equal(StbImageSharp.ColorComponents.RedGreenBlue, asStored.SourceComp);

        var rgba = ImageResult.FromMemory(bytes, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        Assert.Equal(image.Width, rgba.Width);
        Assert.Equal(image.Height, rgba.Height);
        Assert.Equal(image.Pixels, rgba.Data); // alpha comes back as 255
    }

    [Fact]
    public void One_translucent_pixel_keeps_the_alpha_channel()
    {
        var image = Noise(16, 9, seed: 2, alpha: i => i == 16 * 9 - 1 ? (byte)254 : (byte)255);
        var path = Write(image, "translucent.png");

        Assert.Equal((16, 9, 8, ColourTypeRgba), ReadHeader(path));
        var rgba = ImageResult.FromMemory(File.ReadAllBytes(path), StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        Assert.Equal(StbImageSharp.ColorComponents.RedGreenBlueAlpha, rgba.SourceComp);
        Assert.Equal(image.Pixels, rgba.Data);
    }

    [Fact]
    public void Transparent_images_round_trip_exactly()
    {
        var image = Noise(8, 8, seed: 3, alpha: i => (byte)(i * 4));
        var path = Write(image, "alpha.png");

        Assert.Equal((8, 8, 8, ColourTypeRgba), ReadHeader(path));
        Assert.Equal(image.Pixels, ImageResult.FromMemory(File.ReadAllBytes(path), StbImageSharp.ColorComponents.RedGreenBlueAlpha).Data);
    }

    [Fact]
    public void An_rgb_file_is_smaller_than_the_rgba_file_of_the_same_pixels()
    {
        // Incompressible noise: the RGB file holds three bytes per pixel instead of four.
        var opaque = Noise(128, 128, seed: 4, alpha: _ => 255);
        var translucent = Noise(128, 128, seed: 4, alpha: i => i == 0 ? (byte)254 : (byte)255);

        long rgb = new FileInfo(Write(opaque, "noise-rgb.png")).Length;
        long rgba = new FileInfo(Write(translucent, "noise-rgba.png")).Length;

        Assert.True(rgb < rgba * 0.8, $"RGB file {rgb} bytes, RGBA file {rgba} bytes.");
    }

    [Fact]
    public void Bytes_beyond_the_image_size_are_ignored()
    {
        var image = Noise(5, 4, seed: 5, alpha: _ => 255);
        var padded = new byte[image.Pixels.Length + 8];
        image.Pixels.CopyTo(padded, 0);
        padded[^1] = 0; // a "transparent" byte past the last pixel must not switch the file to RGBA
        var path = Write(new ImageData { Width = 5, Height = 4, Pixels = padded }, "padded.png");

        Assert.Equal((5, 4, 8, ColourTypeRgb), ReadHeader(path));
        Assert.Equal(image.Pixels, ImageResult.FromMemory(File.ReadAllBytes(path), StbImageSharp.ColorComponents.RedGreenBlueAlpha).Data);
    }

    [Fact]
    public void Missing_directories_are_created()
    {
        var path = Path.Combine(_directory, "a", "b", "c.png");
        ImageFiles.WritePng(Noise(2, 2, seed: 6, alpha: _ => 255), path);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Invalid_images_are_refused()
    {
        var path = Path.Combine(_directory, "bad.png");
        Assert.Throws<ArgumentNullException>(() => ImageFiles.WritePng(null!, path));
        Assert.ThrowsAny<ArgumentException>(() => ImageFiles.WritePng(Noise(2, 2, 7, _ => 255), ""));
        Assert.Throws<ArgumentException>(() => ImageFiles.WritePng(new ImageData { Width = 0, Height = 2 }, path));
        Assert.Throws<ArgumentException>(() => ImageFiles.WritePng(new ImageData { Width = 2, Height = 2, Pixels = new byte[15] }, path));
        Assert.Throws<ArgumentException>(() =>
            ImageFiles.WritePng(new ImageData { Width = 1, Height = 1, Format = PixelFormat.RgbaFloat, Pixels = new byte[16] }, path));
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(new byte[0], true)]
    [InlineData(new byte[] { 1, 2, 3, 255 }, true)]
    [InlineData(new byte[] { 1, 2, 3, 255, 4, 5, 6, 0 }, false)]
    [InlineData(new byte[] { 255, 255, 255, 254 }, false)]
    public void Opacity_is_decided_by_the_alpha_bytes_only(byte[] rgba, bool opaque) =>
        Assert.Equal(opaque, ImageFiles.IsOpaque(rgba));

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(1, 1, false)]
    [InlineData(37, 23, true)]
    [InlineData(64, 3, false)]
    [InlineData(3, 64, true)]
    public void Files_are_valid_png_for_a_strict_decoder(int width, int height, bool opaque)
    {
        // Independent of stb: every chunk CRC, the zlib stream (with its Adler-32) and the scanline filters are checked
        // by a minimal decoder written from the PNG specification, so a lenient reader cannot hide a broken file.
        var image = Noise(width, height, seed: width * 31 + height, alpha: i => opaque || i % 3 != 0 ? (byte)255 : (byte)(i * 7));
        var path = Write(image, $"strict-{width}x{height}-{opaque}.png");

        var (w, h, pixels, colourType) = StrictPng.Decode(File.ReadAllBytes(path));
        Assert.Equal((width, height), (w, h));
        Assert.Equal(opaque ? ColourTypeRgb : ColourTypeRgba, colourType);
        Assert.Equal(image.Pixels, pixels);
    }

    private string Write(ImageData image, string name)
    {
        var path = Path.Combine(_directory, name);
        ImageFiles.WritePng(image, path);
        return path;
    }

    private static ImageData Noise(int width, int height, int seed, Func<int, byte> alpha)
    {
        var random = new Random(seed);
        var pixels = new byte[width * height * 4];
        random.NextBytes(pixels);
        for (int i = 0; i < width * height; i++) pixels[i * 4 + 3] = alpha(i);
        return new ImageData { Width = width, Height = height, Pixels = pixels };
    }

    /// <summary>Width, height, bit depth and colour type from the IHDR chunk (the first chunk of every PNG).</summary>
    private static (int Width, int Height, int BitDepth, int ColourType) ReadHeader(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length > 33, "The file is too short to be a PNG.");
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }, bytes[..8]);
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(bytes, 12, 4));
        return (BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20)), bytes[24], bytes[25]);
    }
}

/// <summary>
/// A deliberately strict PNG decoder for 8-bit RGB / RGBA, non-interlaced files (what <see cref="ImageFiles.WritePng"/>
/// writes): checks the signature, chunk order and CRCs, IHDR fields, the zlib stream and every filter type.
/// </summary>
internal static class StrictPng
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static (int Width, int Height, byte[] Rgba, int ColourType) Decode(byte[] file)
    {
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, file[..8]);
        int pos = 8;
        int width = 0, height = 0, colourType = -1;
        var idat = new MemoryStream();
        bool sawHeader = false, sawEnd = false;
        while (pos < file.Length)
        {
            Assert.True(pos + 12 <= file.Length, "Truncated chunk.");
            int length = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(pos));
            Assert.InRange(length, 0, file.Length - pos - 12);
            string type = System.Text.Encoding.ASCII.GetString(file, pos + 4, 4);
            var data = file.AsSpan(pos + 8, length);
            uint crc = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(pos + 8 + length));
            Assert.Equal(Crc(file.AsSpan(pos + 4, length + 4)), crc);
            Assert.False(sawEnd, "Data after IEND.");
            switch (type)
            {
                case "IHDR":
                    Assert.False(sawHeader);
                    Assert.Equal(8, pos); // the first chunk
                    Assert.Equal(13, length);
                    width = BinaryPrimitives.ReadInt32BigEndian(data);
                    height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                    Assert.Equal(8, data[8]); // bit depth
                    colourType = data[9];
                    Assert.Contains(colourType, new[] { 2, 6 });
                    Assert.Equal(0, data[10]); // compression
                    Assert.Equal(0, data[11]); // filter method
                    Assert.Equal(0, data[12]); // no interlace
                    sawHeader = true;
                    break;
                case "IDAT":
                    Assert.True(sawHeader);
                    idat.Write(data);
                    break;
                case "IEND":
                    Assert.Equal(0, length);
                    sawEnd = true;
                    break;
                default:
                    Assert.True(char.IsLower(type[0]), $"Unknown critical chunk {type}.");
                    break;
            }
            pos += length + 12;
        }
        Assert.True(sawHeader && sawEnd, "IHDR or IEND missing.");

        int channels = colourType == 6 ? 4 : 3;
        int stride = width * channels;
        idat.Position = 0;
        var raw = new MemoryStream();
        using (var z = new ZLibStream(idat, CompressionMode.Decompress)) z.CopyTo(raw);
        var bytes = raw.ToArray();
        Assert.Equal((stride + 1) * height, bytes.Length);

        var image = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            int filter = bytes[y * (stride + 1)];
            Assert.InRange(filter, 0, 4);
            for (int x = 0; x < stride; x++)
            {
                int raw8 = bytes[y * (stride + 1) + 1 + x];
                int a = x >= channels ? image[y * stride + x - channels] : 0;
                int b = y > 0 ? image[(y - 1) * stride + x] : 0;
                int c = x >= channels && y > 0 ? image[(y - 1) * stride + x - channels] : 0;
                int predictor = filter switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    _ => Paeth(a, b, c),
                };
                image[y * stride + x] = (byte)(raw8 + predictor);
            }
        }

        var rgba = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            rgba[i * 4] = image[i * channels];
            rgba[i * 4 + 1] = image[i * channels + 1];
            rgba[i * 4 + 2] = image[i * channels + 2];
            rgba[i * 4 + 3] = channels == 4 ? image[i * channels + 3] : (byte)255;
        }
        return (width, height, rgba, colourType);
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = System.Math.Abs(p - a), pb = System.Math.Abs(p - b), pc = System.Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint c = 0xFFFFFFFFu;
        foreach (var b in bytes) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
