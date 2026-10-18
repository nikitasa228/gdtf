using System.Numerics;
using Silk.NET.Vulkan;

namespace Candela.Rendering.Vulkan;

/// <summary>
/// The spot-light shadow atlas of a renderer: one reverse-Z depth image (plus, for reflective shadow maps, the
/// diffuse reflectance and normal of what each light sees) shared by every view, carved into square power-of-two
/// tiles. <see cref="PreviewSettings.ShadowMapResolution"/> is the tile size of the most important light; the atlas
/// is twice that wide, so it holds 4 tiles of full size or up to 256 smaller ones (at least 1/8 of the full size).
/// </summary>
/// <remarks>
/// <para><b>Allocation.</b> Tiles are placed on an occupancy grid of the smallest tile size at positions aligned to
/// their own size (buddy-style, no fragmentation between equal sizes). A request reuses the light's tile when its
/// size is within one level of the wanted size (hysteresis); otherwise a new tile is placed, evicting the least
/// recently used tiles of lights that the current frame does not need; when nothing fits even at the smallest size the
/// light stays unshadowed.</para>
/// <para><b>Caching.</b> A tile is re-rendered only when its light's shadow frustum changes (position, axis, or the
/// cone outgrows the frustum, which keeps a 10 % margin for zoom), when a shadow caster inside its frustum is created,
/// destroyed, moved or changes material (<see cref="ShadowCasterChanges"/>), or when reflective-shadow-map data is
/// needed and missing. Intensity, colour and cookie changes never re-render a tile: they are applied when the shadow
/// map and the reflective shadow map are read.</para>
/// <para><b>Frustum.</b> Perspective with the light at the apex, the beam's bounding cone (cut-off, iris, prism spread)
/// as half-angle, clamped to 80° (light beyond that is unshadowed), near plane <see cref="NearPlane"/> so the
/// fixture's own lens and housing never shadow its beam, infinite far plane, reverse-Z (depth = near / z).</para>
/// </remarks>
internal sealed unsafe class ShadowAtlas : IDisposable
{
    /// <summary>Near plane of every shadow frustum, m.</summary>
    public const float NearPlane = 0.1f;

    /// <summary>Most tiles in the atlas.</summary>
    public const int MaxTiles = 256;

    /// <summary>Smallest frustum half-angle, rad (1°).</summary>
    public const float MinHalfAngle = 0.0174533f;

    /// <summary>Largest frustum half-angle, rad (80°).</summary>
    public const float MaxHalfAngle = 1.3962634f;

    private readonly VulkanContext _context;
    private readonly Tile[] _tiles = new Tile[MaxTiles];
    private readonly Dictionary<LightKey, int> _byKey = new(MaxTiles);
    private readonly int[] _renderList = new int[MaxTiles];
    private bool[] _cells = Array.Empty<bool>();
    private int _cellsPerSide;
    private int _cellSize;
    private int _renderCount;

    public ShadowAtlas(VulkanContext context)
    {
        _context = context;
        Slots = new SceneBuffer<GpuShadowSlot>(context, BufferUsageFlags.StorageBufferBit, "shadow slots", MaxTiles);
        Slots.Resize(MaxTiles);
    }

    /// <summary>Atlas depth (D32, reverse-Z), or null before the first shadowed frame.</summary>
    public GpuImage? Depth { get; private set; }

    /// <summary>Reflective shadow map: diffuse reflectance (RGBA8, linear Rec.2020), or null until needed.</summary>
    public GpuImage? RsmAlbedo { get; private set; }

    /// <summary>Reflective shadow map: octahedral normal (RG16F), or null until needed.</summary>
    public GpuImage? RsmNormal { get; private set; }

    /// <summary>Tile records for the shaders (index = tile).</summary>
    public SceneBuffer<GpuShadowSlot> Slots { get; }

    /// <summary>Width and height of the atlas, pixels.</summary>
    public int AtlasSize { get; private set; }

