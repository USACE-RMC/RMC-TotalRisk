using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Results.Risk;
using RMC.TotalRisk.Results.Support;

namespace RMC.TotalRisk.Tests.Results.Support;

/// <summary>
/// Tests for <see cref="QuadratureMassLedger"/> — the recorded-mass side of the loss exceedance
/// curve, where a lookup that silently returns zero would thin a curve without any gate noticing.
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// </remarks>
[TestClass]
public class QuadratureMassLedgerTests
{
    /// <summary>
    /// A sealed ledger returns each abscissa's weight and reports the abscissa as accepted.
    /// </summary>
    [TestMethod]
    public void Test_RecordAndSeal_ReturnsWeightPerAbscissa()
    {
        // Arrange
        var ledger = new QuadratureMassLedger();
        ledger.Record(0.75d, 0.3d, 10d);
        ledger.Record(0.25d, 0.2d, 20d);
        ledger.Record(0.50d, 0.5d, 30d);

        // Act
        ledger.Seal();

        // Assert
        Assert.AreEqual(3, ledger.NodeCount);
        Assert.AreEqual(3, ledger.DistinctAbscissaCount);
        Assert.AreEqual(1d, ledger.TotalWeight, 1e-15);
        Assert.AreEqual(0.2d, ledger.MassAt(0.25d), 1e-15);
        Assert.AreEqual(0.5d, ledger.MassAt(0.50d), 1e-15);
        Assert.AreEqual(0.3d, ledger.MassAt(0.75d), 1e-15);
    }

    /// <summary>
    /// Weights at a repeated abscissa are summed, not overwritten or double-counted as separate
    /// entries. A saturating hazard curve collapses several stratification bins onto one
    /// probability, which is where the engine meets this case.
    /// </summary>
    [TestMethod]
    public void Test_DuplicateAbscissas_SumTheirWeights()
    {
        // Arrange
        var ledger = new QuadratureMassLedger();
        ledger.Record(0.4d, 0.1d, 1d);
        ledger.Record(0.4d, 0.25d, 1d);
        ledger.Record(0.4d, 0.05d, 1d);
        ledger.Record(0.9d, 0.6d, 2d);

        // Act
        ledger.Seal();

        // Assert
        Assert.AreEqual(4, ledger.NodeCount);
        Assert.AreEqual(2, ledger.DistinctAbscissaCount);
        Assert.AreEqual(0.4d, ledger.MassAt(0.4d), 1e-15);
        Assert.AreEqual(1d, ledger.TotalWeight, 1e-15);
    }

    /// <summary>
    /// An accepted node carrying zero weight — the degenerate zero-width stratification bin — is
    /// distinguishable from an abscissa the refinement superseded. The compaction in
    /// <c>Curve.ApplyRecordedMass</c> counts the first as covered and drops the second, so
    /// collapsing the two would trip the coverage gate on every saturating hazard.
    /// </summary>
    [TestMethod]
    public void Test_TryGetMass_SeparatesAcceptedZeroFromSuperseded()
    {
        // Arrange
        var ledger = new QuadratureMassLedger();
        ledger.Record(0.3d, 0d, 5d);
        ledger.Record(0.6d, 1d, 5d);
        ledger.Seal();

        // Act
        bool acceptedZero = ledger.TryGetMass(0.3d, out double zeroMass);
        bool superseded = ledger.TryGetMass(0.45d, out double missingMass);

        // Assert
        Assert.IsTrue(acceptedZero);
        Assert.AreEqual(0d, zeroMass, 0d);
        Assert.IsFalse(superseded);
        Assert.AreEqual(0d, missingMass, 0d);
        Assert.AreEqual(0d, ledger.MassAt(0.45d), 0d);
    }

    /// <summary>
    /// The compensated total survives a long run of weights far smaller than the accumulated sum,
    /// which a naive accumulation loses. The domain-partition gate compares this total against the
    /// bin widths at 1e-9 relative, a threshold a drifting sum would not clear.
    /// </summary>
    [TestMethod]
    public void Test_TotalWeight_IsCompensated()
    {
        // Arrange
        const int nodes = 1_000_000;
        double weight = 1d / nodes;
        var ledger = new QuadratureMassLedger(nodes);
        double naive = 0d;
        for (int i = 0; i < nodes; i++)
        {
            ledger.Record(i / (double)nodes, weight, 1d);
            naive += weight;
        }

        // Act
        ledger.Seal();

        // Assert
        Assert.AreEqual(1d, ledger.TotalWeight, 1e-15);
        Assert.IsTrue(Math.Abs(ledger.TotalWeight - 1d) <= Math.Abs(naive - 1d),
            $"The compensated total ({ledger.TotalWeight:R}) must be no further from one than the naive sum ({naive:R}).");
    }

    /// <summary>
    /// The ledger grows past its initial capacity without losing nodes or weight.
    /// </summary>
    [TestMethod]
    public void Test_Record_GrowsBeyondCapacity()
    {
        // Arrange
        var ledger = new QuadratureMassLedger(1);
        for (int i = 0; i < 500; i++) ledger.Record(i, 2d, 0d);

        // Act
        ledger.Seal();

        // Assert
        Assert.AreEqual(500, ledger.NodeCount);
        Assert.AreEqual(500, ledger.DistinctAbscissaCount);
        Assert.AreEqual(1000d, ledger.TotalWeight, 1e-12);
        Assert.AreEqual(2d, ledger.MassAt(499d), 1e-15);
    }

