using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Analyses;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.Systems.Components;

namespace RMC.TotalRisk.Tests.Core;

/// <summary>
/// Unit tests for the seed-stable perturbation mode
/// (docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §5.5.8) — the captured sampler
/// seed map and its pinned replay: apply(captured) reproduces a run bit-for-bit, pinning
/// isolates a parameter perturbation from seed re-rolls, shape mismatches fault loudly, and
/// the map never touches any serialization surface.
/// </summary>
[TestClass]
public class SamplerSeedMapTests
{
    /// <summary>Builds the stage-frequency hazard (0.999 → 0 ft up to 0.001 → 30 ft).</summary>
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

    /// <summary>Builds an uncertain fragility (triangular ordinates).</summary>
    private static TabularResponse UncertainFragility()
    {
        return new TabularResponse
        {
            Name = "Breach Fragility",
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

    /// <summary>Builds a full-uncertainty analysis over one component with the given failure-consequence scale.</summary>
    private static RiskAnalysis Build(double consequenceTop, int modes = 1)
    {
        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = StageFrequency();
        for (int m = 0; m < modes; m++)
        {
            component.AddFailureMode(new FailureMode(null, null, UncertainFragility(), Consequence($"Failure Loss {m + 1}", consequenceTop)));
        }
        component.AddFailureMode(new FailureMode(null, null, null, Consequence("Non-Failure Loss", 60d)));
        var analysis = new RiskAnalysis(new[] { component });
        analysis.Options.EstimateMeanRiskOnly = false;
        analysis.Options.Realizations = 100;

        // The bit-identity fixtures pin the consequence-blind refinement objective: the
        // adaptive mesh follows the configured integrand, so under a consequence-following
        // objective a consequence perturbation legitimately moves the quadrature mesh (a
        // deterministic parameter effect the seed pin does not — and must not — suppress).
        // Blind the mesh to consequences and the pinned streams imply bit-identical
        // probability outputs, which is the contract under test.
        analysis.Options.RiskIntegrand = RiskIntegrand.TotalProbabilityOfFailure;
        return analysis;
    }

    /// <summary>
    /// Verifies apply(captured) is a bit-identical replay: re-running with the run's own
    /// captured map pinned reproduces the full results JSON byte for byte, and the re-captured
    /// map equals the applied one (capture ∘ apply = identity).
    /// </summary>
    [TestMethod]
    public async Task Test_ApplyCaptured_BitIdenticalRun()
    {
        // Arrange / Act — baseline, then a pinned replay on the same content.
        var analysis = Build(300d);
        await analysis.RunAsync();
        string baseline = analysis.RiskResults!.ToJson();
        var captured = analysis.CapturedSamplerSeeds;
        Assert.IsNotNull(captured, "Every run must capture its seed map.");

        analysis.PinnedSamplerSeeds = captured;
        await analysis.RunAsync();

        // Assert
        Assert.AreEqual(baseline, analysis.RiskResults!.ToJson(), "A pinned replay of the captured map must be bit-identical.");
        Assert.AreEqual(captured!.ComponentCount, analysis.CapturedSamplerSeeds!.ComponentCount);
    }

    /// <summary>
    /// Verifies the perturbation-study contract: perturbing a consequence scale normally
    /// re-rolls every stream (the component hash moved), but pinning the baseline map holds the
    /// unperturbed quantities bit-identical — the per-realization failure probabilities match
    /// the baseline exactly while the means carry the pure parameter effect.
    /// </summary>
    [TestMethod]
    public async Task Test_PerturbationStudy_IsolatesParameterEffect()
    {
        // Arrange — the baseline and its captured map.
        var baseline = Build(300d);
        await baseline.RunAsync();
        var baselineMap = baseline.CapturedSamplerSeeds!;
        int count = baseline.RiskResults!.Count;

        double[] Apfs(RiskAnalysis analysis)
        {
            var values = new double[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = analysis.RiskResults!.Realizations[i]!.Fail.TotalProbability;
            }
            return values;
        }
        var baselineApfs = Apfs(baseline);

        // Act — the perturbed model (consequence 300 → 330) without and with the pin.
        var unpinned = Build(330d);
        await unpinned.RunAsync();
        var pinned = Build(330d);
        pinned.PinnedSamplerSeeds = baselineMap;
        await pinned.RunAsync();

        // Assert — without the pin the content-based seeds re-roll the fragility stream.
        CollectionAssert.AreNotEqual(baselineApfs, Apfs(unpinned),
            "A numeric perturbation must re-roll the streams under content-based seeding (the documented default).");

        // With the pin, the failure probabilities are bit-identical realization for
        // realization (the fragility stream is pinned and the APF never reads consequences),
        // while the mean carries the pure parameter effect.
        CollectionAssert.AreEqual(baselineApfs, Apfs(pinned),
            "The pinned run must hold every unperturbed quantity bit-identical.");
        Assert.AreNotEqual(baseline.RiskResults.Realizations[0]!.Total.Mean, pinned.RiskResults!.Realizations[0]!.Total.Mean,
            "The perturbed parameter's effect must remain visible.");
    }

    /// <summary>
    /// Verifies the shape guards: a map from a different component count throws synchronously,
    /// and a map from a different walk shape (an added failure mode) faults the run.
    /// </summary>
    [TestMethod]
    public async Task Test_ShapeMismatch_FaultsLoudly()
    {
        // Arrange — a captured single-component, single-mode map.
        var baseline = Build(300d);
        await baseline.RunAsync();
        var map = baseline.CapturedSamplerSeeds!;

        // A different component count throws before the run starts.
        var twoComponents = new RiskAnalysis(new[]
        {
            Build(300d).Components[0].Clone(),
            Build(600d).Components[0].Clone(),
        });
        twoComponents.Options.EstimateMeanRiskOnly = false;
        twoComponents.Options.Realizations = 100;
        twoComponents.PinnedSamplerSeeds = map;
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => twoComponents.RunAsync());

        // A different walk shape (two modes) faults the run — no results are published.
        var twoModes = Build(300d, modes: 2);
        twoModes.PinnedSamplerSeeds = map;
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => twoModes.RunAsync());
        Assert.IsFalse(twoModes.IsEstimated, "A walk-shape mismatch must fault the run.");
        Assert.IsNull(twoModes.RiskResults, "No results may publish from a faulted pinned run.");
    }

