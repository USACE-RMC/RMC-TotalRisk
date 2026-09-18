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
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.RiskFunctions.Transforms;
using RMC.TotalRisk.Systems.Components;

namespace RMC.TotalRisk.Tests.Analyses.Risk;

/// <summary>
/// Unit tests for the epistemic conditioning (fractile pinning) surface — the property
/// lifecycle, the validation matrix, the seed-inert walk application, the run-level movement
/// and mean-only inertness, the unreachable-pin refusal, and the inert reporting of a pinned
/// column by the sensitivity and value-of-information surfaces.
/// </summary>
[TestClass]
public class FractilePinningTests
{
    /// <summary>Builds a deterministic three-point stage-frequency hazard.</summary>
    private static TabularHazard Hazard()
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

    /// <summary>Builds an uncertain triangular fragility over three ordinates.</summary>
    private static TabularResponse UncertainFragility()
    {
        return new TabularResponse
        {
            Name = "Fragility",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(10d, new Triangular(0d, 0.05d, 0.1d)),
                    new UncertainOrdinate(15d, new Triangular(0.2d, 0.4d, 0.6d)),
                    new UncertainOrdinate(20d, new Triangular(0.7d, 0.9d, 1d)),
                },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Triangular),
        };
    }

    /// <summary>Builds an uncertain rating-curve transform.</summary>
    private static TabularTransform UncertainRating()
    {
        return new TabularTransform
        {
            Name = "Rating",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            TransformedHazard = "Stage",
            TransformedHazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(0d, new Normal(0d, 1d)), new UncertainOrdinate(30d, new Normal(30d, 1d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Normal),
        };
    }

    /// <summary>Builds a deterministic damage curve.</summary>
    private static TabularConsequence Damages()
    {
        return new TabularConsequence
        {
            Name = "Damages",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(0d, new Deterministic(0d)), new UncertainOrdinate(30d, new Deterministic(1000d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds the standard analysis: rating + fragility + damages behind the hazard.</summary>
    private static (RiskAnalysis Analysis, TabularTransform Rating, TabularResponse Fragility) Build(int realizations)
    {
        var rating = UncertainRating();
        var fragility = UncertainFragility();
        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = Hazard();
        component.AddFailureMode(new FailureMode(
            new List<ITransformFunction> { rating }, null, fragility, Damages()));
        var analysis = new RiskAnalysis(new[] { component });
        analysis.Options.EstimateMeanRiskOnly = false;
        analysis.Options.Realizations = realizations;
        return (analysis, rating, fragility);
    }

    /// <summary>Verifies the property lifecycle: defensive copy, invalidation, and null clearing.</summary>
    [TestMethod]
    public void Test_Property_Lifecycle()
    {
        // Arrange
        var (analysis, _, fragility) = Build(100);
        var raised = new List<string>();
        analysis.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        // Act — a defensive copy: later list edits do not reach the analysis.
        var pins = new List<FractilePin> { new FractilePin(fragility.Id, 0.9d) };
        analysis.FractilePins = pins;
        pins.Clear();

        // Assert
        Assert.AreEqual(1, analysis.FractilePins!.Count);
        Assert.AreEqual(fragility.Id, analysis.FractilePins[0].FunctionId);
        Assert.IsFalse(analysis.IsEstimated);
        CollectionAssert.Contains(raised, nameof(RiskAnalysis.FractilePins));

        analysis.FractilePins = null;
        Assert.IsNull(analysis.FractilePins);
    }

    /// <summary>
    /// Verifies the validation matrix: duplicate and unmatched ids are errors; consequence and
    /// deterministic targets are no-effect warnings; a valid pin adds no message; a mean-only
    /// run warns that pins are ignored.
    /// </summary>
    [TestMethod]
    public void Test_Validate_Matrix()
    {
        // Arrange
        var (analysis, _, fragility) = Build(100);

        // A valid pin adds no pin message.
        analysis.FractilePins = new[] { new FractilePin(fragility.Id, 0.9d) };
        var (validOk, validMessages) = analysis.Validate();
        Assert.IsTrue(validOk);
        Assert.IsFalse(validMessages.Any(m => m.Contains("Fractile pin", StringComparison.Ordinal)));

        // Duplicate ids error.
        analysis.FractilePins = new[] { new FractilePin(fragility.Id, 0.9d), new FractilePin(fragility.Id, 0.5d) };
        var (duplicateOk, duplicateMessages) = analysis.Validate();
        Assert.IsFalse(duplicateOk);
        Assert.IsTrue(duplicateMessages.Any(m => m.StartsWith("Error:", StringComparison.Ordinal) && m.Contains("repeats function id")));

        // An unmatched id errors.
        analysis.FractilePins = new[] { new FractilePin(Guid.NewGuid(), 0.9d) };
        var (unknownOk, unknownMessages) = analysis.Validate();
        Assert.IsFalse(unknownOk);
        Assert.IsTrue(unknownMessages.Any(m => m.StartsWith("Error:", StringComparison.Ordinal) && m.Contains("no component references")));

        // A consequence target warns (excluded: coupling-matrix draws), still valid.
        var damages = (TabularConsequence)analysis.Components[0].FailureModes[0].ConsequenceFunctions[0];
        analysis.FractilePins = new[] { new FractilePin(damages.Id, 0.9d) };
        var (consequenceOk, consequenceMessages) = analysis.Validate();
        Assert.IsTrue(consequenceOk);
        Assert.IsTrue(consequenceMessages.Any(m => m.StartsWith("Warning:", StringComparison.Ordinal) && m.Contains("coupling matrix")));

        // A deterministic target warns (no effect), still valid.
        var hazard = analysis.Components[0].HazardFunction!;
        analysis.FractilePins = new[] { new FractilePin(hazard.Id, 0.9d) };
        var (deterministicOk, deterministicMessages) = analysis.Validate();
        Assert.IsTrue(deterministicOk);
        Assert.IsTrue(deterministicMessages.Any(m => m.StartsWith("Warning:", StringComparison.Ordinal) && m.Contains("deterministic")));

        // A mean-only run warns that pins are ignored.
        analysis.FractilePins = new[] { new FractilePin(fragility.Id, 0.9d) };
        analysis.Options.EstimateMeanRiskOnly = true;
        var (meanOnlyOk, meanOnlyMessages) = analysis.Validate();
        Assert.IsTrue(meanOnlyOk);
        Assert.IsTrue(meanOnlyMessages.Any(m => m.StartsWith("Warning:", StringComparison.Ordinal) && m.Contains("mean-only")));
    }

    /// <summary>
    /// Verifies the seed-inert walk application directly: with a pin, the pinned function's
    /// percentile column is the constant pin value, every other function's column is
    /// bit-identical to the unpinned walk, and the applied-pin sink records the id.
    /// </summary>
    [TestMethod]
    public void Test_Walk_PinnedColumnConstant_OthersBitIdentical()
    {
        // Arrange — one component, seeded twice with identical seeds: without and with the pin.
        var rating = UncertainRating();
        var fragility = UncertainFragility();
        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = Hazard();
        component.AddFailureMode(new FailureMode(
            new List<ITransformFunction> { rating }, null, fragility, Damages()));

        const int N = 64;
        component.SetupSamplers(N, 12345, SamplingScheme.LatinHypercube);
        var ratingDraws = new double[N];
        for (int i = 0; i < N; i++) ratingDraws[i] = rating.SampledPercentile(i, 0);

        // Act — the same seeds with the fragility pinned at 0.9.
        var pins = new Dictionary<Guid, double> { [fragility.Id] = 0.9d };
        var applied = new HashSet<Guid>();
        component.SetupSamplers(N, 12345, SamplingScheme.LatinHypercube, null, pins, applied);

        // Assert
        CollectionAssert.Contains(applied.ToList(), fragility.Id);
        for (int i = 0; i < N; i++)
        {
            Assert.AreEqual(0.9d, fragility.SampledPercentile(i, 0), 0d, "The pinned column must be the constant pin value.");
            Assert.AreEqual(ratingDraws[i], rating.SampledPercentile(i, 0), 0d, "An unpinned function's draws must be bit-identical.");
        }
    }

    /// <summary>
    /// Verifies the run-level contract: a pinned run's captured sampler seeds are identical to
    /// the unpinned run's (pins are seed-inert), its results move (the conditioning is real),
    /// pins are never serialized (the analysis content hash is unmoved), and a mean-only run is
    /// byte-identical with or without pins.
    /// </summary>
    [TestMethod]
    public void Test_Run_SeedsInert_ResultsMove_MeanOnlyIgnores()
    {
        // Arrange / Act — the unpinned and pinned full runs on equal-content analyses.
        var (unpinned, _, _) = Build(100);
        unpinned.RunAsync().GetAwaiter().GetResult();
        var (pinnedAnalysis, _, pinnedFragility) = Build(100);
        pinnedAnalysis.FractilePins = new[] { new FractilePin(pinnedFragility.Id, 0.95d) };
        pinnedAnalysis.RunAsync().GetAwaiter().GetResult();

        // Assert — captured seeds identical, ordinal for ordinal.
        var unpinnedSeeds = unpinned.CapturedSamplerSeeds!;
        var pinnedSeeds = pinnedAnalysis.CapturedSamplerSeeds!;
        Assert.AreEqual(unpinnedSeeds.ComponentCount, pinnedSeeds.ComponentCount);
        for (int c = 0; c < unpinnedSeeds.ComponentSeeds.Count; c++)
        {
            CollectionAssert.AreEqual(unpinnedSeeds.ComponentSeeds[c], pinnedSeeds.ComponentSeeds[c],
                "A pin must never move a captured sampler seed.");
        }

        // The analysis content hash is unmoved (pins are runtime-only, never serialized).
        Assert.AreEqual(unpinned.RiskResults!.Manifest!.AnalysisContentHash,
            pinnedAnalysis.RiskResults!.Manifest!.AnalysisContentHash);

        // The conditioning is real: the ensemble summary moves.
        Assert.AreNotEqual(unpinned.RiskResults.Summary!.Mean.Total.Mean,
            pinnedAnalysis.RiskResults.Summary!.Mean.Total.Mean);

        // Mean-only runs ignore pins byte-for-byte.
        var (meanPlain, _, _) = Build(100);
        meanPlain.Options.EstimateMeanRiskOnly = true;
        meanPlain.RunAsync().GetAwaiter().GetResult();
        var (meanPinned, _, meanFragility) = Build(100);
        meanPinned.Options.EstimateMeanRiskOnly = true;
        meanPinned.FractilePins = new[] { new FractilePin(meanFragility.Id, 0.95d) };
        meanPinned.RunAsync().GetAwaiter().GetResult();
        Assert.AreEqual(meanPlain.RiskResults!.ToJson(), meanPinned.RiskResults!.ToJson());
    }

    /// <summary>
    /// Verifies the unreachable-pin refusal: a response referenced only inside an event tree's
    /// probability sources samples through the tree's own setup clones, is not a
    /// component-referenced function, and so cannot be conditioned — the pin is refused loudly
    /// at validation and at run start instead of silently not conditioning. (The run's
    /// post-walk reconciliation remains as defense in depth behind this classification.)
    /// </summary>
    [TestMethod]
    public void Test_Run_UnreachablePin_RefusedLoudly()
    {
        // Arrange — an event-tree response whose chance node references an uncertain response.
        var referenced = UncertainFragility();
        referenced.Name = "Referenced fragility";
        var tree = new EventTree();
        tree.Add(tree.Root.Id, new ChanceNode("Breach", new ProbabilitySource(referenced)));
        tree.Add(tree.Root.Id, new RemainderNode("Survival"));
        var treeResponse = new EventTreeResponse(new[] { 10d, 20d, 30d }, tree)
        {
            Name = "Breach tree",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };

        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = Hazard();
        component.AddFailureMode(new FailureMode(null, null, treeResponse, Damages()));
        var analysis = new RiskAnalysis(new[] { component });
        analysis.Options.EstimateMeanRiskOnly = false;
        analysis.Options.Realizations = 100;
        analysis.FractilePins = new[] { new FractilePin(referenced.Id, 0.9d) };

        // Act / Assert — refused at validation and at run start.
        var (isValid, messages) = analysis.Validate();
        Assert.IsFalse(isValid);
        Assert.IsTrue(messages.Any(m => m.StartsWith("Error:", StringComparison.Ordinal) && m.Contains("no component references")));
        var fault = Assert.ThrowsException<InvalidOperationException>(
            () => analysis.RunAsync().GetAwaiter().GetResult());
        StringAssert.Contains(fault.Message, "no component references");
    }

    /// <summary>
    /// Verifies the interplay with the post-hoc surfaces: a pinned function's knowledge column
    /// is constant, so the correlation sensitivity reports zero, the given-data measures report
    /// zero, and the value-of-information entry resolves zero variance — reported inert, never
    /// special-cased away.
    /// </summary>
    [TestMethod]
    public void Test_PostHoc_PinnedColumn_ReportsInert()
    {
        // Arrange — a pinned full run.
        var (analysis, rating, fragility) = Build(200);
        analysis.FractilePins = new[] { new FractilePin(fragility.Id, 0.75d) };
        analysis.RunAsync().GetAwaiter().GetResult();

        // Act
        var pearson = analysis.MeasureSensitivity(RiskMeasure.Mean, RiskType.Total, SensitivityMeasure.SensitivityIndex)!;
        var sobol = analysis.MeasureSensitivity(RiskMeasure.Mean, RiskType.Total, SensitivityMeasure.FirstOrderSobol)!;
        var voi = analysis.MeasureValueOfInformation(RiskMeasure.Mean, RiskType.Total)!;

        // Assert — the pinned column is inert on every surface; the unpinned column is not.
        var pinnedEntry = pearson.Entries.Single(e => e.Label.Contains(fragility.Name, StringComparison.Ordinal));
        var freeEntry = pearson.Entries.Single(e => e.Label.Contains(rating.Name, StringComparison.Ordinal));
        Assert.AreEqual(0d, pinnedEntry.Value, 0d);
        Assert.AreNotEqual(0d, freeEntry.Value);

        var pinnedSobol = sobol.Entries.Single(e => e.Label.Contains(fragility.Name, StringComparison.Ordinal));
        Assert.AreEqual(0d, pinnedSobol.Value, 0d);

        var pinnedVoi = voi.Entries.Single(e => e.Label.Contains(fragility.Name, StringComparison.Ordinal));
        var freeVoi = voi.Entries.Single(e => e.Label.Contains(rating.Name, StringComparison.Ordinal));
        Assert.AreEqual(0d, pinnedVoi.ResolvableVariance, 0d);
        Assert.IsTrue(freeVoi.ResolvableVariance > 0d);
    }
}
