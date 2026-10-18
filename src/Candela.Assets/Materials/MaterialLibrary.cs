using System.Numerics;
using Candela.Core.Geometry;
using Candela.Core.SceneGraph.Components;

namespace Candela.Assets;

/// <summary>Groups of built-in materials in the UI.</summary>
public enum MaterialCategory
{
    /// <summary>Neutral fallback.</summary>
    General,

    /// <summary>Painted walls and surfaces.</summary>
    Paint,

    /// <summary>Stage and venue floors.</summary>
    Floor,

    /// <summary>Drapes, cycloramas.</summary>
    Fabric,

    /// <summary>Structural materials (concrete).</summary>
    Building,

    /// <summary>Trusses, stands and hardware.</summary>
    Metal,

    /// <summary>Mirrors and glazing.</summary>
    GlassAndMirror,

    /// <summary>Video surfaces.</summary>
    Screen,
}

/// <summary>
/// Definition of a built-in material: physically plausible PBR values with the reference they are based on, its UI
/// name and category, and the procedural texture channels it uses (<c>builtin:{id}/albedo|normal|roughness</c>).
/// </summary>
public sealed class BuiltinMaterial
{
    private readonly Func<MaterialData> _factory;

    internal BuiltinMaterial(string id, string displayName, MaterialCategory category, string description, Vector3 albedo,
        float roughness, float metallic, string reference, IReadOnlyList<string> textureChannels, Func<MaterialData> factory)
    {
        Id = id;
        DisplayName = displayName;
        Category = category;
        Description = description;
        Albedo = albedo;
        Roughness = roughness;
        Metallic = metallic;
        Reference = reference;
        TextureChannels = textureChannels;
        _factory = factory;
    }

    /// <summary>Material id (one of the constants in <see cref="MaterialIds"/>).</summary>
    public string Id { get; }

    /// <summary>Name shown in the UI.</summary>
    public string DisplayName { get; }

    /// <summary>UI group.</summary>
    public MaterialCategory Category { get; }

    /// <summary>One-line description for tooltips.</summary>
    public string Description { get; }

    /// <summary>
    /// Mean linear Rec.709 albedo (dielectrics: diffuse reflectance; metals: F0 specular colour). Textured materials'
    /// albedo maps average to this value.
    /// </summary>
    public Vector3 Albedo { get; }

    /// <summary>Mean perceptual roughness (glTF).</summary>
    public float Roughness { get; }

    /// <summary>0 = dielectric, 1 = metal.</summary>
    public float Metallic { get; }

    /// <summary>Where the values come from (measured ranges they sit in).</summary>
    public string Reference { get; }

    /// <summary>Texture channels this material has (subset of albedo, normal, roughness).</summary>
    public IReadOnlyList<string> TextureChannels { get; }

    /// <summary>
    /// Luminous reflectance (CIE Y of <see cref="Albedo"/>, Rec.709 weights): the factor ρ in L = ρ·E/π for a matte
    /// surface lit with E lux.
    /// </summary>
    public float LuminousReflectance => 0.2126f * Albedo.X + 0.7152f * Albedo.Y + 0.0722f * Albedo.Z;

    /// <summary>A new <see cref="MaterialData"/> for this material (callers may modify it).</summary>
    public MaterialData CreateMaterial() => _factory();
}

/// <summary>
/// The built-in PBR material library: one entry per id in <see cref="MaterialIds"/>, with small procedural tileable
/// textures where they add realism (wood planks, concrete, carpet, scuffed vinyl, LED panel). Textures are generated on
/// first use and cached; they are served as <c>builtin:{material id}/{channel}</c> keys (e.g.
/// <c>builtin:wood-stage/albedo</c>). Values and references are documented in docs/assets.md. Thread-safe.
/// </summary>
public sealed class MaterialLibrary
{
    /// <summary>Albedo channel name (sRGB-encoded colour).</summary>
    public const string AlbedoChannel = "albedo";

    /// <summary>Normal map channel name (tangent space, glTF convention).</summary>
    public const string NormalChannel = "normal";

    /// <summary>Roughness channel name (glTF metallic-roughness packing: G = roughness, B = metallic).</summary>
    public const string RoughnessChannel = "roughness";

    private readonly List<BuiltinMaterial> _materials = new();
    private readonly Dictionary<string, BuiltinMaterial> _byId = new(StringComparer.Ordinal);
    // Full texture key ("builtin:wood-stage/albedo") → generator and channel; filled by the constructor, read-only after,
    // so lookups are lock- and allocation-free.
    private readonly Dictionary<string, (Lazy<ProceduralTextures.TextureSet> Set, string Channel)> _textures = new(StringComparer.Ordinal);

    /// <summary>The shared library (texture cache shared by all users).</summary>
    public static MaterialLibrary Default { get; } = new();

