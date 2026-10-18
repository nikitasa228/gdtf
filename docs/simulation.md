# Simulation: smoke, fire and sound (`Candela.Simulation`)

CPU reference implementations of everything in the scene that moves on its own: smoke and haze (requirement 1 —
"light cannot be complete without a smoke system"), flames of GENERATOR FIRE devices (requirement 3) and the
sound-pressure field behind the noise overlay. The GPU versions in the renderer are tested against these classes, so
they favour clear physics in SI units over tricks, and every number below is either a physical constant, a published
correlation or a stated assumption. The module references only `Candela.Core` and `Candela.Photometry`; the engine
turns DMX-evaluated fixture states into the emitter descriptors these classes take each step.

| Class | What it does | Output for the engine / renderer |
|---|---|---|
| `SmokeSimulation` | Eulerian stable-fluids solver over the `AtmosphereVolumeComponent` box: air velocity, smoke density, temperature | `Density` (x fastest, then y, then z) + `Bounds`, `SizeX/Y/Z` → `VolumeGrid`; `DensityAt`, `TotalSmokeMass` |
| `FireSimulation` | flame devices: valve, burn timer, burning fuel parcels | `LightSamples` (position, `LightColor`, cd), `Volumes` (temperature in K) / `RasterizeTemperature`, `SmokeEmitters` |
| `AcousticField` | SPL of noise sources (`_specs` `noiseLevelDb`) | `LevelAt`, `ComputeGrid` (dB(A) grid for the volumetric noise fog), `SampleSurface`/`SampleRectangle` |
| `FlameModel` | flame correlations (height ↔ heat release, luminous flux, gas velocity, flicker) | used by `FireSimulation`, handy for UI readouts |

A frame in the engine:

```csharp
fire.Step(dt, flameDevices);                         // GENERATOR FIRE fixtures (output, burn time, height)
emitters.Clear();
emitters.AddRange(hazersFogsFans);                   // SmokeEmitter.Hazer / FogMachine / Fan from fixture states
emitters.AddRange(fire.SmokeEmitters);               // soot and heat of the flames
smoke.Step(dt, CollectionsMarshal.AsSpan(emitters)); // fixed sub-steps inside
atmosphere.SmokeDensity = new VolumeGrid { Min = smoke.Bounds.Min, Max = smoke.Bounds.Max, SizeX = smoke.SizeX, …,
                                           Values = copy of smoke.Density };
lights.AddRange(fire.LightSamples);                  // isotropic point lights, colour × candela
fire.RasterizeTemperature(fire.CombinedBounds, nx, ny, nz, flameGrid);
```

All three are deterministic (identical inputs and identical `dt` sequences give bit-identical results, whatever the
thread count), have `Reset()`, and smoke and fire capture and restore their complete state
(`CaptureSnapshot`/`RestoreSnapshot`) for scrubbing a timecode show: keep snapshots at key frames and re-simulate
forward from the nearest one. Bad input — NaN settings, broken obstacle boxes, emitters with non-finite positions,
duplicate devices — is replaced or skipped and reported (`Issues`, `IgnoredEmitters`), never thrown at run time.

## 1. Smoke and haze (`SmokeSimulation`)

### Equations

Air is an incompressible fluid in the Boussinesq approximation; smoke is a passive aerosol that only loads the air
with its weight:

* momentum: ∂**u**/∂t + (**u**·∇)**u** = −∇p/ρ + ν∇²**u** + **b** + **f**
* incompressibility: ∇·**u** = 0
* buoyancy: **b** = ẑ·(g·θ/T_a − g·c/ρ_a) — θ = T − T_a is the temperature excess (exactly (ρ_a − ρ)/ρ for an ideal gas
  at constant pressure), c the aerosol mass concentration, ρ_a = 1.204 kg/m³, T_a = 293.15 K, g = 9.80665 m/s²
