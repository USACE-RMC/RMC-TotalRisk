using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Xml.Linq;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.RiskFunctions.Support;

namespace RMC.TotalRisk.RiskFunctions.Responses
{
    /// <summary>
    /// A bivariate response (fragility) function: a deterministic two-dimensional
    /// failure-probability surface P(f | x, y) over strictly ascending primary hazard levels and
    /// weighted secondary hazard levels, consumed either collapsed onto the primary axis through
    /// the stored weights (the preserved v1.0 mode) or evaluated jointly at (x, y) pairs under a
    /// bivariate hazard.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Ported from v1.0 <c>BivariateResponse</c> with the collapse mode preserved exactly: the
    /// weighted collapse SRP(xᵢ) = Σⱼ P[i, j]·wⱼ, the grid auto-resize on level-collection adds
    /// and removes (overlap preserved, new cells zero-filled; other collection actions leave the
    /// grid untouched, the exact v1.0 behavior), the Voronoi-midpoint automatic weight estimation
    /// with last-bin residual absorption in both directions, the
    /// <see cref="EmpiricalDistribution"/> wrapper carrying both interpolation transforms, and
    /// the legacy XML payload shape (<c>PrimaryHazardLevels</c>, <c>WeightedHazardLevel</c>
    /// children, <c>Probability_Row</c> rows) so a project importer can lift legacy payloads
    /// verbatim. <see cref="MinProbability"/>/<see cref="MaxProbability"/> report the collapsed
    /// curve's FIRST and LAST ordinates (v1.0 semantics), not its extrema.
    /// </para>
    /// <para>
    /// <b>Deliberate v1.1 refinements within the preserved surface</b> (the porting rule): (a)
    /// the stored <see cref="SecondaryHazardFunction"/> link persists by
    /// <see cref="IRiskFunction.Id"/> with a lenient name fallback as two metadata attributes,
    /// not by bare name; (b) the serialized weights are the authoritative compute content and are
    /// NEVER re-derived on load or on a property set — derivation runs only on an explicit
    /// <see cref="EstimateWeights()"/> call, fixing the v1.0 load-order bug where opening a
    /// project re-derived (overwrote) stored weights whenever the link resolved; (c) staleness is
    /// surfaced honestly as <see cref="Validate"/> Warnings instead of silent divergence. The
    /// surface payload reads are shape-preserving: a ragged or partially unparseable payload
    /// reconstructs to exactly the parsed shape (absent cells NaN) and fails
    /// <see cref="Validate"/> — never silently truncated or zero-filled as in v1.0.
    /// </para>
    /// <para>
    /// <b>Two operating modes, selected only by the parent</b> — the function holds no mode
    /// state. Under a univariate hazard the inherited sampling surface returns the weighted
    /// collapse and the function behaves exactly like a deterministic tabular response. Under a
    /// bivariate hazard a consumer evaluates <see cref="SurfaceProbability"/> or
    /// <see cref="CreateInterpolator"/> jointly per conditional bin, and the stored weights are
    /// inert. The weights are hashed compute content regardless — they drive the standalone
    /// collapse — so a weight edit re-rolls content-derived seeds even in joint mode, where they
    /// do not affect results; the linked hazard's content, by contrast, never enters this
    /// function's hash, so editing the linked hazard re-rolls nothing until weights are
    /// explicitly re-estimated.
    /// </para>
    /// <para>
    /// <b>Extrapolation policy (deliberate — the native <see cref="Bilinear"/> behavior the
    /// legacy evaluator shipped):</b> both coordinates out of range returns the nearest corner
    /// cell exactly; one coordinate out clamps to its nearest edge row or column with
    /// one-dimensional linear interpolation along the in-range axis (in transform space); and
    /// the response clamps the back-transformed result to [0, 1]. The surface is deterministic
    /// (D = 0): percentile and realization-index overloads return the mean collapse, exact v1.0
    /// behavior.
    /// </para>
    /// </remarks>
    public class BivariateResponse : ResponseFunctionBase, IBivariateResponseFunction
    {
        #region Construction

        /// <summary>
        /// Initializes a bivariate response with the default unit-square zero surface: primary
        /// levels {0, 1}, secondary levels {0, 1} at weight 0.5 each, all probabilities zero.
        /// (v1.0 defaulted to a single level per axis, which the v1.1 validation minimum of two
        /// primary levels would reject out of the box.)
        /// </summary>
        public BivariateResponse()
        {
            _primaryHazardLevels.Add(0d);
            _primaryHazardLevels.Add(1d);
            _secondaryHazardLevels.Add(new WeightedHazardLevel { Level = 0d, Weight = 0.5d });
            _secondaryHazardLevels.Add(new WeightedHazardLevel { Level = 1d, Weight = 0.5d });
            _probabilityValues = new double[2, 2];
            AttachCollectionHandlers();
        }

