using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees.Nodes;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Nodes;

namespace RMC.TotalRisk.Tests.RiskFunctions.Responses.Trees;

/// <summary>
/// Tests the node-importance analysis on both tree kinds: closed-form affine cases, shared
/// fault-variable unification, deterministic and saturated degenerate trees, bit-exact
/// determinism, live-state inertness, and argument validation.
/// </summary>
[TestClass]
public class TreeNodeImportanceTests
{
    /// <summary>Builds an aligned three-knot uniform table on hazards 0, 1, and 2.</summary>
    /// <param name="minimum">The uniform lower bound.</param>
    /// <param name="maximum">The uniform upper bound.</param>
    /// <returns>The aligned uncertainty table.</returns>
    private static UncertainOrderedPairedData UniformTable(double minimum, double maximum)
    {
        return new UncertainOrderedPairedData(
            new[]
            {
                new UncertainOrdinate(0d, new Uniform(minimum, maximum)),
                new UncertainOrdinate(1d, new Uniform(minimum, maximum)),
                new UncertainOrdinate(2d, new Uniform(minimum, maximum)),
            }, true, SortOrder.Ascending, false, SortOrder.None,
            UnivariateDistributionType.Uniform);
    }

    /// <summary>Builds the affine event response: one uncertain failure branch plus a remainder.</summary>
    /// <returns>The event response.</returns>
    private static EventTreeResponse BuildAffineEventResponse()
    {
        var tree = new EventTree();
        tree.Add(tree.Root.Id, new ChanceNode("Failure branch",
            new ProbabilitySource(UniformTable(0.2d, 0.4d))) { IsFailure = true });
        tree.Add(tree.Root.Id, new RemainderNode("Survival"));
        return new EventTreeResponse(new[] { 0d, 1d, 2d }, tree)
        {
            Name = "Affine event",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
    }

    /// <summary>Builds the affine fault response: an uncertain event or an independent scalar event.</summary>
    /// <returns>The fault response.</returns>
    private static FaultTreeResponse BuildAffineFaultResponse()
    {
        var tree = new FaultTree();
        tree.Add(tree.Root.Id, new FaultTreeBasicEventNode("Uncertain event",
            new ProbabilitySource(UniformTable(0.2d, 0.4d))));
        tree.Add(tree.Root.Id, new FaultTreeBasicEventNode("Scalar event", new ProbabilitySource(0.3d)));
        return new FaultTreeResponse(new[] { 0d, 1d, 2d }, tree)
        {
            Name = "Affine fault",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
    }

    /// <summary>
    /// Verifies the affine event case: with the aggregate equal to the single uncertain branch
    /// probability, the branch correlates perfectly, the remainder anti-correlates perfectly, and
    /// the branch's first-order index re-estimates one within replicate sampling error.
    /// </summary>
    [TestMethod]
    public void Test_Compute_AffineEventTree_MatchesClosedForm()
    {
        // Arrange
        EventTreeResponse response = BuildAffineEventResponse();
        var options = new TreeNodeImportanceOptions(1d);

        // Act
        TreeNodeImportanceResult result = TreeNodeImportance.Compute(response, options);

        // Assert — echoed inputs and entry population.
        Assert.AreEqual(1d, result.HazardLevel, 0d);
        Assert.AreEqual(1000, result.Iterations);
        Assert.AreEqual(12345, result.Seed);
        Assert.AreEqual(2, result.Entries.Count);
        TreeNodeImportanceEntry branch = result.Entries.Single(entry => entry.Name == "Failure branch");
        TreeNodeImportanceEntry survival = result.Entries.Single(entry => entry.Name == "Survival");

        // The aggregate is exactly the branch probability: correlation +1, anti-correlation -1.
        Assert.IsTrue(branch.HasUncertainty);
        Assert.AreEqual(1d, branch.AggregateCorrelation, 1e-12);
        Assert.IsFalse(survival.HasUncertainty);
        Assert.AreEqual(-1d, survival.AggregateCorrelation, 1e-12);
        Assert.AreEqual(0d, survival.FirstOrderIndex, 0d);

        // The single uncertain source re-estimates the total variance: index near one, and the
        // aggregate variance approaches the exact Uniform(0.2, 0.4) variance of 1/300.
        Assert.AreEqual(1d, branch.FirstOrderIndex, 0.15d);
        Assert.AreEqual(1d / 300d, result.AggregateVariance, 0.0005d);

        // Path-probability summaries live inside the exact supports.
        Assert.IsTrue(branch.ProbabilitySummary[0] >= 0.2d && branch.ProbabilitySummary[4] <= 0.4d);
        Assert.IsTrue(survival.ProbabilitySummary[0] >= 0.6d && survival.ProbabilitySummary[4] <= 0.8d);
        Assert.IsTrue(result.AggregateSummary[0] >= 0.2d && result.AggregateSummary[4] <= 0.4d);
    }

    /// <summary>
    /// Verifies the affine fault case: the aggregate is affine in the single uncertain variable,
    /// so it correlates perfectly while the deterministic variable reports a NaN correlation, a
    /// zero index, and a constant summary.
    /// </summary>
    [TestMethod]
    public void Test_Compute_AffineFaultTree_MatchesClosedForm()
    {
        // Arrange
        FaultTreeResponse response = BuildAffineFaultResponse();
        var options = new TreeNodeImportanceOptions(1d);

        // Act
        TreeNodeImportanceResult result = TreeNodeImportance.Compute(response, options);

        // Assert
        Assert.AreEqual(2, result.Entries.Count);
        TreeNodeImportanceEntry uncertain = result.Entries.Single(entry => entry.Name == "Uncertain event");
        TreeNodeImportanceEntry scalar = result.Entries.Single(entry => entry.Name == "Scalar event");

        Assert.IsTrue(uncertain.HasUncertainty);
        Assert.AreEqual(1d, uncertain.AggregateCorrelation, 1e-12);
        Assert.AreEqual(1d, uncertain.FirstOrderIndex, 0.15d);
        Assert.IsFalse(scalar.HasUncertainty);
        Assert.AreEqual(double.NaN, scalar.AggregateCorrelation);
        Assert.AreEqual(0d, scalar.FirstOrderIndex, 0d);
        for (int i = 0; i < 5; i++) Assert.AreEqual(0.3d, scalar.ProbabilitySummary[i], 0d);

        // Aggregate = 0.3 + 0.7 p, so its variance is 0.49 times the Uniform(0.2, 0.4) variance.
        Assert.AreEqual(0.49d / 300d, result.AggregateVariance, 0.0003d);
    }

    /// <summary>
    /// Verifies shared-logical unification inside importance: a repeated shared transfer produces
    /// one unified entry whose single draw drives the whole tree.
    /// </summary>
    [TestMethod]
    public void Test_Compute_SharedFaultTransfer_UnifiesToOneEntry()
    {
        // Arrange — Or(A, shared transfer to A) reduces exactly to A.
        var tree = new FaultTree();
        var target = new FaultTreeBasicEventNode("Repeated event",
            new ProbabilitySource(UniformTable(0.2d, 0.4d)));
        tree.Add(tree.Root.Id, target);
        tree.LinkShared(tree.Root.Id, target.Id, "Repeat");
        var response = new FaultTreeResponse(new[] { 0d, 1d, 2d }, tree)
        {
            Name = "Shared fault",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };

        // Act
        TreeNodeImportanceResult result = TreeNodeImportance.Compute(response,
            new TreeNodeImportanceOptions(1d));

        // Assert — one unified variable, perfectly correlated with the aggregate it equals.
        Assert.AreEqual(1, result.Entries.Count);
        Assert.AreEqual("Repeated event", result.Entries[0].Name);
        Assert.AreEqual(1d, result.Entries[0].AggregateCorrelation, 1e-12);
    }

    /// <summary>Verifies identical options produce bit-identical results on both kinds.</summary>
    [TestMethod]
    public void Test_Compute_SameOptions_IsBitIdentical()
    {
        // Arrange
        EventTreeResponse eventResponse = BuildAffineEventResponse();
        FaultTreeResponse faultResponse = BuildAffineFaultResponse();
        var options = new TreeNodeImportanceOptions(1d) { Iterations = 200 };

        // Act
        TreeNodeImportanceResult firstEvent = TreeNodeImportance.Compute(eventResponse, options);
        TreeNodeImportanceResult secondEvent = TreeNodeImportance.Compute(eventResponse, options);
        TreeNodeImportanceResult firstFault = TreeNodeImportance.Compute(faultResponse, options);
        TreeNodeImportanceResult secondFault = TreeNodeImportance.Compute(faultResponse, options);

        // Assert
        AssertBitIdentical(firstEvent, secondEvent);
        AssertBitIdentical(firstFault, secondFault);
    }

    /// <summary>Verifies the analysis never disturbs canonical identity or a configured sampler.</summary>
    [TestMethod]
    public void Test_Compute_LeavesLiveStateUntouched()
    {
        // Arrange — a configured sampler and a captured realization before the analysis.
        FaultTreeResponse response = BuildAffineFaultResponse();
        response.SetupSampler(4, 777, SamplingScheme.LatinHypercube);
        byte[] hashBefore = response.CanonicalHash();
        OrderedPairedData realizationBefore = response.SampleResponseFunction(2);

        // Act
        TreeNodeImportance.Compute(response, new TreeNodeImportanceOptions(1d) { Iterations = 50 });

        // Assert — identity and sampler state survive untouched.
        CollectionAssert.AreEqual(hashBefore, response.CanonicalHash());
        OrderedPairedData realizationAfter = response.SampleResponseFunction(2);
        for (int h = 0; h < realizationBefore.Count; h++)
            Assert.AreEqual(realizationBefore[h].Y, realizationAfter[h].Y, 0d);
    }

    /// <summary>
    /// Verifies a fully deterministic tree reports zero variance, NaN correlations, zero indices,
    /// and constant summaries.
    /// </summary>
    [TestMethod]
    public void Test_Compute_DeterministicTree_ZeroVarianceStatistics()
    {
        // Arrange
        var tree = new EventTree();
        tree.Add(tree.Root.Id, new ChanceNode("Deterministic branch",
            new ProbabilitySource(0.4d)) { IsFailure = true });
        tree.Add(tree.Root.Id, new RemainderNode("Survival"));
        var response = new EventTreeResponse(new[] { 0d, 1d, 2d }, tree)
        {
            Name = "Deterministic event",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };

        // Act
        TreeNodeImportanceResult result = TreeNodeImportance.Compute(response,
            new TreeNodeImportanceOptions(1d) { Iterations = 25 });

        // Assert
        Assert.AreEqual(0d, result.AggregateVariance, 0d);
        for (int i = 0; i < 5; i++) Assert.AreEqual(0.4d, result.AggregateSummary[i], 0d);
        foreach (TreeNodeImportanceEntry entry in result.Entries)
        {
            Assert.IsFalse(entry.HasUncertainty);
            Assert.AreEqual(double.NaN, entry.AggregateCorrelation);
            Assert.AreEqual(0d, entry.FirstOrderIndex, 0d);
        }
    }

    /// <summary>
    /// Verifies a saturated tree whose aggregate is constant despite uncertain sources reports a
    /// NaN correlation and a NaN first-order index for the uncertain variable.
    /// </summary>
    [TestMethod]
    public void Test_Compute_SaturatedUncertainFaultTree_NaNStatistics()
    {
        // Arrange — a true house event under the Or root pins the aggregate at one.
        var tree = new FaultTree();
        tree.Add(tree.Root.Id, new FaultTreeHouseEventNode("Always on", true));
        tree.Add(tree.Root.Id, new FaultTreeBasicEventNode("Uncertain event",
            new ProbabilitySource(UniformTable(0.2d, 0.4d))));
        var response = new FaultTreeResponse(new[] { 0d, 1d, 2d }, tree)
        {
            Name = "Saturated fault",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };

        // Act
        TreeNodeImportanceResult result = TreeNodeImportance.Compute(response,
            new TreeNodeImportanceOptions(1d) { Iterations = 25 });

        // Assert — the house event is not a variable; the uncertain entry divides zero by zero.
        Assert.AreEqual(1, result.Entries.Count);
        Assert.AreEqual(0d, result.AggregateVariance, 0d);
        Assert.AreEqual(double.NaN, result.Entries[0].AggregateCorrelation);
        Assert.AreEqual(double.NaN, result.Entries[0].FirstOrderIndex);
    }

    /// <summary>Verifies a hazard level that is not an authored level is rejected on both kinds.</summary>
    [TestMethod]
    public void Test_Compute_UnauthoredHazardLevel_Throws()
    {
        // Act / Assert
        Assert.ThrowsException<ArgumentException>(() => TreeNodeImportance.Compute(
            BuildAffineEventResponse(), new TreeNodeImportanceOptions(0.5d)));
        Assert.ThrowsException<ArgumentException>(() => TreeNodeImportance.Compute(
            BuildAffineFaultResponse(), new TreeNodeImportanceOptions(0.5d)));
    }

    /// <summary>Verifies null arguments are guarded on both overloads.</summary>
    [TestMethod]
    public void Test_Compute_NullArguments_Throw()
    {
        // Act / Assert
        Assert.ThrowsException<ArgumentNullException>(() => TreeNodeImportance.Compute(
            (EventTreeResponse)null!, new TreeNodeImportanceOptions(0d)));
        Assert.ThrowsException<ArgumentNullException>(() => TreeNodeImportance.Compute(
            (FaultTreeResponse)null!, new TreeNodeImportanceOptions(0d)));
        Assert.ThrowsException<ArgumentNullException>(() => TreeNodeImportance.Compute(
            BuildAffineEventResponse(), null!));
        Assert.ThrowsException<ArgumentNullException>(() => TreeNodeImportance.Compute(
            BuildAffineFaultResponse(), null!));
    }

    /// <summary>Asserts two results are bit-identical across every recorded statistic.</summary>
    /// <param name="expected">The first result.</param>
    /// <param name="actual">The repeated result.</param>
    private static void AssertBitIdentical(TreeNodeImportanceResult expected,
        TreeNodeImportanceResult actual)
    {
        Assert.AreEqual(expected.AggregateVariance, actual.AggregateVariance, 0d);
        CollectionAssert.AreEqual(expected.AggregateSummary.ToArray(), actual.AggregateSummary.ToArray());
        Assert.AreEqual(expected.Entries.Count, actual.Entries.Count);
        for (int i = 0; i < expected.Entries.Count; i++)
        {
            Assert.AreEqual(expected.Entries[i].NodeId, actual.Entries[i].NodeId);
            Assert.AreEqual(expected.Entries[i].CanonicalPath, actual.Entries[i].CanonicalPath);
            CollectionAssert.AreEqual(expected.Entries[i].ProbabilitySummary.ToArray(),
                actual.Entries[i].ProbabilitySummary.ToArray());
            Assert.AreEqual(
                BitConverter.DoubleToInt64Bits(expected.Entries[i].AggregateCorrelation),
                BitConverter.DoubleToInt64Bits(actual.Entries[i].AggregateCorrelation));
            Assert.AreEqual(
                BitConverter.DoubleToInt64Bits(expected.Entries[i].FirstOrderIndex),
                BitConverter.DoubleToInt64Bits(actual.Entries[i].FirstOrderIndex));
        }
    }
}
