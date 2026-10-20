using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace Candela.Rendering.Vulkan;

/// <summary>Formats of a render target's images (dynamic rendering needs them at pipeline creation).</summary>
internal static class TargetFormats
{
    /// <summary>Reverse-Z depth (scene and shadow atlas).</summary>
    public const Format Depth = Format.D32Sfloat;

    /// <summary>Instance pick ids.</summary>
    public const Format PickId = Format.R32Uint;

    /// <summary>Instance index + 1 of every pixel (overlays).</summary>
    public const Format InstanceId = Format.R32Uint;

    /// <summary>Octahedral-encoded surface normals (scene and reflective shadow maps).</summary>
    public const Format Normal = Format.R16G16Sfloat;

    /// <summary>Pre-exposed scene-referred linear Rec.2020.</summary>
    public const Format Hdr = Format.R16G16B16A16Sfloat;

    /// <summary>Display image: sRGB-encoded values in a UNORM image (what Dear ImGui and PNG files expect).</summary>
    public const Format Display = Format.R8G8B8A8Unorm;

    /// <summary>Per-pixel analysis values (lux, dB) of the overlays.</summary>
    public const Format Analysis = Format.R32Sfloat;

    /// <summary>Diffuse reflectance of the reflective shadow maps (linear Rec.2020).</summary>
    public const Format RsmAlbedo = Format.R8G8B8A8Unorm;

    /// <summary>Selection mask (coverage, visible) at display resolution.</summary>
    public const Format SelectionMask = Format.R8G8B8A8Unorm;

    /// <summary>Froxel volumes, indirect irradiance and bloom levels (storage + sampled).</summary>
    public const Format HdrStorage = Format.R16G16B16A16Sfloat;

    /// <summary>Scalar volumes of the atmosphere (smoke density, flame temperature, noise level).</summary>
    public const Format Scalar = Format.R16Sfloat;
}

/// <summary>Vertex input layouts of the pipelines.</summary>
internal enum VertexLayout
{
    /// <summary>No vertex buffer (full-screen triangle).</summary>
    None,

    /// <summary><see cref="GpuVertex"/>, per vertex.</summary>
    Mesh,

    /// <summary>Two <see cref="LineVertex"/> per instance (one segment), six vertices per instance.</summary>
    Lines,

    /// <summary>Two object-space <c>vec3</c> endpoints per instance (mesh edges), six vertices per instance.</summary>
    Edges,
}

/// <summary>Colour blending of a pipeline.</summary>
internal enum BlendMode
{
    None,

    /// <summary>Premultiplied alpha: dst = src + (1 − αsrc)·dst.</summary>
    Premultiplied,

    /// <summary>Straight alpha: dst = αsrc·src + (1 − αsrc)·dst.</summary>
    Alpha,

    /// <summary>Participating media over the scene: dst = src (in-scattering) + αsrc (transmittance)·dst.</summary>
    Transmittance,

    /// <summary>Component-wise maximum (selection mask).</summary>
    Max,
}

/// <summary>Up to three colour attachment formats of a pipeline.</summary>
internal readonly record struct ColorTargets(int Count, Format F0 = Format.Undefined, Format F1 = Format.Undefined, Format F2 = Format.Undefined)
{
    public static ColorTargets None => new(0);

    public static ColorTargets One(Format f) => new(1, f);

    public Format this[int i] => i switch
    {
        0 => F0,
        1 => F1,
        _ => F2,
    };
}

/// <summary>Description of a graphics pipeline for <see cref="PipelineLibrary"/>.</summary>
internal readonly record struct GraphicsPipelineDesc(
    string Name,
    ShaderVariant Vertex,
    ShaderVariant Fragment,
    VertexLayout Layout,
    ColorTargets Colors,
    BlendMode Blend = BlendMode.None,
    bool DepthAttachment = true,
    bool DepthTest = true,
    bool DepthWrite = false,
    CompareOp DepthCompare = CompareOp.GreaterOrEqual,
    PolygonMode Polygon = PolygonMode.Fill,
    bool DynamicCull = true,
    bool DynamicDepthTest = false,
    bool DepthBias = false);

