using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.RiskFunctions.Transforms;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees.Nodes;

namespace RMC.TotalRisk.Tests.RiskFunctions.Responses.EventTrees;

/// <summary>
/// Tests event-tree probability sources evaluated on transformed hazard axes: path products at
/// transformed lookups, chain sampler dimensions and content-derived clone streams, live-edit
/// plan invalidation, identity movement, bivariate surface slices, and serialization.
/// </summary>
[TestClass]
public class EventTreeTransformedSourceTests
{
    /// <summary>Verifies a transformed tabular source interpolates at t(h) inside the path product.</summary>
    [TestMethod]
    public void Test_SampleResponseFunction_TransformedTable_InterpolatesAtTransformedHazard()
    {
        // Arrange: t(h) = 2 + 0.5·h maps the tree axis {0, 1} onto {2, 2.5} against t-space
        // knots {2, 3} carrying probabilities {0.2, 0.6}.
        var response = TransformedTableResponse(uncertainTransform: false);

        // Act
        OrderedPairedData curve = response.SampleResponseFunction();

        // Assert: 0.2 at t = 2 exactly, 0.4 midway between the knots at t = 2.5.
        Assert.AreEqual(0.2d, curve[0].Y, 1e-14d);
        Assert.AreEqual(0.4d, curve[1].Y, 1e-14d);
    }

    /// <summary>Verifies chain dimensions extend the sampling class and flatten observably.</summary>
    [TestMethod]
    public void Test_SetupSampler_TransformedSource_CountsChainDimensions()
    {
        // Arrange: one local table dimension plus one uncertain-transform dimension.
        var uncertain = TransformedUncertainResponse();
        var deterministic = TransformedTableResponse(uncertainTransform: false);

        // Assert
        Assert.AreEqual(2, uncertain.SamplingDimensions);
        Assert.AreEqual(1, deterministic.SamplingDimensions);
        uncertain.SetupSampler(16, 12345, SamplingScheme.LatinHypercube);
        _ = uncertain.SampleResponseFunction(7);
    }

    /// <summary>
    /// Verifies realization sampling composes the class's content-seeded transform-clone stream
    /// with the local table percentile — bit-equal to an independently constructed expectation
    /// from the documented seed recipe.
    /// </summary>
    [TestMethod]
    public void Test_SampleResponseFunction_Realization_MatchesContentSeededExpectation()
    {
        // Arrange
        int sampleSize = 16;
        int seed = 20260905;
        var response = TransformedUncertainResponse();
        var chance = response.EventTree.Nodes.OfType<ChanceNode>().Single();
        ProbabilitySource source = chance.ProbabilitySource;
        response.SetupSampler(sampleSize, seed, SamplingScheme.LatinHypercube);

        // The documented recipe: the class consumes one child-stream ordinal, and chain entry i
        // forks from that base with the transform's content hash and position.
        byte[] tokenBytes = Convert.FromHexString(source.CanonicalToken());
        int chainBase = SeedHelpers.HashCombine(seed, tokenBytes, 0);
        var transform = (LinearTransform)source.HazardTransforms[0]!;
        var expectedTransform = UncertainMap();
        expectedTransform.SetupSampler(sampleSize,
            SeedHelpers.HashCombine(chainBase, transform.CanonicalHash(), 0), SamplingScheme.LatinHypercube);

        // Act / Assert: realization-for-realization bit equality on the failure branch.
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
        var response = TransformedTableResponse(uncertainTransform: false);
        var chance = response.EventTree.Nodes.OfType<ChanceNode>().Single();
        var transform = (LinearTransform)chance.ProbabilitySource.HazardTransforms[0]!;
        byte[] baseline = response.CanonicalHash();

        // Act / Assert
        transform.Name = "Renamed map";
        CollectionAssert.AreEqual(baseline, response.CanonicalHash());
        transform.Beta = 0.75d;
        CollectionAssert.AreNotEqual(baseline, response.CanonicalHash());
    }

