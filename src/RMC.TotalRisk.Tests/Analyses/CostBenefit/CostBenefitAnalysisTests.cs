using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using Numerics.Utilities;
using RMC.TotalRisk.Analyses.CostBenefit;
using RMC.TotalRisk.Analyses.LifeCycle;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Analyses.Support;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results.CostBenefit;
using RMC.TotalRisk.Results.LifeCycle;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.Systems.Components;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Nodes;

namespace RMC.TotalRisk.Tests.Analyses.CostBenefit;

/// <summary>
/// Tests the cost-benefit study: guards, the validation matrix, the run orchestration
/// (dedup, baseline row zero, progress, cancellation, the veto event, replace-to-edit),
/// the cost and benefit arithmetic, author inertness, and run-to-run reproducibility.
/// </summary>
[TestClass]
public class CostBenefitAnalysisTests
{
    #region Fixtures

    /// <summary>
    /// Builds the flat OR(AND(house, 0.9), 0.2) fault-tree response: failure probability 0.2
    /// with the house event false, 0.92 with it true.
    /// </summary>
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

    /// <summary>Builds a two-alternative study: a degraded baseline and a costed fix.</summary>
    /// <param name="options">The study declarations; a 30-year 5% study when null.</param>
    /// <returns>The study, its baseline, and the fix alternative.</returns>
    private static (CostBenefitAnalysis Study, RiskReductionAlternative Baseline, RiskReductionAlternative Fix)
        BuildStudy(CostBenefitOptions? options = null)
    {
        var baseline = new RiskReductionAlternative("Existing condition", BuildSystem(houseState: true));
        var fix = new RiskReductionAlternative("Gate repair", BuildSystem(houseState: false),
            new CostStream(
                new[] { new CapitalCostEntry(0, 1000d), new CapitalCostEntry(10, 500d) },
                new[] { new RecurringCostSegment(0, 10d) },
                new[] { new RecurringCostSegment(10, -5d, 30) }));
        var study = new CostBenefitAnalysis(options ?? new CostBenefitOptions(30, 0.05d));
        study.Alternatives.Add(baseline);
        study.Alternatives.Add(fix);
        study.Baseline = baseline;
        return (study, baseline, fix);
    }

