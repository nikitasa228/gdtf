# DMX and fixture state (`Candela.Dmx`)

Turns DMX into the physical state of every fixture, every frame: what the renderer draws (pan/tilt of the geometry
tree, beam angles, intensity in candela, colour, gobos, prisms, blades …), what the properties panel shows (attribute
values, raw DMX) and what the smoke and fire simulations read (fog, haze, fan and flame outputs). The GDTF definition
is the authority: every number comes from the fixture's channel functions, channel sets, wheels, emitters and
geometry; heuristics only fill gaps the file leaves open (and are listed below).

```
network threads                 main thread, once per frame
───────────────                 ─────────────────────────────────────────────────────────────────────────────
Art-Net / sACN … ──Submit──▶ DmxInputHub ──TryRead (each patched universe once)──▶ DmxEngine.Update(t)
                                                                                     │ scene changes → per-fixture runtime
                                                                                     │   FixtureProfileCache → FixtureProfile
                                                                                     │   FixtureState, FixtureDmxBuffer
                                                                                     │ console-driven? copy slots : defaults
                                                                                     │                + ManualAttributes
                                                                                     ▼
                                                   FixtureEvaluator.Evaluate (worker pool) → FixtureState
                                                     axes · beams · device outputs · channel values
```

`tests/Candela.Dmx.Tests` (103 tests, ≈ 15–60 s depending on the machine) runs real fixtures from the repository
library through every stage; one test evaluates **every mode of all 1091 GDTF files** with random DMX.

## Units and conventions

| Quantity | Unit | Where |
|---|---|---|
| angles (pan, tilt, beam/field angle, wheel and gobo rotation, blade rotation) | rad | `AxisState`, `BeamState`, `WheelState`, `BladeState` |
| rotation speeds | rad/s | `WheelState.RotationSpeed` |
| luminous intensity | cd | `BeamState.PeakCandela` (on axis, at the current zoom, after colour filters and dimming) |
| luminous flux | lm | `BeamState.Flux`, `ProfileBeam.ReferenceFlux` |
| levels (intensity, iris, frost, focus, blade insertion, device outputs) | 0 … 1 | |
| frequencies | Hz | strobe, shake, effect rates |
| translations, focus distance, flame height | m | |
| time | s | `Evaluate(…, time)`, `DmxEngine.Update(time)` |

GDTF physical values keep their GDTF units (degrees, Hz, K, percent …) in `ChannelValue.Physical`, in
`FixtureState.TryGetAttribute`/`CopyAttributes` and in `FixtureComponent.ManualAttributes` — that is what the
properties panel shows and edits. Wheel slot selection is the exception: its control value is the **1-based slot
position** (fractional between slots), not the GDTF physical value. Light colour follows the engine convention:
`LightColor` is a chromaticity with luminance 1 (linear Rec.2020); the magnitude is `PeakCandela`/`Flux`.

DMX values are handled at the channel's resolution (8, 16, 24 or 32 bit, coarse byte first, GDTF `Offset` lists may
be non-contiguous). Nothing on the per-frame path throws, allocates or produces NaN: malformed files degrade to
documented defaults and a profile warning.

## Fixture profiles (`FixtureProfile`, `FixtureProfileCache`)

A profile is one GDTF fixture type in one DMX mode, compiled once and shared (immutable, thread-safe) by every
fixture of that type. `FixtureProfileCache` keys it by file path + size + write time + mode, finds GDTF files by file
name or — for MVR stub names such as `Manufacturer@Model` — by manufacturer and model (`FixtureLibrary.FindBySpec`), and
caches failures too (`ProfileResolution.ErrorKind`: unknown file, unreadable file, unknown mode).

Compilation (`FixtureProfile.Builder`):

1. **Geometry** — the mode's geometry tree flattened into parts (`FixtureGeometry`, no meshes loaded).
2. **Breaks** — one `DmxBreakLayout` per GDTF DMX break with its footprint (highest slot used).
3. **Channels** — one `ProfileChannel` per `DmxChannelInstance`: GeometryReferences are expanded, so every pixel or
   head has its own channels with its own offsets (`Pixel 7`, `Head 3` …). Each knows its break, offsets, byte
   width, default and highlight values (at channel resolution), its logical channel's snap mode, the geometry part
   it controls, the axis it moves and the beams it affects. Virtual channels (no offset, e.g. per-pixel virtual
   dimmers) default to full when their GDTF default is 0, otherwise they would black the fixture out for good.
