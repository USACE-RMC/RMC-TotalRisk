using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Data.Statistics;
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
/// Latent-factor capacity dependence — the greenfield family for the
/// <see cref="DependencyType.LatentFactors"/> mode: named factors with per-combination-unit
/// loadings inducing ρij = Σf λif·λjf into the existing Gaussian combination kernels, verified
/// by a bit-exact dense-matrix equivalence twin, the exchangeable one-factor exact integral
/// with the shipped product-of-conditional-marginals error measured and documented, a
/// 1,000,000-draw multi-factor Monte Carlo oracle, a 1,000,000-draw competing-risks oracle at
/// three units (the seeded-lattice dimension), and reproducibility/metadata-inertness pins.
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// <para>
/// <b>Scenario tables</b> (the shared Bucket-1 model where a joint or competing engine run is
/// exercised): hazard = LnNormal(85, 20) (real-space moments) tabulated on a z-grid of ±8 at
/// step 0.1; fragilities = Normal CDFs — PFM-1 (140, 30), PFM-2 (160, 10), PFM-3 (150, 20) —
/// each tabulated at ±8σ step 0.05σ; consequences = the exact legacy five-knot curves over
/// stages {60, 100, 140, 200, 250} with flat end clamps. The union tests instead use constant
/// (hazard-independent) failure probabilities so the annualized failure probability equals the
/// between-mode union exactly and the dependence model is isolated from the hazard axis.
/// </para>
/// <para>
/// <b>Equivalence contract:</b> the latent-factors mode derives its matrix with a unit
/// diagonal assigned exactly and off-diagonals accumulated in declared factor order, then
/// feeds the SAME kernels a user matrix feeds. A latent-factors model and a
/// correlation-matrix twin authored with the identically computed induced matrix therefore
/// agree bit-for-bit on every deterministic-model result — the twins' canonical hashes differ
/// (selecting the mode is the deliberate hash event), but deterministic functions sample
/// identically under any seed, so the seed difference is inert (the configuration-risk
/// family's twin discipline).
/// </para>
/// <para>
/// <b>The engine's dependent-union approximation, measured honestly:</b> the joint-failures
/// dependent path evaluates the Gaussian-copula union through the product-of-conditional
/// -marginals recursion (pairwise bivariate normal probabilities), not the exact orthant, and
/// the lazy exclusive enumeration converges within its documented 1e-4 tolerance. The
/// exchangeable test therefore anchors THREE quantities: the test-local one-factor quadrature
/// against <c>Probability.UnionSingleFactor</c> (two independent exact evaluations, 5e-8
/// relative); the engine against the directly evaluated PCM union (the same approximation
/// class, 2e-4 absolute — the lazy convergence tolerance); and the engine against the exact
/// union within the MEASURED PCM error plus headroom. Measured on the pinned fixture
/// (4 units, common λ = 0.7, p = {0.05, 0.10, 0.15, 0.08}): exact union 0.259005, PCM union
/// 0.258787 — a relative PCM error of 8.4e-4, asserted with headroom at 2.5e-3 relative.
/// </para>
/// <para>
/// <b>Oracle mechanics:</b> the multi-factor Monte Carlo oracle draws, per trial from ONE
/// <c>MersenneTwister(12345)</c> stream in documented order (factor draws first, then one
/// idiosyncratic draw per unit), the latent capacities U_i = Σf λif·z_f + √(1 − Σf λif²)·ε_i,
/// failing unit i when U_i ≤ Φ⁻¹(p_i); N = 1,000,000, binomial 4·SE plus the measured PCM
/// allowance. The competing oracle mirrors the legacy competing convention (hazard uniforms
/// from <c>MersenneTwister(12345)</c>, capacity draws pre-generated from a second
/// <c>MersenneTwister(12345)</c> stream in the same factor order, resistance = the tabulated
/// fragility inverted at Φ(U), the weakest exceeded mode wins, incremental = max(0, fC − nfC))
/// at three units so the engine side exercises the seeded Genz lattice inside the
/// cumulative-incidence pre-processing (D ≥ 3 — the dimension at which a seeding regression
/// is detectable).
/// </para>
/// <para>
/// <b>Tolerances:</b> engine-versus-oracle asserts use k·SE with k = 4 (binomial SEs for
/// probabilities, Welford mean SEs for consequence streams) plus, where the dependent union is
/// involved, the measured PCM allowance above; the competing comparison adds the family's
/// documented cumulative-incidence discretization allowance of 0.3% relative (the
/// competing-failures family's precedent for the same tables).
/// </para>
/// </remarks>
[TestClass]
public class LatentFactorVerification
{
    /// <summary>The oracle realization count (the conversion policy's 1M).</summary>
    private const int OracleRealizations = 1_000_000;

