using System.Numerics;
using System.Security.Cryptography;
using Candela.Assets;
using Candela.Core.Geometry;
using Candela.Core.Math;
using Candela.Dmx;
using Candela.Gdtf;
using Candela.Photometry;
using Candela.Rendering;
using RenderSpotLight = Candela.Rendering.SpotLight;

namespace Candela.Engine;

/// <summary>
/// Renders pictures of fixture types for the library panel: the fixture's 3D model standing upright (GDTF models hang;
/// the thumbnail turns them over), lit by a neutral three-point studio light (5600 K key, fill and rim), seen from a
/// three-quarter front view over a dark background. <see cref="Render"/> caches square thumbnails in a
/// <see cref="ThumbnailStore"/> keyed by the GDTF file's content; <see cref="RenderOrbitFrame"/> renders the frames of the
/// rotating hover preview (the fixture stays loaded between frames).
/// </summary>
/// <remarks>
/// Needs a renderer of its own (e.g. a second <c>VulkanRenderer</c> on the editor's context): it fills that renderer's
/// world with the fixture and the studio lights. Main thread only.
/// </remarks>
public sealed class FixtureThumbnailRenderer : IDisposable
{
    /// <summary>Thumbnail cache variant name.</summary>
    public const string Variant = "fixture";

    /// <summary>Azimuth of the still thumbnail, rad (camera 35° to the right of the fixture's front).</summary>
    public const float DefaultAzimuth = 35f * MathF.PI / 180f;

    /// <summary>Elevation of the camera above the fixture's centre, rad.</summary>
    public const float Elevation = 18f * MathF.PI / 180f;

    private const float FovYDegrees = 30f;
    private const float KeyLux = 600f;

    private readonly IRenderer _renderer;
    private readonly FixtureLibrary _library;
    private readonly ThumbnailStore? _store;
    private readonly Dictionary<string, string> _hashes = new(StringComparer.Ordinal);
    private readonly List<InstanceHandle> _instances = new();
    private readonly List<MeshHandle> _meshes = new();
    private readonly List<MaterialHandle> _materials = new();
    private IRenderTarget? _target;
    private GdtfArchive? _archive;
    private string? _loadedKey;
    private Aabb _bounds;
    private bool _disposed;

