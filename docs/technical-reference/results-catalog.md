# The Results Catalog

> Technical reference for `RMC.TotalRisk.Results.{Risk, CostBenefit, LifeCycle}`: the realization and summary trees, the risk
> profiles, ensemble summaries and convergence diagnostics, the multi-consequence axis,
> contribution, reliability-mode outputs, and the results-JSON conventions. Normative spec:
> [../requirements/MODEL_LIBRARY_ARCHITECTURE.md](../requirements/MODEL_LIBRARY_ARCHITECTURE.md)
> §7.5. Companions: [loss-exceedance-curves.md](loss-exceedance-curves.md) (how each curve is
> built) and [risk-contribution.md](risk-contribution.md) (the attribution mathematics).

## Two trees: full realizations and compact summaries

The engine publishes results on two parallel shapes:

- **The realization tree** — `SystemRealization` → `ComponentRealization` →
  `FailureModeRealization`. Each scope carries a `Curves` block (the five `Curve` streams:
  `Excess`, `Background`, `Total`, `Fail`, `NonFail`), `AdditionalCurves` (one `Curves` block per
  additional declared consequence type), and — at the failure-mode scope — the opt-in
  `AdjustedCurves`/`AdditionalAdjustedCurves` (the adjusted marginal streams, default off) and the
  per-type contributions. During a run the realizations live in the runtime-only `Ensemble`
  store, written strictly index-owned by the parallel loop (the discipline behind bit-identical
  results at any thread count), then post-processed and discarded.
- **The summary tree** — `SystemRiskResults` → `SummaryRiskResults` (the five stream summaries) +
  `ComponentResults` → `FailureModeResults`, plus `ConsequenceResults` under
  `AdditionalConsequences` for each additional type, echoing the declared `ConsequenceLabels` /
  `ConsequenceUnits`, and the integrator diagnostics (`FunctionEvaluations`, `StandardError`,
  `ChiSquared`). One `SystemRiskResults` summarizes one finished realization.

The persisted roots on `RiskAnalysis` after a run:

| Property | Content |
|---|---|
| `RiskResults` (`EnsembleResults`) | One `SystemRiskResults` per realization (a mean-only run publishes a single-entry ensemble), plus the run `Manifest` and the ensemble `Summary` |
| `MeanRiskResults` | The mean realization (mean-only run) or the ensemble mean curves (full run) — a `SystemRealization` curve tree |
| `MedianRiskResults`, `LowerRiskResults`, `UpperRiskResults` | The ensemble median and confidence-bound curve trees at the configured interval width (full runs only) |

Percentile band trees are assembled per scope, stream, and consequence type on per-type
consequence grids (types live on different magnitude scales); the Total percentile curve is read
from the Total LEC directly.

## The `Curve` measure and profile surface

Every `Curve` carries the risk-measure catalog of
[loss-exceedance-curves.md](loss-exceedance-curves.md) (`TotalProbability`, `Mean`,
`ConditionalMean`, `StandardDeviation`, `Skewness`, `Kurtosis`, `ConsequenceThresholdProbability`,
`HazardThresholdProbability`, `ValueAtRisk`, `ConditionalValueAtRisk`) plus the profile catalog:

| Profile | Content |
|---|---|
| `HazardFrequency` | hazard level vs cumulative exceedance probability |
| `CumulativeFailureProbabilities` | the ascending cumulate of recorded probability mass — the distribution of the failure-causing hazard; its terminal ordinate is the annualized failure probability |
| `CumulativeExpectedConsequences` | the ascending cumulate of expected consequence; terminal ≡ the stream mean |
| `SystemResponseProbabilities` | the response profile, plotted against annual exceedance probability (the normalized, transform-independent axis — a hazard-axis response profile is ill-posed when failure modes respond to different transformed signals) |

