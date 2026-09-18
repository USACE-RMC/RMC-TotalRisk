using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Numerics;
using Numerics.Data;
using Numerics.Data.Statistics;
using Numerics.Distributions;
using Numerics.Sampling;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.Core.Support;

namespace RMC.TotalRisk.RiskFunctions.Hazards
{
    /// <summary>
    /// A tabular hazard (frequency) function: an exceedance-probability vs. hazard table with one
    /// of three knowledge-uncertainty modes — none (deterministic), uncertainty around the hazard
    /// axis, or uncertainty around the probability axis.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Ported from v1.0 <c>TabularHazard</c> with the domain surface preserved. The three tables
    /// (<see cref="NoUncertaintyFunction"/> — spelling corrected from the v1.0
    /// <c>NoUncertainyFunction</c> typo, <see cref="HazardUncertainFunction"/>,
    /// <see cref="ProbabilityUncertainFunction"/>) carry the mode-specific data;
    /// <see cref="TargetFunction"/> exposes the active one per <see cref="UncertaintyValue"/>.
    /// Uncertainty is sampled co-monotonically (one percentile drives every ordinate), and sampled
    /// curves that violate monotonicity are repaired with
    /// <see cref="FunctionHelpers.ForceMonotonic(OrderedPairedData)"/>.
    /// </para>
    /// <para>
    /// The mean curve (<see cref="SampleFunction()"/>) is mode-dependent, exactly as in v1.0: the
    /// None mode inverts the deterministic table; the Hazard mode assembles the expected
    /// exceedance-probability curve over 200 stratified hazard quantiles from 10,000
    /// plotting-position percentile curves (<see cref="BootstrapAnalysis.ExpectedProbabilities(IList{double}, IUnivariateDistribution[])"/>);
    /// the Probability mode uses the mean ordinates directly, with a 10,000-curve expected-probability
    /// rebuild for the PERT-percentile-Z distribution whose ordinate mean is not analytic.
    /// </para>
    /// <para>
    /// Sampling dimension D = 1 (one co-monotonic percentile per realization). Improved over v1.0:
    /// invalid-state sampling throws instead of silently returning null.
    /// </para>
    /// </remarks>
    public class TabularHazard : UnivariateHazardBase
    {
        #region Construction

        /// <summary>
        /// Initializes a tabular hazard with the v1.0 default tables (a two-ordinate deterministic
        /// curve, and two-ordinate PERT uncertain variants for each uncertainty mode).
        /// </summary>
        public TabularHazard()
        {
        }

        /// <summary>
        /// Restores a tabular hazard from its serialized form.
        /// </summary>
        /// <param name="xElement">The serialized form produced by <see cref="ToXElement"/>.</param>
        /// <exception cref="ArgumentNullException">Thrown when the element is null.</exception>
        public TabularHazard(XElement xElement)
        {
            if (xElement == null) throw new ArgumentNullException(nameof(xElement));

            ReadIdentityAttributes(xElement);
            SpecifiedHazard = SerializationUtilities.ReadString(xElement, nameof(SpecifiedHazard));
            HazardUnit = SerializationUtilities.ReadString(xElement, nameof(HazardUnit));
            _hazardTransform = SerializationUtilities.ReadEnum(xElement, nameof(HazardTransform), Transform.None);
            _probabilityTransform = SerializationUtilities.ReadEnum(xElement, nameof(ProbabilityTransform), Transform.NormalZ);
            _uncertaintyValue = SerializationUtilities.ReadEnum(xElement, nameof(UncertaintyValue), FunctionUncertainty.None);
            _extrapolation = SerializationUtilities.ReadEnum(xElement, nameof(Extrapolation), ExtrapolationPolicy.None);

            var noUncertainty = ReadTable(xElement, nameof(NoUncertaintyFunction), SortOrder.Descending, SortOrder.Ascending);
            if (noUncertainty is not null) _noUncertaintyFunction = noUncertainty;
            var hazardUncertain = ReadTable(xElement, nameof(HazardUncertainFunction), SortOrder.Descending, SortOrder.Ascending);
            if (hazardUncertain is not null) _hazardUncertainFunction = hazardUncertain;
            var probabilityUncertain = ReadTable(xElement, nameof(ProbabilityUncertainFunction), SortOrder.Ascending, SortOrder.Descending);
            if (probabilityUncertain is not null) _probabilityUncertainFunction = probabilityUncertain;
        }

