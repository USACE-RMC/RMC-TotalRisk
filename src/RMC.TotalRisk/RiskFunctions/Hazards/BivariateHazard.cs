using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Numerics.Distributions;
using Numerics.Distributions.Copulas;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.Results.Risk;
using RMC.TotalRisk.RiskFunctions.Support;

namespace RMC.TotalRisk.RiskFunctions.Hazards
{
    /// <summary>
    /// A bivariate hazard function: a primary (X) and a secondary (Y) marginal hazard function —
    /// LINKS to univariate hazard functions stored by the consuming layer — coupled by a Numerics
    /// copula (default <see cref="IndependenceCopula"/>), with the secondary dimension integrated
    /// per primary slice over <see cref="SecondaryIntegrationBins"/> trapezoid bins in
    /// conditional-probability space. Canonical example: primary = seismic peak ground acceleration
    /// frequency, secondary = reservoir pool-duration curve, independence copula.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The engine integrates the PRIMARY axis exactly as it does a univariate hazard — the
    /// <see cref="SampleFunction()"/> overloads return the sampled X marginal — and evaluates the
    /// conditional Y discretization inside its per-hazard-level objective through the
    /// <see cref="SampleBivariate(int)"/> overloads. The copula operates on (u, v) as NON-exceedance marginal
    /// probabilities (an exceedance probability p converts as u = 1 − p), so upper-tail dependence
    /// in (u, v) is joint extreme-hazard dependence. Copula parameters are fixed user-set values —
    /// dependence-parameter uncertainty is deliberately out of scope, while marginal knowledge
    /// uncertainty propagates through each marginal's own sampler per realization.
    /// </para>
    /// <para>
    /// <b>Marginals are referenced, not owned</b> — the consuming layer stores each univariate
    /// hazard function once and this function links it, exactly like a composite's children. Both
    /// marginals must be univariate hazard functions (<see cref="IUnivariateHazardFunction"/>);
    /// the properties are typed <see cref="IHazardFunction"/> so a nested (vine) dependence
    /// structure could be admitted without a serialization break, and <see cref="Validate"/>
    /// rejects a bivariate marginal. The two marginals must be distinct instances: one stored
    /// function is one knowledge quantity, and coupling a function to itself is a modeling error
    /// (two equal-content instances are legal and draw independently).
    /// </para>
    /// <para>
    /// <b>Seeding</b> follows the composite forward rule: this function occupies ONE component
    /// sampler ordinal and consumes no knowledge draw of its own
    /// (<see cref="SamplingDimensions"/> = 0); <see cref="SetupSampler"/> recurses into marginal X
    /// at ordinal 0 and marginal Y at ordinal 1 with
    /// <c>SeedHelpers.HashCombine(seed, marginal.CanonicalHash(), ordinal)</c>, so identical-content
    /// marginals draw independently and renaming can never change results. Ordinal 2 is reserved
    /// for a copula-parameter posterior, so the X/Y streams never move.
    /// </para>
    /// <para>
    /// <b>Serialization:</b> under <see cref="RiskSerializationMode.SelfContained"/> (the default)
    /// marginal content is written inline; under <see cref="RiskSerializationMode.ByReference"/>
    /// each marginal container holds a <c>FunctionReference</c> marker re-attached to the live
    /// stored instance by an <see cref="IRiskFunctionResolver"/>. The copula serializes through its
    /// own Numerics element; a missing copula element reads as independence. <b>Hashing</b> uses a
    /// projected identity form — the bin count, the copula type and parameters, and the marginals'
    /// own canonical hashes as named X/Y attributes — never the persisted form, so the
    /// serialization mode, ids, names, and axis labels can never move this function's hash, while
    /// swapping the marginals' roles does (the <c>SystemComponent</c> identity-form exception).
    /// </para>
    /// <para>
    /// <b>Discretization</b> (the conditional-trapezoid rule): N bins produce N + 1 nodes
    /// t_j = j/N uniform in conditional-probability space, endpoint nodes clamped to
    /// [1e-16, 1 − 1e-16] for inverse evaluations; y_j =
    /// MarginalY.InverseCDF(copula.InverseConditionalCDF(u, t_j)); trapezoid weights
    /// w_0 = w_N = 1/(2N) and 1/N between, the last computed as the exact residual so the ordered
    /// weight sum is exactly one. The node and weight vectors are realization-independent,
    /// precomputed once per <see cref="SetupSampler"/> and shared read-only across the
    /// per-realization snapshots.
    /// </para>
    /// </remarks>
    public class BivariateHazard : HazardFunctionBase, IBivariateHazardFunction
    {
        #region Construction

        /// <summary>
        /// Initializes an empty bivariate hazard with the defaults: independence copula, 20
        /// secondary integration bins, no marginal links.
        /// </summary>
        public BivariateHazard()
        {
        }

