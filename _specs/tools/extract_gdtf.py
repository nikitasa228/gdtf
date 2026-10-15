"""
Extract a normalized, English-keyed spec record from every .gdtf file.

Usage:  python -I extract_gdtf.py <fixtures_dir> <out_dir>

Each output record holds one value per field plus `_source[field]`:
  "gdtf"          value read directly from a GDTF field
  "gdtf-derived"  computed from GDTF data (DMX channels, wheels, wiring...)
  "estimated"     calculated approximation (e.g. candela from lumen + angle)
  "manual"        filled later from manufacturer spec sheets (overrides.json)
Fields that are null are listed in `_missing`.
"""
import json, math, os, re, sys, zipfile
import xml.etree.ElementTree as ET

SCHEMA_VERSION = "1.0"

CATEGORIES = ["PROFILE", "WASH", "WASH BEAM", "BEAM", "SPOT", "FOLLOW SPOT", "LED BAR", "PAR",
              "HAZE", "FOG", "FAN", "SCANER", "LASER", "STROBE", "BLINDER", "UV", "BSW",
              "GENERATOR FIRE", "OTHER"]  # OTHER: devices outside the list (mirror-ball motor, snow machine...)
LIGHT_SOURCES = ["LED", "Discharge Lamp", "Tungsten Lamp", "Halogen Lamp", "Laser", None]

# Standard mains networks. A fixture supports a network when its voltage/frequency range covers it.
POWER_NETWORKS = [
    ("100 V - 50 Hz", 100, 50), ("100 V - 60 Hz", 100, 60),
    ("110/120 V - 60 Hz", 120, 60), ("110/120 V - 50 Hz", 110, 50),
    ("220/230 V - 50 Hz", 230, 50), ("220/230 V - 60 Hz", 220, 60),
    ("240/245 V - 50 Hz", 240, 50),
]

# GDTF connector type -> canonical power connector name
POWER_CONN = [
    (r"true1|powercon\s*true", "powerCON TRUE1"),
    (r"nac3fca|nac3mpa|powercon\s*blue|^powercon$|powercon in", "powerCON"),
    (r"nac3fcb|nac3mpb|powercon\s*(grey|gray|white)", "powerCON"),
    (r"cee\s*7|schuko", "Schuko"),
    (r"iec|c13|c14", "IEC C14"),
    (r"c19|c20", "IEC C20"),
    (r"\bdc\b|dc power|barrel", "DC Jack"),
    (r"xlr4", "XLR4 (DC)"),
    (r"loose end|fixed", "Fixed Cable"),
    (r"martin pd|martin pde|martin dce", "Martin proprietary (P3/PDE)"),
    (r"tblk|terminal", "Terminal Block"),
    (r"din3", "DIN3 (low-voltage DC)"),
    (r"looseend", "Fixed Cable"),
    (r"power ?con", "powerCON"),
    (r"cee\s*16|cee16", "CEE 16A"),
]


def clean_text(s):
    """GDTF exporters often wrote non-ASCII glyphs (°, ™, ®) as U+FFFD. Repair what is recoverable."""
    if s is None:
        return None
    s = re.sub(r"(\d)\s?�", r"\1°", s)      # "10� beam" -> "10° beam"
    s = s.replace("�", "")                   # "AT�" (™/®) -> "AT"
    s = re.sub(r"[ \t]+", " ", s).replace("\r", "")
    return s.strip()


def fnum(v):
    try:
        x = float(v)
        return x if math.isfinite(x) else None
    except (TypeError, ValueError):
        return None


def rnd(x, n=1):
    return None if x is None else round(x, n)


def attr_base(name):
    return re.sub(r"\d+", "n", name or "")


def classify_power_conn(type_str, name_str=""):
    s = f"{type_str or ''} {name_str or ''}".lower()
    for pat, canon in POWER_CONN:
        if re.search(pat, s):
            return canon
    return clean_text(type_str) or clean_text(name_str) or "Unknown"


def color_mixing(attrs):
    sub = {a for a in attrs if a.startswith("ColorSub_")}
    if {"ColorSub_C", "ColorSub_M", "ColorSub_Y"} <= sub:
        return "CMY"
    add = {a[len("ColorAdd_"):] for a in attrs if a.startswith("ColorAdd_")}
    add |= {"R" for a in attrs if a == "ColorRGB_Red"} | {"G" for a in attrs if a == "ColorRGB_Green"} \
        | {"B" for a in attrs if a == "ColorRGB_Blue"}
    if not add:
        return "None"
    # single-letter emitters first (concatenated), multi-letter ones after, joined with "+"
    order = [("R", "R"), ("G", "G"), ("B", "B"), ("W", "W"), ("RY", "A"), ("GY", "L"), ("C", "C"),
             ("M", "M"), ("Y", "Y"), ("BC", "I"), ("BM", "V"), ("WW", "WW"), ("CW", "CW"), ("GC", "BG"),
             ("UV", "UV")]
    parts = [lbl for k, lbl in order if k in add]
    if set(parts) <= {"W", "WW", "CW", "A"} and {"WW", "CW"} <= set(parts):
        return "Tunable White (CW/WW)"
    if set(parts) <= {"W", "WW", "CW", "A", "UV"}:
        return "None"
    out = ""
    for p in parts:
        out += p if len(p) == 1 else "+" + p
    return out.lstrip("+")


