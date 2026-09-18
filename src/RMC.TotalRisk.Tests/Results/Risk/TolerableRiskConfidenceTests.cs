using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Tests.Results.Risk;

/// <summary>
/// Unit tests for <see cref="TolerableRiskConfidence"/> — the entry defaults and the
/// append-only JSON round trip on the ensemble summary.
/// </summary>
[TestClass]
public class TolerableRiskConfidenceTests
{
    /// <summary>
    /// Verifies the entry defaults and property round trip.
    /// </summary>
    [TestMethod]
    public void Test_Construction_Defaults()
    {
        // Act
        var entry = new TolerableRiskConfidence();

        // Assert — safe defaults, then settable.
        Assert.AreEqual(string.Empty, entry.Measure);
        Assert.AreEqual(string.Empty, entry.RiskType);
        Assert.AreEqual(0, entry.ConsequenceTypeIndex);
        Assert.AreEqual(0d, entry.Threshold, 0d);
        Assert.AreEqual(0d, entry.ExceedanceProbability, 0d);

        entry.Measure = "Mean";
        entry.RiskType = "Excess";
        entry.ConsequenceTypeIndex = 1;
        entry.Threshold = 1e-3;
        entry.ExceedanceProbability = 0.25d;
        Assert.AreEqual("Mean", entry.Measure);
        Assert.AreEqual(0.25d, entry.ExceedanceProbability, 0d);
    }

    /// <summary>
    /// Verifies the append-only serialization on the summary: entries round-trip bit-faithfully
    /// through the ensemble JSON, an entry-free summary omits the block entirely, and an older
    /// payload without the member loads forward as null.
    /// </summary>
    [TestMethod]
    public void Test_Serialization_AppendOnlyOnEnsembleSummary()
    {
        // Arrange — a two-realization ensemble whose summary carries one confidence entry.
        var ensemble = new EnsembleResults(2);
        ensemble[0] = new SystemRiskResults();
        ensemble[1] = new SystemRiskResults();
        ensemble.Summary = ensemble.ComputeSummary(0.9d);
        ensemble.Summary!.TolerableRiskConfidence = new System.Collections.Generic.List<TolerableRiskConfidence>
        {
            new TolerableRiskConfidence
            {
                Measure = "Mean",
                RiskType = "Excess",
                ConsequenceTypeIndex = 0,
                Threshold = 1e-3,
                ExceedanceProbability = 0.125d,
            },
        };

        // Act
        var restored = EnsembleResults.FromJson(ensemble.ToJson());

        // Assert — the block round-trips bit-faithfully.
        var entry = restored.Summary!.TolerableRiskConfidence![0];
        Assert.AreEqual("Mean", entry.Measure);
        Assert.AreEqual("Excess", entry.RiskType);
        Assert.AreEqual(0, entry.ConsequenceTypeIndex);
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(1e-3), BitConverter.DoubleToInt64Bits(entry.Threshold));
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(0.125d), BitConverter.DoubleToInt64Bits(entry.ExceedanceProbability));

        // A block-free summary omits the member entirely, and loads forward as null.
        var bare = new EnsembleResults(2);
        bare[0] = new SystemRiskResults();
        bare[1] = new SystemRiskResults();
        bare.Summary = bare.ComputeSummary(0.9d);
        string bareJson = bare.ToJson();
        StringAssert.DoesNotMatch(bareJson, new System.Text.RegularExpressions.Regex("TolerableRiskConfidence"));
        Assert.IsNull(EnsembleResults.FromJson(bareJson).Summary!.TolerableRiskConfidence);
    }
}
