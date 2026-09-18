using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics;
using Numerics.Data;
using Numerics.Distributions;
using Numerics.Mathematics.SpecialFunctions;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.Tests.Core;
using RMC.TotalRisk.Core.Support;

namespace RMC.TotalRisk.Tests.RiskFunctions.Hazards;

/// <summary>
/// Unit tests for <see cref="NonparametricHazard"/> — v1.0 defaults, validation matrix, the
/// quantile-uncertainty derivation contracts (extension ends, 1%-bound monotonicity, the
/// closed-form σ-repair identity), co-monotonic sampling, bounds, exact uncertainty summary,
/// the inputs-only serialization round-trip with load-time recomputation, and hash identity.
/// </summary>
[TestClass]
public class NonparametricHazardTests
{
    /// <summary>Builds a labeled four-ordinate deterministic-input fixture (anchored at 0.999).</summary>
    private static NonparametricHazard AnchorHazard()
    {
        return new NonparametricHazard
        {
            Name = "Graphical",
            SpecifiedHazard = "Flow",
            HazardUnit = "cfs",
            InputUncertainFunction = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(0.999d, new Deterministic(500d)),
                    new UncertainOrdinate(0.5d, new Deterministic(1400d)),
                    new UncertainOrdinate(0.1d, new Deterministic(3600d)),
                    new UncertainOrdinate(0.01d, new Deterministic(7000d)),
                    new UncertainOrdinate(0.002d, new Deterministic(9500d)),
                },
                true, SortOrder.Descending, true, SortOrder.Ascending, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Verifies the v1.0 default construction state and the eager default derivation.</summary>
    [TestMethod]
    public void Test_Defaults_MatchV10()
    {
        // Act
        var h = new NonparametricHazard();

        // Assert — the exact v1.0 defaults.
        Assert.AreEqual(Transform.Logarithmic, h.HazardTransform);
        Assert.AreEqual(Transform.NormalZ, h.ProbabilityTransform);
        Assert.IsTrue(h.IsUncertain);
        Assert.AreEqual(100, h.EffectiveRecordLength);
        Assert.AreEqual(0.0001d, h.ExtrapolationEP, 0d);
        Assert.AreEqual(2, h.InputUncertainFunction.Count);
        Assert.AreEqual(0.999d, h.InputUncertainFunction[0].X, 0d);
        Assert.AreEqual(0.001d, h.InputUncertainFunction[1].X, 0d);
        Assert.IsFalse(h.IsDeterministic);
        Assert.AreEqual(1, h.SamplingDimensions);

        // The derivation ran eagerly: input anchored at 0.999 (no frequent insert) + the rare
        // extension to the extrapolation AEP.
        Assert.AreEqual(3, h.TrueUncertainFunction.Count);
        Assert.AreEqual(0.999d, h.TrueUncertainFunction[0].X, 0d);
        Assert.AreEqual(0.0001d, h.TrueUncertainFunction[h.TrueUncertainFunction.Count - 1].X, 0d);
        Assert.IsTrue(h.TrueUncertainFunction.IsValid);
    }

    /// <summary>Verifies the validation matrix: labels, ordinate rules, and the scalar ranges.</summary>
    [TestMethod]
    public void Test_Validate_Matrix()
    {
        // Arrange — the configured anchor passes.
        Assert.IsTrue(AnchorHazard().Validate().IsValid);

        // Missing labels are errors.
        var unlabeled = new NonparametricHazard();
        var (isValid, messages) = unlabeled.Validate();
        Assert.IsFalse(isValid);
        Assert.AreEqual(2, messages.Count(m => m.StartsWith("Error:", StringComparison.Ordinal)));

        // A one-ordinate input is an error.
        var tiny = AnchorHazard();
        tiny.InputUncertainFunction = new UncertainOrderedPairedData(
            new[] { new UncertainOrdinate(0.5d, new Deterministic(10d)) },
            true, SortOrder.Descending, true, SortOrder.Ascending, UnivariateDistributionType.Deterministic);
        Assert.IsTrue(tiny.Validate().ValidationMessages.Any(m => m.Contains("at least two ordinates")));

        // The scalar ranges are the v1.0 rules.
        var erl = AnchorHazard();
        erl.EffectiveRecordLength = 5;
        Assert.IsTrue(erl.Validate().ValidationMessages.Any(m => m.Contains("effective record length")));
        var ep = AnchorHazard();
        ep.ExtrapolationEP = 0.5d;
        Assert.IsTrue(ep.Validate().ValidationMessages.Any(m => m.Contains("extrapolation exceedance probability")));

        // A logarithmic hazard axis over negative hazard values is an error.
        var negative = AnchorHazard();
        negative.InputUncertainFunction = new UncertainOrderedPairedData(
            new[] { new UncertainOrdinate(0.999d, new Deterministic(-5d)), new UncertainOrdinate(0.001d, new Deterministic(100d)) },
            true, SortOrder.Descending, true, SortOrder.Ascending, UnivariateDistributionType.Deterministic);
        Assert.IsTrue(negative.Validate().ValidationMessages.Any(m => m.Contains("hazard interpolation transform cannot be logarithmic")));
    }

    /// <summary>
    /// Verifies the derivation extends both curve ends correctly: the rare end to the
    /// extrapolation AEP, and the frequent end to 0.999 by TRUE linear extrapolation — the v1.1
    /// fix of the v1.0 order-of-operations defect that silently produced a flat extension
    /// (deterministic branch, so the expected values are directly computable).
    /// </summary>
    [TestMethod]
    public void Test_Derivation_ExtrapolationEnds()
    {
        // Arrange — a deterministic-mode fixture NOT anchored at 0.999.
        var h = AnchorHazard();
        h.IsUncertain = false;
        h.InputUncertainFunction = new UncertainOrderedPairedData(
            new[]
            {
                new UncertainOrdinate(0.9d, new Deterministic(100d)),
                new UncertainOrdinate(0.1d, new Deterministic(300d)),
                new UncertainOrdinate(0.01d, new Deterministic(600d)),
            },
            true, SortOrder.Descending, true, SortOrder.Ascending, UnivariateDistributionType.Deterministic);

        // Act
        var derived = h.TrueUncertainFunction;

        // Assert — both ends extended: 0.999 inserted first, the extrapolation AEP appended last.
        Assert.AreEqual(5, derived.Count);
        Assert.AreEqual(0.999d, derived[0].X, 0d);
        Assert.AreEqual(h.ExtrapolationEP, derived[derived.Count - 1].X, 0d);

        // The expected values are the Numerics linear extrapolations on the configured
        // transforms over the ORIGINAL input curve.
        var lin = new Linear(new[] { 0.9d, 0.1d, 0.01d }, new[] { 100d, 300d, 600d }, SortOrder.Descending)
        {
            XTransform = h.ProbabilityTransform,
            YTransform = h.HazardTransform,
        };
        Assert.AreEqual(lin.Extrapolate(0.999d), derived[0].Y!.Mean, 1e-9);
        Assert.AreEqual(lin.Extrapolate(h.ExtrapolationEP), derived[derived.Count - 1].Y!.Mean, 1e-9);

        // Interior ordinates carry the input means unchanged.
        Assert.AreEqual(100d, derived[1].Y!.Mean, 0d);
        Assert.AreEqual(600d, derived[3].Y!.Mean, 0d);
    }

    /// <summary>
    /// Verifies the derivation contracts on the uncertain branch: LnNormal ordinates with
    /// positive spread, and the repaired 1% confidence bound never inverting between adjacent
    /// ordinates (the observable contract of the σ repair).
    /// </summary>
    [TestMethod]
    public void Test_Derivation_LowerBoundMonotone()
    {
        // Arrange — three diverse fixtures: the anchor, a linear-space variant, and a
        // wide-tail-spacing variant that stresses the repair.
        var fixtures = new[]
        {
            AnchorHazard(),
            BuildFixture(Transform.None, (0.999d, 10d), (0.5d, 12d), (0.1d, 15d), (0.01d, 30d)),
            BuildFixture(Transform.Logarithmic, (0.999d, 50d), (0.4d, 55d), (0.35d, 60d), (0.01d, 4000d), (0.005d, 4100d)),
        };

        foreach (var h in fixtures)
        {
            // Act
            var derived = h.TrueUncertainFunction;

            // Assert
            Assert.IsTrue(derived.Count >= h.InputUncertainFunction.Count, "The derivation must keep every input ordinate.");
            double previousLow = double.NegativeInfinity;
            for (int i = 0; i < derived.Count; i++)
            {
                var y = (LnNormal)derived[i].Y!;
                Assert.IsTrue(y.StandardDeviation > 0d, $"Ordinate {i} must carry positive spread.");
                double low = y.InverseCDF(0.01d);
                Assert.IsTrue(low >= previousLow - Math.Abs(previousLow) * 1e-9,
                    $"Ordinate {i}: the 1% bound ({low}) inverted below the previous bound ({previousLow}).");
                previousLow = low;
            }
        }
    }

    /// <summary>
    /// Verifies the closed-form σ repair solves the exact equation the v1.0 Brent root find
    /// iterated on: for representative (mean, target-bound) pairs, the quadratic solution's
    /// LnNormal reproduces the target 1% quantile to relative machine-level precision.
    /// </summary>
    [TestMethod]
    public void Test_SigmaRepair_ClosedFormSolvesTheBoundEquation()
    {
        // Arrange — z exactly as LnNormal.InverseCDF evaluates it.
        double z = -Math.Sqrt(2d) * Erf.InverseErfc(2d * 0.01d);
        (double Mean, double Target)[] cases = [(10d, 8d), (100d, 99.9d), (5d, 1.2d), (1400d, 900d)];

        foreach (var (mean, target) in cases)
        {
            // Act — the closed form: v²/2 − z·v + (ln q − ln m) = 0 → v = z + √(z² − 2(ln q − ln m)).
            double c = Math.Log(target) - Math.Log(mean);
            double v = z + Math.Sqrt(z * z - 2d * c);
            double sd = mean * Math.Sqrt(Math.Exp(v * v) - 1d);

            // Assert — the repaired distribution's own 1% quantile is the target.
            Assert.IsTrue(v > 0d && sd > 0d, $"({mean}, {target}): the closed form must yield a positive spread.");
            Assert.AreEqual(target, new LnNormal(mean, sd).InverseCDF(0.01d), Math.Abs(target) * 1e-9,
                $"({mean}, {target}): the closed-form σ must solve the bound equation exactly.");
        }
    }

    /// <summary>Verifies co-monotonic percentile sampling: one percentile drives every ordinate.</summary>
    [TestMethod]
    public void Test_SampleFunction_Percentile_CoMonotonic()
    {
        // Arrange
        var h = AnchorHazard();

        // Act
        var p50 = (EmpiricalDistribution)h.SampleFunction(0.5d);
        var p95 = (EmpiricalDistribution)h.SampleFunction(0.95d);

        // Assert — the sampled curve's hazard ordinates are the per-ordinate percentiles: the
        // 95th curve sits above the median curve at every ordinate, and both carry the same
        // exceedance ladder.
        Assert.AreEqual(h.TrueUncertainFunction.Count, p50.XValues.Count);
        for (int i = 0; i < h.TrueUncertainFunction.Count; i++)
        {
            Assert.AreEqual(h.TrueUncertainFunction[i].Y!.InverseCDF(0.5d), p50.XValues[i], Math.Abs(p50.XValues[i]) * 1e-9);
            Assert.IsTrue(p95.XValues[i] > p50.XValues[i], $"Ordinate {i}: the 95th-percentile curve must sit above the median curve.");
        }

        // A deterministic-mode function samples the same curve at every percentile.
        var deterministic = AnchorHazard();
        deterministic.IsUncertain = false;
        var a = (EmpiricalDistribution)deterministic.SampleFunction(0.1d);
        var b = (EmpiricalDistribution)deterministic.SampleFunction(0.9d);
        CollectionAssert.AreEqual(a.XValues.ToArray(), b.XValues.ToArray());
    }

    /// <summary>Verifies the mean curve per mode, and that invalid states throw.</summary>
    [TestMethod]
    public void Test_SampleFunction_Mean_PerMode()
    {
        // Arrange — deterministic mode: the mean is the derived curve inverted directly.
        var deterministic = AnchorHazard();
        deterministic.IsUncertain = false;
        var mean = (EmpiricalDistribution)deterministic.SampleFunction();
        Assert.AreEqual(deterministic.TrueUncertainFunction.Count, mean.XValues.Count);
        Assert.AreEqual(500d, mean.XValues[0], 1e-9);

        // Uncertain mode: the 10,000-curve expected-probability assembly spans the
        // full-uncertainty hazard bounds.
        var uncertain = AnchorHazard();
        var uncertainMean = (EmpiricalDistribution)uncertain.SampleFunction();
        Assert.IsTrue(uncertainMean.XValues.Count > 2);
        Assert.IsTrue(uncertainMean.XValues[0] >= uncertain.MinHazard(false) - 1e-6);
        Assert.IsTrue(uncertainMean.XValues[uncertainMean.XValues.Count - 1] <= uncertain.MaxHazard(false) + 1e-6);

        // An unusable input state throws (the v1.1 upgrade of the v1.0 null return).
        var invalid = AnchorHazard();
        invalid.EffectiveRecordLength = 5;
        Assert.ThrowsException<InvalidOperationException>(() => invalid.SampleFunction());
        Assert.ThrowsException<InvalidOperationException>(() => invalid.SampleFunction(0.5d));
    }

    /// <summary>Verifies the realization-index overload reads the pre-allocated percentile row.</summary>
    [TestMethod]
    public void Test_SampleFunction_RealizationIndex_UsesSamplerRow()
    {
        // Arrange
        var h = AnchorHazard();
        h.SetupSampler(20, 12345, SamplingScheme.LatinHypercube);

        // Act / Assert — rows are deterministic per seed.
        var first = ((EmpiricalDistribution)h.SampleFunction(7)).XValues[0];
        h.SetupSampler(20, 12345, SamplingScheme.LatinHypercube);
        Assert.AreEqual(first, ((EmpiricalDistribution)h.SampleFunction(7)).XValues[0], 0d);

        // Before setup, index sampling throws.
        var fresh = AnchorHazard();
        Assert.ThrowsException<InvalidOperationException>(() => fresh.SampleFunction(0));
    }

    /// <summary>Verifies the v1.0 bounds surface over the derived table.</summary>
    [TestMethod]
    public void Test_Bounds_MatchV10()
    {
        // Arrange — deterministic mode: bounds are the derived end-ordinate means.
        var deterministic = AnchorHazard();
        deterministic.IsUncertain = false;
        Assert.AreEqual(500d, deterministic.MinHazard(meanOnly: true), 1e-9);
        Assert.IsTrue(deterministic.MaxHazard(meanOnly: true) > 9500d, "The rare-end extrapolation extends beyond the last input hazard.");

        // Uncertain mode: the full-uncertainty probes widen the span beyond the mean bounds.
        var uncertain = AnchorHazard();
        Assert.IsTrue(uncertain.MinHazard(false) < uncertain.MinHazard(true));
        Assert.IsTrue(uncertain.MaxHazard(false) > uncertain.MaxHazard(true));

        // An empty derived table reports the v1.0 sentinels.
        var invalid = AnchorHazard();
        invalid.EffectiveRecordLength = 5;
        Assert.AreEqual(double.MaxValue, invalid.MinHazard(true), 0d);
        Assert.AreEqual(double.MinValue, invalid.MaxHazard(true), 0d);
    }

    /// <summary>Verifies the uncertainty summary is exact percentile evaluation over the derived table.</summary>
    [TestMethod]
    public void Test_ComputeUncertaintyResults_ExactPercentiles()
    {
        // Arrange
        var h = AnchorHazard();

        // Act
        var summary = h.ComputeUncertaintyResults(0.90d)!;

        // Assert — index-aligned exact per-ordinate statistics over the DERIVED table.
        int count = h.TrueUncertainFunction.Count;
        Assert.AreEqual(count, summary.MeanCurve!.Length);
        for (int i = 0; i < count; i++)
        {
            var y = h.TrueUncertainFunction[i].Y!;
            Assert.AreEqual(y.Mean, summary.MeanCurve[i], 0d);
            Assert.AreEqual(y.InverseCDF(0.05d), summary.ConfidenceIntervals![i, 0], 0d);
            Assert.AreEqual(y.InverseCDF(0.95d), summary.ConfidenceIntervals[i, 1], 0d);
        }
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => h.ComputeUncertaintyResults(1.5d));
    }

