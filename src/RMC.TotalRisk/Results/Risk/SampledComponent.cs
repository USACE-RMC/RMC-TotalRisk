using System;
using System.Collections.Generic;
using Numerics;
using Numerics.Data;
using Numerics.Data.Statistics;
using Numerics.Distributions;
using Numerics.Functions;
using Numerics.Sampling;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.Systems.Components;
using RMC.TotalRisk.Results.Support;

namespace RMC.TotalRisk.Results.Risk
{
    /// <summary>
    /// One system component frozen for one Monte Carlo realization: the sampled hazard
    /// distribution, the sampled failure modes, the competing-risks pre-processing, and the
    /// failure-mode combination kernel the risk integrand evaluates.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Ported from v1.0 <c>SampledComponent</c> onto the v1.1 sampler contract, with the four
    /// failure-mode combination methods preserved verbatim in structure: joint failures through
    /// the inclusion–exclusion pathway probabilities
    /// (<c>Probability.IndependentExclusiveLazy</c> / <c>PositivelyDependentExclusiveLazy</c> /
    /// <c>ExclusivePCMLazy</c>, generated without a dense indicator matrix), weak-link
    /// competing failures through cumulative incidence functions pre-processed over 200
    /// stratified hazard levels, the common-cause adjustment, and the mutually-exclusive
    /// normalization (with its probability-above-one warning). The profile-axis
    /// remap: when the component selects a profile hazard element, the sampled
    /// profile transform chain remaps every recorded hazard level — component and mode scope —
    /// onto the profile axis for this realization; unset, recorded hazard levels are the raw
    /// driving hazard, bit-identical to the engine without a profile selection.
    /// </para>
    /// <para>
    /// The exposure-branch generalization: consequences are weighted exposure branches, so recorded
    /// pathway entries enumerate branch combinations. Joint pathways take the cross product over
    /// the failing modes' branch sets (weights multiply; the combined consequence follows the
    /// joint-consequence rule) and cross the component's own non-failure branches for exact
    /// excess pairs. The per-mode methods (competing, common cause, mutually exclusive) record
    /// per-branch failure entries and use each mode's paired excess (exact in the scalar; the
    /// entry lists collapse the paired non-failure spread to its mean — the documented
    /// <see cref="ComponentRiskOutput"/> interim).
    /// </para>
    /// <para>
    /// The multi-consequence axis: the probability structure — response probabilities, pathway
    /// decomposition, combination adjustments, and the total failure probability — is computed
    /// once per evaluation and shared by every consequence type; the consequence kernels then
    /// run per type over that type's branch entries (types never cross), recording into the
    /// realization's primary curves (type 0) or <c>AdditionalCurves[k − 1]</c>, with per-type
    /// consequence extents. Secondary types are evaluated only when recording or when the caller
    /// supplies a per-type output sink, so probe and warm-up evaluations pay the single-type
    /// cost.
    /// </para>
    /// </remarks>
    public class SampledComponent
    {

        #region Construction

        /// <summary>
        /// Samples a system component for one realization, deriving the end-state group layout
        /// from the projected modes (the engine path supplies the frozen layout directly).
        /// </summary>
        /// <param name="component">The component to sample (supplies the combination configuration and caches).</param>
        /// <param name="projectedModes">The component's projected failure modes, captured once per run.</param>
        /// <param name="nonFailureMode">The projected non-failure mode, or null when the component has none.</param>
        /// <param name="realizationIndex">The realization index, or −1 for the mean functions.</param>
        /// <exception cref="ArgumentNullException">Thrown when the component or mode list is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the component has no hazard function, or when sampling by realization
        /// index before the samplers have been set up.
        /// </exception>
        public SampledComponent(SystemComponent component, IReadOnlyList<FailureMode> projectedModes,
            FailureMode? nonFailureMode, int realizationIndex = -1)
            : this(component, projectedModes, nonFailureMode, EndStateGroupLayout.Build(projectedModes), realizationIndex)
        {
        }

        /// <summary>
        /// Samples a system component for one realization against a frozen end-state group
        /// layout (docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §7.9).
        /// </summary>
        /// <param name="component">The component to sample (supplies the combination configuration and caches).</param>
        /// <param name="projectedModes">The component's projected failure modes, captured once per run.</param>
        /// <param name="nonFailureMode">The projected non-failure mode, or null when the component has none.</param>
        /// <param name="layout">The end-state group layout over the projected modes, frozen with them.</param>
        /// <param name="realizationIndex">The realization index, or −1 for the mean functions.</param>
        /// <param name="conditionalOverride">
        /// An optional pre-built conditional snapshot replacing the hazard's own — the
        /// discretization-error diagnostic passes a mean snapshot at a halved bin count; null
        /// (the default, and every engine path) samples the hazard's configured snapshot.
        /// </param>
        /// <exception cref="ArgumentNullException">Thrown when the component, mode list, or layout is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the component has no hazard function, or when sampling by realization
        /// index before the samplers have been set up.
        /// </exception>
        internal SampledComponent(SystemComponent component, IReadOnlyList<FailureMode> projectedModes,
            FailureMode? nonFailureMode, EndStateGroupLayout layout, int realizationIndex = -1,
            SampledBivariateHazard? conditionalOverride = null)
        {
            if (component == null) throw new ArgumentNullException(nameof(component));
            if (projectedModes == null) throw new ArgumentNullException(nameof(projectedModes));
            _layout = layout ?? throw new ArgumentNullException(nameof(layout));
            var hazardFunction = component.HazardFunction
                ?? throw new InvalidOperationException("The system component has no hazard function. Call Validate() and correct the reported errors before sampling.");

            Name = component.Name;
            _failureModeMethod = component.FailureModeMethod;
            _failureModeDependency = component.FailureModeDependency;
            _jointConsequences = component.JointConsequences;
            _correlationMatrix = component.CorrelationMatrix;


            Hazard = realizationIndex < 0 ? hazardFunction.SampleFunction() : hazardFunction.SampleFunction(realizationIndex);

            // The conditional secondary dimension: a bivariate hazard freezes its per-realization
            // snapshot (the sampled Y marginal, a cloned copula, and the shared trapezoid
            // vectors) and the caller-owned bin buffers here — the conditional-bin loop inside
            // ComputeRisk then evaluates already-sampled functions only. Null for every
            // univariate component, whose construction and evaluation are untouched.
            if (hazardFunction is IBivariateHazardFunction bivariateHazard)
            {
                _conditionalHazard = conditionalOverride
                    ?? (realizationIndex < 0 ? bivariateHazard.SampleBivariate() : bivariateHazard.SampleBivariate(realizationIndex));
                int nodeCount = _conditionalHazard.ConditionalNodeCount;
                _binY = new double[nodeCount];
                _binW = new double[nodeCount];

                // The conditional-quadrature selector: an override snapshot IS the
                // discretization-error instrument, whose Richardson ladder measures the fixed
                // conditional-trapezoid grid — it stays on that grid structurally. Every other
                // construction (the engine paths and direct sampling) evaluates the secondary
                // axis adaptively.
                _conditionalFixedGrid = conditionalOverride != null;
            }

            // The profile-axis remap: sample the component's resolved profile
            // transform chain for this realization. The chain functions are the same seeded
            // instances the failure-mode chains draw from, so a shared transform samples the
            // identical curve here and in the modes — dedup coherence for free. Null when the
            // primary hazard is the profile axis (the default), making the remap a single null
            // check per evaluation.
            var profileChain = component.ProfileTransformFunctions;
            if (profileChain != null)
            {
                _profileTransforms = new IUnivariateFunction[profileChain.Length];
                for (int i = 0; i < profileChain.Length; i++)
                {
                    _profileTransforms[i] = realizationIndex < 0 ? profileChain[i].SampleFunction() : profileChain[i].SampleFunction(realizationIndex);
                }
            }

            // Resolve each end state's projected pairing partner (§7.9.4): the
            // flipped-final sibling terminal when wired, else the background non-failure mode
            // (v1.0 parity — every trivial layout resolves to the background). State indexes
            // count the non-background modes in projection order — the layout's index space.
            var stateProjected = new List<FailureMode>(projectedModes.Count);
            for (int i = 0; i < projectedModes.Count; i++)
            {
                if (!projectedModes[i].IsNonFailureMode) stateProjected.Add(projectedModes[i]);
            }

            _failureModes = new List<SampledFailureMode>(projectedModes.Count);
            _failureModesView = _failureModes.AsReadOnly();
            _fModes = new List<SampledFailureMode>(projectedModes.Count);
            int consequenceTypeCount = 1;
            int stateIndex = 0;
            for (int i = 0; i < projectedModes.Count; i++)
            {
                FailureMode? pairing = null;
                bool claimed = false;
                if (!projectedModes[i].IsNonFailureMode)
                {
                    int partner = _layout.PairingPartnerState[stateIndex];
                    pairing = partner >= 0 ? stateProjected[partner] : nonFailureMode;
                    claimed = !_layout.IsFailureState[stateIndex];
                    stateIndex++;
                }
                var sampled = new SampledFailureMode(projectedModes[i], pairing, realizationIndex);
                if (claimed) sampled.SuppressModeRecording = true;
                _failureModes.Add(sampled);
                consequenceTypeCount = Math.Max(consequenceTypeCount, sampled.ConsequenceTypeCount);
                if (sampled.IsNonFailureMode)
                {
                    _nfMode = sampled;
                }
                else
                {
                    _fModes.Add(sampled);
                }
            }
            ConsequenceTypeCount = consequenceTypeCount;

            // The sampled pairing partners, resolved after every mode exists (a sibling partner
            // is itself one of the sampled states).
            _pairedSampled = new SampledFailureMode?[_fModes.Count];
            for (int j = 0; j < _fModes.Count; j++)
            {
                int partner = _layout.PairingPartnerState[j];
                _pairedSampled[j] = partner >= 0 ? _fModes[partner] : _nfMode;
            }

            // Compute-workspace scratch: mode and type counts are fixed for the life
            // of the sampled component, so every per-evaluation buffer is sized exactly once
            // here and reused across the realization's thousands of integrand evaluations — a
            // sampled component is realization-owned, never shared across threads. Recording
            // sites still allocate fresh lists where a RiskPoint adopts them.
            _scratchModeOutputs = new ComponentRiskOutput[_fModes.Count];
            _scratchModeTypeOutputs = new ComponentRiskOutput[_fModes.Count][];
            for (int j = 0; j < _fModes.Count; j++)
            {
                _scratchModeTypeOutputs[j] = new ComponentRiskOutput[consequenceTypeCount];
            }
            _scratchTypeColumns = new ComponentRiskOutput[consequenceTypeCount][];
            _scratchTypeOutputs = new ComponentRiskOutput[consequenceTypeCount];
            _scratchExcessProbabilities = new List<double>[consequenceTypeCount];
            _scratchExcessValues = new List<double>[consequenceTypeCount];
            for (int k = 0; k < consequenceTypeCount; k++)
            {
                _scratchTypeColumns[k] = new ComponentRiskOutput[_fModes.Count];
                _scratchTypeOutputs[k] = new ComponentRiskOutput();
                _scratchExcessProbabilities[k] = new List<double>();
                _scratchExcessValues[k] = new List<double>();
            }
            _scratchResponseProbabilities = new List<double>(_fModes.Count);
            _scratchAdjustedProbabilities = new double[_fModes.Count];
            _scratchParticipating = new List<int>(_fModes.Count);
            _scratchBranchPick = new int[_fModes.Count];
            _scratchContributionProbability = new double[_fModes.Count];
            _scratchContributionFailure = new double[_fModes.Count];
            _scratchContributionExcess = new double[_fModes.Count];
            _scratchTupleValues = new double[_fModes.Count];
            _scratchUnitProbabilities = new List<double>(_layout.CombinationUnitCount);
            _scratchPathwayProbabilities = new List<double>();
            _scratchPathwayIndicators = new List<int[]>();
            _scratchClaimedConditional = _layout.ClaimedStateCount > 0 ? new double[_fModes.Count] : null;
            _scratchPickedStates = new int[_fModes.Count];

            // With claimed states the complement pair baseline is the conditional mixture
            // (remainder-scaled background + q-scaled claimed branches — §7.9.5); size the
            // per-type mixture buffers once.
            if (_layout.ClaimedStateCount > 0)
            {
                _scratchPairWeights = new double[consequenceTypeCount][];
                _scratchPairValues = new double[consequenceTypeCount][];
                for (int k = 0; k < consequenceTypeCount; k++)
                {
                    int size = _nfMode?.BranchCount(k) ?? 0;
                    for (int j = 0; j < _fModes.Count; j++)
                    {
                        if (!_layout.IsFailureState[j]) size += _fModes[j].BranchCount(k);
                    }
                    _scratchPairWeights[k] = new double[size];
                    _scratchPairValues[k] = new double[size];
                }
            }

            // The bivariate per-evaluation accumulators: the per-type cross-bin sums and the
            // per-type, per-mode contribution accumulation the conditional-bin loop folds into
            // (allocated once here, reused per evaluation — never on univariate components).
            if (_conditionalHazard != null)
            {
                _binExpectedFailure = new double[consequenceTypeCount];
                _binExpectedExcess = new double[consequenceTypeCount];
                _binNonFailure = new double[consequenceTypeCount];
                _binMinN = new double[consequenceTypeCount];
                _binMaxN = new double[consequenceTypeCount];
                _binContributionProbability = new double[consequenceTypeCount][];
                _binContributionFailure = new double[consequenceTypeCount][];
                _binContributionExcess = new double[consequenceTypeCount][];
                for (int k = 0; k < consequenceTypeCount; k++)
                {
                    _binContributionProbability[k] = new double[_fModes.Count];
                    _binContributionFailure[k] = new double[_fModes.Count];
                    _binContributionExcess[k] = new double[_fModes.Count];
                }

                // The engine-side competing-risks guard (the validation error's loud runtime
                // mirror): the multi-unit cumulative-incidence pre-processing marginals are
                // primary-axis curves, so every failure state's response probability must be a
                // pure primary function. Single-unit competing needs no pre-processing and
                // stays legal for any binding.
                if (_failureModeMethod == FailureModeMethod.CompetingFailures && _layout.CombinationUnitCount > 1)
                {
                    for (int j = 0; j < _fModes.Count; j++)
                    {
                        if (_layout.IsFailureState[j] && !_fModes[j].IsSrpBinInvariant)
                        {
                            throw new InvalidOperationException($"System component '{Name}' combines competing failure modes over a bivariate hazard, but failure mode '{_fModes[j].Name}' has a secondary-dependent response probability; the cumulative-incidence pre-processing cannot represent it. Call Validate() and correct the reported errors before sampling.");
                        }
                    }
                }
            }

            // Weak-link competing failures: pre-process the cumulative incidence functions over
            // 200 stratified hazard levels (v1.0 constants). A single mode short-circuits to its
            // own response probability, so the pre-processing is skipped then.
            if (_failureModeMethod == FailureModeMethod.CompetingFailures && _layout.CombinationUnitCount > 1)
            {
                double minHazard = Hazard.InverseCDF(ProbabilityFloor);
                double maxHazard = Hazard.InverseCDF(1d - ProbabilityFloor);
                HazardBins = Stratify.XValues(new StratificationOptions(minHazard, maxHazard, 200), false);

                // A deterministic component samples identical curves every realization, so the run
                // may already hold this incidence data — skipping a rectangle integral per unit
                // per hazard level under a dependent configuration.
                if (component.TryGetCompetingIncidence(out var sharedIncidence))
                {
                    _cumulativeIncidenceFunctions = BuildIncidenceFunctions(sharedIncidence!);
                    return;
                }

                // The competing marginals are the combination units' failure masses — a unit's
                // mass sums its exclusive members' polarity-product weights (validation admits
                // only all-Fail signatures under competing, so every mass is monotone and the
                // ascending curve construction holds — §7.9.6). A singleton unit's sum
                // reproduces the plain per-mode value bit-identically.
                int unitCount = _layout.CombinationUnitCount;
                var distributions = new EmpiricalDistribution[unitCount];
                var responseValues = new List<double>[unitCount];
                var hazardValues = new List<double>[unitCount];
                for (int j = 0; j < unitCount; j++)
                {
                    hazardValues[j] = new List<double>(HazardBins.Count + 1);
                    responseValues[j] = new List<double>(HazardBins.Count + 1);
                }

                double level = HazardBins[0].LowerBound;
                for (int j = 0; j < unitCount; j++)
                {
                    hazardValues[j].Add(level);
                    responseValues[j].Add(UnitMassAt(j, level));
                }
                for (int i = 0; i < HazardBins.Count; i++)
                {
                    level = HazardBins[i].UpperBound;
                    for (int j = 0; j < unitCount; j++)
                    {
                        hazardValues[j].Add(level);
                        responseValues[j].Add(UnitMassAt(j, level));
                    }
                }
                for (int j = 0; j < unitCount; j++)
                {
                    // Non-strict ascending probabilities: a fragility legitimately plateaus at 0
                    // below its onset and at 1 past saturation, and the strict two-list
                    // constructor would reject those ties.
                    var responseCurve = new OrderedPairedData(hazardValues[j], responseValues[j],
                        true, SortOrder.Ascending, false, SortOrder.Ascending);
                    distributions[j] = new EmpiricalDistribution(responseCurve);
                }

                var competingRisks = new CompetingRisks(distributions)
                {
                    Dependency = _failureModeDependency switch
                    {
                        DependencyType.Independent => Probability.DependencyType.Independent,
                        DependencyType.PerfectlyPositive => Probability.DependencyType.PerfectlyPositive,
                        _ => Probability.DependencyType.CorrelationMatrix,
                    },

                    // The dependent branches draw from a randomized lattice rule, so the incidence
                    // curves reproduce only when its generator is seeded from model content.
                    PRNGSeed = component.CompetingRiskSeed,
                };
                if (_correlationMatrix != null)
                {
                    competingRisks.CorrelationMatrix = _correlationMatrix;
                }

                var rawIncidenceFunctions = competingRisks.CumulativeIncidenceFunctions(HazardBins);
                var incidenceData = new (double[] Hazards, double[] Probabilities)[rawIncidenceFunctions.Count];
                for (int j = 0; j < rawIncidenceFunctions.Count; j++)
                {
                    incidenceData[j] = (rawIncidenceFunctions[j].XValues.ToArray(), rawIncidenceFunctions[j].ProbabilityValues.ToArray());
                }
                _cumulativeIncidenceFunctions = BuildIncidenceFunctions(incidenceData);

                component.PublishCompetingIncidence(incidenceData);
            }
        }

