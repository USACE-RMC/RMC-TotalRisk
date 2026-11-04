# Bivariate Risk Analysis Design (Phase 11)

**Status: v1.0, ratified 2026-08-03.** The normative design for the Phase 11 bivariate capability —
requirements, architecture, session slicing, testing, and verification. Ratified by Haden Smith at
the 2026-08-03 design review, including the twelve numbered decisions (two revised at review:
minimum secondary bins raised to 3; the `BivariateResponse` stored secondary-hazard link and v1.0
collapse mode preserved) and the conditional-discretization algorithm (decision 7). Built from
three parallel code explorations (this repo; the legacy v1.0 repo at `C:\GIT\RMC-TotalRisk-Dev`;
Numerics + RMC-BestFit) plus two design passes (model library/graph; engine/verification), all
anchored to file:line as of 2026-08-03 — line anchors drift with edits, so re-locate by symbol when
stale. Nothing here is implemented yet; sessions S1–S7 below execute it. Where this document and
[MODEL_LIBRARY_ARCHITECTURE.md](MODEL_LIBRARY_ARCHITECTURE.md) §6.1.2/§6.3.1/§6.5/§7.4 disagree,
this document wins for the bivariate scope; the formal arch-doc amendments land at session S7.

## Context

RMC-TotalRisk v2.0 (headless model library, branch `v2.0-development`, named `v1.1-development` when this document was ratified) currently supports univariate
hazard chains only. Phase 11 adds full bivariate risk analysis:

- **Bivariate hazard** — new, copula-based, following the RMC-BestFit bivariate analysis concept:
  user links marginal X (primary) and marginal Y (secondary) to existing *univariate* hazard
  functions in the analysis, selects a copula from Numerics (default: a new **Independence copula**,
  to be added to Numerics), sets primary/secondary hazard types + units, and sets the number of
  secondary trapezoidal integration bins (default 20, max 1000). Primary hazard is integrated with
  AGK (1D) / VEGAS (joint); secondary is integrated with conditional trapezoid bins per primary slice.
  Example: primary = seismic PGA frequency; secondary = reservoir pool duration; independence copula.
- **Bivariate transform / bivariate consequence** — two-way tables z = f(x, y) using the Numerics
  bivariate (bilinear) interpolation class.
- **Bivariate response** — port the v1.0 bivariate response functionality/behavior first, then
  augment it to be driven by the new bivariate hazard.
- **Risk graph** — hazard node can be bivariate with **two output ports** (primary, secondary).
  Bivariate transform/response/consequence connect to both ports at once and cascade; univariate
  functions may connect to either single port (e.g., bivariate response on both ports + univariate
  consequence on the secondary pool port, since consequences are typically f(pool), not f(PGA)).
- Scope: full Phase 11. BestFit `.rmcbf` import and RFA hazard remain deferred to future phases.
- Deliverable of THIS session: this design plan (requirements, architecture, phases, testing,
  verification, tech-ref + verification doc updates) as a solid hand-off; implementation happens in
  the next sessions.

## Exploration digest (facts)

### Current repo (v1.1) — explored 2026-08-03

**The v1.1 codebase already reserves a bivariate seam (arch doc v0.9 "item 7"); zero serialized-shape
breaks are needed.** Confirmed in code:
- `Core\Enums\HazardDimension.cs` — `HazardDimension { Primary = 0, Secondary = 1 }`, pinned by test.
- `FailureMode.HazardBinding` + `FailureMode.ConsequenceHazardDimension` (both `HazardDimension`):
  serialized by name (`FailureMode.cs:774-775`), read with defaults (`:109-110`), INPC, hashed;
  `Validate()` currently REJECTS `Secondary` for both (`FailureMode.cs:602, 606`) pending bivariate.
- `RiskConnection.SourcePort` (int, immutable, in Equals/HashCode) — serialized as `{kind}Port` by
  `RiskElementBase.cs:441`; doc: "0 for every univariate source; 1 = HazardDimension.Secondary".
- `ResponseElement.SecondaryInput : RiskConnection?` + pending `SecondarySource*` attribute triple
  (`ResponseElement.cs:81, 183, 445`), cleared by graph detach (`ComponentGraph.cs:914`), included in
  hazard-source bookkeeping (`ComponentGraph.cs:565`), with strip rules registered.
- `ComponentGraph` validates `connection.SourcePort >= Source.OutputCount` (`ComponentGraph.cs:941-943`).
- `SystemComponent` projection ALREADY stamps
  `mode.ConsequenceHazardDimension = terminal.HazardSource.SourcePort == 1 ? Secondary : Primary`
  (`SystemComponent.cs:1761-1763`); exit-port plumbing at `:1691`, `:1812`; a
  `ConsequenceHazardDimension == Secondary ? 1 : 0` use at `:837`.
- `HazardSourceOption(IRiskElement Element, int OutputPort, int ChainPosition, string HazardLabel,
  string HazardUnit)` — `OutputPort` docs say "0 until bivariate hazards are introduced"; returned by
  `ComponentGraph.GetAvailableHazardSources` (picker and validator share it).
- `IHazardFunction` (Core\Interfaces) — `FunctionType` discriminator + `SampleFunction()` /
  `(double percentile)` / `(int realizationIndex)` all returning `IUnivariateDistribution` +
  `MinHazard(bool meanOnly)` / `MaxHazard(bool meanOnly)`. `IUnivariateHazardFunction` is an EMPTY
  MARKER whose docs say the bivariate contract "extends IHazardFunction separately".

