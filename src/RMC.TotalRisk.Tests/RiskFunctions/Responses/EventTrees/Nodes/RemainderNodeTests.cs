using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees.Nodes;

namespace RMC.TotalRisk.Tests.RiskFunctions.Responses.EventTrees.Nodes;

/// <summary>Tests residual event-tree branch defaults.</summary>
[TestClass]
public class RemainderNodeTests
{
    /// <summary>Verifies a remainder defaults to a non-failure terminal.</summary>
    [TestMethod]
    public void Test_Defaults_AreResidualNonFailure()
    {
        var node = new RemainderNode();

        Assert.AreEqual("Remainder", node.Name);
        Assert.IsFalse(node.IsFailure);
        Assert.IsTrue(node.IsTerminal);
    }
}
