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
/// <remarks>
/// <b>Flames.</b> Every flame grid (<see cref="AtmosphereState.FlameTemperature"/> and each of
/// <see cref="AtmosphereState.FlameTemperatures"/>, at most <see cref="MaxFlameGrids"/>) keeps its own box and
/// resolution: the grids are packed side by side into one R16F atlas (stacked along z in columns, a new column when
/// the device's 3D image limit is reached), and a record per grid (box, atlas offset, size) follows the blackbody table
/// in <see cref="FlameLut"/>. The shader samples every grid whose box contains the point (clamped to the grid's own
/// texels, so neighbours never bleed in) and takes the hottest.
/// </remarks>
internal sealed unsafe class AtmosphereResources : IDisposable
{
    /// <summary>First temperature of the blackbody table, K (below: no visible emission).</summary>
    public const float FlameLutMinKelvin = 500f;

    /// <summary>Last temperature of the blackbody table, K (hotter values use the last entry).</summary>
    public const float FlameLutMaxKelvin = 4000f;

    /// <summary>Entries of the blackbody table.</summary>
    public const int FlameLutEntries = 1024;

    /// <summary>Most flame grids drawn at once (further grids are ignored with a warning).</summary>
    public const int MaxFlameGrids = 64;

    /// <summary>Vector4 entries of one flame grid record after the blackbody table: box min, box max, atlas offset, size.</summary>
    public const int FlameRecordSize = 4;

