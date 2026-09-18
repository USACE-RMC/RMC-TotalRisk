using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics;
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
/// Combination-method consistency — a family of engine-only property tests with no legacy
/// counterpart, grounded in the failure-mode-combination technical note: the system failure probability
/// depends on the marginal response curves and the dependency structure, NOT on the
/// combination method; the unimodal (Fréchet) bounds order the unions; the background risk is
/// invariant to every combination option; the adaptive-refinement objective steers only where
/// evaluations land; and reliability mode reproduces the risk-mode failure probability.
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// <para>
/// <b>Scenario:</b> the shared legacy Bucket-1 model (LnNormal(85, 20) hazard, the PFM-1/PFM-2
/// Normal fragilities, the legacy five-knot consequence curves — see
/// <see cref="JointFailuresVerification"/>), all runs mean-only, so every comparison is
/// quadrature against quadrature: the invariance tolerances are numerical-integration scales
/// (1e-6 relative for exact identities), not Monte Carlo statistics. The two documented
/// approximation carve-outs: the competing method's union rides its 200-level
/// cumulative-incidence discretization (asserted at 1%), and the correlation-matrix union
/// crosses two different dependent-probability kernels (the common-cause joint-probability
/// evaluation versus the joint method's product-of-conditional-marginals pathway enumeration
/// — asserted at 1%, the v1.0-preserved approximations).
/// </para>
/// </remarks>
[TestClass]
public class CombinationMethodConsistencyVerification
{
    /// <summary>The hazard z-grid step (±8 range).</summary>
    private const double HazardZStep = 0.1d;

    /// <summary>The fragility z-grid step (±8σ range).</summary>
    private const double FragilityZStep = 0.05d;

    /// <summary>The z-grid half-range of every tabulated curve.</summary>
    private const double ZRange = 8d;

    /// <summary>The legacy fragility means for PFM-1..PFM-2.</summary>
    private static readonly double[] FragilityMeans = { 140d, 160d };

    /// <summary>The legacy fragility standard deviations for PFM-1..PFM-2.</summary>
    private static readonly double[] FragilitySds = { 30d, 10d };

    /// <summary>The legacy consequence-curve stages.</summary>
    private static readonly double[] ConsequenceStages = { 60d, 100d, 140d, 200d, 250d };

    /// <summary>The legacy failure-consequence ordinates for PFM-1..PFM-2.</summary>
    private static readonly double[][] FailureValues =
    {
        new[] { 0d, 5d, 50d, 500d, 750d },
        new[] { 0d, 3d, 30d, 300d, 450d },
    };

    /// <summary>The legacy non-failure-consequence ordinates.</summary>
    private static readonly double[] NonFailureValues = { 0d, 1d, 10d, 100d, 150d };

    #region Builders

