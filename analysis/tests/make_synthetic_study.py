"""Writes a fake Logs/SpatialGenerationStudy folder (n participants, both conditions, a few edits)
so the whole pipeline can be tried before real data exists. The numbers mean nothing.

Usage: python tests/make_synthetic_study.py --out demo_logs --n 12
"""
import argparse
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path[:0] = [str(ROOT), str(ROOT / 'tests')]

import numpy as np  # noqa: E402

import synth  # noqa: E402
from spatialscore.scene import load_target  # noqa: E402


def noisy_scene(spec, rng, pos_sd, yaw_sd, p_missing):
    objs = synth.target_to_objects(spec)
    objs = [o for o in objs if rng.random() > p_missing]
    for o in objs:
        synth.move(o, dx=rng.normal(0, pos_sd), dz=rng.normal(0, pos_sd))
        synth.rotate(o, rng.normal(0, yaw_sd))
        synth.scale(o, float(np.clip(rng.normal(1, 0.1), 0.7, 1.3)))
    return synth.transform_all(objs, yaw_deg=rng.uniform(-20, 20), shift=rng.normal(0, 1, 2))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--out', type=Path, default=Path('demo_logs'))
    ap.add_argument('--n', type=int, default=12)
    ap.add_argument('--seed', type=int, default=7)
    a = ap.parse_args()
    rng = np.random.default_rng(a.seed)
    specs = {s: load_target(s) for s in 'XY'}
    for i in range(1, a.n + 1):
        pid = f'P{i:02d}'
        first = 'A' if i % 2 else 'B'
        scenes = ('X', 'Y') if (i // 2) % 2 else ('Y', 'X')
        for k, cond in enumerate((first, 'B' if first == 'A' else 'A')):
            scene = scenes[k]
            d = a.out / pid / f'condition_{cond}'
            d.mkdir(parents=True, exist_ok=True)
            pos_sd, yaw_sd, miss = (0.35, 12, 0.03) if cond == 'B' else (0.8, 25, 0.08)
            objs = noisy_scene(specs[scene], rng, pos_sd, yaw_sd, miss)
            hour = 10 + k
            stamp = lambda m: f'202611{i:02d}_{hour:02d}{m:02d}00_000'  # noqa: E731
            utc = f'2026-11-{i:02d}T{hour:02d}:00:00Z'
            snap = synth.to_snapshot(objs, participant=pid, condition=cond, target=scene, label='accepted_run1', utc=utc)
            (d / f'{stamp(5)}_accepted_run1.json').write_text(json.dumps(snap))
            for e in (1, 2):
                before = synth.to_snapshot(objs, participant=pid, condition=cond, target=scene, label=f'edit{e}_before', utc=utc)
                victim = objs[int(rng.integers(len(objs)))]
                victim['colors'] = {0: '#%06x' % int(rng.integers(0xFFFFFF))}
                if cond == 'A' and rng.random() < 0.5:             # text-only edits sometimes spill over
                    synth.move(objs[int(rng.integers(len(objs)))], dx=0.2)
                after = synth.to_snapshot(objs, participant=pid, condition=cond, target=scene, label=f'edit{e}_after', utc=utc)
                (d / f'{stamp(10 + 2 * e)}_edit{e}_before.json').write_text(json.dumps(before))
                (d / f'{stamp(11 + 2 * e)}_edit{e}_after.json').write_text(json.dumps(after))
                victim_target = victim['id']
                (a.out / 'demo_coding.csv').open('a').write(f'{pid},{cond},{e},{victim_target}\n')
            final = synth.to_snapshot(objs, participant=pid, condition=cond, target=scene, label='final', utc=utc)
            (d / f'{stamp(40)}_final.json').write_text(json.dumps(final))
    coding = a.out / 'demo_coding.csv'
    coding.write_text('participant,condition,edit,intended_targets\n' + coding.read_text())
    print(f'Wrote {a.n} fake participants to {a.out}')


if __name__ == '__main__':
    main()
