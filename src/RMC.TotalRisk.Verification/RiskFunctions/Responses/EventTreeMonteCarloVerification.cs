using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
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
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.Systems.Components;
using RMC.TotalRisk.Systems.Components.Graph;

namespace RMC.TotalRisk.Verification.RiskFunctions.Responses;

/// <summary>
/// Event-tree closure evidence: independent branch-routing Monte Carlo, event-tree aggregate
/// Latin-hypercube variance reduction, and end-to-end thread-count reproducibility for expanded
/// per-leaf graph outputs.
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// </remarks>
public partial class EventTreeVerification
{
    /// <summary>The independent routing-oracle sample size.</summary>
    private const int RoutingRealizations = 1_000_000;

    /// <summary>The independent BCL routing-oracle seed.</summary>
    private const int RoutingSeed = 10_202_671;

    /// <summary>The familywise error allowance across every routing terminal and aggregate.</summary>
    private const double RoutingFamilywiseAlpha = 1e-6d;

    /// <summary>
    /// Routes one million independent samples with <see cref="Random"/> through a hand-derived
    /// tree containing two sibling levels, explicit and implicit remainders, an over-allocated
    /// normalized group, and repeated internal/external clone occurrences. Every terminal and
    /// the aggregate failure frequency must satisfy a finite-sample Hoeffding union bound.
    /// </summary>
    [TestMethod]
    public void Test_IndependentBranchRouting_EqualsAnalyticTerminalProbabilities()
    {
        RoutingFixture fixture = BuildRoutingFixture();
        ResponseBranchSample production = fixture.Response.SampleBranches();
        RoutingTerminal[] analytic = RoutingAnalyticTerminals();
        AssertProductionMatchesAnalytic(production, analytic);

        var counts = new int[analytic.Length];
        int failureCount = 0;
        var random = new Random(RoutingSeed);
        for (int realization = 0; realization < RoutingRealizations; realization++)
        {
            int terminal;
            bool failure;
            if (random.NextDouble() >= 0.5d)
            {
                terminal = 0; // Root survival.
                failure = false;
            }
            else
            {
                double scenario = random.NextDouble();
                if (scenario < 0.2d)
                {
                    terminal = 1; // Direct failure.
                    failure = true;
                }
                else if (scenario < 0.5d)
                {
                    bool localFailure = random.NextDouble() < 0.4d;
                    terminal = localFailure ? 2 : 10; // Local direct occurrence / implicit.
                    failure = localFailure;
                }
                else if (scenario < 0.8d)
                {
                    bool localFailure = random.NextDouble() < 0.4d;
                    terminal = localFailure ? 3 : 10; // Internal clone / implicit.
                    failure = localFailure;
                }
                else
                {
                    bool firstExternalOccurrence = random.NextDouble() < 0.5d;
                    bool externalFailure = random.NextDouble() < 0.3d;
                    terminal = (firstExternalOccurrence, externalFailure) switch
                    {
                        (true, true) => 4,
                        (false, true) => 5,
                        (true, false) => 6,
                        _ => 7,
                    };
                    failure = externalFailure;
                }
            }
            counts[terminal]++;
            if (failure) failureCount++;
        }

        const int comparisonCount = 12; // Eleven terminals plus aggregate failure.
        double tolerance = SimultaneousBinomialTolerance(
            comparisonCount, RoutingFamilywiseAlpha, RoutingRealizations);
        double maximumError = 0d;
        for (int i = 0; i < analytic.Length; i++)
        {
            double observed = counts[i] / (double)RoutingRealizations;
            maximumError = Math.Max(maximumError, Math.Abs(observed - analytic[i].Probability));
            Assert.AreEqual(analytic[i].Probability, observed, tolerance,
                $"{analytic[i].Label}: count {counts[i].ToString(CultureInfo.InvariantCulture)}, " +
                $"N {RoutingRealizations.ToString(CultureInfo.InvariantCulture)}, " +
                $"simultaneous Hoeffding bound {tolerance:G17}.");
        }

        const double expectedAggregateFailure = 0.25d;
        double observedAggregate = failureCount / (double)RoutingRealizations;
        maximumError = Math.Max(maximumError,
            Math.Abs(observedAggregate - expectedAggregateFailure));
        Assert.AreEqual(expectedAggregateFailure, observedAggregate, tolerance,
            $"Aggregate failure: count {failureCount.ToString(CultureInfo.InvariantCulture)}, " +
            $"N {RoutingRealizations.ToString(CultureInfo.InvariantCulture)}, " +
            $"simultaneous Hoeffding bound {tolerance:G17}.");

        Console.WriteLine(
            $"Event-tree routing: seed {RoutingSeed}, N {RoutingRealizations}, comparisons " +
            $"{comparisonCount}, familywise alpha {RoutingFamilywiseAlpha:G1}, " +
            $"max error {maximumError:G17}, finite-sample bound {tolerance:G17}, " +
            $"aggregate observed {observedAggregate:G17}.");
    }

