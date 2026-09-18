using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Analyses.Risk;

namespace RMC.TotalRisk.Tests.Analyses.Risk;

/// <summary>
/// Unit tests for <see cref="FractilePin"/> — the immutable epistemic conditioning pin: eager
/// argument guards and the value surface.
/// </summary>
[TestClass]
public class FractilePinTests
{
    /// <summary>Verifies construction stores the id and percentile.</summary>
    [TestMethod]
    public void Test_Construction_StoresValues()
    {
        // Arrange / Act
        var id = Guid.NewGuid();
        var pin = new FractilePin(id, 0.95d);

        // Assert
        Assert.AreEqual(id, pin.FunctionId);
        Assert.AreEqual(0.95d, pin.Percentile, 0d);
    }

    /// <summary>Verifies the eager argument guards: empty id and out-of-range percentiles.</summary>
    [TestMethod]
    public void Test_Construction_Guards()
    {
        // Act / Assert
        Assert.ThrowsException<ArgumentException>(() => new FractilePin(Guid.Empty, 0.5d));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new FractilePin(Guid.NewGuid(), 0d));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new FractilePin(Guid.NewGuid(), 1d));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new FractilePin(Guid.NewGuid(), -0.1d));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new FractilePin(Guid.NewGuid(), double.NaN));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new FractilePin(Guid.NewGuid(), double.PositiveInfinity));
    }
}
