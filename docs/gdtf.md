# Candela.Gdtf — GDTF fixtures, models and the fixture library

`Candela.Gdtf` reads GDTF fixture files (DIN SPEC 15800, DataVersion 1.0, 1.1 and 1.2), loads their 3D models, and
provides the fixture library that the editor and MVR import use. Every downstream module builds on its object model:
DMX evaluation, the renderer, cabling and the library UI. Units follow GDTF and [ARCHITECTURE.md](ARCHITECTURE.md):
metres, degrees in the model (radians only inside maths), seconds, watts, lumen, candela, and CIE 1931 xyY colour.

## Entry points

| Type | Purpose |
|---|---|
| `GdtfArchive` | The `.gdtf` zip, from a path (lazy, or read into memory) or a stream. Gives `description.xml`, thumbnails, `wheels/*`, `models/*`. Entry lookup ignores case, disposable, safe to share between threads. |
| `GdtfReader` | `ReadFile`, `TryReadFile`, `Read(stream)`, `Read(archive)` and `ReadDescription` all produce a `FixtureType`. Throws `GdtfFormatException` only when there is no fixture type at all (not a zip, no `description.xml`, malformed XML). Every other problem becomes a `GdtfWarning` in `FixtureType.Warnings`. |
| `FixtureType` | The complete object model, listed below. Names as written are kept, and every reference is also resolved into an object link. |
| `DmxValue` | GDTF `"value/bytes"` values with exact resolution conversion. |
| `GdtfMatrix` | GDTF matrix strings to and from `System.Numerics.Matrix4x4`. |
| `GdtfModelLoader` | 3DS, glTF/GLB or procedural mesh → Core `ModelData`, in metres and Z up, fitted to the declared size. |
| `FixtureGeometryBuilder` | Flattens a mode's geometry tree into `FixturePart`s (name, parent index, local matrix, mesh), with GeometryReferences expanded. |
| `GdtfImageSource` | Core `IImageSource` for texture keys `gdtf:{fixture file}#{entry}`. |
| `FixtureLibrary` | Scans folders without parsing, parses on demand with a cache, groups by category, and finds fixtures by human names. |
| `FixtureTypeDiskCache` | Persistent binary cache of parsed fixture types under `ConfigPaths.CacheDir/gdtf`, used by `FixtureLibrary` (see below). |
| `FixtureSpecsIndex` / `FixtureSpecs` | Typed records of `_specs/fixtures_index.json` (see `_specs/SCHEMA.md`), indexed by GDTF file name. |
| `FixtureCategory` | PROFILE, WASH, WASH BEAM, BEAM, SPOT, FOLLOW SPOT, LED BAR, PAR, HAZE, FOG, FAN, SCANER, LASER, STROBE, BLINDER, UV, BSW, GENERATOR FIRE, OTHER. `DisplayName()` gives exactly these strings. |

## What is read

* **Fixture type:** Name, ShortName, LongName, Manufacturer, Description, FixtureTypeID, RefFT, Thumbnail (+ offsets),
  CanHaveChildren and DataVersion.
* **AttributeDefinitions:**
  * activation groups;
  * feature groups and features;
  * attributes with ActivationGroup, Feature, MainAttribute, PhysicalUnit, Color and the 1.2 SubPhysicalUnits.
* **Wheels:** slots with Name, Color (`CieColor`), Filter, MediaFileName (resolved to the archive entry), prism facets
  (colour and 2D transform) and AnimationSystem.
* **PhysicalDescriptions:**
  * emitters (Color, DominantWaveLength, DiodePart, spectral measurements);
  * filters with measurements;
  * the colour space (sRGB, ProPhoto, ANSI, Custom), with primary luminances derived so that white has Y = 1;
  * AdditionalColorSpaces, Gamuts, DMXProfiles and CRIs. `DmxProfile.Evaluate` follows the spec formula
    `CFC3·(x − P)³ + CFC2·(x − P)² + CFC1·(x − P) + CFC0` with the point at or below x (none: 0), x and P as fractions
    of the DMX range, so GDTF Builder's "Linear" profile (CFC1 = 1) is the identity. Its closing point at 100 %
    (CFC0 = 0) starts no segment;
  * legacy Connectors;
  * Properties: OperatingTemperature, Weight, LegHeight and PowerConsumption. PowerConsumption covers Value, PowerFactor,
    the connector link and the voltage and frequency ranges. The connector link resolves to a WiringObject or to a
    legacy `<Connector>`; the 14 library files that have a PowerConsumption all use the legacy form.
