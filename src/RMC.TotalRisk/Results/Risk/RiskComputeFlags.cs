namespace RMC.TotalRisk.Results.Risk
{
    /// <summary>
    /// The per-realization computational-warning flags the risk integrand raises: negative
    /// consequences clamped to zero and combined failure probabilities exceeding one.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Replaces the legacy quartet of <c>ref bool</c> arguments threaded through the compute path.
    /// Each realization owns its instance (written only by that realization's thread inside the
    /// parallel loop), and the engine OR-reduces the instances in a sequential post-pass — shared
    /// mutable flag writes inside the loop would be benign-looking but break the
    /// bit-identical-at-any-thread-count guarantee the reduction order preserves. The clamping
    /// rules themselves are v1.0-documented behavior: negative incremental (and non-failure)
    /// consequences clamp to zero with a warning, and mutually exclusive failure-mode
    /// probabilities summing above one are normalized with a warning.
    /// </para>
    /// </remarks>
    public class RiskComputeFlags
    {
        /// <summary>
        /// True when a failure-consequence evaluation was negative and clamped to zero.
        /// </summary>
        public bool HasNegativeFailureConsequence { get; set; }

        /// <summary>
        /// True when a non-failure-consequence evaluation was negative and clamped to zero.
        /// </summary>
        public bool HasNegativeNonFailureConsequence { get; set; }

        /// <summary>
        /// True when an excess (incremental) consequence was negative before its clamp to zero —
        /// the non-failure consequence exceeded the failure consequence at some hazard level.
        /// </summary>
        public bool HasNegativeExcessConsequence { get; set; }

        /// <summary>
        /// True when combined failure-mode probabilities summed above one and were normalized
        /// (the mutually-exclusive method's documented adjustment).
        /// </summary>
        public bool HasProbabilityGreaterThanOne { get; set; }

        /// <summary>
        /// True when the joint system's exclusive combination enumeration reached its cap, so the
        /// deepest combinations were not enumerated and their mass was attributed to the
        /// all-components-fail combination.
        /// </summary>
        public bool HasTruncatedCombinationEnumeration { get; set; }

        /// <summary>
        /// True when the mass dropped by that truncation was large enough to distort the tail.
        /// </summary>
        public bool HasExcessiveTruncatedMass { get; set; }

        /// <summary>
        /// True when any flag is raised.
        /// </summary>
        public bool Any => HasNegativeFailureConsequence || HasNegativeNonFailureConsequence
            || HasNegativeExcessConsequence || HasProbabilityGreaterThanOne
            || HasTruncatedCombinationEnumeration || HasExcessiveTruncatedMass;

        /// <summary>
        /// OR-merges another instance's flags into this one — the engine's sequential post-pass
        /// reduction across realizations.
        /// </summary>
        /// <param name="other">The flags to merge; a null merge is a no-op.</param>
        public void MergeWith(RiskComputeFlags? other)
        {
            if (other == null) return;
            HasNegativeFailureConsequence |= other.HasNegativeFailureConsequence;
            HasNegativeNonFailureConsequence |= other.HasNegativeNonFailureConsequence;
            HasNegativeExcessConsequence |= other.HasNegativeExcessConsequence;
            HasProbabilityGreaterThanOne |= other.HasProbabilityGreaterThanOne;
            HasTruncatedCombinationEnumeration |= other.HasTruncatedCombinationEnumeration;
            HasExcessiveTruncatedMass |= other.HasExcessiveTruncatedMass;
        }
    }
}
