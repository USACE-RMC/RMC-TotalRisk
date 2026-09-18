using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.Tests.Core;
using RMC.TotalRisk.Core.Support;

namespace RMC.TotalRisk.Tests.RiskFunctions.Hazards;

/// <summary>
/// Unit tests for <see cref="TabularHazard"/> — the three uncertainty modes: defaults, mode-aware
/// validation, mean and percentile sampling at known points, bounds, serialization, and hash identity.
/// </summary>
[TestClass]
public class TabularHazardTests
{
    /// <summary>Builds a labeled tabular hazard on the v1.0 default tables.</summary>
    private static TabularHazard LabeledHazard(FunctionUncertainty mode = FunctionUncertainty.None)
    {
        return new TabularHazard
        {
            Name = "Stage Frequency",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertaintyValue = mode,
        };
    }

    /// <summary>Verifies the v1.0 default construction state: tables, transforms, and mode.</summary>
    [TestMethod]
    public void Test_Defaults_MatchV10()
    {
        // Act
        var h = new TabularHazard();

        // Assert
        Assert.AreEqual(FunctionUncertainty.None, h.UncertaintyValue);
        Assert.AreEqual(Transform.None, h.HazardTransform);
        Assert.AreEqual(Transform.NormalZ, h.ProbabilityTransform);
        Assert.IsTrue(h.IsDeterministic);
        Assert.AreEqual(1, h.SamplingDimensions);
        // Default tables: (0.999→1), (0.001→100) deterministic; PERT variants for the modes.
        Assert.AreEqual(2, h.NoUncertaintyFunction.Count);
        Assert.AreEqual(0.999d, h.NoUncertaintyFunction[0].X, 0d);
        Assert.AreSame(h.NoUncertaintyFunction, h.TargetFunction);
        h.UncertaintyValue = FunctionUncertainty.Hazard;
        Assert.AreSame(h.HazardUncertainFunction, h.TargetFunction);
        Assert.IsFalse(h.IsDeterministic);
        h.UncertaintyValue = FunctionUncertainty.Probability;
        Assert.AreSame(h.ProbabilityUncertainFunction, h.TargetFunction);
    }

    /// <summary>Verifies mode-aware probability-bounds validation (X axis for None/Hazard, Y for Probability).</summary>
    [TestMethod]
    public void Test_Validate_ModeAwareProbabilityBounds()
    {
        // Arrange — defaults are valid in every mode once labeled.
        Assert.IsTrue(LabeledHazard(FunctionUncertainty.None).Validate().IsValid);
        Assert.IsTrue(LabeledHazard(FunctionUncertainty.Hazard).Validate().IsValid);
        Assert.IsTrue(LabeledHazard(FunctionUncertainty.Probability).Validate().IsValid);

        // None mode: an ordinate X (exceedance probability) above 1 is an error.
        var badNone = LabeledHazard(FunctionUncertainty.None);
        badNone.NoUncertaintyFunction = new UncertainOrderedPairedData(
            new[] { new UncertainOrdinate(1.5d, new Deterministic(1d)), new UncertainOrdinate(0.001d, new Deterministic(100d)) },
            true, SortOrder.Descending, true, SortOrder.Ascending, UnivariateDistributionType.Deterministic);
        var (noneValid, noneMessages) = badNone.Validate();
        Assert.IsFalse(noneValid);
        Assert.IsTrue(noneMessages.Any(m => m.Contains("less than or equal to 1")));

        // Probability mode: an ordinate distribution exceeding 1 is an error.
        var badProb = LabeledHazard(FunctionUncertainty.Probability);
        badProb.ProbabilityUncertainFunction = new UncertainOrderedPairedData(
            new[] { new UncertainOrdinate(1d, new Normal(0.9d, 0.2d)), new UncertainOrdinate(100d, new Normal(0.0005d, 0.0001d)) },
            true, SortOrder.Ascending, true, SortOrder.Descending, UnivariateDistributionType.Normal);
        Assert.IsFalse(badProb.Validate().IsValid);

        // Missing labels are errors.
        var unlabeled = new TabularHazard();
        Assert.IsFalse(unlabeled.Validate().IsValid);
    }

    /// <summary>Verifies None-mode sampling: the inverted deterministic curve at exact points.</summary>
    [TestMethod]
    public void Test_SampleFunction_NoneMode_KnownPoints()
    {
        // Arrange — default table: exceedance 0.999 → hazard 1; exceedance 0.001 → hazard 100.
        var h = LabeledHazard();

        // Act
        var mean = h.SampleFunction();

        // Assert — inverted to hazard vs. non-exceedance: F⁻¹(0.001) = 1, F⁻¹(0.999) = 100.
        Assert.AreEqual(1d, mean.InverseCDF(0.001d), 1e-9);
        Assert.AreEqual(100d, mean.InverseCDF(0.999d), 1e-9);
        // Percentile sampling of a deterministic table returns the same curve.
        Assert.AreEqual(mean.InverseCDF(0.5d), h.SampleFunction(0.25d).InverseCDF(0.5d), 0d);
    }