    /// <summary>Builds the hand-derived independent-routing fixture.</summary>
    /// <returns>The valid response fixture.</returns>
    private static RoutingFixture BuildRoutingFixture()
    {
        var externalTree = new EventTree();
        var externalSequence =
            new ChanceNode("External sequence", new ProbabilitySource(0.6d))
            {
                IsFailure = false,
            };
        externalTree.Add(externalTree.Root.Id, externalSequence);
        externalTree.Add(externalSequence.Id,
            new ChanceNode("External failure", new ProbabilitySource(0.3d)));
        externalTree.Add(externalSequence.Id,
            new RemainderNode("External survival") { IsFailure = false });
        EventTreeResponse external = Response(externalTree);
        external.Name = "External routing target";

        var tree = new EventTree();
        var scenario = new ChanceNode("Scenario", new ProbabilitySource(0.5d))
        {
            IsFailure = false,
        };
        var local = new ChanceNode("Local sequence", new ProbabilitySource(0.3d))
        {
            IsFailure = false,
        };
        var routeB = new ChanceNode("Route B", new ProbabilitySource(0.2d))
        {
            IsFailure = false,
        };
        tree.Add(tree.Root.Id, scenario);
        tree.Add(tree.Root.Id, new RemainderNode("Root survival") { IsFailure = false });
        tree.Add(scenario.Id,
            new ChanceNode("Direct failure", new ProbabilitySource(0.2d)));
        tree.Add(scenario.Id, local);
        tree.Add(scenario.Id, routeB);
        tree.Add(local.Id, new ChanceNode("Local failure", new ProbabilitySource(0.4d)));
        tree.LinkIndependent(scenario.Id, local.Id, "Local clone");
        tree.Add(scenario.Id, new RemainderNode("Scenario residual") { IsFailure = false });
        tree.LinkIndependent(routeB.Id, external, externalSequence.Id, "External clone A");
        tree.LinkIndependent(routeB.Id, external, externalSequence.Id, "External clone B");
        tree.Add(routeB.Id, new RemainderNode("Route B residual") { IsFailure = false });
        EventTreeResponse response = Response(tree);
        response.Name = "Independent branch-routing fixture";
        return new RoutingFixture(response);
    }

    /// <summary>Returns the independently derived exhaustive terminal partition.</summary>
    /// <returns>Eleven unique routing categories; repeated occurrence values remain separate.</returns>
    private static RoutingTerminal[] RoutingAnalyticTerminals()
    {
        return
        [
            new("Root survival", false, 0.5d),
            new("Direct failure", true, 0.5d * 0.2d),
            new("Local failure A", true, 0.5d * 0.3d * 0.4d),
            new("Local failure B", true, 0.5d * 0.3d * 0.4d),
            new("External failure A", true, 0.5d * 0.2d * 0.5d * 0.3d),
            new("External failure B", true, 0.5d * 0.2d * 0.5d * 0.3d),
            new("External survival A", false, 0.5d * 0.2d * 0.5d * 0.7d),
            new("External survival B", false, 0.5d * 0.2d * 0.5d * 0.7d),
            new("Scenario residual", false, 0d),
            new("Route B residual", false, 0d),
            new("Implicit local residual", false, 2d * 0.5d * 0.3d * 0.6d),
        ];
    }