* **Models:** Name, Length/Width/Height (m), PrimitiveType, File and the SVG offsets.
* **Geometries:** every type, each with Name, Model and Position. The types are Geometry, Axis, FilterBeam, FilterColor,
  FilterGobo, FilterShaper, Beam, MediaServerLayer/Camera/Master, Display, GeometryReference (+ Breaks), Laser
  (+ Protocols), WiringObject (+ PinPatches), Inventory, Structure, Support and Magnet. Beam keeps all its photometric
  attributes; WiringObject and Support keep all their electrical and mechanical ones.
* **Beam defaults vs file values.** Every Beam attribute has a GDTF default (1000 W, 10 000 lm, 6000 K, 25°/25°,
  Wash, CRI 100 …). `BeamGeometry.Specified` (and `IsColorTemperatureSpecified`, `IsLuminousFluxSpecified`,
  `IsBeamAngleSpecified`, `IsFieldAngleSpecified`, `IsBeamTypeSpecified`, `IsPowerConsumptionSpecified`,
  `IsColorRenderingIndexSpecified`, `IsLampTypeSpecified`) tells which ones the file actually gives with a valid
  value; the properties always hold a usable value. All 12 239 beams of the library specify every attribute (GDTF
  Builder writes them all, defaults included), so a written default can still be the author's untouched default —
  `Candela.Dmx` therefore treats an unspecified value and a written default alike when `_specs` knows better.
* **DMXModes:**
  * channels (DMXBreak including "Overwrite", the Offset list or None, InitialFunction, Highlight, Geometry, and the
    GDTF 1.0 channel Default);
  * logical channels (Snap, Master, MibFade, DMXChangeTimeLimit);
  * channel functions (all 1.2 attributes; DMXTo is computed, taking ModeMaster ranges into account);
  * channel sets (with the resolved wheel slot) and sub channel sets;
  * relations and FTMacros.
* **Revisions, FTPresets and Protocols:** RDM has typed ids and personalities linked to modes. Art-Net, sACN, PSN, OSC
  and CITP blocks are kept generically.

After parsing, the reader resolves references to attributes, features, activation groups, wheels, slots, emitters,
filters, colour spaces, gamuts, DMX profiles, models, geometries, ModeMaster (a channel or a function node), relations,
macros, pin patches and RDM modes. Lookup tries the exact name first, then trimmed and case-insensitive, then the last
segment of a node path. A dangling reference stays `null` and produces a warning.

`DmxLayout` expands GeometryReferences into `DmxMode.ChannelInstances`, which carry absolute offsets per break and the
reference chain (so nested references work). It also fills `BreakFootprints` and `Footprint`, which lets the DMX
layer address every pixel. `FixtureGeometryBuilder` produces the matching parts, and `FixtureGeometry.FindPart(instance)`
links a channel instance to the part it moves. A channel that names a GeometryReference itself (per-layer controls of
the Prolights Eclipse XL, for example) maps to the top part of that instance.

### DMX values

The rule comes from the GDTF 1.2 spec, attribute type DMXValue:

* A value written with fewer bytes than its channel is **byte-mirrored** by default: `255/1` in a 16-bit channel is
  65535, and `128/1` is 32896.
* The `s` suffix means **byte shifting**: `255/1s` is 65280.
* Converting to fewer bytes always drops the low bytes.

(The task text that started this module described `s` as a mirroring suffix. The spec says the opposite, and the spec
is what is implemented.)

Overflowing values in real files are repaired with a warning. `256/1` is clamped to `255/1`. Sentinels such as
`4294967295/1`, which is −1 written unsigned, count as "no value".

`DmxValue.ToRangeEnd(bytes)` converts a value that **ends** a range: written with fewer bytes than the channel, it
covers its whole step, so ranges that meet in the file also meet in the channel. In a 16-bit channel `127/1` ends
at 0x807F (the mirrored `128/1` is 0x8080), `127/1s` at 0x7FFF and `255/1` at 0xFFFF. `ModeTo` uses it:
`ChannelFunction.ModeFromValue` / `ModeToValue` hold the mode range in the resolution of the resolved master channel
(a reversed range is swapped with a warning), and `IsModeActive(masterValue)` tests it. No library file writes a
`ModeTo` with fewer bytes than its master, but the DMX layer no longer leaves one-value gaps when one does.

### DMX ranges (DMXTo)

