namespace Candela.Rendering.Vulkan;

/// <summary>
/// Constants shared between C# and GLSL (emitted into <c>generated.glsl</c>): descriptor set layout, cluster grid,
/// flag bits. Changing a value here changes the shaders' content hash, so cached SPIR-V is rebuilt automatically.
/// </summary>
internal static class ShaderConstants
{
    // Set 0: scene (one per frame slot; the bindless texture array is last because its count is variable).
    public const int SetScene = 0;
    public const uint BindingMaterials = 0;
    public const uint BindingInstances = 1;
    public const uint BindingMaterialSlots = 2;
    public const uint BindingLights = 3;
    public const uint BindingTextures = 4;

    // Set 1: view (one per render target; rebuilt when a referenced resource is replaced).
    public const int SetView = 1;
    public const uint BindingFrame = 0;
    public const uint BindingClusters = 1;
    public const uint BindingClusterLights = 2;
    public const uint BindingClusterCounter = 3;
    public const uint BindingHdr = 4;
    public const uint BindingLightViews = 5;
    public const uint BindingShadowSlots = 6;
    public const uint BindingShadowMap = 7;
    public const uint BindingShadowDepth = 8;
    public const uint BindingRsmAlbedo = 9;
    public const uint BindingRsmNormal = 10;
    public const uint BindingVpls = 11;
    public const uint BindingIndirect = 12;
    public const uint BindingVolume = 13;
    public const uint BindingSmoke = 14;
    public const uint BindingFlame = 15;
    public const uint BindingNoiseField = 16;
    public const uint BindingFlameLut = 17;
    public const uint BindingNoiseSources = 18;
    public const uint BindingGradientLut = 19;
    public const uint BindingSceneDepth = 20;
    public const uint BindingSceneNormals = 21;
    public const uint BindingInstanceIds = 22;
    public const uint BindingBloom = 23;
    public const uint BindingAnalysis = 24;
    public const uint BindingRaymarchList = 25;
    public const uint BindingSelectionMask = 26;
    public const uint BindingExposure = 27;
    public const uint ViewBindingCount = 28;

    // Set 2: per-pass resources of compute passes (storage images, history images, the TLAS).
    public const int SetPass = 2;
    public const uint BindingPassStorage0 = 0;
    public const uint BindingPassStorage1 = 1;
    public const uint BindingPassStorage2 = 2;
    public const uint BindingPassSampled0 = 3;
    public const uint BindingPassSampled1 = 4;
    public const uint BindingPassSampled2 = 5;
    public const uint BindingPassTlas = 6;
    public const uint BindingPassBuffer = 7;

    // Set 2 of the path tracer's pipelines (PathTracer): accumulation images, its acceleration structure, light
    // snapshot, parameters and the exposure histogram.
    public const uint BindingPtAccum = 0;
    public const uint BindingPtAux = 1;
    public const uint BindingPtGuide = 2;
    public const uint BindingPtOutput = 3;
    public const uint BindingPtTlas = 4;
    public const uint BindingPtGeometry = 5;
    public const uint BindingPtLights = 6;
    public const uint BindingPtParams = 7;
    public const uint BindingPtHistogram = 8;
    public const uint BindingPtInput = 9;
    public const uint PtBindingCount = 10;

    /// <summary>Cull-mask bit of the path tracer's light discs (the emitting lens of each spot light).</summary>
    public const uint PtMaskDisc = 0x02;

    /// <summary>Cull-mask bit of the path tracer's light volumes (bounding boxes of each light's cone, light selection).</summary>
    public const uint PtMaskLightVolume = 0x04;

    /// <summary>Instance custom index of the light-disc instance.</summary>
    public const uint PtCustomDisc = 0xFFFFFF;

    /// <summary>Instance custom index of the light-volume instance.</summary>
    public const uint PtCustomVolume = 0xFFFFFE;

    /// <summary>Triangles of each light's lens polygon (area-preserving regular polygon).</summary>
    public const uint PtDiscSegments = 16;

