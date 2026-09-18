using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Support;

namespace RMC.TotalRisk.Tests.Core.Support;

/// <summary>
/// Unit tests for the given-data value-of-information estimator: exact hand-computable
/// decompositions, the exact between-plus-within identity, weighted/replication equivalence,
/// deterministic tie handling, NaN filtering, guard behavior, and the exceedance-movement
/// closed forms.
/// </summary>
[TestClass]
public class ValueOfInformationEstimatorTests
{
    /// <summary>
    /// Verifies the exact two-bin hand case: sorted halves {1,3} and {5,7} give conditional
    /// means 2 and 6 about the grand mean 4 — between-bin variance 4, total variance 5.
    /// </summary>
    [TestMethod]
    public void Test_MainEffect_TwoBinHandCase_Exact()
    {
        // Arrange
        var x = new[] { 0.1d, 0.2d, 0.3d, 0.4d };
        var y = new[] { 1d, 3d, 5d, 7d };

        // Act
        var effect = ValueOfInformationEstimator.MainEffect(x, y, null, 2);

        // Assert
        Assert.AreEqual(4d, effect.ResolvableVariance, 1e-15, "Between-bin variance of the conditional means.");
        Assert.AreEqual(5d, effect.TotalVariance, 1e-15, "Total population variance.");
        Assert.AreEqual(4, effect.Pairs);
    }

    /// <summary>
    /// Verifies a constant output decomposes to exactly zero resolvable variance — an input
    /// that explains nothing reports nothing.
    /// </summary>
    [TestMethod]
    public void Test_MainEffect_ConstantOutput_ZeroResolvable()
    {
        // Arrange
        var x = new[] { 0.9d, 0.1d, 0.5d, 0.3d, 0.7d, 0.2d };
        var y = new[] { 2d, 2d, 2d, 2d, 2d, 2d };

        // Act
        var effect = ValueOfInformationEstimator.MainEffect(x, y, null, 3);

        // Assert
        Assert.AreEqual(0d, effect.ResolvableVariance, 0d, "A constant output has no variance to resolve.");
        Assert.AreEqual(0d, effect.TotalVariance, 0d);
    }

    /// <summary>
    /// Verifies the exact variance decomposition: the between-bin part plus the independently
    /// recomputed within-bin part equals the total variance to rounding.
    /// </summary>
    [TestMethod]
    public void Test_MainEffect_BetweenPlusWithin_EqualsTotal()
    {
        // Arrange — a deterministic irregular sample.
        int n = 40;
        var x = new double[n];
        var y = new double[n];
        for (int i = 0; i < n; i++)
        {
            x[i] = Math.Sin(3.7d * i + 0.4d) * 0.5d + 0.5d;
            y[i] = 3d * x[i] * x[i] + Math.Cos(11d * i);
        }

        // Act
        var effect = ValueOfInformationEstimator.MainEffect(x, y, null, 5);

        // Assert — recompute the within part over the same equal-count partition (unit
        // weights, n divisible by bins, unique x values: 8 items per bin in sorted order).
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        Array.Sort(order, (a, b) => x[a].CompareTo(x[b]));
        double grand = 0d;
        for (int i = 0; i < n; i++) grand += y[i];
        grand /= n;
        double within = 0d;
        for (int b = 0; b < 5; b++)
        {
            double binMean = 0d;
            for (int k = 0; k < 8; k++) binMean += y[order[b * 8 + k]];
            binMean /= 8d;
            for (int k = 0; k < 8; k++)
            {
                double deviation = y[order[b * 8 + k]] - binMean;
                within += deviation * deviation;
            }
        }
        within /= n;
        Assert.AreEqual(effect.TotalVariance, effect.ResolvableVariance + within, 1e-12 * effect.TotalVariance,
            "The equal-weight partition decomposes the total variance exactly.");
    }