    /// <summary>Tile size of the most important light, pixels.</summary>
    public int TileResolution { get; private set; }

    /// <summary>Smallest tile size, pixels.</summary>
    public int MinTileSize => _cellSize;

    /// <summary>Tiles to (re-)render in the current view, in request order.</summary>
    public ReadOnlySpan<int> PendingRenders => _renderList.AsSpan(0, _renderCount);

    /// <summary>Tiles in use.</summary>
    public int TileCount => _byKey.Count;

    /// <summary>Tile records (for the shadow pass).</summary>
    public ref Tile TileAt(int index) => ref _tiles[index];

    /// <summary>
    /// Sizes the atlas for <paramref name="resolution"/> (rounded to a power of two in 128 … 4096 and to the device
    /// limit) and creates the reflective-shadow-map images when <paramref name="rsm"/> is first needed. Changing the
    /// size drops every tile. Starts a new view: the render list is cleared.
    /// </summary>
    public void Configure(int resolution, bool rsm)
    {
        _renderCount = 0;
        int maxAtlas = (int)Math.Min(_context.Capabilities.MaxImageDimension2D, 8192u);
        int r = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Clamp(resolution, 128, 4096));
        r = Math.Min(r, maxAtlas / 2);
        if (r != TileResolution || Depth is null)
        {
            TileResolution = r;
            AtlasSize = 2 * r;
            _cellSize = Math.Max(r / 8, 32);
            _cellsPerSide = AtlasSize / _cellSize;
            _cells = new bool[_cellsPerSide * _cellsPerSide];
            ResetTiles();
            if (Depth is not null)
                _context.DeferDispose(Depth);
            Depth = GpuImage.Create2D(_context, (uint)AtlasSize, (uint)AtlasSize, TargetFormats.Depth,
                ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit, "shadow atlas", dedicated: true);
            if (RsmAlbedo is not null)
            {
                _context.DeferDispose(RsmAlbedo);
                _context.DeferDispose(RsmNormal!);
                RsmAlbedo = RsmNormal = null;
            }
        }

