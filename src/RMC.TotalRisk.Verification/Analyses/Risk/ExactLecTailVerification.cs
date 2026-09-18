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
/// The exact-LEC / mixture-exposure tail oracle — the first Monte-Carlo-parity family of the
/// means-versus-tails policy (docs/verification.md): the engine's loss-exceedance tail measures (standard
/// deviation, exceedance ordinates, value-at-risk, conditional value-at-risk) for a day/night
/// mixture consequence are verified against a NEW brute-force Monte Carlo oracle that draws the
/// full model per realization, never against the v1.0 engine — whose 200-bin midpoint histogram
/// and mixture-mean flattening are exactly the defects the v1.1 exact construction and
/// exposure-branch enumeration correct.
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// <para>
/// <b>Scenario</b> (deterministic inputs; the mean-only quadrature is exact up to integration
/// error, so every discrepancy against the oracle is Monte Carlo error): hazard
/// H ~ Normal(100, 20); fragility P[F|h] = Φ((h − 140)/30); day/night mixture consequence with
/// day weight 0.55 at C_day(h) = 0.5·h and night weight 0.45 at C_night(h) = 2·h (linear tables
/// to stage 300); no non-failure mode. The unconditional annual loss is
/// L = 1{failed} · C_branch(H) — the engine's defective Fail curve with its implicit zero atom
/// describes exactly this variable.
/// </para>
/// <para>
/// <b>Oracle:</b> one million realizations over three independent MersenneTwister streams —
/// hazard 12345, failure Bernoulli 45678, exposure branch 78910 (the legacy seed family).
/// <b>Tolerance derivations</b> (k = 4 throughout, docs/verification.md): mean —
/// SE = σ̂/√N; standard deviation — delta method SE(σ̂) = √(m̂₄ − σ̂⁴)/(2σ̂√N) with m̂₄ the
/// central fourth moment; exceedance ordinates — binomial SE = √(p̂(1 − p̂)/N); value-at-risk —
/// quantile SE = √(α(1 − α)/N)/f̂(q̂) with the density estimated from the empirical quantile
/// slope over ±0.1% exceedance (plus a 0.1% relative floor absorbing the engine curve's output
/// resolution); conditional value-at-risk — tail-mean SE = σ̂_tail/√(αN).
/// </para>
/// </remarks>
[TestClass]
public class ExactLecTailVerification
{
    /// <summary>The oracle realization count.</summary>
    private const int OracleRealizations = 1_000_000;

    /// <summary>The tolerance multiplier on each Monte Carlo standard error.</summary>
    private const double K = 4d;

    /// <summary>The exceedance level for the value-at-risk and conditional value-at-risk pins.</summary>
    private const double Alpha = 0.01d;

    /// <summary>The day exposure weight (night carries the remainder).</summary>
    private const double DayWeight = 0.55d;

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