        /// <summary>
        /// Restores a bivariate response from its serialized form. Reads are permissive and
        /// shape-preserving: a ragged or partially unparseable surface payload reconstructs to
        /// exactly the parsed shape (absent cells NaN) and fails <see cref="Validate"/> — it is
        /// never silently truncated. The stored secondary-hazard link is repaired leniently: the
        /// id is tried first, the name falls back, and any miss (including a resolved function of
        /// the wrong cluster) leaves the link null with the pending reference retained for
        /// re-serialization — never a throw, because the serialized weights are the compute
        /// content. No weight derivation runs during loading.
        /// </summary>
        /// <param name="xElement">The serialized form produced by <see cref="ToXElement"/>.</param>
        /// <param name="resolver">
        /// The function resolver used to re-attach the stored secondary-hazard link to its live
        /// instance; null leaves the link unresolved (retained for re-serialization).
        /// </param>
        /// <exception cref="ArgumentNullException">Thrown when the element is null.</exception>
        public BivariateResponse(XElement xElement, IRiskFunctionResolver? resolver = null)
        {
            if (xElement == null) throw new ArgumentNullException(nameof(xElement));

            ReadIdentityAttributes(xElement);
            SpecifiedHazard = SerializationUtilities.ReadString(xElement, nameof(SpecifiedHazard));
            HazardUnit = SerializationUtilities.ReadString(xElement, nameof(HazardUnit));
            SecondarySpecifiedHazard = SerializationUtilities.ReadString(xElement, nameof(SecondarySpecifiedHazard));
            SecondaryHazardUnit = SerializationUtilities.ReadString(xElement, nameof(SecondaryHazardUnit));
            _hazardTransform = SerializationUtilities.ReadEnum(xElement, nameof(HazardTransform), Transform.None);
            _secondaryHazardTransform = SerializationUtilities.ReadEnum(xElement, nameof(SecondaryHazardTransform), Transform.None);
            _probabilityTransform = SerializationUtilities.ReadEnum(xElement, nameof(ProbabilityTransform), Transform.None);
            _useManualWeights = SerializationUtilities.ReadBoolean(xElement, nameof(UseManualWeights), true);

            double[] primaryLevels = BivariateTableSupport.ReadAxis(xElement, nameof(PrimaryHazardLevels));
            for (int i = 0; i < primaryLevels.Length; i++)
            {
                _primaryHazardLevels.Add(primaryLevels[i]);
            }
            var secondaryElement = xElement.Element(nameof(SecondaryHazardLevels));
            if (secondaryElement != null)
            {
                foreach (var levelElement in secondaryElement.Elements(nameof(WeightedHazardLevel)))
                {
                    _secondaryHazardLevels.Add(new WeightedHazardLevel(levelElement));
                }
            }
            _probabilityValues = BivariateTableSupport.ReadGrid(xElement, nameof(ProbabilityValues), "Probability_Row");
            AttachCollectionHandlers();

            _pendingLinkId = Guid.TryParse(xElement.Attribute(nameof(SecondaryHazardFunctionId))?.Value, out var pendingId) ? pendingId : null;
            _pendingLinkName = xElement.Attribute(nameof(SecondaryHazardFunctionName))?.Value;
            if (resolver != null && (_pendingLinkId != null || !string.IsNullOrEmpty(_pendingLinkName)))
            {
                // The lenient metadata repair: a wrong-cluster resolution falls through the
                // pattern test and is treated as a miss (link null, pending retained).
                if (resolver.TryResolve(_pendingLinkId, _pendingLinkName) is IHazardFunction hazard)
                {
                    SecondaryHazardFunction = hazard;
                }
            }
        }

        #endregion

        #region Members

        /// <summary>
        /// The tolerance on the secondary-weight sum: |Σw − 1| beyond this is a validation error.
        /// </summary>
        private const double WeightSumTolerance = 1e-8;

        /// <summary>
        /// The staleness tolerance: an automatically-derived stored weight differing from a
        /// freshly derived one beyond this triggers the staleness warning.
        /// </summary>
        private const double StaleWeightTolerance = 1e-8;

        /// <summary>
        /// Backing collection for <see cref="PrimaryHazardLevels"/>.
        /// </summary>
        private readonly ObservableCollection<double> _primaryHazardLevels = new ObservableCollection<double>();

        /// <summary>
        /// Backing collection for <see cref="SecondaryHazardLevels"/>.
        /// </summary>
        private readonly ObservableCollection<WeightedHazardLevel> _secondaryHazardLevels = new ObservableCollection<WeightedHazardLevel>();

        /// <summary>
        /// Backing field for <see cref="ProbabilityValues"/> — never null.
        /// </summary>
        private double[,] _probabilityValues = new double[0, 0];

        /// <summary>
        /// Backing field for <see cref="HazardTransform"/>.
        /// </summary>
        private Transform _hazardTransform = Transform.None;

        /// <summary>
        /// Backing field for <see cref="SecondaryHazardTransform"/>.
        /// </summary>
        private Transform _secondaryHazardTransform = Transform.None;

        /// <summary>
        /// Backing field for <see cref="ProbabilityTransform"/> — None by default (v1.0).
        /// </summary>
        private Transform _probabilityTransform = Transform.None;

        /// <summary>
        /// Backing field for <see cref="SecondarySpecifiedHazard"/>.
        /// </summary>
        private string _secondarySpecifiedHazard = string.Empty;

        /// <summary>
        /// Backing field for <see cref="SecondaryHazardUnit"/>.
        /// </summary>
        private string _secondaryHazardUnit = string.Empty;

        /// <summary>
        /// Backing field for <see cref="UseManualWeights"/> — manual by default (v1.0).
        /// </summary>
        private bool _useManualWeights = true;

        /// <summary>
        /// Backing field for <see cref="SecondaryHazardFunction"/>.
        /// </summary>
        private IHazardFunction? _secondaryHazardFunction;

        /// <summary>
        /// The serialized link id retained while the link is unresolved, re-written verbatim so a
        /// resolver-less round trip is bit-equal and provenance is never silently dropped.
        /// Cleared whenever <see cref="SecondaryHazardFunction"/> is assigned — the live link is
        /// then authoritative.
        /// </summary>
        private Guid? _pendingLinkId;

        /// <summary>
        /// The serialized link name retained while the link is unresolved (null when absent —
        /// an empty serialized name round-trips as empty). Cleared whenever
        /// <see cref="SecondaryHazardFunction"/> is assigned.
        /// </summary>
        private string? _pendingLinkName;

        /// <summary>
        /// True while <see cref="LinkPropertyChanged"/> is re-raising, breaking the notification
        /// feedback loop a pathological subscription graph would otherwise create.
        /// </summary>
        private bool _raisingLinkChange;

        /// <summary>
        /// The strictly ascending primary hazard levels defining the surface rows. Adds and
        /// removes auto-resize <see cref="ProbabilityValues"/>, preserving the overlapping cells
        /// and zero-filling new ones (exact v1.0 behavior; other collection actions leave the
        /// grid untouched).
        /// </summary>
        public ObservableCollection<double> PrimaryHazardLevels
        {
            get { return _primaryHazardLevels; }
        }

