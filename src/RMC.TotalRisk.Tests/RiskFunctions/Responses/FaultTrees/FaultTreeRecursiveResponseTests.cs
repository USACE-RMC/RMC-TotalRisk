using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees.Nodes;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Nodes;

namespace RMC.TotalRisk.Tests.RiskFunctions.Responses.FaultTrees;

/// <summary>
/// Tests recursive and cross-kind fault-tree sources: ordinary responses, nested event trees,
/// nested fault trees, and complete mixed-kind cycle diagnostics from both entry points.
/// </summary>
[TestClass]
public class FaultTreeRecursiveResponseTests
{
    /// <summary>Builds a labeled response over hazards 0 and 1.</summary>
    /// <param name="tree">The authored tree.</param>
    /// <param name="name">The function name.</param>
    /// <returns>The response.</returns>
    private static FaultTreeResponse CreateResponse(FaultTree tree, string name)
    {
        return new FaultTreeResponse(new[] { 0d, 1d }, tree)
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
    }

    /// <summary>Builds an aligned deterministic table.</summary>
    /// <param name="hazards">The hazard knots.</param>
    /// <param name="probabilities">The deterministic probabilities.</param>
    /// <returns>The table.</returns>
    private static UncertainOrderedPairedData DeterministicTable(double[] hazards, double[] probabilities)
    {
        return new UncertainOrderedPairedData(
            hazards.Select((hazard, index) =>
                new UncertainOrdinate(hazard, new Deterministic(probabilities[index]))).ToArray(),
            true, SortOrder.Ascending, false, SortOrder.None,
            UnivariateDistributionType.Deterministic);
    }

    /// <summary>Verifies an ordinary tabular response drives a basic event at caller hazards.</summary>
    [TestMethod]
    public void Test_OrdinaryResponseSource_EvaluatedAtCallerHazards()
    {
        // Arrange — the referenced fragility has its own wider axis; the fault axis interpolates.
        var ordinary = new TabularResponse
        {
            Name = "Ordinary fragility",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = DeterministicTable(new[] { 0d, 2d }, new[] { 0.1d, 0.5d }),
        };
        var tree = new FaultTree();
        tree.Add(tree.Root.Id, new FaultTreeBasicEventNode("Referenced", new ProbabilitySource(ordinary)));
        FaultTreeResponse response = CreateResponse(tree, "Fault over ordinary");

        // Act
        var curve = response.SampleResponseFunction();

        // Assert — hazard 1 falls between the referenced knots, and the referenced tabular
        // response contributes its own structural co-monotonic dimension.
        Assert.AreEqual(0.1d, curve[0].Y, 1e-12);
        Assert.AreEqual(0.3d, curve[1].Y, 1e-12);
        Assert.AreEqual(1, response.SamplingDimensions);
    }

    /// <summary>Verifies a fault tree nests inside an event tree through the shared compiler seam.</summary>
    [TestMethod]
    public void Test_FaultInsideEvent_CrossKindComposition()
    {
        // Arrange — event chance driven by a fault tree: Or(A=0.2, B=0.3) = 0.44.
        var faultTree = new FaultTree();
        faultTree.Add(faultTree.Root.Id, new FaultTreeBasicEventNode("A", new ProbabilitySource(0.2d)));
        faultTree.Add(faultTree.Root.Id, new FaultTreeBasicEventNode("B", new ProbabilitySource(0.3d)));
        FaultTreeResponse fault = CreateResponse(faultTree, "Nested fault");

        var eventTree = new EventTree();
        eventTree.Add(eventTree.Root.Id, new ChanceNode("Breach", new ProbabilitySource(fault)));
        eventTree.Add(eventTree.Root.Id, new RemainderNode("No breach"));
        var eventResponse = new EventTreeResponse(new[] { 0d, 1d }, eventTree)
        {
            Name = "Event over fault",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };

        // Act / Assert
        Assert.IsTrue(eventResponse.Validate().IsValid,
            string.Join(" | ", eventResponse.Validate().ValidationMessages));
        Assert.AreEqual(1d - 0.8d * 0.7d, eventResponse.SampleResponseFunction()[0].Y, 1e-15);
        Assert.AreEqual(0, eventResponse.SamplingDimensions);
        Assert.IsTrue(eventResponse.IsDeterministic);
    }

