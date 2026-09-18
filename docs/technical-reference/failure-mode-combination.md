# Failure-Mode Combination Methods

> Technical reference for the `FailureModeMethod` options on `RMC.TotalRisk.Systems.Components.SystemComponent`
> and the combination kernels in `RMC.TotalRisk.Results.Risk.SampledComponent`. Grounding: the
> *Failure Mode Combination Methods* technical note [24], whose structure this page follows, and the
> 2024 verification report [8] §8.1. Executable evidence:
> [../verification/mutually-exclusive.md](../verification/mutually-exclusive.md),
> [../verification/common-cause.md](../verification/common-cause.md),
> [../verification/competing-failures.md](../verification/competing-failures.md),
> [../verification/joint-failures.md](../verification/joint-failures.md), and the cross-method pins in
> [../verification/combination-method-consistency.md](../verification/combination-method-consistency.md).
> Companion pages: [risk-integration.md](risk-integration.md) (where the combined probabilities are
> integrated), [risk-contribution.md](risk-contribution.md) (how the failure events are attributed),
> [cascading-end-states.md](cascading-end-states.md) (the combination-unit generalization).

## 1. The multi-failure-mode problem

Dams and levees are exposed to multiple potential failure modes — overtopping erosion, internal
erosion, foundation piping, structural failure of appurtenant structures — each with its own
marginal system response function `P(Fⱼ|x)` and its own breach characteristics, timing, and
downstream consequences. Quantitative risk analysis must combine the mode-level probabilities
and consequences into a system-level estimate. The appropriate method depends on two questions
[24]: do the failure modes share the same consequences, and how do they interact physically?

## 2. Within one mode, same consequences: the composite response

Before any combination across modes, it is sometimes necessary to combine several response
functions *within* one failure mode — when the response depends on a secondary variable whose
value is uncertain, or when a mode decomposes into mutually exclusive sub-scenarios sharing one
consequence function. The composite response follows from the law of total probability: given n
mutually exclusive, collectively exhaustive scenarios with weights ωₖ (Σωₖ = 1),

```
P(F|xᵢ) = Σₖ ωₖ · P(F|xᵢ, yₖ),                                                    (1)
```

the weighted mixture. The classic use cases are seismic response conditional on reservoir stage
(condition on the variable that does **not** drive consequences, so the consequence function stays
one-dimensional), uncertain subsurface interpretations weighted by assessed likelihood, and
multiple internal-erosion initiation points — though initiation points that can be active
simultaneously are **not** mutually exclusive, and the union (Eq. 4), not the mixture, is then the
right combine. The composite applies *before* the four combination methods and only when every
sub-scenario shares one consequence function; sub-scenarios with different consequences are
separate failure modes. Implementation, estimation, and the mixture-versus-union algebra live in
[composite-functions.md](composite-functions.md); the conditional-secondary generalization (a
full copula-linked joint hazard) lives in [bivariate-hazards.md](bivariate-hazards.md).

## 3. Across modes, same consequences: the probability of union

When all modes share one consequence function there is no attribution problem — only whether
the system fails matters:

```
P(F_sys|xᵢ) = P( ∪ⱼ Fⱼ | xᵢ ).                                                    (2)
```

The exact union depends on the (generally unknown) dependency between failure-mode capacities,
but regardless of dependency it is bounded by the unimodal (Fréchet) bounds [39], [40]:

```
maxⱼ P(Fⱼ|xᵢ)  ≤  P(F_sys|xᵢ)  ≤  min( 1, Σⱼ P(Fⱼ|xᵢ) ).                          (3)
```

The lower bound is perfectly positive dependence (all modes strong or weak together); the upper
bound is perfectly negative dependence or mutual exclusivity (the capped sum). Under statistical
independence, De Morgan's rule gives the intermediate value

```
P( ∪ⱼ Fⱼ | xᵢ ) = 1 − ∏ⱼ (1 − P(Fⱼ|xᵢ)),                                          (4)
```

often adopted as a cautious stand-in for positively dependent modes. The bounds are a sensitivity
instrument: if the risk estimate does not move materially across them, the dependency assumption
is not decision-critical. Their limitation is that the range widens with the number of modes, and no
satisfactory rule for picking a single value inside them exists [30].

