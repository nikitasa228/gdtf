using System.Runtime.InteropServices;
using Candela.Core.Geometry;
using StbImageSharp;
using StbImageWriteSharp;
using ReadComponents = StbImageSharp.ColorComponents;
using WriteComponents = StbImageWriteSharp.ColorComponents;

namespace Candela.Assets;

/// <summary>Dimensions and type of an encoded image, read from its header without decoding the pixels.</summary>
/// <param name="Width">Pixels per row.</param>
/// <param name="Height">Rows.</param>
/// <param name="IsHdr">True for Radiance .hdr (decoded to linear float RGBA).</param>
/// <param name="Format">Short container name ("png", "jpeg", "tga", "bmp", "psd", "gif", "hdr").</param>
public readonly record struct ImageInfo(int Width, int Height, bool IsHdr, string Format);

/// <summary>
/// Decodes PNG, JPEG (baseline and progressive, EXIF orientation applied), TGA, BMP, PSD (flattened composite), GIF
/// (first frame) and Radiance HDR into <see cref="ImageData"/> (top row first), and writes PNG for thumbnails and renders.
/// 8-bit formats decode to <see cref="PixelFormat.Rgba8"/> exactly as stored (sRGB-encoded colour or linear data such
/// as normal maps — the meaning depends on use); HDR decodes to linear <see cref="PixelFormat.RgbaFloat"/>.
/// All methods are thread-safe.
/// </summary>
public static class ImageImporter
{
    /// <summary>Largest accepted image (pixels per side); protects against corrupt or hostile headers.</summary>
    public const int MaxDimension = 32768;

    /// <summary>Largest accepted pixel count (256 megapixels, 1 GiB as RGBA8).</summary>
    public const long MaxPixels = 256L * 1024 * 1024;

