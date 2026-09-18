using System;
using System.Collections.Generic;
using Numerics.Data.Statistics;

namespace RMC.TotalRisk.Results.Risk
{
    /// <summary>
    /// Percentile confidence intervals on every scalar risk measure across a full-uncertainty
    /// ensemble: four <see cref="SystemRiskResults"/> trees — Lower, Upper, Median,
    /// and Mean — whose every scalar (the ten-measure catalog at system, per-component,
    /// per-failure-mode, and per-consequence-type scope, plus the contribution values and the
    /// integrator diagnostics) is the ensemble percentile or mean of that measure, together
    /// with the aggregated <see cref="ConvergenceDiagnostics"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The curve percentile bands predate this container; the scalar catalog carried no
    /// intervals until it. Each measure is reduced <b>independently</b> —
    /// percentile-consistent per measure, not one coherent realization: the 95th percentile of
    /// the value-at-risk is not the value-at-risk of the 95th-percentile curve, which is
    /// precisely why the scalars need their own reduction. NaN values (a measure a realization
    /// could not evaluate — e.g., an undeclared threshold) are filtered pairwise per measure;
    /// an all-NaN measure reduces to NaN. The mean slot sums sequentially in realization order
    /// (bit-identical at any thread count); the percentile slots sort a filtered copy and read
    /// <c>Statistics.Percentile</c> at (1 − width)/2, 0.5, and 1 − (1 − width)/2 — the curve
    /// bands' convention.
    /// </para>
    /// <para>
    /// Serialized append-only as <c>EnsembleResults.Summary</c>; a missing block on older
    /// payloads means "not computed", and <c>EnsembleResults.ComputeSummary</c> rebuilds it
    /// from any loaded ensemble.
    /// </para>
    /// <para>
    /// When the ensemble carries <c>EnsembleResults.RealizationWeights</c>, every reduction is
    /// weighted: the mean slot is the weighted mean, the percentile slots use the symmetric
    /// weighted percentile (zero-weight realizations carry no mass), and the convergence
    /// indicators report the weighted mean with the reliability-weighted standard error
    /// √(V/N_eff), N_eff = (Σw)²/Σw². Weights are reliability (importance) weights — only
    /// relative values carry meaning. Unit weights reproduce the unweighted mean slots exactly
    /// and the percentile slots bit-exactly whenever the equal-weight plotting positions
    /// i/(n − 1) are exactly representable, within one unit of precision otherwise; the
    /// indicator standard error agrees to floating-point rounding (√(V/N_eff) and √V/√N
    /// compose differently in the last unit of precision).
    /// The aggregated integrator-effort diagnostics stay unweighted — they
    /// describe computational effort actually spent, not epistemic expectation. A measure whose
    /// surviving (non-NaN) realizations carry zero total weight reduces to NaN, the all-NaN
    /// convention.
    /// </para>
    /// </remarks>
    public sealed class EnsembleSummary
    {
        #region Construction

        /// <summary>
        /// Initializes an empty summary (deserialization support).
        /// </summary>
        public EnsembleSummary()
        {
            Lower = new SystemRiskResults();
            Upper = new SystemRiskResults();
            Median = new SystemRiskResults();
            Mean = new SystemRiskResults();
            Convergence = new ConvergenceDiagnostics();
        }

        #endregion

        #region Members

        /// <summary>
        /// The confidence-interval width the percentile slots were computed at.
        /// </summary>
        public double ConfidenceIntervalWidth { get; set; }

        /// <summary>
        /// The number of ensemble realizations behind the reduction (null entries excluded).
        /// </summary>
        public int RealizationCount { get; set; }

        /// <summary>
        /// The lower percentile tree, at (1 − width)/2 per measure.
        /// </summary>
        public SystemRiskResults Lower { get; set; }

        /// <summary>
        /// The upper percentile tree, at 1 − (1 − width)/2 per measure.
        /// </summary>
        public SystemRiskResults Upper { get; set; }

        /// <summary>
        /// The median tree per measure.
        /// </summary>
        public SystemRiskResults Median { get; set; }

        /// <summary>
        /// The ensemble-mean tree per measure (sequential sums in realization order).
        /// </summary>
        public SystemRiskResults Mean { get; set; }

        /// <summary>
        /// The aggregated convergence diagnostics.
        /// </summary>
        public ConvergenceDiagnostics Convergence { get; set; }

        /// <summary>
        /// The Kish effective sample size (Σw)²/Σw² of the realization weights behind a weighted
        /// reduction, over the realizations actually reduced. Null when the reduction was
        /// unweighted — the field is absent from unweighted payloads (append-only results JSON),
        /// which is also how a weighted summary identifies itself.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public double? EffectiveRealizationCount { get; set; }