    /// <summary>Builds the engine analysis for one 2-PFM configuration of the shared scenario.</summary>
    /// <param name="method">The failure-mode combination method.</param>
    /// <param name="dependency">The failure-mode dependency (applied after the method — the coercion order).</param>
    /// <param name="userMatrix">The user correlation matrix (correlation-matrix mode only).</param>
    /// <param name="mode">The analysis mode.</param>
    /// <param name="integrand">The adaptive-refinement objective.</param>
    private static RiskAnalysis Build(FailureModeMethod method, DependencyType dependency, double[,]? userMatrix,
        RiskAnalysisMode mode = RiskAnalysisMode.Risk, RiskIntegrand integrand = RiskIntegrand.MeanTotalRisk)
    {
        var hazardDistribution = new LnNormal(85d, 20d);
        int hazardCount = (int)Math.Round(2d * ZRange / HazardZStep) + 1;
        var hazardOrdinates = new UncertainOrdinate[hazardCount];
        for (int i = 0; i < hazardCount; i++)
        {
            double z = -ZRange + i * HazardZStep;
            double probability = Normal.StandardCDF(z);
            hazardOrdinates[i] = new UncertainOrdinate(1d - probability, new Deterministic(hazardDistribution.InverseCDF(probability)));
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

        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = hazard;
        int fragilityCount = (int)Math.Round(2d * ZRange / FragilityZStep) + 1;
        for (int modeIndex = 0; modeIndex < 2; modeIndex++)
        {
            var fragilityOrdinates = new UncertainOrdinate[fragilityCount];
            for (int i = 0; i < fragilityCount; i++)
            {
                double z = -ZRange + i * FragilityZStep;
                fragilityOrdinates[i] = new UncertainOrdinate(FragilityMeans[modeIndex] + FragilitySds[modeIndex] * z, new Deterministic(Normal.StandardCDF(z)));
            }
            var fragility = new TabularResponse
            {
                Name = $"PFM-{modeIndex + 1} Fragility",
                SpecifiedHazard = "Stage",
                HazardUnit = "ft",
                UncertainOrderedPairedData = new UncertainOrderedPairedData(fragilityOrdinates,
                    true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
            };
            component.AddFailureMode(new FailureMode(null, null, fragility, Consequence($"PFM-{modeIndex + 1} Loss", FailureValues[modeIndex])));
        }
        component.AddFailureMode(new FailureMode(null, null, null, Consequence("Non-Failure Loss", NonFailureValues)));

        component.FailureModeMethod = method;
        component.FailureModeDependency = dependency;
        if (userMatrix != null)
        {
            component.CorrelationMatrix = (double[,])userMatrix.Clone();
        }

        var analysis = new RiskAnalysis(new[] { component }) { Name = $"{method} {dependency}" };
        analysis.Options.Mode = mode;
        analysis.Options.RiskIntegrand = integrand;
        return analysis;
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

    /// <summary>Runs one configuration mean-only and returns its summary.</summary>
    /// <param name="analysis">The analysis to run.</param>
    private static Results.SystemRiskResults Run(RiskAnalysis analysis)
    {
        analysis.RunAsync().GetAwaiter().GetResult();
        Assert.IsTrue(analysis.IsEstimated, $"{analysis.Name} must estimate.");
        return analysis.RiskResults![0]!;
    }

    #endregion

    /// <summary>
    /// The technical note's key observation (§6.1): every combination method that models
    /// dependency produces the SAME system failure probability for the same marginals and
    /// dependency. Joint and common-cause agree at quadrature scale under independence; the
    /// competing union rides its 200-level cumulative-incidence discretization (1%).
    /// </summary>
    [TestMethod]
    public void Test_UnionInvariance_Independent()
    {
        double joint = Run(Build(FailureModeMethod.JointFailures, DependencyType.Independent, null)).Fail.TotalProbability;
        double commonCause = Run(Build(FailureModeMethod.CommonCauseFailures, DependencyType.Independent, null)).Fail.TotalProbability;
        double competing = Run(Build(FailureModeMethod.CompetingFailures, DependencyType.Independent, null)).Fail.TotalProbability;

        Assert.AreEqual(joint, commonCause, 1e-6 * joint, "Joint and common-cause unions under independence.");
        Assert.AreEqual(joint, competing, 1e-2 * joint, "The competing union within its cumulative-incidence discretization.");
        Console.WriteLine($"Independent union: joint {joint:G8}, common-cause {commonCause:G8}, competing {competing:G8}");
    }

    /// <summary>
    /// Union invariance under a user correlation matrix (ρ = 0.5): the common-cause factor and
    /// the joint pathway enumeration evaluate the same union through two different dependent
    /// kernels (the joint-probability evaluation versus the product of conditional marginals),
    /// and the competing method through a third (correlated cumulative incidence) — all agree
    /// within the documented 1% approximation scale.
    /// </summary>
    [TestMethod]
    public void Test_UnionInvariance_CorrelationMatrix()
    {
        var matrix = Equicorrelated(2, 0.5d);
        double joint = Run(Build(FailureModeMethod.JointFailures, DependencyType.CorrelationMatrix, matrix)).Fail.TotalProbability;
        double commonCause = Run(Build(FailureModeMethod.CommonCauseFailures, DependencyType.CorrelationMatrix, matrix)).Fail.TotalProbability;
        double competing = Run(Build(FailureModeMethod.CompetingFailures, DependencyType.CorrelationMatrix, matrix)).Fail.TotalProbability;

        Assert.AreEqual(joint, commonCause, 1e-2 * joint, "Joint and common-cause unions at ρ = 0.5.");
        Assert.AreEqual(joint, competing, 1e-2 * joint, "The competing union at ρ = 0.5.");
        Console.WriteLine($"ρ = 0.5 union: joint {joint:G8}, common-cause {commonCause:G8}, competing {competing:G8}");
    }

    /// <summary>
    /// The unimodal (Fréchet) bound ordering on the failure union, realized by the joint
    /// method's dependency options and capped by the mutually-exclusive sum: perfectly
    /// positive ≤ ρ = 0.5 ≤ independent ≤ perfectly negative ≤ mutually exclusive.
    /// </summary>
    [TestMethod]
    public void Test_FrechetBoundOrdering()
    {
        double positive = Run(Build(FailureModeMethod.JointFailures, DependencyType.PerfectlyPositive, null)).Fail.TotalProbability;
        double correlated = Run(Build(FailureModeMethod.JointFailures, DependencyType.CorrelationMatrix, Equicorrelated(2, 0.5d))).Fail.TotalProbability;
        double independent = Run(Build(FailureModeMethod.JointFailures, DependencyType.Independent, null)).Fail.TotalProbability;
        double negative = Run(Build(FailureModeMethod.JointFailures, DependencyType.PerfectlyNegative, null)).Fail.TotalProbability;
        double exclusive = Run(Build(FailureModeMethod.MutuallyExclusive, DependencyType.Independent, null)).Fail.TotalProbability;

        Assert.IsTrue(positive < correlated, $"Perfectly positive ({positive:G6}) must sit below ρ = 0.5 ({correlated:G6}).");
        Assert.IsTrue(correlated < independent, $"ρ = 0.5 ({correlated:G6}) must sit below independent ({independent:G6}).");
        Assert.IsTrue(independent < negative, $"Independent ({independent:G6}) must sit below perfectly negative ({negative:G6}).");
        Assert.IsTrue(negative <= exclusive * (1d + 1e-9), $"Perfectly negative ({negative:G6}) must not exceed the capped exclusive sum ({exclusive:G6}).");
        Console.WriteLine($"Fréchet ordering: positive {positive:G6} < ρ0.5 {correlated:G6} < independent {independent:G6} < negative {negative:G6} ≤ exclusive {exclusive:G6}");
    }

    /// <summary>
    /// Background invariance: the background risk integrates the hazard against the
    /// non-failure consequence alone, so every combination method, dependency, and
    /// joint-consequence rule must reproduce it.
    /// </summary>
    /// <remarks>
    /// The allowance is the integrator's own relative tolerance, not an exact match. The
    /// combination method changes the total integrand, so the adaptive mesh accepts a
    /// different interval set per configuration, and the background stream is integrated on
    /// whichever mesh the total drove. Its recorded mass is the Kronrod weight of that mesh,
    /// so the background agrees to the integration tolerance and no tighter (measured worst
    /// case 6.9e-9 relative, against a 1e-8 configured tolerance).
    /// </remarks>
    [TestMethod]
    public void Test_BackgroundInvariance()
    {
        double reference = Run(Build(FailureModeMethod.JointFailures, DependencyType.Independent, null)).Background.Mean;
        var configurations = new (FailureModeMethod Method, DependencyType Dependency, double[,]? Matrix)[]
        {
            (FailureModeMethod.JointFailures, DependencyType.PerfectlyPositive, null),
            (FailureModeMethod.JointFailures, DependencyType.PerfectlyNegative, null),
            (FailureModeMethod.JointFailures, DependencyType.CorrelationMatrix, Equicorrelated(2, 0.5d)),
            (FailureModeMethod.CompetingFailures, DependencyType.Independent, null),
            (FailureModeMethod.CompetingFailures, DependencyType.PerfectlyNegative, null),
            (FailureModeMethod.CommonCauseFailures, DependencyType.Independent, null),
            (FailureModeMethod.CommonCauseFailures, DependencyType.PerfectlyPositive, null),
            (FailureModeMethod.MutuallyExclusive, DependencyType.Independent, null),
        };
        foreach (var configuration in configurations)
        {
            double background = Run(Build(configuration.Method, configuration.Dependency, configuration.Matrix)).Background.Mean;
            Assert.AreEqual(reference, background, 1e-8 * reference,
                $"Background risk must be invariant ({configuration.Method} + {configuration.Dependency}).");
        }
    }

    /// <summary>
    /// The adaptive-refinement objective steers only where evaluations concentrate — every
    /// <see cref="RiskIntegrand"/> member reproduces the default's total mean and failure
    /// union within 1e-4 relative (different bin refinement, same integrals; the two
    /// tail-focused objectives shift a kink-heavy integrand's placement the most) and always
    /// produces all five risk streams.
    /// </summary>
    [TestMethod]
    public void Test_RiskIntegrandInvariance()
    {
        var reference = Run(Build(FailureModeMethod.JointFailures, DependencyType.Independent, null));
        foreach (RiskIntegrand integrand in Enum.GetValues<RiskIntegrand>())
        {
            var summary = Run(Build(FailureModeMethod.JointFailures, DependencyType.Independent, null, integrand: integrand));
            Assert.AreEqual(reference.Total.Mean, summary.Total.Mean, 1e-4 * reference.Total.Mean,
                $"Total mean under objective {integrand}.");
            Assert.AreEqual(reference.Fail.TotalProbability, summary.Fail.TotalProbability, 1e-4 * reference.Fail.TotalProbability,
                $"Failure union under objective {integrand}.");
            Assert.IsTrue(summary.Excess.Mean > 0d && summary.Background.Mean > 0d && summary.NonFail.Mean > 0d,
                $"All risk streams must be produced under objective {integrand}.");
        }
    }

    /// <summary>
    /// Reliability-mode parity: the annualized failure probability in reliability mode
    /// reproduces the risk-mode union for each combination method at 1e-4 relative — the same
    /// scale as <see cref="Test_RiskIntegrandInvariance"/>, because reliability mode forces the
    /// failure-probability refinement objective and the recorded-mass placement legitimately
    /// shifts with the refinement (measured ≈ 2e-6 here).
    /// </summary>
    [TestMethod]
    public void Test_ReliabilityParity()
    {
        foreach (var method in new[] { FailureModeMethod.JointFailures, FailureModeMethod.CommonCauseFailures, FailureModeMethod.CompetingFailures })
        {
            double risk = Run(Build(method, DependencyType.Independent, null)).Fail.TotalProbability;
            double reliability = Run(Build(method, DependencyType.Independent, null, mode: RiskAnalysisMode.Reliability)).Fail.TotalProbability;
            Assert.AreEqual(risk, reliability, 1e-4 * risk, $"Reliability-mode AFP must match the risk-mode union ({method}).");
        }
    }
}
