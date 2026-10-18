# Real-time renderer (`Candela.Rendering.Vulkan`)

The Vulkan 1.3 implementation of the `Candela.Rendering` contracts (`IRenderer`, `IRenderWorld`, `IRenderTarget`).
**Stage 1** is the foundation and physically correct *direct* light: a shared Vulkan context, run-time GLSL
compilation, a retained GPU scene with bindless textures, a small pass graph, clustered photometric spot lights, tone
mapping, the Colorless and Wireframe views, the ground grid, lines and picking. **Stage 2** adds a cached shadow atlas
with PCSS soft shadows, light cookies (gobo, iris, framing blades, prism, frost) on surfaces and in the air,
volumetric light in haze, smoke and flames (froxels with temporal reprojection plus ray-marched narrow beams),
indirect light (reflective shadow maps, or ray-traced GI with ray query), bloom, auto exposure, the resolution scale,
the selection and beam outlines, reference drawings and the lux, noise and cable overlays. Stage 3 adds the path
tracer (see [Stage 3](#stage-3)). Everything stays in cd/m² (pre-exposed in the HDR image) and agrees with the CPU
photometry of `Candela.Photometry` (`LightMeter`, `Acoustics`, `BlackbodyRadiator`).

```
src/Candela.Rendering.Vulkan
  Core/         VulkanContext (+ options, device selection, capabilities), VulkanSwapchain, FrameScheduler,
                DeferredDeletionQueue, PipelineCacheStore, DebugMessenger, VkUtil
  Memory/       DeviceMemoryAllocator, GpuBuffer, GpuImage, UploadRing
  Graph/        RenderPass / PassSequence / RenderStage, resource-state tracking and batched barriers
  Scene/        VulkanRenderWorld, SceneBuffer<T>, TextureStore, MeshProcessor, LightPacker, MaterialPacker, handles,
                AtmosphereResources (smoke / flame / noise volumes), RayTracingScene (BLAS per mesh, TLAS per frame)
  Rendering/    VulkanRenderer, VulkanRenderTarget, PipelineLibrary, DrawList, ReverseZ, ShadowAtlas, LightPlanner,
                RendererResources, DescriptorWriter, the passes (Passes, LightingPasses, EffectPasses)
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
  | `timelineSemaphore`, `scalarBlockLayout` | `rayQuery` + `accelerationStructure` + `bufferDeviceAddress` → `SupportsRayTracing` (ray-traced GI; stage 3) |
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
| `common.glsl` | constants, GPU structs (`Material`, `Instance`, `Light`, `LightView`, `ShadowSlot`, `Vpl`, `FrameData`), push constants, hashes, octahedral normals |
| `scene.glsl`, `view.glsl` | set 0 / set 1 declarations, cluster indexing, depth ↔ world reconstruction |
| `photometry.glsl` | beam profile (GPU twin of `BeamDistribution.Relative`), range window, Henyey–Greenstein phase |
| `lighting.glsl` | cookies (gobo, blades, prism), light intensity towards a point, haze transmittance, shadow lookups (hard, PCSS) |
| `volume.glsl` | froxel depth mapping, haze + smoke scattering, flame emission (blackbody LUT), sound level, overlay gradient |
| `brdf.glsl` | GGX / height-correlated Smith / Schlick specular + energy-weighted Lambert |
| `color.glsl`, `tonemap.glsl` | sRGB transfer, Rec.709 ↔ Rec.2020, AgX / ACES / Neutral |
| `mesh.vert`, `prepass.frag`, `forward.frag`, `colorless.frag`, `wire.frag`, `lines.vert` / `lines.frag`, `fullscreen.vert`, `tonemap.frag`, `grid.frag`, `cluster.comp` | stage-1 passes |
| `shadow.vert` / `shadow.frag` (`ALPHA_MASK`, `RSM`), `vpl.comp`, `gather.comp`, `rt_gi.comp`, `gi_filter.comp`, `analysis.frag` | shadows and indirect light, analysis overlays |
| `froxel_inject.comp`, `froxel_integrate.comp`, `volumetric.frag` | volumetrics |
| `bloom_down.comp` (`FIRST`), `bloom_up.comp`, `histogram.comp`, `exposure.comp`, `depth_upsample.frag`, `selection.frag`, `outline.frag` | post and editor visuals |

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
* **Atmosphere** (`SetAtmosphere`): the smoke density, flame temperature and noise level grids (`VolumeGrid`) are
  uploaded as `R16F` 3D textures (trilinear, clamped to the box) when set; the blackbody luminance table of
  `BlackbodyRadiator` (Rec.2020 colour × cd/m², 500 K steps up to 12 000 K) is a static buffer. `SetNoiseSources` fills a
  buffer of (position, dB at 1 m).
* **Shadow casters**: creating, moving or destroying an instance with `CastShadow`, or changing its materials, records
  its old and new bounding spheres (`ShadowCasterChanges`); the shadow atlas re-renders only the tiles whose frusta
  they touch. Mesh or material edits invalidate every tile.
* **Ray tracing** (`RayTracingScene`, devices with ray query, only while ray-traced GI is in use): one BLAS per mesh
  (a geometry per sub-mesh, built from the mesh's own vertex/index buffer), a TLAS rebuilt once per frame from the
  visible-or-not instances (custom index = instance slot), and a geometry table (buffer device addresses of vertices
  and indices, sub-mesh ranges and material slots) for hit shading.
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
| 1 (per render target) | 0 | `FrameData` UBO (matrices, previous view-projection, camera, exposure, cluster and froxel parameters, media, feature bits, overlay range, selection colour, …) |
| | 1, 2, 3 | clusters SSBO (`uvec2 offset, count` per cluster), cluster light-index list, light-list counter |
| | 4 | HDR image |
| | 5, 6 | per-view light records (`LightView`: shadow slot, flags, VPL range), shadow slots (`ShadowSlot`: matrices, atlas rectangle, frustum) |
| | 7, 8 | shadow atlas with a comparison sampler (hardware PCF) and as raw depth (PCSS blocker search, VPLs) |
| | 9, 10, 11 | reflective shadow map reflectance and normals, virtual point lights |
| | 12 | indirect irradiance (half resolution, alpha = view depth) |
| | 13 | integrated froxel volume (in-scattering, transmittance) |
| | 14, 15, 16, 17, 18 | smoke density, flame temperature and noise level volumes, blackbody table, noise sources |
| | 19 | overlay gradient table (256 × 1) |
| | 20, 21, 22 | scene depth, normals, instance indices of the pre-pass |
| | 23, 24 | bloom level 0, analysis image (lx or dB) |
| | 25 | per-view light lists (ray-marched beams, then RSM or GI lights) |
| | 26, 27 | selection mask, exposure histogram buffer |
| 2 (per pass) | 0–7 | three storage images, three sampled images, the TLAS and a storage buffer (compute passes) |
| push constants | | instance index, material index, draw mode, line width, colour (all stages) |

All storage blocks use the scalar block layout. Bindings without a resource (feature not in use) point at 1 × 1
placeholders, so every pipeline is valid in every frame; set 1 is rewritten only when one of its resources changes
(`DescriptorSignature`).

### Render targets (`VulkanRenderTarget : IRenderTarget`)

Per viewport, at the internal resolution (`ResolutionScale` × size): depth (`D32_SFLOAT`), pick ids and instance
indices (`R32_UINT`), octahedral normals (`RG16F`), HDR (`RGBA16F`); at the display size: the display image
(`RGBA8_UNORM`). While a feature is in use the target also owns its images and buffers: froxel volumes, indirect
light and its history, VPLs, bloom pyramid, exposure buffers, analysis image, gradient table, selection mask, display
depth. A feature's resources are kept for 60 renders after its last use, so switching view modes does not reallocate,
and then released. Any number of targets share one world (and its shadow atlas). `Resize` recreates the images (old
ones are deferred), increments `DisplayVersion`, drops temporal history and invalidates the last frame (Pick returns
null until the next render). Sizes are clamped to 1 … `maxImageDimension2D`.

### Frame and pass sequence

`IRenderer.Render` records into the open frame when the caller has begun one (`VulkanContext.BeginFrame`, the editor);
otherwise it begins and submits a frame of its own (CLI, tests, thumbnails). Passes (`RenderPass`) declare their
resource uses; `PassSequence` turns them into one batched synchronization2 barrier per pass from the tracked states,
wraps each pass in a debug label and GPU timestamps, and runs passes in `RenderStage` order:

| Stage | Pass | Views | Work |
|---|---|---|---|
| 100 `ShadowAtlas` | shadow atlas | Full, Lux | (re-)renders the atlas tiles that changed; RSM variant also writes reflectance + normal |
| 200 `DepthPrepass` | depth pre-pass | all | reverse-Z depth, pick ids (`R32_UINT`, 0 = none / `NotPickable`), instance indices, normals; alpha-mask variant discards |
| 300 `LightAssignment` | light assignment | Full, Lux | `cluster.comp`, one work group per cluster |
| 350 `Indirect` | virtual point lights, indirect gather / ray-traced GI (+ filter) | Full | half-resolution indirect irradiance |
| 400 `Opaque` | forward opaque / colorless / wireframe | one per view | depth test `EQUAL` against the pre-pass (no overdraw shading) |
| 450 `Analysis` | analysis overlay | Lux, Noise | lx or dB per pixel into the `R32F` analysis image |
| 500 `Volumetrics` | froxel injection, integration, volumetric composite | Full with media | in-scattering and transmittance over the opaque scene, ray-marched narrow beams |
| 600 `Transparent` | forward transparent | Full | blended and transmissive materials, back to front, premultiplied, fogged |
| 700 `HdrPost` | bloom, auto exposure | Full | bloom pyramid; luminance histogram → exposure read back a few frames later |
| 800 `DisplayTransform` | tone mapping | all | HDR (+ bloom) → tone mapper → overlays → sRGB encode (+ ±½ LSB dither) → display |
| 850 `OverlayDepth` | overlay depth | scaled | nearest scene depth at display resolution for the overlays |
| 900 `Overlays` | grid, lines (+ beam outlines), selection mask, outline | all | over the display image, depth-tested against the scene depth |

After the last pass the display image is transitioned to `SHADER_READ_ONLY_OPTIMAL` (fragment-shader read).
Recording allocates no managed memory in steady state (draw lists, barrier batches, light plans and arrays are
reused; verified by `LifecycleTests.SteadyStateFramesDoNotAllocateManagedMemory` and, with every stage-2 feature on
two targets, `SteadyStateFramesWithEveryFeatureDoNotAllocate`). Every synchronisation goes through the tracked
resource states (synchronization2 barriers, `discard` for full overwrites), and every test runs with synchronization
validation.

**Per-view decisions.** `Render` first decides what the view needs (shading, overlay, lights, shadows only when some
light casts shadows, media present, indirect mode — `RayTraced` falls back to reflective shadow maps without ray
query, reflective shadow maps need shadows —, bloom, auto exposure, internal resolution), then `LightPlanner` ranks
the lights by their estimated contribution to this view (flux × on-screen size of the beam / distance², zero outside
the frustum) and picks the shadowed lights and their tile sizes, the ray-marched beams, the RSM lights with their VPL
budgets or the GI lights, and the beam outlines — without allocating.

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
is the distance along the view direction. Accuracy: < 1 cm in the tests (perspective and orthographic). With
`ResolutionScale` < 1 the display pixel maps to the internal pixel under it, and the point is moved along the
surface's tangent plane onto the display pixel's ray (< 1 cm at scale 0.5 in the tests).

## Photometric model

### Spot lights

For a surface point P with normal N and a light at S with axis **a**:

```
E   = I0 · profile(θ) · cookie · colour · shadow · T_haze(d) · max(N·L, 0) / d²   [lx, per Rec.2020 channel]
      d = |S − P|, θ = ∠(P − S, a)
L_o = Σ_lights BRDF(V, L) · E · window(d) + albedo/π · E_indirect + L_emissive     [cd/m²]
HDR = (L_o · T_view + L_in-scattered) · exposure
```

* **Inverse square, no artificial attenuation.** `window(d) = (1 − (d²/R²)²)²` only gives lights the finite support
  that clustered culling needs: `R² = exposure · I_max / (π · ε)` with ε = 2·10⁻⁵ (display-linear units) is where a white
  Lambertian surface lit on axis would fall below 1/50 000 of display white. Where a light contributes 100 × that
  threshold the window is 0.9998, so the inverse-square law is untouched wherever the light is visible.
  Distances are clamped to 1 cm (as `Photometrics` does).
* **Profile** — exactly `Candela.Photometry.BeamDistribution.Relative` (the parameters are taken from
  `BeamDistribution.Create(light.BeamType, BeamAngle, FieldAngle)`, never re-derived): for the GDTF beam types with a
  generalised Gaussian (Wash, the default, Spot, Fresnel, …) `exp(−ln2 · (θ/θ_b)^p)` with θ_b half the beam angle and
  p fitted so the profile is 10 % at half the field angle, multiplied beyond the half field angle by a smoothstep
  window that reaches 0 at the cut-off (`BeamDistribution.CutoffAngle`); the other shapes (e.g. Glow) as
  `BeamDistribution` defines them. Unknown beam types fall back to Wash.
* **Iris** (fraction of the field angle, 1 = open, ≤ 0 = unset = open): a gate stop at `θ_iris = iris · θ_field/2` of the
  unfrosted beam with a soft edge (10 %, wider with focus blur and frost); the centre keeps its intensity. Clamped to
  2 %.
* **Frost** (0..1): the beam is convolved with a Gaussian diffusion kernel of σ = 10° · frost; half-angles grow in
  quadrature (`θ₅₀² + (1.1774σ)²`, `θ₁₀² + (2.1460σ)²`) and the peak drops so the flux (solid-angle integral of the
  profile) is conserved — wider and softer. Frost also blurs the cookie (see [Cookies](#cookies)).
* Shadows, cookies (gobo, framing blades, prism) and haze extinction multiply this, see the stage-2 sections.

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
* **Unlit materials**: base colour shown display-referred. **`InstanceFlags.Reference`** (imported drawings, ghost
  geometry): unlit, base colour display-referred, drawn with the transparent pass at 50 % opacity (no shadows cast,
  not in the indirect light).
* **Background**: `ViewOptions.BackgroundLuminance` cd/m² (a dark venue: 0.02).
* **Indirect light** (stage 2): `albedo/π · E_indirect` is added to the diffuse term, see
  [Indirect light](#indirect-light).

### Exposure and display transform

`exposure = Units.ExposureFromEv100(EV − settings.ExposureCompensation)` = 1 / (1.2 · 2^EV′) with EV = the camera's
`Ev100`, or with `AutoExposure` the adapted EV of the scene (see [Auto exposure](#auto-exposure)). The compensation is
in stops of exposure: +1 doubles the exposure (a brighter image, like photographic exposure compensation). The HDR
image stores luminance × exposure in linear Rec.2020; the display transform mixes in the bloom
([Bloom](#bloom)) and then applies:

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

## Shadows

**Atlas** (`ShadowAtlas`, one per renderer, shared by all views): a reverse-Z `D32` image twice
`PreviewSettings.ShadowMapResolution` (R) wide, carved into square power-of-two tiles from R down to R/8 on an
occupancy grid (tiles aligned to their own size, so equal sizes never fragment), at most 256 tiles. While reflective
shadow maps are in use the atlas also has an `RGBA8` reflectance and an `RG16F` normal image of the same size. The
atlas is created with the first shadow-casting light and released when none is left.

**Which lights.** `LightPlanner` ranks the lights by their estimated contribution to the view; the
`MaxShadowedLights` best that have `SpotLight.CastShadows` get a tile, the most important one R × R and each further
one half the size for every factor of 4 its score is below the best one's (at least R/8). A light keeps its tile while
the wanted size is within one level (hysteresis); otherwise it gets a new tile, evicting the least recently used tiles
of lights the frame does not need. Lights that do not fit stay unshadowed.

**Caching.** A tile is re-rendered only when its frustum changes (light moved or turned, or the cone outgrew the 10 %
margin of the frustum), when a shadow caster inside the frustum was created, destroyed, moved or got other materials
(the world records old and new bounding spheres), when meshes or materials were edited, or when RSM data is needed
and missing. Intensity, colour, gobo, blades, prism and iris changes never re-render a tile (they are applied when the
shadow map is read). Casters are instances with `InstanceFlags.CastShadow` that are not hidden and not references.

**Frustum.** Perspective with the light at the apex, the bounding cone of the beam (cut-off, iris, prism spread) + 10 %
as half-angle, clamped to 80°; near plane 0.1 m (the fixture's own lens and housing never shadow its beam), infinite
far plane, reverse-Z. Rasterised with a slope-scaled depth bias (constant −1.5, slope −2.0) and alpha-masked
materials discard.

**Lookup.** The receiver is offset along its normal by 1.5 shadow texels at its depth (× the sine of the incidence
angle, at least ¼) and 0.75 texel towards the light, then:

* **Surfaces: PCSS** soft shadows from `SpotLight.SourceRadius` R_s — a 12-tap Vogel-disk blocker search over the
  region from which the lens disk can be hidden, penumbra width `w = R_s · (z − z_blocker) / z_blocker` on the
  receiver, then a 16-tap PCF disk of that radius with hardware bilinear comparison (at most 24 texels), rotated per
  pixel by interleaved gradient noise. The filter radius is at least ¾ texel, so a point light (radius 0) still
  gets an anti-aliased edge.
* **Volumes and GI hits**: a single bilinear comparison tap.

## Cookies

Every cookie is evaluated per sample in **gate coordinates**: the direction from the light in its frame
(right = direction × `Up`, up, direction) projected onto the plane at unit distance and divided by tan(θ_field/2) of
the unfrosted beam, so the field-angle circle is the unit circle. Cookies apply to surfaces, the lux overlay, the
froxel volume, the ray-marched beams and the reflective shadow maps alike, so beams in haze carry the same pattern.

* **Gobo** (`SpotLight.Gobo`, a bindless texture; **alpha = transmission**, RGB tints): the image fills the square
  around the unit circle, rotated by `GoboRotation`; outside the square the gate blocks the light. Filtered by the
  receiving pixel's footprint in gate units, plus the defocus: `GoboBlur` (0..1, 12 % of the image width at 1) and frost
  select the mip level (focus blur = level-of-detail bias).
* **Iris**: part of the beam profile (see the photometric model), with its edge softened by blur and frost.
* **Framing blades** A/B/C/D at the top, right, bottom and left of the gate (seen from behind the fixture, `Up` up),
  the module turned by `BladeRotation`; insertion 0 leaves the edge at the cut-off, 1 puts it on the axis;
  `BladeAngle` tilts each edge about the point where it crosses its axis. Each blade removes the half-plane beyond its
  edge with a soft edge of 0.004 gate units + twice the cookie blur.
* **Prism**: `PrismFacets` (≤ 16) copies of the beam, cookie included, each tilted by `PrismSpread` (≤ 60°) towards
  azimuth `PrismRotation + 2πk/N` (rotation of the light about each facet's direction), each with **1/N** of the
  intensity, so the flux is unchanged.
* **Frost** widens the beam (photometric model) and blurs every cookie by its angular spread:
  `blur = sqrt(focusBlur² + frostBlur²)` with `frostBlur = ½ · tan(min(1.1774 · σ_frost, 1.2)) / tan(θ_field/2)`.

## Volumetrics

Media (`AtmosphereState`): uniform haze `BaseScattering` σ_h (1/m), smoke density d(x) from `SmokeDensity` (3D
texture, trilinear) with σ_s = σ_h + d · `SmokeScatteringAtFullDensity`, Henyey–Greenstein phase with `Anisotropy` g,
and flames from `FlameTemperature`. Haze and smoke **scatter without absorbing** (σ_t = σ_s): the scattered light
leaves both the light's path and the view path. Flames emit like a black body with absorption κ = 2 /m (Kirchhoff:
source κ · L_bb(T)); L_bb(T) comes from `BlackbodyRadiator` (Rec.2020 colour × cd/m², a 1024-entry table from 500 K to
4000 K interpolated linearly).

**Froxels** (`VolumetricSlices` ≥ 8): one froxel per 8 × 8 internal pixels × exponential depth slices
`z = near · (far/near)^(s/slices)` between max(camera near, 5 cm) and the far side of the scene (instances, and lights
with up to 30 m of beam), quantised to 25 % steps so the range stays stable, at most 500 m.

1. **Injection** (`froxel_inject.comp`, compute): at the froxel centre (jittered with a Halton (2, 3, 5) sequence over
   64 frames when temporal accumulation is on) the source term
   `S = σ_s · Σ_lights HG(g, cos θ) · I(θ) · cookie · shadow · T_light / d² + κ · L_bb(T)` (cd/m² per metre) over the
   lights of the froxel's cluster, and σ_t. The source is stored pre-exposed. With temporal accumulation the result
   is blended with the previous frame's value at the reprojected froxel position (history weight 0.8, history
   rejected when the grid or depth range changed, scaled by the exposure ratio).
2. **Integration** (`froxel_integrate.comp`): front to back along each froxel column with the energy-conserving
   analytic step `L += T · S · (1 − e^(−σ_t Δl)) / σ_t`, `T *= e^(−σ_t Δl)`, storing (L, T) at every slice.
3. **Composite** (`volumetric.frag`, internal resolution, blending `dst = src.rgb + src.a · dst`): the integrated
   in-scattering and transmittance at the pixel's depth (the far end of the volume over the background) are applied
   to the opaque scene and the background; transparent surfaces apply the same lookup at their own depth.

**Narrow beams** (`MaxRaymarchedBeams` brightest lights with a field angle below 15° and a bounding cone below 30°)
are excluded from the froxels and **ray marched per pixel** in the composite pass: the view ray is clipped to the
beam's cone (and range and scene depth) analytically and sampled with 48 jittered steps of
`σ_s · HG · I(θ) · cookie · shadow · T_light / d² · T_view(camera → sample)`, so gobo patterns in narrow beams stay
sharp. The single-scattering tests compare both paths against a numerical integral (within 5 %).

**Haze transmittance along light paths** `T_light = e^(−σ_h · d)` multiplies all light reaching a surface, a froxel,
a ray-march sample, an RSM texel or a GI hit (uniform haze only, see the limitations). The lux overlay is a clear-air
light-meter reading and does not apply it.

## Indirect light

`PreviewSettings.Indirect` (Full view):

* **`ReflectiveShadowMaps`** (default). Shadow tiles of the RSM lights also store what the light sees: diffuse
  reflectance `(1 − metallic) · base colour` and the octahedral normal. `vpl.comp` (one work group per light) splits
  the tile into 16 × 16 cells, estimates each cell's reflected flux `I(ω) · cookie · T_light · albedo · Ω_texel` from
  4 × 4 stratified texels, builds a CDF and **importance-samples** that light's VPLs by flux (stratified, fixed per
  VPL so the result is stable); each VPL carries `flux / (count · pdf)`, an unbiased estimate of everything the light
  reflects. The view's budget of 256 VPLs is shared among the RSM lights in proportion to their scores (at least 8
  each). `gather.comp` evaluates, at **half resolution**, `E = Σ Φ_k · cos θ_k · cos θ_p · T_light / (π · max(d², (0.1 m)²))`
  for the surface under each pixel (no visibility test), and the forward pass **upsamples bilaterally** (four
  nearest samples, bilinear × view-depth similarity) and adds `albedo/π · E`.
* **`RayTraced`** (devices with ray query; otherwise falls back to RSM). `rt_gi.comp` traces 4 cosine-distributed rays
  per half-resolution pixel against the TLAS; a hit is shaded with the albedo of its material, the direct light of
  the view's GI lights (shadowed lights, up to 64, visibility from their shadow maps), its emission and `T_light`; a
  miss sees the background. `E = π/N · Σ L_hit`. The estimate is blended with the reprojected history (weight 0.9,
  rejected where the depth disagrees by more than 5 %; off with `TemporalAccumulation = false`), and the accumulated,
  unfiltered result becomes the next history. Two à-trous iterations (5 × 5 B3 kernel, steps 1 and 2, depth and
  normal edge-stopping) then remove the remaining speckle before the bilateral upsampling.
* **`Off`**.

Indirect light is one bounce of diffuse light from the shadowed lights; the tests compare both modes with a CPU
integral of the light a red wall reflects onto a white floor.

## Post-processing

### Bloom

`bloom_down.comp` builds a half-resolution pyramid (up to 6 levels, down to ~4 px) with the 13-tap filter of five
overlapping box filters (weights ½ and 4 × ⅛, normalised: every level keeps the image's energy); the first level limits
samples to a luma of 2¹⁰ (10 stops above white) so a stray overflow cannot flood the pyramid — unlike a Karis average
this leaves all realistic highlights untouched. `bloom_up.comp` adds each coarser level, tent-filtered, into the finer
one. The display transform uses `(1 − k) · scene + k · Σ levels / levels` with k = `BloomIntensity`: the bloom only
redistributes light (energy within 3 % in the tests).

### Auto exposure

`histogram.comp` builds a 128-bin histogram of log2 scene luminance (HDR / exposure, cd/m²) over [−14, 22] in shared
memory; `exposure.comp` takes the mean log2 luminance of the pixels between the 50th and 95th percentile (the dark
venue and the brightest highlights do not drive the exposure) and copies it to a host-visible buffer of the frame slot.
When the slot comes round again (no wait) the renderer adapts `log2 L ← log2 L + (measured − log2 L) · (1 − e^(−1.5 · dt))`
and uses `EV100 = log2(L · 100 / 12.5)` (minus the compensation). Without a measurement yet, the camera EV is used.

### Resolution scale

`ResolutionScale` (clamped to 0.5–1) sets the internal resolution of the scene images, the froxel grid and the
indirect light. The display transform upscales the HDR image bilinearly to the display size; the grid, lines and
outlines are drawn at display resolution, depth-tested against the nearest scene depth (`depth_upsample.frag`).
`Pick`, `ReadPixels` and the display image always use display coordinates.

## Analysis overlays (`ViewOptions.Overlay`)

Overlay values are computed per pixel of every visible opaque surface into an `R32F` analysis image
(`analysis.frag`, EQUAL depth test against the pre-pass, any view shading) and coloured in the display transform:
`ColorGradient.Bake(256)` of the overlay's gradient is uploaded as a 256 × 1 table over [RangeMin, RangeMax] (log10
when `Logarithmic`), only when the gradient changes; values outside the range get `OutOfRangeColor` (grey). The
background is not coloured.

* **Lux** (`LuxGradient`, default 1 … 5000 lx, logarithmic): the **illuminance** on the surface, in lux —
  `E = Σ_lights I0 · profile(θ) · cookie · shadow · max(N·L, 0) / d²` (photopic: the luminance of the summed Rec.2020
  illuminance, light colours have luminance 1). Direct light only, no albedo, no indirect light, no haze extinction —
  a light-meter reading at the surface with its normal, equal to `LightMeter.IlluminanceAt` within 1 % where nothing
  shadows the point.
* **Noise** (`NoiseGradient`, default 25 … 80 dB): the **free-field sound pressure level** at the surface,
  `L = 10 · log10 Σ 10^(L_i/10)` with `L_i = L_1m,i − 20 · log10(max(d_i, 0.1 m))` over `SetNoiseSources`
  (= `Acoustics.SplAt` within 0.5 dB), or the `AtmosphereState.NoiseField` grid when one is given (trilinear; outside
  its box: silence). The air gets a **coloured fog**: density `0.05 /m · t · alpha` with t = the level's position in
  the gradient range and the gradient colour, integrated front to back along the view ray up to the surface (or 60 m
  over the background) with 24 quadratically spaced, jittered samples. The fog is display-referred (not tone mapped,
  independent of exposure and lighting), so it shows the gradient colours exactly.
* **Cables**: the shaded image with everything except instances flagged `InstanceFlags.CableHighlight` desaturated and
  darkened to 32 % of its luminance; lines (`SetLines`: the cable runs) are drawn on top of everything at full
  brightness.

`VulkanRenderer.ReadAnalysis` (internal) reads the lux / dB image back for tests and tools.

## Editor visuals

* **Selection outline** (`ShowSelectionOutline`, `SelectionColor` = #FF7A00 by default): selected instances are drawn
  into a display-resolution mask (coverage, and visibility against the scene depth); pixels within ~2 px outside a
  silhouette get the selection colour, anti-aliased by distance; where the nearby part of the object is hidden behind
  other geometry the outline is drawn at 45 % opacity, so selected objects stay findable through occluders. All views.
* **Beam outlines** (`ShowBeamOutlines`, all views — useful in Colorless and Wireframe): per light 8 lines from the
  lens to the field-angle circle where the beam meets the floor (Z = 0; 10 m when it points up; 0.5–30 m) and that
  circle as 24 segments, in the light's colour, 1.5 px, depth-tested.
* **Reference drawings** (`InstanceFlags.Reference`): unlit, base colour display-referred at 50 % opacity, no shadows,
  not ray traced.
* **Wireframe** and **Colorless** as in stage 1; lines, grid and outlines are always drawn at display resolution.

## Accuracy and test coverage

`tests/Candela.Rendering.Vulkan.Tests` (75 tests, about a minute on lavapipe once the SPIR-V cache is warm;
`xunit.runner.json` limits parallelism to two tests because lavapipe renders on the CPU) runs every test with the
validation layer and synchronization validation and asserts **zero validation errors**, including leak reports at
shutdown. Tests are skipped with a message when no Vulkan device exists. Images are 160 × 120 … 320 × 240.

| Test | Checks |
|---|---|
| `ContextTests` | instance/device with validation, device ranking, pipeline-cache persistence and damaged files |
| `ShaderTests` | every shader variant compiles and passes `spirv-val`; include changes invalidate the cache; struct sizes |
| `RenderModeTests`, `GoldenImageTests` | the procedural scene in Full / Colorless / Wireframe (both wireframe paths) against `Golden/*.png` at 320 × 240 — symmetric 3 × 3 neighbourhood comparison, mean error ≤ 2/255 and ≤ 0.5 % of pixels off by more than 40/255 |
| `PhotometricTests` | 10 000 cd at 5 m onto a white Lambertian floor: pool centre 10000/25/π cd/m² within 2 %; 30° off axis `profile(θ)·cos³θ` within 3 %; coloured light keeps luminance and chromaticity; iris and frost; emissive and background luminance |
| `ManyLightsTests` | 1000 lights, validation-clean, shaded luminance = CPU sum over all lights (3 %) |
| `ToneMappingTests` | AgX / ACES / Neutral curves, exposure compensation (stops of exposure) |
| `PickTests` | pick id, position within 1 cm, normal, depth; not-pickable instances |
| `SceneTests` | incremental updates, stale handles, material updates, sRGB / Rec.709 → Rec.2020 conversions, mip chains, uploads staged in frames without rendering |
| `LifecycleTests` | resize, several targets per world, memory returned after destroying everything, leak-free dispose, allocation-free steady-state frames; every stage-2 feature on two targets across resizes and view switches, allocation-free with all of them |
| `ShadowTests` | golden `shadow-box`; a point in the umbra gets ~0 direct light while the lux overlay elsewhere matches `LightMeter` within 1 %; cached tiles are not re-rendered until a caster in their frustum moves; `MaxShadowedLights` priority; `CastShadow` / `CastShadows` respected |
| `CookieTests` | goldens `cookie-gobo` (orientation, rotation), `cookie-blades`, `cookie-prism` (3 facets, flux 1/N each), `cookie-iris`; frost widens the beam and blurs the gobo; blade insertion and rotation positions |
| `VolumetricTests` | single scattering in uniform haze vs the numerical integral of σ·HG·I·e^(−σr)/r²·e^(−σl) within 5 % (froxels with and without temporal accumulation, and a ray-marched narrow beam); goldens `volumetric-beam` (side view), `volumetric-smoke` (density blob), `volumetric-flame`; an optically thick flame shows the blackbody luminance (5 %) and chromaticity (0.01) |
| `OverlayTests` | lux overlay = `LightMeter` within 1 % (four lights, three beam types); gradient and out-of-range grey (golden `overlay-lux`); noise overlay = `Acoustics.SplAt` within 0.5 dB, noise field instead of sources; golden `overlay-noise` (surfaces + fog); golden `overlay-cables` (greyed scene, highlighted device keeps its colours); golden `selection-outline` (orange, dimmed through an occluder, absent when disabled); golden `beam-outlines`; reference drawings unlit at 50 % |
| `PostTests` | golden `bloom`; the bloom image has the HDR image's energy (3 %), a halo appears and the source gives up light; auto exposure converges to the trimmed-mean measurement (0.2 stop), follows a 4× brighter scene by 2 stops (adapting over time) and adds compensation; resolution scale 0.5 renders a quarter of the pixels, keeps the display size and picking (< 1 cm), clamps to [0.5, 1] |
| `IndirectTests` | a red wall bleeds onto a white floor: red/green rises by more than 30 % against `Indirect = Off`, and the added luminance matches a CPU integral of the wall's reflected light (RSM within 12 %, ray-traced GI within 20 %, both measured on lavapipe); fallbacks |
| `MemoryTests` | alignment, free-list reuse and coalescing, granularity, dedicated allocations |
| `SwapchainTests` | headless surface: acquire/present, resize, minimise, misuse errors, ImGui handoff state |

Regenerate golden images after an intentional visual change with
`CANDELA_UPDATE_GOLDEN=1 dotnet test tests/Candela.Rendering.Vulkan.Tests` (optionally `--filter` a class) and inspect
them (`bin/.../render-output` holds the actual image of every comparison and the difference images of failed ones).
Internal test hooks: `VulkanRenderer.ReadHdr` returns the pre-tone-mapping image divided by the exposure (cd/m²),
`ReadBloom` the bloom image the display transform mixes in, `ReadAnalysis` the lux / dB image; `LastStatistics`
counts shadowed lights, shadow tiles rendered, ray-marched beams and VPLs.

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
`Resize` (`DisplayVersion` increments). The display image keeps the target's size whatever the `ResolutionScale`.

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
  [--device "RTX"] [--validation] [--haze 0.02] [--no-shadows] [--shadow-resolution 2048] [--indirect off|rsm|raytraced]
  [--no-bloom] [--bloom 0.04] [--auto-exposure] [--exposure-compensation 1] [--scale 0.75] [--volumetric-slices 64]
  [--overlays]` (from the repository: `dotnet run --project src/Candela.Cli -- render-test --out out`) writes
  `full.png`, `colorless.png`, `wireframe.png` (with `--overlays` also `lux.png`, `noise.png`, `cables.png`) and prints
  the device, driver, capabilities, per-pass GPU times and the shadow / ray-march / VPL statistics. Exit codes: 0 ok,
  1 failure (including validation errors), 2 usage, 3 no usable Vulkan device.
* Several GPUs: `--device <substring>` / `AppSettings.PreferredGpu`; otherwise the discrete GPU wins. Software
  rendering (lavapipe, SwiftShader) is reported as `RendererInfo.IsSoftwareRenderer`.
* Validation: install the Vulkan SDK (or `vulkan-validationlayers`) and set `CANDELA_VK_VALIDATION=1`.
* Caches: `ConfigPaths.CacheDir` (`pipeline-*.bin`, `shaders/*.spv`); deleting them is always safe.
* Debugging: object names and pass labels are set through `VK_EXT_debug_utils`; RenderDoc and Nsight show the pass
  structure of the table above.
* Shader development: `CANDELA_SHADER_DIR=src/Candela.Rendering.Vulkan/Shaders` makes the renderer read the GLSL files
  from disk instead of the embedded copies (no rebuild needed; the SPIR-V cache is keyed by content).

### Performance notes for real GPUs

lavapipe runs every pass on the CPU, so the test timings say nothing about a GPU; the costs below are what scales on
real hardware, with the settings that control them.

| Feature | Cost scales with | Memory | Knobs |
|---|---|---|---|
| Shadow atlas | tiles **re-rendered** × casters in their frusta (cached: a static scene renders no tiles per frame) | (2R)² × 4 B depth, + 8 B with RSM — R = 1024: 16 MiB, 48 MiB with RSM | `ShadowMapResolution`, `MaxShadowedLights` |
| PCSS | 12 + 16 shadow-map taps per shadowed light per shaded pixel | — | `SourceRadius` (wider penumbrae cost the same taps) |
| Froxels | (W/8) × (H/8) × slices × lights per cluster (injection) | 3 × 8 B per froxel — 1920 × 1080 at 128 slices ≈ 95 MiB, at 64 ≈ 47 MiB | `VolumetricSlices`, `ResolutionScale`, `TemporalAccumulation` |
| Ray-marched beams | covered pixels × 48 steps per beam | — | `MaxRaymarchedBeams` |
| RSM | 256 VPLs × ¼ of the internal pixels (≈ 130 M VPL evaluations at 1080p) + VPL generation (one 256-thread group per light) | VPL buffer, half-resolution `RGBA16F` | `Indirect`, `MaxShadowedLights` |
| Ray-traced GI | 4 rays × ¼ of the internal pixels (≈ 2 M rays at 1080p) + hit shading (up to 64 lights) + 2 × 25-tap filter; TLAS rebuilt per frame, BLAS once per mesh | half-resolution images × 3, BLAS ≈ a few × the mesh size | `Indirect` |
| Bloom | ~1⅓ × the half-resolution pixels × 13 + 9 taps | pyramid ≈ ⅓ of an `RGBA16F` image | `Bloom`, `BloomIntensity` (no cost difference) |
| Auto exposure | one histogram pass (shared-memory atomics) + one 128-thread group | 0.5 KiB per frame slot | `AutoExposure` |
| Overlays | one extra opaque pass into `R32F` (lux / noise), 24 sound-level samples per pixel for the noise fog | `R32F` image | `Overlay` |

* Expect the forward pass, the froxel injection and the ray marching of a few wide-coverage beams to dominate a
  busy stage at 1080p; `ResolutionScale = 0.75` cuts all internal-resolution work by ~44 % with little visible loss
  (the overlays and outlines stay sharp).
* A real-time editor viewport should keep `TemporalAccumulation` on: froxel jitter and the GI history turn per-frame
  noise into smooth results within a few frames; moving the camera reprojects, moving objects or lights is handled by
  the depth rejection (GI) and the 0.8 history weight (froxels), with a short trail at most.
* Shadow tiles are only re-rendered when something inside them changes, so moving lights cost their own tiles; a
  moving truss with many fixtures re-renders all of their tiles in that frame.
* GPU timings per pass are printed by `candela render-test` and available as `VulkanContext.LastGpuTimings`
  (timestamps; on lavapipe they are not meaningful).

## Stage 3

The progressive path tracer behind `IRenderer.StartFinalRender` (currently `NotSupportedException ("path tracer
arrives in stage 3")`) can share the BLAS per mesh and the geometry table of `RayTracingScene`, the materials, lights,
cookies, photometric profiles and media of stages 1 and 2.

## Known limitations

* **Haze extinction along light paths** uses the uniform haze only; smoke dims the view path (froxels) but not the
  light reaching a point (that would need a march through the density volume per light and point).
* **Shadow frusta** are clamped to 80° half-angle: light beyond that (very wide floods) is unshadowed. Lights beyond
  `MaxShadowedLights`, and lights that do not fit in the atlas, are unshadowed (and have no reflective shadow map).
* **Reflective shadow maps** test no visibility between a VPL and the receiver (light can leak through thin walls);
  only one bounce; only shadowed lights contribute. The ray-traced GI shades its hits with the shadowed lights (up to
  64 per view) plus emission; it is a noisy estimate that needs a few frames to converge after a change.
* **Transparent surfaces** take the fog of the froxel volume at their depth; volumetric light between overlapping
  transparent layers is approximated by that single lookup. Transmission has no tint and no refraction.
* **Froxel resolution**: beams wider than ~15° are as sharp as 8 × 8 px tiles × the slice spacing; gobo detail in
  wide beams is visible on surfaces but blurred in the air (narrow beams are ray marched at full resolution).
* **Noise overlay**: free-field point sources (no reflections, no directivity) unless a noise field is given.
* Single graphics+compute queue; uploads and rendering share it (no async compute/transfer yet).
