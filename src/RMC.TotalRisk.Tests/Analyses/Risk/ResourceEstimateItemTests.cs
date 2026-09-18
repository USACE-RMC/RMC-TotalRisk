using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Core.Enums;

namespace RMC.TotalRisk.Tests.Analyses.Risk;

/// <summary>Unit tests for <see cref="ResourceEstimateItem"/> estimate lines.</summary>
/// <remarks>
/// <para><b>Authors:</b> Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil</para>
/// </remarks>
[TestClass]
public class ResourceEstimateItemTests
{
    /// <summary>Verifies constructor values and null text coercion.</summary>
    [TestMethod]
    public void Test_Constructor_PreservesValues()
    {
        var item = new ResourceEstimateItem(null!, 1024, 42d, true, ResourceSeverity.Warning, null!);

        Assert.AreEqual(string.Empty, item.Label);
        Assert.AreEqual(1024L, item.Bytes);
        Assert.AreEqual(42d, item.Operations);
        Assert.IsTrue(item.IsRetained);
        Assert.AreEqual(ResourceSeverity.Warning, item.Severity);
        Assert.AreEqual(string.Empty, item.Message);
    }

    /// <summary>Verifies negative and non-finite estimates are rejected.</summary>
    [TestMethod]
    public void Test_Constructor_RejectsInvalidQuantities()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new ResourceEstimateItem("negative", -1L, 0d, false, ResourceSeverity.Error, string.Empty));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new ResourceEstimateItem("negative", 0L, -1d, false, ResourceSeverity.Error, string.Empty));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new ResourceEstimateItem("infinite", 0L, double.PositiveInfinity, false, ResourceSeverity.Error, string.Empty));
    }
}
