using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Analyses.LifeCycle;
using RMC.TotalRisk.Analyses.Risk;

namespace RMC.TotalRisk.Tests.Analyses.LifeCycle;

/// <summary>
/// Tests the life-cycle definition record: the horizon and discount-rate guards, the year
/// bounds, the one-entry-per-year rule, and the defensive snapshots.
/// </summary>
[TestClass]
public class LifeCycleDefinitionTests
{
    /// <summary>Builds a house-only intervention at the given year.</summary>
    /// <param name="year">The intervention year.</param>
    /// <returns>The intervention.</returns>
    private static LifeCycleIntervention At(int year)
    {
        return new LifeCycleIntervention(year,
            new[] { new HouseEventState(Guid.NewGuid(), Guid.NewGuid(), true) });
    }

    /// <summary>Verifies a horizon below one year is refused.</summary>
    [TestMethod]
    public void Test_Ctor_PeriodBelowOne_Throws()
    {
        // Act / Assert
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new LifeCycleDefinition(0));
    }

    /// <summary>Verifies NaN, infinite, and negative discount rates are refused.</summary>
    [TestMethod]
    public void Test_Ctor_InvalidDiscountRate_Throws()
    {
        // Act / Assert
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new LifeCycleDefinition(50, double.NaN));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new LifeCycleDefinition(50, double.PositiveInfinity));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new LifeCycleDefinition(50, -0.01d));
    }

    /// <summary>Verifies evaluation years outside [0, periodYears − 1] are refused.</summary>
    [TestMethod]
    public void Test_Ctor_EvaluationYearOutOfRange_Throws()
    {
        // Act / Assert
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => new LifeCycleDefinition(50, 0d, new[] { -1 }));
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => new LifeCycleDefinition(50, 0d, new[] { 50 }));
    }

    /// <summary>Verifies an intervention at the horizon (a zero-span epoch) is refused.</summary>
    [TestMethod]
    public void Test_Ctor_InterventionYearAtPeriod_Throws()
    {
        // Act / Assert
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => new LifeCycleDefinition(50, 0d, null, new[] { At(50) }));
    }

    /// <summary>Verifies two interventions sharing a year are refused.</summary>
    [TestMethod]
    public void Test_Ctor_DuplicateInterventionYears_Throws()
    {
        // Act / Assert
        Assert.ThrowsException<ArgumentException>(
            () => new LifeCycleDefinition(50, 0d, null, new[] { At(10), At(10) }));
    }

    /// <summary>Verifies a null intervention entry is refused.</summary>
    [TestMethod]
    public void Test_Ctor_NullInterventionItem_Throws()
    {
        // Act / Assert
        Assert.ThrowsException<ArgumentException>(
            () => new LifeCycleDefinition(50, 0d, null, new LifeCycleIntervention[] { null! }));
    }

    /// <summary>Verifies duplicate evaluation years are legal (absorbed by the distinct union).</summary>
    [TestMethod]
    public void Test_Ctor_DuplicateEvaluationYears_Allowed()
    {
        // Act
        var definition = new LifeCycleDefinition(50, 0d, new[] { 10, 10, 30 });

        // Assert — stored as supplied; the query deduplicates.
        Assert.AreEqual(3, definition.EvaluationYears.Count);
    }

    /// <summary>Verifies null lists default to empty and scalars echo.</summary>
    [TestMethod]
    public void Test_Ctor_NullLists_EmptyDefaults()
    {
        // Act
        var definition = new LifeCycleDefinition(50, 0.035d);

        // Assert
        Assert.AreEqual(50, definition.PeriodYears);
        Assert.AreEqual(0.035d, definition.DiscountRate);
        Assert.AreEqual(0, definition.EvaluationYears.Count);
        Assert.AreEqual(0, definition.Interventions.Count);
    }

    /// <summary>Verifies epoch-realization retention defaults off and echoes when requested.</summary>
    [TestMethod]
    public void Test_Ctor_RetainEpochRealizations_DefaultFalse_Echoed()
    {
        // Act
        var bare = new LifeCycleDefinition(50);
        var retaining = new LifeCycleDefinition(50, 0d, null, null, retainEpochRealizations: true);

        // Assert
        Assert.IsFalse(bare.RetainEpochRealizations);
        Assert.IsTrue(retaining.RetainEpochRealizations);
    }

    /// <summary>Verifies a year-zero intervention is legal.</summary>
    [TestMethod]
    public void Test_Ctor_YearZeroIntervention_Valid()
    {
        // Act
        var definition = new LifeCycleDefinition(50, 0d, null, new[] { At(0) });

        // Assert
        Assert.AreEqual(0, definition.Interventions[0].Year);
    }

    /// <summary>Verifies the stored lists are defensive snapshots of the inputs.</summary>
    [TestMethod]
    public void Test_Ctor_Snapshots_InputMutationInert()
    {
        // Arrange
        var years = new List<int> { 10 };
        var interventions = new List<LifeCycleIntervention> { At(20) };
        var definition = new LifeCycleDefinition(50, 0d, years, interventions);

        // Act — mutate the inputs after construction.
        years.Add(30);
        interventions.Clear();

        // Assert
        Assert.AreEqual(1, definition.EvaluationYears.Count);
        Assert.AreEqual(1, definition.Interventions.Count);
    }
}
