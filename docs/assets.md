# Assets: models, images, materials, procedural meshes (`Candela.Assets`)

Everything a user brings into a scene besides fixtures — 3D models with their textures, pictures used as reference
drawings or posters, GDTF files kept with a project — plus the built-in material library and the procedural meshes
(walls, floors, trusses, cables, image planes, primitives) the engine and renderer draw. The module depends only on
`Candela.Core`; it produces Core `ModelData` / `MeshData` / `MaterialData` / `ImageData` and implements Core
`IImageSource`. Third-party code: AssimpNet 5.0.0-beta1 (Assimp 5, BSD-3), SharpGLTF.Core (MIT), StbImageSharp and
StbImageWriteSharp (public domain / MIT).

`tests/Candela.Assets.Tests` (≈ 250 tests, about 15 s) writes every test file itself — OBJ+MTL+PNG, GLB/glTF
built with SharpGLTF, COLLADA text, ASCII/binary STL, ASCII/binary PLY, 3DS, ASCII FBX, PNG/JPEG/TGA/BMP/HDR/GIF/PSD —
and uses the repository's GDTF fixtures as a real-world corpus: `RealModelTests` imports 60 GLB and 80 3DS models
written by many manufacturers' exporters and compares them with the sizes their description.xml declares (43 of 60
GLBs match within 2 % after the Y-up conversion; 61 of 63 decisive 3DS models fit their proportions better as read
Z-up than with Y and Z swapped). `ModelRobustnessTests` feeds every reader truncated and randomly corrupted files
(each kind a former crash or hang of the native library).

## Conventions

| | |
|---|---|
| space | right-handed, **Z up**, metres; X right, Y into the stage, so a model's *front* faces the audience (−Y) |
| triangles | counter-clockwise front faces; `MeshData.Validate()` holds for every produced mesh |
| texture coordinates | glTF convention: U right, V down (top row of an image at V = 0) |
| tangents | `xyz` + handedness `w` as in glTF: `cross(normal, tangent) · w` points towards decreasing V (normal-map green up) |
| material factors | linear Rec.709 (glTF semantics); colour textures sRGB-encoded; the renderer converts to Rec.2020 |
| emission | `MaterialData.EmissiveLuminance` in cd/m² for an emissive colour of 1 |
| procedural UVs | metres × UV scale (one texture tile per metre at scale 1), except image planes (0 … 1) |

**Robustness.** A file that cannot be imported at all throws `AssetImportException` with a message written for the
user. Everything recoverable — a missing texture, an unsupported texture format, a broken mesh part, guessed units —
becomes a warning on the `ImportedModel` and an `Issue` in the `AssetStore`; one bad element never fails a whole model
or project.

Errors that Assimp *reports* are caught like any other, but a segmentation fault inside the native library cannot be
caught — it ends the process (the editor, or a project load that decodes a damaged asset). Fuzzing the bundled Assimp
5.0 build with about 20 000 truncated and corrupted files showed crashes or endless loops in its OBJ, MTL, PLY,
STL (ASCII), COLLADA, glTF, 3MF, X, X3D and ASCII FBX readers. Damaged input therefore never reaches Assimp (and all
1 794 distinct 3DS and GLB models inside the repository's GDTF files import without a failure):

* **OBJ/MTL** are rewritten by `ObjSanitizer` into a strictly well-formed subset first (numbers validated, face corners
  resolved to absolute indices and written uniformly, faces with invalid references dropped with a warning, points,
  lines and free-form statements removed, MTL statements outside a named material dropped, texture options checked).
  An OBJ without any face is rejected before Assimp sees it (it crashes building a point cloud). The original bytes are
  what the project stores.
* **COLLADA** is checked by `ModelPreflight`: well-formed XML, numeric arrays and index lists that contain only valid
  numbers and as many as declared, accessors that stay inside their arrays.
* **ASCII FBX** property records (`P: "name", "type", …`) must carry the value tokens Assimp reads for their type
  (it indexes them without a bounds check). Binary FBX and 3DS showed no failures under the same fuzzing.
* **STL** is read by Candela's own `StlReader` (Assimp crashed on a bare `solid name` line).
* **PLY** is read by Candela's own `PlyReader`; **glTF/GLB** only by SharpGLTF (a file SharpGLTF rejects is not retried
  with Assimp, whose glTF reader crashed on exactly those files). GLB containers whose chunks lack the 4-byte padding
  the specification requires (written by some fixture exporters) are re-packed and read, with a warning.
* 3MF, X, X3D and the other exotic Assimp formats are not offered (they are rejected with advice on what to export).

## Model import (`ModelImporter`)

