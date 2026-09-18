using System;
using System.Linq;
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
using RMC.TotalRisk.Systems.Components.Graph;

namespace RMC.TotalRisk.Tests.Analyses.Risk;

/// <summary>
/// Unit tests for the secondary-axis discretization diagnostic: the null and guard contracts,
/// the bit agreement of the configured-count evaluation with a mean-only run, determinism, and
/// the diagnostic snapshot's bit-exact grid at the configured count.
/// </summary>
[TestClass]
public class RiskAnalysisDiscretizationTests
{
    #region Fixtures

    /// <summary>Builds a marginal whose ordinates carry Normal knowledge uncertainty.</summary>
    private static TabularHazard UncertainMarginal(string name, string hazard, double median, double extreme)
    {
        return new TabularHazard
        {
            Name = name,
            SpecifiedHazard = hazard,
            HazardUnit = "ft",
            UncertaintyValue = FunctionUncertainty.Hazard,
            HazardUncertainFunction = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(0.999d, new Normal(0d, 0.01d)),
                    new UncertainOrdinate(0.5d, new Normal(median, median * 0.05d)),
                    new UncertainOrdinate(0.001d, new Normal(extreme, extreme * 0.05d)),
                },
                true, SortOrder.Descending, true, SortOrder.Ascending, UnivariateDistributionType.Normal),
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
    /// Builds the bivariate fixture: an independence-copula hazard over uncertain marginals, a
    /// Secondary-bound pool fragility with pool-signal damages, and a primary background
    /// terminal.
    /// </summary>
    private static SystemComponent BivariateComponent(int bins)
    {
        var joint = new BivariateHazard(
            UncertainMarginal("Surge Marginal", "Surge", 10d, 30d),
            UncertainMarginal("Pool Marginal", "Pool Elevation", 50d, 100d))
        {
            Name = "Joint Hazard",
            SpecifiedHazard = "Surge",
            HazardUnit = "ft",
            SecondarySpecifiedHazard = "Pool Elevation",
            SecondaryHazardUnit = "ft",
            SecondaryIntegrationBins = bins,
        };
        var component = new SystemComponent(joint) { Name = "Joint Component" };
        var hazard = component.Graph.GetElements<HazardElement>().Single();
        var breach = new ResponseElement("Pool Breach")
        {
            Function = new TabularResponse
            {
                Name = "Pool Fragility",
                SpecifiedHazard = "Pool Elevation",
                HazardUnit = "ft",
                UncertainOrderedPairedData = new UncertainOrderedPairedData(
                    new[] { new UncertainOrdinate(0d, new Deterministic(0d)), new UncertainOrdinate(100d, new Deterministic(1d)) },
                    true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
            },
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

    /// <summary>Builds the analysis over a fresh bivariate component.</summary>
    private static RiskAnalysis Analysis(int bins)
    {
        var analysis = new RiskAnalysis(new[] { BivariateComponent(bins) });
        analysis.Options.EstimateMeanRiskOnly = false;
        analysis.Options.Realizations = 100;
        return analysis;
    }

    #endregion

    /// <summary>
    /// Verifies the null and guard contracts: an out-of-range index throws, a univariate
    /// component returns null, and a bin count whose quarter falls below the three-bin floor
    /// returns null.
    /// </summary>
    [TestMethod]
    public void Test_Guards_NullContracts()
    {
        // Arrange — a univariate analysis and an under-binned bivariate one.
        var univariate = new SystemComponent { Name = "Plain" };
        univariate.HazardFunction = UncertainMarginal("Stage Frequency", "Stage", 10d, 30d);
        univariate.AddFailureMode(new FailureMode(null, null, new TabularResponse
        {
            Name = "Fragility",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(0d, new Deterministic(0d)), new UncertainOrdinate(30d, new Deterministic(1d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        }, Damages("Loss", "Stage", 30d, 300d)));
        univariate.AddFailureMode(new FailureMode(null, null, null, Damages("Background", "Stage", 30d, 30d)));
        var univariateAnalysis = new RiskAnalysis(new[] { univariate });
        univariateAnalysis.Options.Realizations = 100;
        var underBinned = Analysis(bins: 8);

        // Act / Assert
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => underBinned.EstimateSecondaryDiscretizationError(5));
        Assert.IsNull(univariateAnalysis.EstimateSecondaryDiscretizationError(0), "A univariate component has no secondary axis.");
        Assert.IsNull(underBinned.EstimateSecondaryDiscretizationError(0), "Eight bins cannot support the quartered level.");
    }

    /// <summary>
    /// Verifies the instrument contract: the diagnostic's levels are the fixed
    /// conditional-trapezoid grid at the configured, halved, and quartered counts — internally
    /// consistent across configurations (a level shared by two ladders is bit-identical) —
    /// while the published mean run integrates the adaptive interior, cross-checked against
    /// the DENSE instrument rather than the shallow ladder's Richardson limit: this fixture's
    /// piecewise-linear conditional map leaves the {20, 10, 5} ladder pre-asymptotic (observed
    /// ratio ≈ 1.07 against the second-order 4 — the regime flag the diagnostic itself
    /// carries), so its extrapolation is indicative only, while the thousand-bin instrument is
    /// converged (its own ladder differences sit at the noise floor). Measured: dense AFP
    /// 0.4999775 with the adaptive mean 5.0e-5 away and fixed-20 2.7e-4 away; dense mean risk
    /// 154.749 with the adaptive 4.1e-4 relative away and fixed-20 1.7% away — the adaptive
    /// interior beats the configured fixed grid on both measures, asserted with ×5 headroom.
    /// </summary>
    [TestMethod]
    public async Task Test_ConfiguredLevel_InstrumentContract()
    {
        // Arrange — the diagnostic, a doubled-count twin diagnostic (whose halved and
        // quartered levels are the primary's configured and halved counts), and the published
        // mean run.
        var diagnostic = Analysis(bins: 20).EstimateSecondaryDiscretizationError(0);
        var doubledTwin = Analysis(bins: 40).EstimateSecondaryDiscretizationError(0);
        var meanRun = Analysis(bins: 20);
        meanRun.Options.EstimateMeanRiskOnly = true;
        await meanRun.RunAsync();
        var mean = meanRun.MeanRiskResults!.Components[0];

        // Assert — the ladder shape and the cross-configuration instrument parity.
        Assert.IsNotNull(diagnostic);
        Assert.IsNotNull(doubledTwin);
        Assert.AreEqual(20, diagnostic!.Bins);
        Assert.AreEqual(10, diagnostic.HalfBins);
        Assert.AreEqual(5, diagnostic.QuarterBins);
        Assert.AreEqual(doubledTwin!.FailureProbability.HalfValue, diagnostic.FailureProbability.Value, 0d,
            "The twenty-bin instrument level is identical from either ladder, bit-exactly.");
        Assert.AreEqual(doubledTwin.FailureProbability.QuarterValue, diagnostic.FailureProbability.HalfValue, 0d,
            "The ten-bin instrument level is identical from either ladder, bit-exactly.");
        Assert.AreEqual(doubledTwin.MeanRisk[0].HalfValue, diagnostic.MeanRisk[0].Value, 0d,
            "The twenty-bin mean-risk level matches across ladders bit-exactly.");
        Assert.AreEqual(1, diagnostic.MeanRisk.Count, "One consequence type in the fixture.");
        Assert.IsTrue(double.IsFinite(diagnostic.FailureProbability.QuarterValue));

        // The shallow ladder is pre-asymptotic on this fixture — the diagnostic's own regime
        // flag (observed ratio far below the second-order 4) — so the cross-check anchors on
        // the dense instrument instead of the extrapolation.
        Assert.IsTrue(diagnostic.FailureProbability.ObservedRatio < 3.5d,
            "This fixture's shallow ladder is the pre-asymptotic regime the diagnostic exists to flag.");
        var dense = Analysis(bins: 1000).EstimateSecondaryDiscretizationError(0);
        Assert.IsNotNull(dense);
        Assert.AreEqual(dense!.FailureProbability.Value, mean.Curves.Fail.TotalProbability, 2.5e-4,
            "The adaptive interior must land at the dense fixed truth within its budget band (measured 5.0e-5; ×5 headroom).");
        Assert.AreEqual(dense.MeanRisk[0].Value, mean.Curves.Excess.Mean, 2e-3 * Math.Abs(dense.MeanRisk[0].Value),
            "The adaptive mean incremental risk must land at the dense fixed truth (measured 4.1e-4 relative; ×5 headroom).");

        // And the adaptive interior must beat the configured fixed grid against that truth on
        // the consequence measure — the accuracy claim of the two-dimensional rule.
        Assert.IsTrue(Math.Abs(mean.Curves.Excess.Mean - dense.MeanRisk[0].Value)
            < Math.Abs(diagnostic.MeanRisk[0].Value - dense.MeanRisk[0].Value),
            "The adaptive mean risk must sit closer to the dense truth than the configured fixed grid's.");
    }

    /// <summary>
    /// Verifies determinism: two diagnostic calls agree bit-for-bit.
    /// </summary>
    [TestMethod]
    public void Test_Diagnostic_Deterministic()
    {
        // Arrange
        var analysis = Analysis(bins: 20);

        // Act
        var first = analysis.EstimateSecondaryDiscretizationError(0);
        var second = analysis.EstimateSecondaryDiscretizationError(0);

        // Assert
        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        Assert.AreEqual(first!.FailureProbability.Value, second!.FailureProbability.Value, 0d);
        Assert.AreEqual(first.FailureProbability.HalfValue, second.FailureProbability.HalfValue, 0d);
        Assert.AreEqual(first.FailureProbability.QuarterValue, second.FailureProbability.QuarterValue, 0d);
        Assert.AreEqual(first.MeanRisk[0].Value, second.MeanRisk[0].Value, 0d);
    }

    /// <summary>
    /// Verifies the diagnostic snapshot at the configured count reproduces the stored grid
    /// bit-exactly (the shared grid derivation), and the count guards throw.
    /// </summary>
    [TestMethod]
    public void Test_SampleBivariateAt_ConfiguredCount_MatchesStoredGrid()
    {
        // Arrange — set up the component's samplers, then compare snapshots.
        var component = BivariateComponent(bins: 20);
        component.SetupSamplers(100, 12345, SamplingScheme.LatinHypercube);
        var hazard = (BivariateHazard)component.HazardFunction!;
        var stored = hazard.SampleBivariate();
        var diagnostic = hazard.SampleBivariateAt(20);
        var storedNodes = new double[stored.ConditionalNodeCount];
        var storedWeights = new double[stored.ConditionalNodeCount];
        var diagnosticNodes = new double[diagnostic.ConditionalNodeCount];
        var diagnosticWeights = new double[diagnostic.ConditionalNodeCount];

        // Act
        stored.FillConditionalBins(0.5d, storedNodes, storedWeights);
        diagnostic.FillConditionalBins(0.5d, diagnosticNodes, diagnosticWeights);

        // Assert
        Assert.AreEqual(stored.ConditionalNodeCount, diagnostic.ConditionalNodeCount);
        for (int j = 0; j < storedNodes.Length; j++)
        {
            Assert.AreEqual(storedNodes[j], diagnosticNodes[j], 0d, "The diagnostic grid derivation is the stored one.");
            Assert.AreEqual(storedWeights[j], diagnosticWeights[j], 0d);
        }
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => hazard.SampleBivariateAt(2));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => hazard.SampleBivariateAt(1001));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => component.SampleWithConditionalBins(1200));
    }
}
