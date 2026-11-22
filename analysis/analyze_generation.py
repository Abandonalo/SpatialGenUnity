"""Section 5.1 analysis: does condition B (spatial proxies) raise the relation satisfaction rate?

Reads generation_scores.csv and generation_relations.csv from score_generation.py and writes
report.md, analysis_participants.csv, analysis_relations.csv and paired_rates.png to --out.

Primary (H1): linear mixed model
    relation_rate ~ condition + order + target + (1 | participant)
on the accepted generation (snapshot 'accepted'), with the B - A difference reported with a
paired bootstrap 95% CI and Cohen's dz.
Fallback: Wilcoxon signed-rank on the paired rates, with the matched-pairs rank-biserial r.
Robustness: logistic GEE on the single relations (satisfied ~ condition + order + target +
relation, exchangeable within participant).
Secondary measures (coverage, errors, clearance) are described per condition with paired
differences; they are diagnostic and not tested.

Usage: python analyze_generation.py --scores results --out results/analysis [--snapshot accepted]
"""
from __future__ import annotations

import argparse
import warnings
from pathlib import Path

import numpy as np
import pandas as pd
from scipy import stats

SECONDARY = ['object_coverage', 'position_error_median_m', 'size_error_median', 'orientation_error_median_deg',
             'clearance_kept', 'clearance_intruding_volume_m3', 'extra_objects']


def paired_frame(df, col):
    w = df.pivot_table(index='participant', columns='condition', values=col, aggfunc='first')
    return w.dropna(subset=[c for c in ('A', 'B') if c in w.columns]) if {'A', 'B'} <= set(w.columns) else w.iloc[0:0]


def bootstrap_ci(diff, n=10000, seed=1):
    rng = np.random.default_rng(seed)
    if len(diff) < 2:
        return float('nan'), float('nan')
    means = rng.choice(diff, size=(n, len(diff)), replace=True).mean(axis=1)
    return float(np.percentile(means, 2.5)), float(np.percentile(means, 97.5))


def wilcoxon(diff):
    d = diff[diff != 0]
    if len(d) < 2:
        return float('nan'), float('nan')
    res = stats.wilcoxon(d)
    ranks = stats.rankdata(np.abs(d))
    r = (ranks[d > 0].sum() - ranks[d < 0].sum()) / ranks.sum()
    return float(res.pvalue), float(r)


def fit_lmm(df):
    import statsmodels.formula.api as smf
    with warnings.catch_warnings():
        warnings.simplefilter('ignore')
        terms = ["C(condition, Treatment('A'))"]
        if df['order'].nunique() > 1:
            terms.append('C(order)')
        if df['target'].nunique() > 1:
            terms.append('C(target)')
        model = smf.mixedlm('relation_rate ~ ' + ' + '.join(terms), df, groups=df['participant'])
        res, err = None, None
        for method in ('lbfgs', 'bfgs', 'powell', 'nm'):      # lbfgs fails when the participant variance is ~0
            try:
                res = model.fit(reml=True, method=method)
                break
            except Exception as e:  # noqa: BLE001
                err = e
        if res is None:
            raise err
    name = "C(condition, Treatment('A'))[T.B]"
    ci = res.conf_int().loc[name]
    return res, float(res.params[name]), float(ci[0]), float(ci[1]), float(res.pvalues[name])


def fit_gee(rel):
    import statsmodels.api as sm
    import statsmodels.formula.api as smf
    with warnings.catch_warnings():
        warnings.simplefilter('ignore')
        terms = ["C(condition, Treatment('A'))", 'C(relation)']
        if rel['order'].nunique() > 1:
            terms.append('C(order)')
        if rel['target'].nunique() > 1:
            terms.append('C(target)')
        model = smf.gee('satisfied ~ ' + ' + '.join(terms), 'participant', rel,
                        family=sm.families.Binomial(), cov_struct=sm.cov_struct.Exchangeable())
        res = model.fit()
    name = "C(condition, Treatment('A'))[T.B]"
    ci = res.conf_int().loc[name]
    return float(np.exp(res.params[name])), float(np.exp(ci[0])), float(np.exp(ci[1])), float(res.pvalues[name])


