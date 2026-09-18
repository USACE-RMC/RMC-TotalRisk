using System;
using System.Collections.Generic;
using System.Globalization;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results.CostBenefit;
using RMC.TotalRisk.Results.LifeCycle;
using RMC.TotalRisk.Results.Risk;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Analyses.CostBenefit;

namespace RMC.TotalRisk.Analyses.CostBenefit.Support
{
    /// <summary>
    /// Resolves cost-benefit metric selectors to values: economics metrics from the published
    /// economics rows under the study's headline accounting, horizon-basis risk measures from
    /// the trajectory aggregate arrays, and annualized risk measures from the retained epoch
    /// realizations re-measured at the study's exceedance levels through the engine's own
    /// scope-and-measure switches.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Tail and dispersion measures are aleatory, read from each epoch's mean loss-exceedance
    /// curve. Re-measurement never mutates retained state: the retained realization's curves
    /// are cloned per (trajectory, exceedance level), re-measured with the owning system's own
    /// per-type consequence thresholds (no hazard threshold at system scope, matching the
    /// engine's run-time call), assembled into a system-scope summary, and read through the
    /// same switch pair the engine and the tolerable-risk evaluation use. An annualized metric
    /// used as a single per-alternative value resolves at the first epoch — the year-zero,
    /// present-condition reading; whole-horizon readings are declared through the horizon
    /// bases. Reductions are signed, baseline minus alternative, and never clamped. Under
    /// reliability mode, consequence-dependent metrics report NaN and emit one named
    /// diagnostic each; a constraint on a NaN value is unsatisfied — an unmeasurable quantity
    /// cannot demonstrate compliance.
    /// </para>
    /// </remarks>
    internal sealed class CostBenefitMetricResolver
    {
        #region Construction

        /// <summary>The study declarations.</summary>
        private readonly CostBenefitOptions _options;

        /// <summary>The published economics rows, the baseline first.</summary>
        private readonly IReadOnlyList<AlternativeEconomics> _rows;

        /// <summary>The per-row trajectories, parallel to the rows.</summary>
        private readonly IReadOnlyList<LifeCycleRiskResults> _trajectories;

        /// <summary>
        /// The per-row consequence thresholds (position 0 the primary type, then the declared
        /// additional types), read from each alternative's own system declaration.
        /// </summary>
        private readonly IReadOnlyList<IReadOnlyList<double>> _consequenceThresholds;

        /// <summary>True when the study's alternatives run in reliability mode.</summary>
        private readonly bool _reliabilityMode;

        /// <summary>The horizon annuity factor (the derived absorbing equivalent-annual divisor).</summary>
        private readonly double _horizonAnnuity;

        /// <summary>The diagnostics sink the skipped-metric notices append to.</summary>
        private readonly List<ComputationDiagnostic> _diagnostics;

        /// <summary>The already-emitted diagnostic labels, so each skip is named once.</summary>
        private readonly HashSet<string> _emittedDiagnostics = new(StringComparer.Ordinal);

        /// <summary>
        /// The per-epoch summary snapshots, keyed by (trajectory, exceedance level). Rows that
        /// share one trajectory reference one system, so their thresholds agree and the cache
        /// is safe to share across rows.
        /// </summary>
        private readonly Dictionary<(LifeCycleRiskResults Trajectory, double Alpha), SystemRiskResults[]>
            _summaryCache = new();

        /// <summary>
        /// Initializes a resolver over one study run's published tables.
        /// </summary>
        /// <param name="options">The study declarations.</param>
        /// <param name="rows">The economics rows, the baseline first.</param>
        /// <param name="trajectories">The per-row trajectories, parallel to the rows.</param>
        /// <param name="consequenceThresholds">The per-row consequence thresholds (position 0 primary).</param>
        /// <param name="reliabilityMode">True when the alternatives run in reliability mode.</param>
        /// <param name="horizonAnnuity">The horizon annuity factor.</param>
        /// <param name="diagnostics">The diagnostics sink.</param>
        /// <exception cref="ArgumentNullException">Thrown when a required argument is null.</exception>
        internal CostBenefitMetricResolver(CostBenefitOptions options,
            IReadOnlyList<AlternativeEconomics> rows, IReadOnlyList<LifeCycleRiskResults> trajectories,
            IReadOnlyList<IReadOnlyList<double>> consequenceThresholds, bool reliabilityMode,
            double horizonAnnuity, List<ComputationDiagnostic> diagnostics)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _rows = rows ?? throw new ArgumentNullException(nameof(rows));
            _trajectories = trajectories ?? throw new ArgumentNullException(nameof(trajectories));
            _consequenceThresholds = consequenceThresholds ?? throw new ArgumentNullException(nameof(consequenceThresholds));
            _reliabilityMode = reliabilityMode;
            _horizonAnnuity = horizonAnnuity;
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        }

