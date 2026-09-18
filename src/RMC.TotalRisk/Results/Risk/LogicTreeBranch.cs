namespace RMC.TotalRisk.Results.Risk
{
    /// <summary>
    /// One selectable branch of a logic-tree enumeration axis: a positively weighted child of the
    /// axis's epistemic-mixture composite, with the percentile the enumerator forces to select it.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The forced percentile is the midpoint of the branch's cumulative-weight interval over the
    /// composite's positively weighted entries — the same inclusive-upper inverse-CDF algebra the
    /// sampled epistemic mode selects with — so overwriting a selector column with it holds every
    /// realization of a combination block on this branch. Zero-weight entries are unselectable in
    /// both modes and carry no branch.
    /// </para>
    /// </remarks>
    public sealed class LogicTreeBranch
    {
        /// <summary>
        /// Initializes one enumerated branch.
        /// </summary>
        /// <param name="childIndex">The entry index in the owning composite's child list.</param>
        /// <param name="weight">The declared entry weight.</param>
        /// <param name="forcedPercentile">The selector percentile forcing this branch.</param>
        internal LogicTreeBranch(int childIndex, double weight, double forcedPercentile)
        {
            ChildIndex = childIndex;
            Weight = weight;
            ForcedPercentile = forcedPercentile;
        }

        /// <summary>
        /// The entry index of this branch in the owning composite's child list (zero-weight
        /// entries keep their positions, so indices need not be consecutive).
        /// </summary>
        public int ChildIndex { get; }

        /// <summary>
        /// The declared entry weight — the branch's credence as authored. Combination weights
        /// normalize each axis by its positive-weight sum, so only relative values carry meaning.
        /// </summary>
        public double Weight { get; }

        /// <summary>
        /// The selector percentile that forces this branch — strictly inside the branch's
        /// cumulative-weight interval.
        /// </summary>
        public double ForcedPercentile { get; }
    }
}
