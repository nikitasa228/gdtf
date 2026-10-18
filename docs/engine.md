# Engine (`Candela.Engine`) and the command-line tool (`candela`)

`Candela.Engine` is the running world: one open project with everything that brings it to life — the fixture library,
assets, protocol input, DMX evaluation with motor dynamics, smoke/fire/sound simulation, the cable, power and rigging
solvers, issues, undo and selection — and the binding that keeps a renderer in sync with it. The editor and the
command-line tool are thin layers over it, so everything works (and is tested) without a window. It references every
module except the App (see [ARCHITECTURE.md](ARCHITECTURE.md)); conventions are the project's: SI units, right-handed Z-up
metres, linear Rec.2020 light colour with luminance 1 plus a photometric magnitude.

```
                 ┌──────────────── StageEngine (main thread) ─────────────────────────────────────────┐
 Art-Net, sACN … │ ProtocolManager ─▶ DmxInputHub ◀─ DmxPlayer (recording)      DmxRecorder ◀─ hub      │
 (own threads)   │                        │                                                             │
                 │ Tick(dt): NetworkSolution (console-driven?) ─▶ DmxEngine.Update ─▶ FixturePoses       │
                 │   ─▶ StageSimulation.Step (hazers, fog, fans → smoke; flames → fire; noise sources)   │
                 │   ─▶ StageLightCollector (one SpotLight per lit beam, gobos/iris/blades/prism/frost)  │
                 │   ─▶ RenderSync.Update ─▶ IRenderWorld (instances, lights, lines, atmosphere, noise)  │
                 │ Project (ProjectData: scene, AssetStore, settings sections, GDTF copies)             │
                 │ FixtureLibrary + _specs · EngineModels (import worker) · IssueLog · UndoStack · Selection │
                 └────────────────────────────────────────────────────────────────────────────────────┘
```

## StageEngine

```csharp
using var engine = StageEngine.FromSettings(AppSettings.Load());   // library = settings, else the folder of Candela.sln
var opened = engine.Open("stages/Фокина.mvr");                     // or a .candela project
engine.StartProtocols(new[] { ProtocolIds.ArtNet, ProtocolIds.Sacn });
var sync = engine.AttachRenderWorld(renderer.World);           // (world, loadFixtureModelsInBackground: false) for CLI/tests
while (running)
{
    engine.Tick(frameSeconds);                                     // DMX → poses → simulation → lights → render world
    renderer.Render(target, camera, view, preview, frameSeconds);
}
```

