using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Analyses.CostBenefit;
using RMC.TotalRisk.Analyses.LifeCycle;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.RiskFunctions.Hazards;

namespace RMC.TotalRisk.Tests.Analyses.CostBenefit;

/// <summary>
/// Tests the staged life-cycle plan: guards, the duplicate-year refusal, snapshots, and the
/// XML round trip carrying house-event and self-contained hazard-replacement payloads.
/// </summary>
[TestClass]
public class LifeCyclePlanTests
{
    /// <summary>Builds a deterministic stage-frequency hazard for replacement payloads.</summary>
    /// <returns>The hazard.</returns>
    private static TabularHazard Hazard()
    {
        return new TabularHazard
        {
            Name = "Future stage frequency",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            NoUncertaintyFunction = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(0.999d, new Deterministic(0d)),
                    new UncertainOrdinate(0.001d, new Deterministic(1d)),
                },
                true, SortOrder.Descending, true, SortOrder.Ascending,
                UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Verifies the construction guards.</summary>
    [TestMethod]
    public void Test_Ctor_Guards()
    {
        // Arrange
        var intervention = new LifeCycleIntervention(10,
            new[] { new HouseEventState(Guid.NewGuid(), Guid.NewGuid(), true) });

        // Act / Assert
        Assert.ThrowsException<ArgumentNullException>(() => new LifeCyclePlan((IReadOnlyList<LifeCycleIntervention>)null!));
        Assert.ThrowsException<ArgumentException>(() => new LifeCyclePlan(new LifeCycleIntervention[] { null! }));
        Assert.ThrowsException<ArgumentException>(() => new LifeCyclePlan(new[]
        {
            intervention,
            new LifeCycleIntervention(10, new[] { new HouseEventState(Guid.NewGuid(), Guid.NewGuid(), false) }),
        }));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new LifeCyclePlan(
            Array.Empty<LifeCycleIntervention>(), new[] { -1 }));
    }

    /// <summary>Verifies an evaluation-years-only plan is legal and the lists snapshot.</summary>
    [TestMethod]
    public void Test_Ctor_EvaluationYearsOnly_SnapshotsHeld()
    {
        // Arrange
        var years = new List<int> { 10, 20 };

        // Act
        var plan = new LifeCyclePlan(Array.Empty<LifeCycleIntervention>(), years);
        years.Clear();

        // Assert
        Assert.AreEqual(0, plan.Interventions.Count);
        Assert.AreEqual(2, plan.EvaluationYears.Count);
        Assert.AreEqual(10, plan.EvaluationYears[0]);
    }

    /// <summary>
    /// Verifies the XML round trip: the evaluation years, the house-event payload, and the
    /// self-contained hazard-replacement payload all restore with matching content.
    /// </summary>
    [TestMethod]
    public void Test_Serialization_RoundTrip()
    {
        // Arrange
        var functionId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var replacement = Hazard();
        var plan = new LifeCyclePlan(new[]
        {
            new LifeCycleIntervention(0, new[] { new HouseEventState(functionId, nodeId, true) }),
            new LifeCycleIntervention(20, null, new[] { new HazardReplacement(replacement.Id, replacement) }),
        }, new[] { 10, 30 });

        // Act
        var restored = new LifeCyclePlan(plan.ToXElement());

        // Assert — the grid years and both intervention payloads survive.
        CollectionAssert.AreEqual(new[] { 10, 30 }, new List<int>(restored.EvaluationYears));
        Assert.AreEqual(2, restored.Interventions.Count);
        Assert.AreEqual(0, restored.Interventions[0].Year);
        Assert.AreEqual(functionId, restored.Interventions[0].HouseEvents[0].FunctionId);
        Assert.AreEqual(nodeId, restored.Interventions[0].HouseEvents[0].NodeId);
        Assert.IsTrue(restored.Interventions[0].HouseEvents[0].State);
        Assert.AreEqual(20, restored.Interventions[1].Year);
        var restoredReplacement = restored.Interventions[1].HazardReplacements[0];
        Assert.AreEqual(replacement.Id, restoredReplacement.TargetFunctionId);
        Assert.AreEqual(replacement.Id, restoredReplacement.Replacement.Id,
            "The self-contained payload must preserve the replacement function's id.");
        Assert.AreEqual(Convert.ToHexString(replacement.CanonicalHash()),
            Convert.ToHexString(restoredReplacement.Replacement.CanonicalHash()),
            "The self-contained payload must preserve the replacement function's content.");
        Assert.ThrowsException<ArgumentNullException>(() => new LifeCyclePlan((System.Xml.Linq.XElement)null!));
    }
}