def plot(wide, path):
    import matplotlib
    matplotlib.use('Agg')
    import matplotlib.pyplot as plt
    fig, ax = plt.subplots(figsize=(4, 4.2), dpi=150)
    for _, r in wide.iterrows():
        ax.plot([0, 1], [r['A'], r['B']], color='#9aa4b1', lw=1, marker='o', ms=4)
    ax.plot([0, 1], [wide['A'].mean(), wide['B'].mean()], color='#1f4e79', lw=2.5, marker='o', ms=7, label='mean')
    ax.set_xticks([0, 1], ['A: text only', 'B: text + proxies'])
    ax.set_ylabel('Relation satisfaction rate')
    ax.set_ylim(-0.02, 1.02)
    ax.set_xlim(-0.3, 1.3)
    ax.spines[['top', 'right']].set_visible(False)
    ax.legend(frameon=False, loc='lower right')
    fig.tight_layout()
    fig.savefig(path)
    plt.close(fig)


def fmt(x, nd=3):
    return '' if x is None or (isinstance(x, float) and np.isnan(x)) else f'{x:.{nd}f}'


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--scores', type=Path, default=Path('results'), help='folder with score_generation output')
    ap.add_argument('--out', type=Path, default=None)
    ap.add_argument('--snapshot', default='accepted', help="'accepted' (H1) or 'final'")
    ap.add_argument('--bootstrap', type=int, default=10000)
    ap.add_argument('--seed', type=int, default=1)
    args = ap.parse_args(argv)
    out = args.out or args.scores / 'analysis'
    out.mkdir(parents=True, exist_ok=True)

    scores = pd.read_csv(args.scores / 'generation_scores.csv', dtype={'participant': str})
    rels = pd.read_csv(args.scores / 'generation_relations.csv', dtype={'participant': str})
    scores = scores[scores['snapshot'] == args.snapshot].copy()
    rels = rels[(rels['snapshot'] == args.snapshot) & rels['satisfied'].notna()].copy()
    rels['satisfied'] = rels['satisfied'].astype(int)
    rels = rels.merge(scores[['participant', 'condition', 'order']], on=['participant', 'condition'], how='left')

    wide = paired_frame(scores, 'relation_rate')
    complete = list(wide.index)
    dropped = sorted(set(scores['participant']) - set(complete))
    scores_c = scores[scores['participant'].isin(complete)]
    rels_c = rels[rels['participant'].isin(complete)]
    n = len(complete)
    lines = [f'# Section 5.1 analysis ({args.snapshot} generation)', '',
             f'Participants with both conditions: **{n}**' + (f' (left out, one condition missing: {", ".join(dropped)})'
                                                            if dropped else ''), '']
    if n == 0:
        (out / 'report.md').write_text('\n'.join(lines + ['No complete pairs, nothing to analyse.']))
        print('No complete participant pairs.')
        return

    diff = (wide['B'] - wide['A']).to_numpy()
    lo, hi = bootstrap_ci(diff, args.bootstrap, args.seed)
    sd = diff.std(ddof=1) if n > 1 else float('nan')
    dz = diff.mean() / sd if sd and sd > 0 else float('nan')
    p_w, r_rb = wilcoxon(diff)
    shapiro_p = float(stats.shapiro(diff).pvalue) if n >= 3 and np.ptp(diff) > 0 else float('nan')

    lines += ['## Relation satisfaction rate (H1)', '',
              '| Condition | Mean | SD | Median |', '|---|---|---|---|']
    for c in ('A', 'B'):
        v = wide[c]
        lines.append(f'| {c} | {fmt(v.mean())} | {fmt(v.std(ddof=1))} | {fmt(v.median())} |')
    lines += ['', f'Paired difference B - A: mean {fmt(diff.mean())}, bootstrap 95% CI [{fmt(lo)}, {fmt(hi)}], '
              f'Cohen\'s dz {fmt(dz, 2)}.', '']

    try:
        res, b, b_lo, b_hi, p = fit_lmm(scores_c)
        lines += ['**Linear mixed model** `relation_rate ~ condition + order + target + (1 | participant)`', '',
                  f'Condition B vs A: estimate {fmt(b)}, 95% CI [{fmt(b_lo)}, {fmt(b_hi)}], p = {fmt(p, 4)}. '
                  f'Participant intercept variance {fmt(float(res.cov_re.iloc[0, 0]), 4)}'
                  + (' (near zero: the model reduces to ordinary regression; report the paired test alongside).'
                     if float(res.cov_re.iloc[0, 0]) < 1e-3 else '.'), '',
                  '```', str(res.summary()), '```', '']
    except Exception as e:  # noqa: BLE001 - report and continue with the fallback
        lines += [f'Linear mixed model could not be fitted ({e}); use the Wilcoxon result below.', '']

    lines += [f'**Fallback** Wilcoxon signed-rank: p = {fmt(p_w, 4)}, rank-biserial r = {fmt(r_rb, 2)} '
              f'(Shapiro-Wilk on the differences: p = {fmt(shapiro_p, 3)}; use the fallback if the residuals are '
              'clearly non-normal or the rate sits at the ceiling).', '']

    try:
        orr, o_lo, o_hi, p_g = fit_gee(rels_c)
        lines += ['**Robustness** logistic GEE on single relations: odds ratio B vs A '
                  f'{fmt(orr, 2)}, 95% CI [{fmt(o_lo, 2)}, {fmt(o_hi, 2)}], p = {fmt(p_g, 4)}.', '']
    except Exception as e:  # noqa: BLE001
        lines += [f'GEE could not be fitted ({e}).', '']

    per_rel = rels_c.pivot_table(index=['target', 'relation'], columns='condition', values='satisfied', aggfunc='mean')
    per_rel = per_rel.reset_index()
    per_rel.to_csv(out / 'analysis_relations.csv', index=False)
    lines += ['## Per relation (share of participants satisfying it)', '',
              '| Target | Relation | A | B |', '|---|---|---|---|']
    for _, r in per_rel.iterrows():
        lines.append(f"| {r['target']} | {r['relation']} | {fmt(r.get('A'), 2)} | {fmt(r.get('B'), 2)} |")
    lines.append('')

    lines += ['## Diagnostic measures (descriptive)', '',
              '| Measure | A mean | B mean | Paired B - A (median) | n pairs |', '|---|---|---|---|---|']
    for col in SECONDARY:
        if col not in scores_c:
            continue
        w = paired_frame(scores_c.assign(**{col: pd.to_numeric(scores_c[col], errors='coerce')}), col)
        if w.empty:
            continue
        d = (w['B'] - w['A']).dropna()
        lines.append(f'| {col} | {fmt(w["A"].mean())} | {fmt(w["B"].mean())} | {fmt(d.median())} | {len(d)} |')
    lines.append('')

    order_check = scores_c.groupby(['condition', 'order']).size().unstack(fill_value=0)
    target_check = scores_c.groupby(['condition', 'target']).size().unstack(fill_value=0)
    lines += ['## Counterbalancing check', '', '```', str(order_check), '', str(target_check), '```', '']

    part = wide.rename(columns={'A': 'rate_A', 'B': 'rate_B'})
    part['diff_B_minus_A'] = part['rate_B'] - part['rate_A']
    part.to_csv(out / 'analysis_participants.csv')
    plot(wide, out / 'paired_rates.png')
    lines += ['![Paired relation satisfaction rates](paired_rates.png)', '']
    (out / 'report.md').write_text('\n'.join(lines))
    print(f'Analysis of {n} participant(s) -> {out / "report.md"}')


if __name__ == '__main__':
    main()
