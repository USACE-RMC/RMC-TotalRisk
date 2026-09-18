using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.Systems.Components;
using RMC.TotalRisk.Systems.Components.Graph;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees.Nodes;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Nodes;

namespace RMC.TotalRisk.PerfHarness
{
    /// <summary>
    /// The performance harness: Stopwatch medians and a results-JSON SHA-256 over the
    /// reference fixtures, so bit-inert optimizations are byte-verified and value-moving
    /// ones are measured. Not part of the solution — run with
    /// <c>dotnet run -c Release --project scripts/perf/PerfHarness</c>; results are recorded in
    /// <c>scripts/perf/RESULTS.md</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Fixtures: <b>F1</b> — the PROGRESS-recorded trivial 1D fixture (uncertain triangular
    /// fragility) at N = 1000 full uncertainty (the ≈54 s pre-optimization reference); <b>F2</b>
    /// — a two-component joint system at N = 200; <b>F3</b> — F1 with a second consequence type
    /// (the multi-consequence axis); <b>F4</b> — a dependent competing-risks component, the one
    /// shape whose cost is dominated by construction rather than integration, because the
    /// incidence factory evaluates a multivariate-normal rectangle integral per unit per hazard
    /// level; <b>F5</b> — a large event tree with repeated independent external-link occurrences,
    /// measured directly at setup and evaluation boundaries; <b>F6</b> — a four-child mixture
    /// composite hazard over bootstrap posteriors with an uncertain day/night composite
    /// consequence, the shape whose per-realization cost is the combined distribution rebuild:
    /// <c>Mixture.CreateEmpiricalCDF()</c> re-tabulates ~200 bins over the K = 4 sampled children
    /// every realization before the risk integrand inverts the result at every quadrature node;
    /// <b>F7</b> — a fault tree whose ~20 pooled basic events repeat three to five times through
    /// shared-logical transfers across eight crossing trains and two k-of-n voting gates, with
    /// mixed scalar and uncertain-table sources, measured directly at the sampler-setup and
    /// indexed-read boundaries with the frozen decision-diagram size reported; <b>F8</b> — a
    /// single bivariate component (independence copula, 20 conditional bins, uncertain tabular
    /// marginals) carrying a Secondary-bound failure mode and a joint bivariate-response failure
    /// mode at N = 1000, the shape whose per-evaluation cost is the conditional-bin sweep —
    /// every hazard evaluation runs the combination kernels once per bin node.
    /// Each engine fixture reports the mean-only and full-uncertainty medians of three runs plus
    /// the SHA-256 of the concatenated results JSON (mean, lower, upper, median realizations and
    /// the summary ensemble).
    /// </para>
    /// </remarks>
    public static class Program
    {
        /// <summary>
        /// When set by <c>--dump &lt;path&gt;</c>, the concatenated results JSON of each fixture is
        /// written to <c>&lt;path&gt;.&lt;fixture&gt;.json</c> as well as hashed — the diff that shows
        /// whether a moved byte gate is a numeric change or an added field.
        /// </summary>
        private static string? _dumpPath;

        /// <summary>The measurement repetitions per fixture (median reported). One by default —
        /// results are deterministic, so the hash gate needs a single run and the timing signal
        /// at the fixture scale (tens of seconds) resolves the targeted multiples; pass
        /// <c>--reps 3</c> for the committed baseline/final table rows.</summary>
        private static int _reps = 1;

        /// <summary>Runs the requested fixtures (args: optional <c>--reps N</c> plus fixture names among F1 F2 F3 F4 F5 F6 F7 F8; default all).</summary>
        /// <param name="args">Optional repetition count and fixture filter.</param>
        /// <returns>Zero on success.</returns>
        public static int Main(string[] args)
        {
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--reps", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    _reps = Math.Max(1, int.Parse(args[++i], CultureInfo.InvariantCulture));
                }
                else if (string.Equals(args[i], "--dump", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    _dumpPath = args[++i];
                }
                else
                {
                    wanted.Add(args[i]);
                }
            }
            bool All(string name) => wanted.Count == 0 || wanted.Contains(name);

            Console.WriteLine($"RMC.TotalRisk performance harness — median of {_reps}, Release recommended.");
            Console.WriteLine($"Machine: {Environment.MachineName}, {Environment.ProcessorCount} logical processors.");
            Console.WriteLine();

            if (All("F1")) Measure("F1 1D single-component, N=1000", () => BuildF1());
            if (All("F2")) Measure("F2 joint two-component, N=200 (reduced VEGAS budget)", () => BuildF2());
            if (All("F3")) Measure("F3 = F1 + second consequence type", () => BuildF3());
            if (All("F4")) Measure("F4 dependent competing risks, 4 modes, N=200", () => BuildF4());
            if (All("F5")) MeasureEventTree();
            if (All("F6")) Measure("F6 composite hazard + day/night consequence, N=500", () => BuildF6());
            if (All("F7")) MeasureFaultTree();
            if (All("F8")) Measure("F8 bivariate component, 20 conditional bins, N=1000", () => BuildF8());
            return 0;
        }