    /// <summary>Verifies Hazard-mode percentile sampling: co-monotonic ordinate percentiles, inverted.</summary>
    [TestMethod]
    public void Test_SampleFunction_HazardMode_PercentileCurve()
    {
        // Arrange — default PERT tables: (0.999 → Pert(1,1,1)), (0.001 → Pert(90,100,110)).
        var h = LabeledHazard(FunctionUncertainty.Hazard);

        // Act — the 95th-percentile curve evaluates each ordinate's PERT at 0.95.
        var p95 = h.SampleFunction(0.95d);

        // Assert
        double expectedUpper = new Pert(90d, 100d, 110d).InverseCDF(0.95d);
        Assert.AreEqual(expectedUpper, p95.InverseCDF(0.999d), 1e-9);
        Assert.AreEqual(1d, p95.InverseCDF(0.001d), 1e-9);
    }

    /// <summary>Verifies the Hazard-mode mean curve assembles the expected-probability curve.</summary>
    [TestMethod]
    public void Test_SampleFunction_HazardMode_MeanCurve()
    {
        // Arrange
        var h = LabeledHazard(FunctionUncertainty.Hazard);

        // Act — 200-quantile stratification over 10,000 plotting-position curves.
        var mean = h.SampleFunction();

        // Assert — the expected curve spans the full-uncertainty hazard range and is a valid
        // distribution; the low end sits at the deterministic Pert(1,1,1) ordinate.
        Assert.IsNotNull(mean);
        double low = mean.InverseCDF(0.001d);
        double high = mean.InverseCDF(0.999d);
        Assert.IsTrue(low >= 1d - 1e-6, $"Low quantile {low} below the table minimum.");
        Assert.IsTrue(high <= new Pert(90d, 100d, 110d).InverseCDF(1d - 0.00001d) + 1e-6, $"High quantile {high} above the full-uncertainty maximum.");
        Assert.IsTrue(high > low);
    }

    /// <summary>Verifies Probability-mode sampling: ordinate probability percentiles at fixed hazards.</summary>
    [TestMethod]
    public void Test_SampleFunction_ProbabilityMode_PercentileCurve()
    {
        // Arrange — default: hazard 1 → Pert(0.999,...), hazard 100 → Pert(0.0001, 0.0005, 0.005).
        var h = LabeledHazard(FunctionUncertainty.Probability);

        // Act
        var median = h.SampleFunction(0.5d);

        // Assert — at hazard 100 the sampled exceedance probability is the ordinate median, so the
        // non-exceedance CDF is its complement.
        double medianExceedance = new Pert(0.0001d, 0.0005d, 0.005d).InverseCDF(0.5d);
        Assert.AreEqual(1d - medianExceedance, median.CDF(100d), 1e-9);
    }

    /// <summary>
    /// Verifies the PERT-percentile-Z mean curve is bit-reproducible across repeated calls.
    /// </summary>
    /// <remarks>
    /// This ordinate mean is not analytic, so it is rebuilt by averaging 10,000 percentile curves —
    /// a reduction that must stay sequential, since the curve feeds every realization of an
    /// analysis. A guard against reintroducing a parallel reduction here; it compares raw bits,
    /// which a tolerance assert could not.
    /// </remarks>
    [TestMethod]
    public void Test_SampleFunction_PertPercentileZMean_IsBitReproducible()
    {
        // Arrange — a probability-uncertain table whose ordinates are PERT-percentile-Z, the one
        // configuration that takes the 10,000-curve averaging branch.
        var h = LabeledHazard(FunctionUncertainty.Probability);
        h.ProbabilityUncertainFunction = new UncertainOrderedPairedData(
            new[]
            {
                new UncertainOrdinate(1d, new PertPercentileZ(0.98d, 0.999d, 0.9999d)),
                new UncertainOrdinate(100d, new PertPercentileZ(0.0001d, 0.0005d, 0.005d)),
            },
            true, SortOrder.Ascending, true, SortOrder.Descending, UnivariateDistributionType.PertPercentileZ);

        // Act — the mean curve, twice.
        var first = (EmpiricalDistribution)h.SampleFunction();
        var second = (EmpiricalDistribution)h.SampleFunction();

        // Assert — identical ordinate count and bit-identical probabilities.
        Assert.AreEqual(first.ProbabilityValues.Count, second.ProbabilityValues.Count);
        for (int i = 0; i < first.ProbabilityValues.Count; i++)
        {
            Assert.AreEqual(
                BitConverter.DoubleToInt64Bits(first.ProbabilityValues[i]),
                BitConverter.DoubleToInt64Bits(second.ProbabilityValues[i]),
                $"The mean curve is not bit-reproducible at ordinate {i}.");
            Assert.AreEqual(first.XValues[i], second.XValues[i], 0d);
        }
    }

