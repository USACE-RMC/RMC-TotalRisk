using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees.Nodes;

namespace RMC.TotalRisk.Tests.RiskFunctions.Responses.EventTrees.Nodes;

/// <summary>Tests initiating-node root defaults.</summary>
[TestClass]
public class InitiatingNodeTests
{
    /// <summary>Verifies the initiating node defaults to structural non-failure metadata.</summary>
    [TestMethod]
    public void Test_Defaults_AreStructuralRootDefaults()
    {
        var node = new InitiatingNode();

        Assert.AreEqual("Initiating Event", node.Name);
        Assert.IsFalse(node.IsFailure);
        Assert.IsNull(node.Parent);
    }
}
