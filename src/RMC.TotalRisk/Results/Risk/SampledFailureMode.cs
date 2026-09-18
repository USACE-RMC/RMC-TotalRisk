using System;
using System.Collections.Generic;
using Numerics;
using Numerics.Data;
using Numerics.Distributions;
using Numerics.Functions;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.Systems.Components;

namespace RMC.TotalRisk.Results.Risk
{
    /// <summary>
    /// One failure mode frozen for one Monte Carlo realization: the sampled transform curves,
    /// the sampled response distribution, and the weighted exposure branches of the paired
    /// failure and non-failure consequences.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Ported from v1.0 <c>SampledFailureMode</c> onto the v1.1 sampler contract: functions are
    /// sampled by realization index from their pre-allocated percentile matrices (never by PRNG
    /// draw), and the failure/non-failure consequence pair shares one knowledge percentile from
    /// the failure mode's coupling matrix — the v1.0 shared-draw coherence (the
    /// technical reference draws C_F and C_NF perfectly correlated within a mode so the
    /// incremental consequence stays consistent on one hazard scenario). Under the
    /// exposure-branch contract the
    /// consequences are held as weighted exposure branches: a mixture consequence contributes one
    /// branch per exposure state in every compute path, so each realization's loss exceedance
    /// curve carries the full day/night spread.
    /// </para>
    /// <para>
    /// Multi-stage acceptance (docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §7.9): every
    /// response stage is captured —
    /// per-stage transform slices, sampled fragilities, and branch polarities — and the system
    /// response probability is the polarity product ∏ᵢ (polarityᵢ = Fail ? pᵢ(h) : 1 − pᵢ(h)),
    /// each stage's fragility evaluated at its own stage-transformed signal. A single-stage
    /// Fail-polarity mode reproduces the plain single-CDF arithmetic bit-identically (the
    /// product's single factor multiplies 1.0 exactly).
    /// </para>
    /// <para>
    /// The multi-consequence axis: every consequence position of the mode's ordered
    /// <c>ConsequenceFunctions</c> list is sampled and computed — position k reads the coupling
    /// matrix at column k (the per-type shared draw pairing the failure and non-failure
    /// consequences of the same type), and type k's results record into the realization's
    /// primary curves (k = 0) or <c>AdditionalCurves[k − 1]</c>. Probability structure (SRP,
    /// pathway decomposition) is computed once and shared by every type; adaptive refinement is
    /// driven by the primary type, with the secondary types riding the same hazard nodes.
    /// Secondary types are evaluated only when recording or when the caller supplies a per-type
    /// output sink, so probe and warm-up evaluations pay the single-type cost.
    /// </para>
    /// </remarks>
    public class SampledFailureMode
    {
        #region Construction

        /// <summary>
        /// Samples a failure mode for one realization.
        /// </summary>
        /// <param name="failureMode">The failure mode to sample. Its samplers must be set up (via the owning component) unless the mean is requested.</param>
        /// <param name="nonFailureMode">
        /// The component's non-failure mode, whose primary consequence is paired-sampled at this
        /// mode's coupling percentile for the excess computation; null when the component has
        /// none (or when sampling the non-failure mode itself).
        /// </param>
        /// <param name="realizationIndex">The realization index, or −1 for the mean functions.</param>
        /// <exception cref="ArgumentNullException">Thrown when the failure mode is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when sampling by realization index before the samplers have been set up.
        /// </exception>
        public SampledFailureMode(FailureMode failureMode, FailureMode? nonFailureMode, int realizationIndex = -1)
        {
            if (failureMode == null) throw new ArgumentNullException(nameof(failureMode));

            Name = failureMode.ResponseFunction.Name;
            IsNonFailureMode = failureMode.IsNonFailureMode;
            _consequencePosition = failureMode.ResolvedConsequenceHazardPosition;
            _hazardBinding = failureMode.HazardBinding;
            _consequenceDimension = failureMode.ConsequenceHazardDimension;
            bool bivariateParent = failureMode.Parent?.HazardFunction is IBivariateHazardFunction;

            bool mean = realizationIndex < 0;

            // Every stage's transforms and response, sampled from their own content-seeded
            // matrices (the sampler walk covers every stage).
            // Transforms flatten into one chain array with per-stage offsets so the
            // polarity-product SRP and the consequence-input fold index without allocation.
            var stages = failureMode.ResponseStages;
            int stageCount = stages.Count;
            int totalTransforms = 0;
            for (int s = 0; s < stageCount; s++)
            {
                totalTransforms += stages[s].Transforms.Count;
            }
            _stageTransforms = new IUnivariateFunction[totalTransforms];
            _stageTransformOffsets = new int[stageCount + 1];
            _stageResponses = new IUnivariateDistribution[stageCount];
            _stagePolarities = new BranchPolarity[stageCount];
            _stageUsesExpandedBranches = new bool[stageCount];
            int cursor = 0;
            for (int s = 0; s < stageCount; s++)
            {
                _stageTransformOffsets[s] = cursor;
                var transforms = stages[s].Transforms;
                for (int i = 0; i < transforms.Count; i++)
                {
                    // A bivariate stage transform (legal only under a bivariate component
                    // hazard) evaluates as a deterministic two-way surface: its 1-argument
                    // sampling trio deliberately throws, so the capture is a fresh configured
                    // interpolator over the shared grid (the pinned per-realization thread
                    // discipline) held in a parallel slot.
                    if (transforms[i] is IBivariateTransformFunction bivariateTransform)
                    {
                        _stageBivariateTransforms ??= new Bilinear?[totalTransforms];
                        _stageBivariateTransforms[cursor] = bivariateTransform.CreateInterpolator();
                        _stageTransforms[cursor++] = null!;
                    }
                    else
                    {
                        _stageTransforms[cursor++] = mean ? transforms[i].SampleFunction() : transforms[i].SampleFunction(realizationIndex);
                    }
                }
                var response = stages[s].Response;
                _stagePolarities[s] = stages[s].BranchPolarity;
                ResponseBranchDescriptor? selectedBranch = stages[s].GetSelectedBranch();
                if (selectedBranch == null)
                {
                    // Joint mode: under a bivariate component hazard a bivariate response
                    // evaluates its probability surface at (x′, y′) through SRPAt; the stored
                    // univariate sample remains the preserved v1.0 weighted collapse
                    // (deterministic, no draws), keeping SRP(hazardLevel) total. Under a
                    // univariate hazard the collapse IS the mode's response — no surface.
                    if (bivariateParent && response is IBivariateResponseFunction jointResponse)
                    {
                        _jointResponseSurfaces ??= new Bilinear?[stageCount];
                        _jointResponseSurfaces[s] = jointResponse.CreateInterpolator();
                    }
                    _stageResponses[s] = mean
                        ? response.SampleFunction()
                        : response.SampleFunction(realizationIndex);
                    continue;
                }

                var branching = (IBranchingResponseFunction)response;
                ResponseBranchSample branchSample = mean
                    ? branching.SampleBranches()
                    : branching.SampleBranches(realizationIndex);
                int branchIndex = -1;
                for (int branch = 0; branch < branchSample.Branches.Count; branch++)
                {
                    if (branchSample.Branches[branch].Id == selectedBranch.Id)
                    {
                        branchIndex = branch;
                        break;
                    }
                }
                if (branchIndex < 0)
                    throw new InvalidOperationException(
                        $"Response branch '{selectedBranch.Id:D}' became stale while sampling failure mode '{Name}'.");
                _stageResponses[s] = new EmpiricalDistribution(
                    new List<double>(branchSample.Hazards),
                    new List<double>(branchSample.Probabilities[branchIndex]),
                    SortOrder.Ascending, SortOrder.None);
                _stageUsesExpandedBranches[s] = true;
            }
            _stageTransformOffsets[stageCount] = cursor;

            var trailing = failureMode.ResponseToConsequence;
            _responseToConsequence = new IUnivariateFunction[trailing.Count];
            for (int i = 0; i < trailing.Count; i++)
            {
                if (trailing[i] is IBivariateTransformFunction bivariateTrailing)
                {
                    _trailingBivariateTransforms ??= new Bilinear?[trailing.Count];
                    _trailingBivariateTransforms[i] = bivariateTrailing.CreateInterpolator();
                    _responseToConsequence[i] = null!;
                }
                else
                {
                    _responseToConsequence[i] = mean ? trailing[i].SampleFunction() : trailing[i].SampleFunction(realizationIndex);
                }
            }

            // The path's secondary-hazard chain (bivariate components only): ordinary
            // univariate transforms shaping the secondary signal, sampled from the
            // content-seeded streams the mode's sampler walk appended after the trailing
            // transforms. Its folded output y′ is the one secondary signal every
            // bivariate element on the mode's path consumes (the one-chain-per-path
            // identity rule).
            var secondaryChain = failureMode.SecondaryHazardToResponse;
            if (secondaryChain.Count > 0)
            {
                _secondaryTransforms = new IUnivariateFunction[secondaryChain.Count];
                for (int i = 0; i < secondaryChain.Count; i++)
                {
                    _secondaryTransforms[i] = mean ? secondaryChain[i].SampleFunction() : secondaryChain[i].SampleFunction(realizationIndex);
                }
            }

            // Every consequence position, branch-enumerated and coupled per type on one
            // knowledge percentile (position k reads coupling column k, so the
            // failure and non-failure consequences of one type share a draw while distinct types
            // draw independently — the coupling matrix carries K columns). A
            // consequence-free mode (reliability) carries the single zero-consequence branch so
            // failure probability still records.
            var consequences = failureMode.ConsequenceFunctions;
            int typeCount = Math.Max(1, consequences.Count);
            _failureBranchesByType = new IReadOnlyList<(double Weight, IUnivariateFunction Function)>[typeCount];
            for (int k = 0; k < typeCount; k++)
            {
                var consequence = k < consequences.Count ? consequences[k] : null;
                if (consequence == null)
                {
                    _failureBranchesByType[k] = ZeroBranch;
                }
                else if (consequence is IBivariateConsequenceFunction bivariateConsequence)
                {
                    // A bivariate consequence is a deterministic two-way surface: its exposure
                    // branch is the single unit-weight placeholder and the value comes from a
                    // fresh per-realization interpolator evaluated at (bound signal, y′). No
                    // coupling read — the surface carries no knowledge uncertainty.
                    _failureSurfacesByType ??= new Bilinear?[typeCount];
                    _failureSurfacesByType[k] = bivariateConsequence.CreateInterpolator();
                    _failureBranchesByType[k] = ZeroBranch;
                }
                else if (mean)
                {
                    _failureBranchesByType[k] = consequence.SampleExposureBranches();
                }
                else
                {
                    _failureBranchesByType[k] = consequence.SampleExposureBranches(failureMode.CouplingPercentile(realizationIndex, k));
                }
            }

            var pairedConsequences = nonFailureMode?.ConsequenceFunctions;
            if (pairedConsequences != null && pairedConsequences.Count > 0)
            {
                _nonFailureBranchesByType = new IReadOnlyList<(double Weight, IUnivariateFunction Function)>[typeCount];
                for (int k = 0; k < typeCount; k++)
                {
                    var paired = k < pairedConsequences.Count ? pairedConsequences[k] : null;
                    if (paired == null)
                    {
                        _nonFailureBranchesByType[k] = ZeroBranch;
                    }
                    else if (paired is IBivariateConsequenceFunction pairedBivariate)
                    {
                        _nonFailureSurfacesByType ??= new Bilinear?[typeCount];
                        _nonFailureSurfacesByType[k] = pairedBivariate.CreateInterpolator();
                        _nonFailureBranchesByType[k] = ZeroBranch;
                    }
                    else
                    {
                        _nonFailureBranchesByType[k] = mean
                            ? paired.SampleExposureBranches()
                            : paired.SampleExposureBranches(failureMode.CouplingPercentile(realizationIndex, k));
                    }
                }
            }

            // Compute-workspace scratch: branch counts are fixed for the life of the
            // sampled mode, so every per-evaluation buffer is sized exactly once here and reused
            // for the realization's thousands of integrand evaluations — a sampled mode is
            // realization-owned, never shared across threads.
            _scratchOutputs = new ComponentRiskOutput[typeCount];
            _scratchOwnWeights = new double[typeCount][];
            _scratchOwnValues = new double[typeCount][];
            for (int k = 0; k < typeCount; k++)
            {
                _scratchOutputs[k] = new ComponentRiskOutput();
                int ownCount = _failureBranchesByType[k].Count;
                _scratchOwnWeights[k] = new double[ownCount];
                _scratchOwnValues[k] = new double[ownCount];
            }
            if (_nonFailureBranchesByType != null)
            {
                _scratchNonFailWeights = new double[typeCount][];
                _scratchNonFailValues = new double[typeCount][];
                for (int k = 0; k < typeCount; k++)
                {
                    int pairedCount = _nonFailureBranchesByType[k].Count;
                    _scratchNonFailWeights[k] = new double[pairedCount];
                    _scratchNonFailValues[k] = new double[pairedCount];
                }
            }

            // The bin-invariance classification (bivariate components): a mode whose response
            // probability is a pure primary-axis function is SRP-bin-invariant (the multi-unit
            // competing prerequisite), and one whose entire machinery — probabilities and
            // consequences — ignores the secondary signal is bin-invariant, evaluated once per
            // hazard level and reused across the conditional bins. Every univariate mode is
            // trivially both.
            IsSrpBinInvariant = _hazardBinding == HazardDimension.Primary
                && _jointResponseSurfaces == null
                && _stageBivariateTransforms == null;
            IsBinInvariant = IsSrpBinInvariant
                && _consequenceDimension == HazardDimension.Primary
                && _secondaryTransforms == null
                && _trailingBivariateTransforms == null
                && _failureSurfacesByType == null
                && _nonFailureSurfacesByType == null;
        }

