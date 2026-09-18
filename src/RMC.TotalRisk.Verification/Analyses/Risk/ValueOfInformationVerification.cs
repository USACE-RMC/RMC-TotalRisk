using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using Numerics.Sampling;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.Systems.Components;
using RMC.TotalRisk.Core.Support;

namespace RMC.TotalRisk.Verification.Analyses.Risk;

/// <summary>
/// Value-of-information verification: the given-data conditioning estimator against the
/// linear-Gaussian closed form (where the binned main effect and the probit confidence
/// movement are exactly computable), and the full engine query against an independent
/// re-implementation of the documented estimator convention over the publicly stored
/// per-realization measures, unweighted and weighted.
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// <para>
/// <b>Oracle independence:</b> the linear-Gaussian scenarios drive the estimator with a
/// synthetic knowledge map y = Σ aᵢ·Φ⁻¹(uᵢ) whose conditional structure is analytic: over
/// equal-probability bins of one input, the exact binned main effect is
/// aᵢ²·Σ (1/B)·m_b² with m_b = B·(φ(z_(b−1)) − φ(z_b)) the truncated standard-normal bin
/// means, and the exact per-bin exceedance probabilities follow from the probit form
/// P(y &gt; c | uᵢ) = Φ((aᵢ·Φ⁻¹(uᵢ) − c)/σᵣ), integrated over each bin by the oracle's own
/// Simpson rule. The engine scenarios re-implement the documented estimator convention —
/// pairwise NaN filtering, the stable input sort with the realization-index tie break, the
/// equal-weight partition closed at each cumulative target, weighted bin means, and the
/// weighted strict-exceedance fractions — in oracle-local code over measures read from the
/// public per-realization summaries, never through the library's estimator or its measure
/// selectors. The knowledge columns themselves are the engine's recorded percentile draws:
/// content-seeded stream reproduction is pinned by the seed-scribe families, so this family's
/// independence claim covers the conditioning mathematics and the measure extraction.
/// </para>
/// <para>
/// <b>Tolerances:</b> the linear-Gaussian main effect is asserted at 5e-3 relative — the
/// Latin-hypercube design aligns each column's equal-frequency bins exactly with the
/// probability bins, leaving only the other columns' conditional noise, whose bin-mean
/// standard error √(Σⱼ≠ᵢ aⱼ²)/√(n/B) enters the between-bin variance as a positive noise bias
/// of order B/n (≈ 5e-4 absolute here) plus root-n jitter. The total variance is asserted at
/// 2e-2 relative (an unstratified fourth-moment estimate at n = 200,000). The movement oracle
/// is asserted at 8e-3 absolute: each of the 20 bin fractions carries a standard error of at
/// most √(0.25/(n/B)) ≈ 7.1e-3 at n = 100,000, and the weighted average of their absolute
/// deviations concentrates by a further factor of √B. The engine-versus-reimplementation
/// asserts are rounding-bounded, not statistical — the same doubles reduced through
/// independently coded arrangements — at 1e-12 relative; the movement baselines and the
/// published tolerable-risk confidence entries must agree bit-exactly.
/// </para>
/// <para>
/// <b>Engine scenario:</b> the trivial uncertain fixture of the weighted-ensemble family —
/// stage frequency (0.999 → 0 ft, 0.5 → 10 ft, 0.001 → 30 ft), a triangular-ordinate
/// uncertain fragility rising from 10 ft to 20 ft, a triangular-ordinate uncertain failure
/// consequence ramping to 300, a deterministic non-failure consequence to 60 — at 1,000
/// realizations (50 per conditioning bin), with the guideline criterion mean Excess &gt; 1
/// configured so the movement blocks publish.
/// </para>
/// </remarks>
[TestClass]
public class ValueOfInformationVerification
{
    /// <summary>The linear-Gaussian design size of the main-effect scenario.</summary>
    private const int MainEffectCount = 200_000;

    /// <summary>The linear-Gaussian design size of the movement scenario.</summary>
    private const int MovementCount = 100_000;

    /// <summary>The fixed seed of the linear-Gaussian designs.</summary>
    private const int SyntheticSeed = 20260827;