    /// <summary>
    /// Verifies integer reliability weights reproduce the replicated-sample decomposition —
    /// the weighted partition generalizes equal-frequency counting.
    /// </summary>
    [TestMethod]
    public void Test_MainEffect_IntegerWeights_MatchReplication()
    {
        // Arrange
        var x = new[] { 0.1d, 0.2d, 0.3d, 0.4d };
        var y = new[] { 5d, 1d, 4d, 2d };
        var w = new[] { 2d, 1d, 1d, 2d };
        var xr = new[] { 0.1d, 0.1d, 0.2d, 0.3d, 0.4d, 0.4d };
        var yr = new[] { 5d, 5d, 1d, 4d, 2d, 2d };

        // Act
        var weighted = ValueOfInformationEstimator.MainEffect(x, y, w, 2);
        var replicated = ValueOfInformationEstimator.MainEffect(xr, yr, null, 2);

        // Assert
        Assert.AreEqual(replicated.ResolvableVariance, weighted.ResolvableVariance, 1e-15);
        Assert.AreEqual(replicated.TotalVariance, weighted.TotalVariance, 1e-15);
    }

    /// <summary>
    /// Verifies pairwise NaN filtering: rows with a NaN on either side are excluded and the
    /// estimate equals the pre-filtered sample's.
    /// </summary>
    [TestMethod]
    public void Test_MainEffect_NaNPairs_FilteredPairwise()
    {
        // Arrange
        var x = new[] { 0.1d, double.NaN, 0.2d, 0.3d, 0.4d };
        var y = new[] { 1d, 99d, 3d, double.NaN, 7d };
        var xClean = new[] { 0.1d, 0.2d, 0.4d };
        var yClean = new[] { 1d, 3d, 7d };

        // Act
        var filtered = ValueOfInformationEstimator.MainEffect(x, y, null, 3);
        var clean = ValueOfInformationEstimator.MainEffect(xClean, yClean, null, 3);

        // Assert
        Assert.AreEqual(3, filtered.Pairs);
        Assert.AreEqual(clean.ResolvableVariance, filtered.ResolvableVariance, 0d);
        Assert.AreEqual(clean.TotalVariance, filtered.TotalVariance, 0d);
    }

    /// <summary>
    /// Verifies the too-few-pairs guard: fewer valid pairs than bins reports a NaN resolvable
    /// variance while the total stays real.
    /// </summary>
    [TestMethod]
    public void Test_MainEffect_FewerPairsThanBins_NaNResolvable()
    {
        // Arrange / Act
        var effect = ValueOfInformationEstimator.MainEffect(new[] { 0.1d, 0.2d }, new[] { 1d, 2d }, null, 3);

        // Assert
        Assert.IsTrue(double.IsNaN(effect.ResolvableVariance));
        Assert.AreEqual(0.25d, effect.TotalVariance, 1e-15);
        Assert.AreEqual(2, effect.Pairs);
    }

    /// <summary>
    /// Verifies deterministic tie handling and the constant-column convention: a partially tied
    /// input column sorts its ties by realization index (two calls produce identical results),
    /// while a bit-constant column — a pinned function's draws — resolves exactly zero variance
    /// rather than the arbitrary-partition noise floor its tie order would otherwise report.
    /// </summary>
    [TestMethod]
    public void Test_MainEffect_TiedInputs_Deterministic()
    {
        // Arrange — three tied values whose order is the realization index, plus one above.
        var x = new[] { 0.5d, 0.5d, 0.5d, 0.7d };
        var y = new[] { 1d, 3d, 5d, 7d };

        // Act
        var first = ValueOfInformationEstimator.MainEffect(x, y, null, 2);
        var second = ValueOfInformationEstimator.MainEffect(x, y, null, 2);

        // Assert — ties break by index, so the bins are {1,3} and {5,7} exactly.
        Assert.AreEqual(4d, first.ResolvableVariance, 1e-15);
        Assert.AreEqual(first.ResolvableVariance, second.ResolvableVariance, 0d);
        Assert.AreEqual(first.TotalVariance, second.TotalVariance, 0d);

        // A fully constant column resolves exactly zero, with the total variance intact.
        var constant = ValueOfInformationEstimator.MainEffect(new[] { 0.5d, 0.5d, 0.5d, 0.5d }, y, null, 2);
        Assert.AreEqual(0d, constant.ResolvableVariance, 0d);
        Assert.AreEqual(first.TotalVariance, constant.TotalVariance, 0d);
        Assert.AreEqual(0d, ValueOfInformationEstimator.ExceedanceMovement(new[] { 0.5d, 0.5d, 0.5d, 0.5d }, y, null, 2, 4d), 0d);
    }