    /// <summary>Identifies an image from its first bytes; null when the format is not recognised or the header is invalid.</summary>
    public static unsafe ImageInfo? Identify(ReadOnlySpan<byte> data)
    {
        string? format = DetectFormat(data);
        if (format is null) return null;
        try
        {
            fixed (byte* p = data)
            {
                // Header parsing only; the stream wraps the caller's memory without copying it.
                using var stream = new UnmanagedMemoryStream(p, data.Length);
                var info = StbImageSharp.ImageInfo.FromStream(stream);
                if (info is null || info.Value.Width <= 0 || info.Value.Height <= 0) return null;
                return new ImageInfo(info.Value.Width, info.Value.Height, format == "hdr", format);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>
    /// Decodes an image. Throws <see cref="AssetImportException"/> with a readable message for unsupported, corrupt or
    /// oversized data. <paramref name="applyExifOrientation"/> rotates JPEG photos upright.
    /// </summary>
    public static ImageData Decode(byte[] data, bool applyExifOrientation = true)
    {
        ArgumentNullException.ThrowIfNull(data);
        string? format = DetectFormat(data);
        if (format is null)
            throw new AssetImportException(DescribeUnsupported(data));
        var info = Identify(data) ?? throw new AssetImportException($"The {format.ToUpperInvariant()} image header is damaged.");
        if (info.Width > MaxDimension || info.Height > MaxDimension || (long)info.Width * info.Height > MaxPixels)
            throw new AssetImportException($"The image is too large ({info.Width} × {info.Height} pixels; at most {MaxDimension} per side).");
        try
        {
            ImageData image;
            if (info.IsHdr)
            {
                var hdr = ImageResultFloat.FromMemory(data, ReadComponents.RedGreenBlueAlpha);
                var bytes = new byte[hdr.Data.Length * sizeof(float)];
                if (BitConverter.IsLittleEndian) MemoryMarshal.AsBytes(hdr.Data.AsSpan()).CopyTo(bytes);
                else
                {
                    for (int i = 0; i < hdr.Data.Length; i++)
                        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4, 4), hdr.Data[i]);
                }
                image = new ImageData { Width = hdr.Width, Height = hdr.Height, Format = PixelFormat.RgbaFloat, Pixels = bytes };
            }
            else
            {
                var ldr = ImageResult.FromMemory(data, ReadComponents.RedGreenBlueAlpha);
                image = new ImageData { Width = ldr.Width, Height = ldr.Height, Format = PixelFormat.Rgba8, Pixels = ldr.Data };
            }
            int bpp = image.Format == PixelFormat.RgbaFloat ? 16 : 4;
            if (image.Width <= 0 || image.Height <= 0 || image.Pixels.Length != image.Width * image.Height * bpp)
                throw new AssetImportException($"The {format.ToUpperInvariant()} image could not be decoded.");
            if (applyExifOrientation && format == "jpeg") image = ExifOrientation.Apply(image, ExifOrientation.Read(data));
            return image;
        }
        catch (AssetImportException)
        {
            throw;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            throw new AssetImportException($"The {format.ToUpperInvariant()} image is damaged or uses an unsupported variant: {e.Message}", e);
        }
    }

    /// <summary>Decodes an image, returning false (and the reason) instead of throwing.</summary>
    public static bool TryDecode(byte[] data, out ImageData? image, out string? error, bool applyExifOrientation = true)
    {
        try
        {
            image = Decode(data, applyExifOrientation);
            error = null;
            return true;
        }
        catch (AssetImportException e)
        {
            image = null;
            error = e.Message;
            return false;
        }
    }

    /// <summary>Reads and decodes an image file.</summary>
    public static ImageData Load(string path, bool applyExifOrientation = true)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new AssetImportException($"Cannot read '{Path.GetFileName(path)}': {e.Message}", e);
        }
        return Decode(data, applyExifOrientation);
    }

    /// <summary>
    /// Encodes an image as PNG (RGBA, 8 bits per channel). Float images are treated as linear scene values, clamped to
    /// 0 … 1 and sRGB-encoded (no tone mapping — pass tone-mapped data for renders).
    /// </summary>
    public static byte[] EncodePng(ImageData image)
    {
        using var ms = new MemoryStream();
        WritePng(image, ms);
        return ms.ToArray();
    }

    /// <summary>Writes an image as PNG to a stream (see <see cref="EncodePng"/>).</summary>
    public static void WritePng(ImageData image, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(destination);
        if (image.Width <= 0 || image.Height <= 0) throw new ArgumentException("The image has no pixels.", nameof(image));
        var rgba = ToRgba8(image);
        new ImageWriter().WritePng(rgba, image.Width, image.Height, WriteComponents.RedGreenBlueAlpha, destination);
    }

    /// <summary>Writes an image as a PNG file, atomically (temporary file + rename).</summary>
    public static void SavePng(ImageData image, string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            using (var fs = File.Create(tmp)) WritePng(image, fs);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    /// <summary>
    /// 8-bit RGBA pixels of an image: the stored bytes for <see cref="PixelFormat.Rgba8"/>; for float images linear values
    /// clamped to 0 … 1 and sRGB-encoded (alpha linear).
    /// </summary>
    public static byte[] ToRgba8(ImageData image)
    {
        ArgumentNullException.ThrowIfNull(image);
        int count = image.Width * image.Height;
        if (image.Format == PixelFormat.Rgba8)
        {
            if (image.Pixels.Length != count * 4) throw new ArgumentException("Pixel buffer size does not match the dimensions.", nameof(image));
            return image.Pixels;
        }
        if (image.Pixels.Length != count * 16) throw new ArgumentException("Pixel buffer size does not match the dimensions.", nameof(image));
        var result = new byte[count * 4];
        var src = image.Pixels.AsSpan();
        for (int i = 0; i < count * 4; i++)
        {
            float v = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(src.Slice(i * 4, 4));
            if (!float.IsFinite(v)) v = 0;
            v = System.Math.Clamp(v, 0f, 1f);
            if ((i & 3) != 3) v = ColorEncoding.LinearToSrgb(v);
            result[i] = (byte)MathF.Round(v * 255f);
        }
        return result;
    }

    /// <summary>Container format from magic bytes, or null.</summary>
    public static string? DetectFormat(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 8 && data[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "png";
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return "jpeg";
        if (data.Length >= 6 && (data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8))) return "gif";
        if (data.Length >= 2 && data[0] == (byte)'B' && data[1] == (byte)'M') return "bmp";
        if (data.Length >= 4 && data[..4].SequenceEqual("8BPS"u8)) return "psd";
        if (data.Length >= 2 && data[0] == (byte)'#' && data[1] == (byte)'?') return "hdr";
        if (LooksLikeTga(data)) return "tga";
        return null;
    }

    private static bool LooksLikeTga(ReadOnlySpan<byte> d)
    {
        // TGA has no magic number; validate the header fields stb accepts.
        if (d.Length < 18) return false;
        byte colorMapType = d[1], imageType = d[2];
        if (colorMapType > 1) return false;
        if (imageType is not (1 or 2 or 3 or 9 or 10 or 11)) return false;
        int width = d[12] | d[13] << 8, height = d[14] | d[15] << 8;
        byte bits = d[16];
        return width > 0 && height > 0 && bits is 8 or 15 or 16 or 24 or 32;
    }

    private static string DescribeUnsupported(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 4 && (data[..4].SequenceEqual("II*\0"u8) || data[..4].SequenceEqual("MM\0*"u8)))
            return "TIFF images are not supported; save the image as PNG or JPEG.";
        if (data.Length >= 4 && data[..4].SequenceEqual("DDS "u8))
            return "DDS textures are not supported; save the texture as PNG or JPEG.";
        if (data.Length >= 4 && data[..4].SequenceEqual(new byte[] { 0x76, 0x2F, 0x31, 0x01 }))
            return "OpenEXR images are not supported; use Radiance .hdr for high dynamic range images.";
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8))
            return "WebP images are not supported; save the image as PNG or JPEG.";
        if (data.Length >= 12 && data.Slice(4, 4).SequenceEqual("ftyp"u8))
            return "HEIF/AVIF images are not supported; save the image as PNG or JPEG.";
        return "Unsupported or damaged image (PNG, JPEG, TGA, BMP, PSD, GIF and HDR are supported).";
    }
}
