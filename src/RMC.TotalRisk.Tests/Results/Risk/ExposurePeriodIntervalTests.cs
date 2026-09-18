using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Tests.Results.Risk;

/// <summary>
/// Unit tests for <see cref="ExposurePeriodInterval"/> — the plain reduced-quantity container.
/// </summary>
[TestClass]
public class ExposurePeriodIntervalTests
{
    /// <summary>Verifies construction stores the four slots.</summary>
    [TestMethod]
    public void Test_Construction_StoresValues()
    {
        // Act
        var interval = new ExposurePeriodInterval(1d, 2d, 2.5d, 4d);

        // Assert
        Assert.AreEqual(1d, interval.Lower, 0d);
        Assert.AreEqual(2d, interval.Median, 0d);
        Assert.AreEqual(2.5d, interval.Mean, 0d);
        Assert.AreEqual(4d, interval.Upper, 0d);
    }

    /// <summary>Verifies the all-NaN convention passes through unchanged.</summary>
    [TestMethod]
    public void Test_Construction_AllNaN()
    {
        // Act
        var interval = new ExposurePeriodInterval(double.NaN, double.NaN, double.NaN, double.NaN);

        // Assert
        Assert.IsTrue(double.IsNaN(interval.Lower) && double.IsNaN(interval.Median)
            && double.IsNaN(interval.Mean) && double.IsNaN(interval.Upper));
    }
}
