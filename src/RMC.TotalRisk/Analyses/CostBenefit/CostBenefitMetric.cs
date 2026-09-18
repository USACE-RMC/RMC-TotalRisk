using System;
using System.Collections.Generic;
using System.Xml.Linq;
using Numerics;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;

namespace RMC.TotalRisk.Analyses.CostBenefit
{
    /// <summary>
    /// One cost-benefit metric selector: either a risk-measure selection — a scalar measure on
    /// a stream and consequence type, read at a time basis under an accounting convention, as
    /// a level or a reduction — or one of the study's economics metrics.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The selector is immutable (replace it to edit) and deliberately mirrors the
    /// tolerable-risk criterion shape, so objectives and constraints declare over one
    /// vocabulary. The tail measures carry their own exceedance level α; NaN reads as the
    /// study's declared level. Every enum serializes by name, making those member names
    /// append-only serialized contract.
    /// </para>
    /// </remarks>
    public sealed class CostBenefitMetric
    {
        #region Construction

        /// <summary>
        /// The serialized discriminator text for the two selector kinds.
        /// </summary>
        private const string EconomicKind = "Economic";

        /// <summary>
        /// The serialized discriminator text for the risk-measure kind.
        /// </summary>
        private const string RiskMeasureKind = "RiskMeasure";

        /// <summary>
        /// Initializes a selector; the factories are the public construction surface.
        /// </summary>
        /// <param name="isEconomic">True for the economics form.</param>
        /// <param name="economicMetric">The economics metric (meaningful when economic).</param>
        /// <param name="measure">The risk measure (meaningful when not economic).</param>
        /// <param name="riskType">The stream (meaningful when not economic).</param>
        /// <param name="consequenceType">The consequence-type position (meaningful when not economic).</param>
        /// <param name="basis">The time basis (meaningful when not economic).</param>
        /// <param name="accounting">The accounting convention (meaningful when not economic).</param>
        /// <param name="form">The level-or-reduction form (meaningful when not economic).</param>
        /// <param name="alpha">The exceedance level for the tail measures; NaN = the study's level.</param>
        private CostBenefitMetric(bool isEconomic, EconomicMetric economicMetric, RiskMeasure measure,
            RiskType riskType, int consequenceType, MetricBasis basis, LifeCycleAccounting accounting,
            MetricForm form, double alpha)
        {
            IsEconomic = isEconomic;
            EconomicMetric = economicMetric;
            Measure = measure;
            RiskType = riskType;
            ConsequenceType = consequenceType;
            Basis = basis;
            Accounting = accounting;
            Form = form;
            Alpha = alpha;
        }

        /// <summary>
        /// Creates an economics-metric selector.
        /// </summary>
        /// <param name="metric">The economics metric.</param>
        /// <returns>The selector.</returns>
        public static CostBenefitMetric ForEconomic(EconomicMetric metric)
        {
            return new CostBenefitMetric(true, metric, RiskMeasure.Mean, RiskType.Total, 0,
                MetricBasis.AnnualizedPerEpoch, LifeCycleAccounting.NonAbsorbing, MetricForm.Level,
                double.NaN);
        }

