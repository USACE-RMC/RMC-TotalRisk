using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.RiskFunctions.Transforms;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Nodes;

namespace RMC.TotalRisk.Tests.RiskFunctions.Responses.FaultTrees;

/// <summary>
/// Tests fault-tree probability sources evaluated on transformed hazard axes: exact gate algebra
/// at transformed lookups, chain sampler dimensions and content-derived clone streams, identity
/// movement, bivariate surface slices, and serialization.
/// </summary>
[TestClass]
public class FaultTreeTransformedSourceTests
{
    /// <summary>Verifies a transformed basic event feeds the exact gate algebra at t(h).</summary>
    [TestMethod]
    public void Test_SampleResponseFunction_TransformedEventUnderAnd_MatchesClosedForm()
    {
        // Arrange: P(top) = p(t(h)) · 0.5 under an And gate, with t(h) = 2 + 0.5·h against
        // t-space knots {2, 3} carrying {0.2, 0.6}.
        var tree = new FaultTree();
        var and = new FaultTreeGateNode("Both", FaultTreeGateType.And);
        tree.Add(tree.Root.Id, and);
        tree.Add(and.Id, new FaultTreeBasicEventNode("Transformed",
            new ProbabilitySource(TransformedTable(), new[] { Map() })));
        tree.Add(and.Id, new FaultTreeBasicEventNode("Plain", new ProbabilitySource(0.5d)));
        var response = ValidResponse(tree);

        // Act
        OrderedPairedData curve = response.SampleResponseFunction();

        // Assert: 0.2·0.5 at h = 0 and the midway 0.4·0.5 at h = 1.
        Assert.AreEqual(0.1d, curve[0].Y, 1e-14d);
        Assert.AreEqual(0.2d, curve[1].Y, 1e-14d);
    }

    /// <summary>Verifies chain dimensions extend the unified variable slot.</summary>
    [TestMethod]
    public void Test_SetupSampler_TransformedSource_CountsChainDimensions()
    {
        // Arrange
        var response = TransformedUncertainResponse();

        // Assert: one local table dimension plus one uncertain-transform dimension.
        Assert.AreEqual(2, response.SamplingDimensions);
        response.SetupSampler(16, 12345, SamplingScheme.LatinHypercube);
        _ = response.SampleResponseFunction(3);
    }

    /// <summary>
    /// Verifies realization sampling composes the variable's content-seeded transform-clone
    /// stream with the local table percentile — bit-equal to an independently constructed
    /// expectation from the documented seed recipe.
    /// </summary>
    [TestMethod]
    public void Test_SampleResponseFunction_Realization_MatchesContentSeededExpectation()
    {
        // Arrange
        int sampleSize = 16;
        int seed = 20260906;
        var response = TransformedUncertainResponse();
        var basic = response.FaultTree.Nodes.OfType<FaultTreeBasicEventNode>().Single();
        ProbabilitySource source = basic.ProbabilitySource;
        response.SetupSampler(sampleSize, seed, SamplingScheme.LatinHypercube);

        // The documented recipe: the variable's child-stream base uses its unified ordinal, and
        // chain entry i forks from that base with the transform's content hash and position.
        byte[] tokenBytes = Convert.FromHexString(source.CanonicalToken());
        int chainBase = SeedHelpers.HashCombine(seed, tokenBytes, 0);
        var transform = (LinearTransform)source.HazardTransforms[0]!;
        var expectedTransform = UncertainMap();
        expectedTransform.SetupSampler(sampleSize,
            SeedHelpers.HashCombine(chainBase, transform.CanonicalHash(), 0), SamplingScheme.LatinHypercube);

        // Act / Assert: realization-for-realization bit equality on the top curve.
        for (int realization = 0; realization < sampleSize; realization++)
        {
            double localPercentile = response.SampledPercentile(realization, 0);
            double transformed = expectedTransform.SampleFunction(realization).Function(1d);
            double expected = source.Table!.CurveSample(localPercentile).GetYFromX(transformed);
            OrderedPairedData actual = response.SampleResponseFunction(realization);
            Assert.AreEqual(expected, actual[1].Y);
        }
    }

    /// <summary>Verifies chain content edits move the response hash while renames stay inert.</summary>
    [TestMethod]
    public void Test_CanonicalHash_ChainEditMoves_RenameInert()
    {
        // Arrange
        var tree = new FaultTree();
        tree.Add(tree.Root.Id, new FaultTreeBasicEventNode("Transformed",
            new ProbabilitySource(TransformedTable(), new[] { Map() })));
        var response = ValidResponse(tree);
        var basic = response.FaultTree.Nodes.OfType<FaultTreeBasicEventNode>().Single();
        var transform = (LinearTransform)basic.ProbabilitySource.HazardTransforms[0]!;
        byte[] baseline = response.CanonicalHash();

        // Act / Assert
        transform.Name = "Renamed map";
        CollectionAssert.AreEqual(baseline, response.CanonicalHash());
        transform.Alpha = 5d;
        CollectionAssert.AreNotEqual(baseline, response.CanonicalHash());
    }

