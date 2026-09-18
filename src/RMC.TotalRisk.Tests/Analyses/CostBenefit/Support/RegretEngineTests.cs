using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Analyses.CostBenefit.Support;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results;

namespace RMC.TotalRisk.Tests.Analyses.CostBenefit.Support;

/// <summary>
/// Tests the shared-state regret machinery: the hand-matrix computation (direction-aware
/// regret, NaN-state drops with diagnostics, tie-crediting win counts, the exact weighted
/// aggregates) and the state-alignment misalignment reasons one by one, including the
/// shared-instance escape.
/// </summary>
[TestClass]
public class RegretEngineTests
{
    /// <summary>Builds an enumeration map for the misalignment checks.</summary>
    /// <param name="branchCounts">The per-axis branch counts.</param>
    /// <param name="realizationsPerCombination">The block size M.</param>
    /// <param name="sharedFlags">The per-axis shared-variable flags (null = all shared).</param>
    /// <param name="names">The per-axis names (null = θ0, θ1, …).</param>
    /// <param name="weightNudge">A nudge added to the first branch weight before normalizing the rest away (bitwise inequality).</param>
    /// <returns>The map.</returns>
    private static LogicTreeEnumerationMap BuildMap(int[] branchCounts,
        int realizationsPerCombination = 2, bool[]? sharedFlags = null, string[]? names = null,
        double weightNudge = 0d)
    {
        var axes = new List<LogicTreeAxis>(branchCounts.Length);
        int combinations = 1;
        for (int a = 0; a < branchCounts.Length; a++)
        {
            var branches = new List<LogicTreeBranch>(branchCounts[a]);
            for (int b = 0; b < branchCounts[a]; b++)
            {
                double weight = 1d / branchCounts[a];
                if (a == 0 && b == 0) weight += weightNudge;
                branches.Add(new LogicTreeBranch(b, weight, (b + 0.5d) / branchCounts[a]));
            }
            axes.Add(new LogicTreeAxis(names != null ? names[a] : $"θ{a}",
                sharedFlags == null || sharedFlags[a], null, branches));
            combinations *= branchCounts[a];
        }
        var weights = new double[combinations];
        for (int c = 0; c < combinations; c++) weights[c] = 1d / combinations;
        return new LogicTreeEnumerationMap(axes, realizationsPerCombination, weights);
    }

    /// <summary>
    /// Verifies the regret computation: direction-aware regret against the per-state best,
    /// tie-crediting win counts, and the exact unrenormalized weighted aggregates.
    /// </summary>
    [TestMethod]
    public void Test_Compute_RegretMatrixAndAggregates()
    {
        // Arrange
        var names = new[] { "A", "B" };
        var stateLabels = new[] { "s1", "s2", "s3" };
        var stateWeights = new[] { 0.2d, 0.5d, 0.3d };
        var means = new[] { new[] { 10d, 20d, 30d }, new[] { 12d, 18d, 30d } };
        var errors = new[] { new[] { 0.1d, 0.2d, 0.3d }, new[] { 0.4d, 0.5d, 0.6d } };
        var diagnostics = new List<ComputationDiagnostic>();

        // Act
        var computation = RegretEngine.Compute("criterion", ObjectiveDirection.Minimize, names,
            stateLabels, stateWeights, means, errors, diagnostics)!;

        // Assert — per-state bests {10, 18, 30}; s3 is an exact tie credited to both.
        CollectionAssert.AreEqual(new[] { 0d, 2d, 0d }, computation.Regrets[0]);
        CollectionAssert.AreEqual(new[] { 2d, 0d, 0d }, computation.Regrets[1]);
        Assert.AreEqual(2d, computation.MaxRegrets[0], 0d);
        Assert.AreEqual(2d, computation.MaxRegrets[1], 0d);
        Assert.AreEqual(0.5d * 2d, computation.ExpectedRegrets[0], 0d);
        Assert.AreEqual(0.2d * 2d, computation.ExpectedRegrets[1], 0d);
        CollectionAssert.AreEqual(new[] { 2, 2 }, computation.WinCounts);
        CollectionAssert.AreEqual(errors[1], computation.StandardErrors[1]);
        Assert.AreEqual(0, diagnostics.Count);
    }