    /// <summary>The documented conditioning bin count.</summary>
    private const int Bins = 20;

    /// <summary>The engine scenario's realization count.</summary>
    private const int EngineRealizations = 1000;

    /// <summary>The rounding tolerance between the query and the independent re-implementation.</summary>
    private const double RoundingTolerance = 1e-12;

    /// <summary>The linear map's coefficients.</summary>
    private static readonly double[] Coefficients = { 3d, 2d, 1d };

    #region Linear-Gaussian oracles

    /// <summary>The standard normal density.</summary>
    /// <param name="z">The abscissa.</param>
    /// <returns>φ(z).</returns>
    private static double Phi(double z)
    {
        return Math.Exp(-0.5d * z * z) / Math.Sqrt(2d * Math.PI);
    }

    /// <summary>
    /// The exact between-bin variance of a standard normal over equal-probability bins:
    /// Σ (1/B)·m_b² with m_b the truncated bin means B·(φ(z_(b−1)) − φ(z_b)).
    /// </summary>
    /// <param name="bins">The bin count.</param>
    /// <returns>The exact binned variance of the conditional mean.</returns>
    private static double ExactNormalBetweenVariance(int bins)
    {
        double sum = 0d;
        for (int b = 0; b < bins; b++)
        {
            double zLow = b == 0 ? double.NegativeInfinity : Normal.StandardZ((double)b / bins);
            double zHigh = b == bins - 1 ? double.PositiveInfinity : Normal.StandardZ((double)(b + 1) / bins);
            double low = double.IsInfinity(zLow) ? 0d : Phi(zLow);
            double high = double.IsInfinity(zHigh) ? 0d : Phi(zHigh);
            double mean = bins * (low - high);
            sum += mean * mean / bins;
        }
        return sum;
    }

    /// <summary>
    /// The exact expected movement of the exceedance statement for one input of the linear
    /// map at threshold zero: Σ (1/B)·|p_b − 1/2| with p_b the oracle's Simpson integral of
    /// Φ(a·Φ⁻¹(u)/σᵣ) over the bin's probability segment.
    /// </summary>
    /// <param name="coefficient">The input's coefficient.</param>
    /// <param name="residualSigma">The other inputs' combined standard deviation.</param>
    /// <param name="bins">The bin count.</param>
    /// <returns>The exact binned movement.</returns>
    private static double ExactMovement(double coefficient, double residualSigma, int bins)
    {
        var normal = new Normal();
        double movement = 0d;
        for (int b = 0; b < bins; b++)
        {
            double uLow = (double)b / bins;
            double uHigh = (double)(b + 1) / bins;
            const int nodes = 2000;
            double h = (uHigh - uLow) / nodes;
            double integral = 0d;
            for (int k = 0; k <= nodes; k++)
            {
                // Simpson weights over the open-clamped abscissas (the endpoints back off by
                // a half step to avoid the exact 0 and 1 probability bounds).
                double u = uLow + k * h;
                if (u <= 0d) u = uLow + 0.5d * h;
                if (u >= 1d) u = uHigh - 0.5d * h;
                double weight = k == 0 || k == nodes ? 1d : k % 2 == 1 ? 4d : 2d;
                integral += weight * normal.CDF(coefficient * Normal.StandardZ(u) / residualSigma);
            }
            double p = integral * h / 3d / (uHigh - uLow);
            movement += Math.Abs(p - 0.5d) / bins;
        }
        return movement;
    }

    #endregion

