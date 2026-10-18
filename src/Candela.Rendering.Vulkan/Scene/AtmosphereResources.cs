using System.Numerics;
using Candela.Photometry;
using Silk.NET.Vulkan;

namespace Candela.Rendering.Vulkan;

/// <summary>One scalar 3D field of the atmosphere on the GPU (R16F volume over a world box).</summary>
internal sealed class AtmosphereVolume
{
    public AtmosphereVolume(string name, float minValue, float maxValue)
    {
        Name = name;
        MinValue = minValue;
        MaxValue = maxValue;
    }

    public string Name { get; }

    /// <summary>Values are clamped to this range when converted to half floats.</summary>
    public float MinValue { get; }

    public float MaxValue { get; }

    /// <summary>The volume image (null when the field is absent).</summary>
    public GpuImage? Image { get; set; }

    public Vector3 BoxMin { get; set; }

    public Vector3 BoxMax { get; set; }

    /// <summary>True when the current atmosphere has this field and the image holds it.</summary>
    public bool Present { get; set; }

    /// <summary>Grid waiting to be uploaded by the next frame.</summary>
    public VolumeGrid? Pending { get; set; }
}

/// <summary>
/// GPU side of <see cref="AtmosphereState"/> and the noise sources: smoke density, flame temperature and noise level
/// grids as R16F 3D textures (trilinear sampling; recreated only when a grid's dimensions change, re-uploaded on
/// every <see cref="IRenderWorld.SetAtmosphere"/> since the simulation replaces the values every frame), the
/// blackbody luminance table of the flame emission and the noise source list. Uploads are recorded into the next
/// frame through the staging ring. Main thread only.
/// </summary>
internal sealed unsafe class AtmosphereResources : IDisposable
{
    /// <summary>First temperature of the blackbody table, K (below: no visible emission).</summary>
    public const float FlameLutMinKelvin = 500f;

    /// <summary>Last temperature of the blackbody table, K (hotter values use the last entry).</summary>
    public const float FlameLutMaxKelvin = 4000f;

    /// <summary>Entries of the blackbody table.</summary>
    public const int FlameLutEntries = 1024;

