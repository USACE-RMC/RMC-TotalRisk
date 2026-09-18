using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.Systems.Components;

namespace RMC.TotalRisk.Verification.Analyses.Risk;

/// <summary>
/// Extrapolation-policy verification behind the full engine: the closed-form linearly extended
/// chain against an independent quadrature oracle, the default byte identity at engine scale,
/// the Error mode's loud surfacing through the integrator's exception absorption, the response
/// probability clamp on the extended tails, and the deliberate hash/seed/result movement of a
/// configured policy.
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// <para>
/// <b>Scenario:</b> a deterministic stage-frequency curve on the normal-Z probability axis —
/// non-exceedance z is piecewise linear through (0 ft, z(0.001)), (10 ft, 0), (30 ft, z(0.999))
/// — a non-saturating fragility (10 ft → 0.1, 20 ft → 0.8, linear probability axis), and a
/// linear consequence (0 ft → $0, 30 ft → $1000). Every extension is therefore a hand-computed
/// line: the hazard extends linearly in z (its inverse tails widen the integration domain to
/// the engine's 1e-16 probability floors), the fragility extends linearly in probability and
/// clamps to [0, 1] (crossing 1 at 22.857 ft and 0 at 8.571 ft), and the consequence extends
/// linearly with the negative tail clamped at zero.
/// </para>
/// <para>
/// <b>Oracle and tolerances:</b> the oracle re-implements the extended chain from Numerics
/// primitives only — the piecewise-linear z(x) map and its inverse, the clamped extended
/// fragility and consequence lines — and integrates E = ∫ SRP(x(u))·C(x(u)) du by the
/// trapezoid rule on a uniform two-million-interval grid over [1e-16, 1 − 1e-16] plus the two
/// endpoint rectangles (the engine's exhaustive-mass treatment). The integrand is bounded and
/// piecewise smooth, so the discretization error is far below the asserted 1e-5 relative
/// tolerance, which dominates the engine's 1e-8 integration tolerance. Byte-identity and
/// movement pins are exact (delta 0) on the results JSON, the manifest hash, and the captured
/// draws.
/// </para>
/// </remarks>
[TestClass]
public class ExtrapolationVerification
{
    /// <summary>The ensemble size for the movement pins (≥ 100 per the engine-fixture rule).</summary>
    private const int Realizations = 200;

    /// <summary>The engine's probability floor (the sampled-domain clamp).</summary>
    private const double Floor = 1e-16;

    /// <summary>The hazard table's stage ordinates.</summary>
    private static readonly double[] Stages = { 0d, 10d, 30d };

    /// <summary>The hazard table's non-exceedance probabilities at <see cref="Stages"/>.</summary>
    private static readonly double[] NonExceedance = { 0.001d, 0.5d, 0.999d };

