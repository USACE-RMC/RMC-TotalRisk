using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Numerics;
using Numerics.Data;
using Numerics.Data.Statistics;
using Numerics.Distributions;
using Numerics.Mathematics.RootFinding;
using Numerics.Mathematics.SpecialFunctions;
using Numerics.Sampling;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.Core.Support;

namespace RMC.TotalRisk.RiskFunctions.Hazards
{
    /// <summary>
    /// A nonparametric (graphical) hazard function: a user-entered annual-exceedance-probability
    /// vs. hazard curve whose per-ordinate knowledge uncertainty is derived from the asymptotic
    /// order-statistic quantile variance — the HEC-FDA "less simple method", preserved for
    /// backwards compatibility with existing HEC-FDA flood risk management studies.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Ported from v1.0 <c>NonparametricHazard</c> with the domain surface preserved verbatim
    /// (property names, defaults, validation rules, the derivation's statistical method, and the
    /// co-monotonic sampling semantics). <see cref="InputUncertainFunction"/> carries the
    /// user-entered curve (strictly descending exceedance-probability X, strictly ascending
    /// hazard Y); <see cref="TrueUncertainFunction"/> is DERIVED from it by
    /// <c>UpdateHazardFunction()</c>: the curve is extended by linear extrapolation (on the
    /// configured interpolation transforms) to <see cref="ExtrapolationEP"/> at the rare end and
    /// to AEP 0.999 at the frequent end, each ordinate's quantile standard error is the
    /// order-statistic form <c>SE = √(p·(1−p) / (N·f(x)²))</c> with N =
    /// <see cref="EffectiveRecordLength"/> and f the empirical density of the (log-)mean curve,
    /// SEs are pinned beyond p ∈ [0.01, 0.99] and smoothed monotone toward both tails, and each
    /// ordinate becomes a <see cref="LnNormal"/> (via the base-e log-normal moment mapping when
    /// <see cref="HazardTransform"/> is logarithmic) whose σ is repaired so the 1% confidence
    /// bound never inverts between adjacent ordinates.
    /// </para>
    /// <para>
    /// <b>Improved over v1.0 (results-preserving; the Table 38 verification pins are the
    /// arbiter):</b> (1) the per-ordinate Brent root find in the 1%-bound repair is replaced by
    /// the exact closed-form solution — with real-moment parameters (m, s) the repaired log-space
    /// scale solves the quadratic <c>v²/2 − z·v + (ln q − ln m) = 0</c> (z = Φ⁻¹(0.01), q = the
    /// previous ordinate's 1% bound), so <c>v = z + √(z² − 2(ln q − ln m))</c> and
    /// <c>s = m·√(exp(v²) − 1)</c>; the legacy Brent call remains as the guarded fallback for
    /// degenerate configurations. (2) The base-e <c>LogNormal</c> moment mapping and the
    /// previous-bound tracking are inlined closed forms instead of per-ordinate distribution
    /// allocations. (3) The frequent-end extrapolation is evaluated <i>before</i> the ordinate
    /// lists are mutated — v1.0 inserted the 0.999 probability first, which corrupted its own
    /// interpolator and silently produced a flat extension instead of the intended linear
    /// extrapolation (inert for inputs already anchored at AEP 0.999, the HEC-FDA convention and
    /// this type's default). (4) Invalid-state sampling throws instead of returning null.
    /// </para>
    /// <para>
    /// <b>Serialization is inputs-only:</b> <see cref="TrueUncertainFunction"/> is a pure
    /// deterministic function of the serialized inputs (no PRNG, no parallelism) and is
    /// recomputed on load, so the canonical-hash identity surface is exactly the user-specified
    /// content — v1.0 persisted the derived table too, but its own load path already re-derived
    /// before overwriting, so the stored copy only ever mattered when it diverged from the
    /// algorithm. The standard caveat applies: a change to the derivation moves loaded
    /// results without moving hashes (a documented re-pin event, the <c>ForceMonotonic</c>
    /// precedent). Beyond the §5.5.3 summary row, <see cref="ExtrapolationEP"/> and
    /// <see cref="IsUncertain"/> are serialized and hashed — both are compute-relevant.
    /// </para>
    /// <para>
    /// Sampling dimension D = 1 (one co-monotonic percentile drives every ordinate — the same
    /// restrictive-but-FDA-compatible scheme as the tabular types). The mean curve under
    /// uncertainty is the expected exceedance probability over 10,000 Weibull plotting-position
    /// percentile curves on a 200-point stratified hazard grid (the
    /// <see cref="TabularHazard"/> Hazard-mode assembly; the upstream
    /// <c>ExpectedProbabilities</c> reduction sums over a fixed chunk count, so the mean curve
    /// is bit-reproducible at any thread count).
    /// </para>
    /// </remarks>
    public class NonparametricHazard : UnivariateHazardBase
    {
        #region Construction

        /// <summary>
        /// Initializes a nonparametric hazard with the v1.0 defaults: a two-ordinate deterministic
        /// input curve {(0.999 → 1), (0.001 → 100)}, logarithmic hazard and Normal-Z probability
        /// transforms, uncertain, effective record length 100, extrapolation AEP 1e-4.
        /// </summary>
        public NonparametricHazard()
        {
            HookInputEvents();
            UpdateHazardFunction();
        }

