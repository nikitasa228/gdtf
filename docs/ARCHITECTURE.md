# Candela — architecture

Candela is a C# / Vulkan engine and editor for physically based stage-lighting visualisation: GDTF fixtures, MVR scenes,
real-time and offline (path-traced) rendering of volumetric light in haze, photometry in candela and lux, smoke and
fire simulation, DMX/power cabling and live control from lighting consoles (Art-Net, sACN, …).

Requirements (verbatim, Russian): [REQUIREMENTS.ru.md](REQUIREMENTS.ru.md). Milestones and status: [ROADMAP.md](ROADMAP.md).

## 1. Technology

| Area | Choice | Why |
|---|---|---|
| Language / runtime | C# 12, .NET 8 (LTS) | `net8.0`, nullable enabled, unsafe allowed where interop needs it |
| Graphics API | Vulkan **1.3** core (dynamic rendering, synchronization2, descriptor indexing) via **Silk.NET.Vulkan 2.23** | one API for AMD / NVIDIA / Intel on Windows and Linux; macOS through MoltenVK |
| Optional GPU features | `VK_KHR_acceleration_structure` + `VK_KHR_ray_query` (ray tracing), `fillModeNonSolid` (wireframe) | detected at start-up; every feature has a fallback path |
| Shaders | GLSL 4.60 → SPIR-V, compiled at run time with shaderc (cached on disk) and verified by a unit test | no Vulkan SDK needed on the user's machine; hot reload while developing |
| Windowing / input | GLFW (Hexa.NET.GLFW) | multi-window support needed for ImGui multi-viewport |
| UI | Dear ImGui **docking + multi-viewport** (Hexa.NET.ImGui 2.2.9, native GLFW + Vulkan backends), ImGuizmo for move/rotate gizmos | dockable tabbed panels with drop indicators, panels torn off onto other monitors |
| 3D import | AssimpNet (OBJ, FBX, DAE, 3DS, STL, PLY, glTF, …), SharpGLTF (glTF/GLB), own 3DS reader for GDTF models | textured models from common DCC tools |
| Images | StbImageSharp / StbImageWriteSharp | PNG/JPG/TGA/HDR in, PNG out (public domain) |
| Tests | xUnit; render tests run on **lavapipe** (Mesa software Vulkan) in the cloud/CI and on real GPUs locally | golden-image comparison with tolerance |

SketchUp `.skp` is a closed format without an open reader; scenes in `stages/*.skp` have to be exported from SketchUp to
DAE/FBX/glTF first. `stages/Фокина.mvr` (Depence export, 91 fixtures) is imported directly.

## 2. Conventions (all modules)

**Units.** SI everywhere in code: metres, kilograms, seconds, watts, volts, amperes, degrees only at UI/file boundaries
(radians inside math). Photometry: luminous intensity in **candela (cd)**, illuminance in **lux (lx)**, luminance in
**cd/m²**, luminous flux in **lumen (lm)**. Sound: dB(A) SPL.

**Coordinates.** Right-handed, **Z up**, X right, Y forward (into the stage) — the same as MVR/GDTF, so no axis swaps on
import. MVR stores millimetres → divide by 1000 on import. GDTF geometry is already in metres. Rotations are
`System.Numerics.Quaternion`; matrices are `System.Numerics.Matrix4x4` (row-vector convention of System.Numerics:
`v' = v * M`). Vulkan clip space (Y down, depth 0..1) is handled only inside the renderer's projection matrices.
Reverse-Z depth in the renderer.

**Colour.** Scene-referred linear **Rec.2020** is the working RGB space (wide enough for saturated LED colours).
Chromaticities come in as CIE 1931 xyY (GDTF wheel slots, emitters) or spectra and are converted with
`Candela.Photometry`. Light colour is stored as *chromaticity normalised to luminance Y = 1* plus a photometric
magnitude (cd or lm), so "how bright" and "what colour" never mix. Albedo textures are sRGB-encoded and converted to
linear Rec.2020 on load. Display output goes through exposure (EV100, physical camera), tone mapping (AgX default, ACES
option) and the display transform (sRGB; HDR later).

