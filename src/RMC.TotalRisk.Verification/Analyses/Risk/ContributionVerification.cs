using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.Systems.Components;

namespace RMC.TotalRisk.Verification.Analyses.Risk;

/// <summary>
/// % contribution verification — the exclusive-event attribution diagnostic: each
/// failure mode's contribution to its component and each component's contribution to the
/// system, for all four combination methods and both system methods. The exclusive event
/// probability splits equally among participants (the Shapley value of the union game); event
/// consequences split proportionally to the participants' marginal values. Verified against
/// independent dense-quadrature oracles of the adjusted-marginal integrals, the additive
/// system's brute-force exclusive enumeration, exact within-run additivity identities, and
/// reproducibility pins.
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// <para>
/// <b>Scenario:</b> the two-mode component over a 65-knot normal-quantile stage-frequency
/// table (Normal(100, 20), z ± 8 by 0.25): fragility A (100 → 0, 180 → 1), fragility B
/// (120 → 0, 200 → 1), consequences c_A (60 → 0, 200 → 1,000), c_B (60 → 0, 200 → 2,000),
/// non-failure (60 → 0, 200 → 100) — all deterministic, so the mean pass is exactly the
/// quadrature of the oracle's own interpolation chains. The system tests reuse the component
/// at scaled consequences across two or three components.
/// </para>
/// <para>
/// <b>Tolerances:</b> within-run additivity identities (Σ contributions ≡ the parent's raw
/// recorded totals) are pure floating-point association — 1e-12 relative. Oracle terminals
/// carry the engine's quadrature mass-accounting residual (measured ≈ 3e-6 on means in the
/// EAD family under the earlier midpoint-trapezoid partition, since replaced by the
/// recorded-mass ledger) plus the dense-trapezoid oracle's own O(h²) error — 1e-4 relative.
/// The competing method's engine adjustment interpolates cumulative incidence functions
/// pre-processed over 200 stratified bins (the v1.0 constant), while the oracle integrates
/// the tech note's Eq. 14 rectangle rule at 20,001 bins — the comparison carries a 1e-2
/// relative allowance for the engine's CIF discretization (the competing family
/// measured ≈ 0.3% at five modes; two modes sit well inside).
/// </para>
/// </remarks>
[TestClass]
public class ContributionVerification
{
    /// <summary>The dense-trapezoid oracle resolution over the non-exceedance domain.</summary>
    private const int OracleOrdinates = 200_001;

    /// <summary>The oracle's competing cumulative-incidence resolution (Eq. 14 rectangle rule).</summary>
    private const int OracleIncidenceBins = 20_001;

    /// <summary>The engine's probability floor.</summary>
    private const double ProbabilityFloor = 1e-16;

    /// <summary>The shared z-grid.</summary>
    private static readonly double[] ZGrid = BuildZGrid();

    /// <summary>The scenario constants.</summary>
    private const double StageMean = 100d, StageSigma = 20d;
    private const double FragAStart = 100d, FragAEnd = 180d;
    private const double FragBStart = 120d, FragBEnd = 200d;
    private const double ConsequenceStart = 60d, ConsequenceEnd = 200d;
    private const double ConsequenceAScale = 1000d, ConsequenceBScale = 2000d, NonFailScale = 100d;

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