    /// <summary>The legacy stream seed.</summary>
    private const int OracleSeed = 12345;

    /// <summary>The tolerance multiplier on the Monte Carlo standard error.</summary>
    private const double K = 4d;

    /// <summary>The hazard z-grid step (±8 range).</summary>
    private const double HazardZStep = 0.1d;

    /// <summary>The fragility z-grid step (±8σ range).</summary>
    private const double FragilityZStep = 0.05d;

    /// <summary>The z-grid half-range of every tabulated curve.</summary>
    private const double ZRange = 8d;

    /// <summary>The legacy fragility means for PFM-1..PFM-3.</summary>
    private static readonly double[] FragilityMeans = { 140d, 160d, 150d };

    /// <summary>The legacy fragility standard deviations for PFM-1..PFM-3.</summary>
    private static readonly double[] FragilitySds = { 30d, 10d, 20d };

    /// <summary>The legacy consequence-curve stages.</summary>
    private static readonly double[] ConsequenceStages = { 60d, 100d, 140d, 200d, 250d };

    /// <summary>The legacy failure-consequence ordinates for PFM-1..PFM-3.</summary>
    private static readonly double[][] FailureValues =
    {
        new[] { 0d, 5d, 50d, 500d, 750d },
        new[] { 0d, 3d, 30d, 300d, 450d },
        new[] { 0d, 10d, 100d, 1000d, 1500d },
    };

    /// <summary>The legacy non-failure-consequence ordinates.</summary>
    private static readonly double[] NonFailureValues = { 0d, 1d, 10d, 100d, 150d };

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

