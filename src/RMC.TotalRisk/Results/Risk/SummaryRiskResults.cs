using System;

namespace RMC.TotalRisk.Results.Risk
{
    /// <summary>
    /// The compact summary of one loss exceedance curve: the risk-measure catalog captured as
    /// plain values.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The per-realization snapshot the ensemble keeps after the full realization is discarded
    /// (a class in v1.1 — the v1.0 struct predates the JSON results pipeline, and reference
    /// semantics let the summary tree share the serializer path of every other container).
    /// </para>
    /// </remarks>
    public sealed class SummaryRiskResults
    {
        /// <summary>
        /// Initializes an empty summary (deserialization support).
        /// </summary>
        public SummaryRiskResults()
        {
        }

        /// <summary>
        /// Captures the summary of a finished curve.
        /// </summary>
        /// <param name="curve">The curve to summarize.</param>
        /// <exception cref="ArgumentNullException">Thrown when the curve is null.</exception>
        public SummaryRiskResults(Curve curve)
        {
            if (curve == null) throw new ArgumentNullException(nameof(curve));
            TotalProbability = curve.TotalProbability;
            ConditionalMean = curve.ConditionalMean;
            Mean = curve.Mean;
            StandardDeviation = curve.StandardDeviation;
            Skewness = curve.Skewness;
            Kurtosis = curve.Kurtosis;
            ConsequenceThresholdProbability = curve.ConsequenceThresholdProbability;
            HazardThresholdProbability = curve.HazardThresholdProbability;
            ValueAtRisk = curve.ValueAtRisk;
            ConditionalValueAtRisk = curve.ConditionalValueAtRisk;
        }

        /// <summary>
        /// The curve's total probability (the annualized failure probability on the Fail stream).
        /// </summary>
        public double TotalProbability { get; set; }

        /// <summary>
        /// The conditional mean given the curve's event occurs (the η of the α-η plot).
        /// </summary>
        public double ConditionalMean { get; set; }

        /// <summary>
        /// The unconditional mean (the expected annual consequence).
        /// </summary>
        public double Mean { get; set; }

        /// <summary>
        /// The standard deviation of the loss distribution.
        /// </summary>
        public double StandardDeviation { get; set; }

        /// <summary>
        /// The normalized skewness of the loss distribution.
        /// </summary>
        public double Skewness { get; set; }

        /// <summary>
        /// The normalized kurtosis of the loss distribution.
        /// </summary>
        public double Kurtosis { get; set; }

        /// <summary>
        /// The probability the consequence exceeds the analysis consequence threshold (assurance).
        /// </summary>
        public double ConsequenceThresholdProbability { get; set; }

        /// <summary>
        /// The probability the hazard level exceeds the component hazard threshold.
        /// </summary>
        public double HazardThresholdProbability { get; set; }

        /// <summary>
        /// The consequence quantile at the analysis exceedance level α.
        /// </summary>
        public double ValueAtRisk { get; set; }

        /// <summary>
        /// The conditional value-at-risk (expected shortfall) at the analysis exceedance level α.
        /// </summary>
        public double ConditionalValueAtRisk { get; set; }
    }
}