    /// <summary>
    /// Verifies the Maximize direction, the NaN-state drop with its named diagnostic, and the
    /// all-states-dropped null result.
    /// </summary>
    [TestMethod]
    public void Test_Compute_DirectionAndNaNDrops()
    {
        // Arrange — B's s2 block mean is NaN, so s2 drops with a TRC2009 notice.
        var names = new[] { "A", "B" };
        var stateLabels = new[] { "s1", "s2" };
        var stateWeights = new[] { 0.6d, 0.4d };
        var means = new[] { new[] { 5d, 9d }, new[] { 7d, double.NaN } };
        var errors = new[] { new[] { 0.1d, 0.1d }, new[] { 0.1d, double.NaN } };
        var diagnostics = new List<ComputationDiagnostic>();

        // Act
        var computation = RegretEngine.Compute("criterion", ObjectiveDirection.Maximize, names,
            stateLabels, stateWeights, means, errors, diagnostics)!;
        var emptyDiagnostics = new List<ComputationDiagnostic>();
        var empty = RegretEngine.Compute("criterion", ObjectiveDirection.Minimize, names,
            stateLabels, stateWeights,
            new[] { new[] { double.NaN, 1d }, new[] { 1d, double.NaN } },
            errors, emptyDiagnostics);

        // Assert — under Maximize the larger value is best; only s1 survives.
        Assert.AreEqual(1, computation.StateLabels.Length);
        Assert.AreEqual("s1", computation.StateLabels[0]);
        Assert.AreEqual(2d, computation.Regrets[0][0], 0d);
        Assert.AreEqual(0d, computation.Regrets[1][0], 0d);
        Assert.AreEqual(1, diagnostics.FindAll(d => d.Code == "TRC2009").Count);
        StringAssert.Contains(diagnostics[0].Message, "s2");
        Assert.IsNull(empty, "A computation with no surviving state publishes nothing.");
        Assert.AreEqual(2, emptyDiagnostics.Count, "Every dropped state is named.");
    }

    /// <summary>
    /// Verifies the single-state map degenerates cleanly: one column, zero regret for the
    /// best row, and aggregates equal to the single state's regret.
    /// </summary>
    [TestMethod]
    public void Test_Compute_SingleState()
    {
        // Act
        var computation = RegretEngine.Compute("criterion", ObjectiveDirection.Minimize,
            new[] { "A", "B" }, new[] { "s1" }, new[] { 1d },
            new[] { new[] { 4d }, new[] { 4d } }, new[] { new[] { 0.1d }, new[] { 0.2d } },
            new List<ComputationDiagnostic>())!;

        // Assert — an exact tie: zero regret everywhere, both rows win the state.
        Assert.AreEqual(0d, computation.MaxRegrets[0], 0d);
        Assert.AreEqual(0d, computation.MaxRegrets[1], 0d);
        Assert.AreEqual(0d, computation.ExpectedRegrets[1], 0d);
        CollectionAssert.AreEqual(new[] { 1, 1 }, computation.WinCounts);
    }

    /// <summary>
    /// Verifies the misalignment reasons one by one — absent map, combination count, block
    /// size, axis count, axis name, axis kind, the unbound-composite rule, branch count, and
    /// bitwise weight inequality — plus the aligned result and the shared-instance escape.
    /// </summary>
    [TestMethod]
    public void Test_DescribeMisalignment_ReasonsAndEscape()
    {
        // Arrange
        var baseline = BuildMap(new[] { 3, 2 });

        // Act / Assert — aligned twin, then each mismatch.
        Assert.IsNull(RegretEngine.DescribeMisalignment(baseline, BuildMap(new[] { 3, 2 }), false));
        StringAssert.Contains(RegretEngine.DescribeMisalignment(baseline, null, false),
            "no logic-tree enumeration map");
        StringAssert.Contains(RegretEngine.DescribeMisalignment(baseline,
            BuildMap(new[] { 2, 2 }), false), "combination counts differ");
        StringAssert.Contains(RegretEngine.DescribeMisalignment(baseline,
            BuildMap(new[] { 3, 2 }, realizationsPerCombination: 5), false),
            "realizations per combination differ");
        StringAssert.Contains(RegretEngine.DescribeMisalignment(baseline,
            BuildMap(new[] { 6 }), false), "axis counts differ");
        StringAssert.Contains(RegretEngine.DescribeMisalignment(baseline,
            BuildMap(new[] { 3, 2 }, names: new[] { "φ0", "θ1" }), false), "names differ");
        StringAssert.Contains(RegretEngine.DescribeMisalignment(baseline,
            BuildMap(new[] { 3, 2 }, sharedFlags: new[] { false, true }), false),
            "differs in kind");
        var unboundBaseline = BuildMap(new[] { 3, 2 }, sharedFlags: new[] { false, true });
        StringAssert.Contains(RegretEngine.DescribeMisalignment(unboundBaseline,
            BuildMap(new[] { 3, 2 }, sharedFlags: new[] { false, true }), false),
            "unbound composite axis");
        StringAssert.Contains(RegretEngine.DescribeMisalignment(baseline,
            BuildMap(new[] { 3, 2 }, weightNudge: 1e-16), false), "not bitwise equal");

        // The shared-instance escape overrides everything, unbound axes included.
        Assert.IsNull(RegretEngine.DescribeMisalignment(unboundBaseline, null, true));
    }

    /// <summary>
    /// Verifies branch-count misalignment is reported for a matched axis grid whose axis
    /// branch counts differ while the combination count agrees.
    /// </summary>
    [TestMethod]
    public void Test_DescribeMisalignment_BranchCountWithEqualCombinations()
    {
        // Arrange — 3×2 and 2×3 share the combination count 6 but not the axis shapes.
        var baseline = BuildMap(new[] { 3, 2 });
        var transposed = BuildMap(new[] { 2, 3 });

        // Act / Assert
        StringAssert.Contains(RegretEngine.DescribeMisalignment(baseline, transposed, false),
            "branch counts differ");
    }
}
