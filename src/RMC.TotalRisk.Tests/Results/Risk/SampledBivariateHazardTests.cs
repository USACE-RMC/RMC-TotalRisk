using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Distributions;
using Numerics.Distributions.Copulas;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results.Risk;
using RMC.TotalRisk.RiskFunctions.Hazards;

namespace RMC.TotalRisk.Tests.Results.Risk;

/// <summary>
/// Unit tests for <see cref="SampledBivariateHazard"/> — the conditional-trapezoid discretization
/// kernel: node geometry with the pinned endpoint clamps, exact residual-absorbed weight sums, the
/// independence identity, the conditional round trip through a dependent copula, and the buffer
/// contract. Snapshots are obtained through a configured <see cref="BivariateHazard"/>, the only
/// production construction path, so the hazard's precomputed vectors are what is under test.
/// </summary>
[TestClass]
public class SampledBivariateHazardTests
{
    #region Fixtures

    /// <summary>
    /// Builds a set-up bivariate hazard whose Y marginal is Uniform(0, 1) — the identity quantile
    /// function, so every kernel output is an exact closed-form value — under the given copula.
    /// </summary>
    /// <param name="copula">The copula; null selects independence.</param>
    /// <param name="bins">The secondary integration bin count.</param>
    /// <returns>The set-up hazard.</returns>
    private static BivariateHazard UniformFixture(BivariateCopula? copula = null, int bins = 20)
    {
        var hazard = new BivariateHazard(
            Marginal("X", 0d, 4d),
            Marginal("Y", 0d, 1d),
            copula)
        {
            SpecifiedHazard = "Peak Ground Acceleration",
            HazardUnit = "g",
            SecondarySpecifiedHazard = "Pool Duration",
            SecondaryHazardUnit = "days",
            SecondaryIntegrationBins = bins,
        };
        hazard.SetupSampler(8, 12345, SamplingScheme.LatinHypercube);
        return hazard;
    }

    /// <summary>Builds a deterministic parametric marginal over a Uniform parent.</summary>
    /// <param name="name">The function name.</param>
    /// <param name="min">The uniform minimum.</param>
    /// <param name="max">The uniform maximum.</param>
    /// <returns>The marginal.</returns>
    private static ParametricUnivariateHazard Marginal(string name, double min, double max)
    {
        var marginal = new ParametricUnivariateHazard
        {
            Name = name,
            ParentDistribution = new Uniform(min, max),
            IsUncertain = false,
            SpecifiedHazard = "Hazard",
            HazardUnit = "unit",
        };
        marginal.Estimate();
        return marginal;
    }

    /// <summary>Fills fresh buffers from a snapshot at the given conditioning probability.</summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="u">The primary non-exceedance probability.</param>
    /// <returns>The filled node and weight buffers.</returns>
    private static (double[] YNodes, double[] Weights) Fill(SampledBivariateHazard snapshot, double u)
    {
        var yNodes = new double[snapshot.ConditionalNodeCount];
        var weights = new double[snapshot.ConditionalNodeCount];
        snapshot.FillConditionalBins(u, yNodes, weights);
        return (yNodes, weights);
    }

    #endregion