        #endregion

        #region Resolution

        /// <summary>
        /// Resolves a metric to one value per alternative: economics metrics from the row,
        /// horizon-basis measures from the trajectory aggregates, and annualized measures at
        /// the first epoch (the year-zero, present-condition reading).
        /// </summary>
        /// <param name="metric">The metric selector.</param>
        /// <param name="alternativeIndex">The row index (0 = the baseline).</param>
        /// <returns>The resolved value; NaN when the quantity is unavailable.</returns>
        internal double ResolveValue(CostBenefitMetric metric, int alternativeIndex)
        {
            if (metric.IsEconomic) return EconomicValue(metric.EconomicMetric, alternativeIndex);
            return RiskMeasureValue(metric, alternativeIndex, epochIndex: 0);
        }

        /// <summary>
        /// Evaluates a declared constraint for one alternative at its scope: Horizon checks
        /// the single whole-horizon value, FirstEpoch checks the first epoch, and EveryEpoch
        /// requires every epoch to satisfy the bound. A NaN value never satisfies.
        /// </summary>
        /// <param name="constraint">The constraint.</param>
        /// <param name="alternativeIndex">The row index (0 = the baseline).</param>
        /// <returns>True when the constraint is satisfied.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown for an undefined scope.</exception>
        internal bool EvaluateConstraint(CostBenefitConstraint constraint, int alternativeIndex)
        {
            switch (constraint.Scope)
            {
                case ConstraintScope.Horizon:
                    return Satisfies(ResolveValue(constraint.Metric, alternativeIndex), constraint);
                case ConstraintScope.FirstEpoch:
                    return Satisfies(RiskMeasureOrEconomicValue(constraint.Metric, alternativeIndex, 0), constraint);
                case ConstraintScope.EveryEpoch:
                    int epochCount = _trajectories[alternativeIndex].Epochs.Count;
                    for (int k = 0; k < epochCount; k++)
                    {
                        if (!Satisfies(RiskMeasureOrEconomicValue(constraint.Metric, alternativeIndex, k), constraint))
                        {
                            return false;
                        }
                    }
                    return true;
                default:
                    throw new ArgumentOutOfRangeException(nameof(constraint), constraint.Scope,
                        "The constraint scope is not a defined member.");
            }
        }

        /// <summary>
        /// Describes a metric for echoes and diagnostics: the economics member name, or the
        /// risk-measure axes with their exceedance level.
        /// </summary>
        /// <param name="metric">The metric selector.</param>
        /// <returns>The label.</returns>
        internal static string Describe(CostBenefitMetric metric)
        {
            if (metric.IsEconomic) return metric.EconomicMetric.ToString();
            string alpha = double.IsNaN(metric.Alpha)
                ? "study α"
                : "α = " + metric.Alpha.ToString(CultureInfo.InvariantCulture);
            return string.Create(CultureInfo.InvariantCulture,
                $"{metric.Measure} of {metric.RiskType} type {metric.ConsequenceType} ({metric.Basis}, {metric.Accounting}, {metric.Form}, {alpha})");
        }

        #endregion

        #region Private Helpers

        /// <summary>
        /// Applies a constraint's inequality; a NaN value satisfies neither sense.
        /// </summary>
        /// <param name="value">The resolved value.</param>
        /// <param name="constraint">The constraint.</param>
        /// <returns>True when the bound holds.</returns>
        private static bool Satisfies(double value, CostBenefitConstraint constraint)
        {
            return constraint.Sense == Numerics.Mathematics.Optimization.ConstraintType.GreaterThanOrEqualTo
                ? value >= constraint.Threshold
                : value <= constraint.Threshold;
        }

