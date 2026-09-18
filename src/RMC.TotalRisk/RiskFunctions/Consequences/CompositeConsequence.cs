using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Numerics.Data.Statistics;
using Numerics.Distributions;
using Numerics.Functions;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.RiskFunctions.Support;

namespace RMC.TotalRisk.RiskFunctions.Consequences
{
    /// <summary>
    /// A composite consequence function: combines a weighted list of child consequence functions
    /// as a sum (<see cref="CompositeFunctionType.Additive"/>), a weighted average
    /// (<see cref="CompositeFunctionType.Average"/>), a mixture that samples one child per
    /// realization (<see cref="CompositeFunctionType.Mixture"/>, the default) — the day/night
    /// exposure model, with the weight as the probability of each exposure scenario — or an
    /// epistemic mixture (<see cref="CompositeFunctionType.EpistemicMixture"/>), the logic tree
    /// over alternative consequence models of which exactly one is true.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Ported from v1.0 <c>CompositeConsequence</c> with the combination semantics preserved:
    /// Mixture picks one child per realization by cumulative weight (over the ensemble, a true
    /// mixture distribution — same mean as Average, larger variance); the mean-curve overload has
    /// no mixture branch (Average and Mixture both produce Σwᵢ·fᵢ, Additive produces Σfᵢ); and
    /// combined consequences clamp at zero. The v1.0 percentile-reseeded <c>Random</c> is replaced
    /// by the deterministic sampler contract: children draw from their own content-seeded
    /// samplers. Structural wiring follows the sibling BestFit <c>CompositeAnalysis</c> (live
    /// referenced children in an observable weighted collection; reference-only stored
    /// serialization), improving on it where it has documented warts: entries write complete
    /// <c>FunctionReference</c> markers themselves, resolution is id-authoritative, and an
    /// unresolvable reference keeps its entry (weight preserved, reported by
    /// <see cref="Validate"/>) instead of silently dropping it. The v1.0 child-level
    /// <c>HazardTransform</c>/<c>ConsequenceTransform</c> overrides are dropped: children are live
    /// functions that own their interpolation transforms.
    /// </para>
    /// <para>
    /// <b>Exposure branches (docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §6.4.1):</b> the
    /// mixture weights are
    /// aleatory exposure probabilities, so the risk engine never draws a branch — it enumerates
    /// the weighted branches through <see cref="SampleExposureBranches()"/> at every hazard point,
    /// in the mean-only and full Monte Carlo paths alike, and each realization's loss-exceedance
    /// curve carries the full day/night spread. <see cref="SamplingDimensions"/> is therefore
    /// zero: no selector dimension is declared, and the standalone per-realization mixture
    /// surface (<see cref="SampleFunction(int)"/>, the uncertainty summary) rides an internal
    /// selector matrix generated with the seed fold and scheme a declared selector dimension
    /// would use, so the standalone and engine formulations can never drift apart.
    /// </para>
    /// <para>
    /// <b>EpistemicMixture is the logic tree,</b> distinct from the aleatory Mixture above: the
    /// weights are the analyst's credence that each child is the true consequence model, so the
    /// engine draws <i>one</i> branch per realization instead of enumerating them —
    /// <see cref="SampleExposureBranches(double)"/> selects by the failure mode's coupling
    /// percentile (the consequence cluster's knowledge channel; consequences are deliberately
    /// not in the sampler walk) and returns the selected child's own branches, so a nested
    /// aleatory mixture still enumerates inside the chosen branch.
    /// <see cref="SamplingDimensions"/> stays zero, and a shared epistemic variable cannot be
    /// bound here — the coupling matrix is per failure mode, not per function — which is the
    /// recorded companion limitation to consequence fractile pinning. The mean overloads return
    /// the weighted blend, which is exact for mean risk (consequences enter it linearly); a
    /// mean-only analysis over an epistemic consequence composite carries only the spread loss
    /// and is therefore a validation warning rather than an error.
    /// </para>
    /// <para>
    /// <b>Serialization:</b> under <see cref="RiskSerializationMode.SelfContained"/> (the default,
    /// for storeless contexts: headless callers, oracles, and failure-mode projection XML) child
    /// content is written inline; under <see cref="RiskSerializationMode.ByReference"/> (the
    /// stored form) each entry carries only its weight and a <c>FunctionReference</c> marker, so a
    /// store never duplicates child function content. <b>Hashing</b> uses a projected identity
    /// form — the combine mode, the entry count, and per entry the effective weight and the
    /// child's own canonical hash — never the persisted form, so the serialization mode and child
    /// metadata can never move this function's hash (the <c>SystemComponent</c>
    /// identity-form exception). The persisted form must therefore be treated as append-only
    /// contract like any other, but it is not this type's hash surface. Cyclic composites cannot
    /// be serialized or hashed; <see cref="Validate"/> reports them and every compute entry point
    /// checks first.
    /// </para>
    /// </remarks>
    public class CompositeConsequence : ConsequenceFunctionBase
    {
        #region Construction

        /// <summary>
        /// Initializes an empty composite with the v1.0 defaults (Mixture mode, no children).
        /// </summary>
        public CompositeConsequence()
        {
            ConsequenceFunctions = new ObservableCollection<WeightedConsequenceFunction>();
        }

        /// <summary>
        /// Initializes a composite over the specified weighted children (Mixture mode).
        /// </summary>
        /// <param name="consequenceFunctions">The weighted child entries.</param>
        /// <exception cref="ArgumentNullException">Thrown when the sequence is null.</exception>
        public CompositeConsequence(IEnumerable<WeightedConsequenceFunction> consequenceFunctions)
        {
            if (consequenceFunctions == null) throw new ArgumentNullException(nameof(consequenceFunctions));
            ConsequenceFunctions = new ObservableCollection<WeightedConsequenceFunction>(consequenceFunctions);
        }