`[JsonIgnore]` normalized views expose the cumulative profiles as `OrderedPairedData`. When a
component selects a profile hazard element, the profile hazard coordinates are remapped through
the selected transform chain (a seed-inert selection; the response profile's exceedance axis is
deliberately never remapped). Optional measures are gated by the `[Flags]` `RiskMeasureOptions`
(`HigherMoments`, `ValueAtRisk`, `ThresholdProbabilities`, `RiskProfiles`, `Contributions`;
default `All`) — a disabled group is simply not computed, never partially populated.

## Ensemble summaries and convergence diagnostics

`EnsembleResults.Summary` (`EnsembleSummary`) reduces the per-realization scalars into four
`SystemRiskResults` trees — Lower, Upper, Median, Mean — whose every scalar (the measure catalog
at system, component, failure-mode, and consequence-type scope, the three contribution values,
and the integrator diagnostics) is the ensemble percentile or sequential mean of that measure.
`ComputeSummary(confidenceIntervalWidth)` recomputes at another width from the stored ensemble.
The aggregated `ConvergenceDiagnostics` carry the integrator effort/error summaries
(function-evaluation and standard-error statistics) and the realization-adequacy indicators
(SD/√N and confidence half-widths for the headline scalars). Reductions are sequential and
deterministic — repeated runs and JSON round-trips reproduce every slot bit-for-bit. When the
ensemble carries realization weights (below), every measure reduction is weighted and the
summary records the effective sample size; a null weight vector reduces exactly as before.

On the joint (VEGAS) method, one semantic deserves naming: the stored per-realization
`ChiSquared` — and therefore the summary's chi-squared aggregates — describes the **warm-up**
pass (the importance grid's adaptation consistency); the integrator recomputes its chi-squared
for the recording passes that produce the published curves, and the engine historically
discarded it. `RiskAnalysis.JointCertificate` (`JointConvergenceCertificate`, runtime-only —
never serialized, absent on the additive and one-dimensional paths) now keeps those
recording-pass values: the per-degree-of-freedom chi-squared consistency and the relative
standard error, as ensemble mean/max aggregates plus the mean-pass slots on a mean-only run
(the full-uncertainty pass assembles its published mean from percentiles and integrates no
separate mean pass).

## Epistemic realization weights

`EnsembleResults.RealizationWeights` is the optional per-realization epistemic weight vector —
the single authoritative copy, parallel to the realization slots. A weight states the relative
credibility of one realization's knowledge state (reliability semantics: only relative values
carry meaning; weights are stored raw and normalized inside the reductions, never in storage).
Null means every realization carries equal weight, and every reduction then follows the
unweighted path unchanged.

Two write paths exist. `RiskAnalysis.RealizationWeights` supplies weights as a **run input**
(runtime-only state — never serialized with the analysis, never hashed, never an influence on
sampling seeds): the run validates the vector (full-uncertainty mode only; length equal to the
realization count; finite, non-negative, not all zero), weights the percentile band assembly
and the summary reduction, stamps the vector into the stored ensemble, and records its SHA-256
fingerprint as `AnalysisRunManifest.RealizationWeightsHash`.
`EnsembleResults.SetRealizationWeights` annotates a **finished** result set (the write path for
likelihood re-weighting and scenario credibility): the scalar summary is then recomputable through
`ComputeSummary`, while the persisted band curves keep their run-time weighting — the full
per-realization curves they reduce are not retained, so re-banding requires a rerun with the
weights as input. Post-run annotation never touches the manifest: it records run provenance,
and that run was truthfully unweighted.

What reduces weighted: the four summary trees' entire measure catalog (weighted mean; symmetric
weighted percentiles — zero-weight realizations carry no mass), the contribution values, the
percentile band curves and profile bands of a weighted run, and the convergence indicators
(weighted mean with standard error √(V/N_eff), the Kish effective size N_eff = (Σw)²/Σw²
reported as `EnsembleSummary.EffectiveRealizationCount`). The integrator effort/error
aggregates stay unweighted — they describe computational effort actually spent. Weights never
move a sampled realization: a weighted run draws bit-identically the realizations the
unweighted run draws, and only the reductions over them change.

