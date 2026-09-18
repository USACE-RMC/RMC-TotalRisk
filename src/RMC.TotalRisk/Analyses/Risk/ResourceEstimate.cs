using System;
using System.Collections.Generic;
using RMC.TotalRisk.Core.Enums;

namespace RMC.TotalRisk.Analyses.Risk
{
    /// <summary>
    /// One line of a resource estimate: what it measures, how large it is, and — when it is large
    /// enough to matter — which option to change.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// </remarks>
    public sealed class ResourceEstimateItem
    {
        /// <summary>
        /// Initializes an estimate line.
        /// </summary>
        /// <param name="label">What the line measures.</param>
        /// <param name="bytes">The estimated bytes, or zero when the line measures work rather than memory.</param>
        /// <param name="operations">The estimated operation count, or zero when the line measures memory.</param>
        /// <param name="isRetained">True when the memory lives for the whole run rather than one realization.</param>
        /// <param name="severity">The finding's severity.</param>
        /// <param name="message">The caller-facing message; names the option to change when the severity is not informational.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="bytes"/> or <paramref name="operations"/> is negative, or
        /// when <paramref name="operations"/> is not finite.
        /// </exception>
        public ResourceEstimateItem(string label, long bytes, double operations, bool isRetained,
            ResourceSeverity severity, string message)
        {
            if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
            if (!double.IsFinite(operations) || operations < 0d)
                throw new ArgumentOutOfRangeException(nameof(operations));
            Label = label ?? string.Empty;
            Bytes = bytes;
            Operations = operations;
            IsRetained = isRetained;
            Severity = severity;
            Message = message ?? string.Empty;
        }

        /// <summary>What the line measures.</summary>
        public string Label { get; }

        /// <summary>The estimated bytes, or zero when the line measures work.</summary>
        public long Bytes { get; }

        /// <summary>The estimated operation count, or zero when the line measures memory.</summary>
        public double Operations { get; }

        /// <summary>True when the memory lives for the whole run rather than one realization.</summary>
        public bool IsRetained { get; }

        /// <summary>The finding's severity.</summary>
        public ResourceSeverity Severity { get; }

        /// <summary>The caller-facing message.</summary>
        public string Message { get; }
    }

    /// <summary>
    /// What a run is expected to cost before it starts: the memory it will hold and the work that
    /// dominates it.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// These are peak LIVE figures, not total allocation. A long ensemble run churns far more
    /// memory than it holds at any instant, so a measured allocation counter will read much higher
    /// than <see cref="PeakLiveBytes"/> and the two are not comparable.
    /// </para>
    /// </remarks>
    public sealed class ResourceEstimate
    {
        /// <summary>
        /// Initializes an estimate from its lines.
        /// </summary>
        /// <param name="items">The estimate lines.</param>
        /// <param name="concurrency">The number of realizations expected to be in flight at once.</param>
        /// <param name="integrandEvaluationFloor">The lower bound on integrand evaluations for the run.</param>
        /// <param name="genzEvaluations">The estimated multivariate-normal rectangle evaluations for the run.</param>
        /// <exception cref="ArgumentNullException">Thrown when the item list is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the evaluation floor is invalid.</exception>
        public ResourceEstimate(IReadOnlyList<ResourceEstimateItem> items, int concurrency,
            double integrandEvaluationFloor, double genzEvaluations)
            : this(items, concurrency, integrandEvaluationFloor, integrandEvaluationFloor, genzEvaluations)
        {
        }