    /// <summary>pt_display.comp (draw.instanceIndex): the source image holds the mean (denoised), not sum + count.</summary>
    public const uint PtDisplayMean = 1u << 0;

    /// <summary>pt_display.comp (draw.instanceIndex): only add the pixels to the luminance histogram.</summary>
    public const uint PtDisplayHistogram = 1u << 1;

    /// <summary>pt_denoise.comp (draw.mode): resolve sum + count into mean + variance (the denoiser's first input).</summary>
    public const uint PtDenoiseResolve = 0;

    /// <summary>pt_denoise.comp (draw.mode): one à-trous iteration with taps draw.materialIndex pixels apart.</summary>
    public const uint PtDenoiseIterate = 1;

    /// <summary>PtParams.flags: the scene has spot lights to sample.</summary>
    public const uint PtFlagLights = 1u << 0;

    /// <summary>PtParams.flags: uniform haze inside the media box.</summary>
    public const uint PtFlagHaze = 1u << 1;

    /// <summary>PtParams.flags: smoke density grid.</summary>
    public const uint PtFlagSmoke = 1u << 2;

    /// <summary>PtParams.flags: flame temperature grids.</summary>
    public const uint PtFlagFlame = 1u << 3;

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

    // Light flags (Light.flags).
    public const uint LightCastShadows = 1u << 0;
    public const uint LightGobo = 1u << 1;
    public const uint LightBlades = 1u << 2;
    public const uint LightPrism = 1u << 3;

    // Per-view light flags (LightView.flags).

    /// <summary>The light is a volumetric beam of the view (per-pixel single scattering; skipped by the froxels).</summary>
    public const uint LightViewBeam = 1u << 0;
    public const uint LightViewRsm = 1u << 1;

    /// <summary>LightView.shadowSlot of a light without a shadow map.</summary>
    public const uint NoShadow = 0xFFFFFFFFu;

    /// <summary>Most prism facets evaluated per light (more are clamped).</summary>
    public const uint MaxPrismFacets = 16;

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

    // Frame features (FrameData.features).
    public const uint FeatureShadows = 1u << 0;
    public const uint FeatureVolumetrics = 1u << 1;
    public const uint FeatureIndirectRsm = 1u << 2;
    public const uint FeatureIndirectRayTraced = 1u << 3;
    public const uint FeatureHaze = 1u << 4;
    public const uint FeatureSmoke = 1u << 5;
    public const uint FeatureFlame = 1u << 6;
    public const uint FeatureNoiseField = 1u << 7;
    public const uint FeatureBloom = 1u << 8;
    public const uint FeatureScaled = 1u << 9;
    public const uint FeatureTemporal = 1u << 10;
    public const uint FeatureNoiseFog = 1u << 11;
    public const uint FeatureVolumeLights = 1u << 12;

    // Overlay modes (FrameData.overlayMode).
    public const uint OverlayNone = 0;
    public const uint OverlayLux = 1;
    public const uint OverlayNoise = 2;
    public const uint OverlayCables = 3;

    // Draw modes (push constant `mode`).
    public const uint DrawModeDefault = 0;
    public const uint DrawModeSelected = 1;

    // Auto exposure histogram.
    public const uint HistogramBins = 128;

    // Metering of the auto exposure (push constant `mode` of exposure.comp).
    public const uint MeteringAverage = 0;
    public const uint MeteringStage = 1;

    /// <summary>log2 luminance (cd/m²) of the first histogram bin.</summary>
    public const float HistogramMinLog2 = -14f;

    /// <summary>log2 luminance (cd/m²) of the end of the last histogram bin.</summary>
    public const float HistogramMaxLog2 = 22f;

    /// <summary>Exposed value below which a light's contribution counts as negligible (sets its range).</summary>
    public const float LightRangeEpsilon = 2e-5f;

    /// <summary>Beam culling tile edge in half-resolution pixels (twice that at the internal resolution).</summary>
    public const int BeamTileSize = 16;

    /// <summary>Statistics words in front of the beam tile masks (pairs, busiest tile, tiles with beams, …).</summary>
    public const uint BeamMaskHeader = 16;

