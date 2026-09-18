using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Linq;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.Core.Support;

namespace RMC.TotalRisk.RiskFunctions.Responses
{
    /// <summary>
    /// A tabular response (fragility) function: paired-data interpolation from hazard to failure
    /// probability, with optional per-ordinate knowledge uncertainty and optional interpolation
    /// transforms on either axis.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Ported from v1.0 <c>TabularResponse</c> with the domain surface preserved verbatim. The
    /// table is an <see cref="Numerics.Data.UncertainOrderedPairedData"/> with strictly ascending
    /// hazard X and a failure-probability distribution per ordinate, sampled co-monotonically (one
    /// percentile drives every ordinate). Probability ordinates must span [0, 1]. Note the default
    /// <see cref="ProbabilityTransform"/> is <see cref="Transform.None"/> — deliberately different
    /// from the hazard cluster's Normal-Z default.
    /// </para>
    /// <para>
    /// Sampling dimension D = 1. The integer <c>SampleResponseFunction</c>/<c>SampleFunction</c>
    /// overloads take a REALIZATION INDEX into the pre-allocated percentile matrix (v1.1 sampler
    /// contract) — v1.0 treated the integer as a PRNG seed feeding one uniform draw.
    /// </para>
    /// </remarks>
    public class TabularResponse : ResponseFunctionBase
    {
        #region Construction

        /// <summary>
        /// Initializes a tabular response with the v1.0 default table:
        /// {(0 → Deterministic(0)), (1 → Deterministic(1))}.
        /// </summary>
        public TabularResponse()
        {
        }

        /// <summary>
        /// Restores a tabular response from its serialized form.
        /// </summary>
        /// <param name="xElement">The serialized form produced by <see cref="ToXElement"/>.</param>
        /// <exception cref="ArgumentNullException">Thrown when the element is null.</exception>
        public TabularResponse(XElement xElement)
        {
            if (xElement == null) throw new ArgumentNullException(nameof(xElement));

            ReadIdentityAttributes(xElement);
            SpecifiedHazard = SerializationUtilities.ReadString(xElement, nameof(SpecifiedHazard));
            HazardUnit = SerializationUtilities.ReadString(xElement, nameof(HazardUnit));
            _hazardTransform = SerializationUtilities.ReadEnum(xElement, nameof(HazardTransform), Transform.None);
            _probabilityTransform = SerializationUtilities.ReadEnum(xElement, nameof(ProbabilityTransform), Transform.None);
            _extrapolation = SerializationUtilities.ReadEnum(xElement, nameof(Extrapolation), ExtrapolationPolicy.None);

            var tableElement = xElement.Element("UncertainOrderedPairedData");
            if (tableElement != null)
            {
                var table = new UncertainOrderedPairedData(tableElement)
                {
                    // Re-impose the response table's ordering contract after the permissive parse
                    // (exact v1.0 Open() behavior).
                    OrderX = SortOrder.Ascending,
                    OrderY = SortOrder.None,
                    StrictX = true,
                    StrictY = false,
                };
                table.Validate();
                _uncertainOrderedPairedData = table;
            }
        }

        #endregion

        #region Members

        /// <summary>
        /// Backing field for <see cref="HazardTransform"/>.
        /// </summary>
        private Transform _hazardTransform = Transform.None;

        /// <summary>
        /// Backing field for <see cref="ProbabilityTransform"/> — None by default (v1.0; the hazard
        /// cluster defaults Normal-Z, the response cluster does not).
        /// </summary>
        private Transform _probabilityTransform = Transform.None;

        /// <summary>
        /// Backing field for <see cref="Extrapolation"/>.
        /// </summary>
        private ExtrapolationPolicy _extrapolation = ExtrapolationPolicy.None;

        /// <summary>
        /// Backing field for <see cref="UncertainOrderedPairedData"/> — the v1.0 default table.
        /// </summary>
        private UncertainOrderedPairedData _uncertainOrderedPairedData = new UncertainOrderedPairedData(
            new[] { new UncertainOrdinate(0d, new Deterministic(0d)), new UncertainOrdinate(1d, new Deterministic(1d)) },
            true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic);

