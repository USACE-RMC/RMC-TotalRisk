using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Analyses.CostBenefit;

namespace RMC.TotalRisk.Tests.Analyses.CostBenefit;

/// <summary>
/// Tests the v1.0 plan-economics parity anchor: the reference constants for positive rates,
/// the preserved truncation and constant-stream behaviors, the exact zero-rate limit, and
/// the guards.
/// </summary>
[TestClass]
public class PlanEconomicsTests
{
    /// <summary>
    /// Verifies the v1.0 reference constants (independently re-derived): the ramp-and-plateau
    /// case, the silent beyond-horizon truncation, the constant stream when the future year
    /// does not exceed the base year, and the long ramp at the planning defaults.
    /// </summary>
    [TestMethod]
    public void Test_EquivalentAnnualConsequences_V10ReferenceConstants()
    {
        // Act / Assert — ramp 100 → 160 over two years, then the plateau (r = 0.1, n = 4).
        Assert.AreEqual(134.970911441500d,
            PlanEconomics.EquivalentAnnualConsequences(100d, 0, 160d, 2, 0.1d, 4), 1e-9);

        // The future year beyond the horizon silently truncates the ramp — preserved v1.0
        // behavior; the plateau is never reached.
        Assert.AreEqual(108.287007110536d,
            PlanEconomics.EquivalentAnnualConsequences(100d, 0, 160d, 10, 0.1d, 4), 1e-9);

        // A future year at (or before) the base year makes the whole stream the future value,
        // and the equivalent-annual amount of a constant stream is that constant.
        Assert.AreEqual(160d,
            PlanEconomics.EquivalentAnnualConsequences(100d, 0, 160d, 0, 0.1d, 4), 1e-9);

        // Calendar years at the federal planning defaults (7%, 30 years, a 50-year ramp).
        Assert.AreEqual(119.497368419047d,
            PlanEconomics.EquivalentAnnualConsequences(100d, 2026, 200d, 2076, 0.07d, 30), 1e-9);
    }

    /// <summary>
    /// Verifies the zero-rate limit is the exact average of the year values — the documented
    /// deliberate improvement over v1.0's indeterminate capital recovery factor.
    /// </summary>
    [TestMethod]
    public void Test_EquivalentAnnualConsequences_ZeroRate_ExactAverage()
    {
        // Act — the case-one stream at r = 0: values 100, 130, 160, 160 average exactly.
        double actual = PlanEconomics.EquivalentAnnualConsequences(100d, 0, 160d, 2, 0d, 4);

        // Assert — 550/4 is exactly representable, so the limit is exact.
        Assert.AreEqual(137.5d, actual, 0d);
    }

    /// <summary>Verifies the guards: negative or non-finite rates and sub-year periods throw.</summary>
    [TestMethod]
    public void Test_EquivalentAnnualConsequences_Guards()
    {
        // Act / Assert
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => PlanEconomics.EquivalentAnnualConsequences(100d, 0, 160d, 2, -0.01d, 4));
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => PlanEconomics.EquivalentAnnualConsequences(100d, 0, 160d, 2, double.NaN, 4));
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => PlanEconomics.EquivalentAnnualConsequences(100d, 0, 160d, 2, 0.1d, 0));
    }
}