        /// <summary>
        /// Restores a nonparametric hazard from its serialized form and re-derives the true
        /// uncertain function from the restored inputs (see the class remarks — serialization is
        /// inputs-only by design).
        /// </summary>
        /// <param name="xElement">The serialized form produced by <see cref="ToXElement"/>.</param>
        /// <exception cref="ArgumentNullException">Thrown when the element is null.</exception>
        public NonparametricHazard(XElement xElement)
        {
            if (xElement == null) throw new ArgumentNullException(nameof(xElement));

            ReadIdentityAttributes(xElement);
            SpecifiedHazard = SerializationUtilities.ReadString(xElement, nameof(SpecifiedHazard));
            HazardUnit = SerializationUtilities.ReadString(xElement, nameof(HazardUnit));
            _hazardTransform = SerializationUtilities.ReadEnum(xElement, nameof(HazardTransform), Transform.Logarithmic);
            _probabilityTransform = SerializationUtilities.ReadEnum(xElement, nameof(ProbabilityTransform), Transform.NormalZ);
            _isUncertain = SerializationUtilities.ReadBoolean(xElement, nameof(IsUncertain), true);
            _effectiveRecordLength = SerializationUtilities.ReadInt32(xElement, nameof(EffectiveRecordLength), 100);
            _extrapolationEP = SerializationUtilities.ReadDouble(xElement, nameof(ExtrapolationEP), 0.0001d);
            _extrapolation = SerializationUtilities.ReadEnum(xElement, nameof(Extrapolation), ExtrapolationPolicy.None);

            var tableElement = xElement.Element(nameof(InputUncertainFunction))?.Element("UncertainOrderedPairedData");
            if (tableElement != null)
            {
                var table = new UncertainOrderedPairedData(tableElement)
                {
                    // Re-impose the input table's ordering contract after the permissive parse.
                    OrderX = SortOrder.Descending,
                    OrderY = SortOrder.Ascending,
                    StrictX = true,
                    StrictY = true,
                };
                table.Validate();
                _inputUncertainFunction = table;
            }

            HookInputEvents();
            UpdateHazardFunction();
        }

        #endregion

        #region Members

        /// <summary>
        /// Backing field for <see cref="HazardTransform"/> — logarithmic by default (v1.0).
        /// </summary>
        private Transform _hazardTransform = Transform.Logarithmic;

        /// <summary>
        /// Backing field for <see cref="ProbabilityTransform"/> — Normal-Z by default (v1.0).
        /// </summary>
        private Transform _probabilityTransform = Transform.NormalZ;

        /// <summary>
        /// Backing field for <see cref="IsUncertain"/> — the v1.0 default.
        /// </summary>
        private bool _isUncertain = true;

        /// <summary>
        /// Backing field for <see cref="EffectiveRecordLength"/> — the v1.0 default.
        /// </summary>
        private int _effectiveRecordLength = 100;

        /// <summary>
        /// Backing field for <see cref="ExtrapolationEP"/> — the v1.0 default.
        /// </summary>
        private double _extrapolationEP = 0.0001d;

        /// <summary>
        /// Backing field for <see cref="Extrapolation"/>.
        /// </summary>
        private ExtrapolationPolicy _extrapolation = ExtrapolationPolicy.None;

        /// <summary>
        /// The minimum percentile probed for full-uncertainty hazard bounds (v1.0 constant).
        /// </summary>
        private readonly double _minPercentile = 0.00001d;

        /// <summary>
        /// Backing field for <see cref="InputUncertainFunction"/> — the v1.0 default two-ordinate
        /// deterministic AEP vs. hazard curve.
        /// </summary>
        private UncertainOrderedPairedData _inputUncertainFunction = new UncertainOrderedPairedData(
            new[] { new UncertainOrdinate(0.999d, new Deterministic(1d)), new UncertainOrdinate(0.001d, new Deterministic(100d)) },
            true, SortOrder.Descending, true, SortOrder.Ascending, UnivariateDistributionType.Deterministic);

        /// <summary>
        /// Backing field for <see cref="TrueUncertainFunction"/> — derived; empty until the first
        /// derivation runs.
        /// </summary>
        private UncertainOrderedPairedData _trueUncertainFunction = new UncertainOrderedPairedData(
            true, SortOrder.Descending, true, SortOrder.Ascending, UnivariateDistributionType.LnNormal);