        #endregion

        #region Members

        /// <summary>
        /// The single zero-consequence branch a consequence-free mode carries (reliability mode):
        /// unit weight, null function, evaluated as zero.
        /// </summary>
        private static readonly IReadOnlyList<(double Weight, IUnivariateFunction Function)> ZeroBranch =
            new (double Weight, IUnivariateFunction Function)[] { (1d, null!) };

        /// <summary>
        /// The sampled stage transform curves, flattened across all stages in chain order —
        /// stage s owns the slice [<see cref="_stageTransformOffsets"/>[s],
        /// <see cref="_stageTransformOffsets"/>[s + 1]).
        /// </summary>
        private readonly IUnivariateFunction[] _stageTransforms;

        /// <summary>
        /// The per-stage offsets into <see cref="_stageTransforms"/> (length = stage count + 1).
        /// </summary>
        private readonly int[] _stageTransformOffsets;

        /// <summary>
        /// The sampled per-stage response distributions (fragilities): stage s's
        /// P[F|transformed hazard] = CDF.
        /// </summary>
        private readonly IUnivariateDistribution[] _stageResponses;

        /// <summary>
        /// The per-stage branch polarities: Fail contributes p(h), Non-Fail contributes
        /// 1 − p(h) to the polarity-product response probability.
        /// </summary>
        private readonly BranchPolarity[] _stagePolarities;

        /// <summary>
        /// Whether each stage uses an exact expanded branch probability rather than aggregate
        /// Fail/Non-Fail polarity algebra.
        /// </summary>
        private readonly bool[] _stageUsesExpandedBranches;

        /// <summary>
        /// The sampled trailing transform curves applied from the bound consequence position.
        /// </summary>
        private readonly IUnivariateFunction[] _responseToConsequence;

        /// <summary>
        /// The per-realization interpolators of the bivariate stage transforms, index-aligned
        /// with <see cref="_stageTransforms"/> (whose slot is then null); null when the mode has
        /// none — every univariate mode.
        /// </summary>
        private readonly Bilinear?[]? _stageBivariateTransforms;

        /// <summary>
        /// The per-realization interpolators of the bivariate trailing transforms, index-aligned
        /// with <see cref="_responseToConsequence"/> (whose slot is then null); null when the
        /// mode has none.
        /// </summary>
        private readonly Bilinear?[]? _trailingBivariateTransforms;

        /// <summary>
        /// The per-stage joint-mode response surfaces (a bivariate response under a bivariate
        /// component hazard): the per-realization raw interpolator whose back-transformed value
        /// clamps to [0, 1] inside <see cref="SRPAt"/>. Null for every collapse-mode and
        /// univariate mode — the stage's stored sample is then the response itself.
        /// </summary>
        private readonly Bilinear?[]? _jointResponseSurfaces;