4. **Functions** — one `ProfileFunction` per channel function: DMX range at the channel's resolution, physical range,
   DMX profile curve (GDTF 1.2), classification of the attribute (`AttributeClassifier`), unit, level scale (0 … 1
   vs percent ranges), wheel, emitter, channel sets sorted by DMX value with names pre-parsed (`NameParsing`: open /
   closed / no function, invert pan/tilt, kelvin, colour names, waveforms, dimmer curves, prism facets), wheel slot
   maps (see below), colour-temperature neutral points, motion limits. A shutter function named "Open" or "Closed"
   gives that state to the DMX values not covered by a named channel set.
   **Placeholder ranges**: a physical range left at GDTF's default 0 … 1 (and no channel set with real values) is
   not taken literally where it would break the fixture: `Zoom` maps onto the `_specs` beamAngleDeg range (not a
   0 … 1° pencil beam; without a known zoom range, or when the mode also has a real zoom — e.g. a "zoom lens
   rotation" effect written as `Zoom` — the function does not drive the beam angle), `Pan`/`Tilt` onto the `_specs`
   pan/tilt range centred on 0° (a `Pan`/`Tilt` function on a channel of another attribute, e.g. "Display Intensity"
   on a control channel, moves nothing), and strobe/pulse frequencies, spins, shakes and random wheels read it as a
   normalised slow … fast rate (0.5 … 25 Hz strobe, 1 turn/s spin, 0.5 … 10 Hz shake) instead of 0 … 1 Hz. In the
   library: 28 zoom, 56 pan/tilt and ≈ 550 strobe functions. A default 0 … 1 function range whose channel sets carry
   the real values (e.g. "Min to Max" 10 … 60°) takes its range from the sets. Each case adds a profile warning.
5. **Mode masters** resolved to channel instances (the master is looked up along the follower's geometry-reference
   chain, so each pixel's functions depend on that pixel's master) with the master range at the master's resolution.
6. **Relations** (`Multiply`, `Override`) resolved to channel instances, again per reference chain.
7. **Beams** — every Beam geometry instance, with the channels that affect all beams (evaluated once per frame) and
   the channels of each beam (evaluated per beam), photometry and source colour (below).
8. **Axes** — every Axis part moved by Pan, Tilt, PanRotate, TiltRotate, `Rot_*` or `XYZ_*`; a channel attached to a
   non-axis part moves the first axis below it (tilt-like channels the second, nested one).
9. **Outputs and modifiers** — Fog/Haze/Fan/Flame channels; MSpeed, DimmerCurve and pan/tilt-invert channels.
10. **Decode tables** — every 8-bit channel whose functions do not depend on a mode master is decoded for all 256
    values once (shared by all instances of a GDTF channel), so the per-frame decode of most channels is a lookup.

### Wheel slot positions

A wheel selection function maps DMX to a 1-based **slot position**. Channel sets naming a slot hold that slot; sets
whose physical range is an offset within ±0.5 (GDTF's way to describe continuous indexing, e.g. "Blue −0.5…+0.5")
move linearly across the slot; between sets of *adjacent* slots the position is interpolated (split colours),
otherwise held. Going from the last slot to the first continues past the end (position n + 1 is slot 1), so a wheel
scrolling through "Open" keeps turning in one direction. A function without slot sets scrolls over the whole wheel.

## Patch (`Patch`, `PatchIssueProvider`)

`Patch.Build(scene, profiles)` lists every `FixtureComponent` with its resolved profile and the slots each DMX break
occupies (address from `FixtureComponent.Addresses` matched by break number; a single-break mode accepts the only
address whatever break number an MVR file wrote). `PatchIssueProvider` (`IIssueProvider`, category *Patch*) reports,
naming fixtures as `Fixture 101 'Spot 1'`:

| Code | Severity | When |
|---|---|---|
| `patch.unknown-gdtf` | error | no GDTF file set, or the file is not in the library and no same-model file matched |
| `patch.unreadable-gdtf` | error | the file exists but cannot be parsed |
| `patch.unknown-mode` | error | the DMX mode does not exist (the message lists the modes that do) |
| `patch.substitute-gdtf` | warning | an MVR stub was matched to a *similar* model of the same manufacturer |
| `patch.matched-gdtf` | note | an MVR stub was matched to the same model under another file name |
| `patch.invalid-universe` | error | universe < 1 |
| `patch.invalid-address` | error | address outside 1 … 512 |
| `patch.out-of-range` | error | the footprint runs past slot 512 |
| `patch.missing-break` | warning | a break of a multi-break mode has no address |
| `patch.unpatched` | note | a fixture has no address at all |
| `patch.overlap` | error | two fixtures share slots (message gives both ranges and the shared slots), or two DMX breaks of one fixture do |
| `patch.shared-address` | warning | identical type, mode and range: a deliberate "same address" patch |

`Patch.At(universe, slot)` indexes a universe's slots on its first query (a whole patch view costs O(slots +
fixtures)). Overlaps are found with a sort-and-sweep per universe (O(n log n) plus the overlaps found; 4750 fixtures take a few
milliseconds), at most 500 overlap issues per analysis.

## Evaluation (`FixtureEvaluator` → `FixtureState`)

`Evaluate(state, dmx, time, overrides, applyDynamics)` overwrites a preallocated `FixtureState` in place. The steps:

1. **Read** every channel's raw value from the `FixtureDmxBuffer` (break-relative slots; virtual channels have their
   own store).
2. **Select the active function** of each channel: the first function (GDTF order) whose mode-master condition holds
   and whose DMX range contains the value; else the available function starting closest below the value; else the
   first available one. Then the channel set containing the value, the **physical value** — linear between
   PhysicalFrom and PhysicalTo (through the DMX profile curve when present; a channel set with its own physical
   range overrides the function's mapping inside the set) — and the **control value** (the physical value, or the
   slot position for wheel selection).
