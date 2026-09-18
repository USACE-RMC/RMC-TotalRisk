using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Nodes;

namespace RMC.TotalRisk.Tests.RiskFunctions.Responses.FaultTrees;

/// <summary>
/// Tests the common-cause group expansion behind the fault-tree response: exact derived-event
/// algebra through the frozen diagram, the one-shared-draw sampling contract, identity movement
/// and conditional presence, serialization round trips, cut-set naming, invalidation, and the
/// loud invalid-group and partial-context diagnostics.
/// </summary>
[TestClass]
public class FaultTreeCcfExpansionTests
{
    /// <summary>Verifies the exact beta-factor closed forms through Or and And top events.</summary>
    [TestMethod]
    public void Test_SampleResponseFunction_BetaFactorClosedForms_Exact()
    {
        // Arrange: Q = 0.2, β = 0.1 → independent 0.18 each, common 0.02.
        FaultTreeResponse orResponse = GroupedPair(FaultTreeGateType.Or, out _);
        FaultTreeResponse andResponse = GroupedPair(FaultTreeGateType.And, out _);

        // Act
        double orTop = orResponse.SampleResponseFunction()[0].Y;
        double andTop = andResponse.SampleResponseFunction()[0].Y;

        // Assert: P(A∪B) = 1 − (1 − 0.18)²(1 − 0.02); P(A∩B) = 0.02 + 0.98·0.18².
        Assert.AreEqual(1d - 0.82d * 0.82d * 0.98d, orTop, 1e-15d);
        Assert.AreEqual(0.02d + 0.98d * 0.18d * 0.18d, andTop, 1e-15d);
        Assert.IsTrue(andTop > 0.2d * 0.2d,
            "The common-cause coupling must raise the And-gate probability above independence.");
    }

    /// <summary>Verifies the group samples one shared basis stream: one dimension, one draw.</summary>
    [TestMethod]
    public void Test_SetupSampler_Group_SamplesOneSharedBasis()
    {
        // Arrange: two exchangeable uncertain members under an And gate.
        FaultTreeResponse response = UncertainGroupedPair(FaultTreeGateType.And, out UncertainOrderedPairedData table);
        Assert.AreEqual(1, response.SamplingDimensions);
        response.SetupSampler(16, 424242, SamplingScheme.LatinHypercube);

        // Act / Assert: with the shared draw Q, the top is exactly βQ + (1 − βQ)((1 − β)Q)².
        for (int realization = 0; realization < 16; realization++)
        {
            double q = table.CurveSample(response.SampledPercentile(realization, 0))[0].Y;
            double common = 0.1d * q;
            double independent = 0.9d * q;
            double expected = common + (1d - common) * independent * independent;
            Assert.AreEqual(expected, response.SampleResponseFunction(realization)[0].Y, 1e-15d);
        }
    }

    /// <summary>Verifies configuring a group is a hash event with exact add/remove symmetry, while metadata stays inert.</summary>
    [TestMethod]
    public void Test_CanonicalHash_GroupLifecycleAndMetadata()
    {
        // Arrange
        FaultTreeResponse response = GroupedPair(FaultTreeGateType.Or, out FaultTreeCcfGroup group);
        byte[] grouped = response.CanonicalHash();

        // Act / Assert: removal restores the group-free identity bit-exactly.
        Assert.IsTrue(response.FaultTree.RemoveCcfGroup(group));
        byte[] ungrouped = response.CanonicalHash();
        CollectionAssert.AreNotEqual(grouped, ungrouped);
        response.FaultTree.AddCcfGroup(group);
        CollectionAssert.AreEqual(grouped, response.CanonicalHash());

        // Metadata never moves the hash; parameters do.
        group.Name = "Renamed group";
        group.Description = "Changed";
        CollectionAssert.AreEqual(grouped, response.CanonicalHash());
        group.Parameters = new[] { 0.25d };
        CollectionAssert.AreNotEqual(grouped, response.CanonicalHash());
    }

