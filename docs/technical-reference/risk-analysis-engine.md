# The Risk Analysis Engine

> Technical reference for `RMC.TotalRisk.Analyses.Risk` — `RiskAnalysis`, `RiskAnalysisOptions`,
> `ResourceEstimate`, and the run lifecycle — and the definitional spine its outputs implement.
> Grounding: the *Risk Definitions, Measures, and Plots* technical note [23] (§1–4 here follow it)
> and the *System Risk Analysis* technical note [25] (§5–8 here follow it), with the extended
> derivations in [7] §7 and App. D/I. Executable evidence:
> [../verification/engine-reproducibility.md](../verification/engine-reproducibility.md),
> [../verification/single-component-mean-parity.md](../verification/single-component-mean-parity.md),
> [../verification/system-risk.md](../verification/system-risk.md),
> [../verification/system-risk-matrix.md](../verification/system-risk-matrix.md),
> [../verification/nfip-assurance.md](../verification/nfip-assurance.md), and
> [../verification/risk-profiles.md](../verification/risk-profiles.md). Companion pages:
> [risk-integration.md](risk-integration.md) (the numerical methods),
> [loss-exceedance-curves.md](loss-exceedance-curves.md) (the exact LEC and measure construction),
> [failure-mode-combination.md](failure-mode-combination.md),
> [uncertainty-analysis.md](uncertainty-analysis.md), and
> [system-components.md](system-components.md).

## 1. Risk as a triplet, an expectation, and a discrete integral

Risk is qualitatively "probability and severity of an adverse event" [46]; quantitatively, Kaplan
and Garrick's triplet set R = {⟨sᵢ, pᵢ, cᵢ⟩} — what can happen, how likely, how bad [29]. In dam
and levee safety the scenarios are discrete hazard levels, and the triplet at level xᵢ is
⟨xᵢ, P(xᵢ)·P(F|xᵢ), C_F(xᵢ)⟩. Summing the triplets gives risk as an expectation,

```
E[C_F] = ∫ f_X(x) · P(F|x) · C_F(x) dx  ≈  Σᵢ ΔFᵢ · P(F|x̄ᵢ) · C_F(x̄ᵢ),           (1)
```

with bin mass ΔFᵢ = F_X(x_bᵢ) − F_X(x_aᵢ), midpoints x̄ᵢ, and tail atoms closing the mass beyond
the integration limits [23]. The compact USACE form is Risk = P(Hazard) × P(Failure | Hazard) ×
C(Failure). The trapezoidal construction is the *conceptual* foundation; the engine evaluates the
integral with adaptive Gauss–Kronrod quadrature over an exhaustive probability-space mass
partition ([risk-integration.md](risk-integration.md)).

## 2. The five risk types

`RiskType` implements the note's five types exactly [23]. Failure, non-failure, and total risk
describe events that can occur; excess and background risk are analytical constructs for
prioritizing safety investments. USACE usage: excess risk = *dam risk* / *levee risk* (also
*incremental risk*); total risk = *flood risk*.

| `RiskType` | Definition | Description |
|---|---|---|
| `Fail` | E[C_F] = Σ P(xᵢ)·P(F|xᵢ)·C_F(xᵢ) | Consequences from failure events |
| `NonFail` | E[C_NF] = Σ P(xᵢ)·{1 − P(F|xᵢ)}·C_NF(xᵢ) | Consequences without breach (e.g., spillway activation) |
| `Total` | E[C_T] = E[C_F] + E[C_NF] | All outcomes; the *do-no-harm* comparison measure |
| `Excess` | E[C_Δ] = Σ P(xᵢ)·P(F|xᵢ)·C_Δ(xᵢ), C_Δ = C_F − C_NF | Failure consequences in excess of non-failure |
| `Background` | E[C_B] = Σ P(xᵢ)·C_NF(xᵢ) | The risk remaining with no structural vulnerability |

Two exact decompositions of total risk hold per component — by system response and by attribution:

```
E[C_T] = E[C_F] + E[C_NF]        (what happens when the structure fails vs. not)      (2)
E[C_T] = E[C_Δ] + E[C_B]         (risk caused by vulnerability vs. risk regardless)   (3)
```

Background risk is **not** the risk of non-failure: background weights C_NF by the full hazard
probability (α = 1), non-failure by the complement of failure. Negative excess consequences (where
C_NF exceeds C_F at a level) are set to zero, so excess risk counts only consequences genuinely in
excess [23]. A structural modification should reduce *total* risk — reducing failure risk while
increasing non-failure risk by more violates the do-no-harm principle.

## 3. The risk measures

Each risk type reports four standard measures [23]:

- **Exceedance probability α.** For failure and excess risk, α is the Annual Probability of
  Failure, α_F = Σ_{i ≥ x_c} P(xᵢ)·P(F|xᵢ), summed from the critical hazard level x_c (the lowest
  level with P(F|x) > 0) — the R–S probability that demand exceeds capacity. For total and
  background risk α = 1 (unconditional); for non-failure α = 1 − α_F.
- **Mean E[C].** The unconditional expected annual consequences — the basis of benefit–cost and
  investment decisions.
- **Conditional mean E[C|F] = E[C]/α.** Expected consequences *given* the conditioning event, with
  the recovery identity E[C] = α · E[C|F] (traditionally written f·N̄; α avoids overloading f).
  Undefined at α = 0; equal to the mean where α = 1.
- **Standard deviation σ = √(E[C²] − E[C]²).** Dispersion about the mean — two alternatives with
  equal means but different σ carry different tail exposure, and life-safety decisions are
  risk-averse. The note's raw power-sum variance (its Eq. 26) is numerically unstable at scale;
  the v1.1 engine computes central moments with a two-pass weighted Welford reduction instead
  ([loss-exceedance-curves.md](loss-exceedance-curves.md)).

Beyond the standard four, `RiskMeasureOptions` gates the optional tail measures — consequence
threshold probability, value-at-risk, and conditional value-at-risk ([7] §7.6.1) — computed
exactly from the loss-exceedance curve ([loss-exceedance-curves.md](loss-exceedance-curves.md)).

## 4. The two risk plots

**Loss Exceedance Curve** (F-N curve): S(c) = P(C ≥ c) on log–log axes — sort the recorded
(probability, consequence) pairs by decreasing consequence and accumulate probability [23]. The
leftmost ordinate equals α; the area under the curve equals the mean, E[C] = ∫S(c)dc; the shape
separates frequent-modest risk (steep) from rare-catastrophic risk (fat-tailed). The USACE
tolerable risk limit is defined on this curve, making it the primary basis for guideline
comparison. The v1.1 construction is exact — every accepted quadrature point enters at its
recorded mass; no histogram binning ([loss-exceedance-curves.md](loss-exceedance-curves.md)).

**Conditional Mean Loss Plot** (the α–η plot): the single point (E[C|F], α) on log–log axes, with
diagonals as lines of constant mean risk (E[C] = α·E[C|F]). High-α/low-consequence points are
frequent-but-modest modes; low-α/high-consequence points are rare-but-catastrophic ones.
Meaningful only where α < 1. The TRL screening logic is one-directional: a point *below* the limit
guarantees the whole LEC is below (the conditional mean is the conditional distribution's center
of mass); a point *above* proves nothing — examine the LEC [23].

## 5. Components, the system, and what the integral means

A **system component** is identified by its hazard function; it owns failure modes (response +
consequence chains) and a non-failure mode connecting hazard directly to non-failure consequences
([system-components.md](system-components.md)). Failure-mode combination
([failure-mode-combination.md](failure-mode-combination.md)) operates *within* a component — the
joint occurrence of failure modes given a single hazard event. **System risk** is a different
question: the joint occurrence of *hazard events across components* over the block implied by the
hazard functions (annual, for annual-maximum curves) [25]. Components connect in series (any
failure contributes); parallel redundancy is modeled inside a component's response through event
trees. Failure-mode capacities are statistically independent *across* components — local materials,
geometry, and construction — with cross-component dependence carried entirely by the joint hazard
probability (§7).