        /// <summary>
        /// The strictly ascending weighted secondary hazard levels defining the surface columns.
        /// Adds and removes auto-resize <see cref="ProbabilityValues"/>, preserving the
        /// overlapping cells and zero-filling new ones (exact v1.0 behavior). Unlike v1.0,
        /// collection changes never re-derive the weights.
        /// </summary>
        public ObservableCollection<WeightedHazardLevel> SecondaryHazardLevels
        {
            get { return _secondaryHazardLevels; }
        }

        /// <summary>
        /// The failure-probability surface, one row per primary hazard level and one column per
        /// secondary hazard level (<c>ProbabilityValues[i, j] = P(f | primary[i],
        /// secondary[j])</c>, the bilinear convention). Assigning swaps the whole array; null
        /// assignments are ignored.
        /// </summary>
        public double[,] ProbabilityValues
        {
            get { return _probabilityValues; }
            set
            {
                if (!ReferenceEquals(_probabilityValues, value) && value is not null)
                {
                    _probabilityValues = value;
                    RaisePropertyChange(nameof(ProbabilityValues));
                }
            }
        }

        /// <summary>
        /// The interpolation transform applied to the primary hazard axis.
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
        /// The interpolation transform applied to the secondary hazard axis in joint surface
        /// evaluation (new in v1.1 — v1.0 had no secondary-axis transform because the collapse
        /// never interpolated along the secondary axis).
        /// </summary>
        public Transform SecondaryHazardTransform
        {
            get { return _secondaryHazardTransform; }
            set
            {
                if (_secondaryHazardTransform != value)
                {
                    _secondaryHazardTransform = value;
                    RaisePropertyChange(nameof(SecondaryHazardTransform));
                }
            }
        }

        /// <summary>
        /// The interpolation transform applied to the failure-probability axis.
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

        /// <summary>
        /// Whether the secondary weights are manually entered (true, the v1.0 default) or were
        /// derived from the stored secondary-hazard link by <see cref="EstimateWeights()"/>.
        /// Provenance metadata — serialized, never hashed. Unlike v1.0, toggling it never
        /// re-derives the weights.
        /// </summary>
        public bool UseManualWeights
        {
            get { return _useManualWeights; }
            set
            {
                if (_useManualWeights != value)
                {
                    _useManualWeights = value;
                    RaisePropertyChange(nameof(UseManualWeights));
                }
            }
        }

        /// <summary>
        /// The stored secondary hazard function link used by <see cref="EstimateWeights()"/> —
        /// referenced, not owned, and persisted as id/name metadata attributes only. The setter
        /// swaps a change subscription so edits to the linked function re-raise as
        /// <c>SecondaryHazardFunction</c> (staleness awareness for a consuming layer; edits to a
        /// swapped-out function raise nothing) and clears any pending serialized reference — the
        /// live link is then authoritative. Assigning never derives weights. Must be a univariate
        /// hazard function; a bivariate link is a validation error, because its univariate
        /// sampling surface silently delegates to its X marginal.
        /// </summary>
        public IHazardFunction? SecondaryHazardFunction
        {
            get { return _secondaryHazardFunction; }
            set
            {
                if (ReferenceEquals(_secondaryHazardFunction, value)) return;

                if (_secondaryHazardFunction != null) _secondaryHazardFunction.PropertyChanged -= LinkPropertyChanged;
                _secondaryHazardFunction = value;
                if (_secondaryHazardFunction != null) _secondaryHazardFunction.PropertyChanged += LinkPropertyChanged;

                _pendingLinkId = null;
                _pendingLinkName = null;

                RaisePropertyChange(nameof(SecondaryHazardFunction));
            }
        }

        /// <inheritdoc/>
        public override ResponseFunctionType FunctionType => ResponseFunctionType.Bivariate;

        /// <inheritdoc/>
        /// <remarks>Always true — the surface carries no knowledge uncertainty (v1.0 behavior).</remarks>
        public override bool IsDeterministic => true;

        /// <inheritdoc/>
        /// <remarks>Zero — deterministic; the base sampler records the sample size only.</remarks>
        public override int SamplingDimensions => 0;

        /// <inheritdoc/>
        public int SecondaryLevelCount => _secondaryHazardLevels.Count;

        #endregion

        #region IRiskFunction Methods

