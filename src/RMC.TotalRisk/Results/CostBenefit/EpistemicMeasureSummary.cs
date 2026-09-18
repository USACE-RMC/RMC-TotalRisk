using System;
using RMC.TotalRisk.Core.Enums;

namespace RMC.TotalRisk.Results.CostBenefit
{
    /// <summary>
    /// One alternative's weighted epistemic band for one decision criterion: the weighted
    /// mean, the band quantiles with their levels, the weighted variance, the epistemic
    /// conditional value-at-risk with its tail level, and the effective realization counts.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The band is epistemic — it spreads over the stored full-uncertainty ensemble's
    /// realization weights, not over aleatory annual variability — and the direction echo
    /// states which side the conditional value-at-risk tail was taken on. Values are NaN when
    /// the ensemble cannot support them (a single surviving realization leaves no variance
    /// estimate), and the Kish effective count states how concentrated the weights are.
    /// </para>
    /// </remarks>
    public sealed class EpistemicMeasureSummary
    {
        /// <summary>
        /// Initializes an epistemic band row.
        /// </summary>
        /// <param name="alternativeName">The alternative's name.</param>
        /// <param name="criterionLabel">The measured criterion's display label.</param>
        /// <param name="direction">The optimization direction the tail measure conditioned on.</param>
        /// <param name="weightedMean">The weighted mean.</param>
        /// <param name="lowerValue">The lower band quantile.</param>
        /// <param name="median">The weighted median.</param>
        /// <param name="upperValue">The upper band quantile.</param>
        /// <param name="lowerLevel">The lower band level.</param>
        /// <param name="upperLevel">The upper band level.</param>
        /// <param name="variance">The weighted variance (reliability weights).</param>
        /// <param name="conditionalValueAtRisk">The epistemic conditional value-at-risk (the adverse-tail weighted mean).</param>
        /// <param name="tailAlpha">The tail level of the conditional value-at-risk.</param>
        /// <param name="effectiveRealizationCount">The Kish effective realization count (Σw)²/Σw².</param>
        /// <param name="realizationCount">The surviving realization count behind the band.</param>
        /// <exception cref="ArgumentNullException">Thrown when a required label is null.</exception>
        /// <exception cref="ArgumentException">Thrown when the realization count is negative.</exception>
        public EpistemicMeasureSummary(string alternativeName, string criterionLabel,
            ObjectiveDirection direction, double weightedMean, double lowerValue, double median,
            double upperValue, double lowerLevel, double upperLevel, double variance,
            double conditionalValueAtRisk, double tailAlpha, double effectiveRealizationCount,
            int realizationCount)
        {
            AlternativeName = alternativeName ?? throw new ArgumentNullException(nameof(alternativeName));
            CriterionLabel = criterionLabel ?? throw new ArgumentNullException(nameof(criterionLabel));
            if (realizationCount < 0)
                throw new ArgumentException("The realization count cannot be negative.", nameof(realizationCount));
            Direction = direction;
            WeightedMean = weightedMean;
            LowerValue = lowerValue;
            Median = median;
            UpperValue = upperValue;
            LowerLevel = lowerLevel;
            UpperLevel = upperLevel;
            Variance = variance;
            ConditionalValueAtRisk = conditionalValueAtRisk;
            TailAlpha = tailAlpha;
            EffectiveRealizationCount = effectiveRealizationCount;
            RealizationCount = realizationCount;
        }

        /// <summary>The alternative's name.</summary>
        public string AlternativeName { get; }

        /// <summary>The measured criterion's display label.</summary>
        public string CriterionLabel { get; }

        /// <summary>The optimization direction the tail measure conditioned on.</summary>
        public ObjectiveDirection Direction { get; }

        /// <summary>The weighted mean.</summary>
        public double WeightedMean { get; }

        /// <summary>The lower band quantile.</summary>
        public double LowerValue { get; }

        /// <summary>The weighted median.</summary>
        public double Median { get; }

        /// <summary>The upper band quantile.</summary>
        public double UpperValue { get; }

        /// <summary>The lower band level.</summary>
        public double LowerLevel { get; }

        /// <summary>The upper band level.</summary>
        public double UpperLevel { get; }

        /// <summary>The weighted variance (reliability weights; NaN when unsupported).</summary>
        public double Variance { get; }

        /// <summary>The epistemic conditional value-at-risk (the adverse-tail weighted mean).</summary>
        public double ConditionalValueAtRisk { get; }

        /// <summary>The tail level of the conditional value-at-risk.</summary>
        public double TailAlpha { get; }

        /// <summary>The Kish effective realization count (Σw)²/Σw².</summary>
        public double EffectiveRealizationCount { get; }

        /// <summary>The surviving realization count behind the band.</summary>
        public int RealizationCount { get; }
    }
}