    /// <summary>Finds one validation message by prefix.</summary>
    /// <param name="messages">The messages.</param>
    /// <param name="prefix">The required prefix.</param>
    /// <returns>True when a message starts with the prefix.</returns>
    private static bool HasMessage(IReadOnlyList<string> messages, string prefix)
    {
        for (int i = 0; i < messages.Count; i++)
        {
            if (messages[i].StartsWith(prefix, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    #endregion

    /// <summary>Verifies null options are refused at construction and assignment.</summary>
    [TestMethod]
    public void Test_Ctor_Guards()
    {
        // Act / Assert
        Assert.ThrowsException<ArgumentNullException>(() => new CostBenefitAnalysis(null!));
        var study = new CostBenefitAnalysis(new CostBenefitOptions(30));
        Assert.ThrowsException<ArgumentNullException>(() => study.Options = null!);
    }

    /// <summary>Verifies the structural error rules of the validation matrix.</summary>
    [TestMethod]
    public void Test_Validate_Errors()
    {
        // Arrange — an empty study has no baseline.
        var empty = new CostBenefitAnalysis(new CostBenefitOptions(30));
        (bool emptyValid, var emptyMessages) = empty.Validate();
        Assert.IsFalse(emptyValid);
        Assert.IsTrue(HasMessage(emptyMessages, "Error: The study requires a designated baseline alternative."));

        // A baseline that is not a member.
        (CostBenefitAnalysis study, _, _) = BuildStudy();
        study.Baseline = new RiskReductionAlternative("Outsider", BuildSystem(false));
        Assert.IsTrue(HasMessage(study.Validate().ValidationMessages,
            "Error: The designated baseline must be a member of the alternatives collection."));

        // Duplicate names.
        (CostBenefitAnalysis duplicates, RiskReductionAlternative anchor, _) = BuildStudy();
        duplicates.Alternatives.Add(new RiskReductionAlternative("Existing condition", BuildSystem(false)));
        Assert.IsTrue(HasMessage(duplicates.Validate().ValidationMessages,
            "Error: Two alternatives share the name 'Existing condition'."));

        // Plan and cost years against the horizon.
        (CostBenefitAnalysis horizon, RiskReductionAlternative horizonBaseline, _) = BuildStudy();
        horizon.Alternatives.Add(new RiskReductionAlternative("Late plan", horizonBaseline.System,
            plan: new LifeCyclePlan(Array.Empty<LifeCycleIntervention>(), new[] { 30 })));
        horizon.Alternatives.Add(new RiskReductionAlternative("Late capital", horizonBaseline.System,
            new CostStream(new[] { new CapitalCostEntry(30, 100d) })));
        horizon.Alternatives.Add(new RiskReductionAlternative("Long segment", horizonBaseline.System,
            new CostStream(null, new[] { new RecurringCostSegment(0, 10d, 31) })));
        var horizonMessages = horizon.Validate().ValidationMessages;
        Assert.IsTrue(HasMessage(horizonMessages, "Error: Alternative 'Late plan' declares a plan year at or beyond the horizon."));
        Assert.IsTrue(HasMessage(horizonMessages, "Error: Alternative 'Late capital' declares a cost year at or beyond the horizon."));
        Assert.IsTrue(HasMessage(horizonMessages, "Error: Alternative 'Long segment' declares a cost segment ending beyond the horizon."));

        // The consequence-type axis must align with the baseline's.
        (CostBenefitAnalysis axis, _, _) = BuildStudy();
        var widerSystem = BuildSystem(false);
        widerSystem.AdditionalConsequenceTypes.Add(new ConsequenceTypeDescriptor("Life Loss", "lives"));
        axis.Alternatives.Add(new RiskReductionAlternative("Wider axis", widerSystem));
        Assert.IsTrue(HasMessage(axis.Validate().ValidationMessages,
            "Error: Alternative 'Wider axis' declares 2 consequence types while the baseline declares 1."));

        // A conflicting non-blank label.
        (CostBenefitAnalysis labels, _, _) = BuildStudy();
        var renamedSystem = BuildSystem(false);
        renamedSystem.SpecifiedConsequence = "Losses";
        labels.Alternatives.Add(new RiskReductionAlternative("Renamed", renamedSystem));
        Assert.IsTrue(HasMessage(labels.Validate().ValidationMessages,
            "Error: Alternative 'Renamed' consequence type 0 label 'Losses' conflicts with the baseline's 'Damages'."));

        // Monetization against the axis: an undeclared position and an identity type.
        (CostBenefitAnalysis monetized, _, _) = BuildStudy(new CostBenefitOptions(30, 0.05d,
            monetization: new ConsequenceMonetization(new[]
            {
                new MonetizationFactor(3, 100d),
                new MonetizationFactor(0, 100d),
            })));
        var monetizedMessages = monetized.Validate().ValidationMessages;
        Assert.IsTrue(HasMessage(monetizedMessages,
            "Error: A monetization factor prices consequence-type position 3, which is not declared."));
        Assert.IsTrue(HasMessage(monetizedMessages,
            "Error: A monetization factor prices consequence type 0, whose unit already equals the monetary unit"));

        // A life-safety position beyond the declared axis.
        (CostBenefitAnalysis lifeSafety, _, _) = BuildStudy(new CostBenefitOptions(30, 0.05d,
            lifeSafetyConsequenceType: 2));
        Assert.IsTrue(HasMessage(lifeSafety.Validate().ValidationMessages,
            "Error: The life-safety consequence-type position 2 is not declared."));

        // An unsupported benefit stream.
        (CostBenefitAnalysis stream, _, _) = BuildStudy(new CostBenefitOptions(30, 0.05d,
            benefitRiskType: RiskType.Background));
        Assert.IsTrue(HasMessage(stream.Validate().ValidationMessages,
            "Error: The benefit stream must be Total, Excess, or Fail."));
    }

    /// <summary>Verifies the advisory rules of the validation matrix.</summary>
    [TestMethod]
    public void Test_Validate_Warnings()
    {
        // Arrange — the plain study monetizes nothing and stores no ensembles.
        (CostBenefitAnalysis study, _, _) = BuildStudy();

        // Act
        var messages = study.Validate().ValidationMessages;

        // Assert
        Assert.IsTrue(HasMessage(messages,
            "Warning: No consequence types are monetized; net present value, net annual benefit, and benefit-cost ratio will be NaN."));
        Assert.IsTrue(HasMessage(messages,
            "Warning: Alternative 'Existing condition' carries no stored full-uncertainty ensemble; the epistemic decision strategies will be skipped."));
        Assert.IsTrue(HasMessage(messages,
            "Warning: Alternative 'Gate repair' was not enumerated by the exact logic tree; the shared-state regret strategies will be skipped."));
        Assert.IsTrue(HasMessage(messages,
            "Warning: No willingness to pay is declared; the disproportionality and ALARP block is skipped."));
        Assert.IsTrue(study.Validate().IsValid, "Warnings alone must not invalidate the study.");

        // A zero-cost non-baseline alternative advises.
        (CostBenefitAnalysis costless, RiskReductionAlternative costlessBaseline, _) = BuildStudy();
        costless.Alternatives.Add(new RiskReductionAlternative("Free lunch", costlessBaseline.System));
        Assert.IsTrue(HasMessage(costless.Validate().ValidationMessages,
            "Warning: Alternative 'Free lunch' declares no costs; its benefit-cost ratio will be NaN."));

        // A study α list that omits an alternative's run level advises.
        (CostBenefitAnalysis alpha, _, _) = BuildStudy(new CostBenefitOptions(30, 0.05d,
            alphaLevels: new[] { 0.002d }));
        Assert.IsTrue(HasMessage(alpha.Validate().ValidationMessages,
            "Warning: Alternative 'Existing condition' runs exceedance level"));

        // The individual-risk proxy advisory arms once a life-safety type is declared.
        var lifeSystemBaseline = BuildSystem(true);
        lifeSystemBaseline.AdditionalConsequenceTypes.Add(new ConsequenceTypeDescriptor("Life Loss", "lives"));
        var lifeStudy = new CostBenefitAnalysis(new CostBenefitOptions(30, 0.05d, lifeSafetyConsequenceType: 1));
        var lifeBaseline = new RiskReductionAlternative("Existing condition", lifeSystemBaseline);
        lifeStudy.Alternatives.Add(lifeBaseline);
        lifeStudy.Baseline = lifeBaseline;
        Assert.IsTrue(HasMessage(lifeStudy.Validate().ValidationMessages,
            "Warning: The individual-risk seats are not declared; the survival-equivalent annualized failure probability proxy is used and echoed."));
    }

    /// <summary>
    /// Verifies the null-study run: a costless twin sharing the baseline's system and plan
    /// reuses one trajectory evaluation, publishes exactly zero reductions, and reports a NaN
    /// benefit-cost ratio at zero cost.
    /// </summary>
    [TestMethod]
    public void Test_Run_NullStudy_DedupAndZeroDeltas()
    {
        // Arrange — the twin references the same system instance with no plan and no costs;
        // the declared (empty) monetization map identity-prices the dollar type at one.
        var system = BuildSystem(houseState: true);
        var baseline = new RiskReductionAlternative("Existing condition", system);
        var twin = new RiskReductionAlternative("Do nothing", system);
        var study = new CostBenefitAnalysis(new CostBenefitOptions(30, 0.05d,
            monetization: new ConsequenceMonetization()));
        study.Alternatives.Add(baseline);
        study.Alternatives.Add(twin);
        study.Baseline = baseline;

        // Act
        study.RunAsync().GetAwaiter().GetResult();

        // Assert — one evaluation serves both rows.
        CostBenefitResults results = study.Results!;
        Assert.IsTrue(study.IsEstimated);
        Assert.IsTrue(ReferenceEquals(results.Trajectories[0], results.Trajectories[1]),
            "Alternatives sharing one (system, plan) pair must share one trajectory evaluation.");
        Assert.IsTrue(results.Alternatives[0].IsBaseline);
        Assert.AreEqual("Existing condition", results.Alternatives[0].Name);

        // Every reduction row of the twin is exactly zero.
        for (int i = 0; i < results.ConsequenceReductions.Count; i++)
        {
            ConsequenceReduction row = results.ConsequenceReductions[i];
            if (!string.Equals(row.AlternativeName, "Do nothing", StringComparison.Ordinal)) continue;
            Assert.AreEqual(0d, row.PresentValueReduction, 0d);
            Assert.AreEqual(0d, row.EquivalentAnnualReduction, 0d);
            Assert.AreEqual(0d, row.CumulativeReduction, 0d);
            Assert.AreEqual(0d, row.AbsorbingPresentValueReduction, 0d);
            Assert.AreEqual(0d, row.AbsorbingCumulativeReduction, 0d);
        }

        // The identity-monetized type ($ = $) prices the zero reduction: NPV 0 at zero cost,
        // and the benefit-cost ratio is NaN — never infinite or clamped.
        AlternativeEconomics twinRow = results.Alternatives[1];
        Assert.AreEqual(0d, twinRow.MonetizedPresentValueBenefit, 0d);
        Assert.AreEqual(0d, twinRow.NetPresentValue, 0d);
        Assert.IsTrue(double.IsNaN(twinRow.BenefitCostRatio));
        Assert.AreEqual(0d, twinRow.AnnualizedFailureProbabilityReduction, 0d);
    }

    /// <summary>
    /// Verifies the economics row arithmetic: the cost block against independent power-form
    /// discounting, the identity-monetized benefit against the published trajectories, and
    /// the net-benefit identities.
    /// </summary>
    [TestMethod]
    public void Test_Run_EconomicsRow_CostAndBenefitArithmetic()
    {
        // Arrange — the declared (empty) monetization map identity-prices the dollar type.
        (CostBenefitAnalysis study, _, _) = BuildStudy(new CostBenefitOptions(30, 0.05d,
            monetization: new ConsequenceMonetization()));

        // Act
        study.RunAsync().GetAwaiter().GetResult();

        // Assert — the fix row's cost block against an independent computation
        // (capital 1000 @ 0 + 500 @ 10; operations 10/yr over (0, 30]; operating −5/yr over
        // (10, 30]; r = 0.05). The engine evaluates in log space, so agreement is relative.
        CostBenefitResults results = study.Results!;
        AlternativeEconomics fixRow = results.Alternatives[1];
        static double Annuity(int years) => (1d - Math.Pow(1.05d, -years)) / 0.05d;
        double expectedCapital = 1000d + 500d * Math.Pow(1.05d, -10);
        double expectedOperations = 10d * Annuity(30);
        double expectedOperating = -5d * (Annuity(30) - Annuity(10));
        Assert.AreEqual(expectedCapital, fixRow.CapitalPresentValue, Math.Abs(expectedCapital) * 1e-12);
        Assert.AreEqual(expectedOperations, fixRow.OperationsAndMaintenancePresentValue,
            Math.Abs(expectedOperations) * 1e-12);
        Assert.AreEqual(expectedOperating, fixRow.OperatingChangePresentValue,
            Math.Abs(expectedOperating) * 1e-12);
        double expectedTotal = expectedCapital + expectedOperations + expectedOperating;
        Assert.AreEqual(expectedTotal, fixRow.TotalCostPresentValue, Math.Abs(expectedTotal) * 1e-12);
        Assert.AreEqual(expectedTotal / Annuity(30), fixRow.EquivalentAnnualCost,
            Math.Abs(expectedTotal / Annuity(30)) * 1e-12);
        Assert.AreEqual(1000d + 500d + 10d * 30 - 5d * 20, fixRow.CumulativeCost, 1e-12);

        // The identity-monetized benefit is the Total-stream present-value reduction read
        // straight off the published trajectories — bit-exact by construction.
        LifeCycleRiskResults baselineTrajectory = results.Trajectories[0];
        LifeCycleRiskResults fixTrajectory = results.Trajectories[1];
        double expectedBenefit = baselineTrajectory.PresentValueOfExpectedConsequences[0]
            - fixTrajectory.PresentValueOfExpectedConsequences[0];
        Assert.AreEqual(expectedBenefit, fixRow.MonetizedPresentValueBenefit, 0d);
        Assert.IsTrue(fixRow.MonetizedPresentValueBenefit > 0d, "The repair must reduce risk.");
        Assert.AreEqual(fixRow.MonetizedPresentValueBenefit - fixRow.TotalCostPresentValue,
            fixRow.NetPresentValue, 0d);
        Assert.AreEqual(fixRow.MonetizedPresentValueBenefit / fixRow.TotalCostPresentValue,
            fixRow.BenefitCostRatio, 0d);
        double expectedAbsorbingBenefit = baselineTrajectory.AbsorbingPresentValueOfExpectedConsequences[0]
            - fixTrajectory.AbsorbingPresentValueOfExpectedConsequences[0];
        Assert.AreEqual(expectedAbsorbingBenefit, fixRow.AbsorbingMonetizedPresentValueBenefit, 0d);

        // The survival-equivalent annualized failure probability against the independent
        // power form, and the signed reduction against the baseline row.
        double pT = fixTrajectory.FailureProbabilityByHorizon;
        double expectedAnnualized = 1d - Math.Pow(1d - pT, 1d / 30d);
        Assert.AreEqual(expectedAnnualized, fixRow.AnnualizedFailureProbability,
            Math.Abs(expectedAnnualized) * 1e-12);
        Assert.AreEqual(results.Alternatives[0].AnnualizedFailureProbability - fixRow.AnnualizedFailureProbability,
            fixRow.AnnualizedFailureProbabilityReduction, 0d);
        Assert.IsTrue(fixRow.AnnualizedFailureProbabilityReduction > 0d);

        // The trajectory points flatten every epoch of both alternatives.
        Assert.AreEqual(results.Trajectories[0].Epochs.Count + results.Trajectories[1].Epochs.Count,
            results.TrajectoryPoints.Count);
    }

    /// <summary>
    /// Verifies the run lifecycle: the veto event cancels before work, a pre-canceled token
    /// cancels between evaluations, and both leave the study unestimated with a completion
    /// event.
    /// </summary>
    [TestMethod]
    public void Test_Run_VetoAndCancellation()
    {
        // Arrange — a veto handler.
        (CostBenefitAnalysis study, _, _) = BuildStudy();
        AnalysisRunCompletedEventArgs? completion = null;
        study.AnalysisCompleted += (_, e) => completion = e;
        study.AnalysisStarting += (_, e) => e.Cancel = true;

        // Act / Assert — the veto cancels.
        Assert.ThrowsException<OperationCanceledException>(
            () => study.RunAsync().GetAwaiter().GetResult());
        Assert.IsNotNull(completion);
        Assert.IsTrue(completion!.Cancelled);
        Assert.IsFalse(study.IsEstimated);
        Assert.IsNull(study.Results);

        // A pre-canceled token cancels before the first evaluation.
        (CostBenefitAnalysis canceled, _, _) = BuildStudy();
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.ThrowsException<OperationCanceledException>(
            () => canceled.RunAsync(null, source.Token).GetAwaiter().GetResult());
        Assert.IsFalse(canceled.IsEstimated);
    }

    /// <summary>
    /// Verifies progress reporting counts distinct evaluations and completes at one hundred
    /// percent.
    /// </summary>
    [TestMethod]
    public void Test_Run_ProgressCountsEvaluations()
    {
        // Arrange — two distinct (system, plan) pairs. The reporter posts through the
        // synchronization context captured at its construction, so the test installs an
        // inline context to observe the reports deterministically.
        (CostBenefitAnalysis study, _, _) = BuildStudy();
        SynchronizationContext? original = SynchronizationContext.Current;
        var reported = new List<double>();
        try
        {
            SynchronizationContext.SetSynchronizationContext(new InlineSynchronizationContext());
            var reporter = new SafeProgressReporter();
            reporter.ProgressReported += (_, progress, _) => { lock (reported) reported.Add(progress); };

            // Act
            study.RunAsync(reporter).GetAwaiter().GetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(original);
        }

        // Assert
        Assert.AreEqual(2, reported.Count);
        Assert.AreEqual(50d, reported[0], 1e-12);
        Assert.AreEqual(100d, reported[1], 1e-12);
    }

    /// <summary>A synchronization context whose posts execute inline, for deterministic tests.</summary>
    private sealed class InlineSynchronizationContext : SynchronizationContext
    {
        /// <inheritdoc/>
        public override void Post(SendOrPostCallback d, object? state)
        {
            d(state);
        }
    }

    /// <summary>
    /// Verifies replace-to-edit semantics: assigning options, editing membership, or
    /// re-designating the baseline clears the published results.
    /// </summary>
    [TestMethod]
    public void Test_Run_ReplaceToEdit_ClearsPublishedState()
    {
        // Arrange
        (CostBenefitAnalysis study, RiskReductionAlternative baseline, _) = BuildStudy();
        study.RunAsync().GetAwaiter().GetResult();
        Assert.IsTrue(study.IsEstimated);

        // Act / Assert — options replacement clears.
        study.Options = new CostBenefitOptions(30, 0.05d);
        Assert.IsFalse(study.IsEstimated);
        Assert.IsNull(study.Results);

        // Membership edits clear.
        study.RunAsync().GetAwaiter().GetResult();
        study.Alternatives.Add(new RiskReductionAlternative("Another", baseline.System));
        Assert.IsFalse(study.IsEstimated);
        Assert.IsNull(study.Results);

        // Baseline re-designation clears.
        study.Alternatives.RemoveAt(study.Alternatives.Count - 1);
        study.RunAsync().GetAwaiter().GetResult();
        study.Baseline = baseline;
        Assert.IsFalse(study.IsEstimated);
        Assert.IsNull(study.Results);
    }

    /// <summary>
    /// Verifies author inertness and reproducibility: a study run leaves every referenced
    /// analysis byte-untouched, and two identical runs publish bit-identical economics.
    /// </summary>
    [TestMethod]
    public void Test_Run_AuthorInertness_And_Reproducibility()
    {
        // Arrange — monetized so the compared net present value is a finite number.
        (CostBenefitAnalysis study, RiskReductionAlternative baseline, RiskReductionAlternative fix) =
            BuildStudy(new CostBenefitOptions(30, 0.05d, monetization: new ConsequenceMonetization()));
        string baselineHash = Convert.ToHexString(baseline.System.Components[0].CanonicalHash());
        string fixHash = Convert.ToHexString(fix.System.Components[0].CanonicalHash());
        string baselineXml = baseline.System.Components[0].ToXElement().ToString();

        // Act
        study.RunAsync().GetAwaiter().GetResult();
        double firstNetPresentValue = study.Results!.Alternatives[1].NetPresentValue;
        double firstAnnualized = study.Results.Alternatives[1].AnnualizedFailureProbability;
        study.RunAsync().GetAwaiter().GetResult();

        // Assert — authors untouched, runs bit-identical.
        Assert.AreEqual(baselineHash, Convert.ToHexString(baseline.System.Components[0].CanonicalHash()));
        Assert.AreEqual(fixHash, Convert.ToHexString(fix.System.Components[0].CanonicalHash()));
        Assert.AreEqual(baselineXml, baseline.System.Components[0].ToXElement().ToString());
        Assert.IsFalse(baseline.System.IsEstimated, "The study must not estimate the referenced analyses.");
        Assert.AreEqual(firstNetPresentValue, study.Results!.Alternatives[1].NetPresentValue, 0d);
        Assert.AreEqual(firstAnnualized, study.Results.Alternatives[1].AnnualizedFailureProbability, 0d);
    }

    /// <summary>
    /// Verifies the formulary columns wire from the published quantities: the total expected
    /// annual cost identities against the trajectory levels, the failure-prevention ratio
    /// against the published cost and probability columns, the individual-risk proxy echo, and
    /// the skipped blocks (no life-safety type, no willingness to pay) reporting NaN and an
    /// empty band.
    /// </summary>
    [TestMethod]
    public void Test_Run_FormularyColumns_WiringAndSkippedBlocks()
    {
        // Arrange — identity monetization prices the single "$" type at one.
        (CostBenefitAnalysis study, _, _) =
            BuildStudy(new CostBenefitOptions(30, 0.05d, monetization: new ConsequenceMonetization()));

        // Act
        study.RunAsync().GetAwaiter().GetResult();
        CostBenefitResults results = study.Results!;
        AlternativeEconomics baselineRow = results.Alternatives[0];
        AlternativeEconomics fixRow = results.Alternatives[1];
        double horizonAnnuity = DiscountingSupport.AnnuityFactor(30, 0.05d);

        // Assert — TEAC = EAC + the monetized Total-stream equivalent-annual level, per
        // convention (the absorbing level derives as absorbing present value over annuity).
        Assert.AreEqual(fixRow.EquivalentAnnualCost + results.Trajectories[1].EquivalentAnnualConsequences[0],
            fixRow.TotalExpectedAnnualCost, 0d);
        Assert.AreEqual(fixRow.EquivalentAnnualCost
            + results.Trajectories[1].AbsorbingPresentValueOfExpectedConsequences[0] / horizonAnnuity,
            fixRow.AbsorbingTotalExpectedAnnualCost, 0d);

        // The failure-prevention ratio recomposes from published columns.
        Assert.AreEqual(
            (fixRow.CapitalPresentValue + fixRow.OperationsAndMaintenancePresentValue) / horizonAnnuity
                / fixRow.AnnualizedFailureProbabilityReduction,
            fixRow.CostPerStatisticalFailurePrevented, 0d);
        Assert.IsTrue(double.IsNaN(baselineRow.CostPerStatisticalFailurePrevented),
            "The baseline prevents nothing relative to itself.");

        // The individual-risk seats fall back to the survival-equivalent proxy and echo it.
        Assert.IsTrue(fixRow.IndividualRiskIsProxy);
        Assert.AreEqual(baselineRow.AnnualizedFailureProbability, fixRow.BaselineIndividualRiskUsed, 0d);
        Assert.AreEqual(fixRow.AnnualizedFailureProbability, fixRow.AlternativeIndividualRiskUsed, 0d);

        // No life-safety type and no willingness to pay: the life-saved family and the
        // disproportionality block are skipped, and the screen passes.
        Assert.IsTrue(double.IsNaN(fixRow.CostPerStatisticalLifeSavedUnadjusted));
        Assert.IsTrue(double.IsNaN(fixRow.CostPerStatisticalLifeSavedAdjusted));
        Assert.IsTrue(double.IsNaN(fixRow.AbsorbingAdjustedCostPerStatisticalLifeSaved));
        Assert.IsTrue(double.IsNaN(fixRow.DisproportionalityRatio));
        Assert.AreEqual(string.Empty, fixRow.AlarpBand);
        Assert.IsFalse(fixRow.FailsDoNoHarm);
        Assert.AreEqual(0, baselineRow.DoNoHarmOffendingTypes.Count);
    }

    /// <summary>
    /// Verifies the do-no-harm screen across its three policies on an alternative that
    /// increases Total-stream risk: Enforce and WarnOnly flag the row with the offending type
    /// named (WarnOnly also carries the advisory validation Warning), and Off leaves the
    /// screen unevaluated.
    /// </summary>
    [TestMethod]
    public void Test_Run_DoNoHarmScreen_Policies()
    {
        // Arrange — the baseline is the sound configuration; the alternative raises the
        // failure probability (house event true: 0.2 → 0.92), so Total risk increases.
        static (CostBenefitAnalysis Study, RiskReductionAlternative Worse) BuildHarmStudy(CostBenefitOptions options)
        {
            var soundBaseline = new RiskReductionAlternative("Existing condition", BuildSystem(houseState: false));
            var worse = new RiskReductionAlternative("Deferred maintenance", BuildSystem(houseState: true),
                new CostStream(new[] { new CapitalCostEntry(0, 10d) }));
            var harmStudy = new CostBenefitAnalysis(options);
            harmStudy.Alternatives.Add(soundBaseline);
            harmStudy.Alternatives.Add(worse);
            harmStudy.Baseline = soundBaseline;
            return (harmStudy, worse);
        }

        // Act / Assert — Enforce (the default): flagged, the offending type named, no
        // advisory Warning.
        (CostBenefitAnalysis study, _) = BuildHarmStudy(new CostBenefitOptions(30, 0.05d));
        (_, List<string> enforceMessages) = study.Validate();
        Assert.IsFalse(HasMessage(enforceMessages, "Warning: The do-no-harm screen is advisory only"));
        study.RunAsync().GetAwaiter().GetResult();
        Assert.IsTrue(study.Results!.Alternatives[1].FailsDoNoHarm);
        CollectionAssert.AreEqual(new[] { 0 },
            (System.Collections.ICollection)study.Results.Alternatives[1].DoNoHarmOffendingTypes);
        Assert.IsFalse(study.Results.Alternatives[0].FailsDoNoHarm, "The baseline cannot harm itself.");

        // WarnOnly: still flagged, and the advisory Warning joins the validation messages.
        (study, _) = BuildHarmStudy(new CostBenefitOptions(30, 0.05d, doNoHarm: DoNoHarmPolicy.WarnOnly));
        (_, List<string> warnMessages) = study.Validate();
        Assert.IsTrue(HasMessage(warnMessages, "Warning: The do-no-harm screen is advisory only"));
        study.RunAsync().GetAwaiter().GetResult();
        Assert.IsTrue(study.Results!.Alternatives[1].FailsDoNoHarm);

        // Off: the screen is not evaluated.
        (study, _) = BuildHarmStudy(new CostBenefitOptions(30, 0.05d, doNoHarm: DoNoHarmPolicy.Off));
        study.RunAsync().GetAwaiter().GetResult();
        Assert.IsFalse(study.Results!.Alternatives[1].FailsDoNoHarm);
        Assert.AreEqual(0, study.Results.Alternatives[1].DoNoHarmOffendingTypes.Count);
    }

    /// <summary>
    /// Verifies an epistemic-mixture alternative is refused at study validation: the study's
    /// life-cycle trajectories are mean-only quantifications, which cannot select an
    /// epistemic branch, so the refusal fires loudly before any run.
    /// </summary>
    [TestMethod]
    public void Test_Validate_EpistemicAlternative_Refused()
    {
        // Arrange — a flat epistemic fragility pair on the alternative's system.
        var flatLow = new TabularResponse
        {
            Name = "Low branch",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(0d, new Deterministic(0.1d)),
                    new UncertainOrdinate(1d, new Deterministic(0.1d)),
                },
                true, SortOrder.Ascending, false, SortOrder.None,
                UnivariateDistributionType.Deterministic),
        };
        var flatHigh = new TabularResponse
        {
            Name = "High branch",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(0d, new Deterministic(0.4d)),
                    new UncertainOrdinate(1d, new Deterministic(0.4d)),
                },
                true, SortOrder.Ascending, false, SortOrder.None,
                UnivariateDistributionType.Deterministic),
        };
        var epistemic = new CompositeResponse(new[]
        {
            new WeightedResponseFunction(flatLow, 0.5d),
            new WeightedResponseFunction(flatHigh, 0.5d),
        })
        {
            Name = "Fragility tree",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            CompositeCombinationType = CompositeCombinationType.EpistemicMixture,
        };
        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = Hazard();
        component.AddFailureMode(new FailureMode(null, null, epistemic, Consequence()));
        var epistemicSystem = new RiskAnalysis(new[] { component })
        {
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
        };
        var study = new CostBenefitAnalysis(new CostBenefitOptions(30, 0.05d));
        var baseline = new RiskReductionAlternative("Existing condition", BuildSystem(houseState: true));
        study.Alternatives.Add(baseline);
        study.Alternatives.Add(new RiskReductionAlternative("Epistemic repair", epistemicSystem));
        study.Baseline = baseline;

        // Act
        (bool isValid, List<string> messages) = study.Validate();

        // Assert
        Assert.IsFalse(isValid);
        Assert.IsTrue(HasMessage(messages,
            "Error: Alternative 'Epistemic repair' carries an epistemic-mixture composite"));
    }