        /// <summary>
        /// The sampled secondary-hazard chain — the path's univariate transforms shaping the
        /// secondary signal, whose folded output y′ is the one secondary signal every bivariate
        /// element on the mode's path consumes. Null (not empty) for every univariate mode.
        /// </summary>
        private readonly IUnivariateFunction[]? _secondaryTransforms;

        /// <summary>
        /// The per-type, per-realization interpolators of bivariate failure consequences,
        /// index-aligned with <see cref="_failureBranchesByType"/> (whose entry is then the
        /// single unit-weight placeholder branch); null when no type is bivariate.
        /// </summary>
        private readonly Bilinear?[]? _failureSurfacesByType;

        /// <summary>
        /// The per-type, per-realization interpolators of bivariate paired non-failure
        /// consequences; null when no paired type is bivariate.
        /// </summary>
        private readonly Bilinear?[]? _nonFailureSurfacesByType;

        /// <summary>
        /// Which hazard dimension the stage chain consumes as its signal origin (the projection
        /// stamp; Primary for every univariate mode).
        /// </summary>
        private readonly HazardDimension _hazardBinding;

        /// <summary>
        /// Which hazard dimension the consequence binding consumes at its bound position (the
        /// projection stamp; Primary for every univariate mode).
        /// </summary>
        private readonly HazardDimension _consequenceDimension;

        /// <summary>
        /// The paired excess partner of the active binned evaluation, captured by
        /// <see cref="BeginBinnedEvaluation"/>.
        /// </summary>
        private SampledFailureMode? _binPaired;

        /// <summary>
        /// Whether the active binned evaluation records this mode's own streams (the univariate
        /// record rule: recording, not the non-failure mode, not a claimed state).
        /// </summary>
        private bool _binRecord;

        /// <summary>
        /// Whether the active binned evaluation reuses one full-weight bin-0 computation — the
        /// mode and its excess partner are both bin-invariant, so the conditional evaluation
        /// point never moves.
        /// </summary>
        private bool _binInvariant;

        /// <summary>
        /// Whether the invariant bin-0 computation has run for the active evaluation (the reuse
        /// latch of <see cref="ComputeRiskBinned"/>).
        /// </summary>
        private bool _binEvaluated;

        /// <summary>
        /// The consequence-type count of the active binned evaluation (clamped to this mode's
        /// own axis, mirroring the univariate computed-types rule).
        /// </summary>
        private int _binComputedTypes;

        /// <summary>
        /// The per-type staged entry lists of the active recording binned evaluation, adopted by
        /// the committed risk points; null on non-recording evaluations (which therefore
        /// allocate nothing — the univariate recording contract).
        /// </summary>
        private BinnedCurveStaging[]? _binStaging;

        /// <summary>
        /// The weighted exposure branches of each failure consequence type at this realization's
        /// per-type shared knowledge percentiles (entry k is consequence-type position k).
        /// </summary>
        private readonly IReadOnlyList<(double Weight, IUnivariateFunction Function)>[] _failureBranchesByType;

        /// <summary>
        /// The weighted exposure branches of each paired non-failure consequence type, sampled at
        /// THIS mode's per-type coupling percentiles (the per-type shared draw); null when no
        /// non-failure mode pairs.
        /// </summary>
        private readonly IReadOnlyList<(double Weight, IUnivariateFunction Function)>[]? _nonFailureBranchesByType;

        /// <summary>
        /// The resolved consequence hazard position: 0 binds the raw hazard, k the signal after
        /// the k-th stage transform.
        /// </summary>
        private readonly int _consequencePosition;

        /// <summary>
        /// The reusable per-type output scratch — handed out by <see cref="ComputeRisk"/> and
        /// valid until the next evaluation on this mode (reused compute workspace — no
        /// per-evaluation allocation).
        /// </summary>
        private readonly ComponentRiskOutput[] _scratchOutputs;

        /// <summary>
        /// The reusable per-type weight buffers behind <see cref="EvaluateConsequenceBranches(double, int, RiskComputeFlags, out double[], out double[])"/>.
        /// </summary>
        private readonly double[][] _scratchOwnWeights;

        /// <summary>
        /// The reusable per-type value buffers behind <see cref="EvaluateConsequenceBranches(double, int, RiskComputeFlags, out double[], out double[])"/>.
        /// </summary>
        private readonly double[][] _scratchOwnValues;

        /// <summary>
        /// The reusable per-type paired non-failure weight buffers; null when no non-failure
        /// mode pairs.
        /// </summary>
        private readonly double[][]? _scratchNonFailWeights;

        /// <summary>
        /// The reusable per-type paired non-failure value buffers; null when no non-failure
        /// mode pairs.
        /// </summary>
        private readonly double[][]? _scratchNonFailValues;

        /// <summary>
        /// The failure mode's display name (the response function's name — v1.0 behavior).
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Determines whether this is the component's non-failure mode.
        /// </summary>
        public bool IsNonFailureMode { get; }

        /// <summary>
        /// Suppresses this mode's own Fail/Excess recording inside <see cref="ComputeRisk"/> —
        /// set by the sampled component on claimed non-failure states (§7.9.2): a
        /// Non-Fail-final end state is not a failure, so its entries are recorded by the
        /// component's complement decomposition (into the NonFail streams) rather than here.
        /// </summary>
        internal bool SuppressModeRecording { get; set; }

        /// <summary>
        /// Determines whether the mode's response probability is a pure primary-axis function —
        /// a Primary hazard binding with no joint response surface and no bivariate stage
        /// transform. The multi-unit competing-risks prerequisite on a bivariate component
        /// (the cumulative-incidence pre-processing marginals are primary-axis curves); true for
        /// every univariate mode.
        /// </summary>
        internal bool IsSrpBinInvariant { get; }

        /// <summary>
        /// Determines whether the mode's entire machinery — response probability and every
        /// consequence path — ignores the secondary hazard signal, so a conditional-bin sweep
        /// evaluates it once per hazard level and reuses the result across bins. True for every
        /// univariate mode.
        /// </summary>
        internal bool IsBinInvariant { get; }

        /// <summary>
        /// The number of weighted exposure branches the primary failure consequence carries.
        /// </summary>
        public int FailureBranchCount => _failureBranchesByType[0].Count;

        /// <summary>
        /// The number of consequence types this mode carries (the declared-axis length; one for a
        /// consequence-free reliability mode).
        /// </summary>
        public int ConsequenceTypeCount => _failureBranchesByType.Length;

        /// <summary>
        /// The number of exposure branches this mode's consequence carries at one type position
        /// (the last position's count when the index runs past the mode's axis — a defensive
        /// clamp; validation aligns the counts).
        /// </summary>
        /// <param name="typeIndex">The consequence-type position.</param>
        /// <returns>The branch count.</returns>
        internal int BranchCount(int typeIndex)
        {
            return _failureBranchesByType[Math.Min(typeIndex, _failureBranchesByType.Length - 1)].Count;
        }

        #endregion

        #region Methods

        /// <summary>
        /// The end state's weight (system response probability) at a hazard level: the polarity
        /// product over the stages — each stage's sampled fragility CDF, evaluated at that
        /// stage's transformed signal and clamped to [0, 1], contributes p under a Fail polarity
        /// and 1 − p under Non-Fail. A single-stage Fail mode reproduces the
        /// plain single-CDF arithmetic bit-identically.
        /// </summary>
        /// <param name="hazardLevel">The hazard level.</param>
        /// <returns>The response probability.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the mode carries a bivariate stage transform — the chain then needs the
        /// secondary signal, which only <see cref="SRPAt"/> supplies. A Secondary-bound mode's
        /// chain is origin-agnostic (pass the secondary signal), and a joint-mode bivariate
        /// response returns its stored v1.0 weighted collapse here.
        /// </exception>
        public double SRP(double hazardLevel)
        {
            if (_stageBivariateTransforms != null)
                throw new InvalidOperationException("The failure mode's stage chain carries a bivariate transform, which needs the secondary hazard signal; evaluate SRPAt(x, y) instead.");
            double signal = hazardLevel;
            double weight = 1d;
            for (int s = 0; s < _stageResponses.Length; s++)
            {
                for (int i = _stageTransformOffsets[s]; i < _stageTransformOffsets[s + 1]; i++)
                {
                    signal = _stageTransforms[i].Function(signal);
                }
                double p = Tools.Clamp(_stageResponses[s].CDF(signal), 0d, 1d);
                weight *= _stageUsesExpandedBranches[s]
                    ? p
                    : _stagePolarities[s] == BranchPolarity.Fail ? p : 1d - p;
            }
            return Tools.Clamp(weight, 0d, 1d);
        }

