using System.Numerics;
using Candela.Core.Geometry;
using Candela.Core.Math;
using Candela.Dmx;
using Candela.Gdtf;
using Candela.Rendering;
using Candela.Simulation;

namespace Candela.Engine;

/// <summary>The 3D models of one fixture type + mode, per geometry part of its DMX profile.</summary>
/// <param name="Parts">Profile part index (into <c>FixtureProfile.Geometry.Parts</c>) with the model drawn at that part.</param>
/// <param name="Error">Why nothing (or not everything) could be loaded, or null.</param>
internal sealed record LoadedFixtureModels(List<(int Part, ModelData Model)> Parts, string? Error);

/// <summary>
/// Loads the models of a fixture type with Candela.Gdtf (<see cref="FixtureGeometryBuilder"/> + <see cref="GdtfModelLoader"/>)
/// and maps the geometry parts onto the parts of the DMX profile, whose poses the engine computes. Pure function of the
/// GDTF file: safe on a background thread.
/// </summary>
internal static class FixtureModelLoader
{
    public static LoadedFixtureModels Load(string gdtfPath, FixtureProfile profile)
    {
        var parts = new List<(int, ModelData)>();
        try
        {
            using var archive = GdtfArchive.Open(gdtfPath, GdtfArchiveLoad.InMemory);
            var loader = new GdtfModelLoader(archive);
            var geometry = new FixtureGeometryBuilder(profile.FixtureType, loader).Build(profile.Mode);
            var target = profile.Geometry;
            bool sameLayout = geometry.Parts.Count == target.Parts.Count;
            for (int i = 0; i < geometry.Parts.Count; i++)
            {
                var part = geometry.Parts[i];
                if (part.Model is not { Parts.Count: > 0 } model) continue;
                int index = sameLayout && target.Parts[i].Path == part.Path ? i : target.FindPartByPath(part.Path);
                if (index >= 0) parts.Add((index, model));
            }
            return new LoadedFixtureModels(parts, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException
            or InvalidOperationException or NotSupportedException or FormatException)
        {
            return new LoadedFixtureModels(parts, e.Message);
        }
    }

    /// <summary>Bounds of the fixture's models at its rest pose with <paramref name="transform"/> applied.</summary>
    public static Aabb RestBounds(FixtureProfile profile, LoadedFixtureModels models, Matrix4x4 transform)
    {
        var world = new Matrix4x4[profile.Geometry.Parts.Count];
        profile.Geometry.ComputeWorldTransforms(world, transform);
        var bounds = Aabb.Empty;
        foreach (var (part, model) in models.Parts)
            if ((uint)part < (uint)world.Length) bounds.Include(model.ComputeBounds().Transform(world[part]));
        return bounds;
    }
}

/// <summary>Turns a GDTF gobo image into a renderer gobo texture (alpha = transmission, RGB = tint).</summary>
internal static class GoboImage
{
    /// <summary>
    /// GDTF gobo media show the projected image: light passes where the picture is bright (white metal-cut gobos are
    /// black on white), coloured glass tints it, and transparent pixels block. Transmission = linear luminance × alpha;
    /// the tint is the pixel colour normalised to its brightest channel. Float images are converted too.
    /// </summary>
    public static ImageData? ToTransmission(ImageData image)
    {
        if (image.Width <= 0 || image.Height <= 0) return null;
        int n = image.Width * image.Height;
        var output = new byte[n * 4];
        if (image.Format == PixelFormat.Rgba8)
        {
            if (image.Pixels.Length < n * 4) return null;
            Span<float> lut = stackalloc float[256];
            for (int i = 0; i < 256; i++)
            {
                float c = i / 255f;
                lut[i] = c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
            }
            var src = image.Pixels;
            for (int i = 0; i < n; i++)
            {
                byte r = src[4 * i], g = src[4 * i + 1], b = src[4 * i + 2], a = src[4 * i + 3];
                float y = 0.2627f * lut[r] + 0.6780f * lut[g] + 0.0593f * lut[b];
                Write(output, i, r, g, b, y * (a / 255f));
            }
        }
        else
        {
            if (image.Pixels.Length < n * 16) return null;
            var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(image.Pixels.AsSpan(0, n * 16));
            for (int i = 0; i < n; i++)
            {
                float r = Clamp01(floats[4 * i]), g = Clamp01(floats[4 * i + 1]), b = Clamp01(floats[4 * i + 2]), a = Clamp01(floats[4 * i + 3]);
                float y = 0.2627f * r + 0.6780f * g + 0.0593f * b;
                Write(output, i, ToSrgbByte(r), ToSrgbByte(g), ToSrgbByte(b), y * a);
            }
        }
        return new ImageData { Width = image.Width, Height = image.Height, Format = PixelFormat.Rgba8, Pixels = output };
    }

