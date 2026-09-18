using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using Numerics.Sampling;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.Systems.Components;

namespace RMC.TotalRisk.Verification.Analyses.Risk;

/// <summary>
/// Multi-component system risk against independent brute-force Monte Carlo oracles:
/// the additive method's zero-inflated lattice convolution (mean, standard deviation, failure
/// union, tail exceedances, value-at-risk, and conditional value-at-risk of the system loss
/// distribution), the joint method's correlated-hazard VEGAS integration, the power-transform
/// tail-focus audit (the empirical γ gate), and the system-level reproducibility pins.
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// <para>
/// <b>Scenario</b> (deterministic tabular inputs shared bit-for-bit with the oracles): component
/// A tabulates its stage frequency from Normal(100, 20) quantiles and its fragility from
/// Φ((h − 140)/30) on dense z-grids, with failure consequences linear (60 → 0, 200 → 1000) and
/// non-failure (60 → 0, 200 → 100); component B uses Normal(80, 15), Φ((h − 115)/20),
/// (50 → 0, 160 → 400), and (50 → 0, 160 → 40). The oracles never touch the engine: they draw
/// annual events — hazards by inverse-transform through their own interpolator, failure
/// indicators by Bernoulli draws against the fragility — and accumulate the realized system loss
/// (a failed component contributes its failure consequence, a surviving one its non-failure
/// consequence), which is exactly the zero-inflated combination distribution the additive
/// convolution enumerates.
/// </para>
/// <para>
/// <b>Tolerances</b> (docs/verification.md): k·SE with k = 4 and the standard errors computed
/// in-run — the mean's SE from the per-draw variance; the standard deviation's from the fourth
/// central moment, SE(σ̂) = √((m₄ − s⁴)/N)/(2σ̂); exceedance probabilities from binomial
/// √(p(1 − p)/N); the value-at-risk compared in probability space (the engine curve's exceedance
/// at the oracle's empirical quantile must be the quantile level within k·SE plus a one-per-mille
/// output-resolution slack). The joint method's engine side is itself Monte Carlo, so its
/// reported VEGAS standard error (mean) or a conservative binomial error at the recorded
/// evaluation count (probabilities) combines in quadrature with the oracle's.
/// </para>
/// <para>
/// <b>Reproducibility scope:</b> the additive path pins component reordering plus wholesale
/// renaming as bit-inert (content seeding plus the canonical-hash convolution order); the joint
/// path pins renaming as bit-inert. Joint component reordering is statistically equivalent but
/// not bit-identical by construction — the VEGAS variates couple the hypercube dimensions, so
/// reordering permutes which coordinate stream drives which component (documented in
/// docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §7.8).
/// </para>
/// </remarks>
[TestClass]
public class SystemRiskVerification
{
    /// <summary>The oracle realization count (the conversion policy's 1M standard).</summary>
    private const int OracleRealizations = 1_000_000;

    /// <summary>The legacy fixed seed of the additive oracle.</summary>
    private const int AdditiveOracleSeed = 12345;

    /// <summary>The legacy fixed seed of the joint oracle.</summary>
    private const int JointOracleSeed = 45678;

    /// <summary>The tolerance multiplier on the Monte Carlo standard error.</summary>
    private const double K = 4d;

    /// <summary>The cross-component hazard correlation of the joint scenario.</summary>
    private const double JointCorrelation = 0.6d;

    /// <summary>The system-loss exceedance probe consequences (bulk to tail).</summary>
    private static readonly double[] Probes = { 150d, 400d, 700d };

    /// <summary>The shared z-grid step of the tabulated curves.</summary>
    private const double ZStep = 0.25d;

    /// <summary>The shared z-grid half-range of the tabulated curves.</summary>
    private const double ZRange = 8d;

    #region Scenario

    /// <summary>One component's scenario parameters and its oracle-side tables.</summary>
    private sealed class Scenario
    {
        /// <summary>The component name.</summary>
        public string Name = string.Empty;

        /// <summary>The hazard non-exceedance probabilities, ascending.</summary>
        public double[] HazardProbabilities = Array.Empty<double>();

        /// <summary>The hazard stages, ascending with the probabilities.</summary>
        public double[] HazardStages = Array.Empty<double>();

        /// <summary>The fragility stages, ascending.</summary>
        public double[] FragilityStages = Array.Empty<double>();

        /// <summary>The fragility failure probabilities, parallel to the stages.</summary>
        public double[] FragilityProbabilities = Array.Empty<double>();

