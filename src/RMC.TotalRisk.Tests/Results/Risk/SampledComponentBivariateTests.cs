using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.Results.Risk;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.Systems.Components;
using RMC.TotalRisk.Systems.Components.Graph;

namespace RMC.TotalRisk.Tests.Results.Risk;

/// <summary>
/// Unit tests for the conditional-bin evaluation of a bivariate component inside
/// <see cref="SampledComponent.ComputeRisk"/> — the hand-computable independence fold, the
/// one-point-per-stream-per-evaluation recording, the inert optional parameter on univariate
/// components, the no-extra-draws pin, the staged joint/secondary member semantics, and the
/// engine-side competing-risks guard.
/// </summary>
[TestClass]
public class SampledComponentBivariateTests
{
    #region Fixtures

    /// <summary>Builds the deterministic primary (Surge) marginal: exceedance 0.999 → 0 up to 0.001 → 30.</summary>
    private static TabularHazard SurgeFrequency()
    {
        return new TabularHazard
        {
            Name = "Surge Frequency",
            SpecifiedHazard = "Surge",
            HazardUnit = "ft",
            NoUncertaintyFunction = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(0.999d, new Deterministic(0d)),
                    new UncertainOrdinate(0.5d, new Deterministic(10d)),
                    new UncertainOrdinate(0.001d, new Deterministic(30d)),
                },
                true, SortOrder.Descending, true, SortOrder.Ascending, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds the deterministic secondary (Pool) marginal: exceedance 0.999 → 0 up to 0.001 → 100.</summary>
    private static TabularHazard PoolFrequency()
    {
        return new TabularHazard
        {
            Name = "Pool Frequency",
            SpecifiedHazard = "Pool Elevation",
            HazardUnit = "ft",
            NoUncertaintyFunction = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(0.999d, new Deterministic(0d)),
                    new UncertainOrdinate(0.5d, new Deterministic(50d)),
                    new UncertainOrdinate(0.001d, new Deterministic(100d)),
                },
                true, SortOrder.Descending, true, SortOrder.Ascending, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds the independence-copula bivariate hazard over the two deterministic marginals.</summary>
    private static BivariateHazard JointHazard(int bins, IHazardFunction? marginalY = null)
    {
        return new BivariateHazard(SurgeFrequency(), marginalY ?? PoolFrequency())
        {
            Name = "Joint Hazard",
            SpecifiedHazard = "Surge",
            HazardUnit = "ft",
            SecondarySpecifiedHazard = "Pool Elevation",
            SecondaryHazardUnit = "ft",
            SecondaryIntegrationBins = bins,
        };
    }

    /// <summary>Builds a deterministic fragility rising linearly from (0 → 0) to (100 → 1) on the pool axis.</summary>
    private static TabularResponse PoolFragility(string name = "Pool Fragility")
    {
        return new TabularResponse
        {
            Name = name,
            SpecifiedHazard = "Pool Elevation",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(0d, new Deterministic(0d)), new UncertainOrdinate(100d, new Deterministic(1d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds a deterministic fragility rising linearly from (10 → 0) to (20 → 1) on the surge axis.</summary>
    private static TabularResponse SurgeFragility(string name = "Surge Fragility")
    {
        return new TabularResponse
        {
            Name = name,
            SpecifiedHazard = "Surge",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(10d, new Deterministic(0d)), new UncertainOrdinate(20d, new Deterministic(1d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds a deterministic consequence rising linearly from (0 → 0) to (max → valueAtMax).</summary>
    private static TabularConsequence Damages(string name, string hazard, double max, double valueAtMax)
    {
        return new TabularConsequence
        {
            Name = name,
            SpecifiedHazard = hazard,
            HazardUnit = "ft",
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(0d, new Deterministic(0d)), new UncertainOrdinate(max, new Deterministic(valueAtMax)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>
    /// Builds the Secondary-bound fixture: the joint hazard's port 1 drives a pool fragility
    /// whose failure damages read the pool signal, with a primary-bound background terminal.
    /// </summary>
    private static SystemComponent SecondaryBoundComponent(int bins,
        IResponseFunction? response = null, IHazardFunction? marginalY = null)
    {
        var component = new SystemComponent(JointHazard(bins, marginalY)) { Name = "Joint Component" };
        var hazard = component.Graph.GetElements<HazardElement>().Single();
        var breach = new ResponseElement("Pool Breach")
        {
            Function = response ?? PoolFragility(),
            Input = new RiskConnection(hazard, 1),
        };
        component.Graph.AddElement(breach);
        var failure = new ConsequenceElement("Failure Damages") { Input = new RiskConnection(breach) };
        failure.Functions.Add(Damages("Failure Loss", "Pool Elevation", 100d, 600d));
        component.Graph.AddElement(failure);
        var background = new ConsequenceElement("Baseline Damages") { Input = new RiskConnection(hazard) };
        background.Functions.Add(Damages("Baseline Loss", "Surge", 30d, 60d));
        component.Graph.AddElement(background);
        return component;
    }

    /// <summary>
    /// Builds the joint-mode surface response over surge rows {0, 30} and weighted pool columns
    /// {(0, 0.4), (100, 0.6)} with probabilities {{0.1, 0.3}, {0.5, 0.9}}.
    /// </summary>
    private static BivariateResponse JointSurface()
    {
        var response = new BivariateResponse
        {
            Name = "Joint Fragility",
            SpecifiedHazard = "Surge",
            HazardUnit = "ft",
            SecondarySpecifiedHazard = "Pool Elevation",
            SecondaryHazardUnit = "ft",
        };
        response.PrimaryHazardLevels.Clear();
        response.PrimaryHazardLevels.Add(0d);
        response.PrimaryHazardLevels.Add(30d);
        response.SecondaryHazardLevels.Clear();
        response.SecondaryHazardLevels.Add(new WeightedHazardLevel { Level = 0d, Weight = 0.4d });
        response.SecondaryHazardLevels.Add(new WeightedHazardLevel { Level = 100d, Weight = 0.6d });
        response.ProbabilityValues = new[,] { { 0.1d, 0.3d }, { 0.5d, 0.9d } };
        return response;
    }

    /// <summary>
    /// Builds the joint-mode fixture: the surface response takes the hazard's primary output on
    /// its primary port and the raw secondary output on its secondary port.
    /// </summary>
    private static SystemComponent JointResponseComponent(int bins)
    {
        var component = new SystemComponent(JointHazard(bins)) { Name = "Joint Surface Component" };
        var hazard = component.Graph.GetElements<HazardElement>().Single();
        var breach = new ResponseElement("Joint Breach")
        {
            Function = JointSurface(),
            Input = new RiskConnection(hazard),
            SecondaryInput = new RiskConnection(hazard, 1),
        };
        component.Graph.AddElement(breach);
        var failure = new ConsequenceElement("Failure Damages") { Input = new RiskConnection(breach) };
        failure.Functions.Add(Damages("Failure Loss", "Surge", 30d, 600d));
        component.Graph.AddElement(failure);
        var background = new ConsequenceElement("Baseline Damages") { Input = new RiskConnection(hazard) };
        background.Functions.Add(Damages("Baseline Loss", "Surge", 30d, 60d));
        component.Graph.AddElement(background);
        return component;
    }

    /// <summary>Sets up samplers and samples one realization of the component (the adaptive conditional sweep — the production construction).</summary>
    private static SampledComponent Sampled(SystemComponent component, int realizationIndex = 0)
    {
        component.SetupSamplers(8, componentSeed: 12345, SamplingScheme.LatinHypercube);
        return component.Sample(realizationIndex);
    }

    /// <summary>
    /// Sets up samplers and samples the mean snapshot on the fixed conditional-trapezoid grid —
    /// the discretization-error instrument's construction, which pins the fixed kernel's exact
    /// arithmetic.
    /// </summary>
    private static SampledComponent SampledFixed(SystemComponent component, int bins)
    {
        component.SetupSamplers(8, componentSeed: 12345, SamplingScheme.LatinHypercube);
        return component.SampleWithConditionalBins(bins);
    }

    /// <summary>A counting tabular response that tallies every sampling call.</summary>
    private sealed class CountingResponse : TabularResponse
    {
        /// <summary>The number of sampling calls observed.</summary>
        public int SampleCalls { get; private set; }

        /// <inheritdoc/>
        public override IUnivariateDistribution SampleFunction()
        {
            SampleCalls++;
            return base.SampleFunction();
        }

        /// <inheritdoc/>
        public override IUnivariateDistribution SampleFunction(int realizationIndex)
        {
            SampleCalls++;
            return base.SampleFunction(realizationIndex);
        }
    }

    /// <summary>A counting tabular hazard that tallies every sampling call.</summary>
    private sealed class CountingHazard : TabularHazard
    {
        /// <summary>The number of sampling calls observed.</summary>
        public int SampleCalls { get; private set; }

        /// <inheritdoc/>
        public override IUnivariateDistribution SampleFunction()
        {
            SampleCalls++;
            return base.SampleFunction();
        }

        /// <inheritdoc/>
        public override IUnivariateDistribution SampleFunction(int realizationIndex)
        {
            SampleCalls++;
            return base.SampleFunction(realizationIndex);
        }
    }

    #endregion

    #region Independence hand-sum

    /// <summary>
    /// Verifies the marginalized fixed-grid result equals the exact conditional-trapezoid sum
    /// on a hand-computable independence fixture: PoF = Σ w_j · p(y_j) and the mean failure
    /// consequence is the adjusted-weighted average, with the linear pool fragility
    /// p(y) = y / 100 and damages c(y) = 6y. The fixed kernel is the discretization-error
    /// instrument's evaluation path, constructed here through the instrument route.
    /// </summary>
    [TestMethod]
    public void Test_IndependenceBins_MarginalizedRiskEqualsHandSum()
    {
        // Arrange — four bins, evaluation at the surge median (u = 0.5).
        var component = SecondaryBoundComponent(bins: 4);
        var sampled = SampledFixed(component, bins: 4);
        var bivariate = (IBivariateHazardFunction)component.HazardFunction!;
        var nodes = bivariate.SampleConditionalYGivenX(0, 10d);

        double expectedPoF = 0d;
        double expectedFailure = 0d;
        int expectedEntries = 0;
        foreach (var (y, weight) in nodes)
        {
            double p = Math.Clamp(y / 100d, 0d, 1d);
            expectedPoF += weight * p;
            expectedFailure += weight * p * (6d * y);
            if (p > 0d) expectedEntries++;
        }

        // Act
        var flags = new RiskComputeFlags();
        var realization = new ComponentRealization(sampled.FailureModeCount);
        var output = sampled.ComputeRisk(0.5d, 10d, flags, realization);

        // Assert — the marginalized fold, and the entry list enumerating one entry per
        // contributing bin (a zero-probability pathway records nothing, the univariate rule).
        Assert.AreEqual(5, nodes.Count);
        Assert.AreEqual(expectedPoF, output.ProbabilityOfFailure, 1e-12);
        Assert.AreEqual(expectedFailure / expectedPoF, output.MeanFailureConsequences, 1e-9);
        Assert.AreEqual(expectedEntries, output.ResponseProbabilities.Count);
        Assert.AreEqual(60d * 10d / 30d, output.NonFailureConsequences, 1e-12,
            "The primary-bound background evaluates at the surge signal in every bin.");
    }

    /// <summary>
    /// Verifies the adaptive conditional sweep on the same hand fixture: the linear fragility
    /// under the uniform conditional makes the exact marginalized probability of failure 0.5,
    /// and the adopted mesh must land within the refinement budget's honesty band (the
    /// piecewise-linear conditional integrand has table knots inside the panels; measured
    /// 3.3e-5 at the four-bin budget of 84 surrogate evaluations, asserted with headroom). The
    /// structural identities — one point's entries summing to the marginalized fold, the
    /// exhaustive unit budgets — hold on the adaptive path exactly as on the fixed grid.
    /// </summary>
    [TestMethod]
    public void Test_IndependenceAdaptive_TracksExactIntegral()
    {
        // Arrange — the production (adaptive) construction of the same fixture.
        var component = SecondaryBoundComponent(bins: 4);
        var sampled = Sampled(component);
        var flags = new RiskComputeFlags();
        var realization = new ComponentRealization(sampled.FailureModeCount);

        // Act
        var output = sampled.ComputeRisk(0.5d, 10d, flags, realization, recordOutput: true);

        // Assert — the exact conditional mean, within the budget-capped adaptive band.
        Assert.AreEqual(0.5d, output.ProbabilityOfFailure, 2e-4,
            "The adaptive sweep must track the exact linear-fragility conditional mean within its budget band.");
        var failPoint = realization.Curves.Fail.RiskPoints[0];
        Assert.IsTrue(failPoint.ResponseProbabilities.Count > 0);
        Assert.AreEqual(output.ProbabilityOfFailure, failPoint.ResponseProbabilities.Sum(), 1e-15);
        Assert.AreEqual(1d, realization.Curves.Background.RiskPoints[0].ResponseProbabilities.Sum(), 1e-12);
        Assert.AreEqual(1d, realization.Curves.Total.RiskPoints[0].ResponseProbabilities.Sum(), 1e-12);
    }

    /// <summary>
    /// Verifies the NaN default derives the same slice probability the caller would pass: the
    /// explicit-u and derived evaluations agree bit-for-bit on a bivariate component.
    /// </summary>
    [TestMethod]
    public void Test_HazardNonExceedance_ExplicitAndDerivedAgree()
    {
        // Arrange
        var component = SecondaryBoundComponent(bins: 6);
        var sampled = Sampled(component);
        var flags = new RiskComputeFlags();
        var realization = new ComponentRealization(sampled.FailureModeCount);

        // Act — u = CDF(10) = 0.5 exactly on the deterministic marginal.
        double derived = sampled.ComputeRisk(0.5d, 10d, flags, realization).ProbabilityOfFailure;
        double explicitU = sampled.ComputeRisk(0.5d, 10d, flags, realization, hazardNonExceedance: 0.5d).ProbabilityOfFailure;

        // Assert
        Assert.AreEqual(derived, explicitU, 0d);
    }

    /// <summary>
    /// Verifies the joint-mode surface semantics: SRP(x) returns the preserved v1.0 weighted
    /// collapse, SRPAt(x, y) evaluates the clamped probability surface, the fixed-grid engine
    /// fold (the instrument route) marginalizes the surface over the conditional-trapezoid
    /// nodes exactly, and the adaptive production fold agrees with a dense fixed reference
    /// within its refinement band.
    /// </summary>
    [TestMethod]
    public void Test_JointMode_SrpIsCollapseAndSrpAtIsSurface()
    {
        // Arrange — the fixed-grid instrument construction pins the trapezoid fold.
        var component = JointResponseComponent(bins: 4);
        var sampled = SampledFixed(component, bins: 4);
        var mode = sampled.FailureModes.First(m => !m.IsNonFailureMode);

        // Assert — the collapse at x = 10: 0.22 + (0.74 − 0.22)/3.
        Assert.AreEqual(0.22d + (0.74d - 0.22d) / 3d, mode.SRP(10d), 1e-12);

        // The surface at (10, 50): rows interpolate to 0.2 and 0.7, then x = 10 → 0.2 + 0.5/3.
        Assert.AreEqual(0.2d + 0.5d / 3d, mode.SRPAt(10d, 50d), 1e-12);

        // The fixed-grid engine fold: Σ w_j · surface(10, y_j).
        var bivariate = (IBivariateHazardFunction)component.HazardFunction!;
        var nodes = bivariate.SampleConditionalYGivenX(0, 10d);
        double expected = 0d;
        foreach (var (y, weight) in nodes)
        {
            expected += weight * mode.SRPAt(10d, y);
        }
        var flags = new RiskComputeFlags();
        var realization = new ComponentRealization(sampled.FailureModeCount);
        Assert.AreEqual(expected, sampled.ComputeRisk(0.5d, 10d, flags, realization).ProbabilityOfFailure, 1e-12);

        // The adaptive production fold against a dense fixed reference (1000 trapezoid bins on
        // the instrument route): the two discretizations of the same conditional integral must
        // agree within the adaptive budget band.
        var adaptiveComponent = JointResponseComponent(bins: 4);
        var adaptive = Sampled(adaptiveComponent);
        double adaptivePoF = adaptive.ComputeRisk(0.5d, 10d, new RiskComputeFlags(),
            new ComponentRealization(adaptive.FailureModeCount)).ProbabilityOfFailure;
        var denseComponent = JointResponseComponent(bins: 4);
        var dense = SampledFixed(denseComponent, bins: 1000);
        double densePoF = dense.ComputeRisk(0.5d, 10d, new RiskComputeFlags(),
            new ComponentRealization(dense.FailureModeCount)).ProbabilityOfFailure;
        Assert.AreEqual(densePoF, adaptivePoF, 1e-4,
            "The adaptive fold must agree with the dense fixed reference within the budget band.");
    }

    #endregion

    #region Recording

    /// <summary>
    /// Verifies one recording evaluation folds the conditional column into exactly one risk
    /// point per stream per scope, with the entry lists enumerating the nodes and the
    /// exhaustive probability budgets intact — asserted on BOTH constructions: the fixed-grid
    /// instrument route (whose entry counts are the exact trapezoid shape) and the adaptive
    /// production route (whose one-point and unit-budget structure is identical while its node
    /// count follows the adopted mesh).
    /// </summary>
    [TestMethod]
    public void Test_BivariateRecording_OnePointPerStreamPerEvaluation()
    {
        // The adaptive production construction: the one-point-per-stream contract and the
        // exhaustive budgets hold with a mesh-shaped entry enumeration.
        var adaptiveComponent = SecondaryBoundComponent(bins: 4);
        var adaptive = Sampled(adaptiveComponent);
        var adaptiveRealization = new ComponentRealization(adaptive.FailureModeCount);
        var adaptiveOutput = adaptive.ComputeRisk(0.5d, 10d, new RiskComputeFlags(), adaptiveRealization, recordOutput: true);
        Assert.AreEqual(1, adaptiveRealization.Curves.Fail.RiskPoints.Count);
        Assert.AreEqual(1, adaptiveRealization.Curves.Total.RiskPoints.Count);
        Assert.IsTrue(adaptiveRealization.Curves.Fail.RiskPoints[0].ResponseProbabilities.Count > 0);
        Assert.AreEqual(adaptiveOutput.ProbabilityOfFailure,
            adaptiveRealization.Curves.Fail.RiskPoints[0].ResponseProbabilities.Sum(), 1e-15);
        Assert.AreEqual(1d, adaptiveRealization.Curves.Background.RiskPoints[0].ResponseProbabilities.Sum(), 1e-12);
        Assert.AreEqual(1d, adaptiveRealization.Curves.Total.RiskPoints[0].ResponseProbabilities.Sum(), 1e-12);

        // Arrange — the fixed-grid instrument construction, whose entry counts are exact.
        var component = SecondaryBoundComponent(bins: 4);
        var sampled = SampledFixed(component, bins: 4);
        var flags = new RiskComputeFlags();
        var realization = new ComponentRealization(sampled.FailureModeCount);

        // Act
        var output = sampled.ComputeRisk(0.5d, 10d, flags, realization, recordOutput: true);

        // Assert — one point per component stream.
        Assert.AreEqual(1, realization.Curves.Fail.RiskPoints.Count);
        Assert.AreEqual(1, realization.Curves.Excess.RiskPoints.Count);
        Assert.AreEqual(1, realization.Curves.Background.RiskPoints.Count);
        Assert.AreEqual(1, realization.Curves.NonFail.RiskPoints.Count);
        Assert.AreEqual(1, realization.Curves.Total.RiskPoints.Count);

        // The Fail point enumerates one entry per contributing bin (the lowest node's zero
        // pathway probability records nothing — the univariate rule); its entries sum to the
        // marginalized PoF.
        var failPoint = realization.Curves.Fail.RiskPoints[0];
        Assert.AreEqual(4, failPoint.ResponseProbabilities.Count);
        Assert.AreEqual(output.ProbabilityOfFailure, failPoint.ResponseProbabilities.Sum(), 1e-15);

        // The Background point's entries sum to exactly the unit conditional budget, and the
        // Total union is exhaustive.
        var backgroundPoint = realization.Curves.Background.RiskPoints[0];
        Assert.AreEqual(1d, backgroundPoint.ResponseProbabilities.Sum(), 1e-12);
        var totalPoint = realization.Curves.Total.RiskPoints[0];
        Assert.AreEqual(1d, totalPoint.ResponseProbabilities.Sum(), 1e-12);

        // One point per mode stream as well.
        var modeCurves = realization.FailureModes[0].Curves;
        Assert.AreEqual(1, modeCurves.Fail.RiskPoints.Count);
        Assert.AreEqual(1, modeCurves.Excess.RiskPoints.Count);
        Assert.AreEqual(1, modeCurves.Background.RiskPoints.Count);
        Assert.AreEqual(1, modeCurves.NonFail.RiskPoints.Count);
        Assert.AreEqual(1, modeCurves.Total.RiskPoints.Count);
        Assert.AreEqual(5, modeCurves.Fail.RiskPoints[0].ResponseProbabilities.Count);
    }

    /// <summary>
    /// Verifies the new optional parameter is inert on a univariate component: the evaluation
    /// and its recorded points are identical whether the slice probability is supplied or not.
    /// </summary>
    [TestMethod]
    public void Test_UnivariateComponent_HazardNonExceedanceParameterInert()
    {
        // Arrange — the shared univariate two-mode scenario.
        var component = new SystemComponent { Name = "Univariate" };
        component.HazardFunction = SurgeFrequency();
        component.AddFailureMode(new FailureMode(null, null, SurgeFragility(), Damages("Failure Loss", "Surge", 30d, 600d)));
        component.AddFailureMode(new FailureMode(null, null, null, Damages("Baseline Loss", "Surge", 30d, 60d)));
        component.SetupSamplers(8, componentSeed: 12345, SamplingScheme.LatinHypercube);
        var sampled = component.Sample(0);
        var flags = new RiskComputeFlags();
        var withDefault = new ComponentRealization(sampled.FailureModeCount);
        var withExplicit = new ComponentRealization(sampled.FailureModeCount);

        // Act
        var defaultOutput = sampled.ComputeRisk(0.25d, 15d, flags, withDefault, recordOutput: true);
        double defaultPoF = defaultOutput.ProbabilityOfFailure;
        double defaultMean = defaultOutput.MeanFailureConsequences;
        var explicitOutput = sampled.ComputeRisk(0.25d, 15d, flags, withExplicit, recordOutput: true, hazardNonExceedance: 0.25d);

        // Assert — bit-identical scalars and identical recording shape.
        Assert.AreEqual(defaultPoF, explicitOutput.ProbabilityOfFailure, 0d);
        Assert.AreEqual(defaultMean, explicitOutput.MeanFailureConsequences, 0d);
        Assert.AreEqual(withDefault.Curves.Fail.RiskPoints.Count, withExplicit.Curves.Fail.RiskPoints.Count);
        Assert.AreEqual(withDefault.Curves.Fail.RiskPoints[0].ResponseProbabilities.Count,
            withExplicit.Curves.Fail.RiskPoints[0].ResponseProbabilities.Count);
    }

    #endregion

    #region Draw discipline

    /// <summary>
    /// Verifies the no-extra-draws pin: all sampling happens in the sampled constructors, and
    /// the conditional-bin loop performs none — repeated evaluations leave every sampling
    /// counter untouched.
    /// </summary>
    [TestMethod]
    public void Test_BinLoop_PerformsNoAdditionalSampling()
    {
        // Arrange — counting doubles on the secondary marginal and the response.
        var marginalY = new CountingHazard
        {
            Name = "Pool Frequency",
            SpecifiedHazard = "Pool Elevation",
            HazardUnit = "ft",
            NoUncertaintyFunction = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(0.999d, new Deterministic(0d)),
                    new UncertainOrdinate(0.5d, new Deterministic(50d)),
                    new UncertainOrdinate(0.001d, new Deterministic(100d)),
                },
                true, SortOrder.Descending, true, SortOrder.Ascending, UnivariateDistributionType.Deterministic),
        };
        var response = new CountingResponse
        {
            Name = "Pool Fragility",
            SpecifiedHazard = "Pool Elevation",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(0d, new Deterministic(0d)), new UncertainOrdinate(100d, new Deterministic(1d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
        var component = SecondaryBoundComponent(bins: 8, response, marginalY);
        component.SetupSamplers(8, componentSeed: 12345, SamplingScheme.LatinHypercube);

        // Act — construction samples; evaluation must not.
        int setupResponseCalls = response.SampleCalls;
        int setupMarginalCalls = marginalY.SampleCalls;
        var sampled = component.Sample(0);
        int constructedResponseCalls = response.SampleCalls;
        int constructedMarginalCalls = marginalY.SampleCalls;
        var flags = new RiskComputeFlags();
        var realization = new ComponentRealization(sampled.FailureModeCount);
        sampled.ComputeRisk(0.5d, 10d, flags, realization);
        sampled.ComputeRisk(0.25d, 15d, flags, realization, recordOutput: true);

        // Assert — one construction draw each, zero evaluation draws.
        Assert.AreEqual(setupResponseCalls + 1, constructedResponseCalls);
        Assert.AreEqual(setupMarginalCalls + 1, constructedMarginalCalls);
        Assert.AreEqual(constructedResponseCalls, response.SampleCalls,
            "The conditional-bin loop must not sample the response.");
        Assert.AreEqual(constructedMarginalCalls, marginalY.SampleCalls,
            "The conditional-bin loop must not sample the secondary marginal.");
    }

    #endregion

    #region Guards

    /// <summary>
    /// Verifies the InverseSRP guard extends to Secondary-bound modes — no monotone
    /// primary-axis inverse exists.
    /// </summary>
    [TestMethod]
    public void Test_SecondaryBoundMode_InverseSrpThrows()
    {
        // Arrange
        var sampled = Sampled(SecondaryBoundComponent(bins: 4));
        var mode = sampled.FailureModes.First(m => !m.IsNonFailureMode);

        // Act / Assert
        Assert.ThrowsException<NotSupportedException>(() => mode.InverseSRP(0.5d));
    }

    /// <summary>
    /// Verifies the InverseSRP guard extends to joint-mode bivariate responses.
    /// </summary>
    [TestMethod]
    public void Test_JointMode_InverseSrpThrows()
    {
        // Arrange
        var sampled = Sampled(JointResponseComponent(bins: 4));
        var mode = sampled.FailureModes.First(m => !m.IsNonFailureMode);

        // Act / Assert
        Assert.ThrowsException<NotSupportedException>(() => mode.InverseSRP(0.5d));
    }

    /// <summary>
    /// Verifies the engine-side competing-risks guard: sampling a multi-unit competing
    /// component over a bivariate hazard with a secondary-dependent failure mode throws loudly
    /// before any incidence pre-processing can consume the wrong axis.
    /// </summary>
    [TestMethod]
    public void Test_CompetingMultiUnit_SecondaryBoundMode_SampleThrows()
    {
        // Arrange — a primary-bound and a Secondary-bound failure mode under competing.
        var component = SecondaryBoundComponent(bins: 4);
        var hazard = component.Graph.GetElements<HazardElement>().Single();
        var primaryBreach = new ResponseElement("Surge Breach")
        {
            Function = SurgeFragility(),
            Input = new RiskConnection(hazard),
        };
        component.Graph.AddElement(primaryBreach);
        var primaryFailure = new ConsequenceElement("Surge Damages") { Input = new RiskConnection(primaryBreach) };
        primaryFailure.Functions.Add(Damages("Surge Loss", "Surge", 30d, 300d));
        component.Graph.AddElement(primaryFailure);
        component.FailureModeMethod = FailureModeMethod.CompetingFailures;
        component.SetupSamplers(8, componentSeed: 12345, SamplingScheme.LatinHypercube);

        // Act / Assert
        var thrown = Assert.ThrowsException<InvalidOperationException>(() => component.Sample(0));
        StringAssert.Contains(thrown.Message, "competing failure modes over a bivariate hazard");
    }

    #endregion
}
