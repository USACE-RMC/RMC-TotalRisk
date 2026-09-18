using System;
using System.Collections.Generic;

namespace RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Support
{
    /// <summary>
    /// An ordered reduced binary decision diagram (ROBDD) builder over a fixed variable universe.
    /// Construction uses one memoized if-then-else kernel with a unique table, so structurally
    /// equal Boolean functions always share one node, repeated shared variables reduce
    /// idempotently, and house-event constants fold before any expansion. There are no complement
    /// edges: every node is a plain <c>(variable, low, high)</c> triple, which keeps the
    /// canonicity argument and the probability recurrence directly auditable.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Instances are single-threaded builders: compile one expression, freeze the reachable
    /// subgraph with <see cref="Freeze"/>, and discard the builder. The if-then-else recursion
    /// depth is bounded by the variable count. Exceeding the decision-node budget throws
    /// <see cref="FaultTreeBddBudgetException"/> from the exact allocation that crossed it;
    /// nothing is approximated or truncated.
    /// </para>
    /// </remarks>
    internal sealed class FaultTreeBdd
    {
        /// <summary>The constant-false terminal index.</summary>
        internal const int FalseNode = 0;

        /// <summary>The constant-true terminal index.</summary>
        internal const int TrueNode = 1;

        /// <summary>The terminal sentinel variable ordinal, above every real variable.</summary>
        private const int TerminalVariable = int.MaxValue;

        /// <summary>Initializes an empty diagram over a fixed variable universe.</summary>
        /// <param name="variableCount">The number of distinct Boolean variables.</param>
        /// <param name="nodeLimit">The decision-node budget enforced during construction.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when a count is negative or the limit is not positive.</exception>
        internal FaultTreeBdd(int variableCount, int nodeLimit)
        {
            if (variableCount < 0) throw new ArgumentOutOfRangeException(nameof(variableCount));
            if (nodeLimit < 1) throw new ArgumentOutOfRangeException(nameof(nodeLimit));
            _variableCount = variableCount;
            _nodeLimit = nodeLimit;
            _variable = new int[Math.Max(16, 2)];
            _low = new int[_variable.Length];
            _high = new int[_variable.Length];
            _variable[FalseNode] = TerminalVariable;
            _variable[TrueNode] = TerminalVariable;
            _count = 2;
        }

        /// <summary>The fixed variable-universe size.</summary>
        private readonly int _variableCount;

        /// <summary>The decision-node budget.</summary>
        private readonly int _nodeLimit;

        /// <summary>The per-node variable ordinals; terminals carry the sentinel.</summary>
        private int[] _variable;

        /// <summary>The per-node low (variable-false) child indexes.</summary>
        private int[] _low;

        /// <summary>The per-node high (variable-true) child indexes.</summary>
        private int[] _high;

        /// <summary>The used node count, including the two terminals.</summary>
        private int _count;

        /// <summary>The canonical unique table mapping one triple to its single node.</summary>
        private readonly Dictionary<BddTriple, int> _unique = new Dictionary<BddTriple, int>();

        /// <summary>The if-then-else memo table.</summary>
        private readonly Dictionary<BddTriple, int> _computed = new Dictionary<BddTriple, int>();

        /// <summary>The number of decision nodes created so far, excluding the two terminals.</summary>
        internal int DecisionNodeCount => _count - 2;

        /// <summary>Returns one variable's positive-literal node.</summary>
        /// <param name="variableOrdinal">The variable ordinal within the fixed universe.</param>
        /// <returns>The node index.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the ordinal is outside the universe.</exception>
        internal int Variable(int variableOrdinal)
        {
            if (variableOrdinal < 0 || variableOrdinal >= _variableCount)
                throw new ArgumentOutOfRangeException(nameof(variableOrdinal));
            return MakeNode(variableOrdinal, FalseNode, TrueNode);
        }

        /// <summary>Returns one Boolean constant terminal.</summary>
        /// <param name="value">The constant value.</param>
        /// <returns>The terminal index.</returns>
        internal int Constant(bool value)
        {
            return value ? TrueNode : FalseNode;
        }

        /// <summary>Builds the conjunction of two functions.</summary>
        /// <param name="f">The first operand.</param>
        /// <param name="g">The second operand.</param>
        /// <returns>The node index of <c>f AND g</c>.</returns>
        internal int And(int f, int g)
        {
            return Ite(f, g, FalseNode);
        }

        /// <summary>Builds the disjunction of two functions.</summary>
        /// <param name="f">The first operand.</param>
        /// <param name="g">The second operand.</param>
        /// <returns>The node index of <c>f OR g</c>.</returns>
        internal int Or(int f, int g)
        {
            return Ite(f, TrueNode, g);
        }

        /// <summary>Builds the negation of one function.</summary>
        /// <param name="f">The operand.</param>
        /// <returns>The node index of <c>NOT f</c>.</returns>
        internal int Not(int f)
        {
            return Ite(f, FalseNode, TrueNode);
        }

        /// <summary>Builds the exclusive disjunction of two functions.</summary>
        /// <param name="f">The first operand.</param>
        /// <param name="g">The second operand.</param>
        /// <returns>The node index of <c>f XOR g</c>.</returns>
        internal int Xor(int f, int g)
        {
            return Ite(f, Not(g), g);
        }