    /// <summary>Builds the deterministic tabular hazard from the shared table.</summary>
    private static TabularHazard Hazard()
    {
        var (probabilities, stages) = HazardTable();
        var ordinates = new UncertainOrdinate[stages.Length];
        for (int i = 0; i < stages.Length; i++)
        {
            ordinates[i] = new UncertainOrdinate(1d - probabilities[i], new Deterministic(stages[i]));
        }
        return new TabularHazard
        {
            Name = "Stage Frequency",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            ProbabilityTransform = Transform.None,
            NoUncertaintyFunction = new UncertainOrderedPairedData(ordinates,
                true, SortOrder.Descending, true, SortOrder.Ascending, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds the mode's deterministic tabulated fragility from the shared table.</summary>
    /// <param name="mode">The zero-based failure-mode index.</param>
    private static TabularResponse Fragility(int mode)
    {
        var (stages, probabilities) = FragilityTable(mode);
        var ordinates = new UncertainOrdinate[stages.Length];
        for (int i = 0; i < stages.Length; i++)
        {
            ordinates[i] = new UncertainOrdinate(stages[i], new Deterministic(probabilities[i]));
        }
        return new TabularResponse
        {
            Name = $"PFM-{mode + 1} Fragility",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(ordinates,
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds a constant (hazard-independent) fragility spanning the stage support.</summary>
    /// <param name="name">The function name.</param>
    /// <param name="probability">The constant failure probability.</param>
    private static TabularResponse ConstantFragility(string name, double probability)
    {
        return new TabularResponse
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(0d, new Deterministic(probability)),
                    new UncertainOrdinate(500d, new Deterministic(probability)),
                },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
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
    /// Computes the loadings-induced correlation matrix exactly as the component derives it:
    /// unit diagonal assigned, off-diagonals accumulated in declared factor order — the twin
    /// authoring side of the bit-exact dense-equivalence contract.
    /// </summary>
    /// <param name="loadings">The per-factor loading vectors.</param>
    private static double[,] InducedMatrix(double[][] loadings)
    {
        int dimension = loadings[0].Length;
        var matrix = new double[dimension, dimension];
        for (int i = 0; i < dimension; i++)
        {
            matrix[i, i] = 1d;
            for (int j = i + 1; j < dimension; j++)
            {
                double correlation = 0d;
                for (int f = 0; f < loadings.Length; f++)
                {
                    correlation += loadings[f][i] * loadings[f][j];
                }
                matrix[i, j] = correlation;
                matrix[j, i] = correlation;
            }
        }
        return matrix;
    }

    /// <summary>
    /// Builds the three-mode Bucket-1 joint model (deterministic tables; a lean realization
    /// count because every realization of a deterministic model is identical).
    /// </summary>
    /// <param name="configure">Applies the dependence configuration to the component.</param>
    private static RiskAnalysis BuildJointAnalysis(Action<SystemComponent> configure)
    {
        var component = new SystemComponent { Name = "Dam", HazardFunction = Hazard() };
        for (int mode = 0; mode < 3; mode++)
        {
            component.AddFailureMode(new FailureMode(null, null, Fragility(mode), Consequence($"PFM-{mode + 1} Loss", FailureValues[mode])));
        }
        component.AddFailureMode(new FailureMode(null, null, null, Consequence("Non-Failure Loss", NonFailureValues)));
        component.FailureModeMethod = FailureModeMethod.JointFailures;
        configure(component);

        var analysis = new RiskAnalysis(new[] { component }) { Name = "Latent Factors" };
        analysis.Options.Realizations = 100;
        return analysis;
    }

    #endregion

    /// <summary>
    /// The bit-exact dense-equivalence pin: a latent-factors model and a correlation-matrix
    /// twin authored with the identically computed induced matrix must agree bit-for-bit on the
    /// deterministic Bucket-1 model — the summary means, standard deviations, tail measures,
    /// and every mean loss-exceedance ordinate. The twins' canonical hashes deliberately
    /// differ; deterministic functions sample identically under any seed, so the equality is
    /// exact, not statistical.
    /// </summary>
    [TestMethod]
    public void Test_DenseEquivalence_BitIdentical()
    {
        // Arrange — two factors over the three failure paths.
        var loadings = new[] { new[] { 0.8d, 0.6d, 0.5d }, new[] { 0.3d, -0.4d, 0.2d } };
        var factorAnalysis = BuildJointAnalysis(component =>
        {
            component.FailureModeDependency = DependencyType.LatentFactors;
            component.AddLatentFactor(new LatentFactor("Soil Unit", loadings[0]));
            component.AddLatentFactor(new LatentFactor("Design Era", loadings[1]));
        });
        var matrixAnalysis = BuildJointAnalysis(component =>
        {
            component.FailureModeDependency = DependencyType.CorrelationMatrix;
            component.CorrelationMatrix = InducedMatrix(loadings);
        });

        // Act
        factorAnalysis.RunAsync().GetAwaiter().GetResult();
        matrixAnalysis.RunAsync().GetAwaiter().GetResult();
        Assert.IsTrue(factorAnalysis.IsEstimated && matrixAnalysis.IsEstimated, "Both twins must estimate.");

        // Assert — bit identity across the summary scalars and the mean LEC surfaces.
        var factorSummary = factorAnalysis.RiskResults![0]!;
        var matrixSummary = matrixAnalysis.RiskResults![0]!;
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(matrixSummary.Fail.Mean), BitConverter.DoubleToInt64Bits(factorSummary.Fail.Mean),
            "The failure risk mean must be bit-identical between the twins.");
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(matrixSummary.Total.Mean), BitConverter.DoubleToInt64Bits(factorSummary.Total.Mean),
            "The total risk mean must be bit-identical.");
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(matrixSummary.Excess.Mean), BitConverter.DoubleToInt64Bits(factorSummary.Excess.Mean),
            "The incremental risk mean must be bit-identical.");
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(matrixSummary.Fail.TotalProbability), BitConverter.DoubleToInt64Bits(factorSummary.Fail.TotalProbability),
            "The annualized failure probability must be bit-identical.");
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(matrixSummary.Fail.StandardDeviation), BitConverter.DoubleToInt64Bits(factorSummary.Fail.StandardDeviation),
            "The failure standard deviation must be bit-identical.");
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(matrixSummary.Fail.ConditionalValueAtRisk), BitConverter.DoubleToInt64Bits(factorSummary.Fail.ConditionalValueAtRisk),
            "The conditional value-at-risk must be bit-identical.");
        CollectionAssert.AreEqual(matrixAnalysis.MeanRiskResults!.Curves.Fail.LECConsequences, factorAnalysis.MeanRiskResults!.Curves.Fail.LECConsequences,
            "The failure LEC consequences must be bit-identical.");
        CollectionAssert.AreEqual(matrixAnalysis.MeanRiskResults.Curves.Fail.LECProbabilities, factorAnalysis.MeanRiskResults.Curves.Fail.LECProbabilities,
            "The failure LEC probabilities must be bit-identical.");
        CollectionAssert.AreEqual(matrixAnalysis.MeanRiskResults.Curves.Total.LECProbabilities, factorAnalysis.MeanRiskResults.Curves.Total.LECProbabilities,
            "The total LEC probabilities must be bit-identical.");
    }

