using System;
using System.Collections.Generic;
using System.Linq;
using Numerics.Data.Statistics;
using Numerics.Sampling;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees.Nodes;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees.Support;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Support;

namespace RMC.TotalRisk.RiskFunctions.Responses.Trees
{
    /// <summary>
    /// The headless tree node-importance analysis. Two deterministic Monte Carlo passes run
    /// read-only against the published immutable compiled plan: a joint pass varies every
    /// uncertain source together and records each node's probability alongside the aggregate
    /// failure probability, and a one-at-a-time pass holds every source at its mean while varying
    /// exactly one entry to produce the first-order variance-ratio index.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Each pass consumes one uniform draw per uncertain entry per iteration, in canonical entry
    /// order, from an independent Mersenne Twister stream seeded by the base seed, the response's
    /// canonical content hash, and the pass index. A shared-logical fault variable or shared
    /// event-tree sampling class therefore draws once per iteration no matter how many
    /// occurrences reference it, and the one-at-a-time pass varies it as one knowledge quantity.
    /// The analysis never clones the response and never mutates live sampler state, so it is
    /// thread-safe against concurrent read-only evaluation.
    /// </para>
    /// </remarks>
    public static class TreeNodeImportance
    {
        /// <summary>Computes node importance for an event-tree response.</summary>
        /// <param name="response">The valid event-tree response.</param>
        /// <param name="options">The analysis options.</param>
        /// <returns>One entry per expanded non-root occurrence, with the aggregate statistics.</returns>
        /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
        /// <exception cref="ArgumentException">Thrown when the hazard level is not an authored level.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the response is invalid.</exception>
        public static TreeNodeImportanceResult Compute(EventTreeResponse response,
            TreeNodeImportanceOptions options)
        {
            if (response == null) throw new ArgumentNullException(nameof(response));
            if (options == null) throw new ArgumentNullException(nameof(options));
            EnsureUsable(response.Validate(), response.Name);
            int hazardIndex = RequireHazardIndex(response.HazardLevels, options.HazardLevel);

            EventTreeOccurrencePlan plan = response.GetOccurrencePlan();
            IReadOnlyList<EventTreeEvaluationInstruction> instructions = plan.EvaluationInstructions;
            int count = instructions.Count;
            var uncertain = new bool[count];
            var classIndex = new int[count];
            var classFirstInstruction = new List<int>();
            var classInstructions = new Dictionary<EventTreeSamplingClass, int>(
                ReferenceEqualityComparer.Instance);
            for (int j = 1; j < count; j++)
            {
                classIndex[j] = -1;
                EventTreeOccurrenceNode occurrence = instructions[j].Occurrence;
                if (occurrence.SourceNode is not ChanceNode chance
                    || occurrence.SamplingClass == null) continue;
                uncertain[j] = !chance.ProbabilitySource.IsDeterministic;
                if (!classInstructions.TryGetValue(occurrence.SamplingClass, out int index))
                {
                    index = classFirstInstruction.Count;
                    classInstructions.Add(occurrence.SamplingClass, index);
                    classFirstInstruction.Add(j);
                }
                classIndex[j] = index;
            }

            int iterations = options.Iterations;
            var aggregate = new double[iterations];
            var conditionalSeries = new double[count][];
            var pathSeries = new double[count][];
            for (int j = 1; j < count; j++)
            {
                conditionalSeries[j] = new double[iterations];
                pathSeries[j] = new double[iterations];
            }

            var percentiles = new double[count];
            var classDraws = new double[classFirstInstruction.Count];
            var conditionals = new double[count];
            var paths = new double[count];
            var raw = new double[count];
            MersenneTwister jointStream = CreateStream(response.CanonicalHash(), options, 0);
            for (int i = 0; i < iterations; i++)
            {
                for (int k = 0; k < classDraws.Length; k++)
                {
                    classDraws[k] = uncertain[classFirstInstruction[k]]
                        ? jointStream.NextDouble()
                        : -1d;
                }
                Array.Fill(percentiles, -1d);
                for (int j = 1; j < count; j++)
                {
                    if (classIndex[j] >= 0) percentiles[j] = classDraws[classIndex[j]];
                }
                aggregate[i] = response.EvaluateImportanceSample(plan, options.HazardLevel,
                    hazardIndex, percentiles, conditionals, paths, raw);
                for (int j = 1; j < count; j++)
                {
                    conditionalSeries[j][i] = conditionals[j];
                    pathSeries[j][i] = paths[j];
                }
            }

            var firstOrder = new double[count][];
            for (int j = 1; j < count; j++)
            {
                if (uncertain[j]) firstOrder[j] = new double[iterations];
            }
            MersenneTwister oneAtATimeStream = CreateStream(response.CanonicalHash(), options, 1);
            for (int i = 0; i < iterations; i++)
            {
                for (int k = 0; k < classFirstInstruction.Count; k++)
                {
                    if (!uncertain[classFirstInstruction[k]]) continue;
                    Array.Fill(percentiles, -1d);
                    double draw = oneAtATimeStream.NextDouble();
                    for (int j = 1; j < count; j++)
                    {
                        if (classIndex[j] == k) percentiles[j] = draw;
                    }
                    double sample = response.EvaluateImportanceSample(plan, options.HazardLevel,
                        hazardIndex, percentiles, conditionals, paths, raw);
                    for (int j = 1; j < count; j++)
                    {
                        if (classIndex[j] == k) firstOrder[j][i] = sample;
                    }
                }
            }

            double aggregateVariance = SeriesVariance(aggregate);
            var entries = new List<TreeNodeImportanceEntry>(count - 1);
            for (int j = 1; j < count; j++)
            {
                EventTreeOccurrenceNode occurrence = instructions[j].Occurrence;
                entries.Add(new TreeNodeImportanceEntry(occurrence.DisplayNodeId,
                    occurrence.DisplayName, occurrence.CanonicalPath, uncertain[j],
                    Array.AsReadOnly(Statistics.FiveNumberSummary(pathSeries[j])),
                    SafeCorrelation(conditionalSeries[j], aggregate, aggregateVariance),
                    uncertain[j] ? SeriesVariance(firstOrder[j]) / aggregateVariance : 0d));
            }
            return new TreeNodeImportanceResult(options.HazardLevel, iterations, options.Seed,
                Array.AsReadOnly(Statistics.FiveNumberSummary(aggregate)), aggregateVariance,
                entries.AsReadOnly());
        }

