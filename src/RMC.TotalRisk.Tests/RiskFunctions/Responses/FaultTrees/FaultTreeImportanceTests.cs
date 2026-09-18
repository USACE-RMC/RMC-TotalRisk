using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Nodes;

namespace RMC.TotalRisk.Tests.RiskFunctions.Responses.FaultTrees;

/// <summary>
/// Unit tests for <see cref="FaultTreeImportance"/> — the exact importance measures on
/// hand-computed trees: the single-event, two-event And and Or identities, the reduced-out and
/// house-event conventions, the non-coherent refusal, the percentile evaluation, and the
/// argument guards.
/// </summary>
[TestClass]
public class FaultTreeImportanceTests
{
    /// <summary>Builds a labeled response over the given tree.</summary>
    private static FaultTreeResponse Response(FaultTree tree, string name = "Importance")
    {
        return new FaultTreeResponse(new[] { 0d, 1d }, tree)
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
    }

    /// <summary>
    /// Verifies the single-event identities: P = q, Birnbaum 1, criticality 1, Fussell-Vesely
    /// 1, risk achievement worth 1/q, and risk reduction worth positive infinity.
    /// </summary>
    [TestMethod]
    public void Test_SingleEvent_Identities()
    {
        // Arrange
        var tree = new FaultTree();
        tree.Add(tree.Root.Id, new FaultTreeBasicEventNode("A", new ProbabilitySource(0.3d)));
        var response = Response(tree);

        // Act
        var result = FaultTreeImportance.Compute(response, new FaultTreeImportanceOptions(0d));

        // Assert
        Assert.AreEqual(0.3d, result.TopEventProbability, 1e-15);
        Assert.AreEqual(1, result.Entries.Count);
        var entry = result.Entries[0];
        Assert.AreEqual("A", entry.Name);
        Assert.AreEqual(0.3d, entry.BaselineProbability, 0d);
        Assert.AreEqual(1d, entry.Birnbaum, 1e-15);
        Assert.AreEqual(1d, entry.Criticality, 1e-15);
        Assert.AreEqual(1d, entry.FussellVesely, 1e-15);
        Assert.AreEqual(1d / 0.3d, entry.RiskAchievementWorth, 1e-12);
        Assert.IsTrue(double.IsPositiveInfinity(entry.RiskReductionWorth));
    }

    /// <summary>
    /// Verifies the two-event And and Or closed forms for every measure.
    /// </summary>
    [TestMethod]
    public void Test_TwoEvent_AndOr_ClosedForms()
    {
        // And: P = q1·q2 = 0.02.
        var andTree = new FaultTree();
        var andGate = new FaultTreeGateNode("Joint", FaultTreeGateType.And);
        andTree.Add(andTree.Root.Id, andGate);
        andTree.Add(andGate.Id, new FaultTreeBasicEventNode("A", new ProbabilitySource(0.1d)));
        andTree.Add(andGate.Id, new FaultTreeBasicEventNode("B", new ProbabilitySource(0.2d)));
        var andResult = FaultTreeImportance.Compute(Response(andTree), new FaultTreeImportanceOptions(0d));
        Assert.AreEqual(0.02d, andResult.TopEventProbability, 1e-15);
        var a = andResult.Entries.Single(e => e.Name == "A");
        Assert.AreEqual(0.2d, a.Birnbaum, 1e-15);                    // P(1) = 0.2, P(0) = 0.
        Assert.AreEqual(1d, a.Criticality, 1e-12);                   // 0.2·0.1/0.02.
        Assert.AreEqual(1d, a.FussellVesely, 1e-12);                 // 1 − 0/0.02.
        Assert.AreEqual(10d, a.RiskAchievementWorth, 1e-12);         // 0.2/0.02.
        Assert.IsTrue(double.IsPositiveInfinity(a.RiskReductionWorth));

        // Or: P = 1 − (1 − 0.1)(1 − 0.2) = 0.28.
        var orTree = new FaultTree();
        var orGate = new FaultTreeGateNode("Union", FaultTreeGateType.Or);
        orTree.Add(orTree.Root.Id, orGate);
        orTree.Add(orGate.Id, new FaultTreeBasicEventNode("A", new ProbabilitySource(0.1d)));
        orTree.Add(orGate.Id, new FaultTreeBasicEventNode("B", new ProbabilitySource(0.2d)));
        var orResult = FaultTreeImportance.Compute(Response(orTree), new FaultTreeImportanceOptions(0d));
        Assert.AreEqual(0.28d, orResult.TopEventProbability, 1e-15);
        var orA = orResult.Entries.Single(e => e.Name == "A");
        Assert.AreEqual(0.8d, orA.Birnbaum, 1e-15);                  // P(1) = 1, P(0) = 0.2.
        Assert.AreEqual(0.8d * 0.1d / 0.28d, orA.Criticality, 1e-12);
        Assert.AreEqual(1d - 0.2d / 0.28d, orA.FussellVesely, 1e-12);
        Assert.AreEqual(1d / 0.28d, orA.RiskAchievementWorth, 1e-12);
        Assert.AreEqual(0.28d / 0.2d, orA.RiskReductionWorth, 1e-12);
    }