    /// <summary>Verifies the mode-switched hazard bounds, incl. the 1e-5 full-uncertainty probes.</summary>
    [TestMethod]
    public void Test_Bounds_ModeSwitched()
    {
        // None mode: first/last mean ordinate hazards.
        var none = LabeledHazard();
        Assert.AreEqual(1d, none.MinHazard(true), 0d);
        Assert.AreEqual(100d, none.MaxHazard(true), 0d);

        // Hazard mode: means when meanOnly; extreme percentiles under full uncertainty.
        var hazard = LabeledHazard(FunctionUncertainty.Hazard);
        Assert.AreEqual(new Pert(1d, 1d, 1d).Mean, hazard.MinHazard(true), 1e-12);
        Assert.AreEqual(new Pert(90d, 100d, 110d).Mean, hazard.MaxHazard(true), 1e-12);
        Assert.AreEqual(new Pert(90d, 100d, 110d).InverseCDF(1d - 0.00001d), hazard.MaxHazard(false), 1e-12);

        // Probability mode: first/last ordinate hazards.
        var prob = LabeledHazard(FunctionUncertainty.Probability);
        Assert.AreEqual(1d, prob.MinHazard(false), 0d);
        Assert.AreEqual(100d, prob.MaxHazard(false), 0d);
    }

    /// <summary>Verifies invalid states throw on sampling (v1.1 upgrade of the v1.0 null return).</summary>
    [TestMethod]
    public void Test_SampleFunction_Invalid_Throws()
    {
        // Arrange — a one-ordinate active table is unusable.
        var h = LabeledHazard();
        h.NoUncertaintyFunction = new UncertainOrderedPairedData(
            new[] { new UncertainOrdinate(0.999d, new Deterministic(1d)) },
            true, SortOrder.Descending, true, SortOrder.Ascending, UnivariateDistributionType.Deterministic);

        // Act / Assert
        Assert.ThrowsException<InvalidOperationException>(() => h.SampleFunction());
        Assert.ThrowsException<InvalidOperationException>(() => h.SampleFunction(0.5d));
    }

    /// <summary>Verifies the exact mode-aware uncertainty summary.</summary>
    [TestMethod]
    public void Test_ComputeUncertaintyResults_ActiveTable()
    {
        // Arrange
        var h = LabeledHazard(FunctionUncertainty.Hazard);

        // Act
        var summary = h.ComputeUncertaintyResults(0.90d)!;

        // Assert — index-aligned with the hazard-uncertain table: ordinate 1 is Pert(90,100,110).
        Assert.AreEqual(new Pert(90d, 100d, 110d).Mean, summary.MeanCurve![1], 1e-12);
        Assert.AreEqual(new Pert(90d, 100d, 110d).InverseCDF(0.05d), summary.ConfidenceIntervals![1, 0], 0d);
        Assert.AreEqual(new Pert(90d, 100d, 110d).InverseCDF(0.95d), summary.ConfidenceIntervals[1, 1], 0d);
    }

    /// <summary>Verifies the XElement round-trip restores all three tables and the mode.</summary>
    [TestMethod]
    public void Test_Serialization_RoundTrip()
    {
        // Arrange
        var original = LabeledHazard(FunctionUncertainty.Hazard);
        original.HazardTransform = Transform.Logarithmic;

        // Act
        var restored = new TabularHazard(original.ToXElement());

        // Assert
        Assert.AreEqual(FunctionUncertainty.Hazard, restored.UncertaintyValue);
        Assert.AreEqual(Transform.Logarithmic, restored.HazardTransform);
        Assert.AreEqual(Transform.NormalZ, restored.ProbabilityTransform);
        Assert.AreEqual(original.NoUncertaintyFunction.Count, restored.NoUncertaintyFunction.Count);
        Assert.AreEqual(original.HazardUncertainFunction[1].Y!.Mean, restored.HazardUncertainFunction[1].Y!.Mean, 0d);
        Assert.AreEqual(original.ProbabilityUncertainFunction[1].X, restored.ProbabilityUncertainFunction[1].X, 0d);
        CollectionAssert.AreEqual(original.CanonicalHash(), restored.CanonicalHash());
    }

