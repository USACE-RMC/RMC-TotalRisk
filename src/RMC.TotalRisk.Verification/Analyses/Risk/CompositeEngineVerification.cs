using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using Numerics.Mathematics.Integration;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.Systems.Components;

namespace RMC.TotalRisk.Verification.Analyses.Risk;

/// <summary>
/// Engine-level verification of the legacy composite consequence, hazard, and response scenarios.
/// </summary>
/// <remarks>
/// <para>
/// These tests port the model configurations from legacy Test_Composite.vb and the mixture
/// consistency case in Test_RiskAnalysis.vb. Deterministic means are checked against independent
/// fixed Gauss-Legendre integrals assembled directly from Numerics distributions and the legacy
/// piecewise-linear loss table; they are not pinned to current engine output.
/// </para>
/// <para>
/// Composite hazards are represented internally on their established empirical grid. The two
/// hazard-level checks therefore use a 0.2% deterministic discretization allowance, below the
/// verification report's 1% “very good” criterion. Consequence and response checks retain the
/// tighter 2e-4 relative allowance. These are verification assertions, not engine defaults.
/// </para>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// </remarks>
[TestClass]
public class CompositeEngineVerification
{
    /// <summary>The legacy day weight; night carries the complement.</summary>
    private const double DayWeight = 0.45d;

