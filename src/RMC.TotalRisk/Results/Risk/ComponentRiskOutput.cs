using System.Collections.Generic;

namespace RMC.TotalRisk.Results.Risk
{
    /// <summary>
    /// The risk integrand's output at one hazard evaluation point: the combined failure
    /// probability, the conditional mean consequences, and the per-pathway entry lists.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The scalar members preserve the v1.0 shape. The three entry lists realize a capability
    /// v1.0 declared but never implemented (the commented-out design intent at
    /// <c>ComponentRiskOutput.vb:39</c>): they carry the within-component pathway distribution —
    /// one entry per recorded failure pathway/branch — so the joint system-risk method
    /// can enumerate real component failure/non-failure combinations instead of collapsing each
    /// component to its conditional mean (the documented v1.0 system-tail defect). Documented
    /// limitation: <see cref="ExcessConsequences"/> entries are computed against the
    /// branch-weighted mean non-failure consequence — the recorded curves carry the exact
    /// failure/non-failure branch pairs, but the list surface collapses the non-failure spread
    /// (docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §7.8).
    /// </para>
    /// </remarks>
    public class ComponentRiskOutput
    {
        /// <summary>
        /// Initializes an empty output.
        /// </summary>
        public ComponentRiskOutput()
        {
            ResponseProbabilities = new List<double>();
            FailureConsequences = new List<double>();
            ExcessConsequences = new List<double>();
        }

        /// <summary>
        /// The combined (total) probability of failure at the hazard level, clamped to [0, 1].
        /// </summary>
        public double ProbabilityOfFailure { get; set; }

        /// <summary>
        /// The probability of non-failure at the hazard level (zero when the component has no
        /// non-failure mode — v1.0 semantics).
        /// </summary>
        public double ProbabilityOfNonFailure { get; set; }

        /// <summary>
        /// The conditional mean failure consequence at the hazard level, E[C_F | failure].
        /// </summary>
        public double MeanFailureConsequences { get; set; }

        /// <summary>
        /// The conditional mean excess (incremental) consequence at the hazard level.
        /// </summary>
        public double MeanExcessConsequences { get; set; }

        /// <summary>
        /// The (branch-weighted mean) non-failure consequence at the hazard level.
        /// </summary>
        public double NonFailureConsequences { get; set; }

        /// <summary>
        /// The recorded pathway/branch probabilities at the hazard level, P[pathway|x] — parallel
        /// to <see cref="FailureConsequences"/> and <see cref="ExcessConsequences"/>.
        /// </summary>
        public List<double> ResponseProbabilities { get; }

        /// <summary>
        /// The recorded pathway/branch failure consequences, parallel to
        /// <see cref="ResponseProbabilities"/>.
        /// </summary>
        public List<double> FailureConsequences { get; }

        /// <summary>
        /// The recorded pathway/branch excess consequences against the mean non-failure
        /// consequence, parallel to <see cref="ResponseProbabilities"/>.
        /// </summary>
        public List<double> ExcessConsequences { get; }

        /// <summary>
        /// Clears the output for reuse as compute-workspace scratch: the entry lists
        /// empty in place (capacity retained — the allocation-elimination point) and the scalars
        /// zero. The sampled compute paths hand out reused instances that stay valid until the
        /// next evaluation on the owning sampled object.
        /// </summary>
        internal void Reset()
        {
            ResponseProbabilities.Clear();
            FailureConsequences.Clear();
            ExcessConsequences.Clear();
            ProbabilityOfFailure = 0d;
            ProbabilityOfNonFailure = 0d;
            MeanFailureConsequences = 0d;
            MeanExcessConsequences = 0d;
            NonFailureConsequences = 0d;
        }
    }
}
