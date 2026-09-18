using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Tests.Results.Risk;

/// <summary>
/// Unit tests for <see cref="Curves"/> — the five risk-type streams with their v1.0
/// exhaustiveness assignments, the enum-driven stream accessor, and the fan-out pipeline.
/// </summary>
[TestClass]
public class CurvesTests
{
    /// <summary>Verifies the v1.0 exhaustiveness assignments per stream.</summary>
    [TestMethod]
    public void Test_Defaults_ExhaustivenessPerStream()
    {
        // Act
        var curves = new Curves();

        // Assert — Fail/Excess/NonFail defective; Background/Total exhaustive.
        Assert.IsFalse(curves.Excess.IsExhaustive);
        Assert.IsFalse(curves.Fail.IsExhaustive);
        Assert.IsFalse(curves.NonFail.IsExhaustive);
        Assert.IsTrue(curves.Background.IsExhaustive);
        Assert.IsTrue(curves.Total.IsExhaustive);
    }

    /// <summary>
    /// The zero-ulp gate on the percentile post-processing kernel:
    /// <see cref="Curve.InterpolateLogLogDescending"/> must reproduce
    /// <c>OrderedPairedData.GetYFromX(x, Logarithmic, Logarithmic)</c> bit-for-bit — end
    /// clamps, exact ordinate hits, the 1e-16 log floor region, flat segments, and interior
    /// interpolation — with the monotone cursor matching fresh-cursor queries across a
    /// descending sweep.
    /// </summary>
    [TestMethod]
    public void Test_InterpolateLogLogDescending_ZeroUlp_VsOrderedPairedData()
    {
        // Arrange — an LEC-shaped curve: consequences strictly descending, exceedance
        // probabilities ascending with a flat run and floor-region values.
        var xs = new[] { 1000d, 400d, 150d, 149.99999d, 40d, 1.5d, 1e-14, 0d };
        var ys = new[] { 0d, 1e-18, 1e-9, 1e-9, 3.2e-4, 0.02d, 0.5d, 0.5d };
        var reference = new Numerics.Data.OrderedPairedData(xs, ys, true, Numerics.Data.SortOrder.Descending, false, Numerics.Data.SortOrder.Ascending);

        // A descending query sweep: clamps beyond both ends, exact ordinate hits, floor-region
        // values, and interior points.
        var queries = new[] { 2000d, 1000d, 999.999d, 400d, 200d, 150d, 149.995d, 100d, 40d, 3d, 1.5d, 1d, 1e-10, 1e-14, 1e-16, 0d };
        int cursor = 1;

        for (int i = 0; i < queries.Length; i++)
        {
            // Act — the monotone-cursor walk and a fresh-cursor query.
            double walked = Curve.InterpolateLogLogDescending(xs, ys, queries[i], ref cursor);
            int fresh = 1;
            double single = Curve.InterpolateLogLogDescending(xs, ys, queries[i], ref fresh);
            double expected = reference.GetYFromX(queries[i], Numerics.Data.Transform.Logarithmic, Numerics.Data.Transform.Logarithmic);

            // Assert — bit-for-bit.
            Assert.AreEqual(System.BitConverter.DoubleToInt64Bits(expected), System.BitConverter.DoubleToInt64Bits(walked),
                $"Walked query {queries[i]:R} diverged: {expected:R} vs {walked:R}.");
            Assert.AreEqual(System.BitConverter.DoubleToInt64Bits(expected), System.BitConverter.DoubleToInt64Bits(single),
                $"Fresh-cursor query {queries[i]:R} diverged: {expected:R} vs {single:R}.");
        }

        // Degenerate shapes: empty and single-point curves.
        int degenerate = 1;
        Assert.IsTrue(double.IsNaN(Curve.InterpolateLogLogDescending(System.Array.Empty<double>(), System.Array.Empty<double>(), 1d, ref degenerate)));
        Assert.AreEqual(0.25d, Curve.InterpolateLogLogDescending(new[] { 10d }, new[] { 0.25d }, 1d, ref degenerate), 0d);
    }

    /// <summary>
    /// Verifies the enum-driven stream accessor maps every <see cref="RiskType"/> member to its
    /// named stream and rejects undefined members.
    /// </summary>
    [TestMethod]
    public void Test_GetCurve_MapsEveryStream()
    {
        // Arrange
        var curves = new Curves();

        // Act / Assert
        Assert.AreSame(curves.Excess, curves.GetCurve(RiskType.Excess));
        Assert.AreSame(curves.Background, curves.GetCurve(RiskType.Background));
        Assert.AreSame(curves.Total, curves.GetCurve(RiskType.Total));
        Assert.AreSame(curves.Fail, curves.GetCurve(RiskType.Fail));
        Assert.AreSame(curves.NonFail, curves.GetCurve(RiskType.NonFail));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => curves.GetCurve((RiskType)99));
    }

    /// <summary>Verifies the fan-out pipeline builds every stream and the clone is deep.</summary>
    [TestMethod]
    public void Test_FanOut_AndDeepClone()
    {
        // Arrange — one pair set per stream through the shared construction.
        var curves = new Curves();
        var pairs = new List<(double Mass, double Consequence)> { (0.1d, 10d), (0.2d, 5d) };
        curves.Fail.CreateCurve(pairs, 200);
        curves.Total.CreateCurve(new List<(double Mass, double Consequence)> { (0.5d, 10d), (0.5d, 5d) }, 200);

        // Act
        var clone = curves.Clone();
        clone.Fail.LECConsequences[0] = -1d;
        curves.DumpMemory();

        // Assert
        Assert.AreEqual(curves.Fail.Mean, clone.Fail.Mean, 0d);
        Assert.AreNotEqual(-1d, curves.Fail.LECConsequences[0], "Clone must be deep.");
        Assert.AreEqual(7.5d, curves.Total.Mean, 1e-13);
    }
}