* smoke and heat: ∂d/∂t + **u**·∇d = D∇²d + S_d − k_d·d,  ∂θ/∂t + **u**·∇θ = D∇²θ + S_θ − k_θ·θ

**f** are the fan and nozzle jets and the ventilation draft, S the emitter sources, k_d = −ln(1 − f) the dissipation of
the atmosphere component (fraction f lost per second: evaporation, ventilation) and k_θ = 0.05 /s a lumped heat loss to
surfaces and by radiation that the grid does not resolve. ν and D default to 0 (numerical diffusion of the advection
already exceeds air's molecular values at grid scale).

**Density** is the relative aerosol concentration the renderer uses: σ_s = density × `ScatteringAtFullDensity` (1/m,
from the atmosphere component, default 0.5 /m). Mass enters through the mass scattering coefficient
k_s = 3000 m²/kg (Mie theory for ≈ 1 µm glycol/water or mineral-oil droplets, k = 3·Q_ext/(2·ρ·d) with Q_ext ≈ 2.5,
ρ ≈ 1050 kg/m³), so density 1 ↔ c = 0.5/3000 = 1.7×10⁻⁴ kg/m³ (0.17 g/m³).

### Grid and time stepping

A staggered MAC grid over the box: velocity components on cell faces, density, temperature and pressure at cell
centres. The cell size comes from the atmosphere component (default 0.25 m) and is enlarged (with a *Note* issue) until
the grid fits the budget (`MaxCellsPerAxis` 128, `MaxCellCount` 1 M cells ≈ 110 MB of solver state); the box is
rounded up to whole cells around its centre. `Step(dt, emitters)` runs fixed sub-steps of `TimeStep` (1/30 s) and
carries the remainder to the next call; more than `MaxSubStepsPerCall` (4) sub-steps in one call drop the rest of the
frame (counted in `DroppedTime`) so a slow frame cannot snowball. One sub-step:

1. **Sources** — emitters add aerosol mass and heat at exactly their rates (below).
2. **Forces** — buoyancy on the vertical faces; fan and nozzle jets; ventilation draft.
3. **Velocity advection** — semi-Lagrangian (Stam 1999): every face takes the velocity found by tracing back along the
   flow for one step with a midpoint (RK2) trace, interpolated trilinearly. Midpoint velocities come from a
   cell-centred copy of the field (one interpolation for all three components); the final value from the staggered
   component itself. Values are capped at `MaxSpeed` (60 m/s) as a safety net.
4. **Viscosity** (optional) — backward-Euler diffusion (1 + nλ)·x − λΣx_nb = x₀, λ = ν·dt/h², 8 Jacobi sweeps;
   walls are excluded from the stencil (free slip).
5. **Pressure projection** — see below.
6. **Scalar advection** of density and temperature with the projected velocity: semi-Lagrangian, by default with the
   MacCormack correction (Selle et al. 2008): trace back, trace the result forward again, correct by half the
   round-trip error and fall back to the plain value wherever the correction would leave the range of the eight
   interpolated corners (so it stays stable and creates no new extrema). Solid corners are skipped and the remaining
   weights renormalised (smoke is neither pulled into walls nor diluted by them); beyond an open face the air is
   ambient (no smoke, no excess heat). Then optional diffusion (as 4.), the **conservation fixer** (after the
   diffusion, whose few Jacobi sweeps are not exactly conservative either), and finally **dissipation**
   (d ×= (1 − f)^dt) and **cooling** (θ ×= e^(−k_θ·dt)).

### Pressure projection

With the scaled pressure φ = p·dt/ρ, a free face between cells a and b is corrected by u −= (φ_b − φ_a)/h. Zero
divergence in every fluid cell gives the 7-point Poisson system

  n_c·φ_c − Σ_nb φ_nb = −h²·(∇·**u**)_c,

where n_c counts the cell's free faces. Faces to solids and closed domain faces are walls: they drop out and keep zero
normal velocity (**free slip**: the tangential velocity is untouched). An **open** domain face couples to the ambient
pressure φ = 0 outside (Dirichlet), so air and smoke leave and enter freely. With a ventilation draft the faces it
enters through are inflow faces with prescribed velocity. A fluid region with no open face (a closed room, a sealed
pocket inside obstacles) is singular: its right-hand side's mean is removed so the system stays consistent (connected
regions are labelled when the obstacles change), and the conjugate-gradient residual is projected the same way after
every iteration. Without that, float rounding lets a constant creep into the residual; the multigrid preconditioner (an
approximate inverse of the singular operator) amplifies it until the iteration breaks down — in a closed box at a
tolerance of 10⁻⁶ the "projected" flow kept up to 3300× the divergence it started with (regression-tested). A
non-positive r·M⁻¹r also ends the solve.

