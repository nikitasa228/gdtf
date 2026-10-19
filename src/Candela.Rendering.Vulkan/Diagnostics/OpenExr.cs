using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Candela.Core.Geometry;

namespace Candela.Rendering.Vulkan;

/// <summary>Compression of an OpenEXR file written by <see cref="OpenExr"/>.</summary>
public enum ExrCompression
{
    /// <summary>NO_COMPRESSION: one scanline per chunk.</summary>
    None,

    /// <summary>ZIPS_COMPRESSION: zlib (with the EXR byte reordering and delta predictor) per scanline.</summary>
    ZipSingleLine,

    /// <summary>ZIP_COMPRESSION: zlib per block of 16 scanlines (the usual choice).</summary>
    Zip,
}

/// <summary>Sample type of an OpenEXR channel.</summary>
public enum ExrPixelType
{
    /// <summary>32-bit unsigned integer.</summary>
    UInt = 0,

    /// <summary>16-bit float (enough for display-referred and most scene-referred images).</summary>
    Half = 1,

    /// <summary>32-bit float.</summary>
    Float = 2,
}

/// <summary>One channel of an OpenEXR image: its name (e.g. "R", "raw.R", "N.X") and width × height values, top row first.</summary>
/// <param name="Name">Channel name (layers are dot-separated prefixes).</param>
/// <param name="Values">width × height samples, top row first.</param>
/// <param name="Type">Sample type in the file.</param>
public sealed record ExrChannel(string Name, float[] Values, ExrPixelType Type = ExrPixelType.Half);

/// <summary>An OpenEXR image read by <see cref="OpenExr.Read(Stream)"/>.</summary>
/// <param name="Width">Width of the data window, px.</param>
/// <param name="Height">Height of the data window, px.</param>
/// <param name="Compression">Compression of the file.</param>
/// <param name="Channels">The channels in file order (sorted by name).</param>
public sealed record ExrImage(int Width, int Height, ExrCompression Compression, IReadOnlyList<ExrChannel> Channels)
{
    /// <summary>The channel with this name, or null.</summary>
    public ExrChannel? Channel(string name) => Channels.FirstOrDefault(c => c.Name == name);
}

/// <summary>
/// A small, dependency-free OpenEXR (2.x) reader/writer for single-part scanline images: uncompressed, ZIPS or ZIP
/// compression (zlib through <see cref="ZLibStream"/> with the format's byte reordering and delta predictor), HALF,
/// FLOAT or UINT channels with arbitrary (layered) names. Enough for the renderer's linear HDR output and its
/// auxiliary layers; the reader accepts what the writer produces (and other files of that kind) and rejects tiled,
/// deep, multi-part or otherwise compressed files with <see cref="InvalidDataException"/>.
/// </summary>
public static class OpenExr
{
    private const int Magic = 20000630;
    private const int LongNamesFlag = 0x400;
    private const int MaxDimension = 1 << 20;

    /// <summary>
    /// Writes the RGBA float image <paramref name="hdr"/> (linear, e.g. <see cref="IFinalRender.ResultHdr"/>) as channels
    /// R, G, B, A (plus <paramref name="extraChannels"/>, e.g. layers "raw.R" or "albedo.R") to <paramref name="path"/>
    /// (written to a temporary file next to it and moved into place).
    /// </summary>
    public static void WriteRgba(string path, ImageData hdr, ExrPixelType type = ExrPixelType.Half, ExrCompression compression = ExrCompression.Zip,
        IEnumerable<ExrChannel>? extraChannels = null)
    {
        ArgumentNullException.ThrowIfNull(hdr);
        var channels = new List<ExrChannel>(SplitRgba(hdr, "", type, includeAlpha: true));
        if (extraChannels is not null)
            channels.AddRange(extraChannels);
        Write(path, hdr.Width, hdr.Height, channels, compression);
    }

