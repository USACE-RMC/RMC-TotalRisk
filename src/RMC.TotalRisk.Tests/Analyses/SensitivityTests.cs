using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Data.Statistics;
using Numerics.Distributions;
using RMC.TotalRisk.Analyses;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.Results;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.RiskFunctions.Transforms;
using RMC.TotalRisk.Systems.Components;
using RMC.TotalRisk.Systems.Components.Graph;
using RMC.TotalRisk.Core.Support;

namespace RMC.TotalRisk.Tests.Analyses;

/// <summary>
/// Unit tests for the unified sensitivity engine — the measure-level and
/// hazard-level outputs against seed-rederived knowledge inputs: enum pins, the input-column
/// walk (labels, coupling columns, shared-instance dedup), the scope model, rank-exactness on
/// monotone maps, the profile-axis-native hazard interpretation, the null contracts, and the
/// matrix consistency.
/// </summary>
[TestClass]
public class SensitivityTests
{
    /// <summary>Builds the shared deterministic stage-frequency hazard (0.999 → 0 ft up to 0.001 → 30 ft).</summary>
    private static TabularHazard StageFrequency()
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

    /// <summary>Builds a deterministic fragility rising from (10 → 0) to (20 → 1).</summary>
    private static TabularResponse Fragility(string name = "Fragility")
    {
        return new TabularResponse
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(10d, new Deterministic(0d)), new UncertainOrdinate(20d, new Deterministic(1d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds an uncertain fragility (triangular ordinates — one knowledge dimension).</summary>
    private static TabularResponse UncertainFragility(string name = "Breach Fragility")
    {
        return new TabularResponse
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(10d, new Triangular(0d, 0.05d, 0.1d)), new UncertainOrdinate(20d, new Triangular(0.7d, 0.9d, 1d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Triangular),
        };
    }

    /// <summary>Builds a deterministic consequence, linear from (0 → 0) to (30 → top).</summary>
    private static TabularConsequence Consequence(string name, double top)
    {
        return new TabularConsequence
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Life Loss",
            ConsequenceUnit = "lives",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(0d, new Deterministic(0d)), new UncertainOrdinate(30d, new Deterministic(top)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds an uncertain consequence (Normal top ordinate — knowledge through the coupling column).</summary>
    private static TabularConsequence UncertainConsequence(string name, double top)
    {
        return new TabularConsequence
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Life Loss",
            ConsequenceUnit = "lives",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(0d, new Normal(0d, 0d)), new UncertainOrdinate(30d, new Normal(top, top / 10d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Normal),
        };
    }

    /// <summary>Builds a single-mode component (uncertain fragility by default) with a deterministic non-failure path.</summary>
    private static SystemComponent Component(string name = "Dam", IResponseFunction? response = null,
        IConsequenceFunction? failureConsequence = null)
    {
        var component = new SystemComponent { Name = name };
        component.HazardFunction = StageFrequency();
        component.AddFailureMode(new FailureMode(null, null, response ?? UncertainFragility(), failureConsequence ?? Consequence("Failure Loss", 300d)));
        component.AddFailureMode(new FailureMode(null, null, null, Consequence("Non-Failure Loss", 60d)));
        return component;
    }

    /// <summary>Runs a full-uncertainty analysis at 100 realizations.</summary>
    private static async Task<RiskAnalysis> RunFull(params SystemComponent[] components)
    {
        var analysis = new RiskAnalysis(components);
        analysis.Options.EstimateMeanRiskOnly = false;
        analysis.Options.Realizations = 100;
        await analysis.RunAsync();
        Assert.IsTrue(analysis.IsEstimated);
        return analysis;
    }

    /// <summary>Verifies the new enum member pins (runtime-only; values are API contract).</summary>
    [TestMethod]
    public void Test_Enums_MemberPins()
    {
        Assert.AreEqual(0, (int)SensitivityMeasure.PearsonCorrelation);
        Assert.AreEqual(1, (int)SensitivityMeasure.SpearmanCorrelation);
        Assert.AreEqual(2, (int)SensitivityMeasure.SensitivityIndex);
        Assert.AreEqual(3, (int)SensitivityMeasure.FirstOrderSobol);
        Assert.AreEqual(4, (int)SensitivityMeasure.PawnMedian);
        Assert.AreEqual(5, (int)SensitivityMeasure.BorgonovoDelta);

        Assert.AreEqual(0, (int)RiskMeasure.TotalProbability);
        Assert.AreEqual(1, (int)RiskMeasure.ConditionalMean);
        Assert.AreEqual(2, (int)RiskMeasure.Mean);
        Assert.AreEqual(3, (int)RiskMeasure.StandardDeviation);
        Assert.AreEqual(4, (int)RiskMeasure.Skewness);
        Assert.AreEqual(5, (int)RiskMeasure.Kurtosis);
        Assert.AreEqual(6, (int)RiskMeasure.ConsequenceThresholdProbability);
        Assert.AreEqual(7, (int)RiskMeasure.HazardThresholdProbability);
        Assert.AreEqual(8, (int)RiskMeasure.ValueAtRisk);
        Assert.AreEqual(9, (int)RiskMeasure.ConditionalValueAtRisk);
    }

    /// <summary>
    /// Verifies rank-exactness on a single monotone knowledge input: with only the fragility
    /// uncertain, the annualized failure probability is strictly monotone in its draw, so
    /// Spearman is exactly ±1, Pearson is strong, and the sensitivity index is Pearson².
    /// </summary>
    [TestMethod]
    public async Task Test_MeasureSensitivity_MonotoneInput_RankExact()
    {
        // Arrange / Act
        var analysis = await RunFull(Component());
        var spearman = analysis.MeasureSensitivity(RiskMeasure.TotalProbability, RiskType.Fail, SensitivityMeasure.SpearmanCorrelation, componentIndex: 0)!;
        var pearson = analysis.MeasureSensitivity(RiskMeasure.TotalProbability, RiskType.Fail, SensitivityMeasure.PearsonCorrelation, componentIndex: 0)!;
        var index = analysis.MeasureSensitivity(RiskMeasure.TotalProbability, RiskType.Fail, SensitivityMeasure.SensitivityIndex, componentIndex: 0)!;

        // Assert — one input column, rank-perfect association.
        Assert.AreEqual(1, spearman.Entries.Count, "The single uncertain fragility is the only knowledge input.");
        Assert.AreEqual("Dam - Breach Fragility", spearman.Entries[0].Label);
        Assert.AreEqual(1d, Math.Abs(spearman.Entries[0].Value), 1e-12, "A monotone map must be rank-perfect.");
        Assert.IsTrue(Math.Abs(pearson.Entries[0].Value) > 0.9d, "The linear association must be strong.");
        Assert.AreEqual(Math.Pow(pearson.Entries[0].Value, 2d), index.Entries[0].Value, 1e-15,
            "The sensitivity index is the squared Pearson correlation.");
        Assert.AreEqual(100, spearman.Realizations);
        Assert.AreEqual(RiskType.Fail, spearman.RiskType);
    }

    /// <summary>
    /// Verifies the given-data measures behind the engine: each entry equals the direct
    /// upstream estimator call over the re-derived knowledge column and the stored outputs
    /// bit-exactly (the plumbing proof), and the deterministic monotone single-input map
    /// drives all three toward their saturated upper ranges.
    /// </summary>
    [TestMethod]
    public async Task Test_GivenDataMeasures_MatchDirectUpstreamCalls()
    {
        // Arrange — the stored component-scope outputs and the re-derived knowledge column.
        var analysis = await RunFull(Component());
        var outputs = new double[100];
        for (int i = 0; i < 100; i++)
        {
            outputs[i] = analysis.RiskResults![i]!.ComponentResults[0].Fail.TotalProbability;
        }
        var components = new List<SystemComponent>(analysis.Components);
        SystemComponent.AssignOccurrenceIndices(components);
        int seed = SeedHelpers.HashCombine(analysis.Options.PRNGSeed, components[0].CanonicalHash(), components[0].OccurrenceIndex);
        components[0].SetupSamplers(100, seed, analysis.Options.SamplingScheme);
        var inputs = new List<SensitivityInput>();
        components[0].CollectSensitivityInputs(inputs);
        var column = new double[100];
        for (int i = 0; i < 100; i++) column[i] = inputs[0].Read(i);

        // Act
        var sobol = analysis.MeasureSensitivity(RiskMeasure.TotalProbability, RiskType.Fail, SensitivityMeasure.FirstOrderSobol, componentIndex: 0)!;
        var pawn = analysis.MeasureSensitivity(RiskMeasure.TotalProbability, RiskType.Fail, SensitivityMeasure.PawnMedian, componentIndex: 0)!;
        var delta = analysis.MeasureSensitivity(RiskMeasure.TotalProbability, RiskType.Fail, SensitivityMeasure.BorgonovoDelta, componentIndex: 0)!;

        // Assert — bit agreement with the upstream estimators on the identical sample.
        Assert.AreEqual(GlobalSensitivity.FirstOrderSobol(column, outputs), sobol.Entries[0].Value, 0d,
            "The engine routes the same column and outputs through the upstream Sobol estimator.");
        Assert.AreEqual(GlobalSensitivity.PawnMedian(column, outputs), pawn.Entries[0].Value, 0d,
            "The engine routes the same column and outputs through the upstream PAWN estimator.");
        Assert.AreEqual(GlobalSensitivity.BorgonovoDelta(column, outputs), delta.Entries[0].Value, 0d,
            "The engine routes the same column and outputs through the upstream Borgonovo estimator.");
        Assert.IsTrue(sobol.Entries[0].Value > 0.9d, "A deterministic monotone map saturates the main-effect share.");
        Assert.IsTrue(pawn.Entries[0].Value > 0.6d, "A deterministic map separates the conditional distributions.");
        Assert.IsTrue(delta.Entries[0].Value > 0.5d, "A deterministic map concentrates the conditional classes.");
        Assert.AreEqual(SensitivityMeasure.FirstOrderSobol, sobol.Measure);
    }

    /// <summary>
    /// Verifies the given-data sample floor: a design smaller than the bin convention returns
    /// null rather than a noise-dominated result, while the correlation members still answer
    /// on the same design.
    /// </summary>
    [TestMethod]
    public async Task Test_GivenData_BelowBinConvention_ReturnsNull()
    {
        // Arrange
        var analysis = await RunFull(Component());

        // Act
        var givenData = analysis.HazardLevelSensitivity(0, 15d, SensitivityMeasure.BorgonovoDelta, RiskType.Fail, realizations: 10);
        var correlation = analysis.HazardLevelSensitivity(0, 15d, SensitivityMeasure.PearsonCorrelation, RiskType.Fail, realizations: 10);

        // Assert
        Assert.IsNull(givenData, "Fewer valid pairs than bins cannot support a given-data estimate.");
        Assert.IsNotNull(correlation, "The correlation members keep the three-pair floor.");
    }

    /// <summary>
    /// Verifies the input-column walk: an uncertain consequence contributes its coupling
    /// column (consequences are outside the sampler walk — their knowledge is the shared
    /// failure/non-failure coupling draw), and one response instance shared by two modes contributes exactly one column
    /// (one instance = one knowledge quantity).
    /// </summary>
    [TestMethod]
    public async Task Test_MeasureSensitivity_CouplingColumn_AndSharedInstanceDedup()
    {
        // Arrange — a shared uncertain response across two modes; mode A's consequence uncertain.
        var shared = UncertainFragility("Shared Breach");
        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = StageFrequency();
        component.AddFailureMode(new FailureMode(null, null, shared, UncertainConsequence("A Loss", 300d)));
        component.AddFailureMode(new FailureMode(null, null, shared, Consequence("B Loss", 600d)));
        component.AddFailureMode(new FailureMode(null, null, null, Consequence("Non-Failure Loss", 60d)));

        // Act
        var analysis = await RunFull(component);
        var result = analysis.MeasureSensitivity(RiskMeasure.Mean, RiskType.Total, SensitivityMeasure.SpearmanCorrelation, componentIndex: 0)!;

        // Assert — columns: mode A's coupling draw + ONE shared response column. Mode B's
        // coupling is not consumed (its consequence and the paired non-failure are
        // deterministic), and the shared instance appears once.
        var labels = result.Entries.Select(e => e.Label).ToList();
        Assert.AreEqual(1, labels.Count(l => l.Contains("Shared Breach")), "A shared instance is one knowledge quantity — one column.");
        Assert.AreEqual(1, labels.Count(l => l.Contains("Consequence Knowledge")), "Only the uncertain consequence's coupling column is consumed.");
        Assert.IsTrue(labels.Single(l => l.Contains("Consequence Knowledge")).Contains("A Loss"),
            "The coupling column must carry the owning mode's label.");
        Assert.AreEqual(2, labels.Count, "Exactly the two consumed knowledge columns.");
    }

    /// <summary>
    /// Verifies the scope model: the system scope correlates against the union of every
    /// component's columns, a component scope against its own columns only, the failure-mode
    /// scope narrows the output while keeping the owner's columns — and a component-scope
    /// result is bit-identical whether or not another component exists in the analysis
    /// (content-seeded independence).
    /// </summary>
    [TestMethod]
    public async Task Test_MeasureSensitivity_ScopeModel_AndComponentInvariance()
    {
        // Arrange — two components with distinct content.
        var pair = await RunFull(Component("Dam A"), Component("Dam B", failureConsequence: Consequence("Failure Loss", 600d)));
        var solo = await RunFull(Component("Dam A"));

        // Act
        var system = pair.MeasureSensitivity(RiskMeasure.Mean, RiskType.Total, SensitivityMeasure.SpearmanCorrelation)!;
        var componentScope = pair.MeasureSensitivity(RiskMeasure.Mean, RiskType.Total, SensitivityMeasure.SpearmanCorrelation, componentIndex: 0)!;
        var soloScope = solo.MeasureSensitivity(RiskMeasure.Mean, RiskType.Total, SensitivityMeasure.SpearmanCorrelation, componentIndex: 0)!;
        var modeScope = pair.MeasureSensitivity(RiskMeasure.Mean, RiskType.Fail, SensitivityMeasure.SpearmanCorrelation, componentIndex: 0, failureModeIndex: 0)!;

        // Assert — the union at system scope; own columns at component scope.
        Assert.AreEqual(2, system.Entries.Count, "System scope must union every component's columns.");
        Assert.IsTrue(system.Entries.Any(e => e.Label.StartsWith("Dam A", StringComparison.Ordinal)));
        Assert.IsTrue(system.Entries.Any(e => e.Label.StartsWith("Dam B", StringComparison.Ordinal)));
        Assert.AreEqual(1, componentScope.Entries.Count, "Component scope must carry its own columns only.");

        // The component-scope association is invariant to the other component's presence.
        Assert.AreEqual(soloScope.Entries[0].Value, componentScope.Entries[0].Value, 0d,
            "Content-seeded draws and 1D results make component-scope sensitivity independent of the rest of the system.");

        // The mode scope narrows the output, keeps the owner's columns, and gates the stream.
        Assert.AreEqual(1, modeScope.Entries.Count);
        Assert.ThrowsException<ArgumentException>(() =>
            pair.MeasureSensitivity(RiskMeasure.Mean, RiskType.Total, SensitivityMeasure.SpearmanCorrelation, componentIndex: 0, failureModeIndex: 0));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            pair.MeasureSensitivity(RiskMeasure.Mean, RiskType.Total, SensitivityMeasure.SpearmanCorrelation, componentIndex: 7));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            pair.MeasureSensitivity(RiskMeasure.Mean, RiskType.Total, SensitivityMeasure.SpearmanCorrelation, failureModeIndex: 0));
    }

    /// <summary>
    /// Verifies the null contracts: no results (unrun), a mean-only run (a single realization),
    /// and the matrix's empty-list analog.
    /// </summary>
    [TestMethod]
    public async Task Test_MeasureSensitivity_NullContracts()
    {
        // Unrun.
        var unrun = new RiskAnalysis(new[] { Component() });
        Assert.IsNull(unrun.MeasureSensitivity(RiskMeasure.Mean, RiskType.Total, SensitivityMeasure.PearsonCorrelation));

        // Mean-only — one realization cannot correlate.
        var meanOnly = new RiskAnalysis(new[] { Component() });
        await meanOnly.RunAsync();
        Assert.IsNull(meanOnly.MeasureSensitivity(RiskMeasure.Mean, RiskType.Total, SensitivityMeasure.PearsonCorrelation));
        Assert.AreEqual(0, meanOnly.MeasureSensitivityMatrix(RiskType.Total, SensitivityMeasure.PearsonCorrelation).Count);
    }

    /// <summary>
    /// Verifies the matrix call matches the single-measure calls entry for entry (one input
    /// derivation amortized across the catalog).
    /// </summary>
    [TestMethod]
    public async Task Test_MeasureSensitivityMatrix_MatchesSingleCalls()
    {
        // Arrange / Act
        var analysis = await RunFull(Component());
        var matrix = analysis.MeasureSensitivityMatrix(RiskType.Total, SensitivityMeasure.PearsonCorrelation, componentIndex: 0);

        // Assert — every matrix result matches its dedicated single call bit for bit.
        Assert.IsTrue(matrix.Count >= 6, "The catalog's computable measures must be present.");
        foreach (RiskMeasure measure in Enum.GetValues<RiskMeasure>())
        {
            var single = analysis.MeasureSensitivity(measure, RiskType.Total, SensitivityMeasure.PearsonCorrelation, componentIndex: 0);
            var row = matrix.FirstOrDefault(r => r.OutputLabel.StartsWith(measure.ToString(), StringComparison.Ordinal));
            if (single == null)
            {
                Assert.IsNull(row, $"A null single call must have no matrix row ({measure}).");
                continue;
            }
            Assert.IsNotNull(row, $"The matrix must carry {measure}.");
            Assert.AreEqual(single.Entries[0].Value, row!.Entries[0].Value, 0d, $"Matrix and single calls must agree ({measure}).");
        }
    }

    /// <summary>
    /// Verifies the hazard-level tornado: rank-exact on the monotone fragility at a fixed
    /// level, content-seeded reproducibility (two calls bit-identical), the design-size
    /// parameter, and the legacy null contracts (invalid analysis; deterministic component).
    /// </summary>
    [TestMethod]
    public void Test_HazardLevelSensitivity_KnownPoint_AndContracts()
    {
        // Arrange
        var analysis = new RiskAnalysis(new[] { Component() });

        // Act
        var first = analysis.HazardLevelSensitivity(0, 15d, SensitivityMeasure.SpearmanCorrelation, RiskType.Fail)!;
        var second = analysis.HazardLevelSensitivity(0, 15d, SensitivityMeasure.SpearmanCorrelation, RiskType.Fail)!;
        var sized = analysis.HazardLevelSensitivity(0, 15d, SensitivityMeasure.SpearmanCorrelation, RiskType.Fail, realizations: 250)!;

        // Assert — rank-perfect at the level; reproducible bit for bit; the design size honored.
        Assert.AreEqual(1, first.Entries.Count);
        Assert.AreEqual(1d, Math.Abs(first.Entries[0].Value), 1e-12, "The fragility draw is rank-perfect for risk at a level.");
        Assert.AreEqual(first.Entries[0].Value, second.Entries[0].Value, 0d, "Content-seeded designs must reproduce bit for bit.");
        Assert.AreEqual(100, first.Realizations);
        Assert.AreEqual(250, sized.Realizations);

        // Contracts: a deterministic component returns null (the v1.0 contract); an invalid
        // analysis returns null; bad arguments throw.
        var deterministic = new RiskAnalysis(new[] { Component(response: Fragility()) });
        Assert.IsNull(deterministic.HazardLevelSensitivity(0, 15d, SensitivityMeasure.PearsonCorrelation, RiskType.Fail));
        var invalid = new RiskAnalysis(new[] { new SystemComponent { Name = "Empty" } });
        Assert.IsNull(invalid.HazardLevelSensitivity(0, 15d, SensitivityMeasure.PearsonCorrelation, RiskType.Fail));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            analysis.HazardLevelSensitivity(3, 15d, SensitivityMeasure.PearsonCorrelation, RiskType.Fail));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            analysis.HazardLevelSensitivity(0, 15d, SensitivityMeasure.PearsonCorrelation, RiskType.Fail, realizations: 2));
    }

    /// <summary>
    /// Verifies the profile-axis-native interpretation: with a deterministic
    /// rating selected as the profile axis, the tornado at profile level T(h) is bit-identical
    /// to the raw-axis tornado at h on the unprofiled clone — each realization inverts its own
    /// sampled chain, and the seed-inert selector leaves the draws untouched.
    /// </summary>
    [TestMethod]
    public void Test_HazardLevelSensitivity_ProfileAxisNative()
    {
        SystemComponent Build()
        {
            var hazard = new TabularHazard
            {
                Name = "Flow Frequency",
                SpecifiedHazard = "Flow",
                HazardUnit = "cfs",
                NoUncertaintyFunction = new UncertainOrderedPairedData(
                    new[]
                    {
                        new UncertainOrdinate(0.999d, new Deterministic(0d)),
                        new UncertainOrdinate(0.5d, new Deterministic(50d)),
                        new UncertainOrdinate(0.001d, new Deterministic(100d)),
                    },
                    true, SortOrder.Descending, true, SortOrder.Ascending, UnivariateDistributionType.Deterministic),
            };
            // The rating declares an ascending output axis — the profile-axis-native tornado
            // inverts the sampled chain, and inversion requires an ordered output.
            var rating = new TabularTransform
            {
                Name = "Rating",
                SpecifiedHazard = "Flow",
                HazardUnit = "cfs",
                TransformedHazard = "Stage",
                TransformedHazardUnit = "ft",
                UncertainOrderedPairedData = new UncertainOrderedPairedData(
                    new[] { new UncertainOrdinate(0d, new Deterministic(0d)), new UncertainOrdinate(100d, new Deterministic(50d)) },
                    true, SortOrder.Ascending, false, SortOrder.Ascending, UnivariateDistributionType.Deterministic),
            };
            var component = new SystemComponent { Name = "Levee" };
            component.HazardFunction = hazard;
            component.AddFailureMode(new FailureMode(new List<ITransformFunction> { rating }, null,
                UncertainFragility(), Consequence("Failure Loss", 300d)));
            component.AddFailureMode(new FailureMode(null, null, null, Consequence("Non-Failure Loss", 60d)));
            return component;
        }

        // Arrange — the same content; only B selects the profile axis.
        var rawComponent = Build();
        var profiledComponent = Build();
        profiledComponent.SetProfileHazardElement(profiledComponent.Graph.GetElements<TransformElement>().Single());
        var raw = new RiskAnalysis(new[] { rawComponent });
        var profiled = new RiskAnalysis(new[] { profiledComponent });

        // Act — flow 30 on the raw axis ≡ stage 15 on the profile axis (T(q) = q/2).
        var rawResult = raw.HazardLevelSensitivity(0, 30d, SensitivityMeasure.PearsonCorrelation, RiskType.Fail)!;
        var profiledResult = profiled.HazardLevelSensitivity(0, 15d, SensitivityMeasure.PearsonCorrelation, RiskType.Fail)!;

        // Assert — bit-identical associations: same draws (seed-inert selector), exact inversion.
        Assert.AreEqual(rawResult.Entries.Count, profiledResult.Entries.Count);
        for (int i = 0; i < rawResult.Entries.Count; i++)
        {
            Assert.AreEqual(rawResult.Entries[i].Value, profiledResult.Entries[i].Value, 0d,
                "The profile-axis tornado must invert onto the raw-axis tornado bit for bit.");
        }
    }

    /// <summary>
    /// Verifies the ranked view: descending absolute association with the walk order stable
    /// under ties.
    /// </summary>
    [TestMethod]
    public async Task Test_SensitivityResults_RankedByMagnitude()
    {
        // Arrange — two inputs of different strength: an uncertain fragility (strong on APF)
        // and an uncertain consequence (inert on APF).
        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = StageFrequency();
        component.AddFailureMode(new FailureMode(null, null, UncertainFragility(), UncertainConsequence("Failure Loss", 300d)));
        component.AddFailureMode(new FailureMode(null, null, null, Consequence("Non-Failure Loss", 60d)));
        var analysis = await RunFull(component);

        // Act
        var result = analysis.MeasureSensitivity(RiskMeasure.TotalProbability, RiskType.Fail, SensitivityMeasure.SensitivityIndex, componentIndex: 0)!;
        var ranked = result.RankedByMagnitude();

        // Assert — the fragility dominates the APF; the consequence draw is noise-level.
        Assert.AreEqual(2, ranked.Count);
        Assert.IsTrue(ranked[0].Label.Contains("Breach Fragility"), "The fragility must rank first on the failure probability.");
        Assert.IsTrue(ranked[0].Value > ranked[1].Value, "The ranking must descend by magnitude.");
        Assert.IsTrue(ranked[1].Value < 0.1d, "The consequence draw must be noise-level on the APF.");
    }
}