        /// <summary>
        /// Resolves a metric at one epoch: whole-horizon metrics are epoch-invariant, so the
        /// epoch index only steers annualized risk measures.
        /// </summary>
        /// <param name="metric">The metric selector.</param>
        /// <param name="alternativeIndex">The row index.</param>
        /// <param name="epochIndex">The epoch index.</param>
        /// <returns>The resolved value.</returns>
        private double RiskMeasureOrEconomicValue(CostBenefitMetric metric, int alternativeIndex, int epochIndex)
        {
            if (metric.IsEconomic) return EconomicValue(metric.EconomicMetric, alternativeIndex);
            return RiskMeasureValue(metric, alternativeIndex, epochIndex);
        }

        /// <summary>
        /// Resolves an economics metric from the published row, twin-selecting under the
        /// study's headline accounting where both conventions exist.
        /// </summary>
        /// <param name="metric">The economics metric.</param>
        /// <param name="alternativeIndex">The row index.</param>
        /// <returns>The row value.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown for an undefined member.</exception>
        private double EconomicValue(EconomicMetric metric, int alternativeIndex)
        {
            AlternativeEconomics row = _rows[alternativeIndex];
            bool absorbing = _options.Accounting == LifeCycleAccounting.Absorbing;
            if (_reliabilityMode && IsConsequenceDependent(metric))
            {
                return SkippedUnderReliability(metric.ToString());
            }
            return metric switch
            {
                EconomicMetric.PresentValueOfTotalCost => row.TotalCostPresentValue,
                EconomicMetric.EquivalentAnnualCost => row.EquivalentAnnualCost,
                EconomicMetric.TotalExpectedAnnualCost => absorbing
                    ? row.AbsorbingTotalExpectedAnnualCost
                    : row.TotalExpectedAnnualCost,
                EconomicMetric.NetPresentValue => absorbing
                    ? row.AbsorbingNetPresentValue
                    : row.NetPresentValue,
                EconomicMetric.NetAnnualBenefit => absorbing
                    ? row.AbsorbingNetAnnualBenefit
                    : row.NetAnnualBenefit,
                EconomicMetric.BenefitCostRatio => absorbing
                    ? row.AbsorbingBenefitCostRatio
                    : row.BenefitCostRatio,
                EconomicMetric.CostPerStatisticalLifeSavedUnadjusted => row.CostPerStatisticalLifeSavedUnadjusted,
                EconomicMetric.CostPerStatisticalLifeSavedAdjusted => row.CostPerStatisticalLifeSavedAdjusted,
                EconomicMetric.EquityWeightedAdjustedCostPerStatisticalLifeSaved =>
                    row.EquityWeightedAdjustedCostPerStatisticalLifeSaved,
                EconomicMetric.CostPerStatisticalFailurePrevented => row.CostPerStatisticalFailurePrevented,
                EconomicMetric.AbsorbingAdjustedCostPerStatisticalLifeSaved =>
                    row.AbsorbingAdjustedCostPerStatisticalLifeSaved,
                EconomicMetric.DisproportionalityRatio => row.DisproportionalityRatio,
                EconomicMetric.AnnualizedFailureProbability => row.AnnualizedFailureProbability,
                EconomicMetric.AnnualizedFailureProbabilityReduction => row.AnnualizedFailureProbabilityReduction,
                EconomicMetric.MonetizedPresentValueBenefit => absorbing
                    ? row.AbsorbingMonetizedPresentValueBenefit
                    : row.MonetizedPresentValueBenefit,
                _ => throw new ArgumentOutOfRangeException(nameof(metric), metric,
                    "The economics metric is not a defined member."),
            };
        }

