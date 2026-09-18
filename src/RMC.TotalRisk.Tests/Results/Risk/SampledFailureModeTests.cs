using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results.Risk;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.Systems.Components;

namespace RMC.TotalRisk.Tests.Results.Risk;

/// <summary>
/// Unit tests for <see cref="SampledFailureMode"/> — known-point response and consequence math,
/// the shared-percentile consequence pairing, the branch-enumerated recording shape, the clamping
/// flags, and the multi-stage polarity-product math
/// (docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §7.9).
/// </summary>
[TestClass]
public class SampledFailureModeTests
{
    /// <summary>Builds a deterministic fragility: P[F|h] linear from (10 → 0) to (20 → 1).</summary>
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

    /// <summary>Builds a normally uncertain consequence: (0 → N(0,0)) to (30 → N(mean, sd)).</summary>
    private static TabularConsequence UncertainConsequence(string name, double mean, double sd)
    {
        var consequence = Consequence(name, 0d);
        consequence.UncertainOrderedPairedData = new UncertainOrderedPairedData(
            new[] { new UncertainOrdinate(0d, new Normal(0d, 0d)), new UncertainOrdinate(30d, new Normal(mean, sd)) },
            true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Normal);
        return consequence;
    }

    /// <summary>Builds a non-failure mode carrying the given consequence.</summary>
    private static FailureMode NonFailureMode(TabularConsequence consequence)
    {
        return new FailureMode(null, null, null, consequence);
    }

    /// <summary>Verifies the mean-sample response and consequence math at known points.</summary>
    [TestMethod]
    public void Test_MeanSample_KnownPointMath()
    {
        // Arrange — no transforms: SRP(15) = 0.5 on the linear fragility; consequence(15) = 150.
        var mode = new FailureMode(null, null, Fragility(), Consequence("Damages", 300d));
        var sampled = mode.Sample(nonFailureMode: null);
        var realization = new FailureModeRealization();
        var flags = new RiskComputeFlags();

        // Act
        var output = sampled.ComputeRisk(probability: 0.4d, hazardLevel: 15d, null, flags, realization, recordOutput: true);

        // Assert
        Assert.AreEqual(0.5d, sampled.SRP(15d), 1e-12);
        Assert.AreEqual(15d, sampled.InverseSRP(0.5d), 1e-12);
        Assert.AreEqual(0.5d, output.ProbabilityOfFailure, 1e-12);
        Assert.AreEqual(0.5d, output.ProbabilityOfNonFailure, 1e-12);
        Assert.AreEqual(150d, output.MeanFailureConsequences, 1e-12);
        Assert.AreEqual(150d, output.MeanExcessConsequences, 1e-12, "With no non-failure mode the excess equals the failure consequence.");
        Assert.AreEqual(0d, output.NonFailureConsequences, 0d);
        Assert.AreEqual(1, output.ResponseProbabilities.Count);
        Assert.AreEqual(0.5d, output.ResponseProbabilities[0], 1e-12);
        Assert.AreEqual(1, realization.Curves.Fail.RiskPoints.Count);
        Assert.AreEqual(15d, realization.Curves.Fail.RiskPoints[0].HazardLevel, 0d);
        Assert.AreEqual(0.4d, realization.Curves.Fail.RiskPoints[0].HazardProbability, 0d);
        Assert.IsFalse(flags.Any);
    }

    /// <summary>
    /// Verifies the shared failure/non-failure coupling draw: identical-content consequences,
    /// paired at the same coupling percentile, sample the identical curve — the excess is exactly
    /// zero in every realization. Independent draws would differ almost surely. The pin runs a
    /// single-failure-mode component under the competing method, whose component excess IS the
    /// mode's paired excess (the joint method's component-level excess deliberately uses the
    /// non-failure mode's own draw — v1.0 semantics — so it would not cancel).
    /// </summary>
    [TestMethod]
    public void Test_QN_SharedPercentile_PairedConsequencesCoherent()
    {
        // Arrange — a component provides the sampler walk (seeds + coupling matrices); the
        // engine path (SetupSamplers → Sample) carries the frozen projection snapshot.
        var component = new SystemComponent { Name = "Coupling Pin" };
        component.HazardFunction = SampledComponentTests.StageFrequency();
        component.AddFailureMode(new FailureMode(null, null, Fragility(), UncertainConsequence("Failure Loss", 100d, 25d)));
        component.AddFailureMode(NonFailureMode(UncertainConsequence("Non-Failure Loss", 100d, 25d)));
        component.FailureModeMethod = FailureModeMethod.CompetingFailures;
        component.SetupSamplers(64, componentSeed: 12345, SamplingScheme.LatinHypercube);

        // Act / Assert — across realizations the paired excess is exactly zero.
        for (int k = 0; k < 64; k++)
        {
            var sampled = component.Sample(k);
            var flags = new RiskComputeFlags();
            var realization = new ComponentRealization(failureModes: 1);
            var output = sampled.ComputeRisk(0.5d, 15d, flags, realization);
            Assert.AreEqual(0d, output.MeanExcessConsequences, 0d,
                $"Realization {k}: identical-content paired consequences at one shared percentile must cancel exactly.");
            Assert.IsFalse(flags.HasNegativeExcessConsequence);
        }
    }

