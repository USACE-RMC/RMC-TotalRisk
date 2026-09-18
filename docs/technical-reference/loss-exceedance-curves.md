# Loss Exceedance Curves and Risk Measures

> Technical reference for how the `RMC.TotalRisk` engine builds loss exceedance curves (LECs, a.k.a.
> F-N curves) and the risk measures derived from them. Companion:
> [risk-integration.md](risk-integration.md) (how the integrator's evaluation points are produced).
> Normative spec: [../requirements/MODEL_LIBRARY_ARCHITECTURE.md](../requirements/MODEL_LIBRARY_ARCHITECTURE.md)
> §7.7, §7.8. The v1.0 sources cited for defect evidence (`Curve.vb`, `RiskAnalysis.vb`,
> `SampledComponent.vb`, `ComponentRiskOutput.vb`) are in the v1.0 reference repository.

A **loss exceedance curve** plots, for each consequence magnitude `c`, the annual probability that the
realized consequence exceeds `c`. The engine builds five of them per component and per system — one per
`RiskType`: `Excess` (incremental), `Background` (irreducible), `Total`, `Fail`, `NonFail`. Every risk
measure the engine reports is a functional of these curves.

## Input: risk points

The adaptive integrator ([risk-integration.md](risk-integration.md)) records a cloud of `RiskPoint`s.
Each carries the hazard level, the hazard probability mass (`dF`), and — because a failure mode can
have multiple response branches and multiple consequence types — **parallel lists**
`ResponseProbabilities[k]` and `Consequences[k]`. The LEC is built by walking every `(point, k)` pair:

```
mass_k       = point.HazardProbabilityMass * point.ResponseProbabilities[k]
consequence_k = point.Consequences[k]
```

The full set of `(mass, consequence)` pairs is the empirical loss distribution. Everything below is how
the engine turns that set into an exceedance curve and its moments **accurately** — the v1.0 code did
it in a way that is correct in the mean but wrong in the tail.

## Probability mass

Mass should come from the **quadrature weight** of the evaluation point — the `dF` it represents. The
VEGAS path already does this: it uses `wgt` directly (legacy `RiskAnalysis.vb:3030`, `:3074-3077`). The
1D path does **not**: v1.0's `ProcessHazardProbabilities` (`Curve.vb:304-316`) throws the weights away
and *re-derives* mass by sorting risk points on `p` and midpoint-partitioning the gaps between them —
a trapezoidal `dF` heuristic that is only as good as the point spacing and breaks on any duplicate `p`.

**Current state:** the 1D path records exact accepted AGK weights on the hazard's natural probability
support through the acceptance-aware quadrature recorder. An internal pooled ledger
(`QuadratureMassLedger`) adds the lower and upper endpoint rectangles required by Appendix D,
coalesces duplicate abscissas with compensated summation, and seals the exhaustive mass to exactly
one; every recorded curve then consumes the sealed entries (`ApplyRecordedMass`) before its LEC is
built. Endpoint evaluations travel through the same component-risk recording path as interior
nodes. The published `TotalProbability` is always the recorded compensated mass; `Curve` never
substitutes one because a curve is marked exhaustive.

The mass correction is structural rather than proportional: the AGK interior weights remain unchanged.
A lower atom of mass `p_min` and an upper residual atom complete the distribution. Invalid negative,
non-finite, or materially excessive mass faults the run.
## Building the exceedance curve

### The v1.0 histogram (dropped)

v1.0 bins consequences into `LECOutputLength` (default 200) **log10 bins**:

```
idx = floor( (log10(c + offset) − binLower) / binDelta )     // Curve.vb:375
Bins[idx].Weight += mass
```

then forms the exceedance curve as the reverse-cumulative sum of bin weights, plotted at each bin
**midpoint** (`Curve.vb:397-399`). Two defects:

1. The index map (`binOffset`/`binLower`/`binDelta`) and the `Stratify`-built bin edges are **two
   separate constructions** that can disagree about which bin a consequence falls in.
2. Plotting the cumulative probability at the bin **midpoint** biases every ordinate — the exceedance
   probability at the midpoint is not the sum of the mass at-or-above the midpoint.

The histogram also conflates output resolution with compute resolution: `LECOutputLength` controls both
how many ordinates you get *and* how finely mass is discretized before the moments are taken.

### The exact construction (v1.1)

The engine builds the curve **exactly** from the pairs, then thins for output:

```
1. Collect all (mass, consequence) pairs from the risk points.
2. Sort by consequence DESCENDING.
3. Accumulate exactly:  the exceedance probability at consequence c[i]
   is the running sum of mass for all pairs with consequence ≥ c[i].
4. Thin to `LECOutputLength` by repeatedly retaining the original ordinate with the largest log-log interpolation error,
   retaining required anchors and breaking equal-error ties by original index.
```

This is `O(n log n)` (the sort), carries no binning bias, and — crucially — makes `LECOutputLength` an
**output-resolution knob only**. The moments and risk measures are computed from the exact accumulation
before thinning, so shrinking the output curve never degrades a reported statistic. This is the single
biggest accuracy win for tail behavior.

The result is stored as a Numerics `OrderedPairedData` (X = consequence descending, Y = exceedance
probability ascending), preserving the v1.0 curve orientation so downstream `GetXFromY` / `GetYFromX`
interpolation is unchanged.

Two identities anchor interpretation [23]: the curve's **maximum exceedance probability** equals α
(the APF on the failure and excess streams; 1 on the exhaustive streams), and the **area under the
curve on a linear scale equals the mean**, E[C] = ∫₀^∞ S(c) dc — a steep curve is
frequent-but-modest risk, a fat-tailed curve rare-but-catastrophic risk. The USACE tolerable risk
limit is defined directly on this curve, which is why it is the primary basis for guideline
comparison.

## Moments (weighted, numerically stable)

v1.0 computed the mean, standard deviation, skewness, and kurtosis from **raw power sums**
(`Curve.vb:370-389`): `u1 = Σ mass·c`, `u2 = Σ mass·c²`, … then `m2 = sqrt(u2 − u1²)` and a fully
expanded fourth central moment. These **catastrophically cancel** when the mean is large relative to
the spread — the normal case for life-loss consequences, where `u2` and `u1²` agree to many
significant figures and their difference loses most of them.

v1.1 uses a **two-pass weighted central-moment accumulation** instead: the first pass forms the
weighted mean, the second accumulates the central sums `M2`, `M3`, `M4` about it. This keeps full
precision regardless of the mean-to-spread ratio — a deliberate numerical improvement over the v1.0
body, documented in `Curve`'s XML `<remarks>`; the mean itself is algebraically unchanged, so the
v1.0 reference results are preserved.

Central moments then give: mean = weighted mean; standard deviation = `√M2`; skewness = `M3 / M2^{3/2}`;
kurtosis = `M4 / M2²`.

## Mass leakage

`Fail`, `Excess`, and `NonFail` are **defective** distributions (`IsExhaustive = false`,
`TotalProbability < 1`); `Background` and `Total` are exhaustive (`TotalProbability = 1`). v1.0 forced
`totalProbability = If(IsExhaustive, 1, Math.Min(Σ mass, 1))`, silently hiding a mass
budget that came out wrong. v1.1 publishes the compensated recorded mass directly. An exhaustive
stream must equal one exactly after the endpoint-ledger construction; non-finite, negative, or
materially excessive mass faults the run rather than being masked.

## Risk-measure catalog

Every measure is a functional of the finished LEC. Definitions and the v1.0 fixes:

| Measure | Definition | Notes / fixes |
|---|---|---|
| `TotalProbability` | max exceedance probability of the curve | = annualized P(failure) on the `Fail` curve |
| `Mean` | 1st raw moment `Σ mass·c` | = EAD (expected annual damage) / mean annualized risk; **unchanged by the tail fixes** |
| `ConditionalMean` | `Mean / TotalProbability` | expected consequence **given** the curve's event occurs |
| `StandardDeviation` | `√M2` (two-pass weighted central accumulation) | v1.0's raw-power-sum form cancels — fixed |
| `Skewness` | `M3 / M2^{3/2}` | as above |
| `Kurtosis` | `M4 / M2²` | as above |
| `ConsequenceThresholdProbability` | `LEC.GetYFromX(ConsequenceThreshold, Log, Log)` | assurance: P(consequence > threshold) |
| `HazardThresholdProbability` | from the `HazardFrequency` profile at `HazardThreshold` | |
| `ValueAtRisk` | consequence quantile at level α = `Options.Alpha` | returns **0** (not the minimum consequence) when `α > TotalProbability` — no loss is exceeded at that level; v1.0 returned the curve's smallest consequence (`Curve.vb:544`) |
| `ConditionalValueAtRisk` | `(1/α)·∫ VaR(p) dp` over the α-tail, computed as the **exact segment-by-segment closed form** of the piecewise log-log LEC quantile | expected shortfall — the coherent tail measure [17], [18]; v1.0 ran adaptive quadrature at library defaults on its steepest integrand |
| `LEC` | the curve itself (X = consequence, Y = exceedance prob) | the F-N curve |
| `HazardFrequency` | hazard level vs cumulative exceedance probability | risk profile (1D path only — needs `HazardLevel` on the points) |
| `HazardvsCEN` | hazard level vs conditional expected consequence | risk profile |

One more v1.0 inconsistency, corrected in the uncertainty post-processing: v1.0's
`PostProcessUncertainty` reconstructed the **Total** percentile curve as `fAEP + nfAEP`
(`RiskAnalysis.vb:3369`) rather than reading the Total LEC. The v1.1 percentile assembler reads the
Total LEC directly — the Total curve is already built exactly.

### The conditional mean loss summary point

The Conditional Mean Loss Plot (the α–η plot) condenses a stream to the single point
(`ConditionalMean`, `TotalProbability`) — how likely the conditioning event is against how severe
it is when it occurs, with diagonals as lines of constant mean risk (E[C] = α·E[C|F]) [23].
Because the conditional mean is the center of mass of the conditional loss distribution, the TRL
screening logic is one-directional: a point **below** the tolerable risk limit guarantees the
entire LEC is below it, but a point **above** proves nothing — two structures can share the point
and differ entirely in curve shape, so the LEC remains the primary basis for guideline
comparison.

### Cross-software EAD comparison

The 2024 verification report's HEC-FDA comparison (Beargrass Creek EAD, [8] §12) traces the
residual differences between the two programs to three named sources — nonparametric-hazard
uncertainty treatment, damage-curve aggregation, and interpolation-transform choices — all three
of which are configuration semantics documented on these pages rather than integration error.

## System aggregation

After each component has its five LECs, the system LECs are assembled per `SystemRiskMethod`.

### Additive method — strict independence + exact lattice convolution

The additive method is **redefined in v1.1 to assume the components are strictly
independent.** Validation: `SystemRiskMethod = Additive` with `ComponentHazardDependency ≠ Independent`
is an **Error**; the correlation matrix applies to the joint method only. The v1.0 additive path
combined only the first two moments — means added, variances combined through the correlation matrix —
and produced **no system LEC at all** (a warning told the user so). Under independence we can do far
better: build the true system LEC by convolution.

**Zero-inflation makes convolution equal to full combination enumeration.** The `Fail`, `Excess`, and
`NonFail` component curves are defective — with probability `1 − TotalProbability` the component did not
fail and contributes **zero** consequence. Make each curve a proper distribution by adding an atom at
consequence 0 with mass `1 − TotalProbability`. Convolving the D zero-inflated distributions is then
**exactly** the enumeration of all `2^D` component failure/non-failure combinations (each convolution
term is "this component failed and contributed `c`, or it didn't and contributed 0"), in `O(n log n)`
instead of `2^D`, and it reproduces the full tail rather than a conditional mean.

**The atoms force a lattice.** `EmpiricalDistribution.Convolve` cannot host this construction: it
samples continuous
`PDF`s on a uniform grid, and a distribution-function jump —
the zero atom — has no finite density. Any ramp-width approximation either loses the atom or corrupts
the sampled density and its renormalization, and the per-stage PDF-normalize/regrid chain cannot hold
the 1e-6 mean-parity gate. The engine therefore convolves **exactly on a shared consequence lattice**
(`RMC.TotalRisk.Analyses.Risk.SystemConvolution`, public):

```
1. Lattice: step Δ = (Σ component maxima) / (SystemConvolutionPoints − 1); node k ↔ consequence k·Δ.
2. Per component: bin its exact recorded (mass, consequence) pairs — Curve.CollectRecordedPairs() —
   with the moment-preserving two-node split (mass divides between the bracketing nodes so the
   pair's first moment is preserved exactly); add the zero atom 1 − Σmass at node 0.
3. Convolve the lattice mass vectors pairwise by Fourier.FFT (zero-padded, power-of-two complex
   length — the same Numerics primitive Convolve uses internally), in canonical-hash component
   order so declaration order can never move the result by association-rounding.
4. Feed the system lattice into the shared exact construction: Curve.CreateCurve(pairs, LECOutputLength).
```

Mass and first moments are preserved exactly at every step, so **the convolved system mean equals Σ
component means to floating-point roundoff by construction** — the v1.0 additive answer, now with the
full curve. Higher moments carry an O(Δ²) binning quantization that `SystemConvolutionPoints ≥ 4096`
keeps far below sampling error (and under independence the v1.0 σ answer, `√Σσᵢ²`, is reproduced too).

- `Background` is already exhaustive (convolve directly); convolve the component `Total` curves for
  the system `Total`. The lattice zero node is **kept** on exhaustive streams (it is real probability
  at zero consequence, and the recorded budget stays exactly one) and **dropped** on defective streams
  (it is the no-event atom; zero-valued events quantize into it).
- **Stream probabilities keep the v1.0 system-state semantics** after the curve is built: `Fail` and
  `Excess` carry `pF = Probability.IndependentUnion(pfs)`, `NonFail` carries `1 − pF` — the values the
  v1.0 additive summary reported — while the curve mass distribution comes from the convolution.
- **The lattice kernel is deliberately local and permanent.** Migrating `SystemConvolution` onto
  the Numerics discrete-convolution helper was audited and declined: that kernel is pairwise and
  re-derives its lattice step from its two operands, so an N-way fold re-bins the running result
  at a changing step and its two-node atom deposit smears once per fold — where the local kernel
  bins once onto a lattice sized to the summed support and folds by integer shift.
- **Verified** ([../verification/system-risk.md](../verification/system-risk.md)): mean, σ, failure
  union, tail exceedances, VaR, and CVaR of the convolved system curve against a brute-force
  event-level Monte Carlo oracle at N = 10⁶ — every deviation within ~1.2 oracle standard errors.

### Joint method — real combination enumeration

The joint method integrates over correlated hazards with VEGAS
([risk-integration.md](risk-integration.md) §VEGAS). The v1.0 defect was upstream of the
integration: the integrand consumed only **per-component conditional means** (`fC(i) =
MeanFailureConsequences`, `nfC(i)`, `iC(i)` — single scalars, legacy `RiskAnalysis.vb:3030-3047`)
and combined those scalars across component failure/non-failure combinations. The v1.0 system F-N
curve therefore convolved conditional means, discarding each component's within-consequence spread —
a capability v1.0 declared but never implemented: the legacy source carries the TODO for it
(`ComponentRiskOutput.vb:39`: *"These lists below will need to be used to compute precise system FN
curves in the future"*) alongside a double-increment of the total failure probability
(`RiskAnalysis.vb:3117`, `:3129`).

v1.1 activates the parallel `ResponseProbabilities` / `FailureConsequences` / `ExcessConsequences`
lists on `ComponentRiskOutput` — the design intent v1.0 left commented out — so the VEGAS integrand
enumerates the true within-component consequence distribution across the system combinations; the
exhaustive mass budget is verified to self-normalize to exactly one, which a double count would
break.

## The two paths converge

Both the 1D and the multi-D paths reduce to the **same** LEC construction: a set
of `(mass, consequence)` pairs → exact sorted exceedance curve → two-pass weighted central moments →
risk measures. The only difference is where the mass comes from — the AGK Kronrod weight (1D) or the
VEGAS `wgt` (multi-D). The construction lives in one place — `Curve.CreateCurve`, taking weighted
pairs — and the one-dimensional, joint, and convolution paths all converge on it.
