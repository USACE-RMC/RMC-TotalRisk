using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results.Risk;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.Systems.Components;

namespace RMC.TotalRisk.Tests.Analyses.Risk;

/// <summary>
/// Tests the configuration-risk query: re-authored bit parity, author inertness, nested reach,
/// the no-op configuration, and the refusal matrix.
/// </summary>
[TestClass]
public class MeasureConfigurationRiskTests
{
    /// <summary>Verifies the query equals directly re-authored baseline and configured models.</summary>
    [TestMethod]
    public void Test_Query_MatchesReauthoredModels_BitExact()
    {
        (RiskAnalysis author, Guid functionId, Guid houseId) = BuildAnalysis(houseState: false);

        ConfigurationRiskResults results = author.MeasureConfigurationRisk(
            new[] { new HouseEventState(functionId, houseId, true) });

        (RiskAnalysis baselineTwin, _, _) = BuildAnalysis(houseState: false);
        baselineTwin.RunAsync().GetAwaiter().GetResult();
        (RiskAnalysis configuredTwin, _, _) = BuildAnalysis(houseState: true);
        configuredTwin.RunAsync().GetAwaiter().GetResult();

        Assert.AreEqual(baselineTwin.MeanRiskResults!.Curves.Fail.TotalProbability,
            results.System.BaselineFailureProbability);
        Assert.AreEqual(configuredTwin.MeanRiskResults!.Curves.Fail.TotalProbability,
            results.System.ConfiguredFailureProbability);
        Assert.AreEqual(baselineTwin.MeanRiskResults.Curves.Total.Mean,
            results.System.BaselineExpectedConsequences[0]);
        Assert.AreEqual(configuredTwin.MeanRiskResults.Curves.Total.Mean,
            results.System.ConfiguredExpectedConsequences[0]);
        Assert.AreEqual(baselineTwin.MeanRiskResults.Components[0].Curves.Fail.TotalProbability,
            results.Components[0].BaselineFailureProbability);
        Assert.AreEqual(configuredTwin.MeanRiskResults.Components[0].Curves.Fail.TotalProbability,
            results.Components[0].ConfiguredFailureProbability);
        Assert.IsTrue(results.System.FailureProbabilityChange > 0d,
            "Forcing the house event true must raise the failure probability.");
        Assert.AreEqual(1, results.AppliedOverrides.Count);
        StringAssert.Contains(results.AppliedOverrides[0], "Gate out of service");
    }

    /// <summary>Verifies the query leaves the authored model, results, and state untouched.</summary>
    [TestMethod]
    public void Test_Query_AuthorState_Untouched()
    {
        (RiskAnalysis author, Guid functionId, Guid houseId) = BuildAnalysis(houseState: false);
        author.RunAsync().GetAwaiter().GetResult();
        string publishedJson = author.RiskResults!.ToJson();
        string componentHash = Convert.ToHexString(author.Components[0].CanonicalHash());
        bool authoredState = ((FaultTreeHouseEventNode)FaultResponse(author).FaultTree
            .FindById(houseId)!).State;

        author.MeasureConfigurationRisk(new[] { new HouseEventState(functionId, houseId, true) });

        Assert.IsTrue(author.IsEstimated, "The query must not invalidate the published results.");
        Assert.AreEqual(publishedJson, author.RiskResults!.ToJson());
        Assert.AreEqual(componentHash, Convert.ToHexString(author.Components[0].CanonicalHash()));
        Assert.AreEqual(authoredState, ((FaultTreeHouseEventNode)FaultResponse(author).FaultTree
            .FindById(houseId)!).State, "The authored house event must keep its state.");
    }

    /// <summary>Verifies the query runs without any prior estimation of the authored model.</summary>
    [TestMethod]
    public void Test_Query_UnestimatedAuthor_Works()
    {
        (RiskAnalysis author, Guid functionId, Guid houseId) = BuildAnalysis(houseState: false);

        ConfigurationRiskResults results = author.MeasureConfigurationRisk(
            new[] { new HouseEventState(functionId, houseId, true) });

        Assert.IsFalse(author.IsEstimated);
        Assert.IsTrue(results.System.ConfiguredFailureProbability
            > results.System.BaselineFailureProbability);
    }

    /// <summary>Verifies a no-op override reproduces the baseline exactly.</summary>
    [TestMethod]
    public void Test_Query_NoOpOverride_ZeroChange()
    {
        (RiskAnalysis author, Guid functionId, Guid houseId) = BuildAnalysis(houseState: false);

        ConfigurationRiskResults results = author.MeasureConfigurationRisk(
            new[] { new HouseEventState(functionId, houseId, false) });

        Assert.AreEqual(results.System.BaselineFailureProbability,
            results.System.ConfiguredFailureProbability);
        Assert.AreEqual(0d, results.System.FailureProbabilityChange);
        Assert.AreEqual(1d, results.System.FailureProbabilityRatio);
        Assert.AreEqual(results.System.BaselineExpectedConsequences[0],
            results.System.ConfiguredExpectedConsequences[0]);
    }