def pick_category(name, desc, attrs, has_pan, has_tilt, blades, gobo_wheels, zoom_rng, beam_angle,
                  lamp, pixel_count, ip_words):
    n = f" {name} ".lower()
    t = f"{name} {desc}".lower()
    a = set(attrs)
    if re.search(r"\bfire|flame", n): return "GENERATOR FIRE"
    if re.search(r"\bhaz(e|er)\b|haze", n): return "HAZE"
    if re.search(r"\bfog|smoke|\bmist|low fog|geyser|co2", n) or "Fog1" in a or "Fog" in a: return "FOG"
    if re.search(r"\bjet\b", n) and not lamp: return "FOG"
    if re.search(r"\bfan\b|wind machine", n): return "FAN"
    if re.search(r"laser", n): return "LASER"
    if re.search(r"follow ?spot|followspot", n): return "FOLLOW SPOT"
    if re.search(r"blinder|\bmolefay|\bmole\b|\b(2|4|8)[ -]?lite", n): return "BLINDER"
    if re.search(r"\buv\b|black ?light|blacklight", n) and not color_mixing(attrs): return "UV"
    if re.search(r"\bscan(ner)?\b|scanner", n): return "SCANER"
    if has_pan and has_tilt:
        if re.search(r"bswf?|hybrid|\b3[ -]?in[ -]?1\b", n): return "BSW"
        if re.search(r"\bbracket\b|clamp|mount", n) and not lamp: return "OTHER"
        if re.search(r"wash ?beam|beam ?wash|b-eye|beye|\bbee\b", n): return "WASH BEAM"
        if re.search(r"\bbeam\b|beam", n) and not re.search(r"wash", n): return "BEAM"
        if blades and gobo_wheels: return "PROFILE"
        if re.search(r"profile", n): return "PROFILE"
        if re.search(r"\bspot\b|spot", n) or gobo_wheels: return "SPOT"
        if re.search(r"\bbar\b|batten|pixel ?bar", n): return "LED BAR"
        if zoom_rng and zoom_rng[0] is not None and zoom_rng[0] <= 5: return "WASH BEAM"
        return "WASH"
    if re.search(r"\bbracket\b|clamp|mount", n) and not lamp: return "OTHER"
    if re.search(r"strobe|\bflash\b|atomic|lightning", n): return "STROBE"
    if re.search(r"wash|fresnel|\bmanet\b", n) and not re.search(r"profile|ellipsoidal", n): return "WASH"
    if re.search(r"ellipsoidal|profile|leko|source four", n) or blades: return "PROFILE"
    if re.search(r"\bpar\b|par[ -]?\d|\bpar$|\bpar\d|propar", n): return "PAR"
    if re.search(r"\bbar\b|batten|\bline\b|tube|strip|dotline|dotchain|pixel ?line|linear|bar\d|bar\b", n): return "LED BAR"
    if re.search(r"\bspot\b", n) or gobo_wheels: return "SPOT"
    if re.search(r"\bbeam\b", n): return "BEAM"
    return "WASH"


