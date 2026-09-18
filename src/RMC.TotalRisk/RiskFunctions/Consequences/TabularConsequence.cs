using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Linq;
using Numerics.Data;
using Numerics.Distributions;
using Numerics.Functions;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.Core.Support;

namespace RMC.TotalRisk.RiskFunctions.Consequences
{
    /// <summary>
    /// A tabular consequence function: paired-data interpolation from hazard to consequence
    /// magnitude (life loss, damages), with optional per-ordinate knowledge uncertainty and
    /// optional logarithmic interpolation axes. Sampled consequences are clamped at zero.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Ported from v1.0 <c>TabularConsequence</c> with the domain surface preserved verbatim. The
    /// table is an <see cref="Numerics.Data.UncertainOrderedPairedData"/> with strictly ascending
    /// hazard X and a consequence distribution per ordinate, sampled co-monotonically (one
    /// percentile drives every ordinate). Unlike the transform cluster, the sampled
    /// <see cref="TabularFunction"/> sets <c>AllowNegativeYValues = false</c>: negative sampled
    /// consequences evaluate to zero (the v1.0 clamp, matched by the negative-consequence warning
    /// in <see cref="Validate"/>).
    /// </para>
    /// <para>
    /// Sampling dimension D = 1 (one uncertain-ordinate percentile per realization).
    /// </para>
    /// </remarks>
    public class TabularConsequence : ConsequenceFunctionBase
    {
        #region Construction

        /// <summary>
        /// Initializes a tabular consequence function with the v1.0 default table:
        /// {(0 → Deterministic(0)), (1 → Deterministic(1))}.
        /// </summary>
        public TabularConsequence()
        {
        }

