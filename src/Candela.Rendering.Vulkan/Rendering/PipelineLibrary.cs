using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace Candela.Rendering.Vulkan;

/// <summary>Formats of a render target's images (dynamic rendering needs them at pipeline creation).</summary>
internal static class TargetFormats
{
    /// <summary>Reverse-Z depth.</summary>
    public const Format Depth = Format.D32Sfloat;

    /// <summary>Instance pick ids.</summary>
    public const Format PickId = Format.R32Uint;

    /// <summary>Pre-exposed scene-referred linear Rec.2020.</summary>
    public const Format Hdr = Format.R16G16B16A16Sfloat;

    /// <summary>Display image: sRGB-encoded values in a UNORM image (what Dear ImGui and PNG files expect).</summary>
    public const Format Display = Format.R8G8B8A8Unorm;
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
}

/// <summary>Description of a graphics pipeline for <see cref="PipelineLibrary"/>.</summary>
internal readonly record struct GraphicsPipelineDesc(
    string Name,
    ShaderVariant Vertex,
    ShaderVariant Fragment,
    VertexLayout Layout,
    Format ColorFormat,
    BlendMode Blend = BlendMode.None,
    bool DepthAttachment = true,
    bool DepthTest = true,
    bool DepthWrite = false,
    CompareOp DepthCompare = CompareOp.GreaterOrEqual,
    PolygonMode Polygon = PolygonMode.Fill,
    bool DynamicCull = true,
    bool DynamicDepthTest = false);

