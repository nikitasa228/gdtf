# Candela — roadmap

Every requirement from [REQUIREMENTS.ru.md](REQUIREMENTS.ru.md) is listed once, under the milestone that delivers it.
`[x]` done, `[~]` partial (see note), `[ ]` not started.

## M0 — Foundation
- [x] Solution layout, build settings, lint (`dotnet format`), test runner
- [x] Cloud toolchain hook: .NET 8 SDK, lavapipe Vulkan, validation layers, Xvfb
- [x] Architecture, conventions, requirement map (this file)
- [x] Core scene model: entities, transforms, components, selection, undo/redo, issues, presets, settings, key bindings;
      change notifications for every scene state, subtree copying with reference remapping (duplicate, paste, merge),
      tolerant forward-compatible `scene.json` (docs/core.md)

## M1 — Data core
- [x] GDTF reader: fixture type, geometry tree, models (3DS/glTF), DMX modes/channels/functions/sets, wheels (colour,
      gobo images, prisms), emitters, filters, physical descriptions, wiring objects — all 1091 files parse, channel
      counts match `_specs` for 7272 modes, all 4982 models load (docs/gdtf.md)
- [x] Fixture library: scan folders, categories (PROFILE … GENERATOR FIRE), `_specs` physical data, caching,
      MVR stub GDTF → library match, binary disk cache keyed by content + reader version (8–9× faster re-open;
      hostile-input limits; reviewed). 4 of the 5 types in `stages/Фокина.mvr`
      (Ayrton Rivale Profile, MagicBlade R, Zonda 3 Wash, Martin Atomic 3000 LED) are not in the library, so
      same-brand substitutes are used until those GDTF files are added. The Ayrton GDTFs are published only on
      GDTF-Share (download needs a login or CAPTCHA, so they must be added by hand); Martin has published none for
      the Atomic 3000 LED. Sources, revisions and expected footprints: docs/mvr.md
- [ ] Add the official GDTFs of Ayrton Rivale Profile, MagicBlade R and Zonda 3 Wash (GDTF-Share fixtures 10439,
      5377, 9750) and, once Martin publishes it, the Atomic 3000 LED; regenerate `_specs`; update the substitute
      expectations in the Mvr and Gdtf tests (Engine tests and the drop-in check need no change)
- [x] MVR import (layers, fixtures, scene objects, trusses, supports, groups, addresses, embedded GDTF/3D) and export;
      `stages/Фокина.mvr` loads with all 91 fixtures (5 GDTF specs; 4 resolved to same-brand substitutes, reported as
      issues, until the real files are added; each stand-in takes its mode that fits the patch spacing, so the patch
      has no errors; with the real footprints the patch is clean too, checked with stand-ins of the real names and
      modes) — docs/mvr.md
- [x] Photometry: CIE colour science, CCT, emitter mixing, CMY/gels, beam distribution, cd/lux, light meter,
      acoustics, blackbody (docs/photometry.md)
- [x] DMX: universes, patch, attribute evaluation → physical fixture state, motor dynamics, encoder, DmxEngine
      (2000 fixtures ≈ 2 ms/frame, zero allocations; 387 GDTF attributes handled) — docs/dmx.md
- [x] Art-Net 4 receiver (ArtDmx, ArtPoll/PollReply discovery, ArtTimeCode, ArtSync), sACN receiver (priority,
      sync, discovery, termination), DMX input hub with HTP/LTP merge, built-in test console (docs/protocols.md); an
      Art-Net packet moves a fixture end to end through the engine (docs/engine.md)

## M2 — Real-time renderer v1 + headless
- [x] Vulkan device/instance (validation in debug), offscreen + swapchain targets, feature detection (RT, wireframe)
- [x] Mesh/material/texture upload, bindless materials, built-in PBR material library (Candela.Assets)
- [x] Physically based spot lights: cd profile, colour, zoom, gobo/iris/blade/prism/frost cookie (renderer side;
      fixture state → lights is Candela.Engine RenderSync)