        /// <summary>The consequence curves' lower stage (zero consequence at or below).</summary>
        public double ConsequenceLower;

        /// <summary>The consequence curves' upper stage (saturated at or above).</summary>
        public double ConsequenceUpper;

        /// <summary>The failure consequence at the upper stage.</summary>
        public double FailureMaximum;

        /// <summary>The non-failure consequence at the upper stage.</summary>
        public double NonFailureMaximum;

        /// <summary>Evaluates the failure consequence curve.</summary>
        /// <param name="hazard">The hazard stage.</param>
        /// <returns>The failure consequence.</returns>
        public double FailureConsequence(double hazard)
        {
            if (hazard <= ConsequenceLower) return 0d;
            if (hazard >= ConsequenceUpper) return FailureMaximum;
            return (hazard - ConsequenceLower) / (ConsequenceUpper - ConsequenceLower) * FailureMaximum;
        }

        /// <summary>Evaluates the non-failure consequence curve.</summary>
        /// <param name="hazard">The hazard stage.</param>
        /// <returns>The non-failure consequence.</returns>
        public double NonFailureConsequence(double hazard)
        {
            if (hazard <= ConsequenceLower) return 0d;
            if (hazard >= ConsequenceUpper) return NonFailureMaximum;
            return (hazard - ConsequenceLower) / (ConsequenceUpper - ConsequenceLower) * NonFailureMaximum;
        }
    }

    /// <summary>Builds one component's scenario tables.</summary>
    /// <param name="name">The component name.</param>
    /// <param name="hazardMean">The hazard normal mean.</param>
    /// <param name="hazardSd">The hazard normal standard deviation.</param>
    /// <param name="fragilityCenter">The fragility center stage.</param>
    /// <param name="fragilitySpread">The fragility spread.</param>
    /// <param name="consequenceLower">The consequence onset stage.</param>
    /// <param name="consequenceUpper">The consequence saturation stage.</param>
    /// <param name="failureMaximum">The failure consequence at saturation.</param>
    /// <param name="nonFailureMaximum">The non-failure consequence at saturation.</param>
    /// <returns>The scenario.</returns>
    private static Scenario BuildScenario(string name, double hazardMean, double hazardSd,
        double fragilityCenter, double fragilitySpread,
        double consequenceLower, double consequenceUpper, double failureMaximum, double nonFailureMaximum)
    {
        int count = (int)Math.Round(2d * ZRange / ZStep) + 1;
        var scenario = new Scenario
        {
            Name = name,
            HazardProbabilities = new double[count],
            HazardStages = new double[count],
            FragilityStages = new double[count],
            FragilityProbabilities = new double[count],
            ConsequenceLower = consequenceLower,
            ConsequenceUpper = consequenceUpper,
            FailureMaximum = failureMaximum,
            NonFailureMaximum = nonFailureMaximum,
        };
        for (int i = 0; i < count; i++)
        {
            double z = -ZRange + i * ZStep;
            scenario.HazardProbabilities[i] = Normal.StandardCDF(z);
            scenario.HazardStages[i] = hazardMean + hazardSd * z;
            scenario.FragilityStages[i] = fragilityCenter + fragilitySpread * z;
            scenario.FragilityProbabilities[i] = Normal.StandardCDF(z);
        }
        return scenario;
    }

    /// <summary>Component A's scenario.</summary>
    private static Scenario ScenarioA() => BuildScenario("Dam", 100d, 20d, 140d, 30d, 60d, 200d, 1000d, 100d);

    /// <summary>Component B's scenario.</summary>
    private static Scenario ScenarioB() => BuildScenario("Levee", 80d, 15d, 115d, 20d, 50d, 160d, 400d, 40d);

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

