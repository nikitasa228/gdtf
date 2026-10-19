# Candela.Mvr — MVR scene import and export

`Candela.Mvr` reads and writes MVR files (My Virtual Rig, DIN SPEC 15801, versions 1.4, 1.5 and 1.6). MVR is the main
way scenes arrive from other tools: `stages/Фокина.mvr` comes from Depence. It is also how scenes go out to consoles
and visualisers. The module depends on `Candela.Core` (scene graph, components, issues) and `Candela.Gdtf` (embedded
GDTF parsing, the fixture library, 3DS/glTF decoding), plus SharpGLTF for writing binary glTF. Units follow
[ARCHITECTURE.md](ARCHITECTURE.md): metres, right-handed, Z up, CIE 1931 xyY colour.

## Entry points

| Type | Purpose |
|---|---|
| `MvrReader` | `.mvr` (zip) → `MvrDocument`, from a path, a stream or bytes. `ReadSceneDescription` reads a bare `GeneralSceneDescription.xml`. It throws `MvrFormatException` only when there is no scene description at all; every other problem becomes an issue in `MvrDocument.Issues`. |
| `MvrDocument` | The faithful object model of the file (listed below) plus the embedded files. `AllObjects()` walks every layer depth first. `FindFile(reference)` resolves a `GDTFSpec` or `fileName` the way MVR files write them. |
| `MvrImporter` | `Import` / `ImportFile` build a new Core `Scene`. `Merge` / `MergeFile` add the file to an existing scene under a new group entity. Both return an `MvrImportResult`. |
| `MvrImportResult` | `Scene`, `Group` (merge only), `RootEntities`, `Assets` (`ImportedAsset`: id, file name, bytes, kind `Model`, format), `Gdtfs` (`MvrGdtfReference` per distinct `GDTFSpec`), `Issues`, the `Document`, and `FindEntity(mvrUuid)`. |
| `MvrWriter` | `Write` / `WriteFile` export a Core `Scene` as MVR 1.6. `BuildDocument` produces the document without writing it. `WriteDocument` writes any `MvrDocument`, for example one that was read, edited and saved back. |
| `MvrExportOptions` | GDTF callback (`GdtfProvider`, with `GdtfFromLibrary(library, folders…)` as a ready-made one), model callback (`ModelProvider: Func<Guid, ModelData?>`), provider name and version, and wall export. |
| `MvrMatrix` | MVR matrix strings to and from `System.Numerics.Matrix4x4`, and `Decompose` into a Core `Transform`. |
| `MvrModelFiles` / `GlbWriter` | 3DS, glTF and GLB from an archive → Core `ModelData` (metres, Z up). Core `ModelData` → GLB. |
| `MvrIssueCodes` | Stable issue codes, so the issues panel can filter and silence them. |

## What is read

Element and attribute names are matched ignoring case and XML namespaces. Elements the reader does not know are
reported. Where they sit directly in an object, a geometry entry, a layer, `AUXData` or a `ChildList`, they are kept
verbatim (`UnknownElements` on the owner) and written back by `WriteDocument`. Depence's `<FixtureTypeId>` is an
example: it is not part of MVR. Unknown children of the other containers (`Addresses`, `Protocols`, `Mappings`, …) and
unknown attributes are dropped; the note says which.

Uuids are read in any .NET text form and also as 16 space-separated hex byte pairs (`4A B1 94 62 …`), as older
exporters write them and as the DIN SPEC 15801 example of `Focus` and `Position` shows.

