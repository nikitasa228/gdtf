# Scene model and editing core (`Candela.Core`)

`Candela.Core` is the module every other module depends on: the scene document (entities, layers, settings,
component data), change notifications, undo/redo, selection, copying of entity subtrees with reference remapping,
the shared JSON configuration, issues, presets, settings and key bindings. It references no other Candela module and
has no behaviour that needs one (cable solving lives in Electrical, DMX evaluation in Dmx, …). Conventions are those of
[ARCHITECTURE.md](ARCHITECTURE.md): SI units, right-handed Z-up metres, `world = local * parentWorld`.

The scene is owned by the main thread; background work must not touch it.

| Area | Types |
|---|---|
| Document | `Scene`, `Entity`, `Layer` / `LayerCollection`, `SceneSettings`, `Component` and the components in `SceneGraph.Components` |
| Notifications | `Scene.Changed` (`SceneChange`, `SceneChangeKind`), `Scene.Version` |
| Copying | `EntityCloner`, `EntityCloneOptions`, `EntityCloneResult`, `EntityReferenceRemapper`, `[EntityReference]` / `[AssetReference]` / `[LocalId]` |
| Editing | `UndoStack`, `IUndoableCommand` and the commands in `Editing/SceneCommands.cs`, `Selection` |
| Files | `CandelaJson`, `SceneSerializer` (`SceneFile`, `EntityRecord`) |
| Settings | `AppSettings`, `KeyChord`, `KeyBindingSet`, `EditorActions`, `PresetLibrary<T>` |

## 1. Scene, entities, layers

* **`Scene`** — name, id, `Layers` (the first is the default layer), root entities, `Settings`, `Version`, `Changed`.
  `Add(entity, parent, index)` adds a detached subtree in one step (one `Added` change; ids are checked before
  anything changes); `Remove` detaches a subtree intact (ready for undo); `Reparent` keeps the world transform by
  default. Entities without a layer take their parent's layer, roots the default layer.
* **`Entity`** — id, name, local `Transform`, `LayerId`, `Visible`, `Locked`, children, components. `WorldMatrix` is
  cached and recomputed lazily, **iteratively** (no recursion: a 100 000-level chain from a hostile file is fine) and
  without allocation. Moving an entity whose subtree is already dirty costs O(1).
* **Detached subtrees** — `Entity.AddChild` / `RemoveChild` on an entity that is not in a scene build or edit a
  detached hierarchy (a paste buffer, an import being prepared), which `Scene.Add` then inserts in one step. On an
  entity in a scene they are `Scene.Add` / `Scene.Remove`.
* **Layers** — `LayerCollection` behaves like a list (`Add`, `Insert`, `Remove`, `Move`, `Clear`, indexer) and keeps an
  id index in sync, so `Scene.FindLayer`, `Layers.Find`, `IsEntityVisible` and `IsEntityLocked` are dictionary lookups
  that never allocate (safe per entity per frame). A file that repeats a layer id resolves to the first such layer.
* **Visibility and locking** — `Entity.IsVisibleInHierarchy` (entity and ancestors), `Scene.IsEntityVisible` (also the
  layer; an unknown layer counts as visible), `Scene.IsEntityLocked` (entity or layer locked).

## 2. Change notifications

Every mutation of a scene raises `Scene.Changed` **after** it was applied and increments `Scene.Version` (never
decreases, also not on undo). Changes made through undo and redo use the same setters, so they are seen the same way.
A detached entity or layer changes silently (it raises once, as `Added` / `LayersChanged`, when it joins a scene).

