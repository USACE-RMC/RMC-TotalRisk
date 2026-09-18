using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Tests.Results.Risk;

/// <summary>
/// Unit tests for <see cref="SystemRiskResults"/> — the full-realization summary capture with
/// its component tree and integration diagnostics.
/// </summary>
[TestClass]
public class SystemRiskResultsTests
{
    /// <summary>Verifies the capture summarizes the system streams, components, and diagnostics.</summary>
    [TestMethod]
    public void Test_Capture_TreeAndDiagnostics()
    {
        // Arrange
        var realization = new SystemRealization(new List<ComponentRealization> { new ComponentRealization(failureModes: 1) })
        {
            FunctionEvaluations = 777d,
            StandardError = 0.001d,
            ChiSquared = 1.1d,
        };
        realization.Curves.Fail.CreateCurve(new List<(double Mass, double Consequence)> { (0.02d, 25d), (0.03d, 5d) }, 200);

        // Act
        var results = new SystemRiskResults(realization);

        // Assert
        Assert.AreEqual(realization.Curves.Fail.TotalProbability, results.Fail.TotalProbability, 0d);
        Assert.AreEqual(1, results.ComponentResults.Count);
        Assert.AreEqual(1, results.ComponentResults[0].FailureModeResults.Count);
        Assert.AreEqual(777d, results.FunctionEvaluations, 0d);
        Assert.AreEqual(0.001d, results.StandardError, 0d);
        Assert.AreEqual(1.1d, results.ChiSquared, 0d);
        Assert.ThrowsException<ArgumentNullException>(() => new SystemRiskResults(null!));
    }

    /// <summary>
    /// Verifies the multi-consequence capture: additional types summarize at every
    /// scope with the declared labels echoed from the realization, and the summary round-trips
    /// through the ensemble JSON.
    /// </summary>
    [TestMethod]
    public void Test_Capture_AdditionalConsequences_WithLabels()
    {
        // Arrange — one component, one mode, one additional type populated everywhere.
        var component = new ComponentRealization(failureModes: 1);
        component.EnsureAdditionalCurves(1);
        component.AdditionalCurves[0].Total.CreateCurve(new List<(double Mass, double Consequence)> { (0.4d, 1000d), (0.6d, 10d) }, 200);
        component.FailureModes[0].AdditionalCurves[0].Fail.CreateCurve(new List<(double Mass, double Consequence)> { (0.02d, 500d) , (0.01d, 50d) }, 200);
        var realization = new SystemRealization(new List<ComponentRealization> { component });
        realization.EnsureAdditionalCurves(1);
        realization.AdditionalCurves[0].Total.CreateCurve(new List<(double Mass, double Consequence)> { (0.4d, 1000d), (0.6d, 10d) }, 200);
        realization.ConsequenceLabels.AddRange(new[] { "Life Loss", "Damages" });
        realization.ConsequenceUnits.AddRange(new[] { "lives", "$" });

        // Act
        var results = new SystemRiskResults(realization);
        var ensemble = new EnsembleResults(1);
        ensemble[0] = results;
        var restored = EnsembleResults.FromJson(ensemble.ToJson());

        // Assert — capture.
        Assert.AreEqual(1, results.AdditionalConsequences.Count);
        Assert.AreEqual("Damages", results.AdditionalConsequences[0].SpecifiedConsequence);
        Assert.AreEqual("$", results.AdditionalConsequences[0].ConsequenceUnit);
        Assert.AreEqual(realization.AdditionalCurves[0].Total.Mean, results.AdditionalConsequences[0].Total.Mean, 0d);
        Assert.AreEqual(1, results.ComponentResults[0].AdditionalConsequences.Count);
        Assert.AreEqual(1, results.ComponentResults[0].FailureModeResults[0].AdditionalConsequences.Count);
        CollectionAssert.AreEqual(new[] { "Life Loss", "Damages" }, results.ConsequenceLabels);

        // Assert — JSON round-trip.
        var restoredSummary = restored[0]!;
        Assert.AreEqual(1, restoredSummary.AdditionalConsequences.Count);
        Assert.AreEqual("Damages", restoredSummary.AdditionalConsequences[0].SpecifiedConsequence);
        Assert.AreEqual(
            System.BitConverter.DoubleToInt64Bits(results.AdditionalConsequences[0].Total.Mean),
            System.BitConverter.DoubleToInt64Bits(restoredSummary.AdditionalConsequences[0].Total.Mean));
        CollectionAssert.AreEqual(results.ConsequenceUnits, restoredSummary.ConsequenceUnits);
    }
}