    /// <summary>Verifies an event tree nests inside a fault tree through the shared compiler seam.</summary>
    [TestMethod]
    public void Test_EventInsideFault_CrossKindComposition()
    {
        // Arrange — a basic event driven by an event tree whose failure probability is 0.25.
        var eventTree = new EventTree();
        eventTree.Add(eventTree.Root.Id, new ChanceNode("Failure", new ProbabilitySource(0.25d)));
        eventTree.Add(eventTree.Root.Id, new RemainderNode("Survival"));
        var eventResponse = new EventTreeResponse(new[] { 0d, 1d }, eventTree)
        {
            Name = "Nested event",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };

        var faultTree = new FaultTree();
        faultTree.Root.GateType = FaultTreeGateType.And;
        faultTree.Add(faultTree.Root.Id, new FaultTreeBasicEventNode("From event",
            new ProbabilitySource(eventResponse)));
        faultTree.Add(faultTree.Root.Id, new FaultTreeBasicEventNode("Scalar",
            new ProbabilitySource(0.4d)));
        FaultTreeResponse fault = CreateResponse(faultTree, "Fault over event");

        // Act / Assert
        Assert.IsTrue(fault.Validate().IsValid,
            string.Join(" | ", fault.Validate().ValidationMessages));
        Assert.AreEqual(0.25d * 0.4d, fault.SampleResponseFunction()[0].Y, 1e-15);
    }