        /// <summary>
        /// Wraps cumulative incidence data in queryable distributions, one per combination unit.
        /// </summary>
        /// <param name="incidenceData">The hazard and incidence-probability arrays per unit.</param>
        /// <returns>The incidence distributions, in unit order.</returns>
        /// <remarks>
        /// The arrays may be the run's shared copy and are only read here — the
        /// <see cref="OrderedPairedData"/> two-list constructor copies each pair rather than
        /// retaining the inputs, so the distributions it produces are per-realization. The
        /// non-strict Y ordering is required: a cumulative incidence function plateaus wherever a
        /// unit contributes no hazard, which the strict form would reject.
        /// </remarks>
        private static List<EmpiricalDistribution> BuildIncidenceFunctions((double[] Hazards, double[] Probabilities)[] incidenceData)
        {
            var functions = new List<EmpiricalDistribution>(incidenceData.Length);
            for (int j = 0; j < incidenceData.Length; j++)
            {
                var incidenceCurve = new OrderedPairedData(incidenceData[j].Hazards, incidenceData[j].Probabilities,
                    true, SortOrder.Ascending, false, SortOrder.Ascending);
                functions.Add(new EmpiricalDistribution(incidenceCurve));
            }
            return functions;
        }

        #endregion

        #region Members

        /// <summary>
        /// The lower/upper probability clamp shared with the legacy engine.
        /// </summary>
        private const double ProbabilityFloor = 1e-16;

        /// <summary>
        /// All sampled modes, in projected order (the non-failure mode included).
        /// </summary>
        private readonly List<SampledFailureMode> _failureModes;

        /// <summary>The immutable public view over the sampled failure modes.</summary>
        private readonly IReadOnlyList<SampledFailureMode> _failureModesView;

        /// <summary>
        /// The sampled failure modes only, in projected order.
        /// </summary>
        private readonly List<SampledFailureMode> _fModes;

        /// <summary>
        /// The frozen end-state group layout over the states: combination
        /// units, failure/claimed classification, and pairing partners. Trivial for every
        /// non-cascading model, where the kernels reduce to the plain per-mode arithmetic.
        /// </summary>
        private readonly EndStateGroupLayout _layout;

        /// <summary>
        /// Each state's sampled excess pairing partner (§7.9.4): the flipped-final sibling
        /// state, or the background non-failure mode (null when neither exists).
        /// </summary>
        private readonly SampledFailureMode?[] _pairedSampled;

        /// <summary>
        /// The sampled non-failure mode; null when the component has none.
        /// </summary>
        private readonly SampledFailureMode? _nfMode;

        /// <summary>
        /// The captured combination configuration (v1.0 members, captured so the compute loop
        /// never touches the live component).
        /// </summary>
        private readonly FailureModeMethod _failureModeMethod;

        /// <summary>
        /// The captured failure-mode dependency.
        /// </summary>
        private readonly DependencyType _failureModeDependency;

        /// <summary>
        /// The captured joint-consequence combination rule.
        /// </summary>
        private readonly JointConsequenceType _jointConsequences;

        /// <summary>
        /// The captured correlation matrix (dependency-mode content).
        /// </summary>
        private readonly double[,]? _correlationMatrix;


        /// <summary>
        /// The pre-processed cumulative incidence functions (competing failures with two or more
        /// modes); null otherwise.
        /// </summary>
        private readonly List<EmpiricalDistribution>? _cumulativeIncidenceFunctions;

        /// <summary>
        /// The frozen per-realization bivariate snapshot driving the conditional-bin loop; null
        /// for every univariate component — the single branch selecting the unchanged univariate
        /// <see cref="ComputeRisk"/> body.
        /// </summary>
        private readonly SampledBivariateHazard? _conditionalHazard;

        /// <summary>
        /// The reusable conditional secondary-hazard node buffer (length bins + 1); null on
        /// univariate components.
        /// </summary>
        private readonly double[]? _binY;

        /// <summary>
        /// The reusable trapezoid weight buffer, index-aligned with <see cref="_binY"/> and
        /// summing exactly to one; null on univariate components.
        /// </summary>
        private readonly double[]? _binW;

        /// <summary>
        /// True when this snapshot evaluates the secondary axis on the fixed
        /// conditional-trapezoid grid — the discretization-error instrument's construction
        /// (an override snapshot); every other bivariate construction adapts.
        /// </summary>
        private readonly bool _conditionalFixedGrid;

        /// <summary>
        /// The relative tolerance of the per-slice adaptive conditional quadrature; the engine
        /// configures the ensemble/mean discipline split, and the default is the mean
        /// discipline.
        /// </summary>
        private double _conditionalRelativeTolerance = 1e-8;

        /// <summary>
        /// The minimum refinement depth of the per-slice adaptive conditional quadrature.
        /// </summary>
        private int _conditionalMinDepth = 2;

        /// <summary>
        /// The maximum refinement depth of the per-slice adaptive conditional quadrature.
        /// </summary>
        private int _conditionalMaxDepth = 100;

        /// <summary>
        /// Whether the conditional refinement surrogate carries the consequence term (risk
        /// mode); false refines on the failure probability alone (reliability mode — a
        /// consequence-free model's consequence surrogate is identically zero).
        /// </summary>
        private bool _conditionalIncludeConsequences = true;

        /// <summary>
        /// The reused per-slice adaptive conditional integrator (one instance per component,
        /// serially reused — its recorder capture pools are instance state); built lazily on
        /// the first adaptive evaluation.
        /// </summary>
        private Numerics.Mathematics.Integration.AdaptiveGaussKronrod? _conditionalIntegrator;

        /// <summary>
        /// The adopted per-slice conditional mesh, sized by the refinement-budget bound; built
        /// lazily with the integrator.
        /// </summary>
        private ConditionalQuadratureMesh? _conditionalMesh;

        /// <summary>
        /// The reusable conditional secondary-hazard buffer of the adaptive paths (the mesh
        /// replay and the column replay), grown on demand.
        /// </summary>
        private double[] _columnY = Array.Empty<double>();

        /// <summary>
        /// The primary non-exceedance probability of the surrogate evaluation in flight.
        /// </summary>
        private double _surrogateU = double.NaN;

        /// <summary>
        /// The primary hazard level matching <see cref="_surrogateU"/> (the tensor rule
        /// evaluates same-u node runs, so the inverse-CDF caches on the last u).
        /// </summary>
        private double _surrogateX;

        /// <summary>
        /// The throwaway flag sink of the refinement surrogate: pass-one evaluations visit
        /// conditional nodes the committed pass may never adopt, so their advisory flags must
        /// never latch onto the realization.
        /// </summary>
        private readonly RiskComputeFlags _surrogateFlags = new RiskComputeFlags();

        /// <summary>
        /// The consequence-density normalization of the balanced conditional surrogate, from
        /// the deterministic probe sweep; zero drops the term.
        /// </summary>
        private double _surrogateConsequenceScale;

        /// <summary>
        /// The failure-probability normalization of the balanced conditional surrogate, from
        /// the deterministic probe sweep; zero drops the term.
        /// </summary>
        private double _surrogateProbabilityScale;

        /// <summary>
        /// Whether the surrogate scales have been probed for this snapshot.
        /// </summary>
        private bool _surrogateScalesReady;

        /// <summary>
        /// The per-slice conditional sweep's evaluation budget: 21 × bins by default (the
        /// per-evaluation seats' bound), raised by the additive interior to its fair-share
        /// strip budget for the endpoint columns. Zero until first read.
        /// </summary>
        private int _conditionalSweepBudget;

        /// <summary>
        /// The reusable per-type cross-bin Σ w_j · (adjusted probability × failure consequence)
        /// accumulators of one bivariate evaluation; null on univariate components.
        /// </summary>
        private readonly double[]? _binExpectedFailure;

        /// <summary>
        /// The reusable per-type cross-bin expected-excess accumulators.
        /// </summary>
        private readonly double[]? _binExpectedExcess;

        /// <summary>
        /// The reusable per-type cross-bin Σ w_j · (bin non-failure consequence) accumulators —
        /// the marginalized background mean.
        /// </summary>
        private readonly double[]? _binNonFailure;

        /// <summary>
        /// The reusable per-type running minimum consequence extents of one bivariate
        /// evaluation.
        /// </summary>
        private readonly double[]? _binMinN;

        /// <summary>
        /// The reusable per-type running maximum consequence extents of one bivariate
        /// evaluation.
        /// </summary>
        private readonly double[]? _binMaxN;

        /// <summary>
        /// The reusable per-type, per-mode attributed-probability accumulators of one recording
        /// bivariate evaluation (contribution samples accumulate across bins and submit once per
        /// evaluation and type); null on univariate components.
        /// </summary>
        private readonly double[][]? _binContributionProbability;

        /// <summary>
        /// The reusable per-type, per-mode attributed failure-value accumulators.
        /// </summary>
        private readonly double[][]? _binContributionFailure;

        /// <summary>
        /// The reusable per-type, per-mode attributed excess-value accumulators.
        /// </summary>
        private readonly double[][]? _binContributionExcess;

        /// <summary>
        /// The reusable exclusive-pathway probability buffer for the independent joint
        /// decomposition, allocated on first use.
        /// </summary>
        private readonly List<double> _scratchPathwayProbabilities;

        /// <summary>
        /// The reusable exclusive-pathway indicator buffer, parallel to
        /// <see cref="_scratchPathwayProbabilities"/>.
        /// </summary>
        private readonly List<int[]> _scratchPathwayIndicators;

        /// <summary>
        /// The sampled profile transform chain mapping the driving hazard onto the selected
        /// profile axis for this realization; null when the primary hazard is the axis.
        /// </summary>
        private readonly IUnivariateFunction[]? _profileTransforms;

        /// <summary>
        /// The reusable per-mode primary-output view (reused compute workspace — every
        /// scratch buffer below is realization-owned and reused per evaluation).
        /// </summary>
        private readonly ComponentRiskOutput[] _scratchModeOutputs;

        /// <summary>
        /// The reusable per-mode, per-type output sinks the modes fill.
        /// </summary>
        private readonly ComponentRiskOutput[][] _scratchModeTypeOutputs;

        /// <summary>
        /// The reusable per-type column views over the per-mode outputs.
        /// </summary>
        private readonly ComponentRiskOutput[][] _scratchTypeColumns;

        /// <summary>
        /// The reusable per-type component outputs — handed out by <see cref="ComputeRisk"/> and
        /// valid until the next evaluation on this component.
        /// </summary>
        private readonly ComponentRiskOutput[] _scratchTypeOutputs;

        /// <summary>
        /// The reusable per-type excess entry-probability accumulators for non-recording
        /// evaluations (recording evaluations allocate fresh lists — a risk point adopts them).
        /// </summary>
        private readonly List<double>[] _scratchExcessProbabilities;

        /// <summary>
        /// The reusable per-type excess entry-value accumulators for non-recording evaluations.
        /// </summary>
        private readonly List<double>[] _scratchExcessValues;

        /// <summary>
        /// The reusable per-mode response-probability list.
        /// </summary>
        private readonly List<double> _scratchResponseProbabilities;

        /// <summary>
        /// The reusable per-mode combination-adjusted probabilities.
        /// </summary>
        private readonly double[] _scratchAdjustedProbabilities;

        /// <summary>
        /// The reusable participating-mode index list of the joint kernel.
        /// </summary>
        private readonly List<int> _scratchParticipating;

        /// <summary>
        /// The reusable branch odometer of the joint kernel.
        /// </summary>
        private readonly int[] _scratchBranchPick;

        /// <summary>
        /// The reusable per-mode attributed-probability sums of one recording evaluation (the
        /// % contribution diagnostic).
        /// </summary>
        private readonly double[] _scratchContributionProbability;