/// <summary>
/// Descriptor set layouts, the pipeline layout shared by every pipeline (set 0 = scene, set 1 = view, set 2 = pass
/// resources of compute passes, 32 bytes of push constants) and the graphics/compute pipelines of the real-time
/// renderer. SPIR-V comes from the <see cref="ShaderCompiler"/> (disk-cached); pipelines go through the context's
/// persisted pipeline cache. All pipelines use dynamic rendering, a dynamic viewport/scissor and (meshes) a dynamic
/// cull mode, so double-sided materials need no pipeline of their own. Front faces are counter-clockwise: the
/// projection flips Y for Vulkan, so a triangle that is counter-clockwise in the world as seen from its front stays
/// counter-clockwise on screen (and in the shadow maps).
/// </summary>
internal sealed unsafe class PipelineLibrary : IDisposable
{
    private readonly VulkanContext _context;
    private readonly ShaderCompiler _compiler;
    private readonly List<Pipeline> _owned = new();
    private readonly Dictionary<ShaderVariant, ShaderModule> _modules = new();

    public PipelineLibrary(VulkanContext context, ShaderCompiler compiler, DescriptorSetLayout sceneLayout, bool polygonWireframe)
    {
        _context = context;
        _compiler = compiler;
        RayTracing = context.Capabilities.RayTracing;
        try
        {
            ViewSetLayout = CreateViewSetLayout();
            PassSetLayout = CreatePassSetLayout();
            var layouts = stackalloc DescriptorSetLayout[3];
            layouts[ShaderConstants.SetScene] = sceneLayout;
            layouts[ShaderConstants.SetView] = ViewSetLayout;
            layouts[ShaderConstants.SetPass] = PassSetLayout;
            var range = new PushConstantRange(ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit | ShaderStageFlags.ComputeBit, 0,
                (uint)sizeof(GpuDrawConstants));
            var info = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 3,
                PSetLayouts = layouts,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &range,
            };
            PipelineLayout layout;
            VkUtil.Check(context.Vk.CreatePipelineLayout(context.Device, &info, null, &layout), "vkCreatePipelineLayout");
            Layout = layout;

            var prepassTargets = new ColorTargets(3, TargetFormats.PickId, TargetFormats.InstanceId, TargetFormats.Normal);
            PrepassOpaque = Create(new GraphicsPipelineDesc("depth pre-pass", ShaderPrograms.MeshVertex, ShaderPrograms.Prepass, VertexLayout.Mesh,
                prepassTargets, DepthWrite: true, DepthCompare: CompareOp.Greater));
            PrepassMask = Create(new GraphicsPipelineDesc("depth pre-pass (alpha mask)", ShaderPrograms.MeshVertex, ShaderPrograms.PrepassAlphaMask,
                VertexLayout.Mesh, prepassTargets, DepthWrite: true, DepthCompare: CompareOp.Greater));
            ForwardOpaque = Create(new GraphicsPipelineDesc("forward", ShaderPrograms.MeshVertex, ShaderPrograms.Forward, VertexLayout.Mesh,
                ColorTargets.One(TargetFormats.Hdr), DepthCompare: CompareOp.Equal));
            ForwardTransparent = Create(new GraphicsPipelineDesc("forward (transparent)", ShaderPrograms.MeshVertex, ShaderPrograms.ForwardTransparent,
                VertexLayout.Mesh, ColorTargets.One(TargetFormats.Hdr), BlendMode.Premultiplied, DepthCompare: CompareOp.Greater));
            Colorless = Create(new GraphicsPipelineDesc("colorless", ShaderPrograms.MeshVertex, ShaderPrograms.Colorless, VertexLayout.Mesh,
                ColorTargets.One(TargetFormats.Hdr), DepthCompare: CompareOp.Equal));
            if (polygonWireframe)
            {
                WireframePolygon = Create(new GraphicsPipelineDesc("wireframe (polygon mode)", ShaderPrograms.MeshVertex, ShaderPrograms.Wire,
                    VertexLayout.Mesh, ColorTargets.One(TargetFormats.Hdr), DepthAttachment: false, DepthTest: false, Polygon: PolygonMode.Line));
            }

            WireframeEdges = Create(new GraphicsPipelineDesc("wireframe (edge shader)", ShaderPrograms.EdgesVertex, ShaderPrograms.LinesFragment,
                VertexLayout.Edges, ColorTargets.One(TargetFormats.Hdr), BlendMode.Alpha, DepthAttachment: false, DepthTest: false, DynamicCull: false));
            Lines = Create(new GraphicsPipelineDesc("lines", ShaderPrograms.LinesVertex, ShaderPrograms.LinesFragment, VertexLayout.Lines,
                ColorTargets.One(TargetFormats.Display), BlendMode.Alpha, DynamicCull: false, DynamicDepthTest: true));
            Grid = Create(new GraphicsPipelineDesc("grid", ShaderPrograms.FullscreenVertex, ShaderPrograms.Grid, VertexLayout.None,
                ColorTargets.One(TargetFormats.Display), BlendMode.Alpha, DynamicCull: false, DynamicDepthTest: true));
            Tonemap = Create(new GraphicsPipelineDesc("tone mapping", ShaderPrograms.FullscreenVertex, ShaderPrograms.Tonemap, VertexLayout.None,
                ColorTargets.One(TargetFormats.Display), DepthAttachment: false, DepthTest: false, DynamicCull: false));
            ClusterAssign = CreateCompute("cluster light assignment", ShaderPrograms.ClusterAssign);

            // Shadow atlas tiles: casters drawn from both sides with a slope-scaled depth bias (dynamic).
            ShadowDepth = Create(new GraphicsPipelineDesc("shadow", ShaderPrograms.ShadowVertex, ShaderPrograms.ShadowFragment, VertexLayout.Mesh,
                ColorTargets.None, DepthWrite: true, DepthCompare: CompareOp.Greater, DepthBias: true));
            ShadowDepthMask = Create(new GraphicsPipelineDesc("shadow (alpha mask)", ShaderPrograms.ShadowVertex, ShaderPrograms.ShadowFragmentMask,
                VertexLayout.Mesh, ColorTargets.None, DepthWrite: true, DepthCompare: CompareOp.Greater, DepthBias: true));
            var rsmTargets = new ColorTargets(2, TargetFormats.RsmAlbedo, TargetFormats.Normal);
            ShadowRsm = Create(new GraphicsPipelineDesc("shadow + RSM", ShaderPrograms.ShadowVertex, ShaderPrograms.ShadowFragmentRsm, VertexLayout.Mesh,
                rsmTargets, DepthWrite: true, DepthCompare: CompareOp.Greater, DepthBias: true));
            ShadowRsmMask = Create(new GraphicsPipelineDesc("shadow + RSM (alpha mask)", ShaderPrograms.ShadowVertex, ShaderPrograms.ShadowFragmentRsmMask,
                VertexLayout.Mesh, rsmTargets, DepthWrite: true, DepthCompare: CompareOp.Greater, DepthBias: true));

            // Overlays and analysis.
            Analysis = Create(new GraphicsPipelineDesc("analysis", ShaderPrograms.MeshVertex, ShaderPrograms.Analysis, VertexLayout.Mesh,
                ColorTargets.One(TargetFormats.Analysis), DepthCompare: CompareOp.Equal));
            SelectionMask = Create(new GraphicsPipelineDesc("selection mask", ShaderPrograms.MeshVertex, ShaderPrograms.Selection, VertexLayout.Mesh,
                ColorTargets.One(TargetFormats.SelectionMask), BlendMode.Max, DepthAttachment: false, DepthTest: false));
            Outline = Create(new GraphicsPipelineDesc("selection outline", ShaderPrograms.FullscreenVertex, ShaderPrograms.Outline, VertexLayout.None,
                ColorTargets.One(TargetFormats.Display), BlendMode.Alpha, DepthAttachment: false, DepthTest: false, DynamicCull: false));
            DepthUpsample = Create(new GraphicsPipelineDesc("overlay depth", ShaderPrograms.FullscreenVertex, ShaderPrograms.DepthUpsample,
                VertexLayout.None, ColorTargets.None, DepthWrite: true, DepthCompare: CompareOp.Always, DynamicCull: false));

            // Volumetrics.
            FroxelInject = CreateCompute("froxel injection", ShaderPrograms.FroxelInject);
            FroxelIntegrate = CreateCompute("froxel integration", ShaderPrograms.FroxelIntegrate);
            VolumeComposite = Create(new GraphicsPipelineDesc("volumetric composite", ShaderPrograms.FullscreenVertex, ShaderPrograms.VolumeComposite,
                VertexLayout.None, ColorTargets.One(TargetFormats.Hdr), BlendMode.Transmittance, DepthAttachment: false, DepthTest: false,
                DynamicCull: false));
            BeamCull = CreateCompute("beam tile culling", ShaderPrograms.BeamCull);
            BeamScatter = CreateCompute("beam single scattering", ShaderPrograms.BeamScatter);
            BeamTemporal = CreateCompute("beam temporal accumulation", ShaderPrograms.BeamTemporal);

            // Post.
            BloomDownsampleFirst = CreateCompute("bloom downsample (first)", ShaderPrograms.BloomDownsampleFirst);
            BloomDownsample = CreateCompute("bloom downsample", ShaderPrograms.BloomDownsample);
            BloomUpsample = CreateCompute("bloom upsample", ShaderPrograms.BloomUpsample);
            Histogram = CreateCompute("luminance histogram", ShaderPrograms.Histogram);
            ExposureReduce = CreateCompute("exposure", ShaderPrograms.ExposureReduce);

            // Indirect light.
            VplGenerate = CreateCompute("virtual point lights", ShaderPrograms.VplGenerate);
            IndirectGather = CreateCompute("indirect gather (RSM)", ShaderPrograms.IndirectGather);
            if (RayTracing)
            {
                RayTracedGi = CreateCompute("indirect (ray traced)", ShaderPrograms.RayTracedGi);
                IndirectFilter = CreateCompute("indirect filter", ShaderPrograms.IndirectFilter);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            DestroyModules();
        }
    }