        /// <summary>
        /// Creates a risk-measure selector.
        /// </summary>
        /// <param name="measure">The scalar risk measure.</param>
        /// <param name="riskType">The stream the measure reads.</param>
        /// <param name="consequenceType">The consequence-type position (0 = the primary type).</param>
        /// <param name="basis">The time basis the value is read at.</param>
        /// <param name="accounting">The accounting convention.</param>
        /// <param name="form">Whether the value is the alternative's level or its reduction vs the baseline.</param>
        /// <param name="alpha">The exceedance level for the tail measures; NaN = the study's declared level.</param>
        /// <returns>The selector.</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown for a negative consequence-type position or an alpha outside (0, 1) that is
        /// not NaN.
        /// </exception>
        public static CostBenefitMetric ForRiskMeasure(RiskMeasure measure, RiskType riskType,
            int consequenceType = 0, MetricBasis basis = MetricBasis.AnnualizedPerEpoch,
            LifeCycleAccounting accounting = LifeCycleAccounting.NonAbsorbing,
            MetricForm form = MetricForm.Level, double alpha = double.NaN)
        {
            if (consequenceType < 0)
                throw new ArgumentOutOfRangeException(nameof(consequenceType),
                    "The consequence-type position must not be negative.");
            if (!double.IsNaN(alpha) && (!Tools.IsFinite(alpha) || alpha <= 0d || alpha >= 1d))
                throw new ArgumentOutOfRangeException(nameof(alpha),
                    "The exceedance level must lie in (0, 1), or be NaN to read the study's level.");
            return new CostBenefitMetric(false, Core.Enums.EconomicMetric.PresentValueOfTotalCost,
                measure, riskType, consequenceType, basis, accounting, form, alpha);
        }

        /// <summary>
        /// Restores a selector from its serialized form.
        /// </summary>
        /// <param name="xElement">The serialized form produced by <see cref="ToXElement"/>.</param>
        /// <exception cref="ArgumentNullException">Thrown when the element is null.</exception>
        public CostBenefitMetric(XElement xElement)
        {
            SerializationUtilities.RequireElement(xElement, nameof(xElement));
            IsEconomic = string.Equals(SerializationUtilities.ReadString(xElement, "Kind", RiskMeasureKind),
                EconomicKind, StringComparison.Ordinal);
            EconomicMetric = SerializationUtilities.ReadEnum(xElement, nameof(EconomicMetric),
                Core.Enums.EconomicMetric.PresentValueOfTotalCost);
            Measure = SerializationUtilities.ReadEnum(xElement, nameof(Measure), RiskMeasure.Mean);
            RiskType = SerializationUtilities.ReadEnum(xElement, nameof(RiskType), RiskType.Total);
            ConsequenceType = SerializationUtilities.ReadInt32(xElement, nameof(ConsequenceType), 0);
            Basis = SerializationUtilities.ReadEnum(xElement, nameof(Basis), MetricBasis.AnnualizedPerEpoch);
            Accounting = SerializationUtilities.ReadEnum(xElement, nameof(Accounting), LifeCycleAccounting.NonAbsorbing);
            Form = SerializationUtilities.ReadEnum(xElement, nameof(Form), MetricForm.Level);
            Alpha = SerializationUtilities.ReadDouble(xElement, nameof(Alpha), double.NaN);
        }

        #endregion

        #region Members

        /// <summary>
        /// True for the economics form; false for the risk-measure form.
        /// </summary>
        public bool IsEconomic { get; }

        /// <summary>
        /// The economics metric (meaningful when <see cref="IsEconomic"/>).
        /// </summary>
        public EconomicMetric EconomicMetric { get; }

        /// <summary>
        /// The scalar risk measure (meaningful when not economic).
        /// </summary>
        public RiskMeasure Measure { get; }

        /// <summary>
        /// The stream the measure reads (meaningful when not economic).
        /// </summary>
        public RiskType RiskType { get; }

        /// <summary>
        /// The consequence-type position (0 = the primary type; meaningful when not economic).
        /// </summary>
        public int ConsequenceType { get; }

        /// <summary>
        /// The time basis the value is read at (meaningful when not economic).
        /// </summary>
        public MetricBasis Basis { get; }

        /// <summary>
        /// The accounting convention the value is read under (meaningful when not economic).
        /// </summary>
        public LifeCycleAccounting Accounting { get; }

        /// <summary>
        /// Whether the value is the alternative's level or its signed reduction vs the
        /// baseline (meaningful when not economic).
        /// </summary>
        public MetricForm Form { get; }

        /// <summary>
        /// The exceedance level for the tail measures; NaN reads the study's declared level.
        /// </summary>
        public double Alpha { get; }