    /// <summary>
    /// Verifies the Tier-1 strategy catalog wires in order over a mean-only study — the
    /// aleatory per-type family, then the economics family — with the Tier-2 and Tier-3
    /// blocks gated off by named per-alternative diagnostics, the aleatory dominance screen
    /// emitted, and the decision summary's margins reconciling against the rankings.
    /// </summary>
    [TestMethod]
    public void Test_Run_StrategyCatalog_TierOneAndGating()
    {
        // Arrange — identity monetization so the economics family carries real values.
        (CostBenefitAnalysis study, _, _) =
            BuildStudy(new CostBenefitOptions(30, 0.05d, monetization: new ConsequenceMonetization()));

        // Act
        study.RunAsync().GetAwaiter().GetResult();
        CostBenefitResults results = study.Results!;

        // Assert — the catalog order for this study: the aleatory per-type family (one
        // declared type), then TEAC, NPV, BCR, the failure-probability ranking, and the
        // constrained selection over the default objective vector (no life-safety
        // declaration, no MCDA weights).
        var expectedOrder = new[]
        {
            DecisionStrategy.ExpectedValue, DecisionStrategy.MeanPlusDispersion,
            DecisionStrategy.ConditionalValueAtRisk, DecisionStrategy.TotalExpectedAnnualCost,
            DecisionStrategy.NetPresentValue, DecisionStrategy.BenefitCostRatio,
            DecisionStrategy.AnnualizedFailureProbability, DecisionStrategy.ConstrainedSelection,
        };
        Assert.AreEqual(expectedOrder.Length, results.StrategyRankings.Count);
        for (int i = 0; i < expectedOrder.Length; i++)
        {
            Assert.AreEqual(expectedOrder[i], results.StrategyRankings[i].Strategy);
            Assert.AreEqual(1, results.StrategyRankings[i].Tier);
        }
        StrategyRanking expectedValue = results.StrategyRankings[0];
        Assert.AreEqual("Aleatory", expectedValue.Layer);
        Assert.AreEqual("aleatory-from-mean-LEC", expectedValue.Discipline);
        Assert.AreEqual(1, expectedValue.RecommendedIndex,
            "The repair reduces Total risk, so the expected-value rule recommends it.");
        Assert.AreEqual("Exact", results.StrategyRankings[3].Layer);
        StrategyRanking constrained = results.StrategyRankings[7];
        Assert.AreEqual("Present value of total cost", constrained.CriterionLabel,
            "The constrained selection ranks the default objective vector's first axis.");
        Assert.AreEqual(0, constrained.RecommendedIndex,
            "The zero-cost baseline legally wins the cost-minimizing selection.");

        // The aleatory dominance screen: one pair on the one declared type.
        Assert.AreEqual(1, results.Dominance.Count);
        Assert.AreEqual("Aleatory", results.Dominance[0].Layer);

        // Tier 2 and Tier 3 are gated off with named diagnostics; their blocks stay empty.
        Assert.AreEqual(0, results.EpistemicMeasures.Count);
        Assert.AreEqual(0, results.ChanceConstraints.Count);
        Assert.AreEqual(0, results.RegretMatrices.Count);
        int tierTwoSkips = 0;
        int tierThreeSkips = 0;
        int criterionSkips = 0;
        for (int i = 0; i < results.Diagnostics.Count; i++)
        {
            if (results.Diagnostics[i].Code == "TRC2006") tierTwoSkips++;
            if (results.Diagnostics[i].Code == "TRC2008") tierThreeSkips++;
            if (results.Diagnostics[i].Code == "TRC2007") criterionSkips++;
        }
        Assert.AreEqual(2, tierTwoSkips, "Both alternatives lack stored ensembles.");
        Assert.AreEqual(1, tierThreeSkips, "The baseline lacks an enumeration map.");
        Assert.AreEqual(2, criterionSkips,
            "Both default objectives are economics metrics with no per-realization criterion analog.");

        // The summary reconciles: one entry per ranking, margins counting non-withheld
        // recommendations, and no do-no-harm marks on this study.
        Assert.IsNotNull(results.Summary);
        Assert.AreEqual(results.StrategyRankings.Count, results.Summary.Entries.Count);
        var recomputedCounts = new int[results.Summary.AlternativeNames.Count];
        for (int i = 0; i < results.Summary.Entries.Count; i++)
        {
            DecisionSummaryEntry entry = results.Summary.Entries[i];
            if (entry.RecommendationWithheld) continue;
            for (int j = 0; j < results.Summary.AlternativeNames.Count; j++)
            {
                if (results.Summary.AlternativeNames[j] == entry.RecommendedAlternative)
                {
                    recomputedCounts[j]++;
                }
            }
        }
        CollectionAssert.AreEqual(recomputedCounts, results.Summary.RecommendationCounts.ToArray());
        Assert.IsFalse(results.Summary.FailsDoNoHarm[0]);
        Assert.IsFalse(results.Summary.FailsDoNoHarm[1]);
    }

