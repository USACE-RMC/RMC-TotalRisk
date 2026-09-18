using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using Numerics.Sampling;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.Systems.Components;

namespace RMC.TotalRisk.Verification.Analyses.Risk;

/// <summary>
/// Single-component mean parity — the free regression gate of the means-versus-tails
/// policy (docs/verification.md): the engine's five summary means and the annualized failure probability against an
/// independent legacy-style Monte Carlo oracle that never touches the engine.
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// <para>
/// <b>Scenario</b> (deterministic tabular inputs, so the mean-only quadrature is the exact
/// answer up to integration error, and both sides share exactly the same piecewise-linear
/// model): the stage-frequency hazard is tabulated from Normal(100, 20) quantiles on a dense
/// z-grid with linear probability interpolation; the fragility is tabulated from
/// Φ((h − 140)/30) on the matching grid with linear interpolation; the failure consequence is
/// linear from (60 → 0) to (200 → 1000) and the non-failure consequence linear from (60 → 0)
/// to (200 → 100), both clamped at their ends.
/// </para>
/// <para>
/// <b>Oracle:</b> one million hazard draws through <c>MersenneTwister(12345)</c> (the legacy
/// seed) inverse-transform sampling of the SAME tabulated hazard via the oracle's own linear
/// interpolator, accumulating per-draw <c>pF·C_F</c>, <c>(1 − pF)·C_NF</c>, their sum,
/// <c>pF·max(0, C_F − C_NF)</c>, <c>C_NF</c>, and <c>pF</c>. <b>Tolerances</b> are k·SE with
/// k = 4 and SE = σ̂/√N computed in-run per output (σ̂ the sample standard deviation of the
/// per-draw summand; at N = 10⁶ roughly 0.1% of σ̂): the engine side is quadrature at relative
/// tolerance 1e-8, so the oracle's own Monte Carlo error dominates and 4·SE keeps the
/// false-failure probability below 1e-4 per assert (docs/verification.md).
/// </para>
/// </remarks>
[TestClass]
public class SingleComponentMeanParityVerification
{
    /// <summary>The oracle realization count (legacy 10M dropped 10× per the conversion policy).</summary>
    private const int OracleRealizations = 1_000_000;

    /// <summary>The legacy fixed oracle seed.</summary>
    private const int OracleSeed = 12345;

    /// <summary>The tolerance multiplier on the Monte Carlo standard error.</summary>
    private const double K = 4d;

    /// <summary>The shared z-grid step of the tabulated curves.</summary>
    private const double ZStep = 0.25d;

    /// <summary>The shared z-grid half-range of the tabulated curves.</summary>
    private const double ZRange = 8d;

    /// <summary>Builds the hazard table: non-exceedance probabilities (ascending) and stages from Normal(100, 20).</summary>
    private static (double[] Probabilities, double[] Stages) HazardTable()
    {
        int count = (int)Math.Round(2d * ZRange / ZStep) + 1;
        var probabilities = new double[count];
        var stages = new double[count];
        for (int i = 0; i < count; i++)
        {
            double z = -ZRange + i * ZStep;
            probabilities[i] = Normal.StandardCDF(z);
            stages[i] = 100d + 20d * z;
        }
        return (probabilities, stages);
    }

    /// <summary>Builds the fragility table: stages and failure probabilities from Φ((h − 140)/30).</summary>
    private static (double[] Stages, double[] Probabilities) FragilityTable()
    {
        int count = (int)Math.Round(2d * ZRange / ZStep) + 1;
        var stages = new double[count];
        var probabilities = new double[count];
        for (int i = 0; i < count; i++)
        {
            double z = -ZRange + i * ZStep;
            stages[i] = 140d + 30d * z;
            probabilities[i] = Normal.StandardCDF(z);
        }
        return (stages, probabilities);
    }