    /// <summary>Most beams per view (32-bit mask words per tile × 32).</summary>
    public const int MaxBeams = 8192;

    /// <summary>Mask words of the most beams (shared memory of the culling work group).</summary>
    public const int MaxBeamWords = MaxBeams / 32;

    /// <summary>FrameData.beamFlags: a valid beam history exists (one modulation sample per segment instead of two).</summary>
    public const uint BeamFlagHistory = 1u << 0;

    /// <summary>All values as GLSL #define pairs.</summary>
    public static IEnumerable<(string Name, string Value)> Defines => new (string, string)[]
    {
        ("SET_SCENE", I(SetScene)),
        ("BINDING_MATERIALS", I(BindingMaterials)),
        ("BINDING_INSTANCES", I(BindingInstances)),
        ("BINDING_MATERIAL_SLOTS", I(BindingMaterialSlots)),
        ("BINDING_LIGHTS", I(BindingLights)),
        ("BINDING_TEXTURES", I(BindingTextures)),
        ("SET_VIEW", I(SetView)),
        ("BINDING_FRAME", I(BindingFrame)),
        ("BINDING_CLUSTERS", I(BindingClusters)),
        ("BINDING_CLUSTER_LIGHTS", I(BindingClusterLights)),
        ("BINDING_CLUSTER_COUNTER", I(BindingClusterCounter)),
        ("BINDING_HDR", I(BindingHdr)),
        ("BINDING_LIGHT_VIEWS", I(BindingLightViews)),
        ("BINDING_SHADOW_SLOTS", I(BindingShadowSlots)),
        ("BINDING_SHADOW_MAP", I(BindingShadowMap)),
        ("BINDING_SHADOW_DEPTH", I(BindingShadowDepth)),
        ("BINDING_RSM_ALBEDO", I(BindingRsmAlbedo)),
        ("BINDING_RSM_NORMAL", I(BindingRsmNormal)),
        ("BINDING_VPLS", I(BindingVpls)),
        ("BINDING_INDIRECT", I(BindingIndirect)),
        ("BINDING_VOLUME", I(BindingVolume)),
        ("BINDING_SMOKE", I(BindingSmoke)),
        ("BINDING_FLAME", I(BindingFlame)),
        ("BINDING_NOISE_FIELD", I(BindingNoiseField)),
        ("BINDING_FLAME_LUT", I(BindingFlameLut)),
        ("BINDING_NOISE_SOURCES", I(BindingNoiseSources)),
        ("BINDING_GRADIENT_LUT", I(BindingGradientLut)),
        ("BINDING_SCENE_DEPTH", I(BindingSceneDepth)),
        ("BINDING_SCENE_NORMALS", I(BindingSceneNormals)),
        ("BINDING_INSTANCE_IDS", I(BindingInstanceIds)),
        ("BINDING_BLOOM", I(BindingBloom)),
        ("BINDING_ANALYSIS", I(BindingAnalysis)),
        ("BINDING_RAYMARCH_LIST", I(BindingRaymarchList)),
        ("BINDING_SELECTION_MASK", I(BindingSelectionMask)),
        ("BINDING_EXPOSURE", I(BindingExposure)),
        ("SET_PASS", I(SetPass)),
        ("BINDING_PASS_STORAGE0", I(BindingPassStorage0)),
        ("BINDING_PASS_STORAGE1", I(BindingPassStorage1)),
        ("BINDING_PASS_STORAGE2", I(BindingPassStorage2)),
        ("BINDING_PASS_SAMPLED0", I(BindingPassSampled0)),
        ("BINDING_PASS_SAMPLED1", I(BindingPassSampled1)),
        ("BINDING_PASS_SAMPLED2", I(BindingPassSampled2)),
        ("BINDING_PASS_TLAS", I(BindingPassTlas)),
        ("BINDING_PASS_BUFFER", I(BindingPassBuffer)),
        ("BINDING_PT_ACCUM", I(BindingPtAccum)),
        ("BINDING_PT_AUX", I(BindingPtAux)),
        ("BINDING_PT_GUIDE", I(BindingPtGuide)),
        ("BINDING_PT_OUTPUT", I(BindingPtOutput)),
        ("BINDING_PT_TLAS", I(BindingPtTlas)),
        ("BINDING_PT_GEOMETRY", I(BindingPtGeometry)),
        ("BINDING_PT_LIGHTS", I(BindingPtLights)),
        ("BINDING_PT_PARAMS", I(BindingPtParams)),
        ("BINDING_PT_HISTOGRAM", I(BindingPtHistogram)),
        ("BINDING_PT_INPUT", I(BindingPtInput)),
        ("PT_MASK_SCENE", I(RayTracingScene.SceneMask) + "u"),
        ("PT_MASK_SHADOW", I(RayTracingScene.ShadowCasterMask) + "u"),
        ("PT_MASK_DISC", I(PtMaskDisc) + "u"),
        ("PT_MASK_LIGHT_VOLUME", I(PtMaskLightVolume) + "u"),
        ("PT_CUSTOM_DISC", I(PtCustomDisc) + "u"),
        ("PT_CUSTOM_VOLUME", I(PtCustomVolume) + "u"),
        ("PT_DISC_SEGMENTS", I(PtDiscSegments) + "u"),
        ("PT_DISPLAY_MEAN", I(PtDisplayMean) + "u"),
        ("PT_DISPLAY_HISTOGRAM", I(PtDisplayHistogram) + "u"),
        ("PT_DENOISE_RESOLVE", I(PtDenoiseResolve) + "u"),
        ("PT_DENOISE_ITERATE", I(PtDenoiseIterate) + "u"),
        ("PT_FLAG_LIGHTS", I(PtFlagLights) + "u"),
        ("PT_FLAG_HAZE", I(PtFlagHaze) + "u"),
        ("PT_FLAG_SMOKE", I(PtFlagSmoke) + "u"),
        ("PT_FLAG_FLAME", I(PtFlagFlame) + "u"),
        ("CLUSTER_COUNT_X", I(ClusterCountX)),
        ("CLUSTER_COUNT_Y", I(ClusterCountY)),
        ("CLUSTER_COUNT_Z", I(ClusterCountZ)),
        ("CLUSTER_THREADS", I(ClusterThreads)),
        ("MAX_LIGHTS_PER_CLUSTER", I(MaxLightsPerCluster)),
        ("TEXTURE_WHITE", I(TextureWhite)),
        ("TEXTURE_BLACK", I(TextureBlack)),
        ("TEXTURE_FLAT_NORMAL", I(TextureFlatNormal)),
        ("MATERIAL_ALPHA_MASK", I(MaterialAlphaMask)),
        ("MATERIAL_ALPHA_BLEND", I(MaterialAlphaBlend)),
        ("MATERIAL_DOUBLE_SIDED", I(MaterialDoubleSided)),
        ("MATERIAL_UNLIT", I(MaterialUnlit)),
        ("MATERIAL_BASE_COLOR_TEXTURE", I(MaterialBaseColorTexture)),
        ("MATERIAL_EMISSIVE_TEXTURE", I(MaterialEmissiveTexture)),
        ("MATERIAL_NORMAL_TEXTURE", I(MaterialNormalTexture)),
        ("MATERIAL_METALLIC_ROUGHNESS_TEXTURE", I(MaterialMetallicRoughnessTexture)),
        ("MATERIAL_OCCLUSION_TEXTURE", I(MaterialOcclusionTexture)),
        ("MATERIAL_TRANSMISSIVE", I(MaterialTransmissive)),
        ("SHAPE_LAMBERTIAN", I(ShapeLambertian)),
        ("SHAPE_GENERALIZED_GAUSSIAN", I(ShapeGeneralizedGaussian)),
        ("SHAPE_UNIFORM_CONE", I(ShapeUniformCone)),
        ("LIGHT_CAST_SHADOWS", I(LightCastShadows)),
        ("LIGHT_GOBO", I(LightGobo)),
        ("LIGHT_BLADES", I(LightBlades)),
        ("LIGHT_PRISM", I(LightPrism)),
        ("LIGHT_VIEW_BEAM", I(LightViewBeam)),
        ("LIGHT_VIEW_RSM", I(LightViewRsm)),
        ("NO_SHADOW", I(NoShadow) + "u"),
        ("MAX_PRISM_FACETS", I(MaxPrismFacets)),
        ("VIEW_FULL", I(ViewFull)),
        ("VIEW_COLORLESS", I(ViewColorless)),
        ("VIEW_WIREFRAME", I(ViewWireframe)),
        ("TONE_AGX", I(ToneAgX)),
        ("TONE_ACES", I(ToneAces)),
        ("TONE_NEUTRAL", I(ToneNeutral)),
        ("TONE_PASSTHROUGH", I(TonePassthrough)),
        ("FRAME_ORTHOGRAPHIC", I(FrameOrthographic)),
        ("FEATURE_SHADOWS", I(FeatureShadows)),
        ("FEATURE_VOLUMETRICS", I(FeatureVolumetrics)),
        ("FEATURE_INDIRECT_RSM", I(FeatureIndirectRsm)),
        ("FEATURE_INDIRECT_RAY_TRACED", I(FeatureIndirectRayTraced)),
        ("FEATURE_HAZE", I(FeatureHaze)),
        ("FEATURE_SMOKE", I(FeatureSmoke)),
        ("FEATURE_FLAME", I(FeatureFlame)),
        ("FEATURE_NOISE_FIELD", I(FeatureNoiseField)),
        ("FEATURE_BLOOM", I(FeatureBloom)),
        ("FEATURE_SCALED", I(FeatureScaled)),
        ("FEATURE_TEMPORAL", I(FeatureTemporal)),
        ("FEATURE_NOISE_FOG", I(FeatureNoiseFog)),
        ("FEATURE_VOLUME_LIGHTS", I(FeatureVolumeLights)),
        ("OVERLAY_NONE", I(OverlayNone)),
        ("OVERLAY_LUX", I(OverlayLux)),
        ("OVERLAY_NOISE", I(OverlayNoise)),
        ("OVERLAY_CABLES", I(OverlayCables)),
        ("DRAW_MODE_DEFAULT", I(DrawModeDefault)),
        ("DRAW_MODE_SELECTED", I(DrawModeSelected)),
        ("BEAM_TILE_SIZE", I(BeamTileSize)),
        ("BEAM_MASK_HEADER", I(BeamMaskHeader) + "u"),
        ("MAX_BEAM_WORDS", I(MaxBeamWords)),
        ("BEAM_FLAG_HISTORY", I(BeamFlagHistory) + "u"),
        ("BEAM_TABLE_Y_STEPS", I(BeamScatteringTable.YSteps)),
        ("BEAM_TABLE_V_STEPS", I(BeamScatteringTable.VSteps)),
        ("BEAM_TABLE_LAYERS", I(BeamScatteringTable.Layers)),
        ("HISTOGRAM_BINS", I(HistogramBins)),
        ("METERING_AVERAGE", I(MeteringAverage) + "u"),
        ("METERING_STAGE", I(MeteringStage) + "u"),
        ("HISTOGRAM_MIN_LOG2", F(HistogramMinLog2)),
        ("HISTOGRAM_MAX_LOG2", F(HistogramMaxLog2)),
        ("INSTANCE_FLAG_CAST_SHADOW", I((long)InstanceFlags.CastShadow)),
        ("INSTANCE_FLAG_NOT_PICKABLE", I((long)InstanceFlags.NotPickable)),
        ("INSTANCE_FLAG_REFERENCE", I((long)InstanceFlags.Reference)),
        ("INSTANCE_FLAG_SELECTED", I((long)InstanceFlags.Selected)),
        ("INSTANCE_FLAG_CABLE_HIGHLIGHT", I((long)InstanceFlags.CableHighlight)),
    };

    private static string I(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string F(float value) => value.ToString("0.0########", System.Globalization.CultureInfo.InvariantCulture);
}
