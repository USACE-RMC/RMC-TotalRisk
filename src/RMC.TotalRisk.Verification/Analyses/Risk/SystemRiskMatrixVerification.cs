using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics;
using Numerics.Data;
using Numerics.Distributions;
using Numerics.Sampling;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.Results;
using RMC.TotalRisk.Systems.Components;

namespace RMC.TotalRisk.Verification.Analyses.Risk;

/// <summary>
/// Multi-component system risk across the correlation × aggregation matrix — the
/// conversion of the legacy <c>Test_MC_SystemRisk</c> family: 2 components with 2 failure modes
/// each (additive rule), and 2 or 5 components with 1 failure mode each across all four
/// joint-consequence rules, under the four component-hazard dependency options, verified against
/// independent brute-force Monte Carlo oracles at the legacy seeds and pinned to the 2024
/// verification report's published constants (tables 77–103).
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// <para>
/// <b>Scenario</b> (the shared legacy Bucket-1 model, system form): every component draws its
/// own hazard from LnNormal(85, 20) through a correlated latent Gaussian; component <c>c</c>
/// carries the legacy fragility Φ((h − μ_c)/σ_c) and failure-consequence curve of PFM-(c+1)
/// (the 2-per-component family carries PFM-1 AND PFM-2 in both components), and every component
/// shares the legacy non-failure curve. Engine and oracle interpolate the SAME dense z-grid
/// tables (hazard step 0.1, fragilities step 0.05σ, ±8 range) so tabulation error cancels from
/// the engine-versus-oracle asserts.
/// </para>
/// <para>
/// <b>System-state semantics</b> (identical legacy and engine): per annual event each failed
/// component contributes its failure consequence and each surviving component its non-failure
/// consequence; the across-component combination applies the joint-consequence rule separately
/// to the failed set (Fail), the excess of the failed set (Excess/incremental), the surviving
/// set (NonFail), and all components (Background); Total = Fail + NonFail per event. The union
/// of any component failing is the system annualized failure probability.
/// </para>
/// <para>
/// <b>Consolidation:</b> the 36 legacy methods share identical sampling streams within each
/// (family, dependency) group — the combination rule only changes the aggregation arithmetic —
/// so each group test runs ONE oracle pass that accumulates all rules from the same draws
/// (bit-identical to the separate legacy passes at the same seeds). Legacy seeds preserved:
/// the 1-failure-mode families draw hazards from <c>MultivariateNormal.GenerateRandomValues(N,
/// 67891)</c> and capacities from <c>MersenneTwister(12345).NextDoubles(N, D)</c> (one column
/// per component); the 2-failure-mode family draws hazards with seed 78910 and per-component
/// capacity streams from seeds 12345 and 45678. N = 1,000,000 (the legacy 10M dropped 10× per
/// the conversion policy). One legacy method name is mislabeled
/// (<c>Test_5_Component_5_PFM_JointFailures_Negative_Minimum</c> is the 5-component
/// 1-failure-mode negative minimum body) — ported from the body per the conversion policy.
/// </para>
/// <para>
/// <b>Engine mapping:</b> the Independent groups run the ADDITIVE system method (deterministic
/// Gauss–Kronrod + exact lattice convolution — asserted on the full joint-family assert catalog) for the
/// additive rule, and the JOINT method (VEGAS with the engine-level combination enumeration,
/// tail focus off) for every rule. The dependent groups run the joint method only — the v1.1
/// additive method is defined for strictly independent components. Dependencies map to the
/// engine options: r = 0 → Independent, r = 1 − √ε → PerfectlyPositive, r = −1/(D−1) + √ε →
/// PerfectlyNegative, r = 0.5 → CorrelationMatrix. <b>Documented deviation:</b> the legacy
/// 2-component 2-failure-mode Positive/Negative bodies used r = 1 − ε and −1 + ε; this port
/// uses the engine constants 1 − √ε and −1 + √ε (statistically indistinguishable,
/// Cholesky-stable — the same deliberately accepted deviation as the 5-PFM joint family).
/// </para>
/// <para>
/// <b>Tolerances</b> (docs/verification.md): oracle-side standard errors computed in-run (mean
/// SE = σ̂/√N; σ SE by the delta method; probability SEs binomial). The additive engine is
/// deterministic, so its asserts carry oracle error only, with the documented lattice
/// allowances on the tail measures (value-at-risk compared in probability space with the
/// documented output-resolution slack; conditional value-at-risk at k·SE plus a 0.5% lattice/thinning
/// envelope). The joint engine is itself Monte Carlo: stream means combine the oracle SE with
/// the run's reported VEGAS standard error (a conservative proxy for every stream), and
/// probability asserts combine binomially at the recorded evaluation count (5 recording passes
/// × the final evaluations) — the joint-method convention. Report pins use the report's own 10M
/// sampling error (4·σ̂/√10⁷) plus a 0.1% tabulation allowance, plus the engine VEGAS error on
/// joint-method pins.
/// </para>
/// </remarks>
[TestClass]
public class SystemRiskMatrixVerification
{
    /// <summary>The oracle realization count (legacy 10M dropped 10× per the conversion policy).</summary>
    private const int OracleRealizations = 1_000_000;

    /// <summary>The legacy hazard-draw multivariate-normal seed of the 1-failure-mode families.</summary>
    private const int OneModeHazardSeed = 67891;

    /// <summary>The legacy hazard-draw multivariate-normal seed of the 2-failure-mode family.</summary>
    private const int TwoModeHazardSeed = 78910;

    /// <summary>The legacy capacity-stream seed (single stream; also the first per-component stream).</summary>
    private const int CapacitySeed = 12345;

    /// <summary>The legacy second per-component capacity-stream seed of the 2-failure-mode family.</summary>
    private const int SecondCapacitySeed = 45678;

    /// <summary>The tolerance multiplier on the Monte Carlo standard error.</summary>
    private const double K = 4d;

    /// <summary>The exceedance level for the value-at-risk and conditional value-at-risk asserts (the engine default).</summary>
    private const double Alpha = 0.01d;

    /// <summary>The consequence threshold behind the assurance-measure assert.</summary>
    private const double Threshold = 100d;

    /// <summary>The hazard z-grid step (±8 range).</summary>
    private const double HazardZStep = 0.1d;

    /// <summary>The fragility z-grid step (±8σ range).</summary>
    private const double FragilityZStep = 0.05d;

    /// <summary>The z-grid half-range of every tabulated curve.</summary>
    private const double ZRange = 8d;

    /// <summary>The legacy fragility means for PFM-1..PFM-5.</summary>
    private static readonly double[] FragilityMeans = { 140d, 160d, 150d, 130d, 160d };

    /// <summary>The legacy fragility standard deviations for PFM-1..PFM-5.</summary>
    private static readonly double[] FragilitySds = { 30d, 10d, 20d, 35d, 15d };

    /// <summary>The legacy consequence-curve stages.</summary>
    private static readonly double[] ConsequenceStages = { 60d, 100d, 140d, 200d, 250d };

    /// <summary>The legacy failure-consequence ordinates for PFM-1..PFM-5.</summary>
    private static readonly double[][] FailureValues =
    {
        new[] { 0d, 5d, 50d, 500d, 750d },
        new[] { 0d, 3d, 30d, 300d, 450d },
        new[] { 0d, 10d, 100d, 1000d, 1500d },
        new[] { 0d, 2d, 20d, 200d, 300d },
        new[] { 0d, 8d, 80d, 800d, 1200d },
    };

    /// <summary>The legacy non-failure-consequence ordinates (shared by every component).</summary>
    private static readonly double[] NonFailureValues = { 0d, 1d, 10d, 100d, 150d };