    /// <summary>Verifies a nested fault edit propagates staleness across the kind boundary.</summary>
    [TestMethod]
    public void Test_CrossKindDependency_PropagatesInvalidation()
    {
        // Arrange
        var faultTree = new FaultTree();
        var basic = new FaultTreeBasicEventNode("A", new ProbabilitySource(0.2d));
        faultTree.Add(faultTree.Root.Id, basic);
        FaultTreeResponse fault = CreateResponse(faultTree, "Nested fault");

        var eventTree = new EventTree();
        eventTree.Add(eventTree.Root.Id, new ChanceNode("Breach", new ProbabilitySource(fault)));
        eventTree.Add(eventTree.Root.Id, new RemainderNode("No breach"));
        var eventResponse = new EventTreeResponse(new[] { 0d, 1d }, eventTree)
        {
            Name = "Event over fault",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
        Assert.AreEqual(0.2d, eventResponse.SampleResponseFunction()[0].Y, 1e-15);
        byte[] beforeHash = eventResponse.CanonicalHash();

        // Act — edit the nested fault tree's content.
        basic.ProbabilitySource = new ProbabilitySource(0.6d);

        // Assert — the event plan follows the live edit and its identity moves.
        Assert.AreEqual(0.6d, eventResponse.SampleResponseFunction()[0].Y, 1e-15);
        CollectionAssert.AreNotEqual(beforeHash, eventResponse.CanonicalHash());
    }

    /// <summary>
    /// Verifies a mixed-kind reference cycle: transactional authoring rejects completing it, and
    /// a latent cycle introduced by direct source replacement is detected with one complete path
    /// from either entry point.
    /// </summary>
    [TestMethod]
    public void Test_MixedKindCycle_FullPathDiagnosticFromBothSides()
    {
        // Arrange — fault F's basic references event E through a placeholder edit seam.
        var faultTree = new FaultTree();
        FaultTreeResponse fault = CreateResponse(faultTree, "Fault side");
        var placeholder = new FaultTreeBasicEventNode("Via event", new ProbabilitySource(0.1d));
        faultTree.Add(faultTree.Root.Id, placeholder);
        var eventTree = new EventTree();
        var eventResponse = new EventTreeResponse(new[] { 0d, 1d }, eventTree)
        {
            Name = "Event side",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
        eventTree.Add(eventTree.Root.Id, new ChanceNode("Via fault", new ProbabilitySource(fault)));
        eventTree.Add(eventTree.Root.Id, new RemainderNode("Rest"));

        // Act — property replacement bypasses validate-by-compile, leaving a latent cycle.
        placeholder.ProbabilitySource = new ProbabilitySource(eventResponse);
        var faultSide = Assert.ThrowsException<InvalidOperationException>(() => fault.CanonicalHash());
        var eventSide = Assert.ThrowsException<InvalidOperationException>(() => eventResponse.CanonicalHash());

        // Assert — mixed-kind cycles render the generic tree label with both function names.
        foreach (var exception in new[] { faultSide, eventSide })
        {
            StringAssert.Contains(exception.Message, "Cross-function tree cycle detected");
            StringAssert.Contains(exception.Message, "Fault side");
            StringAssert.Contains(exception.Message, "Event side");
        }
    }

    /// <summary>
    /// Verifies a fault-only transfer cycle is rejected at the authoring call that would complete
    /// it, with the full fault-tree-labeled path, leaving the tree unchanged.
    /// </summary>
    [TestMethod]
    public void Test_FaultOnlyCycle_KeepsKindLabel()
    {
        // Arrange — two functions; the first transfer is legal, the closing transfer is not.
        var treeA = new FaultTree();
        FaultTreeResponse a = CreateResponse(treeA, "Alpha");
        var treeB = new FaultTree();
        FaultTreeResponse b = CreateResponse(treeB, "Beta");
        treeA.LinkShared(treeA.Root.Id, b, treeB.Root.Id, "To beta");
        int nodesBefore = treeB.Nodes.Count;

        // Act — completing the cycle fails validate-by-compile inside the transaction.
        var exception = Assert.ThrowsException<InvalidOperationException>(
            () => treeB.LinkShared(treeB.Root.Id, a, treeA.Root.Id, "To alpha"));

        // Assert — the single-kind label survives and the transaction rolled back.
        StringAssert.Contains(exception.Message, "Cross-function fault-tree cycle detected");
        StringAssert.Contains(exception.Message, "Alpha");
        StringAssert.Contains(exception.Message, "Beta");
        Assert.AreEqual(nodesBefore, treeB.Nodes.Count);
        CollectionAssert.AreEqual(a.CanonicalHash(), a.CanonicalHash());
    }

    /// <summary>Verifies referenced-response variables sample with recursive child dimensions.</summary>
    [TestMethod]
    public void Test_ReferencedResponseVariable_SamplesWithChildDimensions()
    {
        // Arrange — a nested uncertain fault tree referenced by one shared basic event.
        var nestedTree = new FaultTree();
        nestedTree.Add(nestedTree.Root.Id, new FaultTreeBasicEventNode("Uncertain",
            new ProbabilitySource(new UncertainOrderedPairedData(new[]
            {
                new UncertainOrdinate(0d, new Uniform(0.2d, 0.4d)),
                new UncertainOrdinate(1d, new Uniform(0.3d, 0.5d)),
            }, true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Uniform))));
        FaultTreeResponse nested = CreateResponse(nestedTree, "Nested uncertain");

        var tree = new FaultTree();
        tree.Add(tree.Root.Id, new FaultTreeBasicEventNode("Referenced", new ProbabilitySource(nested)));
        FaultTreeResponse response = CreateResponse(tree, "Outer");

        // Assert dimensions flow through the nested plan.
        Assert.AreEqual(1, response.SamplingDimensions);
        Assert.IsFalse(response.IsDeterministic);

        // Act — realization sampling draws the nested clone's stream.
        response.SetupSampler(4, 24680, SamplingScheme.LatinHypercube);
        for (int realization = 0; realization < 4; realization++)
        {
            double value = response.SampleResponseFunction(realization)[0].Y;
            Assert.IsTrue(value >= 0.2d && value <= 0.4d, $"Realization {realization} was {value}.");
        }
    }
}