| Member | What it is |
|---|---|
| `Scene`, `Project`, `Assets` | The open project (`Candela.Project.ProjectData`): scene, asset store, settings sections, GDTF copies, thumbnail. Replaced by `New`/`Open` (`SceneReplaced` event). |
| `Library`, `Specs`, `Profiles` | Fixture library (library folders + GDTFs brought in by projects and MVR files), `_specs/fixtures_index.json`, compiled DMX profiles. |
| `Input`, `Protocols`, `StartProtocols` | DMX input hub and the protocol manager; `StartProtocols` subscribes to the universes the patch uses. |
| `Dmx`, `Poses` | DMX evaluation of every fixture; world matrices of every fixture part. |
| `Simulation` | Smoke, fire and the acoustic field (`StageSimulation`). |
| `Network`, `Rigging`, `ElectricalProfiles`, `NetworkSolution`, `HasConsoles` | Cable/power/data solver, rigging analyzer, electrical fixture data. |
| `Issues`, `RefreshIssues()` | Issue log with the patch, electrical, rigging and engine providers, plus the issues of the last operations. |
| `Undo`, `Selection` | Undo history and selection of the open scene. |
| `Settings` (`EngineSettings`), `View` (`ViewSettings`) | Stored with the project in the `engine` and `view` settings sections. |
| `Models`, `Images` | Decoded models (native formats through the import worker) and the image source for every texture key. |
| `LightMeter`, `Recorder`, `Play`, `Player` | Illuminance and light plot; DMX recording and playback. |
| `Lights`, `LightCollector`, `Time`, `Frame` | The frame's photometric lights and clock. |
| `Render`, `AttachRenderWorld`, `DetachRenderWorld` | The renderer binding. |
| `Autosave`, `EnableAutosave`, `DisableAutosave`, `Recover` | Autosave of the open project (`AutosaveService`, driven by `Tick`; a project is autosaved only when its scene, an asset (import options changed, removed) or the `engine`/`view` settings changed; a successful save discards the session's autosaves) and recovery of crashed sessions. `Recover(session)` continues the crashed session's files (`AutosaveOptions.Resume`), so the next successful save removes them — also when autosave is off — and a recovered project is never offered again after it was saved; until then the crash data stays recoverable. Opening another project closes the session normally; on a clean exit the application calls `DisableAutosave()` before disposing the engine (disposing alone keeps the recovery files). |

**Construction.** `new StageEngine(StageEngineOptions)`: `LibraryFolders` (empty → the repository during development:
the folder containing `Candela.sln`, searched upwards from the program and the current directory), `SpecsIndexPath`
(default: `_specs/fixtures_index.json` next to a library folder), a shared `Library` (tests, several engines),
`CacheDirectory` (default `ConfigPaths.CacheDir`), the import worker (below), and thread counts. A missing library or
unreadable specs file becomes an issue (`engine.library-missing`), never an exception.

### Operations

Every operation returns an `EngineResult` (`Success`, a one-line `Summary` for the status bar or the CLI, `Issues` most
severe first, the `Entities` it created) and reports the issues to `Issues`. Content problems never throw; `Success` is
false only when nothing happened (the open scene is then unchanged).

| Operation | Behaviour |
|---|---|
| `New(name)` | Empty project; undo, selection, settings and assets start afresh. |
| `Open(path)` | `.candela` through `Candela.Project` (tolerant loading; load issues reported; the `engine`/`view` sections restored, a section that no longer parses gives `engine.settings-invalid` and the defaults); `.mvr` through `Candela.Mvr` as a new, unsaved project. |
| `Save()`, `SaveAs(path)` | Atomic `.candela` write with the scene, every asset (original bytes), copies of the GDTF files the fixtures use (from the library or the project's own copies), the thumbnail (`Project.Thumbnail`) and the `engine`/`view` sections. `Save()` without a path fails with "Save as". |
| `Import(path, EngineImportOptions)` | `.mvr` merged under a new group (undoable); `.candela` merged under a group through `ProjectMerge` (new ids, references remapped, undoable); 3D models (OBJ, FBX, DAE, 3DS, STL, PLY, glTF/GLB) as a `MeshComponent` entity at `Placement`; images (PNG, JPEG, TGA, BMP, PSD, GIF, HDR) as an `ImageComponent` reference drawing or poster sized to the image's aspect; `.gdtf` added to the fixture library. SketchUp files are refused with advice. |
| `ExportMvr(path)` | MVR 1.6 with the library's GDTF files and the models as GLB (`Candela.Mvr`). |

**GDTF files that are not in the library.** Complete GDTFs embedded in an MVR, and the GDTF copies of a project, are
extracted to `{cache}/gdtf/{content hash}/` and added to the library when the library has no file of that name, so a
project opens and evaluates on a computer without those files (tested with an empty library). Library files win over
project copies of the same name. Whenever GDTF files are added to the library (also `Import` of a `.gdtf`), every
fixture is resolved again (`DmxEngine.ReloadProfiles`): a fixture that was unresolved, or resolved to a substitute,
picks up the new file, fixtures an MVR import drew with a stand-in switch to the real GDTF of their model (below), and
every fixture keeps its motor positions — a moving head does not travel to its position again, and fixtures whose
profile did not change keep their state. Changing an asset's import options marks the project modified.

**Stand-in GDTFs (substitutes).** When the library has no file of a model an MVR asks for, the MVR importer draws it
with a similar model of the same manufacturer (`mvr.gdtf-substitute`; Фокина's MagicBlade R as an Ayrton MagicPanel
602). The importer keeps the mode of the requested name, which belongs to the missing model and may be far wider (the
MagicBlade's 44-channel "Extended" became the panel's 160-channel "Extended": 73 overlap errors on Фокина). After the
import the engine gives each stand-in type the mode that best fits its patch spacing (`SubstituteModes.Choose`: the
requested mode when it fits, else the largest that fits, else the smallest; note `engine.substitute-mode`) and records
every stand-in in `EngineSettings.Substitutes` (requested GDTF and mode, stand-in file, spacing; saved with the
project). The patch judges stand-ins by that record (`PatchIssueProvider.Substitutions`): one `patch.substitute-gdtf`
warning per type with all its fixtures, and overlaps or slots past 512 that only the stand-in's larger footprint causes
are one warning per type, not errors. When the real GDTF arrives (library reload, `.gdtf` import), the stand-ins switch
to it in the requested mode (else the mode that fits the spacing) and leave the record (`engine.substitute-replaced`).
`SubstitutionOf(entityId)` says what a fixture stands in for.

**MVR models** become model assets with the MVR conventions as import options (3DS millimetres Z-up, glTF metres Y-up);
the MVR module's managed decode is reused (and cached), so opening an MVR never needs the native importer.

### Import worker (native-crash isolation)

Assimp is native code: a crafted OBJ, FBX, COLLADA or 3DS file could crash the process despite the checks in
`Candela.Assets`. Models in those formats are therefore imported by **`candela import-worker <input> <output-dir>
[--up-axis auto|y|z] [--unit-scale m] [--no-guess-units] [--smoothing deg] [--max-parts n]`** in a separate process:

1. The engine starts the worker (`ImportWorker.Run`) with the persistent import options and a timeout
   (`StageEngineOptions.ImportWorkerTimeout`, default 2 minutes), draining its output pipes.
2. The worker imports with `ModelImporter` and writes Candela's own `ModelPackage` (`model.cmodel`: meshes, part
   transforms, materials, the files that were read, embedded and generated images, warnings), or `error.txt` and exit
   code 4 when the file is not importable (a user-facing reason, reported as `engine.import-failed`).
3. A crash (any other exit, a signal), a timeout (the process tree is killed) or an unreadable package is reported as
   **`engine.import-unsafe`: "'file' could not be imported safely (…)"**; nothing is added to the project. The timeout
   bounds the whole import: after the worker ended, its output is read for at most 2 s
   (`ImportWorker.OutputDrainTimeout`), since a helper process it left behind can hold the pipes open.
4. The package is checked defensively when read (every count and length against the remaining bytes), the asset is
   stored with its original bytes and companions, and the decoded model is cached in memory and on disk
   (`{cache}/models/{content hash}_{options}.cmodel`), so a model passes the native importer at most once per machine.
   Models of opened projects go the same way (their files are written to a private temporary folder for the worker).

The worker program is `StageEngineOptions.ImportWorkerPath` when set (a path that does not exist means "no worker"),
else `CANDELA_IMPORT_WORKER`, else `candela` / `candela.exe` / `candela.dll` next to the running program (a `.dll` is
started through the `dotnet` host running this process). **Without a worker** the import falls back to the
application process and a note says so (`engine.import-in-process`). glTF/GLB (SharpGLTF), STL and PLY (Candela's own
readers) are managed code and always stay in-process. `IsolateNativeImports = false` turns isolation off for trusted
files. Textures of worker-decoded models (`asset:{id}/…`) are served by `EngineImageSource` from the decoded package,
so embedded and generated images never require the native importer again.

## Frame (`Tick`)

`Tick(deltaTime)` (seconds; ≤ 0 re-evaluates without advancing time):

1. **Input.** A playing recording submits its universes to the hub (`DmxPlayer`); a running recording captures the
   merged input (`DmxRecorder`); autosave checks its interval.
2. **Console-driven status** from the data network: with a console in the scene, a fixture follows DMX only when a
   data path connects it to a console output (`NetworkSolution.IsConsoleDriven`) — its manual controls are locked;
   without consoles, a fixture follows DMX when every universe it is patched in is live, otherwise it uses its manual
   attributes. The network is solved again only after structural edits (cables, consoles, fixtures, power sources,
   entities added/removed/reparented), not when something moves.
3. **DMX** (`DmxEngine.Update`, motor dynamics per `EngineSettings.MotorDynamics`) and **poses** (`FixturePoses`: world
   matrices of every fixture part from the axis rotations, recomputed for changed states and moved entities).
4. **Simulation** (`StageSimulation`): HAZE/FOG outputs → hazer and fog-machine emitters (fog machines use the `_specs`
   power as heater power; "low/ground fog" devices emit chilled fog), FAN outputs → fan jets, GENERATOR FIRE flame
   outputs → the fire simulation (whose soot and heat feed the smoke), and every device with a `_specs` `noiseLevelDb`
   → a sound source of `noiseLevelDb + 10·log₁₀(activity)` (lights: idle share + (1 − share) × brightest beam;
   atmospheric devices: their largest output, silent when off). The smoke domain is the first enabled
   `AtmosphereVolumeComponent`, or — with `AutomaticAtmosphere` — a box around the scene created when a device first
   emits; walls are obstacles. Fixed sub-steps; `SimulationPaused` freezes smoke, fire and sound.
5. **Lights** (`StageLightCollector`): one `SpotLight` per beam with intensity > 0 — position, direction and gobo `Up`
   from the beam part's world matrix (GDTF beams leave along local −Z), peak candela, colour (`LightColor.Rgb`), beam
   and field angle, beam type, source radius (`BeamRadius`), gobo texture of the current wheel slot (a wheel between
   two slots projects the nearer one, blurred), gobo rotation and blur (focus distance from the middle of the range,
   frost), iris, framing blades (insertion, tilt from A/B, module rotation), prism (facets and spread from the wheel
   slot's facet offsets), frost, `CastShadow`; plus two hemispherical lights per flame light sample. The field angle
   comes from the DMX profile, which already reads beam/field angles that are a zoom range or an implausible ratio as
   the type's typical ratio (docs/dmx.md); the collector only keeps it within the renderer's 170° cone as a sanity
   clamp, lists the fixtures it had to clamp (`ClampedFieldFixtures`) and reports them (`engine.field-clamped`).
6. **Render sync** (below).

The result depends only on the scene, the DMX received and the sequence of frame times (tested: two engines, same
input sequence, identical lights and smoke; evaluation and simulation threads do not change results). "The scene"
includes its entity ids: they seed each fixture's random effects (random strobe, random wheel positions), so the same
show rebuilt with new ids flickers differently. Steady-state frames — fixtures moving,
nothing edited — allocate nothing on the main thread (tested on `Фокина.mvr` with every motor moving).

## RenderSync

`engine.AttachRenderWorld(world, loadFixtureModelsInBackground = true)` returns a `RenderSync` that keeps an
`IRenderWorld` in sync, incrementally from `Scene.Changed` plus per-frame dynamic data; `DetachRenderWorld()` destroys
everything it created. The incremental result equals a binding built from scratch after any sequence of edits, undo
and redo (tested). What a change replaces — a model whose import options changed, a fixture type recompiled after a
library change, its gobo textures — is destroyed after every entity drawing it has been rebuilt and the new light list
was sent, so the renderer never holds an instance or light that refers to a destroyed resource. A gobo of a fixture type
seen for the first time is resolved from the next frame (the light list of a frame is collected before the binding's
update).

| Content | How it is drawn |
|---|---|
| Fixtures | The models of each fixture type + mode are loaded once (`FixtureGeometryBuilder` + `GdtfModelLoader`, textures through `gdtf:` keys) — on a background task by default (`LoadFixtureModelsInBackground`; `CompletePendingLoads` waits) — and uploaded once; every fixture gets one instance per model part of every geometry part, whose world matrix follows the part's pose. |
| Lights | `SetLights` every frame; gobo textures are created once per wheel slot from the GDTF media (alpha = transmission = linear luminance × alpha of the picture, RGB = tint of coloured glass). |
| Walls, floors, ceilings | `WallMesh` + the built-in material (`MaterialLibrary`) with its UV scale. |
| Images | `ImagePlaneMesh` + `ImagePlaneMesh.CreateMaterial`; reference drawings unlit (`InstanceFlags.Reference`), posters lit. |
| Imported models | Decoded through `EngineModels` — on a background task by default (`LoadModelsInBackground`; a native format goes through the import worker, which may take up to its timeout; the objects appear when their model has decoded, `PendingModels` counts the decodes, `CompletePendingLoads` waits for them too); material overrides by slot with built-in materials. Decoded models live in the project's `AssetStore` (`SetDecodedModel`), so the store, the renderer and the light meter share one copy. |
| Trusses | Their model, or `TrussMesh` in aluminium. |
| Cables | Tube meshes (`CableTubeMesh`, catenary sag from the slack factor, dark rubber tinted by the type) plus coloured lines (`SetLines`, the cable type's colour) for the cable overlay; rebuilt when an anchor entity moves. |
| Consoles, power sources, rigging points | Simple primitives (desk, distro box, hoist). |
| Atmosphere | `SceneSettings.BaseHazeScattering` (or `HazeOverride`) and anisotropy, the smoke density grid when the smoke simulation stepped, the flames' temperature grids composited into one grid; noise sources every frame. |

**Flags.** `Selected` (selection), `Hidden` (entity or an ancestor hidden, or its layer hidden; a hidden fixture still
emits its light — hiding the rig to look at the stage keeps the stage lit; its dimmer switches it off), `CableHighlight`
(cables and the devices they plug into), `CastShadow` (`FixtureComponent`/`MeshComponent.CastShadow`; walls, trusses
and posters cast, reference drawings and cables do not), `Reference`. Every drawn entity has a pick id:
`TryGetEntity(pickId, out entityId)`, `PickIdOf(entityId)`; `InstancesOf(entityId)` lists what was drawn (part names,
world matrices, flags) for tools and tests.

## Light meter and light plot

`engine.LightMeter` (recomputed lazily after each frame) sums `E = I(θ)·cos α / d²` over the frame's lights with
`Candela.Photometry.LightMeter` (the CPU reference the GPU lux overlay is tested against): `IlluminanceAt(point,
normal)`, `HorizontalIlluminanceAt`, `IlluminanceRgbAt`, `Breakdown` (lux per fixture, brightest first, for tooltips),
`IlluminanceOnGrid`, and `IncidentEv100(area, height)` — the exposure an incident-light meter would suggest for the lit
part of a floor (median of a 32 × 32 grid, `EV100 = log₂(E·100/250)`). Gobos, iris, blades, prisms and shadows are not
applied (open beams, free field).

`BuildLightPlot()` gives the fixture schedule: per fixture the fixture id, name, type (manufacturer + model of the GDTF
in use, substitutes marked), mode, category, address per DMX break, channels, power (W) and weight (kg) from the
electrical profile (`_specs` / GDTF), position, layer and the current peak candela; totals; `ToCsv()` and `ToText()`.

## DMX recording and playback

`engine.Recorder.Start(engine.Time, name)` … `Stop()` records the merged hub input once per frame: for every received
universe the slots that changed (runs; gaps of up to four equal slots bridged), universes that appear (full) or stop
(lost), with the frame time and the last timecode received (ArtTimeCode …). `DmxRecording.Save/Load` (`.cdmx`): magic,
version, creation time, name, then a zlib stream of frames (varint-encoded runs). A file cut short loads the frames
before the damage with `engine.recording-damaged`; anything that is not a recording throws `InvalidDataException`.
Recordings keep a full state every 2 s for seeking.

`engine.Play(recording, startSeconds)` plays it into the hub as the source `recording` following the engine clock;
`DmxPlayer.Seek(time)`, `SeekTimecode(timecode)` (to the moment that timecode was received, interpolated between
captures) rebuild the state from the nearest keyframe, so rendering a timecode range means: seek, then tick at the
frame rate. Universes are re-sent every frame so they never time out in the hub.

## Fixture thumbnails

`FixtureThumbnailRenderer(renderer, library, thumbnailStore)` renders fixture types for the library panel with a
renderer of its own (e.g. a second `VulkanRenderer` on the editor's context): the model turned upright (GDTF models
hang), a neutral three-point studio light (5600 K key, fill and rim spots), a three-quarter front view framing the
model's box, dark background, fixed exposure. `Render(entry, size, mode)` caches square images in Candela.Assets'
`ThumbnailStore` keyed by the GDTF file's SHA-256 (variant `fixture`); `RenderOrbitFrame(entry, width, height,
azimuth)` renders the frames of the rotating hover preview (the fixture stays loaded between frames).

## Looks and the test show

`StageLooks.ApplyOpenWhite(engine, options)` sets every fixture's manual values to "open white": GDTF highlight values,
dimmer at full, shutter open (the channel set flagged open), additive emitters and indirect RGB at full, zoom at the
middle of its range, and moving heads aimed towards the stage centre (`Spread` 0.75: each head points mostly down,
leaning towards the target, so the pools spread over the stage). Pan/tilt are found by a grid search over the
functions' physical ranges refined by pattern search on the fixture's real geometry (aim error < 1.5° in the tests).
The values are checked to reproduce the encoded intensity when compiled back; otherwise every channel's value is
carried. One undo step.

`StageLooks.ApplyDemo(engine, options)` sets a deterministic, show-like state for judging the picture (`candela render
--look demo`). The rig is split into groups — fixtures of one type at one height in one line: a row across the stage,
or the pair of columns at the same distance left and right of the centre line (a fixture is in its row when the row
has three or more fixtures of its type and height, or at least as many as its column pair) — taken from the top of the
rig down. Each group takes the next colour of `DemoPalette` (white D65, deep blue, magenta, amber, cyan, red; made
with whatever the fixture has, `DmxLooks.SetColor`) and a pattern, symmetric about the stage's centre line:

| Group | Pattern |
|---|---|
| Moving heads less than 3.5 m above the floor (narrowest zoom) | downstage rows fan up and out over the audience (−Y), or cross over it (every other such group); side column pairs cross over the stage, or fan up (every other pair); other rows fan up |
| Overhead washes (wide zoom) | pools on the floor in the group's colour and the one after next, alternating every 1.5 m outwards from the centre line |
| Overhead spots and beams (narrowest zoom) | cross onto the floor of the other side, or fan across the downstage edge (alternating by group) |
| Every other overhead group with a gobo wheel | a gobo |
| Fixed lights, strobes, atmospheric devices | their group's colour at full; strobes dark (a still image of a strobe is a lottery); hazers at `HazeOutput` (0.6) with their fans at half, fog off |

Dimmers are at full and shutters open (`DmxLooks.OpenWhite`), heads are aimed like in open white (grid and pattern
search on the fixture's geometry). One undo step; the same scene always gets the same look (tested: mirror partners
share colour and mirror their beams on Фокина).

`GdtfTestPatternEncoder.ForScene(engine)` builds the Art-Net test show (`ArtNetTestPattern`) over the scene's patched
fixtures with their own GDTF channel functions (pan/tilt/zoom/focus over their ranges, emitters by colour code, CMY as
complements, shutter open).

## Command-line tool

`candela` (`src/Candela.Cli`). Exit codes: **0** success, **1** failure (file not readable, render or I/O error),
**2** usage error, **3** no usable Vulkan device, **4** the input has errors (`validate` found error-level issues;
`import-worker` rejected the model).

```
candela render <scene.mvr|scene.candela> --out <file.png> [--width W --height H]
               [--camera x,y,z --target x,y,z | --view front|top|side|auto] [--fov deg]
               [--mode full|colorless|wireframe] [--overlay none|lux|noise|cables] [--haze <1/m>]
               [--look open-white|demo|show|dmx-file <recording.cdmx>] [--time <s>] [--ev <EV100>|auto]
               [--auto-exposure] [--frames N] [--grid] [--floor auto|none] [--device <name>]
               [--library <dir>]... [--cache <dir>]
candela artnet-test --to <ip[:port]> [--universes 1-4] [--seconds N] [--fps F] [--offset 0|1] [--scene <file>]
candela info <file.gdtf|file.mvr|file.candela>
candela validate <scene> [--plot]
candela import-worker <input> <output-dir> [options]     (hidden: used by the engine)
candela render-test --out <dir> [options]                (renderer self-test, see docs/rendering.md)
```

**render** loads the scene through `StageEngine`, applies the look (`open-white` default; `demo` = the show-like
state of `StageLooks.ApplyDemo`; `show` = as saved; `dmx-file` plays a recording from its start), ticks the simulation for `--time` seconds at 30 frames per second
(motors travel, haze builds up, flames burn), renders `--frames` frames (temporal accumulation) and writes the PNG,
printing the scene, camera, exposure, timings and the most severe issues. `--view auto` frames the scene from the front
left, elevated; `front`, `top`, `side` look along the axes. The preset views frame the content itself rather than its
bounding box (`SceneBounds.CollectContentCorners` — the corners of every object's box — plus
`SceneBounds.CollectFixtureParts` — every fixture part as it hangs and moves — fitted by `ViewFraming.Frame(points, …)`:
each side of the picture touches its outermost point with a 10 % margin and the content is centred), so the stage fills
the picture. `--out` must name a file (a folder is a usage error). `--overlay lux` keeps the default gradient
(1 … 5000 lx) unless the floor is brighter; then the same colours are stretched up to the brightest floor reading
(a 32 × 32 grid plus the point under every beam axis), so a rig at full is not drawn grey (out of range) everywhere —
the range used is printed. A scene without a floor (MVR exports often contain only the rig) gets a
temporary wooden stage floor under its footprint (`--floor none` disables it). The exposure defaults to an
incident-light reading of the lit floor (`--ev auto`); `--ev` sets EV100, `--auto-exposure` lets the renderer adapt.
The demo look uses auto exposure unless `--ev` is given: its floor — coloured pools and spill — says little about how
bright its beams in the haze are. `render stages/Фокина.mvr --out f.png --width 480 --height 270` takes about 10 s on
lavapipe; with `--look demo` (12 frames of auto exposure) about 15 s at 320 × 180 and 55 s at 960 × 540.

**artnet-test** sends `ArtNetTestPattern` (pan/tilt sweep, colour chase, dimmer wave, zoom) with ArtSync and
ArtTimeCode to a node, a directed broadcast or `127.0.0.1` — 8 moving heads (12 ch) and 8 RGBW pars per universe, or
the fixtures of `--scene` with their own GDTF modes — so Art-Net reception can be checked without a console.

**info** prints a GDTF's type, specs (power, weight, candela, flux, noise), geometry, modes with footprints and wheels;
an MVR's layers, components, fixture types, GDTF resolution (embedded, library, substitute) and import issues; a
project's scene and assets. **validate** prints every issue grouped by category with the entities involved (`--plot`
adds the light plot).

## Issues raised by the engine

| Code | Severity | When |
|---|---|---|
| `engine.open-failed`, `engine.save-failed`, `engine.import-failed`, `engine.export-failed` | error | an operation could not happen at all |
| `engine.import-unsafe` | error | a model import crashed or timed out in the worker |
| `engine.import-in-process` | note | no worker was found; the model was imported in-process |
| `engine.asset-missing` | error | entities use a model or image the project does not contain |
| `engine.asset-unreadable` | error | a model asset cannot be decoded |
| `engine.fixture-model-failed` | warning | a fixture type's 3D model could not be loaded (its light is still drawn) |
| `engine.extra-atmosphere` | warning | several atmosphere volumes; only the first is simulated |
| `engine.gdtf-missing` | warning | a GDTF from a project or MVR could not be written to the cache |
| `engine.recording-damaged` | warning | a DMX recording is truncated or damaged |
| `engine.settings-invalid` | warning | a project's `engine`/`view` section could not be read |
| `engine.library-missing` | warning/error | no library folder, or it cannot be scanned |
| `engine.field-clamped` | warning | a fixture's field angle was wider than the renderer's 170° cone and was clamped |
| `engine.substitute-mode` | note | an MVR import's stand-in GDTF uses another of its modes, to fit the patch spacing |
| `engine.substitute-replaced` | note | fixtures that stood in for a missing model now use its GDTF, which the library has |

Plus the modules' own: `patch.*` (patch), `data.*`, `power.*`, `rigging.*` (electrical), `asset.*` (asset store),
`mvr.*`, `project.*`, and the simulations' issues.

## Tests

`tests/Candela.Engine.Tests` (75 tests, ≈ 50 s; the Vulkan tests run one at a time on lavapipe and are skipped without
a device):

* **Фокина.mvr**: opens with 91 fixtures, one substitute per model the library lacks (4 today) and no errors; Depence's
  patch spacing equals the manufacturers' footprints (Rivale Profile 42, MagicBlade R 44, Zonda 3 Wash/FX 26,
  Atomic 3000 LED 3), and GDTFs with the real names, modes and footprints in place of the substitutes resolve every
  type exactly with a clean patch (docs/mvr.md); the info/validate CLI checks follow the library the same way; every
  fixture has a state and a pose; the stand-ins use the mode that fits their patch spacing (no patch errors, one
  substitute warning per type, the record survives save and reopen, and the real GDTF replaces the stand-in when it
  arrives); after open white, RenderSync draws every model part of every fixture (pick ids map back) and exactly one
  light per lit beam — every fixture lit, the pixel-mapped MagicBlade stand-in included, except a pure strobe between
  flashes — with no field wider than 4 × its beam and nothing clamped; identical results for identical input
  sequences; export → reopen keeps ids, specs and addresses.
* **Demo look**: on Фокина the same look twice, strobes dark and everything else at full, ≥ 30 mirror pairs with the
  same colour and mirrored beams, ≥ 5 colours with white and ≥ 3 saturated ones, low heads fanning up, over the
  audience, across the stage and crossing, overhead heads down, Rivale stand-ins at their narrowest zoom, gobos on
  some overhead heads; a hazer runs and fog stays off; one undo step.
* **Pipeline**: an Art-Net packet (real UDP receiver on loopback) makes a MAC Viper DMX-driven and its head instances
  move over several frames (motor dynamics; the base does not move); console-driven status follows a DMX cable from a
  console (unplugging returns the fixture to manual; without consoles live universes drive); a hazer fills the air
  around it in 6 simulated seconds (automatic domain, density grid in the render world, pause freezes it); the light
  meter equals I/d² under an aimed head (aim error < 1.5°) and the light plot has its power and weight; moving an
  entity moves its instances, selection and visibility become flags, removal destroys instances; steady frames
  allocate nothing.
* **Projects**: save → open keeps fixtures (manual values, patch), walls, cables (anchors, endpoints), the imported
  model (decodes again) and the `engine`/`view` sections; a project opens with an empty library from its GDTF copies;
  merge import under a group with undo/redo; autosave writes a recoverable copy and `Recover` restores it; damaged and
  foreign files fail without losing the open scene.
* **Imports**: the worker is found next to the program; OBJ + MTL + texture imported in the worker (texture served
  from the package, disk cache written, undo/redo); a worker that crashes (SIGSEGV) or hangs (timeout 1 s) gives
  `engine.import-unsafe` and adds nothing; no worker → in-process with a note; unreadable and SketchUp files rejected
  with reasons; model packages round-trip and every truncation is rejected; images become posters/reference drawings.
* **Recordings**: 120 frames of two universes with timecode round-trip through a file (< 8 KiB) and play back
  byte-exact, including a lost universe and seeking backwards; seek by timecode; truncated and damaged files; the
  engine records its own input and plays it back to its fixtures.
* **Fire**: a flame projector (synthetic GENERATOR FIRE GDTF) burns from its manual values, lights the floor through
  the flame light samples, sends a temperature grid above 1000 K to the render world, feeds soot into the smoke and
  dies down when its valve closes.
* **CLI**: usage errors and missing files give their exit codes; every documented `render` option parses (the demo
  look with auto exposure unless `--ev`); `info` on the MVR and a GDTF; `validate` exits with 0 on Фокина (no patch
  errors with the fitting stand-in modes, no `data.no-signal` without consoles or data cables) and prints the light
  plot; `import-worker`
  writes a package with the given units or the reason it cannot; `artnet-test` sends ArtDmx packets to a UDP port;
  engine options from the user settings default to the repository library, and a missing library stays reported.
* **Rendering** (Vulkan): `candela render` on Фокина.mvr at 480 × 270 gives a lit, not blown-out stage that spans most
  of the picture; `--look demo` at 320 × 180 gives saturated light of at least three hues and beams in the air above
  the stage; the thumbnail of the MAC Viper shows the fixture against the background, is cached, and orbit frames
  differ; a GDTF deleted since the scan gives a dark thumbnail, not an exception; `--look dmx-file` plays a recording
  for `--time` before rendering.
* **Integration review**: lights against independent computations (position = the beam part's pose, unit direction
  and up, colour of luminance 1 in Rec.2020 for every light of Фокина, the same pan/tilt on a fixture turned 90° gives
  the beam turned 90°); incremental render sync equals a fresh binding after moves, reparenting, removal, undo/redo, a
  hidden layer and selection, and detaching leaves the render world empty; gobos projected when the world is attached
  before the first frame; a fixture whose GDTF is imported later appears; changing a fixture's GDTF or a model's import
  options redraws it; a library reload rebuilds every fixture without dangling meshes or gobos; opening another scene
  while attached; Фокина saved as a project reopens with identical fixtures, instances and lights; an MVR merged twice
  with undo/redo while drawn; noise sources at the specs level scaled by activity; a daisy chain behind a cabled
  fixture is console-driven; smoke and lights identical for 1 and 4 threads; recovered autosave sessions are gone after
  saving (with and without autosave); worker crashes with a lingering child, garbage output, and a project whose model
  crashes the worker; framing fits every point tightly; thousands of damaged recordings and model packages load or are
  refused with `InvalidDataException`.

## Known limitations

* Stand-ins look like the model they stand in for only roughly: Фокина's MagicBlade R (a batten of seven pixels) is
  drawn as a MagicPanel 602 (a 6 × 6 panel); the real GDTFs are published only on GDTF-Share, behind a login or
  CAPTCHA (docs/mvr.md). The MVR importer still picks the stand-in's mode by name; the engine corrects it afterwards.
* The demo look's patterns follow from positions and fixture categories only (no knowledge of the set, the camera or
  the performers); a rig with no symmetry gets fans that are not symmetric either.
* The light meter ignores gobos, blades, iris, prisms and shadows (the GPU lux overlay applies them).
* Fixture lens glow (emissive lenses following the intensity) is not drawn; beams light the scene and the haze.
* The DMX recorder samples the input once per engine frame (not every received packet).
* Flames from several devices are composited into one temperature grid of at most 96³ cells.