        /// <summary>
        /// Builds the k-of-n threshold of an input sequence by the exact if-then-else dynamic
        /// program <c>T[j] = ITE(input, T[j-1], T[j])</c>, which remains exact when inputs share
        /// variables.
        /// </summary>
        /// <param name="inputs">The input functions in canonical order.</param>
        /// <param name="k">The required count, between one and the input count.</param>
        /// <returns>The node index of the threshold function.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the input list is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="k"/> is outside <c>[1, n]</c>.</exception>
        internal int KOfN(IReadOnlyList<int> inputs, int k)
        {
            if (inputs == null) throw new ArgumentNullException(nameof(inputs));
            if (k < 1 || k > inputs.Count) throw new ArgumentOutOfRangeException(nameof(k));
            var threshold = new int[k + 1];
            threshold[0] = TrueNode;
            for (int j = 1; j <= k; j++) threshold[j] = FalseNode;
            for (int i = 0; i < inputs.Count; i++)
            {
                for (int j = k; j >= 1; j--)
                    threshold[j] = Ite(inputs[i], threshold[j - 1], threshold[j]);
            }
            return threshold[k];
        }

        /// <summary>The memoized if-then-else kernel over the shared variable order.</summary>
        /// <param name="f">The selector function.</param>
        /// <param name="g">The function selected when <paramref name="f"/> is true.</param>
        /// <param name="h">The function selected when <paramref name="f"/> is false.</param>
        /// <returns>The node index of <c>ITE(f, g, h)</c>.</returns>
        internal int Ite(int f, int g, int h)
        {
            if (f == TrueNode) return g;
            if (f == FalseNode) return h;
            if (g == h) return g;
            if (g == TrueNode && h == FalseNode) return f;

            var key = new BddTriple(f, g, h);
            if (_computed.TryGetValue(key, out int memoized)) return memoized;

            int top = Math.Min(_variable[f], Math.Min(_variable[g], _variable[h]));
            int low = Ite(Cofactor(f, top, false), Cofactor(g, top, false), Cofactor(h, top, false));
            int high = Ite(Cofactor(f, top, true), Cofactor(g, top, true), Cofactor(h, top, true));
            int result = MakeNode(top, low, high);
            _computed.Add(key, result);
            return result;
        }

        /// <summary>Extracts the root-reachable subgraph into an immutable evaluation form.</summary>
        /// <param name="root">The root node index.</param>
        /// <returns>The frozen diagram.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the root index is invalid.</exception>
        internal FrozenFaultTreeBdd Freeze(int root)
        {
            if (root < 0 || root >= _count) throw new ArgumentOutOfRangeException(nameof(root));
            var keep = new bool[_count];
            keep[FalseNode] = true;
            keep[TrueNode] = true;
            var stack = new Stack<int>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                int node = stack.Pop();
                if (keep[node]) continue;
                keep[node] = true;
                stack.Push(_low[node]);
                stack.Push(_high[node]);
            }

            var remap = new int[_count];
            int liveCount = 0;
            for (int i = 0; i < _count; i++)
            {
                if (keep[i]) remap[i] = liveCount++;
            }
            var variable = new int[liveCount];
            var low = new int[liveCount];
            var high = new int[liveCount];
            for (int i = 0; i < _count; i++)
            {
                if (!keep[i]) continue;
                int target = remap[i];
                variable[target] = _variable[i];
                low[target] = i < 2 ? target : remap[_low[i]];
                high[target] = i < 2 ? target : remap[_high[i]];
            }
            return new FrozenFaultTreeBdd(variable, low, high, remap[root], _variableCount);
        }

        /// <summary>Reads one function's cofactor with respect to the expansion variable.</summary>
        /// <param name="node">The function node.</param>
        /// <param name="variable">The expansion variable ordinal.</param>
        /// <param name="positive">Whether to take the variable-true branch.</param>
        /// <returns>The cofactor node index.</returns>
        private int Cofactor(int node, int variable, bool positive)
        {
            if (_variable[node] != variable) return node;
            return positive ? _high[node] : _low[node];
        }

        /// <summary>Returns the unique node for one triple, creating it only when necessary.</summary>
        /// <param name="variable">The decision variable ordinal.</param>
        /// <param name="low">The variable-false child.</param>
        /// <param name="high">The variable-true child.</param>
        /// <returns>The canonical node index.</returns>
        /// <exception cref="FaultTreeBddBudgetException">Thrown when a new node would exceed the budget.</exception>
        private int MakeNode(int variable, int low, int high)
        {
            if (low == high) return low;
            var key = new BddTriple(variable, low, high);
            if (_unique.TryGetValue(key, out int existing)) return existing;
            if (DecisionNodeCount >= _nodeLimit)
                throw new FaultTreeBddBudgetException(DecisionNodeCount + 1, _nodeLimit);

            if (_count == _variable.Length)
            {
                int grown = _variable.Length * 2;
                Array.Resize(ref _variable, grown);
                Array.Resize(ref _low, grown);
                Array.Resize(ref _high, grown);
            }
            int node = _count++;
            _variable[node] = variable;
            _low[node] = low;
            _high[node] = high;
            _unique.Add(key, node);
            return node;
        }

        /// <summary>One value-typed triple key for the unique and memo tables.</summary>
        /// <param name="First">The variable ordinal or if-then-else selector.</param>
        /// <param name="Second">The low child or if-then-else true branch.</param>
        /// <param name="Third">The high child or if-then-else false branch.</param>
        private readonly record struct BddTriple(int First, int Second, int Third);
    }
}
