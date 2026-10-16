# Candela — roadmap

Every requirement from [REQUIREMENTS.ru.md](REQUIREMENTS.ru.md) is listed once, under the milestone that delivers it.
`[x]` done, `[~]` partial (see note), `[ ]` not started.

## M0 — Foundation
- [x] Solution layout, build settings, lint (`dotnet format`), test runner
- [x] Cloud toolchain hook: .NET 8 SDK, lavapipe Vulkan, validation layers, Xvfb
- [x] Architecture, conventions, requirement map (this file)
- [x] Core scene model: entities, transforms, components, selection, undo/redo, issues, presets, settings, key bindings

## M1 — Data core
- [ ] GDTF reader: fixture type, geometry tree, models (3DS/glTF), DMX modes/channels/functions/sets, wheels (colour,
      gobo images, prisms), emitters, filters, physical descriptions, wiring objects
- [ ] Fixture library: scan folders, categories (PROFILE … GENERATOR FIRE), `_specs` physical data, caching,
      MVR stub GDTF → library match
- [ ] MVR import (layers, fixtures, scene objects, trusses, supports, groups, addresses, embedded GDTF/3D) and export;
      `stages/Фокина.mvr` loads with all 91 fixtures resolved
- [ ] Photometry: CIE colour science, CCT, emitter mixing, CMY/gels, beam distribution, cd/lux, light meter
- [ ] DMX: universes, patch, attribute evaluation → physical fixture state, motor dynamics
- [ ] Art-Net receiver (ArtDmx, ArtPoll/PollReply discovery, ArtTimeCode, ArtSync), sACN receiver; DMX input hub

## M2 — Real-time renderer v1 + headless
- [ ] Vulkan device/instance (validation in debug), offscreen + swapchain targets, feature detection (RT, wireframe)
- [ ] Mesh/material/texture upload, bindless materials, built-in PBR material library
- [ ] Physically based spot lights from fixture state: cd profile, colour, zoom, gobo/iris/blade/prism/frost cookie
- [ ] Shadows (atlas), clustered forward shading, HDR, exposure (EV100), tone mapping
- [ ] Volumetric light in haze: froxel in-scattering (HG phase), shadows/cookies in the beam, temporal accumulation
- [ ] View modes Wireframe / Colorless / Full; perspective / orthographic; selection outline (vivid orange)
- [ ] `Candela.Cli render` → PNG on lavapipe; golden-image tests; Фокина.mvr renders

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
- [ ] Cable tool: pick cable type from the tool menu, icon on cursor, click walls/mounts/ports to route, right-click
      or Esc to finish, click on a segment to branch, cable nodes movable with gizmo/drag
- [ ] Port picker popup when a device has several matching inputs/outputs
- [ ] Ports from GDTF wiring objects + specs; connector illustrations in the properties panel with the connected cable
- [ ] Cable link window: strip of all devices on a cable, click → select device
- [ ] Console model: outputs → universes; fixtures cabled to a console are console-driven (locked controls);
      cable indices shown in the patch
- [ ] Power network: sources, links (TRUE1 through), distros, dimmers (own draw vs `maxLoadW`), breaker checks
- [ ] Data network: DMX chains, splitters, Y-split warning, address overlap, chain length
- [ ] Rigging: fixtures mounted on trusses/pipes, truss/point load incl. cable weight, overload warnings
- [ ] Cable overlay with legend

## M5 — Atmosphere, fire, overlays
- [ ] Smoke/haze fluid simulation (GPU, CPU reference): HAZE/FOG emitters from DMX, FAN airflow, obstacles,
      dissipation; feeds volumetric rendering and path tracer
- [ ] Fire simulation for GENERATOR FIRE fixtures: flame jets from DMX, blackbody emission lighting the scene, smoke
- [ ] Lux overlay with gradient editor in the viewport corner (movable colour stops, min/max range, outside → grey,
      presets: Default / Custom / save as named)
- [ ] Noise overlay: SPL on surfaces + coloured volumetric fog, own gradient + presets

## M6 — Final render and indirect light
- [ ] Path tracer (ray query): PBR materials, spot lights with cookies, heterogeneous media, fire emission, progressive
      accumulation, PNG/EXR output
- [ ] Render a frame or a timecode range (DMX recorded from the live input, replayed for each frame) → image sequence
- [ ] Real-time indirect light: reflective shadow maps (all GPUs), ray-query GI/reflections (RT GPUs)
- [ ] Narrow-beam cone ray marching for sharp gobo beams

## M7 — More protocols and polish
- Protocol matrix (external control panel lists all of them; status shows what works):
  - [ ] Art-Net (M1) - [ ] sACN (M1) - [ ] OSC - [ ] PosiStageNet - [ ] EntTec DMX USB Pro - [ ] CITP
  - [ ] ETC Eos (OSC) - [ ] BlackTrax (RTTrPM) - [ ] Kinesys - [ ] PangolinLD - [ ] LasergraphDSP - [ ] NDI (SDK)
  - [ ] Compulite VC - [ ] MA-Net2 / MA-Net3 (proprietary, need MA's SDK — Art-Net/sACN from MA consoles work)
- [ ] Performance: GPU-driven instancing, thousands of fixtures, async compute
- [ ] Windows packaging, crash reporting, autosave/recovery

## Known limitations
- SketchUp `.skp` cannot be read (closed format); export to DAE/FBX/glTF from SketchUp.
- Real-GPU performance and vendor driver behaviour can only be checked on a machine with a GPU; the cloud uses
  lavapipe (correct but slow).
