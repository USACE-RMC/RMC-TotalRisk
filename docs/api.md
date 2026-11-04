# RMC-TotalRisk API and MCP Server

`RMC.TotalRisk.Api` hosts two surfaces in one ASP.NET Core (net10.0) process: a REST API and a
[Model Context Protocol](https://modelcontextprotocol.io/) server for agentic callers. Both sit
over the same singleton services, share one camelCase JSON wire contract, and expose the same
capability: a **stateless round-trip compute** — a complete risk-analysis definition in, the mean
risk results out, nothing stored between calls.

```bash
dotnet run --project src/RMC.TotalRisk.Api
```

Development URLs: `http://localhost:5220` (the MCP endpoint requires plain HTTP locally) and
`https://localhost:7220`. The OpenAPI document is served at `GET /openapi/v1.json` in Development,
or anywhere when `EnableSwagger: true` is configured.

## Concepts

- **Stateless round trips.** Every call is self-contained and replayable. There is no resource
  store, no ids, no session; the caller owns all input and output memory. (A store-backed
  create/run/get resource lifecycle is deferred scope — see the end of this page.)
- **One response envelope.** Every REST response (success and failure) carries the same base
  fields: `success`, `errorMessage`, `validationIssues`, `validationErrors`,
  `validationWarnings`, `computationTimeMs`, `timestamp`, `nonFiniteFindings`. Failures return
  the same typed DTO as successes, so clients parse one shape.
- **Structured validation issues.** Every issue is `{ code, severity, message, objectPath }`.
  Model-layer issues carry the engine's stable codes (`TRV…`); API request-shape issues use the
  `API_` family (`API_TABLE_ORDER`, `API_MIXTURE_WEIGHTS_SUM`, `API_MEAN_ONLY_REQUIRED`, …).
  Messages echo the offending values and `objectPath` points into the request
  (`components[0].hazard.exceedanceProbabilities`), so payloads can be repaired iteratively.
  Warnings ride successful responses too.
- **Probability conventions.** Hazard tables pair ANNUAL EXCEEDANCE probabilities — strictly
  descending, the frequent event first — with strictly ascending hazard levels. Nothing is ever
  silently reordered; a violation is a structured 400.
- **Deterministic, provenance-stamped results.** Sampling seeds derive from canonical content
  hashes plus the request's `prngSeed`, so the same request content produces bit-identical
  results at any thread count, and renaming functions or components never changes numbers. The
  response's `provenance` block carries the content hashes, the seed, and the schema/assembly
  versions; `effectiveOptions` echoes every setting the run actually used, so a caller can
  replay a run exactly by sending the echo back.
- **Mean-only compute (current contract).** `options.estimateMeanRiskOnly` must be `true` or
  omitted; `false` is refused with `API_MEAN_ONLY_REQUIRED`. Full-uncertainty ensembles arrive
  in a later contract increment. Mean-only runs on deterministic inputs complete in well under a
  second.
- **Adjusted vs. unadjusted failure-mode risk.** Each failure mode's `curves` carry its raw
  UNADJUSTED marginal response risk — what an investment decision compares across modes. Its
  `adjustedCurves` carry the mode's share after the component's combination method has resolved
  the modes against one another, so adjusted values sum to the component total. Both answer
  different questions; the API computes both by default
  (`outputAdjustedFailureModeCurves` defaults `true` here — the engine default is `false`).
- **Risk contributions.** Every failure mode and component carries a
  `contribution { failureProbability, failureMean, excessMean }` attribution. Sum identities:
  across a component's failure modes, `failureProbability` values sum to the component fail
  stream's `massBalance`, `failureMean` values to the component fail mean, and `excessMean`
  values to the component excess mean. Contributions are always computed, independent of the
  `riskMeasures` flags.
- **Hazard-to-response transforms.** A failure mode's `transforms` chain converts the hazard
  signal in order ahead of the response — stage to overtopping depth for an overtopping
  fragility, stage to discharge for spillway erosion — so the response is keyed to the LAST
  transform's output axis. `consequenceHazardPosition` selects the axis the mode's consequences
  read: 0 is the raw component hazard (the omitted default, so consequences stay stage-keyed
  unless the request says otherwise — a documented divergence from the engine's
  last-response-input default), k is the signal after the k-th transform. Three deterministic
  kinds: `tabularTransform` (a conversion table, e.g. a rating curve), `linearTransform`
  (`y = alpha + beta·x`), and `powerTransform` (`y = alpha·(x − xi)^beta`, optionally
  inverted — the weir form). Output labels (`transformedHazard`, `transformedHazardUnit`) are
  required; linear and power transforms clamp evaluation to a required `[minimum, maximum]`
  range that must cover the hazard table. These transform FUNCTIONS are distinct from the
  `hazardTransform`/`probabilityTransform` INTERPOLATION-space fields on tabular functions.
- **Day/night exposure mixtures.** A `compositeMixture` consequence's branch weights are
  aleatory exposure probabilities: the engine enumerates the weighted branches (a mixture, not an
  average), which preserves the consequence variance an average would destroy. Under the
  `jointFailures` method the branches multiply across failure paths — a component whose paths all
  carry two-branch day/night mixtures warns above 64 branch combinations and errors above 1,024
  (roughly six or ten two-branch paths); the other combination methods do not cross-multiply.
- **NaN policy.** A `null` scalar measure means "not computed" (a disabled `riskMeasures` flag or
  an undefined value). NaN appears on the wire only as the JSON literal `"NaN"`; clients must
  enable named floating-point literals to parse it. ±Infinity is never served — the response
  auditor converts it into a 500 whose `nonFiniteFindings` name the offending paths.
- **Host limits.** `appsettings.json` section `Api`: `MaxConcurrentRuns` (global engine-run
  throttle, default 4), `MaxComponents` (default 64), `MaxOrdinatesPerTable` (default 10,000).

## REST endpoints

| Method and path | Purpose | Returns |
|---|---|---|
| `POST /api/risk-analyses/compute` | Map, validate, and compute a complete definition synchronously | `ComputeRiskAnalysisResponse` (200; 400 with issues; 499 on client cancel; 500) |
| `POST /api/risk-analyses/validate` | The run-free twin: the identical body, validated only | `ValidateRiskAnalysisResponse` (always 200 with `isValid` + issues) |
| `GET /api/risk-analyses/example` | A filled, valid, computable request template | `ComputeRiskAnalysisRequest` |
| `GET /api/metadata` | Enum values, function kinds, defaults, limits, conventions | `MetadataResponse` |
| `GET /api/info` | Service identity, `apiContractVersion`, feature areas | `ApiInfoDto` |
| `GET /health`, `GET /health/detailed` | Liveness and version | text / `HealthCheckDto` |
| `GET /openapi/v1.json` | The OpenAPI document (Development or `EnableSwagger`) | OpenAPI 3 JSON |

## The compute request

```text
ComputeRiskAnalysisRequest
├─ name, description
├─ specifiedConsequence, consequenceUnit          ← the primary consequence type (required)
├─ additionalConsequenceTypes[]?                  ← { specifiedConsequence, consequenceUnit, consequenceThreshold? }
├─ components[] (min 1)
│  ├─ name (required)
│  ├─ hazard: tabularHazard                       ← { exceedanceProbabilities[] ↓, hazardValues[] ↑,
│  │                                                  specifiedHazard, hazardUnit, interpolation transforms, extrapolation }
│  ├─ failureModes[]                              ← may be empty (pure background-risk component)
│  │  ├─ name (required; labels the results row)
│  │  ├─ transforms[]?                            ← tabularTransform | linearTransform | powerTransform
│  │  │                                              (ordered hazard-domain chain, e.g. stage → depth;
│  │  │                                               transformedHazard/transformedHazardUnit required)
│  │  ├─ response: tabularResponse                ← { hazardValues[] ↑, responseProbabilities[] ∈ [0,1] }
│  │  │                                              keyed to the LAST transform's output axis
│  │  ├─ consequences[] (one per declared type)   ← tabularConsequence | compositeMixture
│  │  │  └─ compositeMixture.branches[]           ← { weight ∈ [0,1] (Σ = 1), function }
│  │  └─ consequenceHazardPosition?               ← 0 = the raw hazard (default) … k = after the k-th transform
│  ├─ nonFailConsequences[]                       ← the single response-free path (0 or one per declared type)
│  ├─ failureModeMethod, failureModeDependency, failureModeCorrelationMatrix?,
│  │  jointConsequences, hazardThreshold
├─ options?                                       ← the full engine-settings mirror; null fields keep defaults
└─ resultOptions?                                 ← { includeCurves = true }
```

Label inheritance keeps minimal payloads clean: a blank `specifiedHazard`/`hazardUnit` on a
transform inherits the incoming signal's pair (the component hazard's for the first chain entry,
the previous transform's output for later ones); a blank pair on a response inherits the last
transform's output pair (the component hazard's when the mode has no transforms); and a blank
pair on a consequence inherits the pair at the consequence-bound position. Blank consequence
type labels inherit the declared type at the position the function fills. A blank primary
consequence function name inherits the failure mode's name, which is what labels the mode's
results row.