**Implicit dependency and the reversed bounds.** Most studies assess failure modes independently,
yet shared loading (elevated pool, high stage) creates implicit positive dependency between
capacities. Zielinski [32] showed the inter-mode dependency is not always positive: two embankment
modes both driven by pool can be *negatively* dependent on each other — the first breach drains
the reservoir and protects the second — even though both are positively dependent on the load.
When modes are negatively dependent the bounds in (3) effectively reverse: independence sits near
the *lower* end and the capped sum is the honest upper bound [31], [32]. Confusing common-cause
dependency with inter-mode dependency selects the wrong bounds; the competing and joint models
below address the interaction explicitly. The engine reproduces the reversed-bound algebra exactly —
at two modes, the perfectly negative joint model degenerates to the mutually exclusive capped sum,
a pinned identity in [../verification/combination-method-consistency.md](../verification/combination-method-consistency.md).

## 4. Different consequences: the four combination methods

When modes carry different consequences, a single union is insufficient [30] — a slow internal-erosion
breach with moderate consequences and a rapid overtopping breach with severe consequences cannot
share one consequence function without over- or under-stating risk. The marginal response functions
must be adjusted into **failure events** `P(F*ⱼ|x)` that pair with the right consequence functions. The
adjustment is the combination method, selected per component by `FailureModeMethod`.

### 4.1 Mutually exclusive

The simplest method treats the modes as mutually exclusive failure events:

```
P(F*ⱼ|xᵢ) = P(Fⱼ|xᵢ),        P(F_sys|xᵢ) = Σⱼ P(Fⱼ|xᵢ),                           (5)
E[C]_T = Σⱼ E[C]ⱼ.                                                                (6)
```

Because real modes are rarely exclusive, the sum can exceed one (Eq. 7); the engine then normalizes
so the events remain physically realizable at that hazard level:

```
P(F*ⱼ|xᵢ) = P(Fⱼ|xᵢ) / Σⱼ P(Fⱼ|xᵢ)     when Σⱼ P(Fⱼ|xᵢ) > 1,                      (8)
```

and uses the raw marginals otherwise. This is the upper unimodal bound made computable: the most
conservative allocation, right either for screening or when the analyst has *constructed* genuinely
exclusive events (e.g., separate event trees for gate A only, gate B only, and A and B together, each
with its own consequence function). No capacity dependency can be expressed.

### 4.2 Common cause adjustment (CCA)

The CCA [33], [34] reapportions the marginals so the adjusted probabilities sum to the system
probability of failure:

```
P(F*ⱼ|xᵢ) = P(Fⱼ|xᵢ) · c,     c = P(∪ⱼFⱼ|xᵢ) / Σⱼ P(Fⱼ|xᵢ),                       (9, 10)
Σⱼ P(F*ⱼ|xᵢ) = P(∪ⱼFⱼ|xᵢ),    0 ≤ c ≤ 1.                                          (11)
```

Worked example [24]: marginals 0.25 / 0.35 / 0.50 sum to 1.10; under independence the union is
1 − (0.75)(0.65)(0.50) = 0.75625, so c = 0.75625/1.10 = 0.6875 and the adjusted events are
0.171875 / 0.240625 / 0.343750 — non-overlapping and summing exactly to the union.

Its documented defects motivate the two stronger models: the normalization implies an ordering in
which the *highest*-probability mode has the *lowest* implied probability of failing first [35]; the
adjusted curves can be non-monotonic in a gradually rising hazard (the historical "freezing"
work-around is ad hoc, and **RMC-TotalRisk deliberately does not implement freezing or dominance
ordering** — analysts needing monotonic adjusted curves use the competing model); and the CCA is an
implicit competing-risks model that precludes joint failures by accident of normalization rather
than by physics. Use it for screening only.

### 4.3 Weak-link competing failure modes

The competing model [41]–[43] is the weakest-link formulation: modes proceed independently until
the **first** failure, which precludes the rest — one failure event per mode, mutually exclusive by
construction, each response function assumed monotone in the hazard. The adjusted curve for a mode
is its **cumulative incidence function** (CIF): the probability of being weak *and* weakest,