    /// <summary>Verifies a group-free tree serializes without the group container.</summary>
    [TestMethod]
    public void Test_Serialization_GroupFreeTree_HasNoGroupContainer()
    {
        // Arrange
        var tree = new FaultTree();
        tree.Add(tree.Root.Id, new FaultTreeBasicEventNode("Plain", new ProbabilitySource(0.2d)));

        // Assert
        Assert.IsNull(tree.ToXElement().Element("CcfGroups"));
    }

    /// <summary>Verifies a grouped tree round-trips with identical hash and results.</summary>
    [TestMethod]
    public void Test_Serialization_GroupedTree_RoundTrips()
    {
        // Arrange
        FaultTreeResponse response = UncertainGroupedPair(FaultTreeGateType.And, out _);

        // Act
        var restored = new FaultTreeResponse(response.ToXElement());
        response.SetupSampler(8, 777, SamplingScheme.LatinHypercube);
        restored.SetupSampler(8, 777, SamplingScheme.LatinHypercube);

        // Assert
        CollectionAssert.AreEqual(response.CanonicalHash(), restored.CanonicalHash());
        for (int realization = 0; realization < 8; realization++)
        {
            Assert.AreEqual(response.SampleResponseFunction(realization)[0].Y,
                restored.SampleResponseFunction(realization)[0].Y);
        }
    }

    /// <summary>Verifies minimal cut sets report the derived events with their combination names.</summary>
    [TestMethod]
    public void Test_GetMinimalCutSets_NamesDerivedEvents()
    {
        // Arrange
        FaultTreeResponse response = GroupedPair(FaultTreeGateType.Or, out _);

        // Act
        var cutSets = response.GetMinimalCutSets();
        var names = cutSets.SelectMany(set => set.Events.Select(item => item.Name)).ToList();

        // Assert: two singleton independent cut sets and the singleton common-cause cut set.
        Assert.AreEqual(3, cutSets.Count);
        Assert.AreEqual(2, names.Count(name => name.EndsWith("(independent)", StringComparison.Ordinal)));
        Assert.AreEqual(1, names.Count(name => name.EndsWith("(common cause)", StringComparison.Ordinal)));
    }

    /// <summary>Verifies a live parameter edit invalidates the compiled plan.</summary>
    [TestMethod]
    public void Test_CompiledPlan_LiveGroupEdit_Invalidates()
    {
        // Arrange
        FaultTreeResponse response = GroupedPair(FaultTreeGateType.Or, out FaultTreeCcfGroup group);
        double baseline = response.SampleResponseFunction()[0].Y;

        // Act: β = 1 makes every failure common cause, so the Or top equals the shared Q.
        group.Parameters = new[] { 1d };

        // Assert
        Assert.AreEqual(0.2d, response.SampleResponseFunction()[0].Y, 1e-15d);
        Assert.AreNotEqual(baseline, response.SampleResponseFunction()[0].Y);
    }

