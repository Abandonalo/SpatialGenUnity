"""Section 5.2: out-of-target change and boundary integrity for every refinement edit.

Each edit has two snapshots in Logs/SpatialGenerationStudy/<participant>/condition_<A|B>/:
  <time>_edit<n>_before.json   and   <time>_edit<n>_after.json   (or edit<n>_undone.json)
  When an applied edit is undone (and maybe redone), the latest after/undone snapshot is scored.

The intended target of an edit is a coder's judgement, so it comes from a coding CSV:
  participant, condition, edit, intended_targets
where intended_targets is one or more target object ids separated by ';' (e.g. 'stall_a;crates'),
or 'unclear' (the edit is then left out of both measures). Without --coding the script writes
edit_coding_template.csv, pre-filled with the request text and region from the interaction log,
for the coders to fill in.

Outputs (in --out):
  edit_scores.csv   one row per edit
  edit_parts.csv    one row per changed part outside the intended target (what changed)

Usage:
  python score_edits.py --logs "<project>/Logs/SpatialGenerationStudy" \
                        --interaction-logs "<project>/Logs/SpatialGenerationLogs" --out results
  python score_edits.py --logs ... --coding edit_coding.csv --out results
"""
from __future__ import annotations

import argparse
import csv
import json
import re
from pathlib import Path

import numpy as np

from spatialscore.geometry import min_gap, penetration_depth, point_box_surface_distance, solids
from spatialscore.scene import (load_overrides, load_snapshot, load_target, match_objects, snapshot_objects,
                                target_objects, target_scene_id)

EDIT_RE = re.compile(r'^\d{8}_\d{6}_\d{3}_edit(?P<n>\d+)_(?P<kind>before|after|undone)\.json$')

MOVE_TOL_M = 0.01        # a part counts as moved beyond 1 cm
ROT_TOL_DEG = 1.0
SIZE_TOL_REL = 0.01
DEV_THRESHOLD_M = 0.02   # surface counted as changed beyond 2 cm
SAMPLE_SPACING_M = 0.03
TOUCH_M = 0.01           # a kept part touched the changed region if within 1 cm before the edit
GAP_M = 0.02             # new gap / new penetration threshold
MINOR_M = 0.05           # minor vs broken boundary


# ----------------------------------------------------------------------------- finding edits

def find_edits(root: Path, include_dev=False):
    for pdir in sorted(p for p in root.iterdir() if p.is_dir()):
        if pdir.name.lower() in ('dev', 'unassigned') and not include_dev:
            continue
        for cdir in sorted(c for c in pdir.iterdir() if c.is_dir() and c.name.startswith('condition_')):
            cond = cdir.name.split('_', 1)[1]
            edits = {}
            for f in sorted(cdir.glob('*.json')):
                m = EDIT_RE.match(f.name)
                if m:
                    edits.setdefault(int(m['n']), {})[m['kind']] = f
            for n, files in sorted(edits.items()):
                if 'before' in files and ('after' in files or 'undone' in files):
                    # Ctrl+Z / Ctrl+Y after an apply write further undone / after snapshots; the
                    # latest of them is the state the edit left the scene in.
                    files['result'] = max((files[k] for k in ('after', 'undone') if k in files), key=lambda f: f.name)
                    yield pdir.name, cond, n, files


def read_requests(log_dir: Path | None):
    """{(participant, condition, edit): {request, region_center, region_size}} from interaction logs."""
    out = {}
    if not log_dir or not log_dir.exists():
        return out
    for f in sorted(log_dir.glob('*.jsonl')):
        for line in f.read_text(errors='replace').splitlines():
            try:
                ev = json.loads(line)
            except json.JSONDecodeError:
                continue
            if ev.get('type') != 'study_edit_submit':
                continue
            text = next((v for v in ev.values() if isinstance(v, str) and 'edit=' in v), '')
            fields = {}
            for part in re.split(r';\s*(?=[a-z_]+=)', text):
                k, _, v = part.partition('=')
                fields[k.strip()] = v.strip()
            try:
                key = (fields.get('participant', ''), fields.get('condition', ''), int(fields.get('edit', '-1')))
            except ValueError:
                continue
            out[key] = {'request': fields.get('request', ''), 'region_center': fields.get('region_center', ''),
                        'region_size': fields.get('region_size', '')}
    return out