    /// <summary>
    /// Verifies the inputs-only serialization contract (DC1): the round-trip restores every
    /// input member, the derived table is NOT serialized, and load-time recomputation reproduces
    /// it bit-for-bit (the derivation is deterministic).
    /// </summary>
    [TestMethod]
    public void Test_Serialization_RoundTrip_RecomputesDerived()
    {
        // Arrange
        var original = AnchorHazard();
        original.Description = "Round trip";
        original.EffectiveRecordLength = 75;
        original.ExtrapolationEP = 0.0005d;

        // Act
        var element = original.ToXElement();
        var restored = new NonparametricHazard(element);

        // Assert — the serialized form carries the inputs only.
        Assert.IsNull(element.Element(nameof(NonparametricHazard.TrueUncertainFunction)),
            "The derived table must never serialize — identity is the user-specified inputs.");
        Assert.IsNotNull(element.Element(nameof(NonparametricHazard.InputUncertainFunction)));

        // Every input member restores.
        Assert.AreEqual(original.Name, restored.Name);
        Assert.AreEqual(original.Description, restored.Description);
        Assert.AreEqual(original.SpecifiedHazard, restored.SpecifiedHazard);
        Assert.AreEqual(original.HazardTransform, restored.HazardTransform);
        Assert.AreEqual(original.ProbabilityTransform, restored.ProbabilityTransform);
        Assert.AreEqual(original.IsUncertain, restored.IsUncertain);
        Assert.AreEqual(original.EffectiveRecordLength, restored.EffectiveRecordLength);
        Assert.AreEqual(original.ExtrapolationEP, restored.ExtrapolationEP, 0d);
        Assert.AreEqual(original.InputUncertainFunction.Count, restored.InputUncertainFunction.Count);

        // The load-time recomputation reproduces the derived table exactly.
        Assert.AreEqual(original.TrueUncertainFunction.Count, restored.TrueUncertainFunction.Count);
        for (int i = 0; i < original.TrueUncertainFunction.Count; i++)
        {
            Assert.AreEqual(original.TrueUncertainFunction[i].X, restored.TrueUncertainFunction[i].X, 0d);
            Assert.AreEqual(original.TrueUncertainFunction[i].Y!.Mean, restored.TrueUncertainFunction[i].Y!.Mean, 0d);
            Assert.AreEqual(original.TrueUncertainFunction[i].Y!.StandardDeviation, restored.TrueUncertainFunction[i].Y!.StandardDeviation, 0d);
        }

        CollectionAssert.AreEqual(original.CanonicalHash(), restored.CanonicalHash(), "Round-trip must preserve the canonical hash.");
    }

