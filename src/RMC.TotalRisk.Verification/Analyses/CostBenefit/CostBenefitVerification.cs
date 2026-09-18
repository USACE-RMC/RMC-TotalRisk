using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Analyses.CostBenefit;
using RMC.TotalRisk.Analyses.CostBenefit.Support;
using RMC.TotalRisk.Analyses.LifeCycle;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Analyses.Support;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results.CostBenefit;
using RMC.TotalRisk.Results.LifeCycle;
using RMC.TotalRisk.Results.Risk;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.Systems.Components;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Nodes;

namespace RMC.TotalRisk.Verification.Analyses.CostBenefit;

/// <summary>
/// Cost-benefit verification, greenfield: the v1.0 plan-economics parity constants, the
/// cost-stream present-value oracle, the null study's exact zeros, the stationary bridge onto
/// hand closed forms with the configuration-query cross-check, the two-epoch benefit closed
/// form pairing capital and benefit discounting, grid-refinement inertness with deterioration
/// re-aging on the shared grid, the monetization identities with the economic and monetized
/// aggregate split, the Appendix L cost-per-life-saved family with the basis-invariance
/// lemma, the equity-weighted and absorbing ratios against independent survival arithmetic,
/// the thesis ε-constraint and conditional-tail discrimination tables on hand matrices, the
/// tolerable-life-risk template end to end, the frontier and incremental-analysis hand set
/// with the trade-off unification, the multi-criteria arithmetic, the study-level
/// re-measurement against a directly-configured twin, the do-no-harm screen policies, and
/// the reliability-mode study subset — plus the decision-strategy catalog (the Strategies
/// partial): the shared-state regret machinery over genuinely enumerated ensembles with the
/// epistemic-alternative refusal, the classical epistemic rule picks, the exact tail
/// averages, the stochastic-dominance verdicts, the expected-utility closed forms with the
/// partition-to-CVaR pin, the chance-constraint parity, the designed decision-summary
/// disagreement, study author-inertness with run-to-run reproducibility, and the strategy
/// validation sweep.
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// <para>
/// <b>Scenario tables.</b> The flat model drives a five-ordinate deterministic
/// stage-frequency hazard (exceedance 0.999 to 0.001 over stages 60 to 260) through the flat
/// OR(AND(house, 0.375), 0.2) fault tree — failure probability exactly 0.2 with the house
/// event false and 0.5 with it true — with a flat failure consequence of 1000 dollars over a
/// flat non-failure background of 100, so every stream mean is closed-form: Fail = 1000p,
/// Total = 1000p + 100(1 − p), Excess = 900p. The life-safety variant declares a second
/// consequence type with flat failure life loss 0.05 and background 0.005, so the Total life
/// mean is 0.05p + 0.005(1 − p) and the Excess life mean 0.045p. Studies designate the
/// broken (house-true) condition as the baseline and repair it through a year-zero or
/// year-ten plan intervention on the shared system instance.
/// </para>
/// <para>
/// <b>Equivalence contract.</b> Every quantification is mean-only on deterministic fixtures,
/// so the study's trajectory evaluations are bit-reproducible and the per-alternative deltas
/// are exact twin differences. The year-zero epoch of a plan that flips the house event runs
/// the same clone machinery as the configuration-risk query, so their entries must agree with
/// no delta — the cross-anchor between the study's trajectory rows and the shipped
/// configuration query.
/// </para>
/// <para>
/// <b>Oracle mechanics.</b> Oracles are independent re-implementations: plan economics
/// re-derives its stream with a value-array loop, power-form discounting, and the capital
/// recovery factor; cost streams re-price with per-year Math.Pow sums that never touch the
/// annuity forms; benefits recompute from power-form annuity segments and per-year survival
/// loops on the closed-form stream means; monetized aggregates recompose from the published
/// trajectory arrays and declared factors.
/// </para>
/// <para>
/// <b>Tolerances.</b> Recompositions from published values assert bit-exact (no delta), and
/// the null study's deltas are exact zeros. Closed-form stream means carry 1e-9 relative
/// (the integrator's 1e-8 relative discipline is quadrature-exact on flat integrands,
/// leaving rounding), and derived money quantities inherit that bound. Independent
/// discounting arithmetic (power form against the engine's log-space form) carries 1e-12
/// relative. The v1.0 parity constants carry 1e-9 absolute against the recorded reference
/// values and 1e-12 relative against the in-test re-derivation. Grid-refinement inertness is
/// pinned at 1e-13 relative: refining a stationary trajectory's epoch grid re-associates the
/// same telescoping sums, which is exact arithmetic but not bit-identical addition; each
/// derivation is documented on its assert. The Appendix L hand values, the trade-off
/// unification, the hand-matrix selections, and the twin re-measurement assert exact (no
/// delta): they are quotients and differences of shared operands through one code path. The
/// basis-invariance lemma asserts bit-exact under a power-of-two annuity surrogate — scaling
/// by a power of two is exact, so the annuity factor cancels without rounding — and at 1e-15
/// relative under a real annuity factor, where each scaled term rounds once.
/// </para>
/// </remarks>
[TestClass]
public partial class CostBenefitVerification
{
    #region Fixtures

    /// <summary>Builds the deterministic five-ordinate stage-frequency hazard.</summary>
    /// <returns>The hazard.</returns>
    private static TabularHazard StageFrequency()
    {
        return new TabularHazard
        {
            Name = "Stage frequency",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            NoUncertaintyFunction = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(0.999d, new Deterministic(60d)),
                    new UncertainOrdinate(0.5d, new Deterministic(100d)),
                    new UncertainOrdinate(0.1d, new Deterministic(140d)),
                    new UncertainOrdinate(0.01d, new Deterministic(180d)),
                    new UncertainOrdinate(0.001d, new Deterministic(260d)),
                },
                true, SortOrder.Descending, true, SortOrder.Ascending,
                UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>
    /// Builds the flat OR(AND(house, 0.375), 0.2) fault-tree response: failure probability
    /// exactly 0.2 with the house event false and 1 − 0.8 · 0.625 = 0.5 with it true.
    /// </summary>
    /// <param name="houseState">The authored house state.</param>
    /// <param name="houseId">The house-event node id.</param>
    /// <returns>The response.</returns>
    private static FaultTreeResponse FlatFaultResponse(bool houseState, out Guid houseId)
    {
        var faultTree = new FaultTree();
        Guid gateId = faultTree.Add(faultTree.Root.Id,
            new FaultTreeGateNode("Outage impact", FaultTreeGateType.And));
        houseId = faultTree.Add(gateId, new FaultTreeHouseEventNode("Gate out of service", houseState));
        faultTree.Add(gateId, new FaultTreeBasicEventNode("Load exceedance", new ProbabilitySource(0.375d)));
        faultTree.Add(faultTree.Root.Id,
            new FaultTreeBasicEventNode("Structural failure", new ProbabilitySource(0.2d)));
        return new FaultTreeResponse(new[] { 0d, 1d }, faultTree)
        {
            Name = "Spillway fault tree",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
    }

    /// <summary>Builds a flat deterministic consequence over the fixture's stage range.</summary>
    /// <param name="name">The function name.</param>
    /// <param name="value">The flat consequence value.</param>
    /// <param name="label">The consequence-type label.</param>
    /// <param name="unit">The consequence-type unit.</param>
    /// <returns>The consequence.</returns>
    private static TabularConsequence FlatConsequence(string name, double value,
        string label = "Damages", string unit = "$")
    {
        return new TabularConsequence
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = label,
            ConsequenceUnit = unit,
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(40d, new Deterministic(value)),
                    new UncertainOrdinate(280d, new Deterministic(value)),
                },
                true, SortOrder.Ascending, false, SortOrder.None,
                UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>
    /// Builds the flat one-component system: the fault-tree failure mode over the 1000-dollar
    /// flat consequence plus the 100-dollar non-failure background, optionally carrying the
    /// life-loss type (failure 0.05, background 0.005 lives).
    /// </summary>
    /// <param name="houseState">The authored house state.</param>
    /// <param name="houseId">The house-event node id.</param>
    /// <param name="response">The fault-tree response (for intervention addressing).</param>
    /// <param name="includeLifeLoss">True to declare the life-loss consequence type.</param>
    /// <returns>The analysis.</returns>
    private static RiskAnalysis BuildFlatSystem(bool houseState, out Guid houseId,
        out FaultTreeResponse response, bool includeLifeLoss = false)
    {
        response = FlatFaultResponse(houseState, out houseId);
        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = StageFrequency();
        var failureMode = new FailureMode(null, null, response,
            FlatConsequence("Failure damages", 1000d));
        var backgroundMode = new FailureMode(null, null, new NonFailResponse { Name = "Background" },
            FlatConsequence("Background damages", 100d));
        if (includeLifeLoss)
        {
            failureMode.ConsequenceFunctions.Add(
                FlatConsequence("Failure life loss", 0.05d, "Life Loss", "lives"));
            backgroundMode.ConsequenceFunctions.Add(
                FlatConsequence("Background life loss", 0.005d, "Life Loss", "lives"));
        }
        component.AddFailureMode(failureMode);
        component.AddFailureMode(backgroundMode);
        var author = new RiskAnalysis(new[] { component })
        {
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
        };
        if (includeLifeLoss)
        {
            author.AdditionalConsequenceTypes.Add(new ConsequenceTypeDescriptor("Life Loss", "lives"));
        }
        return author;
    }

    /// <summary>The independent power-form annuity factor.</summary>
    /// <param name="years">The horizon in years.</param>
    /// <param name="rate">The annual discount rate.</param>
    /// <returns>The annuity factor.</returns>
    private static double Annuity(int years, double rate)
    {
        return rate > 0d ? (1d - Math.Pow(1d + rate, -years)) / rate : years;
    }

    /// <summary>
    /// Builds a flat one-component system at an arbitrary failure probability and background
    /// level: a flat tabular response over the fixture's stage range drives the 1000-dollar
    /// failure consequence over the given background, optionally with the life-loss type
    /// (failure 0.05, background 0.005 lives).
    /// </summary>
    /// <param name="failureProbability">The flat system response probability.</param>
    /// <param name="backgroundDamages">The flat background damages.</param>
    /// <param name="includeLifeLoss">True to declare the life-loss consequence type.</param>
    /// <returns>The analysis.</returns>
    private static RiskAnalysis BuildCustomFlatSystem(double failureProbability,
        double backgroundDamages, bool includeLifeLoss = false)
    {
        var response = new TabularResponse
        {
            Name = "Flat response",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(40d, new Deterministic(failureProbability)),
                    new UncertainOrdinate(280d, new Deterministic(failureProbability)),
                },
                true, SortOrder.Ascending, false, SortOrder.None,
                UnivariateDistributionType.Deterministic),
        };
        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = StageFrequency();
        var failureMode = new FailureMode(null, null, response,
            FlatConsequence("Failure damages", 1000d));
        var backgroundMode = new FailureMode(null, null, new NonFailResponse { Name = "Background" },
            FlatConsequence("Background damages", backgroundDamages));
        if (includeLifeLoss)
        {
            failureMode.ConsequenceFunctions.Add(
                FlatConsequence("Failure life loss", 0.05d, "Life Loss", "lives"));
            backgroundMode.ConsequenceFunctions.Add(
                FlatConsequence("Background life loss", 0.005d, "Life Loss", "lives"));
        }
        component.AddFailureMode(failureMode);
        component.AddFailureMode(backgroundMode);
        var author = new RiskAnalysis(new[] { component })
        {
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
        };
        if (includeLifeLoss)
        {
            author.AdditionalConsequenceTypes.Add(new ConsequenceTypeDescriptor("Life Loss", "lives"));
        }
        return author;
    }