    /// <summary>Builds the deterministic scenario, applying one policy to the whole chain.</summary>
    private static RiskAnalysis Build(ExtrapolationPolicy policy, bool uncertainConsequence = false)
    {
        var hazard = new TabularHazard
        {
            Name = "Stage Frequency",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            Extrapolation = policy,
            NoUncertaintyFunction = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(1d - NonExceedance[0], new Deterministic(Stages[0])),
                    new UncertainOrdinate(1d - NonExceedance[1], new Deterministic(Stages[1])),
                    new UncertainOrdinate(1d - NonExceedance[2], new Deterministic(Stages[2])),
                },
                true, SortOrder.Descending, true, SortOrder.Ascending, UnivariateDistributionType.Deterministic),
        };
        var fragility = new TabularResponse
        {
            Name = "Fragility",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            Extrapolation = policy,
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(10d, new Deterministic(0.1d)), new UncertainOrdinate(20d, new Deterministic(0.8d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
        var consequence = new TabularConsequence
        {
            Name = "Failure Loss",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
            Extrapolation = policy,
            UncertainOrderedPairedData = uncertainConsequence
                ? new UncertainOrderedPairedData(
                    new[]
                    {
                        new UncertainOrdinate(0d, new Normal(0d, 0d)),
                        new UncertainOrdinate(30d, new Normal(1000d, 100d)),
                    },
                    true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Normal)
                : new UncertainOrderedPairedData(
                    new[]
                    {
                        new UncertainOrdinate(0d, new Deterministic(0d)),
                        new UncertainOrdinate(30d, new Deterministic(1000d)),
                    },
                    true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };

        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = hazard;
        component.AddFailureMode(new FailureMode(null, null, fragility, consequence));
        var analysis = new RiskAnalysis(new[] { component });
        if (uncertainConsequence)
        {
            analysis.Options.EstimateMeanRiskOnly = false;
            analysis.Options.Realizations = Realizations;
        }
        return analysis;
    }

    /// <summary>The piecewise-linear non-exceedance z at a stage, extended per policy.</summary>
    private static double ZOfStage(double x, bool extend)
    {
        var z = new double[Stages.Length];
        for (int i = 0; i < z.Length; i++) z[i] = Normal.StandardZ(NonExceedance[i]);
        if (x <= Stages[0])
            return extend ? z[0] + (z[1] - z[0]) / (Stages[1] - Stages[0]) * (x - Stages[0]) : z[0];
        if (x >= Stages[^1])
            return extend ? z[^1] + (z[^1] - z[^2]) / (Stages[^1] - Stages[^2]) * (x - Stages[^1]) : z[^1];
        int k = x <= Stages[1] ? 0 : 1;
        return z[k] + (z[k + 1] - z[k]) / (Stages[k + 1] - Stages[k]) * (x - Stages[k]);
    }

    /// <summary>The stage at a non-exceedance probability (the inverse map), extended per policy.</summary>
    private static double StageOfU(double u, bool extend)
    {
        var z = new double[Stages.Length];
        for (int i = 0; i < z.Length; i++) z[i] = Normal.StandardZ(NonExceedance[i]);
        double zu = Normal.StandardZ(u);
        if (zu <= z[0])
            return extend ? Stages[0] + (Stages[1] - Stages[0]) / (z[1] - z[0]) * (zu - z[0]) : Stages[0];
        if (zu >= z[^1])
            return extend ? Stages[^1] + (Stages[^1] - Stages[^2]) / (z[^1] - z[^2]) * (zu - z[^1]) : Stages[^1];
        int k = zu <= z[1] ? 0 : 1;
        return Stages[k] + (Stages[k + 1] - Stages[k]) / (z[k + 1] - z[k]) * (zu - z[k]);
    }

    /// <summary>The clamped extended fragility line: (10 → 0.1), (20 → 0.8).</summary>
    private static double Srp(double x, bool extend)
    {
        double p = extend
            ? 0.1d + (0.8d - 0.1d) / 10d * (x - 10d)
            : x <= 10d ? 0.1d : x >= 20d ? 0.8d : 0.1d + (0.8d - 0.1d) / 10d * (x - 10d);
        return p < 0d ? 0d : p > 1d ? 1d : p;
    }

    /// <summary>The clamped extended consequence line: (0 → 0), (30 → 1000).</summary>
    private static double Loss(double x, bool extend)
    {
        double c = extend
            ? 1000d / 30d * x
            : x <= 0d ? 0d : x >= 30d ? 1000d : 1000d / 30d * x;
        return c < 0d ? 0d : c;
    }

    /// <summary>
    /// The independent chain integral E = ∫ f(x(u)) du by trapezoid over [Floor, 1 − Floor]
    /// plus the engine's two endpoint rectangles.
    /// </summary>
    private static double Integrate(Func<double, double> f, bool extend)
    {
        const int intervals = 2_000_000;
        double lo = Floor;
        double hi = 1d - Floor;
        double h = (hi - lo) / intervals;
        double sum = 0.5d * (f(StageOfU(lo, extend)) + f(StageOfU(hi, extend)));
        for (int i = 1; i < intervals; i++)
        {
            sum += f(StageOfU(lo + i * h, extend));
        }
        double interior = sum * h;
        // The endpoint rectangles: mass lo below the support and (1 − hi) above it.
        return interior + lo * f(StageOfU(lo, extend)) + (1d - hi) * f(StageOfU(hi, extend));
    }

    /// <summary>Applies one policy to the scenario's whole chain on a built analysis.</summary>
    private static void SetChainPolicy(RiskAnalysis analysis, ExtrapolationPolicy policy)
    {
        var component = analysis.Components[0];
        ((TabularHazard)component.HazardFunction!).Extrapolation = policy;
        ((TabularResponse)component.FailureModes[0].ResponseFunction).Extrapolation = policy;
        ((TabularConsequence)component.FailureModes[0].ConsequenceFunctions[0]).Extrapolation = policy;
    }

    /// <summary>
    /// Verifies the closed-form extended chain behind the full engine: the mean-only expected
    /// annual consequence and annual failure probability match the independent extended-curve
    /// oracle, and both exceed their endpoint-hold companions (the tails add mass).
    /// </summary>
    [TestMethod]
    public void Test_ExtendedChain_MeanOnly_VsOracle()
    {
        // Arrange / Act
        var held = Build(ExtrapolationPolicy.None);
        held.RunAsync().GetAwaiter().GetResult();
        var extended = Build(ExtrapolationPolicy.Both);
        extended.RunAsync().GetAwaiter().GetResult();

        double heldEad = held.RiskResults![0]!.Total.Mean;
        double extendedEad = extended.RiskResults![0]!.Total.Mean;
        double heldAfp = held.RiskResults[0]!.Fail.TotalProbability;
        double extendedAfp = extended.RiskResults[0]!.Fail.TotalProbability;

        // Assert — the oracle at 1e-5 relative (oracle discretization dominates; see remarks).
        double oracleHeldEad = Integrate(x => Srp(x, false) * Loss(x, false), extend: false);
        double oracleExtendedEad = Integrate(x => Srp(x, true) * Loss(x, true), extend: true);
        double oracleHeldAfp = Integrate(x => Srp(x, false), extend: false);
        double oracleExtendedAfp = Integrate(x => Srp(x, true), extend: true);
        Assert.AreEqual(oracleHeldEad, heldEad, 1e-5 * oracleHeldEad, "Held EAD vs oracle.");
        Assert.AreEqual(oracleExtendedEad, extendedEad, 1e-5 * oracleExtendedEad, "Extended EAD vs oracle.");
        Assert.AreEqual(oracleHeldAfp, heldAfp, 1e-5 * oracleHeldAfp, "Held AFP vs oracle.");
        Assert.AreEqual(oracleExtendedAfp, extendedAfp, 1e-5 * oracleExtendedAfp, "Extended AFP vs oracle.");

        // The policy moves results materially and the engine agrees with the oracle's direction.
        // On this chain the extension REDUCES risk: the held fragility keeps its 0.1 plateau
        // below the table, while the extension descends through zero at 8.571 ft — the low-stage
        // mass it sheds outweighs the upper-tail gain. Extension is a modeling statement, not a
        // conservatism knob; the oracle carries the sign.
        Assert.AreEqual(oracleExtendedEad > oracleHeldEad, extendedEad > heldEad,
            "The engine must agree with the oracle on the direction of the movement.");
        Assert.IsTrue(Math.Abs(extendedEad - heldEad) > 1e-3 * heldEad,
            "The configured policy must move the expected consequence materially.");
        Assert.IsTrue(Math.Abs(extendedAfp - heldAfp) > 1e-3 * heldAfp,
            "The configured policy must move the failure probability materially.");
    }

    /// <summary>
    /// Verifies the response probability clamp on the extended tails at engine scale: beyond
    /// 22.857 ft the extended fragility contributes exactly the hazard mass (probability one),
    /// so the extended AFP equals the oracle whose upper clamp region integrates to the exact
    /// u-measure above that crossing.
    /// </summary>
    [TestMethod]
    public void Test_ResponseClamp_ExtendedTails()
    {
        // Arrange / Act
        var extended = Build(ExtrapolationPolicy.Both);
        extended.RunAsync().GetAwaiter().GetResult();
        double afp = extended.RiskResults![0]!.Fail.TotalProbability;

        // Assert — split the oracle at the clamp crossings: the region above x = 22.857 ft
        // contributes 1 − F(22.857) exactly (the clamp at one), the region below x = 8.571 ft
        // contributes zero (the clamp at zero), and the middle is the line.
        double upperCrossing = 10d + (1d - 0.1d) * 10d / 0.7d;   // p(x) = 1
        double lowerCrossing = 10d - 0.1d * 10d / 0.7d;          // p(x) = 0
        double uUpper = Normal.StandardCDF(ZOfStage(upperCrossing, extend: true));
        double uLower = Normal.StandardCDF(ZOfStage(lowerCrossing, extend: true));
        double middle = Integrate(x => x > lowerCrossing && x < upperCrossing ? Srp(x, true) : 0d, extend: true);
        double oracle = middle + (1d - uUpper);
        Assert.IsTrue(uLower > 0d && uUpper < 1d, "The clamp crossings must sit inside the extended domain.");
        Assert.AreEqual(oracle, afp, 1e-5 * oracle, "The clamped extended AFP must match the split oracle.");
        Assert.IsTrue(afp < 1d, "The clamped AFP stays a probability.");
    }

    /// <summary>
    /// Verifies the default byte identity at engine scale: a model with the policy left unset
    /// and one with every policy explicitly set to None publish byte-identical results JSON —
    /// mean-only and at ensemble scale.
    /// </summary>
    [TestMethod]
    public void Test_DefaultNone_ByteIdentical_EngineScale()
    {
        // Mean-only: the companion sets every policy to Both and clears it back to None before
        // running, so the pin covers the set-then-clear path, not just the untouched default.
        var unset = Build(ExtrapolationPolicy.None);
        unset.RunAsync().GetAwaiter().GetResult();
        var cleared = Build(ExtrapolationPolicy.Both);
        SetChainPolicy(cleared, ExtrapolationPolicy.None);
        cleared.RunAsync().GetAwaiter().GetResult();
        Assert.AreEqual(unset.RiskResults!.ToJson(), cleared.RiskResults!.ToJson(),
            "Set-then-cleared None must publish byte-identical mean-only results.");

        // Ensemble scale with an uncertain consequence: seeds, draws, and every realization.
        var ensembleUnset = Build(ExtrapolationPolicy.None, uncertainConsequence: true);
        ensembleUnset.RunAsync().GetAwaiter().GetResult();
        var ensembleCleared = Build(ExtrapolationPolicy.Both, uncertainConsequence: true);
        SetChainPolicy(ensembleCleared, ExtrapolationPolicy.None);
        ensembleCleared.RunAsync().GetAwaiter().GetResult();
        Assert.AreEqual(ensembleUnset.RiskResults!.ToJson(), ensembleCleared.RiskResults!.ToJson(),
            "Set-then-cleared None must publish byte-identical ensemble results.");
    }

    /// <summary>
    /// Verifies the Error mode surfaces loudly through the integrator's exception absorption:
    /// an Error-mode fragility narrower than the hazard domain stops the run with the
    /// integration-failure message carrying the function name and its table range.
    /// </summary>
    [TestMethod]
    public void Test_ErrorMode_LoudSurfacing()
    {
        // Arrange — only the fragility refuses; the hazard's own domain spans [0, 30] ft, so
        // integrand evaluations below 10 ft hit the guard inside the absorbed integrand.
        var analysis = Build(ExtrapolationPolicy.None);
        ((TabularResponse)analysis.Components[0].FailureModes[0].ResponseFunction).Extrapolation = ExtrapolationPolicy.Error;

        // Act / Assert
        var fault = Assert.ThrowsException<InvalidOperationException>(
            () => analysis.RunAsync().GetAwaiter().GetResult());
        StringAssert.Contains(fault.Message, "Fragility");
        StringAssert.Contains(fault.Message, "[10, 20]");
        StringAssert.Contains(fault.Message, "extrapolation policy");
    }

    /// <summary>
    /// Verifies the deliberate movement of a configured policy: the analysis content hash moves,
    /// the owning function's sampling stream re-rolls (per-realization draws differ), and the
    /// ensemble mean lands on the independent extended oracle — while the None companion stays
    /// on the held oracle.
    /// </summary>
    [TestMethod]
    public void Test_PolicyEdit_MovesHashSeedsAndResults()
    {
        // Arrange / Act — the uncertain-consequence ensemble under None and under Both.
        var held = Build(ExtrapolationPolicy.None, uncertainConsequence: true);
        held.RunAsync().GetAwaiter().GetResult();
        var extended = Build(ExtrapolationPolicy.Both, uncertainConsequence: true);
        extended.RunAsync().GetAwaiter().GetResult();

        // The manifest's analysis content hash moves (the policy is hashed compute content).
        Assert.AreNotEqual(held.RiskResults!.Manifest!.AnalysisContentHash,
            extended.RiskResults!.Manifest!.AnalysisContentHash,
            "A configured policy must move the analysis content hash.");

        // The consequence stream re-rolls: the first realization's draw-driven mean differs by
        // more than the deterministic extension ratio alone would produce is not asserted —
        // the exact pin is that the per-realization sequence is not a scaled copy: compare the
        // normalized first-realization means (draw identity would keep them equal because the
        // linear map scales out).
        double heldRatio = held.RiskResults[0]!.Total.Mean / held.RiskResults.Summary!.Mean.Total.Mean;
        double extendedRatio = extended.RiskResults[0]!.Total.Mean / extended.RiskResults.Summary!.Mean.Total.Mean;
        Assert.AreNotEqual(heldRatio, extendedRatio,
            "The policy edit must re-roll the consequence stream (a preserved stream would keep the normalized draw identical).");

        // The ensemble means land on the two oracles: the sampled top ordinate averages 1000,
        // so the ensemble mean tracks the deterministic oracle within 4·σ/√N (σ = E·0.1).
        double oracleHeld = Integrate(x => Srp(x, false) * Loss(x, false), extend: false);
        double oracleExtended = Integrate(x => Srp(x, true) * Loss(x, true), extend: true);
        Assert.AreEqual(oracleHeld, held.RiskResults.Summary!.Mean.Total.Mean,
            4d * oracleHeld * 0.1d / Math.Sqrt(Realizations), "Held ensemble mean vs oracle.");
        Assert.AreEqual(oracleExtended, extended.RiskResults.Summary!.Mean.Total.Mean,
            4d * oracleExtended * 0.1d / Math.Sqrt(Realizations), "Extended ensemble mean vs oracle.");
    }
}