    /// <summary>Creates a library with all built-in materials (textures are generated lazily).</summary>
    public MaterialLibrary()
    {
        Define(MaterialIds.Default, "Default grey", MaterialCategory.General,
            "Neutral mid-grey for surfaces without a material.",
            new Vector3(0.40f), 0.6f, 0f, "Neutral grey (40 % reflectance)", null);

        Define(MaterialIds.PaintedWallWhite, "White painted wall", MaterialCategory.Paint,
            "Matte white emulsion paint.",
            new Vector3(0.80f, 0.80f, 0.78f), 0.85f, 0f, "Matte white paint 0.75–0.85 (IES/CIBSE reflectance tables)", null);

        Define(MaterialIds.PaintedWallBlack, "Black painted wall", MaterialCategory.Paint,
            "Matte black theatre paint.",
            new Vector3(0.04f), 0.8f, 0f, "Matte black paint 0.03–0.05", null);

        Define(MaterialIds.Concrete, "Concrete", MaterialCategory.Building,
            "Fair-faced grey concrete with mottling and pores.",
            new Vector3(0.36f, 0.35f, 0.33f), 0.9f, 0f, "Concrete 0.25–0.40 (IES; physically based albedo charts)",
            (a, r) => ProceduralTextures.Concrete(a, r), Channels(AlbedoChannel, NormalChannel, RoughnessChannel));

        Define(MaterialIds.StageFloorBlack, "Black stage floor", MaterialCategory.Floor,
            "Black vinyl dance floor / painted stage floor with a slight sheen.",
            new Vector3(0.035f), 0.5f, 0f, "Black vinyl 0.03–0.05; satin finish",
            (_, r) => ProceduralTextures.VinylScuffs(r), Channels(RoughnessChannel));

        Define(MaterialIds.DanceFloorWhite, "White dance floor", MaterialCategory.Floor,
            "White vinyl dance floor, semi-gloss.",
            new Vector3(0.75f, 0.75f, 0.74f), 0.35f, 0f, "White vinyl 0.70–0.80; semi-gloss", null);

        Define(MaterialIds.WoodStage, "Wooden stage", MaterialCategory.Floor,
            "Varnished softwood stage boards (125 mm planks).",
            new Vector3(0.33f, 0.21f, 0.11f), 0.55f, 0f, "Light/medium wood 0.2–0.4 (IES; physically based albedo charts)",
            (a, r) => ProceduralTextures.WoodPlanks(a, r), Channels(AlbedoChannel, NormalChannel, RoughnessChannel));

        Define(MaterialIds.MoltonBlack, "Black molton", MaterialCategory.Fabric,
            "Black cotton molton drape: almost no reflection, fully matte.",
            new Vector3(0.025f), 1f, 0f, "Black molton/serge 0.02–0.04 (black velvet 0.01–0.02)", null, doubleSided: true);

        Define(MaterialIds.AluminiumTruss, "Aluminium truss", MaterialCategory.Metal,
            "Mill-finish aluminium tube.",
            new Vector3(0.91f, 0.92f, 0.92f), 0.35f, 1f, "Aluminium F0 ≈ (0.91, 0.92, 0.92) linear (Real-Time Rendering, 4th ed., tab. 9.2)", null);

        Define(MaterialIds.SteelBlack, "Black steel", MaterialCategory.Metal,
            "Black powder-coated steel (stands, clamps, hardware); the coating is a dielectric.",
            new Vector3(0.045f), 0.45f, 0f, "Black satin powder coat 0.04–0.05", null);

        Define(MaterialIds.Mirror, "Mirror", MaterialCategory.GlassAndMirror,
            "Silvered mirror.",
            new Vector3(0.97f, 0.96f, 0.91f), MaterialApproximation.MinimumRoughness, 1f,
            "Silver F0 ≈ (0.97, 0.96, 0.91) linear (Real-Time Rendering, 4th ed., tab. 9.2)", null);

        Define(MaterialIds.Glass, "Clear glass", MaterialCategory.GlassAndMirror,
            "Clear float glass (transmissive, IOR 1.52, 4 % reflection per surface at normal incidence).",
            new Vector3(1f), MaterialApproximation.MinimumRoughness, 0f, "Soda-lime glass n = 1.52, F0 = 0.04", null,
            doubleSided: true, transmission: 1f, ior: 1.52f);

        Define(MaterialIds.LedScreenOff, "LED screen (off)", MaterialCategory.Screen,
            "Switched-off LED wall, 3.9 mm pitch: black louvred mask with glossy SMD packages.",
            new Vector3(0.02f), 0.66f, 0f, "LED panel black level: face reflectance ≈ 2 % (black-face SMD)",
            (a, _) => ProceduralTextures.LedPanel(a), Channels(AlbedoChannel, RoughnessChannel), uvScale: 32f);

        Define(MaterialIds.Cyclorama, "Cyclorama", MaterialCategory.Fabric,
            "White/light-grey seamless cyclorama cloth.",
            new Vector3(0.70f, 0.70f, 0.69f), 0.95f, 0f, "Bleached cyclorama cloth 0.65–0.75", null, doubleSided: true);

        Define(MaterialIds.Carpet, "Carpet", MaterialCategory.Floor,
            "Anthracite cut-pile venue carpet.",
            new Vector3(0.10f, 0.10f, 0.11f), 1f, 0f, "Dark carpet 0.05–0.15",
            (a, r) => ProceduralTextures.Carpet(a, r), Channels(AlbedoChannel, NormalChannel, RoughnessChannel), uvScale: 2f);
    }