    /// <summary>Verifies a house event inside an external transfer target is reached.</summary>
    [TestMethod]
    public void Test_Query_ExternalTransferTarget_Reached()
    {
        var targetTree = new FaultTree();
        Guid nestedGateId = targetTree.Add(targetTree.Root.Id,
            new FaultTreeGateNode("Nested outage impact", FaultTreeGateType.And));
        Guid houseId = targetTree.Add(nestedGateId,
            new FaultTreeHouseEventNode("Nested gate", false));
        targetTree.Add(nestedGateId,
            new FaultTreeBasicEventNode("Nested load", new ProbabilitySource(0.8d)));
        targetTree.Add(targetTree.Root.Id,
            new FaultTreeBasicEventNode("Nested basic", new ProbabilitySource(0.05d)));
        var target = new FaultTreeResponse(new[] { 0d, 1d }, targetTree)
        {
            Name = "Nested fault tree",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };

        var ownerTree = new FaultTree();
        ownerTree.Add(ownerTree.Root.Id,
            new FaultTreeBasicEventNode("Owner basic", new ProbabilitySource(0.1d)));
        ownerTree.Add(ownerTree.Root.Id, new FaultTreeTransferNode("Nested subsystem",
            new TreeNodeReference(target.Id, targetTree.Root.Id, target.Name, "Top event"),
            target));
        var owner = new FaultTreeResponse(new[] { 0d, 1d }, ownerTree)
        {
            Name = "Owner fault tree",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
        RiskAnalysis author = BuildAnalysisOver(owner);

        ConfigurationRiskResults results = author.MeasureConfigurationRisk(
            new[] { new HouseEventState(target.Id, houseId, true) });

        Assert.IsTrue(results.System.ConfiguredFailureProbability
            > results.System.BaselineFailureProbability,
            "Forcing the nested house event true must raise the failure probability.");
        StringAssert.Contains(results.AppliedOverrides[0], "Nested gate");
    }

    /// <summary>Verifies the refusal matrix: null, empty, duplicate, and unmatched overrides.</summary>
    [TestMethod]
    public void Test_Query_Refusals()
    {
        (RiskAnalysis author, Guid functionId, Guid houseId) = BuildAnalysis(houseState: false);

        Assert.ThrowsException<ArgumentNullException>(() =>
            author.MeasureConfigurationRisk(null!));
        Assert.ThrowsException<ArgumentException>(() =>
            author.MeasureConfigurationRisk(Array.Empty<HouseEventState>()));
        Assert.ThrowsException<ArgumentException>(() =>
            author.MeasureConfigurationRisk(new[]
            {
                new HouseEventState(functionId, houseId, true),
                new HouseEventState(functionId, houseId, false),
            }));
        var unmatchedNode = Assert.ThrowsException<InvalidOperationException>(() =>
            author.MeasureConfigurationRisk(new[]
            {
                new HouseEventState(functionId, Guid.NewGuid(), true),
            }));
        StringAssert.Contains(unmatchedNode.Message, "cannot reach");
        Assert.ThrowsException<InvalidOperationException>(() =>
            author.MeasureConfigurationRisk(new[]
            {
                new HouseEventState(Guid.NewGuid(), houseId, true),
            }));
    }

    /// <summary>Gets the authored fault-tree response of the standard fixture.</summary>
    /// <param name="analysis">The authored analysis.</param>
    /// <returns>The response.</returns>
    private static FaultTreeResponse FaultResponse(RiskAnalysis analysis)
    {
        return analysis.Components[0].GetReferencedFunctions()
            .OfType<FaultTreeResponse>().Single();
    }

    /// <summary>
    /// Builds the standard fixture: OR(AND(house, 0.9), 0.2) behind a deterministic chain, so
    /// the configured state raises the flat response from 0.2 to 0.92 without reaching the
    /// degenerate certain-failure curve.
    /// </summary>
    /// <param name="houseState">The authored house-event state.</param>
    /// <returns>The analysis with the fault-tree function and house-event ids.</returns>
    private static (RiskAnalysis Analysis, Guid FunctionId, Guid HouseId) BuildAnalysis(bool houseState)
    {
        var faultTree = new FaultTree();
        Guid gateId = faultTree.Add(faultTree.Root.Id,
            new FaultTreeGateNode("Outage impact", FaultTreeGateType.And));
        Guid houseId = faultTree.Add(gateId,
            new FaultTreeHouseEventNode("Gate out of service", houseState));
        faultTree.Add(gateId,
            new FaultTreeBasicEventNode("Load exceedance", new ProbabilitySource(0.9d)));
        faultTree.Add(faultTree.Root.Id,
            new FaultTreeBasicEventNode("Structural failure", new ProbabilitySource(0.2d)));
        var response = new FaultTreeResponse(new[] { 0d, 1d }, faultTree)
        {
            Name = "Spillway fault tree",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
        return (BuildAnalysisOver(response), response.Id, houseId);
    }

    /// <summary>Builds a one-component analysis over one fault-tree response.</summary>
    /// <param name="response">The fault-tree response.</param>
    /// <returns>The analysis.</returns>
    private static RiskAnalysis BuildAnalysisOver(FaultTreeResponse response)
    {
        var hazard = new TabularHazard
        {
            Name = "Stage frequency",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            NoUncertaintyFunction = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(0.999d, new Deterministic(0d)),
                    new UncertainOrdinate(0.5d, new Deterministic(0.5d)),
                    new UncertainOrdinate(0.001d, new Deterministic(1d)),
                },
                true, SortOrder.Descending, true, SortOrder.Ascending,
                UnivariateDistributionType.Deterministic),
        };
        var consequence = new TabularConsequence
        {
            Name = "Failure loss",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(0d, new Deterministic(500d)),
                    new UncertainOrdinate(1d, new Deterministic(1000d)),
                },
                true, SortOrder.Ascending, false, SortOrder.None,
                UnivariateDistributionType.Deterministic),
        };
        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = hazard;
        component.AddFailureMode(new FailureMode(null, null, response, consequence));
        return new RiskAnalysis(new[] { component })
        {
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
        };
    }
}
