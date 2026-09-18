using System;
using System.Collections.Generic;
using System.Linq;

namespace RMC.TotalRisk.Results.CostBenefit
{
    /// <summary>
    /// The decision summary: the strategy-by-recommendation cross-tabulation, the
    /// per-alternative recommendation-count margins, and the study-global do-no-harm marks.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The summary is presentation, not a decision: strategies that disagree are the finding,
    /// and the margins simply count how often each alternative was recommended across the
    /// computed rankings (withheld rows count for no one). The do-no-harm marks restate the
    /// screen's study-global verdicts beside the margins so an excluded alternative's absence
    /// from every recommendation is legible at a glance.
    /// </para>
    /// </remarks>
    public sealed class DecisionSummary
    {
        /// <summary>
        /// Initializes a decision summary.
        /// </summary>
        /// <param name="entries">The cross-tabulation rows, one per computed ranking.</param>
        /// <param name="alternativeNames">The alternative names, in results row order.</param>
        /// <param name="recommendationCounts">The per-alternative recommendation counts, parallel to the names.</param>
        /// <param name="failsDoNoHarm">The per-alternative do-no-harm marks, parallel to the names.</param>
        /// <exception cref="ArgumentNullException">Thrown when a required list is null.</exception>
        /// <exception cref="ArgumentException">Thrown when a per-alternative list does not parallel the names.</exception>
        public DecisionSummary(IReadOnlyList<DecisionSummaryEntry> entries,
            IReadOnlyList<string> alternativeNames, IReadOnlyList<int> recommendationCounts,
            IReadOnlyList<bool> failsDoNoHarm)
        {
            if (entries == null) throw new ArgumentNullException(nameof(entries));
            if (alternativeNames == null) throw new ArgumentNullException(nameof(alternativeNames));
            if (recommendationCounts == null) throw new ArgumentNullException(nameof(recommendationCounts));
            if (failsDoNoHarm == null) throw new ArgumentNullException(nameof(failsDoNoHarm));
            if (recommendationCounts.Count != alternativeNames.Count || failsDoNoHarm.Count != alternativeNames.Count)
            {
                throw new ArgumentException("Every per-alternative list must parallel the alternative names.",
                    nameof(alternativeNames));
            }
            Entries = Array.AsReadOnly(entries.ToArray());
            AlternativeNames = Array.AsReadOnly(alternativeNames.ToArray());
            RecommendationCounts = Array.AsReadOnly(recommendationCounts.ToArray());
            FailsDoNoHarm = Array.AsReadOnly(failsDoNoHarm.ToArray());
        }

        /// <summary>The cross-tabulation rows, one per computed ranking.</summary>
        public IReadOnlyList<DecisionSummaryEntry> Entries { get; }

        /// <summary>The alternative names, in results row order.</summary>
        public IReadOnlyList<string> AlternativeNames { get; }

        /// <summary>The per-alternative recommendation counts (withheld rows count for no one).</summary>
        public IReadOnlyList<int> RecommendationCounts { get; }

        /// <summary>The per-alternative do-no-harm marks, parallel to the names.</summary>
        public IReadOnlyList<bool> FailsDoNoHarm { get; }
    }
}
