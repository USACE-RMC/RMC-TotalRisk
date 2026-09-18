# Sensitivity Analysis

> Technical reference for the unified sensitivity engine on `RMC.TotalRisk.Analyses.Risk.RiskAnalysis`:
> `MeasureSensitivity`, `MeasureSensitivityMatrix`, and `HazardLevelSensitivity`, the
> `SensitivityMeasure` association catalog, and the `SensitivityResults`/`SensitivityEntry`
> containers. v1.1 replaces the v1.0 tornado analysis with this engine by approved scope decision.
> Executable evidence: [../verification/sensitivity.md](../verification/sensitivity.md).
> Methodology grounding: the RMC-TotalRisk Technical Reference Manual, Appendix G.
> The value-of-information surface rides the same knowledge columns and ranks them in the
> measure's own units: [value-of-information.md](value-of-information.md).

## The design in one sentence

Sensitivity is a **post-processing diagnostic over stored results**: the per-realization outputs the
run already persisted are correlated against the per-function knowledge percentile draws, re-derived
bit-exactly from the content seeds — no re-simulation, no integration, and no serialized state of
its own (`SensitivityResults` is runtime-only, never persisted).

Appendix G of the report [7] develops four sensitivity formulations — the derivative index
`(μ_θ/μ_f)·(∂f/∂θ)`, the first-order variance-propagation share, the regression variance share
`SIᵢ = (σ²_θᵢ/σ²_y)·βᵢ²`, and the correlation coefficient. The engine's Monte Carlo measures are
the sample-based members of that family: the Pearson and rank correlations, and the squared
correlation as the sensitivity index — for a single regressor exactly the regression variance
share. The tornado plot is the consuming layer's presentation of the ranked indices.

## The three entry points

| Method | Output being explained | Where the outputs come from |
|---|---|---|
| `MeasureSensitivity(outputMeasure, riskType, measure, componentIndex, failureModeIndex, consequenceType)` | One stored scalar risk measure (APF, mean, conditional mean, σ, skewness, kurtosis, threshold probabilities, VaR, CVaR) on one risk-type stream | The per-realization summaries persisted in `RiskResults` |
| `MeasureSensitivityMatrix(riskType, measure, …)` | The full scalar-measure catalog in one pass | Same — one input-matrix derivation is reused across all measures |
| `HazardLevelSensitivity(componentIndex, hazardLevel, measure, riskType, realizations, consequenceType)` | The risk at one hazard level (the tornado diagnostic) | A dedicated content-seeded design (default 100 realizations) that evaluates the failure-mode combination decomposition at the level — one evaluation per realization |

Scope arguments select the output: `componentIndex = −1` explains the overall system (inputs are
every component's knowledge columns); a component position narrows the inputs to that component's
columns (other components' draws are independent of its results by construction);
`failureModeIndex` narrows further to one failure mode's summaries (Excess and Fail streams only —
any other stream throws `ArgumentException`). `consequenceType` selects the declared
consequence-type position (0 is the primary). The measure methods return null when no
full-uncertainty results are stored, the scope's outputs are unavailable, fewer than three valid
realization pairs remain after NaN filtering, or the scope has no knowledge inputs;
`HazardLevelSensitivity` additionally returns null for an invalid analysis (the v1.0 contract) or a
deterministic component.

## `SensitivityMeasure` — the association catalog

Runtime-only (never serialized, no hash surface); the v1.0 member names are preserved:

| Member | Definition | Reading |
|---|---|---|
| `PearsonCorrelation` | Pearson's linear correlation between input draws and output | linear association |
| `SpearmanCorrelation` | Pearson over fractional ranks | exact under any monotone re-expression of the input; preferred for curvilinear monotone relations |
| `SensitivityIndex` | squared Pearson correlation | the input's fractional contribution to output variance — because the inputs are independent per-function knowledge draws (near-orthogonal under Latin hypercube stratification), r² estimates the same main-effect variance share as the regression form of the Technical Reference Manual Appendix G (Eq. 249–250), and the indices sum to at most one across inputs |
| `FirstOrderSobol` | given-data first-order Sobol index (Plischke): the variance of the conditional output mean across equal-frequency input bins over the output variance | the main-effect variance share without the linearity assumption of `SensitivityIndex` — sees nonlinear but not interaction effects |
| `PawnMedian` | the PAWN median: the median across input bins of the Kolmogorov–Smirnov distance between conditional and unconditional output distributions | moment-independent — sensitive to tail and shape effects a variance share cannot see |
| `BorgonovoDelta` | Borgonovo's δ via the double-histogram total-variation estimator over rank classes | moment-independent and exactly invariant under monotone re-expression of the input |

The three given-data members route through the Numerics `GlobalSensitivity` estimators over the
same stored realizations and columns, under the documented 20-bin equal-frequency convention
(shared with the [value-of-information](value-of-information.md) conditioning). They need at
least as many valid realization pairs as bins — a smaller sample returns null rather than a
noise-dominated result — and they carry the given-data biases of order 1/bins² (discretization)
and bins/n (bin noise). The estimators themselves are verified upstream against the analytic
Ishigami and Sobol-g decompositions; the engine's plumbing is pinned bit-exactly against direct
upstream calls and against the linear-map closed form below.

## The input-column contract

Input columns are collected by the **exact sampler walk** the engine samples with, so labels,
ordering, and dimensionality match the run:

- Every non-deterministic function with `SamplingDimensions > 0` contributes one column per
  sampling dimension, labeled `"{Component} - {Function}"` (multi-dimensional functions gain a
  `[d]` suffix; duplicate labels dedupe with an ordinal). Deterministic functions are skipped —
  their pre-allocated percentile rows are never read, and an inert column would only add tornado
  noise bars.
- A **shared function instance contributes one column** (reference-identity dedup): one live
  function wired into several places is one knowledge quantity.
- Each failure mode contributes its **consequence-coupling columns** — the shared
  failure/non-failure coupling draw, one per declared consequence type — but only where an
  uncertain consequence actually consumes them.
- The walk order is: the hazard function, then per mode the coupling columns, the stage
  transforms, the stage responses, and the trailing transforms.

## The seed-rederivation contract

The correlation pairs stored outputs with re-derived inputs **through the content seeds**: the
component sampler setup is re-run at the stored ensemble size (`SetupSamplers`), reproducing the
run's percentile matrices bit-exactly because seeds derive from (analysis seed, component canonical
hash, occurrence index) — never a wall clock. Two consequences:

1. **The model must be unchanged since the run** — the same guarantee every diagnostic over stored
   results carries. An edited function has a different content hash, therefore different seeds,
   therefore columns that no longer pair with the stored outputs.
2. **Sampler state is mutated as a documented side effect.** `HazardLevelSensitivity` re-runs
   `SetupSamplers` at its own design size, so a caller reading sampled functions directly after a
   sensitivity call sees the sensitivity design; `RunAsync` and the measure methods re-seed
   defensively at their own entry.

`HazardLevelSensitivity` preserves the legacy hazard-bin weighting (the sampled hazard's
probability mass over ±(range/200) around the level, tail masses at the domain ends) and is native
to the component's selected profile hazard axis: when a profile element is set, each realization
inverts its own sampled profile chain back to the driving hazard, which requires every transform on
the chain to declare an ordered output axis (an unordered declaration faults the query loudly
rather than inverting ambiguously).

## Results containers

`SensitivityResults` (runtime-only) carries `OutputLabel` (e.g. `"Mean — Total — System"`),
`RiskType`, `Measure`, `Realizations` (the valid pair count), and `Entries` — one
`SensitivityEntry { Label, Value }` per input column in the sampler walk order.
`RankedByMagnitude()` returns the tornado view (descending absolute association). Nothing here is
serialized; the diagnostic recomputes on demand from stored results.

## Tree node importance

`TreeNodeImportance.Compute(response, options)` in `RiskFunctions.Responses.Trees` is the
node-level importance sweep for both tree responses: one overload takes an `EventTreeResponse`
(one entry per expanded non-root occurrence) and one takes a `FaultTreeResponse` (one entry per
unified basic-event variable, so shared-logical repetition contributes a single entry).
`TreeNodeImportanceOptions` carries one authored hazard level — it must exactly equal a level on
the response's axis — with `Iterations` defaulting to 1000 and `Seed` defaulting to 12345.

Two deterministic Monte Carlo passes run **read-only against the published immutable compiled
plan** — no clone, no mutation of live sampler state, and the canonical hash is untouched:

1. **The joint pass** varies every uncertain source together. Per iteration it records the
   aggregate `P(F|h)` and, per entry, the effective (post-normalization/residual) conditional
   probability and — for event occurrences — the absolute path probability; a fault variable's
   recorded probability is its sampled event probability.
2. **The one-at-a-time pass** holds every source at its mean and varies exactly one entry per
   evaluation.

Each pass consumes one uniform draw per uncertain entry per iteration, in canonical entry order,
from an independent Mersenne Twister stream seeded by
`ToPositiveSeed(HashCombine(Seed, CanonicalHash(), passIndex))`; a shared fault variable draws
once per iteration no matter how many occurrences reference it. Statistics come from Numerics:
the entry's five-number summary describes its recorded per-node probability (path probability
for event occurrences, sampled probability for fault variables), the Pearson coefficient
correlates the effective conditional probability with the aggregate (`NaN` when either series is
constant), and the first-order index is `Var[aggregate | only entry i varying] /
Var[aggregate | all varying]` — zero for deterministic sources, `NaN` when the joint aggregate
variance is zero. `TreeNodeImportanceResult` echoes the inputs and carries the aggregate
five-number summary and variance beside the entries. Same options, same tree content →
bit-identical results.

## Verification

[../verification/sensitivity.md](../verification/sensitivity.md): the analytic
corr(U, Φ⁻¹(U)) = √(3/π) Pearson pin at the Fisher-z bound, Spearman rank exactness with the
inert-input 4/√N null band, an independent hazard-level response oracle with affine invariance,
content-seeded bit-reproducibility, and the profile-axis-native pin (the tornado at profile level
T(h) is bit-identical to the raw-axis tornado at h on the unprofiled clone).
[../verification/fault-tree.md](../verification/fault-tree.md) adds the tree node-importance
oracles: the affine event tree with exact `a·σ/√(Σa²σ²)` Pearson and `a²σ²/Σa²σ²` variance-ratio
forms, and the from-scratch BCL-random reimplementation for a shared-event fault tree with
determinism and live-state-inertness pins.