```
P(F*ⱼ|xᵢ) = ∫₀^xᵢ hⱼ(x) · S_sys(x) dx,     S_sys(x) = ∏ₘ (1 − P(Fₘ|x)),           (12, 13)
```

with hⱼ the mode's hazard rate — exactly the min-of-random-variables density decomposition of the
*Competing Risks Overview* [28]. Discretely, over hazard levels x₀ < x₁ < … the engine accumulates
the product-limit increments

```
P(F*ⱼ|xᵢ) = Σₖ [ P(Sⱼ|xₖ₋₁) − P(Sⱼ|xₖ) ] · ∏_{m≠j} P(Sₘ|x̄ₖ),                      (14)
```

survival probabilities evaluated at interval midpoints x̄ₖ. The CIF is monotone increasing (no
freezing needed), the CIFs sum to the system probability of failure at every level, dominance is
automatic (the weaker mode absorbs more incidence), and there are no joint events — so no joint
consequence rule is needed. Dependency shifts allocation, not the union: positive correlation
concentrates incidence on the dominant mode, negative correlation spreads it.

**v1.1 mechanics.** The CIFs come from Numerics `CompetingRisks.CumulativeIncidenceFunctions` on a
discretization of the sampled response curves ([../verification/competing-failures.md](../verification/competing-failures.md)
documents the measured discretization allowance); the upstream builder returns strictly increasing
tables, so the engine rebuilds each CIF non-strict before querying (a saturating fragility plateaus
at 0/1). Under the dependent options the CIF preprocessing runs multivariate-normal integration
(§5) with `CompetingRisks.PRNGSeed` driven from the component's content seed, keeping the ≥ 3-mode
randomized-lattice MVN evaluations reproducible.

### 4.4 Joint failure modes

The joint model is the most general: ordering and timing do not matter, and multiple modes can
occur in the same hazard event [45]. n modes produce **2ⁿ − 1 failure events** — every non-empty
subset. For two independent modes,

```
P(F*_A|xᵢ) = P_A − P_A·P_B,   P(F*_B|xᵢ) = P_B − P_A·P_B,   P(F*_AB|xᵢ) = P_A·P_B,  (15–17)
```

and in general each subset's exclusive probability follows from the inclusion–exclusion principle

```
P( ∪ⱼ Fⱼ | xᵢ ) = Σₖ (−1)^{k+1} Σ_{j₁<…<jₖ} P(F_{j₁} ∩ … ∩ F_{jₖ} | xᵢ),           (18)
```

with the intersections evaluated by multivariate-normal integration [44], [45] under dependency.
A single-mode event keeps its own consequence function (Eq. 19); a multi-mode event combines
consequences by the component's `JointConsequenceType`:

| Rule | `C*_AB(x)` | Appropriate when |
|---|---|---|
| `Additive` | `C_A + C_B` | inundation areas do not overlap; consequences accumulate |
| `Average` | `(C_A + C_B)/2` | areas partially overlap |
| `Maximum` | `max(C_A, C_B)` | areas largely coincide; the worst mode dominates |
| `Minimum` | `min(C_A, C_B)` | a deliberate lower bound on joint consequences |

**v1.1 mechanics.** The v1.0 product documented a hard 20-mode cap (≈ one million events). The
v1.1 engine instead enumerates the exclusive events **lazily** through the Numerics kernels
`IndependentExclusiveLazy`, `PositivelyDependentExclusiveLazy`, and `ExclusivePCM` (the
product-of-conditional-marginals path [44] for correlation-matrix dependency), preserving
subset-size/lexicographic order, the closing half-gap row, and the dual convergence predicate at
the documented 1e-4 default while reusing caller-owned row buffers — wide components trade a hard
rejection for a resource-estimate warning. Emitted event probabilities are clipped against the
sequential remaining budget before consequences, profiles, and contributions consume them — never
proportionally renormalized — and the upstream union-convergence shortcut (Σ exclusive
probabilities may fall short of the union by up to the tolerance) is surfaced honestly by the
engine's mass-balance witness rather than hidden ([risk-integration.md](risk-integration.md) §Lazy
enumeration).

