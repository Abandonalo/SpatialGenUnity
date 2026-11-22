# Scoring and analysis scripts (study design, sections 5.1 and 5.2)

These scripts read the snapshots that the Spatial Generation window writes to
`Logs/SpatialGenerationStudy/<participant>/condition_<A|B>/` and compute the generation and
refinement measures. Developer-mode sessions (`dev`) are skipped unless you pass `--include-dev`.

```
pip install -r requirements.txt
python make_targets.py                                    # once: writes targets/target_X.json, target_Y.json
python score_generation.py --logs ../Logs/SpatialGenerationStudy --out results
python score_edits.py      --logs ../Logs/SpatialGenerationStudy --interaction-logs ../Logs/SpatialGenerationLogs --out results
#   -> fill results/edit_coding_template.csv (intended_targets), save as edit_coding.csv, then:
python score_edits.py      --logs ../Logs/SpatialGenerationStudy --coding edit_coding.csv --out results
python analyze_generation.py --scores results            # -> results/analysis/report.md
```

To try everything on fake data first: `python tests/make_synthetic_study.py --out demo_logs`, then run
the same commands with `--logs demo_logs` (the demo coding file is `demo_logs/demo_coding.csv`).
`python tests/test_scoring.py` checks the scoring on scenes built from the targets.

## Section 5.1 (`score_generation.py`)

Scored snapshots: the latest `accepted_run*` (used for H1) and `final`.

1. **Match.** Each generated asset is assigned to a target object by name (proxy label in B, asset
   label in A, compared with the target object's names) and position (Hungarian assignment).
   `generation_matches.csv` flags matches made by position only or far away (`check` column).
   Corrections go in an overrides CSV: `participant, condition, snapshot, asset_id, target_id`
   (`snapshot` = `accepted` or `final`; empty `target_id` = not a target object).
2. **Align.** A 2D rigid transform (Kabsch, rotation about the vertical axis plus a ground-plane shift)
   is fitted on the matched object centres. The offset itself is not reported.
3. **Measures** (`generation_scores.csv`):
   - `object_coverage` = matched target objects / 11; `extra_objects` = unmatched generated assets.
   - `position_error_median_m` (aligned centre distance), `size_error_median` (mean relative error of
     the object's dimensions), `orientation_error_median_deg` (front or long axis after alignment; round
     objects have none; 180-degree symmetric objects count either way). Present objects only.
     Per-object values, including `vertical_error_m` for objects that sit on another, are in
     `generation_objects.csv`.
   - `clearance_kept` / `clearance_intruders` / `clearance_intruding_volume_m3`: the walkway corridor
     (1.5 m wide, 2 m high) between the path's start and end object must hold no geometry. Unmatched
     assets count as intruders too.
   - `relation_rate` = satisfied / 14 relations. **R5 (occlusion) is not computed**; it needs an
     object-ID render from the front camera, so its row in `generation_relations.csv` stays empty.
     Rules with a numeric target (distances, gaps, height ratio, lateral offset) are compared with the
     same quantity measured on the target scene; tolerances are in `make_targets.py` and should be
     frozen after the pilot.
   - `order` (first/second) is taken from the snapshot time stamps.

## Section 5.1 analysis (`analyze_generation.py`)

Linear mixed model `relation_rate ~ condition + order + target + (1 | participant)` on the accepted
generation, paired bootstrap 95% CI and Cohen's dz for B - A, Wilcoxon signed-rank with rank-biserial r
as the fallback, and a logistic GEE on the single relations as robustness check. Diagnostic measures are
described per condition only. `--snapshot final` runs the same on the final scenes.

## Section 5.2 (`score_edits.py`)

Each edit is `edit<n>_before` vs `edit<n>_after` (or `_undone`). The intended target comes from the
coders (`edit_coding.csv`; `unclear` edits are left out). Objects are matched on the before snapshot.

- **Out-of-target change**: every part of every asset outside the intended target is compared by part id
  (moved > 1 cm, rotated > 1 deg, resized > 1 %, recoloured, reshaped, removed, added).
  `out_of_target_change` (0/1), `out_of_target_part_share`, and `out_of_target_area_share` (share of
  the outside surface that moved more than 2 cm, sampled every 3 cm in both directions). What changed is
  listed in `edit_parts.csv`.
- **Boundary integrity** between the changed geometry and what was kept: a kept part that touched the
  replaced geometry must still touch the new one (gap > 2 cm counts), new penetrations deeper than 2 cm,
  and parts the edit left floating. `intact` (all <= 2 cm), `minor` (<= 5 cm), `broken` (> 5 cm or a
  floating part), or `no_boundary` when nothing inside the target changed.

Thresholds are constants at the top of `score_edits.py`.

## Not covered yet

- R5 occlusion (needs a render with object IDs).
- Intended change achieved (5.2) stays a coder judgement.
- Proxy moves and rotations are not in the interaction log, so they are not scored.