        /// <summary>
        /// True for the economics metrics that require consequence output — the members that
        /// read monetized benefits or the life-loss axis. Costs, the failure-probability
        /// metrics, and cost per statistical failure prevented stay active under reliability
        /// mode.
        /// </summary>
        /// <param name="metric">The economics metric.</param>
        /// <returns>True when consequence-dependent.</returns>
        private static bool IsConsequenceDependent(EconomicMetric metric)
        {
            return metric is EconomicMetric.TotalExpectedAnnualCost
                or EconomicMetric.NetPresentValue
                or EconomicMetric.NetAnnualBenefit
                or EconomicMetric.BenefitCostRatio
                or EconomicMetric.MonetizedPresentValueBenefit
                or EconomicMetric.CostPerStatisticalLifeSavedUnadjusted
                or EconomicMetric.CostPerStatisticalLifeSavedAdjusted
                or EconomicMetric.EquityWeightedAdjustedCostPerStatisticalLifeSaved
                or EconomicMetric.AbsorbingAdjustedCostPerStatisticalLifeSaved
                or EconomicMetric.DisproportionalityRatio;
        }

        /// <summary>
        /// Resolves a risk-measure selector, form-aware: the level, or the signed reduction
        /// (baseline minus alternative at the same epoch and basis), never clamped.
        /// </summary>
        /// <param name="metric">The selector.</param>
        /// <param name="alternativeIndex">The row index.</param>
        /// <param name="epochIndex">The epoch index (annualized basis only).</param>
        /// <returns>The resolved value.</returns>
        private double RiskMeasureValue(CostBenefitMetric metric, int alternativeIndex, int epochIndex)
        {
            if (_reliabilityMode && metric.Measure != RiskMeasure.TotalProbability)
            {
                return SkippedUnderReliability(Describe(metric));
            }
            double level = RiskMeasureLevel(metric, alternativeIndex, epochIndex);
            if (metric.Form == MetricForm.Level) return level;
            return RiskMeasureLevel(metric, 0, epochIndex) - level;
        }

        /// <summary>
        /// Reads one alternative's level for a risk-measure selector: horizon bases from the
        /// trajectory aggregate arrays (the absorbing equivalent-annual level derives as the
        /// absorbing present value over the horizon annuity), the annualized basis from the
        /// epoch summary through the engine's scope-and-measure switches.
        /// </summary>
        /// <param name="metric">The selector.</param>
        /// <param name="alternativeIndex">The row index.</param>
        /// <param name="epochIndex">The epoch index (annualized basis only).</param>
        /// <returns>The level; NaN when the quantity is unavailable.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown for an undefined basis.</exception>
        private double RiskMeasureLevel(CostBenefitMetric metric, int alternativeIndex, int epochIndex)
        {
            LifeCycleRiskResults trajectory = _trajectories[alternativeIndex];
            if (metric.Basis == MetricBasis.AnnualizedPerEpoch)
            {
                double alpha = double.IsNaN(metric.Alpha) ? _options.AlphaLevels[0] : metric.Alpha;
                SystemRiskResults summary = SummariesAt(alternativeIndex, alpha)[epochIndex];
                SummaryRiskResults? stream = RiskAnalysis.SelectScope(summary, -1, -1,
                    metric.RiskType, metric.ConsequenceType);
                return stream == null ? double.NaN : RiskAnalysis.ExtractMeasure(stream, metric.Measure);
            }

            // The horizon bases carry the Mean measure on the aggregate streams (validated at
            // declaration time); the type position is bounds-checked defensively.
            var aggregates = CostBenefitAnalysis.SelectStreamAggregates(trajectory, metric.RiskType);
            if (metric.ConsequenceType >= aggregates.PresentValue.Count) return double.NaN;
            bool absorbing = metric.Accounting == LifeCycleAccounting.Absorbing;
            return metric.Basis switch
            {
                MetricBasis.HorizonPresentValue => absorbing
                    ? aggregates.AbsorbingPresentValue[metric.ConsequenceType]
                    : aggregates.PresentValue[metric.ConsequenceType],
                MetricBasis.HorizonEquivalentAnnual => absorbing
                    ? aggregates.AbsorbingPresentValue[metric.ConsequenceType] / _horizonAnnuity
                    : aggregates.EquivalentAnnual[metric.ConsequenceType],
                MetricBasis.HorizonCumulative => absorbing
                    ? aggregates.AbsorbingCumulative[metric.ConsequenceType]
                    : aggregates.Cumulative[metric.ConsequenceType],
                _ => throw new ArgumentOutOfRangeException(nameof(metric), metric.Basis,
                    "The metric basis is not a defined member."),
            };
        }