3. **Manual overrides** (`AttributeOverrides`, compiled from attribute → value, `"Attribute@Instance"` for one pixel)
   replace the DMX of their channels: the function that produces the value is selected and its DMX value set. Mode
   masters are honoured as over DMX: the overrides' DMX is in place *before* step 2, so channels depending on a
   manual mode master are decoded against it (a manual `Gobo1` that selects indexing turns the `Gobo1Pos` channel
   from a rotation speed into an angle); a manual function whose mode master is not set manually moves that master
   into its range; and a manual master that can produce its value inside the range a manual dependent needs takes
   that function (`Gobo1` = 3 with `Gobo1PosRotate` = 200 selects slot 3 in the wheel's rotation mode).
4. **Relations**: a `Multiply` relation scales its follower's level by the master's level (virtual dimmers — a
   relation master is not multiplied into the beams again); `Override` replaces the follower's value.
5. **Fixture-wide modifiers**: DimmerCurve (linear / square / inverse square / S-curve by channel-set name), MSpeed
   channels (scale the motion limits of their group: position, colour, gobo, beam, shaper, or all), pan/tilt invert
   switches recognised in channel-set names.
6. **Dynamics** (see *Motor dynamics*): motorised control values move toward their targets; continuous rotations
   (PanRotate, TiltRotate, wheel spin, gobo/prism rotation, select-spin) integrate their angle over time — exactly,
   from time 0 on the first evaluation, so a state evaluated once at time *t* shows the angle reached at *t*.
7. **Axes**: pan (about local Z) and tilt (about local X) in radians including continuous rotation and invert
   flags, `Rot_X/Y/Z`, `XYZ_X/Y/Z`. `FixtureState.WriteAnimation` writes one matrix per geometry part for
   `FixtureGeometry.ComputeWorldTransforms`.
8. **Beams** (one `BeamState` per Beam geometry instance; channels shared by all beams are applied once and copied):
   intensity, colour, photometry, optics and mechanics, below.
9. **Device outputs** (`DeviceOutputs`).

`FixtureState` also keeps, per channel, raw DMX, active function, physical and control value (`GetChannel`), and
answers `TryGetAttribute("Zoom")` / `CopyAttributes(dict)` for the properties panel (instanced channels as
`Attribute@Instance`). `Version` increments with every evaluation; `IsAnimating` tells the engine that the state
changes with time alone (strobe, rotation, shake, moving motor).

### Intensity and strobe

`Intensity = Dimmer × relations × dimmer curve × Shutter`. Shutter channels open/close by value or by channel-set
name ("Open", "Closed"); strobe functions (`Shutter(n)Strobe…`, `StrobeRate` with `StrobeMode…`, iris and frost
pulses) are deterministic periodic functions of time at the GDTF frequency (Hz; 0 … 1 ranges without a frequency
unit, and GDTF's default range 0 … 1, span 0.5 … 25 Hz), so a frame rendered twice — or offline from recorded DMX —
looks the same:

| Waveform | GDTF attributes | Shape over one period |
|---|---|---|
| Strobe | `Shutter(n)Strobe`, `…StrobeEffect`, `StrobeModeStrobe` | rectangular flash, duration from `StrobeDuration` or 50 ms (2 … 50 % duty) |
| Pulse | `…StrobePulse`, `Frost(n)Ramp` | triangle (fade in and out) |
| PulseOpen / PulseClose | `…StrobePulseOpen/Close`, `IrisPulseOpen/Close`, `Frost(n)PulseOpen/Close` | ramp up / ramp down |
| Random, RandomPulse(Open/Close) | `…StrobeRandom…`, `IrisStrobeRandom`, `IrisRandomPulse…` | one event per period at a hashed random time, some periods dark; decorrelated per fixture by the seed |
| Burst | `…StrobeBurst`, `…BurstPulse` | three short flashes in the first 40 % of the period |
| Sine | `…StrobeSineWave` | raised cosine |

### Photometry

`PeakCandela = reference peak at the current beam angle × Intensity × ColorTransmission`. The reference output of a
beam at full white comes from, in order: `_specs` `maxLuminousIntensityCd` (the datasheet value, read as the peak at
the narrowest zoom and shared between the fixture's beams; 1058 of the 1060 light-emitting files have it) → the
GDTF Beam's `LuminousFlux` when it is not GDTF's default 10 000 lm → `_specs` `luminousFluxLm` → GDTF's default flux
(split between the beams of a multi-beam fixture) → beam power × a typical efficacy of the lamp type (LED 40,
discharge 45, halogen 18, tungsten 15 lm/W). The `_specs` values are shared between the beams in proportion to their
GDTF `LuminousFlux` when the file gives the beams different fluxes (a main engine and an aura ring, large and small
cells: 89 of the library's multi-beam modes), a beam with GDTF's default or no flux counting as the mean of the
others; equally otherwise. The lamp type is LED for a beam whose mode drives additive emitters, else the `_specs`
`lightSourceType` (files often keep GDTF's default "Discharge" on LED fixtures), else the GDTF `LampType`. The flux is kept constant when zooming: the peak follows
`flux / Ω_eff(beam angle)` from `Candela.Photometry.BeamDistribution` (tabulated per beam), so zooming wide lowers
the peak by the solid-angle ratio. `ColorTransmission` is the luminous share of the current colour relative to full
white (emitter mix level, CMY/CTO, colour-wheel filters).

### Colour

* **Additive emitters** (`ColorAdd_*`): each function is linked to a GDTF `Emitter` (the function's Emitter link, else
  an emitter whose name matches the attribute's colour code), else a standard LED primary from
  `Candela.Photometry` for the code (R, G, B, W, WW, CW, A, L, UV, RY, GY, …). Emitters share the beam's reference
  flux by their relative output — the GDTF emitter colour's Y when the file gives measured values, typical LED shares
  when it does not (all Y equal, or two clearly different colours with the same Y, e.g. red = green = 100) — and are
  mixed by luminous flux in CIE XYZ. Full white is therefore the mix of all emitters at full. A mode whose only additive
  attributes are `ColorAdd_C/M/Y` is CMY colour mixing written with the wrong attribute (10 files: spots with CMY
  flags, virtual CMY modes of LED fixtures) and is read as `ColorSub_C/M/Y`.
* **Indirect colour systems**: `ColorRGB_*` (in the fixture's GDTF colour space, sRGB when it is missing or
  degenerate), `HSB_Hue/Saturation/Brightness`, `CIE_X/CIE_Y/CIE_Brightness` (x, y as 0 … 1 or ×10⁴ integers).
* **White sources**: the lamp's own colour temperature (Beam `ColorTemperature`, or the CCT of its emitter
  spectrum; 6000 K when unknown).
* **Subtractive**: `ColorSub_C/M/Y` (and R/G/B as complementary pairs) and `CTO`/`CTB`/`CTC` go through a spectral
  model of the source (CIE reference illuminant at the source CCT × idealised dichroic CMY bands × an ideal
  conversion filter to the target CCT), precomputed per source CCT. `CTC` names the resulting CCT. CTO/CTB with
  kelvin physicals shift the colour temperature relative to their neutral end (no filter at the highest / lowest
  kelvin); a CTO/CTB whose kelvin range runs from warmer to cooler than the source *and* has a separate "Open" range is
  a colour-temperature selector and names the resulting CCT like CTC. Kelvin channel sets ("3200K") interpolate in
  mired; 0 … 1 ranges map to a full 5600 → 3200 K conversion. `Tint` shifts Δuv (±0.02 for symmetric ranges).
* **Electronic colour temperature**: in an LED engine that mixes its colour or tunes its white (emitters in the
  mode, several coloured emitters in the fixture, a CTC channel, or a CTO/CTB on an LED that is not a spot, profile,
  beam or follow spot — whose CTO is a flag — and no CMY flags) CTC/CTO/CTB re-balance the emitters instead of filtering: the colour
  moves like under a conversion filter, but the luminous output follows the engine's efficiency at the new white
  point — the largest flux its emitters can produce at that CCT relative to the source CCT (an exact small linear
  program solved once per profile on a 10-mired grid, `WhiteEfficiencyTable`), never more than before. Without
  emitter data the white point moves without a luminous cost.
* **White and colour engines**: fixtures with a CTC channel and a cross-fade between their white and their colour
  engine (`Color XF` of ARRI SkyPanel / L-series / Orbiter, `ColorXFade`, `CTC<>RGB`, `ColorCrossfade` … in 81 files;
  0 = white, 1 = colour) mix the two in XYZ: the white engine is the Planckian colour of the CTC value with the tint,
  at the emitters' best output for that CCT (the brightest white of the engine = the reference output); the colour
  engine is the emitter / RGB / HSB / CIE mix, unaffected by CTC and tint. At the default cross-fade the RGBW channels
  therefore do not darken the fixture.
* **Saturation without hue**: an `HSB_Saturation` channel in a fixture without `HSB_Hue` (a "saturation" of virtual
  gels) desaturates the colour-wheel and colour-macro filters (0 = white, 1 = the full gel).
* **Colour wheels**: each slot's CIE colour and its Y (transmission) become a linear Rec.2020 transmission;
  open slots (no filter, media, facets; named "Open"/"White" or D65 at Y ≈ 100 %) pass everything. A position
  between two slots blends their transmissions (split colour); wheel spin, index and random positions follow.
* **Colour macros**: channel-set names carrying a colour name ("Deep Red", "Lavender" …) or a colour temperature
  are applied as a filter on the white source.

### Optics and mechanics

* **Beam/field angle** from `Zoom` (GDTF physical degrees, clamped 0.1 … 179°; ranges named "No function" /
  "Disabled" leave it alone; `ZoomModeSpot`/`ZoomModeBeam` drive it only in modes without a `Zoom` channel — next to
  one they are presets or mode switches and would otherwise override it); without zoom, the Beam geometry's
  angles (or `_specs` beamAngleDeg when the file carries GDTF's 25°/25° defaults). Field angle = beam angle × the
  beam's field ratio (from the GDTF field angle, or the beam type's typical ratio).
* **Focus** 0 … 1 within the function's range; `Focus(n)Distance` in metres. **Iris** opening 0 … 1 (1 = open),
  including iris pulses. **Frost** 1 − Π(1 − fᵢ) over all frost channels and frost pulses.
* **Wheels** (`WheelState` per colour wheel, gobo wheel, prism, animation wheel/system, effects wheel, beam shaper;
  index = wheel number − 1): wheel index, 1-based fractional position, current and next slot with blend, wheel
  angle (index/spin), slot-item rotation (indexed `…Pos` or continuous `…PosRotate`, integrated), rotation speed,
  shake frequency (`…SelectShake`, `…WheelShake`, `…PosShake` add a deterministic oscillation), random slot
  changes, prism facets (from the slot's GDTF facets, else the slot name "3-facet", else 3), insertion
  (`AnimationSystem(n)` / `…Insertion`), effect rates. A wheel named only by a rotation or rate function sits on its
  first slot.
* **Shapers**: `Blade(n)A/B` insertion, `Blade(n)Rot`, `BladeSoft(n)A/B`, `ShaperRot`.

Gobo images, iris, blades, prism and frost are not applied to `PeakCandela`; the renderer applies them through the
beam's projected cookie.

### Device outputs

`DeviceOutputs` (0 … 1): `Fog`, `Haze`, `FanSpeed` + `FanDirection` (±1), `Flame` + `FlameHeight` (m, NaN when the
fixture does not give it). The library's atmospheric devices (2 HAZE, 5 FOG files) use `Haze1`, `Fog1`, `Fan1` and
`Blower1` (low-fog blower); some foggers also carry RGB LEDs, which are ordinary beams. There are no FAN or
GENERATOR FIRE files in the library yet, so the standard names are accepted as specified (`Fan(n)`,
`Fan(n)Direction`, `Blower(n)`, `Flame(n)`/`Fire(n)` [+`Output`/`Intensity`], `Flame(n)Height`), and a GENERATOR
FIRE device without flame attributes fires from its dimmer/fog channel. `Fan` attributes drive `FanSpeed` only on
atmospheric devices (category HAZE/FOG/FAN/GENERATOR FIRE, or no beams and fog/haze/flame channels); in lights they
are cooling fans and are ignored.

## Motor dynamics (`MotorDynamics`)

Mechanical attributes do not jump between DMX frames: positions (pan, tilt, `Rot_*`, `XYZ_*`, zoom, focus, iris,
frost, blades, shaper rotation, animation insertion, wheel slot positions and indexing, gobo/prism indexing, and the
CMY/CTO flags of lamp fixtures) follow their target with a **time-optimal trapezoidal profile** limited by a maximum
speed and acceleration; rotation speeds ramp with an angular acceleration limit while the angle integrates. Each step
integrates the constant-acceleration phases analytically, so the trajectory does not depend on the frame rate (60
frames or 7 uneven frames over a second give the same result to 10⁻⁶). The motor carries its quantity, so a wheel
switching from "select" to "spin" or a gobo from "rotate" to "index" continues from where the mechanism physically is.

Limits come from GDTF `RealFade` (time to travel PhysicalFrom → PhysicalTo from rest to rest) and `RealAcceleration`
(time to reach full speed) — a full-range move takes exactly `RealFade` (trapezoid: v = range / (fade − tₐ); a
triangle when fade < 2·tₐ). 2089 pan/tilt functions in the library carry them. Without them, typical values of the
fixture category apply:

| Attribute | Default (moving head) | Scanner |
|---|---|---|
| Pan | 257 °/s, 0.4 s to full speed → 540° in ≈ 2.5 s | 2000 °/s, 0.05 s |
| Tilt | 200 °/s, 0.33 s → 270° in ≈ 1.7 s | 2000 °/s, 0.05 s |
| Zoom / focus / iris / frost | full range in 0.75 / 0.6 / 0.3 / 0.35 s | |
| Colour / gobo wheel | one turn in ≈ 0.9 s (prism 0.6 s) | |
| Gobo/prism index, wheel index | 360 / 450 °/s | |
| Continuous rotation | 1080 °/s² (pan/tilt rotate 360 °/s², scanners 3600) | |

LED intensity, emitter levels, strobe and shutter are electronic and follow DMX instantly (CMY/CTO too on LED
engines). MSpeed channels scale speed by f and acceleration by f² for their motion group (DMX low = fastest unless
the GDTF physical range says otherwise). The first evaluation of a state jumps to its DMX values (a fixture powers up
in place).

## Encoding (`DmxEncoder`) and manual control

`DmxEncoder` turns physical values back into DMX: `Encode(buffer, "Pan", 90f)` picks the channel function that can
produce the value (respecting the current mode-master state, setting a mode master when only a dependent function
fits), writes coarse/fine/ultra bytes at the channel's resolution, and returns the number of channels written;
`EncodeFunction` targets one function, `ToDmx` gives the DMX value. Encoding a value of any channel function and
evaluating the result gives the value back within the DMX quantisation (tested for every function of eight modes). Manual values in `FixtureComponent.ManualAttributes` are
physical control values (wheel selection: slot position) and are compiled once into `AttributeOverrides`.

## Engine (`DmxEngine`)

`new DmxEngine(scene, profileCache, inputHub)`, then `Update(timeSeconds)` once per frame on the main thread:

1. Pending scene changes (fixture entities or components added, removed, changed) are applied incrementally from
   `Scene.Changed`: a new profile and state only when GDTF file or mode changed; addresses and manual values are
   re-read; removal is O(1).
2. Every universe that a fixture is patched in is read from the `DmxInputHub` once (merge, version check).
3. For each fixture the **console-driven** predicate (`ConsoleDriven`, supplied later by the engine from the cable
   topology; default: driven when every universe it is patched in has a live source) decides between DMX (its slots
   are copied when the universe changed) and its manual attributes over GDTF defaults.
4. Fixtures whose input changed or whose state is animating are evaluated — on `MaxDegreeOfParallelism` threads
   (default: all cores; an allocation-free fork-join pool, used from 96 fixtures on) — with motor dynamics. A fixture
   skipped while it rested has its clock advanced to the previous frame first (`FixtureState.AdvanceIdleClock`), so
   a move after a pause starts from rest at that frame instead of integrating the pause and completing at once.
5. `Changed` lists the entity ids whose state changed this frame, `Removed` those that lost their state;
   `GetState(id)`, `IsConsoleDriven(id)` and `GetResolution(id)` (why a fixture has no state) serve the renderer
   and the panels. `BuildPatch()` gives the patch for issue checks.

Steady-state frames allocate nothing; idle fixtures cost nothing.

## Performance

2000 fixtures of a realistic mix (MAC Viper Profile, Robin Esprite 16-bit, ArcPod 27Q, Robin Tetra1 10-pixel,
MAC Encore, Arolla Aqua LT, Paladin Panel 10-cell, Entour Haze: 6750 beams, 69 250 channels, 179 universes) with
**every slot changing every frame** — the worst case: every motor moves, every pixel changes colour — measured on this
machine (4-core Xeon VM, shared with the builds and tests of other modules, load average 9–26 during the runs):

| Build | `DmxEngine.Update` (4 threads) | `FixtureEvaluator` on one thread |
|---|---|---|
| Release | **≈ 2.2 ms** per frame (fastest frame ≈ 1.9 ms) | ≈ 3.8 ms (≈ 1.3 µs per fixture with its data in cache) |
| Debug (unoptimised JIT) | ≈ 11–22 ms | ≈ 17–25 ms |

`PerformanceTests` (run `dotnet test -c Release` for the optimised numbers) asserts < 5 ms per frame for optimised
builds — the median, or the fastest frame when the machine's load average exceeds its core count (then the median
measures the other processes) — and that frames do not allocate. What makes it fast: flat arrays on the per-frame
path; 8-bit channels decoded by table lookup and channel sets found by binary search; a 32-byte runtime record per
channel with motor state only for channels that move; motor steps without divisions or square roots; channels shared
by all beams evaluated once, one-beam fixtures evaluated in place; colour-temperature filters, white points and beam
solid angles computed once per fixture and frame; emitter mixing as a precomputed XYZ sum; mechanics resolved once
per fixture when pixels have none of their own; and the parallel evaluation in the engine.

## Attribute coverage

The 1091 GDTF files of the repository use **1168 distinct attribute names** in 30 800 (file, attribute) pairs.
`AttributeClassifier` models **387 names covering 22 591 pairs (73 %)** — every attribute that changes light output,
position or a device output in a way the GDTF file describes. Attribute names are matched case-insensitively,
with the numbered forms `Gobo(n)…`, `Shutter(n)…`, `Blade(n)…`, etc.

### Handled

Library counts are the number of files using the names.

| Kind | Files | Names | Most used (files) |
|---|---|---|---|
| Dimmer | 1071 | 3 | `Dimmer` (1066), `Dim` (3), `Intensity` (2) |
| Shutter | 960 | 5 | `Shutter1` (916), `Shutter2` (25), `Shutter3` (10), `Shutter4` (8), `Shutter8` (1) |
| Strobe | 3236 | 39 | `Shutter1Strobe` (905), `Shutter1StrobeRandom` (775), `Shutter1StrobePulseClose` (413), `Shutter1StrobePulseOpen` (412), `Shutter1StrobePulse` (344), …; also `Burst`, `Burst Pulse`, `Random Burst Pulse`, `Sine Wave` |
| StrobeRate | 38 | 2 | `StrobeRate` (31), `StrobeFrequency` (7) |
| StrobeDuration | 84 | 2 | `StrobeDuration` (82), `ShutterStrobeDuration` (2) |
| StrobeMode | 64 | 9 | `StrobeModeStrobe` (22), `StrobeModeShutter` (13), `StrobeModePulseOpen` (11), `StrobeModeRandom` (5), … |
| Pan / Tilt | 573 / 662 | 1 / 1 | `Pan`, `Tilt` |
| PanRotate / TiltRotate | 38 / 28 | 1 / 1 | `PanRotate`, `TiltRotate` |
| Translation / Rotation | 10 / 1 | 2 / 1 | `XYZ_X`, `XYZ_Y`, `Rot_Z` (and the other axes) |
| ColorAdd | 2780 | 21 | `ColorAdd_B` (591), `ColorAdd_G` (589), `ColorAdd_R` (587), `ColorAdd_W` (379), `ColorAdd_RY` (172), `ColorAdd_GY` (130), … |
| ColorSub | 792 | 6 | `ColorSub_C/M/Y` (263 each), `ColorSub_R/G/B` (1 each) |
| ColorRgb | 101 | 6 | `ColorRGB_Red/Green/Blue` (28–30), `ColorRGB_Cyan/Magenta/Yellow` (5) |
| HSB | 82 / 84 / 37 | 3 | `HSB_Hue`, `HSB_Saturation`, `HSB_Brightness` |
| CIE | 46 / 46 / 15 | 3 | `CIE_X`, `CIE_Y`, `CIE_Brightness` |
| CTO / CTB / CTC | 337 / 24 / 239 | 1 / 1 / 4 | `CTO`, `CTB`, `CTC` (235), `CCT`, `COLORTEMPERATURE`, `Color Temperature` |
| Tint | 176 | 2 | `Tint` (161), `Green / Magenta` (15) |
| White ↔ colour cross-fade | 81 | 8 | `ColorXFade` (22), `ColorXfade` (19), `CTC<>RGB` (14), `CTC<>XY` (10), `Color XF` (8), `COLORCROSSFADE` (3), `CTC-Color CrossFade` (3), `ColorCrossfade` (2) |
| ColorMacro | 335 | 4 | `ColorMacro1` (309), `ColorMacro2` (21), … (by channel-set name) |
| Wheel select | 2407 | 120 | `Color1` (589), `Effects1` (340), `Gobo1` (300), `Prism1` (266), `Gobo2` (195), `Color2` (139), … |
| Select spin / shake | 75 / 338 | 6 / 6 | `Gobo1SelectSpin` (28), `Gobo1SelectShake` (191), `Gobo2SelectShake` (130), … |
| Wheel index / spin / shake / random | 129 / 875 / 108 / 278 | 5 / 7 / 4 / 6 | `Color1WheelIndex` (93), `Color1WheelSpin` (340), `Gobo1WheelSpin` (265), `Gobo1WheelShake` (62), `Color1WheelRandom` (108), … |
| Slot index / rotate / shake | 786 / 843 / 27 | 14 / 14 / 5 | `Prism1Pos` (247), `Gobo1Pos` (176), `Prism1PosRotate` (263), `Gobo1PosRotate` (173), `BeamShaperPosRotate` (23), `Gobo1PosShake` (19), … |
| Effect rate | 322 | 14 | `Effects1Rate` (256), `Effects2Rate` (43), … |
| Animation system | 27 | 2 | `AnimationSystem1` (24), `AnimationSystem1Insertion` (3) |
| Frost / frost pulse | 428 / 42 | 3 / 9 | `Frost1` (298), `Frost2` (108), `Frost3` (22), `Frost1Ramp`, `Frost1PulseOpen/Close`, … |
| Iris / iris pulse | 184 / 275 | 1 / 6 | `Iris`, `IrisPulseClose` (104), `IrisPulseOpen` (98), `IrisStrobeRandom` (38), … |
| Zoom | 482 | 2 | `Zoom` (478), `ZoomModeBeam` (4) |
| Focus / focus distance | 319 / 86 | 2 / 2 | `Focus1` (305), `Focus2` (14), `Focus1Distance` (83), … |
| Blades | 672 / 292 / 346 | 4 / 4 / 4 | `Blade1…4A`, `Blade1…4B`, `Blade1…4Rot`, `BladeSoft(n)A/B`, `ShaperRot` (149) |
| DimmerCurve | 475 | 1 | `DimmerCurve` |
| MotionSpeed | 659 | 15 | `PositionMSpeed` (356), `ColorMixMSpeed` (61), `GlobalMSpeed` (56), `IntensityMSpeed` (46), `GoboWheel1MSpeed` (36), …; `PTSpeed`, `PanTiltSpeed` |
| Fog / Haze / Fan | 5 / 2 / 20 | 1 / 1 / 2 | `Fog1`, `Haze1`, `Fan1` (19), `Blower1` (1); `Flame(n)`, `Fire(n)`, `Flame(n)Height`, `Fan(n)Direction` |

### Ignored, and why

781 names (328 of them used by a single file) do not change light, position or a device output in a way the GDTF
describes. The classifier returns `AttributeKind.Ignored`; their channels are still decoded (raw DMX, active function
and physical value are in `FixtureState` for the properties panel), they only do not feed the physical state.

| Group | Examples (files) | Reason |
|---|---|---|
| Placeholders | `NoFeature` (504), `Dummy` (49), `Reserved`, empty names | nothing to model |
| Fixture control and maintenance | `Control1` (401), `Function` (321), `FixtureGlobalReset` (345), every `…Reset` (`PositionReset` 188, `BeamReset` 102, …), `LampControl` (55), `LampPowerMode`, `DisplayIntensity` (203), `DMXInput` (96), `Hibernation`, `Standby`, `FixtureCalibration`, `PressureTest`, `ParkingPosition`, `RoboSpot`, `VideoTracking`, `P3DMX`/`P3Video`/`P3Mix` | firmware commands and settings; lamps are taken as struck, displays and resets do not change the light |
| Mode switches | `DimmerMode` (456), `BlackoutMode` (220), `ColorMixMode` (127), `PanTiltMode` (96), `ColorCalibrationMode` (89), `PositionModes`, `PanMode`, `TiltMode`, `ColorModelMode`, `ChromaticMode`, `CRIMode`, `ZoomMode`, `FocusMode`, `PixelMode`, `LEDZoneMode`, `IrisMode`, `Gobo1WheelMode`, `Color1Mode` | they change how *other* channels behave, which GDTF expresses with mode-master dependencies (evaluated); invert switches in their channel sets ("Pan reverse") are recognised by name |
| Cooling and power | `FanMode` (265), `Fans` (84), `QuietMode`, `HighPowerMode` | cooling fans and power modes of lights, not device fans |
| LED driver | `LEDFrequency` (386), `LEDFrequencyAdjust` (59), `FreqTweak`, `Refresh Rate` | PWM frequency, invisible at render frame rates |
| Built-in effect engines and macros | `Effects1Fade` (93), `EffectsSync` (55), `ColorEffects1` (62), `ColorMacro1Rate` (101), `Prism1Macro` (74), `ShaperMacros` (37), `Sparkle` (42), `RainbowEffect` (31), `Effects1Adjust1`, `Effects1Color…`, `Pattern…`, `Shape…`, `Macro…`, `Playmode`, `PositionEffect` | fixture-internal programs whose output is defined by firmware, not by the GDTF |
| Sound to light | `Color1WheelAudio` (38), `Gobo1WheelAudio` (36), `ColorMacroAudio` | needs audio input |
| Fades and other colour-system blends | `Crossfade` (18, a fade *time*), `XY<>GEL`, `RGB<>GEL`, `GEL<>RAW`, `XY1<>XY2`, `XY_Crossfade`, `LAYER1<>LAYER2`, `ColorMixMTime` | fade timing, or blends with gel / second-point / layer engines that are not modelled (the white ↔ colour cross-fades are, see above) |
| Gel pickers | `GelSelector`, `GelCategory`, `GelSource`, `Lee …`, `Rosco …`, `Storaro`, `Cinelux`, `Cal Color`, `Brand` | the colours live in a gel database the GDTF does not contain |
| Calibration and emulation | `ColorConsistency`, `ColorAdjust_*`, `TintCalibration`, `Green-BlueCorrection`, `WhitePoint`, `ColorGamma`, `TungstenEmulation`, `TungstenEffect`, `HalogenSimulation`, `Redshift`, `Incand`, `ExtendedCTO`, `CTCDisable` | per-unit trims and lamp emulations without a physical description |
| Aura / background layers | `BackgroundDimmer`, `Background Dimmer`, `Back Dimmer`, `ForegroundDimmer`, `BackgroundColor`, `Effects1…Aura/Backlight` | light the lens or body, not a Beam geometry |
| Tracking and limits | `FocusTracking` (30), `AutoFocus`, `PanTiltLimits` (17), `BeamSmoothing` | automatic behaviour of the fixture |
| Non-standard names with unclear meaning | `ZoomPos`/`ZoomPosRot`, `Fram Zoom`, `GoboRot`, `Hue`, `Size`, `Direction`, `Offset` (1–21 files each) | ambiguous; unambiguous spellings are aliased instead (`Dim`, `Intensity`, `CCT`, `Color Temperature`, `Green / Magenta`, `PTSpeed`, `Burst …`, `Sine Wave`) |

## Tests

`tests/Candela.Dmx.Tests`, all with real files from the repository:

* **Moving head** (MAC Viper Profile): basic-mode layout, pan/tilt at DMX 0 / centre / max = PhysicalFrom / centre /
  PhysicalTo, axis matrices, dimmer and peak candela = the datasheet's 760 000 cd, zoom extremes = the GDTF zoom range
  and `_specs` beamAngleDeg, colour-wheel slot colours and split colours, gobo slot selection, indexing and
  continuous rotation, prism insertion and indexing, full CMY → very low transmission, CTO lowers the CCT, iris /
  frost / focus, strobe periodic in time, manual overrides.
* **LED** (ArcPod 27Q RGBW, Robin Tetra1 pixels): pure emitters have the GDTF emitter chromaticities, full white is
  the emitter mix, intensity scales peak candela, LED strobe periodic, dimmer relations multiply once, virtual
  dimmers, one beam per pixel with independent colours from independent DMX.
* **Device outputs**: hazer (haze + fan), four fog machines, low-fog blower, cooling fans of lights not reported,
  flame projector (output + height) and a generator-fire device firing from its dimmer (synthetic GDTFs).
* **Encoder**: encode → evaluate round trip for every channel function of eight fixture modes, coarse/fine bytes,
  mode-master dependent functions, highlight values.
* **Motor dynamics**: default moving-head pan 540° in ≈ 2.5 s, RealFade = full-range travel time (trapezoid and
  triangle), limits never exceeded and no overshoot, frame-rate independence, exact rotation integration,
  evaluator motion identical at 24 and 60 fps, MSpeed scaling.
* **Patch**: ranges per break, overlaps naming both fixtures, two breaks of one fixture overlapping, shared
  addresses, slot 512 overflow, unknown file / mode / universe / address, missing break, MVR stub resolution, 4750
  fixtures checked in milliseconds and every slot of 250 universes queried.
* **Manual values and mode masters** (MAC Viper): a manual `Gobo1` re-decodes the `Gobo1Pos` channel, a manual
  rotation speed moves `Gobo1` into its rotation mode, manual slot + rotation select the slot in that mode.
* **File defects**: GDTF-default 0 … 1 zoom (Briteq BTI-Blizzard Wash2 → datasheet zoom and candela), zoom sets with
  the real values (ADJ Inno Pocket Z4), a mislabelled "zoom lens rotation" next to a real zoom (BeamZ Nereid 760),
  pan/tilt 0 … 1 (Blizzard Wash2), `Pan` on a control channel (Elation Proteus Rayzor Blade L), strobe, spin and
  shake 0 … 1 (Briteq BTX-Hawk, Vari-Lite VL2600, Elation Fuze Max Spot), a shutter function named "Open" without
  sets (LIGHT SKY IP2021), a `ZoomModeBeam` channel next to the zoom (Elation Proteus Brutus FS), an LED engine with GDTF's default lamp type (ARRI Orbiter), a tunable white without emitter
  data (ROXX Neo Mini), datasheet candela shared by GDTF beam flux (Elation Rayzor 760).
* **Colour engines**: ARRI L5-C white engine follows CCT at the emitters' efficiency and ignores RGBW, the colour
  engine and the cross-fade between them, placeholder emitter outputs, a CTO channel that selects the CCT (PRstage
  Fresnel), saturation of virtual gels, electronic CTC cheaper than a filter (Elation Fuze Profile), CMY written as
  ColorAdd (LIGHT SKY F1000).
* **Engine**: live vs manual control, change detection, the console-driven predicate, incremental add / repatch /
  re-mode / remove, unknown GDTF, dynamics across frames, a move after an idle pause starts from rest, parallel =
  sequential, no allocations per frame.
* **Library-wide**: every mode of all 1091 files (7272 modes, 221 048 channel instances) compiles and evaluates
  random DMX (and random manual values) to finite, in-range values.
* **Performance**: 2000 fixtures per frame (above).

## Limitations

* Fixture-internal effect engines, macros and gel pickers are not simulated (see *Ignored*).
* CTO/CTB/CTC on LED engines move the colour like a conversion filter (chromatic adaptation of the current mix) with
  the engine's luminous cost; the firmware's actual emitter levels, and so the spectrum, are not reproduced.
* The colour of an emitter mix is only as good as the file's emitter data: files whose emitter Y values are not
  photometric (but not obviously placeholders) give a tinted full-on white (e.g. some RGBW washes come out bluish).
* `_specs` gives one candela value per fixture; multi-beam fixtures share it by the beams' GDTF flux when the file
  gives different fluxes, equally otherwise (e.g. the decorative RGB rings of a wash whose beams all keep GDTF's
  default flux get the same share as the main beam).
* Placeholder ranges are recognised only when they are exactly GDTF's default 0 … 1; other implausible ranges in the
  files (e.g. a zoom written as 0.1 … 15°) are taken as written.
