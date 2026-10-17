# Photometry and colour science (`Candela.Photometry`)

All colour science and light math of the engine: spectra, CIE colorimetry, colour spaces, colour temperature,
additive and subtractive colour, beam intensity distributions, illuminance and the reference light meter, blackbody
emission (fire) and free-field acoustics for the noise overlay. The renderer, DMX evaluation, the lux overlay and the
path tracer build on it, so every model is checked against published reference values in
`tests/Candela.Photometry.Tests` (≈ 200 tests, a few seconds).

## Units and conventions

| Quantity | Unit | Notes |
|---|---|---|
| wavelength | nm | spectra are sampled on regular grids, default 360–830 nm at 5 nm |
| angles | rad | GDTF degrees only at the boundary (`BeamDistribution.FromDegrees`) |
| luminous intensity | cd | `SpotLight.PeakCandela`, `BeamDistribution.PeakIntensityFromFlux` |
| luminous flux | lm | emitters, beams |
| illuminance | lx | `LightMeter`, `Photometrics.Illuminance`; 1 fc = 10.7639 lx |
| luminance | cd/m² | `Photometrics.LuminanceLambertian`, `BlackbodyRadiator.Luminance` |
| temperature | K | CCT 1000–25000 K, blackbody 300 K – 10⁶ K |
| sound pressure level | dB(A) | `Acoustics` |

**Light colour.** A `LightColor` is a chromaticity stored as *linear Rec.2020 RGB normalised to luminance Y = 1*
(0.2627 R + 0.6780 G + 0.0593 B = 1). The magnitude (cd, lm, lx) is kept separately, so the renderer's RGB intensity
of a light is `color.Rgb * candela`. `default(LightColor)` is D65 white.

**Robustness.** Fixture files are messy, so inputs are sanitised instead of rejected: NaN/∞ become documented
defaults, values are clamped to valid ranges, and nothing on a per-frame path throws or returns NaN. Structural
programming errors (a spectrum grid with step ≤ 0, a LUT with fewer than two entries) do throw.

**Performance.** Everything DMX evaluation or overlays call per frame is allocation-free (checked by a test with
`GC.GetAllocatedBytesForCurrentThread`): `BeamDistribution` (struct, ≈ 3 µs to build and integrate),
`ColorMixing.Mix`, `SubtractiveFilterModel.Evaluate` (≈ 0.6 µs, ≈ 1.7 µs with a CTO), `LightColor.From*`,
`Cct.FromXy` (≈ 0.4 µs), `LightMeter`, `Acoustics`.

## Colorimetry

* **Observer.** CIE 1931 2° colour-matching functions x̄, ȳ, z̄, 360–830 nm at 5 nm, embedded verbatim from the
  CIE 015:2004 tables (`CieTables.cs`; e.g. x̄(535) = 0.2257499, ȳ(555) = 1). No analytic fit is used anywhere.
* **Integration.** Tristimulus values use the CIE summation rule X = Σ S(λ)·x̄(λ)·Δλ on the finer of the two grids
  (CIE 015:2004 §7.1). Photometric quantities use K_m = 683.002 lm/W.
* **Illuminants.** D65 tabulated 300–780 nm (CIE 015:2004 Table T.1); A computed from its defining equation
  (ISO 11664-2); daylight D(T) for 4000–25000 K from S0, S1, S2 (Table T.2) with M1, M2 rounded to three decimals;
  the CIE 13.3 reference illuminant (Planckian below 5000 K, daylight above) stands in for white sources of known CCT.
* **Colour spaces.** RGB↔XYZ matrices are derived in double precision from primaries and the D65 white
  (0.3127, 0.3290) (SMPTE RP 177 method) and reproduce the published IEC 61966-2-1 / BT.2020 / BT.2087 matrices to
  ≤ 2×10⁻⁶. sRGB transfer functions follow IEC 61966-2-1 (mirrored for negative values).

## Colour temperature

The Planckian locus is computed by integrating Planck's law (c₂ = h·c/k) against the CMFs and cached on a 0.25 %
logarithmic temperature grid (≈ 1300 nodes, built once in about a millisecond). `Cct.FromXy` implements Ohno's combined
method: nearest node (coarse/fine search), triangular solution for |Δuv| < 0.002, parabolic otherwise. With the fine
table the round-trip error is < 0.2 K and < 3×10⁻⁷ in Δuv over 1000–25000 K, |Δuv| ≤ 0.04. Δuv is positive above
the locus (ANSI C78.377). `Cct.FromXyMcCamy` is a fast approximation for UI readouts. `Cct.XyFromCct(T, Δuv)` offsets
along the exact locus normal.

