"""Writes targets/target_X.json and targets/target_Y.json from the target scene layouts.

The positions follow the LAYOUT constants and placement formulas in targets/src/market.js and
targets/src/camp.js (three.js). three.js is right-handed and Unity is left-handed, so every
z coordinate and every z component of a direction is negated here: the scenes look the same in
both engines, and the scoring scripts work in Unity coordinates.

Sizes are the footprint and height of each modelled object. Orientation modes:
  front  the object has a front (local +X of a generated asset); size = [depth along front, height, width]
  axis   only the long horizontal axis matters (crates, cart, logs ...); size = [long, height, short]
  round  no orientation (well, barrel, posts ...); size = [diameter, height, diameter]
Run again after changing a layout:  python make_targets.py
"""
import json
import math
from pathlib import Path


def face_to(x, z, tx, tz):
    """three.js rotation.y that turns local +Z towards (tx, tz)."""
    return math.atan2(tx - x, tz - z)


def local_xz(x, z, rot, lx, lz):
    c, s = math.cos(rot), math.sin(rot)
    return x + lx * c + lz * s, z - lx * s + lz * c


def three_local_z(rot):
    return math.sin(rot), math.cos(rot)


def three_local_x(rot):
    return math.cos(rot), -math.sin(rot)


def U(xz):
    """three.js (x, z) -> Unity (x, z)."""
    return [round(xz[0], 4), round(-xz[1], 4)]


def obj(oid, names, center, size, mode, direction=None, y0=0.0, symmetry='none', parts=None):
    o = {'id': oid, 'names': names, 'center': U(center), 'y0': y0, 'size': size, 'mode': mode, 'symmetry': symmetry}
    if direction is not None:
        o['direction'] = U(direction)
    if parts:
        o['parts'] = parts
    return o


