using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Analyses.LifeCycle;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.Results.LifeCycle;
using RMC.TotalRisk.Results.Risk;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.RiskFunctions.Transforms;
using RMC.TotalRisk.Systems.Components;

namespace RMC.TotalRisk.Verification.Analyses.LifeCycle;

/// <summary>
/// Life-cycle verification, greenfield: the deteriorating response's transform-equivalence
/// bit-oracle across ages, realization-for-realization knowledge parity on one content-seeded
/// stream, the age-zero base identity, an engine-level mean-only twin against the re-authored
/// base-plus-shift-transform model, the family reproducibility pin, and the trajectory query's
/// anchors — the stationary bridge onto the exposure-period conversions, the two-epoch closed
/// form with independent annuity and survival arithmetic on every carried consequence stream,
/// the background-split closed form separating the Total, Excess, and Fail streams with the
/// retained per-epoch measure surface, per-epoch re-authored configuration twins, the
/// deterioration-monotone/intervention-drop trajectory, and the author byte pin.
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// <para>
/// <b>Scenario tables.</b> The base fragility tabulates a Normal(150, 20) capacity CDF over
/// stages −10 to 310 at unit steps (321 ordinates); the uncertain variant carries per-ordinate
/// Uniform(p ± 0.05) probabilities clamped to [0, 1]. The deterioration law maps ages
/// {0, 25, 50, 100} to shifts {0, 5, 12, 30}; the uncertain variant pins age zero at an exact
/// zero (the degenerate uniform) and spreads later shifts Uniform(0.8Δ, 1.2Δ). Probe ages
/// {0, 10, 25, 60, 150} cover the age-zero identity, interpolated ages, a knot, and the
/// boundary hold; probe hazards span 60 to 260. The engine fixture drives a five-ordinate
/// deterministic stage-frequency hazard (exceedance 0.999 to 0.001 over stages 60 to 260) and
/// a five-knot deterministic stage-damage consequence through a single failure mode plus the
/// non-failure complement.
/// </para>
/// <para>
/// <b>Equivalence contract.</b> A deteriorating response at age t is exactly its base behind a
/// deterministic unit-slope linear transform with intercept Δ(t): with slope one the transform
/// computes Δ + h and the wrapper computes h + Δ, bit-identical in IEEE arithmetic, and both
/// paths evaluate the same sampled base product. The engine twin re-authors the wrapper model
/// as base-plus-stage-transform and must reproduce the annual failure probability, the expected
/// annual consequence, and the loss-exceedance ordinates with no delta — the twins' differing
/// canonical hashes and seeds are provably inert because every fixture function is
/// deterministic and the runs are mean-only (the configuration-risk family's twin discipline).
/// </para>
/// <para>
/// <b>Oracle mechanics.</b> The one-stream parity test seeds the wrapper once (64 realizations,
/// Latin hypercube, one fixed seed) and reconstructs every realization independently: the base's
/// product at its own child-stream row i composed with the law sampled at the wrapper's own
/// percentile for row i, interpolated at the probe age — proving the same state of knowledge
/// serves every age. The reproducibility pin runs the full-uncertainty wrapper scenario twice
/// from independently built models and compares the published ensemble and mean JSON byte
/// streams.
/// </para>
/// <para>
/// <b>The trajectory anchors.</b> The stationary bridge pins the life-cycle aggregates against
/// the exposure-period conversions on an all-deterministic model with the integration
/// discipline pinned in-test at the smallest legal ensemble (one hundred realizations):
/// deterministic functions make every realization bit-equal to the mean pass, so the
/// conversions reduce one hundred identical values — every percentile slot interpolates
/// identical order statistics (x + f·(x − x) = x, exact) and asserts with no delta, while the
/// mean slot sums one hundred identical doubles, whose partial-sum rounding admits a relative
/// error of order the count times machine epsilon; it asserts at 1e-13 relative, documented.
/// The two-epoch closed form drives the flat OR(AND(house, 0.375), 0.2)
/// tree — baseline probability 0.2, configured exactly 0.5 — and recomputes the horizon
/// aggregates with independent power-form annuities and a per-year survival loop, for the
/// Total stream and its Excess and Fail twins (which coincide analytically on that
/// background-free model). The background-split closed form adds a flat non-failure mode —
/// failure consequence 1000, background 100, so the epoch stream means are exactly
/// Fail = 1000p, NonFail = 100(1 − p), Total = 1000p + 100(1 − p), Excess = 900p, and
/// Background = 100 — retains each epoch's realization, and checks Total = Excess +
/// Background on the retained curves, the retained means against the epoch rows with no
/// delta, the per-stream aggregates against the independent loop on the closed-form means,
/// and retention's aggregate inertness against a retention-free twin query. The
/// configuration twins re-author each epoch's cumulative state directly (the
/// configuration-risk family's twin discipline). The trajectory test drives the deteriorating
/// wrapper through ages {0, 10, 20, 30} and drops the load with a milder replacement hazard.
/// </para>
/// <para>
/// <b>Tolerances.</b> Deterministic identities assert bit-exact (no-delta equality). The
/// two-epoch closed form allows 1e-10 absolute on probabilities (quadrature exactness of the
/// flat fixture compounded through tenth powers), 1e-10 on the factored consequence ratio, and
/// 1e-12 relative against the independent annuity and survival arithmetic; the
/// background-split closed form allows 1e-9 relative on the flat stream means (the
/// integrator's 1e-8 relative discipline is quadrature-exact on flat integrands, leaving
/// rounding), 1e-12 relative on the retained-curve stream identity, and 1e-9 relative on the
/// aggregates recomputed from the closed-form means (the mean error dominates the loop's
/// rounding); each derivation is documented on its assert.
/// </para>
/// </remarks>
[TestClass]
public class LifeCycleVerification
{
    #region Fixtures

