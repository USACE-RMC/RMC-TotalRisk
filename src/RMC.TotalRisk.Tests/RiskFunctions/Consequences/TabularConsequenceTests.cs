using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using Numerics.Functions;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.Tests.Core;
using RMC.TotalRisk.Core.Support;

namespace RMC.TotalRisk.Tests.RiskFunctions.Consequences;

/// <summary>
/// Unit tests for <see cref="TabularConsequence"/> — v1.0 defaults, validation matrix incl. the
/// legacy warnings, the negative-consequence clamp, sampling, serialization, and hash identity.
/// </summary>
[TestClass]
public class TabularConsequenceTests
{
    /// <summary>Builds a labeled deterministic damage table: (0→0), (100→1000).</summary>
    private static TabularConsequence DeterministicConsequence()
    {
        return new TabularConsequence
        {
            Name = "Damages",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(0d, new Deterministic(0d)), new UncertainOrdinate(100d, new Deterministic(1000d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Verifies the v1.0 default construction state.</summary>
    [TestMethod]
    public void Test_Defaults_MatchV10()
    {
        // Act
        var c = new TabularConsequence();

        // Assert
        Assert.AreEqual(Transform.None, c.HazardTransform);
        Assert.AreEqual(Transform.None, c.ConsequenceTransform);
        Assert.AreEqual(2, c.UncertainOrderedPairedData.Count);
        Assert.IsTrue(c.IsDeterministic);
        Assert.AreEqual(1, c.SamplingDimensions);
    }

    /// <summary>Verifies the validation matrix incl. both v1.0 warnings.</summary>
    [TestMethod]
    public void Test_Validate_Matrix()
    {
        // Arrange — valid table passes with no warnings (first ordinate is zero).
        var valid = DeterministicConsequence();
        var (ok, okMessages) = valid.Validate();
        Assert.IsTrue(ok);
        Assert.AreEqual(0, okMessages.Count);

        // Non-zero first ordinate warns about flat extrapolation below the table.
        var nonZero = DeterministicConsequence();
        nonZero.UncertainOrderedPairedData = new UncertainOrderedPairedData(
            new[] { new UncertainOrdinate(0d, new Deterministic(5d)), new UncertainOrdinate(100d, new Deterministic(1000d)) },
            true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic);
        var (stillValid, warnMessages) = nonZero.Validate();
        Assert.IsTrue(stillValid, "Warnings must not invalidate.");
        Assert.IsTrue(warnMessages.Any(m => m.StartsWith("Warning:", StringComparison.Ordinal) && m.Contains("greater than zero")));

        // A negative sampled range warns about the zero clamp.
        var negative = DeterministicConsequence();
        negative.UncertainOrderedPairedData = new UncertainOrderedPairedData(
            new[] { new UncertainOrdinate(0d, new Normal(0d, 1d)), new UncertainOrdinate(100d, new Normal(1000d, 10d)) },
            true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Normal);
        var (negValid, negMessages) = negative.Validate();
        Assert.IsTrue(negValid);
        Assert.IsTrue(negMessages.Any(m => m.Contains("Negative consequence values will be set to zero")));

        // Missing labels are errors (4 label errors).
        var unlabeled = new TabularConsequence();
        var (isValid, messages) = unlabeled.Validate();
        Assert.IsFalse(isValid);
        Assert.AreEqual(4, messages.Count(m => m.StartsWith("Error:", StringComparison.Ordinal)));
    }

    /// <summary>Verifies mean sampling, interpolation, extrapolation clamps, and the negative clamp.</summary>
    [TestMethod]
    public void Test_SampleFunction_KnownPoints_AndNegativeClamp()
    {
        // Arrange
        var c = DeterministicConsequence();

        // Act
        var f = c.SampleFunction();

        // Assert — exact interpolation and clamps.
        Assert.AreEqual(500d, f.Function(50d), 0d);
        Assert.AreEqual(0d, f.Function(-5d), 0d);
        Assert.AreEqual(1000d, f.Function(500d), 0d);

        // The sampled function clamps negative consequences to zero (AllowNegativeYValues=false):
        // a low percentile of a Normal(0, 1) first ordinate is negative → evaluates to 0.
        var uncertain = DeterministicConsequence();
        uncertain.UncertainOrderedPairedData = new UncertainOrderedPairedData(
            new[] { new UncertainOrdinate(0d, new Normal(0d, 1d)), new UncertainOrdinate(100d, new Normal(1000d, 10d)) },
            true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Normal);
        var low = uncertain.SampleFunction(0.01d);
        Assert.AreEqual(0d, low.Function(0d), 0d, "Negative sampled consequences must clamp to zero.");
    }

    /// <summary>Verifies invalid tables throw on sampling.</summary>
    [TestMethod]
    public void Test_SampleFunction_InvalidTable_Throws()
    {
        // Arrange
        var c = DeterministicConsequence();
        c.UncertainOrderedPairedData = new UncertainOrderedPairedData(
            new[] { new UncertainOrdinate(0d, new Deterministic(0d)) },
            true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic);

        // Act / Assert
        Assert.ThrowsException<InvalidOperationException>(() => c.SampleFunction());
    }

    /// <summary>Verifies bounds and the exact uncertainty summary.</summary>
    [TestMethod]
    public void Test_Bounds_And_UncertaintySummary()
    {
        // Arrange
        var c = DeterministicConsequence();
        c.UncertainOrderedPairedData = new UncertainOrderedPairedData(
            new[] { new UncertainOrdinate(0d, new Normal(100d, 10d)), new UncertainOrdinate(100d, new Normal(1000d, 10d)) },
            true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Normal);

        // Act
        var summary = c.ComputeUncertaintyResults(0.90d)!;

        // Assert
        Assert.AreEqual(0d, c.MinHazard(), 0d);
        Assert.AreEqual(100d, c.MaxHazard(), 0d);
        Assert.AreEqual(100d, summary.MeanCurve![0], 0d);
        Assert.AreEqual(new Normal(100d, 10d).InverseCDF(0.05d), summary.ConfidenceIntervals![0, 0], 0d);
        Assert.AreEqual(new Normal(1000d, 10d).InverseCDF(0.95d), summary.ConfidenceIntervals[1, 1], 0d);
    }

    /// <summary>Verifies the XElement round-trip preserves state and hash.</summary>
    [TestMethod]
    public void Test_Serialization_RoundTrip()
    {
        // Arrange
        var original = DeterministicConsequence();
        original.ConsequenceTransform = Transform.Logarithmic;

        // Act
        var restored = new TabularConsequence(original.ToXElement());

        // Assert
        Assert.AreEqual(original.SpecifiedConsequence, restored.SpecifiedConsequence);
        Assert.AreEqual(original.ConsequenceUnit, restored.ConsequenceUnit);
        Assert.AreEqual(original.ConsequenceTransform, restored.ConsequenceTransform);
        Assert.AreEqual(original.UncertainOrderedPairedData[1].Y!.Mean, restored.UncertainOrderedPairedData[1].Y!.Mean, 0d);
        CollectionAssert.AreEqual(original.CanonicalHash(), restored.CanonicalHash());
    }

    /// <summary>Verifies hash identity: metadata inert, table edits compute-relevant.</summary>
    [TestMethod]
    public void Test_CanonicalHash_Identity()
    {
        // Arrange
        var c = DeterministicConsequence();
        c.SpecifiedConsequence = "Life Loss";
        byte[] baseline = c.CanonicalHash();

        // Act / Assert — consequence labels are metadata (stripped).
        c.ConsequenceUnit = "lives";
        CollectionAssert.AreEqual(baseline, c.CanonicalHash());
        HashInvariance.AssertMetadataInvariant(c);
        HashInvariance.AssertStrippedAttributesInert(c.ToXElement());
        HashInvariance.AssertComputeSensitive(c.CanonicalHash, () => c.HazardTransform = Transform.Logarithmic);
        HashInvariance.AssertComputeSensitive(c.CanonicalHash, () => c.Extrapolation = ExtrapolationPolicy.Both);
    }

    /// <summary>
    /// Verifies the extrapolation policy's serialization contract — ABSENT when default, with the
    /// explicit-None assignment byte-identical (the hash-preservation pin), present by name and
    /// hash-moving when configured, round-tripping faithfully — and its wiring: the sampled
    /// wrappers carry the mapped sides, extend with the zero clamp still binding, and the Error
    /// mode guards forward evaluation with the full diagnostic.
    /// </summary>
    [TestMethod]
    public void Test_Extrapolation_ConditionalPresence_Wiring_AndErrorGuard()
    {
        // Arrange — the deterministic (0→0), (100→1000) table; slope 10 on both boundary segments.
        var c = DeterministicConsequence();
        var baselineXml = c.ToXElement().ToString();
        byte[] baselineHash = c.CanonicalHash();
        Assert.IsNull(c.ToXElement().Attribute(nameof(TabularConsequence.Extrapolation)));

        // The explicit-None assignment is byte-inert.
        c.Extrapolation = ExtrapolationPolicy.Both;
        c.Extrapolation = ExtrapolationPolicy.None;
        Assert.AreEqual(baselineXml, c.ToXElement().ToString());
        CollectionAssert.AreEqual(baselineHash, c.CanonicalHash());

        // Default sampling holds the endpoints (the raw wrapper, no guard).
        var held = c.SampleFunction();
        Assert.IsInstanceOfType(held, typeof(TabularFunction));
        Assert.AreEqual(1000d, held.Function(200d));

        // A configured policy serializes by name, moves the hash, and round-trips faithfully.
        c.Extrapolation = ExtrapolationPolicy.Both;
        var xml = c.ToXElement();
        Assert.AreEqual(nameof(ExtrapolationPolicy.Both), xml.Attribute(nameof(TabularConsequence.Extrapolation))?.Value);
        Assert.IsFalse(c.CanonicalHash().SequenceEqual(baselineHash),
            "The extrapolation policy is compute content and must move the canonical hash.");
        var restored = new TabularConsequence(xml);
        Assert.AreEqual(ExtrapolationPolicy.Both, restored.Extrapolation);
        CollectionAssert.AreEqual(c.CanonicalHash(), restored.CanonicalHash());
        Assert.AreEqual(xml.ToString(), restored.ToXElement().ToString());

        // Wiring: mean and percentile products carry the mapped sides, extend above, and the
        // negative-consequence zero clamp still binds on the extended lower tail.
        var mean = (TabularFunction)c.SampleFunction();
        var percentile = (TabularFunction)c.SampleFunction(0.5d);
        Assert.AreEqual(ExtrapolationSides.Both, mean.Extrapolation);
        Assert.AreEqual(ExtrapolationSides.Both, percentile.Extrapolation);
        Assert.AreEqual(2000d, mean.Function(200d), 1E-12);
        Assert.AreEqual(0d, mean.Function(-50d));

        // Error mode: the guarded product refuses out-of-range forward evaluation loudly and
        // keeps in-range evaluation.
        c.Extrapolation = ExtrapolationPolicy.Error;
        var guarded = c.SampleFunction();
        Assert.IsInstanceOfType(guarded, typeof(RangeGuardedUnivariateFunction));
        Assert.AreEqual(500d, guarded.Function(50d), 1E-12);
        var fault = Assert.ThrowsException<ExtrapolationRangeException>(() => guarded.Function(150d));
        StringAssert.Contains(fault.Message, "Damages");
        StringAssert.Contains(fault.Message, "Stage (ft)");
        Assert.AreEqual(0d, fault.RangeMinimum, 0d);
        Assert.AreEqual(100d, fault.RangeMaximum, 0d);
        Assert.IsNotNull(EvaluationFaultScope.Consume());
    }
}
