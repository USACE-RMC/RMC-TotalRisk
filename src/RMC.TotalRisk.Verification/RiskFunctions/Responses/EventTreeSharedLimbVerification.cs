using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using Numerics.Sampling;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.Systems.Components;

namespace RMC.TotalRisk.Verification.RiskFunctions.Responses;

/// <summary>
/// Shared-limb verification for the event-tree response. The core oracle independently
/// re-implements the class-unified sampling recipe from Numerics primitives — one Latin hypercube
/// draw per sampling class from the documented seed path, the co-monotonic table curve sample,
/// and the explicit mass algebra — and asserts the engine's indexed realizations bit-exactly for
/// a shared limb (one draw) and its independent twin (two draws, exactly one column assignment
/// consistent). Sharing is provably invisible on the mean and percentile paths, pinned here at
/// response and engine scale, and moves only the realization ensemble, whose variance is checked
/// against the closed form.
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// <para>
/// Bit-grade shared-versus-independent comparisons require an evaluation order that the link-mode
/// selection cannot move: the mode attribute participates in each occurrence's identity token,
/// and sibling sums evaluate in canonical token order, so a group with three or more explicit
/// branches can reorder its compensated sum at ulp scale between the two modes. Every fixture
/// below therefore carries at most two explicit branches per sibling group, where a compensated
/// two-term sum is order-insensitive and bit equality is the mathematically correct expectation.
/// The mean-path invisibility itself is analytic — sharing changes which draws repeat, never a
/// mean or percentile source value.
/// </para>
/// </remarks>
public partial class EventTreeVerification
{
    /// <summary>The realization count for the bit-exact reconstruction oracles.</summary>
    private const int SharedLimbRealizations = 1000;

    /// <summary>The fixed seed for the shared-limb reconstruction oracles.</summary>
    private const int SharedLimbSeed = 10202801;

    /// <summary>
    /// Verifies the shared limb bit-exactly against a single-draw reconstruction built from
    /// Numerics primitives: one Latin hypercube column, the co-monotonic table sample, and the
    /// explicit path products 0.5&#183;p and 0.25&#183;p summed exactly as the evaluator does.
    /// </summary>
    [TestMethod]
    public void Test_SharedLimb_SingleDrawReconstruction_BitExact()
    {
        EventTreeResponse response = SharedLimbResponse(TreeLinkMode.SharedLogicalEvent, constantLimb: false);
        Assert.AreEqual(1, response.SamplingDimensions);
        response.SetupSampler(SharedLimbRealizations, SharedLimbSeed, SamplingScheme.LatinHypercube);

        double[,] percentiles = LatinHypercube.Random(SharedLimbRealizations, 1,
            SeedHelpers.ToPositiveSeed(SharedLimbSeed));
        UncertainOrderedPairedData table = LimbTable(constant: false);
        for (int realization = 0; realization < SharedLimbRealizations; realization++)
        {
            OrderedPairedData limb = table.CurveSample(percentiles[realization, 0]);
            OrderedPairedData actual = response.SampleResponseFunction(realization);
            for (int h = 0; h < actual.Count; h++)
            {
                double p = limb[h].Y;
                double expected = 0.5d * p + 0.25d * p;
                Assert.AreEqual(expected, actual[h].Y,
                    $"Realization {realization}, hazard {h}: the shared limb must reuse one draw.");
            }
        }
    }