    /// <summary>The joint-consequence rules in engine declaration order, driving every per-rule array.</summary>
    private static readonly JointConsequenceType[] Rules =
    {
        JointConsequenceType.Additive, JointConsequenceType.Average, JointConsequenceType.Maximum, JointConsequenceType.Minimum,
    };

    /// <summary>The 1-failure-mode-per-component mode layout for two components (PFM-1, PFM-2).</summary>
    private static readonly int[][] TwoComponentModes = { new[] { 0 }, new[] { 1 } };

    /// <summary>The 2-failure-modes-per-component layout for two components (PFM-1 and PFM-2 in both).</summary>
    private static readonly int[][] TwoComponentTwoModes = { new[] { 0, 1 }, new[] { 0, 1 } };

    /// <summary>The 1-failure-mode-per-component layout for five components (PFM-1..PFM-5).</summary>
    private static readonly int[][] FiveComponentModes = { new[] { 0 }, new[] { 1 }, new[] { 2 }, new[] { 3 }, new[] { 4 } };

    /// <summary>
    /// The 2024 report's published Monte Carlo constants for the system matrix (tables 77–103),
    /// keyed by (family, dependency, rule) with values ordered {incremental, background, total,
    /// failure, non-failure}. Only Independent, PerfectlyPositive, and PerfectlyNegative were
    /// published; the correlation-matrix groups are legacy-only scenarios.
    /// </summary>
    private static readonly Dictionary<(string Family, DependencyType Dependency, JointConsequenceType Rule), double[]> ReportConstants = new()
    {
        // Tables 77–80: 2 independent components, 1 failure mode each.
        [("2C1P", DependencyType.Independent, JointConsequenceType.Additive)] = new[] { 2.017288d, 2.855437d, 4.872725d, 2.593366d, 2.279360d },
        [("2C1P", DependencyType.Independent, JointConsequenceType.Average)] = new[] { 2.004752d, 1.427719d, 3.758951d, 2.575244d, 1.183707d },
        [("2C1P", DependencyType.Independent, JointConsequenceType.Maximum)] = new[] { 2.013030d, 2.416342d, 4.487518d, 2.587785d, 1.899734d },
        [("2C1P", DependencyType.Independent, JointConsequenceType.Minimum)] = new[] { 1.996474d, 0.439095d, 3.030384d, 2.562703d, 0.467681d },
        // Tables 81–84: 2 positively dependent components, 1 failure mode each.
        [("2C1P", DependencyType.PerfectlyPositive, JointConsequenceType.Additive)] = new[] { 2.017594d, 2.856296d, 4.873890d, 2.593824d, 2.280066d },
        [("2C1P", DependencyType.PerfectlyPositive, JointConsequenceType.Average)] = new[] { 1.664981d, 1.428148d, 3.434284d, 2.123673d, 1.310610d },
        [("2C1P", DependencyType.PerfectlyPositive, JointConsequenceType.Maximum)] = new[] { 1.782519d, 1.428148d, 3.551821d, 2.241211d, 1.310610d },
        [("2C1P", DependencyType.PerfectlyPositive, JointConsequenceType.Minimum)] = new[] { 1.547443d, 1.428148d, 3.316746d, 2.006136d, 1.310610d },
        // Tables 85–88: 2 negatively dependent components, 1 failure mode each.
        [("2C1P", DependencyType.PerfectlyNegative, JointConsequenceType.Additive)] = new[] { 2.014946d, 2.852113d, 4.867058d, 2.589851d, 2.277207d },
        [("2C1P", DependencyType.PerfectlyNegative, JointConsequenceType.Average)] = new[] { 2.014888d, 1.426056d, 3.735204d, 2.589764d, 1.145439d },
        [("2C1P", DependencyType.PerfectlyNegative, JointConsequenceType.Maximum)] = new[] { 2.014946d, 2.593128d, 4.617460d, 2.589851d, 2.027609d },
        [("2C1P", DependencyType.PerfectlyNegative, JointConsequenceType.Minimum)] = new[] { 2.014830d, 0.258984d, 2.852948d, 2.589678d, 0.263270d },
        // Tables 89–91: 2 components with 2 independent failure modes each, additive rule.
        [("2C2P", DependencyType.Independent, JointConsequenceType.Additive)] = new[] { 4.265464d, 2.852090d, 7.117553d, 5.182284d, 1.935270d },
        [("2C2P", DependencyType.PerfectlyPositive, JointConsequenceType.Additive)] = new[] { 4.269872d, 2.853251d, 7.123123d, 5.187768d, 1.935355d },
        [("2C2P", DependencyType.PerfectlyNegative, JointConsequenceType.Additive)] = new[] { 4.278800d, 2.854866d, 7.133666d, 5.197708d, 1.935957d },
        // Tables 92–95: 5 independent components, 1 failure mode each.
        [("5C1P", DependencyType.Independent, JointConsequenceType.Additive)] = new[] { 6.108527d, 7.127700d, 13.236227d, 7.681828d, 5.554399d },
        [("5C1P", DependencyType.Independent, JointConsequenceType.Average)] = new[] { 5.568252d, 1.425540d, 8.189262d, 7.025408d, 1.163855d },
        [("5C1P", DependencyType.Independent, JointConsequenceType.Maximum)] = new[] { 6.025967d, 4.598846d, 10.968579d, 7.558594d, 3.409985d },
        [("5C1P", DependencyType.Independent, JointConsequenceType.Minimum)] = new[] { 5.098583d, 0.153408d, 6.633722d, 6.478531d, 0.155191d },
        // Tables 96–99: 5 positively dependent components, 1 failure mode each.
        [("5C1P", DependencyType.PerfectlyPositive, JointConsequenceType.Additive)] = new[] { 6.143270d, 7.140574d, 13.283844d, 7.723975d, 5.559869d },
        [("5C1P", DependencyType.PerfectlyPositive, JointConsequenceType.Average)] = new[] { 2.633103d, 1.428115d, 4.774072d, 3.412262d, 1.361811d },
        [("5C1P", DependencyType.PerfectlyPositive, JointConsequenceType.Maximum)] = new[] { 3.878425d, 1.428115d, 6.019395d, 4.657584d, 1.361811d },
        [("5C1P", DependencyType.PerfectlyPositive, JointConsequenceType.Minimum)] = new[] { 1.516603d, 1.426499d, 3.656122d, 2.294723d, 1.361399d },
        // Tables 100–103: 5 negatively dependent components, 1 failure mode each.
        [("5C1P", DependencyType.PerfectlyNegative, JointConsequenceType.Additive)] = new[] { 6.095003d, 7.125940d, 13.220943d, 7.666340d, 5.554603d },
        [("5C1P", DependencyType.PerfectlyNegative, JointConsequenceType.Average)] = new[] { 5.829741d, 1.425188d, 8.492369d, 7.344564d, 1.147805d },
        [("5C1P", DependencyType.PerfectlyNegative, JointConsequenceType.Maximum)] = new[] { 6.070426d, 4.965946d, 11.280812d, 7.627586d, 3.653226d },
        [("5C1P", DependencyType.PerfectlyNegative, JointConsequenceType.Minimum)] = new[] { 5.590722d, 0.100801d, 7.165477d, 7.063415d, 0.102062d },
    };

    #region Shared Tables and Builders

    /// <summary>Builds the shared hazard table: non-exceedance probabilities (ascending) and stages from LnNormal(85, 20).</summary>
    private static (double[] Probabilities, double[] Stages) HazardTable()
    {
        var hazard = new LnNormal(85d, 20d);
        int count = (int)Math.Round(2d * ZRange / HazardZStep) + 1;
        var probabilities = new double[count];
        var stages = new double[count];
        for (int i = 0; i < count; i++)
        {
            double z = -ZRange + i * HazardZStep;
            probabilities[i] = Normal.StandardCDF(z);
            stages[i] = hazard.InverseCDF(probabilities[i]);
        }
        return (probabilities, stages);
    }