    /// <summary>Builds the engine component for a scenario from the shared tables.</summary>
    /// <param name="scenario">The scenario.</param>
    /// <returns>The system component.</returns>
    private static SystemComponent BuildComponent(Scenario scenario)
    {
        var hazardOrdinates = new UncertainOrdinate[scenario.HazardStages.Length];
        for (int i = 0; i < scenario.HazardStages.Length; i++)
        {
            hazardOrdinates[i] = new UncertainOrdinate(1d - scenario.HazardProbabilities[i], new Deterministic(scenario.HazardStages[i]));
        }
        var hazard = new TabularHazard
        {
            Name = $"{scenario.Name} Stage Frequency",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            ProbabilityTransform = Transform.None,
            NoUncertaintyFunction = new UncertainOrderedPairedData(hazardOrdinates,
                true, SortOrder.Descending, true, SortOrder.Ascending, UnivariateDistributionType.Deterministic),
        };

        var fragilityOrdinates = new UncertainOrdinate[scenario.FragilityStages.Length];
        for (int i = 0; i < scenario.FragilityStages.Length; i++)
        {
            fragilityOrdinates[i] = new UncertainOrdinate(scenario.FragilityStages[i], new Deterministic(scenario.FragilityProbabilities[i]));
        }
        var fragility = new TabularResponse
        {
            Name = $"{scenario.Name} Fragility",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(fragilityOrdinates,
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };

        static TabularConsequence Consequence(string name, double lower, double upper, double maximum)
        {
            return new TabularConsequence
            {
                Name = name,
                SpecifiedHazard = "Stage",
                HazardUnit = "ft",
                SpecifiedConsequence = "Life Loss",
                ConsequenceUnit = "lives",
                UncertainOrderedPairedData = new UncertainOrderedPairedData(
                    new[] { new UncertainOrdinate(lower, new Deterministic(0d)), new UncertainOrdinate(upper, new Deterministic(maximum)) },
                    true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
            };
        }

        var component = new SystemComponent { Name = scenario.Name };
        component.HazardFunction = hazard;
        component.AddFailureMode(new FailureMode(null, null, fragility,
            Consequence($"{scenario.Name} Failure Loss", scenario.ConsequenceLower, scenario.ConsequenceUpper, scenario.FailureMaximum)));
        component.AddFailureMode(new FailureMode(null, null, null,
            Consequence($"{scenario.Name} Non-Failure Loss", scenario.ConsequenceLower, scenario.ConsequenceUpper, scenario.NonFailureMaximum)));
        return component;
    }

    #endregion

    #region Oracles

    /// <summary>One oracle run's accumulated system-loss statistics.</summary>
    private sealed class OracleResults
    {
        /// <summary>The mean system loss and its standard error.</summary>
        public double Mean, MeanSe;

        /// <summary>The system-loss standard deviation and its standard error.</summary>
        public double Sigma, SigmaSe;

        /// <summary>The any-component-failed frequency and its standard error.</summary>
        public double FailureUnion, FailureUnionSe;

        /// <summary>Per probe consequence: the exceedance frequency and its standard error.</summary>
        public double[] Exceedance = Array.Empty<double>(), ExceedanceSe = Array.Empty<double>();

        /// <summary>The empirical 1% exceedance quantile of the system loss.</summary>
        public double ValueAtRisk;

        /// <summary>The mean loss beyond the 1% quantile and its standard error.</summary>
        public double ConditionalValueAtRisk, ConditionalValueAtRiskSe;
    }

    /// <summary>
    /// The brute-force event-level oracle: draws annual events — correlated or independent
    /// hazards, Bernoulli failures against the fragilities — and accumulates the realized system
    /// loss distribution. Draw order per realization: the two hazard uniforms, then the two
    /// failure uniforms (documented for reproducibility).
    /// </summary>
    /// <param name="seed">The fixed oracle seed.</param>
    /// <param name="correlation">The hazard correlation (zero for the additive/independent oracle).</param>
    /// <returns>The accumulated statistics.</returns>
    private static OracleResults RunEventOracle(int seed, double correlation)
    {
        var prng = new MersenneTwister(seed);
        var a = ScenarioA();
        var b = ScenarioB();
        double cross = Math.Sqrt(1d - correlation * correlation);

        var totals = new double[OracleRealizations];
        long anyFailureCount = 0;
        var exceedCounts = new long[Probes.Length];

        for (int i = 0; i < OracleRealizations; i++)
        {
            double hazardUniformA = prng.NextDouble();
            double hazardUniformB = prng.NextDouble();
            double failureUniformA = prng.NextDouble();
            double failureUniformB = prng.NextDouble();

            double probabilityA, probabilityB;
            if (correlation == 0d)
            {
                probabilityA = hazardUniformA;
                probabilityB = hazardUniformB;
            }
            else
            {
                double zA = Normal.StandardZ(hazardUniformA);
                double zB = correlation * zA + cross * Normal.StandardZ(hazardUniformB);
                probabilityA = Normal.StandardCDF(zA);
                probabilityB = Normal.StandardCDF(zB);
            }

            double hazardA = Interpolate(a.HazardProbabilities, a.HazardStages, probabilityA);
            double hazardB = Interpolate(b.HazardProbabilities, b.HazardStages, probabilityB);
            double failureProbabilityA = Math.Max(0d, Math.Min(1d, Interpolate(a.FragilityStages, a.FragilityProbabilities, hazardA)));
            double failureProbabilityB = Math.Max(0d, Math.Min(1d, Interpolate(b.FragilityStages, b.FragilityProbabilities, hazardB)));
            bool failedA = failureUniformA < failureProbabilityA;
            bool failedB = failureUniformB < failureProbabilityB;

            double total = (failedA ? a.FailureConsequence(hazardA) : a.NonFailureConsequence(hazardA))
                + (failedB ? b.FailureConsequence(hazardB) : b.NonFailureConsequence(hazardB));
            totals[i] = total;
            if (failedA || failedB) anyFailureCount++;
            for (int p = 0; p < Probes.Length; p++)
            {
                if (total > Probes[p]) exceedCounts[p]++;
            }
        }

        // The moments and their in-run standard errors (mean from the variance; σ from the
        // fourth central moment).
        double mean = 0d;
        for (int i = 0; i < OracleRealizations; i++) mean += totals[i];
        mean /= OracleRealizations;
        double m2 = 0d, m4 = 0d;
        for (int i = 0; i < OracleRealizations; i++)
        {
            double delta = totals[i] - mean;
            m2 += delta * delta;
            m4 += delta * delta * delta * delta;
        }
        m2 /= OracleRealizations;
        m4 /= OracleRealizations;
        double sigma = Math.Sqrt(m2);

        var results = new OracleResults
        {
            Mean = mean,
            MeanSe = sigma / Math.Sqrt(OracleRealizations),
            Sigma = sigma,
            SigmaSe = Math.Sqrt(Math.Max(0d, m4 - m2 * m2) / OracleRealizations) / (2d * sigma),
            FailureUnion = (double)anyFailureCount / OracleRealizations,
            Exceedance = new double[Probes.Length],
            ExceedanceSe = new double[Probes.Length],
        };
        results.FailureUnionSe = Math.Sqrt(results.FailureUnion * (1d - results.FailureUnion) / OracleRealizations);
        for (int p = 0; p < Probes.Length; p++)
        {
            results.Exceedance[p] = (double)exceedCounts[p] / OracleRealizations;
            results.ExceedanceSe[p] = Math.Sqrt(results.Exceedance[p] * (1d - results.Exceedance[p]) / OracleRealizations);
        }

        // The empirical 1% tail: the quantile and the mean loss beyond it.
        Array.Sort(totals);
        int tailCount = (int)(0.01d * OracleRealizations);
        results.ValueAtRisk = totals[OracleRealizations - tailCount];
        double tailMean = 0d;
        for (int i = OracleRealizations - tailCount; i < OracleRealizations; i++) tailMean += totals[i];
        tailMean /= tailCount;
        double tailVariance = 0d;
        for (int i = OracleRealizations - tailCount; i < OracleRealizations; i++)
        {
            double delta = totals[i] - tailMean;
            tailVariance += delta * delta;
        }
        tailVariance /= tailCount;
        results.ConditionalValueAtRisk = tailMean;
        results.ConditionalValueAtRiskSe = Math.Sqrt(tailVariance / tailCount);
        return results;
    }

    #endregion

    /// <summary>
    /// The additive-method gate: the zero-inflated lattice convolution reproduces the
    /// brute-force system loss distribution — mean, standard deviation, failure union, the tail
    /// exceedances, and the 1% value-at-risk and conditional value-at-risk — where v1.0 reported
    /// two moments and no system curve at all.
    /// </summary>
    [TestMethod]
    public void Test_AdditiveSystem_LecVsBruteForceOracle()
    {
        // Arrange / Act
        var oracle = RunEventOracle(AdditiveOracleSeed, correlation: 0d);
        var analysis = new RiskAnalysis(new[] { BuildComponent(ScenarioA()), BuildComponent(ScenarioB()) })
        {
            Name = "Additive System",
        };
        analysis.RunAsync().GetAwaiter().GetResult();
        Assert.IsTrue(analysis.IsEstimated);
        var summary = analysis.RiskResults![0]!;
        var systemTotal = analysis.MeanRiskResults!.Curves.Total;

        // Assert — moments and the union.
        Assert.AreEqual(oracle.Mean, summary.Total.Mean, K * oracle.MeanSe, "System mean vs the event oracle.");
        Assert.AreEqual(oracle.Sigma, summary.Total.StandardDeviation, K * oracle.SigmaSe, "System standard deviation vs the event oracle.");
        Assert.AreEqual(oracle.FailureUnion, summary.Fail.TotalProbability, K * oracle.FailureUnionSe, "System failure union vs the event oracle.");

        // The exceedance ordinates across the curve (each probe populated enough to test).
        for (int p = 0; p < Probes.Length; p++)
        {
            Assert.IsTrue(oracle.Exceedance[p] * OracleRealizations > 100d, $"Probe {Probes[p]} is too sparse to verify.");
            double engineExceedance = systemTotal.LEC.GetYFromX(Probes[p], Transform.Logarithmic, Transform.Logarithmic);
            Assert.AreEqual(oracle.Exceedance[p], engineExceedance, K * oracle.ExceedanceSe[p],
                $"System exceedance at consequence {Probes[p]}.");
        }

        // Value-at-risk in probability space: the engine curve's exceedance at the oracle's
        // empirical 1% quantile is 1% within k·SE plus the output-resolution slack.
        double exceedanceAtOracleVar = systemTotal.LEC.GetYFromX(oracle.ValueAtRisk, Transform.Logarithmic, Transform.Logarithmic);
        double varSe = Math.Sqrt(0.01d * 0.99d / OracleRealizations);
        Assert.AreEqual(0.01d, exceedanceAtOracleVar, K * varSe + 1e-3,
            $"The engine exceedance at the oracle 1% quantile ({oracle.ValueAtRisk}).");

        // Conditional value-at-risk (expected shortfall) at the documented 5% envelope: the
        // engine integrates the log-log quantile of the thinned output curve, so interpolation
        // error joins the oracle's tail sampling error.
        Assert.AreEqual(oracle.ConditionalValueAtRisk, systemTotal.ConditionalValueAtRisk,
            K * oracle.ConditionalValueAtRiskSe + 0.05d * oracle.ConditionalValueAtRisk,
            "System conditional value-at-risk vs the oracle tail mean.");
    }

    /// <summary>
    /// Verifies the additive convolution and one-dimensional component integrations complete for
    /// a system larger than the former twenty-component ceiling.
    /// </summary>
    /// <remarks>
    /// This computational scalability case is intentionally kept in Verification rather than the
    /// sub-thirty-second unit gate. Joint-method admission is covered programmatically in the unit
    /// project because a 24-dimensional joint run is governed by its explicit combination budget.
    /// </remarks>
    [TestMethod]
    public void Test_AdditiveSystem_BeyondTwentyComponents_Completes()
    {
        var components = new SystemComponent[24];
        for (int i = 0; i < components.Length; i++)
        {
            components[i] = BuildComponent(ScenarioA());
            components[i].Name = $"Component {i + 1}";
        }
        var analysis = new RiskAnalysis(components);
        analysis.Options.SystemRiskMethod = SystemRiskType.AdditiveRiskMethod;

        analysis.RunAsync().GetAwaiter().GetResult();

        Assert.IsTrue(analysis.IsEstimated);
        Assert.AreEqual(24, analysis.MeanRiskResults!.Components.Count);
    }

    /// <summary>
    /// The joint-method gate: the correlated-hazard VEGAS integration with real combination
    /// enumeration reproduces the correlated brute-force oracle — mean, failure union, and the
    /// tail exceedances — and the per-evaluation additive-combine identity makes the system mean
    /// equal the sum of the component means exactly within the run.
    /// </summary>
    [TestMethod]
    public void Test_JointSystem_CorrelatedVsBruteForceOracle()
    {
        // Arrange / Act
        var oracle = RunEventOracle(JointOracleSeed, JointCorrelation);
        var analysis = new RiskAnalysis(new[] { BuildComponent(ScenarioA()), BuildComponent(ScenarioB()) })
        {
            Name = "Joint System",
        };
        analysis.Options.SystemRiskMethod = SystemRiskType.JointRiskMethod;
        analysis.Options.ComponentHazardDependency = DependencyType.CorrelationMatrix;
        analysis.Options.HazardCorrelationMatrix = new[,] { { 1d, JointCorrelation }, { JointCorrelation, 1d } };
        analysis.Options.VegasTailFocusMode = VegasTailFocusMode.None;
        analysis.RunAsync().GetAwaiter().GetResult();
        Assert.IsTrue(analysis.IsEstimated);
        var summary = analysis.RiskResults![0]!;
        var systemTotal = analysis.MeanRiskResults!.Curves.Total;

        // The engine side is Monte Carlo too: its reported VEGAS standard error (mean) and a
        // conservative binomial error at the recorded evaluation count (probabilities) combine
        // in quadrature with the oracle's.
        double recordedEvaluations = 5d * analysis.Options.FinalEvaluations;
        double meanTolerance = K * Math.Sqrt(oracle.MeanSe * oracle.MeanSe + summary.StandardError * summary.StandardError);
        Assert.AreEqual(oracle.Mean, summary.Total.Mean, meanTolerance, "Joint system mean vs the correlated event oracle.");

        double unionEngineSe = Math.Sqrt(oracle.FailureUnion * (1d - oracle.FailureUnion) / recordedEvaluations);
        Assert.AreEqual(oracle.FailureUnion, summary.Fail.TotalProbability,
            K * Math.Sqrt(oracle.FailureUnionSe * oracle.FailureUnionSe + unionEngineSe * unionEngineSe),
            "Joint failure union vs the correlated event oracle.");

        for (int p = 0; p < Probes.Length; p++)
        {
            double engineExceedance = systemTotal.LEC.GetYFromX(Probes[p], Transform.Logarithmic, Transform.Logarithmic);
            double engineSe = Math.Sqrt(oracle.Exceedance[p] * (1d - oracle.Exceedance[p]) / recordedEvaluations);
            Assert.AreEqual(oracle.Exceedance[p], engineExceedance,
                K * Math.Sqrt(oracle.ExceedanceSe[p] * oracle.ExceedanceSe[p] + engineSe * engineSe),
                $"Joint system exceedance at consequence {Probes[p]}.");
        }

        // The per-evaluation additive-combine identity: Σ over combinations of the enumerated
        // entries reproduces Σ over components exactly, so the recorded system mean equals the
        // sum of the recorded component means to roundoff within one run.
        double componentMeanSum = summary.ComponentResults[0].Total.Mean + summary.ComponentResults[1].Total.Mean;
        Assert.AreEqual(componentMeanSum, summary.Total.Mean, 1e-9 * componentMeanSum,
            "The combination enumeration must preserve the additive-combine mean identity exactly.");

        // The self-normalized exhaustive budget.
        Assert.AreEqual(1d, systemTotal.MassBalance, 1e-9, "The joint Total budget must self-normalize to one.");
    }

    /// <summary>
    /// The power-transform audit (the empirical gate before trusting γ &gt; 1): γ = 1, a
    /// manual γ = 4, and the automatic probe-driven focus must agree on the mean, the failure
    /// union, and a tail ordinate within their combined Monte Carlo errors, and every recorded
    /// budget must self-normalize to one — the Jacobian reaches the recorded weights.
    /// </summary>
    [TestMethod]
    public void Test_JointSystem_TailFocusAudit()
    {
        // Arrange
        RiskAnalysis Build(VegasTailFocusMode mode, double gamma)
        {
            var analysis = new RiskAnalysis(new[] { BuildComponent(ScenarioA()), BuildComponent(ScenarioB()) });
            analysis.Options.SystemRiskMethod = SystemRiskType.JointRiskMethod;
            analysis.Options.ComponentHazardDependency = DependencyType.CorrelationMatrix;
            analysis.Options.HazardCorrelationMatrix = new[,] { { 1d, JointCorrelation }, { JointCorrelation, 1d } };
            analysis.Options.VegasTailFocusMode = mode;
            analysis.Options.VegasTailFocusParameter = gamma;
            return analysis;
        }
        var baseline = Build(VegasTailFocusMode.None, 1d);
        var manual = Build(VegasTailFocusMode.Manual, 4d);
        var automatic = Build(VegasTailFocusMode.Automatic, 1d);

        // Act
        baseline.RunAsync().GetAwaiter().GetResult();
        manual.RunAsync().GetAwaiter().GetResult();
        automatic.RunAsync().GetAwaiter().GetResult();

        // Assert — unbiased means within combined reported errors; exact budgets.
        var baselineSummary = baseline.RiskResults![0]!;
        foreach (var (candidate, label) in new[] { (manual, "manual γ = 4"), (automatic, "automatic") })
        {
            var summary = candidate.RiskResults![0]!;
            double tolerance = K * Math.Sqrt(baselineSummary.StandardError * baselineSummary.StandardError
                + summary.StandardError * summary.StandardError);
            Assert.AreEqual(baselineSummary.Total.Mean, summary.Total.Mean, tolerance,
                $"The {label} tail focus must leave the system mean unbiased.");
            Assert.AreEqual(baselineSummary.Fail.TotalProbability, summary.Fail.TotalProbability,
                0.05d * baselineSummary.Fail.TotalProbability,
                $"The {label} tail focus must leave the failure union unbiased.");
            Assert.AreEqual(1d, candidate.MeanRiskResults!.Curves.Total.MassBalance, 1e-9,
                $"The {label} recorded budget must self-normalize to one (the Jacobian reaches the weights).");

            double baselineTail = baseline.MeanRiskResults!.Curves.Total.LEC.GetYFromX(Probes[2], Transform.Logarithmic, Transform.Logarithmic);
            double candidateTail = candidate.MeanRiskResults!.Curves.Total.LEC.GetYFromX(Probes[2], Transform.Logarithmic, Transform.Logarithmic);
            Assert.AreEqual(baselineTail, candidateTail, 0.35d * baselineTail,
                $"The {label} tail ordinate at consequence {Probes[2]} must agree with γ = 1 (both Monte Carlo).");
        }
    }

    /// <summary>
    /// The opt-in joint Sobol driver gate: with <c>UseSobolJointSampling</c> enabled the
    /// correlated joint system must still match the brute-force event oracle on the mean and
    /// the failure union, the recorded budget must self-normalize exactly (the tail-focus
    /// Jacobian reaches the weights under the quasi-random driver too, checked at a manual
    /// γ = 4 against the driver's own γ = 1 run), and two enabled runs must publish
    /// byte-identical results — the seeded scrambling restoring the content-seed contract the
    /// v1.0 unrandomized sequence could not honor.
    /// </summary>
    /// <remarks>
    /// <b>Tolerance derivation:</b> the oracle comparison reuses the correlated-oracle test's
    /// combined-error bounds (the engine side quotes its recording-pass VEGAS standard error;
    /// the union adds a conservative binomial error at the recorded evaluation count); the
    /// γ agreement uses the combined reported errors with the same 5% union band as the
    /// pseudo-random audit; the budget and reproducibility pins are exact (1e-9 mass; 0 byte).
    /// </remarks>
    [TestMethod]
    public void Test_JointSystem_SobolDriver_OracleParityAndReproducibility()
    {
        // Arrange
        RiskAnalysis Build(VegasTailFocusMode mode, double gamma)
        {
            var analysis = new RiskAnalysis(new[] { BuildComponent(ScenarioA()), BuildComponent(ScenarioB()) });
            analysis.Options.SystemRiskMethod = SystemRiskType.JointRiskMethod;
            analysis.Options.ComponentHazardDependency = DependencyType.CorrelationMatrix;
            analysis.Options.HazardCorrelationMatrix = new[,] { { 1d, JointCorrelation }, { JointCorrelation, 1d } };
            analysis.Options.VegasTailFocusMode = mode;
            analysis.Options.VegasTailFocusParameter = gamma;
            analysis.Options.UseSobolJointSampling = true;
            return analysis;
        }

        // Act
        var oracle = RunEventOracle(JointOracleSeed, JointCorrelation);
        var driver = Build(VegasTailFocusMode.None, 1d);
        driver.RunAsync().GetAwaiter().GetResult();
        var repeat = Build(VegasTailFocusMode.None, 1d);
        repeat.RunAsync().GetAwaiter().GetResult();
        var focused = Build(VegasTailFocusMode.Manual, 4d);
        focused.RunAsync().GetAwaiter().GetResult();

        // Assert — oracle parity under the quasi-random driver.
        var summary = driver.RiskResults![0]!;
        double recordedEvaluations = 5d * driver.Options.FinalEvaluations;
        double meanTolerance = K * Math.Sqrt(oracle.MeanSe * oracle.MeanSe + summary.StandardError * summary.StandardError);
        Assert.AreEqual(oracle.Mean, summary.Total.Mean, meanTolerance,
            "The Sobol-driven joint system mean must match the correlated event oracle.");
        double unionEngineSe = Math.Sqrt(oracle.FailureUnion * (1d - oracle.FailureUnion) / recordedEvaluations);
        Assert.AreEqual(oracle.FailureUnion, summary.Fail.TotalProbability,
            K * Math.Sqrt(oracle.FailureUnionSe * oracle.FailureUnionSe + unionEngineSe * unionEngineSe),
            "The Sobol-driven joint failure union must match the correlated event oracle.");
        Assert.AreEqual(1d, driver.MeanRiskResults!.Curves.Total.MassBalance, 1e-9,
            "The Sobol-driven recorded budget must self-normalize to one.");

        // The tail-focus Jacobian under the quasi-random driver.
        var focusedSummary = focused.RiskResults![0]!;
        double focusTolerance = K * Math.Sqrt(summary.StandardError * summary.StandardError
            + focusedSummary.StandardError * focusedSummary.StandardError);
        Assert.AreEqual(summary.Total.Mean, focusedSummary.Total.Mean, focusTolerance,
            "Manual γ = 4 under the Sobol driver must leave the system mean unbiased.");
        Assert.AreEqual(summary.Fail.TotalProbability, focusedSummary.Fail.TotalProbability,
            0.05d * summary.Fail.TotalProbability,
            "Manual γ = 4 under the Sobol driver must leave the failure union unbiased.");
        Assert.AreEqual(1d, focused.MeanRiskResults!.Curves.Total.MassBalance, 1e-9,
            "The focused Sobol-driven budget must self-normalize to one (the Jacobian reaches the weights).");

        // The restored reproducibility contract.
        Assert.AreEqual(driver.RiskResults.ToJson(), repeat.RiskResults!.ToJson(),
            "Two Sobol-driven runs must publish byte-identical results.");
    }

    /// <summary>
    /// The system-level reproducibility pins: reordering plus renaming is bit-inert on the
    /// additive path (content seeding plus the canonical-hash convolution order), and renaming
    /// is bit-inert on the joint path.
    /// </summary>
    [TestMethod]
    public void Test_SystemReproducibility_ShuffleAndRename()
    {
        // The additive path: [A, B] versus renamed [B, A] — bit-identical system results.
        var forward = new RiskAnalysis(new[] { BuildComponent(ScenarioA()), BuildComponent(ScenarioB()) });
        forward.RunAsync().GetAwaiter().GetResult();

        var shuffledB = BuildComponent(ScenarioB());
        var shuffledA = BuildComponent(ScenarioA());
        shuffledB.Name = "Levee (renamed)";
        shuffledA.Name = "Dam (renamed)";
        var shuffled = new RiskAnalysis(new SystemComponent[] { shuffledB, shuffledA }) { Name = "Shuffled" };
        shuffled.RunAsync().GetAwaiter().GetResult();

        Assert.AreEqual(
            BitConverter.DoubleToInt64Bits(forward.RiskResults![0]!.Total.Mean),
            BitConverter.DoubleToInt64Bits(shuffled.RiskResults![0]!.Total.Mean),
            "The additive system mean must be bit-inert under component reordering and renaming.");
        CollectionAssert.AreEqual(
            forward.MeanRiskResults!.Curves.Total.LECProbabilities,
            shuffled.MeanRiskResults!.Curves.Total.LECProbabilities,
            "The additive system curve must be bit-inert under component reordering and renaming.");

        // The component streams follow content, not position: forward's A is shuffled's second.
        Assert.AreEqual(
            BitConverter.DoubleToInt64Bits(forward.RiskResults[0]!.ComponentResults[0].Fail.Mean),
            BitConverter.DoubleToInt64Bits(shuffled.RiskResults[0]!.ComponentResults[1].Fail.Mean),
            "Component results must follow content identity through the shuffle.");

        // The joint path: renaming everything is bit-inert (reordering is documented
        // order-sensitive — the VEGAS variates couple the hypercube dimensions).
        RiskAnalysis BuildJoint(string suffix)
        {
            var componentA = BuildComponent(ScenarioA());
            var componentB = BuildComponent(ScenarioB());
            componentA.Name += suffix;
            componentB.Name += suffix;
            var analysis = new RiskAnalysis(new[] { componentA, componentB }) { Name = $"Joint{suffix}" };
            analysis.Options.SystemRiskMethod = SystemRiskType.JointRiskMethod;
            analysis.Options.UseDefaults = false;
            analysis.Options.WarmupEvaluations = 500;
            analysis.Options.WarmupCycles = 2;
            analysis.Options.FinalEvaluations = 2000;
            return analysis;
        }
        var joint = BuildJoint(string.Empty);
        var renamed = BuildJoint(" (renamed)");
        joint.RunAsync().GetAwaiter().GetResult();
        renamed.RunAsync().GetAwaiter().GetResult();
        Assert.AreEqual(
            BitConverter.DoubleToInt64Bits(joint.RiskResults![0]!.Total.Mean),
            BitConverter.DoubleToInt64Bits(renamed.RiskResults![0]!.Total.Mean),
            "The joint system mean must be bit-inert under renaming.");
        CollectionAssert.AreEqual(
            joint.MeanRiskResults!.Curves.Fail.LECProbabilities,
            renamed.MeanRiskResults!.Curves.Fail.LECProbabilities,
            "The joint system curve must be bit-inert under renaming.");
    }
}