        /// <summary>
        /// Initializes a bivariate hazard over the specified marginal links.
        /// </summary>
        /// <param name="marginalX">The primary (X) marginal hazard function; may be null while unconfigured.</param>
        /// <param name="marginalY">The secondary (Y) marginal hazard function; may be null while unconfigured.</param>
        /// <param name="copula">The copula coupling the marginals; null selects the independence copula.</param>
        public BivariateHazard(IHazardFunction? marginalX, IHazardFunction? marginalY, BivariateCopula? copula = null)
        {
            MarginalX = marginalX;
            MarginalY = marginalY;
            if (copula != null) Copula = copula;
        }

        /// <summary>
        /// Restores a bivariate hazard function from its serialized form.
        /// </summary>
        /// <param name="xElement">The serialized form produced by <see cref="ToXElement(RiskSerializationMode)"/>.</param>
        /// <param name="resolver">
        /// The function resolver, required only to read a by-reference form. An unresolvable
        /// name-only reference keeps its marginal slot null and is reported by
        /// <see cref="Validate"/>; a stale id throws.
        /// </param>
        /// <exception cref="ArgumentNullException">Thrown when the element is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when inline marginal content cannot be reconstructed (dropping it would lose
        /// model content on the next save), or when a serialized reference id is stale.
        /// </exception>
        public BivariateHazard(XElement xElement, IRiskFunctionResolver? resolver = null)
        {
            if (xElement == null) throw new ArgumentNullException(nameof(xElement));

            ReadIdentityAttributes(xElement);
            SpecifiedHazard = SerializationUtilities.ReadString(xElement, nameof(SpecifiedHazard));
            HazardUnit = SerializationUtilities.ReadString(xElement, nameof(HazardUnit));
            _secondarySpecifiedHazard = SerializationUtilities.ReadString(xElement, nameof(SecondarySpecifiedHazard));
            _secondaryHazardUnit = SerializationUtilities.ReadString(xElement, nameof(SecondaryHazardUnit));
            _secondaryIntegrationBins = SerializationUtilities.ReadInt32(xElement, nameof(SecondaryIntegrationBins), 20);

            // The copula rides its own Numerics element; a missing element reads as the
            // independence default so a minimally-authored form is a valid independent coupling.
            var copulaElement = xElement.Element(CopulaElementName);
            _copula = copulaElement != null ? CopulaFactory.CreateCopula(copulaElement) : new IndependenceCopula();

            MarginalX = ReadMarginal(xElement, nameof(MarginalX), resolver, "marginal X hazard function");
            MarginalY = ReadMarginal(xElement, nameof(MarginalY), resolver, "marginal Y hazard function");
        }

        #endregion

        #region Members

        /// <summary>
        /// The smallest legal <see cref="SecondaryIntegrationBins"/> value — fewer than three bins
        /// is unacceptably coarse coverage of the conditional integral.
        /// </summary>
        public const int MinimumSecondaryIntegrationBins = 3;

        /// <summary>
        /// The largest legal <see cref="SecondaryIntegrationBins"/> value.
        /// </summary>
        public const int MaximumSecondaryIntegrationBins = 1000;

        /// <summary>
        /// The probability floor clamping the endpoint conditional nodes (and the diagnostic
        /// primary-slice conversion): inverse evaluations run on
        /// [<c>ProbabilityFloor</c>, 1 − <c>ProbabilityFloor</c>], keeping conditional hazard
        /// values finite for unbounded marginals.
        /// </summary>
        private const double ProbabilityFloor = 1e-16;

        /// <summary>
        /// The element name the Numerics copula serialization contract writes —
        /// <c>BivariateCopula.ToXElement()</c> names its element "Copula" for every family.
        /// </summary>
        private const string CopulaElementName = "Copula";

        /// <summary>
        /// Backing field for <see cref="MarginalX"/>.
        /// </summary>
        private IHazardFunction? _marginalX;

        /// <summary>
        /// Backing field for <see cref="MarginalY"/>.
        /// </summary>
        private IHazardFunction? _marginalY;

        /// <summary>
        /// Backing field for <see cref="Copula"/> — never null; the default is independence.
        /// </summary>
        private BivariateCopula _copula = new IndependenceCopula();

        /// <summary>
        /// Backing field for <see cref="SecondarySpecifiedHazard"/>.
        /// </summary>
        private string _secondarySpecifiedHazard = string.Empty;

        /// <summary>
        /// Backing field for <see cref="SecondaryHazardUnit"/>.
        /// </summary>
        private string _secondaryHazardUnit = string.Empty;

        /// <summary>
        /// Backing field for <see cref="SecondaryIntegrationBins"/>.
        /// </summary>
        private int _secondaryIntegrationBins = 20;

        /// <summary>
        /// The precomputed conditional-probability nodes (endpoint-clamped), length bins + 1; null
        /// until <see cref="SetupSampler"/> runs, and cleared when the bin count changes.
        /// </summary>
        private double[]? _conditionalNodes;

        /// <summary>
        /// The precomputed trapezoid weights, index-aligned with the nodes and summing exactly to
        /// one; null until <see cref="SetupSampler"/> runs, and cleared when the bin count changes.
        /// </summary>
        private double[]? _conditionalWeights;

