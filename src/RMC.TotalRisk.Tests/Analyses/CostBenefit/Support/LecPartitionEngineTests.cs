using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Analyses.CostBenefit.Support;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Tests.Analyses.CostBenefit.Support;

/// <summary>
/// Tests the loss-exceedance partition arithmetic: region means with the exact
/// conditional-value-at-risk consistency at the low-probability tail, the width partition,
/// regions above the total exceedance, and the certainty-equivalent closed forms with their
/// expected-value limits and overflow safety.
/// </summary>
[TestClass]
public class LecPartitionEngineTests
{
    /// <summary>Builds the exact power-law curve X(p) = p^(−1/2) on a log grid.</summary>
    /// <returns>The curve.</returns>
    private static Curve PowerLawCurve()
    {
        var probabilities = new double[81];
        var consequences = new double[81];
        for (int j = 0; j <= 80; j++)
        {
            double p = Math.Pow(10d, -16d + 0.2d * j);
            probabilities[j] = p;
            consequences[j] = Math.Pow(p, -0.5d);
        }
        return new Curve
        {
            LECConsequences = consequences,
            LECProbabilities = probabilities,
            TotalProbability = 1d,
        };
    }

    /// <summary>
    /// Verifies the region means: dyadic boundaries partition the exceedance axis with widths
    /// summing exactly to one, and the low-probability high-consequence region reproduces the
    /// curve's conditional value-at-risk at its boundary bit-exactly.
    /// </summary>
    [TestMethod]
    public void Test_RegionConditionalMeans_PartitionAndCVaRPin()
    {
        // Arrange — dyadic boundaries so the telescoped widths are exact.
        var curve = PowerLawCurve();
        var boundaries = new[] { 0.25d, 0.0625d };

        // Act
        double[] means = LecPartitionEngine.RegionConditionalMeans(curve, boundaries);
        curve.ComputeRiskMeasures(consequenceThreshold: double.NaN, alpha: 0.0625d);

        // Assert — three regions; the widths (1−0.25) + (0.25−0.0625) + 0.0625 sum exactly to
        // one; each mean is the tail-integral difference over its width; the tail region is
        // the published CVaR at its boundary, bit for bit (the shared integral authority).
        Assert.AreEqual(3, means.Length);
        Assert.AreEqual(1d, (1d - 0.25d) + (0.25d - 0.0625d) + 0.0625d, 0d);
        Assert.AreEqual(curve.QuantileTailIntegral(0.25d, 1d) / 0.75d, means[0], 0d);
        Assert.AreEqual(curve.QuantileTailIntegral(0.0625d, 0.25d) / (0.25d - 0.0625d), means[1], 0d);
        Assert.AreEqual(curve.ConditionalValueAtRisk, means[2], 0d,
            "The (0, b] region mean must be the curve's CVaR at b bit-exactly.");
    }

    /// <summary>
    /// Verifies a region whose lower bound sits at or above the curve's total exceedance is
    /// NaN, while a straddling region still integrates, and an empty curve reports NaN
    /// throughout.
    /// </summary>
    [TestMethod]
    public void Test_RegionConditionalMeans_AboveTotalExceedance()
    {
        // Arrange — a defective curve with total probability 0.1 (the terminal ordinate is
        // zero consequence at 0.1).
        var curve = new Curve
        {
            LECConsequences = new[] { 50d, 20d, 0d },
            LECProbabilities = new[] { 0.001d, 0.05d, 0.1d },
            TotalProbability = 0.1d,
        };

        // Act — the top region (0.5, 1] lies wholly above the total exceedance.
        double[] means = LecPartitionEngine.RegionConditionalMeans(curve, new[] { 0.5d, 0.05d });
        double[] empty = LecPartitionEngine.RegionConditionalMeans(new Curve(), new[] { 0.5d });

        // Assert
        Assert.IsTrue(double.IsNaN(means[0]), "A region at or above the total exceedance has no losses.");
        Assert.AreEqual(curve.QuantileTailIntegral(0.05d, 0.5d) / 0.45d, means[1], 0d,
            "A straddling region integrates its real mass over its full width.");
        Assert.IsTrue(double.IsNaN(empty[0]) && double.IsNaN(empty[1]));
    }

