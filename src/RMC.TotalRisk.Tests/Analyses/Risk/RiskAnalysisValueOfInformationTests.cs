using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.Systems.Components;

namespace RMC.TotalRisk.Tests.Analyses.Risk;

/// <summary>
/// Unit tests for the value-of-information surface on the analysis: null conditions, the
/// entry/group structure over the sampler walk, the exact share and rollup identities,
/// determinism, stored-ensemble inertness, the tolerable-risk movement blocks' bit agreement
/// with the published confidence entries, and the scope rules.
/// </summary>
[TestClass]
public class RiskAnalysisValueOfInformationTests
{
    /// <summary>Builds the deterministic stage-frequency hazard.</summary>
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

    /// <summary>Builds an uncertain fragility (triangular ordinates).</summary>
    private static TabularResponse UncertainFragility()
    {
        return new TabularResponse
        {
            Name = "Breach Fragility",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(10d, new Triangular(0d, 0.05d, 0.1d)), new UncertainOrdinate(20d, new Triangular(0.7d, 0.9d, 1d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Triangular),
        };
    }

    /// <summary>Builds an uncertain consequence, triangular about a linear ramp to the top value.</summary>
    private static TabularConsequence UncertainConsequence(string name, double top)
    {
        return new TabularConsequence
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Life Loss",
            ConsequenceUnit = "lives",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(0d, new Triangular(0d, 1d, 2d)),
                    new UncertainOrdinate(30d, new Triangular(0.8d * top, top, 1.2d * top)),
                },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Triangular),
        };
    }

    /// <summary>Builds a deterministic non-failure consequence.</summary>
    private static TabularConsequence DeterministicConsequence(string name, double top)
    {
        return new TabularConsequence
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Life Loss",
            ConsequenceUnit = "lives",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(0d, new Deterministic(0d)), new UncertainOrdinate(30d, new Deterministic(top)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>
    /// Builds a full-uncertainty one-component analysis with an uncertain fragility and an
    /// uncertain failure consequence — three knowledge columns: the fragility, the consequence
    /// function, and the mode's consequence-coupling draw.
    /// </summary>
    private static RiskAnalysis Build(params TolerableRiskCriterion[] criteria)
    {
        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = StageFrequency();
        component.AddFailureMode(new FailureMode(null, null, UncertainFragility(), UncertainConsequence("Failure Loss", 300d)));
        component.AddFailureMode(new FailureMode(null, null, null, DeterministicConsequence("Non-Failure Loss", 60d)));
        var analysis = new RiskAnalysis(new[] { component });
        analysis.Options.EstimateMeanRiskOnly = false;
        analysis.Options.Realizations = 100;
        for (int i = 0; i < criteria.Length; i++)
        {
            analysis.Options.TolerableRiskCriteria.Add(criteria[i]);
        }
        return analysis;
    }

    /// <summary>
    /// Verifies the null conditions: before estimation, and on a mean-only run with no stored
    /// ensemble.
    /// </summary>
    [TestMethod]
    public async Task Test_NoStoredEnsemble_ReturnsNull()
    {
        // Arrange
        var unestimated = Build();
        var meanOnly = Build();
        meanOnly.Options.EstimateMeanRiskOnly = true;

        // Act
        var before = unestimated.MeasureValueOfInformation(RiskMeasure.Mean, RiskType.Total);
        await meanOnly.RunAsync();
        var meanOnlyResult = meanOnly.MeasureValueOfInformation(RiskMeasure.Mean, RiskType.Total);

        // Assert
        Assert.IsNull(before);
        Assert.IsNull(meanOnlyResult);
    }

    /// <summary>
    /// Verifies the structure over the sampler walk: one entry per knowledge column aligned
    /// with the sensitivity engine's labels, groups partitioning the entries with exact summed
    /// rollups, and exact share identities against the total variance.
    /// </summary>
    [TestMethod]
    public async Task Test_FullRun_StructureAndIdentities()
    {
        // Arrange
        var analysis = Build();
        await analysis.RunAsync();

        // Act
        var voi = analysis.MeasureValueOfInformation(RiskMeasure.Mean, RiskType.Total);
        var sensitivity = analysis.MeasureSensitivity(RiskMeasure.Mean, RiskType.Total, SensitivityMeasure.PearsonCorrelation);

        // Assert — the same walk produces the same labels in the same order.
        Assert.IsNotNull(voi);
        Assert.IsNotNull(sensitivity);
        Assert.AreEqual(sensitivity!.Entries.Count, voi!.Entries.Count, "The knowledge columns are shared with the sensitivity engine.");
        for (int i = 0; i < voi.Entries.Count; i++)
        {
            Assert.AreEqual(sensitivity.Entries[i].Label, voi.Entries[i].Label, "Walk-order label alignment.");
        }
        Assert.AreEqual(100, voi.Realizations);
        Assert.AreEqual(20, voi.Bins);
        Assert.IsTrue(voi.TotalVariance > 0d, "An uncertain model has epistemic variance to resolve.");

        // The groups partition the entries and sum their members exactly.
        int memberCount = 0;
        foreach (var group in voi.Groups)
        {
            double sum = 0d;
            foreach (var entry in voi.Entries)
            {
                if (entry.GroupLabel == group.Label) { sum += entry.ResolvableVariance; memberCount++; }
            }
            Assert.AreEqual(sum, group.ResolvableVariance, 0d, $"Group '{group.Label}' sums its members bit-exactly.");
            Assert.AreEqual(group.ResolvableVariance / voi.TotalVariance, group.VarianceShare, 0d);
        }
        Assert.AreEqual(voi.Entries.Count, memberCount, "Every entry belongs to exactly one group.");
        foreach (var entry in voi.Entries)
        {
            Assert.AreEqual(entry.ResolvableVariance / voi.TotalVariance, entry.VarianceShare, 0d);
            Assert.IsTrue(double.IsNaN(entry.ResolvableVariance) || entry.ResolvableVariance >= 0d);
            Assert.IsTrue(double.IsNaN(entry.VarianceShare) || entry.VarianceShare <= 1d + 1e-12,
                "A main effect cannot exceed the total variance beyond rounding.");
        }
    }

    /// <summary>
    /// Verifies determinism and stored-ensemble inertness: two queries agree bit-for-bit and
    /// the stored results JSON is byte-identical across the call.
    /// </summary>
    [TestMethod]
    public async Task Test_Query_DeterministicAndInert()
    {
        // Arrange
        var analysis = Build();
        await analysis.RunAsync();
        string before = analysis.RiskResults!.ToJson();

        // Act
        var first = analysis.MeasureValueOfInformation(RiskMeasure.Mean, RiskType.Total);
        var second = analysis.MeasureValueOfInformation(RiskMeasure.Mean, RiskType.Total);
        string after = analysis.RiskResults!.ToJson();

        // Assert
        Assert.AreEqual(before, after, "A read-only query never mutates the stored ensemble.");
        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        Assert.AreEqual(first!.TotalVariance, second!.TotalVariance, 0d);
        for (int i = 0; i < first.Entries.Count; i++)
        {
            Assert.AreEqual(first.Entries[i].ResolvableVariance, second.Entries[i].ResolvableVariance, 0d);
        }
    }

    /// <summary>
    /// Verifies the tolerable-risk movement blocks: one per configured criterion, the baseline
    /// bit-equal to the published summary entry, the exact 2·p·(1−p) ceiling, and per-entry
    /// movements bounded by the ceiling.
    /// </summary>
    [TestMethod]
    public async Task Test_CriterionMovements_MatchPublishedConfidence()
    {
        // Arrange — thresholds spanning always-exceeded, mid-scale, and never-exceeded.
        var analysis = Build(
            new TolerableRiskCriterion(RiskMeasure.Mean, RiskType.Excess, 0, 0d),
            new TolerableRiskCriterion(RiskMeasure.Mean, RiskType.Excess, 0, 1d),
            new TolerableRiskCriterion(RiskMeasure.Mean, RiskType.Excess, 0, 1e12d));
        await analysis.RunAsync();
        var published = analysis.RiskResults!.Summary!.TolerableRiskConfidence;
        Assert.IsNotNull(published);

        // Act
        var voi = analysis.MeasureValueOfInformation(RiskMeasure.Mean, RiskType.Total);

        // Assert
        Assert.IsNotNull(voi);
        Assert.AreEqual(3, voi!.CriterionMovements.Count);
        for (int k = 0; k < 3; k++)
        {
            var block = voi.CriterionMovements[k];
            var entry = published![k];
            Assert.AreEqual(entry.Measure, block.Measure);
            Assert.AreEqual(entry.RiskType, block.RiskType);
            Assert.AreEqual(entry.Threshold, block.Threshold, 0d);
            Assert.AreEqual(entry.ExceedanceProbability, block.BaselineExceedanceProbability, 0d,
                "The movement baseline is the published confidence, bit-exactly.");
            double p = block.BaselineExceedanceProbability;
            Assert.AreEqual(2d * p * (1d - p), block.PerfectInformationMovement, 0d);
            Assert.AreEqual(voi.Entries.Count, block.EntryMovements.Count);
            for (int c = 0; c < block.EntryMovements.Count; c++)
            {
                double movement = block.EntryMovements[c];
                Assert.IsTrue(double.IsNaN(movement) || movement <= block.PerfectInformationMovement + 1e-12,
                    "No single input moves the statement beyond the perfect-information ceiling.");
            }
        }
    }

    /// <summary>
    /// Verifies post-run weights are honored: the weighted movement baseline equals the
    /// post-hoc weighted confidence recomputation bit-exactly, and the weighted total variance
    /// differs from the unweighted one.
    /// </summary>
    [TestMethod]
    public async Task Test_PostHocWeights_Honored()
    {
        // Arrange
        var analysis = Build(new TolerableRiskCriterion(RiskMeasure.Mean, RiskType.Excess, 0, 1d));
        await analysis.RunAsync();
        var unweighted = analysis.MeasureValueOfInformation(RiskMeasure.Mean, RiskType.Total);
        var weights = new double[analysis.RiskResults!.Count];
        for (int i = 0; i < weights.Length; i++) weights[i] = 1d + 0.35d * (i % 7);
        analysis.RiskResults.SetRealizationWeights(weights);

        // Act
        var weighted = analysis.MeasureValueOfInformation(RiskMeasure.Mean, RiskType.Total);
        var confidence = analysis.ComputeTolerableRiskConfidence();

        // Assert
        Assert.IsNotNull(weighted);
        Assert.IsNotNull(confidence);
        Assert.AreEqual(confidence![0].ExceedanceProbability, weighted!.CriterionMovements[0].BaselineExceedanceProbability, 0d,
            "The weighted baseline matches the post-hoc weighted confidence bit-exactly.");
        Assert.AreNotEqual(unweighted!.TotalVariance, weighted.TotalVariance,
            "Non-uniform weights move the weighted total variance.");
    }

    /// <summary>
    /// Verifies the scope rules: a component-scope query carries no movement blocks (criteria
    /// are system-scope statements) while keeping the component's walk columns.
    /// </summary>
    [TestMethod]
    public async Task Test_ComponentScope_NoMovementBlocks()
    {
        // Arrange
        var analysis = Build(new TolerableRiskCriterion(RiskMeasure.Mean, RiskType.Excess, 0, 1d));
        await analysis.RunAsync();

        // Act
        var scoped = analysis.MeasureValueOfInformation(RiskMeasure.Mean, RiskType.Total, componentIndex: 0);

        // Assert
        Assert.IsNotNull(scoped);
        Assert.AreEqual(0, scoped!.CriterionMovements.Count, "Criteria are defined at the system scope only.");
        Assert.IsTrue(scoped.Entries.Count > 0);
    }
}
