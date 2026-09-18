using System;
using System.Collections.Generic;

namespace RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Support
{
    /// <summary>
    /// The immutable root-reachable form of a compiled fault-tree decision diagram. Nodes are
    /// stored children-before-parents, so exact top-event probability is one forward pass of
    /// <c>p(node) = (1 - p) * p(low) + p * p(high)</c> — linear in live nodes with no allocation
    /// inside the loop. Instances are safe for concurrent read-only evaluation because callers
    /// own their scratch arrays.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// </remarks>
    internal sealed class FrozenFaultTreeBdd
    {
        /// <summary>Initializes the frozen diagram from compacted builder arrays.</summary>
        /// <param name="variable">The per-node variable ordinals; terminals carry the sentinel.</param>
        /// <param name="low">The per-node variable-false child indexes.</param>
        /// <param name="high">The per-node variable-true child indexes.</param>
        /// <param name="root">The root node index.</param>
        /// <param name="variableCount">The fixed variable-universe size.</param>
        internal FrozenFaultTreeBdd(int[] variable, int[] low, int[] high, int root, int variableCount)
        {
            _variable = variable;
            _low = low;
            _high = high;
            Root = root;
            VariableCount = variableCount;
        }

        /// <summary>The per-node variable ordinals; terminals carry the sentinel.</summary>
        private readonly int[] _variable;

        /// <summary>The per-node variable-false child indexes.</summary>
        private readonly int[] _low;

        /// <summary>The per-node variable-true child indexes.</summary>
        private readonly int[] _high;

        /// <summary>The root node index.</summary>
        internal int Root { get; }

        /// <summary>The fixed variable-universe size.</summary>
        internal int VariableCount { get; }

        /// <summary>The live node count, including the two terminals.</summary>
        internal int NodeCount => _variable.Length;

        /// <summary>The live decision-node count, excluding the two terminals.</summary>
        internal int DecisionNodeCount => _variable.Length - 2;

        /// <summary>Evaluates the exact top-event probability for one variable-probability vector.</summary>
        /// <param name="probabilities">One probability per variable ordinal.</param>
        /// <param name="scratch">A caller-owned buffer of at least <see cref="NodeCount"/> entries.</param>
        /// <returns>The exact top-event probability.</returns>
        internal double Evaluate(double[] probabilities, double[] scratch)
        {
            scratch[FaultTreeBdd.FalseNode] = 0d;
            scratch[FaultTreeBdd.TrueNode] = 1d;
            for (int node = 2; node < _variable.Length; node++)
            {
                double p = probabilities[_variable[node]];
                scratch[node] = (1d - p) * scratch[_low[node]] + p * scratch[_high[node]];
            }
            return scratch[Root];
        }

        /// <summary>
        /// Extracts the minimal cut sets of a coherent function by the standard decision-diagram
        /// recursion: the cut sets of a node are the low-branch sets plus each high-branch set
        /// extended by the node's variable, keeping an extended set only when no low-branch set is
        /// contained in it. The caller must ensure the function is coherent; this inspection
        /// result is never the probability algorithm.
        /// </summary>
        /// <param name="maxCutSets">The loud extraction bound.</param>
        /// <returns>
        /// The minimal cut sets as ascending variable-ordinal arrays, ordered by cardinality and
        /// then lexicographically.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the bound is not positive.</exception>
        /// <exception cref="InvalidOperationException">Thrown when extraction exceeds the bound.</exception>
        internal IReadOnlyList<int[]> ExtractMinimalCutSets(int maxCutSets)
        {
            if (maxCutSets < 1) throw new ArgumentOutOfRangeException(nameof(maxCutSets));
            var memo = new List<int[]>?[_variable.Length];
            memo[FaultTreeBdd.FalseNode] = new List<int[]>();
            memo[FaultTreeBdd.TrueNode] = new List<int[]> { Array.Empty<int>() };
            List<int[]> rootSets = CollectCutSets(Root, memo, maxCutSets);

            var ordered = new List<int[]>(rootSets);
            ordered.Sort(CompareCutSets);
            return ordered;
        }

        /// <summary>Collects one node's minimal cut sets bottom-up with per-node memoization.</summary>
        /// <param name="node">The node index.</param>
        /// <param name="memo">The per-node memo table.</param>
        /// <param name="maxCutSets">The loud extraction bound.</param>
        /// <returns>The node's minimal cut sets.</returns>
        /// <exception cref="InvalidOperationException">Thrown when extraction exceeds the bound.</exception>
        private List<int[]> CollectCutSets(int node, List<int[]>?[] memo, int maxCutSets)
        {
            List<int[]>? existing = memo[node];
            if (existing != null) return existing;

            List<int[]> lowSets = CollectCutSets(_low[node], memo, maxCutSets);
            List<int[]> highSets = CollectCutSets(_high[node], memo, maxCutSets);
            var result = new List<int[]>(lowSets.Count);
            result.AddRange(lowSets);
            int variable = _variable[node];
            for (int i = 0; i < highSets.Count; i++)
            {
                int[] extended = ExtendSorted(highSets[i], variable);
                if (IsSupersetOfAny(extended, lowSets)) continue;
                result.Add(extended);
                if (result.Count > maxCutSets)
                {
                    throw new InvalidOperationException(
                        $"Minimal cut-set extraction exceeded the configured bound: more than {maxCutSets} " +
                        "cut sets were collected. Raise the bound to inspect this coherent tree; the exact " +
                        "decision-diagram probability does not depend on cut sets.");
                }
            }
            memo[node] = result;
            return result;
        }

        /// <summary>Inserts one variable into an ascending ordinal array.</summary>
        /// <param name="sorted">The ascending source array.</param>
        /// <param name="variable">The variable ordinal to insert.</param>
        /// <returns>The extended ascending array.</returns>
        private static int[] ExtendSorted(int[] sorted, int variable)
        {
            var extended = new int[sorted.Length + 1];
            int index = 0;
            while (index < sorted.Length && sorted[index] < variable)
            {
                extended[index] = sorted[index];
                index++;
            }
            extended[index] = variable;
            for (int i = index; i < sorted.Length; i++) extended[i + 1] = sorted[i];
            return extended;
        }

        /// <summary>Checks whether any candidate set is contained in one extended set.</summary>
        /// <param name="extended">The ascending extended set.</param>
        /// <param name="candidates">The ascending candidate sets.</param>
        /// <returns>True when a candidate is a subset of the extended set.</returns>
        private static bool IsSupersetOfAny(int[] extended, List<int[]> candidates)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                if (IsSubset(candidates[i], extended)) return true;
            }
            return false;
        }

        /// <summary>Checks ascending-array containment.</summary>
        /// <param name="subset">The ascending candidate subset.</param>
        /// <param name="superset">The ascending candidate superset.</param>
        /// <returns>True when every subset member appears in the superset.</returns>
        private static bool IsSubset(int[] subset, int[] superset)
        {
            int j = 0;
            for (int i = 0; i < subset.Length; i++)
            {
                while (j < superset.Length && superset[j] < subset[i]) j++;
                if (j == superset.Length || superset[j] != subset[i]) return false;
                j++;
            }
            return true;
        }

        /// <summary>Orders cut sets by cardinality, then lexicographic ordinal sequence.</summary>
        /// <param name="left">The first cut set.</param>
        /// <param name="right">The second cut set.</param>
        /// <returns>The comparison result.</returns>
        private static int CompareCutSets(int[] left, int[] right)
        {
            if (left.Length != right.Length) return left.Length.CompareTo(right.Length);
            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i]) return left[i].CompareTo(right[i]);
            }
            return 0;
        }
    }
}