        /// <summary>
        /// The interpolation transform applied to the hazard (X) axis.
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
        /// The interpolation transform applied to the failure-probability (Y) axis.
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
        /// The extrapolation policy applied when the response function is evaluated outside its
        /// table range. Default = <see cref="ExtrapolationPolicy.None"/> (the v1.0 endpoint hold,
        /// bit-identical); a non-default policy is compute-relevant hashed content. Extended
        /// probabilities stay clamped to [0, 1] by the sampled distribution. The policy governs
        /// the sampled distribution products; the raw-curve
        /// <see cref="SampleResponseFunction()"/> members are policy-free by design.
        /// </summary>
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
        /// The response table: strictly ascending hazard X with a failure-probability distribution
        /// per ordinate.
        /// </summary>
        public UncertainOrderedPairedData UncertainOrderedPairedData
        {
            get { return _uncertainOrderedPairedData; }
            set
            {
                if (!ReferenceEquals(_uncertainOrderedPairedData, value) && value is not null)
                {
                    _uncertainOrderedPairedData = value;
                    RaisePropertyChange(nameof(UncertainOrderedPairedData));
                }
            }
        }

        /// <inheritdoc/>
        public override ResponseFunctionType FunctionType => ResponseFunctionType.Tabular;

        /// <inheritdoc/>
        public override bool IsDeterministic => UncertainOrderedPairedData.Distribution == UnivariateDistributionType.Deterministic;

        /// <inheritdoc/>
        public override int SamplingDimensions => 1;

        #endregion

        #region IRiskFunction Methods

        /// <inheritdoc/>
        /// <remarks>
        /// Errors (invalidating): fewer than two ordinates; invalid ordinates; probability values
        /// outside [0, 1]; missing axis labels; a logarithmic hazard axis over negative hazards; a
        /// logarithmic probability axis over a negative sampled range. Warnings (advisory): a
        /// first-ordinate mean failure probability above 1e-8 — worded for the configured
        /// <see cref="Extrapolation"/> policy (held, extended, or refused below the table) — and a
        /// non-monotonic response. As in v1.0, validation coerces PertPercentile ordinates to the
        /// allowable [0, 1] range.
        /// </remarks>
        public override (bool IsValid, List<string> ValidationMessages) Validate()
        {
            var messages = new List<string>();

            if (string.IsNullOrEmpty(SpecifiedHazard))
                messages.Add("Error: The tabular response function does not have a specified hazard type.");
            if (string.IsNullOrEmpty(HazardUnit))
                messages.Add("Error: The tabular response function does not have a specified hazard unit.");

            // v1.0 coerces PertPercentile ordinates to the [0, 1] allowable range during validation.
            if (UncertainOrderedPairedData is not null && UncertainOrderedPairedData.Distribution == UnivariateDistributionType.PertPercentile)
            {
                for (int i = 0; i < UncertainOrderedPairedData.Count; i++)
                {
                    ((PertPercentile)UncertainOrderedPairedData[i].Y!).MinAllowableValue = 0d;
                    ((PertPercentile)UncertainOrderedPairedData[i].Y!).MaxAllowableValue = 1d;
                }
            }

            bool tableUsable = true;
            if (UncertainOrderedPairedData is null || UncertainOrderedPairedData.Count < 2)
            {
                messages.Add("Error: The response function must have at least two ordinates.");
                tableUsable = false;
            }
            else if (!UncertainOrderedPairedData.IsValid)
            {
                messages.Add("Error: Invalid response function ordinates.");
                tableUsable = false;
            }
            else
            {
                foreach (var ordinate in UncertainOrderedPairedData)
                {
                    if (ordinate.Y!.Minimum < 0d)
                    {
                        messages.Add("Error: Probabilities must be greater than or equal to 0.");
                        tableUsable = false;
                        break;
                    }
                    if (ordinate.Y.Maximum > 1d)
                    {
                        messages.Add("Error: Probabilities must be less than or equal to 1.");
                        tableUsable = false;
                        break;
                    }
                }
            }

            if (tableUsable)
            {
                if (ProbabilityTransform == Transform.Logarithmic && HasNegativeProbabilityRange())
                {
                    messages.Add("Error: The probability interpolation transform cannot be logarithmic. There are inputs that will produce probability values less than zero.");
                }
                if (HazardTransform == Transform.Logarithmic && UncertainOrderedPairedData![0].X < 0.0d)
                {
                    messages.Add("Error: The hazard interpolation transform cannot be logarithmic. There are hazard values less than zero.");
                }

                // The wording follows the configured extrapolation policy (held, extended, or
                // refused below the table).
                double firstHazard = UncertainOrderedPairedData![0].X;
                double firstMeanProbability = UncertainOrderedPairedData[0].GetOrdinate().Y;
                if (firstMeanProbability > 0.00000001d)
                {
                    if (_extrapolation == ExtrapolationPolicy.Below || _extrapolation == ExtrapolationPolicy.Both)
                    {
                        messages.Add("Warning: Any hazard level evaluated below " + firstHazard.ToString("N2", CultureInfo.InvariantCulture)
                            + " will follow the extended lower boundary segment from a mean failure probability of " + firstMeanProbability.ToString("E4", CultureInfo.InvariantCulture)
                            + ". Confirm the extension is appropriate for lower hazard levels.");
                    }
                    else if (_extrapolation == ExtrapolationPolicy.Error)
                    {
                        messages.Add("Warning: Any hazard level evaluated below " + firstHazard.ToString("N2", CultureInfo.InvariantCulture)
                            + " will stop the analysis (the extrapolation policy is Error) even though the mean failure probability there is greater than zero ("
                            + firstMeanProbability.ToString("E4", CultureInfo.InvariantCulture) + ").");
                    }
                    else
                    {
                        messages.Add("Warning: Any hazard level evaluated below " + firstHazard.ToString("N2", CultureInfo.InvariantCulture)
                            + " will result in a probability of failure greater than zero (" + firstMeanProbability.ToString("E4", CultureInfo.InvariantCulture)
                            + ") on average. This can result in inaccurate risk estimates for lower hazard levels.");
                    }
                }

                if (!IsMonotonic())
                {
                    messages.Add("Warning: The response function is not monotonically increasing. Please confirm you have entered your inputs correctly before proceeding.");
                }
            }

            return (messages.FindIndex(m => m.StartsWith("Error:", StringComparison.Ordinal)) < 0, messages);
        }

