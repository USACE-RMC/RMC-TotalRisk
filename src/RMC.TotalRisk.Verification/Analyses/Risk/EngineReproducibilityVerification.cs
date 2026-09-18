using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.RiskFunctions;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.Systems.Components;
using RMC.TotalRisk.Systems.Components.Graph;

namespace RMC.TotalRisk.Verification.Analyses.Risk;

/// <summary>
/// Engine reproducibility verification — the v1 seed-dependency bug regression at the analysis
/// level: metadata edits (renames, descriptions, fresh ids) and serialization round trips can
/// never move Monte Carlo results, repeated runs are bit-identical (the parallel realization
/// loop uses index-owned writes and sequential reductions, so thread-schedule variation across
/// runs exercises the determinism claim), and a numeric edit moves the results.
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// <para>
/// v1.0 handed each component a seed from a master PRNG iterated in canvas order, so dragging a
/// node on the canvas changed the results. v1.1 seeds derive from
/// (analysis seed, component canonical hash, occurrence index) with per-function ordinals — the
/// identity surface is content, never presentation. These pins compare full results JSON byte
/// streams, the strongest equality the results contract can express.
/// </para>
/// </remarks>
[TestClass]
public class EngineReproducibilityVerification
{
    /// <summary>The ensemble size for the reproducibility pins (thread scheduling varies freely at this size).</summary>
    private const int Realizations = 200;