        /// <summary>Computes node importance for a fault-tree response.</summary>
        /// <param name="response">The valid fault-tree response.</param>
        /// <param name="options">The analysis options.</param>
        /// <returns>One entry per unified basic-event variable, with the aggregate statistics.</returns>
        /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
        /// <exception cref="ArgumentException">Thrown when the hazard level is not an authored level.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the response is invalid.</exception>
        public static TreeNodeImportanceResult Compute(FaultTreeResponse response,
            TreeNodeImportanceOptions options)
        {
            if (response == null) throw new ArgumentNullException(nameof(response));
            if (options == null) throw new ArgumentNullException(nameof(options));
            EnsureUsable(response.Validate(), response.Name);
            RequireHazardIndex(response.HazardLevels, options.HazardLevel);

            FaultTreeOccurrencePlan plan = response.GetOccurrencePlan();
            int count = plan.Variables.Count;
            var uncertain = new bool[count];
            for (int j = 0; j < count; j++)
            {
                // The slot's own determinism: a derived common-cause event is deterministic (its
                // group's basis variable carries the one shared knowledge quantity), and every
                // ordinary slot's flag equals its source's.
                uncertain[j] = !plan.Variables[j].IsDeterministic;
            }

            int iterations = options.Iterations;
            var aggregate = new double[iterations];
            var probabilitySeries = new double[count][];
            for (int j = 0; j < count; j++) probabilitySeries[j] = new double[iterations];

            var percentiles = new double[count];
            var probabilities = new double[count];
            var scratch = new double[plan.FrozenBdd?.NodeCount ?? 0];
            MersenneTwister jointStream = CreateStream(response.CanonicalHash(), options, 0);
            for (int i = 0; i < iterations; i++)
            {
                Array.Fill(percentiles, -1d);
                for (int j = 0; j < count; j++)
                {
                    if (uncertain[j]) percentiles[j] = jointStream.NextDouble();
                }
                aggregate[i] = response.EvaluateImportanceSample(plan, options.HazardLevel,
                    percentiles, probabilities, scratch);
                for (int j = 0; j < count; j++) probabilitySeries[j][i] = probabilities[j];
            }

            var firstOrder = new double[count][];
            for (int j = 0; j < count; j++)
            {
                if (uncertain[j]) firstOrder[j] = new double[iterations];
            }
            MersenneTwister oneAtATimeStream = CreateStream(response.CanonicalHash(), options, 1);
            for (int i = 0; i < iterations; i++)
            {
                for (int j = 0; j < count; j++)
                {
                    if (!uncertain[j]) continue;
                    Array.Fill(percentiles, -1d);
                    percentiles[j] = oneAtATimeStream.NextDouble();
                    firstOrder[j][i] = response.EvaluateImportanceSample(plan, options.HazardLevel,
                        percentiles, probabilities, scratch);
                }
            }

            double aggregateVariance = SeriesVariance(aggregate);
            var entries = new List<TreeNodeImportanceEntry>(count);
            for (int j = 0; j < count; j++)
            {
                FaultTreeVariableSlot variable = plan.Variables[j];
                entries.Add(new TreeNodeImportanceEntry(variable.SourceNode.Id,
                    variable.DisplayName, variable.FirstOccurrence!.CanonicalPath, uncertain[j],
                    Array.AsReadOnly(Statistics.FiveNumberSummary(probabilitySeries[j])),
                    SafeCorrelation(probabilitySeries[j], aggregate, aggregateVariance),
                    uncertain[j] ? SeriesVariance(firstOrder[j]) / aggregateVariance : 0d));
            }
            return new TreeNodeImportanceResult(options.HazardLevel, iterations, options.Seed,
                Array.AsReadOnly(Statistics.FiveNumberSummary(aggregate)), aggregateVariance,
                entries.AsReadOnly());
        }

