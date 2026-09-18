using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.Systems.Components;

namespace RMC.TotalRisk.Tests.Analyses.Risk;

/// <summary>
/// Unit tests for the scrambled-Sobol sampling surfaces: the knowledge-uncertainty scheme
/// through the component walk and the coupling matrix, the composite mixture selector, the
/// stream-seed inertness of a scheme switch, the power-of-two advisory, and the opt-in joint
/// Sobol driver's deliberate movement and reproducibility.
/// </summary>
[TestClass]
public class ScrambledSobolSamplingTests
{
    /// <summary>Builds one uncertain single-mode component.</summary>
    private static SystemComponent Component(string name)
    {
        var hazard = new TabularHazard
        {
            Name = name + " frequency",
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
        var fragility = new TabularResponse
        {
            Name = name + " fragility",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(10d, new Triangular(0d, 0.05d, 0.1d)),
                    new UncertainOrdinate(20d, new Triangular(0.7d, 0.9d, 1d)),
                },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Triangular),
        };
        var consequence = new TabularConsequence
        {
            Name = name + " loss",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(0d, new Normal(0d, 0d)), new UncertainOrdinate(30d, new Normal(1000d, 100d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Normal),
        };
        var component = new SystemComponent { Name = name };
        component.HazardFunction = hazard;
        component.AddFailureMode(new FailureMode(null, null, fragility, consequence));
        return component;
    }

    /// <summary>Builds a two-component joint analysis with small VEGAS budgets.</summary>
    private static RiskAnalysis JointAnalysis(bool useSobolDriver)
    {
        var analysis = new RiskAnalysis(new[] { Component("Dam"), Component("Levee") });
        analysis.Options.SystemRiskMethod = SystemRiskType.JointRiskMethod;
        analysis.Options.EstimateMeanRiskOnly = false;
        analysis.Options.Realizations = 128;
        analysis.Options.UseDefaults = false;
        analysis.Options.WarmupEvaluations = 2000;
        analysis.Options.WarmupCycles = 3;
        analysis.Options.FinalEvaluations = 2000;
        analysis.Options.UseSobolJointSampling = useSobolDriver;
        return analysis;
    }

    /// <summary>
    /// Verifies the scheme through the component walk and the coupling matrix: the scrambled
    /// draws are deterministic per seed, lie in the unit interval, differ from the Latin
    /// hypercube draws at the same seeds, and the coupling matrix samples under the scheme.
    /// </summary>
    [TestMethod]
    public void Test_Walk_SchemeGeneratesDeterministicDraws()
    {
        // Arrange
        var component = Component("Dam");
        var fragility = (TabularResponse)component.FailureModes[0].ResponseFunction;
        const int N = 32;

        // Act — two scrambled setups and one Latin hypercube setup at identical seeds. (The
        // walk also generates each projected mode's coupling matrix under the scheme — the
        // full-engine scheme tests below execute that branch end to end.)
        component.SetupSamplers(N, 12345, SamplingScheme.ScrambledSobol);
        var first = new double[N];
        for (int i = 0; i < N; i++) first[i] = fragility.SampledPercentile(i, 0);

        component.SetupSamplers(N, 12345, SamplingScheme.ScrambledSobol);
        for (int i = 0; i < N; i++)
        {
            Assert.AreEqual(first[i], fragility.SampledPercentile(i, 0), 0d, "Identical seeds must reproduce bit-for-bit.");
            Assert.IsTrue(first[i] >= 0d && first[i] < 1d, "Draws must lie in [0, 1).");
        }

        component.SetupSamplers(N, 12345, SamplingScheme.LatinHypercube);
        bool anyDiffers = false;
        for (int i = 0; i < N; i++)
        {
            if (first[i] != fragility.SampledPercentile(i, 0)) anyDiffers = true;
        }
        Assert.IsTrue(anyDiffers, "The scrambled scheme must generate different draws than the Latin hypercube at the same seeds.");
    }