    /// <summary>The ray-query pipelines exist (device supports ray tracing).</summary>
    public bool RayTracing { get; }

    /// <summary>Layout of set 1 (per view).</summary>
    public DescriptorSetLayout ViewSetLayout { get; private set; }

    /// <summary>Layout of set 2 (per-pass compute resources).</summary>
    public DescriptorSetLayout PassSetLayout { get; private set; }

    /// <summary>Layout shared by every pipeline.</summary>
    public PipelineLayout Layout { get; private set; }

    public Pipeline PrepassOpaque { get; }
    public Pipeline PrepassMask { get; }
    public Pipeline ForwardOpaque { get; }
    public Pipeline ForwardTransparent { get; }
    public Pipeline Colorless { get; }

    /// <summary>Polygon-mode LINE wireframe (default handle when <c>fillModeNonSolid</c> is unavailable).</summary>
    public Pipeline WireframePolygon { get; }

    public Pipeline WireframeEdges { get; }
    public Pipeline Lines { get; }
    public Pipeline Grid { get; }
    public Pipeline Tonemap { get; }
    public Pipeline ClusterAssign { get; }
    public Pipeline ShadowDepth { get; }
    public Pipeline ShadowDepthMask { get; }
    public Pipeline ShadowRsm { get; }
    public Pipeline ShadowRsmMask { get; }
    public Pipeline Analysis { get; }
    public Pipeline SelectionMask { get; }
    public Pipeline Outline { get; }
    public Pipeline DepthUpsample { get; }
    public Pipeline FroxelInject { get; }
    public Pipeline FroxelIntegrate { get; }
    public Pipeline VolumeComposite { get; }

