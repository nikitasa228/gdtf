# Project files (`Candela.Project`)

Candela's own save format. Ordinary scene formats (MVR, glTF, …) cannot hold what the program keeps per project —
cables with branches and port connections, consoles, power sources, the atmosphere domain, saved views, the renderer's
preview/final settings, overlay gradients, panel layouts — so a project is a custom archive: a `.candela` file is a
**zip** with JSON documents and the original bytes of every asset and fixture file the scene needs, so the project
opens on another computer without the user's model folders or fixture library.

The module references only `Candela.Core` (scene model, JSON configuration) and `Candela.Assets` (asset store). Higher
layers store their own settings in the project as named JSON sections without this module knowing their types.

| Operation | API | Thread |
|---|---|---|
| Save / Save as | `ProjectFile.Save(path, ProjectData, ProjectSaveOptions?)` | main (serialises the scene) |
| Save in the background | `ProjectSnapshot.Capture(project, options)` on the main thread, then `ProjectFile.Save(path, snapshot)` | any |
| Open | `ProjectFile.Load(path, ProjectLoadOptions?)` → `ProjectData` | any (builds a new, unshared project) |
| Describe without opening | `ProjectFile.ReadManifest(path)`, `ReadThumbnail(path)`, `IsProjectFile(path)` | any |
| Import (merge) another project | `ProjectMerge.Import(target, source \| path, ProjectMergeOptions?)`, undoable via `ProjectMerge.CreateCommand` | main |
| Autosave and recovery | `AutosaveService` (`Update()` from the main loop), `AutosaveService.FindRecoverable()`, `RecoverableSession.Load()` | main + background writer |

## 1. Archive layout

```
show.candela (zip, UTF-8 entry names)
├── scene.json                 entities, layers, scene settings (Core SceneSerializer)
├── settings.json              { "<section>": <any JSON>, … } — project settings of higher layers
├── assets/
│   ├── index.json             one record per asset (id, kind, file names, sizes, SHA-256, import options)
│   └── {asset guid}/          original files, unchanged
│       ├── chair.obj          main file (original file name)
│       ├── chair.mtl          companion files of models, at their relative paths
│       └── tex/wood.png
├── fixtures/
│   ├── index.json             GDTF name → entry, size, SHA-256
│   └── Robe@Robin_T1.gdtf     copies of every GDTF file the scene's fixtures use
├── thumbnail.png              optional preview (PNG only)
└── manifest.json              written last; format id, version, counts
```

Entries are written in the order shown; `manifest.json` is last because its counts are only known at the end (readers use
the zip directory, so order does not matter for reading). JSON documents and compressible assets are deflated
(`ProjectSaveOptions.CompressionLevel`, default *Optimal*); files that are already compressed (PNG, JPEG, GDTF, zip, …)
are *stored* so saving never spends time recompressing them.

**Safe names.** Stored file names keep the original name where possible but are made extractable on Windows, macOS and
Linux: control characters and `/ \ : * ? " < > |` become `_`, trailing dots/spaces are removed, reserved device names
(`CON`, `COM1`, …) get a leading `_`, long names are shortened (keeping the extension), and names that differ only in
case get ` (2)`, ` (3)`, …. The original name is kept in the index (`fileName`, `name`), so nothing is lost.

### 1.1 `manifest.json`

```json
{
  "format": "candela-project",
  "formatVersion": 1,
  "appVersion": "0.1.0",
  "createdUtc": "2026-10-18T12:00:00Z",
  "modifiedUtc": "2026-10-18T14:31:07Z",
  "projectId": "5b0c3c0e-…",
  "sceneName": "Фокина",
  "entityCount": 91,
  "fixtureCount": 91,
  "assetCount": 0,
  "gdtfCount": 4,
  "hasThumbnail": true
}
```

`ProjectManifest` mirrors it. `projectId` is the stable identity of the project (it names the autosave files and
survives *Save as*); it defaults to the scene id. `createdUtc` is kept across saves; `modifiedUtc` is the time of the
write (`ProjectSaveOptions.TimeProvider`). The counts let an Open dialog describe a project without loading it.

### 1.2 `scene.json`

