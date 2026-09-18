using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Results.Risk;
using RMC.TotalRisk.Results.Support;

namespace RMC.TotalRisk.Tests.Results.Risk;

/// <summary>
/// Unit tests for <see cref="Curve"/> — the exact loss-exceedance-curve construction, the stable
/// two-pass weighted central moments (with the implicit zero atom on defective curves), the
/// tail-preserving thinning, the mass post-processing, and the risk-measure catalog with its two
/// v1.0 fixes (VaR beyond the total probability, explicit CVaR integration caps).
/// </summary>
[TestClass]
public class CurveTests
{
    /// <summary>
    /// Verifies the exact exceedance construction against a hand-computed discrete distribution,
    /// including the interpolation anchors above the largest and at zero consequence.
    /// </summary>
    [TestMethod]
    public void Test_CreateCurve_ExactExceedance_VsAnalytic()
    {
        // Arrange — an exhaustive three-atom loss distribution.
        var curve = new Curve();
        var pairs = new List<(double Mass, double Consequence)> { (0.2d, 10d), (0.3d, 5d), (0.5d, 1d) };

        // Act
        curve.CreateCurve(pairs, outputLength: 200);

        // Assert — exact reverse-cumulative exceedance with the two anchors.
        CollectionAssert.AreEqual(new[] { 10d * (1d + 1e-8), 10d, 5d, 1d, 0d }, curve.LECConsequences);
        CollectionAssert.AreEqual(new[] { 0d, 0.2d, 0.5d, 1d, 1d }, curve.LECProbabilities);
        Assert.AreEqual(1d, curve.TotalProbability, 0d);
        Assert.AreEqual(1d, curve.MassBalance, 1e-15);

        // Exact moments: E[X] = 4.0, Var = 28 − 16 = 12.
        Assert.AreEqual(4.0d, curve.Mean, 1e-14);
        Assert.AreEqual(Math.Sqrt(12d), curve.StandardDeviation, 1e-13);
    }

    /// <summary>
    /// Verifies defective-curve semantics: the total probability is the recorded mass, and the
    /// moments include the implicit zero-consequence atom so the mean stays unconditional (the
    /// v1.0 semantics, computed stably).
    /// </summary>
    [TestMethod]
    public void Test_CreateCurve_Defective_ZeroAtomMoments()
    {
        // Arrange — the same distribution at one-tenth probability, on a defective stream.
        var curve = new Curve { IsExhaustive = false };
        var pairs = new List<(double Mass, double Consequence)> { (0.02d, 10d), (0.03d, 5d), (0.05d, 1d) };

        // Act
        curve.CreateCurve(pairs, outputLength: 200);

        // Assert — TP = 0.1; unconditional E[X] = 0.4; Var = 2.8 − 0.16 = 2.64 (zero atom carries 0.9).
        Assert.AreEqual(0.1d, curve.TotalProbability, 1e-15);
        Assert.AreEqual(0.4d, curve.Mean, 1e-15);
        Assert.AreEqual(Math.Sqrt(2.64d), curve.StandardDeviation, 1e-13);
        Assert.AreEqual(4.0d, curve.ConditionalMean, 1e-13);

        // The bottom anchor carries the defective total probability at zero consequence.
        Assert.AreEqual(0d, curve.LECConsequences[^1], 0d);
        Assert.AreEqual(0.1d, curve.LECProbabilities[^1], 1e-15);
    }

    /// <summary>
    /// Verifies the catastrophic-cancellation fixture the two-pass accumulation exists for: a
    /// distribution with mean 1e6 and standard deviation 1e-2. The v1.0 raw power sums
    /// (√(u2 − u1²)) lose all significant digits here — u2 ≈ 1e12 has representable spacing
    /// ~1.2e-4, the same order as the entire variance.
    /// </summary>
    [TestMethod]
    public void Test_CreateCurve_WeightedCentralMoments_NoCancellation()
    {
        // Arrange — symmetric two-point distribution: mean 1e6, sd exactly 0.01.
        var curve = new Curve();
        var pairs = new List<(double Mass, double Consequence)> { (0.5d, 1e6 - 0.01d), (0.5d, 1e6 + 0.01d) };

        // Act
        curve.CreateCurve(pairs, outputLength: 200);

        // Assert
        Assert.AreEqual(1e6, curve.Mean, 1e-9);
        Assert.AreEqual(0.01d, curve.StandardDeviation, 1e-9);
        Assert.AreEqual(0d, curve.Skewness, 1e-6);
        Assert.AreEqual(1d, curve.Kurtosis, 1e-6, "A symmetric two-point distribution has plain kurtosis one.");
    }