    /// <summary>
    /// Verifies the given-data main effect against the linear-Gaussian closed form: for
    /// y = Σ aᵢ·Φ⁻¹(uᵢ) over a Latin-hypercube design, each input's estimated resolvable
    /// variance matches aᵢ² times the exact binned normal between-variance, and the total
    /// matches Σ aᵢ².
    /// </summary>
    [TestMethod]
    public void Test_LinearGaussian_MainEffect_MatchesClosedBinnedForm()
    {
        // Arrange — the stratified design and the linear-normal map.
        var design = LatinHypercube.Random(MainEffectCount, Coefficients.Length, SyntheticSeed);
        var columns = new double[Coefficients.Length][];
        var outputs = new double[MainEffectCount];
        for (int c = 0; c < Coefficients.Length; c++) columns[c] = new double[MainEffectCount];
        for (int i = 0; i < MainEffectCount; i++)
        {
            double y = 0d;
            for (int c = 0; c < Coefficients.Length; c++)
            {
                double u = design[i, c];
                columns[c][i] = u;
                y += Coefficients[c] * Normal.StandardZ(u);
            }
            outputs[i] = y;
        }
        double exactBetween = ExactNormalBetweenVariance(Bins);
        double exactTotal = 0d;
        for (int c = 0; c < Coefficients.Length; c++) exactTotal += Coefficients[c] * Coefficients[c];

        // Act / Assert — each input against its exact binned main effect.
        for (int c = 0; c < Coefficients.Length; c++)
        {
            var effect = ValueOfInformationEstimator.MainEffect(columns[c], outputs, null, Bins);
            double exact = Coefficients[c] * Coefficients[c] * exactBetween;
            Assert.AreEqual(exact, effect.ResolvableVariance, 5e-3 * exact,
                $"Input {c}: the estimated main effect must match the exact binned form.");
            Assert.AreEqual(exactTotal, effect.TotalVariance, 2e-2 * exactTotal,
                $"Input {c}: the total variance must match Σ aᵢ².");
        }
    }

    /// <summary>
    /// Verifies the guideline-movement estimator against the probit quadrature oracle: for
    /// each input of the linear map at threshold zero, the estimated expected movement of the
    /// exceedance statement matches the exact binned probit value, and the strongest input
    /// moves the statement the most.
    /// </summary>
    [TestMethod]
    public void Test_LinearGaussian_Movement_MatchesProbitQuadrature()
    {
        // Arrange
        var design = LatinHypercube.Random(MovementCount, Coefficients.Length, SyntheticSeed + 1);
        var columns = new double[Coefficients.Length][];
        var outputs = new double[MovementCount];
        for (int c = 0; c < Coefficients.Length; c++) columns[c] = new double[MovementCount];
        for (int i = 0; i < MovementCount; i++)
        {
            double y = 0d;
            for (int c = 0; c < Coefficients.Length; c++)
            {
                double u = design[i, c];
                columns[c][i] = u;
                y += Coefficients[c] * Normal.StandardZ(u);
            }
            outputs[i] = y;
        }
        double sumSquares = 0d;
        for (int c = 0; c < Coefficients.Length; c++) sumSquares += Coefficients[c] * Coefficients[c];

        // Act / Assert
        var movements = new double[Coefficients.Length];
        for (int c = 0; c < Coefficients.Length; c++)
        {
            double residual = Math.Sqrt(sumSquares - Coefficients[c] * Coefficients[c]);
            double exact = ExactMovement(Coefficients[c], residual, Bins);
            movements[c] = ValueOfInformationEstimator.ExceedanceMovement(columns[c], outputs, null, Bins, 0d);
            Assert.AreEqual(exact, movements[c], 8e-3,
                $"Input {c}: the estimated movement must match the probit quadrature.");
        }
        Assert.IsTrue(movements[0] > movements[1] && movements[1] > movements[2],
            "Stronger inputs move the confidence statement more.");
    }

    #region Engine scenario

    /// <summary>Builds the engine fixture with the movement criterion configured.</summary>
    private static RiskAnalysis BuildEngineAnalysis()
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
        var failureLoss = new TabularConsequence
        {
            Name = "Failure Loss",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Life Loss",
            ConsequenceUnit = "lives",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(0d, new Triangular(0d, 1d, 2d)), new UncertainOrdinate(30d, new Triangular(240d, 300d, 360d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Triangular),
        };
        var nonFailureLoss = new TabularConsequence
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
        component1.AddFailureMode(new FailureMode(null, null, fragility, failureLoss));
        component1.AddFailureMode(new FailureMode(null, null, null, nonFailureLoss));
        var analysis = new RiskAnalysis(new[] { component1 });
        analysis.Options.EstimateMeanRiskOnly = false;
        analysis.Options.Realizations = EngineRealizations;
        analysis.Options.TolerableRiskCriteria.Add(new TolerableRiskCriterion(RiskMeasure.Mean, RiskType.Excess, 0, 1d));
        return analysis;
    }

