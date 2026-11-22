"""The relation rules of section 5.1 and the clear-path test.

Every rule reads a scene: {object id: SceneObject} plus the list of all objects (matched and
unmatched), because unmatched geometry can still block the path. Rules whose target value is
a number (a distance, a gap, a size ratio, a lateral offset) compare the generated scene with
the same quantity measured on the target scene, so the target values always follow the layout.
"""
from __future__ import annotations

import math

import numpy as np

from .geometry import Box, angle_deg, axis_angle_deg, min_gap, penetration_depth, unit2

NOT_COMPUTED = {'occlusion'}


class Scene:
    def __init__(self, objects: dict, all_objects: list | None = None):
        self.o = objects
        self.all = all_objects if all_objects is not None else list(objects.values())

    def has(self, *ids):
        return [i for i in ids if i not in self.o]


def _dir(obj, mode):
    d, _ = obj.oriented(mode)
    return d


def _proj_extent(parts, origin, axis):
    """Min and max of the parts' corners projected on a horizontal axis, relative to origin."""
    vals = np.concatenate([(p.corners()[:, [0, 2]] - origin) @ axis for p in parts])
    return float(vals.min()), float(vals.max())


def corridor(scene: Scene, start: str, end: str, width: float, height: float):
    s, e = scene.o[start], scene.o[end]
    v = e.c - s.c
    if np.linalg.norm(v) < 1e-6:
        return None
    u = unit2(v)
    _, s_hi = _proj_extent(s.solids, s.c, u)
    e_lo, _ = _proj_extent(e.solids, e.c, u)
    p0 = s.c + u * s_hi
    p1 = e.c + u * e_lo
    length = float((p1 - p0) @ u)
    if length <= 0.05:
        return None
    mid = (p0 + p1) / 2
    x = np.array([u[0], 0.0, u[1]])
    y = np.array([0.0, 1.0, 0.0])
    rot = np.column_stack([x, y, np.cross(x, y)])
    return Box.make([mid[0], height / 2, mid[1]], rot, [length, height, width], tag='corridor')


def path_intrusion(scene: Scene, path: dict, min_pen=0.01, cell=0.1):
    """(kept, intruding object labels, intruding volume m^3). Start and end objects are excluded."""
    missing = scene.has(path['start'], path['end'])
    if missing:
        return False, [], math.nan, f'missing {missing}'
    box = corridor(scene, path['start'], path['end'], path['width'], path['height'])
    if box is None:
        return False, [], math.nan, 'start and end objects overlap: no path'
    intruders, parts = [], []
    for obj in scene.all:
        if obj.oid in (path['start'], path['end']):
            continue
        hit = [p for p in obj.solids if penetration_depth(box, p) > min_pen]
        if hit:
            intruders.append(obj.oid)
            parts.extend(hit)
    volume = 0.0
    if parts:
        n = np.maximum(np.round(2 * box.half / cell).astype(int), 1)
        g = [((np.arange(k) + 0.5) / k * 2 - 1) for k in n]
        gx, gy, gz = np.meshgrid(*g, indexing='ij')
        local = np.column_stack([gx.ravel(), gy.ravel(), gz.ravel()]) * box.half
        pts = box.center + local @ box.rot.T
        inside = np.zeros(len(pts), bool)
        for p in parts:
            inside |= p.contains(pts)
        volume = float(inside.sum() * np.prod(2 * box.half / n))
    return not intruders, intruders, volume, ''


def evaluate(rel: dict, scene: Scene, ref: dict | None = None):
    """Returns (passed: bool | None, values: dict). None = not computed (occlusion)."""
    t = rel['type']
    if t in NOT_COMPUTED:
        return None, {'note': 'needs an object-ID render from the front camera'}
    fn = RULES[t]
    return fn(rel, scene, ref or {})


def _missing(*ids_scene):
    *ids, scene = ids_scene
    m = scene.has(*ids)
    return (False, {'missing': ';'.join(m)}) if m else None


