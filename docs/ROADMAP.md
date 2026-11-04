# RMC-TotalRisk v1.1 Roadmap

> Phased development of the v1.1 model library, tests, and verification suite. **One phase per working session.** Every phase exits with: a zero-warning build, the fast test suite green (`dotnet test -c Release`), `validate-code-xml-docs.ps1` green, the Ported Types Matrix in the repository guidance updated, `docs/PROGRESS.md` updated. **Every ported compute type ships with unit tests in `RMC.TotalRisk.Tests`, and gains verification coverage in `RMC.TotalRisk.Verification` by the phase that converts its legacy oracle scenarios.**
>
> **Ordering driver (2026-07-20):** the model library and REST API are needed by the web-based **Dam Screening Tool**, which uses only the tabular input functions and mean-only risk compute. Phases 1–6 therefore deliver the minimal function surface → risk components → engine → verification, before the remaining function options are backfilled (7–13). The REST API + MCP server is Phase 14; it depends only on Phases 1–6 and can be executed any time after Phase 6.

Porting sources in order of authority: (1) the partial C# port `C:\GIT\RMC-TotalRisk-Dev\RMC-TotalRisk\RMC.TotalRisk.IO\Project\Elements\`; (2) the legacy VB engine `...\RMC.TotalRisk\`; (3) the `Test_TotalRisk` Monte Carlo oracles (verification only); (4) the normative specs in [requirements/](requirements/) — where the spec deliberately departs from legacy (content-based seeding, LHS sampling, no-BestFit imports, JSON results), the spec wins.

**v1.0 API preservation (ratified 2026-07-20):** the input-function domain surface is preserved verbatim from v1.0 (property names/types/defaults, `SampleFunction` overload shapes and returns, `Min/Max*` shapes, the `Estimate()` lifecycle, legacy enum names) plus the ratified sampler additions. The analysis layer instead adopts a growth foundation (`RiskAnalysisOptions` extraction, a `RiskAnalysisMode` reliability option, future `CostBenefitAnalysis`); the future UI layer maps v1.0 projects onto the new analysis API on import. Risk results are the sanctioned exception: v1.0 BinaryFormatter BLOBs are replaced by redesigned System.Text.Json containers, and v1.0 projects re-run their analyses in v1.1.

| Phase | Scope | Status |
|---|---|---|
| 0 | Repo bootstrap: structure, projects, process machinery, docs | Complete (2026-07-20) |
| 1 | Model kernel foundation: `IRiskFunction`/`RiskFunctionBase`, hashing, seeding, sampling, serialization support | Complete (2026-07-20) |
| 2 | Core input functions: tabular hazard/transform/response/consequence + parametric hazard/response + non-fail response | Complete (2026-07-20) |
| 3 | Risk components + the structured component graph (results containers moved to Phase 4) | Complete (2026-07-20) |
| 3.5 | Layer boundary seams: function `Id`, serialization modes, function resolver, change propagation | Complete (2026-07-20) |
| 4 | Analysis foundation + RiskAnalysis engine core (1D): AGK integrator, exact LEC + risk measures, `RiskIntegrand`, mixture-exposure mean-only | Complete (2026-07-22) |
| 4b | Multi-dimensional system risk: additive lattice convolution (strict independence), joint Vegas tail focus + real combination enumeration | Complete (2026-07-23) |
| 4c | `RiskAnalysisMode.Reliability` | Complete (2026-07-23) |
| 5 | Verification I — single-component oracle families | Complete (2026-07-23) |
| 6 | Verification II — system risk + NFIP assurance | Complete (2026-07-23) |
| 6.5 | Multi-consequence axis (Q-U closure) + engine performance + cascade design ratification | Complete (2026-07-23) |
| 6.6 | Risk measures, diagnostics & sensitivity | Complete (2026-07-24) |
| 6.7 | Cascading response end states (the event tree in the risk diagram) | Complete (2026-07-24) |
| 7 | Remaining closed-form functions: linear/power transforms, parametric consequence, nonparametric hazard | Complete (2026-07-25) |
| 8 | Numerics.Functions expansion (numerics repo) + RMC.Numerics 2.2.0 package switch | Implementation complete (2026-07-25); 2.2.0 release + package switch pending user push |
| 8.5 | Polish & optimization: Numerics helper adoption, determinism fixes, dimension-cap removal, optional measures, adjusted marginal LEC, N7 adoption | Complete (2026-07-26) |
| 8.6 | Numerical safety remediation: lazy dependent failure-mode enumeration, probability boundary clipping, mass/LEC endpoint correction | Complete (2026-07-27) |
| 9 | Composites + RFA hazard + weighted wrappers + BestFit composite imports | Partial (2026-07-25): hazard/response/transform composites P/T/V; RFA hazard + parameter-set imports remain |
| 10A | Event-tree response + common tree/reference/manipulation foundation | Complete (2026-07-28) |
| 10B | Static fault-tree response with exact repeated-event evaluation | ✅ Complete (2026-07-31) |
| 11 | Bivariate risk analysis | ✅ Complete (2026-08-07) — sessions S1–S7 landed against [requirements/BIVARIATE_RISK_DESIGN.md](requirements/BIVARIATE_RISK_DESIGN.md) |
| 11B | External imports + LifeSim (BestFit bivariate/tabular/transform posteriors, RFA hazard, LifeSim) | Not started (re-sliced out of Phase 11, 2026-08-03) |
| 12 | Hardening: coverage gate, Linux check, examples, getting-started | Not started (the ≥90% unit-coverage gate script already exists and passes) |
| 13 | Release prep — `v1.1.0-alpha` tag | Not started |
| 14 | REST API + MCP server (`RMC.TotalRisk.Api`) | **14A complete (2026-09-04)** — the stateless round-trip compute surface (REST + MCP + tests + `docs/api.md`); 14B (store-backed resources, full-uncertainty mapping, auth/containers) deferred |

---

## Phase 0 — Repo bootstrap

**Scope:** Reconcile the local folder with the official GitHub repo on branch `v1.1-development`; restructure to `docs/`, `examples/`, `src/`; scaffold `RMC.TotalRisk` + `RMC.TotalRisk.Tests` + `RMC.TotalRisk.Verification` (MSTest.Sdk/3.6.4, Verification excluded from Release); port the development-process machinery (the repository guidance, validation scripts, Directory.Build/Packages.props, NuGet.config, global.json, LICENSE); seed the docs tree; import the architecture + shared-functions specs into `requirements/`.

**Exit criteria:** solution builds 0-warning; 2 smoke tests pass in Release; Verification produces no Release output; doc script green; docs tree live; work committed on `v1.1-development`. **Complete 2026-07-20.**

## Phase 1 — Model kernel foundation — **COMPLETE (2026-07-20)**

**Scope:** `Models/Support` — the model library's domain-named kernel (no "element" vocabulary; that is UI-layer lingo from the wpf-framework `IElement` world, and no root `IModel` abstraction; rationale in [requirements/MODEL_LIBRARY_ARCHITECTURE.md](requirements/MODEL_LIBRARY_ARCHITECTURE.md) v0.8):

- `IRiskFunction` — THE kernel contract every input function implements: `INotifyPropertyChanged`, `Name`/`Description` (identity metadata, never hashed), `SpecifiedHazard`/`HazardUnit` axis labels, `IsDeterministic`, `SamplingDimensions`, `SetupSampler(sampleSize, seed, scheme)`, `ComputeUncertaintyResults(confidenceIntervalWidth)`, `Validate()`, `ToXElement()`, `CanonicalHash()`.
- `RiskFunctionBase` — the one shared implementation base: INPC scaffolding, label backing, `CanonicalHash()` pipeline, and the per-function N×D percentile sampler machinery (LHS default, MC legacy, LHS-median).
- `CanonicalContentHasher` + `CanonicalizationRules` (`ModelRules` audited strip list) — SHA-256 canonical content hashing over `ToXElement()`, adapted from Hydrologics `src/Hydrologics/Core/`.
- `SeedHelpers` (`HashCombine`, `IndependentUniform`), `ByteArrayComparer`, `SamplingScheme` enum, `SerializationUtilities` (G17/InvariantCulture helpers), `FunctionHelpers.ForceMonotonic`.

**Unit tests:** hash canonicalization (strip rules; rename/reorder invariance kitchen-sink scaffold that later phases extend), `HashCombine` determinism, sampler matrix shapes for LHS/MC schemes, serialization helpers (±0/NaN/∞/denormal, culture safety), INPC raises.

**Exit criteria:** all support types P/T; the kitchen-sink invariance test runs green and is extendable per cluster.

## Phase 2 — Core input functions — **COMPLETE (2026-07-20)**

**Scope:** the minimal function surface the Dam Screening Tool and the engine phases need, with the v1.0 domain surface preserved verbatim:

- Cluster contracts on the Phase 1 kernel: `IHazardFunction`/`IUnivariateHazardFunction`/`HazardFunctionBase`/`UnivariateHazardBase`, `ITransformFunction`/`TransformFunctionBase`, `IResponseFunction`/`ResponseFunctionBase`, `IConsequenceFunction`/`ConsequenceFunctionBase`, `FunctionUncertainty` enum (standalone file, legacy name).
- **`TabularHazard`** — None/Hazard/Probability uncertainty modes, three `UncertainOrderedPairedData` tables (`NoUncertaintyFunction` — spelling fixed from the v1.0 typo, `HazardUncertainFunction`, `ProbabilityUncertainFunction`), mode-switched mean-curve assembly and percentile sampling, `ForceMonotonic` on invalid samples.
- **`ParametricUnivariateHazard`** (renamed from `ParametricHazard`) — `ParentDistribution` + bootstrap posterior via `Estimate()` (v1.0 lifecycle), **plus the posterior-injection overload `Estimate(IList<ParameterSet>)`** so the future UI importer passes BestFit posteriors (UnivariateAnalysis/Bulletin17C/PointProcess) as already-parsed Numerics artifacts.
- **`TabularTransform`** — single uncertain table over Numerics `TabularFunction`; co-monotonic `ConfidenceLevel` sampling.
- **`TabularResponse`** — co-monotonic `CurveSample` fragility sampling, `IsMonotonic()` legacy algorithm.
- **`ParametricResponse`** — LnNormal default, non-exceedance ordinates, bootstrap + the same `Estimate(IList<ParameterSet>)` injection overload.
- **`NonFailResponse`** — the engine's non-failure branch; instantiable type (no singleton), type-test identification.
- **`TabularConsequence`** — uncertain table, `AllowNegativeYValues = false` clamp.
- **Uncertainty-results contract:** every function implements `ComputeUncertaintyResults(confidenceIntervalWidth)` → Numerics `UncertaintyAnalysisResults`. Tabular types evaluate **exact co-monotonic percentile curves** (deterministic — no simulation); parametric types surface their stored bootstrap/imported posterior. This replaces the v1.0 app-layer code-behind visualization math and is the same method the Phase 4 uncertainty options and Phase 14 API consume.

