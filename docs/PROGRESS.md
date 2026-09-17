# Progress Log

## 2026-09-17 — CWBI deployment preparation (branch `cwbi-release-prep`, on top of `v2.0-development`)

**Goal:** make the repository publishable to `cwbi-apps/dst-total-risk` through the same
guarded local-snapshot workflow System-Response and RasProcessingApi use, without changing any
algorithm, result, or seed. Requested by Adam Gohs (CWBI release access only); the source
branch for publication is `v2.0-development` until v2.0 merges to `main`.

**Decisions (Adam, this session):** ship `RMC.TotalRisk.Tests` and `RMC.TotalRisk.Api.Tests` in
the snapshot so both the local preparation and the CWBI-side workflow run the complete fast
suites (the Verification project stays out); service port 8083 under path base `/total-risk`
(dst-src is 8081 `/src`, dst-ras 8082 `/ras`); Haden's `C:\GIT\numerics\packages` feed line in
`NuGet.config` stays untouched; the interim Numerics dependency is served from an in-repo
prerelease package.

**Landed:**
- **Numerics as a package (the planned switch, done early against a local feed).** The five
  csproj `<HintPath>` references to the sibling Debug DLL are replaced by one
  `RMC.Numerics` PackageReference on the library (transitive to every consumer, the perf
  harness included). `scripts/pack-local-numerics.ps1` clones the Numerics checkout at an exact
  commit into a temp directory (never building in Haden's working tree), points its origin at
  the public GitHub URL so Source Link is real, and packs `RMC.Numerics 2.2.0-dev.<sha7>`
  (net10.0, Release) into `local-feed/`. Pinned in `Directory.Packages.props` at
  `2.2.0-dev.7a80e35` (Numerics `7a80e354…`, the `bug-fixes-and-enhancements` head Haden had
  built locally). `NuGet.config` gains the `local-feed` source; the new `nuget.cwbi.config`
  lists only `local-feed` + nuget.org for the container and the CWBI runner. The runbook records
  the exit procedure for the published 2.2.0.
- **Lock files.** `RestorePackagesWithLockFile` in `Directory.Build.props`; `packages.lock.json`
  committed for all six projects. Floating versions in `Directory.Packages.props` were left as
  they were: two consecutive `--locked-mode` restores (root config and CWBI config) reproduced
  the lock files byte-for-byte.
- **API hosting.** `Program.cs` honors a `PathBase` setting and no longer redirects to HTTPS
  outside Development (TLS terminates at CWBI's load balancer; the redirect turned the plain-HTTP
  health probe into a 307). New integration test
  `Test_ProductionHostingUnderPathBase_ServesPlainHttpWithoutRedirect` pins both.
- **Release tooling** (RasProcessingApi's copy, which adds first-publication support for an
  empty destination; the verifier pair from System-Response, whose Alpine base and health
  contract match): `scripts/Publish-CwbiSnapshot.ps1`, `scripts/Prepare-CwbiRelease.ps1`,
  `scripts/cwbi-release/{CwbiRelease.psm1,policy.json}`, `.github/workflows/cwbi-build-push-api.yml`,
  `.github/scripts/{CwbiNuGetAudit.ps1,Verify-TotalRiskImage.ps1}`, the four
  `.github/tests/*.ps1` contract suites, root `Dockerfile` (repo-root context, pinned
  Alpine digests, non-root user, 8083, `/total-risk`, wget healthcheck), `.dockerignore`,
  `.gitattributes` (no line-ending rule; binary markers only), `docs/CWBI_RELEASE_RUNBOOK.md`.
  Source-branch approval is `v2.0-development`. Because the snapshot omits the solution's
  Verification project, every dotnet command in the release path targets the two test projects
  directly (they restore and build the library and the API transitively).
- CLAUDE.md: the interim-dependency note corrected, the layout tree and commands table extended,
  a CWBI Release section added; AGENTS.md regenerated.

**Verified:** F1 perf fixture reproduces its pinned byte gate
`b2e6ea88…` bit-exactly under the packaged Release Numerics (assembly version string
unchanged at 2.2.0.0; Numerics has no `#if DEBUG` code); `dotnet build` 0 warnings; fast suite
**1,634/1,634** + Api **91/91** (+1) in Release; `validate-code-xml-docs.ps1` clean; all four
release contract suites pass (Prepare, Publish, Workflow, Verify-Image under Docker). **Full
rehearsal of `Prepare-CwbiRelease.ps1`** in a throwaway clone (local `v2.0-development` at the
branch head, empty orphan target — the first-publication shape): export + policy + secret scans,
the four contract suites, locked restores, Release builds, both suites green on the exported
tree, the four-project audit clean, the Docker image built and verified live (labels, non-root
user, 8083, `/total-risk/health` → `Healthy`, detailed health JSON), root snapshot commit
`1ff722b0…` with manifest SHA-256 `121c22a1…`. Two defects the rehearsal surfaced and the fixes
that followed: (1) `git archive` under `core.autocrlf=true` exports CRLF, so the verifier
fixtures' embedded `/bin/sh` shims died on their first line — the fixture writer now normalizes
to LF (RasProcessingApi's own fix, taken verbatim); (2) `dotnet list <project> package
--vulnerable` reports only the named project, so the gate now audits all four shipped projects
and the shared parser requires a clean line from each.

**Next:** merge `cwbi-release-prep` into `v2.0-development`, push, then run
`pwsh -NoProfile -File .\scripts\Publish-CwbiSnapshot.ps1 -PrepareOnly` from a clean
`v2.0-development` for the full end-to-end rehearsal before the first real publication (the
one-time setup in the runbook: `cwbi` remote + `git config --local push.default nothing`).
When Numerics 2.2.0 publishes, follow the runbook's exit procedure to retire `local-feed/`.

## 2026-09-10 — API transform-chain increment (wire contract 1.1.0)

**Goal:** expose failure-mode hazard-to-response transform functions through the REST/MCP API
for the two practitioner shapes Haden named — overtopping fragilities keyed to stage-to-depth
and spillway-erosion fragilities keyed to stage-to-discharge, with consequences remaining a
function of stage. The model library already supported the whole feature (`ResponseStage`
chains + `FailureMode.ConsequenceHazardPosition`); the API had never exposed it
(`ComponentMapper` hard-coded an empty chain).

**Rulings (Haden, this session):** (1) all three deterministic transform kinds on the wire —
`tabularTransform`, `linearTransform`, `powerTransform`; (2) `consequenceHazardPosition` is
optional and **omitted defaults to the raw (first) hazard — position 0** (a documented
divergence from the engine's null = last-response-input v1.0 default, in the
`OutputAdjustedFailureModeCurves` precedent), with any chain position `[0, transforms.Count]`
selectable; (3) the canonical example demonstrates the feature (the Overtopping Erosion mode
now routes a stage-to-depth crest offset into a depth-keyed fragility with stage-keyed
consequences).

**Landed (Api + Api.Tests + docs only — zero library edits, perf byte gates untouched by
construction):** `TransformFunctionDto` (flat union on the `ConsequenceFunctionDto` pattern;
`transformedHazard`/`transformedHazardUnit` REQUIRED, linear/power `minimum`/`maximum`
REQUIRED — the model's [0, 100] ctor default is a silent-clamp trap; deterministic only,
`IsUncertain` hard-set false) + three append-only `FunctionTypeNames` discriminators;
`FailureModeDto.transforms` + `consequenceHazardPosition`; `FunctionMapper.ToTransforms`
(label threading through the chain, per-kind mappers, new codes `API_TRANSFORM_REQUIRED`,
`API_TRANSFORM_RANGE_REQUIRED`, `API_TRANSFORM_RANGE_INVALID`); the `ComponentMapper` rework —
the response inherits the LAST transform's output labels, consequences inherit the
bound-position labels, and the **load-bearing `API_CONSEQUENCE_POSITION_RANGE` gate**
(`SystemComponent.AddFailureMode` silently drops an out-of-range binding and the projected
mode re-derives the default, so the model's own range error never fires on this path); a
transform-free mode maps with a NULL position, keeping pre-1.1.0 requests hash/seed/byte
identical (pinned by a canonical-hash twin test). Discovery surfaces: metadata gains the
`transform` function-kind role, the `consequenceHazardPosition` default, and two conventions;
the example and all three MCP tool descriptions updated; `ApiContractInfo.Version` → `1.1.0`
(the `ApiInfoDto` literal now initializes from the const); `docs/api.md` request tree +
concepts + label-inheritance + deferred-features updates; CLAUDE.md API-layer rules +
AGENTS.md regenerated; REMAINING-WORK row 6 refreshed.

**Verified:** Api suite **90/90** (+17: nine `FunctionMapper` transform tests, six
`ComponentMapper` threading/position tests incl. the hash twin, the wire round-trip, and the
integration golden `Test_TransformEquivalenceTwin_MatchesUntransformedOverHttp` — a
Sterbenz-exact −1210 ft stage shift whose transformed run reproduces the untransformed twin
through the full HTTP stack at 1e-12 relative with bit-equality expected, plus the
label-continuity-silence assert); fast suite **1,634/1,634** in Release; `dotnet build`
0 warnings; `validate-code-xml-docs.ps1` clean. The updated example validates and computes
through the existing pins (its depth re-expression is Sterbenz-exact against the 1214 ft
crest, so the example's numbers are unchanged).

**Next:** the 14B remainder is unchanged (store-backed lifecycle, full-uncertainty mapping,
the wider function-kind catalog — uncertain tabular functions, uncertain/composite transforms,
trailing response-to-consequence chains, multi-stage responses, trees, bivariate hazards —
containerization + auth). CB4 (the serialized cost-benefit study) remains the next capability
session; the exhaustive testing campaign follows.

## 2026-09-10 — Capability program session 14: C5 implementation session CB3 — the strategy catalog

**Goal:** execute the approved CB3 plan (preserved at
`~/.claude/plans/session-14-totalrisk-c5-cb3-plan.md`, approved 2026-09-08 with the two
rulings, re-baselined 2026-09-09): the three-tier decision-strategy catalog — rankings, the
decision summary, the Savage/expected regret machinery, chance constraints, epistemic
measures, dominance screens, PMRM, and expected utility — with verification fixtures 18–26,
everything post-processing over published state, all eight byte gates bit-identical at start
and close.

**Baseline (session start, re-verified from the plan-review sweep):** numerics `7a80e35`
clean on `bug-fixes-and-enhancements` (no v2.2.x tag — N4/T8 out of play; sibling Debug DLL
rebuilt); TotalRisk `v2.0-development` clean at `0172ca4` (never push). `dotnet build`
0 warnings; fast suite **1,589 + 73**; **all eight perf byte gates bit-exact** as separate
invocations vs the RESULTS.md pins (F1 `b2e6ea88…` F2 `ac35a7fa…` F3 `e46763ef…`
F4 `8a3a8b52…` F5 `2ae3925b…` F6 `15493553…` F7 `f985ca02…` F8 `c9599e0a…`).

**Mid-session ruling (Haden, 2026-09-10): the Tier-3 live-study conflict → Defer + pin.**
Discovered at fixture 18: every system carrying a `LogicTreeEnumeration` map necessarily
carries a walked epistemic-mixture composite, and the ratified B3 gate refuses mean-only
runs over such composites — while the study's life-cycle trajectories are mean-only
quantifications. Probed end-to-end: the enumerations run (K 3, M 4, N 12, maps published),
the study validated cleanly, then `RunAsync` threw the wrapped Jensen-gap Error from the
year-zero epoch. Ruled: keep the Tier-3 wiring and its tests; add a study Validate Error so
an epistemic alternative is refused loudly before any run (`RiskAnalysis` gained the shared
internal predicate `CarriesWalkedEpistemicComposite()` — extracted from the B3 gate so the
two refusals can never drift, the gate's messages unchanged); restructure fixture 18 to
verify the regret machinery bit-exactly at the engine seam over the genuinely enumerated
published ensembles and pin the live-study refusal as the current contract; fixture 25 drops
its C3-map clause to stored-ensemble Tier 2. The epistemic-capable study trajectory
(enumerated or per-branch-forced epoch quantification) is the recorded design extension.

**Landed (seven commits `4428842..d59606c` + the close-out):** the
`Curve.QuantileTailIntegral` extract-with-delegation (R-C — CVaR delegates verbatim, the
zero lower bound floors to the quantile transforms' own 1e-16, sub-interval additivity and
the CVaR identity pinned bit-exact in `CurveTests`); `DecisionStrategy` (20 members) +
`DominanceVerdict` (6) with pin tests; the six result containers (`StrategyRanking`,
`DecisionSummaryEntry`/`DecisionSummary`, `RegretMatrixResults`, `EpistemicMeasureSummary`,
`ChanceConstraintEntry`, `DominanceEntry`) + `CostBenefitResults` growth by six appended
optional ctor params (existing calls compile unchanged, pinned); the five internal engines
(`DecisionStrategyEngine` — the ranking core with the layer/discipline label authority;
`EpistemicCriteriaEngine` — the A7 token-for-token exceedance mirror, the satisfaction
fraction, the upstream-delegating weighted reductions, the exact-boundary-split tail
average, zero-weight-ignoring extremes, the Hurwicz blend with bit-exact endpoints;
`LecPartitionEngine` — PMRM region means as quantile tail-integral differences with the
(0, b] ≡ CVaR(b) identity by construction, the certainty equivalent over the LEC mass
pairs with log-sum-exp CARA; `StochasticDominanceEngine` — union-knot FSD, stop-loss SSD
with interior log-log crossing abscissae, the gains-oriented weighted-sample variant;
`RegretEngine` — the R-D 7 misalignment reasons incl. the shared-instance escape and the
pure hand-matrix regret computation with sequential mirrored sums); the `ComputeStudy`
wiring at the L848 seam (four private helpers + the shared Tier-2/3 criterion builder per
the R-B ruling; Tier 1 in catalog order incl. the seat-gated PMRM/CE on the year-zero
retained benefit-stream curves, aleatory FSD/SSD pairs, the exact economics family, the
constrained selection over `Objectives[0]`, and the MCDA echo; Tier 2 gated on stored
ensembles with the band rows, classical rules, quantile regret at the ensemble band trio,
epistemic dominance, raw-fraction chance entries, and per-level chance-constrained
selections; Tier 3 gated on aligned maps with block means/SEs over [c·M, (c+1)·M) and the
minimax/expected-regret rankings whose picks are the ranking core's — one tie authority);
diagnostics TRC2005–TRC2009 (TRC2002 stays the recorded hole); 27 engine/wiring unit tests
+ the refusal test; the `partial` split of `CostBenefitVerification` with the nine strategy
fixtures.

**Measured lessons:** `EstimateMeanRiskOnly` defaults TRUE (the v1.0 default) — a
"full-uncertainty" verification fixture that only raises `Realizations` runs one mean pass
publishing a single-slot ensemble, silently degenerate; MSTest's delta compare fails
NaN-vs-NaN, so reproducibility pins over rankings with legitimate NaN slots (the zero-cost
baseline's benefit-cost ratio) compare `DoubleToInt64Bits`; a stored LEC's log-log segment
inflates a down-sweep's mean (the atom story misleads — dominance fixtures must argue on
stored-curve semantics, and closed-form utility fixtures place masses on flat
duplicated-knot segments where the discrete convention is exact); weighted atom quantiles
can exactly reproduce state regrets under co-monotone branches — fixture 18's repaired
branches are deliberately unsorted so the pairing-blind quantile regret demonstrably
differs; the default objective vector is three-wide (PV cost, monetized benefit, SD of
annual Total risk), so the SD axis is a live Tier-2 criterion and the constrained selection
publishes on every default study.

**Verified (close-out, families isolated):** `dotnet build` 0 warnings; fast suite
**1,634 + 73** (Release); `CostBenefitVerification` **26/26** isolated (the nine new
fixtures joining the seventeen; the Savage seam bit-oracle, the classical picks, the exact
tail splits, the analytic dominance verdicts with the independent segment-integration
cross-check, the CARA/CRRA closed forms at 1e-12 with the live partition-to-CVaR bit pin,
the chance parity bit-equal to `ComputeTolerableRiskConfidence` under post-hoc weights, the
three-way designed disagreement with the do-no-harm exclusion, author byte-inertness with
bit-reproducible strategy blocks, and the validation sweep); `EpistemicMixtureVerification`
5/5, `LogicTreeEnumerationVerification` 5/5, `LifeCycleVerification` 11/11,
`ExactLecTailVerification` 2/2 isolated; both validators green; **all eight byte gates
bit-identical at close** — CB3 is gate-proven post-processing, the `QuantileTailIntegral`
extraction and the B3 gate refactor included.

**Next:** session 15 = CB4 — the serialized study + presentation closure + docs (prompt:
`~/.claude/plans/session-15-totalrisk-c5-cb4.md`; the six CB3 blocks join the JSON freeze
and the `DecisionStrategy`/`DominanceVerdict` names become append-only there); the
epistemic-capable study trajectory extension recorded for the design round; **the
exhaustive unit + verification testing campaign follows CB4.**

## 2026-09-09 — Interleave: the upstream distribution-repair campaign (CB3 paused mid-plan)

**Goal:** session 14 (CB3, the strategy catalog) was planned and its plan approved with two
Haden rulings (the Tier-2/3 criterion set = benefit-stream Mean per declared type +
declared risk-measure Level metrics; the `Curve.QuantileTailIntegral` extract-with-delegate),
but the mandatory session-start baseline found F6 moved and Haden redirected the session to
the upstream numerics work: review the GPT-authored distribution-hardening delta
(`94d1713..62424a2` + the doc commit `376e200`), keep every accuracy/precision/hardening
improvement, repair every performance regression, restore parallel tests, reorganize
misfiled helpers, and gate across numerics (net481 + net10), TotalRisk, and BestFit.
**CB3 resumes with a fresh baseline; its approved plan and rulings stand.**

**F6 attribution and ruling:** F6 read `15493553…` against the pinned `bd4a26e8…`
(F1–F5/F7/F8 bit-exact). DLL-swap bisection attributed the movement to the single upstream
commit `b8bf912` (the `Mixture` log-sum-exp/tail rework F6 alone exercises); in-tolerance
(`CompositeHazardVerification` 11/11, `CompositeEngineVerification` 5/5, fast suite green).
**Ruled session-local (2026-09-09): the working F6 target is `15493553…`; the RESULTS.md
table retains `bd4a26e8…` pending the formal re-pin ruling** (the dated RESULTS.md note
carries the full three-state table). The same round exposed the allocation regression —
52.95 GB / ~15–16 s vs the recorded 3.12 GB / ~5.7 s — root-caused to per-call recursive
XML configuration keys (62,512 B + 11.6 µs per `Mixture.InverseCDF` call) and per-call
allocating validation.

**Landed (numerics `bug-fixes-and-enhancements`, seven commits `1b90450..7a80e35`, never
pushed):** restored `Parallelize(ClassLevel)` (suites −30/−35% wall; the serialization
leftover removed); `DistributionSnapshot` — the generalized bitwise configuration snapshot
(exact-type per-family capture incl. the LogNormal/LP3 `Base` and LnNormal physical-moment
surfaces, recursive composites, derived/table-backed defeat → the canonical string stays
the deciding authority) with `CompetingRisks`' Weibull-only fast path generalized (dependent
CDF 31,057 → 136 B/call, −69% wall; the Weibull-gated `DependentCDFCore` arm deliberately
unwidened); the Mixture refresh short-circuit + validation certificate + certified support
bounds + single-walk `InverseCDF` (62,512 → 96 B/call and 11.1 → 0.79 µs — **better than
the pre-hardening 144 B / 0.97 µs on both axes**; CDF 160 → 0 B at wall parity); the
snapshot guard suite (round-trip + the reflection completeness sweep, which immediately
caught `GeneralizedPareto.Lambda`); static log-Pearson routing (56 → 0 B/call on
CDF/LogCDF/LogCCDF/InverseCDF); the ruled file moves (`DistributionEndpointTail` →
`CompetingRisks.cs`, `MixtureLogWeights` → `Mixture.cs`, the two kappa kernels → `Base/`;
`StandardErrorExtensions` stays — public API). Deferred with evidence: the `GammaLogTail`
no-derivative overload (the derivative stream is exit-coupled to the value stream — a
bit-inert variant saves only a Digamma), the CR collection-storm rewrite (LINQ Min/Sum
NaN/signed-zero semantics vs no downstream visibility), the GEV moment cache
(cold path). BestFit's stale ">1 day USER-ONLY" verification rule updated per Haden's
ruling (local instruction files; scoped verification now standard).

**Verified (close-out):** numerics net481 **2,840/2,840**, net10.0 2,853/2,855 with the
two failures = the `Integration`-category live-USGS full-period downloads (transient —
both pass in isolation; also the source of the session-start baseline flakes); TotalRisk
build 0 warnings, fast suite **1,589 + 73**, `CompositeHazardVerification` 11/11 +
`CompositeEngineVerification` 5/5 isolated, **all eight gates bit-exact** (F1–F5/F7/F8 vs
pins, F6 vs the session-local target) with **F6 recovered to 3.11 GB / 4.90 s — beating
the pre-regression 3.12 GB / 5.72 s**; BestFit (building numerics from the repaired tree)
build 0 warnings, fast suites **3,420 + 645 + 444 + 498 all green**, scoped verification
**Univariate 121/121 (23m16s)** and **DistributionFitting 52/52 (1m28s)** — the first
runs under the updated scoped-verification rules.

**Close-out addendum (2026-09-09):** Haden approved the formal F6 re-pin — RESULTS.md now
pins `15493553…` with the fresh `--reps 3` row (0.714 / 4.752 s, 3.11 GB; the hash
reproduced bit-exactly across five runs on three upstream states). **Next:** CB3
re-baselines (numerics expected at `7a80e35`, `bug-fixes-and-enhancements`) and executes
its approved plan — the plan and both plan-review rulings are preserved at
`~/.claude/plans/session-14-totalrisk-c5-cb3-plan.md`, referenced from the re-baselined
session prompt.

## 2026-09-07 — Capability program session 13: C5 implementation session CB2 — the decision framework

**Goal:** execute CB2 per the ratified design's phase table (`COST_BENEFIT_ANALYSIS_DESIGN.md`
v1.0): the metric selectors wired end-to-end through the promoted `SelectScope`/`ExtractMeasure`
and the retained epoch realizations, the CSSL formulary (App. L exact + EWACSLS/CSFP/AACSLS +
disproportionality/ALARP), TEAC, the do-no-harm screen, the discrete ε-constraint sweep with
the three templates executing, frontier + incremental CE/ICA + MCDA, and verification fixtures
12–17 + 27–28 plus the relocated 9–10 and the CB1 fixture extensions.

**Baseline (session start):** numerics `94d1713` clean (ahead 6, no v2.2.x tag; sibling Debug
DLL rebuilt); TotalRisk `v2.0-development` clean at `59ae8e8` (ahead 8 — never push).
`dotnet build` 0 warnings; fast suite **1,547/1,547** + Api **73/73**; **all eight perf byte
gates bit-exact** as separate invocations against their RESULTS.md pins — F1 `b2e6ea88…`,
F2 `ac35a7fa…`, F3 `e46763ef…`, F4 `8a3a8b52…`, F5 `2ae3925b…`, F6 `bd4a26e8…`,
F7 `f985ca02…`, F8 `c9599e0a…`.

**User rulings (2026-09-07, at plan review):**

1. **A scope × metric mismatch is refused.** A whole-horizon metric (an economics metric or
   a Horizon-basis risk measure) declared with a year-by-year constraint scope — or an
   annualized metric declared at the Horizon scope — is a Validate Error naming the
   combination. To keep natural declarations legal, `CostBenefitConstraint`'s `scope`
   parameter became nullable with a metric-aware resolution (EveryEpoch for annualized
   metrics, Horizon for whole-horizon ones — decision 6's own "default for annualized
   measures" phrasing); nothing is serialized yet, so the tightening breaks nothing.
2. **An annualized metric used as an objective, frontier axis, MCDA axis, or ε axis
   resolves at the first epoch** (year zero — the present-condition reading, matching the
   established year-zero echo convention); whole-horizon readings are declared through the
   Horizon bases. This pins the default objective vector's dispersion member.

Plan-ratified conventions (recorded in the plan and the API remarks): TEAC's consequence
term is the monetized equivalent-annual level of the benefit stream (forced by the
min-TEAC ≡ max-net-benefits identity; the absorbing twin follows the CB1 pattern); Horizon
bases are Mean-only on Total/Excess/Fail with the absorbing equivalent-annual level derived
as AbsorbingPV/A(T); a metric's NaN α reads `AlphaLevels[0]`; α re-evaluation always clones
retained curves (trajectories are reference-shared — mutation is off the table) and
re-measures with each system's own per-type consequence thresholds; the automatic ε grid is
GridPoints uniform inclusive values over the fixed-feasible payoff range; the swept bound is
always an upper bound, binding when the fixed-constraints-only optimum violates it, ties to
the first results row; λ̂ ≡ ICA through one shared `TradeOffRatio` authority; do-no-harm
under Off is unevaluated and under WarnOnly advisory (the deferred CB1 notice landed);
reliability-mode consequence metrics resolve NaN with one named `ComputationDiagnostic`
each (TRC2001); declared study constraints evaluate into a per-alternative table so they
are never silently inert; AACSLS's cost base is capital + O&M (the documented reading —
Section D's literal PV_cost token would double-count the operating stream its own term
subtracts).

**Landed (this commit):** `CostBenefitFormulary` (internal pure statics: TEAC,
CSSL(U)/(A) per App. L with the zero clamp, EWACSLS with the IRL floor and equity exponent,
CSFP, AACSLS with the family-consistency clamp, disproportionality, and the first code home
of the ER ALARP band tables {1, 4, 20}/{0.3, 1, 6} with labels never verdicts);
`ComputeStudy` computes the family per row with the per-side individual-risk proxy/override
seats echoed as used, and the do-no-harm screen (Total-stream equivalent-annual reduction
≥ 0 per declared type under the headline accounting) flags rows with offending types;
`CostBenefitMetricResolver` (economics from the rows under headline-accounting twins;
Horizon bases from the trajectory aggregates; `AnnualizedPerEpoch` through
clone → `ComputeRiskMeasures` → a system-scope `SystemRiskResults` → the promoted
`SelectScope`/`ExtractMeasure` verbatim, cached per (trajectory, α); reductions signed;
constraints at the three scopes with NaN never satisfying); `EpsilonSweepEngine`,
`ParetoFrontierEngine`, and `McdaEngine` as internal statics over plain per-alternative
arrays (the hand-matrix seam the CB3 strategy catalog reuses; `InternalsVisibleTo` already
covers both test projects) — the sweep with degenerate-grid handling and TRC2003/TRC2004
diagnostics, the frontier with exact ties both kept, `IsExcludedForNaN`, THREE
always-emitted projections (PV cost vs monetized benefit, EAC vs annualized lives saved,
and the decision-17 PV cost vs ΔAFP reliability projection), the cost-ranked incremental
table, and MCDA with direction-aware min-max normalization, the constant-objective zero
rule, and competition ranks among recommendation-eligible rows; do-no-harm eligibility
flows into every engine (sweep selections enforce; frontier/ICA/MCDA rows stay visibly
marked). Results growth by appended optional ctor params: `AlternativeEconomics` gains the
fourteen decision columns, `CostBenefitResults` gains the individual-risk echo seats, the
declaration echoes, `ConstraintEvaluations`, `EpsilonSweep`, `Frontier`, `Mcda`, and
`Diagnostics`; new sealed containers `EpsilonSweepResults`/`EpsilonSweepEntry`,
`ParetoFrontierResults`/`FrontierProjection`/`IncrementalEntry`, `McdaResults`, and
`ConstraintEvaluation`. Validation: the metric-local horizon-legality Errors
(`CostBenefitMetric`), the scope-compatibility Errors (`CostBenefitConstraint`), the
axis-bound Error for every declared metric (`ValidateMessagesCore` — an out-of-range type
position no longer falls through `SelectScope` into a silent NaN), and the
do-no-harm-under-WarnOnly advisory. `RiskMeasureTests` closes the standing enum-pin gap.
Nothing serializes into any canonical-hash or seed surface; the study stays runtime state
until CB4.

**Verified:** `CostBenefitVerification` **17/17 isolated** — the ten new fixtures: the
App. L family at the hand values (30,000 / 20,000 exactly, the clamp to exactly zero, the
NaN denominator rule) with the basis-invariance lemma bit-exact under a power-of-two
annuity surrogate and at 1e-15 relative under a real one; the equity/failure-prevention/
absorbing arithmetic (the IRL floor engaging one side, the n ∈ {0.5, 1, 2} sweep against
independent `Math.Pow`, Δp_eq against an independent survival computation at the 1e-9
flat-quadrature grade — **measured lesson: the published probabilities inherit the
quadrature bound, not the pure-discounting 1e-12 grade** — CSFP/EWACSLS recomposing from
published values with no delta, AACSLS against independent survival loops); the
closed-form Haimes ε table (ten analytic noninferior points, selections walked in order,
secant trade-offs exact and bracketed by the analytic tangent multipliers, the
1.667-at-ε-13.31 upstream anchor — **measured lesson: a √-then-square bound can land one
ulp off its algebraic ε, so the grid passes the points' own computed swept values**); the
identical-EV conditional-tail discrimination table with the ≤ 100 filter selecting option
two; the tolerable-life-risk template end to end (engine-backed — the recorded superset of
the design's hand-built reading); the frontier/ICA hand set with **ICA ≡ λ̂ bit-exact,
including the reversed-walk pairing (IEEE negation and subtraction reversal are exact)**;
the MCDA arithmetic exactly; the study-α re-measurement **bit-exact against a
directly-configured twin run**; the do-no-harm screen across all three policies (Excess
falls 180 → 60 while Total rises 280 → 460 through background growth; Enforce excludes the
row from the selection it would otherwise win); and the reliability-mode study end to end
(the AFP axis live, every consequence skip named once, the ΔAFP projection carrying the
frontier, mixed modes refused) — plus the CB1 extensions (the null study's
frontier-tie/do-no-harm/CSLS-NaN closures and the stationary bridge's TEAC identity
TEAC_base − TEAC_alt = −NPV/A(T)). `LifeCycleVerification` **11/11 isolated** (untouched;
run for confidence). Fast suite **1,589/1,589** + Api **73/73** (42 new tests);
`validate-code-xml-docs.ps1` green; **all eight byte gates bit-identical at close** (the
second full round — CB2 is post-processing, and the gates prove it). Docs:
`docs/verification/cost-benefit.md` to 17 tests with the new conventions, the README
registry row, the CLAUDE.md matrix row + namespace map + AGENTS.md regenerated.

**Next:** session 14 = **CB3 — the strategy catalog** (prompt:
`~/.claude/plans/session-14-totalrisk-c5-cb3.md`): the three-tier decision-strategy
catalog over the assembled metric matrix (Tier 1 mean-only scalar rules incl. EV/TEAC/BCR/
CSSL orderings, FSD/SSD, PMRM, expected utility/certainty equivalents; Tier 2 per-alternative
stored-ensemble strategies incl. Laplace/maximin/maximax/Hurwicz/mean+kσ, epistemic
variance and CVaR, epistemic-CDF dominance, chance constraints, quantile regret; Tier 3
shared logic-tree states: minimax/expected regret), `StrategyRanking` rows + the
`DecisionSummary` cross-tabulation honoring the do-no-harm exclusions; fixtures 18–26.
Then CB4 (the serialized study + docs) → the exhaustive testing campaign.

## 2026-09-07 — Capability program session 12: C5 implementation session CB1 — kernel, model, parity

**Goal:** execute CB1 per the ratified design's phase table (`COST_BENEFIT_ANALYSIS_DESIGN.md`
v1.0, commit `1f1e04d`): the Section-A kernel promotions and life-cycle extensions proven
bit-inert, every Section-B input record with XML round-trips, `CostBenefitAnalysis :
AnalysisBase` (orchestration + the full validation matrix; study XML deferred to CB4),
per-alternative economics in both accounting conventions with the monetized/economic split,
`PlanEconomics` (the decision-19 v1.0 parity anchor), and the CB1 verification fixtures.

**Baseline (session start):** numerics `94d1713` clean (ahead 6, no v2.2.x tag; sibling Debug
DLL rebuilt); TotalRisk `v2.0-development` clean at `1f1e04d` (ahead of origin — never push).
`dotnet build` 0 warnings; fast suite **1,463/1,463** + Api **73/73**; **all eight perf byte
gates bit-exact** as separate invocations against their RESULTS.md pins — F1 `b2e6ea88…`,
F2 `ac35a7fa…`, F3 `e46763ef…`, F4 `8a3a8b52…`, F5 `2ae3925b…`, F6 `bd4a26e8…`,
F7 `f985ca02…`, F8 `c9599e0a…`.

**User rulings (2026-09-07, at plan review and mid-session):**

1. **Fixtures 9–10 seat in CB2.** The design assigns CB1 fixtures 1–11 while its phase table
   builds the CSSL formulary (App. L exact + EWACSLS/CSFP/AACSLS) those two fixtures test in
   CB2; ruled: the CSSL family and its fixtures land together in CB2, CB1 lands 1–8 + 11
   (the prompt's never-silently-drop rule, split recorded here). Fixture 5's
   frontier/do-no-harm/CSLS asserts and fixture 6's TEAC assert likewise extend at CB2.
2. **`EconomicMetric.MonetizedPresentValueBenefit = 14` appended before first landing.** The
   ratified default objective vector and default frontier axis name "monetized present-value
   benefit", which the ratified 14-member catalog could not select (NetPresentValue = benefit
   − cost screens a DIFFERENT frontier); ruled: append the member now, while the append is
   free — the enum's names become serialized contract at this landing.

**Landed — Section A (commit `06ea033`, proven bit-inert at its own commit):**
`internal static DiscountingSupport` (`AnnuityFactor` moved verbatim, `RiskAnalysis`
delegates; the log-space `DiscountFactor` generalizing the shipped first-year expression —
year-zero exactly 1; the A5 inline annuity deliberately untouched, the stationary bridge
pins it); `SelectScope`/`ExtractMeasure` promoted `internal static`, bodies unchanged;
the life-cycle stream axis — `CreateLifeCycleEntry` reads Total/Excess/Fail per type,
`LifeCycleEpochEntry` gains the two stream lists and `LifeCycleRiskResults` the ten
per-stream aggregate arrays as appended optional ctor params, and `MeasureLifeCycleRisk`
accumulates every stream through ONE `AccumulateStreamAggregates` authority (Total's
operations verbatim in order — the bit-inertness argument; per-epoch scalars computed once,
stream-independent); epoch retention — `LifeCycleDefinition.RetainEpochRealizations`
(default false) attaches each epoch's `DumpMemory()`-applied mean realization to
`LifeCycleEpochRisk.Realization`. Inertness proof at the Section-A commit: fast suite
1,471/1,471, `LifeCycleVerification` 10/10 isolated (stationary bridge + two-epoch closed
form bit-exact), **all eight byte gates bit-identical** (the second full round).

**Landed — the CB1 model and parity (this commit):** the nine declaration enums
(`LifeCycleAccounting`, `MetricBasis`, `MetricForm`, `EconomicMetric` incl. the ruled
15th member, `ObjectiveDirection`, `ConstraintScope`, `AlarpProximity`, `DoNoHarmPolicy`,
`UtilityFunctionForm`) with name/order/value pins; the Section-B records with ctor-throwing
guards + XML round-trips (element = nameof, G17, enums by NAME — wire shapes append-only
from this landing): `CapitalCostEntry`, `RecurringCostSegment` (conditional `EndYear`),
`CostStream` (three kinds under wrapper children), `MonetizationFactor`,
`ConsequenceMonetization` (duplicate positions refused), `LifeCyclePlan` (serializes
interventions inline — `LifeCycleIntervention`/`HouseEventState`/`HazardReplacement` gained
XML forms, replacement hazards written self-contained via `RiskFunctionFactory`; a
serialization-mode seat is the recorded CB4 extension if the study document needs
by-reference replacements), `RiskReductionAlternative` (referenced never-owned system;
deliberately no XML until CB4's study document), `CostBenefitMetric` (the selector union
with both factories; `Kind` discriminator), `ObjectiveDeclaration`, `CostBenefitConstraint`
(sense = the numerics `ConstraintType`, equality refused — the `CopulaType` upstream-enum
precedent), `EpsilonConstraintStudy` + the three template factories as declarations
(tolerable-life-risk with the 1e-3 guideline as a default ARGUMENT, mean-variance,
reliability; the sweep engine is CB2), `UtilityDeclaration`, `PmrmPartition`, and
`CostBenefitOptions` (the full declaration set with the design's defaults — BenefitRiskType
Total, Accounting NonAbsorbing, α {0.01}, DoNoHarm Enforce, the default objective vector
cost/monetized-benefit/SD — plus the option-local Validate matrix). `CostBenefitAnalysis :
AnalysisBase`: replace-to-edit options, ObservableCollection alternatives + designated
`Baseline` (edits clear published state), the full Error/Warning validation matrix (options
carrying CB2/CB3 declarations validate now; the do-no-harm-under-WarnOnly notice lands with
the screen), `RunAsync` = veto → race-safe snapshot validation → ONE
`MeasureLifeCycleRisk` per distinct (System, Plan) pair on the study-wide union grid
(baseline first, retention on, cancellation between evaluations, progress = evaluations
done/total) → decision-3 cost pricing → both-convention monetized/economic aggregates
(identity pricing only under a DECLARED map; empty set ⇒ NaN + Warning) → NPV/NetAnnual/BCR
+ AFP p_eq + year-0 echo + Excess lives saved → publish; authors byte-untouched, runs
bit-reproducible. CB1 results containers (runtime-only until CB4's JSON):
`CostBenefitResults` (the convention echo block + tables), `AlternativeEconomics` (baseline
row 0), `ConsequenceReduction` (type × stream, absorbing twins, monetized ΔPV),
`TrajectoryPoint`. `PlanEconomics.EquivalentAnnualConsequences` anchored to the v1.0 code
exactly (r > 0 loop verbatim incl. the silent beyond-horizon truncation and the
constant-stream behavior; r = 0 the exact-average improvement; the TR App. H discrepancy
recorded in the API remarks).

**Verified:** `CostBenefitVerification` **7/7 isolated** (fixtures 3–8 + 11) — the five
decision-19 parity constants each re-derived by an independent in-test loop (1e-9 abs /
1e-12 rel); the cost-stream oracle on per-year power sums that never touch the annuity
forms (1e-12 rel + the EAC identity); the null study's exact zeros with trajectory dedup
(reference-equal) and the NaN benefit-cost ratio; the stationary bridge's 270·A(20) closed
form with the B8 `MeasureConfigurationRisk` cross-check at NO delta (both queries run the
same clone machinery); the two-epoch (1+r)^(−10) capital vs (1+r)^(−11) first-benefit
convention pairing with the absorbing survival-loop difference; grid telescoping — **probed
bit-exact first and MEASURED at ulp scale: 4.6e-13 absolute on a net present value of
magnitude 115 (≈ 4e-15 relative), so refining a stationary grid re-associates exact
telescoping sums without bit-identical addition; pinned at 1e-13 relative with the
derivation documented** (the design's "bit-identical" phrasing does not survive
floating-point re-association — the C1 stationary bridge's N·ε precedent) — plus
shared-grid deterioration re-aging (the union grid re-ages the baseline at the repair
alternative's plan year); and the monetization split identities (life-only V·ΔPV with an
exactly zero economic aggregate, the identity-priced recomposition at no delta, the
empty-set NaN + Warning). `LifeCycleVerification` **11/11 isolated** — the two-epoch test
extended with per-stream aggregate asserts (the streams coincide analytically on the
background-free model), and the NEW background-split closed form (flat failure 1000 over
flat background 100 ⇒ Fail = 1000p, Total = 1000p + 100(1 − p), Excess = 900p, Background
= 100) with retained-realization row consistency at no delta, Total = Excess + Background
on the retained curves, and retention aggregate-inertness against a retention-free twin.
Fast suite **1,547/1,547** + Api **73/73** (84 new tests); `validate-code-xml-docs.ps1`
green; **all eight byte gates bit-identical at close** (the third full round — baseline,
post-Section-A, close). Docs: `docs/verification/cost-benefit.md` (new run-of-record),
`life-cycle.md` updated to 11 tests, the README family rows, CLAUDE.md matrix + namespace
map + AGENTS.md regenerated.

**Next:** session 13 = **CB2 — the decision framework** (prompt:
`~/.claude/plans/session-13-totalrisk-c5-cb2.md`): the metric selectors wired end-to-end
through the promoted `SelectScope`/`ExtractMeasure` and the retained epoch realizations
(the study-α re-evaluation route), the CSSL formulary (App. L exact + EWACSLS/CSFP/AACSLS
+ disproportionality/ALARP), TEAC, the do-no-harm screen, the ε-constraint sweep engine +
the noninferior set + total trade-off ratios, frontier + incremental CE/ICA, MCDA;
fixtures 12–17 + 27–28 **plus the relocated fixtures 9–10** and the CB1 fixture extensions
(the null study's frontier/do-no-harm/CSLS asserts, the stationary bridge's TEAC). Then
CB3 (the strategy catalog) → CB4 (the serialized study + docs) → the exhaustive testing
campaign.

## 2026-09-06 — Capability program session 11: the C5 CostBenefitAnalysis DESIGN session (the ratified normative design)

**Goal:** the dedicated C5 design session per the 2026-08-27 ruling ("C5 needs its own design
session") and the 2026-09-06 sequencing — produce and ratify the normative
`docs/requirements/COST_BENEFIT_ANALYSIS_DESIGN.md` on the BIVARIATE precedent (legacy audit,
complete type/results design, numbered decision ledger ratified via batched review rounds,
verification strategy, implementation slicing), land the records, and write the CB1
implementation prompt. **Docs, plans, and records only — no library code**; the eight gates
end where the baseline left them.

**Baseline (session start):** numerics `94d1713` clean (ahead 6, no v2.2.x tag — latest
v2.1.4; sibling Debug DLL rebuilt from committed state); TotalRisk `v2.0-development` clean at
`6f70a57` (ahead 5 of the now-live origin — never push). `dotnet build` 0 warnings; fast suite
**1,463/1,463** + Api **73/73**; **all eight perf byte gates bit-exact** as separate
invocations against their RESULTS.md pins — F1 `b2e6ea88…`, F2 `ac35a7fa…`, F3 `e46763ef…`,
F4 `8a3a8b52…`, F5 `2ae3925b…`, F6 `bd4a26e8…`, F7 `f985ca02…`, F8 `c9599e0a…` (F8 4.080 s
mean-only / 124.405 s full / 155.02 GB — near the recorded row; the hash is the gate).
Docs-only session ⇒ the baseline gates are the closing gates.

**Exploration (four parallel passes, all source-anchored):** (1) the legacy repo — the ONLY
v1.0 economics is `PlanRow.EquivalentAnnual` (WPF-side, pure statics; verbatim transcription
into the design doc; r = 0 → NaN, base year discounted one full period, silent beyond-horizon
ramp truncation; no differencing/benefits/costs anywhere; zero test coverage; VB-only), and
**"TR App. H" resolved** — Appendix H of the in-repo `RMC-TR-2022-XX` draft (Eqs. 252–256),
which DISAGREES with the shipped code on the discounting index (base year undiscounted over
b..b+n vs the code's one-period-discounted n terms); (2) the current repo — the C1/A5/B8
surfaces, the `Curve` measure inventory (SD, VaR/CVaR at `Options.Alpha`, ConditionalMean),
`RiskMeasure`/`RiskType`/`TolerableRiskCriterion` as the ready-made constraint vocabulary,
the `SelectScope`/`ExtractMeasure` private-static reuse blocker, the epoch rows' Total-only
capture, and the `EnsembleSummary` epistemic-variance/CVaR gap; (3) numerics — **no
ε-constraint/Pareto class exists**; the "2-objective version" is `AugmentedLagrange` (exposes
the multipliers) + `Test_Haimes_5_2` (pins Mu[0] = 1.67 at ε = 13.31) + two water-economics
net-benefit multiplier tests; `ConditionalExpectedValue(alpha)` is the distribution-level
CVaR; v1 needs zero numerics work under the discrete ruling; (4) the literature — **the
lead's M.S. thesis located and read in full** (Smith 2022, Colorado School of Mines,
open-access): TEAC (Eq. 2.4), CVaR (2.9–2.11 with the Ch. 2-vs-App. A α-notation split),
N_Δ (2.12), the ε-constraint program + trade-off λ (B.6/B.7), **Eq. 4.2** (min TEAC s.t.
EAC_Δ ≤ 0.001, N_Δ ≤ ε₃ — proposed there, implemented discretely by this design), PMRM, and
the worked Tables B.1/2.1/3.4 adopted as verification fixtures; USACE ER 1110-2-1156 App. L
CSSL(U)/(A) verbatim + disproportionality/ALARP bands; the iPresas indicator family
(ACSLS/EWACSLS/CSFP) and Fluixá-Sanmartín 2020 AACSLS (algebraically the shipped C1
absorbing aggregates); HEC-FDA's EqAD guardrail; Reclamation 2022's no-numeric-threshold
negative finding; DAMRAE's thin alternatives layer. The dissertation proposal
(`C:\GIT\Portfolio-Optimization\docs\dissertation_proposal.docx`) was read for positioning:
the PhD is the portfolio tier ABOVE C5 (C4-adjacent); C5 is the single-study
alternatives-evaluation engine beneath it. Parity oracle constants independently re-derived
before pinning (EA cases A–E: 134.970911441500 / 108.287007110536 / 160 / 119.497368419047 /
137.5).

**User rulings (2026-09-06, the design review — four batched rounds + a serialization
follow-up, plus three mid-session scope directions):** the mid-session directions reshaped
the draft before ratification — (a) the collection model (a `CostBenefitAnalysis` owns
`RiskReductionAlternative`s, the user designates the baseline, each alternative carries a
`RiskAnalysis` + cost, stream selectable with "flexibility and lots of options"); (b) **the
ε-constraint method is DISCRETE and manually defined** (the user defines the alternative
array to rank; no continuous nonlinear optimization; mean-variance/mean-CVaR; Lagrangians as
shadow prices; max net benefits s.t. excess life loss ≤ TRG and CVaR ≤ threshold — the
NED-under-constraints framing; dissertation-grade); (c) the full-uncertainty risk-aversion
tier ("minimize maximum regret" and ALL viable risk-based design strategies a dissertation or
journal publication should test) plus the presentation-complete results contract for the
future UI/App. The ratified ledger is the design doc's twenty-two decisions; the review
revisions/additions beyond the drafted recommendations: **default benefit stream = Total**
(fail/non-fail trade-offs Excess alone can miss; CSLS stays Excess by App. L definition);
**the do-no-harm screen** (Total risk cannot increase from baseline; Enforce default);
**standard deviation of risk as the declared secondary dispersion objective** + a
mean-variance ε template; **reliability-based design support on AFP** (metrics, constraints,
the reliability template, all-reliability-mode studies legal, mixed modes refused); **the
study is a formal serialized class** — definition XML in BOTH `RiskSerializationMode`s, a
hash-stripped `RiskAnalysis.Id` for ByReference links, **and study results serialized as
JSON** (the injection ctor restores an estimated study; deep drill-down stays runtime and
re-runs); **four implementation sessions** (CB1 kernel/model/parity → CB2 the decision
framework → CB3 the strategy catalog → CB4 the serialized study + presentation closure +
docs). Also ratified as recommended: the designated-baseline collection model; study-owned
horizon/rate + the study-wide epoch grid + comparability validation; the three-kind cost
model with the epoch-consistent discounting conventions; non-absorbing headline accounting
with the absorbing twins always alongside; epoch realization retention + study-α
re-evaluation; the App. L-exact formulary with the documented NaN/clamp discipline and the
basis-invariance lemma; the exceedance-α pin; opt-in split-accounted monetization with no
shipped WTP/VSL defaults; CSSL(A)-basis disproportionality + proximity-selected ALARP bands;
the metric-selector vocabulary; the discrete ε study with total trade-off shadow prices and
the CE/ICA ≡ trade-off unification; the k-objective frontier + MCDA rules; all three
strategy tiers in v1 (Tier 2 chance constraints on the A7 semantics; Tier 3 Savage regret on
C3 shared states); the honesty clauses (aleatory-from-mean-LEC labeling,
pairing-impossibility documentation); anchor-to-code for the v1.0 parity with the TR App. H
discrepancy recorded; the deferral list (continuous optimization on `AugmentedLagrange`+DE,
iPresas prioritization sequences/CTB → C4, distributional NPV routes, TOPSIS, info-gap, the
sample-CVaR numerics ask-first item, real options on the named C1 seat).

**Landed (docs/plans/records only):** `docs/requirements/COST_BENEFIT_ANALYSIS_DESIGN.md`
v1.0 (ratified — exploration digest with the verbatim v1.0 transcription and the thesis
formulary; the 22-decision ledger; R1–R42; architecture A–I incl. the CB1 kernel promotions
(`DiscountingSupport`, `SelectScope`/`ExtractMeasure` → internal, the life-cycle stream axis
{Total, Excess, Fail} + opt-in epoch realization retention), the input records, the study
class, the normative formulary, the formal decision framework, the strategy catalog, the
results table catalog, the serialized-study wire shape; the CB1–CB4 phase table; the
31-fixture verification plan with the independently re-derived constants); ROADMAP's
future-phases bullet rewritten (C5 on the critical path); REMAINING-WORK ruling 5 (the
ratified design) + the post-v1.1 block correction; this entry; the CB1 implementation prompt
at `~/.claude/plans/session-12-totalrisk-c5-cb1.md`.

**Verified:** docs-only — the baseline gates stand (1,463 + 73 green; eight hashes
bit-exact); no library, test, or verification source touched; tree committed on
`v2.0-development`; nothing pushed.

**Next:** session 12 = CB1 per the ratified design (the prompt above; baseline re-checked at
write time) → CB2 → CB3 → CB4 → **the exhaustive unit + verification testing campaign (the
ruled critical path)** → the import surface (`session-11-totalrisk-imports.md` re-baselines
after the campaign) → N4/T8 whenever Haden re-packs, tags, and pushes 2.2.0 → Phase 12
hardening → Phase 13 release prep → Phase 14B → C4 at Haden's convenience → the publish
session.

## 2026-09-06 — Capability program session 10: C2 deteriorating responses AND C1 the epoch-sequence life-cycle axis (the life-cycle foundations)

**Goal:** the life-cycle/real-options FOUNDATIONS under the ruled scope discipline — C2 (the
age-indexed deteriorating response wrapper with the evaluation year as external never-hashed
state) closed completely before C1 (the ratified epoch-sequence architecture with the
runtime-only trajectory query) opened; everything beyond the foundations recorded, not built;
every existing model byte-identical with all eight gates proving it at every close. The
session supersedes the imports prompt, which re-baselines to session 11.

**Baseline (session start):** numerics `43a6927` clean (no v2.2 tag, 5 ahead — the session-9
hand-off exactly; sibling Debug DLL rebuilt and frozen); TotalRisk on `v2.0-development`
clean at the 2026-09-06 scope-rulings commit — read as `b30ee7b` at pre-flight, relabeled
`e91d00d` when the concurrent history-excision session (the VerificationModels data-commit
removal + the first push of the branch) rewrote the lineage mid-session; the rewrite is
content-identical for everything tracked, the post-rewrite `.gitignore` commit `5acd24e`
became the effective session base, and Haden's interleaved docs-only `83ebe53` (the
AdaptiveGaussKronrod file-rename gotcha note, CLAUDE/AGENTS 2 lines) landed on it with zero
compute impact — both session landings build on that lineage. B1 relocated to
`C:\GIT\System-Response` and B7 deferred indefinitely per the rulings commit (REMAINING-WORK
carries the text). Fast suite **1,369/1,369** + Api **73/73**; **all eight perf byte gates
bit-exact** against their RESULTS.md pins (F8 on the adaptive-interior pin `c9599e0a…`) — the
pins are results-content hashes, indifferent to the relabeling and proving the rewrite
compute-inert here too.

**User rulings (2026-09-06, batched at plan time):** (1) C2 as specced — `DeterioratingResponse`
wrapping a base response plus an OWNED tabular age→shift law (`UncertainOrderedPairedData`,
strictly ascending ages ≥ 0, optional per-ordinate uncertainty under the co-monotonic
single-percentile convention, linear in age with boundary hold, Δ(0) ≠ 0 a Warning), positive
shift weakening (base evaluated at h + Δ(age)), the evaluation age runtime-only, the base
through `FunctionEntry` in both modes with verbatim pending-marker re-write, projected-identity
hash; parametric trend laws mirroring the BestFit `TrendFunctions` vocabulary recorded. (2) v1
bases restricted to `TabularResponse`/`ParametricResponse` with Validate-Error refusals for
every other base kind and for the wrapper as composite child (dual guard), tree probability
source, multi-stage stage, or under a bivariate parent — the single-stage univariate seat
legal; each refusal a recorded extension carrying the epistemic-walker checklist. (3) C1 as
the epoch-sequence reading with a runtime-only definition/query (`MeasureLifeCycleRisk`); the
serialized `LifeCycleAnalysis` recorded; v1 actions = house events (the configuration walk
reused directly — zero extraction) + same-arity chained hazard replacements; conditional
exercise a NAMED seat only. (4) BOTH aggregation conventions from one mean-only trajectory —
the exposure-period-consistent non-absorbing block (stationary case reproducing
`MeasureExposurePeriodRisk`) plus the absorbing survival-weighted variants; full-uncertainty
trajectories recorded. Folded micro-rulings presented with the plan: pending-base re-write
YES; the wrapped base NOT surfaced through `GetReferencedFunctions` (composite-child parity —
a fractile pin on the base id fails loudly at classification); the age setter throws on
NaN/negative; the default law is the single zero ordinate.

**Landed — C2 (`9949d24`):** `DeterioratingResponse` + the internal shifted-axis
`ShiftedUnivariateDistribution` (the range-guard template: CDF(x) = base.CDF(x + Δ), location
measures shifted, dispersion delegated, zero shift bit-reproducing the base); D = 1 (the law
column) with the base re-seeded `HashCombine(seed, base.CanonicalHash(), 0)` and the
parametric posterior-capacity gate; the full landing checklist (`ResponseFunctionType.
Deteriorating`, the factory case, TWO kitchen-sink registry entries, the discriminator pins);
the seat refusals in `CompositeResponse` (dual guard), `ProbabilitySource`, and `FailureMode`
(multi-stage + bivariate-parent); both serialization modes with resolver repair and the
verbatim pending re-write; and the session's structural discovery — **`RiskAnalysisRunContext.
Capture` now carries authored evaluation ages onto the run's cloned instances by function id**
(the snapshot clone alone resets never-serialized state; found by the engine twin diverging
0.2145 vs 0.3256, fixed at the one seat, a no-op for wrapper-free models, unit- and
twin-pinned). The engine twin also surfaced the consequence-binding subtlety: the re-authored
twin must pin `ConsequenceHazardPosition = 0` because the capacity shift is response-internal
while the last-response-input default binds consequences to the transformed signal. 43 unit
tests in `DeterioratingResponseTests` + 8 across six existing classes; docs
(`life-cycle-analysis.md` + `response-functions.md` + the index rows) and the CLAUDE.md
matrix/namespace rows (plus the stale "F1–F7" perf note corrected to F1–F8).

**Landed — C1 (`32ef74b`):** the runtime-only records `LifeCycleDefinition` /
`LifeCycleIntervention` (the named conditional-exercise seat in its remarks) /
`HazardReplacement`; the unpersisted containers `LifeCycleRiskResults` / `LifeCycleEpochRisk`
/ `LifeCycleEpochEntry`; and `RiskAnalysis.MeasureLifeCycleRisk` — epoch boundaries =
sortedDistinct({0} ∪ evaluation years ∪ intervention years) with every year in [0, T−1] (a
horizon-year epoch would be zero-span, refused) and one intervention entry per year; each
epoch a mean-only quantification on fresh self-contained clones via the configuration-risk
machinery called directly, carrying the cumulative last-wins house states, the chained
same-arity hazard replacements matched against the LIVE element assignment (factory-cloned
once per epoch so authored instances are never event-wired; A→B@5 then B→C@20 composes while
re-targeting A refuses as unreachable; arity mismatches loud), and every deteriorating
response at the epoch's start age; every epoch failure wrapped with its start year; aggregates
in the exact exposure-period expression shapes plus the absorbing survival-weighted
cumulative/PV (absorbing P_T ≡ P_T documented, not duplicated); label echoes per entry and per
epoch; the authored model byte-untouched. 46 unit tests across seven new files; docs
(uncertainty-analysis §6.7, the results-catalog block, the life-cycle page's query half, the
REMAINING-WORK extension bullet incl. the BestFit nonstationary artifact mapping for the
import sessions).

**Verified (families isolated):** `LifeCycleVerification` **10/10** (≈ 12 s) — the unit-slope
transform-equivalence bit-oracle across ages, realization-for-realization one-stream knowledge
parity, the age-zero base identity, the engine mean-only twin bit-exact including the
loss-exceedance ordinates, full-uncertainty two-run byte reproducibility, the stationary
bridge collapsing every `MeasureExposurePeriodRisk` percentile slot with no delta at 3.5% and
0% discounting (run at the engine's 100-realization floor — the N = 2 exact-mean design is
below it, so the mean slots carry the documented count-times-epsilon summation rounding,
asserted 1e-13 relative), the exact two-epoch closed form (the 0.375-AND tree configuring
0.2 → exactly 0.5; independent power-form annuities and a per-year survival loop at 1e-12
relative; undiscounted PV ≡ cumulative with no delta), per-epoch re-authored configuration
twins bit-exact at both scopes, the deterioration-monotone/intervention-drop trajectory shape,
and the 200-realization author byte pin. Fast suite **1,463/1,463** + Api **73/73** at both
closes (1,369 + 48 C2 + 46 C1); **all eight perf byte gates bit-exact at the baseline and at
both closes** (three full rounds this session; nothing moved, nothing re-pinned).

**Next:** session 11 = the external-import surface (the re-baselined imports prompt at
`~/.claude/plans/session-11-totalrisk-imports.md`, now carrying the nonstationary
artifact→per-epoch-hazard mapping recorded this session); C4/C5 design rounds whenever called
(C5 consumes the C1 trajectories for NPV/BCR/real-option economics and the A5
equivalent-annual identity); N4/T8 whenever Haden re-packs, tags, and pushes 2.2.0 (the packed
nupkg remains stale); then Phase 12 hardening (the F8 allocation profile stays queued there) →
Phase 13 release prep → Phase 14B → the publish session.

## 2026-09-05 — Capability program session 9: B4 latent-factor dependence AND B10 two-dimensional adaptive bivariate quadrature (B7 deferred by ruling)

**Goal:** the session-9 slate in ruled order — B4 (latent-factor capacity dependence at
component scope), B10 (the adaptive secondary-axis quadrature), B7 (tail-dependent capacity
copulas) — whole-or-not per item, all ratification questions batched at plan time, the
session-start protocol (git states, sibling Debug rebuild, suite, eight gates, fresh A2
evidence sweep) mandatory before any code.

**Baseline (session start):** numerics `7a3f0e4` clean (no v2.2 tag, 4 ahead — the session-8
hand-off exactly); TotalRisk `7f7aca6` on `v2.0-development` clean; fast suite **1,346/1,346**
+ Api **73/73**; **all eight perf byte gates bit-exact** against their RESULTS.md pins; the
fresh A2 evidence sweep reproduced the recorded convergence study without contradiction
(smooth {1.104e-3, 4.418e-5, 4.418e-7}; cusp {6.93e-4, 8.22e-5, 3.81e-6}; legacy seismic
{0.353, 0.0491, 1.98e-3} at bins {20, 100, 1000}).

**User rulings (2026-09-05, batched at plan time + two in-flight):** (1) **B4 approved as
designed** — component-scope `DependencyType.LatentFactors`, conditional-presence loadings,
derived matrix into the existing kernels, zero upstream; (2) **B10 route** — "No reason for
backwards compatibility here. Just make all bivariate hazards use 2d adaptive AGK," clarified
"The whole reason we added 2d agk to numerics was for bivariate hazards in totalrisk. Review
Numerics and TR and design it to use 2d agk" ⇒ `AdaptiveGaussKronrod2D` unconditionally, no
knob, hashes/seeds hold, values move, F8 re-pins, re-anchors cite the ruling; (3) **the A2
diagnostic stays** as the quarantined fixed-grid instrument whose Richardson limit is the
standing independent cross-check; (4) **the fixed-u seats** — "2D+ component systems with
joint rule use vegas. 2D bivariate hazards in a single component use AGK2D … what if 2D
bivariate hazard is in a multiD joint risk compute with Vegas? In that case, it needs a
secondary axis adaptive conditional sweep given the vegas evaluation on the primary hazard
axis. This is a must have, so we need to just go ahead and do it now" ⇒ the per-slice adaptive
sweep shipped at every fixed-u seat; (5) **the two upstream AGK2D recorder regression pins**
added to Test_Numerics, riding 2.2.0; (6) **B7 DEFERRED** — nothing implemented, the complete
prepared plan recorded in REMAINING-WORK; (7) **release train** — B7's future upstream items
ride 2.2.0 (standing answer). In-flight: **the probit coordinate** ("Probit coordinate
(Recommended)") — the conditional axis integrates in z = Φ⁻¹(t) with the φ(z) Jacobian folded
into recorded masses; and the plan's proposed dependent-common-cause advisory Warning was
dropped on evidence (a SUPPORTED v1.0 configuration — the correction is recorded in
REMAINING-WORK).

**Landed — B4 (`fc3d2b2`):** `DependencyType.LatentFactors = 4` (append-only); new
`Systems/Components/LatentFactor` (Name/Description metadata, pipe-G17 `Loadings`, factor
ORDER semantic); `SystemComponent.LatentFactors` + `AddLatentFactor`/`RemoveLatentFactor`
with item-PropertyChanged staleness, the conditional `<LatentFactors>` child persisted AND
hashed only under the mode with count > 0 (factor-free and non-mode components byte/seed
identical — pinned), the `UpdateMultivariateNormal` derivation branch (ρᵢᵢ = 1 assigned,
ρᵢⱼ = Σf λᵢf·λⱼf in declared factor order, back-filled), the PD gate on the derived matrix,
the configuration Validate matrix (≥ 1 factor, length = combination-unit count, λ ∈ [−1, 1]
finite, Σλ² ≤ 1), coercion parity with the matrix mode, and the riding hygiene (the
`FailureModeMethod` doc falsehood fixed — coupling is analytic, no draws; the dead
`RiskAnalysis._jointMultivariateNormal` removed). Verified: `LatentFactorVerification` 5/5
isolated — dense-equivalence bit pins (derived ≡ λλᵀ; PCM decomposition bit-identical between
LatentFactors and CorrelationMatrix twins; engine mean-only bit pin on a deterministic
fixture; competing twins at D = 3 within lattice tolerance), the exchangeable Simpson factor
integral cross-anchored to `Probability.UnionSingleFactor` at 5e-8 with the measured 8.4e-4
PCM approximation error asserted at 2.5e-3, 1M two-factor and 1M competing MC oracles, and
full-run reproducibility (a lesson recorded: `EstimateMeanRiskOnly` defaults TRUE and
full-run `RiskResults[i]` is realization i — published means live on `MeanRiskResults`).
Docs: `failure-mode-combination.md` §5.1, `docs/verification/latent-factor.md`, README row,
matrix rows. Extensions recorded in REMAINING-WORK (cross-component factors, the
factor-integral evaluation path, Vanmarcke loadings, per-mode authoring;
`MaxPathwayCombinations` authored-but-unconsumed and the inspection-only
`FailureModeMultivariateNormal` left as-is on the record).

**Landed — the numerics pins (numerics `43a6927`):** two Test_Numerics regressions on
`AdaptiveGaussKronrod2D` — the recorder flushes the frozen composite on budget/iteration
exhaustion, and Σ recorded weights = domain area on every non-Failure status (25/25
net10.0+net481); proven engine-inert by gate re-run.

**Landed — B10 (the ruled value-moving package):** the two-dimensional adaptive conditional
interior. α (additive/single-component): `IntegrateComponentAdaptive2D` — one
`AdaptiveGaussKronrod2D` pass per stratification strip over (u, z = Φ⁻¹(t)), steered by the
balanced probability+consequence surrogate (3-slice deterministic scale probe; reliability =
probability-only), fair-share strip budgets max(441, MaxEvaluations/activeStrips), per-strip
Jacobian-folded mass rescale to the exact strip width (sanity 1e-3), `ConditionalColumnLedger`
grouping flushed nodes by exact abscissa, ONE staged `ComputeRiskBivariateColumns` replay per
distinct x (every downstream one-point-per-abscissa gate verbatim), endpoint columns +
`SealExhaustive` unchanged; the S6 body survives renamed `IntegrateComponentConditionalGrid`
(univariate + the A2 instrument, which keeps the fixed grid structurally via the
conditional-override ctor slot). β (every fixed-u seat — joint VEGAS, AFP probe,
`HazardLevelSensitivity`, endpoint columns): the per-slice 1-D adaptive probit sweep
(`BuildAdaptiveConditionalMesh` + `ConditionalQuadratureMesh.AdoptProbitExhaustive`,
budget 21×bins, endpoint sweeps granted the strip fair share). Disciplines: mean/probes
Tolerance + MinDepth 2, ensemble EnsembleTolerance + EnsembleMinDepth, Abs 1e-15 pinned.
Nothing new serialized/hashed; `RiskAnalysisOptions` gains no field; `SecondaryIntegrationBins`
keeps its place as the instrument ladder + sweep-budget price. **The three-fix convergence
chain, each measured:** the K−G estimator was blind to the t ∈ [0.999, 1] layer in t-space
(+0.586% budget-insensitive) ⇒ the probit-coordinate ruling; sequential budgeting starved
tail strips at the 441 floor (+12% → pofMax +15% localized by the A9 retained-detail probe) ⇒
fair-share strips (3.7e-6); endpoint columns at 21×bins carried 64% of the mass under-refined
(±1% default-vs-1000 ratios) ⇒ the sweep-budget grant (5/5). **And the ensemble-scale
carrier fix:** residual-last unit-sum normalization rounds NEGATIVE when a probit-edge node's
mass (w·φ(z) ~ 1e-16) sits below the summation's rounding noise — found by the 4 bivariate
`EngineReproducibilityVerification` fixtures crashing at ensemble scale; both adopters now
carry the residual on the LARGEST-mass node (first on ties; unreachable-negative from
rounding), unit-pinned tail-node regressions added.

**Verified (families isolated):** `BivariateRiskVerification` 5/5 re-anchored citing the
ruling — the SRP probe ≈ 3.7e-6 relative from the exact union-grid closed form at BOTH bin
configurations (fixed grid: 0.353 default / 1.98e-3 at 1000), the per-slice sweep ≈ 5.3e-12
(pinned 1e-9), oracle bands re-derived k·SE + 1e-4, the historical overshoot regimes replaced
by |ratio − 1| consistency pins, the v1.0-arrangement >50× superiority assert replaced by
both-arrangements-within-0.5% (the axis-choice guidance RELAXED), the collapse-vs-joint
joint-side strict-shrink retired (both configurations adaptive; collapse-side shrink kept);
`CopulaDependenceVerification` 8/8 — the convergence study re-routed (fixed series through
the instrument via `InstrumentFailureProbability`, doubled-twin ladder parity bit-exact), the
NEW adaptive adequacy layer (all three fixtures at quadrature scale at every budget), the NEW
Gumbel/Joe θ = 8 extreme-dependence robustness probe (measured 4.6e-12/3.3e-12 vs dense
closed forms, pinned 1e-9), retained bands re-documented as upper bounds;
`EngineReproducibilityVerification` 7/7 (bit-identical repeats, metadata/mode inertness,
thread counts, pinned seeds — all on the adaptive path). Fast suite **1,369/1,369** (+
`LatentFactorTests` 5, +2 carrier regressions, re-anchored adaptive twins/instrument
contracts) + Api **73/73**, Release gate green, validation script green, zero warnings.
**Gates: F1–F7 bit-exact** (the inertness proof; F1 the univariate zero-overhead pin);
**F8 re-pinned** `c9599e0a…` (single-rep ≡ `--reps 3` bit-exact) with the dated value-moving
section in RESULTS.md — mean-only 3.152 s (+19%), full 121.776 s (+60%), alloc 154.79 GB
(4.0×, the committed-structure driver recorded for the dedicated perf session; the old pin
`7833ad5f…` retained as audit trail). Docs: bivariate-hazards.md rewritten (probit adaptive
interior, accuracy table, instrument section, axis guidance relaxed), risk-integration.md
bivariate subsection, BIVARIATE_RISK_DESIGN decision-7 superseded-annotation, both
verification pages' new runs of record, README rows.

**B7:** deferred by ruling — nothing implemented; the complete prepared plan (verified
consumption map, upstream inventory, genuine gaps, the N1→N3/T1→T3 shape with the N1→T1→T2
truncation boundary) recorded in REMAINING-WORK with the ride-2.2.0 standing answer.

**Next:** session 10 per the re-baselined prompt (`session-9-totalrisk-imports.md` →
session-10, new HEAD + counts + the new F8 pin) — the imports slate; B1/C1/C4/C5 remain
design-session gated; N4/T8 ask-first (the packed 2.2.0 nupkg remains stale pending Haden's
re-pack).

**Goal:** land B5 end to end per the ratified design — an optional ordered hazard-transform
chain on `ProbabilitySource` so a tree node (event-tree chance, fault-tree basic event) can be
keyed on a derived hazard axis (overtopping depth, duration, warning time) rather than the
driving hazard, one landing covering both tree kinds through the shared source seat — plus the
axis selector retiring the bivariate-tree-source guard. B6 (parametric CCF) as the follow-on
only if B5 closes clean.

**Baseline (session start):** numerics `7a3f0e4` clean (the tree was dirty at plan time —
uncommitted `CompetingRisks.cs`; the user committed on request per the protocol's stop-and-ask;
no v2.2 tag, 4 ahead of origin); TotalRisk `91d52f5` on `v2.0-development` clean; fast suite
**1,295/1,295** + Api **73/73**; **all eight perf byte gates bit-exact** against their
RESULTS.md pins — no bisect needed (`7a3f0e4` "Support legacy competing-risks matrix
placeholders" proved engine-inert).

**User rulings (2026-09-05, batched at plan time, all as recommended):** (1) **seat shape** —
ctor-injected immutable ordered chain (`IReadOnlyList<ITransformFunction>`) legal on the
tabular and reference kinds, serialized as a conditional `<HazardTransforms>` child through
`FunctionEntry` in BOTH modes (resolver-repaired markers), identity = ordered transform
content hashes; (2) **sampling** — each chain entry contributes its own dimensions,
content-seeded clone streams (the `FailureMode`/referenced-response convention), realization
mode reads the clones, percentile mode applies the one consistent percentile, importance
sweeps keep one draw per class; (3) **bivariate guard retired with the axis selector** —
`BivariateSourceAxis` (Primary/Secondary, conditional attribute) + a non-empty chain supplying
the other coordinate evaluates the clamped slice p(h) = S(h, t(h)); an undeclared bivariate
reference keeps the original Error verbatim; (4) **greenfield family**
`TreeSourceTransformVerification` AND both discovered epistemic-gate gaps fixed in-session.

**Design-verification findings (three parallel source sweeps before planning):** the
evaluation surface has **seven** production paths per tree kind, not six — realization-mode
referenced responses bypass `ProbabilitySource` and read the sampler-bound clone — and the
aligned/off-axis dispatch is duplicated across four seats (the importance sweeps are separate
copies); the aligned tabular arm ignores its hazard argument entirely, so no aligned fast path
can survive a transform; `ProbabilitySource.CanonicalToken()` and the two realization-reference
sextet arms are dead code (left dead, documented); `LinearTransform` defaults `IsUncertain`
true (the v1.0 default — a fixture lesson); and **two pre-existing silent gaps**: the C3/B3
containment walker could not see an epistemic `CompositeTransform` behind a tree source
(`IResponseFunction`-typed) and skipped external fault-transfer targets that the B8 walk
follows.

**Landed (`v2.0-development`):**

- **The seat** — `ProbabilitySource` gains `HazardTransforms` + `BivariateAxis` (three new
  ctors; the reading ctor parses both for every kind so malformed pairings round-trip
  shape-preserving and fail Validate), conditional-presence serialization and identity (every
  chainless source's form, hash, and seed byte-identical — gate-proven), dimension/determinism
  folds (+ `HazardTransformDimensions`, `HazardTransformsAreDeterministic` for the plan
  compilers' nested-tree special cases), the extended `Validate` matrix (scalar-with-chain,
  misplaced axis, bivariate-in-chain, axis-without-chain, axis-on-univariate Errors; the
  transformed table freed from tree-axis alignment; owner→chain→target label-continuity
  Warnings via a new optional owner-label parameter), and `CloneForFragment` carrying the
  chain as live references. New `BivariateSourceAxis` enum (Trees-colocated, append-only).
- **Evaluation** — mean/percentile chain application INSIDE the evaluation sextet (single
  funnel: the two importance seats and the Validate sweeps ride it with zero edits; the
  aligned trio redirects to the off-axis route at the transformed hazard, branch-guarded so
  the transform-free hot path is untouched); realization mode at the two tree dispatch seats
  through the sampler-bound clones (`ApplyTransformRealizations` shared static; the aligned
  realization entry THROWS under a chain — the four-seat duplication tripwire); the bivariate
  slice evaluated through `EvaluateBivariateSurfaceOn` (live function on mean/percentile, the
  binding's clone surface on realizations).
- **Sampling** — both `SetupSampler`s clone + content-seed each chain entry from the class's
  child-stream base (`HashCombine(base, transform.CanonicalHash(), position)`; event base =
  the `childStreamOccurrence` counter — renamed from `responseOccurrence`, existing
  consumption byte-unchanged; fault base = the unified-variable ordinal) and flatten the clone
  percentiles into the parent matrix after the source's own dimensions; the nested-clone
  dimension checks subtract `HazardTransformDimensions`.
- **Invalidation** — a fourth `TransformFunctions` bucket (canonical-hash fingerprints +
  PropertyChanged subscriptions) on `TreeDependencyCollector`/`TreePlanDependencies`,
  populated by both plan compilers; a live chain-transform edit invalidates the compiled plan
  (pinned: `IsUncertain` flip moves the plan's dimension count).
- **The walker fixes** — `TreeCarriesEpistemicComposite` now sees chain-carried epistemic
  composite transforms (`ChainCarriesEpistemicTransform`) AND follows external fault-transfer
  targets (the pre-existing gap closed): both shapes now trip the C3 enumeration refusal and
  the B3 mean-only blend gate loudly, each pinned by a new gate test; verified that no
  transform type can carry a response or house event, so `ApplyHouseEventStates` needs no new
  arm (the config reach through a transformed source is family-pinned).

**Verified:** fast suite **1,331/1,331** (36 new: the `ProbabilitySourceTests` seat matrix,
`BivariateSourceAxisTests` pins, `EventTreeTransformedSourceTests` +
`FaultTreeTransformedSourceTests` — incl. content-seed expectation tests reconstructing the
clone streams from the documented recipe — the two `LogicTreeEnumerationTests` gate tests, and
the kitchen-sink transformed-source registry entry); **`TreeSourceTransformVerification` 6/6
isolated** (3.5 s) — the two-transform reconstruction bit-oracle at N = 1,024, the dyadic
pre-transformed equivalence bit-exact at the response surface AND behind mean-only engine
twins, the fault inclusion–exclusion oracle at 1e-14 with the transformed event
bit-reconstructed, the bivariate-slice engine twin bit-exact, full-run byte reproducibility +
the configuration reach through a transformed source, and the conditional-presence pin;
touched families re-run isolated — `EventTreeVerification` **20/20** (incl. the shared-limb
partial), `FaultTreeVerification` **11/11**, `FaultTreeMonteCarloVerification` **7/7**;
**all eight perf byte gates bit-identical** post-implementation (F5/F7 the tree tripwires —
the zero-overhead proof); Release gate + both validators green.

**Docs:** `EVENT_AND_FAULT_TREE_RESPONSE_DESIGN.md` amended under the ratification (status
block, §1.2 non-goal annotation, §3.2 source semantics, §8 chain sampling, §9.1 fingerprints,
§10 serialization/identity); `BIVARIATE_RISK_DESIGN.md` decision-10 tree arm retirement note;
technical-reference `event-trees.md` (the new chain/axis section) + `fault-trees.md`;
`docs/verification/tree-source-transform.md` + the README family row; the five Ported Types
Matrix rows in CLAUDE.md (Trees, both tree responses, RiskAnalysis, BivariateResponse) and
AGENTS.md regenerated.

**Recorded boundaries:** one chain per source (a surface with both axes independently
transformed is out of scope); the transformed lookup inherits the established off-axis
endpoint behavior; the dead sextet arms stay dead; unresolved-reference re-saves keep the
seat's existing drop precedent.

**B6 — parametric common-cause failure (same session, its own ratification round, all four
answers as recommended):** (1) attempt now; (2) the **alpha-factor kernel with beta/MGL as
parameter facades** — one per-multiplicity factor vector f_k (alpha non-staggered
f_k = k·α_k/(C(n−1,k−1)·α_t); beta f₁ = 1−β, f_n = β; MGL f_k = (Πρ_i)(1−ρ_{k+1})/C(n−1,k−1))
with the exact per-member identity Σ C(n−1,k−1)·f_k = 1 holding for all three; (3)
**content-equal members with ONE shared basis draw** per group per independent context —
declaring the group is the exchangeability statement, the basis rides the first member's
source through the ordinary variable machinery (B5 chains included) and derived events are
deterministic factors on it; (4) as scoped — `FaultTreeCcfGroup` on `FaultTree` with a
conditional `<CcfGroups>` child, n ≤ 6, plain same-tree basic-event members, compile-time
expansion into derived variables the exact diagram evaluates unchanged, greenfield family.

**B6 landed:** `FaultTreeCcfGroup` + `FaultTreeCcfModel` (INPC group: name/description
metadata, model/parameters/members compute content; permissive reads, the factor kernel with
loud invalid throws, the configuration-error matrix incl. the content-identical
exchangeability check); the `FaultTree` surface (`CcfGroups`/`AddCcfGroup`/`RemoveCcfGroup`,
group-edit plan invalidation via PropertyChanged, `FindCcfGroup`/`FindNode`, cross-group
membership validation, conditional serialization + reading); the compiler expansion
(per-(group, context) slot sets — one basis via `GetOrCreateVariable` on the first member,
per-member synthesized Or occurrences over shared derived slots keyed by combination mask,
derived identities carrying model/parameters/size/multiplicity/member positions + the basis
source identity; basis ordinals appended after the walk outside the diagram; partial-context
coverage diagnosed loudly; invalid groups fall back to plain expansion with their errors as
plan diagnostics); two-pass evaluation at `EvaluateTopCurve` and `EvaluateImportanceSample`
(derived = factor × basis, order-independent); `SetupSampler`/`Validate` skip derived slots;
cut sets, node importance, and exact importance report the derived display names, and the
fault node-importance uncertain flag now reads the slot's own determinism (value-identical
for every existing model) so a group registers as ONE knowledge quantity.

**B6 verified:** 15 new unit tests (`FaultTreeCcfModelTests`, `FaultTreeCcfGroupTests`,
`FaultTreeCcfExpansionTests` — exact beta And/Or closed forms through the diagram, the
one-shared-draw realization pin, hash add/remove symmetry, round trips, cut-set names,
live-edit invalidation, blocking diagnostics, the split-context refusal) + the kitchen-sink
CCF registry entry; **`FaultTreeCcfVerification` 4/4 isolated** (3.2 s) — the exhaustive
derived-product-space enumeration with the independently re-derived alpha split (1e-14), the
exact Greek-letter union closed form + the 2.825 first-order rare-event coefficient and the
common-cause union discount at Q = 1e-8, the bit-identical beta/MGL two-member facade
identity, and the engine-scale exact closed form + the one-knowledge-quantity importance pin
+ full-run byte reproducibility. Fast suite **1,346/1,346**; touched families re-run isolated
(`FaultTreeVerification` 11/11, `FaultTreeMonteCarloVerification` 7/7,
`TreeSourceTransformVerification` 6/6); all eight perf gates re-run bit-identical (F7 the
group-free byte-inertness proof); Release gate + both validators green. **Recorded:** the
staggered-testing alpha convention is a future append-only model member; published
worked-example tables can be pinned as constants when a reference document is supplied — the
exhaustive enumeration and independent closed forms carry the correctness claim; fragments
and copy/paste do not carry groups (pasted members join no group; deleting a member leaves a
loud unresolved-member Error).

**Next:** the session-9 prompt.

## 2026-09-04 — Phase 14A: the REST API + MCP server (stateless round-trip compute), and the v2.0-development branch rename

**Goal:** switch gears from the capability program to Phase 14, per the 2026-09-03 user
directive (superseding the 2026-08-17 "after the alpha" timing): stand up `RMC.TotalRisk.Api` —
REST + MCP over the model library — with the near-term surface the Dam Screening Tool needs: a
**stateless round-trip compute** (complete JSON definition in → mean-only results out, DST owns
all memory), the DST model shape (deterministic tabular stage-frequency hazard; a definable
failure-mode list each with a deterministic tabular SRP and a day/night exposure-weighted
composite-mixture fail consequence; one day/night non-fail mixture on the response-free path),
full-featured JSON settings, and both **unadjusted and adjusted** marginal failure-mode results
with risk contributions. Also rename the development branch: the program is well beyond a v1.1.

**User rulings (2026-09-03/04):** (1) branch + docs move to the v2.0 identity while the three
csproj version stamps stay 1.1.0 until release prep — the run manifest records the assembly
version into every results payload, so stamping 2.0.0 moves all eight perf byte-gate hashes and
is done as one deliberate re-record; (2) the wire contract carries `components[]` from day one;
(3) mean-only first — `estimateMeanRiskOnly=false` gets a structured 400
(`API_MEAN_ONLY_REQUIRED`) until the full-uncertainty increment.

**Landed (TotalRisk `v2.0-development`, `cc3d4e5` + `46328d6` + the closeout commit):**

- **Branch renamed** `v1.1-development` → `v2.0-development` (local; the user coordinates the
  remote), with CLAUDE.md/README/BIVARIATE_RISK_DESIGN identity updates and AGENTS regenerated.
- **`src/RMC.TotalRisk.Api`** (net10.0, `Microsoft.NET.Sdk.Web`) on the confirmed
  `RMC.BestFit.Api` template **minus the store**: `ApiControllerBase.ExecuteAsync` (typed
  envelope; structured `validationIssues` carrying `ValidationIssue.Code/Severity/Message/
  ObjectPath` verbatim — resolving open question Q-F — with the `API_` family for request-shape
  checks that echo offending values and request-relative paths; ±Infinity response audit → 500;
  400/499/500 map), camelCase wire options (string enums, ignore-null, NaN as a named literal),
  `AddOpenApi()`, CORS, health/info endpoints, and the **stateless** streamable-HTTP MCP server
  at `/mcp` (`run_risk_analysis`, `validate_risk_analysis`, `get_metadata`,
  `get_example_request`) reusing the same singleton services.
- **The contract:** `POST api/risk-analyses/compute` + `validate` (run-free twin), `GET
  example` + `api/metadata`. Request: analysis metadata + declared consequence-type axis,
  `components[]` (tabular hazard with strictly-descending AEP; failure modes with tabular
  responses and per-declared-type consequence lists; `compositeMixture` day/night branches with
  Σweights = 1; non-fail consequences on the single response-free path; combination settings),
  the full `RiskAnalysisOptions` mirror (every field nullable → engine default), and
  `resultOptions.includeCurves`. Label inheritance keeps minimal payloads clean (hazard pair and
  declared-type pair flow into blank function labels; a blank primary consequence name inherits
  the failure-mode name, which labels the results row). Response: provenance (content hashes,
  seed, versions, `apiContractVersion` 1.0.0), the effective-options echo (replayable),
  computation warnings/diagnostics, and the mean results tree — per-mode **unadjusted** marginal
  curves plus **adjusted** shares (`OutputAdjustedFailureModeCurves` defaults TRUE on the API —
  documented divergence), contributions at mode and component scope, five streams × stats
  (NaN → null) × LEC/hazard-frequency/conditional-mean/profile arrays.
- **Mapper rules that bite:** `OptionsMapper` sets `UseDefaults = false` whenever any of the
  eight integration knobs is supplied (the run-start defaults re-application would overwrite
  them); enum strings never silently default; AEP order violations are structured errors, never
  reorders; the results tree maps from the realization tree only (`RiskResults.Summary` is null
  on mean-only); failure-mode rows are failure paths only — the non-fail path rides the
  component background/non-fail streams.
- **`src/RMC.TotalRisk.Api.Tests`** (MSTest.Sdk 3.6.4, linked MSTestSettings): 73 tests —
  mapper request-shape matrices, the UseDefaults pin, controller status contracts, MCP tool
  round trips, plus `WebApplicationFactory` integration: the **EAD golden** (the eight-knot
  damage-frequency scenario as hazard + identity non-fail consequence reproduces the exact
  closed form 52,085.41 at 1e-4 relative through the whole HTTP stack — linear-in-probability
  interpolation pinned; VaR/CVaR/σ against their closed forms), **byte-identical results +
  provenance across identical requests**, the structured-400 contract, and the raw JSON-RPC
  `tools/list` + `tools/call run_risk_analysis` round trip (complex typed tool params bind).
- **Process:** both Api projects registered in `validate-code-xml-docs.ps1` ($codeRoots + build
  gate — full XML docs enforced; Authors block stays library+Verification-only); `docs/api.md`
  live (concepts, endpoints, contract trees, MCP section, deferred features); ROADMAP Phase 14
  re-sliced 14A (landed) / 14B (store-backed lifecycle, full-uncertainty mapping, wider
  function catalog, auth/containers); REMAINING-WORK item 6 + ruling 2 supersede note + Q-F
  resolved; CLAUDE.md gains the API Layer section, layout/commands rows, and the updated future
  enhancements bullet; AGENTS regenerated.

**Verified:** `dotnet build` 0 warnings across all five projects; `dotnet test -c Release` —
Api suite 73/73 + fast suite 1,295/1,295 (Verification correctly excluded);
`validate-code-xml-docs.ps1 -Configuration Debug` fully green with the Api projects enforced.
**The model library is untouched** — no compute, serialization, or seed surface moved, so no
verification family re-runs and all eight perf byte gates stand.

**Next:** 14B when demanded (store-backed lifecycle, full-uncertainty results mapping, wider
function kinds on the wire, containerization + auth for the DST deployment); the alpha sequence
(items 1–5 in REMAINING-WORK) resumes unchanged; the user pushes `v2.0-development` and retires
the remote `v1.1-development` when ready.

## 2026-09-03 — v2.0 program session 7: B9 shared event-tree limbs

**Goal:** execute session 7 of the capability program — **B9, shared-limb links in event
trees**: a cloned limb that samples and computes identically to its source, mirroring the
fault-tree occurrence plan's context-keyed unified-variable scheme onto the event-tree seams.
The design doc listed shared event-tree reuse as a deliberate non-goal *"unless a future,
separately approved dependency feature says otherwise"*; the capability plan's B9 row records
the approval, and the session presented the verified design for ratification before any code.

**Session-start baseline (the mandated protocol):** numerics HEAD `2b57771`, clean, 3 ahead of
origin, **no v2.2.x tag** (identical to hand-off — T8/N4 did not fire); sibling Debug DLL
rebuilt and frozen. TotalRisk `dcb05bd` clean; build 0 warnings; fast suite 1,264/1,264; all
eight gates as separate invocations — **bit-exact to every pin**, no movement, no bisect.

**Design verification (every Item 1 claim checked against source before planning):** three
findings made B9 cheaper than scoped — `LinkMode` was already serialized unconditionally on
every link node AND already stamped into the projected-identity wrapper, so selecting
`SharedLogicalEvent` is automatically the deliberate hash event with zero conditional-presence
work; the cycle guard makes a second same-class factor on one root-to-leaf path impossible, so
sharing moves draws only and mean/percentile curves are analytically invariant; and absent
shared links every (node, context) key is reached exactly once, so the class machinery is
byte-inert by construction (F5, built in code from 24 `LinkIndependent` calls, safe by design).
One real semantic fork surfaced: the shipped fault compiler forks a fresh context at EVERY
expansion of an independent transfer, so an independent transfer nested inside a shared fault
target re-instantiates per occurrence — shared copies of such a subtree are not deeply
identical there.

**Rulings (AskUserQuestion, all as recommended):** (1) the B9 design approved as presented —
(ChanceNode, context) sampling classes, shared inherits/independent forks, one
binding/clone/draw per class, conditional `SharedVariable` ordinals stamped only on classes
with ≥ 2 occurrences, `LinkShared` overloads, `EventTreeReadScope`, class-level importance
draws, the design doc amended; (2) **deep identity** — a nested independent link inside a
shared limb memoizes its fork per (authored link, caller context), so the limb is one deep
object ("samples and computes identically" holds unconditionally; the fault side stays as
shipped, the asymmetry recorded in REMAINING-WORK); (3) **extend `EventTreeVerification`**
with the reconstruction/invisibility/variance oracle set as a fourth partial file.

**Landed (`a36bdc2` model + unit tests; `cdc9cbf` verification + corpus + its docs page):**
- **The plan** (`EventTreeOccurrencePlan`): context threading through `Expand`;
  `EventTreeSamplingClass` keyed (authored chance node, context) carrying
  dimensions/determinism/nested-response hash (the `GetOrCreateVariable` pattern, nested
  compiles memoized per class); the `_linkContexts` deep-identity memo; per-plan class
  collection in first-occurrence pre-order with `SamplingDimensions` = Σ classes; and the
  conditional stamped identity — the unstamped path is the prior code verbatim, and the
  stamped rebuild (underlying identity + wrapper modes + `SharedVariable` numbers over
  shared classes only) runs only when a class unifies ≥ 2 occurrences.
- **Sampling** (`EventTreeResponse.SetupSampler`): bind once per class at its first canonical
  occurrence, register every member occurrence's path to the same `SamplingBinding` — the
  evaluation path (`EvaluateSource`) untouched. `EventTreeLinkNode` accepts both modes
  (undefined modes throw `ArgumentOutOfRangeException`; the reader mirrors the fault reader's
  `InvalidOperationException`); `EventTree.LinkShared` internal/external overloads;
  `EventTreeReadScope` (the `FaultTreeReadScope` sibling, entered in the read ctor,
  `GetOrAdd` on embedded external link targets with the divergent-embed rejection);
  `TreeNodeImportance` event passes draw once per uncertain class in first-occurrence order
  and vary a class as one knowledge quantity (bit-inert absent sharing).
- **Unit tests** (+17 → fast suite 1,281/1,281 with the corpus pass): the bitwise shared-draw
  halving pin and the independent anti-pin; mean/percentile bit-invisibility (with the
  documented two-explicit-branch constructive condition); hash movement/restoration and
  metadata inertness; the sharing-group disambiguation (equal-content limbs, different
  groupings, different hashes); two-mode round trips; external read-scope unification +
  divergent-embed throw; the deep-identity nested-fork halving chains; `LinkShared`
  authoring + cycle rollback; materialize value movement; paste-clone joining the source
  class; importance class draws (exact path-summary halving, equal first-order indices); the
  kitchen-sink registration ("EventTreeResponse shared limb"); and the corpus **shared-twin
  pass** — all 128 fixed-seed cases rebuilt with every serialized link mode rewritten,
  checking validity, branch-mass exhaustiveness, both persistence modes, tolerance-grade mean
  equality, and identity stability/movement.
- **A session finding the corpus caught immediately:** the mode attribute participates in each
  occurrence's identity token, tokens drive canonical sibling ordering, and sibling sums
  evaluate in canonical order — so flipping a link mode can reorder a ≥ 3-term compensated
  sum at ulp scale. Sharing's mean invisibility is analytic; bit-grade cross-mode comparisons
  require ≤ 2 explicit branches per sibling group. Documented in the tests, the verification
  page, and the tech ref.
- **Verification** (`EventTreeSharedLimbVerification`, family **20/20 isolated**, 46 s): the
  Numerics-primitive single-draw reconstruction bit-oracle (N = 1,000, seed 10202801 —
  `LatinHypercube.Random` + `ToPositiveSeed` + `CurveSample` + the explicit mass algebra,
  bit-equal every realization); the two-column twin oracle (exactly one column assignment
  consistent, the single-draw collapse excluded); the exact 1.8 closed-form variance ratio
  (measured **1.79077** at N = 4,096; ensemble means 0.300000005/0.299999909; mean and median
  curves bit-equal); engine mean-only bit-identity between the modes (AFP 0.3, EAD 225); the
  full-run byte-reproducibility pin plus published-ensemble-mean agreement (0.300028 vs
  0.300012) and the retained-ensemble variance ordering (measured 1.64615 in the documented
  1.8 ± 0.5 band at N = 200) — with the session lesson that full-run `RiskResults[i]` is
  realization i and the published ensemble means live on `MeanRiskResults`; and the at-scale
  external round trip (one class, one hash, bit-equal realizations after the round trip).
- **Docs:** the normative design doc amended under the ratification (status line; the §1.2
  non-goal retired; §3.2/§4/§5.3/§8/§10/§12 updated incl. the deep-identity reading and the
  conditional-ordinal identity rule); event-trees.md (links/sampling/serialization/identity/
  sibling/importance sections); the verification page (the shared-limb section with the
  2026-09-03 run of record and measured values); REMAINING-WORK (the program note; the
  fault-side nested-transfer asymmetry recorded as a future value-moving ruling); the Ported
  Types Matrix (EventTreeResponse + Trees rows); AGENTS.md regenerated.

**Gates at the B9 commits:** build 0 warnings; fast suite 1,281/1,281 (Release and Debug); both
validators green; all eight perf gates bit-exact (F1 `b2e6ea88…`, F2 `ac35a7fa…`,
F3 `e46763ef…`, F4 `8a3a8b52…`, F5 `2ae3925b…` — the B9 tripwire, unmoved, F6 `bd4a26e8…`,
F7 `f985ca02…`, F8 `7833ad5f…`).

**B8 — configuration risk (landed `bd5c743` impl + tests + verification; docs following).**
B9 closed clean, so the session executed the follow-on per its prompt, source-verifying the
seams first (mutable INPC `FaultTreeHouseEventNode.State`; `SystemComponent(XElement)` +
`RiskAnalysisOptions(XElement)` round trips; the settable `Options`; `FractilePin` as the
input-record precedent; `MeanRiskResults` as the published-mean surface) and ratifying two
questions (both as recommended): the design as scoped, and the **mean-only boundary** — a
configured house state is compute content, so a configured ensemble would re-roll every seed
and mix stream noise into the delta; the extension is recorded, not built.
- **Landed:** runtime-only `RiskAnalysis.MeasureConfigurationRisk(IReadOnlyList<HouseEventState>)`
  — SelfContained component clones; the containment walk applying (functionId, nodeId, state)
  overrides across element-assigned functions, external transfer targets, tree-referenced
  responses, structural links, and composite-response children, with the loud unmatched-override
  refusal (the fractile-pin pattern) and every live instance of one function id reconfigured
  (one physical equipment reading); two mean-only twin runs (options round-tripped,
  `EstimateMeanRiskOnly` forced) read from `MeanRiskResults`; the unpersisted
  `ConfigurationRiskResults`/`ConfigurationRiskEntry` (system + per-component
  baseline/configured/change/ratio AFP — NaN ratio at zero baseline — and per-type expected
  consequences, label echoes, applied-override display labels); `HouseEventState` in Analyses.
  The author model is byte-untouched: published JSON, `IsEstimated`, component hash, and the
  authored house state all pinned.
- **Tests** (+14 → fast suite **1,295/1,295**): re-authored bit parity, author inertness,
  unestimated-author path, the no-op configuration reproducing the baseline exactly, the
  external-transfer nested reach, the refusal matrix, and the two container contracts.
- **Session findings (recorded in REMAINING-WORK):** the published-surface lesson — after a
  full run `RiskResults[i]` is realization i (a mean-only run publishes a single-entry
  ensemble) and the published ensemble means live on `MeanRiskResults` (measured, not assumed:
  the retained ensemble mean was 0.30003 while `RiskResults[0]` returned realization 0's
  0.17438 verbatim); and the pre-existing loud degeneracy — a system response of exactly one at
  every hazard (one true house event under an Or top, no B8 involved) validates but fails
  integration with the engine's diagnostic, while a flat 0.999 runs fine. The B8 fixtures gate
  the house event behind an And with a 0.9 basic (0.2 → 0.92) instead of forcing certainty.
- **Verification:** greenfield **`ConfigurationRiskVerification` 4/4 isolated** (2.5 s) — the
  two-component re-authored twin oracle bit-exact on every system/component slot with the
  untouched component exactly zero-change; the exact flat-response closed forms (0.2 → 0.92,
  change 0.72, ratio 4.6, and the same 4.6 on the consequence ratio because a flat response
  factors out of the consequence integral); the cross-kind reach into a fault tree carried by
  an event-tree probability source; and the full-run author byte pin
  (docs/verification/configuration-risk.md).
- **Docs:** the fault-trees.md "Configuration risk" section, the verification page + README
  rows (the event-tree row refreshed for the shared-limb extension in passing), the Ported
  Types Matrix (RiskAnalysis + Results rows), REMAINING-WORK (the configured-ensemble
  extension, the certain-failure observation, the program note).

**Gates at the B8 close:** build 0 warnings; fast suite **1,295/1,295** (Release); both
validators green; all eight perf gates bit-exact to the same pins.

**Next:** Haden's B1 design session over the standing draft
(`~/.claude/plans/b1-bayesian-updating-design-draft.md`) then B1; B10 route (i) whenever the
value-moving change is ruled (A2 supplies the error measurements); B4 needs a numerics
authorization; C1/C5 design sessions whenever called; N4/T8 whenever Haden re-packs, tags, and
pushes 2.2.0.

## 2026-09-02 — v2.0 program session 6: C3 exact logic-tree enumeration (the sampled mode's exact oracle)

**Goal:** execute session 6 of the capability program — **C3, exact logic-tree enumeration**:
branch combinations × continuous knowledge samples with weight = the branch-weight product ÷ the
per-combination count, producing exact weighted fractiles with zero Monte Carlo noise on the
branch axis, riding the A4 weighted-ensemble carrier. Designed against source (the B3 forcing
mechanisms, the run flow, the weight plumbing, and the fractile-pin constraints all verified
before planning) and ratified via four AskUserQuestion rulings, all as recommended: **auto
Id-keyed forcing** for unbound epistemic composites (every walked epistemic composite
enumerable; no naming hoop, no hash movement); **Error** for epistemic consequence composites
(whole-or-not exactness — the coupling seat stays the recorded future ratification); **N = K·M
with explicit M** on a runtime-only property (the run sizes its isolated options snapshot;
`Options.Realizations` unused under enumeration — the `HazardLevelSensitivity` own-count
precedent); and **LogicTree\*** naming (the practice term; "branch enumeration" collides with
the aleatory exposure-branch surface).

**Session-start baseline (the mandated protocol):** numerics HEAD `2b57771`, clean, 3 ahead of
origin, **no v2.2.x tag** (identical to hand-off — T8/N4 triggers did not fire); sibling Debug
DLL rebuilt and frozen. TotalRisk `09ad798` clean; build 0 warnings; fast suite 1,246/1,246;
all eight gates as separate invocations — **bit-exact to every pin**, no movement, no bisect.

**Landed (`66c125b` impl + unit tests; `9aaf607` verification + its docs; docs/bookkeeping
following):**
- **The enumerator:** runtime-only `RiskAnalysis.LogicTreeEnumerationRealizations` (int? M —
  never serialized, hashed, or seed-affecting; setter guards ≥ 1 and invalidates IsEstimated;
  default null byte-inert, gate-proven). One full-uncertainty run of N = K·M realizations:
  axis discovery across components (one axis per shared `EpistemicVariable` with bitwise
  binder-weight agreement enforced; one per unbound epistemic composite by function Id, with
  equal-Id self-contained copies merged as one knowledge quantity; deterministic order — bound
  by ordinal name, then unbound by Id; the three walked clusters and `SystemComponent` carry
  the cycle-safe `CollectLogicTreeAxes` collectors over the shared `LogicTreeAxisSeed`
  registrar), block [c·M, (c+1)·M) forced onto combination c by overwriting selector columns
  with the branch's cumulative-midpoint percentile (the `SelectMixtureChild` algebra verbatim)
  through the extended `EpistemicSharingScope` — name-keyed for bound axes, **Id-keyed for
  unbound** (`TryGetColumnById`, populated only by the enumerator's forcing scope; the walked
  composites consult it only when unbound, so ordinary runs are byte-inert) — with applied-key
  reconciliation refusing any unreached axis loudly (the fractile-pin refusal pattern), and
  the exact products wᵢ = W(c)/M published as the run's realization weights. Everything
  downstream composes untouched: weighted bands and summary (A4), tolerable-risk confidence
  (A7), retention and re-banding (A9), given-data VoI (B2).
- **Attribution:** the unpersisted `LogicTreeEnumerationMap`/`LogicTreeAxis`/`LogicTreeBranch`
  (Results) published on `RiskAnalysis.LogicTreeEnumeration` — axes, branches with forcing
  percentiles, exact lexicographic combination weights, `CombinationOf`/`BranchIndexOf`/
  `RealizationWeight` — the post-run attribution authority (the run computes on clones);
  cleared with published state, absent from restored analyses. The three diagnostic re-seeds
  rebuild the forcing columns from the published map at matching design size
  (`EnterEpistemicScopeForRederivation`), so post-run sensitivity/VoI re-derivations are
  bit-exact — pinned by the author composite's re-derived selector column equaling the map's
  forcing percentiles bitwise.
- **Gates (Validate + race-safe run re-check on the clones):** Errors — mean-only; user
  `RealizationWeights` (the enumerator owns the vector); no epistemic axes; epistemic
  consequence composites; fractile pins targeting enumerated composites (pins apply after the
  scope override and would silently break exactness); differing binder weight vectors on a
  shared variable (the sampled-mode Warning stays); **tree-carried epistemic composites** —
  see the finding below; single-realization enumerations; K > 65,536; N > 1,000,000.
  Warnings — K > 4,096; N > 10,000; the effective-N scrambled-Sobol power-of-two note.
- **Session finding (a real boundary + a closed B3 gap):** `GetReferencedFunctions` yields
  element-assigned functions only — an epistemic composite referenced through an event- or
  fault-tree probability source is invisible to axis discovery while the tree samples it in
  isolated self-contained setup clones. Enumeration therefore **refuses tree-carried epistemic
  composites loudly** (never a silently part-sampled "exact" result), via a new containment
  walker (`TreeCarriesEpistemicComposite`, (function, inside-tree)-keyed visited set crossing
  composite children and tree sources incl. links); and the **same walker closes the
  previously silent B3 mean-only gap** — a tree-carried epistemic composite in a mean-only run
  now fires the ratified blend Error instead of silently blending. The forcing mechanism would
  in fact reach the tree clones (they keep the source id and seed inside the walk's ambient
  scope — verified in source), so extending enumeration through trees is feasible future work,
  recorded in REMAINING-WORK with the percentile-rescale caveat.
- **Tests** (+18 → fast suite **1,264/1,264**): `LogicTreeBranchTests`/`LogicTreeAxisTests`/
  `LogicTreeEnumerationMapTests` (counts, lexicographic decode, weights, guards);
  `LogicTreeEnumerationTests` (the property contract, the full gate matrix, the guardrails,
  the exact deterministic 2×3 tree at M = 1 with weights/map/weighted-mean identities, the
  shared-variable axis forcing both binders, the nested-unbound Id-keyed forcing through an
  aleatory parent, the tree-carried refusal triple — enumeration Error, mean-only Error, valid
  sampled config — the bitwise sensitivity re-derivation, and the published-map lifecycle);
  `EpistemicSharingScopeTests` extended (Id columns, applied-key sinks, nesting restore of all
  four scope slots).
- **Verification:** new family **`LogicTreeEnumerationVerification` 5/5 isolated** (2 m 14 s)
  — the closed-form analytic tree (per-realization manual-run identity at 1e-12 under the
  pinned discipline, exact weight products at 1e-15, the hand-blended weighted mean, and exact
  weighted exceedance fractions at every threshold probed between adjacent atoms — the
  convention-free exact-weighted-fractile statement), the roles-reversed convergence (the
  enumerator ≡ the manual blend at 1e-12; the sampled N = 4,096 ensemble lands every
  realization on an enumerated atom and converges at 4·SE), shared-variable forcing through a
  nested binder inside an aleatory mixture, the post-hoc re-band byte-equal to the published
  bands under the map's weights (the A9+A4 composition on the C3 vector), and the 0.02-weight
  branch resolved with exactly one 64-realization block carrying exactly 0.02 mass vs the
  sampled mode's exact N·ω = 20-of-1,000 allocation (docs/verification/
  logic-tree-enumeration.md). The touched B3 family re-run: **`EpistemicMixtureVerification`
  5/5 isolated** (4 m 10 s).
- **Docs:** uncertainty-analysis.md §6.6 (the full contract), composite-functions.md §2
  "Running the tree exactly" + the verification-link header, the new verification page, the
  epistemic-mixture.md roles-reversed cross-note, the verification README rows (adding the
  session-5-omitted epistemic-mixture row in passing), the Ported Types Matrix (RiskAnalysis,
  Results, and CompositeCombinationType rows), REMAINING-WORK (the program note; the
  consequence-seat item extended with the enumeration Error; the new tree-source enumeration
  future-ratification record), AGENTS.md regenerated.

**Verified:** build 0 warnings throughout; fast suite **1,264/1,264** (Debug and Release);
code/XML + traceability validators green (one process-language catch fixed in the family's XML
docs); `LogicTreeEnumerationVerification` **5/5** isolated; `EpistemicMixtureVerification`
**5/5** isolated; **all eight byte gates re-run after the implementation — bit-identical**
(F1 `b2e6ea88…`, F2 `ac35a7fa…`, F3 `e46763ef…`, F4 `8a3a8b52…`, F5 `2ae3925b…`,
F6 `bd4a26e8…`, F7 `f985ca02…`, F8 `7833ad5f…`) — no committed fixture opts into enumeration,
and F6's bit-identity is the zero-overhead proof for the composite-seam edits.

**Next:** session 7 = **B9 (shared event-tree limbs)** per the saved prompt
(`~/.claude/plans/session-7-totalrisk-b9.md`), with B8 (configuration risk) the follow-on if
B9 closes clean; B1 stays gated on Haden's design session over the draft; B10 route (i) is
implementable whenever Haden rules the value-moving change; B4 needs a numerics authorization;
N4/T8 whenever Haden re-packs, tags, and pushes 2.2.0.

## 2026-09-02 — v2.0 program session 5: B3 epistemic branch mode + shared epistemic variables (the Q-Y closure)

**Goal:** execute session 5 of the capability program — **B3, the epistemic-mixture (logic-tree)
mode with named shared epistemic variables**, designed against source (two reconnaissance passes:
the composite cluster and the sampler walk/sharing precedents) and ratified via four
AskUserQuestion rulings, all as recommended: **all four composite clusters** (consequence
coupling-driven, no sharing seat in B3); **sharing by hashed string key + run-scope selector
overwrite** (the A6 `OverrideSampledPercentiles` pattern — zero walk-ordinal movement, captured
seed maps stay valid); **split mean-only gating** (Error for walked-cluster epistemic composites —
the 11× Jensen gap; Warning for consequence ones — the blend keeps the exact mean); and the
**four-oracle verification family**.

**Session-start baseline (the mandated protocol):** numerics HEAD `2b57771`, clean, 3 ahead of
origin, **no v2.2.x tag** (T8's ask-first trigger did not fire; N4 unchanged — the packed nupkg
stays stale), **43 commits past the session-4 close `fa91884`**; sibling Debug DLL rebuilt and
frozen. TotalRisk `5b4cd30` clean; build 0 warnings; fast suite 1,225/1,225; eight gates as
separate invocations — **seven bit-exact, F6 moved** (`53be64aa…` → `bd4a26e8…`). Attribution by
automated detached-worktree `git bisect run` over the 43 commits: the single stable step is
`99db59a` "Stabilize logarithmic distribution lower tails" (LnNormal/LogNormal/near-zero-skew
LogPearsonTypeIII CDFs rerouted from `0.5·(1+erf(z/√2))` onto `Normal.StandardCDF` = MVNPHI —
tail-stable Φ); every commit before it reproduces the old pin bitwise and every commit from it
through HEAD reproduces the new hash. The harness `--dump` payload diff shows the
`AnalysisContentHash` itself moved (options hash unchanged): F6's bootstrap-estimated log-family
content shifts at the ulp level, the stored model identity moves, and the content-derived seeds
legitimately re-roll — the second stored-content-identity movement precedent (after the 2026-08-27
product-moments/erfc batch). Magnitude: `CompositeHazardVerification` 11/11,
`CompositeEngineVerification` 5/5, `NfipAssuranceVerification` 7/7, fast suite 1,225/1,225 — no
tolerance or pinned constant violated. **Ruling: re-pin approved and executed** (`bcf4cee`,
attribution recorded in RESULTS.md).

**Landed (`b068d15` impl + tests + verification; docs following):**
- **The mode:** `CompositeCombinationType.EpistemicMixture = 2` and
  `CompositeFunctionType.EpistemicMixture = 3` (append-only, serialized by name — selecting is the
  deliberate hash event; both mode attributes were already unconditional, so existing forms are
  untouched). Walked clusters (`CompositeHazard`/`CompositeResponse`/`CompositeTransform`) declare
  ONE selector dimension in epistemic mode — the first non-zero-dimension containers among the
  composites; the base sampler allocates the N×1 matrix, pinned bit-equal to the consequence's
  legacy inline selector recipe. Index path selects by inverse-CDF of the cumulative weights at
  `Percentile(i, 0)` and samples the chosen child at the same index (child streams untouched);
  percentile path uses the consequence single-uniform composition convention (select, rescale into
  the branch); mean path stays the analytic blend with the loud Jensen caveat.
  `CompositeConsequence` keeps `SamplingDimensions` 0 — its epistemic selection rides the mode's
  coupling draw: `SampleExposureBranches(p)` returns the SELECTED child's own branches (nested
  aleatory mixtures still enumerate inside the chosen branch — the flatten also learned to route
  a nested epistemic child correctly), `CountExposureBranches` prices the worst selectable child
  (max, not sum). `CompositeTransform` legalizes Average + EpistemicMixture (the aleatory Mixture
  keeps its recorded enumeration blocker; epistemic transformed-hazard bounds take the extremum
  across positively weighted branches). `IsDeterministic` false at ≥ 2 positive weights on all
  four; degenerate single-branch Warning; weight rules apply in epistemic mode.
- **Shared epistemic variables:** `EpistemicVariable` string on the three walked composites —
  conditional-presence serialized AND hashed (unbound forms/hashes/seeds byte-identical, pinned;
  the name is the variable's identity; naming outside epistemic mode is an Error in Validate and
  the sampling gate). The ambient `EpistemicSharingScope` ([ThreadStatic], the tree-scope
  precedent) carries per-run columns keyed by name — derived
  `ToPositiveSeed(HashCombine(PRNGSeed, SHA256(UTF8(name)), 0))` through the run's scheme — and
  every bound composite (nested ones included, reached through the composites' own recursion)
  overwrites its selector column post-seeding via the new internal
  `RiskFunctionBase.OverrideSelectorColumn`: seed-inert to every other function, ZERO new walk
  ordinals (`SeedScribe` shapes unchanged, pinned). `RiskAnalysis` enters the scope at the run
  seeding loop and all three diagnostic re-seed sites; the standalone public
  `SystemComponent.SetupSamplers` shares within component scope from the component seed
  (documented divergence). Engine gates: mean-only Error/Warning split via the recursive
  `UsesEpistemicMode` discovery; differing binder weight vectors Warning; a fractile pin on a
  bound composite warns and wins for that function alone. Sensitivity/VoI collect ONE column per
  variable (`Epistemic Variable - <name>`, deduped across components through the threaded seen
  set); `SelectedBranchIndex(realizationIndex)` is the runtime-only branch-attribution query.
- **Tests** (+21 → fast suite **1,246/1,246**): the two enum pins extended;
  `EpistemicSharingScopeTests` (scope nesting, the declared column recipe reproduced
  independently, guards); `CompositeEpistemicMixtureTests` (per-cluster selection semantics with
  the exact 3/4/3 LHS allocation, selector bit-parity, percentile rescale pins, mean-blend
  equality, conditional-presence + hash events + both-mode round trips, validation matrices, the
  shared-scope binder alignment with the unbound anti-check, the transform legalization, the
  consequence exposure-branch selection incl. the nested-aleatory preservation and the max
  guardrail); `EpistemicSharingTests` (the mean-only gates, the deterministic-branch identity
  behind a full run — every realization bitwise one branch run's value, 30/40/30, the ensemble
  mean exactly the weighted branch risks — the run-scope alignment recovered from published
  results plus the declared column, walk-shape invariance via captured seed-map ordinal counts,
  pin-holds-branch, the one-column sensitivity dedup); three epistemic kitchen-sink registry
  entries. Two engine-test lessons recorded: `RunAsync` computes on `RiskAnalysisRunContext`
  clones, so post-run reads must re-derive streams or use published results; and the
  ensemble/mean integration-discipline split must be pinned in-test wherever per-realization
  values meet mean-only values (the documented rule, hit live by the enumeration oracle).
- **Verification:** new family **`EpistemicMixtureVerification` 5/5 isolated** (4 m 17 s) — the
  bitwise conditioned-interleaving partition (the unpinned ensemble ≡ the per-index interleaving
  of three fractile-pinned branch runs, exact 300/400/300 — simultaneously the A6 composition
  proof), the cross-model statistical mixture identity at 4·SE, the exact Jensen doctrine pin
  (0.023 blended vs 0.262 weighted through a composite-transform chain, 1e-12 against the closed
  forms, the elevenfold gap asserted), the shared-variable selection oracle against an
  independently re-implemented column derivation (SHA-256 + the little-endian fold + the
  positive-seed map + the LHS block, no library seed kernel) with the unbound anti-oracle, and
  the six-combination exact enumeration with 4·SE convergence — the C3 seed
  (docs/verification/epistemic-mixture.md).
- **Docs:** composite-functions.md rewritten (three questions, the per-reading mechanism table,
  the shared-variable/state-of-knowledge-correlation doctrine, the mean-only gates, §4 "blend or
  select" with both worked-example rows now runnable, the deferred table reduced to the aleatory
  transform Mixture and the consequence sharing seat); uncertainty-analysis.md §5 epistemic
  selector paragraph; arch doc — the §5.8.4 dimension rows amended, the stale pre-Q-Y §5.8.5
  sketch corrected (wrong fold-argument order and dimensions formula), the Q-Y entry closed with
  the landing record; REMAINING-WORK — the Q-Y row closed, the consequence sharing seat and the
  optional first-class variable object recorded as future ratifications.

**Verified:** build 0 warnings throughout; fast suite **1,246/1,246**; code/XML + traceability
validators green; `EpistemicMixtureVerification` **5/5** isolated; **all eight byte gates re-run
after the implementation — bit-identical** (F1 `b2e6ea88…`, F2 `ac35a7fa…`, F3 `e46763ef…`, F4
`8a3a8b52…`, F5 `2ae3925b…`, F6 `bd4a26e8…` at its new session pin, F7 `f985ca02…`, F8
`7833ad5f…`) — no committed fixture opts into the epistemic mode, and the F6 composite fixture's
bit-identity is the zero-overhead proof for the mode dispatch.

**Next:** session 6 = **C3 (exact logic-tree enumeration — B3's exact oracle)** per the saved
prompt; B1 stays gated on Haden's design session over the draft; B10 follows A2's measurements;
B4 independent whenever; N4/T8 whenever Haden re-packs, tags, and pushes 2.2.0 (the stale-nupkg
list now also trails the 43 post-`fa91884` parallel commits, `99db59a` included).

## 2026-08-28 — v2.0 program session 4: A10 extrapolation policy (both repos) + A6/A5/A1 + the full A8 stretch

**Goal:** execute session 4 of the capability program per Haden's redirect — **A10, the
extrapolation policy, both halves** (the upstream connection surface on
`TabularFunction`/`EmpiricalDistribution`, then the TotalRisk `ExtrapolationPolicy` whose design
REMAINING-WORK recorded), followed by the quick-win batch **A6 → A5 → A1** and the A8 stretch —
under the scoped numerics authorization (A10 surface only, extended by Haden's plan-review
directive to the 6b Vegas Sobol enablement). Four rulings via AskUserQuestion: **A10 upstream
rides INTO 2.2.0** (no tag existed; the release-notes draft updated twice, the packed nupkg
recorded stale — Haden re-packs); **A5 uses the binomial P_T = 1 − (1−p)^T** primary convention;
**the A8 certificate is runtime-only**; and **scrambled Sobol: ratify and attempt** (reached —
everything above closed clean). Two plan-review directives executed: the
KDE/Mixture/CompetingRisks empirical-under-the-hood no-regression coverage, and "make sure we
are using Sobol in the right places, beyond KU sampling" (the audit + the seeded VEGAS driver +
the KU scheme member).

**Session-start baseline (the mandated protocol):** numerics HEAD `8e4f08a` "Prepare v2.2.0
release", clean, 15 ahead, **no v2.2.x tag**; sibling Debug DLL rebuilt and frozen. TotalRisk
`3cdce5e` clean; build 0 warnings; fast suite 1,181/1,181; all eight gates as separate
invocations — **bit-exact to every pin**, no movement, no bisect.

**Landed (eight commits across the two repos, every item closed with its full gate round):**
- **A10 upstream** (numerics `91ccaf9`): `Extrapolation` (`ExtrapolationSides`, default None) on
  `TabularFunction` (both lookup directions) and `EmpiricalDistribution` — X-space sides with the
  survival-orientation mapping, the [0,1] CDF clamp kept, `InverseCDF` total and monotone over
  extended tails (the 1e-16 floors evaluate the extended lookup on enabled sides; the endpoint
  clamp side-aware), PDF deliberately table-span, `Clone` carries, conditional-presence
  serialization. Haden's directive: Mixture (`CreateEmpiricalCDF`)/CompetingRisks (CIFs +
  empirical CDF) pinned default-hold and attribute-free; KernelDensity **verified a sibling, not
  a composer** (own opd + hard gates) — pinned unchanged, recorded in REMAINING-WORK. 12+3
  upstream tests; 4-TFM suite green (2,394/2,394 per TFM at that point); the sanctioned DLL
  refresh re-baselined TotalRisk bit-identically.
- **A10 TotalRisk** (`c877d81`): `Core.Enums.ExtrapolationPolicy {None, Below, Above, Both,
  Error}` + `ExtrapolationSupport.Map` (Error → upstream None); the serialized
  conditional-presence property on the five types wired at all 13 verified evaluation-wrapper
  sites (the two hazard types gained an internal `SampleCore` so their 10,000-curve mean
  builders keep consuming raw products — the mean grid stays on declared bounds while values
  follow extended percentile curves; `Min/MaxHazard` keep reporting data bounds); policy-aware
  below-table warnings; **zero engine-math changes** — the domain widening flows through the
  upstream inverse at `BuildHazardBins`, endpoint rectangles shrinking against the growing
  interior. The Error mode: internal `RangeGuardedUnivariateFunction` /
  `RangeGuardedUnivariateDistribution` (a `UnivariateDistributionBase` subclass so the derived
  CDF family guards by virtual dispatch) created only when configured, forward-only (inverse
  lookups keep the hold — an Error hazard integrates over its span), throwing public
  `ExtrapolationRangeException` with function/axis/value/range after recording the diagnostic in
  the thread-static `EvaluationFaultScope`, which the four integrator failure guards append —
  surviving the `ReportFailure = false` absorption. New family **`ExtrapolationVerification`
  5/5 isolated**: the extended chain vs an independent two-million-node transform-space oracle,
  the clamp split, engine-scale byte identity incl. set-then-clear, the loud Error surfacing,
  and the hash/seed/result movement — with the measured finding that extension REDUCED risk on
  the anchor chain (the held fragility plateau sheds below-table mass): extension is a modeling
  statement, not a conservatism knob, now doctrine in the hazard chapter. Docs: policy sections
  in all four function chapters + risk-integration and hashing-and-seeding cross-refs.
- **A6 — epistemic conditioning** (`f48c889`): runtime-only `RiskAnalysis.FractilePins`
  (`FractilePin(functionId, percentile)`), applied at the sampler walk AFTER seeding by
  overwriting the target's own percentile matrix (`RiskFunctionBase.OverrideSampledPercentiles`)
  — captured seed maps bit-identical, every other function's draws bit-identical; the walk
  records applied ids and the run refuses unapplied conditioning pins; Validate matrix
  (duplicate/no-match Errors; deterministic/no-surface/consequence no-effect Warnings — the
  coupling-column consequence extension recorded in REMAINING-WORK; mean-only Warning with a
  byte-identical ignore); the three diagnostic re-seeds apply current pins; the ratified
  constant-column convention (a bit-constant input reports exactly 0 in the given-data measures
  and resolves 0 VoI variance/movement — the tied-inputs unit pin re-anchored to a partial tie).
  `SensitivityVerification` +1 → **5/5 isolated**: the exact Median-LHS recovery identity (the
  equal-weight average of 100 conditioned ensemble means equals the unconditional mean at 1e-12
  — algebraic, both sides sum the same conditional values), conditioned-run collapse and seed
  equality exact.
- **A5 — exposure-period and life-cycle conversions** (`523aff0`):
  `MeasureExposurePeriodRisk(periodYears, discountRate, riskType, scope…)` → unpersisted
  `ExposurePeriodRiskResults`/`ExposurePeriodInterval` — the ruled binomial P_T on the Fail
  stream evaluated as −expm1(T·log1p(−p)) (the Poisson rate form documented at ≈ p·(T−1)/2
  relative), cumulative T·m, the log-space annuity present value (r = 0 ⇒ bit-equal to the
  cumulative), and the equivalent annual as the annuity round trip (the stationary identity —
  the base-vs-future `PlanRow.EquivalentAnnual` composition stays CBA-layer, cited); weighted
  from birth over stored `RealizationWeights` incl. post-hoc; stationarity caveat loud. Ruled
  fast-only (exact closed-form identities vs independent `Math.Pow` forms + upstream
  weighted-reducer parity; the A9 precedent).
- **A1 — exact fault-tree importance** (`cefac8d`): `FaultTreeImportance.Compute` +
  Options/Result/Entry — Birnbaum/criticality/Fussell-Vesely/RAW/RRW via two allocation-free
  frozen-diagram conditional evaluations per unified variable at the mean or a co-monotonic
  percentile; coherent-only with the cut-set-style Xor refusal; reduced-out ⇒ Birnbaum exactly
  0 (pinned via a false house event); P = 0 ⇒ ratio NaN; P(0) = 0 ⇒ RRW +∞.
  `FaultTreeVerification` +1 → **11/11 isolated**: `EnumerateWithOverride` extends the
  exhaustive Boolean oracle with per-variable forced conditionals — every measure on a
  shared-transfer 2-of-3 tree at the means and a 0.75 percentile at 1e-13. Closes the
  REMAINING-WORK future-work note.
- **A8-certificate (6a, RULED runtime-only)** (`3f9dd40`): `RiskAnalysis.JointCertificate`
  (`JointConvergenceCertificate`) — the RECORDING-pass per-dof χ² and relative SE the
  integrator recomputes after `Initialize = 1` resets its accumulators and the engine
  previously discarded (the stored per-realization `ChiSquared` is the WARM-UP pass's — now
  documented in the results catalog), captured index-owned and aggregated at publication;
  mean-pass slots populate on mean-only runs (a full run assembles its published mean from
  percentiles and integrates no −1 pass — NaN by contract, pinned); null off the joint method;
  cleared per run.
- **A8-Sobol (6b, RULED attempt — reached)** (numerics `fa91884` + TotalRisk `1571777`): the
  audit classified every stochastic-driver site (the joint VEGAS driver the v1.0-parity target;
  the three KU matrix generators; tree/importance MC sweeps and the MVN lattice documented
  out). Upstream `Vegas.SobolSeed` (nullable; null keeps the unrandomized sequence bit-exact;
  a seed rebuilds the Matousek-scrambled driver) with default-inert/reproducibility/γ-Jacobian
  pins, riding 2.2.0 (release notes updated; the second sanctioned DLL refresh re-baselined
  TotalRisk bit-identically — absorbing **ten parallel commits Haden landed mid-session**, all
  engine-inert). TotalRisk: `SamplingScheme.ScrambledSobol = 3` (`SeedHelpers.ScrambledSobol`,
  one joint sequence per function from the same positive stream seed — scheme switches never
  re-roll seeds, pinned) at all three matrix generators incl. the composite mixture selector;
  the power-of-two advisory; and the conditional-presence
  `RiskAnalysisOptions.UseSobolJointSampling` driving the joint path at `SobolSeed = vegasSeed`
  (default false = the MT driver verbatim; enabling is a deliberate hashed movement).
  `SystemRiskVerification` +1 → **6/6 isolated** (Sobol-driver brute-force-oracle parity, exact
  budgets under manual γ = 4, two enabled runs byte-identical);
  `LhsVarianceReductionVerification` +1 → **2/2 isolated** (≥ 10× replicate-variance floor vs
  MC, pooled-mean and mean-only agreement, per-seed movement, bit reproducibility, rename
  inertness; 13 m wall).

**Verified (every item closed with the full round):** builds 0 warnings both repos throughout;
fast suite **1,225/1,225** (1,181 + 3 + 6 + 30 + 9 + 10 + 1 + 9 across the slices); code/XML +
traceability validators green at every close; families isolated — `ExtrapolationVerification`
5/5, `SensitivityVerification` 5/5, `FaultTreeVerification` 11/11, `SystemRiskVerification`
6/6, `LhsVarianceReductionVerification` 2/2; **all eight byte gates re-run as separate
invocations after every landed item AND after both DLL refreshes — bit-identical to the pinned
baseline every time** (F1 `b2e6ea88…` … F8 `7833ad5f…`; no re-pin this session). Numerics
4-TFM gate: green per TFM; the only failures ever observed were the twelve `BOM_*` live-API
tests during a remote outage (HTTP 500 "Error connecting to WDP"; they passed whenever the
service responded, and all four TFMs are 2,396/2,396 excluding that group) — recorded, not
touched, as is the pre-existing `Test_LogNormal` MSTEST0037 advisory the test runner surfaces.

**Numerics at close:** `bug-fixes-and-enhancements` at `1571777`-adjacent HEAD `fa91884`, 27
ahead of origin (my `91ccaf9` + `fa91884` interleaved with Haden's ten parallel commits, e.g.
SmoothedSeries, correlated-search window scaling, sequential ParallelMean — all proven
engine-inert by the re-baselines); still no v2.2.x tag; **the packed 2.2.0 nupkg is stale** —
both A10-upstream and Vegas-SobolSeed ride the release per the ruling, and the release-notes
draft carries both items with re-pack reminders.

**Next:** session 5 = **B3 (epistemic branch mode + shared epistemic variables)** per the saved
prompt; B1 waits on Haden's design session over the draft; B10 follows A2's measurements; C3
opens after B3; N4/T8 whenever Haden re-packs, tags, and pushes 2.2.0 (both upstream halves
aboard).

## 2026-08-28 — v2.0 program session 3: B2 value of information + the tier-A quick-win batch

**Goal:** execute session 3 of the capability program — **B2, value of information** (the
owner-ranked top priority, designed output-first in decision-maker language) as the major, then
the quick-win batch **A3-TotalRisk → A9 → A2 → A10-TotalRisk** (each whole or not at all, A10
ratification-gated), with the **B1 Bayesian-updating design draft** as the document-only stretch
— under the standing N4/T8 deferral. Four rulings via AskUserQuestion, all as recommended: the
six-gate re-pin approved; T8 stays deferred (the 2.2.0 nupkg exists in the local feed and HEAD
is the release-prep commit, but no tag exists and the branch is unpushed — flagged per the
ask-first clause); the B2 design package (a)–(g); and the quick-win batch as designed.

**Session-start baseline (the mandated protocol):** numerics HEAD `8e4f08a` "Prepare v2.2.0
release", clean tree, 15 ahead of origin; sibling Debug DLL rebuilt from committed state and
frozen. TotalRisk `e892cfc` clean; build 0 warnings; fast suite 1,135/1,135; eight gates as
separate invocations — **F5/F7 bit-exact, F1–F4/F6/F8 moved.** Detached-worktree bisect: at
`7d7d6aa` (every upstream commit except the version bump) **all six moved fixtures reproduce
their pins bit-exactly**, and the harness `--dump` payload diff between the two builds contains
exactly one changed field — `NumericsAssemblyVersion` `"2.1.4.0"` → `"2.2.0.0"` in the five
hashed result containers, the analysis content hash unmoved. The whole movement is the manifest
truthfully echoing the version stamp; every numeric byte proven equal; the fourteen substantive
parallel commits (percentile-cancellation guard, paired-data hardening, deterministic mean
reductions included) all bit-inert on the engine. No families needed — byte-level identity is
stronger evidence. **Re-pin approved and executed as its own commit** (`b338135`, the
F4/F6-precedent shape, attribution recorded in RESULTS.md).

**Landed:**
- **B2 — value of information** (`254e645` impl + tests + verification; `7fb57fe` docs):
  `RiskAnalysis.MeasureValueOfInformation` — weighted given-data conditioning of the stored
  per-realization measures on the recorded knowledge columns (the internal
  `ValueOfInformationEstimator`: stable-sorted equal-weight bins, exact between+within
  decomposition, documented 1/B² and B/n biases, the 20-bin convention); per-input
  `ValueOfInformationEntry` + per-function `ValueOfInformationGroup` rollups (Σ main effects —
  exact under additivity; `SensitivityInput` gained the `GroupLabel` study key); the
  perfect-information total (exact — a realization's measure is deterministic given its
  knowledge draws); per-criterion `TolerableRiskConfidenceMovement` blocks at the system scope
  (baseline bit-equal to the published A7 confidence, the 2p(1−p) ceiling); all unpersisted,
  nothing hashed, zero seed movement. Fast tests +29 → 1,164. New family
  **`ValueOfInformationVerification` 4/4 isolated**: the linear-Gaussian closed binned form
  (exact truncated-normal main effects under LHS-aligned bins, 5e-3 relative), the probit
  Simpson movement quadrature (8e-3 absolute), and the independent re-implementation over the
  public per-realization summaries, unweighted and weighted (1e-12 relative; baselines at 0).
  New chapter `technical-reference/value-of-information.md` written output-first — the ranked
  practitioner table leads.
- **A3 — given-data sensitivity measures** (`26aa213`): `SensitivityMeasure` gains
  `FirstOrderSobol`/`PawnMedian`/`BorgonovoDelta` (values 3–5, runtime-only), routed through
  the upstream `GlobalSensitivity` estimators over the same columns under the shared
  `GivenDataBins` convention (≥ bins valid pairs, null below; non-finite coercion per the v1.0
  convention). Fast tests +2 → 1,166 incl. the bit-parity pin against direct upstream calls.
  `SensitivityVerification` +1 → **4/4 isolated**: the linear-map given-data Sobol index against
  the exact binned truncated-normal form at 2e-3 relative (LHS strata align the bins exactly).
- **A9 — retained realizations + post-hoc re-banding** (`d9a0d33`): runtime-only
  `RetainRealizations`/`RetainedRealizations` and `ReassemblePercentileBands(weights?)` — the
  weight-aware assembly re-run over retained state; **the post-hoc weighted re-band is
  byte-identical to a run carrying those weights as its input** (the A9+A4 synergy, pinned at
  JSON byte level); `RetainedIntegrationDetailIndex`/`RetainedIntegrationDetail` skip the
  selected realization's memory dump so the recorded risk-point ledger stays inspectable (on
  the component curves — `Curve.Clone()` excludes points by design); run-start snapshots,
  publish/clear lifecycle, Validate memory-cost and no-match Warnings. Fast tests +7 → 1,173;
  default-off byte-inertness pinned at test and gate scale. No new family (ruled: fast pins +
  gates are the acceptance).
- **A2 — the secondary-axis Richardson diagnostic** (`004fc08`):
  `EstimateSecondaryDiscretizationError(componentIndex)` — the mean pass integrated at the
  configured/halved/quartered bin counts through diagnostic snapshots
  (`BivariateHazard.SampleBivariateAt` over the shared grid derivation,
  `SystemComponent.SampleWithConditionalBins`, the `SampledComponent` conditional-override ctor
  slot — stored count, hash, and seeds untouched); `DiscretizationEstimate` (extrapolated
  limit, estimated relative error, observed convergence ratio = the regime check) for the AFP
  and the per-type Excess mean; `SecondaryDiscretizationDiagnostic` unpersisted; needs ≥ 12
  bins. Fast tests +8 → **1,181** incl. the configured level bit-equal to a mean-only run.
  `CopulaDependenceVerification` +1 → **7/7 isolated**: the reduced levels bit-equal to models
  configured at those counts, the smooth-regime estimate within a factor of two of the study's
  true error with the ratio at four, and the legacy tail-concentrated fixture flagged below
  four — exactly the fixture the convergence study flagged.
- **A10-TotalRisk — skipped per its ratified rule, design recorded** (REMAINING-WORK): the
  landed upstream `ExtrapolationSides` policy sits on `OrderedPairedData.GetYFromX/GetXFromY`
  only, while the model library's sampled compute paths evaluate through
  `Numerics.Functions.TabularFunction` (transform/response/consequence realizations) and
  `EmpiricalDistribution` (hazard realizations) — neither carries the policy, so the TotalRisk
  half needs a new upstream item before it can land without hand-rolling distribution
  numerics. The full prepared design (the `ExtrapolationPolicy` enum with the Error mode,
  conditional-presence serialization, scope, exclusions) is recorded for the next numerics
  batch.
- **B1 design draft (the stretch)** written to `~/.claude/plans/b1-bayesian-updating-design-draft.md`
  — the conjugate Beta-on-SRP shape (Neff + exposure period) beside A4 likelihood re-weighting,
  the observation-model menu, the likelihood-evaluation placement (tied to A9), the
  double-counting composition rule, the API sketch, the exact-posterior verification strategy,
  and eight open questions for Haden's B1 design session. A document only — no B1 code.

**Verified (every item closed with the full gate round):** `dotnet build` 0 warnings; fast
suite `dotnet test -c Release` **1,181/1,181** (1,135 + 29 + 2 + 7 + 8); code/XML-doc validator
and traceability validator green throughout; families isolated — `ValueOfInformationVerification`
4/4, `SensitivityVerification` 4/4, `CopulaDependenceVerification` 7/7; **all eight byte gates
re-run as separate invocations after every landed item — bit-identical to the session's
re-pinned baseline every time** (F1 `b2e6ea88…`, F2 `ac35a7fa…`, F3 `e46763ef…`, F4
`8a3a8b52…`, F5 `2ae3925b…`, F6 `53be64aa…`, F7 `f985ca02…`, F8 `7833ad5f…`).

**Next:** B3 (epistemic branch mode + shared epistemic variables — the Q-Y closure, the next
major) with A6/A5/A1 as the quick-win batch; B1 waits on Haden's design session over the draft;
A10 waits on the upstream extrapolation surface; N4/T8 whenever Haden tags and pushes 2.2.0.

## 2026-08-27 — v2.0 program session 2: A4, the weighted epistemic ensemble (+ the A7 stretch)

**Goal:** execute session 2 of the capability program — **A4, optional realization weights and weighted ensemble reductions**, the foundation primitive the B2 (value of information) → B1 (Bayesian updating) thread and B3/C1/C3 build on, with **A7 tolerable-risk confidence** taken as the stretch after A4 closed clean — under the standing N4/T8 deferral (sibling Debug `<HintPath>` kept; `C:\GIT\numerics` read-only, Haden working it in parallel). Six rulings via AskUserQuestion: the A4 design as recommended (the two-path weight API, one authoritative stored vector, raw storage with Reliability semantics, the reduction scope with effort aggregates/sensitivity/function-level bands deferred); the **F6 re-pin approved**; the manifest gets a nullable `RealizationWeightsHash` at schema 1; the load-integrity ruling in Haden's own shape — **an invalid stored weight vector never throws: the results are cleared, `IsEstimated` ends false, and the payload must be rerun and re-saved** (programmatic misuse on the setter/run paths still throws eagerly; the unsupported-schema throw stays); A7 attempted in-session; and the A7 criteria **serialized on `RiskAnalysisOptions`** — the deliberate hash-surface event, conditionally written so every criteria-free form and hash stays put.

**Session-start baseline (the mandated protocol):** numerics HEAD `dc5b17c`, clean tree — and origin now **in sync** (the branch has been pushed since session 1; the 2.2.0 release cut remains the N4 gate); sibling Debug DLL rebuilt from committed state and frozen. TotalRisk `328f793` clean; build 0 warnings; fast suite 1,112/1,112; all eight byte gates as separate invocations — F1–F5, F7, F8 bit-exact to their pins and F6 at the attributed `74af2e95…` exactly as expected. No unexpected movement; no bisect needed. The F6 re-pin then executed as its own commit (`a5c8663`, the F4-precedent shape).

**Landed (two commits: implementation + tests + verification; docs + bookkeeping):**
- `Results\EnsembleResults` — `RealizationWeights` (`double[]?`, the single authoritative raw vector, parallel to the slots; `[JsonIgnore(WhenWritingNull)]` and declared last, so unweighted payloads stay **byte-identical** — pinned against the pre-change build's captured SHA-256 `D901ACF7…` over the 9,955-byte fixture payload); `SetRealizationWeights` (eager validation, defensive copy, null clears) beside the lenient serialization property; the shared internal rule `DescribeInvalidWeights` (length = slot count; finite, non-negative, not all zero — the upstream guard convention) consumed by the setter, the summary reduction, the analysis validation, and the readers; runtime-only `LoadDiagnostics`; and the ruled reader behavior — `FromJson`/`FromCompressedBytes` clear realizations + summary + weights (manifest kept for identification) and record an `Error:` diagnostic ending in "rerun the analysis and save its results again".
- `Results\EnsembleSummary` — every reduction weight-aware behind one `weighted == null` gate whose null branch is the existing code verbatim: the measure catalog, contributions, and root diagnostics reduce by weighted mean (`Statistics.Mean(values, weights)`) and the symmetric weighted percentile (the **vector-k** `Statistics.Percentile(values, levels, weights)` — one weighted preparation per measure); pairwise NaN/weight filtering with the zero-surviving-weight → NaN rule; indicators at the weighted mean with SE = √(V/N_eff) (Reliability-weighted variance, Kish N_eff = (Σw)²/Σw²); the effort/error aggregates deliberately raw; new `EffectiveRealizationCount` (`double?`, absent when unweighted) as the weighted summary's self-description.
- `Analyses\RiskPercentileAssembler` — `AssembleLecPercentiles`/`AssemblePercentileCurve`/`AssembleCurveScalars`/`AssignScalarPercentiles`/`AssembleProfilePercentiles` gain an optional weight vector (null = verbatim): per-grid-ordinate and curve-scalar weighted percentiles + weighted means, zero-weight-total ordinates mirroring the all-skipped convention.
- `Analyses\RiskAnalysis` — public runtime-only `RealizationWeights` (defensive copy + read-only view; setter invalidates `IsEstimated`; never serialized/hashed/seed-affecting), `Validate()` Errors (mean-only + weights; the shared weight rule against `Options.Realizations`), the run-start snapshot with a race-safe re-check inside the staged run, threading through `PostProcessUncertainty` into the assembler, the ensemble stamp before `ComputeSummary`, and `AnalysisRunManifest.Create(..., realizationWeights)`; the results-injection ctor now restores `IsEstimated` only over a **non-empty** ensemble (the cleared-on-load shape reports unestimated).
- `Results\AnalysisRunManifest` — `RealizationWeightsHash` (trailing optional `[JsonConstructor]` parameter; `WhenWritingNull`; SHA-256 over count + little-endian double bits, the seed-map-hash style) recorded **only for run-input weights** — post-hoc annotation never touches the manifest, which stays truthful run provenance at schema 1.
- Fast tests (+15 → **1,127**): the validation matrices, the pre-weight byte-identity SHA pin, weighted JSON/compressed round trips, the corrupt-weights load ruling end to end (cleared container → unestimated analysis), unit-weight bit-identity at representable plotting positions, direct-upstream-call equality at the reducer's exact tails, the integer-weight replication knots, zero-weight/slot-alignment/zero-surviving-weight semantics, `EffectiveRealizationCount` append-only serialization, the property/INPC/invalidation semantics, the run validation matrix, and the three-run engine end-to-end (per-index bit invariance, stamp + fingerprint, published-vs-post-hoc summary bit equality, band scalars vs direct weighted calls, unit-weight bands within the documented rounding envelope).
- **New verification family `WeightedEnsembleVerification` (4 tests, run isolated)** with a fully independent oracle — its own pair sort, prefix sums, plotting positions p(i) = A/(A+B), bracket search, and moment formulas, never the library's weighted paths: the synthetic fixed-seed reduction oracle (1e-12 relative, a rounding bound, not statistics), the integer-weight replication identity (means 1e-13; percentiles within the replicated sample's largest adjacent gap, knot-exact per the upstream contract), the weighted engine run (per-realization **bit invariance** under weights; band scalars and summary vs the independent reduction; the manifest fingerprint with the analysis content hash unmoved), and the at-scale round trip. `docs/verification/weighted-ensemble.md` + README row.
- Docs: `technical-reference/uncertainty-analysis.md` §6.1 (what a weight means, the reduction forms, N_eff guidance, the two write paths, the invariance contract) + §9 anchor row; `results-catalog.md` (the weight surface, the null-suppression convention, the load-integrity rule, the verification row); REMAINING-WORK (F6 ruling closed; item 1 push-half done; the program-progress note); the Ported Types Matrix rows; AGENTS.md regenerated.
- **A7 — tolerable-risk confidence (the stretch, third commit):** `Analyses\TolerableRiskCriterion` — immutable (replace-to-edit, the declared-type idiom): `Measure`/`RiskType` by append-only enum NAME, `ConsequenceTypeIndex` (0 = primary), finite `Threshold`; the parameterless ctor is the guideline default (mean Excess risk of the primary type at 1e-3). `RiskAnalysisOptions.TolerableRiskCriteria` (ObservableCollection bridged to the property-change surface) serializes as a **conditional child** — written and hashed only when configured, so every criteria-free options form and canonical hash is byte-identical (pinned by test: add → hash moves; clear → the original form and hash restore exactly); criteria never touch seeds (per-realization bit-invariance pinned at unit and verification grade, alongside the deliberate `AnalysisContentHash` movement). The engine evaluates each criterion at the system scope through the existing `SelectScope`/`ExtractMeasure` selectors — P(measure > threshold) as the realization-weight strict-exceedance fraction, NaN-filtered pairwise, zero surviving weight → NaN — into `EnsembleSummary.TolerableRiskConfidence` (`Results\TolerableRiskConfidence` entries echoing the criterion; null-suppressed, so criteria-free payloads including the A4 byte pin are untouched); `RiskAnalysis.ComputeTolerableRiskConfidence()` re-evaluates post-hoc over the stored ensemble including post-run weights; Validate gains the indexed criterion Errors, the declared-type position bound, and the mean-only Warning (a mean-only run publishes no summary and no block). **No `RiskMeasureOptions` flag exists** — the feature gates on criteria presence, so the recorded `"All"`-serialization trap never engages; `RiskMeasure` member names are now documented append-only serialized contract. Fast tests +8 → **1,135** (criterion matrix/round-trip, entry POCO + append-only block serialization, the options conditional-presence hash identity, the exact-count engine end-to-end with post-hoc and post-hoc-weighted recomputation and both validation gates); `WeightedEnsembleVerification` +1 → **5** (the weighted exceedance fractions bit-equal to independent Σw·1[x > c]/Σw counts behind the full engine, with the content-hash-moves/realizations-don't contrast).

**Two findings recorded (documented in code and tests, nothing changed):** (1) unit weights reproduce the unweighted percentile bit-exactly only where the equal-weight plotting positions i/(n−1) are exactly representable (n = 5 yes, n = 100 no — a few ulps through the position-interpolation quotient), and the weighted scalar-band mean is the plain Σw·x/Σw where the unweighted path is Kahan-compensated — both asserted at a 1e-15 relative rounding envelope with the mechanisms named; (2) the weighted indicator SE √(V/N_eff) equals the unweighted √V/√N algebraically but not bitwise (composition rounding), documented in the XML remarks.

**Verified:** `dotnet build` 0 warnings; fast suite `dotnet test -c Release` **1,135/1,135** (A4 closed at 1,127 with its own full gate round before A7 began); code/XML-doc/namespace/dependency validator green; traceability validator green (142 methods / 49 scenarios); `WeightedEnsembleVerification` **5/5** run isolated; the close-out byte-gate round ran **all eight fixtures as separate invocations** — F1 `4c1472d2…`, F2 `876ce063…`, F3 `6469666e…`, F4 `846234f1…`, F5 `2ae3925b…`, F6 `74af2e95…` (the new pin), F7 `f985ca02…`, F8 `5f4d4c39…` — **bit-identical to the session baseline**, the additivity proof that the null-weight, criteria-free paths are the existing code verbatim.

**Next:** B2 (value of information — the owner-ranked top priority, design output-first in decision-maker language) now that A4 and A7 exist; the B1 Bayesian-updating design session after; A1/A2/A3-TotalRisk/A9/A10-TotalRisk as quick wins between majors; N4 (Haden cuts 2.2.0 — the push half is done) → T8 whenever ruled.

## 2026-08-27 — v2.0 program session 1: the Numerics 2.2.0 slate (tiers A/B upstream work)

**Goal:** execute session 1 of the approved capability program (ratified 2026-08-27; final release **v2.0.0**, all tier A–C work ships in it, program closes with the NuGet publish + public code) — complete every Numerics-side work item tiers A and B need so RMC.Numerics 2.2.0 can be released and T8 clears the Phase 8 gate. Seven decisions ratified at plan approval (D-1 revised by Haden from the ASR2D mirror to "the most efficient and robust implementation with all the output TotalRisk needs"; D-2–D-7 as recommended).

**Landed (numerics `bug-fixes-and-enhancements`, seven commits `894dbc3`/`069751f`/`78bce26`/`ffdb44d`/`a2b5523`/`7f03540`/`dc5b17c`):**
- **`AdaptiveGaussKronrod2D`** (B10's Numerics half, D-1): G10K21 tensor product per rectangle (441 evaluations, all-positive weights — mass-ledger compatible, the reason Genz–Malik's cheaper negative-weight rule was rejected) under QUADPACK-QAG-style global refinement — a max-heap of regions keyed by |K−G|, bisection along the axis with the larger mixed-product error indicator (read from the same 441 values at zero cost), convergence on Σ region errors, `MaximumIterationsReached` on depth exhaustion (never silent success), and an optional `(x, y, weight, f)` recorder over the final composite rule (Σw = domain area, Σw·f ≡ Result, bit-inert on the computed result). The shared Genz battery (all six families vs closed forms, polynomial exactness, separable-vs-two-1-D-passes, error-estimate honesty at k = 1, budget/depth status, guards, degenerate widths, determinism) runs BOTH 2-D integrators — the ASR2D rows pin existing behavior as-is.
- **Weighted statistics** (N16, D-2): the symmetric weighted percentile p(i) = A(i)/(A(i)+B(i)) — exactly Type 7 at equal weights, unit weights arithmetically identical to the unweighted method, knot-exact against integer-weight replication (the two acceptance properties are provably incompatible in full, documented) — plus weighted mean/variance/sd/skewness/kurtosis under `WeightType { Frequency, Reliability }` (frequency replication-exact; reliability scale-invariant via W²/Σw²). **Defect caught by the battery mid-session:** the kurtosis moment ratio was not scale-invariant under reliability weights; it now routes through weight-normalized central moments.
- **`ExtrapolationSides` on `OrderedPairedData`** (A10's Numerics half, D-3): the Hydrologics-mirror [Flags] enum; sided transform-aware boundary-segment extension through the existing `BaseInterpolate` kernel (log-linear on log axes; bounded (0,1) on normal-z axes; plateau slope-0 ≡ hold); default None regression-pinned bit-identical; the "out of range is an error" mode ruled TotalRisk-side; `Linear` untouched; `UncertainOrderedPairedData` needs no new member (`CurveSample` returns OPD — the ladder-monotonicity test proves the sampled path). **Release-quality catch (follow-up commit `dc5b17c`):** the first form appended an *optional* parameter, which changes the compiled signature — assemblies built against 2.1.x would throw `MissingMethodException` (surfaced by the bisect's stale-binding combination, exactly what a not-recompiled consumer like BestFit would hit). Reshaped to distinct required-4-arg overloads with the historical 3-arg signatures restored verbatim and forwarding.
- **Seeded scrambled Sobol** (A8's Numerics half, D-4): `SobolSequence(dimension, seed)` — Matoušek linear matrix scramble pre-applied once to the direction table (GF(2) linearity makes that exact) + per-dimension digital shift at emission; the unseeded stream arithmetically identical (the existing R randtoolbox pins are the gate); nullable `Seed` property; the seeded draw order documented as contract. Tests: bit-identical same-seed, SkipTo parity (pinning the historical next-emission indexing where SkipTo(0) ≡ SkipTo(1), as-is), exact one-per-bin dyadic-block stratification under scrambling, marginal uniformity, and a 20-seed QMC-vs-MC RMSE measurement (≈10×, asserted at 2×).
- **Single-factor union** (B4's Numerics half, D-5): `Probability.UnionSingleFactor` — the equicorrelated-Gaussian union as a 1-D AGK quadrature in u = Φ(z) space over [1e-16, 1−1e-16], O(n) per node, survival products in log space via `Tools.Log1p` and the new **`Tools.Expm1`** (net481 has neither framework intrinsic); ρ ∈ [0, 1] with the comonotone limit analytic and negative ρ loudly refused (no real single-factor loading; PSD bound documented); plus the zero-allocation `SingleFactorConditionalProbabilities` buffer helper for TotalRisk's future conditional kernels. **Defect caught by the exact bivariate oracle:** AGK acceptance is absolute-OR-relative and the default absolute tolerance governed small unions (three orders of relative accuracy lost at 6e-4 scale — the oracle side verified independently via a tetrachoric Φ₂ expansion before any change); fixed by pinning `AbsoluteTolerance` at the framework floor. Oracles: independence ≡ `IndependentUnion`; n = 2 exact vs the closed bivariate normal CDF down to 1e-6 marginals; n = 4 vs PCM/MVN inside their own accuracy; n = 50 vs a 200k-realization fixed-seed factor-construction simulation at 4 SE; comonotone/monotonicity/Fréchet/rare-event/guard batteries.
- **`GlobalSensitivity`** (A3, D-6): given-data first-order Sobol (Plischke), PAWN per-bin KS + median, Borgonovo δ (double-histogram total variation; rank classes make monotone-transform invariance bit-exact) — deterministic, equal-frequency rank binning; verified against the analytic Ishigami and Sobol-g decompositions, the exact 1−1/yBins saturation, independence at documented bias scales.
- **2.2.0 release-notes draft** written to `~/.claude/plans/numerics-2.2.0-release-notes-draft.md` — the short `PackageReleaseNotes` paragraph plus the full release body per the numerics release workflow, inventorying all **127 commits** since the v2.1.4 merge (the recorded D1/D2/D4–D7 rulings, the MCMC/NUTS changes and BestFit impact, the Frank/Gumbel/Joe corrections, the Dijkstra cluster, the pre-session 22-commit review batch, and this session's additions) with every behavioral change flagged.

**Audits closed (D-7):** Q-H — `NextIntegers` confirmed public API (three `System.Random` extension overloads in `Numerics.Utilities.ExtensionMethods`). N15 — closed as unneeded: `CumulativeIncidenceFunctions` already takes the optional `bins` knob and the strict-output wrinkle has the stable engine-side rebuild. The `Network._nodeCount` REMAINING-WORK row was **stale** — the ruling was executed 2026-08-03 (six commits, four ratified decisions, 46 tests); the current constructor computes the count from both edge endpoints; row closed. Vegas diagnostics — `StandardError` + per-dof `ChiSquared` sufficient for the future A8 certificate; a per-pass series is not retained and not needed; no change.

**F6 byte-gate finding (ruling pending — no re-pin without Haden's approval):** the close-out round found F6 moved `c183d28f…` → `74af2e95…` while the other seven pins held bit-exact. Detached-worktree bisect attribution: the pin reproduces exactly at the last pushed commit `6137387`; the movement enters in Haden's own pre-session 22-commit review batch in TWO steps — **`4aab2e9`** ("Accumulate the product moment power sums about a shifted origin") and **`4f0d4f0`** ("Hold relative accuracy in the far normal and complement error function tails") — both ratified accuracy corrections on exactly F6's bootstrap-MLE surface (the only fixture that fits distributions; the other seven fixtures never touched those paths). This session's commits are exonerated: the hash is identical immediately before and after them. Magnitude evidence: `CompositeEngineVerification` **5/5**, `CompositeHazardVerification` **11/11** (incl. the bit-reproducibility pin), `CompositeConsequenceVerification` **9/9** against the final DLL — the movement is inside every statistical tolerance. Recommended ruling: approve the documented F6 re-pin to `74af2e95…` (the F4-precedent shape).

**Verified:** numerics — build 0 warnings on all four TFMs; full `Test_Numerics` Release **2,360/2,360 on every TFM** (net10.0/net9.0/net8.0/net481; an earlier run had 2 failures from the BOM download service returning HTTP 500 — the service recovered and the final full run is clean). TotalRisk (against the refreshed sibling Debug DLL at `dc5b17c`) — `dotnet build` 0 warnings; fast suite `dotnet test -c Release` **1,112/1,112**; code/XML-doc/namespace/dependency validator green; the final byte-gate round ran **all eight fixtures as separate invocations** — F1 `4c1472d2…`, F2 `876ce063…`, F3 `6469666e…`, F4 `846234f1…`, F5 `2ae3925b…`, F7 `f985ca02…`, F8 `5f4d4c39…` bit-exact; F6 `74af2e95…` documented above. No TotalRisk source changed this session.

**Next:** Haden's F6 re-pin ruling; then N4 — Haden pushes `bug-fixes-and-enhancements` (29 unpushed commits) and cuts 2.2.0 (release notes drafted; `dotnet pack /p:Version=2.2.0` to the local feed); then T8 (the four HintPath → PackageReference swaps + all eight byte gates re-run). A follow-up question recorded in REMAINING-WORK: audit the engine's AGK `AbsoluteTolerance` exposure for small-integral runs (the UnionSingleFactor lesson — acceptance is absolute-OR-relative).

## 2026-08-17 — Full repo review: documentation quality remediation + the remaining-v1.1 scope map

**Goal:** execute the approved full-repo review plan on three fronts — scrub the source XML documentation of phase/process and change-log narration, bring the technical-reference tree to textbook standard grounded in the `docs/reports` technical notes, and produce the authoritative remaining-v1.1 scope map — in five gated commits.

**Landed:**

1. **Code XML remediation + validator hardening** (`01d3483`, 64 files): the ~100-site sweep — stale "future bivariate X" docs where X shipped (12, incl. the `LegacyEventTreeConverter` user-facing exception string), the retired `pre-cascade` history vocabulary → trivial-layout/plain per-mode phrasing (25), ratified/decision-N narration (4), the `CompositeConsequence` "superseded design" narration (4), change-log sentences, and the two **factually false** `RiskAnalysis.cs` claims that multi-stage response composition "remains a validation error" (doc-only corrections; `ValidateMessages()` untouched). The EventTrees/Trees cluster gained its 22 missing Authors blocks and the undocumented nested members; four latent CS0419 ambiguous-`SampleBivariate` crefs (visible only under `EnforceXmlDocumentation`) were disambiguated. `validate-code-xml-docs.ps1` now enforces the Authors block (library + Verification) and a phase/process-language deny-list over all three source projects **and** the technical-reference/verification doc trees — the doc-tree scan immediately caught five residual sites the review sweeps had missed.
2. **Docs-tree corrections** (`e489baf`, 22 files): `docs/verification.md` de-phased (the conversion table rewritten as family × oracle source, keeping landed dates; the stale 113-mapped/3-blocked stats corrected to 116/0), `docs/index.md` fixed (the bivariate design is implemented and verified; a `docs/reports/` row added), the six page-level phase tags cleared, the stale composite-hazard/response "Deferred" sections repointed at the existing `CompositeEngine` family, and `composite-engine.md` rebuilt to the family-page standard (fixtures, the 256×GL-20 oracle, per-test tolerance derivations). `references.md` grew 22 → 46 entries: the four 2026 Smith technical notes and the two RMC overviews entered as [23]–[28], the dam-safety methodology and probability/competing-risks groups as [29]–[46], the in-repo locations annotated on [7]/[8], and the previously orphaned [15]–[18]/[22] plus the title-only report citations wired into the pages.
3. **Three new textbook chapters** (`technical-reference/failure-mode-combination.md`, `uncertainty-analysis.md`, `risk-analysis-engine.md`): each closely follows its technical note ([24]; [26]; [23] + [25]) — structure, derivations, worked examples, comparison tables, pitfalls — with the v1.1 mechanics layered on (lazy exclusive enumeration and the budget clip, the dependency constants, the CIF machinery, the deterministic percentile design and measured Jensen gap, the AGK/VEGAS-γ/FFT-convolution resolutions of the notes' v1.0 limitations, reliability mode with the NFIP API, the options catalog, and risk profiles). Indexed in the tech-ref README and `docs/index.md`.
4. **Existing-page enrichment** (`ede5ace`, 11 files): the transform Eq-17–25 gap closed with the OLS/log-linearization/inverse derivations; the hazard page gained the three interpolation-transform forms, the order-statistic quantile-variance derivation, a full `NonparametricHazard` section, and the mixture/max-of-RVs duality; the consequence page's stale Mixture selector-dimension claim was corrected to the exposure-branch contract; the LEC page gained E[C] = ∫S(c)dc, the one-directional CML/TRL screening logic, and the report's three named HEC-FDA difference sources; `risk-integration.md` gained the trapezoid foundation (all four variants + tail atoms); `system-components.md` the component-identity/series/block-semantics framing; `composite-functions.md` the three mixture challenges and the competing-risks duality; `bivariate-hazards.md` the eight-family copula catalog; plus the event-tree diagnostics and Appendix-G sensitivity reconciliations.
5. **Scope bookkeeping** (this commit): `docs/REMAINING-WORK.md` created (the Phase 12 deliverable, seeded early) with the alpha sequence — N4 (user-gated 2.2.0 release) → T8 (package switch, closes Phase 8) → Phase 9 residue + 11B imports → 12 → 13 → 14 — the open-question routing, the deliberate deferrals, and the post-v1.1 block. Three rulings recorded from the plan-approval question round: the Authors block is library + Verification scope (tests exempt); **Phase 14 is inside v1.1 and ships after the `v1.1.0-alpha` tag**; the **`SystemModel` root is post-v1.1** (namespace reservation stays). ROADMAP amendments: `RFAHazard` ownership consolidated under 11B, the three-vs-four csproj count corrected, and the N4 task-ID collision (PROGRESS remediation task vs strategy N-item) disambiguated. CLAUDE.md codifies the Authors scope and the new validator checks; AGENTS.md regenerated.

**Verified:** `dotnet build` 0 warnings; fast suite `dotnet test -c Release` **1,112/1,112**; `validate-code-xml-docs.ps1` green including the new Authors and deny-list checks (the doc-enforcement builds are now 0-warning too); traceability green (142 methods / 49 scenarios); relative-link checks over every tech-ref chapter green. No verification families were run — no compute path, algorithm, seed, or tolerance changed anywhere in this session.

**Next:** N4 — Haden pushes numerics `5ea77d3` and cuts 2.2.0 (release notes per the recorded list) → T8 package switch with the eight byte gates → then Phase 9 residue + Phase 11B per [REMAINING-WORK.md](REMAINING-WORK.md).

## 2026-08-07 — Phase 11 close-out addendum: the Gumbel/Joe inverse fix and the axis-choice measurement

**Goal:** execute Haden's two directions after the S7 report — fix the Gumbel copula defect the verification family exposed, and answer whether the v1.0 arrangement (stage as the driving univariate hazard, PGA marginalized out through the bivariate response's automatic collapse weights) matches the legacy Monte Carlo oracle better than the bivariate-hazard arrangement.

**The numerics fix (repo `C:\GIT\numerics`, branch `bug-fixes-and-enhancements`):** `GumbelCopula.InverseConditionalCDF` and `JoeCopula.InverseConditionalCDF` — an audit confirmed these are the ONLY two conditional inverses that solve numerically (every other family is analytic) — now evaluate the objective at the bracket end and saturate at the boundary when the requested conditional level is unbracketed within rounding. Measured before the fix: Gumbel threw for 121/1,999 uniform u values at θ = 2 (first at u = 0.006); Joe was rare at moderate θ but threw for **1,648/1,999** at θ = 20. Both gained dense-sweep regression tests over a θ range. **Two findings recorded without changing anything:** (1) the round-trip assertion I first wrote was wrong, not the code — at the saturated top edge many v satisfy h(v|u) = 1 − 1e-16 to full precision, so the tests assert completion, range, and monotonicity in the conditional level instead; (2) a pre-existing accuracy limitation surfaced while diagnosing and was deliberately left alone under the algorithm-change rule — the Archimedean families inherit `ConditionalCDF` as the generator ratio φ′(u)/φ′(C(u,v)) rather than the closed form their inverse solves, and it degrades at v = 1 for large θ (Joe returns 0.5245 at θ = 20, u = 0.846 where the exact value is 1). That is why the boundary tests do not round-trip across the two formulas.

**The axis-choice measurement (new `BivariateRiskVerification.Test_V1Arrangement_StagePrimaryPgaCollapsed`, +1 → 5 tests):** the answer is yes, decisively. Running the legacy scenario the v1.0 way — the stage-duration curve as the univariate driving hazard, the transposed 6×4 surface in collapse mode with `EstimateWeights(PgaHazard)` marginalizing PGA out, life loss reading stage — lands **0.241% from the exact reference on BOTH the failure-probability and risk axes**, against **35.4%** and **145%** for the default-20-bin bivariate arrangement of the identical scenario, and it also edges the 1000-bin bivariate run (0.199%/1.43%) at roughly 1/50th the cost. Against the 1M oracle: 0.25σ and 1.22σ, versus 6.4σ and 25.8σ for the default-bin bivariate run. **Mechanism:** the engine integrates the primary axis exactly and discretizes the secondary, so stage-primary spends the exact quadrature on the normal-Z tail where the convergence study located all the error, while the four-point PGA collapse covers an axis whose frequency curve puts 99.96% of its mass in the clamped region below the surface's first level (derived weights {0.999593, 2.90e-4, 7.49e-5, 4.21e-5}). Note the EAD comparison against the oracle is oracle-limited, not engine-limited: only 298 of 1,000,000 realizations fail, so the oracle's own EAD standard error is ≈ 6.4% relative and the oracle sits 1.22σ from the exact value the engine reproduces to a quarter of a percent. New exact reference for this axis order: `ExactMarginalizedSurfaceWeighted` (the union grid extended with the life-loss knots and a linear-in-z consequence factor, closed via ∫z·e^{βz}φ(z)dz), asserted against the unweighted closed form at a unit consequence before use. The test pins the accuracy at 0.5% and pins the head-to-head at >50× so the guidance cannot silently rot.

**Documentation:** the arrangement rule — put the steep, heavy-tailed, consequence-bearing axis on the primary and collapse the mass-concentrated one — landed in the arch doc's v0.24 status block and §7.4 cost model, a new "Which variable belongs on the primary axis" section in `technical-reference/bivariate-hazards.md`, a mode-choice paragraph in `response-functions.md`, and the four-arrangement comparison table in `verification/bivariate-risk.md`. The copula-dependence page's defect section is rewritten from "blocking" to "found and fixed", and the CLAUDE.md gotcha now covers both families plus the untouched `ConditionalCDF` boundary limitation. ROADMAP Phase 11 is stamped complete (status table + phase heading + exit criteria met, with the defect and axis-choice results recorded there).

**Verified:** numerics — `Numerics.csproj` Release 0 warnings, full `Test_Numerics` Release **2,104/2,104 on every TFM** (net10.0/net9.0/net8.0/net481), committed as `5ea77d3`; the sibling Debug net10.0 DLL rebuilt from it. TotalRisk — `dotnet build` 0 warnings, fast suite `dotnet test -c Release` **1,112/1,112**, both validators green, and the three families re-run isolated against the fixed DLL: `CopulaDependenceVerification` **6/6** (the Gumbel engine orientation pin now passes), `BivariateRiskVerification` **5/5**, `EngineReproducibilityVerification` **7/7**. The standing close-out byte-gate round ran all eight fixtures as separate invocations with every pin bit-exact — the proof that the copula fix is inert for every existing model (no fixture uses Gumbel or Joe).

**Numerics push state (corrected this session):** `origin/bug-fixes-and-enhancements` now points at `6062044`, so the previously recorded 48–54-commit backlog has already been pushed; the branch is ahead by exactly one commit (`5ea77d3`), left unpushed by convention. N4/T8 (the 2.2.0 release + package switch) remain deferred at Haden's direction; the Gumbel/Joe saturation fix joins the 2.2.0 release notes beside the S1 Frank correction.

## 2026-08-07 — Phase 11 session S7: verification + docs close-out

**Goal:** execute the design's Verification and Documentation-updates sections — `BivariateRiskVerification` (the legacy `Test_Bivariate_Risk` oracle, the exact SRP closed form with the re-captured legacy 100M cross-check, the re-anchored DAMRAE chained-bilinear oracle, the decision-12 collapse-vs-joint consistency cross-anchor), `CopulaDependenceVerification` (independence exactness, Normal/Clayton analytic anchors, the N-bin convergence study feeding every discretization allowance, the Gumbel tail-orientation pin, posterior-injected marginal parity), the `EngineReproducibilityVerification` bivariate pins, traceability re-classification, and the full documentation slate — with the standing constraint that an oracle exposing an engine defect stops for a ruling (diagnosis only).

**Prerequisite verified:** numerics HEAD `6062044` (clean tree) with the sibling Debug DLL rebuilt after it — the S5/S6 state. NO model-library code changed this session (the working tree touches only `RMC.TotalRisk.Verification`, docs, and the traceability CSV).

**Landed (working tree; commit withheld pending the ruling below):**
- `Verification\Analyses\BivariateOracleFixtures.cs` — the shared legacy seismic fixtures (curves/surface/life-loss verbatim from `Test_BivariateRisk.vb`), the graph-wired joint/probe component builders, the exact union-grid closed form for E_Y[P(x, Y)] (per-segment lognormal partial expectations e^{α+β²/2}·[Φ(z₂−β)−Φ(z₁−β)] + the marginal's two saturation atoms), dense Simpson references, and the two PINNED discretization figures the family tolerances cite.
- `Verification\Analyses\CopulaDependenceVerification.cs` (6 tests) — independence trapezoid exactness at ANY bin count (1e-10, bins 3 and 20); the Normal h-function transcription pin + the analytic mean through E[UV] = 1/4 + arcsin(ρ/2)/(2π) (engine measured 3.8e-6 rel at 1000 bins; the endpoint-cusp order reduction documented); the three-fixture convergence study — smooth separable-log fixture {1.104e-3, 4.418e-5, 4.418e-7} with ratios **24.995/99.999** (textbook O(N⁻²)), Normal-cusp series {6.9e-4, 8.2e-5, 3.8e-6}, and the legacy seismic probe {**0.353, 0.0491, 1.98e-3**} vs the exact closed form — the uniform-t trapezoid is rate-limited by the marginal's normal-Z tail (P ~ e^{c·Φ⁻¹(t)} concentrates in the top bins), so the design's a-priori "~1e-6 at 1000 bins" expectation is superseded by measurement, pins 0.5/4e-3 with head-room; Clayton analytic h/inverse/round-trip pins + the engine vs the dense **∫∫C** moment re-derivation (the original Simpson-over-h⁻¹ oracle was itself cusp-corrupted — replaced by the smooth CDF identity E[UV] = ∫∫C with independence + refinement-doubling self-checks); the Gumbel finite-difference h pin (passes) + the ENGINE orientation runs (blocked — the defect below); posterior-injected marginal parity, 120 realizations each within 0.1% of a hand-rolled dense conditional integral.
- `Verification\Analyses\BivariateRiskVerification.cs` (4 tests) — the ported 1M oracle (seed 45678, the unconditional pga/stage/rnd draw order; surface lookups ride the same Numerics `BivariateEmpirical`/`Bilinear` primitives legacy used, so clamping parity is by construction): EAD 0.035252 (SE 2.24e-3) / AFP 2.98e-4 (298 failures) vs engine(1000) 0.038446/3.0221e-4 — 1.4σ/0.24σ; FN probes {1,25,50,100,150} lives binomial (200 excluded at 12 expected); the 20-bin overshoot pinned as measured ratios (EAD 2.418, AFP 1.351 — the consequence weighting compounds the tail concentration); the SRP closed form 0.029670393164335482 cross-checked against the dense reference at 5.3e-9 and against the ONE-OFF 100M re-capture 0.029662826999636242 (σ̂ 0.0794859 — 0.95·SE agreement; the legacy test printed only, so this constant IS the run of record); the DAMRAE re-anchoring per the approved plan (deformation + life-loss grids verbatim, the warning-time grid verbatim on the affine def-remap def = (u−0.11)·66 + 0.2, a new SRP grid on (time, pool), pool EP endpoints trimmed 1/0 → 0.9999/0.00001, MersenneTwister(45678), 3 draws) — oracle EAD 5.8385 (SE 0.0471)/AFP 0.018498 vs engine(1000) +1.60%/+1.22% (2.0σ/1.7σ; asserted at k·SE + an explicit 2.5% measured-residual allowance), 20-bin ratios 1.643/1.460; the collapse-vs-joint cross-anchor — distances to the exact dense reference: collapse **27.3% → 1.91%** (verbatim → twice-refined interpolant-preserving surface with re-derived Voronoi weights), joint **35.4% → 0.199%** (20 → 1000 bins), tight-vs-tight gap 1.71%, and the engine's collapse semantics pinned against an independent re-implementation (log-interpolated collapse knots + end clamps) at 5.1e-6.
- `Verification\Analyses\EngineReproducibilityVerification.cs` (+4 → 7) — bivariate thread counts {1, 4, unbounded} byte-identical; metadata/canvas/`SelfContained`/`ByReference` (live resolver, marginal-link rename + re-Id) byte-identical on the label-stripped surface; unrelated-component growth leaves every per-realization component summary bit-identical (curve ordinates excluded deliberately — they resample onto analysis-level grids whose extents widen with a second component); the §5.5.8 pin × bins BOTH ways — an all-primary-bound component is byte-identical under a pinned MarginalY content perturbation (manifest stripped: it embeds the component content hashes, which legitimately move), and the secondary-bound counterpart moves as a pure, self-reproducing parameter effect.
- Traceability: `Test_BivariateRisk.vb` rows 12/85 → Covered with class/method; `Test_DAMRAE.vb:14` → Covered with the full re-anchoring note and the event-tree misattribution corrected; validator green (142 methods, 49 scenarios).
- Docs: arch doc → **v0.24** status block; §6.1.2 REWRITTEN (one `BivariateHazard`; BestFit types to the imports phase), §6.3.1 rewritten (collapse + joint reality), §6.5 rewritten (the structural two-port binding surface), **§7.4 fully rewritten** against the real engine (dispatch placement, combine-then-marginalize with the −Cov_j argument, no-draws, ledger invariance, endpoint policy, the γ probe, the one-VEGAS-dimension rationale WITH the rejected second-dimension alternative, the ×(bins+1) cost model with the measured adequacy table), §3 layout + §5.5.3 hash rows + §5.8.4 dimension rows corrected to the implemented types, §8.3 marginal-link/`TryResolve` notes, §11 Q-P deferred to the imports phase / **Q-Q RESOLVED** (composite forward rule, D = 0, ordinal 2 reserved) / **Q-R RESOLVED** (ratified deterministic); NEW `technical-reference/bivariate-hazards.md` (conditional-trapezoid math, the (u,v) convention + tail orientation, seeding, the worked PGA×stage example, the measured cost/accuracy table, scope guards); `response-functions.md`/`transform-functions.md`/`consequence-functions.md` bivariate sections; NEW `verification/bivariate-risk.md` + `verification/copula-dependence.md` (fixture provenance, per-assert derivations, dated runs-of-record) + `engine-reproducibility.md` bivariate pins + README/index rows; CLAUDE.md — four matrix rows to P/T/V, the Later row trimmed to Phase 11B, six new Numerics gotchas (`Bilinear` location + thread discipline, the conditional `InverseCDF(u,v)` argument, `CopulaType` append-only, the zero-parameter estimation guard, the AMH "AHM" doc-comment typo, and the Gumbel Brent-bracket defect); AGENTS.md regenerated. ROADMAP deliberately NOT stamped complete (the ruling below gates the phase close).
- **DEFECT FOUND (upstream, diagnosis only — no algorithm touched):** `GumbelCopula.InverseConditionalCDF(u, t)` fails "Brent's method failed because the root is not bracketed" at t = 1 − 1e-16 for 121/1,999 uniform u values (first at u = 0.006). Mechanism confirmed analytically: h(1|u) ≡ 1, but the objective computes `exp(−pow(pow(−ln u, θ), 1/θ))`, which rounds to 1 − |ln u|·ulp < 1 − 1e-16 whenever |ln u| ≳ 1, leaving no sign change over [0, 1]. The engine's conditional bins always include that clamp node, so ANY Gumbel-copula `BivariateHazard` engine run is near-certain to crash (independence/Normal/Clayton/Frank/AMH/Student-t have analytic inverses and are unaffected; Joe shares the Brent shape but passed the same scan at θ = 2). The AGK integrand absorption (`ReportFailure = false`) surfaces it as "an integrand evaluation threw" with the original exception swallowed — first-chance capture was required to diagnose.

**Verified:** `dotnet build` 0 warnings; fast suite `dotnet test -c Release` **1,112/1,112** (no fast tests added — S7 is verification + docs); code/XML-doc/namespace/dependency validator green; traceability validator green; run isolated — `BivariateRiskVerification` **4/4**, `EngineReproducibilityVerification` **7/7**, `CopulaDependenceVerification` **5/6** at the time of this entry (the Gumbel engine runs blocked by the defect below; its library-level pins passed); the standing close-out byte-gate round ran **all eight fixtures as separate invocations** — every pin bit-exact (no library code moved; identity is the proof). The defect and the family's sixth test are closed in the addendum entry above.

**Defect found (upstream, diagnosed only in this session):** `GumbelCopula.InverseConditionalCDF(u, t)` threw "Brent's method failed because the root is not bracketed" at t = 1 − 1e-16 for 121/1,999 uniform u values (first at u = 0.006). Mechanism confirmed analytically: h(1|u) ≡ 1, but the objective computes `exp(−pow(pow(−ln u, θ), 1/θ))`, which rounds to 1 − |ln u|·ulp < 1 − 1e-16 whenever |ln u| ≳ 1, leaving no sign change over [0, 1]. The engine's conditional bins always include that clamp node, so any Gumbel-copula `BivariateHazard` run was near-certain to crash. The AGK integrand absorption (`ReportFailure = false`) surfaces it as "an integrand evaluation threw" with the original exception swallowed — first-chance capture was required to diagnose. Fixed in the addendum entry above.

## 2026-08-06 — Phase 11 session S6: the engine (conditional-bin integration inside the sampled component)

**Goal:** implement design §D (D.1–D.8, `BIVARIATE_RISK_DESIGN.md`) — the conditional-bin loop entirely inside `SampledComponent.ComputeRisk` under the overriding D.7 constraint (univariate models bit-identical and zero-overhead through ONE `_conditionalHazard == null` branch selecting today's body verbatim), honoring decisions 7 (consume the S2 `FillConditionalBins` kernel, never reimplement), 8 (no draws anywhere in the bin loop), 10 (the competing-risks scope guard), and the D.3 requirement (combine per (X, Y_j) THEN weight-sum — marginalize-then-combine errs by exactly −Cov_j(P_A, P_B) and is forbidden). Six in-envelope choices approved at plan review: the mean `SampleBivariate()` overload on the S2 interface (the mean pass and the γ probe sample at index −1, so the mean snapshot is forced; the `SampleFunction()`/`SampleFunction(int)` house pattern); the competing-guard predicate read as SRP-primary-pure (Primary binding AND no joint bivariate response AND no bivariate stage transform — a Primary-bound joint-response mode still has a y-dependent SRP the 200-level CIF pre-processing cannot represent; bivariate/Secondary consequences stay legal, the CIFs govern probabilities only); joint-mode `SRP(x)` returning the stored v1.0 collapse value (the pinned kernel stays branch-free for it and the public member stays total; `SRPAt` is the joint path); a bivariate consequence's secondary input = the full secondary-chain output y′ (the one signal every bivariate element on a path consumes, per the S5 one-chain-per-path identity Error); the interim excess LIST baseline per bin using that bin's own non-failure mean (conditional coherence — life loss keyed to one (x, y_j) scenario); and bin-invariant modes (with bin-invariant partners) recording unscaled single-bin mode-scope entries while component streams always enumerate bins.

**Prerequisite verified:** numerics HEAD `6062044` (clean tree) with the sibling Debug DLL rebuilt after it — the same state S5 recorded. No serialized attribute moved anywhere this session (no `CanonicalizationRules` edit; `RiskAnalysisOptions` gains no field — bins live on the hazard and enter identity through its hash).

**Landed (one commit):**
- `Core\Interfaces\IBivariateHazardFunction.cs` + `RiskFunctions\Hazards\BivariateHazard.cs` — the mean `SampleBivariate()` overload: the mean Y frequency curve + cloned copula over the shared node vectors, same SetupSampler gate.
- `Results\SampledFailureMode.cs` — the ctor captures the projection stamps (`HazardBinding`/`ConsequenceHazardDimension`), the sampled secondary chain (at the S5-appended ordinals — the walk is untouched), and per-realization `Bilinear` wrappers in parallel nullable slots: bivariate stage/trailing transforms (evaluated at (signal, y′)), the joint response surface (raw interpolator; SRPAt clamps [0,1] after back-transform — the S4 contract; the stage's stored sample remains the v1.0 collapse so `SRP(x)` stays total), and per-type bivariate consequence surfaces for the own AND paired branch sets (single unit-weight placeholder branches; no coupling read — the surfaces are deterministic). Ctor-computed `IsSrpBinInvariant`/`IsBinInvariant`; new members `FoldSecondary`, public `SRPAt`/`ConsequenceInputAt` (+ shared-y′ cores), internal `EvaluateConsequenceBranchesAt`, and the staged trio `BeginBinnedEvaluation`/`ComputeRiskBinned`/`CommitBinnedPoint` (per-bin per-type conditional outputs into the reused scratch; staged w_j-scaled entry lists allocated ONLY on recording evaluations and adopted by the committed points — one point per stream per type; an invariant mode with an invariant partner computes once at full weight and stages unscaled univariate-shaped entries). Loud guards make the univariate members total (`SRP` under bivariate stage transforms, `ConsequenceInput` under y-dependent routing, `EvaluateConsequenceBranches` under surfaces) and `InverseSRP` extends to Secondary-bound and joint modes.
- `Results\SampledComponent.cs` — `_conditionalHazard` frozen in the ctor (mean/indexed overloads) with the `_binY`/`_binW` buffers and the per-type cross-bin accumulators (expected sums, marginalized background, extents, contribution rows — allocated only when bivariate); the engine-side competing guard (a loud ctor mirror of the Validate Error, thrown BEFORE the incidence pre-processing can consume the wrong axis); `ComputeRisk` gains the ONE trailing optional `hazardNonExceedance = double.NaN` parameter and the ONE dispatch branch — the univariate body below it is verbatim-untouched; `ComputeRiskBivariate` — u derived or passed then floor-clamped, `FillConditionalBins` once per X, bin-outer/type-inner (the probability structure is per-bin and type-independent), every mode evaluated at the same (x, y_j), the EXISTING combination kernels reused per bin (the joint pathway probabilities pre-scale by w_j after decomposition — the linear fold flows exactly through entry probabilities, expected sums, and the Shapley/consequence-proportional splits, while raw unit masses keep driving conditional branch weights; per-mode methods fold w_j·adjusted through the unchanged `AppendModeEntries`), per-bin claimed shares/complement mixtures/exact excess pairs at the shared y_j, marginalized PoF = Σ w_j·PoF_j, contribution accumulation across bins submitted once per (X, type), the accumulate-then-commit recording for component streams, claimed-state NonFail entries, and adjusted-mode curves (joint attribution via per-bin contribution deltas), and the new `AppendAdjustedEntries` helper.
- `Analyses\RiskAnalysis.cs` — three surgical call-site edits: the 1D `Evaluate` objective and the AFP probe pass their true u; the joint VEGAS integrand passes its clamped local probability (its probability argument stays the recorded weight — exactly why the parameter exists); sensitivity leaves NaN (documented at the call site). XML-doc notes only: the marginalized objective, the TailConditionalRisk α boundary on the primary axis, the probe's inherited (bins+1)× factor.
- `Systems\Components\SystemComponent.cs` — `EstimateRecordedFailureEntries` ×(bins+1) under a bivariate hazard (capped; the joint-entry limits price the (bin × pathway × branch) cross product); the decision-10 Validate Error (multi-unit competing over a bivariate hazard requires every failure state SRP-primary-pure; single-unit stays legal for any binding).
- `scripts/perf/PerfHarness` — the F8 fixture (single bivariate component, independence copula, 20 bins, uncertain tabular marginals, a Secondary-bound FM + a joint `BivariateResponse` FM, N = 1000) + the RESULTS.md pin row, fixture description, and dated measurement section.
- Tests (+15 → fast suite 1,112): `Results\SampledComponentBivariateTests.cs` — the hand-computable independence fold (engine PoF ≡ Σ w_j·p(y_j), mean consequence the adjusted-weighted average, the primary-bound background invariant across bins), NaN-vs-explicit-u bit agreement (bivariate AND univariate — the parameter is inert on univariate components), one-point-per-stream-per-evaluation recording with bin-enumerated entries and exact Σ = 1 Background/Total budgets, THE NO-EXTRA-DRAWS PIN (counting doubles on the secondary marginal and the response: one construction draw each, zero across repeated binned evaluations — decision 8 pinned), joint-mode `SRP` = collapse / `SRPAt` = surface with the engine fold matching Σ w_j·surface(x, y_j), the extended `InverseSRP` guards, and the engine-side competing throw; `Analyses\RiskAnalysisBivariateTests.cs` — a full uncertain bivariate run holding the exhaustive mass budgets (MassBalance/TotalProbability exactly 1 at system/component/mode scope — both ledger adoption gates exercised through the bin fold) and thread-count bit-identity (override 1 vs production parallel ⇒ identical ensemble + mean JSON); `SystemComponentBivariateProjectionTests` (+4) — the competing-guard matrix (multi-unit Secondary-bound ⇒ Error; all-primary and single-unit legal) and the estimate ×(bins+1) scaling; `BivariateHazardTests` (+1) — the mean overload (gate throw + mean-marginal identity + cloned copula).

**Verified:** `dotnet build` 0 warnings; fast suite Debug and Release (`dotnet test -c Release`) **1,112/1,112** (1,097 pre-session + 15); code/XML-doc/namespace/dependency validator green; `EngineReproducibilityVerification` run isolated **3/3**; the standing close-out byte-gate round ran **all seven fixtures as separate invocations** — F1 `4c1472d2…`, F2 `876ce063…`, F3 `6469666e…`, F4 `846234f1…`, F5 `2ae3925b…` (one plan), F6 `c183d28f…`, F7 `f985ca02…` (one plan) — **every pin bit-exact; F1 is the univariate zero-overhead proof**. New F8 baseline committed at `--reps 3` with its pin `5f4d4c39…` (mean-only median 2.652 s, full 76.001 s, 38.25 GB allocated — the recording-path staging lists dominate the allocation, the same allocate-what-the-point-adopts contract as the univariate multi-entry path; recorded as a future perf-session note per the no-preoptimization rule). No verification family authored — `BivariateRiskVerification`/`CopulaDependenceVerification` and the reproducibility additions are S7, as are the arch-doc/tech-ref amendments.

**Next:** S7 — verification + docs close-out (design §Verification: the legacy `Test_Bivariate_Risk` oracle, the `Test_Bivariate_SRP` closed form, the re-anchored DAMRAE chained-bilinear oracle, the collapse-vs-joint consistency cross-anchor, `CopulaDependenceVerification` incl. the N-bin convergence study, the `EngineReproducibilityVerification` additions; traceability rows; the arch-doc §6.1.2/§6.5/§7.4 rewrites, `docs/technical-reference/bivariate-hazards.md`, verification pages, CLAUDE.md gotchas). N4/T8 (the numerics 2.2.0 release + package switch) remain deferred; the numerics branch still carries 54 unpushed commits awaiting Haden's `--force-with-lease` push.

## 2026-08-05 — Phase 11 session S5: the two-port graph surface + FailureMode (the five reserved gates opened)

**Goal:** implement design §C (C.1–C.6, `BIVARIATE_RISK_DESIGN.md`) — open the five reserved v1.1 gates (`HazardElement.OutputCount`, `ResponseElement.InputCount` + its reserved secondary error, `FailureMode`'s Secondary gates, `ComponentGraph.GetAvailableHazardSources` port labels), add `SecondaryInput` to `TransformElement`/`ConsequenceElement` on the `ResponseElement` pattern verbatim, land the shared `TryResolveSecondaryChain` walk with the full mode-dependent wiring/validation matrices, stamp the projection (`HazardBinding` from the root exit port, the Y-chain into the new serialized `FailureMode.SecondaryHazardToResponse`, Secondary binding positions), and replace `FailureMode`'s reserved gates with the parent-typed bivariate matrix — under the binding decisions (6: bivariate transform 2-in/2-out, port 0 = z, port 1 = secondary passthrough; 10: loud scope guards; 12: collapse mode legal under a univariate root with the secondary null, joint mode under a bivariate root requiring the wired secondary + single stage + `SecondaryLevelCount` ≥ 2) and the overriding constraint that EXISTING MODELS MUST NOT MOVE. Not a v1.0 port — v1.0 had no two-port graph. Five in-envelope choices approved at plan review: the one-secondary-chain-per-path identity Error (per-element walks cannot see a downstream bivariate element wired to a prefix of the chain — two chains on one path would silently evaluate at the wrong y′; "one Y-chain per path" made literal); `AddFailureMode` throwing `NotSupportedException` on bivariate topology (a linear expansion cannot wire secondary connections, and dropping them would break the bit-identical projection contract); `IsDeterministic` including the Y-chain (its functions are seeded and sample — excluding them would corrupt the competing-incidence run-sharing gate); `CollectSensitivityInputs` walking the Y-chain after trailing transforms (the collection mirrors the seeding walk by contract); and the "secondary input" kind label on dangling/bounds messages (the reserved-message pin rewrote as its gate opened).

**Prerequisite verified:** numerics HEAD `6062044` (clean tree) with the sibling Debug DLL rebuilt after it (18:01:36 > the 17:53:29 commit). No `CanonicalizationRules` edits were needed, exactly as the S4 carry-fact predicted: the `SecondarySource*` names live in graph XML (never hashed) and the new `FailureMode` child is compute-relevant hashed content.

**Landed (one commit):**
- `Graph\HazardElement.cs` — `OutputCount => Function is IBivariateHazardFunction ? 2 : 1`; the setter raises `OutputCount`.
- `Graph\TransformElement.cs` + `Graph\ConsequenceElement.cs` — the `ResponseElement` secondary plumbing verbatim: `_secondaryInput`/`_pendingSecondaryInput`, INPC (the "SecondaryInput" name already routes topology invalidation), `SecondarySource` read/write (null ⇒ no attributes ⇒ existing bytes identical), resolve + clone remap, `GetInputConnections` yielding `Input` FIRST (the load-bearing off-path rule), widened `RestoreInputConnections` (2 and 3 slots); arity — transform in/out `=> bivariate ? 2 : 1` (port 0 = z, port 1 = passthrough), consequence `InputCount => any entry bivariate ? 2 : 1` with the collection-change raise; element-local validation (secondary-with-univariate Error, bivariate-without-secondary Error, consequence mixed-kind Error).
- `Graph\ResponseElement.cs` — `InputCount => bivariate ? 2 : 1` (the secondary port is OFFERED; the wired-vs-null rule lives in the graph); the reserved unconditional secondary error replaced by secondary-with-univariate-function; outputs unchanged. `RiskConnection`/`RiskElementFactory`: no changes, as designed.
- `Graph\ComponentGraph.cs` — the new public `TryResolveSecondaryChain(RiskConnection, List<TransformElement>, out string)`: hazard port 1 ⇒ success; univariate transform port 0 ⇒ front-insert and continue; bivariate transform port 1 ⇒ passthrough hop continuing from its own secondary input, guarded per segment (a passthrough reached after any univariate front-insert since the walk start or the last hop ⇒ Error — no univariate transforms between bivariate elements); closed loud error set (hazard port 0, bivariate z into a secondary slot, response/consequence sources, function-less/unconnected members, out-of-graph, cycle). The wiring matrix in a new `ValidateBivariateWiring` (single-root acyclic block): univariate root — bivariate transform/consequence Errors, bivariate response with a CONNECTED secondary Error (null ⇒ legal collapse mode, cascades included); bivariate root — every wired secondary must resolve, a bivariate response must be wired (joint mode) and single-response-path, bivariate elements' primary inputs must trace primary-kind (kind propagates through univariate transforms and responses), the one-chain-per-path identity Error, and the unused-port-1 Warning. `ValidateBinding` (now instance): primary-path targets as before + the bivariate-passthrough-port rejection; off-path targets legal when on the resolved secondary chain. `GetAvailableHazardSources` (gate 5): the port-1 option labeled from the bivariate secondary pair (ChainPosition 0 = raw Y) + one option per chain transform at k+1, `HazardSourceOption` unchanged; the anchor rule (`FindSecondaryAnchor`: the first bivariate element on the path) shared with projection and binding validation. `ConnectionSnapshot` widened to 3 slots; `DisconnectBranchConnections` clears the two new secondary slots; `GetReferencedFunctions` yields a bivariate hazard's marginals through the same seen set.
- `SystemComponent.cs` — `BuildFailureMode` stamps `HazardBinding` from the root exit port (existing models always port 0 ⇒ the Primary no-op), collects the Y-chain through `TryResolveSecondaryChain` (loops, non-throwing, unresolvable ⇒ empty), and generalizes binding stamping (chain target ⇒ Secondary position k+1; the existing hazard-port-1 branch already gave raw-Y position 0); the profile selector's port-0 first-hazard-edge Error; `CollectSensitivityInputs` — the `MarginalX`/`MarginalY` branch (fallback roles "Marginal X/Y Hazard"; the bivariate hazard itself contributes no column) + the Y-chain walked after trailing transforms; `AddFailureMode` refuses Secondary bindings/dimensions and non-empty chains with `NotSupportedException`. `AssignOccurrenceIndices` and the component-level `SetupSamplers` untouched (hazard stays ordinal 0).
- `FailureMode.cs` — the serialized `List<ITransformFunction> SecondaryHazardToResponse` (null-coercing setter + INPC, the `ResponseToConsequence` shape) written after `<ConsequenceFunctions>` ONLY WHEN NON-EMPTY (the `ParametricConsequence` conditional-presence precedent; missing ⇒ loads empty; unrecognized entries throw); the sampler walk seeds the chain AFTER `ResponseToConsequence` (appended ordinals); `IsDeterministic` includes it; the reserved gates replaced with the full parent-typed matrix (univariate/unparented ⇒ Secondary binding/dimension/chain Errors + bivariate transform/consequence-in-mode Errors, bivariate RESPONSE legal; bivariate parent ⇒ dimension-scoped position bounds — Secondary requires an explicit position ∈ [0, chain length] — joint single-stage + `SecondaryLevelCount` ≥ 2 Errors; Y-chain entries validate like trailing transforms with the bivariate-inside-chain Error; trailing transforms under a bivariate consequence Error); label continuity walks the bound marginal's axis pair (a Secondary-bound mode's incoming labels are the secondary pair; the secondary-chain, joint-response, bivariate-transform, and bivariate-consequence secondary labels compare against the chain's folded signal — Warnings only, silenced automatically under univariate parents by the empty-side skip); the consequence input routing contract documented in the XML docs (the engine implements it at S6). `ResponseStage`: no changes.
- Tests (+54 → fast suite 1,097): the four element files (arity gates + INPC raise sequences incl. the rewritten pre-S5 pins, the `SecondarySource` triple round-trip/pending-resolution/clone-remap on both new carriers, the no-secondary-no-attributes byte pin, the element-local validation pairs + mixed-list Error, hazard port-1 bounds across the gate); the new `ComponentGraphBivariateTests` (17) — every `TryResolveSecondaryChain` case (direct port-1, ordered multi-transform chain, passthrough hop resolving the identical chain, z-into-secondary, response source, hazard port 0, univariate-between-bivariate, four dangling variants, cycle) and the full wiring matrix one row per method (joint valid, cascade valid, Secondary-bound-path valid, raw-Y + chain bindings valid, collapse valid incl. a cascade stage, collapse-with-connected-secondary, transform/consequence-under-univariate, joint-missing-secondary, joint-multi-stage, primary-traces-secondary, chain-identity mismatch, passthrough binding, off-path binding, unused-port-1 Warning) + the picker's exact option tuples + marginals-once; the new `SystemComponentBivariateProjectionTests` (10) — root-exit stamping, ordered chain collection, the three binding stampings (raw-Y 0, chain k+1, primary unchanged), non-throwing empty projection, the profile guard both ways, sensitivity marginal + chain columns, component-level marginals, the `AddFailureMode` triple throw, and THE REGRESSION PINS — a univariate component's modes serialize with no chain child and explicit-empty assignment is byte- and hash-inert, and the seed-scribe capture equals independently constructed `HashCombine` expectations at the established ordinals (6 seeds univariate; the bivariate walk pins the chain at the appended position with shared re-encounters advancing without re-seeding); `FailureModeTests` (+8) — the gate matrix one rule per block, secondary-axis label warnings ×3, the conditional-presence serialization pin (absent ⇒ byte-identical, populated ⇒ round-trips + moves the hash, Clone carries, null-coercion raises), `IsDeterministic`; the event-tree graph file gains the secondary-slot cascade-detach coverage and the unswapped 3-slot restore pin.

**Verified:** `dotnet build` 0 warnings; fast suite Debug **1,097/1,097** and Release (`dotnet test -c Release`) **1,097/1,097** (1,043 pre-session + 54); code/XML-doc/namespace/dependency validator green; the standing close-out byte-gate round ran **all seven fixtures as separate invocations** — F1 `4c1472d2…`, F2 `876ce063…`, F3 `6469666e…`, F4 `846234f1…`, F5 `2ae3925b…` (one plan), F6 `c183d28f…`, F7 `f985ca02…` (one plan) — every pin bit-exact (no fixture uses two-port wiring; identity proves the reserved-gate, projection, sampler-walk, and FailureMode edits inert for existing models). No verification family touched — S5 is structural surface; the bivariate oracle families arrive with the engine at S6–S7 per the design.

**Next:** S6 — the engine (design §D: conditional trapezoid bins inside `SampledComponent.ComputeRisk`, per-(X, Y_j) combination then weighted summation, one recorded point per stream per X, one VEGAS dimension per component, the F1 byte gate as the univariate zero-overhead proof, the F8 fixture). N4/T8 (the numerics 2.2.0 release + package switch) remain deferred; the numerics branch still carries 54 unpushed commits awaiting Haden's `--force-with-lease` push.

## 2026-08-04 — Phase 11 session S4: BivariateResponse (the v1.0 collapse port + joint-surface augmentation)

**Goal:** implement design §B.4 plus the §B.5 response rows (`BIVARIATE_RISK_DESIGN.md`) — `WeightedHazardLevel`, the marker `IBivariateResponseFunction`, and `BivariateResponse` with the FULL v1.0 collapse mode preserved beside the new joint surface — under the ratified decisions that bind the type (5: deterministic surface; 12: the stored `SecondaryHazardFunction` link + collapse mode preserved with the three v1.1 refinements — Id/lenient-name metadata link, serialized weights authoritative with derivation only on explicit `EstimateWeights()`, staleness as Warnings; 10: scope guards; 6: no graph work — ports arrive at S5) and the two pinned S3 policies (native `Bilinear` extrapolation PLUS the response's [0,1] clamp after back-transform; one fresh interpolator per sampled realization, nothing cached). The two operating modes are selected ONLY by the parent — the response holds no mode state, and its 1-arg surfaces return the mean collapse rather than throwing (unlike the S3 tables). Seven in-envelope choices approved at plan review: the 2×2 default that validates (primary {0, 1}, secondary {0, 1} at weight 0.5 — v1.0's 1×1 default fails the ratified ≥2-primary rule); the lenient link-repair mechanism as a new `IRiskFunctionResolver.TryResolve` (id-first null-on-miss, name fallback, never throws — the ratified "miss ⇒ null, NEVER an Error" cannot ride the loud `Resolve` without catch-based control flow; the struct is the interface's only implementor); pending-link retention (unresolved attributes re-written verbatim so resolver-less round trips are bit-equal and provenance never silently drops); loud `EstimateWeights` edges (empty levels throw; a bivariate hazard argument/link throws — deriving from marginal X silently is what the Validate Error prevents; one CDF evaluation per midpoint, bit-identical to legacy's two); `WeightedHazardLevel` reads (missing attribute → 0, unparseable token → NaN); bounds throwing only on empty collections (v1.0 `First()`/`Last()` made loud); and `EstimateWeights` raising `SecondaryHazardLevels` (content changed) instead of legacy's quirky `SecondaryHazardFunction` raise.

**Prerequisite verified:** numerics HEAD `6062044` (clean tree) with the sibling Debug DLL rebuilt after it (18:01:36 > the 17:53:29 commit). Porting sources read end-to-end: the RMC.TotalRisk.IO C# port (primary) with the VB.NET engine cross-checked — identical algebra, and the three v1.0 load-order overwrite sites confirmed (`Open()`, the `UseManualWeights` setter, and the link setter all called `EstimateWeights()`).

**Landed (one commit):**
- `Core\Interfaces\IRiskFunctionResolver.cs` + `RiskFunctions\RiskFunctionResolver.cs` — the appended `TryResolve(Guid?, string?)`: the lenient counterpart for METADATA links (weight provenance), documented against the loud compute-link `Resolve`, which is unchanged.
- `Core\Interfaces\IBivariateResponseFunction.cs` — `: IResponseFunction`: the secondary label pair, `SecondaryLevelCount` (S5's ≥2-for-joint gate reads it), `SurfaceProbability(x, y)` (clamped), `CreateInterpolator()` (THE engine seam; the thread discipline and the consumer-side clamp pinned at the contract), parameterless secondary bounds. The contract documents the mode asymmetry versus the S3 tables: the collapse IS the univariate surface.
- `RiskFunctions\Responses\WeightedHazardLevel.cs` — the verbatim v1.0 row item in house style: `Level`/`Weight` INPC, the exact legacy `<WeightedHazardLevel Level Weight/>` shape, permissive ctor-from-XElement, `Clone()`.
- `RiskFunctions\Responses\BivariateResponse.cs` — the port: v1.0 parity surface (ObservableCollection levels; the Σ P·w collapse into `OrderedPairedData(xStrict, Ascending, yStrict:false, None)`; `EmpiricalDistribution { XTransform, ProbabilityTransform }`; percentile/index overloads → mean collapse, D = 0, no sampler state required; `IsMonotonic` on the collapse; first/last-ordinate `Min/MaxProbability`; the Add/Remove-only grid auto-resize with overlap preserved and zero fill — Reset deliberately inert, the v1.0 quirk pinned); the stored link with the S2 subscription-swap/bubbling shape and NO derivation side effects anywhere; `EstimateWeights()`/`EstimateWeights(IHazardFunction)` — the exact legacy Voronoi algorithm (single level ⇒ 1; F(μ) differences; last-bin residual absorption both directions, Σw = 1 exact) as one pure static core shared with Validate's staleness comparison so the two cannot drift, guards before any mutation, sets `UseManualWeights = false`; the augmentation (secondary labels; compute-relevant `SecondaryHazardTransform`; `SurfaceProbability`/`CreateInterpolator` = `Bilinear(primary[], levels[], grid)` sharing the grid array with axis vectors copied per call — the parity storage is collections, nothing cached); TWO usability predicates (collapse vs surface — a single secondary level collapses fine but refuses joint evaluation); the full Validate matrix (labels ×4; ≥2 primary/≥1 secondary/finite/strict-ascending/dims; p ∈ [0,1] cells; weight range + 1e-8 sum tolerance; per-axis + surface log/normal-Z guards; exact v1.0 zero-row and monotonicity Warnings; single-secondary-level Warning; bivariate-link Error; the two staleness Warnings); the v1.1 attribute envelope over the LEGACY child names (`PrimaryHazardLevels` pipe G17, `WeightedHazardLevel` children, `Probability_Row` rows) with shape-preserving ragged reads via `BivariateTableSupport` under the legacy names; default leaf-path hash.
- Registration (§B.5 response rows): `ResponseFunctionType.Bivariate` appended; the factory case THREADING the resolver (unlike the S3 leaves); three appended `CanonicalizationRules` strip rows (`UseManualWeights`, `SecondaryHazardFunctionId`, `SecondaryHazardFunctionName` — provenance, not compute; collision-checked); the decision-10 guards — `CompositeResponse` DUAL (the Validate Error beside the NonFail rejection + the `ThrowIfUnusable` rejection) and `ProbabilitySource.Validate` (Error without recursing into the bivariate child), per the ratified Validate-only tree scope.
- Tests (+73 → fast suite 1,043): `BivariateResponseTests` (65) per the design enumeration — the legacy-parity block (hand-computed 4×6 Isabella-style collapse incl. the 0.0305 literal; curve flags on a decreasing collapse; wrapper CDF spot-checks incl. the log-geometric midpoint in normal-Z space; overload identity; first/last bounds semantics on the decreasing fixture; unusable-table throws with Validate-reports-instead); the `EstimateWeights` block (single level; hand-computed Φ(−0.5) Voronoi values bit-exact vs an independent reimplementation; the residual-absorption battery with Σ = 1 delta-0 plus a constructed Uniform(0, 1) fixture that deterministically drives the raw sum BELOW one — the search proved the > 1 branch unreachable without an exact rounding tie (the telescoped sum ends in a complement addition that round-to-nearest cannot carry above one), so it is documented as v1.0 defense and covered by reimplementation parity; the no-arg/explicit overload contracts; guards-before-mutation; THE fixed-bug pin — link set, flag set, and resolver-backed load all leave stored weights bit-identical); the stored-link block (subscription swap; live id/name attributes; id-first repair surviving rename; lenient name fallback; miss ⇒ null with no Error; wrong-cluster miss; pending attributes re-written verbatim, replaced by live assignment, cleared by null; bivariate-link Error; both staleness Warnings); grid auto-resize ×3 (primary/secondary/Reset-inert); the joint-surface block (corner/interior known points; the secondary-log geometric midpoint; the [0,1] clamp pinned INSIDE `SurfaceProbability` via out-of-range cells the structural gate admits, with the raw interpolator returning them unclamped; the full extrapolation-policy pin — four corners exact, four edge 1-D interpolations; fresh-interpolator-per-call with grid-reference sharing; the mode asymmetry — one secondary level throws jointly while the collapse works); the 18-rule Validate matrix one test per rule (incl. zero-legal log, normal-Z endpoints-legal, and the saturated [0, 1] surface under both probability transforms); serialization (the pinned legacy child shape with exact G17 payload strings; bit-equal round trip; dropped-row and shortened-row ragged pins; bare-envelope permissive read; factory round trip with the resolver threaded); and the hash block (metadata + provenance + linked-content inertness — the decision-12 pin that editing the linked hazard moves ITS hash but not the response's; seven compute movers incl. the weight-edit-in-joint-mode consequence); `WeightedHazardLevelTests` (6); +1 dual-guard test in `CompositeResponseTests`, +1 in `ProbabilitySourceTests`; scaffold extensions (discriminator pins, not-serialized, three strip rows in `CanonicalizationRulesTests` append-only, kitchen-sink registry entry, factory all-types + cluster-filter cases).

**Verified:** `dotnet build` 0 warnings; fast suite Debug **1,043/1,043** and Release (`dotnet test -c Release`) **1,043/1,043** (970 pre-session + 73); code/XML-doc/namespace/dependency validator green; the standing close-out byte-gate round ran **all seven fixtures as separate invocations** — F1 `4c1472d2…`, F2 `876ce063…`, F3 `6469666e…`, F4 `846234f1…`, F5 `2ae3925b…` (one plan), F6 `c183d28f…`, F7 `f985ca02…` (one plan) — every pin bit-exact (no fixture uses the new type and no existing serialized form carries the three new stripped names; identity proves the enum/factory/strip-rule/resolver/guard edits inert). No verification family touched — the type's oracle anchor (the collapse-vs-joint consistency cross-anchor in `BivariateRiskVerification`) arrives with the engine at S6–S7 per the design, so the matrix row lands P/T.

**Next:** S5 — the two-port graph surface + `FailureMode` (design §C: the five reserved gates, `TryResolveSecondaryChain`, `HazardBinding` stamping, the full wiring/validation matrices incl. the ≥2-secondary-for-joint mode gate that reads `SecondaryLevelCount`). N4/T8 (the numerics 2.2.0 release + package switch) remain deferred; the numerics branch still carries 54 unpushed commits awaiting Haden's `--force-with-lease` push.

## 2026-08-04 — Phase 11 session S3: BivariateTransform + BivariateConsequence (deterministic two-way tables)

**Goal:** implement design §B.3 plus the §B.5 transform/consequence rows (`BIVARIATE_RISK_DESIGN.md`) — the two marker interfaces, the two deterministic two-way-table types on Numerics `Bilinear`, and full registration — under the ratified decisions that bind them (5: all 2-D surfaces deterministic; 6: z-output labeling only, ports arrive at S5; 10: scope guards) and the two pinned policies: the extrapolation policy (accept `Bilinear`'s native behavior — nearest-corner clamp when both axes are out of range, edge-row/column clamp with 1-D linear interpolation along the in-range axis when one is — documented loudly) and the interpolator thread discipline (one fresh configured `Bilinear` per sampled realization, never per call, never shared, nothing cached on the function). Three in-envelope choices approved at plan review: the 2×2 unit-square zero default grid (the `TabularTransform` default-table posture), the DUAL composite scope guard (Validate Error + `ThrowIfUnusable` rejection — the S2 `CompositeHazard` precedent, though the scope note named Validate only), and the shared internal `BivariateTableSupport` helper.

**Prerequisite verified:** numerics HEAD `6062044` (clean tree) with the sibling Debug DLL rebuilt after it (18:01:36 > the 17:53:29 commit). Facts pinned from source before implementation: `Bilinear` lives in namespace `Numerics.Data`, checks lengths only, holds REFERENCES to the caller's arrays (exactly the share-the-arrays/rebuild-the-wrapper discipline), and its corner returns are raw cell values; `Normal.StandardZ` throws outside [0, 1] and clamps the endpoints asymmetrically (0 → −38.47 finite, 1 → +∞ because `1 − double.Epsilon` rounds to 1.0), so the ratified [0, 1]-INCLUSIVE normal-Z validation rule is exactly the guard that prevents the runtime throw while preserving native legacy saturated-surface behavior (documented in both types' remarks; known-point tests pin interior probabilities); `Tools.Log10` floors zero (−16) and NaNs negatives, matching the v1.0 `< 0`-only log guard.

**Landed (one commit):**
- `Core\Interfaces\IBivariateTransformFunction.cs` / `IBivariateConsequenceFunction.cs` — `: ITransformFunction` / `: IConsequenceFunction`: the secondary label pair (primary axis = the inherited `SpecifiedHazard`/`HazardUnit`), allocating `Evaluate(x, y)` (one interpolator per call, documented as convenience), `Bilinear CreateInterpolator()` (THE engine seam; the thread discipline pinned at the contract), parameterless `Min/MaxSecondaryHazard()`. Both contracts document the univariate-surface rejection rationale (a fabricated bridge would silently evaluate at a meaningless y; graph arity + validation make the throw unreachable in valid models).
- `RiskFunctions\BivariateTableSupport.cs` (internal static — the `CompositeSupport` precedent; the slicing table's "shared deterministic 2-D table support") — pipe-joined-G17 axis write/read, `<ZValues><Row>` grid write/read, the structural validation core (axes ≥ 2/finite/strictly-ascending with per-axis precise messages, dims, cell finiteness), the log + normal-Z transform guards (axis endpoints suffice post-ascending; surfaces full-scan), the usability predicate, grid bounds, and the negative-cell probe. **Shape-preserving ragged policy:** a ragged or partially unparseable payload reconstructs to exactly the parsed shape — R rows × max parsed width, absent/unparseable cells NaN — so nothing parsed is ever dropped and `Validate()` reports dims/finiteness honestly (never silent truncation, never fabricated zeros).
- `RiskFunctions\Transforms\BivariateTransform.cs` — `: TransformFunctionBase, IBivariateTransformFunction`: `X1Values`/`X2Values`/`ZValues[X1.Length, X2.Length]` on the `Bilinear` convention (INPC reference-swap setters, null ignored — the `TabularTransform` table-setter pattern); `HazardTransform`/`SecondaryHazardTransform`/`TransformTransform` (default None); inherited `TransformedHazard`/`TransformedHazardUnit` label the z output; `FunctionType = TransformFunctionType.Bivariate`; deterministic D = 0 (inherited sampler records SampleSize only); `Min/MaxHazard` = X1 bounds, secondary = X2 bounds, `Min/MaxTransformedHazard` = grid min/max (meanOnly-independent); `ComputeUncertaintyResults` → null (the `NonFailResponse` posture); the 1-arg trio throws `NotSupportedException` naming `Evaluate(x, y)`; `CreateInterpolator` gates on structural usability then shares the arrays (the remark warns against defensive copies — they would put a whole-table allocation into every realization); every bounds accessor throws `InvalidOperationException` on an unusable table while `Validate` reports instead of throwing (pinned by test); append-only child order `X1Values`, `X2Values`, `ZValues` with `Row` children (one per X1 value, X2-ordered — NOT the legacy `Probability_Row`; these cells are not probabilities); default leaf-path `CanonicalHash()` (the S2 strip rows already cover the secondary labels — S3 appends no strip attributes).
- `RiskFunctions\Consequences\BivariateConsequence.cs` — the identical grid design with cluster names: output enum `ConsequenceTransform`; output labels via inherited `SpecifiedConsequence`/`ConsequenceUnit`; negative Z cells → advisory Warning (never invalidating); `CountExposureBranches()` = 1 (the inherited structural single branch) with BOTH `SampleExposureBranches` overloads throwing like the trio.
- Registration (§B.5 transform/consequence rows): `TransformFunctionType.Bivariate` + `ConsequenceFunctionType.Bivariate` appended (runtime-only); two `RiskFunctionFactory` leaf cases (no resolver threading); two kitchen-sink registry entries; the two now-actionable decision-10 scope guards as DUAL guards — `CompositeTransform`/`CompositeConsequence` Validate Errors emitted before the child-Validate recursion plus the matching `ThrowIfUnusable` rejections. **Crash path found and closed while wiring the transform guard:** `CompositeTransform.Validate`'s child-domain intersection calls every child's `Min/MaxHazard()`, which a structurally broken bivariate child now refuses with a throw — the domain block therefore skips when any bivariate child is present (the child is rejected outright, so its domain is meaningless), keeping Validate report-only.
- Tests (+51 → fast suite 970): `BivariateTransformTests` (29) and `BivariateConsequenceTests` (22) per the design enumeration — ctor defaults; INPC for every property incl. the ignored-null pin; the full validation matrix one test per rule (labels ×6, short/non-finite/tied axes with the finite-before-ascending single-report rule, dims, cells, log ×3, normal-Z ×3 with endpoints-legal, the consequence negative-cell warning); known-point bilinear values (corners exact, hand-computed interior 2.75, log-axis √10 midpoint, log-output 400^0.25 geometric form, normal-Z output vs the mirrored z-space formula); the pinned extrapolation policy (all four corner clamps exact + all four edge 1-D interpolations); `Evaluate` ≡ `CreateInterpolator().Interpolate` bit-equal; fresh-interpolator-per-call with array-reference sharing pinned (so defensive copies cannot creep into the per-realization path); 1-arg + exposure-branch throws with the structural count; deterministic/D=0; unusable-table throws with Validate-reports-instead; XML round-trips (Row shape pinned, bit-equal re-serialization) and the ragged-payload tests (dropped row → preserved 2×2 + dims Error; shortened row → preserved parsed cell + NaN + finiteness Error); factory + cluster-filter round-trips; the hash sets (metadata/labels/rename/re-Id inert; every axis value, cell, and each transform enum moves it; transposing a square asymmetric surface moves it); plus one dual-guard test in each composite test class and the scaffold extensions (discriminator member pins ×2, report-own-type ×2, not-serialized ×2, factory all-types + cluster-filter cases ×2, kitchen-sink entries ×2).

**Verified:** `dotnet build` 0 warnings; fast suite Debug **970/970** and Release (`dotnet test -c Release`) **970/970** (919 pre-session + 51); code/XML-doc/namespace/dependency validator green; the standing close-out byte-gate round ran **all seven fixtures as separate invocations** — F1 `4c1472d2…`, F2 `876ce063…`, F3 `6469666e…`, F4 `846234f1…`, F5 `2ae3925b…` (one plan), F6 `c183d28f…`, F7 `f985ca02…` (one plan) — every pin bit-exact (no fixture uses the new types; identity proves the enum/factory/composite-guard edits inert). No verification family was touched — the tables' oracle anchor (the re-anchored DAMRAE chained-bilinear oracle in `BivariateRiskVerification`) arrives with the engine at S6–S7 per the design, so both matrix rows land P/T.

**Next:** S4 — the `BivariateResponse` port (design §B.4: the preserved v1.0 collapse mode with the stored secondary-hazard link and explicit `EstimateWeights()`, plus the joint surface mode; `WeightedHazardLevel`; the `CompositeResponse` and `ProbabilitySource` scope guards land there). N4/T8 (the numerics 2.2.0 release + package switch) remain deferred; the numerics branch still carries 54 unpushed commits awaiting Haden's `--force-with-lease` push.

## 2026-08-03 — Phase 11 session S2: BivariateHazard (interface, type, conditional discretizer, registration)

**Goal:** implement design §B.1–§B.2 plus the §B.5 hazard rows (`BIVARIATE_RISK_DESIGN.md`) in TotalRisk under the twelve ratified decisions — `IBivariateHazardFunction`, `BivariateHazard` with Id-linked univariate marginals and a user-set fixed-parameter Numerics copula, the conditional-trapezoid discretizer snapshot, projected-identity hashing, composite-forward seeding, and full registration — with two rulings taken at plan approval: `ComputeUncertaintyResults` delegates to marginal X (width-guarded; null when the configuration is invalid), and the `CompositeHazard` bivariate-child scope guard lands NOW (the only decision-10 guard actionable before the S3/S4 types exist; the composite response/transform/consequence and `ProbabilitySource` guards land with the types they guard against).

**Prerequisite verified:** numerics HEAD `6062044` (clean tree) with the sibling Debug DLL rebuilt after it (18:01:36 > the 17:53:29 commit); the S1 surface — `ConditionalCDF`, scalar `InverseConditionalCDF`, `ToXElement()` → `<Copula Type Parameters/>`, `CopulaFactory`, zero-parameter `IndependenceCopula`, `ValidateParameter` returning the surfaceable diagnostic — confirmed in source before any edit.

**Landed (one commit):**
- `Core\Interfaces\IBivariateHazardFunction.cs` — `: IHazardFunction`; marginals typed `IHazardFunction?` (decision 3: univariateness is validation-enforced so a future phase can admit bivariate marginals without a serialization break); secondary label pair (primary axis = the inherited `SpecifiedHazard`/`HazardUnit`); `SecondaryIntegrationBins`/`ConditionalNodeCount`; the `SampleSecondaryFunction` trio + secondary bounds; the engine seam `SampleBivariate(int)`; the allocating diagnostic `SampleConditionalYGivenX` (docs: diagnostics/tests only). The (u, v) NON-exceedance convention (decision 9) is documented at the contract.
- `Results\SampledBivariateHazard.cs` — frozen per-realization snapshot: the realization's sampled Y marginal + a CLONED copula (thread isolation — sampled marginals and copula caches are not share-safe) + the hazard's shared read-only precomputed node/weight vectors; `FillConditionalBins(u, yNodes, weights)` evaluates `y_j = MarginalY.InverseCDF(copula.InverseConditionalCDF(u, t_j))` over the clamped nodes and copies weights via `Array.Copy` — null/short-buffer/u∉(0,1) guards, zero allocation, no LINQ.
- `RiskFunctions\Hazards\BivariateHazard.cs` — `: HazardFunctionBase, IBivariateHazardFunction` (deliberately never `UnivariateHazardBase`, making self-nesting structurally impossible): subscription-swapping marginal links with reentrancy-guarded bubbling (the `WeightedHazardFunction` shape); never-null `Copula` (null coerces to fresh Independence — the `ResponseStage.Response` precedent); `CopulaTheta` passthrough; the bins setter clears the precomputed vectors so snapshot geometry can never disagree with the configured count (re-setup required, pinned by test); `SamplingDimensions` 0 and `IsDeterministic` = the marginals' (θ fixed, decision 4); the `CompositeHazard`-recipe XElement ctor via `FunctionEntry.Read` per marginal container (stale id throws; an id-less lenient-name miss records for `Validate`; wrong cluster throws; missing `<Copula>` ⇒ Independence); `SetupSampler` (decision 8) — usability throw, per-marginal posterior-capacity guards, `HashCombine(seed, marginal.CanonicalHash(), 0/1)` with ordinal 2 reserved for a future θ posterior, then the vector precompute (nodes `[1e-16, j/N…, 1−1e-16]`; weights `1/(2N), 1/N…` with the LAST the exact residual — the ordered sum is exactly one, Sterbenz-exact at N ≥ 3); the 9-rule `Validate` matrix (four label Errors; unresolved-reference Errors; null/non-univariate/same-instance/invalid-marginal Errors — recursion into a marginal's `Validate` runs only for univariate marginals, which doubles as the cycle breaker; copula-invalid Error surfacing `ValidateParameter`'s own message; the pinned bins [3, 1000] Error; per-axis label-mismatch Warnings); delegation surfaces (X trio + bounds, Y trio + bounds, `ComputeUncertaintyResults` → marginal X per the ruling); two-mode `ToXElement` (append-only child order `Copula`, `MarginalX`, `MarginalY`); projected-identity `CanonicalHash` (decision 11: bins + copula type/parameters + `MarginalXHash`/`MarginalYHash` as named attributes — mode/id/name/label-inert by construction, X↔Y asymmetric, null marginal = empty token).
- Registration (§B.5 hazard rows): `HazardFunctionType.Bivariate` appended; the `RiskFunctionFactory` case threading the resolver; `CanonicalizationRules` + `SecondarySpecifiedHazard`/`SecondaryHazardUnit` (the two hazard-side strip rows; the three response-side rows land at S4); `CompositeHazard` — the Validate bivariate-child Error emitted WITHOUT recursing into the child (which also breaks the bivariate→composite→bivariate `Validate` cycle) and the matching `ThrowIfUnusable` rejection so sampling is loud too, not just validation.
- Tests (+51 → fast suite 919): `BivariateHazardTests` (42) — the full design enumeration: defaults/coercions/INPC incl. swapped-out-marginal silence, the 9-rule matrix one test per rule (bins 2/1001 invalid + 3/1000 valid; the non-univariate guard via a second `BivariateHazard`), exact child-seed asserts against independently constructed `HashCombine` expectations, equal-content-marginals independence (|r| < 0.1 at N = 2000, ≈4.5σ), posterior-capacity throw, delegation + throws-when-unusable, snapshot shape + bins-edit invalidation, both-mode round-trips (bit-equal self-contained re-serialization; live re-attachment; stale-id throw; lenient-name null + named `Validate` report; resolverless degradation; StudentT two-parameter copula; missing-copula default), factory round-trips both modes, the hash set (metadata/labels/marginal-rename/re-Id/mode inert; family/θ/ν/bins/marginal-content/X↔Y-swap move), and component-level mode invariance on the `SerializationModeTests` pattern; `SampledBivariateHazardTests` (8) — the kernel block: trapezoid pattern, ordered Σw exactly one (delta 0) at bins ∈ {3, 7, 20, 333, 1000}, endpoint clamps exact through a Uniform(0, 1) marginal, Independence ⇒ plain marginal quantiles (Uniform and Normal), Clayton conditional round trip at 1e-9 with node monotonicity and u-sensitivity, buffer guards incl. the oversized-tail-untouched contract, ctor nulls; +1 `CompositeHazardTests` scope-guard test; scaffold extensions (no new methods) in the kitchen-sink registry, `CanonicalizationRulesTests`, `FunctionTypeDiscriminatorTests`, and `RiskFunctionFactoryTests`.

**Verified:** `dotnet build` 0 warnings; fast suite Debug **919/919** and Release (`dotnet test -c Release`) **919/919** (868 pre-session + 51); code/XML-doc/namespace/dependency validator green; the standing close-out byte-gate round ran **all seven fixtures as separate invocations** — F1 `4c1472d2…`, F2 `876ce063…`, F3 `6469666e…`, F4 `846234f1…`, F5 `2ae3925b…` (one plan), F6 `c183d28f…`, F7 `f985ca02…` (one plan) — every pin bit-exact (no fixture uses the new type; identity proves the enum/factory/strip-rule/composite-guard edits inert). No verification family was touched — the type's oracle families (`BivariateRiskVerification`/`CopulaDependenceVerification`) arrive with the engine at S6–S7 per the design, so the matrix row lands P/T.

**Fixture facts recorded while testing:** `ParametricUnivariateHazard.Estimate()` requires the axis labels and `Realizations ≥ 100`; its `Min/MaxHazard` report probability-floor bounds (Uniform(0, 4) → 0.04/3.96), not support edges — the delegation tests therefore assert bit-equality against the marginal's own reports.

**Next:** S3 — `BivariateTransform` + `BivariateConsequence` (design §B.3) on Numerics `Bilinear` with the pinned extrapolation policy (S4, the `BivariateResponse` port, is parallel-eligible per the slicing table). N4/T8 (the numerics 2.2.0 release + package switch) remain deferred; the numerics branch still carries 54 unpushed commits awaiting Haden's `--force-with-lease` push.

## 2026-08-03 — Phase 11 session S1: Numerics copula work (conditional h-function API, Independence copula, serialization factory)

**Goal:** execute S1 of the ratified bivariate design (`BIVARIATE_RISK_DESIGN.md` §A.1–A.6) in `C:\GIT\numerics` on `bug-fixes-and-enhancements`, under two amendments Haden added at plan approval: no existing bivariate-copula API signature may move (BestFit's calls must keep resolving — audited, not assumed), and the conditional CDFs must be verified against named R or Python packages cited in the Numerics test XML docs.

**BestFit compatibility audit (pre-implementation, recorded here):** no BestFit type subclasses `BivariateCopula`/`ArchimedeanCopula`; every copula member BestFit touches (`CDF`/`PDF`/`LogPDF`/`Clone`/`Theta`/`Type`/`NumberOfCopulaParameters`/`SetCopulaParameters`/`ParameterConstraints`/`MarginalDistributionX/Y`/`ANDJointExceedanceProbability`/`GenerateRandomValues`/`BivariateCopulaEstimation.Estimate`) is signature-preserved verbatim; its copula picker is hardcoded `CopulaItem` constructions (zero `Enum.GetValues(CopulaType)` call sites) and its own `CreateCopula` switch never receives the appended member; `CopulaType` serializes by name everywhere. Implementation rule derived from the audit: **every existing `InverseCDF` override slot stays declared where it is today** (bodies became one-line recompositions over the new scalar), so compiled member references and source call sites resolve unchanged; all new API is purely additive.

**Landed (numerics `bug-fixes-and-enhancements`, five commits `e92c6a3`/`2530f91`/`242b304`/`7673ac2`/`6062044`):**
- `2530f91` **conditional API (§A.2):** `ConditionalCDF(u,v)` — the forward h-function h(v|u) = ∂C(u,v)/∂u on non-exceedance probabilities — virtual on `BivariateCopula` with a clamped central-finite-difference fallback (ε = 1e-6) for external subclasses, overridden analytically everywhere: the generic Archimedean ratio φ′(u)/φ′(C(u,v)) (exact for all five families, insensitive to each family's internal φ′ sign convention; per-family closed forms recorded in the override remarks), the Gaussian and Student-t elliptical conditionals, and the Independence identity. Scalar non-allocating `InverseConditionalCDF(u,t)` added with every family's array `InverseCDF` recomposed as `[u, scalar]` — arithmetic-identical body moves. **Found under the ratified §A.2 round-trip contract and fixed: `FrankCopula`'s θ > 0 conditional inversion inverted at the complement** — the legacy reflection returned h⁻¹(u, 1−t) (θ = 4: v = 0.21481 with h(v|0.3) = 0.30 where 0.70 was requested); distributionally invisible to simulation (t and 1−t are equally uniform, which is why it never surfaced) but a violation of the conditional-inverse contract. The reflection identity h_θ(v|u) = 1 − h_{−θ}(1−v|u) requires handing the negative-θ evaluation the complemented conditional; with the complement in place both branches match pyvinecopulib to 1e-8 and the round trip closes to 1e-10. No committed test in numerics or BestFit pinned Frank θ > 0 fixed-seed streams; consumers that fix seeds over Frank conditional simulation will draw different (distributionally identical) pairs at 2.2.0 — recorded in the commit body.
- `242b304` **`IndependenceCopula` + enum + estimation guard (§A.1/§A.3/§A.4):** direct `BivariateCopula` subclass (Archimedean would bake in a vestigial θ) — zero parameters, permanently valid (`ValidateParameter` always null; even `Theta = NaN` cannot invalidate — pinned), PDF = 1, CDF = u·v, identity conditionals, tail dependences 0, deep-cloned marginals; `CopulaType.Independence` appended LAST (implicit 7, serialize-by-name append-only contract pinned by test); `BivariateCopulaEstimation.Estimate` returns immediately for zero-parameter copulas, before the full-likelihood path touches marginals. `Test_ParameterValidity` gains a separate Independence block (the family loop asserts NaN ⇒ invalid, which the zero-parameter contract deliberately inverts).
- `7673ac2` **serialization + factory (§A.5):** `BivariateCopula.ToXElement()` — one virtual writer, `<Copula Type="…" Parameters="…"/>`, type by enum NAME, parameters pipe-joined G17 invariant in `SetCopulaParameters` order, Independence writes an empty attribute, marginals never serialized; static `CopulaFactory` — closed 8-way `CreateCopula(CopulaType)` with a throwing default, and `CreateCopula(XElement)` on the `UnivariateDistributionFactory` hardening ladder (null throw; name-parsed + defined type; missing/empty Parameters = zero values so the count check governs both directions; invariant finite parses via `Tools.IsFinite`; post-assignment `ParametersValid` rejection).
- `6062044` **docs:** `docs/distributions/copulas.md` — Independence section + selection-guide row, the Conditional Distributions section rewritten around the new API with the per-family h-function table (replacing the hand-rolled finite-difference example it supersedes), and the serialization element + factory.
- `e92c6a3` **pre-existing hygiene:** 15 MSTEST0037 analyzer warnings surfaced on the committed Dijkstra-session test files (net481 leg; the analyzer began firing since that session's gate — SDK roll-forward suspected) — mechanical assert-API substitutions only, Dynamic classes retained 63/63.

**Oracle program (the package-verification amendment):** R is not installed on this machine, so the pins are Python: **pyvinecopulib 0.7.6** (`Bicop.hfunc1`/`hinv1` — gaussian ±ρ, student, clayton ×2θ, gumbel ×2θ, frank ±θ, joe), with **every pyvinecopulib value cross-checked at generation against a 50-digit numerical ∂C/∂u of the family's independently transcribed CDF in mpmath 1.4.1** (max divergence ≤ 7e-16, proving the parameterization mapping); AMH (absent from pyvinecopulib) is pinned directly against the mpmath 50-digit derivative and root solve, cross-checked against the closed form v(1−θ(1−v))/D² to 1e-30. Package and version are cited in every new test method's XML docs; per-assert tolerances carry derivations (Brent-backed Gumbel/Joe inverses at 1e-6 from the 1e-8 solver tolerance; Frank inverse pins at 1e-8 absorbing pyvinecopulib's ~3e-11 internal inversion residual; StudentT finite-difference at 1e-3 documented against the K = 200 stratified `MultivariateStudentT.CDF` integration, with the pyvinecopulib pins as the precision anchor at 1e-8, and no v = 1 edge assert — the t quantile is unbounded there). In-test structural oracles for every family: central finite difference vs CDF, the exact edge h(1|u) = 1, round-trip closure, and bit-identical array/scalar recomposition.

**Verified:** numerics — build 0 warnings on all four TFMs; scoped copula + validity classes **109/109 on net10.0 AND net481**; full `Test_Numerics` in Release **2,102/2,102 on every TFM** (net10.0/net9.0/net8.0/net481). TotalRisk — sibling Debug net10.0 DLL rebuilt from the committed numerics tree; fast suite `dotnet test -c Release` **868/868**; the standing close-out byte-gate round ran **all seven fixtures as separate invocations** — F1 `4c1472d2…`, F2 `876ce063…`, F3 `6469666e…`, F4 `846234f1…`, F5 `2ae3925b…`, F6 `c183d28f…`, F7 `f985ca02…` (one compiled plan) — every pin reproduced bit-exactly (TotalRisk references none of the new copula surface; identity is the proof).

**Session fact:** Haden committed `e57af20` ("Preserve log distribution base when cloning") in the numerics repo in parallel at the start of the session; it sits beneath all five session commits, so every gate above — the full Release suite, the TotalRisk smoke, and the seven byte gates — ran with it included (the byte-gate identities additionally prove it engine-inert). All session staging used explicit paths only.

**Next:** S2 — `BivariateHazard` in TotalRisk per design §B. Haden's pre-release numerics batch and push (`--force-with-lease`; the branch now carries 54 unpushed commits) and the 2.2.0 release remain pending (N4/T8 deferred at his direction); the Frank θ > 0 conditional-inversion correction joins the 2.2.0 release notes.

## 2026-08-03 — Phase 11 design session: bivariate risk analysis (design ratified, roadmap re-sliced)

**Goal:** produce and ratify the comprehensive Phase 11 design — copula-based bivariate hazards with linked marginals, bivariate transform/consequence two-way tables, the v1.0 `BivariateResponse` port plus its bivariate-hazard augmentation, the two-port risk-graph surface, and the engine's conditional-bin integration — as the hand-off for the implementing sessions, with BestFit/RFA imports deferred.

**Landed (`docs/requirements/BIVARIATE_RISK_DESIGN.md`, ratified v1.0):** the full requirements (R1–R24), architecture (Numerics work item; `BivariateHazard`/`BivariateTransform`/`BivariateConsequence`/`BivariateResponse` type designs; graph + `FailureMode` changes; the engine integration designed against the real engine), the S1–S7 session slicing with exit gates, the exhaustive unit-test enumeration, the verification program, and the documentation deliverables. Twelve decisions ratified by Haden, two revised at review: **secondary integration bins are [3, 1000]** (default 20 — fewer than 3 is unacceptably coarse), and **`BivariateResponse` keeps the stored secondary-hazard link and the complete v1.0 collapse mode** (single-primary-hazard wiring with automatic or user-defined weights) beside the new joint mode — the preserved mode doubles as the collapse-vs-joint verification cross-anchor; the v1.1 refinement is confined to *when* weight derivation runs (explicit `EstimateWeights()`, never a load side effect — the legacy load-order overwrite bug does not port). Other headline decisions: one `BivariateHazard` type (supersedes the `ParametricBivariateHazard` naming) with marginals linked by `IRiskFunction.Id` to univariate hazards in the analysis; a new Numerics `IndependenceCopula` (zero-parameter) as the default plus the forward conditional h-function, scalar `InverseConditionalCDF`, `CopulaFactory`, and copula XML serialization; trapezoid discretization in conditional-probability space (N+1 nodes, last weight the exact residual); composite-forward-rule seeding (one hazard ordinal — Q-Q resolved, no existing model's seeds move); per-(X, Y_j) failure-mode combination then weighted summation (the old §7.4 sketch's marginalize-then-combine is wrong by exactly the covariance term); one recorded point per stream per X so the mass-ledger gates hold unchanged; one VEGAS dimension per bivariate component; univariate models guarded to bit-identity (F1 as proof); nested copulas, copula-θ uncertainty, and 2-D surface uncertainty deferred behind loud validation guards. Roadmap re-sliced: Phase 11 = bivariate only; new Phase 11B carries `BestFitBivariateHazard`/`BestFitTabularHazard`/`BestFitTransform`/`RFAHazard`/LifeSim and the "full v1.1 input-function surface P/T/V" exit criterion.

**Verified:** design-only session — no code changed; the traceability validator remains green (142 legacy methods / 49 report scenarios; `Test_BivariateRisk.vb` rows stay `BlockedMissingFeature` until S7 re-classifies them).

**Next:** S1 — the Numerics copula work in `C:\GIT\numerics` (after Haden's pending numerics push/2.2.0 release, or on the current branch at his direction); then S2–S7 per the design doc, one session each.

**Goal:** execute Haden's ruling on the `Network._nodeCount` defect, expanded to the full Dynamic cluster (`Dijkstra`, `Network`, `BinaryHeap<T>`, `Edge`) under four ratified decisions: every public signature frozen (LifeSim consumes the surface; no LifeSim source exists locally to verify against, so all of it is load-bearing), `GetPath` reimplemented to intent, crash-fixes-only validation (clear argument exceptions replace raw index/LINQ crashes; negative/NaN weights deliberately NOT rejected — the non-negative precondition is documented, never enforced), and exact multi-destination behavior plus a new single-pass `SolveNearest`.

**Landed (numerics `bug-fixes-and-enhancements`, six commits `e07f80a`/`4a880eb`/`108793f`/`e9d8ece`/`aa177f9`/`c361f28`):** the `BinaryHeap.Replace` fix (position-map lookup + bidirectional sift — weight increases previously corrupted heap order) with contract docs; the Dijkstra rework on two new internal types — a stable counting-sort CSR (`CompactAdjacency`, relaxation order and therefore every tie outcome bit-identical to the list traversal, with positional weight-overlay support) and an array-mapped `IndexedMinHeap` (8-byte slots, hole-shifting sifts proven layout-equivalent to the public heap) — with the heap sized at exactly the node count (the invariant: the heap holds exactly the state-2 nodes, no duplicates, independent of weight signs), replacing the fixed 10,000-slot heap that THREW on larger networks; the walkers `GetPath`/`TryGetPath` and the multi-source `SolveNearest`; the `Network` repair (node count computed, off-by-one adjacency fixed, defensive copies, compiled dual CSR, custom weights ACTUALLY APPLIED — they were silently ignored through the stale-adjacency pass-through — plus additive `NodeCount`, `SolveNearest`, and zero-allocation table-reuse overloads); the `GetPath` reimplementation (forward Dijkstra over the outgoing CSR with excluded-index skipping, nearest-destination early exit, table fast path; the caller's array is no longer sorted in place); and the shortest-path section in the numerics optimization docs. 46 new tests landed beside the 17 untouched pins, including an independent Bellman-Ford oracle on fixed-seed graphs, tie-order pins, heap fuzz against a reference model, and permissiveness pins for negative/NaN/infinite weights.

**Measured (development machine, recorded in the commit bodies):** a 316×316 grid (99,856 nodes — the old heap overflowed above ~10k in flight) solves in 0.042 s single-destination and 0.084 s for four merged destinations; `SolveNearest` covers the same four in 0.034 s; one thousand nearest-destination re-solves with varying weights on a 100×100 grid run in 1.6 s (~1.6 ms per solve) through the zero-allocation reuse overload.

**Verified:** per-commit gates (build 0 warnings + the scoped Dynamic-class run); close-out — the scoped set green on net10.0 AND net481 (63/63 each), all four TFMs build 0 warnings in Debug and Release, the FULL numerics suite green in Release (2,070/2,070 on net481, the tail of the all-TFM run), the TotalRisk consumer smoke 868/868 (Release), and the standing byte-gate round ran ALL SEVEN fixtures as separate invocations — F1 `4c1472d2…`, F2 `876ce063…`, F3 `6469666e…`, F4 `846234f1…`, F5 `2ae3925b…` (one plan), F6 `c183d28f…`, F7 `f985ca02…` — every pin bit-exact (TotalRisk references none of these types; identity confirms it).

**Next:** Haden's pre-release numerics batch and push (`--force-with-lease` — now 48 unpushed commits), the 2.2.0 release, T8, then Phase 11. Recorded future options, deliberately not built: parallel per-destination solves with ordered merge (bit-identical), and A* on the `GetPath` re-solve with table costs as the admissible heuristic.

## 2026-08-03 — Phase 10B residue close-out, Test_Numerics de-journaling, loose-end review, docs parity

**Goal:** land the uncommitted `Mixture.CreateEmpiricalCDF` restore under Haden's ratified decisions (AskUserQuestion, four answers: commit now + regression pin; reword the N## history now; fix all pre-existing library warts including the Console.WriteLine; rename the event-tree verification file only), relocate the 18 misplaced `Correctness`-namespace tests, sweep both repos for remaining loose ends before Phase 11, and close the one tech-ref parity gap (the Systems namespace page).

**Landed (numerics `bug-fixes-and-enhancements` — six commits; all SHAs post-rewrite):**
- **Mixture close-out:** the A-delegated one-line finite tail-quantile lower bound committed with a `Test_Mixture` regression pin (two-Normal mixture → `CreateEmpiricalCDF()` → finite median inside the exact (60, 100) bracket and finite 0.001/0.999 quantiles). The Phase 10B open hazard is closed — a clean numerics rebuild no longer regresses the three composite tests or F6.
- **History rewrite (commit-tree plumbing, no working tree):** the 14 `N##`-token subjects reworded (token stripped, text kept), the net481 `Tools.IsFinite` fixup folded into its adjacent parent (40 → 39 commits), and the three "the N1 factory" body references neutralized — final tree byte-identical to the pre-rewrite head (witnessed by tree-hash equality), so the rewrite is content-inert by construction. **Haden's eventual push must be `--force-with-lease`** (the branch now diverges from origin).
- **Test de-journaling:** all 18 tests moved 1:1 out of the root-level `Test_CorrectnessRepairs.cs` (bogus `namespace Correctness`) into their subject classes under the folder-mirroring namespaces, renamed to the `Test_<Subject>_<Behavior>` convention with behavioral summaries in each destination's doc variant; the `ConfidenceProbeFunction` helper moved nested into `Test_CompositeFunction`; the tail MVNUNI null-guard split into its own `Test_MultivariateNormal.Test_MVNUNI_RejectsNull`; `Test_Mixture_Phase4.cs`'s 8 well-named tests merged into `Test_Mixture` (zero collisions); both journal files deleted — the `Correctness` namespace no longer exists. A junk `using static System.Reflection.Metadata.BlobBuilder;` fell out of `Test_BootstrapAnalysis.cs` in passing.
- **Change-narration scrub (5 test files):** the two "Corrected expected value / Previous test value" pairs deleted (the docstring derivations remain the justification), the `RMC.BestFit` internal-phase leak deleted, "verifies the fix for…" and the off-by-one retitle reworded to contracts (including the pre/post-fix inline wording and assertion literal), and the orphaned `// ???` removed.
- **Library hygiene:** dead commented-out code removed from `Dijkstra.cs` and `MultivariateNormal.cs` (C++ porting residue; the `rr < 0` shell emptied by the removal went with it — behavior identical); Dijkstra's per-node `Console.WriteLine` removed (approved observable change — unreachable nodes remain detectable by their infinite cost); `BivariateEmpirical.PDF` now documents honestly that a joint density is not implemented and returns NaN (return value unchanged); `BinaryHeapTesting.cs`/`DijkstraTesting.cs` renamed to `Test_BinaryHeap.cs`/`Test_ShortestPath.cs` with matching class names.
- **Deliberately untouched — defect found and preserved:** `Network.cs`'s commented-out constructor block is not dead code; it is the missing `_nodeCount` computation. The field is only ever assigned zero yet sizes every `Solve` array and flows into every Dijkstra call from `Network`, so the class is latently broken for any real network. Deleting the comments would have destroyed the evidence; recorded here for Haden's ruling (fix `_nodeCount`, or triage the `Dynamic/` folder before release).

**Landed (TotalRisk `v1.1-development`, `eafb05d` + `c2cc4a8` + this entry):**
- `eafb05d` **docs parity:** the new [technical-reference/system-components.md](technical-reference/system-components.md) documents the Systems namespaces end to end — the element graph and authoring surface, the failure-mode projection with branch polarities, the end-state group layout, combination options and dependence, three-scope validation, the projected identity form with occurrence indexing and the sampler walk, and both serialization modes (every documented member verified against source; the two API blocks compile against the real surface). Registered in the tech-ref README and the docs map. `event-trees.md` gains the node-importance forward (symmetry with fault-trees.md), `scripts/perf/RESULTS.md` gains a consolidated current-pins table (F1–F7) above the dated history, and the stale `RiskElementFactory` remark now states the loader's actual behavior — `ComponentGraph` throws on an unknown element type rather than skipping it.
- `c2cc4a8` **naming:** `EventTreePhaseCloseVerification.cs` → `EventTreeMonteCarloVerification.cs` (file only; the partial class is unchanged so no test identity moves — the recorded 10A naming wart is closed), `Test_FixedSeedGeneratedTrees_SatisfyPhaseCloseProperties` → `…SatisfyCorpusInvariants`, and the event-tree verification page header/prose synced. `src/` now carries zero phase-close tokens.
- **Recorded, no action:** `IBranchingResponseFunction` has no mirrored test file — consistent with repo convention (no interface has one; coverage lives in the implementing responses).

**Session facts:** Haden worked in the numerics repo in parallel — his `CompetingRisks` correlation-validation edit appeared uncommitted mid-session (all staging thereafter used explicit paths only; the history rewrite waited until his own commit, "Honor competing-risk dependencies in simulation", left the tree clean and used compare-and-swap so a concurrent move would have failed loudly). He also committed the `VerificationModels/` staging data himself (`274815b`), so that folder is now tracked content.

**Verified:** numerics — all four TFMs build 0 warnings from a detached gate worktree at the final HEAD; **402/402** scoped tests on net10.0 **and** net481 (the Mixture family plus every touched destination class); the gate-built net10.0 DLL installed at the sibling HintPath so TotalRisk gates consumed committed state only. TotalRisk — build 0 warnings; Release fast suite **868/868**; Debug **868/868**; `EventTreeVerification` isolated **14/14** (the renamed partial file proven); code/XML-doc/namespace/dependency validator green; traceability green (**142 legacy methods / 49 report scenarios**); and the close-out byte-gate round ran **all seven fixtures** as single invocations — F1 `4c1472d2…`, F2 `876ce063…`, F3 `6469666e…`, F4 `846234f1…`, F5 `2ae3925b…` (one compiled plan), F6 `c183d28f…`, F7 `f985ca02…` — every pin reproduced bit-exactly. F4's identity additionally proves Haden's new competing-risks simulation commit is engine-inert, and F6's proves the committed Mixture restore against source.

**Next:** Haden's ruling on the `Network._nodeCount` defect; his pre-release numerics batch, push (`--force-with-lease`), and the 2.2.0 release (N4/T8 remain deferred at his direction); then Phase 11 (bivariate + BestFit import + LifeSim).

## 2026-07-31 — Phase 10B closure session (upstream ruling, slice 2b landing, corpus, importance, verification, F7, close-out)

**Goal:** resolve the upstream numerics gate under Haden's ruling, land the held slice 2b, and close Phase 10B end to end: the fixed-seed fault property corpus, the tree node-importance API, the greenfield verification family, the F7 performance fixture, and the full documentation/matrix/close-out gates.

**Upstream gate resolution (Haden's A-delegated ruling):** the fresh diagnosis pinned the `1462e35` collision to one line — the rewritten `Mixture.CreateEmpiricalCDF` seeds its stratification grid at `minX = Minimum`, which is **−∞ for any unbounded-below child** (Normal, LnNormal, …); a minimal scratch repro (`0.45·N(100,20) + 0.55·N(60,15)`, outside both repos' suites) produced a one-point empirical table and the exact production exception, and bounded children received a finite-but-different grid origin, which is what silently moved F6. Haden authorized the delegated minimal fix: the pre-`1462e35` finite tail-quantile lower bound (`minX = Distributions.Min(d => d.InverseCDF(1e-16))`) restored in `C:\GIT\numerics` `Mixture.cs` — one line, everything else in `1462e35` (the hurdle-aware zero-inflated bounds, simplex normalization) preserved — and the sibling Debug DLL rebuilt. Results: numerics `Mixture` family **20/20** (his new hurdle tests pin no grid behavior), TotalRisk fast suite **842/842**, and F6 back at `c183d28f…` bit-exactly. **The numerics fix is applied and rebuilt but deliberately uncommitted in the numerics repo** — Haden coordinates that repo's commits; folding it into his pre-release batch (or committing it directly) is his call, and until then a clean rebuild of numerics HEAD would regress the three composite tests again.

**Landed (seven commits + this entry):**
- `1801a46` **slice 2b** (32 files, +5,496): the complete fault model — `FaultTreeGateType`/`FaultTreeNodeBase`/gate/basic/house/transfer nodes, `FaultTree` + authoring partial, `FaultTreeOccurrencePlan` with per-context shared-variable unification, `FaultTreeResponse`, cut-set results — plus registrations (`ResponseFunctionType.FaultTree`, factory case, discriminator pin, kitchen-sink row), the cross-kind nesting seams (`TreeDependencyCollector`, event-compiler `CompileNested` + cross-kind source branch), and 13 fault test files. Committed only after the full gate round: build 0 warnings, Release suite green, validator green, byte gates F1–F5 reproduced and F6 restored, and `EventTreeVerification` isolated **14/14** (slice 2b edits the event compiler).
- `514f67f` **design-doc reconciliation** (§3.1 rule): §3.3 single-parent tree whose DAG arises from transfer resolution; §4 `LinkShared` event rejection phase-token-free; §7 the recorded choices — `BddNodeLimit` default 1,000,000 runtime-only, `Xor` exactly two inputs, Rauzy cut sets bounded by `GetMinimalCutSets(maxCutSets = 10000)`, and the read-once fast path deliberately not implemented (the single exact BDD path, with the closed-form gate identities as its read-once verification); §10 the `<Input ParentNodeId ChildNodeId Order/>` edge shape and the `SharedVariable` first-occurrence-ordinal identity bullet.
- `c3e52a9` **fault property corpus + the defect it found** (5 files, +1,314): `FaultTreePropertyTests` — seeds `0x10B20261–64`, 32 cases each (128), a feature-mask completeness assert over every gate type, degenerate k=1/k=n thresholds, both house states, shared+independent transfers internal AND external, the repeated-shared-target block, all three source kinds, and both modes; per case, exact 1e-13 parity (mean, percentile 0.75, and one median-LHS realization — sampleSize 1 pins every percentile at one half, keeping the oracle independent of the identity-sorted dimension layout) against an exhaustive Boolean-enumeration oracle that walks the authored tree, resolves transfers, forks contexts, and unifies shared variables itself; round-trip hash/value equality both modes; GUID/metadata/reorder invariance and compute-edit sensitivity; `MaterializeLink` value-identity (independent) versus deliberate value movement (shared repeat) with a construction-guaranteed margin; and the bounded ≤32-reduction minimizer. **The corpus immediately found a real slice-2b gap:** two shared external transfers embedding the same function self-contained deserialized into disjoint instances, silently splitting shared unification and moving both values and the canonical hash against the live and by-reference forms — a violation of the design's §5.4 "both modes produce the same canonical identity and sampled values." Fixed reader-side with the ambient `FaultTreeReadScope` (the established `[ThreadStatic]` try/finally pattern): repeated same-Id embeds materialize as one live instance, divergent-content duplicates are rejected loudly, and two pinned unit tests cover both behaviors. No hash recipe, probability rule, or existing result moved — the fix makes the previously uncovered round trip honor the ratified contract.
- `08d7482` **tree node importance** (11 files, +1,110): the ratified v1.0 `TreeNodeSensitivity` port under v1.1 conventions — public `TreeNodeImportanceOptions` (authored hazard level; Iterations 1000; Seed 12345), static `TreeNodeImportance.Compute` for BOTH tree kinds, `TreeNodeImportanceResult`/`TreeNodeImportanceEntry`, and internal read-only per-plan evaluation seams on both responses (percentile −1 = mean). Pass A varies every uncertain source jointly, recording per entry the effective conditional probability and — per the v1.0 source — the absolute path probability (event) or the unified variable's sampled probability (fault) plus the aggregate; pass B holds means and varies one entry at a time for the variance-ratio index. Per-pass independent MersenneTwister streams via `ToPositiveSeed(HashCombine(Seed, CanonicalHash(), passIndex))`; draws in canonical entry order; one draw per shared fault variable per iteration. Statistics via Numerics (`FiveNumberSummary`/`Pearson`/`Variance`) behind a bit-constant-series guard, because the numeric variance of a constant array carries a ~1e-33 sum-of-squares residue that would defeat the documented NaN/zero semantics. 17 unit tests: closed-form affine event/fault cases, shared unification to one entry, bit-exact determinism, live-state inertness (hash + configured sampler survive), deterministic and saturated degenerate trees, argument validation.
- `0cf6f80` **verification family** (2 files, +1,823): `FaultTreeVerification` (10) + `FaultTreeMonteCarloVerification` (7) — the nine ratified gate groups. Every expected value derived in-test (greenfield; zero traceability rows): exhaustive-enumeration parity at 1e-13 (feature-rich fixture + 64 indexed single-dimension realizations), closed-form series/parallel/shared-vs-independent/XOR/binomial-tail pins at 1e-15, the five-component bridge by fifteen-term hand inclusion–exclusion, hand-derived cut sets with the anti-approximation proof (exact 0.71 vs rare-event 0.92; the response equals enumeration, never the bound; non-coherent refusal), referenced tabular/event/fault sources vs manual inlines across both resolver-backed modes with content-seeded child-clone realization parity, graph-connected **bit-equal** risk equivalence against a tabular twin pinned to the diagram's symmetric Shannon ordinates and Normal-Z transform (proving integration lives outside the tree), the loud `BddNodeLimit` diagnostics with exact recovery, the million-trial BCL Boolean simulation (seed 10,203,671; exhaustive expectation 0.97161166844151381; observed error 5.67e-5 against the 2.69e-3 familywise-1e-6 Hoeffding bound), shared-once/clone-separate dimensions with full 256-strata coverage and per-realization closed-form parity, the affine SRS/LHS replicate comparison (seeds 10,203,731–742; exact expectation 0.65; variance ratio 32,365.37 against the conservative 100 gate), production scheduling bit identity (seed 10,203,791; sequential/four-worker/two default runs; JSON, curves, hashes, seed maps), canonical realization invariance (rename/GUID/reorder/mode), and the node-importance oracles (exact √0.5 Pearson and 0.5 index on the affine event tree; the from-scratch BCL-random reimplementation of all three statistics for the shared fault tree). Isolated run: **17/17**.
- `3d87912` **perf fixture F7** (2 files, +229): `BuildF7` — twenty pooled basic events repeated three to seven times through shared transfers across twenty crossing AND trains plus two wide k-of-n voting gates (one counting crossing pool pairs), forty locals, mixed scalar/33-knot-table sources; the frozen diagram landed at **23,344 decision nodes** over 60 unified variables (the earlier 8-train drafts collapsed to 252–5,247 nodes — the frontier is governed by concurrently live AND branches and undecided threshold counters, so the landing structure multiplies both). `MeasureFaultTree` mirrors `MeasureEventTree` (sampleSize 64, seed 24681357, 32 indexed reads, G17/invariant curve hash). Committed `--reps 3` row: setup 0.091965 s / 62.18 MB; 32 reads 0.604993 s / 574.74 MB; plans 1; pin `f985ca02…`. RESULTS.md gains the fixture bullet, the row, and the F1–F7 standing-rule extension.
- **Close-out commit (with this entry):** six additional `FaultTreeAuthoringTests` covering the previously untested authoring surfaces (expanded topological order with ownership rules, orphan pruning after a dropped serialized edge, fragment outside-target preservation/promotion including the owned cross-tree external promotion, before-sibling paste with the placement guards, unresolvable-target materialization diagnostics, and the graph-level mutation checkpoint) — raising unit-only library coverage from the razor-edge 89.97% to **90.39%**; the new [technical-reference/fault-trees.md](technical-reference/fault-trees.md) and [verification/fault-tree.md](verification/fault-tree.md) pages; the event-tree/response-functions/README/index/sensitivity cross-reference edits; the architecture doc v0.23 status, §3 layout, and §5.5.3 `SharedVariable` note; the design doc header/§13/§17 closure blocks; the CLAUDE.md matrix/namespace/Later/commands updates with AGENTS.md regenerated; and the ROADMAP 10B completion rows.

**Judgment calls recorded:** the corpus and enumeration oracles read production sampler state only through the public `SampledPercentile` surface and otherwise stay independent of the identity-token-sorted dimension layout (the median-LHS single-realization construction); `FaultTreeMonteCarloVerification` is named without a phase token (the event family's second-file name is a recorded wart, not a precedent); the graph-equivalence tabular twin computes its ordinates in the diagram's own symmetric Shannon form and adopts `Transform.NormalZ` so both responses expose bit-identical sampled functions (tree responses inherit the `EmpiricalDistribution` Normal-Z default; `TabularResponse` deliberately defaults to None).

**Verified (close-out gates):** build 0 warnings; Release fast suite **868/868** (785 pre-session + 57 slice-2b + 1 corpus + 2 read-scope + 17 importance + 6 authoring); code/XML-doc/namespace/dependency validator and verification traceability green (**142 legacy methods / 49 report scenarios**, zero new rows — greenfield family); `FaultTree` family isolated **17/17**; `EventTreeVerification` isolated **14/14** (re-run after the importance seam touched the event response); unit-only library coverage **90.39%** (19,161/21,197, coverlet in VSTest mode — the 10A-comparable collection); and the close-out byte-gate round ran **all seven fixtures** as single invocations after the session's final compute-reaching commit — F1 `4c1472d2…`, F2 `876ce063…`, F3 `6469666e…`, F4 `846234f1…`, F5 `2ae3925b…` (one plan), F6 `c183d28f…`, F7 `f985ca02…` (one plan) — every pin reproduced bit-exactly.

**Next:** Haden's ruling on committing the one-line `CreateEmpiricalCDF` restore in the numerics repo (until it lands there, a clean numerics rebuild regresses the three composite tests and F6); then his pre-release Numerics batch, the 2.2.0 release gate, T8 (package switch + Phase 8 exit), and Phase 11. Exact BDD-based Birnbaum/criticality importance measures remain documented future work.

## 2026-07-31 — Phase 10B session 1 (plan approval, machinery lift, ROBDD kernel, slice 2b build, upstream collision)

**Goal:** begin Phase 10B under the approved plan — promote the shared probability-source pair, lift the event-private compilation machinery into `Trees/` as bit-inert refactors, land the internal ROBDD kernel, and build the full fault model.

**Landed (three commits; slice 2b held uncommitted):**
- `7cfc99b` **slice 0a** — `ProbabilitySource` + `ProbabilitySourceKind` promoted from `Responses.EventTrees` to `Responses.Trees` (serialization-inert; element name unchanged); the two hard-coded diagnostic surfaces parameterized (`nodeLabel` + `treeKind`) with event callers passing the exact previous strings.
- `450a07d` **slice 0b** — the shared machinery lift: `ITreeComputeSource`, `TreePlanDependencies` (generalizing the event-typed dependency snapshot), the ambient `[ThreadStatic]` `TreeCompilationScope` cycle stack with `TreeFrameDescriber` (exact current cycle wording preserved for all-event paths; mixed-kind paths render per-frame kind labels), `TreeCanonicalPaths`, `TreeMutationSnapshot<TNode>`, and `IProjectedIdentityResponse` with `ResponseStage.ToIdentityXElement` projecting any implementer (closing the seam that would have hashed raw node GUIDs for a nested fault response).
- `af46586` **slice 2a** — the internal ROBDD kernel: `FaultTreeBdd` (memoized ITE, unique/computed tables, no complement edges, exact `KOfN` threshold recurrence, `FaultTreeBddBudgetException`), `FrozenFaultTreeBdd` (children-before-parents arrays, allocation-free forward evaluation, Rauzy minimal-cut-set extraction with a loud bound), and 10 kernel tests.

Each slice exited its full gate round at commit time: build 0 warnings, Release suite green, validator green, and byte gates F1–F6 bit-identical (the ratified proof that the lift and promotion were inert). Slice structure was reordered from the plan with rationale recorded: plan slices 1+3 merged into one atomic landing commit (transfer typing and owner wiring need `FaultTreeResponse`; a stub-free standalone tree commit is impossible), and the kernel committed before the model. The ratified in-plan decisions bound the session: the single-parent tree+transfers structural model, `SharedVariable` first-occurrence ordinals as the shared-logical identity encoding, two-input `Xor`, `BddNodeLimit` 1,000,000 runtime-only, Rauzy-not-MOCUS cut sets, **BDD-only evaluation** (the design's optional read-once gate-formula fast path deliberately not implemented — one exact code path), and exact Birnbaum/criticality measures out of scope.

**Slice 2b built complete in the working tree** (the full fault model, registrations, cross-kind nesting, and 13 fault test files): build 0 warnings, doc validator fully green, fast suite **839/842**. Behavior facts pinned while building it: transactional authoring rejects cycle creation at the mutating call (validate-by-compile, rollback proven); latent cycles arise only via `ProbabilitySource` property replacement and are detected at the next compile with the full path; a deterministic `TabularResponse` still reports one sampling dimension; XOR probability is `p₁(1−p₂)+(1−p₁)p₂`.

**Upstream collision (the session's blocker):** numerics commit `1462e35` ("Fix mixture simplex and positive-hurdle behavior") landed mid-session with a sibling Debug DLL rebuild. Proven from a clean worktree at pre-session `fbcb304`: the pre-session tree fails `CompositeHazardTests.Test_ComputeUncertaintyResults_Deterministic_CollapsesToMeanCurve`, `CompositeHazardTests.Test_NestedComposite_SamplesAndHashes`, and `CompositeResponseTests.Test_NestedComposite_SamplesAndHashes` (782/785) — all through `Mixture.CreateEmpiricalCDF` → child `EmpiricalDistribution` "At least two empirical ordinates are required" — and the committed F6 byte gate moved `c183d28f…` → `773b5543…` while F1–F5 reproduced exactly. Per the technical-authority rule the session stopped without accommodating: slice 2b was held uncommitted awaiting Haden's ruling (fix upstream versus ratify + re-pin F6), recorded with the full branch protocol for the continuation session.

**Verified:** the three committed slices' full gate rounds (each: build 0 warnings, Release suite green, validator green, F1–F6 bit-identical); the fbcb304 clean-worktree proof isolating the three failures and the F6 movement to the upstream commit.

**Next:** Haden's ruling on `1462e35`, then the slice 2b commit and the remaining Phase 10B slices (property corpus, node importance, verification family, F7, docs, close-out).

## 2026-07-30 — Review remediation, closeout session (R1 residue closure + T9 performance session)

**Goal:** close the two recorded TotalRisk residues under the pre-approved ruling — restore the v1.0 `ParametricResponse.SetDistributionParameters` parity gap and fix the stale `RiskProfileVerification` inline comment — then run the dedicated performance session: commit the composite fixture F6 with a fresh byte-gate pin and measure the two flagged upstream Numerics costs, recording everything and changing nothing upstream (the standing no-preoptimization directive).

**Landed (four commits + this entry):**
- `a65a0a5` **R1 parity restoration** (3 files, +121/−2): `ParametricResponse.SetDistributionParameters(IList<double>)` added as a verbatim mirror of the hazard twin — null or count-mismatched input returns silently, a per-value comparison invalidates the estimate and raises the `ParentDistribution` notification on the first difference, then the values pass through `ParentDistribution.SetParameters`; a method only (no serialized attribute, no hash surface — proven by the unchanged byte gates). Verified against the v1.0 C# port (`RMC.TotalRisk.IO` `ParametricResponse.cs:1211` — same silent-return/compare/invalidate/set shape; the messenger tail is UI-layer; the null guard is the v1.1 hazard's addition). Four new tests (change invalidates + notifies; equal values keep the estimate with zero notifications; null and wrong-arity no-op; the hazard/response twins agree on both the changed and unchanged paths). The response-functions.md example was verified against the restored member: the call is valid, the trailing shared-parametric-surface note is accurate as written, and the example's parameter comment was corrected — `LnNormal.SetParameters(IList)` takes the **real-space mean and standard deviation** (the direct method, `GetParameters` symmetric), not the log-space μ/σ the comment claimed.
- `b7e7fb7` **R1 comment alignment** (1 file, +3/−2, diff-shape witness 5/5 comment-shaped): the quartile-probe inline comment at `RiskProfileVerification.cs:433` now matches the corrected class-level remark — the engine's partial sum of the recorded per-abscissa masses represents the integral only to within the local inter-node spacing, a half-interval discretization allowance (the old text still attributed the allowance to the retired midpoint-trapezoid partition).
- `62e3d96` **T9 fixture F6** (2 files, +135/−11 harness, +24/−1 RESULTS.md): a four-child mixture `CompositeHazard` over bootstrap posteriors (LnNormal scenario parents, method of moments, effective record length 50, 500 replications each) with an uncertain day/night mixture `CompositeConsequence` behind a deterministic parametric fragility at N = 500 — every realization rebuilds the combined distribution, so `Mixture.CreateEmpiricalCDF()` re-tabulates ~200 bins over the K = 4 sampled children before the risk integrand inverts the interpolated CDF at every quadrature node. Committed row (isolated, Release, `--reps 3`): mean-only 0.159 s, full 6.633 s, 3.10 GB, GC 282/275/5, pin `c183d28f…` (stable across single-rep and median-of-3). While adding F6 to the harness remarks, the stale "three reference fixtures" count and the file's three phase tokens were corrected per the phase-language policy (the harness sat outside the T2 sweep's mandate).
- `aa6ddac` **T9 upstream measurements** (1 file, +59): the record-only micro-measurement section in RESULTS.md (summary below).

**F6 measurement row:**

| Fixture | Mean-only median (s) | Full median (s) | Allocated (GB) | GC gen0/1/2 | SHA-256 |
|---|---:|---:|---:|---|---|
| F6 | 0.159 | 6.633 | 3.10 | 282/275/5 | `c183d28f83f41db40a4921542225382e30dc72cafc5df13ffc6ebe76be2d9132` |

**Upstream Numerics measurements (record-only; single-threaded medians of 5; bench Release; both Numerics configurations, with the Debug pass verified by file hash to have loaded the sibling Debug build):**
- `CompositeFunction` ConditionalWeakTable+lock, per non-deterministic child per evaluation: **11.5–18.8 ns Release** (8.9–41.7 ns Debug), two paired-difference estimates agreeing at K = 2 and K = 8; deterministic children bypass it entirely. Engine exposure: one call site (`CompositeTransform.BuildCombined`, mean-convention branch) — the mean pass wraps deterministic-flagged curves and pays nothing; the per-realization ensemble path pays ~12–19 ns × K per integrand evaluation, single-digit µs per realization. Contention is nil (per-child-instance locks over freshly sampled children).
- `EnsembleFunction.Sample`: **1,575 ns/draw** on a 142-char linear template — `XElement.Parse` alone is 1,146 ns and parse + factory 1,592 ns, i.e. reconstruction is effectively the whole draw; a 5,183-char 50-row tabular template pays 29,930 ns (~19×). The constructor parses a validation instance per parameter set (4.1 ms per 1,000 sets). No TotalRisk call site exists today (BestFit-import vehicle), so no committed fixture moves.

**Upstream proposals recorded for the pre-release Numerics batch (decisions pending; nothing implemented this session):** (1) `CompositeFunction` — resolve each child's lock object once at construction into a per-instance array, removing the table lookup from every evaluation; optionally skip the lock on the configured-state mean path, which mutates no child state. (2) `EnsembleFunction` — retain the parsed template `XElement` and hand the factory a deep copy instead of re-parsing per draw; hoist the constructor's per-set validation instance out of the loop.

**Deferred at the technical authority's direction (recorded, not executed):** N4 (the 2.2.0 release prep) and T8 (the package switch) wait until Haden finishes his own pre-release Numerics changes and cuts the release; the sibling Debug HintPath and the CLAUDE.md interim-dependency note stay as written. No commits were made in the numerics repo this session (a Release build was compiled for the measurement pass only — filesystem output, nothing staged). The 36 unpushed numerics commits remain on this machine until Haden pushes. The v1.0 wall-clock head-to-head remains declined (D10).

**Verified:** build 0 warnings; Release fast suite **785/785** (781 + the four new setter tests) after the R1 commits and unchanged through close; code/XML-doc/namespace/dependency validator green; `SingleComponentUncertaintyVerification` (the ParametricResponse anchor family) isolated 4/4; diff-shape witness on the comment commit (5/5 lines comment-shaped); the close-out byte-gate round ran **all six fixtures** as single invocations after the final commit — F1 `4c1472d2…`, F2 `876ce063…`, F3 `6469666e…`, F4 `846234f1…`, F5 `2ae3925b…` (one compiled plan), F6 `c183d28f…` — every pin reproduced bit-exactly, proving the restored setter is hash- and seed-inert.

**Next:** Haden's pre-release Numerics changes (the two upstream proposals above are candidates for that batch, alongside the recorded N4 release-note obligations), then the 2.2.0 release gate (Haden pushes, tags, releases), T8 (package switch + Phase 8 exit, with all six byte gates expected bit-identical), and the remaining roadmap phases.

## 2026-07-30 — Review remediation, session T4–T7 (doc overhaul, new reference pages, parity closure, literal ruling)

**Goal:** execute the four documentation/recording tasks of the remediation program in order — repair the technical-reference set (voice, stale content, accuracy, and the two queued library comment items), standardize the verification pages on run-of-record headers, write the four missing reference pages, close the input-function API-parity question, and apply the approved nine-literal ruling — verifying every content statement against the source before writing it.

**Landed (twelve commits):**
- `364061c` **C1 voice pass** (10 files, +144/−261): all ten technical-reference pages rewritten in as-is voice — phase/landing headers, ratification narration, Q-IDs, and port-instruction voice removed; the in-page event-tree changelog deleted (the response page keeps a contract-level summary); local repository paths and the CLAUDE.md citation retired for requirements-doc citations and published report titles (*Failure Mode Combination Methods in RMC-TotalRisk*, Smith 2026, cited by title); every defect-evidence `.vb` citation kept (`ComponentRiskOutput.vb:39` verbatim).
- `5ee2f9d` **C2 stale content** (5 files, +40/−12): the three input-function page headers now name the shipped type lists (`NonparametricHazard` pointer on the hazard page; `CompositeResponse`/`EventTreeResponse` on the response page); the linear/power transform section rewritten as shipped (verified defaults, co-monotonic semantics, the `PowerFunction.Minimum` wrapper-state caveat); all four interface contract blocks gained their current members (`FunctionType` ×4, `SupportsOrderedCurveSampling`, the exposure-branch surface); the shipped `QuadratureMassLedger`/`ApplyRecordedMass` pipeline named where the mass accounting was anonymous.
- `d9a057d` **C3 accuracy pass, isolated for review** (6 files, +33/−25): the midpoint-trapezoid interim retired as current in docs/verification.md ("Finalized policy"), ead.md, contribution.md, risk-profiles.md, and multi-consequence.md — each now describes the measured residuals as taken under the earlier partition, since replaced by the recorded-mass ledger, with the exact 8.5 A/B figures (5.664e-7 → 3.996e-11 against a 4,000,000-point dense reference) and every recorded bound unchanged a fortiori; the risk-profiles interior-probe rationale corrected to the shipped partial-sum-of-recorded-masses cumulate (mirroring the ruling-approved test wording); loss-exceedance-curves.md now records the audited permanent decision that `SystemConvolution` stays local (the pairwise helper re-derives its lattice step per fold and smears the zero atoms).
- `2c0592a` **C4 library comment items** (3 files, +9/−8; diff-shape witness 17/17 comment-shaped): `SampledFailureMode.cs` cites the documented `ComponentRiskOutput` interim by name; both `FinalizeContributions` ledger-parameter docs (ComponentRealization, FailureModeRealization) replaced the stale boolean/trapezoid wording with the actual non-null-ledger-vs-null contract read from `ContributionAccumulator.FinalizeFromLedger`/`FinalizeDirect`.
- `e622dbb` **C5 verification index** (2 files, +27/−27): the family-table split at the stray blank line rejoined (five rows had rendered headerless); the event-tree row/page retitled from phase-status to capability wording; anchor-column phase/Q-ID tags converted to the families and designs they name (every no-legacy-counterpart fact kept).
- `3d7480c` **C6 per-page standardization** (31 files, +178/−160): every family page opens `**Test class** · **Tests:** n · **Run of record:** date, isolated run` with counts read from the class files ([TestMethod] counts; the event-tree page lists its three classes summing to 14); bodies de-phased with all seeds, tolerances, derivations, and pins verbatim; the two "session log" pointers now cite PROGRESS; stale whole-suite counts reframed as dated run-of-record figures ("the fast suite at that date passed in full (447/447)"); the four validator-skipped legacy template-noise items annotated in README prose (three empty `TestMethod1` stubs, the commented-out `Test_FDA.TestAll` driver, one hello-world async template) — never as CSV rows; stale content synced against source while sweeping (the composite-hazard upstream finding now records the fixed-chunk resolution and the renamed `Test_UpstreamEstimation_IsBitReproducible` pin; multi-consequence records the shipped per-type thresholds; engine-reproducibility points at the multi-component shuffle families; composite-consequence links the landed composite-engine family).
- `421453a`/`1de0dfc`/`54d30f0`/`d1cc5c6` **C7–C10 the four new reference pages** (+102/+134/+124/+129 lines): sensitivity-analysis.md, event-trees.md, hashing-and-seeding.md, results-catalog.md — as-is voice, phase-free from birth, every documented member verified against the source (the `CollectSensitivityInputs` walk, the `ModelRules` strip list verbatim, `ProbabilitySourceKind`/`TreeLinkMode`/`TreeDeletePolicy`, the results roots and profile catalog), each registered in the technical-reference index; the docs-map row now names the full catalog.
- `fa4c789` **C11 parity recordings** (2 files, +3/−3 + AGENTS regen, byte-identical by the future-section convention): `PlanRow.EquivalentAnnual` named in the CostBenefitAnalysis scope in the roadmap and the process guide (year-interpolated EAD between condition years, discounted at the plan rate, annualized — the only cost-benefit math v1.0 shipped, algorithmically headless); the UI-layer future-phase bullet consolidates the recorded presentation dispositions (TRG lines, KDE-smoothed assurance banding over the engine's exact assurance, batch-run orchestration). Node sensitivity was already recorded and was not duplicated.
- `e722c6f` **C12 the nine-literal ruling** (7 files, +9/−9): the approved wordings applied verbatim; no class or method renamed; the closing grep confirms zero references to the old literals anywhere in src/. The two renamed fixture `Name`s are hash-stripped display metadata, so no seed could move — proven by the byte gates below.

**T7 API-parity verdicts (tracer member-diff vs the Dev repo, READ-ONLY):**
- **Hazard** (`ParametricHazard`→`ParametricUnivariateHazard`, `NonparametricHazard`, `CompositeHazard`): **parity.** Every v1.0-only member is the UI/persistence element envelope (Copy/Open/Save/Delete/CreationDate/LastModified/NameOnDisk/UpdateChartSettings/CopyFromExternal, the BinaryFormatter results BLOBs, the project-collection XElement statics) — all deliberately UI-layer by the ratified architecture; v1.1-only members are the documented additive surface.
- **Transform** (`TabularTransform`, `LinearTransform`, `PowerTransform`): **parity** — same envelope split, no gaps.
- **Response** (`TabularResponse`, `ParametricResponse`, `NonFailResponse`, `CompositeResponse`): **one real gap** — v1.0 `ParametricResponse.SetDistributionParameters(IList<double>)` has no v1.1 counterpart (the hazard twin has it, with estimate invalidation; the response only exposes wholesale `ParentDistribution` replacement), and response-functions.md's API example calls the missing member — presented for ruling in the session-close message, deliberately not fixed in-session. `NonFailResponse.GetInstance` is the documented deliberate singleton removal.
- **Consequence** (`TabularConsequence`, `CompositeConsequence`): **parity** — same envelope split, no gaps.
- Not re-diffed by standing disposition: `BestFitHazard` (replaced by posterior injection), `RFAHazard`/`LifeSimConsequence`/`BivariateResponse` (tracked future work), `EventTreeResponse` (exhaustively audited at its own landing), `TabularHazard` (the review's original tracer).

**Judgment-heavy calls for review:**
- Source verification during C1 corrected three claims the pages carried: the LEC moments are a **two-pass weighted central accumulation** (the page said "streaming Welford/West"; `Curve` says two-pass); the CVaR quadrature call site is **retired** by the exact segment-by-segment closed form (the "two 1D call sites" table now lists the risk integral and the AFP probe); and the upstream tail-focus unit tests **exist** (`Test_VegasTailFocusJacobian.cs` verified in the numerics repo), so the "remains item N9" caveat became the shipped fact.
- C6 headers carry date + isolated-run result but no commit hashes: no page or PROGRESS entry records per-family run-of-record commits, and the sourcing rule forbids reconstructing them from git history.
- "Bucket-1" **kept** as the established scenario name (cascade-end-states.md and single-component-uncertainty.md still title with it; the code side matches).
- docs/verification.md (the strategy page at docs root) keeps its roadmap-mirroring conversion-order table — it is a planning companion, not a results page; only the policy section heading (now "The means-versus-tails policy", matching the pages that cite it), the tail-focus-audit pointer, and the CLAUDE.md-citing sentence were reworded.
- The link checker skips GitHub-web navigation links (`../../tree/<branch>` in README.md resolves on github.com, not on disk) — documented in the script.

**Residue (recorded, deliberately untouched):** `RiskProfileVerification.cs:433` still carries a stale inline comment ("the engine's partial sum carries the midpoint-trapezoid partition — a half-interval discretization allowance") that contradicts the corrected XML remark above it — a test-project comment outside the C12 mandate, queued for the next approved test-side pass. Process note: the first C12 commit attempt swept the untracked `VerificationModels/` staging area into the commit via an overbroad `git add`; it was immediately soft-reset and recommitted clean (`e722c6f`), and the folder remains untracked with its files untouched.

**Verified:** build 0 warnings and Release fast suite 781/781 after C4, after C12, and at close; code/XML-doc/namespace/dependency validator green per cluster; verification-traceability validator green after T5 and at close (142 legacy methods / 49 report scenarios); scripted relative-link check over docs/ + README/CLAUDE/AGENTS green (55 files, 232 relative links, zero broken); diff-shape witness on C4 (17/17 comment-shaped lines); all five byte gates reproduce their pins bit-exactly as single invocations at close — F1 `4c1472d2…`, F2 `876ce063…`, F3 `6469666e…`, F4 `846234f1…`, F5 `2ae3925b…` (one compiled plan).

**Next:** the `ParametricResponse.SetDistributionParameters` parity ruling (with the response-functions.md example reconciled whichever way it goes) and the `RiskProfileVerification.cs:433` comment fix; then N4 (2.2.0 release-note prep), the release gate, T8 (package switch + Phase 8 exit), and T9 (the dedicated performance session).

## 2026-07-29 — Review remediation, session T3 (tests + verification comment sweep, literal ruling)

**Goal:** apply the phase-language policy to every comment in both test projects — strip phase numbers, landing chronology, ratification narration, design-question IDs, upstream work-item IDs, and informal spec citations while keeping every seed, realization count, tolerance and its k·SE derivation, pinned constant, oracle provenance (legacy `Test_*` names, `MersenneTwister(12345)`/`(45678)`, MVN seeds 12345/67891/78910), run-of-record capture data, and v1.0-vs-v1.1 comparison verbatim — in two witness-proven comment-only commits, then execute the approved rewording of the four executable diagnostic literals the library sweep deferred.

**Landed (three commits):** `7707613` unit tests (30 files, 245 comment-shaped lines), `56fd878` verification (26 files, 249 comment-shaped lines), `c3b10a3` the four approved literal rewordings (`LegacyEventTreeConverter.cs` — "an IndependentClone structural link", "excluded by the event-tree design", "the future bivariate-response capability"; `ResponseElement.cs` — "reserved for future bivariate response functions") with the two matching expected-string pins updated in the same commit (`LegacyEventTreeConversionTests` expected fragment "Phase 11" → "bivariate-response"; `ResponseElementTests` `Contains("reserved for bivariate")` → `Contains("reserved for future bivariate")`). No test class or method was renamed.

**Judgment-heavy keep/strip calls for review:**
- **"Bucket-1" kept** as the shared oracle scenario's established name: the published verification pages title the scenario with it (joint-failures.md and four siblings), so the code↔page linkage stands until the T5 page pass decides its fate; it is a scenario name, not phase narration.
- Kept verbatim throughout: every seed (including the routing oracle's 10,202,671 and the property-generation seeds 0x10A20261–64), N = 10⁶ counts, all k·SE tolerance derivations with their measured figures (≈ 3e-6 mass residual, ≈ 0.3% CIF discretization, ≈ 0.12% tabulation, ~1e-12 placement noise), the 2024 report table pins, the `Verification/Data/MANIFEST.md` audit date (run-of-record provenance), and every legacy-method disposition and mislabeling note.
- "v0.13 means-versus-tails policy" refs now cite the policy's normative home: "the means-versus-tails policy (docs/verification.md)"; per-assert "per the ratified policy" comments name the policy instead.
- "arch doc §x.y"/"architecture doc §x.y" became `docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §x.y` at each file's first site with bare `§x.y` at member repeats (~12 conversions); the T2 Q-ID dictionary was reused (exposure branches, the failure/non-failure coupling draw, the profile-axis remap, the multi-consequence axis, the branch-explosion guardrails, the multi-stage constructor throw, the legacy fan-out preservation, the end-state labels).
- "NEW Phase 5 family" became "a family … with no legacy counterpart" — the traceability-relevant fact survives without the phase tag; the kitchen-sink registry's "Phase N —" section labels became cluster descriptions and "PHASE LANDING CHECKLIST" became "LANDING CHECKLIST".
- Upstream work-item IDs (N7/N9/N12) were replaced by feature names per the library convention: the recorded-mass ledger, the tail-focus γ-audit, the fixed-chunk bootstrap reduction.

**Stale forward-references synchronized with shipped reality (content edits; no test value, tolerance, or pin moved):**
- `NfipAssuranceVerification` — "the profile-axis remap remains open question Q-T" → the remap now exists; the fixture's flow-space threshold identity means it needs no remap.
- `RiskAnalysisCombosVerification`'s legacy-disposition catalog — the composite oracles "Phase 9 per the roadmap" → converted in `CompositeEngineVerification`/`CompositeHazardVerification`/`CompositeConsequenceVerification`; "TOL 60/65 (+ the FDA variant) are Phase 9 scope" → TOL 60/65 ride `NfipAssuranceVerification`'s bootstrap-hazard ensembles and the FDA variant is retired obsolete (both verified against `legacy-traceability.csv`).
- `CompositeHazardVerification` — the "Upstream finding" remark describing `BootstrapAnalysis` estimation as non-bit-reproducible was rewritten as history: the upstream reduction now sums over a fixed chunk count and `Test_UpstreamEstimation_IsBitReproducible` pins the repaired behavior; the "keep estimated parametric children out of byte-gate fixtures" advice was dropped as superseded.
- The nine "N7 recorded-mass interim" tolerance rationales (Contribution, MultiConsequence ×2, RiskProfile ×2, ScalarUncertainty, SingleComponentUncertainty ×2) now describe the quadrature mass-accounting residual as measured under the earlier midpoint-trapezoid partition, since replaced by the recorded-mass ledger; `RiskProfileVerification`'s claim that the ascending cumulate "carries the midpoint-trapezoid partition" was corrected to the partial sum of recorded per-abscissa masses (verified against `Curve.ApplyRecordedMass`/`CreateProfiles`). Every bound is unchanged and remains valid a fortiori.

**Residue (recorded, deliberately untouched):**
- Nine test-side executable string literals still carry phase/Q-ID/work-item wording — assert messages `RiskAnalysisTests.cs:1505` ("Phase 6.5 interim"), `CompositeConsequenceTests.cs:85` ("Ratified Q-V"), `ComponentGraphTests.cs:392` ("pre-6.7 shape"), `SingleComponentUncertaintyVerification.cs:459/548` ("Q-N"), `RiskProfileVerification.cs:427` ("N7 recorded-mass envelope"); the diagnostic `Console.WriteLine` at `SingleComponentUncertaintyVerification.cs:552`; and the fixture names `SampledFailureModeTests.cs:111` ("Q-N Pin") and `EventTreePhaseCloseVerification.cs:536` ("Phase 10A thread-count reproducibility"). All are test-internal diagnostics that never reach a results payload; proposed disposition: one follow-up ruling like this session's C3.
- Two library comments found out of scope: `SampledFailureMode.cs:544` still says "the documented 4b interim" (a bare phase token the library sweep's greps could not see), and `ComponentRealization.cs:268` documents `FinalizeContributions`' ledger parameter with pre-ledger wording ("masses re-derived by the midpoint-trapezoid partition") that looks stale against `FinalizeFromLedger` — both queued for the technical-reference accuracy pass rather than edited here.

**Verified:** diff-shape witness per comment-only commit (245 and 249 changed lines, every one `///`/`//`-shaped); build 0 warnings, Release fast suite 781/781, code/XML-doc/namespace/dependency and verification-traceability validators green per commit (142 legacy methods / 49 report scenarios); after the literal commit all five byte gates reproduce their pins bit-exactly as single invocations — F1 `4c1472d2…`, F2 `876ce063…`, F3 `6469666e…`, F4 `846234f1…`, F5 `2ae3925b…`. Closing detection greps return zero comment hits across both test projects and zero phase/ratification hits in the library (the nine string literals above are the only file hits). **No verification families were re-run:** the two sweep commits are comment-only (witness-proven compile-inert), and the literal commit changes diagnostic text that never enters a results payload — the byte gates prove it.

**Next:** T4 (technical-reference de-journal + stale-content + accuracy passes, including the two library-comment accuracy items above), T5 (verification-docs standardization), T6 (four new reference pages), T7 (input-function API-parity diff), the string-literal follow-up ruling, then the Numerics release-prep and package-switch sessions.

## 2026-07-29 — Review remediation, session T2 (library comment sweep)

**Goal:** apply the ratified phase-language policy to every comment in `src/RMC.TotalRisk` — strip phase numbers, landing chronology, ratification narration, design-question IDs, and informal spec citations while keeping every v1.0-vs-v1.1 comparison, domain constraint, tolerance derivation, and seed recipe — in four namespace-batched comment-only commits, each proven comment-only by a diff-shape witness before committing.

**Landed (four commits):** `f1cc5f7` Core/Enums/Interfaces (17 files, 90 lines), `d360a11` RiskFunctions (13 files, 130 lines), `981e6bf` Systems (10 files, 193 lines), `cac6b30` Analyses+Results (27 files, 412 lines). Recurring conversions: phase handles became feature names (the multi-consequence axis, the risk-profile catalog, the % contribution diagnostic, the seed-stable perturbation mode, the cascade end-state design, pre-cascade layouts/arithmetic, "earlier payloads" for serialization forward-compatibility); `arch doc §x.y` and `architecture doc vN` became `docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §x.y` at load-bearing sites (~30 conversions) with member-level repeats dropped or left as bare subsection references once the file anchors the document; Q-IDs became the designs they named (Q-V → the exposure-branch contract §6.4.1, Q-N → the shared failure/non-failure coupling draw, Q-T → the profile-axis remap, Q-U → the multi-consequence axis, Q-W → the branch-explosion guardrail, Q-X → an earlier constructor throw on multi-stage chains, the Q2 ruling → deliberately preserving the legacy fan-out semantics).

**Judgment-heavy keep/strip calls for review:**
- Detection is word-bounded: bare `ratified` matches the domain terms "stratified"/"StratificationOptions", so the sweep and the closing greps use `\bratified\b`; sampling vocabulary is untouched. `git grep -E` is POSIX ERE (no `\d`), so the closing Phase grep ran as `Phase [0-9]`.
- Stale forward-references synchronized with shipped behavior (content edits, not just de-phasing): the `NonparametricHazard` mean-curve note now records the deterministic fixed-chunk upstream reduction instead of the retired parallel-sum/byte-gate warning (the resolution CLAUDE.md already records); `RiskAnalysisOptions.Mode` no longer claims reliability is gated on a future engine stage; the options-surface remarks now say the system-risk/reliability fields were serialized ahead of the engine stages that now read them; `Curve.ComputeRiskMeasures` now describes the shipped declared per-type thresholds instead of promising them; the two Numerics `CompositeFunction` fold-in notes are now conditional on a future upstream expansion rather than "planned (roadmap Phase 8)".
- Report citations completed by title per the citation ruling: Appendix D and Appendix G now cite the RMC-TotalRisk Technical Reference Manual; composite report tables cite the RMC-TotalRisk Verification Report.
- `SystemComponent.ProfileHazardElementId`: the question-registry pointer for the `HazardThreshold` asymmetry is replaced by the in-place rationale (removing an established identity attribute would move every component hash).
- `RiskAnalysis.PrepareJointSystem`: "the ratified v0.13 heuristic … (recorded in the phase log)" became "an earlier design harvested the target from the VEGAS warm-up"; the two facts that forced the deterministic probe are kept verbatim.
- `ComponentRiskOutput`: the "v1.0 TODO" wording became "a capability v1.0 declared but never implemented (the commented-out design intent at `ComponentRiskOutput.vb:39`)" — the legacy citation is kept as defect evidence.
- Kept verbatim throughout: v1.0 comparison notes (including the v1.0 percentile-seed collision, the v1.0 matrix read-loop defect, the day/night mean-collapse defect), MVN off-diagonal constants, guardrail limits, and every tolerance and seed recipe.

**Residue awaiting a ruling (deliberately not edited — executable strings):** four diagnostic/validation string literals still carry phase wording: `LegacyEventTreeConverter.cs` lines 217, 353, 356 ("Phase 10A"/"Phase 11"/"ratified") and `ResponseElement.cs` line 412 ("Phase 11"). The comment-only witness rule excludes them, and rewording user-facing messages is a behavioral edit (message-pinning tests may exist). Proposed disposition: reword alongside T3 with explicit approval.

**Verified:** diff-shape witness per commit (90/130/193/412 changed lines, all `///`, `//`, `#region`, `#endregion`); build 0 warnings, Release fast suite 781/781, code/XML-doc/namespace/dependency/traceability validators green per commit; close-out byte gates run as five single invocations after the final commit — F1 `4c1472d2…`, F2 `876ce063…`, F3 `6469666e…`, F4 `846234f1…`, F5 `2ae3925b…` all reproduce their recorded pins bit-exactly; closing detection greps return zero hits in comments across `src/RMC.TotalRisk` (the four string literals above are the only file hits).

**Next:** T3 (tests + verification comment sweep, same mechanics, run-of-record values kept verbatim) plus the string-literal ruling; then the technical-reference/verification doc overhaul (T4/T5), the four new reference pages (T6), and the remaining program sessions.

## 2026-07-29 — Review remediation, session 2 (the twelve technical-authority rulings)

**Goal:** execute the review's decision packet one ruling at a time under the technical authority's boxed answers, with the full gate stack per change.

**Landed (numerics, five commits `13e6092`…`9400317`):** `NegativeJointProbability` fixed to the Fréchet–Hoeffding bound max(0, Σp−(n−1)) with indicated-count semantics on the indicator overload, six pins including the 0.8/0.9→0.7 anchor (D1). The bias-correction proportion restored to the published plotting-position form count(≤)/(B+1) over successful replicates — the (count+1) numerator matched no published estimator (Efron eq. 14.14, the boot package, SciPy all checked); two pins added, including the zero-bias identity at the bootstrap median (D2). MCMC behavior changes ratified keep-both; the ARWMH push-rule and NUTS acceptance pins were found already present, and `MCMCResults.AcceptanceRates` now documents its per-sampler meaning (D3). Seven golden-fixture pins read v2.1.4-format XML captured verbatim from the pre-batch build — capture also established the 2.1.4 scalar writer faulted on table-bearing types, so no legacy tableless files exist (D4; D5 keep-strict release-noted). A constant kernel-density sample now yields a near-point-mass bandwidth (magnitude × 1e-9; 1e-9 absolute for zero) instead of magnitude-proportional fabricated spread; single observations follow the same rule; four tests re-pinned (D6). `DefaultMVNUNISeed` is static readonly with the shared-seed error-correlation caveat documented and a three-dimensional bit-reproducibility pin (D7). Numerics suites 2008/2008 on all four target frameworks per commit.

**Landed (this repo, three commits):** report citations link the published RMC documentation site and the architecture doc states the headless constraints as its own normative content (D8/D9, `3d28df0`); Phase 10B scope gains the headless node-importance analysis in the roadmap and the normative tree design, the Phase 12 scope records the declined v1.0 wall-clock head-to-head, and the phase-language policy is recorded in the process guide (D10–D12, `c421ec4`).

**F4 byte-gate finding (`92d8775`):** the cross-repo tripwire found F4 at `846234f1…` against the recorded pin. Bisecting with fixed engine states exonerated the environment (the 8.5-close pair reproduces the old hash bit-exactly today) and every session commit, isolating the movement to the pre-existing numerics boundary-clipping commit `eb6718b` (2026-07-27), whose measurement-ordering gap left the F4 row stale. F1/F2/F3/F5 reproduce their pins. Re-pinned with approval and provenance; standing rule added: close-out byte-gate rounds run every committed fixture after the session's final upstream commit.

**Verified:** build 0 warnings; Release fast suite 781/781; doc/traceability validators green; F1 `4c1472d2…`, F2 `876ce063…`, F3 `6469666e…`, F4 `846234f1…` (re-pinned), F5 `2ae3925b…`.

**Next:** the comment-language sweeps (T2 library, T3 tests+verification) under the ratified phase-language policy; the technical-reference/verification doc overhaul and four new reference pages; the remaining input-function API-parity diff; 2.2.0 release notes must enumerate the ruling outcomes (D1, D2, D4/D5 strictness, D6 convention, D7, the MCMC changes with their BestFit impact).

## 2026-07-29 — Phases 0–10A review remediation, session 1 (encoding, statuses, links)

**Goal:** land the zero-decision slice of the full Phases 0–10A review remediation — repair the comment-encoding damage, reconcile stale status tracking, and remove unresolvable links and internal-tooling references from the published docs — and assemble the review's decision packet for technical-authority rulings.

**Landed:** 111 double-encoded UTF-8 sequences repaired in `RiskAnalysis.cs`/`RiskPercentileAssembler.cs` (all comment sites plus one user-facing validation message; a diff-shape witness confirmed no other executable change). Roadmap summary rows corrected (9 partial, 10A complete, the missing 8.6 row added, the 12 coverage-gate note); ported-types Later row updated with AGENTS.md regenerated; architecture doc §11 refiled (eight resolved questions moved out of Open, Q-Z records the upstream N12 resolution and the renamed reproducibility pin, the status log restored to reverse-chronological order, a retired-numbering note added); shared-functions strategy N4/§6/§8 reconciled. Four unresolvable architecture-doc links fixed; ten local plan-file citations and internal-tooling wording removed from this log; perf-results title and three missing entry separators corrected.

**Verified:** build 0 warnings; Release fast suite 781/781; code/XML-doc/namespace/dependency/traceability validators green; F1 and F5 results hashes reproduced bit-identically after the encoding repair (`4c1472d2…`, `2ae3925b…`).

**Next:** the review's decision packet (12 rulings, including the upstream Numerics `NegativeJointProbability` Fréchet-bound defect — no v1.1 call sites) awaits technical-authority direction. Remaining remediation sequence: comment-language sweeps (library, then tests+verification), technical-reference/verification doc overhaul plus the four new reference pages, parity-closure recordings (equivalent-annual-damages math on the future cost-benefit scope; the event-tree node-sensitivity disposition), the Numerics hygiene/ruling sessions, the 2.2.0 release gate, and the package switch.

## 2026-07-28 - Phase 10A event-tree response COMPLETE

**Goal and authority:** close every remaining Phase 10A gate without changing production probability
propagation, sibling normalization, remainder/path products, interpolation, sampling, seed recipes,
tolerances, clipping, canonical identity, serialization/reference semantics, or reduction order.
The only production surface added is an internal, runtime-only scheduling cap and null-by-default
iteration observer used to prove actual thread-count invariance; neither is serialized, hashed, or
copied into a run snapshot, and production defaults are unchanged.

**Fixed-seed properties:** `EventTreePropertyTests` generates 128 valid small trees from seeds
`0x10A20261`..`0x10A20264` (32 cases each), spanning shallow/deep/wide topology, every sibling-sum
regime, both remainder forms, terminal classifications, scalar/aligned-table/nested sources,
internal/external repeated clones, and both XML modes. An independent recursive oracle checks every
terminal, aggregate failure, and mass conservation. Round trips, explicit materialization,
metadata/GUID/name/order invariance, and compute sensitivity are pinned. Failures report a
deterministically minimized self-contained XML counterexample after at most 32 accepted
branch/subtree removals or scalar-source simplifications.

**Verification closure:** the independent BCL router uses seed **10,202,671**, **N=1,000,000**, and
12 simultaneous terminal/aggregate comparisons under a finite-sample Hoeffding union bound with
familywise `alpha=1e-6`; maximum error **0.001039** is below the common bound
**0.00291492404603552**. Aggregate SRS/LHS uses **N=256**, **R=12**, seeds
**10,202,731..10,202,742**, exact expectation 0.4, and complete two-dimension strata checks:
sample variances are **1.2758276684807033e-5** versus **3.3868001385987057e-10**, ratio
**37,670.592189374926**. The production expanded five-port analysis uses seed **10,202,791** and 100
LHS realizations: sequential, capped four-worker, and two default runs are bit-identical in
aggregate/per-leaf curves, full JSON, hashes, complete seed maps, and stable IDs/ports; observed
unique executing thread identities were 1, 5, and 23/26.

**Coverage and performance:** the Release fast suite is **781/781** and unit-only library coverage is
**90.40% (12,257/13,584)**. Final isolated F5 median-of-three: one published plan; 1,105
instructions, 1,104 edges, 745 branches; setup **0.035477 s / 20.55 MB**; 32 indexed reads
**0.283314 s / 99.79 MB**; unchanged SHA-256
`2ae3925bfb7488cbfa4bd516cc2d4eb7d71c6f84bfff9f4891873a2bfd811349`. Wall-clock movement is
ordinary workstation variation; the deterministic allocation shape, plan count, and hash are stable.

**Final gates:** warning-free `dotnet build`; Release fast suite **781/781**; isolated
`EventTreeVerification` **14/14**; coverage **90.40%**; traceability **142 legacy methods / 49
report scenarios**; code/XML-doc/namespace/dependency validation green; isolated final F5/hash green;
and unstaged whitespace validation green.

**Status:** isolated `EventTreeVerification` is **14/14**. The roadmap, normative design,
architecture history, technical reference/map, verification page/map, performance ledger, and
ported-types matrix now record complete evidence. Phase 10A is complete and Phase 10B is unblocked;
Phase 10B was not begun in this session.

## 2026-07-28 - Phase 10A immutable compiled-plan/performance slice landed (PARTIAL)

**Goal:** reuse one immutable expanded event-tree occurrence/evaluation plan across all read paths, invalidate every affected owner when live compute content changes, preserve exact rollback, and characterize a large repeated-link tree without changing any established numerical rule.

**Landed:** each `EventTreeResponse` owns an instance-scoped cached occurrence plan containing canonical parent-before-child primitive-index instructions, leaves, projected identity, sampler dimensions, and complete recursive dependencies. A separately checkpointed immutable branch-address plan is prepared lazily so identity/hash-only reads retain their prior no-port-allocation behavior. Publication uses a per-instance lock plus volatile references; concurrent read-only cold and stale publication converges on one plan, and the hot evaluation path performs a single linear pass over compiled edges with reusable primitive work arrays.

**Invalidation and rollback:** controlled add, insert, move, replace, delete, materialize, paste, and prune invalidate only after tentative expanded-cycle validation succeeds. Terminal classification and probability-source replacement, direct/nested event-tree sources, internal/external independent links, resolver-backed live functions, and table/ordinary-response edits invalidate affected owners. Compute revisions provide immediate propagation; canonical fingerprints defensively detect silent in-place Numerics distribution/table or ordinary-response changes. Names, descriptions, IDs, hazard labels/units, and nonterminal classification remain cache-inert. Coordinated tree and graph checkpoints collectively preserve topology, IDs, child order, append-only branch IDs/ports/paths, connections, the exact occurrence/branch cache objects, and compute revision/build count. Failed edits restore that state; hashes and configured sampler observations remain unchanged, while already-notified dependent owners may remain invalidated but equivalently rebuildable.

**Numerical contract preserved:** sibling compensated-sum order, normalization only above one, residual/remainder assignment, terminal path products, interpolation/extrapolation, aggregate compensated order, sampling, content-derived seeds, tolerances, both XML modes, resolver semantics, projected canonical identity, and aggregate/per-leaf graph contracts are unchanged. The F5 byte gate is identical before and after caching.

**Performance:** isolated Release F5 (`--reps 3`) expands 24 external independent links into 1,105 instructions, 1,104 edges, and 745 branches over 33 hazards. Setup improved from **0.086353 s / 51.70 MB** to **0.031395 s / 20.55 MB**; 32 indexed reads improved from **1.157486 s / 1,695.75 MB** to **0.239866 s / 99.77 MB**. Both produce SHA-256 `2ae3925bfb7488cbfa4bd516cc2d4eb7d71c6f84bfff9f4891873a2bfd811349`; no unapproved threshold or floating-point allowance was introduced.

**Verified:** new compiled-plan fast family **8/8**; affected cache/authoring/recursive tests **25/25**; warning-free `dotnet build`; Release fast suite **780/780**; isolated `EventTreeVerification` **11/11** unchanged; F5 final median-of-three and hash gate recorded; traceability remains **142 legacy methods and 49 report scenarios**; code/XML-doc/namespace/dependency/public-class validation and `git diff --check` green.

**Still open before Phase 10A can close:** fixed-seed property-based valid-tree generation/minimized counterexamples; independent branch-routing Monte Carlo; aggregate LHS variance-reduction evidence; explicit production thread-count reproducibility; final >=90% library coverage accounting; and the remaining phase-close coverage/performance review. Phase 10B remains gated behind full Phase 10A closure.

## 2026-07-28 - Phase 10A expanded event-tree graph-integration slice landed (PARTIAL)

**Goal:** expose every arbitrary n-way event-tree terminal through the existing headless risk-graph authoring, persistence, projection, validation, identity, and compute surfaces without changing the established probability or ordinary response-port contracts.

**Authority and compatibility:** Haden Smith explicitly approved the additive expanded-branch canonical identity contract. The default response view remains exactly port `0 = Fail`, port `1 = Non-Fail`; expanded output is opt-in and cannot be mixed with aggregate outputs. Expanded port `2` is the stable implicit-unmodeled non-failure branch. Every authored or linked terminal receives an append-only port from 3, so the v1 requirement for more than two splits, arbitrary sibling counts, normalization, and automatic remainder is preserved with no capability loss.

**Landed:** shared `ResponseElement` branch discovery/connection authoring; ID-addressed `RiskConnection` and `ResponseStage` persistence with exact-name migration fallback; append-only linked-occurrence port registry; stable mapping across rename, metadata edits, sibling reorder, copy/paste, link materialization, pruning, and both XML modes; graph factory/resolver restoration; branch-aware validation; arbitrary n-way `SystemComponent` failure-mode projection; exclusive end-state grouping; exact per-leaf mean/indexed probability sampling; and projected branch identity that is persistence-ID/name/port/order/mode inert while remaining sensitive to selected compute content.

**Stale-connection policy:** `ComponentGraph.DeleteEventTreeNode` defaults to `RejectIfReferenced` and restores the full edit when a connected terminal would disappear. `CascadeLinks` clears only structural, secondary, consequence, or hazard-binding slots selecting the removed stable branch. `MaterializeLinks` preserves a connection only when exact link materialization retains the same branch ID and port; a deleted direct terminal never receives a fabricated substitute. Failed edits, including observer exceptions during cascade, restore topology, connection fields, IDs, branch-port allocation, canonical hash, and configured sampler state exactly.

**Numerical contract preserved:** no event-tree sibling normalization, remainder computation, path product, compensated aggregate, interpolation/extrapolation, sampling, seed, tolerance, or reference semantic changed. Graph-connected leaf probabilities are the existing terminal outputs; hazard frequency, consequence, annualization, and risk remain outside the event tree. Aggregate `EventTreeResponse` failure remains the sum of failure-classified leaves.

**Verified:** focused graph/canonicalization/stage suite **52/52**; warning-free `dotnet build`; Release fast suite **772/772**; isolated `EventTreeVerification` **11/11**, adding five graph-connected terminal consequences (`0.10`, `0.15`, `0.09`, `0.36`, `0.30`) against an independent n-way path-product oracle and aggregate failure `0.34`; traceability remains **142 legacy methods and 49 report scenarios**; code/XML-doc/namespace/dependency/public-class validator green; `git diff --check` clean.

**Still open before Phase 10A can close:** immutable compiled-plan caching/invalidation; large-tree performance; property-based testing; branch-routing Monte Carlo; aggregate LHS variance reduction; and the remaining thread-count, coverage, and performance gates. Phase 10A remains partial and Phase 10B remains gated behind full closure.

## 2026-07-28 - Phase 10A legacy XML conversion/template vertical slice landed (PARTIAL)

**Goal:** import the released recursive event-tree XML and representative shipped templates into the landed v1.1 controlled tree model without changing probability, interpolation, identity, seeding, or serialization contracts.

**Legacy authority audited:** the partial C# and released VB `EventTreeResponse`/event-node implementations, `Test_TotalRisk/Test_EventTree.vb`, and shipped `RMC-TotalRisk/Resources/TreeTemplates.xml`. The inventory contains 29 roots, 466 nodes, and 219 chance nodes: 208 `MultiValue`, 11 `SingleValue`, and no response/event-node/link sources. The exact shipped `Basic` and `Concrete Dam Gate Failure` roots are committed with an authority manifest.

**Landed:** import-only recognition of a direct recursive `Node` or the released `EventTreeResponse/Node` and `EventTreeResponse/EventTree/Node` envelopes; both `HazardLevels`/`HazardIntervals` and `NodeGuid`/`NodeGUID`; deterministic preservation or path-derived creation of node IDs; scalar, compact/table, and name-only ordinary/nested response sources; legacy automatic remainder creation; resolver-backed response-ID repair; current-only explicit-node/edge writes in both serialization modes; and deterministic path-specific malformed, missing-reference, excluded-node, duplicate-ID, and unsupported-source diagnostics. Failed conversion and converted-source cycle detection leave live configured samplers unchanged.

**Compatibility boundary:** legacy `EventNode` means reuse of another node's probability source, not reuse of a subtree, so it is rejected rather than misrepresented as Phase 10A `IndependentClone`. The commented `SecondaryHazardNode` remains excluded, and `WeightedHazardLevel` remains Phase 11 bivariate-response work. Legacy `Test_Product` has no assertion and constructs no event tree, so it is classified as a non-oracle workbench.

**Numerical and identity contracts preserved:** compensated sibling sums, normalization only above one, remainder/residual behavior, terminal path products, compensated failure aggregation, established Normal-Z interpolation, occurrence-aware child seeding, and output ports are unchanged. Legacy wrappers, valid or derived IDs, names, descriptions, sibling order, serialization mode, and repaired reference wrappers remain hash/seed-inert. No established algorithm, formula, tolerance, seed behavior, clipping, or normalization policy changed.

**Verified:** focused legacy-conversion unit tests **6/6**; warning-free `dotnet build`; Release fast suite **761/761**; isolated `EventTreeVerification` **10/10**, including legacy `TestIO`, both exact shipped fixtures terminal-for-terminal against an independent recursive oracle, and 256 realization-for-realization nested-response LHS parity across both XML modes; traceability validator green at **142 legacy methods / 113 applicable methods** plus all 49 report scenarios; full code/XML-doc/namespace/dependency/public-class validator green; `git diff --check` clean.

**Still open before Phase 10A can close:** expanded per-leaf graph output ports and stale-connection policy; immutable compiled-plan caching/invalidation and allocation-free O(V+E) evaluation; large-tree performance fixtures; property-based testing; independent branch-routing Monte Carlo; graph-connected per-leaf consequence verification; aggregate LHS variance reduction; and the remaining thread-count, coverage, and performance exit gates. Phase 10A remains partial and Phase 10B remains gated behind full closure.


## 2026-07-28 — Phase 10A recursive probability-source vertical slice landed (PARTIAL)

**Goal:** allow `EventTreeResponse` to participate recursively as a chance-node probability source while preserving every landed probability, interpolation, seeding, identity, serialization, and output-port contract.

**Landed:** direct and arbitrary acyclic multi-level event-tree response sources evaluated at the caller's current hazard ordinate through the established response-CDF contract; recursive occurrence-plan discovery across nested event trees, ordinary response functions, uncertain tables, and internal/external `IndependentClone` links; exact recursive `SamplingDimensions`/`IsDeterministic`; isolated deterministic epistemic streams for every nested occurrence, including repeated references to one live response; realization-index, co-monotonic percentile, and LHS propagation through the full source/link graph; source-aware projected identity; SelfContained and ByReference nested XML with resolver ID/name fallback and reference repair; and complete direct, indirect, mixed source/link, and cross-function cycle paths.

**Transactional sampler contract:** every referenced occurrence is set up on an isolated self-contained clone, its exact child percentile columns are copied into the owner's flattened sampler, and the live stored function remains untouched. Failed recursive compilation, clone/setup, sampler-capacity validation, or stale-dimension detection restores the owner's exact prior sample size, percentile matrix, sampler identity, and occurrence bindings. Diagnostics identify the owning response, chance occurrence, canonical path, referenced function, and nested failure.

**Numerical and identity contracts preserved:** compensated sibling sums, raw probabilities when `S <= 1`, `q/S` normalization only when `S > 1`, explicit/implicit residual branches, path products, compensated failure aggregation, established Normal-Z response interpolation/extrapolation, the child-seed recipe, and output ports are unchanged. IDs, names, sibling presentation order, serialization mode, and reference wrappers remain hash/seed-inert; nested compute content moves the parent hash. Event trees still support `IndependentClone` only and reject `SharedLogical`.

**Verified:** focused fast event-tree/probability/hash suite **55/55**; warning-free `dotnet build`; Release fast suite **755/755**; isolated `EventTreeVerification` **7/7**, including independent multi-level analytic caller-hazard parity and 256 realization-for-realization nested LHS comparisons; code/XML-doc/namespace/dependency/public-class/traceability validator green; `git diff --check` clean. The canonical-hash kitchen sink now includes a nested event-tree source and repeated internal occurrence.

**Still open before Phase 10A can close:** legacy recursive-XML conversion and representative templates; expanded graph ports and stale-connection policy; immutable cached-plan invalidation and allocation-free O(V+E) evaluation; large-tree performance fixtures; property-based tests; independent branch-routing Monte Carlo; graph-connected per-leaf consequence equivalence; aggregate LHS variance reduction; and the remaining thread-count, legacy, and performance exit gates. Phase 10A remains partial and Phase 10B remains gated behind full closure.

## 2026-07-28 - Phase 10A controlled authoring/topology vertical slice landed (PARTIAL)

**Goal:** complete the next coherent controlled-authoring and topology surface without changing the landed event-tree probability, serialization-identity, seed, or output-port contracts.

**Landed:** public immutable `TreeFragment`; snapshot copy and fresh-ID paste with fragment-local link remapping and live external-reference preservation; transactional `ReplaceSubtree`, internal/external `MaterializeLink`, `PruneUnreachable`, and all three delete policies including `TreeDeletePolicy.MaterializeLinks`; deterministic expanded topological order; unreachable-node detection; internal, external, incoming, and outgoing reference queries; and operation/source/target/reason diagnostics for invalid or unresolved targets. Cross-tree fragments promote out-of-fragment internal targets to explicit external references when the source tree has an owner.

**Transactional contract:** every multi-step mutation snapshots authored nodes, parentage, child order, ownership, lookup state, and the output-port allocator. A failed operation detaches provisional clones and restores the exact pre-operation XML topology, persistent IDs, output ports, canonical hash, and configured-sampler validity; the unit suite exercises failure after provisional IDs and ports have been attached.

**Numerical contract preserved:** the new surface is authoring-only and introduces no serialized compute property, formula, tolerance, seed rule, or local random-number generator. Raw sibling probabilities still apply when the compensated sum is at most one, normalization remains `q/S` only above one, residual behavior and compensated aggregation are unchanged, and event-tree links remain `IndependentClone` only. Fresh IDs, names, presentation order, and persistence wrappers remain outside projected canonical identity.

**Verified:** warning-free `dotnet build`; Release fast suite **747/747**; isolated `EventTreeVerification` **5/5**. No verification case was added because this slice has no new numerically observable behavior; fast tests prove copy/materialization calculation parity and sampler/hash rollback. The code/XML-doc/namespace/dependency/public-class/traceability validator is green.

**Still open before Phase 10A can close:** recursive `EventTreeResponse` probability sources; legacy recursive-XML conversion and representative templates; expanded graph ports and stale-connection policy; immutable cached-plan invalidation and allocation-free O(V+E) evaluation; large-tree performance fixtures; property-based tests; independent branch-routing Monte Carlo; graph-connected per-leaf consequence equivalence; LHS variance reduction; and the remaining thread-count, legacy, and performance exit gates. Phase 10A remains partial and Phase 10B remains gated behind full closure.

## 2026-07-28 — Phase 10A independent-link vertical slice landed (PARTIAL)

**Goal:** extend the event-tree foundation with the next coherent reference-aware slice while preserving the landed conditional-probability, seed, serialization-identity, and output-port contracts.

**Landed:** public `EventTreeLinkNode`; controlled internal and external `IndependentClone` link authoring; immutable expanded occurrence plans; transactional same-tree and cross-function cycle rejection with full function/node paths; resolver-backed self-contained and by-reference XML; ID/name fallback and persistence-reference repair; distinct sampler occurrences for repeated linked and ordinary response sources; linked tables evaluated on the caller hazard axis; link-aware branch evaluation; and projected canonical identity that excludes IDs, names, authoring order, and persistence wrappers. The canonical-hash kitchen sink now contains a repeated independent subtree occurrence.

**Numerical contract preserved:** explicit sibling probabilities remain raw when their compensated sum is at most one and are divided by that sum only when it exceeds one; explicit and implicit residual behavior, terminal path products, compensated failure aggregation, content-derived child seeds, tolerances, and the existing implicit output-port contract are unchanged. `SharedLogical` links remain rejected for event trees.

**Verified:** fast unit coverage includes the new public link class, internal/external binding, isolated repeated-source sampling, linked-axis interpolation, both XML modes, resolver ID/name fallback, projected-hash invariance/sensitivity, transactional cross-tree cycle diagnostics, delete policies, and reproducibility. Final gates: warning-free `dotnet build`; Release fast suite **737/737**; isolated `EventTreeVerification` **5/5** for the original analytic/indexed cases plus linked-subtree parity across XML modes and linked-uncertainty reproducibility across round trips and authoring order; and the code/XML-doc/namespace/dependency/public-class/traceability validator green.

**Still open before Phase 10A can close:** `TreeFragment` copy/paste plus replace/materialize/prune operations; topological/unreachable/reference queries; recursive `EventTreeResponse` probability sources; legacy recursive-XML conversion and representative templates; expanded graph ports and stale-connection policy; immutable cached-plan invalidation and allocation-free O(V+E) evaluation; large-tree performance fixtures; property-based tests; independent branch-routing Monte Carlo; graph-connected per-leaf consequence equivalence; LHS variance reduction; and the remaining thread-count/legacy/performance exit gates. Phase 10A remains partial and Phase 10B remains gated behind its full closure.

## 2026-07-28 — Phase 10A first vertical slice landed (PARTIAL)

**Goal:** begin Phase 10A with the largest coherent end-to-end event-tree slice that could be implemented, tested, verified, and documented without pretending the multi-week full phase was complete.

**Landed:** common `TreeLinkMode`/`TreeDeletePolicy`/`TreeNodeReference` and immutable branch descriptor/sample contracts; controlled `EventTree` ownership over initiating/chance/remainder nodes; transactional add/insert/move/delete; deterministic DFS/BFS/search/ancestry/reachability/leaves/structural hashes; scalar, aligned uncertain-table, and ordinary response-function probability sources; `EventTreeResponse` mean/percentile/indexed sampling with recursive direct-child setup and flattened sampler columns; exhaustive terminal outputs plus aggregate failure; explicit node/edge self-contained and by-reference XML; projected metadata/GUID/presentation-order-inert hashing; factory/discriminator/kitchen-sink registrations; and mirrored public-class tests.

**Probability contract preserved:** compensated sibling sums; explicit probabilities unchanged when their sum is at most one; proportional normalization when above one; remainder `1-S` or zero; path products; failure-terminal sum; and a stable implicit non-failure branch for unassigned mass. `SecondaryHazardNode` was not added and `WeightedHazardLevel` was untouched. No hazard frequency, consequence, annualization, or risk math moved into the response.

**Verified:** warning-free `dotnet build`; Release fast gate **724/724**; isolated `EventTreeVerification` **3/3** (deep/wide analytic path products, over-allocation normalization, and 256 realization-for-realization LHS table samples); code/XML-doc/namespace/dependency/public-class/traceability validator green. Verification results are recorded in [event-tree.md](verification/event-tree.md). Legacy `Test_Product` is explicitly not an oracle.

**Still open before Phase 10A can close:** `EventTreeLinkNode`, internal/external independent-clone occurrence binding, `TreeFragment` copy/paste and replace/materialize/prune operations, topological/unreachable/reference queries, cross-function cycle paths and recursive tree sources, repeated referenced-response occurrences, legacy recursive-XML conversion/templates, expanded graph ports and stale-connection policy, immutable compiled plans/cache invalidation/allocation-free O(V+E) evaluation, large-tree performance fixtures, property-based tests, branch-routing Monte Carlo, graph-connected consequence equivalence, LHS variance-reduction and thread/round-trip/link reproducibility gates. The roadmap remains partial and Phase 10B remains gated behind full 10A closure.

## 2026-07-28 — Event/fault-tree response implementation design complete

**Goal:** produce a decision-complete plan another session can implement without redoing the legacy audit or reopening the central semantics, while preserving all previously requested scope.

**Landed (documentation only):** the normative [event-tree and fault-tree response design](requirements/EVENT_AND_FAULT_TREE_RESPONSE_DESIGN.md), roadmap split into Phase 10A (common/event foundation) and 10B (static fault tree), architecture v0.21/Q-B/Q-O resolution, technical/verification maps, bibliography, and corrected legacy traceability. The design specifies conditional fragility `P(F|h)` as the only tree output responsibility; hazard probability remains in hazard functions and risk remains in graph connections plus `RiskAnalysis`. It locks the event probability/remainder rules, exact ROBDD fault evaluation, stable per-leaf ports, internal/external references, independent-clone and shared-logical semantics, copy/paste/add/insert/delete/link/manipulation APIs, traversal/search/topological/pruning algorithms, compiled performance model, recursive LHS/content seeding, two-mode serialization, projected hashing, unit/verification/performance gates, file/phase order, and definition of done.

**Legacy findings confirmed:** `SecondaryHazardNode` and its paths are commented out in both old implementations, no live factory/template/test uses them, and they are excluded. `WeightedHazardLevel` is active in legacy `BivariateResponse`, so it remains Phase 11 scope and is not mislabeled as dead event-tree code. The 29 templates contain 466 event nodes (largest 33, depth 10) but no node/response references. `Test_EventTree.Test_Product` has no assertion and does not create an event tree; verification now uses analytic path products, exhaustive identities, and independent branch-routing oracles instead of claiming false legacy parity.

**Assessment:** Phase 10A is approximately 5.5–8 contributor-weeks. Exact static fault trees add approximately 4–6.5 weeks after that foundation; combined estimate 9.5–14.5 weeks. The fault-tree cost-benefit is favorable when practitioners need repeated basic events, because exact shared-event semantics avoid exponential manual event-tree expansion and modeling errors. Recommendation is to plan 10B now but gate it after 10A; defer rather than ship naive independence or truncated-cut-set probability math.

**Code impact:** none. No algorithm, serialized model, hash, seed, or result changed in this documentation session.

> Newest entries first. Keep entries short: what landed, what's verified, what's next. Every session appends an entry; phase completions append "— PHASE N COMPLETE" to the title.

## 2026-07-27 - Forensic numerical and contract remediation — PHASE 8.6 COMPLETE

**Goal:** Close the release-blocking findings from the v1.1 forensic audit without changing established algorithms, default tolerances, correlations, seed behavior, or reference-result doctrine beyond the approved endpoint-mass correction and result manifest.

**Numerics upstream:** committed the repository technical-authority rule, isolated the previously reviewed KDE and MCMC work, and landed lazy `UnionPCMLazy`, `ExclusivePCMLazy`, `PositivelyDependentExclusiveLazy`, plus comprehensive probability-output clipping through `Tools.Clamp`. Dense overloads remain for compatibility and parity tests. The lazy APIs preserve subset-size/lexicographic order, PCM formulas, inclusion/exclusion association, closing half-gap row, dual convergence predicate, and the `1E-4` defaults. Full Numerics Release suite: **1,993/1,993 on each of net481, net8.0, net9.0, and net10.0**, zero warnings.

**TotalRisk numerical correction:** every joint-failure dependency now uses caller-owned lazy enumeration buffers; no execution path materializes the dense `U × (2^U−1)` matrix. Exclusive partitions apply the approved deterministic remaining-budget clip before consequences, profiles, or contributions. The AGK domain is the sampled hazard's natural support; accepted interior weights remain unchanged and explicit endpoint rectangles complete Appendix D's `K+2` construction, with the upper endpoint carrying the exact residual so every exhaustive ledger equals one. `Curve` publishes compensated recorded mass, faults invalid streams, supports degenerate distributions, and thins by deterministic maximum log-log interpolation error.

**Contracts and provenance:** immutable run snapshots, concurrent-run exclusion, `IAnalysis.IsRunning`, atomic publication, task-based exception/cancellation propagation with completion notification, immutable/read-only result surfaces, unknown-graph-type failure, corrected effective-option hashing, structured validation/diagnostics, and deterministic append-only run manifests are covered. The 13 missing public-class test files and the public-class repository guard were added. `QuadratureMassLedger` is internal, pooled, disposable infrastructure.

**Performance:** pooled curve workspaces, in-place pair coalescing/ordinate reuse, and inline storage for the dominant scalar risk points removed the ledger-era regression. A same-session alternating comparison against safety-gate commit `3a03371` avoids the workstation's material wall-clock drift: current versus baseline three-repetition Release medians were **F1 7.763 vs 8.145 s**, **F2 26.839 vs 29.173 s**, and **F3 10.503 vs 10.554 s**. Current allocations are **3.10/12.23/6.03 GB**, below the pre-ledger 3.88/16.52/7.64 GB baselines. F1/F3 retain hashes `4c1472d2…`/`6469666e…`; F2 intentionally re-pins to `876ce063…` because the failure-probability probe now uses the same endpoint rectangles as production integration, changing automatic VEGAS focusing without changing seeds or definitions.

**Verification closure:** the new engine-level composite family covers deterministic and uncertain composite consequence, hazard, response, and mixture-identity scenarios. Active legacy NFIP TOL 60/65 bootstrap configurations are covered; the FDA/NFIP variant is explicitly obsolete and excluded by user direction. `docs/verification/legacy-traceability.csv` accounts for all **141** legacy `Test_*` methods (**112** applicable mappings) and every verification-report scenario **1–49**; the validator is part of the mandatory documentation gate.

**Verified:** Release build zero warnings; fast Release suite **698/698 in 11.5 s**; documentation/namespace/dependency/traceability validator green; unit-only line coverage **91.25% (9,864/10,837)**. All **28 verification families / 159 tests** ran isolated and passed, including `JointFailures` 9/9, `RiskAnalysisCombos` 5/5, `CascadeEndState` 8/8, `Contribution` 5/5, `RiskProfile` 5/5, `SystemRisk` 5/5, `SystemRiskMatrix` 13/13, `CompositeEngine` 5/5, and `NfipAssurance` 7/7. Numerics is green at **1,993/1,993 on each of net481, net8.0, net9.0, and net10.0**. No tolerance widening, correlation change, seed change, or algorithm replacement was used.

**Next:** Phase 9 may resume after Haden Smith's review of this safety-gate commit.

## 2026-07-26 - Polish & optimization stages 0/2/3 — PHASE 8.5 COMPLETE

**Goal:** Finish the approved Phase 8.5 plan — the numerics upstream work, the new capability that had to be append-only, and the one deliberate value-moving batch with its re-pin.

**Stage 0 (numerics, branch `bug-fixes-and-enhancements`).** *N12* — `BootstrapAnalysis` and `UncertaintyAnalysisResults` made bit-reproducible and faster at once: every reduction over replications inverted from `for each quantile { Parallel.For over replications }` to one parallel loop over replications with the quantile loop inside, and merged over a **fixed** chunk count instead of `Tools.ParallelAdd`'s CAS loop, so the floating-point association tree no longer depends on the thread count. The class review also fixed a real bias (`ExpectedProbabilities` divided by the full replication count while skipping failed fits), surfaced failures through `FailedReplications`, and removed LINQ from loop bounds. *N13* — `Factorial.NextCombination`/`AllCombinationsLazy` and `Probability.IndependentExclusiveLazy` with an `ExclusiveEnumerationStatus`, removing the 2^D materialization. *N14* — AGK recorder capture-array pooling. *N17* — deterministic MVN seeding. *N18* — `ProcessParameterSets` fills a NaN parameter set for a failed replication instead of leaving a null in a public array consumers index. N15/N16 were not needed by the stages that would have consumed them and stay open upstream.

**Stage 2 — append-only capability.** `RiskMeasureOptions` gating higher moments, VaR/CVaR, threshold probabilities, profiles, and contributions (every flag defaults true, so the byte gates held); the adjusted marginal failure-mode LEC as opt-in `AdjustedExcess`/`AdjustedFail` streams, default off; `EstimateResourceRequirements()` + `ResourceEstimate` replacing four scattered magic caps with one estimator that names the knob and a value that fits; and the dimension caps lifted onto the lazy enumeration. **A web survey of the reference VEGAS implementations (user-directed) found none capping dimension at 20** — GSL, Cuba, and Lepage's own vegas all use 50 bins per axis on a separable grid, and stratification self-limits because `(calls/2)^(1/D)` drops to importance sampling on its own. The engine's cap moved to 50 and a 24-component joint system now runs. Byte-gate movement at this stage was proven JSON-only: a `--dump` mode on PerfHarness let the four new keys be stripped and the previous hash reproduced byte-for-byte.

**Stage 3 — the value-moving batch.** *3a* — LEC probability mass now comes from the N7 acceptance-aware AGK `Recorder` through a new `QuadratureMassLedger`, retiring the v1.0 midpoint-trapezoid re-derivation; `ContributionAccumulator.FinalizeTrapezoid` collapsed into `FinalizeFromLedger` reading the same ledger. Measured through an `RMCTR_LEGACY_MASS` A/B switch against a 4,000,000-point dense reference: **5.664E-007 → 3.996E-011** relative error. *3b* — the parametric hazard and response summarize their posterior into an `UncertaintyAnalysisResults` directly rather than calling `bootstrap.Estimate`, which read as though the posterior were necessarily a bootstrap; goodness-of-fit fields now report NaN rather than zero, which moves an estimated function's canonical hash once. *3c* — **declined after audit**, with the reason recorded in the type: `SystemConvolution` does not migrate onto N8's `ConvolveDiscrete`, because that kernel is pairwise and re-derives its lattice step from its two operands, so an N-way fold re-bins the running result at a changing step and its two-node atom deposit smears once per fold, where the local kernel bins once onto a lattice sized to the summed support and folds by integer shift. *3d* — the last four `Math.Pow(x, 2d)` → `Tools.Sqr`. The A/B scaffold and the whole `ProcessHazardProbabilities` chain are **deleted** (the phase exit gate).

**Three findings worth carrying forward.** (1) **The old `Σ mass = 1` assertion measured nothing** — the midpoint-trapezoid partition telescopes to one for *any* point set, correct or not. It is replaced by a domain-partition witness (compensated Σ weights vs Σ bin widths) and a per-curve fan-out coverage check, either of which fails macroscopically on a real defect. (2) **Normalizing the recorded mass was the wrong fix and was reverted.** The bins covered only the hazard's tabulated support (0.998 wide), and scaling every mass by 1/0.998 spreads the uncovered tail mass *proportionally across the whole curve* — a systematic 0.5% bias — when the missing mass belongs at the tails. `BuildHazardBins` extends its end bins to the integration domain instead. (3) **F2 moved and that was not a leak.** The ledger is 1D-only, but `BuildHazardBins` also feeds `ProbeAnnualFailureProbability`, whose result sets the VEGAS tail-focus γ, so a different probe redistributes every joint sample.

**Two verification allowances widened, each with its derivation recorded rather than a silent re-pin.** The NFIP API is not a pure integral — its second term is the non-failure mass above the hazard threshold, a discontinuous functional resolved only to the spacing of the quadrature nodes straddling the crossing, so it moves with the mass partition independently of the integral's accuracy (worst case 1.32e-4 relative at TOL 50 parametric; allowance 1e-4 → 2e-4). Background invariance across combination methods now holds to the integrator's own tolerance rather than tighter, because the combination method changes the total integrand and therefore the accepted mesh the background stream rides (6.9e-9 measured against a 1e-8 configured tolerance).

**Verified:** build 0 warnings and `validate-code-xml-docs.ps1` green at every commit; fast suite **665/665** (from 651). Byte gates re-pinned once at 3a and confirmed unchanged through 3b–3d: F1 `23a0f30e…` (4.54 GB), F2 `9dbec059…` (16.52 GB), F3 `8c849441…` (8.81 GB), F4 `31bef59f…` (4.63 GB). **All 27 verification families run isolated, 149 tests, all green.** Numerics 1956/1956 on net10.0.

**Caveat on the numerics gate:** the other three target frameworks could not be exercised this session. The numerics working tree carries unrelated uncommitted MCMC work whose untracked test file calls `double.IsFinite`, which does not exist on net481 — so the *test project* fails to build there. The `Numerics` library itself builds clean on all four TFMs, and my commits are scoped to `UncertaintyAnalysisResults` and its tests. The full four-TFM suite should be re-run once that MCMC work lands.

**Next:** the remaining input functions — bivariate hazard, transform, response, and consequence, plus event trees and possibly fault trees. Carried forward from this round: past roughly 15 components the joint method is pure importance sampling, which is correct but resolves tails less sharply per evaluation; the ledger costs allocation (F1 3.88 → 4.54 GB, F3 7.64 → 8.81 GB) because it retains one row per accepted evaluation; and N15/N16 remain open upstream.

## 2026-07-26 - Polish & optimization — PHASE 8.5 STAGE 1 COMPLETE

**Goal:** Execute Stage 1 of the approved Phase 8.5 plan — the provably bit-inert half of the polish round, gated throughout on the F1/F2/F3 results hashes reproducing bit-for-bit.

**User decisions ratified (planning, 2026-07-26):** the numerics repo **is** in scope (full upstream work on `bug-fixes-and-enhancements`); work is **staged bit-inert first, then one deliberate value-moving batch**; the new risk-measure flags **default to everything on**, so today's JSON and every pinned constant are unchanged; the adjusted marginal LEC surfaces as **additive opt-in streams**, default off. Mid-session the user directed that the MVN seeding be fixed by seeding a fresh Mersenne Twister from the component method rather than deferring it, and flagged the CIF caching for a thread-safety review — both done, see below.

**Landed (6 commits `57b67b8`…`a6caae2`, plus numerics `06d3157`):** *1a* — 25 `IsNaN||IsInfinity` chains → `Tools.IsFinite`, six nested clamps → `Tools.Clamp` (incl. the VEGAS integrand and two per-evaluation kernels), `SystemConvolution.Scale`/`Curve.BuildNormalizedView` → `ExtensionMethods.Multiply`/`Divide`; three sites kept local with the reason recorded (`CombineComplement` — the `Tools.Sum/Mean` indicator overloads take `IList`, so array access would become per-element interface dispatch inside the integrand; `Curve`'s weighted moments — Numerics has no weighted API and its kurtosis is excess where v1.0 is plain; `BuildDescendingGrid` — `Tools.Sequence` accumulates its step and derives its length). *1b* — the `TabularHazard` PERT-percentile-Z mean curve off `Statistics.ParallelMean` onto a sequential reduction that also drops a `double[10000]` row copy per ordinate; the branch had **no test at all** and gained one. *1c* — the 2^U combination caches built only for `JointFailures` (the sole reader), `…For(unitCount)` accessors so the frozen layout is reused instead of re-projecting the graph twice per component per realization, and both caches pre-warmed at `SetupSamplers` — closing a latent race where the getters mutated shared state from inside `Parallel.For`. *1d* — the competing-risks pre-processing shared across a deterministic run + the new **F4** fixture. *1e* — the pooled `IndependentExclusive` overload at both joint call sites with caller-owned buffers. *1f* — six byte-identical uncertainty-summary blocks collapsed to `FunctionHelpers.SummarizeEnsembleRow`, and the two automatic-dependency correlation constants into `Core/DependencyMatrix`.

**Two defects found, both pre-existing, both fixed.** (1) **`MultivariateNormal` seeded its Genz quadrature randomizer from the wall clock** (`new MersenneTwister()`), so every dependent competing-risks incidence curve — and every failure probability computed from it — was irreproducible across runs. F4 returned a different results hash every run (`237b694e…`, `a37864ab…`, `603f565e…`). It had hidden because no fixture exercised dependent competing risks and the verification family asserts at k·SE tolerances far wider than the ≈1e-4 Genz error. Fixed upstream (N17: fixed default + a `CompetingRisks.PRNGSeed`) and driven from the component's content seed, deliberately **excluding the realization index** so the run-shared cache cannot make results depend on which thread wins the publish race. Note for future work: the MVN uses a closed bivariate form at D = 2 and only reaches the randomized rule above it, so a two-unit fixture cannot detect a seeding regression. (2) An earlier cut of 1d **shared `EmpiricalDistribution` instances** across realizations; `Interpolater.SearchStart` is mutable state written on every lookup, so that was a data race, and on the non-strict incidence curves a corrupted search start can return a *different ordinate*. The byte gate caught it. Only plain `double[]` is shared now — `OrderedPairedData`'s two-list constructor copies rather than retaining its inputs — and the stratification bins are rebuilt too, since `StratificationBin.Weight` is publicly settable and 200 bins are free beside the integration being skipped.

**One planned item deferred with a measurement:** `Math.Pow(x, 2d)` → `Tools.Sqr` is **not** bit-inert — measured 1,560 differences per 3,000,000 random inputs (~0.05%, 1 ulp). It moves to the Stage 3 re-pin batch rather than riding Stage 1's gate.

**Verified:** `dotnet build` 0 warnings and `validate-code-xml-docs.ps1` green at every commit; fast suite **651/651** (from 648). **F1/F2/F3 results hashes bit-identical at every sub-stage** (`917ff3a5…`/`f5de82ea…`/`f2714852…`), with allocations **F1 4.10 → 3.88 GB, F2 17.93 → 16.38 GB, F3 7.85 → 7.64 GB**; F4 **22.28 s → 5.49 s and 20.36 → 4.77 GB** (≈4×), now reproducible and pinned at `af2348d1…`. Verification families re-run isolated at their gates: `EngineReproducibility` 3/3, `CompetingFailures` 9/9, `JointFailures` 9/9, `SystemRisk` 4/4 — zero pinned constants moved. Numerics **1943/1943 on all four TFMs**.

**Next:** Stage 0 (numerics N12–N16: the `BootstrapAnalysis`/`UncertaintyAnalysisResults` determinism-and-speed rework, lazy exclusive enumeration, AGK recorder pooling, CIF knobs, weighted moments), then Stage 2 (risk-measure flags, adjusted marginal LEC, the resource estimator, lifting the dimension caps) and Stage 3 (the deliberate re-pin: AGK quadrature mass, the `UncertaintyAnalysisResults` migration, `ConvolveDiscrete`, `Tools.Sqr`). Carry-forward: the dependent competing-risks path is now reproducible but still costs a Genz integral per unit per hazard level for a NON-deterministic component — N15's bin-count knob is the lever; and the three `JointConsequenceType` switch copies stay deliberately un-unified (fused per-evaluation kernels).

## 2026-07-25 - Composite hazard, transform, and response — PHASE 9 PARTIAL

**Goal:** Port `CompositeHazard`, `CompositeTransform`, and `CompositeResponse` with their `Weighted*Function` wrappers, unit tests, and verification, using the shipped `CompositeConsequence` as the blueprint. Out of scope by user direction: `RFAHazard`, the `CompositeHazard` parameter-set import, and the roadmap's `CompositeConsequence` → Numerics `CompositeFunction` migration.

**Ratified this session (D1–D6, recorded in the plan and the doctrine page):** hazard/response ship **full v1.0 parity — Mixture and CompetingRisks** (max rule for hazards, weakest link for responses); the mixture is **aleatory only**, so `SamplingDimensions` is 0, there is no branch selector, and a mixture of deterministic children is deterministic — the deliberate divergence from `CompositeConsequence`, justified because a hazard/response realization *is already a distribution* so the mixture folds in losslessly and the Q-V tail defect cannot arise. `CompositeTransform` is **Average only**: Mixture would need per-realization branch selection with no transform analog of the exposure-branch surface, so a mean-only run's LEC tail would diverge from the mean of the full-uncertainty ensemble (the user's call, and correct). New enum `CompositeCombinationType { Mixture, CompetingRisks }` rather than overloading `CompositeFunctionType`, which is pointwise *curve* algebra. `CompositeConsequence` untouched.

**Landed (7 commits):** the enum + three `Composite` discriminator members; `FormatMatrix`/`ParseMatrix` promoted from `SystemComponent` to `SerializationUtilities` (byte-identical text, no hash moves); the three composites + wrappers + `CompositeSupport` (shared correlation-matrix gate, name-based `DependencyType` mapping, posterior-capacity guard, Numerics-distribution guard); factory cases + resolver-carrying cluster overloads; and a **bug fix** — `HazardElement`/`TransformElement`/`ResponseElement` passed a bare method group as their inline factory, so a nested by-reference composite child silently loaded as null (`ConsequenceElement` already threaded the resolver). Improvements over v1.0, each tested: RNG-free percentile sampling (v1.0 derived a `Random` seed from the percentile, colliding percentiles closer than 1e-5); children keep their own interpolation transforms; the lossy all-empirical union-knot collapse not ported; empty composites throw instead of returning `MaxValue`/`MinValue` sentinels; visited-set cycle recursion; and `SetupSampler` rejecting a posterior-indexed child whose capacity is below the requested sample size.

**Verified:** fast suite **648/648** (from 536), doc script green, build 0 warnings. Three verification families run isolated — `CompositeHazardVerification` **11/11**, `CompositeResponseVerification` **8/8**, `CompositeTransformVerification` **6/6** — against report Tables 44–46 (R `mistr`), an index-parity oracle rebuilt from Numerics `BootstrapAnalysis`/`Mixture` at the legacy seeds, and exact probability identities for the competing-risks modes the report does not tabulate.

**Two findings, both pinned rather than papered over.** (1) v1.1 is **~6× closer to the analytic mixture** than two published Table 45 mid-distribution rows: the published AEP 0.5 quantile 21.38 inverts to 0.5044 where v1.1's 21.290 inverts to 0.5007. The report records 0.0% there because v1.0 agreed with `mistr`, so this is an accuracy improvement; the curve test therefore uses the report's own 1% "very good" band rather than a print-rounding tolerance that would encode the published error as the target. (2) **`BootstrapAnalysis.Estimate()` is not bit-reproducible across calls** — its summary assembly uses a parallel, order-nondeterministic reduction, so two `Estimate()` calls with an identical `PRNGSeed` agree numerically (≤ 1e-6 on every sampled quantile) but not bit-for-bit. The posterior is serialized content, so any container folding a child hash has an unstable canonical hash across estimation runs. Upstream, not a composite defect; the reproducibility pins round-trip through XML (what a stored project does) and a dedicated test pins the nondeterminism so it is visible if fixed. Both added to the CLAUDE.md gotchas, alongside a third: `Mixture.CreateEmpiricalCDF()` shifts `InverseCDF` by ~3e-3 of the exceedance probability, so an oracle comparing against a plain `Mixture` must build it too.

**Next:** the engine-level composite oracles — `Test_Composite_Hazard`, `Test_Composite_Response`, the `Test_RiskAnalysis.Test_Composite` mixture-consistency identity, and NFIP TOL 60/65 — put a composite behind the full risk engine rather than exercising the function alone, and convert in a follow-on session. Also still open in Phase 9: `RFAHazard`, the `CompositeHazard` parameter-set import, the `CompositeConsequence` → `CompositeFunction` migration, and an explicit epistemic mixture mode (deferred with the doctrine written; the code is cheap, the ratification and verification are the work). No PerfHarness fixture was added for the composites — `CreateEmpiricalCDF()` runs ~200 bins × K children per sampled realization, which is worth measuring before the engine-level oracles land.

## 2026-07-25 - Numerics.Functions expansion — PHASE 8 IMPLEMENTATION COMPLETE (release + switch pending)

**Goal:** Execute Phase 8 in `C:\GIT\numerics` per SHARED_FUNCTIONS_STRATEGY v1.2 §4 (same approved plan as the Phase 7 entry below), under the session's ratified decisions: N7/N8 delivered upstream only (no engine adoption — its own future re-pin session), N11 implemented in 2.2.0, and **no push / no release / no package switch** — TotalRisk keeps the sibling Debug HintPath until the user pushes and releases 2.2.0 (the remaining exit gate).

**Landed (11 commits on `bug-fixes-and-enhancements`, `b484b21`…`0a16f9d`, unpushed):** a pre-existing net481 break at HEAD fixed first (the Kappa-Four regression's `double.IsFinite` → `Tools.IsFinite`, the user-directed helper). *N1* — `UnivariateFunctionType` (append-only) + `UnivariateFunctionFactory` (`CreateFunction`/`CreateFromXElement`/`GetFunctionType`) + concrete `ToXElement`/static `FromXElement` on `LinearFunction`/`PowerFunction`/`TabularFunction` — the v1.2 amendment holds: nothing joins `IUnivariateFunction` (TotalRisk's `ClampedPowerFunction`/`CompositeUnivariateFunction` implement it; net481 has no DIMs); `ConfidenceLevel` never serializes; `PowerFunction.Minimum` (derived from ξ) never serializes. *N2* — `SegmentedPowerFunction`: BaRatin addition mode with the exact BestFit `[h₁, log₁₀α₁, β₁, …, σ]` layout (posterior `ParameterSet.Values` applies via `SetParameters` directly), log₁₀-space residual matching the BestFit stochastic `Predict`, strict breakpoint ordering + non-negative exponents, geometric-bracket Brent inverse, one-segment `PowerFunction` degeneracy, independently evaluated parity constants. *N3* — `CompositeFunction` + `CompositeFunctionMode`: pointwise weighted average and the single-uniform mixture (bucket select + re-scaled remainder, no internal RNG); weights are the parameters (Σ = 1); a multi-branch mixture reports nondeterministic; children re-validate at evaluation (the composite deliberately does not fold in the latent child-flag quirk where a deterministic 2-arg `LinearFunction` reports invalid until first evaluation). *N4* — `EnsembleFunction`: template + `ParameterSet[]`, pure `Sample(int)`/`Sample(double)` (min(⌊u·N⌋, N−1)) returning fresh clones off an immutable serialized template snapshot — parallel no-shared-mutation pinned. *N5* — `EmpiricalDistribution`/`KernelDensity` XElement round-trips fixed (both previously FAULTED: parameter names with empty scalar vectors made the inherited form throw) — tables/sample/weights/transforms/probability-order now serialize, wired into `UnivariateDistributionFactory` (ascending and exceedance-convention ladders round-trip). *N7* — the acceptance-aware AGK `Recorder`: nodes cached per interval, flushed `(x, w·halfLength, f)` ONLY on acceptance (a call-time hand-off double-counts rejected parents); Σweights = domain width to 1e-12 and Σw·f ≡ `Result` pinned under forced deep subdivision, whole-interval and stratified forms; recorder-off byte-identical. *N8* — `ConvolveDiscrete` (exact atom-aware lattice convolution: moment-preserving two-node deposits, FFT mass vectors, ringing floored + renormalized; joint zero atoms exact; means add exactly) + the opt-in `logSpacedOutput` ladder (the linear pipeline untouched; positive-support guard). *N9* — the Vegas Jacobian unit tests (heavy-tail integrand unbiased at γ ∈ {1,4,10}; Σwgt = volume per 10k-call batch; seeded MT, Sobol off). *N11* — the pooled `IndependentExclusive` overload: caller-owned lists, in-place indicator-row reuse (reference-identity pinned across calls), stale-tail trim, and the truncation FLAG surfacing the previously silent inclusion–exclusion early exit; the allocating overload delegates (parity pinned for full and truncated enumerations); the sibling exclusive kernels follow the same recipe when their call sites go pooled. *N6* — round-trip tests throughout + the new `docs/functions/index.md` guide.

**Verified:** numerics `dotnet build -c Release` 0 warnings at every commit; the full suite **1943/1943 on all four TFMs** (net10.0/net9.0/net8.0/net481; 6–8 min each). TotalRisk revalidated against the refreshed sibling Debug build: fast suite **534/534** Release, doc script green, **PerfHarness F1 byte gate bit-identical** (`917ff3a5…`, allocations 4.10 GB unchanged — the pre-flight had re-baselined F1 against the numerics HEAD before any N-work, so movement would have isolated cleanly; every upstream change is additive and off by default), `EngineReproducibilityVerification` **3/3** isolated. One latent Phase 6.7 test gap surfaced and fixed (TotalRisk commit `a786733`): the Q3 `PathLabel` display field echoes renamed functions inside the ensemble JSON by design, so the metadata bit-identity comparison now strips exactly the two display-label fields — canonical hashes and every numeric byte were proven equal before the fix (scratch diff), the engine untouched.

**Next:** the user pushes `bug-fixes-and-enhancements`, cuts the RMC.Numerics 2.2.0 release (version-bearing files per the numerics release workflow + `dotnet pack … /p:Version=2.2.0 -o ./packages`), then a small follow-up session performs the HintPath→PackageReference switch in the four csprojs (decide pin-vs-float `2.*` then — nuget.org also hosts RMC.Numerics), re-runs the full gates + F1/F2/F3 byte gates + spot families isolated, and retires the CLAUDE.md interim note. Engine adoption of N7 (quadrature-mass LEC), N8 (`SystemConvolution` onto `ConvolveDiscrete`), and N11 (pooled joint-path buffers) remains a separate ratified re-verification session. Phase 9 consumes N2/N3/N4 (composites + RFA + BestFit imports); the NPHF-style FDA importer work consumes the N5 round-trips.

## 2026-07-25 - Doc reconciliation + closed-form functions — PHASE 7 COMPLETE

**Goal:** Execute the approved plan: reconcile the stale phase statuses the user flagged (ROADMAP table rows 6.6/6.7 said "Not started" against their own COMPLETE banners), then Phase 7 — port `LinearTransform`, `PowerTransform`, and `NonparametricHazard` with a lean verification family anchored on the 2024 verification report (user directive), and the user-directed optimization of the nonparametric quantile-uncertainty derivation. Phase 8 (Numerics expansion) follows in this session under its own entry.

**User decisions ratified (planning, 2026-07-25):** Phase 8 delivers N7/N8 inside Numerics only — no engine adoption (the plan documents HOW the weight-aware AGK works: an acceptance-aware ledger, since call-time weight hand-off double-counts rejected parents); **N11 implemented in 2.2.0** (§4 gains the row); Phase 7 verification = yes, lean family anchored on `docs/reports/Verification of the RMC-TotalRisk Software - 2024.docx` Table 38; **no push, no release, no package switch** — TotalRisk keeps the sibling Debug HintPath until the user releases 2.2.0; optimize `NonparametricHazard.UpdateHazardFunction`.

**Landed (5 commits `c5cfaf8`…`bb0089e` + this one):** *Docs* — the XML-doc gate fix (a missing ctor param tag + 11 ambiguous `SetupSamplers` crefs had broken `validate-code-xml-docs.ps1` on the committed tree), ROADMAP rows 6.6/6.7 → Complete, the missing 6.7 verification.md conversion row, SHARED_FUNCTIONS_STRATEGY v1.2 (N11 row scoped into 2.2.0; **N1 amended** — `ToXElement()` joins the concrete classes + a `UnivariateFunctionFactory`, NOT `IUnivariateFunction`: TotalRisk's `ClampedPowerFunction`/`CompositeUnivariateFunction` implement that interface and net481 has no default interface members, so the interface form would be breaking). *B1* — `LinearTransform`/`PowerTransform` as zero-math wrappers (v1.0 defaults/validation verbatim; σ serialized+hashed only while uncertain; `SamplingDimensions = IsUncertain ? 1 : 0` with the deterministic `SampleFunction(int)` branch the full-MC by-index path requires; `PowerFunction.Minimum` never assigned — historically a silent no-op setter, documented + advisory warning; exact analytic `ComputeUncertaintyResults` with the inverse-form CI envelope). *B2* — `NonparametricHazard` (HEC-FDA less-simple method; input table + ERL/EP scalars; derived `TrueUncertainFunction`) with the optimization: closed-form σ repair (quadratic in the log-space scale; Brent retained as guarded fallback), inlined base-e moment maps, evaluate-before-mutate extrapolation (fixing the latent v1.0 frequent-end corruption — inert for 0.999-anchored inputs), inputs-only serialization + deterministic load-time recomputation (v1.0's own load already re-derived before overwriting), and the log-axis negativity derivation gate (`Tools.Log` floors only positives). *B3* — `ClosedFormFunctionsVerification` (4 tests): SF-8 (Figure 16 transcribed: 9 ordinates, ERL 48, EP 1e-4) vs **Table 38 — all 20 published log10 quantile pins green at 5.5e-4** (computed 4.2482/4.7404 vs published 4.248/4.740 at AEP 1e-4); the independent legacy-object-pipeline re-derivation (Brent fixed points ≡ shipped closed forms at 1e-6); transform-chain ensembles (1,000 LHS realizations) vs flat MC oracles at MT(12345) N = 10⁶ — every deviation ≤ 0.5·SE; D = 0 dense-quadrature parity with bit-identical realizations; reliability AFP vs independent knot-semantics oracles at MT(45678) + repeated-run/rename/round-trip bit pins. One faithful-port gate fix surfaced by the family: sampling gates on INPUT validity (v1.0 semantics), never the derived table's strict-dominance flag — the σ repair enforces the 1% bound only, so derived LnNormal ladders legitimately cross deep in the tails.

**Verified:** `dotnet build` 0 warnings + `validate-code-xml-docs.ps1` green at every commit; fast suite **534/534** Debug and Release (500 → 534). The NEW family **4/4 isolated** (~20 s). Dev-repo sweep findings recorded: zero legacy oracle usages of the transforms, NPHF via the FDA importer only — the matrix's "deferred EAD/NFIP scenarios" anchor was moot.

**Next:** Phase 8 in `C:\GIT\numerics` (N1–N9 + N11 per SHARED_FUNCTIONS_STRATEGY v1.2; commits on `bug-fixes-and-enhancements`, NO push), then TotalRisk revalidation against the refreshed sibling Debug build; the 2.2.0 release + HintPath→PackageReference switch stays pending the user's push/release (the remaining Phase 8 exit gate).

## 2026-07-24 - Cascading response end states — PHASE 6.7 COMPLETE

**Goal:** Execute Phase 6.7 per the approved plan: implement the ratified v0.16 cascade design (typed Fail/Non-Fail ports, per-stage branch polarity, end states as projected modes in mutually-exclusive state groups) with a design-first Stage 0 resolving the end-state classification and non-failure-branch semantics the user flagged as needing depth, then land the identity surface, the multi-stage engine, the state-group layer, the verification family, and the doc closure — Q-X closes.

**User decisions ratified (this session):** *Stage 0 (arch doc §7.9)* — **final-polarity classification** (Fail-final terminals are failure states; APF keeps the breach convention — wiring a partial-damage terminal never changes it); **flipped-final-sibling excess pairing** (R2-Fail excess = C_full − C_partial when the sibling is wired, background fallback = v1.0 parity; rides the Q-N construct unchanged); **single-claiming-group scope** (branch-scoped non-failure consequences in one state group per component; the multi-group cross-product complement is the documented lift); Non-Fail-port terminals ARE the direct wiring of non-failure consequences, and continuations are legal on either port. *Planning* — **Q2: duplicate-leaf and prefix-nested wiring stay legal** with the legacy flat combination (advisory warnings; the mass-balance witness reports double counts honestly); **Q3: names + path descriptors** (terminal-first `Name` + append-only `PathLabel` on realization/summary/band trees). *Mid-session directive (pinned by test)* — **knowledge uncertainty samples independently across all functions**, cascade stage responses included; the only coupling constructs are the Q-N consequence pairing and the one-instance-one-quantity rule for a shared response element whose sibling branches must partition on one sampled curve. *Correctness challenge accepted* — the competing gate NARROWED from all-multi-state-groups to else-chain failure states only (∏ of monotone fragilities is monotone, so progression cascades compete fine; the telescoping-union relaxation is documented, not implemented).

**Landed (6 commits `051531b`…this one):** *Stage 1 (the one deliberate hash event)* — `BranchPolarity` (serialized enum, values = port indexes), `ResponseStage.BranchPolarity` resolved-on-write (pre-6.7 payloads load forward as Fail), `ResponseElement.OutputCount` 1 → 2, projection reads exit ports into polarities and stamps runtime-only `ProjectedResponseOrdinals`/`ProjectedTerminalName`, `AddFailureMode` wires polarity ports (bit-identical round-trips), same-port-aware `MultipleConsequences`, the identity-form `ResponseNodes` topology annotation (identity-form ONLY — closing the real gap where one shared response element and two equal-content duplicates hashed identically while computing differently), and the §7.9.1 branch-claim advisories (cascade-active graphs only — every pre-6.7 shape validates silently). *Stage 2* — both Q-X seams replaced: per-stage sampled capture, the polarity-product SRP (single-stage Fail bit-identical), the all-stage `ConsequenceInput` fold (fixing a stage-0 truncation defect), the restrictive `InverseSRP` policy (no engine consumer; polarity products are non-monotone). *Stage 3* — `EndStateGroupLayout` (public: leaf-signature exclusive groups, Q2 standalone ejection, final-polarity classification, sibling pairing, trivial-layout detection); combination caches/MVN/correlation dimension = the combination-unit count; unit-level ME/CCA/Joint/Competing kernels with conditional state distribution (the joint odometer crosses per-unit concatenated (state, branch) entries; Shapley lands on picked states with exact Σ-identities); the claimed-complement mixture (one distribution driving the non-failure scalar, the joint excess baseline, and the exhaustive Background/NonFail/Total recording); mode-scope claimed recording into the state's own NonFail stream; the narrow competing gate + single-claiming-group validation + group-aware guardrails (within-unit ADD, across-unit MULTIPLY). *Q3* — `FailureModeRealization`/`FailureModeResults` gain the stamped `Name` + append-only `PathLabel`; percentile band trees copy from their template. *Docs* — arch doc → v0.18 (§7.9 authored and ratified in-session; Q-X → Resolved), [technical-reference/cascading-end-states.md](technical-reference/cascading-end-states.md), [verification/cascade-end-states.md](verification/cascade-end-states.md), ROADMAP 6.7 landing block, CLAUDE.md matrix/gotchas + AGENTS regen.

**Verified:** `dotnet build` 0 warnings + `validate-code-xml-docs.ps1` green at every stage; fast suite **500/500** Debug and Release (487 → 500). The NEW `CascadeEndStateVerification` family **8/8** isolated: the partial-damage cascade vs its NATURAL MC oracle (MT 12345/45678, N = 10⁶, k·SE k = 4 — natural simulation IS the reference because the conditional claimed-complement is exact under independence; APF ≈ E[p₁p₂] = 0.00714, NOT the initiation 0.0308), the joint/ME/competing across-unit matrix (convention oracles for the latter two; competing carries the documented 1% CIF-discretization floor), the **bit-exact** saturated-stage single-stage equivalence, reliability-mode APF vs the Rao-Blackwellized oracle, additive mean-sum + joint mass-balance system smokes, and the port/polarity reproducibility pins (same-seed and rename+id+XML-round-trip bit-identical; port rewiring moves results). **Inertness proof for every pre-6.7 model:** the deterministic bit-pin (`Test_Deterministic_BitPin_CascadePhases`) held through all stages, and the F1/F2/F3 results byte gates reproduced the Stage-1 post-hash-event values **bit-exactly with the whole state-group layer active**, re-pinning only once for the Q3 JSON labels ([scripts/perf/RESULTS.md](../scripts/perf/RESULTS.md)); allocations moved 4.09 → 4.10 GB (the per-realization layout builds). Phase-close sequential sweep: every verification family re-run isolated one at a time (the hash event moved every seed; statistical families held inside their k·SE tolerances with zero re-captures — `SingleComponentUncertainty`'s anchors are seed-relational and moved with the engine; oracle constants never move).

**Next:** Phase 7 (LinearTransform/PowerTransform/NonparametricHazard), Phase 14 (REST API + MCP — unblocked since Phase 6), or Phase 8 (Numerics expansion + package switch). Carry-forward notes: cascade deferrals live in §7.9.9 (multi-group claimed states; competing else-chains via the telescoping-union analysis; state-level cross-group `ExclusivePCM`; numeric `InverseSRP`); Phase 10's `EventTreeResponse` consumes the landed end-state contract (per-leaf ports ride the 6.7 machinery); a claimed state's mode-scope curves live in its NonFail stream (Fail/Excess empty — final polarity), and the component Background stream is the complement-conditional mixture whenever claimed states exist.

## 2026-07-24 - Risk measures, diagnostics & sensitivity — PHASE 6.6 COMPLETE

**Goal:** Execute the Phase 6.6 plan: close Q-T (the profile hazard selector + recording-time remap), expand the risk-profile catalog with five-stream banding parity, ship per-type consequence thresholds, build the general % contribution diagnostic, add scalar CIs + convergence diagnostics, replace the legacy tornado port with a unified sensitivity engine over objective risk measures, and implement the §5.5.8 seed-stable perturbation mode — everything with unit AND verification coverage.

**User decisions ratified (planning, 2026-07-24):** Joint Max/Min contribution split **consequence-proportional** (probability always equal — the Shapley value of the union game); the profile selector **seed-inert** (serialized append-only, excluded from the identity form; deliberate asymmetry with the hashed `HazardThreshold`); hazard-level sensitivity **profile-axis native** (each realization inverts its own sampled chain); `SensitivityIndex` = **Pearson r² everywhere** (≡ the TR Appendix G regression main-effect index under orthogonal LHS); **unified-only sensitivity API — the ratified scope amendment vs the ROADMAP wording**: no Dictionary-shaped `Sensitivity()` clone, no public `RiskAtHazardLevel` port (the legacy intermediate-quantity bars were collinear; the independent per-function inputs make SI a true variance share); the expanded profile catalog **banded like the existing profiles** with the traditional ascending cumulative plot named honestly (terminal ≡ Fail `MassBalance`, never "APF vs hazard"); sensitivity scoped **by total system OR sub-component** (`componentIndex` = −1/i, the GUI filter maps 1:1); % contribution reports **both bases** — % of APF (consequence-free → full in reliability mode) and % of mean loss; the **SRP profile plots against hazard exceedance probability** (a hazard-axis SRP is ill-posed under mixed transforms; AEP is the universal normalized scale). Joint-path polynomial shortcuts explicitly OUT of scope (recorded as the Phase 8/12 future-performance note in ROADMAP).

**Landed (7 commits `c115f79`…`767bf95`):** *Q-T* — `SystemComponent.ProfileHazardElementId`/`SetProfileHazardElement` (upstream-path validation, runtime-only resolved chain, kitchen-sink hash-inertness pin) with the remap at the recording seam (component profiles/extents, `HazardThresholdProbability`, mode-scope pass-down; single null check when unset → bit-identical). *Profiles* — `Curve.CumulativeFailureProbabilities`/`CumulativeExpectedConsequences` (one ascending pass; terminals ≡ `MassBalance`/`Mean` at 1e-12) + `SystemResponseProbabilities` on its own AEP axis, `[JsonIgnore]` normalized views, five-stream percentile banding restored (the v1.1 Total-only banding was a parity bug vs v1.0) and extended to the new arrays + all additional types. *Thresholds* — `ConsequenceTypeDescriptor.ConsequenceThreshold` (append-only, NaN default) threaded to every measure site. *Contribution* — `RiskContribution` (raw values; shares on read) + `ContributionAccumulator` (separate accumulators, trapezoid/direct finalize mirroring the two mass regimes; nothing on probes/warm-ups) through all four combination kernels (ME/CCA/Competing exclusive marginals; Joint per-pathway Shapley + consequence-proportional splits), the additive-system union APF via the exact O(D²) Poisson-binomial DP (φᵢ = pᵢ·E[1/(1+Kᵢ)], pinned vs 2^D brute force), joint-system per-component accumulators under the mass self-normalization, containers at FM/component/system scope per type (append-only JSON; pre-6.6 payloads load null = "not computed"); math page [technical-reference/risk-contribution.md](technical-reference/risk-contribution.md). *Scalar CIs* — `EnsembleResults.Summary` (`EnsembleSummary`: Mean/Median/Lower/Upper `SystemRiskResults` trees ×every scope/stream/type via explicit accessor catalogs, sequential reductions, `ComputeSummary` recomputable on load) + `ConvergenceDiagnostics` (integrator aggregates + per-headline ensemble SEs; numbers only). *Sensitivity* — `SensitivityMeasure`/`RiskMeasure` enums (runtime-only, order-pinned), `MeasureSensitivity`/`MeasureSensitivityMatrix` (any stored scalar measure across the ensemble — zero re-integration; inputs re-derived bit-exactly from content seeds), `HazardLevelSensitivity` (content-seeded own stream, no-record `ComputeRisk` path — zero kernel duplication, profile-axis inversion, v1.0 five-type formulas), the one shared input walk `SystemComponent.CollectSensitivityInputs` (labels/order/dedup mirror `SetupSamplers`; deterministic functions contribute no column), `RiskFunctionBase.SampledPercentile`, results as unpersisted typed objects. *Seed-stable mode* — `SamplerSeedMap`/`SeedScribe` (per-walk-ordinal capture/apply incl. coupling ordinals + the joint VEGAS seed base), `RiskAnalysis.CapturedSamplerSeeds`/`PinnedSamplerSeeds` (runtime-only, never serialized/hashed), loud shape validation (component-count throw; walk-shape fault), §5.5.8 rewritten from trade-off note to API contract with the documented residuals (integrand-following refinement; the joint integrand is inherently consequence-bearing — the unit tests pin bit-identity via a consequence-blind 1D objective and a hashed-but-integrand-inert joint perturbation). *Docs* — arch doc → v0.17 (Q-T/Q-W RESOLVED, §6.4.1 shared-exposure sketch recorded), ROADMAP 6.6 landing block (UI deferrals: exact-pair excess lists, TRG lines; the joint polynomial-identities future-perf note), verification README + four new results pages.

**Verified:** `dotnet build` 0 warnings + `validate-code-xml-docs.ps1` green at every stage; fast suite **476/476** in Release (433 → 476). Four NEW verification families, run isolated one at a time at their stage gates and again at phase close: `RiskProfileVerification` 5/5 (profile-axis pushforward exact at knots with bit-identical non-profile outputs; threshold knot equivalence; cumulative/SRP profiles vs an independent dense-quadrature oracle; reliability-mode presence; five-stream band restoration), `ContributionVerification` 5/5 (Σ-identities vs `MassBalance`/`Mean` at 1e-12; joint-Additive ≡ marginal FM means; ME/CCA/Competing vs independent quadrature oracles inside the N7 recorded-mass envelope; additive φ vs brute enumeration; joint-system sums vs recorded mass), `ScalarUncertaintyVerification` 2/2 (ensemble-quantile targets on an exactly linear knowledge map; NFIP-style posterior APF CI), `SensitivityVerification` 3/3 (analytic corr(U, Φ⁻¹(U)) = √(3/π) at the Fisher-z bound; rank exactness + inert-input 4/√N null band; independent hazard-level response oracle with affine invariance) — suite **97 → 112**. Regression families re-run isolated at their gates: `NfipAssurance`, `EngineReproducibility` (also after the Stage-7 seed-walk change), `MultiConsequence`, `JointFailures`, `SingleComponentUncertainty` — **zero pinned constants moved** (the contribution accumulators are additive-only side-cars; the remap defaults to a null check; thresholds default NaN; results JSON append-only). Perf harness F1 (one fixture per invocation): stage 1 reproduced the post-6.5 hash exactly (remap bit-inert unset); the session's wall-clock regime was noisy, so the deterministic signals are allocation deltas — +0.26 GB (+7%) for the catalog arrays + 15 extra banded families at stage 2, +0.16 GB for the contribution rows at stage 4, byte-identical allocations at phase close (the stage-5 `Summary` block is grid-scale; stages 6/7 are run-path-inert and this run is their gate); three documented append-only byte-gate re-pins (stages 2, 4, and 5's serialized `EnsembleSummary`) with existing-field equality carried by the append-only load tests ([scripts/perf/RESULTS.md](../scripts/perf/RESULTS.md)).

**Next:** Phase 6.7 (cascading response end states — design ratified v0.16), Phase 7 (LinearTransform/PowerTransform/NonparametricHazard), or Phase 14 (REST API + MCP). Carry-forward notes: the §5.5.8 pin removes seed noise only — the adaptive mesh follows the integrand (1D configurable; the joint integrand is consequence-bearing by construction), so perturbation studies wanting bit-identical probability outputs pair the pin with `RiskIntegrand.TotalProbabilityOfFailure` or integrand-inert edits; `HazardLevelSensitivity` re-runs `SetupSamplers` at its own N (documented; `RunAsync`/`MeasureSensitivity` re-seed defensively); the joint independence-only polynomial identities stay a ratification-gated future note.

## 2026-07-23 - Multi-consequence axis + engine performance + cascade ratification — PHASE 6.5 COMPLETE

**Goal:** Execute the post-Phase-6 gap-audit plan: reconcile roadmap/doc statuses, close the Q-U multi-consequence interim end to end, run the dedicated performance session against the recorded ~10× regression brief, ratify the cascading-response design (added mid-planning by the user), and scope-but-defer the measures/diagnostics/sensitivity work.

**User decisions ratified (planning):** relaxed ensemble integration budget as the DEFAULT (one deliberate re-pin event — which in the end moved zero pins); consequence types **declared at the analysis level** with strict bubble-down validation ("hazard and consequence types are how we ensure valid system configurations"); `ConsequenceThreshold` primary-only this phase; numbering 6.5 now / 6.6 deferred. **Cascade decisions:** typed Fail/Non-Fail output ports on `ResponseElement`; exact mutually-exclusive partition with auto-remainder to background; design ratified now (arch doc v0.16 item 4), implementation = new Phase 6.7; `EventTreeResponse` survives as the compact node (Phase 10 reshaped). **Standing workflow directives:** run verification families and perf fixtures isolated, ONE at a time — never a monolithic suite or a heavy median-of-3 joint fixture; no formal benchmark library now (allocation/GC counters folded into the lean harness; a BenchmarkDotNet micro-suite is scoped into Phase 12).

**Landed (9 commits `f7d0bbc`…`7271412` + this one):** *Docs* — ROADMAP Phases 1–3 markers fixed, N7 collision resolved (release → N10 with N7–N9 rows added), CLAUDE.md Phase-8/9 pointers. *Q-U closure* — `ConsequenceTypeDescriptor` + `RiskAnalysis.AdditionalConsequenceTypes` (append-only child; K = 1 legacy fallback) with strict per-terminal validation (counts/order always; labels/units when both non-blank; blank = wildcard) + a hazard-axis consistency advisory; guardrails to per-type-marginal semantics (mode Π→Σ, component/estimate per-type max — the §6.4.1 erratum); the engine computes EVERY declared type in one pass (per-type Q-N coupling columns the matrix carried since Phase 3, probability structure computed once, primary-driven refinement, secondaries ride the same nodes and are skipped on probes/warm-ups); per-type containers at every scope (`AdditionalCurves` + per-type extents and percentile grids on the realization trees, `ConsequenceResults`/`AdditionalConsequences` + declared-label echo on the summary tree, `Curves.GetCurve(RiskType)`); the new `MultiConsequenceVerification` family (two-type MC oracle at MT(12345) N = 10⁶ verifying both axes of one pass, single-type bit-identity pin, dedicated-primary quadrature parity at 1e-4, cross-model ensemble parity, additive/joint system identities — [verification/multi-consequence.md](verification/multi-consequence.md)). *Performance* — the lean harness (`scripts/perf/PerfHarness`, one fixture per invocation, single rep default, results-JSON SHA-256 byte gate, allocation/GC counters; [scripts/perf/RESULTS.md](../scripts/perf/RESULTS.md)); realization-owned compute workspaces in both sampled kernels (record sites keep fresh adopted lists); single-pass balanced-objective scales sharing one bin build; the joint latent transform in place off a per-run cached Cholesky factor (bit-identical accumulation order); the zero-ulp merge-walk percentile interpolator (`Curve.InterpolateLogLogDescending`, pinned against `OrderedPairedData` bit-for-bit) with transposed assembly loops; profile sort → strictness-gated reverse; the EXACT closed-form CVaR (piecewise power integral of the log-log quantile — more exact than the 1e-8 AGK it retires); `EnsembleTolerance`/`EnsembleMinDepth` options (defaults 1e-4/0 inside ensemble realizations only; mean pass, probes, and mean-only runs stay 1e-8/MinDepth-2). Convolution buffer reuse consciously skipped (~10 allocations per realization, no fixture shows it). *Cascade* — design ratified in arch doc v0.16 (typed ports, partition + auto-remainder, end states = projected modes in mutually-exclusive state groups, partial damage states on Non-Fail continuations); Phase 6.5 carries the resilience hooks only; Q-X annotated with its resolution path; Phases 6.6/6.7 inserted in ROADMAP and Phase 10 reshaped.

**Verified:** `dotnet build` 0 warnings and `validate-code-xml-docs.ps1` green at every commit; fast suite **433/433** (415 → 433; the Release suite runtime itself halved to ~18 s under the relaxed ensemble default); the full 97-test verification suite passed UNCHANGED after the engine rewrite (the commit-4 invariance gate, 21 m 33 s), then every family re-ran green one at a time across the optimization commits. **Zero pinned verification constants moved**: the exact-anchored families pin their discipline in-test with documented rationale (SingleComponentUncertainty at its de facto 1e-8 — its pre-6.5 in-test 1e-6 relaxation never took effect because `UseDefaults` reset the knobs at run time, a latent test-fixture finding; NfipAssurance full rigor for the exact assurance-fraction pin; MeanParity kernel-identity; LhsVarianceReduction at 1e-6 to keep integration noise out of the sampling-variance ratio), while MultiConsequence + EngineReproducibility exercise the relaxed default (~12 s each, down from ~50–70 s). **Performance (Release, fixture F1 = the PROGRESS brief's trivial 1D N = 1000 ensemble): 31.46 s → 3.90 s (8.1×) and 38.97 → 3.67 GB allocated (10.6×); F3 (two-type) 66.50 → 7.81 s (8.5×); F2 (joint) is VEGAS-budget-bound by design.** Byte-gate hashes held bit-for-bit through commits C1–C6 and re-pinned once at C7+C8, exactly as planned. The mean-pass equivalence pin proves declaring a second consequence type leaves the primary axis bit-identical.

**Next:** Phase 6.6 (risk measures, diagnostics & sensitivity — scoped in ROADMAP), Phase 6.7 (cascading response end states — design ratified, implementation phased), or Phase 7/14 as before. Carry-forward notes: the `UseDefaults` run-time reset silently overrides in-test integration knobs — set `UseDefaults = false` whenever a fixture pins integration settings (two legacy fixtures were corrected); N11 (pooled `IndependentExclusive` outputs) joined the Numerics follow-up list; the joint path's remaining per-eval allocations are Numerics-owned (N11) and VEGAS recording (the data itself).

## 2026-07-23 - Verification II: system risk + NFIP assurance — PHASE 6 COMPLETE

**Goal:** Execute Phase 6 in full: convert the legacy `Test_MC_SystemRisk` correlation × aggregation matrix and the `Test_RiskAnalysis` N-element/N-PFM combos, convert NFIP Assurance TOL 50/55/70, add the LHS variance-reduction test, and finalize the tolerance policy — with the assurance conversion extended per the user's mid-session direction: **assurance (API confidence) requires uncertainty**, so the family also verifies the full-uncertainty assurance computation the TR's NFIP appendix and the v1.0 `AssuranceControl` diagnostic define (per-realization API = Fail.TotalProbability + NonFail.HazardThresholdProbability over the knowledge ensemble; assurance = P(API ≤ 0.01)).

**Landed (4 new verification families, 24 tests — suite 67 → 91):** `SystemRiskMatrixVerification` — the 36 legacy methods consolidated to 12 dependency-group tests (2C/2-PFM additive ×4 dependencies at seeds 78910/12345/45678; 2C/1-PFM and 5C/1-PFM ×4 dependencies ×4 rules at seeds 67891/12345; one legacy name mislabeled, ported from the body), each group one oracle pass accumulating every rule (bit-identical to the separate legacy passes), the Independent groups additionally asserted on the deterministic additive-method path (full Phase 5 catalog at 65,536 lattice nodes) and every group×rule on the joint path (4b convention, tail focus off), with all 27 published 2024-report scenarios pinned (tables 77–103). `RiskAnalysisCombosVerification` — 1-comp 3/4-PFM perfectly negative groups (all four rules from the legacy additive draws), 3/4-comp perfectly negative systems (legacy increment-accumulation typo corrected to the per-component excess convention), and the r = −0.25 average scenario the mislabeled `Test_5Element_1PFM_2` body computes; every remaining legacy method's disposition documented (stream-identical Phase 5 duplicates, system-matrix duplicates, inert workbenches, Phase 9 scope). `NfipAssuranceVerification` — TOL 50/55/70 API oracles at MT(45678) N=10⁶ with the legacy short-circuit draw order preserved, an exact composite-Simpson reference with the overtopping crossing split out, engine runs in reliability mode for BOTH hazard types (exact-LP3 `ParametricUnivariateHazard` and a 641-knot normal-z `TabularHazard` tabulation) with the top-of-levee `HazardThreshold` mapped to exact rating knots (stage ordinates are the integers 0–100; flow-space threshold ≡ stage-space for the strictly increasing rating; Q-T remap stays open), Table 104 pinned both columns (oracle vs MC at combined 1M/10M binomial; engine vs v1.0 at 0.5% parity), plus the **assurance ensemble**: a deterministic 300-set injected LP3 posterior (the `Estimate(IList<ParameterSet>)` BestFit surface), realization-for-realization API parity against per-set exact quadrature (the D = 0 posterior walk), ensemble mean, exact assurance-count agreement with a borderline guard, and reproducibility pins (rename + XML round-trip bit-identical — the parametric estimate travels in its serialized results). `LhsVarianceReductionVerification` — 5 replicate pairs at N = 1k: variance ratio ≥ 10 (measured 4.27×10⁴), unbiasedness, the mean-only linearity anchor (measured 5.4e-4 relative), and per-replicate seed sensitivity. Design finding recorded on the results page: the first fixture also sampled the fragility, and its measured ratio ≈ 9.7 is the reciprocal of the statistic's P_F·C interaction share — per-dimension LHS removes main-effect variance only — so the final fixture keeps the fragility deterministic and the statistic linear in the stratified coupling dimension (which also makes a near-one ratio the loud failure signature if the scheme ever stopped reaching the coupling matrix).

**One latent engine gap found and fixed (the Phase 6 reproducibility pin caught it):** the additive system's failure union folded per-component probabilities in DECLARATION order while the convolution and seed base fold in canonical-hash order — component reordering moved the union by 1–2 ulp against the 4b bit-inertness contract. `AggregateAdditiveSystem` now folds the union in canonical order (documented in its remarks); the new 5-component shuffle+rename pin holds bit-identically and the fast suite passed unchanged. Two tolerance discoveries recorded in-test rather than silently widened: (1) the joint mass balance and the additive-combine identity are exact only at D = 2 — above that the documented Numerics `IndependentExclusive` convergence shortcut drifts both by up to its 1e-4 tolerance (observed ~1e-6 at D = 5) — the engine's honest witness of the upstream truncation; (2) system-level report pins carry a 2e-3 relative tabulation allowance (the z-grid bias adds coherently across summed components, and the report's own table 92 background sits ≈ 3.8 of its own SEs from five times its single-component constant — the widest internal spread in tables 77–103). The additive lattice at the default 4,096 nodes quantizes the 5-component support enough to shift a conditional-median probe ~2× its binomial tolerance — the family probes at 65,536 nodes (documented; a resolution property, not engine math).

**Verified:** `dotnet build` 0 warnings; fast suite **415/415** (Debug and Release — the Release gate 53.9 s, Verification correctly excluded); full verification suite **91/91** (67 → 91), wall **20 min 28 s** with TRX-captured console results recorded in the four new `docs/verification/` pages; `validate-code-xml-docs.ps1` green. Worst engine-vs-oracle deviations: additive-path system means ≤ 0.9% (inside 4·SE on the zero-inflated streams), joint-path means ≤ 2.2% (the heavy-σ 5-component perfectly-positive stream, where the engine sits 0.1% from the report's 10M constant), NFIP APIs within 5e-5 relative of exact quadrature (parametric) and 3.4e-4 (tabular), all Table 104 pins within 0.1% of the v1.0 engine column, assurance ensemble realization-parity ≤ 2.5e-6 with exact count agreement at 95.3% assurance.

**Next:** Phase 7 — remaining closed-form functions (`LinearTransform`, `PowerTransform`, `NonparametricHazard`); or Phase 14 (REST API + MCP server) is now unblocked (Phases 1–6 complete). Carry-forward notes: the `IndependentExclusive` truncation now has TWO engine-level witnesses (mass balance + additive identity) feeding the Numerics follow-up list; the NFIP family's exact-quadrature-vs-engine allowances (1e-4 relative deterministic; 2e-4 + 2e-6 per-realization across the sampled ensemble) are the measured envelope for the N7 recorded-mass interim on reliability integrals; TOL 60/65 + engine-level composites remain Phase 9.

**Engine-performance finding recorded for a DEDICATED future session (user directive: do not pre-optimize mid-phase):** the v1.1 full-uncertainty (ensemble) path measured ≈ 54 s per 1,000-realization 1D analysis on a trivial fixture, versus the user's v1.0 reference of ≈ 5 s for a 1,000-realization NFIP run. The structure is port-consistent (`Parallel.For` over realizations, index-owned writes), but three v1.1 cost constants multiply per realization: (1) the risk integral runs the mean-pass discipline unchanged in every realization — 50 stratified bins × G10K21 with `MinDepth = 2` per bin ≈ a 4,200-evaluation floor at tolerance 1e-8, where v1.0 ran Adaptive Simpson over the same 50 bins at ≈ 300 evaluations total at 1e-4 (AGK needs FEWER evaluations per unit accuracy — the floor is the 21-point kernel granularity × forced per-bin depth, a configuration artifact, not the rule); (2) the risk-measure catalog computes eagerly per realization (v1.0 computed lazily on property access) — component + per-failure-mode stream sets, each with a CVaR `AdaptiveGaussKronrod` at a hardcoded 1e-8 over [1e-16, α] on a LINEAR probability axis (≈ 46 bisection levels to reach the deep-tail structure; an exact segment-by-segment integral of the piecewise log-log interpolant would be both exact and O(knots)); (3) percentile post-processing across the ensemble. Candidate remediations for that session: a reduced ensemble-realization integration budget (v1.0 philosophy — ensemble statistics average integration noise; keep the mean pass at full discipline), lazy or optional per-realization measures, and the closed-form CVaR segment integration. Any change moves pinned uncertainty-family verification constants, so the session re-runs those families deliberately.

## 2026-07-23 - Verification I: single-component oracle families — PHASE 5 COMPLETE

**Goal:** Execute Phase 5 per the approved plan: convert the Bucket-1 legacy oracle families to asserted verification tests at N = 10⁶ with the legacy seeds, realize the v0.13 means-versus-tails policy (each ported oracle doubles as the brute-force tail oracle), and add the ratified new-coverage families.

**User decisions ratified (planning):** consolidate by sampling group — one oracle pass per dependency group accumulates all four combination rules (bit-identical to the four separate legacy passes at the same seeds); 53 legacy methods → 27 scenario tests + 5 per-family reproducibility pins, with the results pages mapping every legacy method name. All three new-coverage additions in scope (the two-loop uncertainty family, the engine-only consistency family, and the richer per-family tail asserts). The broken legacy `Test_1_Component_5_PFM_Competing_Positive` body (commented-out MVN, unused Cholesky, shared z(0)) replaced by the correct r = 1 − √ε equicorrelated oracle. Mid-session: fix both engine defects immediately (below).

**Two latent engine defects found by pre-flight verification and fixed before any family landed (commits b287682, b76b782):** (1) the automatic failure-mode dependency matrix was materialized only by the never-consumed `FailureModeMultivariateNormal` getter — v1.0 rebuilt eagerly on every ctor/setter/mode edit, so its compute-time raw reads were always fresh; v1.1's `SampledComponent` captured a null matrix under `PerfectlyNegative`, the joint/common-cause kernels threw into the adaptive integrator's non-reporting catch (`ReportFailure = false`, status never checked), and the run published **all-zero risk with a success status** (competing faulted with an NRE). Fixed at the `SetupSamplers` per-run freeze point (`EnsureDependencyMatrixCurrent`, shared with the getter; no serialization or hash surface moves), and `IntegrateComponent`/`ProbeAnnualFailureProbability` now throw on `IntegrationStatus.Failure` (mirroring the VEGAS guards) so a swallowed integrand exception can never again truncate recorded curves into silent zeros. (2) The common-cause perfectly-positive factor called the Numerics `CommonCauseAdjustment` overload with no matrix; that overload null-checks before dispatching on the dependency, so every CCA + PerfectlyPositive run faulted — surfaced loudly by the new status guard. The factor now passes the materialized matrix (the positive kernel never reads it; the legacy oracle's own three-argument call form). Fast-suite regressions pin both: the `SetupSamplers` materialization, all three dependent methods computing under `PerfectlyNegative` with unions agreeing and sitting strictly above independence, and the perfectly-positive pair agreeing strictly below it.

**Landed (5 commits):** `JointFailuresVerification` (8 dependency groups × 4 rules; 2024 report tables 61–76 pinned — 80 pin asserts), `CompetingFailuresVerification` (8 groups; tables 59–60), `CommonCauseVerification` (8 groups — the `_CCA`/`_CommonCause_Independent` pairs merged, identical streams; tables 55–58), `MutuallyExclusiveVerification` (2 + the Σp > 1 normalization-warning pin), `EadVerification` (exact closed form for mean/σ/VaR/CVaR of the clamped piecewise-linear curve + the ported oracle + two equivalent engine mappings — background and always-fail), `SingleComponentUncertaintyVerification` (a two-loop oracle whose inner integral is exact — curve knots aligned to the hazard grid make the integrands piecewise quadratic, per-interval Simpson exact; ensemble grand means/percentiles, the 90% CI LEC band, LHS/MC scheme agreement, the Q-N coupling pin with decoupled counter-pin — engine ensemble σ 6.99 vs coupled 6.80 vs decoupled 12.56 — and the `ParametricResponse` posterior-injection realization-for-realization anchor, recording the mean-only Jensen gap the uncertainty tech note warns about), `CombinationMethodConsistencyVerification` (the tech note's §6.1 union invariance — joint ≡ common-cause to eight digits, competing within its CIF discretization; the Fréchet bound ordering with the D = 2 negative ≡ exclusive degeneracy; background invariance across nine configurations; `RiskIntegrand` invariance across all seven objectives; reliability parity per method). Per-scenario assert catalog: five summary means + union/complement identity + σ(Fail/Total) + conditional mean + assurance + two data-driven LEC probes + VaR/CVaR, every tolerance documented as k·SE (k = 4) with its derivation in the test XML docs. Docs: seven new `docs/verification/` pages + README rows, `verification.md` conversion-order row, ROADMAP Phase 5 banner, CLAUDE.md matrix (TabularResponse, ParametricResponse, NonFailResponse, TabularConsequence, ResponseStage/FailureMode, SystemComponent → V) + AGENTS.md regen.

**Verified:** `dotnet build` 0 warnings; fast suite **415/415** (413 → 415, Debug and Release); verification **67/67** (25 → 67), full-suite wall 7 min 5 s; `validate-code-xml-docs.ps1` green at every commit boundary. Worst engine-vs-oracle deviation across the 50 converted combination scenarios: ~1.2% on a mean and ~1.6% on a dispersion measure — every assert within its 4·SE, dominated by the oracle's own Monte Carlo error on the zero-inflated streams; all 22 published report scenarios pinned. Tolerance discoveries recorded in-test rather than silently widened: the exceedance probes read the output LEC at resolution 1000 (the default 200-point ladder's knee interpolation ≈ 1.5% exceeds the binomial 4·SE — a presentation property, not engine math), and the EAD closed-form comparisons carry the documented N7 recorded-mass interim (≈ 3e-6 on the mean, ≈ 1.1e-5 on σ, asserted at 1e-5/5e-5). Physics checks visible in the numbers: the additive failure mean is dependency-invariant; negative dependence raises the union and positive lowers it; the mixed-sign 5×5 matrix exposes the v1.0-preserved PCM approximation at ≈ 0.3% (inside MC tolerance).

**Next:** Phase 6 — Verification II: `Test_MC_SystemRisk` (2-comp/2-PFM and 5-comp/1-PFM across the correlation × aggregation matrix — port from bodies, names are mislabeled), the `Test_RiskAnalysis` N-element combos, NFIP assurance TOL 50/55/70, and the LHS variance-reduction test. Carry-forward notes: the competing CIF discretization (~0.3% at 5 PFM) and the PCM mixed-sign observation feed the Phase 6 tolerance derivations; the uncertainty family's exact-inner-integral technique (grid-aligned knots + Simpson) generalizes to the system oracles.

## 2026-07-23 - Multi-component system risk + reliability mode — PHASES 4b & 4c COMPLETE

**Goal:** Execute Phases 4b (multi-dimensional system risk: additive strict-independence convolution + joint VEGAS with tail focus and real combination enumeration) and 4c (reliability mode) per the staged plan approved at the Phase 4 session.

**Implementation corrections ratified in-session (arch doc → v0.15; the v0.13 design intent was unimplementable in two places):** (1) **the additive convolution is an exact lattice, not `EmpiricalDistribution.Convolve`** — `Convolve` samples continuous PDFs on a uniform grid, so the zero-inflation atom (a CDF jump) has no representation and the per-stage PDF-normalize/regrid chain cannot hold the 1e-6 mean gate; the engine bins each component's exact recorded pairs onto a shared lattice with a moment-preserving two-node split plus the zero atom and convolves by `Fourier.FFT` (public kernel `Analyses/SystemConvolution`; mean parity exact *by construction*; N8 extended to an atom-aware overload). (2) **the automatic γ target comes from a deterministic per-component AGK failure-probability probe, not the warm-up harvest** — `ConfigureForRareEvents` raises `NumberOfBins`, whose setter reallocates the importance grid (γ must be configured *before* the warm-up), and a γ = 1 MC warm-up cannot observe the rare failure probabilities the target needs; the probe (`AFP_i = ∫P_F,i dp` on the mean sample — the one call site where the integral's return value is the product) is deterministic, ~10³ evaluations per component per run, and lets the warm-up itself adapt under the active γ. Also ratified: five self-normalized recording passes (per-pass Σwgt = volume only in expectation → exhaustive budget exactly one); v1.0 system-state stream probabilities restored post-convolution (Fail/Excess = independent union, NonFail = complement); canonical-hash component order for both the convolution association and the VEGAS seed base (additive reorder+rename bit-inert; joint rename bit-inert, reorder statistically-equivalent-only — inherent to a coupled driving stream, documented); `UseSobolSequence = false` + seeded MersenneTwister (the Numerics default Sobol would void the content-seed contract); a system-level combination-width guardrail (warn 4,096 / error 65,536 entries per evaluation via internal `SystemComponent.EstimateRecordedFailureEntries`); the legacy `tPF` double-increment not carried forward; reliability results ride the existing containers (AFP = Fail.TotalProbability at mode/component/system levels — no parallel results tree) with the effective integrand forced to `TotalProbabilityOfFailure` and the mass-balance warning suppressed in reliability mode.

**Landed:** `RiskAnalysis` multi-D paths — additive aggregation (`AggregateAdditiveSystem`/`ConvolveSystemStream` over the new `SystemConvolution` lattice kernel) and the joint VEGAS path (`PrepareJointSystem` run-scoped MVN/combination caches/probe, `IntegrateJointSystem` with the engine-level 2^D enumeration crossing the components' recorded pathway/branch entry lists per the activated `ComponentRiskOutput` lists, background/fail/excess/total/non-fail recording in the v1.0 point layout, warm-up + five recording passes, mass self-normalization, γ configuration per `VegasTailFocusMode`); the real strict-independence/dimension-limit/correlation-matrix/entry-width validation catalog replacing the 4b/4c stage gates; `EffectiveIntegrand` reliability forcing; the mode-aware validation chain (`Validate(RiskAnalysisMode)` overloads on `FailureMode`, `ConsequenceElement`, `ComponentGraph`, `SystemComponent` — reliability relaxes exactly the consequence-content requirements; consequence elements stay structural terminals); `Curve.CollectRecordedPairs` (public) + internal mass-scaling helpers (`Curve`/`Curves`/`ComponentRealization`); the latent `IAnalysis.AnalysisCompleted` cref fixed. Docs: arch doc v0.15 status block + §7.8 banner; tech-refs updated (lattice construction; probe heuristic; driving stream/passes/Jacobian audit); ROADMAP 4/4b/4c complete + N8/N9 rewordings; new `docs/verification/system-risk.md` results page + README index row; CLAUDE.md matrix rows (`RiskAnalysis` P/T/V incl. 4b/4c, new `SystemConvolution` row) + three new/updated Numerics gotchas (Convolve atoms, Vegas Sobol default + NumberOfBins grid reset, IndependentExclusive truncation); AGENTS.md regenerated.

**Verified:** `dotnet build` 0 warnings; fast suite **413/413** (396 → 413: convolution exact-enumeration/mean/variance tests, additive aggregate/percentile tests, joint smoke/bit-identity/γ-consistency/full-MC tests, reliability AFP-vs-dense-reference/union/forcing tests, mode-aware relaxation tests); verification **25/25** (~71 s; 21 → 25): new `SystemRiskVerification` — additive system LEC vs a 10⁶-event brute-force oracle (mean 0.13%, σ 0.10%, union 0.23%, tail exceedances ≤ 0.55%, CVaR 0.06% — all "very good", every deviation ≤ ~1.2·SE), joint correlated (ρ = 0.6) vs its oracle (mean 0.21%, union 0.29%, tail ≤ 0.5%; the additive-combine identity exact to 1e-9 — system mean ≡ Σ component means within the run; Total budget 1 + 4.5e-14), the γ-audit green (γ ∈ {1, 4, auto} unbiased — the N9 empirical gate before enabling γ > 1), and the reproducibility pins (additive shuffle+rename bit-identical with component results following content; joint rename bit-identical); `validate-code-xml-docs.ps1` green. Physics check on the recorded numbers: ρ = 0.6 *lowers* the failure union (0.1987 vs 0.2052 independent) while *fattening* the deep tail (P(L>700): 0.00887 vs 0.00382).

**Next:** Phase 5 — Verification I (single-component oracle families: `Test_MC_JointFailures` 2- and 5-PFM across the dependency × combination matrix, `Test_MC_CompetingFailures`, `Test_MC_CommonCause`, `Test_MC_MutuallyExclusive`, `Test_EAD`) per the D-4 means-vs-tails policy; pull `LinearTransform`/`PowerTransform` forward from Phase 7 only if a converted scenario needs them.

## 2026-07-22 - Risk engine core (1D) — PHASE 4 COMPLETE

**Goal:** Execute Phase 4 per the rewritten roadmap: the Phase-3 sampling deferrals, the `Results` JSON-first containers, the `Analyses` foundation, and `RiskAnalysis.RunAsync` (1D single-component; AGK; exact LEC; `RiskIntegrand`; mixture exposure), with the stage's unit and verification coverage.

**User decisions ratified (session start + mid-session):** plan scope = full 4 + 4b + 4c staged (4b/4c next sessions); **Q-V = YES** (branch enumeration in mean-only AND full-MC; `CompositeConsequence.SamplingDimensions` → 0; standalone per-realization mixture surface preserved bit-for-bit via an internal selector matrix — the P/T/V verification constants did not move); **Q-N resolved** (TR-manual shared draw → the failure mode's N×max(K,1) coupling matrix; consequences excluded from the sampler walk); **Q-T deferred** (`ProfileHazardFunction` + legacy `Sensitivity()` → risk-diagnostics sessions); **multi-stage response composition deferred** (users will want event-tree semantics — engine gate + placeholder seam, new Q-X); **Q-U interim** (primary-consequence-only recording). Arch doc → **v0.14** (Q-V/Q-N Resolved, Q-X added, §5.5.3 options recipe extended, §5.8.7/§7.3 errata, `SystemRiskType` naming settled: enum `SystemRiskType { AdditiveRiskMethod, JointRiskMethod }`, property `SystemRiskMethod`).

**Landed:** `RiskIntegrand`/`VegasTailFocusMode`/`SystemRiskType` enums; `SeedHelpers.ToPositiveSeed` public; `"UseDefaults"` strip rule; `IConsequenceFunction.SampleExposureBranches()` ×2 + `CountExposureBranches()` (base defaults + composite overrides); the full `Results` namespace (`RiskPoint`, `Curve` — exact LEC, two-pass weighted central moments with the defective-curve zero atom, hybrid tail/bulk output thinning, VaR→0 fix, CVaR via AGK with explicit caps — `Curves`, `RiskComputeFlags`, realization + summary trees, `EnsembleResults`/`SystemRealization` JSON + GZip via internal `ResultsJson`); the sampled machinery (`SampledFailureMode` branch-aware with the Q-N pairing, `SampledComponent` with all four `FailureModeMethod` combination rules incl. joint branch cross-products and 200-bin CIF pre-processing, `ComponentRiskOutput` with the activated per-pathway lists); `FailureMode`/`SystemComponent` `SetupSamplers`/`Sample` (single-owner reference-identity dedup, frozen projection snapshot, Q-W 64/1024 guardrails); `IAnalysis`/`AnalysisBase`/`AnalysisRunCompletedEventArgs` (BestFit mirror + linked external cancellation); `RiskAnalysisOptions` (full v1.0 surface + v1.1 fields, effective `SetIntegrationDefaults` with component-count scaling, canonical hash); `RiskAnalysis` (content-seeded walk, mean-only + parallel full-MC with index-owned writes and sequential reductions, all seven `RiskIntegrand` objectives with discontinuity bin injection, percentile post-processing reading the Total LEC directly, stage gates for 4b/4c/event-tree pinned by tests).

**Verified:** `dotnet build` 0 warnings; fast suite **396/396** (317 → 396); verification **21/21** (14 → 21; ~65 s): `EngineReproducibilityVerification` (repeated-run/metadata/round-trip bit-identity of results JSON; compute-edit counter-pin), `SingleComponentMeanParityVerification` (five means + AFP vs a `MersenneTwister(12345)` N=10⁶ oracle over shared tables, 4·SE), `ExactLecTailVerification` (σ/exceedances/VaR/CVaR vs a new brute-force oracle, seeds 12345/45678/78910, incl. the flattened-composite counter-pin); `validate-code-xml-docs.ps1` green. Engine-vs-dense-trapezoid mean parity (0.1%), decomposition identities, and Q-V mean-parity + σ-inflation pinned in the fast suite. Two implementation discoveries hardened during the session: the LEC output thinning became a hybrid ladder (pure log-exceedance targets under-resolved the flat bulk → ~4% log-log interpolation overshoot, caught by the tail oracle), and the competing-risks CIF path rebuilds strict-invalid Numerics curves as non-strict (fragility plateaus are legitimate; Numerics follow-up noted).

**Next:** Phase 4b (multi-D system risk: `SystemConvolution` zero-inflated FFT additive + Vegas tail-focus joint with real combination enumeration) per the approved staged plan; then Phase 4c (reliability mode). The γ-audit test gates enabling γ > 1 (Jacobian-in-weight already confirmed at `Vegas.cs:466-472`).

## 2026-07-21 - Phase 4 risk-computation port corrections (docs only)

**Goal:** Retarget the Phase 4 engine port away from a verbatim v1.0 reproduction. A read of the legacy compute (`RiskAnalysis.vb`, `Curve.vb`, `SampledComponent.vb`, `ComponentRiskOutput.vb`) against current `C:\GIT\Numerics` found six correctable defects, and the ROADMAP still instructed "port the integration constants exactly." Documentation-only change: no library code touched.

**User decisions ratified:** (D-1) the new `RiskIntegrand` enum selects the **adaptive refinement objective only** — all five risk-type LECs and every risk measure are always produced; default `MeanTotalRisk`. (D-2) the **Additive risk method is redefined to assume strict independence** between components, so FFT convolution applies exactly; the hazard correlation matrix is joint-method only. (D-3) Phase 4 splits into **4 (engine core, 1D) / 4b (multi-D system risk) / 4c (reliability mode)**. (D-4) verification: **means** stay v1.0-parity (algebraically unchanged, the free regression gate); **σ / VaR / CVaR / F-N tails** are validated against **new brute-force Monte Carlo oracles**, not v1.0 parity.

**Landed (docs):** the six items, specified so the next session implements without re-deriving anything — (1) `AdaptiveGaussKronrod` (G10K21) replaces `AdaptiveSimpsonsRule` at both 1D call sites; (2) exact LEC construction (drop the 200-bin log10 midpoint histogram; sort `(mass, consequence)` pairs, accumulate exactly, thin for output only), weighted-Welford moments (v1.0 raw-power-sum forms cancel), and the full risk-measure catalog with the VaR-at-α>P and Total-percentile-reconstruction fixes; (3) additive → strict-independence FFT convolution via zero-inflated `EmpiricalDistribution.Convolve` (zero-inflation = exact 2^D combination enumeration; v1.0 built no system LEC at all); (4) joint → Vegas power transform exposed with a warm-up-derived γ heuristic, multi-pass recording, real combination enumeration via the now-live `ComponentRiskOutput` per-pathway lists, and the Jacobian-in-`wgt` audit; (5) the `RiskIntegrand` enum (7 members incl. the CVaR-family `TailConditionalRisk`, with discontinuous-integrand bin-boundary injection); (6) mean-only mixture exposure via a new `SampleExposureBranches()` on the consequence contract (mixture weights as exposure probabilities, so σ/tails become correct while the mean is unchanged). New Numerics items N7–N9 (Phase 8), open questions Q-V/Q-W. Files: ROADMAP (Phase 4/4b/4c split + Phase 8 + Phase 5 policy), arch doc **v0.13** (§6.4.1, §7.3, §7.7, §7.8 new/amended; §4/§3 enums; Q-V/Q-W), two new technical-reference docs (`risk-integration.md`, `loss-exceedance-curves.md`), `verification.md` (means-vs-tails policy), `references.md` ([15]–[18]), CLAUDE.md + AGENTS.md (enums row, 1D-integration guidance, three new Numerics gotchas).

**Verified:** documentation-only — `validate-code-xml-docs.ps1` green; `dotnet test -c Release` **317/317** (unchanged, proving no code drift); AGENTS.md regenerated from CLAUDE.md; internal links from all new/edited docs resolve (8 pre-existing broken links to legacy Dev-repo paths untouched). Web search grounded the tail-measure members (CVaR/expected shortfall; Basel III FRTB 97.5% ES).

**Next:** execute Phase 4 (engine core, 1D) per the rewritten roadmap section — ratify Q-V (full-MC mixture enumeration) at session start before building the sampled machinery.

## 2026-07-21 - Consequence cluster completion + verification workflow bootstrap (pre-Phase-4)

**Goal:** Land the two consequence types Phase 4 needs — `CompositeConsequence` (day/night exposure via mixture consequences) and a literature-grounded `ParametricConsequence` — and stand up the verification workflow (first `RMC.TotalRisk.Verification` families + the `docs/verification/` results folder) before the risk engine, per the approved plan. A web literature review validated the spec's parametric form: power growth is the best-fit to observed depth-damage data (Environmental Processes 2014, R² 0.68–0.78), damage-initiation thresholds matter (npj Nat Hazards 2024), and bounded damage matches JRC/NFIP-calibrated practice — citations [9]–[14] in `docs/references.md`.

**User decisions ratified:** no Numerics changes this session (combine math + the exact `ClampedPowerFunction` adapter live in the model library; Phase 9 migrates onto Numerics `CompositeFunction` when N3 ships); the new type is **`ParametricConsequence`** (cluster-consistent; arch doc/roadmap wording updated — typeTag permanence made this a pre-landing decision); scope is the consequence cluster only (hazard/response composites stay Phase 9); **the BestFit `CompositeAnalysis` pattern is the composite's structural reference** (user-directed review) — matched on live referenced children in an observable weighted collection and reference-only stored serialization ((Id, Name, Weight) per entry — a store never duplicates child content), deliberately improved where BestFit has warts (complete self-written `FunctionReference` markers vs caller-patched Name attributes; id-authoritative resolution vs name-based; unresolvable references keep their weighted entries and surface through `Validate()` vs silent entry loss; shadow-set collection reconciliation vs the `Reset` leak), and deliberately divergent on nesting (allowed with a cycle-guard error — Mixture-over-Additive is a real modeling need) and weight rules (Σw = 1, no zero-inflation semantics). Two behavior ratifications: composite child label mismatches are Warnings (the Phase 3 downgrade precedent), and the legacy composite-level transforms are dropped (children own their interpolation transforms). Arch doc amended to **v0.12** (naming; composite hash recipe gains `CompositeFunctionType` — the spec table omitted the mode, which is compute-relevant; Q-I composite half + Q-J resolved: declared entry order is semantic, identical-content siblings draw independently via `HashCombine(seed, childHash, ordinal)`).

**Landed (4 commits):** `ParametricConsequence` — `C(h) = clamp(α·max(h−h₀,0)^β, 0, U)` per §6.4, two log-space coefficient sigmas sampled from independent LHS dimensions (D = 2), sigmas serialized only while uncertain (recipe-literal conditional hashing), nominal-median mean curve with the heavy-tail divergence documented and warned (σ_β > 0 with U = ∞ has an unbounded mean above the unit offset), content-seeded deterministic uncertainty summary over a log-spaced grid; plus the public exact `ClampedPowerFunction` adapter (threshold-zero and saturation semantics Numerics `PowerFunction` lacks). `FunctionEntry` seam extraction — the `WriteFunctionEntry`/`ReadFunctionEntry` marker mechanics moved out of `RiskElementBase` into one shared internal implementation; resolver-aware `RiskFunctionFactory` overloads; `RiskFunctionBase.CanonicalHash()` virtual. `CompositeConsequence` + `WeightedConsequenceFunction` + `CompositeUnivariateFunction` — legacy three-mode semantics preserved (Mixture picks one child per realization by cumulative weight; the mean path has no mixture branch), the legacy percentile-reseeded `Random` replaced by RNG-free single-uniform composition sampling (bucket select + rescaled remainder) and per-child content-derived sampler seeds, pointwise-exact combine (no union-grid snapshot — exact under parametric children), projected-identity hashing (mode/count/effective-weights/child-hashes; serialization-mode- and child-metadata-inert; Additive weight edits inert), and a reentrancy guard so cyclic composite graphs degrade to a reportable validation error instead of a stack overflow (found by the circular-reference test crashing the first run). Improve-on-port calls documented in remarks: Mixture over multiple reachable branches reports `IsDeterministic = false`; empty composites throw from `MinHazard()`/`MaxHazard()` instead of returning the legacy poison sentinels; `ContainsComposite` gained a visited set (the legacy recursion never terminates on cycles not involving the target).

**Verified:** `dotnet build` 0 warnings; `dotnet test -c Release` **317/317 passed** (265 → 317); `validate-code-xml-docs.ps1` green at every commit boundary. **First two verification families landed and green — `dotnet test src/RMC.TotalRisk.Verification` 14/14 passed (~3 s wall)**: `CompositeConsequenceVerification` reproduces the 2024 report's §Composite Consequence Function at N = 10⁶ (Additive vs exact N(130, √30); Average vs exact N(57, √6.65) — the σ = 2.58 match is the independent-pooling check; Mixture vs exact moments (σ = 43.30, ~17× Average) and vs the Numerics `Mixture` inverse-CDF analytic oracle with an independent `MersenneTwister(12345)` MC cross-check, plus the report's published 10M constants pinned at a widened tolerance covering the report's own sampling error — its q05 of 8.04 sits ≈ 0.025 from the analytic 8.0652); `ParametricConsequenceVerification` anchors the greenfield family on closed forms (deterministic algebra at 1e-12; exact lognormal with σ_α only; the two-sigma unit-offset lognormal proving the sampler dimensions don't leak; a capped two-sigma configuration vs an independent MC oracle). Worst percent difference against exact values: **0.035%** (all "very good"). Reproducibility pinned in both families: same seed → bit-identical, metadata edits → bit-identical, compute edits move the stream. Results documented with the run's actual numbers in **`docs/verification/`** (README + per-family pages — the living counterpart of the 2024 Word report).

**Next:** Phase 4 as scheduled — the Phase 3 deferrals (`SampledComponent`/`SampledFailureMode`, `ComponentRiskOutput`, JSON-first results containers), then `Analyses` + `RiskAnalysisOptions` + `RiskAnalysis.RunAsync`, inheriting the verification workflow established here (Phase 4 engine reproducibility regressions get their own family + results page). **Hand-off flag for the Phase 4 sampler orchestration:** a child function referenced by both a composite and a graph element gets `SetupSampler` called twice (last call wins and the streams correlate) — the engine's `SetupSamplers` walk must define single-owner seeding (composite recursion wins, or dedup via `GetReferencedFunctions`).

## 2026-07-20 - Layer boundary seams — PHASE 3.5 COMPLETE

**Goal:** Review `Systems.Components` and `Components.Graph` against the real consumer shapes now that `RMC.TotalRisk.UI` (SQLite projects, `IElement` wrappers holding an inner `IRiskFunction`, a WPF DAG control) and the App layer are next, using `C:\GIT\rmc-bestfit` (model → UI → App) as the in-house precedent. The review found one structural problem and four smaller seams; everything else — Guid element identity, dual Id+Name connection references, `RiskElementResolver`, construct-then-resolve deserialization, clone-map re-linking, the name authority, `GetAvailableHazardSources` — was already the right shape and was left alone.

**The structural problem:** elements owned and inlined their functions, so `SystemComponent.ToXElement()` embedded a full copy of every function. Once a `TabularHazard` is a stored item in its own right, it would exist twice and the embedded copy would win on load, silently discarding edits made in the function's own editor. BestFit avoids exactly this: `UnivariateAnalysis.ToXElement()` serializes config only and documents that it excludes the underlying model and results, which arrive through `new UnivariateAnalysis(dist, xElement, mcmcResults, …)`.

**User decisions ratified:** dual-mode serialization with inline as the unchanged default (headless callers, oracles, and the API must keep working standalone); input functions are stored items referenced by Guid, while **components are owned by their `RiskAnalysis` and are not independently creatable in the UI/App**; `Clone()` stays deep and isolated (no sharing mode); own phase before Phase 4. Arch doc amended to **v0.11** with a new normative **§8 Layer boundaries & consumer contract**.

**Landed:** `IRiskFunction.Id` + `AssignNewId` implemented once in `RiskFunctionBase` (with new `WriteIdentityAttributes`/`ReadIdentityAttributes` helpers all seven concrete functions now route through, so future types inherit the id automatically); `"Id"` appended to the `CanonicalizationRules` audited strip list. `RiskSerializationMode { SelfContained, ByReference }` + additive `ToXElement(mode)` overloads on `IRiskElement`, `ComponentGraph`, `SystemComponent`; `IRiskFunctionResolver`/`RiskFunctionResolver` mirroring `RiskElementResolver`'s policy exactly (ids authoritative and loud, names lenient and reported), threaded through `RiskElementFactory` and every deserializing ctor. Wrapped-function change propagation: elements re-raise their functions' notifications, and `ComponentGraph` now forwards element changes (it previously consumed them for cache invalidation and stopped there — caught by a test, not by reading). `ConsequenceElement.Functions` is an `ObservableCollection<IConsequenceFunction>` whose owner reconciles per-item subscriptions on every membership change, following the BestFit `CompositeAnalysis.Analyses` pattern. Authoring surface for the DAG control: `RiskElementFactory.CreateForFunction`/`Create(RiskElementType)`, `IRiskElement.TryAssignFunction` (reports a cluster mismatch rather than throwing), `GetReferencedFunctions()` on graph and component, and validation that distinguishes an unresolved reference from an unassigned function.

**Verified:** `dotnet build` 0 warnings; `dotnet test -c Release` **265/265 passed** (248 → 265); `validate-code-xml-docs.ps1` green. Headline pin: **a component's canonical hash is byte-identical across both serialization modes** — the mode is persistence only, so results can never depend on how a project was saved. Also pinned: a by-reference round-trip re-attaches the *same* function instances (`Assert.AreSame`), which is the property an embedded copy silently breaks; stale function ids throw and unresolved names are reported by name; the id is hash-inert (two identical-content functions with different ids hash equal, and the existing `AssertStrippedAttributesInert` sweep covers the new strip entry across every registered type); and a model-only end-to-end test builds, validates, projects, and hashes a system with no store, no resolver, and no consuming layer in the call path. Every pre-existing test passed untouched — the regression gate for "headless callers are unaffected".

**Collection wiring (closed within the session, at the user's direction):** the first cut left `Functions` a `List<T>` with `AddFunction`/`RemoveFunction` as the notification-safe path, which meant a bound editor mutating the list in place would silently desynchronize. Corrected to an `ObservableCollection<T>` per the BestFit `CompositeAnalysis.Analyses` precedent, with `AddFunction`/`RemoveFunction` removed as redundant. One deliberate departure from that precedent: subscriptions reconcile against a **shadow set** rather than the event's `OldItems`/`NewItems`, because `Clear()` raises a Reset carrying no removed items — BestFit's handler would leak a subscription there. The reconcile also makes a duplicated entry subscribe once. Five tests pin it: in-place Add/Replace/Remove/Clear each notify exactly once; an in-place add forwards the function's own edits; every removal path (including `Clear`) detaches; re-assignment detaches the previous entries; a duplicate notifies once and survives partial removal.

**Next:** Phase 4 — the Phase 3 deferrals (`SampledComponent`/`SampledFailureMode`, `ComponentRiskOutput`, `SetupSamplers`/`Sample`, JSON-first containers into `RMC.TotalRisk.Results`), then `Analyses` + `RiskAnalysisOptions` (carrying `RiskAnalysisMode`) + `RiskAnalysis.RunAsync`, with `RiskAnalysis.ToXElement()` serializing options only per §8.

## 2026-07-20 - Namespace reorganization for the public package

**Goal:** Make the library's public shape intuitive ahead of the repo going public, using the sibling `Hydrologics` library (`C:\GIT\Hydrologics`) as the model. Three problems were being locked in by publication: the vestigial `Models` segment (everything in a model library is a model); four `Support` folders that declared their *parent's* namespace, violating the "folders mirror namespaces" rule in five places and scattering interfaces, enums, and bases with no single place to look; and no type discriminators to branch on. User decisions ratified: the system model gets its own root (`Systems` > `Systems.Components` > `Systems.Components.Graph`) because risk analysis is over a *system* of components — the `Basin`/`Basin.Elements` vs `Simulation` split in Hydrologics; flat singular function clusters with the `Univariate` sub-namespace flattened away; discriminator enums as runtime-only properties; results in their own top-level namespace. Arch doc amended to **v0.10**.

**Landed:** `Core` (kernel utilities + `RiskFunctionBase`), `Core.Enums` (every enum, one per file), `Core.Interfaces` (every interface), `RiskFunctions` + `.Hazards`/`.Transforms`/`.Responses`/`.Consequences`, `Systems.Components` + `.Graph`; `Analyses` and `Results` reserved for Phase 4. New `Core.Enums` discriminators `HazardFunctionType`/`TransformFunctionType`/`ResponseFunctionType`/`ConsequenceFunctionType`/`RiskElementType`, surfaced as `FunctionType`/`ElementType` on the cluster contracts (the Hydrologics `LossMethodType MethodType` pattern) and **never serialized** — the `ToXElement()` element name stays the serialization and canonical-hash discriminator, so the enums add no hash surface. `RiskAnalysisMode { Risk, Reliability }` withdraws the planned `ReliabilityAnalysis` sibling: reliability is a mode of the one analysis so the `ComponentGraph` traversal has a single implementation. Guard script gained a `RMC.TotalRisk.Models` namespace ban so the retired layout cannot creep back.

**Plural-segment rule (v0.10), learned the hard way:** `RMC.TotalRisk.System` would shadow the BCL `System` namespace from inside every `RMC.TotalRisk.*` namespace (CS0234) and break consumers writing `using RMC.TotalRisk;` (CS0104) — caught in review before it was written. A singular `RiskFunctions.Transform` segment shadowing `Numerics.Data.Transform` was **not** caught in review; the compiler found it (16 × CS0118) mid-refactor, which is why all five segments are plural: `Systems`, `Hazards`, `Transforms`, `Responses`, `Consequences`. Type names stay singular.

**Verified:** `dotnet build` 0 warnings; `dotnet test -c Release` **247/247 passed** (240 → 247; 3 new test classes for the discriminators); `validate-code-xml-docs.ps1` green. **The hash contract is the acceptance criterion and it held:** no type was renamed and no serialized attribute changed, so every canonical hash, seed stream, and XML round-trip is bit-identical — the pre-existing hash-invariance, kitchen-sink, and factory round-trip suites passed unchanged, and new tests assert no `FunctionType`/`ElementType` attribute reaches any serialized form.

**Next:** Phase 4 as scheduled — the Phase 3 deferrals (`SampledComponent`/`SampledFailureMode`, `ComponentRiskOutput`, `SetupSamplers`/`Sample`, JSON-first results containers into `RMC.TotalRisk.Results`), then `Analyses` + `RiskAnalysisOptions` (now carrying `RiskAnalysisMode`) + `RiskAnalysis.RunAsync`.

## 2026-07-20 - Risk components + structured component graph — PHASE 3 COMPLETE

**Goal:** Execute the approved Phase 3 plan: formalize the risk setup as a structured DAG **within** each `SystemComponent`, per the user's session mandate. User decisions ratified: v0.8's "topology decoupling" was about the DAG.dll dependency (which stays UI-only — DAG.dll/DAGControls remain the visualization layer for both TotalRisk and Hydrologics); a `SystemComponent` already contained the implicit DAG, so v1.1 formalizes topology *inside* the component while the analysis stays a flat list of ≤ ~20 non-interacting components; element vocabulary **`IRiskElement`**/`RiskElementBase` + `*Element` concretes (Hydrologics `IBasinElement` mirror; the v0.8 element purge narrows to the WPF `IElement` wrapper lingo); container named **`ComponentGraph`** (chosen over ComponentDiagram — "Diagram" is the UI's picture, `RiskDiagram` reserved for UI/App controls — and over ComponentDAG — .NET casing, names the constraint not the role, collides with DAG.dll); results containers + `Sampled*`/`SetupSamplers` **deferred to Phase 4** (Q-N designed with the compute loop). New capabilities ratified: response chains (downstream response applies to both branches — engine semantics Phase 4), ordered multi-type consequences (index 0 = primary for integration; positional fail/non-fail pairing), structural label-free hazard binding (consequences may consume any upstream hazard, e.g. peak flow computed upstream of a stage response), bivariate output/input ports reserved now (functions Phase 11, zero serialized-shape breaks). Arch doc amended to **v0.9** (nine-point entry; Q-G resolved full-matrix row-major CorrelationMatrix-mode-only; Q-I failure-mode half resolved as declared-terminal-order; Q-N amended per-pair; new Q-T ProfileHazardFunction-with-results, Q-U MultipleConsequences engine semantics).

**Landed:** `Models/Support`: `RiskFunctionFactory` (closed reconstruction switch; graph callers skip unknowns, compute chains throw — no silent content loss). `Models/RiskAnalysis/Components`: enums `FailureModeMethod`/`DependencyType`/`JointConsequenceType`/`RiskType`/`HazardDimension` (values pinned by tests — serialized contract); `ResponseStage` (`T* (R T*)* C` grammar unit); `FailureMode` (staged shape + trailing `ResponseToConsequence` + ordered `ConsequenceFunctions`; v1.0 members as views over stage 0; `ConsequenceHazardPosition` resolved-on-write so default and explicit-equal hash identically; self-contained inline serialization; label mismatches downgraded to warnings — labels are unhashed metadata and can never gate or rewire compute); `SystemComponent` (owns `ComponentGraph`; `HazardFunction` view creates the root; `FailureModes` = fresh deterministic projection in declared terminal order with the response-free path canonicalized to stage form; `AddFailureMode` chain expansion — bit-identical hash round-trip for failure chains; identity-vs-persistence split: `CanonicalHash()` hashes options + hazard + projected FM XML, never element-graph XML; `AssignOccurrenceIndices` §5.5.4 reference impl; v1.0 MVN constants + dependency coercion + combination caches re-keyed on failure-path count; G17 correlation-matrix round-trip — v1.0's parse discarded every value, so its format never round-tripped). `Models/RiskAnalysis/Graph`: `IRiskElement`/`RiskElementBase` (Guid Id + `AssignNewId`, v1.0 `LeftPosition`/`TopPosition` names — zero `ModelRules` appends this session), `RiskConnection` (consumer-stored typed inputs; `{kind}ElementId/{kind}Element/{kind}Port` dual serialization; kinds `Source`/`SecondarySource`/`HazardSource`), the four concrete elements (functions owned inline; `NonFailResponse` never wrapped — a non-failure path is a path with no response element), `RiskElementFactory`/`RiskElementResolver` (Id-authoritative loud, name-fallback lenient), `HazardSourceOption`, and `ComponentGraph` (name authority + `TryRenameElement`/`GetUniqueName`, Kahn sort with cycle detection, derived fan-out, root-first upstream paths, `GetAvailableHazardSources` discoverability reused by binding validation, the structural validation catalog incl. ≤ 1 response-free path and positional consequence alignment, construct-then-resolve deserialization with unknown-element skip, clone-map re-linking with shared Ids).

**Verified:** `dotnet build` 0 warnings; `dotnet test -c Release` **240/240 passed** (113 → 240; 19 new test classes); `validate-code-xml-docs.ps1` green. Flagship pins: element renames/`AssignNewId`/canvas moves/description edits are hash-inert and independently built equal-content components hash identically (the v1 canvas-position seed bug is structurally unreachable); the levee acceptance scenario (flow hazard → rating → stage response → consequence bound to raw peak flow) projects binding position 0; occurrence indices are declared-order-stable and metadata-inert; expansion→projection round-trips bit-identically.

**Next:** Phase 4 — the deferrals first (`SampledComponent`/`SampledFailureMode` with Q-N per-pair shared-draw coupling, `ComponentRiskOutput`, `SetupSamplers`/`Sample`, JSON-first results containers), then `Analyses` + `RiskAnalysisOptions` + `RiskAnalysis.RunAsync` (content-based seeds + occurrence indices; mean-only and full-MC paths) + the reliability mode. Q-T (ProfileHazardFunction) and Q-U (MultipleConsequences engine semantics) resolve there.

## 2026-07-20 - Core input functions — PHASE 2 COMPLETE

**Goal:** Land the Phase 2 core input-function surface (same session as Phase 1, per the approved plan). Mid-session user directives ratified: **improve-on-port** — preserve the v1.0 API surface but actively fix/enhance flawed v1.0 method bodies (recorded in CLAUDE.md's amended porting rule + memory); **report-grounded documentation** — technical-reference pages derive from the RMC-TR-2022-XX tech report and 2024 verification report in `docs/reports/`, with API code blocks kept in lock-step with the library.

**Landed:** Cluster contracts on the Phase 1 kernel (`IHazardFunction`/`IUnivariateHazardFunction`/`HazardFunctionBase`/`UnivariateHazardBase`/`FunctionUncertainty`, `ITransformFunction`/`TransformFunctionBase`, `IResponseFunction`/`ResponseFunctionBase`, `IConsequenceFunction`/`ConsequenceFunctionBase`). Seven concrete types with v1.0 surfaces preserved: `TabularHazard` (three uncertainty modes incl. the 200-quantile/10,000-curve expected-probability mean assembly; `NoUncertaintyFunction` typo fixed), `ParametricUnivariateHazard` (bootstrap `Estimate()` + NEW `Estimate(IList<ParameterSet>)` posterior injection for BestFit imports via the UI layer), `TabularTransform`, `TabularResponse` (legacy `IsMonotonic` algorithm), `ParametricResponse` (non-exceedance ordinates, no inversion; injection overload), `NonFailResponse` (instantiable sentinel, type-test identification), `TabularConsequence` (`AllowNegativeYValues=false` clamp + both legacy warnings). Every function implements `ComputeUncertaintyResults(ciWidth)` (shared `TabularUncertainty` exact-percentile + `ParametricPosterior` posterior-summary helpers) — the app-layer visualization math now lives in the model. Improve-on-port fixes (each documented in XML remarks + tested): `ForceMonotonic` rewritten scale-aware (v1.0's absolute-epsilon nudge was unrepresentable at magnitudes ≥ 1 and swallowed by the tolerance-equal ordinate setter); invalid/un-estimated sampling throws instead of returning null; bootstrap failures propagate; parametric bounds cache keyed per meanOnly flag (v1.0 shared one cache); race-free full-posterior bounds scans (v1.0 raced in `Parallel.For`); percentile→index lookup clamped at p=1.0. Technical reference: four report-grounded family pages (`hazard-functions.md`, `transform-functions.md`, `response-functions.md`, `consequence-functions.md`) + refreshed index. Tests: 7 new test classes + 6 kitchen-sink registry entries.

**Verified:** `dotnet build` 0 warnings; `dotnet test -c Release` **113/113 passed**; `validate-code-xml-docs.ps1` green (the guard also caught and we reworded a doc-comment literal that matched the forbidden BestFit-reference pattern).

**Next:** Phase 3 — risk components (`SystemComponent`, `FailureMode`, `Sampled*`, legacy-named enums) + JSON-first results-container redesign. Q-N (fail/non-fail shared-draw coupling) must be preserved in the sampler design; occurrence-index scheme per arch doc §5.5.4.

## 2026-07-20 - Roadmap reorder + model kernel foundation — PHASE 1 COMPLETE

**Goal:** Execute the approved Phase 1+2 session plan: reorder the roadmap around the Dam Screening Tool (tabular functions + mean-only compute + REST API first), then land the Phase 1 model kernel. User decisions ratified this session: v1.0 API preservation on the input-function surface (`NoUncertaintyFunction` typo fixed; legacy enum names on standalone files); analysis-layer growth foundation (`RiskAnalysisOptions` extraction, `ReliabilityAnalysis` in Phase 4, future `CostBenefitAnalysis` over `List<RiskAnalysis>`); risk results go System.Text.Json (BinaryFormatter BLOBs not ported; v1.0 projects re-run); **model/UI vocabulary separation** — no "element" lingo and **no root `IModel` abstraction** in the model lib (BestFit's `IModel` serves polymorphic calibration; TotalRisk's engine consumes functions by role — kernel is `IRiskFunction`); posterior injection `Estimate(IList<ParameterSet>)` on parametric types (BestFit imports via UI layer, no `RMC.BestFit.dll`); `ComputeUncertaintyResults(ciWidth)` on every function (app code-behind visualization math moves into the model; tabular = exact percentile pulls, no simulation).

**Landed:** Docs restructure commit: ROADMAP.md rewritten (Phases 1–6 minimal-surface-first, 7–13 backfill, 14 REST API + MCP from the confirmed `RMC.BestFit.Api` template — single ASP.NET Core project, `ModelContextProtocol.AspNetCore` stateless HTTP at `/mcp`); arch doc amended to v0.8 (eight-point status entry + §3/§5.5/§5.8/§7.5/§9/Appendix A–B edits); verification.md re-pointed (families now Phases 5–6/9–11); CLAUDE.md namespace map/matrix/serialization contract/future enhancements; AGENTS.md regenerated. Phase 1 commit: `Models/Support` kernel — `IRiskFunction` (INPC + Name/Description + axis labels + IsDeterministic + SamplingDimensions + SetupSampler + ComputeUncertaintyResults + Validate + ToXElement + CanonicalHash), `RiskFunctionBase` (INPC scaffolding, hash pipeline, N×D percentile sampler with LHS/LHS-median/MC schemes and a positive-seed fold guarding the `LatinHypercube` wall-clock fallback), `CanonicalContentHasher` + `CanonicalizationRules.ModelRules` (13-entry audited strip list; adapted from Hydrologics), `SeedHelpers` (`HashCombine`, `IndependentUniform`), `ByteArrayComparer`, `SamplingScheme`, `SerializationUtilities` (G17/invariant + permissive readers), `FunctionHelpers.ForceMonotonic` (exact v1.0 port). `BuildInfo` + smoke tests retired (real content landed). Tests: 8 new test classes incl. the kitchen-sink hash-invariance registry (`HashInvariance` delegate-based asserter + per-type registry) that Phase 2+ extends per concrete type.

**Verified:** `dotnet build` 0 warnings; `dotnet test -c Release` 51/51 passed; `validate-code-xml-docs.ps1` green. Porting discovery pinned by test: `ForceMonotonic`'s one-epsilon nudge writes through the `OrderedPairedData` indexer whose tolerance-based `Ordinate` equality discards tolerance-equal assignments — genuine crossings repair, exact ties survive (identical to v1.0 against the same Numerics semantics).

**Next:** Phase 2 (same session if capacity allows): cluster contracts (`IHazardFunction`/`ITransformFunction`/`IResponseFunction`/`IConsequenceFunction` + bases + `FunctionUncertainty`), then TabularHazard, ParametricUnivariateHazard (+injection), TabularTransform, TabularResponse, ParametricResponse (+injection), NonFailResponse, TabularConsequence with per-type tests and kitchen-sink registrations.

## 2026-07-20 - Repo bootstrap — PHASE 0 COMPLETE

**Goal:** Stand up the official `USACE-RMC/RMC-TotalRisk` repo as the v1.1 development home with the full Hydrologics-style process machinery, per the approved plan. User decisions this session: remove the local UI/App skeleton projects (re-scaffold at their future phases); adopt the no-per-file-header rule (USACE notice moved to `LICENSE`, Authors in XML remarks — reverses arch-doc §5.6, now amended as v0.7); develop on a long-lived **`v1.1-development`** branch (`main` stays the v1.0-era public face); roadmap details the model-lib phases with UI/App/API as future placeholders. Carried decisions from 2026-07-19 (Dev-repo session): shared function math in Numerics, model lib references Numerics ONLY (no RMC.BestFit — posteriors imported as Numerics artifacts), canonical hashing = XML canonicalization, HintPath now → RMC.Numerics package at 2.2.0.

**Landed:** Git reconcile (`git init` + fetch of the official repo; local VS scaffold cruft, `.slnx`, and empty UI/App projects removed; `Example Projects/` → `examples/`). Root `RMC-TotalRisk.sln` (Core/Tests/Verification solution folders; Verification has no Release `Build.0` line). Three projects scaffolded: `src/RMC.TotalRisk` (net10.0, Version 1.1.0, interim Numerics HintPath, `Models/BuildInfo.cs` smoke seed), `src/RMC.TotalRisk.Tests` (MSTest.Sdk/3.6.4, linked `MSTestSettings.cs`, 2 smoke tests), `src/RMC.TotalRisk.Verification` (MSTest.Sdk/3.6.4, assembly-level `TestCategory("Verification")`). Support files: `global.json` (SDK 10.0.100/latestFeature/allowPrerelease), `.gitignore`, `Directory.Build.props` (opt-in `EnforceXmlDocumentation`), `Directory.Packages.props` (CPM: coverlet 6.0.4, RMC.Numerics 2.* declared for the future switch), `NuGet.config` (local feed first), `LICENSE` (USACE notice/conditions/disclaimer). Process machinery: `CLAUDE.md` (TotalRisk rule set: headless constraints, Numerics-only red line, contracts, verification workflow, Ported Types Matrix, session/git workflow), `AGENTS.md` generated via `scripts/sync-agents-md.py`, `scripts/validate-code-xml-docs.ps1` (doc coverage + namespace/culture guards + forbidden-reference guards: RMC.BestFit, System.Windows, SQLite, legacy flat `TotalRisk`). Docs tree: `docs/index.md`, `docs/ROADMAP.md` (phases 0–13 + future placeholders), `docs/verification.md` (oracle-conversion pattern + k·SE tolerance policy, 1M realizations), `docs/references.md` (seeded [1]–[8]), `docs/technical-reference/README.md`, and the normative specs imported to `docs/requirements/` (`MODEL_LIBRARY_ARCHITECTURE.md` amended to v0.7 — authoritative-home banner + §5.6 reversal; `SHARED_FUNCTIONS_STRATEGY.md` with home banner). README gained a Development section; `examples/README.md` refreshed.

**Verified:** `dotnet build` 0 warnings / 0 errors; `dotnet test -c Release` 2/2 passed (Verification correctly not built — no `bin/Release`); `validate-code-xml-docs.ps1` green; `sync-agents-md.py` idempotent (identical checksum on re-run). Work committed on `v1.1-development`; nothing pushed.

**Next:** Phase 1 — model support layer (`Models/Support`: `IModelElement`, `ModelElementBase`, `CanonicalContentHasher` + `CanonicalizationRules` adapted from Hydrologics, `SeedHelpers`, `ByteArrayComparer`, `SampledModelElement` + `SamplingScheme`, serialization utilities, `FunctionHelpers.ForceMonotonic`) with the kitchen-sink hash-invariance test scaffold. Reminder for that session: read this file + `docs/ROADMAP.md` first; porting sources live in `C:\GIT\RMC-TotalRisk-Dev`.