    /// <summary>Verifies a bivariate surface source evaluates the declared-axis slice through the tree.</summary>
    [TestMethod]
    public void Test_SampleResponseFunction_BivariateAxisSource_MatchesSurfaceSlice()
    {
        // Arrange: the tree hazard drives the secondary axis; t(h) = 0.25 + 0.5·h supplies the
        // primary coordinate.
        var surface = Surface();
        var map = Map(alpha: 0.25d, beta: 0.5d);
        var tree = new FaultTree();
        tree.Add(tree.Root.Id, new FaultTreeBasicEventNode("Breach",
            new ProbabilitySource(surface, BivariateSourceAxis.Secondary, new[] { map })));
        var response = ValidResponse(tree);
        response.SetupSampler(4, 12345, SamplingScheme.LatinHypercube);

        // Act / Assert: mean and realization both reproduce the clamped surface slice.
        OrderedPairedData curve = response.SampleResponseFunction();
        OrderedPairedData realization = response.SampleResponseFunction(1);
        for (int h = 0; h < 2; h++)
        {
            double hazard = h;
            double expected = surface.SurfaceProbability(0.25d + 0.5d * hazard, hazard);
            Assert.AreEqual(expected, curve[h].Y, 1e-14d);
            Assert.AreEqual(expected, realization[h].Y, 1e-14d);
        }
    }

    /// <summary>Verifies a transformed source round-trips through tree XML with identical results.</summary>
    [TestMethod]
    public void Test_XmlRoundTrip_TransformedSource_PreservesResultsAndHash()
    {
        // Arrange
        var response = TransformedUncertainResponse();

        // Act
        var restored = new FaultTreeResponse(response.ToXElement());
        restored.SetupSampler(8, 777, SamplingScheme.LatinHypercube);
        response.SetupSampler(8, 777, SamplingScheme.LatinHypercube);

        // Assert
        CollectionAssert.AreEqual(response.CanonicalHash(), restored.CanonicalHash());
        for (int realization = 0; realization < 8; realization++)
        {
            OrderedPairedData expected = response.SampleResponseFunction(realization);
            OrderedPairedData actual = restored.SampleResponseFunction(realization);
            for (int h = 0; h < expected.Count; h++)
                Assert.AreEqual(expected[h].Y, actual[h].Y);
        }
    }

    /// <summary>Builds the shared deterministic linear map onto the Duration axis.</summary>
    /// <param name="alpha">The intercept.</param>
    /// <param name="beta">The slope.</param>
    /// <returns>The transform.</returns>
    private static LinearTransform Map(double alpha = 2d, double beta = 0.5d)
    {
        return new LinearTransform
        {
            Name = "Duration map",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            TransformedHazard = "Duration",
            TransformedHazardUnit = "hr",
            Minimum = -100d,
            Maximum = 100d,
            Alpha = alpha,
            Beta = beta,
            IsUncertain = false,
        };
    }

    /// <summary>Builds the shared uncertain linear map onto the Duration axis.</summary>
    /// <returns>The transform.</returns>
    private static LinearTransform UncertainMap()
    {
        var map = Map();
        map.Sigma = 0.25d;
        map.IsUncertain = true;
        return map;
    }

    /// <summary>Builds a t-space deterministic probability table.</summary>
    /// <returns>The table.</returns>
    private static UncertainOrderedPairedData TransformedTable()
    {
        return new UncertainOrderedPairedData(
            new[]
            {
                new UncertainOrdinate(2d, new Deterministic(0.2d)),
                new UncertainOrdinate(3d, new Deterministic(0.6d)),
            }, true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic);
    }

    /// <summary>Builds a single transformed uncertain basic event under the root gate.</summary>
    /// <returns>The response.</returns>
    private static FaultTreeResponse TransformedUncertainResponse()
    {
        var table = new UncertainOrderedPairedData(
            new[]
            {
                new UncertainOrdinate(2d, new Uniform(0.1d, 0.3d)),
                new UncertainOrdinate(3d, new Uniform(0.4d, 0.8d)),
            }, true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Uniform);
        var tree = new FaultTree();
        tree.Add(tree.Root.Id, new FaultTreeBasicEventNode("Transformed",
            new ProbabilitySource(table, new[] { UncertainMap() })));
        return ValidResponse(tree);
    }

    /// <summary>Builds a valid 2×2 bivariate response surface on (Stage, Duration).</summary>
    /// <returns>The surface.</returns>
    private static BivariateResponse Surface()
    {
        var surface = new BivariateResponse
        {
            Name = "Surface",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SecondarySpecifiedHazard = "Duration",
            SecondaryHazardUnit = "hr",
        };
        surface.PrimaryHazardLevels.Clear();
        surface.PrimaryHazardLevels.Add(0d);
        surface.PrimaryHazardLevels.Add(1d);
        surface.SecondaryHazardLevels.Clear();
        surface.SecondaryHazardLevels.Add(new WeightedHazardLevel { Level = 0d, Weight = 0.5d });
        surface.SecondaryHazardLevels.Add(new WeightedHazardLevel { Level = 2d, Weight = 0.5d });
        surface.ProbabilityValues = new[,] { { 0.1d, 0.3d }, { 0.5d, 0.9d } };
        return surface;
    }

    /// <summary>Wraps a tree in a valid two-level response.</summary>
    /// <param name="tree">The authored tree.</param>
    /// <returns>The response.</returns>
    private static FaultTreeResponse ValidResponse(FaultTree tree)
    {
        return new FaultTreeResponse(new[] { 0d, 1d }, tree)
        {
            Name = "Fault tree",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
    }
}