    /// <summary>
    /// Verifies the reliability-mode catalog: consequence-dependent strategies skip whole
    /// with one named diagnostic each while the failure-probability ranking stays live and
    /// recommends the repaired configuration.
    /// </summary>
    [TestMethod]
    public void Test_Run_StrategyCatalog_ReliabilityModeSkips()
    {
        // Arrange — both systems in reliability mode.
        (CostBenefitAnalysis study, RiskReductionAlternative baseline, RiskReductionAlternative fix) =
            BuildStudy();
        baseline.System.Options.Mode = RiskAnalysisMode.Reliability;
        fix.System.Options.Mode = RiskAnalysisMode.Reliability;

        // Act
        study.RunAsync().GetAwaiter().GetResult();
        CostBenefitResults results = study.Results!;

        // Assert — only the failure-probability ranking and the cost-side constrained
        // selection survive Tier 1; the probability ranking recommends the repair (0.2
        // versus the degraded 0.92).
        Assert.AreEqual(2, results.StrategyRankings.Count);
        StrategyRanking probability = results.StrategyRankings[0];
        Assert.AreEqual(DecisionStrategy.AnnualizedFailureProbability, probability.Strategy);
        Assert.AreEqual(1, probability.RecommendedIndex);
        Assert.AreEqual(DecisionStrategy.ConstrainedSelection, results.StrategyRankings[1].Strategy,
            "The default objective vector's cost axis stays active under reliability mode.");
        Assert.AreEqual(0, results.Dominance.Count);

        bool namedExpectedValue = false;
        bool namedDominanceScreen = false;
        for (int i = 0; i < results.Diagnostics.Count; i++)
        {
            if (results.Diagnostics[i].Code != "TRC2005") continue;
            if (results.Diagnostics[i].Message.Contains("'ExpectedValue'")) namedExpectedValue = true;
            if (results.Diagnostics[i].Message.Contains("stochastic-dominance")) namedDominanceScreen = true;
        }
        Assert.IsTrue(namedExpectedValue, "Each skipped strategy is named once.");
        Assert.IsTrue(namedDominanceScreen, "The skipped aleatory screen is named.");
    }

