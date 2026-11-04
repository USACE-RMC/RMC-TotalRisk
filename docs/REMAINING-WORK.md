# Remaining v1.1 Work

> The authoritative map of what stands between the current state and full v1.1, maintained
> alongside [ROADMAP.md](ROADMAP.md) (phases and exit gates) and [PROGRESS.md](PROGRESS.md)
> (session log). Updated 2026-09-06 after v2.0-program session 10 (C2 + C1, the life-cycle
> foundations); update whenever an item lands or a ruling changes scope.

> **Program decision (2026-08-27):** the capability roadmap beyond v1.1 is approved; the final
> TotalRisk release is **v2.0.0** — all tier A–C work ships in it (the old v1.2/v1.3 mapping is
> superseded) — and the program closes with a dedicated session that publishes the NuGet package
> and makes the code public. The v1.1 sequence below still runs first and is unchanged except as
> noted. Program progress: session 1 (the Numerics 2.2.0 tier A/B slate), session 2 (**A4 —
> the weighted epistemic ensemble**, the foundation primitive for B2/B1/B3/C1/C3, plus the
> **A7 tolerable-risk confidence** stretch — serialized `TolerableRiskCriteria` on the options
> per the 2026-08-27 ruling, the conditional-presence hash identity pinned, and no
> `RiskMeasureOptions` flag so the recorded `"All"`-serialization trap never engages), and
> session 3 (**B2 — value of information**, output-first with the given-data weighted EVPPI
> conditioning and the A7 guideline-movement framing; plus the quick wins **A3** given-data
> sensitivity measures, **A9** retained realizations with byte-exact post-hoc weighted
> re-banding, and **A2** the secondary-bin Richardson diagnostic; A10-TotalRisk skip-recorded
> on a discovered upstream gap; the B1 design draft written to
> the local plan `b1-bayesian-updating-design-draft.md` for Haden's design session), and
> session 4 (**A10 — the extrapolation policy end to end across both repos**: the upstream
> `Extrapolation` wrappers riding the 2.2.0 release plus the TotalRisk `ExtrapolationPolicy`
> with the Error guards; the quick wins **A6** epistemic conditioning via fractile pins, **A5**
> exposure-period/life-cycle conversions, **A1** exact fault-tree importance; and the full A8
> stretch — the runtime joint convergence certificate and, per the ratified attempt, the
> scrambled-Sobol `SamplingScheme` member plus the opt-in seeded-Sobol joint driver, with
> `Vegas.SobolSeed` also riding 2.2.0), session 5 (**B3 — the epistemic-mixture logic-tree mode
> on all four composite clusters with named shared epistemic variables**, closing arch item
> Q-Y), and session 6 (**C3 — exact logic-tree enumeration**: the runtime-only
> `RiskAnalysis.LogicTreeEnumerationRealizations` mode running every branch combination as one
> weighted ensemble of K·M realizations with the exact branch-weight products on the A4
> carrier — zero Monte Carlo noise on the branch axis, the sampled mode's exact oracle with the
> convergence roles reversed), and session 7 (**B9 — shared event-tree limbs**: the
> `SharedLogicalEvent` link mode on event trees through context-keyed sampling classes — one
> draw per realization per shared limb, deep identity through nested independent links,
> conditional `SharedVariable` identity ordinals, the read-scope embed unification, and the
> reconstruction/invisibility/variance verification set — retiring the design doc's
> shared-reuse non-goal under its recorded approval; plus **B8 — configuration risk**: the
> runtime-only `MeasureConfigurationRisk` house-event query as mean-only clone-and-rerun
> twins with the bit-exact re-authored oracle) are complete.

## The sequence to `v1.1.0-alpha`

| # | Item | Owner / gate | What it entails |
|---|---|---|---|
| 1 | **Numerics 2.2.0 release** (remediation task N4) | **User-gated** — Haden cuts the release | The branch push is done (origin in sync at `dc5b17c`, observed 2026-08-27): the tier A/B slate is upstream (`AdaptiveGaussKronrod2D`, weighted statistics, `ExtrapolationSides`, scrambled Sobol, `UnionSingleFactor` + `Tools.Expm1`, `GlobalSensitivity`, the lookup binary-compatibility overloads). Remaining: `dotnet pack /p:Version=2.2.0` to the local feed `C:\GIT\numerics\packages` — **the previously packed nupkg is stale**: per the 2026-08-28 release-train ruling the A10 extrapolation wrappers (`91ccaf9`) and `Vegas.SobolSeed` (`fa91884`) ride this release, and the draft carries both items with re-pack reminders. Release notes are **drafted** at the local plan `numerics-2.2.0-release-notes-draft.md`, covering all 127 commits since the v2.1.4 merge incl. the ratified rulings (D1, D2, D4/D5, D6, D7), the MCMC changes and their BestFit impact, the Frank θ > 0 conditional-inversion correction, and the Gumbel/Joe boundary saturation. The F6 byte-gate re-pin ruling is **executed** (approved and re-pinned to `74af2e95…` 2026-08-27; attribution and evidence in PROGRESS 2026-08-27) |
| 2 | **Package switch** (remediation task T8; closes Phase 8) | One session after (1) | Swap all **four** `<HintPath>` references (library, both test projects, `scripts/perf/PerfHarness`) to the `RMC.Numerics` PackageReference already declared in `Directory.Packages.props`; update the repository guidance's interim dependency note and the README build prerequisites; re-run all eight perf byte gates (expected bit-identical) |
| 3 | **Phase 9 residue + Phase 11B — external imports and LifeSim** | One or two sessions | `RFAHazard` (owner: 11B — the tabular import of RMC-RFA results); `CompositeHazard` parameter-set import (+ resolve whether parametric posterior injection supersedes the planned `BestFitUnivariateHazard`); `CompositeConsequence` → Numerics `CompositeFunction` migration; `BestFitBivariateHazard` (θ-posterior import — copula-parameter uncertainty arrives here), `BestFitTabularHazard` (resolves arch Q-P: the import hash shape), `BestFitTransform`; `LifeSimConsequence` + `LifeSimResult` (arch Q-D: audit for hidden file I/O; needs the numerics `Network._nodeCount` ruling — LifeSim consumes that surface). Verification: a BestFit import contract test (deserialize with Numerics alone → construct → evaluate) plus RFA/LifeSim oracle families. **Exit: full v1.1 input-function surface P/T/V** |
| 4 | **Phase 12 — hardening** | One session | The ≥ 90% coverage gate script already exists and passes; the real delta is the one-off Linux `dotnet build` container check, `docs/getting-started.md`, a headless code example in `examples/`, and the BenchmarkDotNet micro-suite over the hot kernels. Candidate: the arch Q-E threading audit. Standing perf note: the F8 recording-path allocation profile (38.25 GB staging lists) |
| 5 | **Phase 13 — release prep** | One session; user tags | Recorded full-suite verification run, release notes, version stamping, the `v1.1.0-alpha` tag (user pushes/tags) |
| 6 | **Phase 14 — `RMC.TotalRisk.Api`** (REST + MCP) | **14A executed 2026-09-04** (user directive 2026-09-03 superseded the post-alpha timing to unblock the Dam Screening Tool); **the transform-chain increment (contract 1.1.0) executed 2026-09-10**; 14B remains | 14A landed: `src/RMC.TotalRisk.Api` + `src/RMC.TotalRisk.Api.Tests` on the confirmed `RMC.BestFit.Api` template minus the store — a **stateless round-trip compute** (`POST api/risk-analyses/compute` / `validate`, `GET example` / `metadata`, OpenAPI, stateless MCP at `/mcp` with run/validate/metadata/example tools), `docs/api.md`, 73 tests incl. the EAD closed-form golden and the MCP JSON-RPC round trip. The 1.1.0 increment added failure-mode **hazard-to-response transform chains** (`tabularTransform`/`linearTransform`/`powerTransform`, deterministic) + `consequenceHazardPosition` (omitted = the raw component hazard, ruled 2026-09-10; the API-side range gate is load-bearing over `AddFailureMode`'s silent binding drop), label threading, the metadata/example/MCP discovery updates, and the Sterbenz-exact transform-equivalence HTTP golden — 90 Api tests, library untouched. **14B (deferred)**: the store-backed resource lifecycle (create/run/get with ids), full-uncertainty ensemble result mapping, the wider function-kind catalog on the wire (uncertain tabular functions, uncertain/composite transforms, trailing response-to-consequence chains, multi-stage responses, parametric/composite hazards and responses, trees, bivariate hazards), and containerization + auth when deployment demands them |

## Rulings recorded 2026-08-17

1. **Authors-block scope**: required in the library and Verification projects; test classes are
   exempt (the ~23 test files that carry it are harmless). Enforced by
   `scripts/validate-code-xml-docs.ps1`.
2. **Phase 14 (API)** is inside v1.1 and ships **after** the `v1.1.0-alpha` tag — the alpha is the
   model-library milestone. *(Timing superseded 2026-09-03 by user directive: the 14A stateless
   compute round trip executed 2026-09-04 to unblock the Dam Screening Tool; the alpha sequence
   itself is unchanged.)*
3. **`RMC.TotalRisk.Systems` root** (`SystemModel`, the Hydrologics `BasinModel` analog) is
   **post-v1.1**: the namespace reservation stays; `RiskAnalysis`-owns-components remains the v1.1
   system representation.

## Rulings recorded 2026-09-06

1. **B1 (Bayesian updating) is relocated out of this repository's program**: the capability
   belongs to `C:\GIT\System-Response` (the physics-based system-response library — the same
   home ruled for FORM/SORM and fragility derivation on 2026-08-27), where the fragility
   evidence lives. The existing design draft
   (the local plan `b1-bayesian-updating-design-draft.md` — conjugate Beta-on-SRP
   parameterized by effective record length and exposure period, plus the ensemble
   likelihood re-weighting companion) becomes the System-Response design reference; the
   TotalRisk-side A4 realization-weight carrier it composes with is already shipped and
   needs nothing further here. No TotalRisk design-session gate remains for B1.
2. **B7 (tail-dependent capacity coupling) is deferred indefinitely**: it is not a near-term
   practitioner need and will not be scheduled; the complete prepared plan below stays as the
   archive so a future ruling can reopen it without re-derivation.
3. **C4 (PortfolioAnalysis) is deferred off the critical path**: it stays in the v2.0.0
   scope but is not scheduled — it slots at Haden's convenience before release prep, with
   its own design round when called.
4. **The critical path after C5 is the exhaustive testing campaign**: once C5
   (CostBenefitAnalysis) lands, the program spends a multi-week campaign developing
   exhaustive unit AND verification testing across every feature already developed, before
   anything else on the critical path (the import surface, hardening, and release prep
   follow the campaign). C5 itself runs design-first: a dedicated design session produces
   the ratified normative design document, then implementation sessions execute it.
5. **The C5 design is ratified (the 2026-09-06 design review)**:
   [requirements/COST_BENEFIT_ANALYSIS_DESIGN.md](requirements/COST_BENEFIT_ANALYSIS_DESIGN.md)
   v1.0 — twenty-two decisions across four batched rounds plus a serialization follow-up.
   The study is a **serialized** `CostBenefitAnalysis : AnalysisBase` owning a collection of
   `RiskReductionAlternative`s (each a `RiskAnalysis` + tagged cost stream + optional
   life-cycle plan) with a user-designated baseline; economics on a selectable stream
   (**default Total** — fail/non-fail trade-offs), both accounting conventions
   (non-absorbing headline); the discrete ε-constraint framework with Haimes total
   trade-off shadow prices and three templates (thesis Eq. 4.2 tolerable-life-risk,
   mean-variance, reliability/AFP); the App. L-exact CSSL family + EWACSLS/CSFP/AACSLS +
   disproportionality/ALARP; opt-in VSL monetization with split accounting and no shipped
   dollar defaults; the **do-no-harm screen** (Total risk must not increase; Enforce
   default); the three-tier decision-strategy catalog (mean-only exact / per-ensemble
   epistemic incl. chance constraints via the A7 semantics / C3 shared-state Savage
   regret); the presentation-complete results catalog as the UI/App contract; study
   definition XML in both serialization modes + a hash-stripped `RiskAnalysis.Id` +
   results JSON. Implementation = sessions CB1–CB4 (kernel/model/parity → decision
   framework → strategy catalog → serialized study + docs), each closing on the standard
   gates with all eight byte gates bit-identical; **the testing campaign follows CB4**.
   Methodological anchors: Smith (2022) — the lead's M.S. thesis, read in full at design
   time; Haimes, Lasdon & Wismer (1971); Rockafellar & Uryasev (2000/2002); ER 1110-2-1156
   App. L; the v1.0 `PlanRow.EquivalentAnnual` parity anchor with the TR App. H
   discrepancy ruled to the code.

## Open questions that resolve inside the items above

| Question | Where it resolves |
|---|---|
| Q-P — `BestFitTabularHazard` import hash shape (full grid vs compressed posterior summary) | Item 3 |
| Q-D — `LifeSimConsequence` hidden-file-I/O audit | Item 3 |
| Engine AGK `AbsoluteTolerance` audit — acceptance is absolute-OR-relative, so a small integral can be governed by the absolute criterion (the `UnionSingleFactor` lesson, 2026-08-27); confirm the engine's small-EAD runs are not exposed | Item 4 candidate |
| Q-E — threading audit of `SampledComponent`/`SampledFailureMode` | Item 4 candidate |
| Q-F — structured `ValidationIssue` error codes for API/agentic clients | **Resolved 2026-09-04 (item 6 / 14A)**: the API surfaces `ValidationIssue.Code/Severity/Message/ObjectPath` verbatim on every response (`validationIssues`), and API request-shape checks use the `API_` code family with request-relative object paths |
| Q-M — bootstrap posterior size vs `Realizations` (index-wrap vs percentile path) | Open (arch §11) |

Closed 2026-08-27 (v2.0-program session 1): the `Network._nodeCount` row was stale — the ruling
was executed 2026-08-03 (six commits, four ratified decisions, 46 tests); Q-H — `NextIntegers`
confirmed public API (three `System.Random` extension overloads); N15 closed as unneeded (the CIF
`bins` knob exists and the strict-output wrinkle keeps its engine-side rebuild); N16 closed by the
weighted statistics landed for 2.2.0. Closed 2026-08-27 (session 2): the F6 re-pin ruling —
approved and executed (`74af2e95…`, its own commit).

## Unscheduled items needing future ratification (not blocking alpha)

- **Life-cycle foundation extensions (recorded 2026-09-06, from the C2 + C1 landing; not
  scheduled).** The landed foundations are the `DeterioratingResponse` wrapper (the tabular
  age→shift law, the evaluation age as external never-hashed state carried onto run clones by
  function id) and the runtime-only epoch-sequence `MeasureLifeCycleRisk` query (cumulative
  house events, chained same-arity hazard replacements, mean-only epochs, the
  exposure-period-consistent plus absorbing aggregates). Recorded extensions, none blocking:
  (1) **parametric deterioration laws** mirroring the RMC-BestFit `TrendFunctions` vocabulary
  (constant/linear/…/logistic/step) as an alternative law representation — the tabular law
  subsumes the first shapes practitioners reach for; (2) **composite/tree bases and wider
  seats for the wrapper** — widening the base allow-list must extend
  `TreeCarriesEpistemicComposite`, `CollectLogicTreeAxes`, `CollectEpistemicVariables`, and
  the sensitivity enumeration in the same change (the epistemic-walker containment rationale),
  and surfacing the wrapped base through `GetReferencedFunctions` is recorded alongside (the
  composite-child parity reading keeps a fractile pin on the base id failing loudly at
  classification today); (3) **conditional intervention exercise** — the named seat on
  `LifeCycleIntervention`: a condition member gating exercise on the state observed at the
  entry's year (the real-options decision rule; least-squares Monte Carlo and its relatives),
  which turns deterministic schedules into policies; (4) **full-uncertainty trajectories** —
  with the age external, an unconfigured epoch's ensemble is already realization-aligned with
  the authored run, while epochs that flip house events or swap hazards change content and
  re-roll those functions' streams (the established content-seed reading; the B8
  configured-ensemble extension entry covers the same discipline per epoch); (5) **a
  serialized `LifeCycleAnalysis`** (the `AnalysisBase`/cost-benefit-driver shape) once the
  runtime contract stabilizes through use — serialized shapes are append-only forever, so the
  definition stays runtime-only until then; (6) **cross-arity and marginal-target hazard
  replacement** — replacing a univariate hazard with a bivariate one (or a linked marginal
  inside a bivariate hazard) needs binding-migration rules the same-arity rule deliberately
  refuses today; (7) **function-swap and added-failure-mode intervention actions** beyond
  house events and hazard replacement. The BestFit artifact mapping for nonstationary
  per-epoch hazards (a parent `UnivariateDistributionBase` at year t via
  `GetParameterValues(t)` on a clone; a per-year posterior `ParameterSet[]` mapped from
  `MCMCResults.Output` through the trend evaluation, feeding
  `ParametricUnivariateHazard.Estimate(IList<ParameterSet>)` named by `HazardReplacement`
  entries) is recorded for the import sessions — zero new hazard types needed.

- **KernelDensity extrapolation surface (observation recorded 2026-08-28, from the A10 close;
  not scheduled).** A10 landed end to end on 2026-08-28 — the upstream `Extrapolation` property
  on the two evaluation wrappers (`TabularFunction`/`EmpiricalDistribution`, numerics
  `bug-fixes-and-enhancements`, riding the 2.2.0 release) and the TotalRisk
  `ExtrapolationPolicy` on the five tabular/nonparametric function types with the Error guards
  (`ExtrapolationVerification`). One adjacent surface was deliberately left outside the ruled
  two-type upstream scope: `KernelDensity` does **not** compose `EmpiricalDistribution` — it
  duplicates the lookup pattern (its own `OrderedPairedData`, transforms, and hard CDF support
  gates) — so it carries no extrapolation surface today. If KDE-backed curves ever need the
  policy (the post-v1.1 KDE-smoothed assurance banding is the plausible consumer), that is a
  new upstream item, not a TotalRisk wiring gap; a no-regression pin
  (`Test_KernelDensity_EmpiricalUnderTheHood_NoRegression`) documents the relationship.

- **Consequence fractile pinning via the coupling columns (recorded 2026-08-28, from the A6
  close; not scheduled).** The epistemic conditioning surface pins functions that own a
  percentile matrix (hazard, transforms, responses, profile transforms); consequence functions
  draw from each failure mode's coupling matrix instead, so a consequence pin is a validated
  no-effect warning today. Extending pins to the coupling columns means overriding column k of
  every consuming mode's matrix — which necessarily pins the paired failure/non-failure
  consequences of that type together (the pairing is the column's purpose) — a small, separate
  ratification when a consequence cross-tab is actually wanted.
- `CompositeTransform` **aleatory `Mixture`** mode — still waits on an engine transform-branch
  analog of the consequence exposure branches (within-realization enumeration). The explicit
  epistemic mixture mode landed 2026-09-02 (arch Q-Y closure): `EpistemicMixture` on all four
  composite clusters with named shared epistemic variables (`EpistemicVariable`,
  conditional-presence hashed; run-scope selector columns overwritten after seeding — zero
  walk-ordinal movement), branch attribution, the mean-only Error/Warning gates, and the
  `EpistemicMixtureVerification` family. The epistemic reading needed no branch-enumeration
  surface — one branch per realization chains like any sampled function — which is why it
  shipped while the aleatory transform mixture stays deferred.
- **Shared epistemic variables on `CompositeConsequence` (recorded 2026-09-02, from the
  epistemic-mixture landing; not scheduled).** An epistemic consequence composite selects from
  its failure mode's coupling draw (consequences are never walked), so it has no per-function
  selector seat for a shared variable to overwrite — binding one is a validation-visible
  non-feature (the property is deliberately absent). Extending sharing there rides the same
  future ratification as consequence fractile pinning via the coupling columns (above): both
  need a ruled seat for per-function conditioning of coupling-driven draws. The exact
  logic-tree enumerator inherits the boundary: it refuses epistemic consequence composites
  with an Error until the coupling seat exists (the aleatory Mixture's exposure branches
  already enumerate exactly within every realization).
- **Enumerating epistemic composites carried by tree probability sources (recorded 2026-09-02,
  from the exact-enumeration close; extended 2026-09-05; not scheduled).** A composite
  referenced through an event- or fault-tree probability source is sampled by the tree's own
  self-contained setup clones — invisible to the walked-cluster axis discovery — so the
  enumerator refuses it loudly (whole-or-not exactness), and the same containment walker now
  makes the mean-only blend gate fire for tree-carried epistemic composites (previously
  silent). The 2026-09-05 tree-source-transform landing extended the walker's reach twice: an
  epistemic `CompositeTransform` inside a probability source's hazard-transform chain is
  refused the same way (chains sample in the tree's isolated clones), and the walker now
  follows external fault-tree transfer targets — closing a pre-existing silent gap in which an
  epistemic composite reachable only through an external transfer's basic event passed both
  gates undetected. The forcing mechanism would actually reach the clones (they keep the
  source function's id and seed inside the walk's ambient scope), so extending enumeration
  through trees is feasible — but the tree evaluation paths compose branch selection with the
  percentile-rescale convention for uncertain branch chains, which needs its own ratified
  verification story before the exactness claim can cover it.
- **The configured-ensemble extension of the configuration-risk query (recorded 2026-09-03,
  from the B8 landing; not scheduled).** `MeasureConfigurationRisk` is deliberately mean-only:
  a house-event state is compute content, so a configured realization ensemble re-rolls every
  content-derived seed and a full-uncertainty delta would mix stream noise into the
  difference. If a configured uncertainty band is ever wanted, the honest construction is a
  ruled comparison discipline (paired seeds are impossible under content seeding; the deltas
  would be statistical with documented k·SE), not a silent extension.

- **The exact certain-failure response fails integration loudly (observation recorded
  2026-09-03, from the B8 landing; not scheduled).** A model whose system response probability
  is exactly one at every hazard level — constructible with a single true house event under an
  Or top gate, with no B8 involvement — validates but fails the run with the engine's
  integration-failure diagnostic ("an integrand evaluation threw"); a flat response of 0.999
  runs fine, so the degeneracy is the exact-one curve. Loud, never silently wrong, and
  pre-existing; worth a diagnosis-and-ruling pass if certain-failure configurations become a
  practitioner pattern.

- **Fault-tree independent transfers nested inside shared targets re-instantiate per
  occurrence (observation recorded 2026-09-03, from the shared-limb landing; not scheduled).**
  The shipped fault compiler forks a fresh variable context at every expansion of an
  independent transfer, so two shared occurrences of a subtree containing one are not the same
  Boolean function — each carries its own copy of the interior clone's variables. The
  event-tree shared-limb implementation deliberately strengthened this: an independent link
  inside a shared limb memoizes its fork per (authored link, caller context), making the limb
  one deep object. Aligning the fault side to the memoized reading would move shipped
  identities (`SharedVariable` ordinals) and sampled values for that nesting, so it is a
  value-moving ruling if ever wanted; both behaviors are documented in the design doc §5.3 and
  the technical references.

- **The staggered-testing alpha-factor convention (recorded 2026-09-05, from the
  common-cause landing; not scheduled).** The shipped CCF kernel maps alpha factors through
  the non-staggered convention; the staggered variant divides differently by the sharing
  count and would arrive as an append-only `FaultTreeCcfModel` member with its own factor
  mapping, never as a silent change to the shipped one. Related recorded items from the same
  landing: published worked-example tables can be pinned as verification constants when a
  reference document is supplied (the exhaustive derived-space enumeration and independent
  closed forms carry the correctness claim today), and tree fragments/copy-paste deliberately
  do not carry CCF groups — a pasted member joins no group, and deleting a member leaves the
  group with a loud unresolved-member error.

- **Latent-factor dependence extensions (recorded 2026-09-05, from the B4 landing; not
  scheduled).** The landed `DependencyType.LatentFactors` mode is component-scope: loadings
  per combination unit induce the matrix the existing kernels consume. Four extensions are
  recorded, none blocking: (1) **cross-component capacity factors** — the joint system path
  is hard-coded conditionally independent given the correlated hazards
  (`Probability.IndependentExclusiveLazy` at the system combination seat), so factors spanning
  components need that call replaced, not configured; (2) **the factor-integral evaluation
  path** — conditional on the factors the units are independent, so the union collapses to a
  low-dimensional exact integral wrapping the independent kernels
  (`Probability.UnionSingleFactor` and `SingleFactorConditionalProbabilities` are the shipped
  upstream seeds); it would lift the enumeration ceilings and replace the PCM approximation
  with an exact evaluation for factor-structured models — a value-moving ruling when wanted;
  (3) **Vanmarcke loadings-from-geometry** (segment length + scale of fluctuation deriving
  the loadings); (4) **Guid-keyed per-mode factor authoring** for cascade layouts (loadings
  are positional by combination unit today, the correlation-matrix convention). Two adjacent
  observations from the same landing: `RiskAnalysisOptions.MaxPathwayCombinations` is
  authored, serialized, and hashed but has no consumer (the per-component lazy enumeration
  passes no cap; only the system-level call consumes `MaxSystemCombinations`), and
  `SystemComponent.FailureModeMultivariateNormal` is a public inspection-only member with no
  production consumer (its tests pin the derived-matrix back-fill) — both left as-is
  deliberately. Also corrected on the record: dependent common-cause is a SUPPORTED v1.0
  configuration (the method setter's coercion is a selection-time reset only; the dependency
  setter is unguarded by design, and `CommonCauseAdjustment` has a dedicated dependent
  kernel) — the session-9 plan's proposed advisory warning was dropped as mislabeling
  supported behavior.

- **B7 — tail-dependent (Student-t) capacity coupling: DEFERRED by ruling 2026-09-05 and
  deferred INDEFINITELY by ruling 2026-09-06 (not a near-term practitioner need; reopening
  requires a fresh ruling).** Nothing was implemented; the design survey is preserved so the
  item can start without re-derivation. *Verified consumption map:* failure-mode coupling is consumed
  analytically, never as draws — JointFailures dependent = `Probability.ExclusivePCMLazy` → HPCM
  over closed-form `MultivariateNormal.BivariateCDF` (deterministic at any dimension);
  CompetingFailures dependent = the seeded Genz lattice inside
  `CompetingRisks.CumulativeIncidenceFunctions` (the only lattice consumer);
  CommonCause/MutuallyExclusive coerced or none. No draw site exists, so a "cheap draws-only
  t-scope" is vacuous. *Upstream inventory:* `MultivariateStudentT` already ships (CDF = K = 200
  χ²-midpoint mixing over the MVN CDF, scipy-validated 1D–4D, deterministic at D = 2, full draw
  APIs including the D+1-coordinate InverseCDF); `StudentTCopula` + `CopulaType.StudentT` ship
  with closed-form tail coefficients; no Genz BVTL/MVTDST port is needed. *Genuine gaps:* a
  t-joint kernel beside HPCM (the χ²-mixture over the UNCHANGED Gaussian HPCM — an exact
  identity, no new conditioning math, deterministic at any dimension; singleton bypass;
  ν ≥ 1e8 bit-exact Gaussian delegation), `MultivariateStudentT.Interval` (≈ 40-line mixing
  loop) plus `MixingStrata` and the value-inert inner-MVN hoist, and
  `CompetingRisks.StudentTDegreesOfFreedom` (null default byte-identical — the F4 protection).
  *Ratified-shape plan:* upstream N1 (Probability kernels + the delegate-core walker refactor,
  value-inert with a pre-captured delta-0 pin; fallback = body duplication) → N2 (the MVT
  surface) → N3 (CompetingRisks); TotalRisk T1 (`CapacityCouplingType` +
  `CapacityCouplingDegreesOfFreedom`, conditional presence, the Validate matrix — ν < 1 Error,
  [1, 2) and > 100 Warnings — and the one t branch in `ComputePathwayDecomposition`) → T2
  (`TailDependenceVerification`: 1M MC oracles with the measured Gaussian-PCM baseline in the
  tolerance discipline, the deterministic ν = 1e8 bit-identical recovery, the tail-coefficient
  pin vs `StudentTCopula.UpperTailDependence`, the t-vs-Gaussian amplification pin) → T3 (the
  competing half). N1 → T1 → T2 is the clean truncation boundary. Release train: the upstream
  items ride the 2.2.0 slate (standing answer, 2026-09-05).

- **Bivariate adaptive-interior follow-ups (recorded 2026-09-05, from the B10 landing; not
  scheduled).** Three items from the two-dimensional adaptive conditional quadrature landing:
  (1) **the F8 allocation profile** — the adaptive interior commits more distinct primary
  abscissas per realization than the fixed grid's evaluation set, and each committed point
  stages and adopts its entry lists, measuring 154.79 GB against the fixed grid's 38.25 GB on
  the F8 fixture (wall +60% full-run, +19% mean-only — the price of the accuracy table in
  `scripts/perf/RESULTS.md`); candidate reductions for the dedicated perf session are pooling
  the per-abscissa staging objects and reusing tensor-region storage upstream, both value-inert
  by construction; (2) **residual bin-count sensitivity through the surrogate probe** — the
  refinement surrogate's normalization scales probe the sampled hazard's fixed conditional grid
  at the configured count, so two bin configurations of one model can adopt slightly different
  (equally converged) meshes; harmless within tolerance and pinned by the consistency asserts,
  but a bins-free probe (e.g. a fixed 21-node probit sweep) would make the adaptive answer
  fully bins-inert if ever wanted; (3) **upstream tensor-region reuse** — `AdaptiveGaussKronrod2D`
  allocates per-region storage per pass; a pooled-region variant is a numerics item for the
  same perf session.

- **A first-class shared-epistemic-variable object (recorded 2026-09-02; not scheduled).** The
  landed sharing identity is the name string on each binder — cheap, mode-portable, and exact.
  If authoring UX ever wants one place to declare a variable (description, weight-vector
  defaults, discoverability before any binder exists), that is a new Id-linked object paying the
  full registration bill (factory/resolver cases, both serialization modes, its own hash
  surface); the run mechanics underneath would not change.
- S-4 — upstreaming `CanonicalContentHasher` to `Numerics.Utilities` (deferred; TotalRisk copies
  the pattern).
- Arch §7.9.9 cascade deferrals: multi-group claimed non-failure states, competing over else-chain
  failure states, cross-group `ExclusivePCM` coupling, numeric `InverseSRP` for cascades.
- The Archimedean `ConditionalCDF` boundary accuracy limitation (documented, deliberately
  untouched under the algorithm-change rule).
- Joint-path independence-only polynomial shortcuts (a performance idea; not to be attempted
  without a ratified re-verification pass).

## Explicitly post-v1.1 (the roadmap's Future-phases block)

UI layer (`RMC.TotalRisk.UI`: element wrappers, `.tra`/`.rmcbf` reading, v1.0-project import,
TRG-line comparison, KDE-smoothed assurance banding, batch orchestration) · Desktop App (WPF
shell) · FDA importer + datasets (the six `BlockedExternalData` traceability rows) ·
Hydrologics cross-engine parity · the `SystemModel` system root (ruling 3) · the shared
`Hydrologics.Risk` assembly. *(`CostBenefitAnalysis` left this block 2026-09-06 — it is the
C5 capability on the v2.0 critical path with a ratified design; see ruling 5 above. The
`PlanRow.EquivalentAnnual` + TR App. H parity scope moved with it, into the design's
decision 19.)*

## Standing facts worth restating

- The source tree carries **zero** in-code debt markers (no TODO/FIXME/HACK/NotImplementedException
  across 407 files); every remaining item is tracked here, in the roadmap, or in the architecture
  spec's §11.
- The traceability matrix maps 116 applicable legacy methods to current tests; the only
  blocked rows are the six FDA importer workflows awaiting external datasets (post-v1.1).