    /// <summary>
    /// Verifies the exposure-branch recording shape: a day/night mixture consequence records one Fail entry
    /// per exposure branch with weight-scaled probabilities and branch-specific consequences.
    /// </summary>
    [TestMethod]
    public void Test_MixtureConsequence_BranchEnumeratedRecording()
    {
        // Arrange — day 55% at 100, night 45% at 300 (values at hazard 30; halved at 15).
        var mixture = new CompositeConsequence(new[]
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
        };
        var mode = new FailureMode(null, null, Fragility(), mixture);
        var sampled = mode.Sample(null);
        var realization = new FailureModeRealization();
        var flags = new RiskComputeFlags();

        // Act — SRP(15) = 0.5; branch values at 15 are 50 and 150.
        var output = sampled.ComputeRisk(0.4d, 15d, null, flags, realization, recordOutput: true);

        // Assert — entries per branch on the output and the recorded point.
        Assert.AreEqual(2, sampled.FailureBranchCount);
        Assert.AreEqual(2, output.ResponseProbabilities.Count);
        Assert.AreEqual(0.5d * 0.55d, output.ResponseProbabilities[0], 1e-12);
        Assert.AreEqual(50d, output.FailureConsequences[0], 1e-12);
        Assert.AreEqual(0.5d * 0.45d, output.ResponseProbabilities[1], 1e-12);
        Assert.AreEqual(150d, output.FailureConsequences[1], 1e-12);
        Assert.AreEqual(0.55d * 50d + 0.45d * 150d, output.MeanFailureConsequences, 1e-12);

        var point = realization.Curves.Fail.RiskPoints[0];
        Assert.AreEqual(2, point.ResponseProbabilities.Count);
        Assert.AreEqual(0.5d * 0.55d, point.ResponseProbabilities[0], 1e-12);
        Assert.AreEqual(150d, point.Consequences[1], 1e-12);
    }

    /// <summary>
    /// Verifies the clamping flags: a non-failure consequence exceeding the failure consequence
    /// clamps the excess to zero and raises the excess flag (only while failure is possible).
    /// </summary>
    [TestMethod]
    public void Test_ComputeRisk_NegativeExcess_ClampsAndFlags()
    {
        // Arrange — fail 30 at full scale, non-fail 300: excess is negative everywhere.
        var mode = new FailureMode(null, null, Fragility(), Consequence("Small Fail", 30d));
        var nonFailure = NonFailureMode(Consequence("Large NonFail", 300d));
        var sampledNonFail = nonFailure.Sample(null);
        var sampled = mode.Sample(nonFailure);
        var flags = new RiskComputeFlags();
        var realization = new FailureModeRealization();

        // Act
        var output = sampled.ComputeRisk(0.4d, 15d, sampledNonFail, flags, realization);

        // Assert
        Assert.AreEqual(0d, output.MeanExcessConsequences, 0d);
        Assert.IsTrue(flags.HasNegativeExcessConsequence);
        Assert.AreEqual(15d, output.MeanFailureConsequences, 1e-12);
        Assert.AreEqual(150d, output.NonFailureConsequences, 1e-12);
    }

    /// <summary>Builds a wider deterministic fragility: P[F|h] linear from (10 → 0) to (30 → 1).</summary>
    private static TabularResponse WideFragility()
    {
        var response = Fragility();
        response.UncertainOrderedPairedData = new UncertainOrderedPairedData(
            new[] { new UncertainOrdinate(10d, new Deterministic(0d)), new UncertainOrdinate(30d, new Deterministic(1d)) },
            true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic);
        return response;
    }