/// <summary>
/// Descriptor set layouts, the pipeline layout shared by every pipeline (set 0 = scene, set 1 = view, 32 bytes of
/// push constants) and the graphics/compute pipelines of the real-time renderer. SPIR-V comes from the
/// <see cref="ShaderCompiler"/> (disk-cached); pipelines go through the context's persisted pipeline cache.
/// All pipelines use dynamic rendering, a dynamic viewport/scissor and (meshes) a dynamic cull mode, so double-sided
/// materials need no pipeline of their own. Front faces are counter-clockwise: the projection flips Y for Vulkan,
/// so a triangle that is counter-clockwise in the world as seen from its front stays counter-clockwise on screen.
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
        try
        {
            ViewSetLayout = CreateViewSetLayout();
            var layouts = stackalloc DescriptorSetLayout[2];
            layouts[ShaderConstants.SetScene] = sceneLayout;
            layouts[ShaderConstants.SetView] = ViewSetLayout;
            var range = new PushConstantRange(ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit | ShaderStageFlags.ComputeBit, 0,
                (uint)sizeof(GpuDrawConstants));
            var info = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 2,
                PSetLayouts = layouts,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &range,
            };
            PipelineLayout layout;
            VkUtil.Check(context.Vk.CreatePipelineLayout(context.Device, &info, null, &layout), "vkCreatePipelineLayout");
            Layout = layout;

            PrepassOpaque = Create(new GraphicsPipelineDesc("depth pre-pass", ShaderPrograms.MeshVertex, ShaderPrograms.Prepass, VertexLayout.Mesh,
                TargetFormats.PickId, DepthWrite: true, DepthCompare: CompareOp.Greater));
            PrepassMask = Create(new GraphicsPipelineDesc("depth pre-pass (alpha mask)", ShaderPrograms.MeshVertex, ShaderPrograms.PrepassAlphaMask,
                VertexLayout.Mesh, TargetFormats.PickId, DepthWrite: true, DepthCompare: CompareOp.Greater));
            ForwardOpaque = Create(new GraphicsPipelineDesc("forward", ShaderPrograms.MeshVertex, ShaderPrograms.Forward, VertexLayout.Mesh,
                TargetFormats.Hdr, DepthCompare: CompareOp.Equal));
            ForwardTransparent = Create(new GraphicsPipelineDesc("forward (transparent)", ShaderPrograms.MeshVertex, ShaderPrograms.ForwardTransparent,
                VertexLayout.Mesh, TargetFormats.Hdr, BlendMode.Premultiplied, DepthCompare: CompareOp.Greater));
            Colorless = Create(new GraphicsPipelineDesc("colorless", ShaderPrograms.MeshVertex, ShaderPrograms.Colorless, VertexLayout.Mesh,
                TargetFormats.Hdr, DepthCompare: CompareOp.Equal));
            if (polygonWireframe)
            {
                WireframePolygon = Create(new GraphicsPipelineDesc("wireframe (polygon mode)", ShaderPrograms.MeshVertex, ShaderPrograms.Wire,
                    VertexLayout.Mesh, TargetFormats.Hdr, DepthAttachment: false, DepthTest: false, Polygon: PolygonMode.Line));
            }

            WireframeEdges = Create(new GraphicsPipelineDesc("wireframe (edge shader)", ShaderPrograms.EdgesVertex, ShaderPrograms.LinesFragment,
                VertexLayout.Edges, TargetFormats.Hdr, BlendMode.Alpha, DepthAttachment: false, DepthTest: false, DynamicCull: false));
            Lines = Create(new GraphicsPipelineDesc("lines", ShaderPrograms.LinesVertex, ShaderPrograms.LinesFragment, VertexLayout.Lines,
                TargetFormats.Display, BlendMode.Alpha, DynamicCull: false));
            Grid = Create(new GraphicsPipelineDesc("grid", ShaderPrograms.FullscreenVertex, ShaderPrograms.Grid, VertexLayout.None,
                TargetFormats.Display, BlendMode.Alpha, DynamicCull: false, DynamicDepthTest: true));
            Tonemap = Create(new GraphicsPipelineDesc("tone mapping", ShaderPrograms.FullscreenVertex, ShaderPrograms.Tonemap, VertexLayout.None,
                TargetFormats.Display, DepthAttachment: false, DepthTest: false, DynamicCull: false));
            ClusterAssign = CreateCompute("cluster light assignment", ShaderPrograms.ClusterAssign);
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

    /// <summary>Layout of set 1 (per view).</summary>
    public DescriptorSetLayout ViewSetLayout { get; private set; }

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

    /// <summary>Binds sets 0 and 1 for <paramref name="bindPoint"/>.</summary>
    public void BindSets(CommandBuffer cmd, PipelineBindPoint bindPoint, DescriptorSet scene, DescriptorSet view)
    {
        var sets = stackalloc DescriptorSet[2];
        sets[ShaderConstants.SetScene] = scene;
        sets[ShaderConstants.SetView] = view;
        _context.Vk.CmdBindDescriptorSets(cmd, bindPoint, Layout, 0, 2, sets, 0, null);
    }

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
    }

    private DescriptorSetLayout CreateViewSetLayout()
    {
        const ShaderStageFlags all = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit | ShaderStageFlags.ComputeBit;
        const ShaderStageFlags clusterStages = ShaderStageFlags.FragmentBit | ShaderStageFlags.ComputeBit;
        var bindings = stackalloc DescriptorSetLayoutBinding[5];
        bindings[0] = new DescriptorSetLayoutBinding(ShaderConstants.BindingFrame, DescriptorType.UniformBuffer, 1, all);
        bindings[1] = new DescriptorSetLayoutBinding(ShaderConstants.BindingClusters, DescriptorType.StorageBuffer, 1, clusterStages);
        bindings[2] = new DescriptorSetLayoutBinding(ShaderConstants.BindingClusterLights, DescriptorType.StorageBuffer, 1, clusterStages);
        bindings[3] = new DescriptorSetLayoutBinding(ShaderConstants.BindingClusterCounter, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);
        bindings[4] = new DescriptorSetLayoutBinding(ShaderConstants.BindingHdr, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit);
        var info = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 5,
            PBindings = bindings,
        };
        DescriptorSetLayout layout;
        VkUtil.Check(_context.Vk.CreateDescriptorSetLayout(_context.Device, &info, null, &layout), "vkCreateDescriptorSetLayout (view)");
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
            var blendAttachment = new PipelineColorBlendAttachmentState
            {
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            };
            if (d.Blend != BlendMode.None)
            {
                blendAttachment.BlendEnable = true;
                blendAttachment.SrcColorBlendFactor = d.Blend == BlendMode.Premultiplied ? BlendFactor.One : BlendFactor.SrcAlpha;
                blendAttachment.DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha;
                blendAttachment.ColorBlendOp = BlendOp.Add;
                blendAttachment.SrcAlphaBlendFactor = BlendFactor.One;
                blendAttachment.DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha;
                blendAttachment.AlphaBlendOp = BlendOp.Add;
            }

            var blend = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = 1,
                PAttachments = &blendAttachment,
            };
            var dynamicStates = stackalloc DynamicState[4];
            uint dynamicCount = 0;
            dynamicStates[dynamicCount++] = DynamicState.Viewport;
            dynamicStates[dynamicCount++] = DynamicState.Scissor;
            if (d.DynamicCull)
                dynamicStates[dynamicCount++] = DynamicState.CullMode;
            if (d.DynamicDepthTest)
                dynamicStates[dynamicCount++] = DynamicState.DepthTestEnable;
            var dynamic = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = dynamicCount,
                PDynamicStates = dynamicStates,
            };
            var colorFormat = d.ColorFormat;
            var rendering = new PipelineRenderingCreateInfo
            {
                SType = StructureType.PipelineRenderingCreateInfo,
                ColorAttachmentCount = 1,
                PColorAttachmentFormats = &colorFormat,
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
}