**Identity.** Every scene object is an `Entity` with a `Guid` id (MVR uuids are kept). Assets (imported models, images,
GDTF files) are referenced by asset id or GDTF file name, never by absolute path.

**Threading.** One main thread owns the scene, UI and Vulkan submission. Network receivers run on their own threads and
only write into lock-free DMX universe buffers. Long jobs (GDTF loading, thumbnails, path-traced renders, imports) run
as background tasks and hand results back to the main thread through a queue. No module touches the scene from a
background thread.

**Code style.** `.editorconfig` at the root; `dotnet format --verify-no-changes` is the lint gate. Public types get
XML doc comments explaining *why*/units. No static mutable state except well-named caches. No allocations in per-frame
hot paths (use `Span<T>`, pooled arrays, struct arrays).

## 3. Solution layout and dependencies

```
Candela.sln
src/
  Candela.Core             scene graph + component data, math, units, undo/redo, selection, issues, presets, settings
  Candela.Photometry       colour science (CIE, CCT, spectra, filters), beam intensity distribution, lux/cd math, acoustics
  Candela.Gdtf             GDTF reader (fixture type, geometry tree, DMX modes, wheels, emitters, wiring), 3DS/glTF
                           models, fixture library index + _specs physical data
  Candela.Dmx              universes, patch, DMX → attribute → physical fixture state, motor dynamics
  Candela.Protocols        Art-Net, sACN, (OSC, PSN, EntTec USB Pro, …) → DMX input hub, timecode
  Candela.Electrical       cable/connector catalogue, device ports, cable graph, power + data network solver, checks
  Candela.Simulation       smoke/haze fluid simulation, fire, acoustic field (CPU reference implementations)
  Candela.Mvr              MVR import/export
  Candela.Assets           3D model / image import, built-in material library, thumbnails
  Candela.Project          `.candela` project archive (save / load / merge-import)
  Candela.Engine           the running world: ties scene + DMX + protocols + simulation + analysis, builds render input
  Candela.Rendering        render-API-neutral render world, views, settings, renderer interface
  Candela.Rendering.Vulkan Vulkan renderer: real-time pipeline + path tracer
  Candela.App              the editor (GLFW + ImGui docking/viewports)
  Candela.Cli              headless tool: render scene → PNG, validate, benchmark (used by tests and in the cloud)
tests/
  Candela.<Module>.Tests   xUnit, one per module
docs/
```

Dependency rule (no cycles; arrows point to what a project may reference):

```
Core ← Photometry ← Gdtf ← Dmx ← Protocols
Core ← Electrical (→ Gdtf)          Core ← Simulation (→ Photometry)
Core ← Mvr (→ Gdtf)                 Core ← Assets
Core ← Project (→ Assets)
Engine → Core, Photometry, Gdtf, Dmx, Protocols, Electrical, Simulation, Mvr, Assets, Project, Rendering
Rendering → Core, Photometry          Rendering.Vulkan → Rendering
App → Engine, Rendering.Vulkan        Cli → Engine, Rendering.Vulkan
```

`Candela.Core` holds **all persisted component data classes** (plain data, no behaviour that needs other modules). Logic
lives in the module that owns it (e.g. cable network solving in `Candela.Electrical` works on `CableComponent` data
from Core). This keeps project serialisation in one place and lets every module read the same scene.

## 4. Scene model (`Candela.Core`)

* `Scene` — the document: name, layers, root entities, `SceneSettings` (units display, atmosphere domain defaults…).
* `Entity` — id, name, parent/children, local `Transform` (position m, rotation quaternion, scale), layer, visible,
  locked, list of components. World matrices are computed on demand and cached with a dirty flag.
