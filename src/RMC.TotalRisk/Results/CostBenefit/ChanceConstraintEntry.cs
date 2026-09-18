using System;
using System.Collections.Generic;
using System.Linq;

namespace RMC.TotalRisk.Results.CostBenefit
{
    /// <summary>
    /// One declared constraint's chance evaluation over the stored epistemic ensembles: the
    /// constraint echo, the per-alternative raw threshold-exceedance fractions and
    /// satisfaction probabilities, and the verdicts at each declared confidence level.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The raw exceedance fraction is the parity surface: it is the same weighted strict
    /// exceedance the tolerable-risk confidence block publishes, reported before any
    /// complementation so floating-point rearrangement can never open a gap between the two
    /// surfaces. The satisfaction probability derives from it per the constraint's sense, and
    /// a NaN fraction (no surviving weight) never satisfies a confidence level.
    /// </para>
    /// </remarks>
    public sealed class ChanceConstraintEntry
    {
        /// <summary>
        /// Initializes a chance-constraint evaluation row.
        /// </summary>
        /// <param name="constraintLabel">The constraint's display label (metric, sense, threshold).</param>
        /// <param name="alternativeNames">The alternative names, in results row order.</param>
        /// <param name="exceedanceFractions">The raw weighted strict threshold-exceedance fractions, parallel to the names.</param>
        /// <param name="satisfactionProbabilities">The satisfaction probabilities per the constraint's sense, parallel to the names.</param>
        /// <param name="confidenceLevels">The declared confidence levels.</param>
        /// <param name="verdicts">The satisfaction verdicts (one row per confidence level, one column per alternative).</param>
        /// <exception cref="ArgumentNullException">Thrown when a required argument is null.</exception>
        /// <exception cref="ArgumentException">Thrown when a list does not parallel its axis or the verdict matrix is not rectangular.</exception>
        public ChanceConstraintEntry(string constraintLabel, IReadOnlyList<string> alternativeNames,
            IReadOnlyList<double> exceedanceFractions, IReadOnlyList<double> satisfactionProbabilities,
            IReadOnlyList<double> confidenceLevels, IReadOnlyList<IReadOnlyList<bool>> verdicts)
        {
            ConstraintLabel = constraintLabel ?? throw new ArgumentNullException(nameof(constraintLabel));
            if (alternativeNames == null) throw new ArgumentNullException(nameof(alternativeNames));
            if (exceedanceFractions == null) throw new ArgumentNullException(nameof(exceedanceFractions));
            if (satisfactionProbabilities == null) throw new ArgumentNullException(nameof(satisfactionProbabilities));
            if (confidenceLevels == null) throw new ArgumentNullException(nameof(confidenceLevels));
            if (verdicts == null) throw new ArgumentNullException(nameof(verdicts));
            if (exceedanceFractions.Count != alternativeNames.Count
                || satisfactionProbabilities.Count != alternativeNames.Count)
            {
                throw new ArgumentException("Every per-alternative list must parallel the alternative names.",
                    nameof(alternativeNames));
            }
            if (verdicts.Count != confidenceLevels.Count)
                throw new ArgumentException("The verdict matrix must carry one row per confidence level.", nameof(verdicts));
            var verdictRows = new IReadOnlyList<bool>[verdicts.Count];
            for (int i = 0; i < verdicts.Count; i++)
            {
                if (verdicts[i] == null || verdicts[i].Count != alternativeNames.Count)
                    throw new ArgumentException("Every verdict row must carry one column per alternative.", nameof(verdicts));
                verdictRows[i] = Array.AsReadOnly(verdicts[i].ToArray());
            }
            AlternativeNames = Array.AsReadOnly(alternativeNames.ToArray());
            ExceedanceFractions = Array.AsReadOnly(exceedanceFractions.ToArray());
            SatisfactionProbabilities = Array.AsReadOnly(satisfactionProbabilities.ToArray());
            ConfidenceLevels = Array.AsReadOnly(confidenceLevels.ToArray());
            Verdicts = Array.AsReadOnly(verdictRows);
        }

        /// <summary>The constraint's display label (metric, sense, threshold).</summary>
        public string ConstraintLabel { get; }

        /// <summary>The alternative names, in results row order.</summary>
        public IReadOnlyList<string> AlternativeNames { get; }

        /// <summary>
        /// The raw weighted strict threshold-exceedance fractions, parallel to the names —
        /// the parity surface shared with the tolerable-risk confidence block.
        /// </summary>
        public IReadOnlyList<double> ExceedanceFractions { get; }

        /// <summary>The satisfaction probabilities per the constraint's sense, parallel to the names.</summary>
        public IReadOnlyList<double> SatisfactionProbabilities { get; }

        /// <summary>The declared confidence levels.</summary>
        public IReadOnlyList<double> ConfidenceLevels { get; }

        /// <summary>The satisfaction verdicts (one row per confidence level, one column per alternative).</summary>
        public IReadOnlyList<IReadOnlyList<bool>> Verdicts { get; }
    }
}