def extract(path):
    with zipfile.ZipFile(path) as z:
        raw = z.read("description.xml")
        files_in_zip = z.namelist()
    root = ET.fromstring(raw)
    ft = root.find("FixtureType")
    src = {}
    issues = []

    def put(rec, key, val, source):
        rec[key] = val
        if val is not None and val != [] and val != {}:
            src[key] = source

    name = clean_text(ft.get("Name"))
    long_name = clean_text(ft.get("LongName")) or None
    short_name = clean_text(ft.get("ShortName")) or None
    desc = clean_text(ft.get("Description")) or None
    manu = clean_text(ft.get("Manufacturer"))
    for k in ("Name", "LongName", "ShortName", "Description", "Manufacturer"):
        if "�" in (ft.get(k) or ""):
            issues.append(f"{k} contained broken characters (U+FFFD), repaired")

    # ---- attributes / channel functions ---------------------------------------------------------
    attrs = [a.get("Name") for a in ft.iter("Attribute")]
    used_attrs = set()
    chf_by_attr = {}
    for cf in ft.iter("ChannelFunction"):
        at = cf.get("Attribute") or ""
        at = at.split(".")[-1]
        if at and at != "NoFeature":
            used_attrs.add(at)
            chf_by_attr.setdefault(at, []).append(cf)
    all_attrs = used_attrs or set(attrs)
    # attributes reachable through an addressed DMX channel; channels with an empty Offset are virtual
    # (e.g. Prolights files use them for manual yoke/bracket positioning in visualisers)
    dmx_attrs = set()
    for ch in ft.iter("DMXChannel"):
        o = (ch.get("Offset") or "").strip()
        if o and o.lower() != "none":
            for cf in ch.iter("ChannelFunction"):
                dmx_attrs.add((cf.get("Attribute") or "").split(".")[-1])

    def phys_range(attr):
        vals = []
        for cf in chf_by_attr.get(attr, []):
            pf, pt = fnum(cf.get("PhysicalFrom")), fnum(cf.get("PhysicalTo"))
            if pf is not None and pt is not None and not (pf == 0 and pt == 1) and not (pf == pt == 0):
                vals.append((pf, pt))
        return vals

    # ---- DMX modes ----------------------------------------------------------------------------
    modes = []
    for m in ft.iter("DMXMode"):
        offs = set()
        for ch in m.iter("DMXChannel"):
            o = ch.get("Offset")
            if o and o.lower() != "none":
                for p in o.split(","):
                    if p.strip().isdigit():
                        offs.add((ch.get("DMXBreak") or "1", int(p)))
        fp = {}
        for brk, o in offs:
            fp[brk] = max(fp.get(brk, 0), o)
        modes.append({"name": clean_text(m.get("Name")), "channels": sum(fp.values()) if fp else 0})

    # ---- geometry / beams -----------------------------------------------------------------------
    beams = list(ft.iter("Beam"))
    lamp_types = [b.get("LampType") for b in beams if b.get("LampType")]
    beam_power = sum(fnum(b.get("PowerConsumption")) or 0 for b in beams)
    beam_flux = sum(fnum(b.get("LuminousFlux")) or 0 for b in beams)
    main_beam = beams[0] if beams else None
    pixel_count = len(beams)

    # ---- physical properties ----------------------------------------------------------------------
    props = ft.find("PhysicalDescriptions/Properties")
    weight = op_lo = op_hi = None
    pc_value = None
    volt_lo = volt_hi = f_lo = f_hi = None
    if props is not None:
        w = props.find("Weight")
        if w is not None:
            weight = fnum(w.get("Value"))
            if weight == 0:
                weight = None
                issues.append("Weight is 0 in GDTF")
        ot = props.find("OperatingTemperature")
        if ot is not None:
            op_lo, op_hi = fnum(ot.get("Low")), fnum(ot.get("High"))
        for p in props.iter("PowerConsumption"):
            pc_value = (pc_value or 0) + (fnum(p.get("Value")) or 0) or pc_value
            volt_lo, volt_hi = fnum(p.get("VoltageLow")), fnum(p.get("VoltageHigh"))
            f_lo, f_hi = fnum(p.get("FrequencyLow")), fnum(p.get("FrequencyHigh"))

    # ---- connectors (GDTF 1.0/1.1 <Connector>, 1.2 <WiringObject>) -------------------------------
    power_in, power_out, data_conns = [], [], set()
    wiring_payload = 0
    for wo in ft.iter("WiringObject"):
        ct, nm, comp = wo.get("ConnectorType"), wo.get("Name") or "", wo.get("ComponentType") or ""
        sig = (wo.get("SignalType") or "").lower()
        lname = nm.lower()
        is_power = comp in ("Consumer", "PowerSource") or "power" in sig or "power" in lname or "mains" in lname
        if "dmx" in sig or "dmx" in lname:
            is_power = "power" in sig and "dmx" not in sig
        if is_power and comp not in ("NetworkInput", "NetworkOutput", "NetworkInOut"):
            canon = classify_power_conn(ct, nm)
            if comp in ("PowerSource", "Output") or re.search(r"\bout|link|thru", lname):
                power_out.append(canon)
            else:
                power_in.append(canon)
                wiring_payload += fnum(wo.get("ElectricalPayLoad")) or 0
                vmin, vmax = fnum(wo.get("VoltageRangeMin")), fnum(wo.get("VoltageRangeMax"))
                if vmin and vmax and volt_lo is None:
                    volt_lo, volt_hi = vmin, vmax
                fmin, fmax = fnum(wo.get("FrequencyRangeMin")), fnum(wo.get("FrequencyRangeMax"))
                if fmin and fmax and f_lo is None:
                    f_lo, f_hi = fmin, fmax
        else:
            data_conns.add((ct or "", sig, lname))
    for c in ft.iter("Connector"):
        ct, nm = c.get("Type") or "", c.get("Name") or ""
        lname = nm.lower()
        if re.search(r"power|mains|^ac|netz", lname) or ct.lower().startswith(("powercon", "nac3", "cee", "iec")):
            canon = classify_power_conn(ct, nm)
            (power_out if re.search(r"\bout|link|thru", lname) or ct.upper() == "NAC3FCB" else power_in).append(canon)
        else:
            data_conns.add((ct, "", lname))

    # ---- control signal ---------------------------------------------------------------------------
    signal = []
    if modes:
        signal.append("DMX512")
    if ft.find("Protocols/FTRDM") is not None or re.search(r"\brdm\b", (desc or "").lower()):
        signal.append("RDM")
    dc_text = " ".join(f"{a} {b} {c}" for a, b, c in data_conns).lower()
    if re.search(r"rj45|ethercon|ethernet|artnet|art-net|sacn|network", dc_text) or \
            re.search(r"art-?net|sacn|ethernet", (desc or "").lower()):
        signal.append("Ethernet (Art-Net/sACN)")
    if re.search(r"crmx|wireless dmx|w-dmx|wdmx|lumenradio", f"{name} {desc}".lower()):
        signal.append("Wireless DMX (CRMX/W-DMX)")
    dmx_conn = sorted({ct for ct, s, n in data_conns if re.search(r"xlr|ethercon|rj45", ct.lower())})

    # ---- movement -----------------------------------------------------------------------------
    def span(attr):
        r = phys_range(attr)
        if not r:
            return None
        return max(abs(b - a) for a, b in r)
    pan = span("Pan")
    tilt = span("Tilt")
    has_pan = "Pan" in all_attrs or "Pan" in attrs
    has_tilt = "Tilt" in all_attrs or "Tilt" in attrs
    motor_pan = "Pan" in dmx_attrs
    motor_tilt = "Tilt" in dmx_attrs
    infinite_pan = any(a in all_attrs for a in ("PanRotate", "PanMode")) and "PanRotate" in all_attrs
    infinite_tilt = "TiltRotate" in all_attrs

    # ---- beam / optics ----------------------------------------------------------------------------
    zoom = "Zoom" in all_attrs or any(a.startswith("Zoom") and a not in ("ZoomReset", "ZoomMSpeed") for a in all_attrs)
    zr = phys_range("Zoom")
    zoom_min = zoom_max = None
    if zr:
        vals = [v for pair in zr for v in pair if v and 0 < v < 180]
        if vals:
            zoom_min, zoom_max = min(vals), max(vals)
    beam_angle = fnum(main_beam.get("BeamAngle")) if main_beam is not None else None
    field_angle = fnum(main_beam.get("FieldAngle")) if main_beam is not None else None
    beam_angle_is_default = beam_angle in (25.0,) and field_angle in (25.0,)
    if beam_angle_is_default:
        issues.append("Beam angle equals the GDTF default (25°) - probably not filled in")

    gobo_wheels, prism_info, color_wheel = [], [], False
    wheels = {w.get("Name"): w for w in ft.iter("Wheel")}
    wheel_users = {}
    for cf in ft.iter("ChannelFunction"):
        if cf.get("Wheel"):
            at = (cf.get("Attribute") or "").split(".")[-1]
            wheel_users.setdefault(cf.get("Wheel"), set()).add(at)
    for wn, w in wheels.items():
        users = wheel_users.get(wn, set())
        slots = [s for s in w.findall("Slot") if (s.get("Name") or "").strip().lower() not in ("open", "empty", "")]
        if any(re.match(r"Gobo\d", u) for u in users):
            gnum = next(re.match(r"Gobo(\d)", u).group(1) for u in sorted(users) if re.match(r"Gobo\d", u))
            rot = any(a.startswith(f"Gobo{gnum}Pos") for a in all_attrs)
            gobo_wheels.append({"wheel": clean_text(wn), "slots": len(slots), "rotating": rot})
        elif any(u.startswith("Prism") for u in users):
            for s in slots:
                facets = len(s.findall("PrismFacet"))
                prism_info.append({"name": clean_text(s.get("Name")), "facets": facets or None})
        elif any(u.startswith("Color") and "Wheel" not in u for u in users):
            color_wheel = True
    if not gobo_wheels and any(re.match(r"Gobo\dn?$|Gobo\d$", a) for a in all_attrs):
        gobo_wheels.append({"wheel": "Gobo", "slots": None, "rotating": any("Pos" in a and a.startswith("Gobo") for a in all_attrs)})
    prisms = sorted({a for a in all_attrs if re.match(r"Prism\d$", a)})
    if prisms and not prism_info:
        prism_info = [{"name": p, "facets": None} for p in prisms]
    blades = sorted({a for a in all_attrs if re.match(r"Blade\d(A|B)$", a)})
    blade_rot = any(re.match(r"Blade\dRot|ShaperRot", a) for a in all_attrs)
    frost = sorted({a for a in all_attrs if re.match(r"Frost\d$", a)})
    iris = "Iris" in all_attrs
    focus = any(re.match(r"Focus\d$", a) for a in all_attrs)
    animation = any(a.startswith("AnimationWheel") or a.startswith("AnimationSystem") for a in all_attrs)

    # ---- color temperature -------------------------------------------------------------------------
    cts = []
    for at in ("CTC", "CTO", "CTB", "ExtendedCTO"):
        for a, b in phys_range(at):
            for v in (a, b):
                if 1000 <= v <= 20000:
                    cts.append(v)
    for w in wheels.values():
        for s in w.findall("Slot"):
            m = re.search(r"(\d{4,5})\s*K\b", s.get("Name") or "")
            if m and 1000 <= int(m.group(1)) <= 20000:
                cts.append(float(m.group(1)))
    for m in re.finditer(r"(\d{1,2}[.,]?\d{3})\s*K\b", desc or ""):
        v = float(m.group(1).replace(",", "").replace(".", ""))
        if 1000 <= v <= 20000:
            cts.append(v)
    beam_ct = fnum(main_beam.get("ColorTemperature")) if main_beam is not None else None

    ip = None
    m = re.search(r"\bIP\s?-?(\d{2})\b", f"{name} {long_name or ''} {desc or ''}", re.I)
    if m:
        ip = f"IP{m.group(1)}"

    # ================== build record =============================================================
    rec = {}
    put(rec, "longName", long_name or name, "gdtf" if long_name else "gdtf-derived")
    put(rec, "shortName", short_name, "gdtf")
    put(rec, "description", desc, "gdtf")
    put(rec, "manufacturer", manu, "gdtf")
    cat = pick_category(f"{name} {long_name or ''}", desc or "", all_attrs, motor_pan, motor_tilt, blades,
                        gobo_wheels, (zoom_min, zoom_max) if zoom_min else None, beam_angle,
                        lamp_types, pixel_count, None)
    put(rec, "category", cat, "gdtf-derived")

    nets = None
    if volt_lo and volt_hi and f_lo and f_hi:
        nets = [lbl for lbl, v, f in POWER_NETWORKS if volt_lo <= v <= volt_hi and f_lo <= f <= f_hi]
    put(rec, "supportedPowerNetworks", nets or None, "gdtf-derived")
    put(rec, "voltageRangeV", {"min": volt_lo, "max": volt_hi} if volt_lo and volt_hi else None, "gdtf")
    put(rec, "frequencyRangeHz", {"min": f_lo, "max": f_hi} if f_lo and f_hi else None, "gdtf")

    power = None
    power_src = None
    if pc_value:
        power, power_src = pc_value, "gdtf"
    elif wiring_payload:
        power, power_src = wiring_payload, "gdtf"
    elif beam_power and not (len(beams) and all(fnum(b.get("PowerConsumption")) in (1000.0,) for b in beams)) \
            and beam_power >= 5:
        power, power_src = beam_power, "gdtf-derived"
        issues.append("Power consumption taken from beam geometry sum (unverified)")
    put(rec, "powerConsumptionW", rnd(power, 0), power_src)

    cd = None
    ang = zoom_min or beam_angle
    if beam_flux and ang and not beam_angle_is_default and beam_flux not in (10000.0,) and 0.5 < ang < 120:
        # on-axis intensity of a beam whose half-intensity cone is `ang`: I ≈ Φ / (2π(1-cos(θ/2))) * ~0.7 peak factor
        omega = 2 * math.pi * (1 - math.cos(math.radians(ang) / 2))
        cd = beam_flux / omega * 0.7
    put(rec, "maxLuminousIntensityCd", rnd(cd, -2) if cd else None, "estimated")
    put(rec, "luminousFluxLm", rnd(beam_flux, 0) if beam_flux and beam_flux != 10000.0 * max(1, len(beams)) else None, "gdtf")
    put(rec, "noiseLevelDb", None, None)
    put(rec, "weightKg", rnd(weight, 2), "gdtf")

    pin = sorted(set(power_in))
    pout = sorted(set(power_out))
    put(rec, "powerConnectorIn", pin[0] if len(pin) == 1 else (" / ".join(pin) if pin else None), "gdtf-derived")
    put(rec, "powerConnectorOut", pout[0] if len(pout) == 1 else (" / ".join(pout) if pout else None), "gdtf-derived")
    pst = None
    if pin:
        pst = pin[0] + (" LINK" if pout else "")
    put(rec, "powerSupplyType", pst, "gdtf-derived")

    # pan/tilt ranges describe the motorised movement; a manual yoke/bracket range goes to manual*RangeDeg
    put(rec, "motorizedPanTilt", motor_pan or motor_tilt, "gdtf-derived")
    put(rec, "panRangeDeg", rnd(pan) if motor_pan else None, "gdtf-derived")
    put(rec, "tiltRangeDeg", rnd(tilt) if motor_tilt else None, "gdtf-derived")
    put(rec, "infinitePan", infinite_pan if motor_pan else None, "gdtf-derived")
    put(rec, "infiniteTilt", infinite_tilt if motor_tilt else None, "gdtf-derived")
    put(rec, "manualPanRangeDeg", rnd(pan) if has_pan and not motor_pan and pan else None, "gdtf-derived")
    put(rec, "manualTiltRangeDeg", rnd(tilt) if has_tilt and not motor_tilt and tilt else None, "gdtf-derived")
    put(rec, "controlSignal", signal or None, "gdtf-derived")
    put(rec, "dataConnectors", dmx_conn or None, "gdtf-derived")

    lamp = None
    if lamp_types:
        lamp = max(set(lamp_types), key=lamp_types.count)
        lamp = {"LED": "LED", "Discharge": "Discharge Lamp", "Tungsten": "Tungsten Lamp",
                "Halogen": "Halogen Lamp"}.get(lamp, lamp)
    put(rec, "lightSourceType", lamp, "gdtf")

    if zoom_min:
        put(rec, "beamAngleDeg", {"min": rnd(zoom_min), "max": rnd(zoom_max)}, "gdtf-derived")
    elif beam_angle and not beam_angle_is_default:
        put(rec, "beamAngleDeg", {"min": rnd(beam_angle), "max": rnd(beam_angle)}, "gdtf")
    else:
        put(rec, "beamAngleDeg", None, None)
    put(rec, "fieldAngleDeg", rnd(field_angle) if field_angle and not beam_angle_is_default else None, "gdtf")
    put(rec, "zoom", zoom, "gdtf-derived")

    bt = main_beam.get("BeamType") if main_beam is not None else None
    lens = {"Spot": "Spot", "Wash": "Wash", "Fresnel": "Fresnel", "PC": "PC (Plano-Convex)",
            "Rectangle": "Rectangle", "Glow": "Diffused (Glow)"}.get(bt)
    if blades and not has_pan:
        lens = "Ellipsoidal (Profile)"
    put(rec, "lensType", lens, "gdtf-derived")

    put(rec, "goboWheels", gobo_wheels or [], "gdtf-derived")
    put(rec, "goboSlots", sum(g["slots"] or 0 for g in gobo_wheels) if gobo_wheels else 0, "gdtf-derived")
    put(rec, "framingShutters", bool(blades), "gdtf-derived")
    put(rec, "framingShutterBlades", len({b[:6] for b in blades}) if blades else 0, "gdtf-derived")
    put(rec, "framingShutterRotation", blade_rot if blades else False, "gdtf-derived")
    put(rec, "framingShutterType", None, None)  # resolved in main() once the final category is known
    put(rec, "colorMixing", color_mixing(all_attrs), "gdtf-derived")
    put(rec, "colorWheel", color_wheel, "gdtf-derived")
    if cts:
        put(rec, "colorTemperatureK", {"min": min(cts), "max": max(cts)}, "gdtf-derived")
    elif beam_ct and beam_ct not in (6000.0,) and lamp in ("Discharge Lamp", "Tungsten Lamp", "Halogen Lamp", "LED"):
        put(rec, "colorTemperatureK", {"min": beam_ct, "max": beam_ct}, "gdtf")
    else:
        put(rec, "colorTemperatureK", None, None)
    put(rec, "prism", bool(prism_info), "gdtf-derived")
    put(rec, "prisms", prism_info, "gdtf-derived")
    put(rec, "frost", bool(frost), "gdtf-derived")
    put(rec, "frostFilters", len(frost), "gdtf-derived")
    put(rec, "iris", iris, "gdtf-derived")
    put(rec, "focus", focus, "gdtf-derived")
    put(rec, "animationWheel", animation, "gdtf-derived")
    put(rec, "ipRating", ip, "gdtf-derived")
    put(rec, "operatingTemperatureC", {"min": op_lo, "max": op_hi} if op_hi else None, "gdtf")
    put(rec, "dmxModes", modes, "gdtf")
    put(rec, "pixelCount", pixel_count, "gdtf-derived")

    meta = {
        "file": os.path.basename(path),
        "fixtureTypeId": ft.get("FixtureTypeID"),
        "gdtfDataVersion": root.get("DataVersion"),
        "gdtfName": name,
    }
    return meta, rec, src, issues


