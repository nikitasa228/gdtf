using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace Candela.Rendering.Vulkan;

/// <summary>
/// The path tracer's pipelines (pt_trace.comp, pt_denoise.comp, pt_display.comp) and their layout: set 0 = the
/// world's scene set, set 1 = the real-time view layout (frame constants, media volumes), set 2 = the path tracer's own
/// set (accumulation images, its acceleration structure, light snapshot, parameters, histogram), the shared 32 bytes
/// of push constants. Created once per renderer, on first use, on devices with ray query. Main thread only.
/// </summary>
internal sealed unsafe class PathTracer : IDisposable
{
    /// <summary>Storage images of set 2 (accumulation, auxiliary, guide, output, input).</summary>
    public const uint StorageImages = 5;

    /// <summary>Storage buffers of set 2 (geometry table, lights, histogram).</summary>
    public const uint StorageBuffers = 3;

    private readonly VulkanContext _context;

    public PathTracer(VulkanContext context, ShaderCompiler compiler, DescriptorSetLayout sceneLayout, DescriptorSetLayout viewLayout)
    {
        _context = context;
        try
        {
            SetLayout = CreateSetLayout();
            var layouts = stackalloc DescriptorSetLayout[3];
            layouts[ShaderConstants.SetScene] = sceneLayout;
            layouts[ShaderConstants.SetView] = viewLayout;
            layouts[ShaderConstants.SetPass] = SetLayout;
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
            VkUtil.Check(context.Vk.CreatePipelineLayout(context.Device, &info, null, &layout), "vkCreatePipelineLayout (path tracer)");
            Layout = layout;
            Trace = CreateCompute(compiler, "path tracer", ShaderPrograms.PathTrace);
            Denoise = CreateCompute(compiler, "path tracer denoiser", ShaderPrograms.PathTraceDenoise);
            Display = CreateCompute(compiler, "path tracer display", ShaderPrograms.PathTraceDisplay);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Layout of set 2.</summary>
    public DescriptorSetLayout SetLayout { get; private set; }

    public PipelineLayout Layout { get; private set; }

    /// <summary>One sample per pixel of a band of rows (pt_trace.comp).</summary>
    public Pipeline Trace { get; private set; }

    /// <summary>Resolve and à-trous iterations of the denoiser (pt_denoise.comp).</summary>
    public Pipeline Denoise { get; private set; }

    /// <summary>Display transform and exposure histogram (pt_display.comp).</summary>
    public Pipeline Display { get; private set; }

    /// <summary>
    /// Why the device cannot run the path tracer, or null when it can: ray query (acceleration structures, buffer
    /// device addresses) and the descriptor limits of its pipeline layout.
    /// </summary>
    public static string? Unsupported(VulkanContext context)
    {
        if (!context.Capabilities.RayTracing)
            return $"the final render (path tracer) needs ray query support (VK_KHR_ray_query and VK_KHR_acceleration_structure), which " +
                   $"'{context.Capabilities.DeviceName}' does not offer: not supported on this GPU";
        PipelineLibrary.ViewAndPassDescriptors(out _, out uint storageBuffers, out uint storageImages, out uint uniforms);
        storageBuffers += StorageBuffers - PipelineLibrary.PassSetStorageBuffers;
        storageImages += StorageImages - PipelineLibrary.PassSetStorageImages;
        uniforms += 1;
        var limits = context.Limits;
        var p12 = new PhysicalDeviceVulkan12Properties { SType = StructureType.PhysicalDeviceVulkan12Properties };
        var properties = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &p12 };
        context.Vk.GetPhysicalDeviceProperties2(context.PhysicalDevice, &properties);
        uint scene = PipelineLibrary.SceneSetStorageBuffers;
        if (limits.MaxPerStageDescriptorStorageBuffers < storageBuffers || limits.MaxPerStageDescriptorStorageImages < storageImages ||
            limits.MaxPerStageDescriptorUniformBuffers < uniforms || limits.MaxDescriptorSetStorageBuffers < storageBuffers ||
            limits.MaxDescriptorSetStorageImages < storageImages || p12.MaxPerStageDescriptorUpdateAfterBindStorageBuffers < scene + storageBuffers ||
            p12.MaxPerStageDescriptorUpdateAfterBindStorageImages < storageImages)
        {
            return $"the final render (path tracer) needs {storageBuffers} storage buffers, {storageImages} storage images and {uniforms} uniform " +
                   $"buffers per shader stage, more than '{context.Capabilities.DeviceName}' offers: not supported on this GPU";
        }

        return null;
    }

    /// <summary>Pushes the per-dispatch constants.</summary>
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
        var device = _context.Device;
        foreach (var p in new[] { Trace, Denoise, Display })
        {
            if (p.Handle != 0)
                vk.DestroyPipeline(device, p, null);
        }

        Trace = Denoise = Display = default;
        if (Layout.Handle != 0)
            vk.DestroyPipelineLayout(device, Layout, null);
        Layout = default;
        if (SetLayout.Handle != 0)
            vk.DestroyDescriptorSetLayout(device, SetLayout, null);
        SetLayout = default;
    }

