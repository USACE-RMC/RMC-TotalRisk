using System;
using System.Collections.Generic;

namespace RMC.TotalRisk.Core.Support
{
    /// <summary>
    /// The raw material of one discovered logic-tree axis: an epistemic-mixture composite's
    /// identity (its shared variable name when bound, its function id when unbound) and its
    /// declared entry weights, collected by the walked clusters' cycle-safe recursions and
    /// aggregated by the analysis into the enumeration axes.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// A bound composite contributes to (or must agree with) the axis of its variable name — one
    /// axis per distinct name, however many binders exist — while every unbound epistemic
    /// composite is its own axis. Weight vectors are compared bitwise across a variable's binders:
    /// disagreement is recorded rather than resolved, because exact enumeration needs one
    /// well-defined branch set per axis.
    /// </para>
    /// </remarks>
    internal sealed class LogicTreeAxisSeed
    {
        /// <summary>
        /// Initializes a discovered axis seed.
        /// </summary>
        /// <param name="variable">The shared variable name; empty for an unbound composite.</param>
        /// <param name="functionId">The declaring composite's id (the first binder's, for a bound axis).</param>
        /// <param name="functionName">The declaring composite's name, for messages and labels.</param>
        /// <param name="weights">The composite's declared entry weights, in entry order.</param>
        private LogicTreeAxisSeed(string variable, Guid functionId, string functionName, double[] weights)
        {
            Variable = variable;
            FunctionId = functionId;
            FunctionName = functionName;
            Weights = weights;
        }

        /// <summary>
        /// The shared epistemic variable name, or empty when the axis is a single unbound
        /// composite.
        /// </summary>
        internal string Variable { get; }

        /// <summary>
        /// The declaring composite's function id — the id-keyed forcing target for an unbound
        /// axis, and the first binder's id for a bound one.
        /// </summary>
        internal Guid FunctionId { get; }

        /// <summary>
        /// The declaring composite's name, used in axis labels and diagnostics.
        /// </summary>
        internal string FunctionName { get; }

        /// <summary>
        /// The declared entry weights in entry order, zero-weight entries included (the branch
        /// set is the positively weighted subset).
        /// </summary>
        internal double[] Weights { get; }

        /// <summary>
        /// Registers one epistemic composite into the discovery sinks: a bound composite merges
        /// into (or bitwise-verifies against) its variable's axis, an unbound one becomes its own
        /// axis, and every registration records the composite's id for the pin-conflict gate.
        /// </summary>
        /// <param name="variable">The composite's variable name (empty when unbound).</param>
        /// <param name="functionId">The composite's id.</param>
        /// <param name="functionName">The composite's name.</param>
        /// <param name="weights">The composite's declared entry weights, in entry order.</param>
        /// <param name="boundAxes">The bound axes, keyed by variable name.</param>
        /// <param name="unboundAxes">The unbound axes, in discovery order.</param>
        /// <param name="mismatchedVariables">The sink recording variables whose binders declare
        /// differing weight vectors.</param>
        /// <param name="epistemicFunctionIds">The sink recording every epistemic composite id.</param>
        internal static void Register(string variable, Guid functionId, string functionName, double[] weights,
            IDictionary<string, LogicTreeAxisSeed> boundAxes, IList<LogicTreeAxisSeed> unboundAxes,
            ISet<string> mismatchedVariables, ISet<Guid> epistemicFunctionIds)
        {
            epistemicFunctionIds.Add(functionId);
            if (variable.Length == 0)
            {
                unboundAxes.Add(new LogicTreeAxisSeed(variable, functionId, functionName, weights));
                return;
            }
            if (boundAxes.TryGetValue(variable, out var existing))
            {
                if (!WeightsMatch(existing.Weights, weights)) mismatchedVariables.Add(variable);
                return;
            }
            boundAxes.Add(variable, new LogicTreeAxisSeed(variable, functionId, functionName, weights));
        }

        /// <summary>
        /// Compares this seed's declared weight vector bitwise against another's — the agreement
        /// rule for equal-id self-contained copies of one composite.
        /// </summary>
        /// <param name="other">The other seed.</param>
        /// <returns>True when the declared vectors agree bitwise.</returns>
        internal bool WeightsMatch(LogicTreeAxisSeed other)
        {
            return WeightsMatch(Weights, other.Weights);
        }

        /// <summary>
        /// Compares two declared weight vectors bitwise — the agreement rule for a shared
        /// variable's binders.
        /// </summary>
        /// <param name="first">The first vector.</param>
        /// <param name="second">The second vector.</param>
        /// <returns>True when the vectors have equal length and bitwise-equal entries.</returns>
        private static bool WeightsMatch(double[] first, double[] second)
        {
            if (first.Length != second.Length) return false;
            for (int i = 0; i < first.Length; i++)
            {
                if (!first[i].Equals(second[i])) return false;
            }
            return true;
        }
    }
}