    /// <summary>All built-in materials in UI order.</summary>
    public IReadOnlyList<BuiltinMaterial> Materials => _materials;

    /// <summary>Categories in UI order (only those that contain materials).</summary>
    public IReadOnlyList<MaterialCategory> Categories =>
        Enum.GetValues<MaterialCategory>().Where(c => _materials.Any(m => m.Category == c)).ToArray();

    /// <summary>Display name of a category.</summary>
    public static string CategoryDisplayName(MaterialCategory category) => category switch
    {
        MaterialCategory.General => "General",
        MaterialCategory.Paint => "Paint",
        MaterialCategory.Floor => "Floors",
        MaterialCategory.Fabric => "Fabrics & drapes",
        MaterialCategory.Building => "Building",
        MaterialCategory.Metal => "Metal",
        MaterialCategory.GlassAndMirror => "Glass & mirrors",
        MaterialCategory.Screen => "Screens",
        _ => category.ToString(),
    };

    /// <summary>Materials of a category in UI order.</summary>
    public IEnumerable<BuiltinMaterial> InCategory(MaterialCategory category) => _materials.Where(m => m.Category == category);

    /// <summary>The material with this id, or null.</summary>
    public BuiltinMaterial? Find(string? id) => id is not null && _byId.TryGetValue(id, out var m) ? m : null;

    /// <summary>True when <paramref name="id"/> is a built-in material.</summary>
    public bool Contains(string? id) => Find(id) is not null;

    /// <summary>A new material for <paramref name="id"/>; unknown ids get the default grey.</summary>
    public MaterialData GetMaterial(string? id) => (Find(id) ?? _byId[MaterialIds.Default]).CreateMaterial();

    /// <summary>Texture key of a material channel (<c>builtin:wood-stage/albedo</c>).</summary>
    public static string TextureKey(string materialId, string channel) => materialId + "/" + channel;

    /// <summary>
    /// Decodes a <c>builtin:{id}/{channel}</c> key to its procedural texture (generated on first use, then cached).
    /// Unknown materials or channels return null.
    /// </summary>
    public ImageData? LoadTexture(string key)
    {
        if (key is null || !_textures.TryGetValue(key, out var texture)) return null;
        var set = texture.Set.Value;
        return texture.Channel switch
        {
            AlbedoChannel => set.Albedo,
            NormalChannel => set.Normal,
            RoughnessChannel => set.Roughness,
            _ => null,
        };
    }

    private static string[] Channels(params string[] channels) => channels;

    private void Define(string id, string name, MaterialCategory category, string description, Vector3 albedo, float roughness,
        float metallic, string reference, Func<Vector3, float, ProceduralTextures.TextureSet>? textures, string[]? channels = null,
        bool doubleSided = false, float transmission = 0f, float ior = 1.5f, float uvScale = 1f)
    {
        channels ??= Array.Empty<string>();
        bool hasRoughnessMap = channels.Contains(RoughnessChannel);
        MaterialData Factory()
        {
            var m = new MaterialData
            {
                Name = name,
                BaseColorFactor = channels.Contains(AlbedoChannel) ? Vector4.One : new Vector4(albedo, 1f),
                Metallic = metallic,
                Roughness = hasRoughnessMap ? 1f : roughness,
                DoubleSided = doubleSided,
                Transmission = transmission,
                Ior = ior,
                UvScale = new Vector2(uvScale),
            };
            if (channels.Contains(AlbedoChannel)) m.BaseColorTexture = TextureKey(id, AlbedoChannel);
            if (channels.Contains(NormalChannel)) m.NormalTexture = TextureKey(id, NormalChannel);
            if (hasRoughnessMap) m.MetallicRoughnessTexture = TextureKey(id, RoughnessChannel);
            return m;
        }
        var material = new BuiltinMaterial(id, name, category, description, albedo, roughness, metallic, reference, channels, Factory);
        _materials.Add(material);
        _byId[id] = material;
        if (textures is not null)
        {
            var set = new Lazy<ProceduralTextures.TextureSet>(() => textures(albedo, roughness), LazyThreadSafetyMode.ExecutionAndPublication);
            foreach (var channel in channels) _textures[TextureKey(id, channel)] = (set, channel);
        }
    }
}