**The engine is not an event simulator.** The system integral computes a block-level expectation
over the joint distribution of annual maxima — the probability that a large flood and a strong
earthquake occur in the same *year*, never in the same *event* [25].

For one component the risk integral is one-dimensional (Eq. 1). For D components it becomes the
D-dimensional system integral over the joint hazard density,

```
E[C]_Ω = ∫…∫ ( Σ_d C_d(x_d) ) · f_{X₁…X_D}(x₁,…,x_D) dx₁…dx_D,                     (4)
```

whose discrete two-component expansion enumerates the failure/non-failure combinations:

```
E[C_F]_Ω = Σᵢ Σⱼ P_XY(xᵢ,yⱼ) · [ R_Xonly + R_Yonly + R_XY ],
R_Xonly = P(F_X|xᵢ)(1 − P(F_Y|yⱼ))·C_FX(xᵢ),
R_Yonly = (1 − P(F_X|xᵢ))P(F_Y|yⱼ)·C_FY(yⱼ),
R_XY    = P(F_X|xᵢ)·P(F_Y|yⱼ)·C*_XY(xᵢ,yⱼ),                                        (5)
```

the joint term priced by the joint consequence rule (§8) and generalizing to 2^D combinations. A
non-adaptive K-bin grid would cost K^D evaluations (100⁵ = 10¹⁰ at five components) — the curse of
dimensionality that motivates the two system methods [25].

## 6. The two system risk methods

**Additive** (`SystemRiskType.AdditiveRiskMethod`, the default): strictly independent components,
consequences summed. Each component integrates one-dimensionally and the results add, E[C]_Ω =
Σ_d E[C]_d — exactly the separated form of Eq. 4 under independent hazards. The v1.0 product could
not produce a system-level LEC this way; **v1.1 adds it** through exact lattice FFT convolution of
the component consequence distributions, atom-aware for the zero-consequence mass
(`SystemConvolution`; [loss-exceedance-curves.md](loss-exceedance-curves.md) §System aggregation),
resolving the note's §5.1.1 planned enhancement.

**Joint** (`SystemRiskType.JointRiskMethod`): evaluates the full D-dimensional integral with
VEGAS adaptive importance sampling, supports all four hazard dependency options and all four
joint consequence rules, and produces the complete system output set (all risk types, all
measures, system LEC and CML). The v1.0 tail limitation — VEGAS adapts toward the total-risk bulk
and can starve rare-failure tails ([25] §6.7) — is addressed in v1.1 by the power-transform tail
focus: a deterministic per-component quadrature probe measures the annualized failure probability
and sets the concentration parameter γ before any adaptive pass, with the recording accumulated
over five self-normalized passes ([risk-integration.md](risk-integration.md) §VEGAS; the γ audit
in [../verification/system-risk.md](../verification/system-risk.md)).

Selection follows the note [25]: additive for screening and mean system risk; joint when system
LECs feed tolerable-risk evaluation, when hazard dependency matters, or when a non-additive joint
consequence rule is required. The joint method is limited to 20 components (the VEGAS dimension
limit); most dam studies use 1–3.

## 7. Hazard dependency between components

Cross-component dependence enters only through the joint hazard probability (joint method only;
the additive method is strictly independent by definition — a component-hazard dependence under
the additive method is a validation error). Four options [25]:
**independent** (P_XY = P_X·P_Y — different physical mechanisms, separate watersheds);
**perfectly positive** (same quantile everywhere — dams on one river, adjacent reaches);
**perfectly negative** (the ρ > −1/(k−1) positive-definite bound — a sensitivity case); and a
**user-defined correlation matrix** (validated positive definite) under the Gaussian-copula
framework, for correlated-but-distinct tributaries.