    /// <summary>Builds a deterministic parametric hazard from a Numerics distribution.</summary>
    /// <param name="name">The hazard name.</param>
    /// <param name="distribution">The parent distribution.</param>
    /// <returns>The estimated deterministic hazard.</returns>
    private static ParametricUnivariateHazard Hazard(string name, UnivariateDistributionBase distribution)
    {
        var hazard = new ParametricUnivariateHazard
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            ParentDistribution = distribution,
            EffectiveRecordLength = 100,
            Realizations = 100,
            IsUncertain = false,
        };
        hazard.Estimate();
        return hazard;
    }

    /// <summary>Builds a deterministic parametric response from a Numerics distribution.</summary>
    /// <param name="name">The response name.</param>
    /// <param name="distribution">The parent capacity distribution.</param>
    /// <returns>The estimated deterministic response.</returns>
    private static ParametricResponse Response(string name, UnivariateDistributionBase distribution)
    {
        var response = new ParametricResponse
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            ParentDistribution = distribution,
            EffectiveRecordLength = 100,
            Realizations = 100,
            IsUncertain = false,
        };
        response.Estimate();
        return response;
    }

    /// <summary>Builds the legacy piecewise-linear consequence table.</summary>
    /// <param name="name">The consequence name.</param>
    /// <param name="scale">The multiplier applied to the legacy daytime ordinates.</param>
    /// <param name="uncertain">True to give each positive ordinate symmetric triangular uncertainty.</param>
    /// <returns>The tabular consequence.</returns>
    private static TabularConsequence Loss(string name, double scale, bool uncertain = false)
    {
        double[] x = { 60d, 100d, 140d, 200d, 250d };
        double[] y = { 0d, 10d, 100d, 1000d, 1500d };
        var ordinates = new UncertainOrdinate[x.Length];
        for (int i = 0; i < x.Length; i++)
        {
            double value = scale * y[i];
            ordinates[i] = new UncertainOrdinate(x[i], uncertain
                ? new Triangular(0.8d * value, value, 1.2d * value)
                : new Deterministic(value));
        }

        return new TabularConsequence
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Damage",
            ConsequenceUnit = "dollars",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(ordinates,
                true, SortOrder.Ascending, false, SortOrder.None,
                uncertain ? UnivariateDistributionType.Triangular : UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Evaluates the legacy piecewise-linear daytime loss table independently.</summary>
    /// <param name="hazard">The hazard magnitude.</param>
    /// <returns>The daytime consequence.</returns>
    private static double LegacyLoss(double hazard)
    {
        double[] x = { 60d, 100d, 140d, 200d, 250d };
        double[] y = { 0d, 10d, 100d, 1000d, 1500d };
        if (hazard <= x[0]) return y[0];
        if (hazard >= x[x.Length - 1]) return y[y.Length - 1];
        int upper = Array.BinarySearch(x, hazard);
        if (upper >= 0) return y[upper];
        upper = ~upper;
        double fraction = (hazard - x[upper - 1]) / (x[upper] - x[upper - 1]);
        return y[upper - 1] + fraction * (y[upper] - y[upper - 1]);
    }

    /// <summary>Runs a mean-only engine analysis for one composite configuration.</summary>
    /// <param name="hazard">The component hazard.</param>
    /// <param name="response">The component response.</param>
    /// <param name="consequence">The failure consequence.</param>
    /// <returns>The unconditional Total-stream mean.</returns>
    private static double RunMean(IHazardFunction hazard, IResponseFunction response,
        IConsequenceFunction consequence)
    {
        var component = new SystemComponent { Name = "Legacy composite fixture", HazardFunction = hazard };
        component.AddFailureMode(new FailureMode(null, null, response, consequence));
        var analysis = new RiskAnalysis(new[] { component });
        analysis.Options.RiskMeasures = RiskMeasureOptions.None;
        analysis.RunAsync().GetAwaiter().GetResult();
        return analysis.MeanRiskResults!.Curves.Total.Mean;
    }

    /// <summary>Computes an independent fixed-quadrature unconditional mean over hazard probability.</summary>
    /// <param name="hazard">The Numerics hazard distribution.</param>
    /// <param name="failureProbability">The conditional response probability.</param>
    /// <param name="consequence">The conditional consequence.</param>
    /// <returns>The unconditional annual mean.</returns>
    private static double OracleMean(UnivariateDistributionBase hazard,
        Func<double, double> failureProbability, Func<double, double> consequence)
    {
        double Integrand(double probability)
        {
            double h = hazard.InverseCDF(probability);
            return failureProbability(h) * consequence(h);
        }

        const int intervals = 256;
        double sum = 0d;
        double compensation = 0d;
        for (int i = 0; i < intervals; i++)
        {
            double term = Integration.GaussLegendre20(Integrand,
                i / (double)intervals, (i + 1d) / intervals);
            double adjusted = term - compensation;
            double next = sum + adjusted;
            compensation = (next - sum) - adjusted;
            sum = next;
        }
        return sum;
    }

    /// <summary>
    /// Verifies the day/night composite consequence behind the complete engine against the legacy
    /// configuration's independent quadrature mean.
    /// </summary>
    [TestMethod]
    public void Test_CompositeConsequence_EngineVsIndependentQuadrature()
    {
        var hazardDistribution = new LnNormal(85d, 20d);
        var capacity = new Normal(140d, 30d);
        var consequence = new CompositeConsequence(new[]
        {
            new WeightedConsequenceFunction(Loss("Day", 1d), DayWeight),
            new WeightedConsequenceFunction(Loss("Night", 0.5d), 1d - DayWeight),
        })
        {
            Name = "Day or night",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Damage",
            ConsequenceUnit = "dollars",
            CompositeFunctionType = CompositeFunctionType.Mixture,
        };

        double engine = RunMean(Hazard("Hazard", hazardDistribution), Response("Fragility", capacity), consequence);
        double expected = OracleMean(hazardDistribution, capacity.CDF,
            h => (DayWeight + 0.5d * (1d - DayWeight)) * LegacyLoss(h));

        Assert.AreEqual(expected, engine, Math.Abs(expected) * 2e-4);
    }

    /// <summary>
    /// Verifies the uncertain day/night consequence completes through the full ensemble engine,
    /// remains reproducible, and is centered on the deterministic symmetric-triangular mean.
    /// </summary>
    [TestMethod]
    public void Test_CompositeConsequenceBootstrap_FullEngineReproducibleAndCentered()
    {
        RiskAnalysis Build()
        {
            var consequence = new CompositeConsequence(new[]
            {
                new WeightedConsequenceFunction(Loss("Day", 1d, uncertain: true), DayWeight),
                new WeightedConsequenceFunction(Loss("Night", 0.5d, uncertain: true), 1d - DayWeight),
            })
            {
                Name = "Uncertain day or night",
                SpecifiedHazard = "Stage",
                HazardUnit = "ft",
                SpecifiedConsequence = "Damage",
                ConsequenceUnit = "dollars",
                CompositeFunctionType = CompositeFunctionType.Mixture,
            };
            var component = new SystemComponent
            {
                Name = "Uncertain composite fixture",
                HazardFunction = Hazard("Hazard", new LnNormal(85d, 20d)),
            };
            component.AddFailureMode(new FailureMode(null, null,
                Response("Fragility", new Normal(140d, 30d)), consequence));
            var analysis = new RiskAnalysis(new[] { component });
            analysis.Options.EstimateMeanRiskOnly = false;
            analysis.Options.Realizations = 100;
            analysis.Options.RiskMeasures = RiskMeasureOptions.None;
            return analysis;
        }

        var first = Build();
        var second = Build();
        first.RunAsync().GetAwaiter().GetResult();
        second.RunAsync().GetAwaiter().GetResult();

        double expected = OracleMean(new LnNormal(85d, 20d), new Normal(140d, 30d).CDF,
            h => (DayWeight + 0.5d * (1d - DayWeight)) * LegacyLoss(h));
        Assert.AreEqual(expected, first.MeanRiskResults!.Curves.Total.Mean, Math.Abs(expected) * 0.03d);
        Assert.AreEqual(first.RiskResults!.ToJson(), second.RiskResults!.ToJson());
    }

    /// <summary>
    /// Verifies the legacy two-child composite hazard behind the engine against the weighted
    /// independent quadrature of its two hazard scenarios.
    /// </summary>
    [TestMethod]
    public void Test_CompositeHazard_EngineVsIndependentQuadrature()
    {
        var firstDistribution = new LnNormal(85d, 5d);
        var secondDistribution = new LnNormal(65d, 20d);
        var composite = new CompositeHazard(new[]
        {
            new WeightedHazardFunction(Hazard("Hazard 1", firstDistribution), DayWeight),
            new WeightedHazardFunction(Hazard("Hazard 2", secondDistribution), 1d - DayWeight),
        })
        {
            Name = "Composite hazard",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            CompositeCombinationType = CompositeCombinationType.Mixture,
        };
        var capacity = new Normal(140d, 30d);

        double engine = RunMean(composite, Response("Fragility", capacity), Loss("Loss", 1d));
        double expected = DayWeight * OracleMean(firstDistribution, capacity.CDF, LegacyLoss)
            + (1d - DayWeight) * OracleMean(secondDistribution, capacity.CDF, LegacyLoss);

        Assert.AreEqual(expected, engine, Math.Abs(expected) * 2e-3);
    }

    /// <summary>
    /// Verifies the legacy two-child composite response behind the engine against the weighted
    /// conditional-failure quadrature.
    /// </summary>
    [TestMethod]
    public void Test_CompositeResponse_EngineVsIndependentQuadrature()
    {
        var hazardDistribution = new LnNormal(85d, 20d);
        var firstCapacity = new Normal(140d, 30d);
        var secondCapacity = new Normal(160d, 10d);
        var response = new CompositeResponse(new[]
        {
            new WeightedResponseFunction(Response("Response 1", firstCapacity), DayWeight),
            new WeightedResponseFunction(Response("Response 2", secondCapacity), 1d - DayWeight),
        })
        {
            Name = "Composite response",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            CompositeCombinationType = CompositeCombinationType.Mixture,
        };

        double engine = RunMean(Hazard("Hazard", hazardDistribution), response, Loss("Loss", 1d));
        double expected = OracleMean(hazardDistribution,
            h => DayWeight * firstCapacity.CDF(h) + (1d - DayWeight) * secondCapacity.CDF(h),
            LegacyLoss);

        Assert.AreEqual(expected, engine, Math.Abs(expected) * 2e-4);
    }

    /// <summary>
    /// Verifies mixture selection and direct mixture-distribution integration are equivalent
    /// behind the engine, the identity exercised by the legacy RiskAnalysis composite workbench.
    /// </summary>
    [TestMethod]
    public void Test_CompositeHazard_MixtureIdentityBehindEngine()
    {
        var firstDistribution = new LnNormal(85d, 5d);
        var secondDistribution = new LnNormal(65d, 20d);
        var composite = new CompositeHazard(new[]
        {
            new WeightedHazardFunction(Hazard("Hazard 1", firstDistribution), DayWeight),
            new WeightedHazardFunction(Hazard("Hazard 2", secondDistribution), 1d - DayWeight),
        })
        {
            Name = "Composite hazard",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
        var capacity = new Normal(140d, 30d);
        double compositeMean = RunMean(composite, Response("Fragility", capacity), Loss("Loss", 1d));
        double componentMixture = DayWeight * RunMean(Hazard("Hazard 1", firstDistribution),
                Response("Fragility", capacity), Loss("Loss", 1d))
            + (1d - DayWeight) * RunMean(Hazard("Hazard 2", secondDistribution),
                Response("Fragility", capacity), Loss("Loss", 1d));

        Assert.AreEqual(componentMixture, compositeMean, Math.Abs(compositeMean) * 2e-3);
    }
}