**Unit tests:** ctor/default matrices (every preserved v1.0 default asserted), validation matrices, XElement round-trips, hash-invariance registrations, `SampleFunction` known-point checks, sampler smoke, Min/Max bounds, `Estimate` bootstrap vs injection, `ComputeUncertaintyResults` exactness.

**Exit criteria:** all seven concrete types + contracts P/T with hash-invariance coverage.

## Phase 3 — Risk components + the structured component graph — **COMPLETE (2026-07-20)**

*(Re-scoped at session start, ratified by the user: the risk setup is formalized as a structured DAG **within** each `SystemComponent`; results containers and the sampled machinery move to Phase 4. Arch doc v0.9 records the amendment.)*

**Scope (landed):** `Models/RiskAnalysis/Components`: `SystemComponent`, `FailureMode` (concrete classes — no interfaces; v1.0 option surface preserved incl. ctor defaults, `FailureModeDependency`/`CorrelationMatrix double[,]` with Cholesky validation, MVN builds with `1−√εmach` / `−1/(D−1)+√εmach` off-diagonals, CommonCause/MutuallyExclusive dependency coercion), `ResponseStage` (v1.1 response chains — grammar `T* (R T*)* C`; v1.0 members survive as views over stage 0), ordered multi-type `ConsequenceFunctions` (index 0 = primary for integration; positional fail/non-fail pairing), and the legacy-named enums `DependencyType`, `FailureModeMethod`, `JointConsequenceType`, `RiskType` + new `HazardDimension` (standalone files; `SystemRiskType` stays Phase 4/Analyses). Occurrence-index assignment (`AssignOccurrenceIndices`, arch doc §5.5.4). Name-based `FromXElement(XElement, IProject)` resolution replaced by self-contained inline child serialization (`RiskFunctionFactory`).