`ModelImporter.Import(path)` reads a model from disk together with its companion files; `Import(fileName, bytes,
companions)` reads the same model from memory (a project archive). Both return an `ImportedModel`: the converted
`ModelData`, every file that was read (main file, MTL, `.bin` buffers, textures — exactly what a project must keep),
embedded images, images generated during import, missing references, warnings, and the up axis / unit scale that were
applied.

### Formats

| Format | Reader | Up axis (Auto) | Units (Auto) |
|---|---|---|---|
| glTF 2.0 `.gltf` (external or data-URI buffers), `.glb` | SharpGLTF | Y (specification) | metres (specification) |
| Wavefront `.obj` + `.mtl` | Assimp (after `ObjSanitizer`) | Y (convention of Blender, Maya, 3ds Max OBJ export) | none → metres / guess |
| Autodesk `.fbx` (binary and ASCII) | Assimp (ASCII after `ModelPreflight`) | from `GlobalSettings` (UpAxis, FrontAxis, CoordAxis and signs) | `UnitScaleFactor` (cm per unit; default 1 cm) |
| COLLADA `.dae` | Assimp (after `ModelPreflight`) | `<asset><up_axis>` (Y_UP default, Z_UP, X_UP) | `<asset><unit meter>` (default 1) |
| 3D Studio `.3ds` | Assimp | Z | none → metres / guess |
| STL `.stl` (ASCII and binary) | Candela (`StlReader`) | Z (CAD) | none → metres / guess |
| Stanford `.ply` (ASCII, binary little/big endian) | Candela (`PlyReader`) | Z | none → metres / guess |

Rejected with advice instead of a generic error:

* **SketchUp** `.skp` — recognised by extension *and* by its header (`0xFF 0xFE 0xFF`, length, "SketchUp Model" in
  UTF-16LE), so a renamed file is caught too: *"SketchUp files cannot be read; export from SketchUp as .dae, .fbx or
  .glb"* (`AssetFormats.SketchUpMessage`).
* Native scene files of other tools (`.blend`, `.max`, `.c4d`, `.ma`/`.mb`, `.3dm`), drawings (`.dwg`, `.dxf`), USD and
  CAD exchange formats (`.step`, `.iges`) — each with the export to use.
* Formats the bundled Assimp could read but not robustly (`.3mf`, `.x`, `.x3d`, `.zae`, `.ase`, `.lwo`, `.off`, `.ac`,
  `.ms3d`) — each with the export to use.
* glTF with Draco/meshopt mesh compression ("export it without compression"); a glTF file SharpGLTF rejects is reported
  with SharpGLTF's reason.

### Axes and units

All axis and unit handling happens in one place after reading; Assimp's own root conversions (COLLADA unit and up
axis, the 3DS Z→Y rotation) are switched off or discarded so every format goes through the same rules.

* **Y-up sources** are rotated by `(x, y, z) → (x, −z, y)`: +Y becomes +Z and the model's front (+Z in glTF/OBJ/Maya)
  faces the audience (−Y). This is a proper rotation, so winding and handedness are preserved.
* **FBX** carries a full axis system. The basis is built from it exactly: the coordinate (right) axis maps to +X, the up
  axis to +Z, the front axis to −Y (`SourceMetadata.FbxBasis`), which covers Maya (Y up, front +Z), 3ds Max (Z up,
  front −Y) and mirrored systems. Mirroring bases flip the triangle winding back to counter-clockwise.
* **COLLADA** `X_UP` is mapped by its definition (right = −Y, up = +X, towards the viewer = +Z).
* **Units**: FBX `UnitScaleFactor` is centimetres per file unit (metres = factor / 100); COLLADA `<unit meter>` is metres
  per unit. Formats without units
  are read as metres, **unless the model would be larger than 200 m** — then it is read as millimetres (the usual CAD
  unit) and a warning says so (`ImportOptions.GuessUnits`, `ImportOptions.GuessMillimetresAbove`).
