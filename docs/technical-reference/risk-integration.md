# Risk Integration

> Technical reference for the numerical integration inside the `RMC.TotalRisk.Analyses.Risk.RiskAnalysis`
> engine. Covers the 1D adaptive quadrature, the selectable integrand
> (`RiskIntegrand`), and the multi-dimensional VEGAS integration with its power-transform tail focus.
> Companion: [loss-exceedance-curves.md](loss-exceedance-curves.md) (how the integrator's evaluation
> points become LECs and risk measures). Normative spec:
> [../requirements/MODEL_LIBRARY_ARCHITECTURE.md](../requirements/MODEL_LIBRARY_ARCHITECTURE.md)
> §7.3, §7.7, §7.8. The v1.0 sources cited for defect evidence (`RiskAnalysis.vb`, `Curve.vb`)
> are in the v1.0 reference repository.

## The integrator is an adaptive sampler

The single most important fact about the engine's design: **the risk integral's returned value is
discarded.** In v1.0 the per-component `AdaptiveSimpsonsRule` integrand returns the conditional total
expected consequence at each hazard probability `p`, but the call site
(legacy `RiskAnalysis.vb:2891-2892`) reads only `integrator.FunctionEvaluations` and
`integrator.StandardError` — a deliberate v1.0 design v1.1 preserves. The reported means, standard
deviations, and LECs are built afterward from
the **risk points** the integrand recorded as a side effect (`recordOutput = true`).

So the adaptive integrator is being used as an *importance sampler*: its job is to decide **where**
along `p ∈ (0, 1)` to place evaluations, densely where the integrand varies, sparsely where it is
flat. Each evaluation records a `RiskPoint` — the hazard level, the failure probability, and the
per-branch consequences — and the LEC is assembled from that point cloud
([loss-exceedance-curves.md](loss-exceedance-curves.md)). Two consequences follow:

1. The **choice of integrand changes only where points land**, never what is reported — every
   risk-type LEC and every risk measure is produced regardless (this is why `RiskIntegrand` is a
   refinement-objective enum, not an output selector).
2. The integrator's error estimate and evaluation count are genuine diagnostics worth surfacing on
   the results container.

## The trapezoid foundation

