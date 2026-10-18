# Real-time renderer (`Candela.Rendering.Vulkan`)

The Vulkan 1.3 implementation of the `Candela.Rendering` contracts (`IRenderer`, `IRenderWorld`, `IRenderTarget`).
**Stage 1** (this document) is the foundation and physically correct *direct* light: a shared Vulkan context, run-time
GLSL compilation, a retained GPU scene with bindless textures, a small pass graph, clustered photometric spot lights,
tone mapping, the Colorless and Wireframe views, the ground grid, lines and picking. Stage 2 adds shadows, cookies,
volumetrics in haze, bloom, the selection outline and the analysis overlays; stage 3 the path tracer. The code is laid
out so those plug in without reworking stage 1 (see [Stage 2 and 3](#stage-2-and-3)).

```
src/Candela.Rendering.Vulkan
  Core/         VulkanContext (+ options, device selection, capabilities), VulkanSwapchain, FrameScheduler,
                DeferredDeletionQueue, PipelineCacheStore, DebugMessenger, VkUtil
  Memory/       DeviceMemoryAllocator, GpuBuffer, GpuImage, UploadRing
  Graph/        RenderPass / PassSequence / RenderStage, resource-state tracking and batched barriers
  Scene/        VulkanRenderWorld, SceneBuffer<T>, TextureStore, MeshProcessor, LightPacker, MaterialPacker, handles
  Rendering/    VulkanRenderer, VulkanRenderTarget, the passes, PipelineLibrary, DrawList, ReverseZ
  Shaders/      GLSL 460 sources (embedded) + ShaderCompiler, ShaderLibrary, GpuStructs, ShaderConstants
  Diagnostics/  ProceduralTestScene, ProceduralMeshes, ImageFiles (CLI, tests, golden images)
src/Candela.Cli            candela render-test
tests/Candela.Rendering.Vulkan.Tests
```

## Conventions and units

| | |
|---|---|
| World | SI units, metres, right-handed, **Z up** |
| Matrices | `System.Numerics` row-vector convention (`v' = v · M`), uploaded verbatim; GLSL reads them transposed, so `M * v` in GLSL equals `v * M` in C# |
| Camera | `RenderCamera.View` looks down −Z with +Y up; the renderer builds the projection |
| Depth | **reverse-Z**, `D32_SFLOAT`, near → 1, far → 0, compare `GREATER`; perspective has an **infinite far plane** (`depth = near / −z_view`), orthographic is a finite linear range |
| Working colour space | **linear Rec.2020** everywhere in the HDR pipeline |
| Light colour | chromaticity as linear Rec.2020 normalised to luminance Y = 1 (`LightColor`), magnitude separate (`PeakCandela`) |
| Material colours | `MaterialData` colours are linear Rec.709 and are converted to Rec.2020 on the CPU; colour textures are sampled through `_SRGB` formats (decoded to linear Rec.709 by the sampler) and converted in the shader |
| Scene luminance | cd/m²; the HDR image stores **luminance × exposure** (pre-exposed) in `RGBA16F` |
| Display image | `RGBA8_UNORM` holding sRGB-encoded values, top row first |

## Architecture

### Vulkan context (`VulkanContext`, public)

One context per GPU, shared by the renderer, the editor's swapchain and the Dear ImGui backend.

* **Instance**: API 1.3, application "Candela". Instance extensions come from the caller
  (`VulkanContextOptions.InstanceExtensions`, e.g. `glfwGetRequiredInstanceExtensions`); `VK_EXT_debug_utils` is added
  when available (object names and pass labels show up in RenderDoc / Nsight).
* **Validation**: `VK_LAYER_KHRONOS_validation` with a debug-utils messenger when `EnableValidation` is true, or when
  it is null and `CANDELA_VK_VALIDATION=1` is set. Messages go to `VulkanContextOptions.Log`; errors are counted
  (`ValidationErrorCount`, `ValidationErrors`) so tests can assert zero. The messenger is also chained into
  `VkInstanceCreateInfo`, so leaks reported at `vkDestroyInstance` are caught. `SynchronizationValidation` adds the
  layer's hazard detection (on in all tests).
* **Device selection** (`DeviceSelector`, unit-tested): only devices with everything required are eligible; a device
  whose name contains `PreferredDeviceName` (`AppSettings.PreferredGpu`, CLI `--device`) wins, otherwise
  discrete > integrated > virtual > CPU, then more device-local memory.

  | Required | Optional (detected → `RendererInfo` / `DeviceCapabilities`) |
  |---|---|
  | Vulkan 1.3, `dynamicRendering`, `synchronization2` | `fillModeNonSolid` → polygon-mode wireframe (`SupportsWireframe`) |
  | `runtimeDescriptorArray`, `shaderSampledImageArrayNonUniformIndexing`, `descriptorBindingPartiallyBound`, `descriptorBindingVariableDescriptorCount`, `descriptorBindingSampledImageUpdateAfterBind` | `wideLines` |
  | `timelineSemaphore`, `scalarBlockLayout` | `rayQuery` + `accelerationStructure` + `bufferDeviceAddress` → `SupportsRayTracing` (enabled for stage 3) |
  | one graphics+compute queue (that can present, when a surface is given); `D32_SFLOAT` sampled + copyable | `samplerAnisotropy`, timestamps |

* **Frames in flight**: 2 (`FramesInFlight`). Each slot has a command pool, one primary command buffer, a fence and a
  timestamp query pool. `BeginFrame` waits only for the fence of the slot's previous use. GPU time per pass is
  reported in `LastGpuTimings`.
* **Memory** (`DeviceMemoryAllocator`): blocks per memory type (64 MiB device-local, 16 MiB host-visible, at most 1/8
  of small heaps) carved with a first-fit, coalescing free list; per-request alignment; `bufferImageGranularity`
  honoured by keeping linear resources and optimal-tiled images in separate blocks when it is > 1; dedicated
  allocations for requests larger than half a block, images ≥ 16 MiB, images the driver prefers dedicated and render
  targets; host-visible memory is persistently mapped; flush/invalidate for non-coherent memory by
  `nonCoherentAtomSize`.
* **Uploads** (`UploadRing`): one persistently mapped staging ring (8 MiB, doubles when needed); the tail advances when a
  frame's fence has been waited for. Oversized requests use a temporary buffer retired with the frame.
* **Deferred destruction** (`DeferDispose`, `DeferDestroy`): an object retired during frame N is destroyed when frame
  N + 3 begins, i.e. after every frame that may reference it (including descriptor sets of the other slot).
* **Pipeline cache**: persisted under `ConfigPaths.CacheDir` as `pipeline-<vendor>-<device>-<uuid>.bin`, wrapped in an
  envelope (magic, length, SHA-256) and written atomically (unique temporary file + rename); damaged files and caches of
  other devices/drivers are ignored.
* **Immediate submissions** (readbacks, `Pick`, `ReadPixels`) use a separate command buffer and fence and are not
  allowed while a frame is being recorded.

### Shaders

GLSL 460 sources in `src/Candela.Rendering.Vulkan/Shaders`, embedded as resources and compiled at run time with
Silk.NET.Shaderc (`ShaderCompiler`): `#include` resolution through a callback over the embedded files, macro variants
(`ShaderVariant`, e.g. `forward.frag` + `TRANSPARENT`), target Vulkan 1.3 / SPIR-V 1.6, performance optimisation. The
SPIR-V is cached by a SHA-256 of the source, its include closure and the defines under `<CacheDir>/shaders` (atomic
writes). `generated.glsl` is produced from `ShaderConstants`/`ColorSpaces` so binding numbers, flags and colour
matrices have one source of truth; struct sizes are asserted against `GpuStructs` by a test.

| File | Purpose |
|---|---|
| `common.glsl` | constants, GPU structs (`Material`, `Instance`, `Light`, `FrameData`), push constants |
| `scene.glsl`, `view.glsl` | set 0 / set 1 declarations, cluster indexing |
| `photometry.glsl` | beam profile (GPU twin of `BeamDistribution.Relative`), spot illuminance, range window |
| `brdf.glsl` | GGX / height-correlated Smith / Schlick specular + energy-weighted Lambert |
| `color.glsl`, `tonemap.glsl` | sRGB transfer, Rec.709 ↔ Rec.2020, AgX / ACES / Neutral |
| `mesh.vert` (`invariant gl_Position`), `prepass.frag` (`ALPHA_MASK`), `forward.frag` (`TRANSPARENT`), `colorless.frag`, `wire.frag`, `lines.vert` (`MESH_EDGES`) / `lines.frag`, `fullscreen.vert`, `tonemap.frag`, `grid.frag`, `cluster.comp` | the passes |

The test `ShaderTests.EveryShaderVariantCompilesAndValidates` compiles every variant and runs `spirv-val
--target-env vulkan1.3 --scalar-block-layout` on the output when the tool is on `PATH`.

### Retained scene (`VulkanRenderWorld : IRenderWorld`)

Handles (`MeshHandle`, …) are generation-checked indices: they stay valid across unrelated creations/destructions,
stale handles are rejected (`ArgumentException`) and double destruction is a no-op.

* **Meshes**: one device-local buffer per mesh holding interleaved vertices (position, normal, uv, tangent with the
  bitangent sign in w — 48 bytes) followed by 32-bit indices. Missing normals are generated (area-weighted), missing
  tangents from UV derivatives (Gram–Schmidt against the normal). A bounding sphere drives frustum culling; on devices
  without `fillModeNonSolid` a unique edge list is built for the edge-shader wireframe.
* **Textures**: `RGBA8` → `R8G8B8A8_SRGB` (colour) or `_UNORM` (data), float → `RGBA16F`; full mip chains generated
  with `vkCmdBlitImage` (box-filtered on the CPU when the format cannot be blitted linearly). All textures live in **one
  bindless array**. Slots 0, 1, 2 are the default white, black and flat-normal textures; a destroyed slot points at
  white until reused. Material textures are loaded through `IImageSource` and shared per (key, sRGB); unknown keys use
  the defaults (a warning is logged once per key).
* **Materials** (`MaterialPacker` → SSBO): base colour factor (Rec.709 → Rec.2020; the Rec.709 factor is kept to
  multiply sRGB texture samples before conversion), metallic/roughness, IOR-derived dielectric F0 (IOR 1 → no
  specular), emissive = `EmissiveColor` (Rec.709) × `EmissiveLuminance` cd/m² → Rec.2020, alpha mode (opaque / mask with
  cutoff / blend), double-sided, transmission (approximated, see below), UV scale, texture slots. Index 0 is the default
  material; a destroyed material that instances still use falls back to it.
* **Instances** (SSBO): world matrix, normal matrix, flags, pick id, offset of the instance's material slots (a second
  SSBO maps slot → material index, so meshes with several sub-meshes share one instance record).
* **Lights** (SSBO, replaced per frame by `SetLights`): `LightPacker` converts each `SpotLight`; see the photometric
  model below. **Lines** (`SetLines`) go to a vertex buffer of `LineVertex`.
* **Uploads**: every container keeps a CPU mirror and a dirty range; once per frame the dirty ranges are copied through
  the staging ring after one batched write-after-read barrier against earlier frames' reads. Growing a buffer replaces
  it (old one deferred) and bumps a version so the per-slot descriptor sets are rewritten. Mesh and texture data is
  staged when created and copied by the next `Render`; whatever is still staged when a frame is submitted (objects
  created after the viewports rendered, or frames that render no viewport) is recorded by a submit hook into that
  frame, so staging memory is always consumed by the frame it was reserved in. No device-wide waits.

### Descriptor layout

| Set | Binding | Content |
|---|---|---|
| 0 (scene, one set per frame slot) | 0 | materials SSBO |
| | 1 | instances SSBO |
| | 2 | instance material slots SSBO |
| | 3 | lights SSBO |
| | 4 | `sampler2D textures[]` — variable count (≤ min(device limit, 16384)), `UPDATE_AFTER_BIND`, `PARTIALLY_BOUND`, indexed with `nonuniformEXT` |
| 1 (per render target) | 0 | `FrameData` UBO (matrices, camera, exposure, cluster parameters, grid, background) |
| | 1 | clusters SSBO (`uvec2 offset, count` per cluster) |
| | 2 | cluster light-index list SSBO |
| | 3 | light-list counter SSBO |
| | 4 | HDR image (tone-mapping input) |
| push constants | | instance index, material index, draw mode, line width, colour (all stages) |

All storage blocks use the scalar block layout.

### Render targets (`VulkanRenderTarget : IRenderTarget`)

Per viewport: depth (`D32_SFLOAT`), pick ids (`R32_UINT`), HDR (`RGBA16F`), display (`RGBA8_UNORM`), the `FrameData`
buffer, the cluster buffers and the set-1 descriptor set. Any number of targets share one world. `Resize` recreates the
images (old ones are deferred), increments `DisplayVersion` and invalidates the last frame (Pick returns null until the
next render). Sizes are clamped to 1 … `maxImageDimension2D`.

### Frame and pass sequence

`IRenderer.Render` records into the open frame when the caller has begun one (`VulkanContext.BeginFrame`, the editor);
otherwise it begins and submits a frame of its own (CLI, tests, thumbnails). Passes (`RenderPass`) declare their
resource uses; `PassSequence` turns them into one batched synchronization2 barrier per pass from the tracked states,
wraps each pass in a debug label and GPU timestamps, and runs passes in `RenderStage` order:

| Stage | Pass | Views | Work |
|---|---|---|---|
| 100 `ShadowAtlas` | — (stage 2) | | |
| 200 `DepthPrepass` | depth pre-pass | all | reverse-Z depth + pick ids (`R32_UINT`, 0 = none / `NotPickable`); alpha-mask variant discards |
| 300 `LightAssignment` | light assignment | Full | `cluster.comp`, one work group per cluster |
| 400 `Opaque` | forward opaque / colorless / wireframe | one per view | depth test `EQUAL` against the pre-pass (no overdraw shading) |
| 500 `Volumetrics` | — (stage 2) | | |
| 600 `Transparent` | forward transparent | Full | blended and transmissive materials, back to front, premultiplied |
| 700 `HdrPost` | — (stage 2: bloom) | | |
| 800 `DisplayTransform` | tone mapping | all | HDR → tone mapper → sRGB encode (+ ±½ LSB triangular dither) → display |
| 900 `Overlays` | grid, lines | all | over the display image, depth-tested against the scene depth |

After the last pass the display image is transitioned to `SHADER_READ_ONLY_OPTIMAL` (fragment-shader read).
Recording allocates no managed memory in steady state (draw lists, barrier batches and arrays are reused; verified by
`LifecycleTests.SteadyStateFramesDoNotAllocateManagedMemory`).

**Clustered light assignment.** 16 × 9 screen tiles × 24 depth slices, logarithmic in view depth between near and far
(`slice = log(z) · 24 / log(far/near) − 24 · log(near) / log(far/near)`). For each cluster the compute shader tests
every light's cone (cut-off angle, range from the range window below) against the cluster's view-space bounds
(bounding sphere vs cone, plus a box vs range sphere test), writes the indices in light order (deterministic) into a
compact list with one atomic per cluster, at most 512 lights per cluster. The index buffer is sized so it cannot
overflow (`clusters × min(lights, 512)`); an overflow counter is kept for diagnostics. 1000+ lights are covered by
`ManyLightsTests` (validation-clean, and the shaded luminance equals the CPU sum over all 1000 lights within 3 %).

**Picking.** `Pick(x, y)` copies a 3 × 3 block of depth and pick ids of the last frame: the centre gives the pick id
and the world position (unprojected with the inverse view-projection of that frame), the neighbours on the same object
with the smaller depth step give two tangents whose cross product is the normal (turned towards the camera); `Depth`
is the distance along the view direction. Accuracy: < 1 cm in the tests (perspective and orthographic).

## Photometric model

### Spot lights

For a surface point P with normal N and a light at S with axis **a**:

```
E   = I0 · profile(θ) · colour · max(N·L, 0) / d²      [lx, per Rec.2020 channel]   d = |S − P|, θ = ∠(P − S, a)
L_o = Σ_lights BRDF(V, L) · E · window(d) + L_emissive  [cd/m²]
HDR = L_o · exposure
```

* **Inverse square, no artificial attenuation.** `window(d) = (1 − (d²/R²)²)²` only gives lights the finite support
  that clustered culling needs: `R² = exposure · I_max / (π · ε)` with ε = 2·10⁻⁵ (display-linear units) is where a white
  Lambertian surface lit on axis would fall below 1/50 000 of display white. Where a light contributes 100 × that
  threshold the window is 0.9998, so the inverse-square law is untouched wherever the light is visible.
  Distances are clamped to 1 cm (as `Photometrics` does).
* **Profile** — exactly `Candela.Photometry.BeamDistribution.Relative` (the parameters are taken from
  `BeamDistribution.Create(BeamType.Wash, BeamAngle, FieldAngle)`, never re-derived): a generalised Gaussian
  `exp(−ln2 · (θ/θ_b)^p)` (= `exp(−a θ^p)` with `a = ln2 / θ_b^p`; θ_b half the beam angle, p fitted so the profile is
  10 % at half the field angle), multiplied beyond the half field angle by a smoothstep window that reaches 0 at the
  cut-off (`BeamDistribution.CutoffAngle`) — a smooth falloff, no hard edge. `SpotLight` has no GDTF beam type, so
  every light uses Wash (the GDTF default).
* **Iris** (fraction of the field angle, 1 = open, ≤ 0 = unset = open): a gate stop at `θ_iris = iris · θ_field/2` with a
  10 % soft edge; the centre keeps its intensity. Clamped to 2 %.
* **Frost** (0..1): the beam is convolved with a Gaussian diffusion kernel of σ = 10° · frost; half-angles grow in
  quadrature (`θ₅₀² + (1.1774σ)²`, `θ₁₀² + (2.1460σ)²`) and the peak drops so the flux (solid-angle integral of the
  profile) is conserved — wider and softer. A simple stage-1 model; stage 2 replaces it with the frost cookie blur.
* `SourceRadius`, gobos, blades, prisms and shadows are carried in the light record for stage 2.

### BRDF (energy-conserving, single scattering)

```
f = (1 − F) · (1 − metallic) · albedo / π                         Lambert diffuse
  + D_GGX(h) · V_SmithGGX-correlated(v, l) · F_Schlick(v·h)        specular, α = roughness²
F0 = mix(((IOR − 1)/(IOR + 1))², albedo, metallic),  F90 = saturate(50 · avg(F0))
```

The (1 − F) weight keeps diffuse + specular ≤ 1 for every direction; multiple-scattering compensation for very rough
metals is not applied. A material with IOR 1 and metallic 0 is exactly Lambertian (`albedo/π`) — the configuration the
photometric tests use. Roughness is clamped to [0.02, 1] (α ≥ 0.002 keeps a point light's highlight finite).

* **Emissive**: `EmissiveColor` (Rec.709, white = 1) × `EmissiveLuminance` cd/m², optionally × emissive texture.
* **Transparency**: blend materials and `Transmission > 0` are drawn after the opaque pass, premultiplied:
  coverage = alpha · (1 − transmission) scales diffuse and emission, specular reflection is added in full (glass keeps
  its highlights). Tinted transmission and refraction are left to the path tracer.
* **Unlit / `InstanceFlags.Reference`**: base colour shown display-referred.
* **Background**: `ViewOptions.BackgroundLuminance` cd/m² (a dark venue: 0.02).

### Exposure and display transform

`exposure = Units.ExposureFromEv100(camera.Ev100 + settings.ExposureCompensation)` = 1 / (1.2 · 2^EV) (the
compensation is added to the EV: positive values darken, like closing the aperture). The HDR image stores luminance ×
exposure in linear Rec.2020; then:

* **AgX** (default; Troy Sobotka's, as in Blender 4 / Filament): inset matrix towards the achromatic axis, log2
  encoding over [−12.47, +4.03] stops around 0.18, 6th-order polynomial sigmoid
  `15.5x⁶ − 40.14x⁵ + 31.96x⁴ − 6.868x³ + 0.4298x² + 0.1191x − 0.00232`, outset matrix, gamma 2.2, Rec.2020 → Rec.709.
  Saturated stage colours desaturate towards white instead of skewing hue when very bright.
* **ACES** (Stephen Hill's fit of RRT + sRGB ODT): Rec.709 → AP1-ish input matrix,
  `(a(a + 0.0245786) − 0.000090537) / (a(0.983729a + 0.4329510) + 0.238081)`, output matrix.
* **Neutral** (Khronos PBR Neutral): toe offset `x − 6.25x²` below 0.08 (else 0.04), identity below 0.76, then
  `peak' = 1 − 0.24² / (peak + 0.24 − 0.76)` with desaturation 0.15.

The result is sRGB-encoded (IEC 61966-2-1) with a ±½ LSB triangular dither and stored in the `UNORM` display image.
`ToneMappingTests` checks each curve for neutral greys against CPU reference implementations.

### Colorless, Wireframe, grid and lines

* **Colorless**: neutral grey Lambert (albedo 0.62) lit by a key light from above, behind the camera's left shoulder
  (camera yaw only), a weak headlight along the view direction and a sky/ground hemisphere ambient; no materials, no
  stage lights; display-referred over a dark grey background.
* **Wireframe**: polygon mode `LINE` when `fillModeNonSolid` exists; otherwise (or with
  `VulkanRendererOptions.ForceEdgeShaderWireframe`) each mesh edge is drawn as a screen-space, anti-aliased quad. Both
  are depth-tested against the pre-pass (hidden lines removed) over the dark grey background; selected instances use
  the selection colour.
* **Grid** (`ViewOptions.ShowGrid`, all views): analytic ray/plane intersection with Z = 0 in a full-screen pass, depth
  written for the depth test, anti-aliased lines from screen-space derivatives, fades where cells get smaller than a few
  pixels and with distance (150 cells), a major line every 10 cells (`GridSpacing` = cell size), red X axis (Y = 0) and
  green Y axis (X = 0).
* **Lines** (`SetLines`): each segment is a screen-space quad of `Width` pixels with anti-aliased edges, depth-tested.

## Accuracy and test coverage

`tests/Candela.Rendering.Vulkan.Tests` (41 tests, a few seconds on lavapipe once the SPIR-V cache is warm) runs every test with the validation
layer and synchronization validation and asserts **zero validation errors**, including leak reports at shutdown.
Tests are skipped with a message when no Vulkan device exists.

| Test | Checks |
|---|---|
| `ContextTests` | instance/device with validation, device ranking, pipeline-cache persistence and damaged files |
| `ShaderTests` | every shader variant compiles and passes `spirv-val`; include changes invalidate the cache; struct sizes |
| `RenderModeTests`, `GoldenImageTests` | the procedural scene in Full / Colorless / Wireframe (both wireframe paths) against `Golden/*.png` at 320 × 240 — symmetric 3 × 3 neighbourhood comparison, mean error ≤ 2/255 and ≤ 0.5 % of pixels off by more than 40/255 |
| `PhotometricTests` | 10 000 cd at 5 m onto a white Lambertian floor: pool centre 10000/25/π cd/m² within 2 %; 30° off axis `profile(θ)·cos³θ` within 3 %; coloured light keeps luminance and chromaticity; iris and frost; emissive and background luminance |
| `ManyLightsTests` | 1000 lights, validation-clean, shaded luminance = CPU sum over all lights (3 %) |
| `ToneMappingTests` | AgX / ACES / Neutral curves, exposure compensation |
| `PickTests` | pick id, position within 1 cm, normal, depth; not-pickable instances |
| `SceneTests` | incremental updates, stale handles, material updates, sRGB / Rec.709 → Rec.2020 conversions, mip chains, uploads staged in frames without rendering |
| `LifecycleTests` | resize, several targets per world, memory returned after destroying everything, leak-free dispose, allocation-free steady-state frames |
| `MemoryTests` | alignment, free-list reuse and coalescing, granularity, dedicated allocations |
| `SwapchainTests` | headless surface: acquire/present, resize, minimise, misuse errors, ImGui handoff state |

Regenerate golden images after an intentional visual change with
`CANDELA_UPDATE_GOLDEN=1 dotnet test tests/Candela.Rendering.Vulkan.Tests --filter GoldenImageTests` and inspect them
(`bin/.../render-output` also holds the actual and difference images of failed comparisons). The HDR test hook
`VulkanRenderer.ReadHdr` (internal) returns the pre-tone-mapping image divided by the exposure, i.e. cd/m².

## Editor integration

### Shared context and swapchain

```csharp
// Instance extensions and surface from the windowing system (GLFW shown).
var options = new VulkanContextOptions
{
    InstanceExtensions = glfwRequiredInstanceExtensions,                  // includes VK_KHR_surface
    CreateSurface = instance => CreateWindowSurface(window, instance),    // VkSurfaceKHR handle; device must present to it
    PreferredDeviceName = appSettings.PreferredGpu,
    Log = (level, message) => logger.Log(level, message),
};
var context = VulkanContext.Create(options);
var renderer = new VulkanRenderer(context);                               // does not own the context
var swapchain = new VulkanSwapchain(context, context.PresentationSurface, framebufferWidth, framebufferHeight);
```

Dear ImGui (`ImGui_ImplVulkan_InitInfo`): `Instance`, `PhysicalDevice`, `Device`, `QueueFamily = QueueFamilyIndex`,
`Queue`, `PipelineCache`, `MinImageCount = swapchain.MinImageCount`, `ImageCount = swapchain.ImageCount`,
`UseDynamicRendering = true` with `PipelineRenderingCreateInfo.ColorAttachmentFormats = { swapchain.Format }`, and a
descriptor pool of the editor's own (with `FREE_DESCRIPTOR_SET` for `AddTexture`/`RemoveTexture`).

The swapchain uses a `UNORM` format (B8G8R8A8 / R8G8B8A8, sRGB non-linear colour space): ImGui's colours and the
display images are already sRGB-encoded, so they pass through unchanged.

### Per frame

```csharp
var frame = context.BeginFrame();                       // waits only for this slot's previous frame
foreach (var viewport in viewports)
    renderer.Render(viewport.Target, viewport.Camera, viewport.View, previewSettings, dt);  // recorded into `frame`
if (swapchain.TryAcquire(frame, out var image))
{
    swapchain.BeginRendering(frame, clearColor);        // UNDEFINED → COLOR_ATTACHMENT, clear, vkCmdBeginRendering
    ImGui_ImplVulkan_RenderDrawData(drawData, frame.CommandBuffer);
    swapchain.EndRendering(frame);
}
if (!swapchain.Present(frame) || windowResized)         // → PRESENT_SRC, submit (wait acquire, signal finished), present
    swapchain.Recreate(framebufferWidth, framebufferHeight);   // waits idle; 0 × 0 = minimised
```

Synchronisation: an "image acquired" semaphore per frame slot (reusable once the slot's fence signalled) and a
"rendering finished" semaphore per swapchain image (reusable once that image is acquired again). The submission waits
for the acquisition at `COLOR_ATTACHMENT_OUTPUT`, where the layout transition starts. `Pick` and `ReadPixels` are
blocking and must be called outside `BeginFrame` … `Present`.

### Showing a viewport in Dear ImGui

After every `Render` the target's display image is in `SHADER_READ_ONLY_OPTIMAL` and the barrier for fragment-shader
reads is recorded, so ImGui can sample it in the same frame or any later submission:

```csharp
var t = (VulkanRenderTarget)target;
if (t.DisplayVersion != viewport.RegisteredVersion)
{
    if (viewport.TextureId != 0)
    {
        var old = viewport.TextureId;                    // the old image may still be sampled by frames in flight
        context.DeferDestroy(() => ImGui_ImplVulkan_RemoveTexture(old));
    }
    viewport.TextureId = ImGui_ImplVulkan_AddTexture(t.DisplaySampler, t.DisplayImageView, t.DisplayImageLayout);
    viewport.RegisteredVersion = t.DisplayVersion;
}
ImGui.Image(viewport.TextureId, new Vector2(t.Width, t.Height));
```

`DisplaySampler` is a linear clamp-to-edge sampler shared by all targets; `DisplayImageView`/`DisplayImage` change on
`Resize` (`DisplayVersion` increments). `ViewOptions.ShowSelectionOutline`, overlays and `PreviewSettings` features
of stage 2 are accepted and ignored for now.

### Shutdown order

Dispose the targets, the renderer, the swapchain, then destroy the surface
(`context.SurfaceApi!.DestroySurface(context.Instance, surface, null)`), then the context. `VulkanRenderer.Create`
creates a renderer that owns its context (CLI, tests).

## Running on a real GPU

* Windows / Linux with a Vulkan 1.3 driver: current AMD (Adrenalin, RADV), NVIDIA and Intel (Windows driver, ANV)
  drivers all qualify; macOS needs MoltenVK with Vulkan 1.3 support (portability enumeration is enabled
  automatically). The renderer only uses core 1.3 features, queried limits (bindless array size, alignment,
  granularity, image dimensions, line width) and documented fallbacks (edge-shader wireframe, CPU mip generation), so
  nothing depends on one vendor or on lavapipe.
* `candela render-test --out out [--width 1920 --height 1080] [--frames 30] [--tonemapper agx|aces|neutral]
  [--device "RTX"] [--validation]` (from the repository: `dotnet run --project src/Candela.Cli -- render-test --out out`)
  writes `full.png`, `colorless.png`, `wireframe.png` and prints the device, driver, capabilities and per-pass GPU
  times. Exit codes: 0 ok, 1 failure (including validation errors), 2 usage, 3 no usable Vulkan device.
* Several GPUs: `--device <substring>` / `AppSettings.PreferredGpu`; otherwise the discrete GPU wins. Software
  rendering (lavapipe, SwiftShader) is reported as `RendererInfo.IsSoftwareRenderer`.
* Validation: install the Vulkan SDK (or `vulkan-validationlayers`) and set `CANDELA_VK_VALIDATION=1`.
* Caches: `ConfigPaths.CacheDir` (`pipeline-*.bin`, `shaders/*.spv`); deleting them is always safe.
* Debugging: object names and pass labels are set through `VK_EXT_debug_utils`; RenderDoc and Nsight show the pass
  structure of the table above.
* Shader development: `CANDELA_SHADER_DIR=src/Candela.Rendering.Vulkan/Shaders` makes the renderer read the GLSL files
  from disk instead of the embedded copies (no rebuild needed; the SPIR-V cache is keyed by content).

## Stage 2 and 3

The pass graph has reserved `RenderStage` slots, the scene lives in shared SSBOs with a bindless texture array, and
the device is created with ray query + acceleration structures + buffer device address when available, so later stages
add passes and buffers without changing stage 1:

* **Shadows and cookies (stage 2, `ShadowAtlas`)**: a depth atlas for the brightest `MaxShadowedLights` spot lights
  (sorted by contribution), PCF/PCSS softness from `SourceRadius`; gobo, iris, framing blades and prism facets as
  per-light cookie projections (gobos are already bindless `TextureHandle`s). The light record already carries `Up`,
  source radius and shadow flags.
* **Volumetrics (`Volumetrics`)**: froxel grid (8 px tiles × `VolumetricSlices`, matching the cluster frustum),
  Henyey–Greenstein in-scattering of the spot lights in `AtmosphereState` haze and smoke grids, temporal reprojection;
  dedicated cone ray-marching for the `MaxRaymarchedBeams` narrowest beams.
* **HDR post (`HdrPost`)**: bloom (`BloomIntensity`), auto exposure; **Overlays**: selection outline from the pick-id
  image, lux overlay (illuminance is available in the forward pass), noise and cable overlays, beam outlines.
* **Indirect light**: reflective shadow maps from the shadow atlas; ray-traced one-bounce GI with ray query.
* **Stage 3**: the progressive path tracer behind `IRenderer.StartFinalRender` (currently `NotSupportedException
  ("path tracer arrives in stage 3")`), sharing the mesh buffers (BLAS from the same vertex/index buffers), materials,
  lights and photometric profiles of stage 1.

## Known limitations (stage 1)

* Direct light only: no shadows, no indirect light, no volumetric light yet.
* All spot lights use the Wash beam type (the contract has no beam type).
* Transmission is an approximation (no tint, no refraction).
* `PreviewSettings.ResolutionScale`, temporal accumulation and auto exposure are ignored.
* Single graphics+compute queue; uploads and rendering share it (no async compute/transfer yet).