        /// <summary>
        /// The evaluated tolerable-risk confidence statements, one per configured criterion in
        /// declared order — P(measure &gt; threshold) as the realization-weight fraction
        /// strictly exceeding each threshold. Null when the analysis configures no criteria —
        /// the field is absent from such payloads (append-only results JSON). Populated by the
        /// engine's full-uncertainty run; criteria live on the analysis options, so a bare
        /// <see cref="Compute"/> over a loaded ensemble produces a summary without the block —
        /// the analysis-level recomputation supplies it.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public List<TolerableRiskConfidence>? TolerableRiskConfidence { get; set; }

        #endregion

        #region Computation

        /// <summary>
        /// The ten-measure accessor catalog over a <see cref="SummaryRiskResults"/> — the
        /// explicit selector/setter list the reducer walks (no reflection).
        /// </summary>
        private static readonly (Func<SummaryRiskResults, double> Get, Action<SummaryRiskResults, double> Set)[] MeasureAccessors =
        {
            (s => s.TotalProbability, (s, v) => s.TotalProbability = v),
            (s => s.ConditionalMean, (s, v) => s.ConditionalMean = v),
            (s => s.Mean, (s, v) => s.Mean = v),
            (s => s.StandardDeviation, (s, v) => s.StandardDeviation = v),
            (s => s.Skewness, (s, v) => s.Skewness = v),
            (s => s.Kurtosis, (s, v) => s.Kurtosis = v),
            (s => s.ConsequenceThresholdProbability, (s, v) => s.ConsequenceThresholdProbability = v),
            (s => s.HazardThresholdProbability, (s, v) => s.HazardThresholdProbability = v),
            (s => s.ValueAtRisk, (s, v) => s.ValueAtRisk = v),
            (s => s.ConditionalValueAtRisk, (s, v) => s.ConditionalValueAtRisk = v),
        };

        /// <summary>
        /// The three-value contribution accessor catalog over a <see cref="RiskContribution"/>.
        /// </summary>
        private static readonly (Func<RiskContribution, double> Get, Action<RiskContribution, double> Set)[] ContributionAccessors =
        {
            (c => c.FailureProbability, (c, v) => c.FailureProbability = v),
            (c => c.FailureMean, (c, v) => c.FailureMean = v),
            (c => c.ExcessMean, (c, v) => c.ExcessMean = v),
        };

        /// <summary>
        /// The prepared weighted-reduction state threaded through the reducers when the ensemble
        /// carries realization weights: null selects the unweighted path unchanged.
        /// </summary>
        private sealed class WeightedReduction
        {
            /// <summary>
            /// Initializes the weighted-reduction state.
            /// </summary>
            /// <param name="weights">The weights aligned to the reduced realization list.</param>
            /// <param name="tail">The percentile tail level, (1 − width)/2.</param>
            internal WeightedReduction(double[] weights, double tail)
            {
                Weights = weights;
                WeightBuffer = new double[weights.Length];
                Levels = new[] { tail, 1d - tail, 0.5d };
            }

            /// <summary>The weights aligned to the reduced realization list.</summary>
            internal double[] Weights { get; }

            /// <summary>The shared scratch buffer for the NaN-filtered weights.</summary>
            internal double[] WeightBuffer { get; }

            /// <summary>The percentile levels in slot order: lower, upper, median.</summary>
            internal double[] Levels { get; }
        }

