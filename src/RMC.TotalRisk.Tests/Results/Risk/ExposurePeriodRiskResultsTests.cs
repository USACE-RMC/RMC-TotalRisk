using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Tests.Results.Risk;

/// <summary>
/// Unit tests for <see cref="ExposurePeriodRiskResults"/> — the plain query container: value
/// storage and the null guards.
/// </summary>
[TestClass]
public class ExposurePeriodRiskResultsTests
{
    /// <summary>Builds a distinct interval.</summary>
    private static ExposurePeriodInterval Interval(double seed)
    {
        return new ExposurePeriodInterval(seed, seed + 1d, seed + 1.5d, seed + 2d);
    }

    /// <summary>Verifies construction stores every slot.</summary>
    [TestMethod]
    public void Test_Construction_StoresValues()
    {
        // Act
        var results = new ExposurePeriodRiskResults("Total — System", 50, 0.03d, 0, 100,
            Interval(0d), Interval(10d), Interval(20d), Interval(30d));

        // Assert
        Assert.AreEqual("Total — System", results.OutputLabel);
        Assert.AreEqual(50, results.PeriodYears);
        Assert.AreEqual(0.03d, results.DiscountRate, 0d);
        Assert.AreEqual(0, results.ConsequenceTypeIndex);
        Assert.AreEqual(100, results.ValidRealizations);
        Assert.AreEqual(0d, results.PeriodFailureProbability.Lower, 0d);
        Assert.AreEqual(11d, results.CumulativeExpectedConsequence.Median, 0d);
        Assert.AreEqual(21.5d, results.PresentValueOfExpectedConsequences.Mean, 0d);
        Assert.AreEqual(32d, results.EquivalentAnnualConsequence.Upper, 0d);
    }

    /// <summary>Verifies the null guards on every interval slot.</summary>
    [TestMethod]
    public void Test_Construction_NullGuards()
    {
        // Act / Assert
        Assert.ThrowsException<ArgumentNullException>(() => new ExposurePeriodRiskResults(
            "L", 1, 0d, 0, 1, null!, Interval(0d), Interval(0d), Interval(0d)));
        Assert.ThrowsException<ArgumentNullException>(() => new ExposurePeriodRiskResults(
            "L", 1, 0d, 0, 1, Interval(0d), null!, Interval(0d), Interval(0d)));
        Assert.ThrowsException<ArgumentNullException>(() => new ExposurePeriodRiskResults(
            "L", 1, 0d, 0, 1, Interval(0d), Interval(0d), null!, Interval(0d)));
        Assert.ThrowsException<ArgumentNullException>(() => new ExposurePeriodRiskResults(
            "L", 1, 0d, 0, 1, Interval(0d), Interval(0d), Interval(0d), null!));
    }
}
