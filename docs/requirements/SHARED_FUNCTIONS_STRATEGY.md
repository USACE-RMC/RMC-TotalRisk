# Shared Risk Input Functions — Cross-Repo Strategy

> **Status:** v1.0, ratified 2026-07-19. Moved to its authoritative home (`docs/requirements/` in the RMC-TotalRisk repo) on 2026-07-20; the copy at the `RMC-TotalRisk-Dev` root is frozen with a pointer here, and relative links to Dev-repo files (ROADMAP/MEMORY/CLAUDE) refer to that repo. Companion to [MODEL_LIBRARY_ARCHITECTURE.md](MODEL_LIBRARY_ARCHITECTURE.md) v0.7, which carries the TotalRisk-side amendments. **v1.1 (2026-07-23):** the §4 table gains the risk-engine follow-ups **N7–N9** (raised by TotalRisk Phases 4/4b; every other doc already used these numbers) and the release item is renumbered **N10**, resolving the duplicate-N7 collision. **v1.2 (2026-07-25):** the §4 table gains **N11** (pooled `IndependentExclusive` outputs + truncation diagnostic — raised by TotalRisk Phases 6/6.5 and ratified into the 2.2.0 scope), and **N1 is amended**: `ToXElement()` joins the concrete function classes + a new `UnivariateFunctionFactory`, NOT the `IUnivariateFunction` interface — external implementors exist (TotalRisk's `ClampedPowerFunction`/`CompositeUnivariateFunction`) and net481 has no default-interface-member support, so an interface member would be a breaking change the minor bump forbids. **v1.3 (2026-10-01):** D4 is amended for hazards. A hazard function that Hydrologics' own stochastic drivers consume lives in `Hydrologics.dll` under `Hydrologics.RiskFunctions.Hazards`, not in the future `Hydrologics.Risk` assembly (§6, Hydrologics).
>
> **Audience:** future working sessions in any of the four sibling repos (`C:\GIT\numerics`, `C:\GIT\RMC-TotalRisk-Dev`, `C:\GIT\Hydrologics`, `C:\GIT\rmc-bestfit`), and the user coordinating releases. This document is the single source of truth for *what is shared, where it lives, and who does what next*.

---

## 1. The decision

RMC-TotalRisk and Hydrologics both require the four risk **input function** families — hazard (frequency distribution), transform, response (fragility), and consequence — and equivalent problems (e.g., a simple river levee analysis) must produce the same results in both tools. Rather than implementing these twice or standing up a new shared package, the shared home is **Numerics** — the one library every sibling repo already references.

Ratified decisions (2026-07-19):

| # | Decision |
|---|---|
| D1 | **Shared library = an expansion of `Numerics.Functions`** (plus small fixes elsewhere in Numerics). Domain vocabulary — `IHazardFunction` / `ITransformFunction` / `IResponseFunction` / `IConsequenceFunction`, event trees, LifeSim import — stays **per-repo** as thin, zero-math adapter layers. |
| D2 | **`RMC.TotalRisk.dll` drops the planned `RMC.BestFit.dll` reference.** BestFit interop is *data import via Numerics artifacts* (§5). Reading `.rmcbf` files (SQLite) is a UI-layer concern. The model lib's dependency red line becomes identical to Hydrologics': **Numerics only.** |
| D3 | **Canonical hashing uses XML canonicalization** — SHA-256 over each type's `ToXElement()` after an audited strip-rule pass (the Hydrologics `CanonicalContentHasher` + `CanonicalizationRules` pattern, itself adapted from TotalRisk's §5.5 spec). The per-class `WriteCanonical` binary writers of arch-doc v0.5 are retired. Seeding semantics (SHA-256 content hash + occurrence index + `HashCombine`) are unchanged. |
| D4 | **Hydrologics' event-based risk layer** will be a future, separate **`Hydrologics.Risk`** assembly (references `Hydrologics` + `Numerics`). `Hydrologics.dll`'s only-Numerics red line is untouched. *(Amended 2026-10-01 for hazards; see §6, Hydrologics.)* |
| D5 | **Numerics consumption:** sibling `HintPath`/`ProjectReference` during co-development; once the expanded Numerics ships as a package release, Hydrologics and TotalRisk migrate to the **`RMC.Numerics` NuGet package** (central-package-managed, like BestFit already does). |

