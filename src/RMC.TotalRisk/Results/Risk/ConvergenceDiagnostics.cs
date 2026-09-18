using System;
using System.Collections.Generic;

namespace RMC.TotalRisk.Results.Risk
{
    /// <summary>
    /// Convergence diagnostics aggregated across a full-uncertainty ensemble:
    /// integrator effort and error summaries, and realization-adequacy indicators for the
    /// headline scalar measures. Numbers only — a headless library reports the evidence; the
    /// consuming layer judges adequacy.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The integrator aggregates summarize the per-realization
    /// <c>FunctionEvaluations</c>/<c>StandardError</c>/<c>ChiSquared</c> diagnostics the
    /// realizations already carry (the v1.0 Integration diagnostic's data). The indicators
    /// answer the realization-adequacy question of the uncertainty technical note (§8.2): the
    /// ensemble standard error of each headline mean, SD/√N, and the confidence half-width —
    /// both raw and relative — so "are 1,000 realizations enough for this decision?" is
    /// readable directly. Serialized append-only in the results JSON; a missing block on older
    /// payloads means "not computed".
    /// </para>
    /// </remarks>
    public sealed class ConvergenceDiagnostics
    {
        /// <summary>
        /// The total integrand evaluations across the ensemble.
        /// </summary>
        public double TotalFunctionEvaluations { get; set; }

        /// <summary>
        /// The mean integrand evaluations per realization.
        /// </summary>
        public double MeanFunctionEvaluations { get; set; }

        /// <summary>
        /// The largest single realization's integrand evaluations.
        /// </summary>
        public double MaxFunctionEvaluations { get; set; }

        /// <summary>
        /// The mean integrator error estimate across realizations.
        /// </summary>
        public double MeanStandardError { get; set; }

        /// <summary>
        /// The median integrator error estimate across realizations.
        /// </summary>
        public double MedianStandardError { get; set; }

        /// <summary>
        /// The largest integrator error estimate across realizations.
        /// </summary>
        public double MaxStandardError { get; set; }

        /// <summary>
        /// The mean VEGAS chi-squared consistency diagnostic (joint path only; zero otherwise).
        /// </summary>
        public double MeanChiSquared { get; set; }

        /// <summary>
        /// The largest VEGAS chi-squared consistency diagnostic.
        /// </summary>
        public double MaxChiSquared { get; set; }

        /// <summary>
        /// The realization-adequacy indicators for the headline scalar measures, in a stable
        /// order: the system annualized failure probability, the mean total risk, and the mean
        /// incremental risk — the primary type first, then each declared additional type's mean
        /// total risk.
        /// </summary>
        public List<ConvergenceIndicator> Indicators { get; set; } = new List<ConvergenceIndicator>();
    }

    /// <summary>
    /// One headline measure's realization-adequacy evidence: the ensemble mean, its Monte Carlo
    /// standard error (SD/√N), and the confidence half-width, raw and relative.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// </remarks>
    public sealed class ConvergenceIndicator
    {
        /// <summary>
        /// The measure's display label (e.g., "Annualized Failure Probability").
        /// </summary>
        public string Label { get; set; } = string.Empty;

        /// <summary>
        /// The ensemble mean of the measure.
        /// </summary>
        public double Mean { get; set; }

        /// <summary>
        /// The Monte Carlo standard error of the ensemble mean, SD/√N over the valid
        /// realizations.
        /// </summary>
        public double EnsembleStandardError { get; set; }

        /// <summary>
        /// The standard error relative to the mean (NaN when the mean is zero).
        /// </summary>
        public double RelativeStandardError { get; set; }

        /// <summary>
        /// Half the width of the ensemble confidence interval, (upper − lower) / 2 at the
        /// analysis confidence-interval width.
        /// </summary>
        public double CiHalfWidth { get; set; }

        /// <summary>
        /// The confidence half-width relative to the mean (NaN when the mean is zero).
        /// </summary>
        public double RelativeCiHalfWidth { get; set; }
    }
}