    /// <summary>Builds a deterministic transform scaling the signal by the given factor at 100.</summary>
    private static RMC.TotalRisk.RiskFunctions.Transforms.TabularTransform ScaleTransform(double valueAtHundred)
    {
        return new RMC.TotalRisk.RiskFunctions.Transforms.TabularTransform
        {
            Name = "Scale",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            TransformedHazard = "Stage",
            TransformedHazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(0d, new Deterministic(0d)), new UncertainOrdinate(100d, new Deterministic(valueAtHundred)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>
    /// Verifies the multi-stage polarity product: each stage's fragility evaluates
    /// at its own stage-transformed signal, Fail contributes p and Non-Fail contributes 1 − p,
    /// and the single-stage view is unchanged.
    /// </summary>
    [TestMethod]
    public void Test_MultiStage_PolarityProductSRP()
    {
        // Arrange — stage 0: no transforms, p1(15) = 0.5 on the (10→0, 20→1) fragility;
        // stage 1: a 1.5× transform remaps 15 → 22.5, p2(22.5) = 0.625 on (10→0, 30→1).
        FailureMode BuildMode(BranchPolarity finalPolarity) => new FailureMode(
            new List<ResponseStage>
            {
                new ResponseStage(new List<RMC.TotalRisk.Core.Interfaces.ITransformFunction>(), Fragility()),
                new ResponseStage(new List<RMC.TotalRisk.Core.Interfaces.ITransformFunction> { ScaleTransform(150d) },
                    WideFragility(), finalPolarity),
            },
            null,
            new List<RMC.TotalRisk.Core.Interfaces.IConsequenceFunction> { Consequence("Damages", 300d) });

        // Act / Assert — Fail-final: p1 · p2; Non-Fail-final: p1 · (1 − p2).
        var progression = BuildMode(BranchPolarity.Fail).Sample(null);
        Assert.AreEqual(0.5d * 0.625d, progression.SRP(15d), 1e-12);

        var partial = BuildMode(BranchPolarity.NonFail).Sample(null);
        Assert.AreEqual(0.5d * 0.375d, partial.SRP(15d), 1e-12);

        // Authoring validation stays untouched by the compute acceptance.
        Assert.IsTrue(BuildMode(BranchPolarity.Fail).Validate().IsValid);
    }

    /// <summary>
    /// Verifies the consequence-input fold spans every stage's transforms (a fold truncated at
    /// stage 0's transform count would miss the later stages): position 2 folds
    /// stage 0's and stage 1's transforms in chain order.
    /// </summary>
    [TestMethod]
    public void Test_MultiStage_ConsequenceInput_FoldsAllStages()
    {
        // Arrange — stage 0 carries a 1.5× transform (15 → 22.5), stage 1 a 3× transform
        // (22.5 → 67.5); the default binding resolves to position 2 (the last response's input).
        var mode = new FailureMode(
            new List<ResponseStage>
            {
                new ResponseStage(new List<RMC.TotalRisk.Core.Interfaces.ITransformFunction> { ScaleTransform(150d) }, Fragility()),
                new ResponseStage(new List<RMC.TotalRisk.Core.Interfaces.ITransformFunction> { ScaleTransform(300d) }, WideFragility()),
            },
            null,
            new List<RMC.TotalRisk.Core.Interfaces.IConsequenceFunction> { Consequence("Damages", 300d) });
        Assert.AreEqual(2, mode.ResolvedConsequenceHazardPosition);

        // Act / Assert — both stage transforms fold; binding position 1 folds only the first.
        Assert.AreEqual(67.5d, mode.Sample(null).ConsequenceInput(15d), 1e-12);
        mode.ConsequenceHazardPosition = 1;
        Assert.AreEqual(22.5d, mode.Sample(null).ConsequenceInput(15d), 1e-12);
    }

    /// <summary>
    /// Pins the cascade knowledge-sampling contract: every stage's
    /// response uncertainty samples independently like any other function — two equal-content
    /// uncertain fragilities in one chain draw different curves at a realization — while ONE
    /// shared instance wired into sibling end states stays one knowledge quantity (its Fail and
    /// Non-Fail branches must ride the same sampled curve). The only cross-function coupling
    /// remains the failure/non-failure consequence pairing.
    /// </summary>
    [TestMethod]
    public void Test_MultiStage_ResponseKnowledge_IndependentPerStage()
    {
        // Arrange — a two-stage chain whose stages wrap EQUAL-CONTENT but DISTINCT uncertain
        // fragilities (triangular ordinates), inside a component so the seeded walk runs.
        static TabularResponse UncertainFragility() => new TabularResponse
        {
            Name = "Uncertain Fragility",
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

        var component = new SystemComponent { Name = "Cascade Sampling Pin" };
        component.HazardFunction = SampledComponentTests.StageFrequency();
        var chained = new FailureMode(
            new List<ResponseStage>
            {
                new ResponseStage(new List<RMC.TotalRisk.Core.Interfaces.ITransformFunction>(), UncertainFragility()),
                new ResponseStage(new List<RMC.TotalRisk.Core.Interfaces.ITransformFunction>(), UncertainFragility()),
            },
            null,
            new List<RMC.TotalRisk.Core.Interfaces.IConsequenceFunction> { Consequence("Damages", 300d) });
        component.AddFailureMode(chained);
        component.SetupSamplers(64, componentSeed: 12345, SamplingScheme.LatinHypercube);

        // Act / Assert — equal-content stages draw independent knowledge: at some realization
        // the two stages' sampled fragilities must differ (identical draws would make the SRP
        // the square of one curve at every probe).
        var projected = component.FailureModes[0];
        bool stagesDiffer = false;
        for (int k = 0; k < 64 && !stagesDiffer; k++)
        {
            var stage0 = projected.ResponseStages[0].Response.SampleFunction(k);
            var stage1 = projected.ResponseStages[1].Response.SampleFunction(k);
            stagesDiffer = Math.Abs(stage0.CDF(15d) - stage1.CDF(15d)) > 1e-12;
        }
        Assert.IsTrue(stagesDiffer,
            "Equal-content cascade stages must sample their knowledge uncertainty independently.");

        // A shared instance across sibling end states is ONE knowledge quantity: both branches
        // of one chance node read the identical sampled curve.
        var sharedComponent = new SystemComponent { Name = "Shared Instance Pin" };
        var hazardElement = new RMC.TotalRisk.Systems.Components.Graph.HazardElement("Hazard")
        {
            Function = SampledComponentTests.StageFrequency(),
        };
        var response = new RMC.TotalRisk.Systems.Components.Graph.ResponseElement("Initiation")
        {
            Function = UncertainFragility(),
            Input = new RMC.TotalRisk.Systems.Components.Graph.RiskConnection(hazardElement),
        };
        var fail = new RMC.TotalRisk.Systems.Components.Graph.ConsequenceElement("Fail Damages")
        {
            Input = new RMC.TotalRisk.Systems.Components.Graph.RiskConnection(response),
        };
        fail.Functions.Add(Consequence("Fail Loss", 300d));
        var partial = new RMC.TotalRisk.Systems.Components.Graph.ConsequenceElement("Partial Damages")
        {
            Input = new RMC.TotalRisk.Systems.Components.Graph.RiskConnection(response, 1),
        };
        partial.Functions.Add(Consequence("Partial Loss", 100d));
        sharedComponent.Graph.AddElement(hazardElement);
        sharedComponent.Graph.AddElement(response);
        sharedComponent.Graph.AddElement(fail);
        sharedComponent.Graph.AddElement(partial);
        sharedComponent.SetupSamplers(64, componentSeed: 12345, SamplingScheme.LatinHypercube);
        var sampledShared = sharedComponent.Sample(7);
        var sharedRealization = new ComponentRealization(sampledShared.FailureModeCount);
        var sharedFlags = new RiskComputeFlags();
        var sharedOutput = sampledShared.ComputeRisk(0.5d, 15d, sharedFlags, sharedRealization, recordOutput: true);
        double failWeight = sharedOutput.ProbabilityOfFailure;
        double partialWeight = sharedRealization.FailureModes[1].Curves.NonFail.RiskPoints[0].ResponseProbabilities[0];
        Assert.AreEqual(1d, failWeight + partialWeight, 1e-12,
            "Sibling branches of one shared response must partition exactly — one sampled curve drives p and 1 − p.");
    }

    /// <summary>
    /// Verifies the InverseSRP policy (§7.9): exact for a single-stage Fail-polarity
    /// mode, unsupported for cascades and Non-Fail polarities (a polarity product has no
    /// monotone inverse; no engine path consumes the member).
    /// </summary>
    [TestMethod]
    public void Test_MultiStage_InverseSRP_Policy()
    {
        // A cascade has no monotone inverse.
        var cascade = new FailureMode(
            new List<ResponseStage>
            {
                new ResponseStage(new List<RMC.TotalRisk.Core.Interfaces.ITransformFunction>(), Fragility()),
                new ResponseStage(new List<RMC.TotalRisk.Core.Interfaces.ITransformFunction>(), WideFragility()),
            },
            null,
            new List<RMC.TotalRisk.Core.Interfaces.IConsequenceFunction> { Consequence("Damages", 300d) });
        Assert.ThrowsException<NotSupportedException>(() => cascade.Sample(null).InverseSRP(0.5d));

        // A single-stage Non-Fail-polarity mode is not monotone increasing either.
        var nonFailFinal = new FailureMode(
            new List<ResponseStage>
            {
                new ResponseStage(new List<RMC.TotalRisk.Core.Interfaces.ITransformFunction>(), Fragility(), BranchPolarity.NonFail),
            },
            null,
            new List<RMC.TotalRisk.Core.Interfaces.IConsequenceFunction> { Consequence("Damages", 300d) });
        Assert.ThrowsException<NotSupportedException>(() => nonFailFinal.Sample(null).InverseSRP(0.5d));
    }

    /// <summary>Verifies the constructor argument contract.</summary>
    [TestMethod]
    public void Test_Constructor_NullMode_Throws()
    {
        // Act / Assert
        Assert.ThrowsException<ArgumentNullException>(() => new SampledFailureMode(null!, null));
    }
}