    /// <summary>
    /// Builds and runs a broken-baseline repair study on one shared flat system: the
    /// designated baseline is the authored house-true condition and the repair alternative
    /// flips the house event through a plan intervention at the given year, carrying the
    /// given costs.
    /// </summary>
    /// <param name="options">The study declarations.</param>
    /// <param name="repairYear">The repair intervention year.</param>
    /// <param name="repairCosts">The repair alternative's costs.</param>
    /// <param name="includeLifeLoss">True to declare the life-loss consequence type.</param>
    /// <returns>The estimated study and the shared system.</returns>
    private static (CostBenefitAnalysis Study, RiskAnalysis System, FaultTreeResponse Response, Guid HouseId)
        RunRepairStudy(CostBenefitOptions options, int repairYear, CostStream? repairCosts,
            bool includeLifeLoss = false)
    {
        RiskAnalysis system = BuildFlatSystem(houseState: true, out Guid houseId,
            out FaultTreeResponse response, includeLifeLoss);
        var repairPlan = new LifeCyclePlan(new[]
        {
            new LifeCycleIntervention(repairYear,
                new[] { new HouseEventState(response.Id, houseId, false) }),
        });
        var baseline = new RiskReductionAlternative("Existing condition", system);
        var repair = new RiskReductionAlternative("Gate repair", system, repairCosts, repairPlan);
        var study = new CostBenefitAnalysis(options);
        study.Alternatives.Add(baseline);
        study.Alternatives.Add(repair);
        study.Baseline = baseline;
        study.RunAsync().GetAwaiter().GetResult();
        return (study, system, response, houseId);
    }

    #endregion

    /// <summary>
    /// The v1.0 plan-economics parity: the recorded reference constants for positive rates
    /// (the ramp-and-plateau case, the silent beyond-horizon truncation, the constant stream,
    /// and the long planning-default ramp), the exact zero-rate average, and an independent
    /// in-test re-derivation of every case with power-form discounting and the capital
    /// recovery factor.
    /// </summary>
    [TestMethod]
    public void Test_PlanEconomics_V10ParityConstants()
    {
        // Arrange — (baseEac, baseYear, futureEac, futureYear, rate, periodYears, reference).
        var cases = new (double BaseEac, int BaseYear, double FutureEac, int FutureYear,
            double Rate, int Period, double Reference)[]
        {
            (100d, 0, 160d, 2, 0.1d, 4, 134.970911441500d),
            (100d, 0, 160d, 10, 0.1d, 4, 108.287007110536d),
            (100d, 0, 160d, 0, 0.1d, 4, 160d),
            (100d, 2026, 200d, 2076, 0.07d, 30, 119.497368419047d),
            (100d, 0, 160d, 2, 0d, 4, 137.5d),
        };

        foreach (var scenario in cases)
        {
            // Act
            double actual = PlanEconomics.EquivalentAnnualConsequences(scenario.BaseEac,
                scenario.BaseYear, scenario.FutureEac, scenario.FutureYear, scenario.Rate,
                scenario.Period);

            // The independent oracle: build the year values, discount with power forms, and
            // apply the capital recovery factor (the zero-rate case averages the values —
            // the exact limit).
            var values = new double[scenario.Period];
            for (int i = 0; i < scenario.Period; i++)
            {
                int year = scenario.BaseYear + i;
                values[i] = year >= scenario.FutureYear
                    ? scenario.FutureEac
                    : scenario.BaseEac + (scenario.FutureEac - scenario.BaseEac)
                        * (year - scenario.BaseYear) / (double)(scenario.FutureYear - scenario.BaseYear);
            }
            double expected;
            if (scenario.Rate > 0d)
            {
                double presentValue = 0d;
                for (int i = 0; i < scenario.Period; i++)
                {
                    presentValue += values[i] * Math.Pow(1d + scenario.Rate, -(i + 1));
                }
                double growth = Math.Pow(1d + scenario.Rate, scenario.Period);
                expected = scenario.Rate * growth / (growth - 1d) * presentValue;
            }
            else
            {
                double total = 0d;
                for (int i = 0; i < scenario.Period; i++) total += values[i];
                expected = total / scenario.Period;
            }

            // Assert — the recorded reference at 1e-9 absolute (twelve recorded digits) and
            // the re-derivation at 1e-12 relative (independent expression order).
            Assert.AreEqual(scenario.Reference, actual, 1e-9d);
            Assert.AreEqual(expected, actual, Math.Abs(expected) * 1e-12d);
        }
    }

    /// <summary>
    /// The cost-stream present-value oracle: dated capital {0: 1000, 10: 500, 20: −200},
    /// operations 10 per year over (0, 50], operating −5 per year over (10, 50], at 3.5
    /// percent over fifty years — re-priced with per-year Math.Pow sums that never touch the
    /// annuity forms — plus the equivalent-annual identity.
    /// </summary>
    [TestMethod]
    public void Test_CostStream_PresentValueOracle()
    {
        // Arrange — the priced repair alternative over the shared flat system.
        var costs = new CostStream(
            new[]
            {
                new CapitalCostEntry(0, 1000d),
                new CapitalCostEntry(10, 500d),
                new CapitalCostEntry(20, -200d),
            },
            new[] { new RecurringCostSegment(0, 10d) },
            new[] { new RecurringCostSegment(10, -5d, 50) });
        (CostBenefitAnalysis study, _, _, _) = RunRepairStudy(
            new CostBenefitOptions(50, 0.035d), repairYear: 0, costs);

        // The independent oracle: every yearly amount discounted with Math.Pow — capital at
        // its own year, recurring amounts one year after their start through their end.
        double rate = 0.035d;
        double expectedCapital = 1000d + 500d * Math.Pow(1d + rate, -10) - 200d * Math.Pow(1d + rate, -20);
        double expectedOperations = 0d;
        for (int year = 1; year <= 50; year++) expectedOperations += 10d * Math.Pow(1d + rate, -year);
        double expectedOperating = 0d;
        for (int year = 11; year <= 50; year++) expectedOperating += -5d * Math.Pow(1d + rate, -year);
        double expectedTotal = expectedCapital + expectedOperations + expectedOperating;

        // Assert — the engine's log-space discounting against the per-year power sums at
        // 1e-12 relative, and the equivalent-annual identity against the independent annuity.
        AlternativeEconomics row = study.Results!.Alternatives[1];
        Assert.AreEqual(expectedCapital, row.CapitalPresentValue, Math.Abs(expectedCapital) * 1e-12d);
        Assert.AreEqual(expectedOperations, row.OperationsAndMaintenancePresentValue,
            Math.Abs(expectedOperations) * 1e-12d);
        Assert.AreEqual(expectedOperating, row.OperatingChangePresentValue,
            Math.Abs(expectedOperating) * 1e-12d);
        Assert.AreEqual(expectedTotal, row.TotalCostPresentValue, Math.Abs(expectedTotal) * 1e-12d);
        Assert.AreEqual(expectedTotal / Annuity(50, rate), row.EquivalentAnnualCost,
            Math.Abs(expectedTotal / Annuity(50, rate)) * 1e-12d);
        Assert.AreEqual(1000d + 500d - 200d + 10d * 50 - 5d * 40, row.CumulativeCost, 1e-12d);
    }