    /// <summary>Checks the production mean sample against the independently derived terminals.</summary>
    /// <param name="sample">The production branch sample.</param>
    /// <param name="analytic">The independent terminal partition.</param>
    private static void AssertProductionMatchesAnalytic(ResponseBranchSample sample,
        IReadOnlyList<RoutingTerminal> analytic)
    {
        var actual = Enumerable.Range(0, sample.Branches.Count)
            .Select(index => new TerminalProbability(sample.Branches[index].IsFailure,
                sample.Probabilities[index][0]))
            .OrderBy(value => value.IsFailure)
            .ThenBy(value => value.Probability)
            .ToArray();
        TerminalProbability[] expected = analytic
            .Select(value => new TerminalProbability(value.IsFailure, value.Probability))
            .OrderBy(value => value.IsFailure)
            .ThenBy(value => value.Probability)
            .ToArray();
        Assert.AreEqual(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.AreEqual(expected[i].IsFailure, actual[i].IsFailure, $"Terminal {i} classification.");
            Assert.AreEqual(expected[i].Probability, actual[i].Probability, 1e-14d,
                $"Terminal {i} analytic path product.");
        }
        double expectedFailure = analytic.Where(value => value.IsFailure)
            .Sum(value => value.Probability);
        Assert.AreEqual(expectedFailure,
            sample.Branches.Select((branch, index) => branch.IsFailure
                ? sample.Probabilities[index][0] : 0d).Sum(), 1e-14d);
    }

    /// <summary>Derives a finite-sample two-sided Hoeffding bound with a union allowance.</summary>
    /// <param name="comparisons">The simultaneous comparison count.</param>
    /// <param name="familywiseAlpha">The familywise failure allowance.</param>
    /// <param name="count">The fixed sample size.</param>
    /// <returns>The verification-only absolute confidence bound.</returns>
    private static double SimultaneousBinomialTolerance(int comparisons,
        double familywiseAlpha, int count)
    {
        return Math.Sqrt(Math.Log(2d * comparisons / familywiseAlpha) / (2d * count));
    }

    /// <summary>The response fixture for independent branch routing.</summary>
    private sealed record RoutingFixture(EventTreeResponse Response);

    /// <summary>One unique independently routed terminal category.</summary>
    private sealed record RoutingTerminal(string Label, bool IsFailure, double Probability);

    /// <summary>A terminal classification/probability pair for identity-free sorting.</summary>
    private readonly record struct TerminalProbability(bool IsFailure, double Probability);

    /// <summary>The equal per-replicate sample count for the event-tree SRS/LHS comparison.</summary>
    private const int LhsRealizations = 256;

    /// <summary>The fixed replicate count for each sampling scheme.</summary>
    private const int LhsReplicates = 12;

    /// <summary>The first fixed seed; replicate <c>r</c> uses this value plus <c>r</c>.</summary>
    private const int LhsSeedBase = 10_202_731;

    /// <summary>The conservative minimum accepted SRS/LHS replicate-variance ratio.</summary>
    private const double MinimumLhsVarianceRatio = 100d;