    /// <summary>
    /// Verifies the independent twin bit-exactly against a two-draw reconstruction: exactly one
    /// assignment of the two Latin hypercube columns to the two occurrences reproduces every
    /// realization, and the single-draw reconstruction fails, so the two semantics cannot collapse.
    /// </summary>
    [TestMethod]
    public void Test_IndependentTwin_TwoDrawReconstruction_BitExact()
    {
        EventTreeResponse response = SharedLimbResponse(TreeLinkMode.IndependentClone, constantLimb: false);
        Assert.AreEqual(2, response.SamplingDimensions);
        response.SetupSampler(SharedLimbRealizations, SharedLimbSeed, SamplingScheme.LatinHypercube);

        double[,] percentiles = LatinHypercube.Random(SharedLimbRealizations, 2,
            SeedHelpers.ToPositiveSeed(SharedLimbSeed));
        UncertainOrderedPairedData table = LimbTable(constant: false);
        bool[] assignmentConsistent = { true, true };
        bool singleDrawConsistent = true;
        for (int realization = 0; realization < SharedLimbRealizations; realization++)
        {
            OrderedPairedData first = table.CurveSample(percentiles[realization, 0]);
            OrderedPairedData second = table.CurveSample(percentiles[realization, 1]);
            OrderedPairedData actual = response.SampleResponseFunction(realization);
            for (int h = 0; h < actual.Count; h++)
            {
                double direct = 0.5d * first[h].Y + 0.25d * second[h].Y;
                double swapped = 0.5d * second[h].Y + 0.25d * first[h].Y;
                double collapsed = 0.5d * first[h].Y + 0.25d * first[h].Y;
                assignmentConsistent[0] &= actual[h].Y == direct;
                assignmentConsistent[1] &= actual[h].Y == swapped;
                singleDrawConsistent &= actual[h].Y == collapsed;
            }
        }

        Assert.IsTrue(assignmentConsistent[0] ^ assignmentConsistent[1],
            "Exactly one column assignment must reproduce the independent twin bit-exactly.");
        Assert.IsFalse(singleDrawConsistent,
            "The independent twin must not be reproducible from one shared draw.");
    }

    /// <summary>
    /// Verifies the realization-ensemble movement sharing causes: with a hazard-constant limb the
    /// shared aggregate is 0.75&#183;p against 0.5&#183;p&#8321; + 0.25&#183;p&#8322;
    /// independent, so the ensemble variance ratio approaches
    /// 0.75&#178; / (0.5&#178; + 0.25&#178;) = 1.8 while the ensemble means agree, and the mean
    /// and median curves stay bit-equal.
    /// </summary>
    [TestMethod]
    public void Test_SharedLimb_VarianceMovement_MatchesClosedForm()
    {
        const int realizations = 4096;
        EventTreeResponse shared = SharedLimbResponse(TreeLinkMode.SharedLogicalEvent, constantLimb: true);
        EventTreeResponse independent = SharedLimbResponse(TreeLinkMode.IndependentClone, constantLimb: true);
        shared.SetupSampler(realizations, SharedLimbSeed, SamplingScheme.LatinHypercube);
        independent.SetupSampler(realizations, SharedLimbSeed, SamplingScheme.LatinHypercube);

        OrderedPairedData sharedMean = shared.SampleResponseFunction();
        OrderedPairedData independentMean = independent.SampleResponseFunction();
        OrderedPairedData sharedMedian = shared.SampleResponseFunction(0.5d);
        OrderedPairedData independentMedian = independent.SampleResponseFunction(0.5d);
        for (int h = 0; h < sharedMean.Count; h++)
        {
            Assert.AreEqual(independentMean[h].Y, sharedMean[h].Y,
                "Sharing must not move the mean curve.");
            Assert.AreEqual(independentMedian[h].Y, sharedMedian[h].Y,
                "Sharing must not move a percentile curve.");
        }

        var sharedSeries = new double[realizations];
        var independentSeries = new double[realizations];
        for (int realization = 0; realization < realizations; realization++)
        {
            sharedSeries[realization] = shared.SampleResponseFunction(realization)[0].Y;
            independentSeries[realization] = independent.SampleResponseFunction(realization)[0].Y;
        }

        double sharedVariance = SampleVariance(sharedSeries);
        double independentVariance = SampleVariance(independentSeries);
        Assert.IsTrue(sharedVariance > independentVariance,
            "Sharing must widen the realization ensemble of the aggregate.");
        // The Latin hypercube pairs the two independent columns with O(1/N) sample covariance and
        // both column variances estimate one marginal, so the ratio sits within a few percent of
        // the exact 1.8 at N = 4096; the 0.1 allowance is approximately ten times the observed
        // fluctuation scale.
        Assert.AreEqual(1.8d, sharedVariance / independentVariance, 0.1d,
            "The ensemble variance ratio must match the closed form.");

        double sharedEnsembleMean = Mean(sharedSeries);
        double independentEnsembleMean = Mean(independentSeries);
        // Each ensemble mean is a stratified Latin hypercube estimate of the same 0.75-scaled
        // uniform mean of 0.3, with stratified standard error below 1e-5 at N = 4096; the bound
        // is hundreds of standard errors and still one part in a thousand.
        Assert.AreEqual(sharedEnsembleMean, independentEnsembleMean, 3e-4d,
            "The ensemble means must agree between the two modes.");
    }

