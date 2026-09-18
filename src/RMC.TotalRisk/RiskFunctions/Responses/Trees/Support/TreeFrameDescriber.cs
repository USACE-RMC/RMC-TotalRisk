namespace RMC.TotalRisk.RiskFunctions.Responses.Trees.Support
{
    /// <summary>
    /// Renders one tree-kind's compilation-scope frames for cycle diagnostics. Each tree compiler
    /// supplies one shared instance, so pushing a frame never allocates a per-frame closure.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// </remarks>
    internal abstract class TreeFrameDescriber
    {
        /// <summary>The hyphenated tree-kind label used when a cycle stays within one kind.</summary>
        internal abstract string CycleKindLabel { get; }

        /// <summary>Describes one frame without using the description as compute identity.</summary>
        /// <param name="function">The frame's owning tree response.</param>
        /// <param name="node">The frame's authored node.</param>
        /// <returns>The diagnostic label.</returns>
        internal abstract string Describe(object function, object node);
    }
}