        /// <summary>
        /// The interpolation transform applied to the hazard axis. Logarithmic derives the
        /// per-ordinate uncertainty in log space (the base-e log-normal moment mapping).
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
                    UpdateHazardFunction();
                }
            }
        }

        /// <summary>
        /// The interpolation transform applied to the exceedance-probability axis; drives the
        /// extrapolation and the empirical density behind the quantile standard errors.
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
                    UpdateHazardFunction();
                }
            }
        }

        /// <summary>
        /// Whether the hazard function carries knowledge uncertainty. When false, the derived
        /// function is the deterministic extension of the input curve.
        /// </summary>
        public bool IsUncertain
        {
            get { return _isUncertain; }
            set
            {
                if (_isUncertain != value)
                {
                    _isUncertain = value;
                    RaisePropertyChange(nameof(IsUncertain));
                    UpdateHazardFunction();
                }
            }
        }

        /// <summary>
        /// The effective record length N (years of systematic record the curve is worth), in
        /// [10, 10,000]; the quantile standard errors scale as 1/√N.
        /// </summary>
        public int EffectiveRecordLength
        {
            get { return _effectiveRecordLength; }
            set
            {
                if (_effectiveRecordLength != value)
                {
                    _effectiveRecordLength = value;
                    RaisePropertyChange(nameof(EffectiveRecordLength));
                    UpdateHazardFunction();
                }
            }
        }

        /// <summary>
        /// The annual exceedance probability the rare tail is extrapolated to, in [1e-8, 0.01].
        /// </summary>
        public double ExtrapolationEP
        {
            get { return _extrapolationEP; }
            set
            {
                if (_extrapolationEP != value)
                {
                    _extrapolationEP = value;
                    RaisePropertyChange(nameof(ExtrapolationEP));
                    UpdateHazardFunction();
                }
            }
        }

        /// <summary>
        /// The extrapolation policy applied when the sampled hazard curves are evaluated outside
        /// the derived table's range. Default = <see cref="ExtrapolationPolicy.None"/> (the v1.0
        /// endpoint hold, bit-identical); a non-default policy is compute-relevant hashed content.
        /// </summary>
        /// <remarks>
        /// This function carries two distinct extrapolation mechanisms. The serialized
        /// <see cref="ExtrapolationEP"/> is a derivation step: it extends the input curve to the
        /// declared rare tail before the per-ordinate uncertainty is derived, and every model
        /// already has it. This policy is a sampling behavior: it governs how the sampled
        /// (monotonicity-repaired) curves respond when evaluated beyond the derived table —
        /// which is why, unlike every other compute-relevant scalar on this type, its setter
        /// does not re-derive the table.
        /// </remarks>
        public ExtrapolationPolicy Extrapolation
        {
            get { return _extrapolation; }
            set
            {
                if (_extrapolation != value)
                {
                    // Sampling-only: the derived table does not depend on the policy, so no
                    // UpdateHazardFunction() here (the deliberate asymmetry — see the remarks).
                    _extrapolation = value;
                    RaisePropertyChange(nameof(Extrapolation));
                }
            }
        }

        /// <summary>
        /// The user-entered curve: strictly descending annual-exceedance-probability X with a
        /// hazard distribution per ordinate (typically deterministic — the derivation supplies
        /// the uncertainty). Assigning a new table re-derives; in-place ordinate edits re-derive
        /// through the table's collection-change notification.
        /// </summary>
        public UncertainOrderedPairedData InputUncertainFunction
        {
            get { return _inputUncertainFunction; }
            set
            {
                if (!ReferenceEquals(_inputUncertainFunction, value) && value is not null)
                {
                    _inputUncertainFunction.CollectionChanged -= OnInputCollectionChanged;
                    _inputUncertainFunction = value;
                    HookInputEvents();
                    RaisePropertyChange(nameof(InputUncertainFunction));
                    UpdateHazardFunction();
                }
            }
        }

        /// <summary>
        /// The derived curve the function samples from: the extended input curve with a
        /// <see cref="LnNormal"/> (or deterministic) hazard distribution per ordinate. Read-only —
        /// recomputed from <see cref="InputUncertainFunction"/> and the scalar options on every
        /// compute-relevant edit and on load; empty while the inputs are unusable.
        /// </summary>
        public UncertainOrderedPairedData TrueUncertainFunction
        {
            get { return _trueUncertainFunction; }
        }

        /// <inheritdoc/>
        public override HazardFunctionType FunctionType => HazardFunctionType.Nonparametric;

        /// <inheritdoc/>
        public override bool IsDeterministic => !IsUncertain;

        /// <inheritdoc/>
        public override int SamplingDimensions => 1;

        #endregion

        #region IRiskFunction Methods

        /// <inheritdoc/>
        /// <remarks>
        /// Errors (invalidating, the exact v1.0 NPHF rule set): missing axis labels; fewer than
        /// two input ordinates; invalid ordinates; input probabilities outside [0, 1]; an
        /// effective record length outside [10, 10,000]; an extrapolation exceedance probability
        /// outside [1e-8, 0.01]; a logarithmic hazard axis over hazard values below zero (probed
        /// across each ordinate's lower/mean/upper range); a logarithmic probability axis over
        /// negative probabilities.
        /// </remarks>
        public override (bool IsValid, List<string> ValidationMessages) Validate()
        {
            var messages = new List<string>();

            if (string.IsNullOrEmpty(SpecifiedHazard))
                messages.Add("Error: The nonparametric hazard function does not have a specified hazard type.");
            if (string.IsNullOrEmpty(HazardUnit))
                messages.Add("Error: The nonparametric hazard function does not have a specified hazard unit.");

            if (_inputUncertainFunction is null || _inputUncertainFunction.Count < 2)
            {
                messages.Add("Error: The hazard function must have at least two ordinates.");
            }
            else if (!_inputUncertainFunction.IsValid)
            {
                messages.Add("Error: Invalid hazard function ordinates.");
            }
            else
            {
                foreach (var ordinate in _inputUncertainFunction)
                {
                    if (ordinate.X < 0d)
                    {
                        messages.Add("Error: Probabilities must be greater than or equal to 0.");
                        break;
                    }
                    if (ordinate.X > 1d)
                    {
                        messages.Add("Error: Probabilities must be less than or equal to 1.");
                        break;
                    }
                }

                ValidateHazardTransform(messages);
                ValidateProbabilityTransform(messages);
            }

            if (EffectiveRecordLength < 10 || EffectiveRecordLength > 10000)
                messages.Add("Error: The effective record length must be between 10 and 10,000.");
            if (double.IsNaN(ExtrapolationEP) || ExtrapolationEP < 0.00000001d || ExtrapolationEP > 0.01d)
                messages.Add("Error: The extrapolation exceedance probability must be between 0.01 and 1E-8.");

            return (messages.FindIndex(m => m.StartsWith("Error:", StringComparison.Ordinal)) < 0, messages);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The mean curve, exactly as in v1.0: under uncertainty, the expected exceedance
        /// probability over 10,000 Weibull plotting-position percentile curves on a 200-point
        /// stratified hazard grid spanning the full-uncertainty hazard bounds (the
        /// <see cref="TabularHazard"/> Hazard-mode assembly, with N =
        /// <see cref="EffectiveRecordLength"/>); when deterministic, the derived curve inverted
        /// directly. Improved over v1.0: an unusable state throws instead of returning null.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown when the derived table is unusable.</exception>
        public override IUnivariateDistribution SampleFunction()
        {
            ThrowIfNotUsable();

            if (!IsUncertain)
            {
                var deterministic = _trueUncertainFunction.CurveSample().Invert();
                if (!deterministic.IsValid)
                    FunctionHelpers.ForceMonotonic(deterministic);
                return GuardSample(new EmpiricalDistribution(deterministic) { XTransform = HazardTransform, ProbabilityTransform = ProbabilityTransform, Extrapolation = ExtrapolationSupport.Map(_extrapolation) });
            }

            double min = MinHazard(false);
            double max = MaxHazard(false);
            if (min >= max)
                throw new InvalidOperationException("The nonparametric hazard span is degenerate: the minimum hazard is not below the maximum hazard.");

            // 200 stratified hazard quantiles across the full-uncertainty hazard span.
            var strat = Stratify.XValues(new StratificationOptions(min, max, 199, false), true);
            var quantiles = strat.Select(x => x.LowerBound).ToList();
            quantiles.Add(strat[strat.Count - 1].UpperBound);

            // Expected exceedance probability at each quantile over 10,000 plotting-position
            // percentile curves.
            const int realizations = 10000;
            var curves = new EmpiricalDistribution[realizations];
            double[] pp = PlottingPositions.Weibull(realizations);
            Parallel.For(0, realizations, idx => curves[idx] = SampleCore(pp[idx]));

            var boot = new BootstrapAnalysis(new EmpiricalDistribution(), ParameterEstimationMethod.MethodOfMoments, EffectiveRecordLength, realizations);
            double[] meanProbabilities = boot.ExpectedProbabilities(quantiles, curves);

            // Rebuild a strictly ordered exceedance table (v1.0 keeps ordinates that decrease the
            // exceedance probability by more than 1e-8).
            var meanFunction = new UncertainOrderedPairedData(true, SortOrder.Descending, true, SortOrder.Ascending, UnivariateDistributionType.Deterministic);
            for (int i = 0; i < meanProbabilities.Length; i++)
            {
                double exceedance = 1d - meanProbabilities[i];
                if (meanFunction.Count == 0 || meanFunction[meanFunction.Count - 1].X > exceedance + 0.00000001d)
                {
                    meanFunction.Add(new UncertainOrdinate(exceedance, new Deterministic(quantiles[i])));
                }
            }

            var opd = meanFunction.CurveSample().Invert();
            if (!opd.IsValid)
                FunctionHelpers.ForceMonotonic(opd);
            return GuardSample(new EmpiricalDistribution(opd) { XTransform = HazardTransform, ProbabilityTransform = ProbabilityTransform, Extrapolation = ExtrapolationSupport.Map(_extrapolation) });
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The co-monotonic percentile curve: every derived ordinate is evaluated at the same
        /// percentile, the curve is inverted to hazard vs. exceedance, and monotonicity is
        /// repaired when a sampled curve violates it (the exact v1.0 behavior).
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown when the derived table is unusable.</exception>
        public override IUnivariateDistribution SampleFunction(double percentile)
        {
            ThrowIfNotUsable();
            return GuardSample(SampleCore(percentile));
        }

        /// <summary>
        /// The percentile-curve construction shared by the public sampling members and the
        /// internal mean-curve builder. The builder consumes the raw product directly: the mean
        /// derivation probes percentile curves across the derived span, which is internal
        /// construction rather than chain evaluation, so the Error-mode guard applies only to the
        /// published products.
        /// </summary>
        /// <param name="percentile">The knowledge-uncertainty percentile.</param>
        /// <returns>The sampled (monotonicity-repaired) hazard curve.</returns>
        private EmpiricalDistribution SampleCore(double percentile)
        {
            var opd = _trueUncertainFunction.CurveSample(percentile).Invert();
            if (!opd.IsValid)
                FunctionHelpers.ForceMonotonic(opd);
            return new EmpiricalDistribution(opd) { XTransform = HazardTransform, ProbabilityTransform = ProbabilityTransform, Extrapolation = ExtrapolationSupport.Map(_extrapolation) };
        }

        /// <summary>
        /// Wraps a sampled product in the Error-mode range guard when configured; every other
        /// policy returns the raw wrapper (the unchanged code path).
        /// </summary>
        /// <param name="sampled">The sampled distribution product.</param>
        /// <returns>The product, guarded when the policy is Error.</returns>
        private IUnivariateDistribution GuardSample(EmpiricalDistribution sampled)
        {
            if (_extrapolation != ExtrapolationPolicy.Error) return sampled;
            return new RangeGuardedUnivariateDistribution(sampled, Name, $"{SpecifiedHazard} ({HazardUnit})");
        }

        /// <inheritdoc/>
        public override IUnivariateDistribution SampleFunction(int realizationIndex)
        {
            return SampleFunction(Percentile(realizationIndex, 0));
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The first derived ordinate's mean hazard, or its 0.00001 percentile under full
        /// uncertainty (the v1.0 probes). Empty derived tables report
        /// <see cref="double.MaxValue"/>.
        /// </remarks>
        public override double MinHazard(bool meanOnly)
        {
            return _trueUncertainFunction is null || _trueUncertainFunction.Count == 0
                ? double.MaxValue
                : meanOnly ? _trueUncertainFunction[0].GetOrdinate().Y : _trueUncertainFunction[0].GetOrdinate(_minPercentile).Y;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The last derived ordinate's mean hazard, or its 1 − 0.00001 percentile under full
        /// uncertainty (the v1.0 probes). Empty derived tables report
        /// <see cref="double.MinValue"/>.
        /// </remarks>
        public override double MaxHazard(bool meanOnly)
        {
            return _trueUncertainFunction is null || _trueUncertainFunction.Count == 0
                ? double.MinValue
                : meanOnly ? _trueUncertainFunction[_trueUncertainFunction.Count - 1].GetOrdinate().Y : _trueUncertainFunction[_trueUncertainFunction.Count - 1].GetOrdinate(1d - _minPercentile).Y;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Exact percentile evaluation over the DERIVED table — no simulation. Curves are
        /// index-aligned with <see cref="TrueUncertainFunction"/> ordinates (hazard-value curves
        /// per extended exceedance-probability ordinate).
        /// </remarks>
        public override UncertaintyAnalysisResults? ComputeUncertaintyResults(double confidenceIntervalWidth = 0.9)
        {
            return TabularUncertainty.FromCoMonotonicTable(TrueUncertainFunction, confidenceIntervalWidth);
        }

        #endregion

        #region Serialization

        /// <inheritdoc/>
        /// <remarks>
        /// Inputs-only by design (see the class remarks): the derived
        /// <see cref="TrueUncertainFunction"/> is never written — it is recomputed from these
        /// inputs on load, so the canonical-hash identity surface is exactly the user-specified
        /// content.
        /// </remarks>
        public override XElement ToXElement()
        {
            var element = new XElement(nameof(NonparametricHazard));
            WriteIdentityAttributes(element);
            element.SetAttributeValue(nameof(SpecifiedHazard), SpecifiedHazard);
            element.SetAttributeValue(nameof(HazardUnit), HazardUnit);
            element.SetAttributeValue(nameof(HazardTransform), HazardTransform.ToString());
            element.SetAttributeValue(nameof(ProbabilityTransform), ProbabilityTransform.ToString());
            element.SetAttributeValue(nameof(IsUncertain), IsUncertain.ToString());
            element.SetAttributeValue(nameof(EffectiveRecordLength), EffectiveRecordLength.ToString(CultureInfo.InvariantCulture));
            element.SetAttributeValue(nameof(ExtrapolationEP), SerializationUtilities.FormatDouble(ExtrapolationEP));
            // Conditional presence: the attribute is written only when non-default, so every
            // pre-existing serialized form — and its canonical hash and seeds — is unchanged.
            if (_extrapolation != ExtrapolationPolicy.None)
            {
                element.SetAttributeValue(nameof(Extrapolation), _extrapolation.ToString());
            }
            element.Add(new XElement(nameof(InputUncertainFunction), InputUncertainFunction.SaveToXElement()));
            return element;
        }

        #endregion

        #region Private Helpers

        /// <summary>
        /// Subscribes the derivation to the input table's collection-change notification (the
        /// v1.0 behavior: in-place ordinate edits re-derive immediately).
        /// </summary>
        private void HookInputEvents()
        {
            _inputUncertainFunction.CollectionChanged += OnInputCollectionChanged;
        }

        /// <summary>
        /// Re-derives the true uncertain function when the input table changes in place.
        /// </summary>
        /// <param name="sender">The input table.</param>
        /// <param name="e">The collection-change details (unused; any change re-derives).</param>
        private void OnInputCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            UpdateHazardFunction();
        }

        /// <summary>
        /// Determines whether the derivation inputs are usable (the v1.0 gate): at least two
        /// valid input ordinates with probabilities inside [0, 1], a record length inside
        /// [10, 10,000], an extrapolation probability inside [1e-8, 0.01], and — under a
        /// logarithmic hazard axis — a non-negative hazard range (a negative mean would put NaN
        /// through the log-space derivation; v1.0 gated derivation on the same transform check).
        /// </summary>
        /// <returns>True when the derivation can run.</returns>
        private bool AreInputsUsable()
        {
            if (_inputUncertainFunction is null || _inputUncertainFunction.Count < 2 || !_inputUncertainFunction.IsValid) return false;
            foreach (var ordinate in _inputUncertainFunction)
            {
                if (ordinate.X < 0d || ordinate.X > 1d) return false;
            }
            if (EffectiveRecordLength < 10 || EffectiveRecordLength > 10000) return false;
            if (double.IsNaN(ExtrapolationEP) || ExtrapolationEP < 0.00000001d || ExtrapolationEP > 0.01d) return false;
            if (HazardTransform == Transform.Logarithmic && HasNegativeHazardRange()) return false;
            return true;
        }

        /// <summary>
        /// Detects a negative hazard range across the input ordinates' lower, mean, and upper
        /// values (shared by the logarithmic-axis validation and the derivation gate; the
        /// ±1e-5-percentile probes guard unbounded distributions).
        /// </summary>
        /// <returns>True when any ordinate's hazard range dips below zero.</returns>
        private bool HasNegativeHazardRange()
        {
            for (int i = 0; i < _inputUncertainFunction.Count; i++)
            {
                var y = _inputUncertainFunction[i].Y!;
                if (double.IsPositiveInfinity(y.Maximum))
                {
                    if (y.InverseCDF(1d - _minPercentile) < 0.0d) return true;
                }
                else if (y.Maximum < 0.0d)
                {
                    return true;
                }

                if (y.Mean < 0.0d) return true;

                if (double.IsNegativeInfinity(y.Minimum))
                {
                    if (y.InverseCDF(_minPercentile) < 0.0d) return true;
                }
                else if (y.Minimum < 0.0d)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Throws when the function cannot be sampled — v1.0's function-valid gate (INPUT
        /// validity), upgraded from a silent null return. Deliberately NOT a check of the derived
        /// table's own strict-dominance validity flag: the σ repair enforces the 1% bound only,
        /// so a legitimately derived LnNormal ladder can still cross deep in the tails when the
        /// spread jumps between ordinates (v1.0 never consulted the derived flag either — sampled
        /// curves that cross are repaired by <c>ForceMonotonic</c>, exactly as in v1.0).
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when the inputs are unusable.</exception>
        private void ThrowIfNotUsable()
        {
            if (!AreInputsUsable() || _trueUncertainFunction is null || _trueUncertainFunction.Count < 2)
                throw new InvalidOperationException("The nonparametric hazard function is invalid. Call Validate() and correct the reported errors before sampling.");
        }

        /// <summary>
        /// Derives <see cref="TrueUncertainFunction"/> from the input curve and the scalar
        /// options — the quantile-uncertainty engine described in the class remarks. Deterministic
        /// (no PRNG, no parallelism), so load-time recomputation is bit-stable.
        /// </summary>
        private void UpdateHazardFunction()
        {
            if (IsUncertain)
            {
                _trueUncertainFunction = new UncertainOrderedPairedData(true, SortOrder.Descending, true, SortOrder.Ascending, UnivariateDistributionType.LnNormal);
                if (!AreInputsUsable())
                {
                    RaisePropertyChange(nameof(TrueUncertainFunction));
                    return;
                }

                bool logHazard = HazardTransform == Transform.Logarithmic;
                int inputCount = _inputUncertainFunction.Count;
                var xVals = new List<double>(inputCount + 2);
                var pVals = new List<double>(inputCount + 2);
                for (int i = 0; i < inputCount; i++)
                {
                    xVals.Add(logHazard ? Tools.Log(_inputUncertainFunction[i].Y!.Mean) : _inputUncertainFunction[i].Y!.Mean);
                    pVals.Add(_inputUncertainFunction[i].X);
                }

                var lin = new Linear(pVals.ToArray(), xVals.ToArray(), SortOrder.Descending) { XTransform = ProbabilityTransform };
                ExtendCurve(lin, pVals, xVals);

                // Order-statistic quantile standard errors off the empirical density of the
                // (log-)mean curve, pinned beyond p ∈ [0.01, 0.99] (the v1.0 form).
                var opd = new OrderedPairedData(xVals, pVals, true, SortOrder.Ascending, true, SortOrder.Descending);
                var empDist = new EmpiricalDistribution(opd) { ProbabilityTransform = ProbabilityTransform };
                int n = EffectiveRecordLength;
                double se99 = Math.Sqrt((1d - 0.99d) * 0.99d / (n * Tools.Sqr(empDist.PDF(lin.Interpolate(0.99d)))));
                double se01 = Math.Sqrt((1d - 0.01d) * 0.01d / (n * Tools.Sqr(empDist.PDF(lin.Interpolate(0.01d)))));

                var se = new List<double>(xVals.Count);
                for (int i = 0; i < xVals.Count; i++)
                {
                    if (pVals[i] >= 0.99d)
                    {
                        se.Add(se99);
                    }
                    else if (pVals[i] <= 0.01d)
                    {
                        se.Add(se01);
                    }
                    else
                    {
                        se.Add(Math.Sqrt((1d - pVals[i]) * pVals[i] / (n * Tools.Sqr(empDist.PDF(xVals[i])))));
                    }
                    // Forward monotone smoothing: the SE never shrinks toward the rare tail.
                    if (i > 0 && pVals[i] < 0.5d && se[i] < se[i - 1]) se[i] = se[i - 1];
                }
                // Backward monotone smoothing: the SE never shrinks toward the frequent tail.
                for (int i = xVals.Count - 2; i >= 0; i--)
                {
                    if (pVals[i] > 0.5d && se[i] < se[i + 1]) se[i] = se[i + 1];
                }

                // The z the LnNormal inverse CDF evaluates at p = 0.01 — used by the closed-form
                // σ repair so the repaired bound reproduces the distribution's own quantile.
                double zLow = -Math.Sqrt(2d) * Erf.InverseErfc(2d * 0.01d);
                double previousLow = double.NaN;
                for (int i = 0; i < xVals.Count; i++)
                {
                    double mean;
                    double sd;
                    if (logHazard)
                    {
                        // Base-e log-normal moment mapping (log-space location/scale → real
                        // moments), inlined: mean = exp(μ + σ²/2), var = exp(2μ + σ²)(exp(σ²) − 1).
                        double s2 = se[i] * se[i];
                        mean = Math.Exp(xVals[i] + 0.5d * s2);
                        sd = Math.Sqrt(Math.Exp(2d * xVals[i] + s2) * (Math.Exp(s2) - 1d));
                    }
                    else
                    {
                        mean = xVals[i];
                        sd = se[i];
                    }
                    sd = SanitizeSigma(sd);

                    if (i > 0)
                    {
                        double currentLow = LnNormalLowerQuantile(mean, sd, zLow);
                        if (currentLow < previousLow)
                        {
                            sd = SanitizeSigma(SolveSigmaForLowerQuantile(mean, sd, previousLow, zLow));
                        }
                    }

                    _trueUncertainFunction.Add(new UncertainOrdinate(pVals[i], new LnNormal(mean, sd)));
                    previousLow = LnNormalLowerQuantile(mean, sd, zLow);
                }
            }
            else
            {
                _trueUncertainFunction = new UncertainOrderedPairedData(true, SortOrder.Descending, true, SortOrder.Ascending, UnivariateDistributionType.Deterministic);
                if (!AreInputsUsable())
                {
                    RaisePropertyChange(nameof(TrueUncertainFunction));
                    return;
                }

                int inputCount = _inputUncertainFunction.Count;
                var xVals = new List<double>(inputCount + 2);
                var pVals = new List<double>(inputCount + 2);
                for (int i = 0; i < inputCount; i++)
                {
                    xVals.Add(_inputUncertainFunction[i].Y!.Mean);
                    pVals.Add(_inputUncertainFunction[i].X);
                }

                var lin = new Linear(pVals.ToArray(), xVals.ToArray(), SortOrder.Descending) { XTransform = ProbabilityTransform, YTransform = HazardTransform };
                ExtendCurve(lin, pVals, xVals);

                for (int i = 0; i < xVals.Count; i++)
                {
                    _trueUncertainFunction.Add(new UncertainOrdinate(pVals[i], new Deterministic(xVals[i])));
                }
            }

            RaisePropertyChange(nameof(TrueUncertainFunction));
        }

        /// <summary>
        /// Extends the curve by linear extrapolation to <see cref="ExtrapolationEP"/> at the rare
        /// end and to AEP 0.999 at the frequent end. Both extrapolations are evaluated BEFORE any
        /// list mutation: the interpolator holds references to its source arrays, and v1.0
        /// inserted the frequent-end probability first, corrupting its own extrapolation into a
        /// flat extension (see the class remarks; inert for inputs anchored at 0.999).
        /// </summary>
        /// <param name="lin">The interpolator over the pre-extension curve (snapshot arrays).</param>
        /// <param name="pVals">The exceedance probabilities, descending; extended in place.</param>
        /// <param name="xVals">The (log-)hazard values, ascending; extended in place.</param>
        private void ExtendCurve(Linear lin, List<double> pVals, List<double> xVals)
        {
            bool extendRare = pVals[pVals.Count - 1] > ExtrapolationEP;
            bool extendFrequent = pVals[0] < 0.999d;
            double rareValue = extendRare ? lin.Extrapolate(ExtrapolationEP) : 0d;
            double frequentValue = extendFrequent ? lin.Extrapolate(0.999d) : 0d;

            if (extendRare)
            {
                pVals.Add(ExtrapolationEP);
                xVals.Add(rareValue);
            }
            if (extendFrequent)
            {
                pVals.Insert(0, 0.999d);
                xVals.Insert(0, frequentValue);
            }
        }

        /// <summary>
        /// The 1% (lower-bound) quantile of a real-moment LnNormal, inlined: the direct method of
        /// moments maps (m, s) to log-space (μ, σ) — σ = √ln(1 + s²/m²) floored at machine
        /// epsilon, μ = ln(m²/√(s² + m²)) — and the quantile is exp(μ + z·σ), exactly the
        /// expressions <see cref="LnNormal"/> evaluates.
        /// </summary>
        /// <param name="mean">The real-space mean m.</param>
        /// <param name="sd">The real-space standard deviation s.</param>
        /// <param name="z">The standard-normal quantile at the bound probability.</param>
        /// <returns>The quantile, or NaN when the moments are unusable.</returns>
        private static double LnNormalLowerQuantile(double mean, double sd, double z)
        {
            double variance = sd * sd;
            double m2 = mean * mean;
            double sigmaLn = Math.Sqrt(Math.Log(1d + variance / m2));
            if (sigmaLn < 1E-16 && Math.Sign(sigmaLn) != -1) sigmaLn = Tools.DoubleMachineEpsilon;
            double muLn = Math.Log(m2 / Math.Sqrt(variance + m2));
            return Math.Exp(muLn + z * sigmaLn);
        }

        /// <summary>
        /// Solves for the real-space standard deviation whose LnNormal 1% quantile equals the
        /// target bound — the closed form replacing the v1.0 per-ordinate Brent root find: with
        /// v = σ_ln, the bound condition <c>ln q = ln m − v²/2 + z·v</c> is the quadratic
        /// <c>v²/2 − z·v + (ln q − ln m) = 0</c>, whose positive root is
        /// <c>v = z + √(z² − 2(ln q − ln m))</c>, and s = m·√(exp(v²) − 1). Exact to machine
        /// precision where v1.0 iterated to Brent tolerance. Degenerate configurations (a
        /// non-positive mean or target, a negative discriminant, or an overflowing v) fall back
        /// to the exact legacy Brent call.
        /// </summary>
        /// <param name="mean">The real-space mean m of the ordinate.</param>
        /// <param name="currentSd">The unrepaired standard deviation (the legacy Brent upper bracket).</param>
        /// <param name="targetLow">The previous ordinate's 1% quantile q the bound must not fall below.</param>
        /// <param name="z">The standard-normal quantile at the bound probability (negative).</param>
        /// <returns>The repaired standard deviation.</returns>
        private static double SolveSigmaForLowerQuantile(double mean, double currentSd, double targetLow, double z)
        {
            if (mean > 0d && targetLow > 0d)
            {
                double c = Math.Log(targetLow) - Math.Log(mean);
                double discriminant = z * z - 2d * c;
                if (discriminant > 0d)
                {
                    double v = z + Math.Sqrt(discriminant);
                    if (v > 0d && Tools.IsFinite(v))
                    {
                        double sd = mean * Math.Sqrt(Math.Exp(v * v) - 1d);
                        if (Tools.IsFinite(sd) && sd > 0d) return sd;
                    }
                }
            }

            // The exact legacy fallback: bracketed Brent over [ε, current σ].
            return Brent.Solve(x => targetLow - new LnNormal(mean, x).InverseCDF(0.01d), Tools.DoubleMachineEpsilon, currentSd);
        }

        /// <summary>
        /// Applies the v1.0 σ floor: NaN and sub-floor values become 1e-16.
        /// </summary>
        /// <param name="sigma">The candidate standard deviation.</param>
        /// <returns>The floored standard deviation.</returns>
        private static double SanitizeSigma(double sigma)
        {
            if (double.IsNaN(sigma)) return 0.0000000000000001d;
            return Math.Max(0.0000000000000001d, sigma);
        }

        /// <summary>
        /// Validates the logarithmic hazard axis across each input ordinate's lower, mean, and
        /// upper range (the v1.0 check; the range probe is shared with the derivation gate).
        /// </summary>
        /// <param name="messages">The message sink.</param>
        private void ValidateHazardTransform(List<string> messages)
        {
            if (HazardTransform != Transform.Logarithmic) return;

            if (HasNegativeHazardRange())
            {
                messages.Add("Error: The hazard interpolation transform cannot be logarithmic. There are hazard values less than zero.");
            }
        }

        /// <summary>
        /// Validates the logarithmic probability axis against negative probabilities (the v1.0
        /// check: the exceedance probabilities are the input X ordinates).
        /// </summary>
        /// <param name="messages">The message sink.</param>
        private void ValidateProbabilityTransform(List<string> messages)
        {
            if (ProbabilityTransform != Transform.Logarithmic) return;

            if (_inputUncertainFunction.Count > 0
                && (_inputUncertainFunction[0].X < 0.0d || _inputUncertainFunction[_inputUncertainFunction.Count - 1].X < 0.0d))
            {
                messages.Add("Error: The probability interpolation transform cannot be logarithmic. There are inputs that will produce probability values less than zero.");
            }
        }

        #endregion
    }
}