    /// <summary>Builds a linear consequence table to stage 300 with the given full-scale value.</summary>
    private static TabularConsequence Consequence(string name, double valueAtThreeHundred)
    {
        return new TabularConsequence
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Life Loss",
            ConsequenceUnit = "lives",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(0d, new Deterministic(0d)), new UncertainOrdinate(300d, new Deterministic(valueAtThreeHundred)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds the engine analysis for the day/night scenario.</summary>
    private static RiskAnalysis BuildAnalysis()
    {
        var mixture = new CompositeConsequence(new[]
        {
            new WeightedConsequenceFunction(Consequence("Day", 150d), DayWeight),
            new WeightedConsequenceFunction(Consequence("Night", 600d), 1d - DayWeight),
        })
        {
            Name = "Day/Night Life Loss",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Life Loss",
            ConsequenceUnit = "lives",
        };
        var component = new SystemComponent { Name = "Dam" };
        var (hazardProbabilities, hazardStages) = HazardTable();
        var hazardOrdinates = new UncertainOrdinate[hazardStages.Length];
        for (int i = 0; i < hazardStages.Length; i++)
        {
            hazardOrdinates[i] = new UncertainOrdinate(1d - hazardProbabilities[i], new Deterministic(hazardStages[i]));
        }
        component.HazardFunction = new TabularHazard
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
        component.AddFailureMode(new FailureMode(null, null, fragility, mixture));

        var analysis = new RiskAnalysis(new[] { component }) { Name = "Tail Oracle" };
        analysis.Options.ConsequenceThreshold = 100d;
        analysis.Options.Alpha = Alpha;
        return analysis;
    }

    /// <summary>
    /// The brute-force oracle: draws the full model per realization and returns the sorted
    /// unconditional annual losses (ascending).
    /// </summary>
    /// <returns>The sorted losses.</returns>
    private static double[] RunOracle()
    {
        var hazardStream = new MersenneTwister(12345);
        var failureStream = new MersenneTwister(45678);
        var exposureStream = new MersenneTwister(78910);
        var (hazardProbabilities, hazardStages) = HazardTable();
        var (fragilityStages, fragilityProbabilities) = FragilityTable();

        var losses = new double[OracleRealizations];
        for (int i = 0; i < OracleRealizations; i++)
        {
            double hazard = Interpolate(hazardProbabilities, hazardStages, hazardStream.NextDouble());
            double failureProbability = Math.Max(0d, Math.Min(1d, Interpolate(fragilityStages, fragilityProbabilities, hazard)));
            bool failed = failureStream.NextDouble() < failureProbability;
            bool day = exposureStream.NextDouble() < DayWeight;
            double clamped = Math.Max(0d, Math.Min(300d, hazard));
            losses[i] = failed ? (day ? 0.5d * clamped : 2d * clamped) : 0d;
        }
        Array.Sort(losses);
        return losses;
    }

    /// <summary>
    /// The tail pins: mean (the v1.0-parity gate), standard deviation, two exceedance
    /// ordinates, value-at-risk, and conditional value-at-risk — each within its documented
    /// k·SE of the brute-force oracle. These are the measures the v1.0 histogram and mixture
    /// flattening got wrong; they are Monte-Carlo-parity by the means-versus-tails policy.
    /// </summary>
    [TestMethod]
    public void Test_MixtureTail_VsBruteForceOracle()
    {
        // Arrange — the oracle's empirical loss distribution.
        double[] losses = RunOracle();
        int count = losses.Length;

        double mean = 0d;
        for (int i = 0; i < count; i++) mean += losses[i];
        mean /= count;
        double m2 = 0d, m4 = 0d;
        for (int i = 0; i < count; i++)
        {
            double delta = losses[i] - mean;
            double delta2 = delta * delta;
            m2 += delta2;
            m4 += delta2 * delta2;
        }
        m2 /= count;
        m4 /= count;
        double sigma = Math.Sqrt(m2);
        double meanSe = sigma / Math.Sqrt(count);
        double sigmaSe = Math.Sqrt(Math.Max(0d, m4 - m2 * m2)) / (2d * sigma * Math.Sqrt(count));

        double ExceedanceAt(double level)
        {
            int exceeding = 0;
            for (int i = count - 1; i >= 0 && losses[i] > level; i--) exceeding++;
            return exceeding / (double)count;
        }

        // The empirical α-quantile and its density-scaled standard error.
        double valueAtRisk = losses[(int)Math.Round((1d - Alpha) * count)];
        double deltaAlpha = 0.001d;
        double quantileLow = losses[(int)Math.Round((1d - Alpha - deltaAlpha) * count)];
        double quantileHigh = losses[(int)Math.Round((1d - Alpha + deltaAlpha) * count)];
        double density = 2d * deltaAlpha / Math.Max(1e-12, quantileHigh - quantileLow);
        double varSe = Math.Sqrt(Alpha * (1d - Alpha) / count) / density;

        // The tail mean above the α-quantile.
        int tailCount = (int)Math.Round(Alpha * count);
        double tailMean = 0d, tailM2 = 0d;
        for (int i = count - tailCount; i < count; i++) tailMean += losses[i];
        tailMean /= tailCount;
        for (int i = count - tailCount; i < count; i++)
        {
            double delta = losses[i] - tailMean;
            tailM2 += delta * delta;
        }
        double cvarSe = Math.Sqrt(tailM2 / tailCount) / Math.Sqrt(tailCount);

        // Act — the engine's mean-only pass (exact quadrature on the same model).
        var analysis = BuildAnalysis();
        analysis.RunAsync().GetAwaiter().GetResult();
        Assert.IsTrue(analysis.IsEstimated);
        var fail = analysis.MeanRiskResults!.Curves.Fail;

        // Assert — mean parity (the free gate) and the Monte-Carlo-parity tail catalog.
        Assert.AreEqual(mean, fail.Mean, K * meanSe, "Unconditional mean annual loss.");
        Assert.AreEqual(sigma, fail.StandardDeviation, K * sigmaSe,
            "Loss standard deviation — the measure the v1.0 raw power sums and mixture flattening destroyed.");
        Assert.AreEqual(ExceedanceAt(100d), fail.LEC.GetYFromX(100d, Transform.Logarithmic, Transform.Logarithmic),
            K * Math.Sqrt(ExceedanceAt(100d) * (1d - ExceedanceAt(100d)) / count),
            "Exceedance at consequence 100.");
        Assert.AreEqual(ExceedanceAt(300d), fail.LEC.GetYFromX(300d, Transform.Logarithmic, Transform.Logarithmic),
            K * Math.Sqrt(ExceedanceAt(300d) * (1d - ExceedanceAt(300d)) / count),
            "Exceedance at consequence 300 — beyond the day branch's reach; only branch enumeration can populate it.");
        Assert.AreEqual(valueAtRisk, fail.ValueAtRisk, Math.Max(K * varSe, 1e-3 * valueAtRisk),
            "Value-at-risk at α = 0.01 (0.1% relative floor absorbs the output-resolution interpolation).");
        Assert.AreEqual(tailMean, fail.ConditionalValueAtRisk, Math.Max(K * cvarSe, 1e-3 * tailMean),
            "Conditional value-at-risk at α = 0.01.");
    }

    /// <summary>
    /// The counter-pin against the flattened composite: the same model with the mixture
    /// collapsed to its weighted-average curve reproduces the mean but destroys the night
    /// branch's tail — its exceedance at consequence 300 is zero. This is the v1.0 defect made
    /// visible.
    /// </summary>
    [TestMethod]
    public void Test_FlattenedComposite_DestroysTail()
    {
        // Arrange — identical children, Average combine mode.
        var analysis = BuildAnalysis();
        var mixtureFail = RunAndReturnFail(analysis);
        var flattened = BuildAnalysis();
        var component = (SystemComponent)flattened.Components[0];
        foreach (var function in component.GetReferencedFunctions())
        {
            if (function is CompositeConsequence composite)
            {
                composite.CompositeFunctionType = RMC.TotalRisk.Core.Enums.CompositeFunctionType.Average;
            }
        }
        var flattenedFail = RunAndReturnFail(flattened);

        // Assert — means agree tightly; the flattened tail beyond the average curve's reach is empty.
        Assert.AreEqual(flattenedFail.Mean, mixtureFail.Mean, 1e-6 * flattenedFail.Mean,
            "The mixture identity keeps the mean unchanged.");
        double mixtureTail = mixtureFail.LEC.GetYFromX(300d, Transform.Logarithmic, Transform.Logarithmic);
        double flattenedTail = flattenedFail.LEC.GetYFromX(300d, Transform.Logarithmic, Transform.Logarithmic);
        Assert.IsTrue(mixtureTail > 1e-6, $"The enumerated night branch populates the deep tail ({mixtureTail}).");
        Assert.IsTrue(flattenedTail < mixtureTail / 10d,
            $"The flattened curve starves the deep tail ({flattenedTail} vs {mixtureTail}).");
    }

    /// <summary>Runs an analysis and returns its mean Fail curve.</summary>
    /// <param name="analysis">The analysis to run.</param>
    /// <returns>The mean-only Fail curve.</returns>
    private static RMC.TotalRisk.Results.Curve RunAndReturnFail(RiskAnalysis analysis)
    {
        analysis.RunAsync().GetAwaiter().GetResult();
        Assert.IsTrue(analysis.IsEstimated);
        return analysis.MeanRiskResults!.Curves.Fail;
    }
}
