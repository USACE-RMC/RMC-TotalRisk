using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Tests.Results.Risk;

/// <summary>Unit tests for <see cref="SensitivityResults"/> snapshots and stable ranking.</summary>
/// <remarks>
/// <para><b>Authors:</b> Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil</para>
/// </remarks>
[TestClass]
public class SensitivityResultsTests
{
    /// <summary>Verifies defensive snapshots, descending magnitude, and stable ties.</summary>
    [TestMethod]
    public void Test_Entries_AreSnapshotAndRankingIsStable()
    {
        var entries = new List<SensitivityEntry>
        {
            new SensitivityEntry("first tie", 0.5d),
            new SensitivityEntry("largest", -0.9d),
            new SensitivityEntry("second tie", -0.5d),
        };
        var results = new SensitivityResults(null, RiskType.Total, SensitivityMeasure.SpearmanCorrelation, 50, entries);
        entries.Clear();

        Assert.AreEqual(string.Empty, results.OutputLabel);
        Assert.AreEqual(3, results.Entries.Count);
        Assert.AreEqual(50, results.Realizations);
        Assert.ThrowsException<NotSupportedException>(() =>
            ((IList<SensitivityEntry>)results.Entries).Add(new SensitivityEntry("x", 0d)));

        var ranked = results.RankedByMagnitude();
        Assert.AreEqual("largest", ranked[0].Label);
        Assert.AreEqual("first tie", ranked[1].Label);
        Assert.AreEqual("second tie", ranked[2].Label);
        Assert.ThrowsException<NotSupportedException>(() =>
            ((IList<SensitivityEntry>)ranked).Clear());
        Assert.ThrowsException<ArgumentNullException>(() =>
            new SensitivityResults("x", RiskType.Total, SensitivityMeasure.SpearmanCorrelation, 1, null!));
    }
}