The spec: a function ends at the next function's DMXFrom − 1 (at the channel resolution), the last one at the
channel maximum; a channel set ends at the next set's DMXFrom − 1 or at the end of its function. With mode masters,
files list one function per master range, often all starting at 0, so "next" only counts functions that can be
active at the same time. Two functions are exclusive when their mode-master conditions cannot hold together:

* both depend on the same master **channel** (whether spelled as the channel or as one of its functions) with
  disjoint mode ranges, or name two different functions of it (a channel runs one function at a time);
* conditions are followed through master functions: a function mastered by a "CTC range" function that is itself
  mastered by a colour-mode channel is only active in that colour mode.

Verified on the library by computing every function and set range of all 1091 files and inspecting each change:
comparing masters by spelling cut the static-pattern selection of the BeamZ MHL 1940 (3 modes) at 23 instead of
255, and comparing them one level deep cut the blue channel of nine Prolights Eclipse fixtures (RGB mode) at 19 and
their colour temperature at 89. Without mode masters, functions and sets tile every channel exactly as the spec
says (tested on a sample of the library).

### Channel counts vs `_specs`

`DmxMode.DeclaredChannelCount` is the highest offset written on the channels of each break, summed over breaks. It
equals `_specs/fixtures_index.json` `dmxModes[].channels` for all 7272 modes of the 1091 files, with no mismatch.

`DmxMode.Footprint` is the real number of slots after instancing. It is larger in 1019 modes: every one of them
addresses pixels or heads through GeometryReference Breaks. The specs extractor reads the offsets as written on the
template channels, so it does not count those instances.

## Coordinate and axis conventions found

* **Geometry matrices.** GDTF writes `{a,b,c,d}{e,f,g,h}{i,j,k,l}{m,n,o,p}` for column vectors, with the translation in
  d, h and l. `GdtfMatrix` transposes this into System.Numerics row vectors, with the translation in M41..M43, so that
  `world = local * parentWorld`.
* **Checked on real files.** In the Robe MegaPointe the yoke sits 0.087 m below the base, and the head another 0.284 m
  below the yoke. GDTF models fixtures hanging, with the beam leaving a Beam geometry along its local −Z.
* **glTF/GLB (1085 models).** These are Y-up, as glTF requires. Converting (x, y, z) → (x, −z, y) makes the file extents
  equal the declared Length (X), Width (Y) and Height (Z): 766 models match within 2 % with no scaling at all, and lens
  faces end up pointing −Z.
* **3DS (1743 models).** These are Z-up like GDTF, but their units vary despite the spec: millimetres in 1116, metres
  in 203, centimetres in 296 and inches in 128. The loader picks the unit factor that best matches the declared size.
* **Declared size wins.** When a file's bounds still differ by more than 2 % per axis from the declared size, the mesh
  is scaled per axis to the declared size, about its origin. Some manufacturers reuse one generic mesh at many sizes.
  The one exception is a declared size of exactly 1 × 1 × 1 m, which is treated as unset.
* **Primitives (2070 models).** Cube, Cylinder, Sphere, Base, Yoke, Head, Scanner, Conventional, Pigtail and the 1.1
  variants are generated procedurally and sized by Length, Width and Height. GDTF 1.1 moved the origin of the Base,
  Scanner and Conventional meshes to the mounting plate, so `Base1_1`, `Scanner1_1` and `Conventional1_1` hang below
  their origin (Z from −Height to 0); all other primitives are centred. The files agree: Conventional1_1 bodies carry
  their Beam at Z ≈ −Height (BOS P-7 HEX, Briteq BT-THEATRE), 1.0 Conventional bodies at −Height/2 (ACME BW-545), and
  Base1_1 bases end where their centred Yoke begins.
* **Missing files.** A model whose file is missing falls back to its primitive. With no primitive either, it gets a box
  of the declared size and a warning.
* **Library-wide load.** All 4982 model definitions of the library load with no failures: 6.3 M triangles in about 26 s
  on 4 cores.
* **Textures.** glTF baseColor textures, and 3DS texture maps that are present in the archive, become
  `gdtf:{fixture file}#{entry}` keys. Images embedded in a GLB use `…#{model entry}?image={index}`.

## Fixture library

