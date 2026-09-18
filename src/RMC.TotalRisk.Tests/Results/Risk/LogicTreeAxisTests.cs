using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Tests.Results.Risk;

/// <summary>
/// Unit tests for <see cref="LogicTreeAxis"/> — one shared-variable or unbound-composite axis of
/// a logic-tree enumeration.
/// </summary>
[TestClass]
public class LogicTreeAxisTests
{
    /// <summary>
    /// Verifies a shared-variable axis stores its label, kind, null function id, and branch list.
    /// </summary>
    [TestMethod]
    public void Test_Construction_SharedVariable_StoresValues()
    {
        // Arrange
        var branches = new List<LogicTreeBranch> { new LogicTreeBranch(0, 0.3d, 0.15d), new LogicTreeBranch(1, 0.7d, 0.65d) };

        // Act
        var axis = new LogicTreeAxis("SOK-Frag", true, null, branches);

        // Assert
        Assert.AreEqual("SOK-Frag", axis.Name);
        Assert.IsTrue(axis.IsSharedVariable);
        Assert.IsNull(axis.FunctionId);
        Assert.AreEqual(2, axis.Branches.Count);
        Assert.AreEqual(1, axis.Branches[1].ChildIndex);
    }

    /// <summary>
    /// Verifies an unbound-composite axis carries the forcing function id.
    /// </summary>
    [TestMethod]
    public void Test_Construction_UnboundComposite_CarriesFunctionId()
    {
        // Arrange
        var id = Guid.NewGuid();
        var branches = new List<LogicTreeBranch> { new LogicTreeBranch(0, 1d, 0.5d) };

        // Act
        var axis = new LogicTreeAxis("Rating Tree", false, id, branches);

        // Assert
        Assert.IsFalse(axis.IsSharedVariable);
        Assert.AreEqual(id, axis.FunctionId);
    }

    /// <summary>
    /// Verifies the constructor guards: a null branch list throws, and an empty one throws.
    /// </summary>
    [TestMethod]
    public void Test_Construction_Guards_Throw()
    {
        // Act & Assert
        Assert.ThrowsException<ArgumentNullException>(() => new LogicTreeAxis("Axis", true, null, null!));
        Assert.ThrowsException<ArgumentException>(() => new LogicTreeAxis("Axis", true, null, new List<LogicTreeBranch>()));
    }
}