    /// <summary>
    /// Verifies mean-path invisibility at engine scale: mean-only runs over the shared component
    /// and its independent twin publish bit-identical expected annual consequences and failure
    /// probabilities, because every mean source value and every sibling evaluation order is
    /// identical between the two modes in this fixture.
    /// </summary>
    [TestMethod]
    public void Test_SharedLimb_EngineMeanOnly_BitIdentical_ToIndependentTwin()
    {
        RiskAnalysis shared = BuildEngine(TreeLinkMode.SharedLogicalEvent, fullUncertainty: false);
        shared.RunAsync().GetAwaiter().GetResult();
        RiskAnalysis independent = BuildEngine(TreeLinkMode.IndependentClone, fullUncertainty: false);
        independent.RunAsync().GetAwaiter().GetResult();

        Assert.AreEqual(independent.RiskResults![0]!.Total.Mean, shared.RiskResults![0]!.Total.Mean,
            "Mean-only expected annual consequences must be bit-identical between the modes.");
        Assert.AreEqual(independent.RiskResults[0]!.Fail.TotalProbability,
            shared.RiskResults[0]!.Fail.TotalProbability,
            "Mean-only annual failure probabilities must be bit-identical between the modes.");
    }

    /// <summary>
    /// Verifies the shared component behind the full engine: a repeated same-seed run publishes
    /// byte-identical results; the published ensemble-mean annual failure probabilities of the
    /// shared and independent twins agree within a derived sampling bound; and the retained
    /// per-realization failure probabilities carry the closed-form variance movement — the
    /// engine-scale counterpart of the response-level identity.
    /// </summary>
    [TestMethod]
    public void Test_SharedLimb_EngineEnsemble_ReproducibleAndMeanConsistent()
    {
        RiskAnalysis first = BuildEngine(TreeLinkMode.SharedLogicalEvent, fullUncertainty: true);
        first.RetainRealizations = true;
        first.RunAsync().GetAwaiter().GetResult();
        RiskAnalysis second = BuildEngine(TreeLinkMode.SharedLogicalEvent, fullUncertainty: true);
        second.RunAsync().GetAwaiter().GetResult();
        Assert.AreEqual(first.RiskResults!.ToJson(), second.RiskResults!.ToJson(),
            "A repeated same-content full run must publish byte-identical results.");

        RiskAnalysis independent = BuildEngine(TreeLinkMode.IndependentClone, fullUncertainty: true);
        independent.RetainRealizations = true;
        independent.RunAsync().GetAwaiter().GetResult();
        double sharedAfp = first.MeanRiskResults!.Components[0].Curves.Fail.TotalProbability;
        double independentAfp = independent.MeanRiskResults!.Components[0].Curves.Fail.TotalProbability;
        // Both published ensemble means estimate the same 0.3 integral. The per-realization
        // aggregate spread is at most 0.75 times the uniform limb sigma of 0.1155, so four
        // combined standard errors at N = 200 stay below 0.035; 0.05 adds headroom.
        Assert.AreEqual(sharedAfp, independentAfp, 0.05d,
            "The published ensemble-mean annual failure probabilities must agree within sampling error.");

        var sharedSeries = new double[200];
        var independentSeries = new double[200];
        for (int realization = 0; realization < 200; realization++)
        {
            sharedSeries[realization] =
                first.RetainedRealizations![realization].Components[0].Curves.Fail.TotalProbability;
            independentSeries[realization] =
                independent.RetainedRealizations![realization].Components[0].Curves.Fail.TotalProbability;
        }
        double sharedVariance = SampleVariance(sharedSeries);
        double independentVariance = SampleVariance(independentSeries);
        Assert.IsTrue(sharedVariance > independentVariance,
            "Sharing must widen the annual-failure-probability ensemble behind the engine.");
        // The exact ratio is 1.8; each variance estimate carries a relative standard error near
        // sqrt(2 / 199), so the ratio fluctuates at roughly the 15 percent scale at N = 200.
        Assert.AreEqual(1.8d, sharedVariance / independentVariance, 0.5d,
            "The engine-scale ensemble variance ratio must match the closed form.");
    }

