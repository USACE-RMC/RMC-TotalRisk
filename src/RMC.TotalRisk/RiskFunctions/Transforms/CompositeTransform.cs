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
using RMC.TotalRisk.Core.Support;

namespace RMC.TotalRisk.RiskFunctions.Transforms
{
    /// <summary>
    /// A composite transform function: the weighted average <c>Σ ωᵢ·fᵢ(x)</c> of a weighted list of
    /// child transform functions (<see cref="CompositeFunctionType.Average"/>, the default) —
    /// several candidate rating curves blended into one consensus curve by their credibility
    /// weights — or an epistemic mixture
    /// (<see cref="CompositeFunctionType.EpistemicMixture"/>), the logic tree that selects one
    /// candidate curve as the truth per realization.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// New in v1.1 — v1.0 had no composite transform, so there is no legacy behavior to preserve
    /// and no legacy oracle. The combine rides the Numerics <see cref="CompositeFunction"/> in
    /// <see cref="CompositeFunctionMode.WeightedAverage"/> mode, per the thin-wrapper mapping
    /// for the transform cluster (docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §6.2).
    /// </para>
    /// <para>
    /// <b><see cref="CompositeFunctionType.Average"/> and
    /// <see cref="CompositeFunctionType.EpistemicMixture"/> are supported</b> (Average is the
    /// default, unlike <c>CompositeConsequence</c>, so a new instance is valid out of the box).
    /// <see cref="CompositeFunctionType.Mixture"/> and
    /// <see cref="CompositeFunctionType.Additive"/> are validation errors:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>Mixture</b> (the aleatory reading) would require within-realization branch enumeration,
    /// and there is no transform
    /// analog of the consequence exposure-branch surface — <c>SampledFailureMode</c> chains
    /// transforms deterministically. A mean-only run would therefore collapse the branch and its
    /// loss-exceedance tail would diverge from the mean of the full-uncertainty ensemble: exactly
    /// the defect the exposure-branch contract solved for consequences
    /// (docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §6.4.1). Deferred until
    /// the engine gains transform-branch enumeration. The epistemic reading needs no such
    /// surface: one branch per realization chains through <c>SampledFailureMode</c> exactly like
    /// any other sampled transform, which is why <see cref="CompositeFunctionType.EpistemicMixture"/>
    /// ships while the aleatory Mixture stays deferred.
    /// </description></item>
    /// <item><description>
    /// <b>Additive</b> (summing transforms, weights inert) has no physical reading for a
    /// hazard-to-hazard mapping.
    /// </description></item>
    /// </list>
    /// <para>
    /// <b>Choosing between the two supported modes:</b> a weighted average is the aleatory-mean
    /// reading of a set of
    /// candidate transforms — exact when everything downstream is linear, approximate otherwise.
    /// Because a fragility is steeply nonlinear, blending candidate rating curves before evaluating
    /// it is not the same as weighting the risks each curve produces (Jensen's inequality), and the
    /// blended curve contributes no spread of its own to the uncertainty bands. Where the weights
    /// genuinely mean "one of these curves is the truth and we do not know which", use
    /// <see cref="CompositeFunctionType.EpistemicMixture"/>: the composite declares one
    /// branch-selector dimension, each realization selects one candidate curve by inverse-CDF of
    /// the cumulative weights and chains it downstream whole, and the ensemble straddles the
    /// alternatives — the doctrine's three-rating-curve example made runnable. A named
    /// <see cref="EpistemicVariable"/> shares the selector draw across binders, and mean-only
    /// analyses over an epistemic composite are refused by analysis-level validation. See
    /// <c>docs/technical-reference/composite-functions.md</c>.
    /// </para>
    /// <para>
    /// <b>Domain:</b> the composite's input domain is the <i>intersection</i> of its children's —
    /// the one deliberate departure from the union rule its sibling composites use. A weighted
    /// average needs every child evaluable at every input, and averaging a rating curve
    /// extrapolated far outside its own table is a modeling error rather than a bound. An empty
    /// intersection is a validation error and differing child domains raise a warning.
    /// </para>
    /// <para>
    /// <b>Serialization and hashing</b> follow the sibling composites: dual-mode persistence over
    /// the shared function-entry contract, and a projected identity form carrying the combine mode,
    /// the entry count, and per entry the effective weight and the child's own canonical hash.
    /// </para>
    /// </remarks>
    public class CompositeTransform : TransformFunctionBase
    {
        #region Construction

        /// <summary>
        /// Initializes an empty composite in <see cref="CompositeFunctionType.Average"/> mode.
        /// </summary>
        public CompositeTransform()
        {
            TransformFunctions = new ObservableCollection<WeightedTransformFunction>();
        }

        /// <summary>
        /// Initializes a composite over the specified weighted children.
        /// </summary>
        /// <param name="transformFunctions">The weighted child entries.</param>
        /// <exception cref="ArgumentNullException">Thrown when the sequence is null.</exception>
        public CompositeTransform(IEnumerable<WeightedTransformFunction> transformFunctions)
        {
            if (transformFunctions == null) throw new ArgumentNullException(nameof(transformFunctions));
            TransformFunctions = new ObservableCollection<WeightedTransformFunction>(transformFunctions);
        }

