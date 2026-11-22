"""Section 5.1 measures for one snapshot: coverage, aligned errors, clearance, relations."""
from __future__ import annotations

import math

import numpy as np

from .geometry import angle_deg, axis_angle_deg
from .relations import Scene, evaluate, path_intrusion, reference_values
from .scene import match_objects, snapshot_objects, target_objects


def _orientation_error(gen, tgt, mode, symmetry, rot):
    if mode == 'round':
        return math.nan
    gd, _ = gen.oriented(mode)
    td, _ = tgt.oriented(mode)
    gd = rot @ gd
    if mode == 'axis':
        return axis_angle_deg(gd, td)
    a = angle_deg(gd, td)
    return min(a, 180.0 - a) if symmetry == '180' else a


def _size_error(gen, tgt, mode):
    _, g = gen.oriented(mode)
    _, t = tgt.oriented(mode)
    return float(np.mean(np.abs(g - t) / np.maximum(t, 1e-6)))


class TargetContext:
    """Everything that depends only on the target scene, computed once."""

    def __init__(self, spec: dict):
        self.spec = spec
        self.targets = target_objects(spec)
        self.modes = {o['id']: (o['mode'], o['symmetry'], o['y0']) for o in spec['objects']}
        self.refs = reference_values(spec, Scene(self.targets))


def score_snapshot(snap: dict, ctx: TargetContext, overrides: dict | None = None):
    """Returns (summary dict, per-relation rows, per-object rows, matching rows)."""
    spec, targets = ctx.spec, ctx.targets
    gen = snapshot_objects(snap)
    match = match_objects(gen, spec, targets, overrides)
    scene = Scene(match.matched, list(match.matched.values()) + match.extras)

    obj_rows, pos, size, orient = [], [], [], []
    for tid, tgt in targets.items():
        mode, symmetry, ty0 = ctx.modes[tid]
        g = match.matched.get(tid)
        row = {'object': tid, 'present': g is not None}
        if g is not None:
            p = float(np.linalg.norm(match.align_point(g.c) - tgt.c))
            se = _size_error(g, tgt, mode)
            oe = _orientation_error(g, tgt, mode, symmetry, match.rot)
            row.update(position_error_m=round(p, 4), size_error=round(se, 4),
                       orientation_error_deg='' if math.isnan(oe) else round(oe, 2))
            if ty0 > 0.05:
                row['vertical_error_m'] = round(abs(g.y0 - ty0), 4)
            pos.append(p)
            size.append(se)
            if not math.isnan(oe):
                orient.append(oe)
        obj_rows.append(row)

    rel_rows, satisfied, computed = [], 0, 0
    for rel in spec['relations']:
        ok, vals = evaluate(rel, scene, ctx.refs.get(rel['id']))
        row = {'relation': rel['id'], 'type': rel['type'],
               'satisfied': '' if ok is None else int(bool(ok)),
               'detail': '; '.join(f'{k}={v}' for k, v in vals.items())}
        rel_rows.append(row)
        if ok is not None:
            computed += 1
            satisfied += int(bool(ok))

    kept, intruders, volume, note = path_intrusion(scene, spec['path'])
    n_targets = len(targets)
    summary = {
        'objects_present': len(match.matched),
        'objects_required': n_targets,
        'object_coverage': round(len(match.matched) / n_targets, 4),
        'extra_objects': len(match.extras),
        'position_error_median_m': round(float(np.median(pos)), 4) if pos else '',
        'size_error_median': round(float(np.median(size)), 4) if size else '',
        'orientation_error_median_deg': round(float(np.median(orient)), 2) if orient else '',
        'clearance_kept': int(kept),
        'clearance_intruders': ';'.join(intruders),
        'clearance_intruding_volume_m3': round(volume, 4) if volume == volume else '',
        'relations_satisfied': satisfied,
        'relations_computed': computed,
        'relation_rate': round(satisfied / computed, 4) if computed else '',
        'matches_to_check': sum(1 for r in match.rows if r['check'] in ('position_only', 'far')),
    }
    return summary, rel_rows, obj_rows, match.rows
