using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Results.LifeCycle;

namespace RMC.TotalRisk.Tests.Results.LifeCycle;

/// <summary>
/// Tests the life-cycle trajectory root: guards, the per-type alignment rule, echoes, and the
/// defensive snapshots.
/// </summary>
[TestClass]
public class LifeCycleRiskResultsTests
{
    /// <summary>Builds a one-epoch trajectory with the given per-type list length.</summary>
    /// <param name="typeCount">The declared consequence-type count.</param>
    /// <param name="unitCount">The unit-list length (defaults to the type count).</param>
    /// <returns>The results.</returns>
    private static LifeCycleRiskResults Build(int typeCount, int? unitCount = null)
    {
        var labels = new string[typeCount];
        var units = new string[unitCount ?? typeCount];
        for (int i = 0; i < labels.Length; i++) labels[i] = $"Type {i}";
        for (int i = 0; i < units.Length; i++) units[i] = "$";
        var perType = new double[typeCount];
        var epoch = new LifeCycleEpochRisk(0, 10, 0d, 0.4d,
            new LifeCycleEpochEntry("System", 0.05d, perType),
            Array.Empty<LifeCycleEpochEntry>(), Array.Empty<string>());
        return new LifeCycleRiskResults(10, 0.035d, labels, units, new[] { epoch },
            0.4d, perType, perType, perType, perType, perType, new[] { "Year 0: fixed" });
    }

    /// <summary>Verifies null lists are refused.</summary>
    [TestMethod]
    public void Test_Ctor_NullArguments_Throw()
    {
        // Arrange
        var perType = new[] { 0d };
        var epochs = Array.Empty<LifeCycleEpochRisk>();

        // Act / Assert — one representative per list argument.
        Assert.ThrowsException<ArgumentNullException>(() => new LifeCycleRiskResults(
            10, 0d, null!, new[] { "$" }, epochs, 0d, perType, perType, perType, perType, perType,
            Array.Empty<string>()));
        Assert.ThrowsException<ArgumentNullException>(() => new LifeCycleRiskResults(
            10, 0d, new[] { "Damages" }, new[] { "$" }, null!, 0d, perType, perType, perType,
            perType, perType, Array.Empty<string>()));
        Assert.ThrowsException<ArgumentNullException>(() => new LifeCycleRiskResults(
            10, 0d, new[] { "Damages" }, new[] { "$" }, epochs, 0d, perType, perType, perType,
            perType, perType, null!));
    }

    /// <summary>Verifies label/unit and per-type list misalignments are refused.</summary>
    [TestMethod]
    public void Test_Ctor_Misalignment_Throws()
    {
        // Act / Assert — unit list shorter than the labels.
        Assert.ThrowsException<ArgumentException>(() => Build(2, unitCount: 1));

        // A per-type aggregate list of the wrong length.
        var epoch = new LifeCycleEpochRisk(0, 10, 0d, 0.4d,
            new LifeCycleEpochEntry("System", 0.05d, new[] { 0d }),
            Array.Empty<LifeCycleEpochEntry>(), Array.Empty<string>());
        Assert.ThrowsException<ArgumentException>(() => new LifeCycleRiskResults(
            10, 0d, new[] { "Damages" }, new[] { "$" }, new[] { epoch }, 0.4d,
            new[] { 0d, 0d }, new[] { 0d }, new[] { 0d }, new[] { 0d }, new[] { 0d },
            Array.Empty<string>()));
    }

    /// <summary>Verifies the properties echo the arguments.</summary>
    [TestMethod]
    public void Test_Ctor_PropertiesEchoed()
    {
        // Act
        var results = Build(2);

        // Assert
        Assert.AreEqual(10, results.PeriodYears);
        Assert.AreEqual(0.035d, results.DiscountRate);
        Assert.AreEqual(2, results.ConsequenceLabels.Count);
        Assert.AreEqual(2, results.ConsequenceUnits.Count);
        Assert.AreEqual(1, results.Epochs.Count);
        Assert.AreEqual(0.4d, results.FailureProbabilityByHorizon);
        Assert.AreEqual(2, results.CumulativeExpectedConsequences.Count);
        Assert.AreEqual(2, results.PresentValueOfExpectedConsequences.Count);
        Assert.AreEqual(2, results.EquivalentAnnualConsequences.Count);
        Assert.AreEqual(2, results.AbsorbingCumulativeExpectedConsequences.Count);
        Assert.AreEqual(2, results.AbsorbingPresentValueOfExpectedConsequences.Count);
        Assert.AreEqual(1, results.AppliedInterventions.Count);
    }

