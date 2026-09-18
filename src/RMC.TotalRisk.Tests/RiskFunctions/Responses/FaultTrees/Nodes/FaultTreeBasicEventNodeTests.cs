using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Nodes;

namespace RMC.TotalRisk.Tests.RiskFunctions.Responses.FaultTrees.Nodes;

/// <summary>Tests the fault-tree basic-event node contract.</summary>
[TestClass]
public class FaultTreeBasicEventNodeTests
{
    /// <summary>Verifies source ownership, replacement notification, and null guards.</summary>
    [TestMethod]
    public void Test_ProbabilitySource_ReplaceNotifiesAndGuards()
    {
        // Arrange
        var original = new ProbabilitySource(0.25d);
        var node = new FaultTreeBasicEventNode("Pump fails", original);
        var raised = new List<string>();
        node.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        // Act
        var replacement = new ProbabilitySource(0.5d);
        node.ProbabilitySource = replacement;
        node.ProbabilitySource = replacement;

        // Assert
        Assert.AreSame(replacement, node.ProbabilitySource);
        CollectionAssert.AreEqual(new[] { "ProbabilitySource" }, raised);
        Assert.ThrowsException<ArgumentNullException>(() => node.ProbabilitySource = null!);
        Assert.ThrowsException<ArgumentNullException>(
            () => new FaultTreeBasicEventNode("Bad", null!));
    }
}