    /// <summary>Verifies an invalid group is diagnosed loudly and blocks sampling.</summary>
    [TestMethod]
    public void Test_Validate_InvalidGroup_DiagnosedAndBlocking()
    {
        // Arrange: content-unequal members.
        var tree = new FaultTree();
        var pump = new FaultTreeBasicEventNode("Pump", new ProbabilitySource(0.2d));
        var valve = new FaultTreeBasicEventNode("Valve", new ProbabilitySource(0.5d));
        tree.Add(tree.Root.Id, pump);
        tree.Add(tree.Root.Id, valve);
        tree.AddCcfGroup(new FaultTreeCcfGroup("Unequal", FaultTreeCcfModel.BetaFactor, new[] { 0.1d },
            new[] { pump.Id, valve.Id }));
        var response = ValidResponse(tree);

        // Act
        var (isValid, messages) = response.Validate();

        // Assert
        Assert.IsFalse(isValid);
        Assert.IsTrue(messages.Any(m => m.StartsWith("Error:") && m.Contains("same probability-source content")));
        try
        {
            _ = response.SampleResponseFunction();
            Assert.Fail("An invalid group must block sampling.");
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// Verifies the partial-context diagnostic: a member reachable only inside an independent
    /// transfer context cannot carry the group coupling and the plan says so loudly.
    /// </summary>
    [TestMethod]
    public void Test_Validate_PartialContextCoverage_Diagnosed()
    {
        // Arrange: member A at the root context; member B only inside an independently
        // transferred subtree, so no single context expands both.
        var tree = new FaultTree();
        var memberA = new FaultTreeBasicEventNode("A", new ProbabilitySource(0.2d));
        tree.Add(tree.Root.Id, memberA);
        Guid carrierId = tree.Add(tree.Root.Id, new FaultTreeGateNode("Carrier", FaultTreeGateType.Or));
        var memberB = new FaultTreeBasicEventNode("B", new ProbabilitySource(0.2d));
        tree.Add(carrierId, memberB);
        var response = ValidResponse(tree);
        tree.LinkIndependent(tree.Root.Id, carrierId, "Cloned carrier");
        tree.AddCcfGroup(new FaultTreeCcfGroup("Split", FaultTreeCcfModel.BetaFactor, new[] { 0.1d },
            new[] { memberA.Id, memberB.Id }));

        // Act
        var (isValid, messages) = response.Validate();

        // Assert: the carrier subtree is reachable directly AND through the clone, so at least
        // one context holds only part of the group.
        Assert.IsFalse(isValid);
        Assert.IsTrue(messages.Any(m => m.StartsWith("Error:") && m.Contains("in one independent context")),
            string.Join(" | ", messages));
    }

    /// <summary>Builds a valid two-member beta-factor pair (Q = 0.2, β = 0.1) under the requested gate.</summary>
    /// <param name="gateType">The top gate over the two members.</param>
    /// <param name="group">The attached group.</param>
    /// <returns>The response.</returns>
    private static FaultTreeResponse GroupedPair(FaultTreeGateType gateType, out FaultTreeCcfGroup group)
    {
        var tree = new FaultTree();
        var gate = new FaultTreeGateNode("Pair", gateType);
        tree.Add(tree.Root.Id, gate);
        var pump = new FaultTreeBasicEventNode("Pump", new ProbabilitySource(0.2d));
        var valve = new FaultTreeBasicEventNode("Valve", new ProbabilitySource(0.2d));
        tree.Add(gate.Id, pump);
        tree.Add(gate.Id, valve);
        group = new FaultTreeCcfGroup("Pair group", FaultTreeCcfModel.BetaFactor, new[] { 0.1d },
            new[] { pump.Id, valve.Id });
        tree.AddCcfGroup(group);
        return ValidResponse(tree);
    }

    /// <summary>Builds a valid two-member uncertain beta-factor pair sharing one basis table.</summary>
    /// <param name="gateType">The top gate over the two members.</param>
    /// <param name="table">The shared basis table content.</param>
    /// <returns>The response.</returns>
    private static FaultTreeResponse UncertainGroupedPair(FaultTreeGateType gateType,
        out UncertainOrderedPairedData table)
    {
        UncertainOrderedPairedData Build()
        {
            return new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(0d, new Uniform(0.1d, 0.3d)),
                    new UncertainOrdinate(1d, new Uniform(0.2d, 0.6d)),
                }, true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Uniform);
        }

        table = Build();
        var tree = new FaultTree();
        var gate = new FaultTreeGateNode("Pair", gateType);
        tree.Add(tree.Root.Id, gate);
        var pump = new FaultTreeBasicEventNode("Pump", new ProbabilitySource(Build()));
        var valve = new FaultTreeBasicEventNode("Valve", new ProbabilitySource(Build()));
        tree.Add(gate.Id, pump);
        tree.Add(gate.Id, valve);
        tree.AddCcfGroup(new FaultTreeCcfGroup("Pair group", FaultTreeCcfModel.BetaFactor, new[] { 0.1d },
            new[] { pump.Id, valve.Id }));
        return ValidResponse(tree);
    }

    /// <summary>Wraps a tree in a valid two-level response.</summary>
    /// <param name="tree">The authored tree.</param>
    /// <returns>The response.</returns>
    private static FaultTreeResponse ValidResponse(FaultTree tree)
    {
        return new FaultTreeResponse(new[] { 0d, 1d }, tree)
        {
            Name = "CCF fault tree",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
    }
}