        /// <summary>
        /// Computes the ensemble summary from a stored ensemble: percentile confidence
        /// intervals on every scalar measure plus the convergence diagnostics. When the ensemble
        /// carries realization weights, every measure reduction is weighted (see the class
        /// remarks); a null weight vector reduces exactly as before.
        /// </summary>
        /// <param name="results">The ensemble to reduce.</param>
        /// <param name="confidenceIntervalWidth">The confidence-interval width, in (0, 1).</param>
        /// <returns>The summary, or null when the ensemble holds no realizations.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the ensemble is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the width is outside (0, 1).</exception>
        /// <exception cref="ArgumentException">Thrown when the ensemble carries an invalid realization weight vector.</exception>
        public static EnsembleSummary? Compute(EnsembleResults results, double confidenceIntervalWidth)
        {
            if (results == null) throw new ArgumentNullException(nameof(results));
            if (!(confidenceIntervalWidth > 0d) || !(confidenceIntervalWidth < 1d))
            {
                throw new ArgumentOutOfRangeException(nameof(confidenceIntervalWidth), "The confidence-interval width must be in (0, 1).");
            }

            var storedWeights = results.RealizationWeights;
            List<double>? alignedWeights = null;
            if (storedWeights != null)
            {
                string? reason = EnsembleResults.DescribeInvalidWeights(storedWeights, results.Count, out _);
                if (reason != null)
                {
                    throw new ArgumentException($"The ensemble realization weights are invalid. {reason}", nameof(results));
                }
                alignedWeights = new List<double>(results.Count);
            }

            var realizations = new List<SystemRiskResults>(results.Count);
            for (int i = 0; i < results.Count; i++)
            {
                if (results[i] != null)
                {
                    realizations.Add(results[i]!);
                    alignedWeights?.Add(storedWeights![i]);
                }
            }
            if (realizations.Count == 0) return null;

            double tail = (1d - confidenceIntervalWidth) / 2d;
            WeightedReduction? weighted = null;
            double? effectiveCount = null;
            if (alignedWeights != null)
            {
                double total = 0d, sumOfSquares = 0d;
                for (int i = 0; i < alignedWeights.Count; i++)
                {
                    total += alignedWeights[i];
                    sumOfSquares += alignedWeights[i] * alignedWeights[i];
                }
                effectiveCount = sumOfSquares > 0d ? total * total / sumOfSquares : 0d;
                weighted = new WeightedReduction(alignedWeights.ToArray(), tail);
            }

            var template = realizations[0];
            var summary = new EnsembleSummary
            {
                ConfidenceIntervalWidth = confidenceIntervalWidth,
                RealizationCount = realizations.Count,
                EffectiveRealizationCount = effectiveCount,
                Lower = CreateShapedTree(template),
                Upper = CreateShapedTree(template),
                Median = CreateShapedTree(template),
                Mean = CreateShapedTree(template),
            };

            var buffer = new double[realizations.Count];
            var targets = new[] { summary.Lower, summary.Upper, summary.Median, summary.Mean };

            // System streams and additional types.
            ReduceSummarySet(realizations, targets, r => r, buffer, tail, weighted);

            // Components, their additional types, and their failure modes.
            for (int c = 0; c < template.ComponentResults.Count; c++)
            {
                int componentIndex = c;
                ReduceComponent(realizations, targets, componentIndex, buffer, tail, weighted);
            }

            // The root integrator diagnostics reduce like measures.
            Reduce(realizations, r => r.FunctionEvaluations, buffer, tail,
                (slot, value) => SetRootDiagnostic(targets[slot], 0, value), weighted);
            Reduce(realizations, r => r.StandardError, buffer, tail,
                (slot, value) => SetRootDiagnostic(targets[slot], 1, value), weighted);
            Reduce(realizations, r => r.ChiSquared, buffer, tail,
                (slot, value) => SetRootDiagnostic(targets[slot], 2, value), weighted);

            summary.Convergence = ComputeConvergence(realizations, summary, buffer, tail, weighted);
            return summary;
        }

        /// <summary>
        /// Assigns one root diagnostic slot (0 evaluations, 1 standard error, 2 chi-squared).
        /// </summary>
        /// <param name="target">The target tree.</param>
        /// <param name="slot">The diagnostic slot.</param>
        /// <param name="value">The reduced value.</param>
        private static void SetRootDiagnostic(SystemRiskResults target, int slot, double value)
        {
            if (slot == 0) target.FunctionEvaluations = value;
            else if (slot == 1) target.StandardError = value;
            else target.ChiSquared = value;
        }

        /// <summary>
        /// Builds an empty results tree shaped like the template (component, mode, and
        /// consequence-type counts, with the declared labels), ready for measure assignment.
        /// </summary>
        /// <param name="template">The shape template (the first realization).</param>
        /// <returns>The shaped tree.</returns>
        private static SystemRiskResults CreateShapedTree(SystemRiskResults template)
        {
            var tree = new SystemRiskResults
            {
                ConsequenceLabels = new List<string>(template.ConsequenceLabels),
                ConsequenceUnits = new List<string>(template.ConsequenceUnits),
            };
            for (int k = 0; k < template.AdditionalConsequences.Count; k++)
            {
                tree.AdditionalConsequences.Add(new ConsequenceResults
                {
                    SpecifiedConsequence = template.AdditionalConsequences[k].SpecifiedConsequence,
                    ConsequenceUnit = template.AdditionalConsequences[k].ConsequenceUnit,
                });
            }
            for (int c = 0; c < template.ComponentResults.Count; c++)
            {
                var component = new ComponentResults();
                if (template.ComponentResults[c].SystemContribution != null)
                {
                    component.SystemContribution = new RiskContribution();
                }
                for (int k = 0; k < template.ComponentResults[c].AdditionalConsequences.Count; k++)
                {
                    component.AdditionalConsequences.Add(new ConsequenceResults
                    {
                        Contribution = template.ComponentResults[c].AdditionalConsequences[k].Contribution != null ? new RiskContribution() : null,
                    });
                }
                for (int m = 0; m < template.ComponentResults[c].FailureModeResults.Count; m++)
                {
                    var mode = new FailureModeResults();
                    if (template.ComponentResults[c].FailureModeResults[m].Contribution != null)
                    {
                        mode.Contribution = new RiskContribution();
                    }
                    for (int k = 0; k < template.ComponentResults[c].FailureModeResults[m].AdditionalConsequences.Count; k++)
                    {
                        mode.AdditionalConsequences.Add(new ConsequenceResults
                        {
                            Contribution = template.ComponentResults[c].FailureModeResults[m].AdditionalConsequences[k].Contribution != null ? new RiskContribution() : null,
                        });
                    }
                    component.FailureModeResults.Add(mode);
                }
                tree.ComponentResults.Add(component);
            }
            return tree;
        }

