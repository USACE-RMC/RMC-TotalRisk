using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using Numerics.Sampling;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results.Risk;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.Systems.Components;

namespace RMC.TotalRisk.Verification.Analyses.Risk;

/// <summary>
/// Multi-consequence verification: the declared consequence-type axis
/// computes every type through one engine pass — verified against an independent two-type
/// Monte Carlo oracle, the single-type bit-identity pin, the dedicated-primary quadrature
/// cross-check, ensemble statistical parity, within-run probability-stream identities, and the
/// additive/joint system paths.
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// <para>
/// <b>Scenario A (oracle-grade, deterministic):</b> the mean-parity tables — stage frequency
/// from Normal(100, 20) quantiles, fragility Φ((h − 140)/30), both on a dense z-grid with
/// linear interpolation — carrying TWO consequence types per path: Life Loss linear
/// (60 → 0, 200 → 1000) failure / (200 → 100) non-failure, and Damages linear
/// (60 → 0, 200 → 5,000,000) failure / (200 → 1,500,000) non-failure. The damages ratio
/// deliberately differs from the life-loss ratio so the per-type excess clamps behave
/// differently — the types are genuinely independent axes, not scalings.
/// </para>
/// <para>
/// <b>Oracle:</b> one million hazard draws through <c>MersenneTwister(12345)</c> (the legacy
/// seed), accumulating the five stream summands and the failure probability for BOTH types from
/// the oracle's own interpolation of the shared tables. <b>Tolerances</b> are k·SE with k = 4
/// and SE = σ̂/√N per output (docs/verification.md); the engine side is quadrature at 1e-8, so
/// the oracle's Monte Carlo error dominates. The dedicated-primary cross-check uses a 1e-4
/// relative tolerance bounding the quadrature mass-accounting residual on both sides
/// (measured ≈ 3e-6 on means under the earlier midpoint-trapezoid partition, since replaced
/// by the recorded-mass ledger); the joint-versus-additive consistency check uses 1e-2 relative,
/// bounding the VEGAS mean-only error at the default evaluation budget on this smooth
/// two-dimensional integrand.
/// </para>
/// <para>
/// <b>Scenario B (ensemble-grade, uncertain):</b> the trivial engine fixture — stage frequency
/// (0.999 → 0 ft, 0.5 → 10 ft, 0.001 → 30 ft), a triangular-ordinate uncertain fragility, and
/// linear consequences over (0, 30) ft — run single-type and two-type at 400 LHS realizations.
/// Adding a consequence function legitimately changes the mode's content hash and therefore
/// every seed, so the cross-model ensemble comparison is statistical (4·√(SE₁² + SE₂²) on the
/// ensemble means), while the within-run per-type probability streams must agree to 1e-12
/// relative — the probability structure is computed once and shared by every type.
/// </para>
/// </remarks>
[TestClass]
public class MultiConsequenceVerification
{
    /// <summary>The oracle realization count (the conversion-policy standard).</summary>
    private const int OracleRealizations = 1_000_000;

    /// <summary>The legacy fixed oracle seed.</summary>
    private const int OracleSeed = 12345;

    /// <summary>The tolerance multiplier on the Monte Carlo standard error.</summary>
    private const double K = 4d;

    /// <summary>The shared z-grid step of the tabulated curves.</summary>
    private const double ZStep = 0.25d;

    /// <summary>The shared z-grid half-range of the tabulated curves.</summary>
    private const double ZRange = 8d;

    /// <summary>The ensemble realization count of the uncertain scenario.</summary>
    private const int EnsembleRealizations = 400;

    #region Scenario A — dense deterministic tables

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

    /// <summary>Evaluates a clamped linear consequence from (60 → 0) to (200 → valueAtTwoHundred).</summary>
    private static double LinearConsequence(double hazard, double valueAtTwoHundred)
    {
        if (hazard <= 60d) return 0d;
        if (hazard >= 200d) return valueAtTwoHundred;
        return (hazard - 60d) / 140d * valueAtTwoHundred;
    }