def read_coding(path):
    coding = {}
    with open(path, newline='') as f:
        for r in csv.DictReader(f):
            val = (r.get('intended_targets') or '').strip()
            if not val:
                continue
            key = (r['participant'], r['condition'], int(r['edit']))
            coding[key] = None if val.lower() == 'unclear' else [t.strip() for t in val.split(';') if t.strip()]
    return coding


# ----------------------------------------------------------------------------- comparisons

def _parts_by_id(objs):
    return {p.meta.get('tag') or p.tag: (o, p) for o in objs for p in o.parts}


def _rot_diff_deg(r1, r2):
    c = (np.trace(r1.T @ r2) - 1) / 2
    return float(np.degrees(np.arccos(np.clip(c, -1, 1))))


def part_change(p0, p1):
    """List of change kinds between the same part before and after."""
    kinds = []
    if np.linalg.norm(p0.center - p1.center) > MOVE_TOL_M:
        kinds.append('moved')
    if _rot_diff_deg(p0.rot, p1.rot) > ROT_TOL_DEG:
        kinds.append('rotated')
    if np.any(np.abs(p1.half - p0.half) > SIZE_TOL_REL * np.maximum(p0.half, 1e-6)):
        kinds.append('resized')
    if p0.meta.get('color') != p1.meta.get('color'):
        kinds.append('recolored')
    if p0.meta.get('shape') != p1.meta.get('shape') or p0.meta.get('segments') != p1.meta.get('segments'):
        kinds.append('reshaped')
    if _piece_signature(p0) != _piece_signature(p1):
        kinds.append('clipped')
    return kinds


def _piece_signature(part):
    """The part's clipped pieces, rounded to the move tolerance; empty for an unclipped part."""
    q = lambda v: tuple(np.round(np.asarray(v) / MOVE_TOL_M).astype(int))
    return sorted((q(b.center), q(b.half)) for b in part.meta.get('pieces') or [])


def part_penetration(a, b):
    """Deepest overlap between the actual geometry of two parts."""
    return max((penetration_depth(x, y) for x in solids([a]) for y in solids([b])), default=0.0)


def is_remainder(part, before_parts):
    """True for the untouched leftover of a partially edited part: a new part split off from a part
    that existed before (SceneSnapshot `source_part_id`) whose geometry lies inside that part's
    geometry before the edit. It is preserved material, not a change."""
    source = part.meta.get('source')
    if not source or source not in before_parts:
        return False
    before = solids([before_parts[source][1]])
    return all(any(np.all(b.contains(c, tol=0.005)) for b in before) for piece in solids([part])
               for c in piece.corners())


def surface_deviation(parts_a, parts_b):
    """Distances from the surface of parts_a to the nearest surface of parts_b, area weighted."""
    if not parts_a:
        return np.zeros(0), np.zeros(0)
    parts_a, parts_b = solids(parts_a), solids(parts_b)
    pts, areas = [], []
    for p in parts_a:
        q, a = p.surface_points(SAMPLE_SPACING_M)
        pts.append(q)
        areas.append(a)
    pts, areas = np.vstack(pts), np.concatenate(areas)
    if not parts_b:
        return np.full(len(pts), np.inf), areas
    d = np.min(np.column_stack([point_box_surface_distance(pts, b) for b in parts_b]), axis=1)
    return d, areas


def weighted_quantile(x, w, q):
    if len(x) == 0:
        return float('nan')
    i = np.argsort(x)
    x, w = x[i], w[i]
    c = np.cumsum(w) / w.sum()
    return float(x[min(np.searchsorted(c, q), len(x) - 1)])


def floating(part, others):
    """True when a part neither rests on the ground nor touches any other part."""
    if min(b.y_min for b in solids([part])) <= 0.02:
        return False
    return all(min_gap(solids([part]), solids([o])) > GAP_M for o in others if o is not part)


# ----------------------------------------------------------------------------- one edit

