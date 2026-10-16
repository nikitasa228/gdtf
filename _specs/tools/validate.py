"""
Check every record in fixtures_index.json against the schema in SCHEMA.md.

Usage:  python -I validate.py <fixtures_index.json>
Exit code 1 when a record breaks the schema (wrong type, value outside a vocabulary, missing required field).
"""
import json, re, sys

CATEGORIES = {"PROFILE", "WASH", "WASH BEAM", "BEAM", "SPOT", "FOLLOW SPOT", "LED BAR", "PAR", "HAZE", "FOG", "FAN",
              "SCANER", "LASER", "STROBE", "BLINDER", "UV", "BSW", "GENERATOR FIRE", "OTHER"}
LIGHT_SOURCES = {"LED", "Discharge Lamp", "Tungsten Lamp", "Halogen Lamp", "Laser", None}
NETWORKS = {"100 V - 50 Hz", "100 V - 60 Hz", "110/120 V - 60 Hz", "110/120 V - 50 Hz", "220/230 V - 50 Hz",
            "220/230 V - 60 Hz", "240/245 V - 50 Hz"}
CONNECTORS = {"powerCON", "powerCON TRUE1", "powerCON TRUE1 TOP", "Schuko", "IEC C14", "IEC C20", "CEE 16A",
              "Fixed Cable", "DC Jack", "DC (external PSU)", "DIN3 (low-voltage DC)", "XLR4 (DC)", "XLR5",
              "Terminal Block", "Martin proprietary (P3/PDE)", "Martin 8-pin", "IP moulded connector",
              "4-pin power/data connector", "Track adapter", "E27 lamp socket", "Battery (internal)", "PWB-CAB Connector"}
SIGNALS = {"DMX512", "RDM", "Ethernet (Art-Net/sACN)", "Wireless DMX (CRMX/W-DMX)", "Ethernet (Martin P3)"}
LENSES = {"Spot", "Wash", "Fresnel", "PC (Plano-Convex)", "Rectangle", "Ellipsoidal (Profile)", "Diffused (Glow)",
          "Asymmetric (Cyc)", None}
SHUTTERS = {"Framing shutters", "Barn doors", None}
SOURCES = {"gdtf", "gdtf-derived", "manual", "estimated"}
REQUIRED = ["longName", "shortName", "fullName", "description", "manufacturer", "category", "supportedPowerNetworks",
            "powerConsumptionW", "maxLuminousIntensityCd", "noiseLevelDb", "weightKg", "powerSupplyType",
            "panRangeDeg", "tiltRangeDeg", "controlSignal", "lightSourceType", "beamAngleDeg", "zoom", "lensType",
            "goboSlots", "framingShutters", "colorMixing", "colorTemperatureK", "prism", "frost"]


def connectors(v):
    """'A / B' = alternative inputs, 'A + B' = both, trailing ' LINK' = has a power output."""
    v = re.sub(r" LINK$", "", v)
    return [p.strip() for p in re.split(r" / | \+ ", v)]


def check(x):
    err = []
    num = lambda k, lo, hi: x.get(k) is None or (isinstance(x[k], (int, float)) and lo <= x[k] <= hi) or err.append(f"{k}={x[k]!r}")
    rng = lambda k, lo, hi: x.get(k) is None or (isinstance(x[k], dict) and lo <= x[k]["min"] <= x[k]["max"] <= hi) or err.append(f"{k}={x[k]!r}")
    if x["category"] not in CATEGORIES: err.append(f"category={x['category']!r}")
    if x["lightSourceType"] not in LIGHT_SOURCES: err.append(f"lightSourceType={x['lightSourceType']!r}")
    if x["lensType"] not in LENSES: err.append(f"lensType={x['lensType']!r}")
    if x["framingShutterType"] not in SHUTTERS: err.append(f"framingShutterType={x['framingShutterType']!r}")
    for n in x.get("supportedPowerNetworks") or []:
        if n not in NETWORKS: err.append(f"network={n!r}")
    for s in x.get("controlSignal") or []:
        if s not in SIGNALS: err.append(f"signal={s!r}")
    for k in ("powerSupplyType", "powerConnectorIn", "powerConnectorOut"):
        if x.get(k):
            for c in connectors(x[k]):
                if c not in CONNECTORS: err.append(f"{k} part {c!r}")
    num("powerConsumptionW", 0.5, 20000); num("weightKg", 0.01, 200); num("noiseLevelDb", 0, 120)
    num("maxLuminousIntensityCd", 1, 5e8); num("panRangeDeg", 1, 720); num("tiltRangeDeg", 1, 720)
    num("maxInputPowerW", 0.5, 50000); num("maxLoadW", 1, 50000); num("maxLoadCurrentA", 0.1, 400)
    num("maxLoadPerChannelA", 0.1, 400); num("outputChannels", 1, 256)
    if x.get("maxLoadW") and x.get("powerConsumptionW") and x["powerConsumptionW"] > x["maxLoadW"]:
        err.append("powerConsumptionW (own draw) exceeds maxLoadW")
    rng("beamAngleDeg", 0.1, 360); rng("colorTemperatureK", 1000, 20000); rng("voltageRangeV", 5, 480)
    if x.get("ipRating") is not None and not re.fullmatch(r"IP[0-6X][0-9X]", x["ipRating"]): err.append(f"ipRating={x['ipRating']!r}")
    for k, v in x["_source"].items():
        if v not in SOURCES: err.append(f"_source[{k}]={v!r}")
    for k in REQUIRED + ["maxInputPowerW", "maxLoadW", "maxLoadCurrentA", "maxLoadPerChannelA", "outputChannels", "outputVoltage"]:
        if k not in x: err.append(f"field {k} absent")
    na = set(x["_notApplicable"])
    moving = x.get("motorizedPanTilt")
    for k in REQUIRED:
        if x.get(k) is None and k not in na and k not in x["_missing"]:
            if k in ("panRangeDeg", "tiltRangeDeg") and not moving: continue
            if x.get("lightSourceType") is None and k in ("maxLuminousIntensityCd", "beamAngleDeg", "lensType",
                                                       "colorTemperatureK", "lightSourceType"): continue
            if k in ("panRangeDeg", "tiltRangeDeg") and moving and (x.get("panRangeDeg") or x.get("tiltRangeDeg")): continue
            err.append(f"{k} is null but neither _missing nor _notApplicable")
    return err


def main(path):
    data = json.load(open(path, encoding="utf-8"))
    bad = 0
    for x in data:
        e = check(x)
        if e:
            bad += 1
            print(x["file"], "->", "; ".join(e))
    print(f"{len(data)} records, {bad} with schema errors, {sum(bool(x['_missing']) for x in data)} with missing fields")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1]))
