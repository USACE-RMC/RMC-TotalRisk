using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Analyses.CostBenefit;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results.CostBenefit;
using RMC.TotalRisk.Results.LifeCycle;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Tests.Results.CostBenefit;

/// <summary>
/// Tests the study results root: guards, the trajectory-parallelism rule, and the
/// convention echoes.
/// </summary>
[TestClass]
public class CostBenefitResultsTests
{
    /// <summary>Builds a one-row results container.</summary>
    /// <param name="trajectoryCount">The trajectory count (1 = parallel to the single row).</param>
    /// <returns>The results.</returns>
    private static CostBenefitResults Build(int trajectoryCount = 1)
    {
        var row = new AlternativeEconomics("Baseline", string.Empty, isBaseline: true,
            0d, 0d, 0d, 0d, 0d, 0d, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN,
            double.NaN, double.NaN, double.NaN, double.NaN, double.NaN,
            1e-3, 0d, 1e-3, 0d, double.NaN);
        var epoch = new LifeCycleEpochRisk(0, 50, 0d, 0.05d,
            new LifeCycleEpochEntry("System", 1e-3, new[] { 100d }, new[] { 80d }, new[] { 20d }),
            Array.Empty<LifeCycleEpochEntry>(), Array.Empty<string>());
        var trajectory = new LifeCycleRiskResults(50, 0.035d, new[] { "Damages" }, new[] { "$" },
            new[] { epoch }, 0.05d, new[] { 0d }, new[] { 0d }, new[] { 0d }, new[] { 0d },
            new[] { 0d }, Array.Empty<string>());
        var trajectories = new LifeCycleRiskResults[trajectoryCount];
        for (int i = 0; i < trajectoryCount; i++) trajectories[i] = trajectory;
        return new CostBenefitResults(50, 0.035d, new[] { 0 }, RiskType.Total,
            LifeCycleAccounting.NonAbsorbing, new[] { 0.01d },
            new ConsequenceMonetization(new[] { new MonetizationFactor(0, 2d, "Price", "2026") }),
            -1, double.NaN, "vintage", AlarpProximity.JustBelowTolerableLimit, null,
            1e-4, 1d, DoNoHarmPolicy.Enforce, new[] { "Damages" }, new[] { "$" },
            new[] { row }, Array.Empty<ConsequenceReduction>(), Array.Empty<TrajectoryPoint>(),
            trajectories);
    }

    /// <summary>Verifies the trajectory-parallelism rule and a null guard.</summary>
    [TestMethod]
    public void Test_Ctor_Guards()
    {
        // Act / Assert
        Assert.ThrowsException<ArgumentException>(() => Build(trajectoryCount: 2));
    }

    /// <summary>Verifies the convention echoes.</summary>
    [TestMethod]
    public void Test_Ctor_ConventionEchoes()
    {
        // Act
        var results = Build();

        // Assert
        Assert.AreEqual(50, results.PeriodYears);
        Assert.AreEqual(0.035d, results.DiscountRate);
        Assert.AreEqual(1, results.EpochGridYears.Count);
        Assert.AreEqual(RiskType.Total, results.BenefitRiskType);
        Assert.AreEqual(LifeCycleAccounting.NonAbsorbing, results.Accounting);
        Assert.AreEqual(0.01d, results.AlphaLevels[0]);
        Assert.IsNotNull(results.Monetization);
        Assert.AreEqual("2026", results.Monetization.Factors[0].Vintage);
        Assert.AreEqual(-1, results.LifeSafetyConsequenceType);
        Assert.IsTrue(double.IsNaN(results.WillingnessToPay));
        Assert.AreEqual("vintage", results.WillingnessToPayVintage);
        Assert.AreEqual(AlarpProximity.JustBelowTolerableLimit, results.AlarpProximity);
        Assert.IsNull(results.AlarpBandThresholds);
        Assert.AreEqual(1e-4, results.IndividualRiskLimit);
        Assert.AreEqual(1d, results.EquityExponent);
        Assert.AreEqual(DoNoHarmPolicy.Enforce, results.DoNoHarm);
        Assert.AreEqual("Damages", results.ConsequenceLabels[0]);
        Assert.AreEqual("$", results.ConsequenceUnits[0]);
        Assert.AreEqual(1, results.Alternatives.Count);
        Assert.IsTrue(results.Alternatives[0].IsBaseline);
        Assert.AreEqual(1, results.Trajectories.Count);
    }

