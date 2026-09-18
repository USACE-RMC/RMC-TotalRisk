using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Results.CostBenefit;

namespace RMC.TotalRisk.Tests.Results.CostBenefit;

/// <summary>
/// Tests the alternative-economics row: guards and the per-slot echo.
/// </summary>
[TestClass]
public class AlternativeEconomicsTests
{
    /// <summary>Builds a row with distinct values in every slot.</summary>
    /// <param name="name">The row name.</param>
    /// <returns>The row.</returns>
    private static AlternativeEconomics Build(string name = "Gate fix")
    {
        return new AlternativeEconomics(name, "Fix the gate", isBaseline: false,
            1d, 2d, 3d, 6d, 0.5d, 7d,
            10d, 9d, 4d, 0.4d, 1.67d,
            9.5d, 8.5d, 3.5d, 0.35d, 1.58d,
            1e-3, 5e-4, 2e-3, 1e-3, 0.002d);
    }

    /// <summary>Verifies null names are refused.</summary>
    [TestMethod]
    public void Test_Ctor_NullArguments_Throw()
    {
        // Act / Assert
        Assert.ThrowsException<ArgumentNullException>(() => new AlternativeEconomics(null!, "", false,
            0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d));
        Assert.ThrowsException<ArgumentNullException>(() => new AlternativeEconomics("Baseline", null!, true,
            0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d));
    }

    /// <summary>
    /// Verifies the appended decision-framework columns default to skipped values and echo
    /// explicit values, with the offending-type list defensively copied.
    /// </summary>
    [TestMethod]
    public void Test_Ctor_DecisionColumns_DefaultsAndEcho()
    {
        // Act — the defaults.
        var defaulted = Build();

        // Assert
        Assert.IsTrue(double.IsNaN(defaulted.TotalExpectedAnnualCost));
        Assert.IsTrue(double.IsNaN(defaulted.AbsorbingTotalExpectedAnnualCost));
        Assert.IsTrue(double.IsNaN(defaulted.CostPerStatisticalLifeSavedUnadjusted));
        Assert.IsTrue(double.IsNaN(defaulted.CostPerStatisticalLifeSavedAdjusted));
        Assert.IsTrue(double.IsNaN(defaulted.EquityWeightedAdjustedCostPerStatisticalLifeSaved));
        Assert.IsTrue(double.IsNaN(defaulted.CostPerStatisticalFailurePrevented));
        Assert.IsTrue(double.IsNaN(defaulted.AbsorbingAdjustedCostPerStatisticalLifeSaved));
        Assert.IsTrue(double.IsNaN(defaulted.DisproportionalityRatio));
        Assert.AreEqual(string.Empty, defaulted.AlarpBand);
        Assert.IsFalse(defaulted.FailsDoNoHarm);
        Assert.AreEqual(0, defaulted.DoNoHarmOffendingTypes.Count);
        Assert.IsTrue(double.IsNaN(defaulted.BaselineIndividualRiskUsed));
        Assert.IsTrue(double.IsNaN(defaulted.AlternativeIndividualRiskUsed));
        Assert.IsFalse(defaulted.IndividualRiskIsProxy);

        // Act — explicit values, with a mutable offending list copied at construction.
        var offending = new System.Collections.Generic.List<int> { 0, 2 };
        var row = new AlternativeEconomics("Fix", "d", false,
            1d, 2d, 3d, 6d, 0.5d, 7d, 10d, 9d, 4d, 0.4d, 1.67d,
            9.5d, 8.5d, 3.5d, 0.35d, 1.58d, 1e-3, 5e-4, 2e-3, 1e-3, 0.002d,
            totalExpectedAnnualCost: 150d, absorbingTotalExpectedAnnualCost: 149d,
            costPerStatisticalLifeSavedUnadjusted: 30000d,
            costPerStatisticalLifeSavedAdjusted: 20000d,
            equityWeightedAdjustedCostPerStatisticalLifeSaved: 2000d,
            costPerStatisticalFailurePrevented: 120000d,
            absorbingAdjustedCostPerStatisticalLifeSaved: 10000d,
            disproportionalityRatio: 0.0034d, alarpBand: "Very Strong",
            failsDoNoHarm: true, doNoHarmOffendingTypes: offending,
            baselineIndividualRiskUsed: 1e-3, alternativeIndividualRiskUsed: 1e-4,
            individualRiskIsProxy: true);
        offending.Add(9);

        // Assert
        Assert.AreEqual(150d, row.TotalExpectedAnnualCost);
        Assert.AreEqual(149d, row.AbsorbingTotalExpectedAnnualCost);
        Assert.AreEqual(30000d, row.CostPerStatisticalLifeSavedUnadjusted);
        Assert.AreEqual(20000d, row.CostPerStatisticalLifeSavedAdjusted);
        Assert.AreEqual(2000d, row.EquityWeightedAdjustedCostPerStatisticalLifeSaved);
        Assert.AreEqual(120000d, row.CostPerStatisticalFailurePrevented);
        Assert.AreEqual(10000d, row.AbsorbingAdjustedCostPerStatisticalLifeSaved);
        Assert.AreEqual(0.0034d, row.DisproportionalityRatio);
        Assert.AreEqual("Very Strong", row.AlarpBand);
        Assert.IsTrue(row.FailsDoNoHarm);
        CollectionAssert.AreEqual(new[] { 0, 2 }, (System.Collections.ICollection)row.DoNoHarmOffendingTypes);
        Assert.AreEqual(1e-3, row.BaselineIndividualRiskUsed);
        Assert.AreEqual(1e-4, row.AlternativeIndividualRiskUsed);
        Assert.IsTrue(row.IndividualRiskIsProxy);
    }