        /// <summary>
        /// True when the metric resolves to one whole-horizon value per alternative — every
        /// economics metric, and every risk-measure selection on a horizon basis. Whole-horizon
        /// metrics are checked once at the Horizon constraint scope; annualized metrics are
        /// checked per epoch.
        /// </summary>
        internal bool IsWholeHorizon => IsEconomic || Basis != MetricBasis.AnnualizedPerEpoch;

        #endregion

        #region IModel Methods

        /// <summary>
        /// Validates the selector: recognized enum members, a non-negative consequence-type
        /// position, an exceedance level in (0, 1) or NaN, and the horizon-basis legality
        /// rules — the trajectory aggregates carry the Mean measure on the Total, Excess, and
        /// Fail streams only, so a horizon basis with any other measure or stream has no
        /// backing quantity.
        /// </summary>
        /// <returns>The validity flag and messages.</returns>
        public (bool IsValid, List<string> ValidationMessages) Validate()
        {
            var messages = new List<string>();
            if (IsEconomic)
            {
                if (!Enum.IsDefined(EconomicMetric))
                    messages.Add("Error: The economics metric is not a recognized member.");
            }
            else
            {
                if (!Enum.IsDefined(Measure))
                    messages.Add("Error: The metric's risk measure is not a recognized member.");
                if (!Enum.IsDefined(RiskType))
                    messages.Add("Error: The metric's stream is not a recognized member.");
                if (!Enum.IsDefined(Basis))
                    messages.Add("Error: The metric's basis is not a recognized member.");
                if (!Enum.IsDefined(Accounting))
                    messages.Add("Error: The metric's accounting convention is not a recognized member.");
                if (!Enum.IsDefined(Form))
                    messages.Add("Error: The metric's form is not a recognized member.");
                if (ConsequenceType < 0)
                    messages.Add("Error: The metric's consequence-type position must not be negative.");
                if (!double.IsNaN(Alpha) && (!Tools.IsFinite(Alpha) || Alpha <= 0d || Alpha >= 1d))
                    messages.Add("Error: The metric's exceedance level must lie in (0, 1), or be NaN for the study's level.");
                if (Enum.IsDefined(Basis) && Basis != MetricBasis.AnnualizedPerEpoch)
                {
                    if (Measure != RiskMeasure.Mean)
                        messages.Add($"Error: The metric declares the {Measure} measure on the {Basis} basis; horizon bases carry the Mean measure only — tail and dispersion measures are annualized.");
                    if (RiskType != RiskType.Total && RiskType != RiskType.Excess && RiskType != RiskType.Fail)
                        messages.Add($"Error: The metric declares the {RiskType} stream on the {Basis} basis; horizon aggregates exist for the Total, Excess, and Fail streams only.");
                }
            }
            return (messages.Count == 0, messages);
        }

        #endregion

        #region Serialization

        /// <summary>
        /// Serializes the selector. The kind discriminator and every enum write by name;
        /// element and attribute names are append-only contract.
        /// </summary>
        /// <returns>The serialized form.</returns>
        public XElement ToXElement()
        {
            var element = new XElement(nameof(CostBenefitMetric));
            element.SetAttributeValue("Kind", IsEconomic ? EconomicKind : RiskMeasureKind);
            if (IsEconomic)
            {
                element.SetAttributeValue(nameof(EconomicMetric), EconomicMetric.ToString());
            }
            else
            {
                element.SetAttributeValue(nameof(Measure), Measure.ToString());
                element.SetAttributeValue(nameof(RiskType), RiskType.ToString());
                element.SetAttributeValue(nameof(ConsequenceType), ConsequenceType);
                element.SetAttributeValue(nameof(Basis), Basis.ToString());
                element.SetAttributeValue(nameof(Accounting), Accounting.ToString());
                element.SetAttributeValue(nameof(Form), Form.ToString());
                element.SetAttributeValue(nameof(Alpha), SerializationUtilities.FormatDouble(Alpha));
            }
            return element;
        }

        #endregion
    }
}