Exactly what Core's `SceneSerializer` writes (`SceneFile`): `formatVersion` (scene format, currently 1), `id`, `name`,
`layers`, `settings` (`SceneSettings`), and `entities` — flat, parents before children, each with `id`, `parent`,
`name`, `layer`, `visible`, `locked`, `transform` (`position` m, `rotation` quaternion x y z w, `scale`) and
`components`. Components are polymorphic with a `$type` discriminator that is part of the file format and never
changes: `fixture`, `mesh`, `wall`, `image`, `truss`, `riggingPoint`, `cable`, `console`, `powerSource`, `atmosphere`,
`camera`, `group`. Units are SI (metres, kilograms, volts, amperes, …), coordinates right-handed Z-up; vectors are
JSON arrays; enums are strings; non-finite numbers are the strings `"NaN"`, `"Infinity"`, `"-Infinity"`; JSON is
camelCase and tolerant of unknown properties (forward compatible). Properties added later are optional and absent
from older files, which then load with the documented default — e.g. `powerSource.mainsInConnector` (the connector of
a distro's inlet; absent or null: derived from the source's outlets and phases by Candela.Electrical). The lists of
the cabling components (`cable.nodes`, `segments`, `endpoints`, `console.outputs`, `powerSource.outlets`) are never
null in memory: a damaged file that writes `null` for one loads it as an empty list, so the cable, power and data
code that walks them cannot fail on it.

