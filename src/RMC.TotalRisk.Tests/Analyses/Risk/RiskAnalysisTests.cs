using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Analyses.Support;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.Results.Risk;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.Systems.Components;

namespace RMC.TotalRisk.Tests.Analyses.Risk;

/// <summary>
/// Unit tests for <see cref="RiskAnalysis"/> — the mean-only and full-uncertainty smokes with
/// their decomposition identities and dense-reference parity, the mixture exposure-branch
/// behavior, the stage gates with pinned messages, the event lifecycle, options-only
/// serialization, and same-seed reproducibility.
/// </summary>
[TestClass]
public class RiskAnalysisTests
{
    /// <summary>Builds the shared stage-frequency hazard (0.999 → 0 ft up to 0.001 → 30 ft).</summary>
    private static TabularHazard StageFrequency()
    {
        return new TabularHazard
        {
            Name = "Stage Frequency",
            SpecifiedHazard = "Stage",
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

    /// <summary>Builds a deterministic fragility rising linearly from (10 → 0) to (20 → 1).</summary>
    private static TabularResponse Fragility()
    {
        return new TabularResponse
        {
            Name = "Fragility",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(10d, new Deterministic(0d)), new UncertainOrdinate(20d, new Deterministic(1d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds an uncertain fragility (triangular ordinates) for ensemble spread.</summary>
    private static TabularResponse UncertainFragility()
    {
        var response = Fragility();
        response.UncertainOrderedPairedData = new UncertainOrderedPairedData(
            new[] { new UncertainOrdinate(10d, new Triangular(0d, 0.05d, 0.1d)), new UncertainOrdinate(20d, new Triangular(0.7d, 0.9d, 1d)) },
            true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Triangular);
        return response;
    }

    /// <summary>Builds a labeled deterministic consequence: linear from (0 → 0) to (30 → valueAtThirty).</summary>
    private static TabularConsequence Consequence(string name, double valueAtThirty)
    {
        return new TabularConsequence
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Life Loss",
            ConsequenceUnit = "lives",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(0d, new Deterministic(0d)), new UncertainOrdinate(30d, new Deterministic(valueAtThirty)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds the standard single-component scenario with the given consequence function.</summary>
    private static SystemComponent Component(IConsequenceFunction failureConsequence, IResponseFunction? response = null)
    {
        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = StageFrequency();
        component.AddFailureMode(new FailureMode(null, null, response ?? Fragility(), failureConsequence));
        component.AddFailureMode(new FailureMode(null, null, null, Consequence("Non-Failure Loss", 60d)));
        return component;
    }


    /// <summary>
    /// Verifies the finite tabular hazard uses the integration appendix's explicit endpoint
    /// rectangles: the 0.001 upper-loss tail remains a distinct atom, and every populated Total
    /// stream carries exactly one unit of raw recorded mass.
    /// </summary>
    [TestMethod]
    public async Task Test_AgkEndpointRectangles_ExhaustiveAtEveryScope()
    {
        // Arrange: StageFrequency has natural probability support [0.001, 0.999].
        var analysis = new RiskAnalysis(new[] { Component(Consequence("Failure Loss", 300d)) });
        analysis.Options.RiskMeasures = RiskMeasureOptions.None;

        // Act
        await analysis.RunAsync();

        // Assert: system, component, and failure-mode Total streams publish their recorded mass.
        var realization = analysis.MeanRiskResults!;
        Assert.AreEqual(1d, realization.Curves.Total.MassBalance, 0d);
        Assert.AreEqual(1d, realization.Curves.Total.TotalProbability, 0d);
        var component = realization.Components[0];
        Assert.AreEqual(1d, component.Curves.Total.MassBalance, 0d);
        Assert.AreEqual(1d, component.Curves.Total.TotalProbability, 0d);
        Assert.IsTrue(component.FailureModes.Count > 0);
        for (int i = 0; i < component.FailureModes.Count; i++)
        {
            var modeTotal = component.FailureModes[i].Curves.Total;
            if (modeTotal.LECConsequences.Length == 0) continue;
            Assert.AreEqual(1d, modeTotal.MassBalance, 0d);
            Assert.AreEqual(1d, modeTotal.TotalProbability, 0d);
        }

        // The maximum failure consequence occurs only at p = 0.001. The natural AGK panels do
        // not stretch across that tail, so its exceedance probability is the endpoint rectangle.
        const int endpoint = 1;
        Assert.AreEqual(300d, component.Curves.Total.LECConsequences[endpoint], 1e-10, "The true maximum-loss endpoint must be a retained LEC anchor.");
        Assert.AreEqual(0.001d, component.Curves.Total.LECProbabilities[endpoint], 2e-15);
        Assert.IsTrue(realization.FunctionEvaluations >= 2d,
            "The two finite endpoint calculations must be included in the evaluation diagnostic.");
    }
    /// <summary>
    /// Verifies the optional measures are computed by default and skipped when deselected.
    /// </summary>
    [TestMethod]
    public async Task Test_RiskMeasureOptions_SkipDeselectedMeasures()
    {
        // Arrange — the same scenario with and without the optional measures.
        var full = new RiskAnalysis(new[] { Component(Consequence("Failure Loss", 300d)) });
        var lean = new RiskAnalysis(new[] { Component(Consequence("Failure Loss", 300d)) });
        lean.Options.RiskMeasures = RiskMeasureOptions.None;

        // Act
        await full.RunAsync();
        await lean.RunAsync();

        var fullTotal = full.MeanRiskResults!.Components[0].Curves.Total;
        var leanTotal = lean.MeanRiskResults!.Components[0].Curves.Total;

        // Assert — the contract measures always compute, and agree.
        Assert.AreEqual(fullTotal.Mean, leanTotal.Mean, 0d);
        Assert.AreEqual(fullTotal.StandardDeviation, leanTotal.StandardDeviation, 0d);
        Assert.AreEqual(fullTotal.TotalProbability, leanTotal.TotalProbability, 0d);

        // The optional ones compute by default and are absent when deselected.
        Assert.IsFalse(double.IsNaN(fullTotal.Skewness));
        Assert.IsFalse(double.IsNaN(fullTotal.ValueAtRisk));
        Assert.IsTrue(double.IsNaN(leanTotal.Skewness));
        Assert.IsTrue(double.IsNaN(leanTotal.Kurtosis));
        Assert.IsTrue(double.IsNaN(leanTotal.ValueAtRisk));
        Assert.IsTrue(double.IsNaN(leanTotal.ConditionalValueAtRisk));
    }

    /// <summary>
    /// Verifies the adjusted failure-mode curves are absent by default and, when requested, carry
    /// the mode's share of the component's failure probability rather than its raw marginal.
    /// </summary>
    [TestMethod]
    public async Task Test_AdjustedFailureModeCurves_OptInAndSumToComponent()
    {
        // Arrange — two modes combined mutually exclusively, so the adjustment is a normalization.
        var component = TwoModeMethodComponent(FailureModeMethod.MutuallyExclusive);
        var baseline = new RiskAnalysis(new[] { component });
        await baseline.RunAsync();
        Assert.IsNull(baseline.MeanRiskResults!.Components[0].FailureModes[0].AdjustedCurves);

        var adjusted = new RiskAnalysis(new[] { TwoModeMethodComponent(FailureModeMethod.MutuallyExclusive) });
        adjusted.Options.OutputAdjustedFailureModeCurves = true;

        // Act
        await adjusted.RunAsync();

        // Assert — the unadjusted curves are unchanged, and the adjusted ones sum to the component.
        var componentResults = adjusted.MeanRiskResults!.Components[0];
        var modes = componentResults.FailureModes;
        double rawSum = 0d;
        double adjustedSum = 0d;
        for (int i = 0; i < modes.Count; i++)
        {
            Assert.AreEqual(baseline.MeanRiskResults!.Components[0].FailureModes[i].Curves.Fail.TotalProbability,
                modes[i].Curves.Fail.TotalProbability, 0d, "Requesting adjusted curves must not move the unadjusted ones.");
            Assert.IsNotNull(modes[i].AdjustedCurves);
            rawSum += modes[i].Curves.Fail.TotalProbability;
            adjustedSum += modes[i].AdjustedCurves!.Fail.TotalProbability;
        }

        Assert.AreEqual(componentResults.Curves.Fail.TotalProbability, adjustedSum, 1e-9,
            "The adjusted failure probabilities must sum to the component's.");
        Assert.IsTrue(rawSum > adjustedSum,
            "The mutually exclusive normalization must reduce the raw marginal sum.");
    }

    /// <summary>
    /// Verifies both system risk methods admit more than twenty components without performing a
    /// high-dimensional integration in the fast unit-test project.
    /// </summary>
    /// <remarks>
    /// The corresponding computational run belongs to the system-risk verification family.
    /// </remarks>
    [TestMethod]
    public void Test_SystemRisk_BeyondTwentyComponents_ValidatesAndEstimates()
    {
        // Arrange — twenty-four components, mean-only.
        var components = new List<SystemComponent>();
        for (int i = 0; i < 24; i++)
        {
            var component = Component(Consequence($"Failure Loss {i}", 100d + i));
            component.Name = $"Dam {i}";
            components.Add(component);
        }
        var analysis = new RiskAnalysis(components)
        {
            SpecifiedConsequence = "Life Loss",
            ConsequenceUnit = "lives",
        };
        analysis.Options.SystemRiskMethod = SystemRiskType.AdditiveRiskMethod;

        // Act / Assert — validation admits both methods and the estimate reports their cost.
        var (isValid, messages) = analysis.Validate();
        Assert.IsTrue(isValid, string.Join(" | ", messages));

        var estimate = analysis.EstimateResourceRequirements();
        Assert.IsTrue(estimate.PeakLiveBytes > 0);
        Assert.IsTrue(estimate.EstimatedIntegrandEvaluationFloor > 0);

        analysis.Options.SystemRiskMethod = SystemRiskType.JointRiskMethod;
        analysis.Options.WarmupEvaluations = 1000;
        analysis.Options.WarmupCycles = 1;
        analysis.Options.FinalEvaluations = 1000;
        var (jointValid, jointMessages) = analysis.Validate();
        Assert.IsTrue(jointValid, string.Join(" | ", jointMessages));
        Assert.IsTrue(analysis.EstimateResourceRequirements().EstimatedIntegrandEvaluationFloor > 0d);
    }

    /// <summary>Verifies endpoint-aware adaptive-work ranges and optional storage estimates.</summary>
    [TestMethod]
    public void Test_ResourceEstimate_ReportsForcedDepthEndpointsAndOptionalStorage()
    {
        var analysis = new RiskAnalysis(new[] { Component(Consequence("Failure Loss", 100d)) });
        analysis.Options.EstimateMeanRiskOnly = true;

        var estimate = analysis.EstimateResourceRequirements();

        Assert.AreEqual(7352d, estimate.EstimatedIntegrandEvaluationFloor,
            "Fifty bins require seven evaluated G10K21 panels at depth two, plus two endpoints.");
        Assert.AreEqual(1_001_073d, estimate.EstimatedIntegrandEvaluationCeiling,
            "The cap includes bounded two-child and remaining-bin panel overhead plus endpoints.");
        Assert.IsTrue(estimate.Items.Any(item => item.Label == "Pooled quadrature mass ledgers"));
        Assert.IsTrue(estimate.Items.Any(item => item.Label == "Risk profile arrays"));

        analysis.Options.RiskMeasures = RiskMeasureOptions.None;
        var withoutProfiles = analysis.EstimateResourceRequirements();
        Assert.IsFalse(withoutProfiles.Items.Any(item => item.Label == "Risk profile arrays"));
    }

    /// <summary>
    /// Verifies a wide joint component is not rejected by any dense-matrix limit and instead
    /// reports the lazy buffer range and combinatorial slow-convergence warning.
    /// </summary>
    [TestMethod]
    public void Test_ResourceEstimate_WideJointFailureModes_ReportsLazyRange()
    {
        var component = new SystemComponent { Name = "Wide Joint" };
        component.HazardFunction = StageFrequency();
        for (int i = 0; i < 32; i++)
        {
            component.AddFailureMode(new FailureMode(null, null, Fragility(), Consequence($"Loss {i}", 100d)));
        }
        component.AddFailureMode(new FailureMode(null, null, null, Consequence("Non-Failure Loss", 60d)));
        component.FailureModeMethod = FailureModeMethod.JointFailures;
        var analysis = new RiskAnalysis(new[] { component })
        {
            SpecifiedConsequence = "Life Loss",
            ConsequenceUnit = "lives",
        };

        var estimate = analysis.EstimateResourceRequirements();
        var lazyItem = estimate.Items.Single(item => item.Label == "Lazy combination buffers — Wide Joint");

        Assert.AreEqual(ResourceSeverity.Warning, lazyItem.Severity);
        StringAssert.Contains(lazyItem.Message, "convergence is slow");
        StringAssert.Contains(lazyItem.Message, "No dense");
        Assert.IsTrue(analysis.Validate().IsValid);
    }
    /// <summary>
    /// Bit-pins a fully deterministic scenario. The expected values include the Appendix-D
    /// endpoint rectangles, while definition hashes and sampler seeds remain unchanged. Any
    /// future numerical change must be justified independently before updating these pins.
    /// </summary>
    [TestMethod]
    public async Task Test_Deterministic_BitPin_CascadePhases()
    {
        // Arrange — the standard deterministic scenario, mean-only.
        var meanOnly = new RiskAnalysis(new[] { Component(Consequence("Failure Loss", 300d)) });

        // Act
        await meanOnly.RunAsync();

        // Assert — exact bit patterns of the headline scalars.
        var summary = meanOnly.RiskResults![0]!;
        Assert.AreEqual(4633156762156744978L, BitConverter.DoubleToInt64Bits(summary.Total.Mean), "Total.Mean moved.");
        Assert.AreEqual(4631174403777349894L, BitConverter.DoubleToInt64Bits(summary.Fail.Mean), "Fail.Mean moved.");
        Assert.AreEqual(4629978886483779793L, BitConverter.DoubleToInt64Bits(summary.Excess.Mean), "Excess.Mean moved.");
        Assert.AreEqual(4627048969028059266L, BitConverter.DoubleToInt64Bits(summary.Background.Mean), "Background.Mean moved.");
        Assert.AreEqual(4597854404619542115L, BitConverter.DoubleToInt64Bits(summary.Fail.TotalProbability), "Fail.TotalProbability moved.");

        // A deterministic full-uncertainty run pins the ensemble path too (every realization is
        // identical by construction; the value differs from the mean pass only by the documented
        // ensemble integration discipline).
        var full = new RiskAnalysis(new[] { Component(Consequence("Failure Loss", 300d)) });
        full.Options.EstimateMeanRiskOnly = false;
        full.Options.Realizations = 100;
        await full.RunAsync();
        Assert.AreEqual(4633156767926136440L, BitConverter.DoubleToInt64Bits(full.RiskResults![50]!.Total.Mean),
            "Mid-ensemble realization Total.Mean moved.");
    }

    /// <summary>
    /// Verifies the mean-only smoke: a single-entry ensemble, the risk-type decomposition
    /// identities E[C_T] = E[C_F] + E[C_NF] = E[C_Δ] + E[C_B] (exact here — the failure
    /// consequence dominates the non-failure everywhere, so the excess clamp never binds), and
    /// live integrator diagnostics.
    /// </summary>
    [TestMethod]
    public async Task Test_MeanOnly_Smoke_DecompositionIdentities()
    {
        // Arrange
        var analysis = new RiskAnalysis(new[] { Component(Consequence("Failure Loss", 300d)) });

        // Act
        await analysis.RunAsync();

        // Assert
        Assert.IsTrue(analysis.IsEstimated);
        Assert.IsNotNull(analysis.RiskResults);
        Assert.AreEqual(1, analysis.RiskResults!.Count);
        var summary = analysis.RiskResults[0]!;
        Assert.IsTrue(summary.Fail.TotalProbability > 0d && summary.Fail.TotalProbability < 1d);
        Assert.AreEqual(summary.Total.Mean, summary.Fail.Mean + summary.NonFail.Mean, 1e-9 * summary.Total.Mean,
            "E[C_T] must decompose into failure plus non-failure risk.");
        Assert.AreEqual(summary.Total.Mean, summary.Excess.Mean + summary.Background.Mean, 1e-9 * summary.Total.Mean,
            "E[C_T] must decompose into incremental plus background risk.");
        Assert.IsTrue(summary.FunctionEvaluations > 100d, "The adaptive integrator must have run.");
        Assert.IsNotNull(analysis.MeanRiskResults);
        Assert.IsTrue(analysis.MeanRiskResults!.Curves.Total.LECConsequences.Length > 2);
    }

    /// <summary>
    /// Verifies the mean-only total risk against a dense independent trapezoid reference over
    /// the same sampled math (twenty thousand probability ordinates).
    /// </summary>
    [TestMethod]
    public async Task Test_MeanOnly_MeanVsDenseReference()
    {
        // Arrange
        var component = Component(Consequence("Failure Loss", 300d));
        var analysis = new RiskAnalysis(new[] { component });

        // Act
        await analysis.RunAsync();

        // The run uses an immutable clone, so prepare the independent authoring component before
        // drawing the mean sample used by this reference.
        component.SetupSamplers(1, analysis.Options.PRNGSeed, SamplingScheme.MonteCarlo);
        var sampled = component.Sample(-1);
        var scratch = new ComponentRealization(sampled.FailureModeCount);
        var flags = new RiskComputeFlags();
        int gridCount = 20_000;
        double lower = 1e-16;
        double upper = 1d - 1e-16;
        double step = (upper - lower) / gridCount;
        double reference = 0d;
        double previous = Integrand(lower);
        for (int i = 1; i <= gridCount; i++)
        {
            double current = Integrand(lower + i * step);
            reference += 0.5d * (previous + current) * step;
            previous = current;
        }

        double Integrand(double probability)
        {
            double hazard = sampled.Hazard.InverseCDF(probability);
            var output = sampled.ComputeRisk(probability, hazard, flags, scratch);
            return output.ProbabilityOfFailure * output.MeanFailureConsequences
                + output.ProbabilityOfNonFailure * output.NonFailureConsequences;
        }

        // Assert — the recorded-curve mean matches the dense reference within 0.1%.
        double engineMean = analysis.RiskResults![0]!.Total.Mean;
        Assert.AreEqual(reference, engineMean, 1e-3 * reference,
            $"Engine mean {engineMean} vs dense reference {reference}.");
    }

    /// <summary>
    /// Verifies the multi-stage acceptance end to end: a component carrying a
    /// single-stage mode AND a two-stage progression chain (each its own singleton combination
    /// unit) integrates to the same mean-only total risk as a dense independent trapezoid over
    /// the same sampled math, whose per-mode probabilities now include the polarity product.
    /// </summary>
    [TestMethod]
    public async Task Test_MeanOnly_TwoStage_MeanVsDenseReference()
    {
        // Arrange — the standard component plus a two-stage chain (stage 1's wider fragility
        // rises over (10 → 0, 30 → 1)).
        var wide = Fragility();
        wide.UncertainOrderedPairedData = new UncertainOrderedPairedData(
            new[] { new UncertainOrdinate(10d, new Deterministic(0d)), new UncertainOrdinate(30d, new Deterministic(1d)) },
            true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic);
        var component = Component(Consequence("Failure Loss", 300d));
        component.AddFailureMode(new FailureMode(
            new List<ResponseStage>
            {
                new ResponseStage(new List<ITransformFunction>(), Fragility()),
                new ResponseStage(new List<ITransformFunction>(), wide),
            },
            null, new List<IConsequenceFunction> { Consequence("Chained Loss", 200d) }));
        var analysis = new RiskAnalysis(new[] { component });

        // Act
        await analysis.RunAsync();

        // The reference: a dense trapezoid over the mean sampled component.
        component.SetupSamplers(1, analysis.Options.PRNGSeed, SamplingScheme.MonteCarlo);
        var sampled = component.Sample(-1);
        var scratch = new ComponentRealization(sampled.FailureModeCount);
        var flags = new RiskComputeFlags();
        int gridCount = 20_000;
        double lower = 1e-16;
        double upper = 1d - 1e-16;
        double step = (upper - lower) / gridCount;
        double reference = 0d;
        double previous = Integrand(lower);
        for (int i = 1; i <= gridCount; i++)
        {
            double current = Integrand(lower + i * step);
            reference += 0.5d * (previous + current) * step;
            previous = current;
        }

        double Integrand(double probability)
        {
            double hazard = sampled.Hazard.InverseCDF(probability);
            var output = sampled.ComputeRisk(probability, hazard, flags, scratch);
            return output.ProbabilityOfFailure * output.MeanFailureConsequences
                + output.ProbabilityOfNonFailure * output.NonFailureConsequences;
        }

        // Assert — the engine mean matches the dense reference within 0.1%, and the chained
        // mode genuinely contributed (the union exceeds the single-mode component's).
        double engineMean = analysis.RiskResults![0]!.Total.Mean;
        Assert.AreEqual(reference, engineMean, 1e-3 * reference,
            $"Engine mean {engineMean} vs dense reference {reference}.");

        var single = new RiskAnalysis(new[] { Component(Consequence("Failure Loss", 300d)) });
        await single.RunAsync();
        Assert.IsTrue(analysis.RiskResults[0]!.Fail.TotalProbability > single.RiskResults![0]!.Fail.TotalProbability,
            "The two-stage mode must add failure probability to the union.");
    }

    /// <summary>
    /// Verifies the mixture exposure-branch enumeration
    /// (docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §6.4.1): the mean-only mean matches the flattened
    /// (average) composite exactly, while the loss-exceedance spread is strictly larger because
    /// the branches are enumerated instead of collapsed — the corrected v1.0 day/night defect.
    /// </summary>
    [TestMethod]
    public async Task Test_MixtureExposure_MeanParity_SigmaInflation()
    {
        // Arrange — identical children and weights; only the combine mode differs.
        static CompositeConsequence Composite(CompositeFunctionType type)
        {
            return new CompositeConsequence(new[]
            {
                new WeightedConsequenceFunction(Consequence("Day", 100d), 0.55d),
                new WeightedConsequenceFunction(Consequence("Night", 300d), 0.45d),
            })
            {
                Name = "Day/Night",
                SpecifiedHazard = "Stage",
                HazardUnit = "ft",
                SpecifiedConsequence = "Life Loss",
                ConsequenceUnit = "lives",
                CompositeFunctionType = type,
            };
        }
        var mixtureAnalysis = new RiskAnalysis(new[] { Component(Composite(CompositeFunctionType.Mixture)) });
        var averageAnalysis = new RiskAnalysis(new[] { Component(Composite(CompositeFunctionType.Average)) });

        // Act
        await mixtureAnalysis.RunAsync();
        await averageAnalysis.RunAsync();

        // Assert — the mixture identity Σ wᵢ·P_F·fᵢ = P_F·Σ wᵢ·fᵢ keeps the mean unchanged...
        var mixture = mixtureAnalysis.RiskResults![0]!;
        var average = averageAnalysis.RiskResults![0]!;
        Assert.AreEqual(average.Fail.Mean, mixture.Fail.Mean, 1e-9 * average.Fail.Mean,
            "Branch enumeration must not move the mean (the free regression gate).");

        // ...while the enumerated branches carry the exposure spread the flattened curve destroys.
        Assert.IsTrue(mixture.Fail.StandardDeviation > average.Fail.StandardDeviation * 1.05d,
            $"Mixture σ {mixture.Fail.StandardDeviation} must exceed flattened σ {average.Fail.StandardDeviation}.");
    }

    /// <summary>
    /// Verifies the validation catalog with its pinned messages: the empty analysis, the
    /// additive method's strict-independence requirement, the joint method's
    /// dimension limit and correlation-matrix checks, and the transitional cascade
    /// gate (single-terminal chains compute; state-group configurations wait for the group
    /// layer). Two independent additive components and reliability mode now validate — their
    /// original gates are gone, as is the multi-stage constructor throw.
    /// </summary>
    [TestMethod]
    public async Task Test_Validate_StageGates_PinnedMessages()
    {
        // No components.
        var empty = new RiskAnalysis(Array.Empty<SystemComponent>());
        Assert.IsTrue(empty.Validate().ValidationMessages.Any(m => m.Contains("no system components")));
        var structured = empty.ValidateIssues();
        Assert.IsTrue(structured.Any(i => i.Severity == DiagnosticSeverity.Error));
        Assert.IsTrue(structured.Any(i => i.Code == "TRV0001"));
        CollectionAssert.AreEqual(
            structured.Select(i => i.ToLegacyMessage()).ToList(),
            empty.Validate().ValidationMessages);

        // Two independent additive components validate (the earlier single-component gate is gone).
        var two = new RiskAnalysis(new[] { Component(Consequence("A", 300d)), Component(Consequence("B", 300d)) });
        Assert.IsTrue(two.Validate().IsValid);

        // The additive method with a hazard dependence → the strict-independence error.
        var dependentAdditive = new RiskAnalysis(new[] { Component(Consequence("A", 300d)), Component(Consequence("B", 300d)) });
        dependentAdditive.Options.ComponentHazardDependency = DependencyType.PerfectlyPositive;
        Assert.IsTrue(dependentAdditive.Validate().ValidationMessages.Any(m => m.Contains("strictly independent")));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => dependentAdditive.RunAsync());

        // The joint method under the correlation-matrix dependency needs a valid matrix.
        var jointMatrix = new RiskAnalysis(new[] { Component(Consequence("A", 300d)), Component(Consequence("B", 300d)) });
        jointMatrix.Options.SystemRiskMethod = SystemRiskType.JointRiskMethod;
        jointMatrix.Options.ComponentHazardDependency = DependencyType.CorrelationMatrix;
        Assert.IsTrue(jointMatrix.Validate().ValidationMessages.Any(m => m.Contains("correlation matrix")),
            "A missing matrix under the correlation-matrix dependency must be an error.");
        jointMatrix.Options.HazardCorrelationMatrix = new[,] { { 1d, 2d }, { 2d, 1d } };
        Assert.IsTrue(jointMatrix.Validate().ValidationMessages.Any(m => m.Contains("positive-definite")),
            "A non-positive-definite matrix must be an error.");
        jointMatrix.Options.HazardCorrelationMatrix = new[,] { { 1d, 0.5d }, { 0.5d, 1d } };
        Assert.IsTrue(jointMatrix.Validate().IsValid, "A valid matrix passes.");

        // Reliability mode validates (its earlier gate is gone).
        var reliability = new RiskAnalysis(new[] { Component(Consequence("A", 300d)) });
        reliability.Options.Mode = RiskAnalysisMode.Reliability;
        Assert.IsTrue(reliability.Validate().IsValid);

        // The earlier multi-stage constructor throw is gone: a single-terminal two-stage chain
        // validates and computes under the multi-stage acceptance.
        var multiStage = Component(Consequence("A", 300d));
        var chained = new FailureMode(
            new List<ResponseStage>
            {
                new ResponseStage(new List<ITransformFunction>(), Fragility()),
                new ResponseStage(new List<ITransformFunction>(), Fragility()),
            },
            null, new List<IConsequenceFunction> { Consequence("Chained", 100d) });
        multiStage.AddFailureMode(chained);
        var multiStageAnalysis = new RiskAnalysis(new[] { multiStage });
        Assert.IsTrue(multiStageAnalysis.Validate().IsValid,
            string.Join(" | ", multiStageAnalysis.Validate().ValidationMessages));

        // The state-group layer accepts the cascade configurations the transitional Stage 2
        // gate held back: a Non-Fail-final end state (a branch-scoped non-failure consequence)
        // and both-port divergence now validate.
        var partialOnly = Component(Consequence("A", 300d));
        partialOnly.AddFailureMode(new FailureMode(
            new List<ResponseStage>
            {
                new ResponseStage(new List<ITransformFunction>(), Fragility(), BranchPolarity.NonFail),
            },
            null, new List<IConsequenceFunction> { Consequence("Partial", 50d) }));
        Assert.IsTrue(new RiskAnalysis(new[] { partialOnly }).Validate().IsValid);

        var divergent = Component(Consequence("A", 300d));
        var response = divergent.Graph.GetElements<RMC.TotalRisk.Systems.Components.Graph.ResponseElement>().First();
        var partialTerminal = new RMC.TotalRisk.Systems.Components.Graph.ConsequenceElement("Partial Damages")
        {
            Input = new RMC.TotalRisk.Systems.Components.Graph.RiskConnection(response, 1),
        };
        partialTerminal.Functions.Add(Consequence("Partial", 50d));
        divergent.Graph.AddElement(partialTerminal);
        Assert.IsTrue(new RiskAnalysis(new[] { divergent }).Validate().IsValid);

        // The §7.9 gates that stay: a second claiming state group, and competing over an
        // else-chain failure state.
        var doubleClaim = Component(Consequence("A", 300d));
        var claimResponse = doubleClaim.Graph.GetElements<RMC.TotalRisk.Systems.Components.Graph.ResponseElement>().First();
        var claimOne = new RMC.TotalRisk.Systems.Components.Graph.ConsequenceElement("Partial One")
        {
            Input = new RMC.TotalRisk.Systems.Components.Graph.RiskConnection(claimResponse, 1),
        };
        claimOne.Functions.Add(Consequence("Partial 1", 50d));
        doubleClaim.Graph.AddElement(claimOne);
        var secondResponse = new RMC.TotalRisk.Systems.Components.Graph.ResponseElement("Second Response")
        {
            Function = Fragility(),
            Input = new RMC.TotalRisk.Systems.Components.Graph.RiskConnection(doubleClaim.Graph.GetElements<RMC.TotalRisk.Systems.Components.Graph.HazardElement>().First()),
        };
        var claimTwoFail = new RMC.TotalRisk.Systems.Components.Graph.ConsequenceElement("Second Failure")
        {
            Input = new RMC.TotalRisk.Systems.Components.Graph.RiskConnection(secondResponse),
        };
        claimTwoFail.Functions.Add(Consequence("Second Loss", 100d));
        var claimTwo = new RMC.TotalRisk.Systems.Components.Graph.ConsequenceElement("Partial Two")
        {
            Input = new RMC.TotalRisk.Systems.Components.Graph.RiskConnection(secondResponse, 1),
        };
        claimTwo.Functions.Add(Consequence("Partial 2", 25d));
        doubleClaim.Graph.AddElement(secondResponse);
        doubleClaim.Graph.AddElement(claimTwoFail);
        doubleClaim.Graph.AddElement(claimTwo);
        Assert.IsTrue(new RiskAnalysis(new[] { doubleClaim }).Validate().ValidationMessages
            .Any(m => m.StartsWith("Error:", StringComparison.Ordinal) && m.Contains("one state group")));

        var elseChain = Component(Consequence("A", 300d));
        var elseRoot = elseChain.Graph.GetElements<RMC.TotalRisk.Systems.Components.Graph.ResponseElement>().First();
        var elseNext = new RMC.TotalRisk.Systems.Components.Graph.ResponseElement("Else Response")
        {
            Function = Fragility(),
            Input = new RMC.TotalRisk.Systems.Components.Graph.RiskConnection(elseRoot, 1),
        };
        var elseTerminal = new RMC.TotalRisk.Systems.Components.Graph.ConsequenceElement("Else Failure")
        {
            Input = new RMC.TotalRisk.Systems.Components.Graph.RiskConnection(elseNext),
        };
        elseTerminal.Functions.Add(Consequence("Else Loss", 100d));
        elseChain.Graph.AddElement(elseNext);
        elseChain.Graph.AddElement(elseTerminal);
        elseChain.FailureModeMethod = FailureModeMethod.CompetingFailures;
        Assert.IsTrue(new RiskAnalysis(new[] { elseChain }).Validate().ValidationMessages
            .Any(m => m.StartsWith("Error:", StringComparison.Ordinal) && m.Contains("weak-link")));
        elseChain.FailureModeMethod = FailureModeMethod.JointFailures;
        Assert.IsTrue(new RiskAnalysis(new[] { elseChain }).Validate().IsValid,
            "The else-chain is legal outside the competing method.");
    }

    /// <summary>
    /// Verifies the event lifecycle: a vetoed start completes as canceled without running; a
    /// successful run raises the succeeded completion; an already-canceled token surfaces as a
    /// canceled completion and also propagates cancellation through the returned task.
    /// </summary>
    [TestMethod]
    public async Task Test_Events_Lifecycle()
    {
        // Vetoed start.
        var vetoed = new RiskAnalysis(new[] { Component(Consequence("A", 300d)) });
        AnalysisRunCompletedEventArgs? vetoedArgs = null;
        vetoed.AnalysisStarting += (_, e) => e.Cancel = true;
        vetoed.AnalysisCompleted += (_, e) => vetoedArgs = e;
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => vetoed.RunAsync());
        Assert.IsNotNull(vetoedArgs);
        Assert.IsTrue(vetoedArgs!.Cancelled);
        Assert.IsFalse(vetoed.IsEstimated);

        // Successful run.
        var success = new RiskAnalysis(new[] { Component(Consequence("A", 300d)) });
        AnalysisRunCompletedEventArgs? successArgs = null;
        success.AnalysisCompleted += (_, e) => successArgs = e;
        await success.RunAsync();
        Assert.IsNotNull(successArgs);
        Assert.IsTrue(successArgs!.Succeeded);
        Assert.IsTrue(success.IsEstimated);

        // A pre-canceled external token.
        var canceled = new RiskAnalysis(new[] { Component(Consequence("A", 300d)) });
        AnalysisRunCompletedEventArgs? canceledArgs = null;
        canceled.AnalysisCompleted += (_, e) => canceledArgs = e;
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => canceled.RunAsync(cancellationToken: source.Token));
        Assert.IsNotNull(canceledArgs);
        Assert.IsTrue(canceledArgs!.Cancelled);
        Assert.IsFalse(canceled.IsEstimated);
        Assert.IsFalse(canceled.IsRunning);
        Assert.IsFalse(vetoed.IsRunning);
    }
    /// <summary>
    /// Verifies an exception raised by the evaluation path propagates through the returned task,
    /// is reported by completion before that task ends, and leaves no partial publication.
    /// </summary>
    [TestMethod]
    public async Task Test_RunAsync_ThrownEvaluationPropagatesAtomically()
    {
        var analysis = new RiskAnalysis(new[] { Component(Consequence("A", 300d)) });
        analysis.Options.RiskIntegrand = RiskIntegrand.Balanced;
        var fault = new ApplicationException("Injected evaluation fault.");
        analysis.RunWorkerObserver = () => throw fault;
        AnalysisRunCompletedEventArgs? completion = null;
        analysis.AnalysisCompleted += (_, e) => completion = e;

        var propagated = await Assert.ThrowsExceptionAsync<ApplicationException>(() => analysis.RunAsync());

        Assert.AreSame(fault, propagated);
        Assert.IsNotNull(completion);
        Assert.AreSame(fault, completion!.Error);
        Assert.IsFalse(completion.Succeeded);
        Assert.IsFalse(completion.Cancelled);
        Assert.IsNull(analysis.RiskResults);
        Assert.IsNull(analysis.MeanRiskResults);
        Assert.IsFalse(analysis.IsEstimated);
        Assert.IsFalse(analysis.IsRunning);
    }

    /// <summary>
    /// Verifies cancellation requested after integration has started propagates as cancellation,
    /// completes notification before the task ends, and publishes no partial result.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task Test_RunAsync_ActiveExternalCancellationPropagatesAtomically()
    {
        var analysis = new RiskAnalysis(new[] { Component(Consequence("A", 300d)) });
        analysis.Options.RiskIntegrand = RiskIntegrand.MeanTotalRisk;
        using var source = new CancellationTokenSource();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        analysis.RunWorkerObserver = () =>
        {
            entered.TrySetResult(true);
            while (!source.IsCancellationRequested) Thread.Yield();
        };
        AnalysisRunCompletedEventArgs? completion = null;
        analysis.AnalysisCompleted += (_, e) => completion = e;

        Task run = analysis.RunAsync(cancellationToken: source.Token);
        Task first = await Task.WhenAny(entered.Task, run);
        Assert.AreSame(entered.Task, first, "The run ended before the computation worker started.");
        source.Cancel();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => run);

        Assert.IsNotNull(completion);
        Assert.IsTrue(completion!.Cancelled);
        Assert.IsFalse(completion.Succeeded);
        Assert.IsNull(completion.Error);
        Assert.IsNull(analysis.RiskResults);
        Assert.IsNull(analysis.MeanRiskResults);
        Assert.IsFalse(analysis.IsEstimated);
        Assert.IsFalse(analysis.IsRunning);
    }

    /// <summary>Verifies computation warning strings are exact adapters over structured diagnostics.</summary>
    [TestMethod]
    public async Task Test_ComputationDiagnostics_StructuredWarningAdapter()
    {
        var analysis = new RiskAnalysis(new[] { Component(Consequence("A", -300d)) });

        await analysis.RunAsync();

        Assert.IsTrue(analysis.ComputationDiagnostics.Count > 0);
        Assert.IsTrue(analysis.ComputationDiagnostics.Any(d =>
            d.Code == "TRC1003" &&
            d.Severity == DiagnosticSeverity.Warning &&
            d.ObjectPath == "/Consequences/Excess"));
        CollectionAssert.AreEqual(
            analysis.ComputationDiagnostics.Select(d => d.ToLegacyMessage()).ToList(),
            analysis.ComputationWarnings.ToList());
        Assert.ThrowsException<NotSupportedException>(() =>
            ((IList<ComputationDiagnostic>)analysis.ComputationDiagnostics).Add(
                new ComputationDiagnostic("TRC9999", DiagnosticSeverity.Warning, "x", "")));
    }

    /// <summary>
    /// Verifies a successful run stamps both roots with the same deterministic manifest and that
    /// its effective-options fingerprint matches the definition contract.
    /// </summary>
    [TestMethod]
    public async Task Test_RunManifest_IsDeterministicAndMatchesDefinition()
    {
        var analysis = new RiskAnalysis(new[] { Component(Consequence("A", 300d)) });

        await analysis.RunAsync();
        var first = analysis.RiskResults!.Manifest;
        string firstResults = analysis.RiskResults.ToJson();

        Assert.IsNotNull(first);
        Assert.IsTrue(analysis.RiskResults.IsProvenanceVerified);
        Assert.AreSame(first, analysis.MeanRiskResults!.Manifest);
        Assert.AreEqual(Convert.ToHexString(analysis.Options.CanonicalHash()), first!.EffectiveOptionsHash);
        Assert.AreEqual(analysis.Options.PRNGSeed, first.PRNGSeed);
        Assert.AreEqual(1, first.ComponentContentHashes.Length);
        Assert.AreEqual(64, first.AnalysisContentHash.Length);
        Assert.AreEqual(64, first.SamplerSeedMapHash.Length);

        await analysis.RunAsync();
        Assert.AreEqual(firstResults, analysis.RiskResults!.ToJson(),
            "An identical run must publish byte-identical JSON including provenance.");

        analysis.Options.PRNGSeed++;
        await analysis.RunAsync();
        Assert.AreNotEqual(first.EffectiveOptionsHash,
            analysis.RiskResults!.Manifest!.EffectiveOptionsHash);
        Assert.AreNotEqual(first.AnalysisContentHash,
            analysis.RiskResults.Manifest.AnalysisContentHash);
    }


    /// <summary>
    /// A second run fails immediately while the first owns the execution slot; the first run can
    /// still be canceled and both attempts raise completion notifications before their tasks end.
    /// </summary>
    [TestMethod]
    public async Task Test_RunAsync_ConcurrentRunGuardAndCompletionOrdering()
    {
        var analysis = new RiskAnalysis(new[] { Component(Consequence("A", 300d), UncertainFragility()) });
        analysis.Options.EstimateMeanRiskOnly = false;
        analysis.Options.Realizations = 100;
        var completions = new List<AnalysisRunCompletedEventArgs>();
        analysis.AnalysisCompleted += (_, e) => completions.Add(e);

        Task first = analysis.RunAsync();
        Assert.IsTrue(analysis.IsRunning);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => analysis.RunAsync());
        Assert.IsTrue(analysis.IsRunning, "Rejecting a second call must not release the first run's slot.");

        analysis.CancelAnalysis();
        try
        {
            await first;
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation wins the race with completion.
        }

        Assert.IsFalse(analysis.IsRunning);
        Assert.IsTrue(completions.Exists(e => e.Error is InvalidOperationException));
        Assert.IsTrue(completions.Exists(e => e.Cancelled) || completions.Exists(e => e.Succeeded));
    }

    /// <summary>
    /// Options and component functions are deep-snapshotted before background computation, so
    /// authoring edits made after <see cref="IAnalysis.RunAsync"/> returns its task affect the
    /// next run only.
    /// </summary>
    [TestMethod]
    public async Task Test_RunAsync_AuthoringMutationDoesNotAffectActiveSnapshot()
    {
        var baselineConsequence = Consequence("Failure Loss", 300d);
        var baseline = new RiskAnalysis(new[] { Component(baselineConsequence) });
        await baseline.RunAsync();

        var editedConsequence = Consequence("Failure Loss", 300d);
        var edited = new RiskAnalysis(new[] { Component(editedConsequence) });
        Task active = edited.RunAsync();
        Assert.IsTrue(edited.IsRunning);

        edited.Options.PRNGSeed = 98765;
        editedConsequence.UncertainOrderedPairedData = Consequence("Changed Failure Loss", 600d).UncertainOrderedPairedData;

        await active;

        Assert.AreEqual(98765, edited.Options.PRNGSeed, "The authoring edit must remain available for the next run.");
        Assert.AreEqual(baseline.MeanRiskResults!.ToJson(), edited.MeanRiskResults!.ToJson(),
            "The active run must remain bit-identical to the pre-edit snapshot.");
        Assert.IsTrue(edited.IsEstimated);
        Assert.IsFalse(edited.IsRunning);
    }

    /// <summary>
    /// Validation failures notify completion, propagate through the task, and leave no prior
    /// result partially published.
    /// </summary>
    [TestMethod]
    public async Task Test_RunAsync_ValidationFailureClearsPublishedState()
    {
        var analysis = new RiskAnalysis(new[] { Component(Consequence("A", 300d)) });
        await analysis.RunAsync();
        Assert.IsNotNull(analysis.MeanRiskResults);

        AnalysisRunCompletedEventArgs? completion = null;
        analysis.AnalysisCompleted += (_, e) => completion = e;
        analysis.Options.MaxDepth = 1;
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => analysis.RunAsync());

        Assert.IsNotNull(completion);
        Assert.IsInstanceOfType<InvalidOperationException>(completion!.Error);
        Assert.IsNull(analysis.RiskResults);
        Assert.IsNull(analysis.MeanRiskResults);
        Assert.IsFalse(analysis.IsEstimated);
        Assert.IsFalse(analysis.IsRunning);
    }
    /// <summary>
    /// Verifies options-only serialization (docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §8)
    /// and the results-through-
    /// constructor round trip.
    /// </summary>
    [TestMethod]
    public async Task Test_ToXElement_OptionsOnly_CtorRoundTrip()
    {
        // Arrange
        var analysis = new RiskAnalysis(new[] { Component(Consequence("A", 300d)) })
        {
            Name = "Levee Study",
            Description = "Stage 4 smoke",
            SpecifiedConsequence = "Life Loss",
            ConsequenceUnit = "lives",
        };
        analysis.Options.Alpha = 0.02d;
        await analysis.RunAsync();

        // Act
        var element = analysis.ToXElement();
        var restored = new RiskAnalysis(new[] { Component(Consequence("A", 300d)) }, element,
            analysis.RiskResults, analysis.MeanRiskResults);

        // Assert — configuration only: the options child and the declared-axis child (empty
        // here — the legacy single-type declaration), nothing else.
        Assert.AreEqual(2, element.Elements().Count());
        Assert.AreEqual(nameof(RiskAnalysisOptions), element.Elements().First().Name.LocalName);
        Assert.AreEqual(nameof(RiskAnalysis.AdditionalConsequenceTypes), element.Elements().Skip(1).First().Name.LocalName);
        Assert.AreEqual(0, restored.AdditionalConsequenceTypes.Count);
        Assert.AreEqual("Levee Study", restored.Name);
        Assert.AreEqual(0.02d, restored.Options.Alpha, 0d);
        Assert.IsTrue(restored.IsEstimated, "Supplied results restore the estimated state.");
        Assert.AreSame(analysis.RiskResults, restored.RiskResults);

        // Option edits invalidate the results.
        restored.Options.Alpha = 0.05d;
        Assert.IsFalse(restored.IsEstimated);
    }

    /// <summary>Builds a second, distinct component (steeper fragility, smaller consequences).</summary>
    private static SystemComponent ComponentB()
    {
        var fragility = new TabularResponse
        {
            Name = "Fragility B",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(14d, new Deterministic(0d)), new UncertainOrdinate(24d, new Deterministic(1d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
        var component = new SystemComponent { Name = "Levee" };
        component.HazardFunction = StageFrequency();
        component.AddFailureMode(new FailureMode(null, null, fragility, Consequence("Levee Failure Loss", 150d)));
        component.AddFailureMode(new FailureMode(null, null, null, Consequence("Levee Non-Failure Loss", 25d)));
        return component;
    }

    /// <summary>Builds a consequence-free component for reliability mode.</summary>
    private static SystemComponent ReliabilityComponent(string name, IResponseFunction? response = null)
    {
        var component = new SystemComponent { Name = name };
        component.HazardFunction = StageFrequency();
        component.AddFailureMode(new FailureMode(null, null, response ?? Fragility(), null));
        return component;
    }

    /// <summary>
    /// Verifies the additive system aggregation: the convolved system mean equals the
    /// sum of the component means (the v1.0 mean-parity gate, exact by construction), the system
    /// failure probability is the independent union with the v1.0 stream-probability semantics,
    /// independent variances add, the decomposition identity holds, and — the headline v1.1
    /// capability — a true system loss exceedance curve exists where v1.0 produced none.
    /// </summary>
    [TestMethod]
    public async Task Test_AdditiveSystem_TwoComponents_ExactAggregates()
    {
        // Arrange
        var analysis = new RiskAnalysis(new[] { Component(Consequence("Failure Loss", 300d)), ComponentB() });

        // Act
        await analysis.RunAsync();

        // Assert
        var summary = analysis.RiskResults![0]!;
        var componentA = summary.ComponentResults[0];
        var componentB = summary.ComponentResults[1];

        // Mean parity: system mean == Σ component means (per stream).
        double expectedTotalMean = componentA.Total.Mean + componentB.Total.Mean;
        Assert.AreEqual(expectedTotalMean, summary.Total.Mean, 1e-6 * expectedTotalMean,
            "The convolved system Total mean must equal the sum of the component means (the v1.0 additive answer).");
        double expectedFailMean = componentA.Fail.Mean + componentB.Fail.Mean;
        Assert.AreEqual(expectedFailMean, summary.Fail.Mean, 1e-6 * expectedFailMean,
            "The convolved system Fail mean must equal the sum of the component means.");

        // The failure union and the v1.0 stream-probability semantics.
        double union = 1d - (1d - componentA.Fail.TotalProbability) * (1d - componentB.Fail.TotalProbability);
        Assert.AreEqual(union, summary.Fail.TotalProbability, 1e-12, "System AFP must be the independent union.");
        Assert.AreEqual(union, summary.Excess.TotalProbability, 1e-12);
        Assert.AreEqual(1d - union, summary.NonFail.TotalProbability, 1e-12);

        // Independent variances add (within the lattice quantization).
        double expectedTotalVariance = componentA.Total.StandardDeviation * componentA.Total.StandardDeviation
            + componentB.Total.StandardDeviation * componentB.Total.StandardDeviation;
        double systemVariance = summary.Total.StandardDeviation * summary.Total.StandardDeviation;
        Assert.AreEqual(expectedTotalVariance, systemVariance, 1e-4 * expectedTotalVariance,
            "Independent component variances must add through the convolution.");

        // Decomposition identity survives aggregation.
        Assert.AreEqual(summary.Total.Mean, summary.Fail.Mean + summary.NonFail.Mean, 1e-9 * summary.Total.Mean);

        // The system LEC exists (v1.0's additive path produced no system curve at all).
        var systemLec = analysis.MeanRiskResults!.Curves.Total;
        Assert.IsTrue(systemLec.LECConsequences.Length > 2, "The additive system Total LEC must be produced.");
        Assert.IsTrue(systemLec.LECConsequences[0] > 300d,
            "The system curve support must extend beyond a single component's maximum (the summed tail).");
        Assert.AreEqual(1d, systemLec.MassBalance, 1e-9, "The exhaustive system budget must be exactly one.");
    }

    /// <summary>
    /// Verifies reliability mode on a single consequence-free component: risk-mode
    /// validation rejects the model, reliability-mode validation accepts it, and the annualized
    /// failure probability matches a dense independent reference at every level — failure mode,
    /// component, and system.
    /// </summary>
    [TestMethod]
    public async Task Test_Reliability_SingleComponent_AfpVsDenseReference()
    {
        // Arrange
        var component = ReliabilityComponent("Dam");
        var analysis = new RiskAnalysis(new[] { component });
        Assert.IsFalse(analysis.Validate().IsValid, "A consequence-free model must fail risk-mode validation.");
        analysis.Options.Mode = RiskAnalysisMode.Reliability;
        Assert.IsTrue(analysis.Validate().IsValid, "Reliability mode must accept a consequence-free model.");

        // Act
        await analysis.RunAsync();

        // The dense trapezoid reference over the same sampled math.
        component.SetupSamplers(1, analysis.Options.PRNGSeed, SamplingScheme.MonteCarlo);
        var sampled = component.Sample(-1);
        var scratch = new ComponentRealization(sampled.FailureModeCount);
        var flags = new RiskComputeFlags();
        int gridCount = 20_000;
        double lower = 1e-16;
        double upper = 1d - 1e-16;
        double step = (upper - lower) / gridCount;
        double reference = 0d;
        double previous = FailureProbability(lower);
        for (int i = 1; i <= gridCount; i++)
        {
            double current = FailureProbability(lower + i * step);
            reference += 0.5d * (previous + current) * step;
            previous = current;
        }
        double FailureProbability(double probability)
        {
            return sampled.ComputeRisk(probability, sampled.Hazard.InverseCDF(probability), flags, scratch).ProbabilityOfFailure;
        }

        // Assert — the AFP at every level, and the degenerate consequence surface.
        Assert.IsTrue(analysis.IsEstimated);
        var summary = analysis.RiskResults![0]!;
        Assert.AreEqual(reference, summary.Fail.TotalProbability, 1e-3 * reference,
            $"System AFP {summary.Fail.TotalProbability} vs dense reference {reference}.");
        Assert.AreEqual(reference, analysis.MeanRiskResults!.Components[0].Curves.Fail.TotalProbability, 1e-3 * reference);
        Assert.AreEqual(reference, analysis.MeanRiskResults.Components[0].FailureModes[0].Curves.Fail.TotalProbability, 1e-3 * reference);
        Assert.AreEqual(0d, summary.Total.Mean, 1e-12, "A consequence-free model carries zero risk mean.");
        Assert.AreEqual(0d, analysis.ComputationWarnings.Count,
            "Reliability mode must not raise the mass-balance drift warning on its degenerate total stream.");
    }

    /// <summary>
    /// Verifies multi-component reliability: the additive system annualized
    /// failure probability is the independent union of the component probabilities.
    /// </summary>
    [TestMethod]
    public async Task Test_Reliability_MultiComponent_UnionAfp()
    {
        // Arrange
        var analysis = new RiskAnalysis(new[] { ReliabilityComponent("Dam"), ReliabilityComponent("Levee") });
        analysis.Options.Mode = RiskAnalysisMode.Reliability;

        // Act
        await analysis.RunAsync();

        // Assert
        var summary = analysis.RiskResults![0]!;
        double first = summary.ComponentResults[0].Fail.TotalProbability;
        double second = summary.ComponentResults[1].Fail.TotalProbability;
        Assert.IsTrue(first > 0d && second > 0d);
        double union = 1d - (1d - first) * (1d - second);
        Assert.AreEqual(union, summary.Fail.TotalProbability, 1e-12,
            "The reliability system AFP must be the independent union of the component AFPs.");
    }

    /// <summary>
    /// Verifies the reliability integrand forcing: in reliability mode the adaptive refinement
    /// objective is <see cref="RiskIntegrand.TotalProbabilityOfFailure"/> regardless of the
    /// configured option, so two runs differing only in <see cref="RiskAnalysisOptions.RiskIntegrand"/>
    /// are bit-identical.
    /// </summary>
    [TestMethod]
    public async Task Test_Reliability_EffectiveIntegrand_BitIdentical()
    {
        // Arrange
        static RiskAnalysis Build(RiskIntegrand integrand)
        {
            var analysis = new RiskAnalysis(new[] { ReliabilityComponent("Dam") });
            analysis.Options.Mode = RiskAnalysisMode.Reliability;
            analysis.Options.RiskIntegrand = integrand;
            return analysis;
        }
        var meanObjective = Build(RiskIntegrand.MeanTotalRisk);
        var failureObjective = Build(RiskIntegrand.TotalProbabilityOfFailure);

        // Act
        await meanObjective.RunAsync();
        await failureObjective.RunAsync();

        // Assert — identical refinement, identical bits.
        Assert.AreEqual(
            BitConverter.DoubleToInt64Bits(meanObjective.RiskResults![0]!.Fail.TotalProbability),
            BitConverter.DoubleToInt64Bits(failureObjective.RiskResults![0]!.Fail.TotalProbability));
    }

    /// <summary>Builds a labeled deterministic damages consequence: linear from (0 → 0) to (30 → valueAtThirty).</summary>
    private static TabularConsequence Damages(string name, double valueAtThirty)
    {
        var damages = Consequence(name, valueAtThirty);
        damages.SpecifiedConsequence = "Damages";
        damages.ConsequenceUnit = "$";
        return damages;
    }

    /// <summary>Builds a component whose failure and non-failure paths both carry the two-type axis [Life Loss, Damages].</summary>
    private static SystemComponent TwoTypeComponent(IResponseFunction? response = null,
        double failureDamagesAtThirty = 5_000_000d, double nonFailureDamagesAtThirty = 1_000_000d)
    {
        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = StageFrequency();
        var failure = new FailureMode(null, null, response ?? Fragility(), Consequence("Failure Loss", 300d));
        failure.ConsequenceFunctions.Add(Damages("Failure Damages", failureDamagesAtThirty));
        component.AddFailureMode(failure);
        var nonFailure = new FailureMode(null, null, null, Consequence("Non-Failure Loss", 60d));
        nonFailure.ConsequenceFunctions.Add(Damages("Non-Failure Damages", nonFailureDamagesAtThirty));
        component.AddFailureMode(nonFailure);
        return component;
    }

    /// <summary>Declares the [Life Loss, Damages] axis on an analysis over the given components.</summary>
    private static RiskAnalysis TwoTypeAnalysis(params SystemComponent[] components)
    {
        var analysis = new RiskAnalysis(components)
        {
            SpecifiedConsequence = "Life Loss",
            ConsequenceUnit = "lives",
        };
        analysis.AdditionalConsequenceTypes.Add(new ConsequenceTypeDescriptor("Damages", "$"));
        return analysis;
    }

    /// <summary>
    /// The multi-consequence mean-pass equivalence pin: adding a second consequence type
    /// leaves the primary type's mean-pass results bit-identical to the single-type run (the
    /// mean pass is seed-free and refinement is primary-driven), and a secondary type that is an
    /// exact scalar multiple of the primary reproduces every stream scaled — with identical
    /// per-type failure probabilities.
    /// </summary>
    [TestMethod]
    public async Task Test_MultiConsequence_MeanPass_PrimaryBitIdentical_SecondaryScales()
    {
        // Arrange — damages are exactly 1000 × lives at every hazard level.
        const double scale = 1000d;
        var singleType = new RiskAnalysis(new[] { Component(Consequence("Failure Loss", 300d)) });
        var twoType = TwoTypeAnalysis(TwoTypeComponent(null, 300d * scale, 60d * scale));

        // Act
        await singleType.RunAsync();
        await twoType.RunAsync();

        // Assert — the primary axis is bit-identical to the single-type run.
        var single = singleType.MeanRiskResults!.Curves;
        var primary = twoType.MeanRiskResults!.Curves;
        Assert.AreEqual(single.Total.Mean, primary.Total.Mean, 0d, "The primary mean-pass mean must be bit-identical.");
        Assert.AreEqual(single.Fail.TotalProbability, primary.Fail.TotalProbability, 0d, "The primary failure probability must be bit-identical.");
        Assert.AreEqual(single.Excess.Mean, primary.Excess.Mean, 0d, "The primary excess mean must be bit-identical.");
        CollectionAssert.AreEqual(single.Total.LECConsequences, primary.Total.LECConsequences, "The primary Total LEC must be bit-identical.");
        CollectionAssert.AreEqual(single.Total.LECProbabilities, primary.Total.LECProbabilities, "The primary Total LEC probabilities must be bit-identical.");

        // The secondary axis exists at every scope and scales exactly.
        Assert.AreEqual(1, twoType.MeanRiskResults.AdditionalCurves.Count);
        var secondary = twoType.MeanRiskResults.AdditionalCurves[0];
        Assert.AreEqual(primary.Total.Mean * scale, secondary.Total.Mean, 1e-9 * primary.Total.Mean * scale,
            "A secondary type that is 1000 × the primary must produce 1000 × the mean.");
        Assert.AreEqual(primary.Excess.Mean * scale, secondary.Excess.Mean, 1e-9 * Math.Max(1d, primary.Excess.Mean * scale));
        Assert.AreEqual(primary.NonFail.Mean * scale, secondary.NonFail.Mean, 1e-9 * Math.Max(1d, primary.NonFail.Mean * scale));

        // Probability streams are identical across types (weights per type sum to one).
        Assert.AreEqual(primary.Fail.TotalProbability, secondary.Fail.TotalProbability, 1e-12 * primary.Fail.TotalProbability,
            "Per-type failure probabilities must agree — the probability structure is shared.");
        Assert.AreEqual(primary.Total.MassBalance, secondary.Total.MassBalance, 1e-12,
            "Per-type exhaustive mass must agree.");

        // The component and failure-mode scopes carry the secondary axis too.
        var component = twoType.MeanRiskResults.Components[0];
        Assert.AreEqual(1, component.AdditionalCurves.Count);
        Assert.IsTrue(component.AdditionalCurves[0].Total.LECConsequences.Length > 2);
        Assert.AreEqual(1, component.FailureModes[0].AdditionalCurves.Count);
        Assert.IsTrue(component.FailureModes[0].AdditionalCurves[0].Fail.LECConsequences.Length > 2);

        // Secondary assurance is NaN (the threshold is declared in the primary type's units).
        Assert.IsTrue(double.IsNaN(secondary.Total.ConsequenceThresholdProbability));
        Assert.IsFalse(double.IsNaN(primary.Total.ConsequenceThresholdProbability));

        // The summary tree carries the secondary axis with the declared labels.
        var summary = twoType.RiskResults![0]!;
        Assert.AreEqual(1, summary.AdditionalConsequences.Count);
        Assert.AreEqual("Damages", summary.AdditionalConsequences[0].SpecifiedConsequence);
        Assert.AreEqual("$", summary.AdditionalConsequences[0].ConsequenceUnit);
        Assert.AreEqual(secondary.Total.Mean, summary.AdditionalConsequences[0].Total.Mean, 0d);
        CollectionAssert.AreEqual(new[] { "Life Loss", "Damages" }, summary.ConsequenceLabels);
        CollectionAssert.AreEqual(new[] { "lives", "$" }, summary.ConsequenceUnits);
        Assert.AreEqual(1, summary.ComponentResults[0].AdditionalConsequences.Count);
        Assert.AreEqual(1, summary.ComponentResults[0].FailureModeResults[0].AdditionalConsequences.Count);
    }

    /// <summary>
    /// Verifies the K = 2 additive system: both types convolve onto system curves, the
    /// type-independent failure union is shared, and a proportional secondary type scales the
    /// convolved system mean.
    /// </summary>
    [TestMethod]
    public async Task Test_MultiConsequence_AdditiveSystem_SecondaryScales()
    {
        // Arrange — two independent two-type components, damages = 1000 × lives on both.
        const double scale = 1000d;
        var analysis = TwoTypeAnalysis(
            TwoTypeComponent(null, 300d * scale, 60d * scale),
            TwoTypeComponent(null, 300d * scale, 60d * scale));

        // Act
        await analysis.RunAsync();

        // Assert
        var system = analysis.MeanRiskResults!;
        Assert.AreEqual(1, system.AdditionalCurves.Count);
        var primary = system.Curves;
        var secondary = system.AdditionalCurves[0];
        Assert.IsTrue(secondary.Total.LECConsequences.Length > 2, "The secondary system Total must be convolved.");
        Assert.AreEqual(primary.Total.Mean * scale, secondary.Total.Mean, 1e-9 * primary.Total.Mean * scale,
            "The convolved secondary system mean must scale with the type.");
        Assert.AreEqual(primary.Fail.TotalProbability, secondary.Fail.TotalProbability, 0d,
            "The failure union is type-independent and shared verbatim.");
    }

    /// <summary>
    /// Verifies the K = 2 joint system: both types record through the VEGAS combination
    /// enumeration and a proportional secondary type scales the system mean under the additive
    /// joint-consequence rule.
    /// </summary>
    [TestMethod]
    public async Task Test_MultiConsequence_JointSystem_SecondaryScales()
    {
        // Arrange
        const double scale = 1000d;
        var analysis = TwoTypeAnalysis(
            TwoTypeComponent(null, 300d * scale, 60d * scale),
            TwoTypeComponent(null, 300d * scale, 60d * scale));
        analysis.Options.SystemRiskMethod = SystemRiskType.JointRiskMethod;
        analysis.Options.UseDefaults = false;
        analysis.Options.WarmupEvaluations = 500;
        analysis.Options.WarmupCycles = 2;
        analysis.Options.FinalEvaluations = 1000;

        // Act
        await analysis.RunAsync();

        // Assert
        var system = analysis.MeanRiskResults!;
        Assert.AreEqual(1, system.AdditionalCurves.Count);
        Assert.IsTrue(system.AdditionalCurves[0].Total.LECConsequences.Length > 2);
        Assert.AreEqual(system.Curves.Total.Mean * scale, system.AdditionalCurves[0].Total.Mean,
            1e-9 * system.Curves.Total.Mean * scale,
            "The joint secondary system mean must scale with the type.");
        Assert.AreEqual(system.Curves.Fail.TotalProbability, system.AdditionalCurves[0].Fail.TotalProbability,
            1e-12 * system.Curves.Fail.TotalProbability,
            "Per-type failure mass must agree on the joint path.");
    }

    /// <summary>
    /// Verifies reliability mode carries no consequence-type axis: consequence-free modes
    /// produce no additional curve sets.
    /// </summary>
    [TestMethod]
    public async Task Test_MultiConsequence_Reliability_NoAdditionalCurves()
    {
        // Arrange — a consequence-free reliability model with a declared (inert) second type.
        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = StageFrequency();
        component.AddFailureMode(new FailureMode(null, null, Fragility(), null));
        var analysis = new RiskAnalysis(new[] { component });
        analysis.Options.Mode = RiskAnalysisMode.Reliability;
        analysis.AdditionalConsequenceTypes.Add(new ConsequenceTypeDescriptor("Damages", "$"));

        // Act
        await analysis.RunAsync();

        // Assert
        Assert.IsTrue(analysis.IsEstimated);
        Assert.AreEqual(0, analysis.MeanRiskResults!.AdditionalCurves.Count);
    }

    /// <summary>
    /// Verifies the declared consequence-type axis round-trips through the configuration
    /// serialization: order and labels survive, and an axis-free legacy form
    /// restores the single-type declaration.
    /// </summary>
    [TestMethod]
    public void Test_AdditionalConsequenceTypes_SerializationRoundTrip()
    {
        // Arrange
        var analysis = new RiskAnalysis(new[] { Component(Consequence("A", 300d)) })
        {
            SpecifiedConsequence = "Life Loss",
            ConsequenceUnit = "lives",
        };
        analysis.AdditionalConsequenceTypes.Add(new ConsequenceTypeDescriptor("Damages", "$"));
        analysis.AdditionalConsequenceTypes.Add(new ConsequenceTypeDescriptor("Environmental", "acres"));

        // Act
        var restored = new RiskAnalysis(new[] { Component(Consequence("A", 300d)) }, analysis.ToXElement());

        // Assert — order and labels survive.
        Assert.AreEqual(2, restored.AdditionalConsequenceTypes.Count);
        Assert.AreEqual("Damages", restored.AdditionalConsequenceTypes[0].SpecifiedConsequence);
        Assert.AreEqual("$", restored.AdditionalConsequenceTypes[0].ConsequenceUnit);
        Assert.AreEqual("Environmental", restored.AdditionalConsequenceTypes[1].SpecifiedConsequence);
        Assert.AreEqual("acres", restored.AdditionalConsequenceTypes[1].ConsequenceUnit);

        // An axis-free legacy form restores the single-type declaration.
        var legacyElement = analysis.ToXElement();
        legacyElement.Element(nameof(RiskAnalysis.AdditionalConsequenceTypes))!.Remove();
        var legacy = new RiskAnalysis(new[] { Component(Consequence("A", 300d)) }, legacyElement);
        Assert.AreEqual(0, legacy.AdditionalConsequenceTypes.Count);
    }

    /// <summary>
    /// Verifies the declared-axis count gate: every failure and
    /// non-failure path must carry exactly one consequence function per declared type — a
    /// two-type declaration over single-consequence paths errors, the two-type component
    /// satisfies it, and the two-type component under the legacy single-type declaration errors
    /// the other way.
    /// </summary>
    [TestMethod]
    public void Test_Validate_ConsequenceTypeAxis_Counts()
    {
        // A K = 2 declaration over single-consequence paths: every path errors.
        var underDeclared = new RiskAnalysis(new[] { Component(Consequence("A", 300d)) });
        underDeclared.AdditionalConsequenceTypes.Add(new ConsequenceTypeDescriptor("Damages", "$"));
        var (isValid, messages) = underDeclared.Validate();
        Assert.IsFalse(isValid);
        Assert.IsTrue(messages.Any(m => m.StartsWith("Error:", StringComparison.Ordinal) && m.Contains("declares 2 consequence type(s)")),
            string.Join("; ", messages));

        // The two-type component satisfies the K = 2 declaration.
        var matched = new RiskAnalysis(new[] { TwoTypeComponent() })
        {
            SpecifiedConsequence = "Life Loss",
            ConsequenceUnit = "lives",
        };
        matched.AdditionalConsequenceTypes.Add(new ConsequenceTypeDescriptor("Damages", "$"));
        var matchedResult = matched.Validate();
        Assert.IsTrue(matchedResult.IsValid, string.Join("; ", matchedResult.ValidationMessages));

        // The two-type component under a K = 1 declaration errors the other way.
        var overCarried = new RiskAnalysis(new[] { TwoTypeComponent() });
        var overResult = overCarried.Validate();
        Assert.IsFalse(overResult.IsValid);
        Assert.IsTrue(overResult.ValidationMessages.Any(m => m.Contains("declares 1 consequence type(s)")),
            string.Join("; ", overResult.ValidationMessages));
    }

    /// <summary>
    /// Verifies the declared-axis label gate: non-blank labels and units must agree per
    /// position (ordinal, case-insensitive), blank on either side is a wildcard, and
    /// reliability mode ignores the axis entirely.
    /// </summary>
    [TestMethod]
    public void Test_Validate_ConsequenceTypeAxis_Labels()
    {
        // A mismatched non-blank label at position 1 errors.
        var mismatched = new RiskAnalysis(new[] { TwoTypeComponent() })
        {
            SpecifiedConsequence = "Life Loss",
            ConsequenceUnit = "lives",
        };
        mismatched.AdditionalConsequenceTypes.Add(new ConsequenceTypeDescriptor("Environmental", "$"));
        var (labelValid, labelMessages) = mismatched.Validate();
        Assert.IsFalse(labelValid);
        Assert.IsTrue(labelMessages.Any(m => m.Contains("position 1") && m.Contains("'Damages'") && m.Contains("'Environmental'")),
            string.Join("; ", labelMessages));

        // A mismatched non-blank unit errors.
        var unitMismatch = new RiskAnalysis(new[] { TwoTypeComponent() })
        {
            SpecifiedConsequence = "Life Loss",
            ConsequenceUnit = "lives",
        };
        unitMismatch.AdditionalConsequenceTypes.Add(new ConsequenceTypeDescriptor("Damages", "EUR"));
        var (unitValid, unitMessages) = unitMismatch.Validate();
        Assert.IsFalse(unitValid);
        Assert.IsTrue(unitMessages.Any(m => m.Contains("unit '$'") && m.Contains("'EUR'")),
            string.Join("; ", unitMessages));

        // Blank declarations are wildcards: an all-blank K = 2 axis accepts labeled functions.
        var wildcard = new RiskAnalysis(new[] { TwoTypeComponent() });
        wildcard.AdditionalConsequenceTypes.Add(new ConsequenceTypeDescriptor(string.Empty, string.Empty));
        var wildcardResult = wildcard.Validate();
        Assert.IsTrue(wildcardResult.IsValid, string.Join("; ", wildcardResult.ValidationMessages));

        // Case difference is not a mismatch.
        var cased = new RiskAnalysis(new[] { TwoTypeComponent() })
        {
            SpecifiedConsequence = "LIFE LOSS",
            ConsequenceUnit = "LIVES",
        };
        cased.AdditionalConsequenceTypes.Add(new ConsequenceTypeDescriptor("damages", "$"));
        Assert.IsTrue(cased.Validate().IsValid, string.Join("; ", cased.Validate().ValidationMessages));

        // Reliability mode ignores the axis (consequence-free models declare nothing usable).
        var reliability = new RiskAnalysis(new[] { Component(Consequence("A", 300d)) });
        reliability.Options.Mode = RiskAnalysisMode.Reliability;
        reliability.AdditionalConsequenceTypes.Add(new ConsequenceTypeDescriptor("Damages", "$"));
        Assert.IsTrue(reliability.Validate().IsValid, string.Join("; ", reliability.Validate().ValidationMessages));
    }

    /// <summary>
    /// Verifies the hazard-axis consistency advisory: components whose driving hazards disagree
    /// on non-blank labels warn (one analysis models one hazard axis) without invalidating.
    /// </summary>
    [TestMethod]
    public void Test_Validate_HazardAxis_MismatchWarns()
    {
        // Arrange — two additive components whose hazards are labeled differently.
        var flowComponent = Component(Consequence("B", 300d));
        flowComponent.Name = "Levee";
        flowComponent.HazardFunction!.SpecifiedHazard = "Flow";
        var analysis = new RiskAnalysis(new[] { Component(Consequence("A", 300d)), flowComponent });

        // Act
        var (isValid, messages) = analysis.Validate();

        // Assert — advisory only.
        Assert.IsTrue(isValid, string.Join("; ", messages));
        Assert.IsTrue(messages.Any(m => m.StartsWith("Warning:", StringComparison.Ordinal) && m.Contains("share the driving hazard axis")),
            string.Join("; ", messages));
    }

    /// <summary>
    /// Verifies the per-type consequence thresholds: a declared secondary threshold
    /// computes the secondary assurance measure at every scope — and on an exactly scaled
    /// secondary axis, the scaled threshold reads the same probability as the primary — while
    /// an undeclared secondary threshold preserves the primary-only interim (NaN).
    /// </summary>
    [TestMethod]
    public async Task Test_PerTypeConsequenceThresholds_ComputedAndInterimPreserved()
    {
        // Arrange — damages are exactly 1000 × lives, and the declared damages threshold is
        // exactly 1000 × the primary threshold, so both assurance reads must agree.
        const double scale = 1000d;
        const double primaryThreshold = 50d;
        var declared = new RiskAnalysis(new[] { TwoTypeComponent(null, 300d * scale, 60d * scale) })
        {
            SpecifiedConsequence = "Life Loss",
            ConsequenceUnit = "lives",
        };
        declared.AdditionalConsequenceTypes.Add(new ConsequenceTypeDescriptor("Damages", "$", primaryThreshold * scale));
        declared.Options.ConsequenceThreshold = primaryThreshold;

        // Act
        await declared.RunAsync();

        // Assert — the secondary assurance measure computes at system, component, and mode scope.
        var summary = declared.RiskResults![0]!;
        double primaryProbability = summary.Total.ConsequenceThresholdProbability;
        double secondaryProbability = summary.AdditionalConsequences[0].Total.ConsequenceThresholdProbability;
        Assert.IsTrue(primaryProbability > 0d && primaryProbability < 1d, "The primary threshold must read an interior probability.");
        Assert.AreEqual(primaryProbability, secondaryProbability, 1e-9 * primaryProbability,
            "The scaled threshold on the exactly scaled axis must read the same probability.");
        Assert.AreEqual(primaryProbability,
            summary.ComponentResults[0].AdditionalConsequences[0].Total.ConsequenceThresholdProbability,
            1e-9 * primaryProbability, "The component scope must carry the per-type threshold too.");

        // The undeclared shape preserves the primary-only interim: secondary assurance NaN.
        var interim = TwoTypeAnalysis(TwoTypeComponent(null, 300d * scale, 60d * scale));
        interim.Options.ConsequenceThreshold = primaryThreshold;
        await interim.RunAsync();
        Assert.IsFalse(double.IsNaN(interim.RiskResults![0]!.Total.ConsequenceThresholdProbability));
        Assert.IsTrue(double.IsNaN(interim.RiskResults[0]!.AdditionalConsequences[0].Total.ConsequenceThresholdProbability),
            "An undeclared per-type threshold must preserve the primary-only interim (NaN assurance).");
    }

    /// <summary>Builds a two-mode component with a non-failure path under the given combination method.</summary>
    private static SystemComponent TwoModeMethodComponent(FailureModeMethod method,
        JointConsequenceType jointConsequences = JointConsequenceType.Maximum)
    {
        var component = new SystemComponent { Name = "Two Modes" };
        component.HazardFunction = StageFrequency();
        var fragilityB = new TabularResponse
        {
            Name = "Mode B",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(10d, new Deterministic(0d)), new UncertainOrdinate(30d, new Deterministic(1d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
        component.AddFailureMode(new FailureMode(null, null, Fragility(), Consequence("A Loss", 300d)));
        component.AddFailureMode(new FailureMode(null, null, fragilityB, Consequence("B Loss", 600d)));
        component.AddFailureMode(new FailureMode(null, null, null, Consequence("Non-Failure Loss", 60d)));
        component.FailureModeMethod = method;
        component.JointConsequences = jointConsequences;
        return component;
    }

    /// <summary>Builds the profile-remap engine fixture: flow hazard → rating (T(h) = h/2) → uncertain stage fragility → stage consequences.</summary>
    private static SystemComponent RemapEngineComponent()
    {
        var hazard = new TabularHazard
        {
            Name = "Flow Frequency",
            SpecifiedHazard = "Flow",
            HazardUnit = "cfs",
            NoUncertaintyFunction = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(0.999d, new Deterministic(0d)),
                    new UncertainOrdinate(0.5d, new Deterministic(50d)),
                    new UncertainOrdinate(0.001d, new Deterministic(100d)),
                },
                true, SortOrder.Descending, true, SortOrder.Ascending, UnivariateDistributionType.Deterministic),
        };
        var rating = new RMC.TotalRisk.RiskFunctions.Transforms.TabularTransform
        {
            Name = "Rating",
            SpecifiedHazard = "Flow",
            HazardUnit = "cfs",
            TransformedHazard = "Stage",
            TransformedHazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(0d, new Deterministic(0d)), new UncertainOrdinate(100d, new Deterministic(50d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
        var fragility = UncertainFragility();
        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = hazard;
        component.AddFailureMode(new FailureMode(new List<ITransformFunction> { rating }, null, fragility, Consequence("Failure Loss", 300d)));
        component.AddFailureMode(new FailureMode(null, null, null, Consequence("Non-Failure Loss", 60d)));
        component.HazardThreshold = 40d;
        return component;
    }

    /// <summary>
    /// Verifies the profile catalog on the mean pass: the cumulative failure
    /// probability (terminal ≡ the Fail mass balance) and system response profile on the
    /// primary Fail stream only, the cumulative expected consequence on every stream (terminal
    /// ≡ the stream mean), the 1D exceedance axis ≡ 1 − p, and the failure-mode profiles built
    /// on the mean tree.
    /// </summary>
    [TestMethod]
    public async Task Test_ProfileCatalog_MeanPass_CatalogAndModeProfiles()
    {
        // Arrange
        var analysis = new RiskAnalysis(new[] { Component(Consequence("Failure Loss", 300d)) });

        // Act
        await analysis.RunAsync();

        // Assert — component scope, primary type.
        var component = analysis.MeanRiskResults!.Components[0];
        var fail = component.Curves.Fail;
        Assert.IsTrue(fail.CumulativeFailureProbabilities.Length > 2, "The cumulative failure probability must build on the Fail stream.");
        Assert.AreEqual(fail.MassBalance, fail.CumulativeFailureProbabilities[0], 1e-12 * fail.MassBalance,
            "The terminal ordinate must equal the annualized failure probability (the recorded mass balance).");
        Assert.AreEqual(fail.Mean, fail.CumulativeExpectedConsequences[0], 1e-12 * fail.Mean,
            "The Fail stream's cumulative expected consequence terminal must equal its mean.");
        var total = component.Curves.Total;
        Assert.IsTrue(total.CumulativeExpectedConsequences.Length > 2, "The cumulative expected consequence must build on every stream.");
        Assert.AreEqual(total.Mean, total.CumulativeExpectedConsequences[0], 1e-12 * total.Mean);
        Assert.AreEqual(0, total.CumulativeFailureProbabilities.Length, "The failure profiles are Fail-stream-only.");
        Assert.AreEqual(0, component.Curves.Excess.SystemResponseProbabilities.Length, "The response profile is Fail-stream-only.");

        // The system response profile: X strictly descending in (0, 1); on the 1D path the
        // exceedance coordinate is exactly 1 − p, so Y rises toward rare hazards and every
        // value stays a probability.
        var srpX = fail.SystemResponseExceedanceProbabilities;
        var srpY = fail.SystemResponseProbabilities;
        Assert.IsTrue(srpX.Length > 2, "The system response profile must build on the Fail stream.");
        Assert.AreEqual(srpX.Length, srpY.Length);
        for (int i = 0; i < srpX.Length; i++)
        {
            Assert.IsTrue(srpX[i] > 0d && srpX[i] < 1d, "Exceedance coordinates must be probabilities.");
            if (i > 0) Assert.IsTrue(srpX[i] < srpX[i - 1], "The exceedance axis must be strictly descending.");
            Assert.IsTrue(srpY[i] >= 0d && srpY[i] <= 1d, "Response ordinates must be probabilities.");
        }
        Assert.IsTrue(srpY[srpX.Length - 1] > srpY[0],
            "The combined response must rise toward rare (small-exceedance) hazards for a monotone fragility.");

        // Failure-mode profiles are built on the mean tree (mean pass only).
        var mode = component.FailureModes[0].Curves.Fail;
        Assert.IsTrue(mode.HazardFrequencyHazards.Length > 2, "Mode-scope profiles must build on the mean pass.");
        Assert.IsTrue(mode.CumulativeFailureProbabilities.Length > 2);
        Assert.IsTrue(mode.SystemResponseProbabilities.Length > 2);
        Assert.AreEqual(mode.MassBalance, mode.CumulativeFailureProbabilities[0], 1e-12 * mode.MassBalance,
            "The mode's cumulative terminal carries its raw marginal mass (documented semantics).");
    }

    /// <summary>
    /// Verifies the realization-weights input property: assignment stores a defensive copy,
    /// invalidates the estimated state, raises change notification, and null clears.
    /// </summary>
    [TestMethod]
    public async Task Test_RealizationWeights_PropertySemantics()
    {
        // Arrange — an estimated analysis so invalidation is observable.
        var analysis = new RiskAnalysis(new[] { Component(Consequence("Failure Loss", 300d)) });
        await analysis.RunAsync();
        Assert.IsTrue(analysis.IsEstimated);
        var raised = new List<string?>();
        analysis.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        // Act — assign, mutate the source, then clear.
        var source = new List<double> { 1d, 2d, 3d };
        analysis.RealizationWeights = source;
        source[0] = 99d;

        // Assert — copy semantics, invalidation, and notification.
        Assert.AreEqual(1d, analysis.RealizationWeights![0], 0d, "The stored weights must be a copy.");
        Assert.IsFalse(analysis.IsEstimated, "Assigning weights must invalidate the estimated state.");
        CollectionAssert.Contains(raised, nameof(RiskAnalysis.RealizationWeights));
        analysis.RealizationWeights = null;
        Assert.IsNull(analysis.RealizationWeights);
    }

    /// <summary>
    /// Verifies the weight validation surface: a weight vector on a mean-only run and an
    /// invalid vector on a full run are Errors reported by <see cref="RiskAnalysis.Validate"/>
    /// and enforced by <see cref="RiskAnalysis.RunAsync"/>.
    /// </summary>
    [TestMethod]
    public async Task Test_RealizationWeights_ValidationMatrix()
    {
        // Mean-only + weights is an Error.
        var meanOnly = new RiskAnalysis(new[] { Component(Consequence("Failure Loss", 300d)) });
        meanOnly.RealizationWeights = new[] { 1d };
        var (meanOnlyValid, meanOnlyMessages) = meanOnly.Validate();
        Assert.IsFalse(meanOnlyValid);
        Assert.IsTrue(meanOnlyMessages.Exists(m => m.StartsWith("Error:") && m.Contains("mean-only")),
            "The mean-only weight refusal must be a validation Error.");
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => meanOnly.RunAsync());

        // A length mismatch on a full run is an Error naming both counts (100 is the options
        // floor on the realization count).
        var full = new RiskAnalysis(new[] { Component(Consequence("Failure Loss", 300d)) });
        full.Options.EstimateMeanRiskOnly = false;
        full.Options.Realizations = 100;
        full.RealizationWeights = new[] { 1d, 2d, 3d };
        var (fullValid, fullMessages) = full.Validate();
        Assert.IsFalse(fullValid);
        Assert.IsTrue(fullMessages.Exists(m => m.StartsWith("Error:") && m.Contains("(3)") && m.Contains("(100)")),
            "The length mismatch must name both counts.");
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => full.RunAsync());

        // A valid vector reports no weight message and the run proceeds.
        full.RealizationWeights = CreateIndexWeights(100);
        var (valid, messages) = full.Validate();
        Assert.IsTrue(valid, string.Join(Environment.NewLine, messages));
        await full.RunAsync();
        Assert.IsTrue(full.IsEstimated);
    }

    /// <summary>Builds the deterministic index-varying weight fixture w(i) = 1 + i mod 5.</summary>
    private static double[] CreateIndexWeights(int count)
    {
        var weights = new double[count];
        for (int i = 0; i < count; i++)
        {
            weights[i] = 1d + (i % 5);
        }
        return weights;
    }

    /// <summary>
    /// Verifies the weighted full-uncertainty run end to end against the unweighted run of the
    /// identical model and seed: weights never move a sampled realization (per-index ensemble
    /// entries bit-identical), the stored ensemble carries the weights and the manifest their
    /// fingerprint, the published summary equals the post-hoc weighted re-reduction of the
    /// unweighted ensemble bit-for-bit, the band trees' curve scalars equal direct upstream
    /// weighted calls over the stored per-realization values, and unit weights publish band
    /// curves bit-identical to the unweighted run.
    /// </summary>
    [TestMethod]
    public async Task Test_WeightedRun_EndToEnd()
    {
        // Arrange — three runs of the identical model and seed: unweighted, weighted, unit
        // (100 is the options floor on the realization count).
        const int realizations = 100;
        var weights = CreateIndexWeights(realizations);
        RiskAnalysis Create()
        {
            var analysis = new RiskAnalysis(new[] { Component(Consequence("Failure Loss", 300d), UncertainFragility()) });
            analysis.Options.EstimateMeanRiskOnly = false;
            analysis.Options.Realizations = realizations;
            return analysis;
        }
        var unweighted = Create();
        var weighted = Create();
        weighted.RealizationWeights = weights;
        var unit = Create();
        unit.RealizationWeights = new double[realizations].Select(_ => 1d).ToArray();

        // Act
        await unweighted.RunAsync();
        await weighted.RunAsync();
        await unit.RunAsync();

        // Assert — weights never touch a seed: every per-realization summary is bit-identical.
        var failureProbabilities = new double[realizations];
        for (int i = 0; i < realizations; i++)
        {
            var baseline = unweighted.RiskResults![i]!;
            var candidate = weighted.RiskResults![i]!;
            Assert.AreEqual(BitConverter.DoubleToInt64Bits(baseline.Fail.TotalProbability),
                BitConverter.DoubleToInt64Bits(candidate.Fail.TotalProbability), $"Realization {i} failure probability moved.");
            Assert.AreEqual(BitConverter.DoubleToInt64Bits(baseline.Total.Mean),
                BitConverter.DoubleToInt64Bits(candidate.Total.Mean), $"Realization {i} total mean moved.");
            failureProbabilities[i] = baseline.Fail.TotalProbability;
        }

        // The stored ensemble carries the weights; the manifest carries their fingerprint.
        CollectionAssert.AreEqual(weights, weighted.RiskResults!.RealizationWeights);
        Assert.IsNull(unweighted.RiskResults!.RealizationWeights);
        Assert.IsNotNull(weighted.RiskResults.Manifest!.RealizationWeightsHash);
        Assert.IsNull(unweighted.RiskResults.Manifest!.RealizationWeightsHash);
        Assert.AreEqual(unweighted.RiskResults.Manifest.AnalysisContentHash,
            weighted.RiskResults.Manifest.AnalysisContentHash,
            "Weights are results-side: the analysis content identity must not move.");

        // The published summary equals the post-hoc weighted re-reduction of the unweighted
        // ensemble — the run-time and post-hoc paths are the same reduction.
        unweighted.RiskResults.SetRealizationWeights(weights);
        var postHoc = unweighted.RiskResults.ComputeSummary(unweighted.Options.ConfidenceIntervalWidth)!;
        var published = weighted.RiskResults.Summary!;
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(postHoc.Mean.Total.Mean), BitConverter.DoubleToInt64Bits(published.Mean.Total.Mean));
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(postHoc.Lower.Fail.TotalProbability), BitConverter.DoubleToInt64Bits(published.Lower.Fail.TotalProbability));
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(postHoc.EffectiveRealizationCount!.Value), BitConverter.DoubleToInt64Bits(published.EffectiveRealizationCount!.Value));
        double totalWeight = 0d, sumOfSquares = 0d;
        for (int i = 0; i < realizations; i++) { totalWeight += weights[i]; sumOfSquares += weights[i] * weights[i]; }
        Assert.AreEqual(totalWeight * totalWeight / sumOfSquares, published.EffectiveRealizationCount!.Value, 1e-12);

        // The band trees' curve scalars equal direct upstream weighted calls over the stored
        // per-realization values (the weighted percentile-assembly proof).
        Assert.AreEqual(Numerics.Data.Statistics.Statistics.Percentile(failureProbabilities, 0.5d, weights),
            weighted.MedianRiskResults!.Curves.Fail.TotalProbability, 0d);
        Assert.AreEqual(Numerics.Data.Statistics.Statistics.Mean(failureProbabilities, weights),
            weighted.MeanRiskResults!.Curves.Fail.TotalProbability, 0d);

        // Unit weights publish band curves equal to the unweighted run to floating-point
        // rounding: at n = 100 the equal-weight plotting positions i/99 are not exactly
        // representable, so the weighted percentile interpolation lands a few units of
        // precision away (each rounded position feeds the interpolation quotient), and the
        // scalar mean slot composes Σw·x/Σw rather than the compensated sequential sum (both
        // documented). The 1e-15 relative tolerance is that rounding envelope.
        var baselineMedian = unweighted.MedianRiskResults!.Curves.Total;
        var unitMedian = unit.MedianRiskResults!.Curves.Total;
        Assert.AreEqual(baselineMedian.LECProbabilities.Length, unitMedian.LECProbabilities.Length);
        for (int i = 0; i < baselineMedian.LECProbabilities.Length; i++)
        {
            double expected = baselineMedian.LECProbabilities[i];
            Assert.AreEqual(expected, unitMedian.LECProbabilities[i], Math.Abs(expected) * 1e-15,
                $"Unit-weight median LEC ordinate {i} moved beyond rounding.");
        }
        double baselineFailureProbability = unweighted.MeanRiskResults!.Curves.Fail.TotalProbability;
        Assert.AreEqual(baselineFailureProbability, unit.MeanRiskResults!.Curves.Fail.TotalProbability,
            Math.Abs(baselineFailureProbability) * 1e-15);
    }

    /// <summary>
    /// Verifies the estimated-state restoration rule on the results-injection constructor: a
    /// populated ensemble restores the serialized estimated flag, while an empty or
    /// integrity-cleared container forces an unestimated analysis that must be rerun.
    /// </summary>
    [TestMethod]
    public async Task Test_ResultsConstructor_EmptyEnsembleIsUnestimated()
    {
        // Arrange — an estimated analysis provides the serialized configuration.
        var analysis = new RiskAnalysis(new[] { Component(Consequence("Failure Loss", 300d)) });
        await analysis.RunAsync();
        var element = analysis.ToXElement();

        // Act / Assert — a populated ensemble restores as estimated.
        var restored = new RiskAnalysis(new[] { Component(Consequence("Failure Loss", 300d)) }, element,
            analysis.RiskResults, analysis.MeanRiskResults);
        Assert.IsTrue(restored.IsEstimated);

        // An empty container (the shape a failed load integrity check produces) restores
        // unestimated — the analysis must be rerun.
        var cleared = new RiskAnalysis(new[] { Component(Consequence("Failure Loss", 300d)) }, element,
            new EnsembleResults(0), analysis.MeanRiskResults);
        Assert.IsFalse(cleared.IsEstimated);

        // No results at all stays unestimated (the existing rule).
        var bare = new RiskAnalysis(new[] { Component(Consequence("Failure Loss", 300d)) }, element);
        Assert.IsFalse(bare.IsEstimated);
    }

    /// <summary>
    /// Verifies the tolerable-risk confidence end to end: criteria never move a sampled
    /// realization, every published entry equals the exact count (or weight fraction) over the
    /// stored per-realization measures, the block serializes append-only, the post-hoc
    /// recomputation matches the published block and follows post-hoc weights, and the
    /// validation surface gates the mean-only and out-of-range configurations.
    /// </summary>
    [TestMethod]
    public async Task Test_TolerableRiskConfidence_EndToEnd()
    {
        // Arrange — a criteria-free reference run fixes the per-realization measures.
        const int realizations = 100;
        RiskAnalysis Create()
        {
            var analysis = new RiskAnalysis(new[] { Component(Consequence("Failure Loss", 300d), UncertainFragility()) });
            analysis.Options.EstimateMeanRiskOnly = false;
            analysis.Options.Realizations = realizations;
            return analysis;
        }
        var reference = Create();
        await reference.RunAsync();
        var excessMeans = new double[realizations];
        for (int i = 0; i < realizations; i++)
        {
            excessMeans[i] = reference.RiskResults![i]!.Excess.Mean;
        }
        // Any interior value splits the ensemble; the oracle recounts against it either way.
        double midThreshold = excessMeans[realizations / 2];

        // Act — the criteria'd run: an always-exceeded, a mid, and a never-exceeded threshold.
        var analysisWithCriteria = Create();
        analysisWithCriteria.Options.TolerableRiskCriteria.Add(new TolerableRiskCriterion(RiskMeasure.Mean, RiskType.Excess, 0, double.MinValue));
        analysisWithCriteria.Options.TolerableRiskCriteria.Add(new TolerableRiskCriterion(RiskMeasure.Mean, RiskType.Excess, 0, midThreshold));
        analysisWithCriteria.Options.TolerableRiskCriteria.Add(new TolerableRiskCriterion(RiskMeasure.Mean, RiskType.Excess, 0, double.MaxValue));
        await analysisWithCriteria.RunAsync();

        // Assert — criteria are seed-inert: every per-realization measure is bit-identical.
        int exceedingCount = 0;
        for (int i = 0; i < realizations; i++)
        {
            double value = analysisWithCriteria.RiskResults![i]!.Excess.Mean;
            Assert.AreEqual(BitConverter.DoubleToInt64Bits(excessMeans[i]), BitConverter.DoubleToInt64Bits(value),
                $"Realization {i} moved under configured criteria.");
            if (value > midThreshold) exceedingCount++;
        }

        // The published block: exact counts over the stored measures.
        var block = analysisWithCriteria.RiskResults!.Summary!.TolerableRiskConfidence!;
        Assert.AreEqual(3, block.Count);
        Assert.AreEqual(1d, block[0].ExceedanceProbability, 0d);
        Assert.AreEqual(exceedingCount / (double)realizations, block[1].ExceedanceProbability, 0d);
        Assert.AreEqual(0d, block[2].ExceedanceProbability, 0d);
        Assert.AreEqual("Mean", block[1].Measure);
        Assert.AreEqual("Excess", block[1].RiskType);
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(midThreshold), BitConverter.DoubleToInt64Bits(block[1].Threshold));

        // Serialization: the block round-trips; the criteria-free run's payload omits it.
        var restored = EnsembleResults.FromJson(analysisWithCriteria.RiskResults.ToJson());
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(block[1].ExceedanceProbability),
            BitConverter.DoubleToInt64Bits(restored.Summary!.TolerableRiskConfidence![1].ExceedanceProbability));
        StringAssert.DoesNotMatch(reference.RiskResults!.ToJson(), new System.Text.RegularExpressions.Regex("TolerableRiskConfidence"));

        // Post-hoc recomputation matches the published block, and follows post-hoc weights.
        var recomputed = analysisWithCriteria.ComputeTolerableRiskConfidence()!;
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(block[1].ExceedanceProbability),
            BitConverter.DoubleToInt64Bits(recomputed[1].ExceedanceProbability));
        var weights = CreateIndexWeights(realizations);
        analysisWithCriteria.RiskResults.SetRealizationWeights(weights);
        var weighted = analysisWithCriteria.ComputeTolerableRiskConfidence()!;
        double exceedingWeight = 0d, totalWeight = 0d;
        for (int i = 0; i < realizations; i++)
        {
            totalWeight += weights[i];
            if (analysisWithCriteria.RiskResults[i]!.Excess.Mean > midThreshold) exceedingWeight += weights[i];
        }
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(exceedingWeight / totalWeight),
            BitConverter.DoubleToInt64Bits(weighted[1].ExceedanceProbability));

        // A criteria-free analysis recomputes to null; so does an unestimated one.
        Assert.IsNull(reference.ComputeTolerableRiskConfidence());

        // Validation: mean-only is a Warning (legal, no output); an out-of-range type position
        // is an Error the run gate enforces.
        var meanOnly = new RiskAnalysis(new[] { Component(Consequence("Failure Loss", 300d)) });
        meanOnly.Options.TolerableRiskCriteria.Add(new TolerableRiskCriterion());
        var (meanOnlyValid, meanOnlyMessages) = meanOnly.Validate();
        Assert.IsTrue(meanOnlyValid);
        Assert.IsTrue(meanOnlyMessages.Exists(m => m.StartsWith("Warning:") && m.Contains("mean-only")));
        await meanOnly.RunAsync();
        Assert.IsNull(meanOnly.RiskResults!.Summary, "A mean-only run publishes no summary and therefore no confidence block.");

        var outOfRange = Create();
        outOfRange.Options.TolerableRiskCriteria.Add(new TolerableRiskCriterion(RiskMeasure.Mean, RiskType.Excess, 3, 1e-3));
        var (rangeValid, rangeMessages) = outOfRange.Validate();
        Assert.IsFalse(rangeValid);
        Assert.IsTrue(rangeMessages.Exists(m => m.StartsWith("Error:") && m.Contains("position 3")));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => outOfRange.RunAsync());
    }
}
