using System;
using System.Collections.Generic;
using System.Linq;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Results.LifeCycle
{
    /// <summary>
    /// One epoch of a life-cycle trajectory: the years it spans, the evaluation age its
    /// deteriorating responses held, the cumulative failure probability through its end, the
    /// system and per-component scope rows, and the configuration labels in effect.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// A plain query result — never serialized. Annual risk is stepwise-constant within the
    /// epoch: the row's values hold for exposure years StartYear + 1 through EndYear.
    /// </para>
    /// </remarks>
    public sealed class LifeCycleEpochRisk
    {
        /// <summary>
        /// Initializes one epoch row.
        /// </summary>
        /// <param name="startYear">The epoch's start year (an offset from now; 0 = the horizon start).</param>
        /// <param name="spanYears">The number of exposure years the epoch covers (at least one).</param>
        /// <param name="evaluationAge">The age deteriorating responses evaluated at (the start year in this convention).</param>
        /// <param name="cumulativeFailureProbability">P(at least one failure by the epoch's end year).</param>
        /// <param name="system">The system scope row.</param>
        /// <param name="components">The per-component scope rows in declared component order.</param>
        /// <param name="appliedActions">The configuration labels in effect during the epoch.</param>
        /// <param name="realization">The epoch's retained mean realization, or null when not retained.</param>
        /// <exception cref="ArgumentNullException">Thrown when a required row or list is null.</exception>
        public LifeCycleEpochRisk(int startYear, int spanYears, double evaluationAge,
            double cumulativeFailureProbability, LifeCycleEpochEntry system,
            IReadOnlyList<LifeCycleEpochEntry> components, IReadOnlyList<string> appliedActions,
            SystemRealization? realization = null)
        {
            StartYear = startYear;
            SpanYears = spanYears;
            EvaluationAge = evaluationAge;
            CumulativeFailureProbability = cumulativeFailureProbability;
            System = system ?? throw new ArgumentNullException(nameof(system));
            if (components == null) throw new ArgumentNullException(nameof(components));
            Components = Array.AsReadOnly(components.ToArray());
            if (appliedActions == null) throw new ArgumentNullException(nameof(appliedActions));
            AppliedActions = Array.AsReadOnly(appliedActions.ToArray());
            Realization = realization;
        }

        /// <summary>
        /// The epoch's start year — an offset from now; 0 is the horizon start.
        /// </summary>
        public int StartYear { get; }

        /// <summary>
        /// The number of exposure years the epoch covers.
        /// </summary>
        public int SpanYears { get; }

        /// <summary>
        /// The epoch's end year: the last exposure year its values govern.
        /// </summary>
        public int EndYear => StartYear + SpanYears;

        /// <summary>
        /// The age the epoch's deteriorating responses evaluated at — the start year under the
        /// stepwise-constant convention.
        /// </summary>
        public double EvaluationAge { get; }

        /// <summary>
        /// The probability of at least one system failure by the epoch's end year, accumulated in
        /// log space across the trajectory.
        /// </summary>
        public double CumulativeFailureProbability { get; }

        /// <summary>
        /// The system scope row.
        /// </summary>
        public LifeCycleEpochEntry System { get; }

        /// <summary>
        /// The per-component scope rows, in declared component order.
        /// </summary>
        public IReadOnlyList<LifeCycleEpochEntry> Components { get; }

        /// <summary>
        /// The configuration labels in effect during the epoch — every applied house-event state
        /// and hazard replacement, in application order.
        /// </summary>
        public IReadOnlyList<string> AppliedActions { get; }

        /// <summary>
        /// The epoch's retained mean realization — every curve measure readable per stream, with
        /// the per-sample workspace dropped — or null when the definition did not retain
        /// realizations.
        /// </summary>
        public SystemRealization? Realization { get; }
    }
}
