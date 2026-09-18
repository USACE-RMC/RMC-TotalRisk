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
using RMC.TotalRisk.RiskFunctions.Transforms;
using RMC.TotalRisk.Systems.Components;
using RMC.TotalRisk.Systems.Components.Graph;

namespace RMC.TotalRisk.Verification.Analyses.Risk;

/// <summary>
/// Risk-profile verification — the profile-axis remap and the profile catalog:
/// the pushforward of the profile axis is exact at recorded knots and leaves every non-profile
/// output bit-identical; the hazard threshold reads equivalently on the raw and profile axes at
/// a shared knot; the cumulative failure probability, cumulative expected consequence, and
/// system response profiles agree with an independent dense-quadrature oracle over the same
/// tables; reliability mode carries the cumulative failure profile; and the full-uncertainty
/// bands restore the v1.0 five-stream profile scope with ordered percentile bands.
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// <para>
/// <b>Scenario R (remap):</b> a deterministic flow-frequency curve driving a Flow→Stage rating
/// (T(q) = q/200), a stage fragility, and stage consequences — the profile element is the
/// rating, so every profile ordinate must be the exact rating pushforward of the raw-axis run.
/// <b>Scenario C (catalog):</b> a two-mode joint-failure component (independent capacities,
/// Additive consequence rule) over a 65-knot normal-quantile stage-frequency table with linear
/// fragilities and consequences — every catalog quantity has an independent oracle: the
/// annualized failure probability ∫(1 − (1−p_A)(1−p_B)) du, the additive-rule failure mean
/// ∫(p_A·c_A + p_B·c_B) du (linearity of the Sum rule), interior cumulative ordinates
/// ∫ up to u(h_j), and the exact per-knot combined response 1 − (1−p_A(h_j))(1−p_B(h_j)).
/// </para>
/// <para>
/// <b>Tolerances:</b> identity asserts (terminal ordinate vs the stored mass balance / mean)
/// are exact bookkeeping and use 1e-12 relative. Oracle comparisons of the terminals use 1e-4
/// relative — the quadrature mass-accounting residual envelope (measured ≈ 3e-6 in the
/// EAD family under the earlier midpoint-trapezoid partition, since replaced by the
/// recorded-mass ledger) plus the dense-trapezoid oracle's own O(h²) error at 200,001 ordinates
/// (≈ 1e-9). Interior cumulative probes use 2e-3 relative of the terminal: the engine's
/// ascending cumulate at ordinate j is a partial sum of the per-abscissa masses, representing
/// the integral only to within the local inter-node spacing — a half-interval
/// discretization allowance at the mean pass's recorded density (measured well inside the
/// bound). Per-knot response comparisons are exact interpolation chains on both sides and use
/// 1e-9 relative.
/// </para>
/// </remarks>
[TestClass]
public class RiskProfileVerification
{
    /// <summary>The dense-trapezoid oracle resolution over the non-exceedance domain.</summary>
    private const int OracleOrdinates = 200_001;

    /// <summary>The engine's probability floor (the recorded domain is [floor, 1 − floor]).</summary>
    private const double ProbabilityFloor = 1e-16;

    #region Scenario R — remap

