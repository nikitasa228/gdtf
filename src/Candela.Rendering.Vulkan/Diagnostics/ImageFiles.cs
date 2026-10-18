using Candela.Core.Geometry;
using StbImageWriteSharp;

namespace Candela.Rendering.Vulkan;

/// <summary>Writes rendered frames to image files (CLI, tests, thumbnails).</summary>
public static class ImageFiles
{
    /// <summary>Writes an RGBA8 image as PNG (top row first, sRGB-encoded as read back by <see cref="IRenderer.ReadPixels"/>).</summary>
    /// <param name="image">The image (<see cref="PixelFormat.Rgba8"/>).</param>
    /// <param name="path">Output file; the directory is created when missing.</param>
    public static void WritePng(ImageData image, string path)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (image.Format != PixelFormat.Rgba8)
            throw new ArgumentException("Only RGBA8 images can be written as PNG.", nameof(image));
        if (image.Pixels.Length < image.Width * image.Height * 4)
            throw new ArgumentException("The image has fewer pixels than its size.", nameof(image));
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        using var stream = File.Create(path);
        new ImageWriter().WritePng(image.Pixels, image.Width, image.Height, ColorComponents.RedGreenBlueAlpha, stream);
    }
}