    /// <summary>
    /// Verifies external sharing at ensemble scale across a self-contained round trip: repeated
    /// embeds of the target unify onto one live instance, so the class count, canonical identity,
    /// and every indexed realization are preserved bit-exactly.
    /// </summary>
    [TestMethod]
    public void Test_SharedLimb_ExternalRoundTrip_AtScale()
    {
        var targetTree = new EventTree();
        var carrier = new ChanceNode("Carrier", new ProbabilitySource(0.5d)) { IsFailure = false };
        targetTree.Add(targetTree.Root.Id, carrier);
        var limb = new ChanceNode("Stored limb", new ProbabilitySource(LimbTable(constant: false)));
        targetTree.Add(carrier.Id, limb);
        targetTree.Add(targetTree.Root.Id, new RemainderNode("Target remainder"));
        EventTreeResponse target = TreeResponse(targetTree, "Stored tree");

        var ownerTree = new EventTree();
        var gateOne = new ChanceNode("Gate one", new ProbabilitySource(0.5d)) { IsFailure = false };
        var gateTwo = new ChanceNode("Gate two", new ProbabilitySource(0.25d)) { IsFailure = false };
        ownerTree.Add(ownerTree.Root.Id, gateOne);
        ownerTree.Add(ownerTree.Root.Id, gateTwo);
        ownerTree.LinkShared(gateOne.Id, target, limb.Id, "First occurrence");
        ownerTree.LinkShared(gateTwo.Id, target, limb.Id, "Second occurrence");
        ownerTree.Add(ownerTree.Root.Id, new RemainderNode("Owner remainder"));
        EventTreeResponse original = TreeResponse(ownerTree, "Owner tree");
        Assert.AreEqual(1, original.SamplingDimensions);

        var restored = new EventTreeResponse(original.ToXElement(RiskSerializationMode.SelfContained));
        Assert.AreEqual(1, restored.SamplingDimensions,
            "Repeated embeds of one external target must stay one sampling class after the round trip.");
        Assert.AreEqual(Convert.ToHexString(original.CanonicalHash()),
            Convert.ToHexString(restored.CanonicalHash()));

        original.SetupSampler(SharedLimbRealizations, SharedLimbSeed, SamplingScheme.LatinHypercube);
        restored.SetupSampler(SharedLimbRealizations, SharedLimbSeed, SamplingScheme.LatinHypercube);
        for (int realization = 0; realization < SharedLimbRealizations; realization++)
        {
            OrderedPairedData expected = original.SampleResponseFunction(realization);
            OrderedPairedData actual = restored.SampleResponseFunction(realization);
            for (int h = 0; h < expected.Count; h++) Assert.AreEqual(expected[h].Y, actual[h].Y);
        }
    }