    /// <summary>
    /// The R, G, B (and A) planes of an RGBA float image as channels named <paramref name="layer"/> + "R" etc.
    /// (layer "" for the default layer, "raw." for a layer called raw).
    /// </summary>
    public static IEnumerable<ExrChannel> SplitRgba(ImageData image, string layer, ExrPixelType type = ExrPixelType.Half, bool includeAlpha = false,
        string names = "RGBA")
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(layer);
        if (image.Format != PixelFormat.RgbaFloat)
            throw new ArgumentException("The image must be RGBA float.", nameof(image));
        int pixels = checked(image.Width * image.Height);
        if (image.Pixels.Length < pixels * 16)
            throw new ArgumentException("The image has fewer pixels than its size says.", nameof(image));
        var values = new float[pixels * 4];
        System.Buffer.BlockCopy(image.Pixels, 0, values, 0, pixels * 16);
        int count = includeAlpha ? 4 : 3;
        for (int c = 0; c < count && c < names.Length; c++)
        {
            var plane = new float[pixels];
            for (int i = 0; i < pixels; i++)
                plane[i] = values[4 * i + c];
            yield return new ExrChannel(layer + names[c], plane, type);
        }
    }

    /// <summary>Writes an image to <paramref name="path"/> through a temporary file (atomic replace).</summary>
    public static void Write(string path, int width, int height, IReadOnlyList<ExrChannel> channels, ExrCompression compression = ExrCompression.Zip)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string full = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        string temporary = full + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                Write(stream, width, height, channels, compression);
            File.Move(temporary, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    /// <summary>Writes a single-part scanline OpenEXR image (channels are sorted by name, as the format requires).</summary>
    public static void Write(Stream stream, int width, int height, IReadOnlyList<ExrChannel> channels, ExrCompression compression = ExrCompression.Zip)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(channels);
        if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension)
            throw new ArgumentOutOfRangeException(nameof(width), "The image size must be between 1 and 2^20 pixels per side.");
        if (channels.Count == 0)
            throw new ArgumentException("An EXR image needs at least one channel.", nameof(channels));
        var sorted = channels.OrderBy(c => c.Name, StringComparer.Ordinal).ToArray();
        for (int i = 0; i < sorted.Length; i++)
        {
            var c = sorted[i];
            if (string.IsNullOrEmpty(c.Name) || c.Name.Length > 255 || c.Name.Contains('\0'))
                throw new ArgumentException($"Invalid channel name '{c.Name}'.", nameof(channels));
            if (i > 0 && sorted[i - 1].Name == c.Name)
                throw new ArgumentException($"Duplicate channel '{c.Name}'.", nameof(channels));
            if (c.Values.Length < (long)width * height)
                throw new ArgumentException($"Channel '{c.Name}' has {c.Values.Length} values for {width} x {height} pixels.", nameof(channels));
            if (c.Type is not (ExrPixelType.Half or ExrPixelType.Float or ExrPixelType.UInt))
                throw new ArgumentException($"Channel '{c.Name}' has an unknown pixel type.", nameof(channels));
        }

        bool longNames = sorted.Any(c => c.Name.Length > 31);
        int linesPerChunk = LinesPerChunk(compression);
        int chunkCount = (height + linesPerChunk - 1) / linesPerChunk;
        int lineBytes = sorted.Sum(c => width * BytesPerSample(c.Type));

        var header = new MemoryStream();
        var w = new BinaryWriter(header, Encoding.ASCII, leaveOpen: true);
        w.Write(Magic);
        w.Write(2 | (longNames ? LongNamesFlag : 0));
        Attribute(w, "channels", "chlist", writer =>
        {
            foreach (var c in sorted)
            {
                CString(writer, c.Name);
                writer.Write((int)c.Type);
                writer.Write((byte)0); // pLinear
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write(1); // x sampling
                writer.Write(1); // y sampling
            }

            writer.Write((byte)0);
        });
        Attribute(w, "compression", "compression", writer => writer.Write((byte)CompressionCode(compression)));
        Attribute(w, "dataWindow", "box2i", writer => Box(writer, width, height));
        Attribute(w, "displayWindow", "box2i", writer => Box(writer, width, height));
        Attribute(w, "lineOrder", "lineOrder", writer => writer.Write((byte)0)); // INCREASING_Y
        Attribute(w, "pixelAspectRatio", "float", writer => writer.Write(1f));
        Attribute(w, "screenWindowCenter", "v2f", writer =>
        {
            writer.Write(0f);
            writer.Write(0f);
        });
        Attribute(w, "screenWindowWidth", "float", writer => writer.Write(1f));
        w.Write((byte)0); // end of header
        w.Flush();

        // Chunks: encoded first (their sizes give the offset table), then written after the table.
        var chunks = new byte[chunkCount][];
        var raw = new byte[lineBytes * linesPerChunk];
        for (int chunk = 0; chunk < chunkCount; chunk++)
        {
            int y0 = chunk * linesPerChunk;
            int lines = Math.Min(linesPerChunk, height - y0);
            int size = lines * lineBytes;
            int at = 0;
            for (int y = y0; y < y0 + lines; y++)
            {
                foreach (var c in sorted)
                {
                    int row = y * width;
                    for (int x = 0; x < width; x++)
                        at = WriteSample(raw, at, c.Type, c.Values[row + x]);
                }
            }

            var data = compression == ExrCompression.None ? raw.AsSpan(0, size).ToArray() : Compress(raw.AsSpan(0, size));
            var block = new byte[8 + data.Length];
            BinaryPrimitives.WriteInt32LittleEndian(block, y0);
            BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(4), data.Length);
            data.CopyTo(block, 8);
            chunks[chunk] = block;
        }

        long offset = header.Length + 8L * chunkCount;
        var table = new byte[8 * chunkCount];
        for (int chunk = 0; chunk < chunkCount; chunk++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(8 * chunk), (ulong)offset);
            offset += chunks[chunk].Length;
        }

        header.Position = 0;
        header.CopyTo(stream);
        stream.Write(table);
        foreach (var block in chunks)
            stream.Write(block);
        stream.Flush();
    }

    /// <summary>Reads an OpenEXR file.</summary>
    public static ExrImage Read(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    /// <summary>
    /// Reads a single-part scanline OpenEXR image (NO, ZIPS or ZIP compression; HALF, FLOAT or UINT channels with
    /// sampling 1). Throws <see cref="InvalidDataException"/> for anything else or a damaged file.
    /// </summary>
    public static ExrImage Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        var bytes = copy.ToArray();
        try
        {
            return Parse(bytes);
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException or EndOfStreamException or OverflowException)
        {
            throw new InvalidDataException("The EXR file is damaged: " + e.Message, e);
        }
    }

    private static ExrImage Parse(byte[] bytes)
    {
        var span = bytes.AsSpan();
        if (span.Length < 8 || BinaryPrimitives.ReadInt32LittleEndian(span) != Magic)
            throw new InvalidDataException("Not an OpenEXR file.");
        int version = BinaryPrimitives.ReadInt32LittleEndian(span[4..]);
        if ((version & 0xFF) != 2)
            throw new InvalidDataException($"Unsupported OpenEXR version {version & 0xFF}.");
        if ((version & 0x200) != 0 || (version & 0x800) != 0 || (version & 0x1000) != 0)
            throw new InvalidDataException("Tiled, deep or multi-part OpenEXR files are not supported.");
        int at = 8;
        var channels = new List<(string Name, ExrPixelType Type)>();
        int compressionCode = -1;
        int xMin = 0, yMin = 0, xMax = -1, yMax = -1;
        bool dataWindow = false;
        while (true)
        {
            string name = ReadCString(span, ref at);
            if (name.Length == 0)
                break;
            string type = ReadCString(span, ref at);
            int size = BinaryPrimitives.ReadInt32LittleEndian(span[at..]);
            at += 4;
            if (size < 0 || at + size > span.Length)
                throw new InvalidDataException($"Attribute '{name}' runs past the end of the file.");
            var value = span.Slice(at, size);
            at += size;
            switch (name)
            {
                case "channels" when type == "chlist":
                    int p = 0;
                    while (p < value.Length && value[p] != 0)
                    {
                        string channel = ReadCString(value, ref p);
                        int pixelType = BinaryPrimitives.ReadInt32LittleEndian(value[p..]);
                        int xs = BinaryPrimitives.ReadInt32LittleEndian(value[(p + 8)..]);
                        int ys = BinaryPrimitives.ReadInt32LittleEndian(value[(p + 12)..]);
                        p += 16;
                        if (pixelType is < 0 or > 2)
                            throw new InvalidDataException($"Channel '{channel}' has an unknown pixel type {pixelType}.");
                        if (xs != 1 || ys != 1)
                            throw new InvalidDataException($"Channel '{channel}' is subsampled, which is not supported.");
                        channels.Add((channel, (ExrPixelType)pixelType));
                    }

                    break;
                case "compression":
                    compressionCode = value.Length > 0 ? value[0] : -1;
                    break;
                case "dataWindow" when type == "box2i" && value.Length >= 16:
                    xMin = BinaryPrimitives.ReadInt32LittleEndian(value);
                    yMin = BinaryPrimitives.ReadInt32LittleEndian(value[4..]);
                    xMax = BinaryPrimitives.ReadInt32LittleEndian(value[8..]);
                    yMax = BinaryPrimitives.ReadInt32LittleEndian(value[12..]);
                    dataWindow = true;
                    break;
                case "type" when !Encoding.ASCII.GetString(value).TrimEnd('\0').Equals("scanlineimage", StringComparison.Ordinal):
                    throw new InvalidDataException("Only scanline OpenEXR images are supported.");
            }
        }

        if (!dataWindow || channels.Count == 0)
            throw new InvalidDataException("The EXR header has no data window or no channels.");
        long w = (long)xMax - xMin + 1, h = (long)yMax - yMin + 1;
        if (w <= 0 || h <= 0 || w > MaxDimension || h > MaxDimension || w * h * channels.Count > int.MaxValue / 4)
            throw new InvalidDataException($"Unsupported EXR data window {w} x {h}.");
        var compression = compressionCode switch
        {
            0 => ExrCompression.None,
            2 => ExrCompression.ZipSingleLine,
            3 => ExrCompression.Zip,
            _ => throw new InvalidDataException($"Unsupported EXR compression {compressionCode}."),
        };
        int width = (int)w, height = (int)h;
        int linesPerChunk = LinesPerChunk(compression);
        int chunkCount = (height + linesPerChunk - 1) / linesPerChunk;
        int lineBytes = channels.Sum(c => width * BytesPerSample(c.Type));
        var planes = channels.Select(_ => new float[width * height]).ToArray();
        for (int chunk = 0; chunk < chunkCount; chunk++)
        {
            long offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(span[(at + 8 * chunk)..]);
            if (offset < at || offset + 8 > span.Length)
                throw new InvalidDataException($"EXR chunk {chunk} lies outside the file.");
            var block = span[(int)offset..];
            int y0 = BinaryPrimitives.ReadInt32LittleEndian(block) - yMin;
            int size = BinaryPrimitives.ReadInt32LittleEndian(block[4..]);
            if (y0 < 0 || y0 >= height || size < 0 || 8L + size > block.Length)
                throw new InvalidDataException($"EXR chunk {chunk} is damaged.");
            int lines = Math.Min(linesPerChunk, height - y0);
            int expected = lines * lineBytes;
            var data = block.Slice(8, size);
            byte[] raw = size == expected ? data.ToArray() : compression == ExrCompression.None
                ? throw new InvalidDataException($"EXR chunk {chunk} has {size} bytes instead of {expected}.")
                : Decompress(data, expected);
            int p = 0;
            for (int y = y0; y < y0 + lines; y++)
            {
                for (int c = 0; c < channels.Count; c++)
                {
                    var plane = planes[c];
                    int row = y * width;
                    for (int x = 0; x < width; x++)
                        plane[row + x] = ReadSample(raw, ref p, channels[c].Type);
                }
            }
        }

        var result = channels.Select((c, i) => new ExrChannel(c.Name, planes[i], c.Type)).ToArray();
        return new ExrImage(width, height, compression, result);
    }

    private static int LinesPerChunk(ExrCompression compression) => compression == ExrCompression.Zip ? 16 : 1;

    private static int CompressionCode(ExrCompression compression) => compression switch
    {
        ExrCompression.None => 0,
        ExrCompression.ZipSingleLine => 2,
        _ => 3,
    };

    private static int BytesPerSample(ExrPixelType type) => type == ExrPixelType.Half ? 2 : 4;

    private static int WriteSample(byte[] buffer, int at, ExrPixelType type, float value)
    {
        switch (type)
        {
            case ExrPixelType.Half:
                BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(at), BitConverter.HalfToUInt16Bits((Half)value));
                return at + 2;
            case ExrPixelType.Float:
                BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(at), value);
                return at + 4;
            default:
                uint u = float.IsFinite(value) ? (uint)Math.Clamp(value, 0f, uint.MaxValue) : 0u;
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(at), u);
                return at + 4;
        }
    }

    private static float ReadSample(byte[] buffer, ref int at, ExrPixelType type)
    {
        switch (type)
        {
            case ExrPixelType.Half:
                var h = BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(at)));
                at += 2;
                return (float)h;
            case ExrPixelType.Float:
                float f = BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(at));
                at += 4;
                return f;
            default:
                uint u = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(at));
                at += 4;
                return u;
        }
    }

    /// <summary>EXR ZIP: split even/odd bytes, delta predictor, zlib. Raw bytes when that is not smaller.</summary>
    private static byte[] Compress(ReadOnlySpan<byte> raw)
    {
        var tmp = new byte[raw.Length];
        int half = (raw.Length + 1) / 2;
        for (int i = 0, t1 = 0, t2 = half; i < raw.Length; i++)
        {
            if ((i & 1) == 0)
                tmp[t1++] = raw[i];
            else
                tmp[t2++] = raw[i];
        }

        int previous = tmp.Length > 0 ? tmp[0] : 0;
        for (int i = 1; i < tmp.Length; i++)
        {
            int d = tmp[i] - previous + (128 + 256);
            previous = tmp[i];
            tmp[i] = (byte)d;
        }

        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(tmp);
        return output.Length < raw.Length ? output.ToArray() : raw.ToArray();
    }

    private static byte[] Decompress(ReadOnlySpan<byte> data, int expected)
    {
        var tmp = new byte[expected];
        using (var input = new MemoryStream(data.ToArray()))
        using (var zlib = new ZLibStream(input, CompressionMode.Decompress))
        {
            int read = 0;
            while (read < expected)
            {
                int n = zlib.Read(tmp, read, expected - read);
                if (n == 0)
                    throw new InvalidDataException("An EXR chunk decompresses to fewer bytes than its lines need.");
                read += n;
            }
        }

        for (int i = 1; i < tmp.Length; i++)
            tmp[i] = (byte)(tmp[i - 1] + tmp[i] - 128);
        var raw = new byte[expected];
        int half = (expected + 1) / 2;
        for (int i = 0, t1 = 0, t2 = half; i < expected; i++)
            raw[i] = (i & 1) == 0 ? tmp[t1++] : tmp[t2++];
        return raw;
    }

    private static void Attribute(BinaryWriter w, string name, string type, Action<BinaryWriter> value)
    {
        CString(w, name);
        CString(w, type);
        using var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, Encoding.ASCII, leaveOpen: true))
            value(writer);
        w.Write((int)body.Length);
        w.Write(body.ToArray());
    }

    private static void Box(BinaryWriter w, int width, int height)
    {
        w.Write(0);
        w.Write(0);
        w.Write(width - 1);
        w.Write(height - 1);
    }

    private static void CString(BinaryWriter w, string s)
    {
        w.Write(Encoding.UTF8.GetBytes(s));
        w.Write((byte)0);
    }

    private static string ReadCString(ReadOnlySpan<byte> span, ref int at)
    {
        int end = span[at..].IndexOf((byte)0);
        if (end < 0 || end > 255)
            throw new InvalidDataException("An EXR header string is not terminated.");
        string s = Encoding.UTF8.GetString(span.Slice(at, end));
        at += end + 1;
        return s;
    }
}
