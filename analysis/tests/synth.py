"""Builds snapshot JSON in the SceneSnapshot.cs format from a target spec, with optional changes.

Used by the tests and by make_synthetic_study.py; not needed for real data.
"""
from __future__ import annotations

import copy
import math
import uuid

import numpy as np

from spatialscore.scene import frame_from_direction


def matrix_to_quat(m):
    m = np.asarray(m, float)
    tr = m[0, 0] + m[1, 1] + m[2, 2]
    if tr > 0:
        s = math.sqrt(tr + 1.0) * 2
        w, x, y, z = 0.25 * s, (m[2, 1] - m[1, 2]) / s, (m[0, 2] - m[2, 0]) / s, (m[1, 0] - m[0, 1]) / s
    elif m[0, 0] > m[1, 1] and m[0, 0] > m[2, 2]:
        s = math.sqrt(1.0 + m[0, 0] - m[1, 1] - m[2, 2]) * 2
        w, x, y, z = (m[2, 1] - m[1, 2]) / s, 0.25 * s, (m[0, 1] + m[1, 0]) / s, (m[0, 2] + m[2, 0]) / s
    elif m[1, 1] > m[2, 2]:
        s = math.sqrt(1.0 + m[1, 1] - m[0, 0] - m[2, 2]) * 2
        w, x, y, z = (m[0, 2] - m[2, 0]) / s, (m[0, 1] + m[1, 0]) / s, 0.25 * s, (m[1, 2] + m[2, 1]) / s
    else:
        s = math.sqrt(1.0 + m[2, 2] - m[0, 0] - m[1, 1]) * 2
        w, x, y, z = (m[1, 0] - m[0, 1]) / s, (m[0, 2] + m[2, 0]) / s, (m[1, 2] + m[2, 1]) / s, 0.25 * s
    return [x, y, z, w]


def yaw_matrix(deg):
    """Rotation about +Y by deg that rotates the 2D (x, z) plane counter-clockwise."""
    a = math.radians(deg)
    c, s = math.cos(a), math.sin(a)
    # maps (x, z) -> (c x - s z, s x + c z), the same sense as geometry.rot2
    return np.array([[c, 0, -s], [0, 1, 0], [s, 0, c]])


def target_to_objects(spec):
    """Editable object list: id, label, center(3), rot(3x3), parts [(center3, size3)]."""
    out = []
    for o in spec['objects']:
        rot = frame_from_direction(o.get('direction', [1.0, 0.0]))
        size = o['size']
        dims = np.array([size[0], size[1], size[2] if len(size) > 2 else size[0]], float)
        base = np.array([o['center'][0], o['y0'], o['center'][1]], float)
        parts = []
        if o.get('parts'):
            for p in o['parts']:
                parts.append((base + rot @ np.array(p['center'], float), np.array(p['size'], float)))
        else:
            parts.append((base + np.array([0, dims[1] / 2, 0]), dims))
        out.append({'id': o['id'], 'label': o['names'][0], 'origin': base, 'rot': rot, 'parts': parts})
    return out


def transform_all(objs, yaw_deg=0.0, shift=(0.0, 0.0)):
    r = yaw_matrix(yaw_deg)
    t = np.array([shift[0], 0.0, shift[1]])
    for o in objs:
        o['origin'] = r @ o['origin'] + t
        o['rot'] = r @ o['rot']
        o['parts'] = [(r @ c + t, s) for c, s in o['parts']]
    return objs


def move(obj, dx=0.0, dz=0.0, dy=0.0):
    d = np.array([dx, dy, dz])
    obj['origin'] = obj['origin'] + d
    obj['parts'] = [(c + d, s) for c, s in obj['parts']]


def rotate(obj, yaw_deg):
    r = yaw_matrix(yaw_deg)
    o = obj['origin']
    obj['rot'] = r @ obj['rot']
    obj['parts'] = [(o + r @ (c - o), s) for c, s in obj['parts']]


def scale(obj, k):
    o = obj['origin']
    obj['parts'] = [(o + (c - o) * np.array([k, k, k]), s * k) for c, s in obj['parts']]


def to_snapshot(objs, *, participant='P00', condition='B', target='X', label='accepted_run1', utc='2026-11-20T10:00:00Z',
                with_proxies=True, part_ids=None):
    assets, proxies = [], []
    for o in objs:
        aid = o.get('asset_id') or uuid.uuid5(uuid.NAMESPACE_DNS, f"{participant}{condition}{o['id']}").hex[:8]
        pid = 'p_' + aid if with_proxies else ''
        q = matrix_to_quat(o['rot'])
        parts = []
        for i, (c, s) in enumerate(o['parts']):
            part_id = (part_ids or {}).get((o['id'], i)) or f"{aid}_{i}"
            parts.append({'part_id': part_id, 'name': f"part_{i}", 'shape': o.get('shapes', {}).get(i, 'Box'), 'segments': 8,
                          'color': o.get('colors', {}).get(i, '#888888'), 'center_world': list(map(float, c)),
                          'rotation_world': q, 'size_world': list(map(float, s))})
        assets.append({'asset_id': aid, 'label': o['label'], 'prompt': '', 'proxy_id': pid,
                       'origin_world': list(map(float, o['origin'])), 'rotation_world': q, 'volume_size': [1, 1, 1],
                       'parts': parts})
        if with_proxies:
            proxies.append({'proxy_id': pid, 'label': o['label'], 'prompt': '', 'role': 'Occupy',
                            'center_world': list(map(float, o['origin'])), 'rotation_world': q, 'size_world': [1, 1, 1]})
    return {'label': label, 'utc': utc, 'participant': participant, 'condition': condition, 'target': target,
            'phase': 'Refinement', 'run': 1, 'assets': assets, 'proxies': proxies}


def clone(objs):
    return copy.deepcopy(objs)
