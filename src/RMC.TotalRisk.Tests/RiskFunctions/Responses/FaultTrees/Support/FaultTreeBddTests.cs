using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Support;

namespace RMC.TotalRisk.Tests.RiskFunctions.Responses.FaultTrees.Support;

/// <summary>Tests the internal fault-tree decision-diagram kernel against exhaustive enumeration.</summary>
[TestClass]
public class FaultTreeBddTests
{
    /// <summary>The default kernel budget used by tests that never approach it.</summary>
    private const int TestNodeLimit = 100000;

    /// <summary>
    /// Verifies exact probability and Boolean parity between the diagram and exhaustive
    /// truth-table enumeration over fixed-seed random formula corpora with shared variables,
    /// constants, negation, exclusive disjunction, and thresholds.
    /// </summary>
    [TestMethod]
    public void Test_RandomFormulaCorpus_MatchesExhaustiveEnumeration()
    {
        // Arrange
        int[] seeds = { 730101, 730102 };
        const int formulasPerSeed = 24;
        const int variableCount = 8;

        foreach (int seed in seeds)
        {
            var random = new Random(seed);
            for (int formula = 0; formula < formulasPerSeed; formula++)
            {
                var bdd = new FaultTreeBdd(variableCount, TestNodeLimit);
                Expression expression = GenerateExpression(random, variableCount, depth: 4);
                int root = expression.Build(bdd);
                FrozenFaultTreeBdd frozen = bdd.Freeze(root);
                var probabilities = new double[variableCount];
                for (int i = 0; i < variableCount; i++) probabilities[i] = random.NextDouble();
                var scratch = new double[frozen.NodeCount];
                var states = new bool[variableCount];
                var indicator = new double[variableCount];

                // Act
                double actual = frozen.Evaluate(probabilities, scratch);
                double expected = 0d;
                for (int assignment = 0; assignment < 1 << variableCount; assignment++)
                {
                    double weight = 1d;
                    for (int i = 0; i < variableCount; i++)
                    {
                        states[i] = (assignment & (1 << i)) != 0;
                        indicator[i] = states[i] ? 1d : 0d;
                        weight *= states[i] ? probabilities[i] : 1d - probabilities[i];
                    }
                    bool truth = expression.Evaluate(states);
                    if (truth) expected += weight;

                    // Assert Boolean parity exactly: 0/1 probabilities select branches without rounding.
                    Assert.AreEqual(truth ? 1d : 0d, frozen.Evaluate(indicator, scratch),
                        $"Boolean parity failed for seed {seed}, formula {formula}, assignment {assignment}.");
                }

                // Assert
                Assert.AreEqual(expected, actual, 1e-13,
                    $"Probability parity failed for seed {seed}, formula {formula}.");
            }
        }
    }

    /// <summary>Verifies structurally equal functions share one canonical node.</summary>
    [TestMethod]
    public void Test_Canonicity_EquivalentBuildsShareOneNode()
    {
        // Arrange
        var bdd = new FaultTreeBdd(3, TestNodeLimit);
        int a = bdd.Variable(0);
        int b = bdd.Variable(1);
        int c = bdd.Variable(2);

        // Act
        int direct = bdd.And(a, b);
        int deMorgan = bdd.Not(bdd.Or(bdd.Not(a), bdd.Not(b)));
        int distributedLeft = bdd.Or(bdd.And(a, b), bdd.And(a, c));
        int distributedRight = bdd.And(a, bdd.Or(b, c));

        // Assert
        Assert.AreEqual(direct, deMorgan);
        Assert.AreEqual(distributedLeft, distributedRight);
    }

    /// <summary>Verifies repeated shared variables reduce idempotently.</summary>
    [TestMethod]
    public void Test_SharedVariableIdempotence_ReducesExactly()
    {
        // Arrange
        var bdd = new FaultTreeBdd(2, TestNodeLimit);
        int a = bdd.Variable(0);

        // Act / Assert
        Assert.AreEqual(a, bdd.And(a, a));
        Assert.AreEqual(a, bdd.Or(a, a));
        Assert.AreEqual(FaultTreeBdd.FalseNode, bdd.Xor(a, a));
    }

    /// <summary>Verifies constants fold before any expansion.</summary>
    [TestMethod]
    public void Test_ConstantFolding_ShortCircuits()
    {
        // Arrange
        var bdd = new FaultTreeBdd(2, TestNodeLimit);
        int a = bdd.Variable(0);
        int b = bdd.Variable(1);
        int f = bdd.Or(a, b);

        // Act / Assert
        Assert.AreEqual(FaultTreeBdd.FalseNode, bdd.And(f, bdd.Constant(false)));
        Assert.AreEqual(FaultTreeBdd.TrueNode, bdd.Or(f, bdd.Constant(true)));
        Assert.AreEqual(f, bdd.And(f, bdd.Constant(true)));
        Assert.AreEqual(f, bdd.Or(f, bdd.Constant(false)));
        Assert.AreEqual(f, bdd.Not(bdd.Not(f)));
    }