    private DescriptorSetLayout CreateSetLayout()
    {
        const ShaderStageFlags compute = ShaderStageFlags.ComputeBit;
        var bindings = stackalloc DescriptorSetLayoutBinding[(int)ShaderConstants.PtBindingCount];
        uint n = 0;
        bindings[n++] = new DescriptorSetLayoutBinding(ShaderConstants.BindingPtAccum, DescriptorType.StorageImage, 1, compute);
        bindings[n++] = new DescriptorSetLayoutBinding(ShaderConstants.BindingPtAux, DescriptorType.StorageImage, 1, compute);
        bindings[n++] = new DescriptorSetLayoutBinding(ShaderConstants.BindingPtGuide, DescriptorType.StorageImage, 1, compute);
        bindings[n++] = new DescriptorSetLayoutBinding(ShaderConstants.BindingPtOutput, DescriptorType.StorageImage, 1, compute);
        bindings[n++] = new DescriptorSetLayoutBinding(ShaderConstants.BindingPtTlas, DescriptorType.AccelerationStructureKhr, 1, compute);
        bindings[n++] = new DescriptorSetLayoutBinding(ShaderConstants.BindingPtGeometry, DescriptorType.StorageBuffer, 1, compute);
        bindings[n++] = new DescriptorSetLayoutBinding(ShaderConstants.BindingPtLights, DescriptorType.StorageBuffer, 1, compute);
        bindings[n++] = new DescriptorSetLayoutBinding(ShaderConstants.BindingPtParams, DescriptorType.UniformBuffer, 1, compute);
        bindings[n++] = new DescriptorSetLayoutBinding(ShaderConstants.BindingPtHistogram, DescriptorType.StorageBuffer, 1, compute);
        bindings[n++] = new DescriptorSetLayoutBinding(ShaderConstants.BindingPtInput, DescriptorType.StorageImage, 1, compute);
        var info = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = n,
            PBindings = bindings,
        };
        DescriptorSetLayout layout;
        VkUtil.Check(_context.Vk.CreateDescriptorSetLayout(_context.Device, &info, null, &layout), "vkCreateDescriptorSetLayout (path tracer)");
        return layout;
    }

    private Pipeline CreateCompute(ShaderCompiler compiler, string name, ShaderVariant shader)
    {
        uint[] words = compiler.Compile(shader.File, shader.Defines);
        ShaderModule module;
        fixed (uint* code = words)
        {
            var moduleInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)(words.Length * sizeof(uint)),
                PCode = code,
            };
            VkUtil.Check(_context.Vk.CreateShaderModule(_context.Device, &moduleInfo, null, &module), $"vkCreateShaderModule {shader}");
        }

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
                    Module = module,
                    PName = (byte*)entry,
                },
                Layout = Layout,
            };
            Pipeline pipeline;
            VkUtil.Check(_context.Vk.CreateComputePipelines(_context.Device, _context.PipelineCache, 1, &info, null, &pipeline),
                $"vkCreateComputePipelines '{name}'");
            _context.SetDebugName(ObjectType.Pipeline, pipeline.Handle, name);
            return pipeline;
        }
        finally
        {
            Marshal.FreeCoTaskMem(entry);
            _context.Vk.DestroyShaderModule(_context.Device, module, null);
        }
    }
}