    /// <summary>
    /// Verifies the argument guards: null samples, mismatched lengths, mismatched weights, and
    /// an out-of-range bin count all throw.
    /// </summary>
    [TestMethod]
    public void Test_MainEffect_ArgumentGuards_Throw()
    {
        // Arrange
        var x = new[] { 0.1d, 0.2d };
        var y = new[] { 1d, 2d };

        // Act / Assert
        Assert.ThrowsException<ArgumentNullException>(() => ValueOfInformationEstimator.MainEffect(null!, y, null, 2));
        Assert.ThrowsException<ArgumentNullException>(() => ValueOfInformationEstimator.MainEffect(x, null!, null, 2));
        Assert.ThrowsException<ArgumentException>(() => ValueOfInformationEstimator.MainEffect(x, new[] { 1d }, null, 2));
        Assert.ThrowsException<ArgumentException>(() => ValueOfInformationEstimator.MainEffect(x, y, new[] { 1d }, 2));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => ValueOfInformationEstimator.MainEffect(x, y, null, 1));
    }

    /// <summary>
    /// Verifies the exceedance-movement hand case: a perfectly separating input moves the
    /// confidence statement by the full 2·p·(1−p) ceiling.
    /// </summary>
    [TestMethod]
    public void Test_ExceedanceMovement_SeparatingInput_HitsCeiling()
    {
        // Arrange — below-threshold outputs in the low bin, above in the high bin; p = 0.5.
        var x = new[] { 0.1d, 0.2d, 0.3d, 0.4d };
        var y = new[] { 1d, 3d, 5d, 7d };

        // Act
        double movement = ValueOfInformationEstimator.ExceedanceMovement(x, y, null, 2, 4d);

        // Assert — bins report 0 and 1 against the baseline 0.5: movement 0.5 = 2·0.5·0.5.
        Assert.AreEqual(0.5d, movement, 1e-15);
    }

    /// <summary>
    /// Verifies an uninformative input moves the confidence statement by exactly zero when
    /// every bin reproduces the baseline fraction.
    /// </summary>
    [TestMethod]
    public void Test_ExceedanceMovement_UninformativeInput_Zero()
    {
        // Arrange — each of the two bins holds one exceedance and one non-exceedance.
        var x = new[] { 0.1d, 0.2d, 0.3d, 0.4d };
        var y = new[] { 1d, 7d, 1d, 7d };

        // Act
        double movement = ValueOfInformationEstimator.ExceedanceMovement(x, y, null, 2, 4d);

        // Assert
        Assert.AreEqual(0d, movement, 0d);
    }

    /// <summary>
    /// Verifies the movement guard and weighting: fewer pairs than bins reports NaN, and
    /// weights shift the baseline and the per-bin fractions consistently.
    /// </summary>
    [TestMethod]
    public void Test_ExceedanceMovement_GuardAndWeights()
    {
        // Arrange
        var x = new[] { 0.1d, 0.2d, 0.3d, 0.4d };
        var y = new[] { 1d, 3d, 5d, 7d };
        var w = new[] { 3d, 1d, 1d, 3d };

        // Act
        double guarded = ValueOfInformationEstimator.ExceedanceMovement(new[] { 0.1d }, new[] { 1d }, null, 2, 0d);
        double weighted = ValueOfInformationEstimator.ExceedanceMovement(x, y, w, 2, 4d);

        // Assert — weighted baseline p = 4/8; bins carry weights 4 and 4 with fractions 0 and
        // 1: movement (4·0.5 + 4·0.5)/8 = 0.5.
        Assert.IsTrue(double.IsNaN(guarded));
        Assert.AreEqual(0.5d, weighted, 1e-15);
    }
}
