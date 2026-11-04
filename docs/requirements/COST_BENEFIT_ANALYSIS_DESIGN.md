# Cost-Benefit Analysis Design (C5)

**Status: v1.0, ratified 2026-09-06.** The normative design for the C5 `CostBenefitAnalysis`
capability — the alternatives-comparison, constrained discrete-optimization, and
decision-analysis layer over the shipped risk engine. Ratified by Haden Smith at the
2026-09-06 design review across four batched decision rounds plus one serialization
follow-up, including the twenty-two numbered decisions below. Six carry review revisions or
additions beyond the drafted recommendations: the default benefit stream is **Total**
(decision 4 — fail/non-fail trade-offs that Excess alone can miss); the **do-no-harm
screen** (decision 9, added); **standard deviation of risk as the declared secondary
dispersion objective** with a mean-variance study template (decisions 7/13, added);
**reliability-based design support on the annualized failure probability** (decision 17,
added); the study is a **formal serialized class** — definition XML in both serialization
modes, a hash-stripped `RiskAnalysis.Id`, and serialized study results (decision 18,
revised from the drafted runtime-only recommendation); and **four implementation sessions**
(decision 22, revised from three). Built from four parallel explorations (this repo; the
legacy v1.0 repo at `C:\GIT\RMC-TotalRisk-Dev`; `C:\GIT\numerics`; the published
methodological literature including the lead's 2022 thesis, read in full), all anchored to
file:line as of 2026-09-06 at commit `6f70a57` — line anchors drift with edits, so re-locate
by symbol when stale. Nothing here is implemented yet; sessions CB1–CB4 below execute it.
Where this document and [MODEL_LIBRARY_ARCHITECTURE.md](MODEL_LIBRARY_ARCHITECTURE.md)
disagree (§7's `CostBenefitAnalysis` sketch in particular), this document wins for the C5
scope; the formal arch-doc amendment lands at session CB4.

## Context

RMC-TotalRisk quantifies flood risk; C5 makes the risk **decidable**. The capability brief,
assembled from the 2026-08-27 program objectives and the 2026-09-06 design-review
directions:

- A `CostBenefitAnalysis` owns a **collection of `RiskReductionAlternative`s**. The user
  **designates which alternative is the baseline** (the existing condition). Each
  alternative contains a **`RiskAnalysis` and its associated cost**.
- The economics run on a **user-selected risk stream** — Total, Excess, or Fail — with
  flexibility and options throughout, and produce **NPV, net benefits, BCR, and cost per
  statistical life saved**, the **efficient frontier**, and **multi-objective**
  presentation.
- The optimization framework is the **ε-constraint method over the discrete, manually
  defined alternative array** — the user defines the alternatives to rank; no continuous
  nonlinear optimization in v1. **Mean-variance and mean-CVaR** orderings are supported, and
  the **Lagrange multipliers appear as shadow prices** — discretely, as Haimes total
  trade-off ratios between adjacent noninferior alternatives.
- The flagship formulation is **constrained net-benefits maximization**: maximize net
  benefits subject to excess life loss below the tolerable risk guideline and CVaR below an
  acceptable threshold — the thesis's Equation 4.2, which also satisfies the standing
  federal net-benefits (NED) selection rule under life-safety constraints.
- **All consequence types can optionally be monetized** (VSL-style per-type factors), and an
  optional **MCDA weighted-objective** presentation complements the frontier.
- **When every alternative has been run with full uncertainty, the epistemic decision-rule
  tier activates** — minimize maximum regret and the rest of the risk-aversion catalog. The
  design carries **all viable risk-based design strategies one should test as part of a
  dissertation or journal publication.**
- The study is a **formal, persistable document**: users save, reopen, and edit studies; the
  definition serializes as XML in both serialization modes and the results serialize as
  JSON.
- The results are **presentation-complete**: every table the future UI and desktop App need
  for tables and plots is a first-class, label/unit-echoed container. The library stays
  headless — no plotting, no UI types.
- The capability must be **full-featured and more robust than the competition** (iPresas,
  DAMRAE), and the v1.0 `PlanRow.EquivalentAnnual` math plus the TR Appendix H
  equivalent-annual math are preserved as the porting parity anchor.

Positioning. The methodological core is the lead's M.S. thesis (Smith 2022, Colorado School
of Mines) — CVaR-based risk-averse evaluation of flood-risk-management alternatives with the
ε-constraint method and trade-off (shadow-price) interpretation — generalized here from the
thesis's continuous levee-sizing case to the discrete alternative arrays practitioners
actually screen, exactly the discrete total-trade-off setting the thesis itself recommends
for modification studies. Above C5 sits the portfolio problem (sequencing across facilities
under capacity constraints — the PhD dissertation direction and the deferred C4 capability);
C5 is the single-study alternatives-evaluation engine beneath it. C5 composes with shipped
machinery only: the life-cycle trajectory query (C1), the configuration-clone discipline
(B8), the exposure-period conversions (A5), the measure/criterion vocabulary (`RiskMeasure`,
`RiskType`, `TolerableRiskCriterion`), the realization-weight carrier (A4), and the exact
logic-tree enumeration (C3). It invents no parallel engines.

## Exploration digest (facts)

### Current repo (v2.0) — the composition surfaces — explored 2026-09-06

Notation: `TR` = `src\RMC.TotalRisk`. Line anchors as of commit `6f70a57`.

**The life-cycle trajectory query (C1).** `RiskAnalysis.MeasureLifeCycleRisk(LifeCycleDefinition)`
(`TR\Analyses\RiskAnalysis.cs:3899`) — runtime-only, mean-only. Epochs = sortedDistinct({0} ∪
evaluation years ∪ intervention years), each epoch one mean-only engine run on throwaway
self-contained clones (`CloneComponentsForConfiguration` → `ApplyHouseEventStates` → chained
same-arity `ApplyHazardReplacements` → `ApplyEvaluationAges` → `RunConfigurationQuery`, the
B8 machinery called directly). Aggregates per consequence type in BOTH conventions:

- Non-absorbing (exposure-period-consistent): `cumulative += span·mean`;
  `presentValue += mean·[A(end) − A(start)]` with the annuity factor
  `A(y) = −Tools.Expm1(−y·Tools.Log1p(r))/r` (exact r → 0 limit y; `RiskAnalysis.cs:4269`);
  `EAC = PV/A(T)`; `P_T` accumulated in log space (`logSurvival += span·Log1p(−p)`).
- Absorbing (first-failure-terminates): each year weighted by the survival of every earlier
  year via per-epoch geometric closed forms (`survivalYears = −Expm1(span·Log1p(−p))/p`;
  `geometric = (1 − xˢᵖᵃⁿ)/(1 − x)`, `x = (1−p)/(1+r)`).

Containers (`TR\Results\`): `LifeCycleRiskResults` (PeriodYears, DiscountRate,
ConsequenceLabels/Units, Epochs, FailureProbabilityByHorizon, per-type Cumulative /
PresentValue / EquivalentAnnual + AbsorbingCumulative / AbsorbingPresentValue,
AppliedInterventions); `LifeCycleRiskResults.Epochs` rows `LifeCycleEpochRisk` (StartYear,
SpanYears, EndYear, EvaluationAge, CumulativeFailureProbability, System + per-component
`LifeCycleEpochEntry`, AppliedActions); `LifeCycleEpochEntry` (Name, FailureProbability,
ExpectedConsequences[type]). All unpersisted. **Two facts that drive this design:** (i)
`CreateLifeCycleEntry` (`RiskAnalysis.cs:4248`) reads `curves.Total.Mean` ONLY — the epoch
rows carry no Excess or Fail stream values; (ii) the epoch's `SystemRealization` is
discarded after the entry is built — no `Curve` measure (SD, CVaR, …) survives per epoch.

**Inputs (C1).** `LifeCycleDefinition(periodYears, discountRate = 0, evaluationYears?,
interventions?)`; `LifeCycleIntervention(year, houseEvents?, hazardReplacements?)` — carries
the NAMED conditional-exercise extension seat (real-options decision rules; deterministic
schedules today); `HazardReplacement(targetFunctionId, replacement)`;
`HouseEventState(functionId, nodeId, state)`. All runtime-only.

**The exposure-period conversions (A5).** `MeasureExposurePeriodRisk(periodYears,
discountRate = 0, riskType, componentIndex, failureModeIndex, consequenceType)`
(`RiskAnalysis.cs:3782`) — the full-uncertainty, weight-aware ensemble query returning
`ExposurePeriodRiskResults` with `ExposurePeriodInterval` (Lower/Median/Mean/Upper) slots
for P_T, cumulative, PV, and EAC; the EAC round trip is exact by construction, and the
stationary single-epoch life-cycle case reproduces it bit-for-bit (pinned by
`LifeCycleVerification`).

**The configuration query (B8).** `MeasureConfigurationRisk(IReadOnlyList<HouseEventState>)`
(`RiskAnalysis.cs:3578`) — twin mean-only clone runs; `ConfigurationRiskEntry` (baseline /
configured / change per type; ratio NaN at zero baseline) is the repo's existing
baseline-vs-alternative delta shape and the NaN-convention precedent.

**The measure vocabulary.** `Curve` (`TR\Results\Curve.cs`) carries per stream:
`TotalProbability` (on Fail = the annualized failure probability), `Mean` (the expected
annual consequence), `StandardDeviation`, `Skewness`/`Kurtosis`, `ConditionalMean`
(= Mean/TotalProbability), `ValueAtRisk` and `ConditionalValueAtRisk` at the analysis
exceedance level `RiskAnalysisOptions.Alpha` (default 0.01 — the 1% AEP; the CVaR is an
exact piecewise integral of the log-log LEC quantile), `ConsequenceThresholdProbability`,
`HazardThresholdProbability`, plus the LEC/profile arrays. The public, mutating
`Curves.ComputeRiskMeasures(consequenceThreshold, alpha, hazardThreshold)`
(`TR\Results\Curves.cs:148`) re-evaluates VaR/CVaR/threshold measures at any α from the
stored (thinned, `LECOutputLength`-long) LEC. `RiskMeasure` (`TR\Core\Enums\RiskMeasure.cs`)
is the ten-member append-only serialized measure vocabulary; `RiskType` the five-stream
vocabulary (Excess = the dam/levee-safety incremental measure; Total = Fail + NonFail);
`RiskMeasureOptions` gates the optional measures. `TolerableRiskCriterion`
(`TR\Analyses\TolerableRiskCriterion.cs`) = (Measure, RiskType, ConsequenceTypeIndex,
Threshold) — already the constraint shape — evaluated by the engine through the private
static pair `SelectScope`/`ExtractMeasure` (`RiskAnalysis.cs:4330/4410`), which support
every stream × scope × measure combination. **Reuse blocker:** both are `private static`,
so a new layer cannot reach the measure-resolution switches today.

**Epistemic levers.** A4 `RealizationWeights` (run-input weight vector; stored
authoritative copy on `EnsembleResults.RealizationWeights`); A6 `FractilePins`; A9
`RetainRealizations` / `RetainedIntegrationDetail` (every retained realization has had
`DumpMemory()` called — only the single detail realization keeps its risk-point ledger); C3
`LogicTreeEnumerationRealizations` + `LogicTreeEnumerationMap` (K branch combinations × M
realizations each, exact weight products published as the run's realization weights;
`CombinationOf`/`BranchIndexOf`/`RealizationWeight` assignment queries). `EnsembleSummary`
reduces to mean/median/two percentiles per measure plus convergence indicators — **it has
no epistemic variance and no epistemic tail-expectation member**; the per-realization
measure vectors and weights are public, so both are computable by a caller with new
arithmetic. A7 `TolerableRiskConfidence` = the weighted strict-exceedance fraction
P(measure > threshold) — the epistemic chance-constraint evaluator, already shipped.

**The analysis foundation.** `AnalysisBase` (`TR\Analyses\AnalysisBase.cs`) supplies
events, `IsEstimated`, the `IsRunning` single-run slot, cancellation, and progress
plumbing; the three abstracts are `RunAsync`, `ValidateIssues`, `Validate`. The base
carries no serialization surface; `RiskAnalysis` supplies its own options-only
`ToXElement` with components and results injected through constructors — the
consuming-layer pattern this design's serialized study follows (decision 18).

**Multi-consequence axis.** Primary type + declared `ConsequenceTypeDescriptor`s
(SpecifiedConsequence, ConsequenceUnit, ConsequenceThreshold); per-type expected annual
consequences read at `curves.Total.Mean` / `additionalCurves[t−1].Total.Mean`; the
blank-label wildcard comparability rule on `RiskAnalysis` path validation is the precedent
for cross-alternative axis matching. Life loss is a declared type like any other — a study
must say which position is life safety; nothing infers it from labels.

### Legacy v1.0 economics (explored 2026-09-06)

The ONLY cost-benefit computation v1.0 shipped is `PlanRow.EquivalentAnnual`, in the WPF app
(`RMC-TotalRisk-Dev\RMC-TotalRisk\RMC-TotalRisk\RMC-TotalRisk\Support\Risk Comparison\PlanRow.vb`;
UI `EquivalentAnnualConsequences.xaml(.vb)`, launched from the Tools menu). The engine and
the partial C# port contain none of it. Verbatim, complete:

```vb
Public Shared Function EquivalentAnnual(baseEAD As Double, baseYear As Int32, futureEAD As Double,
        futureYear As Int32, discountRate As Double, periodOfAnalysis As Int32) As Double
    Dim interpolatedValue As Double
    Dim year As Int32 = baseYear
    Dim totalPresentValue As Double
    For i As Int32 = 1 To periodOfAnalysis
        If year >= futureYear Then
            interpolatedValue = futureEAD
        Else
            interpolatedValue = LinearInterpolateFunction(year, baseYear, futureYear, baseEAD, futureEAD)
        End If
        Dim PV As Double = interpolatedValue * (1 + discountRate) ^ (-1 * i)
        totalPresentValue += PV
        year += 1
    Next
    Dim capitalRecoveryFactor As Double = discountRate * ((1 + discountRate) ^ periodOfAnalysis) /
        (((1 + discountRate) ^ periodOfAnalysis) - 1)
    Return capitalRecoveryFactor * totalPresentValue
End Function

Public Shared Function LinearInterpolateFunction(ByVal X As Double, ByVal X1 As Double,
        ByVal X2 As Double, ByVal Y1 As Double, ByVal Y2 As Double) As Double
    Return Y1 + (X - X1) / (X2 - X1) * (Y2 - Y1)
End Function
```

Facts a port must preserve or deliberately rule on:

- **End-of-year discounting with the base year discounted one full period** (iteration
  i = 1 while year = baseYear). Exactly n terms.
- Linear ramp from (baseYear, baseEAD) to (futureYear, futureEAD), then a plateau at
  futureEAD (`>=` guard).
- **r = 0 returns NaN**: the upstream guard blocks only `DiscountRate < 0`, and the CRF
  evaluates 0/0. The UI permitted 0.
- **futureYear beyond the horizon silently truncates the ramp** — the plateau is never
  reached; no warning. futureYear ≤ baseYear ⇒ the whole stream is futureEAD (which also
  shields the interpolator's zero-division).
- The rate arrives as percent and is divided by 100 at the call site; UI defaults 7% and a
  30-year period (the window's 30 overrides `PlanRow`'s 50); futureYear defaults
  baseYear + 50.
- EAD provenance: `MeanRiskResults.Curves.Total.Mean` per selected analysis, or a
  free-typed value ("User Entered_n") — the dialog worked as a standalone calculator.
- **The semantic is one condition at two points in time** — base and most-likely-future —
  not with-project vs without-project. There is no differencing, no benefit, no cost
  anywhere in v1.0; a `Marginal` (difference) column was drafted and cut before ship
  (commented code).
- No test coverage exists anywhere in the legacy suites; the parity oracle must be
  hand-computed.

**TR Appendix H** (the in-repo technical reference draft,
`docs/reports/RMC-TR-2022-XX - Quantitative Risk Analysis with RMC-TotalRisk - 07-31-23.docx`,
"Appendix H – Equivalent Annual Consequences", Equations 252–256) documents the same
computation from the HEC-FDA procedure: EqAC = CRF·TPV (252); CRF = r(1+r)ⁿ/((1+r)ⁿ−1)
(253); TPV = Σ_{i=b}^{b+n} PV_i (254); PV_i = FV_i·(1+r)^(−(i−b)) (255); FV_i = EAC_f for
i ≥ y_f, else EAC_b + (EAC_f − EAC_b)·(i − y_b)/(y_f − y_b) (256). **The document and the
shipped code disagree on the discounting index**: Eq. 255's exponent −(i−b) leaves the base
year undiscounted (and Eq. 254's bounds read as n+1 terms), while the code discounts the
base year one full period over exactly n terms. Decision 19 anchors the port to the code.

### The methodological base — explored 2026-09-06

**The thesis (primary source, read in full, open access).** C. Haden Smith, *Improving the
Economic Evaluation of Flood Risk Management Studies*, M.S. (Mineral and Energy Economics),
Colorado School of Mines, 2022. https://hdl.handle.net/11124/15369. The extracted
formulary, in the thesis's numbering:

- EAC = ∫ C(x)·f(C(x))·dx (2.1); discrete failure risk EAC_F = Σ P(eᵢ)·P(F|eᵢ)·C_F(eᵢ)
  (2.2).
- max NPV = Σ_{t=0}^{T} (B_t − C_t)/(1+r)ᵗ (2.3) — defined, not numerically optimized
  there.
- **TEAC = FC·CRF + E[C]** (2.4, the primary objective; Mays 2001), CRF as above (2.5); the
  thesis notes minimizing TEAC and maximizing net benefits "will lead to the same optimal
  design level."
- NED-style decomposition NB = B_L + B_I + (E[C_without] − E[C_with]) − TC (2.6).
- **CSSL = (AC − (E[C_without] − E[C_with])) / (E[L_without] − E[L_with])** (2.8, from ER
  1110-2-1156; negative values taken as zero).
- CVaR: CVaR_α(X) = (1/(1−α))·∫_β^∞ x f(x) dx with β the α-quantile (2.9–2.11); the
  flood-specific App. A.15 form divides by the *exceedance* probability. **The two chapters
  use α in opposite senses (confidence level vs exceedance probability); decision 8 pins
  the exceedance reading.** Case-study level: the 1% AEP (100-year).
- **Conditional incremental value-at-risk N_Δ = E[C_Δ | e ≥ e_c] = EAC_Δ/f_Δ**
  (2.12 = A.14 = 4.1), with C_Δ = C_F − C_NF.
- The ε-constraint program min f₁ s.t. fⱼ ≤ εⱼ (2.13/B.6; Haimes, Lasdon & Wismer 1971),
  the trade-off/shadow price **λᵢⱼ = −∂fᵢ/∂fⱼ** (B.7), the Lagrangian/KKT development
  (B.3–B.5, B.11–B.16), and Algorithm 5 (sweep ε by a tightening decrement until
  infeasibility; inner solver = augmented Lagrangian around Differential Evolution).
  Interpretive anchor: "In economics, the Lagrangian multiplier represents the marginal
  cost of the constraint, which is also referred to as the shadow price"; "Positive
  Lagrangian multipliers correspond to the noninferior set of solutions. The set of nonzero
  multipliers represents the set of trade-off ratios."
- **Equation 4.2 — the dam/levee-safety recommendation this design implements
  discretely:** `min TEAC s.t. EAC_Δ ≤ 0.001 lives/yr (the tolerable risk guideline),
  N_Δ ≤ ε₃`, with the Pareto frontier generated by varying ε₃.
- PMRM (Asbeck & Haimes 1984): partitioned conditional expectations f₂/f₃/f₄ over declared
  probability ranges plus f₅ = E[C] (2.14–2.15); the thesis positions its CVaR framework as
  the low-probability/high-consequence special case.
- Worked fixtures this design adopts: **Table B.1** (the ten-row noninferior set of the
  Haimes example — min f₁ = (x₁−2)² + (x₂−4)² + 5 s.t. f₂ = (x₁−6)² + (x₂−10)² + 6 ≤ ε₂ —
  with analytic multipliers; internally consistent, closed-form checkable via
  λ = −(x₁−2)/(x₁−6), x₁(λ) = (2+6λ)/(1+λ), x₂(λ) = (4+10λ)/(1+λ));
  **Table 2.1/2.2** (four hypothetical options with IDENTICAL expected life loss of 10
  lives/yr and CVaR at α = 0.999 of {30, 67, 149, 577} — the "CVaR discriminates where the
  expected value cannot" demonstration); the Chapter 3 levee case (Tables 3.1–3.6, incl.
  the TEAC-vs-CVaR frontier Table 3.4 with λ column).
- Honest corrections this document records rather than smooths over: the thesis contains
  **no mean-variance formulation** (one citation-level PMRM remark — the review nonetheless
  ruled mean-variance in, on the shipped `StandardDeviation` measure); Eq. 4.2 is a
  Chapter 4 recommendation, not an implemented study; no $/life shadow price is derived
  (the case-study λ is $/$); Table 3.4 rows 2–5 violate the printed crest-width bound; and
  the thesis itself concedes that for real modification studies practitioners screen
  "fewer than a dozen discrete modification alternatives," for which it recommends Haimes'
  **total trade-off** concept for discrete problems — precisely this design's setting, by
  the 2026-09-06 ruling.

**The numerics repo (read-only).** No ε-constraint, Pareto, or multi-objective class
exists. The "2-objective version" is the constrained-optimization template:
`AugmentedLagrange` (`Numerics.Mathematics.Optimization`; Birgin–Martínez augmented
Lagrangian exposing the multipliers `Lambda`/`Mu`/`Nu`) exercised by `Test_Haimes_5_2`
(min f₁ s.t. f₂ ≤ 13.31 → pins x = (4.5, 7.75), f₁ = 25.31, **Mu[0] = 1.67** — the shadow
price at ε = 13.31) and two water-economics tests pinning net-benefit multipliers.
`UnivariateDistributionBase.ConditionalExpectedValue(alpha)` is the distribution-level
CVaR; there is no sample-CVaR helper and no LP/QP machinery. **Under the discrete ruling,
C5 v1 needs no numerics work**; the continuous design-variable stack (ε-sweep →
`AugmentedLagrange` → `DifferentialEvolution`, the thesis Chapter 3 pipeline) is the
recorded parameterized-alternative extension.

**Standards.** USACE ER 1110-2-1156 (2011), Appendix L, verbatim algebra:
`CSSL(U) = C_A / (E[L:e] − E[L:pr])`;
`CSSL(A) = [C_A − (E[R:e] − E[R:pr]) − (O:e − O:pr)] / (E[L:e] − E[L:pr])`, "with the
proviso that a negative value is taken as zero"; C_A = ANNUALIZED implementation cost; all
terms on incremental consequences. Disproportionality ratio = CSSL/WTP (¶5.3.8; WTP from
USDOT — $5.8M ± $2.6M at the 2009 vintage, updated over time — configurable, never
hard-coded); ALARP justification bands Very Strong / Strong / Moderate / Poor at ratio
thresholds {1, 4, 20} for risks just below the tolerable limit and {0.3, 1, 6} for risks
just above broadly acceptable (Tables 5.1/5.2). The shared USACE/USBR/FERC tolerable risk
guideline: incremental expected annual life loss 0.001 lives/yr. ER 1105-2-100 / the
2013–2014 PR&G codify the federal selection rule as **net-benefits maximization subject to
constraints** (the NED plan), which Eq. 4.2 instantiates with life-safety constraints.
HEC-FDA's guardrail: damage reduced is computed as EAD-reduced AND EqAD-reduced, and
**only the equivalent-annual form may be compared against equivalent annual cost** for net
benefits and BCR. Reclamation's 2022 Public Protection Guidelines publish **no numeric
CSLS threshold and no VSL** — chart portrayal and ALARP are presentation- and
process-level.

**Competing software.** iPresas (Calc + NIRMAN): the indicator family CSLS ≡ CSSL(U),
ACSLS ≡ CSSL(A) without the operating-cost term, **EWACSLS = ACSLS / [max(rᵢ:e, IRL) /
max(rᵢ:pr, IRL)]ⁿ** (Serrano-Lombillo et al. 2016; IRL = 10⁻⁴/yr per USACE; n ∈
[0.1, 10], equity-vs-efficiency weight, default 1 = equilibrium), CSFP (cost per
statistical failure prevented), economic CBR; greedy re-baselining prioritization
sequences, variation curves, and CTB closeness-to-best indexes (portfolio-level — C4
territory, recorded not shipped). Fluixá-Sanmartín et al. 2020's time-dependent **AACSLS**
is built on survival-weighted discounted risk streams that are **algebraically the shipped
C1 absorbing aggregates**, plus discounted cost streams with implementation lag — native
here. DAMRAE/DAMRAE-U: an event-tree engine with a thin "cost effectiveness of risk
reduction alternatives" layer and uncertainty propagation; no published indicator family,
discounting machinery, or frontier support. Methodological anchors the document cites:
Haimes, Lasdon & Wismer (1971); Haimes (2004); Rockafellar & Uryasev (2000, 2002) — the
CVaR auxiliary-function results (VaR falls out of CVaR minimization; joint convexity; the
sampling form F̃_β = α + (1/(q(1−β)))·Σ[f − α]⁺); Artzner et al. (1999) coherence; Savage
(1951) minimax regret; Mavrotas (2009) AUGMECON (abstract-level only — primary source not
yet read; flagged). Could-not-verify list retained from the exploration record: the 2022
revision of ER 1110-2-1156 (its CSSL temporal-guidance companion document unlocated),
DAMRAE's exact metric set, Miettinen's theorem statements, Mavrotas's primary formulation.

## Ratified decisions (2026-09-06)

Ratified by Haden Smith at the 2026-09-06 design review (four batched rounds plus a
serialization follow-up). Decisions 4, 7, 9, 13, 17, 18, and 22 carry review revisions or
additions beyond the drafted recommendations; decision 6 is infrastructural and was
ratified through the framework decisions that depend on it.

1. **The alternative model: a collection with a designated baseline.** `CostBenefitAnalysis`
   owns `Alternatives` (an observable collection of `RiskReductionAlternative`); the user
   MUST designate one member as the baseline/existing condition (`Baseline` reference
   property; Validate Error when unset or not a collection member). An alternative =
   Name/Description + a REQUIRED `RiskAnalysis` (alternatives may share one instance —
   e.g. the baseline system carrying different plans) + a `CostStream` (empty default — the
   typical baseline) + an optional `LifeCyclePlan` overlay (interventions + extra
   evaluation years; NO rate, NO horizon — decision 2). The baseline is comparison row zero
   and anchors every delta; deltas are signed (reduction = baseline − alternative; positive
   good), never clamped except where App. L says (decision 7).
2. **The study owns time and money conventions; comparability is validated, not assumed.**
   One `PeriodYears` and one `DiscountRate` per study on `CostBenefitOptions`; plans carry
   neither, so mismatch is unrepresentable; a different horizon is a different study.
   Trajectories all run on the **study-wide epoch grid** — {0} ∪ study evaluation years ∪
   every alternative's plan years — so deterioration re-ages at identical boundaries for
   every alternative (without deteriorating responses the grid is provably inert: the
   annuity segments telescope; pinned by fixture). Cross-alternative comparability
   validation: consequence-type axes must align by position (label/unit equality with the
   engine's blank-wildcard rule; count mismatch is an Error); measure-relevant option
   mismatches across alternatives (Alpha, ConsequenceThreshold, RiskMeasures flags) are
   Warnings with the study-α re-evaluation route (decision 6) as the remedy; a running
   baseline or alternative refuses `RunAsync`. Discount-rate sensitivity is recorded as
   re-invocation (correct by construction), not a v1 feature.
3. **The cost model: three tagged stream kinds under the trajectory's own discounting
   conventions.** `CostStream` = capital entries (year, amount, label), O&M segments
   (startYear, annualAmount, endYear?, label), and operating-change segments (same shape) —
   the separate third kind exists because ER 1110-2-1156 App. L subtracts the
   operating-cost delta in CSSL(A)'s numerator while C_A carries capital + O&M; folding
   them together would double-count. Conventions, pinned to the epoch algebra and the v1.0
   anchor: dated capital at year k discounts by (1+r)^(−k) — **year-0 capital is
   undiscounted**; recurring segments accrue over exposure years (s, e] with
   PV = a·[A(e) − A(s)] — exactly the shipped annuity segments, so an intervention at year
   k pairs a (1+r)^(−k) capital factor with benefits whose first term is (1+r)^(−(k+1)),
   term-for-term consistent with `MeasureLifeCycleRisk` and with the v1.0 i = 1..n stream.
   Signed amounts are legal (salvage/residual credits). **Costs are never survival-weighted
   in v1** (commitments — the AACSLS reading); a survival-weighted cost variant is a
   recorded extension, as are IDC, price-level escalation, and measure-lifespan
   annualization (renewal capital entries subsume lifespans and are strictly more
   expressive).
4. **The benefit stream axis: per-stream aggregates with Total as the default (revised at
   review).** The life-cycle containers gain the stream axis {Total, Excess, Fail}
   (library extension, CB1): `LifeCycleEpochEntry` carries per-stream expected
   consequences, and `LifeCycleRiskResults` carries the parallel aggregate arrays per
   stream, produced by the SAME aggregation loop parameterized over stream — one
   aggregation authority, appended optional ctor parameters on unpersisted containers
   (contract-legal; existing fixtures bit-exact; Background/NonFail recorded as future
   streams). The study's `BenefitRiskType` selects the stream the headline economics
   (NPV/net benefits/BCR/TEAC) difference — **default Total** (Haden's review ruling:
   Total is the most robust because it accounts for trade-offs between fail and non-fail
   consequences that Excess alone can miss), with Excess (the incremental dam/levee-safety
   frame) and Fail selectable. The CSLS family ALWAYS reads Excess regardless of the study
   default — App. L is an incremental-consequence definition, not a convention.
5. **The headline convention: NonAbsorbing, with the absorbing family always alongside.**
   Both conventions are always computed for every stream and type. The headline
   NPV/net-benefits/BCR read the study's `Accounting` selection — **default NonAbsorbing**:
   it is continuous with the shipped default aggregates, the A5 stationary bridge, the
   v1.0 EqAD anchor, and the federal planning currency (HEC-FDA EqAD), and at dam-safety
   AFPs the absorbing correction is second-order (the survival weight is 1 − O(P_T)). The
   absorbing fields sit in the same row (the first-failure-terminates reading), and AACSLS
   is the absorbing member of the metric family. Ratio discipline (the FDA guardrail,
   generalized): **every ratio takes numerator and denominator from the same convention
   and the same annualization basis.** With that rule the whole CSSL family is
   basis-invariant (PV-form and EqA-form quotients are identical because A(T) cancels and
   max(0,·) commutes with positive scaling) — the lemma is documented and pinned by
   fixture, and it defuses the App.-L-annualizes-over-the-period vs
   iPresas-annualizes-over-lifespan dispute: C_A ≡ PV(capital + O&M)/A(T), one basis,
   echoed.
6. **Epoch measure access: opt-in realization retention + study-α re-evaluation.**
   `LifeCycleEpochRisk` gains an opt-in retained mean `SystemRealization`
   (definition-level flag; studies default it on — measures need it; byte-inert when off),
   so every `Curve` measure (SD, CVaR, ConditionalMean, threshold probabilities) is
   readable per epoch and per stream. Measures at levels other than each run's own α are
   produced through the PUBLIC `Curves.ComputeRiskMeasures(threshold, α, hazardThreshold)`
   on the retained realization's curves — the documented thinned-LEC precision route —
   under the study's declared α list (multi-α supported; the study Warns when a declared α
   differs from an alternative's run α and re-evaluates uniformly). Constraint evaluation
   scopes: EveryEpoch (default for annualized measures — the TRG reading "in every
   exposure year"), FirstEpoch, and Horizon (the aggregate basis metrics).
7. **The metric formulary (per alternative, vs the designated baseline; extended at
   review).** PV cost (capital/O&M/operating split echoed), EAC = PV/A(T), cumulative
   cost; per stream × type × convention: ΔPV, ΔEqA, ΔCumulative; **TEAC = EAC_cost +
   EAD(selected stream)** (thesis Eq. 2.4; the min-TEAC ≡ max-net-benefits identity
   documented); NPV = monetized PV benefit − PV total cost; net annual benefit = NPV/A(T);
   BCR = monetized PV benefit / PV total cost; **CSSL(U) and CSSL(A) exactly per App. L**
   including the negative-numerator→zero clamp and the operating-cost term, on Excess life
   loss; **EWACSLS** with IRL (default 10⁻⁴/yr), equity exponent n (default 1), and
   individual risk defaulting to the survival-equivalent annual failure probability proxy
   p_eq = 1 − (1 − P_T)^(1/T) with explicit per-study override seats (the proxy is the
   published case-study convention; echoed); **CSFP** = C_A/Δp_eq (+ the year-0 ΔAFP
   echo); **AACSLS** = the absorbing-basis adjusted ratio (clamped like CSSL(A) for family
   consistency — a documented choice, the source does not state one); disproportionality
   ratio + ALARP band echo (decision 11). **Review additions:** the **annualized failure
   probability (AFP) level and reduction are first-class metrics** (reliability-based
   design, decision 17), and the **standard deviation of annual risk is the declared
   secondary dispersion objective** — it joins the default objective vector so the default
   frontier is mean-variance-capable out of the box. NaN conventions: any ratio with
   denominator ≤ 0 is NaN (cost per life saved is undefined when no lives are saved —
   never negative, never clamped); the clamp applies to the CSSL(A)/AACSLS numerator only;
   an empty monetized set makes NPV/net-benefits/BCR NaN with a Warning (zero would assert
   "no benefits"; NaN says "not a monetary question").
8. **Thesis mappings and the α convention pin.** EAC_Δ = the Excess stream Mean;
   N_Δ = the Excess stream ConditionalMean (E[C_Δ | failure] — the failure-conditioned
   reading of Eq. 2.12's threshold form; both the exceedance-threshold CVaR and the
   failure-conditioned N_Δ are available, and the study says which a constraint uses);
   CVaR_α = the stream's ConditionalValueAtRisk. **α is pinned as the EXCEEDANCE
   probability** (α = 0.01 is the 100-year level) — the `RiskAnalysisOptions.Alpha`
   semantics — resolving the thesis's Ch. 2 (confidence) vs App. A (exceedance) notational
   split in favor of the implemented convention; the document states the mapping to both
   notations once, loudly.
9. **The do-no-harm screen (added at review).** Total risk cannot increase from the
   baseline: for every declared consequence type, the alternative's Total-stream
   equivalent-annual expected consequence must not exceed the baseline's (headline
   accounting; signed reduction ≥ 0). An alternative failing the screen is flagged
   (`FailsDoNoHarm`, with the offending types named) and **excluded from every strategy
   recommendation by default**; it remains in all tables, visibly marked. A study policy
   relaxes the screen (`DoNoHarmPolicy`: Enforce | WarnOnly | Off; default Enforce). The
   screen is a display-and-recommendation gate, never a mutation of any metric.
10. **Monetization policy: opt-in, split-accounted, never defaulted for life safety.**
    `ConsequenceMonetization` = per-type factors (type position, $/unit, label, vintage) +
    the study monetary unit; a type whose declared unit equals the monetary unit is
    identity-monetized at factor 1 (a factor on it is refused — one price level); the
    life-safety type is DECLARED by position (`LifeSafetyConsequenceType`; −1 = the CSLS
    family and lives-saved metrics are skipped). Two benefit aggregates are always
    maintained: the ECONOMIC PV benefit (monetized reductions excluding the life-safety
    type — always) and the MONETIZED PV benefit (the full monetized set). CSSL(A)'s
    numerator adjustment reads the economic aggregate only (App. L's definition — lives
    never appear in numerator and denominator); NPV/BCR read the monetized aggregate.
    Monetizing the life-safety type is legal (the VSL option) and emits exactly one
    Warning documenting the accounting: monetized lives enter NPV/BCR; the adjusted CSSL
    stays economic-only; NPV > 0 and the ALARP ratio are not independent evidence when the
    VSL differs from the WTP. **No WTP or VSL number ships as a default** — absent WTP
    (NaN) skips the disproportionality/ALARP block; every supplied value carries a vintage
    string into the results echo.
11. **Disproportionality and ALARP.** Ratio = CSSL(A)/WTP (the ER's cost-adjusted decision
    reading; the parts are echoed so CSSL(U)/WTP is recomposable); the band table is
    selected by a declared `AlarpProximity` (JustBelowTolerableLimit → {1, 4, 20};
    JustAboveBroadlyAcceptable → {0.3, 1, 6}) with a threshold-override seat; the echo is
    a labeled band (Very Strong/Strong/Moderate/Poor), never a pass/fail verdict — chart
    placement and process conclusions stay presentation-layer.
12. **The objective/constraint vocabulary.** A `CostBenefitMetric` selector is either (a)
    a risk-measure selector — `RiskMeasure` × `RiskType` × consequence type × basis
    (AnnualizedPerEpoch | HorizonPresentValue | HorizonEquivalentAnnual |
    HorizonCumulative) × accounting (NonAbsorbing | Absorbing) × form (Level |
    ReductionVsBaseline) × α (for the tail measures) — or (b) an economics metric (the
    decision-7 list incl. AFP). Objectives = (name, metric, direction). Constraints =
    (metric, ≤/≥, threshold, scope per decision 6). The shapes deliberately mirror
    `TolerableRiskCriterion` and the numerics `Constraint`/`ConstraintType` vocabulary.
    Mean-variance and mean-CVaR orderings are declared objective pairs (Mean vs
    StandardDeviation; TEAC or Mean vs ConditionalValueAtRisk) — no special machinery.
13. **The discrete ε-constraint study (templates extended at review).** Inputs: the
    primary objective, the ε objective, the ε grid (an explicit list, or auto: a uniform
    grid across the feasible alternatives' payoff range — the thesis Algorithm 5 grid; the
    Mavrotas payoff-table refinement recorded), and the fixed-constraint list. Per ε:
    filter the alternatives on the fixed constraints and the ε constraint, select the
    argmin/argmax of the primary; outputs: the per-ε selection table, the noninferior
    (non-dominated within the swept pair) set, **total trade-off ratios
    λ̂ᵢⱼ = −Δfᵢ/Δfⱼ between ADJACENT noninferior alternatives — the discrete shadow
    prices** (Haimes' total trade-off, the thesis's own recommendation for screened
    discrete alternatives), binding-constraint flags, and infeasible-ε diagnostics. **The
    unification this design states formally: the incremental cost-effectiveness ratios of
    USACE planning practice (incremental BCR, incremental CSSL between successive
    non-dominated alternatives) ARE total trade-off ratios** — CE/ICA and the ε-constraint
    dual view produce one table. THREE templates ship as convenience constructors: the
    **Eq. 4.2 study** (min TEAC s.t. Excess annualized life loss ≤ TRG — the 0.001
    lives/yr guideline as the template's DEFAULT ARGUMENT, never a hard-coded policy
    constant — with a declared tail constraint, N_Δ or CVaR_α, swept over ε); the
    **mean-variance study** (min mean risk s.t. standard deviation ≤ ε); and the
    **reliability study** (min cost s.t. AFP ≤ ε, e.g. the 10⁻⁴ guideline).
14. **The frontier and MCDA.** The k-objective Pareto screen over the declared objective
    vector: weak dominance; exact ties both kept; a NaN objective value excludes the
    alternative with an `IsExcludedForNaN` flag; default 2-D projections (PV cost vs
    monetized PV benefit; EAC vs annualized lives saved) always emitted; the incremental
    CE/ICA table runs along the cost-ranked non-dominated set. MCDA = opt-in weighted-sum
    over min-max-normalized objective values (weights positive finite, internally
    normalized and echoed; a constant objective contributes zero to every score rather
    than poisoning the normalization; NaN excludes as above; direction-aware
    normalization). The document records the standard caveat pair: the ε-constraint method
    reaches non-convex Pareto regions that weighted sums cannot; TOPSIS/outranking methods
    are recorded, not shipped.
15. **The decision-strategy catalog: all three tiers in v1.** The design formalizes the
    two-layer decision problem — the aleatory annual-loss distribution per state of
    knowledge, and the epistemic distribution over states — and ships the catalog in three
    tiers:
    - **Tier 1 (always; mean-only, exact twin deltas):** expected value/EAD (the federal
      baseline); TEAC/NED; BCR; the CSSL cost-effectiveness ordering; **reliability
      ranking on AFP**; aleatory mean-variance and mean-CVaR screens; **PMRM partition
      table** (conditional expectations over declared LEC probability partitions — CVaR is
      the LPHC special case); expected utility / certainty-equivalent ranking under a
      declared utility (Exponential/CARA or Power/CRRA — quadrature over the LEC);
      aleatory FSD/SSD dominance screening between annual-loss exceedance curves; the
      ε-constraint study and constrained selection (Eq. 4.2).
    - **Tier 2 (every alternative carries a stored full-uncertainty ensemble; post-hoc,
      weight-aware, no state alignment):** epistemic bands/variance/**epistemic CVaR** of
      any declared measure (new tail-averaging arithmetic over the public per-realization
      vectors + A4 weights); Laplace (the weighted epistemic mean), Wald maximin
      (worst-credible at a declared percentile or the sample extreme), maximax, Hurwicz
      α-blend, mean + k·σ; epistemic-CDF stochastic dominance; **chance-constrained
      selection** — constraints evaluated as satisfaction probabilities exactly the A7
      `TolerableRiskConfidence` way (weighted strict-exceedance fractions) against
      declared confidence levels; quantile regret (regret between epistemic quantile
      functions — documented as a different object from the quantile of regrets).
    - **Tier 3 (alternatives enumerated by the C3 logic tree over SHARED epistemic axes
      with bitwise-equal branch weights):** the K branch combinations are an exact common
      state space (per-realization pairing is impossible under content seeding; branch
      combinations are shared states) → the **Savage minimax-regret matrix** (state ×
      alternative, per-combination block means with documented k·SE/√M block noise),
      expected regret under the exact branch weights, and per-state winner/dominance
      counts.
    Every strategy emits a ranking table; the **decision summary** cross-tabulates
    strategy × recommended alternative — the dissertation/journal comparison table. Tier
    2/3 blocks publish null with named per-alternative diagnostics when preconditions are
    unmet; everything is post-processing arithmetic over already-computed outputs.
16. **The uncertainty discipline (honesty clauses).** Mean-only trajectory deltas are
    exact (deterministic twins — the headline). Tier-1 tail measures are ALEATORY,
    computed from each alternative's mean LEC — stated on every output. Per-alternative
    A5 stationary intervals are echoed when available (labeled per-analysis marginal, not
    a delta distribution). Epistemic variance/CVaR are new arithmetic over public ensemble
    state. Per-realization pairing across alternatives is IMPOSSIBLE under content-based
    seeding (changed content re-rolls streams); the design never presents
    independent-ensemble deltas as paired — distributional NPV/BCR is a recorded extension
    with the honest routes written down (Tier-3 per-state deltas at block-noise k·SE/√M;
    independent ensembles differenced at documented k·SE; shared-epistemic-variable
    partial pairing under a common explicit PRNGSeed pairs only content-unchanged
    functions).
17. **Reliability-based design support (added at review).** The annualized failure
    probability is a first-class decision axis: AFP level and reduction metrics, AFP
    constraints (e.g. the 10⁻⁴ individual-risk-surrogate guideline), the reliability ε
    template (decision 13), CSFP as the reliability cost-effectiveness ratio, and the
    (cost, ΔAFP) frontier projection. **Reliability-mode alternatives are legal**: a study
    whose alternatives all run `RiskAnalysisMode.Reliability` computes the
    reliability-compatible subset (AFP metrics, costs, CSFP, frontier, ε studies on AFP)
    while consequence-dependent metrics report NaN with named diagnostics. Mixing
    reliability-mode and consequence-mode alternatives in one study is a Validate Error
    (the axes are not comparable).
18. **The study is a formal serialized class (revised at review).** `CostBenefitAnalysis :
    AnalysisBase` serializes its full DEFINITION as XML — options (all declarations),
    costs, plans, monetization, the baseline designation, and the alternatives — with each
    alternative's `RiskAnalysis` handled through the existing `RiskSerializationMode`
    pattern: **SelfContained** embeds the analysis (options + components) inline for a
    portable, headless-complete document; **ByReference** writes a link marker the
    consuming layer re-attaches to the live stored analysis on load (id-first, lenient
    name fallback, load diagnostics on a miss, unresolved markers re-written verbatim).
    To make links robust, **`RiskAnalysis` gains an `Id` Guid — serialized but STRIPPED
    from canonical hashing exactly like `IRiskFunction.Id`**, so no hash, seed, or byte
    gate moves (proven at CB4). **Study results serialize too (review ruling)**: the
    results table catalog persists as System.Text.Json (`ToJson`/`FromJson` + compressed
    bytes, the shared `ResultsJson` conventions; append-only, null-suppressed optional
    blocks), and the results-injection constructor restores an estimated study — while the
    per-alternative deep drill-down objects (`LifeCycleRiskResults`, retained epoch
    realizations, LEC overlays) remain runtime references (`[JsonIgnore]`) regenerated by
    a re-run. The study itself never enters any canonical-hash or seed surface — its
    declarations are post-processing, not compute content. Serialized shapes are
    append-only forever from the first landing.
19. **The v1.0 parity anchor and the TR App. H discrepancy.** A public static
    `PlanEconomics.EquivalentAnnualConsequences(baseEac, baseYear, futureEac, futureYear,
    discountRate, periodYears)` reproduces the v1.0 CODE exactly for r > 0 — end-of-year
    discounting with the base year discounted one full period over exactly n terms, the
    ramp-then-plateau stream, INCLUDING the silent beyond-horizon ramp truncation and the
    futureYear ≤ baseYear constant-stream behavior (both documented loudly in XML
    remarks). r = 0 returns the exact limit (the plain average of the year values) — a
    documented deliberate improvement over v1.0's NaN, consistent with the shipped
    `AnnuityFactor` limit; r < 0 and periodYears < 1 throw. **The TR Appendix H convention
    (base year undiscounted, Σ over b..b+n) differs from the shipped code; the port
    anchors to the CODE (ruled at review — the reference-results reading of the porting
    rule), and the TR discrepancy is recorded in the XML remarks and the tech-ref
    chapter** — with a convention-enum seat recorded if a future need for the document's
    convention materializes. Hand-computed oracle constants (independently re-derived
    2026-09-06): base 100 @ 0 → future 160 @ 2, r = 0.1, n = 4 ⇒ EA = 134.970911441500
    (PV 427.839628440680, CRF 0.315470803706098); futureYear 10 (truncation) ⇒
    EA = 108.287007110536; futureYear 0 (constant stream) ⇒ EA = 160 analytically;
    100 @ 2026 → 200 @ 2076 at 7%/30 ⇒ EA = 119.497368419047; r = 0 ⇒ EA = 137.5 exactly.
20. **Results are the UI/App presentation contract.** All results are sealed containers
    with every input convention echoed (rate, horizon, grid, stream, accounting, α list,
    monetization map + vintages, WTP + vintage, IRL/n, thresholds, do-no-harm policy),
    organized as the table catalog in Architecture G: study conventions; per-alternative
    economics; per stream/type/convention reductions; trajectory time series; the ε-sweep
    table (the Table B.1/3.4 shape); frontier + incremental CE/ICA; MCDA; the strategy
    ranking tables + the decision summary; the regret matrix; the epistemic band table;
    the chance-constraint satisfaction table; pairwise dominance verdicts. Plot-ready
    series are numeric arrays; LEC overlays stay by-reference to each alternative's own
    published results. Real-option seats (exercise-probability columns) are named now so
    the conditional-exercise extension lands without breaking shapes. The table payload is
    the serialized results body (decision 18).
21. **Deferrals (recorded, not designed here).** Continuous design-variable optimization
    (the thesis Ch. 3 stack on numerics `AugmentedLagrange` + `DifferentialEvolution`; the
    parameterized-alternative reading); greedy re-baselining prioritization sequences,
    variation curves, and CTB indexes (portfolio-level → C4); distributional NPV/BCR (the
    decision-16 routes); survival-weighted costs, IDC, escalation, measure-lifespan
    annualization (decision 3); discount-rate sensitivity re-invocation helpers;
    TOPSIS/outranking; info-gap robustness; a numerics sample-CVaR helper (ask-first
    upstream item); the conditional-exercise (real-option) valuation on the named C1 seat;
    the AUGMECON grid refinement; richer stored-results variants (deep per-epoch payload
    persistence).
22. **Implementation slicing: four sessions, then the campaign (revised at review).**
    CB1 = kernel + model + parity; CB2 = the decision framework; CB3 = the strategy
    catalog; CB4 = the serialized study document + presentation closure + docs (the
    Implementation phases table). Every session closes whole (build 0 warnings → fast
    suite → `CostBenefitVerification` isolated → docs validator → **all eight byte gates
    bit-identical** → traceability → matrix/PROGRESS → commit). **The exhaustive testing
    campaign follows CB4** — the ruled critical path.

## Requirements

Functional requirements (traceable — architecture sections cite these as R1…R42):

**The study and alternatives:**
- R1. A user can create a `CostBenefitAnalysis` from `CostBenefitOptions`, add
  `RiskReductionAlternative`s (name + `RiskAnalysis` + `CostStream` + optional
  `LifeCyclePlan`), designate the baseline, validate with structured messages, run
  asynchronously with progress/cancellation, and read complete results.
- R2. Alternatives may share a `RiskAnalysis` instance; the baseline is any designated
  member; every published author object is byte-untouched by a study run.
- R3. The study refuses to run when unvalidated preconditions exist: no baseline
  designated, horizon violations, axis mismatches, mixed analysis modes, running
  analyses, malformed weights/factors.
- R4. All trajectory evaluations run mean-only on the study-wide epoch grid; grid
  inertness without deterioration and re-aging alignment with it are testable properties.

**Costs and discounting:**
- R5. Cost streams carry dated capital, O&M segments, and operating-change segments;
  signed amounts; (s, e] accrual; year-0 capital undiscounted; PV/EAC under the study rate
  via the shipped annuity expressions.
- R6. `PlanEconomics.EquivalentAnnualConsequences` reproduces the v1.0 reference results
  for r > 0 and the exact r = 0 limit; the TR App. H discrepancy is documented at the API.

**Benefits, streams, and conventions:**
- R7. The life-cycle trajectory exposes per-stream ({Total, Excess, Fail}) per-type epoch
  values and horizon aggregates in both accounting conventions; existing single-stream
  behavior is bit-unchanged.
- R8. Benefits are signed per-type reductions vs the designated baseline, available per
  stream, basis, and convention; the headline stream and accounting are study options
  (defaults Total, NonAbsorbing) echoed everywhere.
- R9. Every ratio metric draws numerator and denominator from one convention and one
  basis; the basis-invariance lemma holds bit-exactly in tests.

**Metrics:**
- R10. NPV, net annual benefit, and BCR compute from the monetized benefit aggregate and
  total PV cost, NaN when the monetized set is empty.
- R11. CSSL(U)/CSSL(A) match ER 1110-2-1156 App. L exactly (Excess life loss; the clamp;
  the operating-cost term); EWACSLS, CSFP, and AACSLS match their cited definitions with
  declared IRL/n/individual-risk seats and documented NaN/clamp conventions.
- R12. TEAC = EAC cost + selected-stream EAD; the TEAC/net-benefits equivalence is
  documented and tested (argmin TEAC = argmax NB on any fixed alternative set with shared
  monetization).
- R13. The disproportionality ratio and ALARP band echo compute only when WTP is supplied;
  band tables select by declared proximity with override.
- R14. Optional per-type monetization with vintage stamps; the economic-vs-monetized
  aggregate split; the life-safety declaration; the decision-10 warnings.
- R37. The do-no-harm screen evaluates Total-stream per-type equivalent-annual reductions
  ≥ 0, flags failures with offending types named, excludes flagged alternatives from
  strategy recommendations under Enforce (default), and honors WarnOnly/Off.
- R38. The standard deviation of annual risk participates in the default objective vector;
  the mean-variance ε template exists.
- R39. AFP level/reduction metrics, AFP constraints, the reliability ε template, and CSFP
  work end-to-end, including on all-reliability-mode studies (consequence metrics NaN with
  diagnostics; mixed modes refused).

**The decision framework:**
- R15. Objectives and constraints are declared over the decision-12 metric-selector
  vocabulary; constraints evaluate at EveryEpoch/FirstEpoch/Horizon scopes.
- R16. The discrete ε-constraint study produces the per-ε selection, the noninferior set,
  adjacent total trade-off ratios, binding flags, and infeasibility diagnostics; the three
  template constructors exist (Eq. 4.2 with the TRG default argument; mean-variance;
  reliability).
- R17. The k-objective Pareto screen, the incremental CE/ICA table (≡ trade-off ratios),
  and the MCDA weighted-sum block behave per decision 14, including
  tie/NaN/constant-objective rules.
- R18. Tail and dispersion measures are readable per epoch and per stream at declared α
  levels via retained epoch realizations and the public measure re-evaluation route.

**The strategy catalog:**
- R19. Tier-1 strategies (EV, TEAC, BCR, CSSL ordering, AFP ranking, mean-variance,
  mean-CVaR, PMRM partitions, expected utility/certainty equivalent, FSD/SSD, constrained
  selection) compute on every successful run.
- R20. Tier-2 strategies (Laplace, maximin, maximax, Hurwicz, mean + k·σ, epistemic
  variance/CVaR, epistemic-CDF dominance, chance-constrained selection, quantile regret)
  compute when every alternative carries a stored full-uncertainty ensemble; otherwise the
  block is null with named diagnostics.
- R21. Tier-3 minimax/expected regret computes when every alternative was enumerated by
  the C3 logic tree over shared axes with bitwise-equal branch weights and equal M;
  otherwise null with named diagnostics.
- R22. The decision summary cross-tabulates every computed strategy's recommendation and
  marks do-no-harm exclusions.
- R23. Chance-constraint evaluation matches the published A7 semantics bit-for-bit on the
  same ensemble.
- R24. Every strategy output labels its layer (aleatory/epistemic), its tier, and its
  parameters (α, utility, weights, Hurwicz α, k, percentiles).

**Results, presentation, and persistence:**
- R25. The results object carries the complete table catalog (Architecture G) with all
  convention echoes; every number a UI table needs exists without recomputation.
- R26. Trajectory, frontier, sweep, and regret tables are plot-ready (parallel numeric
  arrays, stable ordering, explicit labels/units).
- R27. The study definition round-trips as XML in BOTH serialization modes; ByReference
  markers resolve id-first with lenient name fallback; unresolved markers re-write
  verbatim and surface as load diagnostics, never silent loss.
- R40. Study results round-trip as JSON including the compressed-bytes overloads; the
  results-injection constructor restores `IsEstimated` over a non-null payload; runtime
  drill-down references are `[JsonIgnore]` and regenerate on re-run.
- R41. `RiskAnalysis.Id` serializes, round-trips, and is stripped from canonical hashing —
  hashes, seeds, and all eight byte gates are bit-identical with the append.
- R42. Nothing about a study — definition or results — enters any canonical-hash or seed
  surface; the eight byte gates stay bit-identical with the feature merged but unused.

**Validation and honesty:**
- R28. Every failure mode in decisions 1–18 has an "Error:"/"Warning:" message asserted by
  tests (missing baseline, horizon/axis/option/mode mismatches, monetization misuse,
  weight misuse, tier preconditions, no-monetized-types, life-monetized notice,
  do-no-harm notices, load diagnostics).
- R29. Every uncertainty-bearing output states its discipline (exact /
  aleatory-from-mean-LEC / epistemic-weighted / block-noise k·SE).

**Verification:**
- R30–R36. The verification families of the Verification chapter exist, run isolated, and
  pin: the v1.0 parity constants; the cost/annuity closed forms; the stream-axis and grid
  identities; the App. L family + basis lemma; the thesis Table B.1 and Table 2.1
  fixtures; the strategy-catalog hand fixtures; serialization round-trips + Id inertness;
  author inertness + reproducibility + byte-gate inertness.

## Architecture

Notation: `TR` = `src\RMC.TotalRisk`. New public types are listed with their intended
homes; all follow house style (sealed, ctor-validated, snapshot + `Array.AsReadOnly`,
get-only, full XML docs with Authors blocks; input records carry `ToXElement()` +
`XElement` ctors from CB1 — the study document is their composition).

### A. Kernel promotions and life-cycle extensions (CB1 pre-step; bit-inert, gate-proven)

- `internal static class DiscountingSupport` (`TR\Analyses\`): `AnnuityFactor(years, rate)`
  and `DiscountFactor(year, rate)` — the shipped private expressions moved verbatim;
  `RiskAnalysis` delegates; the stationary-bridge fixture and all eight byte gates prove
  the move inert.
- `SelectScope`/`ExtractMeasure` become `internal static` on `RiskAnalysis` (same bodies)
  so the study layer resolves measures through the one switch pair the engine and A7 use.
- The life-cycle stream axis (decision 4): `LifeCycleEpochEntry` gains
  `ExcessExpectedConsequences` and `FailExpectedConsequences`; `LifeCycleRiskResults`
  gains the per-stream aggregate arrays (appended optional ctor parameters; unpersisted).
  The aggregation loop in `MeasureLifeCycleRisk` runs once per stream over the same epoch
  realization — one authority, no drift.
- Epoch retention (decision 6): `LifeCycleDefinition` gains `RetainEpochRealizations`
  (default false; studies pass true); `LifeCycleEpochRisk` gains `Realization`
  (`SystemRealization?`, null when not retained). Retained realizations have had
  `DumpMemory()` applied (curve scalars + thinned LEC — the measure surface; no risk-point
  ledgers), keeping memory modest.

### B. Input records (`TR\Analyses\`)

```csharp
public sealed class LifeCyclePlan
{
    public LifeCyclePlan(IReadOnlyList<LifeCycleIntervention> interventions,
        IReadOnlyList<int>? evaluationYears = null);
    public IReadOnlyList<LifeCycleIntervention> Interventions { get; }
    public IReadOnlyList<int> EvaluationYears { get; }
    // No rate, no horizon (decision 2). Duplicate intervention years refused (the
    // LifeCycleDefinition rule); horizon bounds checked by the study's Validate.
    // Serializes its interventions inline (house-event + hazard-replacement payloads).
}

public sealed class CapitalCostEntry      // (int year, double amount, string? label)
public sealed class RecurringCostSegment  // (int startYear, double annualAmount, int? endYear, string? label)
public sealed class CostStream
{
    public CostStream(IReadOnlyList<CapitalCostEntry>? capital = null,
        IReadOnlyList<RecurringCostSegment>? operationsAndMaintenance = null,
        IReadOnlyList<RecurringCostSegment>? operatingChanges = null);
    public IReadOnlyList<CapitalCostEntry> Capital { get; }
    public IReadOnlyList<RecurringCostSegment> OperationsAndMaintenance { get; }
    public IReadOnlyList<RecurringCostSegment> OperatingChanges { get; }   // the App. L O-term
}

public sealed class RiskReductionAlternative
{
    public RiskReductionAlternative(string name, RiskAnalysis system,
        CostStream? costs = null, LifeCyclePlan? plan = null, string? description = null);
    public string Name { get; }             // non-blank; unique within a study (validated)
    public string Description { get; }
    public RiskAnalysis System { get; }     // required; instances may be shared
    public CostStream Costs { get; }        // empty default — the typical baseline
    public LifeCyclePlan? Plan { get; }     // the staged-plan overlay
}

public sealed class MonetizationFactor      // (int consequenceType, double amountPerUnit, string? label, string? vintage)
public sealed class ConsequenceMonetization // (factors?, string monetaryUnit = "$"); duplicate positions refused;
                                            // factors on identity-monetized types refused

public enum LifeCycleAccounting { NonAbsorbing, Absorbing }
public enum MetricBasis { AnnualizedPerEpoch, HorizonPresentValue, HorizonEquivalentAnnual, HorizonCumulative }
public enum MetricForm { Level, ReductionVsBaseline }
public enum EconomicMetric
{ PresentValueOfTotalCost, EquivalentAnnualCost, TotalExpectedAnnualCost, NetPresentValue,
  NetAnnualBenefit, BenefitCostRatio, CostPerStatisticalLifeSavedUnadjusted,
  CostPerStatisticalLifeSavedAdjusted, EquityWeightedAdjustedCostPerStatisticalLifeSaved,
  CostPerStatisticalFailurePrevented, AbsorbingAdjustedCostPerStatisticalLifeSaved,
  DisproportionalityRatio, AnnualizedFailureProbability, AnnualizedFailureProbabilityReduction }
public enum ObjectiveDirection { Minimize, Maximize }
public enum ConstraintScope { EveryEpoch, FirstEpoch, Horizon }
public enum AlarpProximity { JustBelowTolerableLimit, JustAboveBroadlyAcceptable }
public enum DoNoHarmPolicy { Enforce, WarnOnly, Off }
public enum UtilityFunctionForm { ExponentialCara, PowerCrra }

public sealed class CostBenefitMetric   // the selector union (decision 12): risk-measure form
{                                       // (measure, riskType, consequenceType, basis, accounting, form, alpha)
                                        // or economics form (EconomicMetric); static factories for both;
                                        // serializes by enum NAMES (append-only)
}
public sealed class ObjectiveDeclaration   // (string name, CostBenefitMetric metric, ObjectiveDirection direction)
public sealed class CostBenefitConstraint  // (CostBenefitMetric metric, ConstraintType sense, double threshold,
                                           //  ConstraintScope scope) — ConstraintType mirrors the numerics senses
public sealed class EpsilonConstraintStudy // (ObjectiveDeclaration primary, CostBenefitMetric epsilonObjective,
                                           //  IReadOnlyList<double>? epsilonGrid /* null = auto uniform */,
                                           //  int gridPoints = 10, IReadOnlyList<CostBenefitConstraint>? fixedConstraints)
                                           // + static CreateTolerableLifeRiskStudy(...)  — Eq. 4.2, TRG default 0.001
                                           // + static CreateMeanVarianceStudy(...)       — min mean s.t. SD ≤ ε
                                           // + static CreateReliabilityStudy(...)        — min cost s.t. AFP ≤ ε
public sealed class UtilityDeclaration     // (UtilityFunctionForm form, double riskAversion)
public sealed class PmrmPartition          // (IReadOnlyList<double> exceedanceBoundaries) — the declared LEC partition

public sealed class CostBenefitOptions
{
    // ctor(periodYears, discountRate = 0, evaluationYears = null, …all below optional…)
    public int PeriodYears { get; }
    public double DiscountRate { get; }
    public IReadOnlyList<int> EvaluationYears { get; }
    public RiskType BenefitRiskType { get; }              // default Total (decision 4, review ruling)
    public LifeCycleAccounting Accounting { get; }        // default NonAbsorbing (decision 5)
    public IReadOnlyList<double> AlphaLevels { get; }     // default { 0.01 }
    public ConsequenceMonetization? Monetization { get; }
    public int LifeSafetyConsequenceType { get; }         // default −1 (CSLS family skipped)
    public double WillingnessToPay { get; }               // default NaN (ALARP block skipped)
    public string? WillingnessToPayVintage { get; }
    public AlarpProximity AlarpProximity { get; }
    public IReadOnlyList<double>? AlarpBandThresholds { get; }  // null = the ER defaults
    public double IndividualRiskLimit { get; }            // default 1e-4
    public double EquityExponent { get; }                 // default 1
    public double BaselineIndividualRisk { get; }         // default NaN = the p_eq proxy (echoed)
    public double AlternativeIndividualRisk { get; }      // default NaN = per-alternative p_eq proxy
    public DoNoHarmPolicy DoNoHarm { get; }               // default Enforce (decision 9)
    public IReadOnlyList<ObjectiveDeclaration> Objectives { get; }
        // default: { PV total cost (min), monetized PV benefit (max),
        //            SD of annual total risk (min) — the declared secondary dispersion objective }
    public IReadOnlyList<CostBenefitConstraint> Constraints { get; } // the fixed constraint set
    public EpsilonConstraintStudy? EpsilonStudy { get; }
    public IReadOnlyList<double>? McdaWeights { get; }    // null = MCDA skipped; parallel to Objectives
    public UtilityDeclaration? Utility { get; }           // null = EU strategy skipped
    public PmrmPartition? PmrmPartition { get; }          // null = PMRM table skipped
    public double HurwiczAlpha { get; }                   // default 0.5
    public double DispersionK { get; }                    // default 1 (mean + k·σ)
    public IReadOnlyList<double> ChanceConstraintConfidenceLevels { get; }  // default { 0.9 }
    public double EpistemicTailAlpha { get; }             // default 0.1 (epistemic CVaR level)
    public XElement ToXElement();                          // + XElement ctor; G17/name conventions
}
```

### C. `CostBenefitAnalysis` (`TR\Analyses\`)

```csharp
public sealed class CostBenefitAnalysis : AnalysisBase
{
    public CostBenefitAnalysis(CostBenefitOptions options);
    public CostBenefitAnalysis(XElement xElement,
        IReadOnlyList<RiskAnalysis>? storedAnalyses = null,
        CostBenefitResults? results = null);              // the injection/restore ctor (decision 18)
    public string Name { get; set; }                       // INPC
    public string Description { get; set; }                // INPC
    public CostBenefitOptions Options { get; set; }        // replace-to-edit; clears IsEstimated
    public ObservableCollection<RiskReductionAlternative> Alternatives { get; }
    public RiskReductionAlternative? Baseline { get; set; } // MUST reference a member (validated)
    public CostBenefitResults? Results { get; }             // published on success; serialized as JSON
    public IReadOnlyList<string> LoadDiagnostics { get; }   // unresolved references, invalid payloads

    public XElement ToXElement(RiskSerializationMode mode = RiskSerializationMode.SelfContained);
    public override Task RunAsync(SafeProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default);
    public override IReadOnlyList<ValidationIssue> ValidateIssues();
    public override (bool IsValid, List<string> ValidationMessages) Validate();
}
```

`RunAsync` orchestration: `TryBeginRun` → veto event → validation (race-safe re-check) →
the study-wide grid derived once → per alternative, `MeasureLifeCycleRisk` on its
(System, Plan) under a study-constructed `LifeCycleDefinition` (deduplicated per distinct
(System, Plan) pair; baseline first; cancellation honored between evaluations; progress =
evaluations done/total) → cost PV/EAC per alternative → the metric matrix (per alternative
× declared metrics, through the promoted `SelectScope`/`ExtractMeasure` on retained epoch
realizations and the aggregate arrays) → deltas vs baseline → the formulary → the
do-no-harm screen → the ε study → frontier/ICA → MCDA → the strategy catalog at every
satisfiable tier → publish → `IsEstimated = true` → completed event → `EndRun`. Author
objects are byte-untouched (the B8/C1 clone discipline end-to-end); reproducibility is
bit-exact run-to-run (mean-only determinism).

Validation matrix (all asserted verbatim-prefix by tests): Errors — no baseline designated
/ baseline not a member; empty or duplicate alternative names; plan or cost years outside
[0, horizon); horizon < 1; rate < 0 or non-finite; consequence-type count mismatch or
non-blank label/unit conflict vs the baseline; mixed `RiskAnalysisMode`s across
alternatives; monetization factor on an identity-monetized type or out-of-range type
position; life-safety type out of range; MCDA weight count ≠ objective count or
non-positive/non-finite weights; ε study referencing an undeclared metric form; a running
baseline/alternative; invalid utility/partition declarations. Warnings — no monetized
types (NPV/BCR NaN); life-safety type monetized (the decision-10 accounting notice);
per-alternative α/threshold/measure-option mismatches (the study re-evaluates uniformly);
zero-cost non-baseline alternative; do-no-harm failures under WarnOnly; Tier-2/Tier-3
preconditions unmet (named per alternative); WTP absent (ALARP skipped); individual-risk
proxy in use; reliability-mode study (consequence metrics skipped, named).

### D. The metric formulary (normative algebra)

With r the study rate, T the horizon, A(y) the annuity factor, and all Δ = baseline −
alternative on the study stream/type unless stated:

- PV_cost = Σ_capital aₖ·(1+r)^(−k) + Σ_segments a·[A(e) − A(s)] (each kind sub-totaled);
  EAC_cost = PV_cost/A(T); TEAC = EAC_cost + EAD_stream (decision 7).
- ΔPV(type, stream, accounting), ΔEqA = ΔPV/A(T), ΔCum — from the per-stream aggregate
  arrays; monetized PV benefit = Σ monetized-type ΔPV·factor; economic PV benefit = the
  same sum excluding the life-safety type.
- NPV = monetizedPVBenefit − PV_cost; NetAnnual = NPV/A(T); BCR = monetizedPVBenefit/PV_cost.
- CSSL(U) = C_A / ΔL_eqA; CSSL(A) = max(0, C_A − ΔR_econ,eqA − ΔO_eqA) / ΔL_eqA, with
  C_A = EAC of capital + O&M, ΔO_eqA = the annualized operating-cost REDUCTION (a measure
  adding operating cost has ΔO < 0 and a larger numerator), ΔL_eqA = the Excess life-loss
  EqA reduction. Basis-invariance: identical from PV forms (A(T) cancels; the clamp
  commutes).
- EWACSLS = CSSL(A) / [max(rᵢ_base, IRL)/max(rᵢ_alt, IRL)]ⁿ; rᵢ defaults to
  p_eq = 1 − (1 − P_T)^(1/T) per side (echoed as proxy) with override seats.
- CSFP = C_A / Δp_eq (+ year-0 ΔAFP echoed); AFP level/reduction are first-class metric
  selections (decision 17).
- AACSLS = max(0, PV_cost − ΔPV_operatingReduction − AbsorbingΔPV_econ) /
  AbsorbingΔCum_lives (the Fluixá composition on the shipped absorbing aggregates; the
  clamp is this design's family-consistency choice, documented).
- Disproportionality = CSSL(A)/WTP; ALARP band per decision 11.
- The do-no-harm screen (decision 9): pass ⇔ ΔEqA(Total, t) ≥ 0 for every declared type t
  under the headline accounting; failures flag the row and gate recommendations per the
  policy.
- Aleatory tail/dispersion measures per stream from the retained epoch curves: SD (the
  declared secondary dispersion objective), CVaR_α (exceedance-α pin, decision 8),
  ConditionalMean (N_Δ's seat), VaR_α, PMRM partition conditional means; expected utility
  EU = Σᵢ wᵢ·u(cᵢ) over the LEC mass pairs with u Exponential(θ) or Power(γ), reported as
  the certainty equivalent u⁻¹(EU).
- Epistemic measures over the stored ensembles (weights w, values v of any declared
  metric): weighted mean/variance/percentiles; epistemic CVaR at level a = the weighted
  mean of the worst a-tail (the weighted-percentile boundary convention documented); the
  chance fraction P(v > threshold) exactly as A7.

NaN/clamp discipline (decision 7) applies uniformly; every metric echoes its parameters.

### E. The constrained discrete decision framework (formal statements)

Let 𝒜 = {a₀ (baseline), a₁, …, a_m} with measure vectors f(a) assembled per Section D. The
**constrained selection problem** is

    min_{a ∈ 𝒜}  f₁(a)   s.t.   gⱼ(a) ≤ εⱼ  (j = 2..k),   h(a) satisfied (fixed constraints)

solved exactly by feasibility filtering + argmin (the set is finite and manually defined —
the 2026-09-06 ruling). The **ε-constraint study** sweeps ε over a grid and records the
selection per ε; the **noninferior set** is the weak-Pareto screen of the swept pair
restricted to feasible alternatives; between adjacent noninferior alternatives the **total
trade-off ratio** λ̂₁ⱼ = −Δf₁/Δfⱼ is reported — the discrete shadow price (Haimes), the
exact discrete analogue of Eq. B.7's λ, and identically the incremental
cost-effectiveness ratio when f₁ is cost-like and fⱼ is effect-like (the CE/ICA
unification, decision 13). Under full uncertainty the fixed constraints admit the
**chance-constrained form** P(gⱼ(a) ≤ εⱼ) ≥ pⱼ, evaluated by the A7 semantics per
alternative (Tier 2). The Eq. 4.2 template instantiates f₁ = TEAC, the fixed constraint
Excess EqA life loss ≤ TRG, and the swept constraint N_Δ (or CVaR_α) ≤ ε₃; the
mean-variance template instantiates f₁ = mean risk, swept SD; the reliability template
instantiates f₁ = cost, swept AFP.

### F. The decision-strategy catalog

Taxonomy (each row: scope, needs, output): **screens** — aleatory FSD/SSD (mean LEC
pairs), epistemic-CDF dominance (Tier 2), the k-objective Pareto frontier; **constrained
selection** — the fixed-constraint argmin, the ε study, chance-constrained selection
(Tier 2); **scalar rules** — EV/EAD, TEAC, NPV/net benefits, BCR, CSSL(A) ordering, AFP
ranking (reliability), certainty-equivalent EU, mean + k·σ (aleatory or epistemic —
labeled), CVaR_α (min tail), PMRM partition objectives, Laplace, maximin, maximax,
Hurwicz(α), minimax regret (Tier 3), expected regret (Tier 3), MCDA score.
Implementation: internal static computation helpers over the assembled metric matrix; each
strategy publishes a `StrategyRanking` (strategy id, layer/tier label, parameters echo,
per-alternative criterion values, ranks, the recommended alternative — do-no-harm
exclusions honored, diagnostics when skipped); the `DecisionSummary` cross-tabulates
recommendations. The regret machinery (Tier 3): per shared state s (a C3 branch
combination present with bitwise-equal weight in every alternative's map, equal M), the
block mean v(a, s); regret ρ(a, s) = v(a, s) − min_{a'} v(a', s) (direction-aware);
minimax regret = argmin_a max_s ρ(a, s); expected regret = argmin_a Σ_s W(s)·ρ(a, s); the
state-alignment preconditions are diagnosed per alternative when unmet.

### G. Results containers (`TR\Results\`; the UI/App presentation contract)

`CostBenefitResults` — the study echo block (every decision-20 convention) plus:

| Table | Container | Columns (gist) |
|---|---|---|
| Alternative economics | `AlternativeEconomics` rows (baseline = row 0) | name, source labels, cost block (PV by kind, EAC, cumulative), TEAC, NPV, net annual, BCR, AFP level + Δ (p_eq + year-0), lives saved (EqA), CSSL(U)/(A), EWACSLS, CSFP, AACSLS, disproportionality + ALARP band, do-no-harm flag + offending types, per-convention twins |
| Consequence reductions | `ConsequenceReduction` rows | type, stream, label/unit, baseline/alternative PV, ΔPV/ΔEqA/ΔCum, absorbing twins, monetized ΔPV |
| Trajectories | per-alternative `LifeCycleRiskResults` (runtime reference) + a serialized flattened `TrajectoryPoint` series | year, per-stream AFP + per-type EAC — plot-ready |
| ε sweep | `EpsilonSweepEntry` rows | ε, selected alternative, f₁, f_ε, λ̂, feasible count, binding flags |
| Frontier + ICA | `ParetoFrontierResults` | objective echo, per-alternative values, non-dominated/NaN flags; `IncrementalEntry` rows (from, to, Δcost, Δbenefit, incremental BCR, incremental CSSL ≡ λ̂) |
| MCDA | `McdaResults` | normalized weights, normalized values, scores, ranks, exclusions |
| Strategy rankings | `StrategyRanking` rows per strategy | criterion values, ranks, recommendation, layer/tier/parameter echoes, skip diagnostics |
| Decision summary | `DecisionSummary` | strategy × recommended alternative (+ criterion value; do-no-harm exclusions marked) |
| Regret | `RegretMatrixResults` | state labels + weights, per-alternative regret rows, max/expected regret, the minimax pick |
| Epistemic bands | `EpistemicMeasureSummary` rows | per alternative × declared metric: weighted mean/median/L/U, variance, epistemic CVaR, N_eff |
| Chance constraints | `ChanceConstraintEntry` rows | constraint echo, per-alternative satisfaction probability, verdict at each declared confidence |
| Dominance | `DominanceEntry` rows | pair, layer, FSD/SSD/none verdict |

All sealed, ctor-validated, label/unit-echoed. **The table payload above is the serialized
results body (System.Text.Json)**; the per-alternative deep objects
(`LifeCycleRiskResults`, retained epoch realizations, LEC overlays by reference) are
`[JsonIgnore]` runtime state regenerated by a re-run.

### H. The serialized study document (decision 18; lands at CB4)

- Element `CostBenefitAnalysis`: Name/Description/Baseline (by alternative name) +
  `<Options>` (the full declaration set, enums by NAME, doubles G17) + `<Alternatives>`.
- Each `<RiskReductionAlternative>`: Name/Description + `<Costs>` + optional `<Plan>` +
  the system per `RiskSerializationMode` — SelfContained: the embedded analysis (its
  options element + `<Components>` in SelfContained mode, the results-injection ctor
  shape); ByReference: `<AnalysisReference Id Name/>` re-attached by the injection ctor
  (id-first, lenient name fallback; a miss re-writes the marker verbatim and lands in
  `LoadDiagnostics` — never silent, never fatal).
- **`RiskAnalysis.Id`**: a Guid property, serialized on the analysis options element,
  STRIPPED from canonical hashing via the existing `CanonicalizationRules` metadata list
  (the `IRiskFunction.Id` pattern) — hashes, seeds, and the eight byte gates bit-identical
  (CB4's gate proof).
- Results: `CostBenefitResults.ToJson()`/`FromJson()` + `ToCompressedBytes()`/
  `FromCompressedBytes()` on the shared `ResultsJson` conventions (named floats,
  null-suppressed optional blocks, append-only fields); the injection ctor restores
  `IsEstimated` over a non-null, shape-valid payload, else clears with `LoadDiagnostics`
  (the A4 load-integrity pattern).
- The study never enters a canonical-hash or seed surface; the wire shapes are append-only
  contract from the first landing (attribute names, element order, enum names).

### I. Uncertainty discipline

Decision 16 verbatim, plus the composition notes: Tier-2/3 blocks read ONLY stored
published state (`RiskResults`, `RealizationWeights`, `LogicTreeEnumeration`) — the study
never runs full uncertainty itself; the A9/A4 post-hoc conventions apply unchanged; the
honest-routes paragraph for distributional NPV is reproduced in the tech-ref chapter so
practitioners see it where they work.

## Implementation phases

Per-session exit gates (every session): `dotnet build` 0 warnings → fast suite
(`dotnet test -c Release`) → `CostBenefitVerification` run ISOLATED →
`validate-code-xml-docs.ps1` → **all eight perf byte gates bit-identical, run as separate
invocations** → traceability validator → matrix/PROGRESS updates → commit. One session per
sitting; each session prompt re-baselines at write time.

| Session | Scope | Depends on |
|---|---|---|
| **CB1 — kernel, model, parity** | The Section-A promotions + life-cycle stream axis + epoch retention (bit-inert, gate-proven); all Section-B records WITH their XML round-trips; `CostBenefitAnalysis` (orchestration, the validation matrix, progress/cancellation; XML deferred to CB4); per-alternative economics both conventions; monetization + the aggregate split; `PlanEconomics`; fixtures 1–11 | — |
| **CB2 — the decision framework** | Metric selectors wired end-to-end; the CSSL formulary (App. L exact + EWACSLS/CSFP/AACSLS + disproportionality/ALARP); TEAC; the do-no-harm screen; the ε-constraint study + the three templates; frontier + ICA (≡ trade-offs); MCDA; fixtures 12–17, 27–28 | CB1 |
| **CB3 — the strategy catalog** | Tier-1/2/3 strategies + rankings + the decision summary + the regret machinery + chance constraints + epistemic measures (variance/CVaR) + dominance screens + PMRM + expected utility; fixtures 18–26 | CB2 |
| **CB4 — the serialized study + presentation closure** | The study `ToXElement`/injection ctor in BOTH modes; the `RiskAnalysis.Id` append (hash-strip + gate proof); results JSON (+ compressed) round-trips; load diagnostics; the presentation-catalog completeness audit; docs: `docs/technical-reference/cost-benefit-analysis.md`, results-catalog + uncertainty-analysis sections, `docs/verification/cost-benefit.md` run-of-record, REMAINING-WORK extension records, the arch-doc amendment, references; fixtures 29–31 | CB3 |

**The exhaustive unit + verification testing campaign follows CB4** — the ruled critical
path; imports, hardening, and release prep follow the campaign.

## Testing

Unit-test matrix (`RMC.TotalRisk.Tests`, mirrored folders; every new public class gets
`<ClassName>Tests.cs`): record ctor guards + immutability + replace-to-edit + XML
round-trips; options defaults + echoes; validation matrix (every Error/Warning
verbatim-prefix); orchestration (dedup of shared (System, Plan) pairs, baseline row 0,
progress counts, cancellation between evaluations, veto event, IsRunning refusals); cost
PV arithmetic incl. signed entries and segment accrual; formulary algebra at hand values
incl. every NaN/clamp rule and the do-no-harm policies; selector resolution vs the
promoted switches; ε-study grid construction + selection + trade-offs + diagnostics +
the three templates; frontier/ICA/MCDA rules; strategy computations at hand matrices;
results container completeness (every echo present) + JSON round-trips; study XML
round-trips both modes + load diagnostics; `RiskAnalysis.Id` round-trip + hash-inertness;
author-state inertness at unit scale; the life-cycle extension appends (existing tests
bit-unchanged + the new stream/retention surfaces). Coverage target: the >90% fast-suite
line-coverage bar holds with C5 merged.

## Verification

Family `CostBenefitVerification` (+ the CB1 extensions inside `LifeCycleVerification`),
run isolated per the long-run workflow rule. Oracles are independent re-implementations
(Math.Pow/loop arithmetic, hand-derived constants, closed forms) — never the production
code.

1. **Stream-axis closed form** — extend `Test_LifeCycle_TwoEpochClosedForm_Exact`: the new
   Excess/Fail aggregates against the independent per-year survival/annuity loop on
   hand-derived stream means; Total = Excess + Background per epoch; existing asserts
   bit-exact.
2. **Promotion + retention inertness** — the stationary bridge and the author-byte pin
   re-run bit-exact after the Section-A moves; retention off = byte-identical results; the
   eight gates bit-identical (the session gate).
3. **v1.0 parity constants** — the five decision-19 cases via an in-test independent loop;
   the r = 0 improvement pinned as the documented deviation; truncation and
   constant-stream behaviors pinned.
4. **Cost-stream PV oracle** — capital {0: 1000; 10: 500; 20: −200}, O&M 10/yr (0, T],
   operating −5/yr (10, T], r = 0.035, T = 50: independent Σ a·(1+r)^(−k) vs the
   annuity-segment path; EAC identity.
5. **Null study** — one alternative ≡ baseline: zero deltas bit-exact, NPV 0, BCR/CSLS
   NaN, frontier tie handling, do-no-harm pass.
6. **Stationary bridge** — a deterministic stationary system, one house-event alternative
   at year 0: NPV/BCR/TEAC against a full hand closed form; the year-0 epoch deltas
   bit-equal to `MeasureConfigurationRisk` entries (the B8 cross-check).
7. **Two-epoch benefit closed form** — intervention + capital at year k: the (1+r)^(−k) vs
   (1+r)^(−(k+1)) convention identity via independent arithmetic; both conventions'
   aggregates against fixture-1 oracles differenced.
8. **Grid alignment** — with deterioration: baseline re-ages at alternative plan years on
   the union grid (values change accordingly, pinned); without: coarse vs fine grid
   bit-identical (telescoping).
9. **App. L family** — hand values (C_A 120, ΔR 30, ΔO 10, ΔL 0.004 ⇒ CSSL(U) 30,000,
   CSSL(A) 20,000; clamp case ⇒ 0 exactly; ΔL ≤ 0 ⇒ NaN); the basis-invariance lemma
   (PV-form vs EqA-form bit-agreement).
10. **EWACSLS/CSFP/AACSLS arithmetic** — hand fixtures incl. the IRL floor engaging on one
    side only and the n ∈ {0.5, 1, 2} sweep; Δp_eq from an independent survival
    computation; AACSLS against fixture-1 absorbing oracles.
11. **Monetization identities** — life-only-monetized: NPV = V·(discounted lives saved) −
    PV_cost from echoed parts; the economic/monetized aggregate split; factor-1 identity
    types; empty-set NaN + Warning.
12. **Thesis Table B.1 (discrete ε-constraint)** — ten alternatives at the ANALYTIC
    noninferior points (x₁(λ) = (2+6λ)/(1+λ), x₂(λ) = (4+10λ)/(1+λ) for the uniform ε
    grid 6→58): the study reproduces the per-ε selections and the noninferior set;
    adjacent λ̂ = −Δf₁/Δf₂ pinned exactly and documented against the analytic λ column
    (the secant-vs-tangent relationship).
13. **Thesis Table 2.1 (CVaR discrimination)** — four alternatives with identical expected
    life loss and CVaR {30, 67, 149, 577}: EV ranking ties, mean-CVaR and CVaR orderings
    pinned; the tolerable-limit filter (≤ 100 at α) selects option 2 per the thesis
    reading.
14. **The Eq. 4.2 template** — a hand-built set where the TRG filter removes named
    alternatives and the ε sweep walks the rest; binding flags + infeasibility diagnostics
    pinned.
15. **Frontier/ICA hand set** — strict/weak/tie/NaN/mixed-direction/zero-cost cases; ICA
    rows ≡ λ̂ (the unification pinned bit-exact).
16. **MCDA arithmetic** — weights {2,1,1} → {0.5, 0.25, 0.25}; a constant objective; a
    NaN exclusion; scores/ranks exact.
17. **Study-α re-evaluation** — retained epoch curves re-measured at a study α vs the run
    α: the thinned-LEC route pinned against a directly-configured twin run.
18. **Savage regret (Tier 3)** — a designed shared-axis C3 pair (3 combinations × small
    M): the exact regret matrix, max/expected regret, the minimax pick; the
    quantile-regret twin computed and pinned as a DIFFERENT number (the honesty
    demonstration).
19. **Epistemic criteria** — hand ensembles with weights: Laplace/maximin/maximax/
    Hurwicz(0, ½, 1)/mean + k·σ picks pinned; Hurwicz endpoints ≡ maximin/maximax.
20. **Epistemic CVaR arithmetic** — weighted tail means incl. degenerate cases (equal
    values, single realization, weight concentration at the boundary).
21. **Stochastic dominance** — constructed LEC pairs: clean FSD, SSD-only,
    non-comparable; the epistemic-CDF variant on constructed ensembles.
22. **Expected utility** — exponential utility over a two-point loss: certainty
    equivalent closed-form; a constructed EV-tie broken by EU exactly as designed.
23. **Chance-constraint parity** — the study's satisfaction fraction bit-equal to the
    published A7 `TolerableRiskConfidence` on the same stored ensemble.
24. **Decision summary** — a designed set where distinct strategies provably recommend
    distinct alternatives; the cross-tabulation pinned row-by-row incl. a do-no-harm
    exclusion.
25. **Author inertness + reproducibility** — a full study leaves every alternative's
    published JSON, hashes, ages, and states byte-identical; two identical runs publish
    bit-identical results.
26. **Validation sweep** — every decision-1..18 Error/Warning asserted verbatim-prefix,
    incl. the tier-precondition and mixed-mode diagnostics.
27. **Do-no-harm screen** — a constructed alternative that reduces Excess risk while
    increasing Total risk through non-fail growth: flagged with the offending type named;
    Enforce excludes it from every recommendation; WarnOnly/Off behaviors pinned.
28. **Reliability-mode study** — all-reliability alternatives: AFP/CSFP/frontier/ε on AFP
    active, consequence metrics NaN with diagnostics; a mixed-mode study refused.
29. **Study XML round-trips** — both modes: SelfContained re-loads standalone and re-runs
    to bit-identical results; ByReference resolves id-first/name-fallback, and an
    unresolved marker re-writes verbatim with a load diagnostic (never silent).
30. **Results JSON round-trips** — the full table payload through `ToJson`/`FromJson` and
    the compressed overloads (compared post-decompression); the injection ctor restores
    `IsEstimated`; an invalid payload clears with diagnostics (the A4 load-integrity
    pattern).
31. **`RiskAnalysis.Id` inertness** — Id present: canonical hash and every captured seed
    bit-identical to the pre-append baseline; Id round-trips; all eight byte gates
    bit-identical (the CB4 session gate).

Run-of-record + tolerances land in `docs/verification/cost-benefit.md` (dated, with commit
and counts, per the house verification-page conventions).

## Documentation updates

At CB4: `docs/technical-reference/cost-benefit-analysis.md` (the practitioner chapter —
the formulary, the strategy catalog with the honesty clauses, the α-convention mapping,
the TR App. H note, the serialized-study lifecycle); results-catalog and
uncertainty-analysis sections; `docs/verification/cost-benefit.md`; REMAINING-WORK
extension records (decision 21); the arch-doc §7 amendment; the repository guidance matrix rows; `docs/references.md` additions (Smith 2022; Haimes 1971/2004;
Rockafellar & Uryasev 2000/2002; Artzner 1999; Savage 1951; Serrano-Lombillo et al. 2016;
Fluixá-Sanmartín et al. 2020; Mays 2001; ER 1105-2-100/PR&G).