        /// <summary>
        /// The reusable per-mode attributed probability × failure-consequence sums.
        /// </summary>
        private readonly double[] _scratchContributionFailure;

        /// <summary>
        /// The reusable per-mode attributed probability × excess-consequence sums.
        /// </summary>
        private readonly double[] _scratchContributionExcess;

        /// <summary>
        /// The reusable per-participant branch consequence values of one joint tuple (the
        /// consequence-proportional split's weights).
        /// </summary>
        private readonly double[] _scratchTupleValues;

        /// <summary>
        /// The reusable combination-unit failure-mass list the method kernels operate over
        /// (§7.9): entry u is the exact sum of unit u's exclusive member weights.
        /// </summary>
        private readonly List<double> _scratchUnitProbabilities;

        /// <summary>
        /// The reusable per-state conditional complement shares of the claimed non-failure
        /// states (§7.9.5, q = w / (1 − P_g)); null when the layout claims nothing.
        /// </summary>
        private readonly double[]? _scratchClaimedConditional;

        /// <summary>
        /// The reusable per-participant picked-state indexes of one joint tuple (the
        /// contribution attribution's landing states).
        /// </summary>
        private readonly int[] _scratchPickedStates;

        /// <summary>
        /// The reusable per-type complement-mixture pair weights (§7.9.5 — remainder-scaled
        /// background plus q-scaled claimed branches); null when the layout claims nothing.
        /// </summary>
        private readonly double[][]? _scratchPairWeights;

        /// <summary>
        /// The reusable per-type complement-mixture pair values, parallel to the weights.
        /// </summary>
        private readonly double[][]? _scratchPairValues;

        /// <summary>
        /// The component's display name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// The sampled hazard distribution for this realization.
        /// </summary>
        public IUnivariateDistribution Hazard { get; }

        /// <summary>
        /// The stratified hazard levels behind the competing-risks pre-processing; null when not
        /// pre-processed.
        /// </summary>
        public List<StratificationBin>? HazardBins { get; }

        /// <summary>
        /// The sampled modes, in projected order (the non-failure mode included).
        /// </summary>
        public IReadOnlyList<SampledFailureMode> FailureModes => _failureModesView;

        /// <summary>
        /// The number of sampled failure modes (the non-failure mode excluded).
        /// </summary>
        public int FailureModeCount => _fModes.Count;

        /// <summary>
        /// The number of consequence types the component carries (the declared-axis length; the
        /// analysis validation gate guarantees every path agrees).
        /// </summary>
        public int ConsequenceTypeCount { get; }

        #endregion

        #region Methods

        /// <summary>
        /// Computes the component's risk at one hazard evaluation point: the per-mode responses,
        /// the failure-mode combination, the branch-enumerated pathway entries per consequence
        /// type, and the recorded risk points.
        /// </summary>
        /// <param name="probability">The hazard non-exceedance probability at the evaluation point (the recorded probability coordinate; the VEGAS path passes its weight).</param>
        /// <param name="hazardLevel">The hazard level.</param>
        /// <param name="flags">The realization's computational-warning flags.</param>
        /// <param name="realization">The component's realization sink (with one failure-mode realization per failure mode, in projected order).</param>
        /// <param name="recordOutput">True to record risk-point entries on the realization curves.</param>
        /// <param name="typeOutputs">
        /// The optional per-type output sink, length at least <see cref="ConsequenceTypeCount"/>
        /// (entry k receives type k's output; entry 0 is the returned primary). Secondary types
        /// are computed only when recording or when this sink is supplied.
        /// </param>
        /// <param name="hazardNonExceedance">
        /// The hazard non-exceedance probability u of the evaluation's X slice, used only by a
        /// bivariate component to condition its secondary discretization: the 1D objective and
        /// the failure-probability probe pass their true u, the VEGAS integrand passes its
        /// clamped local probability (its <paramref name="probability"/> argument is the
        /// recorded weight, not u), and NaN (the default) derives
        /// <c>Clamp(Hazard.CDF(hazardLevel), 1e-16, 1 − 1e-16)</c>. Univariate components never
        /// read it.
        /// </param>
        /// <returns>
        /// The component's primary-type risk output at the evaluation point. The returned output
        /// (and every sink entry) is workspace-backed scratch, valid until the next evaluation
        /// on this component — consume or copy it before evaluating again (reused compute
        /// workspace; the engine's call sites consume within the evaluation).
        /// </returns>
        /// <exception cref="ArgumentNullException">Thrown when the flags or realization sink is null.</exception>
        public ComponentRiskOutput ComputeRisk(double probability, double hazardLevel, RiskComputeFlags flags,
            ComponentRealization realization, bool recordOutput = false, ComponentRiskOutput[]? typeOutputs = null,
            double hazardNonExceedance = double.NaN)
        {
            if (flags == null) throw new ArgumentNullException(nameof(flags));
            if (realization == null) throw new ArgumentNullException(nameof(realization));

            // The single bivariate dispatch: a conditional hazard selects the conditional-bin
            // body; every univariate component falls through to the unchanged body below.
            if (_conditionalHazard != null)
            {
                return ComputeRiskBivariate(probability, hazardLevel, flags, realization, recordOutput, typeOutputs, hazardNonExceedance);
            }

            // The profile-axis remap: recorded hazard levels are the raw driving
            // hazard unless a profile transform chain is selected, in which case every recorded
            // point — component and mode scope alike — carries the composed profile signal for
            // this realization. A single null check when unset keeps the default bit-identical.
            double recordedHazard = hazardLevel;
            var profile = _profileTransforms;
            if (profile != null)
            {
                for (int i = 0; i < profile.Length; i++)
                {
                    recordedHazard = profile[i].Function(recordedHazard);
                }
            }

            // The driving hazard's exceedance probability at this evaluation — the system
            // response profile's X coordinate. Computed from the sampled hazard so
            // it is correct on both integration paths (the 1D path's probability argument is
            // the non-exceedance coordinate, but the VEGAS path passes its weight), and only
            // when the coordinate will be recorded.
            double hazardExceedance = recordOutput
                ? Tools.Clamp(1d - Hazard.CDF(hazardLevel), 0d, 1d)
                : double.NaN;

            // Secondary types ride along only when their results are consumed (recording, or the
            // caller's per-type sink); probes and warm-up evaluations stay single-type.
            bool wantSecondary = ConsequenceTypeCount > 1 && (recordOutput || typeOutputs != null);
            int componentTypes = wantSecondary ? ConsequenceTypeCount : 1;

            // Per-mode risk at this hazard level — every computed consequence type in one pass
            // per mode, into the reused per-mode sinks.
            var modeOutputs = _scratchModeOutputs;
            var modeTypeOutputs = _scratchModeTypeOutputs;
            var responseProbabilities = _scratchResponseProbabilities;
            responseProbabilities.Clear();
            for (int j = 0; j < _fModes.Count; j++)
            {
                if (wantSecondary)
                {
                    _fModes[j].ComputeRisk(probability, hazardLevel, _pairedSampled[j], flags, realization.FailureModes[j], recordOutput, modeTypeOutputs[j], recordedHazard, hazardExceedance);
                    modeOutputs[j] = modeTypeOutputs[j][0];
                }
                else
                {
                    modeOutputs[j] = _fModes[j].ComputeRisk(probability, hazardLevel, _pairedSampled[j], flags, realization.FailureModes[j], recordOutput, null, recordedHazard, hazardExceedance);
                }
                responseProbabilities.Add(Tools.Clamp(modeOutputs[j].ProbabilityOfFailure, 0d, 1d));
            }

            // The combination structure is type-independent — compute it once and share it with
            // every consequence kernel. The methods operate over the combination
            // units: a unit's failure mass is the exact sum of its exclusive members'
            // weights, and the adjusted mass distributes back to the members conditionally. A
            // trivial layout (every non-cascading model) reduces every step to the plain
            // per-mode arithmetic bit-identically (singleton sums add zero; the conditional
            // ratio is exactly one).
            List<double>? pathwayProbabilities = null;
            List<int[]>? pathwayIndicators = null;
            double[]? adjustedProbabilities = null;
            double totalProbabilityOfFailure = 0d;
            int unitCount = _layout.CombinationUnitCount;
            var unitProbabilities = _scratchUnitProbabilities;
            unitProbabilities.Clear();
            if (_fModes.Count > 0 && unitCount > 0)
            {
                for (int u = 0; u < unitCount; u++)
                {
                    var members = _layout.CombinationUnitStates[u];
                    double mass = 0d;
                    for (int m = 0; m < members.Length; m++)
                    {
                        mass += responseProbabilities[members[m]];
                    }
                    unitProbabilities.Add(Tools.Clamp(mass, 0d, 1d));
                }

                if (_failureModeMethod == FailureModeMethod.JointFailures)
                {
                    ComputePathwayDecomposition(unitProbabilities, out pathwayProbabilities, out pathwayIndicators);
                    for (int j = 0; j < pathwayProbabilities.Count; j++)
                    {
                        totalProbabilityOfFailure += pathwayProbabilities[j];
                    }
                }
                else
                {
                    adjustedProbabilities = _scratchAdjustedProbabilities;
                    double commonCauseFactor = _failureModeMethod == FailureModeMethod.CommonCauseFailures
                        ? CommonCauseFactor(unitProbabilities)
                        : 0d;
                    double normalization = 1d;
                    if (_failureModeMethod == FailureModeMethod.MutuallyExclusive)
                    {
                        normalization = Probability.MutuallyExclusiveAdjustment(unitProbabilities);
                        if (normalization < 1d) flags.HasProbabilityGreaterThanOne = true;
                    }
                    for (int u = 0; u < unitCount; u++)
                    {
                        double adjusted;
                        if (_failureModeMethod == FailureModeMethod.CompetingFailures)
                        {
                            adjusted = unitCount == 1 ? unitProbabilities[0] : _cumulativeIncidenceFunctions![u].CDF(hazardLevel);
                        }
                        else if (_failureModeMethod == FailureModeMethod.CommonCauseFailures)
                        {
                            adjusted = unitProbabilities[u] * commonCauseFactor;
                        }
                        else
                        {
                            adjusted = unitProbabilities[u] * normalization;
                        }
                        adjusted = Tools.Clamp(adjusted, 0d, 1d);
                        totalProbabilityOfFailure += adjusted;

                        // Distribute the adjusted unit mass to its member states conditionally
                        // (w / P_g; exactly one for a singleton member).
                        var members = _layout.CombinationUnitStates[u];
                        double unitMass = unitProbabilities[u];
                        for (int m = 0; m < members.Length; m++)
                        {
                            int state = members[m];
                            adjustedProbabilities[state] = unitMass > 0d
                                ? Tools.Clamp(adjusted * (responseProbabilities[state] / unitMass), 0d, 1d)
                                : 0d;
                        }
                    }
                }
            }
            totalProbabilityOfFailure = Tools.Clamp(totalProbabilityOfFailure, 0d, 1d);
            bool hasClaimed = _layout.ClaimedStateCount > 0;
            double probabilityOfNonFailure = Tools.Clamp(1d - totalProbabilityOfFailure, 0d, 1d);

            // The claimed non-failure states' conditional complement shares (§7.9.5):
            // q = w / (1 − P_g) against the state's own cascade unit, exact under independent
            // groups and the documented convention otherwise. Type-independent — the weights
            // are the polarity products.
            double claimedShareTotal = 0d;
            if (hasClaimed)
            {
                var claimedConditional = _scratchClaimedConditional!;
                for (int j = 0; j < _fModes.Count; j++)
                {
                    if (_layout.IsFailureState[j])
                    {
                        claimedConditional[j] = 0d;
                        continue;
                    }
                    int owningUnit = _layout.ClaimedStateUnit[j];
                    double divisor = owningUnit >= 0 ? Tools.Clamp(1d - unitProbabilities[owningUnit], 0d, 1d) : 1d;
                    double share = divisor > 0d ? responseProbabilities[j] / divisor : 0d;
                    claimedConditional[j] = Tools.Clamp(share, 0d, 1d);
                    claimedShareTotal += claimedConditional[j];
                }
                if (claimedShareTotal > 1d)
                {
                    // Duplicate-claim wiring can over-claim the complement (the Q2 ruling keeps
                    // it legal); the shares renormalize and the mass-balance witness reports
                    // the double count honestly.
                    for (int j = 0; j < _fModes.Count; j++)
                    {
                        claimedConditional[j] /= claimedShareTotal;
                    }
                    claimedShareTotal = 1d;
                }
            }

            // The consequence kernels, per type: the non-failure branches from the non-failure
            // mode's OWN sample at this type (its own coupling draw — v1.0 behavior; the
            // per-mode excess uses each mode's PAIRED sample inside
            // SampledFailureMode.ComputeRisk), then the pathway/branch entries, the recorded
            // points, and the per-type consequence extents.
            ComponentRiskOutput primary = null!;
            for (int k = 0; k < componentTypes; k++)
            {
                var typeOutput = _scratchTypeOutputs[k];
                typeOutput.Reset();
                if (k == 0) primary = typeOutput;

                double[] nonFailWeights = _unitWeight;
                double[] nonFailValues = _zeroValue;
                double nonFailureConsequences = 0d;
                if (_nfMode != null)
                {
                    _nfMode.EvaluateConsequenceBranches(hazardLevel, k, flags, out nonFailWeights, out nonFailValues);
                    for (int j = 0; j < nonFailWeights.Length; j++)
                    {
                        nonFailureConsequences += nonFailWeights[j] * nonFailValues[j];
                    }
                }

                var failEntryValues = typeOutput.FailureConsequences;
                var failEntryProbabilities = typeOutput.ResponseProbabilities;

                // Compute into realization-owned scratch. Multi-entry recording copies the final
                // lists once into its adopted risk point; the dominant one-entry path records
                // inline and allocates no entry lists.
                List<double> excessEntryProbabilities = _scratchExcessProbabilities[k];
                List<double> excessEntryValues = _scratchExcessValues[k];
                excessEntryProbabilities.Clear();
                excessEntryValues.Clear();

                double expectedFailureConsequences = 0d;
                double expectedExcessConsequences = 0d;
                double minN = k == 0 ? realization.MinN : realization.AdditionalMinN[k - 1];
                double maxN = k == 0 ? realization.MaxN : realization.AdditionalMaxN[k - 1];

                // With claimed non-failure states the complement pair baseline becomes the
                // conditional mixture (§7.9.5): the remainder share of the background branches
                // plus each claimed state's q-scaled branches. The mixture drives the
                // non-failure scalar, the joint excess pairs, and the complement recording —
                // one distribution, three consumers. Without claimed states the baseline stays
                // the raw background branches, bit-identical to the claim-free path.
                double[] pairWeights = nonFailWeights;
                double[] pairValues = nonFailValues;
                double remainderShare = 1d;
                if (hasClaimed)
                {
                    remainderShare = Tools.Clamp(1d - claimedShareTotal, 0d, 1d);
                    pairWeights = _scratchPairWeights![k];
                    pairValues = _scratchPairValues![k];
                    int cursor = 0;
                    if (_nfMode != null)
                    {
                        for (int b = 0; b < nonFailWeights.Length; b++)
                        {
                            pairWeights[cursor] = remainderShare * nonFailWeights[b];
                            pairValues[cursor++] = nonFailValues[b];
                        }
                    }
                    for (int j = 0; j < _fModes.Count; j++)
                    {
                        if (_layout.IsFailureState[j]) continue;
                        double share = _scratchClaimedConditional![j];
                        _fModes[j].EvaluateConsequenceBranches(hazardLevel, k, flags, out double[] claimedWeights, out double[] claimedValues);
                        for (int b = 0; b < claimedWeights.Length; b++)
                        {
                            pairWeights[cursor] = share * claimedWeights[b];
                            pairValues[cursor++] = claimedValues[b];
                            minN = Math.Min(minN, claimedValues[b]);
                            maxN = Math.Max(maxN, claimedValues[b]);
                        }
                    }
                    nonFailureConsequences = 0d;
                    for (int b = 0; b < cursor; b++)
                    {
                        nonFailureConsequences += pairWeights[b] * pairValues[b];
                    }
                }

                bool accumulateContribution = recordOutput && _fModes.Count > 0;
                if (accumulateContribution)
                {
                    Array.Clear(_scratchContributionProbability, 0, _fModes.Count);
                    Array.Clear(_scratchContributionFailure, 0, _fModes.Count);
                    Array.Clear(_scratchContributionExcess, 0, _fModes.Count);
                }

                if (_fModes.Count > 0 && unitCount > 0)
                {
                    var typeModeOutputs = k == 0 ? modeOutputs : FillTypeColumn(k);
                    if (_failureModeMethod == FailureModeMethod.JointFailures)
                    {
                        ComputeJointPathwayEntries(unitProbabilities, typeModeOutputs, pathwayProbabilities!, pathwayIndicators!,
                            pairWeights, pairValues,
                            failEntryProbabilities, failEntryValues, excessEntryProbabilities, excessEntryValues,
                            ref expectedFailureConsequences, ref expectedExcessConsequences, ref minN, ref maxN,
                            accumulateContribution ? _scratchContributionProbability : null,
                            accumulateContribution ? _scratchContributionFailure : null,
                            accumulateContribution ? _scratchContributionExcess : null);

                        // Under the joint method a mode's adjusted share is its pathway
                        // attribution, which the contribution split already computed.
                        if (recordOutput && RecordAdjustedModeCurves && accumulateContribution)
                        {
                            for (int j = 0; j < _fModes.Count; j++)
                            {
                                if (!_layout.IsFailureState[j]) continue;
                                RecordAdjustedModeEntries(realization, j, k, typeModeOutputs[j],
                                    responseProbabilities[j], _scratchContributionProbability[j],
                                    recordedHazard, probability, hazardExceedance);
                            }
                        }
                    }
                    else
                    {
                        for (int j = 0; j < _fModes.Count; j++)
                        {
                            // Claimed non-failure states never contribute failure entries —
                            // they ride the complement decomposition below (§7.9.2).
                            if (!_layout.IsFailureState[j]) continue;

                            AppendModeEntries(typeModeOutputs[j], responseProbabilities[j], adjustedProbabilities![j],
                                failEntryProbabilities, failEntryValues, excessEntryProbabilities, excessEntryValues, ref minN, ref maxN);

                            if (recordOutput && RecordAdjustedModeCurves)
                            {
                                RecordAdjustedModeEntries(realization, j, k, typeModeOutputs[j],
                                    responseProbabilities[j], adjustedProbabilities[j], recordedHazard, probability, hazardExceedance);
                            }

                            expectedFailureConsequences += adjustedProbabilities[j] * typeModeOutputs[j].MeanFailureConsequences;
                            expectedExcessConsequences += adjustedProbabilities[j] * typeModeOutputs[j].MeanExcessConsequences;

                            // The per-mode methods ARE the exclusive decomposition (one state
                            // per event — within a state group the conditional distribution
                            // keeps the members exclusive): the state's attributed contribution
                            // is its adjusted probability and the adjusted-scaled means — the
                            // same products the expected-value chains above consume
                            // (the % contribution diagnostic).
                            if (accumulateContribution)
                            {
                                _scratchContributionProbability[j] = adjustedProbabilities[j];
                                _scratchContributionFailure[j] = adjustedProbabilities[j] * typeModeOutputs[j].MeanFailureConsequences;
                                _scratchContributionExcess[j] = adjustedProbabilities[j] * typeModeOutputs[j].MeanExcessConsequences;
                            }
                        }
                    }
                }

                if (accumulateContribution)
                {
                    for (int j = 0; j < _fModes.Count; j++)
                    {
                        realization.FailureModes[j].AddContributionSample(k, probability,
                            _scratchContributionProbability[j], _scratchContributionFailure[j], _scratchContributionExcess[j]);
                    }
                }

                double effectiveNonFailure = nonFailureConsequences;
                double meanFailureConsequences = totalProbabilityOfFailure == 0d ? 0d : expectedFailureConsequences / totalProbabilityOfFailure;
                double meanExcessConsequences = totalProbabilityOfFailure == 0d ? 0d : expectedExcessConsequences / totalProbabilityOfFailure;

                // The excess output list against the mean non-failure consequence (the documented
                // ComponentRiskOutput interim; the recorded curves carry the exact pairs).
                for (int e = 0; e < failEntryValues.Count; e++)
                {
                    typeOutput.ExcessConsequences.Add(Math.Max(0d, failEntryValues[e] - effectiveNonFailure));
                }

                // Record the component-level risk points on this type's curves.
                if (recordOutput)
                {
                    var target = k == 0 ? realization.Curves : realization.AdditionalCurves[k - 1];
                    bool inlineRecord = _fModes.Count > 0 && !hasClaimed
                        && failEntryProbabilities.Count == 1 && excessEntryProbabilities.Count == 1
                        && pairWeights.Length == 1;
                    if (inlineRecord)
                    {
                        double failProbability = failEntryProbabilities[0];
                        double failValue = failEntryValues[0];
                        double nonFailProbability = Tools.Clamp(probabilityOfNonFailure * pairWeights[0], 0d, 1d);
                        double nonFailValue = pairValues[0];
                        target.Fail.AddRiskPoint(recordedHazard, probability, failProbability, failValue, hazardExceedance);
                        target.Excess.AddRiskPoint(recordedHazard, probability,
                            excessEntryProbabilities[0], excessEntryValues[0]);
                        target.Background.AddRiskPoint(recordedHazard, probability,
                            Tools.Clamp(pairWeights[0], 0d, 1d), nonFailValue);
                        target.NonFail.AddRiskPoint(recordedHazard, probability, nonFailProbability, nonFailValue);
                        target.Total.AddTwoEntryRiskPoint(recordedHazard, probability,
                            failProbability, failValue, nonFailProbability, nonFailValue);
                    }
                    else
                    {
                        if (_fModes.Count > 0)
                        {
                            target.Fail.AddRiskPoint(recordedHazard, probability,
                                new List<double>(failEntryProbabilities), new List<double>(failEntryValues), hazardExceedance);
                            target.Excess.AddRiskPoint(recordedHazard, probability,
                                new List<double>(excessEntryProbabilities), new List<double>(excessEntryValues));
                        }

                        var totalProbabilities = new List<double>(failEntryProbabilities.Count + nonFailWeights.Length);
                        var totalValues = new List<double>(failEntryValues.Count + nonFailValues.Length);
                        totalProbabilities.AddRange(failEntryProbabilities);
                        totalValues.AddRange(failEntryValues);

                        // The complement recording consumes the pair baseline directly: the raw
                        // background branches without claimed states (bit-identical claim-free
                        // entries), the conditional mixture with them (§7.9.5).
                        var backgroundProbabilities = new List<double>(pairWeights.Length);
                        var backgroundValues = new List<double>(pairValues.Length);
                        var nonFailProbabilities = new List<double>(pairWeights.Length);
                        var nonFailPointValues = new List<double>(pairValues.Length);
                        for (int j = 0; j < pairWeights.Length; j++)
                        {
                            backgroundProbabilities.Add(Tools.Clamp(pairWeights[j], 0d, 1d));
                            backgroundValues.Add(pairValues[j]);
                            nonFailProbabilities.Add(Tools.Clamp(probabilityOfNonFailure * pairWeights[j], 0d, 1d));
                            nonFailPointValues.Add(pairValues[j]);
                            totalProbabilities.Add(Tools.Clamp(probabilityOfNonFailure * pairWeights[j], 0d, 1d));
                            totalValues.Add(pairValues[j]);
                        }

                        // The claimed non-failure states' own mode-scope curves record their
                        // conditional complement entries into the NonFail stream (§7.9.2 — a
                        // Non-Fail-final state is not a failure, so Fail/Excess stay empty).
                        if (hasClaimed)
                        {
                            for (int j = 0; j < _fModes.Count; j++)
                            {
                                if (_layout.IsFailureState[j]) continue;
                                double share = _scratchClaimedConditional![j];
                                _fModes[j].EvaluateConsequenceBranches(hazardLevel, k, flags, out double[] claimedWeights, out double[] claimedValues);
                                var claimedProbabilities = new List<double>(claimedWeights.Length);
                                var claimedPointValues = new List<double>(claimedWeights.Length);
                                for (int b = 0; b < claimedWeights.Length; b++)
                                {
                                    claimedProbabilities.Add(Tools.Clamp(probabilityOfNonFailure * share * claimedWeights[b], 0d, 1d));
                                    claimedPointValues.Add(claimedValues[b]);
                                }
                                var claimedTarget = k == 0 ? realization.FailureModes[j].Curves : realization.FailureModes[j].AdditionalCurves[k - 1];
                                claimedTarget.NonFail.AddRiskPoint(recordedHazard, probability, claimedProbabilities, claimedPointValues);
                            }
                        }
                        target.Background.AddRiskPoint(recordedHazard, probability, backgroundProbabilities, backgroundValues);
                        target.NonFail.AddRiskPoint(recordedHazard, probability, nonFailProbabilities, nonFailPointValues);
                        target.Total.AddRiskPoint(recordedHazard, probability, totalProbabilities, totalValues);
                    }
                }

                // Per-type consequence extents for the percentile post-processing grids (v1.0
                // behavior on the primary).
                minN = Math.Min(minN, effectiveNonFailure);
                maxN = Math.Max(maxN, Math.Max(meanFailureConsequences, effectiveNonFailure));
                if (k == 0)
                {
                    realization.MinN = minN;
                    realization.MaxN = maxN;
                }
                else
                {
                    realization.AdditionalMinN[k - 1] = minN;
                    realization.AdditionalMaxN[k - 1] = maxN;
                }

                typeOutput.ProbabilityOfFailure = totalProbabilityOfFailure;
                typeOutput.ProbabilityOfNonFailure = probabilityOfNonFailure;
                typeOutput.NonFailureConsequences = effectiveNonFailure;
                typeOutput.MeanFailureConsequences = meanFailureConsequences;
                typeOutput.MeanExcessConsequences = meanExcessConsequences;
                if (typeOutputs != null) typeOutputs[k] = typeOutput;
            }

            // Hazard extents are type-independent.
            realization.MinH = Math.Min(realization.MinH, recordedHazard);
            realization.MaxH = Math.Max(realization.MaxH, recordedHazard);

            return primary;
        }

