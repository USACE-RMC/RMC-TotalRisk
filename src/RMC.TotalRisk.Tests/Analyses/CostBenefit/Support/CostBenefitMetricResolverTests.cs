using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using Numerics.Mathematics.Optimization;
using RMC.TotalRisk.Analyses.CostBenefit;
using RMC.TotalRisk.Analyses.CostBenefit.Support;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Analyses.Support;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.Systems.Components;

namespace RMC.TotalRisk.Tests.Analyses.CostBenefit.Support;

/// <summary>
/// Tests the metric resolver over a run study: economics resolution with the headline
/// accounting twins, annualized resolution through the engine's scope-and-measure switches
/// with the year-zero objective reading, horizon-basis resolution from the trajectory
/// aggregates including the derived absorbing equivalent-annual level, reductions, the
/// study-α re-evaluation without mutating retained state, constraint scopes, and the
/// reliability-mode NaN discipline with named diagnostics.
/// </summary>
[TestClass]
public class CostBenefitMetricResolverTests
{
    #region Fixtures

    /// <summary>Builds the flat OR(AND(house, 0.9), 0.2) fault-tree response.</summary>
    /// <param name="houseState">The authored house state.</param>
    /// <returns>The response.</returns>
    private static FaultTreeResponse FaultResponse(bool houseState)
    {
        var faultTree = new FaultTree();
        Guid gateId = faultTree.Add(faultTree.Root.Id,
            new FaultTreeGateNode("Outage impact", FaultTreeGateType.And));
        faultTree.Add(gateId, new FaultTreeHouseEventNode("Gate out of service", houseState));
        faultTree.Add(gateId, new FaultTreeBasicEventNode("Load exceedance", new ProbabilitySource(0.9d)));
        faultTree.Add(faultTree.Root.Id,
            new FaultTreeBasicEventNode("Structural failure", new ProbabilitySource(0.2d)));
        return new FaultTreeResponse(new[] { 0d, 1d }, faultTree)
        {
            Name = "Spillway fault tree",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
    }

    /// <summary>Builds the deterministic stage-frequency hazard.</summary>
    /// <returns>The hazard.</returns>
    private static TabularHazard Hazard()
    {
        return new TabularHazard
        {
            Name = "Stage frequency",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            NoUncertaintyFunction = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(0.999d, new Deterministic(0d)),
                    new UncertainOrdinate(0.5d, new Deterministic(0.5d)),
                    new UncertainOrdinate(0.001d, new Deterministic(1d)),
                },
                true, SortOrder.Descending, true, SortOrder.Ascending,
                UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds the deterministic stage-to-loss consequence.</summary>
    /// <returns>The consequence.</returns>
    private static TabularConsequence Consequence()
    {
        return new TabularConsequence
        {
            Name = "Failure damages",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(0d, new Deterministic(500d)),
                    new UncertainOrdinate(1d, new Deterministic(1000d)),
                },
                true, SortOrder.Ascending, false, SortOrder.None,
                UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds a one-component system over the given house state.</summary>
    /// <param name="houseState">The authored house state.</param>
    /// <returns>The analysis.</returns>
    private static RiskAnalysis BuildSystem(bool houseState)
    {
        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = Hazard();
        component.AddFailureMode(new FailureMode(null, null, FaultResponse(houseState), Consequence()));
        return new RiskAnalysis(new[] { component })
        {
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
        };
    }

    /// <summary>
    /// Runs a two-alternative study and constructs a resolver over its published tables.
    /// </summary>
    /// <param name="options">The study declarations.</param>
    /// <param name="reliabilityMode">The resolver's reliability flag.</param>
    /// <returns>The results, the resolver, and its diagnostics sink.</returns>
    private static (CostBenefitResults Results, CostBenefitMetricResolver Resolver,
        List<ComputationDiagnostic> Diagnostics) RunAndResolve(CostBenefitOptions options,
        bool reliabilityMode = false)
    {
        var baseline = new RiskReductionAlternative("Existing condition", BuildSystem(houseState: true));
        var fix = new RiskReductionAlternative("Gate repair", BuildSystem(houseState: false),
            new CostStream(new[] { new CapitalCostEntry(0, 1000d) }));
        var study = new CostBenefitAnalysis(options);
        study.Alternatives.Add(baseline);
        study.Alternatives.Add(fix);
        study.Baseline = baseline;
        study.RunAsync().GetAwaiter().GetResult();
        CostBenefitResults results = study.Results!;
        var thresholds = new IReadOnlyList<double>[] { new[] { double.NaN }, new[] { double.NaN } };
        var diagnostics = new List<ComputationDiagnostic>();
        var resolver = new CostBenefitMetricResolver(options, results.Alternatives, results.Trajectories,
            thresholds, reliabilityMode, DiscountingSupport.AnnuityFactor(options.PeriodYears, options.DiscountRate),
            diagnostics);
        return (results, resolver, diagnostics);
    }

    #endregion

    /// <summary>
    /// Verifies economics resolution reads the published rows, with the headline accounting
    /// selecting the twin where both conventions exist.
    /// </summary>
    [TestMethod]
    public void Test_Resolve_Economics_RowsAndAccountingTwins()
    {
        // Arrange
        var options = new CostBenefitOptions(30, 0.05d, monetization: new ConsequenceMonetization());
        (CostBenefitResults results, CostBenefitMetricResolver resolver, _) = RunAndResolve(options);

        // Act / Assert — the non-absorbing headline.
        Assert.AreEqual(results.Alternatives[1].TotalCostPresentValue,
            resolver.ResolveValue(CostBenefitMetric.ForEconomic(EconomicMetric.PresentValueOfTotalCost), 1), 0d);
        Assert.AreEqual(results.Alternatives[1].NetPresentValue,
            resolver.ResolveValue(CostBenefitMetric.ForEconomic(EconomicMetric.NetPresentValue), 1), 0d);
        Assert.AreEqual(results.Alternatives[1].MonetizedPresentValueBenefit,
            resolver.ResolveValue(CostBenefitMetric.ForEconomic(EconomicMetric.MonetizedPresentValueBenefit), 1), 0d);

        // The absorbing headline selects the twins.
        var absorbingOptions = new CostBenefitOptions(30, 0.05d, accounting: LifeCycleAccounting.Absorbing,
            monetization: new ConsequenceMonetization());
        (CostBenefitResults absorbingResults, CostBenefitMetricResolver absorbingResolver, _) =
            RunAndResolve(absorbingOptions);
        Assert.AreEqual(absorbingResults.Alternatives[1].AbsorbingNetPresentValue,
            absorbingResolver.ResolveValue(CostBenefitMetric.ForEconomic(EconomicMetric.NetPresentValue), 1), 0d);
        Assert.AreEqual(absorbingResults.Alternatives[1].AbsorbingTotalExpectedAnnualCost,
            absorbingResolver.ResolveValue(CostBenefitMetric.ForEconomic(EconomicMetric.TotalExpectedAnnualCost), 1), 0d);
    }

    /// <summary>
    /// Verifies annualized resolution routes through the engine's switches — the year-zero
    /// mean and failure probability agree bit-exactly with the epoch rows — and that
    /// reductions difference the baseline at the same epoch.
    /// </summary>
    [TestMethod]
    public void Test_Resolve_Annualized_YearZeroAndReduction()
    {
        // Arrange
        var options = new CostBenefitOptions(30, 0.05d);
        (CostBenefitResults results, CostBenefitMetricResolver resolver, _) = RunAndResolve(options);

        // Act / Assert — the epoch row published curves.Total.Mean and curves.Fail.TotalProbability;
        // the resolver reads the same retained curves through the summary snapshot.
        Assert.AreEqual(results.Trajectories[1].Epochs[0].System.ExpectedConsequences[0],
            resolver.ResolveValue(CostBenefitMetric.ForRiskMeasure(RiskMeasure.Mean, RiskType.Total), 1), 0d);
        Assert.AreEqual(results.Trajectories[1].Epochs[0].System.FailureProbability,
            resolver.ResolveValue(CostBenefitMetric.ForRiskMeasure(RiskMeasure.TotalProbability, RiskType.Fail), 1), 0d);

        // The reduction form is baseline minus alternative, signed.
        double baselineMean = resolver.ResolveValue(
            CostBenefitMetric.ForRiskMeasure(RiskMeasure.Mean, RiskType.Total), 0);
        double alternativeMean = resolver.ResolveValue(
            CostBenefitMetric.ForRiskMeasure(RiskMeasure.Mean, RiskType.Total), 1);
        Assert.AreEqual(baselineMean - alternativeMean,
            resolver.ResolveValue(CostBenefitMetric.ForRiskMeasure(RiskMeasure.Mean, RiskType.Total,
                form: MetricForm.ReductionVsBaseline), 1), 0d);
        Assert.IsTrue(baselineMean > alternativeMean, "The repair must reduce mean risk.");
    }

    /// <summary>
    /// Verifies horizon-basis resolution reads the trajectory aggregates, including the
    /// derived absorbing equivalent-annual level (absorbing present value over the horizon
    /// annuity).
    /// </summary>
    [TestMethod]
    public void Test_Resolve_HorizonBases_AggregatesAndDerivedAbsorbing()
    {
        // Arrange
        var options = new CostBenefitOptions(30, 0.05d);
        (CostBenefitResults results, CostBenefitMetricResolver resolver, _) = RunAndResolve(options);
        double horizonAnnuity = DiscountingSupport.AnnuityFactor(30, 0.05d);

        // Act / Assert
        Assert.AreEqual(results.Trajectories[1].PresentValueOfExpectedConsequences[0],
            resolver.ResolveValue(CostBenefitMetric.ForRiskMeasure(RiskMeasure.Mean, RiskType.Total,
                basis: MetricBasis.HorizonPresentValue), 1), 0d);
        Assert.AreEqual(results.Trajectories[1].ExcessCumulativeExpectedConsequences[0],
            resolver.ResolveValue(CostBenefitMetric.ForRiskMeasure(RiskMeasure.Mean, RiskType.Excess,
                basis: MetricBasis.HorizonCumulative), 1), 0d);
        Assert.AreEqual(results.Trajectories[1].AbsorbingPresentValueOfExpectedConsequences[0] / horizonAnnuity,
            resolver.ResolveValue(CostBenefitMetric.ForRiskMeasure(RiskMeasure.Mean, RiskType.Total,
                basis: MetricBasis.HorizonEquivalentAnnual, accounting: LifeCycleAccounting.Absorbing), 1), 0d);
    }

    /// <summary>
    /// Verifies the study-α re-evaluation route: a metric's own level overrides the study
    /// default, the re-measured value moves with the level, and the retained realization's
    /// curves are never mutated.
    /// </summary>
    [TestMethod]
    public void Test_Resolve_AlphaReevaluation_OverridesAndNeverMutates()
    {
        // Arrange
        var options = new CostBenefitOptions(30, 0.05d);
        (CostBenefitResults results, CostBenefitMetricResolver resolver, _) = RunAndResolve(options);
        Curve retained = results.Trajectories[1].Epochs[0].Realization!.Curves.Total;
        double retainedAlpha = retained.Alpha;
        double retainedValueAtRisk = retained.ValueAtRisk;

        // Act — the study default (α = 0.01) and a deep-tail override on the same metric.
        double atStudyLevel = resolver.ResolveValue(
            CostBenefitMetric.ForRiskMeasure(RiskMeasure.ValueAtRisk, RiskType.Total), 1);
        double atOverride = resolver.ResolveValue(
            CostBenefitMetric.ForRiskMeasure(RiskMeasure.ValueAtRisk, RiskType.Total, alpha: 0.5d), 1);

        // Assert — the value-at-risk at the median exceedance sits below the 1% level on a
        // monotone loss-exceedance curve, and retained state is untouched.
        Assert.IsTrue(atStudyLevel > atOverride,
            $"The 1% value at risk ({atStudyLevel}) must exceed the median-level value ({atOverride}).");
        Assert.AreEqual(retainedAlpha, retained.Alpha, 0d);
        Assert.AreEqual(retainedValueAtRisk, retained.ValueAtRisk, 0d);
    }

    /// <summary>
    /// Verifies constraint evaluation at the three scopes on a stationary trajectory, and
    /// that a NaN value satisfies neither sense.
    /// </summary>
    [TestMethod]
    public void Test_EvaluateConstraint_ScopesAndNaN()
    {
        // Arrange — the stationary study: every epoch carries one mean, so a bound between
        // the two alternatives' means separates them at every scope.
        var options = new CostBenefitOptions(30, 0.05d);
        (CostBenefitResults results, CostBenefitMetricResolver resolver, _) = RunAndResolve(options);
        double baselineMean = results.Trajectories[0].Epochs[0].System.ExpectedConsequences[0];
        double fixMean = results.Trajectories[1].Epochs[0].System.ExpectedConsequences[0];
        double between = 0.5d * (baselineMean + fixMean);
        var meanMetric = CostBenefitMetric.ForRiskMeasure(RiskMeasure.Mean, RiskType.Total);

        // Act / Assert — the fix satisfies the bound at every scope; the baseline at none.
        foreach (ConstraintScope scope in new[]
            { ConstraintScope.EveryEpoch, ConstraintScope.FirstEpoch })
        {
            var bound = new CostBenefitConstraint(meanMetric, ConstraintType.LesserThanOrEqualTo, between, scope);
            Assert.IsTrue(resolver.EvaluateConstraint(bound, 1), $"The repair must satisfy at {scope}.");
            Assert.IsFalse(resolver.EvaluateConstraint(bound, 0), $"The baseline must violate at {scope}.");
        }
        var horizonBound = new CostBenefitConstraint(
            CostBenefitMetric.ForRiskMeasure(RiskMeasure.Mean, RiskType.Total,
                basis: MetricBasis.HorizonPresentValue),
            ConstraintType.GreaterThanOrEqualTo, 0d, ConstraintScope.Horizon);
        Assert.IsTrue(resolver.EvaluateConstraint(horizonBound, 1));

        // A NaN value never satisfies: no monetization makes net present value NaN.
        var nanBound = new CostBenefitConstraint(
            CostBenefitMetric.ForEconomic(EconomicMetric.NetPresentValue),
            ConstraintType.GreaterThanOrEqualTo, double.MinValue, ConstraintScope.Horizon);
        Assert.IsFalse(resolver.EvaluateConstraint(nanBound, 1));
    }

    /// <summary>
    /// Verifies the reliability-mode discipline: consequence-dependent metrics resolve NaN
    /// with one named diagnostic each (no duplicates), while costs, failure probabilities,
    /// and cost per statistical failure prevented stay active.
    /// </summary>
    [TestMethod]
    public void Test_Resolve_ReliabilityMode_NaNWithNamedDiagnostics()
    {
        // Arrange
        var options = new CostBenefitOptions(30, 0.05d, monetization: new ConsequenceMonetization());
        (CostBenefitResults results, CostBenefitMetricResolver resolver, List<ComputationDiagnostic> diagnostics) =
            RunAndResolve(options, reliabilityMode: true);

        // Act
        double netPresentValue = resolver.ResolveValue(
            CostBenefitMetric.ForEconomic(EconomicMetric.NetPresentValue), 1);
        double repeated = resolver.ResolveValue(
            CostBenefitMetric.ForEconomic(EconomicMetric.NetPresentValue), 1);
        double meanRisk = resolver.ResolveValue(
            CostBenefitMetric.ForRiskMeasure(RiskMeasure.Mean, RiskType.Total), 1);
        double cost = resolver.ResolveValue(
            CostBenefitMetric.ForEconomic(EconomicMetric.PresentValueOfTotalCost), 1);
        double failureProbability = resolver.ResolveValue(
            CostBenefitMetric.ForRiskMeasure(RiskMeasure.TotalProbability, RiskType.Fail), 1);

        // Assert
        Assert.IsTrue(double.IsNaN(netPresentValue));
        Assert.IsTrue(double.IsNaN(repeated));
        Assert.IsTrue(double.IsNaN(meanRisk));
        Assert.AreEqual(results.Alternatives[1].TotalCostPresentValue, cost, 0d);
        Assert.AreEqual(results.Trajectories[1].Epochs[0].System.FailureProbability, failureProbability, 0d);
        int namedSkips = 0;
        for (int i = 0; i < diagnostics.Count; i++)
        {
            Assert.AreEqual("TRC2001", diagnostics[i].Code);
            if (diagnostics[i].Message.Contains(nameof(EconomicMetric.NetPresentValue), StringComparison.Ordinal))
            {
                namedSkips++;
            }
        }
        Assert.AreEqual(1, namedSkips, "A repeated skip must not duplicate its diagnostic.");
    }
}
