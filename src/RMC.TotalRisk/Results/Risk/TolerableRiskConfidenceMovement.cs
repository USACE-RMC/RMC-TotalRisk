using System;
using System.Collections.Generic;

namespace RMC.TotalRisk.Results.Risk
{
    /// <summary>
    /// The expected movement of one tolerable-risk confidence statement under partial perfect
    /// information: for each knowledge input, the weighted expected absolute change of the
    /// criterion's exceedance probability were that input resolved — the decision-relevant
    /// answer to "would this study move the finding".
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The perfect-information ceiling is 2·p·(1−p) for baseline confidence p: full resolution
    /// collapses every realization's exceedance indicator to 0 or 1, and the expected absolute
    /// movement of the statement is exactly twice the Bernoulli variance. Entry movements are
    /// estimated by the same equal-weight binning as the variance decomposition and are
    /// bounded above by the ceiling.
    /// </para>
    /// </remarks>
    public sealed class TolerableRiskConfidenceMovement
    {
        /// <summary>
        /// Initializes a movement block for one criterion.
        /// </summary>
        /// <param name="measure">The criterion's measure name (the enum-name echo). Null coerces to empty.</param>
        /// <param name="riskType">The criterion's stream name (the enum-name echo). Null coerces to empty.</param>
        /// <param name="consequenceTypeIndex">The criterion's consequence-type position (0 is the primary).</param>
        /// <param name="threshold">The criterion threshold.</param>
        /// <param name="baselineExceedanceProbability">The published exceedance probability P(measure &gt; threshold).</param>
        /// <param name="perfectInformationMovement">The 2·p·(1−p) ceiling for the baseline p.</param>
        /// <param name="entryMovements">The per-input expected movements, aligned with the query's entries.</param>
        /// <exception cref="ArgumentNullException">Thrown when the movement list is null.</exception>
        public TolerableRiskConfidenceMovement(string? measure, string? riskType, int consequenceTypeIndex,
            double threshold, double baselineExceedanceProbability, double perfectInformationMovement,
            IReadOnlyList<double> entryMovements)
        {
            Measure = measure ?? string.Empty;
            RiskType = riskType ?? string.Empty;
            ConsequenceTypeIndex = consequenceTypeIndex;
            Threshold = threshold;
            BaselineExceedanceProbability = baselineExceedanceProbability;
            PerfectInformationMovement = perfectInformationMovement;
            if (entryMovements == null) throw new ArgumentNullException(nameof(entryMovements));
            EntryMovements = Array.AsReadOnly(new List<double>(entryMovements).ToArray());
        }

        /// <summary>
        /// The criterion's scalar measure, echoed by enum name.
        /// </summary>
        public string Measure { get; }

        /// <summary>
        /// The criterion's risk-type stream, echoed by enum name.
        /// </summary>
        public string RiskType { get; }

        /// <summary>
        /// The criterion's consequence-type position (0 is the primary type).
        /// </summary>
        public int ConsequenceTypeIndex { get; }

        /// <summary>
        /// The criterion threshold the confidence statement is evaluated against.
        /// </summary>
        public double Threshold { get; }

        /// <summary>
        /// The baseline confidence: the realization-weight fraction whose measure strictly
        /// exceeds the threshold, matching the published summary entry.
        /// </summary>
        public double BaselineExceedanceProbability { get; }

        /// <summary>
        /// The perfect-information ceiling 2·p·(1−p): the expected absolute movement of the
        /// statement were every knowledge input resolved at once.
        /// </summary>
        public double PerfectInformationMovement { get; }

        /// <summary>
        /// The expected absolute movement of the confidence statement per knowledge input,
        /// index-aligned with the owning query's entries. NaN where an input was inestimable.
        /// </summary>
        public IReadOnlyList<double> EntryMovements { get; }
    }
}
