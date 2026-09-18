using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Analyses.CostBenefit.Support;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Tests.Analyses.CostBenefit.Support;

/// <summary>
/// Tests the stochastic-dominance screens: the aleatory verdict matrix over loss-exceedance
/// curves (clean first order, identity, second-order-only through an interior crossing) and
/// the epistemic weighted-sample variant (first order, second-order-only, non-comparable).
/// </summary>
[TestClass]
public class StochasticDominanceEngineTests
{
    /// <summary>Builds a loss-exceedance curve from parallel arrays.</summary>
    /// <param name="consequences">The consequences, descending.</param>
    /// <param name="probabilities">The exceedance probabilities, ascending.</param>
    /// <param name="totalProbability">The total probability.</param>
    /// <returns>The curve.</returns>
    private static Curve Build(double[] consequences, double[] probabilities, double totalProbability)
    {
        return new Curve
        {
            LECConsequences = consequences,
            LECProbabilities = probabilities,
            TotalProbability = totalProbability,
        };
    }

    /// <summary>
    /// Verifies clean first-order dominance (one curve everywhere at or below the other with
    /// a strict gap) and the identity verdict on equal curves.
    /// </summary>
    [TestMethod]
    public void Test_CompareLossExceedanceCurves_FirstOrderAndIdentical()
    {
        // Arrange — the second curve doubles every consequence at the same exceedances, so
        // its exceedance at any consequence is at least the first's.
        var smaller = Build(new[] { 10d, 1d, 0d }, new[] { 0.001d, 0.1d, 0.1d }, 0.1d);
        var larger = Build(new[] { 20d, 2d, 0d }, new[] { 0.001d, 0.1d, 0.1d }, 0.1d);
        var twin = Build(new[] { 10d, 1d, 0d }, new[] { 0.001d, 0.1d, 0.1d }, 0.1d);

        // Act / Assert
        Assert.AreEqual(DominanceVerdict.FirstDominatesFirstOrder,
            StochasticDominanceEngine.CompareLossExceedanceCurves(smaller, larger));
        Assert.AreEqual(DominanceVerdict.SecondDominatesFirstOrder,
            StochasticDominanceEngine.CompareLossExceedanceCurves(larger, smaller));
        Assert.AreEqual(DominanceVerdict.Identical,
            StochasticDominanceEngine.CompareLossExceedanceCurves(smaller, twin));
    }

    /// <summary>
    /// Verifies second-order-only dominance through an interior exceedance crossing: a
    /// concentrated loss against a spread with a slightly larger mean crosses in exceedance
    /// (no first order) while every stop-loss favors the concentrated curve.
    /// </summary>
    [TestMethod]
    public void Test_CompareLossExceedanceCurves_SecondOrderOnly_InteriorCrossing()
    {
        // Arrange — A concentrates 0.2 at 50; B spreads 0.1 near 92 and 0.1 near 10 (mean
        // 10.2 > A's 10). The exceedance curves cross between the 10 and 50 knots.
        var concentrated = Build(
            new[] { 50.0000005d, 50d, 0d },
            new[] { 1e-12, 0.2d, 0.2d }, 0.2d);
        var spread = Build(
            new[] { 92.0000009d, 92d, 10d, 0d },
            new[] { 1e-12, 0.1d, 0.2d, 0.2d }, 0.2d);

        // Act
        var verdict = StochasticDominanceEngine.CompareLossExceedanceCurves(concentrated, spread);

        // Assert
        Assert.AreEqual(DominanceVerdict.FirstDominatesSecondOrder, verdict,
            "The concentrated loss must dominate at second order only.");
    }

    /// <summary>
    /// Verifies the epistemic weighted-sample screen: pointwise first order, the
    /// certain-versus-spread second order, and a non-comparable pair.
    /// </summary>
    [TestMethod]
    public void Test_CompareWeightedSamples_VerdictMatrix()
    {
        // Act / Assert — pointwise better under Minimize: first order.
        Assert.AreEqual(DominanceVerdict.FirstDominatesFirstOrder,
            StochasticDominanceEngine.CompareWeightedSamples(
                new[] { 1d, 3d }, new[] { 1d, 1d }, 2,
                new[] { 2d, 4d }, new[] { 1d, 1d }, 2, ObjectiveDirection.Minimize));

        // A certain loss against a spread with a worse mean: second order only.
        Assert.AreEqual(DominanceVerdict.FirstDominatesSecondOrder,
            StochasticDominanceEngine.CompareWeightedSamples(
                new[] { 5d, 5d }, new[] { 1d, 1d }, 2,
                new[] { 2d, 9d }, new[] { 1d, 1d }, 2, ObjectiveDirection.Minimize));

        // Better mean against smaller spread with the means crossing the integrals: none.
        Assert.AreEqual(DominanceVerdict.None,
            StochasticDominanceEngine.CompareWeightedSamples(
                new[] { 0d, 10d }, new[] { 1d, 1d }, 2,
                new[] { 4d, 7d }, new[] { 1d, 1d }, 2, ObjectiveDirection.Minimize));

        // Identical weighted distributions.
        Assert.AreEqual(DominanceVerdict.Identical,
            StochasticDominanceEngine.CompareWeightedSamples(
                new[] { 2d, 6d }, new[] { 1d, 3d }, 2,
                new[] { 6d, 2d }, new[] { 3d, 1d }, 2, ObjectiveDirection.Minimize));

        // Empty samples are not comparable.
        Assert.AreEqual(DominanceVerdict.None,
            StochasticDominanceEngine.CompareWeightedSamples(
                new double[0], new double[0], 0,
                new[] { 1d }, new[] { 1d }, 1, ObjectiveDirection.Minimize));
    }

    /// <summary>
    /// Verifies the Maximize orientation flips the epistemic verdict sides.
    /// </summary>
    [TestMethod]
    public void Test_CompareWeightedSamples_MaximizeOrientation()
    {
        // Act — under Maximize the larger values win, so the second sample dominates.
        var verdict = StochasticDominanceEngine.CompareWeightedSamples(
            new[] { 1d, 3d }, new[] { 1d, 1d }, 2,
            new[] { 2d, 4d }, new[] { 1d, 1d }, 2, ObjectiveDirection.Maximize);

        // Assert
        Assert.AreEqual(DominanceVerdict.SecondDominatesFirstOrder, verdict);
    }
}