        /// <summary>
        /// Restores a composite consequence function from its serialized form.
        /// </summary>
        /// <param name="xElement">The serialized form produced by <see cref="ToXElement(RiskSerializationMode)"/>.</param>
        /// <param name="resolver">
        /// The function resolver, required only to read a by-reference form. An unresolvable
        /// name reference keeps its weighted entry with a null function — the weight survives the
        /// round-trip — and is reported by <see cref="Validate"/>.
        /// </param>
        /// <exception cref="ArgumentNullException">Thrown when the element is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when inline child content cannot be reconstructed (dropping it would lose model
        /// content on the next save), or when a serialized reference id is stale.
        /// </exception>
        public CompositeConsequence(XElement xElement, IRiskFunctionResolver? resolver = null)
        {
            if (xElement == null) throw new ArgumentNullException(nameof(xElement));

            ReadIdentityAttributes(xElement);
            SpecifiedHazard = SerializationUtilities.ReadString(xElement, nameof(SpecifiedHazard));
            HazardUnit = SerializationUtilities.ReadString(xElement, nameof(HazardUnit));
            SpecifiedConsequence = SerializationUtilities.ReadString(xElement, nameof(SpecifiedConsequence));
            ConsequenceUnit = SerializationUtilities.ReadString(xElement, nameof(ConsequenceUnit));
            _compositeFunctionType = SerializationUtilities.ReadEnum(xElement, nameof(CompositeFunctionType), CompositeFunctionType.Mixture);

            ConsequenceFunctions = new ObservableCollection<WeightedConsequenceFunction>();
            var container = xElement.Element(nameof(ConsequenceFunctions));
            if (container != null)
            {
                foreach (var entryElement in container.Elements(nameof(WeightedConsequenceFunction)))
                {
                    double weight = SerializationUtilities.ReadDouble(entryElement, nameof(WeightedConsequenceFunction.Weight));
                    IConsequenceFunction? function = null;
                    var child = entryElement.Elements().FirstOrDefault();
                    if (child != null)
                    {
                        // The resolver threads into the inline factory so an inline nested
                        // composite can resolve its own by-reference children.
                        function = FunctionEntry.Read<IConsequenceFunction>(
                            child, resolver, c => RiskFunctionFactory.CreateFromXElement(c, resolver),
                            Name, $"The {nameof(CompositeConsequence)} '{Name}'", "consequence function",
                            _unresolvedFunctionReferences);
                    }

                    // The entry is kept even when the function is null (no child serialized, or an
                    // unresolvable reference): dropping it would silently change the weight list —
                    // and therefore the hash and the weight-sum validation — on the next save.
                    ConsequenceFunctions.Add(new WeightedConsequenceFunction(function, weight));
                }
            }
        }

        #endregion

        #region Members

        /// <summary>
        /// The number of internal realizations behind <see cref="ComputeUncertaintyResults"/>.
        /// </summary>
        private const int SummaryRealizations = 10_000;

        /// <summary>
        /// The fixed base seed folded with the canonical content hash to derive the
        /// <see cref="ComputeUncertaintyResults"/> sampling seed — content-based, so the summary
        /// is deterministic for identical compute content and independent of names or ids.
        /// </summary>
        private const int SummarySeedBase = 12345;

        /// <summary>
        /// The tolerance on the weight sum for the non-Additive modes — the same gate
        /// <see cref="Numerics.Distributions.Mixture"/> applies to its weights. Deliberately looser
        /// than machine epsilon so user-entered decimal weights (0.42/0.58) validate.
        /// </summary>
        private const double WeightSumTolerance = 1e-8;

        /// <summary>
        /// Backing field for <see cref="ConsequenceFunctions"/>. Assigned through the property by
        /// every constructor, so the collection subscription is always attached.
        /// </summary>
        private ObservableCollection<WeightedConsequenceFunction> _consequenceFunctions = null!;

        /// <summary>
        /// Backing field for <see cref="CompositeFunctionType"/> — the v1.0 default is Mixture.
        /// </summary>
        private CompositeFunctionType _compositeFunctionType = CompositeFunctionType.Mixture;

        /// <summary>
        /// The internal N×1 mixture-selector matrix behind the standalone per-realization surface
        /// (<see cref="SampleFunction(int)"/> and the uncertainty summary); allocated by
        /// <see cref="SetupSampler"/> in the Mixture and EpistemicMixture modes, null otherwise.
        /// </summary>
        /// <remarks>
        /// Under the exposure-branch contract the selector is not an engine sampling dimension
        /// (<see cref="SamplingDimensions"/> is zero — the risk engine enumerates branches through
        /// <see cref="SampleExposureBranches()"/> instead of drawing one), but the standalone
        /// ensemble surface keeps its meaning: a sweep of <see cref="SampleFunction(int)"/> over a
        /// set-up sampler reproduces the exact mixture ensemble. The matrix is generated
        /// with the seed fold, scheme, and shape a declared selector dimension would use, so the
        /// standalone and verification streams stay content-seeded and deterministic.
        /// </remarks>
        private double[,]? _mixtureSelector;

        /// <summary>
        /// The distinct entries this composite currently holds a change subscription on — the
        /// shadow of <see cref="ConsequenceFunctions"/> that
        /// <see cref="ConsequenceFunctionsCollectionChanged"/> reconciles against.
        /// </summary>
        /// <remarks>
        /// A shadow set rather than per-item bookkeeping off the event arguments, because
        /// <see cref="NotifyCollectionChangedAction.Reset"/> — which <c>Clear()</c> raises —
        /// carries no <c>OldItems</c>; handling only <c>OldItems</c>/<c>NewItems</c> (the BestFit
        /// <c>CompositeAnalysis</c> wiring) would leak a subscription on every clear. Reconciling
        /// also makes duplicates safe: an entry listed twice is subscribed once.
        /// </remarks>
        private readonly HashSet<WeightedConsequenceFunction> _subscribedEntries = new HashSet<WeightedConsequenceFunction>();

        /// <summary>
        /// Descriptions of serialized function references that could not be resolved, reported by
        /// <see cref="Validate"/> so the precise cause is visible instead of a generic message.
        /// </summary>
        private readonly List<string> _unresolvedFunctionReferences = new List<string>();

        /// <summary>
        /// True while <see cref="EntryPropertyChanged"/> is re-raising, breaking the notification
        /// feedback loop a cyclic composite graph would otherwise create: a composite that
        /// (directly or transitively) wraps itself hears its own re-raise through the entry
        /// subscription and recurses without bound. Cycles are validation errors, but the event
        /// wiring must stay crash-free so <see cref="Validate"/> can actually report them — a
        /// consuming layer that momentarily wires a cycle must get an error message, not a stack
        /// overflow.
        /// </summary>
        private bool _raisingEntryChange;

        /// <summary>
        /// The ordered weighted child entries. Children are referenced, not owned — a consuming
        /// layer may store one function and use it in several composites or graphs. Assigning null
        /// coerces to an empty collection.
        /// </summary>
        /// <remarks>
        /// Observable, and the composite tracks its membership: adding, removing, replacing, or
        /// clearing entries attaches and detaches each entry's change subscription and reports the
        /// change as <c>ConsequenceFunctions</c>. Entry-level edits (weights, function swaps, and
        /// child content edits forwarded by <see cref="WeightedConsequenceFunction"/>) surface the
        /// same way, so an edit made where a child function is stored reaches every composite that
        /// references it. Declared order is compute-relevant: it drives the child sampler ordinals
        /// and the hashed entry order.
        /// </remarks>
        public ObservableCollection<WeightedConsequenceFunction> ConsequenceFunctions
        {
            get { return _consequenceFunctions; }
            set
            {
                if (ReferenceEquals(_consequenceFunctions, value)) return;

                if (_consequenceFunctions != null) _consequenceFunctions.CollectionChanged -= ConsequenceFunctionsCollectionChanged;
                _consequenceFunctions = value ?? new ObservableCollection<WeightedConsequenceFunction>();
                _consequenceFunctions.CollectionChanged += ConsequenceFunctionsCollectionChanged;

                ReconcileEntrySubscriptions();
                RaisePropertyChange(nameof(ConsequenceFunctions));
            }
        }