    /// <summary>
    /// The single-node inversion reproduces the corresponding grid node bit-exactly (the two
    /// paths share the clamp constants and the inversion expression), clamps out-of-band
    /// conditional probabilities onto the grid's own endpoint values, and guards the
    /// conditioning probability like the grid fill.
    /// </summary>
    [TestMethod]
    public void Test_InverseConditional_MatchesGridNodesBitExactly()
    {
        // Arrange
        var hazard = UniformFixture(bins: 20);
        var snapshot = hazard.SampleBivariate(0);
        var (yNodes, _) = Fill(snapshot, 0.37d);

        // Act / Assert — every grid node t_j = j/N (endpoint-clamped) reproduces bit-exactly.
        for (int j = 0; j <= 20; j++)
        {
            double t = Math.Clamp(j / 20d, 1e-16, 1d - 1e-16);
            Assert.AreEqual(yNodes[j], snapshot.InverseConditional(0.37d, t), 0d, $"node {j}");
        }

        // Out-of-band conditional probabilities clamp onto the endpoint values.
        Assert.AreEqual(yNodes[0], snapshot.InverseConditional(0.37d, -0.5d), 0d);
        Assert.AreEqual(yNodes[20], snapshot.InverseConditional(0.37d, 1.5d), 0d);

        // The conditioning probability keeps the grid fill's guard.
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => snapshot.InverseConditional(0d, 0.5d));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => snapshot.InverseConditional(1d, 0.5d));
    }

    /// <summary>N bins fill N + 1 nodes with the trapezoid weight pattern.</summary>
    [TestMethod]
    public void Test_FillConditionalBins_NodeCountAndWeights()
    {
        // Arrange
        var snapshot = UniformFixture(bins: 20).SampleBivariate(0);

        // Act
        var (_, weights) = Fill(snapshot, 0.5d);

        // Assert — half weights at the ends, uniform interior.
        Assert.AreEqual(21, snapshot.ConditionalNodeCount);
        Assert.AreEqual(20, snapshot.SecondaryIntegrationBins);
        Assert.AreEqual(1d / 40d, weights[0], 0d);
        for (int j = 1; j < 20; j++)
        {
            Assert.AreEqual(1d / 20d, weights[j], 0d, $"interior weight {j}");
        }
        // The last weight is the exact residual, not the literal 1/(2N) — it may sit a few ulps
        // away so that the ordered SUM (the property that matters) is exactly one.
        Assert.AreEqual(1d / 40d, weights[20], 1e-14);
    }

    /// <summary>
    /// The ordered weight sum is exactly one at every legal bin count probed — the last weight is
    /// the exact residual (Sterbenz-exact for the running sums these counts produce), which the
    /// engine's exhaustive-mass discipline relies on.
    /// </summary>
    [TestMethod]
    public void Test_FillConditionalBins_WeightSum_IsExactlyOne()
    {
        foreach (int bins in new[] { 3, 7, 20, 333, 1000 })
        {
            // Arrange
            var snapshot = UniformFixture(bins: bins).SampleBivariate(0);

            // Act
            var (_, weights) = Fill(snapshot, 0.25d);
            double sum = 0d;
            for (int j = 0; j < snapshot.ConditionalNodeCount; j++)
            {
                sum += weights[j];
            }

            // Assert
            Assert.AreEqual(1d, sum, 0d, $"bins = {bins}");
        }
    }

    /// <summary>
    /// The endpoint nodes are clamped to the pinned probability floor: with the identity Y
    /// quantile the first and last conditional values are exactly 1e-16 and 1 − 1e-16.
    /// </summary>
    [TestMethod]
    public void Test_FillConditionalBins_EndpointClamps()
    {
        // Arrange
        var snapshot = UniformFixture(bins: 20).SampleBivariate(0);

        // Act
        var (yNodes, _) = Fill(snapshot, 0.5d);

        // Assert
        Assert.AreEqual(1e-16, yNodes[0], 0d);
        Assert.AreEqual(1d - 1e-16, yNodes[20], 0d);
    }

    /// <summary>
    /// Under independence the conditional inversion is the identity, so the interior nodes are the
    /// plain marginal quantiles at t_j = j/N — for the identity marginal, exactly j/N; for a
    /// Normal marginal, exactly its quantile at j/N.
    /// </summary>
    [TestMethod]
    public void Test_FillConditionalBins_Independence_YieldsMarginalQuantiles()
    {
        // Arrange — the identity marginal.
        const int bins = 20;
        var snapshot = UniformFixture(bins: bins).SampleBivariate(0);

        // Act
        var (yNodes, _) = Fill(snapshot, 0.37d);

        // Assert — interior nodes are t_j exactly, at ANY conditioning probability.
        for (int j = 1; j < bins; j++)
        {
            Assert.AreEqual((double)j / bins, yNodes[j], 0d, $"node {j}");
        }

        // Arrange — a Normal(10, 2) Y marginal behind the same independence coupling.
        var normalY = new ParametricUnivariateHazard
        {
            Name = "Y",
            ParentDistribution = new Normal(10d, 2d),
            IsUncertain = false,
            SpecifiedHazard = "Pool Duration",
            HazardUnit = "days",
        };
        normalY.Estimate();
        var hazard = new BivariateHazard(Marginal("X", 0d, 4d), normalY)
        {
            SpecifiedHazard = "Peak Ground Acceleration",
            HazardUnit = "g",
            SecondarySpecifiedHazard = "Pool Duration",
            SecondaryHazardUnit = "days",
        };
        hazard.SetupSampler(8, 12345, SamplingScheme.LatinHypercube);
        var normalSnapshot = hazard.SampleBivariate(0);
        var reference = new Normal(10d, 2d);

        // Act
        var (normalNodes, _) = Fill(normalSnapshot, 0.37d);

        // Assert
        for (int j = 1; j < bins; j++)
        {
            Assert.AreEqual(reference.InverseCDF((double)j / bins), normalNodes[j], 0d, $"Normal node {j}");
        }
    }

    /// <summary>
    /// Under a dependent copula the produced nodes invert the conditional distribution: pushing a
    /// node back through the copula's forward conditional recovers t_j, and the nodes adapt to the
    /// conditioning probability.
    /// </summary>
    [TestMethod]
    public void Test_FillConditionalBins_Clayton_RoundTripsConditionalCDF()
    {
        // Arrange — Clayton θ = 2 over the identity Y marginal, so y_j IS v_j.
        const int bins = 20;
        const double u = 0.3d;
        var copula = new ClaytonCopula(2d);
        var snapshot = UniformFixture(new ClaytonCopula(2d), bins).SampleBivariate(0);

        // Act
        var (yNodes, _) = Fill(snapshot, u);

        // Assert — the conditional round trip closes at every interior node, and the node set is
        // strictly ascending (a distribution's quantiles must be).
        for (int j = 1; j < bins; j++)
        {
            double t = (double)j / bins;
            Assert.AreEqual(t, copula.ConditionalCDF(u, yNodes[j]), 1e-9, $"node {j}");
            Assert.IsTrue(yNodes[j] > yNodes[j - 1], $"node {j} must ascend");
        }

        // Assert — dependence actually engaged: a different conditioning probability moves the
        // interior nodes (under independence it would not).
        var (shifted, _) = Fill(snapshot, 0.8d);
        Assert.AreNotEqual(yNodes[10], shifted[10]);
    }

    /// <summary>The buffer contract: nulls, short buffers, and out-of-range u all throw.</summary>
    [TestMethod]
    public void Test_FillConditionalBins_BufferGuards()
    {
        // Arrange
        var snapshot = UniformFixture(bins: 20).SampleBivariate(0);
        var good = new double[snapshot.ConditionalNodeCount];
        var short20 = new double[20];

        // Act / Assert
        Assert.ThrowsException<ArgumentNullException>(() => snapshot.FillConditionalBins(0.5d, null!, good));
        Assert.ThrowsException<ArgumentNullException>(() => snapshot.FillConditionalBins(0.5d, good, null!));
        Assert.ThrowsException<ArgumentException>(() => snapshot.FillConditionalBins(0.5d, short20, good));
        Assert.ThrowsException<ArgumentException>(() => snapshot.FillConditionalBins(0.5d, good, short20));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => snapshot.FillConditionalBins(0d, good, good));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => snapshot.FillConditionalBins(1d, good, good));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => snapshot.FillConditionalBins(double.NaN, good, good));

        // Assert — oversized buffers are legal; entries beyond the node count are untouched.
        var oversized = new double[snapshot.ConditionalNodeCount + 5];
        oversized[^1] = -99d;
        snapshot.FillConditionalBins(0.5d, oversized, new double[oversized.Length]);
        Assert.AreEqual(-99d, oversized[^1]);
    }

    /// <summary>The snapshot exposes its construction: the sampled Y marginal and the cloned copula.</summary>
    [TestMethod]
    public void Test_Snapshot_PropertiesExposeConstruction()
    {
        // Arrange
        var hazard = UniformFixture(new ClaytonCopula(2d), bins: 12);

        // Act
        var snapshot = hazard.SampleBivariate(1);

        // Assert
        Assert.AreEqual(12, snapshot.SecondaryIntegrationBins);
        Assert.AreEqual(13, snapshot.ConditionalNodeCount);
        Assert.IsInstanceOfType<ClaytonCopula>(snapshot.Copula);
        Assert.AreNotSame(hazard.Copula, snapshot.Copula);
        Assert.AreEqual(0.5d, snapshot.MarginalY.InverseCDF(0.5d), 0d);
    }

    /// <summary>The internal constructor rejects null arguments.</summary>
    [TestMethod]
    public void Test_Constructor_NullArguments_Throw()
    {
        // Arrange
        var marginal = new Uniform(0d, 1d);
        var copula = new IndependenceCopula();
        var nodes = new[] { 1e-16, 0.5d, 1d - 1e-16 };
        var weights = new[] { 0.25d, 0.5d, 0.25d };

        // Act / Assert
        Assert.ThrowsException<ArgumentNullException>(() => new SampledBivariateHazard(null!, copula, nodes, weights));
        Assert.ThrowsException<ArgumentNullException>(() => new SampledBivariateHazard(marginal, null!, nodes, weights));
        Assert.ThrowsException<ArgumentNullException>(() => new SampledBivariateHazard(marginal, copula, null!, weights));
        Assert.ThrowsException<ArgumentNullException>(() => new SampledBivariateHazard(marginal, copula, nodes, null!));
    }
}