- [x] Shadows (atlas), clustered forward shading, HDR, exposure (EV100 + auto), bloom, tone mapping
- [x] Volumetric light in haze: froxel in-scattering (HG phase), shadows/cookies in the beam, temporal accumulation,
      smoke density volume, flame emission
- [x] View modes Wireframe / Colorless / Full; perspective / orthographic; selection outline (vivid orange)
- [x] `candela render-test` → PNG on lavapipe with golden-image tests (96 renderer tests); `candela render` of
      Фокина.mvr through Candela.Engine (scene → DMX → lights → render world, docs/engine.md)

## M3 — Editor shell
- [ ] GLFW + ImGui docking: every panel is a dockable tab; drop-zone indicators; tab style per panel group
      (icon + name on top, or icon-only column on the left, switch in the corner); torn-off panels move to other
      monitors (multi-viewport); layouts saved
- [ ] Viewport panel: orbit/pan/zoom, perspective/ortho, corner view cube (top/front/right…), several viewports
- [ ] Selection (click, Shift to add, box), orange outline, move/rotate gizmos on 3 axes, numeric entry, undo/redo
- [ ] Library panel: Fixtures → category sub-tabs; 3D thumbnail per fixture with name, hover → grows upward with a
      rotating 3D preview; custom models tab; drag into the scene
- [ ] Import custom 3D models with textures (OBJ/FBX/DAE/3DS/glTF/…), images as reference drawings or posters
- [ ] Walls and floors as resizable rectangles; built-in materials applicable to any surface
- [ ] Properties panel: live state first (pan/tilt/colour/intensity/zoom…, locked when console-driven), then
      manufacturer data from GDTF + specs (manufacturer, weight, power, cd, connectors with illustrations…)
- [ ] File menu: Save / Save as (`.candela` archive) / Open / Render / Export (MVR) / Import (project, 3D model, MVR,
      image) / Settings
- [ ] Settings window: UI/font scale (100 %+), key bindings with named presets, library folders, network adapter,
      units, theme, autosave
- [ ] Preview (real-time quality) and Render (final quality) settings panels with presets; "Render" button
- [ ] Issues panel (errors / warnings / notes, click → select related objects)
- [ ] External control panel: protocol selection (all protocols listed below), status, universes, timecode