    /// <summary>Beam culling into screen tiles (beam_cull.comp).</summary>
    public Pipeline BeamCull { get; }

    /// <summary>Per-pixel single scattering of the beams (beam_scatter.comp).</summary>
    public Pipeline BeamScatter { get; }

    /// <summary>Temporal accumulation of the beam modulation (beam_temporal.comp).</summary>
    public Pipeline BeamTemporal { get; }
    public Pipeline BloomDownsampleFirst { get; }
    public Pipeline BloomDownsample { get; }
    public Pipeline BloomUpsample { get; }
    public Pipeline Histogram { get; }
    public Pipeline ExposureReduce { get; }
    public Pipeline VplGenerate { get; }
    public Pipeline IndirectGather { get; }

    /// <summary>Ray-query one-bounce GI (default handle without ray tracing).</summary>
    public Pipeline RayTracedGi { get; }

    /// <summary>Edge-aware a-trous filter of the ray-traced GI (default handle without ray tracing).</summary>
    public Pipeline IndirectFilter { get; }

    /// <summary>Binds sets 0 and 1 for <paramref name="bindPoint"/>.</summary>
    public void BindSets(CommandBuffer cmd, PipelineBindPoint bindPoint, DescriptorSet scene, DescriptorSet view)
    {
        var sets = stackalloc DescriptorSet[2];
        sets[ShaderConstants.SetScene] = scene;
        sets[ShaderConstants.SetView] = view;
        _context.Vk.CmdBindDescriptorSets(cmd, bindPoint, Layout, 0, 2, sets, 0, null);
    }