    /// <summary>Verifies hash identity: metadata inert; the mode switch and table edits compute-relevant.</summary>
    [TestMethod]
    public void Test_CanonicalHash_Identity()
    {
        // Arrange
        var h = LabeledHazard();

        // Act / Assert
        HashInvariance.AssertMetadataInvariant(h);
        HashInvariance.AssertStrippedAttributesInert(h.ToXElement());
        HashInvariance.AssertComputeSensitive(h.CanonicalHash, () => h.UncertaintyValue = FunctionUncertainty.Hazard);
        HashInvariance.AssertComputeSensitive(h.CanonicalHash, () => h.Extrapolation = ExtrapolationPolicy.Both);
    }

    /// <summary>
    /// Verifies the extrapolation policy's serialization contract — ABSENT when default, with the
    /// explicit-None assignment byte-identical (the hash-preservation pin), present by name and
    /// hash-moving when configured, round-tripping faithfully — and its wiring: an extending
    /// policy widens the sampled curve's inverse tails (the engine's integration domain), while
    /// the Error mode guards hazard-axis queries and deliberately retains the inverse hold so an
    /// Error-mode hazard integrates over its table span exactly as under the default.
    /// </summary>
    [TestMethod]
    public void Test_Extrapolation_ConditionalPresence_Wiring_AndErrorGuard()
    {
        // Arrange — the v1.0 default deterministic table: hazard [1, 100].
        var h = LabeledHazard();
        var baselineXml = h.ToXElement().ToString();
        byte[] baselineHash = h.CanonicalHash();
        Assert.IsNull(h.ToXElement().Attribute(nameof(TabularHazard.Extrapolation)));

        // The explicit-None assignment is byte-inert.
        h.Extrapolation = ExtrapolationPolicy.Both;
        h.Extrapolation = ExtrapolationPolicy.None;
        Assert.AreEqual(baselineXml, h.ToXElement().ToString());
        CollectionAssert.AreEqual(baselineHash, h.CanonicalHash());

        // Default sampling holds the far-tail inverse at the table span.
        var held = (EmpiricalDistribution)h.SampleFunction();
        Assert.AreEqual(ExtrapolationSides.None, held.Extrapolation);
        Assert.AreEqual(1d, held.InverseCDF(1E-16));
        Assert.AreEqual(100d, held.InverseCDF(1d - 1E-16));

        // A configured policy serializes by name, moves the hash, and round-trips faithfully.
        h.Extrapolation = ExtrapolationPolicy.Both;
        var xml = h.ToXElement();
        Assert.AreEqual(nameof(ExtrapolationPolicy.Both), xml.Attribute(nameof(TabularHazard.Extrapolation))?.Value);
        Assert.IsFalse(h.CanonicalHash().SequenceEqual(baselineHash),
            "The extrapolation policy is compute content and must move the canonical hash.");
        var restored = new TabularHazard(xml);
        Assert.AreEqual(ExtrapolationPolicy.Both, restored.Extrapolation);
        CollectionAssert.AreEqual(h.CanonicalHash(), restored.CanonicalHash());
        Assert.AreEqual(xml.ToString(), restored.ToXElement().ToString());

        // Wiring: the sampled curve carries the mapped sides and widens its inverse tails — the
        // engine's integration-domain probes see the extension.
        var extended = (EmpiricalDistribution)h.SampleFunction();
        Assert.AreEqual(ExtrapolationSides.Both, extended.Extrapolation);
        double low = extended.InverseCDF(1E-16);
        double high = extended.InverseCDF(1d - 1E-16);
        Assert.IsFalse(double.IsNaN(low) || double.IsInfinity(low));
        Assert.IsFalse(double.IsNaN(high) || double.IsInfinity(high));
        Assert.IsTrue(low < 1d, "The extended lower tail must fall below the table span.");
        Assert.IsTrue(high > 100d, "The extended upper tail must rise above the table span.");

        // Error mode: hazard-axis queries beyond the sampled span throw loudly; the inverse
        // retains the hold, so the integration domain stays the table span.
        h.Extrapolation = ExtrapolationPolicy.Error;
        var guarded = h.SampleFunction();
        Assert.IsInstanceOfType(guarded, typeof(RangeGuardedUnivariateDistribution));
        Assert.AreEqual(1d, guarded.InverseCDF(1E-16));
        Assert.AreEqual(100d, guarded.InverseCDF(1d - 1E-16));
        double inRange = guarded.CDF(50d);
        Assert.IsFalse(double.IsNaN(inRange));
        var fault = Assert.ThrowsException<ExtrapolationRangeException>(() => guarded.CDF(200d));
        StringAssert.Contains(fault.Message, "Stage Frequency");
        StringAssert.Contains(fault.Message, "Stage (ft)");
        Assert.AreEqual(1d, fault.RangeMinimum, 0d);
        Assert.AreEqual(100d, fault.RangeMaximum, 0d);
        Assert.IsNotNull(EvaluationFaultScope.Consume());
    }
}
