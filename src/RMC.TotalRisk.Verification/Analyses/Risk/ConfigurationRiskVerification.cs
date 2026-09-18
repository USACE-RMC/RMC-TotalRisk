using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.Systems.Components;

namespace RMC.TotalRisk.Verification.Analyses.Risk;

/// <summary>
/// Configuration-risk verification. The primary oracle is structural: a configured clone is
/// content-identical to a directly re-authored model with the same house-event states, so the
/// query's baseline and configured quantifications must equal independent mean-only runs of
/// re-authored twins bit-exactly, at system and component scope, across multiple components and
/// through a cross-kind nesting (a house event inside a fault tree referenced as an event-tree
/// probability source). A flat-response closed form anchors the algebra exactly, and the
/// authored model's published full-run results are pinned byte-untouched by the query.
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// <para>
/// The fixtures keep flat conditional responses so the closed forms are exact: with a flat
/// system response probability the annual failure probability equals that probability (the
/// hazard mass integrates to one exactly), and the expected-consequence ratio between the
/// configured and baseline states equals the response ratio because the flat response factors
/// out of the consequence integral. The queried states avoid the exact certain-failure response
/// (a flat probability of one), which the engine's integration rejects loudly for any model,
/// independent of this query.
/// </para>
/// </remarks>
[TestClass]
public class ConfigurationRiskVerification
{
    /// <summary>
    /// Verifies the query against re-authored twins over a two-component system: baseline and
    /// configured annual failure probabilities and expected consequences are bit-equal at
    /// system and component scope, and the component without the house event is bit-unchanged.
    /// </summary>
    [TestMethod]
    public void Test_Configuration_MatchesReauthoredSystem_BitExact()
    {
        RiskAnalysis author = BuildTwoComponentAnalysis(houseState: false,
            out Guid functionId, out Guid houseId);

        ConfigurationRiskResults results = author.MeasureConfigurationRisk(
            new[] { new HouseEventState(functionId, houseId, true) });

        RiskAnalysis baselineTwin = BuildTwoComponentAnalysis(houseState: false, out _, out _);
        baselineTwin.RunAsync().GetAwaiter().GetResult();
        RiskAnalysis configuredTwin = BuildTwoComponentAnalysis(houseState: true, out _, out _);
        configuredTwin.RunAsync().GetAwaiter().GetResult();

        Assert.AreEqual(baselineTwin.MeanRiskResults!.Curves.Fail.TotalProbability,
            results.System.BaselineFailureProbability);
        Assert.AreEqual(configuredTwin.MeanRiskResults!.Curves.Fail.TotalProbability,
            results.System.ConfiguredFailureProbability);
        Assert.AreEqual(baselineTwin.MeanRiskResults.Curves.Total.Mean,
            results.System.BaselineExpectedConsequences[0]);
        Assert.AreEqual(configuredTwin.MeanRiskResults.Curves.Total.Mean,
            results.System.ConfiguredExpectedConsequences[0]);
        for (int i = 0; i < 2; i++)
        {
            Assert.AreEqual(baselineTwin.MeanRiskResults.Components[i].Curves.Fail.TotalProbability,
                results.Components[i].BaselineFailureProbability);
            Assert.AreEqual(configuredTwin.MeanRiskResults.Components[i].Curves.Fail.TotalProbability,
                results.Components[i].ConfiguredFailureProbability);
            Assert.AreEqual(baselineTwin.MeanRiskResults.Components[i].Curves.Total.Mean,
                results.Components[i].BaselineExpectedConsequences[0]);
            Assert.AreEqual(configuredTwin.MeanRiskResults.Components[i].Curves.Total.Mean,
                results.Components[i].ConfiguredExpectedConsequences[0]);
        }
        Assert.AreEqual(0d, results.Components[1].FailureProbabilityChange,
            "The component without the house event must be bit-unchanged.");
        Assert.AreEqual(0d, results.Components[1].ExpectedConsequenceChanges[0]);
    }

    /// <summary>
    /// Verifies the exact flat-response closed forms: baseline probability 0.2, configured
    /// probability 1 − (1 − 0.9)(1 − 0.2) = 0.92, change 0.72, ratio 4.6, and the identical
    /// 4.6 ratio on the expected consequences because the flat response factors out of the
    /// consequence integral.
    /// </summary>
    [TestMethod]
    public void Test_Configuration_FlatResponseClosedForm_Exact()
    {
        RiskAnalysis author = BuildSingleComponentAnalysis(houseState: false,
            out Guid functionId, out Guid houseId);

        ConfigurationRiskResults results = author.MeasureConfigurationRisk(
            new[] { new HouseEventState(functionId, houseId, true) });

        Assert.AreEqual(0.2d, results.System.BaselineFailureProbability, 1e-12d);
        Assert.AreEqual(0.92d, results.System.ConfiguredFailureProbability, 1e-12d);
        Assert.AreEqual(0.72d, results.System.FailureProbabilityChange, 1e-12d);
        Assert.AreEqual(4.6d, results.System.FailureProbabilityRatio, 1e-10d);
        Assert.AreEqual(4.6d,
            results.System.ConfiguredExpectedConsequences[0] / results.System.BaselineExpectedConsequences[0],
            1e-10d, "The flat response must factor out of the consequence integral.");
    }