    /// <summary>Binds set 2 (pass resources) for <paramref name="bindPoint"/>.</summary>
    public void BindPassSet(CommandBuffer cmd, PipelineBindPoint bindPoint, DescriptorSet set) =>
        _context.Vk.CmdBindDescriptorSets(cmd, bindPoint, Layout, (uint)ShaderConstants.SetPass, 1, &set, 0, null);

    /// <summary>Pushes the per-draw constants.</summary>
    public void Push(CommandBuffer cmd, in GpuDrawConstants constants)
    {
        fixed (GpuDrawConstants* p = &constants)
        {
            _context.Vk.CmdPushConstants(cmd, Layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit | ShaderStageFlags.ComputeBit, 0,
                (uint)sizeof(GpuDrawConstants), p);
        }
    }

    public void Dispose()
    {
        var vk = _context.Vk;
        foreach (var p in _owned)
            vk.DestroyPipeline(_context.Device, p, null);
        _owned.Clear();
        DestroyModules();
        if (Layout.Handle != 0)
            vk.DestroyPipelineLayout(_context.Device, Layout, null);
        Layout = default;
        if (ViewSetLayout.Handle != 0)
            vk.DestroyDescriptorSetLayout(_context.Device, ViewSetLayout, null);
        ViewSetLayout = default;
        if (PassSetLayout.Handle != 0)
            vk.DestroyDescriptorSetLayout(_context.Device, PassSetLayout, null);
        PassSetLayout = default;
    }

    /// <summary>Storage images, combined image samplers and storage buffers of set 2 (<see cref="CreatePassSetLayout"/>).</summary>
    internal const uint PassSetStorageImages = 3;

    /// <inheritdoc cref="PassSetStorageImages"/>
    internal const uint PassSetSampledImages = 3;

    /// <inheritdoc cref="PassSetStorageImages"/>
    internal const uint PassSetStorageBuffers = 1;

    /// <summary>Storage buffers of set 0 (materials, instances, material slots, lights; the set has update-after-bind).</summary>
    internal const uint SceneSetStorageBuffers = 4;

    /// <summary>
    /// Descriptors of sets 1 and 2 (created without update-after-bind) that one shader stage can access — what the
    /// shared pipeline layout needs from the device's per-stage and per-set limits besides the bindless textures and
    /// the storage buffers of set 0 (checked when the device is chosen; the bindless array is sized to leave room for
    /// them under the update-after-bind limits).
    /// </summary>
    internal static void ViewAndPassDescriptors(out uint sampledImages, out uint storageBuffers, out uint storageImages, out uint uniformBuffers)
    {
        sampledImages = PassSetSampledImages;
        storageBuffers = PassSetStorageBuffers;
        storageImages = PassSetStorageImages;
        uniformBuffers = 0;
        for (uint b = 0; b < ShaderConstants.ViewBindingCount; b++)
        {
            switch (ViewBindingType(b))
            {
                case DescriptorType.CombinedImageSampler:
                    sampledImages++;
                    break;
                case DescriptorType.StorageBuffer:
                    storageBuffers++;
                    break;
                case DescriptorType.UniformBuffer:
                    uniformBuffers++;
                    break;
            }
        }
    }