        /// <summary>
        /// Computes a bivariate component's risk at one primary hazard evaluation point by
        /// integrating the conditional secondary dimension adaptively: pass one runs the
        /// per-slice Gauss–Kronrod refinement of a failure-density surrogate over
        /// conditional-probability space and adopts the accepted composite rule (deterministic,
        /// draw-free, budget-bounded at 21 × bins surrogate evaluations); pass two replays the
        /// staged evaluation over the adopted column. The discretization-error instrument's
        /// override snapshots stay on the fixed conditional-trapezoid grid — the Richardson
        /// ladder measures exactly that grid.
        /// </summary>
        /// <param name="probability">The recorded probability coordinate (the VEGAS path passes its weight).</param>
        /// <param name="hazardLevel">The primary hazard level.</param>
        /// <param name="flags">The realization's computational-warning flags.</param>
        /// <param name="realization">The component's realization sink.</param>
        /// <param name="recordOutput">True to record risk-point entries on the realization curves.</param>
        /// <param name="typeOutputs">The optional per-type output sink (entry 0 is the returned primary).</param>
        /// <param name="hazardNonExceedance">The slice's non-exceedance probability u, or NaN to derive it from the sampled primary marginal.</param>
        /// <returns>The component's primary-type risk output, marginalized over the conditional column.</returns>
        private ComponentRiskOutput ComputeRiskBivariate(double probability, double hazardLevel, RiskComputeFlags flags,
            ComponentRealization realization, bool recordOutput, ComponentRiskOutput[]? typeOutputs,
            double hazardNonExceedance)
        {
            var conditional = _conditionalHazard!;
            double u = double.IsNaN(hazardNonExceedance)
                ? Tools.Clamp(Hazard.CDF(hazardLevel), ProbabilityFloor, 1d - ProbabilityFloor)
                : Tools.Clamp(hazardNonExceedance, ProbabilityFloor, 1d - ProbabilityFloor);

            if (_conditionalFixedGrid)
            {
                conditional.FillConditionalBins(u, _binY!, _binW!);
                return ComputeRiskBivariateCore(probability, hazardLevel, flags, realization, recordOutput, typeOutputs,
                    conditional.ConditionalNodeCount, _binY!, _binW!);
            }

            int meshCount = BuildAdaptiveConditionalMesh(u, hazardLevel);
            return ComputeRiskBivariateCore(probability, hazardLevel, flags, realization, recordOutput, typeOutputs,
                meshCount, _columnY, _conditionalMesh!.Weights);
        }