        /// <summary>
        /// Reduces the five stream summaries and every additional type's five streams at one
        /// results scope (the system root, or — through the selector — any nested scope
        /// carrying the same shape).
        /// </summary>
        /// <param name="realizations">The ensemble realizations.</param>
        /// <param name="targets">The four target trees (lower, upper, median, mean).</param>
        /// <param name="scope">Selects the scope's results from a realization.</param>
        /// <param name="buffer">The shared value buffer.</param>
        /// <param name="tail">The percentile tail level.</param>
        /// <param name="weighted">The weighted-reduction state, or null for the unweighted path.</param>
        private static void ReduceSummarySet(List<SystemRiskResults> realizations, SystemRiskResults[] targets,
            Func<SystemRiskResults, SystemRiskResults> scope, double[] buffer, double tail, WeightedReduction? weighted)
        {
            for (int a = 0; a < MeasureAccessors.Length; a++)
            {
                var accessor = MeasureAccessors[a];
                Reduce(realizations, r => accessor.Get(scope(r).Excess), buffer, tail, (slot, v) => accessor.Set(scope(targets[slot]).Excess, v), weighted);
                Reduce(realizations, r => accessor.Get(scope(r).Background), buffer, tail, (slot, v) => accessor.Set(scope(targets[slot]).Background, v), weighted);
                Reduce(realizations, r => accessor.Get(scope(r).Total), buffer, tail, (slot, v) => accessor.Set(scope(targets[slot]).Total, v), weighted);
                Reduce(realizations, r => accessor.Get(scope(r).Fail), buffer, tail, (slot, v) => accessor.Set(scope(targets[slot]).Fail, v), weighted);
                Reduce(realizations, r => accessor.Get(scope(r).NonFail), buffer, tail, (slot, v) => accessor.Set(scope(targets[slot]).NonFail, v), weighted);
            }
            int typeCount = scope(realizations[0]).AdditionalConsequences.Count;
            for (int k = 0; k < typeCount; k++)
            {
                int typeIndex = k;
                for (int a = 0; a < MeasureAccessors.Length; a++)
                {
                    var accessor = MeasureAccessors[a];
                    Reduce(realizations, r => accessor.Get(scope(r).AdditionalConsequences[typeIndex].Excess), buffer, tail,
                        (slot, v) => accessor.Set(scope(targets[slot]).AdditionalConsequences[typeIndex].Excess, v), weighted);
                    Reduce(realizations, r => accessor.Get(scope(r).AdditionalConsequences[typeIndex].Background), buffer, tail,
                        (slot, v) => accessor.Set(scope(targets[slot]).AdditionalConsequences[typeIndex].Background, v), weighted);
                    Reduce(realizations, r => accessor.Get(scope(r).AdditionalConsequences[typeIndex].Total), buffer, tail,
                        (slot, v) => accessor.Set(scope(targets[slot]).AdditionalConsequences[typeIndex].Total, v), weighted);
                    Reduce(realizations, r => accessor.Get(scope(r).AdditionalConsequences[typeIndex].Fail), buffer, tail,
                        (slot, v) => accessor.Set(scope(targets[slot]).AdditionalConsequences[typeIndex].Fail, v), weighted);
                    Reduce(realizations, r => accessor.Get(scope(r).AdditionalConsequences[typeIndex].NonFail), buffer, tail,
                        (slot, v) => accessor.Set(scope(targets[slot]).AdditionalConsequences[typeIndex].NonFail, v), weighted);
                }
            }
        }

