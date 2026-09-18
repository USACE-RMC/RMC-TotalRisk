using System;
using RMC.TotalRisk.Analyses.Risk;

namespace RMC.TotalRisk.Analyses.CostBenefit
{
    /// <summary>
    /// One alternative of a cost-benefit study: a name, the risk analysis describing the
    /// system under that alternative, its tagged cost stream, and an optional staged
    /// life-cycle plan.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The alternative is immutable (replace it to edit). The system is referenced, never
    /// owned or mutated — alternatives may share one analysis instance (the designated
    /// baseline's system carrying different plans is the typical arrangement), and every
    /// trajectory evaluation runs on throwaway clones, leaving the referenced analysis
    /// byte-untouched. The cost stream defaults empty — the typical baseline. Name
    /// uniqueness within a study, and every horizon and axis rule, are validated at the study
    /// level.
    /// </para>
    /// </remarks>
    public sealed class RiskReductionAlternative
    {
        #region Construction

        /// <summary>
        /// Initializes an alternative.
        /// </summary>
        /// <param name="name">The display name (non-blank; unique within a study, validated there).</param>
        /// <param name="system">The risk analysis describing the system under this alternative.</param>
        /// <param name="costs">The tagged cost stream; null reads as empty.</param>
        /// <param name="plan">The staged life-cycle plan, or null for none.</param>
        /// <param name="description">The display description; null reads as empty.</param>
        /// <exception cref="ArgumentException">Thrown when the name is blank.</exception>
        /// <exception cref="ArgumentNullException">Thrown when the system is null.</exception>
        public RiskReductionAlternative(string name, RiskAnalysis system,
            CostStream? costs = null, LifeCyclePlan? plan = null, string? description = null)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("The alternative name must not be blank.", nameof(name));
            Name = name;
            System = system ?? throw new ArgumentNullException(nameof(system));
            Costs = costs ?? new CostStream();
            Plan = plan;
            Description = description ?? string.Empty;
        }

        #endregion

        #region Members

        /// <summary>
        /// The display name, unique within a study.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// The display description (empty when none was given).
        /// </summary>
        public string Description { get; }

        /// <summary>
        /// The risk analysis describing the system under this alternative. Referenced, never
        /// owned: instances may be shared across alternatives, and a study run never mutates
        /// them.
        /// </summary>
        public RiskAnalysis System { get; }

        /// <summary>
        /// The tagged cost stream (empty for the typical baseline).
        /// </summary>
        public CostStream Costs { get; }

        /// <summary>
        /// The staged life-cycle plan, or null when the alternative is unstaged.
        /// </summary>
        public LifeCyclePlan? Plan { get; }

        #endregion
    }
}
