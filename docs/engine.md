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
| `Library`, `Specs`, `Profiles`, `FixtureLibraryChanged` | Fixture library (library folders + GDTFs brought in by projects and MVR files), `_specs/fixtures_index.json`, compiled DMX profiles. `FixtureLibraryChanged` (`FixtureLibraryChange`: the new entries and their source — `Import` of a `.gdtf`, GDTFs embedded in an `Mvr`, a `Project`'s copies) is raised after GDTF files were added and every fixture was re-resolved. |
| `Input`, `Protocols`, `StartProtocols` | DMX input hub and the protocol manager; `StartProtocols` subscribes to the universes the patch uses. |
| `Dmx`, `Poses` | DMX evaluation of every fixture; world matrices of every fixture part. |
| `Simulation` | Smoke, fire and the acoustic field (`StageSimulation`). |
| `Network`, `Rigging`, `ElectricalProfiles`, `NetworkSolution`, `HasConsoles` | Cable/power/data solver, rigging analyzer, electrical fixture data. |
| `Issues`, `RefreshIssues()` | Issue log with the patch, electrical, rigging and engine providers, plus the issues of the last operations. |
| `Undo`, `Selection` | Undo history and selection of the open scene. |
| `Settings` (`EngineSettings`), `View` (`ViewSettings`), `MarkSettingsChanged()`, `MarkViewSettingsChanged()`, `SettingsVersion` | Stored with the project in the `engine` and `view` settings sections. Assigning another object, or calling `Mark…Changed()` after changing one in place (the editor's Preview/Render panels, overlay legends, work light, saved camera), makes the project modified (`IsModified`) and is picked up by the next autosave; `SettingsVersion` counts these changes. `New`/`Open` start unmodified. |
| `Models`, `Images` | Decoded models (native formats through the import worker) and the image source for every texture key. |
| `LightMeter`, `Recorder`, `Play`, `Player` | Illuminance and light plot; DMX recording and playback. |
| `Lights`, `LightCollector`, `Time`, `Frame` | The frame's photometric lights and clock. |
| `Render`, `AttachRenderWorld`, `DetachRenderWorld` | The renderer binding. |
| `Autosave`, `EnableAutosave`, `ConfigureAutosave`, `DisableAutosave`, `Recover`, `IsAutosaveEnabled`, `AutosaveInterval` | `ConfigureAutosave(enabled, interval)` changes the interval or pauses/resumes autosave **on the running session** — its files, lock and session key stay, so changing the setting never leaves a session behind to be offered as a crash; projects opened later use the same values; when autosave was never set up (or was disabled) enabling starts a session with the options last given to `EnableAutosave` (else the defaults), and a non-positive interval pauses and keeps the previous one. Autosave of the open project (`AutosaveService`, driven by `Tick`; a project is autosaved only when its scene, an asset (import options changed, removed) or the `engine`/`view` settings changed; a successful save discards the session's autosaves) and recovery of crashed sessions. `Recover(session)` continues the crashed session's files (`AutosaveOptions.Resume`), so the next successful save removes them — also when autosave is off — and a recovered project is never offered again after it was saved; until then the crash data stays recoverable. Opening another project closes the session normally; on a clean exit the application calls `DisableAutosave()` before disposing the engine (disposing alone keeps the recovery files). |

**Construction.** `new StageEngine(StageEngineOptions)`: `LibraryFolders` (empty → the repository during development:
the folder containing `Candela.sln`, searched upwards from the program and the current directory), `SpecsIndexPath`
(default: `_specs/fixtures_index.json` next to a library folder), a shared `Library` (tests, several engines),
`CacheDirectory` (default `ConfigPaths.CacheDir`), the import worker (below), and thread counts. A missing library or
unreadable specs file becomes an issue (`engine.library-missing`), never an exception.

**Generic cabling devices.** `GenericDeviceLibrary.Register(engine)` (in `Cabling/`) writes the GDTF files of the
generic DMX splitters, Art-Net / sACN nodes and dimmer pack (`Candela.Electrical.GenericDevices`, byte-identical on
every run) into `{CacheDirectory}/generic-devices/` — only when missing or different, written aside and renamed — and
adds those the library does not know yet, so they are placed, patched, solved, rendered and saved in projects like any
GDTF. The editor calls it right after creating its engine. A folder that cannot be written is reported
(`engine.generic-devices`, warning) and the library stays as it was.

### Operations

Every operation returns an `EngineResult` (`Success`, a one-line `Summary` for the status bar or the CLI, `Issues` most
severe first, the `Entities` it created) and reports the issues to `Issues`. Content problems never throw; `Success` is
false only when nothing happened (the open scene is then unchanged).

| Operation | Behaviour |
|---|---|
| `New(name)` | Empty project; undo, selection, settings and assets start afresh. |
| `Open(path)` | `.candela` through `Candela.Project` (tolerant loading; load issues reported; the `engine`/`view` sections restored, a section that no longer parses gives `engine.settings-invalid` and the defaults); `.mvr` through `Candela.Mvr` as a new, unsaved project. |
| `Save()`, `SaveAs(path)` | Atomic `.candela` write with the scene, every asset (original bytes), copies of the GDTF files the fixtures use (from the library or the project's own copies), the thumbnail (`Project.Thumbnail`) and the `engine`/`view` sections. `Save()` without a path fails with "Save as". |
| `Import(path, EngineImportOptions)` | `.mvr` merged under a new group (undoable); `.candela` merged under a group through `ProjectMerge` (new ids, references remapped, undoable); 3D models (OBJ, FBX, DAE, 3DS, STL, PLY, glTF/GLB) as a `MeshComponent` entity at `Placement`; images (PNG, JPEG, TGA, BMP, PSD, GIF, HDR) as an `ImageComponent` reference drawing or poster sized to the image's aspect; `.gdtf` read completely first (`GdtfReader`) and added to the fixture library only when it is a readable fixture type with at least one DMX mode — a broken file is refused with `engine.import-failed` (category Fixture) and never becomes a library entry; warnings of the reader become one `engine.gdtf-warnings` note. The result carries the new (or already present) entry in `EngineResult.LibraryEntry` and `FixtureLibraryChanged` is raised, so the editor can select it. SketchUp files are refused with advice. |
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
602). The importer gives each stand-in type the mode that best fits its patch spacing — the requested mode when it
fits, else the largest that fits, else the smallest (`mvr.substitute-mode`, docs/mvr.md; the MagicBlade's 44-channel
"Extended" would otherwise have become the panel's 160-channel "Extended": 73 overlap errors on Фокина) — with the
same rule (`FixtureType.FitModeToSpacing`, Candela.Gdtf) that the DMX engine and the patch use for fixtures whose
`GdtfSpec` is still a stub name. After the import the engine records every stand-in in `EngineSettings.Substitutes` (requested GDTF and
mode, stand-in file, spacing; saved with the project). The patch judges stand-ins by that record (`PatchIssueProvider.Substitutions`): one `patch.substitute-gdtf`
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
   the type's typical ratio (docs/dmx.md); the collector only keeps beam and field within the renderer's 170° cone as
   a sanity clamp. Wide washes (LED panels, battens, strobes, tubes) reach it with their faint field edge only, which
   is not reported; fixtures whose beam (50 %) cone itself is wider — bulbs radiating in nearly every direction — are
   listed (`ClampedFieldFixtures`) and reported (`engine.field-clamped`).
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
| Fixtures | The models of each fixture type + mode are loaded once (`FixtureGeometryBuilder` + `GdtfModelLoader`, textures through `gdtf:` keys) — on a background task by default (`LoadFixtureModelsInBackground`; `CompletePendingLoads` waits) — and uploaded once; every fixture gets one instance per model part of every geometry part, whose world matrix follows the part's pose. **Lens glow**: the instances of a beam's geometry part emit peak candela / lens area (π·r², r = the beam's `BeamRadius`, 5 cm when unset; summed over the beams of the part, at most 10⁹ cd/m²) in the beam's colour through `IRenderWorld.SetInstanceEmission(instance, luminance, axis, halfAngle)`, seen along the part's −Z axis and falling off like the beam (half at half the widest beam angle), so a lens glares only for eyes in its beam; the renderer's bloom adds the glare. Computed per fixture in one pass over its beams (a reused per-part buffer, linear in parts + beams + instances also for pixel bars with hundreds of beams) and sent only when it changes. |
| Lights | `SetLights` every frame; gobo textures are created once per wheel slot from the GDTF media (alpha = transmission = linear luminance × alpha of the picture, RGB = tint of coloured glass). |
| Walls, floors, ceilings | `WallMesh` + the built-in material (`MaterialLibrary`) with its UV scale. |
| Images | `ImagePlaneMesh` + `ImagePlaneMesh.CreateMaterial`; reference drawings unlit (`InstanceFlags.Reference`), posters lit. |
| Imported models | Decoded through `EngineModels` — on a background task by default (`LoadModelsInBackground`; a native format goes through the import worker, which may take up to its timeout; the objects appear when their model has decoded, `PendingModels` counts the decodes, `CompletePendingLoads` waits for them too); material overrides by slot with built-in materials. Decoded models live in the project's `AssetStore` (`SetDecodedModel`), so the store, the renderer and the light meter share one copy. |
| Trusses | Their model, or `TrussMesh` in aluminium. |
| Cables | Tube meshes (`CableTubeMesh`, catenary sag from the slack factor, dark rubber tinted by the type) plus coloured lines (`SetLines`, the cable type's colour) for the cable overlay; rebuilt when an anchor entity moves. |
| Consoles, power sources, rigging points | Simple primitives (desk, distro box, hoist). |
| Atmosphere | `SceneSettings.BaseHazeScattering` (or `HazeOverride`) and anisotropy, the smoke density grid when the smoke simulation stepped, one temperature grid per flame (its own box and resolution: the first as `FlameTemperature`, the others as `FlameTemperatures`; the grid objects are reused between steps); noise sources every frame. |

**Flags.** `Selected` (selection), `Hidden` (entity or an ancestor hidden, or its layer hidden; a hidden fixture still
emits its light — hiding the rig to look at the stage keeps the stage lit; its dimmer switches it off), `CableHighlight`
(cables and the devices they plug into), `CastShadow` (`FixtureComponent`/`MeshComponent.CastShadow`; walls, trusses
and posters cast, reference drawings and cables do not), `Reference`. Every drawn entity has a pick id:
`TryGetEntity(pickId, out entityId)`, `PickIdOf(entityId)`; `InstancesOf(entityId)` lists what was drawn (part names,
world matrices, flags and world bounds) for tools and tests. With `OutlineSelectedDescendants` the descendants of a
selected entity are drawn `Selected` too (the outline of a selected group shows its content; off by default).

Flags are derived from the scene's change kinds, not by scanning every entity each frame: `VisibilityChanged` and
`Reparented` re-derive the entity's subtree, `LayerChanged` the entity, `LayersChanged` (a layer shown, hidden, added or
removed) every entity once, a selection change only the entities whose selection changed, a cable change the devices
whose cable highlight changed; `LockedChanged` does not affect drawing. Steady frames send no flag updates (tested: 120
random visibility/layer/selection/reparent/cable edits always give the flags a per-frame scan would give).

**World bounds.** `TryGetWorldBounds(entityId | pickId, out Aabb, includeHidden = true)`, `IncludeWorldBounds(pickId, ref
bounds)`, `VisibleWorldBounds()` and `CollectVisibleCorners(list)` give the world-space boxes (m) of what is drawn —
each instance's mesh bounds placed with its current world matrix (a fixture in its current pan/tilt pose, a decoded
model, a wall, a cable) — for box selection, framing and drop targets; boxes larger than `MaxContentExtent` (10 km)
and non-finite placements are left out of the visible totals. Nothing is allocated per query beyond the caller's list.

**Work light.** `RenderSync.WorkLight` (`WorkLightMode`: `Off` — the default, so renders never get it unless asked —,
`Auto`, `On`) adds `WorkLightRig`'s dim neutral rig to the light list sent to the renderer: one D65 key light from the
audience side (front-left, above; 40 lx at the centre on a surface facing it; casts shadows) and a soft ambient of six
very wide lights from ±X, ±Y and ±Z (8 lx each on a surface facing them; no shadows), aimed at the centre of the drawn
content from 2.5 × its radius (at least 5 m). `Auto` adds it only while no stage light reaches 1 cd (no fixture emits,
every dimmer closed). The rig is placed around `WorkLightContent`: the visible instances like `VisibleWorldBounds()`, but
with each fixture's parts at the fixture's own placement instead of their pan/tilt pose, so where the heads point (DMX,
motors still travelling when the rig was placed) never moves it. It depends only on that box and is recomputed only when
the scene or the set of drawn entities changes, so the same scene always gets the same work light, whatever happened
before, and it costs nothing per frame. It is not stage light: `engine.Lights`, the
light meter and the light plot never include it, and it is left out while the project's view shows the lux overlay. The
editor's preview passes `ViewSettings.WorkLight` (stored with the project, default `Auto`); `candela render
--work-light auto|on` asks for it in a render. `IsWorkLightOn` and `WorkLights` tell what the last update sent, `WorkLightContent` the box the rig was placed around.

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

`engine.Recorder.Start(engine.Time, name)` … `Stop()` records the merged hub input **at packet rate**: the recorder
follows the hub's receive path (`DmxInputHub.AddObserver`, called on the protocol threads inside the hub's lock) and
stores every change of a merged universe the moment it arrives — the slots that changed (runs; gaps of up to four equal
slots bridged), universes that appear (full; those already live at the start make the first frame) or stop (lost,
stamped when the last source timed out) — and every new timecode (frames where one arrived are flagged
`TimecodeReceived`; the others carry the last one). Timestamps are the recorder's own: the hub's clock at arrival
relative to the start, so a 44 Hz console recorded by a 30 fps editor keeps all of its frames. `Tick` reports the engine
clock (`Capture`); when the engine runs ahead of real time (offline ticking, a render job, tests) the engine time of the
last frame is the earliest time a packet can get, so a recording made that way stays on the show's timeline. Changes of
different universes within 1 ms share a frame. Unchanged packets cost a 512-byte comparison and allocate nothing.

`DmxRecording.Save/Load` (`.cdmx`, format version 2): magic, version, creation time, name, capture mode
(`DmxCaptureMode.Packets`; `EngineFrames` for recordings sampled per frame), then a zlib stream of frames (time,
flags — bit 0 timecode, bit 1 timecode arrived here — and varint-encoded runs). Version 1 files load
(`LoadedFormatVersion` 1, `EngineFrames`; a frame counts as a timecode arrival when its timecode differs from the
previous frame's) and are written as version 2; a file of a newer version is refused with a clear
`InvalidDataException`. A file cut short loads the frames before the damage with `engine.recording-damaged`; anything
that is not a recording throws `InvalidDataException`. Recordings keep a full state every 2 s for seeking.

`engine.Play(recording, startSeconds)` plays it into the hub as the source `recording` following the engine clock;
`DmxPlayer.Seek(time)`, `SeekTimecode(timecode)` (to the moment that timecode arrived, interpolated between arrivals;
the player submits its timecode with the source `recording`, so `Input.LastTimecodeSource` names it) rebuild the state from the nearest keyframe, so rendering a timecode range means: seek, then tick at the
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
**2** usage error, **3** no usable Vulkan device (for `render --final`: none that can run the path tracer), **4** the
input has errors (`validate` found error-level issues; `import-worker` rejected the model), **130** cancelled with
Ctrl+C (a final render writes what it has rendered).

```
candela render <scene.mvr|scene.candela> --out <file.png> [--width W --height H]
               [--camera x,y,z --target x,y,z | --view front|top|side|auto] [--fov deg]
               [--mode full|colorless|wireframe] [--overlay none|lux|noise|cables] [--haze <1/m>]
               [--look open-white|demo|show|dmx-file <recording.cdmx>] [--time <s|HH:MM:SS:FF>]
               [--end <s|HH:MM:SS:FF> [--fps F]] [--ev auto|floor|<EV100>] [--auto-exposure] [--frames N] [--grid]
               [--floor auto|none] [--work-light off|auto|on] [--device <name>] [--library <dir>]... [--cache <dir>]
               [--final [--samples N] [--seconds S] [--exr <file.exr>] [--denoise] [--bounces N]
                        [--volume-bounces N] [--seed N] [--clamp <cd/m²>]]
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
temporary wooden stage floor under its footprint (`--floor none` disables it). `--work-light auto` (or `on`) adds the
work light (see RenderSync) for scenes whose fixtures are dark; a render never gets it unless asked, and never in a
`--overlay lux` picture (the overlay of the picture rendered decides, not the one saved with a project). The exposure is automatic by default
(`--ev auto`): a stage picture is mostly dark air with bright beams and pools, which a reading of the floor alone says
little about, so the renderer adapts to the picture (`PreviewSettings.AutoExposure` with **stage metering**, `ExposureMetering.Stage`:
the lit part of the image — the 85th to 99th percentile of the luminance — placed 1.5 stops below middle grey, so light
pools stay below white, beams in haze stay visible and the dark venue stays dark; see docs/rendering.md),
starting from an incident-light reading of the lit floor. It renders at least 12 frames, each standing for 0.5 s of
adaptation (the renderer adapts with a time constant of about 0.7 s; at the 1/30 s of a live frame the exposure would
stop less than halfway), so the still is fully adapted. `--ev floor` fixes the exposure at the floor reading,
`--ev <EV100>` at a value, and `--auto-exposure` adapts starting from the `--ev` value. `render stages/Фокина.mvr
--out f.png --width 640 --height 360` takes about 14 s on lavapipe (12 frames; about 9 s with `--ev floor` and its
6 frames).

**Image sequences.** `--end` renders every 1/`--fps` s (default 25 fps) from `--time` up to and including `--end`
(files `name_0001.png` …, at least four digits, like the editor's render job; at most 100 000 frames). The engine
simulates from 0 to the first frame in steps of at most 1/30 s and then advances exactly one frame interval between
frames — DMX playback, motors, haze, smoke and flames follow frame by frame. Times are seconds on the engine clock
(= seconds into a `--look dmx-file` recording) or SMPTE timecodes `HH:MM:SS:FF` (`HH:MM:SS;FF` drop-frame) looked up in
the recording's timecode track (`DmxRecording.FindTimecode`, frame rate from the recording's first timecode; a timecode
the recording never received counts from its first timecode when that lands inside it, otherwise the render stops
with exit code 1). Timecodes need `--look dmx-file`. The real-time view lets auto exposure settle on the first frame
and then adapt in real time.

**Final render** (`--final`): each image is path traced (`VulkanRenderer.StartFinalRender`, docs/rendering.md) with
`--samples` per pixel (default 64) or until `--seconds` of rendering time are up (whichever comes first; `--seconds`
alone renders until the time is up), `--bounces` surface and `--volume-bounces` haze/smoke scattering events (6 and 2),
`--seed` (deterministic), `--clamp` (firefly clamp in cd/m², off by default) and `--denoise`. The PNG uses the same
display transform as the real-time view; `--exr` also writes linear HDR OpenEXR (ZIP): R, G, B, A in float (Rec.2020,
scaled by the PNG's exposure; the `whiteLuminance` attribute = 1 / exposure gives cd/m², `chromaticities` = Rec.2020),
the raw result as layer `raw` when denoised, and `albedo`, `N` (world normal) and `Z` (view depth, m). Auto exposure
meters the final image (stage metering); a sequence keeps the first frame's exposure (no flicker). Progress is printed
every 10 s and a line per frame (samples, time, EV). Ctrl+C stops after the work in flight and writes the image of
the samples done (exit code 130; nothing is written before the first sample). `--mode`, `--overlay` and `--grid` do
not apply to `--final`, and the final-only options need it (usage errors). A GPU without ray queries gets the reason
and exit code 3; the real-time `render` works there. `render stages/Фокина.mvr --look demo --final --seconds 60
--width 480 --height 270` reaches about 10 samples on lavapipe.

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
| `engine.gdtf-warnings` | note | an imported `.gdtf` was read with warnings (defaults used) |
| `engine.recording-damaged` | warning | a DMX recording is truncated or damaged |
| `engine.settings-invalid` | warning | a project's `engine`/`view` section could not be read |
| `engine.library-missing` | warning/error | no library folder, or it cannot be scanned |
| `engine.field-clamped` | warning | a fixture's beam angle (50 % cone) is wider than the renderer's 170° cone; its light is drawn as that cone (a wide wash whose field alone is trimmed to 170° is not reported) |
| `engine.substitute-replaced` | note | fixtures that stood in for a missing model now use its GDTF, which the library has |
| `engine.generic-devices` | warning | the generic cabling devices could not be written to the cache (`GenericDeviceLibrary`) |

Plus the modules' own: `patch.*` (patch), `data.*`, `power.*`, `rigging.*` (electrical), `asset.*` (asset store),
`mvr.*`, `project.*`, and the simulations' issues.

## Tests

`tests/Candela.Engine.Tests` (108 tests, ≈ 1.5 min; the Vulkan tests run one at a time on lavapipe and are skipped without
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
* **Generic devices**: `GenericDeviceLibrary` writes the splitters, nodes and dimmer once (identical bytes, no temporary
  files left), adds them to a private library, adds nothing the second time, rewrites a damaged file, resolves a
  placed node's Ethernet input and DMX outputs, and reports a cache folder that cannot be written instead of throwing.
* **Recordings**: 120 frames of two universes with timecode round-trip through a file (< 8 KiB) and play back
  byte-exact, including a lost universe and seeking backwards; seek by timecode; truncated and damaged files; the
  engine records its own input and plays it back to its fixtures.
* **Fire**: a flame projector (synthetic GENERATOR FIRE GDTF) burns from its manual values, lights the floor through
  the flame light samples, sends a temperature grid above 1000 K to the render world, feeds soot into the smoke and
  dies down when its valve closes.
* **CLI**: usage errors and missing files give their exit codes; every documented `render` option parses (the demo
  look with auto exposure, the default); `info` on the MVR and a GDTF; `validate` exits with 0 on Фокина (no patch
  errors with the fitting stand-in modes, no `data.no-signal` without consoles or data cables) and prints the light
  plot; `import-worker`
  writes a package with the given units or the reason it cannot; `artnet-test` sends ArtDmx packets to a UDP port;
  engine options from the user settings default to the repository library, and a missing library stays reported.
* **Rendering** (Vulkan): `candela render` on Фокина.mvr at 480 × 270 gives a lit, not blown-out stage that spans most
  of the picture; `--look demo` at 320 × 180 gives saturated light of at least three hues and beams in the air above
  the stage; the thumbnail of the MAC Viper shows the fixture against the background, is cached, and orbit frames
  differ; a GDTF deleted since the scan gives a dark thumbnail, not an exception; `--look dmx-file` plays a recording
  for `--time` before rendering.
* **Final render CLI** (`FinalRenderCliTests`): the `--final` options parse, have their defaults (`--seconds` alone
  renders until the time is up) and are refused without `--final` or with `--mode`/`--overlay`/`--grid`; times parse
  as seconds or timecodes (drop-frame with `;`), timecodes need a recording, an end before the start is refused; a
  recording with EBU timecode from 01:00:00:00 plans 01:00:00:10 … 01:00:01:15 at 2 fps as frames at 0.4, 0.9 and
  1.4 s named `_0001` …; a timecode outside the recording is reported. On Vulkan: `--final --samples 2 --denoise
  --exr` on Фокина (demo look, 160 × 90) writes the PNG and an EXR with R, G, B, A (float), `raw.*`, `albedo.*`,
  `N.*`, `Z`, Rec.2020 chromaticities and a white luminance, every value finite; the timecode range of a recording
  (full, then a blackout at 1 s) renders three real-time PNGs and three path-traced PNG + EXR frames with one
  exposure, the frame after the blackout darker than a quarter of the first; a render cancelled before its first
  sample writes nothing and exits with 130.
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
* **Editor follow-ups** (`EngineFollowUpTests`, `RenderSyncFollowUpTests`, `PacketRecordingTests`): importing a
  library GDTF returns its entry and raises `FixtureLibraryChanged` once; random bytes, a truncated archive, broken XML
  and a GDTF without DMX modes are refused with an issue and never enter the library; the autosave interval and pause
  change on the running session (same service and session key), carry over to projects opened later and start a session
  only when enabled; view/engine settings changes make the project modified, survive save and reopen and reach the
  autosave; flags after 120 random visibility/layer/selection/reparent/lock/cable edits equal a per-frame scan, steady
  frames send no flag updates, a selected group can outline its content; world bounds per entity and pick id follow
  moves and hiding; the work light lights a dark scene in `Auto` (40 lx key at the centre, never in `engine.Lights` or
  the light meter, off with a lit fixture, on top of it in `On`, off under the lux overlay, deterministic) and its key
  covers the content; a 44 Hz console recorded by a 30 fps engine keeps every packet at its arrival time and a
  timecode seek lands between engine frames; unchanged packets are not recorded, a timed-out source is stamped when it
  timed out; a version-1 recording loads, migrates to version 2 and a newer version is refused; the engine records at
  packet rate while it ticks.
* **Follow-up review**: a background model decode that throws is recorded as failed in the asset store and not started
  again; fixtures named by an MVR stub spec get the same stand-in mode in the looks and the Art-Net test pattern as in
  the DMX engine; an ARRI SkyPanel's 179° field is trimmed to the renderer's 170° cone silently, while an Astera
  LunaBulb, whose beam cone itself is wider, is reported (`engine.field-clamped`).

## Known limitations

* Stand-ins look like the model they stand in for only roughly: Фокина's MagicBlade R (a batten of seven pixels) is
  drawn as a MagicPanel 602 (a 6 × 6 panel); the real GDTFs are published only on GDTF-Share, behind a login or
  CAPTCHA (docs/mvr.md). The MVR importer still picks the stand-in's mode by name; the engine corrects it afterwards.
* The demo look's patterns follow from positions and fixture categories only (no knowledge of the set, the camera or
  the performers); a rig with no symmetry gets fans that are not symmetric either.
* The light meter ignores gobos, blades, iris, prisms and shadows (the GPU lux overlay applies them).
* Lens glow is camera-only (the renderer's indirect light does not see it; the beam's light is the spot light).
* `Фокина.mvr` at open white: its 32 `Zonda` pixel beams have GDTF beam type Glow and each receives an eighth of the
  fixture's 465 000 cd (Candela.Dmx), so each is a Lambertian emitter of about 183 klm; in haze they light the air
  evenly and mask the beams of the other fixtures (docs/rendering.md shows the stage with and without them).
* The work light is made of spot lights (the render contracts have no ambient term): in haze its six wide ambient
  lights scatter a faint even glow (about a fifth of the brightness of a lit surface at the default haze), and the
  renderer's lux overlay would count it if a view showed lux while the project's view does not.
* The renderer keeps at most 64 flame grids and 64 M texels in all (the hottest wins where they overlap; grids beyond
  the budget are skipped with a warning).