Supplying **any** integration knob (`maxEvaluations`, `maxDepth`, `tolerance`,
`ensembleTolerance`, `ensembleMinDepth`, `warmupEvaluations`, `warmupCycles`,
`finalEvaluations`) switches the engine off its automatic component-count-scaled integration
defaults so the supplied value survives to the run; otherwise the automatic defaults apply.

## The compute response

```text
ComputeRiskAnalysisResponse : envelope
├─ provenance                                     ← schema/assembly versions, content hashes, prngSeed
├─ effectiveOptions                               ← every setting the run actually used (replayable)
├─ computationWarnings[], computationDiagnostics[]
└─ results
   ├─ name ("Mean"), consequenceLabels[], consequenceUnits[]
   ├─ curves { excess, background, total, fail, nonFail }        ← system scope, primary type
   │  └─ each: stats { totalProbability, massBalance, mean, conditionalMean?, standardDeviation,
   │            skewness?, kurtosis?, valueAtRisk?, conditionalValueAtRisk?,
   │            consequenceThresholdProbability?, hazardThresholdProbability?, isExhaustive },
   │           lec?, hazardFrequency?, hazardVsConditionalMean?, profiles?
   ├─ additionalCurves[]?                                        ← one curve set per additional type
   ├─ functionEvaluations, standardError
   └─ components[]
      ├─ name, curves, additionalCurves?, minHazard, maxHazard
      ├─ systemContribution?, additionalSystemContributions[]?
      └─ failureModes[]                                          ← one row per failure path
         ├─ name, pathLabel?
         ├─ curves                                               ← UNADJUSTED marginal
         ├─ adjustedCurves?                                      ← combination-adjusted share
         ├─ additionalCurves[]?, additionalAdjustedCurves[]?
         └─ contribution?, additionalContributions[]?
```