        /// <summary>
        /// Reduces one component's five streams, additional types, contributions, and failure
        /// modes.
        /// </summary>
        /// <param name="realizations">The ensemble realizations.</param>
        /// <param name="targets">The four target trees.</param>
        /// <param name="componentIndex">The component position.</param>
        /// <param name="buffer">The shared value buffer.</param>
        /// <param name="tail">The percentile tail level.</param>
        /// <param name="weighted">The weighted-reduction state, or null for the unweighted path.</param>
        private static void ReduceComponent(List<SystemRiskResults> realizations, SystemRiskResults[] targets,
            int componentIndex, double[] buffer, double tail, WeightedReduction? weighted)
        {
            var template = realizations[0].ComponentResults[componentIndex];
            for (int a = 0; a < MeasureAccessors.Length; a++)
            {
                var accessor = MeasureAccessors[a];
                Reduce(realizations, r => accessor.Get(r.ComponentResults[componentIndex].Excess), buffer, tail,
                    (slot, v) => accessor.Set(targets[slot].ComponentResults[componentIndex].Excess, v), weighted);
                Reduce(realizations, r => accessor.Get(r.ComponentResults[componentIndex].Background), buffer, tail,
                    (slot, v) => accessor.Set(targets[slot].ComponentResults[componentIndex].Background, v), weighted);
                Reduce(realizations, r => accessor.Get(r.ComponentResults[componentIndex].Total), buffer, tail,
                    (slot, v) => accessor.Set(targets[slot].ComponentResults[componentIndex].Total, v), weighted);
                Reduce(realizations, r => accessor.Get(r.ComponentResults[componentIndex].Fail), buffer, tail,
                    (slot, v) => accessor.Set(targets[slot].ComponentResults[componentIndex].Fail, v), weighted);
                Reduce(realizations, r => accessor.Get(r.ComponentResults[componentIndex].NonFail), buffer, tail,
                    (slot, v) => accessor.Set(targets[slot].ComponentResults[componentIndex].NonFail, v), weighted);
            }

            for (int k = 0; k < template.AdditionalConsequences.Count; k++)
            {
                int typeIndex = k;
                for (int a = 0; a < MeasureAccessors.Length; a++)
                {
                    var accessor = MeasureAccessors[a];
                    Reduce(realizations, r => accessor.Get(r.ComponentResults[componentIndex].AdditionalConsequences[typeIndex].Total), buffer, tail,
                        (slot, v) => accessor.Set(targets[slot].ComponentResults[componentIndex].AdditionalConsequences[typeIndex].Total, v), weighted);
                    Reduce(realizations, r => accessor.Get(r.ComponentResults[componentIndex].AdditionalConsequences[typeIndex].Fail), buffer, tail,
                        (slot, v) => accessor.Set(targets[slot].ComponentResults[componentIndex].AdditionalConsequences[typeIndex].Fail, v), weighted);
                    Reduce(realizations, r => accessor.Get(r.ComponentResults[componentIndex].AdditionalConsequences[typeIndex].Excess), buffer, tail,
                        (slot, v) => accessor.Set(targets[slot].ComponentResults[componentIndex].AdditionalConsequences[typeIndex].Excess, v), weighted);
                    Reduce(realizations, r => accessor.Get(r.ComponentResults[componentIndex].AdditionalConsequences[typeIndex].Background), buffer, tail,
                        (slot, v) => accessor.Set(targets[slot].ComponentResults[componentIndex].AdditionalConsequences[typeIndex].Background, v), weighted);
                    Reduce(realizations, r => accessor.Get(r.ComponentResults[componentIndex].AdditionalConsequences[typeIndex].NonFail), buffer, tail,
                        (slot, v) => accessor.Set(targets[slot].ComponentResults[componentIndex].AdditionalConsequences[typeIndex].NonFail, v), weighted);
                }
                ReduceContribution(realizations, targets, buffer, tail,
                    r => r.ComponentResults[componentIndex].AdditionalConsequences[typeIndex].Contribution,
                    t => t.ComponentResults[componentIndex].AdditionalConsequences[typeIndex].Contribution, weighted);
            }

            ReduceContribution(realizations, targets, buffer, tail,
                r => r.ComponentResults[componentIndex].SystemContribution,
                t => t.ComponentResults[componentIndex].SystemContribution, weighted);

            for (int m = 0; m < template.FailureModeResults.Count; m++)
            {
                int modeIndex = m;
                for (int a = 0; a < MeasureAccessors.Length; a++)
                {
                    var accessor = MeasureAccessors[a];
                    Reduce(realizations, r => accessor.Get(r.ComponentResults[componentIndex].FailureModeResults[modeIndex].Excess), buffer, tail,
                        (slot, v) => accessor.Set(targets[slot].ComponentResults[componentIndex].FailureModeResults[modeIndex].Excess, v), weighted);
                    Reduce(realizations, r => accessor.Get(r.ComponentResults[componentIndex].FailureModeResults[modeIndex].Fail), buffer, tail,
                        (slot, v) => accessor.Set(targets[slot].ComponentResults[componentIndex].FailureModeResults[modeIndex].Fail, v), weighted);
                }
                for (int k = 0; k < template.FailureModeResults[m].AdditionalConsequences.Count; k++)
                {
                    int typeIndex = k;
                    for (int a = 0; a < MeasureAccessors.Length; a++)
                    {
                        var accessor = MeasureAccessors[a];
                        Reduce(realizations, r => accessor.Get(r.ComponentResults[componentIndex].FailureModeResults[modeIndex].AdditionalConsequences[typeIndex].Excess), buffer, tail,
                            (slot, v) => accessor.Set(targets[slot].ComponentResults[componentIndex].FailureModeResults[modeIndex].AdditionalConsequences[typeIndex].Excess, v), weighted);
                        Reduce(realizations, r => accessor.Get(r.ComponentResults[componentIndex].FailureModeResults[modeIndex].AdditionalConsequences[typeIndex].Fail), buffer, tail,
                            (slot, v) => accessor.Set(targets[slot].ComponentResults[componentIndex].FailureModeResults[modeIndex].AdditionalConsequences[typeIndex].Fail, v), weighted);
                    }
                    ReduceContribution(realizations, targets, buffer, tail,
                        r => r.ComponentResults[componentIndex].FailureModeResults[modeIndex].AdditionalConsequences[typeIndex].Contribution,
                        t => t.ComponentResults[componentIndex].FailureModeResults[modeIndex].AdditionalConsequences[typeIndex].Contribution, weighted);
                }
                ReduceContribution(realizations, targets, buffer, tail,
                    r => r.ComponentResults[componentIndex].FailureModeResults[modeIndex].Contribution,
                    t => t.ComponentResults[componentIndex].FailureModeResults[modeIndex].Contribution, weighted);
            }
        }