def scene_x():
    well = (0.0, 0.0)
    stall_a, stall_b = (-3.38, -0.91), (3.38, -0.91)
    rot_a = face_to(*stall_a, *well)
    rot_b = face_to(*stall_b, *well)
    crates = local_xz(*stall_a, rot_a, -1.5, 0.05)
    sack = local_xz(*stall_a, rot_a, -1.5, 0.36)
    cart = local_xz(*stall_a, rot_a, 0.0, -1.7)
    objects = [
        obj('well', ['well', 'fountain'], well, [2.0, 3.0, 2.0], 'round'),
        obj('stall_a', ['stall a', 'stalla', 'left stall', 'fruit stall', 'red stall', 'stall 1', 'stall1'], stall_a,
            [1.5, 2.6, 2.4], 'front', three_local_z(rot_a)),
        obj('stall_b', ['stall b', 'stallb', 'right stall', 'bread stall', 'blue stall', 'stall 2', 'stall2'], stall_b,
            [1.5, 2.6, 2.4], 'front', three_local_z(rot_b)),
        obj('crates', ['crate', 'crates', 'box stack', 'boxes'], crates, [1.22, 1.1, 0.6], 'axis', three_local_z(rot_a),
            symmetry='180', parts=[{'center': [0.0, 0.3, 0.0], 'size': [1.22, 0.6, 0.6]},
                                   {'center': [-0.33, 0.85, 0.0], 'size': [0.5, 0.5, 0.5]}]),
        obj('sack', ['sack', 'bag', 'grain sack'], sack, [0.4, 0.47, 0.4], 'round', y0=0.6),
        obj('cart', ['cart', 'wagon', 'handcart'], cart, [2.6, 1.0, 1.1], 'axis', three_local_x(rot_a), symmetry='180'),
        obj('parasol', ['parasol', 'umbrella', 'sunshade'], cart, [1.5, 3.2, 1.5], 'round'),
        obj('barrel', ['barrel', 'keg'], (3.38, 0.95), [0.44, 0.5, 0.44], 'round'),
        obj('bench', ['bench', 'seat'], (0.0, 2.5), [0.48, 0.9, 1.5], 'front', (0.0, -1.0)),
        obj('gate', ['gate', 'gateway', 'entrance'], (0.0, -6.0), [0.64, 3.0, 3.1], 'front', (0.0, 1.0), symmetry='180'),
        obj('signpost', ['signpost', 'sign post', 'sign', 'post'], (2.5, -5.45), [0.2, 2.4, 0.2], 'round'),
    ]
    path = {'start': 'well', 'end': 'gate', 'width': 1.5, 'height': 2.0}
    relations = [
        {'id': 'R1', 'type': 'facing', 'obj': 'stall_a', 'ref': 'well', 'mode': 'front', 'tol_deg': 20},
        {'id': 'R2', 'type': 'facing', 'obj': 'stall_b', 'ref': 'well', 'mode': 'front', 'tol_deg': 20},
        {'id': 'R3', 'type': 'distance', 'pairs': [['stall_a', 'well'], ['stall_b', 'well']], 'tol': 0.5},
        {'id': 'R4', 'type': 'contact', 'a': 'crates', 'b': 'stall_a', 'max_gap': 0.05, 'max_pen': 0.05},
        {'id': 'R5', 'type': 'occlusion', 'occluder': 'barrel', 'occluded': 'stall_b', 'range': [0.2, 0.8]},
        {'id': 'R6', 'type': 'clear_path', **path},
        {'id': 'R7', 'type': 'alignment', 'obj': 'gate', 'ref': 'well', 'max_offset': 0.5, 'tol_deg': 20},
        {'id': 'R8', 'type': 'relative_size', 'a': 'barrel', 'b': 'stall_b', 'tol_rel': 0.25},
        {'id': 'R9', 'type': 'beside', 'obj': 'signpost', 'ref': 'gate', 'tol': 0.4, 'max_depth': 1.0, 'path': path},
        {'id': 'R10', 'type': 'on_top', 'upper': 'sack', 'lower': 'crates', 'tol': 0.05},
        {'id': 'R11', 'type': 'behind_parallel', 'obj': 'cart', 'ref': 'stall_a', 'tol_deg': 15, 'gap_tol': 0.25},
        {'id': 'R12', 'type': 'distance_facing', 'obj': 'bench', 'ref': 'well', 'mode': 'front', 'tol': 0.5, 'tol_deg': 20},
        {'id': 'R13', 'type': 'in_line', 'ends': ['bench', 'gate'], 'middle': 'well', 'tol': 0.3},
        {'id': 'R14', 'type': 'symmetry', 'a': 'stall_a', 'b': 'stall_b', 'axis': ['well', 'gate'], 'tol_pos': 0.5, 'tol_deg': 20},
        {'id': 'R15', 'type': 'intersection', 'pole': 'parasol', 'through': 'cart', 'margin': 0.05},
    ]
    return {'scene': 'X', 'name': 'Market square', 'objects': objects, 'path': path, 'relations': relations}