    /// <summary>Builds the remap scenario's flow-frequency table (0.999 → 0 cfs up to 0.001 → 100,000 cfs).</summary>
    private static TabularHazard FlowFrequency()
    {
        return new TabularHazard
        {
            Name = "Flow Frequency",
            SpecifiedHazard = "Flow",
            HazardUnit = "cfs",
            NoUncertaintyFunction = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(0.999d, new Deterministic(0d)),
                    new UncertainOrdinate(0.5d, new Deterministic(50_000d)),
                    new UncertainOrdinate(0.001d, new Deterministic(100_000d)),
                },
                true, SortOrder.Descending, true, SortOrder.Ascending, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds the remap scenario's Flow→Stage rating, T(q) = q / 200.</summary>
    private static TabularTransform Rating()
    {
        return new TabularTransform
        {
            Name = "Rating",
            SpecifiedHazard = "Flow",
            HazardUnit = "cfs",
            TransformedHazard = "Stage",
            TransformedHazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(0d, new Deterministic(0d)), new UncertainOrdinate(100_000d, new Deterministic(500d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds the remap scenario's component: flow hazard → rating → stage fragility (250 → 0 to 450 → 1) → stage damages.</summary>
    private static SystemComponent RemapComponent()
    {
        var fragility = new TabularResponse
        {
            Name = "Breach",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(250d, new Deterministic(0d)), new UncertainOrdinate(450d, new Deterministic(1d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
        var failure = new TabularConsequence
        {
            Name = "Failure Damages",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(0d, new Deterministic(0d)), new UncertainOrdinate(500d, new Deterministic(1000d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
        var nonFailure = new TabularConsequence
        {
            Name = "Non-Failure Damages",
            SpecifiedHazard = "Flow",
            HazardUnit = "cfs",
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(0d, new Deterministic(0d)), new UncertainOrdinate(100_000d, new Deterministic(100d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };

        var component = new SystemComponent { Name = "Levee" };
        component.HazardFunction = FlowFrequency();
        component.AddFailureMode(new FailureMode(new List<ITransformFunction> { Rating() }, null, fragility, failure));
        component.AddFailureMode(new FailureMode(null, null, null, nonFailure));
        return component;
    }

    /// <summary>Runs a mean-only analysis over one component.</summary>
    private static RiskAnalysis RunMeanOnly(SystemComponent component)
    {
        var analysis = new RiskAnalysis(new[] { component });
        analysis.RunAsync().GetAwaiter().GetResult();
        Assert.IsTrue(analysis.IsEstimated, "The run must succeed.");
        return analysis;
    }

    /// <summary>
    /// Verifies the profile-axis pushforward is exact at every recorded knot: with the rating
    /// selected, each profile hazard ordinate equals T(raw ordinate) while every profile Y
    /// array and every stored risk measure other than the hazard-threshold read is
    /// bit-identical to the raw-axis run.
    /// </summary>
    [TestMethod]
    public void Test_ProfileRemap_PushforwardExactAtKnots()
    {
        // Arrange / Act — the same model on the raw axis and on the profile axis.
        var rawAnalysis = RunMeanOnly(RemapComponent());
        var profiled = RemapComponent();
        profiled.SetProfileHazardElement(profiled.Graph.GetElements<TransformElement>().Single());
        var profileAnalysis = RunMeanOnly(profiled);

        var raw = rawAnalysis.MeanRiskResults!.Components[0].Curves;
        var remapped = profileAnalysis.MeanRiskResults!.Components[0].Curves;

        // Assert — the profile X axis is the exact rating pushforward, ordinate for ordinate.
        Assert.AreEqual(raw.Total.HazardFrequencyHazards.Length, remapped.Total.HazardFrequencyHazards.Length,
            "The same evaluations must be recorded on both axes.");
        for (int j = 0; j < raw.Total.HazardFrequencyHazards.Length; j++)
        {
            double expected = raw.Total.HazardFrequencyHazards[j] / 200d;
            Assert.AreEqual(expected, remapped.Total.HazardFrequencyHazards[j], 1e-9 * Math.Max(1d, Math.Abs(expected)),
                $"Profile ordinate {j} must be the rating pushforward of the raw ordinate.");
        }

        // Every profile Y array is bit-identical — the remap relabels the axis, never the values.
        CollectionAssert.AreEqual(raw.Total.HazardFrequencyProbabilities, remapped.Total.HazardFrequencyProbabilities);
        CollectionAssert.AreEqual(raw.Total.HazardVsCenConsequences, remapped.Total.HazardVsCenConsequences);
        CollectionAssert.AreEqual(raw.Total.CumulativeExpectedConsequences, remapped.Total.CumulativeExpectedConsequences);
        CollectionAssert.AreEqual(raw.Fail.CumulativeFailureProbabilities, remapped.Fail.CumulativeFailureProbabilities);

        // The response profile is deliberately NOT remapped: its exceedance axis is identical.
        CollectionAssert.AreEqual(raw.Fail.SystemResponseExceedanceProbabilities, remapped.Fail.SystemResponseExceedanceProbabilities);
        CollectionAssert.AreEqual(raw.Fail.SystemResponseProbabilities, remapped.Fail.SystemResponseProbabilities);

        // Non-profile outputs are bit-identical.
        var rawSummary = rawAnalysis.RiskResults![0]!.ComponentResults[0];
        var profileSummary = profileAnalysis.RiskResults![0]!.ComponentResults[0];
        Assert.AreEqual(rawSummary.Fail.TotalProbability, profileSummary.Fail.TotalProbability, 0d);
        Assert.AreEqual(rawSummary.Total.Mean, profileSummary.Total.Mean, 0d);
        Assert.AreEqual(rawSummary.Total.StandardDeviation, profileSummary.Total.StandardDeviation, 0d);
        Assert.AreEqual(rawSummary.Total.ConditionalValueAtRisk, profileSummary.Total.ConditionalValueAtRisk, 0d);
    }

    /// <summary>
    /// Verifies the hazard-threshold equivalence at a shared knot: a threshold at recorded raw
    /// knot h reads the same probability as the profile-axis threshold at T(h) — the profile
    /// selection re-expresses the threshold without changing its meaning at recorded ordinates
    /// (between knots the two reads differ only by log-log segment curvature, documented).
    /// </summary>
    [TestMethod]
    public void Test_ProfileRemap_HazardThreshold_KnotEquivalence()
    {
        // Arrange — discover a mid-curve recorded knot from a threshold-free run.
        var discovery = RunMeanOnly(RemapComponent());
        var hazards = discovery.MeanRiskResults!.Components[0].Curves.Fail.HazardFrequencyHazards;
        double rawKnot = hazards[hazards.Length / 2];

        // Act — the same knot expressed on each axis.
        var rawComponent = RemapComponent();
        rawComponent.HazardThreshold = rawKnot;
        var rawAnalysis = RunMeanOnly(rawComponent);

        var profiledComponent = RemapComponent();
        profiledComponent.SetProfileHazardElement(profiledComponent.Graph.GetElements<TransformElement>().Single());
        profiledComponent.HazardThreshold = rawKnot / 200d;
        var profileAnalysis = RunMeanOnly(profiledComponent);

        // Assert — knot reads are exact on both axes.
        double rawProbability = rawAnalysis.RiskResults![0]!.ComponentResults[0].Fail.HazardThresholdProbability;
        double profileProbability = profileAnalysis.RiskResults![0]!.ComponentResults[0].Fail.HazardThresholdProbability;
        Assert.IsTrue(rawProbability > 0d && rawProbability < 1d, "The knot threshold must read an interior probability.");
        Assert.AreEqual(rawProbability, profileProbability, 1e-9 * rawProbability,
            "The threshold must read equivalently on the raw and profile axes at a shared knot.");
    }

    #endregion

    #region Scenario C — catalog vs the quadrature oracle

    /// <summary>The stage-frequency knot z-grid shared by the engine fixture and the oracle.</summary>
    private static readonly double[] OracleZGrid = BuildZGrid();

    /// <summary>Builds the shared z-grid (−8 to 8 in quarter steps).</summary>
    private static double[] BuildZGrid()
    {
        var grid = new double[65];
        for (int i = 0; i < 65; i++)
        {
            grid[i] = -8d + i * 0.25d;
        }
        return grid;
    }

    /// <summary>The scenario constants: stage N(100, 20); fragility A (100 → 0, 180 → 1); fragility B (120 → 0, 200 → 1); c_A (60 → 0, 200 → 1000); c_B (60 → 0, 200 → 2000); non-failure (60 → 0, 200 → 100).</summary>
    private const double StageMean = 100d, StageSigma = 20d;
    private const double FragAStart = 100d, FragAEnd = 180d;
    private const double FragBStart = 120d, FragBEnd = 200d;
    private const double ConsequenceStart = 60d, ConsequenceEnd = 200d;
    private const double ConsequenceAScale = 1000d, ConsequenceBScale = 2000d, NonFailScale = 100d;

    /// <summary>Builds the catalog scenario's two-mode joint component (independent, Additive rule).</summary>
    private static SystemComponent CatalogComponent(bool includeConsequences = true, IResponseFunction? fragilityA = null)
    {
        var hazardOrdinates = new UncertainOrdinate[OracleZGrid.Length];
        for (int i = 0; i < OracleZGrid.Length; i++)
        {
            hazardOrdinates[i] = new UncertainOrdinate(1d - Normal.StandardCDF(OracleZGrid[i]),
                new Deterministic(StageMean + StageSigma * OracleZGrid[i]));
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

        TabularResponse Fragility(string name, double start, double end) => new()
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(start, new Deterministic(0d)), new UncertainOrdinate(end, new Deterministic(1d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
        TabularConsequence Consequence(string name, double scale) => new()
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(ConsequenceStart, new Deterministic(0d)), new UncertainOrdinate(ConsequenceEnd, new Deterministic(scale)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };

        var component = new SystemComponent { Name = "Dam", FailureModeMethod = FailureModeMethod.JointFailures, JointConsequences = JointConsequenceType.Additive };
        component.HazardFunction = hazard;
        component.AddFailureMode(new FailureMode(null, null, fragilityA ?? Fragility("Mode A", FragAStart, FragAEnd),
            includeConsequences ? Consequence("A Damages", ConsequenceAScale) : null));
        component.AddFailureMode(new FailureMode(null, null, Fragility("Mode B", FragBStart, FragBEnd),
            includeConsequences ? Consequence("B Damages", ConsequenceBScale) : null));
        if (includeConsequences)
        {
            component.AddFailureMode(new FailureMode(null, null, null, Consequence("Non-Failure Damages", NonFailScale)));
        }
        return component;
    }

    /// <summary>The oracle's stage at a non-exceedance probability — its own linear interpolation over the shared knots.</summary>
    private static double OracleStage(double nonExceedance)
    {
        int n = OracleZGrid.Length;
        double u0 = Normal.StandardCDF(OracleZGrid[0]);
        double un = Normal.StandardCDF(OracleZGrid[n - 1]);
        if (nonExceedance <= u0) return StageMean + StageSigma * OracleZGrid[0];
        if (nonExceedance >= un) return StageMean + StageSigma * OracleZGrid[n - 1];
        for (int i = 1; i < n; i++)
        {
            double ui = Normal.StandardCDF(OracleZGrid[i]);
            if (nonExceedance <= ui)
            {
                double uPrev = Normal.StandardCDF(OracleZGrid[i - 1]);
                double t = (nonExceedance - uPrev) / (ui - uPrev);
                double stagePrev = StageMean + StageSigma * OracleZGrid[i - 1];
                double stageNext = StageMean + StageSigma * OracleZGrid[i];
                return stagePrev + t * (stageNext - stagePrev);
            }
        }
        return StageMean + StageSigma * OracleZGrid[n - 1];
    }

    /// <summary>The oracle's non-exceedance probability at a stage — the inverse table walk.</summary>
    private static double OracleNonExceedance(double stage)
    {
        int n = OracleZGrid.Length;
        double stage0 = StageMean + StageSigma * OracleZGrid[0];
        double stageN = StageMean + StageSigma * OracleZGrid[n - 1];
        if (stage <= stage0) return Normal.StandardCDF(OracleZGrid[0]);
        if (stage >= stageN) return Normal.StandardCDF(OracleZGrid[n - 1]);
        for (int i = 1; i < n; i++)
        {
            double stageNext = StageMean + StageSigma * OracleZGrid[i];
            if (stage <= stageNext)
            {
                double stagePrev = StageMean + StageSigma * OracleZGrid[i - 1];
                double t = (stage - stagePrev) / (stageNext - stagePrev);
                double uPrev = Normal.StandardCDF(OracleZGrid[i - 1]);
                double uNext = Normal.StandardCDF(OracleZGrid[i]);
                return uPrev + t * (uNext - uPrev);
            }
        }
        return Normal.StandardCDF(OracleZGrid[n - 1]);
    }

    /// <summary>A clamped linear ramp — the oracle's fragility and consequence form.</summary>
    private static double Ramp(double x, double start, double end, double scale)
    {
        if (x <= start) return 0d;
        if (x >= end) return scale;
        return scale * (x - start) / (end - start);
    }

    /// <summary>The oracle's combined (union) response probability at a stage.</summary>
    private static double OracleUnion(double stage)
    {
        double pA = Ramp(stage, FragAStart, FragAEnd, 1d);
        double pB = Ramp(stage, FragBStart, FragBEnd, 1d);
        return 1d - (1d - pA) * (1d - pB);
    }

    /// <summary>Dense-trapezoid oracle integral of an integrand over non-exceedance ∈ [floor, upper].</summary>
    private static double OracleIntegral(Func<double, double> integrand, double upper)
    {
        double lower = ProbabilityFloor;
        if (upper <= lower) return 0d;
        double step = (upper - lower) / (OracleOrdinates - 1);
        double sum = 0.5d * (integrand(lower) + integrand(upper));
        for (int i = 1; i < OracleOrdinates - 1; i++)
        {
            sum += integrand(lower + i * step);
        }
        return sum * step;
    }

    /// <summary>
    /// Verifies the catalog against the independent quadrature oracle on the two-mode joint
    /// scenario: terminal identities (exact bookkeeping), terminals vs the oracle integrals
    /// (the mass-accounting residual envelope), interior cumulative ordinates vs partial oracle
    /// integrals (the half-interval allowance), and the response profile's exact per-knot
    /// union and exceedance coordinates.
    /// </summary>
    [TestMethod]
    public void Test_ProfileCatalog_TwoModeJoint_VsQuadratureOracle()
    {
        // Arrange / Act
        var analysis = RunMeanOnly(CatalogComponent());
        var fail = analysis.MeanRiskResults!.Components[0].Curves.Fail;
        var total = analysis.MeanRiskResults.Components[0].Curves.Total;

        // Assert — exact bookkeeping identities.
        Assert.AreEqual(fail.MassBalance, fail.CumulativeFailureProbabilities[0], 1e-12 * fail.MassBalance,
            "A-terminal ≡ the Fail mass balance (exact identity).");
        Assert.AreEqual(fail.Mean, fail.CumulativeExpectedConsequences[0], 1e-12 * fail.Mean,
            "B-terminal ≡ the Fail mean (exact identity).");
        Assert.AreEqual(total.Mean, total.CumulativeExpectedConsequences[0], 1e-12 * total.Mean,
            "B-terminal ≡ the Total mean (exact identity).");

        // Oracle terminals: APF = ∫ union du; Fail mean = ∫ (p_A c_A + p_B c_B) du (Sum-rule
        // linearity); Total mean adds the complement-weighted non-failure consequence.
        double oracleApf = OracleIntegral(u => OracleUnion(OracleStage(u)), 1d - ProbabilityFloor);
        double oracleFailMean = OracleIntegral(u =>
        {
            double stage = OracleStage(u);
            return Ramp(stage, FragAStart, FragAEnd, 1d) * Ramp(stage, ConsequenceStart, ConsequenceEnd, ConsequenceAScale)
                 + Ramp(stage, FragBStart, FragBEnd, 1d) * Ramp(stage, ConsequenceStart, ConsequenceEnd, ConsequenceBScale);
        }, 1d - ProbabilityFloor);
        double oracleTotalMean = oracleFailMean + OracleIntegral(u =>
        {
            double stage = OracleStage(u);
            return (1d - OracleUnion(stage)) * Ramp(stage, ConsequenceStart, ConsequenceEnd, NonFailScale);
        }, 1d - ProbabilityFloor);

        Assert.AreEqual(oracleApf, fail.CumulativeFailureProbabilities[0], 1e-4 * oracleApf,
            "A-terminal vs the oracle annualized failure probability (mass-accounting residual envelope).");
        Assert.AreEqual(oracleFailMean, fail.CumulativeExpectedConsequences[0], 1e-4 * oracleFailMean,
            "Fail B-terminal vs the oracle additive-rule failure mean.");
        Assert.AreEqual(oracleTotalMean, total.CumulativeExpectedConsequences[0], 1e-4 * oracleTotalMean,
            "Total B-terminal vs the oracle total mean.");

        // Interior cumulative probes at the quartile ordinates: the engine's partial sum of the
        // recorded per-abscissa masses represents the integral only to within the local
        // inter-node spacing — a half-interval discretization allowance.
        var hazards = fail.HazardFrequencyHazards;
        foreach (double fraction in new[] { 0.25d, 0.5d, 0.75d })
        {
            int j = (int)(hazards.Length * fraction);
            double upper = OracleNonExceedance(hazards[j]);
            double oracleA = OracleIntegral(u => OracleUnion(OracleStage(u)), upper);
            Assert.AreEqual(oracleA, fail.CumulativeFailureProbabilities[j], 2e-3 * oracleApf,
                $"Interior cumulative ordinate at fraction {fraction} vs the partial oracle integral.");
        }

        // The response profile: ordinate k pairs with the distinct hazard hazards[n−1−k]
        // (ascending hazard = descending exceedance); the union and the exceedance coordinate
        // are exact interpolation chains on both sides.
        var srpX = fail.SystemResponseExceedanceProbabilities;
        var srpY = fail.SystemResponseProbabilities;
        Assert.AreEqual(hazards.Length, srpX.Length, "One response ordinate per distinct recorded hazard.");
        for (int k = 0; k < srpX.Length; k += Math.Max(1, srpX.Length / 25))
        {
            double stage = hazards[hazards.Length - 1 - k];
            double expectedUnion = OracleUnion(stage);
            double expectedExceedance = 1d - OracleNonExceedance(stage);
            Assert.AreEqual(expectedUnion, srpY[k], 1e-9 * Math.Max(1e-12, expectedUnion),
                $"The combined response at ordinate {k} must equal the exact union at its hazard.");
            Assert.AreEqual(expectedExceedance, srpX[k], 1e-9 * Math.Max(1e-12, expectedExceedance),
                $"The exceedance coordinate at ordinate {k} must equal 1 − u(h).");
        }
    }

    /// <summary>
    /// Verifies reliability mode carries the cumulative failure profile — there it is the
    /// headline profile (the annualized failure probability accumulated by hazard) — with the
    /// terminal equal to the reported annualized failure probability.
    /// </summary>
    [TestMethod]
    public void Test_ProfileCatalog_ReliabilityMode_CumulativePresent()
    {
        // Arrange
        var analysis = new RiskAnalysis(new[] { CatalogComponent(includeConsequences: false) });
        analysis.Options.Mode = RiskAnalysisMode.Reliability;

        // Act
        analysis.RunAsync().GetAwaiter().GetResult();
        Assert.IsTrue(analysis.IsEstimated);

        // Assert
        var fail = analysis.MeanRiskResults!.Components[0].Curves.Fail;
        Assert.IsTrue(fail.CumulativeFailureProbabilities.Length > 2, "Reliability mode must build the cumulative failure profile.");
        Assert.IsTrue(fail.SystemResponseProbabilities.Length > 2, "Reliability mode must build the response profile.");
        double reportedAfp = analysis.RiskResults![0]!.ComponentResults[0].Fail.TotalProbability;
        Assert.AreEqual(reportedAfp, Math.Min(1d, fail.CumulativeFailureProbabilities[0]), 1e-12 * reportedAfp,
            "The cumulative terminal must equal the reported annualized failure probability.");
    }

    /// <summary>
    /// Verifies the full-uncertainty banding: the five-stream profile scope is restored (v1.0
    /// banded every stream; the Total-only interim was a parity gap), the banded cumulative
    /// profiles preserve the Lower ≤ Median ≤ Upper ordering and hazard monotonicity, the
    /// response band rides the log exceedance grid within [0, 1], and repeated runs are
    /// bit-identical on the new arrays.
    /// </summary>
    [TestMethod]
    public void Test_ProfileCatalog_FullUncertainty_FiveStreamBands_AndReproducibility()
    {
        // Arrange — epistemic spread from a triangular fragility.
        SystemComponent Build()
        {
            var uncertain = new TabularResponse
            {
                Name = "Mode A",
                SpecifiedHazard = "Stage",
                HazardUnit = "ft",
                UncertainOrderedPairedData = new UncertainOrderedPairedData(
                    new[]
                    {
                        new UncertainOrdinate(FragAStart, new Triangular(0d, 0.02d, 0.05d)),
                        new UncertainOrdinate(FragAEnd, new Triangular(0.7d, 0.9d, 1d)),
                    },
                    true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Triangular),
            };
            return CatalogComponent(fragilityA: uncertain);
        }
        RiskAnalysis Run()
        {
            var analysis = new RiskAnalysis(new[] { Build() });
            analysis.Options.EstimateMeanRiskOnly = false;
            analysis.Options.Realizations = 200;
            analysis.RunAsync().GetAwaiter().GetResult();
            Assert.IsTrue(analysis.IsEstimated);
            return analysis;
        }

        // Act
        var first = Run();
        var second = Run();

        // Assert — five-stream banded frequency profiles on every band tree.
        foreach (var band in new[] { first.LowerRiskResults!, first.UpperRiskResults!, first.MedianRiskResults!, first.MeanRiskResults! })
        {
            var curves = band.Components[0].Curves;
            foreach (var stream in new[] { curves.Excess, curves.Background, curves.Total, curves.Fail, curves.NonFail })
            {
                Assert.IsTrue(stream.HazardFrequencyHazards.Length > 2, $"Band '{band.Name}' must carry five-stream frequency profiles.");
                Assert.IsTrue(stream.CumulativeExpectedConsequences.Length > 2, $"Band '{band.Name}' must carry the cumulative consequence profile.");
            }
        }

        // Band ordering and monotonicity on the cumulative failure profile.
        var lower = first.LowerRiskResults!.Components[0].Curves.Fail.CumulativeFailureProbabilities;
        var median = first.MedianRiskResults!.Components[0].Curves.Fail.CumulativeFailureProbabilities;
        var upper = first.UpperRiskResults!.Components[0].Curves.Fail.CumulativeFailureProbabilities;
        Assert.IsTrue(lower.Length > 2 && lower.Length == upper.Length && lower.Length == median.Length);
        for (int g = 0; g < lower.Length; g++)
        {
            Assert.IsTrue(lower[g] <= median[g] + 1e-15 && median[g] <= upper[g] + 1e-15,
                $"Band ordering must hold at grid ordinate {g}.");
        }
        for (int g = 1; g < median.Length; g++)
        {
            Assert.IsTrue(median[g] <= median[g - 1] + 1e-15, "The banded cumulate must stay monotone along descending hazard.");
        }

        // The response band rides the log exceedance grid, in [0, 1].
        var srpX = first.MeanRiskResults!.Components[0].Curves.Fail.SystemResponseExceedanceProbabilities;
        var srpY = first.MeanRiskResults.Components[0].Curves.Fail.SystemResponseProbabilities;
        Assert.AreEqual(first.Options.LECOutputLength, srpX.Length);
        for (int g = 0; g < srpY.Length; g++)
        {
            Assert.IsTrue(srpY[g] >= 0d && srpY[g] <= 1d + 1e-12, "Banded response ordinates must stay probabilities.");
            if (g > 0) Assert.IsTrue(srpX[g] < srpX[g - 1], "The exceedance grid must be strictly descending.");
        }

        // Reproducibility: the new arrays are bit-identical across runs.
        CollectionAssert.AreEqual(
            first.MeanRiskResults.Components[0].Curves.Fail.CumulativeFailureProbabilities,
            second.MeanRiskResults!.Components[0].Curves.Fail.CumulativeFailureProbabilities,
            "Repeated runs must reproduce the banded cumulative profile bit-for-bit.");
        CollectionAssert.AreEqual(srpY, second.MeanRiskResults.Components[0].Curves.Fail.SystemResponseProbabilities,
            "Repeated runs must reproduce the banded response profile bit-for-bit.");
    }

    #endregion
}
