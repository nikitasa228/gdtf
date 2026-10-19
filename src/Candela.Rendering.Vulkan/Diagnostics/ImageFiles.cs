using Candela.Core.Geometry;
using StbImageWriteSharp;

namespace Candela.Rendering.Vulkan;

/// <summary>Writes rendered frames to image files (CLI, tests, thumbnails).</summary>
public static class ImageFiles
{
    /// <summary>
    /// Writes an RGBA8 image as PNG (top row first, sRGB-encoded as read back by <see cref="IRenderer.ReadPixels"/>).
    /// When every pixel is opaque (alpha 255, as for rendered frames) the file is RGB (PNG colour type 2), a quarter
    /// smaller; otherwise it is RGBA (colour type 6). Readers that ask for RGBA get alpha 255 from an RGB file, so both
    /// read back to the same pixels.
    /// </summary>
    /// <param name="image">The image (<see cref="PixelFormat.Rgba8"/>).</param>
    /// <param name="path">Output file; the directory is created when missing.</param>
    public static void WritePng(ImageData image, string path)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (image.Format != PixelFormat.Rgba8)
            throw new ArgumentException("Only RGBA8 images can be written as PNG.", nameof(image));
        if (image.Width <= 0 || image.Height <= 0)
            throw new ArgumentException("The image has no pixels.", nameof(image));
        long pixelCount = (long)image.Width * image.Height;
        if (pixelCount > int.MaxValue / 4 || image.Pixels.Length < pixelCount * 4)
            throw new ArgumentException("The image has fewer pixels than its size.", nameof(image));
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        int count = (int)pixelCount;
        var rgba = image.Pixels.AsSpan(0, count * 4);
        using var stream = File.Create(path);
        if (IsOpaque(rgba))
        {
            var rgb = new byte[count * 3];
            for (int i = 0, o = 0; i < rgba.Length; i += 4, o += 3)
            {
                rgb[o] = rgba[i];
                rgb[o + 1] = rgba[i + 1];
                rgb[o + 2] = rgba[i + 2];
            }

            new ImageWriter().WritePng(rgb, image.Width, image.Height, ColorComponents.RedGreenBlue, stream);
        }
        else
        {
            new ImageWriter().WritePng(image.Pixels, image.Width, image.Height, ColorComponents.RedGreenBlueAlpha, stream);
        }
    }

    /// <summary>True when every alpha byte of tightly packed RGBA8 pixels is 255.</summary>
    internal static bool IsOpaque(ReadOnlySpan<byte> rgba)
    {
        for (int i = 3; i < rgba.Length; i += 4)
        {
            if (rgba[i] != 255)
                return false;
        }

        return true;
    }
}
