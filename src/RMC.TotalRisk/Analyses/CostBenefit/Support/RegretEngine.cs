using System;
using System.Collections.Generic;
using System.Globalization;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Analyses.CostBenefit.Support
{
    /// <summary>
    /// The shared-state regret machinery: the state-alignment precondition check between two
    /// logic-tree enumeration maps and the pure hand-matrix regret computation over aligned
    /// block means.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// A shared state is one logic-tree branch combination present with bitwise-equal weight
    /// in every compared alternative's enumeration map at equal block size — anything less is
    /// a different epistemic state space, and comparing regrets across different state spaces
    /// would be fiction, so misalignment is diagnosed per alternative rather than papered
    /// over. An unbound-composite axis is keyed to one analysis instance and aligns only when
    /// the alternatives literally share that instance. Regret is direction-aware against the
    /// per-state best value; a state where any alternative carries no finite block mean is
    /// dropped with a named diagnostic; expected regret uses the exact retained state weights
    /// unrenormalized; and per-state winners credit exact ties to every tied alternative.
    /// Sums accumulate sequentially in state order so oracles can mirror the arithmetic
    /// bit for bit.
    /// </para>
    /// </remarks>
    internal static class RegretEngine
    {
        /// <summary>
        /// One criterion's regret computation over the surviving aligned states.
        /// </summary>
        /// <remarks>
        /// <para>
        ///     <b>Authors:</b>
        ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
        /// </para>
        /// <para>
        /// A plain internal carrier: the wiring derives the ranking picks through the ranking
        /// core so one authority owns tie-breaking, then assembles the published container.
        /// </para>
        /// </remarks>
        internal sealed class RegretComputation
        {
            /// <summary>Initializes the carrier.</summary>
            /// <param name="stateLabels">The surviving state labels.</param>
            /// <param name="stateWeights">The surviving exact state weights.</param>
            /// <param name="values">The block-mean matrix over surviving states.</param>
            /// <param name="standardErrors">The block standard-error matrix over surviving states.</param>
            /// <param name="regrets">The regret matrix over surviving states.</param>
            /// <param name="maxRegrets">The per-alternative maximum regrets.</param>
            /// <param name="expectedRegrets">The per-alternative expected regrets.</param>
            /// <param name="winCounts">The per-alternative per-state win counts.</param>
            internal RegretComputation(string[] stateLabels, double[] stateWeights, double[][] values,
                double[][] standardErrors, double[][] regrets, double[] maxRegrets,
                double[] expectedRegrets, int[] winCounts)
            {
                StateLabels = stateLabels;
                StateWeights = stateWeights;
                Values = values;
                StandardErrors = standardErrors;
                Regrets = regrets;
                MaxRegrets = maxRegrets;
                ExpectedRegrets = expectedRegrets;
                WinCounts = winCounts;
            }

            /// <summary>The surviving state labels.</summary>
            internal string[] StateLabels { get; }

            /// <summary>The surviving exact state weights, unrenormalized.</summary>
            internal double[] StateWeights { get; }

            /// <summary>The block-mean matrix (one row per alternative).</summary>
            internal double[][] Values { get; }

            /// <summary>The block standard-error matrix, congruent to the values.</summary>
            internal double[][] StandardErrors { get; }

            /// <summary>The regret matrix, congruent to the values.</summary>
            internal double[][] Regrets { get; }

            /// <summary>The per-alternative maximum regrets.</summary>
            internal double[] MaxRegrets { get; }

            /// <summary>The per-alternative expected regrets under the exact state weights.</summary>
            internal double[] ExpectedRegrets { get; }

            /// <summary>The per-alternative per-state win counts (exact ties credited to all).</summary>
            internal int[] WinCounts { get; }
        }

        /// <summary>
        /// Describes why a candidate's enumeration map does not align with the baseline's, or
        /// null when the shared-state space is identical.
        /// </summary>
        /// <param name="baseline">The baseline's enumeration map.</param>
        /// <param name="candidate">The candidate's enumeration map, or null when absent.</param>
        /// <param name="sharesBaselineInstance">True when the candidate literally shares the baseline's analysis instance.</param>
        /// <returns>The misalignment reason sentence, or null when aligned.</returns>
        internal static string? DescribeMisalignment(LogicTreeEnumerationMap baseline,
            LogicTreeEnumerationMap? candidate, bool sharesBaselineInstance)
        {
            if (sharesBaselineInstance) return null;
            if (candidate == null)
            {
                return "the alternative carries no logic-tree enumeration map.";
            }
            if (candidate.CombinationCount != baseline.CombinationCount)
            {
                return string.Create(CultureInfo.InvariantCulture,
                    $"the combination counts differ ({candidate.CombinationCount} vs the baseline's {baseline.CombinationCount}).");
            }
            if (candidate.RealizationsPerCombination != baseline.RealizationsPerCombination)
            {
                return string.Create(CultureInfo.InvariantCulture,
                    $"the realizations per combination differ ({candidate.RealizationsPerCombination} vs the baseline's {baseline.RealizationsPerCombination}).");
            }
            if (candidate.Axes.Count != baseline.Axes.Count)
            {
                return string.Create(CultureInfo.InvariantCulture,
                    $"the axis counts differ ({candidate.Axes.Count} vs the baseline's {baseline.Axes.Count}).");
            }
            for (int i = 0; i < baseline.Axes.Count; i++)
            {
                var baselineAxis = baseline.Axes[i];
                var candidateAxis = candidate.Axes[i];
                if (!string.Equals(candidateAxis.Name, baselineAxis.Name, StringComparison.Ordinal))
                {
                    return string.Create(CultureInfo.InvariantCulture,
                        $"axis {i} names differ ('{candidateAxis.Name}' vs the baseline's '{baselineAxis.Name}').");
                }
                if (candidateAxis.IsSharedVariable != baselineAxis.IsSharedVariable)
                {
                    return string.Create(CultureInfo.InvariantCulture,
                        $"axis '{baselineAxis.Name}' differs in kind (shared-variable vs unbound-composite).");
                }
                if (!baselineAxis.IsSharedVariable)
                {
                    return string.Create(CultureInfo.InvariantCulture,
                        $"axis '{baselineAxis.Name}' is an unbound composite axis, which aligns only when the alternatives share one analysis instance.");
                }
                if (candidateAxis.Branches.Count != baselineAxis.Branches.Count)
                {
                    return string.Create(CultureInfo.InvariantCulture,
                        $"axis '{baselineAxis.Name}' branch counts differ ({candidateAxis.Branches.Count} vs the baseline's {baselineAxis.Branches.Count}).");
                }
                for (int j = 0; j < baselineAxis.Branches.Count; j++)
                {
                    if (BitConverter.DoubleToInt64Bits(candidateAxis.Branches[j].Weight)
                        != BitConverter.DoubleToInt64Bits(baselineAxis.Branches[j].Weight))
                    {
                        return string.Create(CultureInfo.InvariantCulture,
                            $"axis '{baselineAxis.Name}' branch {j} weights are not bitwise equal.");
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Computes one criterion's regret matrices over aligned block means: states where any
        /// alternative carries no finite block mean are dropped with a named diagnostic, regret
        /// is direction-aware against the per-state best, and the aggregates accumulate
        /// sequentially in state order.
        /// </summary>
        /// <param name="criterionLabel">The criterion's display label (for the drop diagnostics).</param>
        /// <param name="direction">The optimization direction of the criterion.</param>
        /// <param name="names">The alternative names, in results row order.</param>
        /// <param name="stateLabels">The candidate state labels, in enumeration order.</param>
        /// <param name="stateWeights">The exact state weights, parallel to the labels.</param>
        /// <param name="blockMeans">The block-mean matrix (one row per alternative, one column per candidate state).</param>
        /// <param name="blockStandardErrors">The block standard-error matrix, congruent to the means.</param>
        /// <param name="diagnostics">The diagnostics sink for dropped states.</param>
        /// <returns>The computation over surviving states, or null when no state survives.</returns>
        internal static RegretComputation? Compute(string criterionLabel, ObjectiveDirection direction,
            IReadOnlyList<string> names, IReadOnlyList<string> stateLabels,
            IReadOnlyList<double> stateWeights, double[][] blockMeans,
            double[][] blockStandardErrors, List<ComputationDiagnostic> diagnostics)
        {
            int alternativeCount = names.Count;
            int stateCount = stateLabels.Count;
            var keep = new bool[stateCount];
            int survivingCount = 0;
            for (int s = 0; s < stateCount; s++)
            {
                bool finite = true;
                for (int a = 0; a < alternativeCount; a++)
                {
                    if (double.IsNaN(blockMeans[a][s]))
                    {
                        finite = false;
                        break;
                    }
                }
                keep[s] = finite;
                if (finite)
                {
                    survivingCount++;
                }
                else
                {
                    diagnostics.Add(new ComputationDiagnostic("TRC2009", DiagnosticSeverity.Warning,
                        $"The shared state '{stateLabels[s]}' is dropped from the regret analysis of '{criterionLabel}': an alternative carries no finite block mean there.",
                        string.Empty));
                }
            }
            if (survivingCount == 0) return null;

            var survivingLabels = new string[survivingCount];
            var survivingWeights = new double[survivingCount];
            var values = new double[alternativeCount][];
            var standardErrors = new double[alternativeCount][];
            var regrets = new double[alternativeCount][];
            for (int a = 0; a < alternativeCount; a++)
            {
                values[a] = new double[survivingCount];
                standardErrors[a] = new double[survivingCount];
                regrets[a] = new double[survivingCount];
            }
            int column = 0;
            for (int s = 0; s < stateCount; s++)
            {
                if (!keep[s]) continue;
                survivingLabels[column] = stateLabels[s];
                survivingWeights[column] = stateWeights[s];
                for (int a = 0; a < alternativeCount; a++)
                {
                    values[a][column] = blockMeans[a][s];
                    standardErrors[a][column] = blockStandardErrors[a][s];
                }
                column++;
            }

            var winCounts = new int[alternativeCount];
            for (int s = 0; s < survivingCount; s++)
            {
                double best = values[0][s];
                for (int a = 1; a < alternativeCount; a++)
                {
                    double value = values[a][s];
                    if (direction == ObjectiveDirection.Maximize ? value > best : value < best)
                    {
                        best = value;
                    }
                }
                for (int a = 0; a < alternativeCount; a++)
                {
                    regrets[a][s] = direction == ObjectiveDirection.Maximize
                        ? best - values[a][s]
                        : values[a][s] - best;
                    if (values[a][s] == best) winCounts[a]++;
                }
            }

            var maxRegrets = new double[alternativeCount];
            var expectedRegrets = new double[alternativeCount];
            for (int a = 0; a < alternativeCount; a++)
            {
                double max = 0d;
                double expected = 0d;
                for (int s = 0; s < survivingCount; s++)
                {
                    if (regrets[a][s] > max) max = regrets[a][s];
                    expected += survivingWeights[s] * regrets[a][s];
                }
                maxRegrets[a] = max;
                expectedRegrets[a] = expected;
            }
            return new RegretComputation(survivingLabels, survivingWeights, values, standardErrors,
                regrets, maxRegrets, expectedRegrets, winCounts);
        }
    }
}
