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
  * AdditionalColorSpaces, Gamuts, DMXProfiles (cubic segments, with `Evaluate`) and CRIs;
  * legacy Connectors;
  * Properties: OperatingTemperature, Weight, LegHeight and PowerConsumption. PowerConsumption covers Value, PowerFactor,
    the connector link and the voltage and frequency ranges. The connector link resolves to a WiringObject or to a
    legacy `<Connector>`; the 14 library files that have a PowerConsumption all use the legacy form.
* **Models:** Name, Length/Width/Height (m), PrimitiveType, File and the SVG offsets.
* **Geometries:** every type, each with Name, Model and Position. The types are Geometry, Axis, FilterBeam, FilterColor,
  FilterGobo, FilterShaper, Beam, MediaServerLayer/Camera/Master, Display, GeometryReference (+ Breaks), Laser
  (+ Protocols), WiringObject (+ PinPatches), Inventory, Structure, Support and Magnet. Beam keeps all its photometric
  attributes; WiringObject and Support keep all their electrical and mechanical ones.
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
links a channel instance to the part it moves.

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
  variants are generated procedurally and sized by Length, Width and Height.
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
  are not cached, and the cache keeps the most recently used fixture types (128 by default).
* **Name lookup.** `Find(manufacturer, model)` and `FindBySpec("Manufacturer@Model[@Revision]")` match names from MVR
  stub GDTFs:
  * case, spaces vs underscores and punctuation are ignored;
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

## Robustness and performance

* **Full parse.** All 1091 files parse with no exception in about 8–11 s, in parallel on 4 cores. The test suite
  reports the time.
* **Warnings.** 63 files have warnings, and all of them are genuine file defects:
  * channel sets pointing at wheel slots that do not exist (1269 in 34 files, mostly Robe prism macros);
  * channels whose geometry is outside the mode's tree;
  * duplicate geometry names;
  * DMX channels without a logical channel;
  * overflowing DMX values.
* **Containment.** Each section, mode and channel is parsed under its own guard, so one bad element never loses the
  fixture. Warnings are capped at 500 per file.
* **Hot path.** `FixtureGeometry.ComputeWorldTransforms` takes spans and allocates nothing per frame.

## Known gaps

* **No GDTF writer.** The module only reads.
* **No persistent cache.** [ARCHITECTURE.md](ARCHITECTURE.md) foresees a binary cache keyed by file hash; the library
  currently caches parsed fixture types in memory only.
* **Opaque blocks.** FTPresets (left open by the spec) and non-RDM protocol blocks are kept as generic attribute bags.
* **Uninterpreted content.** SVG model views, Display textures and media-server geometries are read and reachable
  through the archive, but not interpreted. glTF animations and skins are ignored, and so is the 3DS keyframer (3DS
  vertices are already in scene space).
* **Ambiguous geometry names.** When the same name appears several times, references use the first match, preferring
  one in the mode's own tree. GDTF requires unique names, but 4 library files repeat 16 of them.
* **Substitutes need confirmation.** A substitute match is a best effort by name similarity within the brand. The caller
  should show it as a substitute rather than treat it as the real fixture.
