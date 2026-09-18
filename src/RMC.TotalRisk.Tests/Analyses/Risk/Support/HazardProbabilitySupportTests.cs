using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Sampling;
using RMC.TotalRisk.Analyses.Risk.Support;

namespace RMC.TotalRisk.Tests.Analyses.Risk.Support;

/// <summary>
/// Tests for the shared natural-support validation and Appendix D endpoint completion rule.
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// </remarks>
[TestClass]
public class HazardProbabilitySupportTests
{
    /// <summary>
    /// Verifies two-sided, one-sided, saturated, coincident, and full-width supports complete to
    /// exactly one without evaluating a coincident endpoint twice.
    /// </summary>
    /// <param name="lower">The natural lower support probability.</param>
    /// <param name="upper">The natural upper support probability.</param>
    /// <param name="expectedEvaluations">The expected number of distinct endpoint evaluations.</param>
    /// <param name="expectedRecords">The expected number of endpoint mass records.</param>
    [DataTestMethod]
    [DataRow(0.001d, 0.999d, 2, 2)]
    [DataRow(0d, 0.8d, 1, 1)]
    [DataRow(0.2d, 1d, 1, 1)]
    [DataRow(0.5d, 0.5d, 1, 2)]
    [DataRow(0d, 0d, 1, 1)]
    [DataRow(1d, 1d, 1, 1)]
    [DataRow(0d, 1d, 0, 0)]
    public void Test_CompleteExhaustive_SupportMatrix(double lower, double upper,
        int expectedEvaluations, int expectedRecords)
    {
        var support = HazardProbabilitySupport.Create(new[] { new StratificationBin(lower, upper) });
        var records = new List<(double Probability, double Mass, double Value)>();
        int evaluatorCalls = 0;

        double Evaluate(double probability)
        {
            evaluatorCalls++;
            return 10d + probability;
        }

        int evaluations = support.CompleteExhaustive(support.InteriorMass, Evaluate,
            (probability, mass, value) => records.Add((probability, mass, value)), "test support");

        Assert.AreEqual(expectedEvaluations, evaluations);
        Assert.AreEqual(expectedEvaluations, evaluatorCalls);
        Assert.AreEqual(expectedRecords, records.Count);
        Assert.AreEqual(1d, support.InteriorMass + records.Sum(record => record.Mass), 0d);
        for (int i = 0; i < records.Count; i++)
        {
            Assert.AreEqual(10d + records[i].Probability, records[i].Value, 0d);
            Assert.IsTrue(records[i].Mass >= 0d && records[i].Mass <= 1d);
        }
    }

    /// <summary>
    /// Pins the report appendix's worked partition: lower 0.001, five natural interior masses of
    /// 0.1996, and the residual upper 0.001 collectively exhaust probability one.
    /// </summary>
    [TestMethod]
    public void Test_CompleteExhaustive_AppendixDWorkedPartition()
    {
        const double lower = 0.001d;
        const double interiorCell = 0.1996d;
        const double upper = 0.999d;
        var support = HazardProbabilitySupport.Create(new[] { new StratificationBin(lower, upper) });
        var endpointMasses = new List<double>();

        int evaluations = support.CompleteExhaustive(5d * interiorCell, probability => probability,
            (_, mass, _) => endpointMasses.Add(mass), "Appendix D fixture");

        Assert.AreEqual(2, evaluations);
        Assert.AreEqual(lower, endpointMasses[0], 0d);
        Assert.AreEqual(1d - (lower + 5d * interiorCell), endpointMasses[1], 0d);
        Assert.AreEqual(1d, lower + 5d * interiorCell + endpointMasses[1], 0d);
        Assert.AreEqual(1d - upper, endpointMasses[1], 1e-12);
    }

    /// <summary>
    /// Verifies invalid, reversed, overlapping, and non-finite probability supports fail closed.
    /// </summary>
    [TestMethod]
    public void Test_Create_InvalidSupportsThrow()
    {
        Assert.ThrowsException<InvalidOperationException>(
            () => HazardProbabilitySupport.Create(Array.Empty<StratificationBin>()));
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => HazardProbabilitySupport.Create(new[] { new StratificationBin(0.8d, 0.2d) }));
        Assert.ThrowsException<InvalidOperationException>(
            () => HazardProbabilitySupport.Create(new[] { new StratificationBin(double.NaN, 0.2d) }));
        Assert.ThrowsException<InvalidOperationException>(() => HazardProbabilitySupport.Create(new[]
        {
            new StratificationBin(0.1d, 0.6d),
            new StratificationBin(0.5d, 0.9d),
        }));
    }

    /// <summary>
    /// Verifies materially incomplete or non-finite interior mass cannot be hidden by endpoint
    /// completion.
    /// </summary>
    [TestMethod]
    public void Test_CompleteExhaustive_InvalidInteriorMassThrows()
    {
        var support = HazardProbabilitySupport.Create(new[] { new StratificationBin(0.1d, 0.9d) });
        Assert.ThrowsException<InvalidOperationException>(
            () => support.CompleteExhaustive(0.7d, probability => probability, (_, _, _) => { }, "bad mass"));
        Assert.ThrowsException<InvalidOperationException>(
            () => support.CompleteExhaustive(double.NaN, probability => probability, (_, _, _) => { }, "bad mass"));
    }
}