# Fields that must be filled for the simulator; booleans/lists with a GDTF-derived default don't count as missing
REQUIRED = ["longName", "shortName", "description", "manufacturer", "category", "supportedPowerNetworks",
            "powerConsumptionW", "maxLuminousIntensityCd", "noiseLevelDb", "weightKg", "powerSupplyType",
            "panRangeDeg", "tiltRangeDeg", "controlSignal", "lightSourceType", "beamAngleDeg", "zoom",
            "lensType", "goboSlots", "framingShutters", "colorMixing", "colorTemperatureK", "prism", "frost"]
MOVING_ONLY = {"panRangeDeg", "tiltRangeDeg"}
NOT_FOR_ATMOS = {"maxLuminousIntensityCd", "beamAngleDeg", "lensType", "colorTemperatureK", "colorMixing",
                 "lightSourceType", "zoom", "goboSlots", "framingShutters", "prism", "frost"}


def missing_fields(rec, na=()):
    out = []
    moving = bool(rec.get("motorizedPanTilt"))
    atmos = rec.get("category") in ("HAZE", "FOG", "FAN", "OTHER") and rec.get("lightSourceType") is None
    for k in REQUIRED:
        if k in MOVING_ONLY and not moving:
            continue
        if atmos and k in NOT_FOR_ATMOS:
            continue
        if k == "colorMixing" and rec.get("category") in ("PROFILE", "SPOT", "BEAM", "STROBE", "BLINDER", "UV", "FOLLOW SPOT", "LASER"):
            continue  # many of these legitimately have no mixing (white source + wheel)
        if k == "colorTemperatureK" and rec.get("colorMixing") and rec.get("colorMixing") != "CMY":
            pass
        if k in na:
            continue
        if rec.get(k) is None:
            out.append(k)
    return out