        /// <summary>
        /// The hazard level at which the response probability is reached — the inverse of
        /// <see cref="SRP"/> through the transform chain. Exact for a single-stage Fail-polarity
        /// mode only: a multi-stage polarity product is not monotone in the hazard, so no closed
        /// inverse exists (no engine path consumes this member — numeric
        /// inversion can be added if a consumer ever needs it).
        /// </summary>
        /// <param name="probability">The response probability.</param>
        /// <returns>The hazard level.</returns>
        /// <exception cref="NotSupportedException">
        /// Thrown for a multi-stage or Non-Fail-polarity mode, whose polarity product has no
        /// monotone inverse.
        /// </exception>
        public double InverseSRP(double probability)
        {
            if (_stageResponses.Length != 1 || _stagePolarities[0] != BranchPolarity.Fail
                || _stageUsesExpandedBranches[0] || _hazardBinding != HazardDimension.Primary
                || _jointResponseSurfaces != null || _stageBivariateTransforms != null)
            {
                throw new NotSupportedException(
                    "InverseSRP is exact only for a single-stage aggregate Fail-polarity Primary-bound univariate mode; expanded branches, cascade polarity products, secondary bindings, and joint bivariate evaluations have no monotone primary-axis inverse.");
            }

            double signal = _stageResponses[0].InverseCDF(probability);
            for (int i = _stageTransformOffsets[1] - 1; i >= 0; i--)
            {
                signal = _stageTransforms[i].InverseFunction(signal);
            }
            return signal;
        }

        /// <summary>
        /// The hazard signal feeding this mode's consequences: the chain signal at the bound
        /// position (0 = the raw hazard, k = after the k-th stage transform, counted across all
        /// stages), folded through the trailing response-to-consequence transforms. The bound
        /// counts across all stages (an earlier fold truncated it at stage 0's transform count).
        /// </summary>
        /// <param name="hazardLevel">The raw hazard level.</param>
        /// <returns>The consequence input signal.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the routed segments carry a bivariate transform or the consequence
        /// dimension is Secondary — the signal then depends on the secondary hazard, which only
        /// <see cref="ConsequenceInputAt"/> supplies.
        /// </exception>
        public double ConsequenceInput(double hazardLevel)
        {
            if (_stageBivariateTransforms != null || _trailingBivariateTransforms != null
                || _consequenceDimension == HazardDimension.Secondary)
                throw new InvalidOperationException("The failure mode's consequence input depends on the secondary hazard signal; evaluate ConsequenceInputAt(x, y) instead.");
            double signal = hazardLevel;
            int bound = Math.Min(_consequencePosition, _stageTransforms.Length);
            for (int i = 0; i < bound; i++)
            {
                signal = _stageTransforms[i].Function(signal);
            }
            for (int i = 0; i < _responseToConsequence.Length; i++)
            {
                signal = _responseToConsequence[i].Function(signal);
            }
            return signal;
        }

        /// <summary>
        /// Evaluates this mode's own primary-consequence exposure branches at a hazard level —
        /// the component uses this on the non-failure mode to source the Background and NonFail
        /// entries from the mode's own sample (its own coupling draw).
        /// </summary>
        /// <param name="hazardLevel">The raw hazard level.</param>
        /// <param name="flags">The realization's computational-warning flags (negative values clamp with the matching flag).</param>
        /// <param name="weights">Receives the branch weights.</param>
        /// <param name="values">Receives the branch consequence values, clamped at zero.</param>
        /// <exception cref="ArgumentNullException">Thrown when the flags sink is null.</exception>
        public void EvaluateConsequenceBranches(double hazardLevel, RiskComputeFlags flags, out double[] weights, out double[] values)
        {
            EvaluateConsequenceBranches(hazardLevel, 0, flags, out weights, out values);
        }

        /// <summary>
        /// Evaluates this mode's own exposure branches at a hazard level for one consequence
        /// type (position k of the declared axis). The consequence input signal is
        /// type-independent — the chain and bound position are shared — so only the branch set
        /// changes with the type.
        /// </summary>
        /// <param name="hazardLevel">The raw hazard level.</param>
        /// <param name="typeIndex">The consequence-type position (0 is the primary).</param>
        /// <param name="flags">The realization's computational-warning flags (negative values clamp with the matching flag).</param>
        /// <param name="weights">Receives the branch weights — a reused per-type buffer, valid until the next evaluation at this type on this mode (never retain it).</param>
        /// <param name="values">Receives the branch consequence values, clamped at zero — the same reuse contract.</param>
        /// <exception cref="ArgumentNullException">Thrown when the flags sink is null.</exception>
        public void EvaluateConsequenceBranches(double hazardLevel, int typeIndex, RiskComputeFlags flags, out double[] weights, out double[] values)
        {
            if (flags == null) throw new ArgumentNullException(nameof(flags));
            if (_failureSurfacesByType != null)
                throw new InvalidOperationException("The failure mode carries a bivariate consequence surface, which needs the secondary hazard signal; evaluate EvaluateConsequenceBranchesAt(x, y, ...) instead.");

            double signal = ConsequenceInput(hazardLevel);
            var branches = _failureBranchesByType[typeIndex];
            int count = branches.Count;
            weights = _scratchOwnWeights[typeIndex];
            values = _scratchOwnValues[typeIndex];
            for (int i = 0; i < count; i++)
            {
                weights[i] = branches[i].Weight;
                double value = branches[i].Function?.Function(signal) ?? 0d;
                if (value < 0d)
                {
                    if (IsNonFailureMode)
                    {
                        flags.HasNegativeNonFailureConsequence = true;
                    }
                    else
                    {
                        flags.HasNegativeFailureConsequence = true;
                    }
                    value = 0d;
                }
                values[i] = value;
            }
        }

        /// <summary>
        /// Computes this mode's risk at one hazard evaluation point: the response probability,
        /// the branch-enumerated failure and excess consequences per consequence type, and the
        /// recorded risk-point entries.
        /// </summary>
        /// <param name="probability">The hazard non-exceedance probability at the evaluation point.</param>
        /// <param name="hazardLevel">The hazard level.</param>
        /// <param name="nonFailureMode">
        /// The component's sampled non-failure mode — its transform chain locates the paired
        /// non-failure consequence input; null when the component has none.
        /// </param>
        /// <param name="flags">The realization's computational-warning flags.</param>
        /// <param name="realization">The failure mode's realization sink.</param>
        /// <param name="recordOutput">True to record risk-point entries on the realization curves.</param>
        /// <param name="typeOutputs">
        /// The optional per-type output sink, length at least <see cref="ConsequenceTypeCount"/>
        /// (entry k receives type k's output; entry 0 is the returned primary). Secondary types
        /// are computed only when recording or when this sink is supplied — probe evaluations
        /// pay the single-type cost.
        /// </param>
        /// <param name="recordedHazard">
        /// The hazard coordinate recorded risk points carry — the component's profile-axis
        /// signal when a profile hazard element is selected; NaN (the default)
        /// records the raw <paramref name="hazardLevel"/>. Evaluation always uses the raw level;
        /// this parameter labels the recorded points only.
        /// </param>
        /// <param name="hazardExceedanceProbability">
        /// The driving hazard's annual exceedance probability at the evaluation — the system
        /// response profile's X coordinate; NaN (the default) skips that profile.
        /// </param>
        /// <returns>
        /// The mode's primary-type risk output at the evaluation point. The returned output
        /// (and every sink entry) is workspace-backed scratch, valid until the next evaluation
        /// on this mode — consume or copy it before evaluating again (reused compute
        /// workspace; the engine's call sites consume within the evaluation).
        /// </returns>
        /// <exception cref="ArgumentNullException">Thrown when the flags or realization sink is null.</exception>
        /// <remarks>
        /// The v1.0 clamping rules are preserved: negative consequences clamp to zero with the
        /// matching warning flag (failure and excess flags raise only when the failure
        /// probability is positive — a negative consequence on an impossible event is not
        /// actionable). Excess is computed per failure/non-failure branch pair within each type,
        /// <c>max(0, cF_i − cNF_j)</c>, so the recorded Excess entries carry the exact pair
        /// distribution; the output's excess LIST entries use the mean non-failure consequence
        /// (the documented <see cref="ComponentRiskOutput"/> interim), while the scalar
        /// mean excess is pair-exact. Type k's points record into the realization's primary
        /// curves (k = 0) or <c>AdditionalCurves[k − 1]</c>; the analysis validation gate
        /// guarantees the realization carries a slot per declared type.
        /// </remarks>
        public ComponentRiskOutput ComputeRisk(double probability, double hazardLevel, SampledFailureMode? nonFailureMode,
            RiskComputeFlags flags, FailureModeRealization realization, bool recordOutput = false,
            ComponentRiskOutput[]? typeOutputs = null, double recordedHazard = double.NaN,
            double hazardExceedanceProbability = double.NaN)
        {
            if (flags == null) throw new ArgumentNullException(nameof(flags));
            if (realization == null) throw new ArgumentNullException(nameof(realization));

            double probabilityOfFailure = Tools.Clamp(SRP(hazardLevel), 0d, 1d);
            bool record = recordOutput && !IsNonFailureMode && !SuppressModeRecording;
            double recordedLevel = double.IsNaN(recordedHazard) ? hazardLevel : recordedHazard;

            // Secondary types ride along only when their results are consumed: the recorded
            // curves or the caller's per-type sink. Probes and warm-up evaluations stay
            // single-type.
            int computedTypes = record || typeOutputs != null ? _failureBranchesByType.Length : 1;

            // Type-independent signals: the consequence input chain and the paired non-failure
            // mode's chain are shared by every consequence type.
            double consequenceSignal = ConsequenceInput(hazardLevel);
            bool hasPairedNonFailure = _nonFailureBranchesByType != null && nonFailureMode != null;
            double nonFailSignal = hasPairedNonFailure ? nonFailureMode!.ConsequenceInput(hazardLevel) : 0d;

            ComponentRiskOutput primary = null!;
            for (int k = 0; k < computedTypes; k++)
            {
                var output = ComputeTypeRisk(k, probability, recordedLevel, probabilityOfFailure,
                    consequenceSignal, nonFailSignal, hasPairedNonFailure, flags, realization, record, hazardExceedanceProbability);
                if (k == 0) primary = output;
                if (typeOutputs != null) typeOutputs[k] = output;
            }
            return primary;
        }