    /// <summary>
    /// The exchangeable exact oracle: four units under a common loading λ = 0.7 with constant
    /// failure probabilities, so the annualized failure probability equals the between-mode
    /// union exactly. Three anchors: the test-local one-factor quadrature against the shipped
    /// <c>Probability.UnionSingleFactor</c> (two independent exact evaluations); the engine
    /// against the directly evaluated PCM union at the lazy-enumeration tolerance; and the
    /// engine against the exact union within the measured PCM error (8.4e-4 relative on this
    /// fixture) with headroom — the honest statement that the shipped dependent-union kernel is
    /// the product-of-conditional-marginals approximation, not the exact orthant.
    /// </summary>
    [TestMethod]
    public void Test_ExchangeableUnion_ExactFactorIntegral()
    {
        // Arrange — the exchangeable fixture.
        double lambda = 0.7d;
        var probabilities = new[] { 0.05d, 0.10d, 0.15d, 0.08d };
        int units = probabilities.Length;

        // The test-local exact one-factor integral: Simpson over z ∈ [−8.5, 8.5] at 2·10⁵
        // panels — union(z) = 1 − Π(1 − Φ((b_i − λz)/√(1−λ²))).
        var thresholds = new double[units];
        for (int i = 0; i < units; i++)
        {
            thresholds[i] = Normal.StandardZ(probabilities[i]);
        }
        double complementScale = Math.Sqrt(1d - lambda * lambda);
        Func<double, double> unionAt = z =>
        {
            double survival = 1d;
            for (int i = 0; i < units; i++)
            {
                survival *= 1d - Normal.StandardCDF((thresholds[i] - lambda * z) / complementScale);
            }
            return (1d - survival) * Normal.StandardPDF(z);
        };
        int panels = 200_000;
        double lower = -8.5d, upper = 8.5d;
        double step = (upper - lower) / panels;
        double exactUnion = unionAt(lower) + unionAt(upper);
        for (int i = 1; i < panels; i++)
        {
            exactUnion += unionAt(lower + i * step) * (i % 2 == 1 ? 4d : 2d);
        }
        exactUnion *= step / 3d;

        // Anchor 1 — the shipped single-factor primitive agrees with the independent quadrature.
        double shippedUnion = Probability.UnionSingleFactor(probabilities, lambda * lambda);
        Assert.AreEqual(exactUnion, shippedUnion, 5e-8 * exactUnion,
            "The test-local factor integral and Probability.UnionSingleFactor are independent exact evaluations of the same union.");

        // Anchor 2 — the directly evaluated PCM union (the engine's approximation class).
        var loadings = new double[units];
        for (int i = 0; i < units; i++)
        {
            loadings[i] = lambda;
        }
        double pcmUnion = Probability.UnionPCM(probabilities, InducedMatrix(new[] { loadings }));

        // The engine: constant fragilities make the annualized failure probability the union.
        var component = new SystemComponent { Name = "Reach", HazardFunction = Hazard() };
        for (int i = 0; i < units; i++)
        {
            component.AddFailureMode(new FailureMode(null, null,
                ConstantFragility($"Segment {i + 1} Fragility", probabilities[i]),
                Consequence($"Segment {i + 1} Loss", FailureValues[i % FailureValues.Length])));
        }
        component.AddFailureMode(new FailureMode(null, null, null, Consequence("Non-Failure Loss", NonFailureValues)));
        component.FailureModeMethod = FailureModeMethod.JointFailures;
        component.FailureModeDependency = DependencyType.LatentFactors;
        component.AddLatentFactor(new LatentFactor("Reach Capacity", loadings));

        var analysis = new RiskAnalysis(new[] { component }) { Name = "Exchangeable Union" };
        analysis.Options.Realizations = 100;
        analysis.RunAsync().GetAwaiter().GetResult();
        Assert.IsTrue(analysis.IsEstimated, "The exchangeable analysis must estimate.");
        double engineUnion = analysis.RiskResults![0]!.Fail.TotalProbability;

        // Anchor 3 — the engine sits on the PCM union within the lazy-enumeration tolerance,
        // and on the exact union within the measured PCM error plus headroom.
        Assert.AreEqual(pcmUnion, engineUnion, 2e-4,
            "The engine's dependent union is the PCM evaluation within the lazy exclusive enumeration's documented 1e-4 convergence tolerance.");
        Assert.AreEqual(exactUnion, engineUnion, 2.5e-3 * exactUnion,
            "The engine must track the exact factor-integral union within the measured PCM approximation error (8.4e-4 relative on this fixture) plus headroom.");
    }