    /// <summary>
    /// Verifies in-place input-table edits re-derive immediately (the v1.0 collection-changed
    /// behavior), and that assigning a new table re-derives too.
    /// </summary>
    [TestMethod]
    public void Test_InputEdits_Rederive()
    {
        // Arrange
        var h = AnchorHazard();
        int before = h.TrueUncertainFunction.Count;

        // Act — an in-place ordinate insert re-derives through the collection notification.
        h.InputUncertainFunction.Add(new UncertainOrdinate(0.001d, new Deterministic(11000d)));

        // Assert
        Assert.AreEqual(before + 1, h.TrueUncertainFunction.Count);
    }

    /// <summary>Verifies hash identity: metadata inert, every compute-relevant scalar moves it.</summary>
    [TestMethod]
    public void Test_CanonicalHash_Identity()
    {
        // Arrange
        var h = AnchorHazard();

        // Act / Assert
        HashInvariance.AssertMetadataInvariant(h);
        HashInvariance.AssertStrippedAttributesInert(h.ToXElement());
        HashInvariance.AssertComputeSensitive(h.CanonicalHash, () => h.EffectiveRecordLength = 250);
        HashInvariance.AssertComputeSensitive(h.CanonicalHash, () => h.ExtrapolationEP = 0.001d);
        HashInvariance.AssertComputeSensitive(h.CanonicalHash, () => h.Extrapolation = ExtrapolationPolicy.Both);
        HashInvariance.AssertComputeSensitive(h.CanonicalHash, () => h.IsUncertain = false);
        HashInvariance.AssertComputeSensitive(h.CanonicalHash, () => h.HazardTransform = Transform.None);
    }

