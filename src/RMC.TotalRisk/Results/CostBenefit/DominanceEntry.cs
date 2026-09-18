using System;
using RMC.TotalRisk.Core.Enums;

namespace RMC.TotalRisk.Results.CostBenefit
{
    /// <summary>
    /// One pairwise stochastic-dominance screen result: the compared pair, the layer and
    /// criterion echoes, and the verdict.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Dominance is a screen: entries state which pairwise orderings the distributions
    /// support, and no ranking or summary row is derived from them. The layer echo separates
    /// the aleatory comparison of mean loss-exceedance curves from the epistemic comparison
    /// of weighted ensemble distributions.
    /// </para>
    /// </remarks>
    public sealed class DominanceEntry
    {
        /// <summary>
        /// Initializes a dominance-screen entry.
        /// </summary>
        /// <param name="firstAlternative">The first compared alternative's name.</param>
        /// <param name="secondAlternative">The second compared alternative's name.</param>
        /// <param name="layer">The uncertainty-layer label.</param>
        /// <param name="criterionLabel">The compared distribution's display label.</param>
        /// <param name="verdict">The pair's strongest supported ordering.</param>
        /// <exception cref="ArgumentNullException">Thrown when a required label is null.</exception>
        public DominanceEntry(string firstAlternative, string secondAlternative, string layer,
            string criterionLabel, DominanceVerdict verdict)
        {
            FirstAlternative = firstAlternative ?? throw new ArgumentNullException(nameof(firstAlternative));
            SecondAlternative = secondAlternative ?? throw new ArgumentNullException(nameof(secondAlternative));
            Layer = layer ?? throw new ArgumentNullException(nameof(layer));
            CriterionLabel = criterionLabel ?? throw new ArgumentNullException(nameof(criterionLabel));
            Verdict = verdict;
        }

        /// <summary>The first compared alternative's name.</summary>
        public string FirstAlternative { get; }

        /// <summary>The second compared alternative's name.</summary>
        public string SecondAlternative { get; }

        /// <summary>The uncertainty-layer label.</summary>
        public string Layer { get; }

        /// <summary>The compared distribution's display label.</summary>
        public string CriterionLabel { get; }

        /// <summary>The pair's strongest supported ordering.</summary>
        public DominanceVerdict Verdict { get; }
    }
}