    /// <summary>
    /// Verifies a degenerate (zero-variance) distribution reports NaN shape moments instead of
    /// dividing by zero.
    /// </summary>
    [TestMethod]
    public void Test_CreateCurve_Degenerate_NaNShapeMoments()
    {
        // Arrange
        var curve = new Curve();
        var pairs = new List<(double Mass, double Consequence)> { (0.6d, 7d), (0.4d, 7d) };

        // Act
        curve.CreateCurve(pairs, outputLength: 200);

        // Assert
        Assert.AreEqual(7d, curve.Mean, 1e-14);
        Assert.AreEqual(0d, curve.StandardDeviation, 1e-14);
        Assert.IsTrue(double.IsNaN(curve.Skewness));
        Assert.IsTrue(double.IsNaN(curve.Kurtosis));
    }

    /// <summary>
    /// Verifies thinning is output-resolution only: moments and total probability are identical
    /// to the unthinned construction, and the stored curve retains the extreme-tail and terminal
    /// ordinates.
    /// </summary>
    [TestMethod]
    public void Test_CreateCurve_Thinning_PreservesMomentsAndTail()
    {
        // Arrange — ten thousand pairs spanning four orders of magnitude.
        var pairs = new List<(double Mass, double Consequence)>(10_000);
        for (int i = 0; i < 10_000; i++)
        {
            pairs.Add((1e-4, Math.Pow(10d, 4d * i / 9_999d)));
        }
        var full = new Curve();
        var thinned = new Curve();

        // Act
        full.CreateCurve(pairs, outputLength: 1000);
        thinned.CreateCurve(pairs, outputLength: 50);

        // Assert — identical exact statistics; bounded storage; tail and terminals retained.
        Assert.AreEqual(full.Mean, thinned.Mean, 0d);
        Assert.AreEqual(full.StandardDeviation, thinned.StandardDeviation, 0d);
        Assert.AreEqual(full.TotalProbability, thinned.TotalProbability, 0d);
        Assert.IsTrue(thinned.LECConsequences.Length <= 52 + 2, $"Stored {thinned.LECConsequences.Length} ordinates for output length 50.");
        Assert.AreEqual(full.LECConsequences[0], thinned.LECConsequences[0], 0d, "The extreme-tail anchor must be retained.");
        Assert.AreEqual(full.LECConsequences[^1], thinned.LECConsequences[^1], 0d, "The terminal ordinate must be retained.");
        Assert.AreEqual(full.LECProbabilities[^1], thinned.LECProbabilities[^1], 0d);
    }

    /// <summary>
    /// Verifies the recorded-mass application: sort, first-occurrence credit for a repeated
    /// abscissa, mass taken from the ledger, and compaction of the points the refinement
    /// superseded.
    /// </summary>
    [TestMethod]
    public void Test_ApplyRecordedMass_CreditsLedgerAndCompacts()
    {
        // Arrange — three accepted abscissas, one of them recorded twice, plus one abscissa the
        // refinement superseded (absent from the ledger) and one accepted at zero weight.
        var curve = new Curve();
        curve.AddRiskPoint(1d, 0.5d, 0.2d, 10d);
        curve.AddRiskPoint(2d, 0.1d, 0.3d, 20d);
        curve.AddRiskPoint(3d, 0.9d, 0.4d, 30d);
        curve.AddRiskPoint(4d, 0.5d, 0.6d, 40d);
        curve.AddRiskPoint(5d, 0.7d, 0.8d, 50d);
        curve.AddRiskPoint(6d, 0.3d, 0.9d, 60d);

        var ledger = new QuadratureMassLedger();
        ledger.Record(0.1d, 0.3d, 0d);
        ledger.Record(0.5d, 0.4d, 0d);
        ledger.Record(0.9d, 0.3d, 0d);
        ledger.Record(0.3d, 0d, 0d);
        ledger.Seal();

        // Act
        curve.ApplyRecordedMass(ledger);

        // Assert — p = 0.7 was superseded and p = 0.3 carried no weight, so both are compacted
        // away; the duplicate p = 0.5 is credited once.
        Assert.AreEqual(3, curve.RiskPoints.Count);
        Assert.AreEqual(0.1d, curve.RiskPoints[0].HazardProbability, 0d);
        Assert.AreEqual(0.3d, curve.RiskPoints[0].HazardProbabilityMass, 1e-15);
        Assert.AreEqual(0.5d, curve.RiskPoints[1].HazardProbability, 0d);
        Assert.AreEqual(0.4d, curve.RiskPoints[1].HazardProbabilityMass, 1e-15);
        Assert.AreEqual(0.9d, curve.RiskPoints[2].HazardProbability, 0d);
        Assert.AreEqual(0.3d, curve.RiskPoints[2].HazardProbabilityMass, 1e-15);
    }

