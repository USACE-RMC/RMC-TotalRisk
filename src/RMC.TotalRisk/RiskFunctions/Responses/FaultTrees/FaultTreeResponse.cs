using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees.Support;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Support;

namespace RMC.TotalRisk.RiskFunctions.Responses.FaultTrees
{
    /// <summary>
    /// A static fault-tree response function: an authored gate/event tree whose exact top-event
    /// conditional failure probability <c>P(F|h)</c> is evaluated through an ordered reduced
    /// binary decision diagram. Shared-logical transfers unify repeated events onto one Boolean
    /// variable while independent-clone transfers fork distinct variables, so repeated-event
    /// probability is exact rather than naively multiplied. The response is binary — it exposes
    /// the ordinary failure/non-failure contract and never owns hazard frequency, consequences,
    /// or risk.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The compiled plan is instance-scoped and immutable: controlled tree edits, nested tree
    /// responses, mutable tables, and ordinary referenced functions invalidate it through
    /// revisions plus defensive live-content fingerprints. Minimal cut sets are coherent-tree
    /// inspection output only; a resource-budget failure is loud and never degrades to an
    /// approximation.
    /// </para>
    /// </remarks>
    public sealed class FaultTreeResponse : ResponseFunctionBase, ITreeComputeSource,
        IProjectedIdentityResponse
    {
        /// <summary>Initializes an empty fault-tree response over default hazards 0 and 1.</summary>
        public FaultTreeResponse()
            : this(new[] { 0d, 1d }, new FaultTree())
        {
        }

        /// <summary>Initializes a fault-tree response.</summary>
        /// <param name="hazardLevels">The finite strictly ascending hazard axis.</param>
        /// <param name="faultTree">The controlled authored tree.</param>
        /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
        public FaultTreeResponse(IEnumerable<double> hazardLevels, FaultTree faultTree)
        {
            FaultTree = faultTree ?? throw new ArgumentNullException(nameof(faultTree));
            FaultTree.AttachOwner(this);
            SetHazardLevels(hazardLevels);
        }

        /// <summary>Restores a fault-tree response from its explicit graph serialization.</summary>
        /// <param name="xElement">The serialized response.</param>
        /// <param name="resolver">The optional resolver for by-reference sources and transfers.</param>
        /// <exception cref="ArgumentNullException">Thrown when the element is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when required tree content is absent.</exception>
        public FaultTreeResponse(XElement xElement, IRiskFunctionResolver? resolver = null)
        {
            if (xElement == null) throw new ArgumentNullException(nameof(xElement));
            ReadIdentityAttributes(xElement);
            SpecifiedHazard = SerializationUtilities.ReadString(xElement, nameof(SpecifiedHazard));
            HazardUnit = SerializationUtilities.ReadString(xElement, nameof(HazardUnit));

            var levels = xElement.Element(nameof(HazardLevels))?.Elements("Level")
                .Select(level => SerializationUtilities.ReadDouble(level, "Value"))
                ?? Enumerable.Empty<double>();
            _hazardLevels.AddRange(levels);

            XElement treeElement = xElement.Element(nameof(FaultTree))
                ?? throw new InvalidOperationException("The serialized fault-tree response has no FaultTree element.");
            FaultTree = new FaultTree(treeElement, resolver, Name);
            FaultTree.AttachOwner(this);
        }

        /// <summary>Number of median-LHS realizations used for uncertainty summaries.</summary>
        private const int SummaryRealizations = 1000;

        /// <summary>Stable seed base used only for uncertainty-summary sampling on a clone.</summary>
        private const int SummarySeedBase = 18031986;

        /// <summary>The default exact decision-diagram node budget.</summary>
        private const int DefaultBddNodeLimit = 1000000;

        /// <summary>The owned hazard levels.</summary>
        private readonly List<double> _hazardLevels = new List<double>();

        /// <summary>The cached read-only hazard view.</summary>
        private ReadOnlyCollection<double>? _readOnlyHazards;

        /// <summary>Per-unified-variable sampling bindings created by the latest setup.</summary>
        private SamplingBinding?[] _samplingBindings = Array.Empty<SamplingBinding?>();

        /// <summary>The canonical identity under which the current sampler was set up.</summary>
        private byte[]? _samplerIdentity;

        /// <summary>Serializes plan publication, invalidation, and rollback restoration.</summary>
        private readonly object _compiledPlanSync = new object();

        /// <summary>The immutable instance-scoped plan published for concurrent readers.</summary>
        private FaultTreeOccurrencePlan? _compiledPlan;

        /// <summary>The controlled compute-state revision observed by dependent plans.</summary>
        private long _computeRevision;

        /// <summary>Instance-scoped diagnostic count of successfully published plans.</summary>
        private long _compiledPlanBuildCount;

        /// <summary>The runtime-only decision-diagram node budget.</summary>
        private int _bddNodeLimit = DefaultBddNodeLimit;

        /// <summary>The controlled authored fault tree.</summary>
        public FaultTree FaultTree { get; }

        /// <summary>The finite strictly ascending hazard axis.</summary>
        public IReadOnlyList<double> HazardLevels => _readOnlyHazards ??= _hazardLevels.AsReadOnly();

        /// <summary>Replaces the fault-tree hazard axis atomically.</summary>
        /// <param name="hazardLevels">The replacement values.</param>
        /// <exception cref="ArgumentNullException">Thrown when the sequence is null.</exception>
        public void SetHazardLevels(IEnumerable<double> hazardLevels)
        {
            if (hazardLevels == null) throw new ArgumentNullException(nameof(hazardLevels));
            double[] replacement = hazardLevels.ToArray();
            _hazardLevels.Clear();
            _hazardLevels.AddRange(replacement);
            _samplerIdentity = null;
            InvalidateCompiledPlan(false);
            RaisePropertyChange(nameof(HazardLevels));
        }

        /// <summary>
        /// The exact decision-diagram node budget. Runtime-only: never serialized, never hashed,
        /// and never part of any identity surface. Exceeding it fails validation and setup loudly;
        /// exact evaluation is never approximated.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is not positive.</exception>
        public int BddNodeLimit
        {
            get { return _bddNodeLimit; }
            set
            {
                if (value < 1) throw new ArgumentOutOfRangeException(nameof(value));
                if (_bddNodeLimit == value) return;
                _bddNodeLimit = value;
                InvalidateCompiledPlan(false);
                RaisePropertyChange(nameof(BddNodeLimit));
            }
        }

        /// <summary>
        /// Whether the expanded logic is coherent: it contains no exclusive-disjunction gate.
        /// Minimal cut sets are defined only for coherent trees.
        /// </summary>
        public bool IsCoherent => GetCompiledPlan().IsCoherent;

        /// <inheritdoc/>
        public override ResponseFunctionType FunctionType => ResponseFunctionType.FaultTree;

        /// <inheritdoc/>
        public override bool IsDeterministic => GetCompiledPlan().IsDeterministic;

        /// <inheritdoc/>
        /// <remarks>
        /// Counts each unified Boolean variable once: a shared-logical event contributes one
        /// dimension set no matter how many occurrences reference it, while every independent
        /// clone contributes its own.
        /// </remarks>
        public override int SamplingDimensions => GetCompiledPlan().SamplingDimensions;

        /// <summary>
        /// Extracts the minimal cut sets of a coherent tree from the exact decision diagram.
        /// Inspection output only — the response's probability never depends on cut sets.
        /// </summary>
        /// <param name="maxCutSets">The loud extraction bound.</param>
        /// <returns>The minimal cut sets ordered by cardinality, then unified-variable sequence.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the response is invalid, the tree is non-coherent, or extraction exceeds the bound.
        /// </exception>
        public IReadOnlyList<FaultTreeCutSet> GetMinimalCutSets(int maxCutSets = 10000)
        {
            ThrowIfUnusable();
            FaultTreeOccurrencePlan plan = GetCompiledPlan();
            if (!plan.IsCoherent)
                throw new InvalidOperationException(
                    "Minimal cut sets are not defined for a non-coherent fault tree (an Xor gate is present); " +
                    "inspect the exact decision-diagram probability instead.");
            IReadOnlyList<int[]> ordinalSets = plan.FrozenBdd!.ExtractMinimalCutSets(maxCutSets);
            var result = new List<FaultTreeCutSet>(ordinalSets.Count);
            for (int i = 0; i < ordinalSets.Count; i++)
            {
                var events = new List<FaultTreeCutSetEvent>(ordinalSets[i].Length);
                for (int j = 0; j < ordinalSets[i].Length; j++)
                {
                    FaultTreeVariableSlot variable = plan.Variables[ordinalSets[i][j]];
                    events.Add(new FaultTreeCutSetEvent(variable.SourceNode.Id,
                        variable.DisplayName, variable.FirstOccurrence!.CanonicalPath));
                }
                result.Add(new FaultTreeCutSet(events));
            }
            return result;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Local table dimensions are columns of this function's sampler. Every referenced
        /// response variable is prepared on an isolated self-contained setup clone seeded from the
        /// variable's content identity and canonical ordinal, so shared-logical occurrences sample
        /// once and identical independent clones receive distinct reproducible streams. Setup is
        /// transactional: a compilation, clone, capacity, or child-setup failure restores the
        /// exact prior parent sampler state.
        /// </remarks>
        public override void SetupSampler(int sampleSize, int seed, SamplingScheme scheme)
        {
            int priorSampleSize = SampleSize;
            double[,]? priorPercentiles = _percentiles;
            byte[]? priorSamplerIdentity = _samplerIdentity;
            SamplingBinding?[] priorBindings = _samplingBindings;
            try
            {
                ThrowIfUnusable();
                FaultTreeOccurrencePlan plan = GetCompiledPlan();
                base.SetupSampler(sampleSize, seed, scheme);
                var nextBindings = new SamplingBinding?[plan.Variables.Count];
                int dimension = 0;
                for (int ordinal = 0; ordinal < plan.Variables.Count; ordinal++)
                {
                    FaultTreeVariableSlot variable = plan.Variables[ordinal];
                    // Derived common-cause events own no sampler state: their group's basis
                    // variable carries the one shared stream.
                    if (variable.CcfFactor != null) continue;
                    ProbabilitySource source = variable.SourceNode.ProbabilitySource;
                    if (source.Kind == ProbabilitySourceKind.UncertainTabular)
                    {
                        int localDimension = dimension;
                        dimension++;
                        IReadOnlyList<ITransformFunction>? tabularTransforms = null;
                        if (source.HasHazardTransforms)
                        {
                            byte[] sourceHash = Convert.FromHexString(variable.ContentToken);
                            int chainBaseSeed = SeedHelpers.HashCombine(seed, sourceHash, ordinal);
                            tabularTransforms = SetupTransformClones(source, variable, chainBaseSeed,
                                sampleSize, scheme, ref dimension);
                        }
                        nextBindings[ordinal] = new SamplingBinding(localDimension, null, tabularTransforms);
                    }
                    else if (source.Kind == ProbabilitySourceKind.ResponseFunctionReference
                        && source.ResponseFunction != null)
                    {
                        IResponseFunction sampledFunction;
                        int childStreamSeed;
                        try
                        {
                            sampledFunction = CloneResponseFunction(source.ResponseFunction);
                            byte[] sourceHash = Convert.FromHexString(variable.ContentToken);
                            childStreamSeed = SeedHelpers.HashCombine(seed, sourceHash, ordinal);
                            sampledFunction.SetupSampler(sampleSize, childStreamSeed, scheme);
                        }
                        catch (Exception ex) when (ex is ArgumentException
                            || ex is InvalidOperationException
                            || ex is NotSupportedException)
                        {
                            throw new InvalidOperationException(
                                $"Fault-tree response '{Name}' could not set up basic event " +
                                $"'{variable.SourceNode.Name}' at canonical path " +
                                $"'{variable.FirstOccurrence!.CanonicalPath}' from referenced response " +
                                $"'{source.ResponseFunction.Name}': {ex.Message}", ex);
                        }

                        int childDimensions = sampledFunction.SamplingDimensions;
                        if (childDimensions != variable.SamplingDimensions - source.HazardTransformDimensions)
                        {
                            throw new InvalidOperationException(
                                $"Fault-tree response '{Name}' compiled basic event " +
                                $"'{variable.SourceNode.Name}' with {variable.SamplingDimensions} " +
                                $"sampling dimensions, but its setup clone reported {childDimensions}. " +
                                "The referenced response changed during sampler setup; retry setup.");
                        }
                        RiskFunctionBase? sampledBase = sampledFunction as RiskFunctionBase;
                        if (childDimensions > 0 && sampledBase == null)
                            throw new InvalidOperationException(
                                $"Referenced response '{sampledFunction.Name}' does not expose the " +
                                "established RiskFunctionBase sampler state.");
                        for (int realization = 0; realization < sampleSize; realization++)
                        {
                            for (int childDimension = 0; childDimension < childDimensions; childDimension++)
                            {
                                _percentiles![realization, dimension + childDimension] =
                                    sampledBase!.SampledPercentile(realization, childDimension);
                            }
                        }
                        dimension += childDimensions;
                        IReadOnlyList<ITransformFunction>? referenceTransforms = null;
                        if (source.HasHazardTransforms)
                        {
                            referenceTransforms = SetupTransformClones(source, variable, childStreamSeed,
                                sampleSize, scheme, ref dimension);
                        }
                        nextBindings[ordinal] = new SamplingBinding(-1, sampledFunction, referenceTransforms);
                    }
                }
                if (dimension != plan.SamplingDimensions)
                {
                    throw new InvalidOperationException(
                        $"Fault-tree response '{Name}' populated {dimension} sampler dimensions " +
                        $"for a compiled plan requiring {plan.SamplingDimensions}. Retry setup after " +
                        "all referenced functions are stable.");
                }
                _samplingBindings = nextBindings;
                _samplerIdentity = CanonicalHash(plan);
            }
            catch
            {
                SampleSize = priorSampleSize;
                _percentiles = priorPercentiles;
                _samplerIdentity = priorSamplerIdentity;
                _samplingBindings = priorBindings;
                throw;
            }
        }

        /// <inheritdoc/>
        public override (bool IsValid, List<string> ValidationMessages) Validate()
        {
            var messages = new List<string>();
            if (string.IsNullOrEmpty(SpecifiedHazard))
                messages.Add("Error: The fault-tree response does not have a specified hazard type.");
            if (string.IsNullOrEmpty(HazardUnit))
                messages.Add("Error: The fault-tree response does not have a specified hazard unit.");

            if (_hazardLevels.Count == 0)
            {
                messages.Add("Error: The fault-tree response requires at least one hazard level.");
            }
            else
            {
                for (int i = 0; i < _hazardLevels.Count; i++)
                {
                    if (!double.IsFinite(_hazardLevels[i]))
                    {
                        messages.Add("Error: Fault-tree hazard levels must be finite.");
                        break;
                    }
                    if (i > 0 && _hazardLevels[i] <= _hazardLevels[i - 1])
                    {
                        messages.Add("Error: Fault-tree hazard levels must be strictly ascending.");
                        break;
                    }
                }
            }

            messages.AddRange(FaultTree.Validate());

            FaultTreeOccurrencePlan? plan = null;
            try
            {
                plan = GetCompiledPlan();
            }
            catch (InvalidOperationException ex)
            {
                AddUnique(messages, $"Error: {ex.Message}");
            }

            if (plan != null)
            {
                for (int i = 0; i < plan.CompileDiagnostics.Count; i++)
                    AddUnique(messages, $"Error: {plan.CompileDiagnostics[i]}");
                for (int i = 0; i < plan.Warnings.Count; i++) AddUnique(messages, plan.Warnings[i]);
                foreach (FaultTreeVariableSlot variable in plan.Variables)
                {
                    // Derived common-cause events carry no source of their own; the group's
                    // basis variable validates the shared member source once.
                    if (variable.CcfFactor != null) continue;
                    foreach (string message in variable.SourceNode.ProbabilitySource.Validate(
                        variable.SourceFunction.HazardLevels,
                        $"Basic event '{variable.SourceNode.Name}'", "fault-tree",
                        variable.SourceFunction.SpecifiedHazard))
                        AddUnique(messages, message);
                }

                if (plan.FrozenBdd != null
                    && messages.All(message => !message.StartsWith("Error:", StringComparison.Ordinal))
                    && !CurveIsMonotonic(EvaluateTopCurve(SampleMode.Mean, 0d, -1)))
                {
                    AddUnique(messages,
                        "Warning: The response function is not monotonically increasing. Please confirm you have entered your inputs correctly before proceeding.");
                }
            }

            return (messages.All(message => !message.StartsWith("Error:", StringComparison.Ordinal)), messages);
        }

        /// <inheritdoc/>
        public override OrderedPairedData SampleResponseFunction()
        {
            ThrowIfUnusable();
            return EvaluateTopCurve(SampleMode.Mean, 0d, -1);
        }

        /// <inheritdoc/>
        public override OrderedPairedData SampleResponseFunction(double percentile)
        {
            if (percentile <= 0d || percentile >= 1d)
                throw new ArgumentOutOfRangeException(nameof(percentile), "The percentile must be between 0 and 1.");
            ThrowIfUnusable();
            return EvaluateTopCurve(SampleMode.Percentile, percentile, -1);
        }

        /// <inheritdoc/>
        public override OrderedPairedData SampleResponseFunction(int realizationIndex)
        {
            ThrowIfUnusable();
            EnsureSamplerCurrent();
            if (realizationIndex < 0 || realizationIndex >= SampleSize)
                throw new ArgumentOutOfRangeException(nameof(realizationIndex));
            return EvaluateTopCurve(SampleMode.Realization, 0d, realizationIndex);
        }

        /// <inheritdoc/>
        public override IUnivariateDistribution SampleFunction()
        {
            return new EmpiricalDistribution(SampleResponseFunction());
        }

        /// <inheritdoc/>
        public override IUnivariateDistribution SampleFunction(double percentile)
        {
            return new EmpiricalDistribution(SampleResponseFunction(percentile));
        }

        /// <inheritdoc/>
        public override IUnivariateDistribution SampleFunction(int realizationIndex)
        {
            return new EmpiricalDistribution(SampleResponseFunction(realizationIndex));
        }

        /// <inheritdoc/>
        public override bool IsMonotonic()
        {
            if (!Validate().IsValid) return false;
            if (!CurveIsMonotonic(SampleResponseFunction())) return false;
            if (IsDeterministic) return true;
            return CurveIsMonotonic(SampleResponseFunction(0.00001d))
                && CurveIsMonotonic(SampleResponseFunction(0.5d))
                && CurveIsMonotonic(SampleResponseFunction(1d - 0.00001d));
        }

        /// <inheritdoc/>
        public override double MinHazard()
        {
            if (_hazardLevels.Count == 0) throw new InvalidOperationException("The fault-tree response has no hazard levels.");
            return _hazardLevels[0];
        }

        /// <inheritdoc/>
        public override double MaxHazard()
        {
            if (_hazardLevels.Count == 0) throw new InvalidOperationException("The fault-tree response has no hazard levels.");
            return _hazardLevels[_hazardLevels.Count - 1];
        }

        /// <inheritdoc/>
        public override double MinProbability()
        {
            return SampleResponseFunction()[0].Y;
        }

        /// <inheritdoc/>
        public override double MaxProbability()
        {
            OrderedPairedData curve = SampleResponseFunction();
            return curve[curve.Count - 1].Y;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Deterministic trees are summarized exactly. Uncertain trees use a deterministic
        /// 1,000-realization median-LHS sampler on a self-contained clone, preserving the live
        /// function's sampler state.
        /// </remarks>
        public override UncertaintyAnalysisResults? ComputeUncertaintyResults(double confidenceIntervalWidth = 0.9)
        {
            if (confidenceIntervalWidth <= 0d || confidenceIntervalWidth >= 1d)
                throw new ArgumentOutOfRangeException(nameof(confidenceIntervalWidth), "The confidence interval width must be between 0 and 1.");
            if (!Validate().IsValid) return null;

            var results = new UncertaintyAnalysisResults
            {
                ModeCurve = new double[_hazardLevels.Count],
                MeanCurve = new double[_hazardLevels.Count],
                ConfidenceIntervals = new double[_hazardLevels.Count, 2],
            };
            if (IsDeterministic)
            {
                OrderedPairedData curve = SampleResponseFunction();
                for (int i = 0; i < curve.Count; i++)
                {
                    results.ModeCurve[i] = curve[i].Y;
                    results.MeanCurve[i] = curve[i].Y;
                    results.ConfidenceIntervals[i, 0] = curve[i].Y;
                    results.ConfidenceIntervals[i, 1] = curve[i].Y;
                }
                return results;
            }

            var clone = new FaultTreeResponse(ToXElement(RiskSerializationMode.SelfContained));
            int seed = ToPositiveSeed(SeedHelpers.HashCombine(SummarySeedBase, CanonicalHash(), 0));
            clone.SetupSampler(SummaryRealizations, seed, SamplingScheme.LatinHypercubeMedian);
            var values = new double[_hazardLevels.Count, SummaryRealizations];
            for (int realization = 0; realization < SummaryRealizations; realization++)
            {
                OrderedPairedData curve = clone.SampleResponseFunction(realization);
                for (int h = 0; h < curve.Count; h++) values[h, realization] = curve[h].Y;
            }

            double tail = (1d - confidenceIntervalWidth) / 2d;
            var row = new double[SummaryRealizations];
            for (int h = 0; h < _hazardLevels.Count; h++)
                FunctionHelpers.SummarizeEnsembleRow(values, h, row, tail, results);
            return results;
        }

        /// <inheritdoc/>
        public override XElement ToXElement()
        {
            return ToXElement(RiskSerializationMode.SelfContained);
        }

        /// <summary>Serializes the response and propagates the mode to source and transfer references.</summary>
        /// <param name="mode">The serialization mode.</param>
        /// <returns>The serialized response.</returns>
        public XElement ToXElement(RiskSerializationMode mode)
        {
            var element = new XElement(nameof(FaultTreeResponse));
            WriteIdentityAttributes(element);
            element.SetAttributeValue(nameof(SpecifiedHazard), SpecifiedHazard);
            element.SetAttributeValue(nameof(HazardUnit), HazardUnit);
            var hazards = new XElement(nameof(HazardLevels));
            foreach (double value in _hazardLevels)
            {
                var level = new XElement("Level");
                level.SetAttributeValue("Value", SerializationUtilities.FormatDouble(value));
                hazards.Add(level);
            }
            element.Add(hazards);
            element.Add(FaultTree.ToXElement(mode));
            return element;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Hashes a projected tree identity rather than persistent node/input ids, display order,
        /// or nested reference markers. Commutative gate inputs are sorted by child identity, and
        /// unified basic events carry first-occurrence ordinals, so shared-logical repetition and
        /// content-identical independent clones hash differently while equivalent self-contained
        /// and by-reference forms share one seed identity.
        /// </remarks>
        public override byte[] CanonicalHash()
        {
            return CanonicalHash(GetCompiledPlan());
        }

        /// <summary>Hashes a previously compiled plan without recursively recompiling source edges.</summary>
        /// <param name="plan">The occurrence plan compiled on the active cycle-detection stack.</param>
        /// <returns>The metadata-free SHA-256 canonical identity.</returns>
        internal byte[] CanonicalHash(FaultTreeOccurrencePlan plan)
        {
            return CanonicalContentHasher.Hash(BuildIdentityXElement(plan),
                CanonicalizationRules.ModelRules);
        }

        /// <summary>Builds the projected identity form used when this response is nested in a component chain.</summary>
        /// <returns>The metadata- and persistence-free response identity.</returns>
        internal XElement ToIdentityXElement()
        {
            return BuildIdentityXElement(GetCompiledPlan());
        }

        /// <summary>Builds projected identity from a previously compiled occurrence plan.</summary>
        /// <param name="plan">The compiled plan.</param>
        /// <returns>The identity element.</returns>
        private XElement BuildIdentityXElement(FaultTreeOccurrencePlan plan)
        {
            var identity = new XElement(nameof(FaultTreeResponse));
            var hazards = new XElement(nameof(HazardLevels));
            foreach (double value in _hazardLevels)
            {
                var level = new XElement("Level");
                level.SetAttributeValue("Value", SerializationUtilities.FormatDouble(value));
                hazards.Add(level);
            }
            identity.Add(hazards);
            identity.Add(plan.Identity);
            return identity;
        }

        /// <summary>Returns the current immutable plan, compiling and publishing it once when stale.</summary>
        /// <returns>The current plan.</returns>
        private FaultTreeOccurrencePlan GetCompiledPlan()
        {
            while (true)
            {
                FaultTreeOccurrencePlan? current = Volatile.Read(ref _compiledPlan);
                if (current != null)
                {
                    if (current.Dependencies.IsCurrent()) return current;
                    TryInvalidateCompiledPlan(current, true);
                    continue;
                }

                FaultTreeOccurrencePlan? candidate = null;
                lock (_compiledPlanSync)
                {
                    if (_compiledPlan != null) continue;
                    candidate = FaultTreeOccurrencePlan.Compile(this);
                    if (!candidate.Dependencies.IsCurrent()) continue;
                    candidate.Dependencies.Attach(this);
                    if (!candidate.Dependencies.IsCurrent())
                    {
                        candidate.Dependencies.Detach(this);
                        continue;
                    }
                    Volatile.Write(ref _compiledPlan, candidate);
                    Interlocked.Increment(ref _compiledPlanBuildCount);
                }

                if (candidate.Dependencies.IsCurrent()) return candidate;
                TryInvalidateCompiledPlan(candidate, true);
            }
        }

        /// <summary>Returns the cached occurrence plan to the owning tree's inspection surface.</summary>
        /// <returns>The current plan.</returns>
        internal FaultTreeOccurrencePlan GetOccurrencePlan()
        {
            return GetCompiledPlan();
        }

        /// <summary>The controlled compute revision observed by dependent tree plans.</summary>
        internal long ComputeRevision => Interlocked.Read(ref _computeRevision);

        /// <summary>Raised internally after this response's compute state becomes stale.</summary>
        internal event EventHandler? ComputeStateChanged;

        /// <summary>
        /// The number of plans this instance has published. This internal diagnostic seam proves
        /// reuse/invalidation without exposing cache mechanics in the public model API.
        /// </summary>
        internal long CompiledPlanBuildCount => Interlocked.Read(ref _compiledPlanBuildCount);

        /// <summary>The current frozen decision-node count for tests and the performance harness.</summary>
        internal int CompiledDecisionNodeCount => GetCompiledPlan().FrozenBdd?.DecisionNodeCount ?? 0;

        /// <summary>The current unified-variable count for tests and the performance harness.</summary>
        internal int CompiledVariableCount => GetCompiledPlan().Variables.Count;

        /// <summary>An opaque identity for proving that repeated reads reuse one immutable plan.</summary>
        internal object CompiledPlanIdentity => GetCompiledPlan();

        /// <summary>Invalidates this instance and propagates compute staleness to dependent owners.</summary>
        /// <param name="raisePropertyChange">Whether to notify ordinary function observers.</param>
        private void InvalidateCompiledPlan(bool raisePropertyChange)
        {
            lock (_compiledPlanSync)
            {
                FaultTreeOccurrencePlan? current = _compiledPlan;
                current?.Dependencies.Detach(this);
                Volatile.Write(ref _compiledPlan, null);
                Interlocked.Increment(ref _computeRevision);
            }
            ComputeStateChanged?.Invoke(this, EventArgs.Empty);
            if (raisePropertyChange) RaisePropertyChange(nameof(FaultTree));
        }

        /// <summary>Invalidates after one committed controlled-tree edit.</summary>
        internal void NotifyTreeComputeChanged()
        {
            InvalidateCompiledPlan(true);
        }

        /// <summary>Invalidates only the stale plan observed by one racing reader.</summary>
        /// <param name="expected">The plan that failed its dependency check.</param>
        /// <param name="raisePropertyChange">Whether to notify ordinary function observers.</param>
        /// <returns>True when the expected plan was still current and was invalidated.</returns>
        private bool TryInvalidateCompiledPlan(FaultTreeOccurrencePlan expected, bool raisePropertyChange)
        {
            lock (_compiledPlanSync)
            {
                if (!ReferenceEquals(_compiledPlan, expected)) return false;
                expected.Dependencies.Detach(this);
                Volatile.Write(ref _compiledPlan, null);
                Interlocked.Increment(ref _computeRevision);
            }
            ComputeStateChanged?.Invoke(this, EventArgs.Empty);
            if (raisePropertyChange) RaisePropertyChange(nameof(FaultTree));
            return true;
        }

        /// <summary>Invalidates when a nested or external tree dependency changes.</summary>
        /// <param name="sender">The originating tree response.</param>
        /// <param name="e">The event payload.</param>
        internal void DependencyTreeComputeChanged(object? sender, EventArgs e)
        {
            InvalidateCompiledPlan(true);
        }

        /// <summary>Invalidates when a directly or recursively owned uncertain table changes membership.</summary>
        /// <param name="sender">The originating table.</param>
        /// <param name="e">The collection-change payload.</param>
        internal void DependencyTableCollectionChanged(object? sender,
            NotifyCollectionChangedEventArgs e)
        {
            InvalidateCompiledPlan(true);
        }

        /// <summary>Invalidates for compute-relevant edits to an ordinary referenced response.</summary>
        /// <param name="sender">The originating function.</param>
        /// <param name="e">The property-change payload.</param>
        internal void DependencyFunctionPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Name) || e.PropertyName == nameof(Description)
                || e.PropertyName == nameof(SpecifiedHazard) || e.PropertyName == nameof(HazardUnit)
                || e.PropertyName == nameof(Id)) return;
            InvalidateCompiledPlan(true);
        }

        /// <inheritdoc/>
        long ITreeComputeSource.ComputeRevision => ComputeRevision;

        /// <inheritdoc/>
        event EventHandler? ITreeComputeSource.ComputeStateChanged
        {
            add { ComputeStateChanged += value; }
            remove { ComputeStateChanged -= value; }
        }

        /// <inheritdoc/>
        void ITreeComputeSource.DependencyTreeComputeChanged(object? sender, EventArgs e)
        {
            DependencyTreeComputeChanged(sender, e);
        }

        /// <inheritdoc/>
        void ITreeComputeSource.DependencyTableCollectionChanged(object? sender,
            NotifyCollectionChangedEventArgs e)
        {
            DependencyTableCollectionChanged(sender, e);
        }

        /// <inheritdoc/>
        void ITreeComputeSource.DependencyFunctionPropertyChanged(object? sender,
            PropertyChangedEventArgs e)
        {
            DependencyFunctionPropertyChanged(sender, e);
        }

        /// <inheritdoc/>
        XElement IProjectedIdentityResponse.ToIdentityXElement()
        {
            return ToIdentityXElement();
        }

        /// <summary>Captures the exact owner cache/revision state for an authoring transaction.</summary>
        /// <returns>The opaque checkpoint.</returns>
        internal object CreateCompiledPlanCheckpoint()
        {
            lock (_compiledPlanSync)
                return new CompiledPlanCheckpoint(_compiledPlan, _computeRevision, _compiledPlanBuildCount);
        }

        /// <summary>Restores the exact owner cache/revision state after authoring rollback.</summary>
        /// <param name="checkpoint">The checkpoint returned by <see cref="CreateCompiledPlanCheckpoint"/>.</param>
        /// <exception cref="ArgumentException">Thrown when the checkpoint is invalid.</exception>
        internal void RestoreCompiledPlanCheckpoint(object checkpoint)
        {
            if (checkpoint is not CompiledPlanCheckpoint state)
                throw new ArgumentException("The fault-tree compiled-plan checkpoint is invalid.",
                    nameof(checkpoint));
            lock (_compiledPlanSync)
            {
                _compiledPlan?.Dependencies.Detach(this);
                _computeRevision = state.ComputeRevision;
                _compiledPlan = state.Plan;
                _compiledPlanBuildCount = state.PlanBuildCount;
                _compiledPlan?.Dependencies.Attach(this);
            }
        }

        /// <summary>One exact cache/revision authoring checkpoint.</summary>
        private sealed class CompiledPlanCheckpoint
        {
            /// <summary>Initializes one checkpoint.</summary>
            /// <param name="plan">The pre-mutation plan.</param>
            /// <param name="computeRevision">The pre-mutation revision.</param>
            /// <param name="planBuildCount">The pre-mutation diagnostic publication count.</param>
            internal CompiledPlanCheckpoint(FaultTreeOccurrencePlan? plan, long computeRevision,
                long planBuildCount)
            {
                Plan = plan;
                ComputeRevision = computeRevision;
                PlanBuildCount = planBuildCount;
            }

            /// <summary>The pre-mutation plan.</summary>
            internal FaultTreeOccurrencePlan? Plan { get; }

            /// <summary>The pre-mutation revision.</summary>
            internal long ComputeRevision { get; }

            /// <summary>The pre-mutation diagnostic publication count.</summary>
            internal long PlanBuildCount { get; }
        }

        /// <summary>The sampling mode used by the shared evaluator.</summary>
        private enum SampleMode
        {
            /// <summary>Mean source values.</summary>
            Mean,

            /// <summary>One co-monotonic percentile.</summary>
            Percentile,

            /// <summary>One pre-allocated realization.</summary>
            Realization,
        }

        /// <summary>Evaluates the exact top-event curve over the tree hazard axis.</summary>
        /// <param name="mode">The source sampling mode.</param>
        /// <param name="percentile">The percentile when applicable.</param>
        /// <param name="realizationIndex">The realization index when applicable.</param>
        /// <returns>The hazard/failure-probability curve.</returns>
        private OrderedPairedData EvaluateTopCurve(SampleMode mode, double percentile, int realizationIndex)
        {
            FaultTreeOccurrencePlan plan = GetCompiledPlan();
            FrozenFaultTreeBdd frozen = plan.FrozenBdd
                ?? throw new InvalidOperationException(
                    "The fault-tree response has no compiled decision diagram. Call Validate() and correct the reported errors.");
            var probabilities = new double[plan.Variables.Count];
            var scratch = new double[frozen.NodeCount];
            var values = new double[_hazardLevels.Count];
            for (int h = 0; h < _hazardLevels.Count; h++)
            {
                // Ordinary and common-cause basis variables evaluate their sources first; each
                // derived common-cause event is then its multiplicity factor times its group's
                // evaluated basis, independent of ordinal order.
                for (int ordinal = 0; ordinal < plan.Variables.Count; ordinal++)
                {
                    if (plan.Variables[ordinal].CcfFactor != null) continue;
                    probabilities[ordinal] = EvaluateVariable(plan.Variables[ordinal],
                        _hazardLevels[h], mode, percentile, realizationIndex);
                }
                for (int ordinal = 0; ordinal < plan.Variables.Count; ordinal++)
                {
                    FaultTreeVariableSlot variable = plan.Variables[ordinal];
                    if (variable.CcfFactor is double factor)
                        probabilities[ordinal] = factor * probabilities[variable.CcfBasisSlot!.Ordinal];
                }
                values[h] = ClampRoundoff(frozen.Evaluate(probabilities, scratch));
            }
            return new OrderedPairedData(_hazardLevels.ToArray(), values,
                true, SortOrder.Ascending, false, SortOrder.None);
        }

        /// <summary>Evaluates one unified variable's source in the selected sampling mode.</summary>
        /// <param name="variable">The unified variable.</param>
        /// <param name="hazard">The caller's current hazard value.</param>
        /// <param name="mode">The sampling mode.</param>
        /// <param name="percentile">The shared percentile.</param>
        /// <param name="realizationIndex">The realization index.</param>
        /// <returns>The conditional event probability.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the source evaluates outside the unit interval.</exception>
        private double EvaluateVariable(FaultTreeVariableSlot variable, double hazard,
            SampleMode mode, double percentile, int realizationIndex)
        {
            ProbabilitySource source = variable.SourceNode.ProbabilitySource;
            int sourceHazardIndex = variable.SourceFunction._hazardLevels.IndexOf(hazard);
            bool aligned = sourceHazardIndex >= 0;
            double value;
            if (mode == SampleMode.Mean)
            {
                value = aligned
                    ? source.EvaluateMean(hazard, sourceHazardIndex)
                    : source.EvaluateMeanAtHazard(hazard);
            }
            else if (mode == SampleMode.Percentile)
            {
                value = aligned
                    ? source.EvaluatePercentile(hazard, sourceHazardIndex, percentile)
                    : source.EvaluatePercentileAtHazard(hazard, percentile);
            }
            else if (mode == SampleMode.Realization)
            {
                if (source.HasHazardTransforms || source.BivariateAxis != null)
                {
                    // Realization-mode chain values come from the variable's sampler-bound
                    // transform clones, so the transformed hazard is computed here and every
                    // lookup takes the off-axis route — the aligned index is undefined on a
                    // transformed axis.
                    SamplingBinding binding = _samplingBindings[variable.Ordinal]
                        ?? throw new InvalidOperationException(
                            "The transformed basic-event variable has no sampler binding.");
                    IReadOnlyList<ITransformFunction> transforms = binding.Transforms
                        ?? throw new InvalidOperationException(
                            "The transformed basic-event variable has no sampler-bound hazard-transform clones.");
                    double transformed = ProbabilitySource.ApplyTransformRealizations(
                        transforms, hazard, realizationIndex);
                    if (source.BivariateAxis != null)
                    {
                        var surface = binding.ResponseFunction as IBivariateResponseFunction
                            ?? throw new InvalidOperationException(
                                "The bivariate-surface basic-event variable has no sampler binding.");
                        value = source.EvaluateBivariateSurfaceOn(surface, hazard, transformed);
                    }
                    else if (source.Kind == ProbabilitySourceKind.ResponseFunctionReference)
                    {
                        IResponseFunction sampledFunction = binding.ResponseFunction
                            ?? throw new InvalidOperationException("The referenced basic-event variable has no sampler binding.");
                        value = sampledFunction.SampleFunction(realizationIndex).CDF(transformed);
                    }
                    else
                    {
                        double localPercentile = Percentile(realizationIndex, binding.LocalDimension);
                        value = source.EvaluateRealizationAtHazard(transformed, realizationIndex, localPercentile);
                    }
                }
                else if (source.Kind == ProbabilitySourceKind.ResponseFunctionReference)
                {
                    IResponseFunction sampledFunction = _samplingBindings[variable.Ordinal]?.ResponseFunction
                        ?? throw new InvalidOperationException("The referenced basic-event variable has no sampler binding.");
                    value = sampledFunction.SampleFunction(realizationIndex).CDF(hazard);
                }
                else
                {
                    double localPercentile = source.Kind == ProbabilitySourceKind.UncertainTabular
                        ? Percentile(realizationIndex, _samplingBindings[variable.Ordinal]!.LocalDimension)
                        : 0d;
                    value = aligned
                        ? source.EvaluateRealization(hazard, sourceHazardIndex, realizationIndex, localPercentile)
                        : source.EvaluateRealizationAtHazard(hazard, realizationIndex, localPercentile);
                }
            }
            else
            {
                throw new InvalidOperationException($"Unsupported fault-tree sample mode '{mode}'.");
            }

            if (!double.IsFinite(value) || value < 0d || value > 1d)
                throw new InvalidOperationException($"Basic event '{variable.SourceNode.Name}' evaluated outside [0, 1]. Call Validate() and correct the source.");
            return value;
        }

        /// <summary>
        /// Evaluates one node-importance draw read-only against a published plan. Each unified
        /// basic-event variable takes its own source percentile, with -1 selecting the source
        /// mean, and the caller receives every variable's sampled probability — one value per
        /// unified variable regardless of how many shared occurrences reference it.
        /// </summary>
        /// <param name="plan">The published immutable occurrence plan.</param>
        /// <param name="hazard">The analyzed authored hazard level.</param>
        /// <param name="variablePercentiles">Per-ordinal source percentiles; -1 selects the mean.</param>
        /// <param name="variableProbabilities">The receiving sampled variable probabilities.</param>
        /// <param name="scratch">The caller-owned diagram evaluation scratch array.</param>
        /// <returns>The exact top-event probability at the analyzed hazard level.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the plan has no compiled diagram or a source evaluates outside the unit interval.
        /// </exception>
        internal double EvaluateImportanceSample(FaultTreeOccurrencePlan plan, double hazard,
            double[] variablePercentiles, double[] variableProbabilities, double[] scratch)
        {
            FrozenFaultTreeBdd frozen = plan.FrozenBdd
                ?? throw new InvalidOperationException(
                    "The fault-tree response has no compiled decision diagram. Call Validate() and correct the reported errors.");
            for (int ordinal = 0; ordinal < plan.Variables.Count; ordinal++)
            {
                FaultTreeVariableSlot variable = plan.Variables[ordinal];
                if (variable.CcfFactor != null) continue;
                ProbabilitySource source = variable.SourceNode.ProbabilitySource;
                int sourceHazardIndex = variable.SourceFunction._hazardLevels.IndexOf(hazard);
                bool aligned = sourceHazardIndex >= 0;
                double percentile = variablePercentiles[ordinal];
                double value;
                if (percentile < 0d)
                {
                    value = aligned
                        ? source.EvaluateMean(hazard, sourceHazardIndex)
                        : source.EvaluateMeanAtHazard(hazard);
                }
                else
                {
                    value = aligned
                        ? source.EvaluatePercentile(hazard, sourceHazardIndex, percentile)
                        : source.EvaluatePercentileAtHazard(hazard, percentile);
                }
                if (!double.IsFinite(value) || value < 0d || value > 1d)
                    throw new InvalidOperationException($"Basic event '{variable.DisplayName}' evaluated outside [0, 1]. Call Validate() and correct the source.");
                variableProbabilities[ordinal] = value;
            }
            for (int ordinal = 0; ordinal < plan.Variables.Count; ordinal++)
            {
                FaultTreeVariableSlot variable = plan.Variables[ordinal];
                if (variable.CcfFactor is double factor)
                    variableProbabilities[ordinal] = factor * variableProbabilities[variable.CcfBasisSlot!.Ordinal];
            }
            return ClampRoundoff(frozen.Evaluate(variableProbabilities, scratch));
        }

        /// <summary>Creates an isolated self-contained response occurrence for setup.</summary>
        /// <param name="source">The referenced live response.</param>
        /// <returns>The setup clone.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the reference cannot be cloned.</exception>
        private static IResponseFunction CloneResponseFunction(IResponseFunction source)
        {
            return RiskFunctionFactory.CreateResponseFunction(source.ToXElement())
                ?? throw new InvalidOperationException(
                    $"Referenced response function '{source.Name}' cannot be cloned for an independent occurrence.");
        }

        /// <summary>
        /// Clones one source's hazard-transform chain onto isolated self-contained setup
        /// instances, seeds each clone from the variable's child-stream base combined with the
        /// transform's content hash and chain position, and copies the clones' exact flattened
        /// percentiles into this sampler after the source's own dimensions.
        /// </summary>
        /// <param name="source">The probability source carrying the chain.</param>
        /// <param name="variable">The unified variable, for diagnostics.</param>
        /// <param name="chainBaseSeed">The variable's child-stream seed the clone streams fork from.</param>
        /// <param name="sampleSize">The realization count.</param>
        /// <param name="scheme">The sampling scheme.</param>
        /// <param name="dimension">The running parent-sampler dimension cursor.</param>
        /// <returns>The sampler-bound transform clones, in chain order.</returns>
        /// <exception cref="InvalidOperationException">Thrown when a chain entry is unresolved or cannot be set up.</exception>
        private IReadOnlyList<ITransformFunction> SetupTransformClones(ProbabilitySource source,
            FaultTreeVariableSlot variable, int chainBaseSeed, int sampleSize, SamplingScheme scheme,
            ref int dimension)
        {
            var clones = new ITransformFunction[source.HazardTransforms.Count];
            for (int i = 0; i < source.HazardTransforms.Count; i++)
            {
                ITransformFunction? transform = source.HazardTransforms[i];
                if (transform == null)
                {
                    throw new InvalidOperationException(
                        $"Fault-tree response '{Name}' cannot set up basic event " +
                        $"'{variable.SourceNode.Name}': the hazard transform at position {i} is missing " +
                        "or unresolved. Call Validate() and correct the reported errors.");
                }
                ITransformFunction clone;
                try
                {
                    clone = RiskFunctionFactory.CreateTransformFunction(transform.ToXElement())
                        ?? throw new InvalidOperationException(
                            $"Hazard transform '{transform.Name}' cannot be cloned for an independent occurrence.");
                    clone.SetupSampler(sampleSize,
                        SeedHelpers.HashCombine(chainBaseSeed, transform.CanonicalHash(), i), scheme);
                }
                catch (Exception ex) when (ex is ArgumentException
                    || ex is InvalidOperationException
                    || ex is NotSupportedException)
                {
                    throw new InvalidOperationException(
                        $"Fault-tree response '{Name}' could not set up basic event " +
                        $"'{variable.SourceNode.Name}' at canonical path " +
                        $"'{variable.FirstOccurrence!.CanonicalPath}' from hazard transform " +
                        $"'{transform.Name}': {ex.Message}", ex);
                }

                int cloneDimensions = clone.SamplingDimensions;
                RiskFunctionBase? cloneBase = clone as RiskFunctionBase;
                if (cloneDimensions > 0 && cloneBase == null)
                    throw new InvalidOperationException(
                        $"Hazard transform '{transform.Name}' does not expose the established " +
                        "RiskFunctionBase sampler state.");
                for (int realization = 0; realization < sampleSize; realization++)
                {
                    for (int cloneDimension = 0; cloneDimension < cloneDimensions; cloneDimension++)
                    {
                        _percentiles![realization, dimension + cloneDimension] =
                            cloneBase!.SampledPercentile(realization, cloneDimension);
                    }
                }
                dimension += cloneDimensions;
                clones[i] = clone;
            }
            return clones;
        }

        /// <summary>Adds a diagnostic once while preserving first-discovery order.</summary>
        /// <param name="messages">The accumulating diagnostics.</param>
        /// <param name="message">The candidate diagnostic.</param>
        private static void AddUnique(ICollection<string> messages, string message)
        {
            if (!messages.Contains(message)) messages.Add(message);
        }

        /// <summary>Throws when the response cannot be evaluated.</summary>
        /// <exception cref="InvalidOperationException">Thrown when validation reports errors.</exception>
        private void ThrowIfUnusable()
        {
            var validation = Validate();
            if (validation.IsValid) return;
            string errors = string.Join(" ", validation.ValidationMessages.Where(message =>
                message.StartsWith("Error:", StringComparison.Ordinal)));
            throw new InvalidOperationException(
                "The fault-tree response is invalid. Call Validate() and correct the reported " +
                $"errors before sampling. {errors}");
        }

        /// <summary>Ensures realization sampling uses the configuration that was set up.</summary>
        /// <exception cref="InvalidOperationException">Thrown when the sampler is stale.</exception>
        private void EnsureSamplerCurrent()
        {
            if (_samplerIdentity == null || !_samplerIdentity.AsSpan().SequenceEqual(CanonicalHash()))
                throw new InvalidOperationException("SetupSampler() must be called after the latest fault-tree compute edit and before sampling by realization index.");
        }

        /// <summary>Tests a response curve for non-decreasing failure probability.</summary>
        /// <param name="curve">The sampled curve.</param>
        /// <returns>True when no ordinate decreases.</returns>
        private static bool CurveIsMonotonic(OrderedPairedData curve)
        {
            for (int i = 1; i < curve.Count; i++)
                if (curve[i].Y < curve[i - 1].Y) return false;
            return true;
        }

        /// <summary>Clamps only floating-point roundoff immediately outside the unit interval.</summary>
        /// <param name="value">The computed top-event probability.</param>
        /// <returns>The unit-interval value.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the value is outside the tolerance band.</exception>
        private static double ClampRoundoff(double value)
        {
            if (value < 0d && value >= -ResponseBranchSample.ProbabilitySumTolerance) return 0d;
            if (value > 1d && value <= 1d + ResponseBranchSample.ProbabilitySumTolerance) return 1d;
            if (value < 0d || value > 1d)
                throw new InvalidOperationException("The fault-tree top-event probability fell outside [0, 1].");
            return value;
        }

        /// <summary>One unified variable's sampler binding.</summary>
        private sealed class SamplingBinding
        {
            /// <summary>Initializes one binding.</summary>
            /// <param name="localDimension">The local percentile column, or -1 for a referenced response.</param>
            /// <param name="responseFunction">The prepared setup clone, for referenced responses.</param>
            /// <param name="transforms">The sampler-bound hazard-transform clones, for transformed sources.</param>
            internal SamplingBinding(int localDimension, IResponseFunction? responseFunction,
                IReadOnlyList<ITransformFunction>? transforms = null)
            {
                LocalDimension = localDimension;
                ResponseFunction = responseFunction;
                Transforms = transforms;
            }

            /// <summary>The local percentile column, or -1 for a referenced response.</summary>
            internal int LocalDimension { get; }

            /// <summary>The prepared setup clone, for referenced responses.</summary>
            internal IResponseFunction? ResponseFunction { get; }

            /// <summary>The sampler-bound hazard-transform clones, or null for an untransformed source.</summary>
            internal IReadOnlyList<ITransformFunction>? Transforms { get; }
        }
    }
}
