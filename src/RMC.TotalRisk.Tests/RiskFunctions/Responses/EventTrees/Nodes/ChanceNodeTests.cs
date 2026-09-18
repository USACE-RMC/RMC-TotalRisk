using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees.Nodes;

namespace RMC.TotalRisk.Tests.RiskFunctions.Responses.EventTrees.Nodes;

/// <summary>Tests explicit event-tree chance branches.</summary>
[TestClass]
public class ChanceNodeTests
{
    /// <summary>Verifies failure default, source replacement, and null rejection.</summary>
    [TestMethod]
    public void Test_ProbabilitySource_IsControlled()
    {
        var initial = new ProbabilitySource(0.2d);
        var replacement = new ProbabilitySource(0.3d);
        var node = new ChanceNode("Chance", initial);

        Assert.IsTrue(node.IsFailure);
        node.ProbabilitySource = replacement;
        Assert.AreSame(replacement, node.ProbabilitySource);
        Assert.ThrowsException<ArgumentNullException>(() => node.ProbabilitySource = null!);
    }
}