        /// <summary>Measures one fixture: mean-only and full-uncertainty medians plus the results hash.</summary>
        /// <param name="label">The fixture label.</param>
        /// <param name="factory">Builds a fresh analysis per run.</param>
        private static void Measure(string label, Func<RiskAnalysis> factory)
        {
            // Warm-up run (JIT) — timing discarded; its results feed the hash.
            var warm = factory();
            warm.RunAsync().GetAwaiter().GetResult();

            double meanOnlyMedian = Median(() =>
            {
                var analysis = factory();
                return Time(() => analysis.RunAsync().GetAwaiter().GetResult());
            });

            string hash = string.Empty;
            long allocatedBytes = 0;
            int gen0 = 0, gen1 = 0, gen2 = 0;
            double fullMedian = Median(() =>
            {
                var analysis = factory();
                analysis.Options.EstimateMeanRiskOnly = false;
                long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
                int gen0Before = GC.CollectionCount(0);
                int gen1Before = GC.CollectionCount(1);
                int gen2Before = GC.CollectionCount(2);
                double elapsed = Time(() => analysis.RunAsync().GetAwaiter().GetResult());
                allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
                gen0 = GC.CollectionCount(0) - gen0Before;
                gen1 = GC.CollectionCount(1) - gen1Before;
                gen2 = GC.CollectionCount(2) - gen2Before;
                hash = ResultsHash(analysis, label);
                return elapsed;
            });

            Console.WriteLine($"{label}");
            Console.WriteLine($"  mean-only median: {meanOnlyMedian,10:F3} s");
            Console.WriteLine($"  full-MC   median: {fullMedian,10:F3} s");
            Console.WriteLine($"  full-MC allocated: {allocatedBytes / (1024d * 1024d * 1024d),8:F2} GB (GC gen0/1/2: {gen0}/{gen1}/{gen2})");
            Console.WriteLine($"  results SHA-256:  {hash}");
            Console.WriteLine();
        }

        /// <summary>
        /// Measures the large event-tree fixture at the sampler-setup and repeated-read
        /// boundaries. The result hash covers every branch ordinate from the final indexed read.
        /// </summary>
        private static void MeasureEventTree()
        {
            const int sampleSize = 64;
            const int evaluations = 32;
            const int seed = 24681357;

            EventTreeResponse warm = BuildF5();
            warm.SetupSampler(sampleSize, seed, SamplingScheme.LatinHypercube);
            ResponseBranchSample warmSample = warm.SampleBranches(0);

            long setupAllocatedBytes = 0;
            double setupMedian = Median(() =>
            {
                EventTreeResponse response = BuildF5();
                long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
                double elapsed = Time(() => response.SetupSampler(
                    sampleSize, seed, SamplingScheme.LatinHypercube));
                setupAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
                return elapsed;
            });

            long evaluationAllocatedBytes = 0;
            ResponseBranchSample finalSample = warmSample;
            double evaluationMedian = Median(() =>
            {
                EventTreeResponse response = BuildF5();
                response.SetupSampler(sampleSize, seed, SamplingScheme.LatinHypercube);
                long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
                double elapsed = Time(() =>
                {
                    for (int i = 0; i < evaluations; i++)
                        finalSample = response.SampleBranches(i % sampleSize);
                });
                evaluationAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
                return elapsed;
            });

            Console.WriteLine("F5 large event tree, 24 repeated independent links");
            Console.WriteLine($"  expanded nodes:       {warm.CompiledInstructionCount,10}");
            Console.WriteLine($"  expanded edges:       {warm.CompiledEdgeCount,10}");
            Console.WriteLine($"  compiled plans:       {warm.CompiledPlanBuildCount,10}");
            Console.WriteLine($"  authored nodes:       {warm.EventTree.Nodes.Count,10}");
            Console.WriteLine($"  expanded branches:   {warmSample.Branches.Count,10}");
            Console.WriteLine($"  setup median:         {setupMedian,10:F6} s");
            Console.WriteLine($"  setup allocated:      {setupAllocatedBytes / (1024d * 1024d),10:F2} MB");
            Console.WriteLine($"  {evaluations,2} indexed reads:     {evaluationMedian,10:F6} s");
            Console.WriteLine($"  reads allocated:      {evaluationAllocatedBytes / (1024d * 1024d),10:F2} MB");
            Console.WriteLine($"  result SHA-256:       {BranchSampleHash(finalSample)}");
            Console.WriteLine();
        }

