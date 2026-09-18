using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Analyses.Support;

namespace RMC.TotalRisk.Tests.Analyses.Support;

/// <summary>
/// Unit tests for <see cref="AnalysisRunCompletedEventArgs"/> — the BestFit completion-argument
/// mapping.
/// </summary>
[TestClass]
public class AnalysisRunCompletedEventArgsTests
{
    /// <summary>Verifies the three outcome shapes map onto the base and the succeeded flag.</summary>
    [TestMethod]
    public void Test_Constructor_OutcomeMapping()
    {
        // Act
        var success = new AnalysisRunCompletedEventArgs(wasCanceled: false, succeeded: true, error: null);
        var canceled = new AnalysisRunCompletedEventArgs(wasCanceled: true, succeeded: false, error: null);
        var faulted = new AnalysisRunCompletedEventArgs(wasCanceled: false, succeeded: false, error: new InvalidOperationException("boom"));

        // Assert
        Assert.IsTrue(success.Succeeded);
        Assert.IsFalse(success.Cancelled);
        Assert.IsNull(success.Error);
        Assert.IsTrue(canceled.Cancelled);
        Assert.IsFalse(canceled.Succeeded);
        Assert.IsFalse(faulted.Succeeded);
        Assert.IsInstanceOfType(faulted.Error, typeof(InvalidOperationException));
    }
}