        /// <inheritdoc/>
        /// <remarks>
        /// Errors (invalidating): missing axis labels (the primary and secondary hazard pairs);
        /// fewer than two primary hazard levels; fewer than one secondary hazard level;
        /// non-finite or non-strictly-ascending levels on either axis; a surface not dimensioned
        /// one row per primary level by one column per secondary level; a non-finite surface
        /// probability or one outside [0, 1]; a secondary weight that is non-finite or outside
        /// [0, 1]; weights not summing to one (±1e-8); a logarithmic or normal-Z interpolation
        /// transform over incompatible values (per axis and over the surface); and a bivariate
        /// secondary-hazard link. Warnings (advisory): a first collapsed ordinate above 1e-8 and
        /// a non-monotone collapsed curve (the exact v1.0 conditions); a single secondary hazard
        /// level (the surface degenerates to a univariate response — joint evaluation under a
        /// bivariate hazard requires at least two, enforced mode-side); and the two
        /// automatic-weight staleness rules — stored weights differing beyond 1e-8 from weights
        /// freshly derived from a usable link, or an unresolved/invalid link that prevents the
        /// staleness check entirely.
        /// </remarks>
        public override (bool IsValid, List<string> ValidationMessages) Validate()
        {
            var messages = new List<string>();

            if (string.IsNullOrEmpty(SpecifiedHazard))
                messages.Add("Error: The bivariate response function does not have a specified hazard type.");
            if (string.IsNullOrEmpty(HazardUnit))
                messages.Add("Error: The bivariate response function does not have a specified hazard unit.");
            if (string.IsNullOrEmpty(SecondarySpecifiedHazard))
                messages.Add("Error: The bivariate response function does not have a specified secondary hazard type.");
            if (string.IsNullOrEmpty(SecondaryHazardUnit))
                messages.Add("Error: The bivariate response function does not have a specified secondary hazard unit.");

            bool structureUsable = ValidateStructure(messages);
            ValidateWeights(messages);
            ValidateLink(messages);

            if (structureUsable)
            {
                BivariateTableSupport.ValidateAxisTransform(messages, "hazard", _hazardTransform, CopyPrimaryLevels());
                BivariateTableSupport.ValidateAxisTransform(messages, "secondary hazard", _secondaryHazardTransform, CopySecondaryLevels());
                BivariateTableSupport.ValidateSurfaceTransform(messages, "probability", _probabilityTransform, _probabilityValues);

                var collapse = SampleResponseFunction();
                double firstHazard = collapse[0].X;
                double firstMeanProbability = collapse[0].Y;
                if (firstMeanProbability > 0.00000001d)
                {
                    messages.Add("Warning: Any hazard level evaluated below " + firstHazard.ToString("N2", CultureInfo.InvariantCulture)
                        + " will result in a probability of failure greater than zero (" + firstMeanProbability.ToString("E4", CultureInfo.InvariantCulture)
                        + ") on average. This can result in inaccurate risk estimates for lower hazard levels.");
                }

                if (!IsMonotonic())
                {
                    messages.Add("Warning: The response function is not monotonically increasing. Please confirm you have entered your inputs correctly before proceeding.");
                }
            }

            if (_secondaryHazardLevels.Count == 1)
            {
                messages.Add("Warning: The bivariate response function has a single secondary hazard level and degenerates to a univariate response. Joint evaluation under a bivariate hazard requires at least two secondary hazard levels.");
            }

            return (messages.FindIndex(m => m.StartsWith("Error:", StringComparison.Ordinal)) < 0, messages);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The preserved v1.0 collapse: SRP(xᵢ) = Σⱼ P[i, j]·wⱼ over the stored secondary
        /// weights, as an ordered curve with strictly ascending hazards and unconstrained (not
        /// forced monotone) probabilities.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown when the table is invalid.</exception>
        public override OrderedPairedData SampleResponseFunction()
        {
            ThrowIfCollapseUnusable();
            int rows = _primaryHazardLevels.Count;
            int columns = _secondaryHazardLevels.Count;
            var hazards = new double[rows];
            var probabilities = new double[rows];
            for (int i = 0; i < rows; i++)
            {
                double mixedProbability = 0d;
                for (int j = 0; j < columns; j++)
                {
                    mixedProbability += _probabilityValues[i, j] * _secondaryHazardLevels[j].Weight;
                }
                hazards[i] = _primaryHazardLevels[i];
                probabilities[i] = mixedProbability;
            }
            return new OrderedPairedData(hazards, probabilities, true, SortOrder.Ascending, false, SortOrder.None);
        }

        /// <inheritdoc/>
        /// <remarks>Returns the mean collapse — the surface is deterministic (v1.0 behavior).</remarks>
        /// <exception cref="InvalidOperationException">Thrown when the table is invalid.</exception>
        public override OrderedPairedData SampleResponseFunction(double percentile)
        {
            return SampleResponseFunction();
        }

        /// <inheritdoc/>
        /// <remarks>Returns the mean collapse — the surface is deterministic (v1.0 behavior).</remarks>
        /// <exception cref="InvalidOperationException">Thrown when the table is invalid.</exception>
        public override OrderedPairedData SampleResponseFunction(int realizationIndex)
        {
            return SampleResponseFunction();
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The collapsed curve as a distribution whose CDF is the failure probability, carrying
        /// both interpolation transforms (exact v1.0 construction).
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown when the table is invalid.</exception>
        public override IUnivariateDistribution SampleFunction()
        {
            return new EmpiricalDistribution(SampleResponseFunction()) { XTransform = _hazardTransform, ProbabilityTransform = _probabilityTransform };
        }

        /// <inheritdoc/>
        /// <remarks>Returns the mean collapse distribution — the surface is deterministic (v1.0 behavior).</remarks>
        /// <exception cref="InvalidOperationException">Thrown when the table is invalid.</exception>
        public override IUnivariateDistribution SampleFunction(double percentile)
        {
            return SampleFunction();
        }

        /// <inheritdoc/>
        /// <remarks>Returns the mean collapse distribution — the surface is deterministic (v1.0 behavior).</remarks>
        /// <exception cref="InvalidOperationException">Thrown when the table is invalid.</exception>
        public override IUnivariateDistribution SampleFunction(int realizationIndex)
        {
            return SampleFunction();
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Exact v1.0 algorithm on the deterministic mean collapse: any decreasing
        /// failure-probability step fails the check.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown when the table is invalid.</exception>
        public override bool IsMonotonic()
        {
            bool monotonic = true;
            var func = SampleResponseFunction();
            for (int i = 1; i < func.Count; i++)
            {
                if (func[i].Y < func[i - 1].Y)
                    monotonic = false;
            }
            return monotonic;
        }

        /// <inheritdoc/>
        /// <remarks>The first primary hazard level (v1.0 semantics, made loud on an empty axis).</remarks>
        /// <exception cref="InvalidOperationException">Thrown when there are no primary hazard levels.</exception>
        public override double MinHazard()
        {
            if (_primaryHazardLevels.Count == 0)
                throw new InvalidOperationException("The bivariate response function has no primary hazard levels.");
            return _primaryHazardLevels[0];
        }

        /// <inheritdoc/>
        /// <remarks>The last primary hazard level (v1.0 semantics, made loud on an empty axis).</remarks>
        /// <exception cref="InvalidOperationException">Thrown when there are no primary hazard levels.</exception>
        public override double MaxHazard()
        {
            if (_primaryHazardLevels.Count == 0)
                throw new InvalidOperationException("The bivariate response function has no primary hazard levels.");
            return _primaryHazardLevels[_primaryHazardLevels.Count - 1];
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The collapsed curve's FIRST ordinate — v1.0 semantics, not the curve minimum (they
        /// differ on a non-monotone collapse).
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown when the table is invalid.</exception>
        public override double MinProbability()
        {
            var func = SampleResponseFunction();
            return func[0].Y;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The collapsed curve's LAST ordinate — v1.0 semantics, not the curve maximum (they
        /// differ on a non-monotone collapse).
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown when the table is invalid.</exception>
        public override double MaxProbability()
        {
            var func = SampleResponseFunction();
            return func[func.Count - 1].Y;
        }

        /// <inheritdoc/>
        /// <remarks>Not applicable — the surface is deterministic; returns null.</remarks>
        public override UncertaintyAnalysisResults? ComputeUncertaintyResults(double confidenceIntervalWidth = 0.9)
        {
            return null;
        }

        #endregion

        #region IBivariateResponseFunction Methods

        /// <inheritdoc/>
        public double SurfaceProbability(double x, double y)
        {
            double probability = CreateInterpolator().Interpolate(x, y);
            if (probability < 0d) return 0d;
            if (probability > 1d) return 1d;
            return probability;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The interpolator shares the function's surface array (the array is only ever swapped
        /// whole, never mutated in place) while the two axis vectors are copied out of the level
        /// collections on each call — the collections are the preserved v1.0 storage, so there
        /// are no axis arrays to share, and nothing is cached on the function by the pinned
        /// thread discipline. Requires at least two levels on each axis
        /// (<see cref="Validate"/> reports a single secondary level as the degenerate univariate
        /// case).
        /// </remarks>
        public Bilinear CreateInterpolator()
        {
            var primary = CopyPrimaryLevels();
            var secondary = CopySecondaryLevels();
            if (!BivariateTableSupport.IsUsable(primary, secondary, _probabilityValues))
                throw new InvalidOperationException("The bivariate response surface is invalid. Call Validate() and correct the reported errors before evaluating.");
            return new Bilinear(primary, secondary, _probabilityValues, SortOrder.Ascending)
            {
                X1Transform = _hazardTransform,
                X2Transform = _secondaryHazardTransform,
                YTransform = _probabilityTransform,
            };
        }

        /// <inheritdoc/>
        /// <remarks>The first secondary hazard level (made loud on an empty collection).</remarks>
        /// <exception cref="InvalidOperationException">Thrown when there are no secondary hazard levels.</exception>
        public double MinSecondaryHazard()
        {
            if (_secondaryHazardLevels.Count == 0)
                throw new InvalidOperationException("The bivariate response function has no secondary hazard levels.");
            return _secondaryHazardLevels[0].Level;
        }

        /// <inheritdoc/>
        /// <remarks>The last secondary hazard level (made loud on an empty collection).</remarks>
        /// <exception cref="InvalidOperationException">Thrown when there are no secondary hazard levels.</exception>
        public double MaxSecondaryHazard()
        {
            if (_secondaryHazardLevels.Count == 0)
                throw new InvalidOperationException("The bivariate response function has no secondary hazard levels.");
            return _secondaryHazardLevels[_secondaryHazardLevels.Count - 1].Level;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Derives the secondary weights from the stored <see cref="SecondaryHazardFunction"/>
        /// link and sets <see cref="UseManualWeights"/> false. The exact v1.0 algorithm: a single
        /// level takes weight one; otherwise Voronoi midpoints between adjacent levels are
        /// evaluated against the link's mean marginal CDF — the first bin takes everything below
        /// the first midpoint, interior bins take CDF differences, the last bin takes the upper
        /// tail — and the last bin absorbs the floating-point residual in either direction so the
        /// weights sum to one exactly. Derivation runs ONLY here (and in the explicit overload) —
        /// never on load or on a property set, the deliberate v1.1 fix of the v1.0 load-order
        /// overwrite bug.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the link is null (unset or unresolved), bivariate, or invalid, or when
        /// there are no secondary hazard levels to weight.
        /// </exception>
        public void EstimateWeights()
        {
            var hazard = _secondaryHazardFunction
                ?? throw new InvalidOperationException(
                    "The bivariate response function has no resolved secondary hazard function link. Assign SecondaryHazardFunction or call EstimateWeights(IHazardFunction).");
            ThrowIfHazardUnusableForWeights(hazard);
            ThrowIfNoLevelsToWeight();
            DeriveAndApplyWeights(hazard);
        }

        /// <summary>
        /// Derives the secondary weights from an explicitly supplied secondary hazard function —
        /// the headless-caller overload — storing the argument as the
        /// <see cref="SecondaryHazardFunction"/> link and setting <see cref="UseManualWeights"/>
        /// false. Same algorithm as <see cref="EstimateWeights()"/>; every guard runs before any
        /// state changes.
        /// </summary>
        /// <param name="secondaryHazardFunction">The univariate secondary hazard function to derive from.</param>
        /// <exception cref="ArgumentNullException">Thrown when the hazard function is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the hazard function is bivariate or invalid, or when there are no
        /// secondary hazard levels to weight.
        /// </exception>
        public void EstimateWeights(IHazardFunction secondaryHazardFunction)
        {
            if (secondaryHazardFunction == null) throw new ArgumentNullException(nameof(secondaryHazardFunction));
            ThrowIfHazardUnusableForWeights(secondaryHazardFunction);
            ThrowIfNoLevelsToWeight();
            SecondaryHazardFunction = secondaryHazardFunction;
            DeriveAndApplyWeights(secondaryHazardFunction);
        }

        #endregion

        #region Serialization

        /// <inheritdoc/>
        /// <remarks>
        /// The v1.1 attribute envelope over the LEGACY inner payload shape — pipe-joined "G17"
        /// <c>PrimaryHazardLevels</c>, <c>WeightedHazardLevel</c> children, and
        /// <c>ProbabilityValues</c> with <c>Probability_Row</c> rows (these cells ARE
        /// probabilities) — kept deliberately so a project importer can lift legacy payloads
        /// verbatim. The link attributes are written from the live function when resolved, from
        /// the retained pending reference when not, and omitted when no link was ever stored.
        /// Unlike v1.0, the surface is written at its stored shape even when it disagrees with
        /// the level counts, so nothing is silently dropped.
        /// </remarks>
        public override XElement ToXElement()
        {
            var element = new XElement(nameof(BivariateResponse));
            WriteIdentityAttributes(element);
            element.SetAttributeValue(nameof(SpecifiedHazard), SpecifiedHazard);
            element.SetAttributeValue(nameof(HazardUnit), HazardUnit);
            element.SetAttributeValue(nameof(SecondarySpecifiedHazard), SecondarySpecifiedHazard);
            element.SetAttributeValue(nameof(SecondaryHazardUnit), SecondaryHazardUnit);
            element.SetAttributeValue(nameof(HazardTransform), _hazardTransform.ToString());
            element.SetAttributeValue(nameof(SecondaryHazardTransform), _secondaryHazardTransform.ToString());
            element.SetAttributeValue(nameof(ProbabilityTransform), _probabilityTransform.ToString());
            element.SetAttributeValue(nameof(UseManualWeights), _useManualWeights.ToString());
            if (_secondaryHazardFunction != null)
            {
                element.SetAttributeValue(nameof(SecondaryHazardFunctionId), _secondaryHazardFunction.Id.ToString("D"));
                element.SetAttributeValue(nameof(SecondaryHazardFunctionName), _secondaryHazardFunction.Name);
            }
            else
            {
                if (_pendingLinkId != null)
                    element.SetAttributeValue(nameof(SecondaryHazardFunctionId), _pendingLinkId.Value.ToString("D"));
                if (_pendingLinkName != null)
                    element.SetAttributeValue(nameof(SecondaryHazardFunctionName), _pendingLinkName);
            }

            element.Add(BivariateTableSupport.WriteAxis(nameof(PrimaryHazardLevels), CopyPrimaryLevels()));
            var secondaryElement = new XElement(nameof(SecondaryHazardLevels));
            foreach (var level in _secondaryHazardLevels)
            {
                secondaryElement.Add(level.ToXElement());
            }
            element.Add(secondaryElement);
            element.Add(BivariateTableSupport.WriteGrid(nameof(ProbabilityValues), "Probability_Row", _probabilityValues));
            return element;
        }

        /// <summary>
        /// The serialized attribute name carrying the stored secondary-hazard link's id.
        /// Provenance metadata — stripped from canonical hashing.
        /// </summary>
        private const string SecondaryHazardFunctionId = nameof(SecondaryHazardFunctionId);

        /// <summary>
        /// The serialized attribute name carrying the stored secondary-hazard link's name (the
        /// lenient fallback). Provenance metadata — stripped from canonical hashing.
        /// </summary>
        private const string SecondaryHazardFunctionName = nameof(SecondaryHazardFunctionName);

        #endregion

        #region Private Helpers

        /// <summary>
        /// Attaches the level-collection change handlers driving the v1.0 grid auto-resize.
        /// Called after construction fills the collections, so loading never resizes a
        /// freshly-parsed surface.
        /// </summary>
        private void AttachCollectionHandlers()
        {
            _primaryHazardLevels.CollectionChanged += PrimaryHazardLevelsChanged;
            _secondaryHazardLevels.CollectionChanged += SecondaryHazardLevelsChanged;
        }

        /// <summary>
        /// Handles primary-level collection changes: adds and removes resize the grid (exact
        /// v1.0 behavior — other actions do not), and every change re-raises the collection
        /// property.
        /// </summary>
        /// <param name="sender">The collection.</param>
        /// <param name="e">The change arguments.</param>
        private void PrimaryHazardLevelsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Add || e.Action == NotifyCollectionChangedAction.Remove)
                ResizeProbabilityMatrix();
            RaisePropertyChange(nameof(PrimaryHazardLevels));
        }

        /// <summary>
        /// Handles secondary-level collection changes: adds and removes resize the grid (exact
        /// v1.0 behavior — other actions do not), and every change re-raises the collection
        /// property. Unlike v1.0, no weight derivation runs here.
        /// </summary>
        /// <param name="sender">The collection.</param>
        /// <param name="e">The change arguments.</param>
        private void SecondaryHazardLevelsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Add || e.Action == NotifyCollectionChangedAction.Remove)
                ResizeProbabilityMatrix();
            RaisePropertyChange(nameof(SecondaryHazardLevels));
        }

        /// <summary>
        /// Rebuilds the grid at the current level counts, preserving the overlapping cells and
        /// zero-filling new ones (the exact v1.0 resize; safe against any stored grid shape).
        /// </summary>
        private void ResizeProbabilityMatrix()
        {
            var resized = new double[_primaryHazardLevels.Count, _secondaryHazardLevels.Count];
            int copyRows = Math.Min(resized.GetLength(0), _probabilityValues.GetLength(0));
            int copyColumns = Math.Min(resized.GetLength(1), _probabilityValues.GetLength(1));
            for (int i = 0; i < copyRows; i++)
            {
                for (int j = 0; j < copyColumns; j++)
                {
                    resized[i, j] = _probabilityValues[i, j];
                }
            }
            ProbabilityValues = resized;
        }

        /// <summary>
        /// Re-raises a linked secondary hazard's change notification as a change of
        /// <see cref="SecondaryHazardFunction"/>, giving a consuming layer its staleness hook.
        /// Reentrant notifications are suppressed via <see cref="_raisingLinkChange"/>.
        /// </summary>
        /// <param name="sender">The linked hazard function.</param>
        /// <param name="e">The originating change arguments.</param>
        private void LinkPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_raisingLinkChange) return;
            _raisingLinkChange = true;
            try
            {
                RaisePropertyChange(nameof(SecondaryHazardFunction));
            }
            finally
            {
                _raisingLinkChange = false;
            }
        }

        /// <summary>
        /// Copies the primary levels into a fresh vector (the collections are the preserved v1.0
        /// storage, so axis vectors are materialized on demand).
        /// </summary>
        /// <returns>The primary hazard levels.</returns>
        private double[] CopyPrimaryLevels()
        {
            var values = new double[_primaryHazardLevels.Count];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = _primaryHazardLevels[i];
            }
            return values;
        }