    /// <summary>
    /// The multi-factor Monte Carlo oracle: three units under two factors with constant failure
    /// probabilities; 1,000,000 trials drawing, from one MersenneTwister(12345) stream in
    /// documented order (both factor draws, then one idiosyncratic draw per unit), the latent
    /// capacities U_i = Σf λif·z_f + √(1 − Σf λif²)·ε_i with unit i failing when
    /// U_i ≤ Φ⁻¹(p_i). The engine's annualized failure probability must match the simulated
    /// union within 4·SE plus the measured PCM allowance.
    /// </summary>
    [TestMethod]
    public void Test_MultiFactor_MonteCarlo1M()
    {
        // Arrange — the two-factor fixture.
        var loadings = new[] { new[] { 0.8d, 0.6d, 0.5d }, new[] { 0.3d, -0.4d, 0.2d } };
        var probabilities = new[] { 0.10d, 0.06d, 0.14d };
        int units = probabilities.Length;
        var thresholds = new double[units];
        var idiosyncratic = new double[units];
        for (int i = 0; i < units; i++)
        {
            thresholds[i] = Normal.StandardZ(probabilities[i]);
            double squaredSum = loadings[0][i] * loadings[0][i] + loadings[1][i] * loadings[1][i];
            idiosyncratic[i] = Math.Sqrt(1d - squaredSum);
        }

        // The oracle pass.
        var stream = new MersenneTwister(OracleSeed);
        long unionCount = 0;
        for (int n = 0; n < OracleRealizations; n++)
        {
            double z1 = Normal.StandardZ(stream.NextDouble());
            double z2 = Normal.StandardZ(stream.NextDouble());
            bool any = false;
            for (int i = 0; i < units; i++)
            {
                double epsilon = Normal.StandardZ(stream.NextDouble());
                double latent = loadings[0][i] * z1 + loadings[1][i] * z2 + idiosyncratic[i] * epsilon;
                if (latent <= thresholds[i]) any = true;
            }
            if (any) unionCount++;
        }
        double oracleUnion = unionCount / (double)OracleRealizations;
        double oracleSe = Math.Sqrt(oracleUnion * (1d - oracleUnion) / OracleRealizations);

        // The engine.
        var component = new SystemComponent { Name = "Reach", HazardFunction = Hazard() };
        for (int i = 0; i < units; i++)
        {
            component.AddFailureMode(new FailureMode(null, null,
                ConstantFragility($"Segment {i + 1} Fragility", probabilities[i]),
                Consequence($"Segment {i + 1} Loss", FailureValues[i])));
        }
        component.AddFailureMode(new FailureMode(null, null, null, Consequence("Non-Failure Loss", NonFailureValues)));
        component.FailureModeMethod = FailureModeMethod.JointFailures;
        component.FailureModeDependency = DependencyType.LatentFactors;
        component.AddLatentFactor(new LatentFactor("Soil Unit", loadings[0]));
        component.AddLatentFactor(new LatentFactor("Design Era", loadings[1]));

        var analysis = new RiskAnalysis(new[] { component }) { Name = "Multi-Factor Union" };
        analysis.Options.Realizations = 100;
        analysis.RunAsync().GetAwaiter().GetResult();
        Assert.IsTrue(analysis.IsEstimated, "The multi-factor analysis must estimate.");
        double engineUnion = analysis.RiskResults![0]!.Fail.TotalProbability;

        // Assert — 4·SE plus the measured PCM allowance (the same approximation class as the
        // exchangeable test; the allowance is carried at the same 2.5e-3 relative headroom).
        Assert.AreEqual(oracleUnion, engineUnion, K * oracleSe + 2.5e-3 * oracleUnion,
            "The engine union must match the two-factor Monte Carlo oracle within 4·SE plus the PCM allowance.");
    }