    /// <summary>Descriptor type of a set-1 binding.</summary>
    public static DescriptorType ViewBindingType(uint binding) => binding switch
    {
        ShaderConstants.BindingFrame => DescriptorType.UniformBuffer,
        ShaderConstants.BindingClusters or ShaderConstants.BindingClusterMasks or ShaderConstants.BindingClusterCounter or
            ShaderConstants.BindingLightViews or ShaderConstants.BindingShadowSlots or ShaderConstants.BindingVpls or
            ShaderConstants.BindingFlameLut or ShaderConstants.BindingNoiseSources or ShaderConstants.BindingRaymarchList or
            ShaderConstants.BindingExposure => DescriptorType.StorageBuffer,
        _ => DescriptorType.CombinedImageSampler,
    };

    private DescriptorSetLayout CreateViewSetLayout()
    {
        const ShaderStageFlags all = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit | ShaderStageFlags.ComputeBit;
        var bindings = stackalloc DescriptorSetLayoutBinding[(int)ShaderConstants.ViewBindingCount];
        for (uint b = 0; b < ShaderConstants.ViewBindingCount; b++)
            bindings[b] = new DescriptorSetLayoutBinding(b, ViewBindingType(b), 1, all);
        var info = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = ShaderConstants.ViewBindingCount,
            PBindings = bindings,
        };
        DescriptorSetLayout layout;
        VkUtil.Check(_context.Vk.CreateDescriptorSetLayout(_context.Device, &info, null, &layout), "vkCreateDescriptorSetLayout (view)");
        return layout;
    }

    private DescriptorSetLayout CreatePassSetLayout()
    {
        const ShaderStageFlags stages = ShaderStageFlags.ComputeBit | ShaderStageFlags.FragmentBit;
        var bindings = stackalloc DescriptorSetLayoutBinding[8];
        uint n = 0;
        // Counted by ViewAndPassDescriptors (PassSet* constants).
        bindings[n++] = new DescriptorSetLayoutBinding(ShaderConstants.BindingPassStorage0, DescriptorType.StorageImage, 1, stages);
        bindings[n++] = new DescriptorSetLayoutBinding(ShaderConstants.BindingPassStorage1, DescriptorType.StorageImage, 1, stages);
        bindings[n++] = new DescriptorSetLayoutBinding(ShaderConstants.BindingPassStorage2, DescriptorType.StorageImage, 1, stages);
        bindings[n++] = new DescriptorSetLayoutBinding(ShaderConstants.BindingPassSampled0, DescriptorType.CombinedImageSampler, 1, stages);
        bindings[n++] = new DescriptorSetLayoutBinding(ShaderConstants.BindingPassSampled1, DescriptorType.CombinedImageSampler, 1, stages);
        bindings[n++] = new DescriptorSetLayoutBinding(ShaderConstants.BindingPassSampled2, DescriptorType.CombinedImageSampler, 1, stages);
        bindings[n++] = new DescriptorSetLayoutBinding(ShaderConstants.BindingPassBuffer, DescriptorType.StorageBuffer, 1, stages);
        if (RayTracing)
            bindings[n++] = new DescriptorSetLayoutBinding(ShaderConstants.BindingPassTlas, DescriptorType.AccelerationStructureKhr, 1, ShaderStageFlags.ComputeBit);
        var info = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = n,
            PBindings = bindings,
        };
        DescriptorSetLayout layout;
        VkUtil.Check(_context.Vk.CreateDescriptorSetLayout(_context.Device, &info, null, &layout), "vkCreateDescriptorSetLayout (pass)");
        return layout;
    }

    private ShaderModule Module(ShaderVariant variant)
    {
        if (_modules.TryGetValue(variant, out var module))
            return module;
        uint[] words = _compiler.Compile(variant.File, variant.Defines);
        fixed (uint* code = words)
        {
            var info = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)(words.Length * sizeof(uint)),
                PCode = code,
            };
            VkUtil.Check(_context.Vk.CreateShaderModule(_context.Device, &info, null, &module), $"vkCreateShaderModule {variant}");
        }

        _modules.Add(variant, module);
        return module;
    }

    private void DestroyModules()
    {
        foreach (var m in _modules.Values)
            _context.Vk.DestroyShaderModule(_context.Device, m, null);
        _modules.Clear();
    }

    private Pipeline CreateCompute(string name, ShaderVariant shader)
    {
        var entry = Marshal.StringToCoTaskMemUTF8("main");
        try
        {
            var info = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = Module(shader),
                    PName = (byte*)entry,
                },
                Layout = Layout,
            };
            Pipeline pipeline;
            VkUtil.Check(_context.Vk.CreateComputePipelines(_context.Device, _context.PipelineCache, 1, &info, null, &pipeline),
                $"vkCreateComputePipelines '{name}'");
            _owned.Add(pipeline);
            _context.SetDebugName(ObjectType.Pipeline, pipeline.Handle, name);
            return pipeline;
        }
        finally
        {
            Marshal.FreeCoTaskMem(entry);
        }
    }

    private Pipeline Create(in GraphicsPipelineDesc d)
    {
        var entry = Marshal.StringToCoTaskMemUTF8("main");
        try
        {
            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit,
                Module = Module(d.Vertex),
                PName = (byte*)entry,
            };
            stages[1] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit,
                Module = Module(d.Fragment),
                PName = (byte*)entry,
            };

            var binding = default(VertexInputBindingDescription);
            var attributes = stackalloc VertexInputAttributeDescription[6];
            uint attributeCount = 0;
            switch (d.Layout)
            {
                case VertexLayout.Mesh:
                    binding = new VertexInputBindingDescription(0, (uint)sizeof(GpuVertex), VertexInputRate.Vertex);
                    attributes[attributeCount++] = new VertexInputAttributeDescription(0, 0, Format.R32G32B32Sfloat, 0);
                    attributes[attributeCount++] = new VertexInputAttributeDescription(1, 0, Format.R32G32B32Sfloat, 12);
                    attributes[attributeCount++] = new VertexInputAttributeDescription(2, 0, Format.R32G32Sfloat, 24);
                    attributes[attributeCount++] = new VertexInputAttributeDescription(3, 0, Format.R32G32B32A32Sfloat, 32);
                    break;
                case VertexLayout.Lines:
                    binding = new VertexInputBindingDescription(0, 2 * (uint)sizeof(LineVertex), VertexInputRate.Instance);
                    attributes[attributeCount++] = new VertexInputAttributeDescription(0, 0, Format.R32G32B32Sfloat, 0);
                    attributes[attributeCount++] = new VertexInputAttributeDescription(1, 0, Format.R32G32B32A32Sfloat, 12);
                    attributes[attributeCount++] = new VertexInputAttributeDescription(2, 0, Format.R32Sfloat, 28);
                    attributes[attributeCount++] = new VertexInputAttributeDescription(3, 0, Format.R32G32B32Sfloat, 32);
                    attributes[attributeCount++] = new VertexInputAttributeDescription(4, 0, Format.R32G32B32A32Sfloat, 44);
                    attributes[attributeCount++] = new VertexInputAttributeDescription(5, 0, Format.R32Sfloat, 60);
                    break;
                case VertexLayout.Edges:
                    binding = new VertexInputBindingDescription(0, 24, VertexInputRate.Instance);
                    attributes[attributeCount++] = new VertexInputAttributeDescription(0, 0, Format.R32G32B32Sfloat, 0);
                    attributes[attributeCount++] = new VertexInputAttributeDescription(1, 0, Format.R32G32B32Sfloat, 12);
                    break;
            }

            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = d.Layout == VertexLayout.None ? 0u : 1u,
                PVertexBindingDescriptions = &binding,
                VertexAttributeDescriptionCount = attributeCount,
                PVertexAttributeDescriptions = attributes,
            };
            var inputAssembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList,
            };
            var viewport = new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                ScissorCount = 1,
            };
            var raster = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = d.Polygon,
                CullMode = CullModeFlags.None,
                FrontFace = FrontFace.CounterClockwise,
                LineWidth = 1f,
                DepthBiasEnable = d.DepthBias,
            };
            var multisample = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit,
            };
            var depth = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = d.DepthTest,
                DepthWriteEnable = d.DepthWrite,
                DepthCompareOp = d.DepthCompare,
                MinDepthBounds = 0f,
                MaxDepthBounds = 1f,
            };
            var blendAttachments = stackalloc PipelineColorBlendAttachmentState[3];
            for (int i = 0; i < d.Colors.Count; i++)
                blendAttachments[i] = BlendState(d.Blend);
            var blend = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = (uint)d.Colors.Count,
                PAttachments = blendAttachments,
            };
            var dynamicStates = stackalloc DynamicState[5];
            uint dynamicCount = 0;
            dynamicStates[dynamicCount++] = DynamicState.Viewport;
            dynamicStates[dynamicCount++] = DynamicState.Scissor;
            if (d.DynamicCull)
                dynamicStates[dynamicCount++] = DynamicState.CullMode;
            if (d.DynamicDepthTest)
                dynamicStates[dynamicCount++] = DynamicState.DepthTestEnable;
            if (d.DepthBias)
                dynamicStates[dynamicCount++] = DynamicState.DepthBias;
            var dynamic = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = dynamicCount,
                PDynamicStates = dynamicStates,
            };
            var colorFormats = stackalloc Format[3];
            for (int i = 0; i < d.Colors.Count; i++)
                colorFormats[i] = d.Colors[i];
            var rendering = new PipelineRenderingCreateInfo
            {
                SType = StructureType.PipelineRenderingCreateInfo,
                ColorAttachmentCount = (uint)d.Colors.Count,
                PColorAttachmentFormats = colorFormats,
                DepthAttachmentFormat = d.DepthAttachment ? TargetFormats.Depth : Format.Undefined,
            };
            var info = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                PNext = &rendering,
                StageCount = 2,
                PStages = stages,
                PVertexInputState = &vertexInput,
                PInputAssemblyState = &inputAssembly,
                PViewportState = &viewport,
                PRasterizationState = &raster,
                PMultisampleState = &multisample,
                PDepthStencilState = d.DepthAttachment ? &depth : null,
                PColorBlendState = &blend,
                PDynamicState = &dynamic,
                Layout = Layout,
            };
            Pipeline pipeline;
            VkUtil.Check(_context.Vk.CreateGraphicsPipelines(_context.Device, _context.PipelineCache, 1, &info, null, &pipeline),
                $"vkCreateGraphicsPipelines '{d.Name}'");
            _owned.Add(pipeline);
            _context.SetDebugName(ObjectType.Pipeline, pipeline.Handle, d.Name);
            return pipeline;
        }
        finally
        {
            Marshal.FreeCoTaskMem(entry);
        }
    }

    private static PipelineColorBlendAttachmentState BlendState(BlendMode mode)
    {
        var state = new PipelineColorBlendAttachmentState
        {
            ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
        };
        switch (mode)
        {
            case BlendMode.Premultiplied:
            case BlendMode.Alpha:
                state.BlendEnable = true;
                state.SrcColorBlendFactor = mode == BlendMode.Premultiplied ? BlendFactor.One : BlendFactor.SrcAlpha;
                state.DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha;
                state.ColorBlendOp = BlendOp.Add;
                state.SrcAlphaBlendFactor = BlendFactor.One;
                state.DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha;
                state.AlphaBlendOp = BlendOp.Add;
                break;
            case BlendMode.Transmittance:
                state.BlendEnable = true;
                state.SrcColorBlendFactor = BlendFactor.One;
                state.DstColorBlendFactor = BlendFactor.SrcAlpha;
                state.ColorBlendOp = BlendOp.Add;
                state.SrcAlphaBlendFactor = BlendFactor.Zero;
                state.DstAlphaBlendFactor = BlendFactor.One;
                state.AlphaBlendOp = BlendOp.Add;
                break;
            case BlendMode.Max:
                state.BlendEnable = true;
                state.SrcColorBlendFactor = BlendFactor.One;
                state.DstColorBlendFactor = BlendFactor.One;
                state.ColorBlendOp = BlendOp.Max;
                state.SrcAlphaBlendFactor = BlendFactor.One;
                state.DstAlphaBlendFactor = BlendFactor.One;
                state.AlphaBlendOp = BlendOp.Max;
                break;
        }

        return state;
    }
}