* **Scanning.** `FixtureLibrary.Scan(folder, specs)` reads only file names, using the pattern
  `Manufacturer@Model@Revision.gdtf`, plus the specs index. The repository root's 1091 files are indexed in about
  0.4 s, every one with a category from the specs:

  | Category | Files | Category | Files |
  |---|--:|---|--:|
  | PROFILE | 181 | HAZE | 2 |
  | WASH | 293 | FOG | 5 |
  | WASH BEAM | 70 | SCANER | 2 |
  | BEAM | 60 | LASER | 1 |
  | SPOT | 48 | STROBE | 44 |
  | FOLLOW SPOT | 2 | BLINDER | 36 |
  | LED BAR | 173 | UV | 1 |
  | PAR | 90 | BSW | 53 |
  |  |  | OTHER | 30 |

  For files without a specs record, the category is guessed from words in the model name.
* **Revisions.** Revisions are ordered by a date in the revision text (several date layouts are recognised), then by a
  version number, then by file time. `GetRevisions` returns them newest first, and `GroupByCategory` builds the
  library tabs.
* **Parsing.** `GetFixtureType` / `GetFixtureTypeAsync` parse on demand. Concurrent callers share one parse, failures
  are not cached, and the cache keeps the most recently used fixture types (128 by default). Below it,
  `FixtureLibrary.DiskCache` (default `FixtureTypeDiskCache.Default`, null to disable) keeps parsed fixture types on
  disk across sessions.
* **Name lookup.** `Find(manufacturer, model)` and `FindBySpec("Manufacturer@Model[@Revision]")` match names from MVR
  stub GDTFs:
  * case, spaces vs underscores and punctuation are ignored, except `+`, which is spelled out as "plus" the way file
    names write it ("Robin Spikie+" = `Robin_SpikiePlus`, "SIX+ BAR L" = `SIX_plus_BAR_L`), so "Spikie+" and
    "Spikie" stay different models;
  * a model named by its file name beats one that only has the name as a specs alias, before revisions are compared;
  * manufacturer names reduce to their brand ("Martin" = "Martin_Professional", "Robe" = "Robe_Lighting", "Clay Paky" =
    "Clay_Paky", "Chauvet" = "Chauvet_Professional", "Ayrton " = "Ayrton"), and a brand prefix inside the model name is
    removed;
  * the newest revision wins;
  * when the model itself is not in the library, the most similar model of the same brand is returned as
    `FixtureMatchKind.Substitute`, so the UI can say so.

  For the stubs in `stages/Фокина.mvr`:

  | Stub | Library file | Match |
  |---|---|---|
  | `Ayrton@Zonda 3 FX` | `Ayrton@Zonda_3_FX@V1.81_Corrected_Factory_Tilt.gdtf` | same model |
  | `Ayrton@Zonda 3 Wash` | Zonda 3 FX (same body) | substitute |
  | `Ayrton@Rivale Profile`, `Ayrton @Rivale Profile` | `Ayrton@Rivale_Wash@V1.8_Correct_Gobo_Value.gdtf` | substitute |
  | `Ayrton@MagicBlade R` | `Ayrton@MagicPanel_602@V2.5_Add_Mode.gdtf` (Magic beam family) | substitute |
  | `Martin@Atomic 3000 LED` | `Martin_Professional@VDO_Atomic_Bold@20260620.gdtf` (its LED successor) | substitute |

  Rivale Profile, MagicBlade R, Zonda 3 Wash and Atomic 3000 LED are not in the repository. Adding their GDTFs makes
  the same calls return them as exact matches.

## Disk cache

`FixtureTypeDiskCache` stores parsed fixture types in `{ConfigPaths.CacheDir}/gdtf/{key}.gdtfc`:

* **Key.** SHA-256 over the archive's table of contents — every entry's name, sizes and CRC-32, the zip format's own
  content checksum — plus the cache format version and the module version ids of `Candela.Gdtf` and `Candela.Core`.
  The same content under another name or path hits; an edited file misses; any change of the reader code changes
  the module id (builds are deterministic) and so the key. Hashing the whole file instead would cost more than
  parsing it: 3D models are over 95 % of a `.gdtf`, the description a few percent.
* **Format.** A header (magic, version, reader id, key, length, SHA-256 of the payload) and a binary payload: every
  object of the model with its values, then every resolved link as an index into the object lists, so a loaded
  fixture type has exactly the links of the parse. Channel instances are rebuilt by `DmxLayout`, warnings are
  stored. A test round-trips all 1091 library files and compares the loaded object graph with the parsed one
  property by property, links included.
* **Corruption.** Files are written to a temporary name and renamed. A file that fails any check (magic, version,
  key, checksum, counts, indices, enum values, trailing data) is deleted and the GDTF is parsed again; I/O failures
  only cost the caching. `Statistics` counts hits, misses, stores, damaged files and write failures.