        /// <summary>
        /// Descriptions of serialized function references that could not be resolved, reported by
        /// <see cref="Validate"/> so the precise cause is visible instead of a generic message.
        /// </summary>
        private readonly List<string> _unresolvedFunctionReferences = new List<string>();

        /// <summary>
        /// True while <see cref="MarginalPropertyChanged"/> is re-raising, breaking the
        /// notification feedback loop an (illegal) cyclic marginal graph would otherwise create —
        /// the wiring must stay crash-free so <see cref="Validate"/> can report the cycle's cause.
        /// </summary>
        private bool _raisingMarginalChange;

        /// <inheritdoc/>
        /// <remarks>
        /// Referenced, not owned. The setter swaps a change subscription so edits to the linked
        /// function re-raise as <c>MarginalX</c> (and edits to a swapped-out function raise
        /// nothing).
        /// </remarks>
        public IHazardFunction? MarginalX
        {
            get { return _marginalX; }
            set
            {
                if (ReferenceEquals(_marginalX, value)) return;

                if (_marginalX != null) _marginalX.PropertyChanged -= MarginalPropertyChanged;
                _marginalX = value;
                if (_marginalX != null) _marginalX.PropertyChanged += MarginalPropertyChanged;

                RaisePropertyChange(nameof(MarginalX));
            }
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Referenced, not owned. The setter swaps a change subscription so edits to the linked
        /// function re-raise as <c>MarginalY</c> (and edits to a swapped-out function raise
        /// nothing).
        /// </remarks>
        public IHazardFunction? MarginalY
        {
            get { return _marginalY; }
            set
            {
                if (ReferenceEquals(_marginalY, value)) return;

                if (_marginalY != null) _marginalY.PropertyChanged -= MarginalPropertyChanged;
                _marginalY = value;
                if (_marginalY != null) _marginalY.PropertyChanged += MarginalPropertyChanged;

                RaisePropertyChange(nameof(MarginalY));
            }
        }

        /// <summary>
        /// The copula coupling the marginals, with fixed user-set parameters. Never null:
        /// assigning null coerces to a fresh <see cref="IndependenceCopula"/>. Compute-relevant —
        /// the copula type and parameters are part of the hashed identity form.
        /// </summary>
        public BivariateCopula Copula
        {
            get { return _copula; }
            set
            {
                var coerced = value ?? new IndependenceCopula();
                if (!ReferenceEquals(_copula, coerced))
                {
                    _copula = coerced;
                    RaisePropertyChange(nameof(Copula));
                }
            }
        }

        /// <summary>
        /// The copula's dependence parameter θ — a passthrough onto
        /// <see cref="BivariateCopula.Theta"/> with change notification. Meaningless (and inert in
        /// the hash, which folds <see cref="BivariateCopula.GetCopulaParameters"/>) on the
        /// zero-parameter independence copula.
        /// </summary>
        public double CopulaTheta
        {
            get { return _copula.Theta; }
            set
            {
                if (_copula.Theta != value)
                {
                    _copula.Theta = value;
                    RaisePropertyChange(nameof(CopulaTheta));
                }
            }
        }

        /// <inheritdoc/>
        public string SecondarySpecifiedHazard
        {
            get { return _secondarySpecifiedHazard; }
            set
            {
                if (_secondarySpecifiedHazard != value)
                {
                    _secondarySpecifiedHazard = value;
                    RaisePropertyChange(nameof(SecondarySpecifiedHazard));
                }
            }
        }

        /// <inheritdoc/>
        public string SecondaryHazardUnit
        {
            get { return _secondaryHazardUnit; }
            set
            {
                if (_secondaryHazardUnit != value)
                {
                    _secondaryHazardUnit = value;
                    RaisePropertyChange(nameof(SecondaryHazardUnit));
                }
            }
        }

        /// <inheritdoc/>
        /// <remarks>
        /// No silent clamp — <see cref="Validate"/> reports a value outside
        /// [<see cref="MinimumSecondaryIntegrationBins"/>, <see cref="MaximumSecondaryIntegrationBins"/>]
        /// as an error. Changing the count clears the precomputed discretization vectors, so
        /// the <see cref="SampleBivariate(int)"/> overloads require a fresh <see cref="SetupSampler"/>
        /// afterwards — the snapshot geometry can never disagree with the configured count.
        /// </remarks>
        public int SecondaryIntegrationBins
        {
            get { return _secondaryIntegrationBins; }
            set
            {
                if (_secondaryIntegrationBins != value)
                {
                    _secondaryIntegrationBins = value;
                    _conditionalNodes = null;
                    _conditionalWeights = null;
                    RaisePropertyChange(nameof(SecondaryIntegrationBins));
                }
            }
        }

        /// <inheritdoc/>
        public int ConditionalNodeCount => _secondaryIntegrationBins + 1;

        /// <inheritdoc/>
        public override HazardFunctionType FunctionType => HazardFunctionType.Bivariate;

        /// <inheritdoc/>
        /// <remarks>
        /// Deterministic when every linked marginal is — the copula parameters are fixed values and
        /// contribute no uncertainty of their own.
        /// </remarks>
        public override bool IsDeterministic
        {
            get
            {
                if (_marginalX != null && !_marginalX.IsDeterministic) return false;
                if (_marginalY != null && !_marginalY.IsDeterministic) return false;
                return true;
            }
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Always zero: the coupling consumes no knowledge draw of its own. The marginals own
        /// their dimensions and are set up recursively by <see cref="SetupSampler"/>
        /// (docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §5.8.5).
        /// </remarks>
        public override int SamplingDimensions => 0;

        #endregion

        #region IRiskFunction Methods

        /// <inheritdoc/>
        /// <remarks>
        /// Recurses into both marginals with content-derived seeds:
        /// <c>SeedHelpers.HashCombine(seed, marginal.CanonicalHash(), ordinal)</c> at ordinal 0 for
        /// X and 1 for Y — identical-content marginals draw independently, and renaming a marginal
        /// can never change results. Also precomputes the realization-independent conditional node
        /// and weight vectors the per-realization snapshots share.
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the bivariate configuration is invalid, or when a posterior-indexed marginal
        /// cannot serve the requested sample size.
        /// </exception>
        public override void SetupSampler(int sampleSize, int seed, SamplingScheme scheme)
        {
            ThrowIfUnusable();
            base.SetupSampler(sampleSize, seed, scheme);

            CompositeSupport.ThrowIfPosteriorCapacityTooSmall(_marginalX!, sampleSize, Name);
            _marginalX!.SetupSampler(sampleSize, SeedHelpers.HashCombine(seed, _marginalX.CanonicalHash(), 0), scheme);
            CompositeSupport.ThrowIfPosteriorCapacityTooSmall(_marginalY!, sampleSize, Name);
            _marginalY!.SetupSampler(sampleSize, SeedHelpers.HashCombine(seed, _marginalY.CanonicalHash(), 1), scheme);

            BuildConditionalVectors();
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Errors (invalidating): missing primary or secondary axis labels; an unresolved
        /// serialized marginal reference (reported precisely); an undefined marginal; a marginal
        /// that is not a univariate hazard function (nested bivariate dependence structures are
        /// deliberately unsupported); the two marginals referencing the same function instance
        /// (one stored function is one knowledge quantity); an invalid marginal (summary line only
        /// — the marginal reports its own details where it is stored); invalid copula parameters
        /// (the copula's own diagnostic is surfaced); a secondary bin count outside
        /// [3, 1000]. Warnings (advisory): marginal axis labels that do not match the declared
        /// primary or secondary labels — labels are unhashed metadata and never gate compute.
        /// </remarks>
        public override (bool IsValid, List<string> ValidationMessages) Validate()
        {
            var messages = new List<string>();

            if (string.IsNullOrEmpty(SpecifiedHazard))
                messages.Add("Error: The bivariate hazard function does not have a specified primary hazard type.");
            if (string.IsNullOrEmpty(HazardUnit))
                messages.Add("Error: The bivariate hazard function does not have a specified primary hazard unit.");
            if (string.IsNullOrEmpty(_secondarySpecifiedHazard))
                messages.Add("Error: The bivariate hazard function does not have a specified secondary hazard type.");
            if (string.IsNullOrEmpty(_secondaryHazardUnit))
                messages.Add("Error: The bivariate hazard function does not have a specified secondary hazard unit.");

            foreach (string reference in _unresolvedFunctionReferences)
            {
                messages.Add($"Error: The bivariate hazard function '{Name}' references {reference}, which was not found.");
            }

            ValidateMarginal(_marginalX, "X", "primary", SpecifiedHazard, HazardUnit, messages);
            ValidateMarginal(_marginalY, "Y", "secondary", _secondarySpecifiedHazard, _secondaryHazardUnit, messages);

            if (_marginalX != null && ReferenceEquals(_marginalX, _marginalY))
                messages.Add("Error: The marginal X and marginal Y hazard functions reference the same function instance — one stored function is one knowledge quantity. Reference two distinct hazard functions.");

            if (!_copula.ParametersValid)
            {
                string? detail = _copula.ValidateParameter(_copula.Theta, false)?.Message;
                messages.Add(string.IsNullOrEmpty(detail)
                    ? "Error: The copula parameters are invalid."
                    : $"Error: The copula parameters are invalid. {detail}");
            }

            if (_secondaryIntegrationBins < MinimumSecondaryIntegrationBins || _secondaryIntegrationBins > MaximumSecondaryIntegrationBins)
                messages.Add("Error: The number of secondary integration bins must be between 3 and 1000.");

            return (messages.FindIndex(m => m.StartsWith("Error:", StringComparison.Ordinal)) < 0, messages);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The mean primary marginal — the sampled hazard IS the X marginal; the engine integrates
        /// the primary axis and asks for the conditional secondary discretization separately.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown when the bivariate configuration is invalid.</exception>
        public override IUnivariateDistribution SampleFunction()
        {
            ThrowIfUnusable();
            return _marginalX!.SampleFunction();
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The primary marginal sampled co-monotonically at the given knowledge percentile.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown when the bivariate configuration is invalid.</exception>
        public override IUnivariateDistribution SampleFunction(double percentile)
        {
            ThrowIfUnusable();
            return _marginalX!.SampleFunction(percentile);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The primary marginal's per-realization sample from its own content-seeded stream.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown when the bivariate configuration is invalid.</exception>
        public override IUnivariateDistribution SampleFunction(int realizationIndex)
        {
            ThrowIfUnusable();
            return _marginalX!.SampleFunction(realizationIndex);
        }

        /// <inheritdoc/>
        /// <remarks>The primary (X) marginal's minimum.</remarks>
        /// <exception cref="InvalidOperationException">Thrown when the bivariate configuration is invalid.</exception>
        public override double MinHazard(bool meanOnly)
        {
            ThrowIfUnusable();
            return _marginalX!.MinHazard(meanOnly);
        }

        /// <inheritdoc/>
        /// <remarks>The primary (X) marginal's maximum.</remarks>
        /// <exception cref="InvalidOperationException">Thrown when the bivariate configuration is invalid.</exception>
        public override double MaxHazard(bool meanOnly)
        {
            ThrowIfUnusable();
            return _marginalX!.MaxHazard(meanOnly);
        }

        /// <inheritdoc/>
        /// <remarks>The mean secondary (Y) marginal.</remarks>
        /// <exception cref="InvalidOperationException">Thrown when the bivariate configuration is invalid.</exception>
        public IUnivariateDistribution SampleSecondaryFunction()
        {
            ThrowIfUnusable();
            return _marginalY!.SampleFunction();
        }

        /// <inheritdoc/>
        /// <remarks>The secondary marginal sampled co-monotonically at the given knowledge percentile.</remarks>
        /// <exception cref="InvalidOperationException">Thrown when the bivariate configuration is invalid.</exception>
        public IUnivariateDistribution SampleSecondaryFunction(double percentile)
        {
            ThrowIfUnusable();
            return _marginalY!.SampleFunction(percentile);
        }

        /// <inheritdoc/>
        /// <remarks>The secondary marginal's per-realization sample from its own content-seeded stream.</remarks>
        /// <exception cref="InvalidOperationException">Thrown when the bivariate configuration is invalid.</exception>
        public IUnivariateDistribution SampleSecondaryFunction(int realizationIndex)
        {
            ThrowIfUnusable();
            return _marginalY!.SampleFunction(realizationIndex);
        }

        /// <inheritdoc/>
        /// <remarks>The secondary (Y) marginal's minimum.</remarks>
        /// <exception cref="InvalidOperationException">Thrown when the bivariate configuration is invalid.</exception>
        public double MinSecondaryHazard(bool meanOnly)
        {
            ThrowIfUnusable();
            return _marginalY!.MinHazard(meanOnly);
        }

        /// <inheritdoc/>
        /// <remarks>The secondary (Y) marginal's maximum.</remarks>
        /// <exception cref="InvalidOperationException">Thrown when the bivariate configuration is invalid.</exception>
        public double MaxSecondaryHazard(bool meanOnly)
        {
            ThrowIfUnusable();
            return _marginalY!.MaxHazard(meanOnly);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The mean snapshot holds the Y marginal's mean frequency curve (the same
        /// <see cref="SampleSecondaryFunction()"/> surface), an independently cloned copula, and
        /// the precomputed node/weight vectors shared read-only across snapshots — the shape the
        /// engine's mean pass and its deterministic probes consume.
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the bivariate configuration is invalid, or when <see cref="SetupSampler"/>
        /// has not been called since the last bin-count change.
        /// </exception>
        public SampledBivariateHazard SampleBivariate()
        {
            ThrowIfUnusable();
            if (_conditionalNodes == null || _conditionalWeights == null)
                throw new InvalidOperationException("SetupSampler() must be called before sampling the bivariate snapshot.");

            return new SampledBivariateHazard(_marginalY!.SampleFunction(), _copula.Clone(),
                _conditionalNodes, _conditionalWeights);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The snapshot holds the realization's sampled Y marginal, an independently cloned copula
        /// (thread isolation — the sampled marginal and the copula's own marginal caches are not
        /// safe to share across threads), and the precomputed node/weight vectors shared read-only
        /// across snapshots.
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the bivariate configuration is invalid, or when <see cref="SetupSampler"/>
        /// has not been called since the last bin-count change.
        /// </exception>
        public SampledBivariateHazard SampleBivariate(int realizationIndex)
        {
            ThrowIfUnusable();
            if (_conditionalNodes == null || _conditionalWeights == null)
                throw new InvalidOperationException("SetupSampler() must be called before sampling by realization index.");

            return new SampledBivariateHazard(_marginalY!.SampleFunction(realizationIndex), _copula.Clone(),
                _conditionalNodes, _conditionalWeights);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Allocates per call — diagnostics, oracles, and consuming-layer previews only. The
        /// primary hazard level converts to its non-exceedance probability through the
        /// realization's sampled X marginal, clamped to [1e-16, 1 − 1e-16].
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the bivariate configuration is invalid, or when <see cref="SetupSampler"/>
        /// has not been called since the last bin-count change.
        /// </exception>
        public IReadOnlyList<(double Y, double Weight)> SampleConditionalYGivenX(int realizationIndex, double xHazardLevel)
        {
            var snapshot = SampleBivariate(realizationIndex);
            double u = Math.Clamp(_marginalX!.SampleFunction(realizationIndex).CDF(xHazardLevel),
                ProbabilityFloor, 1d - ProbabilityFloor);

            var yNodes = new double[snapshot.ConditionalNodeCount];
            var weights = new double[snapshot.ConditionalNodeCount];
            snapshot.FillConditionalBins(u, yNodes, weights);

            var result = new (double Y, double Weight)[yNodes.Length];
            for (int j = 0; j < result.Length; j++)
            {
                result[j] = (yNodes[j], weights[j]);
            }
            return result;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The primary marginal's knowledge-uncertainty summary — the primary axis is what the
        /// engine integrates and what a hazard summary plot shows; the secondary marginal's summary
        /// remains available on the linked function itself. Returns null when <see cref="Validate"/>
        /// reports errors.
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the width is outside (0, 1).</exception>
        public override UncertaintyAnalysisResults? ComputeUncertaintyResults(double confidenceIntervalWidth = 0.9)
        {
            if (confidenceIntervalWidth <= 0d || confidenceIntervalWidth >= 1d)
                throw new ArgumentOutOfRangeException(nameof(confidenceIntervalWidth), "The confidence interval width must be between 0 and 1.");
            if (!Validate().IsValid) return null;

            return _marginalX!.ComputeUncertaintyResults(confidenceIntervalWidth);
        }

        #endregion

        #region Serialization

        /// <inheritdoc/>
        /// <remarks>
        /// The self-contained form (marginal content inline) — the storeless default. Not this
        /// type's hash surface: see <see cref="CanonicalHash"/>.
        /// </remarks>
        public override XElement ToXElement()
        {
            return ToXElement(RiskSerializationMode.SelfContained);
        }

        /// <summary>
        /// Serializes the bivariate hazard in the requested mode: marginal content inline
        /// (<see cref="RiskSerializationMode.SelfContained"/>), or marginal containers holding
        /// <c>FunctionReference</c> markers
        /// (<see cref="RiskSerializationMode.ByReference"/> — the stored form, which never
        /// duplicates linked function content).
        /// </summary>
        /// <param name="mode">The serialization mode; the mode propagates into inline marginal content.</param>
        /// <returns>The serialized form. Child order — copula, marginal X, marginal Y — is append-only contract.</returns>
        public XElement ToXElement(RiskSerializationMode mode)
        {
            var element = new XElement(nameof(BivariateHazard));
            WriteIdentityAttributes(element);
            element.SetAttributeValue(nameof(SpecifiedHazard), SpecifiedHazard);
            element.SetAttributeValue(nameof(HazardUnit), HazardUnit);
            element.SetAttributeValue(nameof(SecondarySpecifiedHazard), _secondarySpecifiedHazard);
            element.SetAttributeValue(nameof(SecondaryHazardUnit), _secondaryHazardUnit);
            element.SetAttributeValue(nameof(SecondaryIntegrationBins), _secondaryIntegrationBins.ToString(CultureInfo.InvariantCulture));

            element.Add(_copula.ToXElement());
            element.Add(WriteMarginal(nameof(MarginalX), _marginalX, mode));
            element.Add(WriteMarginal(nameof(MarginalY), _marginalY, mode));
            return element;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Hashes a projected identity form, never the persisted form (the <c>SystemComponent</c>
        /// identity-form exception): the secondary bin count, the copula type and parameters, and
        /// the marginals' own canonical hashes as named X and Y attributes. Consequences by
        /// construction: the serialization mode can never move the hash; marginal metadata edits
        /// (ids, names, labels) are inert; the axis labels are excluded; swapping the marginals'
        /// roles moves the hash (the roles are asymmetric); and a null marginal projects an empty
        /// hash token, so an unresolved reference does not alias a resolved one.
        /// </remarks>
        public override byte[] CanonicalHash()
        {
            var identity = new XElement(nameof(BivariateHazard));
            identity.SetAttributeValue(nameof(SecondaryIntegrationBins), _secondaryIntegrationBins.ToString(CultureInfo.InvariantCulture));
            identity.SetAttributeValue("CopulaType", _copula.Type.ToString());
            identity.SetAttributeValue("CopulaParameters", FormatCopulaParameters());
            identity.SetAttributeValue("MarginalXHash", _marginalX == null
                ? string.Empty
                : CanonicalContentHasher.ToTokenHex(_marginalX.CanonicalHash()));
            identity.SetAttributeValue("MarginalYHash", _marginalY == null
                ? string.Empty
                : CanonicalContentHasher.ToTokenHex(_marginalY.CanonicalHash()));
            return CanonicalContentHasher.Hash(identity, CanonicalizationRules.ModelRules);
        }

        #endregion

        #region Private Helpers

        /// <summary>
        /// The message reported when a compute surface is reached with an unusable configuration.
        /// </summary>
        private const string UnusableMessage =
            "The bivariate hazard configuration is invalid. Call Validate() and correct the reported errors before sampling.";

        /// <summary>
        /// Validates one marginal link: undefined, non-univariate, or invalid marginals are
        /// errors; axis-label mismatches against the declared pair are warnings.
        /// </summary>
        /// <param name="marginal">The linked marginal, or null while unconfigured.</param>
        /// <param name="axisName">The axis letter for messages ("X" or "Y").</param>
        /// <param name="axisWord">The axis word for messages ("primary" or "secondary").</param>
        /// <param name="declaredHazard">The declared hazard type for the axis.</param>
        /// <param name="declaredUnit">The declared hazard unit for the axis.</param>
        /// <param name="messages">The accumulating message list.</param>
        /// <remarks>
        /// The nested <c>Validate()</c> recursion runs only for univariate marginals: a bivariate
        /// marginal is rejected by the deferral guard without recursion, which also keeps an
        /// (illegal) cyclic marginal graph from recursing forever — a bivariate function can never
        /// be univariate, so every cycle passes through a rejected link.
        /// </remarks>
        private void ValidateMarginal(IHazardFunction? marginal, string axisName, string axisWord,
            string declaredHazard, string declaredUnit, List<string> messages)
        {
            if (marginal == null)
            {
                messages.Add($"Error: The marginal {axisName} hazard function has not been defined for the bivariate hazard.");
                return;
            }

            if (marginal is not IUnivariateHazardFunction)
            {
                messages.Add($"Error: The marginal {axisName} hazard function '{marginal.Name}' must be a univariate hazard function — nested bivariate marginals are not supported.");
                return;
            }

            if (!marginal.Validate().IsValid)
                messages.Add($"Error: The selected marginal {axisName} hazard function '{marginal.Name}' is invalid.");

            if (marginal.SpecifiedHazard != declaredHazard)
                messages.Add($"Warning: The marginal {axisName} hazard function '{marginal.Name}' does not match the {axisWord} hazard type '{declaredHazard}' of the bivariate function.");
            if (marginal.HazardUnit != declaredUnit)
                messages.Add($"Warning: The marginal {axisName} hazard function '{marginal.Name}' does not match the {axisWord} hazard unit '{declaredUnit}' of the bivariate function.");
        }

        /// <summary>
        /// The sample-time usability gate (the cluster's invalid-configuration throw): both
        /// marginals defined, univariate, and distinct; valid copula parameters; a bin count
        /// inside [3, 1000].
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when the configuration is invalid.</exception>
        private void ThrowIfUnusable()
        {
            bool usable =
                _marginalX is IUnivariateHazardFunction &&
                _marginalY is IUnivariateHazardFunction &&
                !ReferenceEquals(_marginalX, _marginalY) &&
                _copula.ParametersValid &&
                _secondaryIntegrationBins >= MinimumSecondaryIntegrationBins &&
                _secondaryIntegrationBins <= MaximumSecondaryIntegrationBins;

            if (!usable) throw new InvalidOperationException(UnusableMessage);
        }

        /// <summary>
        /// Precomputes the realization-independent discretization vectors for the configured
        /// bin count.
        /// </summary>
        private void BuildConditionalVectors()
        {
            int bins = _secondaryIntegrationBins;
            var nodes = new double[bins + 1];
            var weights = new double[bins + 1];
            BuildConditionalVectors(bins, nodes, weights);
            _conditionalNodes = nodes;
            _conditionalWeights = weights;
        }

        /// <summary>
        /// Fills the discretization vectors for a bin count: N + 1 conditional nodes t_j = j/N
        /// with the endpoint nodes clamped to [1e-16, 1 − 1e-16], and the trapezoid weights
        /// with the last computed as the exact residual so the ordered weight sum is exactly
        /// one in floating point.
        /// </summary>
        /// <param name="bins">The bin count.</param>
        /// <param name="nodes">Receives the N + 1 conditional nodes.</param>
        /// <param name="weights">Receives the index-aligned trapezoid weights.</param>
        private static void BuildConditionalVectors(int bins, double[] nodes, double[] weights)
        {
            double interior = 1d / bins;
            double running = 0d;
            for (int j = 0; j < bins; j++)
            {
                nodes[j] = Math.Clamp((double)j / bins, ProbabilityFloor, 1d - ProbabilityFloor);
                weights[j] = j == 0 ? interior / 2d : interior;
                running += weights[j];
            }
            nodes[bins] = 1d - ProbabilityFloor;
            weights[bins] = 1d - running;
        }

        /// <summary>
        /// Builds a mean diagnostic snapshot at an arbitrary bin count — the same mean Y
        /// marginal and cloned copula as <see cref="SampleBivariate()"/> over freshly derived
        /// discretization vectors — without touching the stored bin count, the precomputed
        /// vectors, the canonical hash, or any seed. The discretization-error diagnostic
        /// evaluates the mean pass at halved counts through this snapshot.
        /// </summary>
        /// <param name="bins">The diagnostic bin count, inside the configured [3, 1000] range.</param>
        /// <returns>The mean snapshot at the diagnostic count.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the count is outside the supported range.</exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the bivariate configuration is invalid, or when <see cref="SetupSampler"/>
        /// has not been called since the last bin-count change.
        /// </exception>
        internal SampledBivariateHazard SampleBivariateAt(int bins)
        {
            if (bins < MinimumSecondaryIntegrationBins || bins > MaximumSecondaryIntegrationBins)
            {
                throw new ArgumentOutOfRangeException(nameof(bins),
                    $"The diagnostic bin count must be inside [{MinimumSecondaryIntegrationBins}, {MaximumSecondaryIntegrationBins}].");
            }
            ThrowIfUnusable();
            if (_conditionalNodes == null || _conditionalWeights == null)
                throw new InvalidOperationException("SetupSampler() must be called before sampling the bivariate snapshot.");

            var nodes = new double[bins + 1];
            var weights = new double[bins + 1];
            BuildConditionalVectors(bins, nodes, weights);
            return new SampledBivariateHazard(_marginalY!.SampleFunction(), _copula.Clone(), nodes, weights);
        }

        /// <summary>
        /// Formats the copula parameters for the identity form: pipe-joined round-trip values in
        /// <see cref="BivariateCopula.SetCopulaParameters"/> order; empty for the zero-parameter
        /// independence copula.
        /// </summary>
        /// <returns>The formatted parameter token.</returns>
        private string FormatCopulaParameters()
        {
            var parameters = _copula.GetCopulaParameters;
            if (parameters.Length == 0) return string.Empty;

            var values = new string[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                values[i] = SerializationUtilities.FormatDouble(parameters[i]);
            }
            return string.Join("|", values);
        }

        /// <summary>
        /// Serializes one marginal container: the container element always present (append-only
        /// child order), holding one inline or reference child when the marginal is defined.
        /// </summary>
        /// <param name="containerName">The container element name.</param>
        /// <param name="marginal">The linked marginal, or null while unconfigured.</param>
        /// <param name="mode">The serialization mode.</param>
        /// <returns>The marginal container element.</returns>
        private static XElement WriteMarginal(string containerName, IHazardFunction? marginal, RiskSerializationMode mode)
        {
            var container = new XElement(containerName);
            if (marginal != null) container.Add(FunctionEntry.Write(marginal, mode));
            return container;
        }

        /// <summary>
        /// Reads one marginal container child through the shared function-entry mechanics: inline
        /// content reconstructs through the factory, a reference marker resolves to the live
        /// stored instance (stale id throws; a name-only miss records for <see cref="Validate"/>).
        /// </summary>
        /// <param name="xElement">The serialized bivariate hazard form.</param>
        /// <param name="containerName">The marginal container element name.</param>
        /// <param name="resolver">The function resolver; null when reading a self-contained form.</param>
        /// <param name="linkDescription">The reference description used in unresolved-reference records.</param>
        /// <returns>The marginal, or null when absent or unresolved.</returns>
        private IHazardFunction? ReadMarginal(XElement xElement, string containerName,
            IRiskFunctionResolver? resolver, string linkDescription)
        {
            var child = xElement.Element(containerName)?.Elements().FirstOrDefault();
            if (child == null) return null;

            return FunctionEntry.Read<IHazardFunction>(
                child, resolver, c => RiskFunctionFactory.CreateFromXElement(c, resolver),
                Name, $"The {nameof(BivariateHazard)} '{Name}'", linkDescription,
                _unresolvedFunctionReferences);
        }

        /// <summary>
        /// Re-raises a linked marginal's change notification as a change of the owning link
        /// property. Reentrant notifications are suppressed via
        /// <see cref="_raisingMarginalChange"/> so an (illegal) cyclic marginal graph degrades to
        /// a reportable validation error instead of unbounded recursion.
        /// </summary>
        /// <param name="sender">The linked marginal.</param>
        /// <param name="e">The originating change arguments.</param>
        private void MarginalPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_raisingMarginalChange) return;
            _raisingMarginalChange = true;
            try
            {
                RaisePropertyChange(ReferenceEquals(sender, _marginalX) ? nameof(MarginalX) : nameof(MarginalY));
            }
            finally
            {
                _raisingMarginalChange = false;
            }
        }

        #endregion
    }
}
