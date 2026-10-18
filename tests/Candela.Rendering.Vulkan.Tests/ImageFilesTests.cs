using System.Buffers.Binary;
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