    /// <summary>Verifies the stream aggregate lists echo when supplied and default empty when omitted.</summary>
    [TestMethod]
    public void Test_Ctor_StreamAggregates_EchoedAndEmptyByDefault()
    {
        // Arrange
        var perType = new[] { 10d };
        var epoch = new LifeCycleEpochRisk(0, 10, 0d, 0.4d,
            new LifeCycleEpochEntry("System", 0.05d, perType),
            Array.Empty<LifeCycleEpochEntry>(), Array.Empty<string>());

        // Act — the pre-stream form, then the stream-bearing form with distinct values per slot.
        var bare = Build(1);
        var full = new LifeCycleRiskResults(10, 0d, new[] { "Damages" }, new[] { "$" },
            new[] { epoch }, 0.4d, perType, perType, perType, perType, perType,
            Array.Empty<string>(),
            new[] { 1d }, new[] { 2d }, new[] { 3d }, new[] { 4d }, new[] { 5d },
            new[] { 6d }, new[] { 7d }, new[] { 8d }, new[] { 9d }, new[] { 11d });

        // Assert — omitted stream aggregates are empty, not null.
        Assert.AreEqual(0, bare.ExcessCumulativeExpectedConsequences.Count);
        Assert.AreEqual(0, bare.ExcessPresentValueOfExpectedConsequences.Count);
        Assert.AreEqual(0, bare.ExcessEquivalentAnnualConsequences.Count);
        Assert.AreEqual(0, bare.AbsorbingExcessCumulativeExpectedConsequences.Count);
        Assert.AreEqual(0, bare.AbsorbingExcessPresentValueOfExpectedConsequences.Count);
        Assert.AreEqual(0, bare.FailCumulativeExpectedConsequences.Count);
        Assert.AreEqual(0, bare.FailPresentValueOfExpectedConsequences.Count);
        Assert.AreEqual(0, bare.FailEquivalentAnnualConsequences.Count);
        Assert.AreEqual(0, bare.AbsorbingFailCumulativeExpectedConsequences.Count);
        Assert.AreEqual(0, bare.AbsorbingFailPresentValueOfExpectedConsequences.Count);

        // Each supplied slot echoes its own list.
        Assert.AreEqual(1d, full.ExcessCumulativeExpectedConsequences[0]);
        Assert.AreEqual(2d, full.ExcessPresentValueOfExpectedConsequences[0]);
        Assert.AreEqual(3d, full.ExcessEquivalentAnnualConsequences[0]);
        Assert.AreEqual(4d, full.AbsorbingExcessCumulativeExpectedConsequences[0]);
        Assert.AreEqual(5d, full.AbsorbingExcessPresentValueOfExpectedConsequences[0]);
        Assert.AreEqual(6d, full.FailCumulativeExpectedConsequences[0]);
        Assert.AreEqual(7d, full.FailPresentValueOfExpectedConsequences[0]);
        Assert.AreEqual(8d, full.FailEquivalentAnnualConsequences[0]);
        Assert.AreEqual(9d, full.AbsorbingFailCumulativeExpectedConsequences[0]);
        Assert.AreEqual(11d, full.AbsorbingFailPresentValueOfExpectedConsequences[0]);
    }

    /// <summary>Verifies a supplied stream aggregate list must align with the declared types.</summary>
    [TestMethod]
    public void Test_Ctor_StreamAggregateMisalignment_Throws()
    {
        // Arrange
        var perType = new[] { 10d };
        var epoch = new LifeCycleEpochRisk(0, 10, 0d, 0.4d,
            new LifeCycleEpochEntry("System", 0.05d, perType),
            Array.Empty<LifeCycleEpochEntry>(), Array.Empty<string>());

        // Act / Assert — one representative misaligned stream slot.
        Assert.ThrowsException<ArgumentException>(() => new LifeCycleRiskResults(
            10, 0d, new[] { "Damages" }, new[] { "$" }, new[] { epoch }, 0.4d,
            perType, perType, perType, perType, perType, Array.Empty<string>(),
            excessCumulativeExpectedConsequences: new[] { 1d, 2d }));
    }

    /// <summary>Verifies the stored lists are defensive snapshots.</summary>
    [TestMethod]
    public void Test_Ctor_Snapshots_InputMutationInert()
    {
        // Arrange
        var labels = new List<string> { "Damages" };
        var units = new List<string> { "$" };
        var perType = new double[] { 0d };
        var epoch = new LifeCycleEpochRisk(0, 10, 0d, 0.4d,
            new LifeCycleEpochEntry("System", 0.05d, perType),
            Array.Empty<LifeCycleEpochEntry>(), Array.Empty<string>());
        var interventions = new List<string> { "Year 0: fixed" };
        var results = new LifeCycleRiskResults(10, 0d, labels, units, new[] { epoch },
            0.4d, perType, perType, perType, perType, perType, interventions);

        // Act
        labels.Add("Life Loss");
        interventions.Clear();

        // Assert
        Assert.AreEqual(1, results.ConsequenceLabels.Count);
        Assert.AreEqual(1, results.AppliedInterventions.Count);
    }
}
