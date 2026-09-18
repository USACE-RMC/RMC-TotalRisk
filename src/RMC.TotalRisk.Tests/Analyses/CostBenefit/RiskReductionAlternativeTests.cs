using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Analyses.CostBenefit;
using RMC.TotalRisk.Analyses.LifeCycle;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Systems.Components;

namespace RMC.TotalRisk.Tests.Analyses.CostBenefit;

/// <summary>
/// Tests the risk-reduction alternative: guards, the empty-cost default, and reference
/// semantics on the shared system.
/// </summary>
[TestClass]
public class RiskReductionAlternativeTests
{
    /// <summary>Verifies the construction guards.</summary>
    [TestMethod]
    public void Test_Ctor_Guards()
    {
        // Arrange
        var system = new RiskAnalysis(Array.Empty<SystemComponent>());

        // Act / Assert
        Assert.ThrowsException<ArgumentException>(() => new RiskReductionAlternative(" ", system));
        Assert.ThrowsException<ArgumentNullException>(() => new RiskReductionAlternative("Baseline", null!));
    }

    /// <summary>Verifies the properties echo, with the empty-cost and empty-description defaults.</summary>
    [TestMethod]
    public void Test_Ctor_PropertiesEchoed_Defaults()
    {
        // Arrange
        var system = new RiskAnalysis(Array.Empty<SystemComponent>());
        var costs = new CostStream(new[] { new CapitalCostEntry(0, 1000d) });
        var plan = new LifeCyclePlan(Array.Empty<LifeCycleIntervention>(), new[] { 10 });

        // Act
        var baseline = new RiskReductionAlternative("Existing condition", system);
        var alternative = new RiskReductionAlternative("Gate fix", system, costs, plan, "Fix the gate");

        // Assert — the baseline defaults.
        Assert.AreEqual("Existing condition", baseline.Name);
        Assert.AreEqual(string.Empty, baseline.Description);
        Assert.AreEqual(0, baseline.Costs.Capital.Count);
        Assert.IsNull(baseline.Plan);

        // The full form echoes, and the system is referenced, not copied.
        Assert.AreEqual("Gate fix", alternative.Name);
        Assert.AreEqual("Fix the gate", alternative.Description);
        Assert.IsTrue(ReferenceEquals(system, alternative.System));
        Assert.IsTrue(ReferenceEquals(system, baseline.System),
            "Alternatives may share one analysis instance.");
        Assert.IsTrue(ReferenceEquals(costs, alternative.Costs));
        Assert.IsTrue(ReferenceEquals(plan, alternative.Plan));
    }
}