    /// <summary>Verifies the threshold construction against the binomial tail and gate degeneracies.</summary>
    [TestMethod]
    public void Test_KOfN_MatchesBinomialTailAndDegenerateGates()
    {
        // Arrange
        const int inputCount = 5;
        const double probability = 0.3d;
        var bdd = new FaultTreeBdd(inputCount, TestNodeLimit);
        var inputs = new int[inputCount];
        for (int i = 0; i < inputCount; i++) inputs[i] = bdd.Variable(i);
        var probabilities = new double[inputCount];
        for (int i = 0; i < inputCount; i++) probabilities[i] = probability;

        // Act / Assert: exact binomial tail at every threshold.
        for (int k = 1; k <= inputCount; k++)
        {
            int threshold = bdd.KOfN(inputs, k);
            FrozenFaultTreeBdd frozen = bdd.Freeze(threshold);
            double expected = 0d;
            for (int successes = k; successes <= inputCount; successes++)
            {
                expected += Combinations(inputCount, successes)
                    * Math.Pow(probability, successes)
                    * Math.Pow(1d - probability, inputCount - successes);
            }
            Assert.AreEqual(expected, frozen.Evaluate(probabilities, new double[frozen.NodeCount]), 1e-14,
                $"Binomial tail mismatch at k = {k}.");
        }

        // Assert degeneracies: 1-of-n is the disjunction and n-of-n the conjunction, node-identical.
        int orFold = inputs[0];
        int andFold = inputs[0];
        for (int i = 1; i < inputCount; i++)
        {
            orFold = bdd.Or(orFold, inputs[i]);
            andFold = bdd.And(andFold, inputs[i]);
        }
        Assert.AreEqual(orFold, bdd.KOfN(inputs, 1));
        Assert.AreEqual(andFold, bdd.KOfN(inputs, inputCount));

        // Assert exactness under repeated shared inputs: 2-of-[a, a, b] is a OR (a AND b) = a.
        int shared = bdd.KOfN(new[] { inputs[0], inputs[0], inputs[1] }, 2);
        Assert.AreEqual(inputs[0], shared);
    }

    /// <summary>Verifies a fixed build produces deterministic pinned node counts.</summary>
    [TestMethod]
    public void Test_FixedBuild_NodeCountsDeterministic()
    {
        // Arrange: 2-of-3 over distinct variables is the textbook five-decision-node diagram.
        var bdd = new FaultTreeBdd(3, TestNodeLimit);
        int root = bdd.KOfN(new[] { bdd.Variable(0), bdd.Variable(1), bdd.Variable(2) }, 2);

        // Act
        FrozenFaultTreeBdd frozen = bdd.Freeze(root);

        // Assert: the threshold dynamic program creates nine deterministic decision nodes in the
        // builder (literals, partial thresholds, and the unused final 1-of-n row), while the
        // frozen root-reachable majority diagram keeps exactly 4 decision nodes plus 2 terminals.
        Assert.AreEqual(4, frozen.DecisionNodeCount);
        Assert.AreEqual(6, frozen.NodeCount);
        Assert.AreEqual(9, bdd.DecisionNodeCount);
    }

    /// <summary>Verifies the loud decision-node budget failure carries both counts.</summary>
    [TestMethod]
    public void Test_NodeBudget_ThrowsLoudlyWithCounts()
    {
        // Arrange
        var bdd = new FaultTreeBdd(4, 3);

        // Act
        var exception = Assert.ThrowsException<FaultTreeBddBudgetException>(() =>
        {
            int f = bdd.Variable(0);
            for (int i = 1; i < 4; i++) f = bdd.Xor(f, bdd.Variable(i));
        });

        // Assert
        Assert.AreEqual(4, exception.ObservedNodeCount);
        Assert.AreEqual(3, exception.ConfiguredLimit);
        StringAssert.Contains(exception.Message, "decision nodes were created");
    }