    /// <summary>
    /// Verifies the cross-kind nested reach: a house event inside a fault tree referenced as an
    /// event-tree chance probability source is reconfigured by the query, bit-equal to the
    /// directly re-authored nested variant.
    /// </summary>
    [TestMethod]
    public void Test_Configuration_EventTreeCarriedFaultTree_Reached()
    {
        RiskAnalysis author = BuildEventTreeCarriedAnalysis(houseState: false,
            out Guid functionId, out Guid houseId);

        ConfigurationRiskResults results = author.MeasureConfigurationRisk(
            new[] { new HouseEventState(functionId, houseId, true) });

        RiskAnalysis configuredTwin = BuildEventTreeCarriedAnalysis(houseState: true, out _, out _);
        configuredTwin.RunAsync().GetAwaiter().GetResult();

        Assert.AreEqual(configuredTwin.MeanRiskResults!.Curves.Fail.TotalProbability,
            results.System.ConfiguredFailureProbability);
        Assert.IsTrue(results.System.FailureProbabilityChange > 0d,
            "Forcing the tree-carried house event true must raise the failure probability.");
        StringAssert.Contains(results.AppliedOverrides[0], "Carried gate");
    }

    /// <summary>
    /// Verifies the authored model is byte-untouched at full-run scale: the published ensemble
    /// JSON, the estimated state, the component hash, and the authored house state are all
    /// unchanged by the query.
    /// </summary>
    [TestMethod]
    public void Test_Configuration_AuthorFullRun_ByteUntouched()
    {
        RiskAnalysis author = BuildSingleComponentAnalysis(houseState: false,
            out Guid functionId, out Guid houseId, uncertainConsequence: true);
        author.Options.EstimateMeanRiskOnly = false;
        author.Options.Realizations = 200;
        author.RunAsync().GetAwaiter().GetResult();
        string publishedJson = author.RiskResults!.ToJson();
        string componentHash = Convert.ToHexString(author.Components[0].CanonicalHash());

        ConfigurationRiskResults results = author.MeasureConfigurationRisk(
            new[] { new HouseEventState(functionId, houseId, true) });

        Assert.IsTrue(author.IsEstimated);
        Assert.AreEqual(publishedJson, author.RiskResults!.ToJson(),
            "The query must not move a byte of the published full-run results.");
        Assert.AreEqual(componentHash, Convert.ToHexString(author.Components[0].CanonicalHash()));
        Assert.IsTrue(results.System.FailureProbabilityChange > 0d);
    }