    /// <summary>
    /// Compares equal-size paired-seed SRS and LHS estimates of the aggregate failure response
    /// for two independent uniform terminal probabilities. The statistic is affine, so its
    /// expectation and scheme-specific mean variances are exact: V/N for SRS and V/N³ for LHS.
    /// </summary>
    [TestMethod]
    public void Test_AggregateFailure_LhsReducesVarianceAndCoversStrata()
    {
        var lhsMeans = new double[LhsReplicates];
        var srsMeans = new double[LhsReplicates];
        var lhsFirstSamples = new double[LhsReplicates];
        for (int replicate = 0; replicate < LhsReplicates; replicate++)
        {
            int seed = LhsSeedBase + replicate;
            lhsMeans[replicate] = AggregateFailureMean(
                SamplingScheme.LatinHypercube, seed, assertStrata: true,
                out lhsFirstSamples[replicate]);
            srsMeans[replicate] = AggregateFailureMean(
                SamplingScheme.MonteCarlo, seed, assertStrata: false, out _);
            Console.WriteLine(
                $"Event-tree aggregate replicate {replicate}: seed {seed}, " +
                $"SRS {srsMeans[replicate]:G17}, LHS {lhsMeans[replicate]:G17}.");
        }

        var (lhsMean, lhsVariance) = MeanAndSampleVariance(lhsMeans);
        var (srsMean, srsVariance) = MeanAndSampleVariance(srsMeans);
        Assert.IsTrue(lhsVariance > 0d, "Distinct LHS seeds must move the jittered strata.");
        double varianceRatio = srsVariance / lhsVariance;
        Assert.IsTrue(varianceRatio >= MinimumLhsVarianceRatio,
            $"The affine event-tree fixture must reduce replicate variance by at least " +
            $"{MinimumLhsVarianceRatio:G0}x; SRS {srsVariance:G17}, LHS " +
            $"{lhsVariance:G17}, ratio {varianceRatio:G17}.");

        const double expected = 0.4d;
        const double perDrawVariance = 2d * (0.2d * 0.2d / 12d);
        double srsPooledStandardError =
            Math.Sqrt(perDrawVariance / LhsRealizations / LhsReplicates);
        double lhsPooledStandardError =
            Math.Sqrt(perDrawVariance /
                (LhsRealizations * LhsRealizations * LhsRealizations) / LhsReplicates);
        const double standardErrorMultiplier = 25d;
        Assert.AreEqual(expected, srsMean,
            standardErrorMultiplier * srsPooledStandardError,
            "The SRS pooled aggregate must be unbiased against the analytic expectation.");
        Assert.AreEqual(expected, lhsMean,
            standardErrorMultiplier * lhsPooledStandardError,
            "The LHS pooled aggregate must be unbiased against the analytic expectation.");
        Assert.AreEqual(srsMean, lhsMean,
            standardErrorMultiplier *
            (srsPooledStandardError + lhsPooledStandardError),
            "The two unbiased scheme estimates must agree within their combined analytic errors.");

        Assert.AreEqual(LhsReplicates, lhsMeans.Distinct().Count(),
            "Every fixed replicate seed must produce a distinct LHS aggregate mean.");
        Assert.AreEqual(LhsReplicates, lhsFirstSamples.Distinct().Count(),
            "Every fixed replicate seed must move the first sampled aggregate.");

        Console.WriteLine(
            $"Event-tree aggregate LHS: N {LhsRealizations}, R {LhsReplicates}, " +
            $"seeds {LhsSeedBase}..{LhsSeedBase + LhsReplicates - 1}, expected {expected:G17}, " +
            $"SRS pooled {srsMean:G17}, variance {srsVariance:G17}, 25-SE bound " +
            $"{standardErrorMultiplier * srsPooledStandardError:G17}; LHS pooled " +
            $"{lhsMean:G17}, variance {lhsVariance:G17}, 25-SE bound " +
            $"{standardErrorMultiplier * lhsPooledStandardError:G17}; ratio {varianceRatio:G17}.");
    }

    /// <summary>Builds and samples one aggregate-failure replicate.</summary>
    /// <param name="scheme">The sampling scheme.</param>
    /// <param name="seed">The fixed replicate seed.</param>
    /// <param name="assertStrata">Whether to assert one sample in every stratum per dimension.</param>
    /// <param name="firstSample">The first sampled aggregate, for seed-sensitivity evidence.</param>
    /// <returns>The replicate aggregate-failure mean.</returns>
    private static double AggregateFailureMean(SamplingScheme scheme, int seed,
        bool assertStrata, out double firstSample)
    {
        EventTreeResponse response = BuildLhsFixture();
        response.SetupSampler(LhsRealizations, seed, scheme);
        ResponseBranchDescriptor[] descriptors = response.GetBranches().ToArray();
        int firstBranch = Array.FindIndex(descriptors,
            descriptor => descriptor.Name == "Failure A");
        int secondBranch = Array.FindIndex(descriptors,
            descriptor => descriptor.Name == "Failure B");
        Assert.IsTrue(firstBranch >= 0 && secondBranch >= 0);
        var firstStrata = new int[LhsRealizations];
        var secondStrata = new int[LhsRealizations];
        double sum = 0d;
        firstSample = double.NaN;
        for (int realization = 0; realization < LhsRealizations; realization++)
        {
            ResponseBranchSample branches = response.SampleBranches(realization);
            double aggregate = response.SampleResponseFunction(realization)[0].Y;
            if (realization == 0) firstSample = aggregate;
            sum += aggregate;
            if (!assertStrata) continue;
            double firstPercentile =
                (branches.Probabilities[firstBranch][0] - 0.05d) / 0.2d;
            double secondPercentile =
                (branches.Probabilities[secondBranch][0] - 0.15d) / 0.2d;
            firstStrata[Stratum(firstPercentile)]++;
            secondStrata[Stratum(secondPercentile)]++;
        }
        if (assertStrata)
        {
            for (int stratum = 0; stratum < LhsRealizations; stratum++)
            {
                Assert.AreEqual(1, firstStrata[stratum],
                    $"Failure A LHS stratum {stratum} at seed {seed}.");
                Assert.AreEqual(1, secondStrata[stratum],
                    $"Failure B LHS stratum {stratum} at seed {seed}.");
            }
        }
        return sum / LhsRealizations;
    }