def score_edit(before: dict, after: dict, spec: dict, targets: dict, intended: list, overrides=None):
    b_objs = snapshot_objects(before)
    a_objs = snapshot_objects(after)
    match = match_objects(b_objs, spec, targets, overrides)
    asset_to_target = {o.asset_id: tid for tid, o in match.matched.items()}
    intended_assets = {o.asset_id for tid, o in match.matched.items() if tid in intended}
    missing_targets = [t for t in intended if t not in match.matched]

    b_parts, a_parts = _parts_by_id(b_objs), _parts_by_id(a_objs)
    b_assets = {o.asset_id: o for o in b_objs}
    a_assets = {o.asset_id: o for o in a_objs}

    # ---------------- out-of-target change
    part_rows = []
    outside_before = [p for o in b_objs if o.asset_id not in intended_assets for p in o.parts]
    outside_after = [p for o in a_objs if o.asset_id in b_assets and o.asset_id not in intended_assets for p in o.parts]
    changed = removed = added = 0
    for pid, (o, p0) in b_parts.items():
        if o.asset_id in intended_assets:
            continue
        if pid not in a_parts:
            removed += 1
            part_rows.append({'asset_id': o.asset_id, 'object': asset_to_target.get(o.asset_id, ''),
                              'part_id': pid, 'change': 'removed'})
            continue
        kinds = part_change(p0, a_parts[pid][1])
        if kinds:
            changed += 1
            part_rows.append({'asset_id': o.asset_id, 'object': asset_to_target.get(o.asset_id, ''),
                              'part_id': pid, 'change': '+'.join(kinds)})
    for pid, (o, p1) in a_parts.items():
        if pid not in b_parts and o.asset_id in b_assets and o.asset_id not in intended_assets \
                and not is_remainder(p1, b_parts):
            added += 1
            part_rows.append({'asset_id': o.asset_id, 'object': asset_to_target.get(o.asset_id, ''),
                              'part_id': pid, 'change': 'added'})
    removed_assets = [aid for aid in b_assets if aid not in a_assets and aid not in intended_assets]
    new_assets = [o for o in a_objs if o.asset_id not in b_assets]

    d1, w1 = surface_deviation(outside_before, outside_after)
    d2, w2 = surface_deviation(outside_after, outside_before)
    d, w = np.concatenate([d1, d2]), np.concatenate([w1, w2])
    area = float(w.sum())
    dev_area = float(w[d > DEV_THRESHOLD_M].sum())
    n_outside = len(outside_before)

    # ---------------- boundary integrity
    changed_after = []      # C: added or modified parts of the intended assets, plus parts of new assets
    removed_or_mod_before = []
    for aid in intended_assets:
        o_b, o_a = b_assets.get(aid), a_assets.get(aid)
        before_ids = {p.tag for p in o_b.parts} if o_b else set()
        for p in (o_a.parts if o_a else []):
            if p.tag not in before_ids and is_remainder(p, b_parts):
                continue                      # preserved leftover of a partially edited part
            if p.tag not in before_ids or part_change(b_parts[p.tag][1], p):
                changed_after.append(p)
        for p in (o_b.parts if o_b else []):
            if p.tag not in a_parts or part_change(p, a_parts[p.tag][1]):
                removed_or_mod_before.append(p)
    for o in new_assets:
        changed_after.extend(o.parts)
    changed_ids = {p.tag for p in changed_after}
    kept = [p for _, p in a_parts.values() if p.tag not in changed_ids and
            (p.tag in b_parts or is_remainder(p, b_parts))]

    def before_of(p):
        """The part's geometry before the edit; a remainder was already there as itself."""
        return b_parts[p.tag][1] if p.tag in b_parts else p

    if not changed_after:
        level, worst_gap, worst_pen, n_float = 'no_boundary', float('nan'), float('nan'), 0
    else:
        # kept parts that touched what the edit replaced should still touch the new geometry
        touching = [p for p in kept if removed_or_mod_before and
                    min_gap(solids([before_of(p)]), solids(removed_or_mod_before)) <= TOUCH_M]
        gaps = [min_gap(solids([p]), solids(changed_after)) for p in touching]
        worst_gap = max([g for g in gaps if g > GAP_M], default=0.0)
        # new penetrations between changed and kept geometry that were not there before
        worst_pen = 0.0
        for c in changed_after:
            for k in kept:
                pen = part_penetration(c, k)
                if pen > GAP_M:
                    k0 = before_of(k)
                    c0 = b_parts[c.tag][1] if c.tag in b_parts else None
                    if c0 is None or part_penetration(c0, k0) <= GAP_M:
                        worst_pen = max(worst_pen, pen)
        all_after = [p for _, p in a_parts.values()]
        all_before = [p for _, p in b_parts.values()]
        n_float = sum(floating(c, all_after) and not (c.tag in b_parts and floating(b_parts[c.tag][1], all_before))
                      for c in changed_after)   # only parts the edit left floating
        worst = max(worst_gap, worst_pen)
        level = 'broken' if worst > MINOR_M or n_float else 'minor' if worst > GAP_M else 'intact'

    return {
        'intended_targets': ';'.join(intended),
        'intended_not_found': ';'.join(missing_targets),
        'outside_parts_before': n_outside,
        'outside_parts_changed': changed,
        'outside_parts_removed': removed,
        'outside_parts_added': added,
        'outside_assets_removed': len(removed_assets),
        'new_assets': len(new_assets),
        'out_of_target_change': int(bool(changed or removed or added or removed_assets)),
        'out_of_target_part_share': round((changed + removed) / n_outside, 4) if n_outside else '',
        'out_of_target_area_share': round(dev_area / area, 4) if area else '',
        'surface_dev_median_m': round(weighted_quantile(d, w, 0.5), 4) if len(d) else '',
        'surface_dev_p95_m': round(weighted_quantile(d, w, 0.95), 4) if len(d) else '',
        'changed_parts_in_target': len(changed_after),
        'boundary_worst_gap_m': '' if worst_gap != worst_gap else round(worst_gap, 4),
        'boundary_worst_new_penetration_m': '' if worst_pen != worst_pen else round(worst_pen, 4),
        'boundary_floating_parts': n_float,
        'boundary_integrity': level,
    }, part_rows


