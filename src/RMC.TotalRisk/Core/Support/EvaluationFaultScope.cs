using System;

namespace RMC.TotalRisk.Core.Support
{
    /// <summary>
    /// A per-thread first-fault slot that preserves an evaluation diagnostic across the adaptive
    /// integrators' exception absorption.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The engine's integrators run with <c>ReportFailure</c> false, so an exception thrown by an
    /// integrand evaluation is absorbed and only a failure status survives — the exception object
    /// and its message are destroyed. An evaluation guard writes its diagnostic here immediately
    /// before throwing; the engine's integration-failure guards consume it on the same thread
    /// (the integrators evaluate synchronously on the calling thread) and append it to the
    /// surfaced failure. Only the first fault per thread is kept — the first out-of-range
    /// evaluation is the actionable one — and consuming clears the slot so a later run starts
    /// clean.
    /// </para>
    /// </remarks>
    internal static class EvaluationFaultScope
    {
        /// <summary>
        /// The per-thread first-fault diagnostic.
        /// </summary>
        [ThreadStatic]
        private static string? _firstFault;

        /// <summary>
        /// Records a diagnostic when the slot is empty; later faults on the same thread are
        /// ignored until the slot is consumed.
        /// </summary>
        /// <param name="diagnostic">The diagnostic message to keep.</param>
        internal static void TryCapture(string diagnostic)
        {
            if (_firstFault == null)
            {
                _firstFault = diagnostic;
            }
        }

        /// <summary>
        /// Returns the recorded diagnostic and clears the slot; null when no fault was recorded.
        /// </summary>
        /// <returns>The recorded diagnostic, or null.</returns>
        internal static string? Consume()
        {
            string? fault = _firstFault;
            _firstFault = null;
            return fault;
        }
    }
}