        /// <summary>
        /// Reduces the three contribution values at one scope (skipped when the target tree
        /// carries no contribution slot — the template realization had none).
        /// </summary>
        /// <param name="realizations">The ensemble realizations.</param>
        /// <param name="targets">The four target trees.</param>
        /// <param name="buffer">The shared value buffer.</param>
        /// <param name="tail">The percentile tail level.</param>
        /// <param name="source">Selects the scope's contribution from a realization.</param>
        /// <param name="target">Selects the scope's contribution slot from a target tree.</param>
        /// <param name="weighted">The weighted-reduction state, or null for the unweighted path.</param>
        private static void ReduceContribution(List<SystemRiskResults> realizations, SystemRiskResults[] targets,
            double[] buffer, double tail,
            Func<SystemRiskResults, RiskContribution?> source, Func<SystemRiskResults, RiskContribution?> target,
            WeightedReduction? weighted)
        {
            if (target(targets[0]) == null) return;
            for (int a = 0; a < ContributionAccessors.Length; a++)
            {
                var accessor = ContributionAccessors[a];
                Reduce(realizations, r =>
                {
                    var contribution = source(r);
                    return contribution != null ? accessor.Get(contribution) : double.NaN;
                }, buffer, tail, (slot, v) => accessor.Set(target(targets[slot])!, v), weighted);
            }
        }