A stored payload whose weight vector fails the integrity checks loads **without throwing**:
the results are cleared (realizations, summary, and weights; the manifest is kept for
identification), the failure is recorded in the runtime-only `EnsembleResults.LoadDiagnostics`,
and an analysis restored over the cleared container reports unestimated — the analysis must be
rerun and its results saved again.

## Retained realizations (runtime-only)

Two opt-in diagnostic surfaces on the analysis expose state the run normally releases, neither
of them serialized, hashed, or able to move a published byte: `RetainRealizations` keeps the
full per-realization ensemble on `RetainedRealizations` and enables
`ReassemblePercentileBands(weights)` — the post-hoc weighted re-band whose output is
byte-identical to a run that carried those weights as its input — and
`RetainedIntegrationDetailIndex` keeps one selected realization's recorded risk-point ledger on
`RetainedIntegrationDetail` (−1 selects the mean-only pass). Both are cleared and replaced by
each run; validation warns about the memory cost while retention is on. The mechanics live in
[uncertainty-analysis §6.3](uncertainty-analysis.md).

## Tolerable-risk confidence

`EnsembleSummary.TolerableRiskConfidence` carries the evaluated epistemic guideline statements —
one `TolerableRiskConfidence` entry per `TolerableRiskCriterion` configured on the analysis
options, in declared order: the criterion echo (measure and stream by enum name, the
consequence-type position, the threshold) and P(measure > threshold), the realization-weight
fraction strictly exceeding the threshold ([uncertainty-analysis.md
§6.2](uncertainty-analysis.md)). The block is null-suppressed — absent when no criteria are
configured — and populated by the full-uncertainty run; a bare `ComputeSummary` over a loaded
ensemble produces a summary without it, while
`RiskAnalysis.ComputeTolerableRiskConfidence()` re-evaluates the configured criteria over the
stored ensemble (including post-run weights) without re-simulation.

## Exposure-period and life-cycle conversions (runtime-only)

`RiskAnalysis.MeasureExposurePeriodRisk(periodYears, discountRate, riskType, scope…)` converts
the stored annual measures into the decision forms — the probability of at least one failure
over the period (the exact binomial P_T = 1 − (1 − p)^T of the scope's Fail-stream annualized
failure probability, evaluated in log space) and the cumulative (T·m), discounted
(m·(1 − (1 + r)^−T)/r), and equivalent-annual expected consequences of the requested stream —
each computed per realization and reduced with the stored realization weights into
`ExposurePeriodRiskResults` (lower/median/mean/upper `ExposurePeriodInterval`s at the
configured confidence width). A plain query result: never serialized, post-run weights honored,
the published results untouched; null without a stored full-uncertainty ensemble. The
equivalent annual reproduces the annual expected consequence for a stationary analysis (the
identity is the point — the base-versus-future composition belongs to the future cost-benefit
layer), and every conversion assumes stationarity and inter-year independence, the caveat that
motivates a genuine time axis.

## Life-cycle risk trajectories (runtime-only)

`RiskAnalysis.MeasureLifeCycleRisk(definition)` returns `LifeCycleRiskResults`: the per-epoch
rows (`LifeCycleEpochRisk` — start year, span, evaluation age, the cumulative failure
probability through the epoch's end, a system `LifeCycleEpochEntry` plus one per component,
and the configuration labels in effect) and the horizon aggregates — the probability of at
least one failure by the horizon, cumulative/discounted/equivalent-annual expected
consequences per declared type under the non-absorbing (annual-renewal) convention of the
exposure-period conversions, and the absorbing (first-failure-terminates) survival-weighted
cumulative and discounted variants. Inputs are the runtime-only records `LifeCycleDefinition`,
`LifeCycleIntervention` (house events + hazard replacements per year), and
`HazardReplacement`. Plain query results and inputs: nothing serialized, hashed, or
seed-affecting; every epoch quantifies mean-only on throwaway clones and the authored model
is never touched. See [uncertainty-analysis.md](uncertainty-analysis.md) §6.7.

