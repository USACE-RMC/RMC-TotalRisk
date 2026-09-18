using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
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
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees.Nodes;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees.Support;

namespace RMC.TotalRisk.RiskFunctions.Responses.EventTrees
{
    /// <summary>
    /// A conditional-failure response defined by an authored event tree. Explicit sibling
    /// probabilities retain their legacy conditional meaning: sums above one are normalized
    /// proportionally, a remainder receives the residual, and terminal path products form the
    /// mutually exclusive end-state probabilities.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Supports scalar, uncertain-tabular, ordinary-response,
    /// and recursively nested event-tree probability sources together with internal/external
    /// link occurrences in both <see cref="Trees.TreeLinkMode"/> semantics: an independent-clone
    /// link forks its own sampling streams, while a shared-logical link unifies its occurrences
    /// onto the source limb's sampling classes so the limb samples and computes identically
    /// wherever it appears. Reads the recursive node XML emitted by the
    /// v1.0 product and writes only the explicit v1.1 graph form. Every nested occurrence
    /// participates in recursive sampling, two-mode serialization, projected hashing, and mixed
    /// source/link cycle diagnostics. Expanded graph ports are append-only. An instance-scoped
    /// immutable occurrence/evaluation plan is published once and invalidated through controlled
    /// revisions plus defensive live-content fingerprints; branch-address preparation remains lazy
    /// so identity-only reads do not mutate persistence state.
    /// </para>
    /// </remarks>
    public sealed class EventTreeResponse : ResponseFunctionBase, IBranchingResponseFunction,
        ITreeComputeSource, IProjectedIdentityResponse
    {
        /// <summary>Initializes an empty event-tree response over default hazards 0 and 1.</summary>
        public EventTreeResponse()
            : this(new[] { 0d, 1d }, new EventTree())
        {
        }

        /// <summary>Initializes an event-tree response.</summary>
        /// <param name="hazardLevels">The finite strictly ascending hazard axis.</param>
        /// <param name="eventTree">The controlled authored tree.</param>
        /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
        public EventTreeResponse(IEnumerable<double> hazardLevels, EventTree eventTree)
        {
            EventTree = eventTree ?? throw new ArgumentNullException(nameof(eventTree));
            EventTree.AttachOwner(this);
            SetHazardLevels(hazardLevels);
        }

        /// <summary>
        /// Restores an event-tree response from v1.1 explicit graph serialization or imports a
        /// legacy recursive event-node root/envelope into that representation.
        /// </summary>
        /// <param name="xElement">The current response or legacy recursive event tree.</param>
        /// <param name="resolver">The optional resolver for by-reference probability sources.</param>
        /// <exception cref="ArgumentNullException">Thrown when the element is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when required tree content is absent.</exception>
        public EventTreeResponse(XElement xElement, IRiskFunctionResolver? resolver = null)
        {
            if (xElement == null) throw new ArgumentNullException(nameof(xElement));
            xElement = LegacyEventTreeConverter.Normalize(xElement);
            ReadIdentityAttributes(xElement);
            SpecifiedHazard = SerializationUtilities.ReadString(xElement, nameof(SpecifiedHazard));
            HazardUnit = SerializationUtilities.ReadString(xElement, nameof(HazardUnit));

            var levels = xElement.Element(nameof(HazardLevels))?.Elements("Level")
                .Select(level => SerializationUtilities.ReadDouble(level, "Value"))
                ?? Enumerable.Empty<double>();
            _hazardLevels.AddRange(levels);

            XElement treeElement = xElement.Element(nameof(EventTree))
                ?? throw new InvalidOperationException("The serialized event-tree response has no EventTree element.");
            EventTree = new EventTree(treeElement, resolver, Name);
            EventTree.AttachOwner(this);
        }

        /// <summary>The stable id of the aggregate implicit non-failure branch.</summary>
        private static readonly Guid ImplicitBranchId = new Guid("f7bf9271-d0b5-5b8a-9b2a-2707517de6e6");

        /// <summary>Number of median-LHS realizations used for uncertainty summaries.</summary>
        private const int SummaryRealizations = 1000;

        /// <summary>Stable seed base used only for uncertainty-summary sampling on a clone.</summary>
        private const int SummarySeedBase = 18031986;

        /// <summary>The owned hazard levels.</summary>
        private readonly List<double> _hazardLevels = new List<double>();

        /// <summary>The cached read-only hazard view.</summary>
        private ReadOnlyCollection<double>? _readOnlyHazards;

        /// <summary>Occurrence-path sampling bindings created by the latest setup.</summary>
        private readonly Dictionary<string, SamplingBinding> _samplingBindings =
            new Dictionary<string, SamplingBinding>(StringComparer.Ordinal);

        /// <summary>The canonical identity under which the current sampler was set up.</summary>
        private byte[]? _samplerIdentity;

        /// <summary>Serializes plan publication, invalidation, and rollback restoration.</summary>
        private readonly object _compiledPlanSync = new object();

        /// <summary>The immutable instance-scoped plan published for concurrent readers.</summary>
        private CompiledEventTreePlan? _compiledPlan;

        /// <summary>The optional immutable branch-address preparation for the current plan.</summary>
        private CompiledBranchPlan? _compiledBranches;

        /// <summary>The controlled compute-state revision observed by dependent plans.</summary>
        private long _computeRevision;

        /// <summary>Instance-scoped diagnostic count of successfully published plans.</summary>
        private long _compiledPlanBuildCount;

        /// <summary>The controlled authored event tree.</summary>
        public EventTree EventTree { get; }

        /// <summary>The finite strictly ascending hazard axis.</summary>
        public IReadOnlyList<double> HazardLevels => _readOnlyHazards ??= _hazardLevels.AsReadOnly();

        /// <summary>Replaces the event-tree hazard axis atomically.</summary>
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

        /// <inheritdoc/>
        public override ResponseFunctionType FunctionType => ResponseFunctionType.EventTree;

        /// <inheritdoc/>
        public override bool IsDeterministic => GetCompiledPlan().Occurrences.IsDeterministic;

        /// <inheritdoc/>
        /// <remarks>
        /// Counts each unified sampling class once: a shared-logical limb contributes one
        /// dimension set no matter how many occurrences reference it, while every expanded
        /// independent-link occurrence owns a distinct local column and each referenced response
        /// class reports its full child dimension count.
        /// </remarks>
        public override int SamplingDimensions => GetCompiledPlan().Occurrences.SamplingDimensions;

        /// <inheritdoc/>
        /// <remarks>
        /// Local table dimensions are columns of this function's sampler, bound once per unified
        /// sampling class. Every referenced response class, including the first and every
        /// recursively nested event tree, is prepared on an isolated self-contained setup clone;
        /// shared-logical occurrences of one class reuse that clone so the limb draws once per
        /// realization. The parent copies the clone's exact flattened percentiles, so indexed
        /// sampling and LHS strata remain observable without mutating a live stored child. Setup
        /// is transactional: a compilation, clone, capacity, or child-setup failure restores the
        /// exact prior parent sampler state.
        /// </remarks>
        public override void SetupSampler(int sampleSize, int seed, SamplingScheme scheme)
        {
            int priorSampleSize = SampleSize;
            double[,]? priorPercentiles = _percentiles;
            byte[]? priorSamplerIdentity = _samplerIdentity;
            KeyValuePair<string, SamplingBinding>[] priorBindings = _samplingBindings.ToArray();
            try
            {
                ThrowIfUnusable();
                EventTreeOccurrencePlan plan = GetCompiledPlan().Occurrences;
                base.SetupSampler(sampleSize, seed, scheme);
                var nextBindings = new Dictionary<string, SamplingBinding>(StringComparer.Ordinal);
                var classBindings = new Dictionary<EventTreeSamplingClass, SamplingBinding?>(
                    ReferenceEqualityComparer.Instance);
                int dimension = 0;
                int childStreamOccurrence = 0;
                foreach (EventTreeOccurrenceNode occurrence in plan.CanonicalPreOrder)
                {
                    if (occurrence.SourceNode is not ChanceNode chance
                        || occurrence.SamplingClass == null) continue;
                    if (classBindings.TryGetValue(occurrence.SamplingClass,
                        out SamplingBinding? sharedBinding))
                    {
                        // A shared-logical occurrence reuses its class's binding: one dimension
                        // set, one referenced-response clone, one draw per realization.
                        if (sharedBinding != null)
                            nextBindings.Add(occurrence.CanonicalPath, sharedBinding);
                        continue;
                    }
                    ProbabilitySource source = chance.ProbabilitySource;
                    SamplingBinding? createdBinding = null;
                    if (source.Kind == ProbabilitySourceKind.UncertainTabular)
                    {
                        int localDimension = dimension;
                        dimension++;
                        IReadOnlyList<ITransformFunction>? tabularTransforms = null;
                        if (source.HasHazardTransforms)
                        {
                            byte[] sourceHash = Convert.FromHexString(
                                occurrence.ProbabilityIdentityToken);
                            int chainBaseSeed = SeedHelpers.HashCombine(seed, sourceHash, childStreamOccurrence++);
                            tabularTransforms = SetupTransformClones(source, occurrence, chainBaseSeed,
                                sampleSize, scheme, ref dimension);
                        }
                        createdBinding = new SamplingBinding(localDimension, null, tabularTransforms);
                        nextBindings.Add(occurrence.CanonicalPath, createdBinding);
                    }
                    else if (source.Kind == ProbabilitySourceKind.ResponseFunctionReference
                        && source.ResponseFunction != null)
                    {
                        IResponseFunction sampledFunction;
                        int childStreamSeed;
                        try
                        {
                            sampledFunction = CloneResponseFunction(source.ResponseFunction);
                            byte[] sourceHash = Convert.FromHexString(
                                occurrence.ProbabilityIdentityToken);
                            childStreamSeed = SeedHelpers.HashCombine(seed, sourceHash, childStreamOccurrence++);
                            sampledFunction.SetupSampler(sampleSize, childStreamSeed, scheme);
                        }
                        catch (Exception ex) when (ex is ArgumentException
                            || ex is InvalidOperationException
                            || ex is NotSupportedException)
                        {
                            throw new InvalidOperationException(
                                $"Event-tree response '{Name}' could not set up chance occurrence " +
                                $"'{occurrence.DisplayName}' at canonical path " +
                                $"'{occurrence.CanonicalPath}' from referenced response " +
                                $"'{source.ResponseFunction.Name}': {ex.Message}", ex);
                        }

                        int childDimensions = sampledFunction.SamplingDimensions;
                        if (childDimensions != occurrence.ProbabilitySamplingDimensions - source.HazardTransformDimensions)
                        {
                            throw new InvalidOperationException(
                                $"Event-tree response '{Name}' compiled chance occurrence " +
                                $"'{occurrence.DisplayName}' with {occurrence.ProbabilitySamplingDimensions} " +
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
                            referenceTransforms = SetupTransformClones(source, occurrence, childStreamSeed,
                                sampleSize, scheme, ref dimension);
                        }
                        createdBinding = new SamplingBinding(-1, sampledFunction, referenceTransforms);
                        nextBindings.Add(occurrence.CanonicalPath, createdBinding);
                    }
                    classBindings.Add(occurrence.SamplingClass, createdBinding);
                }
                if (dimension != plan.SamplingDimensions)
                {
                    throw new InvalidOperationException(
                        $"Event-tree response '{Name}' populated {dimension} sampler dimensions " +
                        $"for a compiled plan requiring {plan.SamplingDimensions}. Retry setup after " +
                        "all referenced functions are stable.");
                }
                _samplingBindings.Clear();
                foreach (var binding in nextBindings)
                    _samplingBindings.Add(binding.Key, binding.Value);
                _samplerIdentity = CanonicalHash(plan);
            }
            catch
            {
                SampleSize = priorSampleSize;
                _percentiles = priorPercentiles;
                _samplerIdentity = priorSamplerIdentity;
                _samplingBindings.Clear();
                foreach (var binding in priorBindings)
                    _samplingBindings.Add(binding.Key, binding.Value);
                throw;
            }
        }

        /// <inheritdoc/>
        public override (bool IsValid, List<string> ValidationMessages) Validate()
        {
            var messages = new List<string>();
            if (string.IsNullOrEmpty(SpecifiedHazard))
                messages.Add("Error: The event-tree response does not have a specified hazard type.");
            if (string.IsNullOrEmpty(HazardUnit))
                messages.Add("Error: The event-tree response does not have a specified hazard unit.");

            if (_hazardLevels.Count == 0)
            {
                messages.Add("Error: The event-tree response requires at least one hazard level.");
            }
            else
            {
                for (int i = 0; i < _hazardLevels.Count; i++)
                {
                    if (!double.IsFinite(_hazardLevels[i]))
                    {
                        messages.Add("Error: Event-tree hazard levels must be finite.");
                        break;
                    }
                    if (i > 0 && _hazardLevels[i] <= _hazardLevels[i - 1])
                    {
                        messages.Add("Error: Event-tree hazard levels must be strictly ascending.");
                        break;
                    }
                }
            }

            messages.AddRange(EventTree.Validate(_hazardLevels));
            if (EventTree.Nodes.Any(node => node.Id == ImplicitBranchId))
                messages.Add("Error: An event-tree node uses the reserved implicit-branch id.");

            EventTreeOccurrencePlan? plan = null;
            try
            {
                plan = GetCompiledPlan().Occurrences;
            }
            catch (InvalidOperationException ex)
            {
                AddUnique(messages, $"Error: {ex.Message}");
            }

            if (plan != null)
            {
                for (int i = 0; i < plan.Warnings.Count; i++) AddUnique(messages, plan.Warnings[i]);
                foreach (EventTreeOccurrenceNode occurrence in plan.CanonicalPreOrder)
                {
                    if (occurrence.SourceNode is not ChanceNode chance) continue;
                    foreach (string message in chance.ProbabilitySource.Validate(
                        occurrence.SourceFunction.HazardLevels, $"Chance node '{occurrence.DisplayName}'", "event-tree",
                        occurrence.SourceFunction.SpecifiedHazard))
                        AddUnique(messages, message);
                }

                if (messages.All(message => !message.StartsWith("Error:", StringComparison.Ordinal)))
                {
                    foreach (EventTreeOccurrenceNode parent in plan.CanonicalPreOrder)
                    {
                        if (parent.Children.Count == 0) continue;
                        int remainderCount = parent.Children.Count(child => child.SourceNode is RemainderNode);
                        if (remainderCount > 1)
                        {
                            AddUnique(messages,
                                $"Error: Expanded event-tree occurrence '{parent.DisplayName}' has more than one remainder branch.");
                            continue;
                        }

                        for (int h = 0; h < _hazardLevels.Count; h++)
                        {
                            double sum = 0d;
                            double compensation = 0d;
                            foreach (EventTreeOccurrenceNode child in parent.Children)
                            {
                                if (child.SourceNode is not ChanceNode) continue;
                                AddCompensated(ref sum, ref compensation,
                                    EvaluateSourceMean(child, _hazardLevels[h], h));
                            }
                            if (sum > 1d)
                            {
                                AddUnique(messages,
                                    $"Warning: Explicit branches of event-tree occurrence '{parent.DisplayName}' sum to {sum.ToString("G17", CultureInfo.InvariantCulture)} at hazard {_hazardLevels[h].ToString("G17", CultureInfo.InvariantCulture)} and will be normalized proportionally.");
                                break;
                            }
                        }
                    }
                }
            }

            return (messages.All(message => !message.StartsWith("Error:", StringComparison.Ordinal)), messages);
        }

        /// <inheritdoc/>
        public IReadOnlyList<ResponseBranchDescriptor> GetBranches()
        {
            return GetCompiledBranchPlan().CreateDescriptors();
        }

        /// <inheritdoc/>
        public ResponseBranchSample SampleBranches()
        {
            ThrowIfUnusable();
            return EvaluateBranches(SampleMode.Mean, 0d, -1);
        }

        /// <inheritdoc/>
        public ResponseBranchSample SampleBranches(double percentile)
        {
            if (percentile <= 0d || percentile >= 1d)
                throw new ArgumentOutOfRangeException(nameof(percentile), "The percentile must be between 0 and 1.");
            ThrowIfUnusable();
            return EvaluateBranches(SampleMode.Percentile, percentile, -1);
        }

        /// <inheritdoc/>
        public ResponseBranchSample SampleBranches(int realizationIndex)
        {
            ThrowIfUnusable();
            EnsureSamplerCurrent();
            if (realizationIndex < 0 || realizationIndex >= SampleSize)
                throw new ArgumentOutOfRangeException(nameof(realizationIndex));
            return EvaluateBranches(SampleMode.Realization, 0d, realizationIndex);
        }

        /// <inheritdoc/>
        public override OrderedPairedData SampleResponseFunction()
        {
            return AggregateFailureCurve(SampleBranches());
        }

        /// <inheritdoc/>
        public override OrderedPairedData SampleResponseFunction(double percentile)
        {
            return AggregateFailureCurve(SampleBranches(percentile));
        }

        /// <inheritdoc/>
        public override OrderedPairedData SampleResponseFunction(int realizationIndex)
        {
            return AggregateFailureCurve(SampleBranches(realizationIndex));
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
            if (_hazardLevels.Count == 0) throw new InvalidOperationException("The event-tree response has no hazard levels.");
            return _hazardLevels[0];
        }

        /// <inheritdoc/>
        public override double MaxHazard()
        {
            if (_hazardLevels.Count == 0) throw new InvalidOperationException("The event-tree response has no hazard levels.");
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

            var clone = new EventTreeResponse(ToXElement(RiskSerializationMode.SelfContained));
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

        /// <summary>Serializes the response and propagates the mode to probability-source references.</summary>
        /// <param name="mode">The serialization mode.</param>
        /// <returns>The serialized response.</returns>
        public XElement ToXElement(RiskSerializationMode mode)
        {
            _ = GetCompiledBranchPlan();
            var element = new XElement(nameof(EventTreeResponse));
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
            element.Add(EventTree.ToXElement(mode));
            return element;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Hashes a projected tree identity rather than persistent node/edge ids, display order,
        /// or nested reference markers. Equivalent self-contained and by-reference forms therefore
        /// share one seed identity.
        /// </remarks>
        public override byte[] CanonicalHash()
        {
            EventTreeOccurrencePlan plan = GetCompiledPlan().Occurrences;
            return CanonicalHash(plan);
        }

        /// <summary>Hashes a previously compiled plan without recursively recompiling source edges.</summary>
        /// <param name="plan">The occurrence plan compiled on the active cycle-detection stack.</param>
        /// <returns>The metadata-free SHA-256 canonical identity.</returns>
        internal byte[] CanonicalHash(EventTreeOccurrencePlan plan)
        {
            return CanonicalContentHasher.Hash(BuildIdentityXElement(plan),
                CanonicalizationRules.ModelRules);
        }

        /// <summary>Builds the projected identity form used when this response is nested in a component chain.</summary>
        /// <returns>The metadata- and persistence-free response identity.</returns>
        internal XElement ToIdentityXElement()
        {
            return BuildIdentityXElement(GetCompiledPlan().Occurrences);
        }

        /// <summary>Builds projected identity from a previously compiled occurrence plan.</summary>
        private XElement BuildIdentityXElement(EventTreeOccurrencePlan plan)
        {
            var identity = new XElement(nameof(EventTreeResponse));
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

        /// <summary>Evaluates every terminal and implicit branch over the tree hazard axis.</summary>
        /// <param name="mode">The source sampling mode.</param>
        /// <param name="percentile">The percentile when applicable.</param>
        /// <param name="realizationIndex">The realization index when applicable.</param>
        /// <returns>The exhaustive branch sample.</returns>
        private ResponseBranchSample EvaluateBranches(SampleMode mode, double percentile, int realizationIndex)
        {
            CompiledBranchPlan compiled = GetCompiledBranchPlan();
            IReadOnlyList<EventTreeEvaluationInstruction> instructions =
                compiled.Occurrences.EvaluationInstructions;
            ResponseBranchDescriptor[] descriptors = compiled.CreateDescriptors();
            var probabilities = new double[descriptors.Length][];
            for (int branch = 0; branch < probabilities.Length; branch++)
                probabilities[branch] = new double[_hazardLevels.Count];
            var masses = new double[instructions.Count];
            var rawProbabilities = new double[instructions.Count];

            for (int h = 0; h < _hazardLevels.Count; h++)
            {
                Array.Clear(masses);
                masses[0] = 1d;
                double implicitCompensation = 0d;
                for (int instructionIndex = 0; instructionIndex < instructions.Count;
                    instructionIndex++)
                {
                    EventTreeEvaluationInstruction instruction = instructions[instructionIndex];
                    EventTreeOccurrenceNode occurrence = instruction.Occurrence;
                    if (instruction.ChildCount == 0)
                    {
                        if (occurrence.SourceNode is not InitiatingNode)
                        {
                            int branchIndex = compiled.BranchIndexByPath[occurrence.CanonicalPath];
                            probabilities[branchIndex][h] = masses[instructionIndex];
                        }
                        continue;
                    }

                    double explicitSum = 0d;
                    double sumCompensation = 0d;
                    int remainderChild = -1;
                    for (int childOrdinal = 0; childOrdinal < instruction.ChildCount; childOrdinal++)
                    {
                        int childIndex = instruction.ChildIndex(childOrdinal);
                        EventTreeOccurrenceNode child = instructions[childIndex].Occurrence;
                        if (child.SourceNode is RemainderNode)
                        {
                            remainderChild = childIndex;
                            continue;
                        }
                        rawProbabilities[childIndex] = EvaluateSource(child,
                            _hazardLevels[h], h, mode, percentile, realizationIndex);
                        AddCompensated(ref explicitSum, ref sumCompensation,
                            rawProbabilities[childIndex]);
                    }

                    double scale = explicitSum > 1d ? 1d / explicitSum : 1d;
                    double residual = explicitSum < 1d ? 1d - explicitSum : 0d;
                    if (remainderChild < 0 && residual > 0d)
                    {
                        double contribution = masses[instructionIndex] * residual;
                        double current = probabilities[compiled.ImplicitBranchIndex][h];
                        AddCompensated(ref current, ref implicitCompensation, contribution);
                        probabilities[compiled.ImplicitBranchIndex][h] = current;
                    }
                    for (int childOrdinal = 0; childOrdinal < instruction.ChildCount; childOrdinal++)
                    {
                        int childIndex = instruction.ChildIndex(childOrdinal);
                        double conditional = childIndex == remainderChild
                            ? residual : rawProbabilities[childIndex] * scale;
                        masses[childIndex] = masses[instructionIndex] * conditional;
                    }
                }
            }

            return new ResponseBranchSample(_hazardLevels, descriptors, probabilities);
        }

        /// <summary>Evaluates one chance-node source in the selected sampling mode.</summary>
        /// <param name="occurrence">The expanded chance-node occurrence.</param>
        /// <param name="hazard">The caller's current hazard value.</param>
        /// <param name="hazardIndex">The hazard index.</param>
        /// <param name="mode">The sampling mode.</param>
        /// <param name="percentile">The shared percentile.</param>
        /// <param name="realizationIndex">The realization index.</param>
        /// <returns>The raw conditional probability.</returns>
        private double EvaluateSource(EventTreeOccurrenceNode occurrence, double hazard,
            int hazardIndex, SampleMode mode, double percentile, int realizationIndex)
        {
            var chance = (ChanceNode)occurrence.SourceNode;
            ProbabilitySource source = chance.ProbabilitySource;
            int sourceHazardIndex = occurrence.SourceFunction._hazardLevels.IndexOf(hazard);
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
                    // Realization-mode chain values come from the class's sampler-bound transform
                    // clones, so the transformed hazard is computed here and every lookup takes
                    // the off-axis route — the aligned index is undefined on a transformed axis.
                    SamplingBinding binding = _samplingBindings[occurrence.CanonicalPath];
                    IReadOnlyList<ITransformFunction> transforms = binding.Transforms
                        ?? throw new InvalidOperationException(
                            "The transformed occurrence has no sampler-bound hazard-transform clones.");
                    double transformed = ProbabilitySource.ApplyTransformRealizations(
                        transforms, hazard, realizationIndex);
                    if (source.BivariateAxis != null)
                    {
                        var surface = binding.ResponseFunction as IBivariateResponseFunction
                            ?? throw new InvalidOperationException(
                                "The bivariate-surface occurrence has no sampler binding.");
                        value = source.EvaluateBivariateSurfaceOn(surface, hazard, transformed);
                    }
                    else if (source.Kind == ProbabilitySourceKind.ResponseFunctionReference)
                    {
                        IResponseFunction sampledFunction = binding.ResponseFunction
                            ?? throw new InvalidOperationException("The referenced response occurrence has no sampler binding.");
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
                    IResponseFunction sampledFunction = _samplingBindings[occurrence.CanonicalPath].ResponseFunction
                        ?? throw new InvalidOperationException("The referenced response occurrence has no sampler binding.");
                    value = sampledFunction.SampleFunction(realizationIndex).CDF(hazard);
                }
                else
                {
                    double localPercentile = source.Kind == ProbabilitySourceKind.UncertainTabular
                        ? Percentile(realizationIndex, _samplingBindings[occurrence.CanonicalPath].LocalDimension)
                        : 0d;
                    value = aligned
                        ? source.EvaluateRealization(hazard, sourceHazardIndex, realizationIndex, localPercentile)
                        : source.EvaluateRealizationAtHazard(hazard, realizationIndex, localPercentile);
                }
            }
            else
            {
                throw new InvalidOperationException($"Unsupported event-tree sample mode '{mode}'.");
            }

            if (!double.IsFinite(value) || value < 0d || value > 1d)
                throw new InvalidOperationException($"Chance occurrence '{occurrence.DisplayName}' evaluated outside [0, 1]. Call Validate() and correct the source.");
            return value;
        }

        /// <summary>Evaluates one occurrence's mean source for validation.</summary>
        private double EvaluateSourceMean(EventTreeOccurrenceNode occurrence, double hazard, int hazardIndex)
        {
            return EvaluateSource(occurrence, hazard, hazardIndex, SampleMode.Mean, 0d, -1);
        }

        /// <summary>
        /// Evaluates one node-importance draw read-only against a published plan. Each expanded
        /// occurrence takes its own source percentile, with -1 selecting the source mean, and the
        /// caller receives the effective (post-normalization/residual) conditional probability and
        /// absolute path probability of every non-root occurrence, indexed by evaluation
        /// instruction.
        /// </summary>
        /// <param name="plan">The published immutable occurrence plan.</param>
        /// <param name="hazard">The analyzed authored hazard level.</param>
        /// <param name="hazardIndex">The authored hazard index.</param>
        /// <param name="occurrencePercentiles">Per-instruction source percentiles; -1 selects the mean.</param>
        /// <param name="effectiveConditionals">The receiving effective conditional probabilities.</param>
        /// <param name="pathProbabilities">The receiving absolute path probabilities.</param>
        /// <param name="rawProbabilities">The caller-owned raw-probability scratch array.</param>
        /// <returns>The aggregate failure probability at the analyzed hazard level.</returns>
        /// <exception cref="InvalidOperationException">Thrown when a source evaluates outside the unit interval.</exception>
        internal double EvaluateImportanceSample(EventTreeOccurrencePlan plan, double hazard,
            int hazardIndex, double[] occurrencePercentiles, double[] effectiveConditionals,
            double[] pathProbabilities, double[] rawProbabilities)
        {
            IReadOnlyList<EventTreeEvaluationInstruction> instructions = plan.EvaluationInstructions;
            double aggregate = 0d;
            double aggregateCompensation = 0d;
            pathProbabilities[0] = 1d;
            effectiveConditionals[0] = 1d;
            for (int instructionIndex = 0; instructionIndex < instructions.Count; instructionIndex++)
            {
                EventTreeEvaluationInstruction instruction = instructions[instructionIndex];
                if (instruction.ChildCount == 0)
                {
                    if (instruction.Occurrence.SourceNode is not InitiatingNode
                        && instruction.Occurrence.IsFailure)
                    {
                        AddCompensated(ref aggregate, ref aggregateCompensation,
                            pathProbabilities[instructionIndex]);
                    }
                    continue;
                }

                double explicitSum = 0d;
                double sumCompensation = 0d;
                int remainderChild = -1;
                for (int childOrdinal = 0; childOrdinal < instruction.ChildCount; childOrdinal++)
                {
                    int childIndex = instruction.ChildIndex(childOrdinal);
                    EventTreeOccurrenceNode child = instructions[childIndex].Occurrence;
                    if (child.SourceNode is RemainderNode)
                    {
                        remainderChild = childIndex;
                        continue;
                    }
                    rawProbabilities[childIndex] = EvaluateImportanceSource(child, hazard,
                        occurrencePercentiles[childIndex]);
                    AddCompensated(ref explicitSum, ref sumCompensation, rawProbabilities[childIndex]);
                }

                double scale = explicitSum > 1d ? 1d / explicitSum : 1d;
                double residual = explicitSum < 1d ? 1d - explicitSum : 0d;
                for (int childOrdinal = 0; childOrdinal < instruction.ChildCount; childOrdinal++)
                {
                    int childIndex = instruction.ChildIndex(childOrdinal);
                    double conditional = childIndex == remainderChild
                        ? residual : rawProbabilities[childIndex] * scale;
                    effectiveConditionals[childIndex] = conditional;
                    pathProbabilities[childIndex] = pathProbabilities[instructionIndex] * conditional;
                }
            }
            return ClampRoundoff(aggregate);
        }

        /// <summary>Evaluates one occurrence's source at an importance percentile or its mean.</summary>
        /// <param name="occurrence">The expanded chance-node occurrence.</param>
        /// <param name="hazard">The analyzed hazard value.</param>
        /// <param name="percentile">The source percentile, or -1 for the mean.</param>
        /// <returns>The raw conditional probability.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the source evaluates outside the unit interval.</exception>
        private static double EvaluateImportanceSource(EventTreeOccurrenceNode occurrence,
            double hazard, double percentile)
        {
            var chance = (ChanceNode)occurrence.SourceNode;
            ProbabilitySource source = chance.ProbabilitySource;
            int sourceHazardIndex = occurrence.SourceFunction._hazardLevels.IndexOf(hazard);
            bool aligned = sourceHazardIndex >= 0;
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
                throw new InvalidOperationException($"Chance occurrence '{occurrence.DisplayName}' evaluated outside [0, 1]. Call Validate() and correct the source.");
            return value;
        }

        /// <summary>Converts exhaustive branch probabilities to aggregate failure probability.</summary>
        /// <param name="sample">The exhaustive branch sample.</param>
        /// <returns>The aggregate hazard/failure-probability curve.</returns>
        private OrderedPairedData AggregateFailureCurve(ResponseBranchSample sample)
        {
            CompiledBranchPlan compiled = GetCompiledBranchPlan();
            EventTreeOccurrencePlan plan = compiled.Occurrences;

            var failure = new double[sample.Hazards.Count];
            for (int h = 0; h < failure.Length; h++)
            {
                double compensation = 0d;
                foreach (EventTreeOccurrenceNode leaf in plan.Leaves)
                {
                    if (!leaf.IsFailure) continue;
                    int branch = compiled.BranchIndexByPath[leaf.CanonicalPath];
                    AddCompensated(ref failure[h], ref compensation, sample.Probabilities[branch][h]);
                }
                failure[h] = ClampRoundoff(failure[h]);
            }
            return new OrderedPairedData(sample.Hazards.ToArray(), failure,
                true, SortOrder.Ascending, false, SortOrder.None);
        }

        /// <summary>Builds stable direct and linked terminal descriptors for one occurrence plan.</summary>
        private IReadOnlyList<BranchBinding> BuildBranchBindings(EventTreeOccurrencePlan plan)
        {
            var result = new List<BranchBinding>(plan.Leaves.Count + 1)
            {
                new BranchBinding(null, ImplicitBranchId, 2),
            };
            var usedIds = new HashSet<Guid> { ImplicitBranchId };
            foreach (EventTreeOccurrenceNode leaf in plan.Leaves)
            {
                Guid branchId;
                int outputPort;
                if (EventTree.TryGetPreservedBranchAddress(leaf.PersistencePath,
                    out branchId, out outputPort))
                {
                    if (!usedIds.Add(branchId))
                        throw new InvalidOperationException(
                            $"Expanded event-tree branch id '{branchId:D}' resolves to more than one terminal occurrence.");
                }
                else if (!leaf.IsLinkedOccurrence)
                {
                    branchId = leaf.SourceNode.Id;
                    outputPort = leaf.SourceNode.OutputPort;
                    if (!usedIds.Add(branchId))
                        throw new InvalidOperationException(
                            $"Expanded event-tree branch id '{branchId:D}' is not unique.");
                }
                else
                {
                    int salt = 0;
                    do
                    {
                        branchId = CreateLinkedBranchId(leaf.PersistencePath, salt++);
                    }
                    while (!usedIds.Add(branchId));
                    outputPort = EventTree.GetOrAssignLinkedBranchPort(branchId,
                        leaf.PersistencePath);
                }

                result.Add(new BranchBinding(leaf, branchId, outputPort));
            }
            return result.OrderBy(binding => binding.OutputPort).ToArray();
        }

        /// <summary>Gets the metadata-free projected identity of one selected branch.</summary>
        /// <param name="branchId">The stable branch id.</param>
        /// <returns>The canonical occurrence path, or the implicit-unmodeled token.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the branch is stale.</exception>
        internal string GetBranchIdentityToken(Guid branchId)
        {
            if (branchId == ImplicitBranchId) return "ImplicitUnmodeled";
            BranchBinding? binding = GetCompiledBranchPlan().Bindings
                .FirstOrDefault(item => item.BranchId == branchId);
            if (binding?.Occurrence == null)
                throw new InvalidOperationException(
                    $"Event-tree response '{Name}' does not expose branch '{branchId:D}'.");
            return string.Join("/", binding.Occurrence.CanonicalPath.Split('/').Select(segment =>
            {
                int separator = segment.LastIndexOf(':');
                return separator < 0 ? segment : segment.Substring(0, separator);
            }));
        }

        /// <summary>Creates a stable persisted-occurrence branch id without placing ids on the hash surface.</summary>
        private static Guid CreateLinkedBranchId(string persistencePath, int salt)
        {
            var identity = new XElement("LinkedEventTreeBranch");
            identity.SetAttributeValue("PersistencePath", persistencePath);
            identity.SetAttributeValue("Salt", salt.ToString(CultureInfo.InvariantCulture));
            byte[] hash = CanonicalContentHasher.Hash(identity, CanonicalizationRules.ModelRules);
            var bytes = new byte[16];
            Array.Copy(hash, bytes, bytes.Length);
            return new Guid(bytes);
        }
        /// <summary>Creates an isolated self-contained response occurrence for setup.</summary>
        private static IResponseFunction CloneResponseFunction(IResponseFunction source)
        {
            return RiskFunctionFactory.CreateResponseFunction(source.ToXElement())
                ?? throw new InvalidOperationException(
                    $"Referenced response function '{source.Name}' cannot be cloned for an independent occurrence.");
        }

        /// <summary>
        /// Clones one source's hazard-transform chain onto isolated self-contained setup
        /// instances, seeds each clone from the class's child-stream base combined with the
        /// transform's content hash and chain position, and copies the clones' exact flattened
        /// percentiles into this sampler after the source's own dimensions.
        /// </summary>
        /// <param name="source">The probability source carrying the chain.</param>
        /// <param name="occurrence">The expanded occurrence, for diagnostics.</param>
        /// <param name="chainBaseSeed">The class's child-stream seed the clone streams fork from.</param>
        /// <param name="sampleSize">The realization count.</param>
        /// <param name="scheme">The sampling scheme.</param>
        /// <param name="dimension">The running parent-sampler dimension cursor.</param>
        /// <returns>The sampler-bound transform clones, in chain order.</returns>
        /// <exception cref="InvalidOperationException">Thrown when a chain entry is unresolved or cannot be set up.</exception>
        private IReadOnlyList<ITransformFunction> SetupTransformClones(ProbabilitySource source,
            EventTreeOccurrenceNode occurrence, int chainBaseSeed, int sampleSize, SamplingScheme scheme,
            ref int dimension)
        {
            var clones = new ITransformFunction[source.HazardTransforms.Count];
            for (int i = 0; i < source.HazardTransforms.Count; i++)
            {
                ITransformFunction? transform = source.HazardTransforms[i];
                if (transform == null)
                {
                    throw new InvalidOperationException(
                        $"Event-tree response '{Name}' cannot set up chance occurrence " +
                        $"'{occurrence.DisplayName}': the hazard transform at position {i} is missing " +
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
                        $"Event-tree response '{Name}' could not set up chance occurrence " +
                        $"'{occurrence.DisplayName}' at canonical path '{occurrence.CanonicalPath}' " +
                        $"from hazard transform '{transform.Name}': {ex.Message}", ex);
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
        private static void AddUnique(ICollection<string> messages, string message)
        {
            if (!messages.Contains(message)) messages.Add(message);
        }

        /// <summary>Returns the current immutable plan, compiling and publishing it once when stale.</summary>
        private CompiledEventTreePlan GetCompiledPlan()
        {
            while (true)
            {
                CompiledEventTreePlan? current = Volatile.Read(ref _compiledPlan);
                if (current != null)
                {
                    if (current.Occurrences.Dependencies.IsCurrent()) return current;
                    TryInvalidateCompiledPlan(current, true);
                    continue;
                }

                CompiledEventTreePlan? candidate = null;
                lock (_compiledPlanSync)
                {
                    if (_compiledPlan != null) continue;
                    EventTreeOccurrencePlan occurrences = EventTreeOccurrencePlan.Compile(this);
                    candidate = new CompiledEventTreePlan(occurrences);
                    if (!occurrences.Dependencies.IsCurrent()) continue;
                    occurrences.Dependencies.Attach(this);
                    if (!occurrences.Dependencies.IsCurrent())
                    {
                        occurrences.Dependencies.Detach(this);
                        continue;
                    }
                    Volatile.Write(ref _compiledPlan, candidate);
                    Interlocked.Increment(ref _compiledPlanBuildCount);
                }

                if (candidate.Occurrences.Dependencies.IsCurrent()) return candidate;
                TryInvalidateCompiledPlan(candidate, true);
            }
        }

        /// <summary>Returns immutable branch addressing prepared once for the current occurrence plan.</summary>
        private CompiledBranchPlan GetCompiledBranchPlan()
        {
            while (true)
            {
                CompiledEventTreePlan plan = GetCompiledPlan();
                CompiledBranchPlan? current = Volatile.Read(ref _compiledBranches);
                if (current != null && ReferenceEquals(current.Occurrences, plan.Occurrences))
                    return current;

                lock (_compiledPlanSync)
                {
                    plan = GetCompiledPlan();
                    current = _compiledBranches;
                    if (current != null && ReferenceEquals(current.Occurrences, plan.Occurrences))
                        return current;

                    var candidate = new CompiledBranchPlan(plan.Occurrences,
                        BuildBranchBindings(plan.Occurrences));
                    if (!ReferenceEquals(_compiledPlan, plan)) continue;
                    Volatile.Write(ref _compiledBranches, candidate);
                    return candidate;
                }
            }
        }

        /// <summary>Returns the cached occurrence plan to the owning tree's inspection surface.</summary>
        internal EventTreeOccurrencePlan GetOccurrencePlan()
        {
            return GetCompiledPlan().Occurrences;
        }

        /// <summary>The controlled compute revision observed by dependent event-tree plans.</summary>
        internal long ComputeRevision => Interlocked.Read(ref _computeRevision);

        /// <summary>Raised internally after this response's compute state becomes stale.</summary>
        internal event EventHandler? ComputeStateChanged;

        /// <summary>
        /// The number of plans this instance has published. This internal diagnostic seam proves
        /// reuse/invalidation without exposing cache mechanics in the public model API.
        /// </summary>
        internal long CompiledPlanBuildCount => Interlocked.Read(ref _compiledPlanBuildCount);

        /// <summary>The current expanded instruction count for tests and the performance harness.</summary>
        internal int CompiledInstructionCount => GetCompiledPlan().Occurrences.EvaluationInstructions.Count;

        /// <summary>The current expanded edge count for tests and the performance harness.</summary>
        internal int CompiledEdgeCount => GetCompiledPlan().Occurrences.ExpandedEdgeCount;

        /// <summary>An opaque identity for proving that repeated reads reuse one immutable plan.</summary>
        internal object CompiledPlanIdentity => GetCompiledPlan();

        /// <summary>Invalidates this instance and propagates compute staleness to dependent owners.</summary>
        /// <param name="raisePropertyChange">Whether to notify ordinary function observers.</param>
        private void InvalidateCompiledPlan(bool raisePropertyChange)
        {
            lock (_compiledPlanSync)
            {
                CompiledEventTreePlan? current = _compiledPlan;
                current?.Occurrences.Dependencies.Detach(this);
                Volatile.Write(ref _compiledPlan, null);
                Volatile.Write(ref _compiledBranches, null);
                Interlocked.Increment(ref _computeRevision);
            }
            ComputeStateChanged?.Invoke(this, EventArgs.Empty);
            if (raisePropertyChange) RaisePropertyChange(nameof(EventTree));
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
        private bool TryInvalidateCompiledPlan(CompiledEventTreePlan expected,
            bool raisePropertyChange)
        {
            lock (_compiledPlanSync)
            {
                if (!ReferenceEquals(_compiledPlan, expected)) return false;
                expected.Occurrences.Dependencies.Detach(this);
                Volatile.Write(ref _compiledPlan, null);
                Volatile.Write(ref _compiledBranches, null);
                Interlocked.Increment(ref _computeRevision);
            }
            ComputeStateChanged?.Invoke(this, EventArgs.Empty);
            if (raisePropertyChange) RaisePropertyChange(nameof(EventTree));
            return true;
        }

        /// <summary>Invalidates when a nested or external event-tree dependency changes.</summary>
        internal void DependencyEventTreeChanged(object? sender, EventArgs e)
        {
            InvalidateCompiledPlan(true);
        }

        /// <summary>Invalidates when a directly or recursively owned uncertain table changes membership.</summary>
        internal void DependencyTableCollectionChanged(object? sender,
            NotifyCollectionChangedEventArgs e)
        {
            InvalidateCompiledPlan(true);
        }

        /// <summary>Invalidates for compute-relevant edits to an ordinary referenced response.</summary>
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
            DependencyEventTreeChanged(sender, e);
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
        internal object CreateCompiledPlanCheckpoint()
        {
            lock (_compiledPlanSync)
                return new CompiledPlanCheckpoint(_compiledPlan, _compiledBranches,
                    _computeRevision, _compiledPlanBuildCount);
        }

        /// <summary>Restores the exact owner cache/revision state after authoring rollback.</summary>
        internal void RestoreCompiledPlanCheckpoint(object checkpoint)
        {
            if (checkpoint is not CompiledPlanCheckpoint state)
                throw new ArgumentException("The event-tree compiled-plan checkpoint is invalid.",
                    nameof(checkpoint));
            lock (_compiledPlanSync)
            {
                _compiledPlan?.Occurrences.Dependencies.Detach(this);
                _computeRevision = state.ComputeRevision;
                _compiledPlan = state.Plan;
                _compiledBranches = state.Branches;
                _compiledPlanBuildCount = state.PlanBuildCount;
                _compiledPlan?.Occurrences.Dependencies.Attach(this);
            }
        }

        /// <summary>One exact cache/revision authoring checkpoint.</summary>
        private sealed class CompiledPlanCheckpoint
        {
            /// <summary>Initializes a checkpoint from the pre-mutation cache state.</summary>
            internal CompiledPlanCheckpoint(CompiledEventTreePlan? plan,
                CompiledBranchPlan? branches, long computeRevision, long planBuildCount)
            {
                Plan = plan;
                Branches = branches;
                ComputeRevision = computeRevision;
                PlanBuildCount = planBuildCount;
            }

            /// <summary>The pre-mutation published plan, when one exists.</summary>
            internal CompiledEventTreePlan? Plan { get; }

            /// <summary>The pre-mutation branch-address plan, when already prepared.</summary>
            internal CompiledBranchPlan? Branches { get; }

            /// <summary>The pre-mutation compute revision.</summary>
            internal long ComputeRevision { get; }

            /// <summary>The pre-mutation diagnostic publication count.</summary>
            internal long PlanBuildCount { get; }
        }

        /// <summary>An immutable published occurrence/evaluation plan.</summary>
        private sealed class CompiledEventTreePlan
        {
            /// <summary>Initializes a published plan over one expanded occurrence plan.</summary>
            internal CompiledEventTreePlan(EventTreeOccurrencePlan occurrences)
            {
                Occurrences = occurrences;
            }

            /// <summary>The immutable expanded occurrence plan.</summary>
            internal EventTreeOccurrencePlan Occurrences { get; }
        }

        /// <summary>
        /// Immutable branch addressing prepared independently so identity-only reads retain their
        /// pre-caching serialization behavior.
        /// </summary>
        private sealed class CompiledBranchPlan
        {
            /// <summary>Initializes the immutable branch addressing over an expanded plan.</summary>
            internal CompiledBranchPlan(EventTreeOccurrencePlan occurrences,
                IReadOnlyList<BranchBinding> bindings)
            {
                Occurrences = occurrences;
                Bindings = Array.AsReadOnly(bindings.ToArray());
                var branchIndexByPath = new Dictionary<string, int>(StringComparer.Ordinal);
                int implicitIndex = -1;
                for (int i = 0; i < Bindings.Count; i++)
                {
                    if (Bindings[i].Occurrence == null)
                        implicitIndex = i;
                    else
                        branchIndexByPath.Add(Bindings[i].Occurrence!.CanonicalPath, i);
                }
                if (implicitIndex < 0)
                    throw new InvalidOperationException("The compiled event tree has no implicit branch address.");
                ImplicitBranchIndex = implicitIndex;
                BranchIndexByPath = new ReadOnlyDictionary<string, int>(
                    branchIndexByPath);
            }

            /// <summary>The expanded occurrence plan the addresses were prepared from.</summary>
            internal EventTreeOccurrencePlan Occurrences { get; }

            /// <summary>The immutable branch bindings in output-port order.</summary>
            internal IReadOnlyList<BranchBinding> Bindings { get; }

            /// <summary>Maps each modeled occurrence's canonical path to its binding index.</summary>
            internal IReadOnlyDictionary<string, int> BranchIndexByPath { get; }

            /// <summary>The index of the implicit (unmodeled) branch binding.</summary>
            internal int ImplicitBranchIndex { get; }

            /// <summary>Creates descriptors with current metadata over immutable addresses.</summary>
            internal ResponseBranchDescriptor[] CreateDescriptors()
            {
                var descriptors = new ResponseBranchDescriptor[Bindings.Count];
                for (int i = 0; i < Bindings.Count; i++)
                    descriptors[i] = Bindings[i].CreateDescriptor();
                return descriptors;
            }
        }

        /// <summary>One realization-sampling binding keyed by canonical occurrence path.</summary>
        private sealed class SamplingBinding
        {
            /// <summary>Initializes a realization-sampling binding.</summary>
            internal SamplingBinding(int localDimension, IResponseFunction? responseFunction,
                IReadOnlyList<ITransformFunction>? transforms = null)
            {
                LocalDimension = localDimension;
                ResponseFunction = responseFunction;
                Transforms = transforms;
            }

            /// <summary>The tree-local knowledge dimension backing an uncertain-tabular percentile lookup.</summary>
            internal int LocalDimension { get; }

            /// <summary>The referenced response function, or null for non-response sources.</summary>
            internal IResponseFunction? ResponseFunction { get; }

            /// <summary>The sampler-bound hazard-transform clones, or null for an untransformed source.</summary>
            internal IReadOnlyList<ITransformFunction>? Transforms { get; }
        }

        /// <summary>Associates one expanded terminal occurrence with its public branch descriptor.</summary>
        private sealed class BranchBinding
        {
            /// <summary>Initializes a branch binding.</summary>
            internal BranchBinding(EventTreeOccurrenceNode? occurrence, Guid branchId,
                int outputPort)
            {
                Occurrence = occurrence;
                BranchId = branchId;
                OutputPort = outputPort;
            }

            /// <summary>The expanded terminal occurrence, or null for the implicit unmodeled branch.</summary>
            internal EventTreeOccurrenceNode? Occurrence { get; }

            /// <summary>The stable public branch identity.</summary>
            internal Guid BranchId { get; }

            /// <summary>The graph output port reserved for the branch.</summary>
            internal int OutputPort { get; }

            /// <summary>Creates a descriptor with the current compute-inert display name.</summary>
            internal ResponseBranchDescriptor CreateDescriptor()
            {
                return Occurrence == null
                    ? new ResponseBranchDescriptor(BranchId, "Unmodeled", false, OutputPort)
                    : new ResponseBranchDescriptor(BranchId, Occurrence.DisplayName,
                        Occurrence.IsFailure, OutputPort);
            }
        }

        /// <summary>Throws when validation reports any error.</summary>
        private void ThrowIfUnusable()
        {
            var validation = Validate();
            if (validation.IsValid) return;
            string errors = string.Join(" ", validation.ValidationMessages.Where(message =>
                message.StartsWith("Error:", StringComparison.Ordinal)));
            throw new InvalidOperationException(
                "The event-tree response is invalid. Call Validate() and correct the reported " +
                $"errors before sampling. {errors}");
        }

        /// <summary>Ensures realization sampling uses the configuration that was set up.</summary>
        private void EnsureSamplerCurrent()
        {
            if (_samplerIdentity == null || !_samplerIdentity.AsSpan().SequenceEqual(CanonicalHash()))
                throw new InvalidOperationException("SetupSampler() must be called after the latest event-tree compute edit and before sampling by realization index.");
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

        /// <summary>Adds one value using Kahan compensated summation.</summary>
        /// <param name="sum">The running sum.</param>
        /// <param name="compensation">The running compensation.</param>
        /// <param name="value">The addend.</param>
        private static void AddCompensated(ref double sum, ref double compensation, double value)
        {
            double adjusted = value - compensation;
            double next = sum + adjusted;
            compensation = (next - sum) - adjusted;
            sum = next;
        }

        /// <summary>Clamps only floating-point roundoff immediately outside the unit interval.</summary>
        /// <param name="value">The computed aggregate probability.</param>
        /// <returns>The unit-interval value.</returns>
        private static double ClampRoundoff(double value)
        {
            if (value < 0d && value >= -ResponseBranchSample.ProbabilitySumTolerance) return 0d;
            if (value > 1d && value <= 1d + ResponseBranchSample.ProbabilitySumTolerance) return 1d;
            if (value < 0d || value > 1d)
                throw new InvalidOperationException("The event-tree aggregate probability fell outside [0, 1].");
            return value;
        }
    }
}