* Components (data only, polymorphic JSON with a `$type` discriminator):
  * `FixtureComponent` — GDTF file + mode, DMX patch (universe/address per DMX break), fixture id, unit number, gel
    colour, cast shadow, mount (entity it hangs on), manual attribute values (used when not driven by a console).
  * `MeshComponent` — imported model asset + material overrides. `WallComponent` — parametric wall/floor/ceiling
    rectangle (width, height, thickness, material). `ImageComponent` — reference drawing or poster (image asset, size,
    unlit reference vs lit poster, opacity).
  * `TrussComponent` / `RiggingPointComponent` — structure that carries load (capacity kg), what fixtures mount on.
  * `CableComponent` — a cable *tree*: nodes (position, optional anchor to an entity), segments between nodes (branching
    allowed), endpoints connected to device ports (`entity id + port name`), cable type id, label.
  * `ConsoleComponent` — a lighting console model: its DMX/network outputs (port → universe) and which external
    protocol input feeds it. Fixtures that are cabled to a console are *console-driven* and their controls are locked.
  * `PowerSourceComponent` — mains outlet / distro: connectors, voltage, phases, breaker rating per outlet.
  * `AtmosphereVolumeComponent` — simulation domain (bounds, resolution, base haze, dissipation).
  * `CameraComponent` — saved views.
* `Selection` (multi-select, Shift adds), `UndoStack` (every edit is an `IUndoableCommand`), `Issue` + `IIssueProvider`
  (severity, category, message, related entities), `PresetLibrary<T>` (read-only *Default*, edited → *Custom*,
  *Save as…* under a name; stored per user), `KeyBindings`, `AppSettings` (UI scale ≥ 100 %, theme, library folders,
  network adapter, …).

## 5. Fixture data (`Candela.Gdtf`)

GDTF is the source of truth for geometry, DMX behaviour, wheels/gobos, emitters and wiring; `_specs/fixtures_index.json`
(researched data, see `_specs/SCHEMA.md`) adds what GDTF files usually lack: category, real power draw, weight,
max candela, noise, connectors, load capacity of distribution devices. `FixtureLibrary` scans library folders,
indexes by manufacturer / name / category without parsing everything, parses a GDTF on first use and caches the
result (binary cache keyed by file hash). MVR-embedded GDTFs that are stubs (e.g. Depence exports) are matched to
the library by manufacturer + model name.

## 6. DMX and fixture state (`Candela.Dmx`, `Candela.Protocols`)

`DmxUniverse` (512 bytes, sequence number, source, timestamp) ← protocol receivers (Art-Net incl. ArtPoll reply,
ArtTimeCode, ArtSync; sACN E1.31 multicast/unicast with priority; more protocols behind `IExternalProtocol`, see
roadmap for the full list). `Patch` maps fixtures to universe/address. `FixtureEvaluator` turns DMX bytes into GDTF
attribute values (coarse/fine/ultra, channel functions, channel sets, defaults, virtual dimmer, relations) and then
into a **physical state**: pan/tilt angles, intensity 0..1, emitter mix, CMY, CTO, colour/gobo wheel positions and
rotation, prism, frost, iris, zoom angle, focus, shaper blades, strobe, fog/haze output, fan speed, flame output.
Motor dynamics (pan/tilt/zoom/wheels move at finite speed) are integrated per frame so movement looks right between
44 Hz DMX frames.

## 7. Photometry (`Candela.Photometry`)

* Colour: CIE 1931 2° observer, xyY ↔ XYZ ↔ linear Rec.2020/Rec.709, Planckian locus and CCT (Ohno), blackbody
  spectra, additive emitter mixing by luminous flux, subtractive filters (CMY, gels, colour wheel) by spectral
  transmission when available, otherwise in xyY with a luminous transmission factor.
* Beam: intensity distribution `I(θ)` from GDTF beam/field angles and beam type (normalised profile × peak cd), with
  peak cd from `_specs` (`maxLuminousIntensityCd`) or flux; zoom changes the angles while keeping flux.
* `E = I(θ)·cos α / d²` (lux), surface luminance for Lambertian surfaces `L = ρ·E/π`, light-meter queries at points
  and on grids (CPU reference; the GPU lux overlay must agree with it — tested).
* Acoustics: free-field SPL `L(d) = L₁ₘ − 20·log₁₀(d)`, energetic sum of sources (noise overlay).

## 8. Rendering (`Candela.Rendering`, `Candela.Rendering.Vulkan`)

