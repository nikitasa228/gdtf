"""One-line-per-fixture summary: python -I brief.py <index.json> <prefix> [<prefix>...]"""
import json, sys
sys.stdout.reconfigure(encoding="utf-8")
d = json.load(open(sys.argv[1], encoding="utf-8"))
for x in d:
    if not any(x["file"].startswith(p + "@") for p in sys.argv[2:]):
        continue
    b = x.get("beamAngleDeg")
    print(f"{x['file'][:72]:72} | {x['longName'][:26]:26} | {str(x['category']):9} | W={x['powerConsumptionW']}{'*' if x['_source'].get('powerConsumptionW')=='gdtf-derived' else ''} "
          f"kg={x['weightKg']} | {x['powerSupplyType']} | nets={'Y' if x['supportedPowerNetworks'] else '-'} | pt={x['panRangeDeg']}/{x['tiltRangeDeg']} | {x['colorMixing']} | "
          f"{x['lightSourceType']} | z={b and (b['min'],b['max'])} gb={x['goboSlots']} bl={int(x['framingShutters'])} | miss={','.join(m for m in x['_missing'] if m!='noiseLevelDb')}")