The non-fail path is not a failure-mode row; its risk rides the component's background and
non-fail streams. The `fail` stream's `totalProbability` is the annualized failure probability;
the `profiles` block (present under the `riskProfiles` measure) carries the cumulative failure
probability and expected consequence by hazard plus the system response probability curve.

## MCP server

The MCP endpoint runs the streamable HTTP transport in stateless mode at `/mcp`. Register
`http://localhost:5220/mcp` with any MCP client that supports the streamable HTTP transport.

| Tool | Purpose |
|---|---|
| `run_risk_analysis` | Compute a complete definition and return the full results body |
| `validate_risk_analysis` | The run-free verdict with structured, repairable issues |
| `get_metadata` | Enums, function kinds, defaults, limits, conventions — the pre-flight call |
| `get_example_request` | The filled screening-model template, valid and computable as returned |

The recommended agent chain: `get_metadata` → `get_example_request` → edit →
`validate_risk_analysis` until `isValid` → `run_risk_analysis`. Tool failures surface as
JSON-RPC errors whose messages carry the structured issue codes and remediation text.

## Deferred features

Store-backed resources (create/run/get lifecycle with ids), full-uncertainty ensemble results
(percentile bands, convergence diagnostics, tolerable-risk confidence), the wider input-function
catalog (uncertain tabular functions, uncertain and composite transform functions, trailing
response-to-consequence transform chains, multi-stage response chains, parametric and composite
hazards/responses, event and fault trees, bivariate hazards), authentication, and
containerization. The wire contract is append-only: these arrive as new fields and new
function-kind discriminators, never as breaking changes; `apiContractVersion` (served by
`GET /api/info` and stamped into `provenance`) tracks the contract — deterministic
hazard-to-response transform chains and `consequenceHazardPosition` are its `1.1.0` additive
increment.
