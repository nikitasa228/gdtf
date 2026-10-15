"""Compact per-manufacturer table for research: python -I list_mfr.py <index.json> <file-prefix>"""
import json, sys
sys.stdout.reconfigure(encoding="utf-8")
d = json.load(open(sys.argv[1], encoding="utf-8"))
for x in d:
    if not x["file"].startswith(sys.argv[2] + "@"):
        continue
    ba = x.get("beamAngleDeg")
    print(f'{x["file"]}\n   {x["longName"]} | cat={x["category"]} | W={x["powerConsumptionW"]}({x["_source"].get("powerConsumptionW","")[:4]}) '
          f'kg={x["weightKg"]} | pwr={x["powerSupplyType"]} nets={bool(x["supportedPowerNetworks"])} | pan/tilt={x["panRangeDeg"]}/{x["tiltRangeDeg"]} '
          f'| mix={x["colorMixing"]} gobo={x["goboSlots"]} blades={x["framingShutters"]} prism={x["prism"]} frost={x["frost"]} '
          f'| beam={ba and (ba["min"],ba["max"])} cct={x["colorTemperatureK"] and tuple(x["colorTemperatureK"].values())} '
          f'| src={x["lightSourceType"]} lens={x["lensType"]} | miss={",".join(x["_missing"])}')
