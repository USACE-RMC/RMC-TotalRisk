using System;
using System.Threading;
using System.Threading.Tasks;
using Numerics.Data.Statistics;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Analyses.Risk.Support
{
    /// <summary>
    /// Deterministically assembles uncertainty percentile curves, scalar measures, and risk
    /// profiles from completed system realizations.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     This service owns post-processing only. It does not sample, integrate, or mutate an
    ///     analysis definition, so extracting it cannot change sampler or integration order.
    /// </para>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// </remarks>
    internal static class RiskPercentileAssembler
    {
        private const double ProbabilityFloor = 1e-16;
        /// <summary>
        /// Builds a descending linear grid over [minimum, maximum] with the given ordinate count.
        /// </summary>
        /// <param name="minimum">The grid minimum.</param>
        /// <param name="maximum">The grid maximum.</param>
        /// <param name="count">The ordinate count (at least two).</param>
        /// <returns>The descending grid.</returns>
        /// <remarks>
        /// Not <c>Tools.Sequence</c>: that form accumulates its step and derives its length from
        /// it, where the percentile assembly needs exactly <paramref name="count"/> ordinates.
        /// </remarks>
        internal static double[] BuildDescendingGrid(double minimum, double maximum, int count)
        {
            var grid = new double[count];
            double step = (maximum - minimum) / (count - 1);
            for (int i = 0; i < count; i++)
            {
                grid[i] = maximum - i * step;
            }
            return grid;
        }

        /// <summary>
        /// Assembles the five risk-type LEC percentile curves onto the target curve sets: at
        /// each grid consequence, the realizations' exceedance probabilities are interpolated
        /// (log-log — v1.0 behavior), sorted for the percentile levels, and summed sequentially
        /// for the mean. Optional realization weights make every reduction weighted; null
        /// weights reduce exactly as before.
        /// </summary>
        /// <param name="realizations">The realization ensemble.</param>
        /// <param name="source">Selects the source curve set from a realization.</param>
        /// <param name="target">Selects the target curve set by percentile slot (0 lower, 1 upper, 2 median, 3 mean).</param>
        /// <param name="consequenceGrid">The shared descending consequence grid.</param>
        /// <param name="tail">The percentile tail level, (1 − width)/2.</param>
        /// <param name="weights">The optional realization weights, parallel to the ensemble; null for the unweighted path.</param>
        /// <param name="token">The run cancellation token.</param>
        internal static void AssembleLecPercentiles(SystemRealization[] realizations,
            Func<SystemRealization, Curves> source, Func<int, Curves> target,
            double[] consequenceGrid, double tail, double[]? weights, CancellationToken token)
        {
            AssemblePercentileCurve(realizations, r => { var c = source(r).Excess; return (c.LECConsequences, c.LECProbabilities); }, consequenceGrid, tail, weights, token,
                (slot, x, y) => { target(slot).Excess.LECConsequences = x; target(slot).Excess.LECProbabilities = y; });
            AssembleCurveScalars(realizations, r => source(r).Excess, slot => target(slot).Excess, tail, weights);
            AssemblePercentileCurve(realizations, r => { var c = source(r).Background; return (c.LECConsequences, c.LECProbabilities); }, consequenceGrid, tail, weights, token,
                (slot, x, y) => { target(slot).Background.LECConsequences = x; target(slot).Background.LECProbabilities = y; });
            AssembleCurveScalars(realizations, r => source(r).Background, slot => target(slot).Background, tail, weights);
            AssemblePercentileCurve(realizations, r => { var c = source(r).Total; return (c.LECConsequences, c.LECProbabilities); }, consequenceGrid, tail, weights, token,
                (slot, x, y) => { target(slot).Total.LECConsequences = x; target(slot).Total.LECProbabilities = y; });
            AssembleCurveScalars(realizations, r => source(r).Total, slot => target(slot).Total, tail, weights);
            AssemblePercentileCurve(realizations, r => { var c = source(r).Fail; return (c.LECConsequences, c.LECProbabilities); }, consequenceGrid, tail, weights, token,
                (slot, x, y) => { target(slot).Fail.LECConsequences = x; target(slot).Fail.LECProbabilities = y; });
            AssembleCurveScalars(realizations, r => source(r).Fail, slot => target(slot).Fail, tail, weights);
            AssemblePercentileCurve(realizations, r => { var c = source(r).NonFail; return (c.LECConsequences, c.LECProbabilities); }, consequenceGrid, tail, weights, token,
                (slot, x, y) => { target(slot).NonFail.LECConsequences = x; target(slot).NonFail.LECProbabilities = y; });
            AssembleCurveScalars(realizations, r => source(r).NonFail, slot => target(slot).NonFail, tail, weights);
        }

        /// <summary>
        /// Carries scalar curve measures into the lower, upper, median, and arithmetic-mean
        /// percentile realizations. This is the scalar counterpart to the pointwise LEC assembly;
        /// in particular, it preserves the raw exhaustive mass instead of fabricating it from the
        /// curve kind.
        /// </summary>
        /// <param name="realizations">The realization ensemble.</param>
        /// <param name="source">Selects the source curve.</param>
        /// <param name="target">Selects a target by percentile slot.</param>
        /// <param name="tail">The lower percentile tail.</param>
        /// <param name="weights">The optional realization weights; null for the unweighted path.</param>
        private static void AssembleCurveScalars(SystemRealization[] realizations,
            Func<SystemRealization, Curve> source, Func<int, Curve> target, double tail, double[]? weights)
        {
            var sources = new Curve[realizations.Length];
            for (int i = 0; i < realizations.Length; i++) sources[i] = source(realizations[i]);
            var targets = new[] { target(0), target(1), target(2), target(3) };
            for (int i = 0; i < targets.Length; i++)
            {
                targets[i].IsExhaustive = sources[0].IsExhaustive;
                targets[i].MeasureOptions = sources[0].MeasureOptions;
                targets[i].Alpha = sources[0].Alpha;
                targets[i].ConsequenceThreshold = sources[0].ConsequenceThreshold;
                targets[i].HazardThreshold = sources[0].HazardThreshold;
            }

            AssignScalarPercentiles(sources, targets, c => c.TotalProbability, (c, v) => c.TotalProbability = v, tail, weights);
            AssignScalarPercentiles(sources, targets, c => c.MassBalance, (c, v) => c.MassBalance = v, tail, weights);
            AssignScalarPercentiles(sources, targets, c => c.Mean, (c, v) => c.Mean = v, tail, weights);
            AssignScalarPercentiles(sources, targets, c => c.StandardDeviation, (c, v) => c.StandardDeviation = v, tail, weights);
            AssignScalarPercentiles(sources, targets, c => c.Skewness, (c, v) => c.Skewness = v, tail, weights);
            AssignScalarPercentiles(sources, targets, c => c.Kurtosis, (c, v) => c.Kurtosis = v, tail, weights);
            AssignScalarPercentiles(sources, targets, c => c.ConsequenceThresholdProbability,
                (c, v) => c.ConsequenceThresholdProbability = v, tail, weights);
            AssignScalarPercentiles(sources, targets, c => c.HazardThresholdProbability,
                (c, v) => c.HazardThresholdProbability = v, tail, weights);
            AssignScalarPercentiles(sources, targets, c => c.ValueAtRisk, (c, v) => c.ValueAtRisk = v, tail, weights);
            AssignScalarPercentiles(sources, targets, c => c.ConditionalValueAtRisk,
                (c, v) => c.ConditionalValueAtRisk = v, tail, weights);
        }

        /// <summary>
        /// Assigns finite scalar percentiles and a compensated arithmetic mean. Under weights
        /// the finite filter carries the weights pairwise, the percentile levels use the
        /// symmetric weighted percentile, and the mean is the upstream weighted mean; a
        /// surviving set with zero total weight assigns NaN like the all-non-finite case.
        /// At unit weights the weighted forms agree with the unweighted path to floating-point
        /// rounding, not bitwise: the weighted mean is the plain Σw·x/Σw rather than the
        /// compensated sequential sum, and the weighted percentile interpolates on plotting
        /// positions i/(n − 1) that are exact only when representable.
        /// </summary>
        /// <param name="sources">The source curves.</param>
        /// <param name="targets">The four percentile targets.</param>
        /// <param name="read">Reads the scalar.</param>
        /// <param name="write">Writes the scalar.</param>
        /// <param name="tail">The lower percentile tail.</param>
        /// <param name="weights">The optional realization weights; null for the unweighted path.</param>
        private static void AssignScalarPercentiles(Curve[] sources, Curve[] targets,
            Func<Curve, double> read, Action<Curve, double> write, double tail, double[]? weights)
        {
            if (weights == null)
            {
                var values = new double[sources.Length];
                double sum = 0d;
                double compensation = 0d;
                int used = 0;
                for (int i = 0; i < sources.Length; i++)
                {
                    double value = read(sources[i]);
                    if (!double.IsFinite(value)) continue;
                    values[used++] = value;
                    double adjusted = value - compensation;
                    double next = sum + adjusted;
                    compensation = (next - sum) - adjusted;
                    sum = next;
                }
                if (used == 0)
                {
                    for (int i = 0; i < targets.Length; i++) write(targets[i], double.NaN);
                    return;
                }
                Array.Sort(values, 0, used);
                var trimmed = new double[used];
                Array.Copy(values, trimmed, used);
                write(targets[0], Statistics.Percentile(trimmed, tail, true));
                write(targets[1], Statistics.Percentile(trimmed, 1d - tail, true));
                write(targets[2], Statistics.Percentile(trimmed, 0.5d, true));
                write(targets[3], sum / used);
                return;
            }

            var filtered = new double[sources.Length];
            var filteredWeights = new double[sources.Length];
            double totalWeight = 0d;
            int count = 0;
            for (int i = 0; i < sources.Length; i++)
            {
                double value = read(sources[i]);
                if (!double.IsFinite(value)) continue;
                filtered[count] = value;
                filteredWeights[count] = weights[i];
                totalWeight += weights[i];
                count++;
            }
            if (count == 0 || totalWeight <= 0d)
            {
                for (int i = 0; i < targets.Length; i++) write(targets[i], double.NaN);
                return;
            }
            var trimmedValues = new double[count];
            var trimmedWeights = new double[count];
            Array.Copy(filtered, trimmedValues, count);
            Array.Copy(filteredWeights, trimmedWeights, count);
            var percentiles = Statistics.Percentile(trimmedValues, new[] { tail, 1d - tail, 0.5d }, trimmedWeights, dataIsSorted: false);
            write(targets[0], percentiles[0]);
            write(targets[1], percentiles[1]);
            write(targets[2], percentiles[2]);
            write(targets[3], Statistics.Mean(trimmedValues, trimmedWeights));
        }

        /// <summary>
        /// Assembles one percentile curve family: each realization merge-walks the shared
        /// descending grid once with the monotone-cursor log-log interpolator
        /// (<see cref="Curve.InterpolateLogLogDescending"/> — bit-identical to the per-query
        /// Numerics interpolation it replaces, O(n + m) per realization instead of a binary
        /// search per ordinate, in parallel over realizations); then per grid ordinate
        /// (parallel, index-owned) the values are compacted in realization order, summed
        /// sequentially for the mean, and sorted for the lower/upper/median levels.
        /// Realizations whose source curve is empty are skipped, and an all-empty family leaves
        /// the targets empty.
        /// </summary>
        /// <param name="realizations">The realization ensemble.</param>
        /// <param name="curve">Selects the source curve's serialized arrays from a realization (X descending).</param>
        /// <param name="grid">The descending X grid.</param>
        /// <param name="tail">The percentile tail level.</param>
        /// <param name="weights">The optional realization weights; null for the unweighted path. A weighted grid ordinate whose contributing realizations carry zero total weight is left unassigned, mirroring the all-skipped ordinate.</param>
        /// <param name="token">The run cancellation token.</param>
        /// <param name="assign">Assigns the assembled arrays per percentile slot (0 lower, 1 upper, 2 median, 3 mean).</param>
        private static void AssemblePercentileCurve(SystemRealization[] realizations,
            Func<SystemRealization, (double[] Xs, double[] Ys)> curve, double[] grid, double tail, double[]? weights,
            CancellationToken token, Action<int, double[], double[]> assign)
        {
            int realizationCount = realizations.Length;
            bool anySource = false;
            for (int i = 0; i < realizationCount; i++)
            {
                if (curve(realizations[i]).Xs.Length > 0)
                {
                    anySource = true;
                    break;
                }
            }
            if (!anySource) return;

            // Transposed fill: values[ordinate][realization], NaN marking skipped realizations.
            var values = new double[grid.Length][];
            for (int g = 0; g < grid.Length; g++)
            {
                values[g] = new double[realizationCount];
            }
            Parallel.For(0, realizationCount, new ParallelOptions { CancellationToken = token }, r =>
            {
                var (xs, ys) = curve(realizations[r]);
                if (xs.Length == 0)
                {
                    for (int g = 0; g < grid.Length; g++)
                    {
                        values[g][r] = double.NaN;
                    }
                    return;
                }
                if (xs.Length == 1)
                {
                    for (int g = 0; g < grid.Length; g++) values[g][r] = ys[0];
                }
                else
                {
                    int cursor = 1;
                    for (int g = 0; g < grid.Length; g++)
                    {
                        values[g][r] = Curve.InterpolateLogLogDescending(xs, ys, grid[g], ref cursor);
                    }
                }
            });

            var lowerValues = new double[grid.Length];
            var upperValues = new double[grid.Length];
            var medianValues = new double[grid.Length];
            var meanValues = new double[grid.Length];
            var levels = weights == null ? null : new[] { tail, 1d - tail, 0.5d };

            Parallel.For(0, grid.Length, new ParallelOptions { CancellationToken = token }, g =>
            {
                var row = values[g];
                if (weights == null)
                {
                    var window = new double[realizationCount];
                    double sum = 0d;
                    int used = 0;
                    for (int r = 0; r < realizationCount; r++)
                    {
                        double value = row[r];
                        if (double.IsNaN(value)) continue;
                        window[used] = value;
                        sum += value;
                        used++;
                    }
                    if (used == 0) return;
                    Array.Sort(window, 0, used);
                    var trimmed = new double[used];
                    Array.Copy(window, trimmed, used);
                    lowerValues[g] = Statistics.Percentile(trimmed, tail, true);
                    upperValues[g] = Statistics.Percentile(trimmed, 1d - tail, true);
                    medianValues[g] = Statistics.Percentile(trimmed, 0.5d, true);
                    meanValues[g] = sum / used;
                    return;
                }

                var filtered = new double[realizationCount];
                var filteredWeights = new double[realizationCount];
                double totalWeight = 0d;
                int count = 0;
                for (int r = 0; r < realizationCount; r++)
                {
                    double value = row[r];
                    if (double.IsNaN(value)) continue;
                    filtered[count] = value;
                    filteredWeights[count] = weights[r];
                    totalWeight += weights[r];
                    count++;
                }
                if (count == 0 || totalWeight <= 0d) return;
                var trimmedValues = new double[count];
                var trimmedWeights = new double[count];
                Array.Copy(filtered, trimmedValues, count);
                Array.Copy(filteredWeights, trimmedWeights, count);
                var percentiles = Statistics.Percentile(trimmedValues, levels!, trimmedWeights, dataIsSorted: false);
                lowerValues[g] = percentiles[0];
                upperValues[g] = percentiles[1];
                medianValues[g] = percentiles[2];
                meanValues[g] = Statistics.Mean(trimmedValues, trimmedWeights);
            });

            assign(0, (double[])grid.Clone(), lowerValues);
            assign(1, (double[])grid.Clone(), upperValues);
            assign(2, (double[])grid.Clone(), medianValues);
            assign(3, (double[])grid.Clone(), meanValues);
        }

        /// <summary>
        /// Assembles the risk-profile percentiles for one component and one consequence type
        /// onto the four percentile realizations: the hazard-frequency, conditional-consequence,
        /// and cumulative-expected-consequence profiles on all five risk-type streams (v1.0
        /// banded all five — an interim Total-only build was a parity gap, since closed), plus
        /// — for the primary consequence type — the Fail stream's cumulative failure probability
        /// on the hazard grid and the system response profile on its own log-spaced
        /// exceedance-probability grid.
        /// </summary>
        /// <param name="realizations">The realization ensemble.</param>
        /// <param name="componentIndex">The component index.</param>
        /// <param name="scope">Selects the consequence type's curve set from a component realization.</param>
        /// <param name="hazardGrid">The component's descending hazard grid.</param>
        /// <param name="tail">The percentile tail level.</param>
        /// <param name="weights">The optional realization weights; null for the unweighted path.</param>
        /// <param name="targets">The percentile realizations (0 lower, 1 upper, 2 median, 3 mean).</param>
        /// <param name="primaryType">True when the scope selects the primary consequence type (enables the failure-stream profiles).</param>
        /// <param name="outputLength">The banded profile resolution (the LEC output length).</param>
        /// <param name="token">The run cancellation token.</param>
        internal static void AssembleProfilePercentiles(SystemRealization[] realizations, int componentIndex,
            Func<ComponentRealization, Curves> scope, double[] hazardGrid, double tail, double[]? weights,
            SystemRealization[] targets, bool primaryType, int outputLength, CancellationToken token)
        {
            Span<RiskType> streams = stackalloc RiskType[]
            {
                RiskType.Excess, RiskType.Background, RiskType.Total, RiskType.Fail, RiskType.NonFail,
            };
            foreach (RiskType stream in streams)
            {
                RiskType riskType = stream;
                AssemblePercentileCurve(realizations,
                    r => { var c = scope(r.Components[componentIndex]).GetCurve(riskType); return (c.HazardFrequencyHazards, c.HazardFrequencyProbabilities); }, hazardGrid, tail, weights, token,
                    (slot, x, y) =>
                    {
                        var target = scope(targets[slot].Components[componentIndex]).GetCurve(riskType);
                        target.HazardFrequencyHazards = x;
                        target.HazardFrequencyProbabilities = y;
                    });
                AssemblePercentileCurve(realizations,
                    r => { var c = scope(r.Components[componentIndex]).GetCurve(riskType); return (c.HazardVsCenHazards, c.HazardVsCenConsequences); }, hazardGrid, tail, weights, token,
                    (slot, x, y) =>
                    {
                        var target = scope(targets[slot].Components[componentIndex]).GetCurve(riskType);
                        target.HazardVsCenHazards = x;
                        target.HazardVsCenConsequences = y;
                    });
                AssemblePercentileCurve(realizations,
                    r => { var c = scope(r.Components[componentIndex]).GetCurve(riskType); return (c.HazardFrequencyHazards, c.CumulativeExpectedConsequences); }, hazardGrid, tail, weights, token,
                    (slot, x, y) =>
                    {
                        // Y-only: the banded X axis is the hazard grid the frequency assembly
                        // already stamped on the target's HazardFrequencyHazards.
                        scope(targets[slot].Components[componentIndex]).GetCurve(riskType).CumulativeExpectedConsequences = y;
                    });
            }

            if (!primaryType) return;

            // The failure-stream profiles (primary type only): the cumulative failure
            // probability rides the hazard grid; the system response profile bands on its own
            // log-spaced exceedance grid spanning the engine's recorded probability domain.
            AssemblePercentileCurve(realizations,
                r => { var c = scope(r.Components[componentIndex]).Fail; return (c.HazardFrequencyHazards, c.CumulativeFailureProbabilities); }, hazardGrid, tail, weights, token,
                (slot, x, y) =>
                {
                    scope(targets[slot].Components[componentIndex]).Fail.CumulativeFailureProbabilities = y;
                });
            var exceedanceGrid = BuildLogDescendingGrid(ProbabilityFloor, 1d - ProbabilityFloor, outputLength);
            AssemblePercentileCurve(realizations,
                r => { var c = scope(r.Components[componentIndex]).Fail; return (c.SystemResponseExceedanceProbabilities, c.SystemResponseProbabilities); }, exceedanceGrid, tail, weights, token,
                (slot, x, y) =>
                {
                    var target = scope(targets[slot].Components[componentIndex]).Fail;
                    target.SystemResponseExceedanceProbabilities = x;
                    target.SystemResponseProbabilities = y;
                });
        }

        /// <summary>
        /// Builds a descending log-spaced grid over [minimum, maximum] with the given ordinate
        /// count — the system response profile's exceedance-probability ladder.
        /// </summary>
        /// <param name="minimum">The positive grid minimum.</param>
        /// <param name="maximum">The grid maximum (greater than the minimum).</param>
        /// <param name="count">The ordinate count (at least two).</param>
        /// <returns>The descending grid.</returns>
        private static double[] BuildLogDescendingGrid(double minimum, double maximum, int count)
        {
            var grid = new double[count];
            double logMaximum = Math.Log(maximum);
            double logStep = Math.Log(maximum / minimum) / (count - 1);
            for (int i = 0; i < count; i++)
            {
                grid[i] = Math.Exp(logMaximum - i * logStep);
            }
            return grid;
        }
    }
}
