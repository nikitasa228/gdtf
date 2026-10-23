# Handoff — state of the project and how to continue

*Written on 2026-10-23 when development moved from a cloud session to the user's own Windows PC.*

## Where things stand
- Everything is merged on branch `claude/ecstatic-darwin-bal444`; all 14 test projects pass (about 2 900 tests).
- The user installed preview 0.1.0-preview.1 on Windows 10 (AMD Radeon RX 580, ~60 fps in the editor) and reported
  five problems; all are handled in 0.1.0-preview.2 (see "First user test" below).
- Milestones (docs/ROADMAP.md): data core, renderer (real time + path tracer), editor, cabling/power/consoles, external
  protocols (Art-Net, sACN, OSC, PSN, EntTec), smoke/fire simulation, Windows installer are in place. What remains is
  listed as work packages in [AUDIT-PLAN.md](AUDIT-PLAN.md).

## Open work, in suggested order
Work packages from the requirements audit (each has evidence, scope and acceptance criteria in AUDIT-PLAN.md):
1. **WP-06 light occlusion** (P1): MVRs with `CastShadow=false` (all of Фокина) let light pass through floors, walls
   and scenery in both renderers; flames are not occluded; only 64–256 beams get shadow maps.
2. **WP-08 / WP-09 atmosphere** (P1): smoke domain behaviour (venting, dissipation, obstacles from imported models,
   airflow, pre-haze/fill) and editor controls for it. The smoke overlay (Alt+H) helps to study it.
3. **WP-10 rigging** (P1): trusses in the library, mounting fixtures on trusses, truss load ratings and overweight issues.
4. **WP-12 bounce light and reflections** (P1): real-time GI defaults, RSM for non-shadowed lights, specular indirect.
5. **WP-11 import** (P1): SketchUp hint/converter path, placement, re-patch on merge.
6. P2/P3 packages WP-13 … WP-27 (protocol subscription, overlay UX, smoke in the light path, fans/flame units, fire
   appearance, timecode render, cable polish, viewport navigation, library management, properties figures, real-GPU
   scalability, docking/platform, more protocols (CITP, BlackTrax…), issues quality, more export formats).

Known follow-ups from the latest reviews:
- **Verify on the real GPU** that the dark blocky artefacts near bright lenses are gone (fixed blind: half-float
  overflow/NaN sanitised everywhere — LensGlareTests inject inf/NaN). If they persist, capture with RenderDoc.
- The Dear ImGui Vulkan backend waits for the queue on every texture upload; the library uploads up to 6 previews per
  frame → visible stutter while thumbnails stream in. Batch uploads or use a dedicated upload path.
- UI-scale cap uses an estimate (atlas side / 160 px per pixel of text height); revisit on HiDPI screens.
- Path tracer: emissive meshes are not light-sampled (slow LED walls), no light BVH (slow with ~1000 beams), no bloom
  in final renders.
- WP-07: the optional GPU smoke solver is not built (CPU solver runs on worker threads).
- Engine issue texts and some protocol texts are English only (needs a structured, localisable issue text).
- `stages/Фокина.mvr` uses 4 fixture types missing from the library (stand-ins are used). The official GDTFs of
  Ayrton Rivale Profile (GDTF-Share 10439, V1.8), MagicBlade R (5377, V2.73) and Zonda 3 WASH (9750, V1.7) need a
  GDTF-Share login to download; Martin has published none for Atomic 3000 LED. Put them in the repository root.
- `.github/workflows/windows-installer.yml` has never run (pushing workflows was not possible from the cloud).
- The App test suite takes ~3.5 min on lavapipe; one scripted-click test is timing-sensitive under heavy load.

## First user test (preview.1 → preview.2)
1. UI-scale slider froze/crashed → style rescaled from a fresh default, atlas capped by the GPU's texture limit, sliders
   apply on release (`UiScaleRegressionTests`).
2. Crash while hovering library tiles → released ImGui textures were never destroyed and the backend's descriptor pool
   ran out; textures are now counted and freed, failing fixtures show a fallback icon (`UiTextureLifecycleTests`,
   `ThumbnailSweepTests`, `LibraryHoverEndToEndTests`).
3. Dark flickering squares when looking into a light → half-float overflow / NaN in HDR, bloom and temporal histories,
   now sanitised (`LensGlareTests`).
4. Colour mosaic from Rivale Profile with gobo wheel 1 = Open → the animation-wheel insertion bug fixed in WP-02 (not
   in the preview.1 build); guarded by `RivaleOpenGoboTests`.
5. New: smoke/haze density overlay (View → Overlay → Smoke, Alt+H; `candela render --overlay smoke`).

## How the work was run (and can continue)
- Every change goes through: implementation in its own git worktree → an independent adversarial review that tries to
  break it (tests that would fail without the fix) → merge into the work branch → full test run → push.
- Packages that touch different modules run in parallel; files touched by several packages have one owner per wave
  (AUDIT-PLAN.md §3).
- Each agent commits WIP early (usage limits can interrupt work); continuation picks up from the WIP commits.
- On the PC: set `CANDELA_CACHE_DIR` to a temp folder for test runs, keep at most 2–3 build/test agents in parallel
  (each .NET build + GPU test run takes several GB of RAM).
