using System;

namespace RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Support
{
    /// <summary>
    /// Signals that binary-decision-diagram construction exceeded its configured node budget.
    /// The caller composes the user-facing remediation message; exact evaluation is never
    /// silently replaced by an approximation.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// </remarks>
    internal sealed class FaultTreeBddBudgetException : InvalidOperationException
    {
        /// <summary>Initializes the budget failure.</summary>
        /// <param name="observedNodeCount">The decision-node count at the moment of failure.</param>
        /// <param name="configuredLimit">The configured decision-node budget.</param>
        internal FaultTreeBddBudgetException(int observedNodeCount, int configuredLimit)
            : base($"Binary-decision-diagram construction exceeded the configured budget: " +
                  $"{observedNodeCount} decision nodes were created and the limit is {configuredLimit}.")
        {
            ObservedNodeCount = observedNodeCount;
            ConfiguredLimit = configuredLimit;
        }

        /// <summary>The decision-node count at the moment of failure.</summary>
        internal int ObservedNodeCount { get; }

        /// <summary>The configured decision-node budget.</summary>
        internal int ConfiguredLimit { get; }
    }
}