| Kind | Raised by | `SceneChange` fields |
|---|---|---|
| `Added`, `Removed` | `Scene.Add` / `Remove`, `Entity.AddChild` / `RemoveChild` in a scene | `Entity` = subtree root; `FormerParent` for `Removed` |
| `Reparented` | `Scene.Reparent` | `FormerParent` |
| `Renamed` | `Entity.Name` | |
| `TransformChanged` | `Entity.Local`, `Entity.World` | the subtree's world transforms changed with it |
| `ComponentAdded`, `ComponentRemoved` | `Entity.Add` / `Remove` | `Component` |
| `ComponentChanged` | `Component.NotifyChanged()` (component data are plain properties; `SetValueCommand.ForComponent` and `ModifyComponentCommand` call it) | `Component` |
| `VisibilityChanged` | `Entity.Visible` | the subtree's effective visibility changed with it |
| `LockedChanged` | `Entity.Locked` | |
| `LayerChanged` | `Entity.LayerId` | `FormerLayerId` |
| `LayersChanged` | any `LayerCollection` operation; `Layer.Name` / `Visible` / `Locked` of a layer in the scene | `Layer` (null when several changed at once); `Entity` null |
| `SettingsChanged` | any `SceneSettings` value, assigning `Scene.Settings`, `Scene.NotifySettingsChanged()` | `Entity` null |
| `SceneRenamed` | `Scene.Name` | `Entity` null |

New kinds are only ever appended; consumers switch over the kinds they care about. Setting a value to what it already
is raises nothing. `UnknownProperties` (opaque data of newer versions, §5) is not editable state and changes silently.

## 3. Components and references

Components are plain persisted data; every concrete type is registered on `Component` with a `$type` discriminator
that is part of the file format and never changes (`fixture`, `mesh`, `wall`, `image`, `truss`, `riggingPoint`,
`cable`, `console`, `powerSource`, `atmosphere`, `camera`, `group`).

Every `Guid` member reachable from a component (also in nested data such as cable nodes) carries exactly one
classification:

| Attribute | Meaning | Members today |
|---|---|---|
| `[EntityReference]` | the id of another entity | `FixtureComponent.MountedOn`, `RiggingPointComponent.Carries`, `CableAnchor.EntityId`, `CableEndpoint.EntityId` |
| `[AssetReference]` | an asset of the project | `MeshComponent.ModelAssetId`, `ImageComponent.ImageAssetId`, `TrussComponent.ModelAssetId` |
| `[LocalId]` | an id local to the component, or a reference to one | `CableNode.Id`, `CableSegment.FromNode` / `ToNode`, `CableEndpoint.NodeId` |

Each component rewrites its references in `Component.RemapReferences(EntityReferenceRemapper)`. **Adding a component
or a member:** register the type with a new discriminator, classify every `Guid` member, override `RemapReferences`
when it holds entity or asset references. The tests in `EntityReferenceCoverageTests` reflect over every component
type and fail when a type is not registered, a `Guid` member is unclassified, a member of an unknown shape holds
Candela data, or any entity or asset reference survives a remap — so a reference cannot be forgotten by the copy code.

World-space points held by components (`FixtureComponent.FocusPoint`, `CameraComponent.Target` — the point the view
looks at, world metres) are not references; copies keep them unchanged.

## 4. Copying subtrees: duplicate, copy/paste, merge import

`EntityCloner.Clone(entities, EntityCloneOptions)` deep-copies subtrees — entities with their name, flags, layer,
local transform, unknown properties and exact component copies (nulls included) — with **new ids**, and rewrites the
references inside the copied components through an `EntityReferenceRemapper`: a reference to a copied entity points at
its copy; a reference to any other entity is handled by `ExternalReferences`:

| `ExternalReferenceHandling` | Use | A reference to an entity that was not copied |
|---|---|---|
| `Keep` (default) | Duplicate in the same scene | kept: the copy of a fixture still hangs on the original truss |
| `Clear` | Paste into another scene, merge import | removed: a nullable id becomes null, a list entry is dropped, a cable endpoint is unplugged, a cable node anchor is released **where it is now** (world position expressed in the cable's space) |
| `KeepIfResolvable` | Paste into the scene it may have come from | kept when `TargetScene` has the entity, removed otherwise |

Other options: `TargetScene` (copies never get one of its ids; layer ids it does not have are left empty so
`Scene.Add` assigns the parent's or default layer), `IdFactory` (preferred ids, replaced when taken), `LayerMap`
(merge layer table), `RootsKeepWorldTransform` (a nested entity copied to the root lands where it was),
`RegenerateLocalIds` (cable node ids renewed, segments and endpoints follow; default on), `AssetIdMap` (merge asset
table) and `ComponentCopied(original, copy)` — called for every copied component after remapping, for what Core does not
know (asset texture keys inside material ids, GDTF file names, console fixture ids, world points moved by a placement).

An entity listed together with one of its ancestors is copied once. Nothing fails the whole copy: a component that
cannot be copied, remapped or completed by the callback is left out and reported (`scene.copy-component-failed`);
removed references are summarised (`scene.copy-references-removed`) with the copies that lost them. The result lists
the copied roots, the `IdMap` (all levels), `FindCopy(originalId)`, counters and `ClearedReferences`. The work is
linear: 10 200 entities with 10 000 mounts copy in well under a second.

```csharp
// Duplicate (one undo step; copies go next to their originals)
undo.Execute(new DuplicateEntitiesCommand(scene, selection));

// Copy, then paste (every paste gets fresh ids)
var clipboard = EntityCloner.Clone(selected, new() { RootsKeepWorldTransform = true });
var paste = EntityCloner.Clone(clipboard.Roots,
    new() { TargetScene = scene, ExternalReferences = ExternalReferenceHandling.KeepIfResolvable });
undo.Execute(new AddEntitiesCommand(scene, paste.Roots, description: "Paste"));

// Merge another scene under a new group
var copies = EntityCloner.Clone(source.Roots, new()
{
    TargetScene = target, ExternalReferences = ExternalReferenceHandling.Clear,
    LayerMap = layerTable, AssetIdMap = assetTable, ComponentCopied = TranslateGdtfAndMaterialKeys,
});
var group = new Entity("Imported");
foreach (var root in copies.Roots) group.AddChild(root);
undo.Execute(new AddEntityCommand(target, group));
```

## 5. Undo and redo

Every user edit is an `IUndoableCommand` executed through `UndoStack` (grouping with `Group("…")`, merging of continuous
edits such as gizmo drags and slider drags, capacity 500). Command **descriptions are computed once, when the command is
created**, and stored: menus read them every frame without allocating, and renaming an entity later does not rewrite
the history. Core's commands: `AddEntityCommand`, `AddEntitiesCommand` (several subtrees, one step: paste, import),
`DuplicateEntitiesCommand`, `RemoveEntityCommand`, `ReparentCommand` (restores the sibling position),
`SetTransformCommand`, `SetValueCommand<T>` (any property: names, flags, layers, settings), `SetValueCommand.ForComponent`
(raises `ComponentChanged`), `AddComponentCommand`, `RemoveComponentCommand`, `ModifyComponentCommand` (snapshot-based
in-place edit of any component, exact for nulls), `CompositeCommand`.

`Selection` keeps the selected ids in order (the last is the primary one) with a hash set beside the list, so
`Contains` is O(1) and allocation-free.

## 6. JSON and `scene.json`

`CandelaJson.Options` / `CompactOptions` are the one JSON configuration for project files, presets and settings:
camelCase, enums as strings, vectors as arrays, non-finite numbers as `"NaN"` / `"Infinity"`, polymorphic components with
`$type`, comments and trailing commas accepted.

* **Exact nulls.** Nulls are omitted, except a null whose property default is not null (`ConsoleComponent.ProtocolId`,
  default `"artnet"`): it is written as `null`, because an omitted value reads back as the default. Writing and reading
  back is exact. `Component.Clone`, `CandelaJson.DeepClone` and undo snapshots write every null.
* **Forward compatibility.** Properties this version does not know are kept in `UnknownProperties` of `Scene`,
  `Entity`, `Layer`, `SceneSettings`, every `Component`, `AppSettings` and `KeyBindingSet`, and written back on save, so
  a file from a newer version survives being saved by an older one. They are opaque: never interpreted, copied
  unchanged, never remapped.

`SceneSerializer` writes `SceneFile` (format 1: `formatVersion`, `id`, `name`, `layers`, `settings`, `entities` flat with
parents before children; each record has `id`, `parent`, `name`, `layer`, `visible`, `locked`, `transform`,
`components`). Reading is **tolerant**: `FromJson(json, issues)` reads every layer, entity and component on its own, and
`FromFile(file, issues)` repairs the record. Only text that is not JSON (`JsonException`), a root that is not an object
and a newer format (`InvalidDataException`) throw. The `$type` discriminator may stand anywhere in a component object.

| Damage | Result | Issue code |
|---|---|---|
| an entity that is not an object, a null record | skipped | `scene.load-entity-invalid` |
| missing, empty or repeated entity id | new id (references to a repeated id keep pointing at the first) | `scene.load-id-invalid` |
| parent not listed before the entity | moved to the root | `scene.load-parent-missing` |
| unreadable or non-finite transform | identity | `scene.load-transform-invalid` |
| unreadable value (`"visible": "yes"`, a bad scene id) | default | `scene.load-value-invalid` |
| component without or with an unknown `$type`, damaged component, null | dropped, the rest of the entity loads | `scene.load-component-invalid` |
| unreadable layer | skipped (an empty list keeps the default layer) | `scene.load-layer-invalid` |
| unreadable scene settings | defaults | `scene.load-settings-invalid` |

`Candela.Project` has its own, more thorough reader for project archives (value-level salvage, unknown component types
kept verbatim) and hands the result to `SceneSerializer.FromFile`, so scenes are built the same way.

## 7. Key bindings

`KeyChord.Parse` reads `"Ctrl+Shift+S"`: `+`-separated parts (spaces ignored), modifiers in any order and any case —
`Ctrl` (`Control`), `Shift`, `Alt` (`Option`), `Super` (`Cmd`, `Command`, `Win`, `Windows`, `Meta`) — and **exactly one
key**, kept as written. It rejects, with a message that quotes the offending part: an empty text or empty part
(`"Ctrl++S"`), a repeated modifier, no key, **more than one key** (`"Ctrl+A+B"`) and **unknown modifiers**
(`"Hyper+S"`). `TryParse` returns false for the same inputs; `TryParse(text, out chord, out error)` gives the message
for the key binding editor. `ToString` writes the canonical form (`Ctrl+Shift+Alt+Super+Key`). Key names Candela
documents are listed by `KeyChord.IsKnownKey`; other single key names still parse (the windowing layer may know more).
`KeyBindingSet` skips chords that do not parse (a hand-edited preset still works) and `FindInvalidChords` lists them.
Every chord of the built-in *Default* preset parses back to the chord it was made from.

## 8. Renderer diagnostics: PNG files

`Candela.Rendering.Vulkan.ImageFiles.WritePng` (CLI renders, tests, golden images) writes an RGBA8 image as **RGB**
(PNG colour type 2, a quarter smaller) when every alpha byte is 255 — rendered frames always are — and as RGBA
(colour type 6) otherwise. Readers that ask for RGBA get alpha 255 from an RGB file, so both read back to the same
pixels; bytes beyond `Width × Height × 4` are ignored.

## 9. Tests

`tests/Candela.Core.Tests` (about 185 tests, a few seconds):

* `SceneChangeTests` — every kind is raised once with a version bump, also through undo/redo; detached entities are
  silent; layer list operations keep the index in sync; per-frame lookups allocate nothing.
* `EntityReferenceCoverageTests` — the reflection guards of §3, per component type (remap to copies, keep, clear,
  keep-if-resolvable, asset maps, local ids, malformed data).
* `EntityClonerTests` — duplicate, paste into another scene, keep-if-resolvable, fresh ids per paste, cable node ids and
  released anchors, a scene merged into itself, merge options (layers, assets, completion callback), failure isolation,
  unknown properties and nulls, detached subtrees, the commands, 10 200 entities.
* `SerializationTests` — exact nulls, unknown properties at every level, `$type` anywhere, damaged files with issue
  codes, a 100 000-level hierarchy, world matrices under arbitrary edits without allocation.
* `KeyChordTests`, `CommandTests` (descriptions fixed and allocation-free, selection), `SceneTests`,
  `PresetAndSettingsTests`.

`tests/Candela.Rendering.Vulkan.Tests/ImageFilesTests` checks the PNG colour type, exact read-back, size, padding and
argument validation without a GPU.