    /// <summary>Builds one fragility table: stages and failure probabilities from Φ((h − mean)/sd).</summary>
    /// <param name="mode">The zero-based failure-mode index into the legacy parameters.</param>
    private static (double[] Stages, double[] Probabilities) FragilityTable(int mode)
    {
        int count = (int)Math.Round(2d * ZRange / FragilityZStep) + 1;
        var stages = new double[count];
        var probabilities = new double[count];
        for (int i = 0; i < count; i++)
        {
            double z = -ZRange + i * FragilityZStep;
            stages[i] = FragilityMeans[mode] + FragilitySds[mode] * z;
            probabilities[i] = Normal.StandardCDF(z);
        }
        return (stages, probabilities);
    }

    /// <summary>The oracle's own linear interpolator with end clamping (x ascending).</summary>
    /// <param name="xValues">The abscissae, ascending.</param>
    /// <param name="yValues">The ordinates.</param>
    /// <param name="x">The lookup abscissa.</param>
    /// <returns>The interpolated ordinate.</returns>
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

    /// <summary>Builds a uniform equicorrelated matrix with unit diagonal.</summary>
    /// <param name="dimension">The matrix dimension.</param>
    /// <param name="offDiagonal">The shared off-diagonal correlation.</param>
    private static double[,] Equicorrelated(int dimension, double offDiagonal)
    {
        var matrix = new double[dimension, dimension];
        for (int i = 0; i < dimension; i++)
        {
            for (int j = 0; j < dimension; j++)
            {
                matrix[i, j] = i == j ? 1d : offDiagonal;
            }
        }
        return matrix;
    }

    /// <summary>The engine's dependency off-diagonal for a group (the v1.0 constants the engine rebuilds internally).</summary>
    /// <param name="dependency">The dependency option.</param>
    /// <param name="dimension">The component count.</param>
    /// <returns>The off-diagonal correlation the oracle must realize.</returns>
    private static double DependencyOffDiagonal(DependencyType dependency, int dimension)
    {
        return dependency switch
        {
            DependencyType.PerfectlyPositive => 1d - Math.Sqrt(Tools.DoubleMachineEpsilon),
            DependencyType.PerfectlyNegative => -1d / (dimension - 1) + Math.Sqrt(Tools.DoubleMachineEpsilon),
            DependencyType.CorrelationMatrix => 0.5d,
            _ => 0d,
        };
    }

