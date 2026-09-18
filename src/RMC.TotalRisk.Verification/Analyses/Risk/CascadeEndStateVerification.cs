using System;
using System.Linq;
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
using RMC.TotalRisk.Systems.Components.Graph;

namespace RMC.TotalRisk.Verification.Analyses.Risk;

/// <summary>
/// Cascading response end states — the cascade verification family
/// (docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §7.9): hand-rolled
/// Monte Carlo oracles for the two-stage cascade with a partial-damage state, the across-group
/// combination matrix (joint, mutually exclusive, competing), the saturated-stage single-stage
/// equivalence, reliability mode, system aggregation smokes, and the reproducibility pins.
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// <para>
/// <b>Scenario:</b> the Bucket-1 style model — LnNormal(85, 20) hazard tabulated on a ±8 z-grid
/// at step 0.1; Normal-CDF fragilities tabulated on ±8σ z-grids at step 0.05σ (initiation
/// Φ((h−140)/30), progression Φ((h−150)/20), standalone Φ((h−160)/10)); the legacy five-knot
/// consequence curves (full breach 0/10/100/1000/1500, partial damage 0/2/20/200/300, standalone
/// 0/3/30/300/450, background 0/1/10/100/150). Engine and oracle interpolate the SAME tables.
/// The cascade wires the graph by port: the initiation response's Fail port feeds the
/// progression response, whose Fail port carries the full-breach terminal and whose Non-Fail
/// port carries the partial-damage terminal (a claimed non-failure state, §7.9.2/§7.9.5).
/// </para>
/// <para>
/// <b>Oracle mechanics:</b> hazard uniforms from <c>MersenneTwister(12345)</c>; branch/selection
/// uniforms from <c>MersenneTwister(45678)</c> with a FIXED draw count per realization so the
/// streams never depend on outcomes. The natural oracles (single cascade; joint independent)
/// simulate the branch outcomes directly — the engine's conditional claimed-state formula
/// C·w/(1 − P_g) is exact under independence, so natural simulation IS the reference. The
/// mutually-exclusive and competing oracles implement the engine's documented across-unit
/// conventions (unit-mass selection / capacity weak-link, complement split by the conditional
/// share q) — they verify the convention is implemented faithfully, which is all a convention
/// admits. N = 1,000,000; no published constants exist for cascades, so all asserts are
/// engine-versus-oracle.
/// </para>
/// <para>
/// <b>Tolerances:</b> k·SE with k = 4 (mean SE = σ̂/√N; σ SE by the delta method; probability
/// SEs binomial; value-at-risk density-scaled with the 0.1% relative floor; conditional mean by
/// the ratio-estimator first-order SE). The competing scenario's total-risk mean additionally
/// carries a 1% relative floor for the engine's 200-bin cumulative-incidence discretization
/// (the same allowance the combination-consistency family documents).
/// </para>
/// </remarks>
[TestClass]
public class CascadeEndStateVerification
{
    /// <summary>The oracle realization count (the conversion policy's 1M).</summary>
    private const int OracleRealizations = 1_000_000;

    /// <summary>The legacy hazard-stream seed.</summary>
    private const int HazardSeed = 12345;

    /// <summary>The legacy branch/selection-stream seed.</summary>
    private const int BranchSeed = 45678;

    /// <summary>The tolerance multiplier on the Monte Carlo standard error.</summary>
    private const double K = 4d;

    /// <summary>The exceedance level for the value-at-risk asserts.</summary>
    private const double Alpha = 0.01d;

    /// <summary>The consequence threshold behind the assurance-measure assert.</summary>
    private const double Threshold = 100d;

    /// <summary>The hazard z-grid step (±8 range).</summary>
    private const double HazardZStep = 0.1d;

    /// <summary>The fragility z-grid step (±8σ range).</summary>
    private const double FragilityZStep = 0.05d;

    /// <summary>The z-grid half-range of every tabulated curve.</summary>
    private const double ZRange = 8d;

    /// <summary>The initiation fragility mean and standard deviation.</summary>
    private static readonly double[] Initiation = { 140d, 30d };

    /// <summary>The progression fragility mean and standard deviation.</summary>
    private static readonly double[] Progression = { 150d, 20d };

    /// <summary>The standalone fragility mean and standard deviation.</summary>
    private static readonly double[] Standalone = { 160d, 10d };

    /// <summary>The legacy consequence-curve stages.</summary>
    private static readonly double[] ConsequenceStages = { 60d, 100d, 140d, 200d, 250d };

    /// <summary>The full-breach consequence ordinates.</summary>
    private static readonly double[] FullValues = { 0d, 10d, 100d, 1000d, 1500d };

    /// <summary>The partial-damage consequence ordinates.</summary>
    private static readonly double[] PartialValues = { 0d, 2d, 20d, 200d, 300d };

    /// <summary>The standalone-mode consequence ordinates.</summary>
    private static readonly double[] StandaloneValues = { 0d, 3d, 30d, 300d, 450d };

