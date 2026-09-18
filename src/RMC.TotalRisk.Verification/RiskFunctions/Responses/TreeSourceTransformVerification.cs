using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.Results.Risk;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.RiskFunctions.Transforms;
using RMC.TotalRisk.Systems.Components;

namespace RMC.TotalRisk.Verification.RiskFunctions.Responses;

/// <summary>
/// Transform- and axis-mapped tree probability-source verification. The chain seat is proven
/// four independent ways: a bit-grade reconstruction oracle that re-derives every transformed
/// lookup from Numerics primitives and the documented content-seed recipe; a pre-transformed
/// equivalence in which a table authored directly at the composed ordinates reproduces the
/// transformed model bit-for-bit on a dyadic fixture, at the function surface and behind the
/// full engine; an exhaustive-algebra fault-tree oracle with an independently composed
/// inclusion–exclusion top event; and a bivariate-surface slice twin in which the declared-axis
/// evaluation equals a directly authored univariate slice behind the engine. Full-run byte
/// reproducibility of the new streams and the configuration-risk reach through a transformed
/// source close the family.
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// <para>
/// The reconstruction oracle is bit-exact because it composes the same Numerics primitives the
/// source evaluates — <see cref="UncertainOrderedPairedData.CurveSample(double)"/> lookups at
/// independently re-seeded transform-clone curves — in the same order; the fault-tree
/// inclusion–exclusion oracle instead re-orders the top-event arithmetic, so its comparison is
/// held to 1e-14 absolute (operation-order roundoff only). The pre-transformed equivalence uses
/// dyadic knots, probabilities, and an affine map whose composed ordinates are exactly
/// representable, so both interpolation paths compute identical doubles and the comparison is
/// exact.
/// </para>
/// </remarks>
[TestClass]
public class TreeSourceTransformVerification
{
    /// <summary>
    /// Verifies the transformed path product realization-for-realization at N = 1,024: a
    /// two-transform uncertain chain and an uncertain t-space table are re-derived from
    /// independently constructed samplers at the documented content-seed recipe — the class
    /// consumes one child-stream ordinal, and chain entry i forks from that base with the
    /// transform's content hash and position — and every lookup reproduces the engine's branch
    /// probabilities bit-exactly across all hazards.
    /// </summary>
    [TestMethod]
    public void Test_EventTree_TransformedChain_ReconstructionOracle_BitExact()
    {
        int sampleSize = 1024;
        int seed = 987654321;
        var table = UncertainTTable();
        LinearTransform first = UncertainMap("Depth map", 1d, 0.5d, 0.2d);
        LinearTransform second = UncertainMap("Duration map", 0.5d, 1d, 0.3d);
        var tree = new EventTree();
        tree.Add(tree.Root.Id, new ChanceNode("Breach",
            new ProbabilitySource(table, new ITransformFunction[] { first, second })));
        tree.Add(tree.Root.Id, new ChanceNode("Spill", new ProbabilitySource(0.1d)) { IsFailure = false });
        tree.Add(tree.Root.Id, new RemainderNode("No failure"));
        var response = new EventTreeResponse(new[] { 0d, 0.5d, 1d }, tree)
        {
            Name = "Transformed tree",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
        response.SetupSampler(sampleSize, seed, SamplingScheme.LatinHypercube);
        ProbabilitySource source =
            ((ChanceNode)tree.Nodes.First(n => n.Name == "Breach")).ProbabilitySource;

        byte[] tokenBytes = Convert.FromHexString(source.CanonicalToken());
        int chainBase = SeedHelpers.HashCombine(seed, tokenBytes, 0);
        LinearTransform firstOracle = UncertainMap("Depth map", 1d, 0.5d, 0.2d);
        firstOracle.SetupSampler(sampleSize,
            SeedHelpers.HashCombine(chainBase, first.CanonicalHash(), 0), SamplingScheme.LatinHypercube);
        LinearTransform secondOracle = UncertainMap("Duration map", 0.5d, 1d, 0.3d);
        secondOracle.SetupSampler(sampleSize,
            SeedHelpers.HashCombine(chainBase, second.CanonicalHash(), 1), SamplingScheme.LatinHypercube);

        var hazards = new[] { 0d, 0.5d, 1d };
        for (int realization = 0; realization < sampleSize; realization++)
        {
            double localPercentile = response.SampledPercentile(realization, 0);
            OrderedPairedData sampledTable = table.CurveSample(localPercentile);
            OrderedPairedData actual = response.SampleResponseFunction(realization);
            for (int h = 0; h < hazards.Length; h++)
            {
                double transformed = secondOracle.SampleFunction(realization).Function(
                    firstOracle.SampleFunction(realization).Function(hazards[h]));
                double expected = sampledTable.GetYFromX(transformed);
                Assert.AreEqual(expected, actual[h].Y,
                    $"Realization {realization}, hazard {hazards[h]} diverged from the reconstruction.");
            }
        }
    }

    /// <summary>
    /// Verifies the pre-transformed equivalence on a dyadic fixture: a t-space table behind an
    /// affine map and a direct table authored at the composed ordinates produce bit-identical
    /// response curves at every tree hazard, and bit-identical mean-only engine results behind
    /// identical hazards and consequences.
    /// </summary>
    [TestMethod]
    public void Test_EventTree_PreTransformedEquivalence_BitExact()
    {
        EventTreeResponse transformed = DyadicTransformedResponse();
        EventTreeResponse direct = DyadicDirectResponse();

        OrderedPairedData transformedCurve = transformed.SampleResponseFunction();
        OrderedPairedData directCurve = direct.SampleResponseFunction();
        Assert.AreEqual(directCurve.Count, transformedCurve.Count);
        for (int h = 0; h < directCurve.Count; h++)
        {
            Assert.AreEqual(directCurve[h].X, transformedCurve[h].X);
            Assert.AreEqual(directCurve[h].Y, transformedCurve[h].Y,
                "The dyadic affine reparameterization must interpolate identically.");
        }

        RiskAnalysis transformedAnalysis = BuildMeanOnlyAnalysis(DyadicTransformedResponse());
        transformedAnalysis.RunAsync().GetAwaiter().GetResult();
        RiskAnalysis directAnalysis = BuildMeanOnlyAnalysis(DyadicDirectResponse());
        directAnalysis.RunAsync().GetAwaiter().GetResult();

        Assert.AreEqual(directAnalysis.MeanRiskResults!.Curves.Fail.TotalProbability,
            transformedAnalysis.MeanRiskResults!.Curves.Fail.TotalProbability,
            "The engine must integrate the two authorings identically.");
        Assert.AreEqual(directAnalysis.MeanRiskResults.Curves.Total.Mean,
            transformedAnalysis.MeanRiskResults.Curves.Total.Mean);
    }

    /// <summary>
    /// Verifies the fault-tree top event over a transformed basic event against an independently
    /// composed inclusion–exclusion oracle at N = 512: the transformed event is the only
    /// uncertain variable, so its local and chain percentiles are read from the parent's own
    /// flattened matrix (columns zero and one), the transformed probability is re-derived
    /// through the live map at the copied chain percentile — bit-equal to the clone stream by
    /// the flattening contract — and the OR(AND(transformed, 0.3), 0.15) top event matches
    /// p3 + p1·p2 − p1·p2·p3 within operation-order roundoff.
    /// </summary>
    [TestMethod]
    public void Test_FaultTree_TransformedEvent_InclusionExclusionOracle()
    {
        int sampleSize = 512;
        int seed = 246813579;
        var table = UncertainTTable();
        LinearTransform map = UncertainMap("Depth map", 1d, 0.5d, 0.2d);
        var tree = new FaultTree();
        var and = new FaultTreeGateNode("Both", FaultTreeGateType.And);
        tree.Add(tree.Root.Id, and);
        tree.Add(and.Id, new FaultTreeBasicEventNode("Transformed",
            new ProbabilitySource(table, new ITransformFunction[] { map })));
        tree.Add(and.Id, new FaultTreeBasicEventNode("Fixed", new ProbabilitySource(0.3d)));
        tree.Add(tree.Root.Id, new FaultTreeBasicEventNode("Plain", new ProbabilitySource(0.15d)));
        var response = new FaultTreeResponse(new[] { 0d, 1d }, tree)
        {
            Name = "Transformed fault tree",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
        response.SetupSampler(sampleSize, seed, SamplingScheme.LatinHypercube);

        var hazards = new[] { 0d, 1d };
        for (int realization = 0; realization < sampleSize; realization++)
        {
            OrderedPairedData transformedTable = table.CurveSample(response.SampledPercentile(realization, 0));
            double chainPercentile = response.SampledPercentile(realization, 1);
            OrderedPairedData actual = response.SampleResponseFunction(realization);
            for (int h = 0; h < hazards.Length; h++)
            {
                double transformed = map.SampleFunction(chainPercentile).Function(hazards[h]);
                double p1 = transformedTable.GetYFromX(transformed);
                double p2 = 0.3d;
                double p3 = 0.15d;
                double expected = p3 + (p1 * p2) - (p1 * p2 * p3);
                Assert.AreEqual(expected, actual[h].Y, 1e-14d,
                    $"Realization {realization}, hazard {hazards[h]} diverged from inclusion–exclusion.");
            }
        }
    }

    /// <summary>
    /// Verifies the bivariate-surface slice behind the full engine: a surface source whose tree
    /// hazard drives the primary axis with an affine chain supplying the secondary coordinate is
    /// bit-equal, in mean-only annual failure probability and expected consequences, to a twin
    /// authored directly as the univariate slice p(h) = S(h, t(h)) at the same tree levels.
    /// </summary>
    [TestMethod]
    public void Test_EngineMeanOnly_BivariateAxisSlice_MatchesDirectSliceTwin()
    {
        BivariateResponse surface = Surface();
        LinearTransform map = AffineMap("Duration map", 0.5d, 1d);
        var surfaceTree = new EventTree();
        surfaceTree.Add(surfaceTree.Root.Id, new ChanceNode("Breach",
            new ProbabilitySource(surface, BivariateSourceAxis.Primary, new ITransformFunction[] { map })));
        surfaceTree.Add(surfaceTree.Root.Id, new RemainderNode("No failure"));
        var surfaceResponse = new EventTreeResponse(new[] { 0d, 1d }, surfaceTree)
        {
            Name = "Surface tree",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };

        var sliceOrdinates = new[]
        {
            new UncertainOrdinate(0d, new Deterministic(surface.SurfaceProbability(0d, 0.5d))),
            new UncertainOrdinate(1d, new Deterministic(surface.SurfaceProbability(1d, 1.5d))),
        };
        var sliceTree = new EventTree();
        sliceTree.Add(sliceTree.Root.Id, new ChanceNode("Breach", new ProbabilitySource(
            new UncertainOrderedPairedData(sliceOrdinates, true, SortOrder.Ascending, false,
                SortOrder.None, UnivariateDistributionType.Deterministic))));
        sliceTree.Add(sliceTree.Root.Id, new RemainderNode("No failure"));
        var sliceResponse = new EventTreeResponse(new[] { 0d, 1d }, sliceTree)
        {
            Name = "Slice tree",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };

        RiskAnalysis surfaceAnalysis = BuildMeanOnlyAnalysis(surfaceResponse);
        surfaceAnalysis.RunAsync().GetAwaiter().GetResult();
        RiskAnalysis sliceAnalysis = BuildMeanOnlyAnalysis(sliceResponse);
        sliceAnalysis.RunAsync().GetAwaiter().GetResult();

        Assert.AreEqual(sliceAnalysis.MeanRiskResults!.Curves.Fail.TotalProbability,
            surfaceAnalysis.MeanRiskResults!.Curves.Fail.TotalProbability,
            "The declared-axis surface slice must integrate exactly like its direct authoring.");
        Assert.AreEqual(sliceAnalysis.MeanRiskResults.Curves.Total.Mean,
            surfaceAnalysis.MeanRiskResults.Curves.Total.Mean);
    }

    /// <summary>
    /// Verifies full-run byte reproducibility of the new chain streams and the
    /// configuration-risk reach through a transformed source: two re-authored full-uncertainty
    /// twins publish byte-identical results JSON, and a house event inside a fault tree
    /// referenced through a TRANSFORMED event-tree source is reconfigured bit-equal to the
    /// directly re-authored configured twin.
    /// </summary>
    [TestMethod]
    public void Test_EngineFullRun_Reproducibility_And_ConfigurationReach()
    {
        RiskAnalysis first = BuildTransformedCarrierAnalysis(houseState: false, out Guid functionId, out Guid houseId);
        first.Options.EstimateMeanRiskOnly = false;
        first.Options.Realizations = 200;
        first.RunAsync().GetAwaiter().GetResult();
        RiskAnalysis second = BuildTransformedCarrierAnalysis(houseState: false, out _, out _);
        second.Options.EstimateMeanRiskOnly = false;
        second.Options.Realizations = 200;
        second.RunAsync().GetAwaiter().GetResult();
        Assert.AreEqual(first.RiskResults!.ToJson(), second.RiskResults!.ToJson(),
            "Re-authored transformed twins must publish byte-identical results.");

        RiskAnalysis author = BuildTransformedCarrierAnalysis(houseState: false, out functionId, out houseId);
        ConfigurationRiskResults configured = author.MeasureConfigurationRisk(
            new[] { new HouseEventState(functionId, houseId, true) });
        RiskAnalysis configuredTwin = BuildTransformedCarrierAnalysis(houseState: true, out _, out _);
        configuredTwin.RunAsync().GetAwaiter().GetResult();
        Assert.AreEqual(configuredTwin.MeanRiskResults!.Curves.Fail.TotalProbability,
            configured.System.ConfiguredFailureProbability,
            "The configuration walk must reach the house event through the transformed source.");
        Assert.IsTrue(configured.System.FailureProbabilityChange > 0d);
    }

    /// <summary>
    /// Verifies conditional presence at model scale: a transform-free tree component's
    /// serialized form carries no chain child and no axis attribute anywhere, so every existing
    /// model's bytes are untouched by the seat (the perf byte gates carry the full-scale proof).
    /// </summary>
    [TestMethod]
    public void Test_Serialization_TransformFreeModel_CarriesNoChainMarkup()
    {
        RiskAnalysis author = BuildTransformedCarrierAnalysis(houseState: false, out _, out _);
        string transformed = author.Components[0].ToXElement().ToString();
        StringAssert.Contains(transformed, "HazardTransforms");

        var plainTree = new EventTree();
        plainTree.Add(plainTree.Root.Id, new ChanceNode("Breach", new ProbabilitySource(0.2d)));
        plainTree.Add(plainTree.Root.Id, new RemainderNode("No failure"));
        var plainResponse = new EventTreeResponse(new[] { 0d, 1d }, plainTree)
        {
            Name = "Plain tree",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
        RiskAnalysis plain = BuildMeanOnlyAnalysis(plainResponse);
        string serialized = plain.Components[0].ToXElement().ToString();
        Assert.IsFalse(serialized.Contains("HazardTransforms"),
            "A transform-free model must serialize without the chain child.");
        Assert.IsFalse(serialized.Contains("BivariateAxis"),
            "A transform-free model must serialize without the axis attribute.");
    }

    /// <summary>Builds the shared uncertain t-space probability table.</summary>
    /// <returns>The table.</returns>
    private static UncertainOrderedPairedData UncertainTTable()
    {
        return new UncertainOrderedPairedData(
            new[]
            {
                new UncertainOrdinate(1d, new Uniform(0.05d, 0.25d)),
                new UncertainOrdinate(2d, new Uniform(0.3d, 0.5d)),
                new UncertainOrdinate(3d, new Uniform(0.55d, 0.85d)),
            }, true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Uniform);
    }

    /// <summary>Builds an uncertain Stage-to-derived-axis linear map.</summary>
    /// <param name="name">The transform name.</param>
    /// <param name="alpha">The intercept.</param>
    /// <param name="beta">The slope.</param>
    /// <param name="sigma">The standard error.</param>
    /// <returns>The transform.</returns>
    private static LinearTransform UncertainMap(string name, double alpha, double beta, double sigma)
    {
        return new LinearTransform
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            TransformedHazard = "Duration",
            TransformedHazardUnit = "hr",
            Minimum = -100d,
            Maximum = 100d,
            Alpha = alpha,
            Beta = beta,
            Sigma = sigma,
            IsUncertain = true,
        };
    }

    /// <summary>Builds a deterministic affine Stage-to-derived-axis map.</summary>
    /// <param name="name">The transform name.</param>
    /// <param name="alpha">The intercept.</param>
    /// <param name="beta">The slope.</param>
    /// <returns>The transform.</returns>
    private static LinearTransform AffineMap(string name, double alpha, double beta)
    {
        return new LinearTransform
        {
            Name = name,
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

    /// <summary>Builds the dyadic t-space model: knots {2, 3}, probabilities {0.25, 0.75}, t(h) = 2 + 0.5·h.</summary>
    /// <returns>The response.</returns>
    private static EventTreeResponse DyadicTransformedResponse()
    {
        var table = new UncertainOrderedPairedData(
            new[]
            {
                new UncertainOrdinate(2d, new Deterministic(0.25d)),
                new UncertainOrdinate(3d, new Deterministic(0.75d)),
            }, true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic);
        var tree = new EventTree();
        tree.Add(tree.Root.Id, new ChanceNode("Breach",
            new ProbabilitySource(table, new ITransformFunction[] { AffineMap("Depth map", 2d, 0.5d) })));
        tree.Add(tree.Root.Id, new RemainderNode("No failure"));
        return new EventTreeResponse(new[] { 0d, 1d, 2d }, tree)
        {
            Name = "Dyadic transformed tree",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
    }

    /// <summary>Builds the pre-composed dyadic twin: the same slice authored on the tree axis.</summary>
    /// <returns>The response.</returns>
    private static EventTreeResponse DyadicDirectResponse()
    {
        var table = new UncertainOrderedPairedData(
            new[]
            {
                new UncertainOrdinate(0d, new Deterministic(0.25d)),
                new UncertainOrdinate(1d, new Deterministic(0.5d)),
                new UncertainOrdinate(2d, new Deterministic(0.75d)),
            }, true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic);
        var tree = new EventTree();
        tree.Add(tree.Root.Id, new ChanceNode("Breach", new ProbabilitySource(table)));
        tree.Add(tree.Root.Id, new RemainderNode("No failure"));
        return new EventTreeResponse(new[] { 0d, 1d, 2d }, tree)
        {
            Name = "Dyadic direct tree",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
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

    /// <summary>Builds the deterministic hazard over stages zero to one.</summary>
    /// <returns>The hazard.</returns>
    private static TabularHazard BuildHazard()
    {
        return new TabularHazard
        {
            Name = "Stage frequency",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            NoUncertaintyFunction = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(0.999d, new Deterministic(0d)),
                    new UncertainOrdinate(0.5d, new Deterministic(0.5d)),
                    new UncertainOrdinate(0.001d, new Deterministic(1d)),
                },
                true, SortOrder.Descending, true, SortOrder.Ascending,
                UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds the stage-to-loss consequence.</summary>
    /// <param name="uncertain">Whether the loss carries Normal uncertainty.</param>
    /// <returns>The consequence.</returns>
    private static TabularConsequence BuildConsequence(bool uncertain = false)
    {
        return new TabularConsequence
        {
            Name = "Failure loss",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
            UncertainOrderedPairedData = uncertain
                ? new UncertainOrderedPairedData(
                    new[]
                    {
                        new UncertainOrdinate(0d, new Normal(500d, 50d)),
                        new UncertainOrdinate(1d, new Normal(1000d, 100d)),
                    },
                    true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Normal)
                : new UncertainOrderedPairedData(
                    new[]
                    {
                        new UncertainOrdinate(0d, new Deterministic(500d)),
                        new UncertainOrdinate(1d, new Deterministic(1000d)),
                    },
                    true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds a mean-only one-component analysis over the given response.</summary>
    /// <param name="response">The response.</param>
    /// <returns>The analysis.</returns>
    private static RiskAnalysis BuildMeanOnlyAnalysis(EventTreeResponse response)
    {
        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = BuildHazard();
        component.AddFailureMode(new FailureMode(null, null, response, BuildConsequence()));
        return new RiskAnalysis(new[] { component })
        {
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
        };
    }

    /// <summary>
    /// Builds a full-uncertainty analysis whose event tree carries a TRANSFORMED fault-tree
    /// reference: the chance source maps the stage onto a derived axis before evaluating the
    /// referenced fault tree, and the fault tree carries the queried house event.
    /// </summary>
    /// <param name="houseState">The authored house state.</param>
    /// <param name="functionId">The fault-tree function id.</param>
    /// <param name="houseId">The house-event node id.</param>
    /// <returns>The analysis.</returns>
    private static RiskAnalysis BuildTransformedCarrierAnalysis(bool houseState, out Guid functionId,
        out Guid houseId)
    {
        var faultTree = new FaultTree();
        Guid gateId = faultTree.Add(faultTree.Root.Id,
            new FaultTreeGateNode("Carried gate", FaultTreeGateType.And));
        houseId = faultTree.Add(gateId, new FaultTreeHouseEventNode("Gate out of service", houseState));
        faultTree.Add(gateId, new FaultTreeBasicEventNode("Load exceedance", new ProbabilitySource(0.9d)));
        faultTree.Add(faultTree.Root.Id,
            new FaultTreeBasicEventNode("Structural failure", new ProbabilitySource(0.2d)));
        var faultResponse = new FaultTreeResponse(new[] { 0.5d, 1.5d }, faultTree)
        {
            Name = "Carried fault tree",
            SpecifiedHazard = "Duration",
            HazardUnit = "hr",
        };
        functionId = faultResponse.Id;

        var eventTree = new EventTree();
        eventTree.Add(eventTree.Root.Id, new ChanceNode("Breach", new ProbabilitySource(
            faultResponse, new ITransformFunction[] { AffineMap("Duration map", 0.5d, 1d) })));
        eventTree.Add(eventTree.Root.Id, new RemainderNode("No failure"));
        var response = new EventTreeResponse(new[] { 0d, 1d }, eventTree)
        {
            Name = "Carrier tree",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };

        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = BuildHazard();
        component.AddFailureMode(new FailureMode(null, null, response, BuildConsequence(uncertain: true)));
        return new RiskAnalysis(new[] { component })
        {
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
        };
    }
}