Hazard dependency and failure-mode dependency are different controls: hazard dependency couples
*components* through P_XY (a system option); failure-mode dependency couples *capacities within a
component* (a component option) — confusing them is the note's first pitfall.

## 8. Joint consequence rules

When several components fail in the same block, `JointConsequenceType` prices the joint term:
`Additive` (non-overlapping inundation areas), `Average` (partial overlap), `Maximum` (largely
coincident areas — **the system-level default**, because the most common multi-component case is
multiple hazard types at one structure, where adding would double-count the same population at
risk), and `Minimum` (a deliberate lower bound). The same four rules price joint failure events
within a component's joint combination method.

At the system level the single-component attribution identity weakens to an inequality — embedded
correlation between excess and non-failure consequences across components gives

```
E[C_T]_Ω ≥ E[C_Δ]_Ω + E[C_B]_Ω,                                                    (6)
```

with the gap depending on the joint consequence rule [25].

## 9. The run lifecycle

`RiskAnalysis` owns its components (constructor input) and serializes options only — the
self-contained analysis shape. A run proceeds:

1. **Validate.** Options, per-component validation (mode-aware), the declared consequence-type
   axis (§10), hazard-axis label consistency, the joint combination guardrails, and the resource
   estimate's error lines all fold into one `Validate()` result.
2. **`EstimateResourceRequirements()`.** An itemized pre-run estimate of peak live memory and
   dominant work — realization storage, recorded quadrature points, combination buffers, VEGAS
   dimensions — surfaced as `ResourceEstimate` line items with severities, so an infeasible
   configuration names the option to change instead of failing mid-run.
3. **Occurrence indexing and sampler setup.** `AssignOccurrenceIndices` distinguishes
   identical-content functions; `SetupSamplers` builds every function's deterministic percentile
   design from content-based seeds ([hashing-and-seeding.md](hashing-and-seeding.md),
   [uncertainty-analysis.md](uncertainty-analysis.md)).
4. **The mean pass.** Every component integrates at the production tolerance from mean-sampled
   inputs; deterministic probes (the annualized-failure-probability probe driving the VEGAS γ)
   run here.
5. **The ensemble** (unless `EstimateMeanRiskOnly`). Realizations integrate at the ensemble
   discipline, in parallel, with sequential reductions — bit-identical at any thread count.
6. **Post-processing.** Exact LECs and measures, percentile bands, ensemble summaries,
   convergence diagnostics, contributions, and profiles assemble into the results trees
   ([results-catalog.md](results-catalog.md)), serialized as JSON.

### The options catalog

| Group | Options |
|---|---|
| Mode and scope | `Mode` (`RiskAnalysisMode.Risk`/`Reliability`), `EstimateMeanRiskOnly`, `UseDefaults` (re-applies the integration defaults at run start — set false before in-test overrides) |
| Ensemble | `Realizations` (default 1,000), `SamplingScheme` (`LatinHypercube` default / `MonteCarlo`), `PRNGSeed`, `ConfidenceIntervalWidth` |
| 1D integration | `Tolerance` (default 1e-8), `MaxDepth`, `MaxEvaluations`, `RiskIntegrand` (the adaptive-refinement objective), `EnsembleTolerance` (default 1e-4), `EnsembleMinDepth` |
| Joint system | `SystemRiskMethod`, `ComponentHazardDependency` (+ the hazard correlation matrix), `JointConsequences`, `WarmupCycles`, `WarmupEvaluations`, `FinalEvaluations`, `VegasTailFocusMode`, `VegasTailFocusParameter`, `MaxSystemCombinations`, `MaxPathwayCombinations` |
| Outputs | `LECOutputLength`, `SystemConvolutionPoints`, `RiskMeasures` (`RiskMeasureOptions`), `ConsequenceThreshold`, `Alpha` (the VaR/CVaR tail level), `OutputAdjustedFailureModeCurves`, `TolerableRiskCriteria` (`TolerableRiskCriterion` entries — the epistemic P(measure > threshold) statements the full-uncertainty summary reports; see [uncertainty-analysis.md](uncertainty-analysis.md)) |