    /// <summary>
    /// A curve whose recorded abscissas do not cover the ledger's throws rather than publishing a
    /// thinned curve. The recording fan-out receives one point per evaluation by construction, so
    /// a shortfall is an engine invariant violation.
    /// </summary>
    [TestMethod]
    public void Test_ApplyRecordedMass_ShortfallThrows()
    {
        // Arrange — the ledger accepted two abscissas; the curve only recorded one of them.
        var curve = new Curve();
        curve.AddRiskPoint(1d, 0.5d, 0.2d, 10d);
        curve.AddRiskPoint(2d, 0.1d, 0.3d, 20d);

        var ledger = new QuadratureMassLedger();
        ledger.Record(0.1d, 0.3d, 0d);
        ledger.Record(0.5d, 0.4d, 0d);
        ledger.Record(0.9d, 0.3d, 0d);
        ledger.Seal();

        // Act / Assert
        Assert.ThrowsException<InvalidOperationException>(() => curve.ApplyRecordedMass(ledger));
    }

    /// <summary>
    /// Verifies the value-at-risk fix: when the exceedance level exceeds the curve's total
    /// probability the consequence is not realized and the value at risk is zero — v1.0 returned
    /// the curve's smallest consequence.
    /// </summary>
    [TestMethod]
    public void Test_ComputeRiskMeasures_VaR_ZeroBeyondTotalProbability()
    {
        // Arrange — a defective curve with total probability 0.1.
        var curve = new Curve { IsExhaustive = false };
        curve.CreateCurve(new List<(double Mass, double Consequence)> { (0.02d, 10d), (0.03d, 5d), (0.05d, 1d) }, 200);

        // Act / Assert — above the total probability: zero. Below: a genuine quantile.
        curve.ComputeRiskMeasures(consequenceThreshold: 0d, alpha: 0.2d);
        Assert.AreEqual(0d, curve.ValueAtRisk, 0d, "α above the total probability must yield zero, not the smallest consequence.");

        curve.ComputeRiskMeasures(consequenceThreshold: 0d, alpha: 0.05d);
        Assert.AreEqual(5d, curve.ValueAtRisk, 1e-12, "α = 0.05 sits exactly on the (5, 0.02 + 0.03) ordinate.");
    }

    /// <summary>
    /// Verifies the conditional-value-at-risk integral against a closed form: a log-log linear
    /// LEC X(p) = p^(−1/2) has CVaR(α) = (1/α)·2(√α − √ε) ≈ 2/√α.
    /// </summary>
    [TestMethod]
    public void Test_ComputeRiskMeasures_CVaR_VsClosedForm()
    {
        // Arrange — the power-law curve sampled on a log grid from 1e-16 to 1 (81 ordinates), so
        // log-log interpolation reproduces X(p) exactly over the whole integration domain.
        var probabilities = new double[81];
        var consequences = new double[81];
        for (int j = 0; j <= 80; j++)
        {
            double p = Math.Pow(10d, -16d + 0.2d * j);
            probabilities[j] = p;
            consequences[j] = Math.Pow(p, -0.5d);
        }
        var curve = new Curve
        {
            LECConsequences = consequences,
            LECProbabilities = probabilities,
            TotalProbability = 1d,
        };

        // Act
        curve.ComputeRiskMeasures(consequenceThreshold: 10d, alpha: 0.01d);

        // Assert — VaR(0.01) = 0.01^(−1/2) = 10 (an exact grid ordinate); the assurance measure at
        // threshold 10 reads back 0.01; CVaR = 100·2(√0.01 − √1e-16) = 20 − 2e-6.
        Assert.AreEqual(10d, curve.ValueAtRisk, 1e-9);
        Assert.AreEqual(0.01d, curve.ConsequenceThresholdProbability, 1e-9);
        Assert.AreEqual(20d - 2e-6, curve.ConditionalValueAtRisk, 20d * 1e-6, "CVaR must match the closed form within 1e-6 relative.");
    }