    /// <summary>The oracle's own linear interpolator with end clamping (x ascending).</summary>
    private static double Interpolate(double[] xValues, double[] yValues, double x)
    {
        if (x <= xValues[0]) return yValues[0];
        if (x >= xValues[xValues.Length - 1]) return yValues[yValues.Length - 1];
        int index = Array.BinarySearch(xValues, x);
        if (index >= 0) return yValues[index];
        index = ~index;
        double fraction = (x - xValues[index - 1]) / (xValues[index] - xValues[index - 1]);
        return yValues[index - 1] + fraction * (yValues[index] - yValues[index - 1]);
    }

    /// <summary>Evaluates the failure consequence curve of the scenario.</summary>
    private static double FailureConsequence(double hazard)
    {
        if (hazard <= 60d) return 0d;
        if (hazard >= 200d) return 1000d;
        return (hazard - 60d) / 140d * 1000d;
    }

    /// <summary>Evaluates the non-failure consequence curve of the scenario.</summary>
    private static double NonFailureConsequence(double hazard)
    {
        if (hazard <= 60d) return 0d;
        if (hazard >= 200d) return 100d;
        return (hazard - 60d) / 140d * 100d;
    }

    /// <summary>Builds the engine analysis for the scenario from the shared tables.</summary>
    private static RiskAnalysis BuildAnalysis()
    {
        var (hazardProbabilities, hazardStages) = HazardTable();
        var hazardOrdinates = new UncertainOrdinate[hazardStages.Length];
        for (int i = 0; i < hazardStages.Length; i++)
        {
            // The table stores exceedance descending as stage ascends.
            hazardOrdinates[i] = new UncertainOrdinate(1d - hazardProbabilities[i], new Deterministic(hazardStages[i]));
        }
        var hazard = new TabularHazard
        {
            Name = "Stage Frequency",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            ProbabilityTransform = Transform.None,
            NoUncertaintyFunction = new UncertainOrderedPairedData(hazardOrdinates,
                true, SortOrder.Descending, true, SortOrder.Ascending, UnivariateDistributionType.Deterministic),
        };

        var (fragilityStages, fragilityProbabilities) = FragilityTable();
        var fragilityOrdinates = new UncertainOrdinate[fragilityStages.Length];
        for (int i = 0; i < fragilityStages.Length; i++)
        {
            fragilityOrdinates[i] = new UncertainOrdinate(fragilityStages[i], new Deterministic(fragilityProbabilities[i]));
        }
        var fragility = new TabularResponse
        {
            Name = "Breach Fragility",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(fragilityOrdinates,
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };

        var failure = new TabularConsequence
        {
            Name = "Failure Loss",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Life Loss",
            ConsequenceUnit = "lives",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(60d, new Deterministic(0d)), new UncertainOrdinate(200d, new Deterministic(1000d)) },
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
                new[] { new UncertainOrdinate(60d, new Deterministic(0d)), new UncertainOrdinate(200d, new Deterministic(100d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };

        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = hazard;
        component.AddFailureMode(new FailureMode(null, null, fragility, failure));
        component.AddFailureMode(new FailureMode(null, null, null, nonFailure));
        return new RiskAnalysis(new[] { component }) { Name = "Mean Parity" };
    }

    /// <summary>
    /// The oracle: per-draw accumulation of the six outputs with their in-run standard errors,
    /// over the oracle's own interpolation of the shared tables.
    /// </summary>
    /// <returns>Per output: the mean and its Monte Carlo standard error.</returns>
    private static (double Mean, double Se)[] RunOracle()
    {
        var prng = new MersenneTwister(OracleSeed);
        var (hazardProbabilities, hazardStages) = HazardTable();
        var (fragilityStages, fragilityProbabilities) = FragilityTable();

        // Outputs: fail, non-fail, total, excess, background, failure probability.
        var sums = new double[6];
        var sumsOfSquares = new double[6];
        var draw = new double[6];
        for (int i = 0; i < OracleRealizations; i++)
        {
            double hazard = Interpolate(hazardProbabilities, hazardStages, prng.NextDouble());
            double pF = Math.Max(0d, Math.Min(1d, Interpolate(fragilityStages, fragilityProbabilities, hazard)));
            double cF = FailureConsequence(hazard);
            double cNF = NonFailureConsequence(hazard);

            draw[0] = pF * cF;
            draw[1] = (1d - pF) * cNF;
            draw[2] = draw[0] + draw[1];
            draw[3] = pF * Math.Max(0d, cF - cNF);
            draw[4] = cNF;
            draw[5] = pF;
            for (int k = 0; k < 6; k++)
            {
                sums[k] += draw[k];
                sumsOfSquares[k] += draw[k] * draw[k];
            }
        }

        var results = new (double Mean, double Se)[6];
        for (int k = 0; k < 6; k++)
        {
            double mean = sums[k] / OracleRealizations;
            double variance = Math.Max(0d, sumsOfSquares[k] / OracleRealizations - mean * mean);
            results[k] = (mean, Math.Sqrt(variance / OracleRealizations));
        }
        return results;
    }

    /// <summary>
    /// The mean-parity gate: the engine's mean-only summary means (and the annualized failure
    /// probability) match the independent oracle within 4·SE per output.
    /// </summary>
    [TestMethod]
    public void Test_MeanOnly_FiveMeans_VsOracle()
    {
        // Arrange / Act
        var oracle = RunOracle();
        var analysis = BuildAnalysis();
        analysis.RunAsync().GetAwaiter().GetResult();
        Assert.IsTrue(analysis.IsEstimated);
        var summary = analysis.RiskResults![0]!;

        // Assert — each output within k·SE of the oracle.
        Assert.AreEqual(oracle[0].Mean, summary.Fail.Mean, K * oracle[0].Se, "Failure risk mean.");
        Assert.AreEqual(oracle[1].Mean, summary.NonFail.Mean, K * oracle[1].Se, "Non-failure risk mean.");
        Assert.AreEqual(oracle[2].Mean, summary.Total.Mean, K * oracle[2].Se, "Total risk mean.");
        Assert.AreEqual(oracle[3].Mean, summary.Excess.Mean, K * oracle[3].Se, "Incremental (excess) risk mean.");
        Assert.AreEqual(oracle[4].Mean, summary.Background.Mean, K * oracle[4].Se, "Background risk mean.");
        Assert.AreEqual(oracle[5].Mean, summary.Fail.TotalProbability, K * oracle[5].Se, "Annualized failure probability.");
    }

    /// <summary>
    /// The full-uncertainty path on the same deterministic scenario reproduces the mean-only
    /// means exactly (every realization is identical), proving both paths share one compute
    /// kernel.
    /// </summary>
    [TestMethod]
    public void Test_FullUncertainty_Deterministic_MatchesMeanOnly()
    {
        // Arrange — the ensemble discipline is pinned to the mean pass's: this test
        // proves both paths share one compute kernel, so the engine's relaxed ensemble default
        // (a deliberate accuracy split) is set aside for the comparison.
        var meanOnly = BuildAnalysis();
        meanOnly.RunAsync().GetAwaiter().GetResult();
        var full = BuildAnalysis();
        full.Options.EstimateMeanRiskOnly = false;
        full.Options.Realizations = 100;
        full.Options.UseDefaults = false;
        full.Options.EnsembleTolerance = 1e-8;
        full.Options.EnsembleMinDepth = 2;

        // Act
        full.RunAsync().GetAwaiter().GetResult();

        // Assert — the ensemble is degenerate at the mean-only answer.
        double meanOnlyTotal = meanOnly.RiskResults![0]!.Total.Mean;
        for (int i = 0; i < full.RiskResults!.Count; i++)
        {
            Assert.AreEqual(meanOnlyTotal, full.RiskResults[i]!.Total.Mean, 1e-12 * meanOnlyTotal,
                $"Realization {i} diverged from the mean-only answer on a deterministic model.");
        }
    }
}