        /// <summary>
        /// Restores a tabular consequence function from its serialized form.
        /// </summary>
        /// <param name="xElement">The serialized form produced by <see cref="ToXElement"/>.</param>
        /// <exception cref="ArgumentNullException">Thrown when the element is null.</exception>
        public TabularConsequence(XElement xElement)
        {
            if (xElement == null) throw new ArgumentNullException(nameof(xElement));

            ReadIdentityAttributes(xElement);
            SpecifiedHazard = SerializationUtilities.ReadString(xElement, nameof(SpecifiedHazard));
            HazardUnit = SerializationUtilities.ReadString(xElement, nameof(HazardUnit));
            SpecifiedConsequence = SerializationUtilities.ReadString(xElement, nameof(SpecifiedConsequence));
            ConsequenceUnit = SerializationUtilities.ReadString(xElement, nameof(ConsequenceUnit));
            _hazardTransform = SerializationUtilities.ReadEnum(xElement, nameof(HazardTransform), Transform.None);
            _consequenceTransform = SerializationUtilities.ReadEnum(xElement, nameof(ConsequenceTransform), Transform.None);
            _extrapolation = SerializationUtilities.ReadEnum(xElement, nameof(Extrapolation), ExtrapolationPolicy.None);

            var tableElement = xElement.Element("UncertainOrderedPairedData");
            if (tableElement != null)
            {
                var table = new UncertainOrderedPairedData(tableElement)
                {
                    // Re-impose the consequence table's ordering contract after the permissive
                    // parse (exact v1.0 Open() behavior).
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
        /// Backing field for <see cref="ConsequenceTransform"/>.
        /// </summary>
        private Transform _consequenceTransform = Transform.None;

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
        /// The interpolation transform applied to the consequence (Y) axis.
        /// </summary>
        public Transform ConsequenceTransform
        {
            get { return _consequenceTransform; }
            set
            {
                if (_consequenceTransform != value)
                {
                    _consequenceTransform = value;
                    RaisePropertyChange(nameof(ConsequenceTransform));
                }
            }
        }

        /// <summary>
        /// The extrapolation policy applied when the consequence function is evaluated outside
        /// its table range. Default = <see cref="ExtrapolationPolicy.None"/> (the v1.0 endpoint
        /// hold, bit-identical); a non-default policy is compute-relevant hashed content. The
        /// negative-consequence zero clamp still binds on extended evaluations.
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
        /// The consequence table: strictly ascending hazard X with a consequence distribution per
        /// ordinate.
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
        public override ConsequenceFunctionType FunctionType => ConsequenceFunctionType.Tabular;

        /// <inheritdoc/>
        public override bool IsDeterministic => UncertainOrderedPairedData.Distribution == UnivariateDistributionType.Deterministic;

        /// <inheritdoc/>
        public override int SamplingDimensions => 1;

        #endregion

        #region IRiskFunction Methods

        /// <inheritdoc/>
        /// <remarks>
        /// Errors (invalidating): fewer than two ordinates; invalid ordinates; missing axis labels;
        /// a logarithmic hazard axis over negative hazards; a logarithmic consequence axis over any
        /// negative consequence range. Warnings (advisory): a first-ordinate mean consequence above
        /// zero — worded for the configured <see cref="Extrapolation"/> policy (held, extended, or
        /// refused below the table) — and consequence values below −0.00001 anywhere in the sampled
        /// range (they clamp to zero during simulation). As in v1.0, validation coerces
        /// PertPercentile ordinates to a minimum allowable value of zero.
        /// </remarks>
        public override (bool IsValid, List<string> ValidationMessages) Validate()
        {
            var messages = new List<string>();

            if (string.IsNullOrEmpty(SpecifiedHazard))
                messages.Add("Error: The tabular consequence function does not have a specified hazard type.");
            if (string.IsNullOrEmpty(HazardUnit))
                messages.Add("Error: The tabular consequence function does not have a specified hazard unit.");
            if (string.IsNullOrEmpty(SpecifiedConsequence))
                messages.Add("Error: The tabular consequence function does not have a specified consequence type.");
            if (string.IsNullOrEmpty(ConsequenceUnit))
                messages.Add("Error: The tabular consequence function does not have a specified consequence unit.");

            bool tableUsable = true;
            if (UncertainOrderedPairedData is null || UncertainOrderedPairedData.Count < 2)
            {
                messages.Add("Error: The consequence function must have at least two ordinates.");
                tableUsable = false;
            }
            else if (!UncertainOrderedPairedData.IsValid)
            {
                messages.Add("Error: Invalid consequence function ordinates.");
                tableUsable = false;
            }

            if (tableUsable && HazardTransform == Transform.Logarithmic && UncertainOrderedPairedData![0].X < 0.0d)
            {
                messages.Add("Error: The hazard interpolation transform cannot be logarithmic. There are hazard values less than zero.");
            }

            if (tableUsable)
            {
                // v1.0 coerces PertPercentile ordinates to clamp at zero during validation.
                if (UncertainOrderedPairedData!.Distribution == UnivariateDistributionType.PertPercentile)
                {
                    for (int i = 0; i < UncertainOrderedPairedData.Count; i++)
                        ((PertPercentile)UncertainOrderedPairedData[i].Y!).MinAllowableValue = 0d;
                }

                // Non-zero consequences below the first hazard level; the wording follows the
                // configured extrapolation policy (held, extended, or refused below the table).
                double firstHazard = UncertainOrderedPairedData[0].X;
                double firstMean = Math.Round(UncertainOrderedPairedData[0].GetOrdinate().Y, 4);
                if (firstMean > 0.0000000000000001d)
                {
                    if (_extrapolation == ExtrapolationPolicy.Below || _extrapolation == ExtrapolationPolicy.Both)
                    {
                        messages.Add("Warning: Any hazard level evaluated below " + firstHazard.ToString("N2", CultureInfo.InvariantCulture)
                            + " will follow the extended lower boundary segment from a mean consequence of " + firstMean.ToString("N2", CultureInfo.InvariantCulture)
                            + ". Confirm the extension is appropriate for lower hazard levels.");
                    }
                    else if (_extrapolation == ExtrapolationPolicy.Error)
                    {
                        messages.Add("Warning: Any hazard level evaluated below " + firstHazard.ToString("N2", CultureInfo.InvariantCulture)
                            + " will stop the analysis (the extrapolation policy is Error) even though the mean consequence there is greater than zero ("
                            + firstMean.ToString("N2", CultureInfo.InvariantCulture) + ").");
                    }
                    else
                    {
                        messages.Add("Warning: Any hazard level evaluated below " + firstHazard.ToString("N2", CultureInfo.InvariantCulture)
                            + " will result in consequences greater than zero (" + firstMean.ToString("N2", CultureInfo.InvariantCulture)
                            + ") on average. This can result in inaccurate risk estimates for lower hazard levels.");
                    }
                }

                // Negative-consequence scan across each ordinate's upper, mean, and lower range.
                var (isNegative, isVeryNegative) = ScanForNegativeConsequences();
                if (isVeryNegative)
                {
                    messages.Add("Warning: There is a potential to produce negative consequence values during a risk simulation with full uncertainty. Negative consequence values will be set to zero.");
                }
                if (ConsequenceTransform == Transform.Logarithmic && isNegative)
                {
                    messages.Add("Error: The consequence interpolation transform cannot be logarithmic. There are consequence values less than zero.");
                }
            }

            return (messages.FindIndex(m => m.StartsWith("Error:", StringComparison.Ordinal)) < 0, messages);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The sampled function clamps negative consequences to zero
        /// (<c>AllowNegativeYValues = false</c>). Improved over v1.0: an invalid table throws
        /// instead of silently returning null.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown when the table is invalid.</exception>
        public override IUnivariateFunction SampleFunction()
        {
            if (!TableIsUsable())
                throw new InvalidOperationException("The tabular consequence table is invalid. Call Validate() and correct the reported errors before sampling.");
            return GuardSample(new TabularFunction(UncertainOrderedPairedData)
            {
                ConfidenceLevel = -1,
                XTransform = HazardTransform,
                YTransform = ConsequenceTransform,
                AllowNegativeYValues = false,
                Extrapolation = ExtrapolationSupport.Map(_extrapolation),
            });
        }

        /// <inheritdoc/>
        /// <exception cref="InvalidOperationException">Thrown when the table is invalid.</exception>
        public override IUnivariateFunction SampleFunction(double percentile)
        {
            if (!TableIsUsable())
                throw new InvalidOperationException("The tabular consequence table is invalid. Call Validate() and correct the reported errors before sampling.");
            return GuardSample(new TabularFunction(UncertainOrderedPairedData)
            {
                ConfidenceLevel = percentile,
                XTransform = HazardTransform,
                YTransform = ConsequenceTransform,
                AllowNegativeYValues = false,
                Extrapolation = ExtrapolationSupport.Map(_extrapolation),
            });
        }

        /// <inheritdoc/>
        public override IUnivariateFunction SampleFunction(int realizationIndex)
        {
            return SampleFunction(Percentile(realizationIndex, 0));
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
            var element = new XElement(nameof(TabularConsequence));
            WriteIdentityAttributes(element);
            element.SetAttributeValue(nameof(SpecifiedHazard), SpecifiedHazard);
            element.SetAttributeValue(nameof(HazardUnit), HazardUnit);
            element.SetAttributeValue(nameof(SpecifiedConsequence), SpecifiedConsequence);
            element.SetAttributeValue(nameof(ConsequenceUnit), ConsequenceUnit);
            element.SetAttributeValue(nameof(HazardTransform), HazardTransform.ToString());
            element.SetAttributeValue(nameof(ConsequenceTransform), ConsequenceTransform.ToString());
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
        /// <param name="sampled">The sampled function product.</param>
        /// <returns>The product, guarded when the policy is Error.</returns>
        private IUnivariateFunction GuardSample(TabularFunction sampled)
        {
            if (_extrapolation != ExtrapolationPolicy.Error) return sampled;
            return new RangeGuardedUnivariateFunction(
                sampled, Name, $"{SpecifiedHazard} ({HazardUnit})", MinHazard(), MaxHazard());
        }

        /// <summary>
        /// Determines whether the table can be sampled (v1.0's function-valid gate).
        /// </summary>
        /// <returns>True when the table has at least two valid ordinates.</returns>
        private bool TableIsUsable()
        {
            return UncertainOrderedPairedData is not null && UncertainOrderedPairedData.Count >= 2 && UncertainOrderedPairedData.IsValid;
        }

        /// <summary>
        /// Scans each ordinate's upper, mean, and lower consequence range for negative values,
        /// with the ±0.00001-percentile probes guarding unbounded distributions (exact v1.0 scan).
        /// </summary>
        /// <returns>
        /// <c>isNegative</c>: any value below zero (blocks a logarithmic consequence axis);
        /// <c>isVeryNegative</c>: any value below −0.00001 (raises the clamp warning).
        /// </returns>
        private (bool isNegative, bool isVeryNegative) ScanForNegativeConsequences()
        {
            bool isNegative = false;
            bool isVeryNegative = false;
            for (int i = 0; i < UncertainOrderedPairedData.Count; i++)
            {
                var y = UncertainOrderedPairedData[i].Y!;

                if (double.IsPositiveInfinity(y.Maximum))
                {
                    double sampled = y.InverseCDF(1d - 0.00001d);
                    if (sampled < 0.0d) isNegative = true;
                    if (sampled < -0.00001d) isVeryNegative = true;
                }
                else
                {
                    if (y.Maximum < 0.0d) isNegative = true;
                    if (y.Maximum < -0.00001d) isVeryNegative = true;
                }

                if (y.Mean < 0.0d) isNegative = true;
                if (y.Mean < -0.00001d) isVeryNegative = true;

                if (double.IsNegativeInfinity(y.Minimum))
                {
                    double sampled = y.InverseCDF(0.00001d);
                    if (sampled < 0.0d) isNegative = true;
                    if (sampled < -0.00001d) isVeryNegative = true;
                }
                else
                {
                    if (y.Minimum < 0.0d) isNegative = true;
                    if (y.Minimum < -0.00001d) isVeryNegative = true;
                }

                if (isNegative) break;
            }
            return (isNegative, isVeryNegative);
        }

        #endregion
    }
}