def expand_override(ov):
    """Shorthands used in overrides/*.json:
       "mains": "100-240"            -> voltageRangeV, frequencyRangeHz (50/60), supportedPowerNetworks
       "mains": "200-240/50"         -> same, single frequency
       "lux": [84000, 5]             -> maxLuminousIntensityCd = lux * d^2
       "beam": [5, 50] / "beam": 2   -> beamAngleDeg {min,max}
       "cct": [2700, 6500]           -> colorTemperatureK {min,max}
    """
    ov = dict(ov)
    m = ov.pop("mains", None)
    if m:
        v, _, f = m.partition("/")
        lo, _, hi = v.partition("-")
        lo, hi = float(lo), float(hi or lo)
        fl, fh = (float(f), float(f)) if f else (50.0, 60.0)
        ov.setdefault("voltageRangeV", {"min": lo, "max": hi})
        ov.setdefault("frequencyRangeHz", {"min": fl, "max": fh})
        ov.setdefault("supportedPowerNetworks",
                      [lbl for lbl, vv, ff in POWER_NETWORKS if lo <= vv <= hi and fl <= ff <= fh] or None)
    lx = ov.pop("lux", None)
    if lx:
        ov.setdefault("maxLuminousIntensityCd", round(lx[0] * lx[1] ** 2, -2))
    b = ov.pop("beam", None)
    if b is not None:
        b = b if isinstance(b, list) else [b, b]
        ov.setdefault("beamAngleDeg", {"min": float(b[0]), "max": float(b[-1])})
    pst = ov.get("powerSupplyType")
    if pst and "powerConnectorIn" not in ov:
        base = pst[:-5] if pst.endswith(" LINK") else pst
        ov["powerConnectorIn"] = base
        ov.setdefault("powerConnectorOut", base if pst.endswith(" LINK") else None)
    c = ov.pop("cct", None)
    if c is not None:
        c = c if isinstance(c, list) else [c, c]
        ov.setdefault("colorTemperatureK", {"min": float(c[0]), "max": float(c[-1])})
    return ov