# ----------------------------------------------------------------------------- main

def write_csv(path, rows):
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
    ap.add_argument('--logs', type=Path, required=True)
    ap.add_argument('--interaction-logs', type=Path, help='Logs/SpatialGenerationLogs, for the request text')
    ap.add_argument('--coding', type=Path, help='coded intended targets')
    ap.add_argument('--overrides', type=Path, help='object-matching corrections (snapshot column = edit<n>)')
    ap.add_argument('--target', help='X or Y, if snapshots do not name it')
    ap.add_argument('--out', type=Path, default=Path('results'))
    ap.add_argument('--include-dev', action='store_true')
    args = ap.parse_args(argv)
    args.out.mkdir(parents=True, exist_ok=True)

    edits = list(find_edits(args.logs, args.include_dev))
    requests = read_requests(args.interaction_logs)

    if not args.coding:
        rows = []
        for p, c, n, files in edits:
            snap = load_snapshot(files['before'])
            req = requests.get((p, c, n), {})
            rows.append({'participant': p, 'condition': c, 'edit': n, 'target_scene': snap.get('target', ''),
                         'request': req.get('request', ''), 'region_center': req.get('region_center', ''),
                         'region_size': req.get('region_size', ''), 'intended_targets': ''})
        write_csv(args.out / 'edit_coding_template.csv', rows)
        print(f'{len(rows)} edit(s). Fill intended_targets in {args.out / "edit_coding_template.csv"} '
              '(target ids separated by ";", or "unclear") and rerun with --coding.')
        return

    coding = read_coding(args.coding)
    overrides = load_overrides(args.overrides)
    specs = {}
    scores, parts = [], []
    for p, c, n, files in edits:
        key = (p, c, n)
        if key not in coding:
            continue
        before = load_snapshot(files['before'])
        after = load_snapshot(files['result'])
        scene = args.target or target_scene_id(before.get('target', ''))
        spec, targets = specs.setdefault(scene, (load_target(scene), None))
        if targets is None:
            targets = target_objects(spec)
            specs[scene] = (spec, targets)
        row = {'participant': p, 'condition': c, 'edit': n, 'target': scene,
               'undone': int(files['result'].name.endswith('_undone.json'))}
        if coding[key] is None:
            scores.append({**row, 'intended_targets': 'unclear'})
            continue
        s, part_rows = score_edit(before, after, spec, targets, coding[key], overrides.get((p, c, f'edit{n}'), {}))
        scores.append({**row, **s})
        parts += [{**row, **r} for r in part_rows]

    write_csv(args.out / 'edit_scores.csv', scores)
    write_csv(args.out / 'edit_parts.csv', parts)
    print(f'Scored {len(scores)} edit(s) -> {args.out}')


if __name__ == '__main__':
    main()