    /// <summary>Builds a clamped linear tabular consequence with the given type labels.</summary>
    private static TabularConsequence Consequence(string name, string type, string unit, double valueAtTwoHundred)
    {
        return new TabularConsequence
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = type,
            ConsequenceUnit = unit,
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(60d, new Deterministic(0d)), new UncertainOrdinate(200d, new Deterministic(valueAtTwoHundred)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds the dense tabular hazard of Scenario A.</summary>
    private static TabularHazard DenseHazard()
    {
        var (hazardProbabilities, hazardStages) = HazardTable();
        var hazardOrdinates = new UncertainOrdinate[hazardStages.Length];
        for (int i = 0; i < hazardStages.Length; i++)
        {
            hazardOrdinates[i] = new UncertainOrdinate(1d - hazardProbabilities[i], new Deterministic(hazardStages[i]));
        }
        return new TabularHazard
        {
            Name = "Stage Frequency",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            ProbabilityTransform = Transform.None,
            NoUncertaintyFunction = new UncertainOrderedPairedData(hazardOrdinates,
                true, SortOrder.Descending, true, SortOrder.Ascending, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds the dense tabular fragility of Scenario A.</summary>
    private static TabularResponse DenseFragility()
    {
        var (fragilityStages, fragilityProbabilities) = FragilityTable();
        var fragilityOrdinates = new UncertainOrdinate[fragilityStages.Length];
        for (int i = 0; i < fragilityStages.Length; i++)
        {
            fragilityOrdinates[i] = new UncertainOrdinate(fragilityStages[i], new Deterministic(fragilityProbabilities[i]));
        }
        return new TabularResponse
        {
            Name = "Breach Fragility",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(fragilityOrdinates,
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds a Scenario A component carrying both consequence types on both paths.</summary>
    private static SystemComponent TwoTypeComponent()
    {
        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = DenseHazard();
        var failure = new FailureMode(null, null, DenseFragility(), Consequence("Failure Loss", "Life Loss", "lives", 1000d));
        failure.ConsequenceFunctions.Add(Consequence("Failure Damages", "Damages", "$", 5_000_000d));
        component.AddFailureMode(failure);
        var nonFailure = new FailureMode(null, null, null, Consequence("Non-Failure Loss", "Life Loss", "lives", 100d));
        nonFailure.ConsequenceFunctions.Add(Consequence("Non-Failure Damages", "Damages", "$", 1_500_000d));
        component.AddFailureMode(nonFailure);
        return component;
    }

    /// <summary>Builds the two-type Scenario A analysis with the declared axis.</summary>
    private static RiskAnalysis TwoTypeAnalysis(params SystemComponent[] components)
    {
        var analysis = new RiskAnalysis(components)
        {
            Name = "Multi-Consequence",
            SpecifiedConsequence = "Life Loss",
            ConsequenceUnit = "lives",
        };
        analysis.AdditionalConsequenceTypes.Add(new ConsequenceTypeDescriptor("Damages", "$"));
        return analysis;
    }

    /// <summary>Builds a single-type Scenario A analysis carrying only the given type's curves.</summary>
    private static RiskAnalysis SingleTypeAnalysis(string type, string unit, double failureAtTwoHundred, double nonFailureAtTwoHundred)
    {
        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = DenseHazard();
        component.AddFailureMode(new FailureMode(null, null, DenseFragility(), Consequence("Failure", type, unit, failureAtTwoHundred)));
        component.AddFailureMode(new FailureMode(null, null, null, Consequence("Non-Failure", type, unit, nonFailureAtTwoHundred)));
        return new RiskAnalysis(new[] { component })
        {
            Name = "Single Type",
            SpecifiedConsequence = type,
            ConsequenceUnit = unit,
        };
    }

    /// <summary>
    /// The two-type oracle: per-draw accumulation of the five stream summands and the failure
    /// probability for both consequence types, with in-run standard errors. Outputs 0–5 are the
    /// Life Loss set (fail, non-fail, total, excess, background, failure probability); outputs
    /// 6–11 are the Damages set.
    /// </summary>
    /// <returns>Per output: the mean and its Monte Carlo standard error.</returns>
    private static (double Mean, double Se)[] RunOracle()
    {
        var prng = new MersenneTwister(OracleSeed);
        var (hazardProbabilities, hazardStages) = HazardTable();
        var (fragilityStages, fragilityProbabilities) = FragilityTable();

        var sums = new double[12];
        var sumsOfSquares = new double[12];
        var draw = new double[12];
        for (int i = 0; i < OracleRealizations; i++)
        {
            double hazard = Interpolate(hazardProbabilities, hazardStages, prng.NextDouble());
            double pF = Math.Max(0d, Math.Min(1d, Interpolate(fragilityStages, fragilityProbabilities, hazard)));
            double livesF = LinearConsequence(hazard, 1000d);
            double livesNF = LinearConsequence(hazard, 100d);
            double damagesF = LinearConsequence(hazard, 5_000_000d);
            double damagesNF = LinearConsequence(hazard, 1_500_000d);

            draw[0] = pF * livesF;
            draw[1] = (1d - pF) * livesNF;
            draw[2] = draw[0] + draw[1];
            draw[3] = pF * Math.Max(0d, livesF - livesNF);
            draw[4] = livesNF;
            draw[5] = pF;
            draw[6] = pF * damagesF;
            draw[7] = (1d - pF) * damagesNF;
            draw[8] = draw[6] + draw[7];
            draw[9] = pF * Math.Max(0d, damagesF - damagesNF);
            draw[10] = damagesNF;
            draw[11] = pF;
            for (int k = 0; k < 12; k++)
            {
                sums[k] += draw[k];
                sumsOfSquares[k] += draw[k] * draw[k];
            }
        }

        var results = new (double Mean, double Se)[12];
        for (int k = 0; k < 12; k++)
        {
            double mean = sums[k] / OracleRealizations;
            double variance = Math.Max(0d, sumsOfSquares[k] / OracleRealizations - mean * mean);
            results[k] = (mean, Math.Sqrt(variance / OracleRealizations));
        }
        return results;
    }

    #endregion

    #region Scenario B — trivial uncertain fixture

    /// <summary>Builds the trivial stage-frequency hazard of Scenario B.</summary>
    private static TabularHazard TrivialHazard()
    {
        return new TabularHazard
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
    }

    /// <summary>Builds the uncertain triangular-ordinate fragility of Scenario B.</summary>
    private static TabularResponse UncertainFragility()
    {
        return new TabularResponse
        {
            Name = "Fragility",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(10d, new Triangular(0d, 0.05d, 0.1d)), new UncertainOrdinate(20d, new Triangular(0.7d, 0.9d, 1d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Triangular),
        };
    }

    /// <summary>Builds a clamped linear Scenario B consequence over (0, 30) ft.</summary>
    private static TabularConsequence TrivialConsequence(string name, string type, string unit, double valueAtThirty)
    {
        return new TabularConsequence
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = type,
            ConsequenceUnit = unit,
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(0d, new Deterministic(0d)), new UncertainOrdinate(30d, new Deterministic(valueAtThirty)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds a Scenario B component, single- or two-type.</summary>
    private static SystemComponent TrivialComponent(bool twoTypes)
    {
        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = TrivialHazard();
        var failure = new FailureMode(null, null, UncertainFragility(), TrivialConsequence("Failure Loss", "Life Loss", "lives", 300d));
        if (twoTypes) failure.ConsequenceFunctions.Add(TrivialConsequence("Failure Damages", "Damages", "$", 900_000d));
        component.AddFailureMode(failure);
        var nonFailure = new FailureMode(null, null, null, TrivialConsequence("Non-Failure Loss", "Life Loss", "lives", 60d));
        if (twoTypes) nonFailure.ConsequenceFunctions.Add(TrivialConsequence("Non-Failure Damages", "Damages", "$", 250_000d));
        component.AddFailureMode(nonFailure);
        return component;
    }

    /// <summary>Builds a Scenario B full-uncertainty analysis.</summary>
    private static RiskAnalysis TrivialAnalysis(bool twoTypes)
    {
        var analysis = new RiskAnalysis(new[] { TrivialComponent(twoTypes) })
        {
            SpecifiedConsequence = "Life Loss",
            ConsequenceUnit = "lives",
        };
        if (twoTypes) analysis.AdditionalConsequenceTypes.Add(new ConsequenceTypeDescriptor("Damages", "$"));
        analysis.Options.EstimateMeanRiskOnly = false;
        analysis.Options.Realizations = EnsembleRealizations;
        return analysis;
    }

    /// <summary>The ensemble mean and its Monte Carlo standard error of one summary output.</summary>
    /// <param name="ensemble">The run's summary ensemble.</param>
    /// <param name="select">Selects the output from a realization summary.</param>
    /// <returns>The ensemble mean and standard error.</returns>
    private static (double Mean, double Se) EnsembleStatistic(EnsembleResults ensemble,
        Func<SystemRiskResults, double> select)
    {
        double sum = 0d;
        double sumOfSquares = 0d;
        int count = ensemble.Count;
        for (int i = 0; i < count; i++)
        {
            double value = select(ensemble[i]!);
            sum += value;
            sumOfSquares += value * value;
        }
        double mean = sum / count;
        double variance = Math.Max(0d, sumOfSquares / count - mean * mean);
        return (mean, Math.Sqrt(variance / count));
    }

    #endregion

    /// <summary>
    /// The two-type oracle gate: the engine's mean-pass five stream means and failure
    /// probability match the independent oracle within 4·SE per output, on BOTH consequence
    /// types of one engine pass.
    /// </summary>
    [TestMethod]
    public void Test_MeanPass_TwoTypes_VsOracle()
    {
        // Arrange / Act
        var oracle = RunOracle();
        var analysis = TwoTypeAnalysis(TwoTypeComponent());
        analysis.RunAsync().GetAwaiter().GetResult();
        Assert.IsTrue(analysis.IsEstimated);
        var summary = analysis.RiskResults![0]!;
        var damages = summary.AdditionalConsequences[0];

        // Assert — Life Loss (primary).
        Assert.AreEqual(oracle[0].Mean, summary.Fail.Mean, K * oracle[0].Se, "Life-loss failure risk mean.");
        Assert.AreEqual(oracle[1].Mean, summary.NonFail.Mean, K * oracle[1].Se, "Life-loss non-failure risk mean.");
        Assert.AreEqual(oracle[2].Mean, summary.Total.Mean, K * oracle[2].Se, "Life-loss total risk mean.");
        Assert.AreEqual(oracle[3].Mean, summary.Excess.Mean, K * oracle[3].Se, "Life-loss incremental risk mean.");
        Assert.AreEqual(oracle[4].Mean, summary.Background.Mean, K * oracle[4].Se, "Life-loss background risk mean.");
        Assert.AreEqual(oracle[5].Mean, summary.Fail.TotalProbability, K * oracle[5].Se, "Annualized failure probability.");

        // Assert — Damages (secondary, the same engine pass).
        Assert.AreEqual(oracle[6].Mean, damages.Fail.Mean, K * oracle[6].Se, "Damages failure risk mean.");
        Assert.AreEqual(oracle[7].Mean, damages.NonFail.Mean, K * oracle[7].Se, "Damages non-failure risk mean.");
        Assert.AreEqual(oracle[8].Mean, damages.Total.Mean, K * oracle[8].Se, "Damages total risk mean.");
        Assert.AreEqual(oracle[9].Mean, damages.Excess.Mean, K * oracle[9].Se, "Damages incremental risk mean.");
        Assert.AreEqual(oracle[10].Mean, damages.Background.Mean, K * oracle[10].Se, "Damages background risk mean.");
        Assert.AreEqual(oracle[11].Mean, damages.Fail.TotalProbability, K * oracle[11].Se, "Damages-axis failure probability.");
    }

    /// <summary>
    /// The single-type bit-identity pin: the two-type mean pass reproduces the single-type
    /// analysis's primary results bit-for-bit — the mean pass is seed-free and refinement is
    /// primary-driven, so declaring a second type can never move the first.
    /// </summary>
    [TestMethod]
    public void Test_MeanPass_PrimaryUnperturbed_BitIdentical()
    {
        // Arrange
        var singleType = SingleTypeAnalysis("Life Loss", "lives", 1000d, 100d);
        var twoType = TwoTypeAnalysis(TwoTypeComponent());

        // Act
        singleType.RunAsync().GetAwaiter().GetResult();
        twoType.RunAsync().GetAwaiter().GetResult();

        // Assert — bit-identical primary summaries and curves.
        var single = singleType.RiskResults![0]!;
        var primary = twoType.RiskResults![0]!;
        Assert.AreEqual(single.Total.Mean, primary.Total.Mean, 0d);
        Assert.AreEqual(single.Fail.Mean, primary.Fail.Mean, 0d);
        Assert.AreEqual(single.Excess.Mean, primary.Excess.Mean, 0d);
        Assert.AreEqual(single.Fail.TotalProbability, primary.Fail.TotalProbability, 0d);
        CollectionAssert.AreEqual(
            singleType.MeanRiskResults!.Curves.Total.LECConsequences,
            twoType.MeanRiskResults!.Curves.Total.LECConsequences);
        CollectionAssert.AreEqual(
            singleType.MeanRiskResults!.Curves.Total.LECProbabilities,
            twoType.MeanRiskResults!.Curves.Total.LECProbabilities);
    }

    /// <summary>
    /// The dedicated-primary cross-check: the two-type run's secondary axis matches a dedicated
    /// damages-primary analysis within the quadrature/mass-fallback tolerance — the secondary
    /// rides the primary-driven refinement nodes, so the parity is numerical, not bit-exact.
    /// </summary>
    [TestMethod]
    public void Test_SecondaryVsDedicatedPrimary_QuadratureParity()
    {
        // Arrange
        var damagesPrimary = SingleTypeAnalysis("Damages", "$", 5_000_000d, 1_500_000d);
        var twoType = TwoTypeAnalysis(TwoTypeComponent());

        // Act
        damagesPrimary.RunAsync().GetAwaiter().GetResult();
        twoType.RunAsync().GetAwaiter().GetResult();

        // Assert — 1e-4 relative bounds the mass-accounting residual on both sides.
        var dedicated = damagesPrimary.RiskResults![0]!;
        var secondary = twoType.RiskResults![0]!.AdditionalConsequences[0];
        Assert.AreEqual(dedicated.Total.Mean, secondary.Total.Mean, 1e-4 * dedicated.Total.Mean, "Damages total mean.");
        Assert.AreEqual(dedicated.Fail.Mean, secondary.Fail.Mean, 1e-4 * dedicated.Fail.Mean, "Damages failure mean.");
        Assert.AreEqual(dedicated.Excess.Mean, secondary.Excess.Mean, 1e-4 * dedicated.Excess.Mean, "Damages excess mean.");
    }

    /// <summary>
    /// The ensemble statistical parity and within-run identity gates: single-type and two-type
    /// full-uncertainty ensembles agree statistically on the primary axis (the seeds
    /// legitimately differ — a consequence function is content), and within the two-type run
    /// every realization's per-type probability streams are identical to 1e-12 relative.
    /// </summary>
    [TestMethod]
    public void Test_Ensemble_CrossModelParity_And_WithinRunIdentity()
    {
        // Arrange / Act
        var singleType = TrivialAnalysis(twoTypes: false);
        var twoType = TrivialAnalysis(twoTypes: true);
        singleType.RunAsync().GetAwaiter().GetResult();
        twoType.RunAsync().GetAwaiter().GetResult();

        // Assert — cross-model statistical parity on the primary ensemble means (independent
        // samples of the same population; 4·√(SE₁² + SE₂²)).
        var singleAfp = EnsembleStatistic(singleType.RiskResults!, s => s.Fail.TotalProbability);
        var twoAfp = EnsembleStatistic(twoType.RiskResults!, s => s.Fail.TotalProbability);
        double afpTolerance = K * Math.Sqrt(singleAfp.Se * singleAfp.Se + twoAfp.Se * twoAfp.Se);
        Assert.AreEqual(singleAfp.Mean, twoAfp.Mean, afpTolerance, "Ensemble-mean annualized failure probability.");

        var singleTotal = EnsembleStatistic(singleType.RiskResults!, s => s.Total.Mean);
        var twoTotal = EnsembleStatistic(twoType.RiskResults!, s => s.Total.Mean);
        double totalTolerance = K * Math.Sqrt(singleTotal.Se * singleTotal.Se + twoTotal.Se * twoTotal.Se);
        Assert.AreEqual(singleTotal.Mean, twoTotal.Mean, totalTolerance, "Ensemble-mean total risk.");

        // Assert — within-run per-type identities on every realization.
        for (int i = 0; i < twoType.RiskResults!.Count; i++)
        {
            var summary = twoType.RiskResults[i]!;
            var damages = summary.AdditionalConsequences[0];
            Assert.AreEqual(summary.Fail.TotalProbability, damages.Fail.TotalProbability,
                1e-12 * Math.Max(1e-300, summary.Fail.TotalProbability),
                $"Realization {i}: per-type failure probabilities diverged.");
        }
    }

    /// <summary>
    /// The additive system gate: two independent two-type components convolve each type onto
    /// its own system curve set with the convolved mean equal to the sum of the component means
    /// (the exact additive identity), sharing the type-independent failure union.
    /// </summary>
    [TestMethod]
    public void Test_AdditiveSystem_SecondaryConvolutionIdentity()
    {
        // Arrange
        var analysis = TwoTypeAnalysis(TwoTypeComponent(), TwoTypeComponent());

        // Act
        analysis.RunAsync().GetAwaiter().GetResult();

        // Assert — the convolved secondary system mean equals the sum of the component
        // secondary means (1e-6 relative — the additive mean-parity identity).
        var system = analysis.MeanRiskResults!;
        double componentSum = system.Components[0].AdditionalCurves[0].Total.Mean
            + system.Components[1].AdditionalCurves[0].Total.Mean;
        Assert.AreEqual(componentSum, system.AdditionalCurves[0].Total.Mean, 1e-6 * componentSum,
            "Convolved secondary system mean must equal the sum of component means.");
        Assert.AreEqual(system.Curves.Fail.TotalProbability, system.AdditionalCurves[0].Fail.TotalProbability, 0d,
            "The failure union is shared verbatim across types.");
    }

    /// <summary>
    /// The joint system gate: the joint method's secondary axis agrees with the additive
    /// method's on strictly independent components (1e-2 relative — the VEGAS mean-only error
    /// at the default budget), and the per-type failure masses agree within the run.
    /// </summary>
    [TestMethod]
    public void Test_JointSystem_SecondaryMatchesAdditive()
    {
        // Arrange
        var additive = TwoTypeAnalysis(TwoTypeComponent(), TwoTypeComponent());
        var joint = TwoTypeAnalysis(TwoTypeComponent(), TwoTypeComponent());
        joint.Options.SystemRiskMethod = SystemRiskType.JointRiskMethod;

        // Act
        additive.RunAsync().GetAwaiter().GetResult();
        joint.RunAsync().GetAwaiter().GetResult();

        // Assert
        double additiveMean = additive.MeanRiskResults!.AdditionalCurves[0].Total.Mean;
        double jointMean = joint.MeanRiskResults!.AdditionalCurves[0].Total.Mean;
        Assert.AreEqual(additiveMean, jointMean, 1e-2 * additiveMean,
            "Joint and additive secondary system means must agree on independent components.");

        double primaryFailMass = joint.MeanRiskResults!.Curves.Fail.TotalProbability;
        double secondaryFailMass = joint.MeanRiskResults!.AdditionalCurves[0].Fail.TotalProbability;
        Assert.AreEqual(primaryFailMass, secondaryFailMass, 1e-12 * Math.Max(1e-300, primaryFailMass),
            "Per-type failure masses must agree on the joint path.");
    }
}
