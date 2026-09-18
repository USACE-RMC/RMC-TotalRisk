using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Results.CostBenefit;

namespace RMC.TotalRisk.Tests.Results.CostBenefit;

/// <summary>
/// Tests the ε-sweep entry row: the per-slot echo and the null-name normalization.
/// </summary>
[TestClass]
public class EpsilonSweepEntryTests
{
    /// <summary>Verifies every slot echoes and a null selection normalizes to empty.</summary>
    [TestMethod]
    public void Test_Ctor_PropertiesEchoed()
    {
        // Act
        var entry = new EpsilonSweepEntry(0.5d, "Gate repair", 20d, 0.4d, 5d, 3, epsilonBinding: true,
            isInfeasible: false);
        var infeasible = new EpsilonSweepEntry(0.1d, null, double.NaN, double.NaN, double.NaN, 0,
            epsilonBinding: true, isInfeasible: true);

        // Assert
        Assert.AreEqual(0.5d, entry.Epsilon);
        Assert.AreEqual("Gate repair", entry.SelectedAlternative);
        Assert.AreEqual(20d, entry.PrimaryValue);
        Assert.AreEqual(0.4d, entry.EpsilonValue);
        Assert.AreEqual(5d, entry.TradeOffRatio);
        Assert.AreEqual(3, entry.FeasibleCount);
        Assert.IsTrue(entry.EpsilonBinding);
        Assert.IsFalse(entry.IsInfeasible);
        Assert.AreEqual(string.Empty, infeasible.SelectedAlternative);
        Assert.IsTrue(infeasible.IsInfeasible);
    }
}