    /// <summary>Builds one tabular consequence over the legacy five-knot stages.</summary>
    /// <param name="name">The function name.</param>
    /// <param name="values">The consequence ordinates.</param>
    private static TabularConsequence Consequence(string name, double[] values)
    {
        var ordinates = new UncertainOrdinate[ConsequenceStages.Length];
        for (int i = 0; i < ConsequenceStages.Length; i++)
        {
            ordinates[i] = new UncertainOrdinate(ConsequenceStages[i], new Deterministic(values[i]));
        }
        return new TabularConsequence
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Loss",
            ConsequenceUnit = "lives",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(ordinates,
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>
    /// Builds one system component from the shared tables: the z-grid stage-frequency hazard,
    /// one failure mode per assigned legacy PFM (fragility + failure consequence), the shared
    /// non-failure mode, joint within-component combination with independent modes, and the
    /// additive within-component consequence rule (every legacy system body).
    /// </summary>
    /// <param name="name">The component name.</param>
    /// <param name="modes">The zero-based legacy PFM indices carried by this component.</param>
    /// <returns>The system component.</returns>
    private static SystemComponent BuildComponent(string name, int[] modes)
    {
        var (hazardProbabilities, hazardStages) = HazardTable();
        var hazardOrdinates = new UncertainOrdinate[hazardStages.Length];
        for (int i = 0; i < hazardStages.Length; i++)
        {
            hazardOrdinates[i] = new UncertainOrdinate(1d - hazardProbabilities[i], new Deterministic(hazardStages[i]));
        }
        var hazard = new TabularHazard
        {
            Name = $"{name} Stage Frequency",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            ProbabilityTransform = Transform.None,
            NoUncertaintyFunction = new UncertainOrderedPairedData(hazardOrdinates,
                true, SortOrder.Descending, true, SortOrder.Ascending, UnivariateDistributionType.Deterministic),
        };

        var component = new SystemComponent { Name = name };
        component.HazardFunction = hazard;
        foreach (int mode in modes)
        {
            var (fragilityStages, fragilityProbabilities) = FragilityTable(mode);
            var fragilityOrdinates = new UncertainOrdinate[fragilityStages.Length];
            for (int i = 0; i < fragilityStages.Length; i++)
            {
                fragilityOrdinates[i] = new UncertainOrdinate(fragilityStages[i], new Deterministic(fragilityProbabilities[i]));
            }
            var fragility = new TabularResponse
            {
                Name = $"{name} PFM-{mode + 1} Fragility",
                SpecifiedHazard = "Stage",
                HazardUnit = "ft",
                UncertainOrderedPairedData = new UncertainOrderedPairedData(fragilityOrdinates,
                    true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
            };
            component.AddFailureMode(new FailureMode(null, null, fragility, Consequence($"{name} PFM-{mode + 1} Loss", FailureValues[mode])));
        }
        component.AddFailureMode(new FailureMode(null, null, null, Consequence($"{name} Non-Failure Loss", NonFailureValues)));

        component.FailureModeMethod = FailureModeMethod.JointFailures;
        component.FailureModeDependency = DependencyType.Independent;
        component.JointConsequences = JointConsequenceType.Additive;
        return component;
    }

    /// <summary>Builds the engine analysis for one system scenario.</summary>
    /// <param name="componentModes">The per-component legacy PFM index layout.</param>
    /// <param name="method">The system risk method.</param>
    /// <param name="dependency">The component-hazard dependency option.</param>
    /// <param name="rule">The across-component joint-consequence rule.</param>
    /// <returns>The configured analysis.</returns>
    private static RiskAnalysis BuildAnalysis(int[][] componentModes, SystemRiskType method,
        DependencyType dependency, JointConsequenceType rule)
    {
        var components = new SystemComponent[componentModes.Length];
        for (int c = 0; c < componentModes.Length; c++)
        {
            components[c] = BuildComponent($"Component {c + 1}", componentModes[c]);
        }
        var analysis = new RiskAnalysis(components) { Name = $"System {method} {dependency} {rule}" };
        analysis.Options.SystemRiskMethod = method;
        analysis.Options.ComponentHazardDependency = dependency;
        if (dependency == DependencyType.CorrelationMatrix)
        {
            analysis.Options.HazardCorrelationMatrix = Equicorrelated(componentModes.Length, 0.5d);
        }
        analysis.Options.JointConsequences = rule;
        analysis.Options.ConsequenceThreshold = Threshold;
        analysis.Options.Alpha = Alpha;
        // Tail focus off: γ = 1 sampling, identical to v1.0 — the tail-focus modes are audited
        // separately by SystemRiskVerification's γ-audit, and γ > 1 only adds Monte Carlo noise
        // to the bulk quantities this matrix asserts.
        analysis.Options.VegasTailFocusMode = VegasTailFocusMode.None;
        // The exceedance probes read the output LEC surface; the maximum output resolution keeps
        // the thinning error an order below the statistical tolerance (the finding the joint
        // family documents).
        analysis.Options.LECOutputLength = 1000;
        // The additive lattice at the default 4,096 nodes quantizes the five-component support
        // (Σ max ≈ 4,200) to ≈ 1 loss unit — at the failure distribution's high-density
        // conditional-median knee that shifts the read exceedance by ~2× the binomial tolerance.
        // Sixteen times the nodes keeps quantization an order below the statistical tolerances.
        analysis.Options.SystemConvolutionPoints = 65_536;
        return analysis;
    }

    /// <summary>Runs an analysis synchronously and asserts it estimated, surfacing the completion error.</summary>
    /// <param name="analysis">The analysis to run.</param>
    /// <param name="label">The assert label.</param>
    private static void Run(RiskAnalysis analysis, string label)
    {
        Exception? error = null;
        analysis.AnalysisCompleted += (_, e) => error = e.Error;
        analysis.RunAsync().GetAwaiter().GetResult();
        Assert.IsTrue(analysis.IsEstimated,
            $"{label}: the analysis must estimate.{(error == null ? string.Empty : $" Run error: {error}")}");
    }

    #endregion

    #region Oracle

    /// <summary>
    /// Online central-moment accumulator (Welford/Pébay updates through the fourth moment) —
    /// numerically stable for the zero-inflated loss streams whose raw power sums cancel.
    /// </summary>
    private struct Moments
    {
        /// <summary>The accumulated count.</summary>
        private long _count;

        /// <summary>The running mean.</summary>
        private double _m1;

        /// <summary>The running second central power sum.</summary>
        private double _m2;

        /// <summary>The running third central power sum.</summary>
        private double _m3;

        /// <summary>The running fourth central power sum.</summary>
        private double _m4;

        /// <summary>Accumulates one draw.</summary>
        /// <param name="value">The draw value.</param>
        public void Add(double value)
        {
            _count++;
            double delta = value - _m1;
            double deltaOverN = delta / _count;
            double deltaOverN2 = deltaOverN * deltaOverN;
            double term1 = delta * deltaOverN * (_count - 1);
            _m1 += deltaOverN;
            _m4 += term1 * deltaOverN2 * ((double)_count * _count - 3d * _count + 3d) + 6d * deltaOverN2 * _m2 - 4d * deltaOverN * _m3;
            _m3 += term1 * deltaOverN * (_count - 2) - 3d * deltaOverN * _m2;
            _m2 += term1;
        }

        /// <summary>The sample mean.</summary>
        public readonly double Mean => _m1;

        /// <summary>The population standard deviation.</summary>
        public readonly double Sigma => Math.Sqrt(_m2 / _count);

        /// <summary>The Monte Carlo standard error of the mean.</summary>
        public readonly double MeanSe => Sigma / Math.Sqrt(_count);

        /// <summary>The delta-method standard error of the standard deviation, √(m₄ − σ⁴)/(2σ√N).</summary>
        public readonly double SigmaSe
        {
            get
            {
                double variance = _m2 / _count;
                double m4 = _m4 / _count;
                return Math.Sqrt(Math.Max(0d, m4 - variance * variance)) / (2d * Math.Sqrt(variance) * Math.Sqrt(_count));
            }
        }
    }

    /// <summary>One across-component rule's oracle streams.</summary>
    private sealed class OracleRuleStreams
    {
        /// <summary>The system failure risk stream (zero on no-failure draws).</summary>
        public Moments Fail;

        /// <summary>The system non-failure risk stream (surviving components' combination).</summary>
        public Moments NonFail;

        /// <summary>The system total risk stream (failure + non-failure per draw).</summary>
        public Moments Total;

        /// <summary>The system incremental (excess) risk stream (failed components' excess combination).</summary>
        public Moments Excess;

        /// <summary>The system background risk stream (all components' non-failure combination).</summary>
        public Moments Background;

        /// <summary>The sorted unconditional system failure losses (zeros on no-failure draws).</summary>
        public double[] SortedFail = Array.Empty<double>();

        /// <summary>The sorted system total losses.</summary>
        public double[] SortedTotal = Array.Empty<double>();
    }

    /// <summary>One dependency group's consolidated oracle output.</summary>
    private sealed class SystemOracleResult
    {
        /// <summary>The any-component-failed union estimate.</summary>
        public double FailureUnion;

        /// <summary>The binomial standard error of the failure union.</summary>
        public double FailureUnionSe;

        /// <summary>The number of realizations with at least one failed component.</summary>
        public long UnionCount;

        /// <summary>The per-rule streams, indexed like <see cref="Rules"/>.</summary>
        public OracleRuleStreams[] ByRule = Array.Empty<OracleRuleStreams>();
    }

    /// <summary>
    /// The consolidated system oracle: one pass over the shared legacy draws accumulating every
    /// across-component rule. Per realization each component draws its correlated hazard through
    /// the latent Gaussian, evaluates its failure modes independently against its capacity
    /// uniforms (within-component additive combination — every legacy system body), and the
    /// across-component combinations aggregate the failed and surviving sets under each rule.
    /// </summary>
    /// <param name="componentModes">The per-component legacy PFM index layout.</param>
    /// <param name="correlation">The cross-component hazard correlation matrix.</param>
    /// <param name="hazardSeed">The multivariate-normal hazard-draw seed.</param>
    /// <param name="capacitySeeds">
    /// One seed → a single <c>MersenneTwister.NextDoubles(N, D)</c> stream with one column per
    /// component (the 1-failure-mode legacy layout); one seed per component → per-component
    /// <c>NextDoubles(N, modes)</c> streams (the 2-failure-mode legacy layout).
    /// </param>
    /// <returns>The accumulated group result.</returns>
    private static SystemOracleResult RunSystemOracle(int[][] componentModes, double[,] correlation,
        int hazardSeed, int[] capacitySeeds)
    {
        int componentCount = componentModes.Length;
        var (hazardProbabilities, hazardStages) = HazardTable();
        var fragilityStages = new double[FragilityMeans.Length][];
        var fragilityProbabilities = new double[FragilityMeans.Length][];
        for (int mode = 0; mode < FragilityMeans.Length; mode++)
        {
            (fragilityStages[mode], fragilityProbabilities[mode]) = FragilityTable(mode);
        }

        var multivariate = new MultivariateNormal(new double[componentCount], correlation);
        double[,] hazardDraws = multivariate.GenerateRandomValues(OracleRealizations, hazardSeed);

        double[][,] capacity;
        if (capacitySeeds.Length == 1)
        {
            // One shared stream, one column per component (each component has one mode).
            var shared = new MersenneTwister(capacitySeeds[0]).NextDoubles(OracleRealizations, componentCount);
            capacity = new double[componentCount][,];
            for (int c = 0; c < componentCount; c++) capacity[c] = shared;
        }
        else
        {
            capacity = new double[componentCount][,];
            for (int c = 0; c < componentCount; c++)
            {
                capacity[c] = new MersenneTwister(capacitySeeds[c]).NextDoubles(OracleRealizations, componentModes[c].Length);
            }
        }
        bool sharedColumns = capacitySeeds.Length == 1;

        var result = new SystemOracleResult { ByRule = new OracleRuleStreams[Rules.Length] };
        for (int r = 0; r < Rules.Length; r++)
        {
            result.ByRule[r] = new OracleRuleStreams
            {
                SortedFail = new double[OracleRealizations],
                SortedTotal = new double[OracleRealizations],
            };
        }

        var hazardLevels = new double[componentCount];
        var nonFailureValues = new double[componentCount];
        var componentFailed = new bool[componentCount];
        var componentFail = new double[componentCount];
        var componentExcess = new double[componentCount];

        for (int i = 0; i < OracleRealizations; i++)
        {
            bool anyFailed = false;
            for (int c = 0; c < componentCount; c++)
            {
                hazardLevels[c] = Interpolate(hazardProbabilities, hazardStages, Normal.StandardCDF(hazardDraws[i, c]));
                nonFailureValues[c] = Interpolate(ConsequenceStages, NonFailureValues, hazardLevels[c]);

                componentFailed[c] = false;
                componentFail[c] = 0d;
                for (int m = 0; m < componentModes[c].Length; m++)
                {
                    int mode = componentModes[c][m];
                    double failureProbability = Math.Max(0d, Math.Min(1d,
                        Interpolate(fragilityStages[mode], fragilityProbabilities[mode], hazardLevels[c])));
                    double uniform = sharedColumns ? capacity[c][i, c] : capacity[c][i, m];
                    if (uniform <= failureProbability)
                    {
                        componentFailed[c] = true;
                        componentFail[c] += Interpolate(ConsequenceStages, FailureValues[mode], hazardLevels[c]);
                    }
                }
                componentExcess[c] = componentFail[c] - nonFailureValues[c];
                if (componentFailed[c]) anyFailed = true;
            }

            if (anyFailed) result.UnionCount++;

            for (int r = 0; r < Rules.Length; r++)
            {
                var rule = Rules[r];
                double failCombined = CombineSubset(componentFail, componentFailed, wantFailed: true, rule);
                double excessCombined = CombineSubset(componentExcess, componentFailed, wantFailed: true, rule);
                double nonFailCombined = CombineSubset(nonFailureValues, componentFailed, wantFailed: false, rule);
                double backgroundCombined = CombineAllValues(nonFailureValues, rule);
                double total = failCombined + nonFailCombined;

                var streams = result.ByRule[r];
                streams.Fail.Add(failCombined);
                streams.NonFail.Add(nonFailCombined);
                streams.Total.Add(total);
                streams.Excess.Add(anyFailed ? excessCombined : 0d);
                streams.Background.Add(backgroundCombined);
                streams.SortedFail[i] = failCombined;
                streams.SortedTotal[i] = total;
            }
        }

        for (int r = 0; r < Rules.Length; r++)
        {
            Array.Sort(result.ByRule[r].SortedFail);
            Array.Sort(result.ByRule[r].SortedTotal);
        }
        result.FailureUnion = result.UnionCount / (double)OracleRealizations;
        result.FailureUnionSe = Math.Sqrt(result.FailureUnion * (1d - result.FailureUnion) / OracleRealizations);
        return result;
    }

    /// <summary>Combines the values of the failed (or surviving) components under a rule; an empty subset yields zero.</summary>
    /// <param name="values">The per-component values.</param>
    /// <param name="failed">The per-component failure indicators.</param>
    /// <param name="wantFailed">True to combine the failed subset; false for the surviving subset.</param>
    /// <param name="rule">The combination rule.</param>
    /// <returns>The combined value.</returns>
    private static double CombineSubset(double[] values, bool[] failed, bool wantFailed, JointConsequenceType rule)
    {
        double combined = 0d;
        int count = 0;
        for (int c = 0; c < values.Length; c++)
        {
            if (failed[c] != wantFailed) continue;
            if (count == 0)
            {
                combined = values[c];
            }
            else
            {
                combined = rule switch
                {
                    JointConsequenceType.Additive or JointConsequenceType.Average => combined + values[c],
                    JointConsequenceType.Maximum => Math.Max(combined, values[c]),
                    _ => Math.Min(combined, values[c]),
                };
            }
            count++;
        }
        if (count == 0) return 0d;
        return rule == JointConsequenceType.Average ? combined / count : combined;
    }

    /// <summary>Combines every component's value under a rule.</summary>
    /// <param name="values">The per-component values.</param>
    /// <param name="rule">The combination rule.</param>
    /// <returns>The combined value.</returns>
    private static double CombineAllValues(double[] values, JointConsequenceType rule)
    {
        double combined = values[0];
        for (int c = 1; c < values.Length; c++)
        {
            combined = rule switch
            {
                JointConsequenceType.Additive or JointConsequenceType.Average => combined + values[c],
                JointConsequenceType.Maximum => Math.Max(combined, values[c]),
                _ => Math.Min(combined, values[c]),
            };
        }
        return rule == JointConsequenceType.Average ? combined / values.Length : combined;
    }

    #endregion

    #region Assert Drivers

    /// <summary>
    /// Asserts one loss-exceedance ordinate against the oracle's empirical exceedance,
    /// combining the oracle's binomial standard error with an engine-side standard error in
    /// quadrature, after verifying the probe carries at least 100 exceedances.
    /// </summary>
    /// <param name="sortedLosses">The oracle's sorted unconditional losses.</param>
    /// <param name="level">The consequence level probed.</param>
    /// <param name="engineExceedance">The engine's exceedance ordinate at the level.</param>
    /// <param name="engineCount">The engine-side effective sample count (zero for a deterministic engine path).</param>
    /// <param name="message">The assert label.</param>
    private static void AssertExceedance(double[] sortedLosses, double level, double engineExceedance,
        double engineCount, string message)
    {
        int exceeding = 0;
        for (int i = sortedLosses.Length - 1; i >= 0 && sortedLosses[i] > level; i--) exceeding++;
        Assert.IsTrue(exceeding >= 100, $"{message} — the probe must carry at least 100 exceedances (found {exceeding}).");
        double probability = exceeding / (double)sortedLosses.Length;
        double oracleSe = Math.Sqrt(probability * (1d - probability) / sortedLosses.Length);
        double engineSe = engineCount > 0d ? Math.Sqrt(probability * (1d - probability) / engineCount) : 0d;
        Assert.AreEqual(probability, engineExceedance, K * Math.Sqrt(oracleSe * oracleSe + engineSe * engineSe), message);
    }

    /// <summary>
    /// Asserts the deterministic additive-method engine run (Independent groups, additive rule)
    /// against the oracle on the full catalog: the five summary means, the failure union and the
    /// non-failure complement, the failure and total standard deviations, the assurance measure,
    /// two data-driven failure-curve probes and one total-curve probe, the value-at-risk in
    /// probability space, the conditional value-at-risk, and the report pins where published.
    /// </summary>
    /// <param name="oracle">The group oracle.</param>
    /// <param name="componentModes">The per-component mode layout.</param>
    /// <param name="family">The report pin family key.</param>
    /// <param name="label">The assert label.</param>
    private static void AssertAdditiveEngine(SystemOracleResult oracle, int[][] componentModes,
        string family, string label)
    {
        var streams = oracle.ByRule[0];
        var analysis = BuildAnalysis(componentModes, SystemRiskType.AdditiveRiskMethod,
            DependencyType.Independent, JointConsequenceType.Additive);
        Run(analysis, label);
        var summary = analysis.RiskResults![0]!;
        var curves = analysis.MeanRiskResults!.Curves;

        // The five summary means (v1.0-parity per the means-versus-tails policy).
        Assert.AreEqual(streams.Fail.Mean, summary.Fail.Mean, K * streams.Fail.MeanSe, $"{label}: failure risk mean.");
        Assert.AreEqual(streams.NonFail.Mean, summary.NonFail.Mean, K * streams.NonFail.MeanSe, $"{label}: non-failure risk mean.");
        Assert.AreEqual(streams.Total.Mean, summary.Total.Mean, K * streams.Total.MeanSe, $"{label}: total risk mean.");
        Assert.AreEqual(streams.Excess.Mean, summary.Excess.Mean, K * streams.Excess.MeanSe, $"{label}: incremental (excess) risk mean.");
        Assert.AreEqual(streams.Background.Mean, summary.Background.Mean, K * streams.Background.MeanSe, $"{label}: background risk mean.");

        // The failure union and the v1.0 stream-probability semantics.
        Assert.AreEqual(oracle.FailureUnion, summary.Fail.TotalProbability, K * oracle.FailureUnionSe,
            $"{label}: system annualized failure probability.");
        Assert.AreEqual(1d - oracle.FailureUnion, summary.NonFail.TotalProbability, K * oracle.FailureUnionSe,
            $"{label}: the non-failure stream's total probability must complement the failure union.");

        // Dispersion (Monte-Carlo-parity per the means-versus-tails policy; the lattice quantization is
        // an order below these statistical tolerances at 4096 nodes).
        Assert.AreEqual(streams.Fail.Sigma, summary.Fail.StandardDeviation, K * streams.Fail.SigmaSe,
            $"{label}: failure risk standard deviation.");
        Assert.AreEqual(streams.Total.Sigma, summary.Total.StandardDeviation, K * streams.Total.SigmaSe,
            $"{label}: total risk standard deviation.");

        // The assurance measure and the data-driven curve probes.
        AssertExceedance(streams.SortedFail, Threshold, summary.Fail.ConsequenceThresholdProbability, 0d,
            $"{label}: assurance P(C > {Threshold}) on the failure stream.");
        double failProbe1 = streams.SortedFail[OracleRealizations - (int)Math.Round(0.5d * oracle.UnionCount)];
        double failProbe2 = streams.SortedFail[OracleRealizations - (int)Math.Round(0.05d * oracle.UnionCount)];
        AssertExceedance(streams.SortedFail, failProbe1,
            curves.Fail.LEC.GetYFromX(failProbe1, Transform.Logarithmic, Transform.Logarithmic), 0d,
            $"{label}: failure exceedance at the conditional median loss {failProbe1:G6}.");
        AssertExceedance(streams.SortedFail, failProbe2,
            curves.Fail.LEC.GetYFromX(failProbe2, Transform.Logarithmic, Transform.Logarithmic), 0d,
            $"{label}: failure exceedance at the conditional 95th-percentile loss {failProbe2:G6}.");
        double totalProbe = streams.SortedTotal[OracleRealizations - (int)Math.Round(0.01d * OracleRealizations)];
        AssertExceedance(streams.SortedTotal, totalProbe,
            curves.Total.LEC.GetYFromX(totalProbe, Transform.Logarithmic, Transform.Logarithmic), 0d,
            $"{label}: total exceedance at the 1% loss {totalProbe:G6}.");

        // Value-at-risk in probability space (the 4b convention — sidesteps the convolution
        // lattice quantization): the engine failure curve's exceedance at the oracle's empirical
        // α quantile must be α within k·SE plus the one-per-mille output-resolution slack.
        double valueAtRisk = streams.SortedFail[(int)Math.Round((1d - Alpha) * OracleRealizations)];
        double exceedanceAtVar = curves.Fail.LEC.GetYFromX(valueAtRisk, Transform.Logarithmic, Transform.Logarithmic);
        double varSe = Math.Sqrt(Alpha * (1d - Alpha) / OracleRealizations);
        Assert.AreEqual(Alpha, exceedanceAtVar, K * varSe + 1e-3,
            $"{label}: the engine failure exceedance at the oracle α = {Alpha} quantile ({valueAtRisk:G6}).");

        // Conditional value-at-risk at k·SE plus the documented 0.5% lattice/thinning envelope.
        int tailCount = (int)Math.Round(Alpha * OracleRealizations);
        double tailMean = 0d;
        for (int i = OracleRealizations - tailCount; i < OracleRealizations; i++) tailMean += streams.SortedFail[i];
        tailMean /= tailCount;
        double tailM2 = 0d;
        for (int i = OracleRealizations - tailCount; i < OracleRealizations; i++)
        {
            double delta = streams.SortedFail[i] - tailMean;
            tailM2 += delta * delta;
        }
        double tailMeanSe = Math.Sqrt(tailM2 / tailCount) / Math.Sqrt(tailCount);
        Assert.AreEqual(tailMean, summary.Fail.ConditionalValueAtRisk, K * tailMeanSe + 5e-3 * tailMean,
            $"{label}: conditional value-at-risk at α = {Alpha}.");

        // The report pins (the engine is deterministic on this path, so the tolerance is the
        // report's own 10M sampling error plus the 0.1% tabulation allowance — the joint
        // family's pin formula).
        AssertReportPins(family, DependencyType.Independent, JointConsequenceType.Additive,
            streams, summary, 0d, label);

        Console.WriteLine(
            $"{label}: mean fail {streams.Fail.Mean:G6}/{summary.Fail.Mean:G6}, total {streams.Total.Mean:G6}/{summary.Total.Mean:G6}, " +
            $"excess {streams.Excess.Mean:G6}/{summary.Excess.Mean:G6}, background {streams.Background.Mean:G6}/{summary.Background.Mean:G6}, " +
            $"nonfail {streams.NonFail.Mean:G6}/{summary.NonFail.Mean:G6}, union {oracle.FailureUnion:G6}/{summary.Fail.TotalProbability:G6}, " +
            $"σF {streams.Fail.Sigma:G6}/{summary.Fail.StandardDeviation:G6}, CVaR {tailMean:G6}/{summary.Fail.ConditionalValueAtRisk:G6} (oracle/engine)");
    }

    /// <summary>
    /// Asserts one joint-method engine run against the oracle: the five summary means with the
    /// oracle and reported-VEGAS errors combined, the failure union and curve probes with
    /// binomial errors combined at the recorded evaluation count, the exhaustive mass balance,
    /// the additive-rule component-mean identity, and the report pins where published.
    /// </summary>
    /// <param name="oracle">The group oracle.</param>
    /// <param name="ruleIndex">The across-component rule index into <see cref="Rules"/>.</param>
    /// <param name="componentModes">The per-component mode layout.</param>
    /// <param name="dependency">The engine dependency option.</param>
    /// <param name="family">The report pin family key.</param>
    /// <param name="label">The assert label.</param>
    private static void AssertJointEngine(SystemOracleResult oracle, int ruleIndex, int[][] componentModes,
        DependencyType dependency, string family, string label)
    {
        var rule = Rules[ruleIndex];
        var streams = oracle.ByRule[ruleIndex];
        var analysis = BuildAnalysis(componentModes, SystemRiskType.JointRiskMethod, dependency, rule);
        Run(analysis, label);
        var summary = analysis.RiskResults![0]!;
        var curves = analysis.MeanRiskResults!.Curves;
        double recordedEvaluations = 5d * analysis.Options.FinalEvaluations;
        double engineSe = summary.StandardError;

        // The five summary means: the engine side is Monte Carlo, so its reported VEGAS standard
        // error (of the total-mean integral — a conservative proxy for every stream) adds to the
        // oracle standard error.
        Assert.AreEqual(streams.Fail.Mean, summary.Fail.Mean, K * (streams.Fail.MeanSe + engineSe), $"{label}: failure risk mean.");
        Assert.AreEqual(streams.NonFail.Mean, summary.NonFail.Mean, K * (streams.NonFail.MeanSe + engineSe), $"{label}: non-failure risk mean.");
        Assert.AreEqual(streams.Total.Mean, summary.Total.Mean, K * (streams.Total.MeanSe + engineSe), $"{label}: total risk mean.");
        Assert.AreEqual(streams.Excess.Mean, summary.Excess.Mean, K * (streams.Excess.MeanSe + engineSe), $"{label}: incremental (excess) risk mean.");
        Assert.AreEqual(streams.Background.Mean, summary.Background.Mean, K * (streams.Background.MeanSe + engineSe), $"{label}: background risk mean.");

        // The failure union with binomial errors combined at the recorded evaluation count.
        double unionEngineSe = Math.Sqrt(oracle.FailureUnion * (1d - oracle.FailureUnion) / recordedEvaluations);
        Assert.AreEqual(oracle.FailureUnion, summary.Fail.TotalProbability,
            K * Math.Sqrt(oracle.FailureUnionSe * oracle.FailureUnionSe + unionEngineSe * unionEngineSe),
            $"{label}: system annualized failure probability.");

        // Data-driven curve probes with combined binomial errors.
        double failProbe = streams.SortedFail[OracleRealizations - (int)Math.Round(0.5d * oracle.UnionCount)];
        AssertExceedance(streams.SortedFail, failProbe,
            curves.Fail.LEC.GetYFromX(failProbe, Transform.Logarithmic, Transform.Logarithmic), recordedEvaluations,
            $"{label}: failure exceedance at the conditional median loss {failProbe:G6}.");
        double totalProbe = streams.SortedTotal[OracleRealizations - (int)Math.Round(0.01d * OracleRealizations)];
        AssertExceedance(streams.SortedTotal, totalProbe,
            curves.Total.LEC.GetYFromX(totalProbe, Transform.Logarithmic, Transform.Logarithmic), recordedEvaluations,
            $"{label}: total exceedance at the 1% loss {totalProbe:G6}.");

        // The self-normalized exhaustive budget: exact at D = 2 (the exclusive enumeration is
        // complete), and within the documented Numerics IndependentExclusive convergence
        // tolerance above (its early exit closes the inclusion–exclusion gap with one pseudo-row
        // once the union converges within 1e-4, so a high-dimensional budget can drift by up to
        // that tolerance — the engine's honest mass-balance witness; observed ~1e-6 at D = 5).
        double massTolerance = componentModes.Length <= 2 ? 1e-9 : 1e-4;
        Assert.AreEqual(1d, curves.Total.MassBalance, massTolerance, $"{label}: the joint Total budget must self-normalize to one.");

        // The additive-rule combination identity: the enumeration preserves the sum-of-component
        // means exactly at D = 2 (complete exclusive enumeration); above that the documented
        // IndependentExclusive convergence shortcut drifts the identity by the dropped
        // combination mass (observed ≤ ~5e-6 relative at D = 5, bounded by the 1e-4 enumeration
        // tolerance — the same upstream truncation the mass-balance witness surfaces).
        if (rule == JointConsequenceType.Additive)
        {
            double componentMeanSum = 0d;
            for (int c = 0; c < summary.ComponentResults.Count; c++)
            {
                componentMeanSum += summary.ComponentResults[c].Total.Mean;
            }
            double identityTolerance = (componentModes.Length <= 2 ? 1e-9 : 1e-4) * componentMeanSum;
            Assert.AreEqual(componentMeanSum, summary.Total.Mean, identityTolerance,
                $"{label}: the combination enumeration must preserve the additive-combine mean identity.");
        }

        AssertReportPins(family, dependency, rule, streams, summary, engineSe, label);

        Console.WriteLine(
            $"{label}: mean fail {streams.Fail.Mean:G6}/{summary.Fail.Mean:G6}, total {streams.Total.Mean:G6}/{summary.Total.Mean:G6}, " +
            $"excess {streams.Excess.Mean:G6}/{summary.Excess.Mean:G6}, background {streams.Background.Mean:G6}/{summary.Background.Mean:G6}, " +
            $"nonfail {streams.NonFail.Mean:G6}/{summary.NonFail.Mean:G6}, union {oracle.FailureUnion:G6}/{summary.Fail.TotalProbability:G6}, " +
            $"VEGAS SE {summary.StandardError:G3} (oracle/engine)");
    }

    /// <summary>
    /// Pins the engine's five summary means against the 2024 report's published constants where
    /// the scenario was published: tolerance = the report's own 10M sampling error (4·σ̂/√10⁷,
    /// σ̂ from this oracle's matching stream) plus a 0.2% tabulation allowance, plus the
    /// engine's reported VEGAS error on joint-method runs. The tabulation allowance is twice
    /// the single-component families' figure because the report sampled the exact distributions
    /// while this scenario tabulates them, and the z-grid bias adds coherently across the
    /// summed components — measured ≈ 0.12% relative on the five-component background stream
    /// (whose report constant itself sits ≈ 3.8 of its own standard errors from five times the
    /// report's single-component background, the widest internal spread in tables 77–103).
    /// </summary>
    /// <param name="family">The report pin family key.</param>
    /// <param name="dependency">The dependency option.</param>
    /// <param name="rule">The across-component rule.</param>
    /// <param name="streams">The oracle streams supplying the σ̂ estimates.</param>
    /// <param name="summary">The engine summary.</param>
    /// <param name="engineSe">The engine's reported standard error (zero on the deterministic additive path).</param>
    /// <param name="label">The assert label.</param>
    private static void AssertReportPins(string family, DependencyType dependency, JointConsequenceType rule,
        OracleRuleStreams streams, SystemRiskResults summary, double engineSe, string label)
    {
        if (!ReportConstants.TryGetValue((family, dependency, rule), out var pins)) return;
        double reportN = Math.Sqrt(1e7);
        Assert.AreEqual(pins[0], summary.Excess.Mean, K * (streams.Excess.Sigma / reportN + engineSe) + 2e-3 * pins[0], $"{label}: report incremental pin.");
        Assert.AreEqual(pins[1], summary.Background.Mean, K * (streams.Background.Sigma / reportN + engineSe) + 2e-3 * pins[1], $"{label}: report background pin.");
        Assert.AreEqual(pins[2], summary.Total.Mean, K * (streams.Total.Sigma / reportN + engineSe) + 2e-3 * pins[2], $"{label}: report total pin.");
        Assert.AreEqual(pins[3], summary.Fail.Mean, K * (streams.Fail.Sigma / reportN + engineSe) + 2e-3 * pins[3], $"{label}: report failure pin.");
        Assert.AreEqual(pins[4], summary.NonFail.Mean, K * (streams.NonFail.Sigma / reportN + engineSe) + 2e-3 * pins[4], $"{label}: report non-failure pin.");
    }

    /// <summary>
    /// Runs one dependency group of a 1-failure-mode family end to end: the consolidated oracle
    /// pass, the joint-method engine runs for all four rules, and — for the independent group —
    /// the deterministic additive-method run on the additive rule.
    /// </summary>
    /// <param name="componentModes">The per-component mode layout.</param>
    /// <param name="dependency">The dependency option.</param>
    /// <param name="family">The report pin family key.</param>
    private static void RunOneModeGroup(int[][] componentModes, DependencyType dependency, string family)
    {
        int componentCount = componentModes.Length;
        var correlation = Equicorrelated(componentCount, DependencyOffDiagonal(dependency, componentCount));
        var oracle = RunSystemOracle(componentModes, correlation, OneModeHazardSeed, new[] { CapacitySeed });

        if (dependency == DependencyType.Independent)
        {
            AssertAdditiveEngine(oracle, componentModes, family, $"{family} {dependency} Additive (additive method)");
        }
        for (int r = 0; r < Rules.Length; r++)
        {
            AssertJointEngine(oracle, r, componentModes, dependency, family, $"{family} {dependency} {Rules[r]} (joint method)");
        }
    }

    /// <summary>
    /// Runs one dependency group of the 2-component 2-failure-mode family: the consolidated
    /// oracle pass at the legacy per-component capacity streams, the joint-method engine run for
    /// the additive rule (the legacy scope), and — for the independent group — the deterministic
    /// additive-method run.
    /// </summary>
    /// <param name="dependency">The dependency option.</param>
    private static void RunTwoModeGroup(DependencyType dependency)
    {
        var correlation = Equicorrelated(2, DependencyOffDiagonal(dependency, 2));
        var oracle = RunSystemOracle(TwoComponentTwoModes, correlation, TwoModeHazardSeed,
            new[] { CapacitySeed, SecondCapacitySeed });

        if (dependency == DependencyType.Independent)
        {
            AssertAdditiveEngine(oracle, TwoComponentTwoModes, "2C2P", $"2C2P {dependency} Additive (additive method)");
        }
        AssertJointEngine(oracle, 0, TwoComponentTwoModes, dependency, "2C2P", $"2C2P {dependency} Additive (joint method)");
    }

    #endregion

    /// <summary>2 components / 2 failure modes each, independent hazards, additive rule (legacy line 14; report table 89).</summary>
    [TestMethod]
    public void Test_2Comp2Pfm_Independent_Additive_VsOracle()
    {
        RunTwoModeGroup(DependencyType.Independent);
    }

    /// <summary>
    /// 2 components / 2 failure modes each, perfectly positive hazards, additive rule (legacy
    /// line 178; report table 90). Documented deviation: legacy r = 1 − ε → the engine constant
    /// 1 − √ε.
    /// </summary>
    [TestMethod]
    public void Test_2Comp2Pfm_PerfectlyPositive_Additive_VsOracle()
    {
        RunTwoModeGroup(DependencyType.PerfectlyPositive);
    }

    /// <summary>
    /// 2 components / 2 failure modes each, perfectly negative hazards, additive rule (legacy
    /// line 342; report table 91). Documented deviation: legacy r = −1 + ε → the engine constant
    /// −1 + √ε.
    /// </summary>
    [TestMethod]
    public void Test_2Comp2Pfm_PerfectlyNegative_Additive_VsOracle()
    {
        RunTwoModeGroup(DependencyType.PerfectlyNegative);
    }

    /// <summary>2 components / 2 failure modes each, hazard correlation r = 0.5, additive rule (legacy line 506; unpublished).</summary>
    [TestMethod]
    public void Test_2Comp2Pfm_CorrelationMatrix_Additive_VsOracle()
    {
        RunTwoModeGroup(DependencyType.CorrelationMatrix);
    }

    /// <summary>2 components / 1 failure mode each, independent hazards — all four rules (legacy lines 670–1174; report tables 77–80).</summary>
    [TestMethod]
    public void Test_2Comp1Pfm_Independent_AllRules_VsOracle()
    {
        RunOneModeGroup(TwoComponentModes, DependencyType.Independent, "2C1P");
    }

    /// <summary>2 components / 1 failure mode each, perfectly positive hazards (r = 1 − √ε) — all four rules (legacy lines 1240–1810; report tables 81–84).</summary>
    [TestMethod]
    public void Test_2Comp1Pfm_PerfectlyPositive_AllRules_VsOracle()
    {
        RunOneModeGroup(TwoComponentModes, DependencyType.PerfectlyPositive, "2C1P");
    }

    /// <summary>2 components / 1 failure mode each, perfectly negative hazards (r = −1 + √ε) — all four rules (legacy lines 1814–2388; report tables 85–88).</summary>
    [TestMethod]
    public void Test_2Comp1Pfm_PerfectlyNegative_AllRules_VsOracle()
    {
        RunOneModeGroup(TwoComponentModes, DependencyType.PerfectlyNegative, "2C1P");
    }

    /// <summary>2 components / 1 failure mode each, hazard correlation r = 0.5 — all four rules (legacy lines 2389–2963; unpublished).</summary>
    [TestMethod]
    public void Test_2Comp1Pfm_CorrelationMatrix_AllRules_VsOracle()
    {
        RunOneModeGroup(TwoComponentModes, DependencyType.CorrelationMatrix, "2C1P");
    }

    /// <summary>5 components / 1 failure mode each, independent hazards — all four rules (legacy lines 2964–3567; report tables 92–95).</summary>
    [TestMethod]
    public void Test_5Comp1Pfm_Independent_AllRules_VsOracle()
    {
        RunOneModeGroup(FiveComponentModes, DependencyType.Independent, "5C1P");
    }

    /// <summary>5 components / 1 failure mode each, perfectly positive hazards (r = 1 − √ε) — all four rules (legacy lines 3568–4171; report tables 96–99).</summary>
    [TestMethod]
    public void Test_5Comp1Pfm_PerfectlyPositive_AllRules_VsOracle()
    {
        RunOneModeGroup(FiveComponentModes, DependencyType.PerfectlyPositive, "5C1P");
    }

    /// <summary>
    /// 5 components / 1 failure mode each, perfectly negative hazards (r = −1/4 + √ε) — all four
    /// rules (legacy lines 4172–4775; report tables 100–103). The minimum-rule legacy method is
    /// mislabeled <c>Test_5_Component_5_PFM_JointFailures_Negative_Minimum</c>; its body is the
    /// 5-component 1-failure-mode scenario ported here.
    /// </summary>
    [TestMethod]
    public void Test_5Comp1Pfm_PerfectlyNegative_AllRules_VsOracle()
    {
        RunOneModeGroup(FiveComponentModes, DependencyType.PerfectlyNegative, "5C1P");
    }

    /// <summary>5 components / 1 failure mode each, hazard correlation r = 0.5 — all four rules (legacy lines 4776–5376; unpublished).</summary>
    [TestMethod]
    public void Test_5Comp1Pfm_CorrelationMatrix_AllRules_VsOracle()
    {
        RunOneModeGroup(FiveComponentModes, DependencyType.CorrelationMatrix, "5C1P");
    }

    /// <summary>
    /// The 5-component additive-path reproducibility pin (extends the 2-component pin):
    /// reversing the component declaration order and renaming every component and function is
    /// bit-inert on the system results — content seeding plus the canonical-hash convolution
    /// order — and the per-component results follow content identity through the shuffle.
    /// </summary>
    [TestMethod]
    public void Test_5CompAdditive_ShuffleRename_BitIdentical()
    {
        // Arrange / Act — forward and reversed+renamed five-component additive systems.
        var forward = BuildAnalysis(FiveComponentModes, SystemRiskType.AdditiveRiskMethod,
            DependencyType.Independent, JointConsequenceType.Additive);
        Run(forward, "5C additive forward");

        var reversedModes = new[] { new[] { 4 }, new[] { 3 }, new[] { 2 }, new[] { 1 }, new[] { 0 } };
        var reversed = BuildAnalysis(reversedModes, SystemRiskType.AdditiveRiskMethod,
            DependencyType.Independent, JointConsequenceType.Additive);
        reversed.Name = "Shuffled system";
        foreach (var component in reversed.Components)
        {
            component.Name = $"Renamed {component.Name}";
            foreach (var function in component.GetReferencedFunctions())
            {
                function.Name = $"Renamed {function.Name}";
                function.AssignNewId();
            }
        }
        Run(reversed, "5C additive reversed+renamed");

        // Assert — bit-identical system results and content-following component results.
        Assert.AreEqual(
            BitConverter.DoubleToInt64Bits(forward.RiskResults![0]!.Total.Mean),
            BitConverter.DoubleToInt64Bits(reversed.RiskResults![0]!.Total.Mean),
            "The additive system total mean must be bit-inert under component reordering and renaming.");
        Assert.AreEqual(
            BitConverter.DoubleToInt64Bits(forward.RiskResults[0]!.Fail.TotalProbability),
            BitConverter.DoubleToInt64Bits(reversed.RiskResults[0]!.Fail.TotalProbability),
            "The additive system failure union must be bit-inert under component reordering and renaming.");
        CollectionAssert.AreEqual(
            forward.MeanRiskResults!.Curves.Fail.LECProbabilities,
            reversed.MeanRiskResults!.Curves.Fail.LECProbabilities,
            "The additive system failure curve must be bit-inert under component reordering and renaming.");
        CollectionAssert.AreEqual(
            forward.MeanRiskResults.Curves.Total.LECConsequences,
            reversed.MeanRiskResults.Curves.Total.LECConsequences,
            "The additive system total curve must be bit-inert under component reordering and renaming.");
        for (int c = 0; c < 5; c++)
        {
            Assert.AreEqual(
                BitConverter.DoubleToInt64Bits(forward.RiskResults[0]!.ComponentResults[c].Fail.Mean),
                BitConverter.DoubleToInt64Bits(reversed.RiskResults[0]!.ComponentResults[4 - c].Fail.Mean),
                $"Component {c} results must follow content identity through the shuffle.");
        }
    }
}