        /// <summary>Creates one pass's independent deterministic draw stream.</summary>
        /// <param name="contentHash">The response's canonical content hash.</param>
        /// <param name="options">The analysis options.</param>
        /// <param name="passIndex">The pass index: zero for the joint pass, one for one-at-a-time.</param>
        /// <returns>The seeded stream.</returns>
        private static MersenneTwister CreateStream(byte[] contentHash,
            TreeNodeImportanceOptions options, int passIndex)
        {
            return new MersenneTwister(SeedHelpers.ToPositiveSeed(
                SeedHelpers.HashCombine(options.Seed, contentHash, passIndex)));
        }

        /// <summary>Requires the analyzed hazard to exactly equal one authored level.</summary>
        /// <param name="hazards">The authored hazard levels.</param>
        /// <param name="hazardLevel">The requested hazard level.</param>
        /// <returns>The authored hazard index.</returns>
        /// <exception cref="ArgumentException">Thrown when no authored level matches.</exception>
        private static int RequireHazardIndex(IReadOnlyList<double> hazards, double hazardLevel)
        {
            for (int i = 0; i < hazards.Count; i++)
            {
                if (hazards[i] == hazardLevel) return i;
            }
            throw new ArgumentException(
                $"The importance hazard level {hazardLevel} must exactly equal one of the tree's authored hazard levels.",
                nameof(hazardLevel));
        }

        /// <summary>Requires a valid response before analysis.</summary>
        /// <param name="validation">The response validation result.</param>
        /// <param name="name">The response display name.</param>
        /// <exception cref="InvalidOperationException">Thrown when validation reports errors.</exception>
        private static void EnsureUsable((bool IsValid, List<string> ValidationMessages) validation,
            string name)
        {
            if (validation.IsValid) return;
            string errors = string.Join(" ", validation.ValidationMessages.Where(message =>
                message.StartsWith("Error:", StringComparison.Ordinal)));
            throw new InvalidOperationException(
                $"The tree response '{name}' is invalid. Call Validate() and correct the reported " +
                $"errors before computing node importance. {errors}");
        }

        /// <summary>Computes the Pearson correlation, or NaN when either series is constant.</summary>
        /// <param name="series">The per-node probability series.</param>
        /// <param name="aggregate">The sampled aggregate series.</param>
        /// <param name="aggregateVariance">The precomputed aggregate variance.</param>
        /// <returns>The correlation coefficient.</returns>
        private static double SafeCorrelation(double[] series, double[] aggregate,
            double aggregateVariance)
        {
            if (aggregateVariance == 0d || IsConstant(series)) return double.NaN;
            return Correlation.Pearson(series, aggregate);
        }

        /// <summary>
        /// Computes a series variance, reporting exactly zero for a bit-constant series. The
        /// numeric variance of a constant series carries a tiny sum-of-squares roundoff residue,
        /// which would silently defeat the documented zero-variance statistics.
        /// </summary>
        /// <param name="series">The recorded series.</param>
        /// <returns>The variance.</returns>
        private static double SeriesVariance(double[] series)
        {
            return IsConstant(series) ? 0d : Statistics.Variance(series);
        }

        /// <summary>Determines whether every recorded value is bit-identical to the first.</summary>
        /// <param name="series">The recorded series.</param>
        /// <returns>True for a constant series.</returns>
        private static bool IsConstant(double[] series)
        {
            for (int i = 1; i < series.Length; i++)
            {
                if (series[i] != series[0]) return false;
            }
            return true;
        }
    }
}
