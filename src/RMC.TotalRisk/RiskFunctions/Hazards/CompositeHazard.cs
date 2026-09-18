using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Numerics.Data;
using Numerics.Data.Statistics;
using Numerics.Distributions;
using Numerics.Mathematics.LinearAlgebra;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.Core.Support;

namespace RMC.TotalRisk.RiskFunctions.Hazards
{
    /// <summary>
    /// A composite hazard function: combines a weighted list of child hazard functions as a mixture
    /// distribution (<see cref="CompositeCombinationType.Mixture"/>, the default), as a
    /// competing-risks maximum-rule combination
    /// (<see cref="CompositeCombinationType.CompetingRisks"/>) — the dam-safety practice of
    /// evaluating gate-failure or debris-blockage scenarios as separate analyses and assigning a
    /// likelihood to each — or as an epistemic mixture
    /// (<see cref="CompositeCombinationType.EpistemicMixture"/>), the logic tree over alternative
    /// hazard descriptions of which exactly one is true.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Ported from v1.0 <c>CompositeHazard</c>. <b>Mixture</b> builds a
    /// <see cref="Numerics.Distributions.Mixture"/> over the children sampled at the same
    /// realization: <c>F(x) = Σ ωᵢ·Fᵢ(x)</c>, the 2024 verification report's Equation 49.
    /// <b>CompetingRisks</b> builds a <see cref="Numerics.Distributions.CompetingRisks"/> with
    /// <c>MinimumOfRandomVariables = false</c> (the maximum rule — the governing, most severe
    /// loading controls) under the configured <see cref="Dependency"/>; weights are inert there.
    /// </para>
    /// <para>
    /// <b>The mixture is aleatory by design.</b> The weights are the fraction of the event
    /// population each child describes, so the mixture is a single distribution carried through
    /// every realization — there is no per-realization branch selection and
    /// <see cref="SamplingDimensions"/> is zero in Mixture and CompetingRisks mode. This is the
    /// deliberate divergence
    /// from <c>CompositeConsequence</c>, and it needs no exposure-branch surface: a hazard
    /// realization is <i>already a distribution</i>, so the mixture folds into it losslessly and
    /// the loss-exceedance tail is exact. (The defect the consequence composite's exposure
    /// branches solve — collapsing a mixture to its weighted-mean <i>curve</i> — simply does not
    /// arise.) Knowledge uncertainty enters through the children's own posteriors, exactly as
    /// Tables 45 and 46 of the RMC-TotalRisk Verification Report model it.
    /// </para>
    /// <para>
    /// <b>EpistemicMixture is the logic tree.</b> The weights are the analyst's credence that
    /// each child is the true description, so the selection is a knowledge draw: the composite
    /// declares one sampling dimension of its own, and each realization selects one child by
    /// inverse-CDF of the cumulative weights at that realization's selector percentile, then
    /// samples the selected child at the same realization index (child streams are untouched).
    /// The ensemble therefore carries branch-conditional realizations — the epistemic percentiles
    /// straddle the alternatives instead of blending them, which is the Jensen-gap distinction
    /// the composite-functions doctrine works through. The mean overload still returns the
    /// analytic blend (a mean pass has no realization to select with), so a mean-only analysis
    /// over an epistemic composite is refused by analysis-level validation rather than silently
    /// answering with the blend. Naming an <see cref="EpistemicVariable"/> shares the selector
    /// draw with every other composite bound to the same variable (state-of-knowledge
    /// correlation); the selection then conditions like any declared dimension, so a fractile pin
    /// on the composite holds its branch choice.
    /// </para>
    /// <para>
    /// <b>Improved over v1.0</b> (each covered by test): the percentile overload is deterministic
    /// and RNG-free — v1.0 built <c>new Random((int)Math.Round(1 + p·100000))</c> and fed children
    /// <c>NextDouble()</c>, so percentiles differing by less than 1e-5 collided onto one seed;
    /// children own their interpolation transforms rather than having the composite's pushed onto
    /// them; the all-empirical union-knot collapse is not ported
    /// (a lossy re-tabulation that <c>CreateEmpiricalCDF()</c> supersedes — the exact combined CDF
    /// agrees at every union knot and is exact between them); an empty composite throws instead of
    /// returning the <see cref="double.MaxValue"/> sentinel that silently poisons integration
    /// bounds; the circular-reference recursion carries a visited set; and
    /// <see cref="SetupSampler"/> rejects a posterior-indexed child whose realization capacity is
    /// below the requested sample size, rather than letting it throw mid-loop.
    /// </para>
    /// <para>
    /// <b>Serialization:</b> under <see cref="RiskSerializationMode.SelfContained"/> (the default)
    /// child content is written inline; under <see cref="RiskSerializationMode.ByReference"/> each
    /// entry carries only its weight and a <c>FunctionReference</c> marker. <b>Hashing</b> uses a
    /// projected identity form — the combination mode, the interpolation transforms, the dependence
    /// (coerced out under Mixture), the entry count, and per entry the effective weight and the
    /// child's own canonical hash — never the persisted form, so the serialization mode and child
    /// metadata can never move this function's hash (the <c>SystemComponent</c>
    /// identity-form exception).
    /// </para>
    /// </remarks>
    public class CompositeHazard : UnivariateHazardBase
    {
        #region Construction

        /// <summary>
        /// Initializes an empty composite with the v1.0 defaults (Mixture mode, independent, no
        /// children).
        /// </summary>
        public CompositeHazard()
        {
            HazardFunctions = new ObservableCollection<WeightedHazardFunction>();
        }

        /// <summary>
        /// Initializes a composite over the specified weighted children (Mixture mode).
        /// </summary>
        /// <param name="hazardFunctions">The weighted child entries.</param>
        /// <exception cref="ArgumentNullException">Thrown when the sequence is null.</exception>
        public CompositeHazard(IEnumerable<WeightedHazardFunction> hazardFunctions)
        {
            if (hazardFunctions == null) throw new ArgumentNullException(nameof(hazardFunctions));
            HazardFunctions = new ObservableCollection<WeightedHazardFunction>(hazardFunctions);
        }