* **Size.** 64 MB for the whole library. Beyond `MaxBytes` (512 MiB) the least recently used files are deleted down to
  three quarters of the limit.
* **Speed.** Reading all 1091 files: 4.6 s parsing vs 0.5 s from the cache (8–9× faster, Release, one thread); the
  test suite (Debug) measures about 3× for the 60 largest descriptions.
* **Tests.** The Gdtf, Dmx and Mvr test assemblies point `CANDELA_CACHE_DIR` at a temporary folder of the run (unless
  it is set), so test runs neither write into the user's cache nor read what earlier runs left there.

## Robustness and performance

* **Full parse.** All 1091 files parse with no exception in about 8–11 s, in parallel on 4 cores. The test suite
  reports the wall-clock and processor time; it fails only when both say the parse is slow (more than 90 s, and more
  than 60 s of processor time per core), so a machine busy with other work does not fail it.
* **Warnings.** 63 files have warnings, and all of them are genuine file defects:
  * channel sets pointing at wheel slots that do not exist (1269 in 34 files, mostly Robe prism macros);
  * channels whose geometry is outside the mode's tree;
  * duplicate geometry names;
  * DMX channels without a logical channel;
  * overflowing DMX values.
* **Containment.** Each section, mode and channel is parsed under its own guard, so one bad element never loses the
  fixture. Warnings are capped at 500 per file, and their paths and messages are cut to 240 and 600 characters.
* **Hostile input.** `description.xml` is read through a filtering `XmlReader`: elements nested deeper than 264
  levels (enough for the 256 geometry levels kept; real files reach 12) are skipped with their content, and reading
  stops after 4 million elements plus attributes (the largest library description has 260 000), both with a
  warning. Descriptions over 64 MiB (the largest is 6 MB) are rejected. `XDocument` costs
  O(depth²), so 30 000 nested geometries took 5.5 s; they now take 12 ms. Normal files are not slower: loading all
  1091 descriptions takes the same CPU time with and without the filter (2.2 s), and a full sequential parse of the
  library 4.4 s before and after. Further limits: 200 000 geometry-reference instances and 1 000 000 channel
  instances per mode, reference chains of 16, DMX slot offsets up to 32 768 per break (the library's largest
  footprint is 1813), warning paths built from shortened names, part paths of at most 4096 characters, a step
  budget in `FixtureGeometryBuilder` for references refused over and over, and images above 64 megapixels refused
  from their header. Geometry matrices whose last row is not `0,0,0,1` are made affine, and a namespaced
  description (not GDTF, but written by generic XML tools) is read by local names, both with a warning. A model file
  that its decoder cannot read only falls back to the primitive or a box, whatever the decoder throws.
* **Linear resolution.** Every name lookup of the resolver is a dictionary lookup, built once per fixture type or
  mode: mode masters, relations and macro values (with all their tolerant spellings), features, same-named geometries,
  RDM personalities, and the attribute/wheel/emitter/… indexes, which used to be rebuilt for every mode. Function and
  channel-set ranges skip equal starts directly, and a channel with tens of thousands of mutually exclusive functions
  stops the exclusivity search after 2^20 skips (warning; the rest end at the next start). Descriptions of 2–4 MB
  built to hit the old scans took 10 s to 3 minutes; they now take well under a second. Model files outside their
  folder and 3DS texture files are found through an index of the archive's entries.
* **Part paths.** Besides the per-path limit, all part paths of a build together are limited to 4 Mi characters (the
  library's largest needs 113 000); a 50 kB description of a deep template instanced 800 times otherwise made the
  paths alone take 800 MB.
* **Hot path.** `FixtureGeometry.ComputeWorldTransforms` takes spans and allocates nothing per frame.

## Known gaps

* **No GDTF writer.** The module only reads.
* **Opaque blocks.** FTPresets (left open by the spec) and non-RDM protocol blocks are kept as generic attribute bags.
* **Uninterpreted content.** SVG model views, Display textures and media-server geometries are read and reachable
  through the archive, but not interpreted. glTF animations and skins are ignored, and so is the 3DS keyframer (3DS
  vertices are already in scene space).
* **Ambiguous geometry names.** When the same name appears several times, references use the first match, preferring
  one in the mode's own tree. GDTF requires unique names, but 4 library files repeat 16 of them.
* **Substitutes need confirmation.** A substitute match is a best effort by name similarity within the brand. The caller
  should show it as a substitute rather than treat it as the real fixture.
