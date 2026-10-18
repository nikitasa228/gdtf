# Candela.Electrical — cables, ports, power and data networks, rigging checks

`Candela.Electrical` covers requirement 2 of [REQUIREMENTS.ru.md](REQUIREMENTS.ru.md): cables drawn in the scene,
power networks with real consumption, which fixtures a console drives, and the checks behind the issues panel. It
reads the plain Core components (`CableComponent`, `ConsoleComponent`, `PowerSourceComponent`, `FixtureComponent`,
`TrussComponent`, `RiggingPointComponent`) and fixture data from `Candela.Gdtf` (GDTF + `_specs`). It never changes
the scene. Units follow [ARCHITECTURE.md](ARCHITECTURE.md): metres, kilograms, watts, volts, amperes.

## Entry points

| Type | Purpose |
|---|---|
| `CableCatalog` / `CableType` | Cable types: signal, connector at each end, rating, cross-section, kg/m, maximum run, legend colour, icon key. |
| `ConnectorCatalog` / `ConnectorInfo` | Connector vocabulary shared by GDTF, `_specs`, outlets and cables; lookup tolerant of spelling. |
| `ConnectorCompatibility` | Cable ↔ port rules: compatible, adapter needed (warning) or wrong (error). |
| `PortResolver` / `Port` / `PortRef` | Ports of fixtures, consoles and power sources; port picker ranking for the cable tool. |
| `FixtureProfile`, `IFixtureProfileProvider`, `LibraryFixtureProfileProvider`, `InMemoryFixtureProfileProvider` | Per GDTF + mode: ports, power, power factor, weight, accepted mains, distribution ratings. Cached and thread-safe. |
| `CableGeometry` | World node positions (anchors follow entities), segment and routed lengths, slack, tree paths, plus an allocation-free position writer for drawing. |
| `NetworkSolver` → `NetworkSolution` | Data lines, console-driven fixtures, power circuits and feeds, cable currents, issues. `GetOrSolve` caches per `Scene.Version`. |
| `RiggingAnalyzer` → `RiggingSolution` | Truss and rigging-point loads, mounting checks. |
| `ElectricalIssueProvider`, `RiggingIssueProvider` | Core `IIssueProvider`s for the issues panel. |
| `PowerReport`, `DataReport`, `CableSchedule`, `CableLinkView` | View models for the UI tables and the cable link window. |
| `IssueCodes` | Stable issue codes (`power.breaker-overload`, `data.y-split`, …). |

Typical wiring in the engine:

```csharp
var profiles = new LibraryFixtureProfileProvider(fixtureLibrary);
var solver = new NetworkSolver(profiles);
issueLog.Register(new ElectricalIssueProvider(solver));
issueLog.Register(new RiggingIssueProvider(new RiggingAnalyzer(profiles)));
bool locked = solver.GetOrSolve(scene).IsConsoleDriven(fixtureEntity.Id); // cheap every frame
```

## Cable catalogue

`ConnectorA` is the source end: it plugs into an outlet, a console output or a link output. `ConnectorB` is the load
end. The rating is that of the whole assembly. Weights are for typical touring cable: H07RN-F rubber, 110 Ω DMX
pair, tour-grade Cat6.