        /// <summary>
        /// Restores a composite hazard function from its serialized form.
        /// </summary>
        /// <param name="xElement">The serialized form produced by <see cref="ToXElement(RiskSerializationMode)"/>.</param>
        /// <param name="resolver">
        /// The function resolver, required only to read a by-reference form. An unresolvable
        /// reference keeps its weighted entry with a null function — the weight survives the
        /// round-trip — and is reported by <see cref="Validate"/>.
        /// </param>
        /// <exception cref="ArgumentNullException">Thrown when the element is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when inline child content cannot be reconstructed (dropping it would lose model
        /// content on the next save), or when a serialized reference id is stale.
        /// </exception>
        public CompositeHazard(XElement xElement, IRiskFunctionResolver? resolver = null)
        {
            if (xElement == null) throw new ArgumentNullException(nameof(xElement));

            ReadIdentityAttributes(xElement);
            SpecifiedHazard = SerializationUtilities.ReadString(xElement, nameof(SpecifiedHazard));
            HazardUnit = SerializationUtilities.ReadString(xElement, nameof(HazardUnit));
            _compositeCombinationType = SerializationUtilities.ReadEnum(xElement, nameof(CompositeCombinationType), CompositeCombinationType.Mixture);
            _dependency = SerializationUtilities.ReadEnum(xElement, nameof(Dependency), DependencyType.Independent);
            _correlationMatrix = SerializationUtilities.ParseMatrix(SerializationUtilities.ReadString(xElement, nameof(CorrelationMatrix)));
            _hazardTransform = SerializationUtilities.ReadEnum(xElement, nameof(HazardTransform), Transform.None);
            _probabilityTransform = SerializationUtilities.ReadEnum(xElement, nameof(ProbabilityTransform), Transform.NormalZ);
            _epistemicVariable = SerializationUtilities.ReadString(xElement, nameof(EpistemicVariable));

            HazardFunctions = new ObservableCollection<WeightedHazardFunction>();
            var container = xElement.Element(nameof(HazardFunctions));
            if (container != null)
            {
                foreach (var entryElement in container.Elements(nameof(WeightedHazardFunction)))
                {
                    double weight = SerializationUtilities.ReadDouble(entryElement, nameof(WeightedHazardFunction.Weight));
                    IHazardFunction? function = null;
                    var child = entryElement.Elements().FirstOrDefault();
                    if (child != null)
                    {
                        // The resolver threads into the inline factory so an inline nested
                        // composite can resolve its own by-reference children.
                        function = FunctionEntry.Read<IHazardFunction>(
                            child, resolver, c => RiskFunctionFactory.CreateFromXElement(c, resolver),
                            Name, $"The {nameof(CompositeHazard)} '{Name}'", "hazard function",
                            _unresolvedFunctionReferences);
                    }

                    // The entry is kept even when the function is null (no child serialized, or an
                    // unresolvable reference): dropping it would silently change the weight list —
                    // and therefore the hash and the weight-sum validation — on the next save.
                    HazardFunctions.Add(new WeightedHazardFunction(function, weight));
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
        /// <see cref="ComputeUncertaintyResults"/> sampling seed — content-based, so the summary is
        /// deterministic for identical compute content and independent of names or ids.
        /// </summary>
        private const int SummarySeedBase = 12345;

        /// <summary>
        /// The tolerance on the weight sum in Mixture mode — the same gate
        /// <see cref="Numerics.Distributions.Mixture"/> applies to its own weights. Deliberately
        /// looser than machine epsilon so user-entered decimal weights (0.3/0.2/0.5) validate.
        /// </summary>
        private const double WeightSumTolerance = 1e-8;

        /// <summary>
        /// Backing field for <see cref="HazardFunctions"/>. Assigned through the property by every
        /// constructor, so the collection subscription is always attached.
        /// </summary>
        private ObservableCollection<WeightedHazardFunction> _hazardFunctions = null!;

        /// <summary>
        /// Backing field for <see cref="CompositeCombinationType"/> — the v1.0 default is Mixture
        /// (<c>IsMixture = true</c>).
        /// </summary>
        private CompositeCombinationType _compositeCombinationType = CompositeCombinationType.Mixture;

        /// <summary>
        /// Backing field for <see cref="Dependency"/>; meaningful only under CompetingRisks.
        /// </summary>
        private DependencyType _dependency = DependencyType.Independent;

        /// <summary>
        /// Backing field for <see cref="CorrelationMatrix"/>; meaningful only under CompetingRisks
        /// with <see cref="DependencyType.CorrelationMatrix"/>.
        /// </summary>
        private double[,]? _correlationMatrix;

        /// <summary>
        /// Backing field for <see cref="HazardTransform"/> — the v1.0 default is None.
        /// </summary>
        private Transform _hazardTransform = Transform.None;

        /// <summary>
        /// Backing field for <see cref="ProbabilityTransform"/> — the v1.0 default is NormalZ.
        /// </summary>
        private Transform _probabilityTransform = Transform.NormalZ;

        /// <summary>
        /// Backing field for <see cref="EpistemicVariable"/> — empty means unbound.
        /// </summary>
        private string _epistemicVariable = string.Empty;

        /// <summary>
        /// The distinct entries this composite currently holds a change subscription on — the
        /// shadow of <see cref="HazardFunctions"/> that
        /// <see cref="HazardFunctionsCollectionChanged"/> reconciles against.
        /// </summary>
        /// <remarks>
        /// A shadow set rather than per-item bookkeeping off the event arguments, because
        /// <see cref="NotifyCollectionChangedAction.Reset"/> — which <c>Clear()</c> raises —
        /// carries no <c>OldItems</c>. Reconciling also makes duplicates safe: an entry listed
        /// twice is subscribed once.
        /// </remarks>
        private readonly HashSet<WeightedHazardFunction> _subscribedEntries = new HashSet<WeightedHazardFunction>();

        /// <summary>
        /// Descriptions of serialized function references that could not be resolved, reported by
        /// <see cref="Validate"/> so the precise cause is visible instead of a generic message.
        /// </summary>
        private readonly List<string> _unresolvedFunctionReferences = new List<string>();

        /// <summary>
        /// True while <see cref="EntryPropertyChanged"/> is re-raising, breaking the notification
        /// feedback loop a cyclic composite graph would otherwise create. Cycles are validation
        /// errors, but the event wiring must stay crash-free so <see cref="Validate"/> can actually
        /// report them — a consuming layer that momentarily wires a cycle must get an error
        /// message, not a stack overflow.
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
        /// change as <c>HazardFunctions</c>. Declared order is compute-relevant: it drives the
        /// child sampler ordinals and the hashed entry order.
        /// </remarks>
        public ObservableCollection<WeightedHazardFunction> HazardFunctions
        {
            get { return _hazardFunctions; }
            set
            {
                if (ReferenceEquals(_hazardFunctions, value)) return;

                if (_hazardFunctions != null) _hazardFunctions.CollectionChanged -= HazardFunctionsCollectionChanged;
                _hazardFunctions = value ?? new ObservableCollection<WeightedHazardFunction>();
                _hazardFunctions.CollectionChanged += HazardFunctionsCollectionChanged;

                ReconcileEntrySubscriptions();
                RaisePropertyChange(nameof(HazardFunctions));
            }
        }

        /// <summary>
        /// How the children are combined. Compute-relevant: part of the hashed identity form.
        /// </summary>
        public CompositeCombinationType CompositeCombinationType
        {
            get { return _compositeCombinationType; }
            set
            {
                if (_compositeCombinationType != value)
                {
                    _compositeCombinationType = value;
                    RaisePropertyChange(nameof(CompositeCombinationType));
                }
            }
        }

        /// <summary>
        /// The statistical dependence between the children under
        /// <see cref="CompositeCombinationType.CompetingRisks"/>. Inert under Mixture — a mixture
        /// is a single distribution, not a joint event — and coerced out of the canonical hash
        /// there, so editing it on a mixture composite cannot re-roll seeds.
        /// </summary>
        public DependencyType Dependency
        {
            get { return _dependency; }
            set
            {
                if (_dependency != value)
                {
                    _dependency = value;
                    RaisePropertyChange(nameof(Dependency));
                }
            }
        }

        /// <summary>
        /// The user-specified correlation matrix, used only under
        /// <see cref="CompositeCombinationType.CompetingRisks"/> with
        /// <see cref="DependencyType.CorrelationMatrix"/> (one row and column per child, positive
        /// definite). Serialized and hashed only in that combination.
        /// </summary>
        public double[,]? CorrelationMatrix
        {
            get { return _correlationMatrix; }
            set
            {
                _correlationMatrix = value;
                RaisePropertyChange(nameof(CorrelationMatrix));
            }
        }

        /// <summary>
        /// The interpolation transform applied to the hazard axis of the combined distribution's
        /// empirical CDF. Compute-relevant in both modes: it drives the
        /// <c>CreateEmpiricalCDF()</c> interpolation the engine's <c>InverseCDF</c> rides.
        /// </summary>
        public Transform HazardTransform
        {
            get { return _hazardTransform; }
            set
            {
                if (_hazardTransform != value)
                {
                    _hazardTransform = value;
                    RaisePropertyChange(nameof(HazardTransform));
                }
            }
        }

        /// <summary>
        /// The interpolation transform applied to the probability axis of the combined
        /// distribution's empirical CDF (v1.0 default NormalZ). Compute-relevant in both modes.
        /// </summary>
        public Transform ProbabilityTransform
        {
            get { return _probabilityTransform; }
            set
            {
                if (_probabilityTransform != value)
                {
                    _probabilityTransform = value;
                    RaisePropertyChange(nameof(ProbabilityTransform));
                }
            }
        }

        /// <summary>
        /// The named shared epistemic variable the branch selection binds to, empty (the default)
        /// when the composite selects independently. Meaningful only under
        /// <see cref="CompositeCombinationType.EpistemicMixture"/> — naming a variable in any
        /// other mode is a validation error. During an analysis run (or a standalone
        /// component-scope setup), every bound composite of the same variable receives the same
        /// per-realization selector draw, derived from the variable name alone — the logic tree's
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
        public override HazardFunctionType FunctionType => HazardFunctionType.Composite;

        /// <inheritdoc/>
        /// <remarks>
        /// Deterministic when every non-null child is. There is deliberately <b>no</b> mixture
        /// special case under the aleatory modes (the divergence from <c>CompositeConsequence</c>):
        /// the mixture is aleatory, so no branch is drawn per realization and a mixture of fixed
        /// distributions is itself one fixed distribution. Under
        /// <see cref="CompositeCombinationType.EpistemicMixture"/> the selection itself is a
        /// knowledge draw, so the composite is never deterministic while two or more branches
        /// carry positive weight — even over fixed children.
        /// </remarks>
        public override bool IsDeterministic
        {
            get
            {
                if (_compositeCombinationType == CompositeCombinationType.EpistemicMixture && CountPositiveWeights() >= 2)
                    return false;
                for (int i = 0; i < _hazardFunctions.Count; i++)
                {
                    var function = _hazardFunctions[i].HazardFunction;
                    if (function != null && !function.IsDeterministic) return false;
                }
                return true;
            }
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Zero under the aleatory modes: the combination consumes no knowledge draw of its own.
        /// One under <see cref="CompositeCombinationType.EpistemicMixture"/> — the branch-selector
        /// dimension. Children own their dimensions and are set up recursively by
        /// <see cref="SetupSampler"/> (docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §5.8.5).
        /// </remarks>
        public override int SamplingDimensions =>
            _compositeCombinationType == CompositeCombinationType.EpistemicMixture ? 1 : 0;

        #endregion

        #region IRiskFunction Methods

        /// <inheritdoc/>
        /// <remarks>
        /// Recurses into every child with a content-derived seed:
        /// <c>SeedHelpers.HashCombine(seed, child.CanonicalHash(), ordinal)</c>. The ordinal gives
        /// identical-content siblings independent draws; the child hash is metadata-inert, so
        /// renaming a child can never change results. Nested composites recurse naturally. Under
        /// <see cref="CompositeCombinationType.EpistemicMixture"/> the base call also allocates
        /// the one-column branch-selector matrix from this composite's own seed; when the
        /// composite is bound to an <see cref="EpistemicVariable"/> and a sharing scope is active,
        /// the selector column is then overwritten with the variable's shared draw — seed-inert
        /// to every other function, exactly like a fractile pin. An unbound composite consults
        /// the scope for a column keyed by its own <see cref="RiskFunctionBase.Id"/> instead —
        /// populated only by the exact logic-tree enumerator's forcing scope, so every ordinary
        /// run leaves the unbound selector untouched.
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the composite configuration is invalid, or when a posterior-indexed child
        /// cannot serve the requested sample size.
        /// </exception>
        public override void SetupSampler(int sampleSize, int seed, SamplingScheme scheme)
        {
            ThrowIfUnusable(checkCycles: true);
            base.SetupSampler(sampleSize, seed, scheme);

            if (_compositeCombinationType == CompositeCombinationType.EpistemicMixture)
            {
                var shared = _epistemicVariable.Length > 0
                    ? EpistemicSharingScope.TryGetColumn(_epistemicVariable)
                    : EpistemicSharingScope.TryGetColumnById(Id);
                if (shared != null) OverrideSelectorColumn(shared);
            }

            for (int i = 0; i < _hazardFunctions.Count; i++)
            {
                var child = _hazardFunctions[i].HazardFunction;
                if (child == null) continue;
                CompositeSupport.ThrowIfPosteriorCapacityTooSmall(child, sampleSize, Name);
                child.SetupSampler(sampleSize, SeedHelpers.HashCombine(seed, child.CanonicalHash(), i), scheme);
            }
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Errors (invalidating): missing axis labels; no children (or an unresolved serialized
        /// reference, reported precisely instead); a null child entry; a bivariate child (which
        /// has no univariate collapse — the composite would silently combine its X marginal
        /// alone); Mixture or EpistemicMixture weights outside
        /// [0, 1] or not summing to one (±1e-8, the Numerics <c>Mixture</c> gate); an
        /// <see cref="EpistemicVariable"/> named outside EpistemicMixture mode (dead hashed
        /// content); a correlation
        /// matrix that is missing, wrongly dimensioned, or not positive definite when the
        /// competing-risks dependence requires one; a circular reference through nested composites;
        /// an invalid child (summary line only — the child reports its own details where it is
        /// stored). Warnings (advisory): child axis labels that do not match the composite's
        /// (labels are unhashed metadata and never gate compute),
        /// a single-entry competing-risks combination, which degenerates to that child, and an
        /// epistemic mixture with fewer than two positively weighted branches, which degenerates
        /// to the one branch it can select.
        /// </remarks>
        public override (bool IsValid, List<string> ValidationMessages) Validate()
        {
            var messages = new List<string>();

            if (string.IsNullOrEmpty(SpecifiedHazard))
                messages.Add("Error: The composite hazard function does not have a specified hazard type.");
            if (string.IsNullOrEmpty(HazardUnit))
                messages.Add("Error: The composite hazard function does not have a specified hazard unit.");

            foreach (string reference in _unresolvedFunctionReferences)
            {
                messages.Add($"Error: The composite hazard function '{Name}' references {reference}, which was not found.");
            }

            if (_hazardFunctions.Count == 0)
            {
                if (_unresolvedFunctionReferences.Count == 0)
                    messages.Add("Error: No hazard functions have been defined for the composite.");
                return (messages.FindIndex(m => m.StartsWith("Error:", StringComparison.Ordinal)) < 0, messages);
            }

            bool weightsApply = _compositeCombinationType != CompositeCombinationType.CompetingRisks;
            bool anyWeightOutOfRange = false;
            double weightSum = 0d;
            for (int i = 0; i < _hazardFunctions.Count; i++)
            {
                var entry = _hazardFunctions[i];
                if (entry.HazardFunction == null)
                    messages.Add("Error: A weighted hazard function has not been defined for the composite function.");
                if (weightsApply && (entry.Weight < 0d || entry.Weight > 1d)) anyWeightOutOfRange = true;
                weightSum += entry.Weight;
            }

            if (weightsApply && anyWeightOutOfRange)
                messages.Add("Error: The hazard function weight must be between 0 and 1.");
            if (weightsApply && !anyWeightOutOfRange && Math.Abs(weightSum - 1d) > WeightSumTolerance)
                messages.Add("Error: Composite hazard function weights do not sum to 1.");

            if (!IsCorrelationMatrixValid())
                messages.Add($"Error: The composite hazard function correlation matrix must be a positive definite {_hazardFunctions.Count}x{_hazardFunctions.Count} matrix.");

            if (_compositeCombinationType == CompositeCombinationType.CompetingRisks && _hazardFunctions.Count == 1)
                messages.Add("Warning: A competing-risks combination over a single hazard function degenerates to that function.");

            if (_epistemicVariable.Length > 0 && _compositeCombinationType != CompositeCombinationType.EpistemicMixture)
                messages.Add($"Error: The composite hazard function names the shared epistemic variable '{_epistemicVariable}' but is not in {nameof(CompositeCombinationType.EpistemicMixture)} mode; clear the variable or select the epistemic mode.");
            if (_compositeCombinationType == CompositeCombinationType.EpistemicMixture && !anyWeightOutOfRange && CountPositiveWeights() < 2)
                messages.Add("Warning: An epistemic mixture with fewer than two positively weighted branches degenerates to the one branch it can select.");

            var circularChild = FindCircularChild();
            if (circularChild != null)
                messages.Add($"Error: Circular reference error in the selected composite hazard function '{circularChild.Name}'.");

            for (int i = 0; i < _hazardFunctions.Count; i++)
            {
                var function = _hazardFunctions[i].HazardFunction;
                if (function == null) continue;

                // A bivariate child has no univariate collapse — the composite would silently
                // combine its X marginal alone. Rejected without recursing into its Validate,
                // which also keeps a cycle through a bivariate hazard's marginal links from
                // recursing forever.
                if (function is IBivariateHazardFunction)
                {
                    messages.Add($"Error: The hazard function '{function.Name}' is bivariate; a composite hazard function cannot combine bivariate hazard functions.");
                    continue;
                }

                // A cyclic child would recurse forever through its own Validate; the circular
                // error above already reports the precise cause.
                if (circularChild == null && !function.Validate().IsValid)
                    messages.Add($"Error: The selected hazard function '{function.Name}' is invalid.");

                if (function.SpecifiedHazard != SpecifiedHazard)
                    messages.Add($"Warning: The hazard function '{function.Name}' does not match hazard type '{SpecifiedHazard}' of the composite function.");
                if (function.HazardUnit != HazardUnit)
                    messages.Add($"Warning: The hazard function '{function.Name}' does not match hazard unit '{HazardUnit}' of the composite function.");
            }

            return (messages.FindIndex(m => m.StartsWith("Error:", StringComparison.Ordinal)) < 0, messages);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The combined mean distribution: every child contributes its own mean distribution.
        /// Under <see cref="CompositeCombinationType.EpistemicMixture"/> this is still the
        /// analytic blend — a mean pass has no realization to select a branch with — and the
        /// blend is the wrong answer for a nonlinear downstream chain (the Jensen gap), which is
        /// why analysis-level validation refuses a mean-only run over an epistemic composite. The
        /// blend remains the correct deterministic probe value inside a full-uncertainty run.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown when the composite configuration is invalid.</exception>
        public override IUnivariateDistribution SampleFunction()
        {
            ThrowIfUnusable(checkCycles: true);
            int count = _hazardFunctions.Count;
            var distributions = new IUnivariateDistribution[count];
            for (int i = 0; i < count; i++)
            {
                distributions[i] = _hazardFunctions[i].HazardFunction!.SampleFunction();
            }
            return BuildCombined(distributions);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Deterministic and RNG-free (the v1.0 percentile-reseeded <c>Random</c> is deliberately
        /// gone). Under the aleatory modes every child is sampled co-monotonically at the given
        /// knowledge percentile and the combination is rebuilt over the resulting distributions —
        /// the combination itself consumes no percentile. Under
        /// <see cref="CompositeCombinationType.EpistemicMixture"/> the percentile is the single
        /// composition draw: it selects the branch by inverse-CDF of the cumulative weights and
        /// the selected child is sampled at the percentile rescaled within its weight span (the
        /// consequence composite's percentile-path convention).
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown when the composite configuration is invalid.</exception>
        public override IUnivariateDistribution SampleFunction(double percentile)
        {
            ThrowIfUnusable(checkCycles: true);
            if (_compositeCombinationType == CompositeCombinationType.EpistemicMixture)
            {
                var (index, childPercentile) = SelectMixtureChild(percentile, rescale: true);
                return _hazardFunctions[index].HazardFunction!.SampleFunction(childPercentile);
            }
            int count = _hazardFunctions.Count;
            var distributions = new IUnivariateDistribution[count];
            for (int i = 0; i < count; i++)
            {
                distributions[i] = _hazardFunctions[i].HazardFunction!.SampleFunction(percentile);
            }
            return BuildCombined(distributions);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The per-realization path: every child samples realization
        /// <paramref name="realizationIndex"/> from its own content-seeded sampler (children are
        /// mutually independent), and the combination is rebuilt over the resulting distributions.
        /// This is the exact shape of report Table 46's oracle — bootstrap each child, then form
        /// the mixture per realization with the fixed weights. Under
        /// <see cref="CompositeCombinationType.EpistemicMixture"/> the realization's selector
        /// percentile (the composite's own declared dimension, or the shared variable's draw when
        /// bound) picks one branch, and that child alone is sampled at the same realization index
        /// — its distribution is returned as-is, because the selected child is the realization's
        /// whole truth.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown when the composite configuration is invalid.</exception>
        public override IUnivariateDistribution SampleFunction(int realizationIndex)
        {
            ThrowIfUnusable(checkCycles: false);
            if (_compositeCombinationType == CompositeCombinationType.EpistemicMixture)
            {
                var (index, _) = SelectMixtureChild(Percentile(realizationIndex, 0), rescale: false);
                return _hazardFunctions[index].HazardFunction!.SampleFunction(realizationIndex);
            }
            int count = _hazardFunctions.Count;
            var distributions = new IUnivariateDistribution[count];
            for (int i = 0; i < count; i++)
            {
                distributions[i] = _hazardFunctions[i].HazardFunction!.SampleFunction(realizationIndex);
            }
            return BuildCombined(distributions);
        }

        /// <summary>
        /// The branch the given realization selects under
        /// <see cref="CompositeCombinationType.EpistemicMixture"/> — the branch-attribution query
        /// ("which model alternative did this realization live in"). Runtime-only: nothing is
        /// persisted, and the answer is re-derivable bit-exactly from the content seeds.
        /// </summary>
        /// <param name="realizationIndex">The realization row, in [0, <see cref="RiskFunctionBase.SampleSize"/>).</param>
        /// <returns>The selected entry index in <see cref="HazardFunctions"/> declared order.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the composite is not in epistemic mode, or when
        /// <see cref="SetupSampler"/> has not been called.
        /// </exception>
        public int SelectedBranchIndex(int realizationIndex)
        {
            if (_compositeCombinationType != CompositeCombinationType.EpistemicMixture)
                throw new InvalidOperationException("Branch attribution is defined only in EpistemicMixture mode.");
            var (index, _) = SelectMixtureChild(Percentile(realizationIndex, 0), rescale: false);
            return index;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The smallest child minimum — the combined support starts where the first child's does,
        /// under either combination rule. Improved over v1.0: an empty composite throws instead of
        /// returning the legacy <see cref="double.MaxValue"/> sentinel, which silently poisons
        /// engine integration bounds. A child that reports its own sentinel (an empty tabular
        /// table, say) propagates through — the composite does not second-guess a child's contract.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown when the composite has no usable children.</exception>
        public override double MinHazard(bool meanOnly)
        {
            double minimum = double.MaxValue;
            bool any = false;
            for (int i = 0; i < _hazardFunctions.Count; i++)
            {
                var function = _hazardFunctions[i].HazardFunction;
                if (function == null) continue;
                any = true;
                double value = function.MinHazard(meanOnly);
                if (value < minimum) minimum = value;
            }
            if (!any) throw new InvalidOperationException(NoChildrenMessage);
            return minimum;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The largest child maximum. Improved over v1.0: an empty composite throws instead of
        /// returning the legacy <see cref="double.MinValue"/> sentinel.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown when the composite has no usable children.</exception>
        public override double MaxHazard(bool meanOnly)
        {
            double maximum = double.MinValue;
            bool any = false;
            for (int i = 0; i < _hazardFunctions.Count; i++)
            {
                var function = _hazardFunctions[i].HazardFunction;
                if (function == null) continue;
                any = true;
                double value = function.MaxHazard(meanOnly);
                if (value > maximum) maximum = value;
            }
            if (!any) throw new InvalidOperationException(NoChildrenMessage);
            return maximum;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// <para>
        /// Deterministic internal Monte Carlo on a clone: the composite is round-tripped through
        /// its self-contained form (so the live instance's engine sampler state is never
        /// disturbed), the clone's sampler runs <see cref="SummaryRealizations"/> median-LHS
        /// realizations seeded by <see cref="SeedHelpers.HashCombine(int, byte[], int)"/> over
        /// (<see cref="SummarySeedBase"/>, <see cref="CanonicalHash"/>, 0), and every realization's
        /// combined distribution is inverted at <see cref="UncertaintySummaryProbabilities"/>.
        /// Clone-based sampling is essential: engine sampling draws the children independently, and
        /// a co-monotonic percentile sweep would overstate the bands.
        /// </para>
        /// <para>
        /// Curves are hazard values index-aligned with
        /// <see cref="UncertaintySummaryProbabilities"/>: mean, median (mode curve), and the
        /// (1 ∓ w)/2 percentiles — the report Table 46 shape. Deterministic composites evaluate the
        /// mean distribution exactly with no simulation. Returns null when <see cref="Validate"/>
        /// reports errors.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the width is outside (0, 1).</exception>
        public override UncertaintyAnalysisResults? ComputeUncertaintyResults(double confidenceIntervalWidth = 0.9)
        {
            if (confidenceIntervalWidth <= 0d || confidenceIntervalWidth >= 1d)
                throw new ArgumentOutOfRangeException(nameof(confidenceIntervalWidth), "The confidence interval width must be between 0 and 1.");
            if (!Validate().IsValid) return null;

            double[] probabilities = UncertaintySummaryProbabilities();
            var results = new UncertaintyAnalysisResults
            {
                ModeCurve = new double[probabilities.Length],
                MeanCurve = new double[probabilities.Length],
                ConfidenceIntervals = new double[probabilities.Length, 2],
            };

            if (IsDeterministic)
            {
                var mean = SampleFunction();
                for (int i = 0; i < probabilities.Length; i++)
                {
                    double value = mean.InverseCDF(probabilities[i]);
                    results.ModeCurve[i] = value;
                    results.MeanCurve[i] = value;
                    results.ConfidenceIntervals[i, 0] = value;
                    results.ConfidenceIntervals[i, 1] = value;
                }
                return results;
            }

            var clone = (CompositeHazard)RiskFunctionFactory.CreateHazardFunction(ToXElement())!;
            int seed = ToPositiveSeed(SeedHelpers.HashCombine(SummarySeedBase, CanonicalHash(), 0));
            clone.SetupSampler(SummaryRealizations, seed, SamplingScheme.LatinHypercubeMedian);

            var values = new double[probabilities.Length, SummaryRealizations];
            for (int k = 0; k < SummaryRealizations; k++)
            {
                var distribution = clone.SampleFunction(k);
                for (int i = 0; i < probabilities.Length; i++)
                {
                    values[i, k] = distribution.InverseCDF(probabilities[i]);
                }
            }

            double tail = (1d - confidenceIntervalWidth) / 2d;
            var row = new double[SummaryRealizations];
            for (int i = 0; i < probabilities.Length; i++)
            {
                FunctionHelpers.SummarizeEnsembleRow(values, i, row, tail, results);
            }
            return results;
        }

        /// <summary>
        /// The non-exceedance probability grid that <see cref="ComputeUncertaintyResults"/>
        /// summarizes over — callers pair the returned hazard curves with these probabilities by
        /// index.
        /// </summary>
        /// <returns>The sorted distinct union of the child summary probabilities.</returns>
        /// <remarks>
        /// Parametric children contribute their own probability ordinates (inverted to
        /// non-exceedance), tabular children their table probabilities, nested composites recurse,
        /// and any other child type contributes the default ordinate set. Cyclic composites are
        /// guarded by a visited set — validate first.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown when the composite has no usable children.</exception>
        public double[] UncertaintySummaryProbabilities()
        {
            var probabilities = new SortedSet<double>();
            CollectSummaryProbabilities(probabilities, new HashSet<CompositeHazard>());
            if (probabilities.Count == 0) throw new InvalidOperationException(NoChildrenMessage);
            return probabilities.ToArray();
        }

        /// <summary>
        /// Determines whether the correlation matrix satisfies the competing-risks
        /// correlation-matrix dependence: one row and column per child entry, and positive definite
        /// (Cholesky). Always true in every other mode, whose combinations construct their own
        /// valid matrices.
        /// </summary>
        /// <returns>True when the matrix is usable (or not required).</returns>
        public bool IsCorrelationMatrixValid()
        {
            return CompositeSupport.IsValid(_compositeCombinationType, _dependency, _correlationMatrix, _hazardFunctions.Count);
        }

        /// <summary>
        /// Determines whether the given composite appears anywhere in this composite's child graph
        /// — the circular-reference guard (call with the candidate parent before assigning a child,
        /// or with <c>this</c> to detect an existing cycle).
        /// </summary>
        /// <param name="composite">The composite to search for.</param>
        /// <returns>True when the composite is referenced at any nesting depth.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the composite is null.</exception>
        /// <remarks>
        /// Improved over the v1.0 recursion: a visited set guards against cycles that do not
        /// involve the target, which would otherwise recurse forever.
        /// </remarks>
        public bool ContainsComposite(CompositeHazard composite)
        {
            if (composite == null) throw new ArgumentNullException(nameof(composite));
            return ContainsComposite(composite, new HashSet<CompositeHazard>());
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
        /// (<see cref="RiskSerializationMode.ByReference"/> — the stored form, which never
        /// duplicates child function content).
        /// </summary>
        /// <param name="mode">The serialization mode; the mode propagates to nested composites.</param>
        /// <returns>The serialized form.</returns>
        public XElement ToXElement(RiskSerializationMode mode)
        {
            var element = new XElement(nameof(CompositeHazard));
            WriteIdentityAttributes(element);
            element.SetAttributeValue(nameof(SpecifiedHazard), SpecifiedHazard);
            element.SetAttributeValue(nameof(HazardUnit), HazardUnit);
            element.SetAttributeValue(nameof(CompositeCombinationType), _compositeCombinationType.ToString());
            element.SetAttributeValue(nameof(Dependency), _dependency.ToString());
            element.SetAttributeValue(nameof(CorrelationMatrix),
                CompositeSupport.IsMatrixMode(_compositeCombinationType, _dependency)
                    ? SerializationUtilities.FormatMatrix(_correlationMatrix)
                    : string.Empty);
            element.SetAttributeValue(nameof(HazardTransform), _hazardTransform.ToString());
            element.SetAttributeValue(nameof(ProbabilityTransform), _probabilityTransform.ToString());
            // Conditional presence: written only when bound, so every unbound composite's form —
            // and its canonical hash — is unchanged.
            if (_epistemicVariable.Length > 0)
            {
                element.SetAttributeValue(nameof(EpistemicVariable), _epistemicVariable);
            }

            var container = new XElement(nameof(HazardFunctions));
            for (int i = 0; i < _hazardFunctions.Count; i++)
            {
                var entry = _hazardFunctions[i];
                var entryElement = new XElement(nameof(WeightedHazardFunction));
                entryElement.SetAttributeValue(nameof(WeightedHazardFunction.Weight), SerializationUtilities.FormatDouble(entry.Weight));
                if (entry.HazardFunction != null)
                {
                    // Nested composites need no special case: under ByReference the nested
                    // composite itself becomes a marker (its children stay live in the store), and
                    // under SelfContained its parameterless ToXElement() recurses fully inline.
                    entryElement.Add(FunctionEntry.Write(entry.HazardFunction, mode));
                }
                container.Add(entryElement);
            }
            element.Add(container);
            return element;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Hashes the projected identity form, never the persisted form (the
        /// <c>SystemComponent</c> identity-form exception): the combination mode, the interpolation
        /// transforms, the dependence and correlation matrix, the shared epistemic variable when
        /// bound, the entry count, and per entry the
        /// effective weight and the child's own canonical hash. Three coercions keep inert edits
        /// from re-rolling seeds — weights project as one under CompetingRisks (where they do not
        /// participate), the dependence projects as Independent under the mixture modes (which
        /// ignore it),
        /// and the correlation matrix projects as empty outside the one mode that reads it.
        /// Consequences by construction: the serialization mode can never move the hash; child
        /// metadata edits are inert; entry order is semantic; and a null child projects an empty
        /// hash token, so an unresolved reference does not alias a resolved one.
        /// </remarks>
        public override byte[] CanonicalHash()
        {
            var identity = new XElement(nameof(CompositeHazard));
            identity.SetAttributeValue(nameof(CompositeCombinationType), _compositeCombinationType.ToString());
            identity.SetAttributeValue(nameof(Dependency), EffectiveDependency().ToString());
            identity.SetAttributeValue(nameof(CorrelationMatrix),
                CompositeSupport.IsMatrixMode(_compositeCombinationType, _dependency)
                    ? SerializationUtilities.FormatMatrix(_correlationMatrix)
                    : string.Empty);
            identity.SetAttributeValue(nameof(HazardTransform), _hazardTransform.ToString());
            identity.SetAttributeValue(nameof(ProbabilityTransform), _probabilityTransform.ToString());
            // Conditional presence mirrors the persisted form: binding a shared epistemic
            // variable is compute-relevant (it couples this selector to every other binder), so
            // it enters the identity only when named — an unbound composite's hash is unchanged.
            if (_epistemicVariable.Length > 0)
            {
                identity.SetAttributeValue(nameof(EpistemicVariable), _epistemicVariable);
            }
            identity.SetAttributeValue("Count", _hazardFunctions.Count.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < _hazardFunctions.Count; i++)
            {
                var entry = _hazardFunctions[i];
                var entryElement = new XElement(nameof(WeightedHazardFunction));
                entryElement.SetAttributeValue(nameof(WeightedHazardFunction.Weight), SerializationUtilities.FormatDouble(EffectiveWeight(i)));
                entryElement.SetAttributeValue("FunctionHash", entry.HazardFunction == null
                    ? string.Empty
                    : CanonicalContentHasher.ToTokenHex(entry.HazardFunction.CanonicalHash()));
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
            "The composite hazard function has no child functions. Call Validate() and correct the reported errors before sampling.";

        /// <summary>
        /// Builds the combined distribution over the sampled child distributions — the single place
        /// the combination rule lives, shared by all three sampling overloads.
        /// </summary>
        /// <param name="distributions">The child distributions, one per entry, in declared order.</param>
        /// <returns>The combined distribution, with its empirical CDF built.</returns>
        /// <remarks>
        /// <c>CreateEmpiricalCDF()</c> is v1.0 parity and load-bearing: it turns the combined
        /// <c>InverseCDF</c> into an interpolation rather than a Brent solve at every quadrature
        /// node, which the risk integrand calls thousands of times per realization.
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// Thrown when a child sampled to a distribution the Numerics combination kernels cannot
        /// accept.
        /// </exception>
        private IUnivariateDistribution BuildCombined(IUnivariateDistribution[] distributions)
        {
            CompositeSupport.ThrowIfNotNumericsDistributions(distributions, nameof(CompositeHazard));

            // EpistemicMixture reaches this path only from the mean overload, where the analytic
            // blend is the deterministic probe value; its per-percentile and per-realization
            // overloads select a branch before ever building a combination.
            if (_compositeCombinationType != CompositeCombinationType.CompetingRisks)
            {
                var weights = new double[distributions.Length];
                for (int i = 0; i < weights.Length; i++) weights[i] = EffectiveWeight(i);
                var mixture = new Mixture(weights, distributions)
                {
                    XTransform = _hazardTransform,
                    ProbabilityTransform = _probabilityTransform,
                };
                mixture.CreateEmpiricalCDF();
                return mixture;
            }

            var competing = new CompetingRisks(distributions)
            {
                // The maximum rule: the governing, most severe loading controls. (A response
                // composite uses the minimum — weakest link — rule instead.)
                MinimumOfRandomVariables = false,
                Dependency = CompositeSupport.MapDependency(_dependency),
                XTransform = _hazardTransform,
                ProbabilityTransform = _probabilityTransform,
            };
            if (CompositeSupport.IsMatrixMode(_compositeCombinationType, _dependency) && _correlationMatrix != null)
            {
                competing.CorrelationMatrix = _correlationMatrix;
            }
            competing.CreateEmpiricalCDF();
            return competing;
        }

        /// <summary>
        /// The weight the compute paths use for an entry: one under CompetingRisks (weights are
        /// inert there), the entry weight otherwise.
        /// </summary>
        /// <param name="index">The entry index.</param>
        /// <returns>The effective weight.</returns>
        private double EffectiveWeight(int index)
        {
            return _compositeCombinationType == CompositeCombinationType.CompetingRisks ? 1d : _hazardFunctions[index].Weight;
        }

        /// <summary>
        /// The dependence the hash projects: the configured value under CompetingRisks, and
        /// Independent under the mixture modes, which ignore dependence entirely.
        /// </summary>
        /// <returns>The effective dependence.</returns>
        private DependencyType EffectiveDependency()
        {
            return _compositeCombinationType == CompositeCombinationType.CompetingRisks ? _dependency : DependencyType.Independent;
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
            for (int i = 0; i < _hazardFunctions.Count; i++)
            {
                double weight = _hazardFunctions[i].Weight;
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
            for (int i = 0; i < _hazardFunctions.Count; i++)
            {
                if (_hazardFunctions[i].Weight > 0d) count++;
            }
            return count;
        }

        /// <summary>
        /// True when this composite, or any nested composite hazard beneath it, is in
        /// <see cref="CompositeCombinationType.EpistemicMixture"/> mode — the analysis-level
        /// mean-only gate's discovery surface.
        /// </summary>
        /// <returns>True when an epistemic mixture exists anywhere in the subtree.</returns>
        internal bool UsesEpistemicMode()
        {
            return UsesEpistemicMode(new HashSet<CompositeHazard>());
        }

        /// <summary>
        /// Accumulates every shared epistemic variable named by this composite or any nested
        /// composite hazard — the sharing scope's discovery surface.
        /// </summary>
        /// <param name="sink">The accumulating distinct variable names.</param>
        /// <exception cref="ArgumentNullException">Thrown when the sink is null.</exception>
        internal void CollectEpistemicVariables(ISet<string> sink)
        {
            if (sink == null) throw new ArgumentNullException(nameof(sink));
            CollectEpistemicVariables(sink, new HashSet<CompositeHazard>());
        }

        /// <summary>
        /// The cycle-safe recursion behind <see cref="UsesEpistemicMode()"/>.
        /// </summary>
        /// <param name="visited">The composites already searched.</param>
        /// <returns>True when an epistemic mixture exists anywhere in the subtree.</returns>
        private bool UsesEpistemicMode(HashSet<CompositeHazard> visited)
        {
            if (!visited.Add(this)) return false;
            if (_compositeCombinationType == CompositeCombinationType.EpistemicMixture) return true;
            for (int i = 0; i < _hazardFunctions.Count; i++)
            {
                if (_hazardFunctions[i].HazardFunction is CompositeHazard nested && nested.UsesEpistemicMode(visited))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The cycle-safe recursion behind <see cref="CollectEpistemicVariables(ISet{string})"/>.
        /// </summary>
        /// <param name="sink">The accumulating distinct variable names.</param>
        /// <param name="visited">The composites already searched.</param>
        private void CollectEpistemicVariables(ISet<string> sink, HashSet<CompositeHazard> visited)
        {
            if (!visited.Add(this)) return;
            if (_compositeCombinationType == CompositeCombinationType.EpistemicMixture && _epistemicVariable.Length > 0)
                sink.Add(_epistemicVariable);
            for (int i = 0; i < _hazardFunctions.Count; i++)
            {
                if (_hazardFunctions[i].HazardFunction is CompositeHazard nested)
                    nested.CollectEpistemicVariables(sink, visited);
            }
        }

        /// <summary>
        /// Accumulates the logic-tree axes declared by this composite or any nested composite
        /// hazard — the exact enumerator's cycle-safe discovery surface. A bound composite merges
        /// into its variable's axis (weight disagreement recorded), an unbound epistemic composite
        /// is its own axis, and every epistemic composite's id feeds the pin-conflict gate.
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
            if (_compositeCombinationType == CompositeCombinationType.EpistemicMixture)
            {
                var weights = new double[_hazardFunctions.Count];
                for (int i = 0; i < weights.Length; i++) weights[i] = _hazardFunctions[i].Weight;
                LogicTreeAxisSeed.Register(_epistemicVariable, Id, Name, weights,
                    boundAxes, unboundAxes, mismatchedVariables, epistemicFunctionIds);
            }
            for (int i = 0; i < _hazardFunctions.Count; i++)
            {
                if (_hazardFunctions[i].HazardFunction is CompositeHazard nested)
                    nested.CollectLogicTreeAxes(boundAxes, unboundAxes, mismatchedVariables, epistemicFunctionIds, visited);
            }
        }

        /// <summary>
        /// The sample-time usability gate (the cluster's invalid-configuration throw): at least one
        /// entry, every entry configured and univariate (a bivariate child has no univariate
        /// collapse), Mixture or EpistemicMixture weights in [0, 1] summing to one, a variable
        /// bound only in epistemic mode, and a usable
        /// correlation matrix where the dependence requires one. Cycle detection is opt-in because
        /// the per-realization path runs this on every draw and a cycle cannot survive
        /// <see cref="SetupSampler"/>.
        /// </summary>
        /// <param name="checkCycles">True to also reject circular references.</param>
        /// <exception cref="InvalidOperationException">Thrown when the configuration is invalid.</exception>
        private void ThrowIfUnusable(bool checkCycles)
        {
            bool usable = _hazardFunctions.Count > 0;
            if (usable && _epistemicVariable.Length > 0 && _compositeCombinationType != CompositeCombinationType.EpistemicMixture)
                usable = false;
            if (usable)
            {
                double weightSum = 0d;
                bool weightsApply = _compositeCombinationType != CompositeCombinationType.CompetingRisks;
                for (int i = 0; i < _hazardFunctions.Count; i++)
                {
                    var entry = _hazardFunctions[i];
                    if (entry.HazardFunction == null || entry.HazardFunction is IBivariateHazardFunction ||
                        (weightsApply && (entry.Weight < 0d || entry.Weight > 1d)))
                    {
                        usable = false;
                        break;
                    }
                    weightSum += entry.Weight;
                }
                if (usable && weightsApply && Math.Abs(weightSum - 1d) > WeightSumTolerance) usable = false;
            }
            if (usable && !IsCorrelationMatrixValid()) usable = false;
            if (usable && checkCycles && FindCircularChild() != null) usable = false;

            if (!usable)
                throw new InvalidOperationException("The composite hazard configuration is invalid. Call Validate() and correct the reported errors before sampling.");
        }

        /// <summary>
        /// Finds the first child that closes a cycle back to this composite, or null when the child
        /// graph is acyclic.
        /// </summary>
        /// <returns>The offending child function, or null.</returns>
        private IHazardFunction? FindCircularChild()
        {
            for (int i = 0; i < _hazardFunctions.Count; i++)
            {
                var function = _hazardFunctions[i].HazardFunction;
                if (function is not CompositeHazard nested) continue;
                if (ReferenceEquals(nested, this) || nested.ContainsComposite(this)) return function;
            }
            return null;
        }

        /// <summary>
        /// The cycle-safe recursion behind <see cref="ContainsComposite(CompositeHazard)"/>.
        /// </summary>
        /// <param name="target">The composite being searched for.</param>
        /// <param name="visited">The composites already searched (guards cycles not involving the target).</param>
        /// <returns>True when the target is referenced at any nesting depth.</returns>
        private bool ContainsComposite(CompositeHazard target, HashSet<CompositeHazard> visited)
        {
            if (!visited.Add(this)) return false;
            for (int i = 0; i < _hazardFunctions.Count; i++)
            {
                var function = _hazardFunctions[i].HazardFunction;
                if (ReferenceEquals(function, target)) return true;
                if (function is CompositeHazard nested && nested.ContainsComposite(target, visited)) return true;
            }
            return false;
        }

        /// <summary>
        /// Accumulates the summary probability grid from the children: parametric ordinates,
        /// tabular table probabilities, nested composites recursively (cycle-safe), and the default
        /// ordinate set for any other child type.
        /// </summary>
        /// <param name="probabilities">The accumulating sorted distinct grid.</param>
        /// <param name="visited">The composites already visited.</param>
        private void CollectSummaryProbabilities(SortedSet<double> probabilities, HashSet<CompositeHazard> visited)
        {
            if (!visited.Add(this)) return;
            for (int i = 0; i < _hazardFunctions.Count; i++)
            {
                switch (_hazardFunctions[i].HazardFunction)
                {
                    case ParametricUnivariateHazard parametric:
                        for (int j = 0; j < parametric.ProbabilityOrdinates.Count; j++)
                            probabilities.Add(1d - parametric.ProbabilityOrdinates[j]);
                        break;
                    case CompositeHazard nested:
                        nested.CollectSummaryProbabilities(probabilities, visited);
                        break;
                    case null:
                        break;
                    default:
                        // Any other child kind (tabular, nonparametric) contributes the shared
                        // default ordinate set: its own grid is a hazard axis, not a probability
                        // axis, so it cannot be unioned in directly.
                        foreach (double p in CompositeSupport.DefaultSummaryProbabilities)
                            probabilities.Add(p);
                        break;
                }
            }
        }

        /// <summary>
        /// Keeps the composite's change subscriptions in step with the entry list, and reports the
        /// membership change as <c>HazardFunctions</c>.
        /// </summary>
        /// <param name="sender">The entry collection.</param>
        /// <param name="e">The membership change.</param>
        private void HazardFunctionsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            ReconcileEntrySubscriptions();
            RaisePropertyChange(nameof(HazardFunctions));
        }

        /// <summary>
        /// Subscribes to every entry now in the list and unsubscribes from every entry that has
        /// left it, using <see cref="_subscribedEntries"/> as the record of what is attached.
        /// </summary>
        private void ReconcileEntrySubscriptions()
        {
            foreach (var stale in _subscribedEntries.Where(entry => !_hazardFunctions.Contains(entry)).ToList())
            {
                stale.PropertyChanged -= EntryPropertyChanged;
                _subscribedEntries.Remove(stale);
            }

            for (int i = 0; i < _hazardFunctions.Count; i++)
            {
                var entry = _hazardFunctions[i];
                if (entry != null && _subscribedEntries.Add(entry)) entry.PropertyChanged += EntryPropertyChanged;
            }
        }

        /// <summary>
        /// Re-raises an entry-level change (a weight edit, a function swap, or a forwarded child
        /// content edit) as a change of the composite's entry list. Reentrant notifications are
        /// suppressed via <see cref="_raisingEntryChange"/> so a cyclic composite graph degrades to
        /// a reportable validation error instead of unbounded recursion.
        /// </summary>
        /// <param name="sender">The entry.</param>
        /// <param name="e">The originating change arguments.</param>
        private void EntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_raisingEntryChange) return;
            _raisingEntryChange = true;
            try
            {
                RaisePropertyChange(nameof(HazardFunctions));
            }
            finally
            {
                _raisingEntryChange = false;
            }
        }

        #endregion
    }
}