        /// <summary>
        /// Reduces one measure across the ensemble into the four slots: NaN-filtered, the mean
        /// summed sequentially in realization order, the percentiles from a sorted copy at the
        /// curve-band convention. An all-NaN measure assigns NaN to every slot. Under weights
        /// the filter carries the weights pairwise, the mean is the weighted mean, and the
        /// percentiles use the symmetric weighted percentile; a surviving set with zero total
        /// weight assigns NaN to every slot like the all-NaN case.
        /// </summary>
        /// <param name="realizations">The ensemble realizations.</param>
        /// <param name="value">Selects the measure from a realization.</param>
        /// <param name="buffer">The shared value buffer (at least the realization count).</param>
        /// <param name="tail">The percentile tail level.</param>
        /// <param name="assign">Assigns a slot's reduced value (0 lower, 1 upper, 2 median, 3 mean).</param>
        /// <param name="weighted">The weighted-reduction state, or null for the unweighted path.</param>
        private static void Reduce(List<SystemRiskResults> realizations, Func<SystemRiskResults, double> value,
            double[] buffer, double tail, Action<int, double> assign, WeightedReduction? weighted)
        {
            if (weighted == null)
            {
                int count = 0;
                double total = 0d;
                for (int i = 0; i < realizations.Count; i++)
                {
                    double v = value(realizations[i]);
                    if (double.IsNaN(v)) continue;
                    buffer[count] = v;
                    total += v;
                    count++;
                }
                if (count == 0)
                {
                    assign(0, double.NaN);
                    assign(1, double.NaN);
                    assign(2, double.NaN);
                    assign(3, double.NaN);
                    return;
                }
                var sorted = new double[count];
                Array.Copy(buffer, sorted, count);
                Array.Sort(sorted);
                assign(0, Statistics.Percentile(sorted, tail, true));
                assign(1, Statistics.Percentile(sorted, 1d - tail, true));
                assign(2, Statistics.Percentile(sorted, 0.5d, true));
                assign(3, total / count);
                return;
            }

            var weights = weighted.Weights;
            var weightBuffer = weighted.WeightBuffer;
            int used = 0;
            double totalWeight = 0d;
            for (int i = 0; i < realizations.Count; i++)
            {
                double v = value(realizations[i]);
                if (double.IsNaN(v)) continue;
                buffer[used] = v;
                weightBuffer[used] = weights[i];
                totalWeight += weights[i];
                used++;
            }
            if (used == 0 || totalWeight <= 0d)
            {
                assign(0, double.NaN);
                assign(1, double.NaN);
                assign(2, double.NaN);
                assign(3, double.NaN);
                return;
            }
            var values = new double[used];
            var valueWeights = new double[used];
            Array.Copy(buffer, values, used);
            Array.Copy(weightBuffer, valueWeights, used);
            var percentiles = Statistics.Percentile(values, weighted.Levels, valueWeights, dataIsSorted: false);
            assign(0, percentiles[0]);
            assign(1, percentiles[1]);
            assign(2, percentiles[2]);
            assign(3, Statistics.Mean(values, valueWeights));
        }

        /// <summary>
        /// Aggregates the convergence diagnostics: integrator effort and error summaries plus
        /// the headline realization-adequacy indicators. The effort and error aggregates are
        /// deliberately unweighted even for a weighted ensemble — they describe computational
        /// effort actually spent; only the epistemic indicators reduce weighted.
        /// </summary>
        /// <param name="realizations">The ensemble realizations.</param>
        /// <param name="summary">The finished percentile trees (the half-width source).</param>
        /// <param name="buffer">The shared value buffer.</param>
        /// <param name="tail">The percentile tail level.</param>
        /// <param name="weighted">The weighted-reduction state, or null for the unweighted path.</param>
        /// <returns>The diagnostics.</returns>
        private static ConvergenceDiagnostics ComputeConvergence(List<SystemRiskResults> realizations,
            EnsembleSummary summary, double[] buffer, double tail, WeightedReduction? weighted)
        {
            var diagnostics = new ConvergenceDiagnostics();
            double totalEvaluations = 0d, maxEvaluations = 0d, sumError = 0d, maxError = 0d, sumChi = 0d, maxChi = 0d;
            int count = realizations.Count;
            for (int i = 0; i < count; i++)
            {
                totalEvaluations += realizations[i].FunctionEvaluations;
                maxEvaluations = Math.Max(maxEvaluations, realizations[i].FunctionEvaluations);
                sumError += realizations[i].StandardError;
                maxError = Math.Max(maxError, realizations[i].StandardError);
                sumChi += realizations[i].ChiSquared;
                maxChi = Math.Max(maxChi, realizations[i].ChiSquared);
                buffer[i] = realizations[i].StandardError;
            }
            diagnostics.TotalFunctionEvaluations = totalEvaluations;
            diagnostics.MeanFunctionEvaluations = totalEvaluations / count;
            diagnostics.MaxFunctionEvaluations = maxEvaluations;
            diagnostics.MeanStandardError = sumError / count;
            diagnostics.MaxStandardError = maxError;
            diagnostics.MeanChiSquared = sumChi / count;
            diagnostics.MaxChiSquared = maxChi;
            var sortedErrors = new double[count];
            Array.Copy(buffer, sortedErrors, count);
            Array.Sort(sortedErrors);
            diagnostics.MedianStandardError = Statistics.Percentile(sortedErrors, 0.5d, true);

            diagnostics.Indicators.Add(BuildIndicator("Annualized Failure Probability", realizations,
                r => r.Fail.TotalProbability, summary.Lower.Fail.TotalProbability, summary.Upper.Fail.TotalProbability, buffer, weighted));
            diagnostics.Indicators.Add(BuildIndicator("Mean Total Risk", realizations,
                r => r.Total.Mean, summary.Lower.Total.Mean, summary.Upper.Total.Mean, buffer, weighted));
            diagnostics.Indicators.Add(BuildIndicator("Mean Incremental Risk", realizations,
                r => r.Excess.Mean, summary.Lower.Excess.Mean, summary.Upper.Excess.Mean, buffer, weighted));
            for (int k = 0; k < realizations[0].AdditionalConsequences.Count; k++)
            {
                int typeIndex = k;
                string label = typeIndex + 1 < realizations[0].ConsequenceLabels.Count && realizations[0].ConsequenceLabels[typeIndex + 1].Length > 0
                    ? $"Mean Total Risk ({realizations[0].ConsequenceLabels[typeIndex + 1]})"
                    : $"Mean Total Risk (type {typeIndex + 1})";
                diagnostics.Indicators.Add(BuildIndicator(label, realizations,
                    r => r.AdditionalConsequences[typeIndex].Total.Mean,
                    summary.Lower.AdditionalConsequences[typeIndex].Total.Mean,
                    summary.Upper.AdditionalConsequences[typeIndex].Total.Mean, buffer, weighted));
            }
            return diagnostics;
        }