        /// <summary>
        /// Restores a composite transform function from its serialized form.
        /// </summary>
        /// <param name="xElement">The serialized form produced by <see cref="ToXElement(RiskSerializationMode)"/>.</param>
        /// <param name="resolver">
        /// The function resolver, required only to read a by-reference form. An unresolvable
        /// reference keeps its weighted entry with a null function and is reported by
        /// <see cref="Validate"/>.
        /// </param>
        /// <exception cref="ArgumentNullException">Thrown when the element is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when inline child content cannot be reconstructed, or when a serialized reference
        /// id is stale.
        /// </exception>
        public CompositeTransform(XElement xElement, IRiskFunctionResolver? resolver = null)
        {
            if (xElement == null) throw new ArgumentNullException(nameof(xElement));

            ReadIdentityAttributes(xElement);
            SpecifiedHazard = SerializationUtilities.ReadString(xElement, nameof(SpecifiedHazard));
            HazardUnit = SerializationUtilities.ReadString(xElement, nameof(HazardUnit));
            TransformedHazard = SerializationUtilities.ReadString(xElement, nameof(TransformedHazard));
            TransformedHazardUnit = SerializationUtilities.ReadString(xElement, nameof(TransformedHazardUnit));
            _compositeFunctionType = SerializationUtilities.ReadEnum(xElement, nameof(CompositeFunctionType), CompositeFunctionType.Average);
            _epistemicVariable = SerializationUtilities.ReadString(xElement, nameof(EpistemicVariable));

            TransformFunctions = new ObservableCollection<WeightedTransformFunction>();
            var container = xElement.Element(nameof(TransformFunctions));
            if (container != null)
            {
                foreach (var entryElement in container.Elements(nameof(WeightedTransformFunction)))
                {
                    double weight = SerializationUtilities.ReadDouble(entryElement, nameof(WeightedTransformFunction.Weight));
                    ITransformFunction? function = null;
                    var child = entryElement.Elements().FirstOrDefault();
                    if (child != null)
                    {
                        // The resolver threads into the inline factory so an inline nested
                        // composite can resolve its own by-reference children.
                        function = FunctionEntry.Read<ITransformFunction>(
                            child, resolver, c => RiskFunctionFactory.CreateFromXElement(c, resolver),
                            Name, $"The {nameof(CompositeTransform)} '{Name}'", "transform function",
                            _unresolvedFunctionReferences);
                    }

                    // The entry is kept even when the function is null: dropping it would silently
                    // change the weight list — and therefore the hash and the weight-sum
                    // validation — on the next save.
                    TransformFunctions.Add(new WeightedTransformFunction(function, weight));
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
        /// The number of evenly spaced input-hazard values <see cref="UncertaintySummaryHazards"/>
        /// reports — the same grid size the closed-form transforms use.
        /// </summary>
        private const int SummaryGridPoints = 100;

        /// <summary>
        /// The fixed base seed folded with the canonical content hash to derive the
        /// <see cref="ComputeUncertaintyResults"/> sampling seed.
        /// </summary>
        private const int SummarySeedBase = 12345;

        /// <summary>
        /// The tolerance on the weight sum — the same gate the Numerics
        /// <see cref="CompositeFunction"/> applies to its own weights.
        /// </summary>
        private const double WeightSumTolerance = 1e-8;

        /// <summary>
        /// Backing field for <see cref="TransformFunctions"/>. Assigned through the property by
        /// every constructor, so the collection subscription is always attached.
        /// </summary>
        private ObservableCollection<WeightedTransformFunction> _transformFunctions = null!;

        /// <summary>
        /// Backing field for <see cref="CompositeFunctionType"/> — the default is Average.
        /// </summary>
        private CompositeFunctionType _compositeFunctionType = CompositeFunctionType.Average;

        /// <summary>
        /// Backing field for <see cref="EpistemicVariable"/> — empty means unbound.
        /// </summary>
        private string _epistemicVariable = string.Empty;

        /// <summary>
        /// The distinct entries this composite currently holds a change subscription on — the
        /// shadow of <see cref="TransformFunctions"/> that
        /// <see cref="TransformFunctionsCollectionChanged"/> reconciles against.
        /// </summary>
        /// <remarks>
        /// A shadow set rather than per-item bookkeeping off the event arguments, because
        /// <see cref="NotifyCollectionChangedAction.Reset"/> — which <c>Clear()</c> raises —
        /// carries no <c>OldItems</c>.
        /// </remarks>
        private readonly HashSet<WeightedTransformFunction> _subscribedEntries = new HashSet<WeightedTransformFunction>();

        /// <summary>
        /// Descriptions of serialized function references that could not be resolved, reported by
        /// <see cref="Validate"/>.
        /// </summary>
        private readonly List<string> _unresolvedFunctionReferences = new List<string>();

        /// <summary>
        /// True while <see cref="EntryPropertyChanged"/> is re-raising, breaking the notification
        /// feedback loop a cyclic composite graph would otherwise create.
        /// </summary>
        private bool _raisingEntryChange;

        /// <summary>
        /// The ordered weighted child entries. Children are referenced, not owned. Assigning null
        /// coerces to an empty collection.
        /// </summary>
        /// <remarks>
        /// Observable, and the composite tracks its membership. Declared order is compute-relevant:
        /// it drives the child sampler ordinals and the hashed entry order.
        /// </remarks>
        public ObservableCollection<WeightedTransformFunction> TransformFunctions
        {
            get { return _transformFunctions; }
            set
            {
                if (ReferenceEquals(_transformFunctions, value)) return;

                if (_transformFunctions != null) _transformFunctions.CollectionChanged -= TransformFunctionsCollectionChanged;
                _transformFunctions = value ?? new ObservableCollection<WeightedTransformFunction>();
                _transformFunctions.CollectionChanged += TransformFunctionsCollectionChanged;

                ReconcileEntrySubscriptions();
                RaisePropertyChange(nameof(TransformFunctions));
            }
        }

        /// <summary>
        /// How the children are combined. <see cref="CompositeFunctionType.Average"/> and
        /// <see cref="CompositeFunctionType.EpistemicMixture"/> are supported; the other members
        /// are validation errors (see the class remarks). Hashed
        /// content, so enabling a further mode later cannot silently reinterpret a stored model.
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

        /// <summary>
        /// The named shared epistemic variable the branch selection binds to, empty (the default)
        /// when the composite selects independently. Meaningful only under
        /// <see cref="CompositeFunctionType.EpistemicMixture"/> — naming a variable in any other
        /// mode is a validation error. During an analysis run (or a standalone component-scope
        /// setup), every bound composite of the same variable receives the same per-realization
        /// selector draw, derived from the variable name alone — the logic tree's
        /// state-of-knowledge correlation. Compute-relevant when non-empty: serialized and hashed
        /// by conditional presence, so an unbound composite's form and hash are unchanged, and
        /// renaming a variable deliberately re-rolls its shared draw (the name is the variable's
        /// identity). Null coerces to empty.
        /// </summary>
        public string EpistemicVariable
        {
            get { return _epistemicVariable; }
            set
            {
                string coerced = value ?? string.Empty;
                if (_epistemicVariable != coerced)
                {
                    _epistemicVariable = coerced;
                    RaisePropertyChange(nameof(EpistemicVariable));
                }
            }
        }

        /// <inheritdoc/>
        public override TransformFunctionType FunctionType => TransformFunctionType.Composite;

        /// <inheritdoc/>
        /// <remarks>
        /// Deterministic when every non-null child is. Under
        /// <see cref="CompositeFunctionType.EpistemicMixture"/> the selection itself is a
        /// knowledge draw, so the composite is never deterministic while two or more branches
        /// carry positive weight — even over fixed children.
        /// </remarks>
        public override bool IsDeterministic
        {
            get
            {
                if (_compositeFunctionType == CompositeFunctionType.EpistemicMixture && CountPositiveWeights() >= 2)
                    return false;
                for (int i = 0; i < _transformFunctions.Count; i++)
                {
                    var function = _transformFunctions[i].TransformFunction;
                    if (function != null && !function.IsDeterministic) return false;
                }
                return true;
            }
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Zero under <see cref="CompositeFunctionType.Average"/>: a weighted average consumes no
        /// knowledge draw of its own. One under
        /// <see cref="CompositeFunctionType.EpistemicMixture"/> — the branch-selector dimension.
        /// Children own their dimensions and are set up recursively by <see cref="SetupSampler"/>.
        /// </remarks>
        public override int SamplingDimensions =>
            _compositeFunctionType == CompositeFunctionType.EpistemicMixture ? 1 : 0;

        #endregion

        #region IRiskFunction Methods

        /// <inheritdoc/>
        /// <remarks>
        /// Recurses into every child with a content-derived seed:
        /// <c>SeedHelpers.HashCombine(seed, child.CanonicalHash(), ordinal)</c>. The ordinal gives
        /// identical-content siblings independent draws; the child hash is metadata-inert. Under
        /// <see cref="CompositeFunctionType.EpistemicMixture"/> the base call also allocates the
        /// one-column branch-selector matrix from this composite's own seed; when bound to an
        /// <see cref="EpistemicVariable"/> inside an active sharing scope, the selector column is
        /// then overwritten with the variable's shared draw — seed-inert to every other function,
        /// exactly like a fractile pin.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown when the composite configuration is invalid.</exception>
        public override void SetupSampler(int sampleSize, int seed, SamplingScheme scheme)
        {
            ThrowIfUnusable(checkCycles: true);
            base.SetupSampler(sampleSize, seed, scheme);

            if (_compositeFunctionType == CompositeFunctionType.EpistemicMixture)
            {
                var shared = _epistemicVariable.Length > 0
                    ? EpistemicSharingScope.TryGetColumn(_epistemicVariable)
                    : EpistemicSharingScope.TryGetColumnById(Id);
                if (shared != null) OverrideSelectorColumn(shared);
            }

            for (int i = 0; i < _transformFunctions.Count; i++)
            {
                var child = _transformFunctions[i].TransformFunction;
                if (child == null) continue;
                CompositeSupport.ThrowIfPosteriorCapacityTooSmall(child, sampleSize, Name);
                child.SetupSampler(sampleSize, SeedHelpers.HashCombine(seed, child.CanonicalHash(), i), scheme);
            }
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Errors (invalidating): missing axis labels; a combine mode other than
        /// <see cref="CompositeFunctionType.Average"/> or
        /// <see cref="CompositeFunctionType.EpistemicMixture"/>; an
        /// <see cref="EpistemicVariable"/> named outside EpistemicMixture mode (dead hashed
        /// content); no children (or an unresolved serialized
        /// reference, reported precisely instead); a null child entry; a bivariate child (which
        /// has no univariate sampling surface for the composite to average or select); weights
        /// outside
        /// [0, 1] or
        /// not summing to one (±1e-8); an empty child-domain intersection; a circular reference; an
        /// invalid child (summary line only). Warnings (advisory): child axis labels that do not
        /// match the composite's (labels are unhashed metadata and never gate compute), child input domains that
        /// differ, since the composite evaluates only over their intersection, and an epistemic
        /// mixture with fewer than two positively weighted branches, which degenerates to the one
        /// branch it can select.
        /// </remarks>
        public override (bool IsValid, List<string> ValidationMessages) Validate()
        {
            var messages = new List<string>();

            if (string.IsNullOrEmpty(SpecifiedHazard))
                messages.Add("Error: The composite transform function does not have a specified hazard type.");
            if (string.IsNullOrEmpty(HazardUnit))
                messages.Add("Error: The composite transform function does not have a specified hazard unit.");
            if (string.IsNullOrEmpty(TransformedHazard))
                messages.Add("Error: The composite transform function does not have a specified transformed hazard type.");
            if (string.IsNullOrEmpty(TransformedHazardUnit))
                messages.Add("Error: The composite transform function does not have a specified transformed hazard unit.");

            if (_compositeFunctionType != CompositeFunctionType.Average && _compositeFunctionType != CompositeFunctionType.EpistemicMixture)
                messages.Add($"Error: The composite transform function only supports the {nameof(CompositeFunctionType.Average)} and " +
                    $"{nameof(CompositeFunctionType.EpistemicMixture)} combinations. " +
                    $"{nameof(CompositeFunctionType.Mixture)} (the aleatory reading) requires within-realization branch enumeration, which " +
                    "the risk engine cannot yet perform for transforms, so a mean-only run would disagree with the mean of the " +
                    "full-uncertainty ensemble; " +
                    $"{nameof(CompositeFunctionType.Additive)} has no physical reading for a hazard-to-hazard mapping.");

            if (_epistemicVariable.Length > 0 && _compositeFunctionType != CompositeFunctionType.EpistemicMixture)
                messages.Add($"Error: The composite transform function names the shared epistemic variable '{_epistemicVariable}' but is not in {nameof(CompositeFunctionType.EpistemicMixture)} mode; clear the variable or select the epistemic mode.");

            foreach (string reference in _unresolvedFunctionReferences)
            {
                messages.Add($"Error: The composite transform function '{Name}' references {reference}, which was not found.");
            }

            if (_transformFunctions.Count == 0)
            {
                if (_unresolvedFunctionReferences.Count == 0)
                    messages.Add("Error: No transform functions have been defined for the composite.");
                return (messages.FindIndex(m => m.StartsWith("Error:", StringComparison.Ordinal)) < 0, messages);
            }

            bool anyWeightOutOfRange = false;
            double weightSum = 0d;
            for (int i = 0; i < _transformFunctions.Count; i++)
            {
                var entry = _transformFunctions[i];
                if (entry.TransformFunction == null)
                    messages.Add("Error: A weighted transform function has not been defined for the composite function.");
                if (entry.Weight < 0d || entry.Weight > 1d) anyWeightOutOfRange = true;
                weightSum += entry.Weight;
            }

            if (anyWeightOutOfRange)
                messages.Add("Error: The transform function weight must be between 0 and 1.");
            if (!anyWeightOutOfRange && Math.Abs(weightSum - 1d) > WeightSumTolerance)
                messages.Add("Error: Composite transform function weights do not sum to 1.");
            if (_compositeFunctionType == CompositeFunctionType.EpistemicMixture && !anyWeightOutOfRange && CountPositiveWeights() < 2)
                messages.Add("Warning: An epistemic mixture with fewer than two positively weighted branches degenerates to the one branch it can select.");

            var circularChild = FindCircularChild();
            if (circularChild != null)
                messages.Add($"Error: Circular reference error in the selected composite transform function '{circularChild.Name}'.");

            // A bivariate child is rejected outright below, so the child-domain intersection is
            // meaningless (and its bounds may legitimately refuse to evaluate) — skip it.
            bool anyBivariateChild = false;
            for (int i = 0; i < _transformFunctions.Count; i++)
            {
                if (_transformFunctions[i].TransformFunction is IBivariateTransformFunction)
                {
                    anyBivariateChild = true;
                    break;
                }
            }

            if (circularChild == null && !anyBivariateChild && AllChildrenConfigured())
            {
                var (lower, upper, differ) = ChildDomain();
                if (upper <= lower)
                    messages.Add("Error: The composite transform function's child input domains do not overlap; a weighted average requires every child to be evaluable at every input.");
                else if (differ)
                    messages.Add("Warning: The composite transform function's child input domains differ; the composite evaluates only over their intersection.");
            }

            for (int i = 0; i < _transformFunctions.Count; i++)
            {
                var function = _transformFunctions[i].TransformFunction;
                if (function == null) continue;

                // A bivariate child has no univariate sampling surface — its one-argument
                // SampleFunction contract throws by design, so the composite average cannot
                // include it.
                if (function is IBivariateTransformFunction)
                {
                    messages.Add($"Error: The transform function '{function.Name}' is bivariate; a composite transform function cannot combine bivariate transform functions.");
                    continue;
                }

                // A cyclic child would recurse forever through its own Validate; the circular error
                // above already reports the precise cause.
                if (circularChild == null && !function.Validate().IsValid)
                    messages.Add($"Error: The selected transform function '{function.Name}' is invalid.");

                if (function.SpecifiedHazard != SpecifiedHazard)
                    messages.Add($"Warning: The transform function '{function.Name}' does not match hazard type '{SpecifiedHazard}' of the composite function.");
                if (function.HazardUnit != HazardUnit)
                    messages.Add($"Warning: The transform function '{function.Name}' does not match hazard unit '{HazardUnit}' of the composite function.");
                if (function.TransformedHazard != TransformedHazard)
                    messages.Add($"Warning: The transform function '{function.Name}' does not match transformed hazard type '{TransformedHazard}' of the composite function.");
                if (function.TransformedHazardUnit != TransformedHazardUnit)
                    messages.Add($"Warning: The transform function '{function.Name}' does not match transformed hazard unit '{TransformedHazardUnit}' of the composite function.");
            }

            return (messages.FindIndex(m => m.StartsWith("Error:", StringComparison.Ordinal)) < 0, messages);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The weighted average of the child mean curves. Under
        /// <see cref="CompositeFunctionType.EpistemicMixture"/> this is still the analytic blend —
        /// a mean pass has no realization to select a branch with — which is the wrong answer for
        /// the nonlinear downstream chain (the Jensen gap), so analysis-level validation refuses
        /// a mean-only run over an epistemic composite. The blend remains the correct
        /// deterministic probe value inside a full-uncertainty run.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown when the composite configuration is invalid.</exception>
        public override IUnivariateFunction SampleFunction()
        {
            ThrowIfUnusable(checkCycles: true);
            int count = _transformFunctions.Count;
            var functions = new IUnivariateFunction[count];
            for (int i = 0; i < count; i++)
            {
                functions[i] = _transformFunctions[i].TransformFunction!.SampleFunction();
            }
            return BuildCombined(functions);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Deterministic and RNG-free. Under <see cref="CompositeFunctionType.Average"/> every
        /// child is sampled co-monotonically at the given
        /// knowledge percentile and the weighted average is rebuilt over the resulting curves.
        /// Under <see cref="CompositeFunctionType.EpistemicMixture"/> the percentile is the
        /// single composition draw: it selects the branch by inverse-CDF of the cumulative
        /// weights and the selected child is sampled at the percentile rescaled within its weight
        /// span (the consequence composite's percentile-path convention).
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown when the composite configuration is invalid.</exception>
        public override IUnivariateFunction SampleFunction(double percentile)
        {
            ThrowIfUnusable(checkCycles: true);
            if (_compositeFunctionType == CompositeFunctionType.EpistemicMixture)
            {
                var (index, childPercentile) = SelectMixtureChild(percentile, rescale: true);
                return _transformFunctions[index].TransformFunction!.SampleFunction(childPercentile);
            }
            int count = _transformFunctions.Count;
            var functions = new IUnivariateFunction[count];
            for (int i = 0; i < count; i++)
            {
                functions[i] = _transformFunctions[i].TransformFunction!.SampleFunction(percentile);
            }
            return BuildCombined(functions);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The per-realization path: every child samples realization
        /// <paramref name="realizationIndex"/> from its own content-seeded sampler (children are
        /// mutually independent), and the weighted average is rebuilt over the resulting curves.
        /// Under <see cref="CompositeFunctionType.EpistemicMixture"/> the realization's selector
        /// percentile (the composite's own declared dimension, or the shared variable's draw when
        /// bound) picks one branch, and that child alone is sampled at the same realization index
        /// — its curve chains downstream whole, because the selected child is the realization's
        /// whole truth.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown when the composite configuration is invalid.</exception>
        public override IUnivariateFunction SampleFunction(int realizationIndex)
        {
            ThrowIfUnusable(checkCycles: false);
            if (_compositeFunctionType == CompositeFunctionType.EpistemicMixture)
            {
                var (index, _) = SelectMixtureChild(Percentile(realizationIndex, 0), rescale: false);
                return _transformFunctions[index].TransformFunction!.SampleFunction(realizationIndex);
            }
            int count = _transformFunctions.Count;
            var functions = new IUnivariateFunction[count];
            for (int i = 0; i < count; i++)
            {
                functions[i] = _transformFunctions[i].TransformFunction!.SampleFunction(realizationIndex);
            }
            return BuildCombined(functions);
        }

        /// <summary>
        /// The branch the given realization selects under
        /// <see cref="CompositeFunctionType.EpistemicMixture"/> — the branch-attribution query
        /// ("which model alternative did this realization live in"). Runtime-only: nothing is
        /// persisted, and the answer is re-derivable bit-exactly from the content seeds.
        /// </summary>
        /// <param name="realizationIndex">The realization row, in [0, <see cref="RiskFunctionBase.SampleSize"/>).</param>
        /// <returns>The selected entry index in <see cref="TransformFunctions"/> declared order.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the composite is not in epistemic mode, or when
        /// <see cref="SetupSampler"/> has not been called.
        /// </exception>
        public int SelectedBranchIndex(int realizationIndex)
        {
            if (_compositeFunctionType != CompositeFunctionType.EpistemicMixture)
                throw new InvalidOperationException("Branch attribution is defined only in EpistemicMixture mode.");
            var (index, _) = SelectMixtureChild(Percentile(realizationIndex, 0), rescale: false);
            return index;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The largest child minimum — the composite's domain is the <i>intersection</i> of its
        /// children's, not their union: a weighted average needs every child evaluable at every
        /// input. See the class remarks.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown when the composite has no usable children.</exception>
        public override double MinHazard()
        {
            var (lower, _, _) = ChildDomainOrThrow();
            return lower;
        }

        /// <inheritdoc/>
        /// <remarks>The smallest child maximum — see <see cref="MinHazard"/>.</remarks>
        /// <exception cref="InvalidOperationException">Thrown when the composite has no usable children.</exception>
        public override double MaxHazard()
        {
            var (_, upper, _) = ChildDomainOrThrow();
            return upper;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The combined curve evaluated at <see cref="MinHazard"/> — the same shape the closed-form
        /// transforms use, and a tight bound for monotone children. Under
        /// <see cref="CompositeFunctionType.EpistemicMixture"/> the uncertain bound is the
        /// smallest low-percentile value across the positively weighted branches, because any of
        /// them may be the selected truth.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown when the composite configuration is invalid.</exception>
        public override double MinTransformedHazard(bool meanOnly)
        {
            if (!meanOnly && _compositeFunctionType == CompositeFunctionType.EpistemicMixture)
                return BranchExtremum(0.00001d, smallest: true);
            var function = meanOnly ? SampleFunction() : SampleFunction(0.00001d);
            return function.Function(MinHazard());
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The combined curve evaluated at <see cref="MaxHazard"/> — see
        /// <see cref="MinTransformedHazard"/>. Under
        /// <see cref="CompositeFunctionType.EpistemicMixture"/> the uncertain bound is the largest
        /// high-percentile value across the positively weighted branches.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown when the composite configuration is invalid.</exception>
        public override double MaxTransformedHazard(bool meanOnly)
        {
            if (!meanOnly && _compositeFunctionType == CompositeFunctionType.EpistemicMixture)
                return BranchExtremum(1d - 0.00001d, smallest: false);
            var function = meanOnly ? SampleFunction() : SampleFunction(1d - 0.00001d);
            return function.Function(MaxHazard());
        }

        /// <inheritdoc/>
        /// <remarks>
        /// <para>
        /// Deterministic internal Monte Carlo on a clone (so the live instance's engine sampler
        /// state is never disturbed): the clone's sampler runs <see cref="SummaryRealizations"/>
        /// median-LHS realizations seeded by
        /// <see cref="SeedHelpers.HashCombine(int, byte[], int)"/> over
        /// (<see cref="SummarySeedBase"/>, <see cref="CanonicalHash"/>, 0), and every realization's
        /// combined curve is evaluated across <see cref="UncertaintySummaryHazards"/>.
        /// </para>
        /// <para>
        /// Clone-based sampling is essential: engine sampling draws the children independently
        /// (variance Σω²σ²), so a co-monotonic percentile sweep would overstate the bands.
        /// Deterministic composites evaluate the mean curve exactly with no simulation. Returns
        /// null when <see cref="Validate"/> reports errors.
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

            var clone = (CompositeTransform)RiskFunctionFactory.CreateTransformFunction(ToXElement())!;
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
        /// The input-hazard grid that <see cref="ComputeUncertaintyResults"/> summarizes over —
        /// callers pair the returned curves with these hazards by index.
        /// </summary>
        /// <returns>
        /// <see cref="SummaryGridPoints"/> evenly spaced values across the child-domain
        /// intersection, inclusive of both bounds — the grid the closed-form transforms use.
        /// </returns>
        /// <exception cref="InvalidOperationException">Thrown when the composite has no usable children.</exception>
        public double[] UncertaintySummaryHazards()
        {
            var (lower, upper, _) = ChildDomainOrThrow();
            var hazards = new double[SummaryGridPoints];
            double step = (upper - lower) / (SummaryGridPoints - 1);
            for (int i = 0; i < SummaryGridPoints; i++) hazards[i] = lower + (i * step);
            return hazards;
        }

        /// <summary>
        /// Determines whether the given composite appears anywhere in this composite's child graph
        /// — the circular-reference guard.
        /// </summary>
        /// <param name="composite">The composite to search for.</param>
        /// <returns>True when the composite is referenced at any nesting depth.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the composite is null.</exception>
        public bool ContainsComposite(CompositeTransform composite)
        {
            if (composite == null) throw new ArgumentNullException(nameof(composite));
            return ContainsComposite(composite, new HashSet<CompositeTransform>());
        }

        #endregion

        #region Serialization

        /// <inheritdoc/>
        /// <remarks>
        /// The self-contained form (child content inline) — the storeless default. Not this type's
        /// hash surface: see <see cref="CanonicalHash"/>.
        /// </remarks>
        public override XElement ToXElement()
        {
            return ToXElement(RiskSerializationMode.SelfContained);
        }

        /// <summary>
        /// Serializes the composite in the requested mode: child content inline
        /// (<see cref="RiskSerializationMode.SelfContained"/>), or weight-bearing entries whose
        /// children are <c>FunctionReference</c> markers
        /// (<see cref="RiskSerializationMode.ByReference"/>).
        /// </summary>
        /// <param name="mode">The serialization mode; the mode propagates to nested composites.</param>
        /// <returns>The serialized form.</returns>
        public XElement ToXElement(RiskSerializationMode mode)
        {
            var element = new XElement(nameof(CompositeTransform));
            WriteIdentityAttributes(element);
            element.SetAttributeValue(nameof(SpecifiedHazard), SpecifiedHazard);
            element.SetAttributeValue(nameof(HazardUnit), HazardUnit);
            element.SetAttributeValue(nameof(TransformedHazard), TransformedHazard);
            element.SetAttributeValue(nameof(TransformedHazardUnit), TransformedHazardUnit);
            element.SetAttributeValue(nameof(CompositeFunctionType), _compositeFunctionType.ToString());
            // Conditional presence: written only when bound, so every unbound composite's form —
            // and its canonical hash — is unchanged.
            if (_epistemicVariable.Length > 0)
            {
                element.SetAttributeValue(nameof(EpistemicVariable), _epistemicVariable);
            }

            var container = new XElement(nameof(TransformFunctions));
            for (int i = 0; i < _transformFunctions.Count; i++)
            {
                var entry = _transformFunctions[i];
                var entryElement = new XElement(nameof(WeightedTransformFunction));
                entryElement.SetAttributeValue(nameof(WeightedTransformFunction.Weight), SerializationUtilities.FormatDouble(entry.Weight));
                if (entry.TransformFunction != null)
                {
                    entryElement.Add(FunctionEntry.Write(entry.TransformFunction, mode));
                }
                container.Add(entryElement);
            }
            element.Add(container);
            return element;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Hashes the projected identity form, never the persisted form (the
        /// <c>SystemComponent</c> identity-form exception): the combine mode, the entry count, and
        /// per entry the effective weight and the child's own canonical hash. The weight coercion
        /// under <see cref="CompositeFunctionType.Additive"/> is carried for symmetry with
        /// <c>CompositeConsequence</c> even though Additive is a validation error, so the two
        /// curve-algebra composites keep identical recipes if a further mode is ever enabled.
        /// </remarks>
        public override byte[] CanonicalHash()
        {
            var identity = new XElement(nameof(CompositeTransform));
            identity.SetAttributeValue(nameof(CompositeFunctionType), _compositeFunctionType.ToString());
            // Conditional presence mirrors the persisted form: binding a shared epistemic
            // variable is compute-relevant, so it enters the identity only when named.
            if (_epistemicVariable.Length > 0)
            {
                identity.SetAttributeValue(nameof(EpistemicVariable), _epistemicVariable);
            }
            identity.SetAttributeValue("Count", _transformFunctions.Count.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < _transformFunctions.Count; i++)
            {
                var entry = _transformFunctions[i];
                var entryElement = new XElement(nameof(WeightedTransformFunction));
                entryElement.SetAttributeValue(nameof(WeightedTransformFunction.Weight), SerializationUtilities.FormatDouble(EffectiveWeight(i)));
                entryElement.SetAttributeValue("FunctionHash", entry.TransformFunction == null
                    ? string.Empty
                    : CanonicalContentHasher.ToTokenHex(entry.TransformFunction.CanonicalHash()));
                identity.Add(entryElement);
            }
            return CanonicalContentHasher.Hash(identity, CanonicalizationRules.ModelRules);
        }

        #endregion

        #region Private Helpers

        /// <summary>
        /// The message reported when a compute surface is reached with no usable children.
        /// </summary>
        private const string NoChildrenMessage =
            "The composite transform function has no child functions. Call Validate() and correct the reported errors before sampling.";

        /// <summary>
        /// Builds the weighted-average combine over the sampled child curves.
        /// </summary>
        /// <param name="functions">The child curves, one per entry, in declared order.</param>
        /// <returns>The combined curve.</returns>
        /// <remarks>
        /// The returned <see cref="CompositeFunction"/> keeps its <c>ConfidenceLevel</c> at the
        /// default −1 deliberately: the wrapper has already baked each child's knowledge percentile
        /// into the curve it sampled, so the combine must take the mean-convention branch
        /// (<c>Σ ωᵢ·fᵢ(x)</c> with children in their configured state) rather than re-driving them.
        /// </remarks>
        private IUnivariateFunction BuildCombined(IUnivariateFunction[] functions)
        {
            var weights = new double[functions.Length];
            for (int i = 0; i < weights.Length; i++) weights[i] = EffectiveWeight(i);
            return new CompositeFunction(functions, weights) { Mode = CompositeFunctionMode.WeightedAverage };
        }

        /// <summary>
        /// The weight the compute paths use for an entry: one under
        /// <see cref="CompositeFunctionType.Additive"/> (where weights are inert), the entry weight
        /// otherwise. Additive is a validation error here; the coercion exists so the hash recipe
        /// matches <c>CompositeConsequence</c>.
        /// </summary>
        /// <param name="index">The entry index.</param>
        /// <returns>The effective weight.</returns>
        private double EffectiveWeight(int index)
        {
            return _compositeFunctionType == CompositeFunctionType.Additive ? 1d : _transformFunctions[index].Weight;
        }

        /// <summary>
        /// Maps a composition percentile onto a child entry: entries are laid out on [0, 1] in
        /// declared order by weight, the percentile lands in one span (inclusive upper edge), and
        /// zero-weight entries are skipped. Numerical drift past the last positive weight clamps
        /// to that entry. The algorithm is the consequence composite's, verbatim, so the two
        /// selectors can never disagree.
        /// </summary>
        /// <param name="percentile">The composition percentile, in [0, 1].</param>
        /// <param name="rescale">True to rescale the percentile within the selected span (the
        /// single-uniform composition convention); false to pass it through unchanged.</param>
        /// <returns>The selected entry index and the child percentile.</returns>
        private (int Index, double ChildPercentile) SelectMixtureChild(double percentile, bool rescale)
        {
            double cumulative = 0d;
            int lastPositive = -1;
            for (int i = 0; i < _transformFunctions.Count; i++)
            {
                double weight = _transformFunctions[i].Weight;
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
        /// Counts the entries carrying strictly positive weight — the epistemic mixture's
        /// selectable branch count.
        /// </summary>
        /// <returns>The positively weighted entry count.</returns>
        private int CountPositiveWeights()
        {
            int count = 0;
            for (int i = 0; i < _transformFunctions.Count; i++)
            {
                if (_transformFunctions[i].Weight > 0d) count++;
            }
            return count;
        }

        /// <summary>
        /// The transformed-hazard extremum across the positively weighted branches at a knowledge
        /// percentile — the epistemic mode's uncertain bound, where any branch may be the
        /// selected truth. The smallest bound evaluates at <see cref="MinHazard"/>, the largest
        /// at <see cref="MaxHazard"/>.
        /// </summary>
        /// <param name="percentile">The child knowledge percentile to sample at.</param>
        /// <param name="smallest">True for the minimum at the domain's lower bound, false for the
        /// maximum at the upper bound.</param>
        /// <returns>The extremum across the selectable branches.</returns>
        private double BranchExtremum(double percentile, bool smallest)
        {
            double hazard = smallest ? MinHazard() : MaxHazard();
            double extremum = smallest ? double.MaxValue : double.MinValue;
            for (int i = 0; i < _transformFunctions.Count; i++)
            {
                var entry = _transformFunctions[i];
                if (entry.TransformFunction == null || entry.Weight <= 0d) continue;
                double value = entry.TransformFunction.SampleFunction(percentile).Function(hazard);
                if (smallest ? value < extremum : value > extremum) extremum = value;
            }
            return extremum;
        }

        /// <summary>
        /// True when this composite, or any nested composite transform beneath it, is in
        /// <see cref="CompositeFunctionType.EpistemicMixture"/> mode — the analysis-level
        /// mean-only gate's discovery surface.
        /// </summary>
        /// <returns>True when an epistemic mixture exists anywhere in the subtree.</returns>
        internal bool UsesEpistemicMode()
        {
            return UsesEpistemicMode(new HashSet<CompositeTransform>());
        }

        /// <summary>
        /// Accumulates every shared epistemic variable named by this composite or any nested
        /// composite transform — the sharing scope's discovery surface.
        /// </summary>
        /// <param name="sink">The accumulating distinct variable names.</param>
        /// <exception cref="ArgumentNullException">Thrown when the sink is null.</exception>
        internal void CollectEpistemicVariables(ISet<string> sink)
        {
            if (sink == null) throw new ArgumentNullException(nameof(sink));
            CollectEpistemicVariables(sink, new HashSet<CompositeTransform>());
        }

        /// <summary>
        /// The cycle-safe recursion behind <see cref="UsesEpistemicMode()"/>.
        /// </summary>
        /// <param name="visited">The composites already searched.</param>
        /// <returns>True when an epistemic mixture exists anywhere in the subtree.</returns>
        private bool UsesEpistemicMode(HashSet<CompositeTransform> visited)
        {
            if (!visited.Add(this)) return false;
            if (_compositeFunctionType == CompositeFunctionType.EpistemicMixture) return true;
            for (int i = 0; i < _transformFunctions.Count; i++)
            {
                if (_transformFunctions[i].TransformFunction is CompositeTransform nested && nested.UsesEpistemicMode(visited))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The cycle-safe recursion behind <see cref="CollectEpistemicVariables(ISet{string})"/>.
        /// </summary>
        /// <param name="sink">The accumulating distinct variable names.</param>
        /// <param name="visited">The composites already searched.</param>
        private void CollectEpistemicVariables(ISet<string> sink, HashSet<CompositeTransform> visited)
        {
            if (!visited.Add(this)) return;
            if (_compositeFunctionType == CompositeFunctionType.EpistemicMixture && _epistemicVariable.Length > 0)
                sink.Add(_epistemicVariable);
            for (int i = 0; i < _transformFunctions.Count; i++)
            {
                if (_transformFunctions[i].TransformFunction is CompositeTransform nested)
                    nested.CollectEpistemicVariables(sink, visited);
            }
        }

        /// <summary>
        /// Accumulates the logic-tree axes declared by this composite or any nested composite
        /// transform — the exact enumerator's cycle-safe discovery surface. A bound composite
        /// merges into its variable's axis (weight disagreement recorded), an unbound epistemic
        /// composite is its own axis, and every epistemic composite's id feeds the pin-conflict
        /// gate.
        /// </summary>
        /// <param name="boundAxes">The bound axes, keyed by variable name.</param>
        /// <param name="unboundAxes">The unbound axes, in discovery order.</param>
        /// <param name="mismatchedVariables">The sink recording variables whose binders declare differing weight vectors.</param>
        /// <param name="epistemicFunctionIds">The sink recording every epistemic composite id.</param>
        /// <param name="visited">The functions already searched (reference identity, shared across clusters).</param>
        internal void CollectLogicTreeAxes(IDictionary<string, LogicTreeAxisSeed> boundAxes,
            IList<LogicTreeAxisSeed> unboundAxes, ISet<string> mismatchedVariables,
            ISet<Guid> epistemicFunctionIds, ISet<IRiskFunction> visited)
        {
            if (!visited.Add(this)) return;
            if (_compositeFunctionType == CompositeFunctionType.EpistemicMixture)
            {
                var weights = new double[_transformFunctions.Count];
                for (int i = 0; i < weights.Length; i++) weights[i] = _transformFunctions[i].Weight;
                LogicTreeAxisSeed.Register(_epistemicVariable, Id, Name, weights,
                    boundAxes, unboundAxes, mismatchedVariables, epistemicFunctionIds);
            }
            for (int i = 0; i < _transformFunctions.Count; i++)
            {
                if (_transformFunctions[i].TransformFunction is CompositeTransform nested)
                    nested.CollectLogicTreeAxes(boundAxes, unboundAxes, mismatchedVariables, epistemicFunctionIds, visited);
            }
        }

        /// <summary>
        /// Determines whether every entry carries a configured function.
        /// </summary>
        /// <returns>True when there is at least one entry and none is null.</returns>
        private bool AllChildrenConfigured()
        {
            if (_transformFunctions.Count == 0) return false;
            for (int i = 0; i < _transformFunctions.Count; i++)
            {
                if (_transformFunctions[i].TransformFunction == null) return false;
            }
            return true;
        }

        /// <summary>
        /// The intersection of the children's input domains, and whether the child domains differ.
        /// </summary>
        /// <returns>The intersection bounds and a flag set when the children do not all agree.</returns>
        private (double Lower, double Upper, bool Differ) ChildDomain()
        {
            double lower = double.MinValue;
            double upper = double.MaxValue;
            bool differ = false;
            bool first = true;
            double firstLower = 0d;
            double firstUpper = 0d;

            for (int i = 0; i < _transformFunctions.Count; i++)
            {
                var function = _transformFunctions[i].TransformFunction;
                if (function == null) continue;

                double childLower = function.MinHazard();
                double childUpper = function.MaxHazard();
                if (childLower > lower) lower = childLower;
                if (childUpper < upper) upper = childUpper;

                if (first)
                {
                    firstLower = childLower;
                    firstUpper = childUpper;
                    first = false;
                }
                else if (childLower != firstLower || childUpper != firstUpper)
                {
                    differ = true;
                }
            }

            return (lower, upper, differ);
        }

        /// <summary>
        /// <see cref="ChildDomain"/>, throwing when the composite has no configured children.
        /// </summary>
        /// <returns>The intersection bounds and the differing-domains flag.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the composite has no usable children.</exception>
        private (double Lower, double Upper, bool Differ) ChildDomainOrThrow()
        {
            bool any = false;
            for (int i = 0; i < _transformFunctions.Count; i++)
            {
                if (_transformFunctions[i].TransformFunction != null) { any = true; break; }
            }
            if (!any) throw new InvalidOperationException(NoChildrenMessage);
            return ChildDomain();
        }

        /// <summary>
        /// The sample-time usability gate: the Average or EpistemicMixture combine mode, at least
        /// one entry, every
        /// entry configured and univariate (a bivariate child has no univariate sampling
        /// surface), weights in [0, 1] summing to one, a variable bound only in epistemic mode,
        /// and an overlapping child domain.
        /// </summary>
        /// <param name="checkCycles">True to also reject circular references.</param>
        /// <exception cref="InvalidOperationException">Thrown when the configuration is invalid.</exception>
        private void ThrowIfUnusable(bool checkCycles)
        {
            bool usable = (_compositeFunctionType == CompositeFunctionType.Average ||
                _compositeFunctionType == CompositeFunctionType.EpistemicMixture) && _transformFunctions.Count > 0;
            if (usable && _epistemicVariable.Length > 0 && _compositeFunctionType != CompositeFunctionType.EpistemicMixture)
                usable = false;
            if (usable)
            {
                double weightSum = 0d;
                for (int i = 0; i < _transformFunctions.Count; i++)
                {
                    var entry = _transformFunctions[i];
                    if (entry.TransformFunction == null || entry.TransformFunction is IBivariateTransformFunction ||
                        entry.Weight < 0d || entry.Weight > 1d)
                    {
                        usable = false;
                        break;
                    }
                    weightSum += entry.Weight;
                }
                if (usable && Math.Abs(weightSum - 1d) > WeightSumTolerance) usable = false;
            }
            if (usable && checkCycles && FindCircularChild() != null) usable = false;
            if (usable)
            {
                var (lower, upper, _) = ChildDomain();
                if (upper <= lower) usable = false;
            }

            if (!usable)
                throw new InvalidOperationException("The composite transform configuration is invalid. Call Validate() and correct the reported errors before sampling.");
        }

        /// <summary>
        /// Finds the first child that closes a cycle back to this composite, or null when the child
        /// graph is acyclic.
        /// </summary>
        /// <returns>The offending child function, or null.</returns>
        private ITransformFunction? FindCircularChild()
        {
            for (int i = 0; i < _transformFunctions.Count; i++)
            {
                var function = _transformFunctions[i].TransformFunction;
                if (function is not CompositeTransform nested) continue;
                if (ReferenceEquals(nested, this) || nested.ContainsComposite(this)) return function;
            }
            return null;
        }

        /// <summary>
        /// The cycle-safe recursion behind <see cref="ContainsComposite(CompositeTransform)"/>.
        /// </summary>
        /// <param name="target">The composite being searched for.</param>
        /// <param name="visited">The composites already searched (guards cycles not involving the target).</param>
        /// <returns>True when the target is referenced at any nesting depth.</returns>
        private bool ContainsComposite(CompositeTransform target, HashSet<CompositeTransform> visited)
        {
            if (!visited.Add(this)) return false;
            for (int i = 0; i < _transformFunctions.Count; i++)
            {
                var function = _transformFunctions[i].TransformFunction;
                if (ReferenceEquals(function, target)) return true;
                if (function is CompositeTransform nested && nested.ContainsComposite(target, visited)) return true;
            }
            return false;
        }

        /// <summary>
        /// Keeps the composite's change subscriptions in step with the entry list, and reports the
        /// membership change as <c>TransformFunctions</c>.
        /// </summary>
        /// <param name="sender">The entry collection.</param>
        /// <param name="e">The membership change.</param>
        private void TransformFunctionsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            ReconcileEntrySubscriptions();
            RaisePropertyChange(nameof(TransformFunctions));
        }

        /// <summary>
        /// Subscribes to every entry now in the list and unsubscribes from every entry that has left
        /// it, using <see cref="_subscribedEntries"/> as the record of what is attached.
        /// </summary>
        private void ReconcileEntrySubscriptions()
        {
            foreach (var stale in _subscribedEntries.Where(entry => !_transformFunctions.Contains(entry)).ToList())
            {
                stale.PropertyChanged -= EntryPropertyChanged;
                _subscribedEntries.Remove(stale);
            }

            for (int i = 0; i < _transformFunctions.Count; i++)
            {
                var entry = _transformFunctions[i];
                if (entry != null && _subscribedEntries.Add(entry)) entry.PropertyChanged += EntryPropertyChanged;
            }
        }

        /// <summary>
        /// Re-raises an entry-level change as a change of the composite's entry list. Reentrant
        /// notifications are suppressed via <see cref="_raisingEntryChange"/> so a cyclic composite
        /// graph degrades to a reportable validation error instead of unbounded recursion.
        /// </summary>
        /// <param name="sender">The entry.</param>
        /// <param name="e">The originating change arguments.</param>
        private void EntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_raisingEntryChange) return;
            _raisingEntryChange = true;
            try
            {
                RaisePropertyChange(nameof(TransformFunctions));
            }
            finally
            {
                _raisingEntryChange = false;
            }
        }

        #endregion
    }
}