The file is written with Core's `CandelaJson.Options`. Null values are omitted, **except** for a property whose default
is not null: `"protocolId": null` (a console without protocol input, whose default is Art-Net) is written explicitly,
because an omitted value reads back as the default. (Candela.Project used to apply this rule with options of its own;
Core's options now do the same and write byte-identical files, so no migration is needed.)

**Properties of newer versions are kept.** Properties this version does not know — at the top of the file, in an entity
record, a layer, the scene settings or a component — are read into Core's `UnknownProperties` of the scene, entity,
layer, settings or component and written back unchanged on save (the tolerant reader collects them for the scene and
the entity records it reads element by element; layers, settings and components keep them through Core's JSON
extension data). Components of types this version does not know are kept from loading and written back unchanged to
the entities that still exist (`ProjectData.UnknownComponents`). Data of newer versions may nest as deep as the readers
allow (`CandelaJson.MaxDepth`, 256 levels): every path that copies it back into a file (scene, kept components, kept
asset records, repaired components) parses with the same limit, so what loads also saves.

### 1.3 `settings.json`

An object of **named sections**, each any JSON value, owned and versioned by the layer that writes it — for example
`preview` and `render` (renderer quality presets), `view` (view mode, camera, exposure), `luxOverlay` / `noiseOverlay`
(gradients and ranges), `layout` (docked panels). In memory they are `ProjectData.Settings` (`ProjectSettings`, an
`IReadOnlyDictionary<string, JsonElement>`):

```csharp
project.Settings.Set("preview", previewSettings);                  // serialised with CandelaJson.Options
var preview = project.Settings.GetOrDefault("preview", PreviewSettings.Default);   // never throws
project.Settings.SetJson("layout", layoutJson);                    // raw JSON
```

`TryGet<T>` / `GetOrDefault<T>` return the fallback when a section is missing or no longer matches the type, so a
layer whose settings schema changed simply starts from its defaults. Sections are stored as cloned, immutable
`JsonElement`s, so a snapshot can be written on a background thread. Section names are case-sensitive, by convention
short lower-camel identifiers. `ProjectSettings.Changed` is raised for every change (autosave uses it).

### 1.4 `assets/index.json`

```json
{ "assets": [ {
    "id": "7f9c…", "kind": "Model", "fileName": "chair.obj", "entry": "assets/7f9c…/chair.obj",
    "size": 1234, "sha256": "…", "contentHash": "…", "sourcePath": "C:\\Models\\chair.obj",
    "importOptions": { "upAxis": "Z", "unitScale": 0.01, "guessUnits": true, "smoothingAngleDegrees": 45, "maxParts": 1024 },
    "companions": [ { "path": "tex/wood.png", "entry": "assets/7f9c…/tex/wood.png", "size": 999, "sha256": "…" } ]
} ] }
```

One record per `AssetStore` entry in store order: `id` (referenced by `MeshComponent.ModelAssetId`,
`ImageComponent.ImageAssetId`, `TrussComponent.ModelAssetId` and `asset:{id}` texture keys), `kind` (`Model`, `Image`,
`Gdtf`, `Texture`), original file name, the stored main file and model companions (MTL, `.bin` buffers, textures) with
size and SHA-256, the model import options (axis/unit overrides, so a model decodes identically on another machine)
and the informational import path (`ProjectSaveOptions.IncludeSourcePaths = false` leaves local folder names out of
projects that are shared). Original bytes are never re-encoded.

An asset whose files are intact but that this version cannot load — a `kind` added by a newer version, or content the
asset store does not accept — is **kept verbatim**: its record (with every field, known or not) and every file of its
folder `assets/{id}/` are written back by the next save (`ProjectData.KeptAssetIds`, issue `project.asset-kept`).
Objects using it show a missing asset in this version.

### 1.5 `fixtures/` and `fixtures/index.json`

```json
{ "fixtures": [ { "name": "Ayrton@Zonda_3_FX@V1.81.gdtf", "entry": "fixtures/Ayrton@Zonda_3_FX@V1.81.gdtf", "size": 812345, "sha256": "…" } ] }
```

Every distinct `FixtureComponent.GdtfSpec` of the scene is stored once, keyed by exactly that name. On save the bytes
come from, in order: the `ProjectSaveOptions.GdtfProvider` callback (normally the fixture library), the project's own
copy (`ProjectData.GdtfFiles` — filled by loading, so a project opened without the library keeps its fixtures when
saved again), and a `Gdtf` asset with that file name. Each candidate is checked cheaply (a zip with `description.xml`)
and the first valid one is stored, so a damaged library file (a half-finished download) never replaces a good copy
kept in the project (`project.save-gdtf-invalid` says what happened). On load they become `ProjectData.GdtfFiles` (`FindGdtf(name)`: exact, then case-insensitive), which
the engine offers to the fixture library as project-local files.

### 1.6 `thumbnail.png`

Optional preview image (`ProjectData.Thumbnail`), stored only when it is a PNG; read on its own by
`ProjectFile.ReadThumbnail` for Open dialogs.

## 2. Saving

`ProjectFile.Save(path, project, options)` captures a `ProjectSnapshot` (the scene serialised to UTF-8 JSON, references
to the immutable asset entries, settings elements, GDTF copies and thumbnail) and writes it **atomically**:

1. The archive is written to a hidden temporary file in the target folder (`.show.candela.{random}.tmp`, same volume),
   one GDTF at a time (their bytes never all sit in memory), and flushed to disk.
2. The temporary file is re-opened and checked (zip directory, manifest format id, scene present;
   `ProjectSaveOptions.Verify`).
3. It is moved over the target in one rename (retried briefly if a virus scanner holds the target on Windows).

If anything fails before the rename — a full disk, a missing permission, the GDTF provider throwing — the exception
propagates, the temporary file is deleted and **the previous file is untouched**; `ProjectData.FilePath` and
`Manifest` change only after success. The file system is respected as well:

* a target that is a **symbolic link** is written through it (the file it points to is replaced, the link stays), so a
  project kept in a synchronised folder and linked from elsewhere really is updated;
* a **read-only** target is refused with `UnauthorizedAccessException` (on Unix a rename would ignore the file's own
  permissions);
* the target's **Unix permission bits** are kept, so saving never makes a private project readable by others;
* the temporary name is shortened when needed, so every file name the file system accepts can be saved (a long
  Cyrillic name is twice as long in UTF-8 bytes).

Problems that do not prevent saving are returned in `ProjectSaveResult.Issues`:

| Code | When |
|---|---|
| `project.save-gdtf-unavailable` | no bytes for a GDTF the scene uses (the fixtures then need the library on another computer) |
| `project.save-gdtf-invalid` | the provided GDTF is not a zip with `description.xml` (stored from the project's copy when that is valid) |
| `project.save-asset-missing` | components refer to an asset id that is not in the store |
| `project.save-thumbnail-invalid` | the thumbnail is not a PNG |

Saving a loaded project again without changes (and with the same clock) reproduces every entry byte for byte (tested).

## 3. Loading

`ProjectFile.Load(path)` reads the manifest, checks the format and version (migrating older files), reads the scene,
then settings, assets, GDTF files and thumbnail. Only four situations throw (`ProjectFormatException`, message written
for the user): the file is not a zip archive (or its directory is damaged); it has neither manifest nor scene; the
manifest names another format; `scene.json` is missing or not valid JSON. A version problem throws
`ProjectVersionException` (a `ProjectFormatException`). Should a framework reader still fail on damage no check
anticipated, the failure reaches the caller as a `ProjectFormatException` with the cause attached, never as another
exception type. **Everything else is tolerated** and reported in `ProjectData.LoadIssues` (Core `Issue`s with stable
codes and the related entity ids, so clicking an issue selects the affected objects):

| Damage | Result | Code |
|---|---|---|
| no `manifest.json` / damaged manifest | read as the current format | `project.manifest-missing` / `project.manifest-invalid` |
| JSON text that parses but cannot become a string (bytes that are not UTF-8, an escaped surrogate without its pair `"\ud800"`) | the characters become U+FFFD | `project.text-repaired` |
| a manifest field of the wrong type | default for that field | `project.manifest-invalid` |
| `settings.json` missing, damaged or not an object | default settings | `project.settings-invalid` |
| an entity record that is not an object | skipped | `project.scene-entity-invalid` |
| missing or repeated entity id | new id | `project.scene-id-invalid` |
| parent missing / parent cycle | moved to the scene root | `project.scene-parent-missing` / `-cycle` |
| nesting deeper than 1000 levels (a hostile chain would overflow the stack of Core's recursive tree walks) | moved to the root, world placement kept | `project.scene-too-deep` |
| non-finite or unreadable transform | identity | `project.scene-transform-invalid` |
| a value that cannot be read (`"kind": "Curtain"`, a malformed id in one cable node) | that value takes its default, the rest of the component, layer or scene settings is kept | `project.scene-value-repaired` / `project.scene-settings-invalid` |
| a component that cannot be read even so (no `$type`) | dropped, the rest of the entity loads | `project.scene-component-invalid` |
| a component type this version does not know | kept verbatim and saved again | `project.scene-component-unknown` |
| explicit `null` in a required value (`"addresses": null`) | default value | `project.scene-value-repaired` |
| entities on layers that do not exist | parent's or default layer | `project.scene-layer-missing` |
| `assets/index.json` missing or damaged | assets recovered from `assets/{guid}/` folders (kind from the extension) | `project.asset-index-invalid` |
| an invalid asset record | skipped; its folder, if any, is recovered like a lost index | `project.asset-record-invalid` / `project.asset-index-invalid` |
| an intact asset of an unknown kind, or content the store rejects | kept verbatim and saved again | `project.asset-kept` |
| content the store rejects in an asset recovered without its record | not loaded | `project.asset-rejected` |
| an asset file missing | asset not loaded | `project.asset-missing` |
| an asset file damaged (unreadable entry, wrong size or SHA-256, over the size limit) | asset not loaded | `project.asset-corrupt` |
| a model companion missing or damaged | model loads without it | `project.asset-companion-missing` |
| `fixtures/index.json` missing or damaged, or one record unusable | GDTFs recovered from `fixtures/*.gdtf`, matched to the names the fixtures use (also names that were made safe for storing) | `project.gdtf-index-invalid` |
| a GDTF file missing / damaged | skipped (the fixture library must supply it) | `project.gdtf-missing` / `project.gdtf-corrupt` |
| fixtures use a GDTF the project does not hold | note | `project.gdtf-not-embedded` |
| thumbnail not a PNG | ignored | `project.thumbnail-invalid` |

Each kind of problem is listed up to `ProjectLoadOptions.MaxIssuesPerCode` times (default 50), then summarised once
(`project.more-issues`), so a file with thousands of broken elements still yields a readable list. Hostile archives are
contained: an entry larger than `MaxEntryBytes` (default ≈ 2 GiB), a total over `MaxTotalBytes` (16 GiB), or a declared
size impossible for its compressed size (deflate expands at most ≈ 1032:1) is refused before memory is allocated;
nothing is ever extracted to disk; stored names are only looked up, never used as paths. With
`VerifyChecksums` (default on) damaged files are detected by SHA-256 before they reach a decoder.

Loading builds a new `ProjectData` and `AssetStore`, so it can run on a background thread; the result is handed to the
main thread. Assets are not decoded during loading (models and images decode lazily in the `AssetStore`).

## 4. Versions and migrations

`ProjectFile.FormatVersion` (currently **1**) covers the archive layout and its JSON documents.

* **Newer file** (`formatVersion` greater than this program's): refused with `ProjectVersionException`
  (`IsNewer = true`, `FileFormatVersion`, `WrittenBy`), e.g. *"This project was saved by Candela 2.3.0 in project
  format 4. This version of Candela (1.0.0) reads project format 1 and older; update Candela to open it."* An older
  program cannot know what a newer file means, so it never guesses. `ReadManifest` still works, so the Open dialog can
  show what the file is. A `scene.json` with a newer scene `formatVersion` is refused the same way.
* **Older file**: converted step by step by the `ProjectMigrationRegistry` (`ProjectLoadOptions.Migrations`, default
  `ProjectMigrationRegistry.Default`). A step converts version *v* to *v + 1* on a `ProjectMigrationContext`: the
  manifest, `scene.json`, `settings.json` and both indexes as mutable `JsonNode`s, read access to every archive entry,
  `ReplaceEntry` for files that moved, and `Note` for messages to the user. Each applied step and note becomes a
  `project.migrated` note. A file for which a step is missing is refused with a message naming the versions.
* **Changing the format**: increment `ProjectFile.FormatVersion`, register the step from the previous version in
  `ProjectMigrationRegistry.CreateBuiltIn`, add a test with a file of the old version. Format 1 is the first published
  format, so there are no built-in steps yet. New settings sections, new component types and new asset kinds need no
  version change: older programs keep them verbatim and write them back (`project.scene-component-unknown`,
  `project.asset-kept`). New optional **properties** of existing components, entities or layers also need no version
  change, but an older program that saves the file drops them (Core's data classes have no place for unknown
  properties); a property whose loss would matter needs a version step.

## 5. Import (merge another project)

*File → Import → Project* inserts another project into the open scene under a **new group entity**
(`GroupComponent`, named after the imported scene or file; `ProjectMergeOptions.GroupName`, `ParentId`, `Placement`):

* **Every imported entity gets a new id**, so a project can be merged into itself, or the same file imported twice,
  without collisions. Cable node ids are renewed as well.
* **All references are translated** to the new ids: `FixtureComponent.MountedOn`, cable node anchors
  (`CableAnchor.EntityId`), cable endpoints (`CableEndpoint.EntityId`, `NodeId`), cable segments,
  `RiggingPointComponent.Carries`, asset ids of `MeshComponent` / `ImageComponent` / `TrussComponent`, and `asset:{id}`
  texture keys used as material ids (`MeshComponent.MaterialOverrides`, `WallComponent.MaterialId`). A reference to
  something that does not exist in the imported project is cleared and reported (`project.merge-reference-dangling`).
  World-space points (`FixtureComponent.FocusPoint`, `CameraComponent.Target`) are moved with the group's placement.
* **Assets**: an imported asset identical to one already in the project (same content hash, kind and import options)
  is reused (`ReuseIdenticalAssets`, default on); otherwise it is added — with its own id, or a new id when that id is
  taken.
* **GDTF files**: a name the project does not use is added; the same name with identical bytes is shared; the same name
  with **different** bytes (in the project, or in the fixture library when `TargetGdtfProvider` is given) is renamed
  `Name (2).gdtf` and the imported fixtures are pointed to it (`project.merge-gdtf-renamed`) — including fixtures whose
  name differs from the stored copy's only in case, which would otherwise bind to this project's different file.
* **Layers** are matched by id, then by name (`MergeLayersByName`); missing ones are added (with their properties of
  newer versions, like the entities).
* Console fixture ids are kept as authored; `RenumberFixtureIds` moves colliding ones above the highest id in use.
  Overlapping DMX addresses are left to the patch checks.
* The copy is made by Core's `EntityCloner` (`ExternalReferenceHandling.Clear`, the merge's layer and asset tables as
  `LayerMap` / `AssetIdMap`), which copies entities, flags, transforms, components and properties of newer versions
  exactly (a null that differs from the default, such as a console without protocol input, stays null) and rewrites
  the entity references; the merge completes each copy (`ComponentCopied`) with what Core does not know — GDTF file
  names, console fixture ids, world-space points moved by the placement, asset texture keys, asset ids that point
  nowhere, cable segments and ends whose node is missing — and reports every removed reference by name.
* Components of unknown types and assets this version cannot load (kept verbatim in the imported project) are not
  imported — their contents may refer to ids that cannot be translated; the result says so
  (`project.scene-component-unknown`, `project.asset-kept`).
* The imported project's settings sections, scene settings and thumbnail are not merged. Its load issues are part of
  the result, with entity ids translated.

`ProjectMerge.CreateCommand` returns a `ProjectImportCommand` (`IUndoableCommand`): the imported tree is built
detached up front (no staging scene); `Do` adds layers (the layer collection raises `LayersChanged` itself), assets,
GDTF copies and the group in one scene change; `Undo` removes exactly what was added. Its `Description` ("Import
&lt;group&gt;") is fixed when the command is made, so renaming the group later does not change the undo history. `ProjectMergeResult` lists the id maps (`EntityIds`, `AssetIds`, `GdtfNames`), what was added and the issues.

## 6. Autosave and recovery

```csharp
// start-up
AutosaveService? autosave = null;
foreach (var session in AutosaveService.FindRecoverable())          // crashed sessions, newest first
{
    // session.IsOlderThanOriginal: the file was saved after this autosave; recovering would bring back an older state.
    if (AskUser(session))
    {
        var project = session.Load();
        autosave = AutosaveService.ForProject(project, saveOptions, new AutosaveOptions { Resume = session });
    }
    else if (UserDiscards(session)) session.Delete();               // "later": keep it, it is offered again next time
}

// while a project is open
autosave ??= AutosaveService.ForProject(project, saveOptions,
    new AutosaveOptions { Interval = TimeSpan.FromMinutes(appSettings.AutosaveMinutes) });
autosave.Failed += e => issues.Report(…);
// every frame, on the main thread:
autosave.Update();
// after File → Save / Save as succeeded:
autosave.MarkSaved();

// project closed normally / clean exit
autosave.CompleteSession();
```

* **Files** live in `ConfigPaths.CacheDir/autosave/` (`AutosaveService.DefaultDirectory`): the newest autosave is
  `{key}.candela`, older ones `{key}.1.candela`, … (`KeepCount`, default 3, rotated just before the new file replaces
  the newest); `{key}.session.json` records the scene name, the file the project belongs to (`OriginalPath`), the
  application version and times. The key is the project id. Every file is written atomically, so a crash during an
  autosave never damages the previous one.
* **Threading**: there is no timer thread. `Update()` (cheap and allocation-free while nothing is due) checks the
  interval; when it has elapsed it calls the snapshot callback **on the main thread** — the only step that touches the
  live scene — and writes the `ProjectSnapshot` **on a background task** with fast compression. `Saved` / `Failed` are
  raised by a later `Update()` on the main thread. `SaveNowAsync()` captures immediately; `FlushAsync()` waits for the
  running write. The general form takes any callback (`new AutosaveService(projectId, () => snapshot-or-null)`;
  null = nothing new); `ForProject` captures only when the scene, the settings or the assets changed since the last
  capture, and a failed write keeps the project marked as changed so the next interval retries.
* **Sessions**: `{key}.lock` is held open exclusively while a session runs (a sharing lock on Windows, `flock` on Unix),
  so `FindRecoverable` never offers a session that is still running in another instance. A new session takes the key
  `{project id}`, or `{project id}~2`, `~3`, … when that key is running in another instance **or holds autosaves of an
  ended session that were neither recovered nor discarded**: a new session never overwrites, rotates away or deletes
  another session's crash data — even when the user opens the project file before answering the recovery offer.
  `CompleteSession()` (clean exit, or the project closed normally) deletes the session's files; `Dispose()` only stops
  the service and keeps them, so an exception unwinding through a `using` never destroys recovery data.
* **After saving**: `MarkSaved()` deletes the session's autosaves (the saved file holds everything they hold; a write
  that is running finishes first) and restarts change tracking, so a crash right after a save never offers an older
  autosave.
* **Recovery**: `RecoverableSession.Load()` opens the newest readable autosave (falling back to older ones with a
  `project.recovered-older` warning) and sets `FilePath` to the original file (null for a never-saved project) so
  *Save* writes back to the user's file. Starting the new service with `AutosaveOptions.Resume = session` continues the
  recovered session's files (rotated by new autosaves, deleted by `CompleteSession()`); without it they stay
  recoverable until `Delete()`. `IsOlderThanOriginal` / `OriginalModifiedUtc` tell when the project file was saved
  after the newest autosave (the program ended before `MarkSaved`, or another instance saved it), so the app can offer
  the file instead of an older recovery.

## 7. Tests

`tests/Candela.Project.Tests` (≈ 80 tests, about ten seconds) uses two real GDTF files of the repository and
`stages/Фокина.mvr`:

* **Round trip**: a scene with every Core component type (non-default values everywhere), a model with MTL and texture
  companions and custom import options, an image, a texture and a GDTF asset, two GDTF copies, three settings sections
  and a thumbnail → save → load → equal (scene JSON, asset bytes/ids/options, GDTF bytes, settings, manifest); the
  model decodes from the loaded bytes; re-saving reproduces every entry byte for byte; background save from a
  snapshot; stream save/load; odd file names, GDTF names with separators and case variants, non-finite numbers, odd
  settings names; a null that differs from the default (console without protocol); source paths left out on request.
* **Atomic save**: a GDTF provider that throws halfway leaves the previous file byte-identical with no temporary files;
  a failed first save creates nothing; saving through a symbolic link updates the linked file; permissions are kept;
  a read-only project is never overwritten; a 240-byte file name saves.
* **Tolerant loading**: corrupted asset (checksum), undecodable deflate data, missing asset, damaged companion,
  damaged/missing indexes (recovery from folders), invalid records (an asset whose record lost its id is recovered
  from its folder), corrupted GDTF, damaged settings and thumbnail, damaged entities/components/transforms, single
  unreadable values reset without losing their component, unknown component types and asset kinds kept and written
  back, explicit nulls, parent cycles, a 25 000-level parent chain loaded on a 1 MB stack, invalid UTF-8 and unpaired
  surrogate escapes in every JSON document, capped issue lists, size limits, not-a-zip / truncated / no scene / other
  format refused with clear messages.
* **Fuzzing** (deterministic): thousands of byte-level and structural damages to the JSON documents and to the archive
  bytes; loading either succeeds or throws the documented `ProjectFormatException`, and whatever loads saves and loads
  again to the same scene.
* **Versions**: future project and scene format refused with a clear message; older format refused without a
  migration and converted with one (scene file moved, fields renamed, settings restructured, notes reported).
* **Merge**: a project merged into itself (every reference remapped into the copy, no id collisions, saves and loads),
  repeated imports, asset id and GDTF name collisions (renamed, fixtures re-pointed, also case-variant names), library
  collisions, dangling references, undo/redo, placement under a parent moving world-space points, fixture id
  renumbering, import from a file with translated load issues, unknown components and kept assets reported and not
  imported, and a guard that fails when Core adds a component type the merge has not reviewed.
* **Autosave**: interval, unchanged project not written, rotation to the last N, live sessions hidden, recovery after a
  crash (`Dispose` without `CompleteSession`), fallback to an older autosave, clean exit deleting files, second
  instance session, crash data of an ended session never overwritten or deleted by a new session, resuming a recovered
  session, `MarkSaved`, recovery older than the saved file flagged, failures reported on the main thread and retried,
  callback semantics.
* **Real scene**: `Фокина.mvr` (91 fixtures) imported with the fixture library, saved with copies of its four library
  GDTFs, loaded without the library, merged into itself (183 entities) and saved again.
