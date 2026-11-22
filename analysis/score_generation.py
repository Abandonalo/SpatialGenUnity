"""Section 5.1: score generated scenes against their target scene.

Reads the snapshots the Spatial Generation window writes to
  Logs/SpatialGenerationStudy/<participant>/condition_<A|B>/<time>_<label>.json
and writes, to --out:
  generation_scores.csv     one row per participant x condition x snapshot (accepted, final)
  generation_relations.csv  one row per relation (satisfied 1/0; R5 empty = not computed)
  generation_objects.csv    one row per target object (present, aligned errors)
  generation_matches.csv    how each generated asset was matched; rows with check != '' need a look

Usage:
  python score_generation.py --logs "<project>/Logs/SpatialGenerationStudy" --out results
  python score_generation.py --snapshot one_snapshot.json --target X       (score a single file)
Options: --overrides matches_override.csv  --include-dev  --snapshots accepted,final
"""
from __future__ import annotations

import argparse
import csv
import json
import re
import sys
from pathlib import Path

from spatialscore.measures import TargetContext, score_snapshot
from spatialscore.scene import load_overrides, load_snapshot, load_target, target_scene_id

LABEL_RE = re.compile(r'^\d{8}_\d{6}_\d{3}_(?P<label>.+)\.json$')


def find_snapshots(root: Path, include_dev: bool, kinds):
    """Yield (participant, condition, kind, path) for the latest snapshot of each kind."""
    for pdir in sorted(p for p in root.iterdir() if p.is_dir()):
        if pdir.name.lower() in ('dev', 'unassigned') and not include_dev:
            continue
        for cdir in sorted(c for c in pdir.iterdir() if c.is_dir() and c.name.startswith('condition_')):
            cond = cdir.name.split('_', 1)[1]
            latest = {}
            for f in sorted(cdir.glob('*.json')):
                m = LABEL_RE.match(f.name)
                if not m:
                    continue
                label = m.group('label')
                kind = 'accepted' if label.startswith('accepted_run') else 'final' if label == 'final' else None
                if kind in kinds:
                    latest[kind] = f          # files sort by time stamp, so the last one wins
            for kind, f in latest.items():
                yield pdir.name, cond, kind, f


def write_csv(path: Path, rows):
    if not rows:
        return
    keys = []
    for r in rows:
        keys += [k for k in r if k not in keys]
    with open(path, 'w', newline='') as fh:
        w = csv.DictWriter(fh, fieldnames=keys)
        w.writeheader()
        w.writerows(rows)


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--logs', type=Path, help='Logs/SpatialGenerationStudy folder')
    ap.add_argument('--snapshot', type=Path, help='score one snapshot file instead')
    ap.add_argument('--target', help='X or Y; overrides the target named in the snapshot')
    ap.add_argument('--out', type=Path, default=Path('results'))
    ap.add_argument('--overrides', type=Path, help='coder corrections to the object matching')
    ap.add_argument('--include-dev', action='store_true', help='also score developer-mode sessions')
    ap.add_argument('--snapshots', default='accepted,final', help='which snapshots to score')
    args = ap.parse_args(argv)
    if not args.logs and not args.snapshot:
        ap.error('give --logs or --snapshot')

    kinds = {k.strip() for k in args.snapshots.split(',') if k.strip()}
    overrides = load_overrides(args.overrides)
    contexts = {}

    if args.snapshot:
        jobs = [('', '', 'single', args.snapshot)]
    else:
        jobs = list(find_snapshots(args.logs, args.include_dev, kinds))
        if not jobs:
            sys.exit(f'No snapshots found under {args.logs}')

    scores, rels, objs, matches = [], [], [], []
    for participant, cond, kind, path in jobs:
        snap = load_snapshot(path)
        participant = participant or snap.get('participant', '')
        cond = cond or snap.get('condition', '')
        scene = args.target or target_scene_id(snap.get('target', ''))
        ctx = contexts.setdefault(scene, TargetContext(load_target(scene)))
        ov = overrides.get((participant, cond, kind), {})
        summary, rel_rows, obj_rows, match_rows = score_snapshot(snap, ctx, ov)
        key = {'participant': participant, 'condition': cond, 'target': scene, 'snapshot': kind}
        scores.append({**key, 'utc': snap.get('utc', ''), 'file': str(path), **summary})
        rels += [{**key, **r} for r in rel_rows]
        objs += [{**key, **r} for r in obj_rows]
        matches += [{**key, **r} for r in match_rows]

    # condition order per participant, from the time each condition's snapshot was taken
    first = {}
    for s in scores:
        if s['snapshot'] in ('accepted', 'single'):
            p = s['participant']
            if p not in first or s['utc'] < first[p][1]:
                first[p] = (s['condition'], s['utc'])
    for s in scores:
        f = first.get(s['participant'])
        s['order'] = '' if not f else ('first' if s['condition'] == f[0] else 'second')

    args.out.mkdir(parents=True, exist_ok=True)
    write_csv(args.out / 'generation_scores.csv', scores)
    write_csv(args.out / 'generation_relations.csv', rels)
    write_csv(args.out / 'generation_objects.csv', objs)
    write_csv(args.out / 'generation_matches.csv', matches)
    to_check = sum(1 for m in matches if m['check'] in ('position_only', 'far'))
    print(f'Scored {len(scores)} snapshot(s) -> {args.out}')
    if to_check:
        print(f'{to_check} object match(es) need checking: see generation_matches.csv, column check. '
              'Corrections go into a --overrides CSV (participant, condition, snapshot, asset_id, target_id).')


if __name__ == '__main__':
    main()
