"""Scene objects from snapshots and target specs, object matching and scene alignment."""
from __future__ import annotations

import json
import re
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np
from scipy.optimize import linear_sum_assignment

from .geometry import Box, kabsch_2d, quat_to_matrix, solids, unit2

TARGET_DIR = Path(__file__).resolve().parent.parent / 'targets'


@dataclass
class SceneObject:
    """One object reduced to what the measures need."""
    oid: str
    label: str
    box: Box                     # object box in the object's own frame
    parts: list                  # list[Box], one per part identity (tag = part id)
    asset_id: str = ''
    proxy_id: str = ''
    meta: dict = field(default_factory=dict)

    @property
    def solids(self) -> list:
        """Geometry of all parts, with clipped parts split into their pieces."""
        return solids(self.parts)

    @property
    def c(self) -> np.ndarray:
        return self.box.center[[0, 2]]

    @property
    def front(self) -> np.ndarray:
        return unit2(self.box.rot[[0, 2], 0])

    @property
    def side(self) -> np.ndarray:
        return unit2(self.box.rot[[0, 2], 2])

    @property
    def y0(self) -> float:
        return min(p.y_min for p in self.solids) if self.parts else self.box.y_min

    @property
    def y1(self) -> float:
        return max(p.y_max for p in self.solids) if self.parts else self.box.y_max

    @property
    def height(self) -> float:
        return self.y1 - self.y0

    @property
    def depth(self) -> float:
        return 2 * self.box.half[0]

    @property
    def width(self) -> float:
        return 2 * self.box.half[2]

    def oriented(self, mode: str):
        """(direction, dims) as the given orientation mode reads the object.

        front: direction = front, dims = [depth, height, width]
        axis:  direction = longer horizontal axis, dims = [long, height, short]
        round: direction = None, dims = [diameter, height]
        """
        if mode == 'front':
            return self.front, np.array([self.depth, self.height, self.width])
        if mode == 'axis':
            if self.depth >= self.width:
                return self.front, np.array([self.depth, self.height, self.width])
            return self.side, np.array([self.width, self.height, self.depth])
        return None, np.array([max(self.depth, self.width), self.height])


# ----------------------------------------------------------------------------- names

def norm_name(s: str) -> str:
    return re.sub(r'[^a-z0-9]+', '', (s or '').lower())


def name_matches(label: str, aliases) -> bool:
    n = norm_name(label)
    if len(n) < 2:
        return False
    for a in aliases:
        a = norm_name(a)
        if a and (a == n or (len(a) >= 3 and a in n) or (len(n) >= 4 and n in a)):
            return True
    return False


# ----------------------------------------------------------------------------- targets

def load_target(scene: str) -> dict:
    p = TARGET_DIR / f'target_{scene}.json'
    if not p.exists():
        raise FileNotFoundError(f'No target spec {p}. Run make_targets.py first.')
    return json.loads(p.read_text())


def target_scene_id(value: str) -> str:
    n = norm_name(value)
    if n in ('x', 'scenex', 'targetx') or 'market' in n:
        return 'X'
    if n in ('y', 'sceney', 'targety') or 'camp' in n:
        return 'Y'
    raise ValueError(f"Cannot tell which target scene '{value}' is. Use X or Y, or pass --target.")


def frame_from_direction(direction) -> np.ndarray:
    """Rotation whose local X is the horizontal direction, local Y is up (det +1)."""
    d = unit2(direction)
    x = np.array([d[0], 0.0, d[1]])
    y = np.array([0.0, 1.0, 0.0])
    z = np.cross(x, y)
    return np.column_stack([x, y, z])