        /// <summary>
        /// Computes one consequence type's risk at one hazard evaluation point — the per-type
        /// kernel behind <see cref="ComputeRisk"/>: the paired non-failure branches at the
        /// type's shared draw, the failure branches, the exact excess pairs, and the recorded
        /// entries on the type's curve set.
        /// </summary>
        /// <param name="typeIndex">The consequence-type position (0 is the primary).</param>
        /// <param name="probability">The hazard non-exceedance probability at the evaluation point.</param>
        /// <param name="recordedLevel">The hazard coordinate recorded risk points carry (the profile-axis signal when a profile is selected; evaluation signals arrive precomputed).</param>
        /// <param name="probabilityOfFailure">The mode's response probability at the hazard level (type-independent).</param>
        /// <param name="consequenceSignal">This mode's consequence input signal (type-independent).</param>
        /// <param name="nonFailSignal">The paired non-failure mode's consequence input signal.</param>
        /// <param name="hasPairedNonFailure">Whether a paired non-failure consequence exists.</param>
        /// <param name="flags">The realization's computational-warning flags.</param>
        /// <param name="realization">The failure mode's realization sink.</param>
        /// <param name="record">True to record risk-point entries on the type's curves.</param>
        /// <param name="hazardExceedanceProbability">The driving hazard's exceedance probability (the system response profile's X coordinate; NaN skips it).</param>
        /// <returns>The type's risk output at the evaluation point.</returns>
        private ComponentRiskOutput ComputeTypeRisk(int typeIndex, double probability, double recordedLevel,
            double probabilityOfFailure, double consequenceSignal, double nonFailSignal, bool hasPairedNonFailure,
            RiskComputeFlags flags, FailureModeRealization realization, bool record,
            double hazardExceedanceProbability = double.NaN)
        {
            var output = _scratchOutputs[typeIndex];
            output.Reset();
            var failureBranches = _failureBranchesByType[typeIndex];

            // Paired non-failure consequence branches, evaluated at the NON-FAILURE mode's own
            // consequence input signal (its transform chain), with THIS mode's paired sample at
            // this type's coupling column.
            int nonFailCount = 1;
            double meanNonFail = 0d;
            double[] nonFailWeights = _singleUnitWeight;
            double[] nonFailValues = _singleZeroValue;
            if (hasPairedNonFailure)
            {
                var nonFailureBranches = _nonFailureBranchesByType![typeIndex];
                nonFailCount = nonFailureBranches.Count;
                nonFailWeights = _scratchNonFailWeights![typeIndex];
                nonFailValues = _scratchNonFailValues![typeIndex];
                for (int j = 0; j < nonFailCount; j++)
                {
                    nonFailWeights[j] = nonFailureBranches[j].Weight;
                    double value = nonFailureBranches[j].Function?.Function(nonFailSignal) ?? 0d;
                    if (value < 0d)
                    {
                        flags.HasNegativeNonFailureConsequence = true;
                        value = 0d;
                    }
                    nonFailValues[j] = value;
                    meanNonFail += nonFailWeights[j] * value;
                }
            }

            // Failure consequence branches at the bound consequence input signal. The common
            // one-by-one case records inline below and therefore needs no temporary entry lists.
            bool inlineRecord = record && failureBranches.Count == 1 && nonFailCount == 1;
            var excessProbabilities = record && !inlineRecord ? new List<double>(failureBranches.Count * nonFailCount) : null;
            var excessValues = record && !inlineRecord ? new List<double>(failureBranches.Count * nonFailCount) : null;
            double inlineExcessProbability = 0d;
            double inlineExcessValue = 0d;
            double meanFailure = 0d;
            double meanExcess = 0d;
            for (int i = 0; i < failureBranches.Count; i++)
            {
                double weight = failureBranches[i].Weight;
                double failureValue = failureBranches[i].Function?.Function(consequenceSignal) ?? 0d;
                if (failureValue < 0d)
                {
                    if (probabilityOfFailure > 0d) flags.HasNegativeFailureConsequence = true;
                    failureValue = 0d;
                }
                meanFailure += weight * failureValue;

                // Excess per failure/non-failure branch pair — the exact pair distribution.
                double pairedExcess = 0d;
                for (int j = 0; j < nonFailCount; j++)
                {
                    double excess = failureValue - nonFailValues[j];
                    if (excess < 0d)
                    {
                        if (probabilityOfFailure > 0d) flags.HasNegativeExcessConsequence = true;
                        excess = 0d;
                    }
                    pairedExcess += nonFailWeights[j] * excess;
                    if (record)
                    {
                        double entryProbability = Tools.Clamp(probabilityOfFailure * weight * nonFailWeights[j], 0d, 1d);
                        if (inlineRecord)
                        {
                            inlineExcessProbability = entryProbability;
                            inlineExcessValue = excess;
                        }
                        else
                        {
                            excessProbabilities!.Add(entryProbability);
                            excessValues!.Add(excess);
                        }
                    }
                }
                meanExcess += weight * pairedExcess;

                output.ResponseProbabilities.Add(Tools.Clamp(probabilityOfFailure * weight, 0d, 1d));
                output.FailureConsequences.Add(failureValue);
                output.ExcessConsequences.Add(Math.Max(0d, failureValue - meanNonFail));
            }

            // Record the complete mode-level decomposition: failure and excess branches, the
            // conditional background distribution, the non-failure complement, and their
            // collectively exhaustive Total union.
            if (record)
            {
                var target = typeIndex == 0 ? realization.Curves : realization.AdditionalCurves[typeIndex - 1];
                double probabilityOfNonFailure = Tools.Clamp(1d - probabilityOfFailure, 0d, 1d);
                if (inlineRecord)
                {
                    double failProbability = output.ResponseProbabilities[0];
                    double failValue = output.FailureConsequences[0];
                    double backgroundProbability = Tools.Clamp(nonFailWeights[0], 0d, 1d);
                    double backgroundValue = nonFailValues[0];
                    double nonFailProbability = Tools.Clamp(probabilityOfNonFailure * nonFailWeights[0], 0d, 1d);
                    target.Background.AddRiskPoint(recordedLevel, probability, backgroundProbability, backgroundValue);
                    target.NonFail.AddRiskPoint(recordedLevel, probability, nonFailProbability, backgroundValue);
                    target.Total.AddTwoEntryRiskPoint(recordedLevel, probability,
                        failProbability, failValue, nonFailProbability, backgroundValue);
                    target.Fail.AddRiskPoint(recordedLevel, probability, failProbability, failValue, hazardExceedanceProbability);
                    target.Excess.AddRiskPoint(recordedLevel, probability, inlineExcessProbability, inlineExcessValue);
                }
                else
                {
                    var failProbabilities = new List<double>(failureBranches.Count);
                    var failValues = new List<double>(failureBranches.Count);
                    for (int i = 0; i < failureBranches.Count; i++)
                    {
                        failProbabilities.Add(output.ResponseProbabilities[i]);
                        failValues.Add(output.FailureConsequences[i]);
                    }

                    var backgroundProbabilities = new List<double>(nonFailCount);
                    var backgroundValues = new List<double>(nonFailCount);
                    var nonFailProbabilities = new List<double>(nonFailCount);
                    var totalProbabilities = new List<double>(failureBranches.Count + nonFailCount);
                    var totalValues = new List<double>(failureBranches.Count + nonFailCount);
                    totalProbabilities.AddRange(failProbabilities);
                    totalValues.AddRange(failValues);
                    for (int j = 0; j < nonFailCount; j++)
                    {
                        backgroundProbabilities.Add(Tools.Clamp(nonFailWeights[j], 0d, 1d));
                        backgroundValues.Add(nonFailValues[j]);
                        nonFailProbabilities.Add(Tools.Clamp(probabilityOfNonFailure * nonFailWeights[j], 0d, 1d));
                        totalProbabilities.Add(Tools.Clamp(probabilityOfNonFailure * nonFailWeights[j], 0d, 1d));
                        totalValues.Add(nonFailValues[j]);
                    }
                    target.Background.AddRiskPoint(recordedLevel, probability, backgroundProbabilities, backgroundValues);
                    target.NonFail.AddRiskPoint(recordedLevel, probability, nonFailProbabilities, new List<double>(backgroundValues));
                    target.Total.AddRiskPoint(recordedLevel, probability, totalProbabilities, totalValues);
                    target.Fail.AddRiskPoint(recordedLevel, probability, failProbabilities, failValues, hazardExceedanceProbability);
                    target.Excess.AddRiskPoint(recordedLevel, probability, excessProbabilities!, excessValues!);
                }
            }

            output.ProbabilityOfFailure = probabilityOfFailure;
            output.ProbabilityOfNonFailure = Tools.Clamp(1d - probabilityOfFailure, 0d, 1d);
            output.NonFailureConsequences = meanNonFail;
            output.MeanFailureConsequences = meanFailure;
            output.MeanExcessConsequences = meanExcess;
            return output;
        }