CAT_WORDS = {"PROFILE": "profile", "WASH": "wash", "WASH BEAM": "wash-beam", "BEAM": "beam", "SPOT": "spot",
             "FOLLOW SPOT": "follow spot", "LED BAR": "LED bar", "PAR": "PAR", "HAZE": "haze machine", "FOG": "fog machine",
             "FAN": "effect fan", "SCANER": "scanner", "LASER": "laser", "STROBE": "strobe", "BLINDER": "blinder",
             "UV": "UV light", "BSW": "beam/spot/wash hybrid", "GENERATOR FIRE": "flame effect", "OTHER": "device"}


def auto_description(rec):
    """Plain English summary built from the normalized fields (used only when the GDTF has no description)."""
    cat = CAT_WORDS.get(rec.get("category"), "fixture")
    moving = rec.get("panRangeDeg") not in (None, 0) and rec.get("tiltRangeDeg")
    src = rec.get("lightSourceType")
    name = rec.get("longName") or ""
    mfr = rec.get("manufacturer") or ""
    head = (name if name.lower().startswith(mfr.lower()) else f"{mfr} {name}") + ": "
    show_src = src and rec.get("category") != "OTHER" and not cat.startswith(src)
    head += ("moving-head " if moving else "") + (f"{src} " if show_src else "") + cat
    if rec.get("subType"):
        head += f" ({rec['subType'].lower()})"
    parts = []
    b = rec.get("beamAngleDeg")
    if b and b.get("min") is not None:
        parts.append(f"{b['min']:g}-{b['max']:g} deg zoom" if rec.get("zoom") and b["min"] != b["max"] else f"{b['min']:g} deg beam")
    cm = rec.get("colorMixing")
    if cm and cm != "None":
        parts.append(f"{cm} colour mixing")
    if rec.get("colorWheel"):
        parts.append("colour wheel")
    if rec.get("goboSlots"):
        parts.append(f"{rec['goboSlots']} gobos")
    if rec.get("framingShutters"):
        parts.append("framing shutters")
    if rec.get("prism"):
        parts.append("prism")
    if rec.get("frost"):
        parts.append("frost")
    if rec.get("ipRating") and rec["ipRating"] not in ("IP20",):
        parts.append(f"{rec['ipRating']} rated")
    return head + (", with " + ", ".join(parts) if parts else "") + "."