    /// <summary>
    /// Verifies the risk profiles: descending hazard, cumulative exceedance, and the conditional
    /// expected consequence ratio.
    /// </summary>
    [TestMethod]
    public void Test_CreateProfiles_CumulativeExceedanceAndCEN()
    {
        // Arrange — three points; masses are already final (no post-processing).
        var curve = new Curve();
        curve.AddRiskPoint(1d, 0.5d, 0.1d, 10d);
        curve.AddRiskPoint(2d, 0.3d, 0.2d, 20d);
        curve.AddRiskPoint(3d, 0.2d, 0.5d, 30d);

        // Act
        curve.CreateProfiles();

        // Assert — hazard descending; EP accumulates 0.1, 0.16, 0.21; CEN = ΣEAC/ΣEP.
        CollectionAssert.AreEqual(new[] { 3d, 2d, 1d }, curve.HazardFrequencyHazards);
        Assert.AreEqual(0.10d, curve.HazardFrequencyProbabilities[0], 1e-15);
        Assert.AreEqual(0.16d, curve.HazardFrequencyProbabilities[1], 1e-15);
        Assert.AreEqual(0.21d, curve.HazardFrequencyProbabilities[2], 1e-15);
        Assert.AreEqual(30d, curve.HazardVsCenConsequences[0], 1e-12);
        Assert.AreEqual(4.2d / 0.16d, curve.HazardVsCenConsequences[1], 1e-12);
        Assert.AreEqual(4.7d / 0.21d, curve.HazardVsCenConsequences[2], 1e-12);
    }

    /// <summary>
    /// Verifies Clone copies every stored value deeply and DumpMemory clears only the runtime
    /// risk points.
    /// </summary>
    [TestMethod]
    public void Test_CloneAndDumpMemory()
    {
        // Arrange
        var curve = new Curve { IsExhaustive = false };
        curve.AddRiskPoint(1d, 0.5d, 0.2d, 10d);
        curve.CreateCurve(new List<(double Mass, double Consequence)> { (0.1d, 10d), (0.2d, 5d) }, 200);
        curve.ComputeRiskMeasures(1d, 0.05d);

        // Act
        var clone = curve.Clone();
        clone.LECConsequences[0] = -999d;
        curve.DumpMemory();

        // Assert
        Assert.AreEqual(curve.Mean, clone.Mean, 0d);
        Assert.AreEqual(curve.TotalProbability, clone.TotalProbability, 0d);
        Assert.AreEqual(curve.ValueAtRisk, clone.ValueAtRisk, 0d);
        Assert.AreNotEqual(-999d, curve.LECConsequences[0], "Clone must copy the arrays, not share them.");
        Assert.AreEqual(0, curve.RiskPoints.Count, "DumpMemory must clear the runtime risk points.");
        Assert.IsTrue(curve.LECConsequences.Length > 0, "DumpMemory must not touch the stored curve.");
    }