        /// <summary>
        /// Folds the raw secondary hazard signal through the mode's sampled secondary-hazard
        /// chain — the y′ every bivariate element on the mode's path consumes (the
        /// one-chain-per-path identity rule). The identity when the mode carries no chain.
        /// </summary>
        /// <param name="secondaryLevel">The raw secondary hazard level y.</param>
        /// <returns>The folded secondary signal y′.</returns>
        internal double FoldSecondary(double secondaryLevel)
        {
            var chain = _secondaryTransforms;
            if (chain == null) return secondaryLevel;
            double signal = secondaryLevel;
            for (int i = 0; i < chain.Length; i++)
            {
                signal = chain[i].Function(signal);
            }
            return signal;
        }

        /// <summary>
        /// The end state's weight at one joint hazard evaluation point (x, y): the polarity
        /// product over the stages with the signal origin selected by the hazard binding, each
        /// bivariate stage transform evaluated at (signal, y′), and a joint-mode bivariate
        /// response evaluated as its probability surface at (x′, y′) — clamped to [0, 1] after
        /// the surface's back-transform. Reduces exactly to <see cref="SRP"/> for a mode with no
        /// bivariate machinery.
        /// </summary>
        /// <param name="hazardLevel">The primary hazard level x.</param>
        /// <param name="secondaryLevel">The raw secondary hazard level y.</param>
        /// <returns>The response probability.</returns>
        public double SRPAt(double hazardLevel, double secondaryLevel)
        {
            return SRPAtCore(hazardLevel, secondaryLevel, FoldSecondary(secondaryLevel));
        }

        /// <summary>
        /// The <see cref="SRPAt"/> kernel over a precomputed folded secondary signal — the
        /// per-bin path computes y′ once and shares it with the consequence routing.
        /// </summary>
        /// <param name="x">The primary hazard level.</param>
        /// <param name="y">The raw secondary hazard level.</param>
        /// <param name="yPrime">The chain-folded secondary signal.</param>
        /// <returns>The response probability.</returns>
        private double SRPAtCore(double x, double y, double yPrime)
        {
            double signal = _hazardBinding == HazardDimension.Secondary ? y : x;
            double weight = 1d;
            for (int s = 0; s < _stageResponses.Length; s++)
            {
                for (int i = _stageTransformOffsets[s]; i < _stageTransformOffsets[s + 1]; i++)
                {
                    var surface = _stageBivariateTransforms?[i];
                    signal = surface != null ? surface.Interpolate(signal, yPrime) : _stageTransforms[i].Function(signal);
                }
                var joint = _jointResponseSurfaces?[s];
                double p = joint != null
                    ? Tools.Clamp(joint.Interpolate(signal, yPrime), 0d, 1d)
                    : Tools.Clamp(_stageResponses[s].CDF(signal), 0d, 1d);
                weight *= _stageUsesExpandedBranches[s]
                    ? p
                    : _stagePolarities[s] == BranchPolarity.Fail ? p : 1d - p;
            }
            return Tools.Clamp(weight, 0d, 1d);
        }

        /// <summary>
        /// The hazard signal feeding this mode's consequences at one joint evaluation point
        /// (x, y): under the Primary consequence dimension the binding-origin signal folded
        /// through the bound number of stage transforms (bivariate ones at (signal, y′)); under
        /// the Secondary dimension the raw secondary signal folded through the bound number of
        /// secondary-chain transforms; the trailing transforms fold after either. Reduces
        /// exactly to <see cref="ConsequenceInput"/> for a mode with no bivariate machinery.
        /// </summary>
        /// <param name="hazardLevel">The primary hazard level x.</param>
        /// <param name="secondaryLevel">The raw secondary hazard level y.</param>
        /// <returns>The consequence input signal.</returns>
        public double ConsequenceInputAt(double hazardLevel, double secondaryLevel)
        {
            return ConsequenceInputAtCore(hazardLevel, secondaryLevel, FoldSecondary(secondaryLevel));
        }

        /// <summary>
        /// The <see cref="ConsequenceInputAt"/> kernel over a precomputed folded secondary
        /// signal.
        /// </summary>
        /// <param name="x">The primary hazard level.</param>
        /// <param name="y">The raw secondary hazard level.</param>
        /// <param name="yPrime">The chain-folded secondary signal.</param>
        /// <returns>The consequence input signal.</returns>
        private double ConsequenceInputAtCore(double x, double y, double yPrime)
        {
            double signal;
            if (_consequenceDimension == HazardDimension.Secondary)
            {
                signal = y;
                var chain = _secondaryTransforms;
                int chainBound = Math.Min(_consequencePosition, chain?.Length ?? 0);
                for (int i = 0; i < chainBound; i++)
                {
                    signal = chain![i].Function(signal);
                }
            }
            else
            {
                signal = _hazardBinding == HazardDimension.Secondary ? y : x;
                int bound = Math.Min(_consequencePosition, _stageTransforms.Length);
                for (int i = 0; i < bound; i++)
                {
                    var surface = _stageBivariateTransforms?[i];
                    signal = surface != null ? surface.Interpolate(signal, yPrime) : _stageTransforms[i].Function(signal);
                }
            }
            for (int i = 0; i < _responseToConsequence.Length; i++)
            {
                var surface = _trailingBivariateTransforms?[i];
                signal = surface != null ? surface.Interpolate(signal, yPrime) : _responseToConsequence[i].Function(signal);
            }
            return signal;
        }