    /// <summary>
    /// An empty ledger seals to nothing rather than throwing, and reads as all-superseded.
    /// </summary>
    [TestMethod]
    public void Test_EmptyLedger_SealsToZero()
    {
        // Arrange
        var ledger = new QuadratureMassLedger();

        // Act
        ledger.Seal();

        // Assert
        Assert.AreEqual(0, ledger.NodeCount);
        Assert.AreEqual(0, ledger.DistinctAbscissaCount);
        Assert.AreEqual(0d, ledger.TotalWeight, 0d);
        Assert.IsFalse(ledger.TryGetMass(0.5d, out _));
    }

    /// <summary>
    /// Sealing twice is a no-op, so a caller cannot double-apply the compensation term.
    /// </summary>
    [TestMethod]
    public void Test_SealTwice_IsIdempotent()
    {
        // Arrange
        var ledger = new QuadratureMassLedger();
        for (int i = 1; i <= 100; i++) ledger.Record(i * 0.001d, 1e-14d * i, 0d);
        ledger.Seal();
        double sealedTotal = ledger.TotalWeight;

        // Act
        ledger.Seal();

        // Assert
        Assert.AreEqual(sealedTotal, ledger.TotalWeight, 0d);
        Assert.AreEqual(100, ledger.DistinctAbscissaCount);
    }

    /// <summary>
    /// Reading before sealing throws rather than returning zeros from an unsorted array — a silent
    /// wrong answer there would thin every curve in the run.
    /// </summary>
    [TestMethod]
    public void Test_ReadBeforeSeal_Throws()
    {
        // Arrange
        var ledger = new QuadratureMassLedger();
        ledger.Record(0.5d, 1d, 0d);

        // Act / Assert
        Assert.ThrowsException<InvalidOperationException>(() => { ledger.TryGetMass(0.5d, out _); });
        Assert.ThrowsException<InvalidOperationException>(() => { ledger.MassAt(0.5d); });
    }

    /// <summary>
    /// Recording after sealing throws, because the sorted array and the compensated total are both
    /// already final.
    /// </summary>
    [TestMethod]
    public void Test_RecordAfterSeal_Throws()
    {
        // Arrange
        var ledger = new QuadratureMassLedger();
        ledger.Record(0.5d, 1d, 0d);
        ledger.Seal();

        // Act / Assert
        Assert.ThrowsException<InvalidOperationException>(() => ledger.Record(0.6d, 1d, 0d));
    }

    /// <summary>
    /// Reproduces the integration appendix's K + 2 partition: five natural interior bins plus
    /// one endpoint rectangle at each tail. The edge correction makes the stored mass exactly
    /// one without changing an interior weight.
    /// </summary>
    [TestMethod]
    public void Test_SealExhaustive_AppendixDWorkedPartition_IsExactlyOne()
    {
        // Arrange: 0.001 + 5(0.1996) + 0.001 = 1.
        using var ledger = new QuadratureMassLedger(7);
        ledger.Record(0.001d, 0.001d, 30d);
        for (int i = 0; i < 5; i++)
        {
            ledger.Record(0.1008d + i * 0.1996d, 0.1996d, 25d - 5d * i);
        }
        double upperAbscissa = 0.999d;
        double upperMass = 1d - ledger.RunningTotalWeight;
        ledger.Record(upperAbscissa, upperMass, 0d);

        // Act
        ledger.SealExhaustive();

        // Assert
        Assert.AreEqual(1d, ledger.TotalWeight, 0d);
        Assert.AreEqual(7, ledger.DistinctAbscissaCount);
        Assert.AreEqual(0.001d, ledger.MassAt(0.001d), 0d);
        for (int i = 0; i < 5; i++)
        {
            Assert.AreEqual(0.1996d, ledger.MassAt(0.1008d + i * 0.1996d), 0d,
                "Sealing must not proportionally renormalize an interior AGK weight.");
        }
        Assert.AreEqual(0.001d, ledger.MassAt(upperAbscissa), 2e-16);
    }

    /// <summary>
    /// Non-finite coordinates and non-finite or negative probability weights fail at the
    /// recording boundary.
    /// </summary>
    [TestMethod]
    public void Test_Record_InvalidInputs_Throw()
    {
        using var ledger = new QuadratureMassLedger();
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => ledger.Record(double.NaN, 1d, 0d));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => ledger.Record(0.5d, double.PositiveInfinity, 0d));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => ledger.Record(0.5d, -double.Epsilon, 0d));
    }

    /// <summary>
    /// Disposing the ledger returns its buffers and prevents any subsequent recording or read.
    /// </summary>
    [TestMethod]
    public void Test_Dispose_ClosesLedger()
    {
        var ledger = new QuadratureMassLedger();
        ledger.Record(0.5d, 1d, 0d);
        ledger.Seal();
        ledger.Dispose();

        Assert.ThrowsException<ObjectDisposedException>(() => ledger.TryGetMass(0.5d, out _));
        Assert.ThrowsException<InvalidOperationException>(() => ledger.Record(0.5d, 1d, 0d));
    }
}
