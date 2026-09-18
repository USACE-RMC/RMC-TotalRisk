using System;
using System.Collections.Generic;
using System.Xml.Linq;
using Numerics.Data;
using Numerics.Distributions;
using Numerics.Functions;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.Core.Support;

namespace RMC.TotalRisk.RiskFunctions.Transforms
{
    /// <summary>
    /// A tabular transform function: paired-data interpolation from one hazard domain to another
    /// (e.g., a flow-to-stage rating curve), with optional per-ordinate knowledge uncertainty and
    /// optional logarithmic interpolation axes.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Ported from v1.0 <c>TabularTransform</c> with the domain surface preserved verbatim. The
    /// table is an <see cref="Numerics.Data.UncertainOrderedPairedData"/> with strictly ascending X
    /// (input hazard) and unconstrained Y ordinate distributions (transformed hazard). Knowledge
    /// uncertainty is sampled co-monotonically: one percentile drives every ordinate
    /// (<c>TabularFunction.ConfidenceLevel</c>), preserving perfect rank correlation across hazard
    /// levels. Interpolation is linear on the transformed axes. Beyond the table,
    /// <see cref="Extrapolation"/> governs: the default holds the endpoint ordinates (the v1.0
    /// behavior, bit-identical), the extending policies extend the boundary segments linearly in
    /// the transform spaces, and Error refuses out-of-range forward evaluation loudly.
    /// </para>
    /// <para>
    /// Sampling dimension D = 1 (one uncertain-ordinate percentile per realization).
    /// </para>
    /// </remarks>
    public class TabularTransform : TransformFunctionBase
    {
        #region Construction

        /// <summary>
        /// Initializes a tabular transform with the v1.0 default table:
        /// {(0 → Deterministic(0)), (1 → Deterministic(1))}.
        /// </summary>
        public TabularTransform()
        {
        }