    /// <summary>Builds the standard flat OR(AND(house, 0.9), 0.2) fault-tree response.</summary>
    /// <param name="houseState">The authored house state.</param>
    /// <param name="houseId">The house-event node id.</param>
    /// <returns>The response.</returns>
    private static FaultTreeResponse BuildFaultResponse(bool houseState, out Guid houseId)
    {
        var faultTree = new FaultTree();
        Guid gateId = faultTree.Add(faultTree.Root.Id,
            new FaultTreeGateNode("Outage impact", FaultTreeGateType.And));
        houseId = faultTree.Add(gateId, new FaultTreeHouseEventNode("Gate out of service", houseState));
        faultTree.Add(gateId, new FaultTreeBasicEventNode("Load exceedance", new ProbabilitySource(0.9d)));
        faultTree.Add(faultTree.Root.Id,
            new FaultTreeBasicEventNode("Structural failure", new ProbabilitySource(0.2d)));
        return new FaultTreeResponse(new[] { 0d, 1d }, faultTree)
        {
            Name = "Spillway fault tree",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
    }

    /// <summary>Builds the deterministic hazard over stages zero to one.</summary>
    /// <returns>The hazard.</returns>
    private static TabularHazard BuildHazard()
    {
        return new TabularHazard
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
    }

    /// <summary>Builds the stage-to-loss consequence.</summary>
    /// <param name="uncertain">Whether the loss carries Normal uncertainty.</param>
    /// <returns>The consequence.</returns>
    private static TabularConsequence BuildConsequence(bool uncertain = false)
    {
        return new TabularConsequence
        {
            Name = "Failure loss",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
            UncertainOrderedPairedData = uncertain
                ? new UncertainOrderedPairedData(
                    new[]
                    {
                        new UncertainOrdinate(0d, new Normal(500d, 50d)),
                        new UncertainOrdinate(1d, new Normal(1000d, 100d)),
                    },
                    true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Normal)
                : new UncertainOrderedPairedData(
                    new[]
                    {
                        new UncertainOrdinate(0d, new Deterministic(500d)),
                        new UncertainOrdinate(1d, new Deterministic(1000d)),
                    },
                    true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds a one-component analysis over the standard fault tree.</summary>
    /// <param name="houseState">The authored house state.</param>
    /// <param name="functionId">The fault-tree function id.</param>
    /// <param name="houseId">The house-event node id.</param>
    /// <param name="uncertainConsequence">Whether the loss carries uncertainty.</param>
    /// <returns>The analysis.</returns>
    private static RiskAnalysis BuildSingleComponentAnalysis(bool houseState, out Guid functionId,
        out Guid houseId, bool uncertainConsequence = false)
    {
        FaultTreeResponse response = BuildFaultResponse(houseState, out houseId);
        functionId = response.Id;
        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = BuildHazard();
        component.AddFailureMode(new FailureMode(null, null, response, BuildConsequence(uncertainConsequence)));
        return new RiskAnalysis(new[] { component })
        {
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
        };
    }

    /// <summary>Builds a two-component analysis; only the first carries the house event.</summary>
    /// <param name="houseState">The authored house state.</param>
    /// <param name="functionId">The fault-tree function id.</param>
    /// <param name="houseId">The house-event node id.</param>
    /// <returns>The analysis.</returns>
    private static RiskAnalysis BuildTwoComponentAnalysis(bool houseState, out Guid functionId,
        out Guid houseId)
    {
        FaultTreeResponse response = BuildFaultResponse(houseState, out houseId);
        functionId = response.Id;
        var faultComponent = new SystemComponent { Name = "Dam" };
        faultComponent.HazardFunction = BuildHazard();
        faultComponent.AddFailureMode(new FailureMode(null, null, response, BuildConsequence()));

        var plainTree = new FaultTree();
        plainTree.Add(plainTree.Root.Id,
            new FaultTreeBasicEventNode("Levee breach", new ProbabilitySource(0.05d)));
        var plainResponse = new FaultTreeResponse(new[] { 0d, 1d }, plainTree)
        {
            Name = "Levee fault tree",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
        var plainComponent = new SystemComponent { Name = "Levee" };
        plainComponent.HazardFunction = BuildHazard();
        plainComponent.AddFailureMode(new FailureMode(null, null, plainResponse, BuildConsequence()));

        return new RiskAnalysis(new[] { faultComponent, plainComponent })
        {
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
        };
    }

    /// <summary>
    /// Builds an analysis whose response is an event tree with a chance node sourced by a fault
    /// tree carrying the house event — the cross-kind containment path.
    /// </summary>
    /// <param name="houseState">The authored house state.</param>
    /// <param name="functionId">The carried fault-tree function id.</param>
    /// <param name="houseId">The house-event node id.</param>
    /// <returns>The analysis.</returns>
    private static RiskAnalysis BuildEventTreeCarriedAnalysis(bool houseState, out Guid functionId,
        out Guid houseId)
    {
        var faultTree = new FaultTree();
        Guid gateId = faultTree.Add(faultTree.Root.Id,
            new FaultTreeGateNode("Carried outage impact", FaultTreeGateType.And));
        houseId = faultTree.Add(gateId, new FaultTreeHouseEventNode("Carried gate", houseState));
        faultTree.Add(gateId, new FaultTreeBasicEventNode("Carried load", new ProbabilitySource(0.7d)));
        faultTree.Add(faultTree.Root.Id,
            new FaultTreeBasicEventNode("Carried basic", new ProbabilitySource(0.1d)));
        var carried = new FaultTreeResponse(new[] { 0d, 1d }, faultTree)
        {
            Name = "Carried fault tree",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
        functionId = carried.Id;

        var eventTree = new EventTree();
        var loadCase = new ChanceNode("Load case", new ProbabilitySource(0.6d)) { IsFailure = false };
        eventTree.Add(eventTree.Root.Id, loadCase);
        eventTree.Add(eventTree.Root.Id, new RemainderNode("No load"));
        eventTree.Add(loadCase.Id, new ChanceNode("Subsystem failure", new ProbabilitySource(carried)));
        eventTree.Add(loadCase.Id, new RemainderNode("Survival"));
        var response = new EventTreeResponse(new[] { 0d, 1d }, eventTree)
        {
            Name = "Carrier event tree",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };

        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = BuildHazard();
        component.AddFailureMode(new FailureMode(null, null, response, BuildConsequence()));
        return new RiskAnalysis(new[] { component })
        {
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
        };
    }
}