    /// <summary>Verifies every slot echoes its own value.</summary>
    [TestMethod]
    public void Test_Ctor_PropertiesEchoed()
    {
        // Act
        var row = Build();

        // Assert
        Assert.AreEqual("Gate fix", row.Name);
        Assert.AreEqual("Fix the gate", row.Description);
        Assert.IsFalse(row.IsBaseline);
        Assert.AreEqual(1d, row.CapitalPresentValue);
        Assert.AreEqual(2d, row.OperationsAndMaintenancePresentValue);
        Assert.AreEqual(3d, row.OperatingChangePresentValue);
        Assert.AreEqual(6d, row.TotalCostPresentValue);
        Assert.AreEqual(0.5d, row.EquivalentAnnualCost);
        Assert.AreEqual(7d, row.CumulativeCost);
        Assert.AreEqual(10d, row.MonetizedPresentValueBenefit);
        Assert.AreEqual(9d, row.EconomicPresentValueBenefit);
        Assert.AreEqual(4d, row.NetPresentValue);
        Assert.AreEqual(0.4d, row.NetAnnualBenefit);
        Assert.AreEqual(1.67d, row.BenefitCostRatio);
        Assert.AreEqual(9.5d, row.AbsorbingMonetizedPresentValueBenefit);
        Assert.AreEqual(8.5d, row.AbsorbingEconomicPresentValueBenefit);
        Assert.AreEqual(3.5d, row.AbsorbingNetPresentValue);
        Assert.AreEqual(0.35d, row.AbsorbingNetAnnualBenefit);
        Assert.AreEqual(1.58d, row.AbsorbingBenefitCostRatio);
        Assert.AreEqual(1e-3, row.AnnualizedFailureProbability);
        Assert.AreEqual(5e-4, row.AnnualizedFailureProbabilityReduction);
        Assert.AreEqual(2e-3, row.YearZeroFailureProbability);
        Assert.AreEqual(1e-3, row.YearZeroFailureProbabilityReduction);
        Assert.AreEqual(0.002d, row.LivesSavedEquivalentAnnual);
    }
}