        /// <summary>
        /// The staged bivariate evaluation body over one conditional column — whatever supplies
        /// the node set and its exhaustive unit-sum weights (the fixed trapezoid vectors, the
        /// adopted adaptive mesh, or a merged two-dimensional column): every failure mode
        /// evaluates at the same (x, y_j), the combination kernels run per node on the per-node
        /// response probabilities (combine-then-marginalize — marginalizing first would drop
        /// the modes' shared-Y covariance), and the weighted sums fold into ONE risk point per
        /// stream per evaluation with the entry lists enumerating (node × pathway × branch).
        /// Contribution samples accumulate across nodes and submit once per evaluation and
        /// type, so the recorded-mass and contribution-ledger accounting hold unchanged.
        /// </summary>
        /// <param name="probability">The recorded probability coordinate.</param>
        /// <param name="hazardLevel">The primary hazard level.</param>
        /// <param name="flags">The realization's computational-warning flags.</param>
        /// <param name="realization">The component's realization sink.</param>
        /// <param name="recordOutput">True to record risk-point entries on the realization curves.</param>
        /// <param name="typeOutputs">The optional per-type output sink (entry 0 is the returned primary).</param>
        /// <param name="nodeCount">The column's node count.</param>
        /// <param name="yNodes">The conditional secondary hazard values, one per node.</param>
        /// <param name="weights">The node weights, index-aligned and summing exactly to one.</param>
        /// <returns>The component's primary-type risk output, marginalized over the column.</returns>
        private ComponentRiskOutput ComputeRiskBivariateCore(double probability, double hazardLevel, RiskComputeFlags flags,
            ComponentRealization realization, bool recordOutput, ComponentRiskOutput[]? typeOutputs,
            int nodeCount, double[] yNodes, double[] weights)
        {

            // The profile-axis remap and the recorded exceedance coordinate — the univariate
            // rules verbatim (both are primary-axis quantities).
            double recordedHazard = hazardLevel;
            var profile = _profileTransforms;
            if (profile != null)
            {
                for (int i = 0; i < profile.Length; i++)
                {
                    recordedHazard = profile[i].Function(recordedHazard);
                }
            }
            double hazardExceedance = recordOutput
                ? Tools.Clamp(1d - Hazard.CDF(hazardLevel), 0d, 1d)
                : double.NaN;

            bool wantSecondary = ConsequenceTypeCount > 1 && (recordOutput || typeOutputs != null);
            int componentTypes = wantSecondary ? ConsequenceTypeCount : 1;
            bool hasClaimed = _layout.ClaimedStateCount > 0;
            int unitCount = _layout.CombinationUnitCount;
            bool accumulateContribution = recordOutput && _fModes.Count > 0;
            bool recordAdjusted = recordOutput && RecordAdjustedModeCurves && _fModes.Count > 0;

            // Open the modes' staged evaluations and reset the cross-bin accumulators.
            for (int j = 0; j < _fModes.Count; j++)
            {
                _fModes[j].BeginBinnedEvaluation(_pairedSampled[j], recordOutput, componentTypes);
            }
            for (int k = 0; k < componentTypes; k++)
            {
                _scratchTypeOutputs[k].Reset();
                _scratchExcessProbabilities[k].Clear();
                _scratchExcessValues[k].Clear();
                _binExpectedFailure![k] = 0d;
                _binExpectedExcess![k] = 0d;
                _binNonFailure![k] = 0d;
                _binMinN![k] = k == 0 ? realization.MinN : realization.AdditionalMinN[k - 1];
                _binMaxN![k] = k == 0 ? realization.MaxN : realization.AdditionalMaxN[k - 1];
                if (accumulateContribution)
                {
                    Array.Clear(_binContributionProbability![k], 0, _fModes.Count);
                    Array.Clear(_binContributionFailure![k], 0, _fModes.Count);
                    Array.Clear(_binContributionExcess![k], 0, _fModes.Count);
                }
            }

            // Recording staging: fresh lists per recording evaluation, adopted by the committed
            // risk points (the univariate recording contract — non-recording evaluations
            // allocate nothing).
            List<double>[]? backgroundProbabilityStaging = null;
            List<double>[]? backgroundValueStaging = null;
            List<double>[]? nonFailProbabilityStaging = null;
            List<double>[]? nonFailValueStaging = null;
            List<double>[]? totalProbabilityStaging = null;
            List<double>[]? totalValueStaging = null;
            List<double>[][]? claimedProbabilityStaging = null;
            List<double>[][]? claimedValueStaging = null;
            List<double>[][]? adjustedFailProbabilityStaging = null;
            List<double>[][]? adjustedFailValueStaging = null;
            List<double>[][]? adjustedExcessProbabilityStaging = null;
            List<double>[][]? adjustedExcessValueStaging = null;
            double[]? contributionSnapshot = null;
            if (recordOutput)
            {
                backgroundProbabilityStaging = new List<double>[componentTypes];
                backgroundValueStaging = new List<double>[componentTypes];
                nonFailProbabilityStaging = new List<double>[componentTypes];
                nonFailValueStaging = new List<double>[componentTypes];
                totalProbabilityStaging = new List<double>[componentTypes];
                totalValueStaging = new List<double>[componentTypes];
                for (int k = 0; k < componentTypes; k++)
                {
                    backgroundProbabilityStaging[k] = new List<double>();
                    backgroundValueStaging[k] = new List<double>();
                    nonFailProbabilityStaging[k] = new List<double>();
                    nonFailValueStaging[k] = new List<double>();
                    totalProbabilityStaging[k] = new List<double>();
                    totalValueStaging[k] = new List<double>();
                }
                if (hasClaimed)
                {
                    claimedProbabilityStaging = new List<double>[componentTypes][];
                    claimedValueStaging = new List<double>[componentTypes][];
                    for (int k = 0; k < componentTypes; k++)
                    {
                        claimedProbabilityStaging[k] = new List<double>[_fModes.Count];
                        claimedValueStaging[k] = new List<double>[_fModes.Count];
                        for (int j = 0; j < _fModes.Count; j++)
                        {
                            if (_layout.IsFailureState[j]) continue;
                            claimedProbabilityStaging[k][j] = new List<double>();
                            claimedValueStaging[k][j] = new List<double>();
                        }
                    }
                }
                if (recordAdjusted)
                {
                    adjustedFailProbabilityStaging = new List<double>[componentTypes][];
                    adjustedFailValueStaging = new List<double>[componentTypes][];
                    adjustedExcessProbabilityStaging = new List<double>[componentTypes][];
                    adjustedExcessValueStaging = new List<double>[componentTypes][];
                    for (int k = 0; k < componentTypes; k++)
                    {
                        adjustedFailProbabilityStaging[k] = new List<double>[_fModes.Count];
                        adjustedFailValueStaging[k] = new List<double>[_fModes.Count];
                        adjustedExcessProbabilityStaging[k] = new List<double>[_fModes.Count];
                        adjustedExcessValueStaging[k] = new List<double>[_fModes.Count];
                        for (int j = 0; j < _fModes.Count; j++)
                        {
                            if (!_layout.IsFailureState[j]) continue;
                            adjustedFailProbabilityStaging[k][j] = new List<double>();
                            adjustedFailValueStaging[k][j] = new List<double>();
                            adjustedExcessProbabilityStaging[k][j] = new List<double>();
                            adjustedExcessValueStaging[k][j] = new List<double>();
                        }
                    }
                    if (_failureModeMethod == FailureModeMethod.JointFailures)
                    {
                        contributionSnapshot = new double[_fModes.Count];
                    }
                }
            }

            double totalProbabilityOfFailure = 0d;
            for (int b = 0; b < nodeCount; b++)
            {
                double binY = yNodes[b];
                double binWeight = weights[b];

                // Every mode at the same (x, y_j) — the conditional-independence point the
                // combination kernels require.
                var modeOutputs = _scratchModeOutputs;
                var modeTypeOutputs = _scratchModeTypeOutputs;
                var responseProbabilities = _scratchResponseProbabilities;
                responseProbabilities.Clear();
                for (int j = 0; j < _fModes.Count; j++)
                {
                    _fModes[j].ComputeRiskBinned(hazardLevel, binY, binWeight, flags, modeTypeOutputs[j]);
                    modeOutputs[j] = modeTypeOutputs[j][0];
                    responseProbabilities.Add(Tools.Clamp(modeOutputs[j].ProbabilityOfFailure, 0d, 1d));
                }

                // The bin's combination structure — type-independent, the univariate kernels on
                // the per-bin unit masses. The joint pathway probabilities are w_j-scaled in
                // place after decomposition: the scale flows linearly and exactly through every
                // entry probability, expected sum, and attribution split, while the raw unit
                // masses keep driving the conditional branch weights.
                List<double>? pathwayProbabilities = null;
                List<int[]>? pathwayIndicators = null;
                double[]? adjustedProbabilities = null;
                double binProbabilityOfFailure = 0d;
                var unitProbabilities = _scratchUnitProbabilities;
                unitProbabilities.Clear();
                if (_fModes.Count > 0 && unitCount > 0)
                {
                    for (int un = 0; un < unitCount; un++)
                    {
                        var members = _layout.CombinationUnitStates[un];
                        double mass = 0d;
                        for (int m = 0; m < members.Length; m++)
                        {
                            mass += responseProbabilities[members[m]];
                        }
                        unitProbabilities.Add(Tools.Clamp(mass, 0d, 1d));
                    }

                    if (_failureModeMethod == FailureModeMethod.JointFailures)
                    {
                        ComputePathwayDecomposition(unitProbabilities, out pathwayProbabilities, out pathwayIndicators);
                        for (int j = 0; j < pathwayProbabilities.Count; j++)
                        {
                            binProbabilityOfFailure += pathwayProbabilities[j];
                            pathwayProbabilities[j] *= binWeight;
                        }
                    }
                    else
                    {
                        adjustedProbabilities = _scratchAdjustedProbabilities;
                        double commonCauseFactor = _failureModeMethod == FailureModeMethod.CommonCauseFailures
                            ? CommonCauseFactor(unitProbabilities)
                            : 0d;
                        double normalization = 1d;
                        if (_failureModeMethod == FailureModeMethod.MutuallyExclusive)
                        {
                            normalization = Probability.MutuallyExclusiveAdjustment(unitProbabilities);
                            if (normalization < 1d) flags.HasProbabilityGreaterThanOne = true;
                        }
                        for (int un = 0; un < unitCount; un++)
                        {
                            double adjusted;
                            if (_failureModeMethod == FailureModeMethod.CompetingFailures)
                            {
                                adjusted = unitCount == 1 ? unitProbabilities[0] : _cumulativeIncidenceFunctions![un].CDF(hazardLevel);
                            }
                            else if (_failureModeMethod == FailureModeMethod.CommonCauseFailures)
                            {
                                adjusted = unitProbabilities[un] * commonCauseFactor;
                            }
                            else
                            {
                                adjusted = unitProbabilities[un] * normalization;
                            }
                            adjusted = Tools.Clamp(adjusted, 0d, 1d);
                            binProbabilityOfFailure += adjusted;

                            var members = _layout.CombinationUnitStates[un];
                            double unitMass = unitProbabilities[un];
                            for (int m = 0; m < members.Length; m++)
                            {
                                int state = members[m];
                                adjustedProbabilities[state] = unitMass > 0d
                                    ? Tools.Clamp(adjusted * (responseProbabilities[state] / unitMass), 0d, 1d)
                                    : 0d;
                            }
                        }
                    }
                }
                binProbabilityOfFailure = Tools.Clamp(binProbabilityOfFailure, 0d, 1d);
                totalProbabilityOfFailure += binWeight * binProbabilityOfFailure;
                double binProbabilityOfNonFailure = Tools.Clamp(1d - binProbabilityOfFailure, 0d, 1d);

                // The bin's claimed complement shares — the univariate §7.9.5 arithmetic on the
                // bin's masses.
                double claimedShareTotal = 0d;
                if (hasClaimed)
                {
                    var claimedConditional = _scratchClaimedConditional!;
                    for (int j = 0; j < _fModes.Count; j++)
                    {
                        if (_layout.IsFailureState[j])
                        {
                            claimedConditional[j] = 0d;
                            continue;
                        }
                        int owningUnit = _layout.ClaimedStateUnit[j];
                        double divisor = owningUnit >= 0 ? Tools.Clamp(1d - unitProbabilities[owningUnit], 0d, 1d) : 1d;
                        double share = divisor > 0d ? responseProbabilities[j] / divisor : 0d;
                        claimedConditional[j] = Tools.Clamp(share, 0d, 1d);
                        claimedShareTotal += claimedConditional[j];
                    }
                    if (claimedShareTotal > 1d)
                    {
                        for (int j = 0; j < _fModes.Count; j++)
                        {
                            claimedConditional[j] /= claimedShareTotal;
                        }
                        claimedShareTotal = 1d;
                    }
                }

                // The per-type consequence kernels at this bin, appending w_j-scaled entries
                // into the cross-bin accumulators.
                for (int k = 0; k < componentTypes; k++)
                {
                    double[] nonFailWeights = _unitWeight;
                    double[] nonFailValues = _zeroValue;
                    double binNonFailureConsequences = 0d;
                    if (_nfMode != null)
                    {
                        _nfMode.EvaluateConsequenceBranchesAt(hazardLevel, binY, k, flags, out nonFailWeights, out nonFailValues);
                        for (int j = 0; j < nonFailWeights.Length; j++)
                        {
                            binNonFailureConsequences += nonFailWeights[j] * nonFailValues[j];
                        }
                    }

                    var typeOutput = _scratchTypeOutputs[k];
                    var failEntryProbabilities = typeOutput.ResponseProbabilities;
                    var failEntryValues = typeOutput.FailureConsequences;
                    List<double> excessEntryProbabilities = _scratchExcessProbabilities[k];
                    List<double> excessEntryValues = _scratchExcessValues[k];
                    int failStart = failEntryValues.Count;

                    double expectedFailureConsequences = _binExpectedFailure![k];
                    double expectedExcessConsequences = _binExpectedExcess![k];
                    double minN = _binMinN![k];
                    double maxN = _binMaxN![k];

                    // The bin's complement pair baseline — the raw background branches, or the
                    // claimed conditional mixture (§7.9.5) at this bin's shares.
                    double[] pairWeights = nonFailWeights;
                    double[] pairValues = nonFailValues;
                    if (hasClaimed)
                    {
                        double remainderShare = Tools.Clamp(1d - claimedShareTotal, 0d, 1d);
                        pairWeights = _scratchPairWeights![k];
                        pairValues = _scratchPairValues![k];
                        int cursor = 0;
                        if (_nfMode != null)
                        {
                            for (int q = 0; q < nonFailWeights.Length; q++)
                            {
                                pairWeights[cursor] = remainderShare * nonFailWeights[q];
                                pairValues[cursor++] = nonFailValues[q];
                            }
                        }
                        for (int j = 0; j < _fModes.Count; j++)
                        {
                            if (_layout.IsFailureState[j]) continue;
                            double share = _scratchClaimedConditional![j];
                            _fModes[j].EvaluateConsequenceBranchesAt(hazardLevel, binY, k, flags, out double[] claimedWeights, out double[] claimedValues);
                            for (int q = 0; q < claimedWeights.Length; q++)
                            {
                                pairWeights[cursor] = share * claimedWeights[q];
                                pairValues[cursor++] = claimedValues[q];
                                minN = Math.Min(minN, claimedValues[q]);
                                maxN = Math.Max(maxN, claimedValues[q]);
                            }

                            // The claimed states' conditional complement entries at this bin
                            // (§7.9.2), folded by the bin weight.
                            if (recordOutput)
                            {
                                var stateProbabilities = claimedProbabilityStaging![k][j];
                                var stateValues = claimedValueStaging![k][j];
                                for (int q = 0; q < claimedWeights.Length; q++)
                                {
                                    stateProbabilities.Add(Tools.Clamp(binWeight * binProbabilityOfNonFailure * share * claimedWeights[q], 0d, 1d));
                                    stateValues.Add(claimedValues[q]);
                                }
                            }
                        }
                        binNonFailureConsequences = 0d;
                        for (int q = 0; q < cursor; q++)
                        {
                            binNonFailureConsequences += pairWeights[q] * pairValues[q];
                        }
                    }
                    _binNonFailure![k] += binWeight * binNonFailureConsequences;

                    if (_fModes.Count > 0 && unitCount > 0)
                    {
                        var typeModeOutputs = k == 0 ? modeOutputs : FillTypeColumn(k);
                        if (_failureModeMethod == FailureModeMethod.JointFailures)
                        {
                            if (contributionSnapshot != null)
                            {
                                Array.Copy(_binContributionProbability![k], contributionSnapshot, _fModes.Count);
                            }
                            ComputeJointPathwayEntries(unitProbabilities, typeModeOutputs, pathwayProbabilities!, pathwayIndicators!,
                                pairWeights, pairValues,
                                failEntryProbabilities, failEntryValues, excessEntryProbabilities, excessEntryValues,
                                ref expectedFailureConsequences, ref expectedExcessConsequences, ref minN, ref maxN,
                                accumulateContribution ? _binContributionProbability![k] : null,
                                accumulateContribution ? _binContributionFailure![k] : null,
                                accumulateContribution ? _binContributionExcess![k] : null);

                            // The adjusted-mode entries at this bin: the mode's conditional
                            // entries rescaled to its bin attribution (the contribution delta
                            // this bin added, already w_j-folded).
                            if (recordAdjusted && contributionSnapshot != null)
                            {
                                for (int j = 0; j < _fModes.Count; j++)
                                {
                                    if (!_layout.IsFailureState[j]) continue;
                                    double binAttribution = _binContributionProbability![k][j] - contributionSnapshot[j];
                                    AppendAdjustedEntries(typeModeOutputs[j], responseProbabilities[j], binAttribution,
                                        adjustedFailProbabilityStaging![k][j], adjustedFailValueStaging![k][j],
                                        adjustedExcessProbabilityStaging![k][j], adjustedExcessValueStaging![k][j]);
                                }
                            }
                        }
                        else
                        {
                            for (int j = 0; j < _fModes.Count; j++)
                            {
                                if (!_layout.IsFailureState[j]) continue;

                                double weightedAdjusted = binWeight * adjustedProbabilities![j];
                                AppendModeEntries(typeModeOutputs[j], responseProbabilities[j], weightedAdjusted,
                                    failEntryProbabilities, failEntryValues, excessEntryProbabilities, excessEntryValues, ref minN, ref maxN);

                                if (recordAdjusted)
                                {
                                    AppendAdjustedEntries(typeModeOutputs[j], responseProbabilities[j], weightedAdjusted,
                                        adjustedFailProbabilityStaging![k][j], adjustedFailValueStaging![k][j],
                                        adjustedExcessProbabilityStaging![k][j], adjustedExcessValueStaging![k][j]);
                                }

                                expectedFailureConsequences += weightedAdjusted * typeModeOutputs[j].MeanFailureConsequences;
                                expectedExcessConsequences += weightedAdjusted * typeModeOutputs[j].MeanExcessConsequences;

                                if (accumulateContribution)
                                {
                                    _binContributionProbability![k][j] += weightedAdjusted;
                                    _binContributionFailure![k][j] += weightedAdjusted * typeModeOutputs[j].MeanFailureConsequences;
                                    _binContributionExcess![k][j] += weightedAdjusted * typeModeOutputs[j].MeanExcessConsequences;
                                }
                            }
                        }
                    }

                    // The bin's interim excess-list entries against ITS OWN complement baseline
                    // (conditional coherence on one (x, y_j) scenario — the documented
                    // ComponentRiskOutput interim).
                    for (int e = failStart; e < failEntryValues.Count; e++)
                    {
                        typeOutput.ExcessConsequences.Add(Math.Max(0d, failEntryValues[e] - binNonFailureConsequences));
                    }

                    // The bin's complement-stream entries, w_j-folded.
                    if (recordOutput)
                    {
                        var backgroundProbabilities = backgroundProbabilityStaging![k];
                        var backgroundValues = backgroundValueStaging![k];
                        var nonFailProbabilities = nonFailProbabilityStaging![k];
                        var nonFailValues2 = nonFailValueStaging![k];
                        var totalProbabilities = totalProbabilityStaging![k];
                        var totalValues = totalValueStaging![k];
                        for (int e = failStart; e < failEntryValues.Count; e++)
                        {
                            totalProbabilities.Add(failEntryProbabilities[e]);
                            totalValues.Add(failEntryValues[e]);
                        }
                        for (int q = 0; q < pairWeights.Length; q++)
                        {
                            double backgroundProbability = Tools.Clamp(binWeight * pairWeights[q], 0d, 1d);
                            double nonFailProbability = Tools.Clamp(binWeight * binProbabilityOfNonFailure * pairWeights[q], 0d, 1d);
                            backgroundProbabilities.Add(backgroundProbability);
                            backgroundValues.Add(pairValues[q]);
                            nonFailProbabilities.Add(nonFailProbability);
                            nonFailValues2.Add(pairValues[q]);
                            totalProbabilities.Add(nonFailProbability);
                            totalValues.Add(pairValues[q]);
                        }
                    }

                    _binExpectedFailure[k] = expectedFailureConsequences;
                    _binExpectedExcess[k] = expectedExcessConsequences;
                    _binMinN[k] = minN;
                    _binMaxN[k] = maxN;
                }
            }
            totalProbabilityOfFailure = Tools.Clamp(totalProbabilityOfFailure, 0d, 1d);
            double probabilityOfNonFailure = Tools.Clamp(1d - totalProbabilityOfFailure, 0d, 1d);

            // Contribution samples submit once per evaluation and type — the ledger dedupes
            // rows by probability coordinate, so the fold must arrive as one row.
            if (accumulateContribution)
            {
                for (int k = 0; k < componentTypes; k++)
                {
                    for (int j = 0; j < _fModes.Count; j++)
                    {
                        realization.FailureModes[j].AddContributionSample(k, probability,
                            _binContributionProbability![k][j], _binContributionFailure![k][j], _binContributionExcess![k][j]);
                    }
                }
            }

            // Finalize per type: the marginalized scalars, the committed component points (one
            // per stream per evaluation), and the extents.
            ComponentRiskOutput primary = null!;
            for (int k = 0; k < componentTypes; k++)
            {
                var typeOutput = _scratchTypeOutputs[k];
                if (k == 0) primary = typeOutput;
                double effectiveNonFailure = _binNonFailure![k];
                double meanFailureConsequences = totalProbabilityOfFailure == 0d ? 0d : _binExpectedFailure![k] / totalProbabilityOfFailure;
                double meanExcessConsequences = totalProbabilityOfFailure == 0d ? 0d : _binExpectedExcess![k] / totalProbabilityOfFailure;

                if (recordOutput)
                {
                    var target = k == 0 ? realization.Curves : realization.AdditionalCurves[k - 1];
                    if (_fModes.Count > 0)
                    {
                        target.Fail.AddRiskPoint(recordedHazard, probability,
                            new List<double>(typeOutput.ResponseProbabilities), new List<double>(typeOutput.FailureConsequences), hazardExceedance);
                        target.Excess.AddRiskPoint(recordedHazard, probability,
                            new List<double>(_scratchExcessProbabilities[k]), new List<double>(_scratchExcessValues[k]));
                    }
                    if (hasClaimed)
                    {
                        for (int j = 0; j < _fModes.Count; j++)
                        {
                            if (_layout.IsFailureState[j]) continue;
                            var claimedTarget = k == 0 ? realization.FailureModes[j].Curves : realization.FailureModes[j].AdditionalCurves[k - 1];
                            claimedTarget.NonFail.AddRiskPoint(recordedHazard, probability,
                                claimedProbabilityStaging![k][j], claimedValueStaging![k][j]);
                        }
                    }
                    target.Background.AddRiskPoint(recordedHazard, probability, backgroundProbabilityStaging![k], backgroundValueStaging![k]);
                    target.NonFail.AddRiskPoint(recordedHazard, probability, nonFailProbabilityStaging![k], nonFailValueStaging![k]);
                    target.Total.AddRiskPoint(recordedHazard, probability, totalProbabilityStaging![k], totalValueStaging![k]);
                    if (recordAdjusted)
                    {
                        for (int j = 0; j < _fModes.Count; j++)
                        {
                            if (!_layout.IsFailureState[j]) continue;
                            var adjustedTarget = realization.FailureModes[j].AdjustedCurvesFor(k);
                            if (adjustedTarget == null) continue;
                            adjustedTarget.Fail.AddRiskPoint(recordedHazard, probability,
                                adjustedFailProbabilityStaging![k][j], adjustedFailValueStaging![k][j], hazardExceedance);
                            adjustedTarget.Excess.AddRiskPoint(recordedHazard, probability,
                                adjustedExcessProbabilityStaging![k][j], adjustedExcessValueStaging![k][j]);
                        }
                    }
                }

                double minN = Math.Min(_binMinN![k], effectiveNonFailure);
                double maxN = Math.Max(_binMaxN![k], Math.Max(meanFailureConsequences, effectiveNonFailure));
                if (k == 0)
                {
                    realization.MinN = minN;
                    realization.MaxN = maxN;
                }
                else
                {
                    realization.AdditionalMinN[k - 1] = minN;
                    realization.AdditionalMaxN[k - 1] = maxN;
                }

                typeOutput.ProbabilityOfFailure = totalProbabilityOfFailure;
                typeOutput.ProbabilityOfNonFailure = probabilityOfNonFailure;
                typeOutput.NonFailureConsequences = effectiveNonFailure;
                typeOutput.MeanFailureConsequences = meanFailureConsequences;
                typeOutput.MeanExcessConsequences = meanExcessConsequences;
                if (typeOutputs != null) typeOutputs[k] = typeOutput;
            }

            // The modes' staged points commit once per evaluation.
            if (recordOutput)
            {
                for (int j = 0; j < _fModes.Count; j++)
                {
                    _fModes[j].CommitBinnedPoint(realization.FailureModes[j], recordedHazard, probability, hazardExceedance);
                }
            }

            realization.MinH = Math.Min(realization.MinH, recordedHazard);
            realization.MaxH = Math.Max(realization.MaxH, recordedHazard);
            return primary;
        }

