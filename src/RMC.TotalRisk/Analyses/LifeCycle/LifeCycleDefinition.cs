using System;
using System.Collections.Generic;
using System.Linq;
using Numerics;

namespace RMC.TotalRisk.Analyses.LifeCycle
{
    /// <summary>
    /// The definition of a life-cycle evaluation: a planning horizon, a discount rate, the
    /// evaluation years that refine the trajectory, and the ordered intervention schedule.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Runtime-only input — never serialized or hashed; the query derives its epoch boundaries as
    /// the sorted distinct union of year zero, the evaluation years, and the intervention years.
    /// Annual risk is stepwise-constant within an epoch, so extra evaluation years refine a
    /// trajectory that deterioration makes continuous; duplicate evaluation years are absorbed by
    /// the union, while duplicate intervention years are refused — one intervention entry carries
    /// all of a year's actions. Every year lies in [0, periodYears − 1]: a year equal to the
    /// horizon would open a zero-length epoch.
    /// </para>
    /// </remarks>
    public sealed class LifeCycleDefinition
    {
        /// <summary>
        /// Initializes a life-cycle definition.
        /// </summary>
        /// <param name="periodYears">The planning horizon in years (at least one).</param>
        /// <param name="discountRate">The annual discount rate (0 = undiscounted; finite and non-negative).</param>
        /// <param name="evaluationYears">Additional epoch start years refining the trajectory; null or empty for none.</param>
        /// <param name="interventions">The intervention schedule; null or empty for none.</param>
        /// <param name="retainEpochRealizations">
        /// True to keep each epoch's mean realization on its trajectory row (with the per-sample
        /// workspace dropped), so every curve measure is readable per epoch and per stream.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown for a non-positive horizon, an invalid discount rate, or a year outside
        /// [0, periodYears − 1].
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when an intervention entry is null or two interventions share a year.
        /// </exception>
        public LifeCycleDefinition(int periodYears, double discountRate = 0d,
            IReadOnlyList<int>? evaluationYears = null,
            IReadOnlyList<LifeCycleIntervention>? interventions = null,
            bool retainEpochRealizations = false)
        {
            if (periodYears < 1)
                throw new ArgumentOutOfRangeException(nameof(periodYears), "The exposure period must be at least one year.");
            if (!Tools.IsFinite(discountRate) || discountRate < 0d)
                throw new ArgumentOutOfRangeException(nameof(discountRate), "The discount rate must be finite and non-negative.");
            PeriodYears = periodYears;
            DiscountRate = discountRate;

            var yearSnapshot = evaluationYears == null ? Array.Empty<int>() : evaluationYears.ToArray();
            for (int i = 0; i < yearSnapshot.Length; i++)
            {
                if (yearSnapshot[i] < 0 || yearSnapshot[i] >= periodYears)
                    throw new ArgumentOutOfRangeException(nameof(evaluationYears),
                        $"The evaluation year {yearSnapshot[i]} lies outside [0, {periodYears - 1}].");
            }

            var interventionSnapshot = interventions == null
                ? Array.Empty<LifeCycleIntervention>()
                : interventions.ToArray();
            var seenYears = new HashSet<int>();
            for (int i = 0; i < interventionSnapshot.Length; i++)
            {
                var intervention = interventionSnapshot[i]
                    ?? throw new ArgumentException("The intervention list contains a null entry.", nameof(interventions));
                if (intervention.Year >= periodYears)
                    throw new ArgumentOutOfRangeException(nameof(interventions),
                        $"The intervention year {intervention.Year} lies outside [0, {periodYears - 1}].");
                if (!seenYears.Add(intervention.Year))
                    throw new ArgumentException(
                        $"Two interventions share year {intervention.Year}; carry all of a year's actions on one intervention entry.",
                        nameof(interventions));
            }

            EvaluationYears = Array.AsReadOnly(yearSnapshot);
            Interventions = Array.AsReadOnly(interventionSnapshot);
            RetainEpochRealizations = retainEpochRealizations;
        }

        /// <summary>
        /// The planning horizon in years; exposure years run 1 through this value.
        /// </summary>
        public int PeriodYears { get; }

        /// <summary>
        /// The annual discount rate (0 = undiscounted).
        /// </summary>
        public double DiscountRate { get; }

        /// <summary>
        /// Additional epoch start years refining the trajectory, as supplied (the query sorts and
        /// deduplicates); year zero is always an epoch start.
        /// </summary>
        public IReadOnlyList<int> EvaluationYears { get; }

        /// <summary>
        /// The intervention schedule, as supplied (the query applies entries cumulatively in
        /// ascending year order); at most one entry per year.
        /// </summary>
        public IReadOnlyList<LifeCycleIntervention> Interventions { get; }

        /// <summary>
        /// True to keep each epoch's mean realization on its trajectory row — the per-epoch
        /// measure surface (curve scalars and the thinned exceedance curves; the per-sample
        /// workspace is dropped). False (the default) discards each epoch's realization after
        /// its row is built.
        /// </summary>
        public bool RetainEpochRealizations { get; }
    }
}