        /// <inheritdoc/>
        public override OrderedPairedData SampleResponseFunction()
        {
            return UncertainOrderedPairedData.CurveSample();
        }

        /// <inheritdoc/>
        public override OrderedPairedData SampleResponseFunction(double percentile)
        {
            return UncertainOrderedPairedData.CurveSample(percentile);
        }

        /// <inheritdoc/>
        public override OrderedPairedData SampleResponseFunction(int realizationIndex)
        {
            return UncertainOrderedPairedData.CurveSample(Percentile(realizationIndex, 0));
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Improved over v1.0: an invalid table throws instead of silently returning null.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown when the table is invalid.</exception>
        public override IUnivariateDistribution SampleFunction()
        {
            if (!TableIsUsable())
                throw new InvalidOperationException("The tabular response table is invalid. Call Validate() and correct the reported errors before sampling.");
            return GuardSample(new EmpiricalDistribution(UncertainOrderedPairedData.CurveSample()) { XTransform = HazardTransform, ProbabilityTransform = ProbabilityTransform, Extrapolation = ExtrapolationSupport.Map(_extrapolation) });
        }

        /// <inheritdoc/>
        /// <exception cref="InvalidOperationException">Thrown when the table is invalid.</exception>
        public override IUnivariateDistribution SampleFunction(double percentile)
        {
            if (!TableIsUsable())
                throw new InvalidOperationException("The tabular response table is invalid. Call Validate() and correct the reported errors before sampling.");
            return GuardSample(new EmpiricalDistribution(UncertainOrderedPairedData.CurveSample(percentile)) { XTransform = HazardTransform, ProbabilityTransform = ProbabilityTransform, Extrapolation = ExtrapolationSupport.Map(_extrapolation) });
        }

        /// <inheritdoc/>
        public override IUnivariateDistribution SampleFunction(int realizationIndex)
        {
            return SampleFunction(Percentile(realizationIndex, 0));
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Exact v1.0 algorithm: tests the 0.00001-percentile curve, and — unless the table is
        /// deterministic — also the median and the (1 − 0.00001)-percentile curves, for any
        /// decreasing failure-probability step.
        /// </remarks>
        public override bool IsMonotonic()
        {
            bool monotonic = true;

            var func = SampleResponseFunction(0.00001d);
            for (int i = 1; i < func.Count; i++)
            {
                if (func[i].Y < func[i - 1].Y)
                    monotonic = false;
            }

            if (IsDeterministic)
                return monotonic;

            func = SampleResponseFunction(0.5d);
            for (int i = 1; i < func.Count; i++)
            {
                if (func[i].Y < func[i - 1].Y)
                    monotonic = false;
            }

            func = SampleResponseFunction(1d - 0.00001d);
            for (int i = 1; i < func.Count; i++)
            {
                if (func[i].Y < func[i - 1].Y)
                    monotonic = false;
            }

            return monotonic;
        }

        /// <inheritdoc/>
        public override double MinHazard()
        {
            return UncertainOrderedPairedData[0].X;
        }

        /// <inheritdoc/>
        public override double MaxHazard()
        {
            return UncertainOrderedPairedData[UncertainOrderedPairedData.Count - 1].X;
        }

        /// <inheritdoc/>
        public override double MinProbability()
        {
            return UncertainOrderedPairedData[0].GetOrdinate().Y;
        }

        /// <inheritdoc/>
        public override double MaxProbability()
        {
            return UncertainOrderedPairedData[UncertainOrderedPairedData.Count - 1].GetOrdinate().Y;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Exact percentile evaluation — no simulation. Curves are index-aligned with the table
        /// ordinates (callers pair them with the ordinate hazard values).
        /// </remarks>
        public override UncertaintyAnalysisResults? ComputeUncertaintyResults(double confidenceIntervalWidth = 0.9)
        {
            return TabularUncertainty.FromCoMonotonicTable(UncertainOrderedPairedData, confidenceIntervalWidth);
        }

        #endregion

        #region Serialization

        /// <inheritdoc/>
        public override XElement ToXElement()
        {
            var element = new XElement(nameof(TabularResponse));
            WriteIdentityAttributes(element);
            element.SetAttributeValue(nameof(SpecifiedHazard), SpecifiedHazard);
            element.SetAttributeValue(nameof(HazardUnit), HazardUnit);
            element.SetAttributeValue(nameof(HazardTransform), HazardTransform.ToString());
            element.SetAttributeValue(nameof(ProbabilityTransform), ProbabilityTransform.ToString());
            // Conditional presence: the attribute is written only when non-default, so every
            // pre-existing serialized form — and its canonical hash and seeds — is unchanged.
            if (_extrapolation != ExtrapolationPolicy.None)
            {
                element.SetAttributeValue(nameof(Extrapolation), _extrapolation.ToString());
            }
            element.Add(UncertainOrderedPairedData.SaveToXElement());
            return element;
        }

        #endregion

        #region Private Helpers

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

        /// <summary>
        /// Determines whether the table can be sampled (v1.0's function-valid gate: shape, ordinate
        /// validity, and probability bounds).
        /// </summary>
        /// <returns>True when the table has at least two valid ordinates within [0, 1].</returns>
        private bool TableIsUsable()
        {
            if (UncertainOrderedPairedData is null || UncertainOrderedPairedData.Count < 2 || !UncertainOrderedPairedData.IsValid)
                return false;
            foreach (var ordinate in UncertainOrderedPairedData)
            {
                if (ordinate.Y!.Minimum < 0d || ordinate.Y.Maximum > 1d)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Scans each ordinate's upper, mean, and lower probability range for negative values, with
        /// the ±0.00001-percentile probes guarding unbounded distributions (exact v1.0 scan).
        /// </summary>
        /// <returns>True when any sampled probability can be negative.</returns>
        private bool HasNegativeProbabilityRange()
        {
            for (int i = 0; i < UncertainOrderedPairedData.Count; i++)
            {
                var y = UncertainOrderedPairedData[i].Y!;
                if (double.IsPositiveInfinity(y.Maximum))
                {
                    if (y.InverseCDF(1d - 0.00001d) < 0.0d) return true;
                }
                else if (y.Maximum < 0.0d)
                {
                    return true;
                }

                if (y.Mean < 0.0d) return true;

                if (double.IsNegativeInfinity(y.Minimum))
                {
                    if (y.InverseCDF(0.00001d) < 0.0d) return true;
                }
                else if (y.Minimum < 0.0d)
                {
                    return true;
                }
            }
            return false;
        }

        #endregion
    }
}