    private static void Write(byte[] output, int i, byte r, byte g, byte b, float transmission)
    {
        int max = Math.Max(r, Math.Max(g, b));
        if (max == 0)
        {
            output[4 * i] = output[4 * i + 1] = output[4 * i + 2] = 255;
        }
        else
        {
            output[4 * i] = (byte)(r * 255 / max);
            output[4 * i + 1] = (byte)(g * 255 / max);
            output[4 * i + 2] = (byte)(b * 255 / max);
        }
        output[4 * i + 3] = (byte)Math.Clamp((int)MathF.Round(transmission * 255f), 0, 255);
    }

    private static float Clamp01(float v) => float.IsFinite(v) ? Math.Clamp(v, 0f, 1f) : 0f;

    private static byte ToSrgbByte(float linear)
    {
        float c = linear <= 0.0031308f ? linear * 12.92f : 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;
        return (byte)Math.Clamp((int)MathF.Round(c * 255f), 0, 255);
    }
}

/// <summary>
/// Composites the temperature grids of all flames into the single <see cref="VolumeGrid"/> the renderer takes: a box over
/// every flame (snapped outward to whole metres so it rarely changes), cells as fine as the finest flame grid (at most
/// <see cref="MaxCellsPerAxis"/> per axis), each cell the hottest flame there.
/// </summary>
internal static class FlameComposite
{
    public const int MaxCellsPerAxis = 96;

    public static VolumeGrid? Update(VolumeGrid? previous, IReadOnlyList<FlameVolume> volumes)
    {
        var bounds = Aabb.Empty;
        float cell = float.PositiveInfinity;
        foreach (var v in volumes)
        {
            if (v.Bounds.IsEmpty || v.SizeX <= 0 || v.SizeY <= 0 || v.SizeZ <= 0) continue;
            bounds.Include(v.Bounds);
            if (v.CellSize > 0f && float.IsFinite(v.CellSize)) cell = MathF.Min(cell, v.CellSize);
        }
        if (bounds.IsEmpty || !float.IsFinite(cell)) return null;
        var min = new Vector3(MathF.Floor(bounds.Min.X), MathF.Floor(bounds.Min.Y), MathF.Floor(bounds.Min.Z));
        var max = new Vector3(MathF.Ceiling(bounds.Max.X), MathF.Ceiling(bounds.Max.Y), MathF.Ceiling(bounds.Max.Z));
        max = Vector3.Max(max, min + Vector3.One);
        var size = max - min;
        cell = MathF.Max(cell, MathF.Max(size.X, MathF.Max(size.Y, size.Z)) / MaxCellsPerAxis);
        int nx = Math.Clamp((int)MathF.Ceiling(size.X / cell), 1, MaxCellsPerAxis);
        int ny = Math.Clamp((int)MathF.Ceiling(size.Y / cell), 1, MaxCellsPerAxis);
        int nz = Math.Clamp((int)MathF.Ceiling(size.Z / cell), 1, MaxCellsPerAxis);

        var grid = previous is not null && previous.Min == min && previous.Max == max && previous.SizeX == nx && previous.SizeY == ny && previous.SizeZ == nz
            ? previous
            : new VolumeGrid { Min = min, Max = max, SizeX = nx, SizeY = ny, SizeZ = nz, Values = new float[nx * ny * nz] };
        var values = grid.Values;
        Array.Clear(values);
        var step = new Vector3(size.X / nx, size.Y / ny, size.Z / nz);
        foreach (var v in volumes)
        {
            if (v.Bounds.IsEmpty) continue;
            int x0 = Math.Max(0, (int)((v.Bounds.Min.X - min.X) / step.X)), x1 = Math.Min(nx - 1, (int)((v.Bounds.Max.X - min.X) / step.X));
            int y0 = Math.Max(0, (int)((v.Bounds.Min.Y - min.Y) / step.Y)), y1 = Math.Min(ny - 1, (int)((v.Bounds.Max.Y - min.Y) / step.Y));
            int z0 = Math.Max(0, (int)((v.Bounds.Min.Z - min.Z) / step.Z)), z1 = Math.Min(nz - 1, (int)((v.Bounds.Max.Z - min.Z) / step.Z));
            for (int z = z0; z <= z1; z++)
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        var p = min + new Vector3((x + 0.5f) * step.X, (y + 0.5f) * step.Y, (z + 0.5f) * step.Z);
                        float t = v.TemperatureAt(p);
                        int i = x + nx * (y + ny * z);
                        if (t > values[i] && float.IsFinite(t)) values[i] = t;
                    }
        }
        return grid;
    }
}