        /// <summary>
        /// How the children are combined. Compute-relevant: part of the hashed identity form.
        /// </summary>
        public CompositeFunctionType CompositeFunctionType
        {
            get { return _compositeFunctionType; }
            set
            {
                if (_compositeFunctionType != value)
                {
                    _compositeFunctionType = value;
                    RaisePropertyChange(nameof(CompositeFunctionType));
                }
            }
        }

        /// <inheritdoc/>
        public override ConsequenceFunctionType FunctionType => ConsequenceFunctionType.Composite;

        /// <inheritdoc/>
        /// <remarks>
        /// A Mixture or EpistemicMixture over two or more positively weighted entries is never
        /// deterministic — the
        /// per-realization branch pick is real variability even when every child is deterministic.
        /// (Improved over v1.0, which answered "all children deterministic" and let the branch
        /// variability be silently averaged away.) Otherwise the composite is deterministic when
        /// every non-null child is.
        /// </remarks>
        public override bool IsDeterministic
        {
            get
            {
                if ((_compositeFunctionType == CompositeFunctionType.Mixture ||
                     _compositeFunctionType == CompositeFunctionType.EpistemicMixture) && CountPositiveWeights() >= 2) return false;
                for (int i = 0; i < _consequenceFunctions.Count; i++)
                {
                    var function = _consequenceFunctions[i].ConsequenceFunction;
                    if (function != null && !function.IsDeterministic) return false;
                }
                return true;
            }
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Always zero, in every mode. Under the exposure-branch contract
        /// (docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §6.4.1) the aleatory mixture branch
        /// choice is exposure the risk engine enumerates through
        /// <see cref="SampleExposureBranches()"/> rather than a knowledge-uncertainty dimension it
        /// draws — Mixture mode declares no selector dimension. EpistemicMixture also declares
        /// none: consequence functions are deliberately not in the sampler walk, so its
        /// per-realization branch draw rides the failure mode's coupling percentile instead.
        /// Children own their dimensions and are set up recursively by
        /// <see cref="SetupSampler"/> (docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §5.8.5);
        /// the standalone per-realization mixture surface rides an
        /// internal selector matrix instead (see <see cref="_mixtureSelector"/>).
        /// </remarks>
        public override int SamplingDimensions => 0;

        #endregion

        #region IRiskFunction Methods

        /// <inheritdoc/>
        /// <remarks>
        /// Sets up the internal mixture-selector matrix (the Mixture and EpistemicMixture modes —
        /// generated with the
        /// exact seed fold, scheme, and N×1 shape a declared selector dimension would use, so
        /// standalone streams stay content-seeded), then recurses into every child with a
        /// content-derived seed: <c>SeedHelpers.HashCombine(seed, child.CanonicalHash(), ordinal)</c>.
        /// The ordinal gives identical-content siblings independent draws; the child hash is
        /// metadata-inert, so renaming a child can never change results. Nested composites recurse
        /// naturally.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown when the composite configuration is invalid.</exception>
        /// <exception cref="NotSupportedException">Thrown when the sampling scheme is unrecognized.</exception>
        public override void SetupSampler(int sampleSize, int seed, SamplingScheme scheme)
        {
            ThrowIfUnusable(checkCycles: true);
            base.SetupSampler(sampleSize, seed, scheme);

            if (_compositeFunctionType == CompositeFunctionType.Mixture ||
                _compositeFunctionType == CompositeFunctionType.EpistemicMixture)
            {
                int positiveSeed = SeedHelpers.ToPositiveSeed(seed);
                _mixtureSelector = scheme switch
                {
                    SamplingScheme.LatinHypercube => Numerics.Sampling.LatinHypercube.Random(sampleSize, 1, positiveSeed),
                    SamplingScheme.LatinHypercubeMedian => Numerics.Sampling.LatinHypercube.Median(sampleSize, 1, positiveSeed),
                    SamplingScheme.MonteCarlo => SeedHelpers.IndependentUniform(sampleSize, 1, positiveSeed),
                    SamplingScheme.ScrambledSobol => SeedHelpers.ScrambledSobol(sampleSize, 1, positiveSeed),
                    _ => throw new NotSupportedException($"The sampling scheme '{scheme}' is not supported."),
                };
            }
            else
            {
                _mixtureSelector = null;
            }

            for (int i = 0; i < _consequenceFunctions.Count; i++)
            {
                var child = _consequenceFunctions[i].ConsequenceFunction;
                child?.SetupSampler(sampleSize, SeedHelpers.HashCombine(seed, child.CanonicalHash(), i), scheme);
            }
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Errors (invalidating): missing axis labels; no children (or an unresolved serialized
        /// reference, reported precisely instead); a null child entry; a bivariate child (which
        /// has no univariate sampling surface for the composite to combine); non-Additive weights
        /// outside [0, 1] or not summing to one (±1e-8, the Numerics <c>Mixture</c> gate); a
        /// circular reference through nested composites; an invalid child (summary line only — the
        /// child reports its own details where it is stored). Warnings (advisory): child axis
        /// labels that do not match the composite's — labels are unhashed metadata and never gate
        /// compute — and an epistemic mixture with fewer than two positively weighted branches,
        /// which degenerates to the one branch it can select.
        /// </remarks>
        public override (bool IsValid, List<string> ValidationMessages) Validate()
        {
            var messages = new List<string>();

            if (string.IsNullOrEmpty(SpecifiedHazard))
                messages.Add("Error: The composite consequence function does not have a specified hazard type.");
            if (string.IsNullOrEmpty(HazardUnit))
                messages.Add("Error: The composite consequence function does not have a specified hazard unit.");
            if (string.IsNullOrEmpty(SpecifiedConsequence))
                messages.Add("Error: The composite consequence function does not have a specified consequence type.");
            if (string.IsNullOrEmpty(ConsequenceUnit))
                messages.Add("Error: The composite consequence function does not have a specified consequence unit.");

            foreach (string reference in _unresolvedFunctionReferences)
            {
                messages.Add($"Error: The composite consequence function '{Name}' references {reference}, which was not found.");
            }

            if (_consequenceFunctions.Count == 0)
            {
                if (_unresolvedFunctionReferences.Count == 0)
                    messages.Add("Error: No consequence functions have been defined for the composite.");
                return (messages.FindIndex(m => m.StartsWith("Error:", StringComparison.Ordinal)) < 0, messages);
            }

            bool weightsApply = _compositeFunctionType != CompositeFunctionType.Additive;
            bool anyWeightOutOfRange = false;
            double weightSum = 0d;
            for (int i = 0; i < _consequenceFunctions.Count; i++)
            {
                var entry = _consequenceFunctions[i];
                if (entry.ConsequenceFunction == null)
                    messages.Add("Error: A weighted consequence function has not been defined for the composite function.");
                if (weightsApply && (entry.Weight < 0d || entry.Weight > 1d)) anyWeightOutOfRange = true;
                weightSum += entry.Weight;
            }

            if (weightsApply && anyWeightOutOfRange)
                messages.Add("Error: The consequence function weight must be between 0 and 1.");
            if (weightsApply && !anyWeightOutOfRange && Math.Abs(weightSum - 1d) > WeightSumTolerance)
                messages.Add("Error: Composite consequence function weights do not sum to 1.");
            if (_compositeFunctionType == CompositeFunctionType.EpistemicMixture && !anyWeightOutOfRange && CountPositiveWeights() < 2)
                messages.Add("Warning: An epistemic mixture with fewer than two positively weighted branches degenerates to the one branch it can select.");

            var circularChild = FindCircularChild();
            if (circularChild != null)
                messages.Add($"Error: Circular reference error in the selected composite consequence function '{circularChild.Name}'.");

            for (int i = 0; i < _consequenceFunctions.Count; i++)
            {
                var function = _consequenceFunctions[i].ConsequenceFunction;
                if (function == null) continue;

                // A bivariate child has no univariate sampling surface — its one-argument
                // SampleFunction contract throws by design, so the composite cannot combine it.
                if (function is IBivariateConsequenceFunction)
                {
                    messages.Add($"Error: The consequence function '{function.Name}' is bivariate; a composite consequence function cannot combine bivariate consequence functions.");
                    continue;
                }

                // A cyclic child would recurse forever through its own Validate; the circular
                // error above already reports the precise cause.
                if (circularChild == null && !function.Validate().IsValid)
                    messages.Add($"Error: The selected consequence function '{function.Name}' is invalid.");

                if (function.SpecifiedHazard != SpecifiedHazard)
                    messages.Add($"Warning: The consequence function '{function.Name}' does not match hazard type '{SpecifiedHazard}' of the composite function.");
                if (function.HazardUnit != HazardUnit)
                    messages.Add($"Warning: The consequence function '{function.Name}' does not match hazard unit '{HazardUnit}' of the composite function.");
                if (function.SpecifiedConsequence != SpecifiedConsequence)
                    messages.Add($"Warning: The consequence function '{function.Name}' does not match consequence type '{SpecifiedConsequence}' of the composite function.");
                if (function.ConsequenceUnit != ConsequenceUnit)
                    messages.Add($"Warning: The consequence function '{function.Name}' does not match consequence unit '{ConsequenceUnit}' of the composite function.");
            }

            return (messages.FindIndex(m => m.StartsWith("Error:", StringComparison.Ordinal)) < 0, messages);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The mean curve. Additive sums the child mean curves; Average and Mixture both produce
        /// the weighted average of the child mean curves — the legacy mean path has no mixture
        /// branch, because the mean of a mixture is exactly the weighted average of the component
        /// means.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown when the composite configuration is invalid.</exception>
        public override IUnivariateFunction SampleFunction()
        {
            ThrowIfUnusable(checkCycles: true);
            int count = _consequenceFunctions.Count;
            var functions = new IUnivariateFunction[count];
            var weights = new double[count];
            for (int i = 0; i < count; i++)
            {
                functions[i] = _consequenceFunctions[i].ConsequenceFunction!.SampleFunction();
                weights[i] = EffectiveWeight(i);
            }
            return new CompositeUnivariateFunction(functions, weights);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Deterministic and RNG-free (the v1.0 percentile-reseeded <c>Random</c> is deliberately
        /// gone). Additive and Average sample every child co-monotonically at the given
        /// percentile. Mixture uses single-uniform composition sampling: the percentile selects
        /// the child whose cumulative-weight bucket contains it, and the child is sampled at the
        /// rescaled remainder (p − C_{k−1})/w_k — a sweep of uniform percentiles reproduces the
        /// exact mixture ensemble, and coupled fail/non-fail composites sharing a draw pick the
        /// same branch at the same within-branch knowledge percentile. Zero-weight children are
        /// unreachable. Boundary percentiles (exactly 0 or 1) map to child-curve extremes.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown when the composite configuration is invalid.</exception>
        public override IUnivariateFunction SampleFunction(double percentile)
        {
            ThrowIfUnusable(checkCycles: true);
            if (_compositeFunctionType == CompositeFunctionType.Mixture ||
                _compositeFunctionType == CompositeFunctionType.EpistemicMixture)
            {
                var (index, childPercentile) = SelectMixtureChild(percentile, rescale: true);
                var selected = _consequenceFunctions[index].ConsequenceFunction!.SampleFunction(childPercentile);
                return new CompositeUnivariateFunction(new[] { selected }, new[] { 1d });
            }

            int count = _consequenceFunctions.Count;
            var functions = new IUnivariateFunction[count];
            var weights = new double[count];
            for (int i = 0; i < count; i++)
            {
                functions[i] = _consequenceFunctions[i].ConsequenceFunction!.SampleFunction(percentile);
                weights[i] = EffectiveWeight(i);
            }
            return new CompositeUnivariateFunction(functions, weights);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The standalone per-realization path: every child samples realization
        /// <paramref name="realizationIndex"/> from its own content-seeded matrix (children are
        /// mutually independent), and in Mixture mode the internal selector matrix picks the one
        /// child whose curve is returned — a sweep over the sample size reproduces the exact
        /// mixture ensemble. The risk engine does not use this path for mixtures:
        /// it enumerates the weighted branches via <see cref="SampleExposureBranches(double)"/> so
        /// every realization's loss-exceedance curve carries the full exposure spread.
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the composite configuration is invalid, or when
        /// <see cref="SetupSampler"/> has not been called.
        /// </exception>
        public override IUnivariateFunction SampleFunction(int realizationIndex)
        {
            ThrowIfUnusable(checkCycles: false);
            if (_compositeFunctionType == CompositeFunctionType.Mixture ||
                _compositeFunctionType == CompositeFunctionType.EpistemicMixture)
            {
                if (_mixtureSelector == null)
                    throw new InvalidOperationException("SetupSampler() must be called before sampling by realization index.");
                var (index, _) = SelectMixtureChild(_mixtureSelector[realizationIndex, 0], rescale: false);
                var selected = _consequenceFunctions[index].ConsequenceFunction!.SampleFunction(realizationIndex);
                return new CompositeUnivariateFunction(new[] { selected }, new[] { 1d });
            }

            int count = _consequenceFunctions.Count;
            var functions = new IUnivariateFunction[count];
            var weights = new double[count];
            for (int i = 0; i < count; i++)
            {
                functions[i] = _consequenceFunctions[i].ConsequenceFunction!.SampleFunction(realizationIndex);
                weights[i] = EffectiveWeight(i);
            }
            return new CompositeUnivariateFunction(functions, weights);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The composite's real exposure branches
        /// (docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §6.4.1):
        /// Additive and Average composites are genuine pointwise combinations and return a single
        /// unit-weight entry carrying the collapsed mean curve; a Mixture returns one entry per
        /// positively weighted child carrying the child's mean curve, with nested Mixture children
        /// flattened by multiplied weights (an Additive/Average child is one branch carrying its
        /// collapsed curve). Zero-weight children are unreachable branches and are skipped. An
        /// EpistemicMixture returns the single unit-weight blended mean curve — the mean pass has
        /// no draw to select a branch with, and the blend is exact for mean risk because
        /// consequences enter it linearly; only the epistemic spread is absent, which is why a
        /// mean-only analysis over an epistemic consequence composite is a validation warning.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown when the composite configuration is invalid.</exception>
        public override IReadOnlyList<(double Weight, IUnivariateFunction Function)> SampleExposureBranches()
        {
            ThrowIfUnusable(checkCycles: true);
            if (_compositeFunctionType != CompositeFunctionType.Mixture)
            {
                return new[] { (1d, SampleFunction()) };
            }

            var branches = new List<(double Weight, IUnivariateFunction Function)>();
            CollectExposureBranches(branches, 1d, null);
            return branches;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// For the aleatory modes the branch set is structural — identical weights to
        /// <see cref="SampleExposureBranches()"/> — and every branch curve is sampled
        /// co-monotonically at the given knowledge percentile, the same single-uniform semantic
        /// the percentile overloads already carry. One shared percentile driving both a failure
        /// composite and its paired non-failure consequence keeps the pair coherent. An
        /// EpistemicMixture instead <i>selects</i>: the percentile picks one child by inverse-CDF
        /// of the cumulative weights, and the selected child's own exposure branches at the
        /// rescaled percentile are returned — so a nested aleatory mixture still enumerates
        /// inside the chosen branch, while the epistemic alternatives never mix within one
        /// realization.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown when the composite configuration is invalid.</exception>
        public override IReadOnlyList<(double Weight, IUnivariateFunction Function)> SampleExposureBranches(double percentile)
        {
            ThrowIfUnusable(checkCycles: true);
            if (_compositeFunctionType == CompositeFunctionType.EpistemicMixture)
            {
                var (index, childPercentile) = SelectMixtureChild(percentile, rescale: true);
                return _consequenceFunctions[index].ConsequenceFunction!.SampleExposureBranches(childPercentile);
            }
            if (_compositeFunctionType != CompositeFunctionType.Mixture)
            {
                return new[] { (1d, SampleFunction(percentile)) };
            }

            var branches = new List<(double Weight, IUnivariateFunction Function)>();
            CollectExposureBranches(branches, 1d, percentile);
            return branches;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Structural and sampling-free: one for Additive/Average composites, the flattened
        /// positive-weight leaf count for a Mixture (nested Mixtures recurse; any other child is
        /// one leaf), and for an EpistemicMixture the <i>largest</i> branch count over the
        /// positively weighted children — one branch is active per realization, so the guardrail
        /// prices the worst selectable child rather than the sum of alternatives. Cycle-safe — a
        /// cyclic child graph contributes no further leaves here and is
        /// reported as an error by <see cref="Validate"/>.
        /// </remarks>
        public override int CountExposureBranches()
        {
            if (_compositeFunctionType == CompositeFunctionType.EpistemicMixture)
                return MaxExposureBranchesOverChildren(new HashSet<CompositeConsequence>());
            if (_compositeFunctionType != CompositeFunctionType.Mixture) return 1;
            return CountExposureBranches(new HashSet<CompositeConsequence>());
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The smallest child minimum. Improved over v1.0: an empty composite throws instead of
        /// returning the legacy <c>double.MaxValue</c> sentinel, which silently poisons engine
        /// integration bounds.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown when the composite has no usable children.</exception>
        public override double MinHazard()
        {
            double minimum = double.MaxValue;
            bool any = false;
            for (int i = 0; i < _consequenceFunctions.Count; i++)
            {
                var function = _consequenceFunctions[i].ConsequenceFunction;
                if (function == null) continue;
                any = true;
                double value = function.MinHazard();
                if (value < minimum) minimum = value;
            }
            if (!any) throw new InvalidOperationException("The composite consequence function has no child functions. Call Validate() and correct the reported errors before sampling.");
            return minimum;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The largest child maximum. Improved over v1.0: an empty composite throws instead of
        /// returning the legacy <c>double.MinValue</c> sentinel.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown when the composite has no usable children.</exception>
        public override double MaxHazard()
        {
            double maximum = double.MinValue;
            bool any = false;
            for (int i = 0; i < _consequenceFunctions.Count; i++)
            {
                var function = _consequenceFunctions[i].ConsequenceFunction;
                if (function == null) continue;
                any = true;
                double value = function.MaxHazard();
                if (value > maximum) maximum = value;
            }
            if (!any) throw new InvalidOperationException("The composite consequence function has no child functions. Call Validate() and correct the reported errors before sampling.");
            return maximum;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// <para>
        /// Deterministic internal Monte Carlo on a clone: the composite is round-tripped through
        /// its self-contained form (so the live instance's engine sampler state is never
        /// disturbed), the clone's sampler runs <see cref="SummaryRealizations"/> median-LHS
        /// realizations seeded by <see cref="SeedHelpers.HashCombine(int, byte[], int)"/> over
        /// (<see cref="SummarySeedBase"/>, <see cref="CanonicalHash"/>, 0), and every realization
        /// curve is evaluated over <see cref="UncertaintySummaryHazards"/>. Clone-based sampling
        /// is essential for Average mode: engine sampling draws the children independently
        /// (variance Σw²σ²) — a co-monotonic percentile sweep would overstate the bands.
        /// </para>
        /// <para>
        /// Curves are index-aligned with <see cref="UncertaintySummaryHazards"/>: mean, median
        /// (mode curve), and the (1 ∓ w)/2 percentiles. Deterministic composites evaluate the
        /// mean curve exactly with no simulation. Returns null when <see cref="Validate"/> reports
        /// errors.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the width is outside (0, 1).</exception>
        public override UncertaintyAnalysisResults? ComputeUncertaintyResults(double confidenceIntervalWidth = 0.9)
        {
            if (confidenceIntervalWidth <= 0d || confidenceIntervalWidth >= 1d)
                throw new ArgumentOutOfRangeException(nameof(confidenceIntervalWidth), "The confidence interval width must be between 0 and 1.");
            if (!Validate().IsValid) return null;

            double[] hazards = UncertaintySummaryHazards();
            var results = new UncertaintyAnalysisResults
            {
                ModeCurve = new double[hazards.Length],
                MeanCurve = new double[hazards.Length],
                ConfidenceIntervals = new double[hazards.Length, 2],
            };

            if (IsDeterministic)
            {
                var mean = SampleFunction();
                for (int i = 0; i < hazards.Length; i++)
                {
                    double value = mean.Function(hazards[i]);
                    results.ModeCurve[i] = value;
                    results.MeanCurve[i] = value;
                    results.ConfidenceIntervals[i, 0] = value;
                    results.ConfidenceIntervals[i, 1] = value;
                }
                return results;
            }

            var clone = (CompositeConsequence)RiskFunctionFactory.CreateConsequenceFunction(ToXElement())!;
            int seed = ToPositiveSeed(SeedHelpers.HashCombine(SummarySeedBase, CanonicalHash(), 0));
            clone.SetupSampler(SummaryRealizations, seed, SamplingScheme.LatinHypercubeMedian);

            var values = new double[hazards.Length, SummaryRealizations];
            for (int k = 0; k < SummaryRealizations; k++)
            {
                var curve = clone.SampleFunction(k);
                for (int i = 0; i < hazards.Length; i++)
                {
                    values[i, k] = curve.Function(hazards[i]);
                }
            }

            double tail = (1d - confidenceIntervalWidth) / 2d;
            var row = new double[SummaryRealizations];
            for (int i = 0; i < hazards.Length; i++)
            {
                FunctionHelpers.SummarizeEnsembleRow(values, i, row, tail, results);
            }
            return results;
        }

        /// <summary>
        /// The hazard grid that <see cref="ComputeUncertaintyResults"/> summarizes over — callers
        /// pair the returned curves with these hazards by index.
        /// </summary>
        /// <returns>The sorted distinct union of the child summary hazards.</returns>
        /// <remarks>
        /// Tabular children contribute their table ordinates (the legacy union-of-knots grid),
        /// parametric children their own summary grid, nested composites recurse, and any other
        /// child type contributes its hazard bounds. Cyclic composites throw — validate first.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown when the composite has no usable children.</exception>
        public double[] UncertaintySummaryHazards()
        {
            var hazards = new SortedSet<double>();
            CollectSummaryHazards(hazards, new HashSet<CompositeConsequence>());
            if (hazards.Count == 0)
                throw new InvalidOperationException("The composite consequence function has no child functions. Call Validate() and correct the reported errors before sampling.");
            return hazards.ToArray();
        }

        /// <summary>
        /// Determines whether the given composite appears anywhere in this composite's child
        /// graph — the circular-reference guard (call with the candidate parent before assigning
        /// a child, or with <c>this</c> to detect an existing cycle).
        /// </summary>
        /// <param name="composite">The composite to search for.</param>
        /// <returns>True when the composite is referenced at any nesting depth.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the composite is null.</exception>
        /// <remarks>
        /// Improved over the v1.0 recursion: a visited set guards against cycles that do not
        /// involve the target, which would otherwise recurse forever.
        /// </remarks>
        public bool ContainsComposite(CompositeConsequence composite)
        {
            if (composite == null) throw new ArgumentNullException(nameof(composite));
            return ContainsComposite(composite, new HashSet<CompositeConsequence>());
        }

        #endregion

        #region Serialization

        /// <inheritdoc/>
        /// <remarks>
        /// The self-contained form (child content inline) — the storeless default. Not this
        /// type's hash surface: see <see cref="CanonicalHash"/>.
        /// </remarks>
        public override XElement ToXElement()
        {
            return ToXElement(RiskSerializationMode.SelfContained);
        }

        /// <summary>
        /// Serializes the composite in the requested mode: child content inline
        /// (<see cref="RiskSerializationMode.SelfContained"/>), or weight-bearing entries whose
        /// children are <c>FunctionReference</c> markers
        /// (<see cref="RiskSerializationMode.ByReference"/> — the stored form, which never
        /// duplicates child function content).
        /// </summary>
        /// <param name="mode">The serialization mode; the mode propagates to nested composites.</param>
        /// <returns>The serialized form.</returns>
        public XElement ToXElement(RiskSerializationMode mode)
        {
            var element = new XElement(nameof(CompositeConsequence));
            WriteIdentityAttributes(element);
            element.SetAttributeValue(nameof(SpecifiedHazard), SpecifiedHazard);
            element.SetAttributeValue(nameof(HazardUnit), HazardUnit);
            element.SetAttributeValue(nameof(SpecifiedConsequence), SpecifiedConsequence);
            element.SetAttributeValue(nameof(ConsequenceUnit), ConsequenceUnit);
            element.SetAttributeValue(nameof(CompositeFunctionType), _compositeFunctionType.ToString());

            var container = new XElement(nameof(ConsequenceFunctions));
            for (int i = 0; i < _consequenceFunctions.Count; i++)
            {
                var entry = _consequenceFunctions[i];
                var entryElement = new XElement(nameof(WeightedConsequenceFunction));
                entryElement.SetAttributeValue(nameof(WeightedConsequenceFunction.Weight), SerializationUtilities.FormatDouble(entry.Weight));
                if (entry.ConsequenceFunction != null)
                {
                    // Nested composites need no special case: under ByReference the nested
                    // composite itself becomes a marker (its children stay live in the store), and
                    // under SelfContained its parameterless ToXElement() recurses fully inline.
                    entryElement.Add(FunctionEntry.Write(entry.ConsequenceFunction, mode));
                }
                container.Add(entryElement);
            }
            element.Add(container);
            return element;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Hashes the projected identity form, never the persisted form (the second instance of
        /// the <c>SystemComponent</c> identity-form exception): the combine mode, the entry count,
        /// and per entry the effective weight and the child's own canonical hash. Consequences by
        /// construction: the serialization mode can never move the hash (both modes project
        /// identically); child metadata edits are inert (child hashes are themselves
        /// metadata-inert); Additive weight edits are inert (weights project as one, matching the
        /// compute paths); and entry order is semantic. A null child projects an empty hash token,
        /// so an unresolved reference does not alias a resolved one.
        /// </remarks>
        public override byte[] CanonicalHash()
        {
            var identity = new XElement(nameof(CompositeConsequence));
            identity.SetAttributeValue(nameof(CompositeFunctionType), _compositeFunctionType.ToString());
            identity.SetAttributeValue("Count", _consequenceFunctions.Count.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < _consequenceFunctions.Count; i++)
            {
                var entry = _consequenceFunctions[i];
                var entryElement = new XElement(nameof(WeightedConsequenceFunction));
                entryElement.SetAttributeValue(nameof(WeightedConsequenceFunction.Weight), SerializationUtilities.FormatDouble(EffectiveWeight(i)));
                entryElement.SetAttributeValue("FunctionHash", entry.ConsequenceFunction == null
                    ? string.Empty
                    : CanonicalContentHasher.ToTokenHex(entry.ConsequenceFunction.CanonicalHash()));
                identity.Add(entryElement);
            }
            return CanonicalContentHasher.Hash(identity, CanonicalizationRules.ModelRules);
        }

        #endregion

        #region Private Helpers

        /// <summary>
        /// The weight the compute paths use for an entry: one under Additive (weights are inert
        /// there — the legacy coercion), the entry weight otherwise.
        /// </summary>
        /// <param name="index">The entry index.</param>
        /// <returns>The effective weight.</returns>
        private double EffectiveWeight(int index)
        {
            return _compositeFunctionType == CompositeFunctionType.Additive ? 1d : _consequenceFunctions[index].Weight;
        }

        /// <summary>
        /// Counts entries with strictly positive weight — the reachable Mixture branches.
        /// </summary>
        /// <returns>The number of positively weighted entries.</returns>
        private int CountPositiveWeights()
        {
            int count = 0;
            for (int i = 0; i < _consequenceFunctions.Count; i++)
            {
                if (_consequenceFunctions[i].Weight > 0d) count++;
            }
            return count;
        }

        /// <summary>
        /// Selects the Mixture child whose cumulative-weight bucket contains the percentile
        /// (legacy inclusive-upper comparison), skipping unreachable zero-weight entries.
        /// </summary>
        /// <param name="percentile">The selector percentile.</param>
        /// <param name="rescale">
        /// True for single-uniform composition sampling: also map the percentile onto the selected
        /// bucket's interior, (p − C_{k−1})/w_k, so one uniform drives both the selection and the
        /// child's knowledge percentile. False on the engine path, where the child draws from its
        /// own sampler dimension.
        /// </param>
        /// <returns>The selected entry index and the child percentile (the input when not rescaling).</returns>
        /// <remarks>
        /// A percentile beyond the cumulative sum (floating-point drift at the top of the last
        /// bucket) clamps to the last positively weighted child at its top percentile.
        /// </remarks>
        private (int Index, double ChildPercentile) SelectMixtureChild(double percentile, bool rescale)
        {
            double cumulative = 0d;
            int lastPositive = -1;
            for (int i = 0; i < _consequenceFunctions.Count; i++)
            {
                double weight = _consequenceFunctions[i].Weight;
                if (weight <= 0d) continue;
                double prior = cumulative;
                cumulative += weight;
                lastPositive = i;
                if (percentile <= cumulative)
                {
                    return (i, rescale ? (percentile - prior) / weight : percentile);
                }
            }
            return (lastPositive, rescale ? 1d : percentile);
        }

        /// <summary>
        /// Flattens this Mixture's positively weighted children into exposure branches: a nested
        /// Mixture child recurses with multiplied weights (guarded by its own usability gate, so a
        /// nested cycle throws the standard invalid-configuration error instead of recursing
        /// without bound); a nested EpistemicMixture child on the percentile path contributes its
        /// <i>selected</i> child's branches (so an aleatory mixture inside the chosen epistemic
        /// branch still enumerates), and on the mean path its blended curve; any other child
        /// contributes one branch carrying its mean curve, or its
        /// curve sampled co-monotonically at the given knowledge percentile.
        /// </summary>
        /// <param name="branches">The accumulating branch list.</param>
        /// <param name="parentWeight">The product of ancestor mixture weights applied to this level.</param>
        /// <param name="percentile">The shared knowledge percentile, or null for the mean curves.</param>
        private void CollectExposureBranches(List<(double Weight, IUnivariateFunction Function)> branches,
            double parentWeight, double? percentile)
        {
            for (int i = 0; i < _consequenceFunctions.Count; i++)
            {
                double weight = _consequenceFunctions[i].Weight;
                if (weight <= 0d) continue;

                var child = _consequenceFunctions[i].ConsequenceFunction!;
                if (child is CompositeConsequence nested && nested.CompositeFunctionType == CompositeFunctionType.Mixture)
                {
                    nested.ThrowIfUnusable(checkCycles: true);
                    nested.CollectExposureBranches(branches, parentWeight * weight, percentile);
                }
                else if (percentile.HasValue && child is CompositeConsequence epistemicNested
                    && epistemicNested.CompositeFunctionType == CompositeFunctionType.EpistemicMixture)
                {
                    epistemicNested.ThrowIfUnusable(checkCycles: true);
                    var selected = epistemicNested.SampleExposureBranches(percentile.Value);
                    for (int b = 0; b < selected.Count; b++)
                    {
                        branches.Add((parentWeight * weight * selected[b].Weight, selected[b].Function));
                    }
                }
                else
                {
                    branches.Add((parentWeight * weight,
                        percentile.HasValue ? child.SampleFunction(percentile.Value) : child.SampleFunction()));
                }
            }
        }

        /// <summary>
        /// The cycle-safe recursion behind <see cref="CountExposureBranches()"/>: counts the
        /// flattened positive-weight leaves of a Mixture. A null child entry counts as one leaf so
        /// the guardrail product stays meaningful while <see cref="Validate"/> reports the missing
        /// function; a nested EpistemicMixture child prices its worst selectable branch.
        /// </summary>
        /// <param name="visited">The composites already counted (guards cyclic graphs).</param>
        /// <returns>The leaf count contributed by this composite.</returns>
        private int CountExposureBranches(HashSet<CompositeConsequence> visited)
        {
            if (!visited.Add(this)) return 0;
            int count = 0;
            for (int i = 0; i < _consequenceFunctions.Count; i++)
            {
                if (_consequenceFunctions[i].Weight <= 0d) continue;
                count += _consequenceFunctions[i].ConsequenceFunction switch
                {
                    CompositeConsequence nested when nested.CompositeFunctionType == CompositeFunctionType.Mixture
                        => nested.CountExposureBranches(visited),
                    CompositeConsequence nested when nested.CompositeFunctionType == CompositeFunctionType.EpistemicMixture
                        => nested.MaxExposureBranchesOverChildren(visited),
                    _ => 1,
                };
            }
            return count;
        }

        /// <summary>
        /// The cycle-safe worst-case branch count of an EpistemicMixture: the largest exposure
        /// branch count over the positively weighted children, at least one — exactly one child is
        /// active per realization, so the guardrail prices the worst selectable alternative rather
        /// than the sum of alternatives.
        /// </summary>
        /// <param name="visited">The composites already counted (guards cyclic graphs).</param>
        /// <returns>The worst-case active branch count.</returns>
        private int MaxExposureBranchesOverChildren(HashSet<CompositeConsequence> visited)
        {
            if (!visited.Add(this)) return 1;
            int max = 1;
            for (int i = 0; i < _consequenceFunctions.Count; i++)
            {
                if (_consequenceFunctions[i].Weight <= 0d) continue;
                int count = _consequenceFunctions[i].ConsequenceFunction switch
                {
                    CompositeConsequence nested when nested.CompositeFunctionType == CompositeFunctionType.Mixture
                        => nested.CountExposureBranches(visited),
                    CompositeConsequence nested when nested.CompositeFunctionType == CompositeFunctionType.EpistemicMixture
                        => nested.MaxExposureBranchesOverChildren(visited),
                    null => 1,
                    var child => child.CountExposureBranches(),
                };
                if (count > max) max = count;
            }
            return max;
        }

        /// <summary>
        /// True when this composite, or any nested composite consequence beneath it, is in
        /// <see cref="CompositeFunctionType.EpistemicMixture"/> mode — the analysis-level
        /// mean-only gate's discovery surface.
        /// </summary>
        /// <returns>True when an epistemic mixture exists anywhere in the subtree.</returns>
        internal bool UsesEpistemicMode()
        {
            return UsesEpistemicMode(new HashSet<CompositeConsequence>());
        }

        /// <summary>
        /// The cycle-safe recursion behind <see cref="UsesEpistemicMode()"/>.
        /// </summary>
        /// <param name="visited">The composites already searched.</param>
        /// <returns>True when an epistemic mixture exists anywhere in the subtree.</returns>
        private bool UsesEpistemicMode(HashSet<CompositeConsequence> visited)
        {
            if (!visited.Add(this)) return false;
            if (_compositeFunctionType == CompositeFunctionType.EpistemicMixture) return true;
            for (int i = 0; i < _consequenceFunctions.Count; i++)
            {
                if (_consequenceFunctions[i].ConsequenceFunction is CompositeConsequence nested && nested.UsesEpistemicMode(visited))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The sample-time usability gate (the cluster's invalid-configuration throw): at least
        /// one entry, every entry configured and univariate (a bivariate child has no univariate
        /// sampling surface), and — outside Additive — weights in [0, 1] summing
        /// to one. Cycle detection is opt-in because the per-realization path runs this on every
        /// draw and a cycle cannot survive <see cref="SetupSampler"/>.
        /// </summary>
        /// <param name="checkCycles">True to also reject circular references.</param>
        /// <exception cref="InvalidOperationException">Thrown when the configuration is invalid.</exception>
        private void ThrowIfUnusable(bool checkCycles)
        {
            bool usable = _consequenceFunctions.Count > 0;
            if (usable)
            {
                double weightSum = 0d;
                bool weightsApply = _compositeFunctionType != CompositeFunctionType.Additive;
                for (int i = 0; i < _consequenceFunctions.Count; i++)
                {
                    var entry = _consequenceFunctions[i];
                    if (entry.ConsequenceFunction == null || entry.ConsequenceFunction is IBivariateConsequenceFunction ||
                        (weightsApply && (entry.Weight < 0d || entry.Weight > 1d)))
                    {
                        usable = false;
                        break;
                    }
                    weightSum += entry.Weight;
                }
                if (usable && weightsApply && Math.Abs(weightSum - 1d) > WeightSumTolerance) usable = false;
            }
            if (usable && checkCycles && FindCircularChild() != null) usable = false;

            if (!usable)
                throw new InvalidOperationException("The composite consequence configuration is invalid. Call Validate() and correct the reported errors before sampling.");
        }

        /// <summary>
        /// Finds the first child that closes a cycle back to this composite, or null when the
        /// child graph is acyclic.
        /// </summary>
        /// <returns>The offending child function, or null.</returns>
        private IConsequenceFunction? FindCircularChild()
        {
            for (int i = 0; i < _consequenceFunctions.Count; i++)
            {
                var function = _consequenceFunctions[i].ConsequenceFunction;
                if (function is not CompositeConsequence nested) continue;
                if (ReferenceEquals(nested, this) || nested.ContainsComposite(this)) return function;
            }
            return null;
        }

        /// <summary>
        /// The cycle-safe recursion behind <see cref="ContainsComposite(CompositeConsequence)"/>.
        /// </summary>
        /// <param name="target">The composite being searched for.</param>
        /// <param name="visited">The composites already searched (guards cycles not involving the target).</param>
        /// <returns>True when the target is referenced at any nesting depth.</returns>
        private bool ContainsComposite(CompositeConsequence target, HashSet<CompositeConsequence> visited)
        {
            if (!visited.Add(this)) return false;
            for (int i = 0; i < _consequenceFunctions.Count; i++)
            {
                var function = _consequenceFunctions[i].ConsequenceFunction;
                if (ReferenceEquals(function, target)) return true;
                if (function is CompositeConsequence nested && nested.ContainsComposite(target, visited)) return true;
            }
            return false;
        }

        /// <summary>
        /// Accumulates the summary hazard grid from the children: tabular knots, parametric
        /// summary grids, nested composites recursively (cycle-safe), and hazard bounds for any
        /// other child type.
        /// </summary>
        /// <param name="hazards">The accumulating sorted distinct grid.</param>
        /// <param name="visited">The composites already visited.</param>
        private void CollectSummaryHazards(SortedSet<double> hazards, HashSet<CompositeConsequence> visited)
        {
            if (!visited.Add(this)) return;
            for (int i = 0; i < _consequenceFunctions.Count; i++)
            {
                switch (_consequenceFunctions[i].ConsequenceFunction)
                {
                    case TabularConsequence tabular:
                        for (int j = 0; j < tabular.UncertainOrderedPairedData.Count; j++)
                            hazards.Add(tabular.UncertainOrderedPairedData[j].X);
                        break;
                    case ParametricConsequence parametric:
                        foreach (double hazard in parametric.UncertaintySummaryHazards())
                            hazards.Add(hazard);
                        break;
                    case CompositeConsequence nested:
                        nested.CollectSummaryHazards(hazards, visited);
                        break;
                    case { } other:
                        hazards.Add(other.MinHazard());
                        hazards.Add(other.MaxHazard());
                        break;
                    case null:
                        break;
                }
            }
        }

        /// <summary>
        /// Keeps the composite's change subscriptions in step with the entry list, and reports the
        /// membership change as <c>ConsequenceFunctions</c>.
        /// </summary>
        /// <param name="sender">The entry collection.</param>
        /// <param name="e">The membership change.</param>
        private void ConsequenceFunctionsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            ReconcileEntrySubscriptions();
            RaisePropertyChange(nameof(ConsequenceFunctions));
        }

        /// <summary>
        /// Subscribes to every entry now in the list and unsubscribes from every entry that has
        /// left it, using <see cref="_subscribedEntries"/> as the record of what is attached.
        /// </summary>
        private void ReconcileEntrySubscriptions()
        {
            foreach (var stale in _subscribedEntries.Where(entry => !_consequenceFunctions.Contains(entry)).ToList())
            {
                stale.PropertyChanged -= EntryPropertyChanged;
                _subscribedEntries.Remove(stale);
            }

            for (int i = 0; i < _consequenceFunctions.Count; i++)
            {
                var entry = _consequenceFunctions[i];
                if (entry != null && _subscribedEntries.Add(entry)) entry.PropertyChanged += EntryPropertyChanged;
            }
        }

        /// <summary>
        /// Re-raises an entry-level change (a weight edit, a function swap, or a forwarded child
        /// content edit) as a change of the composite's entry list. Reentrant notifications are
        /// suppressed via <see cref="_raisingEntryChange"/> so a cyclic composite graph degrades
        /// to a reportable validation error instead of unbounded recursion.
        /// </summary>
        /// <param name="sender">The entry.</param>
        /// <param name="e">The originating change arguments.</param>
        private void EntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_raisingEntryChange) return;
            _raisingEntryChange = true;
            try
            {
                RaisePropertyChange(nameof(ConsequenceFunctions));
            }
            finally
            {
                _raisingEntryChange = false;
            }
        }

        #endregion
    }
}
