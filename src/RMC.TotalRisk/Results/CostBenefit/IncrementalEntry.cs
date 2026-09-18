using System;

namespace RMC.TotalRisk.Results.CostBenefit
{
    /// <summary>
    /// One step of the incremental cost-effectiveness table along the cost-ranked
    /// non-dominated set: the cost and benefit increments, the incremental benefit-cost
    /// ratio, and the incremental cost per statistical life saved.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The incremental cost per statistical life saved is identically the discrete
    /// trade-off ratio −Δf₁/Δfⱼ between adjacent noninferior alternatives — the incremental
    /// ratios of planning practice and the ε-constraint sweep's shadow prices are one table,
    /// computed by one shared helper. Ratios with a non-positive denominator are NaN.
    /// </para>
    /// </remarks>
    public sealed class IncrementalEntry
    {
        /// <summary>
        /// Initializes an incremental step.
        /// </summary>
        /// <param name="fromAlternative">The step's lower-cost alternative.</param>
        /// <param name="toAlternative">The step's higher-cost alternative.</param>
        /// <param name="deltaCost">The cost increment (to minus from).</param>
        /// <param name="deltaBenefit">The monetized benefit increment (to minus from).</param>
        /// <param name="incrementalBenefitCostRatio">The benefit increment over the cost increment; NaN when the cost increment is not positive.</param>
        /// <param name="incrementalCostPerLifeSaved">The cost increment over the lives-saved increment; NaN when the lives-saved increment is not positive.</param>
        /// <exception cref="ArgumentNullException">Thrown when a name is null.</exception>
        public IncrementalEntry(string fromAlternative, string toAlternative, double deltaCost,
            double deltaBenefit, double incrementalBenefitCostRatio, double incrementalCostPerLifeSaved)
        {
            FromAlternative = fromAlternative ?? throw new ArgumentNullException(nameof(fromAlternative));
            ToAlternative = toAlternative ?? throw new ArgumentNullException(nameof(toAlternative));
            DeltaCost = deltaCost;
            DeltaBenefit = deltaBenefit;
            IncrementalBenefitCostRatio = incrementalBenefitCostRatio;
            IncrementalCostPerLifeSaved = incrementalCostPerLifeSaved;
        }

        /// <summary>The step's lower-cost alternative.</summary>
        public string FromAlternative { get; }

        /// <summary>The step's higher-cost alternative.</summary>
        public string ToAlternative { get; }

        /// <summary>The cost increment (to minus from).</summary>
        public double DeltaCost { get; }

        /// <summary>The monetized benefit increment (to minus from).</summary>
        public double DeltaBenefit { get; }

        /// <summary>The benefit increment over the cost increment; NaN when the cost increment is not positive.</summary>
        public double IncrementalBenefitCostRatio { get; }

        /// <summary>
        /// The cost increment over the lives-saved increment — the discrete trade-off ratio;
        /// NaN when the lives-saved increment is not positive or unavailable.
        /// </summary>
        public double IncrementalCostPerLifeSaved { get; }
    }
}
