import json, sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[4] / 'analysis'))
from spatialscore.scene import snapshot_objects
from score_edits import part_change, surface_deviation
base=Path(__file__).resolve().parent
before=json.loads((base / 'partial_before.json').read_text())
after=json.loads((base / 'partial_after.json').read_text())
a=snapshot_objects(before)[0].parts[0];b=snapshot_objects(after)[0].parts[0]
raw0=before['assets'][0]['parts'][0];raw1=after['assets'][0]['parts'][0]
print('Source: real Unity partial-removal snapshots; fixture asserts remaining volume = 0.6 (40% removed).')
print('Before clipped cells:', len(raw0.get('clipped_geometry_local') or []))
print('After clipped cells:', len(raw1.get('clipped_geometry_local') or []))
print('Scorer change kinds:', part_change(a,b))
print('Scorer max surface deviation:', surface_deviation([a],[b])[0].max())
assert len(raw1['clipped_geometry_local']) > 0
assert part_change(a,b) == [], 'Current bug no longer reproduces'
