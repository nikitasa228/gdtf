# Candela editor (`Candela.App`)

The desktop editor of Candela: a dockable workspace around Vulkan viewports, built on GLFW, the shared
`VulkanContext` / `VulkanRenderer` of `Candela.Rendering.Vulkan` and Dear ImGui (docking + multi-viewport branch,
through the Hexa.NET bindings). This document describes **phase A** — the shell, workspace, viewports, selection and
editing tools, settings and the headless verification mode. Phase B connects the stage engine (fixture library,
open/save/import/export, real stages); until then every engine operation is routed through one session class that
answers with a clear *"not available yet"* message instead of failing.

![Default layout with a selected object](images/editor-default-layout.png)

*Default layout: Library on the left, the viewport in the centre (move gizmo on the selected sphere), Properties /
Preview / Render on the right, Issues / Console below. The viewport shows the renderer's procedural test scene.*

## Running

```
dotnet run --project src/Candela.App -- [scene] [options]
```

The executable is `candela-app` (`candela-app.exe` on Windows). It needs a GPU with a Vulkan 1.3 driver; on Linux
without a GPU, Mesa's software driver (lavapipe, package `mesa-vulkan-drivers`) works — slowly, but completely.

| Option | Meaning |
|---|---|
| `scene` / `--open <file>` | scene or project to open at start (`.candela`, `.mvr`; phase B — today a "not available" message) |
| `--layout <name>` | start with a layout: `default`, `viewports-4` or a saved layout name |
| `--reset-layout` | ignore the saved workspace and start with the default layout |
| `--scale <1-4>` | UI scale (1 = 100 %), overrides the setting |
| `--language <en\|ru>` | UI language, overrides the setting |
| `--device <name>` | preferred GPU (substring of the device name) |
| `--validation` | enable the Vulkan validation layer |
| `--config-dir <dir>` | folder for settings, layouts, presets and the log of this run |
| `--single-window` | keep torn-off panels inside the main window (no OS windows) |
| `--width <px>`, `--height <px>` | initial window size (default 1600 × 900) |
| `--screenshot <png>` | headless verification: render, save the last frame as PNG and exit |
| `--frames <n>` | frames rendered before the screenshot (default 12) |
| `--script <json>` | replay scripted input first (see [Scripted input](#scripted-input)) |

Exit codes: `0` ok, `1` failure, `2` usage error (bad option, unreadable script), `3` no usable Vulkan device.

`--scale`, `--language` and `--device` apply to the current run only: the settings file keeps its values unless the
setting is changed in the editor during the run. Headless runs (`--screenshot`) never write settings or the workspace.

Per-user files live in the Candela config folder (`%APPDATA%\Candela`, `~/.config/Candela`,
`~/Library/Application Support/Candela`; `CANDELA_CONFIG_DIR` or `--config-dir` override it):

| File | Content |
|---|---|
| `settings.json` | all settings (Core `AppSettings`) |
| `layouts/*.layout.json` | saved layouts; `_workspace.layout.json` is the arrangement restored at the next start |
| `presets/keybindings/*.json` | named key binding presets |
| `keybindings.custom.json` | the edited ("Custom") key bindings |
| `recent-places.json` | recently used folders of the file browser |
| `logs/candela-app.log` | the log of the current run (also shown in the Console panel; at most 16 MB), `candela-app.previous.log` the one before |

## Workspace

The main window holds a menu bar, a status bar and one dock space. Every panel is a dockable window: drag a tab to
another group, to an edge of a group (split) or out of the window. Panels torn out of the main window become **real
OS windows** (Dear ImGui multi-viewport), so they can live on another monitor; `--single-window` keeps them inside.

![Panels torn off as separate OS windows](images/editor-undocked.png)

*Properties and Console torn off as their own OS windows (here on a virtual X screen without a window manager, so the
windows have no OS decorations).*

| Panel | Content |
|---|---|
| Viewport (1–16) | 3D view of the stage, see [Viewports](#viewports) |
| Library | Fixtures / Models tabs, search, category chips — fills with the engine's fixture library in phase B |
| Properties | name, visibility, lock, layer, numeric transform (metres or feet, Euler degrees), components, wall material |
| Preview | real-time quality settings of the viewports (shadows, volumetrics, indirect light, bloom, exposure …) |
| Render | final render settings (resolution, samples, bounces, format, frame range); *Render* asks the session |
| Issues | errors / warnings / notes with severity toggles, category filter and search; click selects and frames; the tooltip shows the whole message and the issue code |
| Console | the editor log with level filter, text filter and auto-scroll |

### Tab styles

Every panel group has two tab styles, switched from the **⋮** button at the right end of the group's tab bar:

* **Tabs on top: icon + name** — the standard tab bar.
* **Icon column on the left** — a narrow vertical column of panel icons (tooltip = name); the selected panel shows
  next to it, an accent bar marks it, dragging an icon tears the panel off.

The menu of the same button lists the panels of the group and can undock or close the current one. The style is
stored per group in the layout (and in the restored workspace).

![Icon-column style and the tab style menu](images/editor-tab-styles.png)

*Properties / Preview / Render (right) and Issues / Console (bottom) in the icon-column style; the tab style menu of
the viewport group is open.*

### Layouts

*Window → Layouts* applies the built-in **Default** layout or **Four Viewports** (top / front / right orthographic
colourless views and a perspective view, see below), and lists saved layouts. *Save Layout As…* stores the current
arrangement under a name (dock tree, open panels, floating windows, tab styles and the viewport settings — view,
projection, shading, overlay, grid, stats); *Delete Layout* removes one; *Reset Layout* (View menu) returns to the
default. The workspace is saved on exit and restored at the next start; a damaged layout file is reported and the
default layout is used instead. Layout files are JSON (`format: 1`) with the Dear ImGui ini text embedded, so they can be
copied between machines. The embedded dock tree is checked before Dear ImGui sees it (unique non-zero node ids, parents
before children, split nodes with exactly two children, one central leaf per tree, sizes within Dear ImGui's 16-bit
fields): Dear ImGui does not report such damage, it aborts the process — every start, since the workspace is restored
each time — so a file that fails the check is treated as damaged.

## Viewports

Each viewport renders into its own render target sized to the panel (in framebuffer pixels, so high-DPI monitors get
full resolution) and shows it as an ImGui image. *View → New Viewport* (Ctrl+Alt+N) adds more viewports (up to 16) as
tabs next to the active one — drag a tab to an edge to see them side by side; each has its own camera, shading, overlay
and grid. Shortcuts and View-menu commands act on the **active viewport**: the one last clicked, or whose tab was
selected (a new viewport becomes active at once). With several viewports on screen it has an orange frame. A viewport
hidden behind another tab of its group is never the target — the commands then go to a visible one.

![Four viewports, Russian UI](images/editor-four-viewports-ru.png)

*Four Viewports layout in Russian: top, front and right orthographic views (colourless) plus perspective, all showing
the selection and the gizmo; the perspective view is the active one (orange frame). The view cube of each orthographic
view shows the snapped face, the arrows to its neighbours and the caption.*

**Toolbar** (top left; compact icons when the viewport is narrow): select / move / rotate tool, local / world space,
snapping, shading (Wireframe, Colorless, Full — the menu's last entry, *Viewport Settings…*, opens the Preview panel
with the real-time quality settings of the Full mode), projection (perspective / orthographic), overlay (None, Lux,
Noise, Cables), grid, statistics. When even the compact toolbar is wider than the view (four views at a large UI
scale), the view options move to a second row, so every button stays reachable.

**View cube** (top right, beside the toolbar when there is room, otherwise below it; it shrinks in small views
instead of disappearing): the cube turned like the scene; clicking a face snaps the camera to that orthographic view
(Top, Bottom, Front = from the audience, Back, Left, Right). Looking straight at a face, the cube shows only that face,
so four arrows around it lead to the neighbouring views (from Top: up = Back, down = Front, left / right = Left /
Right) — every view is one or two clicks away without orbiting. The caption under it names the current view.
Orbiting leaves the snapped view.

![View cube](images/editor-view-cube.png)

*The view cube in a perspective view, and snapped to Top, Front and Right with the neighbour arrows.*

**Statistics** (bottom left): frames per second, frame time and target size; the world axis triad above it.

**Navigation** follows *Settings → Viewport → Navigation style*. The left button without modifiers always selects.

| Style | Orbit | Pan | Zoom | Fly (W/A/S/D, Q/E down/up, Shift faster) |
|---|---|---|---|---|
| Candela (default) | MMB, Alt+LMB | Shift+MMB, Alt+Shift+LMB | wheel (towards the cursor), Ctrl+MMB drag | RMB |
| Blender | MMB | Shift+MMB | wheel, Ctrl+MMB drag | Shift+RMB |
| Maya | Alt+LMB | MMB, Alt+MMB | wheel, Alt+RMB drag | RMB |

Double-click focuses the camera on the point under the cursor; *F* frames the selection (or the whole stage); an object
without geometry (a light, a group) is framed as a 1 m box around its origin.
The camera works in metres with Z up; orthographic views keep the apparent size when switching projection, also when fly
mode (which needs a perspective eye) starts from an orthographic view. The far plane follows the zoom-out limit, so the
stage never disappears when zooming far out. A navigation, box selection or gizmo drag ends when its viewport stops being
drawn (its tab covered, the panel closed) — fly mode suspends the shortcuts, and they would otherwise stay off.

## Selection and editing

* **Click** selects the object under the cursor (through the renderer's pick buffer), a click on the background clears
  the selection; **Shift+click** adds or removes; **drag** draws a selection box (Shift adds) that selects the objects
  lying *entirely* inside it — a box drawn around props does not also pick up the floor or the walls behind them.
  Hidden and locked objects, and objects on hidden or locked layers, are never selected.
* **Gizmos** (ImGuizmo): *Move* (W) and *Rotate* (E) in world or local space; with several objects selected they move
  rigidly around the primary (last selected) object. An object selected together with its parent moves once, with the
  parent; a selected child of a locked parent moves on its own. Locked objects and objects on locked layers have no
  gizmo, and their transform fields in the Properties panel are read-only. **Snapping** (toolbar or Alt+S, Ctrl inverts
  it while dragging) uses half the scene grid spacing for moves and 15° for rotations. Each viewport has its own gizmo
  id, so a drag in one of several views showing the same selection moves it once. The rotation fields keep the Euler
  angles as entered (a Y of 95° stays 95° instead of flipping to the equivalent −170° / 85° / −160°, and a drag on Y
  continues past ±90°) until the rotation is changed elsewhere.
* **Undo**: every edit goes through Core's `UndoStack`. A whole gizmo drag, or a whole drag on a numeric field, is
  *one* undo step (consecutive transform commands merge until the drag ends); Undo and Redo wait until a drag in a
  viewport is finished (the drag would re-apply its transform at once and the redo history would be lost). Delete,
  Duplicate, Cut, Copy and Paste work on the top-level selected objects (children follow their parents) and are single
  steps too.

## Menus

| Menu | Items |
|---|---|
| File | New, Open…, Open Recent, Save, Save As…, Import (Candela project, 3D model, MVR scene, image), Export (MVR scene), Render…, Settings…, Exit |
| Edit | Undo / Redo (with the step name), Cut, Copy, Paste, Duplicate, Delete, Select All, Deselect |
| View | Shading, Projection, Camera views, Overlay, Grid, Statistics, View cube, Frame selection, New viewport, Reset layout |
| Tools | Select, Move, Rotate, Wall, Floor, Cable (the drawing tools arrive with the engine), Snapping, Local/World space |
| Window | open/close every panel, Layouts (Default, Four Viewports, saved, Save Layout As…, Delete Layout), Reset |
| Help | Keyboard shortcuts, Documentation, About |

All file dialogs use the editor's **portable file browser** (the same on Windows, Linux and macOS): standard places
and drives, recent folders, a path bar, sortable name / size / date columns with folders first and natural number
order, type filters, hidden files toggle, and an overwrite confirmation when saving. When saving, the file name field has
the keyboard as soon as the dialog opens; the typed name is used by Enter and by the Save button alike (the
extension of the selected type is added when missing). Only the visible rows of a folder are drawn and their texts are
built once per listing, so folders with tens of thousands of files scroll as smoothly as small ones. A folder that
cannot be read says so (the previous listing stays) instead of looking empty; symbolic link loops and broken links are
listed but refused when opened; `~` in the path bar is the home folder.

![File browser](images/editor-file-browser.png)

File operations that need the stage engine go through `EditorSession`, the only class that will talk to
`StageEngine` in phase B. Today they change nothing and say so — in a message box, the status bar, the log and as a
note in the Issues panel:

![Not available yet](images/editor-not-available.png)

## Keyboard shortcuts

Shortcuts are commands bound to key chords (Core `KeyBindingSet`); menus show the current chords. The defaults:

| Action | Keys | Action | Keys |
|---|---|---|---|
| New / Open / Save | Ctrl+N / Ctrl+O / Ctrl+S | Save As | Ctrl+Shift+S |
| Import | Ctrl+I | Render | F12 |
| Settings | Ctrl+, | Exit | Ctrl+Q |
| Undo | Ctrl+Z | Redo | Ctrl+Y, Ctrl+Shift+Z |
| Cut / Copy / Paste | Ctrl+X / Ctrl+C / Ctrl+V | Duplicate | Ctrl+D |
| Delete | Delete | Select all / Deselect | Ctrl+A / Escape |
| Select / Move / Rotate tool | Q / W / E | Wall / Floor / Cable tool | B / Shift+B / C |
| Snapping / Local-world space | Alt+S / Alt+X | Frame selection | F |
| Top / Front / Right view | Num 7 / 1 / 3 | Bottom / Back / Left view | Ctrl+Num 7 / 1 / 3 |
| Perspective ↔ orthographic | Num 5 | Grid | Alt+G |
| Wireframe / Colorless / Full | Alt+1 / Alt+2 / Alt+3 | Overlay None / Lux / Noise / Cables | Alt+0 / Alt+L / Alt+N / Alt+C |
| New viewport | Ctrl+Alt+N | Keyboard shortcuts | F1 |

While a text field has the keyboard, plain keys type into it and the text-editing chords (Ctrl+A / C / V / X / Z /
Y, Ctrl+arrows …) edit the text — they do not select, copy, paste or undo objects; other chords with Ctrl / Alt /
Super (Ctrl+S, Ctrl+O …) still work. **Escape**
first closes whatever is open — a menu, a combo box, a dialog, the Settings / Keyboard Shortcuts / About window, a
shortcut being recorded — and only a press with nothing open clears the selection. Menus and the shortcut editor show
punctuation and keypad keys as `Ctrl+,` and `Num 7` (stored in the binding files as `Ctrl+Comma`, `Numpad7`).

## Settings

*File → Settings…* (Ctrl+,) — changes apply immediately and are saved after a short delay (atomic file replace; a
damaged settings file is kept aside as `settings.json.damaged-<time>` and defaults are used).

| Section | Settings |
|---|---|
| Interface | UI scale (100–400 %, multiplied with the monitor's DPI scale), font size, language (English / Русский), theme |
| Keyboard | key binding editor: search, click a chord to record a new one (the recorded press does not run the command), add / remove chords (right-click a chord), conflict warnings; presets *Default*, saved names and *Custom* (Core `PresetLibrary` over a `FilePresetStore` in `presets/`; any edit switches to *Custom*), Save as…, Delete, Reset to default |
| Viewport | navigation style (Candela / Blender / Maya, with a summary of its mouse bindings), zoom speed, display units (metric / imperial), grid spacing (also the move-snapping step), rotation snapping step |
| Library | library folders (add with the folder browser, remove; missing folders in red) |
| Network | network interface for Art-Net / sACN, Art-Net universe offset (internal universe = Art-Net port-address + offset; 1 or 0, see `protocols.md`) |
| Files | autosave interval (off – 60 min), recent files count |
| GPU | current device and driver, preferred GPU, Vulkan validation (on restart) |

![Settings: interface](images/editor-settings-interface.png)
![Settings: keyboard](images/editor-settings-keyboard.png)

## Look and language

* Dark theme with an orange accent; panel content uses a two-column property grid (labels left, editors right) whose
  label column grows to fit the longest (translated) label. The X / Y / Z fields of the transform sit side by side and
  stack one per line when the panel is too narrow for the numbers (large UI scale, icon-column groups).
* Font: **Inter** Regular / SemiBold (Latin, Cyrillic, Greek; SIL OFL) with the **Lucide** icon font (ISC) merged in,
  embedded in the assembly; the licences are copied next to the executable (`licenses/`). Dear ImGui 1.92 rasterises
  glyphs on demand, so changing the UI scale or font size is instant and stays sharp.
* Language: English and Russian; every user-visible string is a `Str` key with both texts in `Ui/Loc.cs` (tests check
  that every key has both and that placeholders match), including the issue categories of Core. Menus and panel titles
  switch immediately.
* DPI: the UI scale is *user scale × monitor content scale*; windows moved between monitors adapt.

## Engine-backed panels

Properties, External Control, Preview, Render and the overlay legends of the viewports work on the open stage: fixture
state from the DMX engine, manufacturer data from the GDTF file and the curated `_specs` sheets, connectors and cables
from the electrical model, materials from the material library, protocols, timecode and the DMX recorder from the stage
engine. Every edit of the stage (manual values, patch, walls, models, scene settings) is one undo step — a slider drag is
one step, not one per frame. Without an open stage the panels show an empty state. The pictures show
`stages/Фокина.mvr` with a wooden stage floor added and the *Open white* look applied.

| Panel | Content |
|---|---|
| Properties | current state of a fixture, manual controls for every attribute of its mode, DMX patch, manufacturer data, connectors with the cable in each port; size and material of walls, floors and models |
| External Control | the fifteen show-control protocols with support level, settings and live status; universe activity and 512-slot view; sources; timecode; merge mode; Art-Net test show; DMX recorder |
| Preview | real-time quality of every viewport, exposure (with a light meter), haze, ambient light and grid of the stage; presets |
| Render | still frames or image sequences of a timecode range at the highest real-time quality; presets |

### Properties

![Fixture: current state and manual controls](images/editor-properties-fixture.png)
![Fixture: patch, manufacturer data and connectors](images/editor-properties-type.png)

*Left: a Rivale fixture under manual control. Right (scrolled): its DMX patch — two overlapping fixtures are listed in
red —, the manufacturer data and the connectors, each drawn as the real connector.*

**Current state** sums up what the fixture emits right now: intensity (%), peak luminous intensity (cd), luminous flux
(lm), colour as a swatch with CIE 1931 *x y* and the correlated colour temperature (K), pan and tilt (°), beam and field
angles (°), focus, iris and frost (%), and — when present — strobe frequency (Hz), gobo slot, prism facets and the
fog / haze / fan output. A line above it says **who drives the fixture**:

* *Manual control* — no live DMX: the controls below set the fixture.
* *Locked: universe N receives live DMX* — an Art-Net / sACN / EntTec source sends the fixture's universe (see
  [External Control](#external-control)).
* *Locked: driven by console …* — a console in the scene feeds the fixture through a DMX cable.
* *The fixture type could not be resolved* — with the reason (missing or broken GDTF file, unknown mode).

While locked, the controls are disabled and show the values the console or DMX source sets; the tooltip repeats the
reason.

The **controls** cover every attribute of the fixture's mode, grouped *Intensity, Position, Colour, Beam, Gobos and
effects, Shapers, Atmosphere* and *Other channels* (collapsed), the main attribute of each group first (dimmer, pan,
zoom …), framing blades in blade order. Values are physical: degrees for pan, tilt, zoom and rotations, % for levels,
kelvin for colour-temperature filters, Hz for strobes, the GDTF unit otherwise; wheels are drop-downs of their slots
with the slot names. A control whose channel currently runs another function shows "—". Setting a control writes a
*manual value* into the fixture (`FixtureComponent.ManualAttributes`, saved with the project); manual values carry an
orange bar at the left edge; right-click one to clear it, *Reset manual values* clears them all. A slider drag is one
undo step; every discrete change — a wheel slot, a cleared value, a mode, a fixture ID, an address, a material, the
shadow switch — is a step of its own. With several fixtures
selected, a change applies to every selected fixture that has the attribute and is not driven by a console or live DMX
(the panel says how many); the panel shows the primary fixture.

**DMX patch**: fixture ID, DMX mode, universe and address of each DMX break (± buttons step by one, typing jumps), the
resulting slot range, and the patch problems of this fixture from the Issues panel (overlaps, footprints running past
slot 512). Changing the mode re-reads the fixture; every change is one undo step.

**Fixture type**: manufacturer, model, category, light source, weight, power (with power factor), luminous intensity,
flux, beam and field angle (zoom range), colour temperature, CRI, colour mixing, noise level, IP rating, supply (voltage,
frequency, connector), pan / tilt range, operating temperature, features, control protocols and the number of DMX modes
— taken from the curated specification sheet when there is one, else from the GDTF file and the electrical profile. A
note says when a fixture was matched to a substitute type (an MVR fixture whose exact GDTF is not in the library).
Every value says where it comes from (tooltip: the GDTF file, derived from the GDTF data, the manufacturer's datasheet,
or an estimate); **estimated values** — the `_source` of the `_specs` record is `estimated`, e.g. most noise levels and
many peak intensities, or lamp power standing in for the mains draw — are shown dimmed with a leading **≈**, and the
datasheets the researched values come from are listed below the table. The power factor is shown only when the GDTF
or the specs declare one (the 0.95 the electrical model assumes otherwise is not data). Levels are percentages whatever
unit the GDTF gives them (a GDTF `Dimmer` declares *LuminousIntensity* with physical values 0 … 1: that is 0 … 100 %,
not candela).

**Connectors**: every data and power port of the fixture with a vector drawing of its connector (powerCON / TRUE1,
XLR 3/5-pin, RJ45 / etherCON, Schuko, IEC, CEE, Socapex, Harting … — 38 drawings, a neutral socket for anything else),
signal and direction, current ratings and breakers, and the cable plugged in (label, type, length, the cable colour).
Clicking a cable asks the cable editor to open it (`StageToolState.CableLinkRequested`); without a listener the cable
is selected.

![Floor: size, material and preview](images/editor-properties-floor.png)

![Свойства прибора, русский интерфейс, 150 %](images/editor-properties-ru.png)

*The same panel in Russian at 150 % UI scale (unit symbols follow the language: кд, лм, Вт, дБ(А) …).*

**Walls and floors**: kind, width, height / depth, thickness, area, the material (drop-down grouped by category with
colour swatches; a lit sphere preview with the material's description, luminous reflectance ρ and roughness) and the
texture scale. **Models**: size from their bounds, a material override per material slot, shadow casting. Several
selected walls or models are edited together.

### External Control

![Art-Net test show driving the rig](images/editor-artnet-test.png)

*The Art-Net test show playing to this computer: the External Control panel shows the received timecode, Art-Net
running at 320 packets/s and the status of the receiver; the selected fixture is locked to the live DMX and its current
state follows the show.*

The header shows the **last timecode** received with its source (*Art-Net* ArtTimeCode, or the *DMX recording* being
played), its frame rate and *live* or how long ago, the **merge
mode** of the DMX input hub (*HTP*: highest level per slot wins; *LTP*: the latest change wins — for two consoles on one
universe) and **Send Art-Net test** (below). The tabs:

* **Protocols** — all fifteen protocols of `Candela.Protocols` with their support level and live status (packets per
  second). Selecting one shows its description, why it has that support level, its settings and its status (listening
  address, errors, packet counters; discovered Art-Net nodes; the Eos connection). The tick box starts or stops it;
  protocols that are only planned or need a vendor SDK cannot be started.

  | Protocol | Support | Settings |
  |---|---|---|
  | Art-Net 4 | implemented | network interface, universe offset, port (6454), ArtSync, broadcast ArtPollReply |
  | sACN (E1.31) | implemented | port (5568), multicast join, preview data, unpatched universes |
  | EntTec DMX USB Pro | implemented | serial port, universe |
  | OSC | implemented | port (8000) |
  | PosiStageNet | implemented | port (56565) |
  | ETC Eos | partial | OSC output port (8001) |
  | BlackTrax, CITP, Compulite VC, Kinesys, LasergraphDSP, PangolinLD | planned | — |
  | MA-Net2, MA-Net3, NDI | needs the vendor's SDK | — |

* **Universes** — a tile per universe: green while it receives data (brighter = more packets per second), outlined when
  the patch uses it. Click a tile for its 512 slots with their values; patched slots are highlighted and the tooltip
  names the fixture and attribute.
* **Sources** — every sender: protocol, name (Art-Net short name, sACN source name), IP address, universes, priority,
  packets.
* **Recorder** — records the merged DMX input with the timecode as it arrives; plays it back (the fixtures follow the
  recording as they would a console), seeks by position or by timecode, saves and loads `.cdmx` files. A recording is
  what the Render panel renders a timecode range from.

![Universes and recorder](images/editor-external-universes.png)

![Внешняя связь, русский интерфейс, 150 %](images/editor-external-control-ru.png)

*External Control in Russian at 150 % while the test show runs: universe activity and the slot values of universe 1.*

The protocol settings are stored in `external-control.json` in the config folder (enabled protocols, ports, options,
merge mode, test target); the network interface and the Art-Net universe offset are the ones of *Settings → Network*.

#### Acceptance path: a console over Art-Net

1. **Open the stage** — *File → Open* `stages/Фокина.mvr` (or a project). The fixtures keep their MVR patch; the
   Properties panel shows each fixture's universe and address.
2. **Enable Art-Net** — *External Control → Protocols*, tick **Art-Net**. It listens on UDP 6454 on the interface of
   *Network interface* (*All interfaces* by default; pick the lighting network's adapter when there are several) and
   answers ArtPoll, so consoles list the computer as the node **Candela** with the universes the patch uses. *Art-Net
   universe offset* maps console numbering to Candela's: with offset 1, Art-Net port-address 0:0:0 is universe 1.
   Allow inbound UDP 6454 in the computer's firewall.
3. **Point the console at the computer** — on the console, output Art-Net on the universes of the patch, unicast to the
   computer's address or broadcast on its subnet (2.x.x.x / 10.x.x.x networks work the same). The **Sources** tab lists
   the console with its IP address and universes, the **Universes** tab turns green.
4. **Fixtures follow** — the console now drives every fixture on those universes: the viewports show the beams, the
   Properties panel says *Locked: universe N receives live DMX* and its current state follows the console. With two
   consoles on one universe the merge mode decides (HTP / LTP).
5. **Timecode** — when the console (or a show controller) sends ArtTimeCode, the header shows it running. Record the
   show in the **Recorder**, save it, and render a timecode range of it in the Render panel later — without the console.

Without a console, **Send Art-Net test** does the same over the network: it encodes a moving show for the stage's own
patch (pan, tilt, dimmer, colour, zoom and focus mapped onto each fixture's channel functions from its GDTF) with a
running timecode and sends it as a separate Art-Net console to the test target (127.0.0.1 = this computer; Art-Net is
switched on for it). When *Network interface* names one adapter, "this computer" is reached at that adapter's address
instead of 127.0.0.1 (a receiver bound to one adapter, as on Windows, does not see loopback datagrams). Sending to
another address feeds a second Candela or any Art-Net node. An empty stage gets a demo rig's show.

### Preview

![Preview and Render panels](images/editor-preview-render.png)

*Preview (left) and Render (right) with their preset drop-downs.*

**Quality**: resolution scale, shadows (map size, lights with shadows), volumetric light (volume slices, sharp beams),
temporal accumulation, indirect light (off / reflective shadow maps / ray traced). **Image**: bloom (intensity), tone
mapper, automatic exposure or exposure compensation (EV) and camera exposure (EV100) with **Meter**, which sets the
exposure from the engine's light meter (incident light at the stage's centre), applied to every viewport. **Stage**
(scene settings, undoable, saved with the stage): haze density (scattering coefficient, m⁻¹), haze forward scattering,
ambient light (cd/m²), grid and grid spacing.

The **preset drop-down** (also in Render and in the legends) lists *Default* (read-only), *Custom* (the edited values,
shown as soon as anything differs from the chosen preset) and saved presets; the save button stores the current
values under a name, the bin deletes the chosen preset (the values in use stay; the drop-down then shows *Custom*). Presets are Core `PresetLibrary` files in
`presets/preview/`, `presets/final-render/`, `presets/lux-gradient/` and `presets/noise-gradient/` of the config folder;
the current values are saved in the project (`settings.json`, section `view`) and come back when it is opened.

### Render

**Output**: folder (default `Pictures/Candela`), file name and format (PNG; EXR needs the path tracer and is disabled
with that reason). **Quality**: resolution (with common sizes) and samples per pixel, tone mapper; the panel shows the
plan, e.g. *2× supersampling, 64 accumulated frames → 1920 × 1080 PNG*. **Frame or timecode range**: a single frame, or
start and end (timecode `HH:MM:SS:FF` or seconds) and frame rate of the DMX recording. **Path tracer**: bounces, volume
bounces and denoising, kept for the final renderer.

Final path tracing is not available yet, and the panel says so: **Render uses the real-time renderer at its highest
quality** — 2048-texel shadow maps for up to 256 lights, 256 volume slices, ray-marched beams, indirect light on — and
renders at twice the resolution in each direction (when the target stays within 8192 pixels) for `samples ÷ 4` frames
of temporal accumulation (up to 256), averages the last four frames read back and filters them down to the output
size. A **timecode range** renders an image sequence
(`name_0001.png`, …): the job takes over the engine clock (the editor stops ticking the stage with wall-clock time),
plays the recording from two seconds before the start so moving heads and fades have arrived, and for every frame seeks
the player, ticks the engine by one frame period and renders. The job runs a slice per editor frame (about 60 ms) with
a progress bar and *Cancel*, so the editor stays usable; errors (folder not writable, disk full, a GPU error such as a
lost device or a target too large for the GPU) stop it with the reason, and a cancelled or failed sequence stops the
recording playback it started.

### Overlay legends

![Illuminance overlay with its legend](images/editor-overlay-lux.png)
![Cables legend](images/editor-overlay-cables.png)

With the *Illuminance* or *Noise* overlay, the viewport shows a legend in its lower-right corner: the colour gradient
with its stops and range. Click the bar to add a stop, drag stops to move them, drag the range brackets to set the
minimum and maximum, right-click a stop to recolour or remove it; the chevron expands numeric fields (from, to,
logarithmic scale, the value of the selected stop). Values outside the range are drawn grey. Each gradient has presets
like the Preview panel, applies to every viewport and is saved in the project. The *Cables* overlay shows the colour of
each cable type (those in the scene, or the whole catalogue while there are none).

### Implementation

```
src/Candela.App/
  Session/  StageToolState (StagePanelContext: what the panels see; per-frame update of presets, external control and
            render job), PresetBinding / SettingsCopy (live settings ↔ preset library ↔ project), FixtureControls
            (attributes of a mode → controls, live state), ManualAttributeCommand, SetFieldCommand, DeviceFacts
            (manufacturer rows, ports and cables), ExternalControl (protocol settings, status polling, test show,
            recorder), RenderJob (RenderJobRunner: plan, supersampling, accumulation, timecode sequences)
  Panels/   StagePropertiesSection → FixtureInspectorView, DeviceFactsView, SurfaceInspectorView; ExternalControlPanel;
            PreviewPanel; RenderPanel
  Viewport/ OverlayLegend (legend window of a viewport)
  Ui/       StageStrings (StageStr / StageLoc: English and Russian texts of these panels), StageUnits (unit symbols in
            the UI language), StageIcons, ConnectorIcons (vector connector drawings), GradientEditor, PresetCombo,
            MaterialSwatch, AttributeLabels, LabelColumn
```

The shared shell only hosts them: `Workspace` adds the External Control panel and calls `StageToolState.Update` before
each frame, `EditorSession` exposes the engine and `EngineClockHeld`, `ViewportPanel` draws the legend, the Properties
panel calls `StagePropertiesSection`. Their steady-state frames do not allocate: texts are rebuilt when the values
behind them change (current state at most ten times a second, protocol status four times a second), labels once per
language.

Tests: `FixturePropertiesTests` (controls of a moving head in physical units, ordering, manual values and undo,
the lock by a console cable and by live DMX, multi-selection, patch changes, manufacturer rows, ports and the cable in
a port, every connector drawing), `ExternalControlTests` (the acceptance path end to end on the loopback interface — an
Art-Net console sends DMX and timecode, the source, universe activity and fixture state appear —, the test show,
recording / playback / seeking / saving, settings files, timecode parsing), `PresetsAndOverlayTests` (presets, Custom,
project adoption, gradient editing rules, legends, Preview / Render / legend drawing under a window-less ImGui context)
and `RenderJobTests` (the plan, a frame and a timecode sequence rendered on a headless Vulkan device and checked
pixel by pixel).

Limitations: final path tracing and EXR output wait for the path tracer; the recorder samples the input once per editor
frame; status texts of the protocols come from `Candela.Protocols` in English; the cable editor that a cable link opens
comes with phase B3.

## Robustness

* No exception escapes the frame loop: a failing frame is logged and reported in the Issues panel and the next frame
  runs; after 120 consecutive failures the editor stops with exit code 1. Dear ImGui aborts the process (a native
  assertion) when a frame is left open, so the UI frame is always ended — the window, ID and style stacks are unwound
  with Dear ImGui's error recovery — before an exception reaches the loop. Every top-level part of the UI runs under
  its own guard: a failing panel shows its error with a *Retry* button instead of its content, a failing dialog
  (settings, file browser, message, about, shortcuts) closes, a failing menu or status bar only loses that frame's
  drawing, and the rest of the UI keeps working.
* An error that repeats (every frame, or every time a dialog opens) is logged in full once and then counted (at 10,
  100, 1000 … occurrences), and appears once in the Issues panel (at most 200 different program messages; the panel's
  clear button really empties the list and lets them be reported again). The log file holds the current run and stops
  at 16 MB; the previous run is kept as `candela-app.previous.log`.
* Swapchain loss, resize and minimise are handled (the loop idles while minimised); GPU objects of closed viewports
  are released after the frames that used them.
* Without a Vulkan device the editor explains what to install, in a small OpenGL window (or on standard error in
  headless runs) and exits with code 3:

  > No Vulkan graphics device: Candela needs a GPU with a Vulkan 1.3 driver. Install or update the graphics driver
  > (AMD, NVIDIA, Intel), or on Linux without a GPU install Mesa's software Vulkan driver (mesa-vulkan-drivers,
  > lavapipe).

* Settings, layouts, presets and the recent-places list are written atomically and read defensively. A damaged
  settings file is moved aside once (`settings.json.damaged-<time>`), not copied again at every start; headless runs
  leave it untouched. Key binding files with missing tables, null or empty chords are repaired when read (a custom set
  saved by an older version also gets the newer actions' defaults). Missing library folders are detected in the
  background, so a network share that does not answer never freezes the settings window.
* The Vulkan validation layer (`--validation`) reports no errors for start-up, a session with window resizes, panels
  torn off into OS windows and docked back, new and closed viewports and UI scale changes, and shutdown (also with OS
  windows still open); an integration test runs such a session.

## Headless verification

`--screenshot` renders the real UI (window, swapchain, ImGui backends, viewports) for `--frames` frames, copies the
last swapchain image and writes it as PNG. Under Linux without a display, run it through Xvfb:

```
xvfb-run -a -s "-screen 0 1920x1080x24" dotnet candela-app.dll --screenshot out.png --layout viewports-4 --language ru
```

With multi-viewport, panels torn off into OS windows are not part of the main swapchain image; use `--single-window`
to keep them in the screenshot, or grab the whole X screen (`import -window root`) as for the picture above.

### Scripted input

`--script <file.json>` replays input before the screenshot (and also in interactive runs). Actions run frame by frame;
mouse and key events are queued into Dear ImGui exactly like real input, so a scripted click exercises the same code as
a real one. Coordinates are pixels in the main window.

```json
{ "actions": [
    { "type": "wait", "frames": 3 },
    { "type": "click", "x": 763, "y": 365 },
    { "type": "screenshot", "path": "selected.png" },
    { "type": "drag", "x": 830, "y": 365, "toX": 950, "toY": 365, "steps": 6 },
    { "type": "key", "chord": "Ctrl+Z" },
    { "type": "menu", "path": "File/Import/MVR Scene" }
] }
```

| Type | Fields | Effect |
|---|---|---|
| `wait` | `frames` | do nothing for n frames |
| `move` | `x`, `y` | move the mouse |
| `click` | `x`, `y`, `button` (left/right/middle), `modifiers` ("ctrl+shift"), `double` | click |
| `drag` | `x`, `y`, `toX`, `toY`, `steps`, `button`, `modifiers` | press, move in steps, release |
| `scroll` | `x`, `y`, `delta` | mouse wheel |
| `key` | `chord` ("Ctrl+Shift+S") | press and release a key chord |
| `text` | `text` | type text into the focused field |
| `menu` | `path` ("File/Settings", "/" or ">" separated; labels in either language, ellipsis and icons ignored) | select a main-menu item |
| `action` | `id` ("view.frameSelection") | run a command |
| `panel` | `panel`, `open` | open or close a panel |
| `undock` | `panel`, `x`, `y`, `width`, `height` | tear a panel off into a floating window |
| `tabStyle` | `panel`, `style` (TopTabs / LeftIcons) | set the tab style of the panel's group |
| `viewport` | `viewport`, `view`, `projection`, `shading`, `overlay`, `grid` | configure a viewport |
| `select` | `names` | select objects by name |
| `layout` | `name` | apply a layout |
| `setting` | `name` (uiScale, fontSize, language, navigation, settingsSection), `value` | change a setting |
| `window` | `width`, `height` (screen pixels), or `value` (minimize, restore, maximize) | resize the main window or change its state (swapchain recreation) |
| `screenshot` | `path` | save the current frame (in `--screenshot` runs) |

## Architecture

```
src/Candela.App/
  Program.cs, AppOptions.cs        command line, exit codes
  Shell/      EditorApplication (start-up, frame loop, shutdown), GlfwPlatform, MainWindow, GpuDevice,
              ImGuiHost (context, backends, textures), SwapchainCapture, StartupFailureScreen, FrameTimer,
              Workspace (+ .Menus: panels, commands, menus, layouts, dialogs), MenuScope, WorkspaceContext
  Docking/    DockWorkspace (dock space, presets, tab styles, icon column), Panel, TabStyleMap, LayoutFile, LayoutStore
  Viewport/   ViewportPanel, ViewportCamera, ViewCube, NavigationStyles
  Editing/    SelectionController, EditOperations, TransformMath, SceneLookup
  Session/    EditorSession (the single gateway to the engine), PreviewStage, SessionResult
  Input/      key names, commands, KeyBindingMap / dispatcher, KeyBindingsService, InputScript, ScriptPlayer
  Files/      FileFilter, FileBrowserModel (pure logic), FileBrowserDialog (UI)
  Panels/     Library, Properties, Issues, Console, Render, Preview
  Windows/    SettingsWindow, message boxes, About, Shortcuts
  Settings/   SettingsStore
  Ui/         Theme, FontSet, Icons, Loc / Str, Labels, PropertyGrid, UiScale
  Diagnostics/AppLog
```

**Frame order** (main thread): poll events → session update → workspace *BeforeFrame* (pending layout ini, viewport
pick queries of the last frame) → scripted input → ImGui NewFrame → workspace draw (menus, status bar, dock space,
panels, dialogs, shortcut dispatch) → ImGui Render → GPU frame: viewports sync and render into their targets →
swapchain acquire → ImGui draw data → (screenshot copy) → present → platform windows.

Picking runs outside the GPU frame (the renderer's `Pick` reads the pick buffer of the last rendered frame), so a click
is answered on the next frame. The scene, selection and undo stack are Core types (`Scene`, `Selection`, `UndoStack`)
owned by `EditorSession`; in phase B the session takes them from `StageEngine` instead and `PreviewStage` gives way to
the engine's render synchronisation.

**Steady-state frames do not allocate** on the managed heap in the editor's own code: labels are built once per
language (`Labels`), status texts and statistics only when their values change (statistics at most four times a
second), commands are cached delegates, and the preview stage re-derives instance flags without LINQ. Allocations
happen on user actions (edits, opening menus and dialogs).

## Tests

`tests/Candela.App.Tests` (xUnit, about 400 tests, about a minute) covers everything that does not need a window:
camera math (orthonormal basis, axis views, pan / zoom-to-cursor keeping the point under the cursor, framing, rays),
the view cube (projection, hit testing, snapping to the six views, neighbour arrows, no allocations), navigation
styles, selection rules (including box selection of enclosed objects only), edit operations and undo grouping, gizmo
drag merging, Euler angles, key binding maps / presets / dispatch and their display names (through a window-less Dear
ImGui context: Escape that closes a menu, dialog or window, and a chord just recorded, never also run a shortcut),
the choice of the active viewport, docking (default and four-viewport presets, icon-column style, layout and tab-style
round trip through a saved file, loading a layout while running), layout files and store, the file browser model and
filters, command-line and script parsing, settings robustness, localisation completeness (issue categories too),
layout rules found by exercising the UI (menus opening towards the window's inside, the property grid's label column,
transform fields stacking instead of cutting numbers, the file dialog fitting without scrolling), the editor session,
and the preview stage on a real (headless) Vulkan renderer with picking. `RobustnessTests.cs` tries to break the
editor: exceptions inside a UI frame and inside a modal dialog, damaged dock trees (each of which aborted or hung Dear
ImGui), repeated failures, log size and rotation, damaged settings and key binding files, gizmo drags of parent +
child, locked layers and rotations around the pivot, the far plane at the zoom limit, fly mode from an orthographic
view, pick pixels at framebuffer scales 1 / 1.25 / 2 and at the image edges, the file browser on unreadable folders,
link loops, broken links, Unicode names and a 20 000-file folder (allocation per frame measured), background folder
checks, Euler angle display and framing of objects without geometry. Integration tests start the real editor under
`xvfb-run`: the default screenshot must be a real frame, a scripted click must select an object and change the
Properties panel (and Escape must clear it), shortcuts must work after fly mode was interrupted by a tab switch, a
session with resizes, tear-offs and new viewports under `--validation` must report no validation error, and a run
without a Vulkan driver must exit with code 3. They are skipped when `xvfb-run` or a Vulkan device is missing.

## Limitations of phase A

* No stage engine yet: the Library is empty, New / Open / Save / Import / Export / Render only report that they need
  the engine, the drawing tools (wall, floor, cable) are disabled, and the viewports show the renderer's procedural
  test scene as a stand-in stage (its objects can be selected, moved, rotated, duplicated and deleted).
* File dialogs are the editor's own browser on every platform (no native OS dialogs).
* Multi-object editing in the Properties panel edits the primary object; the gizmo moves all selected objects.
* Autosave is configurable but saves nothing until the engine can write projects.
