using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.Systems.Components;

namespace RMC.TotalRisk.Tests.Analyses.Risk;

/// <summary>
/// Unit tests for the runtime-only retention surface: the default-off inertness of the
/// retained ensemble and the integration-detail selector, post-hoc re-banding reproducing the
/// published bands bit-exactly (and the weighted re-band equaling a weighted run's published
/// bands), the retained risk-point ledger, the guards, and the validation warnings.
/// </summary>
[TestClass]
public class RiskAnalysisRetentionTests
{
    /// <summary>Builds the trivial uncertain single-component analysis.</summary>
    private static RiskAnalysis Build(int realizations = 100)
    {
        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = new TabularHazard
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
        var fragility = new TabularResponse
        {
            Name = "Breach Fragility",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(10d, new Triangular(0d, 0.05d, 0.1d)), new UncertainOrdinate(20d, new Triangular(0.7d, 0.9d, 1d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Triangular),
        };
        var loss = new TabularConsequence
        {
            Name = "Failure Loss",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Life Loss",
            ConsequenceUnit = "lives",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(0d, new Deterministic(0d)), new UncertainOrdinate(30d, new Deterministic(300d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
        var nonFailure = new TabularConsequence
        {
            Name = "Non-Failure Loss",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Life Loss",
            ConsequenceUnit = "lives",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(0d, new Deterministic(0d)), new UncertainOrdinate(30d, new Deterministic(60d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
        var component1 = component;
        component1.AddFailureMode(new FailureMode(null, null, fragility, loss));
        component1.AddFailureMode(new FailureMode(null, null, null, nonFailure));
        var analysis = new RiskAnalysis(new[] { component1 });
        analysis.Options.EstimateMeanRiskOnly = false;
        analysis.Options.Realizations = realizations;
        return analysis;
    }

    /// <summary>The deterministic index-varying weight pattern of the weighted fixtures.</summary>
    private static double[] Weights(int count)
    {
        var weights = new double[count];
        for (int i = 0; i < count; i++) weights[i] = 0.25d + ((37 * i) % 11);
        return weights;
    }

    /// <summary>
    /// Verifies the defaults and default-off inertness: nothing retained, and a retained run's
    /// published payloads byte-identical to an un-retained run of the identical content.
    /// </summary>
    [TestMethod]
    public async Task Test_Defaults_OffAndByteInert()
    {
        // Arrange
        var plain = Build();
        var retained = Build();
        retained.RetainRealizations = true;
        retained.RetainedIntegrationDetailIndex = 3;

        // Act
        await plain.RunAsync();
        await retained.RunAsync();

        // Assert — defaults and stores.
        Assert.IsFalse(plain.RetainRealizations);
        Assert.IsNull(plain.RetainedIntegrationDetailIndex);
        Assert.IsNull(plain.RetainedRealizations);
        Assert.IsNull(plain.RetainedIntegrationDetail);
        Assert.IsNotNull(retained.RetainedRealizations);
        Assert.IsNotNull(retained.RetainedIntegrationDetail);

        // Retention never moves a published byte (identical content, identical manifests).
        Assert.AreEqual(plain.RiskResults!.ToJson(), retained.RiskResults!.ToJson());
        Assert.AreEqual(plain.MeanRiskResults!.ToJson(), retained.MeanRiskResults!.ToJson());
        Assert.AreEqual(plain.LowerRiskResults!.ToJson(), retained.LowerRiskResults!.ToJson());
        Assert.AreEqual(plain.UpperRiskResults!.ToJson(), retained.UpperRiskResults!.ToJson());
    }

    /// <summary>
    /// Verifies the retained ensemble re-assembles the published bands bit-exactly at null
    /// weights: the retained realizations are the run's own state, so the unweighted re-band
    /// is the published assembly verbatim.
    /// </summary>
    [TestMethod]
    public async Task Test_Reassemble_NullWeights_ReproducesPublishedBands()
    {
        // Arrange
        var analysis = Build();
        analysis.RetainRealizations = true;
        await analysis.RunAsync();
        Assert.AreEqual(100, analysis.RetainedRealizations!.Count);

        // Act
        var bands = analysis.ReassemblePercentileBands();

        // Assert — stamp the published manifest onto the fresh objects, then compare bytes.
        bands.Lower!.Manifest = analysis.LowerRiskResults!.Manifest;
        bands.Upper!.Manifest = analysis.UpperRiskResults!.Manifest;
        bands.Median!.Manifest = analysis.MedianRiskResults!.Manifest;
        bands.Mean!.Manifest = analysis.MeanRiskResults!.Manifest;
        Assert.AreEqual(analysis.LowerRiskResults.ToJson(), bands.Lower.ToJson(), "The unweighted re-band is the published lower band verbatim.");
        Assert.AreEqual(analysis.UpperRiskResults.ToJson(), bands.Upper.ToJson());
        Assert.AreEqual(analysis.MedianRiskResults!.ToJson(), bands.Median.ToJson());
        Assert.AreEqual(analysis.MeanRiskResults.ToJson(), bands.Mean.ToJson());
    }

    /// <summary>
    /// Verifies the post-hoc weighted re-band equals a weighted run's published bands
    /// bit-exactly — the retention synergy: weights never move a sampled realization, so
    /// re-banding the retained unweighted ensemble under new weights is exactly the run that
    /// carried them as its input.
    /// </summary>
    [TestMethod]
    public async Task Test_Reassemble_Weighted_EqualsWeightedRunBands()
    {
        // Arrange — one retained unweighted run, one run-input-weighted run, identical content.
        var retained = Build();
        retained.RetainRealizations = true;
        await retained.RunAsync();
        var weighted = Build();
        weighted.RealizationWeights = Weights(100);
        await weighted.RunAsync();

        // Act
        var bands = retained.ReassemblePercentileBands(Weights(100));

        // Assert
        bands.Lower!.Manifest = weighted.LowerRiskResults!.Manifest;
        bands.Upper!.Manifest = weighted.UpperRiskResults!.Manifest;
        bands.Median!.Manifest = weighted.MedianRiskResults!.Manifest;
        bands.Mean!.Manifest = weighted.MeanRiskResults!.Manifest;
        Assert.AreEqual(weighted.LowerRiskResults.ToJson(), bands.Lower.ToJson(),
            "The post-hoc weighted re-band must equal the weighted run's published lower band.");
        Assert.AreEqual(weighted.UpperRiskResults.ToJson(), bands.Upper.ToJson());
        Assert.AreEqual(weighted.MedianRiskResults!.ToJson(), bands.Median.ToJson());
        Assert.AreEqual(weighted.MeanRiskResults.ToJson(), bands.Mean.ToJson());
    }

    /// <summary>
    /// Verifies the integration-detail selector on a full run: the selected realization keeps
    /// its recorded risk points while its ensemble peers release theirs.
    /// </summary>
    [TestMethod]
    public async Task Test_IntegrationDetail_FullRun_KeepsSelectedPointsOnly()
    {
        // Arrange
        var analysis = Build();
        analysis.RetainRealizations = true;
        analysis.RetainedIntegrationDetailIndex = 3;

        // Act
        await analysis.RunAsync();

        // Assert
        var detail = analysis.RetainedIntegrationDetail;
        Assert.IsNotNull(detail);
        Assert.AreSame(analysis.RetainedRealizations![3], detail, "The detail realization is the selected ensemble member.");
        Assert.IsTrue(detail!.Components[0].Curves.Total.RiskPoints.Count > 0, "The selected realization keeps its recorded ledger.");
        Assert.AreEqual(0, analysis.RetainedRealizations[0].Components[0].Curves.Total.RiskPoints.Count,
            "Unselected realizations release their points as always.");
    }

    /// <summary>
    /// Verifies the mean-only selector: −1 retains the mean pass itself, points intact.
    /// </summary>
    [TestMethod]
    public async Task Test_IntegrationDetail_MeanOnly_RetainsMeanPass()
    {
        // Arrange
        var analysis = Build();
        analysis.Options.EstimateMeanRiskOnly = true;
        analysis.RetainedIntegrationDetailIndex = -1;

        // Act
        await analysis.RunAsync();

        // Assert
        Assert.IsNotNull(analysis.RetainedIntegrationDetail);
        Assert.AreSame(analysis.MeanRiskResults, analysis.RetainedIntegrationDetail);
        Assert.IsTrue(analysis.RetainedIntegrationDetail!.Components[0].Curves.Total.RiskPoints.Count > 0);
    }

    /// <summary>
    /// Verifies the re-band guards: no retained ensemble throws, and an invalid weight vector
    /// is refused by the shared weight rule.
    /// </summary>
    [TestMethod]
    public async Task Test_Reassemble_Guards()
    {
        // Arrange
        var bare = Build();
        var retained = Build();
        retained.RetainRealizations = true;
        await retained.RunAsync();

        // Act / Assert
        Assert.ThrowsException<InvalidOperationException>(() => bare.ReassemblePercentileBands());
        Assert.ThrowsException<ArgumentException>(() => retained.ReassemblePercentileBands(new double[3]));
        Assert.ThrowsException<ArgumentException>(() => retained.ReassemblePercentileBands(new double[100]));
    }

    /// <summary>
    /// Verifies the validation warnings: the memory-cost advisory while retention is on, the
    /// mean-only no-op wording, and the unmatched detail-index advisory — all warnings, never
    /// errors.
    /// </summary>
    [TestMethod]
    public void Test_Validate_RetentionWarnings()
    {
        // Arrange
        var retention = Build();
        retention.RetainRealizations = true;
        var meanOnly = Build();
        meanOnly.Options.EstimateMeanRiskOnly = true;
        meanOnly.RetainRealizations = true;
        var unmatched = Build();
        unmatched.RetainedIntegrationDetailIndex = 500;
        var matched = Build();
        matched.RetainedIntegrationDetailIndex = 10;

        // Act / Assert
        var (valid1, messages1) = retention.Validate();
        Assert.IsTrue(valid1);
        Assert.IsTrue(messages1.Exists(m => m.StartsWith("Warning:") && m.Contains("Retaining the realization ensemble")));

        var (valid2, messages2) = meanOnly.Validate();
        Assert.IsTrue(valid2);
        Assert.IsTrue(messages2.Exists(m => m.Contains("a mean-only run retains nothing")));

        var (valid3, messages3) = unmatched.Validate();
        Assert.IsTrue(valid3);
        Assert.IsTrue(messages3.Exists(m => m.Contains("matches no realization")));

        var (valid4, messages4) = matched.Validate();
        Assert.IsTrue(valid4);
        Assert.IsFalse(messages4.Exists(m => m.Contains("matches no realization")));
    }
}
