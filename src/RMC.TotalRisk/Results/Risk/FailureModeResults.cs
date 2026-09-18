using System;
using System.Collections.Generic;

namespace RMC.TotalRisk.Results.Risk
{
    /// <summary>
    /// The compact summary of one failure mode's realization: the Excess and Fail stream
    /// summaries (the decision-relevant per-mode measures — v1.0 scope, preserved), per
    /// consequence type.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// </remarks>
    public sealed class FailureModeResults
    {
        /// <summary>
        /// Initializes an empty summary (deserialization support).
        /// </summary>
        public FailureModeResults()
        {
            Excess = new SummaryRiskResults();
            Fail = new SummaryRiskResults();
            AdditionalConsequences = new List<ConsequenceResults>();
        }

        /// <summary>
        /// Captures the summary of a finished failure-mode realization, including every
        /// additional consequence type.
        /// </summary>
        /// <param name="failureModeRealization">The realization to summarize.</param>
        /// <exception cref="ArgumentNullException">Thrown when the realization is null.</exception>
        public FailureModeResults(FailureModeRealization failureModeRealization)
        {
            if (failureModeRealization == null) throw new ArgumentNullException(nameof(failureModeRealization));
            Name = failureModeRealization.Name;
            PathLabel = failureModeRealization.PathLabel;
            Excess = new SummaryRiskResults(failureModeRealization.Curves.Excess);
            Fail = new SummaryRiskResults(failureModeRealization.Curves.Fail);
            if (failureModeRealization.AdjustedCurves != null)
            {
                AdjustedExcess = new SummaryRiskResults(failureModeRealization.AdjustedCurves.Excess);
                AdjustedFail = new SummaryRiskResults(failureModeRealization.AdjustedCurves.Fail);
            }
            Contribution = RiskContribution.Copy(failureModeRealization.Contribution);
            AdditionalConsequences = new List<ConsequenceResults>(failureModeRealization.AdditionalCurves.Count);
            for (int k = 0; k < failureModeRealization.AdditionalCurves.Count; k++)
            {
                AdditionalConsequences.Add(new ConsequenceResults(failureModeRealization.AdditionalCurves[k])
                {
                    Contribution = RiskContribution.Copy(k < failureModeRealization.AdditionalContributions.Count
                        ? failureModeRealization.AdditionalContributions[k]
                        : null),
                });
            }
        }

        /// <summary>
        /// The failure mode's display name, copied from the realization (the consequence
        /// terminal's element name when the mode came from a graph).
        /// </summary>
        public string Name { get; set; } = "Failure Mode Risk";

        /// <summary>
        /// The end state's branch path descriptor (append-only; null on earlier
        /// payloads).
        /// </summary>
        public string? PathLabel { get; set; }

        /// <summary>
        /// The incremental (excess) risk summary (primary consequence type).
        /// </summary>
        public SummaryRiskResults Excess { get; set; }

        /// <summary>
        /// The failure risk summary — its total probability is the mode's annualized failure
        /// probability (primary consequence type).
        /// </summary>
        public SummaryRiskResults Fail { get; set; }

        /// <summary>
        /// The additional consequence types' summaries, in declared order (entry k − 1 is type
        /// k; the mode scope records Excess and Fail only, so the other streams summarize
        /// empty). Empty on a single-type analysis.
        /// </summary>
        public List<ConsequenceResults> AdditionalConsequences { get; set; }

        /// <summary>
        /// The combination-adjusted incremental risk summary — this mode's share after the
        /// component's combination method has resolved the modes against one another. Null unless
        /// <c>RiskAnalysisOptions.OutputAdjustedFailureModeCurves</c> was set.
        /// </summary>
        public SummaryRiskResults? AdjustedExcess { get; set; }

        /// <summary>
        /// The combination-adjusted failure risk summary; its total probability is the mode's
        /// share of the component's annualized failure probability. Null unless adjusted output
        /// was requested.
        /// </summary>
        public SummaryRiskResults? AdjustedFail { get; set; }

        /// <summary>
        /// This mode's attributed contribution to the component's risk on the primary
        /// consequence type (the % contribution diagnostic — see <see cref="RiskContribution"/>);
        /// null when not computed (older payloads, band realizations). Per-type contributions
        /// ride <see cref="ConsequenceResults.Contribution"/> on
        /// <see cref="AdditionalConsequences"/>.
        /// </summary>
        public RiskContribution? Contribution { get; set; }
    }
}
