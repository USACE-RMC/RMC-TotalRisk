using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Numerics.Utilities;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Interfaces;

namespace RMC.TotalRisk.Analyses.Support
{
    /// <summary>
    /// The common base for analysis types: property change notification, the run lifecycle
    /// events, cancellation support, and the estimated flag — the BestFit <c>AnalysisBase</c>
    /// mirror.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Two deliberate divergences from the BestFit source: (1) the reprocess gate
    /// (<c>ReprocessIfEstimated</c>) is not ported — TotalRisk invalidates results when options
    /// change rather than reprocessing from retained chains, so there is no background reprocess
    /// to serialize against; (2) <see cref="ResetCancellationToken(CancellationToken)"/> links
    /// the fresh run source with a caller-supplied external token, so headless hosts cancel
    /// through their own token while <see cref="CancelAnalysis"/> keeps working
    /// (docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §7.3).
    /// </para>
    /// </remarks>
    public abstract class AnalysisBase : IAnalysis
    {
        /// <summary>
        /// Backing field for <see cref="IsEstimated"/>.
        /// </summary>
        protected bool _isEstimated;

        /// <summary>
        /// The cancellation source for the current run, linked with any external token.
        /// </summary>
        protected CancellationTokenSource? _cancellationTokenSource;

        /// <summary>
        /// Atomic execution-slot state: zero when idle, one while a run owns the analysis.
        /// </summary>
        private int _runState;

        /// <inheritdoc/>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <inheritdoc/>
        public event EventHandler<CancelEventArgs>? AnalysisStarting;

        /// <inheritdoc/>
        public event EventHandler<AnalysisRunCompletedEventArgs>? AnalysisCompleted;

        /// <inheritdoc/>
        public bool IsEstimated
        {
            get { return _isEstimated; }
            protected set
            {
                if (_isEstimated != value)
                {
                    _isEstimated = value;
                    RaisePropertyChange(nameof(IsEstimated));
                }
            }
        }

        /// <summary>
        /// Raises the <see cref="PropertyChanged"/> event.
        /// </summary>
        /// <param name="propertyName">The name of the property that changed.</param>
        protected virtual void RaisePropertyChange(string? propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        /// <inheritdoc/>
        public bool IsRunning => Volatile.Read(ref _runState) != 0;

        /// <summary>
        /// Atomically acquires the single-run execution slot.
        /// </summary>
        /// <returns>True when the caller acquired the slot; false when another run owns it.</returns>
        protected bool TryBeginRun()
        {
            if (Interlocked.CompareExchange(ref _runState, 1, 0) != 0)
            {
                return false;
            }
            RaisePropertyChange(nameof(IsRunning));
            return true;
        }

        /// <summary>
        /// Releases the single-run execution slot.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no run owned the slot, which indicates an analysis lifecycle defect.
        /// </exception>
        protected void EndRun()
        {
            if (Interlocked.Exchange(ref _runState, 0) == 0)
            {
                throw new InvalidOperationException("The analysis execution slot was released while idle.");
            }
            RaisePropertyChange(nameof(IsRunning));
        }


        /// <summary>
        /// Raises the <see cref="AnalysisStarting"/> event.
        /// </summary>
        /// <param name="e">The cancelable arguments handlers can veto the run through.</param>
        protected virtual void OnAnalysisStarting(CancelEventArgs e)
        {
            AnalysisStarting?.Invoke(this, e);
        }

        /// <summary>
        /// Raises the <see cref="AnalysisCompleted"/> event.
        /// </summary>
        /// <param name="e">The completion arguments describing the run outcome.</param>
        protected virtual void OnAnalysisCompleted(AnalysisRunCompletedEventArgs e)
        {
            AnalysisCompleted?.Invoke(this, e);
        }

        /// <summary>
        /// Disposes any existing cancellation source and creates a fresh one for the next run,
        /// linked with the caller's external token, returning the joined token derived classes
        /// pass into long-running operations.
        /// </summary>
        /// <param name="externalToken">The caller's cancellation token (may be default).</param>
        /// <returns>The joined cancellation token for the run.</returns>
        protected CancellationToken ResetCancellationToken(CancellationToken externalToken)
        {
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            return _cancellationTokenSource.Token;
        }

        /// <inheritdoc/>
        public virtual void CancelAnalysis()
        {
            _cancellationTokenSource?.Cancel();
        }

        /// <inheritdoc/>
        public abstract Task RunAsync(SafeProgressReporter? progressReporter = null, CancellationToken cancellationToken = default);

        /// <inheritdoc/>
        public abstract IReadOnlyList<ValidationIssue> ValidateIssues();

        /// <inheritdoc/>
        public abstract (bool IsValid, List<string> ValidationMessages) Validate();
    }
}
