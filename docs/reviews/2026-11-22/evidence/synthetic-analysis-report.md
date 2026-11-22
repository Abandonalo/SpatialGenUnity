# Section 5.1 analysis (accepted generation)

Participants with both conditions: **12**

## Relation satisfaction rate (H1)

| Condition | Mean | SD | Median |
|---|---|---|---|
| A | 0.250 | 0.099 | 0.214 |
| B | 0.530 | 0.160 | 0.500 |

Paired difference B - A: mean 0.280, bootstrap 95% CI [0.179, 0.381], Cohen's dz 1.52.

**Linear mixed model** `relation_rate ~ condition + order + target + (1 | participant)`

Condition B vs A: estimate 0.280, 95% CI [0.178, 0.381], p = 0.0000. Participant intercept variance 0.0011.

```
                   Mixed Linear Model Regression Results
============================================================================
Model:                   MixedLM      Dependent Variable:      relation_rate
No. Observations:        24           Method:                  REML         
No. Groups:              12           Scale:                   0.0161       
Min. group size:         2            Log-Likelihood:          7.9333       
Max. group size:         2            Converged:               Yes          
Mean group size:         2.0                                                
----------------------------------------------------------------------------
                                  Coef.  Std.Err.   z    P>|z| [0.025 0.975]
----------------------------------------------------------------------------
Intercept                          0.274    0.053  5.199 0.000  0.171  0.377
C(condition, Treatment('A'))[T.B]  0.280    0.052  5.403 0.000  0.178  0.381
C(order)[T.second]                 0.030    0.052  0.575 0.565 -0.072  0.131
C(target)[T.Y]                    -0.077    0.052 -1.495 0.135 -0.179  0.024
Group Var                          0.001    0.046                           
============================================================================

```

**Fallback** Wilcoxon signed-rank: p = 0.0015, rank-biserial r = 0.96 (Shapiro-Wilk on the differences: p = 0.565; use the fallback if the residuals are clearly non-normal or the rate sits at the ceiling).

**Robustness** logistic GEE on single relations: odds ratio B vs A 6.15, 95% CI [3.30, 11.44], p = 0.0000.

## Per relation (share of participants satisfying it)

| Target | Relation | A | B |
|---|---|---|---|
| X | R1 | 0.83 | 0.67 |
| X | R10 | 0.00 | 0.50 |
| X | R11 | 0.00 | 0.33 |
| X | R12 | 0.00 | 0.67 |
| X | R13 | 0.33 | 0.67 |
| X | R14 | 0.00 | 0.00 |
| X | R15 | 0.17 | 0.67 |
| X | R2 | 0.50 | 0.83 |
| X | R3 | 0.00 | 0.50 |
| X | R4 | 0.17 | 0.17 |
| X | R6 | 1.00 | 1.00 |
| X | R7 | 0.17 | 0.33 |
| X | R8 | 0.83 | 0.83 |
| X | R9 | 0.00 | 0.83 |
| Y | R1 | 0.50 | 0.83 |
| Y | R10 | 0.00 | 0.00 |
| Y | R11 | 0.17 | 0.17 |
| Y | R12 | 0.50 | 0.33 |
| Y | R13 | 0.17 | 0.67 |
| Y | R14 | 0.00 | 0.17 |
| Y | R15 | 0.00 | 0.50 |
| Y | R2 | 0.17 | 0.83 |
| Y | R3 | 0.00 | 0.50 |
| Y | R4 | 0.00 | 0.00 |
| Y | R6 | 0.50 | 1.00 |
| Y | R7 | 0.17 | 0.50 |
| Y | R8 | 0.83 | 0.67 |
| Y | R9 | 0.00 | 0.67 |

## Diagnostic measures (descriptive)

| Measure | A mean | B mean | Paired B - A (median) | n pairs |
|---|---|---|---|---|
| object_coverage | 0.902 | 0.985 | 0.091 | 12 |
| position_error_median_m | 0.832 | 0.341 | -0.576 | 12 |
| size_error_median | 0.074 | 0.056 | -0.024 | 12 |
| orientation_error_median_deg | 20.983 | 9.010 | -12.450 | 12 |
| clearance_kept | 0.750 | 1.000 | 0.000 | 12 |
| clearance_intruding_volume_m3 | 0.172 | 0.000 | 0.000 | 11 |
| extra_objects | 0.000 | 0.000 | 0.000 | 12 |

## Counterbalancing check

```
order      first  second
condition               
A              6       6
B              6       6

target     X  Y
condition      
A          6  6
B          6  6
```

![Paired relation satisfaction rates](paired_rates.png)
