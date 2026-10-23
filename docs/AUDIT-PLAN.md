# Candela: consolidated audit results and remaining work plan

> **Status (2026-10-23).** Done and merged: WP-01, WP-02, WP-03, WP-04, WP-05, WP-07 (CPU worker thread; the optional
> GPU solver is not built). Open: WP-06 and WP-08 … WP-27. The audit artefacts (scripts, screenshots) are not in the
> repository; each package below carries its evidence, scope and acceptance criteria. See [HANDOFF.md](HANDOFF.md).


## 1. Summary

- **What was audited:** 10 areas with 129 requirement entries in total.
  - 66 are met.
  - 61 are partial.
  - 2 are missing: smoke controls in the editor, and SketchUp import.
- **Deduplication:** the 63 open entries contain many repeats. The repeats were merged; the main ones are:
  - Path tracer not available in the editor: 4 entries.
  - Work light not connected in the editor: 4 entries.
  - Trusses and rigging: 4 entries.
  - Light leaking through geometry: 3 entries.
  - SketchUp import: 2 entries.
  - Settings window cannot dock: 2 entries.
  - Too many low-value issues in the Issues list: 3 entries.
  - Light plot only in the CLI: 2 entries.
  - Smoke and light interaction: 2 entries.
- **Result:** the remaining work is grouped into 27 work packages (WP).
- **Priority levels:** the audits rated items high, medium or low. I added a P0 (critical) level above "high" for gaps that are both on the default path and silent: the user sees wrong or invisible results, or wrong data is exported, with no warning. That moved four high items to P0.
  - P0: critical, on the default path and silent.
  - P1: high, a core requirement cannot be reached in the editor.
  - P2: medium.
  - P3: low or polish.
- **Size:** S is up to about 3 days, M is 1–2 weeks, L is more than 2 weeks.

| WP | Title | Prio | Size | Main modules | Depends on |
|---|---|---|---|---|---|
| 01 | Editor lighting baseline (work light, ambient, floor, reference drawings) | P0 | S–M | App/Session, App/Panels/PreviewPanel, Engine, Assets/Procedural/ImagePlaneMesh | – |
| 02 | DMX evaluation correctness (wheels, prisms, shaper, LED presets, white point) | P0 | M | Candela.Dmx, Candela.Photometry | – |
| 03 | MVR export fidelity | P0 | M | Candela.Mvr, Engine/StageEngine.Files.cs (ExportMvr) | – |
| 04 | Lux overlay correctness on large rigs | P0 | S–M | Rendering.Vulkan (analysis.frag, cluster.comp, VulkanRenderer) | – |
| 05 | Path-traced final render in the editor | P1 | M | App/Session/RenderJob, App/Panels/RenderPanel, EditorSession, Menus | – |
| 06 | Light occlusion for all sources (fixtures, flames) | P1 | L | Engine/Lighting, Rendering.Vulkan (shadows, pt_trace, beam_scatter), LightMeter | 04 (analysis.frag only) |
| 07 | Smoke solver off the main thread / on the GPU | P1 | L | Candela.Simulation/Smoke, StageEngine.Tick | – |
| 08 | Atmosphere domain behaviour (engine) | P1 | M–L | Engine/StageSimulation, AtmosphereVolumeComponent, CLI | – |
| 09 | Atmosphere and simulation controls in the editor | P1 | M | App (new object, Preview Simulation section, viewport gizmo) | 08 |
| 10 | Rigging and stage building (trusses, mounting, load) | P1 | L | Candela.Electrical/Rigging, Assets/Procedural, App Library/Placement | – |
| 11 | Import path (SketchUp, placement, merge re-patch) | P1 | S (a) / L (b) | App/Workspace.Session, import worker, Engine import | – |
| 12 | Real-time bounce light and reflections | P1 | L | Rendering (RenderTypes, LightPlanner, rt_gi, new reflections pass, forward.frag) | 01 (forward.frag), 05 (RenderPanel) |
| 13 | Console binding and protocol subscription correctness | P2 | M | Candela.Protocols, StageEngine, Dmx/DmxEngine, App/ExternalControl | – |
| 14 | Overlay UX and visuals (legend, probe, luminance, cable overlay) | P2 | M | App/Viewport/OverlayLegend, ViewportPanel, tonemap.frag, EffectPasses | 04 |
| 15 | Smoke in the light path (transmittance, smoke shadows) | P2 | M–L | Shaders: lighting.glsl, froxel_inject, beam_scatter, volume.glsl | 06 |
| 16 | Effect devices: fans and flame units (content and DMX) | P2 | M | Library content/_specs, Dmx FixtureProfile.Builder, GenericDeviceLibrary | 02 |
| 17 | Fire physics and appearance (wind, obstacles, soot) | P2 | M | Candela.Simulation/Fire, SmokeSimulation, volume.glsl | 07, 15 |
| 18 | Timecode-range render (video, recording, deterministic smoke) | P2 | M | App RenderJob (sequence), Candela.Project, DmxRecorder | 05, 07 |
| 19 | Cable geometry and cable-tool polish | P2 | S–M | Assets/CableTubeMesh, RenderSync.BuildCable, App cable UI | – |
| 20 | Viewport navigation (view lock, 4-view framing, picking) | P2 | S–M | App/Viewport, Workspace.ApplyPreset | – |
| 21 | Library management (persistence, thumbnails, categories) | P2 | M | App/LibraryPanel, ModelLibrary, ThumbnailWorker | 10 (LibraryPanel tab) |
| 22 | Fixture Properties: illustrations and live figures | P2 | M | App/DeviceFactsView, FixtureInspectorView, FixtureControls, Photometry/Cct | – |
| 23 | Real-time scalability on real rigs | P2 (benchmark now) | L | LightPlanner, StageLightCollector, ShadowAtlas | 06, 12 (optimisation part) |
| 24 | Docking, windows and platform | P2 | M | App/Docking, SettingsWindow, GlfwPlatform, FileDropQueue, UiScale | – |
| 25 | More external protocols | P2 | L | Candela.Protocols, Engine tracker binding | 13 |
| 26 | Issues quality and electrical reports | P3 | M | Electrical/Reports+Issues, IssuesPanel, CLI validate | – |
| 27 | Project save extras and more export formats | P3 | M | EditorSession/StageEngine save, Export kinds, Light Plot panel | 03 |

---

## 2. Work packages in priority order

### P0: Critical