        if (rsm && RsmAlbedo is null)
        {
            RsmAlbedo = GpuImage.Create2D(_context, (uint)AtlasSize, (uint)AtlasSize, TargetFormats.RsmAlbedo,
                ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit, "reflective shadow map (albedo)", dedicated: true);
            RsmNormal = GpuImage.Create2D(_context, (uint)AtlasSize, (uint)AtlasSize, TargetFormats.Normal,
                ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit, "reflective shadow map (normal)", dedicated: true);
            for (int i = 0; i < MaxTiles; i++)
                _tiles[i].HasRsm = false;
        }
    }

    /// <summary>Invalidates the tiles whose frustum contains a changed shadow caster; consumes the changes.</summary>
    public void ProcessChanges(ShadowCasterChanges changes)
    {
        if (changes.All)
        {
            for (int i = 0; i < MaxTiles; i++)
                _tiles[i].Valid = false;
        }
        else
        {
            var spheres = changes.Spheres;
            for (int i = 0; i < MaxTiles; i++)
            {
                ref var tile = ref _tiles[i];
                if (!tile.Used || !tile.Valid)
                    continue;
                foreach (var s in spheres)
                {
                    if (tile.Intersects(s))
                    {
                        tile.Valid = false;
                        break;
                    }
                }
            }
        }

        changes.Clear();
    }

    /// <summary>
    /// Requests a tile of about <paramref name="wantedSize"/> pixels for a light this frame. Returns the tile index,
    /// or -1 when the atlas is full. Tiles that must be (re-)rendered are added to <see cref="PendingRenders"/>.
    /// </summary>
    public int Request(LightKey key, in GpuLight light, int wantedSize, bool needRsm, ulong frame)
    {
        wantedSize = Math.Clamp(wantedSize, _cellSize, TileResolution);
        ComputeFrustum(light, out var position, out var axis, out float required);
        if (_byKey.TryGetValue(key, out int index))
        {
            ref var tile = ref _tiles[index];
            if (tile.Size * 2 >= wantedSize && tile.Size <= wantedSize * 2)
            {
                tile.LastUsed = frame;
                if (!tile.Matches(position, axis, required))
                {
                    SetFrustum(index, position, axis, required);
                    tile.Valid = false;
                }

                if (needRsm && !tile.HasRsm)
                    tile.Valid = false;
                tile.NeedRsm = needRsm;
                if (!tile.Valid)
                    AddRender(index);
                return index;
            }

            Free(index);
        }

        int slot = FreeSlot();
        if (slot < 0)
            return -1;
        int x = 0, y = 0, size = wantedSize;
        bool placed = false;
        while (!placed)
        {
            for (size = wantedSize; size >= _cellSize; size >>= 1)
            {
                if (TryPlace(size, out x, out y))
                {
                    placed = true;
                    break;
                }
            }

            if (!placed && !EvictLeastRecentlyUsed(frame))
                return -1;
        }

        slot = FreeSlot();
        if (slot < 0)
            return -1;
        ref var t = ref _tiles[slot];
        t = new Tile { Used = true, Key = key, X = x, Y = y, Size = size, LastUsed = frame, NeedRsm = needRsm };
        Occupy(x, y, size, true);
        _byKey[key] = slot;
        SetFrustum(slot, position, axis, required);
        AddRender(slot);
        return slot;
    }

    /// <summary>Marks a tile as rendered (depth and, when wanted, the reflective shadow map).</summary>
    public void MarkRendered(int index, bool withRsm)
    {
        ref var tile = ref _tiles[index];
        tile.Valid = true;
        tile.HasRsm = withRsm;
        UpdateSlotFlags(index);
    }

    public void Dispose()
    {
        Depth?.Dispose();
        RsmAlbedo?.Dispose();
        RsmNormal?.Dispose();
        Slots.Dispose();
    }

    /// <summary>The bounding cone of a light used as its shadow frustum (position, unit axis, half-angle).</summary>
    public static void ComputeFrustum(in GpuLight light, out Vector3 position, out Vector3 axis, out float halfAngle)
    {
        position = light.Position;
        axis = light.Direction;
        halfAngle = Math.Clamp(light.BoundingAngle, MinHalfAngle, MaxHalfAngle);
    }

    private void SetFrustum(int index, Vector3 position, Vector3 axis, float required)
    {
        ref var tile = ref _tiles[index];
        float half = MathF.Min(required * 1.1f + 0.005f, MaxHalfAngle);
        tile.Position = position;
        tile.Axis = axis;
        tile.HalfAngle = half;
        var up = MathF.Abs(axis.Z) < 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        var view = Matrix4x4.CreateLookAt(position, position + axis, up);
        var projection = ReverseZ.Perspective(2f * half, 1f, NearPlane);
        var viewProjection = view * projection;
        Matrix4x4.Invert(viewProjection, out var inverse);
        float tan = MathF.Tan(half);
        Slots[index] = new GpuShadowSlot
        {
            ViewProjection = viewProjection,
            InverseViewProjection = inverse,
            AtlasRect = new Vector4(tile.X, tile.Y, tile.Size, tile.Size) / AtlasSize,
            Position = position,
            Near = NearPlane,
            Axis = axis,
            TanHalfAngle = tan,
            TexelScale = 2f * tan / tile.Size,
            Resolution = tile.Size,
            AtlasTexel = 1f / AtlasSize,
            Flags = tile.HasRsm ? 1u : 0u,
        };
        Slots.MarkDirty(index);
    }

    private void UpdateSlotFlags(int index)
    {
        uint flags = _tiles[index].HasRsm ? 1u : 0u;
        if (Slots[index].Flags != flags)
        {
            Slots[index].Flags = flags;
            Slots.MarkDirty(index);
        }
    }

    private void AddRender(int index)
    {
        for (int i = 0; i < _renderCount; i++)
        {
            if (_renderList[i] == index)
                return;
        }

        _renderList[_renderCount++] = index;
    }

    private int FreeSlot()
    {
        for (int i = 0; i < MaxTiles; i++)
        {
            if (!_tiles[i].Used)
                return i;
        }

        return -1;
    }

    private bool TryPlace(int size, out int x, out int y)
    {
        int n = size / _cellSize;
        for (int cy = 0; cy + n <= _cellsPerSide; cy += n)
        {
            for (int cx = 0; cx + n <= _cellsPerSide; cx += n)
            {
                if (IsFree(cx, cy, n))
                {
                    x = cx * _cellSize;
                    y = cy * _cellSize;
                    return true;
                }
            }
        }

        x = y = 0;
        return false;
    }

    private bool IsFree(int cx, int cy, int n)
    {
        for (int j = 0; j < n; j++)
        {
            int row = (cy + j) * _cellsPerSide + cx;
            for (int i = 0; i < n; i++)
            {
                if (_cells[row + i])
                    return false;
            }
        }

        return true;
    }

    private void Occupy(int x, int y, int size, bool used)
    {
        int cx = x / _cellSize, cy = y / _cellSize, n = size / _cellSize;
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
                _cells[(cy + j) * _cellsPerSide + cx + i] = used;
        }
    }

    private bool EvictLeastRecentlyUsed(ulong frame)
    {
        int victim = -1;
        ulong oldest = ulong.MaxValue;
        for (int i = 0; i < MaxTiles; i++)
        {
            ref var t = ref _tiles[i];
            if (t.Used && t.LastUsed < frame && t.LastUsed < oldest)
            {
                oldest = t.LastUsed;
                victim = i;
            }
        }

        if (victim < 0)
            return false;
        Free(victim);
        return true;
    }

    private void Free(int index)
    {
        ref var tile = ref _tiles[index];
        if (!tile.Used)
            return;
        Occupy(tile.X, tile.Y, tile.Size, false);
        _byKey.Remove(tile.Key);
        for (int i = 0; i < _renderCount; i++)
        {
            if (_renderList[i] == index)
            {
                _renderList[i] = _renderList[--_renderCount];
                break;
            }
        }

        tile = default;
    }

    private void ResetTiles()
    {
        Array.Clear(_tiles);
        _byKey.Clear();
        _renderCount = 0;
    }

    /// <summary>A tile of the atlas.</summary>
    internal struct Tile
    {
        public bool Used;
        public LightKey Key;
        public int X;
        public int Y;
        public int Size;
        public Vector3 Position;
        public Vector3 Axis;
        public float HalfAngle;
        public bool Valid;
        public bool HasRsm;
        public bool NeedRsm;
        public ulong LastUsed;

        /// <summary>The cached frustum still serves a light with this position, axis and required half-angle.</summary>
        public readonly bool Matches(Vector3 position, Vector3 axis, float required) =>
            Vector3.DistanceSquared(position, Position) < 1e-10f && Vector3.Dot(axis, Axis) >= 1f - 1e-6f &&
            HalfAngle >= required && HalfAngle <= required * 1.25f + 0.01f;

        /// <summary>Conservative test of a bounding sphere against the tile's frustum cone (beyond the near plane).</summary>
        public readonly bool Intersects(Vector4 sphere)
        {
            var c = new Vector3(sphere.X, sphere.Y, sphere.Z);
            float r = sphere.W;
            if (!float.IsFinite(r) || !float.IsFinite(c.X) || !float.IsFinite(c.Y) || !float.IsFinite(c.Z))
                return true;
            var v = c - Position;
            float along = Vector3.Dot(v, Axis);
            if (along + r < NearPlane)
                return false;
            float across = MathF.Sqrt(MathF.Max(v.LengthSquared() - along * along, 0f));
            return MathF.Cos(HalfAngle) * across - along * MathF.Sin(HalfAngle) <= r;
        }
    }
}
