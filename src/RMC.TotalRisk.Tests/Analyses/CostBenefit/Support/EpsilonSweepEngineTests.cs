using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Analyses.CostBenefit.Support;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results.CostBenefit;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Tests.Analyses.CostBenefit.Support;

/// <summary>
/// Tests the ε-constraint sweep engine on hand matrices: the shared trade-off helper, grid
/// construction with its degenerate cases, selection with ties and eligibility, binding and
/// infeasibility flags, the noninferior screen with ties kept, and NaN exclusion.
/// </summary>
[TestClass]
public class EpsilonSweepEngineTests
{
    /// <summary>Runs the engine over a hand matrix with defaults for the common axes.</summary>
    /// <param name="names">The names.</param>
    /// <param name="primary">The primary values (minimized).</param>
    /// <param name="epsilon">The swept values.</param>
    /// <param name="grid">The explicit grid, or null for automatic.</param>
    /// <param name="feasible">The fixed-feasibility flags, or null for all feasible.</param>
    /// <param name="eligible">The eligibility flags, or null for all eligible.</param>
    /// <param name="gridPoints">The automatic grid's point count.</param>
    /// <param name="diagnostics">The diagnostics sink, or null for a throwaway.</param>
    /// <returns>The sweep results.</returns>
    private static EpsilonSweepResults Run(string[] names, double[] primary, double[] epsilon,
        double[]? grid = null, bool[]? feasible = null, bool[]? eligible = null, int gridPoints = 10,
        List<ComputationDiagnostic>? diagnostics = null)
    {
        if (feasible == null)
        {
            feasible = new bool[names.Length];
            for (int i = 0; i < feasible.Length; i++) feasible[i] = true;
        }
        eligible ??= feasible;
        return EpsilonSweepEngine.Run("Primary", ObjectiveDirection.Minimize, "Swept", names,
            primary, epsilon, feasible, eligible, grid, gridPoints,
            diagnostics ?? new List<ComputationDiagnostic>());
    }

    /// <summary>Verifies the shared trade-off helper's formula and its coincident-value NaN.</summary>
    [TestMethod]
    public void Test_TradeOffRatio_FormulaAndNaN()
    {
        // Act / Assert — −Δf₁/Δf_ε: primary falls 10 as the bound loosens 2 → ratio 5.
        Assert.AreEqual(5d, EpsilonSweepEngine.TradeOffRatio(30d, 20d, 4d, 6d), 0d);
        Assert.AreEqual(-5d, EpsilonSweepEngine.TradeOffRatio(20d, 30d, 4d, 6d), 0d);
        Assert.IsTrue(double.IsNaN(EpsilonSweepEngine.TradeOffRatio(30d, 20d, 4d, 4d)));
    }

    /// <summary>
    /// Verifies the sweep walk: selections improve as the bound loosens, the trade-off column
    /// carries the ratio against the previous distinct selection, binding clears once the
    /// unconstrained optimum is admitted, and the noninferior set keeps the frontier order.
    /// </summary>
    [TestMethod]
    public void Test_Run_SelectionWalkBindingAndNoninferior()
    {
        // Arrange — three frontier points and one dominated alternative (D: worse primary
        // and worse swept value than B).
        string[] names = { "A", "B", "C", "D" };
        double[] primary = { 30d, 20d, 15d, 25d };
        double[] epsilon = { 1d, 2d, 4d, 3d };

        // Act
        EpsilonSweepResults results = Run(names, primary, epsilon, grid: new[] { 1d, 2d, 3d, 4d });

        // Assert — the per-ε selections walk the frontier.
        Assert.AreEqual("A", results.Entries[0].SelectedAlternative);
        Assert.AreEqual("B", results.Entries[1].SelectedAlternative);
        Assert.AreEqual("B", results.Entries[2].SelectedAlternative);
        Assert.AreEqual("C", results.Entries[3].SelectedAlternative);

        // The trade-off ratios against the previous distinct selection.
        Assert.IsTrue(double.IsNaN(results.Entries[0].TradeOffRatio));
        Assert.AreEqual(EpsilonSweepEngine.TradeOffRatio(30d, 20d, 1d, 2d),
            results.Entries[1].TradeOffRatio, 0d);
        Assert.IsTrue(double.IsNaN(results.Entries[2].TradeOffRatio), "An unchanged selection carries no ratio.");
        Assert.AreEqual(EpsilonSweepEngine.TradeOffRatio(20d, 15d, 2d, 4d),
            results.Entries[3].TradeOffRatio, 0d);

        // Binding until the unconstrained optimum (C at ε = 4) is admitted.
        Assert.IsTrue(results.Entries[0].EpsilonBinding);
        Assert.IsTrue(results.Entries[2].EpsilonBinding);
        Assert.IsFalse(results.Entries[3].EpsilonBinding);

        // Feasible counts and the noninferior set (D is dominated by B).
        Assert.AreEqual(1, results.Entries[0].FeasibleCount);
        Assert.AreEqual(4, results.Entries[3].FeasibleCount);
        CollectionAssert.AreEqual(new[] { "A", "B", "C" },
            (System.Collections.ICollection)results.NoninferiorAlternatives);
    }