#### WP-01 Editor lighting baseline
- **Impact:** Full is the default shading mode. Today, a freshly opened MVR, an imported model, a poster or a reference drawing all show as black. This blocks evaluating almost everything else.
- **Sources:**
  - viewport/Full shading
  - viewport/Ambient row
  - library/3D formats invisible
  - library/reference drawings
  - library/posters
  - engine-light/floorless MVR
  - fixture-state/Фокина black on open
- **Scope:**
  - `src/Candela.App/Session/EngineViewportContent.cs`
  - `src/Candela.App/Panels/PreviewPanel.cs`
  - View menu item
  - Move `AddFloorIfMissing` from `src/Candela.Cli/RenderCommand.cs` into Candela.Engine
  - Path from `SceneSettings.AmbientLuminance` through RenderSync to ViewOptions, the forward.frag ambient term and FinalRenderOptions
  - `src/Candela.Assets/Procedural/ImagePlaneMesh.cs` (CreateMaterial)
  - The reference/unlit branch in forward.frag (lines 77–87)
- **Work:**
  - Set `RenderSync.WorkLight = engine.View.WorkLight` (default Auto) every frame.
  - Add a Work light Off/Auto/On control in Preview and in the View menu.
  - Wire `AmbientLuminance` into the real-time renderer and the path tracer, or remove the row.
  - When a floorless MVR is opened, offer or add a stage floor as an undoable step. Use the same Engine code as the CLI's `--floor auto`.
  - Fix the reference-drawing material: set `BaseColorTexture` and `BaseColorFactor = (1,1,1,opacity)`. Check the path tracer.
- **Acceptance criteria:**
  - After opening `stages/Фокина.mvr`, `Binding.IsWorkLightOn` is true (App test).
  - A textured OBJ dropped into an empty scene has non-black mean luminance in Full, and its texture is visible (headless screenshot test).
  - The Work light setting is saved with the project view settings and restored on reopen. With Off, the current behaviour returns.
  - Changing Ambient from 0 to 30 cd/m² measurably changes the luminance of an unlit object, in real time and in the path tracer (test). If the row is removed instead, this does not apply.
  - Opening a floorless MVR offers or adds a floor, and Undo removes it. The CLI floor uses the same Engine helper.
  - A reference drawing shows image pixels in Full, in Colorless and in a CLI render. A GPU test asserts non-black pixels that match the source image. Posters are unchanged.

#### WP-02 DMX evaluation correctness
- **Impact:** wheels and prisms are inserted on their "Open" functions. In the reference scene, every Rivale (27 fixtures) projects the GD053 animation at open white. The scan found 20 library files with the same pattern. Other defects:
  - Beam shaper selection is lost.
  - LED colour presets come out the wrong colour.
  - Фокина's open white renders violet.
- **Sources:**
  - fixture-state/all attributes (high)
  - fixture-state/Фокина white point
  - engine-light/MVR gel colour
  - windows/beam-field contradiction
- **Scope (Candela.Dmx and Candela.Photometry):**
  - `FixtureProfile.Builder.BuildSlotMap`
  - `FixtureEvaluator.ApplyWheel` (around line 914)
  - BeamShaper control value
  - Colour-wheel handling on additive LED engines
  - Emitter compilation and the white-point check against _specs
  - Applying `FixtureComponent.Color` as an xyY filter for fixtures without their own colour system
  - Derivation of beam and field angle
- **Acceptance criteria:**
  - New library-wide regression test: at GDTF defaults, and on every set named open/removed/close or with WheelSlotIndex 0, no wheel, animation wheel or prism is inserted. This covers Rivale AnimationWheel1, ACME Chiron Prism1, and Elation Artiste Picasso/Mondrian.
  - "Ramp open→full" functions give a partial insertion.
  - Фокина open-white render: no GD053 pattern. The Zonda full white lands within |Δuv| ≤ 0.02 of a CCT inside the _specs range of 2700–8000 K, and is marked as an estimate when rescaled.
  - Rivale BeamShaper at DMX 128 shows slot 2 and renders an elongated (anamorphic) beam.
  - Zonda SCARLET preset with RGBW at full: the output xy is within 0.02 of the slot's xy.
  - Zonda Basic "Liquid Effect" emits 0 cd at RGBW 0, or the GDTF is documented as being the cause.
  - An MVR fixture with `<Color>` and no colour system renders in that gel colour.
  - The derived field angle stays inside the GDTF zoom range, or is labelled as derived.
  - Candela.Dmx.Tests stay green (147 or more).

#### WP-03 MVR export fidelity
- **Impact:** exporting Фокина without any edits silently writes the wrong fixture type and DMX mode for 87 of 91 fixtures. In addition, model textures and layer membership are lost. A console or CAD tool receiving this file gets wrong data.
- **Sources:** file-menu/export fidelity, file-menu/import MVR (stand-in side effect).
- **Scope:**
  - `src/Candela.Engine/StageEngine.Files.cs` ExportMvr (lines 136–150)
  - Candela.Mvr writer (GLB textures through SharpGLTF, GroupObject and layers)
  - docs/mvr.md
- **Acceptance criteria:**
  - Round-trip test: open Фокина.mvr, export with no edits, and GDTFSpec/GDTFMode equal the source for all 91 fixtures. The fix uses `settings.engine.substitutes` (requestedSpec/requestedMode).
  - An export option "Write stand-ins" exists and is off by default. When it is on, each substituted fixture raises an `mvr.substitute-exported` warning.
  - A textured OBJ exported to MVR and reopened renders with its texture: the GLB contains images.
  - Layer counts survive export and reopen (project Default 237 / Light 39 stays 237/39). Children that span layers are written as one GroupObject per layer, or reported.
  - Every lossy change that cannot be avoided is reported in Issues and in the log.

#### WP-04 Lux overlay correctness on large rigs
- **Impact:** the light-meter overlay gives wrong values on the user's own rig, with no warning. Clusters with more than 512 overlapping beams drop lights, so on Фокина some floor tiles read about 50–100 lx where the true value is 300–1000 lx.
- **Sources:** overlays/lux overlay (high).
- **Scope:**
  - `Shaders/analysis.frag` (OVERLAY_LUX): sum all `frame.lightCount` lights, or use a separate uncapped per-tile list for this pass.
  - `cluster.comp` and `ShaderConstants.MaxLightsPerCluster`.
  - `VulkanRenderTarget.EnsureClusterCapacity`.
  - Read the `OverflowedClusters` counter back on the host.
  - OverlayTests.
