# Fixture spec records — schema 1.0

One JSON record per `.gdtf` file in the repository root:

- `fixtures/<gdtf file name without .gdtf>.json`: one record per fixture
- `fixtures_index.json`: all records in one array (same content)

All keys and values are in English. Units are part of the key name (`W`, `Kg`, `Deg`, `K`, `Cd`, `Lm`, `Db`, `V`, `Hz`, `C`).
A value is `null` when it does not apply or is unknown; `_notApplicable` and `_missing` say which.

## Regenerating

```
python -I _specs/tools/extract_gdtf.py . _specs     # GDTF + overrides -> fixtures/*.json, fixtures_index.json
python -I _specs/tools/validate.py _specs/fixtures_index.json   # schema / vocabulary check, exit 1 on errors
```

Never edit `fixtures/*.json` by hand. Researched values go into `overrides/*.json`:

```json
"Manufacturer@Model": {"powerConsumptionW": 470, "weightKg": 25.4,
                       "_refs": ["https://..."], "_estimated": ["weightKg"], "_notes": "...", "_na": ["noiseLevelDb"]}
```

- the key is the GDTF file name, or just `Manufacturer@Model` (first two `@` parts of the file name);
  `Manufacturer@*` holds brand-wide defaults (always marked `estimated` unless a fixture entry overrides them)
- shorthands: `"mains": "100-240"` (or `"200-240/50"`), `"lux": [lux, metres]` → candela, `"beam": [min, max]`, `"cct": [min, max]`
- `_refs`: manufacturer pages / datasheets the values come from; `_estimated`: fields that are not from a datasheet;
  `_na`: fields that do not apply to the device