All options are serialized, canonical-hash content (they are compute-relevant), except where the
architecture spec's strip rules say otherwise; `RiskIntegrand` and the VEGAS tail-focus fields are
hashed like every other option. The tolerable-risk criteria serialize as a conditional child —
written (and therefore hashed) only when configured, so every criteria-free options form and hash
is unchanged; criteria never influence sampling seeds, which derive from `PRNGSeed` and the
component identities alone.

## 10. The declared consequence-type axis

The analysis declares its ordered consequence types — the primary through
`SpecifiedConsequence`/`ConsequenceUnit` and each additional position through
`AdditionalConsequenceTypes` — and validation strictly matches every component's failure and
non-failure paths against the declaration (counts and order always; labels and units when both
sides are non-blank). Every declared type is computed in the same pass and reported per mode,
component, and system. The declaration is label metadata: it never enters a canonical hash, so it
never rewires compute for a valid model ([../verification/multi-consequence.md](../verification/multi-consequence.md)).

## 11. Reliability mode

`RiskAnalysisMode.Reliability` runs the same machinery for probability-only questions: annualized
failure probabilities, system response probabilities, and assurance — component validation relaxes
exactly the consequence-content requirements. Its flagship application is the NFIP levee
accreditation quantity, the **annual probability of inundation** [7] App. I: inundation occurs by
breach at any level or by overtopping without breach above the top-of-levee x_T,

```
API = Σᵢ P(xᵢ)·P(F|xᵢ)  +  Σ_{i ≥ x_T} P(xᵢ)·(1 − P(F|xᵢ)),                        (7)
```

the failure term running over all levels and the non-breach term above the threshold. The engine's
API and full-uncertainty assurance ensembles are verified against exact quadrature, the legacy
oracles, and the 2024 report's Table 104 pins [8] in
[../verification/nfip-assurance.md](../verification/nfip-assurance.md).

## 12. Risk profiles

The profile catalog reports risk *against hazard level* rather than integrated over it: cumulative
failure probabilities, cumulative expected consequences, and AEP-axis system response
probabilities, with normalized views, per component and risk type. Profiles localize the hazard
levels where failure probability or risk rises sharply, and under full uncertainty they carry
ensemble bands that localize *uncertainty* by hazard level. A component's profile axis is chosen
by the seed-inert `ProfileHazardElementId` selector (any hazard-signal node on the path — e.g.,
report profiles against stage rather than flow). The pushforward construction is exact at the
recorded knots and verified against independent dense quadrature in
[../verification/risk-profiles.md](../verification/risk-profiles.md).

## 13. Verification anchors

| Contract | Family |
|---|---|
| Bit-identical reproducibility; metadata inertness | [engine-reproducibility.md](../verification/engine-reproducibility.md) |
| Single-component mean parity vs the legacy oracle | [single-component-mean-parity.md](../verification/single-component-mean-parity.md) |
| Exact-LEC tails, σ, VaR/CVaR vs brute-force MC | [exact-lec-tail.md](../verification/exact-lec-tail.md) |
| Additive convolution and joint VEGAS vs event-level MC; the γ audit | [system-risk.md](../verification/system-risk.md) |
| The legacy system matrix + report tables 77–103 [8] | [system-risk-matrix.md](../verification/system-risk-matrix.md) |
| Method/dependency combos and dispositions | [risk-analysis-combos.md](../verification/risk-analysis-combos.md) |
| Reliability mode, API, Table 104, assurance ensembles | [nfip-assurance.md](../verification/nfip-assurance.md) |
| The declared consequence-type axis | [multi-consequence.md](../verification/multi-consequence.md) |
| Profiles vs independent dense quadrature | [risk-profiles.md](../verification/risk-profiles.md) |