    /// <summary>
    /// Verifies the map never touches a serialization surface: the analysis element and the
    /// options hash are byte-identical with and without a pinned map.
    /// </summary>
    [TestMethod]
    public async Task Test_PinnedMap_NeverSerialized()
    {
        // Arrange
        var analysis = Build(300d);
        await analysis.RunAsync();
        string before = analysis.ToXElement().ToString();
        byte[] optionsHash = analysis.Options.CanonicalHash();

        // Act
        analysis.PinnedSamplerSeeds = analysis.CapturedSamplerSeeds;

        // Assert
        Assert.AreEqual(before, analysis.ToXElement().ToString(), "The pinned map must never serialize.");
        CollectionAssert.AreEqual(optionsHash, analysis.Options.CanonicalHash(), "The pinned map must never enter a hash surface.");
    }

    /// <summary>
    /// Verifies the joint seed base rides the map. The perturbation is a hashed-but-
    /// integrand-inert edit (a <see cref="SystemComponent.HazardThreshold"/> on component B):
    /// it moves the component's canonical hash — so an unpinned run re-rolls every stream —
    /// while leaving the joint VEGAS integrand's values untouched, so the pinned run must be a
    /// bit-identical replay. A consequence perturbation would NOT reproduce bit-for-bit even
    /// pinned: the joint integrand is inherently consequence-bearing, so the VEGAS importance
    /// grid re-adapts deterministically — a parameter effect, not seed noise (the §5.5.8
    /// documented residual).
    /// </summary>
    [TestMethod]
    public async Task Test_JointSeedBase_Pinned()
    {
        RiskAnalysis BuildJoint(double thresholdB)
        {
            var a = Build(300d).Components[0].Clone();
            var b = Build(600d).Components[0].Clone();
            b.Name = "Levee";
            b.HazardThreshold = thresholdB;
            var analysis = new RiskAnalysis(new[] { a, b });
            analysis.Options.EstimateMeanRiskOnly = false;
            analysis.Options.Realizations = 100;
            analysis.Options.SystemRiskMethod = SystemRiskType.JointRiskMethod;
            analysis.Options.UseDefaults = false;
            analysis.Options.WarmupEvaluations = 500;
            analysis.Options.WarmupCycles = 2;
            analysis.Options.FinalEvaluations = 1000;
            return analysis;
        }

        // Arrange — the joint baseline (no threshold declared on B).
        var baseline = BuildJoint(double.NaN);
        await baseline.RunAsync();
        var map = baseline.CapturedSamplerSeeds!;

        // Act — declare B's threshold (hash moves, integrand does not), without and with the pin.
        var unpinned = BuildJoint(15d);
        await unpinned.RunAsync();
        var pinned = BuildJoint(15d);
        pinned.PinnedSamplerSeeds = map;
        await pinned.RunAsync();

        // Assert — unpinned, the moved hash re-rolls the streams (content-based seeding).
        bool anyDiffers = false;
        for (int i = 0; i < baseline.RiskResults!.Count && !anyDiffers; i++)
        {
            anyDiffers = baseline.RiskResults.Realizations[i]!.Fail.TotalProbability
                != unpinned.RiskResults!.Realizations[i]!.Fail.TotalProbability;
        }
        Assert.IsTrue(anyDiffers, "The hash-moving perturbation must re-roll the streams when unpinned.");

        // Pinned, every stream-determined scalar holds bit-for-bit across the whole ensemble —
        // the per-component ordinal pins AND the joint VEGAS seed base both rode the map.
        for (int i = 0; i < baseline.RiskResults.Count; i++)
        {
            Assert.AreEqual(baseline.RiskResults.Realizations[i]!.Fail.TotalProbability,
                pinned.RiskResults!.Realizations[i]!.Fail.TotalProbability, 0d,
                $"The pinned joint stream must reproduce the system failure probability (realization {i}).");
            Assert.AreEqual(baseline.RiskResults.Realizations[i]!.Total.Mean,
                pinned.RiskResults!.Realizations[i]!.Total.Mean, 0d,
                $"The pinned joint stream must reproduce the system mean (realization {i}).");
        }
    }
}