- files are merged field by field, shallowest path first, so `overrides/web/<Manufacturer>.json` (values read from the
  manufacturer's own product pages, brochures and manuals) is applied last and wins; a layer with `_refs` clears earlier
  estimates of the fields it sets

## Provenance

`_source[field]` tells where each value came from:

| value | meaning |
|---|---|
| `gdtf` | read directly from a GDTF field |
| `gdtf-derived` | computed from GDTF data (DMX channels, wheels, wiring, geometry) |
| `manual` | from a manufacturer page, datasheet or manual (see `_refs`) |
| `estimated` | approximation: same-platform model, rated current × 230 V, scaling within a series, or a typical value for the device class (listed in `_issues`) |

`_missing` lists required fields still `null`; `_notApplicable` lists fields that do not apply (e.g. noise for a bracket);
`_issues` holds notes about data quality.

## Fields

### Identity
| field | type | notes |
|---|---|---|
| `file`, `fixtureTypeId`, `gdtfName`, `gdtfDataVersion` | string | from the GDTF file |
| `longName` | string | model name without the brand, e.g. `Robin T1 Profile` |
| `shortName` | string | short label (GDTF ShortName) |
| `fullName` | string | brand + model, e.g. `Robe Lighting Robin T1 Profile` |
| `manufacturer` | string | GDTF manufacturer name |
| `description` | string | English description; when the GDTF text was empty, boilerplate or not English it is generated from the fields below and the original is kept in `gdtfDescription` |
| `category` | enum | `PROFILE`, `WASH`, `WASH BEAM`, `BEAM`, `SPOT`, `FOLLOW SPOT`, `LED BAR`, `PAR`, `HAZE`, `FOG`, `FAN`, `SCANER`, `LASER`, `STROBE`, `BLINDER`, `UV`, `BSW`, `GENERATOR FIRE`, `OTHER` (`OTHER` = not a light: brackets, power/data distributors, controllers, mirror-ball motor, snow machine) |
| `subType` | string | optional refinement (`Fresnel`, `Battery uplight`, `Linear graze light`, …) |

### Electrical
| field | type | notes |
|---|---|---|
| `supportedPowerNetworks` | string[] | from `100 V - 50 Hz`, `100 V - 60 Hz`, `110/120 V - 60 Hz`, `110/120 V - 50 Hz`, `220/230 V - 50 Hz`, `220/230 V - 60 Hz`, `240/245 V - 50 Hz` |
| `voltageRangeV`, `frequencyRangeHz` | `{min,max}` | mains input range |
| `powerConsumptionW` | number | maximum mains draw of the device itself (strobes: peak; see `_issues` for static/peak notes). For power-distribution devices (dimmers, PSUs, feeders, distros) this is the device's **own** draw only, without the connected loads |
| `powerSupplyType` | string | input connector, `+ LINK` when the fixture has a power output for daisy-chaining |
| `powerConnectorIn` / `powerConnectorOut` | string / null | connector names: `powerCON`, `powerCON TRUE1`, `powerCON TRUE1 TOP` (IP65 TRUE1-compatible, incl. Seetronic), `Schuko`, `IEC C14`, `IEC C20`, `CEE 16A`, `Fixed Cable`, `DC Jack`, `DC (external PSU)`, `DIN3 (low-voltage DC)`, `XLR4 (DC)`, `XLR5`, `Terminal Block`, `Martin proprietary (P3/PDE)`, `Martin 8-pin`, `IP moulded connector`, `4-pin power/data connector`, `Track adapter`, `E27 lamp socket`, `Battery (internal)`, `PWB-CAB Connector`. `A / B` = alternative inputs, `A + B` = both |
| `powerLinkable` | bool | power output present |
| `maxInputPowerW` | number / null | power-distribution devices: maximum input draw with all outputs fully loaded (own draw + loads + losses) |
| `maxLoadW` | number / null | power-distribution devices: maximum power the device can deliver to connected loads |
| `maxLoadCurrentA`, `maxLoadPerChannelA` | number / null | total and per-output current limit of the outputs |
| `outputChannels` | number / null | number of power outputs / dimmer channels |
| `outputVoltage` | string / null | what the outputs supply, e.g. `230 VAC (dimmed / switched)`, `48 VDC` |

Load calculation for a circuit: Σ `powerConsumptionW` of every device on it, where the loads behind a dimmer/PSU are counted as
their own devices (their `powerConsumptionW`) and must stay within that dimmer/PSU's `maxLoadW` / `maxLoadPerChannelA`;
`maxInputPowerW` is the worst case the dimmer/PSU itself puts on the mains.
| `batteryPowered` | bool | runs from an internal battery |

### Physical
| field | type | notes |
|---|---|---|
| `weightKg` | number | fixture weight without packaging |
| `noiseLevelDb` | number | dB(A) at 1 m; `0` = fanless/passive cooling |
| `ipRating` | string | `IP20`, `IP65`, … |
| `operatingTemperatureC` | `{min,max}` | |

### Movement
| field | type | notes |
|---|---|---|
| `motorizedPanTilt` | bool | pan or tilt driven by a DMX channel |
| `panRangeDeg`, `tiltRangeDeg` | number / null | motorised range; `null` for an axis without a motor |
| `infinitePan`, `infiniteTilt` | bool / null | continuous rotation |
| `manualPanRangeDeg`, `manualTiltRangeDeg` | number / null | manual yoke/bracket adjustment of static fixtures |

### Control
| field | type | notes |
|---|---|---|
| `controlSignal` | string[] | `DMX512`, `RDM`, `Ethernet (Art-Net/sACN)`, `Wireless DMX (CRMX/W-DMX)`, `Ethernet (Martin P3)` |
| `dataConnectors` | string[] | GDTF connector types (`XLR3`, `XLR5`, `RJ45`, …) |
| `dmxModes` | `{name, channels}[]` | |

### Light and optics
| field | type | notes |
|---|---|---|
| `lightSourceType` | enum / null | `LED`, `Discharge Lamp`, `Tungsten Lamp`, `Halogen Lamp`, `Laser` |
| `luminousFluxLm` | number | output flux |
| `maxLuminousIntensityCd` | number | on-axis intensity at full output, narrowest zoom (from lux × d² when published, otherwise estimated from flux and beam angle) |
| `beamAngleDeg` | `{min,max}` | zoom range, or min = max for a fixed lens |
| `fieldAngleDeg` | number | |
| `zoom` | bool | motorised zoom |
| `lensType` | enum | `Spot`, `Wash`, `Fresnel`, `PC (Plano-Convex)`, `Rectangle`, `Ellipsoidal (Profile)`, `Diffused (Glow)`, `Asymmetric (Cyc)` |
| `goboSlots`, `goboWheels` | number, `{wheel, slots, rotating}[]` | |
| `framingShutters`, `framingShutterBlades`, `framingShutterRotation` | bool, number, bool | |
| `framingShutterType` | enum / null | `Framing shutters` (profiles/spots) or `Barn doors` (washes, fresnels) |
| `colorMixing` | string | `CMY`, `RGB`, `RGBW`, `RGBA`, `RGBAL`, `RGBALC`, `Tunable White (CW/WW)`, `None`… Letters: R G B W(white) A(amber) L(lime) C(cyan) I(indigo) V(violet) M Y; `+WW`/`+CW`/`+UV`/`+BG` = warm white, cool white, UV, blue-green emitters |
| `colorWheel` | bool | |
| `colorTemperatureK` | `{min,max}` | white point / CCT range; not applicable to RGB-only and laser sources |
| `prism`, `prisms` | bool, `{name, facets}[]` | |
| `frost`, `frostFilters` | bool, number | |
| `iris`, `focus`, `animationWheel` | bool | |
| `pixelCount` | number | GDTF beam count |