        /// <summary>
        /// Copies the secondary level values into a fresh vector.
        /// </summary>
        /// <returns>The secondary hazard levels.</returns>
        private double[] CopySecondaryLevels()
        {
            var values = new double[_secondaryHazardLevels.Count];
            for (int j = 0; j < values.Length; j++)
            {
                values[j] = _secondaryHazardLevels[j].Level;
            }
            return values;
        }

        /// <summary>
        /// Determines whether the weighted collapse can be computed: at least two finite,
        /// strictly ascending primary levels; at least one secondary level; matching grid
        /// dimensions; finite cells. Weight and level VALUES never gate compute (v1.0 computed
        /// the collapse regardless; value validity is <see cref="Validate"/>'s concern).
        /// </summary>
        /// <returns>True when the collapse is computable.</returns>
        private bool CollapseIsUsable()
        {
            int rows = _primaryHazardLevels.Count;
            int columns = _secondaryHazardLevels.Count;
            if (rows < 2 || columns < 1) return false;
            if (_probabilityValues.GetLength(0) != rows || _probabilityValues.GetLength(1) != columns) return false;

            if (!double.IsFinite(_primaryHazardLevels[0])) return false;
            for (int i = 1; i < rows; i++)
            {
                if (!double.IsFinite(_primaryHazardLevels[i]) || _primaryHazardLevels[i] <= _primaryHazardLevels[i - 1]) return false;
            }
            for (int i = 0; i < rows; i++)
            {
                for (int j = 0; j < columns; j++)
                {
                    if (!double.IsFinite(_probabilityValues[i, j])) return false;
                }
            }
            return true;
        }