        /// <summary>
        /// Restores a tabular transform from its serialized form.
        /// </summary>
        /// <param name="xElement">The serialized form produced by <see cref="ToXElement"/>.</param>
        /// <exception cref="ArgumentNullException">Thrown when the element is null.</exception>
        public TabularTransform(XElement xElement)
        {
            if (xElement == null) throw new ArgumentNullException(nameof(xElement));

            ReadIdentityAttributes(xElement);
            SpecifiedHazard = SerializationUtilities.ReadString(xElement, nameof(SpecifiedHazard));
            HazardUnit = SerializationUtilities.ReadString(xElement, nameof(HazardUnit));
            TransformedHazard = SerializationUtilities.ReadString(xElement, nameof(TransformedHazard));
            TransformedHazardUnit = SerializationUtilities.ReadString(xElement, nameof(TransformedHazardUnit));
            _hazardTransform = SerializationUtilities.ReadEnum(xElement, nameof(HazardTransform), Transform.None);
            _transformTransform = SerializationUtilities.ReadEnum(xElement, nameof(TransformTransform), Transform.None);
            _extrapolation = SerializationUtilities.ReadEnum(xElement, nameof(Extrapolation), ExtrapolationPolicy.None);

            var tableElement = xElement.Element("UncertainOrderedPairedData");
            if (tableElement != null)
            {
                var table = new UncertainOrderedPairedData(tableElement)
                {
                    // Re-impose the transform table's ordering contract after the permissive parse
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
        /// Backing field for <see cref="TransformTransform"/>.
        /// </summary>
        private Transform _transformTransform = Transform.None;

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
        /// The interpolation transform applied to the input-hazard (X) axis.
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
        /// The interpolation transform applied to the transformed-hazard (Y) axis.
        /// </summary>
        public Transform TransformTransform
        {
            get { return _transformTransform; }
            set
            {
                if (_transformTransform != value)
                {
                    _transformTransform = value;
                    RaisePropertyChange(nameof(TransformTransform));
                }
            }
        }

        /// <summary>
        /// The extrapolation policy applied when the transform is evaluated outside its table
        /// range. Default = <see cref="ExtrapolationPolicy.None"/> (the v1.0 endpoint hold,
        /// bit-identical); a non-default policy is compute-relevant hashed content.
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
        /// The transform table: strictly ascending input hazard X with a transformed-hazard
        /// distribution per ordinate.
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
        public override TransformFunctionType FunctionType => TransformFunctionType.Tabular;

        /// <inheritdoc/>
        public override bool IsDeterministic => UncertainOrderedPairedData.Distribution == UnivariateDistributionType.Deterministic;

        /// <inheritdoc/>
        public override int SamplingDimensions => 1;

        #endregion

        #region IRiskFunction Methods

        /// <inheritdoc/>
        /// <remarks>
        /// Errors (invalidating): fewer than two ordinates; invalid ordinates; a logarithmic axis
        /// over values below zero; missing axis labels (v1.0 required non-empty labels — the model
        /// library has no project defaults, so callers set them). Name checks are a UI-layer
        /// concern and are not performed here.
        /// </remarks>
        public override (bool IsValid, List<string> ValidationMessages) Validate()
        {
            var messages = new List<string>();

            if (string.IsNullOrEmpty(SpecifiedHazard))
                messages.Add("Error: The tabular transform function does not have a specified hazard type.");
            if (string.IsNullOrEmpty(HazardUnit))
                messages.Add("Error: The tabular transform function does not have a specified hazard unit.");
            if (string.IsNullOrEmpty(TransformedHazard))
                messages.Add("Error: The tabular transform function does not have a transformed hazard type.");
            if (string.IsNullOrEmpty(TransformedHazardUnit))
                messages.Add("Error: The tabular transform function does not have a specified transformed hazard unit.");

            bool functionValid = ValidateTable(messages);
            ValidateHazardTransform(messages, functionValid);
            ValidateTransformTransform(messages, functionValid);

            return (messages.FindIndex(m => m.StartsWith("Error:", StringComparison.Ordinal)) < 0, messages);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Improved over v1.0: an invalid table throws instead of silently returning null (the
        /// engine validates before sampling either way; the throw surfaces caller mistakes).
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown when the table is invalid.</exception>
        public override IUnivariateFunction SampleFunction()
        {
            if (!TableIsUsable())
                throw new InvalidOperationException("The tabular transform table is invalid. Call Validate() and correct the reported errors before sampling.");
            return GuardSample(new TabularFunction(UncertainOrderedPairedData) { ConfidenceLevel = -1, XTransform = HazardTransform, YTransform = TransformTransform, Extrapolation = ExtrapolationSupport.Map(_extrapolation) });
        }

        /// <inheritdoc/>
        /// <exception cref="InvalidOperationException">Thrown when the table is invalid.</exception>
        public override IUnivariateFunction SampleFunction(double percentile)
        {
            if (!TableIsUsable())
                throw new InvalidOperationException("The tabular transform table is invalid. Call Validate() and correct the reported errors before sampling.");
            return GuardSample(new TabularFunction(UncertainOrderedPairedData) { ConfidenceLevel = percentile, XTransform = HazardTransform, YTransform = TransformTransform, Extrapolation = ExtrapolationSupport.Map(_extrapolation) });
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
        public override double MinTransformedHazard(bool meanOnly)
        {
            var first = UncertainOrderedPairedData[0];
            return meanOnly ? first.GetOrdinate().Y : first.GetOrdinate(0.00001d).Y;
        }

        /// <inheritdoc/>
        public override double MaxTransformedHazard(bool meanOnly)
        {
            var last = UncertainOrderedPairedData[UncertainOrderedPairedData.Count - 1];
            return meanOnly ? last.GetOrdinate().Y : last.GetOrdinate(1d - 0.00001d).Y;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Exact percentile evaluation — no simulation: for each table ordinate, the mean, the
        /// median (50th percentile), and the two-sided confidence bounds of the ordinate's
        /// transformed-hazard distribution. Curves are index-aligned with the table ordinates
        /// (callers pair them with the ordinate X values).
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
            var element = new XElement(nameof(TabularTransform));
            WriteIdentityAttributes(element);
            element.SetAttributeValue(nameof(SpecifiedHazard), SpecifiedHazard);
            element.SetAttributeValue(nameof(HazardUnit), HazardUnit);
            element.SetAttributeValue(nameof(TransformedHazard), TransformedHazard);
            element.SetAttributeValue(nameof(TransformedHazardUnit), TransformedHazardUnit);
            element.SetAttributeValue(nameof(HazardTransform), HazardTransform.ToString());
            element.SetAttributeValue(nameof(TransformTransform), TransformTransform.ToString());
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
        /// Validates the table shape (exact v1.0 checks).
        /// </summary>
        /// <param name="messages">The message sink.</param>
        /// <returns>True when the table is usable for the transform checks below.</returns>
        private bool ValidateTable(List<string> messages)
        {
            if (UncertainOrderedPairedData is null || UncertainOrderedPairedData.Count < 2)
            {
                messages.Add("Error: The tabular transform function must have at least two ordinates.");
                return false;
            }
            if (!UncertainOrderedPairedData.IsValid)
            {
                messages.Add("Error: Invalid tabular transform function ordinates.");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Validates the logarithmic input-hazard axis against negative hazard values (exact v1.0
        /// check: X is ascending, so only the first ordinate is inspected).
        /// </summary>
        /// <param name="messages">The message sink.</param>
        /// <param name="tableUsable">Whether the table checks passed.</param>
        private void ValidateHazardTransform(List<string> messages, bool tableUsable)
        {
            if (!tableUsable || HazardTransform != Transform.Logarithmic) return;
            if (UncertainOrderedPairedData[0].X < 0.0d)
            {
                messages.Add("Error: The hazard interpolation transform cannot be logarithmic. There are hazard values less than zero.");
            }
        }

        /// <summary>
        /// Validates the logarithmic transformed-hazard axis against negative values across each
        /// ordinate's upper, mean, and lower range (exact v1.0 check, with the ±0.00001-percentile
        /// probes guarding unbounded distributions).
        /// </summary>
        /// <param name="messages">The message sink.</param>
        /// <param name="tableUsable">Whether the table checks passed.</param>
        private void ValidateTransformTransform(List<string> messages, bool tableUsable)
        {
            if (!tableUsable || TransformTransform != Transform.Logarithmic) return;

            bool isNegative = false;
            for (int i = 0; i < UncertainOrderedPairedData.Count; i++)
            {
                var y = UncertainOrderedPairedData[i].Y!;
                if (double.IsPositiveInfinity(y.Maximum))
                {
                    if (y.InverseCDF(1d - 0.00001d) < 0.0d) isNegative = true;
                }
                else if (y.Maximum < 0.0d)
                {
                    isNegative = true;
                }

                if (y.Mean < 0.0d) isNegative = true;

                if (double.IsNegativeInfinity(y.Minimum))
                {
                    if (y.InverseCDF(0.00001d) < 0.0d) isNegative = true;
                }
                else if (y.Minimum < 0.0d)
                {
                    isNegative = true;
                }

                if (isNegative) break;
            }

            if (isNegative)
            {
                messages.Add("Error: The transform interpolation transform cannot be logarithmic. There are transform values less than zero.");
            }
        }

        #endregion
    }
}