def r_facing(rel, s, ref):
    if (m := _missing(rel['obj'], rel['ref'], s)):
        return m
    o, r = s.o[rel['obj']], s.o[rel['ref']]
    to_ref = r.c - o.c
    if rel.get('mode') == 'axis_perp':
        dev = 90.0 - axis_angle_deg(_dir(o, 'axis'), to_ref)
    else:
        dev = angle_deg(o.front, to_ref)
    return dev <= rel['tol_deg'], {'facing_dev_deg': round(dev, 2)}


def r_distance(rel, s, ref):
    ids = sorted({i for p in rel['pairs'] for i in p})
    if (m := _missing(*ids, s)):
        return m
    vals = {f'd_{a}_{b}': float(np.linalg.norm(s.o[a].c - s.o[b].c)) for a, b in rel['pairs']}
    ok = all(abs(v - ref.get(k, v)) <= rel['tol'] for k, v in vals.items()) if ref else True
    return ok, {k: round(v, 3) for k, v in vals.items()}


def r_contact(rel, s, ref):
    if (m := _missing(rel['a'], rel['b'], s)):
        return m
    gap = min_gap(s.o[rel['a']].solids, s.o[rel['b']].solids)
    return (-rel['max_pen'] <= gap <= rel['max_gap']), {'gap_m': round(gap, 3)}


def r_clear_path(rel, s, ref):
    kept, intruders, vol, note = path_intrusion(s, rel)
    v = {'intruders': ';'.join(intruders), 'intruding_volume_m3': round(vol, 4) if vol == vol else ''}
    if note:
        v['note'] = note
    return kept, v


def r_alignment(rel, s, ref):
    if (m := _missing(rel['obj'], rel['ref'], s)):
        return m
    o, r = s.o[rel['obj']], s.o[rel['ref']]
    to_ref = r.c - o.c
    dev = angle_deg(o.front, to_ref)
    dev = min(dev, 180.0 - dev)
    offset = abs(float(to_ref @ o.side))
    return dev <= rel['tol_deg'] and offset <= rel['max_offset'], {'facing_dev_deg': round(dev, 2),
                                                                      'lateral_offset_m': round(offset, 3)}


def r_relative_size(rel, s, ref):
    if (m := _missing(rel['a'], rel['b'], s)):
        return m
    ratio = s.o[rel['a']].height / max(1e-6, s.o[rel['b']].height)
    ok = abs(ratio / ref['height_ratio'] - 1) <= rel['tol_rel'] if ref else True
    return ok, {'height_ratio': round(ratio, 3)}


def r_beside(rel, s, ref):
    if (m := _missing(rel['obj'], rel['ref'], s)):
        return m
    o, r = s.o[rel['obj']], s.o[rel['ref']]
    d = o.c - r.c
    lateral = float(abs(float(d @ r.side)) - r.width / 2)
    depth = abs(float(d @ r.front))
    in_path = False
    path = rel.get('path')
    if path and not s.has(path['start'], path['end']):
        box = corridor(s, path['start'], path['end'], path['width'], path['height'])
        in_path = box is not None and any(penetration_depth(box, p) > 0.01 for p in o.solids)
    ok = (abs(lateral - ref['lateral_m']) <= rel['tol'] if ref else True) and depth <= rel['max_depth'] and not in_path
    return ok, {'lateral_m': round(lateral, 3), 'depth_m': round(depth, 3), 'in_path': in_path}


def _support_top(lower, xz, y):
    tops = [p.y_max for p in lower.solids if p.contains_xz(xz)]
    if not tops:
        return None
    return min(tops, key=lambda top: abs(y - top))


def r_on_top(rel, s, ref):
    if (m := _missing(rel['upper'], rel['lower'], s)):
        return m
    up, lo = s.o[rel['upper']], s.o[rel['lower']]
    top = _support_top(lo, up.c, up.y0)
    if top is None:
        return False, {'note': 'upper object not above the lower one'}
    gap = up.y0 - top
    return abs(gap) <= rel['tol'], {'vertical_gap_m': round(gap, 3)}