    private static readonly Lazy<Vector4[]> _flameLut = new(BuildFlameLut, LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly VulkanContext _context;
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    private readonly List<VolumeGrid> _flameGrids = new();
    private readonly Vector4[] _flameRecords = new Vector4[MaxFlameGrids * FlameRecordSize];
    private readonly (int X, int Z)[] _flamePlacement = new (int X, int Z)[MaxFlameGrids];
    private bool _lutUploaded;
    private bool _flamesPending;

    public AtmosphereResources(VulkanContext context)
    {
        _context = context;
        FlameLut = GpuBuffer.Create(context, (ulong)((FlameLutEntries + MaxFlameGrids * FlameRecordSize) * sizeof(Vector4)),
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit, MemoryUsage.GpuOnly, "blackbody luminance table");
        NoiseSources = new SceneBuffer<Vector4>(context, BufferUsageFlags.StorageBufferBit, "noise sources", 16);
    }

    /// <summary>Smoke density 0..1 (scattering = density × SmokeScatteringAtFullDensity).</summary>
    public AtmosphereVolume Smoke { get; } = new("smoke density", 0f, 65000f);

    /// <summary>Flame temperature, K: the atlas of every flame grid (box = the union of their boxes).</summary>
    public AtmosphereVolume Flame { get; } = new("flame temperature", 0f, 65000f);

    /// <summary>Flame grids in the atlas (records after the blackbody table).</summary>
    public int FlameGridCount { get; private set; }

    /// <summary>Sound pressure level, dB.</summary>
    public AtmosphereVolume Noise { get; } = new("noise level", -65000f, 65000f);

    /// <summary>
    /// Blackbody table: per entry Rec.2020 colour (luminance 1) and luminance (cd/m²); followed by
    /// <see cref="FlameRecordSize"/> entries per flame grid (box min, box max, atlas offset in texels, size in texels).
    /// </summary>
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
        StageFlames(state);
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
        if (!_lutUploaded || _flamesPending)
            barriers.Use(FlameLut, BufferUse.TransferDestination);
        return noise || !_lutUploaded || _flamesPending;
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
        RecordFlames(cmd, barriers);
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

    /// <summary>Collects the valid flame grids of <paramref name="state"/> for the next upload (warns once about the rest).</summary>
    private void StageFlames(AtmosphereState? state)
    {
        _flameGrids.Clear();
        _flamesPending = true;
        if (state is null)
            return;
        AddFlame(state.FlameTemperature);
        if (state.FlameTemperatures is { } grids)
        {
            for (int i = 0; i < grids.Count; i++)
                AddFlame(grids[i]);
        }
    }

    private void AddFlame(VolumeGrid? grid)
    {
        if (grid is null)
            return;
        string? problem = Validate(grid, _context.Capabilities.MaxImageDimension3D);
        if (problem is null && _flameGrids.Count >= MaxFlameGrids)
            problem = $"more than {MaxFlameGrids} flame grids";
        if (problem is not null)
        {
            if (_warned.Add(Flame.Name + problem))
                _context.Log(VulkanLogLevel.Warning, $"Atmosphere {Flame.Name} grid ignored: {problem}.");
            return;
        }

        _flameGrids.Add(grid);
    }

    /// <summary>
    /// Packs the staged flame grids into the atlas (columns of grids stacked along z), uploads them and their records,
    /// and sets the union box of <see cref="Flame"/>. Grids that do not fit the device's 3D image limit are skipped.
    /// </summary>
    private void RecordFlames(CommandBuffer cmd, BarrierBatch barriers)
    {
        if (!_flamesPending)
            return;
        _flamesPending = false;
        int limit = (int)Math.Min(_context.Capabilities.MaxImageDimension3D, 2048u);
        int column = 0, columnWidth = 0, z = 0, width = 0, height = 0, depth = 0, count = 0;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = 0; i < _flameGrids.Count; i++)
        {
            var g = _flameGrids[i];
            if (z + g.SizeZ > limit)
            {
                column += columnWidth;
                columnWidth = 0;
                z = 0;
            }

            if (column + g.SizeX > limit)
            {
                if (_warned.Add(Flame.Name + "atlas"))
                    _context.Log(VulkanLogLevel.Warning, "Flame grids beyond the device's 3D image limit are not drawn.");
                break;
            }

            _flamePlacement[count] = (column, z);
            int r = count * FlameRecordSize;
            _flameRecords[r] = new Vector4(g.Min, 0f);
            _flameRecords[r + 1] = new Vector4(g.Max, 0f);
            _flameRecords[r + 2] = new Vector4(column, 0f, z, 0f);
            _flameRecords[r + 3] = new Vector4(g.SizeX, g.SizeY, g.SizeZ, 0f);
            min = Vector3.Min(min, g.Min);
            max = Vector3.Max(max, g.Max);
            z += g.SizeZ;
            columnWidth = Math.Max(columnWidth, g.SizeX);
            width = Math.Max(width, column + columnWidth);
            height = Math.Max(height, g.SizeY);
            depth = Math.Max(depth, z);
            count++;
        }

        FlameGridCount = count;
        if (count > 0)
        {
            ulong recordBytes = (ulong)(count * FlameRecordSize * sizeof(Vector4));
            var records = _context.Upload.Allocate(recordBytes, 16);
            fixed (Vector4* src = _flameRecords)
                System.Buffer.MemoryCopy(src, records.Pointer, (long)recordBytes, (long)recordBytes);
            var copy = new BufferCopy(records.Offset, (ulong)(FlameLutEntries * sizeof(Vector4)), recordBytes);
            _context.Vk.CmdCopyBuffer(cmd, records.Buffer, FlameLut.Handle, 1, &copy);
        }

        if (count == 0)
        {
            Flame.Present = false;
            return;
        }

        uint w = (uint)width, h = (uint)height, d = (uint)depth;
        if (Flame.Image is null || Flame.Image.Width != w || Flame.Image.Height != h || Flame.Image.Depth != d)
        {
            if (Flame.Image is not null)
                _context.DeferDispose(Flame.Image);
            Flame.Image = GpuImage.Create3D(_context, w, h, d, TargetFormats.Scalar, ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit,
                Flame.Name);
        }

        long texels = (long)width * height * depth;
        var region = _context.Upload.Allocate((ulong)texels * 2, 16);
        var dst = new Span<Half>(region.Pointer, (int)texels);
        dst.Clear();
        for (int i = 0; i < count; i++)
        {
            var g = _flameGrids[i];
            var (x0, z0) = _flamePlacement[i];
            var src = g.Values.AsSpan(0, g.SizeX * g.SizeY * g.SizeZ);
            for (int gz = 0; gz < g.SizeZ; gz++)
            {
                for (int gy = 0; gy < g.SizeY; gy++)
                {
                    var row = src.Slice((gz * g.SizeY + gy) * g.SizeX, g.SizeX);
                    var target = dst.Slice(((z0 + gz) * height + gy) * width + x0, g.SizeX);
                    for (int gx = 0; gx < row.Length; gx++)
                    {
                        float v = row[gx];
                        target[gx] = float.IsNaN(v) ? Half.Zero : (Half)Math.Clamp(v, Flame.MinValue, Flame.MaxValue);
                    }
                }
            }
        }

        barriers.Use(Flame.Image, ImageUse.TransferDestination, discard: true);
        barriers.Flush();
        var imageCopy = new BufferImageCopy
        {
            BufferOffset = region.Offset,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Extent3D(w, h, d),
        };
        _context.Vk.CmdCopyBufferToImage(cmd, region.Buffer, Flame.Image.Handle, ImageLayout.TransferDstOptimal, 1, &imageCopy);
        Flame.Image.NeedsInitialization = false;
        Flame.BoxMin = min;
        Flame.BoxMax = max;
        Flame.Present = true;
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
