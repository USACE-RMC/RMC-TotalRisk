using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results.CostBenefit;

namespace RMC.TotalRisk.Tests.Results.CostBenefit;

/// <summary>
/// Tests the frontier results block: guards, the parallel-axis rules, and the echoes.
/// </summary>
[TestClass]
public class ParetoFrontierResultsTests
{
    /// <summary>Builds a two-alternative one-objective block.</summary>
    /// <returns>The block.</returns>
    private static ParetoFrontierResults Build()
    {
        return new ParetoFrontierResults(new[] { "Cost" }, new[] { ObjectiveDirection.Minimize },
            new[] { "A", "B" },
            new IReadOnlyList<double>[] { new[] { 1d }, new[] { 2d } },
            new[] { true, false }, new[] { false, false }, new[] { false, true },
            new[]
            {
                new FrontierProjection("P", "x", ObjectiveDirection.Minimize, "y",
                    ObjectiveDirection.Maximize, new[] { "A", "B" }, new[] { 1d, 2d },
                    new[] { 3d, 4d }, new[] { true, true }, new[] { false, false }),
            },
            new[] { new IncrementalEntry("A", "B", 1d, 1d, 1d, 1d) });
    }

    /// <summary>Verifies the guards: null lists, misaligned axes, and ragged value rows.</summary>
    [TestMethod]
    public void Test_Ctor_Guards()
    {
        // Act / Assert
        Assert.ThrowsException<ArgumentNullException>(() => new ParetoFrontierResults(null!,
            new[] { ObjectiveDirection.Minimize }, new[] { "A" },
            new IReadOnlyList<double>[] { new[] { 1d } }, new[] { true }, new[] { false },
            new[] { false }, Array.Empty<FrontierProjection>(), Array.Empty<IncrementalEntry>()));
        Assert.ThrowsException<ArgumentException>(() => new ParetoFrontierResults(new[] { "Cost" },
            new[] { ObjectiveDirection.Minimize, ObjectiveDirection.Maximize }, new[] { "A" },
            new IReadOnlyList<double>[] { new[] { 1d } }, new[] { true }, new[] { false },
            new[] { false }, Array.Empty<FrontierProjection>(), Array.Empty<IncrementalEntry>()));
        Assert.ThrowsException<ArgumentException>(() => new ParetoFrontierResults(new[] { "Cost" },
            new[] { ObjectiveDirection.Minimize }, new[] { "A" },
            new IReadOnlyList<double>[] { new[] { 1d, 2d } }, new[] { true }, new[] { false },
            new[] { false }, Array.Empty<FrontierProjection>(), Array.Empty<IncrementalEntry>()));
    }

    /// <summary>Verifies the echoes.</summary>
    [TestMethod]
    public void Test_Ctor_PropertiesEchoed()
    {
        // Act
        ParetoFrontierResults results = Build();

        // Assert
        Assert.AreEqual("Cost", results.ObjectiveNames[0]);
        Assert.AreEqual(ObjectiveDirection.Minimize, results.Directions[0]);
        Assert.AreEqual(2, results.AlternativeNames.Count);
        Assert.AreEqual(2d, results.Values[1][0]);
        Assert.IsTrue(results.IsNonDominated[0]);
        Assert.IsFalse(results.IsNonDominated[1]);
        Assert.IsTrue(results.IsExcludedFromRecommendation[1]);
        Assert.AreEqual(1, results.Projections.Count);
        Assert.AreEqual(1, results.IncrementalAnalysis.Count);
    }
}