* **Document:** `verMajor`, `verMinor`, `provider`, `providerVersion`, and `UserData/Data` blocks (kept as XML).
* **AUXData:**
  * `Class` and `Position` definitions;
  * `Symdef` with its `ChildList` of `Geometry3D` and `Symbol`. Symbols may nest, and a bare `Geometry3D` directly
    under `Symdef` is tolerated;
  * `MappingDefinition`: SizeX, SizeY, Source, and ScaleHandeling (the spec's spelling; ScaleHandling is accepted too).
* **Layers:** uuid, name, optional `Matrix`, and `ChildList`.
* **Objects:** `SceneObject`, `GroupObject`, `FocusPoint`, `Fixture`, `Truss`, `Support`, `VideoScreen` and `Projector`.
  Each has uuid, name, `Matrix` and `Classing`. Groups have a nested `ChildList`, and so do all device objects.
  Device objects (every kind except groups and focus points) also carry:
  * `Geometries` (`Geometry3D` with `fileName`, and `Symbol` with `symdef`, each with its own `Matrix`);
  * `GDTFSpec`, `GDTFMode`, `CastShadow`, `Position`, `Function`, `FixtureID`, `FixtureIDNumeric`, `UnitNumber` and
    `ChildPosition`;
  * `Addresses`: each `Address` (break, absolute or `universe.address` value, raw text kept) and `Network`. An address
    of `0` means "not patched": no address is created, and one note counts them;
  * `Protocols`, `Alignments`, `CustomCommands`, `Overwrites`, `Connections`, `CustomIdType`, `CustomId` and
    `multipatch`.

  Some elements belong to one kind only:
  * Fixture: `Focus`, `DMXInvertPan`, `DMXInvertTilt`, `Color` (CIE xyY), `Mappings` and `Gobo` (with rotation);
  * Support: `ChainLength` (millimetres in the file, metres in the model);
  * VideoScreen: `Sources`;
  * Projector: `Projections`.
* **Embedded files:** every archive entry except the scene description, in archive order, with its kind (GDTF, 3DS,
  glTF, GLB, image, other). If `GeneralSceneDescription.xml` sits in a folder rather than at the archive root,
  references are resolved from that folder, and a note is reported. A `Geometry3D fileName` without extension is a
  3DS file, as the spec says: `truss` finds `truss.3ds`, and an entry named just `truss` is decoded as 3DS.

## Coordinates and matrices

MVR writes a transform as `{ux,uy,uz}{vx,vy,vz}{wx,wy,wz}{ox,oy,oz}`. The first three groups are the images of the
local X, Y and Z axes. The last group is the origin, in **millimetres**. A local point maps to `p·u + p·v + p·w + o`
(componentwise: `px·u + py·v + pz·w + o`). That is exactly the System.Numerics row-vector convention, so:

* u, v and w become rows 1–3 of a `Matrix4x4`;
* the origin, divided by 1000, becomes row 4 (`M41`…`M43`), in **metres**;
* children are relative to their parent and compose as `world = local × parentWorld`, the same as Core entities.

MVR, GDTF and Candela all use right-handed Z up, so no axes are swapped. A layer `Matrix` is applied to the layer's root
objects.

The parser also accepts a 4×4 matrix written as four groups of four. If the last column is `0,0,0,1` it is read as rows.
If the last row is `0,0,0,1` it is read as the GDTF column layout. Any other text, a wrong number of values, or a
non-finite value (including numbers too large for single precision, such as `1e300`) is a `mvr.matrix-invalid`
warning, and identity is used instead. An empty `<Matrix/>` means identity
and is not reported.

### Decomposition into entity transforms

`MvrMatrix.Decompose` turns each object matrix into a Core `Transform` (position, rotation quaternion, scale):

* **Rotation.** The axes are normalised and re-orthogonalised with Gram–Schmidt. The local Z axis keeps its direction
  exactly, because it is the beam axis of GDTF fixtures (the beam points along −Z). The rotation is therefore exactly
  orthonormal with determinant +1, even when the file carries float noise.
* **Scale.** The scale is the length of each axis. Values within 1e-4 of 1 snap to exactly 1.
* **Shear.** Non-orthogonal axes (normalised dot product > 1e-3) lose their shear, because a TRS transform cannot
  hold it. This is reported as `mvr.matrix-sheared`.
* **Degenerate matrices.** A zero-length, non-finite or coplanar axis keeps only the translation. This is reported as
  `mvr.matrix-degenerate`.

#### Mirror matrices (determinant < 0)

Some tools write mirror matrices, typically for mirrored symbols or set pieces. They are **kept, not repaired**:

* the rotation stays proper (orthonormal, det = +1);
* the mirror becomes a **negative X scale**, so `u = −|u|·x`.

`Transform.ToMatrix()` then reproduces the file's matrix exactly. Children keep their world positions, and the local Z
(beam) axis keeps its direction. The importer reports a `mvr.matrix-mirrored` note listing the affected entities. The
exporter writes the negative scale back, so the file's determinant sign survives a round trip.

Renderers must flip the triangle winding for entities with an odd number of negative scale factors. Depence writes
only proper rotations: none of the 91 matrices in `stages/Фокина.mvr` mirrors. The synthetic test file contains a
mirrored object to cover this path.

Writing is the inverse. Axes use the shortest round-trip float text. The origin is the metre value shifted by three
decimal places, which gives exact millimetres with no binary noise. Read → write → read therefore returns the
identical float matrix.

## Import: MVR → Core

| MVR | Core |
|---|---|
| Layer | `Layer`, with the MVR uuid as id. In a merge, layers match the target scene by uuid, then by name; unmatched ones are added. |
| GroupObject | `Entity` + `GroupComponent`; children nested |
| Fixture | `Entity` + `FixtureComponent` (see below) |
| Truss | `Entity` + `TrussComponent` (kind Truss, `ModelAssetId`, `Length` = model bounds along X) |
| Support | `Entity` + `RiggingPointComponent` (+ `MeshComponent`), with `Carries` listing child trusses |
| SceneObject, VideoScreen, Projector | `Entity` + `MeshComponent` referencing an imported model asset |
| any device object with a `GDTFSpec` (MVR 1.5+) | an additional `FixtureComponent` |
| FocusPoint | not an entity: its world position becomes `FixtureComponent.FocusPoint` of the fixtures that use it |
| Connection (1.6) | a cable entity with a `CableComponent`: two nodes anchored at the devices, one segment, and endpoints with the port names (`own`/`other`) |

Every entity keeps the MVR uuid as its id and the MVR hierarchy as its parent chain. A uuid that repeats in the file,
or that already exists in the target scene during a merge, is replaced by a deterministic new id, reported as
`mvr.id-remapped`. Use `FindEntity(mvrUuid)` to map back.

**Fixtures.** The fields map as follows:

* `GDTFSpec` → `GdtfSpec`, set to the resolved file name (next section);
* `GDTFMode` → `GdtfMode`, set to the resolved mode;
* `FixtureIDNumeric` → `FixtureId`, falling back to a numeric `FixtureID`;
* `UnitNumber` → `UnitNumber`;
* `CustomId` → `CustomId`;
* `Color` → `Color` (`CieColor` x, y, Y);
* `CastShadow` → `CastShadow`, default true;
* a fixture on a Truss or Support gets `MountedOn` set to it.

**Addresses.** Each address becomes a `DmxAddress`. For an absolute value `v`:

* universe = `(v − 1) / 512 + 1`;
* address = `(v − 1) % 512 + 1`.

The `universe.address` notation is accepted as well. A break must be unique per object; when one repeats, the first
address is used and the others are reported (the document keeps them all). MVR breaks are 0-based (`break="0"` patches GDTF `DMXBreak` 1)
and Core breaks are 1-based, so MVR break `b` becomes `Break = b + 1`. Example: the Фокина probe fixture has
`<Address break="0">741</Address>`, which becomes `DmxAddress(2, 229, 1)`.

**Geometry.** Symbols are expanded through their `Symdef`s, recursively, so nested symdefs work. Recursion is cut at a
self-reference (`mvr.symdef-cycle`) or at a depth of 32. Nested symbols multiply (ten symbols of a symdef with ten
symbols …), so one import expands at most 250 000 symbols and geometry instances; beyond that the rest is skipped and
reported once (`mvr.geometry-limit`). Then:

* when an object has exactly one geometry at its own origin, the mesh goes on the entity itself;
* when it has several geometries, or one with an offset, they become child entities. Each child holds one
  `MeshComponent`, is named after the symdef (or the file), and has a deterministic id derived from the parent's id
  and its index;
* for a truss, the first geometry at the origin is the truss model.

Model files are imported once each, as `ImportedAsset`s with content-addressed ids (the same file gives the same id
on every import). References that spell one archive file differently (`Box.glb`, `./box.glb`, `truss` for
`truss.3ds`) share its asset. They are decoded when `ValidateGeometry` is set (the default):

* 3DS is in millimetres and scaled to metres;
* glTF/GLB is Y up and rotated to Z up, `(x, y, z) → (x, −z, y)`;
* glTF buffers and images are taken from the archive.

A file that is missing, in an unsupported format or undecodable leaves its object without that geometry and is
reported.

## GDTF resolution and stub GDTFs

MVR files must embed their GDTFs, but some exporters embed **stubs**. Depence, for example, writes 1.5–2.3 KB GDTFs
with one dimmer channel and no geometry. For each distinct `GDTFSpec` the importer resolves a GDTF once, in this order:

1. **Complete embedded GDTF.** Used as is (`MvrGdtfSource.Embedded`). A GDTF counts as complete when it has a DMX mode
   with channels *and* a geometry with a drawable model: either a primitive, or a model file that exists in the
   archive.
2. **Fixture library**, when the embedded file is incomplete, unreadable or missing (`MvrGdtfSource.Library`).
   Candidates come from:
   * the stub's `FixtureTypeID`;
   * `FixtureLibrary.FindBySpec(GDTFSpec)`, which parses `Manufacturer@Model[@Revision].gdtf` tolerating spaces, case,
     underscores and punctuation, so `Ayrton @Rivale Profile.gdtf` works;
   * the stub's own Manufacturer, Name and LongName.

   An exact model beats a substitute, then the higher name score wins. A stub replaced by the exact model gets a
   `mvr.gdtf-stub` note. A same-brand **substitute** (the model is not in the library) is a different fixture, so it
   only replaces a missing or unreadable file or a **placeholder stub**: no drawable geometry and at most one DMX
   channel per mode, as Depence writes them (a placeholder without any channel counts only when its objects are
   patched). It is reported as a `mvr.gdtf-substitute` warning that names the stand-in.
3. **The embedded file itself** (`MvrGdtfSource.EmbeddedStub`):
   * an incomplete GDTF that is not a placeholder (a real DMX layout without a drawable model, or an unpatched passive
     device without channels such as a bracket) is kept when the library has no exact model, because a substitute
     would change the device and its patch. This is a `mvr.gdtf-no-geometry` warning. 17 of the 1091 library files
     are such GDTFs (for example `Brighter@Smart_Hybrid_280_Pro`, whose geometries link no model);
   * a placeholder the library has nothing for is a `mvr.gdtf-unresolved` warning.
4. **Nothing** (`MvrGdtfSource.Unresolved`), reported as a `mvr.gdtf-unresolved` error. The fixture is still created,
   placed and patched, and it keeps the spec and mode as written, so adding the GDTF later resolves it.

`MvrImportResult.Gdtfs` reports every spec: the source, the file name (stored in `FixtureComponent.GdtfSpec`), the
library entry and path, match kind and score, the embedded bytes and whether they were a stub, the parsed
`FixtureType`, the object count, and the mode resolutions. `ReadBytes()` returns the bytes of the GDTF in use.

**Modes.** The requested `GDTFMode` is looked up exactly, then ignoring case, spaces and punctuation. If the GDTF lacks
it, the closest mode is chosen by name similarity (weight 0.4) and by how well its first-break footprint matches the
**patch spacing** (weight 0.6). The patch spacing here is the most frequent gap between consecutive start addresses of
fixtures of that spec and mode in one universe. The choice is reported as `mvr.mode-unknown`.

A **substitute** (a similar model standing in for one the library lacks) is treated differently, because the requested
mode name belongs to the missing model: a stand-in mode of that name may be far wider than the real one. When the
fixtures of the spec and mode are patched at least two to a universe, the stand-in takes the requested mode only if its
first-break footprint fits the spacing — here the *smallest* gap between two start addresses in one universe, the most
channels the real mode can use without overlapping — and otherwise its most complete mode that fits (largest
footprint; GDTF order breaks ties), or its smallest mode when none fits. A replaced mode of the requested name is a
`mvr.substitute-mode` note, a missing one a `mvr.mode-unknown` warning that names the spacing. The rule lives in
Candela.Gdtf (`FixtureType.FitModeToSpacing`, `DmxMode.FirstBreakFootprint`, `DmxPatchSpacing.Infer`), which both this
importer and the DMX engine (for stand-ins it resolves itself) use; `SubstituteModeTests` checks that the importer's
loose name match and the engine's `FindMode` pick the same mode for every library fixture type, mode name and a range of
spacings. Without a
spacing (one fixture per universe) the mode is matched by name as above.

When a mode still needs more channels than the patch spacing leaves (a closest mode, or a stand-in none of whose modes
fits), the fixtures' DMX ranges overlap. This is reported as `mvr.mode-footprint`.

### `stages/Фокина.mvr`

The file is a Depence export (MVR 1.4) with 91 fixtures on two layers. It holds **five** distinct `GDTFSpec` values,
each in one DMX mode, all embedded as stubs. Four of the five models are not in the repository's library, so they get
substitutes:

| GDTFSpec (objects) | Resolved library file | Match | Mode |
|---|---|---|---|
| `Ayrton@MagicBlade R.gdtf` (28) | `Ayrton@MagicPanel_602@V2.5_Add_Mode.gdtf` | substitute | Extended → Standard (20 ch, patch spacing 44; the panel's own Extended has 160) |
| `Ayrton@Zonda 3 Wash.gdtf` (28) | `Ayrton@Zonda_3_FX@V1.81_Corrected_Factory_Tilt.gdtf` | substitute | Basic → Basic (26 ch, spacing 26) |
| `Ayrton @Rivale Profile.gdtf` (27) | `Ayrton@Rivale_Wash@V1.8_Correct_Gobo_Value.gdtf` | substitute | Standard → Standard_540 (38 ch, spacing 42) |
| `Martin@Atomic 3000 LED.gdtf` (4) | `Martin_Professional@VDO_Atomic_Bold@20260620.gdtf` | substitute | Mode 1 → 3 Channel (3 ch, spacing 3) |
| `Ayrton@Zonda 3 FX.gdtf` (4) | `Ayrton@Zonda_3_FX@V1.81_Corrected_Factory_Tilt.gdtf` | same model | Basic → Basic (26 ch, spacing 26) |

Every fixture ends up with a library GDTF that has real geometry and DMX modes. The import reports:

* four `mvr.gdtf-substitute` warnings;
* one `mvr.gdtf-stub` note;
* two `mvr.mode-unknown` warnings (Atomic `Mode 1`, Rivale `Standard`), each naming the stand-in mode that fits the
  patch spacing;
* one `mvr.substitute-mode` note (MagicBlade R: the MagicPanel 602's 160-channel `Extended` would overlap the
  44-channel patch, so its 20-channel `Standard` is used);
* one note for Depence's unknown `<FixtureTypeId>` element.

There are no import errors and no stand-in overlaps the patch (before the importer chose stand-in modes by spacing, the
MagicPanel's `Extended` caused 73 `patch.overlap` and 4 `patch.out-of-range` errors). The substitutes are still not
good enough for the show: their bodies, optics and photometry are the stand-ins'. The engine (`StageEngine.Open`/
`Import`, docs/engine.md) records the stand-ins, so the patch reports one `patch.substitute-gdtf` warning per type and
no patch errors (`candela validate` exits 0), and switches them to the real GDTFs once the library has them.

#### What the scene expects, and where the real GDTFs are

Depence wrote the patch for the manufacturers' real DMX modes: the most frequent address spacing of every type is
exactly the documented footprint of the mode it asks for, and with those footprints the 91 fixtures are free of
overlaps and out-of-range addresses (`FokinaLibraryTests` checks the spacing against the table below).

| Model (objects) | `GDTFMode` | Footprint (manufacturer document) | Official GDTF (state on 2026-10-18) |
|---|---|---|---|
| Ayrton MagicBlade-R (28) | `Extended` | 44 ch (DMX chart V1.0: Basic 18, Standard 20, Extended 44) | GDTF-Share fixture 5377 "MagicBlade R", revision `V2.73 Fix_3D_Vectorwork` (2026-03-18), modes `Extended`, `Standard`, `Basic` |
| Ayrton Zonda 3 Wash (28) | `Basic` | 26 ch (DMX chart V130: Basic 26, Standard 37, Extended Ring 46, Extended Pixel 79) | GDTF-Share fixture 9750 "Zonda 3 WASH", revision `V1.7 Add_RDM_Personality` (2026-07-07), modes `Basic`, `Standard`, `Extended Ring`, `Extended Pixel` |
| Ayrton Rivale Profile (27) | `Standard` | 42 ch (DMX chart V122: Standard 42, Extended 65) | GDTF-Share fixture 10439 "Rivale Profile", revision `V1.8 Correct_Gobo_Value` (2026-09-23), modes `Standard_540`, `Extended_540` |
| Martin Atomic 3000 LED (4) | `Mode 1` | 3 ch (user manual rev. C: 3-channel, 4-channel and 14-channel Extended modes) | none published: Martin's GDTF-Share account (linked from martin.com) has the VDO Atomic Bold/Dot but no Atomic 3000 LED |
| Ayrton Zonda 3 FX (4) | `Basic` | 26 ch | in the library (`Ayrton@Zonda_3_FX@V1.81_Corrected_Factory_Tilt.gdtf`) |

Ayrton's product pages and support area (ayrton.eu, "Support" downloads) link every GDTF to the manufacturer's
GDTF-Share page (`gdtf-share.com/userPage.php?name=AyrtonDigitalLighting&page=Revisions&fixtureId=…`) and host no GDTF
files themselves; martin.com links its "GDTF library" to GDTF-Share as well. GDTF-Share shows the revision lists
publicly, but downloading a file needs an account login or a solved CAPTCHA, so the files cannot be fetched
automatically and are not in the repository yet.

To add them, download the three Ayrton files from GDTF-Share (and a Martin Atomic 3000 LED GDTF once one is published),
keep GDTF-Share's file names (`Manufacturer@Fixture_Name@Revision.gdtf`, e.g.
`Ayrton@Rivale_Profile@V1.8_Correct_Gobo_Value.gdtf`) in the repository root, regenerate `_specs`
(`python3 -I _specs/tools/extract_gdtf.py . _specs`, then `python3 -I _specs/tools/validate.py
_specs/fixtures_index.json`) and check with `candela info stages/Фокина.mvr` and `candela validate stages/Фокина.mvr`.
No code change is needed: `FokinaLibraryTests` opens the scene against GDTFs with the real manufacturer names, mode
names and footprints and gets exact matches, no `mvr.mode-footprint` warning and no patch issue at all. Two
`mvr.mode-unknown` warnings remain because the GDTFs name modes differently from Depence: the Rivale Profile's
`Standard` is `Standard_540`, and the Atomic's `Mode 1` is its 3-channel mode. Both are picked correctly by name and
patch spacing. The engine and CLI tests adapt to the library on their own. `tests/Candela.Mvr.Tests/FokinaTests.cs` and
`tests/Candela.Gdtf.Tests/LibraryTests.cs` list today's substitutes explicitly and must be updated together with the
files.

## Merge mode

`Merge(target, document)` adds one root entity named after the file (or `MvrImportOptions.GroupName`) with a
`GroupComponent`, and imports everything below it:

* layers are matched by uuid, then by name, and missing layers are added (a target without any layer first gets a
  `Default` one for the group);
* repeated uuids are remapped (`mvr.id-remapped`), so the same file can be merged twice;
* `Merge` mutates the target scene, so it must run on the scene's owning thread. Read the document in the background
  first.

`Import` builds a scene that nobody else sees yet, so it can run on any thread.

## Export: Core → MVR 1.6

`MvrWriter.Write(scene, stream, options)` writes `verMajor="1" verMinor="6" provider="Candela"` (and `providerVersion`).

* **Layers.** Scene layers become MVR layers, keeping their ids and names. A layer's transform is baked into its
  objects, and layers are written without a `Matrix`.
* **Entity hierarchy.** Entities become objects with their id as uuid, their name, and their local matrix in
  millimetres. The kind is chosen in this order:

  | Entity has | Written as |
  |---|---|
  | `TrussComponent` | `Truss` |
  | `RiggingPointComponent` | `Support` |
  | `MeshComponent` or `WallComponent` | `SceneObject` |
  | `FixtureComponent` | `Fixture` |
  | `GroupComponent` | `GroupObject` |
  | no component | `SceneObject` |

  A device that carries a `FixtureComponent` gets its `GDTFSpec` and patch.
* **Fixtures.** `GDTFSpec`, `GDTFMode`, `Addresses` (absolute value, `break = Core break − 1`), `FixtureID` and
  `FixtureIDNumeric`, `UnitNumber` (when numeric), `CustomId`, `Color`, `CastShadow` and `Focus` are written. A
  `FocusPoint` object with a deterministic uuid is created for each focused fixture. Out-of-range addresses and
  non-finite colours or focus points are reported and skipped.
* **Mandatory elements.** MVR 1.6 requires some elements whatever the scene holds, so they get neutral values:
  * `FixtureID` (empty) and `FixtureIDNumeric` (0) on every device object (`SceneObject`, `Fixture`, `Truss`,
    `Support`), `UnitNumber` 0 on fixtures, `ChainLength` 0 on supports, and `Geometries` (possibly empty) on every
    device object except fixtures; the importer reads `UnitNumber` 0 back as "no unit number";
  * `GDTFMode` whenever `GDTFSpec` is written: Core's `GdtfMode = null` means "the first mode", so the first mode's name
    is read from the embedded GDTF. Without the GDTF bytes the fixture is written without a mode and reported;
  * `Projections` on a `Projector` (document round trip), even when empty.
* **GDTF files.** Each distinct spec is requested once from `GdtfProvider(specName)` and embedded at the archive root.
  Characters MVR file names must not contain (FAT32/NTFS reserved characters and control characters) become `_` in
  the archive name and in `GDTFSpec`.
  A missing, unreadable or non-zip result is reported (`mvr.export-resource-missing`), and the file is then referenced
  without being embedded. `MvrExportOptions.GdtfFromLibrary(library, folders…)` reads library files by name.
* **Models.** Each `MeshComponent` or `TrussComponent` model is requested once from `ModelProvider(assetId)` and
  written as binary glTF (Y up, metres) with SharpGLTF. Part transforms are baked, there is one primitive per sub-mesh,
  and materials are kept as metallic-roughness factors. Objects reference it through `Geometry3D`. Walls, floors and
  ceilings become scene objects with a generated box (`ExportWalls`).
* **Geometry children.** Child entities that the importer created for extra geometry are folded back into `Geometry3D`
  entries of their parent, recognised by their derived ids. If such a child's name is not the stem of the file, it is
  written as a `Symbol` of a one-file `Symdef` named after it. Names and ids therefore survive import → export →
  import.
* **Connections.** A `CableComponent` whose endpoints are two exported devices becomes MVR 1.6 `Connection`s on the
  target device (`own` = its port, `other` = the source's port, `toObject` = the source). A cable with more endpoints
  connects every further endpoint to the first. Routes, cable types and labels are not part of MVR; a note says so.
* **Not representable.** Images, cameras, consoles, power sources, atmosphere volumes, material overrides, manual
  attribute values, procedural trusses without a model, and non-numeric unit numbers are listed in
  `MvrExportResult.Issues` rather than silently lost.

**Deterministic output.** Equal scenes give byte-identical archives: element order follows MVR 1.6, numbers use
invariant round-trip text, zip entries have a fixed timestamp and file order is stable. `WriteFile` writes to a
temporary file and replaces the target, so a failed export never leaves a truncated file.

**Document round trip.** `MvrWriter.WriteDocument(MvrReader.Read(x))` writes everything the reader kept:

* the unknown elements it kept (see "What is read") and `UserData`, without adding attributes the blocks did not have;
* class, position and mapping definitions;
* symdefs;
* all object attributes;
* every readable embedded file.

Writing the result again gives the same bytes.

## Robustness

* **Exceptions.** Content problems never throw. Each problem becomes an issue (`Core.Diagnostics.Issue` with a
  `mvr.*` code, severity, category and the entities involved), and the element is read as far as possible. Examples:
  * missing or invalid uuids get deterministic generated ids, so reading the same bytes twice gives the same ids;
  * repeated uuids are reported;
  * unknown tags are reported once per tag with a count;
  * unparsable numbers, booleans, colours, GUIDs or addresses fall back to defaults;
  * dangling focus, symdef and connection references are reported;
  * an unreadable zip entry, broken GDTF or undecodable model is reported.

  Only data that is not an MVR archive throws `MvrFormatException`: not a zip, no `GeneralSceneDescription.xml`,
  malformed XML, a wrong root element, or an unreadable scene description entry.
* **Containment.** Each object is built under its own guard during import, so one failure never loses the scene: a
  failed object is reported (`mvr.object-failed`) and its children are attached to its parent at their world place.
  Repetitive issues are aggregated, and issues are capped at 2000 per pass.
* **Hostile files.** Limits keep a broken or malicious file from exhausting the machine; each one that applies is
  reported:
  * XML nesting is limited to `MvrReader.MaxXmlDepth` (512) levels, about 250 levels of nested objects. The depth is
    measured in a streaming pass first; a deeper document is pruned while it is streamed (`mvr.nesting-too-deep`), so
    neither the recursive readers nor XDocument (which slows down more than quadratically with depth: 20 000 nested
    groups take it half a minute) see the excess;
  * embedded files are read up to 1 GiB each and 2 GiB in total, whatever sizes the zip directory declares;
  * symbol expansion stops after 250 000 instances (`mvr.geometry-limit`).
* **XML safety.** DTDs are ignored and external entities are never resolved.
* **Cost.** Lookups are dictionary based: uuids, files, symdefs, GDTF specs and modes. Each GDTF and model is parsed
  once per import. `stages/Фокина.mvr` reads, resolves and imports in well under a second, library scan included.
  The whole test suite takes a few seconds.

## Tests

`tests/Candela.Mvr.Tests` covers the following:

* **`FokinaTests`.** Reads every element of the Depence file, resolves all 91 fixtures to library GDTFs with the
  report per spec, and checks the probe fixture: uuid `B1E1000D-…`, at (4.485159, 16.74185, 1.760395) m, address
  741 → universe 2 address 229, with an orthonormal rotation; it stands upright on the stage (local Z points down,
  so its GDTF beam, along local −Z, points up). Every entity transform must
  reproduce its MVR matrix. The scene is exported and re-imported (same entities, positions within 1e-4 m, same
  patch, specs and modes, byte-identical repeated export), and merged twice into a venue scene.
* **`SubstituteModeTests`.** Fixtures of a model the library lacks (MagicBlade R → MagicPanel 602 stand-in) patched
  at various spacings: the stand-in's mode of the requested name is replaced when it overlaps (`mvr.substitute-mode`)
  and kept when it fits; an unknown mode name gets the widest fitting mode with a `mvr.mode-unknown` warning; when
  nothing fits, the smallest mode and `mvr.mode-footprint`; one fixture (no spacing) keeps the mode by name; universes
  do not make a spacing; and the importer's loose name match leads to the same stand-in mode as the DMX engine's lookup
  for every library fixture type, mode name and a range of spacings.
* **`SyntheticTests`.** A hand-written MVR 1.6 archive with every object type and nearly every attribute: nested
  groups, nested symdefs, a GLB and a 3DS model, a mirror matrix, a layer matrix, connections, and complete, stub and
  library-only GDTFs. The tests cover the reader, the importer mapping, the lossless and idempotent document round
  trip, and scene export → re-import.
* **`MalformedTests`.** Covers:
  * bad and NaN matrices;
  * degenerate and sheared axes;
  * missing, invalid and repeated uuids;
  * unknown tags and object types;
  * invalid values and addresses;
  * dangling focus, symdef and connection references;
  * a symdef cycle;
  * a broken GLB, a missing 3DS and an unsupported file type;
  * a non-zip embedded GDTF;
  * an archive entry with an unsupported compression method;
  * a description in a sub-folder and minimal or empty descriptions;
  * the archive errors that do throw;
  * an export with failing callbacks and invalid component values.
* **`ConformanceTests`.** DIN SPEC 15801 details and hostile input: `Geometry3D` names without extension, uuids as hex
  byte pairs, an address of 0, single-precision overflow in matrices, accurate notes for unknown elements, the
  elements MVR 1.6 makes mandatory on export, no invented `UserData` attributes, GDTFs without drawable models that
  must keep their DMX layout instead of a substitute, 20 000 nested groups, 10⁹ nested symbol instances and a glTF with
  an invalid URI.

## Known gaps

* **Data Core does not hold yet.** The following stay in the imported `MvrDocument` and are reported as
  `mvr.not-representable`, but are not written back by a *scene* export: `Classing`, `Position`, `Function`,
  `ChildPosition`, alignments, overwrites, custom commands, protocols, network settings, multipatch, `CustomIdType`,
  DMX inversion, video mappings, custom gobos, video sources and projections. A *document* round trip keeps them.
* **Kinds Core cannot tell apart.** VideoScreen and Projector import as mesh entities and export as `SceneObject`s.
  FocusPoints are not entities, and their geometry is not imported. Geometries on `Fixture` elements are ignored,
  because the body comes from the GDTF.
* **FixtureID.** `FixtureID` is text in MVR but an integer in Core. A non-numeric ID without `FixtureIDNumeric`
  imports as 0, with a note. `UnitNumber` 0 imports as "no unit number".
* **Unknown content.** Unknown attributes, and unknown elements inside containers such as `Addresses`, `Protocols` or
  `Mappings`, are reported but not kept, so a document round trip drops them. Values outside an enumeration (for
  example `ScaleHandeling` `UpScale`, which the spec's own example uses) are reported and dropped the same way.
* **Cables.** Connections carry ports only. Imported cables are straight two-node cables between the device origins.
* **Model export.** Textures are not embedded in exported GLBs (only material factors), glTF animations are ignored on
  import, and a truss's length is taken from its model bounds along X.
* **Not implemented.** MVR-xchange, the network protocol for live scene exchange that accompanies MVR 1.6. Elements
  of newer MVR versions are kept as unknown elements but not interpreted.
* **Substitutes.** A substitute GDTF is a best effort by name within the brand. The UI should show it as a substitute.
  Its mode fits the patch spacing, but its DMX layout is still the stand-in's, so a console's cues for the real model
  drive the wrong attributes. `mvr.mode-footprint` flags patches none of the substitute's modes fits.