| Id | Signal | Ends | Rating | mm² | kg/m | Max run | Colour |
|---|---|---|--:|--:|--:|--:|---|
| `dmx-xlr3` | DMX512 | XLR3 ↔ XLR3 | – | 0.22 | 0.045 | 300 m | `#A855F7` |
| `dmx-xlr5` | DMX512 | XLR5 ↔ XLR5 | – | 0.22 | 0.055 | 300 m | `#7C3AED` |
| `ethernet` | Ethernet | etherCON ↔ etherCON (Cat6) | – | 0.2 | 0.065 | 100 m | `#10B981` |
| `powercon` | Power | powerCON grey → blue | 20 A | 2.5 | 0.20 | – | `#1E3A8A` |
| `powercon-true1` | Power | TRUE1 ↔ TRUE1 | 16 A | 2.5 | 0.20 | – | `#FACC15` |
| `powercon-true1-top` | Power | TRUE1 TOP ↔ TRUE1 TOP | 16 A | 2.5 | 0.21 | – | `#CA8A04` |
| `schuko` | Power | Schuko ↔ Schuko | 16 A | 1.5 | 0.13 | – | `#14B8A6` |
| `iec` | Power | Schuko → IEC C13 | 10 A | 1.0 | 0.08 | – | `#EC4899` |
| `cee16` | Power | CEE 16 A 3-pin | 16 A | 2.5 | 0.20 | – | `#3B82F6` |
| `cee32-3p` | Power, 3 phases | CEE 32 A 5-pin | 32 A | 6 | 0.55 | – | `#EF4444` |
| `cee63-3p` | Power, 3 phases | CEE 63 A 5-pin | 63 A | 16 | 1.25 | – | `#991B1B` |
| `socapex` | Multicore, 6 circuits | Socapex 19-pin | 16 A per circuit | 2.5 | 0.95 | – | `#84CC16` |
| `harting16` | Multicore, 6 circuits | Harting Han 16E | 16 A per circuit | 2.5 | 0.85 | – | `#65A30D` |

`CableCatalog.With(...)` adds or replaces types; ids are case-insensitive.

## Connectors

* **Coverage.** The vocabulary covers every `powerConnectorIn`, `powerConnectorOut` and `dataConnectors` value in
  `_specs` (SCHEMA.md list). It also covers every WiringObject `ConnectorType` and legacy `Connector` `Type` that
  occurs in the 1091 library files. Both are tested.
* **Canonical names.** Where `_specs` spells a connector, that spelling is the canonical id.
* **Aliases.** GDTF spellings resolve too: `PowerconTRUE1`, `powerCONTRUE1TOP`, `NAC3FCA`/`NAC3MPA` (blue),
  `NAC3FCB`/`NAC3MPB` (grey), `PowerConBlue`, `CEE 7/7`, `IEC 60320-C13/14`, `RJ-45`, `TBLK`,
  `DC Power Connector (…)`, `Martin PD/PDE/DCE`, `Loose End`, `Edison`, `DIN3`, `DIN5`.
* **Lookup.** Case, spaces and punctuation are ignored.
* **Unknown names.** These stay as written, with domain `Unknown`, and are never reported.
* **List values.** `_specs` "A / B" means alternatives; "A + B" means both connectors are present
  (`ConnectorCatalog.ResolveAll`).

Rules (`ConnectorCompatibility.Check(cable, port)`):

1. **Signal first.** Power goes into power, DMX into DMX and Ethernet into Ethernet. A hybrid port (power + data on
   one connector) takes any of them. A multicore cable fans out into single power circuits through a breakout.
   Anything else is the wrong cable, reported as an **error**.
2. **Cable end.** The end that meets the port is chosen by the port's direction: the source end on an output, the load
   end on an input, the better of the two on a bidirectional port.
3. **Same family mates.** TRUE1 ↔ TRUE1 TOP, RJ45 ↔ etherCON, and a multicore tail on a power port.
4. **Keyed powerCON.** Legacy powerCON blue (power in) never mates powerCON grey (power out): **error**.
5. **Adapters.** These pairs give a **warning**: XLR3 ↔ XLR5; Schuko ↔ any CEE; legacy powerCON ↔ TRUE1; a power plug
   into a multicore outlet (needs a breakout); every other pair of the same signal.
6. **No check.** A fixed cable, terminal block or loose end, and a generic "XLR" port (the specs only say "DMX512"),
   accept any plug of the right signal.

## Ports

Fixture ports are built in this order:

1. **GDTF 1.2 WiringObjects** of the DMX mode's geometry tree (fuses are internal and skipped). The connector, the
   direction (Input/Consumer/NetworkInput → input, Output/PowerSource/NetworkOutput → output, NetworkInOut →
   bidirectional) and the signal (SignalType text, else component type and connector) are read. So are the current
   limit of power outputs (the smaller of FuseCurrent and MaxPayLoad / Voltage) and the position in the fixture.
   * An XLR port named "DMX" is DMX even when the file says "Power". The Astera AX2 GDTF does this.
   * A name that says "in" or "out" outranks the component type: the BeamZ DOT BAR and BS 1200 files type their
     "Power OUT" thru as a Consumer and "Network IN" as a NetworkOutput.
   * A GDTF limit never lifts a port above its connector rating: several files write the mains voltage into
     FuseCurrent (240 "A" on a TRUE1 thru), which stays a 16 A TRUE1 port. An inlet's fuse protects the device's own
     supply, not the thru wiring, so inlets keep their connector rating.
2. **GDTF 1.0/1.1 legacy Connectors**, used only when there are no WiringObjects.
   * The direction comes from the name ("DMX-In 5pin", "PowerCon True1-Out", "Thru", "Mains") before the gender,
     because files disagree on gender: the ADJ files give the TRUE1 inlet gender +1.
   * The DMX break comes from `DMXBreak`.
3. **`_specs` completion.** This fills what the GDTF does not describe:
   * `Power In` from `powerConnectorIn` (alternatives kept, "A + B" gives `Power In 2`);
   * `Power Out` when `powerLinkable`;
   * blank GDTF connector names, filled with the specs connector of the same role;
   * `DMX In`/`DMX Out` per DMX connector, or a generic XLR when only `controlSignal` says DMX512;
   * `Ethernet` (bidirectional).

   Data ports are only completed when the GDTF has no data port at all: the specs data connectors are derived from
   the same GDTF. Battery-only fixtures get no inlet.
4. **Distribution devices.** A specs record with `maxLoadW`, `maxInputPowerW`, `outputChannels` or `outputVoltage`
   marks a distribution device: a dimmer pack, PSU, P3 PowerPort or feeder.
   * Its power outputs become distribution outputs with a channel number and the per-channel limit.
   * If the GDTF lists no outputs, `Dimmer n` / `Output n` ports are created. The PRstage Dimmer Pack 4 gets
     `Dimmer 1..4` on Schuko at 10 A each.
   * An output with the inlet's connector next to DC outputs stays a mains link (the TRUE1 thru of a P3 PowerPort).

Every library fixture that `_specs` says needs mains power gets a power inlet, and every fixture with wired DMX gets a
data input (tested over all 1091 files).

**Consoles** get one port per `ConsoleOutput`: XLR5 for DMX512, etherCON for Ethernet (also written "Art-Net",
"sACN", "E1.31"), plus universe and count.
**Power sources** get one port per outlet (breaker, phase, connector) and a `Mains In` input, so a distro can itself
be fed from a generator or another distro.

## Data network

`NetworkSolver` walks depth first from every console output (in scene and port order):

* **Following the line.** The walk goes cable → input port → the device's other data ports of the same family
  (DMX thru, Ethernet out, switched Ethernet) → next cable.
* **Feeds.** Each device reached gets a `DataFeed`: console and output, signal, universe(s), chain index (1 = first
  device), the upstream device and port, the cable path from the console, and the run length. A plain daisy chain
  therefore comes out in chain order.
* **Splitters and segments.** A distribution device (library category OTHER) with two or more DMX outputs of the same
  connector is a splitter: the device count and run length start a new segment behind it. A lighting fixture never
  is one. Its XLR 3-pin and 5-pin thru sockets (a sixth of the library has both) are the same line, so the segment
  goes on through it. Cabling both of them branches the line without a splitter (`data.y-split`).
* **DMX breaks.** A thru repeats only the break that arrives at its input.
* **Converters.** A device with Ethernet in, DMX outputs and no DMX input is an Ethernet → DMX converter (Art-Net
  node, Martin feeder). Its n-th DMX output sends the n-th universe of the incoming range.