        /// <summary>
        /// The collapse usability gate (the cluster's invalid-configuration throw).
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when the table is invalid.</exception>
        private void ThrowIfCollapseUnusable()
        {
            if (!CollapseIsUsable())
                throw new InvalidOperationException("The bivariate response table is invalid. Call Validate() and correct the reported errors before sampling.");
        }

        /// <summary>
        /// Rejects a weight-derivation hazard that is bivariate (its univariate sampling surface
        /// silently delegates to its X marginal) or invalid.
        /// </summary>
        /// <param name="hazard">The candidate secondary hazard function.</param>
        /// <exception cref="InvalidOperationException">Thrown when the hazard is bivariate or invalid.</exception>
        private static void ThrowIfHazardUnusableForWeights(IHazardFunction hazard)
        {
            if (hazard is IBivariateHazardFunction)
                throw new InvalidOperationException(
                    $"The secondary hazard function '{hazard.Name}' is bivariate; weights must be derived from a univariate hazard function.");
            if (!hazard.Validate().IsValid)
                throw new InvalidOperationException(
                    $"The secondary hazard function '{hazard.Name}' is invalid. Call Validate() on it and correct the reported errors before estimating weights.");
        }

        /// <summary>
        /// Rejects weight derivation over an empty secondary-level collection — an explicit
        /// estimation call with nothing to weight must be loud, where v1.0's event-driven
        /// derivation silently returned.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when there are no secondary hazard levels.</exception>
        private void ThrowIfNoLevelsToWeight()
        {
            if (_secondaryHazardLevels.Count == 0)
                throw new InvalidOperationException("The bivariate response function has no secondary hazard levels to weight.");
        }