        /// <summary>
        /// Measures the shared-event fault-tree fixture at the sampler-setup and repeated
        /// indexed-read boundaries. The result hash covers every ordinate of the final indexed
        /// top-event curve, and the frozen decision-diagram size is reported alongside the
        /// unified-variable and published-plan counts.
        /// </summary>
        private static void MeasureFaultTree()
        {
            const int sampleSize = 64;
            const int evaluations = 32;
            const int seed = 24681357;

            FaultTreeResponse warm = BuildF7();
            warm.SetupSampler(sampleSize, seed, SamplingScheme.LatinHypercube);
            OrderedPairedData warmSample = warm.SampleResponseFunction(0);

            long setupAllocatedBytes = 0;
            double setupMedian = Median(() =>
            {
                FaultTreeResponse response = BuildF7();
                long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
                double elapsed = Time(() => response.SetupSampler(
                    sampleSize, seed, SamplingScheme.LatinHypercube));
                setupAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
                return elapsed;
            });

            long evaluationAllocatedBytes = 0;
            OrderedPairedData finalSample = warmSample;
            double evaluationMedian = Median(() =>
            {
                FaultTreeResponse response = BuildF7();
                response.SetupSampler(sampleSize, seed, SamplingScheme.LatinHypercube);
                long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
                double elapsed = Time(() =>
                {
                    for (int i = 0; i < evaluations; i++)
                        finalSample = response.SampleResponseFunction(i % sampleSize);
                });
                evaluationAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
                return elapsed;
            });

            Console.WriteLine("F7 fault tree, repeated shared basic events");
            Console.WriteLine($"  decision nodes:       {warm.CompiledDecisionNodeCount,10}");
            Console.WriteLine($"  unified variables:    {warm.CompiledVariableCount,10}");
            Console.WriteLine($"  compiled plans:       {warm.CompiledPlanBuildCount,10}");
            Console.WriteLine($"  authored nodes:       {warm.FaultTree.Nodes.Count,10}");
            Console.WriteLine($"  setup median:         {setupMedian,10:F6} s");
            Console.WriteLine($"  setup allocated:      {setupAllocatedBytes / (1024d * 1024d),10:F2} MB");
            Console.WriteLine($"  {evaluations,2} indexed reads:     {evaluationMedian,10:F6} s");
            Console.WriteLine($"  reads allocated:      {evaluationAllocatedBytes / (1024d * 1024d),10:F2} MB");
            Console.WriteLine($"  result SHA-256:       {CurveHash(finalSample)}");
            Console.WriteLine();
        }

        /// <summary>The median of the configured number of samples of a timed action.</summary>
        /// <param name="sample">Produces one elapsed-seconds sample.</param>
        /// <returns>The median seconds.</returns>
        private static double Median(Func<double> sample)
        {
            var samples = new double[_reps];
            for (int i = 0; i < _reps; i++)
            {
                samples[i] = sample();
            }
            Array.Sort(samples);
            return samples[_reps / 2];
        }

        /// <summary>Times one action in seconds.</summary>
        /// <param name="action">The action to time.</param>
        /// <returns>The elapsed seconds.</returns>
        private static double Time(Action action)
        {
            var stopwatch = Stopwatch.StartNew();
            action();
            stopwatch.Stop();
            return stopwatch.Elapsed.TotalSeconds;
        }

        /// <summary>
        /// The SHA-256 of the run's concatenated results JSON — the byte gate for bit-inert
        /// refactors (mean, lower, upper, median realizations and the summary ensemble).
        /// </summary>
        /// <param name="analysis">The finished analysis.</param>
        /// <returns>The lowercase hex digest.</returns>
        private static string ResultsHash(RiskAnalysis analysis, string label)
        {
            var builder = new StringBuilder();
            builder.Append(analysis.MeanRiskResults?.ToJson() ?? string.Empty).Append('|');
            builder.Append(analysis.LowerRiskResults?.ToJson() ?? string.Empty).Append('|');
            builder.Append(analysis.UpperRiskResults?.ToJson() ?? string.Empty).Append('|');
            builder.Append(analysis.MedianRiskResults?.ToJson() ?? string.Empty).Append('|');
            builder.Append(analysis.RiskResults?.ToJson() ?? string.Empty);
            string payload = builder.ToString();
            if (_dumpPath != null)
            {
                System.IO.File.WriteAllText($"{_dumpPath}.{label.Split(' ')[0]}.json", payload);
            }
            byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
            return Convert.ToHexString(digest).ToLowerInvariant();
        }

        /// <summary>Computes the deterministic byte gate for one exhaustive event-tree sample.</summary>
        /// <param name="sample">The branch sample.</param>
        /// <returns>The lowercase SHA-256 digest.</returns>
        private static string BranchSampleHash(ResponseBranchSample sample)
        {
            var builder = new StringBuilder();
            for (int branch = 0; branch < sample.Branches.Count; branch++)
            {
                builder.Append(sample.Branches[branch].IsFailure ? '1' : '0').Append('|');
                for (int h = 0; h < sample.Hazards.Count; h++)
                {
                    builder.Append(sample.Probabilities[branch][h]
                        .ToString("G17", CultureInfo.InvariantCulture)).Append('|');
                }
            }
            byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
            return Convert.ToHexString(digest).ToLowerInvariant();
        }