    /// <summary>Verifies minimal cut sets for hand-derived coherent structures.</summary>
    [TestMethod]
    public void Test_MinimalCutSets_MatchHandDerivedSets()
    {
        // Arrange
        var bdd = new FaultTreeBdd(3, TestNodeLimit);
        int a = bdd.Variable(0);
        int b = bdd.Variable(1);
        int c = bdd.Variable(2);

        // Act / Assert: series, parallel, mixed, absorbed, and threshold structures.
        AssertCutSets(bdd.Freeze(bdd.And(a, bdd.And(b, c))), new[] { new[] { 0, 1, 2 } });
        AssertCutSets(bdd.Freeze(bdd.Or(a, bdd.Or(b, c))),
            new[] { new[] { 0 }, new[] { 1 }, new[] { 2 } });
        AssertCutSets(bdd.Freeze(bdd.Or(bdd.And(a, b), bdd.And(a, c))),
            new[] { new[] { 0, 1 }, new[] { 0, 2 } });
        AssertCutSets(bdd.Freeze(bdd.Or(bdd.And(a, b), a)), new[] { new[] { 0 } });
        AssertCutSets(bdd.Freeze(bdd.KOfN(new[] { a, b, c }, 2)),
            new[] { new[] { 0, 1 }, new[] { 0, 2 }, new[] { 1, 2 } });
    }

    /// <summary>Verifies cut-set extraction faults loudly at its configured bound.</summary>
    [TestMethod]
    public void Test_MinimalCutSets_BoundOverflowThrows()
    {
        // Arrange
        var bdd = new FaultTreeBdd(3, TestNodeLimit);
        int root = bdd.Or(bdd.Variable(0), bdd.Or(bdd.Variable(1), bdd.Variable(2)));
        FrozenFaultTreeBdd frozen = bdd.Freeze(root);

        // Act
        var exception = Assert.ThrowsException<InvalidOperationException>(
            () => frozen.ExtractMinimalCutSets(2));

        // Assert
        StringAssert.Contains(exception.Message, "cut-set extraction exceeded the configured bound");
    }

    /// <summary>Verifies freezing keeps only the root-reachable subgraph.</summary>
    [TestMethod]
    public void Test_Freeze_DropsUnreachableNodes()
    {
        // Arrange: build a large unrelated function first, then freeze a single literal.
        var bdd = new FaultTreeBdd(6, TestNodeLimit);
        int unrelated = bdd.Variable(1);
        for (int i = 2; i < 6; i++) unrelated = bdd.Xor(unrelated, bdd.Variable(i));
        int root = bdd.Variable(0);

        // Act
        FrozenFaultTreeBdd frozen = bdd.Freeze(root);

        // Assert
        Assert.IsTrue(bdd.DecisionNodeCount > 3);
        Assert.AreEqual(1, frozen.DecisionNodeCount);
        Assert.AreEqual(3, frozen.NodeCount);
        double[] scratch = new double[frozen.NodeCount];
        Assert.AreEqual(0.25d, frozen.Evaluate(new[] { 0.25d, 0d, 0d, 0d, 0d, 0d }, scratch), 0d);
    }

    /// <summary>Asserts one frozen diagram's cut sets equal an expected ordered listing.</summary>
    /// <param name="frozen">The frozen diagram.</param>
    /// <param name="expected">The expected ordered cut sets.</param>
    private static void AssertCutSets(FrozenFaultTreeBdd frozen, int[][] expected)
    {
        IReadOnlyList<int[]> actual = frozen.ExtractMinimalCutSets(10000);
        Assert.AreEqual(expected.Length, actual.Count);
        for (int i = 0; i < expected.Length; i++)
            CollectionAssert.AreEqual(expected[i], actual[i]);
    }

    /// <summary>Computes an exact small binomial coefficient.</summary>
    /// <param name="n">The population size.</param>
    /// <param name="k">The selection size.</param>
    /// <returns>The coefficient.</returns>
    private static double Combinations(int n, int k)
    {
        double result = 1d;
        for (int i = 0; i < k; i++) result = result * (n - i) / (i + 1);
        return result;
    }

    /// <summary>Generates one random expression exercising every kernel operation.</summary>
    /// <param name="random">The fixed-seed generator.</param>
    /// <param name="variableCount">The variable-universe size.</param>
    /// <param name="depth">The remaining nesting depth.</param>
    /// <returns>The generated expression.</returns>
    private static Expression GenerateExpression(Random random, int variableCount, int depth)
    {
        int choice = depth <= 0 ? random.Next(2) : random.Next(8);
        switch (choice)
        {
            case 0:
                return new Expression(ExpressionKind.Variable, random.Next(variableCount), null);
            case 1:
                return new Expression(ExpressionKind.Constant, random.Next(2), null);
            case 2:
                return new Expression(ExpressionKind.Not, 0,
                    new[] { GenerateExpression(random, variableCount, depth - 1) });
            case 3:
            case 4:
                return new Expression(choice == 3 ? ExpressionKind.And : ExpressionKind.Or, 0,
                    GenerateOperands(random, variableCount, depth, random.Next(2, 4)));
            case 5:
                return new Expression(ExpressionKind.Xor, 0,
                    GenerateOperands(random, variableCount, depth, 2));
            default:
                Expression[] operands = GenerateOperands(random, variableCount, depth, random.Next(2, 5));
                return new Expression(ExpressionKind.KOfN, random.Next(1, operands.Length + 1), operands);
        }
    }