        /// <summary>
        /// Derives the Voronoi weights from a hazard's mean marginal CDF, writes them onto the
        /// level collection, marks the weights automatic, and re-raises the collection property
        /// (its contents changed).
        /// </summary>
        /// <param name="hazard">The validated univariate secondary hazard function.</param>
        private void DeriveAndApplyWeights(IHazardFunction hazard)
        {
            var weights = new double[_secondaryHazardLevels.Count];
            DeriveVoronoiWeights(hazard.SampleFunction(), _secondaryHazardLevels, weights);
            for (int j = 0; j < weights.Length; j++)
            {
                _secondaryHazardLevels[j].Weight = weights[j];
            }
            UseManualWeights = false;
            RaisePropertyChange(nameof(SecondaryHazardLevels));
        }

        /// <summary>
        /// The pure v1.0 weight-derivation core, shared by <see cref="EstimateWeights()"/> and
        /// the <see cref="Validate"/> staleness comparison so the two can never drift: a single
        /// level takes weight one; otherwise the first bin takes F(μ₁), interior bins take
        /// F(μᵢ) − F(μᵢ₋₁) over Voronoi midpoints μᵢ = (Lᵢ + Lᵢ₋₁)/2, the last bin takes
        /// 1 − F(μ_last), and the last bin then absorbs the floating-point summation residual in
        /// either direction so the weights sum to one exactly.
        /// </summary>
        /// <param name="meanFunction">The link's mean marginal distribution.</param>
        /// <param name="levels">The secondary hazard levels (their level values position the midpoints).</param>
        /// <param name="destination">Receives one weight per level; length must equal the level count.</param>
        private static void DeriveVoronoiWeights(IUnivariateDistribution meanFunction,
            IList<WeightedHazardLevel> levels, double[] destination)
        {
            int count = levels.Count;
            if (count == 1)
            {
                destination[0] = 1d;
                return;
            }

            double previous = 0d;
            for (int i = 1; i < count; i++)
            {
                double midpoint = (levels[i].Level + levels[i - 1].Level) / 2d;
                double cdf = meanFunction.CDF(midpoint);
                destination[i - 1] = i == 1 ? cdf : cdf - previous;
                previous = cdf;
            }
            destination[count - 1] = 1d - previous;

            double sum = 0d;
            for (int i = 0; i < count; i++)
            {
                sum += destination[i];
            }
            if (sum > 1d)
            {
                destination[count - 1] -= sum - 1d;
            }
            else if (sum < 1d)
            {
                destination[count - 1] += 1d - sum;
            }
        }