    /// <summary>The one fixed stream seed the family samples from.</summary>
    private const int StreamSeed = 20260906;

    /// <summary>The probe ages: the age-zero identity, interpolated ages, a knot, and the hold.</summary>
    private static readonly double[] ProbeAges = [0d, 10d, 25d, 60d, 150d];

    /// <summary>The probe hazards spanning the fragility range.</summary>
    private static readonly double[] ProbeHazards =
        [60d, 80d, 100d, 110d, 120d, 130d, 140d, 145d, 150d, 155d, 160d, 170d, 180d, 200d, 220d, 240d, 260d];

    /// <summary>Builds the tabulated Normal(150, 20) fragility base.</summary>
    /// <param name="uncertain">Whether the ordinates carry Uniform(p ± 0.05) uncertainty.</param>
    /// <returns>The base response.</returns>
    private static TabularResponse FragilityTable(bool uncertain)
    {
        var capacity = new Normal(150d, 20d);
        var ordinates = new List<UncertainOrdinate>();
        for (double x = -10d; x <= 310d; x += 1d)
        {
            double p = capacity.CDF(x);
            ordinates.Add(uncertain
                ? new UncertainOrdinate(x, new Uniform(Math.Max(0d, p - 0.05d), Math.Min(1d, p + 0.05d)))
                : new UncertainOrdinate(x, new Deterministic(p)));
        }
        return new TabularResponse
        {
            Name = "Capacity fragility",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(ordinates, true, SortOrder.Ascending,
                false, SortOrder.None,
                uncertain ? UnivariateDistributionType.Uniform : UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds the deterministic deterioration law: ages {0, 25, 50, 100} → shifts {0, 5, 12, 30}.</summary>
    /// <returns>The law.</returns>
    private static UncertainOrderedPairedData StandardLaw()
    {
        return new UncertainOrderedPairedData(
            new[]
            {
                new UncertainOrdinate(0d, new Deterministic(0d)),
                new UncertainOrdinate(25d, new Deterministic(5d)),
                new UncertainOrdinate(50d, new Deterministic(12d)),
                new UncertainOrdinate(100d, new Deterministic(30d)),
            },
            true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic);
    }

    /// <summary>
    /// Builds the uncertain law: an exact zero at age zero (the degenerate uniform), later
    /// shifts Uniform(0.8Δ, 1.2Δ).
    /// </summary>
    /// <returns>The law.</returns>
    private static UncertainOrderedPairedData UncertainLaw()
    {
        return new UncertainOrderedPairedData(
            new[]
            {
                new UncertainOrdinate(0d, new Uniform(0d, 0d)),
                new UncertainOrdinate(25d, new Uniform(4d, 6d)),
                new UncertainOrdinate(50d, new Uniform(9.6d, 14.4d)),
                new UncertainOrdinate(100d, new Uniform(24d, 36d)),
            },
            true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Uniform);
    }

    /// <summary>Builds a labeled wrapper over the given base and law.</summary>
    /// <param name="baseResponse">The base response.</param>
    /// <param name="law">The deterioration law.</param>
    /// <returns>The wrapper.</returns>
    private static DeterioratingResponse Wrapper(IResponseFunction baseResponse, UncertainOrderedPairedData law)
    {
        return new DeterioratingResponse
        {
            Name = "Aging fragility",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            BaseResponse = baseResponse,
            DeteriorationLaw = law,
        };
    }

    /// <summary>Builds the unit-slope shift transform with intercept Δ and wide bounds.</summary>
    /// <param name="shift">The intercept Δ.</param>
    /// <returns>The transform.</returns>
    private static LinearTransform ShiftTransform(double shift)
    {
        return new LinearTransform
        {
            Name = "Deterioration shift",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            TransformedHazard = "Stage",
            TransformedHazardUnit = "ft",
            Alpha = shift,
            Beta = 1d,
            IsUncertain = false,
            Minimum = -1e9d,
            Maximum = 1e9d,
        };
    }

    /// <summary>Builds the deterministic five-ordinate stage-frequency hazard over stages 60 to 260.</summary>
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
                    new UncertainOrdinate(0.5d, new Deterministic(120d)),
                    new UncertainOrdinate(0.1d, new Deterministic(160d)),
                    new UncertainOrdinate(0.01d, new Deterministic(200d)),
                    new UncertainOrdinate(0.001d, new Deterministic(260d)),
                },
                true, SortOrder.Descending, true, SortOrder.Ascending,
                UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds the deterministic five-knot stage-damage consequence.</summary>
    /// <returns>The consequence.</returns>
    private static TabularConsequence StageDamages()
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
                    new UncertainOrdinate(60d, new Deterministic(0d)),
                    new UncertainOrdinate(100d, new Deterministic(100d)),
                    new UncertainOrdinate(140d, new Deterministic(500d)),
                    new UncertainOrdinate(200d, new Deterministic(2000d)),
                    new UncertainOrdinate(250d, new Deterministic(5000d)),
                },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds a single-component analysis whose failure mode carries the given stage chain.</summary>
    /// <param name="stageTransforms">The stage transforms ahead of the response; null for none.</param>
    /// <param name="response">The stage response.</param>
    /// <param name="consequencePosition">
    /// The consequence hazard position, or null for the last-response-input default; position 0
    /// binds the consequences to the raw hazard ahead of any stage transform.
    /// </param>
    /// <returns>The analysis.</returns>
    private static RiskAnalysis BuildAnalysis(List<ITransformFunction>? stageTransforms, IResponseFunction response,
        int? consequencePosition = null)
    {
        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = StageFrequency();
        var mode = new FailureMode(stageTransforms, null, response, StageDamages())
        {
            ConsequenceHazardPosition = consequencePosition,
        };
        component.AddFailureMode(mode);
        return new RiskAnalysis(new[] { component })
        {
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
        };
    }

    #endregion

    /// <summary>
    /// The transform-equivalence bit-oracle across ages: the wrapper at every probe age equals
    /// the base behind the authored unit-slope shift transform with intercept Δ(age), with no
    /// delta at every probe hazard, for the deterministic base and the uncertain base at its
    /// mean.
    /// </summary>
    [TestMethod]
    public void Test_FunctionLevel_TransformEquivalence_BitExact_AcrossAges()
    {
        foreach (bool uncertain in new[] { false, true })
        {
            // Arrange
            var baseResponse = FragilityTable(uncertain);
            var wrapper = Wrapper(baseResponse, StandardLaw());
            var reference = baseResponse.SampleFunction();

            foreach (double age in ProbeAges)
            {
                // Act — the authored twin transform carries the identical resolved shift.
                double shift = wrapper.DeteriorationLaw.CurveSample().GetYFromX(age);
                var transform = ShiftTransform(shift).SampleFunction();
                var sampled = wrapper.SampleFunctionAtAge(age);

                // Assert — bit-exact: Δ + h with unit slope is h + Δ in IEEE arithmetic.
                foreach (double h in ProbeHazards)
                {
                    Assert.AreEqual(reference.CDF(transform.Function(h)), sampled.CDF(h),
                        $"uncertain {uncertain}, age {age}, hazard {h}");
                }
            }
        }
    }

    /// <summary>
    /// Realization-for-realization knowledge parity on one stream: after a single sampler setup,
    /// every realization at every probe age equals the independently reconstructed composition of
    /// the base's own child-stream row and the law at the wrapper's sampled percentile — the same
    /// state of knowledge serves every age.
    /// </summary>
    [TestMethod]
    public void Test_FunctionLevel_UncertaintyParity_RealizationForRealization_OneStream()
    {
        // Arrange — one setup serves every age.
        var baseResponse = FragilityTable(uncertain: true);
        var wrapper = Wrapper(baseResponse, UncertainLaw());
        wrapper.SetupSampler(64, StreamSeed, SamplingScheme.LatinHypercube);

        for (int i = 0; i < 64; i++)
        {
            // Act — the independent reconstruction for realization i.
            var reference = baseResponse.SampleFunction(i);
            double lawPercentile = wrapper.SampledPercentile(i, 0);
            var sampledLaw = wrapper.DeteriorationLaw.CurveSample(lawPercentile);

            foreach (double age in ProbeAges)
            {
                double shift = sampledLaw.GetYFromX(age);
                var sampled = wrapper.SampleFunctionAtAge(i, age);

                // Assert
                foreach (double h in ProbeHazards)
                {
                    Assert.AreEqual(reference.CDF(h + shift), sampled.CDF(h),
                        $"realization {i}, age {age}, hazard {h}");
                }
            }
        }
    }

    /// <summary>
    /// The age-zero identity: with a zero shift at age zero, the wrapper reproduces its base
    /// bit-for-bit, realization for realization, on the same stream.
    /// </summary>
    [TestMethod]
    public void Test_FunctionLevel_AgeZero_EquivalentToBase()
    {
        // Arrange
        var baseResponse = FragilityTable(uncertain: true);
        var wrapper = Wrapper(baseResponse, UncertainLaw());
        wrapper.SetupSampler(64, StreamSeed, SamplingScheme.LatinHypercube);

        for (int i = 0; i < 64; i++)
        {
            // Act
            var sampled = wrapper.SampleFunctionAtAge(i, 0d);
            var reference = baseResponse.SampleFunction(i);

            // Assert
            foreach (double h in ProbeHazards)
            {
                Assert.AreEqual(reference.CDF(h), sampled.CDF(h), $"realization {i}, hazard {h}");
            }
        }
    }

    /// <summary>
    /// The engine-level mean-only twin: a model whose failure mode carries the wrapper at age 50
    /// reproduces the re-authored model carrying the base behind a stage shift transform with
    /// intercept 12 — annual failure probability, expected annual consequence, and the
    /// loss-exceedance ordinates, all with no delta.
    /// </summary>
    [TestMethod]
    public void Test_Engine_MeanOnlyTwin_BitExact()
    {
        // Arrange — run A: the wrapper at age 50.
        var wrapper = Wrapper(FragilityTable(uncertain: false), StandardLaw());
        wrapper.EvaluationAge = 50d;
        var wrapperRun = BuildAnalysis(null, wrapper);

        // Arrange — run B: the re-authored base behind the equivalent stage transform. The
        // shift is internal to the response path, so the twin pins its consequence input to the
        // raw hazard (position 0) — the wrapper model's binding — rather than the transformed
        // signal the last-response-input default would select.
        var twinRun = BuildAnalysis(
            new List<ITransformFunction> { ShiftTransform(12d) },
            FragilityTable(uncertain: false),
            consequencePosition: 0);

        // Act
        wrapperRun.RunAsync().GetAwaiter().GetResult();
        twinRun.RunAsync().GetAwaiter().GetResult();

        // Assert — bit-exact twins (deterministic functions make the differing seeds inert).
        var wrapperCurves = wrapperRun.MeanRiskResults!.Curves;
        var twinCurves = twinRun.MeanRiskResults!.Curves;
        Assert.AreEqual(twinCurves.Fail.TotalProbability, wrapperCurves.Fail.TotalProbability);
        Assert.AreEqual(twinCurves.Total.Mean, wrapperCurves.Total.Mean);
        CollectionAssert.AreEqual(twinCurves.Total.LECConsequences, wrapperCurves.Total.LECConsequences);
        CollectionAssert.AreEqual(twinCurves.Total.LECProbabilities, wrapperCurves.Total.LECProbabilities);

        // The deterioration is material: the aged model fails more than the base would.
        var baselineRun = BuildAnalysis(null, FragilityTable(uncertain: false));
        baselineRun.RunAsync().GetAwaiter().GetResult();
        Assert.IsTrue(wrapperCurves.Fail.TotalProbability
            > baselineRun.MeanRiskResults!.Curves.Fail.TotalProbability);
    }

    /// <summary>
    /// The family reproducibility pin: the full-uncertainty wrapper scenario run twice from
    /// independently built models publishes byte-identical ensemble and mean JSON.
    /// </summary>
    [TestMethod]
    public void Test_Engine_Reproducibility_SameSeedBitIdentical()
    {
        // Arrange
        static RiskAnalysis Build()
        {
            var wrapper = Wrapper(FragilityTable(uncertain: true), UncertainLaw());
            wrapper.EvaluationAge = 50d;
            var analysis = BuildAnalysis(null, wrapper);
            analysis.Options.EstimateMeanRiskOnly = false;
            analysis.Options.Realizations = 128;
            return analysis;
        }
        var first = Build();
        var second = Build();

        // Act
        first.RunAsync().GetAwaiter().GetResult();
        second.RunAsync().GetAwaiter().GetResult();

        // Assert — byte-identical published results.
        Assert.AreEqual(first.RiskResults!.ToJson(), second.RiskResults!.ToJson());
        Assert.AreEqual(first.MeanRiskResults!.ToJson(), second.MeanRiskResults!.ToJson());
    }

    #region Trajectory fixtures

    /// <summary>
    /// Builds the flat OR(AND(house, 0.375), 0.2) fault-tree response: baseline failure
    /// probability 0.2, configured exactly 1 − 0.8 · 0.625 = 0.5.
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

    /// <summary>Builds a milder stage-frequency hazard: every stage shifted down twenty feet.</summary>
    /// <returns>The hazard.</returns>
    private static TabularHazard MilderStageFrequency()
    {
        return new TabularHazard
        {
            Name = "Mitigated stage frequency",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            NoUncertaintyFunction = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(0.999d, new Deterministic(40d)),
                    new UncertainOrdinate(0.5d, new Deterministic(100d)),
                    new UncertainOrdinate(0.1d, new Deterministic(140d)),
                    new UncertainOrdinate(0.01d, new Deterministic(180d)),
                    new UncertainOrdinate(0.001d, new Deterministic(240d)),
                },
                true, SortOrder.Descending, true, SortOrder.Ascending,
                UnivariateDistributionType.Deterministic),
        };
    }

    #endregion

    /// <summary>
    /// The stationary bridge: on an all-deterministic model with the integration discipline
    /// pinned in-test at the smallest legal ensemble (one hundred realizations), every
    /// realization is bit-equal to the mean pass, so the exposure-period conversions'
    /// percentile slots collapse onto the life-cycle aggregates with no delta — while the mean
    /// slot, a sum of one hundred identical doubles, agrees within its documented
    /// summation-rounding bound (1e-13 relative over the count-times-epsilon estimate) —
    /// discounted and undiscounted.
    /// </summary>
    [TestMethod]
    public void Test_LifeCycle_StationaryMatchesExposurePeriod_BitExact()
    {
        // Arrange — the discipline pin makes ensemble realizations integrate exactly like the
        // mean pass, so the conversions reduce one hundred identical values.
        var author = BuildAnalysis(null, FragilityTable(uncertain: false));
        author.Options.UseDefaults = false;
        author.Options.Tolerance = 1e-8d;
        author.Options.EnsembleTolerance = 1e-8d;
        author.Options.EnsembleMinDepth = 2;
        author.Options.EstimateMeanRiskOnly = false;
        author.Options.Realizations = 100;
        author.RunAsync().GetAwaiter().GetResult();

        // Every slot of one conversion interval against one life-cycle aggregate: the three
        // percentile slots interpolate identical order statistics (x + f·(x − x) = x) and
        // assert with no delta; the mean slot allows the summation-rounding bound.
        static void AssertInterval(ExposurePeriodInterval interval, double aggregate, string label)
        {
            Assert.AreEqual(interval.Lower, aggregate, $"{label} lower");
            Assert.AreEqual(interval.Median, aggregate, $"{label} median");
            Assert.AreEqual(interval.Upper, aggregate, $"{label} upper");
            Assert.AreEqual(interval.Mean, aggregate, Math.Abs(aggregate) * 1e-13d, $"{label} mean");
        }

        foreach (double rate in new[] { 0.035d, 0d })
        {
            // Act
            ExposurePeriodRiskResults? conversions = author.MeasureExposurePeriodRisk(50, rate);
            LifeCycleRiskResults trajectory = author.MeasureLifeCycleRisk(new LifeCycleDefinition(50, rate));

            // Assert — the shared exact expression shapes, one epoch spanning the horizon.
            Assert.IsNotNull(conversions);
            Assert.AreEqual(1, trajectory.Epochs.Count);
            AssertInterval(conversions!.PeriodFailureProbability,
                trajectory.FailureProbabilityByHorizon, $"rate {rate} probability");
            AssertInterval(conversions.CumulativeExpectedConsequence,
                trajectory.CumulativeExpectedConsequences[0], $"rate {rate} cumulative");
            AssertInterval(conversions.PresentValueOfExpectedConsequences,
                trajectory.PresentValueOfExpectedConsequences[0], $"rate {rate} present value");
            AssertInterval(conversions.EquivalentAnnualConsequence,
                trajectory.EquivalentAnnualConsequences[0], $"rate {rate} equivalent annual");
        }
    }

    /// <summary>
    /// The two-epoch closed form: the flat tree's exact probabilities (0.2 baseline, 0.5
    /// configured at year ten of twenty), the factored consequence ratio, and the horizon
    /// aggregates recomputed with independent power-form annuities and a per-year survival
    /// loop, discounted at five percent and undiscounted.
    /// </summary>
    [TestMethod]
    public void Test_LifeCycle_TwoEpochClosedForm_Exact()
    {
        // Arrange
        var response = FlatFaultResponse(houseState: false, out Guid houseId);
        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = StageFrequency();
        component.AddFailureMode(new FailureMode(null, null, response, StageDamages()));
        var author = new RiskAnalysis(new[] { component })
        {
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
        };
        var schedule = new[]
        {
            new LifeCycleIntervention(10,
                new[] { new HouseEventState(response.Id, houseId, true) }),
        };

        foreach (double rate in new[] { 0.05d, 0d })
        {
            // Act
            LifeCycleRiskResults trajectory = author.MeasureLifeCycleRisk(
                new LifeCycleDefinition(20, rate, null, schedule));

            // Assert — the flat probabilities are quadrature-exact: 1e-10 absolute absorbs the
            // integrator's 1e-8 relative discipline compounded through the tenth powers.
            double p1 = trajectory.Epochs[0].System.FailureProbability;
            double p2 = trajectory.Epochs[1].System.FailureProbability;
            Assert.AreEqual(0.2d, p1, 1e-10d);
            Assert.AreEqual(0.5d, p2, 1e-10d);
            Assert.AreEqual(1d - Math.Pow(0.8d, 10) * Math.Pow(0.5d, 10),
                trajectory.FailureProbabilityByHorizon, 1e-10d);

            // The flat response factors out of the consequence integral: m2/m1 = 0.5/0.2.
            double m1 = trajectory.Epochs[0].System.ExpectedConsequences[0];
            double m2 = trajectory.Epochs[1].System.ExpectedConsequences[0];
            Assert.AreEqual(2.5d, m2 / m1, 1e-10d);

            // Independent annuity arithmetic (power form, 1e-12 relative).
            double annuity10 = rate > 0d ? (1d - Math.Pow(1d + rate, -10)) / rate : 10d;
            double annuity20 = rate > 0d ? (1d - Math.Pow(1d + rate, -20)) / rate : 20d;
            Assert.AreEqual(10d * m1 + 10d * m2, trajectory.CumulativeExpectedConsequences[0],
                Math.Abs(trajectory.CumulativeExpectedConsequences[0]) * 1e-12d);
            Assert.AreEqual(m1 * annuity10 + m2 * (annuity20 - annuity10),
                trajectory.PresentValueOfExpectedConsequences[0],
                Math.Abs(trajectory.PresentValueOfExpectedConsequences[0]) * 1e-12d);
            Assert.AreEqual(trajectory.PresentValueOfExpectedConsequences[0] / annuity20,
                trajectory.EquivalentAnnualConsequences[0],
                Math.Abs(trajectory.EquivalentAnnualConsequences[0]) * 1e-12d);

            // The absorbing aggregates against an independent per-year survival loop.
            double survival = 1d;
            double absorbingCumulative = 0d;
            double absorbingPresent = 0d;
            for (int year = 1; year <= 20; year++)
            {
                double p = year <= 10 ? p1 : p2;
                double mean = year <= 10 ? m1 : m2;
                absorbingCumulative += survival * mean;
                absorbingPresent += survival * mean * Math.Pow(1d + rate, -year);
                survival *= 1d - p;
            }
            Assert.AreEqual(absorbingCumulative, trajectory.AbsorbingCumulativeExpectedConsequences[0],
                Math.Abs(absorbingCumulative) * 1e-12d);
            Assert.AreEqual(absorbingPresent, trajectory.AbsorbingPresentValueOfExpectedConsequences[0],
                Math.Abs(absorbingPresent) * 1e-12d);

            // Undiscounted, the present value is the cumulative — with no delta.
            if (rate == 0d)
            {
                Assert.AreEqual(trajectory.CumulativeExpectedConsequences[0],
                    trajectory.PresentValueOfExpectedConsequences[0]);
            }

            // The stream axis on this background-free model: with no non-failure
            // consequences, the Excess and Fail streams analytically coincide with Total
            // (Total = Excess + Background holds with a zero Background), so every epoch's
            // stream means agree to accumulation rounding (1e-12 relative) and the
            // per-stream horizon aggregates reproduce the shared accumulation from the rows'
            // own stream means with no delta.
            var streamAggregates = new double[2, 4];
            double streamLogSurvival = 0d;
            double discountBase = 1d / (1d + rate);
            foreach (LifeCycleEpochRisk epoch in trajectory.Epochs)
            {
                double excessMean = epoch.System.ExcessExpectedConsequences[0];
                double failMean = epoch.System.FailExpectedConsequences[0];
                double totalMean = epoch.System.ExpectedConsequences[0];
                Assert.AreEqual(totalMean, excessMean, Math.Abs(totalMean) * 1e-12d);
                Assert.AreEqual(totalMean, failMean, Math.Abs(totalMean) * 1e-12d);

                double p = epoch.System.FailureProbability;
                int span = epoch.SpanYears;
                double annuityEnd = rate > 0d ? (1d - Math.Pow(1d + rate, -epoch.EndYear)) / rate : epoch.EndYear;
                double annuityStart = rate > 0d ? (1d - Math.Pow(1d + rate, -epoch.StartYear)) / rate : epoch.StartYear;
                double survivalAtStart = Math.Exp(streamLogSurvival);
                double survivalYears = p > 0d ? -(Math.Pow(1d - p, span) - 1d) / p : span;
                double x = (1d - p) * discountBase;
                double geometric = x == 1d ? span : (1d - Math.Pow(x, span)) / (1d - x);
                double firstYearDiscount = Math.Pow(1d + rate, -(epoch.StartYear + 1));
                double[] means = [excessMean, failMean];
                for (int s = 0; s < 2; s++)
                {
                    streamAggregates[s, 0] += span * means[s];
                    streamAggregates[s, 1] += means[s] * (annuityEnd - annuityStart);
                    streamAggregates[s, 2] += means[s] * survivalAtStart * survivalYears;
                    streamAggregates[s, 3] += means[s] * survivalAtStart * firstYearDiscount * geometric;
                }
                streamLogSurvival += span * Math.Log(1d - p);
            }
            double annuityHorizon = rate > 0d ? (1d - Math.Pow(1d + rate, -20)) / rate : 20d;
            Assert.AreEqual(streamAggregates[0, 0], trajectory.ExcessCumulativeExpectedConsequences[0],
                Math.Abs(streamAggregates[0, 0]) * 1e-12d);
            Assert.AreEqual(streamAggregates[0, 1], trajectory.ExcessPresentValueOfExpectedConsequences[0],
                Math.Abs(streamAggregates[0, 1]) * 1e-12d);
            Assert.AreEqual(streamAggregates[0, 1] / annuityHorizon, trajectory.ExcessEquivalentAnnualConsequences[0],
                Math.Abs(streamAggregates[0, 1] / annuityHorizon) * 1e-12d);
            Assert.AreEqual(streamAggregates[0, 2], trajectory.AbsorbingExcessCumulativeExpectedConsequences[0],
                Math.Abs(streamAggregates[0, 2]) * 1e-12d);
            Assert.AreEqual(streamAggregates[0, 3], trajectory.AbsorbingExcessPresentValueOfExpectedConsequences[0],
                Math.Abs(streamAggregates[0, 3]) * 1e-12d);
            Assert.AreEqual(streamAggregates[1, 0], trajectory.FailCumulativeExpectedConsequences[0],
                Math.Abs(streamAggregates[1, 0]) * 1e-12d);
            Assert.AreEqual(streamAggregates[1, 1], trajectory.FailPresentValueOfExpectedConsequences[0],
                Math.Abs(streamAggregates[1, 1]) * 1e-12d);
            Assert.AreEqual(streamAggregates[1, 3], trajectory.AbsorbingFailPresentValueOfExpectedConsequences[0],
                Math.Abs(streamAggregates[1, 3]) * 1e-12d);
        }
    }

    /// <summary>
    /// The background-split closed form: a flat failure consequence of 1000 over a flat
    /// non-failure background of 100 separates every stream exactly — per epoch,
    /// Fail = 1000p, Total = 1000p + 100(1 − p), Excess = 900p, Background = 100 — so the
    /// epoch rows' stream means pin to closed forms, the retained realizations carry the
    /// Total = Excess + Background identity and reproduce the rows with no delta, the
    /// per-stream horizon aggregates match the independent annuity and survival loop on the
    /// closed-form means, and retention itself never moves an aggregate.
    /// </summary>
    [TestMethod]
    public void Test_LifeCycle_BackgroundSplitStreams_ClosedFormAndRetention()
    {
        // Arrange — the flat tree (0.2 baseline, 0.5 configured at year ten of twenty) with a
        // flat failure consequence and a flat non-failure background so every stream mean is
        // closed-form: Fail = 1000p, NonFail = 100(1 − p), Total = 1000p + 100(1 − p),
        // Excess = 900p, Background = 100.
        static TabularConsequence FlatConsequence(string name, double value)
        {
            return new TabularConsequence
            {
                Name = name,
                SpecifiedHazard = "Stage",
                HazardUnit = "ft",
                SpecifiedConsequence = "Damages",
                ConsequenceUnit = "$",
                UncertainOrderedPairedData = new UncertainOrderedPairedData(
                    new[]
                    {
                        new UncertainOrdinate(40d, new Deterministic(value)),
                        new UncertainOrdinate(260d, new Deterministic(value)),
                    },
                    true, SortOrder.Ascending, false, SortOrder.None,
                    UnivariateDistributionType.Deterministic),
            };
        }

        var response = FlatFaultResponse(houseState: false, out Guid houseId);
        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = StageFrequency();
        component.AddFailureMode(new FailureMode(null, null, response, FlatConsequence("Failure damages", 1000d)));
        component.AddFailureMode(new FailureMode(null, null, new NonFailResponse { Name = "Background" },
            FlatConsequence("Background damages", 100d)));
        var author = new RiskAnalysis(new[] { component })
        {
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
        };
        var schedule = new[]
        {
            new LifeCycleIntervention(10, new[] { new HouseEventState(response.Id, houseId, true) }),
        };
        double rate = 0.05d;

        // Act — the retained trajectory and its retention-free twin query.
        LifeCycleRiskResults retained = author.MeasureLifeCycleRisk(
            new LifeCycleDefinition(20, rate, null, schedule, retainEpochRealizations: true));
        LifeCycleRiskResults plain = author.MeasureLifeCycleRisk(
            new LifeCycleDefinition(20, rate, null, schedule));

        // Assert — the closed-form stream means per epoch: the flat integrands are
        // quadrature-exact under the integrator's 1e-8 relative discipline, so 1e-9 relative
        // absorbs rounding.
        double[] probabilities = [0.2d, 0.5d];
        for (int k = 0; k < 2; k++)
        {
            double p = probabilities[k];
            LifeCycleEpochEntry entry = retained.Epochs[k].System;
            Assert.AreEqual(p, entry.FailureProbability, 1e-10d);
            Assert.AreEqual(1000d * p + 100d * (1d - p), entry.ExpectedConsequences[0],
                Math.Abs(1000d * p + 100d * (1d - p)) * 1e-9d);
            Assert.AreEqual(900d * p, entry.ExcessExpectedConsequences[0], 900d * p * 1e-9d);
            Assert.AreEqual(1000d * p, entry.FailExpectedConsequences[0], 1000d * p * 1e-9d);

            // The retained realization is the row's own measure surface: the stream means it
            // carries are the values the row copied (no delta), the per-sample workspace is
            // dropped, and Total = Excess + Background holds to accumulation rounding.
            SystemRealization? realization = retained.Epochs[k].Realization;
            Assert.IsNotNull(realization);
            Assert.AreEqual(0, realization.Curves.Total.RiskPoints.Count);
            Assert.AreEqual(entry.ExpectedConsequences[0], realization.Curves.Total.Mean);
            Assert.AreEqual(entry.ExcessExpectedConsequences[0], realization.Curves.Excess.Mean);
            Assert.AreEqual(entry.FailExpectedConsequences[0], realization.Curves.Fail.Mean);
            Assert.AreEqual(realization.Curves.Excess.Mean + realization.Curves.Background.Mean,
                realization.Curves.Total.Mean, Math.Abs(realization.Curves.Total.Mean) * 1e-12d);
            Assert.AreEqual(100d, realization.Curves.Background.Mean, 100d * 1e-9d);
        }

        // The per-stream horizon aggregates against the independent annuity and survival
        // loop on the closed-form means (1e-9 relative: the mean error dominates).
        static double Annuity(int years, double r) => r > 0d
            ? (1d - Math.Pow(1d + r, -years)) / r
            : years;
        double[][] streamMeans =
        [
            [1000d * 0.2d + 100d * 0.8d, 1000d * 0.5d + 100d * 0.5d],
            [900d * 0.2d, 900d * 0.5d],
            [1000d * 0.2d, 1000d * 0.5d],
        ];
        double[][] published =
        [
            [retained.PresentValueOfExpectedConsequences[0], retained.CumulativeExpectedConsequences[0],
                retained.AbsorbingPresentValueOfExpectedConsequences[0]],
            [retained.ExcessPresentValueOfExpectedConsequences[0], retained.ExcessCumulativeExpectedConsequences[0],
                retained.AbsorbingExcessPresentValueOfExpectedConsequences[0]],
            [retained.FailPresentValueOfExpectedConsequences[0], retained.FailCumulativeExpectedConsequences[0],
                retained.AbsorbingFailPresentValueOfExpectedConsequences[0]],
        ];
        for (int s = 0; s < 3; s++)
        {
            double m1 = streamMeans[s][0];
            double m2 = streamMeans[s][1];
            double presentValue = m1 * Annuity(10, rate) + m2 * (Annuity(20, rate) - Annuity(10, rate));
            double cumulative = 10d * m1 + 10d * m2;
            double survival = 1d;
            double absorbingPresent = 0d;
            for (int year = 1; year <= 20; year++)
            {
                double p = year <= 10 ? 0.2d : 0.5d;
                double mean = year <= 10 ? m1 : m2;
                absorbingPresent += survival * mean * Math.Pow(1d + rate, -year);
                survival *= 1d - p;
            }
            Assert.AreEqual(presentValue, published[s][0], Math.Abs(presentValue) * 1e-9d);
            Assert.AreEqual(cumulative, published[s][1], Math.Abs(cumulative) * 1e-9d);
            Assert.AreEqual(absorbingPresent, published[s][2], Math.Abs(absorbingPresent) * 1e-9d);
        }

        // Retention never moves a number: the retention-free twin query publishes the same
        // aggregates with no delta, and carries no realizations.
        Assert.AreEqual(retained.PresentValueOfExpectedConsequences[0], plain.PresentValueOfExpectedConsequences[0]);
        Assert.AreEqual(retained.ExcessPresentValueOfExpectedConsequences[0], plain.ExcessPresentValueOfExpectedConsequences[0]);
        Assert.AreEqual(retained.FailPresentValueOfExpectedConsequences[0], plain.FailPresentValueOfExpectedConsequences[0]);
        Assert.AreEqual(retained.AbsorbingPresentValueOfExpectedConsequences[0], plain.AbsorbingPresentValueOfExpectedConsequences[0]);
        Assert.AreEqual(retained.FailureProbabilityByHorizon, plain.FailureProbabilityByHorizon);
        Assert.IsNull(plain.Epochs[0].Realization);
        Assert.IsNull(plain.Epochs[1].Realization);
    }

    /// <summary>
    /// The per-epoch configuration twins: each scheduled epoch — the baseline, the configured
    /// house event, and the configured house event compounded with a hazard replacement — is
    /// bit-equal to a directly re-authored mean-only model of that cumulative state.
    /// </summary>
    [TestMethod]
    public void Test_LifeCycle_EpochsMatchReauthoredTwins_BitExact()
    {
        // Arrange — the authored model plus one twin per cumulative epoch state.
        var authorHazard = StageFrequency();
        var response = FlatFaultResponse(houseState: false, out Guid houseId);
        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = authorHazard;
        component.AddFailureMode(new FailureMode(null, null, response, StageDamages()));
        var author = new RiskAnalysis(new[] { component })
        {
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
        };

        static RiskAnalysis Twin(bool houseState, bool milderHazard)
        {
            var twinComponent = new SystemComponent { Name = "Dam" };
            twinComponent.HazardFunction = milderHazard ? MilderStageFrequency() : StageFrequency();
            twinComponent.AddFailureMode(new FailureMode(null, null,
                FlatFaultResponse(houseState, out _), StageDamages()));
            return new RiskAnalysis(new[] { twinComponent })
            {
                SpecifiedConsequence = "Damages",
                ConsequenceUnit = "$",
            };
        }
        var twins = new[] { Twin(false, false), Twin(true, false), Twin(true, true) };
        foreach (RiskAnalysis twin in twins) twin.RunAsync().GetAwaiter().GetResult();

        // Act — the house event at year ten, the replacement compounding at year twenty.
        LifeCycleRiskResults trajectory = author.MeasureLifeCycleRisk(new LifeCycleDefinition(30, 0d, null,
            new[]
            {
                new LifeCycleIntervention(10, new[] { new HouseEventState(response.Id, houseId, true) }),
                new LifeCycleIntervention(20, null,
                    new[] { new HazardReplacement(authorHazard.Id, MilderStageFrequency()) }),
            }));

        // Assert — every epoch row equals its re-authored twin bit-for-bit (deterministic
        // functions make the twins' differing seeds inert).
        for (int k = 0; k < 3; k++)
        {
            var meanResults = twins[k].MeanRiskResults!;
            Assert.AreEqual(meanResults.Curves.Fail.TotalProbability,
                trajectory.Epochs[k].System.FailureProbability, $"epoch {k}");
            Assert.AreEqual(meanResults.Curves.Total.Mean,
                trajectory.Epochs[k].System.ExpectedConsequences[0], $"epoch {k}");
            Assert.AreEqual(meanResults.Components[0].Curves.Fail.TotalProbability,
                trajectory.Epochs[k].Components[0].FailureProbability, $"epoch {k}");
            Assert.AreEqual(meanResults.Components[0].Curves.Total.Mean,
                trajectory.Epochs[k].Components[0].ExpectedConsequences[0], $"epoch {k}");
        }
    }

    /// <summary>
    /// The trajectory shape: under a monotone deterioration law the epoch failure
    /// probabilities never decrease, and a milder replacement hazard at year twenty drops the
    /// intervened trajectory strictly below the unintervened one from that year on.
    /// </summary>
    [TestMethod]
    public void Test_LifeCycle_DeteriorationMonotone_InterventionDrops()
    {
        // Arrange — the deteriorating wrapper behind the engine fixture.
        var authorHazard = StageFrequency();
        var wrapper = Wrapper(FragilityTable(uncertain: false), StandardLaw());
        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = authorHazard;
        component.AddFailureMode(new FailureMode(null, null, wrapper, StageDamages()));
        var author = new RiskAnalysis(new[] { component })
        {
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
        };
        var evaluationYears = new[] { 10, 20, 30 };

        // Act
        LifeCycleRiskResults aging = author.MeasureLifeCycleRisk(
            new LifeCycleDefinition(40, 0d, evaluationYears));
        LifeCycleRiskResults intervened = author.MeasureLifeCycleRisk(
            new LifeCycleDefinition(40, 0d, evaluationYears, new[]
            {
                new LifeCycleIntervention(20, null,
                    new[] { new HazardReplacement(authorHazard.Id, MilderStageFrequency()) }),
            }));

        // Assert — monotone aging, and the intervention drops the aged trajectory.
        for (int k = 1; k < aging.Epochs.Count; k++)
        {
            Assert.IsTrue(aging.Epochs[k].System.FailureProbability
                >= aging.Epochs[k - 1].System.FailureProbability, $"epoch {k}");
        }
        Assert.IsTrue(aging.Epochs[1].System.FailureProbability
            > aging.Epochs[0].System.FailureProbability,
            "The weakening law must raise the failure probability with age.");
        for (int k = 0; k < 2; k++)
        {
            Assert.AreEqual(aging.Epochs[k].System.FailureProbability,
                intervened.Epochs[k].System.FailureProbability,
                $"epoch {k} precedes the intervention and must be untouched");
        }
        for (int k = 2; k < 4; k++)
        {
            Assert.IsTrue(intervened.Epochs[k].System.FailureProbability
                < aging.Epochs[k].System.FailureProbability,
                $"epoch {k} must drop under the milder hazard");
        }
    }

    /// <summary>
    /// The author byte pin: a full-uncertainty run's published results, the component hash, and
    /// the authored references are byte-identical after a trajectory query exercising a house
    /// event, a hazard replacement, and per-epoch deterioration ages.
    /// </summary>
    [TestMethod]
    public void Test_LifeCycle_AuthorFullRun_ByteUntouched()
    {
        // Arrange — an uncertain wrapper model published at two hundred realizations.
        var authorHazard = StageFrequency();
        var wrapper = Wrapper(FragilityTable(uncertain: true), UncertainLaw());
        var response = FlatFaultResponse(houseState: false, out Guid houseId);
        var damComponent = new SystemComponent { Name = "Dam" };
        damComponent.HazardFunction = authorHazard;
        damComponent.AddFailureMode(new FailureMode(null, null, wrapper, StageDamages()));
        var gateComponent = new SystemComponent { Name = "Gate" };
        gateComponent.HazardFunction = authorHazard;
        gateComponent.AddFailureMode(new FailureMode(null, null, response, StageDamages()));
        var author = new RiskAnalysis(new[] { damComponent, gateComponent })
        {
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
        };
        author.Options.EstimateMeanRiskOnly = false;
        author.Options.Realizations = 200;
        author.RunAsync().GetAwaiter().GetResult();
        string publishedJson = author.RiskResults!.ToJson();
        string damHash = Convert.ToHexString(author.Components[0].CanonicalHash());
        string gateHash = Convert.ToHexString(author.Components[1].CanonicalHash());

        // Act
        author.MeasureLifeCycleRisk(new LifeCycleDefinition(30, 0.035d, new[] { 5 }, new[]
        {
            new LifeCycleIntervention(10, new[] { new HouseEventState(response.Id, houseId, true) }),
            new LifeCycleIntervention(20, null,
                new[] { new HazardReplacement(authorHazard.Id, MilderStageFrequency()) }),
        }));

        // Assert — nothing authored or published moves a byte.
        Assert.IsTrue(author.IsEstimated, "The query must not invalidate the published results.");
        Assert.AreEqual(publishedJson, author.RiskResults!.ToJson());
        Assert.AreEqual(damHash, Convert.ToHexString(author.Components[0].CanonicalHash()));
        Assert.AreEqual(gateHash, Convert.ToHexString(author.Components[1].CanonicalHash()));
        Assert.IsTrue(ReferenceEquals(authorHazard, author.Components[0].HazardFunction));
        Assert.AreEqual(0d, wrapper.EvaluationAge, "The authored wrapper must keep its age.");
        Assert.IsFalse(((FaultTreeHouseEventNode)response.FaultTree.FindById(houseId)!).State);
    }
}
