namespace Candela.Rendering.Vulkan;

/// <summary>
/// Constants shared between C# and GLSL (emitted into <c>generated.glsl</c>): descriptor set layout, cluster grid,
/// flag bits. Changing a value here changes the shaders' content hash, so cached SPIR-V is rebuilt automatically.
/// </summary>
internal static class ShaderConstants
{
    // Set 0: scene (one per frame slot; bindless textures last because their count is variable).
    public const int SetScene = 0;
    public const uint BindingMaterials = 0;
    public const uint BindingInstances = 1;
    public const uint BindingMaterialSlots = 2;
    public const uint BindingLights = 3;
    public const uint BindingVertices = 4;
    public const uint BindingEdges = 5;
    public const uint BindingLines = 6;
    public const uint BindingTextures = 7;

    // Set 1: view (one per render target and frame slot).
    public const int SetView = 1;
    public const uint BindingFrame = 0;
    public const uint BindingClusters = 1;
    public const uint BindingClusterLights = 2;
    public const uint BindingClusterCounter = 3;
    public const uint BindingHdr = 4;
    public const uint BindingDepth = 5;

    // Clustered light assignment: 16 × 9 screen tiles × 24 logarithmic depth slices.
    public const uint ClusterCountX = 16;
    public const uint ClusterCountY = 9;
    public const uint ClusterCountZ = 24;
    public const uint ClusterCount = ClusterCountX * ClusterCountY * ClusterCountZ;
    public const uint ClusterThreads = 64;
    public const uint MaxLightsPerCluster = 512;

    // Bindless texture slots of the default textures.
    public const uint TextureWhite = 0;
    public const uint TextureBlack = 1;
    public const uint TextureFlatNormal = 2;

    // Material flags.
    public const uint MaterialAlphaMask = 1u << 0;
    public const uint MaterialAlphaBlend = 1u << 1;
    public const uint MaterialDoubleSided = 1u << 2;
    public const uint MaterialUnlit = 1u << 3;
    public const uint MaterialBaseColorTexture = 1u << 4;
    public const uint MaterialEmissiveTexture = 1u << 5;
    public const uint MaterialNormalTexture = 1u << 6;
    public const uint MaterialMetallicRoughnessTexture = 1u << 7;
    public const uint MaterialOcclusionTexture = 1u << 8;
    public const uint MaterialTransmissive = 1u << 9;

    // Light shapes (Candela.Photometry.BeamShape).
    public const uint ShapeLambertian = 0;
    public const uint ShapeGeneralizedGaussian = 1;
    public const uint ShapeUniformCone = 2;

    // View modes.
    public const uint ViewFull = 0;
    public const uint ViewColorless = 1;
    public const uint ViewWireframe = 2;

    // Tone mappers (tonemap.frag).
    public const uint ToneAgX = 0;
    public const uint ToneAces = 1;
    public const uint ToneNeutral = 2;
    public const uint TonePassthrough = 3;

    // Frame flags.
    public const uint FrameOrthographic = 1u << 0;
    public const uint FrameOverlayDepthTest = 1u << 1;

    /// <summary>All values as GLSL #define pairs.</summary>
    public static IEnumerable<(string Name, long Value)> Defines => new (string, long)[]
    {
        ("SET_SCENE", SetScene),
        ("BINDING_MATERIALS", BindingMaterials),
        ("BINDING_INSTANCES", BindingInstances),
        ("BINDING_MATERIAL_SLOTS", BindingMaterialSlots),
        ("BINDING_LIGHTS", BindingLights),
        ("BINDING_VERTICES", BindingVertices),
        ("BINDING_EDGES", BindingEdges),
        ("BINDING_LINES", BindingLines),
        ("BINDING_TEXTURES", BindingTextures),
        ("SET_VIEW", SetView),
        ("BINDING_FRAME", BindingFrame),
        ("BINDING_CLUSTERS", BindingClusters),
        ("BINDING_CLUSTER_LIGHTS", BindingClusterLights),
        ("BINDING_CLUSTER_COUNTER", BindingClusterCounter),
        ("BINDING_HDR", BindingHdr),
        ("BINDING_DEPTH", BindingDepth),
        ("CLUSTER_COUNT_X", ClusterCountX),
        ("CLUSTER_COUNT_Y", ClusterCountY),
        ("CLUSTER_COUNT_Z", ClusterCountZ),
        ("CLUSTER_THREADS", ClusterThreads),
        ("MAX_LIGHTS_PER_CLUSTER", MaxLightsPerCluster),
        ("TEXTURE_WHITE", TextureWhite),
        ("TEXTURE_BLACK", TextureBlack),
        ("TEXTURE_FLAT_NORMAL", TextureFlatNormal),
        ("MATERIAL_ALPHA_MASK", MaterialAlphaMask),
        ("MATERIAL_ALPHA_BLEND", MaterialAlphaBlend),
        ("MATERIAL_DOUBLE_SIDED", MaterialDoubleSided),
        ("MATERIAL_UNLIT", MaterialUnlit),
        ("MATERIAL_BASE_COLOR_TEXTURE", MaterialBaseColorTexture),
        ("MATERIAL_EMISSIVE_TEXTURE", MaterialEmissiveTexture),
        ("MATERIAL_NORMAL_TEXTURE", MaterialNormalTexture),
        ("MATERIAL_METALLIC_ROUGHNESS_TEXTURE", MaterialMetallicRoughnessTexture),
        ("MATERIAL_OCCLUSION_TEXTURE", MaterialOcclusionTexture),
        ("MATERIAL_TRANSMISSIVE", MaterialTransmissive),
        ("SHAPE_LAMBERTIAN", ShapeLambertian),
        ("SHAPE_GENERALIZED_GAUSSIAN", ShapeGeneralizedGaussian),
        ("SHAPE_UNIFORM_CONE", ShapeUniformCone),
        ("VIEW_FULL", ViewFull),
        ("VIEW_COLORLESS", ViewColorless),
        ("VIEW_WIREFRAME", ViewWireframe),
        ("TONE_AGX", ToneAgX),
        ("TONE_ACES", ToneAces),
        ("TONE_NEUTRAL", ToneNeutral),
        ("TONE_PASSTHROUGH", TonePassthrough),
        ("FRAME_ORTHOGRAPHIC", FrameOrthographic),
        ("FRAME_OVERLAY_DEPTH_TEST", FrameOverlayDepthTest),
        ("INSTANCE_FLAG_NOT_PICKABLE", (long)InstanceFlags.NotPickable),
        ("INSTANCE_FLAG_REFERENCE", (long)InstanceFlags.Reference),
        ("INSTANCE_FLAG_SELECTED", (long)InstanceFlags.Selected),
    };
}
