using System;

namespace RMC.TotalRisk.Results.Risk
{
    /// <summary>
    /// The compact summary of one additional consequence type at one results scope: the five
    /// stream summaries, with the declared type labels carried at the system scope.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The multi-consequence axis: entry k − 1 of a summary container's
    /// <c>AdditionalConsequences</c> list summarizes declared consequence type k. Streams a
    /// scope never records (the failure-mode scope records Excess and Fail only — v1.0 scope)
    /// summarize as empty defaults, exactly like the primary containers. The type labels are
    /// filled at the system scope only — the axis is declared once per analysis, so nested
    /// scopes stay label-free.
    /// </para>
    /// </remarks>
    public sealed class ConsequenceResults
    {
        /// <summary>
        /// Initializes an empty summary (deserialization support).
        /// </summary>
        public ConsequenceResults()
        {
            Excess = new SummaryRiskResults();
            Background = new SummaryRiskResults();
            Total = new SummaryRiskResults();
            Fail = new SummaryRiskResults();
            NonFail = new SummaryRiskResults();
        }

        /// <summary>
        /// Captures the summary of one consequence type's finished curve set.
        /// </summary>
        /// <param name="curves">The type's five-stream curve set.</param>
        /// <exception cref="ArgumentNullException">Thrown when the curve set is null.</exception>
        public ConsequenceResults(Curves curves)
        {
            if (curves == null) throw new ArgumentNullException(nameof(curves));
            Excess = new SummaryRiskResults(curves.Excess);
            Background = new SummaryRiskResults(curves.Background);
            Total = new SummaryRiskResults(curves.Total);
            Fail = new SummaryRiskResults(curves.Fail);
            NonFail = new SummaryRiskResults(curves.NonFail);
        }

        /// <summary>
        /// The declared consequence type label (e.g., "Damages"). Filled at the system scope;
        /// empty at nested scopes.
        /// </summary>
        public string SpecifiedConsequence { get; set; } = string.Empty;

        /// <summary>
        /// The declared consequence unit label (e.g., "$"). Filled at the system scope; empty at
        /// nested scopes.
        /// </summary>
        public string ConsequenceUnit { get; set; } = string.Empty;

        /// <summary>
        /// This scope's attributed contribution to its parent for this consequence type
        /// (the % contribution diagnostic — see <see cref="RiskContribution"/>): at failure-mode
        /// scope the mode's contribution to the component; at component scope the component's
        /// contribution to the system. Null when not computed — the system root, older
        /// payloads, and band realizations.
        /// </summary>
        public RiskContribution? Contribution { get; set; }

        /// <summary>
        /// The incremental (excess) risk summary.
        /// </summary>
        public SummaryRiskResults Excess { get; set; }

        /// <summary>
        /// The background (irreducible) risk summary.
        /// </summary>
        public SummaryRiskResults Background { get; set; }

        /// <summary>
        /// The total risk summary.
        /// </summary>
        public SummaryRiskResults Total { get; set; }

        /// <summary>
        /// The failure risk summary.
        /// </summary>
        public SummaryRiskResults Fail { get; set; }

        /// <summary>
        /// The non-failure risk summary.
        /// </summary>
        public SummaryRiskResults NonFail { get; set; }
    }
}