    /// <summary>
    /// Verifies construction argument contracts: null pairs and an output length below two throw.
    /// </summary>
    [TestMethod]
    public void Test_CreateCurve_ArgumentContracts()
    {
        // Act / Assert
        var curve = new Curve();
        Assert.ThrowsException<ArgumentNullException>(() => curve.CreateCurve(null!, 200));
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => curve.CreateCurve(new List<(double Mass, double Consequence)> { (0.5d, 1d) }, 1));
    }


    /// <summary>
    /// Rebuilding a curve clears every prior derived value, accepts a singleton distribution,
    /// and leaves no stale state when a defective rebuild has no positive mass.
    /// </summary>
    [TestMethod]
    public void Test_CreateCurve_RebuildAndSingleton_ClearPriorState()
    {
        var curve = new Curve();
        curve.CreateCurve(new List<(double Mass, double Consequence)> { (1d, 7d) }, 20);
        curve.ComputeRiskMeasures(5d, 0.05d);
        Assert.AreEqual(7d, curve.Mean, 0d);
        Assert.IsTrue(curve.LECConsequences.Length > 0);

        curve.IsExhaustive = false;
        curve.CreateCurve(new List<(double Mass, double Consequence)> { (0.25d, 4d) }, 20);
        Assert.AreEqual(0.25d, curve.TotalProbability, 0d);
        Assert.AreEqual(1d, curve.Mean, 0d);
        Assert.IsTrue(double.IsNaN(curve.ValueAtRisk), "A rebuild must clear previously computed optional measures.");
        CollectionAssert.DoesNotContain(curve.LECConsequences, 7d);

        curve.CreateCurve(Array.Empty<(double Mass, double Consequence)>(), 20);
        Assert.AreEqual(0d, curve.TotalProbability, 0d);
        Assert.AreEqual(0d, curve.Mean, 0d);
        Assert.AreEqual(0, curve.LECConsequences.Length);
        Assert.AreEqual(0, curve.CumulativeExpectedConsequences.Length);
    }

    /// <summary>
    /// Invalid probability and consequence inputs fail closed, including a defective stream
    /// whose finite non-negative masses materially exceed one.
    /// </summary>
    [TestMethod]
    public void Test_CreateCurve_InvalidPairs_Throw()
    {
        var curve = new Curve { IsExhaustive = false };
        Assert.ThrowsException<ArgumentException>(() => curve.CreateCurve(
            new List<(double Mass, double Consequence)> { (double.NaN, 1d) }, 20));
        Assert.ThrowsException<ArgumentException>(() => curve.CreateCurve(
            new List<(double Mass, double Consequence)> { (1d, double.PositiveInfinity) }, 20));
        Assert.ThrowsException<ArgumentException>(() => curve.CreateCurve(
            new List<(double Mass, double Consequence)> { (-double.Epsilon, 1d) }, 20));
        Assert.ThrowsException<InvalidOperationException>(() => curve.CreateCurve(
            new List<(double Mass, double Consequence)> { (0.75d, 1d), (0.5d, 2d) }, 20));
    }

    /// <summary>
    /// Compensated coalescing and moments preserve tiny high-consequence atoms beside a nearly
    /// unit low-consequence atom.
    /// </summary>
    [TestMethod]
    public void Test_CreateCurve_DynamicRangeMass_IsCompensated()
    {
        const int tinyCount = 1000;
        const double tinyMass = 1e-15;
        var pairs = new List<(double Mass, double Consequence)>(tinyCount + 1)
        {
            (1d - tinyCount * tinyMass, 2d),
        };
        for (int i = 0; i < tinyCount; i++)
        {
            pairs.Add((tinyMass, 1e12));
        }

        var curve = new Curve();
        curve.CreateCurve(pairs, 40);

        Assert.AreEqual(1d, curve.MassBalance, 0d);
        Assert.AreEqual(3d - 2d * tinyCount * tinyMass, curve.Mean, 2e-14);
    }

    /// <summary>
    /// Log-log error refinement is deterministic and invariant to a constant consequence scale.
    /// </summary>
    [TestMethod]
    public void Test_CreateCurve_Thinning_IsScaleInvariant()
    {
        var original = new List<(double Mass, double Consequence)>(2000);
        var scaled = new List<(double Mass, double Consequence)>(2000);
        for (int i = 0; i < 2000; i++)
        {
            double consequence = Math.Exp(i / 150d) * (1d + 0.1d * Math.Sin(i * 0.07d));
            original.Add((0.0005d, consequence));
            scaled.Add((0.0005d, consequence * 1000d));
        }

        var first = new Curve();
        var second = new Curve();
        first.CreateCurve(original, 40);
        second.CreateCurve(scaled, 40);

        CollectionAssert.AreEqual(first.LECProbabilities, second.LECProbabilities);
        Assert.AreEqual(first.LECConsequences.Length, second.LECConsequences.Length);
        for (int i = 0; i < first.LECConsequences.Length; i++)
        {
            Assert.AreEqual(first.LECConsequences[i] * 1000d, second.LECConsequences[i], 1e-9 * Math.Max(1d, second.LECConsequences[i]));
        }
    }
    /// <summary>Builds the profile-catalog fixture: three final-mass points with entry lists and exceedance coordinates.</summary>
    private static Curve CatalogFixture(bool withExceedance = true)
    {
        var curve = new Curve { IsExhaustive = false };
        curve.AddRiskPoint(1d, 0.5d, new List<double> { 0.1d }, new List<double> { 10d }, withExceedance ? 0.9d : double.NaN);
        curve.AddRiskPoint(2d, 0.3d, new List<double> { 0.2d }, new List<double> { 20d }, withExceedance ? 0.4d : double.NaN);
        curve.AddRiskPoint(3d, 0.2d, new List<double> { 0.5d }, new List<double> { 30d }, withExceedance ? 0.1d : double.NaN);
        return curve;
    }

    /// <summary>
    /// Verifies the profile catalog at hand-computed points: the ascending cumulative
    /// failure probability and expected consequence (stored descending), their terminal
    /// identities against the exact curve's mass balance and mean, the system response profile
    /// on the exceedance axis, monotonicity, and the self-normalizing fraction views.
    /// </summary>
    [TestMethod]
    public void Test_CreateProfiles_CumulativeCatalog_KnownPoints()
    {
        // Arrange — masses (0.5, 0.3, 0.2) × responses (0.1, 0.2, 0.5) × consequences (10, 20, 30).
        var curve = CatalogFixture();
        curve.CreateCurve(200);

        // Act
        curve.CreateProfiles(includeFailureProfiles: true);

        // Assert — ascending cumulates stored descending in hazard:
        // A(1) = 0.05, A(2) = 0.11, A(3) = 0.21; B(1) = 0.5, B(2) = 1.7, B(3) = 4.7.
        CollectionAssert.AreEqual(new[] { 3d, 2d, 1d }, curve.HazardFrequencyHazards);
        Assert.AreEqual(0.21d, curve.CumulativeFailureProbabilities[0], 1e-15);
        Assert.AreEqual(0.11d, curve.CumulativeFailureProbabilities[1], 1e-15);
        Assert.AreEqual(0.05d, curve.CumulativeFailureProbabilities[2], 1e-15);
        Assert.AreEqual(4.7d, curve.CumulativeExpectedConsequences[0], 1e-12);
        Assert.AreEqual(1.7d, curve.CumulativeExpectedConsequences[1], 1e-12);
        Assert.AreEqual(0.5d, curve.CumulativeExpectedConsequences[2], 1e-12);

        // Terminal identities: A-terminal ≡ MassBalance, B-terminal ≡ Mean (1e-12 relative).
        Assert.AreEqual(curve.MassBalance, curve.CumulativeFailureProbabilities[0], 1e-12 * curve.MassBalance,
            "The cumulative failure probability's terminal ordinate must equal the recorded mass balance.");
        Assert.AreEqual(curve.Mean, curve.CumulativeExpectedConsequences[0], 1e-12 * curve.Mean,
            "The cumulative expected consequence's terminal ordinate must equal the stream mean.");

        // Monotonicity along descending hazard: cumulates descend.
        for (int i = 1; i < curve.CumulativeFailureProbabilities.Length; i++)
        {
            Assert.IsTrue(curve.CumulativeFailureProbabilities[i] <= curve.CumulativeFailureProbabilities[i - 1]);
            Assert.IsTrue(curve.CumulativeExpectedConsequences[i] <= curve.CumulativeExpectedConsequences[i - 1]);
        }

        // The system response profile: X = exceedance descending, Y = the per-level combined
        // response probability, in [0, 1].
        CollectionAssert.AreEqual(new[] { 0.9d, 0.4d, 0.1d }, curve.SystemResponseExceedanceProbabilities);
        CollectionAssert.AreEqual(new[] { 0.1d, 0.2d, 0.5d }, curve.SystemResponseProbabilities);
        Assert.AreEqual(3, curve.SystemResponseProfile.Count);

        // The fraction views self-normalize by their own terminal.
        var fraction = curve.FractionOfFailureProbabilityByHazard;
        Assert.AreEqual(1d, fraction[0].Y, 1e-15);
        Assert.AreEqual(0.11d / 0.21d, fraction[1].Y, 1e-15);
        Assert.AreEqual(0.05d / 0.21d, fraction[2].Y, 1e-15);
        Assert.AreEqual(1d, curve.FractionOfExpectedConsequenceByHazard[0].Y, 1e-15);
    }

    /// <summary>
    /// Verifies the catalog gating: without the failure-profile flag only the cumulative
    /// expected consequence builds, and with the flag but missing exceedance coordinates the
    /// response profile is skipped while the cumulative failure probability still builds.
    /// </summary>
    [TestMethod]
    public void Test_CreateProfiles_FlagGating_AndMissingExceedance()
    {
        // Without the flag: B only.
        var plain = CatalogFixture();
        plain.CreateProfiles();
        Assert.AreEqual(0, plain.CumulativeFailureProbabilities.Length);
        Assert.AreEqual(0, plain.SystemResponseProbabilities.Length);
        Assert.AreEqual(3, plain.CumulativeExpectedConsequences.Length);

        // With the flag but no exceedance coordinates: A builds, the response profile skips.
        var missing = CatalogFixture(withExceedance: false);
        missing.CreateProfiles(includeFailureProfiles: true);
        Assert.AreEqual(3, missing.CumulativeFailureProbabilities.Length);
        Assert.AreEqual(0, missing.SystemResponseExceedanceProbabilities.Length);
        Assert.AreEqual(0, missing.SystemResponseProbabilities.Length);
        Assert.AreEqual(0, missing.SystemResponseProfile.Count, "The missing-coordinate view must be empty, not throw.");
    }

    /// <summary>
    /// Verifies the profile-catalog arrays round-trip through JSON and that an earlier payload
    /// without them loads forward with empty arrays ("not computed").
    /// </summary>
    [TestMethod]
    public void Test_Serialization_ProfileCatalog_RoundTripAndForwardLoad()
    {
        // Arrange
        var curve = CatalogFixture();
        curve.CreateCurve(200);
        curve.CreateProfiles(includeFailureProfiles: true);

        // Act — round-trip (named float literals per the shared results-JSON convention: the
        // unset measures are NaN).
        var jsonOptions = new JsonSerializerOptions
        {
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };
        var restored = JsonSerializer.Deserialize<Curve>(JsonSerializer.Serialize(curve, jsonOptions), jsonOptions)!;

        // Assert
        CollectionAssert.AreEqual(curve.CumulativeFailureProbabilities, restored.CumulativeFailureProbabilities);
        CollectionAssert.AreEqual(curve.CumulativeExpectedConsequences, restored.CumulativeExpectedConsequences);
        CollectionAssert.AreEqual(curve.SystemResponseExceedanceProbabilities, restored.SystemResponseExceedanceProbabilities);
        CollectionAssert.AreEqual(curve.SystemResponseProbabilities, restored.SystemResponseProbabilities);

        // Act / Assert — the earlier shape (no catalog members) loads forward with empty arrays.
        const string legacyJson = "{\"IsExhaustive\":false,\"TotalProbability\":0.2,\"Mean\":1.5," +
            "\"LECConsequences\":[10,5],\"LECProbabilities\":[0.1,0.2]," +
            "\"HazardFrequencyHazards\":[3,1],\"HazardFrequencyProbabilities\":[0.1,0.2]}";
        var legacy = JsonSerializer.Deserialize<Curve>(legacyJson)!;
        Assert.AreEqual(0, legacy.CumulativeFailureProbabilities.Length);
        Assert.AreEqual(0, legacy.CumulativeExpectedConsequences.Length);
        Assert.AreEqual(0, legacy.SystemResponseExceedanceProbabilities.Length);
        Assert.AreEqual(0, legacy.SystemResponseProbabilities.Length);
        Assert.AreEqual(2, legacy.LECConsequences.Length, "The legacy members must still load.");
    }

    /// <summary>
    /// Verifies <see cref="Curve.Clone"/> copies the profile-catalog arrays deeply.
    /// </summary>
    [TestMethod]
    public void Test_Clone_CopiesProfileCatalogArrays()
    {
        // Arrange
        var curve = CatalogFixture();
        curve.CreateCurve(200);
        curve.CreateProfiles(includeFailureProfiles: true);

        // Act
        var clone = curve.Clone();
        clone.CumulativeFailureProbabilities[0] = -1d;
        clone.SystemResponseProbabilities[0] = -1d;

        // Assert — deep copies, not shared references.
        Assert.AreEqual(0.21d, curve.CumulativeFailureProbabilities[0], 1e-15);
        Assert.AreEqual(0.1d, curve.SystemResponseProbabilities[0], 1e-15);
        CollectionAssert.AreEqual(curve.CumulativeExpectedConsequences, clone.CumulativeExpectedConsequences);
        CollectionAssert.AreEqual(curve.SystemResponseExceedanceProbabilities, clone.SystemResponseExceedanceProbabilities);
    }

    /// <summary>
    /// Verifies the multi-entry recording boundary clips each finite response probability and
    /// preserves NaN so downstream validation still fails closed.
    /// </summary>
    [TestMethod]
    public void Test_AddRiskPoint_ClipsRecordedResponseProbabilities()
    {
        var probabilities = new List<double> { -0.25d, 0.5d, 1.25d, double.NaN };
        var consequences = new List<double> { 1d, 2d, 3d, 4d };
        var curve = new Curve();

        curve.AddRiskPoint(5d, 0.4d, probabilities, consequences);

        CollectionAssert.AreEqual(new[] { 0d, 0.5d, 1d, double.NaN },
            curve.RiskPoints[0].ResponseProbabilities.ToArray());
    }

    /// <summary>
    /// Verifies that the quantile tail integral is the single arithmetic authority behind the
    /// published conditional value-at-risk: <c>QuantileTailIntegral(1e-16, α)/α</c> reproduces
    /// <see cref="Curve.ConditionalValueAtRisk"/> bit-exactly, a zero lower bound floors to the
    /// same domain bit-exactly, and a grid-exact power-law segment integrates to its closed form.
    /// </summary>
    [TestMethod]
    public void Test_QuantileTailIntegral_CVaRIdentityAndClosedForm()
    {
        // Arrange — the power-law curve X(p) = p^(−1/2) sampled on a log grid from 1e-16 to 1
        // (81 ordinates), so log-log interpolation reproduces X(p) exactly on grid segments.
        var probabilities = new double[81];
        var consequences = new double[81];
        for (int j = 0; j <= 80; j++)
        {
            double p = Math.Pow(10d, -16d + 0.2d * j);
            probabilities[j] = p;
            consequences[j] = Math.Pow(p, -0.5d);
        }
        var curve = new Curve
        {
            LECConsequences = consequences,
            LECProbabilities = probabilities,
            TotalProbability = 1d,
        };
        curve.ComputeRiskMeasures(consequenceThreshold: 10d, alpha: 0.01d);

        // Act
        double tail = curve.QuantileTailIntegral(1e-16, 0.01d);
        double floored = curve.QuantileTailIntegral(0d, 0.01d);
        double segment = curve.QuantileTailIntegral(1e-4, 1e-2);

        // Assert — bit-exact delegation identity, bit-exact lower-bound flooring, and the exact
        // closed form ∫ p^(−1/2) dp = 2(√1e-2 − √1e-4) = 0.18 over grid-knot bounds.
        Assert.AreEqual(curve.ConditionalValueAtRisk, tail / 0.01d, 0d,
            "The published CVaR must be QuantileTailIntegral(floor, α)/α bit-exactly.");
        Assert.AreEqual(tail, floored, 0d, "A zero lower bound must floor to 1e-16 bit-exactly.");
        Assert.AreEqual(0.18d, segment, 0.18d * 1e-12,
            "A grid-exact power-law segment must integrate to its closed form.");
    }

    /// <summary>
    /// Verifies sub-interval additivity of the quantile tail integral: splitting at an LEC knot
    /// with a single trailing contribution is bit-exact (the split reproduces the full walk's
    /// left-to-right accumulation), and an interior split agrees within floating-point
    /// re-association (the segment is evaluated as two closed-form pieces instead of one).
    /// </summary>
    [TestMethod]
    public void Test_QuantileTailIntegral_SubIntervalAdditivity()
    {
        // Arrange — a three-ordinate hand curve (descending consequences, ascending exceedances).
        var curve = new Curve
        {
            LECConsequences = new[] { 50d, 20d, 4d },
            LECProbabilities = new[] { 0.1d, 0.4d, 0.8d },
            TotalProbability = 0.8d,
        };

        // Act — one full pass, a knot split at 0.4 (one contribution after the split point), and
        // an interior split at 0.6 (inside the second log-log segment).
        double full = curve.QuantileTailIntegral(0.05d, 0.8d);
        double knotSplit = curve.QuantileTailIntegral(0.05d, 0.4d) + curve.QuantileTailIntegral(0.4d, 0.8d);
        double interiorSplit = curve.QuantileTailIntegral(0.05d, 0.6d) + curve.QuantileTailIntegral(0.6d, 0.8d);

        // Assert — the knot split is bit-exact; the interior split re-associates one segment's
        // closed form and lands within 1e-15 relative.
        Assert.AreEqual(full, knotSplit, 0d, "A knot split with one trailing term must be bit-exact.");
        Assert.AreEqual(full, interiorSplit, full * 1e-15,
            "An interior split must agree within floating-point re-association.");
    }

    /// <summary>
    /// Verifies the quantile tail integral's degenerate domains: an empty curve, a reversed or
    /// empty interval, and an interval wholly below the 1e-16 transform floor all return zero.
    /// </summary>
    [TestMethod]
    public void Test_QuantileTailIntegral_DegenerateDomains_Zero()
    {
        // Arrange
        var empty = new Curve();
        var curve = new Curve
        {
            LECConsequences = new[] { 50d, 20d, 4d },
            LECProbabilities = new[] { 0.1d, 0.4d, 0.8d },
            TotalProbability = 0.8d,
        };

        // Act / Assert
        Assert.AreEqual(0d, empty.QuantileTailIntegral(1e-16, 0.5d), 0d, "An empty curve carries no mass.");
        Assert.AreEqual(0d, curve.QuantileTailIntegral(0.5d, 0.5d), 0d, "An empty interval is zero.");
        Assert.AreEqual(0d, curve.QuantileTailIntegral(0.5d, 0.2d), 0d, "A reversed interval is zero.");
        Assert.AreEqual(0d, curve.QuantileTailIntegral(0d, 1e-16), 0d, "An interval at the floor is zero.");
    }
}