    /// <summary>
    /// Reads the per-realization system-scope measure from the public summaries.
    /// </summary>
    /// <param name="analysis">The estimated analysis.</param>
    /// <param name="riskType">The stream to read.</param>
    /// <returns>The measure sample, NaN for missing realizations.</returns>
    private static double[] PublicMeasures(RiskAnalysis analysis, RiskType riskType)
    {
        var results = analysis.RiskResults!;
        var outputs = new double[results.Count];
        for (int i = 0; i < results.Count; i++)
        {
            var summary = results[i];
            if (summary == null) { outputs[i] = double.NaN; continue; }
            var stream = riskType switch
            {
                RiskType.Excess => summary.Excess,
                RiskType.Background => summary.Background,
                RiskType.Total => summary.Total,
                RiskType.Fail => summary.Fail,
                _ => summary.NonFail,
            };
            outputs[i] = stream == null ? double.NaN : stream.Mean;
        }
        return outputs;
    }

    /// <summary>
    /// Re-derives the engine's knowledge columns from the component walk (the recorded
    /// percentile streams the library also reads; stream reproduction is pinned elsewhere).
    /// </summary>
    /// <param name="analysis">The estimated analysis.</param>
    /// <returns>The labeled columns materialized per realization.</returns>
    private static List<(string Label, string Group, double[] Column)> WalkColumns(RiskAnalysis analysis)
    {
        var components = analysis.Components;
        var componentList = new List<SystemComponent>(components);
        SystemComponent.AssignOccurrenceIndices(componentList);
        var inputs = new List<SensitivityInput>();
        for (int i = 0; i < componentList.Count; i++)
        {
            int seed = SeedHelpers.HashCombine(analysis.Options.PRNGSeed, componentList[i].CanonicalHash(), componentList[i].OccurrenceIndex);
            componentList[i].SetupSamplers(EngineRealizations, seed, analysis.Options.SamplingScheme);
            componentList[i].CollectSensitivityInputs(inputs);
        }
        var columns = new List<(string, string, double[])>(inputs.Count);
        for (int c = 0; c < inputs.Count; c++)
        {
            var column = new double[EngineRealizations];
            for (int i = 0; i < EngineRealizations; i++) column[i] = inputs[c].Read(i);
            columns.Add((inputs[c].Label, inputs[c].GroupLabel, column));
        }
        return columns;
    }

    /// <summary>
    /// The oracle's own main-effect re-implementation of the documented convention: pairwise
    /// NaN filtering, the stable input sort with the index tie break, and the equal-weight
    /// partition closed at each cumulative target.
    /// </summary>
    /// <param name="inputs">The input column.</param>
    /// <param name="outputs">The output sample.</param>
    /// <param name="weights">The weights, or null.</param>
    /// <returns>The between-bin variance and the total variance.</returns>
    private static (double Between, double Total) OracleMainEffect(double[] inputs, double[] outputs, double[]? weights)
    {
        var order = OracleOrder(inputs, outputs);
        double totalWeight = 0d, mean = 0d;
        foreach (int i in order)
        {
            double w = weights == null ? 1d : weights[i];
            totalWeight += w;
            mean += w * outputs[i];
        }
        mean /= totalWeight;
        double total = 0d;
        foreach (int i in order)
        {
            double w = weights == null ? 1d : weights[i];
            total += w * (outputs[i] - mean) * (outputs[i] - mean);
        }
        total /= totalWeight;

        double between = 0d;
        int position = 0;
        double consumed = 0d;
        for (int b = 0; b < Bins && position < order.Count; b++)
        {
            double target = totalWeight * (b + 1) / Bins;
            double binWeight = 0d, binMean = 0d;
            while (position < order.Count && (binWeight <= 0d || consumed + binWeight < target || b == Bins - 1))
            {
                int i = order[position];
                double w = weights == null ? 1d : weights[i];
                binWeight += w;
                binMean += w * outputs[i];
                position++;
            }
            consumed += binWeight;
            if (binWeight > 0d)
            {
                binMean /= binWeight;
                between += binWeight * (binMean - mean) * (binMean - mean);
            }
        }
        return (between / totalWeight, total);
    }