## M4 — Cabling, power, consoles
- [x] Devices section of the Library: consoles (DMX 2/4/8 outputs, Art-Net / sACN network consoles), nodes, splitters,
      mains supplies (Schuko 16 A, CEE 16/32 A, CEE 32/63/125 A three phase, Powerlock), distros, dimmers and the power
      devices of the fixture library; dragged into the scene with their ports (docs/editor.md#devices-in-the-library)
- [x] Cable tool: pick cable type from the tool menu, icon on cursor, click walls/mounts/ports to route, right-click
      or Esc to finish, click on a segment to branch, cable nodes movable with gizmo/drag, length and weight shown
      (docs/editor.md#the-cable-tool)
- [x] Port picker popup when a device has several matching inputs/outputs (connector drawings, what is plugged in,
      why a port does not fit)
- [x] Ports from GDTF wiring objects + specs; connector illustrations in the properties panel with the connected cable
- [x] Cable link window: strip of all devices on a cable in signal order with cable indices, click → select device
- [x] Console model: outputs → universes; fixtures cabled to a console are console-driven (locked controls, following
      the cables); cable indices shown in the patch ("DMX A · cable 3"), universe mismatch in the Issues panel
- [x] Power network: sources, links (TRUE1 through), distros, dimmers (own draw vs `maxLoadW`), breaker checks; power
      issues after cabling in the Issues panel, outlet loads in the Properties panel
- [x] Data network: DMX chains, splitters, Y-split warning, address overlap, chain length (Candela.Electrical)
- [x] Rigging: fixtures mounted on trusses/pipes, truss/point load incl. cable weight, overload warnings
      (Candela.Electrical RiggingAnalyzer) — docs/electrical.md
- [x] Cable overlay with legend (cables drawn as tubes + coloured lines, connected devices highlighted; legend with
      the types in the scene, count, length and weight)
- [x] Duplicated fixtures re-patched to free addresses (setting: ask / next free / keep); Escape cancels wall and
      floor drags
- [x] Cables lying on floors drawn without sag: per-span slack (straight along an object or on a surface) and the
      ground/floors as supports a tube never sinks below (`CableSupportSurfaces`, docs/engine.md)
- [x] Engine: `StageEngine.NetworkSolution` renewed when entities that cable points are fixed to move (only the moved
      cables, `NetworkSolver.UpdateGeometry`); the editor's `CablingNetwork` workaround can switch to it
- [x] `PowerSourceComponent.MainsInConnector`: the distro inlet has a real connector (derived for older projects);
      generic devices marked in Candela's MVR `UserData`; `SpotLight.NoVolumetricScattering` for the work light
      (the Vulkan renderer honouring it is pending)

## M5 — Atmosphere, fire, overlays
- [~] Smoke/haze fluid simulation (GPU, CPU reference): HAZE/FOG emitters from DMX, FAN airflow, obstacles,
      dissipation; feeds volumetric rendering and path tracer (CPU solver driven by the DMX device outputs in the engine,
      density grid rendered; GPU solver pending)
- [~] Fire simulation for GENERATOR FIRE fixtures: flame jets from DMX, blackbody emission lighting the scene, smoke
      (CPU simulation driven by DMX in the engine, flames light the scene and are rendered; no GENERATOR FIRE GDTF in the
      library yet)
- [~] Lux overlay with gradient editor in the viewport corner (movable colour stops, min/max range, outside → grey,
      presets: Default / Custom / save as named) (GPU overlay and CPU light meter done; gradient editor in the editor)
- [~] Noise overlay: SPL on surfaces + coloured volumetric fog, own gradient + presets (sources from `_specs` noise ×
      device activity, GPU overlay done; gradient editor in the editor)

## M6 — Final render and indirect light
- [ ] Path tracer (ray query): PBR materials, spot lights with cookies, heterogeneous media, fire emission, progressive
      accumulation, PNG/EXR output
- [~] Render a frame or a timecode range (DMX recorded from the live input, replayed for each frame) → image sequence
      (DMX recorder/player with timecode seek, `candela render --look dmx-file --time`; path-traced sequences pending)
- [x] Real-time indirect light: reflective shadow maps (all GPUs), ray-query GI (RT GPUs) — docs/rendering.md
- [x] Narrow-beam ray marching for sharp gobo beams

## M7 — More protocols and polish
- Protocol matrix (external control panel lists all of them; status shows what works):
  - [x] Art-Net (M1) - [x] sACN (M1) - [x] OSC - [x] PosiStageNet - [x] EntTec DMX USB Pro - [ ] CITP
  - [~] ETC Eos (OSC: cues, show, command line; levels via Art-Net/sACN) - [ ] BlackTrax (RTTrPM) - [ ] Kinesys - [ ] PangolinLD - [ ] LasergraphDSP - [ ] NDI (SDK)
  - [ ] Compulite VC - [ ] MA-Net2 / MA-Net3 (proprietary, need MA's SDK — Art-Net/sACN from MA consoles work)
- [ ] Performance: GPU-driven instancing, thousands of fixtures, async compute
- [~] Windows packaging, crash reporting, autosave/recovery (autosave and recovery done: Candela.Project + engine)

## Known limitations
- SketchUp `.skp` cannot be read (closed format); export to DAE/FBX/glTF from SketchUp.
- Real-GPU performance and vendor driver behaviour can only be checked on a machine with a GPU; the cloud uses
  lavapipe (correct but slow).