    /// <summary>Builds the standard uncertain single-component analysis.</summary>
    private static RiskAnalysis Build()
    {
        // A deterministic tabulated stage-frequency curve (Normal(100, 20) quantiles on a dense
        // z-grid); the epistemic spread comes from the fragility and consequence ordinates.
        int knotCount = 65;
        var hazardOrdinates = new UncertainOrdinate[knotCount];
        for (int i = 0; i < knotCount; i++)
        {
            double z = -8d + i * 0.25d;
            hazardOrdinates[i] = new UncertainOrdinate(1d - Normal.StandardCDF(z), new Deterministic(100d + 20d * z));
        }
        var hazard = new TabularHazard
        {
            Name = "Stage Frequency",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            ProbabilityTransform = Transform.None,
            NoUncertaintyFunction = new UncertainOrderedPairedData(hazardOrdinates,
                true, SortOrder.Descending, true, SortOrder.Ascending, UnivariateDistributionType.Deterministic),
        };
        var fragility = new TabularResponse
        {
            Name = "Breach Fragility",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(100d, new Triangular(0d, 0.02d, 0.05d)),
                    new UncertainOrdinate(140d, new Triangular(0.3d, 0.5d, 0.7d)),
                    new UncertainOrdinate(180d, new Triangular(0.9d, 0.97d, 1d)),
                },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Triangular),
        };
        var consequence = new TabularConsequence
        {
            Name = "Life Loss",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Life Loss",
            ConsequenceUnit = "lives",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(60d, new Normal(0d, 0d)),
                    new UncertainOrdinate(200d, new Normal(1000d, 150d)),
                },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Normal),
        };

        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = hazard;
        component.AddFailureMode(new FailureMode(null, null, fragility, consequence));
        var analysis = new RiskAnalysis(new[] { component }) { Name = "Reproducibility" };
        analysis.Options.EstimateMeanRiskOnly = false;
        analysis.Options.Realizations = Realizations;
        return analysis;
    }

    /// <summary>Runs an analysis and captures its results as JSON byte streams.</summary>
    private static (string Ensemble, string Mean) Run(RiskAnalysis analysis)
    {
        analysis.RunAsync().GetAwaiter().GetResult();
        Assert.IsTrue(analysis.IsEstimated, "The run must succeed.");
        return (analysis.RiskResults!.ToJson(), analysis.MeanRiskResults!.ToJson());
    }

    /// <summary>
    /// Same content, three runs: the full results JSON must be byte-identical each time. Each
    /// run's parallel loop partitions work differently across threads, so repeated equality is
    /// the practical any-thread-count pin (writes are index-owned; reductions sequential).
    /// </summary>
    [TestMethod]
    public void Test_Reproducibility_RepeatedRuns_BitIdentical()
    {
        // Arrange / Act
        var first = Run(Build());
        var second = Run(Build());
        var third = Run(Build());

        // Assert
        Assert.AreEqual(first.Ensemble, second.Ensemble, "Run 2 ensemble diverged.");
        Assert.AreEqual(first.Ensemble, third.Ensemble, "Run 3 ensemble diverged.");
        Assert.AreEqual(first.Mean, second.Mean, "Run 2 mean curves diverged.");
        Assert.AreEqual(first.Mean, third.Mean, "Run 3 mean curves diverged.");
    }

    /// <summary>
    /// Metadata edits — renaming everything, editing descriptions, assigning fresh ids, and a
    /// serialization round trip — must leave every computed number byte-identical: presentation
    /// is not identity. With the end-state labels, the ensemble JSON carries the stamped
    /// end-state <c>Name</c> and <c>PathLabel</c> display fields, which legitimately echo the
    /// CURRENT function names (labels are display metadata by design, never identity), so the
    /// comparison strips exactly those two fields and asserts every remaining byte — the whole
    /// numeric surface — is identical.
    /// </summary>
    [TestMethod]
    public void Test_Reproducibility_MetadataEdits_BitIdentical()
    {
        // Arrange — baseline.
        var baselineAnalysis = Build();
        var baseline = Run(baselineAnalysis);

        // Act — a heavily re-labeled equivalent, round-tripped through serialization.
        var edited = Build();
        edited.Name = "Completely Different Analysis Name";
        edited.Description = "Edited description";
        var component = (SystemComponent)edited.Components[0];
        component.Name = "Renamed Dam";
        var functions = new System.Collections.Generic.List<RMC.TotalRisk.Core.Interfaces.IRiskFunction>(component.GetReferencedFunctions());
        foreach (var function in functions)
        {
            function.Name = $"{function.Name} (renamed)";
            function.Description = "Edited";
            function.AssignNewId();
        }
        var roundTripped = new RiskAnalysis(
            new[] { new SystemComponent(component.ToXElement()) }, edited.ToXElement());
        roundTripped.Options.EstimateMeanRiskOnly = false;
        roundTripped.Options.Realizations = Realizations;
        var result = Run(roundTripped);

        // Assert — the numeric surfaces are byte-identical. The stamped display labels (the
        // end-state Name/PathLabel fields) echo the renamed functions by design and are
        // stripped from both sides; every other byte of the ensemble JSON must match.
        static string StripDisplayLabels(string json) => System.Text.RegularExpressions.Regex.Replace(
            json, "\"(Name|PathLabel)\":\"[^\"]*\",?", string.Empty);
        Assert.AreEqual(StripDisplayLabels(baseline.Ensemble), StripDisplayLabels(result.Ensemble),
            "Metadata edits or the round trip moved the ensemble's numeric surface.");
        var baselineTotal = baselineAnalysis.MeanRiskResults!.Curves.Total;
        var resultTotal = roundTripped.MeanRiskResults!.Curves.Total;
        CollectionAssert.AreEqual(baselineTotal.LECConsequences, resultTotal.LECConsequences,
            "Metadata edits or the round trip moved the mean Total curve ordinates.");
        CollectionAssert.AreEqual(baselineTotal.LECProbabilities, resultTotal.LECProbabilities,
            "Metadata edits or the round trip moved the mean Total curve probabilities.");
        CollectionAssert.AreEqual(
            baselineAnalysis.UpperRiskResults!.Curves.Fail.LECProbabilities,
            roundTripped.UpperRiskResults!.Curves.Fail.LECProbabilities,
            "Metadata edits or the round trip moved the upper confidence curve.");
    }

    #region Bivariate fixtures

    /// <summary>Builds an uncertain marginal (Normal knowledge uncertainty on the ordinates).</summary>
    /// <param name="name">The function name.</param>
    /// <param name="hazard">The hazard type label.</param>
    /// <param name="median">The median hazard level.</param>
    /// <param name="extreme">The 0.001-exceedance hazard level.</param>
    private static TabularHazard UncertainMarginal(string name, string hazard, double median, double extreme)
    {
        return new TabularHazard
        {
            Name = name,
            SpecifiedHazard = hazard,
            HazardUnit = "ft",
            ProbabilityTransform = Transform.None,
            UncertaintyValue = FunctionUncertainty.Hazard,
            HazardUncertainFunction = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(0.999d, new Normal(0d, 0.01d)),
                    new UncertainOrdinate(0.5d, new Normal(median, median * 0.05d)),
                    new UncertainOrdinate(0.001d, new Normal(extreme, extreme * 0.05d)),
                },
                true, SortOrder.Descending, true, SortOrder.Ascending, UnivariateDistributionType.Normal),
        };
    }

    /// <summary>Builds a deterministic linear fragility from (lo → 0) to (hi → 1).</summary>
    /// <param name="hazard">The hazard type label the fragility reads.</param>
    /// <param name="lo">The zero-probability hazard level.</param>
    /// <param name="hi">The certain-failure hazard level.</param>
    private static TabularResponse LinearFragility(string hazard, double lo, double hi)
    {
        return new TabularResponse
        {
            Name = $"{hazard} Fragility",
            SpecifiedHazard = hazard,
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(lo, new Deterministic(0d)), new UncertainOrdinate(hi, new Deterministic(1d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds a deterministic linear damage from (0 → 0) to (max → valueAtMax).</summary>
    /// <param name="name">The function name.</param>
    /// <param name="hazard">The hazard type label the damage reads.</param>
    /// <param name="max">The top hazard level.</param>
    /// <param name="valueAtMax">The damage at the top level.</param>
    private static TabularConsequence LinearDamage(string name, string hazard, double max, double valueAtMax)
    {
        return new TabularConsequence
        {
            Name = name,
            SpecifiedHazard = hazard,
            HazardUnit = "ft",
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(0d, new Deterministic(0d)), new UncertainOrdinate(max, new Deterministic(valueAtMax)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>
    /// Builds the uncertain bivariate component: an independence-copula hazard over uncertain
    /// Surge and Pool marginals with the failure path bound either to the raw secondary
    /// (pool fragility and pool damages off hazard port 1) or entirely to the primary axis
    /// (surge fragility and surge damages — the integrand then never reads the secondary).
    /// </summary>
    /// <param name="secondaryBound">True for the pool-bound failure path.</param>
    /// <param name="poolMedian">The pool marginal's median level (the perturbation knob).</param>
    private static SystemComponent BivariateComponent(bool secondaryBound, double poolMedian = 50d)
    {
        var joint = new BivariateHazard(
            UncertainMarginal("Surge Marginal", "Surge", 10d, 30d),
            UncertainMarginal("Pool Marginal", "Pool", poolMedian, poolMedian * 2d))
        {
            Name = "Joint Hazard",
            SpecifiedHazard = "Surge",
            HazardUnit = "ft",
            SecondarySpecifiedHazard = "Pool",
            SecondaryHazardUnit = "ft",
            SecondaryIntegrationBins = 8,
        };
        var component = new SystemComponent(joint) { Name = "Joint Component" };
        var hazard = component.Graph.GetElements<HazardElement>().Single();
        var breach = new ResponseElement("Breach")
        {
            Function = secondaryBound ? LinearFragility("Pool", 0d, 100d) : LinearFragility("Surge", 5d, 30d),
            Input = secondaryBound ? new RiskConnection(hazard, 1) : new RiskConnection(hazard),
        };
        component.Graph.AddElement(breach);
        var failure = new ConsequenceElement("Failure Damages") { Input = new RiskConnection(breach) };
        failure.Functions.Add(secondaryBound
            ? LinearDamage("Failure Loss", "Pool", 100d, 600d)
            : LinearDamage("Failure Loss", "Surge", 30d, 600d));
        component.Graph.AddElement(failure);
        var background = new ConsequenceElement("Baseline Damages") { Input = new RiskConnection(hazard) };
        background.Functions.Add(LinearDamage("Baseline Loss", "Surge", 30d, 60d));
        component.Graph.AddElement(background);
        return component;
    }

    /// <summary>Builds an ensemble analysis over the given components.</summary>
    /// <param name="components">The system components.</param>
    private static RiskAnalysis BivariateAnalysis(params SystemComponent[] components)
    {
        var analysis = new RiskAnalysis(components) { Name = "Bivariate Reproducibility" };
        analysis.Options.EstimateMeanRiskOnly = false;
        analysis.Options.Realizations = 100;
        return analysis;
    }

    #endregion

    /// <summary>
    /// Same bivariate content at thread counts one, four, and unbounded: the conditional-bin
    /// fold must be bit-identical at any parallelism (index-owned writes, sequential
    /// reductions, and the no-draws-in-the-bin-loop contract leave nothing schedule-
    /// dependent).
    /// </summary>
    [TestMethod]
    public void Test_Bivariate_ThreadCounts_BitIdentical()
    {
        // Arrange / Act
        var single = BivariateAnalysis(BivariateComponent(secondaryBound: true));
        single.MaximumDegreeOfParallelismOverride = 1;
        var four = BivariateAnalysis(BivariateComponent(secondaryBound: true));
        four.MaximumDegreeOfParallelismOverride = 4;
        var unbounded = BivariateAnalysis(BivariateComponent(secondaryBound: true));
        var first = Run(single);
        var second = Run(four);
        var third = Run(unbounded);

        // Assert
        Assert.AreEqual(first.Ensemble, second.Ensemble, "One and four threads diverged.");
        Assert.AreEqual(first.Ensemble, third.Ensemble, "One and unbounded threads diverged.");
        Assert.AreEqual(first.Mean, second.Mean, "Mean curves diverged at four threads.");
        Assert.AreEqual(first.Mean, third.Mean, "Mean curves diverged unbounded.");
    }

    /// <summary>
    /// Bivariate presentation is not identity: renaming every function including BOTH
    /// marginal links (with fresh ids), moving every graph element on the canvas, and
    /// round-tripping through BOTH serialization modes (self-contained, and by-reference
    /// through a live resolver) must leave the numeric surface byte-identical. The stamped
    /// end-state display labels legitimately echo current names and are stripped exactly as
    /// in the univariate metadata pin.
    /// </summary>
    [TestMethod]
    public void Test_Bivariate_MetadataCanvasAndModes_BitIdentical()
    {
        // Arrange — baseline.
        var baseline = Run(BivariateAnalysis(BivariateComponent(secondaryBound: true)));
        static string StripDisplayLabels(string json) => System.Text.RegularExpressions.Regex.Replace(
            json, "\"(Name|PathLabel)\":\"[^\"]*\",?", string.Empty);

        // Act — renames (marginals included), fresh ids, and canvas moves.
        var editedComponent = BivariateComponent(secondaryBound: true);
        editedComponent.Name = "Renamed Joint Component";
        foreach (var function in editedComponent.GetReferencedFunctions())
        {
            function.Name = $"{function.Name} (renamed)";
            function.AssignNewId();
        }
        foreach (var element in editedComponent.Graph.Elements)
        {
            element.LeftPosition += 250d;
            element.TopPosition += 125d;
        }
        var edited = Run(BivariateAnalysis(editedComponent));

        // The self-contained round trip.
        var selfContained = Run(BivariateAnalysis(
            new SystemComponent(BivariateComponent(secondaryBound: true).ToXElement())));

        // The by-reference round trip: the stored functions stay live and the resolver
        // re-attaches them (marginal links included).
        var source = BivariateComponent(secondaryBound: true);
        var store = source.GetReferencedFunctions().ToDictionary(f => f.Id, f => f);
        var resolver = new RiskFunctionResolver(
            id => store.TryGetValue(id, out var function) ? function : null,
            name => store.Values.FirstOrDefault(f => f.Name == name));
        var byReference = Run(BivariateAnalysis(
            new SystemComponent(source.ToXElement(RiskSerializationMode.ByReference), resolver)));

        // Assert — every numeric byte matches.
        Assert.AreEqual(StripDisplayLabels(baseline.Ensemble), StripDisplayLabels(edited.Ensemble),
            "Renames, fresh ids, or canvas moves moved the bivariate ensemble.");
        Assert.AreEqual(StripDisplayLabels(baseline.Ensemble), StripDisplayLabels(selfContained.Ensemble),
            "The self-contained round trip moved the bivariate ensemble.");
        Assert.AreEqual(StripDisplayLabels(baseline.Ensemble), StripDisplayLabels(byReference.Ensemble),
            "The by-reference round trip moved the bivariate ensemble.");
        Assert.AreEqual(StripDisplayLabels(baseline.Mean), StripDisplayLabels(edited.Mean),
            "Renames or canvas moves moved the mean curves (display labels stripped).");
        Assert.AreEqual(StripDisplayLabels(baseline.Mean), StripDisplayLabels(selfContained.Mean),
            "The self-contained round trip moved the mean curves.");
        Assert.AreEqual(StripDisplayLabels(baseline.Mean), StripDisplayLabels(byReference.Mean),
            "The by-reference round trip moved the mean curves.");
    }

    /// <summary>
    /// Seed stability against unrelated growth: appending an unrelated univariate component
    /// to the analysis must leave the bivariate component's own results bit-identical —
    /// component seeds derive from (analysis seed, component content hash, occurrence
    /// index), never from neighbors.
    /// </summary>
    [TestMethod]
    public void Test_Bivariate_SeedStability_UnrelatedComponentAdded()
    {
        // Arrange
        var alone = BivariateAnalysis(BivariateComponent(secondaryBound: true));
        alone.RunAsync().GetAwaiter().GetResult();
        Assert.IsTrue(alone.IsEstimated);

        var unrelated = new SystemComponent { Name = "Unrelated Reach" };
        unrelated.HazardFunction = UncertainMarginal("Unrelated Hazard", "Stage", 20d, 60d);
        unrelated.AddFailureMode(new FailureMode(null, null,
            LinearFragility("Stage", 0d, 80d), LinearDamage("Unrelated Loss", "Stage", 80d, 100d)));
        var grown = BivariateAnalysis(BivariateComponent(secondaryBound: true), unrelated);

        // Act
        grown.RunAsync().GetAwaiter().GetResult();
        Assert.IsTrue(grown.IsEstimated);

        // Assert — the component's computed numbers are bit-identical at every realization
        // and in the mean summary. The comparison rides the per-component SUMMARY surfaces
        // deliberately: output CURVES resample onto analysis-level consequence grids, whose
        // extents legitimately widen when another component joins the system, so curve
        // ordinates are presentation, not the seed-stability claim.
        for (int i = 0; i < alone.Options.Realizations; i++)
        {
            var baselineRealization = alone.RiskResults![i]!.ComponentResults[0];
            var grownRealization = grown.RiskResults![i]!.ComponentResults[0];
            Assert.AreEqual(BitConverter.DoubleToInt64Bits(baselineRealization.Total.Mean),
                BitConverter.DoubleToInt64Bits(grownRealization.Total.Mean),
                $"Realization {i}: the component total mean moved when an unrelated component was added.");
            Assert.AreEqual(BitConverter.DoubleToInt64Bits(baselineRealization.Fail.Mean),
                BitConverter.DoubleToInt64Bits(grownRealization.Fail.Mean),
                $"Realization {i}: the component failure mean moved when an unrelated component was added.");
            Assert.AreEqual(BitConverter.DoubleToInt64Bits(baselineRealization.Fail.TotalProbability),
                BitConverter.DoubleToInt64Bits(grownRealization.Fail.TotalProbability),
                $"Realization {i}: the component failure probability moved when an unrelated component was added.");
        }
        var baselineMean = new RMC.TotalRisk.Results.Risk.SystemRiskResults(alone.MeanRiskResults!).ComponentResults[0];
        var grownMean = new RMC.TotalRisk.Results.Risk.SystemRiskResults(grown.MeanRiskResults!).ComponentResults[0];
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(baselineMean.Total.Mean),
            BitConverter.DoubleToInt64Bits(grownMean.Total.Mean),
            "The mean-pass component total mean moved when an unrelated component was added.");
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(baselineMean.Fail.TotalProbability),
            BitConverter.DoubleToInt64Bits(grownMean.Fail.TotalProbability),
            "The mean-pass component failure probability moved when an unrelated component was added.");
    }

    /// <summary>
    /// The seed-stable perturbation pin crossed with conditional bins, both ways. On an
    /// all-primary-bound component the integrand never reads the secondary axis — the
    /// conditional weights depend only on the bin count and every per-bin evaluation repeats
    /// the primary value — so perturbing the secondary marginal's content under
    /// <c>PinnedSamplerSeeds</c> must be BIT-IDENTICAL (the content edit re-rolls seeds; the
    /// pin restores them; nothing else flows). On the secondary-bound component the same
    /// perturbation is a pure parameter effect: the pinned results move away from the
    /// baseline, and repeating the pinned perturbed run reproduces itself bit-identically.
    /// </summary>
    [TestMethod]
    public void Test_Bivariate_PinnedSeeds_SecondaryPerturbation()
    {
        // The results manifest embeds the component content hashes and the analysis content
        // hash — a perturbed component legitimately carries different provenance even when
        // every computed number is identical, so the bit-identity comparison strips the flat
        // manifest object and compares the entire remaining payload.
        static string StripManifest(string json) => System.Text.RegularExpressions.Regex.Replace(
            json, "\"Manifest\":\\{[^{}]*\\},", string.Empty);

        // Arrange — the all-primary-bound baseline and its captured seeds.
        var baseline = BivariateAnalysis(BivariateComponent(secondaryBound: false));
        var baselineResult = Run(baseline);
        var map = baseline.CapturedSamplerSeeds!;

        // Act — perturb the pool marginal's content under the pin.
        var perturbed = BivariateAnalysis(BivariateComponent(secondaryBound: false, poolMedian: 57d));
        perturbed.PinnedSamplerSeeds = map;
        var perturbedResult = Run(perturbed);

        // Assert — bit-identical: the integrand never reads the secondary axis.
        Assert.AreEqual(StripManifest(baselineResult.Ensemble), StripManifest(perturbedResult.Ensemble),
            "A pinned secondary perturbation moved an all-primary-bound ensemble.");
        Assert.AreEqual(StripManifest(baselineResult.Mean), StripManifest(perturbedResult.Mean),
            "A pinned secondary perturbation moved all-primary-bound mean curves.");

        // Arrange — the secondary-bound counterpart.
        var boundBaseline = BivariateAnalysis(BivariateComponent(secondaryBound: true));
        var boundResult = Run(boundBaseline);
        var boundMap = boundBaseline.CapturedSamplerSeeds!;

        // Act — the same perturbation where the consequence-bearing path reads the secondary.
        var boundPerturbed = BivariateAnalysis(BivariateComponent(secondaryBound: true, poolMedian: 57d));
        boundPerturbed.PinnedSamplerSeeds = boundMap;
        var boundPerturbedResult = Run(boundPerturbed);
        var repeat = BivariateAnalysis(BivariateComponent(secondaryBound: true, poolMedian: 57d));
        repeat.PinnedSamplerSeeds = boundMap;
        var repeatResult = Run(repeat);

        // Assert — a pure, reproducible parameter effect.
        Assert.AreNotEqual(boundResult.Ensemble, boundPerturbedResult.Ensemble,
            "A secondary-bound perturbation must move the results.");
        Assert.AreEqual(boundPerturbedResult.Ensemble, repeatResult.Ensemble,
            "The pinned perturbed run must reproduce itself bit-identically.");
        Assert.AreEqual(boundPerturbedResult.Mean, repeatResult.Mean,
            "The pinned perturbed mean curves must reproduce bit-identically.");
    }

    /// <summary>
    /// A compute-relevant numeric edit must move the results — the counter-pin proving the
    /// equality asserts are not vacuous.
    /// </summary>
    [TestMethod]
    public void Test_Reproducibility_ComputeEdit_MovesResults()
    {
        // Arrange
        var baseline = Run(Build());

        // Act — nudge one fragility ordinate.
        var edited = Build();
        var component = (SystemComponent)edited.Components[0];
        foreach (var function in component.GetReferencedFunctions())
        {
            if (function is TabularResponse fragility)
            {
                fragility.UncertainOrderedPairedData = new UncertainOrderedPairedData(
                    new[]
                    {
                        new UncertainOrdinate(100d, new Triangular(0d, 0.02d, 0.05d)),
                        new UncertainOrdinate(141d, new Triangular(0.3d, 0.5d, 0.7d)),
                        new UncertainOrdinate(180d, new Triangular(0.9d, 0.97d, 1d)),
                    },
                    true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Triangular);
            }
        }
        var result = Run(edited);

        // Assert
        Assert.AreNotEqual(baseline.Ensemble, result.Ensemble, "A compute edit must move the results.");
    }
}