* **Overrides**: `ImportOptions.UpAxis` (Auto / Y / Z) and `ImportOptions.UnitScale` (metres per file unit) replace the
  file's metadata. They are stored with the asset, so a project re-imports a model identically; `AssetStore
  .SetImportOptions` changes them later and re-decodes the model.
* Node transforms are kept (parts with their own `Transform`); after the conversion `C` they become `C⁻¹ · T · C`, so a
  part's transform is expressed in Candela space too. Shared meshes stay shared (instancing).
* **Mirrored nodes** (negative scale — symmetric props are often exported as one mesh instanced with scale −1) would
  turn an instance inside out for a renderer that culls by winding. The importer bakes the reflection into a mirrored
  copy of the mesh (winding flipped back, normals and tangents correct) and stores `S · T` with `S = diag(−1, 1, 1)`,
  so every part transform has a positive determinant while the world geometry is unchanged.

### Geometry processing

* Triangulation, joining of identical vertices, removal of degenerate and invalid triangles (out-of-range indices,
  non-finite positions); points and lines are skipped with a warning. PLY polygons are fan-triangulated (PLY writers
  emit convex faces); PLY texture coordinates (`s t`, `u v`, `texture_u texture_v`, or MeshLab's per-corner `texcoord`
  lists, which unweld the corners) are flipped from OpenGL's V-up to Candela's V-down.
* **Normals** are taken from the file when it has valid ones (STL facet normals are always ignored: they are often zero
  or disagree with the winding, and STL repeats every corner per facet, so its triangles are welded by position first).
  Otherwise they are generated with the smoothing angle
  `ImportOptions.SmoothingAngleDegrees` (66° by default, the usual DCC default): faces are area-weighted, faces on both
  sides of a texture seam smooth together, and vertices are split where the dihedral angle is sharper — a box keeps
  flat faces, a cylinder's side stays smooth (`MeshTools.GenerateNormals`). Around ordinary vertices (≤ 64 faces) every
  pair of faces is compared; around high-valence positions (the centre of a finely divided disc, a cone apex) the faces
  are first grouped into at most 64 orientation clusters, so a 50 000-triangle fan takes milliseconds instead of
  minutes. Assimp's own generator is not used because it cannot split vertices that a format (PLY, STL, 3DS) already
  shares between faces. glTF files without normals get flat normals, as the glTF specification requires.
* **Tangents** are always computed from the final UVs (Lengyel's method, Gram–Schmidt against the normal, glTF
  handedness), so normal maps work for every format.
* Materials that no part uses (Assimp invents a default material for every OBJ; libraries carry unused entries) are
  dropped and sub-mesh slots renumbered in file order, so slot indices — the keys of `MeshComponent.MaterialOverrides` —
  are stable across re-imports. Assimp's invented default materials are replaced by Candela's neutral grey (0.8,
  roughness 0.6).
* Models with more than `ImportOptions.MaxParts` (1024) mesh-carrying nodes are merged into one part (transforms
  baked, one sub-mesh per material) to bound draw calls.
* Skinned meshes are imported in their bind pose; animations, cameras and lights in the file are ignored; vertex
  colours are not supported (warning).
* **Performance** (Debug build, one core of the test machine): a 105 800-triangle binary STL imports in ≈ 0.2 s, an
  80 000-triangle binary PLY in ≈ 0.1 s, a 115 200-triangle OBJ without normals (sanitising, text parsing, joining,
  normal generation) in ≈ 1 s.

### Companion files and texture keys

Textures are referenced by keys, `ImportOptions.TextureKeyPrefix` + a relative path; the `AssetStore` uses the prefix
`asset:{guid}/`. A relative path resolves to exactly one of:

* a **companion file** read from disk (`maps/wood.png`) — stored with the asset;
* an **embedded image** (`embedded/…`: GLB buffer-view images, data URIs, FBX embedded textures);
* a **generated image** (`generated/…`: see the material conversion below).

References are resolved identically from disk and from memory (`ModelFileSet`), so a model imported from a folder and
later loaded from the project archive produces the same keys: relative to the model's folder (`..` cannot leave the
set's key space), absolute paths from other machines (`C:\Users\…\wood.jpg`) by file name, then any file of that name
(case-insensitive; on disk the model's folder is indexed up to three levels deep). UTF-8 file names mangled by the
native binding's ANSI marshalling are repaired. Only images are ever read from **outside** the model's folder (and
only when their content is an image), and references that turn out not to be images are not stored, so a crafted
model cannot pull private files into a project that is later shared. Missing textures do not fail the import: the
material is used without the map, a warning names the file, and the `AssetStore` reports it as an issue.

### Materials

**glTF** metallic-roughness maps one-to-one: base colour factor and texture, metallic/roughness factors and the packed
texture, normal, occlusion and emissive textures, alpha mode and cutoff, double-sided, `KHR_materials_emissive_strength`,
`KHR_materials_transmission`, `KHR_materials_ior`, and the scale of `KHR_texture_transform` (offset/rotation are not
supported; warning). `KHR_materials_pbrSpecularGlossiness` is approximated (roughness = 1 − glossiness, metallic when
the specular colour exceeds 0.5); unlit materials are imported as matte lit surfaces (a stage model must react to
stage light). glTF emissive colour × strength is relative, so 1 is given `EmissiveReferenceLuminance` = 100 cd/m²
(roughly a display's white).

**Classic materials** (OBJ/MTL, FBX, COLLADA, 3DS: Phong, Blinn, Lambert) are approximated by
`MaterialApproximation.FromPhong`:

* **Roughness** from the Phong exponent n through the Blinn-Phong ↔ Beckmann correspondence α = √(2 / (n + 2))
  (Walter et al. 2007) and glTF's perceptual roughness r = √α, i.e. r = (2 / (n + 2))^¼, clamped to [0.03, 1]:

  | n | 0 | 10 | 50 | 100 | 200 | 1000 |
  |---|---|---|---|---|---|---|
  | roughness | 1 | 0.64 | 0.44 | 0.37 | 0.32 | 0.21 |

  Without an exponent roughness is 0.8; without a highlight (specular ≈ black) at least 0.9.
* **Metallic** = 1 when (a) the specular colour dominates a dark diffuse colour (max Ks > 0.5 and > 2 × max Kd), (b) the
  specular colour is clearly tinted (saturation > 0.25 at max Ks > 0.25 — dielectrics have white highlights), or (c) a
  word of the name starts with a metal (chrome, steel, aluminium, gold, brass, …, also Russian names) and there is a
  highlight (max Ks ≥ 0.2) — **unless** a word names a dielectric surface (plastic, paint, lacquer, varnish, rubber,
  vinyl, ceramic, wood, fabric, glass, …): glossy black plastic is exported with exactly pattern (a), and painted steel
  is a paint surface. Names are split into words at non-letters and camel-case changes, so "MetalFrame" and
  "Brushed_Steel" match while "Environment" (iron) and "Monochrome" (chrom) do not. A metal's base colour is its
  specular colour (its F0); otherwise base colour = diffuse colour.
* **Shading models without highlights** — 3DS Flat/Gouraud shading, FBX and COLLADA Lambert, COLLADA constant — have
  their specular colour and exponent ignored (the original renderer ignores them too). Real fixture 3DS files carry
  black Gouraud "lens" materials with a white specular slot, which would otherwise become metals. OBJ is exempt: Assimp
  reports Gouraud for every MTL material without an `illum` statement.
* **MTL specifics**: `Tr` (transparency) is turned into `d = 1 − Tr` when a material has no `d` (Assimp ignores `Tr`);
  texture statements are matched case-insensitively (`map_kd`), and `norm`/`map_norm` normal maps and `map_emissive`
  are mapped to the spellings Assimp understands.
* **Opacity** < 1: clear materials (named glass/window, or opacity ≤ 0.6 at roughness ≤ 0.3) become transmissive glass
  (transmission = 1 − opacity, IOR 1.5, roughness ≤ 0.1, double-sided); everything else alpha-blends. Opacity 0 is
  treated as "not set" (exporters write it that way), never as invisible.
* **Emission** (`Ke`): colour normalised to its maximum, luminance = maximum × 100 cd/m².
* **Maps**: a diffuse map sets the base colour factor to white (files leave Kd at a grey default next to a map). A bump
  map is used as a normal map when its pixels look like one (mean colour near (0.5, 0.5, ≥ 0.7) and not grey); otherwise it is converted
  from a height map (central differences, `generated/…_normal.png`). Separate metalness and roughness maps (FBX PBR, Assimp PBR slots)
  are packed into one glTF metallic-roughness map; a separate opacity map is merged into the albedo's alpha (alpha
  mask), the albedo's own alpha used as opacity map becomes a mask; emissive and ambient-occlusion maps are carried.

## Image import (`ImageImporter`)

| Format | Notes |
|---|---|
| PNG | 8/16-bit, palette, grey, alpha (16-bit is reduced to 8) |
| JPEG | baseline and progressive; **EXIF orientation** (tag 0x0112, both byte orders) is applied so photos appear upright |
| TGA | uncompressed and RLE, true colour, grey and palette |
| BMP | uncompressed 1–32 bit (RLE-compressed BMPs are not supported) |
| PSD | the flattened composite image (layers are not read) |
| GIF | first frame |
| Radiance HDR | decoded to linear float RGBA (`PixelFormat.RgbaFloat`) |

Decoding is StbImageSharp; output is RGBA, top row first. 8-bit images are returned exactly as stored (sRGB colour or
linear data such as normal maps — the meaning depends on use). Headers are validated before decoding; images larger
than 32 768 pixels per side or 256 megapixels (Radiance HDR: 64 megapixels, 1 GiB as float RGBA) are rejected, and so
is a header that claims more pixels than the file could encode at the best compression ratio of its format (deflate
≈ 1032:1 on 1-bit PNG rows, 12-bit LZW for GIF, run-length for TGA/PSD/HDR) — the decoder would otherwise allocate the
full buffer for a few damaged bytes. A pixel buffer that cannot be allocated ends in a message, not a crash. JPEG marker
segments are walked in managed code first:
StbImageSharp loops forever when a segment's length runs past the end of the data — exactly what a partial download
looks like — so such files are rejected as damaged (truncated *scan* data still decodes as far as it goes). TIFF, WebP, HEIF/AVIF, DDS and OpenEXR are recognised and
rejected with a message naming a supported alternative. `EncodePng` / `SavePng` (atomic: temporary file + rename)
write thumbnails and renders with StbImageWriteSharp; float images are clamped to 0 … 1 and sRGB-encoded (pass
tone-mapped data for renders).

## Asset store (`AssetStore`)

The catalogue of a project's assets: `AssetEntry { Guid Id, AssetKind Kind (Model, Image, Gdtf, Texture), original
file name, original bytes, companion files (models), source path, import options, content hash }`. Original bytes are
kept unchanged so a project can be saved and opened elsewhere.

* **Import**: `ImportFile` (kind from the extension; `Texture` forces a texture), `ImportModel` (decodes immediately to
  validate the model and discover its companion files), `ImportImage`. Importing identical content (same SHA-256 over
  the main file and the companions, same kind and options) returns the existing entry.
* **Persistence API for `Candela.Project`**: `Entries` (in insertion order), `Add(kind, name, bytes, companions, id,
  importOptions)` (validates the content cheaply — header/format — and keeps the stored id), `GetBytes(id)`,
  `GetCompanionFiles(id)`, `Remove`, `Clear`, and the `AssetChanged` event.
* **Decoding**: `GetModel` / `TryGetModel` and `GetImage` / `TryGetImage` decode on first use, exactly once even under
  concurrent requests (`Lazy` with `ExecutionAndPublication`), and cache the result or the error. `ClearDecodedCaches`
  frees memory.
* **Models decoded elsewhere**: `SetDecodedModel(entry, model)` attaches a model decoded outside the store — by the
  engine's isolated import worker, from its disk cache, or by the MVR reader — so `GetModel`, `TryGetModel` and the
  model's texture keys are served from it and the native importer never runs in the application;
  `SetModelDecodeError(entry, reason)` records a failure (a crashed worker) so the asset is not decoded again until it
  changes. Both refuse (return false) when the entry was replaced or removed in the meantime, so a late background
  decode never overwrites a newer one. `TryGetDecodedModel(id, out model, out error)` tells whether the decode result is
  known without starting a decode (the engine's renderer binding uses it to decode in the background).
* **`IImageSource`**: `asset:{guid}` is the image of an Image/Texture asset; `asset:{guid}/{relative path}` is a
  texture of a model asset (companion file — exact, then case-insensitive — embedded or generated image);
  `builtin:{material id}/{channel}` is delegated to the `MaterialLibrary`. `Load` never throws: unknown keys return
  null, and problems (missing asset, missing or undecodable texture, model that cannot be decoded) are recorded once
  each in `Issues` (Core `Issue`, category Scene, codes `asset.*`) and raised through `IssueReported`.
* All members are thread-safe.

## Built-in materials (`MaterialLibrary`)

One entry per id in Core `MaterialIds`, with a display name, a UI category (`Categories`, `InCategory`,
`CategoryDisplayName`), a description, the reference values and the sources they come from. `GetMaterial(id)` returns a
new `MaterialData` (unknown ids get the default grey). Values are typical, physically plausible figures from published
reflectance tables, not measurements of a specific product; albedos are linear Rec.709, ρ is the luminous reflectance
(CIE Y, the factor in L = ρ·E/π for a matte surface lit with E lux).

| Id (`builtin:…`) | Name | Albedo (linear RGB) | ρ | Roughness | Metallic | Textures | Reference |
|---|---|---|---|---|---|---|---|
| `default` | Default grey | 0.40 | 0.40 | 0.60 | 0 | – | neutral grey |
| `painted-wall-white` | White painted wall | 0.80, 0.80, 0.78 | 0.80 | 0.85 | 0 | – | matte white emulsion 0.75–0.85 [1, 2] |
| `painted-wall-black` | Black painted wall | 0.04 | 0.04 | 0.80 | 0 | – | matte black paint 0.03–0.05 [1] |
| `concrete` | Concrete | 0.36, 0.35, 0.33 | 0.35 | 0.90 | 0 | albedo, normal, roughness | concrete 0.25–0.40 (new concrete up to 0.55) [1, 3] |
| `stage-floor-black` | Black stage floor | 0.035 | 0.035 | 0.50 | 0 | roughness | black vinyl / painted stage floor 0.03–0.05, satin |
| `dance-floor-white` | White dance floor | 0.75, 0.75, 0.74 | 0.75 | 0.35 | 0 | – | white vinyl 0.70–0.80, semi-gloss |
| `wood-stage` | Wooden stage | 0.33, 0.21, 0.11 | 0.23 | 0.55 | 0 | albedo, normal, roughness | light/medium wood 0.2–0.4 [1, 3] |
| `molton-black` | Black molton | 0.025 | 0.025 | 1.00 | 0 | – (double-sided) | black molton/serge 0.02–0.04; black velvet 0.01–0.02 |
| `aluminium-truss` | Aluminium truss | 0.91, 0.92, 0.92 | 0.92 | 0.35 | 1 | – | aluminium F0 [4] |
| `steel-black` | Black steel | 0.045 | 0.045 | 0.45 | 0 | – | black satin powder coat 0.04–0.05 (a dielectric coating) |
| `mirror` | Mirror | 0.97, 0.96, 0.91 | 0.96 | 0.03 | 1 | – | silver F0 [4] |
| `glass` | Clear glass | 1 (transmission 1) | – | 0.03 | 0 | – (double-sided) | soda-lime glass n = 1.52, F0 = ((n−1)/(n+1))² ≈ 0.04 |
| `led-screen-off` | LED screen (off) | 0.02 | 0.02 | 0.66 | 0 | albedo, roughness (UV × 32) | black-face SMD LED wall ≈ 2 % |
| `cyclorama` | Cyclorama | 0.70, 0.70, 0.69 | 0.70 | 0.95 | 0 | – (double-sided) | bleached cyclorama cloth 0.65–0.75 |
| `carpet` | Carpet | 0.10, 0.10, 0.11 | 0.10 | 1.00 | 0 | albedo, normal, roughness (UV × 2) | dark carpet 0.05–0.15 [1] |

**Procedural textures.** Small seamlessly tileable maps (256 × 256; the LED panel 64 × 64) generated on first use from
deterministic tileable value noise and cached, served as `builtin:{id}/albedo`, `/normal` and `/roughness`
(`MaterialLibrary.LoadTexture`, allocation-free lookups). A tile covers 1 m at UV scale 1, matching the procedural
meshes' UVs in metres.

* *Wood planks*: 8 boards of 125 mm per metre along U, one butt joint per board, fbm grain and figure, per-board tone,
  dark bevelled gaps (normal map from the bevel height).
* *Concrete*: low-frequency mottling, fine aggregate grain, sparse dark pores.
* *Carpet*: per-pixel fibre noise over tuft-sized blotches.
* *Black vinyl*: roughness only (scuffs, polished traffic lanes).
* *LED panel*: 8 × 8 SMD packages per tile on a black louvred mask; with UV scale 32 a tile spans 31.25 mm, a 3.9 mm
  pixel pitch.

Albedo maps are sRGB-encoded and normalised so their mean *linear* albedo equals the material's reference albedo (the
material's base colour factor is then white); roughness maps use glTF packing (G = roughness, B = metallic) with
absolute values and a roughness factor of 1; normal maps are tangent space with +Y (green) up.

## Procedural meshes

All are Core `MeshData` in the entity's local space (Z up, metres), with normals, UVs (metres × UV scale) and tangents,
one sub-mesh (material slot 0). Invalid sizes (NaN, ≤ 0, > 10 km) are clamped to 1 mm … 10 km instead of throwing.

**`WallMesh`** — a `WallComponent` is a thin box (24 vertices, 12 triangles), following the axes documented on
`WallKind`:

| Kind | X | Y | Z |
|---|---|---|---|
| Wall | width, centred (−W/2 … W/2) | thickness behind the front face (0 … T) | height up from the origin (0 … H) |
| Floor | width, centred | length (`Height`), centred | thickness downwards (−T … 0): top face at the origin |
| Ceiling | width, centred | length, centred | thickness upwards (0 … T): bottom face at the origin |

A wall's visible front face (normal −Y) lies in the plane y = 0 and faces the audience. UVs start at 0 at each face's
top-left corner (vertical faces: up = +Z; horizontal faces: up = +Y) and run in metres × `UvScale`. Zero thickness
gives only the visible face.

**`TrussMesh`** — a `TrussComponent` without its own model. Length runs along local X from the origin (0 … L); the
cross-section is centred on the X axis. Chords (main tubes) run the full length; every face carries zig-zag diagonals
at about 45° plus a perpendicular brace at each end. Chord rings are aligned so the mesh bounds are exactly
`[0, L] × [−S/2, S/2] × [−S/2, S/2]` for a square section of size S. `TrussProfile.Parse` reads the profile label:

| Labels | Shape | Size S | Chord Ø | Diagonal Ø |
|---|---|---|---|---|
| F22 / F23 / F24 | ladder / triangle / square | 220 mm | 35 mm | 16 mm |
| F32 / F33 / F34 | ladder / triangle / square | 290 mm | 50 mm | 20 mm |
| F42 / F43 / F44 | ladder / triangle / square | 400 mm | 50 mm | 25 mm |
| F52 / F53 / F54 | ladder / triangle / square | 520 mm | 50 mm | 30 mm |
| X30L / X30D / X30V | ladder / triangle / square | 290 mm | 48 mm | 16 mm |
| H30L / H30D / H30V, H40L / H40D / H40V | ladder / triangle / square | 290 / 390 mm | 51 mm | 16 mm |
| F31, "Ø48 pipe", "pipe 50 mm" | pipe | the diameter | – | – |
| "square 300 mm", "triangle 0.4 m" | as named | as given (bare numbers > 5 are mm) | 35 mm below 250 mm, else 50 mm | 0.45 × chord |

Dimensions are the outer sizes of the common catalogue series (Global Truss F-series, Prolyte X30/H30/H40). Triangles
have two chords on top and one at the bottom (point down, as usually flown); their height is (S − d)·√3/2 + d for chord
diameter d (`TrussMesh.SectionHeight`). Ladders have two chords one above the other. `TrussKind.Pipe` is a single tube
(default Ø48.3 mm scaffold tube, EN 39), `Ladder` forces a ladder section, `Tower` adds a 12 mm base plate (S + 300 mm
square) at x = 0 — like every truss its length runs along local X and the entity's rotation stands it up. Unknown
labels fall back to F34 with `IsRecognised = false` so the UI can flag them.

**`ImagePlaneMesh`** — one rectangle in the local XZ plane facing −Y, centred on the origin, with normalised UVs (the
image stretched once, top-left at top-left). `CreateMaterial` gives a poster a lit satin print (base colour = the image,
roughness 0.6) and a reference drawing a black surface emitting the image at 100 cd/m², so it reads the same under any
stage light; opacity < 1 alpha-blends. `SizeForAspect` sizes a plane to the image's aspect ratio.

**`CableTubeMesh`** — a polyline (or a `CableComponent` node/segment tree, split into chains at branch points and
ends) swept with a circular cross-section using rotation-minimising frames (double reflection, Wang et al. 2008), so
the tube does not twist. Vertex count for one chain of N path points: N × (segments + 1), plus 2 × (segments + 1) for
the end caps. With a slack factor s > 1 (cable length ÷ straight length, `CableComponent.SlackFactor`) each span hangs
as a catenary: k = L/2a solves sinh(k)/k = s (Newton), mid-span sag d = L·(cosh k − 1)/(2k) — 5 % slack sags about 14 %
of the span. Inclined spans sag by d × (horizontal fraction), vertical drops hang straight. Defaults: radius 4 mm,
8 sides, samples at most 0.1 m apart.
`CableTubeOptions.SpanSlack(nodeA, nodeB)` gives each span of a tree its own slack (1 = straight: a span fixed along a
truss or lying on a floor), and `CableTubeOptions.Support` (a `CableSupportHeight(x, y, maxHeight)`: the highest
surface at (x, y) not above `maxHeight`, or −∞) keeps the cable on the surfaces it lies on: every node and sample is
kept at least the tube radius above the supporting surface, measured along the surface's normal (on a ramp the centre
is lifted by radius / cos(slope), the slope taken from the heights a radius away, up to 60°; a steeper change is an
edge between surfaces), so no tube vertex goes below it. The query height of a
sample is the higher of the span's straight chord there and the cable's height at the previous sample, and the span is
walked from both ends (each sample keeps the higher result): a cable cannot pass down through a surface it lies on or
comes from (a cable leaving a riser drapes over its edge, whichever way the span was stored), while a surface over its
route (a platform it runs under) is ignored. A straight span (slack 1: a taut cable, a span fixed along an object) is
sampled the same way and draped over a surface it would cut through on its way (a taut cable leaving a riser); one
that cuts through nothing stays a single straight piece. `SamplePath(points, spanSlack[], spacing, support, clearance)` exposes
the same sampling for a polyline. Drawing only — a cable's cut length is measured by `Candela.Electrical`, not from
this path.

**`Primitives`** — box, cylinder, cone, UV sphere, capsule (centred, axis Z) and arrow (from the origin along +Z) for
helpers, gizmos and icons; smooth normals on curved surfaces, closed and outward-facing.

## Thumbnail cache (`ThumbnailStore`)

PNG files under `ConfigPaths.CacheDir/thumbnails` (overridable), keyed by a content hash (8–128 hex digits, e.g.
`AssetEntry.ContentHash` or a GDTF file hash), the square size in pixels (1 … 2048) and an optional variant
("fixture", "preview-dark"): `ab/abcdef…_128[_variant].png`. A changed file or another size never hits a stale image.
The renderer produces the images; the store only saves (`Save` from `ImageData`, `SavePng` from encoded bytes —
atomically, temporary file + rename, safe across processes) and loads them (`Load`, `LoadPng`). Corrupt files are
treated as missing and deleted; `Remove(hash)`, `Clear`, `SizeOnDisk` and `Trim(maxBytes)` (least recently used first;
loading counts as a use) manage the cache.

## Native library (Assimp)

AssimpNet 5.0.0-beta1 ships Assimp 5 for `linux-x64` (`libassimp.so`, needs glibc ≥ 2.27 and `libz.so.1`), `win-x64`
and `win-x86` (`assimp.dll`, built with the C runtime linked statically — its only import is `KERNEL32.dll`, so no
Visual C++ redistributable is needed — `WindowsNativeLibraryShipsAndNeedsNoVisualCppRuntime` checks the PE import
table) and `osx-x64`; the build copies them to `runtimes/{rid}/native`. Loading is exercised by the tests on linux-x64.
`AssimpNative.EnsureLoaded()` (called before the first Assimp import) fixes two problems of the managed binding:

* On Linux the binding calls `dlopen` through `[DllImport("libdl.so")]`, but glibc 2.34+ systems only ship
  `libdl.so.2` (the unversioned name belongs to the -dev package). A `DllImport` resolver maps it to `libdl.so.2`
  (or `libc.so.6`).
* The library is searched in `runtimes/{rid}/native` and flat next to the application, this assembly and AssimpNet.dll,
  so both portable builds and RID-specific publishes work, also under hosts with another base directory.

When the native library cannot be loaded, model imports other than glTF fail with a clear message
(`AssimpNative.EnsureLoaded()` returns it); glTF/GLB, images, materials and procedural meshes keep working.

## Known limitations

* The robustness measures above remove every crash and hang that about 20 000 fuzzed files (truncations, byte and
  number mutations, line edits) produced, but Assimp remains native code: a deliberately crafted FBX, COLLADA, 3DS or
  OBJ could still find a fault the checks do not cover. The definitive fix — running the native import in a separate
  worker process — needs a host executable and is left to the application.
* No animation, skinning (bind pose only), morph targets, cameras or lights from model files; vertex colours ignored.
* Textures in WebP, KTX2/Basis, DDS, TIFF or EXR are not decoded (the material is used without them, with a warning);
  Draco/meshopt-compressed glTF is rejected.
* `KHR_texture_transform` offset/rotation and specular-glossiness textures are not represented in Core materials.
* PSD layers, animated GIF frames after the first and 16-bit PNG precision are not kept.
* Unit guessing for unit-less formats is a heuristic (metres unless larger than 200 m); the import options override it.
  OBJ files from Maya are typically in centimetres and are misread by it — set the unit (0.01) in the import options.
* PLY polygons are fan-triangulated (concave PLY faces would be split wrongly); 3DS shininess percentages arrive from
  Assimp as Phong exponents of the same number, so 3DS roughness is approximate.
* OBJ/MTL PBR extensions (`Pr`, `Pm`, `map_Pr`, `map_Pm`) are kept in the file but not used: the bundled Assimp does
  not read them.

## References

1. IES Lighting Handbook, 10th ed. (Illuminating Engineering Society, 2011) — typical reflectances of room surfaces and
   materials.
2. SLL Lighting Handbook (CIBSE / Society of Light and Lighting, 2018) — recommended and typical surface reflectances.
3. S. Lagarde, C. de Rousiers, *Moving Frostbite to Physically Based Rendering 3.0*, SIGGRAPH 2014 course notes —
   albedo ranges of common materials.
4. T. Akenine-Möller et al., *Real-Time Rendering*, 4th ed. (2018), Table 9.2 — F0 of metals (linear RGB).
5. B. Walter, S. Marschner, H. Li, K. Torrance, *Microfacet Models for Refraction through Rough Surfaces*, EGSR 2007 —
   Phong exponent ↔ Beckmann roughness.
6. E. Lengyel, *Computing Tangent Space Basis Vectors for an Arbitrary Mesh* (2001).
7. W. Wang, B. Jüttler, D. Zheng, Y. Liu, *Computation of Rotation Minimizing Frames*, ACM TOG 27(1), 2008.
8. Khronos glTF 2.0 specification and extensions; COLLADA 1.4.1 specification; PLY format (G. Turk, Stanford, 1994);
   Autodesk FBX SDK documentation (axis system, UnitScaleFactor).
