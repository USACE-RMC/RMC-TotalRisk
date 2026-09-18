using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Utilities;
using RMC.TotalRisk.Analyses.Support;
using RMC.TotalRisk.Core;

namespace RMC.TotalRisk.Tests.Analyses.Support;

/// <summary>
/// Unit tests for <see cref="AnalysisBase"/> — the estimated flag notification, the linked
/// cancellation lifecycle, and the event raisers, via a minimal stub analysis.
/// </summary>
[TestClass]
public class AnalysisBaseTests
{
    /// <summary>A minimal concrete analysis exposing the protected lifecycle for testing.</summary>
    private sealed class StubAnalysis : AnalysisBase
    {
        /// <summary>Exposes the protected estimated setter.</summary>
        public void SetEstimated(bool value) => IsEstimated = value;

        /// <summary>Exposes the protected cancellation reset.</summary>
        public CancellationToken Reset(CancellationToken external) => ResetCancellationToken(external);

        /// <summary>Exposes the protected starting raiser.</summary>
        public void RaiseStarting(CancelEventArgs e) => OnAnalysisStarting(e);

        /// <summary>Exposes the protected completed raiser.</summary>
        public void RaiseCompleted(AnalysisRunCompletedEventArgs e) => OnAnalysisCompleted(e);

        /// <inheritdoc/>
        public override Task RunAsync(SafeProgressReporter? progressReporter = null, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public override (bool IsValid, List<string> ValidationMessages) Validate()
        {
            return (true, new List<string>());
        }

        /// <inheritdoc/>
        public override IReadOnlyList<ValidationIssue> ValidateIssues() => Array.Empty<ValidationIssue>();
    }

    /// <summary>Verifies the estimated flag raises change notification once per transition.</summary>
    [TestMethod]
    public void Test_IsEstimated_Notifies()
    {
        // Arrange
        var analysis = new StubAnalysis();
        var raised = new List<string>();
        analysis.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        // Act
        analysis.SetEstimated(true);
        analysis.SetEstimated(true);
        analysis.SetEstimated(false);

        // Assert
        Assert.AreEqual(2, raised.Count);
        Assert.IsTrue(analysis.Validate().IsValid);
    }

    /// <summary>
    /// Verifies the linked cancellation lifecycle: the run token honors both the external token
    /// and <see cref="AnalysisBase.CancelAnalysis"/>, and canceling before any run is a no-op.
    /// </summary>
    [TestMethod]
    public void Test_Cancellation_LinkedLifecycle()
    {
        // Arrange
        var analysis = new StubAnalysis();
        analysis.CancelAnalysis(); // no source yet — must not throw.

        // Act / Assert — CancelAnalysis cancels the run token.
        var runToken = analysis.Reset(CancellationToken.None);
        Assert.IsFalse(runToken.IsCancellationRequested);
        analysis.CancelAnalysis();
        Assert.IsTrue(runToken.IsCancellationRequested);

        // The external token flows into a fresh run token.
        using var external = new CancellationTokenSource();
        var linked = analysis.Reset(external.Token);
        Assert.IsFalse(linked.IsCancellationRequested);
        external.Cancel();
        Assert.IsTrue(linked.IsCancellationRequested);
    }

    /// <summary>Verifies the lifecycle events raise with the supplied arguments.</summary>
    [TestMethod]
    public void Test_Events_Raise()
    {
        // Arrange
        var analysis = new StubAnalysis();
        CancelEventArgs? starting = null;
        AnalysisRunCompletedEventArgs? completed = null;
        analysis.AnalysisStarting += (_, e) => starting = e;
        analysis.AnalysisCompleted += (_, e) => completed = e;

        // Act
        analysis.RaiseStarting(new CancelEventArgs());
        analysis.RaiseCompleted(new AnalysisRunCompletedEventArgs(wasCanceled: false, succeeded: true, error: null));

        // Assert
        Assert.IsNotNull(starting);
        Assert.IsNotNull(completed);
        Assert.IsTrue(completed!.Succeeded);
    }
}