        /// <summary>
        /// Validates the surface structure, appending precise per-rule errors: at least two
        /// finite, strictly ascending primary levels; at least ONE finite, strictly ascending
        /// secondary level (the collapse admits a single weighted level — the shared table rule
        /// of two per axis applies only to joint evaluation, reported as the degenerate-level
        /// warning); matching grid dimensions; and every cell finite and within [0, 1].
        /// </summary>
        /// <param name="messages">The message sink.</param>
        /// <returns>True when the structure is usable and the transform guards and collapse advisories can run.</returns>
        private bool ValidateStructure(List<string> messages)
        {
            // Bitwise-and on purpose: each axis reports its own error rather than the first
            // failure hiding the second axis's problems (the shared-table precedent).
            bool usable = ValidatePrimaryAxis(messages);
            usable &= ValidateSecondaryAxis(messages);

            if (_probabilityValues.GetLength(0) != _primaryHazardLevels.Count
                || _probabilityValues.GetLength(1) != _secondaryHazardLevels.Count)
            {
                messages.Add("Error: The bivariate response function's surface must have one row of probabilities per primary hazard level and one column per secondary hazard level.");
                return false;
            }

            for (int i = 0; i < _probabilityValues.GetLength(0); i++)
            {
                for (int j = 0; j < _probabilityValues.GetLength(1); j++)
                {
                    double cell = _probabilityValues[i, j];
                    if (!double.IsFinite(cell))
                    {
                        messages.Add("Error: The bivariate response function's surface probabilities must all be finite.");
                        return false;
                    }
                    if (cell < 0d || cell > 1d)
                    {
                        messages.Add("Error: The bivariate response function's surface probabilities must be between 0 and 1.");
                        return false;
                    }
                }
            }
            return usable;
        }

        /// <summary>
        /// Validates the primary axis: at least two values, all finite, strictly ascending — one
        /// error per axis, finiteness before ordering (ordering is meaningless against NaN).
        /// </summary>
        /// <param name="messages">The message sink.</param>
        /// <returns>True when the axis is structurally usable.</returns>
        private bool ValidatePrimaryAxis(List<string> messages)
        {
            int rows = _primaryHazardLevels.Count;
            if (rows < 2)
            {
                messages.Add("Error: The bivariate response function must have at least two primary hazard levels.");
                return false;
            }
            for (int i = 0; i < rows; i++)
            {
                if (!double.IsFinite(_primaryHazardLevels[i]))
                {
                    messages.Add("Error: The bivariate response function's primary hazard levels must all be finite.");
                    return false;
                }
            }
            for (int i = 1; i < rows; i++)
            {
                if (_primaryHazardLevels[i] <= _primaryHazardLevels[i - 1])
                {
                    messages.Add("Error: The bivariate response function's primary hazard levels must be strictly ascending.");
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Validates the secondary axis: at least ONE level, all level values finite, strictly
        /// ascending when there are two or more — one error per axis, finiteness before
        /// ordering.
        /// </summary>
        /// <param name="messages">The message sink.</param>
        /// <returns>True when the axis is structurally usable.</returns>
        private bool ValidateSecondaryAxis(List<string> messages)
        {
            int columns = _secondaryHazardLevels.Count;
            if (columns < 1)
            {
                messages.Add("Error: The bivariate response function must have at least one secondary hazard level.");
                return false;
            }
            for (int j = 0; j < columns; j++)
            {
                if (!double.IsFinite(_secondaryHazardLevels[j].Level))
                {
                    messages.Add("Error: The bivariate response function's secondary hazard levels must all be finite.");
                    return false;
                }
            }
            for (int j = 1; j < columns; j++)
            {
                if (_secondaryHazardLevels[j].Level <= _secondaryHazardLevels[j - 1].Level)
                {
                    messages.Add("Error: The bivariate response function's secondary hazard levels must be strictly ascending.");
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Validates the secondary weights (exact v1.0 rules with the pinned tolerance): each
        /// weight finite and within [0, 1], and the weights summing to one within ±1e-8. The sum
        /// rule reports only when the range rule passes (an out-of-range weight already implies a
        /// garbage sum — the composite precedent).
        /// </summary>
        /// <param name="messages">The message sink.</param>
        private void ValidateWeights(List<string> messages)
        {
            int columns = _secondaryHazardLevels.Count;
            if (columns == 0) return;

            bool anyOutOfRange = false;
            double weightSum = 0d;
            for (int j = 0; j < columns; j++)
            {
                double weight = _secondaryHazardLevels[j].Weight;
                if (!double.IsFinite(weight) || weight < 0d || weight > 1d) anyOutOfRange = true;
                weightSum += weight;
            }

            if (anyOutOfRange)
                messages.Add("Error: The secondary hazard weight values must be between 0 and 1.");
            if (!anyOutOfRange && Math.Abs(weightSum - 1d) > WeightSumTolerance)
                messages.Add("Error: The secondary hazard weight values must sum to 1.");
        }

        /// <summary>
        /// Validates the stored secondary-hazard link: a bivariate link is an error (its
        /// univariate sampling surface silently delegates to its X marginal); and when the
        /// weights are automatic, staleness is surfaced — an unresolved or invalid link warns
        /// that the check cannot run, and a usable link warns when freshly derived weights
        /// diverge from the stored ones beyond the pinned tolerance. An unresolved link is NEVER
        /// an error: the serialized weights are the compute content.
        /// </summary>
        /// <param name="messages">The message sink.</param>
        private void ValidateLink(List<string> messages)
        {
            if (_secondaryHazardFunction is IBivariateHazardFunction)
            {
                messages.Add($"Error: The secondary hazard function link '{_secondaryHazardFunction.Name}' is bivariate; weights must be derived from a univariate hazard function.");
                return;
            }
            if (_useManualWeights) return;

            if (_secondaryHazardFunction == null || !_secondaryHazardFunction.Validate().IsValid)
            {
                messages.Add("Warning: The secondary hazard weights were derived automatically, but the secondary hazard function link is unresolved or invalid, so the stored weights cannot be checked for staleness or re-estimated.");
                return;
            }
            if (_secondaryHazardLevels.Count == 0) return;

            var derived = new double[_secondaryHazardLevels.Count];
            DeriveVoronoiWeights(_secondaryHazardFunction.SampleFunction(), _secondaryHazardLevels, derived);
            for (int j = 0; j < derived.Length; j++)
            {
                if (Math.Abs(derived[j] - _secondaryHazardLevels[j].Weight) > StaleWeightTolerance)
                {
                    messages.Add("Warning: The stored secondary hazard weights differ from weights freshly derived from the linked secondary hazard function. Re-estimate the weights or switch to manual weights.");
                    break;
                }
            }
        }

        #endregion
    }
}
