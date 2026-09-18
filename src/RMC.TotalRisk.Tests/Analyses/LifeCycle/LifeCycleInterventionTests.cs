using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Analyses.LifeCycle;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.RiskFunctions.Hazards;

namespace RMC.TotalRisk.Tests.Analyses.LifeCycle;

/// <summary>
/// Tests the life-cycle intervention record: the year and action guards, the per-entry
/// duplicate refusals, and the defensive snapshots.
/// </summary>
[TestClass]
public class LifeCycleInterventionTests
{
    /// <summary>Builds a valid house-event state.</summary>
    /// <returns>The state.</returns>
    private static HouseEventState House()
    {
        return new HouseEventState(Guid.NewGuid(), Guid.NewGuid(), true);
    }

    /// <summary>Builds a valid replacement action.</summary>
    /// <returns>The action.</returns>
    private static HazardReplacement Replacement()
    {
        return new HazardReplacement(Guid.NewGuid(), new TabularHazard());
    }

    /// <summary>Verifies a negative year is refused.</summary>
    [TestMethod]
    public void Test_Ctor_NegativeYear_Throws()
    {
        // Act / Assert
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => new LifeCycleIntervention(-1, new[] { House() }));
    }

    /// <summary>Verifies an actionless entry is refused in every empty shape.</summary>
    [TestMethod]
    public void Test_Ctor_NoActions_Throws()
    {
        // Act / Assert
        Assert.ThrowsException<ArgumentException>(() => new LifeCycleIntervention(5));
        Assert.ThrowsException<ArgumentException>(
            () => new LifeCycleIntervention(5, Array.Empty<HouseEventState>(), Array.Empty<HazardReplacement>()));
    }

    /// <summary>Verifies null list entries are refused.</summary>
    [TestMethod]
    public void Test_Ctor_NullListItem_Throws()
    {
        // Act / Assert
        Assert.ThrowsException<ArgumentException>(
            () => new LifeCycleIntervention(5, new HouseEventState[] { null! }));
        Assert.ThrowsException<ArgumentException>(
            () => new LifeCycleIntervention(5, null, new HazardReplacement[] { null! }));
    }

    /// <summary>Verifies a house event addressed twice in one entry is refused.</summary>
    [TestMethod]
    public void Test_Ctor_DuplicateHouseEventTarget_Throws()
    {
        // Arrange
        var functionId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();

        // Act / Assert
        Assert.ThrowsException<ArgumentException>(() => new LifeCycleIntervention(5, new[]
        {
            new HouseEventState(functionId, nodeId, true),
            new HouseEventState(functionId, nodeId, false),
        }));
    }

    /// <summary>Verifies a replacement target addressed twice in one entry is refused.</summary>
    [TestMethod]
    public void Test_Ctor_DuplicateReplacementTarget_Throws()
    {
        // Arrange
        var target = Guid.NewGuid();

        // Act / Assert
        Assert.ThrowsException<ArgumentException>(() => new LifeCycleIntervention(5, null, new[]
        {
            new HazardReplacement(target, new TabularHazard()),
            new HazardReplacement(target, new TabularHazard()),
        }));
    }

    /// <summary>Verifies single-action shapes are legal, incl. year zero.</summary>
    [TestMethod]
    public void Test_Ctor_SingleActionShapes_Valid()
    {
        // Act
        var houseOnly = new LifeCycleIntervention(0, new[] { House() });
        var replacementOnly = new LifeCycleIntervention(20, null, new[] { Replacement() });

        // Assert
        Assert.AreEqual(0, houseOnly.Year);
        Assert.AreEqual(1, houseOnly.HouseEvents.Count);
        Assert.AreEqual(0, houseOnly.HazardReplacements.Count);
        Assert.AreEqual(20, replacementOnly.Year);
        Assert.AreEqual(0, replacementOnly.HouseEvents.Count);
        Assert.AreEqual(1, replacementOnly.HazardReplacements.Count);
    }

    /// <summary>Verifies the stored lists are defensive snapshots of the inputs.</summary>
    [TestMethod]
    public void Test_Ctor_Snapshots_InputMutationInert()
    {
        // Arrange
        var houseEvents = new List<HouseEventState> { House() };
        var replacements = new List<HazardReplacement> { Replacement() };
        var intervention = new LifeCycleIntervention(5, houseEvents, replacements);

        // Act — mutate the inputs after construction.
        houseEvents.Add(House());
        replacements.Clear();

        // Assert
        Assert.AreEqual(1, intervention.HouseEvents.Count);
        Assert.AreEqual(1, intervention.HazardReplacements.Count);
    }

    /// <summary>Verifies the XML round trip carries both action kinds.</summary>
    [TestMethod]
    public void Test_Serialization_RoundTrip()
    {
        // Arrange
        var house = House();
        var replacement = Replacement();
        var intervention = new LifeCycleIntervention(15, new[] { house }, new[] { replacement });

        // Act
        var restored = new LifeCycleIntervention(intervention.ToXElement());

        // Assert
        Assert.AreEqual(15, restored.Year);
        Assert.AreEqual(1, restored.HouseEvents.Count);
        Assert.AreEqual(house.FunctionId, restored.HouseEvents[0].FunctionId);
        Assert.AreEqual(house.NodeId, restored.HouseEvents[0].NodeId);
        Assert.AreEqual(house.State, restored.HouseEvents[0].State);
        Assert.AreEqual(1, restored.HazardReplacements.Count);
        Assert.AreEqual(replacement.TargetFunctionId, restored.HazardReplacements[0].TargetFunctionId);
        Assert.ThrowsException<ArgumentNullException>(() => new LifeCycleIntervention(null!));
    }
}