    /// <summary>
    /// Verifies a do-no-harm exclusion flows into every ranking and the summary: the harming
    /// alternative keeps its values, is excluded from every recommendation, collects a zero
    /// margin, and is marked in the summary.
    /// </summary>
    [TestMethod]
    public void Test_Run_StrategyCatalog_DoNoHarmExclusion()
    {
        // Arrange — the sound configuration is the baseline; the alternative raises risk.
        var soundBaseline = new RiskReductionAlternative("Existing condition", BuildSystem(houseState: false));
        var worse = new RiskReductionAlternative("Deferred maintenance", BuildSystem(houseState: true),
            new CostStream(new[] { new CapitalCostEntry(0, 10d) }));
        var study = new CostBenefitAnalysis(new CostBenefitOptions(30, 0.05d,
            monetization: new ConsequenceMonetization()));
        study.Alternatives.Add(soundBaseline);
        study.Alternatives.Add(worse);
        study.Baseline = soundBaseline;

        // Act
        study.RunAsync().GetAwaiter().GetResult();
        CostBenefitResults results = study.Results!;

        // Assert
        Assert.IsTrue(results.Summary!.FailsDoNoHarm[1]);
        Assert.AreEqual(0, results.Summary.RecommendationCounts[1],
            "An excluded alternative collects no recommendations.");
        for (int i = 0; i < results.StrategyRankings.Count; i++)
        {
            Assert.IsTrue(results.StrategyRankings[i].IsExcludedFromRecommendation[1]);
            Assert.AreNotEqual(1, results.StrategyRankings[i].RecommendedIndex);
        }
    }