    /// <summary>
    /// Competing risks under latent-factor dependence at three units — the dimension at which
    /// the cumulative-incidence pre-processing routes through the seeded Genz lattice. The
    /// 1,000,000-trial oracle mirrors the legacy competing convention with factor-model
    /// capacity draws; the engine must match the failure union and the mean loss streams
    /// within 4·SE plus the competing family's documented 0.3% cumulative-incidence
    /// discretization allowance.
    /// </summary>
    [TestMethod]
    public void Test_Competing_EngineVsMonteCarlo1M()
    {
        // Arrange — one factor over the three Bucket-1 fragilities.
        var loadings = new[] { 0.8d, 0.7d, 0.6d };
        int units = loadings.Length;
        var (hazardProbabilities, hazardStages) = HazardTable();
        var fragilityStages = new double[units][];
        var fragilityProbabilities = new double[units][];
        for (int mode = 0; mode < units; mode++)
        {
            (fragilityStages[mode], fragilityProbabilities[mode]) = FragilityTable(mode);
        }

        // Pre-generate the factor-model capacity draws (one stream, documented order: the
        // factor draw, then one idiosyncratic draw per unit).
        var capacityStream = new MersenneTwister(OracleSeed);
        var capacityUniforms = new double[OracleRealizations, units];
        for (int n = 0; n < OracleRealizations; n++)
        {
            double z = Normal.StandardZ(capacityStream.NextDouble());
            for (int i = 0; i < units; i++)
            {
                double epsilon = Normal.StandardZ(capacityStream.NextDouble());
                double latent = loadings[i] * z + Math.Sqrt(1d - loadings[i] * loadings[i]) * epsilon;
                capacityUniforms[n, i] = Normal.StandardCDF(latent);
            }
        }

        // The oracle pass: the weakest exceeded mode wins and takes its own consequence.
        var hazardStream = new MersenneTwister(OracleSeed);
        long failureCount = 0;
        double failSum = 0d, totalSum = 0d, failSumSq = 0d, totalSumSq = 0d;
        var resistances = new double[units];
        for (int n = 0; n < OracleRealizations; n++)
        {
            double hazard = Interpolate(hazardProbabilities, hazardStages, hazardStream.NextDouble());
            double nonFailureConsequence = Interpolate(ConsequenceStages, NonFailureValues, hazard);

            double minimumResistance = double.PositiveInfinity;
            for (int mode = 0; mode < units; mode++)
            {
                resistances[mode] = Interpolate(fragilityProbabilities[mode], fragilityStages[mode], capacityUniforms[n, mode]);
                minimumResistance = Math.Min(minimumResistance, resistances[mode]);
            }
            int winner = -1;
            for (int mode = 0; mode < units; mode++)
            {
                double failureProbability = Math.Max(0d, Math.Min(1d, Interpolate(fragilityStages[mode], fragilityProbabilities[mode], hazard)));
                if (capacityUniforms[n, mode] <= failureProbability && resistances[mode] == minimumResistance)
                {
                    winner = mode;
                    break;
                }
            }

            if (winner >= 0)
            {
                failureCount++;
                double consequence = Interpolate(ConsequenceStages, FailureValues[winner], hazard);
                failSum += consequence;
                failSumSq += consequence * consequence;
                totalSum += consequence;
                totalSumSq += consequence * consequence;
            }
            else
            {
                totalSum += nonFailureConsequence;
                totalSumSq += nonFailureConsequence * nonFailureConsequence;
            }
        }
        double oracleUnion = failureCount / (double)OracleRealizations;
        double oracleUnionSe = Math.Sqrt(oracleUnion * (1d - oracleUnion) / OracleRealizations);
        double oracleFailMean = failSum / OracleRealizations;
        double oracleFailSe = Math.Sqrt(Math.Max(0d, failSumSq / OracleRealizations - oracleFailMean * oracleFailMean) / OracleRealizations);
        double oracleTotalMean = totalSum / OracleRealizations;
        double oracleTotalSe = Math.Sqrt(Math.Max(0d, totalSumSq / OracleRealizations - oracleTotalMean * oracleTotalMean) / OracleRealizations);

        // The engine.
        var analysis = BuildJointAnalysis(component =>
        {
            component.FailureModeMethod = FailureModeMethod.CompetingFailures;
            component.FailureModeDependency = DependencyType.LatentFactors;
            component.AddLatentFactor(new LatentFactor("Reach Capacity", loadings));
        });
        analysis.RunAsync().GetAwaiter().GetResult();
        Assert.IsTrue(analysis.IsEstimated, "The competing analysis must estimate.");
        var summary = analysis.RiskResults![0]!;

        // Assert — 4·SE plus the competing family's documented 0.3% relative CIF allowance.
        Assert.AreEqual(oracleUnion, summary.Fail.TotalProbability, K * oracleUnionSe + 0.003d * oracleUnion,
            "The competing failure union must match the factor-model oracle.");
        Assert.AreEqual(oracleFailMean, summary.Fail.Mean, K * oracleFailSe + 0.003d * oracleFailMean,
            "The competing failure risk mean must match the factor-model oracle.");
        Assert.AreEqual(oracleTotalMean, summary.Total.Mean, K * oracleTotalSe + 0.003d * oracleTotalMean,
            "The competing total risk mean must match the factor-model oracle.");
    }

