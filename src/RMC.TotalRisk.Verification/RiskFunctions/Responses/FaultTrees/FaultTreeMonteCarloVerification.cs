using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Analyses.Support;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results.Risk;
using RMC.TotalRisk.Results.Support;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.Systems.Components;
using RMC.TotalRisk.Systems.Components.Graph;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees.Nodes;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Nodes;

namespace RMC.TotalRisk.Verification.RiskFunctions.Responses.FaultTrees;

/// <summary>
/// Fault-tree Monte Carlo, sampling, scheduling, and node-importance closure: an independent
/// million-trial Boolean simulation of a medium repeated-event tree under a finite-sample
/// Hoeffding union bound, shared-once/clone-separate sampling dimensions with complete Latin
/// hypercube strata coverage, replicate SRS-versus-LHS variance reduction on an affine fixture
/// with exact expectations, end-to-end scheduling bit identity through the production engine,
/// canonical-identity realization invariance, and node-importance oracles for both tree kinds.
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// <para>
/// This greenfield family has no legacy oracle. The Boolean simulation draws each unique
/// variable once per trial with <see cref="Random"/> and evaluates gate truth bottom-up in a
/// hand-coded structure twin, never the production evaluator; its acceptance band is the
/// two-sided finite-sample Hoeffding bound <c>sqrt(ln(2K/alpha)/(2N))</c> at familywise
/// <c>alpha = 1e-6</c>. The node-importance checks pair an affine event tree, whose Pearson
/// coefficient and first-order index have the exact forms <c>a·sigma/sqrt(sum a²sigma²)</c> and
/// <c>a²sigma²/sum a²sigma²</c>, with a from-scratch BCL-random reimplementation of the two-pass
/// sweep for a shared-event fault tree.
/// </para>
/// </remarks>
[TestClass]
public class FaultTreeMonteCarloVerification
{
    /// <summary>The independent Boolean-simulation sample size.</summary>
    private const int SimulationRealizations = 1_000_000;

    /// <summary>The independent BCL simulation seed.</summary>
    private const int SimulationSeed = 10_203_671;

    /// <summary>The familywise error allowance for the simulation comparison.</summary>
    private const double SimulationFamilywiseAlpha = 1e-6d;

    /// <summary>The sixteen unique medium-tree event probabilities.</summary>
    private static readonly double[] MediumProbabilities =
        Enumerable.Range(1, 16).Select(i => 0.15d + 0.03d * i).ToArray();

    /// <summary>
    /// Verifies the medium repeated-event tree against one million independent Boolean trials:
    /// each of the sixteen unique variables is drawn once per trial — repeated shared occurrences
    /// reuse the same draw — and the hand-coded gate twin is evaluated bottom-up. The observed
    /// top-event frequency must sit within the one-comparison Hoeffding bound of the exhaustive
    /// expectation, and the production response must equal that expectation at roundoff scale.
    /// </summary>
    [TestMethod]
    public void Test_MediumTree_EqualsIndependentBooleanMonteCarlo()
    {
        FaultTreeResponse response = BuildMediumTree();
        Assert.IsTrue(response.Validate().IsValid,
            string.Join(" | ", response.Validate().ValidationMessages));

        // Exhaustive expectation over all 2^16 assignments of the unique variables.
        double expected = 0d;
        double compensation = 0d;
        for (ulong mask = 0; mask < 1UL << 16; mask++)
        {
            double weight = 1d;
            var states = new bool[16];
            for (int i = 0; i < 16; i++)
            {
                states[i] = (mask & (1UL << i)) != 0;
                weight *= states[i] ? MediumProbabilities[i] : 1d - MediumProbabilities[i];
            }
            if (MediumTreeTruth(states))
            {
                double adjusted = weight - compensation;
                double next = expected + adjusted;
                compensation = (next - expected) - adjusted;
                expected = next;
            }
        }

        double production = response.SampleResponseFunction()[0].Y;
        Assert.AreEqual(expected, production, 1e-13d,
            "The production diagram must reproduce the exhaustive expectation exactly.");

        var random = new Random(SimulationSeed);
        var trialStates = new bool[16];
        int topCount = 0;
        for (int trial = 0; trial < SimulationRealizations; trial++)
        {
            for (int i = 0; i < 16; i++) trialStates[i] = random.NextDouble() < MediumProbabilities[i];
            if (MediumTreeTruth(trialStates)) topCount++;
        }
        double observed = topCount / (double)SimulationRealizations;
        double tolerance = Math.Sqrt(Math.Log(2d / SimulationFamilywiseAlpha)
            / (2d * SimulationRealizations));
        Assert.AreEqual(expected, observed, tolerance,
            $"Simulation: seed {SimulationSeed}, N {SimulationRealizations}, count {topCount}, " +
            $"finite-sample Hoeffding bound {tolerance:G17}.");

        Console.WriteLine(
            $"Fault-tree simulation: expected {expected:G17}, production {production:G17}, " +
            $"observed {observed:G17}, error {Math.Abs(observed - expected):G17}, bound {tolerance:G17}.");
    }