    private static readonly Lazy<Vector4[]> _flameLut = new(BuildFlameLut, LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly VulkanContext _context;
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    private bool _lutUploaded;

    public AtmosphereResources(VulkanContext context)
    {
        _context = context;
        FlameLut = GpuBuffer.Create(context, (ulong)(FlameLutEntries * sizeof(Vector4)), BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
            MemoryUsage.GpuOnly, "blackbody luminance table");
        NoiseSources = new SceneBuffer<Vector4>(context, BufferUsageFlags.StorageBufferBit, "noise sources", 16);
    }

    /// <summary>Smoke density 0..1 (scattering = density × SmokeScatteringAtFullDensity).</summary>
    public AtmosphereVolume Smoke { get; } = new("smoke density", 0f, 65000f);

    /// <summary>Flame temperature, K.</summary>
    public AtmosphereVolume Flame { get; } = new("flame temperature", 0f, 65000f);

    /// <summary>Sound pressure level, dB.</summary>
    public AtmosphereVolume Noise { get; } = new("noise level", -65000f, 65000f);

    /// <summary>Blackbody table: per entry Rec.2020 colour (luminance 1) and luminance (cd/m²).</summary>
    public GpuBuffer FlameLut { get; }

    /// <summary>Noise sources (xyz position, w level at 1 m in dB(A)).</summary>
    public SceneBuffer<Vector4> NoiseSources { get; }

    /// <summary>The atmosphere of the current frame (null = clear air).</summary>
    public AtmosphereState? State { get; private set; }

    /// <summary>Kelvin per table entry.</summary>
    public static float FlameLutStep => (FlameLutMaxKelvin - FlameLutMinKelvin) / (FlameLutEntries - 1);

    /// <summary>The CPU copy of the blackbody table (tests compare the GPU flame against it).</summary>
    public static ReadOnlySpan<Vector4> FlameLutEntriesCpu => _flameLut.Value;

    /// <summary>Stores the atmosphere; its grids are uploaded by the next <see cref="Record"/>.</summary>
    public void Set(AtmosphereState? state)
    {
        State = state;
        Stage(Smoke, state?.SmokeDensity);
        Stage(Flame, state?.FlameTemperature);
        Stage(Noise, state?.NoiseField);
    }

    /// <summary>Replaces the noise sources.</summary>
    public void SetNoiseSources(ReadOnlySpan<NoiseSource> sources)
    {
        NoiseSources.Resize(sources.Length);
        for (int i = 0; i < sources.Length; i++)
        {
            var s = sources[i];
            bool valid = float.IsFinite(s.Position.X) && float.IsFinite(s.Position.Y) && float.IsFinite(s.Position.Z) && float.IsFinite(s.DecibelsAt1m);
            NoiseSources[i] = valid ? new Vector4(s.Position, s.DecibelsAt1m) : new Vector4(0f, 0f, 0f, -1e9f);
        }

        NoiseSources.MarkDirty(0, sources.Length);
    }

    /// <summary>First half of the uploads (buffer barriers into the frame's batch).</summary>
    public bool PrepareUpload(BarrierBatch barriers)
    {
        bool noise = NoiseSources.PrepareUpload(barriers);
        if (!_lutUploaded)
            barriers.Use(FlameLut, BufferUse.TransferDestination);
        return noise || !_lutUploaded;
    }

    /// <summary>Records the uploads (after the barriers of <see cref="PrepareUpload"/> were flushed).</summary>
    public void Record(CommandBuffer cmd, BarrierBatch barriers)
    {
        NoiseSources.RecordUpload(cmd);
        if (!_lutUploaded)
        {
            var lut = _flameLut.Value;
            ulong bytes = (ulong)(lut.Length * sizeof(Vector4));
            var region = _context.Upload.Allocate(bytes, 16);
            fixed (Vector4* src = lut)
                System.Buffer.MemoryCopy(src, region.Pointer, (long)bytes, (long)bytes);
            var copy = new BufferCopy(region.Offset, 0, bytes);
            _context.Vk.CmdCopyBuffer(cmd, region.Buffer, FlameLut.Handle, 1, &copy);
            _lutUploaded = true;
        }

        RecordVolume(cmd, barriers, Smoke);
        RecordVolume(cmd, barriers, Flame);
        RecordVolume(cmd, barriers, Noise);
    }

    public void Dispose()
    {
        Smoke.Image?.Dispose();
        Flame.Image?.Dispose();
        Noise.Image?.Dispose();
        FlameLut.Dispose();
        NoiseSources.Dispose();
    }

    private void Stage(AtmosphereVolume volume, VolumeGrid? grid)
    {
        if (grid is null)
        {
            volume.Pending = null;
            volume.Present = false;
            return;
        }

        string? problem = Validate(grid, _context.Capabilities.MaxImageDimension3D);
        if (problem is not null)
        {
            if (_warned.Add(volume.Name + problem))
                _context.Log(VulkanLogLevel.Warning, $"Atmosphere {volume.Name} grid ignored: {problem}.");
            volume.Pending = null;
            volume.Present = false;
            return;
        }

        volume.Pending = grid;
    }

    private static string? Validate(VolumeGrid grid, uint maxDimension)
    {
        if (grid.SizeX <= 0 || grid.SizeY <= 0 || grid.SizeZ <= 0)
            return "empty grid";
        if (grid.SizeX > maxDimension || grid.SizeY > maxDimension || grid.SizeZ > maxDimension)
            return $"larger than the device's 3D image limit ({maxDimension})";
        long count = (long)grid.SizeX * grid.SizeY * grid.SizeZ;
        if (grid.Values is null || grid.Values.Length < count)
            return $"{grid.Values?.Length ?? 0} values for {grid.SizeX}×{grid.SizeY}×{grid.SizeZ}";
        var size = grid.Max - grid.Min;
        if (!(size.X > 0f && size.Y > 0f && size.Z > 0f) || !float.IsFinite(size.X) || !float.IsFinite(size.Y) || !float.IsFinite(size.Z))
            return "the box is empty or not finite";
        return null;
    }

    private void RecordVolume(CommandBuffer cmd, BarrierBatch barriers, AtmosphereVolume volume)
    {
        var grid = volume.Pending;
        if (grid is null)
            return;
        volume.Pending = null;
        uint w = (uint)grid.SizeX, h = (uint)grid.SizeY, d = (uint)grid.SizeZ;
        if (volume.Image is null || volume.Image.Width != w || volume.Image.Height != h || volume.Image.Depth != d)
        {
            if (volume.Image is not null)
                _context.DeferDispose(volume.Image);
            volume.Image = GpuImage.Create3D(_context, w, h, d, TargetFormats.Scalar,
                ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit, volume.Name);
        }

        int count = grid.SizeX * grid.SizeY * grid.SizeZ;
        var region = _context.Upload.Allocate((ulong)count * 2, 16);
        var dst = new Span<Half>(region.Pointer, count);
        var src = grid.Values.AsSpan(0, count);
        for (int i = 0; i < count; i++)
        {
            float v = src[i];
            dst[i] = float.IsNaN(v) ? Half.Zero : (Half)Math.Clamp(v, volume.MinValue, volume.MaxValue);
        }

        barriers.Use(volume.Image, ImageUse.TransferDestination, discard: true);
        barriers.Flush();
        var copy = new BufferImageCopy
        {
            BufferOffset = region.Offset,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Extent3D(w, h, d),
        };
        _context.Vk.CmdCopyBufferToImage(cmd, region.Buffer, volume.Image.Handle, ImageLayout.TransferDstOptimal, 1, &copy);
        volume.Image.NeedsInitialization = false;
        volume.BoxMin = grid.Min;
        volume.BoxMax = grid.Max;
        volume.Present = true;
    }

    /// <summary>
    /// Blackbody luminance table from <see cref="BlackbodyRadiator"/>: entry i at T = 500 K + i·step holds the
    /// chromaticity as linear Rec.2020 normalised to luminance 1 (xyz) and the luminance in cd/m² (w).
    /// </summary>
    private static Vector4[] BuildFlameLut()
    {
        var lut = new Vector4[FlameLutEntries];
        float step = FlameLutStep;
        for (int i = 0; i < lut.Length; i++)
        {
            float kelvin = FlameLutMinKelvin + i * step;
            var color = BlackbodyRadiator.Color(kelvin).Rgb;
            lut[i] = new Vector4(color, (float)BlackbodyRadiator.Luminance(kelvin));
        }

        return lut;
    }
}