The symmetric system is solved by conjugate gradients **preconditioned with a geometric multigrid V-cycle** (MGPCG,
McAdams et al. 2010), warm-started from the previous step's pressure, until the largest remaining cell divergence is
below `PressureTolerance` (10⁻⁴) × the largest divergence before the projection (`MaxDivergenceBefore/After` in the
statistics; typically 10 1/s → 10⁻³ 1/s). Each coarser level halves the resolution; a coarse cell takes part when any
child does and a coarse face is open when any fine face it covers is, so thin walls stay closed on every level. The
V-cycle uses symmetric red-black Gauss–Seidel (red–black before, black–red after the coarse correction), restriction =
½ × the sum of the children (the h² scaling: b_H = H²f = 4·mean(b_h)), piecewise-constant prolongation and 16 symmetric
sweep pairs on the ≤ 4³ coarsest level; restriction = ½·prolongationᵀ and mirrored smoothing keep the preconditioner
symmetric. Three exact shortcuts halve its memory traffic: the zero initial guess makes the first red sweep x = b/n;
black residuals are exactly zero right after a black sweep, so only red residuals are restricted; and the prolonged
value of a red cell is only read by the next black sweep, which therefore adds the parent's correction on the fly. On
the 64³ benchmark multigrid needs 8.5 iterations per step against 42 for the Jacobi preconditioner
(`PressurePreconditioner.Jacobi`, kept as the simple alternative; both agree to 2×10⁻⁵ in density — tested on odd
grid sizes, open and closed boxes, obstacles and a sealed pocket).

### Conservation fixer

Semi-Lagrangian advection does not conserve: in recirculating flows the plain scheme lost 10–25 % of the smoke over
200 steps and MacCormack gained up to 60 %; in a strong plume MacCormack created twice the injected heat. After every
scalar advection the field is therefore corrected to the total it must have — the total before advection minus what
left through open faces (Σ u_n·q·dt/h over outflow faces, upwind value) — with x += δ·|x|, δ = (target − Σx)/Σ|x|,
|δ| ≤ 0.5. For smoke this is a uniform rescaling; for the signed temperature excess warm and chilled air are corrected in
proportion to their magnitude and no value changes sign. This is the global "mass fixer" of semi-Lagrangian tracer
transport in atmospheric models (Priestley 1993); typical corrections are 0.1–2 % per step (`MassCorrection`,
`HeatCorrection` in the statistics). `ConserveMassAndHeat = false` switches it off.

### Obstacles

