using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Core.Enums;

namespace RMC.TotalRisk.Tests.Analyses.Risk;

/// <summary>Unit tests for <see cref="ResourceEstimate"/> aggregation and immutable items.</summary>
/// <remarks>
/// <para><b>Authors:</b> Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil</para>
/// </remarks>
[TestClass]
public class ResourceEstimateTests
{
    /// <summary>Verifies retained/transient totals, severity, messages, and defensive snapshots.</summary>
    [TestMethod]
    public void Test_AggregatesAndSnapshotsItems()
    {
        var items = new List<ResourceEstimateItem>
        {
            new ResourceEstimateItem("retained", 100, 0d, true, ResourceSeverity.Informational, "retained"),
            new ResourceEstimateItem("transient", 50, 10d, false, ResourceSeverity.Warning, "warning"),
        };
        var estimate = new ResourceEstimate(items, 2, 42d, 84d, 3d);
        items.Clear();

        Assert.AreEqual(2, estimate.Items.Count);
        Assert.AreEqual(100L, estimate.RetainedBytes);
        Assert.AreEqual(50L, estimate.PeakTransientBytes);
        Assert.AreEqual(150L, estimate.PeakLiveBytes);
        Assert.AreEqual(42d, estimate.EstimatedIntegrandEvaluationFloor);
        Assert.AreEqual(84d, estimate.EstimatedIntegrandEvaluationCeiling);
        Assert.AreEqual(ResourceSeverity.Warning, estimate.Severity);
        CollectionAssert.AreEqual(new[] { "warning" }, estimate.Messages(ResourceSeverity.Warning));
        Assert.ThrowsException<NotSupportedException>(() =>
            ((IList<ResourceEstimateItem>)estimate.Items).Clear());
    }

    /// <summary>Verifies overflow saturation and invalid work ranges.</summary>
    [TestMethod]
    public void Test_SaturatesByteTotalsAndRejectsInvalidRanges()
    {
        var items = new[]
        {
            new ResourceEstimateItem("first", long.MaxValue, 0d, true,
                ResourceSeverity.Informational, string.Empty),
            new ResourceEstimateItem("second", 1L, 0d, true,
                ResourceSeverity.Informational, string.Empty),
            new ResourceEstimateItem("transient", long.MaxValue, 0d, false,
                ResourceSeverity.Informational, string.Empty),
        };

        var estimate = new ResourceEstimate(items, 1, 1d, 2d, 0d);

        Assert.AreEqual(long.MaxValue, estimate.RetainedBytes);
        Assert.AreEqual(long.MaxValue, estimate.PeakTransientBytes);
        Assert.AreEqual(long.MaxValue, estimate.PeakLiveBytes);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new ResourceEstimate(items, 0, 0d, 0d, 0d));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new ResourceEstimate(items, 1, 2d, 1d, 0d));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new ResourceEstimate(items, 1, 0d, 1d, double.NaN));
    }
}