        /// <summary>Builds the F1 trivial 1D fixture at N = 1000 (uncertain fragility).</summary>
        private static RiskAnalysis BuildF1()
        {
            var analysis = new RiskAnalysis(new[] { Component(twoTypes: false) })
            {
                SpecifiedConsequence = "Life Loss",
                ConsequenceUnit = "lives",
            };
            analysis.Options.Realizations = 1000;
            return analysis;
        }

        /// <summary>
        /// Builds the F2 two-component joint fixture at N = 200 with a reduced VEGAS budget
        /// (warm-up 1000 × 2 cycles, 2000 final evaluations × 5 recording passes per
        /// realization). The default budget runs ~110k evaluations per realization — far too
        /// heavy for an iteration-speed fixture — and optimization deltas are relative, so the
        /// reduced budget exercises the identical code paths at ~1/9 the cost.
        /// </summary>
        private static RiskAnalysis BuildF2()
        {
            var analysis = new RiskAnalysis(new[] { Component(twoTypes: false), Component(twoTypes: false) })
            {
                SpecifiedConsequence = "Life Loss",
                ConsequenceUnit = "lives",
            };
            analysis.Options.SystemRiskMethod = SystemRiskType.JointRiskMethod;
            analysis.Options.Realizations = 200;
            analysis.Options.UseDefaults = false;
            analysis.Options.WarmupEvaluations = 1000;
            analysis.Options.WarmupCycles = 2;
            analysis.Options.FinalEvaluations = 2000;
            return analysis;
        }

        /// <summary>Builds the F3 fixture: F1 with a second consequence type declared and carried.</summary>
        private static RiskAnalysis BuildF3()
        {
            var analysis = new RiskAnalysis(new[] { Component(twoTypes: true) })
            {
                SpecifiedConsequence = "Life Loss",
                ConsequenceUnit = "lives",
            };
            analysis.AdditionalConsequenceTypes.Add(new ConsequenceTypeDescriptor("Damages", "$"));
            analysis.Options.Realizations = 1000;
            return analysis;
        }