    /// <summary>The background (non-failure) consequence ordinates.</summary>
    private static readonly double[] BackgroundValues = { 0d, 1d, 10d, 100d, 150d };

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
    /// <param name="parameters">The fragility mean and standard deviation.</param>
    private static (double[] Stages, double[] Probabilities) FragilityTable(double[] parameters)
    {
        int count = (int)Math.Round(2d * ZRange / FragilityZStep) + 1;
        var stages = new double[count];
        var probabilities = new double[count];
        for (int i = 0; i < count; i++)
        {
            double z = -ZRange + i * FragilityZStep;
            stages[i] = parameters[0] + parameters[1] * z;
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

    /// <summary>Builds one tabular fragility over its z-grid table.</summary>
    /// <param name="name">The function name.</param>
    /// <param name="parameters">The fragility mean and standard deviation.</param>
    private static TabularResponse Fragility(string name, double[] parameters)
    {
        var (stages, probabilities) = FragilityTable(parameters);
        var ordinates = new UncertainOrdinate[stages.Length];
        for (int i = 0; i < stages.Length; i++)
        {
            ordinates[i] = new UncertainOrdinate(stages[i], new Deterministic(probabilities[i]));
        }
        return new TabularResponse
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(ordinates,
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds a saturated fragility (P[F|h] ≡ 1) for the single-stage equivalence check.</summary>
    private static TabularResponse SaturatedFragility()
    {
        return new TabularResponse
        {
            Name = "Certain Progression",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(0d, new Deterministic(1d)), new UncertainOrdinate(1d, new Deterministic(1d)) },
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

    /// <summary>Builds the shared tabulated hazard function.</summary>
    private static TabularHazard Hazard()
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

    /// <summary>
    /// Builds the cascade component by port-level graph wiring — the point of the family: the
    /// initiation response's Fail port feeds the progression response, the progression's Fail
    /// port carries the full-breach terminal, its Non-Fail port the partial-damage terminal,
    /// and a response-free background path completes the component.
    /// </summary>
    /// <param name="includePartial">True to wire the partial-damage (claimed) terminal.</param>
    /// <param name="includeStandalone">True to add the standalone mode as a second combination unit.</param>
    private static SystemComponent CascadeComponent(bool includePartial, bool includeStandalone)
    {
        var component = new SystemComponent { Name = "Cascade Dam" };
        var hazard = new HazardElement("Hazard") { Function = Hazard() };
        var initiation = new ResponseElement("Initiation")
        {
            Function = Fragility("Initiation Fragility", Initiation),
            Input = new RiskConnection(hazard),
        };
        var progression = new ResponseElement("Progression")
        {
            Function = Fragility("Progression Fragility", Progression),
            Input = new RiskConnection(initiation),
        };
        var full = new ConsequenceElement("Full Breach") { Input = new RiskConnection(progression) };
        full.Functions.Add(Consequence("Full Breach Loss", FullValues));
        component.Graph.AddElement(hazard);
        component.Graph.AddElement(initiation);
        component.Graph.AddElement(progression);
        component.Graph.AddElement(full);

        if (includePartial)
        {
            var partial = new ConsequenceElement("Partial Damage") { Input = new RiskConnection(progression, 1) };
            partial.Functions.Add(Consequence("Partial Loss", PartialValues));
            component.Graph.AddElement(partial);
        }
        if (includeStandalone)
        {
            var standaloneResponse = new ResponseElement("Standalone")
            {
                Function = Fragility("Standalone Fragility", Standalone),
                Input = new RiskConnection(hazard),
            };
            var standaloneTerminal = new ConsequenceElement("Standalone Loss") { Input = new RiskConnection(standaloneResponse) };
            standaloneTerminal.Functions.Add(Consequence("Standalone Damages", StandaloneValues));
            component.Graph.AddElement(standaloneResponse);
            component.Graph.AddElement(standaloneTerminal);
        }

        var background = new ConsequenceElement("Background Damage") { Input = new RiskConnection(hazard) };
        background.Functions.Add(Consequence("Background Loss", BackgroundValues));
        component.Graph.AddElement(background);
        return component;
    }

    /// <summary>Builds the engine analysis for one cascade scenario.</summary>
    /// <param name="method">The failure-mode combination method.</param>
    /// <param name="includePartial">True to wire the partial-damage terminal.</param>
    /// <param name="includeStandalone">True to add the standalone mode.</param>
    private static RiskAnalysis BuildAnalysis(FailureModeMethod method, bool includePartial, bool includeStandalone)
    {
        var component = CascadeComponent(includePartial, includeStandalone);
        component.FailureModeMethod = method;
        component.JointConsequences = JointConsequenceType.Maximum;
        var analysis = new RiskAnalysis(new[] { component }) { Name = $"Cascade {method}" };
        analysis.Options.ConsequenceThreshold = Threshold;
        analysis.Options.Alpha = Alpha;
        // Probe the output LEC at its maximum resolution so the thinning interpolation stays an
        // order below the binomial 4·SE (the Bucket-1 families' documented rationale).
        analysis.Options.LECOutputLength = 1000;
        return analysis;
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

    /// <summary>One cascade oracle's stream outputs.</summary>
    private sealed class OracleResult
    {
        /// <summary>The failure probability (final-polarity states only — the union of failure events).</summary>
        public double FailureProbability;

        /// <summary>The binomial standard error of the failure probability.</summary>
        public double FailureProbabilitySe;

        /// <summary>The number of failing realizations.</summary>
        public long FailureCount;

        /// <summary>The background stream.</summary>
        public Moments Background;

        /// <summary>The non-failure stream.</summary>
        public Moments NonFailure;

        /// <summary>The failure risk stream (zero on non-failing draws).</summary>
        public Moments Fail;

        /// <summary>The total risk stream.</summary>
        public Moments Total;

        /// <summary>The incremental (excess) risk stream.</summary>
        public Moments Excess;

        /// <summary>The sum of squared failure consequences over failing draws (the ratio-estimator SE input).</summary>
        public double ConditionalSumOfSquares;

        /// <summary>The sorted unconditional failure losses (zeros on non-failing draws).</summary>
        public double[] SortedFailureLosses = Array.Empty<double>();

        /// <summary>Sorts the loss array and derives the failure probability and its SE.</summary>
        public void Finish()
        {
            Array.Sort(SortedFailureLosses);
            FailureProbability = FailureCount / (double)OracleRealizations;
            FailureProbabilitySe = Math.Sqrt(FailureProbability * (1d - FailureProbability) / OracleRealizations);
        }
    }

    /// <summary>The tabulated model shared by every oracle.</summary>
    private sealed class OracleTables
    {
        /// <summary>The hazard non-exceedance probabilities (ascending).</summary>
        public double[] HazardProbabilities = Array.Empty<double>();

        /// <summary>The hazard stages parallel to the probabilities.</summary>
        public double[] HazardStages = Array.Empty<double>();

        /// <summary>The initiation fragility stages.</summary>
        public double[] InitiationStages = Array.Empty<double>();

        /// <summary>The initiation fragility probabilities.</summary>
        public double[] InitiationProbabilities = Array.Empty<double>();

        /// <summary>The progression fragility stages.</summary>
        public double[] ProgressionStages = Array.Empty<double>();

        /// <summary>The progression fragility probabilities.</summary>
        public double[] ProgressionProbabilities = Array.Empty<double>();

        /// <summary>The standalone fragility stages.</summary>
        public double[] StandaloneStages = Array.Empty<double>();

        /// <summary>The standalone fragility probabilities.</summary>
        public double[] StandaloneProbabilities = Array.Empty<double>();

        /// <summary>Builds the shared tables.</summary>
        public static OracleTables Build()
        {
            var tables = new OracleTables();
            (tables.HazardProbabilities, tables.HazardStages) = HazardTable();
            (tables.InitiationStages, tables.InitiationProbabilities) = FragilityTable(Initiation);
            (tables.ProgressionStages, tables.ProgressionProbabilities) = FragilityTable(Progression);
            (tables.StandaloneStages, tables.StandaloneProbabilities) = FragilityTable(Standalone);
            return tables;
        }

        /// <summary>The initiation fragility at a stage, clamped to [0, 1].</summary>
        /// <param name="stage">The hazard stage.</param>
        public double P1(double stage) => Math.Max(0d, Math.Min(1d, Interpolate(InitiationStages, InitiationProbabilities, stage)));

        /// <summary>The progression fragility at a stage, clamped to [0, 1].</summary>
        /// <param name="stage">The hazard stage.</param>
        public double P2(double stage) => Math.Max(0d, Math.Min(1d, Interpolate(ProgressionStages, ProgressionProbabilities, stage)));

        /// <summary>The standalone fragility at a stage, clamped to [0, 1].</summary>
        /// <param name="stage">The hazard stage.</param>
        public double P3(double stage) => Math.Max(0d, Math.Min(1d, Interpolate(StandaloneStages, StandaloneProbabilities, stage)));
    }

    /// <summary>
    /// The natural single-cascade oracle: per realization one hazard uniform (12345), then
    /// three branch uniforms (45678 — initiation, progression, and the background-counterfactual
    /// selector, always all three). Full breach (both fail) is THE failure state (final
    /// polarity); initiation-without-progression is the partial-damage claimed state riding the
    /// non-failure world; the excess on full-breach draws pairs against the partial sibling
    /// (§7.9.4); the Background stream is the complement-conditional mixture (§7.9.5 — the
    /// engine's no-failure world carries the claimed state at its conditional share, not the
    /// raw background). Natural simulation is exact for a single combination unit.
    /// </summary>
    private static OracleResult RunSingleCascadeOracle()
    {
        var tables = OracleTables.Build();
        var hazardStream = new MersenneTwister(HazardSeed);
        var branchStream = new MersenneTwister(BranchSeed);
        var result = new OracleResult { SortedFailureLosses = new double[OracleRealizations] };

        for (int i = 0; i < OracleRealizations; i++)
        {
            double stage = Interpolate(tables.HazardProbabilities, tables.HazardStages, hazardStream.NextDouble());
            double b1 = branchStream.NextDouble();
            double b2 = branchStream.NextDouble();
            double counterfactual = branchStream.NextDouble();

            double p1 = tables.P1(stage);
            double p2 = tables.P2(stage);
            double backgroundConsequence = Interpolate(ConsequenceStages, BackgroundValues, stage);
            double partialConsequence = Interpolate(ConsequenceStages, PartialValues, stage);
            bool initiated = b1 <= p1;
            bool progressed = initiated && b2 <= p2;

            double breachMass = p1 * p2;
            double q = breachMass < 1d ? p1 * (1d - p2) / (1d - breachMass) : 0d;
            result.Background.Add(counterfactual <= q ? partialConsequence : backgroundConsequence);
            if (progressed)
            {
                result.FailureCount++;
                double consequence = Interpolate(ConsequenceStages, FullValues, stage);
                result.NonFailure.Add(0d);
                result.Fail.Add(consequence);
                result.Total.Add(consequence);
                result.Excess.Add(Math.Max(0d, consequence - partialConsequence));
                result.ConditionalSumOfSquares += consequence * consequence;
                result.SortedFailureLosses[i] = consequence;
            }
            else
            {
                double nonFailureConsequence = initiated ? partialConsequence : backgroundConsequence;
                result.NonFailure.Add(nonFailureConsequence);
                result.Fail.Add(0d);
                result.Total.Add(nonFailureConsequence);
                result.Excess.Add(0d);
                result.SortedFailureLosses[i] = 0d;
            }
        }

        result.Finish();
        return result;
    }

    /// <summary>
    /// The natural joint-independent oracle over the cascade unit and the standalone unit: per
    /// realization one hazard uniform, then four branch uniforms (initiation, progression,
    /// standalone, and the excess counterfactual selector — always all four). The failure event
    /// is full breach and/or standalone failure (Maximum consequence rule); the no-failure world
    /// splits naturally into partial damage and background; the excess counterfactual draws from
    /// the complement mixture q·partial / (1 − q)·background with q = w_partial / (1 − P_A) —
    /// the engine's pair baseline, exact under independence.
    /// </summary>
    private static OracleResult RunJointOracle()
    {
        var tables = OracleTables.Build();
        var hazardStream = new MersenneTwister(HazardSeed);
        var branchStream = new MersenneTwister(BranchSeed);
        var result = new OracleResult { SortedFailureLosses = new double[OracleRealizations] };

        for (int i = 0; i < OracleRealizations; i++)
        {
            double stage = Interpolate(tables.HazardProbabilities, tables.HazardStages, hazardStream.NextDouble());
            double b1 = branchStream.NextDouble();
            double b2 = branchStream.NextDouble();
            double b3 = branchStream.NextDouble();
            double counterfactual = branchStream.NextDouble();

            double p1 = tables.P1(stage);
            double p2 = tables.P2(stage);
            double backgroundConsequence = Interpolate(ConsequenceStages, BackgroundValues, stage);
            double partialConsequence = Interpolate(ConsequenceStages, PartialValues, stage);
            bool initiated = b1 <= p1;
            bool fullBreach = initiated && b2 <= p2;
            bool standaloneFails = b3 <= tables.P3(stage);

            // The complement-mixture counterfactual (the engine's excess pair baseline).
            double breachMass = p1 * p2;
            double q = breachMass < 1d ? p1 * (1d - p2) / (1d - breachMass) : 0d;
            double counterfactualConsequence = counterfactual <= q ? partialConsequence : backgroundConsequence;

            result.Background.Add(counterfactualConsequence);
            if (fullBreach || standaloneFails)
            {
                result.FailureCount++;
                double combined = 0d;
                if (fullBreach) combined = Interpolate(ConsequenceStages, FullValues, stage);
                if (standaloneFails) combined = Math.Max(combined, Interpolate(ConsequenceStages, StandaloneValues, stage));
                result.NonFailure.Add(0d);
                result.Fail.Add(combined);
                result.Total.Add(combined);
                result.Excess.Add(Math.Max(0d, combined - counterfactualConsequence));
                result.ConditionalSumOfSquares += combined * combined;
                result.SortedFailureLosses[i] = combined;
            }
            else
            {
                double nonFailureConsequence = initiated ? partialConsequence : backgroundConsequence;
                result.NonFailure.Add(nonFailureConsequence);
                result.Fail.Add(0d);
                result.Total.Add(nonFailureConsequence);
                result.Excess.Add(0d);
                result.SortedFailureLosses[i] = 0d;
            }
        }

        result.Finish();
        return result;
    }

    /// <summary>
    /// The mutually-exclusive convention oracle over the two units: per realization one hazard
    /// uniform, then two 45678 uniforms (selection, complement split). The unit masses
    /// [P_A = p₁p₂, p₃] normalize by the mutually-exclusive adjustment and one unit is selected
    /// by the cumulative adjusted masses; an unselected draw lands in the complement, split
    /// partial-versus-background by the conditional share q = w_partial / (1 − P_A) — the
    /// engine's documented across-unit convention (§7.9.5/§7.9.6).
    /// </summary>
    private static OracleResult RunMutuallyExclusiveOracle()
    {
        var tables = OracleTables.Build();
        var hazardStream = new MersenneTwister(HazardSeed);
        var branchStream = new MersenneTwister(BranchSeed);
        var result = new OracleResult { SortedFailureLosses = new double[OracleRealizations] };
        var masses = new double[2];

        for (int i = 0; i < OracleRealizations; i++)
        {
            double stage = Interpolate(tables.HazardProbabilities, tables.HazardStages, hazardStream.NextDouble());
            double selection = branchStream.NextDouble();
            double claim = branchStream.NextDouble();

            double p1 = tables.P1(stage);
            double p2 = tables.P2(stage);
            masses[0] = p1 * p2;
            masses[1] = tables.P3(stage);
            double backgroundConsequence = Interpolate(ConsequenceStages, BackgroundValues, stage);
            double partialConsequence = Interpolate(ConsequenceStages, PartialValues, stage);

            int selected = -1;
            if (masses[0] + masses[1] > 0d)
            {
                double factor = Probability.MutuallyExclusiveAdjustment(masses);
                double cumulative = 0d;
                for (int unit = 0; unit < 2; unit++)
                {
                    cumulative += masses[unit] * factor;
                    if (selection <= cumulative)
                    {
                        selected = unit;
                        break;
                    }
                }
            }

            // The complement mixture drives the Background stream and the complement outcome
            // through ONE uniform — the engine's Background and NonFail entries are the same
            // conditional distribution (§7.9.5).
            double qShare = masses[0] < 1d ? p1 * (1d - p2) / (1d - masses[0]) : 0d;
            double mixtureConsequence = claim <= qShare ? partialConsequence : backgroundConsequence;
            result.Background.Add(mixtureConsequence);
            if (selected >= 0)
            {
                result.FailureCount++;
                double consequence = selected == 0
                    ? Interpolate(ConsequenceStages, FullValues, stage)
                    : Interpolate(ConsequenceStages, StandaloneValues, stage);
                double paired = selected == 0 ? partialConsequence : backgroundConsequence;
                result.NonFailure.Add(0d);
                result.Fail.Add(consequence);
                result.Total.Add(consequence);
                result.Excess.Add(Math.Max(0d, consequence - paired));
                result.ConditionalSumOfSquares += consequence * consequence;
                result.SortedFailureLosses[i] = consequence;
            }
            else
            {
                result.NonFailure.Add(mixtureConsequence);
                result.Fail.Add(0d);
                result.Total.Add(mixtureConsequence);
                result.Excess.Add(0d);
                result.SortedFailureLosses[i] = 0d;
            }
        }

        result.Finish();
        return result;
    }

    /// <summary>
    /// The competing (weak-link) convention oracle over the two units: per realization one
    /// hazard uniform, then three 45678 uniforms (the cascade-unit and standalone capacities,
    /// and the complement split). Capacities invert each unit's failure-mass curve — the
    /// cascade's mass p₁(h)p₂(h) is monotone because the family admits only all-Fail signatures
    /// under competing (§7.9.6) — and the weakest capacity at or below the stage wins.
    /// </summary>
    private static OracleResult RunCompetingOracle()
    {
        var tables = OracleTables.Build();

        // The cascade unit's mass curve on a dense stage grid, for capacity inversion.
        int gridCount = 1201;
        var gridStages = new double[gridCount];
        var gridMasses = new double[gridCount];
        double gridStart = Initiation[0] - ZRange * Initiation[1];
        double gridEnd = Standalone[0] + ZRange * Standalone[1];
        for (int i = 0; i < gridCount; i++)
        {
            gridStages[i] = gridStart + (gridEnd - gridStart) * i / (gridCount - 1);
            gridMasses[i] = tables.P1(gridStages[i]) * tables.P2(gridStages[i]);
        }

        var hazardStream = new MersenneTwister(HazardSeed);
        var branchStream = new MersenneTwister(BranchSeed);
        var result = new OracleResult { SortedFailureLosses = new double[OracleRealizations] };

        for (int i = 0; i < OracleRealizations; i++)
        {
            double stage = Interpolate(tables.HazardProbabilities, tables.HazardStages, hazardStream.NextDouble());
            double uCascade = branchStream.NextDouble();
            double uStandalone = branchStream.NextDouble();
            double claim = branchStream.NextDouble();

            double cascadeCapacity = InverseMass(gridStages, gridMasses, uCascade);
            double standaloneCapacity = Standalone[0] + Standalone[1] * Normal.StandardZ(uStandalone);
            double backgroundConsequence = Interpolate(ConsequenceStages, BackgroundValues, stage);
            double partialConsequence = Interpolate(ConsequenceStages, PartialValues, stage);

            bool cascadeFails = cascadeCapacity <= stage;
            bool standaloneFails = standaloneCapacity <= stage;

            // The complement mixture drives Background and the complement outcome (§7.9.5).
            double breachMass = tables.P1(stage) * tables.P2(stage);
            double qShare = breachMass < 1d ? tables.P1(stage) * (1d - tables.P2(stage)) / (1d - breachMass) : 0d;
            double mixtureConsequence = claim <= qShare ? partialConsequence : backgroundConsequence;
            result.Background.Add(mixtureConsequence);
            if (cascadeFails || standaloneFails)
            {
                result.FailureCount++;
                bool cascadeWins = cascadeFails && (!standaloneFails || cascadeCapacity <= standaloneCapacity);
                double consequence = cascadeWins
                    ? Interpolate(ConsequenceStages, FullValues, stage)
                    : Interpolate(ConsequenceStages, StandaloneValues, stage);
                double paired = cascadeWins ? partialConsequence : backgroundConsequence;
                result.NonFailure.Add(0d);
                result.Fail.Add(consequence);
                result.Total.Add(consequence);
                result.Excess.Add(Math.Max(0d, consequence - paired));
                result.ConditionalSumOfSquares += consequence * consequence;
                result.SortedFailureLosses[i] = consequence;
            }
            else
            {
                result.NonFailure.Add(mixtureConsequence);
                result.Fail.Add(0d);
                result.Total.Add(mixtureConsequence);
                result.Excess.Add(0d);
                result.SortedFailureLosses[i] = 0d;
            }
        }

        result.Finish();
        return result;
    }

    /// <summary>
    /// Inverts a monotone non-decreasing mass curve for a capacity draw: the first stage where
    /// the mass reaches the uniform, linearly interpolated inside the rising segment; a uniform
    /// above the curve's maximum returns past-the-end (an unbreakable capacity).
    /// </summary>
    /// <param name="stages">The ascending stage grid.</param>
    /// <param name="masses">The non-decreasing mass values.</param>
    /// <param name="uniform">The capacity uniform.</param>
    private static double InverseMass(double[] stages, double[] masses, double uniform)
    {
        if (uniform > masses[masses.Length - 1]) return double.MaxValue;
        for (int i = 0; i < masses.Length; i++)
        {
            if (masses[i] >= uniform)
            {
                if (i == 0 || masses[i] <= masses[i - 1]) return stages[i];
                double fraction = (uniform - masses[i - 1]) / (masses[i] - masses[i - 1]);
                return stages[i - 1] + fraction * (stages[i] - stages[i - 1]);
            }
        }
        return double.MaxValue;
    }

    #endregion

    #region Assert Driver

    /// <summary>
    /// Asserts one engine run against an oracle: the five stream means, the failure probability
    /// and its complement, the failure and total standard deviations, the conditional mean, the
    /// assurance measure, two data-driven exceedance probes, value-at-risk, and conditional
    /// value-at-risk (all k·SE-derived; the optional relative floor covers documented
    /// discretization allowances).
    /// </summary>
    /// <param name="oracle">The oracle result.</param>
    /// <param name="analysis">The finished engine analysis.</param>
    /// <param name="label">The assert label.</param>
    /// <param name="totalMeanRelativeFloor">An optional relative floor on the total-mean assert (0 = none).</param>
    private static void AssertAgainstOracle(OracleResult oracle, RiskAnalysis analysis, string label, double totalMeanRelativeFloor = 0d)
    {
        Assert.IsTrue(analysis.IsEstimated, $"{label}: the analysis must estimate.");
        var summary = analysis.RiskResults![0]!;
        var failCurve = analysis.MeanRiskResults!.Curves.Fail;

        Assert.AreEqual(oracle.Fail.Mean, summary.Fail.Mean, K * oracle.Fail.MeanSe, $"{label}: failure risk mean.");
        Assert.AreEqual(oracle.NonFailure.Mean, summary.NonFail.Mean, K * oracle.NonFailure.MeanSe, $"{label}: non-failure risk mean.");
        Assert.AreEqual(oracle.Total.Mean, summary.Total.Mean,
            Math.Max(K * oracle.Total.MeanSe, totalMeanRelativeFloor * oracle.Total.Mean), $"{label}: total risk mean.");
        Assert.AreEqual(oracle.Excess.Mean, summary.Excess.Mean, K * oracle.Excess.MeanSe, $"{label}: incremental (excess) risk mean.");
        Assert.AreEqual(oracle.Background.Mean, summary.Background.Mean, K * oracle.Background.MeanSe, $"{label}: background risk mean.");

        Assert.AreEqual(oracle.FailureProbability, summary.Fail.TotalProbability, K * oracle.FailureProbabilitySe,
            $"{label}: annualized failure probability (final-polarity failure states only).");
        Assert.AreEqual(1d - oracle.FailureProbability, summary.NonFail.TotalProbability, K * oracle.FailureProbabilitySe,
            $"{label}: the non-failure stream must complement the failure probability.");

        Assert.AreEqual(oracle.Fail.Sigma, summary.Fail.StandardDeviation, K * oracle.Fail.SigmaSe, $"{label}: failure risk standard deviation.");
        Assert.AreEqual(oracle.Total.Sigma, summary.Total.StandardDeviation,
            Math.Max(K * oracle.Total.SigmaSe, totalMeanRelativeFloor * oracle.Total.Sigma), $"{label}: total risk standard deviation.");

        double conditionalMean = oracle.Fail.Mean * OracleRealizations / oracle.FailureCount;
        double conditionalSe = Math.Sqrt(Math.Max(0d, oracle.ConditionalSumOfSquares - conditionalMean * conditionalMean * oracle.FailureCount)) / oracle.FailureCount;
        Assert.AreEqual(conditionalMean, summary.Fail.ConditionalMean, K * conditionalSe, $"{label}: conditional mean loss given failure.");

        double[] losses = oracle.SortedFailureLosses;
        AssertExceedance(losses, Threshold, summary.Fail.ConsequenceThresholdProbability, $"{label}: assurance P(C > {Threshold}).");
        double probe1 = losses[OracleRealizations - (int)Math.Round(0.5d * oracle.FailureCount)];
        double probe2 = losses[OracleRealizations - (int)Math.Round(0.05d * oracle.FailureCount)];
        AssertExceedance(losses, probe1, failCurve.LEC.GetYFromX(probe1, Transform.Logarithmic, Transform.Logarithmic), $"{label}: exceedance at the conditional median loss {probe1:G6}.");
        AssertExceedance(losses, probe2, failCurve.LEC.GetYFromX(probe2, Transform.Logarithmic, Transform.Logarithmic), $"{label}: exceedance at the conditional 95th-percentile loss {probe2:G6}.");

        double valueAtRisk = losses[(int)Math.Round((1d - Alpha) * OracleRealizations)];
        double quantileLow = losses[(int)Math.Round((1d - Alpha - 0.001d) * OracleRealizations)];
        double quantileHigh = losses[(int)Math.Round((1d - Alpha + 0.001d) * OracleRealizations)];
        double density = 2d * 0.001d / Math.Max(1e-12, quantileHigh - quantileLow);
        double valueAtRiskSe = Math.Sqrt(Alpha * (1d - Alpha) / OracleRealizations) / density;
        Assert.AreEqual(valueAtRisk, summary.Fail.ValueAtRisk, Math.Max(K * valueAtRiskSe, 1e-3 * valueAtRisk),
            $"{label}: value-at-risk at α = {Alpha}.");

        int tailCount = (int)Math.Round(Alpha * OracleRealizations);
        double tailMean = 0d;
        for (int i = OracleRealizations - tailCount; i < OracleRealizations; i++) tailMean += losses[i];
        tailMean /= tailCount;
        double tailM2 = 0d;
        for (int i = OracleRealizations - tailCount; i < OracleRealizations; i++)
        {
            double delta = losses[i] - tailMean;
            tailM2 += delta * delta;
        }
        double tailMeanSe = Math.Sqrt(tailM2 / tailCount) / Math.Sqrt(tailCount);
        Assert.AreEqual(tailMean, summary.Fail.ConditionalValueAtRisk, Math.Max(K * tailMeanSe, 1e-3 * tailMean),
            $"{label}: conditional value-at-risk at α = {Alpha}.");

        Console.WriteLine(
            $"{label}: mean fail {oracle.Fail.Mean:G6}/{summary.Fail.Mean:G6}, total {oracle.Total.Mean:G6}/{summary.Total.Mean:G6}, " +
            $"excess {oracle.Excess.Mean:G6}/{summary.Excess.Mean:G6}, background {oracle.Background.Mean:G6}/{summary.Background.Mean:G6}, " +
            $"nonfail {oracle.NonFailure.Mean:G6}/{summary.NonFail.Mean:G6}, APF {oracle.FailureProbability:G6}/{summary.Fail.TotalProbability:G6}, " +
            $"σF {oracle.Fail.Sigma:G6}/{summary.Fail.StandardDeviation:G6}, VaR {valueAtRisk:G6}/{summary.Fail.ValueAtRisk:G6}, " +
            $"CVaR {tailMean:G6}/{summary.Fail.ConditionalValueAtRisk:G6} (oracle/engine)");
    }

    /// <summary>
    /// Asserts one loss-exceedance ordinate against the oracle's empirical exceedance at the
    /// binomial k·SE, verifying first that the probe carries at least 100 exceedances (the
    /// tolerance-policy floor for curve checks).
    /// </summary>
    /// <param name="sortedLosses">The oracle's sorted unconditional losses.</param>
    /// <param name="level">The consequence level probed.</param>
    /// <param name="engineExceedance">The engine's exceedance ordinate at the level.</param>
    /// <param name="message">The assert label.</param>
    private static void AssertExceedance(double[] sortedLosses, double level, double engineExceedance, string message)
    {
        int exceeding = 0;
        for (int i = sortedLosses.Length - 1; i >= 0 && sortedLosses[i] > level; i--) exceeding++;
        Assert.IsTrue(exceeding >= 100, $"{message} — the probe must carry at least 100 exceedances (found {exceeding}).");
        double probability = exceeding / (double)sortedLosses.Length;
        double se = Math.Sqrt(probability * (1d - probability) / sortedLosses.Length);
        Assert.AreEqual(probability, engineExceedance, K * se, message);
    }

    #endregion

    /// <summary>
    /// The flagship partial-damage cascade versus its natural Monte Carlo oracle: APF is the
    /// breach product only (final polarity), the partial state rides the complement, and the
    /// full-breach excess pairs against the partial sibling.
    /// </summary>
    [TestMethod]
    public void Test_PartialDamageCascade_VsOracle()
    {
        var oracle = RunSingleCascadeOracle();
        var analysis = BuildAnalysis(FailureModeMethod.MutuallyExclusive, includePartial: true, includeStandalone: false);
        analysis.RunAsync().GetAwaiter().GetResult();
        AssertAgainstOracle(oracle, analysis, "Partial-damage cascade");
    }

    /// <summary>
    /// The cascade unit and a standalone unit under joint independent failures (Maximum rule)
    /// versus the natural oracle — the across-group semantics with the claimed state's
    /// conditional complement, exact under independence.
    /// </summary>
    [TestMethod]
    public void Test_JointAcrossUnits_VsOracle()
    {
        var oracle = RunJointOracle();
        var analysis = BuildAnalysis(FailureModeMethod.JointFailures, includePartial: true, includeStandalone: true);
        analysis.RunAsync().GetAwaiter().GetResult();
        AssertAgainstOracle(oracle, analysis, "Joint across units");
    }

    /// <summary>
    /// The cascade unit and a standalone unit under the mutually-exclusive method versus the
    /// convention oracle (unit-mass selection; complement split by the conditional share).
    /// </summary>
    [TestMethod]
    public void Test_MutuallyExclusiveAcrossUnits_VsOracle()
    {
        var oracle = RunMutuallyExclusiveOracle();
        var analysis = BuildAnalysis(FailureModeMethod.MutuallyExclusive, includePartial: true, includeStandalone: true);
        analysis.RunAsync().GetAwaiter().GetResult();
        AssertAgainstOracle(oracle, analysis, "Mutually exclusive across units");
    }

    /// <summary>
    /// The cascade unit and a standalone unit under competing (weak-link) failures versus the
    /// capacity oracle. The total-mean and total-σ asserts carry a 1% relative floor for the
    /// engine's 200-bin cumulative-incidence discretization (documented allowance; the union
    /// and per-stream probabilities remain at the binomial k·SE).
    /// </summary>
    [TestMethod]
    public void Test_CompetingAcrossUnits_VsOracle()
    {
        var oracle = RunCompetingOracle();
        var analysis = BuildAnalysis(FailureModeMethod.CompetingFailures, includePartial: true, includeStandalone: true);
        analysis.RunAsync().GetAwaiter().GetResult();
        AssertAgainstOracle(oracle, analysis, "Competing across units", totalMeanRelativeFloor: 1e-2);
    }

    /// <summary>
    /// The saturated-stage equivalence: a two-stage cascade whose progression fragility is
    /// identically one must reproduce the single-stage model EXACTLY — the polarity product's
    /// second factor is 1.0, the model is deterministic (no seeded draws in a mean-only run),
    /// and the quadrature therefore sees a bit-identical integrand.
    /// </summary>
    [TestMethod]
    public void Test_SaturatedStage_SingleStageEquivalence_BitExact()
    {
        // The cascade with a certain progression stage.
        var cascade = new SystemComponent { Name = "Saturated Cascade" };
        var hazard = new HazardElement("Hazard") { Function = Hazard() };
        var initiation = new ResponseElement("Initiation")
        {
            Function = Fragility("Initiation Fragility", Initiation),
            Input = new RiskConnection(hazard),
        };
        var certain = new ResponseElement("Certain Progression")
        {
            Function = SaturatedFragility(),
            Input = new RiskConnection(initiation),
        };
        var terminal = new ConsequenceElement("Breach") { Input = new RiskConnection(certain) };
        terminal.Functions.Add(Consequence("Breach Loss", FullValues));
        var background = new ConsequenceElement("Background") { Input = new RiskConnection(hazard) };
        background.Functions.Add(Consequence("Background Loss", BackgroundValues));
        cascade.Graph.AddElement(hazard);
        cascade.Graph.AddElement(initiation);
        cascade.Graph.AddElement(certain);
        cascade.Graph.AddElement(terminal);
        cascade.Graph.AddElement(background);
        var cascadeAnalysis = new RiskAnalysis(new[] { cascade });
        cascadeAnalysis.RunAsync().GetAwaiter().GetResult();

        // The single-stage twin.
        var single = new SystemComponent { Name = "Single Stage" };
        var hazard2 = new HazardElement("Hazard") { Function = Hazard() };
        var initiation2 = new ResponseElement("Initiation")
        {
            Function = Fragility("Initiation Fragility", Initiation),
            Input = new RiskConnection(hazard2),
        };
        var terminal2 = new ConsequenceElement("Breach") { Input = new RiskConnection(initiation2) };
        terminal2.Functions.Add(Consequence("Breach Loss", FullValues));
        var background2 = new ConsequenceElement("Background") { Input = new RiskConnection(hazard2) };
        background2.Functions.Add(Consequence("Background Loss", BackgroundValues));
        single.Graph.AddElement(hazard2);
        single.Graph.AddElement(initiation2);
        single.Graph.AddElement(terminal2);
        single.Graph.AddElement(background2);
        var singleAnalysis = new RiskAnalysis(new[] { single });
        singleAnalysis.RunAsync().GetAwaiter().GetResult();

        Assert.AreEqual(
            BitConverter.DoubleToInt64Bits(singleAnalysis.RiskResults![0]!.Total.Mean),
            BitConverter.DoubleToInt64Bits(cascadeAnalysis.RiskResults![0]!.Total.Mean),
            "Total mean must be bit-identical under a saturated stage.");
        Assert.AreEqual(
            BitConverter.DoubleToInt64Bits(singleAnalysis.RiskResults[0]!.Fail.TotalProbability),
            BitConverter.DoubleToInt64Bits(cascadeAnalysis.RiskResults[0]!.Fail.TotalProbability),
            "The annualized failure probability must be bit-identical under a saturated stage.");
    }

    /// <summary>
    /// Reliability mode over a consequence-free two-stage cascade: the annualized failure
    /// probability equals the Rao-Blackwellized oracle mean of p₁(h)·p₂(h) over the hazard
    /// stream.
    /// </summary>
    [TestMethod]
    public void Test_ReliabilityCascade_ApfVsOracle()
    {
        // Oracle: E[p₁p₂] over the hazard draws (no branch draws needed).
        var tables = OracleTables.Build();
        var hazardStream = new MersenneTwister(HazardSeed);
        var apf = new Moments();
        for (int i = 0; i < OracleRealizations; i++)
        {
            double stage = Interpolate(tables.HazardProbabilities, tables.HazardStages, hazardStream.NextDouble());
            apf.Add(tables.P1(stage) * tables.P2(stage));
        }

        // Engine: the consequence-free cascade in reliability mode.
        var component = new SystemComponent { Name = "Reliability Cascade" };
        var hazard = new HazardElement("Hazard") { Function = Hazard() };
        var initiation = new ResponseElement("Initiation")
        {
            Function = Fragility("Initiation Fragility", Initiation),
            Input = new RiskConnection(hazard),
        };
        var progression = new ResponseElement("Progression")
        {
            Function = Fragility("Progression Fragility", Progression),
            Input = new RiskConnection(initiation),
        };
        var terminal = new ConsequenceElement("Breach") { Input = new RiskConnection(progression) };
        component.Graph.AddElement(hazard);
        component.Graph.AddElement(initiation);
        component.Graph.AddElement(progression);
        component.Graph.AddElement(terminal);
        var analysis = new RiskAnalysis(new[] { component });
        analysis.Options.Mode = RiskAnalysisMode.Reliability;
        analysis.RunAsync().GetAwaiter().GetResult();

        Assert.AreEqual(apf.Mean, analysis.RiskResults![0]!.Fail.TotalProbability, K * apf.MeanSe,
            "Reliability-mode cascade APF versus the Rao-Blackwellized oracle.");
    }

    /// <summary>
    /// System aggregation smokes with a cascading component: the additive system's total mean
    /// is the component-mean sum (the convolution's mean-preservation gate), and the joint
    /// system computes with an intact mass balance (no truncation warning on this small model).
    /// </summary>
    [TestMethod]
    public void Test_SystemAggregation_WithCascade_Smoke()
    {
        static SystemComponent PlainComponent()
        {
            var component = new SystemComponent { Name = "Plain Dam" };
            var hazard = new HazardElement("Hazard") { Function = Hazard() };
            var response = new ResponseElement("Standalone")
            {
                Function = Fragility("Standalone Fragility", Standalone),
                Input = new RiskConnection(hazard),
            };
            var terminal = new ConsequenceElement("Loss") { Input = new RiskConnection(response) };
            terminal.Functions.Add(Consequence("Standalone Damages", StandaloneValues));
            var background = new ConsequenceElement("Background") { Input = new RiskConnection(hazard) };
            background.Functions.Add(Consequence("Background Loss", BackgroundValues));
            component.Graph.AddElement(hazard);
            component.Graph.AddElement(response);
            component.Graph.AddElement(terminal);
            component.Graph.AddElement(background);
            return component;
        }

        // Additive: system total mean = Σ component means (the exact lattice convolution).
        var additive = new RiskAnalysis(new[] { CascadeComponent(includePartial: true, includeStandalone: false), PlainComponent() });
        additive.RunAsync().GetAwaiter().GetResult();
        var systemSummary = additive.RiskResults![0]!;
        double componentSum = additive.MeanRiskResults!.Components.Sum(c => c.Curves.Total.Mean);
        Assert.AreEqual(componentSum, systemSummary.Total.Mean, 1e-6 * componentSum,
            "The additive system total mean must equal the component-mean sum with a cascading component.");

        // Joint: computes clean — the exhaustive mass balance holds with claimed states inside
        // the integrand's component entries.
        var joint = new RiskAnalysis(new[] { CascadeComponent(includePartial: true, includeStandalone: false), PlainComponent() });
        joint.Options.SystemRiskMethod = SystemRiskType.JointRiskMethod;
        joint.RunAsync().GetAwaiter().GetResult();
        Assert.IsTrue(joint.IsEstimated, "The joint system must estimate with a cascading component.");
        Assert.IsFalse(joint.ComputationWarnings.Any(w => w.Contains("mass", StringComparison.OrdinalIgnoreCase)),
            $"The joint system's mass balance must hold: {string.Join(" | ", joint.ComputationWarnings)}");
    }

    /// <summary>
    /// The reproducibility pins: the same seed is bit-identical across repeated runs; element
    /// renames, id reassignment, and the XML round-trip are bit-identical (ports and polarities
    /// survive persistence); rewiring the partial terminal onto the Fail port moves results
    /// (the counter-pin — the polarity IS compute content).
    /// </summary>
    [TestMethod]
    public void Test_Reproducibility_RenameRoundTripAndPortRewire()
    {
        static RiskAnalysis UncertainCascade()
        {
            var component = CascadeComponent(includePartial: true, includeStandalone: false);

            // Give the initiation fragility knowledge uncertainty so the seeded sampler walk is
            // genuinely exercised — a coarse two-knot triangular fragility keeps the percentile
            // curves co-monotone (perturbing the dense 321-knot table would overlap adjacent
            // ordinate spreads and fail ordinate validation).
            var initiation = (ResponseElement)component.Graph.GetElement("Initiation")!;
            ((TabularResponse)initiation.Function!).UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(100d, new Triangular(0d, 0.02d, 0.05d)),
                    new UncertainOrdinate(220d, new Triangular(0.7d, 0.9d, 1d)),
                },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Triangular);

            var analysis = new RiskAnalysis(new[] { component });
            analysis.Options.EstimateMeanRiskOnly = false;
            analysis.Options.Realizations = 200;
            return analysis;
        }

        static long[] Signature(RiskAnalysis analysis)
        {
            var lec = analysis.MeanRiskResults!.Curves.Total.LEC;
            var signature = new long[2 * lec.Count + 2];
            for (int i = 0; i < lec.Count; i++)
            {
                signature[2 * i] = BitConverter.DoubleToInt64Bits(lec[i].X);
                signature[2 * i + 1] = BitConverter.DoubleToInt64Bits(lec[i].Y);
            }
            signature[2 * lec.Count] = BitConverter.DoubleToInt64Bits(analysis.RiskResults![0]!.Total.Mean);
            signature[2 * lec.Count + 1] = BitConverter.DoubleToInt64Bits(analysis.RiskResults[0]!.Fail.TotalProbability);
            return signature;
        }

        // Same seed → bit-identical.
        var baseline = UncertainCascade();
        baseline.RunAsync().GetAwaiter().GetResult();
        var repeat = UncertainCascade();
        repeat.RunAsync().GetAwaiter().GetResult();
        CollectionAssert.AreEqual(Signature(baseline), Signature(repeat), "A repeated run must be bit-identical.");

        // Renames + new ids + the XML round-trip → bit-identical.
        var renamed = UncertainCascade();
        var component = renamed.Components[0];
        component.Name = "Renamed Cascade";
        foreach (var element in component.Graph.Elements.ToList())
        {
            component.Graph.TryRenameElement(element, element.Name + " (renamed)");
            element.AssignNewId();
        }
        var restored = new RiskAnalysis(new[] { new SystemComponent(component.ToXElement()) });
        restored.Options.EstimateMeanRiskOnly = false;
        restored.Options.Realizations = 200;
        restored.RunAsync().GetAwaiter().GetResult();
        CollectionAssert.AreEqual(Signature(baseline), Signature(restored),
            "Renames, id reassignment, and the XML round-trip must be bit-identical (ports and polarities survive persistence).");

        // The counter-pin: rewiring the partial terminal onto the Fail port (a duplicate-leaf
        // claim — legal, preserving the legacy fan-out semantics) changes the computed results.
        var rewired = UncertainCascade();
        var partial = (ConsequenceElement)rewired.Components[0].Graph.GetElement("Partial Damage")!;
        var progression = rewired.Components[0].Graph.GetElement("Progression")!;
        partial.Input = new RiskConnection(progression, 0);
        rewired.RunAsync().GetAwaiter().GetResult();
        CollectionAssert.AreNotEqual(Signature(baseline), Signature(rewired),
            "Moving a terminal to the other branch port is a compute edit and must move results.");
    }
}