        /// <summary>
        /// Whether this component integrates a conditional secondary dimension (a bivariate
        /// hazard) — the engine's dispatch onto the two-dimensional additive interior.
        /// </summary>
        internal bool HasConditionalSecondary => _conditionalHazard != null;

        /// <summary>
        /// Whether this snapshot evaluates the secondary axis on the fixed
        /// conditional-trapezoid grid (the discretization-error instrument's construction) —
        /// the engine keeps such snapshots on the one-dimensional conditional-grid interior.
        /// </summary>
        internal bool UsesFixedConditionalGrid => _conditionalFixedGrid;

        /// <summary>
        /// Configures the per-slice adaptive conditional quadrature discipline — the engine
        /// applies the ensemble/mean split (the relaxed ensemble tolerance with its minimum
        /// depth, or the full mean-pass discipline) and the reliability-mode surrogate flavor.
        /// The defaults are the mean discipline with the consequence-bearing surrogate, so a
        /// path that never configures gets the rigorous treatment.
        /// </summary>
        /// <param name="relativeTolerance">The relative tolerance of the conditional refinement.</param>
        /// <param name="minDepth">The forced minimum refinement depth.</param>
        /// <param name="maxDepth">The maximum refinement depth.</param>
        /// <param name="includeConsequences">True for the failure-density surrogate; false refines on failure probability alone (reliability mode).</param>
        internal void ConfigureConditionalQuadrature(double relativeTolerance, int minDepth, int maxDepth, bool includeConsequences)
        {
            _conditionalRelativeTolerance = relativeTolerance;
            _conditionalMinDepth = minDepth;
            _conditionalMaxDepth = maxDepth;
            _conditionalIncludeConsequences = includeConsequences;
            _surrogateScalesReady = false;
        }

        /// <summary>
        /// Raises the per-slice conditional sweep's evaluation budget above its default of
        /// 21 × bins. The additive interior grants its endpoint columns the same fair-share
        /// budget its strips get — an endpoint rectangle can carry most of a hazard's mass
        /// (everything beyond the tabulated support) and runs once per pass, while the
        /// per-evaluation seats (the joint system integrand, the probes, sensitivity) keep
        /// the bin-bounded default their call volume demands. Must be configured before the
        /// first adaptive sweep — the mesh capacity freezes at first use.
        /// </summary>
        /// <param name="budget">The sweep evaluation budget.</param>
        /// <exception cref="InvalidOperationException">Thrown after the sweep machinery has been built.</exception>
        internal void ConfigureConditionalSweepBudget(int budget)
        {
            if (_conditionalIntegrator != null)
            {
                throw new InvalidOperationException("The conditional sweep budget must be configured before the first adaptive sweep.");
            }
            _conditionalSweepBudget = Math.Max(budget, 21 * (_conditionalHazard?.SecondaryIntegrationBins ?? 1));
        }

        /// <summary>
        /// The hard bound on a per-slice adaptive conditional mesh's node count under the
        /// default refinement budget of 21 × bins surrogate evaluations: every subdivision
        /// evaluates both children before the budget check can stop it, so the accepted
        /// composite rule holds at most ⌈(bins + 1)/2⌉ intervals of 21 nodes each (231 at the
        /// default 20 bins; the fixed grid's 21 nodes arise where one interval converges
        /// immediately).
        /// </summary>
        /// <param name="bins">The hazard's configured secondary integration bin count.</param>
        /// <returns>The mesh capacity.</returns>
        internal static int ConditionalMeshCapacity(int bins)
        {
            return ConditionalMeshCapacityForBudget(21 * bins);
        }

        /// <summary>
        /// The mesh-capacity bound for an arbitrary sweep budget: accepted intervals number at
        /// most ⌈(budget/21 + 1)/2⌉, each contributing 21 nodes.
        /// </summary>
        /// <param name="budget">The sweep evaluation budget.</param>
        /// <returns>The mesh capacity.</returns>
        internal static int ConditionalMeshCapacityForBudget(int budget)
        {
            int intervals = (budget / 21 + 2) / 2;
            return 21 * Math.Max(1, intervals);
        }

        /// <summary>
        /// Runs pass one of the adaptive conditional sweep at one primary slice: the reused
        /// Gauss–Kronrod integrator refines the failure-density surrogate over
        /// conditional-probability space, the mesh adopts the accepted composite rule with its
        /// exhaustive unit-sum gate, and the adopted nodes invert into the column buffer.
        /// Budget or depth exhaustion is accepted by design — the frozen composite is still a
        /// complete partition — while a thrown surrogate evaluation propagates with its own
        /// stack (nothing is half-recorded; the mesh is discarded on the next reset).
        /// </summary>
        /// <param name="u">The slice's clamped non-exceedance probability.</param>
        /// <param name="hazardLevel">The primary hazard level matching <paramref name="u"/>.</param>
        /// <returns>The adopted node count.</returns>
        private int BuildAdaptiveConditionalMesh(double u, double hazardLevel)
        {
            var conditional = _conditionalHazard!;
            if (_conditionalIntegrator == null)
            {
                if (_conditionalSweepBudget == 0)
                {
                    _conditionalSweepBudget = 21 * conditional.SecondaryIntegrationBins;
                }
                int capacity = ConditionalMeshCapacityForBudget(_conditionalSweepBudget);
                _conditionalMesh = new ConditionalQuadratureMesh(capacity);
                _conditionalIntegrator = new Numerics.Mathematics.Integration.AdaptiveGaussKronrod(ConditionalObjective, ConditionalZFloor, ConditionalZCeiling)
                {
                    ReportFailure = true,
                    AbsoluteTolerance = ConditionalAbsoluteTolerance,
                    Recorder = _conditionalMesh.Record,
                };
                EnsureColumnCapacity(capacity);
            }

            EnsureSurrogateScales();
            _surrogateU = u;
            _surrogateX = hazardLevel;
            _conditionalMesh!.Reset();
            var integrator = _conditionalIntegrator;
            integrator.RelativeTolerance = _conditionalRelativeTolerance;
            integrator.MinDepth = _conditionalMinDepth;
            integrator.MaxDepth = _conditionalMaxDepth;
            integrator.MaxFunctionEvaluations = _conditionalSweepBudget;
            integrator.Integrate();
            _conditionalMesh.AdoptProbitExhaustive($"system component '{Name}'");

            int count = _conditionalMesh.Count;
            EnsureColumnCapacity(count);
            var nodes = _conditionalMesh.Nodes;
            for (int i = 0; i < count; i++)
            {
                _columnY[i] = conditional.InverseConditional(u, Normal.StandardCDF(nodes[i]));
            }
            return count;
        }

