using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Results.LifeCycle;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Tests.Results.LifeCycle;

/// <summary>
/// Tests the life-cycle epoch row: guards, echoes, the derived end year, and the defensive
/// snapshots.
/// </summary>
[TestClass]
public class LifeCycleEpochRiskTests
{
    /// <summary>Builds a scope row.</summary>
    /// <param name="name">The scope label.</param>
    /// <returns>The row.</returns>
    private static LifeCycleEpochEntry Entry(string name = "System")
    {
        return new LifeCycleEpochEntry(name, 0.05d, new[] { 500d });
    }

    /// <summary>Verifies null arguments are refused.</summary>
    [TestMethod]
    public void Test_Ctor_NullArguments_Throw()
    {
        // Act / Assert
        Assert.ThrowsException<ArgumentNullException>(() => new LifeCycleEpochRisk(
            0, 10, 0d, 0.4d, null!, Array.Empty<LifeCycleEpochEntry>(), Array.Empty<string>()));
        Assert.ThrowsException<ArgumentNullException>(() => new LifeCycleEpochRisk(
            0, 10, 0d, 0.4d, Entry(), null!, Array.Empty<string>()));
        Assert.ThrowsException<ArgumentNullException>(() => new LifeCycleEpochRisk(
            0, 10, 0d, 0.4d, Entry(), Array.Empty<LifeCycleEpochEntry>(), null!));
    }

    /// <summary>Verifies the properties echo and the end year derives from start and span.</summary>
    [TestMethod]
    public void Test_Ctor_PropertiesEchoed_EndYearDerived()
    {
        // Act
        var epoch = new LifeCycleEpochRisk(10, 15, 10d, 0.4d, Entry(),
            new[] { Entry("Dam") }, new[] { "'Dam' house event 'Gate' = True" });

        // Assert
        Assert.AreEqual(10, epoch.StartYear);
        Assert.AreEqual(15, epoch.SpanYears);
        Assert.AreEqual(25, epoch.EndYear);
        Assert.AreEqual(10d, epoch.EvaluationAge);
        Assert.AreEqual(0.4d, epoch.CumulativeFailureProbability);
        Assert.AreEqual("System", epoch.System.Name);
        Assert.AreEqual(1, epoch.Components.Count);
        Assert.AreEqual("Dam", epoch.Components[0].Name);
        Assert.AreEqual(1, epoch.AppliedActions.Count);
    }

    /// <summary>Verifies the stored lists are defensive snapshots.</summary>
    [TestMethod]
    public void Test_Ctor_Snapshots_InputMutationInert()
    {
        // Arrange
        var components = new List<LifeCycleEpochEntry> { Entry("Dam") };
        var actions = new List<string> { "action" };
        var epoch = new LifeCycleEpochRisk(0, 10, 0d, 0.4d, Entry(), components, actions);

        // Act
        components.Clear();
        actions.Add("late");

        // Assert
        Assert.AreEqual(1, epoch.Components.Count);
        Assert.AreEqual(1, epoch.AppliedActions.Count);
    }

    /// <summary>Verifies the retained realization is null by default and echoed when supplied.</summary>
    [TestMethod]
    public void Test_Ctor_Realization_NullByDefault_Echoed()
    {
        // Arrange
        var realization = new SystemRealization();

        // Act
        var bare = new LifeCycleEpochRisk(0, 10, 0d, 0.4d, Entry(),
            Array.Empty<LifeCycleEpochEntry>(), Array.Empty<string>());
        var retained = new LifeCycleEpochRisk(0, 10, 0d, 0.4d, Entry(),
            Array.Empty<LifeCycleEpochEntry>(), Array.Empty<string>(), realization);

        // Assert
        Assert.IsNull(bare.Realization);
        Assert.IsTrue(ReferenceEquals(realization, retained.Realization));
    }
}