    /// <summary>Builds the two-mode scenario component under a combination method (consequences scaled for the system tests).</summary>
    private static SystemComponent Component(FailureModeMethod method, double scale = 1d,
        JointConsequenceType jointConsequences = JointConsequenceType.Maximum)
    {
        var hazardOrdinates = new UncertainOrdinate[ZGrid.Length];
        for (int i = 0; i < ZGrid.Length; i++)
        {
            hazardOrdinates[i] = new UncertainOrdinate(1d - Normal.StandardCDF(ZGrid[i]),
                new Deterministic(StageMean + StageSigma * ZGrid[i]));
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
        TabularConsequence Consequence(string name, double top) => new()
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(ConsequenceStart, new Deterministic(0d)), new UncertainOrdinate(ConsequenceEnd, new Deterministic(top)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };

        var component = new SystemComponent { Name = $"Dam ×{scale:G3}", FailureModeMethod = method, JointConsequences = jointConsequences };
        component.HazardFunction = hazard;
        component.AddFailureMode(new FailureMode(null, null, Fragility("Mode A", FragAStart, FragAEnd), Consequence("A Damages", ConsequenceAScale * scale)));
        component.AddFailureMode(new FailureMode(null, null, Fragility("Mode B", FragBStart, FragBEnd), Consequence("B Damages", ConsequenceBScale * scale)));
        component.AddFailureMode(new FailureMode(null, null, null, Consequence("Non-Failure Damages", NonFailScale * scale)));
        return component;
    }

    /// <summary>Runs a mean-only analysis and returns it.</summary>
    private static RiskAnalysis RunMeanOnly(params SystemComponent[] components)
    {
        var analysis = new RiskAnalysis(components);
        analysis.RunAsync().GetAwaiter().GetResult();
        Assert.IsTrue(analysis.IsEstimated, "The run must succeed.");
        return analysis;
    }

    #region Oracle

    /// <summary>The oracle's stage at a non-exceedance probability (its own linear interpolation).</summary>
    private static double OracleStage(double nonExceedance)
    {
        int n = ZGrid.Length;
        double u0 = Normal.StandardCDF(ZGrid[0]);
        double un = Normal.StandardCDF(ZGrid[n - 1]);
        if (nonExceedance <= u0) return StageMean + StageSigma * ZGrid[0];
        if (nonExceedance >= un) return StageMean + StageSigma * ZGrid[n - 1];
        for (int i = 1; i < n; i++)
        {
            double ui = Normal.StandardCDF(ZGrid[i]);
            if (nonExceedance <= ui)
            {
                double uPrev = Normal.StandardCDF(ZGrid[i - 1]);
                double t = (nonExceedance - uPrev) / (ui - uPrev);
                double stagePrev = StageMean + StageSigma * ZGrid[i - 1];
                double stageNext = StageMean + StageSigma * ZGrid[i];
                return stagePrev + t * (stageNext - stagePrev);
            }
        }
        return StageMean + StageSigma * ZGrid[n - 1];
    }

    /// <summary>A clamped linear ramp — the oracle's fragility and consequence form.</summary>
    private static double Ramp(double x, double start, double end, double scale)
    {
        if (x <= start) return 0d;
        if (x >= end) return scale;
        return scale * (x - start) / (end - start);
    }

    /// <summary>The oracle's per-mode quantities at one non-exceedance ordinate.</summary>
    private static void OracleModes(double u, out double pA, out double pB, out double cA, out double cB, out double nf)
    {
        double stage = OracleStage(u);
        pA = Ramp(stage, FragAStart, FragAEnd, 1d);
        pB = Ramp(stage, FragBStart, FragBEnd, 1d);
        cA = Ramp(stage, ConsequenceStart, ConsequenceEnd, ConsequenceAScale);
        cB = Ramp(stage, ConsequenceStart, ConsequenceEnd, ConsequenceBScale);
        nf = Ramp(stage, ConsequenceStart, ConsequenceEnd, NonFailScale);
    }

    /// <summary>Dense-trapezoid oracle integral over non-exceedance ∈ [floor, 1 − floor].</summary>
    private static double OracleIntegral(Func<double, double> integrand)
    {
        double lower = ProbabilityFloor;
        double upper = 1d - ProbabilityFloor;
        double step = (upper - lower) / (OracleOrdinates - 1);
        double sum = 0.5d * (integrand(lower) + integrand(upper));
        for (int i = 1; i < OracleOrdinates - 1; i++)
        {
            sum += integrand(lower + i * step);
        }
        return sum * step;
    }

    /// <summary>
    /// The oracle's competing cumulative incidence functions over the sampled hazard domain —
    /// the tech note's Eq. 14 rectangle rule at high resolution: CIF_j(x) accumulates
    /// [S_j(x_{k−1})·Π_{m≠j} S_m(x̄_k) − S_j(x_k)·Π_{m≠j} S_m(x̄_k)] over the bins up to x.
    /// </summary>
    /// <param name="stages">Receives the bin-edge stages.</param>
    /// <param name="cifA">Receives mode A's cumulative incidence at each edge.</param>
    /// <param name="cifB">Receives mode B's cumulative incidence at each edge.</param>
    private static void OracleIncidence(out double[] stages, out double[] cifA, out double[] cifB)
    {
        double minStage = OracleStage(ProbabilityFloor);
        double maxStage = OracleStage(1d - ProbabilityFloor);
        stages = new double[OracleIncidenceBins + 1];
        cifA = new double[OracleIncidenceBins + 1];
        cifB = new double[OracleIncidenceBins + 1];
        double step = (maxStage - minStage) / OracleIncidenceBins;
        double accumulatedA = 0d, accumulatedB = 0d;
        stages[0] = minStage;
        for (int k = 1; k <= OracleIncidenceBins; k++)
        {
            double x0 = minStage + (k - 1) * step;
            double x1 = minStage + k * step;
            double mid = 0.5d * (x0 + x1);
            double survivalA0 = 1d - Ramp(x0, FragAStart, FragAEnd, 1d);
            double survivalA1 = 1d - Ramp(x1, FragAStart, FragAEnd, 1d);
            double survivalB0 = 1d - Ramp(x0, FragBStart, FragBEnd, 1d);
            double survivalB1 = 1d - Ramp(x1, FragBStart, FragBEnd, 1d);
            double survivalAMid = 1d - Ramp(mid, FragAStart, FragAEnd, 1d);
            double survivalBMid = 1d - Ramp(mid, FragBStart, FragBEnd, 1d);
            accumulatedA += (survivalA0 - survivalA1) * survivalBMid;
            accumulatedB += (survivalB0 - survivalB1) * survivalAMid;
            stages[k] = x1;
            cifA[k] = accumulatedA;
            cifB[k] = accumulatedB;
        }
    }

    /// <summary>Linear lookup into the oracle incidence table.</summary>
    private static double IncidenceAt(double stage, double[] stages, double[] cif)
    {
        if (stage <= stages[0]) return 0d;
        if (stage >= stages[stages.Length - 1]) return cif[cif.Length - 1];
        double step = stages[1] - stages[0];
        int index = (int)((stage - stages[0]) / step);
        if (index >= stages.Length - 1) return cif[cif.Length - 1];
        double t = (stage - stages[index]) / step;
        return cif[index] + t * (cif[index + 1] - cif[index]);
    }

    #endregion

    /// <summary>
    /// Verifies the per-mode contributions against the adjusted-marginal quadrature oracle for
    /// the mutually-exclusive and common-cause methods, and against the additive-rule joint
    /// attribution identity: for each mode, contribution = ∫ adj_j dF, ∫ adj_j·c_j dF, and
    /// ∫ adj_j·ē_j dF with the method's own adjustment formula — plus the within-run additivity
    /// identities at floating-point association.
    /// </summary>
    [TestMethod]
    public void Test_Contribution_MEAndCCA_VsQuadratureOracle()
    {
        foreach (FailureModeMethod method in new[] { FailureModeMethod.MutuallyExclusive, FailureModeMethod.CommonCauseFailures })
        {
            // Arrange the method's adjustment formula for the oracle.
            Func<double, double, (double AdjustedA, double AdjustedB)> adjust = method == FailureModeMethod.MutuallyExclusive
                ? (pA, pB) =>
                {
                    double sum = pA + pB;
                    double normalization = sum > 1d ? 1d / sum : 1d;
                    return (pA * normalization, pB * normalization);
                }
            : (pA, pB) =>
                {
                    double sum = pA + pB;
                    if (sum <= 0d) return (0d, 0d);
                    double union = 1d - (1d - pA) * (1d - pB);
                    double factor = union / sum;
                    return (pA * factor, pB * factor);
                };

            // Act
            var analysis = RunMeanOnly(Component(method));
            var component = analysis.MeanRiskResults!.Components[0];
            var a = component.FailureModes[0].Contribution!;
            var b = component.FailureModes[1].Contribution!;

            // Oracle integrals.
            double oracleProbabilityA = OracleIntegral(u => { OracleModes(u, out double pA, out double pB, out _, out _, out _); return adjust(pA, pB).AdjustedA; });
            double oracleProbabilityB = OracleIntegral(u => { OracleModes(u, out double pA, out double pB, out _, out _, out _); return adjust(pA, pB).AdjustedB; });
            double oracleFailureA = OracleIntegral(u => { OracleModes(u, out double pA, out double pB, out double cA, out _, out _); return adjust(pA, pB).AdjustedA * cA; });
            double oracleFailureB = OracleIntegral(u => { OracleModes(u, out double pA, out double pB, out _, out double cB, out _); return adjust(pA, pB).AdjustedB * cB; });
            double oracleExcessA = OracleIntegral(u => { OracleModes(u, out double pA, out double pB, out double cA, out _, out double nf); return adjust(pA, pB).AdjustedA * Math.Max(0d, cA - nf); });
            double oracleExcessB = OracleIntegral(u => { OracleModes(u, out double pA, out double pB, out _, out double cB, out double nf); return adjust(pA, pB).AdjustedB * Math.Max(0d, cB - nf); });

            // Assert — engine vs oracle at the mass-accounting residual envelope.
            Assert.AreEqual(oracleProbabilityA, a.FailureProbability, 1e-4 * oracleProbabilityA, $"{method}: mode A probability contribution.");
            Assert.AreEqual(oracleProbabilityB, b.FailureProbability, 1e-4 * oracleProbabilityB, $"{method}: mode B probability contribution.");
            Assert.AreEqual(oracleFailureA, a.FailureMean, 1e-4 * oracleFailureA, $"{method}: mode A failure-mean contribution.");
            Assert.AreEqual(oracleFailureB, b.FailureMean, 1e-4 * oracleFailureB, $"{method}: mode B failure-mean contribution.");
            Assert.AreEqual(oracleExcessA, a.ExcessMean, 1e-4 * oracleExcessA, $"{method}: mode A excess-mean contribution.");
            Assert.AreEqual(oracleExcessB, b.ExcessMean, 1e-4 * oracleExcessB, $"{method}: mode B excess-mean contribution.");

            // The within-run additivity identities.
            Assert.AreEqual(component.Curves.Fail.MassBalance, a.FailureProbability + b.FailureProbability,
                1e-12 * component.Curves.Fail.MassBalance, $"{method}: Σ probability ≡ the Fail mass balance.");
            Assert.AreEqual(component.Curves.Fail.Mean, a.FailureMean + b.FailureMean,
                1e-12 * component.Curves.Fail.Mean, $"{method}: Σ failure means ≡ the Fail mean.");
            Assert.AreEqual(component.Curves.Excess.Mean, a.ExcessMean + b.ExcessMean,
                1e-12 * component.Curves.Excess.Mean, $"{method}: Σ excess means ≡ the Excess mean.");
        }
    }

    /// <summary>
    /// Verifies the competing-method contributions against the tech note's Eq. 14 cumulative
    /// incidence oracle at 20,001 bins: the engine's 200-bin CIF pre-processing (the v1.0
    /// constant) carries a documented discretization allowance of 1e-2 relative, while the
    /// within-run additivity identities stay exact.
    /// </summary>
    [TestMethod]
    public void Test_Contribution_Competing_VsIncidenceOracle()
    {
        // Arrange / Act
        var analysis = RunMeanOnly(Component(FailureModeMethod.CompetingFailures));
        var component = analysis.MeanRiskResults!.Components[0];
        var a = component.FailureModes[0].Contribution!;
        var b = component.FailureModes[1].Contribution!;

        // Oracle: incidence functions then the quadrature.
        OracleIncidence(out double[] stages, out double[] cifA, out double[] cifB);
        double oracleProbabilityA = OracleIntegral(u => IncidenceAt(OracleStage(u), stages, cifA));
        double oracleFailureA = OracleIntegral(u =>
        {
            double stage = OracleStage(u);
            return IncidenceAt(stage, stages, cifA) * Ramp(stage, ConsequenceStart, ConsequenceEnd, ConsequenceAScale);
        });
        double oracleExcessB = OracleIntegral(u =>
        {
            double stage = OracleStage(u);
            double excess = Math.Max(0d, Ramp(stage, ConsequenceStart, ConsequenceEnd, ConsequenceBScale) - Ramp(stage, ConsequenceStart, ConsequenceEnd, NonFailScale));
            return IncidenceAt(stage, stages, cifB) * excess;
        });

        // Assert — engine vs oracle at the documented CIF-discretization allowance.
        Assert.AreEqual(oracleProbabilityA, a.FailureProbability, 1e-2 * oracleProbabilityA,
            "Mode A's probability contribution vs the incidence oracle (200-bin engine CIF allowance).");
        Assert.AreEqual(oracleFailureA, a.FailureMean, 1e-2 * oracleFailureA,
            "Mode A's failure-mean contribution vs the incidence oracle.");
        Assert.AreEqual(oracleExcessB, b.ExcessMean, 1e-2 * oracleExcessB,
            "Mode B's excess-mean contribution vs the incidence oracle.");

        // Exact within-run identities regardless of the discretization.
        Assert.AreEqual(component.Curves.Fail.MassBalance, a.FailureProbability + b.FailureProbability,
            1e-12 * component.Curves.Fail.MassBalance, "Σ probability ≡ the Fail mass balance.");
        Assert.AreEqual(component.Curves.Fail.Mean, a.FailureMean + b.FailureMean,
            1e-12 * component.Curves.Fail.Mean, "Σ failure means ≡ the Fail mean.");
    }

    /// <summary>
    /// Verifies the joint-method contributions: under the Sum rule the attribution equals the
    /// marginal integrals ∫ p_j·c_j dF exactly (the consequence-proportional split credits each
    /// mode its own value), cross-checked against the quadrature oracle; and under the Maximum
    /// rule the within-run identities hold while the dominant-consequence mode absorbs the
    /// larger share.
    /// </summary>
    [TestMethod]
    public void Test_Contribution_Joint_SumRuleOracle_AndMaximumOrdering()
    {
        // Sum rule: attribution ≡ the marginal quadrature.
        var additive = RunMeanOnly(Component(FailureModeMethod.JointFailures, jointConsequences: JointConsequenceType.Additive));
        var additiveComponent = additive.MeanRiskResults!.Components[0];
        double oracleMarginalA = OracleIntegral(u => { OracleModes(u, out double pA, out _, out double cA, out _, out _); return pA * cA; });
        double oracleMarginalB = OracleIntegral(u => { OracleModes(u, out _, out double pB, out _, out double cB, out _); return pB * cB; });
        Assert.AreEqual(oracleMarginalA, additiveComponent.FailureModes[0].Contribution!.FailureMean, 1e-4 * oracleMarginalA,
            "Sum-rule mode A attribution ≡ ∫ p_A·c_A dF.");
        Assert.AreEqual(oracleMarginalB, additiveComponent.FailureModes[1].Contribution!.FailureMean, 1e-4 * oracleMarginalB,
            "Sum-rule mode B attribution ≡ ∫ p_B·c_B dF.");

        // The Shapley probability attribution vs its own oracle: ∫ [p_j(1−p_other) + p_A·p_B/2] dF.
        double oracleShapleyA = OracleIntegral(u => { OracleModes(u, out double pA, out double pB, out _, out _, out _); return pA * (1d - pB) + 0.5d * pA * pB; });
        Assert.AreEqual(oracleShapleyA, additiveComponent.FailureModes[0].Contribution!.FailureProbability, 1e-4 * oracleShapleyA,
            "The joint probability attribution ≡ the Shapley integral.");

        // Maximum rule: exact identities + the attribution vs its own quadrature oracle — the
        // proportional split of the pathway maximum (c_B = 2·c_A wherever positive, so the
        // both-fail pathway splits 1:2).
        var maximum = RunMeanOnly(Component(FailureModeMethod.JointFailures, jointConsequences: JointConsequenceType.Maximum));
        var maximumComponent = maximum.MeanRiskResults!.Components[0];
        var a = maximumComponent.FailureModes[0].Contribution!;
        var b = maximumComponent.FailureModes[1].Contribution!;
        Assert.AreEqual(maximumComponent.Curves.Fail.MassBalance, a.FailureProbability + b.FailureProbability,
            1e-12 * maximumComponent.Curves.Fail.MassBalance, "Maximum rule: Σ probability ≡ the Fail mass balance.");
        Assert.AreEqual(maximumComponent.Curves.Fail.Mean, a.FailureMean + b.FailureMean,
            1e-12 * maximumComponent.Curves.Fail.Mean, "Maximum rule: Σ failure means ≡ the Fail mean.");

        double oracleMaximumA = OracleIntegral(u =>
        {
            OracleModes(u, out double pA, out double pB, out double cA, out double cB, out _);
            double sum = cA + cB;
            double shareA = sum > 0d ? cA / sum : 0.5d;
            return pA * (1d - pB) * cA + pA * pB * Math.Max(cA, cB) * shareA;
        });
        double oracleMaximumB = OracleIntegral(u =>
        {
            OracleModes(u, out double pA, out double pB, out double cA, out double cB, out _);
            double sum = cA + cB;
            double shareB = sum > 0d ? cB / sum : 0.5d;
            return (1d - pA) * pB * cB + pA * pB * Math.Max(cA, cB) * shareB;
        });
        Assert.AreEqual(oracleMaximumA, a.FailureMean, 1e-4 * oracleMaximumA,
            "Maximum-rule mode A attribution vs the proportional-split quadrature oracle.");
        Assert.AreEqual(oracleMaximumB, b.FailureMean, 1e-4 * oracleMaximumB,
            "Maximum-rule mode B attribution vs the proportional-split quadrature oracle.");
    }

    /// <summary>
    /// Verifies the additive system attribution at three components: the closed-form
    /// Poisson–binomial Shapley split matches the brute-force 2³ exclusive enumeration at
    /// floating-point association, Σ shares ≡ the folded union, Σ mean contributions ≡ the
    /// convolved system means, and the attribution is invariant to component declaration order
    /// (the additive path's reorder contract).
    /// </summary>
    [TestMethod]
    public void Test_Contribution_AdditiveSystem_BruteForce_AndReorderInvariance()
    {
        // Arrange / Act — three scaled components, then the same system declared reversed.
        var analysis = RunMeanOnly(
            Component(FailureModeMethod.JointFailures, 1d),
            Component(FailureModeMethod.JointFailures, 2d),
            Component(FailureModeMethod.JointFailures, 3d));
        var reversed = RunMeanOnly(
            Component(FailureModeMethod.JointFailures, 3d),
            Component(FailureModeMethod.JointFailures, 2d),
            Component(FailureModeMethod.JointFailures, 1d));

        // Brute-force Shapley over the exclusive combinations.
        var components = analysis.MeanRiskResults!.Components;
        var probabilities = new double[3];
        for (int i = 0; i < 3; i++) probabilities[i] = components[i].Curves.Fail.TotalProbability;
        var expected = new double[3];
        for (int mask = 1; mask < 8; mask++)
        {
            double mass = 1d;
            int participants = 0;
            for (int i = 0; i < 3; i++)
            {
                bool fails = (mask & (1 << i)) != 0;
                mass *= fails ? probabilities[i] : 1d - probabilities[i];
                if (fails) participants++;
            }
            for (int i = 0; i < 3; i++)
            {
                if ((mask & (1 << i)) != 0) expected[i] += mass / participants;
            }
        }

        // Assert.
        double sumShares = 0d, sumMeans = 0d, sumExcess = 0d;
        for (int i = 0; i < 3; i++)
        {
            var contribution = components[i].SystemContribution!;
            Assert.AreEqual(expected[i], contribution.FailureProbability, 1e-12, $"Component {i} Shapley share vs brute force.");
            sumShares += contribution.FailureProbability;
            sumMeans += contribution.FailureMean;
            sumExcess += contribution.ExcessMean;
        }
        var system = analysis.MeanRiskResults.Curves;
        Assert.AreEqual(system.Fail.TotalProbability, sumShares, 1e-12, "Σ shares ≡ the folded union.");
        Assert.AreEqual(system.Fail.Mean, sumMeans, 1e-9 * system.Fail.Mean, "Σ mean contributions ≡ the convolved system Fail mean.");
        Assert.AreEqual(system.Excess.Mean, sumExcess, 1e-9 * system.Excess.Mean, "Σ excess contributions ≡ the convolved system Excess mean.");

        // Reorder invariance: the ×1 component's attribution is identical wherever declared.
        Assert.AreEqual(components[0].SystemContribution!.FailureProbability,
            reversed.MeanRiskResults!.Components[2].SystemContribution!.FailureProbability, 0d,
            "The additive attribution must be declaration-order-inert (the 4b reorder contract).");
        Assert.AreEqual(components[0].SystemContribution!.FailureMean,
            reversed.MeanRiskResults.Components[2].SystemContribution!.FailureMean, 0d);
    }

    /// <summary>
    /// Verifies the joint system attribution identities and reproducibility at the default
    /// VEGAS budget: Σ component contributions ≡ the recorded system totals within the run,
    /// and repeated runs reproduce every contribution bit-for-bit (the content-seeded stream).
    /// </summary>
    [TestMethod]
    public void Test_Contribution_JointSystem_Identities_AndReproducibility()
    {
        RiskAnalysis Run()
        {
            var analysis = new RiskAnalysis(new[]
            {
                Component(FailureModeMethod.JointFailures, 1d),
                Component(FailureModeMethod.JointFailures, 2d),
            });
            analysis.Options.SystemRiskMethod = SystemRiskType.JointRiskMethod;
            analysis.RunAsync().GetAwaiter().GetResult();
            Assert.IsTrue(analysis.IsEstimated);
            return analysis;
        }

        // Act
        var first = Run();
        var second = Run();

        // Assert — within-run identities.
        var components = first.MeanRiskResults!.Components;
        double sumProbability = 0d, sumFailure = 0d, sumExcess = 0d;
        for (int i = 0; i < components.Count; i++)
        {
            var contribution = components[i].SystemContribution!;
            sumProbability += contribution.FailureProbability;
            sumFailure += contribution.FailureMean;
            sumExcess += contribution.ExcessMean;
        }
        var system = first.MeanRiskResults.Curves;
        Assert.AreEqual(system.Fail.MassBalance, sumProbability, 1e-12 * system.Fail.MassBalance,
            "Σ probability contributions ≡ the recorded system Fail mass balance.");
        Assert.AreEqual(system.Fail.Mean, sumFailure, 1e-12 * system.Fail.Mean,
            "Σ failure-mean contributions ≡ the recorded system Fail mean.");
        Assert.AreEqual(system.Excess.Mean, sumExcess, 1e-12 * Math.Max(1d, system.Excess.Mean),
            "Σ excess-mean contributions ≡ the recorded system Excess mean.");

        // Mode-level contributions finalize under the weight regime.
        Assert.IsNotNull(components[0].FailureModes[0].Contribution);

        // Reproducibility — bit-identical across runs (thread scheduling varies freely).
        for (int i = 0; i < components.Count; i++)
        {
            Assert.AreEqual(components[i].SystemContribution!.FailureProbability,
                second.MeanRiskResults!.Components[i].SystemContribution!.FailureProbability, 0d,
                $"Component {i}'s joint attribution must reproduce bit-for-bit.");
            Assert.AreEqual(components[i].FailureModes[0].Contribution!.FailureMean,
                second.MeanRiskResults.Components[i].FailureModes[0].Contribution!.FailureMean, 0d,
                $"Component {i}'s mode attribution must reproduce bit-for-bit.");
        }
    }
}
