"""Audit complete saved ground grids and persistent entities; no game/player files changed."""
import argparse
import base64
from collections import Counter
import json
from pathlib import Path
import xml.etree.ElementTree as ET
import zlib

parser = argparse.ArgumentParser()
parser.add_argument("run_root", type=Path)
args = parser.parse_args()
root = args.run_root.resolve()
root.relative_to((Path(__file__).resolve().parent / "Artifacts").resolve())
baseline = json.loads((root / "migration-baseline.json").read_text(encoding="utf-8"))

def read_map(name):
    return next(node for node in ET.parse(root / "Saves" / (name + ".rws")).findall("./game/maps/li")
                if int(node.findtext("uniqueID")) == baseline["map"])

old = read_map("RimLayers_V1Migration")
converted = read_map("RimLayers_V1MigrationConverted")
checks = []
def check(name, passed, **detail):
    checks.append(dict(name=name, passed=bool(passed), **detail))
def unpack(node):
    return zlib.decompress(base64.b64decode(node.text), -15)

check("source-has-no-layer-component", old.find("./components/li[@Class='RimLayers.Core.LayerMapComponent']") is None)
for path in ("terrainGrid/topGridDeflate", "terrainGrid/underGridDeflate", "terrainGrid/foundationGridDeflate",
             "terrainGrid/tempGridDeflate", "terrainGrid/colorGridDeflate", "roofGrid/roofsDeflate",
             "fogGrid/fogGridDeflate", "gasGrid/gasDensityDeflate", "compressedThingMapDeflate"):
    before, after = unpack(old.find(path)), unpack(converted.find(path))
    check(path, before == after[:len(before)], originalBytes=len(before), convertedBytes=len(after))
river = old.find('./components/li[@Class="WaterInfo"]/riverFlowMapDeflate')
if river is not None:
    before = unpack(river)
    after = unpack(converted.find('./components/li[@Class="WaterInfo"]/riverFlowMapDeflate'))
    width, height = baseline["width"], baseline["height"]
    storage_height = 3 * (((height + 1 + 16) // 17) * 17)
    same = len(before) == width * height * 8 and len(after) == width * storage_height * 8
    upper_zero = same
    for x in range(width):
        same = same and before[x*height*8:(x+1)*height*8] == after[x*storage_height*8:(x*storage_height+height)*8]
        upper_zero = upper_zero and not any(after[(x*storage_height+height)*8:(x+1)*storage_height*8])
    check("every-river-float-bit-preserved-after-x-major-remap", same and upper_zero,
          originalBytes=len(before), convertedBytes=len(after))
for area in old.findall("./areaManager/areas/li"):
    match = next(a for a in converted.findall("./areaManager/areas/li") if a.findtext("ID") == area.findtext("ID"))
    before, after = unpack(area.find("./innerGrid/arrDeflate")), unpack(match.find("./innerGrid/arrDeflate"))
    check("area-" + area.get("Class"), before == after[:len(before)], originalBytes=len(before), convertedBytes=len(after))

def things(node):
    return node.findall("./things/*")
def pawns(node):
    return {t.findtext("id") for t in things(node) if t.find("ageTracker") is not None}
check("every-pawn-id-preserved-including-wildlife", pawns(old) == pawns(converted), count=len(pawns(old)))
# Native loading reconstructs compressed rocks/buildings and removes overlapping filth
# via FullRefund. Animals and the test Pawn also move during the real path test.
# Inventory identity/count is checked by the native load scenario, not inferred here.
def static_facts(node):
    return Counter(tuple(t.findtext(k) for k in ("id", "def", "stuff", "pos", "stackCount"))
                   for t in things(node) if t.find("ageTracker") is None
                   and not t.findtext("def", "").startswith("Filth_") and t.findtext("def") != "RL_Stairs")
a, b = static_facts(old), static_facts(converted)
check("all-persistent-non-pawn-non-filth-records-identical", a == b, originalCount=sum(a.values()), convertedCount=sum(b.values()))
filth = lambda node: sum(t.findtext("def", "").startswith("Filth_") for t in things(node))
report = dict(checks=checks, failures=sum(not c["passed"] for c in checks),
              nativeSimulationDifferences=dict(originalFilth=filth(old), convertedFilth=filth(converted),
                  snow="Native weather continues during the stair walk; converted-load uses the saved snapshot."))
(root / "migration-grid-audit.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
print(json.dumps(report, ensure_ascii=False, indent=2))
raise SystemExit(bool(report["failures"]))
