using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Tests.Results.Risk;

/// <summary>
/// Unit tests for <see cref="SystemRealization"/> — construction, the JSON results contract
/// (bit-faithful doubles, NaN/±Infinity literals, GZip compressed overloads), and the memory dump.
/// </summary>
[TestClass]
public class SystemRealizationTests
{
    /// <summary>Builds a small populated realization with gnarly doubles for round-trip tests.</summary>
    private static SystemRealization Build()
    {
        var component = new ComponentRealization(failureModes: 2) { Name = "Levee Reach A", MinH = 0.25d, MaxH = 31.5d };
        component.Curves.Fail.CreateCurve(new List<(double Mass, double Consequence)> { (0.1d + 0.2d, 1e-308), (0.05d, Math.PI) }, 200);
        var realization = new SystemRealization(new List<ComponentRealization> { component })
        {
            MinN = 0.1d + 0.2d,
            MaxN = Math.E,
            FunctionEvaluations = 1234d,
            StandardError = double.NaN,
            ChiSquared = double.PositiveInfinity,
        };
        realization.Curves.Total.CreateCurve(new List<(double Mass, double Consequence)> { (0.4d, 10d), (0.6d, 1d) }, 200);
        return realization;
    }

    /// <summary>Verifies the component constructor sizes the per-component hazard-extent slots.</summary>
    [TestMethod]
    public void Test_Constructor_SizesHazardExtents()
    {
        // Act
        var realization = new SystemRealization(new List<ComponentRealization> { new ComponentRealization(), new ComponentRealization() });

        // Assert
        Assert.AreEqual(2, realization.MinH.Count);
        Assert.AreEqual(double.MaxValue, realization.MinH[0]);
        Assert.AreEqual(double.MinValue, realization.MaxH[1]);
        Assert.ThrowsException<ArgumentNullException>(() => new SystemRealization(null!));
    }

    /// <summary>
    /// Verifies the JSON round trip is bit-faithful for finite doubles and preserves NaN and
    /// infinity as quoted literals.
    /// </summary>
    [TestMethod]
    public void Test_Json_RoundTrip_BitFaithful()
    {
        // Arrange
        var original = Build();

        // Act
        var restored = SystemRealization.FromJson(original.ToJson());

        // Assert — bit-level equality on the gnarly values.
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(original.MinN), BitConverter.DoubleToInt64Bits(restored.MinN));
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(Math.E), BitConverter.DoubleToInt64Bits(restored.MaxN));
        Assert.IsTrue(double.IsNaN(restored.StandardError));
        Assert.IsTrue(double.IsPositiveInfinity(restored.ChiSquared));
        Assert.AreEqual("Levee Reach A", restored.Components[0].Name);
        Assert.AreEqual(2, restored.Components[0].FailureModes.Count);
        Assert.AreEqual(
            BitConverter.DoubleToInt64Bits(original.Components[0].Curves.Fail.Mean),
            BitConverter.DoubleToInt64Bits(restored.Components[0].Curves.Fail.Mean));
        CollectionAssert.AreEqual(original.Curves.Total.LECConsequences, restored.Curves.Total.LECConsequences);
        CollectionAssert.AreEqual(original.Curves.Total.LECProbabilities, restored.Curves.Total.LECProbabilities);
    }

    /// <summary>
    /// Verifies the compressed round trip restores the identical content (compared decompressed —
    /// the GZip header itself is not content).
    /// </summary>
    [TestMethod]
    public void Test_CompressedBytes_RoundTrip()
    {
        // Arrange
        var original = Build();

        // Act
        byte[] bytes = original.ToCompressedBytes();
        var restored = SystemRealization.FromCompressedBytes(bytes);

        // Assert
        Assert.AreEqual(original.ToJson(), restored.ToJson(), "The decompressed content must be identical.");
        Assert.ThrowsException<ArgumentNullException>(() => SystemRealization.FromCompressedBytes(null!));
    }

    /// <summary>
    /// Verifies the multi-consequence surface: EnsureAdditionalCurves cascades the
    /// slots through the tree with fresh extent slots, and the additional curves, extents, and
    /// declared labels survive the JSON round trip bit-faithfully.
    /// </summary>
    [TestMethod]
    public void Test_AdditionalCurves_EnsureAndJsonRoundTrip()
    {
        // Arrange
        var realization = Build();
        realization.EnsureAdditionalCurves(2);
        realization.ConsequenceLabels.AddRange(new[] { "Life Loss", "Damages", "Environmental" });
        realization.ConsequenceUnits.AddRange(new[] { "lives", "$", "acres" });
        realization.AdditionalCurves[0].Total.CreateCurve(new List<(double Mass, double Consequence)> { (0.4d, 1000d), (0.6d, 10d) }, 200);
        realization.AdditionalMinN[0] = 10d;
        realization.AdditionalMaxN[0] = 1000d;

        // Assert — the cascade created the slots everywhere.
        Assert.AreEqual(2, realization.AdditionalCurves.Count);
        Assert.AreEqual(2, realization.Components[0].AdditionalCurves.Count);
        Assert.AreEqual(2, realization.Components[0].AdditionalMinN.Count);
        Assert.AreEqual(2, realization.Components[0].FailureModes[0].AdditionalCurves.Count);
        Assert.AreEqual(double.MaxValue, realization.Components[0].AdditionalMinN[1]);

        // Act — JSON round trip.
        var restored = SystemRealization.FromJson(realization.ToJson());

        // Assert
        Assert.AreEqual(2, restored.AdditionalCurves.Count);
        CollectionAssert.AreEqual(realization.AdditionalCurves[0].Total.LECConsequences, restored.AdditionalCurves[0].Total.LECConsequences);
        Assert.AreEqual(10d, restored.AdditionalMinN[0], 0d);
        Assert.AreEqual(1000d, restored.AdditionalMaxN[0], 0d);
        CollectionAssert.AreEqual(realization.ConsequenceLabels, restored.ConsequenceLabels);
        CollectionAssert.AreEqual(realization.ConsequenceUnits, restored.ConsequenceUnits);
        Assert.AreEqual(2, restored.Components[0].FailureModes[0].AdditionalCurves.Count);
    }

    /// <summary>Verifies DumpMemory clears every recorded risk point across the tree.</summary>
    [TestMethod]
    public void Test_DumpMemory_ClearsTree()
    {
        // Arrange
        var realization = Build();
        realization.Curves.Total.AddRiskPoint(1d, 0.5d, 0.1d, 10d);
        realization.Components[0].Curves.Fail.AddRiskPoint(1d, 0.5d, 0.1d, 10d);

        // Act
        realization.DumpMemory();

        // Assert
        Assert.AreEqual(0, realization.Curves.Total.RiskPoints.Count);
        Assert.AreEqual(0, realization.Components[0].Curves.Fail.RiskPoints.Count);
    }
}