    /// <summary>
    /// Verifies the composite mixture selector samples under the scheme (a missed scheme branch
    /// would refuse the setup loudly) and selects branches deterministically.
    /// </summary>
    [TestMethod]
    public void Test_CompositeMixtureSelector_SamplesUnderScheme()
    {
        // Arrange — a two-branch mixture composite.
        static TabularConsequence Child(string name, double top)
        {
            return new TabularConsequence
            {
                Name = name,
                SpecifiedHazard = "Stage",
                HazardUnit = "ft",
                SpecifiedConsequence = "Damages",
                ConsequenceUnit = "$",
                UncertainOrderedPairedData = new UncertainOrderedPairedData(
                    new[] { new UncertainOrdinate(0d, new Deterministic(0d)), new UncertainOrdinate(10d, new Deterministic(top)) },
                    true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
            };
        }
        var composite = new CompositeConsequence(new[]
        {
            new WeightedConsequenceFunction(Child("Low", 10d), 0.5d),
            new WeightedConsequenceFunction(Child("High", 100d), 0.5d),
        })
        {
            Name = "Mixture",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
            CompositeFunctionType = CompositeFunctionType.Mixture,
        };

        // Act / Assert — the setup accepts the scheme and realization sampling is deterministic.
        composite.SetupSampler(32, 999, SamplingScheme.ScrambledSobol);
        double first = composite.SampleFunction(3).Function(10d);
        composite.SetupSampler(32, 999, SamplingScheme.ScrambledSobol);
        Assert.AreEqual(first, composite.SampleFunction(3).Function(10d), 0d,
            "The mixture selector must draw deterministically under the scheme.");
    }

    /// <summary>
    /// Verifies a scheme switch never re-rolls a stream seed: two runs differing only in the
    /// sampling scheme capture identical sampler seed maps while publishing different results.
    /// </summary>
    [TestMethod]
    public void Test_SchemeSwitch_SeedsInert_ResultsMove()
    {
        // Arrange / Act
        var latin = JointAnalysis(useSobolDriver: false);
        latin.Options.SystemRiskMethod = SystemRiskType.AdditiveRiskMethod;
        latin.RunAsync().GetAwaiter().GetResult();

        var sobol = JointAnalysis(useSobolDriver: false);
        sobol.Options.SystemRiskMethod = SystemRiskType.AdditiveRiskMethod;
        sobol.Options.SamplingScheme = SamplingScheme.ScrambledSobol;
        sobol.RunAsync().GetAwaiter().GetResult();

        // Assert — seeds identical, results different (the generator changed, the streams did not).
        var latinSeeds = latin.CapturedSamplerSeeds!;
        var sobolSeeds = sobol.CapturedSamplerSeeds!;
        Assert.AreEqual(latinSeeds.ComponentCount, sobolSeeds.ComponentCount);
        for (int c = 0; c < latinSeeds.ComponentSeeds.Count; c++)
        {
            CollectionAssert.AreEqual(latinSeeds.ComponentSeeds[c], sobolSeeds.ComponentSeeds[c],
                "A scheme switch must never move a captured sampler seed.");
        }
        Assert.AreNotEqual(latin.RiskResults!.Summary!.Mean.Total.Mean,
            sobol.RiskResults!.Summary!.Mean.Total.Mean);
    }

    /// <summary>
    /// Verifies the power-of-two advisory: a non-power-of-two realization count warns under the
    /// scheme, a power of two does not, and the other schemes never warn.
    /// </summary>
    [TestMethod]
    public void Test_Validate_PowerOfTwoAdvisory()
    {
        // Arrange
        var analysis = JointAnalysis(useSobolDriver: false);
        analysis.Options.SamplingScheme = SamplingScheme.ScrambledSobol;

        // 128 is a power of two: no advisory.
        Assert.IsFalse(analysis.Validate().ValidationMessages.Any(m => m.Contains("power-of-two")));

        // 100 is not: the advisory appears, still valid.
        analysis.Options.Realizations = 100;
        var (isValid, messages) = analysis.Validate();
        Assert.IsTrue(isValid);
        Assert.IsTrue(messages.Any(m => m.StartsWith("Warning:", StringComparison.Ordinal) && m.Contains("power-of-two")));

        // The default scheme never advises.
        analysis.Options.SamplingScheme = SamplingScheme.LatinHypercube;
        Assert.IsFalse(analysis.Validate().ValidationMessages.Any(m => m.Contains("power-of-two")));
    }

    /// <summary>
    /// Verifies the opt-in joint Sobol driver: enabling it moves the analysis content hash and
    /// the joint results deliberately, and two enabled runs reproduce bit-for-bit (the seeded
    /// scrambling restores the content-seed contract the unrandomized sequence cannot honor).
    /// </summary>
    [TestMethod]
    public void Test_JointSobolDriver_DeliberateMovement_AndReproducible()
    {
        // Arrange / Act
        var prng = JointAnalysis(useSobolDriver: false);
        prng.RunAsync().GetAwaiter().GetResult();
        var sobol = JointAnalysis(useSobolDriver: true);
        sobol.RunAsync().GetAwaiter().GetResult();
        var sobolRepeat = JointAnalysis(useSobolDriver: true);
        sobolRepeat.RunAsync().GetAwaiter().GetResult();

        // Assert — a deliberate, hashed, value-moving selection.
        Assert.AreNotEqual(prng.RiskResults!.Manifest!.AnalysisContentHash,
            sobol.RiskResults!.Manifest!.AnalysisContentHash,
            "Enabling the joint Sobol driver must move the analysis content hash.");
        Assert.AreNotEqual(prng.RiskResults.Summary!.Mean.Total.Mean,
            sobol.RiskResults.Summary!.Mean.Total.Mean,
            "The joint Sobol driver must move the joint results deliberately.");

        // Reproducible under the content-seed contract.
        Assert.AreEqual(sobol.RiskResults.ToJson(), sobolRepeat.RiskResults!.ToJson(),
            "Two enabled runs must publish byte-identical results.");
    }
}
