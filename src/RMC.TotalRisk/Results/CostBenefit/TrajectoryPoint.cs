using System;
using System.Collections.Generic;
using System.Linq;

namespace RMC.TotalRisk.Results.CostBenefit
{
    /// <summary>
    /// One plot-ready trajectory point of a cost-benefit study: an alternative's epoch — its
    /// start year and span, the annualized and cumulative failure probabilities, and the
    /// per-type expected annual consequences on the Total, Excess, and Fail streams.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// A presentation-contract row flattened from the per-alternative trajectories: annual
    /// risk is stepwise-constant within an epoch, so the point's values hold for exposure
    /// years StartYear + 1 through StartYear + SpanYears.
    /// </para>
    /// </remarks>
    public sealed class TrajectoryPoint
    {
        /// <summary>
        /// Initializes a trajectory point.
        /// </summary>
        /// <param name="alternativeName">The alternative's display name.</param>
        /// <param name="startYear">The epoch's start year (0 = the horizon start).</param>
        /// <param name="spanYears">The exposure years the epoch covers.</param>
        /// <param name="failureProbability">The epoch's annualized failure probability.</param>
        /// <param name="cumulativeFailureProbability">P(at least one failure by the epoch's end year).</param>
        /// <param name="totalExpectedConsequences">The Total-stream per-type expected annual consequences.</param>
        /// <param name="excessExpectedConsequences">The Excess-stream per-type expected annual consequences.</param>
        /// <param name="failExpectedConsequences">The Fail-stream per-type expected annual consequences.</param>
        /// <exception cref="ArgumentNullException">Thrown when the name or a consequence list is null.</exception>
        public TrajectoryPoint(string alternativeName, int startYear, int spanYears,
            double failureProbability, double cumulativeFailureProbability,
            IReadOnlyList<double> totalExpectedConsequences,
            IReadOnlyList<double> excessExpectedConsequences,
            IReadOnlyList<double> failExpectedConsequences)
        {
            AlternativeName = alternativeName ?? throw new ArgumentNullException(nameof(alternativeName));
            StartYear = startYear;
            SpanYears = spanYears;
            FailureProbability = failureProbability;
            CumulativeFailureProbability = cumulativeFailureProbability;
            if (totalExpectedConsequences == null) throw new ArgumentNullException(nameof(totalExpectedConsequences));
            if (excessExpectedConsequences == null) throw new ArgumentNullException(nameof(excessExpectedConsequences));
            if (failExpectedConsequences == null) throw new ArgumentNullException(nameof(failExpectedConsequences));
            TotalExpectedConsequences = Array.AsReadOnly(totalExpectedConsequences.ToArray());
            ExcessExpectedConsequences = Array.AsReadOnly(excessExpectedConsequences.ToArray());
            FailExpectedConsequences = Array.AsReadOnly(failExpectedConsequences.ToArray());
        }

        /// <summary>The alternative's display name.</summary>
        public string AlternativeName { get; }

        /// <summary>The epoch's start year (0 = the horizon start).</summary>
        public int StartYear { get; }

        /// <summary>The exposure years the epoch covers.</summary>
        public int SpanYears { get; }

        /// <summary>The epoch's annualized failure probability.</summary>
        public double FailureProbability { get; }

        /// <summary>The probability of at least one failure by the epoch's end year.</summary>
        public double CumulativeFailureProbability { get; }

        /// <summary>The Total-stream per-type expected annual consequences.</summary>
        public IReadOnlyList<double> TotalExpectedConsequences { get; }

        /// <summary>The Excess-stream per-type expected annual consequences.</summary>
        public IReadOnlyList<double> ExcessExpectedConsequences { get; }

        /// <summary>The Fail-stream per-type expected annual consequences.</summary>
        public IReadOnlyList<double> FailExpectedConsequences { get; }
    }
}