        #endregion

        #region Members

        /// <summary>
        /// Backing field for <see cref="HazardTransform"/>.
        /// </summary>
        private Transform _hazardTransform = Transform.None;

        /// <summary>
        /// Backing field for <see cref="ProbabilityTransform"/> — Normal-Z by default (v1.0).
        /// </summary>
        private Transform _probabilityTransform = Transform.NormalZ;

        /// <summary>
        /// Backing field for <see cref="UncertaintyValue"/>.
        /// </summary>
        private FunctionUncertainty _uncertaintyValue = FunctionUncertainty.None;

        /// <summary>
        /// Backing field for <see cref="Extrapolation"/>.
        /// </summary>
        private ExtrapolationPolicy _extrapolation = ExtrapolationPolicy.None;

        /// <summary>
        /// The minimum percentile probed for full-uncertainty hazard bounds (v1.0 constant).
        /// </summary>
        private readonly double _minPercentile = 0.00001d;

        /// <summary>
        /// Backing field for <see cref="NoUncertaintyFunction"/> — the v1.0 default deterministic
        /// exceedance-probability (descending X) vs. hazard (ascending Y) table.
        /// </summary>
        private UncertainOrderedPairedData _noUncertaintyFunction = new UncertainOrderedPairedData(
            new[] { new UncertainOrdinate(0.999d, new Deterministic(1d)), new UncertainOrdinate(0.001d, new Deterministic(100d)) },
            true, SortOrder.Descending, true, SortOrder.Ascending, UnivariateDistributionType.Deterministic);

        /// <summary>
        /// Backing field for <see cref="HazardUncertainFunction"/> — the v1.0 default PERT table
        /// (probability ordinates with uncertain hazard values).
        /// </summary>
        private UncertainOrderedPairedData _hazardUncertainFunction = new UncertainOrderedPairedData(
            new[] { new UncertainOrdinate(0.999d, new Pert(1d, 1d, 1d)), new UncertainOrdinate(0.001d, new Pert(90d, 100d, 110d)) },
            true, SortOrder.Descending, true, SortOrder.Ascending, UnivariateDistributionType.Pert);

        /// <summary>
        /// Backing field for <see cref="ProbabilityUncertainFunction"/> — the v1.0 default PERT
        /// table (hazard ordinates with uncertain exceedance probabilities).
        /// </summary>
        private UncertainOrderedPairedData _probabilityUncertainFunction = new UncertainOrderedPairedData(
            new[] { new UncertainOrdinate(1d, new Pert(0.999d, 0.999d, 0.999d)), new UncertainOrdinate(100d, new Pert(0.0001d, 0.0005d, 0.005d)) },
            true, SortOrder.Ascending, true, SortOrder.Descending, UnivariateDistributionType.Pert);

        /// <summary>
        /// The interpolation transform applied to the hazard axis.
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
        /// The interpolation transform applied to the exceedance-probability axis.
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
        /// The extrapolation policy applied when the sampled hazard curves are evaluated outside
        /// their table range. Default = <see cref="ExtrapolationPolicy.None"/> (the v1.0 endpoint
        /// hold, bit-identical); a non-default policy is compute-relevant hashed content.
        /// </summary>
        /// <remarks>
        /// An extending policy widens the sampled curves' evaluated domain: the inverse
        /// hazard-frequency lookups extend their tails, so the engine's integration domain and
        /// endpoint treatment widen deliberately, and each sampled curve extends its own
        /// (monotonicity-repaired) boundary segments. The mean curve's construction grid still
        /// derives from the declared table bounds while its values follow the extended percentile
        /// curves, and <see cref="MinHazard(bool)"/>/<see cref="MaxHazard(bool)"/> keep reporting
        /// the declared data bounds — an extension is unbounded and never widens the reported
        /// bounds. Under <see cref="ExtrapolationPolicy.Error"/>, forward (hazard-axis) queries
        /// outside a sampled curve's span throw loudly, while probability-axis inverse lookups
        /// retain the endpoint hold — the engine probes them to locate the sampled curve's own
        /// span, so an Error-mode hazard integrates over its table span exactly as under the
        /// default.
        /// </remarks>
        public ExtrapolationPolicy Extrapolation
        {
            get { return _extrapolation; }
            set
            {
                if (_extrapolation != value)
                {
                    _extrapolation = value;
                    RaisePropertyChange(nameof(Extrapolation));
                }
            }
        }