    /// <summary>
    /// Verifies the automatic grid spans the feasible payoff range with inclusive endpoints,
    /// collapses to one point on a degenerate range, and reports an empty sweep with a named
    /// diagnostic when nothing is fixed-feasible.
    /// </summary>
    [TestMethod]
    public void Test_Run_AutomaticGridAndDegenerateCases()
    {
        // Arrange / Act — the automatic grid over ε values {2, 6} at five points.
        EpsilonSweepResults spread = Run(new[] { "A", "B" }, new[] { 10d, 5d }, new[] { 2d, 6d },
            gridPoints: 5);

        // Assert — inclusive uniform endpoints.
        CollectionAssert.AreEqual(new[] { 2d, 3d, 4d, 5d, 6d }, (System.Collections.ICollection)spread.Grid);

        // A shared ε value collapses to a one-point grid.
        EpsilonSweepResults degenerate = Run(new[] { "A", "B" }, new[] { 10d, 5d }, new[] { 3d, 3d });
        CollectionAssert.AreEqual(new[] { 3d }, (System.Collections.ICollection)degenerate.Grid);

        // An empty fixed-feasible set yields an empty sweep and the named diagnostic.
        var diagnostics = new List<ComputationDiagnostic>();
        EpsilonSweepResults empty = Run(new[] { "A" }, new[] { 1d }, new[] { 1d },
            feasible: new[] { false }, eligible: new[] { true }, diagnostics: diagnostics);
        Assert.AreEqual(0, empty.Grid.Count);
        Assert.AreEqual(0, empty.Entries.Count);
        Assert.AreEqual(1, diagnostics.Count);
        Assert.AreEqual("TRC2004", diagnostics[0].Code);
    }

    /// <summary>
    /// Verifies infeasible bounds, selection ties breaking to the first row, eligibility
    /// exclusion under the do-no-harm gate, and NaN exclusion with its diagnostic.
    /// </summary>
    [TestMethod]
    public void Test_Run_InfeasibilityTiesEligibilityAndNaN()
    {
        // Arrange — an explicit grid whose first bound admits nothing.
        string[] names = { "A", "B", "C" };
        double[] primary = { 10d, 10d, 5d };
        double[] epsilon = { 2d, 2d, 3d };

        // Act — C is ineligible (a do-no-harm exclusion), so the ties between A and B decide.
        EpsilonSweepResults results = Run(names, primary, epsilon, grid: new[] { 1d, 2d, 3d },
            eligible: new[] { true, true, false });

        // Assert — the infeasible bound, then the tie to the first row, then C stays excluded.
        Assert.IsTrue(results.Entries[0].IsInfeasible);
        Assert.AreEqual(string.Empty, results.Entries[0].SelectedAlternative);
        Assert.AreEqual(0, results.Entries[0].FeasibleCount);
        Assert.AreEqual("A", results.Entries[1].SelectedAlternative);
        Assert.AreEqual("A", results.Entries[2].SelectedAlternative,
            "An ineligible alternative is never selected even when it optimizes the primary.");
        CollectionAssert.AreEqual(new[] { "A", "B", "C" },
            (System.Collections.ICollection)results.NoninferiorAlternatives);

        // A NaN value excludes the alternative with a named diagnostic.
        var diagnostics = new List<ComputationDiagnostic>();
        EpsilonSweepResults withNaN = Run(new[] { "A", "B" }, new[] { double.NaN, 5d }, new[] { 1d, 2d },
            diagnostics: diagnostics);
        Assert.AreEqual("B", withNaN.Entries[withNaN.Entries.Count - 1].SelectedAlternative);
        CollectionAssert.AreEqual(new[] { "B" }, (System.Collections.ICollection)withNaN.NoninferiorAlternatives);
        Assert.AreEqual(1, diagnostics.Count);
        Assert.AreEqual("TRC2003", diagnostics[0].Code);
    }
}
