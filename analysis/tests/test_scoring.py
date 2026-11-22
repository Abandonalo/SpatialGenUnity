"""Checks the scoring on scenes built from the target specs.  Run: python -m pytest tests  (or python tests/test_scoring.py)"""
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path[:0] = [str(ROOT), str(ROOT / 'tests')]

import numpy as np  # noqa: E402

import synth  # noqa: E402
from score_edits import score_edit  # noqa: E402
from spatialscore.measures import TargetContext, score_snapshot  # noqa: E402
from spatialscore.geometry import quat_to_matrix  # noqa: E402
from spatialscore.scene import load_target, target_objects  # noqa: E402

CTX = {s: TargetContext(load_target(s)) for s in 'XY'}


def score(objs, scene='X', **kw):
    summary, rels, objects, matches = score_snapshot(synth.to_snapshot(objs, target=scene, **kw), CTX[scene])
    return summary, {r['relation']: r['satisfied'] for r in rels}, {o['object']: o for o in objects}


def get(objs, oid):
    return next(o for o in objs if o['id'] == oid)


def test_target_scores_perfect():
    for s in 'XY':
        summary, rel, _ = score(synth.target_to_objects(CTX[s].spec), s)
        assert summary['relations_satisfied'] == 14 and summary['relations_computed'] == 14
        assert summary['object_coverage'] == 1.0 and summary['position_error_median_m'] == 0.0
        assert rel['R5'] == ''


def test_global_shift_and_rotation_is_aligned_away():
    for s in 'XY':
        objs = synth.transform_all(synth.target_to_objects(CTX[s].spec), yaw_deg=37, shift=(4.0, -2.5))
        summary, rel, _ = score(objs, s)
        assert summary['relations_satisfied'] == 14, rel
        assert summary['position_error_median_m'] < 1e-6
        assert summary['orientation_error_median_deg'] < 1e-3


def test_rotated_stall_fails_facing():
    objs = synth.target_to_objects(CTX['X'].spec)
    synth.rotate(get(objs, 'stall_a'), 45)
    _, rel, ob = score(objs)
    assert rel['R1'] == 0
    assert abs(ob['stall_a']['orientation_error_deg'] - 45) < 0.5


def test_missing_object_lowers_coverage():
    objs = [o for o in synth.target_to_objects(CTX['X'].spec) if o['id'] != 'barrel']
    summary, rel, ob = score(objs)
    assert summary['objects_present'] == 10 and not ob['barrel']['present']
    assert rel['R8'] == 0


def test_object_in_path_breaks_clearance():
    objs = synth.target_to_objects(CTX['X'].spec)
    sign = get(objs, 'signpost')
    synth.move(sign, dx=-sign['origin'][0] + 0.3, dz=-sign['origin'][2] + 4.0)   # onto the bench-gate path
    summary, rel, _ = score(objs)
    assert summary['clearance_kept'] == 0 and 'signpost' in summary['clearance_intruders']
    assert rel['R6'] == 0


def test_floating_and_scaled():
    objs = synth.target_to_objects(CTX['X'].spec)
    synth.move(get(objs, 'sack'), dy=0.3)
    synth.scale(get(objs, 'barrel'), 1.6)
    _, rel, ob = score(objs)
    assert rel['R10'] == 0 and abs(ob['sack']['vertical_error_m'] - 0.3) < 1e-6
    assert ob['barrel']['size_error'] > 0.5


def test_condition_a_matches_by_asset_label():
    objs = synth.target_to_objects(CTX['Y'].spec)
    summary, _, _ = score(objs, 'Y', condition='A', with_proxies=False)
    assert summary['objects_present'] == 11 and summary['relations_satisfied'] == 14


def _edit_pair(change):
    spec = CTX['X'].spec
    objs = synth.target_to_objects(spec)
    before = synth.to_snapshot(objs, label='edit1_before')
    after_objs = synth.clone(objs)
    change(after_objs)
    after = synth.to_snapshot(after_objs, label='edit1_after')
    return before, after, spec, target_objects(spec)


def test_edit_confined_to_target_is_clean():
    def change(objs):
        o = get(objs, 'barrel')
        o['colors'] = {0: '#aa3300'}
        synth.scale(o, 1.1)
    s, _ = score_edit(*_edit_pair(change), ['barrel'])
    assert s['out_of_target_change'] == 0 and s['out_of_target_area_share'] == 0
    assert s['changed_parts_in_target'] == 1 and s['boundary_integrity'] == 'intact'