## 5. Dependence: the Gaussian copula and its negative-correlation limit

All dependent options model capacity dependence through the multivariate normal (Gaussian copula):
each marginal response probability transforms to a standard-normal z-variate, and the MVN with zero
means and unit variances couples the variates — the marginals themselves need not be normal. Five
options are common to the CCA, competing, and joint models: **independent** (De Morgan / the
multiplication rule), **perfectly positive** (union = max marginal; intersection = min marginal),
**perfectly negative**, a **user-defined correlation matrix** (validated positive definite), and
**latent factors** — a factor-loading construction of the correlation matrix (§5.1).

The correlation matrix must stay positive definite. For a k-variable equicorrelation matrix the
determinant is

```
|Σ| = (1 − ρ)^{k−1} (1 + (k−1)ρ)  >  0   ⇒   ρ > −1/(k−1),                        (20–22)
```

so the attainable "perfectly negative" correlation weakens as modes are added (−0.5 at three modes,
−0.25 at five, −0.1 at eleven). The engine builds the automatic matrices with machine-epsilon
guards on the open bounds — `1 − √ε_mach` for perfectly positive and `−1/(D−1) + √ε_mach` for
perfectly negative, D the combination dimension — and writes the derived matrix back to the
correlation-matrix field (v1.0 behavior; automatic modes never serialize it). At D = 2 the
perfectly negative copula reaches genuine exclusivity, which is why the joint model then reproduces
the capped sum exactly (§3). Above two dimensions the MVN CDF is a randomized lattice rule, seeded
from the component's content seed so results stay reproducible at any thread count
([hashing-and-seeding.md](hashing-and-seeding.md)).

### 5.1 Latent factors: constructing the matrix from shared capacity drivers

Many-mode dependence is rarely known as pairwise correlations; it is known as *shared drivers* — a
soil unit under several levee segments, one design basis behind several gates, a common
construction era. The **latent-factors** dependency option authors exactly that structure: named
factors, each carrying one loading λ_if per combination unit, inducing

```
ρ_ij = Σ_f λ_if · λ_jf     (i ≠ j),      ρ_ii = 1,
```

the classic one-factor (and multi-factor) Gaussian capacity model behind the levee **length
effect** — under a common loading λ the units are conditionally independent given one standard
normal factor z, with conditional failure probabilities Φ((Φ⁻¹(p_i) − λz)/√(1−λ²)), so joint
failure probability rises far above independence exactly where shared capacity drivers say it
should. The induced matrix feeds the same combination kernels a user matrix feeds (the derived
matrix is written back to the correlation-matrix field like the automatic modes'; it never
serializes from this mode — the loadings are the persisted, hashed content).

Validation bounds keep the construction honest: loadings lie in [−1, 1]; each unit's
squared-loading sum Σ_f λ_if² may not exceed one (the remainder 1 − Σ_f λ_if² is the unit's
idiosyncratic capacity variance, which makes the induced matrix positive semi-definite by
construction); and the induced matrix must still pass the positive-definiteness gate — a unit
loaded at Σλ² = 1 has no idiosyncratic variance, and two such units on proportional loading
vectors induce an exactly singular matrix. Declared factor order is the hashed content order
(reordering factors is a deliberate compute edit even though ρ is order-invariant), factor names
and descriptions are display metadata, and a factor set configured under any other dependency mode
is inert and unpersisted (an advisory warning says so).

What the option does not yet do: derive loadings from geometry. The Vanmarcke variance-function
step — segment length and a scale of fluctuation producing the correlation structure — is the
natural companion and remains future work, as does cross-component factor sharing (dependence
between components still enters only through their hazards) and a conditional-independence
evaluation path that would exploit the factor structure directly (`Probability.UnionSingleFactor`
is the upstream seed of that path).

## 6. Comparison, selection, and portrayal

**The invariance observation.** Every dependency-modeling method (CCA, competing, joint) produces
the **same system probability of failure** for the same marginals and dependency structure; the
methods differ only in how that union is *allocated* across failure events — which is exactly what
moves risk when consequences differ. The engine pins this as a test (union invariance across
methods, Fréchet bound ordering, and the D = 2 degeneracy) in
[../verification/combination-method-consistency.md](../verification/combination-method-consistency.md).