    /// <summary>Builds the affine two-dimension uncertain event-tree fixture.</summary>
    /// <returns>The event-tree response whose aggregate expectation is exactly 0.4.</returns>
    private static EventTreeResponse BuildLhsFixture()
    {
        var tree = new EventTree();
        tree.Add(tree.Root.Id, new ChanceNode("Failure A",
            new ProbabilitySource(ConstantUniformTable(0.05d, 0.25d))));
        tree.Add(tree.Root.Id, new ChanceNode("Failure B",
            new ProbabilitySource(ConstantUniformTable(0.15d, 0.35d))));
        tree.Add(tree.Root.Id, new RemainderNode("Survival") { IsFailure = false });
        EventTreeResponse response = Response(tree);
        response.Name = "Aggregate LHS variance fixture";
        return response;
    }

    /// <summary>Builds a two-knot uncertain table with one shared uniform percentile stream.</summary>
    /// <param name="minimum">The uniform minimum.</param>
    /// <param name="maximum">The uniform maximum.</param>
    /// <returns>The aligned uncertainty table.</returns>
    private static UncertainOrderedPairedData ConstantUniformTable(double minimum, double maximum)
    {
        return new UncertainOrderedPairedData(
            new[]
            {
                new UncertainOrdinate(0d, new Uniform(minimum, maximum)),
                new UncertainOrdinate(1d, new Uniform(minimum, maximum)),
            }, true, SortOrder.Ascending, false, SortOrder.None,
            UnivariateDistributionType.Uniform);
    }

    /// <summary>Maps a unit percentile to its bounded LHS stratum.</summary>
    /// <param name="percentile">The recovered uniform percentile.</param>
    /// <returns>The stratum index.</returns>
    private static int Stratum(double percentile)
    {
        return Math.Clamp((int)Math.Floor(percentile * LhsRealizations),
            0, LhsRealizations - 1);
    }

    /// <summary>Computes the mean and unbiased sample variance of replicate values.</summary>
    /// <param name="values">The replicate statistics.</param>
    /// <returns>The mean and sample variance.</returns>
    private static (double Mean, double Variance) MeanAndSampleVariance(
        IReadOnlyList<double> values)
    {
        double mean = values.Sum() / values.Count;
        double sumSquares = values.Sum(value => (value - mean) * (value - mean));
        return (mean, sumSquares / (values.Count - 1));
    }

    /// <summary>The fixed analysis seed for thread-count reproducibility.</summary>
    private const int ThreadReproducibilitySeed = 10_202_791;

    /// <summary>The full-uncertainty realization count for each scheduling run.</summary>
    private const int ThreadReproducibilityRealizations = 100;