def test_edit_that_moves_other_objects():
    def change(objs):
        synth.move(get(objs, 'bench'), dx=0.5)
        synth.move(get(objs, 'well'), dz=0.04)
    s, rows = score_edit(*_edit_pair(change), ['bench'])
    assert s['out_of_target_change'] == 1 and s['outside_parts_changed'] == 1
    assert 0 < s['out_of_target_area_share'] < 1
    assert rows[0]['object'] == 'well' and 'moved' in rows[0]['change']


def test_boundary_broken_when_top_crate_lifted():
    def change(objs):
        c = get(objs, 'crates')
        c['parts'][1] = (c['parts'][1][0] + np.array([0, 0.2, 0]), c['parts'][1][1])
    s, _ = score_edit(*_edit_pair(change), ['crates'])
    assert s['boundary_integrity'] == 'broken' and s['boundary_floating_parts'] == 1


def _bench_halves(snap):
    """The bench's only part and the two halves of it along its width, as SceneSnapshot writes them."""
    asset = next(a for a in snap['assets'] if a['label'] == 'bench')
    part = asset['parts'][0]
    c = np.array(part['center_world'])
    width_axis = quat_to_matrix(part['rotation_world'])[:, 2]
    size = np.array(part['size_world'])
    half_size = list(map(float, size * [1, 1, 0.5]))
    halves = [list(map(float, c + sign * width_axis * size[2] / 4)) for sign in (1, -1)]
    return asset, part, halves, half_size


def _clip(part, center, size):
    part['geometry_center_world'], part['geometry_size_world'] = center, size
    part['pieces'] = [{'center_world': center, 'size_world': size}]


def test_partial_removal_is_a_change_in_the_target():
    """Box-drag edit that removes half a part: the part keeps its transform, only its pieces shrink."""
    before, after, spec, targets = _edit_pair(lambda objs: None)
    _, part, halves, half_size = _bench_halves(after)
    _clip(part, halves[1], half_size)
    s, _ = score_edit(before, after, spec, targets, ['bench'])
    assert s['changed_parts_in_target'] == 1, s
    assert s['out_of_target_change'] == 0


def test_partial_update_remainder_is_preserved_material():
    """Box-drag edit that recolours half a part: the selected half keeps the part id and changes,
    the other half becomes a new remainder part that must not count as new geometry."""
    before, after, spec, targets = _edit_pair(lambda objs: None)
    asset, part, halves, half_size = _bench_halves(after)
    remainder = dict(part, part_id=part['part_id'] + '_rest', source_part_id=part['part_id'])
    _clip(remainder, halves[1], half_size)
    part.update(center_world=halves[0], size_world=half_size, color='#cc2222')
    _clip(part, halves[0], half_size)
    asset['parts'].append(remainder)
    s, _ = score_edit(before, after, spec, targets, ['bench'])
    assert s['changed_parts_in_target'] == 1, s
    assert s['boundary_integrity'] == 'intact', s
    assert s['out_of_target_change'] == 0


def test_remainder_outside_its_source_is_a_change():
    """A part claiming to be a remainder but lying outside its source is new geometry."""
    before, after, spec, targets = _edit_pair(lambda objs: None)
    asset, part, halves, half_size = _bench_halves(after)
    far = list(map(float, np.array(halves[1]) + [0, 2.0, 0]))
    stray = dict(part, part_id=part['part_id'] + '_rest', source_part_id=part['part_id'], center_world=far)
    _clip(stray, far, half_size)
    asset['parts'].append(stray)
    s, _ = score_edit(before, after, spec, targets, ['bench'])
    assert s['changed_parts_in_target'] == 1, s


def test_latest_of_after_and_undone_is_scored():
    """Apply, Ctrl+Z, Ctrl+Y: the edit's result is the newest after/undone snapshot."""
    import tempfile
    from score_edits import find_edits
    with tempfile.TemporaryDirectory() as tmp:
        cdir = Path(tmp) / 'P01' / 'condition_B'
        cdir.mkdir(parents=True)
        names = ['20261122_100000_000_edit1_before', '20261122_100100_000_edit1_after',
                 '20261122_100200_000_edit1_undone']
        for n in names:
            (cdir / f'{n}.json').write_text('{}')
        (_, _, _, files), = find_edits(Path(tmp))
        assert files['result'].name.endswith('_undone.json')
        (cdir / '20261122_100300_000_edit1_after.json').write_text('{}')
        (_, _, _, files), = find_edits(Path(tmp))
        assert files['result'].name == '20261122_100300_000_edit1_after.json'


if __name__ == '__main__':
    fails = 0
    for name, fn in list(globals().items()):
        if name.startswith('test_'):
            try:
                fn()
                print('ok  ', name)
            except Exception as e:  # noqa: BLE001
                fails += 1
                print('FAIL', name, repr(e))
    sys.exit(1 if fails else 0)