* **Console-driven.** `ConsoleDrivenFixtures` / `IsConsoleDriven` hold every fixture that a data path connects to a
  console. The engine uses it to lock manual controls.

| Check | Code | Severity |
|---|---|---|
| Patched fixture with no data path (only fixtures that have a wired data input, and only once the scene has a data network: a console or a data cable — an MVR import without cabling is not 91 warnings) | `data.no-signal` | warning |
| Patched universe ≠ universe arriving at the input (DMX: break of the input; Ethernet: every address in range) | `data.universe-mismatch` | error |
| DMX/Ethernet cable tree with more than two plugged ends; both thru sockets of one fixture in use | `data.y-split` | warning |
| More than 32 devices on one DMX segment (`NetworkSolverOptions.MaxDmxDevicesPerSegment`) | `data.chain-too-long` | warning |
| DMX segment cable length above the cable type's maximum (300 m); Ethernet link above 100 m | `data.run-too-long` | warning |
| A device reached twice from one output (loop or parallel path) | `data.loop` | error |
| Two outputs connected by one cable | `data.output-conflict` | error |
| A device fed by several console outputs | `data.multiple-sources` | warning |

## Power network

**Edges.** A power cable connects its one upstream end (an outlet, link output or distribution output) to its input
ends.

* A cable with two outputs is a back-feed (`power.backfeed`, error) and is ignored.
* A multicore cable assigns its tails to circuits 1..6 in endpoint order. More tails than circuits is
  `power.multicore-circuits`.

**Walk.** The solver walks from every unfed source outlet:

* **Links.** Link outputs pass the same supply on.
* **Distribution outputs.** These pass the device's output supply: DC voltage for PSUs, or mains marked "dimmed" for
  dimmer channels.
* **Fed distros.** A distro reached through its `Mains In` expands its own outlets. A distro whose `Mains In` has a
  power cable is never a source of its own: if that cable does not lead to a powered output, the distro and its
  loads are unpowered.
* **Second feeds.** A device reached a second time is fed twice (`power.multiple-feeds`, error). Only the first feed
  counts.

**Phases.**

* Single-phase outlets use their `Phase`.
* Multicore outlets rotate their circuits over L1–L3 on three-phase sources.
* Three-phase outlets (5-pin CEE) pass all phases to a three-phase distro through a three-phase cable. A single-phase
  device, or a single-phase lead, behind one sits on the outlet's phase. A three-phase distro on a single-phase feed
  is `power.phase-mismatch` (error).
* **Voltage.** `VoltageV` of a source is the voltage its single-phase loads see. A three-phase source entered with
  its line-to-line voltage (300 V or more: 400 V, 415 V) is converted to line-to-neutral (V / √3 = 231 V, 240 V);
  below 300 V it is taken as line-to-neutral already.

**Loads.** Loads are computed post-order, so nothing is counted twice:

* **Own draw.** `_specs` `powerConsumptionW`; else the sum of GDTF `PowerConsumption` Value × PowerFactor; else the
  consumers' `ElectricalPayLoad`; else the sum of beam power. The beam fallback is skipped when every beam carries the
  GDTF default of 1000 W, which means the attribute was not written. Estimated or unknown draws give a
  `power.estimated-load` note.
* **Power factor.** From GDTF PowerFactor or CosPhi when it is below 1, otherwise 0.95.
* **Apparent power and current.** VA = W / cos φ (VA = W on DC), and I = VA / V per phase.
* **Distribution devices.** For a dimmer, PSU, P3 PowerPort or feeder, the mains side carries the device's own draw
  (its `powerConsumptionW`, own draw only) plus the downstream loads × losses. The loads are their own devices.
  * Losses are (`maxInputPowerW` − own) / `maxLoadW`, clamped to 1…2: 1.00 for the Dimmer Pack 4, 1.15 for a P3
    PowerPort 2000.
  * `maxInputPowerW` is never added on top of the loads. A Dimmer Pack 4 feeding four 175 W PARs draws 10 + 700 W.