    /// <summary>
    /// Verifies the certainty-equivalent closed forms on a two-point loss (an atom at zero
    /// and a point mass), the expected-value limits at vanishing risk aversion, and the
    /// constant-loss identity.
    /// </summary>
    [TestMethod]
    public void Test_CertaintyEquivalent_ClosedFormsAndLimits()
    {
        // Arrange — mass 0.2 at consequence 100, atom 0.8 at zero: the flat segment between
        // equal-consequence ordinates makes the discrete convention exact.
        var twoPoint = new Curve
        {
            LECConsequences = new[] { 100d, 100d },
            LECProbabilities = new[] { 0d, 0.2d },
            TotalProbability = 0.2d,
        };
        var constant = new Curve
        {
            LECConsequences = new[] { 50d, 50d },
            LECProbabilities = new[] { 0d, 1d },
            TotalProbability = 1d,
        };

        // Act / Assert — CARA: CE = (1/θ)·ln(0.8 + 0.2·e^{100θ}).
        double theta = 0.01d;
        double expectedCara = Math.Log(0.8d + 0.2d * Math.Exp(100d * theta)) / theta;
        double cara = LecPartitionEngine.CertaintyEquivalent(twoPoint,
            UtilityFunctionForm.ExponentialCara, theta);
        Assert.AreEqual(expectedCara, cara, Math.Abs(expectedCara) * 1e-12);

        // Power CRRA: CE = (0.2·100^(1+γ))^(1/(1+γ)) at γ = 1.
        double crra = LecPartitionEngine.CertaintyEquivalent(twoPoint,
            UtilityFunctionForm.PowerCrra, 1d);
        Assert.AreEqual(Math.Sqrt(0.2d * 10000d), crra, Math.Sqrt(2000d) * 1e-12);

        // The expected-value limits: vanishing risk aversion approaches EV = 20.
        Assert.AreEqual(20d, LecPartitionEngine.CertaintyEquivalent(twoPoint,
            UtilityFunctionForm.ExponentialCara, 1e-8), 1e-4);
        Assert.AreEqual(20d, LecPartitionEngine.CertaintyEquivalent(twoPoint,
            UtilityFunctionForm.PowerCrra, 1e-9), 1e-5);

        // A certain loss is its own certainty equivalent under both families.
        Assert.AreEqual(50d, LecPartitionEngine.CertaintyEquivalent(constant,
            UtilityFunctionForm.ExponentialCara, 0.05d), 50d * 1e-12);
        Assert.AreEqual(50d, LecPartitionEngine.CertaintyEquivalent(constant,
            UtilityFunctionForm.PowerCrra, 2d), 50d * 1e-12);
    }

    /// <summary>
    /// Verifies the exponential family's shifted log-space evaluation survives parameters
    /// that overflow the naive exponential, and the no-loss and unmeasurable degenerates.
    /// </summary>
    [TestMethod]
    public void Test_CertaintyEquivalent_OverflowAndDegenerates()
    {
        // Arrange — θ·c = 10,000 overflows e^{θc} directly; the shifted form is finite.
        var curve = new Curve
        {
            LECConsequences = new[] { 1000d, 1000d },
            LECProbabilities = new[] { 0d, 0.2d },
            TotalProbability = 0.2d,
        };

        // Act
        double cara = LecPartitionEngine.CertaintyEquivalent(curve,
            UtilityFunctionForm.ExponentialCara, 10d);

        // Assert — CE = 1000 + (1/10)·ln(0.2 + 0.8·e^{−10000}) = 1000 + ln(0.2)/10.
        double expected = 1000d + Math.Log(0.2d) / 10d;
        Assert.AreEqual(expected, cara, Math.Abs(expected) * 1e-12);

        // A curve with no recorded losses and zero total probability is a certain zero; an
        // unmeasurable curve is NaN.
        Assert.AreEqual(0d, LecPartitionEngine.CertaintyEquivalent(
            new Curve { TotalProbability = 0d }, UtilityFunctionForm.ExponentialCara, 1d), 0d);
        Assert.IsTrue(double.IsNaN(LecPartitionEngine.CertaintyEquivalent(
            new Curve { TotalProbability = double.NaN }, UtilityFunctionForm.PowerCrra, 1d)));
    }
}