    /// <summary>
    /// Verifies the declared-selection strategies on a mean-only study: the constrained
    /// selection ranks the first objective under the fixed constraints, the chance-constrained
    /// selection stays behind the Tier-2 ensemble gate, and an economics objective is named as
    /// having no per-realization criterion analog.
    /// </summary>
    [TestMethod]
    public void Test_Run_StrategyCatalog_ConstrainedSelections()
    {
        // Arrange — an economics objective and one whole-horizon economics constraint.
        var objective = new ObjectiveDeclaration("Net present value",
            CostBenefitMetric.ForEconomic(EconomicMetric.NetPresentValue),
            ObjectiveDirection.Maximize);
        var constraint = new CostBenefitConstraint(
            CostBenefitMetric.ForEconomic(EconomicMetric.PresentValueOfTotalCost),
            Numerics.Mathematics.Optimization.ConstraintType.LesserThanOrEqualTo, 1e9);
        (CostBenefitAnalysis study, _, _) = BuildStudy(new CostBenefitOptions(30, 0.05d,
            monetization: new ConsequenceMonetization(),
            objectives: new[] { objective }, constraints: new[] { constraint }));

        // Act
        study.RunAsync().GetAwaiter().GetResult();
        CostBenefitResults results = study.Results!;

        // Assert — both selections publish; the chance selection echoes its confidence and
        // the absence of evaluable chance constraints.
        StrategyRanking? constrained = null;
        StrategyRanking? chance = null;
        for (int i = 0; i < results.StrategyRankings.Count; i++)
        {
            if (results.StrategyRankings[i].Strategy == DecisionStrategy.ConstrainedSelection)
            {
                constrained = results.StrategyRankings[i];
            }
            if (results.StrategyRankings[i].Strategy == DecisionStrategy.ChanceConstrainedSelection)
            {
                chance = results.StrategyRankings[i];
            }
        }
        Assert.IsNotNull(constrained);
        Assert.AreEqual("Net present value", constrained.CriterionLabel);
        Assert.AreEqual(ObjectiveDirection.Maximize, constrained.Direction);
        StringAssert.Contains(constrained.ParameterEcho, "1 fixed constraints");
        Assert.IsNull(chance,
            "Without stored ensembles the chance-constrained selection stays behind the Tier-2 gate.");

        bool namedEconomicsCriterion = false;
        for (int i = 0; i < results.Diagnostics.Count; i++)
        {
            if (results.Diagnostics[i].Code == "TRC2007"
                && results.Diagnostics[i].Message.Contains("no per-realization analog under content-based seeding"))
            {
                namedEconomicsCriterion = true;
            }
        }
        Assert.IsTrue(namedEconomicsCriterion);
    }
}