    /// <summary>
    /// Verifies the reduced-out convention: a branch constant-collapsed by a false house event
    /// leaves its basic event with a Birnbaum of exactly zero, while the surviving branch
    /// carries the whole tree.
    /// </summary>
    [TestMethod]
    public void Test_ReducedOutVariable_BirnbaumExactlyZero()
    {
        // Arrange — Or(And(A, house-off), B): the And branch collapses to constant false.
        var tree = new FaultTree();
        var union = new FaultTreeGateNode("Union", FaultTreeGateType.Or);
        tree.Add(tree.Root.Id, union);
        var joint = new FaultTreeGateNode("Joint", FaultTreeGateType.And);
        tree.Add(union.Id, joint);
        tree.Add(joint.Id, new FaultTreeBasicEventNode("A", new ProbabilitySource(0.1d)));
        tree.Add(joint.Id, new FaultTreeHouseEventNode("Maintenance bypass", false));
        tree.Add(union.Id, new FaultTreeBasicEventNode("B", new ProbabilitySource(0.2d)));
        var response = Response(tree);

        // Act
        var result = FaultTreeImportance.Compute(response, new FaultTreeImportanceOptions(0d));

        // Assert
        Assert.AreEqual(0.2d, result.TopEventProbability, 1e-15);
        var a = result.Entries.Single(e => e.Name == "A");
        Assert.AreEqual(0d, a.Birnbaum, 0d);
        Assert.AreEqual(0d, a.Criticality, 0d);
        var b = result.Entries.Single(e => e.Name == "B");
        Assert.AreEqual(1d, b.Birnbaum, 1e-15);
    }

    /// <summary>
    /// Verifies the non-coherent refusal mirrors the cut-set surface: an Xor gate refuses the
    /// exact measures loudly with the Monte Carlo redirect.
    /// </summary>
    [TestMethod]
    public void Test_NonCoherent_RefusedLoudly()
    {
        // Arrange
        var tree = new FaultTree();
        var xor = new FaultTreeGateNode("Exclusive", FaultTreeGateType.Xor);
        tree.Add(tree.Root.Id, xor);
        tree.Add(xor.Id, new FaultTreeBasicEventNode("A", new ProbabilitySource(0.1d)));
        tree.Add(xor.Id, new FaultTreeBasicEventNode("B", new ProbabilitySource(0.2d)));
        var response = Response(tree);

        // Act / Assert
        var fault = Assert.ThrowsException<InvalidOperationException>(
            () => FaultTreeImportance.Compute(response, new FaultTreeImportanceOptions(0d)));
        StringAssert.Contains(fault.Message, "non-coherent");
        StringAssert.Contains(fault.Message, "Monte Carlo");
    }

    /// <summary>
    /// Verifies the percentile evaluation: an uncertain source evaluated at a percentile enters
    /// the measures at its quantile, and the single-event identities hold at that baseline.
    /// </summary>
    [TestMethod]
    public void Test_Percentile_EvaluatesSourceQuantile()
    {
        // Arrange — a uniform (0.1, 0.3) source: the 0.9 quantile is 0.28.
        var table = new UncertainOrderedPairedData(
            new[]
            {
                new UncertainOrdinate(0d, new Uniform(0.1d, 0.3d)),
                new UncertainOrdinate(1d, new Uniform(0.1d, 0.3d)),
            }, true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Uniform);
        var tree = new FaultTree();
        tree.Add(tree.Root.Id, new FaultTreeBasicEventNode("A", new ProbabilitySource(table)));
        var response = Response(tree);
        var options = new FaultTreeImportanceOptions(0d) { Percentile = 0.9d };

        // Act
        var result = FaultTreeImportance.Compute(response, options);

        // Assert
        Assert.AreEqual(0.9d, result.Percentile, 0d);
        Assert.AreEqual(0.28d, result.Entries[0].BaselineProbability, 1e-12);
        Assert.AreEqual(0.28d, result.TopEventProbability, 1e-12);
        Assert.AreEqual(1d, result.Entries[0].Birnbaum, 1e-15);
    }

    /// <summary>Verifies the argument guards: nulls and a non-authored hazard level.</summary>
    [TestMethod]
    public void Test_Guards()
    {
        // Arrange
        var tree = new FaultTree();
        tree.Add(tree.Root.Id, new FaultTreeBasicEventNode("A", new ProbabilitySource(0.3d)));
        var response = Response(tree);

        // Act / Assert
        Assert.ThrowsException<ArgumentNullException>(
            () => FaultTreeImportance.Compute(null!, new FaultTreeImportanceOptions(0d)));
        Assert.ThrowsException<ArgumentNullException>(
            () => FaultTreeImportance.Compute(response, null!));
        Assert.ThrowsException<ArgumentException>(
            () => FaultTreeImportance.Compute(response, new FaultTreeImportanceOptions(5d)));
    }
}
