using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Tests.Results.Risk;

/// <summary>
/// Unit tests for <see cref="ConsequenceResults"/> — the additional consequence-type summary:
/// empty construction, curve-set capture, and the label surface.
/// </summary>
[TestClass]
public class ConsequenceResultsTests
{
    /// <summary>Verifies the empty constructor yields empty stream summaries and blank labels.</summary>
    [TestMethod]
    public void Test_Constructor_EmptyStreams()
    {
        // Act
        var results = new ConsequenceResults();

        // Assert
        Assert.IsNotNull(results.Excess);
        Assert.IsNotNull(results.Background);
        Assert.IsNotNull(results.Total);
        Assert.IsNotNull(results.Fail);
        Assert.IsNotNull(results.NonFail);
        Assert.AreEqual(string.Empty, results.SpecifiedConsequence);
        Assert.AreEqual(string.Empty, results.ConsequenceUnit);
    }

    /// <summary>
    /// Verifies the capture constructor summarizes a curve set stream-for-stream, leaving
    /// unrecorded streams as empty defaults (the failure-mode scope records Excess and Fail
    /// only).
    /// </summary>
    [TestMethod]
    public void Test_Capture_SummarizesStreams()
    {
        // Arrange — a curve set with only the Fail stream populated.
        var curves = new Curves();
        curves.Fail.CreateCurve(new List<(double Mass, double Consequence)> { (0.02d, 25d), (0.03d, 5d) }, 200);

        // Act
        var results = new ConsequenceResults(curves)
        {
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
        };

        // Assert
        Assert.AreEqual(curves.Fail.TotalProbability, results.Fail.TotalProbability, 0d);
        Assert.AreEqual(curves.Fail.Mean, results.Fail.Mean, 0d);
        Assert.AreEqual(0d, results.Total.TotalProbability, 0d, "An unrecorded stream summarizes empty.");
        Assert.AreEqual("Damages", results.SpecifiedConsequence);
        Assert.AreEqual("$", results.ConsequenceUnit);
        Assert.ThrowsException<ArgumentNullException>(() => new ConsequenceResults(null!));
    }
}