    /// <summary>
    /// The oracle's own movement re-implementation: the weighted mean absolute deviation of
    /// the per-bin strict-exceedance fractions from the overall fraction.
    /// </summary>
    /// <param name="inputs">The input column.</param>
    /// <param name="outputs">The criterion measure sample.</param>
    /// <param name="weights">The weights, or null.</param>
    /// <param name="threshold">The criterion threshold.</param>
    /// <returns>The movement and the baseline fraction.</returns>
    private static (double Movement, double Baseline) OracleMovement(double[] inputs, double[] outputs, double[]? weights, double threshold)
    {
        var order = OracleOrder(inputs, outputs);
        double totalWeight = 0d, exceeding = 0d;
        foreach (int i in order)
        {
            double w = weights == null ? 1d : weights[i];
            totalWeight += w;
            if (outputs[i] > threshold) exceeding += w;
        }
        double baseline = exceeding / totalWeight;
        double movement = 0d;
        int position = 0;
        double consumed = 0d;
        for (int b = 0; b < Bins && position < order.Count; b++)
        {
            double target = totalWeight * (b + 1) / Bins;
            double binWeight = 0d, binExceeding = 0d;
            while (position < order.Count && (binWeight <= 0d || consumed + binWeight < target || b == Bins - 1))
            {
                int i = order[position];
                double w = weights == null ? 1d : weights[i];
                binWeight += w;
                if (outputs[i] > threshold) binExceeding += w;
                position++;
            }
            consumed += binWeight;
            if (binWeight > 0d) movement += binWeight * Math.Abs(binExceeding / binWeight - baseline);
        }
        return (movement / totalWeight, baseline);
    }

    /// <summary>
    /// The oracle's own valid-pair ordering: pairwise NaN filtering and the stable ascending
    /// input sort with the realization index breaking ties.
    /// </summary>
    /// <param name="inputs">The input column.</param>
    /// <param name="outputs">The output sample.</param>
    /// <returns>The ordered valid indices.</returns>
    private static List<int> OracleOrder(double[] inputs, double[] outputs)
    {
        var order = new List<int>();
        for (int i = 0; i < inputs.Length; i++)
        {
            if (!double.IsNaN(inputs[i]) && !double.IsNaN(outputs[i])) order.Add(i);
        }
        order.Sort((a, b) =>
        {
            int comparison = inputs[a].CompareTo(inputs[b]);
            return comparison != 0 ? comparison : a.CompareTo(b);
        });
        return order;
    }

    #endregion