    /// <summary>Builds the authored medium tree whose structure the Boolean twin mirrors.</summary>
    /// <returns>The valid response with sixteen unique events and three shared repetitions.</returns>
    private static FaultTreeResponse BuildMediumTree()
    {
        var tree = new FaultTree();
        var events = new FaultTreeBasicEventNode[16];
        for (int i = 0; i < 16; i++)
        {
            events[i] = new FaultTreeBasicEventNode($"E{i + 1}",
                new ProbabilitySource(MediumProbabilities[i]));
        }

        var series = new FaultTreeGateNode("Series", FaultTreeGateType.And);
        tree.Add(tree.Root.Id, series);
        tree.Add(series.Id, events[0]);
        tree.Add(series.Id, events[1]);
        tree.Add(series.Id, events[2]);

        var voting = new FaultTreeGateNode("Voting", FaultTreeGateType.KOfN, 2);
        tree.Add(tree.Root.Id, voting);
        tree.Add(voting.Id, events[3]);
        tree.Add(voting.Id, events[4]);
        tree.Add(voting.Id, events[5]);

        var exclusive = new FaultTreeGateNode("Exclusive", FaultTreeGateType.Xor);
        tree.Add(tree.Root.Id, exclusive);
        tree.Add(exclusive.Id, events[6]);
        tree.Add(exclusive.Id, events[7]);

        var repeatedSeries = new FaultTreeGateNode("Repeated series", FaultTreeGateType.And);
        tree.Add(tree.Root.Id, repeatedSeries);
        tree.Add(repeatedSeries.Id, events[8]);
        tree.LinkShared(repeatedSeries.Id, events[0].Id, "E1 again");
        tree.Add(repeatedSeries.Id, events[9]);

        var parallel = new FaultTreeGateNode("Parallel", FaultTreeGateType.Or);
        tree.Add(tree.Root.Id, parallel);
        tree.Add(parallel.Id, events[10]);
        tree.Add(parallel.Id, events[11]);
        tree.Add(parallel.Id, events[12]);

        var mixed = new FaultTreeGateNode("Mixed repeats", FaultTreeGateType.And);
        tree.Add(tree.Root.Id, mixed);
        tree.Add(mixed.Id, events[13]);
        tree.LinkShared(mixed.Id, events[3].Id, "E4 again");
        tree.LinkShared(mixed.Id, events[6].Id, "E7 again");

        var tail = new FaultTreeGateNode("Tail", FaultTreeGateType.And);
        tree.Add(tree.Root.Id, tail);
        tree.Add(tail.Id, events[14]);
        tree.Add(tail.Id, events[15]);

        return new FaultTreeResponse(new[] { 0d, 1d }, tree)
        {
            Name = "Medium simulation fixture",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
    }

    /// <summary>Evaluates the hand-coded Boolean twin of the medium tree, repeats included.</summary>
    /// <param name="v">The sixteen unique variable states.</param>
    /// <returns>The top-event truth.</returns>
    private static bool MediumTreeTruth(bool[] v)
    {
        bool series = v[0] && v[1] && v[2];
        int votes = (v[3] ? 1 : 0) + (v[4] ? 1 : 0) + (v[5] ? 1 : 0);
        bool voting = votes >= 2;
        bool exclusive = v[6] ^ v[7];
        bool repeatedSeries = v[8] && v[0] && v[9];
        bool parallel = v[10] || v[11] || v[12];
        bool mixed = v[13] && v[3] && v[6];
        bool tail = v[14] && v[15];
        return series || voting || exclusive || repeatedSeries || parallel || mixed || tail;
    }

    /// <summary>
    /// Verifies shared-once/clone-separate sampling: a shared transfer adds no dimension while an
    /// independent clone adds its own, every Latin hypercube dimension covers each stratum
    /// exactly once, and every indexed realization equals the closed form
    /// <c>1-(1-p(u0))(1-p(u1))</c> built from the two recovered dimension percentiles — proving
    /// the shared occurrences consumed one draw and the clone consumed a distinct one.
    /// </summary>
    [TestMethod]
    public void Test_SamplingDimensions_ShareOnceCloneSeparately_AndCoverAllStrata()
    {
        var table = new UncertainOrderedPairedData(
            new[]
            {
                new UncertainOrdinate(0d, new Uniform(0.1d, 0.5d)),
                new UncertainOrdinate(1d, new Uniform(0.1d, 0.5d)),
            }, true, SortOrder.Ascending, false, SortOrder.None,
            UnivariateDistributionType.Uniform);
        var tree = new FaultTree();
        var block = new FaultTreeGateNode("Block", FaultTreeGateType.And);
        tree.Add(tree.Root.Id, block);
        var basic = new FaultTreeBasicEventNode("Uncertain", new ProbabilitySource(table));
        tree.Add(block.Id, basic);
        tree.LinkShared(block.Id, basic.Id, "Shared occurrence");
        tree.LinkIndependent(tree.Root.Id, block.Id, "Independent clone");
        var response = new FaultTreeResponse(new[] { 0d, 1d }, tree)
        {
            Name = "Dimension fixture",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
        Assert.AreEqual(2, response.SamplingDimensions,
            "One shared variable plus one independent clone must occupy exactly two dimensions.");

        const int realizations = 256;
        const int seed = 10_203_651;
        response.SetupSampler(realizations, seed, SamplingScheme.LatinHypercube);
        var strata = new int[2, realizations];
        bool distinctStreams = false;
        for (int r = 0; r < realizations; r++)
        {
            double first = response.SampledPercentile(r, 0);
            double second = response.SampledPercentile(r, 1);
            strata[0, Math.Clamp((int)Math.Floor(first * realizations), 0, realizations - 1)]++;
            strata[1, Math.Clamp((int)Math.Floor(second * realizations), 0, realizations - 1)]++;
            distinctStreams |= first != second;

            double sharedProbability = table.CurveSample(first)[0].Y;
            double cloneProbability = table.CurveSample(second)[0].Y;
            double expected = 1d - (1d - sharedProbability) * (1d - cloneProbability);
            Assert.AreEqual(expected, response.SampleResponseFunction(r)[0].Y, 1e-14d,
                $"Shared-once/clone-separate closed form at realization {r}.");
        }
        for (int stratum = 0; stratum < realizations; stratum++)
        {
            Assert.AreEqual(1, strata[0, stratum], $"Dimension 0 stratum {stratum}.");
            Assert.AreEqual(1, strata[1, stratum], $"Dimension 1 stratum {stratum}.");
        }
        Assert.IsTrue(distinctStreams,
            "The shared variable and its independent clone must consume distinct streams.");
    }

    /// <summary>The equal per-replicate sample count for the SRS/LHS comparison.</summary>
    private const int LhsRealizations = 256;

    /// <summary>The fixed replicate count for each sampling scheme.</summary>
    private const int LhsReplicates = 12;

    /// <summary>The first fixed seed; replicate <c>r</c> uses this value plus <c>r</c>.</summary>
    private const int LhsSeedBase = 10_203_731;

    /// <summary>The conservative minimum accepted SRS/LHS replicate-variance ratio.</summary>
    private const double MinimumLhsVarianceRatio = 100d;

    /// <summary>
    /// Compares equal-size paired-seed SRS and LHS estimates of the affine fixture
    /// <c>OR(A, 0.5) = 0.5 + 0.5·pA</c> with <c>pA ~ U(0.1, 0.5)</c>. The statistic is affine in
    /// the single uniform draw, so its expectation 0.65 and scheme mean variances are exact:
    /// <c>V/N</c> for SRS and <c>V/N³</c> for Latin hypercube with
    /// <c>V = 0.25·(0.4²/12)</c>. Twenty-five pooled standard errors bound each unbiasedness
    /// check, and a conservative 100x replicate-variance ratio pins the reduction.
    /// </summary>
    [TestMethod]
    public void Test_AggregateFailure_LhsReducesVarianceAndCoversExpectation()
    {
        var lhsMeans = new double[LhsReplicates];
        var srsMeans = new double[LhsReplicates];
        for (int replicate = 0; replicate < LhsReplicates; replicate++)
        {
            int seed = LhsSeedBase + replicate;
            lhsMeans[replicate] = AffineReplicateMean(SamplingScheme.LatinHypercube, seed);
            srsMeans[replicate] = AffineReplicateMean(SamplingScheme.MonteCarlo, seed);
            Console.WriteLine(
                $"Fault-tree aggregate replicate {replicate}: seed {seed}, " +
                $"SRS {srsMeans[replicate]:G17}, LHS {lhsMeans[replicate]:G17}.");
        }

        double lhsMean = lhsMeans.Sum() / LhsReplicates;
        double srsMean = srsMeans.Sum() / LhsReplicates;
        double lhsVariance = lhsMeans.Sum(value => (value - lhsMean) * (value - lhsMean))
            / (LhsReplicates - 1);
        double srsVariance = srsMeans.Sum(value => (value - srsMean) * (value - srsMean))
            / (LhsReplicates - 1);
        Assert.IsTrue(lhsVariance > 0d, "Distinct LHS seeds must move the jittered strata.");
        double ratio = srsVariance / lhsVariance;
        Assert.IsTrue(ratio >= MinimumLhsVarianceRatio,
            $"The affine fixture must reduce replicate variance by at least " +
            $"{MinimumLhsVarianceRatio:G0}x; SRS {srsVariance:G17}, LHS {lhsVariance:G17}, " +
            $"ratio {ratio:G17}.");

        const double expected = 0.65d;
        const double perDrawVariance = 0.25d * (0.4d * 0.4d / 12d);
        double srsPooledStandardError = Math.Sqrt(perDrawVariance / LhsRealizations / LhsReplicates);
        double lhsPooledStandardError = Math.Sqrt(perDrawVariance
            / ((double)LhsRealizations * LhsRealizations * LhsRealizations) / LhsReplicates);
        const double standardErrorMultiplier = 25d;
        Assert.AreEqual(expected, srsMean, standardErrorMultiplier * srsPooledStandardError,
            "The SRS pooled aggregate must be unbiased against the exact expectation.");
        Assert.AreEqual(expected, lhsMean, standardErrorMultiplier * lhsPooledStandardError,
            "The LHS pooled aggregate must be unbiased against the exact expectation.");
        Assert.AreEqual(LhsReplicates, lhsMeans.Distinct().Count(),
            "Every fixed replicate seed must produce a distinct LHS aggregate mean.");

        Console.WriteLine(
            $"Fault-tree aggregate LHS: N {LhsRealizations}, R {LhsReplicates}, seeds " +
            $"{LhsSeedBase}..{LhsSeedBase + LhsReplicates - 1}, expected {expected:G17}, SRS " +
            $"pooled {srsMean:G17} variance {srsVariance:G17}; LHS pooled {lhsMean:G17} " +
            $"variance {lhsVariance:G17}; ratio {ratio:G17}.");
    }

    /// <summary>Builds and samples one affine fault replicate.</summary>
    /// <param name="scheme">The sampling scheme.</param>
    /// <param name="seed">The fixed replicate seed.</param>
    /// <returns>The replicate aggregate mean at the first knot.</returns>
    private static double AffineReplicateMean(SamplingScheme scheme, int seed)
    {
        var tree = new FaultTree();
        tree.Add(tree.Root.Id, new FaultTreeBasicEventNode("Uncertain",
            new ProbabilitySource(new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(0d, new Uniform(0.1d, 0.5d)),
                    new UncertainOrdinate(1d, new Uniform(0.1d, 0.5d)),
                }, true, SortOrder.Ascending, false, SortOrder.None,
                UnivariateDistributionType.Uniform))));
        tree.Add(tree.Root.Id, new FaultTreeBasicEventNode("Scalar", new ProbabilitySource(0.5d)));
        var response = new FaultTreeResponse(new[] { 0d, 1d }, tree)
        {
            Name = "Affine variance fixture",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
        response.SetupSampler(LhsRealizations, seed, scheme);
        double sum = 0d;
        for (int r = 0; r < LhsRealizations; r++)
        {
            sum += response.SampleResponseFunction(r)[0].Y;
        }
        return sum / LhsRealizations;
    }

    /// <summary>The fixed analysis seed for scheduling reproducibility.</summary>
    private const int ThreadReproducibilitySeed = 10_203_791;

    /// <summary>The full-uncertainty realization count for each scheduling run.</summary>
    private const int ThreadReproducibilityRealizations = 100;

    /// <summary>
    /// Runs the same graph-connected uncertain fault-tree analysis sequentially, with four
    /// workers, and twice under default scheduling. Results JSON, the mean realization, the
    /// aggregate failure curve, definition/options/manifest hashes, and the complete captured
    /// seed map must be bit-identical across every configuration.
    /// </summary>
    [TestMethod]
    public void Test_FaultAnalysis_IsBitIdenticalAcrossThreadCountsAndSchedules()
    {
        RiskAnalysis analysis = BuildThreadAnalysis(out FaultTreeResponse response,
            out SystemComponent component);

        ThreadSnapshot sequential = RunThreadSnapshot(analysis, response, component, 1,
            out int sequentialWorkers);
        ThreadSnapshot multiWorker = RunThreadSnapshot(analysis, response, component, 4,
            out int multiWorkers);
        ThreadSnapshot defaultFirst = RunThreadSnapshot(analysis, response, component, null, out _);
        ThreadSnapshot defaultSecond = RunThreadSnapshot(analysis, response, component, null, out _);

        Assert.AreEqual(1, sequentialWorkers,
            "The sequential execution-control seam must use exactly one ensemble worker.");
        Assert.IsTrue(multiWorkers >= 2,
            $"The capped multi-worker run must use at least two threads; observed {multiWorkers}.");
        AssertThreadSnapshotsEqual(sequential, multiWorker, "sequential versus four-worker");
        AssertThreadSnapshotsEqual(sequential, defaultFirst, "sequential versus default run 1");
        AssertThreadSnapshotsEqual(sequential, defaultSecond, "sequential versus default run 2");
    }

    /// <summary>Builds the graph-connected uncertain fault-tree scheduling fixture.</summary>
    /// <param name="response">The fault-tree response definition.</param>
    /// <param name="component">The graph-owned component.</param>
    /// <returns>The configured full-uncertainty analysis.</returns>
    private static RiskAnalysis BuildThreadAnalysis(out FaultTreeResponse response,
        out SystemComponent component)
    {
        var tree = new FaultTree();
        tree.Add(tree.Root.Id, new FaultTreeBasicEventNode("Mechanism A",
            new ProbabilitySource(ThreadUniformTable(0.2d, 0.4d))));
        var block = new FaultTreeGateNode("Block", FaultTreeGateType.And);
        tree.Add(tree.Root.Id, block);
        var repeated = new FaultTreeBasicEventNode("Mechanism B",
            new ProbabilitySource(ThreadUniformTable(0.3d, 0.5d)));
        tree.Add(block.Id, repeated);
        tree.LinkShared(block.Id, repeated.Id, "B again");
        tree.Add(block.Id, new FaultTreeBasicEventNode("Trigger", new ProbabilitySource(0.6d)));
        response = new FaultTreeResponse(new[] { 0d, 15d, 30d }, tree)
        {
            Name = "Thread reproducibility fault tree",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };

        var hazardFunction = new TabularHazard
        {
            Name = "Thread fixture stage frequency",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            NoUncertaintyFunction = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(0.999d, new Deterministic(0d)),
                    new UncertainOrdinate(0.5d, new Deterministic(15d)),
                    new UncertainOrdinate(0.001d, new Deterministic(30d)),
                }, true, SortOrder.Descending, true, SortOrder.Ascending,
                UnivariateDistributionType.Deterministic),
        };
        component = new SystemComponent(hazardFunction)
        {
            Name = "Fault-tree scheduling component",
        };
        HazardElement hazard = component.Graph.GetElements<HazardElement>().Single();
        var responseElement = new ResponseElement("Fault tree")
        {
            Function = response,
            Input = new RiskConnection(hazard),
        };
        component.Graph.AddElement(responseElement);
        var consequence = new ConsequenceElement("Damage")
        {
            Input = new RiskConnection(responseElement),
        };
        consequence.Functions.Add(new TabularConsequence
        {
            Name = "Scheduling damage",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Damage",
            ConsequenceUnit = "$",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(0d, new Deterministic(0d)),
                    new UncertainOrdinate(30d, new Deterministic(500d)),
                }, true, SortOrder.Ascending, false, SortOrder.None,
                UnivariateDistributionType.Deterministic),
        });
        component.Graph.AddElement(consequence);

        var analysis = new RiskAnalysis(new[] { component })
        {
            Name = "Fault-tree scheduling reproducibility",
        };
        analysis.Options.EstimateMeanRiskOnly = false;
        analysis.Options.Realizations = ThreadReproducibilityRealizations;
        analysis.Options.SamplingScheme = SamplingScheme.LatinHypercube;
        analysis.Options.PRNGSeed = ThreadReproducibilitySeed;
        analysis.Options.UseDefaults = false;
        analysis.Options.Tolerance = 1e-6d;
        analysis.Options.EnsembleTolerance = 1e-6d;
        analysis.Options.EnsembleMinDepth = 2;
        analysis.Options.MaxEvaluations = 10_000;
        analysis.Options.LECOutputLength = 50;
        analysis.Options.RiskMeasures = RiskMeasureOptions.None;
        return analysis;
    }

    /// <summary>Builds one constant three-knot uniform source aligned to the fixture axis.</summary>
    /// <param name="minimum">The uniform minimum.</param>
    /// <param name="maximum">The uniform maximum.</param>
    /// <returns>The uncertain table.</returns>
    private static UncertainOrderedPairedData ThreadUniformTable(double minimum, double maximum)
    {
        return new UncertainOrderedPairedData(
            new[]
            {
                new UncertainOrdinate(0d, new Uniform(minimum, maximum)),
                new UncertainOrdinate(15d, new Uniform(minimum, maximum)),
                new UncertainOrdinate(30d, new Uniform(minimum, maximum)),
            }, true, SortOrder.Ascending, false, SortOrder.None,
            UnivariateDistributionType.Uniform);
    }

    /// <summary>Runs one scheduling configuration and captures every deterministic contract.</summary>
    /// <param name="analysis">The reusable analysis instance.</param>
    /// <param name="response">The fault-tree response definition.</param>
    /// <param name="component">The graph-owned component.</param>
    /// <param name="maximumDegreeOfParallelism">The internal cap, or null for production default.</param>
    /// <param name="workerCount">The number of observed ensemble worker threads.</param>
    /// <returns>The immutable comparison snapshot.</returns>
    private static ThreadSnapshot RunThreadSnapshot(RiskAnalysis analysis,
        FaultTreeResponse response, SystemComponent component,
        int? maximumDegreeOfParallelism, out int workerCount)
    {
        var workerIds = new HashSet<int>();
        object sync = new();
        analysis.MaximumDegreeOfParallelismOverride = maximumDegreeOfParallelism;
        analysis.EnsembleWorkerObserver = () =>
        {
            lock (sync) workerIds.Add(Environment.CurrentManagedThreadId);
        };
        Exception? completionError = null;
        analysis.AnalysisCompleted += CaptureCompletion;
        try
        {
            analysis.RunAsync().GetAwaiter().GetResult();
        }
        finally
        {
            analysis.AnalysisCompleted -= CaptureCompletion;
            analysis.EnsembleWorkerObserver = null;
        }
        Assert.IsTrue(analysis.IsEstimated,
            completionError == null ? "The scheduling run must estimate." :
            $"The scheduling run failed: {completionError}");
        workerCount = workerIds.Count;

        EnsembleResults results = analysis.RiskResults!;
        SystemRealization mean = analysis.MeanRiskResults!;
        AnalysisRunManifest manifest = results.Manifest!;
        SamplerSeedMap seeds = analysis.CapturedSamplerSeeds!;
        var exactJsonOptions = new JsonSerializerOptions
        {
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };
        return new ThreadSnapshot(
            results.ToJson(),
            mean.ToJson(),
            JsonSerializer.Serialize(mean.Components[0].Curves.Fail, exactJsonOptions),
            Convert.ToHexString(response.CanonicalHash()),
            Convert.ToHexString(component.CanonicalHash()),
            Convert.ToHexString(analysis.Options.CanonicalHash()),
            manifest.AnalysisContentHash,
            manifest.EffectiveOptionsHash,
            manifest.SamplerSeedMapHash,
            string.Join(";", seeds.ComponentSeeds.Select(values => string.Join(",", values)))
                + "|" + seeds.JointSeedBase.ToString(CultureInfo.InvariantCulture));

        // Captures the one completion error for this synchronous run.
        void CaptureCompletion(object? sender, AnalysisRunCompletedEventArgs args) =>
            completionError = args.Error;
    }

    /// <summary>Asserts every deterministic output contract in two scheduling snapshots.</summary>
    /// <param name="expected">The sequential baseline.</param>
    /// <param name="actual">The comparison run.</param>
    /// <param name="label">The scheduling comparison label.</param>
    private static void AssertThreadSnapshotsEqual(ThreadSnapshot expected, ThreadSnapshot actual,
        string label)
    {
        Assert.AreEqual(expected.ResultsJson, actual.ResultsJson, $"{label}: results JSON.");
        Assert.AreEqual(expected.MeanJson, actual.MeanJson, $"{label}: mean-results JSON.");
        Assert.AreEqual(expected.FailureCurveJson, actual.FailureCurveJson,
            $"{label}: aggregate failure curve.");
        Assert.AreEqual(expected.ResponseHash, actual.ResponseHash, $"{label}: response hash.");
        Assert.AreEqual(expected.ComponentHash, actual.ComponentHash, $"{label}: component hash.");
        Assert.AreEqual(expected.OptionsHash, actual.OptionsHash, $"{label}: options hash.");
        Assert.AreEqual(expected.AnalysisContentHash, actual.AnalysisContentHash,
            $"{label}: analysis-content hash.");
        Assert.AreEqual(expected.EffectiveOptionsHash, actual.EffectiveOptionsHash,
            $"{label}: effective-options hash.");
        Assert.AreEqual(expected.SamplerSeedMapHash, actual.SamplerSeedMapHash,
            $"{label}: seed-map hash.");
        Assert.AreEqual(expected.SeedMap, actual.SeedMap, $"{label}: every effective seed.");
    }

    /// <summary>All bitwise scheduling-invariance evidence captured from one completed run.</summary>
    /// <param name="ResultsJson">The full ensemble results JSON.</param>
    /// <param name="MeanJson">The mean realization JSON.</param>
    /// <param name="FailureCurveJson">The aggregate failure-curve JSON.</param>
    /// <param name="ResponseHash">The response canonical hash.</param>
    /// <param name="ComponentHash">The component canonical hash.</param>
    /// <param name="OptionsHash">The options canonical hash.</param>
    /// <param name="AnalysisContentHash">The manifest content hash.</param>
    /// <param name="EffectiveOptionsHash">The manifest effective-options hash.</param>
    /// <param name="SamplerSeedMapHash">The manifest seed-map hash.</param>
    /// <param name="SeedMap">The exact captured seed fingerprint.</param>
    private sealed record ThreadSnapshot(
        string ResultsJson,
        string MeanJson,
        string FailureCurveJson,
        string ResponseHash,
        string ComponentHash,
        string OptionsHash,
        string AnalysisContentHash,
        string EffectiveOptionsHash,
        string SamplerSeedMapHash,
        string SeedMap);

    /// <summary>
    /// Verifies canonical-identity invariance of prepared realizations: metadata renames, GUID
    /// regeneration, sibling reorder, and the by-reference persistence mode all leave the
    /// canonical hash and all 64 prepared Latin hypercube realization curves bit-identical.
    /// </summary>
    [TestMethod]
    public void Test_CanonicalInvariance_RealizationsBitIdentical()
    {
        var externalTree = new FaultTree();
        externalTree.Add(externalTree.Root.Id, new FaultTreeBasicEventNode("External event",
            new ProbabilitySource(new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(0d, new Uniform(0.2d, 0.4d)),
                    new UncertainOrdinate(1d, new Uniform(0.2d, 0.4d)),
                }, true, SortOrder.Ascending, false, SortOrder.None,
                UnivariateDistributionType.Uniform))));
        FaultTreeResponse external = new FaultTreeResponse(new[] { 0d, 1d }, externalTree)
        {
            Name = "Invariance external target",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };

        var tree = new FaultTree();
        var block = new FaultTreeGateNode("Block", FaultTreeGateType.And);
        tree.Add(tree.Root.Id, block);
        var repeated = new FaultTreeBasicEventNode("Repeated", new ProbabilitySource(0.4d));
        tree.Add(block.Id, repeated);
        tree.LinkShared(block.Id, repeated.Id, "Repeat");
        tree.Add(tree.Root.Id, new FaultTreeBasicEventNode("Scalar", new ProbabilitySource(0.2d)));
        tree.LinkShared(tree.Root.Id, external, external.FaultTree.Root.Id, "External use");
        var original = new FaultTreeResponse(new[] { 0d, 1d }, tree)
        {
            Name = "Invariance fixture",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };

        XElement remappedXml = original.ToXElement(RiskSerializationMode.SelfContained);
        RegeneratePersistentIds(remappedXml);
        var remapped = new FaultTreeResponse(remappedXml);
        var metadata = new FaultTreeResponse(original.ToXElement(RiskSerializationMode.SelfContained));
        metadata.Name = "Renamed fixture";
        foreach (FaultTreeNodeBase node in metadata.FaultTree.Nodes)
        {
            node.Name = "Renamed " + node.Name;
        }
        FaultTreeGateNode reorderGate = metadata.FaultTree.Root;
        metadata.FaultTree.Move(reorderGate.Children[^1].Id, reorderGate.Id,
            reorderGate.Children[0].Id);
        var resolver = new RMC.TotalRisk.RiskFunctions.RiskFunctionResolver(
            id => id == external.Id ? external : null,
            name => name == external.Name ? external : null);
        var byReference = new FaultTreeResponse(
            original.ToXElement(RiskSerializationMode.ByReference), resolver);

        const int realizations = 64;
        const int seed = 10_203_661;
        original.SetupSampler(realizations, seed, SamplingScheme.LatinHypercube);
        remapped.SetupSampler(realizations, seed, SamplingScheme.LatinHypercube);
        metadata.SetupSampler(realizations, seed, SamplingScheme.LatinHypercube);
        byReference.SetupSampler(realizations, seed, SamplingScheme.LatinHypercube);

        CollectionAssert.AreEqual(original.CanonicalHash(), remapped.CanonicalHash());
        CollectionAssert.AreEqual(original.CanonicalHash(), metadata.CanonicalHash());
        CollectionAssert.AreEqual(original.CanonicalHash(), byReference.CanonicalHash());
        for (int r = 0; r < realizations; r++)
        {
            OrderedPairedData expected = original.SampleResponseFunction(r);
            for (int h = 0; h < expected.Count; h++)
            {
                Assert.AreEqual(expected[h].Y, remapped.SampleResponseFunction(r)[h].Y, 0d,
                    $"GUID regeneration at realization {r}, hazard {h}.");
                Assert.AreEqual(expected[h].Y, metadata.SampleResponseFunction(r)[h].Y, 0d,
                    $"Metadata/order invariance at realization {r}, hazard {h}.");
                Assert.AreEqual(expected[h].Y, byReference.SampleResponseFunction(r)[h].Y, 0d,
                    $"Serialization-mode invariance at realization {r}, hazard {h}.");
            }
        }
    }

    /// <summary>Regenerates every serialized function/node GUID and matching reference.</summary>
    /// <param name="xml">The self-contained response XML.</param>
    private static void RegeneratePersistentIds(XElement xml)
    {
        var map = new Dictionary<Guid, Guid>();
        int ordinal = 1;
        foreach (XAttribute attribute in xml.DescendantsAndSelf().Attributes()
            .Where(attribute => attribute.Name.LocalName == "Id"))
        {
            if (Guid.TryParse(attribute.Value, out Guid oldId) && oldId != Guid.Empty &&
                !map.ContainsKey(oldId))
            {
                var bytes = new byte[16];
                bytes[0] = 0x10;
                bytes[1] = 0x0B;
                BitConverter.GetBytes(ordinal++).CopyTo(bytes, 8);
                map.Add(oldId, new Guid(bytes));
            }
        }
        foreach (XAttribute attribute in xml.DescendantsAndSelf().Attributes())
        {
            if (Guid.TryParse(attribute.Value, out Guid oldId) &&
                map.TryGetValue(oldId, out Guid replacement))
            {
                attribute.Value = replacement.ToString("D", CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>
    /// Verifies node importance on the affine event tree <c>F = pA + 0.5·pF</c> with
    /// <c>pA ~ U(0.1, 0.3)</c> and <c>pF ~ U(0.2, 0.6)</c>: the exact Pearson coefficient of each
    /// uncertain entry is <c>a·sigma/sqrt(sum a²sigma²) = sqrt(0.5)</c> because the two variance
    /// contributions are equal by construction, the exact first-order index is
    /// <c>a²sigma²/sum a²sigma² = 0.5</c>, residual branches anti-correlate at the same exact
    /// magnitude, and the deterministic gate entry reports NaN correlation and a zero index.
    /// Bounds are replicate standard errors: about <c>(1-rho²)/sqrt(N)</c> for Pearson and the
    /// variance-ratio sampling error for the index at one thousand iterations.
    /// </summary>
    [TestMethod]
    public void Test_NodeImportance_AffineEventTree_MatchesExactStatistics()
    {
        var tree = new EventTree();
        tree.Add(tree.Root.Id, new ChanceNode("Direct failure",
            new ProbabilitySource(ImportanceUniformTable(0.1d, 0.3d))));
        var gate = new ChanceNode("Gate", new ProbabilitySource(0.5d)) { IsFailure = false };
        tree.Add(tree.Root.Id, gate);
        tree.Add(tree.Root.Id, new RemainderNode("Root survival") { IsFailure = false });
        tree.Add(gate.Id, new ChanceNode("Gated failure",
            new ProbabilitySource(ImportanceUniformTable(0.2d, 0.6d))));
        tree.Add(gate.Id, new RemainderNode("Gated survival") { IsFailure = false });
        var response = new EventTreeResponse(new[] { 0d, 1d, 2d }, tree)
        {
            Name = "Affine importance event tree",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };

        TreeNodeImportanceResult result = TreeNodeImportance.Compute(response,
            new TreeNodeImportanceOptions(1d));

        double expectedCorrelation = Math.Sqrt(0.5d);
        TreeNodeImportanceEntry direct = result.Entries.Single(e => e.Name == "Direct failure");
        TreeNodeImportanceEntry gated = result.Entries.Single(e => e.Name == "Gated failure");
        TreeNodeImportanceEntry gateEntry = result.Entries.Single(e => e.Name == "Gate");
        TreeNodeImportanceEntry rootSurvival = result.Entries.Single(e => e.Name == "Root survival");

        Assert.AreEqual(expectedCorrelation, direct.AggregateCorrelation, 0.06d,
            "Pearson(A) = sigmaA/sqrt(sigmaA² + 0.25·sigmaF²) = sqrt(0.5).");
        Assert.AreEqual(expectedCorrelation, gated.AggregateCorrelation, 0.06d,
            "Pearson(F) = 0.5·sigmaF/sqrt(sigmaA² + 0.25·sigmaF²) = sqrt(0.5).");
        Assert.AreEqual(-expectedCorrelation, rootSurvival.AggregateCorrelation, 0.06d,
            "The root residual 0.5 - pA anti-correlates at the same exact magnitude.");
        Assert.AreEqual(0.5d, direct.FirstOrderIndex, 0.15d,
            "Index(A) = sigmaA²/(sigmaA² + 0.25·sigmaF²) = 0.5.");
        Assert.AreEqual(0.5d, gated.FirstOrderIndex, 0.15d,
            "Index(F) = 0.25·sigmaF²/(sigmaA² + 0.25·sigmaF²) = 0.5.");
        Assert.AreEqual(double.NaN, gateEntry.AggregateCorrelation);
        Assert.AreEqual(0d, gateEntry.FirstOrderIndex, 0d);
        Assert.IsFalse(gateEntry.HasUncertainty);

        double exactVariance = (0.04d / 12d) + 0.25d * (0.16d / 12d);
        Assert.AreEqual(exactVariance, result.AggregateVariance, 0.35d * exactVariance,
            "The joint aggregate variance must estimate sigmaA² + 0.25·sigmaF².");
    }

    /// <summary>Builds one aligned three-knot uniform importance source.</summary>
    /// <param name="minimum">The uniform minimum.</param>
    /// <param name="maximum">The uniform maximum.</param>
    /// <returns>The uncertain table.</returns>
    private static UncertainOrderedPairedData ImportanceUniformTable(double minimum, double maximum)
    {
        return new UncertainOrderedPairedData(
            new[]
            {
                new UncertainOrdinate(0d, new Uniform(minimum, maximum)),
                new UncertainOrdinate(1d, new Uniform(minimum, maximum)),
                new UncertainOrdinate(2d, new Uniform(minimum, maximum)),
            }, true, SortOrder.Ascending, false, SortOrder.None,
            UnivariateDistributionType.Uniform);
    }

    /// <summary>
    /// Verifies fault-tree node importance against an independent from-scratch reimplementation:
    /// a BCL-random two-pass sweep over the same shared-event tree — one draw per unified
    /// variable per iteration, aggregate <c>1-(1-pA)(1-pB)</c> — with hand-rolled mean, variance,
    /// quartile, and Pearson statistics. Both implementations estimate the same population
    /// statistics, so they must agree within replicate sampling error at one thousand
    /// iterations; determinism and live-state inertness are pinned exactly.
    /// </summary>
    [TestMethod]
    public void Test_NodeImportance_SharedFaultTree_MatchesIndependentReimplementation()
    {
        var tableA = new UncertainOrderedPairedData(
            new[]
            {
                new UncertainOrdinate(0d, new Uniform(0.3d, 0.6d)),
                new UncertainOrdinate(1d, new Uniform(0.3d, 0.6d)),
            }, true, SortOrder.Ascending, false, SortOrder.None,
            UnivariateDistributionType.Uniform);
        var tableB = new UncertainOrderedPairedData(
            new[]
            {
                new UncertainOrdinate(0d, new Uniform(0.1d, 0.4d)),
                new UncertainOrdinate(1d, new Uniform(0.1d, 0.4d)),
            }, true, SortOrder.Ascending, false, SortOrder.None,
            UnivariateDistributionType.Uniform);
        var tree = new FaultTree();
        var block = new FaultTreeGateNode("Block", FaultTreeGateType.And);
        tree.Add(tree.Root.Id, block);
        var shared = new FaultTreeBasicEventNode("Shared A", new ProbabilitySource(tableA));
        tree.Add(block.Id, shared);
        tree.LinkShared(block.Id, shared.Id, "A again");
        tree.Add(tree.Root.Id, new FaultTreeBasicEventNode("Event B", new ProbabilitySource(tableB)));
        var response = new FaultTreeResponse(new[] { 0d, 1d }, tree)
        {
            Name = "Shared importance fault tree",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
        response.SetupSampler(8, 424242, SamplingScheme.LatinHypercube);
        OrderedPairedData preservedRealization = response.SampleResponseFunction(3);
        byte[] hashBefore = response.CanonicalHash();

        var options = new TreeNodeImportanceOptions(0d);
        TreeNodeImportanceResult first = TreeNodeImportance.Compute(response, options);
        TreeNodeImportanceResult second = TreeNodeImportance.Compute(response, options);

        // Determinism and live-state inertness are exact.
        Assert.AreEqual(first.AggregateVariance, second.AggregateVariance, 0d);
        CollectionAssert.AreEqual(hashBefore, response.CanonicalHash());
        OrderedPairedData survivedRealization = response.SampleResponseFunction(3);
        for (int h = 0; h < preservedRealization.Count; h++)
        {
            Assert.AreEqual(preservedRealization[h].Y, survivedRealization[h].Y, 0d,
                "The configured sampler must survive the importance analysis untouched.");
        }
        Assert.AreEqual(2, first.Entries.Count,
            "The shared occurrences must unify onto one variable, leaving two entries.");

        // Independent two-pass reimplementation with BCL randomness and hand-rolled statistics.
        const int iterations = 1000;
        var random = new Random(97531);
        var aggregate = new double[iterations];
        var seriesA = new double[iterations];
        var seriesB = new double[iterations];
        for (int i = 0; i < iterations; i++)
        {
            double pA = tableA.CurveSample(random.NextDouble())[0].Y;
            double pB = tableB.CurveSample(random.NextDouble())[0].Y;
            seriesA[i] = pA;
            seriesB[i] = pB;
            aggregate[i] = 1d - (1d - pA) * (1d - pB);
        }
        var oatRandom = new Random(86420);
        var firstOrderA = new double[iterations];
        var firstOrderB = new double[iterations];
        double meanA = tableA.CurveSample()[0].Y;
        double meanB = tableB.CurveSample()[0].Y;
        for (int i = 0; i < iterations; i++)
        {
            double pA = tableA.CurveSample(oatRandom.NextDouble())[0].Y;
            firstOrderA[i] = 1d - (1d - pA) * (1d - meanB);
            double pB = tableB.CurveSample(oatRandom.NextDouble())[0].Y;
            firstOrderB[i] = 1d - (1d - meanA) * (1d - pB);
        }

        double aggregateVariance = HandVariance(aggregate);
        TreeNodeImportanceEntry entryA = first.Entries.Single(e => e.Name == "Shared A");
        TreeNodeImportanceEntry entryB = first.Entries.Single(e => e.Name == "Event B");
        Assert.AreEqual(HandQuantile(aggregate, 0.5d), first.AggregateSummary[2], 0.02d,
            "Independent aggregate median estimates must agree within replicate error.");
        Assert.AreEqual(aggregateVariance, first.AggregateVariance, 0.15d * aggregateVariance,
            "Independent aggregate variance estimates must agree within replicate error.");
        Assert.AreEqual(HandQuantile(seriesA, 0.5d), entryA.ProbabilitySummary[2], 0.02d,
            "Independent Shared A median estimates must agree within replicate error.");
        Assert.AreEqual(HandQuantile(seriesB, 0.5d), entryB.ProbabilitySummary[2], 0.02d,
            "Independent Event B median estimates must agree within replicate error.");
        Assert.AreEqual(HandPearson(seriesA, aggregate), entryA.AggregateCorrelation, 0.06d,
            "Independent Shared A correlation estimates must agree within replicate error.");
        Assert.AreEqual(HandPearson(seriesB, aggregate), entryB.AggregateCorrelation, 0.06d,
            "Independent Event B correlation estimates must agree within replicate error.");
        Assert.AreEqual(HandVariance(firstOrderA) / aggregateVariance, entryA.FirstOrderIndex,
            0.15d, "Independent Shared A first-order indices must agree within replicate error.");
        Assert.AreEqual(HandVariance(firstOrderB) / aggregateVariance, entryB.FirstOrderIndex,
            0.15d, "Independent Event B first-order indices must agree within replicate error.");
    }

    /// <summary>Computes a hand-rolled mean.</summary>
    /// <param name="values">The series.</param>
    /// <returns>The arithmetic mean.</returns>
    private static double HandMean(double[] values)
    {
        double sum = 0d;
        for (int i = 0; i < values.Length; i++) sum += values[i];
        return sum / values.Length;
    }

    /// <summary>Computes a hand-rolled unbiased sample variance.</summary>
    /// <param name="values">The series.</param>
    /// <returns>The sample variance.</returns>
    private static double HandVariance(double[] values)
    {
        double mean = HandMean(values);
        double sum = 0d;
        for (int i = 0; i < values.Length; i++) sum += (values[i] - mean) * (values[i] - mean);
        return sum / (values.Length - 1);
    }

    /// <summary>Computes a hand-rolled sorted-order quantile.</summary>
    /// <param name="values">The series.</param>
    /// <param name="probability">The quantile probability.</param>
    /// <returns>The order-statistic quantile.</returns>
    private static double HandQuantile(double[] values, double probability)
    {
        double[] sorted = values.OrderBy(value => value).ToArray();
        double position = probability * (sorted.Length - 1);
        int lower = (int)Math.Floor(position);
        int upper = Math.Min(lower + 1, sorted.Length - 1);
        return sorted[lower] + (position - lower) * (sorted[upper] - sorted[lower]);
    }

    /// <summary>Computes a hand-rolled Pearson correlation.</summary>
    /// <param name="x">The first series.</param>
    /// <param name="y">The second series.</param>
    /// <returns>The correlation coefficient.</returns>
    private static double HandPearson(double[] x, double[] y)
    {
        double meanX = HandMean(x);
        double meanY = HandMean(y);
        double covariance = 0d;
        double varianceX = 0d;
        double varianceY = 0d;
        for (int i = 0; i < x.Length; i++)
        {
            covariance += (x[i] - meanX) * (y[i] - meanY);
            varianceX += (x[i] - meanX) * (x[i] - meanX);
            varianceY += (y[i] - meanY) * (y[i] - meanY);
        }
        return covariance / Math.Sqrt(varianceX * varianceY);
    }
}
