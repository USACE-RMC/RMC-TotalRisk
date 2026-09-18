using System;
using System.Collections.Generic;
using System.Xml.Linq;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;

namespace RMC.TotalRisk.Analyses.Risk
{
    /// <summary>
    /// One tolerable-risk criterion on a risk analysis: a scalar risk measure, the system
    /// stream and consequence type it reads, and the threshold the ensemble is compared
    /// against. The analysis reports the epistemic confidence statement
    /// P(measure &gt; threshold) — the fraction of realization weight whose measure strictly
    /// exceeds the threshold.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The default construction is the annualized incremental-risk guideline shape: the mean
    /// measure on the Excess stream of the primary consequence type at a threshold of 1e-3.
    /// Criteria evaluate at the system scope over the stored full-uncertainty ensemble — the
    /// epistemic statement the aleatory <c>ConsequenceThresholdProbability</c> measure does not
    /// provide (that measure asks about the loss distribution within one realization; this one
    /// asks how much of the knowledge ensemble exceeds a guideline).
    /// </para>
    /// <para>
    /// The criterion is immutable (replace an entry to edit it — the declared-consequence-type
    /// idiom), so it needs no change notification and is thread-safe by construction. It
    /// serializes as a child of <c>RiskAnalysisOptions</c>, written only when criteria exist —
    /// the options form, hash, and seeds of every criteria-free analysis are unchanged. The
    /// measure and stream serialize by enum name, making those member names append-only
    /// serialized contract.
    /// </para>
    /// </remarks>
    public sealed class TolerableRiskCriterion
    {
        #region Construction

        /// <summary>
        /// Initializes the guideline default criterion: mean incremental (Excess) risk of the
        /// primary consequence type against a threshold of 1e-3.
        /// </summary>
        public TolerableRiskCriterion()
            : this(RiskMeasure.Mean, RiskType.Excess, 0, 1e-3)
        {
        }

        /// <summary>
        /// Initializes a tolerable-risk criterion.
        /// </summary>
        /// <param name="measure">The scalar risk measure compared against the threshold.</param>
        /// <param name="riskType">The system stream the measure reads.</param>
        /// <param name="consequenceTypeIndex">The consequence-type position (0 = the primary type; k ≥ 1 = declared additional type k).</param>
        /// <param name="threshold">The tolerable-risk threshold, in the measure's own units. Must be finite.</param>
        public TolerableRiskCriterion(RiskMeasure measure, RiskType riskType, int consequenceTypeIndex, double threshold)
        {
            Measure = measure;
            RiskType = riskType;
            ConsequenceTypeIndex = consequenceTypeIndex;
            Threshold = threshold;
        }

        /// <summary>
        /// Restores a criterion from its serialized form. Missing attributes fall back to the
        /// guideline default construction, so older forms load forward.
        /// </summary>
        /// <param name="xElement">The serialized form produced by <see cref="ToXElement"/>.</param>
        /// <exception cref="ArgumentNullException">Thrown when the element is null.</exception>
        public TolerableRiskCriterion(XElement xElement)
        {
            if (xElement == null) throw new ArgumentNullException(nameof(xElement));
            Measure = SerializationUtilities.ReadEnum(xElement, nameof(Measure), RiskMeasure.Mean);
            RiskType = SerializationUtilities.ReadEnum(xElement, nameof(RiskType), RiskType.Excess);
            ConsequenceTypeIndex = SerializationUtilities.ReadInt32(xElement, nameof(ConsequenceTypeIndex), 0);
            Threshold = SerializationUtilities.ReadDouble(xElement, nameof(Threshold), 1e-3);
        }

        #endregion

        #region Members

        /// <summary>
        /// The scalar risk measure compared against the threshold.
        /// </summary>
        public RiskMeasure Measure { get; }

        /// <summary>
        /// The system stream the measure reads.
        /// </summary>
        public RiskType RiskType { get; }

        /// <summary>
        /// The consequence-type position: 0 reads the primary type, and k ≥ 1 reads declared
        /// additional type k. Bounds against the declared axis are validated at the analysis
        /// level, where the declaration lives.
        /// </summary>
        public int ConsequenceTypeIndex { get; }

        /// <summary>
        /// The tolerable-risk threshold, in the measure's own units. The reported confidence is
        /// the realization-weight fraction whose measure is strictly greater than this value.
        /// </summary>
        public double Threshold { get; }

        #endregion

        #region IModel Methods

        /// <summary>
        /// Validates the criterion: recognized measure and stream members, a non-negative
        /// consequence-type position, and a finite threshold.
        /// </summary>
        /// <returns>The validity flag and messages.</returns>
        public (bool IsValid, List<string> ValidationMessages) Validate()
        {
            var messages = new List<string>();
            if (!Enum.IsDefined(Measure))
                messages.Add("Error: The tolerable-risk measure is not a recognized member.");
            if (!Enum.IsDefined(RiskType))
                messages.Add("Error: The tolerable-risk stream is not a recognized member.");
            if (ConsequenceTypeIndex < 0)
                messages.Add("Error: The tolerable-risk consequence-type position must not be negative.");
            if (double.IsNaN(Threshold) || double.IsInfinity(Threshold))
                messages.Add("Error: The tolerable-risk threshold must be finite.");
            return (messages.Count == 0, messages);
        }

        #endregion

        #region Serialization

        /// <summary>
        /// Serializes the criterion. The element and attribute names are append-only contract,
        /// and the measure and stream serialize by enum name.
        /// </summary>
        /// <returns>The serialized form.</returns>
        public XElement ToXElement()
        {
            var element = new XElement(nameof(TolerableRiskCriterion));
            element.SetAttributeValue(nameof(Measure), Measure.ToString());
            element.SetAttributeValue(nameof(RiskType), RiskType.ToString());
            element.SetAttributeValue(nameof(ConsequenceTypeIndex), ConsequenceTypeIndex);
            element.SetAttributeValue(nameof(Threshold), SerializationUtilities.FormatDouble(Threshold));
            return element;
        }

        #endregion
    }
}