`Models/RiskAnalysis/Graph` — the formal component topology (arch doc v0.9): `IRiskElement`/`RiskElementBase`, `HazardElement` (single root) / `TransformElement` / `ResponseElement` / `ConsequenceElement`, `RiskConnection` (consumer-stored typed inputs; dual Id+Name+port serialization), `RiskElementFactory`/`RiskElementResolver`, `HazardSourceOption`, and **`ComponentGraph`** (name authority, Kahn sort + cycle detection, derived fan-out, upstream paths, `GetAvailableHazardSources` discoverability, the structural validation catalog, three-pass load, clone-map re-linking). `SystemComponent.Graph` is the persisted truth; `FailureModes` is a fresh deterministic projection (declared terminal order); `AddFailureMode` expands chain-style modes losslessly; `CanonicalHash()` hashes the identity form (options + hazard + projected FMs) so element renames/ids/canvas moves can never perturb seeds. Structural label-free consequence binding (`HazardSource` → `(ConsequenceHazardDimension, ConsequenceHazardPosition)`, resolved-on-write default = last response's input); bivariate output/input ports reserved now (functions Phase 11, zero shape breaks).

**Unit tests (landed; suite 240):** occurrence-index scheme (identical-content components get 0..n−1 in canonical order; cross-analysis stability; metadata-inert), component/FM/stage/element/graph validation matrices, correlation-matrix G17 round-trip (Q-G resolved), element-identity hash inertness + equal-content hash equality, projection determinism (levee acceptance scenario: consequence bound to raw peak flow ⇒ position 0), expansion round-trip (bit-identical hash), topology (sort/cycles/paths/fan-out), three-pass serialization incl. stale-Id throws and unknown-type policies.

**Moved to Phase 4:** `SampledComponent`, `SampledFailureMode` (Q-N per-pair shared-draw coupling — designed with the compute loop), `ComponentRiskOutput`, `SetupSamplers`/`Sample`, and all of `Models/RiskAnalysis/Results` (the JSON-first redesign: `Curve`, `Curves`, `RiskPoint`, `Ensemble`, `ComponentRealization`/`FailureModeRealization`/`SystemRealization`, `ComponentResults`/`FailureModeResults`/`SummaryRiskResults`/`SystemRiskResults`/`EnsembleResults` — explicit public state, `ToJson()`/`FromJson()` + compressed-bytes overloads, same computed outputs; v1.0 BLOBs unreadable, old projects re-run). `ProfileHazardFunction` deferred to the results design (Q-T).

**Exit criteria: met** — component + graph layers P/T; occurrence-index behavior pinned by tests; identity-form hashing pinned (the v1 canvas-position seed bug is structurally unreachable).

## Phase 3.5 — Layer boundary seams

Inserted as a fractional phase (rather than renumbering 4–13) after reviewing `Systems.Components`
against the `C:\GIT\rmc-bestfit` model → UI → App layering. Prepares the library for the UI/App
layers without changing anything a headless caller sees. Normative outcome:
[MODEL_LIBRARY_ARCHITECTURE.md §8](requirements/MODEL_LIBRARY_ARCHITECTURE.md#8-layer-boundaries--consumer-contract) (v0.11).

**Scope:** `IRiskFunction.Id`/`AssignNewId` as the rename-proof reference key (stripped from
hashing); `RiskSerializationMode { SelfContained, ByReference }` with additive `ToXElement(mode)`
overloads; `IRiskFunctionResolver`/`RiskFunctionResolver` mirroring `RiskElementResolver`;
wrapped-function change propagation through element → graph → component (with
`ConsequenceElement.Functions` an `ObservableCollection` whose owner reconciles per-item
subscriptions, per the BestFit `CompositeAnalysis.Analyses` precedent); authoring surface for a
DAG editor (`RiskElementFactory.CreateForFunction`/`Create`, `IRiskElement.TryAssignFunction`,
`GetReferencedFunctions`, unresolved-reference validation).

**The problem it solves:** elements owned and inlined their functions, so a component blob carried
a full copy of every function. Once functions are stored items in their own right, that copy would
win on load and silently discard edits made where the function is stored.

**Landed 2026-07-20.** `dotnet build` 0 warnings; `dotnet test -c Release` 265/265; docs validation
green. Headline pin: a component's canonical hash is byte-identical across both serialization
modes, so results can never depend on how a project was saved. Also pinned: by-reference round-trip
re-attaches the *same* function instances (`Assert.AreSame`); stale ids throw, missing names are
reported by validation; and a model-only end-to-end test builds, validates, and hashes a system
with no store, no resolver, and no consuming layer in the call path.

**Exit criteria: met.**

## Phase 4 — Analysis foundation + RiskAnalysis engine core (1D)

> **v0.13 (2026-07-21):** the single Phase 4 was split into 4 / 4b / 4c and the "port the v1.0 compute exactly" instruction was replaced. The legacy engine is demonstrably wrong on LEC tails and system aggregation, and Numerics has since gained the tools to fix it. Normative detail: [requirements/MODEL_LIBRARY_ARCHITECTURE.md](requirements/MODEL_LIBRARY_ARCHITECTURE.md) §6.4.1, §7.3, §7.7, §7.8 (v0.13); math: [technical-reference/risk-integration.md](technical-reference/risk-integration.md), [technical-reference/loss-exceedance-curves.md](technical-reference/loss-exceedance-curves.md).

**Scope:** the Phase-3 deferrals first — `SampledComponent`/`SampledFailureMode` (Q-N per-pair shared-draw coupling; and Q-V, ratified at session start, on whether mixture-branch enumeration also applies in the full-MC path), `ComponentRiskOutput` (**with the parallel `ResponseProbabilities`/`FailureConsequences`/`ExcessConsequences` lists live from the start — the legacy `ComponentRiskOutput.vb:39` TODO — since Phase 4b needs them**), `SetupSamplers`/`Sample` on `SystemComponent`/`FailureMode`, and the `RMC.TotalRisk.Results` JSON-first containers (see the Phase 3 moved-scope list) — then `Analyses` (`IAnalysis` in `Core.Interfaces`, `AnalysisBase`, `AnalysisRunCompletedEventArgs` — BestFit mirror) and **`RiskAnalysisOptions`** (ratified extraction; v1.0 option names/defaults preserved: `EstimateMeanRiskOnly=true`, `Realizations=1000` [100–10000], `PRNGSeed=12345`, `LECOutputLength=200` [50–1000], `ConfidenceIntervalWidth=0.9`, `Alpha=0.01`, `ConsequenceThreshold=0`, `SystemRiskMethod`/`JointConsequences`/`ComponentHazardDependency`/`HazardCorrelationMatrix`, integration options + `UseDefaults`/`SetIntegrationDefaults`; new `SamplingScheme`; **new `RiskIntegrand` (default `MeanTotalRisk`)**). The options' `ConfidenceIntervalWidth` drives per-function uncertainty summaries through the Phase 2 `ComputeUncertaintyResults` contract.

**Engine core (1D / single-component / additive-per-component path):**
- `RiskAnalysis.RunAsync`: validation gate, content-based per-component seeds + occurrence indices (replacing the v1.0 canvas-order master-PRNG cascade — the seed-dependency bug), per-function `SetupSampler` walk, then the mean-only path (`Compute(-1,-1)`-equivalent) and full-MC path (`Parallel.For`, per-realization `SystemRealization` + compact `SystemRiskResults`, `PostProcessUncertainty` percentile curves). Lifecycle: `Task RunAsync(progress, ct)`, cancellation, `AnalysisStarting`/`AnalysisCompleted`. `RiskAnalysis` owns its components; per §8 (v0.11) `ToXElement()` serializes **options + `IsEstimated` only** — components and results arrive through the constructor.
- **1D integrator = `AdaptiveGaussKronrod`** (G10K21), not `AdaptiveSimpsonsRule`, at both call sites (per-component risk integral and CVaR). Same `Integrate(List<StratificationBin>)` surface; p-domain `[1e-16,1−1e-16]`, 50 stratified hazard bins, tol 1e-8, MaxDepth 100, MaxEvaluations 1e6, `MinDepth ≥ 2`. Item 1 of §7.3/§7.7.
- **`RiskIntegrand`** enum (`Core.Enums`, a hashed option) selects the adaptive-refinement objective; default `MeanTotalRisk` reproduces v1.0 point placement. Discontinuous integrands inject their crossing as a bin boundary. §7.7.
- **Exact LEC construction + stable moments + the risk-measure catalog** (§7.7): probability mass from the quadrature weight (the deduped midpoint-trapezoid fallback shipped as the Phase 4 interim; **N7 `Recorder` adopted at Phase 8.5** via `QuadratureMassLedger`); exact exceedance curve from sorted `(mass, consequence)` pairs thinned to `LECOutputLength` for output only; weighted-Welford central moments; `ValueAtRisk`→0 when `α>TotalProbability`; Total percentile curve read from the Total LEC. Competing-failures CIF pre-processing preserved.
- **Mean-only mixture exposure** (§6.4.1): `SampleExposureBranches()` on the consequence contract; the mean-only path enumerates Mixture branches as weighted exposure states instead of flattening to the mean curve, so σ/VaR/CVaR/tail are correct while the mean is algebraically unchanged. Non-composite types return a single unit-weight branch.

**Unit tests:** engine smoke on tiny scenarios (mean-only + full MC); cancellation; validation failures throw; event lifecycle; options round-trip + hash recipe (incl. `RiskIntegrand`); AGK vs a closed-form 1D integral; exact-LEC vs analytic exceedance on a known consequence distribution; weighted-Welford moments vs closed form; mixture-exposure mean parity + non-degenerate σ.

**Verification (pinned here, expanded in Phases 5–6):** the v1 seed-bug regression — shuffle components / rename everything / edit metadata → **bit-identical** results; same seed → bit-identical across thread counts. Mean parity vs a legacy 1-component oracle (means are unchanged by the LEC/mixture fixes — the free regression gate; tails validated by new MC oracles per [verification.md](verification.md)).

**Exit criteria:** foundation + engine core P/T with reproducibility pinned; single-component mean-only and full-MC both green; mean parity vs the legacy 1-component oracle.

## Phase 4b — Multi-dimensional system risk

> **Complete (2026-07-23)** with the v0.15 implementation errata (architecture doc status log):
> the additive convolution is an **exact lattice** via `Fourier.FFT` (`Analyses/SystemConvolution`,
> public) — `EmpiricalDistribution.Convolve` samples continuous PDFs and cannot represent the zero
> atoms, so **N8 is extended** to an atom-aware convolution; the automatic γ target comes from a
> **deterministic per-component AGK failure-probability probe** (the warm-up harvest both wiped the
> importance grid via the `NumberOfBins` reset and could not see rare failures at γ = 1); recording
> accumulates **five self-normalized passes**; defective system stream probabilities keep the v1.0
> system-state semantics (failure union / complement); the convolution and the VEGAS seed base fold
> in canonical-hash component order (additive reorder + rename bit-inert; joint rename bit-inert,
> reorder statistically equivalent by construction). Exit gates met: system LEC for both methods,
> convolved mean == Σ means by construction, brute-force MC tail cross-checks green
> ([verification/system-risk.md](verification/system-risk.md)), γ-audit green.

**Scope:** the two multi-component system-risk methods (§7.8), each producing a true system LEC for all five risk types.

- **Additive → strict independence + FFT convolution.** Redefine the additive method to assume strictly independent components (ratified): error if a correlation is supplied under the additive method; delete the v1.0 correlation-matrix σ formula from the additive path; system `pF = Probability.IndependentUnion(pfs)`. Build the system LEC by zero-inflating each defective component curve (atom at 0 with mass `1−TotalProbability`) and convolving via `EmpiricalDistribution.Convolve(IList<EmpiricalDistribution>, numberOfPoints)` — this is exactly the 2^D failure/non-failure combination enumeration in O(n log n), and produces the system LEC v1.0 never built. New option `SystemConvolutionPoints` (default 4096, min 4096). Assert convolved mean == Σ component means to 1e-6 relative (the exact v1.0 additive answer). Grid caveat + log-spaced follow-up = Numerics item N8.
- **Joint → Vegas power transform + real combination enumeration.** Expose `VegasTailFocusMode { None, Automatic, Manual }` (default Automatic) and `VegasTailFocusParameter` (γ, default 1.0, [1,20]); the Automatic heuristic harvests `pTarget = clamp(min_i P̂_f,i·Alpha, 1e-12, 1e-2)` from the warm-up pass and calls `ConfigureForRareEvents`. Enumerate real component failure/non-failure combinations from the now-live per-pathway lists instead of convolving conditional means; accumulate LEC points across `IndependentEvaluations>1` recording passes; scale `FinalEvaluations` with D; fix the `tPF` double-increment. Audit the Vegas power-transform Jacobian is folded into `wgt` before enabling γ>1 (Numerics item N9).

**Unit tests:** additive-independence validation error; zero-inflation round-trip; convolved-mean == Σ means; Vegas γ heuristic determinism; multi-pass recording; combination-enumeration vs a small brute-force reference.

**Verification:** system LEC produced for **both** methods (v1.0 produced none for additive); brute-force MC cross-check of the system LEC tail at α = 0.01 within documented k·SE; mean parity vs the legacy joint-method oracle.

**Exit criteria:** additive + joint system risk P/T with the MC tail cross-check green; means parity held.

## Phase 4c — Reliability mode

> **Complete (2026-07-23).** The relaxation is a **mode-aware validation chain**
> (`Validate(RiskAnalysisMode)` overloads on `FailureMode` / `ConsequenceElement` /
> `ComponentGraph` / `SystemComponent`; consequence elements stay the structural terminals but
> need no functions), the engine **forces** the effective refinement objective to
> `TotalProbabilityOfFailure` in reliability mode (pinned by a bit-identity test across
> configured objectives), and the results shape is the **existing containers** — AFP =
> the Fail stream's `TotalProbability` at every level (mode/component/system; union under the
> system methods), consequence surface degenerate at zero, mass-balance warning suppressed.
> Multi-component reliability rides the Phase 4b machinery unchanged.

**`RiskAnalysisMode.Reliability`** (v0.10 — replaces the planned `ReliabilityAnalysis` sibling) — a mode of the one `RiskAnalysis`: failure probabilities / annualized failure probability per FM/component/system, no consequence functions required (relaxed FailureMode validation), reusing the sampled-component machinery with the **`RiskIntegrand.TotalProbabilityOfFailure`** integrand. One graph traversal serves both modes.

**Unit tests:** reliability-mode smoke (single + multi-component); consequence-free validation relaxation; AFP vs closed form; mode round-trips in options.

**Exit criteria: met** — reliability mode P/T (AFP vs dense reference at 1e-3; union AFP exact; forcing pinned).

## Phase 5 — Verification I: single-component oracle families

> **Complete (2026-07-23).** All five Bucket-1 families converted at N = 10⁶ (53 legacy
> methods → 27 scenario tests plus per-family reproducibility pins; sampling-identical rule
> variants consolidated per group, the
> broken legacy 5-PFM Competing-Positive body replaced by the correct r = 1 − √ε oracle, the
> `_CCA`/`_CommonCause_Independent` pairs merged — identical streams), with the 2024 report's
> published constants pinned for all 22 published scenarios and the v0.13 tails policy
> realized (each ported oracle doubles as the brute-force tail oracle for σ, LEC probes, VaR,
> CVaR, conditional means, and assurance). **Two new families beyond the roadmap scope**
> (ratified at session start): `SingleComponentUncertainty` (two-loop knowledge-uncertainty
> oracle with an exact inner integral; Q-N coupling counter-pin; the `ParametricResponse`
> posterior-injection anchor) and `CombinationMethodConsistency` (union invariance across
> methods, Fréchet bound ordering, background/`RiskIntegrand` invariance, reliability
> parity). Pre-flight verification exposed and fixed **two latent engine defects** before any
> test was written: the perfectly-negative dependency matrix never materialized on the run
> path (silent zero risk under Joint/CCA, a faulted run under Competing — corrected at the
> `SetupSamplers` freeze point, with the integrator failure-status guard added so swallowed
> integrand exceptions can never truncate curves into silent zeros again), and the
> common-cause perfectly-positive factor faulted against the Numerics overload's
> unconditional null-matrix check. No `LinearTransform`/`PowerTransform` pull-forward was
> needed (the legacy Bucket-1 scenarios use none). Results: [verification/](verification/README.md).

**Scope:** Convert the Bucket-1 legacy oracles to asserted C# verification classes at **1,000,000 realizations** (policy: [verification.md](verification.md)): `Test_MC_JointFailures` (1-component, 2- and 5-PFM, {Independent/Positive/Negative/Correlation} × {Additive/Average/Maximum/Minimum}), `Test_MC_CompetingFailures`, `Test_MC_CommonCause`, `Test_MC_MutuallyExclusive`, `Test_EAD`. Engine-vs-oracle statistical asserts on the five summary outputs (incremental/irreducible/total/failure/non-failure means) + failure probability; FN-curve spot checks. If a legacy scenario requires a `LinearTransform`/`PowerTransform` as a typed model object, pull that thin wrapper forward from Phase 7 (zero-math wrappers over existing `Numerics.Functions`).

> **v0.13 parity policy (ratified 2026-07-21):** the Phase 4/4b/4c LEC/mixture/aggregation fixes leave the **means** unchanged (verify closely against the legacy oracles — the free regression gate), but move **σ / VaR / CVaR / F-N tails** deliberately. Those tail measures are verified against **new brute-force Monte Carlo oracles**, not v1.0 parity. Where a legacy oracle itself encodes the conditional-mean collapse or the mixture-mean flattening, port it for the mean only and add an independent MC oracle for the tail. See [verification.md](verification.md).

**Exit criteria:** all four combination-rule families + EAD verified; tolerances documented per test with k·SE derivation; suite runtime recorded.

## Phase 6 — Verification II: system risk + NFIP assurance

> **Complete (2026-07-23).** Four families landed: `SystemRiskMatrixVerification` (the 36
> legacy `Test_MC_SystemRisk` methods consolidated to 12 dependency-group tests — 2-comp/2-PFM
> additive, 2-comp/1-PFM and 5-comp/1-PFM across {Independent, PerfectlyPositive,
> PerfectlyNegative, CorrelationMatrix} × all four joint-consequence rules — with the 2024
> report's tables 77–103 pinned for every published scenario, the additive engine asserted on
> the full Phase 5 catalog and the joint engine on the 4b convention, plus the 5-component
> additive shuffle/rename bit-identity pin); `RiskAnalysisCombosVerification` (the
> `Test_RiskAnalysis` combos: 1-comp 3/4-PFM perfectly negative groups across all rules, the
> 3/4-component negative systems, the r = −0.25 average scenario the mislabeled
> `Test_5Element_1PFM_2` body computes, with every remaining legacy method's disposition
> documented — stream-identical duplicates, workbenches, and Phase 9 scope); the roadmap's
> "2-component/2-PFM and 5-component/1-PFM" reading of the legacy matrix was extended from the
> bodies (2-comp/1-PFM is the published bulk of the legacy file). `NfipAssuranceVerification`
> (TOL 50/55/70 API oracles at the legacy seed + an exact-quadrature reference + Table 104
> pins, both parametric-LP3 and dense-tabular hazard engine variants in reliability mode with
> the top-of-levee `HazardThreshold` mapped to exact rating knots; **plus the full-uncertainty
> assurance ensemble the TR NFIP appendix requires** — a 300-set injected LP3 posterior,
> per-realization API parity against exact quadrature, and the assurance fraction
> P(API ≤ 0.01) pinned exactly). `LhsVarianceReductionVerification` (MC vs LHS at N = 1k over
> 5 replicate pairs — variance ratio, unbiasedness, mean-only linearity anchor). One latent
> engine gap found and fixed: the additive system's failure union folded component
> probabilities in declaration order (reorder moved the union by 1–2 ulp against the 4b
> bit-inertness contract) — it now folds in the canonical-hash order the convolution uses.
> Tolerance policy finalized in [verification.md](verification.md).

**Scope:** Convert `Test_MC_SystemRisk` (2-component/2-PFM and 5-component/1-PFM across the correlation × aggregation matrix — port from method *bodies*, several legacy names are mislabeled), the `Test_RiskAnalysis` N-element/N-PFM combos, and NFIP Assurance TOL 50/55/70 (LP3 flow frequency → rating transform → fragility → AEP). Add the LHS variance-reduction test (MC vs LHS at N=1k over repeated runs). Finalize the tolerance policy in [verification.md](verification.md).

**Exit criteria: met** — multi-component system risk + NFIP verified; tabular+parametric clusters, the engine, and the reliability mode all carry V status in the matrix.

## Phase 6.5 — Multi-consequence axis + engine performance — **COMPLETE (2026-07-23)**

> The post-Phase-6 gap-audit session (plan ratified with five user decisions recorded in
> PROGRESS). Three deliverables:
>
> 1. **The Q-U closure.** The consequence-type axis is declared at the analysis level
>    (`ConsequenceTypeDescriptor` + `RiskAnalysis.AdditionalConsequenceTypes`, append-only
>    serialization) with strict bubble-down validation — counts and order always, labels/units
>    when both sides are non-blank — and the engine computes **every** declared type in one
>    pass: per-type Q-N coupling columns, shared probability structure, primary-driven
>    refinement, per-type curves/extents/percentile grids at every scope, `ConsequenceResults`
>    on the summary tree with the declared labels echoed. RNG-silent for fixed models (the full
>    pinned suite passed unchanged) and verified by the new `MultiConsequenceVerification`
>    family ([verification/multi-consequence.md](verification/multi-consequence.md)).
> 2. **The dedicated performance pass** (the PROGRESS-recorded ~10× regression brief):
>    realization-owned compute workspaces, single-pass balanced-objective scales, an in-place
>    cached-Cholesky joint latent transform, a zero-ulp merge-walk percentile interpolator
>    (`Curve.InterpolateLogLogDescending`), the exact closed-form CVaR (retiring the deepest
>    per-realization quadrature), and the relaxed ensemble integration budget
>    (`EnsembleTolerance` 1e-4 / `EnsembleMinDepth` 0 defaults; the mean pass, probes, and
>    mean-only runs keep 1e-8/MinDepth-2). **F1 fixture 31.5 → 3.9 s (8.1×), allocations
>    39 → 3.7 GB**; zero pinned verification constants moved (exact-anchored families pin
>    their discipline in-test). Measurements: [scripts/perf/RESULTS.md](../scripts/perf/RESULTS.md).
> 3. **The cascade design ratification** (arch doc v0.16): Q-X closes into the Phase 6.7
>    design below; Phase 6.5 landed the resilience hooks (per-terminal results scope, factored
>    SRP, reserved `ResponseStage` attribute space) without touching the two Q-X seams.
>
> Doc reconciliation rode along: ROADMAP Phase 1–3 status markers fixed, the
> SHARED_FUNCTIONS_STRATEGY N7 numbering collision resolved (release → N10), the repository guidance
> Phase-8/9 pointers corrected.

## Phase 6.6 — Risk measures, diagnostics & sensitivity — **COMPLETE (2026-07-24)**

> Landed in eight staged commits (plan ratified with nine user decisions recorded in PROGRESS;
> arch doc → v0.17). What shipped, against the scope below:
>
> 1. **Q-T closure** — `SystemComponent.ProfileHazardElementId` (seed-inert element reference;
>    every recorded hazard coordinate remaps through the realization's own sampled profile
>    chain) + the expanded profile catalog (`CumulativeFailureProbabilities` with terminal ≡
>    Fail `MassBalance`, per-type `CumulativeExpectedConsequences`, the SRP profile on the
>    **AEP axis**) + restoration of the v1.0 five-stream banding parity
>    ([verification/risk-profiles.md](verification/risk-profiles.md)).
> 2. **% contribution to risk (new diagnostic)** — Shapley probability split +
>    consequence-proportional risk split over each method's exclusive failure events; exact
>    Σ-identities to `MassBalance`/`Mean`; the additive-system union APF via the exact O(D²)
>    Poisson-binomial DP; two bases (% of APF — full in reliability mode — and % of mean loss);
>    math in [technical-reference/risk-contribution.md](technical-reference/risk-contribution.md),
>    evidence in [verification/contribution.md](verification/contribution.md).
> 3. **Scalar CIs + convergence diagnostics** — `EnsembleSummary` (Mean/Median/Lower/Upper
>    trees reduced measure-wise from the stored ensemble, recomputable on load) +
>    `ConvergenceDiagnostics` ([verification/scalar-uncertainty.md](verification/scalar-uncertainty.md)).
> 4. **The unified sensitivity engine** (ratified scope amendment: no legacy Dictionary-shaped
>    port, no public `RiskAtHazardLevel`) — `MeasureSensitivity`/`MeasureSensitivityMatrix`
>    (any stored scalar measure, zero re-simulation) + `HazardLevelSensitivity` (profile-axis
>    native, no-record evaluation); system/component/failure-mode scope selector; inputs from
>    the one shared sampler walk ([verification/sensitivity.md](verification/sensitivity.md)).
> 5. **Per-type `ConsequenceThreshold`** (the 6.5 primary-only interim closes) and the
>    **§5.5.8 seed-stable perturbation mode** (`CapturedSamplerSeeds`/`PinnedSamplerSeeds`,
>    per-walk-ordinal pinning + the joint VEGAS seed base, loud shape validation).
>
> **Deferred to the UI layer (ratified):** exact-pair excess entry lists (the
> `ComponentRiskOutput` interim stays — the recorded per-mode curves carry what the engine
> owes) and TRG-line comparison data (chart furniture, not compute). **Future-performance note
> (Phase 8/12 candidate, NOT to be attempted without a ratified re-verification pass):** the
> joint path's independence-only polynomial identities — Sum-rule mean by linearity, Max-rule
> product CDF, Average-rule Poisson-binomial recursion, Sum-rule per-evaluation lattice
> convolution — could replace the 2ⁿ − 1 exclusive enumeration when hazard dependence is off.

**Scope:** the v1.0 diagnostic surface never ported, plus the measure extensions scoped by the Phase 6.5 audit:

- **Sensitivity/tornado port**: `SensitivityMeasure` enum (`PearsonCorrelation`, `SpearmanCorrelation`, `SensitivityIndex` — legacy names), typed `RiskAtHazardLevel` on the sampled component (the labeled input dictionary), and `RiskAnalysis.Sensitivity(componentIndex, hazardLevel, measure, riskType)` — content-seeded (no wall-clock PRNG), default 100 realizations. Porting source: the Dev-repo partial C# port (`Risk Analysis/RiskAnalysis.cs:4364`, `Support/Components/SampledComponent.cs:253`) first, the VB (`RiskAnalysis.vb:3625`, `SampledComponent.vb:214`) as cross-check.
- **Q-T closure**: `ProfileHazardFunction` as a chain-position remap (the consequence-binding precedent) driving the risk profiles + the `HazardThreshold` companion selector.
- **Ensemble uncertainty on scalar measures**: percentile CIs on Mean/VaR/CVaR/APF and the rest of the catalog from the already-stored `EnsembleResults` (curves carry bands today; scalars do not).
- **Convergence diagnostics**: aggregated integrator standard-error/evaluation summaries, realization-adequacy indicators on the ensemble.
- **Seed-stable perturbation mode** (arch doc §5.5.8) for sensitivity studies.
- **Q-W shared-exposure declaration** (design; per-type marginals make it moot until cross-type joint statistics are wanted) + per-type `ConsequenceThresholds` (the Phase 6.5 primary-only interim).
- Evaluate: exact-pair excess entry lists (`ComponentRiskOutput` interim), TRG-line comparison data (UI-leaning — may defer to the UI layer).

**Exit criteria:** sensitivity family verified against a legacy-style oracle; remap round-trip + hash recipe pinned; scalar-CI family verified; diagnostics JSON append-only; Q-T/Q-W doc closures.

## Phase 6.7 — Cascading response end states — **COMPLETE (2026-07-24)**

> Landed in six staged commits (Stage-0 design ratified with three user decisions — final-polarity
> classification, flipped-final-sibling excess pairing, single-claiming-group scope — plus the Q2
> allow-with-legacy duplicate-leaf ruling and the Q3 names + path-descriptor labeling; the
> knowledge-independence directive recorded and pinned; arch doc → v0.18, **Q-X CLOSED**). What
> shipped, against the scope below:
>
> 1. **The authoring/identity surface (the one deliberate hash event):** `BranchPolarity`
>    (serialized enum, port-index values), `ResponseStage.BranchPolarity` resolved-on-write,
>    `ResponseElement.OutputCount` 1 → 2, port-aware projection/expansion round-trips, the
>    identity-form `ResponseNodes` topology annotation (closing the shared-vs-duplicated response
>    hash gap), same-port-aware `MultipleConsequences`, and the §7.9.1 branch-claim advisories.
>    Every seed moved once; the deterministic bit-pin + relational families + byte gates proved
>    only seeds moved.
> 2. **Multi-stage engine acceptance:** both Q-X seams replaced — per-stage sampled capture, the
>    polarity-product SRP (single-stage Fail bit-identical), the all-stage consequence-input fold
>    (fixing a stage-0 truncation), the restrictive `InverseSRP` policy.
> 3. **The state-group layer:** `EndStateGroupLayout` (public) — leaf-signature exclusive groups,
>    Q2 standalone ejection, final-polarity classification, sibling pairing; unit-dimensioned
>    combination caches/MVN/correlation; unit-level kernels with conditional state distribution;
>    the claimed-complement mixture driving the non-failure scalar, joint excess baseline, and
>    exhaustive recording; contribution at picked states with exact Σ-identities; the narrow
>    competing gate (else-chains only) and single-claiming-group validation; group-aware
>    guardrails. All-singleton inertness proven by bit-identical F1/F2/F3 byte gates.
> 4. **Q3 labels:** stamped end-state `Name` + append-only `PathLabel` on realization, summary,
>    and band trees (JSON-only byte-gate re-pin).
> 5. **`CascadeEndStateVerification`** (8 tests): the partial-damage natural oracle, the
>    joint/ME/competing across-unit matrix, the bit-exact saturated-stage equivalence,
>    reliability APF, system smokes, reproducibility pins
>    ([verification/cascade-end-states.md](verification/cascade-end-states.md)); math in
>    [technical-reference/cascading-end-states.md](technical-reference/cascading-end-states.md).
>
> **Deferred, documented (§7.9.9):** multi-group claimed states; competing over else-chains
> (telescoping-union analysis); state-level cross-group `ExclusivePCM` coupling; numeric
> `InverseSRP` for cascades.

**Scope:** the ratified event-tree-in-the-diagram design (arch doc v0.16 — user decisions: typed output ports; exact partition with auto-remainder; EventTreeResponse survives as a compact node):

- `ResponseElement.OutputCount` 1 → 2: **port 0 = Fail** (the default — every existing connection already targets it), **port 1 = Non-Fail**; polarity-aware path classification and validation (both-port fan-out legal; the response-free background path unchanged; consequence alignment per terminal unchanged).
- `ResponseStage.BranchPolarity` (append-only serialized attribute, resolved-on-write per the `ConsequenceHazardPosition` precedent — a deliberate, documented hash/re-pin event); `FailureMode` SRP(h) becomes ∏ᵢ (polarityᵢ = Fail ? pᵢ(h) : 1 − pᵢ(h)); multi-stage acceptance replaces the two Q-X seams (`RiskAnalysis.Validate` gate + `SampledFailureMode` ctor throw; `SetupSamplers` already walks all stages).
- End states ARE the projected failure modes (one per consequence terminal, carrying the full declared consequence-type axis); terminals sharing upstream response elements form a **mutually-exclusive state group** — within-group exact partition (no inclusion–exclusion), across groups the existing `FailureModeMethod`, remainder mass = 1 − Σ state weights → background (the engine's existing complement math). Shared response instances already share sampled draws, so branch probabilities stay coherent across sibling end states for free.
- Partial failures with partial damage states: consequences wired to Non-Fail-port continuations (e.g., R1-Fail → R2 "progresses to breach": R2-Fail terminal = full-breach consequences, R2-Non-Fail terminal = partial-damage consequences).

**Exit criteria:** hand-rolled two-stage cascade MC oracle family (including a partial-damage state); single-stage-equivalence checks (statistical — the polarity attribute moves hashes); graph round-trip + projection + polarity tests; arch-doc Q-X closure.

## Phase 7 — Remaining closed-form functions — **COMPLETE (2026-07-25)**

> **Landed 2026-07-25** in three commits: `LinearTransform` + `PowerTransform` (v1.0 surfaces
> verbatim over Numerics `LinearFunction`/`PowerFunction`; σ serialized/hashed only while
> uncertain per §5.5.3; D = IsUncertain ? 1 : 0 with the deterministic index-sampling branch;
> `PowerFunction.Minimum` never assigned — its setter was a silent no-op in every shipped v1.0
> build, so the wrapper keeps `Minimum` as API/hash surface with a new advisory warning when it
> sits below ξ), then `NonparametricHazard` with the **user-directed optimized
> quantile-uncertainty derivation** (the per-ordinate Brent 1%-bound repair replaced by the
> exact closed-form quadratic in the log-space scale with the legacy Brent as guarded fallback;
> inlined base-e moment maps; the frequent-end extrapolation evaluated before list mutation —
> fixing a latent v1.0 order-of-operations defect that silently produced a flat extension,
> inert for 0.999-anchored inputs; inputs-only serialization with deterministic load-time
> recomputation of the derived table), then the NEW `ClosedFormFunctionsVerification` family
> (4 tests, run isolated): the 2024 report's SF-8 vs HEC-FDA **Table 38** — all 20 published
> log10 quantile constants pinned at 5.5e-4 — plus the independent legacy-pipeline
> re-derivation (the optimization-equivalence anchor), transform-chain ensembles vs flat MC
> oracles at MT(12345) N = 10⁶, D = 0 dense-quadrature parity, and reliability AFP oracles with
> bit-identity pins ([verification/closed-form-functions.md](verification/closed-form-functions.md)).
> **No deferred EAD/NFIP conversions existed** — the Dev-repo sweep proved the legacy scenarios
> never used these types (transforms: zero Test_TotalRisk usages; the nonparametric hazard:
> FDA-importer only). Q-N needed no action here (resolved Phase 4, counter-pinned Phase 5).

> **Pulled forward (2026-07-21):** **`ParametricConsequence`** landed pre-Phase-4 (renamed from `ParametricConsequenceFunction` for cluster consistency — arch doc v0.12) with unit tests and a function-level verification family ([docs/verification/parametric-consequence.md](verification/parametric-consequence.md)). This phase's remaining scope is the three types below.

**Scope:** **`LinearTransform`** (`y = α + βx`, optional Gaussian uncertainty), **`PowerTransform`** (`y = α(x − ξ)^β`, log-space uncertainty, optional inversion) — thin wrappers over the existing `Numerics.Functions` `LinearFunction`/`PowerFunction` per [requirements/SHARED_FUNCTIONS_STRATEGY.md](requirements/SHARED_FUNCTIONS_STRATEGY.md); **`NonparametricHazard`** (empirical CDF + Weibull extrapolation). Domain labels, validation, serialization, hash identity, `ComputeUncertaintyResults` — zero math in the wrappers.

**Unit tests:** evaluation at known points vs closed forms (incl. uncertainty at fixed percentile, inverse power form, clamping), serialization/hash coverage, transformed-hazard bounds. Resolve open question Q-N (fail/non-fail consequence percentile coupling) formally if not already pinned by Phase 3.

**Exit criteria:** remaining three types P/T; any Phase 5/6 deferred scenarios that needed these types converted.

## Phase 8 — Numerics.Functions expansion (numerics repo) + package switch — **IMPLEMENTATION COMPLETE (2026-07-25); release + switch pending**

> **Landed 2026-07-25** in eleven commits on `bug-fixes-and-enhancements` (`b484b21`…`0a16f9d`,
> unpushed — the user coordinates the push, the v2.2.0 release, and the subsequent
> HintPath→PackageReference switch, which remain the phase's exit gate; TotalRisk keeps the
> sibling Debug HintPath until then, per the session's ratified decision). What shipped, per
> SHARED_FUNCTIONS_STRATEGY v1.2 §4: **N1** (`UnivariateFunctionType` + `UnivariateFunctionFactory`
> + concrete `ToXElement`/`FromXElement` on Linear/Power/Tabular — the ratified amendment keeps
> serialization OFF `IUnivariateFunction`: TotalRisk's adapters implement it and net481 has no
> default interface members); **N2** `SegmentedPowerFunction` (BaRatin addition mode, the exact
> BestFit `[h₁, log₁₀α₁, β₁, …, σ]` layout, Brent inverse, one-segment `PowerFunction`
> degeneracy, independently evaluated parity constants); **N3** `CompositeFunction`
> (weighted-average + single-uniform mixture composition); **N4** `EnsembleFunction` (pure
> index/percentile clone sampling off an immutable template snapshot — parallel
> no-shared-mutation pinned); **N5** the `EmpiricalDistribution`/`KernelDensity` XElement
> round-trip fixes (both previously faulted — parameter names without scalar values — now
> table-bearing overrides wired into the distribution factory); **N7** the acceptance-aware AGK
> `Recorder` (nodes flush with half-length-scaled Kronrod weights ONLY on interval acceptance —
> call-time hand-off double-counts rejected parents; Σweights = domain width and Σw·f ≡ Result
> pinned under forced subdivision; recorder-off byte-identical); **N8** `ConvolveDiscrete`
> (exact atom-aware lattice convolution — zero-inflation atoms representable at last; means add
> exactly) + the opt-in log-spaced output ladder (the linear pipeline untouched); **N9** the
> Vegas Jacobian unit tests (γ ∈ {1, 4, 10} unbiased; Σwgt = volume per batch); **N11** the
> pooled `IndependentExclusive` overload (caller-owned buffers, in-place row reuse, the
> truncation flag; the allocating overload delegates); **N6** round-trip tests throughout + the
> new `docs/functions/` guide. Pre-existing at the numerics HEAD and fixed first: the
> Kappa-Four regression test's net481 `double.IsFinite` break (→ `Tools.IsFinite`). **Gates:**
> numerics 0 warnings + **1943/1943 across all four TFMs**; TotalRisk revalidated against the
> refreshed sibling Debug build — fast suite 534/534, doc script green, the **F1 byte gate
> bit-identical** (`917ff3a5…`, allocations unchanged — every upstream change is additive and
> off by default), `EngineReproducibilityVerification` 3/3 after one latent Phase 6.7 test
> accommodation (the Q3 `PathLabel` display field echoes renamed functions by design; the
> metadata bit-identity comparison now strips exactly the two display-label fields — hashes and
> every numeric byte were proven equal, the engine untouched). **Engine adoption of N7/N8/N11
> stays a future ratified re-pin session** (user decision 2026-07-25) — executed as
> [Phase 8.5](#phase-85--polish--optimization): N11 at Stage 1e, N7 at Stage 3a.

**Scope:** Executed in `C:\GIT\numerics` (branch `bug-fixes-and-enhancements`) per [requirements/SHARED_FUNCTIONS_STRATEGY.md](requirements/SHARED_FUNCTIONS_STRATEGY.md) §4: N1 function serialization + `UnivariateFunctionFactory`; N2 `SegmentedPowerFunction` (BestFit BaRatin rating form, `ParameterSet`-compatible layout); N3 `CompositeFunction`; N4 `EnsembleFunction` posterior sampling; N5 `EmpiricalDistribution` XElement round-trip fix; N6 tests + `docs/functions/` guide. **Plus the v0.13 risk-engine follow-ups (raised by Phases 4/4b, non-blocking there because each has a documented interim):** N7 — an `AdaptiveGaussKronrod` integrand overload that hands the Kronrod weight to the callback (so LEC probability mass comes from the quadrature directly, retiring the midpoint-trapezoid fallback); N8 — `EmpiricalDistribution.Convolve` upgrades: a log-spaced / adaptive-grid option (the current linear grid starves order-of-magnitude consequence tails) **and an atom-aware discrete/mixed-distribution overload** (v0.15 finding: `Convolve` samples continuous PDFs, so a zero-inflation atom has no representation — the engine's exact lattice kernel `SystemConvolution` migrates onto it when it ships); N9 — Vegas power-transform Jacobian unit tests (integrate a known heavy-tail function at γ ∈ {1,4,10} to the same value; confirm `Σ wgt` = domain volume at every γ — the engine-level empirical audit is green in `SystemRiskVerification`, this is the upstream unit-test half). Release **RMC.Numerics 2.2.0** to the local feed; switch this repo's four csprojs (the library, both test projects, and `scripts/perf/PerfHarness`) from the HintPath to the PackageReference (Hydrologics does the same on its side).

**Exit criteria:** 2.2.0 on the feed; this repo builds green on the package.

> **Task-ID note (2026-08-17):** `docs/PROGRESS.md` tracks the release prep and the package switch
> as review-remediation tasks **N4**/**T8** — a task-numbering scheme distinct from the
> SHARED_FUNCTIONS_STRATEGY N-items in the scope line above, where N4 is `EnsembleFunction`
> (shipped) and the release item is **N10** (a prior N7 collision was likewise renumbered).

## Phase 8.5 — Polish & optimization

> Inserted 2026-07-26 at the user's direction: a dedicated polish/optimization round before the
> remaining input-function work (bivariate hazard/transform/response/consequence, event trees,
> fault trees).
> **User decisions ratified at planning:** the numerics repo is in scope; work is staged
> bit-inert first then one deliberate value-moving batch; the new risk-measure flags default to
> everything on (today's JSON and every pinned constant unchanged); the adjusted marginal LEC is
> an additive opt-in stream, default off.

**Stage 1 — bit-inert cleanups. COMPLETE (2026-07-26).** Numerics helper adoption
(`Tools.IsFinite`/`Clamp`, `ExtensionMethods.Multiply`/`Divide`), the last
`Statistics.ParallelMean` call site retired, combination caches built only for the method that
reads them (which also lifts the enumeration ceiling off the per-mode methods and closes a
`Parallel.For` race), competing-risks pre-processing shared across a deterministic run, pooled
exclusive-combination buffers on both joint paths, and the duplicated summary /
dependency-matrix code collapsed. Every sub-stage gated on F1/F2/F3 reproducing bit-for-bit;
allocations F1 4.10 → 3.88 GB, F2 17.93 → 16.38 GB, F3 7.85 → 7.64 GB. New fixture **F4**
(dependent competing risks) 22.28 → 5.49 s. Two pre-existing defects found and fixed — the
clock-seeded Genz quadrature randomizer (N17) and a would-be interpolator data race. Details in
[scripts/perf/RESULTS.md](../scripts/perf/RESULTS.md) and PROGRESS.

**Stage 0 — numerics upstream. COMPLETE (2026-07-26).** N12 `BootstrapAnalysis`/`UncertaintyAnalysisResults`
determinism *and* speed (loop inversion + a fixed-chunk deterministic reduction; the class
review also found a null-divisor bias, silent fit failures, LINQ in loop bounds, re-bootstrapping
accessors, and a per-distribution lock); N13 lazy exclusive enumeration removing the 2^D
materialization; N14 AGK recorder capture-array pooling; N17 deterministic MVN seeding; N18
NaN-filled parameter sets for failed replications. N15 (`CompetingRisks` CIF knobs) and N16
(weighted moments) were not needed by the stages that would have consumed them and remain open
upstream items.

**Stage 2 — new capability, append-only, defaults preserving today's output. COMPLETE (2026-07-26).**
`RiskMeasureOptions` (every flag defaulting true); the adjusted marginal failure-mode LEC as
opt-in streams; `EstimateResourceRequirements()` + a validator clause replacing four scattered
magic caps; the dimension caps lifted onto the lazy enumeration (VEGAS raised to 50 dimensions
after a survey of the reference implementations — none caps at 20).

**Stage 3 — the deliberate value-moving batch. COMPLETE (2026-07-26).** AGK `Recorder` adoption
for LEC probability mass via `QuadratureMassLedger`, retiring the midpoint-trapezoid interim
(measured 5.7e-7 → 4.0e-11 relative against a 4,000,000-point dense reference); the parametric
hazard/response posterior summarized into `UncertaintyAnalysisResults` directly; and
`Math.Pow(x, 2d)` → `Tools.Sqr` (measured non-inert: 1,560 differences per 3,000,000 inputs).
`SystemConvolution` **stays local** — the audit against N8's `ConvolveDiscrete` is recorded in
the type's remarks: that kernel is pairwise and re-derives its lattice step per fold, so an N-way
fold re-bins and smears each atom once per fold, where the local kernel bins once and folds by
integer shift. Byte gates re-pinned once; the `RMCTR_LEGACY_MASS` A/B scaffold deleted at close.

**Exit criteria:** met — build 0 warnings, fast suite 665/665, doc script green, **all 27
verification families green run isolated across the stage-3 sweep** (149 tests), byte gates
re-pinned in [scripts/perf/RESULTS.md](../scripts/perf/RESULTS.md), and the A/B scaffold removed.

## Phase 8.6 — Numerical safety remediation

> Inserted 2026-07-27 as the release safety gate before Phase 9 resumes. **Complete:** implementation,
> isolated verification, coverage, documentation, and performance gates are green.

**Lazy failure-mode enumeration.** Numerics now exposes pooled lazy independent, perfectly-positive,
and PCM exclusive APIs plus lazy PCM union. TotalRisk routes every `JointFailures` dependency through
them and no longer pre-populates `U × (2^U−1)` indicator or binomial caches. Compatibility inspection
properties remain dense only when explicitly requested. The failure-mode memory ceiling is removed;
worst-case compute is still combinatorial when the established `1E-4` convergence predicate does not
stop early. The separate 50-dimensional joint VEGAS limit remains.

**Probability boundary.** Every finite Numerics probability output clips through `Tools.Clamp` while
NaN signaling is preserved. TotalRisk clips each lazy exclusive partition in deterministic row order
against the remaining unit budget before consequence entries, expected values, profiles, and
contributions. Earlier cells are never rescaled, so this is trailing-overshoot clipping, not
normalization. PCM formulas, correlations, inclusion/exclusion order, convergence rules, seeds, and
default tolerances are unchanged.

**Mass and LEC correction.** The sampled hazard retains its natural probability support. AGK records
unchanged accepted interior weights; explicit endpoint rectangles complete the distribution and the
upper residual makes the exhaustive ledger sum exactly one. `Curve` publishes the recorded
compensated mass and faults invalid exhaustive or defective streams instead of masking them.

**Exit criteria:** met — Numerics 1,993/1,993 on all four target frameworks; TotalRisk Release build
zero warnings and fast suite 698/698 in 11.5 s; documentation, dependency, and legacy-traceability
validators green; unit-only coverage 91.25%; all 28 verification families / 159 tests green one at a
time, including `JointFailuresVerification` 9/9 and `RiskAnalysisCombosVerification` 5/5 with no
tolerance widening. The traceability matrix accounts for all 142 legacy methods (113 applicable)
and report scenarios 1–49; the FDA/NFIP variant is locked obsolete. No TotalRisk execution path calls
`Factorial.AllCombinations`; F1/F2/F3 allocations 3.10/12.23/6.03 GB remain below their pre-ledger
baselines, and paired same-session runtime comparisons show no regression. Final diff audit found no
unapproved tolerance, correlation, normalization, seed, or probability-formula change.

## Phase 9 — Composites + RFA hazard + weighted wrappers

> **Pulled forward (2026-07-21):** **`CompositeConsequence` + `WeightedConsequenceFunction`** landed pre-Phase-4 (combine math in the model library until N3 lands here; BestFit `CompositeAnalysis`-pattern wiring; projected-identity hashing per arch doc v0.12) with unit tests and a function-level verification family reproducing the 2024 report's three scenarios ([docs/verification/composite-consequence.md](verification/composite-consequence.md)). Q-I's composite half and Q-J are resolved for the consequence composite (declared order semantic; ordinal-in-seed) — the hazard/response composites below adopt the same rules. This phase migrates the consequence combine onto Numerics `CompositeFunction` when N3 ships and converts the engine-level composite oracles.

**Scope:** `RFAHazard`, `CompositeHazard` + `WeightedHazardFunction`; `CompositeTransform` + `WeightedTransformFunction`; `CompositeResponse` + `WeightedResponseFunction` — composite math on Numerics `CompositeFunction`/`Mixture`/`CompetingRisks` (migrate `CompositeConsequence`'s in-library combine onto `CompositeFunction` at the same time). **`CompositeHazard` gains the parameter-set import option** (mirroring the Phase 2 parametric injection) so BestFit competing-risks/mixture/composite results import via the UI layer as Numerics artifacts. Resolve whether the injection path supersedes the planned `BestFitUnivariateHazard` type.

**Verification:** engine-level composite consequence, uncertainty, hazard, response, and mixture-identity oracles are covered by `CompositeEngineVerification`; active NFIP TOL 60/65 hazard-bootstrap variants are covered by `NfipAssuranceVerification`. The FDA/NFIP variant is obsolete by technical-authority direction and is not a release gate.

> **Partially landed (2026-07-25):** **`CompositeHazard` + `WeightedHazardFunction`, `CompositeResponse` + `WeightedResponseFunction`, and `CompositeTransform` + `WeightedTransformFunction`** are P/T/V, with function-level verification families against report Tables 44–46 and closed-form probability identities ([composite-hazard](verification/composite-hazard.md), [composite-response](verification/composite-response.md), [composite-transform](verification/composite-transform.md)) and the doctrine page [technical-reference/composite-functions.md](technical-reference/composite-functions.md). Ratified this session: hazard/response mixtures are **aleatory only** (a real `Mixture` distribution, D = 0, no branch selector — the Q-V defect does not arise because a realization is already a distribution); `CompositeTransform` is **Average only** (Mixture needs engine support for enumerating transform branches); hazard/response carry the new `CompositeCombinationType` rather than overloading `CompositeFunctionType`; and `CompositeConsequence` was left untouched.

**Still open in this phase (owners assigned 2026-08-17):** `RFAHazard` — **owner: Phase 11B**, where the import family lives (listed in both scopes historically; 11B executes it); the `CompositeHazard` parameter-set import option (and the `BestFitUnivariateHazard` question) — executes with Phase 11B's import work; migrating `CompositeConsequence`'s in-library combine onto Numerics `CompositeFunction`; and an explicit epistemic mixture mode — unscheduled, waiting on the arch Q-Y transform-branch engine capability. The engine-level composite and active NFIP TOL 60/65 verification gaps are closed. The live item list is maintained in [REMAINING-WORK.md](REMAINING-WORK.md).

**Exit criteria:** composite family P/T/V and engine-level verification are met; RFA hazard and the listed production features remain.

## Phase 10A — Event-tree response and common tree foundation (designed 2026-07-28)

> **Responsibility boundary:** `EventTreeResponse` is a response function that creates the
> conditional fragility relationship `P(F|h)`. Hazard probability remains the responsibility
> of hazard functions; consequences and risk are attached/computed through `RiskConnection`s,
> `ComponentGraph`, and `RiskAnalysis`. The Phase 6.7 binary graph contract remains intact;
> the opt-in expanded view is its additive downstream extension.

> **COMPLETE (2026-07-28):** the landed implementation provides the common branch
> contracts, controlled nodes and authoring, recursive probability sources and independent links,
> current and legacy two-mode XML, projected identity, exhaustive terminal propagation, and an
> immutable instance-scoped compiled plan. The graph slice integrates arbitrary n-way end states:
> `ResponseElement` opts into expanded outputs; the aggregate view remains ports 0 = Fail and 1 = Non-Fail; expanded port 2 is
> the implicit unmodeled non-failure branch; authored and linked terminals receive append-only ports
> from 3. Connections and projected stages persist stable branch ID first and exact name fallback
> second, while materialization preserves linked occurrence IDs/ports. Graph-aware delete applies
> reject, cascade-disconnect, or exact link-materialize behavior transactionally. Factory/resolver,
> both XML modes, validation, end-state grouping, projected canonical hash/seed identity, and exact
> per-leaf sampling all use that shared surface. Complete direct/recursive dependency tracking and
> fingerprints prevent stale reuse; validated mutations invalidate lazily and failed edits restore
> the exact cache/port/sampler state. F5 records a 1,105-instruction repeated-link tree with an
> unchanged result hash and stable cached allocation shape. The fixed-seed 128-case property corpus,
> independent million-route Monte Carlo, 12-replicate SRS/LHS comparison, and production
> sequential/four-worker/default scheduling test close the remaining gates. The isolated family is
> 14/14, fast suite 781/781, coverage 90.40% (12,257/13,584), and final F5 median is recorded.
> Phase 10B is unblocked; no Phase 10B implementation began here.

**Scope:** implement the normative [event-tree and fault-tree response design](requirements/EVENT_AND_FAULT_TREE_RESPONSE_DESIGN.md) §§1–15: the controlled event-tree model; scalar/tabular/response probability sources; internal and external independent-clone links; copy/paste/add/insert/delete/move/replace/link/materialize operations; deterministic traversal, topological sort, search, reachability, pruning, and cycle diagnostics; compiled linear-time probability propagation; mean/percentile/indexed response and per-leaf branch outputs; `RiskSerializationMode`, projected canonical identity, legacy XML conversion, recursive sampler discovery, content-derived seeds, and LHS. Per-leaf output ports extend the Phase 6.7 response-port machinery without moving hazard frequency, consequence, or risk math into the tree.

`SecondaryHazardNode` is excluded: both legacy implementations comment it out, no factory/template/test uses it, and it is not active v1.0 behavior. `WeightedHazardLevel` is **not** dead: legacy `BivariateResponse` uses it, so it remains in Phase 11 rather than Phase 10A.

**Verification:** `EventTreeVerification` converts and asserts the legacy `TestIO` shape and exact shipped `Basic` and `Concrete Dam Gate Failure` templates against independent terminal-for-terminal path-product/mass-conservation oracles; pins indexed child parity, independent-link versus explicit-clone equivalence, nested response sampling, serialization-mode parity, and reproducibility/hash invariance; verifies graph-connected arbitrary n-way per-leaf consequences; and closes independent fixed-seed routing, aggregate LHS variance, and thread-count reproducibility. Fast fixed-seed properties and isolated F5/coverage gates complete the evidence. Legacy `Test_Product` has no assertion and does not construct an event tree; it is not a compute oracle.

**Exit criteria (met):** common/event-tree family P/T/V; ≥90% fast-suite coverage retained; performance fixture and verification results recorded; all manipulation/reference/branch-output/LHS/hash gates in the normative design green.

## Phase 10B — Static fault-tree response ✅ COMPLETE (2026-07-31)

**Scope:** after Phase 10A exits, implement the same normative design's static `FaultTreeResponse`: AND/OR/XOR/K-of-N gates; basic and house events; internal/external transfers; explicit shared-logical versus independent-clone semantics; the same controlled authoring/search/topology/pruning surface; and an exact ordered reduced binary decision diagram (ROBDD) evaluator. The response returns top-event conditional fragility `P(F|h)` only. Exactness is mandatory: minimal cut sets are coherent-tree inspection output, never the production probability algorithm, and resource-limit failure never falls back silently to approximation. The phase also delivers the headless node-importance (node-sensitivity) analysis for tree responses — the Monte Carlo node-importance sweep the v1.0 desktop application computed for event trees, as a typed API on the common tree foundation (scope ratified 2026-07-29).

**Verification:** exhaustive Boolean enumeration for small trees, closed-form gate identities, repeated shared-event versus independent-clone cases, coherent cut-set inspection, independent fixed-seed Monte Carlo checks for medium trees, graph-connected equivalence to an ordinary response curve, LHS/referenced-response sampling, canonical/reproducibility gates, and BDD resource/performance fixtures.

**Exit criteria met (2026-07-31):** the fault-tree family is P/T/V and every applicable Phase 10B acceptance item in the normative design is green — the ratified single-parent tree+transfers model with the exact BDD as the single evaluation path (the optional read-once fast path deliberately not implemented), Rauzy cut-set inspection with the anti-approximation proof, the node-importance analysis for both tree kinds, the fixed-seed fault property corpus, the 17-test greenfield verification family run isolated beside the re-run event family, 90.39% unit-only coverage, the F7 byte-gated performance fixture, and bit-reproduced F1–F6 gates. Exact Birnbaum/criticality importance measures remain documented future work.

## Phase 11 — Bivariate risk analysis ✅ COMPLETE (2026-08-07)

**Scope (re-sliced 2026-08-03; the normative design is [requirements/BIVARIATE_RISK_DESIGN.md](requirements/BIVARIATE_RISK_DESIGN.md), ratified with twelve numbered decisions):** the complete bivariate capability across seven sessions S1–S7. S1 — Numerics copula work in the numerics repo (`IndependenceCopula` with zero parameters; the forward conditional h-function `ConditionalCDF(u,v)` plus the scalar non-allocating `InverseConditionalCDF(u,t)` on `BivariateCopula` for all families; `CopulaType.Independence` appended; `CopulaFactory` + copula XML serialization; the zero-parameter estimation guard). S2 — `BivariateHazard`: marginals X/Y linked by `IRiskFunction.Id` (lenient name fallback, resolver repair, both serialization modes) to univariate hazard functions already in the analysis; user-set copula defaulting to Independence with fixed parameters; `SecondaryIntegrationBins` default 20, range [3, 1000]; primary/secondary hazard type + unit labels; projected-identity canonical hash folding marginal content (never Ids/names); composite-forward-rule seeding (one component ordinal). S3 — `BivariateTransform` + `BivariateConsequence` deterministic two-way tables on Numerics `Bilinear` with per-axis transforms and a pinned extrapolation policy. S4 — the `BivariateResponse` port: the v1.0 collapse mode fully preserved (stored secondary-hazard link, `WeightedHazardLevel` weights, automatic Voronoi-midpoint weight estimation — re-derivation explicit, never a load side effect) plus the joint surface mode under a bivariate hazard, with new secondary axis type/unit/transform. S5 — the two-port graph surface: the five reserved gates opened, off-path secondary side-input chains with `TryResolveSecondaryChain`, projection stamping of `HazardBinding`/`ConsequenceHazardDimension` from connection ports, `FailureMode.SecondaryHazardToResponse`, and the full wiring/validation matrices incl. every scope guard. S6 — the engine: trapezoid conditional bins in conditional-probability space evaluated inside `SampledComponent.ComputeRisk`, per-(X, Y_j) failure-mode combination then weighted summation, one recorded point per stream per X (mass-ledger gates unchanged), one VEGAS dimension per component, univariate models bit-identical (F1 byte gate as proof), F8 perf fixture. S7 — verification + docs close-out. Nested copulas, copula-parameter uncertainty, and 2-D surface uncertainty are deliberately deferred with loud validation guards.

**Verification:** `BivariateRiskVerification` — the legacy `Test_Bivariate_Risk` oracle (seed 45678, 100M → 1M, documented k·SE tolerances), the `Test_Bivariate_SRP` closed form, the re-anchored `Test_DAMRAE` chained-bilinear oracle, and the collapse-vs-joint consistency cross-anchor between the preserved v1.0 method and the new bivariate-hazard path; `CopulaDependenceVerification` — independence exactness, Normal/Clayton conditional closed forms, the N-bin convergence study (pins default-20 adequacy), the Gumbel tail-orientation pin, and marginal-uncertainty propagation parity; new pins in `EngineReproducibilityVerification` (thread-count bit-identity with bins, marginal-link rename, serialization-mode invariance, §5.5.8 × bins).

**Exit criteria (met 2026-08-07):** the four bivariate types are P/T/V; the five reserved gates are open with the full validation matrices; the F1 byte-gate hash is unchanged (the univariate zero-overhead proof) and all eight fixtures reproduced their pins; F8 is committed; both new families run green isolated (`BivariateRiskVerification` 5, `CopulaDependenceVerification` 6) beside the extended `EngineReproducibilityVerification` (7); the `Test_BivariateRisk.vb` and `Test_DAMRAE.vb` traceability rows are re-classified Covered with the re-anchoring recorded; and the documentation amendments landed (arch doc v0.24 with the §6.1.2/§6.3.1/§6.5/§7.4 rewrites, §5.5.3 and §5.8.4 rows, and Q-Q/Q-R resolved; `docs/technical-reference/bivariate-hazards.md`; both verification pages; the repository guidance matrix and gotchas).

Two close-out results are worth carrying forward. The verification family **found and closed a real Numerics defect** — `GumbelCopula` and `JoeCopula` (the only conditional inverses that solve numerically) threw on conditional levels within a ulp of 1, which crashed any engine run through those families. And the **axis-choice measurement**: because the engine integrates the primary axis exactly and discretizes the secondary, the same joint model can differ by two orders of magnitude in accuracy depending on which variable drives. On the legacy seismic scenario the v1.0 arrangement — stage primary, PGA collapsed through the response's automatic weights — lands 0.241% from the exact reference against 35.4% for the default-bin bivariate arrangement, which is why the preserved collapse mode is a first-class modeling choice rather than a compatibility shim.

## Phase 11B — External imports + LifeSim

**Scope (re-sliced out of Phase 11, 2026-08-03):** `BestFitBivariateHazard` (θ-posterior import — copula-parameter uncertainty arrives here), `BestFitTabularHazard` (coincident-frequency table import; Q-P resolves here), `BestFitTransform` (rating-curve posterior import), `RFAHazard`, `LifeSimConsequence` + `LifeSimResult`. All posterior-import types hold Numerics artifacts only (no `RMC.BestFit.dll`; `.rmcbf`/SQLite reading stays a UI-layer concern). Revisit `BestFitUnivariateHazard` here only if Phase 9 concluded the parametric injection path does not fully supersede it.

**Verification:** BestFit import contract test (deserialize `MCMCResults`/`UncertaintyAnalysisResults` with Numerics alone → construct → evaluate); RFA/LifeSim families per their legacy oracles.

**Exit criteria:** full v1.1 input-function surface P/T/V.

## Phase 12 — Hardening

**Scope:** ≥90% line-coverage gate on `RMC.TotalRisk.dll` from the fast suite; one-off Linux `dotnet build` container check (proves no Windows-only dependency); `examples/` documentation (the two v1.0 `.tra` projects described + a headless model-lib code example); `docs/getting-started.md`; `docs/REMAINING-WORK.md` (created 2026-08-17 — keep current); a BenchmarkDotNet micro-suite over the hot kernels (closed-form CVaR, `SystemConvolution`, the percentile merge-walk interpolator, the sampled compute kernels) as the standing perf-regression net — the Phase 6.5 macro harness (`scripts/perf/PerfHarness`) stays the whole-engine gate. A measured v1.0-versus-v1.1 wall-clock head-to-head was considered and declined (2026-07-29): the architectural case — exact quadrature at equal tolerance, exact convolution, allocation discipline, LHS variance reduction — stands without it.

**Exit criteria:** coverage + Linux gates green; docs live.

## Phase 13 — Release prep

**Scope:** final full-suite verification run (recorded), release notes, version stamping, tag **`v1.1.0-alpha`** of the model library (user pushes/tags).

**Exit criteria:** alpha tag cut.

## Phase 14 — REST API + MCP server

> Depends only on Phases 1–6 (the tabular + mean-only surface); can be executed any time after Phase 6 to unblock the Dam Screening Tool.
>
> **Ruling (2026-08-17):** Phase 14 is inside v1.1 and ships **after** the `v1.1.0-alpha` tag —
> the alpha is the model-library milestone (Phases 8–13) and does not wait on the API.
>
> **Superseded and re-sliced (2026-09-03/04, user directive):** the Dam Screening Tool needed the
> compute surface immediately, so the phase split. **14A — the stateless round-trip compute —
> landed 2026-09-04**: `src/RMC.TotalRisk.Api` + `src/RMC.TotalRisk.Api.Tests` on the template
> below **minus the store** (`POST api/risk-analyses/compute`/`validate`, `GET example`/
> `api/metadata`, OpenAPI, stateless MCP at `/mcp` with `run_risk_analysis`/
> `validate_risk_analysis`/`get_metadata`/`get_example_request`, the DST model shape —
> deterministic tabular hazard/responses, day/night `compositeMixture` consequences, definable
> failure-mode list, unadjusted + adjusted marginal mode results with contributions — 73 tests
> incl. the EAD closed-form golden, byte-identical reproducibility over HTTP, and the MCP
> JSON-RPC `tools/call` round trip; `docs/api.md` live; Q-F resolved via `validationIssues`).
> **14B (deferred):** the store-backed resource lifecycle below, full-uncertainty ensemble result
> mapping, the wider function-kind catalog on the wire, containerization + auth when deployment
> demands them. The alpha sequence itself is unchanged.

**Scope (the 14B remainder keeps this template):** `src/RMC.TotalRisk.Api` + `src/RMC.TotalRisk.Api.Tests`, mirroring `RMC.BestFit.Api` (confirmed template: one ASP.NET Core `Microsoft.NET.Sdk.Web` net10.0 project hosting **both** surfaces):

- **Packages:** `ModelContextProtocol.AspNetCore`, `Microsoft.AspNetCore.OpenApi`, `Microsoft.OpenApi` (central package management; nuget.org).
- **Layering (all DI singletons; state in the store):** `Store/` (`IResourceStore`/`InMemoryResourceStore`: GUID-keyed concurrent dictionaries, capacity cap, per-resource `RunLock`, no eviction) → `Services/` (stateless facades shared verbatim by controllers and MCP tools; run orchestration: per-resource lock → 409, global `SemaphoreSlim(MaxConcurrentRuns)` throttle, typed exceptions, run-state machine) → `Mappers/` (model/Numerics → flat DTOs; multi-dim arrays decomposed to parallel lists; NaN→null diagnostics) → `DTOs/` (`Create*Request`; responses derive from a shared `ResponseBase`: success/errorMessage/validationErrors/validationWarnings/computationTimeMs/timestamp/nonFiniteFindings) → thin attribute-routed controllers over `ApiControllerBase.ExecuteAsync` (timing, finite-audit rejecting ±Infinity → 500, exception→status mapping 400/404/409/499/500).
- **Endpoints per resource kind** (input functions, system components/failure modes, risk + reliability analyses): `POST` create / `POST {id}/run` (synchronous; CancellationToken wired to `CancelAnalysis`) / `GET` list / `GET {id}` / `GET {id}/results` / `GET {id}/validate` / `DELETE {id}`; function uncertainty previews via `ComputeUncertaintyResults`; plus metadata/health/`/api/info`; OpenAPI at `/openapi/v1.json` via `AddOpenApi()`.
- **JSON:** camelCase, `JsonStringEnumConverter(camelCase)`, ignore-null, `AllowNamedFloatingPointLiterals` (NaN legal; ±Infinity rejected by the finite auditor).
- **MCP:** `AddMcpServer().WithHttpTransport(o => o.Stateless = true).WithTools<...>()` + `app.MapMcp("/mcp")`; one `[McpServerToolType]` class per area; snake_case tool names; flat primitive params with `[Description]`; `McpJson` helper mirroring the REST JSON options; consider collapsed `run_analysis`/`get_analysis_results` dispatching on stored kind.
- **Tests:** `WebApplicationFactory<Program>` in-process integration suite + raw JSON-RPC `tools/list` smoke against `/mcp`; unit tests per DTO/mapper/controller/service. **Docs:** `docs/api.md` in BestFit's structure (concepts → endpoint tables → MCP section with client registration + tool list + agent chains).
- **Known gaps to plan as net-new work when deployment demands:** BestFit ships no Dockerfile/AWS artifacts and configures no auth scheme — containerization and auth are additive tasks for the Dam Screening Tool deployment.

**Exit criteria:** API + MCP green in-process test suite; OpenAPI served; `docs/api.md` live.

## Future phases (placeholders — planned when the model lib stabilizes)

- **UI layer** — `RMC.TotalRisk.UI` (net10.0-windows): project model, element wrappers (the "element" vocabulary lives here), `.tra` (SQLite) + `.rmcbf` reading, v1.0-project import mapping onto the v1.1 analysis API, data binding on the model lib's INPC surface. Presentation-layer diagnostics recorded as UI-layer scope during the model-library work: TRG-line comparison data (chart furniture, not compute), KDE-smoothed assurance banding over the engine's exact assurance outputs, and batch-run orchestration.
- **Desktop App** — the WPF shell ported from the legacy VB app.
- **`CostBenefitAnalysis`** — **moved onto the v2.0 critical path; design ratified 2026-09-06** ([requirements/COST_BENEFIT_ANALYSIS_DESIGN.md](requirements/COST_BENEFIT_ANALYSIS_DESIGN.md), the C5 capability): the alternatives-comparison, constrained discrete-optimization (ε-constraint with total trade-off shadow prices), and decision-analysis layer — a serialized study owning a collection of `RiskReductionAlternative`s with a designated baseline, the metric formulary (TEAC/NPV/BCR + the CSSL family + AFP), the three-tier decision-strategy catalog, and the presentation-complete results contract. Implementation sessions CB1–CB4 per the design's phase table; **the exhaustive testing campaign follows CB4**. The v1.0 `PlanRow.EquivalentAnnual` math is the porting parity anchor (`PlanEconomics`, design decision 19).
- **FDA importer + datasets** — port `FDAImporter_143`; bring the HEC-FDA comparison datasets in-repo via a `docs/verification-requests/` request; convert the FDA integration oracles.
- **Cross-engine parity** — the levee "same results" test against Hydrologics' event-based risk layer, in a verification-only project referencing both (see [requirements/SHARED_FUNCTIONS_STRATEGY.md](requirements/SHARED_FUNCTIONS_STRATEGY.md) §7).
- **`SystemModel` (the `RMC.TotalRisk.Systems` root)** — the multi-component system container (the Hydrologics `BasinModel` analog) the namespace root is reserved for. Ruled post-v1.1 (2026-08-17): the reservation stays, and `RiskAnalysis`-owns-components remains the v1.1 system representation.