    /// <summary>Builds the shared-limb engine scenario over a deterministic hazard and consequence.</summary>
    /// <param name="mode">The link mode under test.</param>
    /// <param name="fullUncertainty">Whether to run the realization ensemble.</param>
    /// <returns>The configured analysis.</returns>
    private static RiskAnalysis BuildEngine(TreeLinkMode mode, bool fullUncertainty)
    {
        var hazard = new TabularHazard
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
        var consequence = new TabularConsequence
        {
            Name = "Failure loss",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(0d, new Deterministic(500d)),
                    new UncertainOrdinate(1d, new Deterministic(1000d)),
                },
                true, SortOrder.Ascending, false, SortOrder.None,
                UnivariateDistributionType.Deterministic),
        };

        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = hazard;
        component.AddFailureMode(new FailureMode(null, null,
            SharedLimbResponse(mode, constantLimb: true), consequence));
        var analysis = new RiskAnalysis(new[] { component });
        if (fullUncertainty)
        {
            analysis.Options.EstimateMeanRiskOnly = false;
            analysis.Options.Realizations = 200;
        }
        return analysis;
    }

    /// <summary>
    /// Builds the canonical shared-limb fixture: an uncertain limb authored under a 0.5 gate and
    /// reused under a 0.25 gate through one link, with at most two explicit branches per sibling
    /// group so mode selection cannot move any compensated summation order.
    /// </summary>
    /// <param name="mode">The link mode.</param>
    /// <param name="constantLimb">Whether the limb table is hazard-constant.</param>
    /// <returns>The configured response.</returns>
    private static EventTreeResponse SharedLimbResponse(TreeLinkMode mode, bool constantLimb)
    {
        var tree = new EventTree();
        var gateOne = new ChanceNode("Gate one", new ProbabilitySource(0.5d)) { IsFailure = false };
        var gateTwo = new ChanceNode("Gate two", new ProbabilitySource(0.25d)) { IsFailure = false };
        tree.Add(tree.Root.Id, gateOne);
        tree.Add(tree.Root.Id, gateTwo);
        tree.Add(tree.Root.Id, new RemainderNode("Root remainder"));
        var limb = new ChanceNode("Limb", new ProbabilitySource(LimbTable(constantLimb)));
        tree.Add(gateOne.Id, limb);
        tree.Add(gateOne.Id, new RemainderNode("Gate one remainder"));
        if (mode == TreeLinkMode.SharedLogicalEvent)
            tree.LinkShared(gateTwo.Id, limb.Id, "Limb occurrence");
        else
            tree.LinkIndependent(gateTwo.Id, limb.Id, "Limb occurrence");
        tree.Add(gateTwo.Id, new RemainderNode("Gate two remainder"));
        return TreeResponse(tree, "Shared limb tree");
    }

    /// <summary>Builds the limb uncertainty table.</summary>
    /// <param name="constant">Whether both hazard rows carry one distribution.</param>
    /// <returns>The table.</returns>
    private static UncertainOrderedPairedData LimbTable(bool constant)
    {
        return new UncertainOrderedPairedData(
            new[]
            {
                new UncertainOrdinate(0d, constant ? new Uniform(0.2d, 0.6d) : new Uniform(0.1d, 0.3d)),
                new UncertainOrdinate(1d, new Uniform(0.2d, 0.6d)),
            }, true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Uniform);
    }

    /// <summary>Builds a labeled valid response over hazards zero and one.</summary>
    /// <param name="tree">The authored tree.</param>
    /// <param name="name">The response name.</param>
    /// <returns>The response.</returns>
    private static EventTreeResponse TreeResponse(EventTree tree, string name)
    {
        return new EventTreeResponse(new[] { 0d, 1d }, tree)
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
    }

    /// <summary>Computes the two-pass sample variance of one series.</summary>
    /// <param name="series">The recorded series.</param>
    /// <returns>The sample variance.</returns>
    private static double SampleVariance(IReadOnlyList<double> series)
    {
        double mean = Mean(series);
        double sum = 0d;
        for (int i = 0; i < series.Count; i++)
        {
            double deviation = series[i] - mean;
            sum += deviation * deviation;
        }
        return sum / (series.Count - 1);
    }

    /// <summary>Computes the sequential mean of one series.</summary>
    /// <param name="series">The recorded series.</param>
    /// <returns>The mean.</returns>
    private static double Mean(IReadOnlyList<double> series)
    {
        double sum = 0d;
        for (int i = 0; i < series.Count; i++) sum += series[i];
        return sum / series.Count;
    }
}