        /// <summary>
        /// The per-slice refinement surrogate in the probit coordinate at the slice in flight —
        /// the one-dimensional integrand of <see cref="BuildAdaptiveConditionalMesh"/>:
        /// φ(z) times the balanced failure density at (x, y(Φ(z))).
        /// </summary>
        /// <param name="z">The probit conditional coordinate z = Φ⁻¹(t).</param>
        /// <returns>The Jacobian-weighted surrogate value.</returns>
        private double ConditionalObjective(double z)
        {
            double y = _conditionalHazard!.InverseConditional(_surrogateU, Normal.StandardCDF(z));
            return Normal.StandardPDF(z) * ConditionalFailureDensity(_surrogateX, y);
        }

        /// <summary>
        /// The two-dimensional refinement surrogate over (primary non-exceedance, probit
        /// conditional coordinate) — the additive interior's integrand: φ(z) times the
        /// balanced failure density. The primary hazard level caches on the last slice
        /// probability (the tensor rule evaluates same-u node runs).
        /// </summary>
        /// <param name="u">The primary non-exceedance probability.</param>
        /// <param name="z">The probit conditional coordinate z = Φ⁻¹(t).</param>
        /// <returns>The Jacobian-weighted surrogate value.</returns>
        internal double ConditionalDensityAt(double u, double z)
        {
            EnsureSurrogateScales();
            double clamped = Tools.Clamp(u, ProbabilityFloor, 1d - ProbabilityFloor);
            if (clamped != _surrogateU)
            {
                _surrogateU = clamped;
                _surrogateX = Hazard.InverseCDF(clamped);
            }
            double y = _conditionalHazard!.InverseConditional(clamped, Normal.StandardCDF(z));
            return Normal.StandardPDF(z) * ConditionalFailureDensity(_surrogateX, y);
        }

        /// <summary>
        /// The balanced refinement surrogate at one joint point: the failure-consequence
        /// density Σ p_j·c̄_j and the failure probability Σ p_j, each normalized by its
        /// deterministic probe scale and summed — the conditional-axis analog of the primary
        /// axis's balanced objective. Both terms matter: a consequence-led surrogate alone is
        /// blind to probability structure wherever consequences vanish (a threshold-bound loss
        /// leaves wide zero-consequence regions whose failure probability still shapes the
        /// committed curves), while a probability-led surrogate alone under-refines the
        /// consequence tail. Under the reliability flavor the consequence term is absent. A
        /// steering quantity only — it decides where the conditional mesh refines, never what
        /// the committed pass computes, so a surrogate approximation costs mesh optimality,
        /// never correctness. Advisory flags from surrogate consequence evaluations land in a
        /// throwaway sink: pass one visits nodes the committed pass may never adopt.
        /// </summary>
        /// <param name="x">The primary hazard level.</param>
        /// <param name="y">The conditional secondary hazard level.</param>
        /// <returns>The surrogate value.</returns>
        private double ConditionalFailureDensity(double x, double y)
        {
            double consequenceDensity = 0d;
            double probabilityDensity = 0d;
            for (int j = 0; j < _fModes.Count; j++)
            {
                if (!_layout.IsFailureState[j]) continue;
                var mode = _fModes[j];
                double p = mode.SRPAt(x, y);
                if (p <= 0d) continue;
                probabilityDensity += p;
                if (_conditionalIncludeConsequences && _surrogateConsequenceScale > 0d)
                {
                    mode.EvaluateConsequenceBranchesAt(x, y, 0, _surrogateFlags, out double[] branchWeights, out double[] branchValues);
                    double mean = 0d;
                    for (int q = 0; q < branchWeights.Length; q++)
                    {
                        mean += branchWeights[q] * branchValues[q];
                    }
                    consequenceDensity += p * mean;
                }
            }
            double surrogate = _surrogateProbabilityScale > 0d ? probabilityDensity / _surrogateProbabilityScale : 0d;
            if (_conditionalIncludeConsequences && _surrogateConsequenceScale > 0d)
            {
                surrogate += consequenceDensity / _surrogateConsequenceScale;
            }
            return surrogate;
        }

        /// <summary>
        /// Probes the balanced surrogate's normalization scales once per snapshot: a
        /// deterministic sweep over the hazard's precomputed conditional grid at three
        /// representative slices accumulates |Σp| and |Σp·c̄|, so the two terms enter the
        /// refinement on comparable footing regardless of consequence units. Draw-free (the
        /// sweep evaluates already-sampled surfaces) and content-deterministic; a vanishing
        /// accumulation drops its term.
        /// </summary>
        private void EnsureSurrogateScales()
        {
            if (_surrogateScalesReady) return;
            _surrogateScalesReady = true;

            var conditional = _conditionalHazard!;
            double consequenceScale = 0d;
            double probabilityScale = 0d;
            Span<double> slices = stackalloc double[] { 0.1d, 0.5d, 0.9d };
            int nodeCount = conditional.ConditionalNodeCount;
            EnsureColumnCapacity(nodeCount);
            var weights = new double[nodeCount];
            foreach (double u in slices)
            {
                double x = Hazard.InverseCDF(u);
                conditional.FillConditionalBins(u, _columnY, weights);
                for (int b = 0; b < nodeCount; b++)
                {
                    double y = _columnY[b];
                    for (int j = 0; j < _fModes.Count; j++)
                    {
                        if (!_layout.IsFailureState[j]) continue;
                        var mode = _fModes[j];
                        double p = mode.SRPAt(x, y);
                        if (p <= 0d) continue;
                        probabilityScale += p;
                        if (_conditionalIncludeConsequences)
                        {
                            mode.EvaluateConsequenceBranchesAt(x, y, 0, _surrogateFlags, out double[] branchWeights, out double[] branchValues);
                            double mean = 0d;
                            for (int q = 0; q < branchWeights.Length; q++)
                            {
                                mean += branchWeights[q] * branchValues[q];
                            }
                            consequenceScale += p * mean;
                        }
                    }
                }
            }
            _surrogateProbabilityScale = probabilityScale;
            _surrogateConsequenceScale = consequenceScale;
        }

        /// <summary>
        /// Replays the staged bivariate evaluation over one supplied conditional column — the
        /// two-dimensional additive interior's pass-two entry: the column's
        /// conditional-probability nodes invert through the slice's conditional distribution
        /// and the shared evaluation body runs verbatim, committing one merged risk point per
        /// stream at this abscissa.
        /// </summary>
        /// <param name="probability">The recorded probability coordinate (the column's primary abscissa).</param>
        /// <param name="hazardLevel">The primary hazard level.</param>
        /// <param name="flags">The realization's computational-warning flags.</param>
        /// <param name="realization">The component's realization sink.</param>
        /// <param name="recordOutput">True to record risk-point entries on the realization curves.</param>
        /// <param name="typeOutputs">The optional per-type output sink.</param>
        /// <param name="u">The slice's non-exceedance probability (clamped internally).</param>
        /// <param name="nodeCount">The column's node count.</param>
        /// <param name="zNodes">The column's probit conditional coordinates z = Φ⁻¹(t).</param>
        /// <param name="weights">The column's probability weights, summing exactly to one.</param>
        /// <returns>The component's primary-type risk output at this column.</returns>
        /// <exception cref="InvalidOperationException">Thrown on a univariate component.</exception>
        internal ComponentRiskOutput ComputeRiskBivariateColumns(double probability, double hazardLevel, RiskComputeFlags flags,
            ComponentRealization realization, bool recordOutput, ComponentRiskOutput[]? typeOutputs,
            double u, int nodeCount, double[] zNodes, double[] weights)
        {
            var conditional = _conditionalHazard
                ?? throw new InvalidOperationException("The conditional column replay requires a bivariate component.");
            double clamped = Tools.Clamp(u, ProbabilityFloor, 1d - ProbabilityFloor);
            EnsureColumnCapacity(nodeCount);
            for (int b = 0; b < nodeCount; b++)
            {
                _columnY[b] = conditional.InverseConditional(clamped, Normal.StandardCDF(zNodes[b]));
            }
            return ComputeRiskBivariateCore(probability, hazardLevel, flags, realization, recordOutput, typeOutputs,
                nodeCount, _columnY, weights);
        }

        /// <summary>
        /// Grows the shared conditional column buffer to at least the requested length.
        /// </summary>
        /// <param name="length">The required node capacity.</param>
        private void EnsureColumnCapacity(int length)
        {
            if (_columnY.Length < length)
            {
                _columnY = new double[Math.Max(length, _columnY.Length * 2)];
            }
        }

        /// <summary>
        /// The pinned absolute-tolerance floor of the conditional refinement: the acceptance
        /// test is absolute OR relative, and the conditional integrals can sit many orders
        /// below the inherited default, so the absolute branch is pinned at the framework
        /// floor and the relative criterion governs.
        /// </summary>
        private const double ConditionalAbsoluteTolerance = 1e-15;

        /// <summary>
        /// The lower probit bound of the conditional refinement coordinate — the standard
        /// normal quantile of the engine's probability floor. The conditional axis integrates
        /// in z = Φ⁻¹(t): both documented fixed-grid failure mechanisms (the copula map's
        /// endpoint cusps and a normal-Z-tailed marginal's tail concentration) are endpoint
        /// layers in t that hide inside a quadrature panel's outermost node gap, while in z
        /// they stretch into interior structure the error estimator genuinely sees.
        /// </summary>
        internal static readonly double ConditionalZFloor = Normal.StandardZ(ProbabilityFloor);

        /// <summary>
        /// The upper probit bound of the conditional refinement coordinate (the deliberate
        /// upstream endpoint asymmetry of the standard normal quantile is inherited as-is).
        /// </summary>
        internal static readonly double ConditionalZCeiling = Normal.StandardZ(1d - ProbabilityFloor);

        /// <summary>
        /// Appends one mode's conditional entries onto its adjusted-curve staging at one bin:
        /// the mode's branch entries rescaled from its conditional marginal probability to its
        /// w_j-folded combination attribution at that bin (summing the folded attribution across
        /// bins reproduces the mode's recorded adjusted share exactly).
        /// </summary>
        /// <param name="modeOutput">The mode's conditional risk output at the bin (one consequence type).</param>
        /// <param name="rawProbability">The mode's conditional marginal probability at the bin.</param>
        /// <param name="attributedProbability">The mode's w_j-folded attributed probability at the bin.</param>
        /// <param name="failProbabilities">The accumulating adjusted Fail entry probabilities.</param>
        /// <param name="failValues">The accumulating adjusted Fail entry values.</param>
        /// <param name="excessProbabilities">The accumulating adjusted Excess entry probabilities.</param>
        /// <param name="excessValues">The accumulating adjusted Excess entry values.</param>
        private static void AppendAdjustedEntries(ComponentRiskOutput modeOutput, double rawProbability, double attributedProbability,
            List<double> failProbabilities, List<double> failValues, List<double> excessProbabilities, List<double> excessValues)
        {
            double scale = rawProbability > 0d ? attributedProbability / rawProbability : 0d;
            for (int i = 0; i < modeOutput.ResponseProbabilities.Count; i++)
            {
                double entryProbability = Tools.Clamp(modeOutput.ResponseProbabilities[i] * scale, 0d, 1d);
                failProbabilities.Add(entryProbability);
                failValues.Add(modeOutput.FailureConsequences[i]);
                excessProbabilities.Add(entryProbability);
                excessValues.Add(modeOutput.ExcessConsequences[i]);
            }
        }

        /// <summary>
        /// Maps a profile-axis hazard level back onto the raw driving-hazard axis through this
        /// realization's sampled profile transform chain, walked in reverse with
        /// <c>InverseFunction</c> (the sensitivity engine's profile-axis-native
        /// interpretation). The identity when no profile is selected. Out-of-range queries clamp per
        /// the sampled functions' own inverse behavior.
        /// </summary>
        /// <param name="profileLevel">The hazard level on the profile axis.</param>
        /// <returns>The raw driving-hazard level.</returns>
        internal double InverseProfileHazard(double profileLevel)
        {
            var profile = _profileTransforms;
            if (profile == null) return profileLevel;
            double level = profileLevel;
            for (int i = profile.Length - 1; i >= 0; i--)
            {
                level = profile[i].InverseFunction(level);
            }
            return level;
        }

        /// <summary>
        /// Fills and returns the reused column view of one consequence type over the per-mode
        /// outputs.
        /// </summary>
        /// <param name="typeIndex">The consequence-type position.</param>
        /// <returns>The per-mode outputs at the given type (the reused column buffer).</returns>
        private ComponentRiskOutput[] FillTypeColumn(int typeIndex)
        {
            var column = _scratchTypeColumns[typeIndex];
            for (int j = 0; j < column.Length; j++)
            {
                column[j] = _scratchModeTypeOutputs[j][typeIndex];
            }
            return column;
        }

        #endregion

        #region Private Helpers

        /// <summary>
        /// The shared single-unit-weight array for the no-non-failure case.
        /// </summary>
        private static readonly double[] _unitWeight = { 1d };

        /// <summary>
        /// The shared single-zero-value array for the no-non-failure case.
        /// </summary>
        private static readonly double[] _zeroValue = { 0d };

        /// <summary>
        /// A combination unit's failure mass at a hazard level: the exact sum of its exclusive
        /// member states' polarity-product weights (disjoint leaves; a singleton unit
        /// reproduces its state's weight bit-identically).
        /// </summary>
        /// <param name="unit">The combination unit.</param>
        /// <param name="hazardLevel">The hazard level.</param>
        /// <returns>The unit's failure mass.</returns>
        private double UnitMassAt(int unit, double hazardLevel)
        {
            var members = _layout.CombinationUnitStates[unit];
            double mass = 0d;
            for (int m = 0; m < members.Length; m++)
            {
                mass += _fModes[members[m]].SRP(hazardLevel);
            }
            return mass;
        }

