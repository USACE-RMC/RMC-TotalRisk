using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Nodes;

namespace RMC.TotalRisk.Tests.RiskFunctions.Responses.FaultTrees.Support;

/// <summary>
/// Tests the instance-scoped immutable fault-tree plan cache: reuse, per-edit invalidation,
/// external-dependency staleness, and exact rollback of a failed transaction.
/// </summary>
[TestClass]
public class FaultTreeCompiledPlanTests
{
    /// <summary>Builds a labeled two-event response.</summary>
    /// <param name="gate">The interior gate.</param>
    /// <returns>The response.</returns>
    private static FaultTreeResponse CreateResponse(out FaultTreeGateNode gate)
    {
        var tree = new FaultTree();
        gate = new FaultTreeGateNode("Joint", FaultTreeGateType.And);
        tree.Add(tree.Root.Id, gate);
        tree.Add(gate.Id, new FaultTreeBasicEventNode("A", new ProbabilitySource(0.1d)));
        tree.Add(gate.Id, new FaultTreeBasicEventNode("B", new ProbabilitySource(0.2d)));
        return new FaultTreeResponse(new[] { 0d, 1d }, tree)
        {
            Name = "Plan cache",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
    }

    /// <summary>Verifies repeated reads and metadata edits reuse one immutable plan.</summary>
    [TestMethod]
    public void Test_RepeatedReadsAndMetadataEdits_ReuseOnePlan()
    {
        // Arrange
        FaultTreeResponse response = CreateResponse(out FaultTreeGateNode gate);

        // Act
        object first = response.CompiledPlanIdentity;
        _ = response.SampleResponseFunction();
        _ = response.CanonicalHash();
        _ = response.SamplingDimensions;
        response.Name = "Renamed";
        gate.Name = "Renamed gate";
        gate.Description = "Documented";

        // Assert
        Assert.AreSame(first, response.CompiledPlanIdentity);
        Assert.AreEqual(1L, response.CompiledPlanBuildCount);
        Assert.IsTrue(response.CompiledDecisionNodeCount > 0);
        Assert.AreEqual(2, response.CompiledVariableCount);
    }

    /// <summary>Verifies every compute-edit category invalidates and republished plans differ.</summary>
    [TestMethod]
    public void Test_ComputeEdits_InvalidateExactly()
    {
        // Arrange
        FaultTreeResponse response = CreateResponse(out FaultTreeGateNode gate);
        object plan = response.CompiledPlanIdentity;
        long builds = response.CompiledPlanBuildCount;

        void AssertRebuilt()
        {
            object next = response.CompiledPlanIdentity;
            Assert.AreNotSame(plan, next);
            Assert.AreEqual(builds + 1, response.CompiledPlanBuildCount);
            plan = next;
            builds = response.CompiledPlanBuildCount;
        }

        // Act / Assert — each edit category rebuilds exactly once on the next read.
        gate.GateType = FaultTreeGateType.Or;
        AssertRebuilt();
        ((FaultTreeBasicEventNode)gate.Children[0]).ProbabilitySource = new ProbabilitySource(0.15d);
        AssertRebuilt();
        response.FaultTree.Add(gate.Id, new FaultTreeHouseEventNode("H", true));
        AssertRebuilt();
        response.SetHazardLevels(new[] { 0d, 0.5d, 1d });
        AssertRebuilt();
        response.BddNodeLimit = 500000;
        AssertRebuilt();
    }

    /// <summary>Verifies an external transfer target's edits stale the referencing plan.</summary>
    [TestMethod]
    public void Test_ExternalDependency_InvalidatesReferencingPlan()
    {
        // Arrange
        var externalTree = new FaultTree();
        var externalBasic = new FaultTreeBasicEventNode("External", new ProbabilitySource(0.25d));
        externalTree.Add(externalTree.Root.Id, externalBasic);
        var external = new FaultTreeResponse(new[] { 0d, 1d }, externalTree)
        {
            Name = "External",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
        var tree = new FaultTree();
        tree.LinkShared(tree.Root.Id, external, externalBasic.Id, "Use external");
        var response = new FaultTreeResponse(new[] { 0d, 1d }, tree)
        {
            Name = "Referrer",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
        Assert.AreEqual(0.25d, response.SampleResponseFunction()[0].Y, 1e-15);
        object plan = response.CompiledPlanIdentity;

        // Act — edit the external function's content.
        externalBasic.ProbabilitySource = new ProbabilitySource(0.5d);

        // Assert — the referencing plan rebuilds and follows the live edit.
        Assert.AreNotSame(plan, response.CompiledPlanIdentity);
        Assert.AreEqual(0.5d, response.SampleResponseFunction()[0].Y, 1e-15);
    }

    /// <summary>Verifies a failed transaction restores the exact cache, hash, and revision.</summary>
    [TestMethod]
    public void Test_FailedMutation_RestoresExactCacheState()
    {
        // Arrange
        FaultTreeResponse response = CreateResponse(out FaultTreeGateNode gate);
        object plan = response.CompiledPlanIdentity;
        byte[] hash = response.CanonicalHash();
        long builds = response.CompiledPlanBuildCount;

        // Act — an illegal move fails inside the transaction.
        Assert.ThrowsException<InvalidOperationException>(
            () => response.FaultTree.Move(gate.Id, gate.Id));

        // Assert — the published plan, hash, and build count are untouched.
        Assert.AreSame(plan, response.CompiledPlanIdentity);
        CollectionAssert.AreEqual(hash, response.CanonicalHash());
        Assert.AreEqual(builds, response.CompiledPlanBuildCount);
    }
}