## Additive mixing (`ColorMixing`, `Emitter`, `StandardEmitters`)

Emitters are mixed in CIE XYZ (Grassmann's law): each emitter contributes level × full-level flux × (x/y, 1, (1−x−y)/y);
the total Y is the flux in lumens and the chromaticity of the sum is the colour. Mixing never happens in clipped RGB.
`TrySolveLevels` finds the RGB levels for a target chromaticity (white balance, virtual CCT).

When a GDTF file has no emitter data, `StandardEmitters.FromColorMixing` builds emitters from the `_specs`
`colorMixing` code (all codes in `_specs/fixtures_index.json` are understood). Typical stage-LED chromaticities:
R (0.700, 0.299), G (0.170, 0.700), B (0.150, 0.030), A (0.575, 0.424), L (0.405, 0.545), C (0.060, 0.520),
I (0.160, 0.016), V (0.170, 0.006), BG (0.075, 0.330), UV (0.172, 0.005); whites on the Planckian locus: W/CW 6500 K,
NW 4000 K, WW 3000 K. R:G:B fluxes are solved so that RGB at full mixes to D65 (0.258 : 0.707 : 0.035); other
emitters relative to that RGB white: W/CW/NW 0.80, WW 0.75, L 0.55, A 0.30, C 0.25, BG 0.15, I 0.03, V 0.01, UV 0.002.

## Subtractive colour (`Filters`, `SubtractiveFilterModel`)

* **CMY flags** are dichroic filters with logistic absorption bands (edge scale 3 nm, 10–90 % width ≈ 13 nm, peak
  absorption 0.995): cyan absorbs above 580 nm, magenta between 490 and 590 nm, yellow below 500 nm. The bands overlap
  by 10 nm so full CMY is practically black (0.8 % under D65; requirement < 2 %). Level scales the absorption depth,
  flags multiply spectrally. Single-flag transmissions under D65: yellow 92 %, cyan 72 %, magenta 27 %.
* **CTO/CTB** conversion filters are ideal: T(λ) = B(λ, T₂)/B(λ, T₁), scaled to a peak of 1 over 380–780 nm. On a
  Planckian source the result lies exactly on the locus at T₂ (6500 → 3200 K: 33 % transmission, +159 mired).
* **xyY filters** (GDTF wheel slots: CIE xy of the transmitted light and Y = transmission in %) are applied per channel
  in linear Rec.2020, exact for a D65 source. Per-channel transmissions above 1 are impossible, so a filter whose
  stated Y exceeds what its chromaticity allows (common: Y = 100 % on saturated slots) is scaled down keeping its
  chromaticity (deep blue at "100 %" becomes ≈ 8 %).
* `SubtractiveFilterModel` precomputes source × CMF products so CMY + CTO evaluate spectrally every frame without
  allocation.

## Beam distribution (`BeamDistribution`)

From the GDTF beam angle (full angle at 50 %) and field angle (full angle at 10 %), with half-angles θ_b and θ_f:

  I(θ)/I₀ = exp(−ln 2·(θ/θ_b)ᵖ),  p = ln(ln 10 / ln 2) / ln(θ_f/θ_b),

which meets both conditions exactly (p = 2 is a Gaussian, field/beam = 1.82). Beyond θ_f a smoothstep window brings
the tail to zero at the cut-off θ_f + k·(θ_f − θ_b) (k = 0.35 Spot/Rectangle, 0.6 Wash, 0.8 PC, 1.0 Fresnel; at least
0.1·θ_f), so lights have finite support for culling and the profile stays C¹. Glow and None are Lambertian
(I = I₀ cos θ); `UniformCone` is a hard-edged cone. Missing or invalid field angles (GDTF files often repeat the beam
angle) are estimated as beam × 1.30 (Spot, Rectangle), 1.82 (Wash), 1.60 (PC), 2.0 (Fresnel); p is kept in [0.5, 64].

Ω_eff = 2π ∫ I_rel(θ) sin θ dθ is integrated with 24-point Gauss–Legendre quadrature per segment (split at θ_b, θ_f
and the cut-off), relative error < 10⁻⁵ against a 200 000-interval Simpson reference; I₀ = Φ/Ω_eff. A uniform cone
gives exactly Φ/(2π(1 − cos α)). Applied to the 840 `_specs` fixtures with flux, beam and field angle, the predicted
peak intensity has a median ratio of 0.92 to the published candela. `BakeLut` produces the 1D texture the GPU uses; a
256-entry LUT reproduces the CPU meter within 1 %.

## Photometric relations and the reference light meter

E = I(θ)·cos α / d² (d clamped to ≥ 1 cm), L = ρ·E/π for Lambertian surfaces, I = E·d² for datasheet "lux at
distance" values. `LightMeter` (and `Photometrics.IlluminanceAt`) sums `SpotLight`s at points, point batches and plane
grids without occlusion; `IlluminanceRgbAt` returns the mixed colour with luminance = lux. It is the ground truth for
the GPU lux overlay.

## Blackbody (fire) and acoustics

`BlackbodyRadiator`: Planck spectral radiance, M = σT⁴, luminous exitance and luminance, efficacy (54.7 lm/W at
4000 K, 94.9 lm/W at 7000 K, maximum 95.4 lm/W near 6600 K; 0.08 lm/W at 1500 K), and colour at any temperature (not
limited to the CCT table). The luminance at the platinum point (2041.4 K) is 5.99×10⁵ cd/m², the historical candela.

`Acoustics`: free-field point sources, L(d) = L₁ₘ − 20·log₁₀ d (d ≥ 0.1 m), energetic sum
L = 10·log₁₀ Σ 10^(Lᵢ/10) (two 60 dB sources → 63.01 dB).

## Validation (selected)

| Check | Reference | Result |
|---|---|---|
| D65 table → xy | 0.3127, 0.3290 (CIE 015 T.3: 0.31272, 0.32903) | 0.31271, 0.32901 |
| Illuminant A → xy | 0.44757, 0.40745 | 0.44757, 0.40744 |
| D50 / D75 from S0–S2 | 0.34567, 0.35850 / 0.29902, 0.31485 | within 2×10⁻⁵ |
| Planck 3200 K | 0.4234, 0.3990 | 0.42336, 0.39895 |
| CCT of D65 xy | 6504 K, Δuv +0.0032 | 6504.2 K, +0.0032 |
| CCT of Planck 2700 K | 2700 K | ±0.02 K |
| RGB LEDs mixed to D65 | Δxy < 0.003 | < 10⁻⁴ |
| full CMY | < 2 % | 0.8 % |
| beam 50 % / 10 % points | at beam/2, field/2 | exact (10⁻⁵) |

## Known approximations

* Colorimetry uses the 5 nm CIE 1931 table with linear interpolation for finer spectra; the 2° observer is used for all
  field sizes.
* Spectra are point-sampled (with linear interpolation) when resampled to a coarser grid.
* Standard LED chromaticities and flux shares are typical values, used only when a GDTF has no emitter data.
* CMY and CTO filters are idealised models, not measurements of a particular fixture; xyY filters without a spectrum
  are applied per channel in Rec.2020, which is exact only for a D65 source (a deep blue filter leaks ≈ 3 % of a
  saturated red light).
* Beam distributions are rotationally symmetric; the rectangular shape of `Rectangle` beams, gobos, shutters and prism
  splits are applied by the renderer's cookie. Zoom keeps the flux, not the peak.
* The light meter has no occlusion and no inter-reflection; sound has no reflection, air absorption or directivity.

## References

* CIE 015:2004 *Colorimetry*, 3rd ed. — CMFs (Table T.4), D65 (T.1), daylight components (T.2), white points (T.3),
  daylight locus and M1/M2 (§3.1), summation (§7.1), CCT validity |Δuv| ≤ 0.05 (§9.5).
* ISO/CIE 11664-1:2019 (CIE 1931 observer); ISO 11664-2:2007 / CIE S 014-2 (standard illuminants A and D65).
* CIE 13.3-1995 *Method of measuring and specifying colour rendering properties of light sources* (reference illuminant).
* Y. Ohno, "Practical Use and Calculation of CCT and Duv", *LEUKOS* 10(1):47–55, 2014.
* C. S. McCamy, "Correlated color temperature as an explicit function of chromaticity coordinates",
  *Color Res. Appl.* 17(2):142–144, 1992.
* ANSI C78.377-2017 (sign of Δuv); ITU-R BT.709-6, BT.2020-2, BT.2087-0; IEC 61966-2-1:1999 (sRGB);
  SMPTE RP 177-1993 (derivation of RGB↔XYZ matrices).
* BIPM, *The International System of Units*, 9th ed., 2019 (K_cd = 683 lm/W at 540 THz; exact h, c, k);
  13th CGPM (1967), Resolution 5 (platinum-point candela).
* G. Wyszecki, W. S. Stiles, *Color Science*, 2nd ed., Wiley 1982 (Grassmann's laws, Planckian radiators).
* DIN SPEC 15800:2022 (GDTF 1.2) — beam types, beam/field angle definitions, emitter and wheel-slot colours.
* ISO 9613-2:1996 — geometric divergence of a point source in a free field.