| Check | Code | Severity |
|---|---|---|
| Outlet circuit current above the breaker (per circuit on multicore outlets) | `power.breaker-overload` | error |
| Link output current above its limit (fuse / payload / connector rating, TRUE1 16 A) | `power.link-overload` | error |
| Inlet current (own + everything behind) above the inlet connector rating | `power.inlet-overload` | error |
| Cable current above the cable type rating (the message names what the cable feeds) | `power.cable-overload` | error |
| Distribution channel above `maxLoadPerChannelA`; total above `maxLoadW` / `maxLoadCurrentA` | `power.channel-overload`, `power.distribution-overload` | error |
| Fixture needing mains with no supply (only once the scene has a power network: a power source or a power cable; the fixtures stay listed in `UnpoweredDevices`); fed distro whose feed has no power | `power.unpowered` | warning |
| Supply not in `supportedPowerNetworks`, outside the voltage/frequency range, or DC on a mains fixture | `power.voltage-mismatch` | error |
| Three-phase distro fed from a single-phase outlet or lead | `power.phase-mismatch` | error |
| Three-phase source with (max − min) / max > 20 % and max ≥ 2 A | `power.phase-imbalance` | note |
| LED/discharge fixture on a dimmed channel | `power.dimmed-electronic-load` | note |
| Source with an invalid voltage | `power.source-invalid` | error |

**Accepted supply** (`FixtureProfile.AcceptsSupply`):

* **AC.** The supply is accepted when it matches a `supportedPowerNetworks` entry (nominal voltage ±6 %, ±1 Hz) or
  lies inside `voltageRangeV` / `frequencyRangeHz` (±3 % / ±1 Hz). Devices whose range is below 90 V are low-voltage
  devices and are judged by their range only.
* **DC.** The voltage must lie inside the range (±5 %).
* **Unknown data** accepts everything.

Messages name the devices as `#FixtureId 'Name'` and carry the numbers, for example:

> Outlet 'Outlet 1' of 'Distro SL' is overloaded: 3 devices draw 4740 W = 21.7 A on a 16.0 A breaker (136 %):
> #1 'AECO 1', #2 'AECO 2', #3 'AECO 3'.

## Cables and connector issues

| Check | Code | Severity |
|---|---|---|
| Unknown cable type (cable ignored by the networks) | `cable.unknown-type` | warning |
| Endpoint on a deleted entity / unknown port / missing node | `cable.endpoint-missing`, `cable.unknown-port`, `cable.endpoint-node-missing` | warning |
| Wrong signal or non-mating keyed connector | `cable.wrong-connector` | error |
| Different connectors of the same signal | `cable.adapter-needed` | warning |
| Two plugs in one port (the second is ignored) | `cable.port-occupied` | error |
| Anchor entity missing (stored position used); NaN nodes, broken segments, loops | `cable.anchor-missing`, `cable.invalid-geometry` | warning |
| GDTF not in the library; unreadable GDTF, missing mode, or a similar model used as substitute | `fixture.unknown-type`, `fixture.data-incomplete` | note |

## Cable geometry

* **Node positions.** An anchored node sits at `anchor.LocalOffset × anchorEntity.WorldMatrix`, so it follows the truss
  or device it is attached to. A free node sits at `node.Position × cableEntity.WorldMatrix`.
* **Lengths.** The cut length is the routed length (sum of segments) × `SlackFactor`; a slack below 1 or NaN counts as
  1. The weight is that length × kg/m.
* **Paths.** `PathLengthM` / `Path` measure between two nodes along the tree. The data walk uses them for run lengths
  and the cable link window for the length of each hop.
* **Drawing.** `WriteNodeWorldPositions` fills a span without allocating, for drawing cables every frame.

## Rigging

`RiggingAnalyzer` runs these checks:

* **Mounts.**
  * A fixture is mounted when `MountedOn` names its carrier. Without `MountedOn`, a fixture under a truss entity in
    the hierarchy hangs on that truss, and one under a rigging point entity hangs from that point.
  * An unmounted fixture more than 0.5 m above the floor gets `rigging.not-mounted` (warning, "not attached to a
    truss/pipe"). The floor is the highest `WallKind.Floor` rectangle below the fixture, taken as centred on its
    entity origin, or Z = 0.
  * A `MountedOn` that points at nothing is `rigging.mount-missing` (error).
* **Truss load.** This is the fixture weights (`_specs` `weightKg`, else GDTF Weight) + 0.5 kg per clamp + cable weight
  along the truss.
  * A cable segment counts fully when both its nodes are anchored to the truss or to fixtures hanging on it. It counts
    half when only one end is.
  * The total is compared with `MaxUniformLoadKg` (`rigging.truss-overload`), and the heaviest single item with
    `MaxPointLoadKg` (`rigging.truss-point-overload`). Both are errors.
  * **Bending.** Fixtures are point loads, so their bending moment is checked as well. The truss is taken as a beam
    simply supported at both ends of its length. M is the largest moment of all carried items. The equivalent
    mid-span point load 4·M / L is compared with `MaxPointLoadKg`. Without a point rating, the equivalent uniform
    load 8·M / L is compared with `MaxUniformLoadKg`. Two 25 kg fixtures 0.2 m apart at the middle of a 6 m truss bend
    it like 49 kg at mid-span, although each is below a 30 kg point rating.
  * Overload messages name the heaviest fixtures with their weights.
* **Rigging points.** Each point carries a share of every truss in its `Carries` list. The shares come from the lever
  rule between neighbouring points along the truss axis; overhangs go to the end pair, with uplift clamped to 0.
  * The truss's own weight is spread along its length. Without `WeightKg` it is estimated from the profile: F34
    4.5 kg/m, F44 6 kg/m, steel pipe 3.6 kg/m, and so on.
  * Fixtures hung straight from the point add their weight.
  * A point carrying more than `CapacityKg` is `rigging.point-overload` (error). A `Carries` entry that points at
    nothing is a warning.
* **Unknown weight.** A mounted fixture with unknown weight gets `rigging.unknown-weight` (note) and is counted as
  0 kg.

## Reports for the UI

* **`PowerReport`.** One row per source, outlet and circuit: load W, apparent power VA, current A, % of breaker, phase,
  connector, devices. It also gives per-source phase currents, unpowered devices, and the total drawn from unfed
  sources (fed distros are inside their feeding circuit).
* **`DataReport`.** One entry per console output with its universe(s) and the chain of devices in cabling order:
  chain index, input port, patch address, whether the patch matches, cable label, run length. It also lists patched
  fixtures that receive no data.
* **`CableSchedule`.** Every cable with label, type, cut and routed length, weight, the source end, the other ends,
  current and power. It also gives totals per type.
* **`CableLinkView`.** The strip of the cable link window: every device along the line the cable belongs to, in order
  from the source, with the hops between them and the selected cable highlighted.
  * For a data cable the line is the console output's chain; for a power cable it is the outlet circuit.
  * A cable that is not part of a fed line shows the devices plugged into it.
* **`NetworkSolution.CableAt(entity, port)`.** The cable plugged into a port, for the connector illustrations in the
  properties panel. Each connector has an `IllustrationKey`.

## Robustness and performance

* **Malformed data.** Nothing in the scene makes the solver or analyzer throw. Null list entries, dangling ids, NaN
  positions, unknown types, ports and GDTFs, unreadable files and invalid voltages each become an issue, and the rest
  of the scene is still analysed.
* **Linear time.** Ports, plugs, edges and feeds are dictionaries; walks are iterative, so a long chain cannot overflow
  the stack. 1000 fixtures with 2000 cables (40 DMX lines, 80 power runs) solve in about 90 ms.
* **Caching.** `GetOrSolve` returns the cached solution while `Scene.Version` is unchanged. Profiles are cached per
  GDTF + mode and keep no reference to the parsed GDTF.

## Tests

`tests/Candela.Electrical.Tests` has 87 tests and runs in about 20 s. It uses the repository's GDTF library and
`_specs`:

* **Vocabulary.** All `_specs` connector values and all GDTF connector names resolve. The connector rules are checked.
* **Ports.** Ports come from WiringObjects (Ayrton Naja), from `_specs` (ACME AECO 20, which has no connectors in its
  GDTF) and from legacy connectors (ADJ COB Cannon). Also tested: the Dimmer Pack 4 outputs, P3 PowerPort DC outputs
  with losses, and inlet/data ports for every library fixture.
* **Data.** Ten moving heads daisy-chained from a console output, in chain order, with universe and console drive
  (also through the data report and cable link view). Also: universe mismatch, Y-split, no data, 33 devices and
  300 m, loops, connected outputs, adapters vs wrong cables, Ethernet universes, Ethernet → DMX converter (Martin
  feeder), two sources, and caching.
* **Power.** Three 1580 W fixtures linked from one 16 A outlet (breaker, inlet and cable overload with numbers), link
  overload, the Dimmer Pack 4 without double counting, a dimmer channel limit, 120 V on a 200–240 V BMFL Blade, an
  unpowered fixture and distro, a generator feeding a three-phase distro with imbalance, Socapex circuits over phases,
  back-feed and double plugs, and DC behind a P3 PowerPort.
* **Geometry and rigging.** Anchored cable length follows a moved and rotated truss; tree paths; broken data;
  allocation-free drawing. Truss overload (uniform and point), unmounted, riser and missing mount, cable weight on a
  truss, lever-rule rigging points, unknown weight.
* **Integration.** Malformed scenes, the issue log with both providers, and a 1000-fixture timing.
* **Review regressions.** A 34-fixture chain of BeamZ Cobra 100 Spots (XLR3 + XLR5 thru) still trips the 32-device
  warning, while a real splitter starts new segments. Also tested: both thru sockets in use, DMX breaks, the 240 A
  "fuse" of the BeamZ Ignite 740 thru, thru sockets typed as inputs, 400 V three-phase sources, a three-phase distro
  on a single-phase feed, a distro on a dead `Mains In` cable, a substitute model, overload values printed next to
  their limits, a fixture hung under a hoist, and two fixtures that bend a truss past its point rating.

## Assumptions and known gaps

* **Port directions.** The direction of a legacy GDTF connector without "in"/"out" in its name falls back to its
  gender, which manufacturers use inconsistently. Unnamed RJ45 ports are bidirectional.
* **Specs-only fixtures.** These get one `DMX In`/`DMX Out` pair per listed DMX connector and one bidirectional
  `Ethernet` port; a second Ethernet port is not invented.
* **Power and phases.** Currents add apparent power arithmetically (conservative; no vector sum of different power
  factors). Three-phase loads are taken as single-phase loads per phase: a 400 V three-phase fixture is not
  modelled. A three-phase source voltage below 300 V is taken as line-to-neutral, so a US 208Y/120 V system must be
  entered as 120 V.
* **Converters.** Ethernet → DMX converters map universes to outputs in port order. Node configuration is not
  modelled.
* **Truss statics.** The lever rule treats a truss on three or more points as simply supported spans, not a
  continuous beam. The bending check uses the truss length as the span, whatever its hanging points. Shear and
  deflection limits of short spans are not modelled. Floors are assumed centred on their entity.
* **Splitters.** A DMX splitter is recognised by its library category (OTHER) and two outputs with the same connector.
  A fixture without a `_specs` record also counts as OTHER.
* **Patch overlap.** Address overlap between fixtures is a patch check of `Candela.Dmx` and is not done here.