        /// <summary>
        /// Evaluates this mode's own exposure branches at one joint evaluation point (x, y) for
        /// one consequence type — the binned analog of
        /// <see cref="EvaluateConsequenceBranches(double, int, RiskComputeFlags, out double[], out double[])"/>
        /// the component uses on the non-failure mode and the claimed states inside its
        /// conditional-bin loop. A bivariate consequence type evaluates its surface at
        /// (bound signal, y′) on its single unit-weight branch.
        /// </summary>
        /// <param name="hazardLevel">The primary hazard level x.</param>
        /// <param name="secondaryLevel">The raw secondary hazard level y.</param>
        /// <param name="typeIndex">The consequence-type position (0 is the primary).</param>
        /// <param name="flags">The realization's computational-warning flags (negative values clamp with the matching flag).</param>
        /// <param name="weights">Receives the branch weights — a reused per-type buffer, valid until the next evaluation at this type on this mode (never retain it).</param>
        /// <param name="values">Receives the branch consequence values, clamped at zero — the same reuse contract.</param>
        /// <exception cref="ArgumentNullException">Thrown when the flags sink is null.</exception>
        internal void EvaluateConsequenceBranchesAt(double hazardLevel, double secondaryLevel, int typeIndex,
            RiskComputeFlags flags, out double[] weights, out double[] values)
        {
            if (flags == null) throw new ArgumentNullException(nameof(flags));

            double yPrime = FoldSecondary(secondaryLevel);
            double signal = ConsequenceInputAtCore(hazardLevel, secondaryLevel, yPrime);
            var branches = _failureBranchesByType[typeIndex];
            var surface = _failureSurfacesByType?[typeIndex];
            int count = branches.Count;
            weights = _scratchOwnWeights[typeIndex];
            values = _scratchOwnValues[typeIndex];
            for (int i = 0; i < count; i++)
            {
                weights[i] = branches[i].Weight;
                double value = surface != null
                    ? surface.Interpolate(signal, yPrime)
                    : branches[i].Function?.Function(signal) ?? 0d;
                if (value < 0d)
                {
                    if (IsNonFailureMode)
                    {
                        flags.HasNegativeNonFailureConsequence = true;
                    }
                    else
                    {
                        flags.HasNegativeFailureConsequence = true;
                    }
                    value = 0d;
                }
                values[i] = value;
            }
        }

        /// <summary>
        /// Opens one binned evaluation at a hazard level: captures the excess pairing partner,
        /// resolves the record and invariance state, and — on recording evaluations only —
        /// allocates the fresh per-type staged entry lists the committed risk points adopt
        /// (non-recording evaluations allocate nothing, the univariate recording contract).
        /// The component calls this once per hazard evaluation before its conditional-bin loop.
        /// </summary>
        /// <param name="paired">The mode's sampled excess pairing partner, or null.</param>
        /// <param name="record">True when the evaluation records risk-point entries.</param>
        /// <param name="computedTypes">The component's computed consequence-type count.</param>
        internal void BeginBinnedEvaluation(SampledFailureMode? paired, bool record, int computedTypes)
        {
            _binPaired = paired;
            _binRecord = record && !IsNonFailureMode && !SuppressModeRecording;
            _binInvariant = IsBinInvariant && (paired == null || paired.IsBinInvariant);
            _binEvaluated = false;
            _binComputedTypes = Math.Min(computedTypes, _failureBranchesByType.Length);
            if (_binRecord)
            {
                _binStaging = new BinnedCurveStaging[_binComputedTypes];
                for (int k = 0; k < _binComputedTypes; k++)
                {
                    _binStaging[k] = new BinnedCurveStaging();
                }
            }
            else
            {
                _binStaging = null;
            }
        }

        /// <summary>
        /// Computes this mode's risk at one conditional bin (x, y_j) of the active binned
        /// evaluation: the response probability and branch consequences at the joint point, the
        /// staged w_j-scaled entry accumulation, and the per-type outputs — the conditional
        /// per-bin analog of <see cref="ComputeRisk"/>'s per-type kernel, with no recording of
        /// its own (the accumulated point commits once per hazard level through
        /// <see cref="CommitBinnedPoint"/>). A bin-invariant mode with a bin-invariant partner
        /// computes once at full weight on the first bin and reuses the outputs afterwards; its
        /// staged entries then carry the unscaled univariate shape.
        /// </summary>
        /// <param name="hazardLevel">The primary hazard level x.</param>
        /// <param name="secondaryLevel">The bin's conditional secondary hazard level y_j.</param>
        /// <param name="binWeight">The bin's trapezoid weight w_j.</param>
        /// <param name="flags">The realization's computational-warning flags.</param>
        /// <param name="typeOutputs">
        /// Receives the per-type conditional outputs (workspace-backed scratch, valid until the
        /// next bin on this mode); entries beyond the mode's computed types are untouched.
        /// </param>
        /// <exception cref="ArgumentNullException">Thrown when the flags or output sink is null.</exception>
        internal void ComputeRiskBinned(double hazardLevel, double secondaryLevel, double binWeight,
            RiskComputeFlags flags, ComponentRiskOutput[] typeOutputs)
        {
            if (flags == null) throw new ArgumentNullException(nameof(flags));
            if (typeOutputs == null) throw new ArgumentNullException(nameof(typeOutputs));

            if (_binInvariant && _binEvaluated)
            {
                for (int k = 0; k < _binComputedTypes; k++)
                {
                    typeOutputs[k] = _scratchOutputs[k];
                }
                return;
            }

            double entryWeight = _binInvariant ? 1d : binWeight;
            double yPrime = FoldSecondary(secondaryLevel);
            double probabilityOfFailure = SRPAtCore(hazardLevel, secondaryLevel, yPrime);
            double consequenceSignal = ConsequenceInputAtCore(hazardLevel, secondaryLevel, yPrime);

            var paired = _binPaired;
            bool hasPairedNonFailure = _nonFailureBranchesByType != null && paired != null;
            double nonFailSignal = 0d;
            double nonFailYPrime = 0d;
            if (hasPairedNonFailure)
            {
                nonFailYPrime = paired!.FoldSecondary(secondaryLevel);
                nonFailSignal = paired.ConsequenceInputAtCore(hazardLevel, secondaryLevel, nonFailYPrime);
            }

            for (int k = 0; k < _binComputedTypes; k++)
            {
                typeOutputs[k] = ComputeTypeRiskBinned(k, probabilityOfFailure, consequenceSignal, yPrime,
                    nonFailSignal, nonFailYPrime, hasPairedNonFailure, entryWeight, flags);
            }
            _binEvaluated = true;
        }

