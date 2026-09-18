using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using Numerics.Functions;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.RiskFunctions.Transforms;
using RMC.TotalRisk.Tests.Core;
using RMC.TotalRisk.Core.Support;

namespace RMC.TotalRisk.Tests.RiskFunctions.Transforms;

/// <summary>
/// Unit tests for <see cref="TabularTransform"/> — v1.0 defaults, validation matrix, co-monotonic
/// sampling at known points, bounds, serialization round-trip, and hash identity.
/// </summary>
[TestClass]
public class TabularTransformTests
{
    /// <summary>Builds a deterministic flow-to-stage table: (0→0), (100→50).</summary>
    private static TabularTransform DeterministicTransform()
    {
        return new TabularTransform
        {
            Name = "Rating",
            SpecifiedHazard = "Flow",
            HazardUnit = "cfs",
            TransformedHazard = "Stage",
            TransformedHazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(0d, new Deterministic(0d)), new UncertainOrdinate(100d, new Deterministic(50d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds a Normal-uncertain table: (0→N(10,2)), (100→N(20,2)).</summary>
    private static TabularTransform UncertainTransform()
    {
        var t = DeterministicTransform();
        t.UncertainOrderedPairedData = new UncertainOrderedPairedData(
            new[] { new UncertainOrdinate(0d, new Normal(10d, 2d)), new UncertainOrdinate(100d, new Normal(20d, 2d)) },
            true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Normal);
        return t;
    }

    /// <summary>Verifies the v1.0 default construction state.</summary>
    [TestMethod]
    public void Test_Defaults_MatchV10()
    {
        // Act
        var t = new TabularTransform();

        // Assert
        Assert.AreEqual(Transform.None, t.HazardTransform);
        Assert.AreEqual(Transform.None, t.TransformTransform);
        Assert.AreEqual(2, t.UncertainOrderedPairedData.Count);
        Assert.AreEqual(0d, t.UncertainOrderedPairedData[0].X, 0d);
        Assert.AreEqual(1d, t.UncertainOrderedPairedData[1].X, 0d);
        Assert.IsTrue(t.IsDeterministic);
        Assert.AreEqual(1, t.SamplingDimensions);
    }

    /// <summary>Verifies the validation matrix: labels, ordinate count, and log-transform guards.</summary>
    [TestMethod]
    public void Test_Validate_Matrix()
    {
        // Arrange — valid configured transform passes.
        var valid = DeterministicTransform();
        Assert.IsTrue(valid.Validate().IsValid);

        // Missing labels are errors.
        var unlabeled = new TabularTransform();
        var (isValid, messages) = unlabeled.Validate();
        Assert.IsFalse(isValid);
        Assert.AreEqual(4, messages.Count(m => m.StartsWith("Error:", StringComparison.Ordinal)));

        // A logarithmic transformed axis over a negative range is an error.
        var negative = DeterministicTransform();
        negative.UncertainOrderedPairedData = new UncertainOrderedPairedData(
            new[] { new UncertainOrdinate(0d, new Deterministic(-5d)), new UncertainOrdinate(100d, new Deterministic(50d)) },
            true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic);
        negative.TransformTransform = Transform.Logarithmic;
        var (logValid, logMessages) = negative.Validate();
        Assert.IsFalse(logValid);
        Assert.IsTrue(logMessages.Any(m => m.Contains("transform interpolation transform cannot be logarithmic")));
    }

    /// <summary>Verifies mean sampling interpolates exactly, with flat extrapolation clamps.</summary>
    [TestMethod]
    public void Test_SampleFunction_Mean_KnownPoints()
    {
        // Arrange
        var t = DeterministicTransform();

        // Act
        var f = t.SampleFunction();

        // Assert — exact linear interpolation and endpoint clamps.
        Assert.AreEqual(25d, f.Function(50d), 0d);
        Assert.AreEqual(0d, f.Function(-10d), 0d);
        Assert.AreEqual(50d, f.Function(200d), 0d);
    }

    /// <summary>Verifies co-monotonic percentile sampling evaluates ordinates at the same percentile.</summary>
    [TestMethod]
    public void Test_SampleFunction_Percentile_CoMonotonic()
    {
        // Arrange
        var t = UncertainTransform();

        // Act — the median curve equals the per-ordinate medians (= means for Normal).
        var median = t.SampleFunction(0.5d);

        // Assert
        Assert.AreEqual(10d, median.Function(0d), 1e-12);
        Assert.AreEqual(20d, median.Function(100d), 1e-12);

        // A high percentile shifts every ordinate up by the same z-score.
        var p95 = t.SampleFunction(0.95d);
        double z95 = new Normal(0d, 1d).InverseCDF(0.95d);
        Assert.AreEqual(10d + 2d * z95, p95.Function(0d), 1e-9);
        Assert.AreEqual(20d + 2d * z95, p95.Function(100d), 1e-9);
    }

    /// <summary>Verifies invalid tables throw on sampling (v1.1 upgrade of the v1.0 null return).</summary>
    [TestMethod]
    public void Test_SampleFunction_InvalidTable_Throws()
    {
        // Arrange — a one-ordinate table is unusable.
        var t = DeterministicTransform();
        t.UncertainOrderedPairedData = new UncertainOrderedPairedData(
            new[] { new UncertainOrdinate(0d, new Deterministic(0d)) },
            true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic);

        // Act / Assert
        Assert.ThrowsException<InvalidOperationException>(() => t.SampleFunction());
        Assert.ThrowsException<InvalidOperationException>(() => t.SampleFunction(0.5d));
    }

    /// <summary>Verifies the realization-index overload reads the pre-allocated percentile row.</summary>
    [TestMethod]
    public void Test_SampleFunction_RealizationIndex_UsesSamplerRow()
    {
        // Arrange
        var t = UncertainTransform();
        t.SetupSampler(20, 12345, SamplingScheme.LatinHypercube);

        // Act / Assert — before setup, index sampling throws; after, rows are deterministic per seed.
        var first = t.SampleFunction(7).Function(0d);
        t.SetupSampler(20, 12345, SamplingScheme.LatinHypercube);
        Assert.AreEqual(first, t.SampleFunction(7).Function(0d), 0d);

        var fresh = UncertainTransform();
        Assert.ThrowsException<InvalidOperationException>(() => fresh.SampleFunction(0));
    }

    /// <summary>Verifies the v1.0 bounds surface.</summary>
    [TestMethod]
    public void Test_Bounds_MatchV10()
    {
        // Arrange
        var t = UncertainTransform();

        // Assert
        Assert.AreEqual(0d, t.MinHazard(), 0d);
        Assert.AreEqual(100d, t.MaxHazard(), 0d);
        Assert.AreEqual(10d, t.MinTransformedHazard(meanOnly: true), 1e-12);
        Assert.AreEqual(20d, t.MaxTransformedHazard(meanOnly: true), 1e-12);
        // Full-uncertainty bounds probe the 1e-5 / 1−1e-5 percentiles.
        Assert.AreEqual(new Normal(10d, 2d).InverseCDF(0.00001d), t.MinTransformedHazard(false), 1e-9);
        Assert.AreEqual(new Normal(20d, 2d).InverseCDF(1d - 0.00001d), t.MaxTransformedHazard(false), 1e-9);
    }

    /// <summary>Verifies the uncertainty summary is exact percentile evaluation (no tolerance).</summary>
    [TestMethod]
    public void Test_ComputeUncertaintyResults_ExactPercentiles()
    {
        // Arrange
        var t = UncertainTransform();

        // Act
        var summary = t.ComputeUncertaintyResults(0.90d)!;

        // Assert — index-aligned exact per-ordinate statistics.
        Assert.AreEqual(10d, summary.MeanCurve![0], 0d);
        Assert.AreEqual(20d, summary.MeanCurve[1], 0d);
        Assert.AreEqual(new Normal(10d, 2d).InverseCDF(0.5d), summary.ModeCurve![0], 0d);
        Assert.AreEqual(new Normal(10d, 2d).InverseCDF(0.05d), summary.ConfidenceIntervals![0, 0], 0d);
        Assert.AreEqual(new Normal(10d, 2d).InverseCDF(0.95d), summary.ConfidenceIntervals[0, 1], 0d);
        Assert.AreEqual(new Normal(20d, 2d).InverseCDF(0.95d), summary.ConfidenceIntervals[1, 1], 0d);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => t.ComputeUncertaintyResults(1.5d));
    }

    /// <summary>Verifies the XElement round-trip restores every serialized member.</summary>
    [TestMethod]
    public void Test_Serialization_RoundTrip()
    {
        // Arrange
        var original = UncertainTransform();
        original.Description = "Round trip";
        original.HazardTransform = Transform.Logarithmic;

        // Act
        var restored = new TabularTransform(original.ToXElement());

        // Assert
        Assert.AreEqual(original.Name, restored.Name);
        Assert.AreEqual(original.Description, restored.Description);
        Assert.AreEqual(original.SpecifiedHazard, restored.SpecifiedHazard);
        Assert.AreEqual(original.TransformedHazard, restored.TransformedHazard);
        Assert.AreEqual(original.HazardTransform, restored.HazardTransform);
        Assert.AreEqual(original.UncertainOrderedPairedData.Count, restored.UncertainOrderedPairedData.Count);
        Assert.AreEqual(original.UncertainOrderedPairedData[1].X, restored.UncertainOrderedPairedData[1].X, 0d);
        Assert.AreEqual(original.UncertainOrderedPairedData[1].Y!.Mean, restored.UncertainOrderedPairedData[1].Y!.Mean, 0d);
        CollectionAssert.AreEqual(original.CanonicalHash(), restored.CanonicalHash(), "Round-trip must preserve the canonical hash.");
    }

    /// <summary>Verifies hash identity: metadata inert, table edits compute-relevant.</summary>
    [TestMethod]
    public void Test_CanonicalHash_Identity()
    {
        // Arrange
        var t = UncertainTransform();

        // Act / Assert
        HashInvariance.AssertMetadataInvariant(t);
        HashInvariance.AssertStrippedAttributesInert(t.ToXElement());
        HashInvariance.AssertComputeSensitive(t.CanonicalHash, () => t.HazardTransform = Transform.Logarithmic);
        HashInvariance.AssertComputeSensitive(t.CanonicalHash, () => t.Extrapolation = ExtrapolationPolicy.Both);
    }

    /// <summary>
    /// Verifies the extrapolation policy's serialization contract — ABSENT when default, with the
    /// explicit-None assignment byte-identical (the hash-preservation pin), present by name and
    /// hash-moving when configured, round-tripping faithfully — and its wiring: the sampled
    /// wrappers carry the mapped sides and actually extend, and the Error mode guards forward
    /// evaluation with the full diagnostic while the inverse retains the hold.
    /// </summary>
    [TestMethod]
    public void Test_Extrapolation_ConditionalPresence_Wiring_AndErrorGuard()
    {
        // Arrange — the deterministic (0→0), (100→50) table; slope 0.5 on both boundary segments.
        var t = DeterministicTransform();
        var baselineXml = t.ToXElement().ToString();
        byte[] baselineHash = t.CanonicalHash();
        Assert.IsNull(t.ToXElement().Attribute(nameof(TabularTransform.Extrapolation)));

        // The explicit-None assignment is byte-inert.
        t.Extrapolation = ExtrapolationPolicy.Both;
        t.Extrapolation = ExtrapolationPolicy.None;
        Assert.AreEqual(baselineXml, t.ToXElement().ToString());
        CollectionAssert.AreEqual(baselineHash, t.CanonicalHash());

        // Default sampling holds the endpoints (the raw wrapper, no guard).
        var held = t.SampleFunction();
        Assert.IsInstanceOfType(held, typeof(TabularFunction));
        Assert.AreEqual(50d, held.Function(200d));

        // A configured policy serializes by name, moves the hash, and round-trips faithfully.
        t.Extrapolation = ExtrapolationPolicy.Both;
        var xml = t.ToXElement();
        Assert.AreEqual(nameof(ExtrapolationPolicy.Both), xml.Attribute(nameof(TabularTransform.Extrapolation))?.Value);
        Assert.IsFalse(t.CanonicalHash().SequenceEqual(baselineHash),
            "The extrapolation policy is compute content and must move the canonical hash.");
        var restored = new TabularTransform(xml);
        Assert.AreEqual(ExtrapolationPolicy.Both, restored.Extrapolation);
        CollectionAssert.AreEqual(t.CanonicalHash(), restored.CanonicalHash());
        Assert.AreEqual(xml.ToString(), restored.ToXElement().ToString());

        // Wiring: mean and percentile products carry the mapped sides and extend.
        var mean = (TabularFunction)t.SampleFunction();
        var percentile = (TabularFunction)t.SampleFunction(0.5d);
        Assert.AreEqual(ExtrapolationSides.Both, mean.Extrapolation);
        Assert.AreEqual(ExtrapolationSides.Both, percentile.Extrapolation);
        Assert.AreEqual(100d, mean.Function(200d), 1E-12);
        Assert.AreEqual(-50d, mean.Function(-100d), 1E-12);

        // Error mode: the guarded product refuses out-of-range forward evaluation loudly, keeps
        // in-range evaluation and the inverse hold, and maps the inner wrapper to None.
        t.Extrapolation = ExtrapolationPolicy.Error;
        var guarded = t.SampleFunction();
        Assert.IsInstanceOfType(guarded, typeof(RangeGuardedUnivariateFunction));
        Assert.AreEqual(25d, guarded.Function(50d), 1E-12);
        // The inverse is deliberately unguarded (forward-only rule); the transform table's Y
        // order is None, so inverse lookups on this fixture are unsupported upstream either way —
        // the probability-axis inverse hold is pinned on the response and hazard guards.
        var fault = Assert.ThrowsException<ExtrapolationRangeException>(() => guarded.Function(150d));
        StringAssert.Contains(fault.Message, "Rating");
        StringAssert.Contains(fault.Message, "Flow (cfs)");
        StringAssert.Contains(fault.Message, "150");
        Assert.AreEqual(0d, fault.RangeMinimum, 0d);
        Assert.AreEqual(100d, fault.RangeMaximum, 0d);
        Assert.IsNotNull(EvaluationFaultScope.Consume());
        Assert.IsNull(EvaluationFaultScope.Consume());
    }
}