## 2. Why (condensed rationale)

1. **The heavy math is already shared.** The legacy TotalRisk input-function classes are ~10–35% portable math; their compute bodies literally construct Numerics objects (`LinearFunction`, `PowerFunction`, `TabularFunction`, `EmpiricalDistribution`, `CompetingRisks`, `UncertainOrderedPairedData.CurveSample`, `BootstrapAnalysis`). The real decision was where the *uncertain-function layer* lives — and that layer is where silent numerical drift happens (extrapolation policy, monotonicity forcing, co-monotonic sampling, tail handling).
2. **BestFit's fitted results are already pure Numerics objects** (§5). No repo needs `RMC.BestFit.dll` to consume a fitted frequency distribution, rating curve, or copula — which dissolves the dependency asymmetry that made a shared library awkward.
3. **Both repos deliberately converged already**: Hydrologics' ratified seeding spec (`docs/requirements/identity-and-seeding.md`) adapts TotalRisk's §5.5; both use INPC, `(bool, List<string>) Validate()`, XElement/G17 serialization, LHS-default epistemic sampling, and the same test-project architecture.
4. **Alternatives rejected:** *write twice* (perpetual drift-policing between two life-safety tools); *new `RMC.RiskFunctions` package* (a fourth library when the user wants to minimize libraries); *TotalRisk-as-shared-lib* (drags the MC engine + BestFit dependency into Hydrologics' graph).
5. **Timing:** TotalRisk Phase 2 porting has not started — the clean function types don't exist yet, so pointing the port at Numerics costs nothing extra. Extracting shared code later would be a breaking refactor.

## 3. Architecture after this strategy

```
Numerics.dll  (RMC.Numerics — net10.0;net9.0;net8.0;net481; public NuGet + local feed)
  ├─ Numerics.Functions        ← EXPANDED: uncertain-function toolkit (§4)
  ├─ Numerics.Data             ← OrderedPairedData / UncertainOrderedPairedData (unchanged)
  ├─ Numerics.Distributions    ← + EmpiricalDistribution XElement round-trip fix
  └─ Numerics.Sampling         ← LatinHypercube / Stratify (unchanged)
        ▲                ▲                    ▲
        │                │                    │
RMC.BestFit.dll    Hydrologics.dll     RMC.TotalRisk.dll        ← ALL reference Numerics ONLY
(fits functions;   (physics engine;    (risk engine; thin
 exports Numerics   thin domain         domain adapters)
 artifacts)         adapters later
                    in Hydrologics.Risk)
        │                                     ▲
        └──── fitted artifacts as DATA ───────┘
              (ParameterSet[], UncertaintyAnalysisResults,
               UncertainOrderedPairedData — no DLL reference)
```

**The scope line** (D1): what goes where.

| Layer | Contents | Home |
|---|---|---|
| Math + uncertainty machinery | Function forms, serialization, posterior-ensemble sampling, composites, paired-data sampling, bootstrap, LHS/stratification | **Numerics** |
| Domain vocabulary | Axis labels (`SpecifiedHazard`/`HazardUnit`), the four interfaces, validation messages, canonical-hash strip rules, `SetupSampler` orchestration, event trees, LifeSim import, bivariate hazard wiring | **Per-repo** (RMC.TotalRisk now; Hydrologics.Risk later) |
| Engine traversal | Frequency-domain integration (TotalRisk) vs event-based total probability (Hydrologics) | **Per-repo, intentionally different** |

Domain names (hazard/response/consequence) deliberately stay **out** of Numerics: it is a public, academic-facing math library, and "hazard function" there already means the survival-analysis HF(x).

## 4. Numerics expansion work plan ("Phase 2.0")

Executed in `C:\GIT\numerics` on branch `bug-fixes-and-enhancements` (currently at v2.1.4). All items follow Numerics conventions: XML docs on everything (CS1570–CS1591 are build **errors**), one class per file, `G17` + `InvariantCulture` formatting, tests in `Test_Numerics` mirrored folders, **all four TFMs must compile (`net10.0;net9.0;net8.0;net481` — mind net481 API limits)**.

| # | Item | Detail |
|---|---|---|
| **N1** | Function serialization + factory | New `UnivariateFunctionType` enum (append-only, like `UnivariateDistributionType`). **(Amended v1.2)** Add concrete `XElement ToXElement()` methods on `LinearFunction`, `PowerFunction`, `TabularFunction` (+ new types below) — **NOT on the `IUnivariateFunction` interface** (external implementors exist in TotalRisk, and net481 lacks default interface members; an interface member would be breaking). `TabularFunction` embeds its `UncertainOrderedPairedData.SaveToXElement()`. New `UnivariateFunctionFactory` with `CreateFunction(UnivariateFunctionType)` + `CreateFromXElement(XElement)` + a `GetFunctionType(IUnivariateFunction)` type-test helper (the enum stays off the interface) — mirror the existing `LinkFunctionFactory` pattern (`Numerics\Functions\Link Functions\LinkFunctionFactory.cs`). `ConfidenceLevel` is runtime sampling state, never serialized. |
| **N2** | `SegmentedPowerFunction` (working name) | The "expand the simple power function" item. Segmented power form matching BestFit's BaRatin matrix-of-controls **addition mode**: `Q(h) = Σₖ αₖ·(h−ξₖ)^βₖ·𝟙{h>activation}`, 1–N segments, log-space σ residual. **Parameter-vector layout must be verified against and kept compatible with `C:\GIT\rmc-bestfit\src\RMC.BestFit\Models\RatingCurve\RatingCurve.cs`** (reported as `[h₁, log₁₀α₁, β₁, …, σ]`, length `3·segments + 1`) so a posterior `ParameterSet.Values` applies via `SetParameters` directly. Numeric `InverseFunction` via monotone bracketing/root find. Degenerates to the existing `PowerFunction` at one segment. |
| **N3** | `CompositeFunction` | Weighted combination over `IUnivariateFunction[]` + weights — **weighted-average** and **mixture** modes (the math behind TotalRisk's `CompositeTransform`/`CompositeConsequence`). Distribution-side analogs `Mixture`/`CompetingRisks` already exist and set the serialization idiom (`FromXElement` static + `ToXElement` override, children created via factory). |
| **N4** | Posterior-ensemble sampling | `EnsembleFunction` (shipped under that name): a template `IUnivariateFunction` + `ParameterSet[]` posterior draws. **Pure** `IUnivariateFunction Sample(int index)` and `Sample(double percentile)` returning configured clones — thread-safe by construction, avoiding the mutable `ConfidenceLevel` idiom inside `Parallel.For` hot loops. This is how an imported BestFit rating curve carries knowledge uncertainty into either engine. |
| **N5** | `EmpiricalDistribution` XElement round-trip fix | Today `ToXElement()` (base-class virtual) writes only scalar parameters — the X/P tables are lost. Override `ToXElement()` (tables + sort orders + transforms), add `FromXElement`, and wire into `UnivariateDistributionFactory.CreateDistribution(XElement)` alongside the existing `Mixture`/`CompetingRisks`/`PertPercentile` special cases. Check `KernelDensity` for the same gap while there. |
| **N6** | Tests + docs | Round-trip tests for every new/changed serialization surface; `SegmentedPowerFunction` parity fixture vs BestFit `RatingCurve.Predict` reference values; new `docs/functions/` user-guide page (the Functions namespace is currently undocumented in `C:\GIT\numerics\docs\`). |
| **N7** | AGK weight-exposing overload | Risk-engine follow-up (TotalRisk Phase 4): an `AdaptiveGaussKronrod` integrand overload that hands the Kronrod weight to the callback, so LEC probability mass comes from the quadrature directly (retires the midpoint-trapezoid fallback). Shipped as the acceptance-aware `Recorder` in Phase 8; **adopted by the engine in Phase 8.5** (`QuadratureMassLedger`). Detail: RMC-TotalRisk [ROADMAP Phase 8](../ROADMAP.md). |
| **N8** | `Convolve` upgrades | Risk-engine follow-up (TotalRisk Phase 4b): a log-spaced / adaptive-grid option and an atom-aware discrete/mixed-distribution overload of `EmpiricalDistribution.Convolve` (the engine's exact lattice kernel `SystemConvolution` migrates onto it). Detail: ROADMAP Phase 8. |
| **N9** | Vegas Jacobian unit tests | Risk-engine follow-up (TotalRisk Phase 4b): upstream unit tests confirming the power-transform Jacobian folds into `wgt` at γ ∈ {1, 4, 10} (the engine-level empirical audit is already green). Detail: ROADMAP Phase 8. |
| **N10** | Release | Ship as **`RMC.Numerics` 2.2.0** (additive → minor bump) to the local feed `C:\GIT\numerics\packages` via `dotnet pack ... /p:Version=2.2.0 -o ./packages`. This release also satisfies Hydrologics' recorded packaging blocker ("revert to PackageReference once a release > 2.1.2 lands"). |
| **N11** | Lazy and pooled exclusive probability enumeration | Risk-engine follow-up completed for Numerics 2.2.0 and adopted by TotalRisk Phase 8.6. `Factorial.AllCombinationsLazy` establishes subset-size/lexicographic order. `Probability.IndependentExclusiveLazy`, `PositivelyDependentExclusiveLazy`, and `ExclusivePCMLazy` fill caller-owned probability/indicator buffers, reuse rows, and return `ExclusiveEnumerationStatus`; `UnionPCMLazy` removes the convenience union's dense materialization. Dense overloads remain for compatibility and parity tests. PCM formulas, association order, closing half-gap row, the dual convergence predicate, and default tolerances (`1E-4`) are unchanged. TotalRisk no longer populates dense failure-mode caches; it clips the emitted exclusive partition sequentially against the remaining unit budget without proportional normalization. |

## 5. BestFit import contract (no BestFit.dll in any model lib)

Confirmed by inspection of `C:\GIT\rmc-bestfit` (2026-07-19): every function-producing analysis already exposes its fitted result as pure Numerics objects, and the `.rmcbf` project file (SQLite) stores those artifacts in **Numerics-native serialization in dedicated columns**, separate from the BestFit-typed configuration.

| Fitted artifact | Numerics representation | Deserialization |
|---|---|---|
| Frequency distribution (univariate analyses) | `UnivariateDistributionBase` (+ `UncertaintyAnalysisResults` with `ParentDistribution`, `ParameterSets`, curves, CIs) | `UncertaintyAnalysisResults.FromXElement` / `UnivariateDistributionFactory.CreateDistribution(XElement)` |
| MCMC posterior | `MCMCResults` → `IList<ParameterSet>` | `MCMCResults.FromByteArray(Tools.Decompress(bytes))` |
| Rating curve | after N2: `SegmentedPowerFunction` + `ParameterSet[]` → `EnsembleFunction` | posterior via `MCMCResults` as above |
| Bivariate / copula | Numerics copula + marginal `UnivariateDistributionBase`s + `MCMCResults`; joint curve as `UncertainOrderedPairedData` | existing Numerics ctors/factories |
| Coincident frequency | X/Y/Z primitive arrays + `EmpiricalDistribution` | after N5 round-trip fix |

Division of responsibility:

- **Model libs** (`RMC.TotalRisk.dll`, future `Hydrologics.Risk`): accept **already-parsed** Numerics artifacts via constructors/properties. No file I/O, per the headless rules.
- **UI layers** (`RMC.TotalRisk.UI` in Phase 3; BestFit's own UI already does this via `AnalysisPersistenceHelper`): read the `.rmcbf` SQLite columns (`MCMCResults` byte[] compressed, `AnalysisResults` XElement string), deserialize with Numerics, hand objects to the model lib.
- **BestFit follow-up (later, its own repo):** delegate `RatingCurve.Predict` to `SegmentedPowerFunction` so the functional form has a single implementation. Until then, N6's parity fixture pins the two implementations together.

## 6. Repo-by-repo consequences

### RMC-TotalRisk (`C:\GIT\RMC-TotalRisk`; porting source `C:\GIT\RMC-TotalRisk-Dev`)

- [MODEL_LIBRARY_ARCHITECTURE.md](MODEL_LIBRARY_ARCHITECTURE.md) amended to **v0.6** (same session as this document): §5.5 hashing mechanics → XML canonicalization; §8 dependency graph → Numerics only; BestFit* types → posterior-import types; §9 gains Phase 2.0; transform cluster documented as thin wrappers.
- Phase 2 port order becomes: **2.0 Numerics expansion → 2.1 Hazards → 2.2 Transforms (now small) → 2.3 Responses → 2.4 Consequences → 2.5 Engine → 2.6 Cleanup.**
- Canonical hashing implemented once as `CanonicalContentHasher` + audited `CanonicalizationRules` (adapt from `C:\GIT\Hydrologics\src\Hydrologics\Core\CanonicalContentHasher.cs` / `CanonicalizationRules.cs`), applied to every model type's `ToXElement()`.
- Numerics reference: sibling `HintPath` (per current CLAUDE.md) while co-developing Phase 2.0; switch to `RMC.Numerics` PackageReference when 2.2.0 ships, then update CLAUDE.md.

### Hydrologics (`C:\GIT\Hydrologics`) — no action required now

- When 2.2.0 ships: revert the three csproj files from the interim `ProjectReference` to the `RMC.Numerics` PackageReference (already tracked in its `docs/REMAINING-WORK.md`).
- Future **`Hydrologics.Risk`** assembly consumes the Numerics toolkit + its own thin domain adapters, fed by the existing per-event observer seam (`IStochasticEventObserver`).
- Existing physics-internal curves (`DischargeElevationCurve`, `SystemResponseFunction`, `ElevationLossCurve`) are **unchanged** — they serve routing/diagnostics inside the O(1) hot path, not risk accounting. Optionally, the junction rating/loss sources adopt the richer Numerics function types later.
- **D4 amendment for hazards (2026-10-01).** Precipitation, flow and volume frequency curves are hazard functions that `Hydrologics.dll`'s own stochastic drivers consume. They therefore live in `Hydrologics.dll`, under `Hydrologics.RiskFunctions.Hazards`, and not in the future `Hydrologics.Risk` assembly:
  - `FrequencyCurve`: one parametric model class with an explicitly estimated, stored ensemble (plain or linked asymptotic normal, parametric bootstrap, or an imported BestFit posterior);
  - `TabularFrequencyCurve`: the co-monotonic uncertain table;
  - the drivers' `IHazardFunction` slot.

  The model follows this strategy's hazard pattern without copying TotalRisk code: method wrappers live outside the model library, summaries are Numerics `UncertaintyAnalysisResults` (the mean curve is E[F(x)]), identity is a Guid, and `Estimate()` is explicit. Hashing is XML canonicalization under an audited `FrequencyCurveRules` (D3). The shared math stays in Numerics (D1), and `Hydrologics.dll` still references only Numerics. The physics-internal curves above (`DischargeElevationCurve`, `ElevationLossCurve`, `SystemResponseFunction`) are unchanged for now. Moving them into RiskFunctions is a later Hydrologics sub-project with its own spec. The event-based risk layer of D4 (`Hydrologics.Risk`) is otherwise unchanged. Recorded on the Hydrologics side in `docs/requirements/stochastic-simulation.md` §15.

### RMC-BestFit (`C:\GIT\rmc-bestfit`) — no action required now

- Later follow-up: `RatingCurve.Predict` delegates to `SegmentedPowerFunction` (single implementation of the functional form). Bump to `RMC.Numerics 2.2.0` when convenient.

## 7. Verification strategy

1. **Numerics level (now):** unit + round-trip tests for every N1–N5 surface across all four TFMs; `SegmentedPowerFunction` vs BestFit `RatingCurve.Predict` parity fixture (reference values generated from BestFit source).
2. **Import contract (Phase 2.1+):** serialize `MCMCResults`/`UncertaintyAnalysisResults` from a real BestFit run → deserialize with Numerics alone → construct the import types → evaluate. Proves the no-BestFit-DLL path end to end.
3. **Cross-engine flagship (later, once `Hydrologics.Risk` exists):** the same levee inputs (hazard + rating transform + fragility + consequence) through TotalRisk's frequency-domain engine and Hydrologics' event-based engine → same annualized risk within sampling tolerance. Lives in a verification-only project that references both (test projects are not bound by the runtime dependency red lines). This is the "kill two birds" payoff test.

## 8. Sequencing (as executed)

1. **2026-07-19:** this document + the TotalRisk doc amendments (arch doc v0.6, ROADMAP, CLAUDE.md dependency notes).
2. **Numerics implementation (complete 2026-07-25/26):** N1–N9 and N11 on `bug-fixes-and-enhancements`; TotalRisk consumes via HintPath in the interim.
3. **TotalRisk port** per the amended architecture doc (the executed phase order is [../ROADMAP.md](../ROADMAP.md)).
4. **Release train (pending):** `RMC.Numerics 2.2.0` → local feed → Hydrologics + TotalRisk switch to PackageReference; BestFit bumps when convenient.
5. **Later:** `Hydrologics.Risk`; BestFit rating-curve delegation; cross-engine parity test.

## 9. Open items

| ID | Item |
|---|---|
| S-1 | Verify `SegmentedPowerFunction` parameter layout against `RatingCurve.cs` before implementing (N2). Decide final type name (`SegmentedPowerFunction` vs `RatingCurveFunction` — lean generic for the public math library). |
| S-2 | `EnsembleFunction` API design: `Sample(int index)` index-wrap policy when posterior size < realization count (mirrors arch-doc Q-M); percentile→draw mapping (nearest-rank vs weighted). |
| S-3 | `KernelDensity` XElement round-trip — same gap as `EmpiricalDistribution`? Fix or flag during N5. |
| S-4 | `CanonicalContentHasher` mechanism could eventually move to `Numerics.Utilities` so TotalRisk and Hydrologics share the hasher (each keeps its own audited rules). Deferred — TotalRisk copies the pattern for now; record in Hydrologics' `upstream-numerics-flags.md` style when proposed. |
| S-5 | Whether TotalRisk's per-ordinate uncertain types need any `UncertainOrderedPairedData` extensions (e.g., envelope validation flags noted in Hydrologics' upstream flags #12). Evaluate during Phase 2.1. |

## 10. Reference index

| Repo | Key files for this strategy |
|---|---|
| `C:\GIT\numerics` | `Numerics\Functions\IUnivariateFunction.cs`, `LinearFunction.cs`, `PowerFunction.cs`, `TabularFunction.cs`, `Link Functions\LinkFunctionFactory.cs` (factory pattern), `Numerics\Data\Paired Data\UncertainOrderedPairedData.cs`, `Numerics\Distributions\Univariate\EmpiricalDistribution.cs`, `...\Uncertainty Analysis\UncertaintyAnalysisResults.cs`, `Numerics\Mathematics\Optimization\Support\ParameterSet.cs`, `packages\` (local feed) |
| `C:\GIT\rmc-bestfit` | `src\RMC.BestFit\Models\RatingCurve\RatingCurve.cs` (functional form + parameter layout), `src\RMC.BestFit.UI\Elements\Support\AnalysisPersistenceHelper.cs` (.rmcbf column serialization), `src\RMC.BestFit\Analyses\Support\IBayesianAnalysis.cs` |
| `C:\GIT\Hydrologics` | `src\Hydrologics\Core\CanonicalContentHasher.cs`, `Core\CanonicalizationRules.cs`, `docs\requirements\identity-and-seeding.md`, `docs\REMAINING-WORK.md` (PackageReference revert item) |
| `C:\GIT\RMC-TotalRisk-Dev` | the frozen architecture/strategy copies, `ROADMAP.md` and `MEMORY.md` at that repo's root, legacy clusters under `RMC-TotalRisk\RMC.TotalRisk.IO\Project\Elements\` |
