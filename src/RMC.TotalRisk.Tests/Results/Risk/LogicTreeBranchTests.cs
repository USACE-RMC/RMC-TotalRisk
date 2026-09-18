using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Tests.Results.Risk;

/// <summary>
/// Unit tests for <see cref="LogicTreeBranch"/> — the immutable branch record of one logic-tree
/// enumeration axis.
/// </summary>
[TestClass]
public class LogicTreeBranchTests
{
    /// <summary>
    /// Verifies the constructor stores the child index, declared weight, and forcing percentile
    /// unchanged.
    /// </summary>
    [TestMethod]
    public void Test_Construction_StoresValues()
    {
        // Arrange & Act
        var branch = new LogicTreeBranch(3, 0.4d, 0.5d);

        // Assert
        Assert.AreEqual(3, branch.ChildIndex);
        Assert.AreEqual(0.4d, branch.Weight);
        Assert.AreEqual(0.5d, branch.ForcedPercentile);
    }
}