        /// <summary>
        /// The common-cause adjustment factor per the captured dependency (v1.0 mapping). The
        /// perfectly-positive branch passes the captured correlation matrix even though the
        /// positive joint-probability kernel never reads it: the Numerics overload rejects a
        /// null matrix before dispatching on the dependency, so the v1.0 matrix-free call form
        /// faulted the run (the matrix is always materialized by the
        /// component's sampler setup).
        /// </summary>
        /// <param name="responseProbabilities">The combination units' failure masses.</param>
        /// <returns>The scaling factor in [0, 1].</returns>
        private double CommonCauseFactor(List<double> responseProbabilities)
        {
            if (_failureModeDependency == DependencyType.Independent)
            {
                return Probability.CommonCauseAdjustment(responseProbabilities);
            }
            if (_failureModeDependency == DependencyType.PerfectlyPositive)
            {
                return Probability.CommonCauseAdjustment(responseProbabilities, _correlationMatrix, Probability.DependencyType.PerfectlyPositive);
            }
            return Probability.CommonCauseAdjustment(responseProbabilities, _correlationMatrix, Probability.DependencyType.CorrelationMatrix);
        }

        /// <summary>
        /// Whether the run records each failure mode's combination-adjusted curves alongside its
        /// unadjusted ones. Set by the engine before sampling.
        /// </summary>
        public bool RecordAdjustedModeCurves { get; set; }

        /// <summary>
        /// Records one evaluation's combination-adjusted entries onto a failure mode's adjusted
        /// curves — the mode's branch entries rescaled from its raw marginal probability to the
        /// share the component's combination method left it.
        /// </summary>
        /// <param name="realization">The component realization owning the mode's curves.</param>
        /// <param name="modeIndex">The failure-mode index.</param>
        /// <param name="typeIndex">The consequence-type index; zero is the primary type.</param>
        /// <param name="modeOutput">The mode's risk output at this consequence type.</param>
        /// <param name="rawProbability">The mode's raw marginal failure probability.</param>
        /// <param name="adjustedProbability">The mode's adjusted failure probability.</param>
        /// <param name="recordedHazard">The recorded hazard coordinate.</param>
        /// <param name="probability">The evaluation's probability coordinate.</param>
        /// <param name="hazardExceedance">The driving hazard's exceedance probability, or NaN.</param>
        private static void RecordAdjustedModeEntries(ComponentRealization realization, int modeIndex, int typeIndex,
            ComponentRiskOutput modeOutput, double rawProbability, double adjustedProbability,
            double recordedHazard, double probability, double hazardExceedance)
        {
            if (modeIndex >= realization.FailureModes.Count) return;
            var target = realization.FailureModes[modeIndex].AdjustedCurvesFor(typeIndex);
            if (target == null) return;

            double scale = rawProbability > 0d ? adjustedProbability / rawProbability : 0d;
            int entries = modeOutput.ResponseProbabilities.Count;
            var failProbabilities = new List<double>(entries);
            var failValues = new List<double>(entries);
            var excessProbabilities = new List<double>(entries);
            var excessValues = new List<double>(entries);
            for (int i = 0; i < entries; i++)
            {
                double entryProbability = Tools.Clamp(modeOutput.ResponseProbabilities[i] * scale, 0d, 1d);
                failProbabilities.Add(entryProbability);
                failValues.Add(modeOutput.FailureConsequences[i]);
                excessProbabilities.Add(entryProbability);
                excessValues.Add(modeOutput.ExcessConsequences[i]);
            }
            target.Fail.AddRiskPoint(recordedHazard, probability, failProbabilities, failValues, hazardExceedance);
            target.Excess.AddRiskPoint(recordedHazard, probability, excessProbabilities, excessValues);
        }

        /// <summary>
        /// Appends one mode's per-branch entries at an adjusted mode probability (the competing,
        /// common-cause, and mutually-exclusive paths): failure entries per branch, excess
        /// entries from the mode's paired pair distribution scaled to the adjusted probability,
        /// and the extent tracking.
        /// </summary>
        /// <param name="modeOutput">The mode's risk output at this hazard level (one consequence type).</param>
        /// <param name="rawProbability">The mode's unadjusted response probability.</param>
        /// <param name="adjustedProbability">The mode's combination-adjusted probability.</param>
        /// <param name="failEntryProbabilities">The accumulating failure entry probabilities.</param>
        /// <param name="failEntryValues">The accumulating failure entry values.</param>
        /// <param name="excessEntryProbabilities">The accumulating excess entry probabilities.</param>
        /// <param name="excessEntryValues">The accumulating excess entry values.</param>
        /// <param name="minN">The type's running minimum consequence extent.</param>
        /// <param name="maxN">The type's running maximum consequence extent.</param>
        private static void AppendModeEntries(ComponentRiskOutput modeOutput, double rawProbability, double adjustedProbability,
            List<double> failEntryProbabilities, List<double> failEntryValues,
            List<double> excessEntryProbabilities, List<double> excessEntryValues, ref double minN, ref double maxN)
        {
            // Branch entry probabilities are the mode's (rawProbability·weight) entries rescaled
            // to the adjusted probability. A zero raw probability contributes nothing.
            double scale = rawProbability > 0d ? adjustedProbability / rawProbability : 0d;
            for (int i = 0; i < modeOutput.ResponseProbabilities.Count; i++)
            {
                double entryProbability = Tools.Clamp(modeOutput.ResponseProbabilities[i] * scale, 0d, 1d);
                double entryValue = modeOutput.FailureConsequences[i];
                double entryExcess = modeOutput.ExcessConsequences[i];
                failEntryProbabilities.Add(entryProbability);
                failEntryValues.Add(entryValue);
                excessEntryProbabilities.Add(entryProbability);
                excessEntryValues.Add(entryExcess);
                minN = Math.Min(minN, entryExcess);
                maxN = Math.Max(maxN, entryValue);
            }
        }

        /// <summary>
        /// Lazily decomposes the combination units' failure masses into exclusive joint-failure
        /// pathways in the established subset-size and lexicographic order, then clips each cell
        /// against the remaining unit probability budget.
        /// </summary>
        /// <param name="responseProbabilities">The combination units' failure masses.</param>
        /// <param name="pathwayProbabilities">Receives the reusable exclusive pathway probabilities.</param>
        /// <param name="pathwayIndicators">Receives the reusable pathway on/off indicators.</param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when a dependent-mode correlation matrix is missing.
        /// </exception>
        /// <remarks>
        /// Every dependency uses a caller-owned lazy output buffer. The final pass is deterministic
        /// remaining-budget clipping: earlier cells are never rescaled, and trailing PCM
        /// approximation overshoot is discarded rather than proportionally normalized.
        /// </remarks>
        private void ComputePathwayDecomposition(List<double> responseProbabilities,
            out List<double> pathwayProbabilities, out List<int[]> pathwayIndicators)
        {
            var probabilities = _scratchPathwayProbabilities;
            var indicators = _scratchPathwayIndicators;

            switch (_failureModeDependency)
            {
                case DependencyType.Independent:
                    Probability.IndependentExclusiveLazy(responseProbabilities, probabilities, indicators);
                    break;
                case DependencyType.PerfectlyPositive:
                    Probability.PositivelyDependentExclusiveLazy(responseProbabilities, probabilities, indicators);
                    break;
                default:
                    if (_correlationMatrix == null)
                    {
                        throw new InvalidOperationException("The failure-mode correlation matrix is missing for the dependent joint combination. Call Validate() and correct the reported errors before sampling.");
                    }
                    Probability.ExclusivePCMLazy(responseProbabilities, _correlationMatrix, probabilities, indicators);
                    break;
            }

            ProbabilityPartitionBoundary.ClipInPlace(probabilities);
            pathwayProbabilities = probabilities;
            pathwayIndicators = indicators;
        }
        /// <summary>
        /// The joint-failures consequence kernel for one consequence type: per pathway, the
        /// cross product over the participating combination units' entries — a unit's entry set
        /// concatenates its exclusive member states' exposure branches, each at conditional
        /// weight (state branch mass / unit mass), so within a unit the exclusive states stay
        /// disjoint while across units the weights multiply; the combined consequence follows
        /// the joint-consequence rule, crossed with the component's non-failure branches for
        /// the exact excess pairs. A singleton unit reproduces the plain per-mode arithmetic
        /// bit-identically. The pathway decomposition is supplied by the caller — it is
        /// type-independent and shared.
        /// </summary>
        /// <param name="unitProbabilities">The combination units' failure masses.</param>
        /// <param name="modeOutputs">The per-state risk outputs at this consequence type (branch entries).</param>
        /// <param name="pathwayProbabilities">The exclusive pathway probabilities.</param>
        /// <param name="pathwayIndicators">The pathway on/off indicators (unit space).</param>
        /// <param name="nonFailWeights">The type's non-failure branch weights.</param>
        /// <param name="nonFailValues">The type's non-failure branch values.</param>
        /// <param name="failEntryProbabilities">The accumulating failure entry probabilities.</param>
        /// <param name="failEntryValues">The accumulating failure entry values.</param>
        /// <param name="excessEntryProbabilities">The accumulating excess entry probabilities.</param>
        /// <param name="excessEntryValues">The accumulating excess entry values.</param>
        /// <param name="expectedFailureConsequences">Accumulates Σ entry probability × consequence.</param>
        /// <param name="expectedExcessConsequences">Accumulates Σ excess entry probability × excess.</param>
        /// <param name="minN">The type's running minimum consequence extent.</param>
        /// <param name="maxN">The type's running maximum consequence extent.</param>
        /// <param name="contributionProbability">The optional per-state attributed-probability sink (the % contribution diagnostic); null skips attribution.</param>
        /// <param name="contributionFailure">The optional per-state attributed failure-value sink, parallel to the probability sink.</param>
        /// <param name="contributionExcess">The optional per-state attributed excess-value sink, parallel to the probability sink.</param>
        /// <remarks>
        /// The attribution scheme, generalized from modes to combination units:
        /// within each exclusive pathway tuple the entry probability splits equally among the
        /// participating units (the Shapley value of the union game) and lands on each unit's
        /// picked state — summed over tuples, a unit's share distributes across its members by
        /// their conditional mass, so the Σ-identities hold at every scope. The tuple's
        /// combined failure and excess values split proportionally to the picked states' branch
        /// failure consequences (equal split when they sum to zero). The attribution runs in
        /// separate accumulation chains — the expected-value chains and recorded entries above
        /// are bit-untouched.
        /// </remarks>
        private void ComputeJointPathwayEntries(List<double> unitProbabilities, ComponentRiskOutput[] modeOutputs,
            List<double> pathwayProbabilities, List<int[]> pathwayIndicators,
            double[] nonFailWeights, double[] nonFailValues,
            List<double> failEntryProbabilities, List<double> failEntryValues,
            List<double> excessEntryProbabilities, List<double> excessEntryValues,
            ref double expectedFailureConsequences, ref double expectedExcessConsequences, ref double minN, ref double maxN,
            double[]? contributionProbability = null, double[]? contributionFailure = null, double[]? contributionExcess = null)
        {
            var participating = _scratchParticipating;
            var branchPick = _scratchBranchPick;
            var tupleValues = _scratchTupleValues;
            var pickedStates = _scratchPickedStates;
            for (int j = 0; j < pathwayProbabilities.Count; j++)
            {
                double pathwayProbability = pathwayProbabilities[j];
                if (pathwayProbability <= 0d) continue;

                participating.Clear();
                var indicators = pathwayIndicators[j];
                for (int m = 0; m < indicators.Length; m++)
                {
                    if (indicators[m] == 1) participating.Add(m);
                }
                if (participating.Count == 0) continue;

                // The odometer over the participating units' concatenated (state, branch)
                // entry sets.
                Array.Clear(branchPick, 0, participating.Count);
                while (true)
                {
                    double tupleWeight = 1d;
                    double combined = 0d;
                    double tupleValueSum = 0d;
                    for (int p = 0; p < participating.Count; p++)
                    {
                        int unit = participating[p];
                        var members = _layout.CombinationUnitStates[unit];
                        int pick = branchPick[p];
                        int state = members[0];
                        for (int m = 0; m < members.Length; m++)
                        {
                            state = members[m];
                            int count = modeOutputs[state].ResponseProbabilities.Count;
                            if (pick < count) break;
                            pick -= count;
                        }
                        pickedStates[p] = state;
                        var modeOutput = modeOutputs[state];
                        double raw = unitProbabilities[unit];
                        double weight = raw > 0d
                            ? modeOutput.ResponseProbabilities[pick] / raw
                            : (branchPick[p] == 0 ? 1d : 0d);
                        tupleWeight *= weight;
                        double value = modeOutput.FailureConsequences[pick];
                        if (contributionProbability != null)
                        {
                            tupleValues[p] = value;
                            tupleValueSum += value;
                        }
                        combined = p == 0
                            ? value
                            : _jointConsequences switch
                            {
                                JointConsequenceType.Additive => combined + value,
                                JointConsequenceType.Average => combined + value,
                                JointConsequenceType.Maximum => Math.Max(combined, value),
                                // Minimum — and the defensive arm for an undefined member.
                                _ => Math.Min(combined, value),
                            };
                    }
                    if (_jointConsequences == JointConsequenceType.Average)
                    {
                        combined /= participating.Count;
                    }

                    if (tupleWeight > 0d)
                    {
                        double entryProbability = Tools.Clamp(pathwayProbability * tupleWeight, 0d, 1d);
                        failEntryProbabilities.Add(entryProbability);
                        failEntryValues.Add(combined);
                        expectedFailureConsequences += entryProbability * combined;
                        maxN = Math.Max(maxN, combined);

                        // Exact excess pairs against the non-failure branches. The attribution's
                        // tuple-excess total accumulates in its own chain so the expected-value
                        // chain stays bit-identical.
                        double tupleExcess = 0d;
                        for (int q = 0; q < nonFailWeights.Length; q++)
                        {
                            double excess = Math.Max(0d, combined - nonFailValues[q]);
                            double excessProbability = Tools.Clamp(entryProbability * nonFailWeights[q], 0d, 1d);
                            excessEntryProbabilities.Add(excessProbability);
                            excessEntryValues.Add(excess);
                            expectedExcessConsequences += excessProbability * excess;
                            minN = Math.Min(minN, excess);
                            if (contributionProbability != null)
                            {
                                tupleExcess += excessProbability * excess;
                            }
                        }

                        if (contributionProbability != null)
                        {
                            double equalShare = 1d / participating.Count;
                            double tupleFailure = entryProbability * combined;
                            for (int p = 0; p < participating.Count; p++)
                            {
                                int state = pickedStates[p];
                                double share = tupleValueSum > 0d ? tupleValues[p] / tupleValueSum : equalShare;
                                contributionProbability[state] += entryProbability * equalShare;
                                contributionFailure![state] += tupleFailure * share;
                                contributionExcess![state] += tupleExcess * share;
                            }
                        }
                    }

                    // Advance the odometer over each unit's total entry count.
                    int digit = 0;
                    while (digit < participating.Count)
                    {
                        branchPick[digit]++;
                        int unit = participating[digit];
                        var members = _layout.CombinationUnitStates[unit];
                        int totalEntries = 0;
                        for (int m = 0; m < members.Length; m++)
                        {
                            totalEntries += modeOutputs[members[m]].FailureConsequences.Count;
                        }
                        if (branchPick[digit] < totalEntries) break;
                        branchPick[digit] = 0;
                        digit++;
                    }
                    if (digit == participating.Count) break;
                }
            }
        }

        #endregion
    }
}
