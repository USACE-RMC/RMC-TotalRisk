using System;
using System.Collections.Generic;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results;

namespace RMC.TotalRisk.Analyses.CostBenefit.Support
{
    /// <summary>
    /// The k-objective weak-dominance screen and the incremental cost-effectiveness table
    /// over an assembled metric matrix.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Dominance is direction-aware and weak: an alternative is removed only when another is
    /// at least as good on every objective and strictly better on one, so exact ties are both
    /// kept. A NaN objective value excludes the alternative from comparison with a flag,
    /// never a silent drop. The incremental table walks the cost-ranked non-dominated set;
    /// its cost-per-life-saved column is the discrete trade-off ratio, computed by the sweep
    /// engine's one shared helper so the two views agree bit-exactly. The engine reads plain
    /// arrays so it can be driven from any assembled matrix.
    /// </para>
    /// </remarks>
    internal static class ParetoFrontierEngine
    {
        /// <summary>
        /// Screens the non-dominated set: direction-aware weak dominance over the included
        /// alternatives, exact ties both kept.
        /// </summary>
        /// <param name="values">The objective values (one row per alternative).</param>
        /// <param name="directions">The objective directions.</param>
        /// <param name="excluded">The per-alternative exclusion flags (excluded rows neither dominate nor survive).</param>
        /// <returns>The per-alternative non-dominance flags (false for excluded rows).</returns>
        /// <exception cref="ArgumentNullException">Thrown when a required argument is null.</exception>
        internal static bool[] NonDominated(double[][] values, IReadOnlyList<ObjectiveDirection> directions,
            bool[] excluded)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            if (directions == null) throw new ArgumentNullException(nameof(directions));
            if (excluded == null) throw new ArgumentNullException(nameof(excluded));
            int count = values.Length;
            var nonDominated = new bool[count];
            for (int i = 0; i < count; i++)
            {
                if (excluded[i]) continue;
                bool dominated = false;
                for (int j = 0; j < count && !dominated; j++)
                {
                    if (j == i || excluded[j]) continue;
                    dominated = Dominates(values[j], values[i], directions);
                }
                nonDominated[i] = !dominated;
            }
            return nonDominated;
        }

        /// <summary>
        /// Builds the incremental cost-effectiveness table along the cost-ranked included
        /// alternatives: cost and benefit increments, the incremental benefit-cost ratio, and
        /// the incremental cost per statistical life saved (the discrete trade-off ratio).
        /// </summary>
        /// <param name="names">The alternative names, in results row order.</param>
        /// <param name="costs">The per-alternative cost values (the ranking axis).</param>
        /// <param name="benefits">The per-alternative monetized benefit values.</param>
        /// <param name="livesSaved">The per-alternative lives-saved values.</param>
        /// <param name="include">The per-alternative inclusion flags (the non-dominated set).</param>
        /// <returns>The incremental steps, one per successive pair.</returns>
        /// <exception cref="ArgumentNullException">Thrown when a required argument is null.</exception>
        /// <exception cref="ArgumentException">Thrown when a list does not parallel the names.</exception>
        internal static List<IncrementalEntry> IncrementalAnalysis(IReadOnlyList<string> names,
            IReadOnlyList<double> costs, IReadOnlyList<double> benefits, IReadOnlyList<double> livesSaved,
            bool[] include)
        {
            if (names == null) throw new ArgumentNullException(nameof(names));
            if (costs == null) throw new ArgumentNullException(nameof(costs));
            if (benefits == null) throw new ArgumentNullException(nameof(benefits));
            if (livesSaved == null) throw new ArgumentNullException(nameof(livesSaved));
            if (include == null) throw new ArgumentNullException(nameof(include));
            if (costs.Count != names.Count || benefits.Count != names.Count
                || livesSaved.Count != names.Count || include.Length != names.Count)
            {
                throw new ArgumentException("Every per-alternative list must parallel the names.", nameof(names));
            }

            // The cost-ranked walk skips rows whose cost cannot rank.
            var order = new List<int>();
            for (int i = 0; i < names.Count; i++)
            {
                if (include[i] && !double.IsNaN(costs[i])) order.Add(i);
            }
            order.Sort((a, b) =>
            {
                int byCost = costs[a].CompareTo(costs[b]);
                return byCost != 0 ? byCost : a.CompareTo(b);
            });

            var entries = new List<IncrementalEntry>(Math.Max(0, order.Count - 1));
            for (int k = 1; k < order.Count; k++)
            {
                int from = order[k - 1];
                int to = order[k];
                double deltaCost = costs[to] - costs[from];
                double deltaBenefit = benefits[to] - benefits[from];
                double incrementalRatio = deltaCost > 0d ? deltaBenefit / deltaCost : double.NaN;
                double deltaLives = livesSaved[to] - livesSaved[from];
                double incrementalCostPerLife = deltaLives > 0d
                    ? EpsilonSweepEngine.TradeOffRatio(costs[from], costs[to], -livesSaved[from], -livesSaved[to])
                    : double.NaN;
                entries.Add(new IncrementalEntry(names[from], names[to], deltaCost, deltaBenefit,
                    incrementalRatio, incrementalCostPerLife));
            }
            return entries;
        }

        /// <summary>
        /// True when the first vector weakly dominates the second: at least as good on every
        /// objective under its direction, strictly better on at least one.
        /// </summary>
        /// <param name="a">The candidate dominator.</param>
        /// <param name="b">The candidate dominated.</param>
        /// <param name="directions">The objective directions.</param>
        /// <returns>True on dominance.</returns>
        private static bool Dominates(double[] a, double[] b, IReadOnlyList<ObjectiveDirection> directions)
        {
            bool strict = false;
            for (int j = 0; j < directions.Count; j++)
            {
                bool atLeast = directions[j] == ObjectiveDirection.Maximize ? a[j] >= b[j] : a[j] <= b[j];
                if (!atLeast) return false;
                if (directions[j] == ObjectiveDirection.Maximize ? a[j] > b[j] : a[j] < b[j]) strict = true;
            }
            return strict;
        }
    }
}