    /// <summary>Generates one operand list.</summary>
    /// <param name="random">The fixed-seed generator.</param>
    /// <param name="variableCount">The variable-universe size.</param>
    /// <param name="depth">The remaining nesting depth.</param>
    /// <param name="count">The operand count.</param>
    /// <returns>The operands.</returns>
    private static Expression[] GenerateOperands(Random random, int variableCount, int depth, int count)
    {
        var operands = new Expression[count];
        for (int i = 0; i < count; i++)
            operands[i] = GenerateExpression(random, variableCount, depth - 1);
        return operands;
    }

    /// <summary>The independent-expression node kinds.</summary>
    private enum ExpressionKind
    {
        /// <summary>A positive variable literal.</summary>
        Variable,

        /// <summary>A Boolean constant.</summary>
        Constant,

        /// <summary>A negation.</summary>
        Not,

        /// <summary>A conjunction.</summary>
        And,

        /// <summary>A disjunction.</summary>
        Or,

        /// <summary>A two-input exclusive disjunction.</summary>
        Xor,

        /// <summary>A k-of-n threshold.</summary>
        KOfN,
    }

    /// <summary>One independent expression node evaluated without the kernel under test.</summary>
    private sealed class Expression
    {
        /// <summary>Initializes one expression node.</summary>
        /// <param name="kind">The node kind.</param>
        /// <param name="value">The variable ordinal, constant flag, or threshold.</param>
        /// <param name="operands">The child expressions.</param>
        internal Expression(ExpressionKind kind, int value, Expression[]? operands)
        {
            Kind = kind;
            Value = value;
            Operands = operands ?? Array.Empty<Expression>();
        }

        /// <summary>The node kind.</summary>
        private ExpressionKind Kind { get; }

        /// <summary>The variable ordinal, constant flag, or threshold.</summary>
        private int Value { get; }

        /// <summary>The child expressions.</summary>
        private Expression[] Operands { get; }

        /// <summary>Builds this expression in the kernel under test.</summary>
        /// <param name="bdd">The diagram builder.</param>
        /// <returns>The built node index.</returns>
        internal int Build(FaultTreeBdd bdd)
        {
            switch (Kind)
            {
                case ExpressionKind.Variable:
                    return bdd.Variable(Value);
                case ExpressionKind.Constant:
                    return bdd.Constant(Value == 1);
                case ExpressionKind.Not:
                    return bdd.Not(Operands[0].Build(bdd));
                case ExpressionKind.And:
                {
                    int result = Operands[0].Build(bdd);
                    for (int i = 1; i < Operands.Length; i++) result = bdd.And(result, Operands[i].Build(bdd));
                    return result;
                }
                case ExpressionKind.Or:
                {
                    int result = Operands[0].Build(bdd);
                    for (int i = 1; i < Operands.Length; i++) result = bdd.Or(result, Operands[i].Build(bdd));
                    return result;
                }
                case ExpressionKind.Xor:
                    return bdd.Xor(Operands[0].Build(bdd), Operands[1].Build(bdd));
                default:
                    return bdd.KOfN(Operands.Select(operand => operand.Build(bdd)).ToArray(), Value);
            }
        }

        /// <summary>Evaluates this expression for one truth assignment.</summary>
        /// <param name="states">The per-variable states.</param>
        /// <returns>The truth value.</returns>
        internal bool Evaluate(bool[] states)
        {
            switch (Kind)
            {
                case ExpressionKind.Variable:
                    return states[Value];
                case ExpressionKind.Constant:
                    return Value == 1;
                case ExpressionKind.Not:
                    return !Operands[0].Evaluate(states);
                case ExpressionKind.And:
                    return Operands.All(operand => operand.Evaluate(states));
                case ExpressionKind.Or:
                    return Operands.Any(operand => operand.Evaluate(states));
                case ExpressionKind.Xor:
                    return Operands[0].Evaluate(states) ^ Operands[1].Evaluate(states);
                default:
                    return Operands.Count(operand => operand.Evaluate(states)) >= Value;
            }
        }
    }
}