def target_objects(spec: dict) -> dict:
    out = {}
    for o in spec['objects']:
        direction = o.get('direction', [1.0, 0.0])
        rot = frame_from_direction(direction)
        size = o['size']
        dims = np.array([size[0], size[1], size[2] if len(size) > 2 else size[0]], float)
        center = np.array([o['center'][0], o['y0'] + dims[1] / 2, o['center'][1]])
        box = Box.make(center, rot, dims, tag=o['id'])
        parts = []
        if o.get('parts'):
            for i, pt in enumerate(o['parts']):
                local = np.array(pt['center'], float)
                world = np.array([o['center'][0], o['y0'], o['center'][1]]) + rot @ local
                parts.append(Box.make(world, rot, pt['size'], tag=f"{o['id']}:{i}", owner=o['id']))
        else:
            parts.append(Box.make(center, rot, dims, tag=f"{o['id']}:0", owner=o['id']))
        out[o['id']] = SceneObject(o['id'], o['names'][0], box, parts, meta={'mode': o['mode'], 'symmetry': o['symmetry']})
    return out


# ----------------------------------------------------------------------------- snapshots

def load_snapshot(path) -> dict:
    return json.loads(Path(path).read_text())


def snapshot_part(p: dict, asset_id: str) -> Box:
    """One part. A part clipped by a partial region edit (SceneSnapshot `pieces`) is reduced to the
    tight box around its remaining geometry, and keeps the pieces for geometric measures."""
    rot = quat_to_matrix(p['rotation_world'])
    pid = p.get('part_id', '')
    pieces = [Box.make(q['center_world'], rot, q['size_world'], tag=f'{pid}#{k}', owner=asset_id)
              for k, q in enumerate(p.get('pieces') or []) if q.get('size_world')]
    center = p.get('geometry_center_world') or p['center_world']
    size = p.get('geometry_size_world') or p['size_world']
    return Box.make(center, rot, size, tag=pid, owner=asset_id, shape=p.get('shape', ''),
                    color=p.get('color', ''), segments=p.get('segments', 0), name=p.get('name', ''),
                    pieces=pieces, source=p.get('source_part_id') or '')


def snapshot_objects(snap: dict) -> list:
    """Generated assets as SceneObjects (oid = asset id until matched). Empty assets are skipped."""
    proxies = {p.get('proxy_id'): p for p in snap.get('proxies') or []}
    objs = []
    for a in snap.get('assets') or []:
        parts = [snapshot_part(p, a.get('asset_id', '')) for p in a.get('parts') or []]
        if not parts:
            continue
        rot = quat_to_matrix(a['rotation_world'])
        origin = np.array(a['origin_world'], float)
        local = np.vstack([(b.corners() - origin) @ rot for b in solids(parts)])
        lo, hi = local.min(0), local.max(0)
        box = Box.make(origin + rot @ ((lo + hi) / 2), rot, hi - lo, tag=a.get('asset_id', ''))
        proxy = proxies.get(a.get('proxy_id') or '')
        label = (proxy or {}).get('label') or a.get('label') or ''
        objs.append(SceneObject(a.get('asset_id', ''), label, box, parts, asset_id=a.get('asset_id', ''),
                                proxy_id=a.get('proxy_id') or '', meta={'asset_label': a.get('label', '')}))
    return objs


# ----------------------------------------------------------------------------- matching and alignment

@dataclass
class Matching:
    matched: dict            # target id -> SceneObject (oid replaced by the target id)
    extras: list             # unmatched generated SceneObjects
    rows: list               # one dict per generated object, for review
    rot: np.ndarray          # scene alignment: aligned = rot @ c + t
    t: np.ndarray

    def align_point(self, xz) -> np.ndarray:
        return self.rot @ np.asarray(xz, float) + self.t

    def align_dir(self, v) -> np.ndarray:
        return self.rot @ np.asarray(v, float)


def _rigid(pairs):
    if len(pairs) >= 2:
        return kabsch_2d(np.array([p[0] for p in pairs]), np.array([p[1] for p in pairs]))
    return np.eye(2), np.zeros(2)