        /// <summary>
        /// Builds the F4 dependent competing-risks fixture: four failure modes under a
        /// perfectly-negative dependency at N = 200.
        /// </summary>
        /// <remarks>
        /// The cost lives in <c>SampledComponent</c>'s constructor rather than in the risk integral:
        /// a dependent competing configuration evaluates a rectangle integral per combination unit
        /// per hazard level, once per realization. The component is deterministic so it is eligible
        /// for the run-shared pre-processing; an uncertain variant measures a different thing.
        /// </remarks>
        private static RiskAnalysis BuildF4()
        {
            var component = new SystemComponent { Name = "Competing Dam" };
            component.HazardFunction = new TabularHazard
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
            for (int i = 0; i < 4; i++)
            {
                var fragility = new TabularResponse
                {
                    Name = $"Fragility {i}",
                    SpecifiedHazard = "Stage",
                    HazardUnit = "ft",
                    UncertainOrderedPairedData = new UncertainOrderedPairedData(
                        new[]
                        {
                            new UncertainOrdinate(8d + i, new Deterministic(0d)),
                            new UncertainOrdinate(22d + i, new Deterministic(1d)),
                        },
                        true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
                };
                component.AddFailureMode(new FailureMode(null, null, fragility,
                    Consequence($"Loss {i}", "Life Loss", "lives", 200d + 100d * i)));
            }
            component.AddFailureMode(new FailureMode(null, null, null, Consequence("Non-Failure Loss", "Life Loss", "lives", 60d)));
            component.FailureModeMethod = FailureModeMethod.CompetingFailures;
            component.FailureModeDependency = DependencyType.PerfectlyNegative;

            var analysis = new RiskAnalysis(new[] { component })
            {
                SpecifiedConsequence = "Life Loss",
                ConsequenceUnit = "lives",
            };
            analysis.Options.Realizations = 200;
            return analysis;
        }

        /// <summary>
        /// Builds the F6 composite fixture at N = 500: a four-child mixture composite hazard over
        /// bootstrap posteriors (LnNormal scenario parents, 500 replications each) and an
        /// uncertain day/night mixture consequence, behind a deterministic parametric fragility.
        /// Every realization rebuilds the combined hazard distribution — the
        /// <c>Mixture.CreateEmpiricalCDF()</c> tabulation over the sampled children — before the
        /// risk integrand inverts it at every quadrature node.
        /// </summary>
        private static RiskAnalysis BuildF6()
        {
            var composite = new CompositeHazard(new[]
            {
                new WeightedHazardFunction(BootstrapHazard("Scenario 1", new LnNormal(85d, 5d)), 0.4d),
                new WeightedHazardFunction(BootstrapHazard("Scenario 2", new LnNormal(65d, 20d)), 0.3d),
                new WeightedHazardFunction(BootstrapHazard("Scenario 3", new LnNormal(75d, 12d)), 0.2d),
                new WeightedHazardFunction(BootstrapHazard("Scenario 4", new LnNormal(90d, 15d)), 0.1d),
            })
            {
                Name = "Composite Stage Frequency",
                SpecifiedHazard = "Stage",
                HazardUnit = "ft",
                CompositeCombinationType = CompositeCombinationType.Mixture,
            };

            var fragility = new ParametricResponse
            {
                Name = "Fragility",
                SpecifiedHazard = "Stage",
                HazardUnit = "ft",
                ParentDistribution = new Normal(140d, 30d),
                IsUncertain = false,
            };
            fragility.Estimate();

            var consequence = new CompositeConsequence(new[]
            {
                new WeightedConsequenceFunction(CompositeLoss("Day", 1d, uncertain: true), 0.45d),
                new WeightedConsequenceFunction(CompositeLoss("Night", 0.5d, uncertain: true), 0.55d),
            })
            {
                Name = "Day or Night Loss",
                SpecifiedHazard = "Stage",
                HazardUnit = "ft",
                SpecifiedConsequence = "Damage",
                ConsequenceUnit = "dollars",
                CompositeFunctionType = CompositeFunctionType.Mixture,
            };

            var component = new SystemComponent { Name = "Composite Dam", HazardFunction = composite };
            component.AddFailureMode(new FailureMode(null, null, fragility, consequence));
            component.AddFailureMode(new FailureMode(null, null, null, CompositeLoss("Non-Failure Loss", 0.1d, uncertain: false)));

            var analysis = new RiskAnalysis(new[] { component })
            {
                SpecifiedConsequence = "Damage",
                ConsequenceUnit = "dollars",
            };
            analysis.Options.Realizations = 500;
            return analysis;
        }

        /// <summary>
        /// Builds the F8 bivariate fixture at N = 1000: an independence-copula hazard over
        /// uncertain tabular marginals at the default 20 conditional bins, carrying a
        /// Secondary-bound pool-fragility failure mode and a joint bivariate-response failure
        /// mode plus a primary background path. Every hazard evaluation sweeps the 21 conditional
        /// nodes, running the combination kernels per node — the (bins + 1)× cost shape the
        /// conditional-trapezoid design prices.
        /// </summary>
        private static RiskAnalysis BuildF8()
        {
            static TabularHazard Marginal(string name, string hazard, double median, double extreme)
            {
                return new TabularHazard
                {
                    Name = name,
                    SpecifiedHazard = hazard,
                    HazardUnit = "ft",
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

            static TabularConsequence PathDamages(string name, string hazard, double max, double valueAtMax)
            {
                return new TabularConsequence
                {
                    Name = name,
                    SpecifiedHazard = hazard,
                    HazardUnit = "ft",
                    SpecifiedConsequence = "Damage",
                    ConsequenceUnit = "dollars",
                    UncertainOrderedPairedData = new UncertainOrderedPairedData(
                        new[] { new UncertainOrdinate(0d, new Deterministic(0d)), new UncertainOrdinate(max, new Deterministic(valueAtMax)) },
                        true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
                };
            }

            var joint = new BivariateHazard(
                Marginal("Surge Marginal", "Surge", 10d, 30d),
                Marginal("Pool Marginal", "Pool Elevation", 50d, 100d))
            {
                Name = "Joint Hazard",
                SpecifiedHazard = "Surge",
                HazardUnit = "ft",
                SecondarySpecifiedHazard = "Pool Elevation",
                SecondaryHazardUnit = "ft",
            };
            var component = new SystemComponent(joint) { Name = "Joint Dam" };
            var hazard = component.Graph.GetElements<HazardElement>().Single();

            // The Secondary-bound path: the hazard's secondary output drives an uncertain pool
            // fragility whose damages read the pool signal.
            var poolBreach = new ResponseElement("Pool Breach")
            {
                Function = new TabularResponse
                {
                    Name = "Pool Fragility",
                    SpecifiedHazard = "Pool Elevation",
                    HazardUnit = "ft",
                    UncertainOrderedPairedData = new UncertainOrderedPairedData(
                        new[]
                        {
                            new UncertainOrdinate(20d, new Triangular(0d, 0.02d, 0.05d)),
                            new UncertainOrdinate(100d, new Triangular(0.6d, 0.8d, 1d)),
                        },
                        true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Triangular),
                },
                Input = new RiskConnection(hazard, 1),
            };
            component.Graph.AddElement(poolBreach);
            var poolFailure = new ConsequenceElement("Pool Damages") { Input = new RiskConnection(poolBreach) };
            poolFailure.Functions.Add(PathDamages("Pool Loss", "Pool Elevation", 100d, 800d));
            component.Graph.AddElement(poolFailure);

            // The joint-mode path: the surface response consumes both hazard outputs.
            var surface = new BivariateResponse
            {
                Name = "Joint Fragility",
                SpecifiedHazard = "Surge",
                HazardUnit = "ft",
                SecondarySpecifiedHazard = "Pool Elevation",
                SecondaryHazardUnit = "ft",
            };
            surface.PrimaryHazardLevels.Clear();
            surface.PrimaryHazardLevels.Add(0d);
            surface.PrimaryHazardLevels.Add(15d);
            surface.PrimaryHazardLevels.Add(30d);
            surface.SecondaryHazardLevels.Clear();
            surface.SecondaryHazardLevels.Add(new WeightedHazardLevel { Level = 0d, Weight = 0.3d });
            surface.SecondaryHazardLevels.Add(new WeightedHazardLevel { Level = 50d, Weight = 0.4d });
            surface.SecondaryHazardLevels.Add(new WeightedHazardLevel { Level = 100d, Weight = 0.3d });
            surface.ProbabilityValues = new[,]
            {
                { 0.001d, 0.01d, 0.05d },
                { 0.02d, 0.10d, 0.30d },
                { 0.10d, 0.40d, 0.80d },
            };
            var jointBreach = new ResponseElement("Joint Breach")
            {
                Function = surface,
                Input = new RiskConnection(hazard),
                SecondaryInput = new RiskConnection(hazard, 1),
            };
            component.Graph.AddElement(jointBreach);
            var jointFailure = new ConsequenceElement("Joint Damages") { Input = new RiskConnection(jointBreach) };
            jointFailure.Functions.Add(PathDamages("Joint Loss", "Surge", 30d, 1200d));
            component.Graph.AddElement(jointFailure);

            var background = new ConsequenceElement("Baseline Damages") { Input = new RiskConnection(hazard) };
            background.Functions.Add(PathDamages("Baseline Loss", "Surge", 30d, 90d));
            component.Graph.AddElement(background);

            var analysis = new RiskAnalysis(new[] { component })
            {
                SpecifiedConsequence = "Damage",
                ConsequenceUnit = "dollars",
            };
            analysis.Options.Realizations = 1000;
            return analysis;
        }

        /// <summary>
        /// Builds one uncertain bootstrap-posterior hazard child for F6 (method-of-moments,
        /// effective record length 50, 500 replications — the posterior must serve the engine's
        /// realization count).
        /// </summary>
        /// <param name="name">The scenario name.</param>
        /// <param name="parent">The parent stage-frequency distribution.</param>
        /// <returns>The estimated uncertain hazard.</returns>
        private static ParametricUnivariateHazard BootstrapHazard(string name, UnivariateDistributionBase parent)
        {
            var hazard = new ParametricUnivariateHazard
            {
                Name = name,
                SpecifiedHazard = "Stage",
                HazardUnit = "ft",
                ParentDistribution = parent,
                EffectiveRecordLength = 50,
                Realizations = 500,
            };
            hazard.Estimate();
            return hazard;
        }

        /// <summary>
        /// Builds the F6 piecewise-linear loss table over (60, 250) ft, optionally with symmetric
        /// triangular uncertainty on each positive ordinate.
        /// </summary>
        /// <param name="name">The consequence name.</param>
        /// <param name="scale">The multiplier applied to the base ordinates.</param>
        /// <param name="uncertain">True for symmetric triangular ordinate uncertainty.</param>
        /// <returns>The tabular consequence.</returns>
        private static TabularConsequence CompositeLoss(string name, double scale, bool uncertain)
        {
            double[] x = { 60d, 100d, 140d, 200d, 250d };
            double[] y = { 0d, 10d, 100d, 1000d, 1500d };
            var ordinates = new UncertainOrdinate[x.Length];
            for (int i = 0; i < x.Length; i++)
            {
                double value = scale * y[i];
                ordinates[i] = new UncertainOrdinate(x[i], uncertain
                    ? new Triangular(0.8d * value, value, 1.2d * value)
                    : new Deterministic(value));
            }

            return new TabularConsequence
            {
                Name = name,
                SpecifiedHazard = "Stage",
                HazardUnit = "ft",
                SpecifiedConsequence = "Damage",
                ConsequenceUnit = "dollars",
                UncertainOrderedPairedData = new UncertainOrderedPairedData(ordinates,
                    true, SortOrder.Ascending, false, SortOrder.None,
                    uncertain ? UnivariateDistributionType.Triangular : UnivariateDistributionType.Deterministic),
            };
        }

        /// <summary>Builds the trivial component (stage frequency, uncertain fragility, linear consequences).</summary>
        /// <param name="twoTypes">True to carry the [Life Loss, Damages] axis on both paths.</param>
        private static SystemComponent Component(bool twoTypes)
        {
            var hazard = new TabularHazard
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
            var fragility = new TabularResponse
            {
                Name = "Fragility",
                SpecifiedHazard = "Stage",
                HazardUnit = "ft",
                UncertainOrderedPairedData = new UncertainOrderedPairedData(
                    new[] { new UncertainOrdinate(10d, new Triangular(0d, 0.05d, 0.1d)), new UncertainOrdinate(20d, new Triangular(0.7d, 0.9d, 1d)) },
                    true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Triangular),
            };

            var component = new SystemComponent { Name = "Dam" };
            component.HazardFunction = hazard;
            var failure = new FailureMode(null, null, fragility, Consequence("Failure Loss", "Life Loss", "lives", 300d));
            if (twoTypes) failure.ConsequenceFunctions.Add(Consequence("Failure Damages", "Damages", "$", 900_000d));
            component.AddFailureMode(failure);
            var nonFailure = new FailureMode(null, null, null, Consequence("Non-Failure Loss", "Life Loss", "lives", 60d));
            if (twoTypes) nonFailure.ConsequenceFunctions.Add(Consequence("Non-Failure Damages", "Damages", "$", 250_000d));
            component.AddFailureMode(nonFailure);
            return component;
        }

        /// <summary>
        /// Builds a large event response whose 24 external links independently expand one
        /// 46-node deep/wide target subtree. The explicit scalar sources keep the fixture focused
        /// on occurrence compilation, branch mapping, and evaluation rather than distribution cost.
        /// </summary>
        private static EventTreeResponse BuildF5()
        {
            double[] hazards = new double[33];
            for (int i = 0; i < hazards.Length; i++) hazards[i] = i;

            var targetTree = new EventTree();
            var targetRoot = new ChanceNode("Reusable sequence", new ProbabilitySource(0.65d));
            targetTree.Add(targetTree.Root.Id, targetRoot);
            AddEventTreeLevel(targetTree, targetRoot, 4);
            var target = new EventTreeResponse(hazards, targetTree)
            {
                Name = "Reusable target",
                SpecifiedHazard = "Stage",
                HazardUnit = "ft",
            };

            var ownerTree = new EventTree();
            for (int i = 0; i < 24; i++)
                ownerTree.LinkIndependent(ownerTree.Root.Id, target, targetRoot.Id,
                    $"Independent occurrence {i + 1}");
            return new EventTreeResponse(hazards, ownerTree)
            {
                Name = "Large linked event tree",
                SpecifiedHazard = "Stage",
                HazardUnit = "ft",
            };
        }

        /// <summary>Adds one recursively branching target-tree level with an explicit remainder.</summary>
        /// <param name="tree">The target tree.</param>
        /// <param name="parent">The current parent.</param>
        /// <param name="remainingDepth">The number of binary levels still to add.</param>
        private static void AddEventTreeLevel(EventTree tree, EventNodeBase parent,
            int remainingDepth)
        {
            if (remainingDepth == 0) return;
            var first = new ChanceNode($"A{remainingDepth}", new ProbabilitySource(0.35d))
            {
                IsFailure = remainingDepth % 2 == 0,
            };
            var second = new ChanceNode($"B{remainingDepth}", new ProbabilitySource(0.45d))
            {
                IsFailure = remainingDepth % 2 != 0,
            };
            tree.Add(parent.Id, first);
            tree.Add(parent.Id, second);
            tree.Add(parent.Id, new RemainderNode($"R{remainingDepth}"));
            AddEventTreeLevel(tree, first, remainingDepth - 1);
            AddEventTreeLevel(tree, second, remainingDepth - 1);
        }

        /// <summary>Computes the deterministic byte gate for one indexed top-event curve.</summary>
        /// <param name="curve">The sampled hazard/probability curve.</param>
        /// <returns>The lowercase SHA-256 digest.</returns>
        private static string CurveHash(OrderedPairedData curve)
        {
            var builder = new StringBuilder();
            for (int i = 0; i < curve.Count; i++)
            {
                builder.Append(curve[i].X.ToString("G17", CultureInfo.InvariantCulture)).Append('|');
                builder.Append(curve[i].Y.ToString("G17", CultureInfo.InvariantCulture)).Append('|');
            }
            byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
            return Convert.ToHexString(digest).ToLowerInvariant();
        }

        /// <summary>
        /// Builds the F7 shared-event fault tree: eight crossing trains and two k-of-n voting
        /// gates draw on a pool of twenty basic events that repeat three to five times through
        /// shared-logical transfers, alongside forty train-local events, with sources alternating
        /// between fixed scalars and aligned uncertain tables. The crossing repetition pattern is
        /// what grows the frozen decision diagram, because no single subtree order can keep the
        /// repeated variables adjacent.
        /// </summary>
        private static FaultTreeResponse BuildF7()
        {
            double[] hazards = new double[33];
            for (int i = 0; i < hazards.Length; i++) hazards[i] = i;

            var tree = new FaultTree();
            var pool = new FaultTreeBasicEventNode?[20];
            int localOrdinal = 0;

            // Uses one pool event: the first use authors it in place, repeats share it.
            void UsePool(FaultTreeGateNode parent, int index)
            {
                if (pool[index] == null)
                {
                    double probability = 0.06d + 0.012d * index;
                    pool[index] = new FaultTreeBasicEventNode($"Pool {index}",
                        index % 2 == 0
                            ? new ProbabilitySource(probability)
                            : new ProbabilitySource(FaultTable(hazards, probability)));
                    tree.Add(parent.Id, pool[index]!);
                    return;
                }
                tree.LinkShared(parent.Id, pool[index]!.Id, $"Pool {index} again");
            }

            // Adds one train-local event with alternating scalar/table sources.
            void AddLocal(FaultTreeGateNode parent)
            {
                int ordinal = localOrdinal++;
                double probability = 0.1d + 0.005d * (ordinal % 20);
                tree.Add(parent.Id, new FaultTreeBasicEventNode($"Local {ordinal}",
                    ordinal % 2 == 0
                        ? new ProbabilitySource(probability)
                        : new ProbabilitySource(FaultTable(hazards, probability))));
            }

            // Sixteen concurrently live AND trains bound the diagram frontier near 2^16, and the
            // strided pool picks force every variable order to keep many trains open at once.
            for (int t = 0; t < 16; t++)
            {
                var train = new FaultTreeGateNode($"Train {t}", FaultTreeGateType.And);
                tree.Add(tree.Root.Id, train);
                AddLocal(train);
                UsePool(train, (3 * t) % 20);
                UsePool(train, (3 * t + 7) % 20);
                UsePool(train, (3 * t + 11) % 20);

                var group = new FaultTreeGateNode($"Train {t} group", FaultTreeGateType.Or);
                tree.Add(train.Id, group);
                AddLocal(group);
                UsePool(group, (3 * t + 14) % 20);
            }

            // A second bank on a co-prime stride keeps a different train subset live at every
            // point of any variable order, multiplying the reachable frontier again.
            for (int t = 16; t < 20; t++)
            {
                var train = new FaultTreeGateNode($"Train {t}", FaultTreeGateType.And);
                tree.Add(tree.Root.Id, train);
                AddLocal(train);
                UsePool(train, (9 * t) % 20);
                UsePool(train, (9 * t + 4) % 20);
                UsePool(train, (9 * t + 13) % 20);

                var group = new FaultTreeGateNode($"Train {t} group", FaultTreeGateType.Or);
                tree.Add(train.Id, group);
                AddLocal(group);
                UsePool(group, (9 * t + 17) % 20);
            }

            // The wide undecided thresholds resolve only near the end of any variable order, so
            // their partial counters multiply the live-train frontier across the whole pool span.
            // Voting A counts crossing pool pairs, so its counter also carries half-open pair
            // obligations; voting B counts the odd pool directly.
            var votingA = new FaultTreeGateNode("Voting A", FaultTreeGateType.KOfN, 3);
            tree.Add(tree.Root.Id, votingA);
            for (int pair = 0; pair < 6; pair++)
            {
                var joint = new FaultTreeGateNode($"Voting A pair {pair}", FaultTreeGateType.And);
                tree.Add(votingA.Id, joint);
                UsePool(joint, (2 * pair) % 20);
                UsePool(joint, (2 * pair + 10) % 20);
            }

            var votingB = new FaultTreeGateNode("Voting B", FaultTreeGateType.KOfN, 5);
            tree.Add(tree.Root.Id, votingB);
            foreach (int index in new[] { 1, 3, 5, 7, 9, 11, 13, 15, 17, 19 })
                UsePool(votingB, index);

            return new FaultTreeResponse(hazards, tree)
            {
                Name = "Shared-event fault tree",
                SpecifiedHazard = "Stage",
                HazardUnit = "ft",
            };
        }

        /// <summary>Builds one aligned constant-band uniform fault-tree table.</summary>
        /// <param name="hazards">The response hazard axis.</param>
        /// <param name="mean">The band mean probability.</param>
        /// <returns>The aligned uncertainty table.</returns>
        private static UncertainOrderedPairedData FaultTable(double[] hazards, double mean)
        {
            double half = Math.Min(0.03d, mean * 0.4d);
            var ordinates = new UncertainOrdinate[hazards.Length];
            for (int i = 0; i < hazards.Length; i++)
            {
                ordinates[i] = new UncertainOrdinate(hazards[i], new Uniform(mean - half, mean + half));
            }
            return new UncertainOrderedPairedData(ordinates, true, SortOrder.Ascending,
                false, SortOrder.None, UnivariateDistributionType.Uniform);
        }

        /// <summary>Builds a clamped linear tabular consequence over (0, 30) ft.</summary>
        private static TabularConsequence Consequence(string name, string type, string unit, double valueAtThirty)
        {
            return new TabularConsequence
            {
                Name = name,
                SpecifiedHazard = "Stage",
                HazardUnit = "ft",
                SpecifiedConsequence = type,
                ConsequenceUnit = unit,
                UncertainOrderedPairedData = new UncertainOrderedPairedData(
                    new[] { new UncertainOrdinate(0d, new Deterministic(0d)), new UncertainOrdinate(30d, new Deterministic(valueAtThirty)) },
                    true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
            };
        }
    }
}