        /// <summary>
        /// The knowledge-uncertainty mode: which table drives the function.
        /// </summary>
        public FunctionUncertainty UncertaintyValue
        {
            get { return _uncertaintyValue; }
            set
            {
                if (_uncertaintyValue != value)
                {
                    _uncertaintyValue = value;
                    RaisePropertyChange(nameof(UncertaintyValue));
                }
            }
        }

        /// <summary>
        /// The deterministic table (mode <see cref="FunctionUncertainty.None"/>): descending
        /// exceedance-probability X, ascending deterministic hazard Y. Property name corrects the
        /// v1.0 <c>NoUncertainyFunction</c> typo.
        /// </summary>
        public UncertainOrderedPairedData NoUncertaintyFunction
        {
            get { return _noUncertaintyFunction; }
            set
            {
                if (!ReferenceEquals(_noUncertaintyFunction, value) && value is not null)
                {
                    _noUncertaintyFunction = value;
                    RaisePropertyChange(nameof(NoUncertaintyFunction));
                }
            }
        }

        /// <summary>
        /// The hazard-uncertainty table (mode <see cref="FunctionUncertainty.Hazard"/>): descending
        /// exceedance-probability X with a hazard distribution per ordinate.
        /// </summary>
        public UncertainOrderedPairedData HazardUncertainFunction
        {
            get { return _hazardUncertainFunction; }
            set
            {
                if (!ReferenceEquals(_hazardUncertainFunction, value) && value is not null)
                {
                    _hazardUncertainFunction = value;
                    RaisePropertyChange(nameof(HazardUncertainFunction));
                }
            }
        }

        /// <summary>
        /// The probability-uncertainty table (mode <see cref="FunctionUncertainty.Probability"/>):
        /// ascending hazard X with an exceedance-probability distribution per ordinate.
        /// </summary>
        public UncertainOrderedPairedData ProbabilityUncertainFunction
        {
            get { return _probabilityUncertainFunction; }
            set
            {
                if (!ReferenceEquals(_probabilityUncertainFunction, value) && value is not null)
                {
                    _probabilityUncertainFunction = value;
                    RaisePropertyChange(nameof(ProbabilityUncertainFunction));
                }
            }
        }

        /// <summary>
        /// The active table per <see cref="UncertaintyValue"/>.
        /// </summary>
        public UncertainOrderedPairedData TargetFunction
        {
            get
            {
                return _uncertaintyValue switch
                {
                    FunctionUncertainty.Hazard => _hazardUncertainFunction,
                    FunctionUncertainty.Probability => _probabilityUncertainFunction,
                    _ => _noUncertaintyFunction,
                };
            }
        }

        /// <inheritdoc/>
        public override HazardFunctionType FunctionType => HazardFunctionType.Tabular;

        /// <inheritdoc/>
        public override bool IsDeterministic => UncertaintyValue == FunctionUncertainty.None;

        /// <inheritdoc/>
        public override int SamplingDimensions => 1;

        #endregion

        #region IRiskFunction Methods