**Five gates to open (per the explorer's closing summary) and nothing else in persistence:**
1. `HazardElement.OutputCount` (`Graph\HazardElement.cs:116`) → arity-derived (2 when bivariate).
2. `ResponseElement.InputCount` (`Graph\ResponseElement.cs:213`) → arity-derived.
3. `ResponseElement.Validate()` secondary-input rejection (`:410-413`) → conditional on bivariate response.
4. `FailureMode.Validate()` two Secondary rejections (`FailureMode.cs:602, 606`) → conditional on parent
   hazard being bivariate.
5. `ComponentGraph.GetAvailableHazardSources` (`ComponentGraph.cs:434-444`) port≠0 label branch → from
   MarginalY.

**Three genuinely new engineering items (explorer's summary):**
- `SampledComponent.Hazard` is a single `IUnivariateDistribution` (`Results\SampledComponent.cs:113,
  546`); ~20 engine hazard-operation sites read it. Needs a parallel secondary accessor or a
  bivariate-aware shape.
- `SampledFailureMode.SRP(double)` / `ConsequenceInput(double)` are scalar seams
  (`Results\SampledFailureMode.cs:373, 429`); the flattened `_stageTransforms` +
  `_stageTransformOffsets` layout is where an X-chain/Y-chain split lives.
- `RiskPoint` carries scalar hazard coordinates (`Results\RiskPoint.cs:84-99`); `Curve.CreateProfiles`
  sorts on `HazardLevel` (`Results\Curve.cs:1010-1026`). §7.4 marginalizes Y inside the integrand
  (weights sum to 1 per X) so `RiskPoint`/curves stay unchanged — keep that property.

**Graph walking:** `ComponentGraph.GetUpstreamPath` follows the PRIMARY input only
(`ComponentGraph.cs:395-409` via `PrimaryInputSource`) — secondary edges are invisible to path
walking, projection, and path-based validation. Decision needed: secondary edges stay OFF-PATH side
inputs (validated separately) vs multi-parent walking (ripples into `BuildFailureMode`,
`EndStateGroupLayout`, `ResponseNodes` identity). → Decision: off-path side inputs (see Decisions).

**Seeding:** `SystemComponent.SetupSamplers` (`SystemComponent.cs:1254-1265`) seeds the hazard at
ordinal 0 and advances once. A bivariate hazard must own its marginals'/copula's samplers INTERNALLY
(the `CompositeHazard` precedent, `CompositeHazard.cs:397`; forward rule verbatim at
`SystemComponent.cs:1196-1199`: subtree members absorbed by the dedup set) — one ordinal, one
`SetupSampler` call, or every existing model's seeds move.

**Arch doc prior bivariate design (docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md):**
- §6.1.2 (lines 903-941): three planned types (`ParametricBivariateHazard` = user-set copula +
  marginals, `BestFitBivariateHazard` = posterior import, `BestFitTabularHazard` = coincident-table
  import) + `IBivariateHazardFunction : IHazardFunction` with `MarginalX/MarginalY :
  IUnivariateHazardFunction`, `SecondaryIntegrationBins` (doc default 50 — USER OVERRIDES: default 20,
  max 1000), and `IReadOnlyList<(double Y, double Weight)> SampleConditionalYGivenX(int
  realizationIndex, double xHazardLevel)` (weights sum to 1). `SampleFunction(idx)` returns the X
  marginal; engine switches on `hazard is IBivariateHazardFunction`.
- §6.3.1 (1052-1077): `BivariateResponse` extension — `PrimaryHazardType/Unit`,
  `SecondaryHazardType/Unit`, a `BivariateSurface`, `SurfaceProbability(x,y)` bilinear; validation
  ties axis labels to marginal hazard types; FM exposes consequence binding.
- §6.5 (1167-1201): the dimensional-binding table (univariate ⇒ all FMs Primary; bivariate + univariate
  response ⇒ binding ∈ {Primary, Secondary} with unit alignment to the bound marginal; bivariate +
  BivariateResponse ⇒ both axes align and ConsequenceHazardBinding required). Both enums hashed.
- §7.4 (1343-1399): nested integration sketch — outer X integration unchanged; at each X point ask the
  hazard for bins; univariate collapses to `{(hX, 1.0)}`; FM contributes by binding (Primary chain at
  hX; Secondary chain loops bins transforming yVal; BivariateResponse loops bins evaluating
  `SurfaceProbability(hX, yVal)` with consequence fed by binding). Cost note: bins × outer evals.
- §5.8.4 (774-789): per-function `SamplingDimensions` table (`BivariateResponse` = 0 deterministic;
  composites = 0 with children owning dims recursively via `HashCombine(seed, ordinal,
  child.CanonicalHash())` — §5.8.5).
- §11 open items: **Q-P** (BestFitTabularHazard hash shape — deferred with imports), **Q-Q** (bivariate
  sampling dims formula `MX + MY + (copula?1:0)` — CONFLICTS with the ratified composite pattern;
  resolve to the composite pattern), **Q-R** (BivariateResponse surface uncertainty — default: stay
  deterministic).
- Amendment conventions: version-history entries + status blocks; open questions move to Resolved with
  dates.

**ROADMAP.md Phase 11** (`:583-589`): currently "Bivariate + BestFit import + LifeSim" — scope text
bundles bivariate types, `BestFitTransform`, `LifeSimConsequence`; verification names
`Test_BivariateRisk` (legacy 100M → 1M widened tolerance), `Test_DAMRAE` (bilinear surfaces), BestFit
import contract test. Exit: "full v1.1 input-function surface P/T/V". USER REDEFINES: this phase =
bivariate only; imports deferred. ROADMAP re-slice needed.

**Legacy traceability:** `docs\verification\legacy-traceability.csv` rows 24-25 classify the two
`Test_BivariateRisk.vb` methods as `BlockedMissingFeature` — they must be re-classified to mapped
current tests when the bivariate family lands. `Test_DAMRAE` rows likewise (verify classification).

**Other repo hits:** `docs\technical-reference\response-functions.md:15` monotonicity policy cites
multivariate scenarios (tailwater/river stage); `system-components.md:127,141,146` +
`cascading-end-states.md:79,84` + several verification pages reference the FM/component Gaussian
copula (MVN across components — distinct concept, keep terminology disjoint); the repository guidance matrix rows
list `HazardDimension` enum as P/T and "bivariate" under Later/Phase 11.
### Legacy v1.0 bivariate functionality (explored 2026-08-03)

**Headline: v1.0 has NO bivariate hazard, NO copula code, NO 2D integration.** The single bivariate
artifact is `BivariateResponse` — a response that *stores* a 2D surface but **collapses it to a 1D
curve at model-build time**; the engine never sees the surface. Independence is structural.

**`BivariateResponse`** (`RMC.TotalRisk\Project\Elements\Response Function\BivariateResponse.vb`,
1105 lines; complete C# port in `RMC.TotalRisk.IO\...\BivariateResponse.cs`):
- Data: `PrimaryHazardLevels : ObservableCollection<double>` (strictly ascending),
  `SecondaryHazardLevels : ObservableCollection<WeightedHazardLevel>` (`.Level` strictly ascending +
  `.Weight` ∈[0,1] summing to 1), `ProbabilityValues : double[nPrimary, nSecondary]` —
  **deterministic conditional P(f) per cell, NO uncertainty of any kind** (`IsDeterministic` hard-coded
  true; percentile/seed overloads silently ignored).
- Only the PRIMARY axis is labeled (`SpecifiedHazard`/`HazardUnit`); **the secondary axis has no
  type/unit/transform** — v1.0 gap to close. `HazardTransform` (None/Log) + `ProbabilityTransform`
  (None/Log/NormalZ) apply to the collapsed 1-D curve.
- **THE COLLAPSE** (`SampleResponseFunction`): `SRP(x_i) = Σ_j z[i,j] · w_j` → `OrderedPairedData
  (xStrict ascending, y NOT forced monotone)` → wrapped in 1-D `EmpiricalDistribution` with the two
  transforms. Weights independent of i ⇒ pure independence marginalization.
- **Weight derivation** (`EstimateWeights`, only when `UseManualWeights=false` — default is TRUE/manual):
  Voronoi midpoint bins between adjacent secondary levels against the secondary hazard's **mean
  marginal CDF**: `w_0 = F(μ_1)`, `w_k = F(μ_{k+1})−F(μ_k)`, `w_last = 1−F(μ_last)`; final weight
  absorbs residual so Σw = 1 exactly. `SecondaryHazardFunction : IHazardFunction` is borrowed from the
  hazard collection **by name** purely for this; contributes no uncertainty, is not a diagram node.
- Validation: `BivariateEmpirical.ValidateParameters` (≥2 per axis, finite, strictly ascending, dims
  match, p∈[0,1]) + log-transform guards + weight checks + zero-row warning + monotonicity **warning
  only** computed on the collapsed curve (surface never checked). Message codes `BRF-ERR-005/006/007/
  011/012/013/014`, `BRF-WRN-001/002`.
- XML payload (inside SQLite cell): `<BivariateResponse><PrimaryHazardLevels>` pipe-joined G17
  invariant `</...><SecondaryHazardLevels><WeightedHazardLevel Level= Weight= />…</...>
  <ProbabilityValues><Probability_Row>p00|p01|…</Probability_Row>…</...></BivariateResponse>`.
- v1.0 UI graph: `BivariateResponse` node collapses to ONE input/ONE output labeled with the primary
  hazard type/unit; chain validation matches hazard-type/unit strings hop-by-hop (primary only).
- Abandoned design vestiges: fully commented-out `SecondaryHazardNode` (event-tree node) and a
  commented block in `ResponseNode.vb:114-151` referencing `SecondaryHazard`/`SecondaryHazardUnit` —
  evidence of an abandoned two-connector design; never shipped.

**v1.0 engine** (`RiskAnalysis.vb`): 1-D adaptive Simpson over primary exceedance probability
p ∈ [1e-16, 1−1e-16] with 50 seeded stratification bins; multi-component = VEGAS with D = component
count and an MVN (`_eMVN`) correlation transform ACROSS components' primary hazards (a Gaussian
copula in structure, across components, never within one hazard). Innermost eval
(`SampledFailureMode.ComputeRisk`): hazard → transform chain → `Response.CDF(th2r)` → transform →
`Consequences.Function(tr2c)`; **consequences are functions of the primary hazard only** in the
shipped engine. Transforms/consequences: NO 2D variants at all.

**Legacy oracles** (`Test_TotalRisk\Test_BivariateRisk.vb` — exactly 2 methods, plus 1 adjacent):
- `Test_Bivariate_Risk()` (line 12): `MersenneTwister(45678)`, 100M realizations. Samples PGA from a
  15-pt seismic curve (`ProbabilityTransform=Log`), stage from a 29-pt stage-duration curve
  (`NormalZ`), **independent draws**; SRP = `BivariateEmpirical.CDF(pga, stage)` on a 4×6 surface
  (`ProbabilityTransform=Log`, axis transforms None); Bernoulli failure; **life loss keyed to the
  SECONDARY (stage)** via `Linear` — more expressive than the shipped product, exactly the v1.1 target
  semantics; outputs EAD + Weibull-plotting-position FN curve. Draw order per realization: pga, stage,
  rnd — always 3 draws (bit-reproducibility contract).
- `Test_Bivariate_SRP()` (line 85): same setup, holds pga=0.8, accumulates mean
  `CDF(0.8, stage)` over 100M → the exact quantity the v1.0 collapse approximates discretely (best
  oracle for the collapse math).
- `Test_DAMRAE.Test_PFM08_OT()` (line 14): BCL `Random(45678)`, 10M; FOUR chained `Bilinear` surfaces
  (deformation(pga,pool) 5×5, timeEQ(u,pool) 6×4, LL(time,pool) 20×5); triangular-distribution SRP;
  good stress oracle for a bilinear evaluator. All three tests print-only (no asserts).
- `Isabella Dam.tra` is the golden example project with real bivariate content (34 Probability_Row
  payloads + a wired-up BivariateResponse); DamonRAE 1/2/3-PFM have degenerate 1-row surfaces.

**Hazard types/units in v1.0** are editable string lists: defaults `{"Flow","Stage","Water Surface
Elevation","Peak Ground Acceleration"}` / `{"ft","m","cfs","cumecs","g","None"}`.

**Dev-repo planning doc** (`C:\GIT\RMC-TotalRisk-Dev\MODEL_LIBRARY_ARCHITECTURE.md`, 51 bivariate
mentions) already sketches v1.1 intent: `IBivariateHazardFunction` (§6.1.2), `ParametricBivariateHazard`/
`BestFitBivariateHazard`, `SecondaryIntegrationBins` (default 50 there; USER NOW SAYS default 20, max
1000), nested Y|X integration (§7.4), `HazardBinding`/`ConsequenceHazardBinding` on FailureMode (§6.5),
`BivariateResponse` with `PrimaryHazardType`/`SecondaryHazardType` (§6.3.1), and notes the legacy
BivariateResponse is deterministic. Cross-check what the CURRENT repo's normative arch doc carries.

**What v1.1 must PORT (behavior-preserving)** per the legacy sweep: the 2D surface storage + XML
round-trip; midpoint-bin weight derivation with last-bin residual absorption; the Σ z·w collapse and
its curve shape; the EmpiricalDistribution wrapper carrying the two transforms; the BRF-* validation
semantics; deterministic sampling behavior. **What v1.1 must INVENT:** everything hazard-side
(copula, marginal links, conditional Y|X bins), 2D engine integration, dimensional binding of failure
modes/consequences (which port feeds what), secondary-axis type/unit validation, uncertainty on the
bivariate response (v1.0 had none), and the two-port graph surface.
### Numerics copulas + bilinear + BestFit concept (explored 2026-08-03)

**Numerics repo:** `C:\GIT\numerics`, branch `bug-fixes-and-enhancements`, Version 2.1.4, multi-target
`net10.0;net9.0;net8.0;net481`, Nullable enable.

**Copulas** — folder `Numerics\Distributions\Bivariate Copulas\` (space in folder name), namespace
`Numerics.Distributions.Copulas`. 7 concrete copulas: `AMHCopula`, `ClaytonCopula`, `FrankCopula`,
`GumbelCopula`, `JoeCopula` (all `: ArchimedeanCopula`), `NormalCopula`, `StudentTCopula`
(`: BivariateCopula` directly). `CopulaType` enum members implicit 0..6:
`AliMikhailHaq, Clayton, Frank, Gumbel, Joe, Normal, StudentT`. **No Independence copula exists**
(grep-confirmed). BestFit serializes `CopulaType` by NAME (`.ToString()`/`Enum.TryParse`) — appending
`Independence` at the end (=7) is the zero-risk addition.

`BivariateCopula` base surface (file `Base\BivariateCopula.cs`):
- `Theta` (+ `_parametersValid` gate), `ThetaMinimum/Maximum`, `abstract CopulaType Type`,
  `ParameterToString`, `ParameterNameShortForm`, `NumberOfCopulaParameters`, `GetCopulaParameters`,
  `SetCopulaParameters(double[])`, `ParameterConstraints(x,y)`, `ValidateParameter(p, throw)` (null ⇒ valid),
  `abstract BivariateCopula Clone()`, `MarginalDistributionX/Y : IUnivariateDistribution?`.
- `PDF(u,v)`, `LogPDF`, `CDF(u,v)`, **`InverseCDF(double u, double v)` → `double[2] {u, C⁻¹(v|u)}`** —
  the SECOND arg is a *conditional* probability; this is the ONLY conditional surface. **There is no
  forward conditional CDF `C(v|u)`** — if needed it must be added (or use finite difference).
- `ORJointExceedanceProbability(u,v) = 1−CDF`, `ANDJointExceedanceProbability(u,v) = 1−u−v+CDF`.
- `GenerateRandomValues(n, seed=-1)` — seeds a LatinHypercube (seed ≤ 0 ⇒ clock).
- Estimation: static `BivariateCopulaEstimation.Estimate(ref copula, x, y, CopulaEstimationMethod)`
  (`FullLikelihood | PseudoLikelihood | InferenceFromMargins`); BrentSearch when
  `NumberOfCopulaParameters==1`, NelderMead otherwise — **a 0-parameter copula breaks it** (guard needed).
- **No copula XML serialization and no factory exist in Numerics.** BestFit owns the only
  `CreateCopula(CopulaType)` switch (`BivariateDistribution.CreateCopula`, throwing default arm).
  Patterns to follow if adding: `UnivariateDistributionFactory`, `StratificationBin.SaveToXElement`.
- Archimedean generic: `CDF = φ⁻¹(φ(u)+φ(v))`; independence fits as `φ(t)=−ln t` but θ is then
  vestigial — cleaner as a direct `BivariateCopula` subclass with `NumberOfCopulaParameters => 0`,
  CDF = u·v, PDF = 1, `InverseCDF(u,v) => [u, v]`, tail deps 0.
- `NormalCopula.CDF` uses deterministic static `MultivariateNormal.BivariateCDF` (Genz BVND) — seed-free,
  thread-safe; only D≥3 MVN CDF is stochastic. `StudentTCopula` caps ν at 30 (deliberate).
- `SetThetaFromTau` exists only on Clayton/Gumbel/AMH. `Correlation.KendallsTau(x,y)` available.
- Numerics test layout: `Test_Numerics\Distributions\Bivariate Copulas\Test_<Name>Copula.cs`;
  `Test_ParameterValidity.CopulasRejectNonFiniteParametersAndRecover()` iterates ALL copula families —
  must be extended for a new copula. Docs: `docs\distributions\copulas.md`.

**Bilinear interpolation** — `Numerics\Data\Interpolation\Bilinear.cs`, **namespace `Numerics.Data`**
(not `.Interpolation`). `Bilinear(double[] x1Values, double[] x2Values, double[,] yValues, SortOrder)`
with **`yValues[i,j] = y(x1[i], x2[j])`** (row = x1). Per-axis `Transform` enum (`None=0,
Logarithmic=1 (log10), NormalZ=2`) via `X1Transform/X2Transform/YTransform`; `Interpolate(x1,x2)`.
Extrapolation: both axes out of range ⇒ clamp to nearest corner; one axis out ⇒ 1-D linear
extrapolation along the in-range axis; ctor checks lengths only (not monotonicity). **NOT thread-safe**
(internal `Linear` searchers mutate `SearchStart` on every call — same hazard class as the existing
per-realization-rebuild discipline in TotalRisk). `BivariateEmpirical`
(`Distributions\Multivariate\BivariateEmpirical.cs`) is the existing 2-D grid CDF container
(lazily builds a cached `Bilinear` — same thread hazard). No `UncertainBilinear` / 2-D
uncertain-paired-data container exists anywhere — that must be new TotalRisk-side (or Numerics-side) work.

**Integration/sampling utilities:** `TrapezoidalRule` (1-D adaptive; base `Integrator`);
`AdaptiveSimpsonsRule2D` exists; `Stratify.MultivariateProbabilities(options, dist, exhaustive,
dimension, seed, double[,]? correlation)` produces per-dimension stratified probability bins;
`LatinHypercube.Random/Median(n, d, seed)` (**seed==0 is treated as unseeded**, like −1; per-column
independent MT streams); `Vegas` integrand is `Func<double[], double, double>` (point, weight) with
`Dimensions`, ctor `(func, dims, min[], max[])`, `TailFocusParameter`, `UseSobolSequence=true` default.

**BestFit bivariate concept** (`C:\GIT\RMC-BestFit`, 3-layer split to mirror):
- Model `RMC.BestFit\Models\BivariateDistribution\BivariateDistribution.cs`: copula + marginal models.
  Deserializing ctor `(marginalX, marginalY, XElement)` — **marginals resolved by the caller and passed
  in; the XElement stores copula state inline only** (`CopulaType` by name + `Parameters`). Static
  `CreateCopula(CopulaType)` switch. `GenerateRandomValues` clones copula and attaches cloned marginals.
- Analysis `Analyses\Bivariate\BivariateAnalysis.cs`: Bayesian MCMC posterior on θ (ARWMH);
  outputs AND joint-exceedance curve with credible intervals; clone-per-draw discipline in `Parallel.For`.
- UI element links marginals **by Name (string)** against the univariate-analysis collection —
  BestFit's documented regret; v1.1 TotalRisk must instead link by `IRiskFunction.Id` with lenient
  name fallback (established resolver policy).
- `Analyses\Bivariate\CoincidentFrequencyAnalysis.cs` — **closest analogue to our 2-D hazard**:
  `BivariateAnalysis` + `XValues[]`/`YValues[]`/`double[,] BivariateResponse` two-way table +
  `NumberOfBins` (default 50) → AEP curve of Z output; marginal-Y bin edges + conditional copula math
  in private helpers (`BuildVEdges`, `ComputeFZAtBin`, `ComputeAEPCurveForDraw`).

**Gotchas recorded by the explorer:** CopulaType has no explicit values (append-only);
`AMHCopula.ShortDisplayName` = "AHM" typo; Bilinear ctor exception messages swap rows/columns text;
independence copula breaks 1-parameter estimation assumptions; `MultivariateNormal.MVNUNI` default
seed 12345 shared across instances (D≥3 only).

## Ratified decisions (2026-08-03)

Haden delegated the nested-copula call at scoping ("up to you to consider in your design"); the
remaining forks carried defaults grounded in his directives and the already-tracked arch-doc
decisions. All twelve were ratified at the 2026-08-03 design review; decisions 2 (bins minimum) and
12 (the stored secondary-hazard link + collapse mode) carry his review revisions.

1. **Phase 11 scope = bivariate only.** `BestFitBivariateHazard`, `BestFitTabularHazard`,
   `BestFitTransform`, `RFAHazard`, and `LifeSimConsequence` all move to a follow-on import phase
   (user: "bestfit and rfa imports deferred to the future"; LifeSim is import-shaped and was not in
   the user's enumerated scope). ROADMAP re-slice records this.
2. **One new hazard type: `BivariateHazard`** (supersedes the doc's `ParametricBivariateHazard`
   naming — "Parametric" is wrong once marginals are links of ANY univariate type). Marginals X/Y are
   **references (by `IRiskFunction.Id`, lenient name fallback) to univariate hazard functions already
   in the analysis** — the tree `ProbabilitySource` referenced-function precedent, not inline
   ownership. Copula user-set from Numerics; **Independence copula (new in Numerics) is the default**;
   `SecondaryIntegrationBins` default 20, **min 3** (fewer is unacceptably coarse coverage of the
   conditional integral — user directive 2026-08-03), max 1000 (hard validation caps both ends);
   primary/secondary hazard type + unit strings user-set.
3. **Nested copulas deferred.** v1.1 validation requires both marginals to be univariate hazard
   functions (`IUnivariateHazardFunction`); the reference surface is typed so a future phase can admit
   a bivariate marginal (vine/nested structure) without a serialization break. Documented as a tracked
   open question in the arch doc.
4. **Copula parameter θ is fixed (user-set) in Phase 11** — no copula-parameter uncertainty until the
   BestFit posterior import phase (which brings the θ posterior naturally). Marginal-function
   knowledge uncertainty IS propagated (each marginal samples per realization as today).
5. **All 2-D surfaces are deterministic in Phase 11** (bivariate response/transform/consequence
   tables) — v1.0 parity, matches tracked Q-R default, avoids inventing an uncertain-2D-grid
   container. Q-R stays open for a future phase.
6. **Bivariate transform graph shape: 2 inputs, 2 outputs.** Output port 0 = z = f(x, y) (the
   transformed primary); output port 1 = the secondary value passed through unchanged. This makes
   cascades fully wired (hazard ⇒ transform ⇒ response ⇒ consequence, all two-port) and lets a
   univariate consequence hang off the secondary anywhere downstream. Univariate functions connect to
   either single port as today (via `RiskConnection.SourcePort`).
7. **Conditional discretization algorithm (Haden ratifies — algorithm authority):** at each primary
   slice with non-exceedance probability u, the secondary is integrated with **N trapezoidal steps in
   conditional-probability space**: nodes t_j uniform on [0, 1] (N bins ⇒ N+1 nodes, endpoint policy
   pinned in the requirements doc), y_j = MarginalY_sampled.InverseCDF(C⁻¹(t_j | u)) via the copula's
   existing inverse-conditional `InverseCDF(u, t)`, trapezoid weights over t. Equal-probability nodes
   adapt to the conditional density; only the existing Numerics conditional surface is needed; under
   Independence y_j are the plain marginal quantiles (the user's PGA/pool-duration reading). A forward
   conditional (h-function) `ConditionalCDF(u, v)` is added to Numerics for oracles/diagnostics, not
   the engine path.
8. **Seeding follows the ratified composite forward rule** (resolves Q-Q against the §6.1.2 sketch):
   the bivariate hazard occupies ONE component sampler ordinal; internally it seeds MarginalX,
   MarginalY (and later copula-θ) samplers via `SeedHelpers.HashCombine(seed, ordinal,
   child.CanonicalHash())`; its own `SamplingDimensions` = 0 in Phase 11. Subtree members are absorbed
   by the SetupSamplers dedup set. No existing model's seeds move.
9. **Copula probability convention pinned:** copulas operate on (u, v) as NON-exceedance marginal
   probabilities. The requirements doc records the exceedance-space conversion and the tail-dependence
   orientation consequence (upper-tail dependence in (u,v) = joint extreme-hazard dependence).
10. **Scope guards (loud validation errors, not silent gaps):** bivariate responses are single-stage
    only (no multi-stage cascade chains through a bivariate response, no bivariate response inside
    event/fault trees or composites, no `CompositeHazard` over bivariate hazards) in Phase 11; each
    guard is a documented validation error with a tracked future-work note.
11. **Identity/hash:** the `BivariateHazard` canonical hash uses a **projected identity form** (the
    `SystemComponent`/`CompositeConsequence` precedent): it folds the referenced marginals' canonical
    content (never their Ids/names), the copula type + θ, the bin count, and the axis metadata
    classification per the landing checklist. `RiskSerializationMode` handles marginal links as
    `FunctionReference` markers (ByReference) or inline embeds (SelfContained) exactly like the trees.
12. **`BivariateResponse` KEEPS the stored `SecondaryHazardFunction` link and the full v1.0 collapse
    mode (user directive 2026-08-03 — backwards compatibility is required).** The v1.0 option — a
    bivariate response connected to a single PRIMARY hazard in the graph, with the secondary
    integrated out through automatic or user-defined weights — is preserved as a first-class mode,
    and doubles as a verification cross-check against the new bivariate-hazard setup. v1.1 refinements
    within that preserved surface: (a) the link is stored v1.1-style — by `IRiskFunction.Id` with
    lenient name fallback (two metadata attributes, resolver-repaired), not by bare name; (b) the
    weights are serialized as the authoritative compute content and are **never silently re-derived
    on load** (fixing the v1.0 load-order overwrite bug) — re-derivation happens only on an explicit
    `EstimateWeights()` call (no-arg overload uses the stored link; an explicit-argument overload
    serves headless callers); (c) staleness is surfaced honestly: when `UseManualWeights == false`
    and the resolved link's freshly-derived weights differ from the stored weights beyond tolerance,
    `Validate()` reports a Warning. Deviation from v1.0 is confined to *when* derivation runs;
    documented in the type's `<remarks>` per the porting rule.

## Requirements

Functional requirements (traceable — architecture sections cite these as R1…R24):

**Bivariate hazard (new, copula-based — the RMC-BestFit bivariate concept):**
- R1. A user can create a `BivariateHazard` function whose marginal X (primary) and marginal Y
  (secondary) are LINKS to univariate hazard functions already stored in the analysis (by
  `IRiskFunction.Id`, lenient name fallback; both serialization modes; resolver repair).
- R2. Marginals must be univariate hazard functions in v1.1 (validation error otherwise); the link
  surface is typed to admit bivariate marginals (nested copulas) in a future phase without breaking
  serialization.
- R3. The copula is user-selectable from the Numerics copula families; a new **Independence copula**
  is added to Numerics and is the default. Copula parameters (θ, ν) are user-set and fixed in this
  phase.
- R4. The user sets primary and secondary hazard **type and unit** labels on the bivariate hazard.
- R5. The secondary dimension is integrated with a user-set number of trapezoidal bins:
  `SecondaryIntegrationBins` default **20**, allowed range **[3, 1000]** (hard validation caps —
  fewer than 3 bins is unacceptably coarse coverage of the conditional integral).
- R6. The PRIMARY hazard is what the engine integrates over (AGK in 1-D, VEGAS dimension in joint
  runs); at every primary slice the secondary is integrated over its copula-conditional distribution
  (Y | X) with the trapezoid bins. Canonical example: primary = seismic PGA frequency, secondary =
  reservoir pool-duration curve, Independence copula.
- R7. Marginal knowledge uncertainty propagates: each realization samples both marginals (their own
  uncertainty machinery) and evaluates conditional bins against the sampled curves.
- R8. Content-based seed identity holds: renaming/reordering/canvas moves/serialization-mode changes
  never change results; the bivariate hazard hashes the referenced marginals' CONTENT (projected
  identity), never their Ids/names.

**Bivariate transform and consequence (two-way tables):**
- R9. `BivariateTransform`: z = f(x, y) two-way table interpolated with the Numerics bivariate
  (bilinear) interpolation class, with per-axis transform options (None/Log10/NormalZ).
- R10. `BivariateConsequence`: consequence = f(x, y) two-way table, same interpolation machinery,
  carrying the declared consequence type like other consequence functions.
- R11. Both are deterministic in this phase (no surface uncertainty), with explicit documented
  extrapolation policy and full validation.

**Bivariate response:**
- R12. Port v1.0 `BivariateResponse` functionality and behavior first: 2-D deterministic P(f|x,y)
  surface; secondary levels with weights (`WeightedHazardLevel`); manual weights default; automatic
  weight estimation from a linked secondary hazard's marginal CDF (midpoint/Voronoi bins, residual
  absorbed into the last bin); the weighted collapse to a 1-D curve when driven by a univariate
  hazard; transforms; validation semantics; monotonicity warning.
- R13. Augment it for bivariate hazards: when the owning component's hazard is bivariate, the surface
  is evaluated jointly at (x, y) per conditional bin — the copula-conditional weights replace the
  static stored weights; secondary axis gains type/unit labels validated against the hazard's
  marginal Y.

**Risk graph:**
- R14. A hazard node whose function is bivariate exposes TWO output ports: port 0 = primary, port 1 =
  secondary, labeled from the marginal axis metadata.
- R15. A bivariate transform/response/consequence node exposes two input ports and can connect to both
  hazard ports at once; bivariate nodes cascade (hazard ⇒ bivariate transform ⇒ bivariate response ⇒
  bivariate consequence).
- R16. A bivariate transform node exposes two output ports: port 0 = z = f(x, y) (transformed
  primary), port 1 = secondary passthrough.
- R17. Univariate transforms/responses/consequences can connect to EITHER single port (primary or
  secondary), giving per-dimension chains; the canonical example is a bivariate response on both
  ports + a univariate consequence fed from the secondary (pool) signal.
- R18. The failure-mode projection stamps `FailureMode.HazardBinding` and
  `ConsequenceHazardDimension` from the connection ports (reserved seam), collects the X-chain and
  Y-chain transform lists, and enforces the §6.5 alignment matrix (axis type/unit agreement with the
  bound marginal).

**Engine:**
- R19. 1-D path: outer AGK integration over the primary axis unchanged in structure (endpoint
  rectangles + QuadratureMassLedger gates intact); the integrand embeds the conditional-bin loop;
  univariate components pay zero overhead.
- R20. Joint/system path: a bivariate component still occupies exactly ONE VEGAS dimension (its
  primary); the bin loop runs inside its per-component evaluation; MVN cross-component hazard
  correlation applies to primaries.
- R21. Mixed failure-mode sets (Primary-bound, Secondary-bound, BivariateResponse) combine at the
  correct conditioning point: per (x, y_j) combination then weighted summation over bins (shared-Y
  correlation honored), preserving every dependency option.
- R22. Every engine feature has a DEFINED behavior for bivariate components — supported semantics or
  a loud validation refusal (profiles, HazardThreshold, ProfileHazardElementId, sensitivity,
  contributions, reliability mode, multi-consequence, seed-pinning, cascades, trees) — no silent
  wrong answers.
- R23. Reproducibility contract unchanged: same inputs + seed ⇒ bit-identical at any thread count;
  bins are deterministic quadrature, not draws.

**Process:**
- R24. Full deliverable discipline: unit tests for every new public class; verification families with
  documented k·SE tolerances (legacy `Test_BivariateRisk`/`Test_DAMRAE` conversions + new copula
  oracles); tech-ref + verification doc pages; arch-doc/ROADMAP/repository-guidance/traceability updates;
  per-session commits after green gates.
## Architecture

Notation: `TR` = `src\RMC.TotalRisk`, `NUM` = `C:\GIT\numerics`. File:line anchors verified 2026-08-03.

### A. Numerics work item (repo `C:\GIT\numerics`, branch `bug-fixes-and-enhancements`; session S1)

**A.1 `IndependenceCopula`** (`NUM\Numerics\Distributions\Bivariate Copulas\IndependenceCopula.cs`) —
direct `BivariateCopula` subclass, NOT Archimedean (θ would be vestigial and
`ArchimedeanCopula.NumberOfCopulaParameters => 1` bakes in one parameter). `[Serializable]`,
net481-safe. Surface: `Type = CopulaType.Independence`; DisplayName "Independence" / short "Π";
`ThetaMinimum = ThetaMaximum = 0`; `ParameterToString = new string[0,2]`;
`NumberOfCopulaParameters = 0`; `GetCopulaParameters = Array.Empty<double>()`;
`SetCopulaParameters` no-op; `ValidateParameter` always null (**permanently valid — even
`Theta = NaN` cannot invalidate it; pinned by test**); `PDF = 1`; `CDF = u·v`;
`InverseCDF(u,v) = [u, v]` (conditional simulation under independence is the identity — the exact
surface the discretizer calls); `ConditionalCDF(u,v) = v`; tail deps 0; `Clone()` deep-copies
marginals via `CloneMarginal`; ctors `()` and `(marginalX, marginalY)`.

**A.2 Forward conditional h-function** — `public virtual double ConditionalCDF(double u, double v)`
on `BivariateCopula`: **virtual, not abstract** (Numerics is a public NuGet; abstract would break
external subclasses), documented central-finite-difference fallback over CDF (ε = 1e-6); every
in-tree family overrides analytically. Convention in XML docs: **h(v|u) = ∂C(u,v)/∂u = P(V ≤ v |
U = u)** on non-exceedance (u,v); `InverseCDF(u,t)[1]` is its inverse in v (round-trip pinned).
Overrides: Archimedean generic `h = φ′(u)/φ′(C(u,v))` (exact inverse of the Genest-1986 simulation
already at `ArchimedeanCopula.cs:119-127`; per-family closed forms recorded in remarks — Clayton
`u^{−θ−1}(u^{−θ}+v^{−θ}−1)^{−1−1/θ}`, Frank, Gumbel `C·A^{1/θ−1}(−ln u)^{θ−1}/u`, Joe, AMH
`v(1−θ(1−v))/D²`); Normal `Φ((Φ⁻¹(v)−ρΦ⁻¹(u))/√(1−ρ²))`; StudentT `T_{ν+1}((x₂−ρx₁)/s)` with
`s = √((1−ρ²)(ν+x₁²)/(ν+1))`; Independence `v`. Also **`InverseConditionalCDF(double u, double t)`
scalar** (non-allocating; the existing `InverseCDF(u,v)` allocates `double[2]` per call — hot-loop
violation; array overload recomposed on top). Purpose: engine uses the scalar inverse; h-function is
oracles/diagnostics.

**A.3 `CopulaType.Independence`** appended LAST (implicit value 7; enum has no explicit values;
BestFit + TotalRisk serialize by NAME — append-only).

**A.4 Estimation guard** (`Base\BivariateCopulaEstimation.cs`): first line of `Estimate`:
`if (copula.NumberOfCopulaParameters == 0) return;` — benign no-op, XML-doc'd; covers all three
methods; test asserts no-throw + untouched state.

**A.5 Copula XML serialization + factory — IN NUMERICS** (TotalRisk stays a pure consumer; BestFit
can migrate later): `BivariateCopula.ToXElement()` (public virtual, one writer for all families) →
`<Copula Type="Clayton" Parameters="2" />` — `Type` = enum name; `Parameters` = pipe-joined G17
invariant of `GetCopulaParameters` in `SetCopulaParameters` order (Independence writes "");
marginals NOT serialized (BestFit precedent; TotalRisk marginals are separately-linked functions).
New static `Base\CopulaFactory.cs`: `CreateCopula(CopulaType)` closed switch over all 8 (default arm
throws `NotSupportedException`); `CreateCopula(XElement)` with the `UnivariateDistributionFactory`
hardening ladder (null throw; Enum.TryParse + IsDefined; split '|'; count == NumberOfCopulaParameters;
`Tools.IsFinite` per value; `SetCopulaParameters`; `!ParametersValid` throws).

**A.6 Numerics tests** (`NUM\Test_Numerics`): `Test_IndependenceCopula.cs` (~9 methods: PDF/CDF/
InverseCDF identity/ConditionalCDF/tail/zero-parameter surface incl. NaN-stays-valid/
GenerateRandomValues shape + τ≈0/Clone/estimation-no-op); per-family `Test_ConditionalCDF` additions
in all 7 existing copula test files (round-trip `ConditionalCDF(u, InverseCDF(u,t)[1]) ≈ t`, central
finite difference vs CDF at 1e-6, R `copula::cCopula` spot values where files already cite R);
`Test_CopulaFactory.cs` (enum round-trip ×8, XElement round-trip bitwise via G17, zero-parameter
round-trip, malformed throws); `Test_ParameterValidity` — Independence added as a SEPARATE assertion
block (the existing loop asserts NaN⇒invalid, which the zero-parameter contract deliberately
inverts). Docs: `NUM\docs\distributions\copulas.md` — Independence section + selection-guide row +
Conditional Distributions section documenting the new API + per-family formulas.

### B. Model-library types (TotalRisk)

**B.1 `IBivariateHazardFunction`** (`TR\Core\Interfaces\IBivariateHazardFunction.cs`) —
`: IHazardFunction`:
- `IHazardFunction? MarginalX { get; set; }` / `MarginalY` — typed `IHazardFunction?` NOT
  `IUnivariateHazardFunction` (decision 3: univariateness is validation-enforced so a future phase
  can admit bivariate marginals without a break; nullable because lenient-name resolver misses keep
  the slot null — the `CompositeHazard` posture).
- `string SecondarySpecifiedHazard { get; set; }` / `SecondaryHazardUnit` (primary axis = the
  inherited `SpecifiedHazard`/`HazardUnit` — no Primary* duplicates; `HazardElement` port-0 labeling
  already reads them).
- `int SecondaryIntegrationBins { get; set; }` (default 20; [3, 1000] validation, no silent clamp);
  `int ConditionalNodeCount { get; }` = bins + 1.
- `IUnivariateDistribution SampleSecondaryFunction()` / `(double)` / `(int)`;
  `double MinSecondaryHazard(bool)` / `MaxSecondaryHazard(bool)`.
- Engine seam: `SampledBivariateHazard SampleBivariate(int realizationIndex)` — frozen
  per-realization snapshot (see D.2); diagnostics convenience
  `IReadOnlyList<(double Y, double Weight)> SampleConditionalYGivenX(int idx, double xHazardLevel)`
  (allocating; docs say "diagnostics/tests only").

**B.2 `BivariateHazard`** (`TR\RiskFunctions\Hazards\BivariateHazard.cs`,
`: HazardFunctionBase, IBivariateHazardFunction` — NOT `UnivariateHazardBase`, so it can never be an
`IUnivariateHazardFunction`, which also makes self-nesting structurally impossible):
- Members: `MarginalX`/`MarginalY` (referenced not owned; setter swaps a PropertyChanged
  subscription and re-raises — the `WeightedHazardFunction` bubbling precedent); `Copula :
  BivariateCopula` default `new IndependenceCopula()`, never null (null assignment coerces to fresh
  Independence — the `ResponseStage.Response` coercion precedent); `CopulaTheta` passthrough with
  INPC; `SecondaryIntegrationBins = 20`; the two secondary label strings; `FunctionType =
  HazardFunctionType.Bivariate`; `IsDeterministic` = both marginals deterministic (θ fixed, decision
  4); `SamplingDimensions = 0` (composite pattern — resolves Q-Q).
- Ctors: `()`; `(marginalX, marginalY, copula = null)`; `(XElement, IRiskFunctionResolver?)` using
  the exact `CompositeHazard` recipe via `FunctionEntry.Read<IHazardFunction>` (stale-id throws;
  name-fallback miss records into `_unresolvedFunctionReferences` for Validate; wrong-cluster
  throws). Missing `<Copula>` child ⇒ Independence default.
- `SetupSampler` (decision 8, forward rule verbatim from `CompositeHazard.cs:397-409`):
  `base.SetupSampler` (D=0 records SampleSize only) then `MarginalX.SetupSampler(N,
  SeedHelpers.HashCombine(seed, MarginalX.CanonicalHash(), 0), scheme)` and MarginalY with ordinal 1
  (+ posterior-capacity guards). ONE component ordinal; a future copula-θ posterior takes ordinal 2
  without moving X/Y streams. The SetupSamplers dedup set needs no additions — `RiskAnalysisRunContext`
  deep-clones components before setup (`RiskAnalysisRunContext.cs:102`), so cross-component shared
  marginals are never double-seeded on live instances.
- Sampling surface: `SampleFunction()` trio delegates to MarginalX (the sampled hazard IS the X
  marginal); `SampleSecondaryFunction` trio to MarginalY; `Min/MaxHazard` = X bounds;
  `Min/MaxSecondaryHazard` = Y bounds; all throw `InvalidOperationException` when unusable.
- **Discretization kernel (decision 7 pinned):** N bins ⇒ N+1 nodes `t_j = j/N`; inverse evaluations
  clamp t to `[1e-16, 1−1e-16]` (the engine's own probability floor; keeps y finite for unbounded
  marginals — Normal at 1e-16 ≈ ∓8.2σ); `v_j = copula.InverseConditionalCDF(u, t_j)` (Independence:
  v_j = t_j exactly); `y_j = sampledY.InverseCDF(v_j)`; trapezoid weights `w_0 = w_N = 1/(2N)`,
  else `1/N`, LAST weight computed as residual `1 − Σ_{j<N} w_j` so Σw = 1 exactly; weight vector
  precomputed once at `SetupSampler` (realization-independent), copied via `Array.Copy`. u is
  NON-exceedance (decision 9; conversion `u = 1 − pExceedance` documented). Buffer contract: caller
  arrays `≥ ConditionalNodeCount`; no allocation/LINQ in the kernel.
- `Validate()` matrix (9 rules): empty labels ×4 → Errors; unresolved references → Errors; null
  marginals → Errors; **marginal not `IUnivariateHazardFunction` → Error** (the nested-copula
  deferral guard); `ReferenceEquals(MarginalX, MarginalY)` → Error (one instance = one knowledge
  quantity; two distinct equal-content instances legal and independent); invalid marginal → Error
  summary; `!Copula.ParametersValid` → Error surfacing the copula's message; bins outside [3,1000]
  → Error ("The number of secondary integration bins must be between 3 and 1000.");
  marginal-vs-declared label mismatches → Warnings (labels never gate compute).
- `ToXElement(mode)` (child order `Copula`, `MarginalX`, `MarginalY`, append-only):
  attributes Id/Name/Description/SpecifiedHazard/HazardUnit/SecondarySpecifiedHazard/
  SecondaryHazardUnit/SecondaryIntegrationBins; `<Copula .../>` embedded via Numerics
  `ToXElement()`; marginal containers hold one `FunctionEntry.Write(marginal, mode)` child each —
  `<FunctionReference Id Name/>` under ByReference, full inline under SelfContained (trees pattern).
- **`CanonicalHash()` projected identity (decision 11; `CompositeHazard.cs:788-811` recipe):**
  identity element with `SecondaryIntegrationBins`, `CopulaType` (name), `CopulaParameters`
  (pipe-joined G17), `MarginalXHash`/`MarginalYHash` = child canonical hashes as NAMED attributes
  (so swapping X↔Y MOVES the hash — asymmetric roles). Axis labels excluded. Mode-invariant by
  construction (built from live state). Component identity composes unchanged
  (`SystemComponent.BuildIdentityXElement` embeds self-contained hazard XML, metadata-stripped).
- Clone/copy: rides the serialization round-trip (house pattern); documented consequence — a cloned
  graph's bivariate hazard owns marginal COPIES (same reload semantics `ValidateSharedInstances`
  already warns about).

**B.3 `BivariateTransform` + `BivariateConsequence`** (two-way tables on Numerics `Bilinear`):
- New marker interfaces `IBivariateTransformFunction : ITransformFunction` /
  `IBivariateConsequenceFunction : IConsequenceFunction`: `SecondarySpecifiedHazard`/
  `SecondaryHazardUnit`; `double Evaluate(double x, double y)` (convenience — builds one
  interpolator); `Bilinear CreateInterpolator()` (THE engine seam — one fresh configured instance
  per sampled realization); `Min/MaxSecondaryHazard()`.
- The inherited 1-arg `SampleFunction` trios (and `SampleExposureBranches` on the consequence)
  **throw `NotSupportedException`** naming `Evaluate(x,y)` — a fabricated univariate bridge would
  silently evaluate at a meaningless y; the throw is unreachable in valid models because graph arity
  + validation keep bivariate functions out of univariate chains (the
  `SupportsOrderedCurveSampling` partial-capability posture). `CountExposureBranches() => 1`.
- **Interpolator thread discipline (pinned): rebuild once per sampled realization, never per call,
  never shared** (`Bilinear` mutates `SearchStart` per lookup — the interpolator-race rule). Nothing
  cached on the function.
- `BivariateTransform` members: `X1Values`/`X2Values`/`ZValues[X1.Length, X2.Length]`
  (`Bilinear` convention `z[i,j] = z(x1[i], x2[j])`); `HazardTransform`/`SecondaryHazardTransform`/
  `TransformTransform` (Numerics `Transform`, default None); inherited
  `TransformedHazard`/`TransformedHazardUnit` label the z output; `FunctionType =
  TransformFunctionType.Bivariate`; deterministic, D=0. Min/Max hazard = X1 bounds; secondary = X2;
  `Min/MaxTransformedHazard` = grid min/max.
- `BivariateConsequence`: identical grid design with cluster names — output enum
  `ConsequenceTransform`; output labels via inherited `SpecifiedConsequence`/`ConsequenceUnit` (how
  the cluster declares type — `RiskType` is a results-stream concept, not a function member);
  negative Z cells → Warning (advisory).
- Validation (both): axes ≥2 finite strictly ascending; grid dims match; every cell finite;
  Log-transform guard per axis + output (any value < 0 → Error); NormalZ guard (outside [0,1] →
  Error); empty labels → Errors.
- **Extrapolation policy (pinned): accept `Bilinear`'s native behavior and document loudly** — both
  axes out ⇒ nearest corner clamp; one axis out ⇒ 1-D linear extrapolation along the in-range axis.
  (It is the evaluator the legacy `Test_DAMRAE` oracles exercised; re-clamping would break parity.)
  `BivariateResponse` additionally clamps its result to [0,1].
- Serialization (clean v1.1 shape, append-only child order `X1Values`, `X2Values`, `ZValues`):
  pipe-joined G17 axis elements + `<ZValues><Row>z00|z01|…</Row>…</ZValues>` (one Row per X1 value,
  X2-ordered; `Row` not the legacy `Probability_Row` — transform cells are not probabilities).
  Ragged payloads reconstruct to parsed shape and fail Validate (never silently truncate). Default
  leaf-path `CanonicalHash()`; labels strip via rules.

**B.4 `BivariateResponse`** (`TR\RiskFunctions\Responses\BivariateResponse.cs`,
`: ResponseFunctionBase, IBivariateResponseFunction`) + **`WeightedHazardLevel`** (verbatim port
into house style: `Level`/`Weight` INPC, `<WeightedHazardLevel Level Weight/>` exact legacy
attribute names, ctor-from-XElement, Clone) + marker
`IBivariateResponseFunction : IResponseFunction` (`Secondary*` labels, `SurfaceProbability(x,y)`,
`CreateInterpolator()`, `Min/MaxSecondaryHazard()`, `SecondaryLevelCount`) so engine/graph switch on
an interface (the `IBranchingResponseFunction` precedent).
- **v1.0 parity surface (exact):** `PrimaryHazardLevels` (ObservableCollection<double>),
  `SecondaryHazardLevels` (ObservableCollection<WeightedHazardLevel>), `ProbabilityValues[,]` with
  the legacy grid auto-resize on collection changes (overlap preserved, new cells zero-filled);
  `HazardTransform`/`ProbabilityTransform`; `UseManualWeights` default TRUE; `IsDeterministic =>
  true`; D=0; percentile/index overloads return the mean collapse; **the collapse**
  `SampleResponseFunction()`: `SRP(x_i) = Σ_j z[i,j]·w_j` → `OrderedPairedData(xStrict:true,
  Ascending, yStrict:false, None)` → `SampleFunction()` wraps in `EmpiricalDistribution
  {XTransform = HazardTransform, ProbabilityTransform = ProbabilityTransform}`; `IsMonotonic()` on
  the collapsed curve; Min/Max hazard/probability from levels/collapse.
- **Stored `SecondaryHazardFunction : IHazardFunction?` link + `EstimateWeights` (decision 12 —
  v1.0 option preserved):** the link is a live reference persisted as TWO METADATA ATTRIBUTES
  (`SecondaryHazardFunctionId` + `SecondaryHazardFunctionName`, written only when non-null),
  resolver-repaired on load by Id with lenient name fallback (miss ⇒ null; NEVER an Error — the
  serialized weights are the compute content); setter swaps a PropertyChanged subscription (staleness
  awareness for the consuming layer) + INPC; the link must be a univariate hazard function (a
  bivariate link ⇒ Error — its `SampleFunction()` would silently derive from marginal X).
  `EstimateWeights()` (no-arg, uses the stored link; throws if null/unresolved/invalid) and
  `EstimateWeights(IHazardFunction)` (explicit, for headless callers; also stores the argument as the
  link): legacy algorithm exact — single level ⇒ 1; Voronoi midpoints `μ_i = (L_i+L_{i−1})/2` against
  the mean marginal CDF; `w_0 = F(μ_1)`, interior differences, `w_last = 1−F(μ_last)`; last-bin
  residual absorption both directions so Σw = 1 exactly; sets `UseManualWeights = false`.
  **Derivation NEVER runs as a load or property-set side effect** (the v1.0 load-order overwrite bug
  is fixed; the consuming layer may auto-call on its own events). Staleness surfaced by `Validate()`:
  when `UseManualWeights == false` and the link resolves, freshly-derived weights differing from the
  stored weights beyond 1e-8 ⇒ Warning ("automatic weights are stale — re-estimate or switch to
  manual"); link unresolved with `UseManualWeights == false` ⇒ Warning.
- **Augmentation:** `SecondarySpecifiedHazard`/`SecondaryHazardUnit` (closes the v1.0 unlabeled-
  secondary gap); `SecondaryHazardTransform` (new compute-relevant enum, default None — the joint
  surface needs an X2 interpolation transform); `SurfaceProbability(x,y)`/`CreateInterpolator()` =
  `Bilinear(primary[], secondary.Level[], grid) {X1 = HazardTransform, X2 =
  SecondaryHazardTransform, Y = ProbabilityTransform}` + [0,1] clamp after back-transform.
- **Two operating modes, selected ONLY by the parent** (the response holds no mode state):
  **collapse mode** under a univariate hazard — the fully-preserved v1.0 option (single primary
  input; secondary integrated out through the automatic or user-defined weights; the engine consumes
  `SampleFunction().CDF(th)` exactly like any tabular response — NO engine special-casing; legal in
  multi-stage chains since it presents as a deterministic univariate response); **joint mode** under
  a bivariate hazard (conditional bins REPLACE the static weights — weights inert; single-stage
  only). The two modes estimate the same integral `E_Y[surface(x, Y)]` under Independence with
  matching marginals — the collapse-vs-joint consistency verification test rests on exactly this
  (decision 12: the preserved mode doubles as the cross-check for the new path).
- Validate: the BRF-derived matrix (empty labels ×4; surface structural rules — ≥2 primary, ≥1
  secondary, strictly ascending, dims, p ∈ [0,1]; weight range + `|Σw−1| > 1e-8` Errors; Log guards
  per axis + cells; collapsed-first-ordinate zero-row Warning; non-monotone collapsed curve Warning
  ONLY; single-secondary-level Warning "degenerates to univariate" — the ≥2-for-joint rule is
  enforced mode-side on FailureMode).
- Serialization: v1.1 attribute envelope (incl. the optional `SecondaryHazardFunctionId`/
  `SecondaryHazardFunctionName` link pair) + **legacy inner child names kept deliberately**
  (`PrimaryHazardLevels` pipe G17, `WeightedHazardLevel` children, `ProbabilityValues` with
  `Probability_Row` rows) so a future `.tra` importer lifts the legacy cell payload verbatim. Ctor
  gains the resolver overload `(XElement, IRiskFunctionResolver?)`; the factory case threads it.
- Hash classification: compute = levels, weighted levels (Level AND Weight — weights drive the
  standalone collapse; documented consequence: a weight edit re-rolls seeds even in joint mode where
  they're inert, because mode is external state), grid, all three transforms. Stripped = Id/Name/
  Description (existing), all four axis labels + **`UseManualWeights` +
  `SecondaryHazardFunctionId` + `SecondaryHazardFunctionName`** (new strip entries — the link and
  the weight-provenance flag record WHO/WHERE the weights came from, not what they are; the
  `UseDefaults` precedent; the linked hazard's CONTENT deliberately does NOT enter this response's
  hash — the weights are the compute, so editing the linked hazard never re-rolls the response's
  seeds until weights are explicitly re-estimated).

**B.5 Enums/factories/registration:** four appended runtime-only discriminator members
(`HazardFunctionType.Bivariate`, `TransformFunctionType.Bivariate`, `ResponseFunctionType.Bivariate`,
`ConsequenceFunctionType.Bivariate`); four `RiskFunctionFactory.CreateFromXElement` cases
(BivariateHazard AND BivariateResponse thread the resolver — the response for its weight-derivation
link; the two table leaves don't); four kitchen-sink registry entries
(`HashInvarianceKitchenSinkTests.RegisteredTypes()` — scaffold auto-derives metadata-inertness,
per-stripped-attribute inertness, determinism, compute-sensitivity); **five new
`CanonicalizationRules` stripped attributes appended: `SecondarySpecifiedHazard`,
`SecondaryHazardUnit`, `UseManualWeights`, `SecondaryHazardFunctionId`,
`SecondaryHazardFunctionName`**; resolver test coverage (ByReference round-trip,
stale-id throw, lenient-name policy); **scope-guard Errors appended** to `CompositeHazard`/
`CompositeResponse`/`CompositeTransform`/`CompositeConsequence`.Validate (no bivariate children) and
`ProbabilitySource.Validate` (no bivariate referenced response in trees); Ported Types Matrix rows;
`FunctionTypeDiscriminatorTests` extensions.

### C. Graph layer + FailureMode

**C.1 `HazardElement`:** `OutputCount => Function is IBivariateHazardFunction ? 2 : 1` (the reserved
gate at `HazardElement.cs:116`); `Function` setter raises `nameof(OutputCount)`;
`TryAssignFunction` unchanged (IHazardFunction admits the type).

**C.2 `TransformElement` + `ConsequenceElement` — add `SecondaryInput`, the `ResponseElement`
pattern verbatim:** `_secondaryInput`/`_pendingSecondaryInput` fields; INPC property (the name
"SecondaryInput" already routes topology invalidation through `ComponentGraph.ElementPropertyChanged`'s
string-matched case); ctor reads `ReadPendingConnection(xElement, "SecondarySource")`; `ToXElement`
writes via `WriteConnection(..., "SecondarySource", ...)`; resolve/clone remap; **`GetInputConnections()`
yields `Input` FIRST then `SecondaryInput`** (load-bearing: `PrimaryInputSource` takes the first
connection — this is what keeps secondary edges off-path); snapshot widening (`TransformElement` 2
slots; `ConsequenceElement` 3 incl. HazardSource); detach clearing in
`ComponentGraph.DisconnectBranchConnections`. **No new strip rules needed** — the
`SecondarySourceBranchId`/`Branch` names are already registered and graph XML is never hashed.
Arity: transform `InputCount`/`OutputCount => bivariate ? 2 : 1` (port 0 = z, port 1 = passthrough y,
decision 6); consequence `InputCount => any entry bivariate ? 2 : 1` (+ collection-change raise).
Element-local validation: secondary-with-univariate-function Error; bivariate-without-secondary
Error; consequence list MIXING bivariate and univariate functions Error (a two-input terminal cannot
route one signal set to both kinds — scope guard).

**C.3 `ResponseElement` gates:** `InputCount => bivariate ? 2 : 1` (the secondary port is OFFERED —
whether it must be wired depends on the root hazard, which the element cannot see, so that rule
lives in the graph, C.4). Element-local Validate keeps only: secondary input with a univariate
FUNCTION ⇒ Error (reworded from the reserved text). Outputs unchanged — a bivariate response emits
the same aggregate Fail(0)/Non-Fail(1) ports (it consumes y, never re-emits it). `RiskConnection`
and `RiskElementFactory` need **no changes** (confirmed — `SourcePort` plumbing complete;
cluster-interface matching admits the new types).

**C.4 `ComponentGraph`:**
- **New public helper `TryResolveSecondaryChain(RiskConnection secondaryInput,
  List<TransformElement> chain, out string error)`** — shared by projection + validation + picker
  (the picker/validator-agreement rule). Walk from the secondary input upstream: hazard port 1 ⇒
  success; univariate transform port 0 ⇒ front-insert, continue from its Input; bivariate transform
  port 1 ⇒ passthrough hop (adds nothing; Phase 11 guard: must connect DIRECTLY to the upstream
  bivariate element's port 1 or hazard port 1 — no univariate transforms between bivariate elements,
  keeping one Y-chain per path); bivariate transform port 0 into a secondary slot ⇒ Error (z is a
  primary-kind signal; axes never cross); response/consequence source ⇒ Error; visited-set cycle
  guard.
- **Connection validation matrix** (in `ValidateConnections`/`ValidatePaths`): univariate-function
  `.Input` may source hazard port 0 OR port 1 (how a Secondary-bound chain starts), transform port 0,
  bivariate-transform port 0 (z) or port 1 (y-continuation), response ports; bivariate-function
  `.Input` must trace to a primary-kind signal (Error if it traces to hazard port 1);
  `.SecondaryInput` must satisfy `TryResolveSecondaryChain`; consequence `HazardSource` binding may
  additionally target hazard port 1 or a resolved Y-chain transform (port 0) — but NOT a bivariate
  transform's port 1 ("bind at the hazard or a secondary-chain transform instead"); structural:
  unused-port-1 Warning; **mode-dependent bivariate-response rules (decision 12):** under a
  BIVARIATE-hazard root a bivariate response element must have its `SecondaryInput` wired (Error if
  null — joint mode consumes both dimensions) and its path may hold only ONE response element (the
  single-stage guard); under a UNIVARIATE-hazard root a bivariate response element is **legal in
  collapse mode** with `SecondaryInput` null (Error if connected — no port-1 source exists) and
  participates in paths/stages like any univariate response; bivariate TRANSFORM/CONSEQUENCE
  elements on a univariate-hazard path ⇒ Error (they have no collapse semantics). Reachability/
  leaf/cycle/topology all work unchanged (secondary edges flow through `GetInputConnections` into
  `DownstreamMap`; `GetUpstreamPath` stays primary-only, untouched).
- **`GetAvailableHazardSources`** (gate 5): port-1 option labeled from
  `biv.SecondarySpecifiedHazard`/`SecondaryHazardUnit` (ChainPosition 0 = raw Y); plus one option
  per resolved Y-chain transform (position k+1). `HazardSourceOption` record UNCHANGED (adding a
  field breaks the pinned ctor; dimension derives from chain membership).
- `ConnectionSnapshot`/`AllConnections`/detach plumbing widened for the two new secondary slots.
- **`GetReferencedFunctions`**: after yielding an `IBivariateHazardFunction`, also yield its
  non-null marginals through the same `seen` set (save-time dependency set + delete-safety must
  include linked marginals).

**C.5 `SystemComponent` projection (`BuildFailureMode`):**
1. Stamp `HazardBinding` from the root exit port: `ExitConnection(path[1], path[0])?.SourcePort == 1
   ⇒ Secondary` (existing modes always port 0 ⇒ Primary ⇒ zero serialized movement).
2. Collect the Y-chain: first bivariate element's `SecondaryInput` through
   `TryResolveSecondaryChain` ⇒ `mode.SecondaryHazardToResponse` (upstream→downstream; loop not
   LINQ). Unresolvable ⇒ empty (projection stays non-throwing; graph validation already reported).
3. Bivariate response stage builds exactly as today (polarity from exit port; the response is just
   an IResponseFunction).
4. Binding stamping generalization: HazardSource target on the primary path ⇒ existing behavior
   (`SourcePort == 1` now only means the hazard element's raw-Y, position 0); target on the mode's
   Y-chain ⇒ `ConsequenceHazardDimension = Secondary`, position = yChainIndex + 1.
5. Profile guard: the profile transform's first hazard edge must use port 0 (Error otherwise — a
   Y-chain transform would pass the existing reachability test).
- **`AssignOccurrenceIndices`: NO change** (reconciled): occurrence indexing operates on component
  identity hashes which already fold marginal content through the hazard's projected identity;
  run-context deep-clone prevents live double-seeding; within-component independence comes from the
  child ordinals.
- `SetupSamplers`: hazard stays ordinal 0 (one call — the forward rule absorbs the subtree);
  `CollectSensitivityInputs` gains the explicit MarginalX/MarginalY enumeration branch (engine D.6).

**C.6 `FailureMode`:**
- **New serialized member `List<ITransformFunction> SecondaryHazardToResponse`** (default empty;
  null-coercing setter + INPC — the `ResponseToConsequence` shape). Written as a
  `<SecondaryHazardToResponse>` child AFTER `<ConsequenceFunctions>` **ONLY WHEN NON-EMPTY** — an
  always-written empty container would move every existing failure-mode hash and re-roll all seeds
  (the conditional-presence precedent: `ParametricConsequence` sigma). Missing ⇒ loads empty.
  Compute-relevant when present (rides ToXElement/identity form automatically). Sampler walk seeds
  the Y-chain AFTER `ResponseToConsequence` (append at end — existing ordinals bit-identical; the
  profile-transform "positions AFTER" precedent).
- **Gates opened with the full matrix** (replacing `FailureMode.cs:602-609`): univariate/unparented
  parent ⇒ Secondary binding/dimension Errors + non-empty Y-chain Error + bivariate
  TRANSFORM/CONSEQUENCE-in-mode Errors, **but a bivariate RESPONSE is legal (collapse mode, decision
  12)** — it presents as a deterministic univariate response, stages/cascades included; bivariate
  parent + univariate response ⇒ both bindings legal, label
  continuity walks from the BOUND marginal's axis pair (warnings only — v1.1 label policy;
  documented deviation from §6.5's error language), Secondary consequence dimension legal only via a
  stamped binding with position ∈ [0, Y-chain count]; bivariate parent + bivariate response ⇒
  **Error unless `ResponseStages.Count == 1`** (the single-stage scope guard) + Error when
  `SecondaryLevelCount < 2` (joint evaluation needs an interpolable secondary axis) + label warnings
  on both axes + Y-chain entries validate like trailing transforms (bivariate transform INSIDE the
  Y-chain ⇒ Error — the Y-chain is univariate by construction).
- Consequence input routing contract (engine implements): univariate consequence + Primary ⇒
  transformed X signal at the bound position (exact v1.0); + Secondary ⇒ Y signal after
  `ConsequenceHazardPosition` Y-chain transforms; trailing `ResponseToConsequence` transforms fold
  after either (1-arg, axis-agnostic); bivariate consequence ⇒ `Evaluate(xBound, yBound)` (both
  axes consumed; `ConsequenceHazardDimension` inert; trailing transforms under it ⇒ Error).
- **`ResponseStage`: no structural changes** — polarity algebra applies to a bivariate response
  unchanged (Fail ⇒ surface, NonFail ⇒ complement); the single-stage rule lives on FailureMode.

### D. Engine integration (designed against the real engine; the §7.4 sketch is superseded)

**D.1 Placement — the conditional-bin loop lives entirely inside `SampledComponent.ComputeRisk`.**
The five engine call sites (1D AGK objective `RiskAnalysis.cs:2510-2515`; balanced-objective scale
probe `:2575-2594`; joint VEGAS integrand `:2967-2984`; γ probe `ProbeAnnualFailureProbability`
`:2778-2817`; tornado `HazardLevelSensitivity` `:1780-1852`) keep their shapes. Three engine facts
force this placement:
1. `Curve.ApplyRecordedMass` dedupes by abscissa keeping the first point (`Curve.cs:646-676`) — so
   bins must fold into **ONE `RiskPoint` per stream per X-evaluation**, with the entry lists
   enumerating (bin × pathway × branch) entries (the `RiskPoint` entry contract `RiskPoint.cs:17-23`
   makes this exact: the point's entries at X are the conditional-on-X mixture over Y).
2. `ContributionAccumulator.FinalizeFromLedger` dedupes rows by probability coordinate
   (`ContributionAccumulator.cs:97-108`) — contribution samples accumulate across bins, submitted
   once per (X, type).
3. Correct combination requires all modes evaluated at the same (X, Y_j) — see D.3.

Signature: `ComputeRisk(...)` gains ONE optional parameter `double hazardNonExceedance = double.NaN`
(the X-slice u; NaN ⇒ derive `Clamp(Hazard.CDF(hazardLevel), 1e-16, 1−1e-16)`). The 1D `Evaluate` and
the probe pass their true u; the VEGAS integrand passes its clamped local probability; sensitivity
leaves NaN. Univariate components never read it.

**D.2 `SampledComponent` shape — conditional-provider member, primary axis untouched.**
`SampledComponent.Hazard : IUnivariateDistribution` keeps its exact current meaning at every one of
the ~20 hazard-op sites (it IS the sampled marginal X — `SampleFunction(idx)` returns marginal X per
the contract). New members:
- `Core/Interfaces/IBivariateHazardFunction.cs` — `MarginalX`/`MarginalY` references,
  `SecondaryIntegrationBins` (default 20, range [3, 1000]), diagnostic `SampleConditionalYGivenX(int idx, double x)`
  (allocating, oracle/UI use), and the engine surface `SampledBivariateHazard SampleBivariate(int
  realizationIndex)`.
- `Results/SampledBivariateHazard.cs` — frozen per-realization snapshot: sampled `MarginalY`
  distribution, a CLONED copula (θ fixed; clone per realization per the interpolator-race spirit),
  bin count, and the non-allocating kernel `void FillConditionalBins(double u, double[] yNodes,
  double[] weights)`.
- `SampledComponent._conditionalHazard : SampledBivariateHazard?` (null ⇒ univariate) + pre-allocated
  `double[] _binY`, `_binW` (length bins+1).

**Discretization kernel (decision 7, Haden ratifies):** N bins ⇒ N+1 nodes `t_j = j/N`, clamped to
`[1e-16, 1−1e-16]` for inverse evaluations only (the engine's own probability floor);
`y_j = MarginalY.InverseCDF(copula.InverseConditionalCDF(u, t_j))`; trapezoid weights `w_0 = w_N =
1/(2N)`, `w_j = 1/N`, with the LAST weight computed as the residual `1 − Σ_{j<N} w_j` so **Σw = 1
exactly in floating point** (legacy residual-absorption precedent; required by the exhaustive-mass
discipline). Under Independence, y_j are plain marginal quantiles. `InverseConditionalCDF(u, t)` is a
NEW scalar non-allocating Numerics member (the existing `InverseCDF(u,v)` allocates a `double[2]`
per call — hot-loop violation); `ConditionalCDF(u,v)` (h-function) is added for oracles/diagnostics.

> **Superseded as the production interior (Haden's ruling, 2026-09-05):** the fixed
> conditional-trapezoid kernel above no longer integrates production runs. All bivariate hazards
> integrate through `AdaptiveGaussKronrod2D` over (u, z = Φ⁻¹(t)) with the φ(z) Jacobian folded into
> recorded masses (the additive/single-component interior, per-strip with fair-share budgets), and
> every fixed-u seat — the joint VEGAS evaluation, the AFP probe, hazard-level sensitivity, the
> endpoint columns — runs a per-slice 1D adaptive sweep in z budgeted at 21 × `SecondaryIntegrationBins`
> (the ruled must-have). No compatibility knob; hashes and seeds hold (nothing new is serialized);
> values move deliberately and the F8 byte gate re-pins. The trapezoid kernel survives verbatim in
> exactly one seat: `EstimateSecondaryDiscretizationError`'s quarantined fixed-grid instrument, whose
> Richardson limit is the standing independent cross-check of the adaptive answer.
> `SecondaryIntegrationBins` keeps its serialized/hashed place: it prices the instrument ladder and
> the fixed-slice sweep budget. Reference: [../technical-reference/bivariate-hazards.md](../technical-reference/bivariate-hazards.md).

**D.3 Per-FM semantics and combination — combine per (X, Y_j), then weight-sum. Required.**
Mixed Primary/Secondary/Bivariate modes are conditionally independent given (X, Y) but correlated
through the shared Y at fixed X. The combination kernels state conditional independence *at the
evaluation point* — true only at a full (x, y_j) point. Marginalize-then-combine errs by exactly
`−Cov_j(P_A, P_B)` — systematically underestimating joint failures for monotone fragilities (worst
under tail dependence). **The old §7.4 sketch sums per-FM independently with no combination at all —
it is wrong and gets rewritten.** Consequences:
- All combination kernels (joint pathway decomposition, common-cause, mutually-exclusive, unit
  masses) run per bin on per-bin SRPs — they are pure functions of per-bin unit probabilities + the
  captured MVN/coupling matrices; scratch arrays reuse across bins; zero cache/allocation impact;
  cost ×(bins+1).
- `SampledFailureMode` captures `_hazardBinding`, `_consequenceDimension`, the sampled
  `_secondaryTransforms` chain (from new `FailureMode.SecondaryHazardToResponse`), a per-realization
  `Bilinear` wrapper for a bivariate-response stage, and per-type bivariate consequence adapters
  (`(Weight, IUnivariateFunction, IBivariateSampledFunction)` branch shape — exactly one non-null).
  Constructor-computed `IsBinInvariant` lets Primary-bound modes compute SRP/consequences ONCE and
  reuse across bins.
- New internal staged members: `BeginBinnedEvaluation()` / `ComputeRiskBinned(x, y_j, w_j, ...)` /
  `CommitBinnedPoint(...)` (one RiskPoint per stream per X) / `SRPAt(x,y)` / `ConsequenceInputAt(x,y)`.
- Primary-bound FM: unchanged fast path — the univariate `ComputeRisk` body is guarded by a single
  `_conditionalHazard == null` check; **zero overhead and bit-identical for every univariate model**
  (F1–F7 byte gates must not move).
- Secondary-bound FM: stage chain runs with y_j as the signal origin (the projection routes the
  port-1 chain into the mode's stages); consequence input folds y_j through the bound position.
- BivariateResponse FM (single stage): `SRP_j = Surface.Interpolate(x', y'_j)` with x' through the
  primary-port chain and y'_j through the secondary-port chain (chains strictly per port, decision
  6); clamped [0,1]; consequence per `ConsequenceHazardDimension`; a bivariate consequence evaluates
  `Surface(xIn, yIn)` with both bound signals.
- NonFail modes participate per their own binding; exact excess pairs form PER BIN at the shared y_j
  (the shared-Y coherence the v1.0 oracle encodes — life loss keyed to stage).
- **No extra draws inside bins**: all sampling stays in the `SampledFailureMode` ctor; the bin loop
  only evaluates already-sampled functions at deterministic points. Draw count per realization
  unchanged; §5.5.4/§5.5.8 stream discipline untouched. Stated as an arch-doc contract + pinned.
- Multi-consequence: per-bin probability structure computed once per bin, shared across types;
  per-type kernels append w_j-scaled entries; secondary types still lazy.
- **Competing-risks guard (new validation error):** the competing CIF machinery pre-processes over
  200 PRIMARY hazard levels and cannot represent y-dependent SRPs. Rule: `CompetingFailures` with >1
  combination unit on a bivariate component requires every mode primary-bound; otherwise loud
  validation error + tracked future-work note. Single-unit competing stays legal for any binding.
- `InverseSRP` guard extends to Secondary/bivariate-bound modes (no monotone primary-axis inverse).

**D.4 Integration bookkeeping — nothing else moves.** The AGK objective still returns one scalar per
X (now marginalized). Both QuadratureMassLedger adoption gates hold unchanged BECAUSE of the
one-point-per-X rule + exact Σw=1. Endpoint rectangles unchanged (bin loop runs inside the endpoint
objective). `BuildHazardBins` stays on the primary marginal's support. The AFP probe returns the
y-marginalized failure probability — the correct VEGAS γ target with no modification. `RiskIntegrand`
members all operate on the marginalized output (TailConditionalRisk's α boundary is on the primary —
documented). Tolerance discipline (Tolerance vs EnsembleTolerance) unchanged. **`RiskAnalysisOptions`
gains NO field** — the bin count lives on the hazard function and enters identity through the
hazard's canonical hash; the options hash recipe is untouched.

**D.5 VEGAS system path — a bivariate component is ONE VEGAS dimension (its primary).** Bin loop
inside its per-component evaluation; MVN latent transform correlates primaries only (each component's
Y is conditionally independent of other components' Y given the primaries — copulas are internal to
their hazards; documented). The exclusive-combination odometer crossing per-component entries is
exactly correct under Y_A ⊥ Y_B | (X_A, X_B). Rejected alternative (recorded honestly in the arch
doc): a second VEGAS dimension per bivariate hazard — rejected because (1) N-node trapezoid converges
O(N⁻²) vs O(M^-1/2) MC on that axis; (2) equal-probability nodes already ARE the importance
transform; (3) dimension-count stability preserves seeds/strata/importance grids for every existing
joint model; (4) the recorded-mass semantics can't carry a per-component conditional-mass
factorization. Guardrail: `SystemComponent.EstimateRecordedFailureEntries` (`SystemComponent.cs:1005`)
multiplies by (bins+1) for bivariate hazards so the joint-entry limits price the cross product.

**D.6 Feature-by-feature behavior (all defined, none silent):** profiles + AEP-axis system response —
supported, primary-axis (one recorded point per X marginalizes Y). `HazardThreshold` — supported,
primary/profile axis. `ProfileHazardElementId` — PRIMARY-chain transform elements only; secondary-
branch selection = loud validation error + future note. Contributions — supported (per-bin
w_j-scaled accumulation into existing scratch; one ledger sample per (X,type); Σ-identities hold).
Sensitivity — supported; `CollectSensitivityInputs` gains an explicit branch enumerating MarginalX
then MarginalY (labels "… - Marginal X/Y Hazard"); tornado axis = primary/profile. Reliability mode —
supported (AFP = marginalized Fail total; assurance over primary annualized probability). Scalar
uncertainty summary — unchanged. §5.5.8 seed pin — supported (one hazard ordinal pins the subtree).
EAD/multi-consequence — supported. Cascades/trees — decision-10 guards: BivariateResponse single
stage only IN JOINT MODE (in collapse mode under a univariate hazard it participates as an ordinary
deterministic univariate response — stages/cascades legal, decision 12); none inside composites in
either mode; no CompositeHazard over bivariate; nested copulas rejected; tree/composite responses
on a secondary-bound mode REMAIN LEGAL (univariate responses on the y-chain). The tree-source arm
of decision 10 was later retired under the ratified transform-mapped-source approval (2026-09-05,
EVENT_AND_FAULT_TREE_RESPONSE_DESIGN.md §3.2): a bivariate response is a legal tree probability
source when a declared `BivariateSourceAxis` names the tree-driven axis and a hazard-transform
chain supplies the other surface coordinate — the silent weight collapse the guard prevented
cannot occur there, and an undeclared bivariate reference keeps the original refusal.
CompetingRisks — the D.3 guard. SystemConvolution — unaffected (consequence space).

**D.7 Performance/allocation:** pre-allocated bin buffers; per-mode staging lists reused; Bilinear
wrappers built per realization over shared grids (never shared across threads); cost ≈ (bins+1)× per
X-eval on bivariate components only (default 20 ⇒ ~21×); Balanced pre-pass and Brent threshold probes
inherit the factor (documented). New perf fixture **F8** (single bivariate component, independence,
20 bins, uncertain tabular marginals, secondary-bound FM + BivariateResponse FM, N=1000) with
committed byte-gate hash + allocation counters; **F1 hash unchanged is the univariate zero-overhead
proof**. Guard shape: one `_conditionalHazard == null` branch selecting today's body verbatim (no
`binWeight=1.0` threading through pinned kernels).

**D.8 Engine file-change list:** `Core/Interfaces/IBivariateHazardFunction.cs` (new);
`RiskFunctions/Hazards/BivariateHazard.cs` (new); `Results/SampledBivariateHazard.cs` (new);
`Results/SampledComponent.cs` (conditional member, bin buffers, bivariate ComputeRisk body,
hazardNonExceedance, competing-guard consumption); `Results/SampledFailureMode.cs` (binding capture,
secondary chain, Bilinear wrappers, bivariate consequence branches, SRPAt/ConsequenceInputAt, staged
Begin/ComputeRiskBinned/Commit, InverseSRP guard); `Analyses/RiskAnalysis.cs` (pass
hazardNonExceedance at :2514/:2787/:2977-2982 — minimal surgical edits); `Systems/Components/
SystemComponent.cs` (SetupSamplers subtree absorption via the dedup set, projection stamping +
off-path secondary chain collection, CollectSensitivityInputs marginal branch,
EstimateRecordedFailureEntries ×(bins+1), profile-element primary-chain validation, §6.5 matrix);
`Systems/Components/FailureMode.cs` (open gate 4 at :602-608, `SecondaryHazardToResponse` member
with walk-ordinal note: appended positions keep existing streams a stable prefix); graph gates 1-3
and 5
(`HazardElement.cs:116`, `ResponseElement.cs:213/:410-413`, `ComponentGraph.cs:434-444`);
`scripts/perf/PerfHarness` F8 + RESULTS.md row.

## Implementation phases

Per-session exit gates (every session): `dotnet build` 0 warnings → fast suite (`dotnet test -c
Release`) → session-targeted verification families run ISOLATED → `validate-code-xml-docs.ps1` →
matrix/PROGRESS updates → commit. One session per sitting.

| Session | Scope | Depends on |
|---|---|---|
| **S1 — Numerics copulas** (repo C:\GIT\numerics) | `IndependenceCopula` (0-parameter); `ConditionalCDF` h-function + scalar `InverseConditionalCDF` for ALL families; `CopulaType.Independence` appended (=7); `CopulaFactory` + copula XML serialization; estimation guard for 0-parameter copulas; full Test_Numerics suites; `docs/distributions/copulas.md` | — |
| **S2 — BivariateHazard type** | `IBivariateHazardFunction`; `BivariateHazard` (Id links + lenient-name resolver repair, both serialization modes, projected-identity hash, composite-forward seeding, validation incl. univariate-marginal + scope guards); `SampledBivariateHazard.FillConditionalBins`; unit tests (bin math, Σw exactness, hash/rename/mode invariance) | S1 |
| **S3 — Bivariate tables** | `BivariateTransform` + `BivariateConsequence` (+ shared deterministic 2-D table support, XML, validation, extrapolation policy); unit tests | S1 (parallel with S4) |
| **S4 — BivariateResponse port** | v1.0 collapse behavior (stored `SecondaryHazardFunction` link with Id/lenient-name resolution, EstimateWeights incl. no-arg overload, manual weights, payload parity, BRF-semantics validation, staleness warning — no load-time re-derivation), secondary axis type/unit, surface mode; collapse-parity unit tests | S1 |
| **S5 — Graph + FailureMode** | Open the five reserved gates; projection (HazardBinding stamping from ports, off-path secondary chain → `FailureMode.SecondaryHazardToResponse`, walk ordinals); §6.5 validation matrix + all scope/competing/profile guards; unit tests | S2, S4 |
| **S6 — Engine** | The full D.2–D.7 machinery; hazardNonExceedance plumbing; sensitivity marginal columns; EstimateRecordedFailureEntries; F8 fixture. Extra gates: **F1 byte-gate hash unchanged** (univariate zero-overhead proof); F8 baseline row committed; existing `EngineReproducibilityVerification` green isolated | S2–S5 |
| **S7 — Verification + docs close-out** | `BivariateRiskVerification` + `CopulaDependenceVerification` + reproducibility additions; both verification pages; traceability rows + validator; all doc amendments (arch doc, ROADMAP re-slice, tech-ref, the repository guidance, PROGRESS) | S6 |

## Testing

**Fast unit tests** (`RMC.TotalRisk.Tests`; one `<ClassName>Tests.cs` per new public class, folders
mirroring src; `Test_<Scenario>_<ExpectedResult>` naming, Arrange/Act/Assert, explicit deltas):

- `RiskFunctions\Hazards\BivariateHazardTests.cs` — default-ctor state (Independence, bins 20, null
  marginals); convenience ctor; null-copula coercion; INPC for every property incl. marginal-swap
  subscription semantics (edits to a swapped-out marginal raise nothing); `CopulaTheta` passthrough;
  the full 9-rule Validate matrix (one test per rule incl. non-univariate marginal via a second
  BivariateHazard, same-instance marginals, bins 2 and 1001 invalid + 3 and 1000 valid);
  `SetupSampler` — throws-before-valid,
  exact child seeds (`HashCombine(seed, child.CanonicalHash(), ordinal)` asserted against
  independently constructed expectations), posterior-capacity throw, equal-content-distinct-marginals
  draw independently (corr ≈ 0), `SamplingDimensions == 0`; delegation (SampleFunction trio → X,
  SampleSecondaryFunction trio → Y, bounds, throws-when-unusable); **discretizer** — node count,
  Σw = 1 (≤1e-12), endpoint clamps at 1e-16/1−1e-16, Independence ⇒ y_j = marginal quantiles at t_j,
  Clayton vs `ConditionalCDF` round-trip, buffer-length throw, pre-setup throw, convenience overload
  agrees with buffer overload; XML round-trips BOTH modes (SelfContained bit-equal re-serialization;
  ByReference markers + resolver re-links live instances, stale-id throw, lenient-name null +
  Validate report; StudentT two-parameter copula; missing `<Copula>` ⇒ Independence); factory
  round-trip; **hash invariance** — rename self/marginals inert, marginal re-Id inert, all four
  labels inert, MODE-invariant, θ/ν/copula-type/bins/marginal-content move it, X↔Y swap moves it;
  component-level mode-invariance (`SerializationModeTests` pattern).
- `RiskFunctions\Transforms\BivariateTransformTests.cs` + `Consequences\BivariateConsequenceTests.cs`
  — ctor defaults; INPC; validation matrix (short/non-ascending/non-finite axes, dim mismatch,
  log/NormalZ guards per axis + output, empty labels; consequence negative-cell warning);
  known-point bilinear values (corners, interior, each transform incl. log axis + NormalZ output);
  **pinned extrapolation policy** (corner clamp, 1-D edge extrapolation); `Evaluate` ≡
  `CreateInterpolator().Interpolate`; fresh interpolator per call; 1-arg surfaces throw
  `NotSupportedException` (+ exposure-branch members on the consequence; `CountExposureBranches()==1`);
  deterministic/D=0; XML round-trip (Row shape; ragged payload → Validate error, never truncation);
  factory round-trip; hash (labels inert; every axis value/cell/transform enum moves; transpose moves).
- `RiskFunctions\Responses\BivariateResponseTests.cs` — legacy-parity block (collapse Σz·w known
  values incl. a 4×6 Isabella-style fixture; `OrderedPairedData` flags — strict-ascending x,
  non-monotone y allowed; `EmpiricalDistribution` wrapper CDF spot-checks under Log/NormalZ;
  percentile/index overloads identical to mean; IsDeterministic; IsMonotonic on the collapsed curve;
  min/max); `EstimateWeights` (single-level ⇒ 1; hand-computed Voronoi F(μ) differences; residual
  absorption BOTH directions; sets UseManualWeights=false; no-arg overload uses the stored link and
  throws when null/unresolved/invalid; explicit overload stores its argument as the link; NO
  derivation on load or on link-set — serialized weights survive a round-trip unchanged, the
  fixed-bug pin); stored-link surface (INPC + subscription swap; link-attribute round-trip; resolver
  repair by Id, lenient-name fallback, miss ⇒ null; bivariate link ⇒ Error; staleness Warning when
  UseManualWeights=false and derived ≠ stored; unresolved-link Warning); grid auto-resize on
  collection add/remove preserving overlap; augmentation (`SurfaceProbability` known points incl.
  `SecondaryHazardTransform`; [0,1] clamp; extrapolation; interpolator freshness); full BRF-derived
  Validate matrix; XML round-trip (legacy child names `Probability_Row`/`WeightedHazardLevel`
  pinned, bit-equal); factory (resolver threaded); hash (weights/levels/grid/three transforms move;
  `UseManualWeights` + link attributes + labels + rename/re-Id inert; **editing the LINKED hazard's
  content leaves the response hash unchanged**).
- `RiskFunctions\Responses\WeightedHazardLevelTests.cs` — INPC, exact attribute round-trip,
  permissive reads, Clone independence.
- `Core\Enums\FunctionTypeDiscriminatorTests.cs` — four appended members pinned + report-own-type +
  not-serialized asserts. `Core\CanonicalizationRulesTests.cs` — three new stripped names present,
  append-only order. `Core\HashInvarianceKitchenSinkTests.cs` — four registry entries.
  `RiskFunctions\RiskFunctionFactoryTests.cs` — four round-trip + cluster-filter cases.
- Scope guards: one test each in `CompositeHazardTests`/`CompositeResponseTests`/
  `CompositeTransformTests`/`CompositeConsequenceTests` (bivariate child ⇒ Error) and
  `Trees\ProbabilitySourceTests` (bivariate referenced response ⇒ Error).
- Graph: `HazardElementTests` (OutputCount 1→2 + INPC; port-1 bounds; univariate + port-1 Error);
  `TransformElementTests`/`ConsequenceElementTests` (SecondaryInput INPC; `SecondarySource*` triple
  round-trip + pending resolution + clone remap; arity incl. consequence collection-change raise;
  the validation pairs + mixed-list Error); `ResponseElementTests` (gate flips; InputCount);
  `ComponentGraphTests`(+`ComponentGraphBivariateTests.cs`) — the FULL wiring matrix (~14+ methods,
  one per legal and illegal row), `TryResolveSecondaryChain` cases (direct port-1, multi-transform
  chain order, passthrough hop, z-into-secondary Error, response-source Error, dangling, cycle),
  `GetAvailableHazardSources` port-1 + Y-chain options, binding-target rules, snapshot/rollback of
  secondary slots, detach clearing, unused-port-1 warning, single-stage guard (joint mode), Y-chain
  reachability; **collapse-mode wiring rows** — bivariate response element under a univariate root
  with null SecondaryInput legal (incl. in multi-response paths), with connected SecondaryInput
  Error; bivariate transform/consequence under a univariate root Error.
- Components: `SystemComponentTests`(+`SystemComponentBivariateProjectionTests.cs`) — HazardBinding
  stamped from port-1 root edge; Y-chain collected in order; binding → (Secondary, position) for
  raw-Y (0) and k-th Y-transform (k+1); **existing univariate fixtures project bit-identically (the
  regression pin)**; SetupSamplers walk — Y-chain seeded after trailing transforms, **existing-model
  ordinals/seeds captured before/after the change and asserted identical**; profile-selector rejects
  Y-chain transforms; `GetReferencedFunctions` includes marginals once. `FailureModeTests` — all
  gate rules (univariate parent rejections for transform/consequence/Y-chain/bindings; **bivariate
  response under a univariate parent LEGAL — collapse mode, incl. as a cascade stage**; bivariate
  parent acceptances; single-stage in joint mode; SecondaryLevelCount < 2; position bounds;
  label-continuity warnings); serialization —
  `SecondaryHazardToResponse` ABSENT when empty (**byte-identical XML for an existing mode — the
  hash-preservation pin**), present round-trips + hashes when populated; Clone carries the Y-chain.
- Engine-side fast tests: `FillConditionalBins` node math vs hand values; projection stamping;
  `EstimateRecordedFailureEntries` ×(bins+1) scaling; competing-guard validation.
- **Verification families** (run isolated, one at a time): `BivariateRiskVerification`,
  `CopulaDependenceVerification`, additions inside `EngineReproducibilityVerification`.
- **Numerics tests** (Test_Numerics): `Test_IndependenceCopula`; h-function vs central finite
  difference of CDF for ALL 8 families; inverse-conditional round-trip
  `ConditionalCDF(u, InverseConditionalCDF(u,t)) == t`; copula XML round-trips; factory full-enum
  coverage; `Test_ParameterValidity` extension; estimation-guard test.

## Verification

**Legacy oracle conversions** (oracles re-implement from Numerics primitives; engine-vs-oracle
statistical, never bit-exact):
1. `Test_Bivariate_Risk` → `BivariateRiskVerification.Test_BivariateRisk_EngineVsLegacyOracle`:
   MersenneTwister(45678), 1M (legacy 100M), pinned draw order (pga, stage, rnd); the 4×6
   `BivariateEmpirical` surface (Log prob transform); engine scenario = BivariateHazard
   (independence; the two curves as deterministic TabularHazards) + BivariateResponse FM + univariate
   consequence bound SECONDARY. Tolerances: EAD at k·σ̂/√N (k=4, σ̂ in-run); FN ordinates at binomial
   k·√(p(1−p)/N) on a probe set, tail bins <~100 expected exceedances excluded; engine run at
   bins=1000 (near-exact) AND bins=20 with the discretization allowance measured by the convergence
   study.
2. `Test_Bivariate_SRP` → closed-form target: `E_Y[CDF(0.8, Y)]` is EXACTLY computable (bilinear
   surface piecewise-linear in y × piecewise-linear marginal ⇒ closed-form sum over the union grid).
   Engine probe via degenerate primary `Deterministic(0.8)` ⇒ AFP = marginalized SRP; bins=1000 vs
   closed form at ~1e-6 relative; bins=20 at the pinned discretization error; legacy 100M value kept
   as k·SE cross-check constant.
3. `Test_DAMRAE` PFM-08 → **re-anchored as a NEW pinned oracle with MersenneTwister** (legacy used
   BCL Random; print-only, no constants to preserve; recorded in traceability Notes). Four chained
   Bilinear surfaces through bivariate transforms + response; 1M; k·SE per output.

**New copula-dependence oracles** (`CopulaDependenceVerification` — greenfield):
independence exactness (bilinear × uniform ⇒ trapezoid exact at ANY N; engine vs iterated closed form
~1e-10 relative); Normal-copula closed form (h-function Φ((Φ⁻¹(v)−ρΦ⁻¹(u))/√(1−ρ²)); bivariate-normal
E[z(X,Y)] analytic); **Clayton** for the Archimedean case (analytic h-function AND analytic inverse
conditional); N-bin convergence study pinning errors at N ∈ {20,100,1000} (asserts ~O(N⁻²), documents
default-20 adequacy, feeds tolerance allowances); Gumbel upper-tail orientation pin (dependence
strictly raises joint-extreme AFP vs independence + h-function matches finite difference — the
sign-error tripwire for the (u,v) convention); marginal-uncertainty propagation via injected
posteriors ⇒ realization-for-realization parity vs hand-rolled dense integral (~0.1% documented).

**Reproducibility pins** (in `EngineReproducibilityVerification`): same-seed bit-identity at thread
counts {1,4,unbounded} with bins; rename/reorder/canvas/mode invariance incl. marginal-link rename
and ByReference↔SelfContained; seed stability adding an unrelated function; §5.5.8 pin × bins
(perturb MarginalY on an all-primary-bound component + PinnedSamplerSeeds ⇒ bit-identical, because
the integrand never reads Y; plus a consequence-bearing secondary perturbation asserting pure
parameter effects).

**v1.0 collapse parity + collapse-vs-joint consistency (decision 12):** EstimateWeights vs hand
values; collapse Σ z·w vs the SRP closed form; manual-weights round-trip (fast tests + verification
tie-in). NEW `BivariateRiskVerification.Test_CollapseVsJoint_Consistency`: the SAME surface run (a)
v1.0-style — collapse mode under the univariate primary hazard, weights auto-derived from hazard
H_Y, and (b) new-style — under `BivariateHazard(X, H_Y, Independence)` in joint mode; both estimate
`E_Y[surface(x, Y)]`, so total risk agrees within the two documented discretization allowances
(secondary-level Voronoi weights vs conditional trapezoid bins; both tightened to demonstrate
convergence). This is the deliberate cross-anchor between the preserved v1.0 method and the new
bivariate-hazard path.

**Traceability:** `legacy-traceability.csv` rows 24-25 (`Test_BivariateRisk.vb`) →
Covered with class/method; `Test_DAMRAE.vb:14` → Covered with the re-anchoring note (correct its
current misattribution); `validate-verification-traceability.ps1` green.

## Documentation updates

- **Arch doc** (`docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md`): §6.1.2 REWRITE (one type
  `BivariateHazard`, Id-linked marginals, bins 20/1000, Independence default, engine surface;
  BestFit types moved to the import phase); §6.5 confirm + guards; **§7.4 FULL REWRITE** against the
  real engine (bin loop placement, one-point-per-X, per-bin combination with the covariance argument,
  ledger-gate invariance, endpoint policy, γ probe, VEGAS one-dimension rationale + rejected
  alternative, cost model); §5.8.4 rows (all new types D=0); §11 Q-P deferred / **Q-Q resolved to the
  composite forward rule** / Q-R kept with the deterministic default recorded; §3 namespace rows; §8
  marginal-link FunctionReference notes; version-history entry.
- **ROADMAP re-slice**: Phase 11 = *Bivariate risk analysis* only (full scope text); new **Phase 11B
  — External imports + LifeSim** (`BestFitBivariateHazard` — θ posterior/copula uncertainty arrives
  here; `BestFitTabularHazard` — Q-P resolves here; `BestFitTransform`; `RFAHazard`;
  `LifeSimConsequence`; BestFit import contract test; the "full v1.1 input-function surface P/T/V"
  exit criterion moves here).
- **Technical reference**: NEW `docs/technical-reference/bivariate-hazards.md` (copula conditional
  integration math with exact trapezoid formulas + endpoint/clamping policy; (u,v) non-exceedance
  convention + exceedance conversion + tail-orientation consequence; marginal linking + seeding;
  worked PGA/pool-duration example end-to-end; cost model; scope guards); `response-functions.md`
  (surface mode vs collapse mode, secondary axis, monotonicity cross-ref); transform/consequence
  pages (two-port cascade semantics, Bilinear evaluation + extrapolation policy).
- **Repository guidance**: matrix rows for the four new types; remove "bivariate" from the Later row;
  namespace-table rows; Numerics gotchas additions (`Bilinear` in `Numerics.Data` + not thread-safe;
  `InverseCDF(u,v)` second arg is a CONDITIONAL probability; LatinHypercube seed==0 unseeded;
  CopulaType append-only; 0-parameter copulas break estimation; AMH "AHM" typo).
- **Verification pages**: `docs/verification/bivariate-risk.md` + `docs/verification/
  copula-dependence.md` (scenario tables with fixture provenance, per-assert tolerance derivations
  incl. bin-discretization figures, runs-of-record); `engine-reproducibility.md` additions.
- **PROGRESS**: per-session entries (Goal/Landed/Verified/Next, exact counts, runs-of-record, commit).
