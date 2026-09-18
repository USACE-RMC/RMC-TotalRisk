using System;

namespace RMC.TotalRisk.Results.CostBenefit
{
    /// <summary>
    /// One grid point of a discrete ε-constraint sweep: the bound, the selected alternative
    /// with its objective values, the trade-off ratio against the previous distinct selection,
    /// the feasible count, and the binding and infeasibility flags.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The trade-off ratio is the discrete shadow price −Δf₁/Δf_ε between this grid point's
    /// selection and the previous distinct selection along the sweep — identically the
    /// incremental cost-effectiveness ratio when the primary is cost-like; NaN at the first
    /// selection, at an unchanged selection, and at an infeasible bound. The bound is binding
    /// when the fixed-constraints-only optimum violates it — the bound actually constrained
    /// the selection.
    /// </para>
    /// </remarks>
    public sealed class EpsilonSweepEntry
    {
        /// <summary>
        /// Initializes a sweep entry.
        /// </summary>
        /// <param name="epsilon">The swept bound.</param>
        /// <param name="selectedAlternative">The selected alternative's name; empty when the bound is infeasible.</param>
        /// <param name="primaryValue">The selection's primary-objective value; NaN when infeasible.</param>
        /// <param name="epsilonValue">The selection's swept-objective value; NaN when infeasible.</param>
        /// <param name="tradeOffRatio">The trade-off ratio against the previous distinct selection; NaN when undefined.</param>
        /// <param name="feasibleCount">The count of alternatives satisfying the fixed constraints and the bound.</param>
        /// <param name="epsilonBinding">True when the bound excluded the fixed-constraints-only optimum.</param>
        /// <param name="isInfeasible">True when no eligible alternative satisfies the bound.</param>
        public EpsilonSweepEntry(double epsilon, string? selectedAlternative, double primaryValue,
            double epsilonValue, double tradeOffRatio, int feasibleCount, bool epsilonBinding,
            bool isInfeasible)
        {
            Epsilon = epsilon;
            SelectedAlternative = selectedAlternative ?? string.Empty;
            PrimaryValue = primaryValue;
            EpsilonValue = epsilonValue;
            TradeOffRatio = tradeOffRatio;
            FeasibleCount = feasibleCount;
            EpsilonBinding = epsilonBinding;
            IsInfeasible = isInfeasible;
        }

        /// <summary>The swept bound.</summary>
        public double Epsilon { get; }

        /// <summary>The selected alternative's name; empty when the bound is infeasible.</summary>
        public string SelectedAlternative { get; }

        /// <summary>The selection's primary-objective value; NaN when infeasible.</summary>
        public double PrimaryValue { get; }

        /// <summary>The selection's swept-objective value; NaN when infeasible.</summary>
        public double EpsilonValue { get; }

        /// <summary>
        /// The discrete trade-off ratio −Δf₁/Δf_ε against the previous distinct selection;
        /// NaN at the first selection, an unchanged selection, or an infeasible bound.
        /// </summary>
        public double TradeOffRatio { get; }

        /// <summary>The count of alternatives satisfying the fixed constraints and the bound.</summary>
        public int FeasibleCount { get; }

        /// <summary>True when the bound excluded the fixed-constraints-only optimum.</summary>
        public bool EpsilonBinding { get; }

        /// <summary>True when no eligible alternative satisfies the bound.</summary>
        public bool IsInfeasible { get; }
    }
}