| Feature | Mutually exclusive | CCA | Competing | Joint |
|---|---|---|---|---|
| Failure events | n | n | n | 2ⁿ − 1 |
| Joint failures | No | No (implicit) | No | Yes |
| Mutual exclusivity | Assumed | Implicit | Explicit (first failure) | No |
| Dependency options | None | All four | All four | All four |
| Monotonic adjusted curves | Yes | Not guaranteed | Yes | Not guaranteed |
| Joint consequence rule | — | — | — | `JointConsequenceType` |
| Cost | O(n) | O(n) | O(n) indep.; MVN preprocessing dep. | lazy 2ⁿ enumeration + budget clip |
| Theoretical basis | Addition rule | Normalization (weak) | Competing risks (strong) | Inclusion–exclusion (strong) |
| Appropriate use | Screening; truly exclusive constructed events | Screening only | First failure precludes others | Simultaneous failures possible |

**Selection.** Competing is generally preferred for flood-loaded dams and levees — the first breach
empties the reservoir or fills the leveed area, relieving the load on the other modes — with strong
theory, monotone curves, and O(n) independent cost. Joint fits genuinely simultaneous development:
independent gate failures, an auxiliary-spillway failure that does not lower the pool, a dam and
dike overtopped by the same flood, independent floodwall monoliths. Mutually exclusive serves
screening and deliberately constructed exclusive events; the CCA serves screening only.

**Pitfalls** [24]: (1) summing marginal risk estimates double-counts joint/competing contributions;
(2) the CCA can mask actionable modes by redistributing probability away from high-probability
modes; (3) non-monotonic CCA curves are physically unrealistic for gradually rising hazards — and
freezing is deliberately not implemented; (4) common-cause dependency is not inter-mode dependency,
and confusing them reverses the bounds [32]; (5) marginal failure *modes* and adjusted failure
*events* are different objects — conflating them corrupts portrayal.

**Portrayal.** Marginal risk by failure mode identifies actionable modes and risk-reduction
alternatives; system risk from the competing or joint model determines actionability and portfolio
priority; risk by individual failure *event* (the 2ⁿ − 1 subsets) invites misreading and is not
routinely portrayed. The engine's attribution machinery
([risk-contribution.md](risk-contribution.md)) computes mode-level contributions generally across
all four methods for exactly this reason.

## 7. The cascade generalization: combination units

With cascading response end states, the combination methods operate unchanged — but over
**combination units** rather than raw modes: exclusive end-state groups plus standalone failure
states, the `EndStateGroupLayout` derivation. Within a unit the exclusive states' masses add;
across units the ambient `FailureModeMethod` combines the unit masses, the MVN dimension is the
unit count, and every non-cascading model reduces byte-for-byte to the plain per-mode arithmetic.
Competing additionally requires every failure state's weight to be monotone (all-Fail signatures —
an else-chain failure state is a validation error under competing). The full construction is
[cascading-end-states.md](cascading-end-states.md).

## 8. Verification anchors

| Method | Family | Report anchor [8] |
|---|---|---|
| Mutually exclusive | [mutually-exclusive.md](../verification/mutually-exclusive.md) | §8.1 scenario matrix |
| CCA | [common-cause.md](../verification/common-cause.md) | Tables 55–58 |
| Competing | [competing-failures.md](../verification/competing-failures.md) | Tables 59–60 |
| Joint | [joint-failures.md](../verification/joint-failures.md) | Tables 61–76 |
| Cross-method invariants | [combination-method-consistency.md](../verification/combination-method-consistency.md) | — (engine-only property pins) |
| Latent factors | [latent-factor.md](../verification/latent-factor.md) | — (greenfield: exact factor integrals + Monte Carlo oracles) |
| Multi-component systems | [system-risk-matrix.md](../verification/system-risk-matrix.md) | Tables 77–103 |

Every family preserves the legacy fixed seeds and documents its k·SE tolerance derivation per
[../verification.md](../verification.md).