## The multi-consequence axis

An analysis declares its consequence types once (`ConsequenceTypeDescriptor` — label, unit, and
an optional per-type `ConsequenceThreshold`), and **every declared type is computed through the
one engine pass**: probability structure is shared, refinement is driven by the primary type, and
type k records into the primary containers (k = 0) or position k − 1 of the `Additional*`
collections at every scope. A secondary type evaluates assurance only when its own declared
per-type threshold supplies one (the analysis-level threshold is declared in the primary type's
units). All multi-type members are results-JSON append-only: an earlier payload loads with null
blocks meaning "not computed".

## Contribution

`RiskContribution` stores the three raw attributed values per (mode, type) —
`FailureProbability`, `FailureMean`, `ExcessMean` — with shares derived on read
(`ShareOf(total)`), summing exactly to the parent's recorded totals under every combination
method ([risk-contribution.md](risk-contribution.md)). Contributions ride
`FailureModeRealization.Contribution`/`AdditionalContributions` and
`ComponentRealization.SystemContribution`/`AdditionalSystemContributions`, with summary capture
in the summary tree — available in mean-only runs, per realization, and as ensemble confidence
intervals, all without re-simulation.

## Reliability mode

`RiskAnalysisMode.Reliability` is a mode of the same analysis, not a second type: the consequence
surface is degenerate at zero, the headline output is the annualized failure probability, the
cumulative failure profile is the headline profile, exhaustive Total streams still obey the
exact-mass rules, and contribution reports the consequence-free %-of-APF basis fully.

## Results-JSON conventions

Results serialize with System.Text.Json only — `ToJson()`/`FromJson()` plus GZip-compressed byte
overloads on the roots (`ResultsJson` holds the one shared configuration). The v1.0
BinaryFormatter BLOBs are deliberately not readable; v1.0 projects re-run their analyses.
Conventions:

- Doubles use the default shortest-round-trip formatting, which is bit-faithful;
  `JsonNumberHandling.AllowNamedFloatingPointLiterals` lets NaN and ±Infinity — legitimate values
  for unpopulated measures — round-trip as quoted literals.
- Serialized members are **append-only**: newer readers load older payloads with the missing
  blocks null ("not computed"), and nothing is ever renamed.
- The weight family (`RealizationWeights`, `EffectiveRealizationCount`,
  `RealizationWeightsHash`) is additionally **null-suppressed**: absent from unweighted
  payloads, so an unweighted result set serializes byte-identically to a pre-weight payload
  (pinned by test against the captured pre-weight digest).
- Compressed payloads are compared on the **decompressed** bytes — the GZip header embeds
  non-content fields.
- `EnsembleResults.Manifest` (`AnalysisRunManifest`) records deterministic run provenance;
  `IsProvenanceVerified` is true only for a current-schema manifest. Out-of-range indexer reads
  on the ensemble preserve the v1.0 null-result convention, while writes fail closed.

## Verification

The results surface is pinned across the verification set: exact-construction and measure tests
in the fast suite; [engine-reproducibility](../verification/engine-reproducibility.md)
(bit-identity of full results JSON); [multi-consequence](../verification/multi-consequence.md)
(the declared-type axis); [risk-profiles](../verification/risk-profiles.md) (the profile
catalog); [scalar-uncertainty](../verification/scalar-uncertainty.md) (`EnsembleSummary` against
closed-form quantiles); [weighted-ensemble](../verification/weighted-ensemble.md) (the weighted
reductions against an independent re-implementation); [contribution](../verification/contribution.md)
(the Σ identities).