def scene_y():
    fire = (0.0, 0.0)
    tent_a, tent_b = (0.91, -3.38), (0.91, 3.38)
    rot_a = face_to(*tent_a, *fire)
    rot_b = face_to(*tent_b, *fire)
    logs = local_xz(*tent_a, rot_a, -(1.1 + 0.39), 0.2)
    table = local_xz(*tent_a, rot_a, 0.0, -(1.3 + 0.5 + 0.35))
    cooler_rot = 0.18
    objects = [
        obj('campfire', ['campfire', 'fire', 'fireplace', 'fire pit', 'firepit'], fire, [1.4, 0.6, 1.4], 'round'),
        obj('tent_a', ['tent a', 'tenta', 'green tent', 'tent 1', 'tent1', 'back tent'], tent_a, [2.6, 1.5, 2.2], 'front',
            three_local_z(rot_a)),
        obj('tent_b', ['tent b', 'tentb', 'orange tent', 'tent 2', 'tent2', 'front tent'], tent_b, [2.6, 1.5, 2.2], 'front',
            three_local_z(rot_b)),
        obj('logs', ['logs', 'log pile', 'firewood', 'woodpile', 'wood pile'], logs, [1.15, 0.71, 0.78], 'axis',
            three_local_z(rot_a), symmetry='180'),
        obj('bucket', ['bucket', 'pail'], logs, [0.3, 0.26, 0.3], 'round', y0=0.71),
        obj('table', ['table', 'camp table'], table, [1.4, 0.75, 0.7], 'axis', three_local_x(rot_a), symmetry='180'),
        obj('parasol', ['parasol', 'umbrella', 'sunshade'], table, [1.8, 2.45, 1.8], 'round'),
        obj('cooler', ['cooler', 'cool box', 'coolbox', 'ice box', 'icebox'], (1.3, 5.32), [0.65, 0.53, 0.41], 'axis',
            three_local_x(cooler_rot), symmetry='180'),
        obj('log_seat', ['log seat', 'logseat', 'seat', 'bench', 'log bench'], (-2.5, 0.0), [1.5, 0.62, 0.42], 'axis',
            (0.0, 1.0), symmetry='180'),
        obj('arch', ['trail arch', 'arch', 'archway', 'trail gate', 'gate'], (6.0, 0.0), [0.3, 2.9, 3.0], 'front',
            (-1.0, 0.0), symmetry='180'),
        obj('lantern', ['lantern', 'lantern post', 'lamp', 'lamp post', 'post'], (6.0, 2.3), [0.5, 2.1, 0.5], 'round'),
    ]
    path = {'start': 'campfire', 'end': 'arch', 'width': 1.5, 'height': 2.0}
    relations = [
        {'id': 'R1', 'type': 'facing', 'obj': 'tent_a', 'ref': 'campfire', 'mode': 'front', 'tol_deg': 20},
        {'id': 'R2', 'type': 'facing', 'obj': 'tent_b', 'ref': 'campfire', 'mode': 'front', 'tol_deg': 20},
        {'id': 'R3', 'type': 'distance', 'pairs': [['tent_a', 'campfire'], ['tent_b', 'campfire']], 'tol': 0.5},
        {'id': 'R4', 'type': 'contact', 'a': 'logs', 'b': 'tent_a', 'max_gap': 0.05, 'max_pen': 0.05},
        {'id': 'R5', 'type': 'occlusion', 'occluder': 'cooler', 'occluded': 'tent_b', 'range': [0.2, 0.8]},
        {'id': 'R6', 'type': 'clear_path', **path},
        {'id': 'R7', 'type': 'alignment', 'obj': 'arch', 'ref': 'campfire', 'max_offset': 0.5, 'tol_deg': 20},
        {'id': 'R8', 'type': 'relative_size', 'a': 'cooler', 'b': 'tent_b', 'tol_rel': 0.25},
        {'id': 'R9', 'type': 'beside', 'obj': 'lantern', 'ref': 'arch', 'tol': 0.4, 'max_depth': 1.0, 'path': path},
        {'id': 'R10', 'type': 'on_top', 'upper': 'bucket', 'lower': 'logs', 'tol': 0.05},
        {'id': 'R11', 'type': 'behind_parallel', 'obj': 'table', 'ref': 'tent_a', 'tol_deg': 15, 'gap_tol': 0.25},
        {'id': 'R12', 'type': 'distance_facing', 'obj': 'log_seat', 'ref': 'campfire', 'mode': 'axis_perp', 'tol': 0.5,
         'tol_deg': 20},
        {'id': 'R13', 'type': 'in_line', 'ends': ['log_seat', 'arch'], 'middle': 'campfire', 'tol': 0.3},
        {'id': 'R14', 'type': 'symmetry', 'a': 'tent_a', 'b': 'tent_b', 'axis': ['campfire', 'arch'], 'tol_pos': 0.5,
         'tol_deg': 20},
        {'id': 'R15', 'type': 'intersection', 'pole': 'parasol', 'through': 'table', 'margin': 0.05},
    ]
    return {'scene': 'Y', 'name': 'Campsite', 'objects': objects, 'path': path, 'relations': relations}


if __name__ == '__main__':
    out = Path(__file__).parent / 'targets'
    out.mkdir(exist_ok=True)
    for spec in (scene_x(), scene_y()):
        p = out / f"target_{spec['scene']}.json"
        p.write_text(json.dumps(spec, indent=2))
        print('wrote', p, len(spec['objects']), 'objects', len(spec['relations']), 'relations')