The conceptual foundation under every method here is fixed-bin trapezoid integration of an
expectation, in any of four equivalent framings ([7] App. D): over the **PDF**
(`E[X] ≈ Σ x̄ᵢ·f(x̄ᵢ)·Δxᵢ`), over the **CDF** (the Stieltjes form `E[X] ≈ Σ x̄ᵢ·ΔFᵢ` with bin mass
`ΔFᵢ = F(x_bᵢ) − F(x_aᵢ)`), over the **inverse CDF** (`E[X] = ∫₀¹ F⁻¹(p) dp` on a probability
grid — the framing the engine's probability-space integrand generalizes), or on a **normal-Z
probability grid** (bins uniform in Φ⁻¹(p), concentrating resolution in the tails). All four add
explicit tail atoms — `x_{a₁}·F(x_{a₁})` below and `x_{b_K}·(1 − F(x_{b_K}))` above — so the
enumerated mass is exactly one. Risk integration replaces the bare x̄ᵢ with the full integrand
`P(F|x̄ᵢ)·C(x̄ᵢ)`. Fixed bins waste evaluations where the integrand is flat, which is exactly what
the adaptive methods below fix — v1.0 with Adaptive Simpson's recursion and adaptive importance
sampling, v1.1 with the Gauss–Kronrod and VEGAS machinery — while the tail-atom discipline
survives as the engine's endpoint rectangles (§Collectively exhaustive probability mass).

## 1D quadrature: Adaptive Gauss–Kronrod (replaces Adaptive Simpson)

v1.0 used `Numerics.Mathematics.Integration.AdaptiveSimpsonsRule` — Simpson's rule per subinterval
with the recursive stopping criterion `(1/15)·|S(a,m) + S(m,b) − S(a,b)| ≤ ε + ε·|S(a,b)|`,
defaults ε = 1e-8, depth 100, 10⁶ evaluations [25]. v1.1 uses
`AdaptiveGaussKronrod` (G10K21 — 10-point Gauss with a 21-point Kronrod extension, 21st-order accurate
for smooth integrands, QUADPACK-style [15]). The Numerics type exposes the **same surface** the engine
drove Simpson through, so the swap changed no call-site shape. The engine configures it as
(defaults shown; the tolerance/depth knobs live on `RiskAnalysisOptions`):

```csharp
var integrator = new AdaptiveGaussKronrod(p => Integrand(p), support.Lower, support.Upper)
{
    ReportFailure = false,
    MaxFunctionEvaluations = 1_000_000,   // Options.MaxEvaluations
    MaxDepth = 100,                       // Options.MaxDepth
    RelativeTolerance = 1e-8,             // Options.Tolerance (ensemble realizations use
                                          // Options.EnsembleTolerance, default 1e-4)
    MinDepth = 2,                         // ensemble realizations use Options.EnsembleMinDepth
};

// The adaptivity is seeded with the same 50 hazard-stratified bins v1.0 used, mapped to
// probability space over the sampled hazard's natural support:
var bins = Stratify.XValues(new StratificationOptions(
                hazard.InverseCDF(1e-16), hazard.InverseCDF(1 - 1e-16), 50), true);
bins = Stratify.XToProbability(bins, hazard.CDF, false);
integrator.Integrate(bins);
```

### The two 1D call sites

| Call site | Domain | Integrand | Notes |
|---|---|---|---|
| Per-component risk integral | the sampled hazard's natural probability support | selected by `RiskIntegrand` (below) | 50 `Stratify` hazard bins; explicit endpoint rectangles complete the mass budget (next section); a **bivariate** component routes to the two-dimensional interior below instead |
| Annualized-failure-probability probe | the same support | `P_F(p)` | deterministic, mean-sample; runs once per joint-method run to set the VEGAS tail-focus target (§VEGAS below); under a bivariate hazard each probe evaluation marginalizes the secondary axis through the fixed-slice conditional sweep |

### The bivariate two-dimensional interior

A component under a `BivariateHazard` replaces the 1D per-component quadrature with
`AdaptiveGaussKronrod2D` over (primary non-exceedance u, probit conditional coordinate
z = Φ⁻¹(t)) — one globally adaptive tensor-rule pass per stratification strip, steered by a
balanced probability-and-consequence surrogate, with each strip granted a fair share of
`MaxEvaluations` (floor one 441-evaluation region) and its Jacobian-folded masses rescaled to
the strip's exact probability width. The flushed nodes group by exact primary abscissa into
merged conditional columns; each distinct abscissa replays one staged component evaluation over
its column and records one risk point per stream, so the endpoint rectangles, the mass ledger's
two invariants, and every downstream consumer operate unchanged. Everywhere a fixed u must be
evaluated — the joint (VEGAS) integrand, the AFP probe, hazard-level sensitivity, the endpoint
rectangles themselves — a 1D adaptive pass over z adopts a per-slice conditional column at the
same discipline (relative tolerance from the pass context, absolute floor 1e-15, budget
21 × `SecondaryIntegrationBins`, raised to the strip fair share for the additive interior's
endpoint columns). The design, the probit-coordinate rationale, and the measured accuracy are
in [bivariate-hazards.md](bivariate-hazards.md); the fixed conditional trapezoid survives only
as the discretization diagnostic's instrument.

v1.0 had a third recurring quadrature — the CVaR integral over the log-log LEC quantile
(`Curve.vb:547-552`), run with library-default tolerances on the engine's steepest integrand. v1.1
retires that integration entirely: the LEC quantile is piecewise `c·(p/p₁)^s` in the floored
base-10 space, so conditional value-at-risk is computed as the **exact segment-by-segment closed
form** ([loss-exceedance-curves.md](loss-exceedance-curves.md)).

### Quadrature properties the engine's mass accounting relies on

- **G10K21 nodes are strictly interior** — the largest abscissa is ≈ 0.99566 < 1, so no evaluation
  lands on an interval endpoint, and adjacent stratification bins never share a `p` from the node
  placement alone. That is *not* enough to make the recorded set duplicate-free: a saturating hazard
  CDF collapses several hazard bins onto the same probability, and `Integrate(List<StratificationBin>)`
  then evaluates a zero-width bin 21 times at one abscissa. The mass ledger keys on the exact abscissa
  and **sums weights per key**, so the degenerate case is credited once at its true (zero) width.
- **Point placement is denser and differently distributed than Simpson's.** Recorded LECs differ
  from v1.0 by quadrature resolution alone. The mean converges to the same value — that is the
  verification gate ([../verification.md](../verification.md), the means-versus-tails policy:
  means are v1.0-parity, tails are MC-parity).
- **`MinDepth` is set to 2** on the mean path (the Numerics default is 0): a region that looks flat
  to the first Kronrod pass (a low-probability shoulder before a steep fragility) would otherwise be
  accepted without subdivision.
- **`StandardError`** is a real error estimate (`√Σ (Kronrod − Gauss)²` accumulated across accepted
  intervals), unlike Simpson's Richardson proxy — the engine surfaces it on the realization and in
  the ensemble convergence diagnostics.

### Collectively exhaustive probability mass

The sampled hazard defines its natural finite probability support `[p_min, p_max]`. Adaptive
Gauss-Kronrod integrates only that support. Its acceptance-aware recorder publishes the unchanged
Kronrod-node weights for intervals that survive refinement; rejected-node data is never retained.
Under the default extrapolation policy the support is the table span; a hazard with an extending
[extrapolation policy](hazard-functions.md#extrapolation-policy) widens its inverse tails, so the
support probed at the 10⁻¹⁶ non-exceedance floors approaches the full axis, the endpoint
rectangles below shrink toward zero mass, and the adaptively integrated interior deliberately
carries what the rectangles carried — the mass budget stays exhaustive by the same construction.

Appendix D's collectively exhaustive construction adds two explicit endpoint rectangles:

1. lower edge mass `p_min`, evaluated by the complete component-risk calculation at the lower
   supported hazard;
2. all accepted AGK interior contributions, whose compensated mass is `p_max - p_min`;
3. upper edge mass computed as the residual
   `1 - compensated_sum(lower edge + interior masses)`, evaluated at the upper supported hazard.

The residual must agree with `1 - p_max` within a tight floating-point bound. Computing it as the
residual makes the underlying ledger total exactly one without stretching the first or last interior
bin and without proportionally renormalizing any AGK weight. The report's worked partition is therefore

```
0.001 + 5(0.1996) + 0.001 = 1
```

for five natural interior bins. In general K interior contributions become K+2 contributions.
Endpoint evaluations use the same recording path as interior nodes, so every consequence type,
stream, profile, contribution, and diagnostic receives the edge mass at its endpoint consequence.
They are included in `FunctionEvaluations`. Coincident, one-sided, saturated, and zero-width supports
coalesce exact abscissas and avoid double counting.

The internal pooled mass ledger seals by sorting exact probability abscissas, compensated-coalescing
duplicates, and checking two invariants:

1. the interior accepted mass agrees with the natural support width;
2. the final exhaustive mass is exactly one, with every recorded curve consuming every sealed entry.

Exhaustive Total streams fault the run if their raw recorded mass is not exactly one after sealing.
Defective streams must be finite and within `[0,1]`. Reliability-mode Total streams obey the same rule
even when every consequence is zero.

### Lazy failure-mode enumeration and the probability partition boundary

For `JointFailures`, the component first forms the marginal failure probabilities of its combination
units. Every dependency then uses caller-owned lazy output buffers in the established
subset-size/lexicographic order: `IndependentExclusiveLazy` for independent units,
`PositivelyDependentExclusiveLazy` for perfectly positive dependence, and `ExclusivePCMLazy` for
perfectly negative or explicit correlation-matrix dependence. The PCM joint-probability formula,
inclusion/exclusion association order and sign changes, closing all-ones half-gap row, dual absolute
and relative convergence predicate, and default tolerances (`1E-4`, `1E-4`) are unchanged. Dense
indicator and binomial matrices remain compatibility inspection surfaces only and are not populated
by the engine. Runtime memory is proportional to emitted rows, although worst-case compute remains
combinatorial when convergence is slow.

PCM is an approximation and its exclusive cells can overshoot a unit partition by roundoff or
approximation error. TotalRisk applies the approved boundary once, immediately after lazy enumeration
and before consequences, expected values, profiles, or contributions:

```text
remaining = 1
for cell in deterministic row order:
    accepted = clamp(cell, 0, remaining)
    remaining = clamp(remaining - accepted, 0, 1)
```

This is not normalization: no earlier cell is rescaled and no probability is redistributed. Only a
trailing overshoot is discarded. Finite scalar probability outputs and recorded conditional
probabilities are likewise clipped with `Tools.Clamp`; NaN remains NaN and is rejected by validation.
The `Curve` mass checks remain strict, so an invalid exhaustive distribution cannot be hidden by a
published-property clamp.

## `RiskIntegrand` — the adaptive-refinement objective

`RiskIntegrand` lives in `RMC.TotalRisk.Core.Enums` (one file). It is **not** a runtime discriminator —
it is a field on `RiskAnalysisOptions` and is therefore **hashed** with the other options (it changes
seeds only through the options hash, never the function/component identity surface). Its default,
`MeanTotalRisk`, reproduces v1.0 point placement exactly.

All members are functions of the hazard non-exceedance probability `p`; `P_F` = failure probability at
`h = Hazard.InverseCDF(p)`, `C_F` = failure consequence, `C_NF` = non-failure consequence.

| Member | Integrand (refinement target) | Concentrates evaluations where… |
|---|---|---|
| `MeanTotalRisk` **(default)** | `P_F·E[C_F] + P_NF·C_NF` | v1.0 behavior; the mean annualized total consequence accumulates |
| `MeanIncrementalRisk` | `P_F·E[(C_F − C_NF)⁺]` (the Excess curve) | the **reducible** risk lives — the quantity most USACE alternatives are judged on |
| `TotalProbabilityOfFailure` | `P_F(p)` | the fragility is steep rather than where consequences are large; the natural pairing for `RiskAnalysisMode.Reliability` |
| `TailConditionalRisk` | `P_F·E[C_F]·1{p ≤ α}`, α = `Options.Alpha` | the α-tail — so VaR, CVaR and the F-N tail ordinates converge (the CVaR / expected-shortfall family) |
| `ThresholdExceedanceProbability` | `P(C > ConsequenceThreshold ∣ p)` | the assurance / tolerable-risk compliance decision is made |
| `SecondMoment` | `E[C² ∣ p]` | the LEC variance converges — v1.0's weakest output |
| `Balanced` | normalized `MeanTotalRisk + SecondMoment + TailConditionalRisk` | every measure converges together; the sensible default for headless / API callers that read all measures |

### Discontinuous integrands need a bin boundary

`TailConditionalRisk` and `ThresholdExceedanceProbability` are **discontinuous** in `p` (the indicator
switches at `p = α`, respectively at the consequence-threshold crossing). A high-order rule loses its
accuracy on any interval straddling a discontinuity. Requirement: inject the discontinuity `p` as an
extra **stratification-bin boundary** so it falls on a bin edge — the 50-bin hazard stratification
simply gains one boundary, and `AdaptiveGaussKronrod.Integrate(List<StratificationBin>)` integrates
each bin independently, so the discontinuity never lands inside a Kronrod panel.

### Why the tail members exist

Value-at-Risk marks the boundary of the α-tail but says nothing about the losses beyond it;
conditional VaR / expected shortfall is the coherent tail measure that averages them [17], and is the
regulatory standard (Basel III FRTB replaced 99% VaR with 97.5% expected shortfall [18]). For
life-safety consequence LECs the tail *is* the decision-relevant region, so the engine needs an
integrand that refines it directly rather than hoping the mean-total objective happens to place
points there.

## Multi-dimensional integration: VEGAS with power-transform tail focus

For the joint system-risk method (D > 1 components with `SystemRiskMethod = Joint`), the engine
integrates over the D-dimensional unit hypercube `(1e-16, 1−1e-16)^D` of correlated hazard
probabilities using `Numerics.Mathematics.Integration.Vegas` [16] (legacy `RiskAnalysis.vb:2984-3177`).
Correlation enters through the multivariate-normal inverse CDF `_eMVN.InverseCDF(p)`. As in the 1D
path, the VEGAS **weight `wgt` is the LEC probability mass** for each recorded point — this is why the
joint path (unlike the 1D path) needs no mass re-derivation.

### The power transform (γ)

Numerics' VEGAS gained a **power transform** for rare-tail sampling
(`Vegas.cs`: `TailFocusParameter`, `ApplyPowerTransform`, `PowerTransformJacobian`,
`ConfigureForRareEvents`). It maps the sampling probability by

```
p' = 1 − (1 − p)^γ        (γ = TailFocusParameter, default 1.0 = identity = v1.0 behavior)
```

concentrating samples near `p → 1` (the upper hazard tail) when γ > 1, with the weight corrected by
the Jacobian `dp'/dp = γ(1 − p)^(γ−1)` so the integral stays unbiased. `ConfigureForRareEvents(pTarget)`
sets `γ = ln(pTarget)/ln(0.05)` clamped to [1, 20], `NumberOfBins ≥ 100`, and `Alpha = 1.8`.

### Exposing and setting γ in TotalRisk

`RiskAnalysisOptions` gains:

- `VegasTailFocusMode { None, Automatic, Manual }` — default `Automatic`. `None` pins γ = 1 for exact
  v1.0 comparability; `Manual` uses the supplied γ.
- `VegasTailFocusParameter` (γ) — default 1.0, valid [1, 20].

**The `Automatic` heuristic: a deterministic quadrature probe.** An earlier design harvested
`pTarget` from the VEGAS warm-up itself; two implementation facts rule that mechanism out. First,
`ConfigureForRareEvents` raises `NumberOfBins`, whose setter
reallocates the importance-grid arrays — configuring γ *after* the warm-up would wipe the warmed grid,
so γ must be set **before** any VEGAS pass. Second, a γ = 1 Monte Carlo warm-up cannot observe the
rare failure probabilities the target needs — that blindness is the very problem the transform
solves. The engine instead probes each component's annualized failure probability **on the mean
sample with adaptive Gauss–Kronrod**, once per run:

```
AFP_i = ∫ P_F,i(p) dp          (the one call site where the integral's returned value is the product)
pTarget = clamp( min_i AFP_i · Alpha , 1e-12 , 1e-2 )
```

then calls `Vegas.ConfigureForRareEvents(pTarget)` before the warm-up. The probe costs about a
thousand evaluations per component, is fully deterministic (mean sample, fixed stratification),
measures the failure probabilities to quadrature accuracy however rare they are — and the warm-up
itself then adapts under the active γ, strictly better than a post-warm-up harvest could be.

### Driving stream, recording passes, and mass normalization

- **Seeded Mersenne Twister by default; seeded scrambled Sobol on request.** Numerics' VEGAS
  defaults to `UseSobolSequence = true` with the *unrandomized* sequence — v1.0 used it, but it is
  seed-independent and would void the §5.5 content-seed contract, so the engine sets
  `UseSobolSequence = false` and `Random = new MersenneTwister(vegasSeed)` with
  `vegasSeed = ToPositiveSeed(HashCombine(systemSeed, "VEGAS", realizationIndex))`, where
  `systemSeed` folds the analysis seed with every component's canonical hash and occurrence index in
  canonical-hash order. The opt-in `RiskAnalysisOptions.UseSobolJointSampling` restores the
  quasi-random driver without giving up that contract: it re-enables the Sobol sequence with
  `SobolSeed = vegasSeed`, the seeded Matousek scrambling, so the driver is reproducible,
  content-seeded, and pinned unbiased with its tail-focus Jacobian intact against the brute-force
  event oracle. A deliberate, hashed, value-moving selection — the attribute serializes only when
  enabled, so every existing options form and hash is unchanged.
- **Five recording passes, self-normalized.** v1.0 recorded a single pass of `FinalEvaluations`
  (default 10,000) — far too sparse for a tail ordinate in D dimensions. The engine records across
  five passes (`Initialize = 1`, `IndependentEvaluations = 5`) with `FinalEvaluations` scaled by D in
  `SetIntegrationDefaults`, then scales every recorded mass by the reciprocal of the realized weight
  sum: per-pass `Σ wgt` equals the domain volume only in expectation, so self-normalization makes the
  exhaustive Total budget exactly one (and stays consistent if the evaluation cap truncates a pass).
- **The Jacobian demonstrably reaches the weight.** In TotalRisk `wgt` *is* the LEC probability mass,
  so a Jacobian missing from the recorded weight would bias every LEC ordinate even with a correct
  returned integral. Source-confirmed (`Vegas.cs` folds `PowerTransformJacobian` into the
  weight handed to the integrand) and **empirically gated** by the tail-focus audit
  ([../verification/system-risk.md](../verification/system-risk.md)): γ = 1, manual γ = 4, and the
  automatic focus agree on the mean, the failure union, and a deep-tail ordinate, with every recorded
  budget self-normalizing to one. Numerics carries its own upstream unit tests for the transform
  (a known heavy-tail function integrates to the same value at γ ∈ {1, 4, 10}; `Σ wgt` equals the
  domain volume at every γ).

See [loss-exceedance-curves.md](loss-exceedance-curves.md) §System aggregation for how the joint path
enumerates real component failure/non-failure combinations (rather than convolving conditional means)
through the per-pathway lists on `ComponentRiskOutput`. Reproducibility scope: renaming is bit-inert;
component *reordering* under the joint method is statistically equivalent but not bit-identical — the
VEGAS variates couple the hypercube dimensions, so reordering permutes which coordinate stream drives
which component.