        /// <summary>Initializes an estimate with an explicit adaptive-work range.</summary>
        /// <param name="items">The estimate lines.</param>
        /// <param name="concurrency">The number of realizations expected to be in flight at once.</param>
        /// <param name="integrandEvaluationFloor">The lower bound on integrand evaluations.</param>
        /// <param name="integrandEvaluationCeiling">The upper bound on integrand evaluations.</param>
        /// <param name="genzEvaluations">The estimated multivariate-normal rectangle evaluations.</param>
        /// <exception cref="ArgumentNullException">Thrown when the item list is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when an evaluation bound is invalid.</exception>
        public ResourceEstimate(IReadOnlyList<ResourceEstimateItem> items, int concurrency,
            double integrandEvaluationFloor, double integrandEvaluationCeiling, double genzEvaluations)
        {
            if (items == null) throw new ArgumentNullException(nameof(items));
            if (concurrency < 1) throw new ArgumentOutOfRangeException(nameof(concurrency));
            if (!double.IsFinite(integrandEvaluationFloor) || integrandEvaluationFloor < 0d)
                throw new ArgumentOutOfRangeException(nameof(integrandEvaluationFloor));
            if (!double.IsFinite(integrandEvaluationCeiling) || integrandEvaluationCeiling < integrandEvaluationFloor)
                throw new ArgumentOutOfRangeException(nameof(integrandEvaluationCeiling));
            if (!double.IsFinite(genzEvaluations) || genzEvaluations < 0d)
                throw new ArgumentOutOfRangeException(nameof(genzEvaluations));
            Items = Array.AsReadOnly(new List<ResourceEstimateItem>(items).ToArray());
            Concurrency = concurrency;
            EstimatedIntegrandEvaluationFloor = integrandEvaluationFloor;
            EstimatedIntegrandEvaluationCeiling = integrandEvaluationCeiling;
            EstimatedGenzEvaluations = genzEvaluations;

            long retained = 0;
            long transient = 0;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].IsRetained) retained = SaturatingAdd(retained, items[i].Bytes);
                else transient = SaturatingAdd(transient, items[i].Bytes);
            }
            RetainedBytes = retained;
            PeakTransientBytes = transient;
            PeakLiveBytes = SaturatingAdd(retained, transient);
        }

        /// <summary>The estimate lines, in reporting order.</summary>
        public IReadOnlyList<ResourceEstimateItem> Items { get; }

        /// <summary>The number of realizations expected to be in flight at once.</summary>
        public int Concurrency { get; }

        /// <summary>Memory held for the whole run — principally the stored ensemble.</summary>
        public long RetainedBytes { get; }

        /// <summary>Memory held by the realizations in flight.</summary>
        public long PeakTransientBytes { get; }

        /// <summary>The estimated peak live memory.</summary>
        public long PeakLiveBytes { get; }

        /// <summary>
        /// A lower bound on integrand evaluations for the run. A floor, not a bound: adaptive
        /// refinement is limited only by <see cref="RiskAnalysisOptions.MaxEvaluations"/>.
        /// </summary>
        public double EstimatedIntegrandEvaluationFloor { get; }

        /// <summary>
        /// The upper bound on integrand evaluations after adaptive caps and fixed endpoint work.
        /// Equal to the floor when the integration schedule is fixed.
        /// </summary>
        public double EstimatedIntegrandEvaluationCeiling { get; }

        /// <summary>
        /// The estimated multivariate-normal rectangle evaluations, which dependent competing-risk
        /// components incur while building their incidence functions.
        /// </summary>
        public double EstimatedGenzEvaluations { get; }

        /// <summary>
        /// The highest severity across the estimate's lines.
        /// </summary>
        public ResourceSeverity Severity
        {
            get
            {
                var worst = ResourceSeverity.Informational;
                for (int i = 0; i < Items.Count; i++)
                {
                    if (Items[i].Severity > worst) worst = Items[i].Severity;
                }
                return worst;
            }
        }

        /// <summary>
        /// The messages of every line at or above the given severity.
        /// </summary>
        /// <param name="minimum">The lowest severity to report.</param>
        /// <returns>The matching messages, in reporting order.</returns>
        public List<string> Messages(ResourceSeverity minimum)
        {
            var messages = new List<string>();
            for (int i = 0; i < Items.Count; i++)
            {
                if (Items[i].Severity >= minimum) messages.Add(Items[i].Message);
            }
            return messages;
        }

        /// <summary>Adds nonnegative byte estimates without wrapping.</summary>
        /// <param name="left">The first byte count.</param>
        /// <param name="right">The second byte count.</param>
        /// <returns>The sum, saturated at <see cref="long.MaxValue"/>.</returns>
        private static long SaturatingAdd(long left, long right)
        {
            if (left < 0 || right < 0) return long.MaxValue;
            return left > long.MaxValue - right ? long.MaxValue : left + right;
        }
    }
}