def main(src_dir, out_dir):
    os.makedirs(os.path.join(out_dir, "fixtures"), exist_ok=True)
    # manual research, one JSON per manufacturer: {"<gdtf file name>": {field: value, "_refs": [...]}}
    overrides = {}
    ov_dir = os.path.join(out_dir, "overrides")
    if os.path.isdir(ov_dir):
        for fn in sorted(os.listdir(ov_dir)):
            if fn.endswith(".json"):
                with open(os.path.join(ov_dir, fn), encoding="utf-8") as f:
                    overrides.update(json.load(f))
    # an override key may also be just "Manufacturer@Model" (or any unique "Manufacturer@Model@" prefix)
    all_files = sorted(f for f in os.listdir(src_dir) if f.lower().endswith(".gdtf"))
    for k in list(overrides):
        if k not in all_files:
            pre = "@".join(k.split("@")[:2]) + "@"
            hits = [f for f in all_files if f.startswith(pre)]
            if len(hits) == 1:
                overrides.setdefault(hits[0], overrides.pop(k))
    index = []
    for fn in sorted(os.listdir(src_dir)):
        if not fn.lower().endswith(".gdtf"):
            continue
        try:
            meta, rec, src, issues = extract(os.path.join(src_dir, fn))
        except Exception as e:  # keep going, report
            index.append({"file": fn, "error": str(e)})
            continue
        ov = expand_override(overrides.get(fn) or overrides.get(meta["fixtureTypeId"]) or {})
        # "_source": default source for the override ("manual" = manufacturer spec sheet),
        # "_estimated": fields filled from general knowledge / typical values, not a verified sheet
        est = set(ov.get("_estimated", []))
        if "powerSupplyType" in est:
            est |= {"powerConnectorIn", "powerConnectorOut"}
        for short, full in (("mains", ("supportedPowerNetworks", "voltageRangeV", "frequencyRangeHz")),
                            ("lux", ("maxLuminousIntensityCd",)), ("beam", ("beamAngleDeg",)),
                            ("cct", ("colorTemperatureK",))):
            if short in est:
                est |= set(full)
        for k, v in ov.items():
            if k.startswith("_"):
                continue
            rec[k] = v
            src[k] = "estimated" if k in est else ov.get("_source", "manual")
        if ov.get("_notes"):
            issues = issues + [ov["_notes"]]
        if "framingShutterType" not in ov:
            # wash/fresnel optics shape the beam with barn doors, profiles/spots with framing shutters in the gate
            kind = None
            if rec.get("framingShutters"):
                kind = "Barn doors" if rec.get("category") in ("WASH", "WASH BEAM", "PAR", "LED BAR") else "Framing shutters"
            rec["framingShutterType"] = kind
            if kind:
                src["framingShutterType"] = "gdtf-derived"
        if not rec.get("description"):
            rec["description"] = auto_description(rec)
            src["description"] = "gdtf-derived"
        if rec.get("category") not in CATEGORIES:
            issues = issues + [f"INVALID category {rec.get('category')!r}"]
        if rec.get("lightSourceType") not in LIGHT_SOURCES:
            issues = issues + [f"INVALID lightSourceType {rec.get('lightSourceType')!r}"]
        na = list(ov.get("_na", []))  # fields that do not apply to this device
        miss = missing_fields(rec, na)
        doc = {"schemaVersion": SCHEMA_VERSION, **meta, **rec, "_source": src, "_missing": miss, "_notApplicable": na, "_issues": issues}
        if ov.get("_refs"):
            doc["_refs"] = ov["_refs"]
        with open(os.path.join(out_dir, "fixtures", fn[:-5] + ".json"), "w", encoding="utf-8") as f:
            json.dump(doc, f, ensure_ascii=False, indent=2)
        index.append(doc)
    known = {d["file"] for d in index}
    for k in overrides:
        if k not in known:
            print("WARNING: override key matches no GDTF file:", k)
    bad = [(d["file"], i) for d in index for i in d.get("_issues", []) if i.startswith("INVALID")]
    for b in bad:
        print("WARNING:", *b)
    with open(os.path.join(out_dir, "fixtures_index.json"), "w", encoding="utf-8") as f:
        json.dump(index, f, ensure_ascii=False, indent=1)
    print(f"{len(index)} fixtures written")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