    /// <summary>
    /// Runs the same expanded event-tree analysis sequentially, with four workers, and twice
    /// under default scheduling. Aggregate curves, every per-leaf curve, JSON, definition/run
    /// hashes, complete seed maps, and append-only branch ports must be bit-identical.
    /// </summary>
    [TestMethod]
    public void Test_ExpandedEventTree_IsBitIdenticalAcrossThreadCountsAndSchedules()
    {
        RiskAnalysis analysis = BuildThreadReproducibilityAnalysis(
            out EventTreeResponse response, out SystemComponent component);

        ThreadSnapshot sequential = RunThreadSnapshot(analysis, response, component,
            maximumDegreeOfParallelism: 1, out int sequentialWorkers);
        ThreadSnapshot multiWorker = RunThreadSnapshot(analysis, response, component,
            maximumDegreeOfParallelism: 4, out int multiWorkers);
        ThreadSnapshot defaultFirst = RunThreadSnapshot(analysis, response, component,
            maximumDegreeOfParallelism: null, out int defaultFirstWorkers);
        ThreadSnapshot defaultSecond = RunThreadSnapshot(analysis, response, component,
            maximumDegreeOfParallelism: null, out int defaultSecondWorkers);

        Assert.AreEqual(1, sequentialWorkers,
            "The sequential execution-control seam must use exactly one ensemble worker.");
        Assert.IsTrue(multiWorkers >= 2,
            $"The capped multi-worker run must use at least two threads; observed {multiWorkers}.");
        AssertThreadSnapshotsEqual(sequential, multiWorker, "sequential versus four-worker");
        AssertThreadSnapshotsEqual(sequential, defaultFirst, "sequential versus default run 1");
        AssertThreadSnapshotsEqual(sequential, defaultSecond, "sequential versus default run 2");

        Console.WriteLine(
            $"Event-tree thread reproducibility: sequential workers {sequentialWorkers}, " +
            $"capped workers {multiWorkers}, default workers {defaultFirstWorkers}/" +
            $"{defaultSecondWorkers}, realizations {ThreadReproducibilityRealizations}, " +
            $"seed {ThreadReproducibilitySeed}, branches {sequential.BranchPorts.Length}, " +
            $"seed-map hash {sequential.SamplerSeedMapHash}, analysis hash " +
            $"{sequential.AnalysisContentHash}.");
    }

    /// <summary>Builds the end-to-end expanded-output thread-count fixture.</summary>
    /// <param name="response">The event-tree response definition.</param>
    /// <param name="component">The graph-owned system component.</param>
    /// <returns>The configured full-uncertainty analysis.</returns>
    private static RiskAnalysis BuildThreadReproducibilityAnalysis(
        out EventTreeResponse response, out SystemComponent component)
    {
        var tree = new EventTree();
        var loaded = new ChanceNode("Loaded",
            new ProbabilitySource(ThreadUniformTable(0.4d, 0.5d)))
        {
            IsFailure = false,
        };
        tree.Add(tree.Root.Id, loaded);
        tree.Add(tree.Root.Id,
            new ChanceNode("Direct failure", new ProbabilitySource(0.1d)));
        tree.Add(tree.Root.Id,
            new RemainderNode("Root survival") { IsFailure = false });
        tree.Add(loaded.Id, new ChanceNode("Loaded failure",
            new ProbabilitySource(ThreadUniformTable(0.2d, 0.4d))));
        tree.Add(loaded.Id,
            new RemainderNode("Loaded survival") { IsFailure = false });
        response = Response(new[] { 0d, 15d, 30d }, tree);
        response.Name = "Thread reproducibility event tree";

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
            Name = "Expanded event-tree component",
            FailureModeMethod = FailureModeMethod.MutuallyExclusive,
        };
        HazardElement hazard = component.Graph.GetElements<HazardElement>().Single();
        var responseElement = new ResponseElement("Expanded event tree")
        {
            Function = response,
            ExpandBranchOutputs = true,
            Input = new RiskConnection(hazard),
        };
        component.Graph.AddElement(responseElement);
        foreach (ResponseBranchDescriptor branch in response.GetBranches())
        {
            var terminal = new ConsequenceElement(branch.Name)
            {
                Input = responseElement.CreateBranchConnection(branch.Id),
            };
            terminal.Functions.Add(ThreadConsequence(
                branch.Name + " consequence", 100d * (branch.OutputPort + 1)));
            component.Graph.AddElement(terminal);
        }

