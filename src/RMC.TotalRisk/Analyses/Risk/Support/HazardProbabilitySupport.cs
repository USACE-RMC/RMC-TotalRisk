using System;
using System.Collections.Generic;
using Numerics.Sampling;

namespace RMC.TotalRisk.Analyses.Risk.Support
{
    /// <summary>
    /// Validated natural probability support for a sampled hazard and the shared endpoint-completion
    /// rule (RMC-TotalRisk Technical Reference Manual, Appendix D) used by recorded risk integration
    /// and scalar probability probes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The adaptive integrator owns only the natural interior support. This type adds the lower and
    /// upper endpoint rectangles without stretching an interior panel and derives the final upper mass
    /// as the residual needed to make the probability ledger exactly exhaustive.
    /// </para>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// </remarks>
    internal readonly struct HazardProbabilitySupport
    {
        /// <summary>
        /// Initializes a validated support.
        /// </summary>
        /// <param name="lower">The lower natural non-exceedance probability.</param>
        /// <param name="upper">The upper natural non-exceedance probability.</param>
        private HazardProbabilitySupport(double lower, double upper)
        {
            Lower = lower;
            Upper = upper;
        }

        /// <summary>
        /// Gets the lower natural non-exceedance probability.
        /// </summary>
        internal double Lower { get; }

        /// <summary>
        /// Gets the upper natural non-exceedance probability.
        /// </summary>
        internal double Upper { get; }

        /// <summary>
        /// Gets the natural interior probability width.
        /// </summary>
        internal double InteriorMass => Upper - Lower;

        /// <summary>
        /// Reads and validates the natural probability support spanned by an ordered stratification.
        /// </summary>
        /// <param name="bins">The ordered probability-space bins.</param>
        /// <returns>The validated support.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the bins are empty, non-finite, outside [0, 1], reversed, or out of order.
        /// </exception>
        internal static HazardProbabilitySupport Create(IReadOnlyList<StratificationBin> bins)
        {
            if (bins == null || bins.Count == 0)
            {
                throw new InvalidOperationException("The sampled hazard produced no probability-space stratification bins.");
            }

            double lower = bins[0].LowerBound;
            double upper = bins[bins.Count - 1].UpperBound;
            if (!double.IsFinite(lower) || !double.IsFinite(upper) || lower < 0d || upper > 1d || upper < lower)
            {
                throw new InvalidOperationException("The sampled hazard probability support [" + lower.ToString("R")
                    + ", " + upper.ToString("R") + "] is invalid.");
            }

            double previousUpper = lower;
            for (int i = 0; i < bins.Count; i++)
            {
                if (!double.IsFinite(bins[i].LowerBound) || !double.IsFinite(bins[i].UpperBound)
                    || bins[i].LowerBound < lower || bins[i].UpperBound > upper
                    || bins[i].UpperBound < bins[i].LowerBound || bins[i].LowerBound < previousUpper)
                {
                    throw new InvalidOperationException("Probability stratification bin " + i
                        + " is invalid or out of order.");
                }
                previousUpper = bins[i].UpperBound;
            }

            return new HazardProbabilitySupport(lower, upper);
        }

        /// <summary>
        /// Validates the accepted interior quadrature mass against the natural support width.
        /// </summary>
        /// <param name="recordedInteriorMass">The compensated mass recorded by accepted nodes.</param>
        /// <param name="context">The object description included in a failure message.</param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the recorded interior mass differs materially from the natural support width.
        /// </exception>
        internal void ValidateInteriorMass(double recordedInteriorMass, string context)
        {
            if (!double.IsFinite(recordedInteriorMass)
                || Math.Abs(recordedInteriorMass - InteriorMass) > 1e-9 * Math.Max(InteriorMass, 1e-12))
            {
                throw new InvalidOperationException("The interior quadrature weights recorded for " + context
                    + " sum to " + recordedInteriorMass.ToString("R") + " instead of the natural support width "
                    + InteriorMass.ToString("R") + ". The recorded risk-point set cannot be trusted.");
            }
        }

        /// <summary>
        /// Evaluates and records the endpoint rectangles (Technical Reference Manual, Appendix D),
        /// reusing one evaluation when both
        /// tails meet at the same supported hazard.
        /// </summary>
        /// <param name="recordedInteriorMass">The accepted interior-node mass before endpoint completion.</param>
        /// <param name="evaluate">The complete integrand evaluation at a supported probability.</param>
        /// <param name="record">The sink receiving probability, mass, and evaluated value.</param>
        /// <param name="context">The object description included in a failure message.</param>
        /// <returns>The number of distinct endpoint evaluations performed.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="evaluate"/> or <paramref name="record"/> is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the residual upper mass disagrees with the natural upper tail.
        /// </exception>
        internal int CompleteExhaustive(double recordedInteriorMass, Func<double, double> evaluate,
            Action<double, double, double> record, string context)
        {
            ArgumentNullException.ThrowIfNull(evaluate);
            ArgumentNullException.ThrowIfNull(record);
            ValidateInteriorMass(recordedInteriorMass, context);

            int evaluations = 0;
            bool lowerEvaluated = false;
            double lowerValue = 0d;
            double completedMass = recordedInteriorMass;

            if (Lower > 0d)
            {
                lowerValue = evaluate(Lower);
                lowerEvaluated = true;
                evaluations++;
                record(Lower, Lower, lowerValue);
                completedMass += Lower;
            }

            double expectedUpperMass = 1d - Upper;
            double upperMass = 1d - completedMass;
            if (!double.IsFinite(upperMass) || Math.Abs(upperMass - expectedUpperMass) > 1e-12)
            {
                throw new InvalidOperationException("The upper endpoint mass for " + context + " is "
                    + upperMass.ToString("R") + " instead of " + expectedUpperMass.ToString("R")
                    + ". The exhaustive probability budget cannot be trusted.");
            }

            if (upperMass > 0d)
            {
                double upperValue;
                if (lowerEvaluated && Upper == Lower)
                {
                    upperValue = lowerValue;
                }
                else
                {
                    upperValue = evaluate(Upper);
                    evaluations++;
                }
                record(Upper, upperMass, upperValue);
            }

            return evaluations;
        }
    }
}