- **Acceptance criteria:**
  - A new OverlayTests case with at least 1300 overlapping beams matches LightMeter within 1 % at the sample points.
  - The Фокина lux overlay (floor drawn, Open White) shows no tile-aligned rectangles, in the editor or in `--overlay lux`.
  - Any remaining overflow, in any pass, raises a visible warning in the legend or status bar and in the log.
  - Check whether Full shading also truncates at 512 lights per cluster, and record the answer. If it does, fix it here or hand it to WP-23 with the warning in place.

### P1: High

#### WP-05 Path-traced final render in the editor
- **Impact:** the main requirement for a slower, higher-quality final render is only met in the CLI. File → Render in the editor writes a supersampled real-time PNG. EXR, bounces and denoise are unavailable or have no effect.
- **Sources:**
  - engine-light/path tracer
  - engine-light/volumetric multiple scattering
  - file-menu/Render
  - windows/Render tab
  - windows/Render button
- **Scope:**
  - `src/Candela.App/Session/RenderJob.cs`: path-traced mode calling `VulkanRenderer.StartFinalRender` and `IFinalRender.Advance` within each frame's budget.
  - `Panels/RenderPanel.cs`: EXR output, bounce, volume-bounce and denoise rows, preview, progress, Cancel, "Open folder".
  - Delete the `EditorSession.StartFinalRender` stub.
  - `Workspace.Menus.cs:36`: F12 starts the render.
  - Remove the "not available" strings (`StageStrings.cs:860/873`, `Loc.cs:237`).
  - Update docs/editor.md and ROADMAP M3/M6.
  - Reuse the wiring from `RenderCommand` and `Diagnostics/OpenExr`.
- **Acceptance criteria:**
  - Rendering Фокina from the editor writes PNG and EXR files that match `candela render --final` (same camera and samples) within the noise tolerance.
  - Changing samples, bounces, volume bounces or denoise changes the output. One test per setting.
  - The editor stays responsive during the render, the progress image updates, and Cancel stops within one frame.
  - When `!SupportsRayTracing`, the render falls back to the real-time "Fast" mode and says so in the panel.
  - F12 and File → Render start a render with the panel's current settings.
  - No "not available yet" text remains, and the docs and ROADMAP are up to date.

#### WP-06 Light occlusion for all light sources
- **Impact:** any MVR with `CastShadow=false` (common in exports, and all 91 fixtures in Фокина) lights the scene through floors, walls and platforms, in both renderers. Flame light passes through walls. Lux readings behind objects are too high.
- **Sources:**
  - engine-light/occlusion (high)
  - smoke-fire/fire lights the scene (high)
  - engine-light/lux occlusion
- **Scope:**
  - `Engine/Lighting/StageLightCollector.cs:101` and `AddFlameHemisphere`.
  - A new per-fixture "light casts shadows" setting: default true, saved in the project, editable in `Panels/FixtureInspectorView.cs`.
  - `RenderSync.cs:780` keeps the MVR CastShadow flag for the fixture body only.
  - `pt_trace.comp`: always trace visibility; flame lights use ShadowCasterMask.
  - Point-light shadows (cube or dual paraboloid) for flames.
  - `beam_scatter.comp`: clip each unshadowed beam at the first opaque hit (ray query or coarse depth).
  - `analysis.frag`: ray-query occlusion, after WP-04 merges.
  - Occlusion in LightMeterService.