        /// <summary>
        /// Computes one consequence type's conditional risk at one bin — the binned counterpart
        /// of <see cref="ComputeTypeRisk"/>: the paired non-failure branches at the partner's
        /// joint signals, the failure branches (a bivariate type through its surface), the exact
        /// excess pairs at the shared conditional point, the staged entry accumulation, and the
        /// conditional per-type output.
        /// </summary>
        /// <param name="typeIndex">The consequence-type position (0 is the primary).</param>
        /// <param name="probabilityOfFailure">The mode's response probability at the bin (type-independent).</param>
        /// <param name="consequenceSignal">This mode's consequence input signal at the bin.</param>
        /// <param name="yPrime">This mode's chain-folded secondary signal at the bin.</param>
        /// <param name="nonFailSignal">The partner's consequence input signal at the bin.</param>
        /// <param name="nonFailYPrime">The partner's chain-folded secondary signal at the bin.</param>
        /// <param name="hasPairedNonFailure">Whether a paired non-failure consequence exists.</param>
        /// <param name="entryWeight">The staged-entry scale: the bin's trapezoid weight, or one on the invariant single evaluation.</param>
        /// <param name="flags">The realization's computational-warning flags.</param>
        /// <returns>The type's conditional risk output at the bin.</returns>
        private ComponentRiskOutput ComputeTypeRiskBinned(int typeIndex, double probabilityOfFailure,
            double consequenceSignal, double yPrime, double nonFailSignal, double nonFailYPrime,
            bool hasPairedNonFailure, double entryWeight, RiskComputeFlags flags)
        {
            var output = _scratchOutputs[typeIndex];
            output.Reset();
            var failureBranches = _failureBranchesByType[typeIndex];
            var failureSurface = _failureSurfacesByType?[typeIndex];

            // Paired non-failure branches at the partner's joint signals, with THIS mode's
            // paired sample at this type's coupling column (the univariate shared-draw rule).
            int nonFailCount = 1;
            double meanNonFail = 0d;
            double[] nonFailWeights = _singleUnitWeight;
            double[] nonFailValues = _singleZeroValue;
            if (hasPairedNonFailure)
            {
                var nonFailureBranches = _nonFailureBranchesByType![typeIndex];
                var nonFailureSurface = _nonFailureSurfacesByType?[typeIndex];
                nonFailCount = nonFailureBranches.Count;
                nonFailWeights = _scratchNonFailWeights![typeIndex];
                nonFailValues = _scratchNonFailValues![typeIndex];
                for (int j = 0; j < nonFailCount; j++)
                {
                    nonFailWeights[j] = nonFailureBranches[j].Weight;
                    double value = nonFailureSurface != null
                        ? nonFailureSurface.Interpolate(nonFailSignal, nonFailYPrime)
                        : nonFailureBranches[j].Function?.Function(nonFailSignal) ?? 0d;
                    if (value < 0d)
                    {
                        flags.HasNegativeNonFailureConsequence = true;
                        value = 0d;
                    }
                    nonFailValues[j] = value;
                    meanNonFail += nonFailWeights[j] * value;
                }
            }

            var staging = _binRecord ? _binStaging![typeIndex] : null;
            double meanFailure = 0d;
            double meanExcess = 0d;
            for (int i = 0; i < failureBranches.Count; i++)
            {
                double weight = failureBranches[i].Weight;
                double failureValue = failureSurface != null
                    ? failureSurface.Interpolate(consequenceSignal, yPrime)
                    : failureBranches[i].Function?.Function(consequenceSignal) ?? 0d;
                if (failureValue < 0d)
                {
                    if (probabilityOfFailure > 0d) flags.HasNegativeFailureConsequence = true;
                    failureValue = 0d;
                }
                meanFailure += weight * failureValue;

                // Excess per failure/non-failure branch pair at the shared conditional point —
                // the exact pair distribution, coherent on one (x, y_j) scenario.
                double pairedExcess = 0d;
                for (int j = 0; j < nonFailCount; j++)
                {
                    double excess = failureValue - nonFailValues[j];
                    if (excess < 0d)
                    {
                        if (probabilityOfFailure > 0d) flags.HasNegativeExcessConsequence = true;
                        excess = 0d;
                    }
                    pairedExcess += nonFailWeights[j] * excess;
                    if (staging != null)
                    {
                        double entryProbability = Tools.Clamp(probabilityOfFailure * weight * nonFailWeights[j], 0d, 1d);
                        staging.ExcessProbabilities.Add(Tools.Clamp(entryProbability * entryWeight, 0d, 1d));
                        staging.ExcessValues.Add(excess);
                    }
                }
                meanExcess += weight * pairedExcess;

                output.ResponseProbabilities.Add(Tools.Clamp(probabilityOfFailure * weight, 0d, 1d));
                output.FailureConsequences.Add(failureValue);
                output.ExcessConsequences.Add(Math.Max(0d, failureValue - meanNonFail));
            }

            // Stage the mode-level decomposition entries: the bin's failure, background,
            // non-failure, and Total union entries at the staged-entry scale, in bin-major
            // order across the sweep.
            if (staging != null)
            {
                double probabilityOfNonFailure = Tools.Clamp(1d - probabilityOfFailure, 0d, 1d);
                for (int i = 0; i < failureBranches.Count; i++)
                {
                    double failProbability = Tools.Clamp(output.ResponseProbabilities[i] * entryWeight, 0d, 1d);
                    staging.FailProbabilities.Add(failProbability);
                    staging.FailValues.Add(output.FailureConsequences[i]);
                    staging.TotalProbabilities.Add(failProbability);
                    staging.TotalValues.Add(output.FailureConsequences[i]);
                }
                for (int j = 0; j < nonFailCount; j++)
                {
                    double backgroundProbability = Tools.Clamp(nonFailWeights[j] * entryWeight, 0d, 1d);
                    double nonFailProbability = Tools.Clamp(probabilityOfNonFailure * nonFailWeights[j] * entryWeight, 0d, 1d);
                    staging.BackgroundProbabilities.Add(backgroundProbability);
                    staging.BackgroundValues.Add(nonFailValues[j]);
                    staging.NonFailProbabilities.Add(nonFailProbability);
                    staging.NonFailValues.Add(nonFailValues[j]);
                    staging.TotalProbabilities.Add(nonFailProbability);
                    staging.TotalValues.Add(nonFailValues[j]);
                }
            }

            output.ProbabilityOfFailure = probabilityOfFailure;
            output.ProbabilityOfNonFailure = Tools.Clamp(1d - probabilityOfFailure, 0d, 1d);
            output.NonFailureConsequences = meanNonFail;
            output.MeanFailureConsequences = meanFailure;
            output.MeanExcessConsequences = meanExcess;
            return output;
        }

        /// <summary>
        /// Commits the active binned evaluation's staged entries as one risk point per stream
        /// per consequence type — the one-point-per-X rule the recorded-mass accounting depends
        /// on — and releases the staging (the points adopt the lists). A no-op on non-recording
        /// evaluations.
        /// </summary>
        /// <param name="realization">The failure mode's realization sink.</param>
        /// <param name="recordedLevel">The hazard coordinate the recorded points carry (the profile-axis signal when one is selected).</param>
        /// <param name="probability">The hazard non-exceedance probability at the evaluation point.</param>
        /// <param name="hazardExceedanceProbability">The driving hazard's exceedance probability (the system response profile's X coordinate; NaN skips it).</param>
        /// <exception cref="ArgumentNullException">Thrown when the realization sink is null.</exception>
        internal void CommitBinnedPoint(FailureModeRealization realization, double recordedLevel,
            double probability, double hazardExceedanceProbability)
        {
            if (realization == null) throw new ArgumentNullException(nameof(realization));
            var staging = _binStaging;
            if (staging == null) return;
            for (int k = 0; k < staging.Length; k++)
            {
                var target = k == 0 ? realization.Curves : realization.AdditionalCurves[k - 1];
                var entries = staging[k];
                target.Background.AddRiskPoint(recordedLevel, probability, entries.BackgroundProbabilities, entries.BackgroundValues);
                target.NonFail.AddRiskPoint(recordedLevel, probability, entries.NonFailProbabilities, entries.NonFailValues);
                target.Total.AddRiskPoint(recordedLevel, probability, entries.TotalProbabilities, entries.TotalValues);
                target.Fail.AddRiskPoint(recordedLevel, probability, entries.FailProbabilities, entries.FailValues, hazardExceedanceProbability);
                target.Excess.AddRiskPoint(recordedLevel, probability, entries.ExcessProbabilities, entries.ExcessValues);
            }
            _binStaging = null;
        }

        #endregion

        #region Private Helpers

        /// <summary>
        /// One consequence type's staged entry lists of a recording binned evaluation — the
        /// five mode-scope streams accumulated across the conditional bins and adopted by the
        /// committed risk points (allocated fresh per recording evaluation, never reused).
        /// </summary>
        private sealed class BinnedCurveStaging
        {
            /// <summary>The staged Fail-stream entry probabilities.</summary>
            public List<double> FailProbabilities { get; } = new List<double>();

            /// <summary>The staged Fail-stream entry values.</summary>
            public List<double> FailValues { get; } = new List<double>();

            /// <summary>The staged Excess-stream entry probabilities.</summary>
            public List<double> ExcessProbabilities { get; } = new List<double>();

            /// <summary>The staged Excess-stream entry values.</summary>
            public List<double> ExcessValues { get; } = new List<double>();

            /// <summary>The staged Background-stream entry probabilities.</summary>
            public List<double> BackgroundProbabilities { get; } = new List<double>();

            /// <summary>The staged Background-stream entry values.</summary>
            public List<double> BackgroundValues { get; } = new List<double>();

            /// <summary>The staged NonFail-stream entry probabilities.</summary>
            public List<double> NonFailProbabilities { get; } = new List<double>();

            /// <summary>The staged NonFail-stream entry values.</summary>
            public List<double> NonFailValues { get; } = new List<double>();

            /// <summary>The staged Total-stream entry probabilities.</summary>
            public List<double> TotalProbabilities { get; } = new List<double>();

            /// <summary>The staged Total-stream entry values.</summary>
            public List<double> TotalValues { get; } = new List<double>();
        }

        /// <summary>
        /// The shared single-unit-weight array for the no-non-failure case.
        /// </summary>
        private static readonly double[] _singleUnitWeight = { 1d };

        /// <summary>
        /// The shared single-zero-value array for the no-non-failure case.
        /// </summary>
        private static readonly double[] _singleZeroValue = { 0d };

        #endregion
    }
}