    /// <summary>
    /// The null study: a costless, planless twin of the baseline reuses the baseline's
    /// trajectory evaluation, publishes exact zeros in every reduction row and every signed
    /// reduction column, and reports an undefined benefit-cost ratio at zero cost.
    /// </summary>
    [TestMethod]
    public void Test_NullStudy_ExactZeros()
    {
        // Arrange — the twin shares the baseline's system with no plan and no costs; the
        // declared (empty) monetization map identity-prices the dollar type at one.
        RiskAnalysis system = BuildFlatSystem(houseState: true, out _, out _);
        var baseline = new RiskReductionAlternative("Existing condition", system);
        var twin = new RiskReductionAlternative("Do nothing", system);
        var study = new CostBenefitAnalysis(new CostBenefitOptions(20, 0.05d,
            monetization: new ConsequenceMonetization()));
        study.Alternatives.Add(baseline);
        study.Alternatives.Add(twin);
        study.Baseline = baseline;

        // Act
        study.RunAsync().GetAwaiter().GetResult();

        // Assert — one evaluation serves both rows, and the twin's deltas are exact zeros:
        // the study differences the same trajectory instance against itself.
        CostBenefitResults results = study.Results!;
        Assert.IsTrue(ReferenceEquals(results.Trajectories[0], results.Trajectories[1]));
        int twinRows = 0;
        for (int i = 0; i < results.ConsequenceReductions.Count; i++)
        {
            ConsequenceReduction row = results.ConsequenceReductions[i];
            if (!string.Equals(row.AlternativeName, "Do nothing", StringComparison.Ordinal)) continue;
            twinRows++;
            Assert.AreEqual(0d, row.PresentValueReduction, 0d);
            Assert.AreEqual(0d, row.EquivalentAnnualReduction, 0d);
            Assert.AreEqual(0d, row.CumulativeReduction, 0d);
            Assert.AreEqual(0d, row.AbsorbingPresentValueReduction, 0d);
            Assert.AreEqual(0d, row.AbsorbingCumulativeReduction, 0d);
        }
        Assert.AreEqual(3, twinRows, "One row per stream for the single declared type.");

        AlternativeEconomics twinRow = results.Alternatives[1];
        Assert.AreEqual(0d, twinRow.MonetizedPresentValueBenefit, 0d);
        Assert.AreEqual(0d, twinRow.EconomicPresentValueBenefit, 0d);
        Assert.AreEqual(0d, twinRow.NetPresentValue, 0d);
        Assert.AreEqual(0d, twinRow.AnnualizedFailureProbabilityReduction, 0d);
        Assert.AreEqual(0d, twinRow.YearZeroFailureProbabilityReduction, 0d);
        Assert.IsTrue(double.IsNaN(twinRow.BenefitCostRatio),
            "Cost per unit of nothing spent is undefined, never infinite.");

        // The twin's default objective vector ties the baseline exactly on every member —
        // zero cost, zero benefit, one shared trajectory — so the weak-dominance screen keeps
        // both rows: exact ties are never resolved by fiat.
        Assert.IsNotNull(results.Frontier);
        Assert.IsTrue(results.Frontier!.IsNonDominated[0], "The baseline survives its own tie.");
        Assert.IsTrue(results.Frontier.IsNonDominated[1], "An exact tie keeps both alternatives.");
        Assert.IsFalse(results.Frontier.IsExcludedForNaN[0]);
        Assert.IsFalse(results.Frontier.IsExcludedForNaN[1]);

        // Doing nothing cannot harm: the screen passes with no offending types.
        Assert.IsFalse(twinRow.FailsDoNoHarm);
        Assert.AreEqual(0, twinRow.DoNoHarmOffendingTypes.Count);
        Assert.IsFalse(results.Frontier.IsExcludedFromRecommendation[1]);

        // No life-safety type is declared, so the cost-per-life-saved family is undefined —
        // NaN, never zero and never a division artifact.
        Assert.IsTrue(double.IsNaN(twinRow.CostPerStatisticalLifeSavedUnadjusted));
        Assert.IsTrue(double.IsNaN(twinRow.CostPerStatisticalLifeSavedAdjusted));
        Assert.IsTrue(double.IsNaN(twinRow.EquityWeightedAdjustedCostPerStatisticalLifeSaved));
        Assert.IsTrue(double.IsNaN(twinRow.AbsorbingAdjustedCostPerStatisticalLifeSaved));
        Assert.IsTrue(double.IsNaN(twinRow.LivesSavedEquivalentAnnual));
    }

    /// <summary>
    /// The stationary bridge: the year-zero repair of the broken baseline is stationary on
    /// both sides, so the monetized benefit is the closed form (m_broken − m_fixed)·A(T) on
    /// the flat stream means 550 and 280, the net present value and benefit-cost ratio
    /// follow by hand, and the year-zero epoch entries agree with the shipped
    /// configuration-risk query with no delta — the cross-anchor onto the clone machinery
    /// both queries share.
    /// </summary>
    [TestMethod]
    public void Test_StationaryBridge_ClosedFormAndConfigurationParity()
    {
        // Arrange / Act — repair at year zero for 1000 capital, at five percent over twenty
        // years, with the dollar type identity-monetized.
        var costs = new CostStream(new[] { new CapitalCostEntry(0, 1000d) });
        (CostBenefitAnalysis study, RiskAnalysis system, FaultTreeResponse response, Guid houseId) =
            RunRepairStudy(new CostBenefitOptions(20, 0.05d,
                monetization: new ConsequenceMonetization()), repairYear: 0, costs);

        // Assert — the hand closed form: broken Total mean 1000·0.5 + 100·0.5 = 550, fixed
        // 1000·0.2 + 100·0.8 = 280, so the stationary benefit is 270·A(20) and the
        // percentage tolerances inherit the flat-quadrature 1e-9 relative bound.
        CostBenefitResults results = study.Results!;
        AlternativeEconomics repair = results.Alternatives[1];
        double annuity = Annuity(20, 0.05d);
        double expectedBenefit = 270d * annuity;
        Assert.AreEqual(expectedBenefit, repair.MonetizedPresentValueBenefit,
            Math.Abs(expectedBenefit) * 1e-9d);
        Assert.AreEqual(expectedBenefit - 1000d, repair.NetPresentValue,
            Math.Abs(expectedBenefit) * 1e-9d);
        Assert.AreEqual(expectedBenefit / 1000d, repair.BenefitCostRatio,
            Math.Abs(expectedBenefit / 1000d) * 1e-9d);
        Assert.AreEqual(1000d, repair.TotalCostPresentValue, 0d);

        // The total expected annual cost on the stationary closed forms: the repair carries
        // its equivalent annual cost plus the repaired stream's monetized annual mean, the
        // costless baseline carries its own mean alone, and the TEAC ordering reproduces the
        // net-benefit ordering — TEAC differs from −NPV/A(T) by the shared baseline mean, so
        // minimizing one is maximizing the other (at the 1e-9 flat-quadrature bound).
        AlternativeEconomics baselineRow = results.Alternatives[0];
        Assert.AreEqual(1000d / annuity + 280d, repair.TotalExpectedAnnualCost,
            Math.Abs(1000d / annuity + 280d) * 1e-9d);
        Assert.AreEqual(550d, baselineRow.TotalExpectedAnnualCost, 550d * 1e-9d);
        Assert.AreEqual(baselineRow.TotalExpectedAnnualCost - repair.TotalExpectedAnnualCost,
            repair.NetPresentValue / annuity,
            Math.Abs(repair.NetPresentValue / annuity) * 1e-9d);

        // The year-zero epochs against the configuration query, with no delta: both run the
        // same mean-only clone machinery over the same author state.
        ConfigurationRiskResults configuration = system.MeasureConfigurationRisk(
            new[] { new HouseEventState(response.Id, houseId, false) });
        LifeCycleRiskResults baselineTrajectory = results.Trajectories[0];
        LifeCycleRiskResults repairTrajectory = results.Trajectories[1];
        Assert.AreEqual(configuration.System.BaselineFailureProbability,
            baselineTrajectory.Epochs[0].System.FailureProbability);
        Assert.AreEqual(configuration.System.ConfiguredFailureProbability,
            repairTrajectory.Epochs[0].System.FailureProbability);
        Assert.AreEqual(configuration.System.BaselineExpectedConsequences[0],
            baselineTrajectory.Epochs[0].System.ExpectedConsequences[0]);
        Assert.AreEqual(configuration.System.ConfiguredExpectedConsequences[0],
            repairTrajectory.Epochs[0].System.ExpectedConsequences[0]);
    }

    /// <summary>
    /// The two-epoch benefit closed form: a year-ten repair pairs capital discounted at
    /// (1 + r)^−10 with benefits whose first term is (1 + r)^−11 — the study's end-of-year
    /// exposure convention — so the monetized benefit equals the per-year power-form loop
    /// over years eleven through twenty and the annuity-segment form Δm·(A(20) − A(10)),
    /// and the absorbing benefit equals the differenced per-year survival loops.
    /// </summary>
    [TestMethod]
    public void Test_TwoEpochBenefit_ClosedFormConventionPairing()
    {
        // Arrange / Act — repair at year ten for 1000 capital, at 3.5 percent over twenty
        // years, with the dollar type identity-monetized.
        double rate = 0.035d;
        var costs = new CostStream(new[] { new CapitalCostEntry(10, 1000d) });
        (CostBenefitAnalysis study, _, _, _) = RunRepairStudy(new CostBenefitOptions(20, rate,
            monetization: new ConsequenceMonetization()), repairYear: 10, costs);

        // Assert — the capital side of the pairing: exactly (1 + r)^−10.
        CostBenefitResults results = study.Results!;
        AlternativeEconomics repair = results.Alternatives[1];
        double expectedCapital = 1000d * Math.Pow(1d + rate, -10);
        Assert.AreEqual(expectedCapital, repair.CapitalPresentValue, Math.Abs(expectedCapital) * 1e-12d);

        // The benefit side: the closed forms on the flat means (broken 550, fixed 280) — the
        // per-year loop over years eleven through twenty and the annuity-segment form agree,
        // and the published benefit matches both at the flat-quadrature 1e-9 relative bound.
        double deltaMean = 550d - 280d;
        double perYearBenefit = 0d;
        for (int year = 11; year <= 20; year++)
        {
            perYearBenefit += deltaMean * Math.Pow(1d + rate, -year);
        }
        double segmentBenefit = deltaMean * (Annuity(20, rate) - Annuity(10, rate));
        Assert.AreEqual(perYearBenefit, segmentBenefit, Math.Abs(perYearBenefit) * 1e-12d);
        Assert.AreEqual(perYearBenefit, repair.MonetizedPresentValueBenefit,
            Math.Abs(perYearBenefit) * 1e-9d);

        // The published benefit recomposed from the published trajectories, with no delta.
        Assert.AreEqual(results.Trajectories[0].PresentValueOfExpectedConsequences[0]
            - results.Trajectories[1].PresentValueOfExpectedConsequences[0],
            repair.MonetizedPresentValueBenefit, 0d);

        // The absorbing benefit against differenced per-year survival loops on the closed
        // forms: the broken side survives at 0.5 throughout, the repaired side at 0.5 through
        // year ten and 0.2 after.
        static double AbsorbingPresent(double rate, Func<int, double> probability, Func<int, double> mean)
        {
            double survival = 1d;
            double present = 0d;
            for (int year = 1; year <= 20; year++)
            {
                present += survival * mean(year) * Math.Pow(1d + rate, -year);
                survival *= 1d - probability(year);
            }
            return present;
        }
        double expectedAbsorbingBenefit =
            AbsorbingPresent(rate, _ => 0.5d, _ => 550d)
            - AbsorbingPresent(rate, year => year <= 10 ? 0.5d : 0.2d, year => year <= 10 ? 550d : 280d);
        Assert.AreEqual(expectedAbsorbingBenefit, repair.AbsorbingMonetizedPresentValueBenefit,
            Math.Abs(expectedAbsorbingBenefit) * 1e-9d);
    }