- **Acceptance criteria:**
  - Harness h3 (platform scene with Фокина's flags): the floor under the platform matches the shadows-forced reference (about sRGB 29/16/12 instead of 56/25/21), in real time and in the path tracer.
  - No beam shows in the haze below the stage front edge (rt-demo-front, pt-demo-front).
  - Fire-wall probe: lux behind the 8 m wall is less than 10 % of the open side, in the light meter, the overlay and both renderers.
  - Harness h5: under the platform, the lux overlay matches the occluding LightMeter within 1 %.
  - Scenes with more than 64 lights (beyond the shadow-map budget) still stop light at geometry (test).
  - The Photometric, Volumetric, PathTracerLight and Indirect tests stay green.

#### WP-07 Smoke solver off the main thread / on the GPU
- **Impact:** once any hazer, fogger or flame starts, the editor drops to 1–5 fps (185 ms average tick, up to 1.7 s per frame). Simulated time silently drops behind real time.
- **Sources:** smoke-fire/real-time (high).
- **Scope:**
  - `src/Candela.Simulation/Smoke/*`: worker thread with double-buffered density grids, cell size chosen from a time budget.
  - Optionally, a Vulkan compute port in Candela.Rendering.Vulkan, with the CPU solver kept as the reference.
  - `StageEngine.Tick` integration.
  - A status-bar warning when `LastStatistics.DroppedTime > 0` (small App hook).
- **Acceptance criteria:**
  - With one hazer running on Фокина, the main-thread engine tick costs less than 2 ms, and the viewport frame rate is within 10 % of the no-smoke case (on reference hardware).
  - Simulated time keeps up with wall time, or the user sees a dropped-time warning.
  - Conservation and determinism are unchanged: the closed-room probe still holds 1.599 g after 40 s, and CLI re-simulation is bit-stable.
  - If a GPU solver is built, it matches the CPU reference within the set tolerance in the solver tests.

#### WP-08 Atmosphere domain behaviour (engine)
- **Impact:** smoke is simulated but not usable in practice:
  - Haze vents out within seconds (2.2 % left after 30 s).
  - Reaching visible haze takes tens of minutes.
  - Sets and models do not block smoke.
  - The domain never grows to fit new content.
  - Low fog cannot be resolved at the default cell size.
- **Sources:**
  - smoke-fire/dissipation (high)
  - smoke-fire/obstacles (high)
  - smoke-fire/hazer visible (high)
  - smoke-fire/airflow (engine part)
  - smoke-fire/low-fog resolution
  - smoke-fire/CO2 emitter
- **Scope:**
  - Candela.Engine `StageSimulation`:
    - `UpdateDomain`: closed or "open above the rig" by default, sized to the venue, recreated when SceneBounds grows, finer cells or a floor sub-grid when low fog is present.
    - `UpdateObstacles`: model AABBs or a conservative voxelisation, MVR scene objects, trusses and fixture bodies through `ObstacleBox.FromLocalBox`.
    - `BuildEmitters`: a CO2/cryo emitter type, and a warning when a nozzle points into a solid.
  - `AtmosphereVolumeComponent`: add OpenFaces and Dissipation.
  - A Fill / fast-forward API built on `SmokeSimulation.Fill`.
  - CLI `--prehaze <1/m>` and a pre-hazed demo look.
- **Acceptance criteria:**
  - Dissipation probe with defaults: at least 70 % of the smoke remains 30 s after the hazer stops (dissipation alone leaves about 74 %).
  - The prehaze API reaches the target σ (for example 0.02/m) in under 1 s of wall time.
  - `candela render stages/Фокина.mvr --look show --haze 0 --prehaze 0.02` shows the beams.
  - An imported model or MVR set piece blocks smoke like the wall-divider probe (density about 0 on the far side).
  - Adding content outside the domain makes the domain grow.
  - With a low-fog device present, density at z = 0.1 m is greater than 0.
  - CO2_Blaster emits a cold, dense jet. A blocked nozzle raises a warning instead of silently emitting 0 g.

#### WP-09 Atmosphere and simulation controls in the editor
- **Impact:** the smoke system is a black box. The user cannot create, size, tune, reset or see the domain, and cannot set airflow.
- **Sources:**
  - smoke-fire/control (missing)
  - smoke-fire/airflow (UI part)
  - smoke-fire/dissipation (UI part)
- **Depends on:** the WP-08 API.
- **Scope:**
  - A new "Atmosphere volume" object (Tools menu and Devices tab) with a Properties editor for Size, CellSize, Dissipation, Scattering, Airflow, OpenFaces and Enabled.
  - A Simulation section in Preview or Settings: smoke and fire on/off, automatic box, cell size, Reset smoke, Fill with haze, Fast-forward N minutes.
  - A domain gizmo in the viewport.
  - Smoke statistics in the status bar.
- **Acceptance criteria:**
  - Create, resize, edit and delete an atmosphere volume. Every change can be undone and is saved in the project.
  - The fire-wind probe can be reproduced through the UI alone, without editing scene.json.
  - "Fill with haze" makes the Фокина beams visible with base haze at 0.
  - Reset clears all density.
  - The domain box is visible while the simulation runs or the volume is selected.

#### WP-10 Rigging and stage building
- **Impact:**
  - The user cannot create a truss, pipe or other rigging object.
  - Fixtures dropped on a truss are reported as not mounted.
  - Truss overload, the requirement's main Issues example, is never detected.
  - On Фокина, 89 not-mounted warnings bury the useful issues.
- **Sources:**
  - windows/not-mounted (high)
  - windows/overweight (high)
  - library/built-in objects
  - cabling/attach to mounts
  - library/ceiling tool
  - fixture-state/not-mounted noise
- **Scope:**
  - `Candela.Electrical/Rigging`: RiggingAnalyzer load tables by profile and span, `rigging.unknown-rating` note, collapse not-mounted warnings when a scene has no rigging objects.
  - `Candela.Assets/Procedural`: TrussMesh, TrussProfile, Primitives.
  - App LibraryPanel: new "Objects" tab with truss F34/F44/X30V, pipe/batten, tower, rigging point, riser, LED screen and primitives.
  - `LibraryPlacement.PlaceObject`, `PlaceFixture`, Placement.cs and EditOperations: set MountedOn on drop or move.
  - Properties: a "Mounted on" row with Attach/Detach, and a truss section (length, profile, ratings).
  - Ceiling tool in `Editing/WallShapes.cs`.
- **Acceptance criteria:**
  - Each listed object can be placed from the Library, and the cable tool anchors to it.
  - truss2 scenario: dropping AECO 15 on the truss sets MountedOn, with no false not-mounted warning.
  - 60 Rivale Washes (1878.8 kg) on a 3 m F34 raise a truss-overload error. A truss with no rating raises an unknown-rating note. Ratings can be edited.
  - Фокина: the 89 not-mounted warnings collapse into one note.
  - The ceiling tool creates `WallKind.Ceiling` at a height the user types.

#### WP-11 Import path (SketchUp, placement, merge)
- **Impact:** 4 of the user's 5 test stages are `.skp` files. They cannot be imported, and the editor does not even show the conversion hint. Menu imports land out of view, and merges overlap existing objects and DMX addresses.
- **Sources:**
  - library/SKP (missing)
  - file-menu/import 3D object
  - file-menu/import project and MVR placement
- **Scope:**
  - (a) `Workspace.Session.cs:399` drop handling and `ShowModelImport`: send `.skp` to `AssetFormats.SketchUpMessage` and list it in the import filter. Size S, do now.
  - (b) A SketchUp reader in the isolated import worker, using the SketchUp C SDK (Windows/macOS). Size L.
  - (c) Place menu imports at the centre of the active view (ray to floor) and frame them.
  - (d) A popup after project or MVR merge: place at cursor or keep coordinates, and "Patch to next free addresses", reusing the Engine's duplicate re-patch logic.
- **Acceptance criteria:**
  - Dropping any `stages/*.skp` shows the export hint.
  - After (b), all four `.skp` files import with textures on the Windows/macOS build.
  - A menu-imported model in Фокина is selected and framed in the active view.
  - Importing fokina.candela into itself with re-patch gives 0 `patch.shared-address` warnings (today: 550).

#### WP-12 Real-time bounce light and reflections
- **Impact:** for typical imported rigs the editor shows no bounce light by default (RSM gives 0 virtual point lights on Фокина), and there are no glossy reflections in real time.
- **Sources:** engine-light/bounce (high).
- **Scope:**
  - `Candela.Rendering/RenderTypes.cs` and the App preview defaults: use RayTraced indirect light when `SupportsRayTracing`.
  - `LightPlanner.cs:155-187`: RSM tiles for the top lights even when they do not cast shadows.
  - `rt_gi.comp`: a light budget per fixture instead of per beam.
  - A new RT reflections pass on the existing TLAS, or SSR in forward.frag.
  - `RenderPanel.HighQuality` uses RayTraced (coordinate with WP-05).
- **Acceptance criteria:**
  - Фокина with a floor: the default mode gives mean |GI−Off| clearly above noise on lit walls (target > 2/255 in harness h2, today 0.10/255).
  - In RSM mode, the virtual point light count is greater than 0.
  - IndirectTests are extended to cover lights that do not cast shadows.
  - A glossy or mirror floor test scene reflects beams and lit surfaces in real time.
  - All 91 Фокина fixtures contribute to GI.
  - The RenderJob code comment and the actual behaviour agree.

### P2: Medium

#### WP-13 Console binding and protocol subscription correctness
- **Sources:**
  - cabling/console fixtures
  - fixture-state/re-subscription
  - fixture-state/lock (Open White, hint)
  - cabling/Art-Net port text
  - windows/ExternalControl status line
- **Scope:**
  - `Candela.Protocols/ProtocolManager`: tag each source by protocol.
  - `StageEngine.IsConsoleDriven`: drive only when the console's input is enabled and its universes are live.
  - `Dmx/Engine/DmxEngine.Update`: read the universe the cable carries.
  - `App/Session/ExternalControl.cs:505`: re-apply protocols when the patch universes change.
  - ExternalControlPanel: fixed-height status line, and the configured port in the description text.
  - Lock text in FixtureControls and ConnectionInspectorView (around lines 255–270).
  - `Engine/Fixtures/StageLooks.cs`: ApplyOpenWhite skips console-driven fixtures.
- **Acceptance criteria:**
  - Console Input set to sACN with sACN off: the cabled fixture stops following Art-Net, shows "console offline", and can be controlled by hand.
  - A console with no live input leaves its fixtures in manual control.
  - Fixture #112 (patched 2.001, cabled into the U1 line) follows U1, or driving is blocked with a clear message. The lock text always names the universe actually applied.
  - After opening a scene that uses universes 1–8, ArtPollReply announces 8 universes and sACN joins multicast for 1–8 (test).
  - Open White reports how many fixtures it skipped.
  - When consoles exist, an uncabled fixture on a live universe shows a hint.
  - Recorder status messages no longer move the buttons below them.

#### WP-14 Overlay UX and visuals
- **Sources:**
  - overlays/legend window
  - overlays/range bar
  - overlays/cable overlay grey
  - overlays/cable highlight
  - overlays/named preset adopt
  - overlays/noise fog strength
  - engine-light/lux readout
  - engine-light/luminance
- **Depends on:** WP-04 (shared analysis.frag).
- **Scope:**
  - `App/Viewport/OverlayLegend.cs`: collapse, clamp below the toolbar, cap height at about 50 %.
  - Lux and luminance probe in `ViewportPanel`, using `VulkanRenderer.ReadAnalysis` (made public) or `LightMeter.Breakdown`.
  - Show the actual minimum and maximum, a "Fit to view" button, and separate under-range and over-range colours.
  - `OverlayMode.Luminance` in cd/m².
  - Cables overlay: colorless or headlight shading independent of stage light, and an outline or tint in the cable colour for connected devices (EffectPasses mask, tonemap.frag).
  - Noise fog strength and toggle carried through ViewOptions.
  - `PresetBinding.Adopt` matches named presets.
- **Acceptance criteria:**
  - Four Viewports at 150 % UI scale: the legend never covers the toolbar and the overlay selector can be clicked.
  - The legend can be collapsed per viewport.
  - The probe value and the per-fixture breakdown match LightMeter within 1 %.
  - Fit to view sets the range to the scene minimum and maximum.
  - Luminance overlay with a point readout.
  - Cables overlay in Full on a dark rig shows readable grey geometry, and connected devices are clearly highlighted.
  - Noise fog can be adjusted and turned off.
  - A project whose gradient equals a named preset reopens showing that preset's name.

#### WP-15 Smoke in the light path
- **Sources:** engine-light/smoke interaction, smoke-fire/smoke and beams.
- **Depends on:** WP-06 (`beam_scatter.comp`).
- **Scope:**
  - A low-resolution optical-depth or deep-shadow volume computed from the smoke grid.
  - Use it in `lighting.glsl` (`lightPathTransmittance`), `froxel_inject.comp` and `beam_scatter.comp` (`smokeLightTransmittance`, replacing the 3-point estimate).
  - Ambient/RSM in-scattering in `volume.glsl`.
  - Update docs/rendering.md.
- **Acceptance criteria:**
  - In the fogbeams scene, the floor pool under dense fog (σ about 27/m) is darkened to within 10 % of the path-tracer reference.
  - Smoke casts shadows on surfaces and on the lit air.
  - Unlit fog in front of a lit wall no longer renders as a black silhouette.
  - VolumetricTests are extended, and the frame-time cost is documented.

#### WP-16 Effect devices: fans and flame units
- **Sources:**
  - smoke-fire/fans
  - smoke-fire/GENERATOR FIRE
  - library/categories coverage (FAN and FIRE empty, light-emitting OTHER items)
- **Depends on:** WP-02 (shared `FixtureProfile.Builder`).
- **Scope:**
  - Ship at least one generic DMX wind machine (`Engine/Cabling/GenericDeviceLibrary.cs`, plus a GDTF with a _specs record) and at least one real flame-projector GDTF.
  - Dimmer/Control fallback for the FAN category in `FixtureProfile.Builder.BuildOutputsAndModifiers`.
  - Model the flame safety/arm channel.
  - Show flame height, heat release and meaningful cd/lm in FixtureInspectorView instead of "0 cd".
  - Recategorise VC-Grid, Dotron, Starburst and TabledC through `_specs` overrides.
- **Acceptance criteria:**
  - The FAN and GENERATOR FIRE chips each list at least one device that can be placed. When driven by DMX, the fan moves smoke and the flame unit burns.
  - A FAN GDTF whose speed channel is Dimmer or Control gets a fan output (test).
  - No flame appears unless the unit is armed.
  - The four light-emitting OTHER fixtures appear under a light category.

#### WP-17 Fire physics and appearance
- **Sources:**
  - smoke-fire/fire physics
  - smoke-fire/soot absorption
  - smoke-fire/flame visuals
- **Depends on:** WP-07 (SmokeSimulation) and WP-15 (`volume.glsl`).
- **Scope:**
  - `FireSimulation.Step`: advect parcels with `SmokeSimulation.VelocityAt` or a Froude-number tilt correlation, and stop parcels at solid cells.
  - A separate soot scalar with absorption in AtmosphereState, `volume.glsl` and the path-tracer media.
  - Flame grid detail: higher resolution near the base and noise detail.
- **Acceptance criteria:**
  - Fire-wind probe: the flame tilts downwind and stays attached to its smoke.
  - A fan aimed at a flame bends it.
  - Parcels stop at walls.
  - Soot-heavy smoke renders visibly darker in real time and in the path tracer.

#### WP-18 Timecode-range render
- **Sources:**
  - file-menu/timecode range
  - smoke-fire/timecode consistency
  - file-menu/save (recording not stored)
- **Depends on:** WP-05 (RenderJob) and WP-07.
- **Scope:**
  - RenderJob sequence path: MP4 H.264 output through bundled or detected ffmpeg (Media Foundation on Windows), and a path-traced sequence option.
  - Store `.cdmx` recordings as project assets in Candela.Project.
  - Save simulation snapshots during recording and playback, and restore them (or re-simulate) before the first frame.
  - RenderPanel TimeRows: labels "From / To (timecode or s)", and wrapped, non-duplicated errors.
- **Acceptance criteria:**
  - A rendered range plays as an MP4 with the correct frame count and rate.
  - The recording is still present after saving and reopening the project.
  - Rendering a range twice gives identical haze. That haze equals the haze of continuous playback at that timecode, and does not depend on the live smoke at render time.

#### WP-19 Cable geometry and cable-tool polish
- **Sources:**
  - cabling/routing (medium)
  - cabling/menu shortcut
  - cabling/node labels and arrow nudge
  - cabling/port picker cosmetics
  - cabling/plugged-into rows
- **Scope:**
  - Merge the existing "cables on floors do not sag below them" branch.
  - `Assets/Procedural/CableTubeMesh.cs`: no sag between nodes on the same surface, and clamp to the highest support below.
  - `RenderSync.BuildCable` (around line 612) passes the support surfaces.
  - `CableNodeEdits.SetWorldPosition` keeps Z at or above the floor.
  - Remove the empty label box in `ViewportPanel.Cables.cs`.
  - Wrap reasons in `PortPicker.cs` and show port names next to the key hints.
  - `ConnectionInspectorView.cs:664-665` shows fixture ID and address.
  - Show the C shortcut on Tools → Cable.
  - Optional: arrow-key nudging.
- **Acceptance criteria:**
  - For s4 and s24, no sampled point of any cable lies below the floor or ground, in the viewport or in a render (test).
  - A dragged node cannot go below the floor.
  - No empty box appears when a cable is selected.
  - Picker reasons are fully readable.
  - Each "Plugged into" row identifies its fixture uniquely.

#### WP-20 Viewport navigation
- **Sources:**
  - viewport/corner selector lock
  - viewport/several viewports
  - viewport/picking tolerance
- **Scope:**
  - A per-viewport "Lock view" toggle at the view cube (`ViewportPanel.DrawViewCube`, Navigate, ViewportCamera), stored in ViewportLayout.
  - `Workspace.ApplyPreset`: deferred framing of all four views, including the perspective view.
  - `FrameScene` accounts for the toolbar, stats line and cube as margins.
  - Pick tolerance of a few pixels in `renderer.Pick`.
  - New Viewport opens as a visible split, in Colorless.
- **Acceptance criteria:**
  - A locked view ignores orbit and fly; pan and zoom still work. The lock is saved in the layout. Four Viewports locks Top, Front and Right by default (CameraTests and LayoutTests).
  - Applying Four Viewports to an open scene frames all four views, with no content hidden under the toolbar or cube.
  - A fixture about 6 px wide in a zoomed-out ortho view can be picked with one click.

#### WP-21 Library management
- **Sources:**
  - library/extension
  - library/categories (OTHER chip, overrides, cosmetics)
  - library/name tooltip
- **Coordinate with:** WP-10, which adds a tab to LibraryPanel.
- **Scope:**
  - Copy imported GDTFs to a user library folder and register it in `AppSettings.LibraryFolders` (`Workspace.ShowImport(Gdtf)`).
  - `ThumbnailKind.Model` in ThumbnailWorker, with still and orbit images.
  - Validate models with ModelImporter before `ModelLibrary.Add`.
  - Keep the grid order stable.
  - Add an OTHER chip and a "Set category…" override.
  - Show "No fixtures in this category" for empty categories, use plural-aware counts, and add a full-name tooltip on tiles.
  - Optional: managed copies of models.
- **Acceptance criteria:**
  - An imported GDTF is still in the library after a restart.
  - Model tiles show a 3D thumbnail and the hover card.
  - broken.obj is rejected at import with a message.
  - The grid order does not change after placing items.
  - Category overrides persist across restarts.

#### WP-22 Fixture Properties: illustrations and live figures
- **Sources:**
  - fixture-state/illustrations (medium)
  - fixture-state/photometric figures
- **Scope:**
  - `Panels/DeviceFactsView.cs`: header image (the GDTF thumbnail or the Library render), a wheel strip with gobo and animation images and colour swatches (xy to sRGB), a DMX chart of the current mode, and dimensions from the geometry bounds.
  - Swatches and images in the wheel combos (`FixtureInspectorView.ControlRow`).
  - `FixtureControls.ReadLive` shows the on-axis total and the per-cell peak.
  - `Candela.Photometry/Cct.cs`: compute Δuv against the true Planckian locus; mark |Δuv| > 0.05 as not meaningful.
  - CTC at 0 K shows "off".
- **Acceptance criteria:**
  - The Rivale shows its gobo and animation images and its colour swatches.
  - The Zonda at full white shows about 465 000 cd on-axis plus the per-cell value.
  - A saturated red shows CCT "n/a" instead of 1000 K.
  - A DMX chart is present for the current mode.

#### WP-23 Real-time scalability on real rigs
- **Sources:**
  - engine-light/real time on a real rig
  - ROADMAP M7
  - engine-light/occlusion (shared shadow tiles)
- **Phases:**
  - (a) Benchmark on a real GPU: start now, no dependencies.
  - (b) Optimise: after WP-06 and WP-12.
- **Scope:**
  - Record benchmark results in docs/rendering.md.
  - `LightPlanner.cs` and `Engine/Lighting/StageLightCollector.cs`: merge co-located or sub-pixel cells of one fixture into one cone (MagicBlade 36 cells, Zonda 19).
  - One shared shadow tile per fixture in ShadowAtlas.
  - GPU-driven instancing and async compute (M7).
- **Acceptance criteria:**
  - Frame times for `render-test` and Фокина on at least two real GPUs are recorded.
  - On the agreed reference GPU, Фокина at 1080p in Full with volumetrics reaches at least 30 fps (target confirmed after phase a), without BeamQuality dropping automatically.
  - Merged beams differ from unmerged ones by less than an agreed image-difference threshold.
  - Every fixture is shadowed.

#### WP-24 Docking, windows and platform
- **Sources:**
  - docking: Settings tab, corner button, icon tear-off, floating icon groups (medium), Wayland (medium), file drop on torn-off windows, Undock feedback
  - windows/font size
  - windows/Settings docking
- **Scope:**
  - `Windows/SettingsWindow.cs:137`: remove NoDocking, or make Settings a Panel.
  - `Docking/DockWorkspace.cs`: right-align ⋮, close the popup after a style choice, select a panel newly added to an icon group, tear off on dragging a non-selected icon, add a move grip and close button to floating icon columns (or disallow the icon column for floating groups), and put ⋮ at the top of the column.
  - `Shell/GlfwPlatform.cs`: set `GLFW_PLATFORM_X11` when multi-viewport is wanted; on Wayland, turn multi-viewport off and say so.
  - `Shell/FileDropQueue.cs`: a drop callback on every platform window, using global coordinates.
  - Undock action: offset and focus the new window.
  - `Ui/UiScale.cs`: minimum font size equals the default (15).
  - Hide or fill the Theme combo, which has one entry.
  - Update docs/editor.md.
- **Acceptance criteria:**
  - Settings can be docked as a tab.
  - Dragging a non-selected icon tears it off (HeadlessImGui test).
  - A floating icon-column group can be moved, closed and re-docked.
  - A native Wayland GNOME/KDE session either uses XWayland with working torn-off windows or runs single-window with a notice.
  - A file dropped on a torn-off viewport is imported at the drop point.
  - Text cannot be scaled below 100 %.

#### WP-25 More external protocols
- **Source:** windows/protocols (9 of 15 cannot be used).
- **Depends on:** WP-13 (ProtocolManager per-protocol sources).
- **Scope:**
  - CITP (PINF, SDMX, CAEX patch exchange).
  - BlackTrax RTTrPM receiver.
  - ETC Eos OSC patch and parameter sync.
  - Bind PSN and RTTrPM trackers to follow-spot or target positions in Candela.Engine.
  - Keep the honest "SDK" labels for MA-Net, NDI and Pangolin.
- **Acceptance criteria:**
  - Tests against captured packet streams pass for CITP, RTTrPM and Eos.
  - A recorded tracker stream moves a target or follow-spot in the scene.
  - These protocols show as "Implemented" in the registry.

### P3: Low

#### WP-26 Issues quality and electrical reports
- **Sources:**
  - cabling/power networks
  - cabling/issues filter bug
  - fixture-state/duplicate fixture ID
- **Scope:**
  - Wire up `Candela.Electrical/Reports` (PowerReport, CableSchedule, DataReport) through a new Power/Cables panel and `candela validate --power --cables`.
  - ElectricalIssueProvider: group data.no-signal and power.unpowered per universe or per layer.
  - `IssuesPanel.SeverityToggle` (lines 164–175): pop according to `wasOn`.
  - PatchIssueProvider: add `patch.duplicate-fixture-id`.
- **Acceptance criteria:**
  - The panel and the CLI show:
    - total rig load, with uncabled fixtures as "not yet fed"
    - per-phase totals
    - a cable cut list per type
    - the DMX line report
  - Фокина with one cable: the 271 warnings collapse to at most one per universe or layer, and the breaker-overload error is easy to see.
  - Clicking each severity toggle under Xvfb produces no imgui-error (regression test).
  - Fixture ID 101 being used twice in Фокина is reported.

#### WP-27 Project save extras and more export formats
- **Sources:**
  - file-menu/save (camera, thumbnail)
  - file-menu/other export formats
  - engine-light/light plot in editor
- **Depends on:** WP-03 (Export code path).
- **Scope:**
  - Save and restore `View.Camera` (EditorSession and StageEngine SaveAs, `Workspace.FrameNewScene`).
  - Write thumbnail.png.
  - New ExportKinds: glTF/GLB scene, patch-list CSV, light plot.
  - A Light Plot panel over `StageEngine.BuildLightPlot`.
- **Acceptance criteria:**
  - Reopening a project restores the viewport and render camera, and the manifest has `hasThumbnail=true`.
  - The exported GLB passes SharpGLTF validation and includes textures.
  - The patch CSV lists id, name, type, mode, universe.address and power for every fixture.
  - The light plot in the editor matches `candela validate --plot`.

---

## 3. Running packages in parallel

### Order of work
- **Wave 1 (start now; modules do not overlap):**
  - P0 packages: WP-01, 02, 03, 04.
  - P1 packages: WP-05, 07, 08, 10, 11a.
  - WP-06: everything except its analysis.frag step.
  - WP-23a: GPU benchmark.
  - WP-13: Protocols/DmxEngine lane, if there is capacity.
- **Wave 2:**
  - WP-06: analysis.frag step, after WP-04.
  - WP-09, after WP-08.
  - WP-12: its forward.frag and RenderPanel parts, after WP-01 and WP-05.
  - WP-14, after WP-04.
  - WP-16, after WP-02.
  - WP-19, 20, 22, 24, 26.
  - WP-21, after WP-10's Library tab lands.
- **Wave 3:**
  - WP-15, after WP-06.
  - WP-17, after WP-07 and WP-15.
  - WP-18, after WP-05 and WP-07.
  - WP-23b, after WP-06 and WP-12.
  - WP-11b (SKP reader), WP-25, WP-27.

### Files touched by several packages
One owner per file per wave; the other packages rebase after the owner merges.

| File | Owner | Also touched by |
|---|---|---|
| Panels/FixtureInspectorView.cs | WP-22 | 06 (light-shadow toggle), 10 (mount row), 13 (lock text), 16 (flame figures) |
| Shaders/analysis.frag | WP-04 | 06, 14 |
| Shaders/forward.frag | WP-01 | 12 |
| Shaders/beam_scatter.comp | WP-06 | 15 |
| Rendering/LightPlanner.cs | WP-12 | 23 (and 06 for shadow coverage) |
| Engine/Lighting/StageLightCollector.cs | WP-06 | 23 |
| Session/RenderJob.cs, Panels/RenderPanel.cs | WP-05 | 12, 18 |
| Panels/PreviewPanel.cs | WP-01 | 09, 12 |
| Engine/Rendering/RenderSync.cs | WP-06 | 01 (ambient), 19 (BuildCable) |
| Panels/LibraryPanel.cs | WP-10 | 21 |
| Engine/StageEngine.Files.cs | WP-03 | 11, 27 |
| Shell/Workspace.Menus.cs | WP-05 | 19, 27 |
| Simulation/Smoke/SmokeSimulation | WP-07 | 08 (Fill API use), 17 (soot) |
| Dmx FixtureProfile.Builder | WP-02 | 16 |

### Quick wins (each about a day or less)
- WP-01: work-light binding and reference-drawing material.
- WP-11a: `.skp` hint.
- WP-04: sum all lights in the lux pass.
- WP-26: SeverityToggle fix and duplicate fixture-ID warning.
- WP-24: remove Settings NoDocking and clamp the font minimum.
- WP-05: F12 starts the render.
- WP-13: Art-Net port text.
- WP-21: empty-category text.

### Not verified in the audit environment
These need checking inside the packages named:
- Real-GPU performance and RT GI/reflection cost: WP-23a, WP-12.
- Native Wayland sessions, multi-monitor placement and DPI changes: WP-24.
- OS file drop (XDND) onto torn-off windows: WP-24.
- sACN re-subscription: read from the code only (WP-13).
- Real flame GDTFs with safety channels: WP-16.
- SketchUp SDK, which exists only for Windows/macOS: WP-11b.

---

## 4. Requirements confirmed as met
Duplicates are merged. A note in brackets points to a package that covers a small remaining gap.

**Light and rendering**
- Physically based photometric light in real time (I·cookie·shadow·cosα/d², GGX/Lambert). The Photometric, Overlay, Indirect, Volumetric and PathTracerLight tests pass, 42/42.
- Volumetric light in haze (single scattering in real time) [multiple scattering in the editor: WP-05; smoke in the light path: WP-15].
- Visible beams: 1259 volumetric beams on Фокина, thin-beam widening, lens glow and bloom.
- Colour mixing: additive in CIE XYZ, subtractive CMY, CTO/CTB, xyY filters, with the mixed result shown as cd, lm, xy and CCT [MVR gel colour: WP-02].

**Smoke and fire**
- Physical smoke solver: MAC grid, MGPCG, exact mass conservation, plumes and low-fog behaviour [resolution: WP-08].
- Hazers and foggers driven from DMX: all 7 library HAZE/FOG GDTFs, with Atmosphere controls in Properties [CO2 device: WP-08].
- Fire produces smoke and heat (Plume emitters with Tewarson soot yield) [soot absorption: WP-17].
- Flames are rendered in real time and in the path tracer [detail: WP-17].

**Cabling, power and protocols**
- GDTF and _specs power data per fixture; load per outlet and per phase; breaker-overload detection.
- Cable connections between device ports (DMX, Ethernet, power), from GDTF WiringObjects, with compatibility checks, adapters and saving in the project.
- Art-Net input. As the acceptance test, live Art-Net with timecode drove pan, tilt, colour, intensity and zoom, and the Recorder captured the stream [universe re-subscription: WP-13].
- Cable tool:
  - picked by type from Tools → Cable [shortcut shown: WP-19]
  - cursor carries the cable icon
  - continues until right-click or Esc
  - clicking mid-cable creates a branch node
  - nodes move with the 3-axis gizmo, with undo
  - a port picker appears near the cursor [cosmetics: WP-19]
- Properties shows connector drawings with the connected cable.
- Cable-link window as a strip of devices. Clicking a device selects it. Cable indices are shown [fixture ID in rows: WP-19].
- Fixtures cabled to a console are locked and driven by it [input, offline and universe edge cases: WP-13].
- Wrong cabling raises issues (universe mismatch, Y-split, termination, type and phase mismatch) [noise and filter bug: WP-26].

**Interface**
- Corner setting per tab group: tabs on top or a column of icons. It is saved and restored [polish: WP-24].
- Drop indicators while docking, docking into zones, layout presets and saved layouts.
- Torn-off panels open as separate OS windows on X11, can be tabbed together and re-docked, and their positions are saved [Wayland: WP-24].
- Every window except Settings can be docked as a tab [Settings: WP-24].

**Viewport**
- Perspective and orthographic projection; Wireframe shading; Colorless shading.
- Shift+click and box selection; orange outline (#FF7A00).
- Move and rotate gizmos on 3 axes with numeric fields and undo.
- The view cube snaps to orthographic views; several viewports can be open; Four Viewports preset [view lock and framing: WP-20].

**Library and scene building**
- Fixtures sub-tab with all 18 categories in the required order, plus search [empty FAN and FIRE: WP-16; OTHER chip and overrides: WP-21].
- 3D thumbnails with names; hover grows the tile upward and rotates the model.
- Import of OBJ/MTL, glTF/GLB, DAE, FBX, STL and 3DS with textures; textures survive in the project [visible in editor: WP-01].
- Images as posters [visible without stage light: WP-01].
- Walls and floors drawn as resizable rectangles [ceiling tool: WP-10].
- 15 built-in materials with per-slot overrides.

**File menu and project**
- File menu: New, Open, Open Recent, Save, Save As, Import, Export, Render, Settings, Exit.
- `.candela` archive format: round trip of textures, GDTFs and settings; 90/90 project tests [camera and thumbnail: WP-27].
- Save As with overwrite and error dialogs.
- Open `.candela` and `.mvr` with an unsaved-changes check.
- MVR export writes a valid file that reopens [fidelity: WP-03].
- Import (merge) a project or MVR as an undoable group [placement and re-patch: WP-11].
- File → Settings opens the settings.

**Overlays**
- Gradient stops can be added, moved and recoloured.
- The range can be limited, with grey outside it.
- Each preset combo switches from Default to Custom when edited; named presets can be saved and deleted.
- Noise overlay on surfaces (within 0.5 dB of Acoustics.SplAt) and as volumetric fog [fog strength control: WP-14].
- Cable-type legend in the cable overlay.

**Windows and fixture state**
- Properties shows the current state first, then manufacturer data. All attributes have live controls, and the manufacturer data shows its provenance [illustrations: WP-22].
- Key bindings can be configured and saved as named presets.
- Further settings: navigation style, units, grid, network, autosave, GPU, editing.
- Preview panel with real-time settings and presets.
- Issues window with severity toggles, category filter, search and click-to-select.
- The test scene `stages/Фокина.mvr` opens in about 1 s, with its stand-in fixture types clearly reported.