    /// <summary>
    /// Verifies the unweighted engine query against the independent re-implementation over the
    /// public per-realization measures: every entry's resolvable variance, the total, the
    /// exact group rollups, and the movement block whose baseline must equal the published
    /// tolerable-risk confidence bit-exactly.
    /// </summary>
    [TestMethod]
    public async Task Test_EngineQuery_MatchesIndependentReimplementation()
    {
        // Arrange
        var analysis = BuildEngineAnalysis();
        await analysis.RunAsync();

        // Act
        var voi = analysis.MeasureValueOfInformation(RiskMeasure.Mean, RiskType.Total);

        // Assert
        Assert.IsNotNull(voi);
        var outputs = PublicMeasures(analysis, RiskType.Total);
        var columns = WalkColumns(analysis);
        Assert.AreEqual(columns.Count, voi!.Entries.Count, "One entry per knowledge column.");
        double groupCheck = 0d;
        for (int c = 0; c < columns.Count; c++)
        {
            var (label, group, column) = columns[c];
            var entry = voi.Entries[c];
            Assert.AreEqual(label, entry.Label);
            Assert.AreEqual(group, entry.GroupLabel);
            var oracle = OracleMainEffect(column, outputs, null);
            Assert.AreEqual(oracle.Between, entry.ResolvableVariance, RoundingTolerance * Math.Max(1d, Math.Abs(oracle.Between)),
                $"Entry '{label}': the independent conditioning must agree to rounding.");
            Assert.AreEqual(oracle.Total, voi.TotalVariance, RoundingTolerance * Math.Max(1d, oracle.Total),
                "The total epistemic variance matches the oracle's population form.");
            groupCheck += entry.ResolvableVariance;
        }
        double groupSum = 0d;
        foreach (var group in voi.Groups) groupSum += group.ResolvableVariance;
        Assert.AreEqual(groupCheck, groupSum, 0d, "The rollups partition the entries bit-exactly.");

        // The movement block against the published confidence and the oracle.
        Assert.AreEqual(1, voi.CriterionMovements.Count);
        var block = voi.CriterionMovements[0];
        var published = analysis.RiskResults!.Summary!.TolerableRiskConfidence![0];
        Assert.AreEqual(published.ExceedanceProbability, block.BaselineExceedanceProbability, 0d,
            "The movement baseline is the published confidence, bit-exactly.");
        var criterionOutputs = PublicMeasures(analysis, RiskType.Excess);
        for (int c = 0; c < columns.Count; c++)
        {
            var movement = OracleMovement(columns[c].Column, criterionOutputs, null, 1d);
            Assert.AreEqual(movement.Movement, block.EntryMovements[c], RoundingTolerance,
                $"Entry '{columns[c].Label}': the independent movement must agree to rounding.");
        }
    }

    /// <summary>
    /// Verifies the weighted engine query: post-hoc realization weights flow through every
    /// conditional reduction, the weighted movement baseline equals the post-hoc weighted
    /// confidence recomputation bit-exactly, and the stored ensemble is untouched by the
    /// queries.
    /// </summary>
    [TestMethod]
    public async Task Test_WeightedEngineQuery_MatchesIndependentReimplementation()
    {
        // Arrange — the A4 house weight pattern.
        var analysis = BuildEngineAnalysis();
        await analysis.RunAsync();
        string stored = analysis.RiskResults!.ToJson();
        var weights = new double[analysis.RiskResults.Count];
        for (int i = 0; i < weights.Length; i++) weights[i] = 0.25d + ((37 * i) % 11);
        analysis.RiskResults.SetRealizationWeights(weights);

        // Act
        var voi = analysis.MeasureValueOfInformation(RiskMeasure.Mean, RiskType.Total);
        var confidence = analysis.ComputeTolerableRiskConfidence();

        // Assert
        Assert.IsNotNull(voi);
        Assert.IsNotNull(confidence);
        var outputs = PublicMeasures(analysis, RiskType.Total);
        var columns = WalkColumns(analysis);
        for (int c = 0; c < columns.Count; c++)
        {
            var oracle = OracleMainEffect(columns[c].Column, outputs, weights);
            Assert.AreEqual(oracle.Between, voi!.Entries[c].ResolvableVariance,
                RoundingTolerance * Math.Max(1d, Math.Abs(oracle.Between)),
                $"Entry '{columns[c].Label}': the weighted conditioning must agree to rounding.");
        }
        var block = voi!.CriterionMovements[0];
        Assert.AreEqual(confidence![0].ExceedanceProbability, block.BaselineExceedanceProbability, 0d,
            "The weighted baseline equals the post-hoc weighted confidence bit-exactly.");
        var criterionOutputs = PublicMeasures(analysis, RiskType.Excess);
        var oracleMovement = OracleMovement(columns[0].Column, criterionOutputs, weights, 1d);
        Assert.AreEqual(oracleMovement.Movement, block.EntryMovements[0], RoundingTolerance,
            "The weighted movement must agree with the independent re-implementation.");

        // The queries never mutate the stored realizations (the weight stamp is the one
        // sanctioned change).
        analysis.RiskResults.SetRealizationWeights(null);
        Assert.AreEqual(stored, analysis.RiskResults.ToJson(),
            "Clearing the weights restores the stored payload byte-for-byte.");
    }
}