        /// <inheritdoc/>
        /// <remarks>
        /// Errors (invalidating, exact v1.0 conditions per mode): fewer than two ordinates in the
        /// active table; invalid ordinates; probability values outside [0, 1] (the ordinate X axis
        /// for the None/Hazard modes, the ordinate distributions for the Probability mode); missing
        /// axis labels; a logarithmic hazard axis over a negative hazard range; a logarithmic
        /// probability axis over a negative probability range. As in v1.0, validation coerces
        /// PertPercentile probability ordinates to the allowable [0, 1] range.
        /// </remarks>
        public override (bool IsValid, List<string> ValidationMessages) Validate()
        {
            var messages = new List<string>();

            if (string.IsNullOrEmpty(SpecifiedHazard))
                messages.Add("Error: The tabular hazard function does not have a specified hazard type.");
            if (string.IsNullOrEmpty(HazardUnit))
                messages.Add("Error: The tabular hazard function does not have a specified hazard unit.");

            // v1.0 coerces PertPercentile probability ordinates to [0, 1] during validation.
            if (_probabilityUncertainFunction.Distribution == UnivariateDistributionType.PertPercentile)
            {
                for (int i = 0; i < _probabilityUncertainFunction.Count; i++)
                {
                    ((PertPercentile)_probabilityUncertainFunction[i].Y!).MinAllowableValue = 0d;
                    ((PertPercentile)_probabilityUncertainFunction[i].Y!).MaxAllowableValue = 1d;
                }
            }

            var target = TargetFunction;
            if (target is null || target.Count < 2)
            {
                messages.Add("Error: The hazard function must have at least two ordinates.");
            }
            else if (!target.IsValid)
            {
                messages.Add("Error: Invalid hazard function ordinates.");
            }
            else if (UncertaintyValue == FunctionUncertainty.None || UncertaintyValue == FunctionUncertainty.Hazard)
            {
                foreach (var ordinate in target)
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
            }
            else
            {
                foreach (var ordinate in target)
                {
                    if (ordinate.Y!.Minimum < 0d)
                    {
                        messages.Add("Error: Probabilities must be greater than or equal to 0.");
                        break;
                    }
                    if (ordinate.Y.Maximum > 1d)
                    {
                        messages.Add("Error: Probabilities must be less than or equal to 1.");
                        break;
                    }
                }
            }

            ValidateHazardTransform(messages);
            ValidateProbabilityTransform(messages);

            return (messages.FindIndex(m => m.StartsWith("Error:", StringComparison.Ordinal)) < 0, messages);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The mean curve per mode, exactly as in v1.0 (see the class remarks). Improved over
        /// v1.0: an invalid state throws instead of silently returning null.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown when the active table is invalid.</exception>
        public override IUnivariateDistribution SampleFunction()
        {
            ThrowIfNotUsable();

            switch (_uncertaintyValue)
            {
                case FunctionUncertainty.Hazard:
                    {
                        double min = MinHazard(false);
                        double max = MaxHazard(false);
                        if (min >= max)
                            throw new InvalidOperationException("The tabular hazard span is degenerate: the minimum hazard is not below the maximum hazard.");

                        // 200 stratified hazard quantiles across the full-uncertainty hazard span.
                        var strat = Stratify.XValues(new StratificationOptions(min, max, 199, false), true);
                        var quantiles = strat.Select(x => x.LowerBound).ToList();
                        quantiles.Add(strat[strat.Count - 1].UpperBound);

                        // Expected exceedance probability at each quantile over 10,000
                        // plotting-position percentile curves.
                        const int realizations = 10000;
                        var curves = new EmpiricalDistribution[realizations];
                        double[] pp = PlottingPositions.Weibull(realizations);
                        Parallel.For(0, realizations, idx => curves[idx] = SampleCore(pp[idx]));

                        var boot = new BootstrapAnalysis(new EmpiricalDistribution(), ParameterEstimationMethod.MethodOfMoments, 100, realizations);
                        double[] meanProbabilities = boot.ExpectedProbabilities(quantiles, curves);

                        // Rebuild a strictly ordered exceedance table (v1.0 keeps ordinates that
                        // decrease the exceedance probability by more than 1e-8).
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

                case FunctionUncertainty.Probability:
                    {
                        var meanCurve = new EmpiricalDistribution(ProbabilityUncertainFunction.CurveSample()) { XTransform = HazardTransform, ProbabilityTransform = ProbabilityTransform, Extrapolation = ExtrapolationSupport.Map(_extrapolation) };
                        if (ProbabilityUncertainFunction.Distribution == UnivariateDistributionType.PertPercentileZ)
                        {
                            // The PERT-percentile-Z ordinate mean is not analytic: rebuild the
                            // expected probability per ordinate from 10,000 percentile curves.
                            const int realizations = 10000;
                            int count = meanCurve.ProbabilityValues.Count;
                            var curves = new EmpiricalDistribution[realizations];
                            double[] pp = PlottingPositions.Weibull(realizations);
                            var probabilities = new double[count, realizations];
                            var expected = new double[count];

                            Parallel.For(0, realizations, idx =>
                            {
                                curves[idx] = SampleCore(pp[idx]);
                                for (int i = 0; i < count; i++)
                                    probabilities[i, idx] = curves[idx].ProbabilityValues[i];
                            });

                            // Summed sequentially in realization order: a parallel reduction here
                            // partitions by core count and would make this mean curve — which feeds
                            // every realization of the analysis — vary between runs.
                            for (int i = 0; i < count; i++)
                            {
                                double sum = 0d;
                                for (int idx = 0; idx < realizations; idx++)
                                    sum += probabilities[i, idx];
                                expected[i] = sum / realizations;
                            }

                            var opd = new OrderedPairedData(meanCurve.XValues, expected, true, SortOrder.Ascending, true, SortOrder.Descending);
                            if (!opd.IsValid)
                                FunctionHelpers.ForceMonotonic(opd);
                            meanCurve = new EmpiricalDistribution(opd) { XTransform = HazardTransform, ProbabilityTransform = ProbabilityTransform, Extrapolation = ExtrapolationSupport.Map(_extrapolation) };
                        }
                        return GuardSample(meanCurve);
                    }

                default:
                    {
                        return GuardSample(new EmpiricalDistribution(NoUncertaintyFunction.CurveSample().Invert()) { XTransform = HazardTransform, ProbabilityTransform = ProbabilityTransform, Extrapolation = ExtrapolationSupport.Map(_extrapolation) });
                    }
            }
        }

        /// <inheritdoc/>
        /// <exception cref="InvalidOperationException">Thrown when the active table is invalid.</exception>
        public override IUnivariateDistribution SampleFunction(double percentile)
        {
            ThrowIfNotUsable();
            return GuardSample(SampleCore(percentile));
        }

        /// <summary>
        /// The percentile-curve construction shared by the public sampling members and the
        /// internal mean-curve builders. The builders consume the raw product directly: the mean
        /// derivation probes percentile curves across the declared span, which is internal
        /// construction rather than chain evaluation, so the Error-mode guard applies only to the
        /// published products.
        /// </summary>
        /// <param name="percentile">The knowledge-uncertainty percentile.</param>
        /// <returns>The sampled (monotonicity-repaired) hazard curve.</returns>
        private EmpiricalDistribution SampleCore(double percentile)
        {
            OrderedPairedData opd = _uncertaintyValue switch
            {
                FunctionUncertainty.Hazard => HazardUncertainFunction.CurveSample(percentile).Invert(),
                FunctionUncertainty.Probability => ProbabilityUncertainFunction.CurveSample(percentile),
                _ => NoUncertaintyFunction.CurveSample().Invert(),
            };

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
        /// Mode-switched, exactly as in v1.0: the None mode reads the first mean ordinate hazard;
        /// the Hazard mode reads the first ordinate's mean (or its 0.00001 percentile under full
        /// uncertainty); the Probability mode reads the first ordinate's hazard value. Empty tables
        /// report <see cref="double.MaxValue"/>.
        /// </remarks>
        public override double MinHazard(bool meanOnly)
        {
            switch (_uncertaintyValue)
            {
                case FunctionUncertainty.Hazard:
                    return HazardUncertainFunction is null || HazardUncertainFunction.Count == 0
                        ? double.MaxValue
                        : meanOnly ? HazardUncertainFunction[0].GetOrdinate().Y : HazardUncertainFunction[0].GetOrdinate(_minPercentile).Y;
                case FunctionUncertainty.Probability:
                    return ProbabilityUncertainFunction is null || ProbabilityUncertainFunction.Count == 0
                        ? double.MaxValue
                        : ProbabilityUncertainFunction[0].X;
                default:
                    return NoUncertaintyFunction is null || NoUncertaintyFunction.Count == 0
                        ? double.MaxValue
                        : NoUncertaintyFunction[0].GetOrdinate().Y;
            }
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Mode-switched, exactly as in v1.0 (see <see cref="MinHazard(bool)"/>); the Hazard mode's
        /// full-uncertainty bound probes the last ordinate at the 1 − 0.00001 percentile. Empty
        /// tables report <see cref="double.MinValue"/>.
        /// </remarks>
        public override double MaxHazard(bool meanOnly)
        {
            switch (_uncertaintyValue)
            {
                case FunctionUncertainty.Hazard:
                    return HazardUncertainFunction is null || HazardUncertainFunction.Count == 0
                        ? double.MinValue
                        : meanOnly ? HazardUncertainFunction[HazardUncertainFunction.Count - 1].GetOrdinate().Y : HazardUncertainFunction[HazardUncertainFunction.Count - 1].GetOrdinate(1d - _minPercentile).Y;
                case FunctionUncertainty.Probability:
                    return ProbabilityUncertainFunction is null || ProbabilityUncertainFunction.Count == 0
                        ? double.MinValue
                        : ProbabilityUncertainFunction[ProbabilityUncertainFunction.Count - 1].X;
                default:
                    return NoUncertaintyFunction is null || NoUncertaintyFunction.Count == 0
                        ? double.MinValue
                        : NoUncertaintyFunction[NoUncertaintyFunction.Count - 1].GetOrdinate().Y;
            }
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Exact percentile evaluation over the ACTIVE table — no simulation. Curves are
        /// index-aligned with the active table's ordinates: hazard-value curves per probability
        /// ordinate in the Hazard mode, probability curves per hazard ordinate in the Probability
        /// mode, and the deterministic curve (all summaries equal) in the None mode.
        /// </remarks>
        public override UncertaintyAnalysisResults? ComputeUncertaintyResults(double confidenceIntervalWidth = 0.9)
        {
            return TabularUncertainty.FromCoMonotonicTable(TargetFunction, confidenceIntervalWidth);
        }

        #endregion

        #region Serialization

        /// <inheritdoc/>
        public override XElement ToXElement()
        {
            var element = new XElement(nameof(TabularHazard));
            WriteIdentityAttributes(element);
            element.SetAttributeValue(nameof(SpecifiedHazard), SpecifiedHazard);
            element.SetAttributeValue(nameof(HazardUnit), HazardUnit);
            element.SetAttributeValue(nameof(HazardTransform), HazardTransform.ToString());
            element.SetAttributeValue(nameof(ProbabilityTransform), ProbabilityTransform.ToString());
            element.SetAttributeValue(nameof(UncertaintyValue), UncertaintyValue.ToString());
            // Conditional presence: the attribute is written only when non-default, so every
            // pre-existing serialized form — and its canonical hash and seeds — is unchanged.
            if (_extrapolation != ExtrapolationPolicy.None)
            {
                element.SetAttributeValue(nameof(Extrapolation), _extrapolation.ToString());
            }
            element.Add(new XElement(nameof(NoUncertaintyFunction), NoUncertaintyFunction.SaveToXElement()));
            element.Add(new XElement(nameof(HazardUncertainFunction), HazardUncertainFunction.SaveToXElement()));
            element.Add(new XElement(nameof(ProbabilityUncertainFunction), ProbabilityUncertainFunction.SaveToXElement()));
            return element;
        }

        /// <summary>
        /// Reads one wrapped table from the serialized form and re-imposes its ordering contract.
        /// </summary>
        /// <param name="parent">The serialized tabular hazard element.</param>
        /// <param name="wrapperName">The wrapper child element name.</param>
        /// <param name="xOrder">The table's X sort order.</param>
        /// <param name="yOrder">The table's Y sort order.</param>
        /// <returns>The parsed table, or null when absent.</returns>
        private static UncertainOrderedPairedData? ReadTable(XElement parent, string wrapperName, SortOrder xOrder, SortOrder yOrder)
        {
            var tableElement = parent.Element(wrapperName)?.Element("UncertainOrderedPairedData");
            if (tableElement == null) return null;
            var table = new UncertainOrderedPairedData(tableElement)
            {
                OrderX = xOrder,
                OrderY = yOrder,
                StrictX = true,
                StrictY = true,
            };
            table.Validate();
            return table;
        }

        #endregion

        #region Private Helpers

        /// <summary>
        /// Throws when the active table cannot be sampled (v1.0's function-valid gate, upgraded
        /// from a silent null return).
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when the active table is invalid.</exception>
        private void ThrowIfNotUsable()
        {
            var target = TargetFunction;
            bool usable = target is not null && target.Count >= 2 && target.IsValid;
            if (usable && (UncertaintyValue == FunctionUncertainty.None || UncertaintyValue == FunctionUncertainty.Hazard))
            {
                foreach (var ordinate in target!)
                {
                    if (ordinate.X < 0d || ordinate.X > 1d) { usable = false; break; }
                }
            }
            else if (usable)
            {
                foreach (var ordinate in target!)
                {
                    if (ordinate.Y!.Minimum < 0d || ordinate.Y.Maximum > 1d) { usable = false; break; }
                }
            }
            if (!usable)
                throw new InvalidOperationException("The tabular hazard table is invalid. Call Validate() and correct the reported errors before sampling.");
        }

        /// <summary>
        /// Validates the logarithmic hazard axis per uncertainty mode (exact v1.0 checks: the
        /// hazard range lives on the Y distributions for the None/Hazard modes and on the X
        /// ordinates for the Probability mode).
        /// </summary>
        /// <param name="messages">The message sink.</param>
        private void ValidateHazardTransform(List<string> messages)
        {
            if (HazardTransform != Transform.Logarithmic) return;

            bool isNegative = false;
            if (_uncertaintyValue == FunctionUncertainty.None)
            {
                for (int i = 0; i < _noUncertaintyFunction.Count; i++)
                {
                    if (_noUncertaintyFunction[i].Y!.Mean < 0.0d) { isNegative = true; break; }
                }
            }
            else if (_uncertaintyValue == FunctionUncertainty.Hazard)
            {
                for (int i = 0; i < _hazardUncertainFunction.Count; i++)
                {
                    var y = _hazardUncertainFunction[i].Y!;
                    if (double.IsPositiveInfinity(y.Maximum))
                    {
                        if (y.InverseCDF(1d - _minPercentile) < 0.0d) isNegative = true;
                    }
                    else if (y.Maximum < 0.0d)
                    {
                        isNegative = true;
                    }
                    if (y.Mean < 0.0d) isNegative = true;
                    if (double.IsNegativeInfinity(y.Minimum))
                    {
                        if (y.InverseCDF(_minPercentile) < 0.0d) isNegative = true;
                    }
                    else if (y.Minimum < 0.0d)
                    {
                        isNegative = true;
                    }
                    if (isNegative) break;
                }
            }
            else if (_probabilityUncertainFunction.Count > 0
                && (_probabilityUncertainFunction[0].X < 0.0d || _probabilityUncertainFunction[_probabilityUncertainFunction.Count - 1].X < 0.0d))
            {
                isNegative = true;
            }

            if (isNegative)
            {
                messages.Add("Error: The hazard interpolation transform cannot be logarithmic. There are hazard values that are less than zero.");
            }
        }

        /// <summary>
        /// Validates the logarithmic probability axis per uncertainty mode (exact v1.0 checks: the
        /// probability range lives on the X ordinates for the None/Hazard modes and on the Y
        /// distributions for the Probability mode).
        /// </summary>
        /// <param name="messages">The message sink.</param>
        private void ValidateProbabilityTransform(List<string> messages)
        {
            if (ProbabilityTransform != Transform.Logarithmic) return;

            bool isNegative = false;
            if (_uncertaintyValue == FunctionUncertainty.None)
            {
                for (int i = 0; i < _noUncertaintyFunction.Count; i++)
                {
                    if (_noUncertaintyFunction[i].X < 0.0d) { isNegative = true; break; }
                }
            }
            else if (_uncertaintyValue == FunctionUncertainty.Hazard)
            {
                if (_hazardUncertainFunction.Count > 0
                    && (_hazardUncertainFunction[0].X < 0.0d || _hazardUncertainFunction[_hazardUncertainFunction.Count - 1].X < 0.0d))
                {
                    isNegative = true;
                }
            }
            else
            {
                for (int i = 0; i < _probabilityUncertainFunction.Count; i++)
                {
                    var y = _probabilityUncertainFunction[i].Y!;
                    if (double.IsPositiveInfinity(y.Maximum))
                    {
                        if (y.InverseCDF(1d - _minPercentile) < 0.0d) isNegative = true;
                    }
                    else if (y.Maximum < 0.0d)
                    {
                        isNegative = true;
                    }
                    if (y.Mean < 0.0d) isNegative = true;
                    if (double.IsNegativeInfinity(y.Minimum))
                    {
                        if (y.InverseCDF(_minPercentile) < 0.0d) isNegative = true;
                    }
                    else if (y.Minimum < 0.0d)
                    {
                        isNegative = true;
                    }
                    if (isNegative) break;
                }
            }

            if (isNegative)
            {
                messages.Add("Error: The probability interpolation transform cannot be logarithmic. There are inputs that will produce probability values less than zero.");
            }
        }

        #endregion
    }
}