Real-time pipeline (Full mode):
1. GPU scene update (instances, materials bindless, lights from fixture states).
2. Shadow / cookie atlas for spot lights (prioritised by contribution; gobos, iris, blades, prism, frost are part of
   the light's projected cookie).
3. Depth pre-pass → clustered light culling (compute).
4. Forward PBR shading (GGX + Lambert) with physically based spot lights in cd, pre-exposed HDR (RGBA16F).
5. Volumetrics: froxel grid (in-scattering with Henyey–Greenstein phase, density from global haze + smoke simulation
   volume + fire emission, shadowed and cookie-masked) + temporal reprojection; dedicated cone ray-marching for the
   brightest narrow beams (sharp gobo beams).
6. Indirect light ("light reflected onto neighbouring surfaces"): reflective shadow maps for spot lights on any GPU;
   ray-query one-bounce GI + reflections when ray tracing is available.
7. Bloom, physical-camera exposure, tone mapping, selection outline (vivid orange #FF7A00), grid, overlays.

View modes: **Wireframe** (edges), **Colorless** (grey shaded geometry with a headlight), **Full** (everything above,
tuned through the *Preview* settings). Projection: perspective / orthographic with top/front/right presets; any number
of viewports.

Overlays: **lux** (per-pixel illuminance from all lights mapped through an editable gradient; outside the chosen range
→ grey), **noise** (SPL on surfaces and as tinted fog in the volume), **cables** (scene greyed out, cables coloured by
type, legend).

Offline/final render: progressive GPU path tracer (ray query) with the same light/material/medium model, heterogeneous
participating media (smoke volume, delta tracking), emissive fire, many-light sampling; frames or a timecode range
(DMX recorded from the live input) to PNG/EXR sequences.

## 9. Simulation (`Candela.Simulation`, GPU versions in the renderer)

* Smoke/haze: Eulerian grid over the atmosphere domain — semi-Lagrangian advection, buoyancy, diffusion, dissipation,
  pressure projection; sources are HAZE/FOG fixtures (output from DMX), FAN fixtures add velocity; obstacles voxelised
  from scene geometry. The density field feeds the volumetric renderer (σₛ = density × scattering coefficient).
* Fire: flame jets of GENERATOR FIRE fixtures driven by DMX — temperature/fuel field or particles, blackbody emission
  (temperature → spectrum → colour + luminous output) that lights the scene and produces smoke.
* The CPU implementation is the reference for tests; the GPU implementation runs in real time.

## 10. Cabling and power (`Candela.Electrical`)

Device ports come from GDTF `WiringObject` geometry (connector type, input/output, signal type, max payload) and the
`_specs` connector fields. A cable catalogue defines types (DMX XLR3/XLR5, Ethernet, powerCON / TRUE1, Schuko, CEE 16/32/
63 A, Socapex, …), colours, current rating, weight per metre. The network solver builds data networks (console port →
daisy chain; Y-splits without a splitter are flagged) and power networks (source → links/distros/dimmers → loads), sums
loads (`powerConsumptionW`, `maxLoadW`, …), and reports issues: overload, wrong connector, unpowered fixture, fixture
without data, address overlap, DMX chain too long, cable weight on trusses, fixtures not mounted, truss overload.

## 11. Project file (`Candela.Project`)

`.candela` = zip: `manifest.json` (format + version), `scene.json` (entities/components), `settings.json`
(view/render/overlay settings of the project), `assets/` (imported models and images, original files kept),
`fixtures/` (copies of every GDTF used, so a project opens on another machine), `thumbnail.png`. Loading migrates older
versions. *Import* can merge another project, an MVR, a 3D model or an image into the open scene.

## 12. Testing in the cloud

`dotnet test Candela.sln` runs everything. Rendering tests need a Vulkan device: lavapipe in the cloud (installed by
`.claude/hooks/session-start.sh`), a real GPU locally; they are skipped when no device exists. `Candela.Cli render`
produces PNG frames that are checked against golden images and can be inspected by eye. The editor runs under Xvfb for
screenshots.