        /// <summary>
        /// Reports one named skip under reliability mode and returns NaN.
        /// </summary>
        /// <param name="label">The metric label.</param>
        /// <returns>NaN.</returns>
        private double SkippedUnderReliability(string label)
        {
            if (_emittedDiagnostics.Add(label))
            {
                _diagnostics.Add(new ComputationDiagnostic("TRC2001", DiagnosticSeverity.Warning,
                    $"The metric '{label}' is consequence-dependent and reports NaN under reliability mode.",
                    string.Empty));
            }
            return double.NaN;
        }

        /// <summary>
        /// Builds or reuses the per-epoch summary snapshots for one trajectory at one
        /// exceedance level.
        /// </summary>
        /// <param name="alternativeIndex">The row index.</param>
        /// <param name="alpha">The exceedance level.</param>
        /// <returns>The per-epoch summaries.</returns>
        /// <exception cref="InvalidOperationException">Thrown when an epoch retains no realization.</exception>
        private SystemRiskResults[] SummariesAt(int alternativeIndex, double alpha)
        {
            LifeCycleRiskResults trajectory = _trajectories[alternativeIndex];
            if (_summaryCache.TryGetValue((trajectory, alpha), out SystemRiskResults[]? cached))
            {
                return cached;
            }
            IReadOnlyList<double> thresholds = _consequenceThresholds[alternativeIndex];
            var summaries = new SystemRiskResults[trajectory.Epochs.Count];
            for (int k = 0; k < trajectory.Epochs.Count; k++)
            {
                SystemRealization? realization = trajectory.Epochs[k].Realization;
                if (realization == null)
                {
                    throw new InvalidOperationException(
                        "The trajectory retains no epoch realization; annualized measures require retained epoch state.");
                }
                summaries[k] = BuildSummary(realization, alpha, thresholds);
            }
            _summaryCache.Add((trajectory, alpha), summaries);
            return summaries;
        }

        /// <summary>
        /// Builds one system-scope summary from a retained realization at an exceedance level:
        /// the curves are cloned (retained state is shared and never mutated), re-measured
        /// with the declared per-type consequence thresholds and no hazard threshold, and
        /// captured as the compact measure snapshot. Component summaries are omitted — the
        /// selector vocabulary is system-scope.
        /// </summary>
        /// <param name="realization">The retained epoch realization.</param>
        /// <param name="alpha">The exceedance level.</param>
        /// <param name="thresholds">The per-type consequence thresholds (position 0 primary).</param>
        /// <returns>The summary snapshot.</returns>
        private static SystemRiskResults BuildSummary(SystemRealization realization, double alpha,
            IReadOnlyList<double> thresholds)
        {
            Curves curves = realization.Curves.Clone();
            curves.ComputeRiskMeasures(thresholds.Count > 0 ? thresholds[0] : double.NaN, alpha);
            var summary = new SystemRiskResults
            {
                Excess = new SummaryRiskResults(curves.Excess),
                Background = new SummaryRiskResults(curves.Background),
                Total = new SummaryRiskResults(curves.Total),
                Fail = new SummaryRiskResults(curves.Fail),
                NonFail = new SummaryRiskResults(curves.NonFail),
            };
            summary.ConsequenceLabels.AddRange(realization.ConsequenceLabels);
            summary.ConsequenceUnits.AddRange(realization.ConsequenceUnits);
            for (int k = 0; k < realization.AdditionalCurves.Count; k++)
            {
                Curves typeCurves = realization.AdditionalCurves[k].Clone();
                double threshold = k + 1 < thresholds.Count ? thresholds[k + 1] : double.NaN;
                typeCurves.ComputeRiskMeasures(threshold, alpha);
                summary.AdditionalConsequences.Add(new ConsequenceResults(typeCurves));
            }
            return summary;
        }

        #endregion
    }
}