    /// <summary>
    /// Reproducibility and metadata inertness at full uncertainty (the mean-only default is
    /// switched off so the ensemble actually samples): an uncertain-hazard latent-factors model
    /// reruns bit-identically at the same seed; renaming the component and its factors is
    /// bit-inert; reordering the factors re-rolls the content-derived component seed (declared
    /// order is hashed content) and moves the sampled ensemble.
    /// </summary>
    [TestMethod]
    public void Test_ReproducibilityAndInertness()
    {
        // Arrange — the Bucket-1 model with an uncertain hazard so seeds matter.
        static RiskAnalysis Build(bool reorderFactors, string suffix)
        {
            var (probabilities, stages) = HazardTable();
            var ordinates = new UncertainOrdinate[stages.Length];
            for (int i = 0; i < stages.Length; i++)
            {
                ordinates[i] = new UncertainOrdinate(1d - probabilities[i], new Normal(stages[i], 2d));
            }
            var hazard = new TabularHazard
            {
                Name = "Stage Frequency" + suffix,
                SpecifiedHazard = "Stage",
                HazardUnit = "ft",
                ProbabilityTransform = Transform.None,
                UncertaintyValue = FunctionUncertainty.Hazard,
                HazardUncertainFunction = new UncertainOrderedPairedData(ordinates,
                    true, SortOrder.Descending, true, SortOrder.Ascending, UnivariateDistributionType.Normal),
            };
            var component = new SystemComponent { Name = "Dam" + suffix, HazardFunction = hazard };
            for (int mode = 0; mode < 3; mode++)
            {
                component.AddFailureMode(new FailureMode(null, null, Fragility(mode), Consequence($"PFM-{mode + 1} Loss", FailureValues[mode])));
            }
            component.AddFailureMode(new FailureMode(null, null, null, Consequence("Non-Failure Loss", NonFailureValues)));
            component.FailureModeMethod = FailureModeMethod.JointFailures;
            component.FailureModeDependency = DependencyType.LatentFactors;
            var soil = new LatentFactor("Soil Unit" + suffix, new[] { 0.8d, 0.6d, 0.5d });
            var era = new LatentFactor("Design Era" + suffix, new[] { 0.3d, -0.4d, 0.2d });
            if (reorderFactors)
            {
                component.AddLatentFactor(era);
                component.AddLatentFactor(soil);
            }
            else
            {
                component.AddLatentFactor(soil);
                component.AddLatentFactor(era);
            }
            var analysis = new RiskAnalysis(new[] { component }) { Name = "Reproducibility" + suffix };
            analysis.Options.Realizations = 100;
            analysis.Options.EstimateMeanRiskOnly = false;
            return analysis;
        }

        var first = Build(reorderFactors: false, string.Empty);
        var second = Build(reorderFactors: false, string.Empty);
        var renamed = Build(reorderFactors: false, " Renamed");
        var reordered = Build(reorderFactors: true, string.Empty);

        // Act
        first.RunAsync().GetAwaiter().GetResult();
        second.RunAsync().GetAwaiter().GetResult();
        renamed.RunAsync().GetAwaiter().GetResult();
        reordered.RunAsync().GetAwaiter().GetResult();

        // Assert — repeat and rename are bit-identical on the published ensemble surfaces; the
        // factor reorder moves them.
        long firstBits = BitConverter.DoubleToInt64Bits(first.MeanRiskResults!.Curves.Total.Mean);
        Assert.AreEqual(firstBits, BitConverter.DoubleToInt64Bits(second.MeanRiskResults!.Curves.Total.Mean),
            "A repeated run at the same seed must be bit-identical.");
        CollectionAssert.AreEqual(first.MeanRiskResults.Curves.Total.LECProbabilities, second.MeanRiskResults.Curves.Total.LECProbabilities,
            "The repeated run's total LEC must be bit-identical.");
        Assert.AreEqual(firstBits, BitConverter.DoubleToInt64Bits(renamed.MeanRiskResults!.Curves.Total.Mean),
            "Component and factor names are metadata — renames must be bit-inert.");
        CollectionAssert.AreEqual(
            first.Components[0].CanonicalHash(), renamed.Components[0].CanonicalHash(),
            "The renamed component's canonical hash must be unchanged.");
        CollectionAssert.AreNotEqual(
            first.Components[0].CanonicalHash(), reordered.Components[0].CanonicalHash(),
            "Reordering factors is a deliberate hash event even though the induced matrix is order-invariant.");
        Assert.AreNotEqual(firstBits, BitConverter.DoubleToInt64Bits(reordered.MeanRiskResults!.Curves.Total.Mean),
            "The factor reorder re-rolls the component seed, so the sampled ensemble must move.");
    }
}