def r_behind_parallel(rel, s, ref):
    if (m := _missing(rel['obj'], rel['ref'], s)):
        return m
    o, r = s.o[rel['obj']], s.o[rel['ref']]
    f = r.front
    behind = float((o.c - r.c) @ f) < 0
    dev = axis_angle_deg(_dir(o, 'axis'), r.side)
    ref_back, _ = _proj_extent(r.solids, r.c, f)
    _, obj_front = _proj_extent(o.solids, r.c, f)
    gap = ref_back - obj_front
    ok = behind and dev <= rel['tol_deg'] and (abs(gap - ref['gap_m']) <= rel['gap_tol'] if ref else True)
    return ok, {'behind': behind, 'parallel_dev_deg': round(dev, 2), 'gap_m': round(gap, 3)}


def r_distance_facing(rel, s, ref):
    if (m := _missing(rel['obj'], rel['ref'], s)):
        return m
    ok_f, vf = r_facing({**rel}, s, ref)
    d = float(np.linalg.norm(s.o[rel['obj']].c - s.o[rel['ref']].c))
    ok_d = abs(d - ref['distance_m']) <= rel['tol'] if ref else True
    return ok_f and ok_d, {**vf, 'distance_m': round(d, 3)}


def r_in_line(rel, s, ref):
    a, b = rel['ends']
    if (m := _missing(a, b, rel['middle'], s)):
        return m
    p, q, mid = s.o[a].c, s.o[b].c, s.o[rel['middle']].c
    u = unit2(q - p)
    dist = abs(float((mid - p) @ np.array([-u[1], u[0]])))
    return dist <= rel['tol'], {'off_line_m': round(dist, 3)}


def r_symmetry(rel, s, ref):
    p_id, q_id = rel['axis']
    if (m := _missing(rel['a'], rel['b'], p_id, q_id, s)):
        return m
    p, q = s.o[p_id].c, s.o[q_id].c
    u = unit2(q - p)
    mirror = np.outer(u, u) * 2 - np.eye(2)
    a, b = s.o[rel['a']], s.o[rel['b']]
    pos = mirror @ (a.c - p) + p
    err = float(np.linalg.norm(pos - b.c))
    ang = angle_deg(mirror @ a.front, b.front)
    return err <= rel['tol_pos'] and ang <= rel['tol_deg'], {'mirror_pos_err_m': round(err, 3),
                                                             'mirror_yaw_err_deg': round(ang, 2)}


def r_intersection(rel, s, ref):
    if (m := _missing(rel['pole'], rel['through'], s)):
        return m
    pole, thr = s.o[rel['pole']], s.o[rel['through']]
    inside = thr.box.contains_xz(pole.c)
    tops = [p.y_max for p in thr.solids if p.contains_xz(pole.c)]
    top = max(tops) if tops else thr.y1
    above = pole.y1 > top + rel['margin']
    below = pole.y0 < top - rel['margin']
    return inside and above and below, {'pole_inside_footprint': bool(inside), 'pole_above': bool(above),
                                        'pole_below': bool(below)}


RULES = {
    'facing': r_facing, 'distance': r_distance, 'contact': r_contact, 'clear_path': r_clear_path,
    'alignment': r_alignment, 'relative_size': r_relative_size, 'beside': r_beside, 'on_top': r_on_top,
    'behind_parallel': r_behind_parallel, 'distance_facing': r_distance_facing, 'in_line': r_in_line,
    'symmetry': r_symmetry, 'intersection': r_intersection,
}


def reference_values(spec: dict, target_scene: Scene) -> dict:
    """Numeric targets for each relation, measured on the target scene itself."""
    refs = {}
    for rel in spec['relations']:
        if rel['type'] in NOT_COMPUTED:
            continue
        ok, vals = evaluate(rel, target_scene, None)
        t = rel['type']
        if t == 'distance':
            refs[rel['id']] = vals
        elif t == 'relative_size':
            refs[rel['id']] = {'height_ratio': vals['height_ratio']}
        elif t == 'beside':
            refs[rel['id']] = {'lateral_m': float(vals['lateral_m'])}
        elif t == 'behind_parallel':
            refs[rel['id']] = {'gap_m': vals['gap_m']}
        elif t == 'distance_facing':
            refs[rel['id']] = {'distance_m': vals['distance_m']}
    return refs
