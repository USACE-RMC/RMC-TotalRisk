using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Results.Support;

namespace RMC.TotalRisk.Tests.Results.Support;

/// <summary>
/// Unit tests for <see cref="ConditionalColumnLedger"/> — the flush append, the global
/// (x, t, order) seal with cross-strip grouping and duplicate coalescing, the compensated
/// group masses, the normalized column fill with its exact unit-sum residual, and the guards.
/// </summary>
[TestClass]
public class ConditionalColumnLedgerTests
{
    /// <summary>
    /// Verifies sealing groups by exact primary abscissa across out-of-order flushes: the
    /// groups ascend in x, each group's nodes ascend in t, masses are the per-group weight
    /// sums, and the normalized fill restores an exact unit sum with the residual on the
    /// largest-mass node.
    /// </summary>
    [TestMethod]
    public void Test_Seal_GroupsAndNormalizes()
    {
        // Arrange — two columns interleaved out of order across two "regions".
        using var ledger = new ConditionalColumnLedger();
        ledger.Record(0.7d, 0.2d, 0.30d, 0d);
        ledger.Record(0.3d, 0.6d, 0.20d, 0d);
        ledger.Record(0.7d, 0.8d, 0.10d, 0d);
        ledger.Record(0.3d, 0.1d, 0.40d, 0d);

        // Act
        ledger.Seal();

        // Assert — two groups, ascending x; nodes ascending t within each.
        Assert.AreEqual(2, ledger.GroupCount);
        Assert.AreEqual(0.3d, ledger.GroupAbscissa(0), 0d);
        Assert.AreEqual(0.7d, ledger.GroupAbscissa(1), 0d);
        Assert.AreEqual(0.6d, ledger.GroupMass(0), 1e-15);
        Assert.AreEqual(0.4d, ledger.GroupMass(1), 1e-15);
        Assert.AreEqual(2, ledger.GroupNodeCount(0));
        Assert.AreEqual(2, ledger.MaxGroupNodeCount);

        var tNodes = new double[2];
        var weights = new double[2];
        int count = ledger.FillGroupNormalized(0, tNodes, weights);
        Assert.AreEqual(2, count);
        Assert.AreEqual(0.1d, tNodes[0], 0d);
        Assert.AreEqual(0.6d, tNodes[1], 0d);
        Assert.AreEqual(1d, weights[0] + weights[1], 0d, "The normalized column must sum to exactly one.");
        Assert.AreEqual(0.4d / 0.6d, weights[0], 1e-15);
    }

    /// <summary>Verifies exact-duplicate (x, t) nodes coalesce with summed weights.</summary>
    [TestMethod]
    public void Test_Seal_CoalescesDuplicateNodes()
    {
        // Arrange
        using var ledger = new ConditionalColumnLedger();
        ledger.Record(0.5d, 0.4d, 0.25d, 0d);
        ledger.Record(0.5d, 0.4d, 0.25d, 0d);
        ledger.Record(0.5d, 0.9d, 0.50d, 0d);

        // Act
        ledger.Seal();

        // Assert
        Assert.AreEqual(1, ledger.GroupCount);
        Assert.AreEqual(2, ledger.GroupNodeCount(0));
        Assert.AreEqual(1d, ledger.GroupMass(0), 1e-15);
        var tNodes = new double[2];
        var weights = new double[2];
        ledger.FillGroupNormalized(0, tNodes, weights);
        Assert.AreEqual(0.5d, weights[0], 1e-15);
    }

    /// <summary>
    /// The rounding-residual regression: a column whose greatest-t node carries a mass far
    /// below the normalization's rounding noise (the probit-edge class) must fill without
    /// throwing and stay non-negative — the exact unit-sum residual rides the largest-mass
    /// node, never an edge node.
    /// </summary>
    [TestMethod]
    public void Test_FillGroupNormalized_TailNodeStaysNonNegative()
    {
        // Arrange — a dominant node, a secondary node, and a 1e-18-mass tail node at the
        // greatest t.
        using var ledger = new ConditionalColumnLedger();
        ledger.Record(0.5d, 0.1d, 0.6d, 0d);
        ledger.Record(0.5d, 0.6d, 0.4d, 0d);
        ledger.Record(0.5d, 0.999d, 1e-18d, 0d);

        // Act
        ledger.Seal();
        var tNodes = new double[3];
        var weights = new double[3];
        int count = ledger.FillGroupNormalized(0, tNodes, weights);

        // Assert — every weight non-negative, the tail weight at its scale, the dominant node
        // carrying the residual, Σ exactly one.
        Assert.AreEqual(3, count);
        Assert.IsTrue(weights[0] >= 0d && weights[1] >= 0d && weights[2] >= 0d,
            "No normalized weight may be negative.");
        Assert.IsTrue(weights[2] < 1e-15, "The tail node keeps its tiny normalized weight.");
        Assert.AreEqual(0.6d, weights[0], 1e-12, "The largest-mass node carries the residual.");
        Assert.AreEqual(1d, weights[0] + weights[1] + weights[2], 1e-15);
    }

    /// <summary>Verifies determinism: two identical record sequences seal to identical state.</summary>
    [TestMethod]
    public void Test_Seal_Deterministic()
    {
        static double[] Run()
        {
            using var ledger = new ConditionalColumnLedger();
            for (int i = 0; i < 100; i++)
            {
                ledger.Record((i % 5) / 5d, (i % 7) / 7d + 0.01d, 0.01d + i * 1e-5, 0d);
            }
            ledger.Seal();
            var signature = new double[ledger.GroupCount * 2];
            for (int g = 0; g < ledger.GroupCount; g++)
            {
                signature[2 * g] = ledger.GroupAbscissa(g);
                signature[2 * g + 1] = ledger.GroupMass(g);
            }
            return signature;
        }

        CollectionAssert.AreEqual(Run(), Run());
    }

    /// <summary>Verifies the guards: reads before sealing, invalid records, short buffers, and disposal.</summary>
    [TestMethod]
    public void Test_Guards()
    {
        var ledger = new ConditionalColumnLedger();
        ledger.Record(0.5d, 0.5d, 1d, 0d);
        Assert.ThrowsException<InvalidOperationException>(() => ledger.GroupAbscissa(0));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => ledger.Record(double.PositiveInfinity, 0.5d, 1d, 0d));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => ledger.Record(0.5d, double.NaN, 1d, 0d));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => ledger.Record(0.5d, 0.5d, -1d, 0d));

        ledger.Seal();
        Assert.ThrowsException<InvalidOperationException>(() => ledger.Record(0.5d, 0.5d, 1d, 0d));
        Assert.ThrowsException<ArgumentNullException>(() => ledger.FillGroupNormalized(0, null!, new double[1]));
        Assert.ThrowsException<ArgumentException>(() => ledger.FillGroupNormalized(0, new double[0], new double[1]));

        ledger.Dispose();
        Assert.ThrowsException<ObjectDisposedException>(() => ledger.GroupAbscissa(0));
    }
}
