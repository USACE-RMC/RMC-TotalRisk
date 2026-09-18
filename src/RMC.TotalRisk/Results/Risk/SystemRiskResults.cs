using System;
using System.Collections.Generic;

namespace RMC.TotalRisk.Results.Risk
{
    /// <summary>
    /// The compact summary of one full realization: the system-level five stream summaries, the
    /// per-component summaries, and the integration diagnostics.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// </remarks>
    public sealed class SystemRiskResults
    {
        /// <summary>
        /// Initializes an empty summary (deserialization support).
        /// </summary>
        public SystemRiskResults()
        {
            ComponentResults = new List<ComponentResults>();
            Excess = new SummaryRiskResults();
            Background = new SummaryRiskResults();
            Total = new SummaryRiskResults();
            Fail = new SummaryRiskResults();
            NonFail = new SummaryRiskResults();
            AdditionalConsequences = new List<ConsequenceResults>();
            ConsequenceLabels = new List<string>();
            ConsequenceUnits = new List<string>();
        }

        /// <summary>
        /// Captures the summary of a finished system realization, including every additional
        /// consequence type and the declared axis labels the realization carries.
        /// </summary>
        /// <param name="systemRealization">The realization to summarize.</param>
        /// <exception cref="ArgumentNullException">Thrown when the realization is null.</exception>
        public SystemRiskResults(SystemRealization systemRealization)
        {
            if (systemRealization == null) throw new ArgumentNullException(nameof(systemRealization));
            Excess = new SummaryRiskResults(systemRealization.Curves.Excess);
            Background = new SummaryRiskResults(systemRealization.Curves.Background);
            Total = new SummaryRiskResults(systemRealization.Curves.Total);
            Fail = new SummaryRiskResults(systemRealization.Curves.Fail);
            NonFail = new SummaryRiskResults(systemRealization.Curves.NonFail);
            ConsequenceLabels = new List<string>(systemRealization.ConsequenceLabels);
            ConsequenceUnits = new List<string>(systemRealization.ConsequenceUnits);
            AdditionalConsequences = new List<ConsequenceResults>(systemRealization.AdditionalCurves.Count);
            for (int k = 0; k < systemRealization.AdditionalCurves.Count; k++)
            {
                AdditionalConsequences.Add(new ConsequenceResults(systemRealization.AdditionalCurves[k])
                {
                    SpecifiedConsequence = k + 1 < ConsequenceLabels.Count ? ConsequenceLabels[k + 1] : string.Empty,
                    ConsequenceUnit = k + 1 < ConsequenceUnits.Count ? ConsequenceUnits[k + 1] : string.Empty,
                });
            }

            ComponentResults = new List<ComponentResults>(systemRealization.Components.Count);
            for (int i = 0; i < systemRealization.Components.Count; i++)
            {
                ComponentResults.Add(new ComponentResults(systemRealization.Components[i]));
            }

            FunctionEvaluations = systemRealization.FunctionEvaluations;
            StandardError = systemRealization.StandardError;
            ChiSquared = systemRealization.ChiSquared;
        }

        /// <summary>
        /// The per-component summaries, in analysis component order.
        /// </summary>
        public List<ComponentResults> ComponentResults { get; set; }

        /// <summary>
        /// The additional consequence types' summaries, in declared order (entry k − 1 is type
        /// k, with its declared labels). Empty on a single-type analysis.
        /// </summary>
        public List<ConsequenceResults> AdditionalConsequences { get; set; }

        /// <summary>
        /// The declared consequence type labels, one per type including the primary (entry 0).
        /// </summary>
        public List<string> ConsequenceLabels { get; set; }

        /// <summary>
        /// The declared consequence unit labels, parallel to <see cref="ConsequenceLabels"/>.
        /// </summary>
        public List<string> ConsequenceUnits { get; set; }

        /// <summary>
        /// The system incremental (excess) risk summary.
        /// </summary>
        public SummaryRiskResults Excess { get; set; }

        /// <summary>
        /// The system background (irreducible) risk summary.
        /// </summary>
        public SummaryRiskResults Background { get; set; }

        /// <summary>
        /// The system total risk summary.
        /// </summary>
        public SummaryRiskResults Total { get; set; }

        /// <summary>
        /// The system failure risk summary — its total probability is the system annualized
        /// failure probability.
        /// </summary>
        public SummaryRiskResults Fail { get; set; }

        /// <summary>
        /// The system non-failure risk summary.
        /// </summary>
        public SummaryRiskResults NonFail { get; set; }

        /// <summary>
        /// The total number of integrand evaluations behind the realization.
        /// </summary>
        public double FunctionEvaluations { get; set; }

        /// <summary>
        /// The integrator's error estimate for the realization.
        /// </summary>
        public double StandardError { get; set; }

        /// <summary>
        /// The VEGAS chi-squared consistency diagnostic (joint path only; zero otherwise).
        /// </summary>
        public double ChiSquared { get; set; }
    }
}