`SetObstacles` takes oriented boxes (`ObstacleBox`: centre, half extents, rotation; `FromAabb`, `FromLocalBox` from an
entity's world matrix) — walls, floors, risers, truss sections, fixture bodies. A cell is solid when its centre lies
inside the box inflated to at least one cell of thickness, so a 2 cm wall never leaks (tested). Smoke inside new solids
is removed; invalid boxes are skipped with a warning. Cost is proportional to the cells under the boxes' bounds.

### Emitters

The engine builds a `SmokeEmitter` per device and step from the fixture position, aim and DMX output (0..1). Nozzles are
far smaller than a cell, so the simulation converts them:

* **mass and heat** go into the cells of a sphere of radius R = max(r₀, h) in front of the nozzle at exactly the
  nozzle's rates: ṁ × output and H = ρ_a·c_p·U₀·πr₀²·ΔT₀ (× output for smoke machines);
* **momentum** of fans and smoke-machine nozzles is imposed as a turbulent round jet: centre-line speed U₀ in the
  potential core (x < B·D), U₀·B·D/x beyond, Gaussian profile exp(−ln2·r²/b²), half-width b = D/2 + S·x, B = 5.8,
  S = 0.094 (Hussein, Capp & George 1994), followed until 0.2 m/s or the box diagonal. Where the jet is narrower than a
  cell it is widened to b' = h and slowed to u·b/b', which keeps its momentum flux (∝ u²b²) — the grid-filtered jet.
  Only the axial component is relaxed toward the jet (time constant 0.25 s, weighted by the profile and the squared
  direction cosine), so buoyancy and cross flow still bend it and the surrounding air is entrained, not held;
* a **plume** (hot gases of a flame) holds its exit velocity U₀·r₀/R in the source sphere only where the air is slower,
  and is otherwise driven by its own buoyancy.

| Emitter | Defaults (full output) | Source / assumption |
|---|---|---|
| `Hazer` | 80 mm outlet, 3 m/s, +2 K (≈ 36 W), 0.04 g/s aerosol | touring hazers run 3–8 h per litre at full output, about half of the fluid ends up as long-lived droplets; water-based fluid leaves slightly warm, oil crackers at ambient |
| `FogMachine` | 20 mm effective nozzle, 10 m/s, heat = ½ heater power (+197 K at the nozzle for 1.5 kW), aerosol = heater power × 0.5 g/1.5 kJ (0.5 g/s for 1.5 kW) | vaporising glycol/water fluid takes ≈ 1.5 MJ/kg, about half (the glycol) condenses into visible droplets → ≈ 15 min per litre at 1.5 kW, as datasheets state; momentum flux ≈ 0.04 N throws the fog 2–4 m; heater power = the `_specs` `powerConsumptionW` (median of the FOG units: 1.5 kW) |
| `FogMachine(lowFog: true)` | 150 mm duct, 1 m/s, −15 K (≈ −320 W), same aerosol rate | fog chilled with ice or CO₂: sinks and spreads over the floor (tested) |
| `Fan` | 400 mm outlet, 10 m/s at full speed (≈ 1.3 m³/s), speed × output | typical 400 mm stage wind machines; no smoke |
| `Plume` | explicit radius, speed, mass rate, temperature excess | used by `FireSimulation` |

Ventilation (`Airflow` of the atmosphere component) is a draft: inflow faces ramp toward it with
`AirflowResponseTime` (2 s) and the whole air mass receives the same uniform increment (the body force of the pressure
difference driving the draft). It needs an open face to enter and another to leave; otherwise it is ignored with an
issue.

### Outputs and queries

`Density` and `TemperatureExcess` (spans, x fastest, valid until the next step), `CopyDensityTo`, `Bounds`, `SizeX/Y/Z`,
`CellSize`; `DensityAt`, `TemperatureAt`, `VelocityAt` (trilinear, 0/ambient outside the box and in solids);
`TotalSmokeMass` (kg), `AverageDensity`, `SmokeCentreOfMass`, `MaxDivergence`; `AddSmoke` and `Fill` for scripted puffs
and pre-hazed rooms; `LastStatistics` (sub-steps, CG iterations, divergence before/after, ignored emitters, dropped
time, fixer corrections) and `LastTimings` (ms per stage).

### Determinism and threading

Not thread-safe; called from one (simulation) thread. Work is split over Z slices with `Parallel.For`; no slice writes
outside itself, and every reduction (dot products, sums, maxima) is accumulated per slice and combined in slice order,
so results are bit-identical for any `MaxDegreeOfParallelism` (tested with 1, 2 and all cores). Grids below 8192 cells
run on one thread. Per-slice delegates are created once; apart from `Parallel.For`'s own bookkeeping, stepping does
not allocate (tested single-threaded).

### Performance

64³ cells (16 m cube at 0.25 m), a fog machine, a hazer, a fan, a 150 K plume and a warm puff; CG with the multigrid
preconditioner, MacCormack advection; Release build on this machine (4 vCPU Xeon @ 2.1 GHz, KVM), shared with other
build/test jobs while measuring:

| Configuration | ms per step | steps/s | notes |
|---|---|---|---|
| 4 threads, machine load ≈ 6 | 84 | 11.9 | best observed; stages: velocity advection 16, projection 41, scalar advection 23, forces 4 ms |
| 4 threads, machine load 10–19 (final code) | 128–187 | 5.3–7.8 | the VM's 4 cores were shared with 10–19 runnable processes, so the run got little more than one core |
| 1 thread (process CPU time, final code) | 128–155 | — | the work itself: velocity advection ≈ 33, projection ≈ 54–64, scalar advection ≈ 45–51, forces ≈ 5 ms |

The 10 steps/s target is met when the four cores are available (11.9 steps/s at load ≈ 6, a 1.6× speed-up over one
thread); on a saturated machine the wall time is bounded by the single-thread work of ≈ 130 ms. A later re-measurement
(review, Release, machine load 7–31 from other jobs) gave 128–150 ms of CPU per step single-threaded (5.7–6.8 steps/s)
and no gain from more threads at that load, so the target could not be re-confirmed then.

Fans and nozzle jets are imposed over their whole reach (up to the box diagonal), in parallel over slices; eight wind
machines add ≈ 20 ms per 64³ step on four threads (≈ 30 ms on one).

The work per step fell from ≈ 205 to ≈ 130 ms of CPU time during optimisation (bounds-check-free interpolation,
cell-centred midpoint velocities, branch-free CG rows, multigrid with fused passes). Projection was 76 ms with
Jacobi-PCG (42 iterations) against 52 ms with MGPCG (8.5 iterations) single-threaded; the CG is memory-bound, which
limits the gain from more threads on this VM. A looser `PressureTolerance` of 10⁻³ needs 6.2 instead of 8.5
iterations (≈ 20 % less projection time) with practically identical smoke (centre of mass equal to 10⁻⁵ m after 30
steps); the reference keeps 10⁻⁴. `SmokePerformanceTests` prints the current numbers.

### Limits

* Coarse grids: sub-grid turbulence is not modelled (no Smagorinsky/eddy viscosity), so resolved plumes and jets entrain
  too little ambient air: a 2 MW flame's plume on a 0.4 m grid is 3–4× hotter 2.5 m above the flame than Heskestad's
  correlation (≈ 180 K), though its total heat is exact. Optional constant `Viscosity`/`Diffusivity` can compensate
  globally.
* Fan jets and nozzle jets are imposed mean fields, not resolved turbulence; jets do not bend around obstacles in the
  first cells from the nozzle.
* The atmosphere is one box with axis-aligned walls/openings; obstacles are voxelised boxes (no curved geometry);
  smoke does not stick to or deposit on surfaces; droplets do not evaporate locally (only the global dissipation).
* Light absorption by the smoke (soot) is not represented by `Density` — it is scattering only.

## 2. Fire (`FireSimulation`, `FlameModel`)

Stage flame projectors (GENERATOR FIRE) fire propane jets that burn as columns of 3–8 m. A `FlameEmitter` per device
and step gives its `Id` (state is kept per id), nozzle `Position`/`Direction`, `Output` 0..1 (fuel valve opening),
`NominalHeight` (m at full output), `BurnTime` (s), optional `NozzleDiameter` (5 cm) and `HeatReleaseRate` (W; derived
from the height when not given).

### Heat release and flame height

Heskestad's flame-height correlation L = 0.235·Q^(2/5) − 1.02·D (Q in kW, L and D in m) gives the heat release for the
nominal height, Q = ((L + 1.02·D)/0.235)^(5/2), and the height for any valve opening (L ∝ Q^(2/5): half output →
0.76 × the height; tested). Propane burns at 46.3 MJ/kg (lower heating value).

### Light: radiated power × luminous efficacy

A fraction χ_r of the heat is radiated, mostly by glowing soot (χ_r = 0.25 for propane; Tewarson, SFPE Handbook:
0.25–0.30). Soot radiates close to a grey body, whose luminous efficacy equals the black body's K(T) at its radiation
temperature (a constant emissivity cancels). With T_r = 1800 K (flame colour temperatures 1800–2000 K; a candle ≈
1850 K), K = 0.62 lm/W (Planck's law integrated against the CIE observer by `Candela.Photometry.BlackbodyRadiator`):

  Φ = χ_r · Q · K(T_r) ≈ 0.155 lm per watt of heat release,  I = Φ/4π.

Check: a candle (Q ≈ 80 W) gives 0.99 cd — the unit's historical origin (tested). Plausible magnitudes:

| Nominal height | Q | Propane | Φ | I (mean) | Gas velocity at the tip | Flicker |
|---|---|---|---|---|---|---|
| 3 m | 0.61 MW | 13 g/s | 94 klm | 7.5 kcd | 6.6 m/s | 2.0 Hz |
| 5 m | 2.14 MW | 46 g/s | 332 klm | 26 kcd | 8.4 m/s | 1.6 Hz |
| 8 m | 6.87 MW | 148 g/s | 1.07 Mlm | 85 kcd | 10.7 m/s | 1.3 Hz |

A 5 m column thus puts ≈ 260 lx on a surface 10 m away. Seen from the side a tall column (a Lambertian cylinder,
I⊥ = Φ/π²) is ≈ 25 % brighter than the mean I = Φ/4π the isotropic samples use; seen end-on it is dimmer.

### Dynamics: burning fuel parcels

While the valve is open (it follows the output with a 30 ms time constant) every 1/120 s sub-step releases 8 parcels,
each carrying Q·dt/8 of heat, which it releases uniformly over its life — the time it takes from the nozzle to the tip,
`IgnitionTime` = 0.2 s ± 15 %. A parcel at progress s = age/life sits at s·L along the axis (a tilted nozzle bends
toward the vertical by ½·s²·L along the up-direction normal to it), scattered uniformly over the column's cross-section
(diameter 0.18·L, widening from the nozzle and narrowing toward the tip), and the whole column sways as a wave
travelling up the flame at the pulsation frequency f = 1.5/√D_flame (Cetegen & Ahmed 1993) with amplitude 0.06·L·s².
All randomness is an integer hash of (seed, device id, parcel number) — deterministic and platform independent.

Consequences, all tested: the flame grows over ≈ 0.2 s after ignition (80 % of its height after 0.19 s, full after
0.3 s); after the valve closes the base lifts off and the flame is gone ≈ 0.3 s later (75 % of the light within 0.1 s);
no light, volume or smoke before ignition or after burn-out. `BurnTime` > 0 closes the valve after burning that long
until the output is released to 0 and raised again (shot length / safety time-out).

Soot radiation temperature along the flame: 0.72·T_r at the blue base, T_r from 12 % to 60 % of the height, cooling to
0.8·T_r (≈ 1440 K) at the orange tip. A parcel's share of the light is its heat × L_bb(T)/L_bb(T_r), normalised so that a steady flame
emits exactly Φ = χ_r·Q·K(T_r) (during ignition, with only the dim base burning, it emits less).

### Outputs

* **Light samples** (`LightSamples`, 4 per flame): the parcels grouped by progress into sections; each sample sits at
  its section's light-weighted centre, with the black-body colour (`LightColor`) of its flux-weighted chromaticity
  mix, intensity Φ_section/4π in cd, its mean temperature and the column radius (light size for soft shadows). The
  samples of a 5 m flame are 6.6, 9.8, 8.7 and 1.5 kcd at 1780, 1800, 1780 and 1595 K CCT; the flame as a whole is
  ≈ 1775 K (test: every sample of 3 m, 5 m and 8 m flames within 1500–2200 K, on the Planckian locus).
* **Temperature volumes** (`Volumes`, one per burning flame, ≤ 48 cells per axis, ≈ 0.1 m cells): parcels splat their
  temperature with the kernel (1 − q²)² of radius 0.35·D_flame·(0.6 + 0.6·s); a cell gets Σk·T/max(Σk, 1) — the mean
  temperature where parcels overlap, fading to 0 (= no flame) at the edge. `EmissionCoefficient` κ makes the volume's
  optically thin emission Σ 4π·κ·L_bb(T)·V equal Φ, so a volumetric renderer emitting κ·L_bb(T) per metre agrees with
  the light samples (tested against the exact Planck integration). `RasterizeTemperature` merges all flames into one
  grid (the renderer's `FlameTemperature`); `TemperatureAt` samples them.
* **Smoke and heat** (`SmokeEmitters`): the convective heat (1 − χ_r)·Q and the soot (yield 0.024 kg/kg of propane,
  Tewarson) are released along the flame, one `Plume` per section with the heat and soot of the fuel burning in it, at
  McCaffrey's gas velocity u = 1.93·Q_c^(1/5) (Q_c in kW) over the column cross-section — as fire models spread the
  combustion heat over the flame volume; releasing megawatts at the tip would put it into a few cells. A 5 m flame
  produces 1.1 g/s of soot; its plume rises at 10–20 m/s.

### Limits

The flame is a kinematic model driven by correlations, not a combustion simulation: it does not react to wind or
obstacles, flames do not merge, and the soot temperature profile is prescribed. Liquid-fuel (Isopar) flame effects can
be represented by their heat release (`HeatReleaseRate`) but use propane's radiant fraction and soot yield.

## 3. Sound pressure (`AcousticField`)

Noise sources are points with their SPL at 1 m in dB(A) — the `_specs` `noiseLevelDb` (fixture fans, hazers 50 dB,
fog machines 60 dB, 0 = fanless). Free field as in `Candela.Photometry.Acoustics`: spherical spreading
L(d) = L₁ − 20·log₁₀ d (d clamped to 0.1 m) and energetic addition of independent broadband sources,
L = 10·log₁₀ Σ 10^(Lᵢ/10). Optionally one specular **floor reflection**: an image source mirrored below the floor plane
(`FloorHeight`) radiating (1 − α) of the power, α = 0.1 by default (hard stage floors absorb 0.05–0.15 across the
A-weighted band), added energetically (no interference for broadband noise); receivers below the floor hear only the
direct sound. Levels are summed as intensities relative to the loudest source, so any input range is safe and each
source costs a few multiplications per receiver.

`LevelAt` (−∞ = silence), `ComputeGrid` (cell centres, dB(A) clamped below at `MinimumLevel` for the noise fog's
`VolumeGrid`; parallel over slices, deterministic), `SampleSurface` (points) and `SampleRectangle` (u × v samples on a
wall or floor). Tests: the grid matches L₁ − 20·log₁₀ d at every cell and the energetic sum of two sources (and
`Acoustics.SplAt`) to 10⁻³ dB; equal sources add 3.01 dB; a hard floor adds 3.01 dB at floor level; real noise levels
from `_specs` produce the expected field. Limits: no directivity, air absorption, screening by obstacles or room
reverberation (a reverberant room adds a diffuse field that this model omits).

## 4. Tests (`tests/Candela.Simulation.Tests`, ≈ 75 tests, ≈ 45 s in Release, ≈ 75 s in Debug on a busy machine)

* Smoke physics: a closed box with a stirring fan conserves mass over 200 steps (0.00 % with the fixer); the projection
  removes > 99.8 % of the divergence (with obstacles, open faces); warm smoke rises and accelerates; a 1.9 kW plume
  rises at ≈ 0.8 m/s one metre above the source after 3 s (Heskestad's steady value: 1.3 m/s); neutral smoke stays put; a 2 cm wall keeps the far side clean; a fan
  carries smoke along its axis; a hazer raises the average density of a closed room linearly and by exactly its mass
  rate; a fog machine injects its mass rate and throws fog forward; low fog sinks and hugs the floor; ventilation
  carries smoke at the draft speed; open ceilings let hot smoke out; dissipation and cooling follow their rates;
  viscosity/diffusion stay stable; trilinear sampling; voxelisation of rotated and matrix-placed boxes.
* Solver: multigrid and Jacobi agree; odd grid sizes, sealed pockets and tiny grids work; a tolerance near float
  precision in a closed box (singular system) never breaks the conjugate gradient down; strong diffusion conserves
  smoke and heat; mirrored entity matrices give correctly placed obstacles.
* State: identical results for identical inputs and any thread count; `Reset`; snapshots restore runs bit-identically;
  fixed sub-stepping and the hitch cap; invalid settings, emitters and obstacles are reported and skipped; the cell
  budget coarsens big domains.
* Fire: magnitudes for 3/5/8 m flames and a candle; light only while burning; ignition growth over ≈ 0.2 s; colour
  1500–2200 K on the Planckian locus; burn time and re-arming; output scaling; tilted nozzles; volumes and their
  emission coefficient; smoke and heat coupling into `SmokeSimulation`; determinism and snapshots; sanitising; no
  allocations in steady state.
* Acoustics: as listed above, plus determinism over thread counts and bad sources.
* Performance: the 64³ benchmark above (prints the numbers; asserts only against pathological regressions).

## References

* J. Stam, *Stable Fluids*, SIGGRAPH 1999. R. Fedkiw, J. Stam, H. W. Jensen, *Visual Simulation of Smoke*, SIGGRAPH 2001.
* A. Selle, R. Fedkiw, B. Kim, Y. Liu, J. Rossignac, *An Unconditionally Stable MacCormack Method*, J. Sci. Comput. 35, 2008.
* A. McAdams, E. Sifakis, J. Teran, *A Parallel Multigrid Poisson Solver for Fluids Simulation on Large Grids*, SCA 2010.
* A. Priestley, *A Quasi-Conservative Version of the Semi-Lagrangian Advection Scheme*, Mon. Wea. Rev. 121, 1993.
* H. J. Hussein, S. P. Capp, W. K. George, *Velocity measurements in a high-Reynolds-number, momentum-conserving,
  axisymmetric, turbulent jet*, J. Fluid Mech. 258, 1994.
* G. Heskestad, *Fire Plumes, Flame Height, and Air Entrainment*; A. Tewarson, *Generation of Heat and Gaseous, Liquid,
  and Solid Products in Fires* — SFPE Handbook of Fire Protection Engineering.
* B. J. McCaffrey, *Purely Buoyant Diffusion Flames: Some Experimental Results*, NBSIR 79-1910, 1979.
* B. M. Cetegen, T. A. Ahmed, *Experiments on the periodic instability of buoyant plumes and pool fires*, Combust.
  Flame 93, 1993.
* C. F. Bohren, D. R. Huffman, *Absorption and Scattering of Light by Small Particles* (mass scattering of droplets).
* ISO 9613-2 (spherical spreading of sound outdoors), IEC 61672-1 (A-weighting).