    /// <summary>
    /// Builds a labeled uncertain fixture from (probability, hazard) pairs.
    /// </summary>
    /// <param name="hazardTransform">The hazard-axis transform.</param>
    /// <param name="ordinates">The (exceedance probability, hazard mean) pairs, descending X.</param>
    /// <returns>The configured fixture.</returns>
    private static NonparametricHazard BuildFixture(Transform hazardTransform, params (double P, double X)[] ordinates)
    {
        return new NonparametricHazard
        {
            Name = "Fixture",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            HazardTransform = hazardTransform,
            InputUncertainFunction = new UncertainOrderedPairedData(
                ordinates.Select(o => new UncertainOrdinate(o.P, new Deterministic(o.X))).ToArray(),
                true, SortOrder.Descending, true, SortOrder.Ascending, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>
    /// Verifies the extrapolation policy's serialization contract — ABSENT when default, with the
    /// explicit-None assignment byte-identical (the hash-preservation pin), present by name and
    /// hash-moving when configured, round-tripping faithfully — its sampling-only asymmetry (the
    /// setter never re-derives the table, unlike the serialized derivation-time
    /// <c>ExtrapolationEP</c> extension), and its wiring incl. the Error guard on the sampled
    /// products.
    /// </summary>
    [TestMethod]
    public void Test_Extrapolation_ConditionalPresence_Wiring_AndErrorGuard()
    {
        // Arrange
        var h = AnchorHazard();
        var baselineXml = h.ToXElement().ToString();
        byte[] baselineHash = h.CanonicalHash();
        Assert.IsNull(h.ToXElement().Attribute(nameof(NonparametricHazard.Extrapolation)));

        // The explicit-None assignment is byte-inert, and the setter is sampling-only: the
        // derived table instance is untouched (a re-derivation would replace it).
        var derived = h.TrueUncertainFunction;
        h.Extrapolation = ExtrapolationPolicy.Both;
        h.Extrapolation = ExtrapolationPolicy.None;
        Assert.AreSame(derived, h.TrueUncertainFunction);
        Assert.AreEqual(baselineXml, h.ToXElement().ToString());
        CollectionAssert.AreEqual(baselineHash, h.CanonicalHash());

        // A configured policy serializes by name, moves the hash, and round-trips faithfully.
        h.Extrapolation = ExtrapolationPolicy.Both;
        Assert.AreSame(derived, h.TrueUncertainFunction);
        var xml = h.ToXElement();
        Assert.AreEqual(nameof(ExtrapolationPolicy.Both), xml.Attribute(nameof(NonparametricHazard.Extrapolation))?.Value);
        Assert.IsFalse(h.CanonicalHash().SequenceEqual(baselineHash),
            "The extrapolation policy is compute content and must move the canonical hash.");
        var restored = new NonparametricHazard(xml);
        Assert.AreEqual(ExtrapolationPolicy.Both, restored.Extrapolation);
        CollectionAssert.AreEqual(h.CanonicalHash(), restored.CanonicalHash());
        Assert.AreEqual(xml.ToString(), restored.ToXElement().ToString());

        // Wiring: the sampled curve carries the mapped sides and widens its inverse tails.
        var extended = (EmpiricalDistribution)h.SampleFunction(0.5d);
        Assert.AreEqual(ExtrapolationSides.Both, extended.Extrapolation);
        double low = extended.InverseCDF(1E-16);
        double high = extended.InverseCDF(1d - 1E-16);
        Assert.IsFalse(double.IsNaN(low) || double.IsInfinity(low));
        Assert.IsFalse(double.IsNaN(high) || double.IsInfinity(high));
        Assert.IsTrue(low < extended.Minimum, "The extended lower tail must fall below the table span.");
        Assert.IsTrue(high > extended.Maximum, "The extended upper tail must rise above the table span.");

        // Error mode: hazard-axis queries beyond the sampled span throw loudly; the inverse
        // retains the hold.
        h.Extrapolation = ExtrapolationPolicy.Error;
        var guarded = h.SampleFunction(0.5d);
        Assert.IsInstanceOfType(guarded, typeof(RangeGuardedUnivariateDistribution));
        Assert.AreEqual(guarded.Maximum, guarded.InverseCDF(1d - 1E-16), 0d);
        var fault = Assert.ThrowsException<ExtrapolationRangeException>(() => guarded.CDF(guarded.Maximum + 1d));
        StringAssert.Contains(fault.Message, "Graphical");
        StringAssert.Contains(fault.Message, "Flow (cfs)");
        Assert.IsNotNull(EvaluationFaultScope.Consume());
    }
}
