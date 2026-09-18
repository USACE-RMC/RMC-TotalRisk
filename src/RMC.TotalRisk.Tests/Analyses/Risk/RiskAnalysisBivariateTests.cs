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
/// Unit tests for the full engine run over a bivariate component — the exhaustive recorded-mass
/// budgets through the conditional-bin fold, and the bit-identical reproducibility of one seed
/// at any thread count.
/// </summary>
[TestClass]
public class RiskAnalysisBivariateTests
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

    /// <summary>
    /// Builds the uncertain bivariate fixture: an independence-copula hazard over uncertain
    /// marginals, a Secondary-bound pool fragility with pool-signal damages, and a primary
    /// background terminal.
    /// </summary>
    private static SystemComponent UncertainBivariateComponent(int bins)
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

    /// <summary>Builds the small-run analysis over the uncertain bivariate component.</summary>
    private static RiskAnalysis Analysis(int bins = 8)
    {
        var analysis = new RiskAnalysis(new[] { UncertainBivariateComponent(bins) });
        analysis.Options.Realizations = 100;
        analysis.Options.RiskMeasures = RiskMeasureOptions.None;
        return analysis;
    }

    #endregion

    /// <summary>
    /// Verifies a bivariate run satisfies the exhaustive recorded-mass budgets at every scope —
    /// the one-point-per-evaluation fold and the exact Σw = 1 conditional weights keep both
    /// quadrature-ledger adoption gates intact.
    /// </summary>
    [TestMethod]
    public async Task Test_BivariateRun_ExhaustiveMassAtEveryScope()
    {
        // Arrange
        var analysis = Analysis();

        // Act
        await analysis.RunAsync();

        // Assert
        Assert.IsTrue(analysis.IsEstimated);
        var realization = analysis.MeanRiskResults!;
        Assert.AreEqual(1d, realization.Curves.Total.MassBalance, 0d);
        Assert.AreEqual(1d, realization.Curves.Total.TotalProbability, 0d);
        var component = realization.Components[0];
        Assert.AreEqual(1d, component.Curves.Total.MassBalance, 0d);
        Assert.AreEqual(1d, component.Curves.Total.TotalProbability, 0d);
        Assert.IsTrue(component.Curves.Fail.TotalProbability > 0d, "The pool fragility must produce failure mass.");
        for (int i = 0; i < component.FailureModes.Count; i++)
        {
            var modeTotal = component.FailureModes[i].Curves.Total;
            if (modeTotal.LECConsequences.Length == 0) continue;
            Assert.AreEqual(1d, modeTotal.MassBalance, 0d);
            Assert.AreEqual(1d, modeTotal.TotalProbability, 0d);
        }
    }

    /// <summary>
    /// Verifies one seed reproduces bit-identically at any thread count on a bivariate fixture:
    /// the single-threaded and production-parallel runs serialize to identical results.
    /// </summary>
    [TestMethod]
    public async Task Test_BivariateRun_BitIdenticalAtAnyThreadCount()
    {
        // Arrange
        var single = Analysis();
        single.MaximumDegreeOfParallelismOverride = 1;
        var parallel = Analysis();

        // Act
        await single.RunAsync();
        await parallel.RunAsync();

        // Assert — ensemble and mean results serialize identically.
        Assert.AreEqual(single.RiskResults!.ToJson(), parallel.RiskResults!.ToJson());
        Assert.AreEqual(single.MeanRiskResults!.ToJson(), parallel.MeanRiskResults!.ToJson());
    }
}