def match_objects(gen: list, spec: dict, targets: dict, overrides: dict | None = None,
                  max_unlabelled_dist=2.0) -> Matching:
    """Assign generated objects to target objects.

    1. Names: the proxy label (condition B) or the asset label (condition A) is compared with
       each target object's names.
    2. A provisional alignment is fitted on the unambiguous name matches.
    3. Hungarian assignment on aligned centre distance, with a penalty when the names do not
       agree; a pair with no name agreement further than max_unlabelled_dist apart is not allowed.
    4. Overrides from a coder ({asset_id: target_id or None}) replace the result.
    Rows flagged 'position_only' or 'far' are the ones a coder should check.
    """
    overrides = overrides or {}
    tids = list(targets)
    aliases = {o['id']: o['names'] + [o['id']] for o in spec['objects']}
    compat = np.array([[name_matches(g.label, aliases[t]) for t in tids] for g in gen], bool) \
        if gen else np.zeros((0, len(tids)), bool)

    unique = []
    for i, g in enumerate(gen):
        js = np.flatnonzero(compat[i])
        if len(js) == 1 and compat[:, js[0]].sum() == 1:
            unique.append((g.c, targets[tids[js[0]]].c))
    r0, t0 = _rigid(unique)

    assign = {}
    if gen:
        cost = np.zeros((len(gen), len(tids)))
        for i, g in enumerate(gen):
            p = r0 @ g.c + t0
            for j, t in enumerate(tids):
                d = float(np.linalg.norm(p - targets[t].c))
                if compat[i, j]:
                    cost[i, j] = d
                elif d <= max_unlabelled_dist:
                    cost[i, j] = d + 3.0
                else:
                    cost[i, j] = 1e6
        rows_i, cols_j = linear_sum_assignment(cost)
        for i, j in zip(rows_i, cols_j):
            if cost[i, j] < 1e6:
                assign[i] = (tids[j], 'name' if compat[i, j] else 'position_only', float(cost[i, j]))

    by_asset = {g.asset_id: i for i, g in enumerate(gen)}
    for asset_id, tid in overrides.items():
        if asset_id not in by_asset:
            continue
        i = by_asset[asset_id]
        for k in [k for k, v in assign.items() if tid and v[0] == tid]:
            del assign[k]
        if tid:
            assign[i] = (tid, 'override', 0.0)
        else:
            assign.pop(i, None)

    pairs = [(gen[i].c, targets[tid].c) for i, (tid, _, _) in assign.items()]
    rot, t = _rigid(pairs)

    matched, extras, rows = {}, [], []
    for i, g in enumerate(gen):
        if i in assign:
            tid, how, _ = assign[i]
            dist = float(np.linalg.norm(rot @ g.c + t - targets[tid].c))
            flag = how if how in ('position_only', 'override') else ('far' if dist > max_unlabelled_dist else '')
            m = SceneObject(tid, g.label, g.box, g.parts, g.asset_id, g.proxy_id, dict(g.meta))
            matched[tid] = m
            rows.append({'asset_id': g.asset_id, 'label': g.label, 'target_id': tid, 'matched_by': how,
                         'aligned_distance_m': round(dist, 3), 'check': flag})
        else:
            extras.append(g)
            rows.append({'asset_id': g.asset_id, 'label': g.label, 'target_id': '', 'matched_by': 'none',
                         'aligned_distance_m': '', 'check': 'unmatched'})
    return Matching(matched, extras, rows, rot, t)


def load_overrides(path) -> dict:
    """CSV with participant, condition, snapshot, asset_id, target_id ('' or 'none' = unmatched)."""
    import csv
    out = {}
    if not path:
        return out
    with open(path, newline='') as f:
        for r in csv.DictReader(f):
            key = (r['participant'], r['condition'], r['snapshot'])
            tid = (r.get('target_id') or '').strip()
            out.setdefault(key, {})[r['asset_id']] = None if tid.lower() in ('', 'none') else tid
    return out
