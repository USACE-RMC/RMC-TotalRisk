using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Tests.Results.Risk;

/// <summary>
/// Unit tests for <see cref="Ensemble"/> — the runtime per-realization store.
/// </summary>
[TestClass]
public class EnsembleTests
{
    /// <summary>Verifies sizing, indexed writes, and clearing.</summary>
    [TestMethod]
    public void Test_IndexerAndLifecycle()
    {
        // Arrange
        var ensemble = new Ensemble(3);
        var realization = new SystemRealization();

        // Act
        ensemble[1] = realization;

        // Assert
        Assert.AreEqual(3, ensemble.Count);
        Assert.IsNull(ensemble[0]);
        Assert.AreSame(realization, ensemble[1]);

        ensemble.Clear();
        Assert.AreEqual(0, ensemble.Count);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new Ensemble(-1));
    }
}
