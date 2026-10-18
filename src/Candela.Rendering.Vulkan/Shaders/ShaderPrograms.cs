namespace Candela.Rendering.Vulkan;

/// <summary>One compiled shader: a stage file plus macro definitions.</summary>
/// <param name="File">Stage file in <c>Shaders/</c> (.vert, .frag, .comp).</param>
/// <param name="Defines">Macro definitions (name, value).</param>
internal sealed record ShaderVariant(string File, params (string Name, string Value)[] Defines)
{
    public override string ToString() =>
        Defines.Length == 0 ? File : $"{File} [{string.Join(", ", Defines.Select(d => $"{d.Name}={d.Value}"))}]";
}

/// <summary>Every shader variant the renderer uses (the shader test compiles and validates exactly this list).</summary>
internal static class ShaderPrograms
{
    public static readonly ShaderVariant MeshVertex = new("mesh.vert");
    public static readonly ShaderVariant Prepass = new("prepass.frag");
    public static readonly ShaderVariant PrepassAlphaMask = new("prepass.frag", ("ALPHA_MASK", "1"));
    public static readonly ShaderVariant Forward = new("forward.frag");
    public static readonly ShaderVariant ForwardTransparent = new("forward.frag", ("TRANSPARENT", "1"));
    public static readonly ShaderVariant Colorless = new("colorless.frag");
    public static readonly ShaderVariant Wire = new("wire.frag");
    public static readonly ShaderVariant LinesVertex = new("lines.vert");
    public static readonly ShaderVariant EdgesVertex = new("lines.vert", ("MESH_EDGES", "1"));
    public static readonly ShaderVariant LinesFragment = new("lines.frag");
    public static readonly ShaderVariant FullscreenVertex = new("fullscreen.vert");
    public static readonly ShaderVariant Tonemap = new("tonemap.frag");
    public static readonly ShaderVariant Grid = new("grid.frag");
    public static readonly ShaderVariant ClusterAssign = new("cluster.comp");
    public static readonly ShaderVariant ShadowVertex = new("shadow.vert");
    public static readonly ShaderVariant ShadowFragment = new("shadow.frag");
    public static readonly ShaderVariant ShadowFragmentMask = new("shadow.frag", ("ALPHA_MASK", "1"));
    public static readonly ShaderVariant ShadowFragmentRsm = new("shadow.frag", ("RSM", "1"));
    public static readonly ShaderVariant ShadowFragmentRsmMask = new("shadow.frag", ("RSM", "1"), ("ALPHA_MASK", "1"));
    public static readonly ShaderVariant Analysis = new("analysis.frag");
    public static readonly ShaderVariant Selection = new("selection.frag");
    public static readonly ShaderVariant Outline = new("outline.frag");
    public static readonly ShaderVariant DepthUpsample = new("depth_upsample.frag");
    public static readonly ShaderVariant FroxelInject = new("froxel_inject.comp");
    public static readonly ShaderVariant FroxelIntegrate = new("froxel_integrate.comp");
    public static readonly ShaderVariant VolumeComposite = new("volumetric.frag");
    public static readonly ShaderVariant BloomDownsampleFirst = new("bloom_down.comp", ("FIRST", "1"));
    public static readonly ShaderVariant BloomDownsample = new("bloom_down.comp");
    public static readonly ShaderVariant BloomUpsample = new("bloom_up.comp");
    public static readonly ShaderVariant Histogram = new("histogram.comp");
    public static readonly ShaderVariant ExposureReduce = new("exposure.comp");
    public static readonly ShaderVariant VplGenerate = new("vpl.comp");
    public static readonly ShaderVariant IndirectGather = new("gather.comp");
    public static readonly ShaderVariant RayTracedGi = new("rt_gi.comp");
    public static readonly ShaderVariant IndirectFilter = new("gi_filter.comp");

    public static IReadOnlyList<ShaderVariant> All { get; } = new[]
    {
        MeshVertex, Prepass, PrepassAlphaMask, Forward, ForwardTransparent, Colorless, Wire, LinesVertex, EdgesVertex,
        LinesFragment, FullscreenVertex, Tonemap, Grid, ClusterAssign, ShadowVertex, ShadowFragment, ShadowFragmentMask,
        ShadowFragmentRsm, ShadowFragmentRsmMask, Analysis, Selection, Outline, DepthUpsample, FroxelInject, FroxelIntegrate,
        VolumeComposite, BloomDownsampleFirst, BloomDownsample, BloomUpsample, Histogram, ExposureReduce, VplGenerate,
        IndirectGather, RayTracedGi, IndirectFilter,
    };
}
