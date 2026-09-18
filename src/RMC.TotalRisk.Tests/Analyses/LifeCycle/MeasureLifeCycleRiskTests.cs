using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Analyses.LifeCycle;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.Results.LifeCycle;
using RMC.TotalRisk.Results.Risk;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.Systems.Components;

namespace RMC.TotalRisk.Tests.Analyses.LifeCycle;

/// <summary>
/// Tests the life-cycle trajectory query: epoch derivation, stationary bit parity with a plain
/// mean run, cumulative interventions with last-wins overrides and chained hazard
/// replacements, per-epoch deterioration ages, the aggregate arithmetic, label echoes, author
/// inertness, and the refusal matrix.
/// </summary>
[TestClass]
public class MeasureLifeCycleRiskTests
{
    #region Fixtures

    /// <summary>
    /// Builds the standard flat OR(AND(house, 0.9), 0.2) fault-tree response: baseline failure
    /// probability 0.2, configured 0.92.
    /// </summary>
    /// <param name="houseState">The authored house state.</param>
    /// <param name="houseId">The house-event node id.</param>
    /// <returns>The response.</returns>
    private static FaultTreeResponse FaultResponse(bool houseState, out Guid houseId)
    {
        var faultTree = new FaultTree();
        Guid gateId = faultTree.Add(faultTree.Root.Id,
            new FaultTreeGateNode("Outage impact", FaultTreeGateType.And));
        houseId = faultTree.Add(gateId, new FaultTreeHouseEventNode("Gate out of service", houseState));
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

    /// <summary>Builds the deterministic stage-frequency hazard with a scalable stage range.</summary>
    /// <param name="stageScale">The factor applied to the stages (1 = the standard 0..1 range).</param>
    /// <returns>The hazard.</returns>
    private static TabularHazard Hazard(double stageScale = 1d)
    {
        return new TabularHazard
        {
            Name = stageScale == 1d ? "Stage frequency" : $"Stage frequency x{stageScale}",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            NoUncertaintyFunction = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(0.999d, new Deterministic(0d)),
                    new UncertainOrdinate(0.5d, new Deterministic(0.5d * stageScale)),
                    new UncertainOrdinate(0.001d, new Deterministic(1d * stageScale)),
                },
                true, SortOrder.Descending, true, SortOrder.Ascending,
                UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds the deterministic stage-to-loss consequence over stages 0 to 1.</summary>
    /// <param name="label">The consequence-type label.</param>
    /// <param name="unit">The consequence-type unit.</param>
    /// <returns>The consequence.</returns>
    private static TabularConsequence Consequence(string label = "Damages", string unit = "$")
    {
        return new TabularConsequence
        {
            Name = $"Failure {label}",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = label,
            ConsequenceUnit = unit,
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

    /// <summary>Builds a one-component analysis over the given response and hazard.</summary>
    /// <param name="response">The stage response.</param>
    /// <param name="hazard">The hazard; the standard one when null.</param>
    /// <returns>The analysis.</returns>
    private static RiskAnalysis BuildOver(IResponseFunction response, TabularHazard? hazard = null)
    {
        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = hazard ?? Hazard();
        component.AddFailureMode(new FailureMode(null, null, response, Consequence()));
        return new RiskAnalysis(new[] { component })
        {
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
        };
    }

    /// <summary>Builds the standard one-component fixture.</summary>
    /// <param name="houseState">The authored house state.</param>
    /// <returns>The analysis with the fault-tree function and house-event ids.</returns>
    private static (RiskAnalysis Analysis, Guid FunctionId, Guid HouseId) Build(bool houseState)
    {
        var response = FaultResponse(houseState, out Guid houseId);
        return (BuildOver(response), response.Id, houseId);
    }

    /// <summary>One house-event intervention at a year.</summary>
    /// <param name="year">The intervention year.</param>
    /// <param name="functionId">The fault-tree function id.</param>
    /// <param name="houseId">The house-event node id.</param>
    /// <param name="state">The configured state.</param>
    /// <returns>The intervention.</returns>
    private static LifeCycleIntervention HouseAt(int year, Guid functionId, Guid houseId, bool state)
    {
        return new LifeCycleIntervention(year, new[] { new HouseEventState(functionId, houseId, state) });
    }

    /// <summary>One hazard-replacement intervention at a year.</summary>
    /// <param name="year">The intervention year.</param>
    /// <param name="targetId">The replaced hazard function id.</param>
    /// <param name="replacement">The replacement hazard.</param>
    /// <returns>The intervention.</returns>
    private static LifeCycleIntervention ReplaceAt(int year, Guid targetId, IHazardFunction replacement)
    {
        return new LifeCycleIntervention(year, null, new[] { new HazardReplacement(targetId, replacement) });
    }

    #endregion

    /// <summary>Verifies a null definition is refused.</summary>
    [TestMethod]
    public void Test_Query_NullDefinition_Throws()
    {
        // Arrange
        (RiskAnalysis author, _, _) = Build(houseState: false);

        // Act / Assert
        Assert.ThrowsException<ArgumentNullException>(() => author.MeasureLifeCycleRisk(null!));
    }

    /// <summary>
    /// Verifies the stationary case: no schedule, no evaluation years — one epoch spanning the
    /// horizon whose values equal a plain mean-only run bit-for-bit.
    /// </summary>
    [TestMethod]
    public void Test_Query_Stationary_SingleEpochMatchesMeanRun_BitExact()
    {
        // Arrange
        (RiskAnalysis author, _, _) = Build(houseState: false);
        (RiskAnalysis twin, _, _) = Build(houseState: false);
        twin.RunAsync().GetAwaiter().GetResult();

        // Act
        LifeCycleRiskResults results = author.MeasureLifeCycleRisk(new LifeCycleDefinition(40));

        // Assert
        Assert.AreEqual(1, results.Epochs.Count);
        var epoch = results.Epochs[0];
        Assert.AreEqual(0, epoch.StartYear);
        Assert.AreEqual(40, epoch.SpanYears);
        Assert.AreEqual(40, epoch.EndYear);
        Assert.AreEqual(0d, epoch.EvaluationAge);
        Assert.AreEqual(twin.MeanRiskResults!.Curves.Fail.TotalProbability, epoch.System.FailureProbability);
        Assert.AreEqual(twin.MeanRiskResults.Curves.Total.Mean, epoch.System.ExpectedConsequences[0]);
        Assert.AreEqual(twin.MeanRiskResults.Components[0].Curves.Fail.TotalProbability,
            epoch.Components[0].FailureProbability);
        Assert.AreEqual(results.Epochs[0].CumulativeFailureProbability, results.FailureProbabilityByHorizon);
        Assert.AreEqual(40, results.PeriodYears);
        Assert.AreEqual("Damages", results.ConsequenceLabels[0]);
        Assert.AreEqual("$", results.ConsequenceUnits[0]);
        Assert.AreEqual(0, results.AppliedInterventions.Count);
    }

    /// <summary>
    /// Verifies epoch derivation: the sorted distinct union of year zero, the evaluation years,
    /// and the intervention years.
    /// </summary>
    [TestMethod]
    public void Test_Query_EpochBoundaries_SortedDistinctWithImplicitZero()
    {
        // Arrange
        (RiskAnalysis author, Guid functionId, Guid houseId) = Build(houseState: false);
        var definition = new LifeCycleDefinition(40, 0d, new[] { 30, 10, 10 }, new[]
        {
            HouseAt(10, functionId, houseId, true),
            HouseAt(20, functionId, houseId, false),
        });

        // Act
        LifeCycleRiskResults results = author.MeasureLifeCycleRisk(definition);

        // Assert
        Assert.AreEqual(4, results.Epochs.Count);
        int[] expectedStarts = [0, 10, 20, 30];
        for (int k = 0; k < 4; k++)
        {
            Assert.AreEqual(expectedStarts[k], results.Epochs[k].StartYear);
            Assert.AreEqual(10, results.Epochs[k].SpanYears);
            Assert.AreEqual(expectedStarts[k], results.Epochs[k].EvaluationAge);
        }
    }

    /// <summary>Verifies a year-zero intervention configures the first epoch (bit-exact twin).</summary>
    [TestMethod]
    public void Test_Query_InterventionYearZero_ConfiguresFirstEpoch()
    {
        // Arrange
        (RiskAnalysis author, Guid functionId, Guid houseId) = Build(houseState: false);
        (RiskAnalysis configuredTwin, _, _) = Build(houseState: true);
        configuredTwin.RunAsync().GetAwaiter().GetResult();

        // Act
        LifeCycleRiskResults results = author.MeasureLifeCycleRisk(
            new LifeCycleDefinition(10, 0d, null, new[] { HouseAt(0, functionId, houseId, true) }));

        // Assert
        Assert.AreEqual(configuredTwin.MeanRiskResults!.Curves.Fail.TotalProbability,
            results.Epochs[0].System.FailureProbability);
        Assert.AreEqual(configuredTwin.MeanRiskResults.Curves.Total.Mean,
            results.Epochs[0].System.ExpectedConsequences[0]);
    }

    /// <summary>
    /// Verifies states accumulate with later entries overriding earlier ones: a revert at year
    /// twenty reproduces the baseline epoch bit-for-bit.
    /// </summary>
    [TestMethod]
    public void Test_Query_LaterStateOverridesEarlier_RevertBitEqualsBaseline()
    {
        // Arrange
        (RiskAnalysis author, Guid functionId, Guid houseId) = Build(houseState: false);
        var definition = new LifeCycleDefinition(30, 0d, null, new[]
        {
            HouseAt(10, functionId, houseId, true),
            HouseAt(20, functionId, houseId, false),
        });

        // Act
        LifeCycleRiskResults results = author.MeasureLifeCycleRisk(definition);

        // Assert — the configured middle epoch moves, the reverted final epoch returns exactly.
        Assert.IsTrue(results.Epochs[1].System.FailureProbability
            > results.Epochs[0].System.FailureProbability);
        Assert.AreEqual(results.Epochs[0].System.FailureProbability,
            results.Epochs[2].System.FailureProbability);
        Assert.AreEqual(results.Epochs[0].System.ExpectedConsequences[0],
            results.Epochs[2].System.ExpectedConsequences[0]);
    }

    /// <summary>
    /// Verifies a hazard replacement applies from its year onward and matches a directly
    /// re-authored twin bit-for-bit.
    /// </summary>
    [TestMethod]
    public void Test_Query_HazardReplacement_AppliedFromItsYear_BitExact()
    {
        // Arrange — the replacement halves the stage range, moving the consequences.
        var authorHazard = Hazard();
        var response = FaultResponse(houseState: false, out _);
        RiskAnalysis author = BuildOver(response, authorHazard);
        var futureHazard = Hazard(stageScale: 0.5d);

        var baselineTwin = BuildOver(FaultResponse(houseState: false, out _), Hazard());
        baselineTwin.RunAsync().GetAwaiter().GetResult();
        var replacedTwin = BuildOver(FaultResponse(houseState: false, out _), Hazard(stageScale: 0.5d));
        replacedTwin.RunAsync().GetAwaiter().GetResult();

        // Act
        LifeCycleRiskResults results = author.MeasureLifeCycleRisk(
            new LifeCycleDefinition(20, 0d, null, new[] { ReplaceAt(10, authorHazard.Id, futureHazard) }));

        // Assert
        Assert.AreEqual(baselineTwin.MeanRiskResults!.Curves.Total.Mean,
            results.Epochs[0].System.ExpectedConsequences[0]);
        Assert.AreEqual(replacedTwin.MeanRiskResults!.Curves.Total.Mean,
            results.Epochs[1].System.ExpectedConsequences[0]);
        Assert.AreNotEqual(results.Epochs[0].System.ExpectedConsequences[0],
            results.Epochs[1].System.ExpectedConsequences[0]);
    }

    /// <summary>
    /// Verifies a replacement reconfigures every live instance of the target function: two
    /// components sharing one hazard both move.
    /// </summary>
    [TestMethod]
    public void Test_Query_HazardReplacement_EveryLiveInstance()
    {
        // Arrange — one stored hazard instance assigned to both components.
        var shared = Hazard();
        var first = new SystemComponent { Name = "Dam" };
        first.HazardFunction = shared;
        first.AddFailureMode(new FailureMode(null, null, FaultResponse(false, out _), Consequence()));
        var second = new SystemComponent { Name = "Levee" };
        second.HazardFunction = shared;
        second.AddFailureMode(new FailureMode(null, null, FaultResponse(false, out _), Consequence()));
        var author = new RiskAnalysis(new[] { first, second })
        {
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
        };

        // Act
        LifeCycleRiskResults baseline = author.MeasureLifeCycleRisk(new LifeCycleDefinition(10));
        LifeCycleRiskResults replaced = author.MeasureLifeCycleRisk(new LifeCycleDefinition(10, 0d, null,
            new[] { ReplaceAt(0, shared.Id, Hazard(stageScale: 0.5d)) }));

        // Assert — both components' consequences move.
        Assert.AreNotEqual(baseline.Epochs[0].Components[0].ExpectedConsequences[0],
            replaced.Epochs[0].Components[0].ExpectedConsequences[0]);
        Assert.AreNotEqual(baseline.Epochs[0].Components[1].ExpectedConsequences[0],
            replaced.Epochs[0].Components[1].ExpectedConsequences[0]);
    }

    /// <summary>Verifies unreachable targets refuse loudly, wrapped with the epoch year.</summary>
    [TestMethod]
    public void Test_Query_UnreachableTargets_ThrowWithEpochYear()
    {
        // Arrange
        (RiskAnalysis author, Guid functionId, _) = Build(houseState: false);

        // Act / Assert — an unmatched house event.
        var houseFailure = Assert.ThrowsException<InvalidOperationException>(() =>
            author.MeasureLifeCycleRisk(new LifeCycleDefinition(10, 0d, null,
                new[] { HouseAt(0, functionId, Guid.NewGuid(), true) })));
        StringAssert.Contains(houseFailure.Message, "starting at year 0");
        StringAssert.Contains(houseFailure.Message, "cannot reach");

        // An unmatched replacement target.
        var replacementFailure = Assert.ThrowsException<InvalidOperationException>(() =>
            author.MeasureLifeCycleRisk(new LifeCycleDefinition(10, 0d, null,
                new[] { ReplaceAt(5, Guid.NewGuid(), Hazard()) })));
        StringAssert.Contains(replacementFailure.Message, "starting at year 5");
        StringAssert.Contains(replacementFailure.Message, "cannot reach");
    }

    /// <summary>Verifies a cross-arity replacement is refused loudly.</summary>
    [TestMethod]
    public void Test_Query_ArityMismatch_Throws()
    {
        // Arrange
        var authorHazard = Hazard();
        RiskAnalysis author = BuildOver(FaultResponse(false, out _), authorHazard);

        // Act / Assert — a bivariate replacement for a univariate hazard.
        var failure = Assert.ThrowsException<InvalidOperationException>(() =>
            author.MeasureLifeCycleRisk(new LifeCycleDefinition(10, 0d, null,
                new[] { ReplaceAt(0, authorHazard.Id, new BivariateHazard { Name = "Joint" }) })));
        StringAssert.Contains(failure.Message, "arity");
    }

    /// <summary>
    /// Verifies chained replacements match the live assignment: a chain through the first
    /// replacement works, while re-targeting the original function refuses as unreachable.
    /// </summary>
    [TestMethod]
    public void Test_Query_ChainedReplacements_TargetLiveFunction()
    {
        // Arrange
        var hazardA = Hazard();
        var hazardB = Hazard(stageScale: 0.5d);
        var hazardC = Hazard(stageScale: 2d);
        RiskAnalysis author = BuildOver(FaultResponse(false, out _), hazardA);
        var finalTwin = BuildOver(FaultResponse(false, out _), Hazard(stageScale: 2d));
        finalTwin.RunAsync().GetAwaiter().GetResult();

        // Act — A→B at year 10, then B→C at year 20.
        LifeCycleRiskResults chained = author.MeasureLifeCycleRisk(new LifeCycleDefinition(30, 0d, null,
            new[] { ReplaceAt(10, hazardA.Id, hazardB), ReplaceAt(20, hazardB.Id, hazardC) }));

        // Assert — the final epoch runs on hazard C.
        Assert.AreEqual(finalTwin.MeanRiskResults!.Curves.Total.Mean,
            chained.Epochs[2].System.ExpectedConsequences[0]);

        // Re-targeting A after A→B is unreachable at the later epoch.
        var failure = Assert.ThrowsException<InvalidOperationException>(() =>
            author.MeasureLifeCycleRisk(new LifeCycleDefinition(30, 0d, null,
                new[] { ReplaceAt(10, hazardA.Id, hazardB), ReplaceAt(20, hazardA.Id, hazardC) })));
        StringAssert.Contains(failure.Message, "starting at year 20");
        StringAssert.Contains(failure.Message, "cannot reach");
    }

    /// <summary>
    /// Verifies every horizon aggregate reproduces the documented arithmetic over the epoch
    /// rows, discounted and undiscounted, with no delta.
    /// </summary>
    [TestMethod]
    public void Test_Query_Aggregates_MatchEpochRowArithmetic_BitExact()
    {
        // Arrange
        (RiskAnalysis author, Guid functionId, Guid houseId) = Build(houseState: false);

        foreach (double rate in new[] { 0.05d, 0d })
        {
            // Act
            LifeCycleRiskResults results = author.MeasureLifeCycleRisk(new LifeCycleDefinition(
                20, rate, null, new[] { HouseAt(10, functionId, houseId, true) }));

            // Assert — recompute every aggregate from the published rows with the documented
            // formulas (the ascending-epoch accumulation order).
            static double Annuity(int years, double r) => r > 0d
                ? -Tools.Expm1(-years * Tools.Log1p(r)) / r
                : years;

            double logSurvival = 0d;
            double cumulative = 0d;
            double presentValue = 0d;
            double absorbingCumulative = 0d;
            double absorbingPresentValue = 0d;
            double discountBase = 1d / (1d + rate);
            foreach (LifeCycleEpochRisk epoch in results.Epochs)
            {
                double p = epoch.System.FailureProbability;
                double mean = epoch.System.ExpectedConsequences[0];
                int span = epoch.SpanYears;
                cumulative += span * mean;
                presentValue += mean * (Annuity(epoch.EndYear, rate) - Annuity(epoch.StartYear, rate));

                double survivalAtStart = Math.Exp(logSurvival);
                double survivalYears = p > 0d ? -Tools.Expm1(span * Tools.Log1p(-p)) / p : span;
                double x = (1d - p) * discountBase;
                double geometric = x == 1d ? span : (1d - Math.Pow(x, span)) / (1d - x);
                double firstYearDiscount = Math.Exp(-(epoch.StartYear + 1) * Tools.Log1p(rate));
                absorbingCumulative += mean * survivalAtStart * survivalYears;
                absorbingPresentValue += mean * survivalAtStart * firstYearDiscount * geometric;

                logSurvival += span * Tools.Log1p(-p);
                Assert.AreEqual(-Tools.Expm1(logSurvival), epoch.CumulativeFailureProbability);
            }

            Assert.AreEqual(-Tools.Expm1(logSurvival), results.FailureProbabilityByHorizon);
            Assert.AreEqual(cumulative, results.CumulativeExpectedConsequences[0]);
            Assert.AreEqual(presentValue, results.PresentValueOfExpectedConsequences[0]);
            Assert.AreEqual(presentValue / Annuity(20, rate), results.EquivalentAnnualConsequences[0]);
            Assert.AreEqual(absorbingCumulative, results.AbsorbingCumulativeExpectedConsequences[0]);
            Assert.AreEqual(absorbingPresentValue, results.AbsorbingPresentValueOfExpectedConsequences[0]);

            // The absorbing readings never exceed the non-absorbing ones.
            Assert.IsTrue(results.AbsorbingCumulativeExpectedConsequences[0]
                <= results.CumulativeExpectedConsequences[0]);
        }
    }

    /// <summary>Verifies the intervention and per-epoch action label echoes.</summary>
    [TestMethod]
    public void Test_Query_LabelEchoes()
    {
        // Arrange
        var authorHazard = Hazard();
        var response = FaultResponse(houseState: false, out Guid houseId);
        RiskAnalysis author = BuildOver(response, authorHazard);
        var definition = new LifeCycleDefinition(30, 0d, null, new[]
        {
            HouseAt(10, response.Id, houseId, true),
            ReplaceAt(20, authorHazard.Id, Hazard(stageScale: 0.5d)),
        });

        // Act
        LifeCycleRiskResults results = author.MeasureLifeCycleRisk(definition);

        // Assert — one label per entry, prefixed with its year.
        Assert.AreEqual(2, results.AppliedInterventions.Count);
        StringAssert.StartsWith(results.AppliedInterventions[0], "Year 10:");
        StringAssert.Contains(results.AppliedInterventions[0], "Gate out of service");
        StringAssert.StartsWith(results.AppliedInterventions[1], "Year 20:");
        StringAssert.Contains(results.AppliedInterventions[1], "replaced by");

        // The epochs carry their cumulative effective configuration.
        Assert.AreEqual(0, results.Epochs[0].AppliedActions.Count);
        Assert.AreEqual(1, results.Epochs[1].AppliedActions.Count);
        Assert.AreEqual(2, results.Epochs[2].AppliedActions.Count);
    }

    /// <summary>Verifies the per-type aggregate axis over a declared second consequence type.</summary>
    [TestMethod]
    public void Test_Query_MultiConsequence_PerTypeAggregates()
    {
        // Arrange — a second declared type with its own consequence on the mode.
        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = Hazard();
        var mode = new FailureMode(null, null, FaultResponse(false, out _), Consequence());
        mode.ConsequenceFunctions.Add(Consequence("Life Loss", "lives"));
        component.AddFailureMode(mode);
        var author = new RiskAnalysis(new[] { component })
        {
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
        };
        author.AdditionalConsequenceTypes.Add(new ConsequenceTypeDescriptor("Life Loss", "lives"));

        // Act
        LifeCycleRiskResults results = author.MeasureLifeCycleRisk(new LifeCycleDefinition(10));

        // Assert — both types ride every list, and the second type's cumulative follows its row.
        Assert.AreEqual(2, results.ConsequenceLabels.Count);
        Assert.AreEqual("Life Loss", results.ConsequenceLabels[1]);
        Assert.AreEqual("lives", results.ConsequenceUnits[1]);
        Assert.AreEqual(2, results.Epochs[0].System.ExpectedConsequences.Count);
        Assert.AreEqual(10 * results.Epochs[0].System.ExpectedConsequences[1],
            results.CumulativeExpectedConsequences[1]);
    }

    /// <summary>Verifies the query leaves the authored model, results, and references untouched.</summary>
    [TestMethod]
    public void Test_Query_AuthorState_Untouched()
    {
        // Arrange
        var authorHazard = Hazard();
        var response = FaultResponse(houseState: false, out Guid houseId);
        RiskAnalysis author = BuildOver(response, authorHazard);
        author.RunAsync().GetAwaiter().GetResult();
        string publishedJson = author.RiskResults!.ToJson();
        string componentHash = Convert.ToHexString(author.Components[0].CanonicalHash());

        // Act — a schedule exercising both action kinds.
        author.MeasureLifeCycleRisk(new LifeCycleDefinition(20, 0d, null, new[]
        {
            HouseAt(0, response.Id, houseId, true),
            ReplaceAt(10, authorHazard.Id, Hazard(stageScale: 0.5d)),
        }));

        // Assert
        Assert.IsTrue(author.IsEstimated, "The query must not invalidate the published results.");
        Assert.AreEqual(publishedJson, author.RiskResults!.ToJson());
        Assert.AreEqual(componentHash, Convert.ToHexString(author.Components[0].CanonicalHash()));
        Assert.IsTrue(ReferenceEquals(authorHazard, author.Components[0].HazardFunction),
            "The authored hazard assignment must be untouched.");
        Assert.IsFalse(((FaultTreeHouseEventNode)response.FaultTree.FindById(houseId)!).State,
            "The authored house event must keep its state.");
    }

    /// <summary>Verifies the query runs without any prior estimation of the authored model.</summary>
    [TestMethod]
    public void Test_Query_UnestimatedAuthor_Works()
    {
        // Arrange
        (RiskAnalysis author, _, _) = Build(houseState: false);

        // Act
        LifeCycleRiskResults results = author.MeasureLifeCycleRisk(new LifeCycleDefinition(10));

        // Assert
        Assert.IsFalse(author.IsEstimated);
        Assert.AreEqual(1, results.Epochs.Count);
    }

    /// <summary>
    /// Verifies the Excess- and Fail-stream aggregates reproduce the documented arithmetic
    /// over the epoch rows' stream lists with no delta, discounted and undiscounted, and that
    /// every scope row carries the stream axis.
    /// </summary>
    [TestMethod]
    public void Test_Query_StreamAggregates_MatchEpochRowArithmetic_BitExact()
    {
        // Arrange
        (RiskAnalysis author, Guid functionId, Guid houseId) = Build(houseState: false);

        foreach (double rate in new[] { 0.05d, 0d })
        {
            // Act
            LifeCycleRiskResults results = author.MeasureLifeCycleRisk(new LifeCycleDefinition(
                20, rate, null, new[] { HouseAt(10, functionId, houseId, true) }));

            // Assert — recompute each stream's aggregates from the published rows with the
            // documented formulas (the ascending-epoch accumulation order).
            static double Annuity(int years, double r) => r > 0d
                ? -Tools.Expm1(-years * Tools.Log1p(r)) / r
                : years;

            double logSurvival = 0d;
            var cumulative = new double[2];
            var presentValue = new double[2];
            var absorbingCumulative = new double[2];
            var absorbingPresentValue = new double[2];
            double discountBase = 1d / (1d + rate);
            foreach (LifeCycleEpochRisk epoch in results.Epochs)
            {
                Assert.AreEqual(1, epoch.System.ExcessExpectedConsequences.Count);
                Assert.AreEqual(1, epoch.System.FailExpectedConsequences.Count);
                Assert.AreEqual(1, epoch.Components[0].ExcessExpectedConsequences.Count);
                Assert.AreEqual(1, epoch.Components[0].FailExpectedConsequences.Count);

                double p = epoch.System.FailureProbability;
                int span = epoch.SpanYears;
                double annuitySegment = Annuity(epoch.EndYear, rate) - Annuity(epoch.StartYear, rate);
                double survivalAtStart = Math.Exp(logSurvival);
                double survivalYears = p > 0d ? -Tools.Expm1(span * Tools.Log1p(-p)) / p : span;
                double x = (1d - p) * discountBase;
                double geometric = x == 1d ? span : (1d - Math.Pow(x, span)) / (1d - x);
                double firstYearDiscount = Math.Exp(-(epoch.StartYear + 1) * Tools.Log1p(rate));
                double[] streamMeans =
                [
                    epoch.System.ExcessExpectedConsequences[0],
                    epoch.System.FailExpectedConsequences[0],
                ];
                for (int s = 0; s < 2; s++)
                {
                    double mean = streamMeans[s];
                    cumulative[s] += span * mean;
                    presentValue[s] += mean * annuitySegment;
                    absorbingCumulative[s] += mean * survivalAtStart * survivalYears;
                    absorbingPresentValue[s] += mean * survivalAtStart * firstYearDiscount * geometric;
                }
                logSurvival += span * Tools.Log1p(-p);
            }

            Assert.AreEqual(cumulative[0], results.ExcessCumulativeExpectedConsequences[0]);
            Assert.AreEqual(presentValue[0], results.ExcessPresentValueOfExpectedConsequences[0]);
            Assert.AreEqual(presentValue[0] / Annuity(20, rate), results.ExcessEquivalentAnnualConsequences[0]);
            Assert.AreEqual(absorbingCumulative[0], results.AbsorbingExcessCumulativeExpectedConsequences[0]);
            Assert.AreEqual(absorbingPresentValue[0], results.AbsorbingExcessPresentValueOfExpectedConsequences[0]);
            Assert.AreEqual(cumulative[1], results.FailCumulativeExpectedConsequences[0]);
            Assert.AreEqual(presentValue[1], results.FailPresentValueOfExpectedConsequences[0]);
            Assert.AreEqual(presentValue[1] / Annuity(20, rate), results.FailEquivalentAnnualConsequences[0]);
            Assert.AreEqual(absorbingCumulative[1], results.AbsorbingFailCumulativeExpectedConsequences[0]);
            Assert.AreEqual(absorbingPresentValue[1], results.AbsorbingFailPresentValueOfExpectedConsequences[0]);

            // The failure-path stream never exceeds the total in this all-positive fixture.
            Assert.IsTrue(results.FailCumulativeExpectedConsequences[0]
                <= results.CumulativeExpectedConsequences[0]);
        }
    }

    /// <summary>
    /// Verifies epoch-realization retention: every aggregate is bit-identical with retention
    /// on or off, the rows carry dumped realizations only when retaining, and each retained
    /// realization agrees with its own scope row.
    /// </summary>
    [TestMethod]
    public void Test_Query_RetainEpochRealizations_DumpedAttachment_AggregatesBitEqual()
    {
        // Arrange
        (RiskAnalysis author, Guid functionId, Guid houseId) = Build(houseState: false);
        var schedule = new[] { HouseAt(10, functionId, houseId, true) };

        // Act
        LifeCycleRiskResults off = author.MeasureLifeCycleRisk(
            new LifeCycleDefinition(20, 0.035d, null, schedule));
        LifeCycleRiskResults on = author.MeasureLifeCycleRisk(
            new LifeCycleDefinition(20, 0.035d, null, schedule, retainEpochRealizations: true));

        // Assert — retention never moves a number.
        Assert.AreEqual(off.FailureProbabilityByHorizon, on.FailureProbabilityByHorizon);
        Assert.AreEqual(off.CumulativeExpectedConsequences[0], on.CumulativeExpectedConsequences[0]);
        Assert.AreEqual(off.PresentValueOfExpectedConsequences[0], on.PresentValueOfExpectedConsequences[0]);
        Assert.AreEqual(off.EquivalentAnnualConsequences[0], on.EquivalentAnnualConsequences[0]);
        Assert.AreEqual(off.AbsorbingPresentValueOfExpectedConsequences[0],
            on.AbsorbingPresentValueOfExpectedConsequences[0]);
        Assert.AreEqual(off.ExcessPresentValueOfExpectedConsequences[0],
            on.ExcessPresentValueOfExpectedConsequences[0]);
        Assert.AreEqual(off.FailPresentValueOfExpectedConsequences[0],
            on.FailPresentValueOfExpectedConsequences[0]);

        for (int k = 0; k < off.Epochs.Count; k++)
        {
            // Off: no realization survives the row build.
            Assert.IsNull(off.Epochs[k].Realization);

            // On: the dumped mean realization rides its row and agrees with it.
            SystemRealization? retained = on.Epochs[k].Realization;
            Assert.IsNotNull(retained);
            Assert.AreEqual(0, retained.Curves.Total.RiskPoints.Count);
            Assert.AreEqual(0, retained.Curves.Fail.RiskPoints.Count);
            Assert.AreEqual(retained.Curves.Fail.TotalProbability,
                on.Epochs[k].System.FailureProbability);
            Assert.AreEqual(retained.Curves.Total.Mean,
                on.Epochs[k].System.ExpectedConsequences[0]);
            Assert.AreEqual(retained.Curves.Excess.Mean,
                on.Epochs[k].System.ExcessExpectedConsequences[0]);
            Assert.AreEqual(retained.Curves.Fail.Mean,
                on.Epochs[k].System.FailExpectedConsequences[0]);
        }
    }

    /// <summary>
    /// Verifies deteriorating responses evaluate at each epoch's start age: the aged epoch
    /// fails more, and the authored wrapper keeps its own evaluation age.
    /// </summary>
    [TestMethod]
    public void Test_Query_DeteriorationAges_AppliedPerEpoch()
    {
        // Arrange — a wrapper fragility over the standard 0..1 stage range with a weakening law.
        var wrapper = new DeterioratingResponse
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
                        new UncertainOrdinate(0d, new Deterministic(0d)),
                        new UncertainOrdinate(1d, new Deterministic(0.5d)),
                    },
                    true, SortOrder.Ascending, false, SortOrder.None,
                    UnivariateDistributionType.Deterministic),
            },
            DeteriorationLaw = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(0d, new Deterministic(0d)),
                    new UncertainOrdinate(20d, new Deterministic(0.2d)),
                },
                true, SortOrder.Ascending, false, SortOrder.None,
                UnivariateDistributionType.Deterministic),
        };
        RiskAnalysis author = BuildOver(wrapper);

        // Act
        LifeCycleRiskResults results = author.MeasureLifeCycleRisk(
            new LifeCycleDefinition(20, 0d, new[] { 10 }));

        // Assert — the aged epoch fails more; the authored age is untouched.
        Assert.IsTrue(results.Epochs[1].System.FailureProbability
            > results.Epochs[0].System.FailureProbability,
            $"{results.Epochs[0].System.FailureProbability} vs {results.Epochs[1].System.FailureProbability}");
        Assert.AreEqual(10d, results.Epochs[1].EvaluationAge);
        Assert.AreEqual(0d, wrapper.EvaluationAge);
    }
}