        var analysis = new RiskAnalysis(new[] { component })
        {
            Name = "Thread-count reproducibility",
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

    /// <summary>Builds one constant three-knot uniform source aligned to the response axis.</summary>
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

    /// <summary>Builds one deterministic consequence for an expanded branch.</summary>
    /// <param name="name">The function name.</param>
    /// <param name="maximum">The consequence at stage 30.</param>
    /// <returns>The consequence function.</returns>
    private static TabularConsequence ThreadConsequence(string name, double maximum)
    {
        return new TabularConsequence
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Damage",
            ConsequenceUnit = "$",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(0d, new Deterministic(0d)),
                    new UncertainOrdinate(30d, new Deterministic(maximum)),
                }, true, SortOrder.Ascending, false, SortOrder.None,
                UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Runs one scheduling configuration and captures every deterministic contract.</summary>
    /// <param name="analysis">The reusable analysis instance.</param>
    /// <param name="response">The event-tree response definition.</param>
    /// <param name="component">The graph-owned component.</param>
    /// <param name="maximumDegreeOfParallelism">The internal cap, or null for production default.</param>
    /// <param name="workerCount">The number of observed ensemble worker threads.</param>
    /// <returns>The immutable comparison snapshot.</returns>
    private static ThreadSnapshot RunThreadSnapshot(RiskAnalysis analysis,
        EventTreeResponse response, SystemComponent component,
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
        string[] branchPorts = response.GetBranches()
            .OrderBy(branch => branch.OutputPort)
            .Select(branch =>
                $"{branch.OutputPort.ToString(CultureInfo.InvariantCulture)}|" +
                $"{branch.Id:D}|{branch.Name}|{branch.IsFailure}")
            .ToArray();
        var exactJsonOptions = new JsonSerializerOptions
        {
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };
        string[] leafCurves = mean.Components[0].FailureModes
            .Select(mode => JsonSerializer.Serialize(mode, exactJsonOptions))
            .ToArray();
        return new ThreadSnapshot(
            results.ToJson(),
            mean.ToJson(),
            JsonSerializer.Serialize(mean.Components[0].Curves.Fail, exactJsonOptions),
            leafCurves,
            branchPorts,
            Convert.ToHexString(response.CanonicalHash()),
            Convert.ToHexString(component.CanonicalHash()),
            Convert.ToHexString(analysis.Options.CanonicalHash()),
            manifest.AnalysisContentHash,
            manifest.EffectiveOptionsHash,
            manifest.SamplerSeedMapHash,
            SeedFingerprint(seeds));

        // Captures the one completion error for this synchronous run.
        void CaptureCompletion(object? sender, AnalysisRunCompletedEventArgs args) =>
            completionError = args.Error;
    }

    /// <summary>Creates an exact textual fingerprint of the complete captured sampler seed map.</summary>
    /// <param name="map">The captured seed map.</param>
    /// <returns>The deterministic seed fingerprint.</returns>
    private static string SeedFingerprint(SamplerSeedMap map)
    {
        string components = string.Join(";",
            map.ComponentSeeds.Select(seeds => string.Join(",", seeds)));
        return components + "|" + map.JointSeedBase.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Asserts every deterministic output contract in two scheduling snapshots.</summary>
    /// <param name="expected">The sequential baseline.</param>
    /// <param name="actual">The comparison run.</param>
    /// <param name="label">The scheduling comparison label.</param>
    private static void AssertThreadSnapshotsEqual(ThreadSnapshot expected,
        ThreadSnapshot actual, string label)
    {
        Assert.AreEqual(expected.ResultsJson, actual.ResultsJson, $"{label}: results JSON.");
        Assert.AreEqual(expected.MeanJson, actual.MeanJson, $"{label}: mean-results JSON.");
        Assert.AreEqual(expected.AggregateFailureCurveJson,
            actual.AggregateFailureCurveJson, $"{label}: aggregate failure curve.");
        CollectionAssert.AreEqual(expected.LeafCurveJson, actual.LeafCurveJson,
            $"{label}: every expanded per-leaf curve.");
        CollectionAssert.AreEqual(expected.BranchPorts, actual.BranchPorts,
            $"{label}: stable append-only branch IDs and ports.");
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
    private sealed record ThreadSnapshot(
        string ResultsJson,
        string MeanJson,
        string AggregateFailureCurveJson,
        string[] LeafCurveJson,
        string[] BranchPorts,
        string ResponseHash,
        string ComponentHash,
        string OptionsHash,
        string AnalysisContentHash,
        string EffectiveOptionsHash,
        string SamplerSeedMapHash,
        string SeedMap);
}