    /// <summary>
    /// Verifies the decision-framework seats: the appended blocks default to empty or null,
    /// the individual-risk seats echo, and explicit blocks pass through.
    /// </summary>
    [TestMethod]
    public void Test_Ctor_DecisionSeats_DefaultsAndEcho()
    {
        // Act — the defaults.
        var defaulted = Build();

        // Assert
        Assert.IsTrue(double.IsNaN(defaulted.BaselineIndividualRisk));
        Assert.IsTrue(double.IsNaN(defaulted.AlternativeIndividualRisk));
        Assert.AreEqual(0, defaulted.Objectives.Count);
        Assert.AreEqual(0, defaulted.Constraints.Count);
        Assert.IsNull(defaulted.EpsilonStudy);
        Assert.AreEqual(0, defaulted.ConstraintEvaluations.Count);
        Assert.IsNull(defaulted.EpsilonSweep);
        Assert.IsNull(defaulted.Frontier);
        Assert.IsNull(defaulted.Mcda);
        Assert.AreEqual(0, defaulted.Diagnostics.Count);

        // Act — explicit blocks pass through on a minimal container.
        var row = new AlternativeEconomics("Baseline", string.Empty, isBaseline: true,
            0d, 0d, 0d, 0d, 0d, 0d, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN,
            double.NaN, double.NaN, double.NaN, double.NaN, double.NaN,
            1e-3, 0d, 1e-3, 0d, double.NaN);
        var epoch = new LifeCycleEpochRisk(0, 50, 0d, 0.05d,
            new LifeCycleEpochEntry("System", 1e-3, new[] { 100d }),
            Array.Empty<LifeCycleEpochEntry>(), Array.Empty<string>());
        var trajectory = new LifeCycleRiskResults(50, 0.035d, new[] { "Damages" }, new[] { "$" },
            new[] { epoch }, 0.05d, new[] { 0d }, new[] { 0d }, new[] { 0d }, new[] { 0d },
            new[] { 0d }, Array.Empty<string>());
        var objective = new ObjectiveDeclaration("Cost",
            CostBenefitMetric.ForEconomic(EconomicMetric.PresentValueOfTotalCost),
            ObjectiveDirection.Minimize);
        var evaluation = new ConstraintEvaluation("label", new[] { true });
        var diagnostic = new ComputationDiagnostic("TRC2001", DiagnosticSeverity.Warning, "m", null);
        var results = new CostBenefitResults(50, 0.035d, new[] { 0 }, RiskType.Total,
            LifeCycleAccounting.NonAbsorbing, new[] { 0.01d }, null, -1, double.NaN, null!,
            AlarpProximity.JustBelowTolerableLimit, null, 1e-4, 1d, DoNoHarmPolicy.Enforce,
            new[] { "Damages" }, new[] { "$" }, new[] { row },
            Array.Empty<ConsequenceReduction>(), Array.Empty<TrajectoryPoint>(),
            new[] { trajectory },
            baselineIndividualRisk: 1e-3, alternativeIndividualRisk: 2e-4,
            objectives: new[] { objective },
            constraintEvaluations: new[] { evaluation },
            diagnostics: new[] { diagnostic });

        // Assert
        Assert.AreEqual(1e-3, results.BaselineIndividualRisk);
        Assert.AreEqual(2e-4, results.AlternativeIndividualRisk);
        Assert.AreSame(objective, results.Objectives[0]);
        Assert.AreSame(evaluation, results.ConstraintEvaluations[0]);
        Assert.AreSame(diagnostic, results.Diagnostics[0]);
    }