    /// <summary>Verifies a live chain-transform edit invalidates the compiled plan.</summary>
    [TestMethod]
    public void Test_CompiledPlan_LiveTransformEdit_Invalidates()
    {
        // Arrange
        var response = TransformedTableResponse(uncertainTransform: false);
        var chance = response.EventTree.Nodes.OfType<ChanceNode>().Single();
        var transform = (LinearTransform)chance.ProbabilitySource.HazardTransforms[0]!;
        Assert.AreEqual(1, response.SamplingDimensions);

        // Act: making the live transform uncertain adds a chain dimension only if the
        // fingerprinted plan invalidates on the property change.
        transform.Sigma = 0.5d;
        transform.IsUncertain = true;

        // Assert
        Assert.AreEqual(2, response.SamplingDimensions);
    }

    /// <summary>Verifies a bivariate surface source evaluates the declared-axis slice through the tree.</summary>
    [TestMethod]
    public void Test_SampleResponseFunction_BivariateAxisSource_MatchesSurfaceSlice()
    {
        // Arrange: the tree hazard drives the primary axis; t(h) = 0.5 + h supplies the secondary.
        var surface = Surface();
        var map = Map(alpha: 0.5d, beta: 1d);
        var tree = new EventTree();
        tree.Add(tree.Root.Id, new ChanceNode("Breach",
            new ProbabilitySource(surface, BivariateSourceAxis.Primary, new[] { map })));
        tree.Add(tree.Root.Id, new RemainderNode("No failure"));
        var response = ValidResponse(tree);
        response.SetupSampler(4, 12345, SamplingScheme.LatinHypercube);

        // Act / Assert: mean and realization both reproduce the clamped surface slice.
        OrderedPairedData curve = response.SampleResponseFunction();
        OrderedPairedData realization = response.SampleResponseFunction(2);
        for (int h = 0; h < 2; h++)
        {
            double hazard = h;
            double expected = surface.SurfaceProbability(hazard, 0.5d + hazard);
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
        var restored = new EventTreeResponse(response.ToXElement());
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

    /// <summary>Builds a one-chance tree whose table is authored at transformed knots.</summary>
    /// <param name="uncertainTransform">Whether the chain transform carries uncertainty.</param>
    /// <returns>The response.</returns>
    private static EventTreeResponse TransformedTableResponse(bool uncertainTransform)
    {
        var table = new UncertainOrderedPairedData(
            new[]
            {
                new UncertainOrdinate(2d, new Deterministic(0.2d)),
                new UncertainOrdinate(3d, new Deterministic(0.6d)),
            }, true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic);
        var tree = new EventTree();
        tree.Add(tree.Root.Id, new ChanceNode("Failure",
            new ProbabilitySource(table, new[] { uncertainTransform ? UncertainMap() : Map() })));
        tree.Add(tree.Root.Id, new RemainderNode("No failure"));
        return ValidResponse(tree);
    }

    /// <summary>Builds a one-chance tree with an uncertain t-space table and an uncertain map.</summary>
    /// <returns>The response.</returns>
    private static EventTreeResponse TransformedUncertainResponse()
    {
        var table = new UncertainOrderedPairedData(
            new[]
            {
                new UncertainOrdinate(2d, new Uniform(0.1d, 0.3d)),
                new UncertainOrdinate(3d, new Uniform(0.4d, 0.8d)),
            }, true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Uniform);
        var tree = new EventTree();
        tree.Add(tree.Root.Id, new ChanceNode("Failure",
            new ProbabilitySource(table, new[] { UncertainMap() })));
        tree.Add(tree.Root.Id, new RemainderNode("No failure"));
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
    private static EventTreeResponse ValidResponse(EventTree tree)
    {
        return new EventTreeResponse(new[] { 0d, 1d }, tree)
        {
            Name = "Event tree",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
    }
}