        /// <summary>
        /// Builds one realization-adequacy indicator: the ensemble mean, its Monte Carlo
        /// standard error (two-pass sample deviation over the valid values), and the confidence
        /// half-width from the finished percentile slots. Under weights the mean is the weighted
        /// mean and the standard error is √(V/N_eff) with the reliability-weighted unbiased
        /// variance V and the Kish effective sample size N_eff = (Σw)²/Σw² — at equal weights
        /// this equals the unweighted formula algebraically (the two compositions round
        /// differently in the last unit of precision). An undefined weighted variance (a single
        /// surviving point, or a degenerate bias-correction denominator) reports a zero standard
        /// error, mirroring the unweighted single-realization convention.
        /// </summary>
        /// <param name="label">The indicator label.</param>
        /// <param name="realizations">The ensemble realizations.</param>
        /// <param name="value">Selects the measure.</param>
        /// <param name="lower">The measure's lower percentile.</param>
        /// <param name="upper">The measure's upper percentile.</param>
        /// <param name="buffer">The shared value buffer.</param>
        /// <param name="weighted">The weighted-reduction state, or null for the unweighted path.</param>
        /// <returns>The indicator.</returns>
        private static ConvergenceIndicator BuildIndicator(string label, List<SystemRiskResults> realizations,
            Func<SystemRiskResults, double> value, double lower, double upper, double[] buffer,
            WeightedReduction? weighted)
        {
            int used = 0;
            double sum = 0d;
            double totalWeight = 0d;
            for (int i = 0; i < realizations.Count; i++)
            {
                double v = value(realizations[i]);
                if (double.IsNaN(v)) continue;
                buffer[used] = v;
                sum += v;
                if (weighted != null)
                {
                    weighted.WeightBuffer[used] = weighted.Weights[i];
                    totalWeight += weighted.Weights[i];
                }
                used++;
            }
            var indicator = new ConvergenceIndicator { Label = label };
            if (used == 0 || (weighted != null && totalWeight <= 0d))
            {
                indicator.Mean = double.NaN;
                indicator.EnsembleStandardError = double.NaN;
                indicator.RelativeStandardError = double.NaN;
                indicator.CiHalfWidth = double.NaN;
                indicator.RelativeCiHalfWidth = double.NaN;
                return indicator;
            }
            double mean;
            double standardError;
            if (weighted == null)
            {
                mean = sum / used;
                double centralSum = 0d;
                for (int i = 0; i < used; i++)
                {
                    double delta = buffer[i] - mean;
                    centralSum += delta * delta;
                }
                standardError = used > 1 ? Math.Sqrt(centralSum / (used - 1)) / Math.Sqrt(used) : 0d;
            }
            else
            {
                var values = new double[used];
                var valueWeights = new double[used];
                Array.Copy(buffer, values, used);
                Array.Copy(weighted.WeightBuffer, valueWeights, used);
                mean = Statistics.Mean(values, valueWeights);
                double variance = Statistics.Variance(values, valueWeights, WeightType.Reliability);
                double sumOfSquares = 0d;
                for (int i = 0; i < used; i++)
                {
                    sumOfSquares += valueWeights[i] * valueWeights[i];
                }
                double effectiveCount = totalWeight * totalWeight / sumOfSquares;
                standardError = double.IsNaN(variance) ? 0d : Math.Sqrt(variance / effectiveCount);
            }
            indicator.Mean = mean;
            indicator.EnsembleStandardError = standardError;
            indicator.RelativeStandardError = mean != 0d ? standardError / Math.Abs(mean) : double.NaN;
            indicator.CiHalfWidth = (upper - lower) / 2d;
            indicator.RelativeCiHalfWidth = mean != 0d ? indicator.CiHalfWidth / Math.Abs(mean) : double.NaN;
            return indicator;
        }

        #endregion
    }
}