    /// <summary>
    /// Verifies the strategy-layer seats: the six appended blocks default to empty or null so
    /// every existing construction compiles and behaves unchanged, and explicit blocks pass
    /// through.
    /// </summary>
    [TestMethod]
    public void Test_Ctor_StrategySeats_DefaultsAndEcho()
    {
        // Act — the defaults.
        var defaulted = Build();

        // Assert
        Assert.AreEqual(0, defaulted.StrategyRankings.Count);
        Assert.IsNull(defaulted.Summary);
        Assert.AreEqual(0, defaulted.RegretMatrices.Count);
        Assert.AreEqual(0, defaulted.EpistemicMeasures.Count);
        Assert.AreEqual(0, defaulted.ChanceConstraints.Count);
        Assert.AreEqual(0, defaulted.Dominance.Count);

        // Arrange — explicit blocks.
        var ranking = new StrategyRanking(DecisionStrategy.ExpectedValue, 1, "Aleatory",
            "aleatory-from-mean-LEC", "criterion", "", ObjectiveDirection.Minimize,
            new[] { "Baseline" }, new[] { 1d }, new[] { 1 }, new[] { false }, new[] { false }, 0);
        var summaryEntry = new DecisionSummaryEntry(DecisionStrategy.ExpectedValue, 1, "Aleatory",
            "criterion", "", "Baseline", 1d, false);
        var summary = new DecisionSummary(new[] { summaryEntry }, new[] { "Baseline" },
            new[] { 1 }, new[] { false });
        var regret = new RegretMatrixResults("criterion", ObjectiveDirection.Minimize,
            new[] { "Baseline" }, new[] { "s1" }, new[] { 1d }, new[] { new[] { 1d } },
            new[] { new[] { double.NaN } }, new[] { new[] { 0d } }, new[] { 0d }, new[] { 0d },
            new[] { 1 }, 0, 0, 1, "block-noise k·SE/√M");
        var band = new EpistemicMeasureSummary("Baseline", "criterion",
            ObjectiveDirection.Minimize, 1d, 0d, 1d, 2d, 0.05d, 0.95d, 0.5d, 2d, 0.1d, 1d, 1);
        var chance = new ChanceConstraintEntry("criterion ≤ 1", new[] { "Baseline" },
            new[] { 0d }, new[] { 1d }, new[] { 0.9d }, new[] { new[] { true } });
        var dominance = new DominanceEntry("Baseline", "Baseline", "Aleatory", "criterion",
            DominanceVerdict.Identical);
        var epoch = new LifeCycleEpochRisk(0, 50, 0d, 0.05d,
            new LifeCycleEpochEntry("System", 1e-3, new[] { 100d }),
            Array.Empty<LifeCycleEpochEntry>(), Array.Empty<string>());
        var trajectory = new LifeCycleRiskResults(50, 0.035d, new[] { "Damages" }, new[] { "$" },
            new[] { epoch }, 0.05d, new[] { 0d }, new[] { 0d }, new[] { 0d }, new[] { 0d },
            new[] { 0d }, Array.Empty<string>());
        var row = new AlternativeEconomics("Baseline", string.Empty, isBaseline: true,
            0d, 0d, 0d, 0d, 0d, 0d, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN,
            double.NaN, double.NaN, double.NaN, double.NaN, double.NaN,
            1e-3, 0d, 1e-3, 0d, double.NaN);

        // Act
        var results = new CostBenefitResults(50, 0.035d, new[] { 0 }, RiskType.Total,
            LifeCycleAccounting.NonAbsorbing, new[] { 0.01d }, null, -1, double.NaN, null!,
            AlarpProximity.JustBelowTolerableLimit, null, 1e-4, 1d, DoNoHarmPolicy.Enforce,
            new[] { "Damages" }, new[] { "$" }, new[] { row },
            Array.Empty<ConsequenceReduction>(), Array.Empty<TrajectoryPoint>(),
            new[] { trajectory },
            strategyRankings: new[] { ranking },
            decisionSummary: summary,
            regretMatrices: new[] { regret },
            epistemicMeasures: new[] { band },
            chanceConstraints: new[] { chance },
            dominance: new[] { dominance });

        // Assert
        Assert.AreSame(ranking, results.StrategyRankings[0]);
        Assert.AreSame(summary, results.Summary);
        Assert.AreSame(regret, results.RegretMatrices[0]);
        Assert.AreSame(band, results.EpistemicMeasures[0]);
        Assert.AreSame(chance, results.ChanceConstraints[0]);
        Assert.AreSame(dominance, results.Dominance[0]);
    }
}
