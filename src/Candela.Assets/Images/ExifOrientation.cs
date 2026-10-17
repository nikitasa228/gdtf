using System.Buffers.Binary;
using Candela.Core.Geometry;

namespace Candela.Assets;

/// <summary>
/// Reads the EXIF orientation tag (0x0112) of a JPEG and applies it to decoded pixels, so photos taken with a rotated
/// camera appear upright. Values follow the TIFF/EXIF specification (1 = as stored … 8 = rotate 90° counter-clockwise).
/// </summary>
public static class ExifOrientation
{
    /// <summary>
    /// The orientation stored in a JPEG's APP1/Exif segment, or 1 (as stored) when the data is not a JPEG, carries no
    /// EXIF block or the block is malformed.
    /// </summary>
    public static int Read(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return 1;
        int pos = 2;
        while (pos + 4 <= jpeg.Length)
        {
            if (jpeg[pos] != 0xFF) return 1;
            byte marker = jpeg[pos + 1];
            if (marker == 0xFF)
            {
                pos++;
                continue;
            }
            // Start of scan or end of image: no metadata after this point.
            if (marker == 0xDA || marker == 0xD9) return 1;
            if (marker is >= 0xD0 and <= 0xD7 or 0x01)
            {
                pos += 2;
                continue;
            }
            int length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.Slice(pos + 2, 2));
            if (length < 2 || pos + 2 + length > jpeg.Length) return 1;
            var segment = jpeg.Slice(pos + 4, length - 2);
            if (marker == 0xE1 && segment.Length >= 14 && segment[..6].SequenceEqual("Exif\0\0"u8))
            {
                int o = ReadTiffOrientation(segment[6..]);
                if (o != 0) return o;
            }
            pos += 2 + length;
        }
        return 1;
    }

    private static int ReadTiffOrientation(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8) return 0;
        bool little;
        if (tiff[0] == (byte)'I' && tiff[1] == (byte)'I') little = true;
        else if (tiff[0] == (byte)'M' && tiff[1] == (byte)'M') little = false;
        else return 0;
        if (U16(tiff, 2, little) != 42) return 0;
        uint ifd = U32(tiff, 4, little);
        if (ifd > (uint)tiff.Length - 2) return 0;
        int count = U16(tiff, (int)ifd, little);
        for (int i = 0; i < count; i++)
        {
            int entry = (int)ifd + 2 + i * 12;
            if (entry + 12 > tiff.Length) return 0;
            if (U16(tiff, entry, little) != 0x0112) continue;
            ushort type = U16(tiff, entry + 2, little);
            int value = type == 3 ? U16(tiff, entry + 8, little) : type == 4 ? (int)U32(tiff, entry + 8, little) : 0;
            return value is >= 1 and <= 8 ? value : 0;
        }
        return 0;
    }

    private static ushort U16(ReadOnlySpan<byte> s, int at, bool little) =>
        little ? BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(at, 2)) : BinaryPrimitives.ReadUInt16BigEndian(s.Slice(at, 2));

    private static uint U32(ReadOnlySpan<byte> s, int at, bool little) =>
        little ? BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(at, 4)) : BinaryPrimitives.ReadUInt32BigEndian(s.Slice(at, 4));

    /// <summary>
    /// Returns the image re-oriented for display according to <paramref name="orientation"/> (2–8); 1 or an invalid
    /// value returns the input unchanged. Orientations 5–8 swap width and height.
    /// </summary>
    public static ImageData Apply(ImageData image, int orientation)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (orientation is < 2 or > 8) return image;
        int w = image.Width, h = image.Height;
        int bpp = image.Format == PixelFormat.RgbaFloat ? 16 : 4;
        bool swap = orientation >= 5;
        int dw = swap ? h : w, dh = swap ? w : h;
        var src = image.Pixels;
        var dst = new byte[src.Length];
        for (int dy = 0; dy < dh; dy++)
        {
            for (int dx = 0; dx < dw; dx++)
            {
                (int sx, int sy) = orientation switch
                {
                    2 => (w - 1 - dx, dy),
                    3 => (w - 1 - dx, h - 1 - dy),
                    4 => (dx, h - 1 - dy),
                    5 => (dy, dx),
                    6 => (dy, h - 1 - dx),
                    7 => (w - 1 - dy, h - 1 - dx),
                    _ => (w - 1 - dy, dx),
                };
                Buffer.BlockCopy(src, (sy * w + sx) * bpp, dst, (dy * dw + dx) * bpp, bpp);
            }
        }
        return new ImageData { Width = dw, Height = dh, Format = image.Format, Pixels = dst };
    }
}