    /// <summary>
    /// Grid alignment: refining a deterioration-free study's epoch grid re-associates the
    /// same telescoping sums, so every economics number agrees at summation rounding; and a
    /// deteriorating baseline re-ages at the repair alternative's plan year because the study
    /// grid is shared, moving its trajectory relative to a standalone coarse evaluation.
    /// </summary>
    [TestMethod]
    public void Test_GridAlignment_TelescopingAndSharedReAging()
    {
        // Arrange / Act — the deterioration-free pair: identical studies, one with extra
        // study-wide evaluation years.
        var costs = new CostStream(new[] { new CapitalCostEntry(0, 1000d) });
        (CostBenefitAnalysis coarse, _, _, _) = RunRepairStudy(new CostBenefitOptions(20, 0.05d,
            monetization: new ConsequenceMonetization()), repairYear: 0, costs);
        (CostBenefitAnalysis fine, _, _, _) = RunRepairStudy(new CostBenefitOptions(20, 0.05d,
            evaluationYears: new[] { 3, 7, 13 },
            monetization: new ConsequenceMonetization()), repairYear: 0, costs);

        // Assert — the refined grid quantifies identical stationary epochs, so the
        // aggregates re-associate the same telescoping sums: exact arithmetic, but not
        // bit-identical floating-point addition. The measured coarse-versus-fine difference
        // is a few units in the last place (4.6e-13 absolute on a net present value of
        // magnitude 115, about 4e-15 relative), so 1e-13 relative pins the identity with two
        // orders of headroom while refusing any real movement.
        AlternativeEconomics coarseRow = coarse.Results!.Alternatives[1];
        AlternativeEconomics fineRow = fine.Results!.Alternatives[1];
        Assert.AreEqual(4, fine.Results.EpochGridYears.Count);
        Assert.AreEqual(coarseRow.MonetizedPresentValueBenefit, fineRow.MonetizedPresentValueBenefit,
            Math.Abs(coarseRow.MonetizedPresentValueBenefit) * 1e-13d);
        Assert.AreEqual(coarseRow.NetPresentValue, fineRow.NetPresentValue,
            Math.Abs(coarseRow.NetPresentValue) * 1e-13d);
        Assert.AreEqual(coarseRow.AbsorbingMonetizedPresentValueBenefit,
            fineRow.AbsorbingMonetizedPresentValueBenefit,
            Math.Abs(coarseRow.AbsorbingMonetizedPresentValueBenefit) * 1e-13d);
        Assert.AreEqual(coarseRow.AnnualizedFailureProbability, fineRow.AnnualizedFailureProbability,
            Math.Abs(coarseRow.AnnualizedFailureProbability) * 1e-13d);

        // The shared-grid re-aging pin: a deteriorating baseline plus a planned repair
        // alternative on a second system — the baseline trajectory re-ages at the repair's
        // plan year purely because the grid is shared.
        var agingComponent = new SystemComponent { Name = "Levee" };
        agingComponent.HazardFunction = StageFrequency();
        agingComponent.AddFailureMode(new FailureMode(null, null, new DeterioratingResponse
        {
            Name = "Aging fragility",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            BaseResponse = new TabularResponse
            {
                Name = "Fragility",
                SpecifiedHazard = "Stage",
                HazardUnit = "ft",
                UncertainOrderedPairedData = new UncertainOrderedPairedData(
                    new[]
                    {
                        new UncertainOrdinate(60d, new Deterministic(0d)),
                        new UncertainOrdinate(260d, new Deterministic(0.5d)),
                    },
                    true, SortOrder.Ascending, false, SortOrder.None,
                    UnivariateDistributionType.Deterministic),
            },
            DeteriorationLaw = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(0d, new Deterministic(0d)),
                    new UncertainOrdinate(20d, new Deterministic(40d)),
                },
                true, SortOrder.Ascending, false, SortOrder.None,
                UnivariateDistributionType.Deterministic),
        }, FlatConsequence("Failure damages", 1000d)));
        var agingSystem = new RiskAnalysis(new[] { agingComponent })
        {
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
        };
        RiskAnalysis repairSystem = BuildFlatSystem(houseState: true, out Guid repairHouseId,
            out FaultTreeResponse repairResponse);
        var agingBaseline = new RiskReductionAlternative("Aging levee", agingSystem);
        var plannedRepair = new RiskReductionAlternative("Year-ten repair", repairSystem,
            costs, new LifeCyclePlan(new[]
            {
                new LifeCycleIntervention(10,
                    new[] { new HouseEventState(repairResponse.Id, repairHouseId, false) }),
            }));
        var sharedGrid = new CostBenefitAnalysis(new CostBenefitOptions(20, 0.05d));
        sharedGrid.Alternatives.Add(agingBaseline);
        sharedGrid.Alternatives.Add(plannedRepair);
        sharedGrid.Baseline = agingBaseline;
        sharedGrid.RunAsync().GetAwaiter().GetResult();

        LifeCycleRiskResults agingTrajectory = sharedGrid.Results!.Trajectories[0];
        LifeCycleRiskResults standalone = agingSystem.MeasureLifeCycleRisk(new LifeCycleDefinition(20, 0.05d));

        // The baseline splits at the repair's plan year and ages across it, while the
        // standalone coarse evaluation holds age zero throughout — so the shared grid moves
        // the baseline's aggregate strictly upward.
        Assert.AreEqual(2, agingTrajectory.Epochs.Count);
        Assert.AreEqual(10d, agingTrajectory.Epochs[1].EvaluationAge);
        Assert.IsTrue(agingTrajectory.Epochs[1].System.FailureProbability
            > agingTrajectory.Epochs[0].System.FailureProbability,
            "Deterioration must raise the re-aged epoch's failure probability.");
        Assert.AreEqual(1, standalone.Epochs.Count);
        Assert.IsTrue(agingTrajectory.CumulativeExpectedConsequences[0]
            > standalone.CumulativeExpectedConsequences[0],
            "Re-aging on the shared grid must move the deteriorating baseline's aggregate.");
    }

    /// <summary>
    /// The monetization identities: with only the life-loss type priced, the monetized
    /// benefit is exactly the factor times the discounted lives saved and the economic
    /// aggregate is exactly zero; with the dollar type identity-priced alongside, the
    /// monetized aggregate recomposes from the published trajectory arrays with no delta;
    /// and an empty monetized set publishes undefined monetary aggregates with the
    /// advisory warning.
    /// </summary>
    [TestMethod]
    public void Test_Monetization_SplitIdentities()
    {
        // Arrange / Act — life-only monetization: the monetary unit differs from the dollar
        // type's unit, so only the declared life factor prices anything.
        double lifeValue = 7.5e6d;
        var costs = new CostStream(new[] { new CapitalCostEntry(0, 1000d) });
        var lifeOnlyOptions = new CostBenefitOptions(20, 0.05d,
            monetization: new ConsequenceMonetization(
                new[] { new MonetizationFactor(1, lifeValue, "Statistical life", "2026") }, "FY26$"),
            lifeSafetyConsequenceType: 1);
        (CostBenefitAnalysis lifeOnly, _, _, _) = RunRepairStudy(lifeOnlyOptions, repairYear: 0,
            costs, includeLifeLoss: true);

        // Assert — the monetized benefit is the factor times the discounted lives saved,
        // recomposed from the published trajectories with no delta; the economic aggregate
        // (which always excludes the life-safety type) is exactly zero because nothing else
        // is priced.
        CostBenefitResults lifeResults = lifeOnly.Results!;
        AlternativeEconomics lifeRepair = lifeResults.Alternatives[1];
        double discountedLivesSaved = lifeResults.Trajectories[0].PresentValueOfExpectedConsequences[1]
            - lifeResults.Trajectories[1].PresentValueOfExpectedConsequences[1];
        Assert.AreEqual(lifeValue * discountedLivesSaved, lifeRepair.MonetizedPresentValueBenefit, 0d);
        Assert.AreEqual(0d, lifeRepair.EconomicPresentValueBenefit, 0d);
        Assert.AreEqual(lifeValue * discountedLivesSaved - 1000d, lifeRepair.NetPresentValue, 0d);

        // The closed form beneath it: the Total life mean is 0.05p + 0.005(1 − p), so the
        // stationary lives-saved stream is (0.0275 − 0.014)·A(20) at the flat-quadrature
        // 1e-9 relative bound; the lives-saved column reads the Excess stream instead —
        // 0.045·(0.5 − 0.2) = 0.0135 equivalent-annual lives.
        double expectedLives = (0.0275d - 0.014d) * Annuity(20, 0.05d);
        Assert.AreEqual(expectedLives, discountedLivesSaved, Math.Abs(expectedLives) * 1e-9d);
        Assert.AreEqual(0.0135d, lifeRepair.LivesSavedEquivalentAnnual, 0.0135d * 1e-9d);

        // The split scenario: the dollar type identity-prices at one beside the life factor,
        // and the monetized aggregate recomposes from both published reductions exactly.
        var splitOptions = new CostBenefitOptions(20, 0.05d,
            monetization: new ConsequenceMonetization(
                new[] { new MonetizationFactor(1, lifeValue, "Statistical life", "2026") }),
            lifeSafetyConsequenceType: 1);
        (CostBenefitAnalysis split, _, _, _) = RunRepairStudy(splitOptions, repairYear: 0,
            costs, includeLifeLoss: true);
        CostBenefitResults splitResults = split.Results!;
        AlternativeEconomics splitRepair = splitResults.Alternatives[1];
        double dollarReduction = splitResults.Trajectories[0].PresentValueOfExpectedConsequences[0]
            - splitResults.Trajectories[1].PresentValueOfExpectedConsequences[0];
        double lifeReduction = splitResults.Trajectories[0].PresentValueOfExpectedConsequences[1]
            - splitResults.Trajectories[1].PresentValueOfExpectedConsequences[1];
        Assert.AreEqual(1d * dollarReduction + lifeValue * lifeReduction,
            splitRepair.MonetizedPresentValueBenefit, 0d);
        Assert.AreEqual(1d * dollarReduction, splitRepair.EconomicPresentValueBenefit, 0d);

        // The empty monetized set: a declared map pricing nothing publishes undefined
        // monetary aggregates and the advisory warning.
        var emptyOptions = new CostBenefitOptions(20, 0.05d,
            monetization: new ConsequenceMonetization(null, "FY26$"));
        (CostBenefitAnalysis empty, _, _, _) = RunRepairStudy(emptyOptions, repairYear: 0, costs);
        Assert.IsTrue(double.IsNaN(empty.Results!.Alternatives[1].MonetizedPresentValueBenefit));
        Assert.IsTrue(double.IsNaN(empty.Results.Alternatives[1].NetPresentValue));
        bool warned = false;
        (_, List<string> messages) = empty.Validate();
        for (int i = 0; i < messages.Count; i++)
        {
            if (messages[i].StartsWith(
                "Warning: No consequence types are monetized", StringComparison.Ordinal))
            {
                warned = true;
            }
        }
        Assert.IsTrue(warned, "An empty monetized set must advise, not silently publish NaN.");
    }

    /// <summary>
    /// The Appendix L cost-per-life-saved family at the hand values — annualized cost 120,
    /// economic reduction 30, operating reduction 10, life-loss reduction 0.004 — with the
    /// unadjusted ratio exactly 30,000, the adjusted ratio exactly 20,000, the
    /// negative-numerator proviso clamping to exactly zero, the undefined-denominator NaN,
    /// and the basis-invariance lemma: present-value-form and equivalent-annual-form
    /// quotients agree bit-exactly under a power-of-two annuity surrogate (scaling is exact)
    /// and to rounding under a real annuity factor.
    /// </summary>
    [TestMethod]
    public void Test_ApplLFamily_HandValuesAndBasisInvariance()
    {
        // Act / Assert — the hand values: 120 / 0.004 and (120 − 30 − 10) / 0.004 exactly.
        Assert.AreEqual(30000d, CostBenefitFormulary.CostPerLifeSavedUnadjusted(120d, 0.004d), 0d);
        Assert.AreEqual(20000d, CostBenefitFormulary.CostPerLifeSavedAdjusted(120d, 30d, 10d, 0.004d), 0d);

        // The proviso: a negative numerator is taken as exactly zero, never negative.
        Assert.AreEqual(0d, CostBenefitFormulary.CostPerLifeSavedAdjusted(120d, 100d, 30d, 0.004d), 0d);

        // The denominator rule: no lives saved makes the ratio undefined.
        Assert.IsTrue(double.IsNaN(CostBenefitFormulary.CostPerLifeSavedUnadjusted(120d, 0d)));
        Assert.IsTrue(double.IsNaN(CostBenefitFormulary.CostPerLifeSavedAdjusted(120d, 30d, 10d, -0.004d)));

        // The basis-invariance lemma, exact mechanism: scaling every term by a power of two
        // is exact floating-point arithmetic (an exponent shift), so the annuity factor
        // cancels bit-for-bit and the clamp commutes.
        double surrogate = 16d;
        Assert.AreEqual(CostBenefitFormulary.CostPerLifeSavedUnadjusted(120d, 0.004d),
            CostBenefitFormulary.CostPerLifeSavedUnadjusted(120d * surrogate, 0.004d * surrogate), 0d);
        Assert.AreEqual(CostBenefitFormulary.CostPerLifeSavedAdjusted(120d, 30d, 10d, 0.004d),
            CostBenefitFormulary.CostPerLifeSavedAdjusted(120d * surrogate, 30d * surrogate,
                10d * surrogate, 0.004d * surrogate), 0d);
        Assert.AreEqual(0d, CostBenefitFormulary.CostPerLifeSavedAdjusted(120d * surrogate,
            100d * surrogate, 30d * surrogate, 0.004d * surrogate), 0d,
            "The clamp commutes with positive scaling.");

        // Under a real annuity factor each scaled term rounds once, so the quotients agree
        // to a few units in the last place — pinned at 1e-15 relative.
        double annuity = Annuity(50, 0.035d);
        double equivalentAnnualForm = CostBenefitFormulary.CostPerLifeSavedAdjusted(120d, 30d, 10d, 0.004d);
        double presentValueForm = CostBenefitFormulary.CostPerLifeSavedAdjusted(120d * annuity,
            30d * annuity, 10d * annuity, 0.004d * annuity);
        Assert.AreEqual(equivalentAnnualForm, presentValueForm, equivalentAnnualForm * 1e-15d);
    }

    /// <summary>
    /// The equity-weighted, failure-prevention, and absorbing adjusted ratios: the
    /// individual-risk floor engaging on one side only with the exponent sweep against
    /// independent power-form arithmetic; the survival-equivalent annualized probability and
    /// its reduction against an independent survival computation behind the year-ten repair
    /// study; the published failure-prevention and equity-weighted columns recomposing
    /// bit-exactly from published values; and the absorbing adjusted ratio against
    /// independent per-year survival loops on the closed-form stream means.
    /// </summary>
    [TestMethod]
    public void Test_EwacslsCsfpAacsls_Arithmetic()
    {
        // Act / Assert — the formulary arithmetic: baseline risk 5e-4, alternative 5e-5,
        // floor 1e-4, so the floor engages on the alternative side only and the weight is
        // (5e-4 / 1e-4)^n = 5^n; the exponent sweep re-derives with independent Math.Pow.
        double adjusted = 20000d;
        foreach (double exponent in new[] { 0.5d, 1d, 2d })
        {
            double expectedWeight = Math.Pow(5e-4 / 1e-4, exponent);
            Assert.AreEqual(adjusted / expectedWeight,
                CostBenefitFormulary.EquityWeightedCostPerLifeSaved(adjusted, 5e-4, 5e-5, 1e-4, exponent),
                Math.Abs(adjusted / expectedWeight) * 1e-12d);
        }

        // Arrange / Act — the year-ten repair with the life-loss type: the dollar type
        // identity-monetized, life safety declared at position one.
        double rate = 0.035d;
        var costs = new CostStream(new[] { new CapitalCostEntry(10, 1000d) });
        (CostBenefitAnalysis study, _, _, _) = RunRepairStudy(new CostBenefitOptions(20, rate,
            monetization: new ConsequenceMonetization(), lifeSafetyConsequenceType: 1),
            repairYear: 10, costs, includeLifeLoss: true);
        CostBenefitResults results = study.Results!;
        AlternativeEconomics baselineRow = results.Alternatives[0];
        AlternativeEconomics repair = results.Alternatives[1];

        // The independent survival computation: a constant annual probability p has
        // survival-equivalent annualized probability exactly p, and the repaired horizon
        // probability composes ten broken years with ten repaired years.
        double brokenEquivalent = 1d - Math.Pow(Math.Pow(1d - 0.5d, 20), 1d / 20d);
        double repairedSurvival = Math.Pow(1d - 0.5d, 10) * Math.Pow(1d - 0.2d, 10);
        double repairedEquivalent = 1d - Math.Pow(repairedSurvival, 1d / 20d);

        // The published probabilities inherit the flat-quadrature bound (the annual failure
        // probability behind the survival accumulation is itself integrated), so the
        // independent survival arithmetic pins at 1e-9 relative, not the pure-discounting
        // 1e-12 grade.
        Assert.AreEqual(brokenEquivalent, baselineRow.AnnualizedFailureProbability,
            Math.Abs(brokenEquivalent) * 1e-9d);
        Assert.AreEqual(brokenEquivalent - repairedEquivalent,
            repair.AnnualizedFailureProbabilityReduction,
            Math.Abs(brokenEquivalent - repairedEquivalent) * 1e-9d);

        // The failure-prevention column recomposes bit-exactly from published values through
        // the shared discounting authority.
        double horizonAnnuity = DiscountingSupport.AnnuityFactor(20, rate);
        double annualizedCost = (repair.CapitalPresentValue
            + repair.OperationsAndMaintenancePresentValue) / horizonAnnuity;
        Assert.AreEqual(annualizedCost / repair.AnnualizedFailureProbabilityReduction,
            repair.CostPerStatisticalFailurePrevented, 0d);

        // The equity-weighted column recomposes bit-exactly from the published adjusted
        // ratio and the echoed per-side individual risks (the survival-equivalent proxies).
        Assert.IsTrue(repair.IndividualRiskIsProxy);
        double weight = Math.Pow(Math.Max(repair.BaselineIndividualRiskUsed, 1e-4)
            / Math.Max(repair.AlternativeIndividualRiskUsed, 1e-4), 1d);
        Assert.AreEqual(repair.CostPerStatisticalLifeSavedAdjusted / weight,
            repair.EquityWeightedAdjustedCostPerStatisticalLifeSaved, 0d);

        // The absorbing adjusted ratio against independent per-year survival loops: the
        // economic (dollar) absorbing reduction discounts, the cumulative life-loss
        // reduction does not, and the cost base is the capital present value alone.
        static (double Present, double Cumulative) AbsorbingLoops(double rate,
            Func<int, double> probability, Func<int, double> presentMean, Func<int, double> cumulativeMean)
        {
            double survival = 1d;
            double present = 0d;
            double cumulative = 0d;
            for (int year = 1; year <= 20; year++)
            {
                present += survival * presentMean(year) * Math.Pow(1d + rate, -year);
                cumulative += survival * cumulativeMean(year);
                survival *= 1d - probability(year);
            }
            return (present, cumulative);
        }
        (double brokenDollars, _) = AbsorbingLoops(rate, _ => 0.5d, _ => 550d, _ => 0d);
        (double repairedDollars, _) = AbsorbingLoops(rate,
            year => year <= 10 ? 0.5d : 0.2d, year => year <= 10 ? 550d : 280d, _ => 0d);
        (_, double brokenLives) = AbsorbingLoops(rate, _ => 0.5d, _ => 0d, _ => 0.045d * 0.5d);
        (_, double repairedLives) = AbsorbingLoops(rate,
            year => year <= 10 ? 0.5d : 0.2d, _ => 0d,
            year => year <= 10 ? 0.045d * 0.5d : 0.045d * 0.2d);
        double expectedNumerator = Math.Max(0d,
            repair.CapitalPresentValue - (brokenDollars - repairedDollars));
        double expectedRatio = expectedNumerator / (brokenLives - repairedLives);
        Assert.AreEqual(expectedRatio, repair.AbsorbingAdjustedCostPerStatisticalLifeSaved,
            Math.Abs(expectedRatio) * 1e-9d);
    }

    /// <summary>
    /// The discrete ε-constraint study on the closed-form Haimes example — minimize
    /// f₁ = (x₁ − 2)² + (x₂ − 4)² + 5 subject to f₂ = (x₁ − 6)² + (x₂ − 10)² + 6 ≤ ε — whose
    /// noninferior frontier is analytic: x₁(λ) = (2 + 6λ)/(1 + λ), x₂(λ) = (4 + 10λ)/(1 + λ)
    /// with the multiplier λ = √(52/(ε − 6)) − 1. Ten alternatives sit at the analytic points
    /// of the uniform ε grid from 6 to 58; the sweep reproduces the per-ε selections and the
    /// noninferior set, the adjacent trade-off ratios pin exactly against the same-order
    /// secant recomputation, and each secant lies between the analytic tangent multipliers at
    /// its endpoints (the frontier is convex). At ε = 13.31 the analytic multiplier is
    /// 1.667 — the recorded upstream augmented-Lagrange anchor (1.67) for the same example.
    /// </summary>
    [TestMethod]
    public void Test_EpsilonSweep_ThesisTableB1()
    {
        // Arrange — the ten analytic noninferior points on the uniform ε grid. The ε = 6
        // point is the constraint set's own minimizer x = (6, 10) with f₁ = 57 (the λ → ∞
        // limit); the rest evaluate the closed forms at λ(ε).
        var epsilon = new double[10];
        var primary = new double[10];
        var swept = new double[10];
        var multipliers = new double[10];
        var names = new string[10];
        for (int k = 0; k < 10; k++)
        {
            epsilon[k] = 6d + k * 52d / 9d;
            names[k] = $"A{k}";
            if (k == 0)
            {
                primary[0] = 57d;
                swept[0] = 6d;
                multipliers[0] = double.PositiveInfinity;
                continue;
            }
            double lambda = Math.Sqrt(52d / (epsilon[k] - 6d)) - 1d;
            multipliers[k] = lambda;
            double onePlus = 1d + lambda;
            primary[k] = 52d * lambda * lambda / (onePlus * onePlus) + 5d;
            swept[k] = 52d / (onePlus * onePlus) + 6d;
        }
        var allTrue = new bool[10];
        for (int i = 0; i < 10; i++) allTrue[i] = true;

        // Act — the grid is the points' own swept values: algebraically the uniform ε grid
        // (the square-root construction inverts exactly in real arithmetic), and passing the
        // computed values keeps each bound admitting its own point to the last bit.
        EpsilonSweepResults results = EpsilonSweepEngine.Run("f1", ObjectiveDirection.Minimize,
            "f2", names, primary, swept, allTrue, allTrue, swept, gridPoints: 10,
            new List<ComputationDiagnostic>());

        // Assert — the per-ε selections walk the ten analytic points in order, and the
        // noninferior set is the whole frontier.
        for (int k = 0; k < 10; k++)
        {
            Assert.AreEqual(names[k], results.Entries[k].SelectedAlternative,
                $"The bound ε = {epsilon[k]} admits exactly the first {k + 1} points.");
        }
        CollectionAssert.AreEqual(names, (System.Collections.ICollection)results.NoninferiorAlternatives);

        // The adjacent trade-off ratios pin exactly against the same-order secant, and each
        // secant lies between the analytic tangent multipliers at its endpoints.
        for (int k = 1; k < 10; k++)
        {
            double secant = -(primary[k] - primary[k - 1]) / (swept[k] - swept[k - 1]);
            Assert.AreEqual(secant, results.Entries[k].TradeOffRatio, 0d);
            Assert.IsTrue(results.Entries[k].TradeOffRatio >= multipliers[k],
                "A convex frontier's secant is at least the tangent at its looser endpoint.");
            Assert.IsTrue(results.Entries[k].TradeOffRatio <= multipliers[k - 1],
                "A convex frontier's secant is at most the tangent at its tighter endpoint.");
        }

        // The recorded upstream anchor: the analytic multiplier at ε = 13.31.
        Assert.AreEqual(1.667d, Math.Sqrt(52d / (13.31d - 6d)) - 1d, 0.001d);
    }

    /// <summary>
    /// The conditional-tail discrimination table: four options with identical expected life
    /// loss (10 lives per year) and conditional tail values {30, 67, 149, 577} on a
    /// descending cost axis {40, 30, 20, 10}. Expected value alone cannot discriminate (every
    /// option survives its screen); adding the tail axis collapses the expected-value screen
    /// to the lowest-tail option; the cost-tail pair is a full frontier; and the tolerable
    /// tail limit of 100 admits options one and two, with least cost selecting option two.
    /// </summary>
    [TestMethod]
    public void Test_CvarDiscrimination_ThesisTable21()
    {
        // Arrange — the table. The cost axis is the test's modeling seat for the thesis
        // reading: safer tails cost more, so the tolerable-limit filter leaves a real choice.
        var names = new[] { "Option 1", "Option 2", "Option 3", "Option 4" };
        var expectedValues = new[] { 10d, 10d, 10d, 10d };
        var tailValues = new[] { 30d, 67d, 149d, 577d };
        var costs = new[] { 40d, 30d, 20d, 10d };
        var allTrue = new[] { true, true, true, true };

        // Act / Assert — expected value alone keeps every option: exact ties never resolve.
        bool[] expectedValueScreen = ParetoFrontierEngine.NonDominated(
            new[] { new[] { 10d }, new[] { 10d }, new[] { 10d }, new[] { 10d } },
            new[] { ObjectiveDirection.Minimize }, new bool[4]);
        for (int i = 0; i < 4; i++)
        {
            Assert.IsTrue(expectedValueScreen[i], "Identical expected values tie every option.");
        }

        // Adding the tail axis discriminates: equal means with a strictly lower tail weakly
        // dominate, leaving option one alone.
        bool[] meanTailScreen = ParetoFrontierEngine.NonDominated(
            new[]
            {
                new[] { expectedValues[0], tailValues[0] },
                new[] { expectedValues[1], tailValues[1] },
                new[] { expectedValues[2], tailValues[2] },
                new[] { expectedValues[3], tailValues[3] },
            },
            new[] { ObjectiveDirection.Minimize, ObjectiveDirection.Minimize }, new bool[4]);
        Assert.IsTrue(meanTailScreen[0]);
        Assert.IsFalse(meanTailScreen[1]);
        Assert.IsFalse(meanTailScreen[2]);
        Assert.IsFalse(meanTailScreen[3]);

        // The cost-tail pair is a full frontier: cost falls exactly as the tail grows.
        bool[] costTailScreen = ParetoFrontierEngine.NonDominated(
            new[]
            {
                new[] { costs[0], tailValues[0] },
                new[] { costs[1], tailValues[1] },
                new[] { costs[2], tailValues[2] },
                new[] { costs[3], tailValues[3] },
            },
            new[] { ObjectiveDirection.Minimize, ObjectiveDirection.Minimize }, new bool[4]);
        for (int i = 0; i < 4; i++)
        {
            Assert.IsTrue(costTailScreen[i], "The cost-tail trade is a complete frontier.");
        }

        // The tolerable-limit filter: a tail bound of 100 admits options one and two, and
        // least cost selects option two.
        EpsilonSweepResults selection = EpsilonSweepEngine.Run("Cost", ObjectiveDirection.Minimize,
            "Conditional tail", names, costs, tailValues, allTrue, allTrue, new[] { 100d },
            gridPoints: 10, new List<ComputationDiagnostic>());
        Assert.AreEqual(2, selection.Entries[0].FeasibleCount);
        Assert.AreEqual("Option 2", selection.Entries[0].SelectedAlternative);
        Assert.IsTrue(selection.Entries[0].EpsilonBinding,
            "The bound excludes the unconstrained least-cost option.");
    }

    /// <summary>
    /// The tolerable-life-risk template end to end: three alternatives (the broken baseline,
    /// a year-zero gate repair, and a low-probability rebuild on its own system) under
    /// minimize-total-expected-annual-cost with the tolerable-risk-guideline filter at every
    /// epoch. The guideline removes the baseline by name; the explicit grid's tight bound is
    /// infeasible for everyone; and the admissible bound selects the repair — the cheaper of
    /// the two compliant alternatives on the hand closed forms, whose conditional tails
    /// coincide at the flat 0.045 up to quadrature rounding.
    /// </summary>
    [TestMethod]
    public void Test_TolerableLifeRiskTemplate_EndToEnd()
    {
        // Arrange — the shared broken system with the repair plan, plus the rebuild's own
        // system at failure probability 0.05; life loss declared at position one; the dollar
        // type identity-monetized; the guideline 0.01 lives per year (the broken condition's
        // 0.045 · 0.5 = 0.0225 violates it, the repair's 0.009 and the rebuild's 0.00225
        // comply).
        RiskAnalysis sharedSystem = BuildFlatSystem(houseState: true, out Guid houseId,
            out FaultTreeResponse response, includeLifeLoss: true);
        var repairPlan = new LifeCyclePlan(new[]
        {
            new LifeCycleIntervention(0, new[] { new HouseEventState(response.Id, houseId, false) }),
        });
        var baseline = new RiskReductionAlternative("Existing condition", sharedSystem);
        var repair = new RiskReductionAlternative("Gate repair", sharedSystem,
            new CostStream(new[] { new CapitalCostEntry(0, 1000d) }), repairPlan);
        var rebuild = new RiskReductionAlternative("Major rebuild",
            BuildCustomFlatSystem(0.05d, 100d, includeLifeLoss: true),
            new CostStream(new[] { new CapitalCostEntry(0, 5000d) }));
        var template = EpsilonConstraintStudy.CreateTolerableLifeRiskStudy(
            lifeSafetyConsequenceType: 1, tolerableRiskGuideline: 0.01d,
            epsilonGrid: new[] { 0.02d, 0.05d });
        var study = new CostBenefitAnalysis(new CostBenefitOptions(20, 0.05d,
            monetization: new ConsequenceMonetization(), lifeSafetyConsequenceType: 1,
            epsilonStudy: template));
        study.Alternatives.Add(baseline);
        study.Alternatives.Add(repair);
        study.Alternatives.Add(rebuild);
        study.Baseline = baseline;

        // Act
        study.RunAsync().GetAwaiter().GetResult();
        EpsilonSweepResults sweep = study.Results!.EpsilonSweep!;

        // Assert — the guideline filter removed the baseline: it appears in no noninferior
        // set and no selection, and the admissible bound's feasible count is the two
        // compliant alternatives.
        Assert.IsNotNull(sweep);
        CollectionAssert.DoesNotContain((System.Collections.ICollection)sweep.NoninferiorAlternatives,
            "Existing condition");

        // The tight bound (0.02) is infeasible: both compliant alternatives carry the flat
        // conditional tail 0.045 (their excess life loss is proportional to their failure
        // probability, so the failure-conditioned mean is the flat 0.045 either way, to
        // quadrature rounding).
        Assert.IsTrue(sweep.Entries[0].IsInfeasible);
        Assert.AreEqual(0, sweep.Entries[0].FeasibleCount);

        // The admissible bound selects the repair: on the hand closed forms the repair's
        // total expected annual cost is 1000/A(20) + 280 ≈ 360 against the rebuild's
        // 5000/A(20) + 145 ≈ 546, and the bound does not bind (the unconstrained optimum is
        // already admitted).
        Assert.AreEqual(2, sweep.Entries[1].FeasibleCount);
        Assert.AreEqual("Gate repair", sweep.Entries[1].SelectedAlternative);
        Assert.IsFalse(sweep.Entries[1].EpsilonBinding);
        Assert.IsTrue(sweep.Entries[0].EpsilonBinding,
            "The infeasible bound excluded the unconstrained optimum.");

        // The repair leads the noninferior set; the rebuild survives only if its rounded
        // conditional tail lands a hair below the repair's (the values coincide in real
        // arithmetic, so neither strictly dominates the other except through rounding).
        CollectionAssert.Contains((System.Collections.ICollection)sweep.NoninferiorAlternatives,
            "Gate repair");
    }

    /// <summary>
    /// The frontier and incremental-analysis hand set: strict and weak dominance, an exact
    /// tie kept on both rows, a NaN exclusion, mixed objective directions, and a zero-cost
    /// do-nothing row; the cost-ranked incremental table's ratios recompose exactly, the
    /// zero-increment steps are NaN, and the incremental cost per life saved equals the
    /// ε-sweep's trade-off ratios bit-for-bit — the two views call one shared helper on the
    /// same operands.
    /// </summary>
    [TestMethod]
    public void Test_FrontierIca_HandSet()
    {
        // Arrange — six alternatives over (cost min, benefit max, lives saved max).
        var names = new[] { "Do nothing", "Small levee", "Big levee", "Gold plating", "Big twin", "Unpriced" };
        var costs = new[] { 0d, 100d, 300d, 300d, 300d, double.NaN };
        var benefits = new[] { 0d, 300d, 500d, 400d, 500d, 100d };
        var lives = new[] { 0d, 0.002d, 0.005d, 0.004d, 0.005d, 0.001d };
        double[][] values =
        {
            new[] { costs[0], benefits[0], lives[0] },
            new[] { costs[1], benefits[1], lives[1] },
            new[] { costs[2], benefits[2], lives[2] },
            new[] { costs[3], benefits[3], lives[3] },
            new[] { costs[4], benefits[4], lives[4] },
            new[] { costs[5], benefits[5], lives[5] },
        };
        var directions = new[]
        {
            ObjectiveDirection.Minimize, ObjectiveDirection.Maximize, ObjectiveDirection.Maximize,
        };
        var excluded = new[] { false, false, false, false, false, true };

        // Act
        bool[] nonDominated = ParetoFrontierEngine.NonDominated(values, directions, excluded);
        List<IncrementalEntry> incremental = ParetoFrontierEngine.IncrementalAnalysis(names, costs,
            benefits, lives, nonDominated);

        // Assert — the screen: the zero-cost row and both exact twins survive, the
        // gold-plated row is weakly dominated (equal cost, lower benefit and lives), and the
        // NaN row is excluded rather than compared.
        Assert.IsTrue(nonDominated[0]);
        Assert.IsTrue(nonDominated[1]);
        Assert.IsTrue(nonDominated[2]);
        Assert.IsFalse(nonDominated[3]);
        Assert.IsTrue(nonDominated[4], "An exact tie keeps both rows.");
        Assert.IsFalse(nonDominated[5]);

        // The cost-ranked incremental walk: Do nothing → Small levee → Big levee → Big twin,
        // with exact increment arithmetic and the zero-increment NaN discipline.
        Assert.AreEqual(3, incremental.Count);
        Assert.AreEqual("Do nothing", incremental[0].FromAlternative);
        Assert.AreEqual("Small levee", incremental[0].ToAlternative);
        Assert.AreEqual(3d, incremental[0].IncrementalBenefitCostRatio, 0d);
        Assert.AreEqual(100d / 0.002d, incremental[0].IncrementalCostPerLifeSaved, 0d);
        Assert.AreEqual(1d, incremental[1].IncrementalBenefitCostRatio, 0d);
        Assert.IsTrue(double.IsNaN(incremental[2].IncrementalBenefitCostRatio),
            "A zero cost increment has no ratio.");
        Assert.IsTrue(double.IsNaN(incremental[2].IncrementalCostPerLifeSaved),
            "A zero lives increment has no ratio.");

        // The unification, bit-exact: an ε sweep of cost against negated lives saved walks
        // the same steps, and its trade-off column must equal the incremental
        // cost-per-life-saved column with no delta — both call the one shared helper on the
        // same operands.
        var sweptLives = new double[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            sweptLives[i] = -lives[i];
        }
        var allTrue = new bool[names.Length];
        for (int i = 0; i < names.Length; i++) allTrue[i] = true;
        EpsilonSweepResults sweep = EpsilonSweepEngine.Run("Cost", ObjectiveDirection.Minimize,
            "Negated lives saved", names, costs, sweptLives, allTrue, allTrue,
            new[] { -0d, -0.002d, -0.005d }, gridPoints: 10, new List<ComputationDiagnostic>());

        // The ascending grid walks the negated axis from the tightest bound (most lives
        // demanded) to the loosest, so the selections traverse the frontier in reverse and
        // each trade-off pairs the same adjacent alternatives as the incremental table with
        // the operand order flipped — IEEE negation and subtraction reversal are exact, so
        // the ratios still agree with no delta.
        Assert.AreEqual("Big levee", sweep.Entries[0].SelectedAlternative);
        Assert.AreEqual("Small levee", sweep.Entries[1].SelectedAlternative);
        Assert.AreEqual("Do nothing", sweep.Entries[2].SelectedAlternative);
        Assert.AreEqual(incremental[1].IncrementalCostPerLifeSaved, sweep.Entries[1].TradeOffRatio, 0d);
        Assert.AreEqual(incremental[0].IncrementalCostPerLifeSaved, sweep.Entries[2].TradeOffRatio, 0d);
    }

    /// <summary>
    /// The multi-criteria arithmetic at hand values: weights {2, 1, 1} normalize to
    /// {0.5, 0.25, 0.25} exactly, the constant objective contributes zero to every score,
    /// the NaN row is excluded with a zero rank, and the scores and ranks are exact — every
    /// normalized value is a quotient of exact integers.
    /// </summary>
    [TestMethod]
    public void Test_Mcda_Arithmetic()
    {
        // Arrange — (cost min, benefit max, constant): the leader is best on both live
        // objectives, the laggard worst on both, and one row carries a NaN.
        var names = new[] { "Leader", "Laggard", "Unmeasured" };
        double[][] values =
        {
            new[] { 10d, 100d, 7d },
            new[] { 30d, 40d, 7d },
            new[] { 20d, double.NaN, 7d },
        };
        var directions = new[]
        {
            ObjectiveDirection.Minimize, ObjectiveDirection.Maximize, ObjectiveDirection.Minimize,
        };

        // Act
        McdaResults results = McdaEngine.Score(new[] { "Cost", "Benefit", "Constant" }, directions,
            new[] { 2d, 1d, 1d }, names, values, new[] { true, true, true });

        // Assert — the weight normalization is exact.
        CollectionAssert.AreEqual(new[] { 0.5d, 0.25d, 0.25d },
            (System.Collections.ICollection)results.NormalizedWeights);

        // The hand scores: the leader normalizes to one on cost ((30 − 10)/20) and benefit
        // ((100 − 40)/60) and zero on the constant, so its score is 0.5 + 0.25 = 0.75
        // exactly; the laggard scores exactly zero.
        Assert.AreEqual(1d, results.NormalizedValues[0][0], 0d);
        Assert.AreEqual(1d, results.NormalizedValues[0][1], 0d);
        Assert.AreEqual(0d, results.NormalizedValues[0][2], 0d);
        Assert.AreEqual(0.75d, results.Scores[0], 0d);
        Assert.AreEqual(0d, results.Scores[1], 0d);
        Assert.AreEqual(1, results.Ranks[0]);
        Assert.AreEqual(2, results.Ranks[1]);

        // The NaN exclusion: no score, no rank, flagged.
        Assert.IsTrue(results.IsExcludedForNaN[2]);
        Assert.IsTrue(double.IsNaN(results.Scores[2]));
        Assert.AreEqual(0, results.Ranks[2]);
    }

    /// <summary>
    /// The study-level re-measurement: tail measures at the study's declared exceedance
    /// level, read from the retained epoch curves through the resolver's clone route, agree
    /// bit-for-bit with a directly-configured twin run at that level — both evaluate the
    /// same thinned loss-exceedance arrays through the same measure computation — and the
    /// re-measured level genuinely differs from the run's own level on the stepped flat
    /// model.
    /// </summary>
    [TestMethod]
    public void Test_StudyAlpha_Reevaluation()
    {
        // Arrange / Act — the repair study declares the 60 percent exceedance level while
        // every referenced analysis runs at its own default one percent.
        var costs = new CostStream(new[] { new CapitalCostEntry(0, 1000d) });
        (CostBenefitAnalysis study, _, _, _) = RunRepairStudy(new CostBenefitOptions(20, 0.05d,
            alphaLevels: new[] { 0.6d }), repairYear: 0, costs);
        CostBenefitResults results = study.Results!;
        var resolver = new CostBenefitMetricResolver(study.Options, results.Alternatives,
            results.Trajectories, new IReadOnlyList<double>[] { new[] { double.NaN }, new[] { double.NaN } },
            reliabilityMode: false, DiscountingSupport.AnnuityFactor(20, 0.05d),
            new List<ComputationDiagnostic>());
        double studyLevelValueAtRisk = resolver.ResolveValue(
            CostBenefitMetric.ForRiskMeasure(RiskMeasure.ValueAtRisk, RiskType.Total), 0);
        double studyLevelConditional = resolver.ResolveValue(
            CostBenefitMetric.ForRiskMeasure(RiskMeasure.ConditionalValueAtRisk, RiskType.Total), 0);
        double runLevelValueAtRisk = resolver.ResolveValue(
            CostBenefitMetric.ForRiskMeasure(RiskMeasure.ValueAtRisk, RiskType.Total, alpha: 0.01d), 0);

        // The directly-configured twin: the same broken flat model run mean-only at the
        // study's level.
        RiskAnalysis twin = BuildFlatSystem(houseState: true, out _, out _);
        twin.Options.Alpha = 0.6d;
        twin.Options.EstimateMeanRiskOnly = true;
        twin.RunAsync().GetAwaiter().GetResult();
        Curve twinTotal = twin.MeanRiskResults!.Curves.Total;

        // Assert — the thinned-LEC route pins bit-exactly against the twin: both read the
        // same deterministic thinned arrays through the same measure computation.
        Assert.AreEqual(twinTotal.ValueAtRisk, studyLevelValueAtRisk, 0d);
        Assert.AreEqual(twinTotal.ConditionalValueAtRisk, studyLevelConditional, 0d);

        // And the level genuinely matters on the stepped model: beyond the 50 percent
        // failure probability the value at risk drops from the failure to the background
        // consequence scale.
        Assert.AreNotEqual(runLevelValueAtRisk, studyLevelValueAtRisk,
            "The study level must re-measure, not echo the run level.");
        Assert.IsTrue(runLevelValueAtRisk > studyLevelValueAtRisk,
            "The one-percent value at risk sits on the failure branch of the stepped curve.");
    }

    /// <summary>
    /// The do-no-harm screen across its policies on an alternative that reduces Excess risk
    /// while raising Total risk through background growth (a lower failure probability over
    /// a quadrupled background): the row is flagged with the offending type named; Enforce
    /// excludes it from the ε selection even where it optimizes the primary and marks it on
    /// the frontier; WarnOnly leaves it selectable and carries the advisory validation
    /// Warning; Off leaves the screen unevaluated.
    /// </summary>
    [TestMethod]
    public void Test_DoNoHarm_Screen()
    {
        // Arrange — the baseline holds Total 280 / Excess 180 (probability 0.2 over
        // background 100); the harmful alternative holds Total 460 / Excess 60 (probability
        // 0.1 over background 400): Excess falls while Total rises.
        static CostBenefitAnalysis BuildScreenStudy(DoNoHarmPolicy policy)
        {
            var baseline = new RiskReductionAlternative("Existing condition",
                BuildCustomFlatSystem(0.2d, 100d));
            var harmful = new RiskReductionAlternative("Background growth",
                BuildCustomFlatSystem(0.1d, 400d),
                new CostStream(new[] { new CapitalCostEntry(0, 10d) }));
            var sweep = new EpsilonConstraintStudy(
                new ObjectiveDeclaration("Excess annual risk",
                    CostBenefitMetric.ForRiskMeasure(RiskMeasure.Mean, RiskType.Excess),
                    ObjectiveDirection.Minimize),
                CostBenefitMetric.ForRiskMeasure(RiskMeasure.TotalProbability, RiskType.Fail),
                new[] { 0.2d });
            var study = new CostBenefitAnalysis(new CostBenefitOptions(20, 0.05d,
                monetization: new ConsequenceMonetization(), doNoHarm: policy, epsilonStudy: sweep));
            study.Alternatives.Add(baseline);
            study.Alternatives.Add(harmful);
            study.Baseline = baseline;
            return study;
        }

        // Act / Assert — Enforce: flagged, the dollar type named, excluded from the
        // selection the harmful alternative would otherwise win (its Excess mean 60 beats
        // the baseline's 180), and marked on the frontier while staying in the table.
        CostBenefitAnalysis enforce = BuildScreenStudy(DoNoHarmPolicy.Enforce);
        enforce.RunAsync().GetAwaiter().GetResult();
        AlternativeEconomics flagged = enforce.Results!.Alternatives[1];
        Assert.IsTrue(flagged.FailsDoNoHarm);
        CollectionAssert.AreEqual(new[] { 0 },
            (System.Collections.ICollection)flagged.DoNoHarmOffendingTypes);
        Assert.AreEqual("Existing condition", enforce.Results.EpsilonSweep!.Entries[0].SelectedAlternative);
        Assert.IsTrue(enforce.Results.Frontier!.IsExcludedFromRecommendation[1]);
        Assert.IsFalse(enforce.Results.Frontier.IsExcludedForNaN[1],
            "A do-no-harm failure marks the row; it never removes it from the tables.");

        // WarnOnly: still flagged, the advisory Warning present, and the selection admits it.
        CostBenefitAnalysis warnOnly = BuildScreenStudy(DoNoHarmPolicy.WarnOnly);
        (_, List<string> warnMessages) = warnOnly.Validate();
        bool advised = false;
        for (int i = 0; i < warnMessages.Count; i++)
        {
            if (warnMessages[i].StartsWith("Warning: The do-no-harm screen is advisory only",
                StringComparison.Ordinal))
            {
                advised = true;
            }
        }
        Assert.IsTrue(advised);
        warnOnly.RunAsync().GetAwaiter().GetResult();
        Assert.IsTrue(warnOnly.Results!.Alternatives[1].FailsDoNoHarm);
        Assert.AreEqual("Background growth", warnOnly.Results.EpsilonSweep!.Entries[0].SelectedAlternative);

        // Off: the screen is not evaluated.
        CostBenefitAnalysis off = BuildScreenStudy(DoNoHarmPolicy.Off);
        off.RunAsync().GetAwaiter().GetResult();
        Assert.IsFalse(off.Results!.Alternatives[1].FailsDoNoHarm);
        Assert.AreEqual(0, off.Results.Alternatives[1].DoNoHarmOffendingTypes.Count);
    }

    /// <summary>
    /// The reliability-mode study: two reliability-mode alternatives run end to end with the
    /// reliability template — the failure-probability axis, costs, cost per statistical
    /// failure prevented, the cost-versus-probability-reduction frontier projection, and the
    /// ε sweep on the annualized failure probability all active — while consequence-dependent
    /// metrics report NaN with named diagnostics, and a study mixing reliability-mode and
    /// consequence-mode alternatives is refused at validation.
    /// </summary>
    [TestMethod]
    public void Test_ReliabilityMode_Study()
    {
        // Arrange — the existing condition at probability 0.2 and a costed upgrade at 0.05,
        // both in reliability mode.
        RiskAnalysis existing = BuildCustomFlatSystem(0.2d, 100d);
        existing.Options.Mode = RiskAnalysisMode.Reliability;
        RiskAnalysis upgraded = BuildCustomFlatSystem(0.05d, 100d);
        upgraded.Options.Mode = RiskAnalysisMode.Reliability;
        var baseline = new RiskReductionAlternative("Existing condition", existing);
        var upgrade = new RiskReductionAlternative("Gate upgrade", upgraded,
            new CostStream(new[] { new CapitalCostEntry(0, 1000d) }));
        // The explicit bounds carry slack above the closed-form probabilities: the resolved
        // values inherit quadrature rounding, so a bound set exactly at a probability could
        // sit one bit below it.
        var study = new CostBenefitAnalysis(new CostBenefitOptions(20, 0.05d,
            epsilonStudy: EpsilonConstraintStudy.CreateReliabilityStudy(new[] { 0.06d, 0.25d })));
        study.Alternatives.Add(baseline);
        study.Alternatives.Add(upgrade);
        study.Baseline = baseline;

        // Act
        study.RunAsync().GetAwaiter().GetResult();
        CostBenefitResults results = study.Results!;
        AlternativeEconomics upgradeRow = results.Alternatives[1];

        // Assert — the failure-probability axis is live: a constant annual probability's
        // survival-equivalent annualized probability is itself, so the reduction is exactly
        // the probability difference at rounding, and the failure-prevention ratio
        // recomposes from published values.
        Assert.AreEqual(0.2d, results.Alternatives[0].AnnualizedFailureProbability, 0.2d * 1e-9d);
        Assert.AreEqual(0.15d, upgradeRow.AnnualizedFailureProbabilityReduction, 0.15d * 1e-9d);
        double horizonAnnuity = DiscountingSupport.AnnuityFactor(20, 0.05d);
        Assert.AreEqual((upgradeRow.CapitalPresentValue / horizonAnnuity)
            / upgradeRow.AnnualizedFailureProbabilityReduction,
            upgradeRow.CostPerStatisticalFailurePrevented, 0d);

        // The ε sweep on the annualized failure probability: the tight bound admits the
        // upgrade alone; the loose bound admits both and least cost selects the baseline.
        EpsilonSweepResults sweep = results.EpsilonSweep!;
        Assert.AreEqual("Gate upgrade", sweep.Entries[0].SelectedAlternative);
        Assert.AreEqual("Existing condition", sweep.Entries[1].SelectedAlternative);

        // The declared-vector frontier NaN-excludes every row (monetized benefit and the
        // dispersion objective are consequence-dependent), each skip named once; the
        // cost-versus-probability-reduction projection carries the live frontier — neither
        // point dominates the other.
        Assert.IsTrue(results.Frontier!.IsExcludedForNaN[0]);
        Assert.IsTrue(results.Frontier.IsExcludedForNaN[1]);
        FrontierProjection reliabilityProjection = results.Frontier.Projections[2];
        Assert.IsFalse(reliabilityProjection.IsExcludedForNaN[0]);
        Assert.IsFalse(reliabilityProjection.IsExcludedForNaN[1]);
        Assert.IsTrue(reliabilityProjection.IsNonDominated[0]);
        Assert.IsTrue(reliabilityProjection.IsNonDominated[1]);
        bool namedSkip = false;
        for (int i = 0; i < results.Diagnostics.Count; i++)
        {
            if (string.Equals(results.Diagnostics[i].Code, "TRC2001", StringComparison.Ordinal)
                && results.Diagnostics[i].Message.Contains("reliability", StringComparison.Ordinal))
            {
                namedSkip = true;
            }
        }
        Assert.IsTrue(namedSkip, "Every consequence-dependent skip is named.");

        // A mixed-mode study is refused at validation.
        var mixed = new CostBenefitAnalysis(new CostBenefitOptions(20, 0.05d));
        var consequenceAlternative = new RiskReductionAlternative("Consequence twin",
            BuildCustomFlatSystem(0.2d, 100d));
        mixed.Alternatives.Add(new RiskReductionAlternative("Reliability member", existing));
        mixed.Alternatives.Add(consequenceAlternative);
        mixed.Baseline = mixed.Alternatives[0];
        (bool mixedValid, List<string> mixedMessages) = mixed.Validate();
        Assert.IsFalse(mixedValid);
        bool refused = false;
        for (int i = 0; i < mixedMessages.Count; i++)
        {
            if (mixedMessages[i].StartsWith("Error: Alternatives mix risk-analysis modes",
                StringComparison.Ordinal))
            {
                refused = true;
            }
        }
        Assert.IsTrue(refused);
    }
}