    /// <summary>Creates the renderer; <paramref name="store"/> caches the still thumbnails (null: no cache).</summary>
    public FixtureThumbnailRenderer(IRenderer renderer, FixtureLibrary library, ThumbnailStore? store = null)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _store = store;
    }

    /// <summary>Background luminance, cd/m² (dark grey studio).</summary>
    public float BackgroundLuminance { get; set; } = 0.04f;

    /// <summary>
    /// A square thumbnail of <paramref name="entry"/> (mode <paramref name="mode"/>, default the first), from the cache when
    /// present. A fixture whose model cannot be loaded still gets a (dark) image.
    /// </summary>
    public ImageData Render(FixtureLibraryEntry entry, int size, string? mode = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, ThumbnailStore.MaxSize);
        string? hash = _store is null ? null : ContentHash(entry);
        string variant = mode is null ? Variant : Variant + "-" + VariantSuffix(mode);
        if (hash is not null && _store!.Load(hash, size, variant) is { } cached) return cached;
        var image = RenderOrbitFrame(entry, size, size, DefaultAzimuth, mode);
        if (hash is not null)
        {
            try
            {
                _store!.Save(hash, size, image, variant);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The cache is an optimisation; a read-only disk only costs a re-render next time.
            }
        }
        return image;
    }

    /// <summary>
    /// One frame of the rotating preview: the camera circles the fixture at <paramref name="azimuth"/> radians from its
    /// front (0 = straight in front, positive turns to the right). Not cached.
    /// </summary>
    public ImageData RenderOrbitFrame(FixtureLibraryEntry entry, int width, int height, float azimuth, string? mode = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        Load(entry, mode);

        var bounds = _bounds.IsEmpty ? new Aabb(new Vector3(-0.2f), new Vector3(0.2f)) : _bounds;
        var centre = bounds.Center;
        float radius = MathF.Max(0.05f, 0.5f * bounds.Size.Length());
        float az = float.IsFinite(azimuth) ? azimuth : DefaultAzimuth;
        // frame the box (not its bounding sphere) as seen from this direction, with a margin, so the fixture fills the picture
        var camera = ViewFraming.Frame(bounds, new Vector3(MathF.Sin(az) * MathF.Cos(Elevation), -MathF.Cos(az) * MathF.Cos(Elevation), MathF.Sin(Elevation)),
            FovYDegrees, (float)width / height, margin: 1.12f);
        // key light 600 lx on a mid-grey body ≈ 75 cd/m²; black fixture bodies (≈ 4 %) need about 6 stops less
        camera.Ev100 = 7.5f;

        SetStudioLights(centre, radius, az);
        if (_target is null) _target = _renderer.CreateTarget(width, height);
        else if (_target.Width != width || _target.Height != height) _target.Resize(width, height);
        var view = new ViewOptions
        {
            Shading = ViewShading.Full,
            Overlay = OverlayMode.None,
            ShowGrid = false,
            ShowSelectionOutline = false,
            BackgroundLuminance = BackgroundLuminance,
        };
        var settings = new PreviewSettings
        {
            Shadows = true,
            ShadowMapResolution = 512,
            Volumetrics = false,
            Indirect = IndirectLighting.Off,
            Bloom = false,
            TemporalAccumulation = false,
            AutoExposure = false,
        };
        _renderer.Render(_target, camera, view, settings, 0f);
        return _renderer.ReadPixels(_target);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unload();
        _target?.Dispose();
        _target = null;
    }

    private void Load(FixtureLibraryEntry entry, string? mode)
    {
        string key = entry.FilePath + "|" + entry.LastWriteTimeUtc.Ticks + "|" + mode;
        if (key == _loadedKey) return;
        Unload();
        _loadedKey = key;
        _bounds = Aabb.Empty;
        FixtureProfile profile;
        try
        {
            var type = _library.GetFixtureType(entry);
            profile = FixtureProfile.Compile(type, mode, entry.Specs);
            _archive = GdtfArchive.Open(entry.FilePath, GdtfArchiveLoad.InMemory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or FixtureProfileException or ArgumentException)
        {
            return;
        }

        var models = FixtureModelLoader.Load(entry.FilePath, profile);
        // GDTF models hang (base up, beam down): turn the fixture over so it stands on its base
        var standing = Matrix4x4.CreateRotationX(MathF.PI);
        var world = new Matrix4x4[profile.Geometry.Parts.Count];
        profile.Geometry.ComputeWorldTransforms(world, standing);
        var images = new GdtfImageSource(_archive);
        var perModel = new Dictionary<ModelData, (MeshHandle[] Meshes, MaterialHandle[] Materials)>(ReferenceEqualityComparer.Instance);
        var world3d = _renderer.World;
        foreach (var (part, model) in models.Parts)
        {
            if ((uint)part >= (uint)world.Length) continue;
            if (!perModel.TryGetValue(model, out var res))
            {
                var materials = (model.Materials.Count == 0 ? new[] { ModelImporter.CreateDefaultMaterial() } : model.Materials.ToArray())
                    .Select(m => world3d.CreateMaterial(m, images)).ToArray();
                _materials.AddRange(materials);
                var meshes = new MeshHandle[model.Parts.Count];
                for (int i = 0; i < model.Parts.Count; i++)
                {
                    var mesh = model.Parts[i].Mesh;
                    if (mesh.Positions.Length == 0 || mesh.Indices.Length == 0) continue;
                    meshes[i] = world3d.CreateMesh(mesh);
                    _meshes.Add(meshes[i]);
                }
                perModel[model] = res = (meshes, materials);
            }
            for (int i = 0; i < model.Parts.Count; i++)
            {
                if (!res.Meshes[i].IsValid) continue;
                var m = model.Parts[i].Transform * world[part];
                _instances.Add(world3d.CreateInstance(res.Meshes[i], res.Materials, m, InstanceFlags.CastShadow, 0));
                _bounds.Include(model.Parts[i].Mesh.ComputeBounds().Transform(m));
            }
        }
    }

    private void Unload()
    {
        var world = _renderer.World;
        foreach (var i in _instances) world.DestroyInstance(i);
        foreach (var m in _meshes) world.DestroyMesh(m);
        foreach (var m in _materials) world.DestroyMaterial(m);
        _instances.Clear();
        _meshes.Clear();
        _materials.Clear();
        _archive?.Dispose();
        _archive = null;
        _loadedKey = null;
    }

    private void SetStudioLights(Vector3 centre, float radius, float azimuth)
    {
        float d = MathF.Max(1f, 4f * radius);
        var white = LightColor.FromCct(5600f).Rgb;
        Span<RenderSpotLight> lights = stackalloc RenderSpotLight[3];
        // key: front left of the camera, above; fill: front right, lower and weaker; rim: behind, above
        lights[0] = Studio(centre, d, azimuth - 0.75f, 0.75f, KeyLux, white);
        lights[1] = Studio(centre, d, azimuth + 0.95f, 0.25f, 0.35f * KeyLux, white);
        lights[2] = Studio(centre, d, azimuth + MathF.PI, 0.9f, 0.6f * KeyLux, white);
        _renderer.World.SetLights(lights);
        _renderer.World.SetAtmosphere(new AtmosphereState { BaseScattering = 0f });
        _renderer.World.SetLines(ReadOnlySpan<LineVertex>.Empty);
        _renderer.World.SetNoiseSources(ReadOnlySpan<NoiseSource>.Empty);
    }

    private static RenderSpotLight Studio(Vector3 centre, float distance, float azimuth, float elevation, float lux, Vector3 color)
    {
        var from = new Vector3(MathF.Sin(azimuth) * MathF.Cos(elevation), -MathF.Cos(azimuth) * MathF.Cos(elevation), MathF.Sin(elevation));
        var position = centre + from * distance;
        var direction = Vector3.Normalize(centre - position);
        var up = Vector3.Normalize(Vector3.Cross(Vector3.Cross(direction, Vector3.UnitZ), direction));
        if (!float.IsFinite(up.X)) up = Vector3.UnitY;
        return new RenderSpotLight
        {
            Position = position,
            Direction = direction,
            Up = up,
            Color = color,
            PeakCandela = lux * distance * distance,
            BeamAngle = 50f * MathF.PI / 180f,
            FieldAngle = 80f * MathF.PI / 180f,
            BeamType = Photometry.BeamType.Wash,
            SourceRadius = 0.15f * distance,
            Iris = 1f,
            CastShadows = true,
        };
    }

    private string ContentHash(FixtureLibraryEntry entry)
    {
        string key = $"{entry.FilePath}|{entry.FileSize}|{entry.LastWriteTimeUtc.Ticks}";
        if (_hashes.TryGetValue(key, out var hash)) return hash;
        using (var fs = new FileStream(entry.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            hash = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        _hashes[key] = hash;
        return hash;
    }

    private static string VariantSuffix(string mode)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(mode));
        return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }
}
