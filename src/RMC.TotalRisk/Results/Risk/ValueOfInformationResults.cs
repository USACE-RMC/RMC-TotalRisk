using System;
using System.Collections.Generic;
using RMC.TotalRisk.Core.Enums;

namespace RMC.TotalRisk.Results.Risk
{
    /// <summary>
    /// One value-of-information query's result: candidate studies ranked by the epistemic
    /// uncertainty in a stored risk measure they could resolve — per knowledge input and rolled
    /// up per function — with the perfect-information ceiling and, when tolerable-risk criteria
    /// are configured, the expected movement of each confidence statement per input.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// A plain query result — never serialized with the analysis results: value of information
    /// is recomputable on demand from the stored ensemble and the model (the knowledge columns
    /// re-derive bit-exactly from the content seeds), so persisting it would only bloat the
    /// append-only results surface. The quantities are measure-based — no remediation action
    /// model is assumed; converting a resolvable uncertainty into a strict decision value
    /// against study costs is the cost-benefit layer's job.
    /// </para>
    /// </remarks>
    public sealed class ValueOfInformationResults
    {
        /// <summary>
        /// Initializes a value-of-information result.
        /// </summary>
        /// <param name="outputLabel">The output's display label. Null coerces to empty.</param>
        /// <param name="riskType">The risk-type stream the measure is read from.</param>
        /// <param name="measure">The scalar measure explained.</param>
        /// <param name="consequenceType">The consequence-type position (0 is the primary).</param>
        /// <param name="bins">The equal-weight bin count behind the estimates.</param>
        /// <param name="realizations">The valid realizations behind the total variance.</param>
        /// <param name="totalVariance">The total epistemic variance of the measure.</param>
        /// <param name="entries">The per-input entries, in the sampler walk order.</param>
        /// <param name="groups">The per-function rollups, in first-appearance order.</param>
        /// <param name="criterionMovements">The per-criterion movement blocks (empty without configured criteria).</param>
        /// <exception cref="ArgumentNullException">Thrown when any list is null.</exception>
        public ValueOfInformationResults(string? outputLabel, RiskType riskType, RiskMeasure measure,
            int consequenceType, int bins, int realizations, double totalVariance,
            IReadOnlyList<ValueOfInformationEntry> entries, IReadOnlyList<ValueOfInformationGroup> groups,
            IReadOnlyList<TolerableRiskConfidenceMovement> criterionMovements)
        {
            OutputLabel = outputLabel ?? string.Empty;
            RiskType = riskType;
            Measure = measure;
            ConsequenceType = consequenceType;
            Bins = bins;
            Realizations = realizations;
            TotalVariance = totalVariance;
            if (entries == null) throw new ArgumentNullException(nameof(entries));
            if (groups == null) throw new ArgumentNullException(nameof(groups));
            if (criterionMovements == null) throw new ArgumentNullException(nameof(criterionMovements));
            Entries = Array.AsReadOnly(new List<ValueOfInformationEntry>(entries).ToArray());
            Groups = Array.AsReadOnly(new List<ValueOfInformationGroup>(groups).ToArray());
            CriterionMovements = Array.AsReadOnly(new List<TolerableRiskConfidenceMovement>(criterionMovements).ToArray());
        }

        /// <summary>
        /// The output's display label (e.g., "Mean — Total — System").
        /// </summary>
        public string OutputLabel { get; }

        /// <summary>
        /// The risk-type stream the measure came from.
        /// </summary>
        public RiskType RiskType { get; }

        /// <summary>
        /// The scalar measure explained.
        /// </summary>
        public RiskMeasure Measure { get; }

        /// <summary>
        /// The consequence-type position the measure was read from (0 is the primary type).
        /// </summary>
        public int ConsequenceType { get; }

        /// <summary>
        /// The equal-weight bin count behind the conditional estimates (the documented
        /// given-data convention).
        /// </summary>
        public int Bins { get; }

        /// <summary>
        /// The valid realizations behind the total variance (after NaN filtering).
        /// </summary>
        public int Realizations { get; }

        /// <summary>
        /// The total epistemic variance of the measure across the weighted ensemble — the
        /// perfect-information row: resolving every knowledge input removes all of it, because
        /// a realization's measure is deterministic given its knowledge draws.
        /// </summary>
        public double TotalVariance { get; }

        /// <summary>
        /// The total epistemic uncertainty in the measure's own units — the square root of
        /// <see cref="TotalVariance"/>.
        /// </summary>
        public double TotalStandardDeviation => TotalVariance >= 0d ? Math.Sqrt(TotalVariance) : double.NaN;

        /// <summary>
        /// The per-input entries, in the sampler walk order (stable across queries).
        /// </summary>
        public IReadOnlyList<ValueOfInformationEntry> Entries { get; }

        /// <summary>
        /// The candidate-study rollups — one per owning function, in first-appearance order.
        /// </summary>
        public IReadOnlyList<ValueOfInformationGroup> Groups { get; }

        /// <summary>
        /// The tolerable-risk confidence movement blocks, one per configured criterion in
        /// declared order; empty when the analysis configures no criteria or the query is not
        /// at the system scope.
        /// </summary>
        public IReadOnlyList<TolerableRiskConfidenceMovement> CriterionMovements { get; }

        /// <summary>
        /// The entries ranked by descending resolvable variance — the ranked-table order.
        /// NaN entries sink to the end; ties keep the walk order (stable sort).
        /// </summary>
        /// <returns>A new ranked list.</returns>
        public IReadOnlyList<ValueOfInformationEntry> RankedEntries()
        {
            var snapshot = new ValueOfInformationEntry[Entries.Count];
            for (int i = 0; i < snapshot.Length; i++) snapshot[i] = Entries[i];
            var indices = new int[snapshot.Length];
            for (int i = 0; i < indices.Length; i++) indices[i] = i;
            Array.Sort(indices, (a, b) =>
            {
                int comparison = CompareDescending(snapshot[a].ResolvableVariance, snapshot[b].ResolvableVariance);
                return comparison != 0 ? comparison : a.CompareTo(b);
            });
            var ranked = new List<ValueOfInformationEntry>(snapshot.Length);
            for (int i = 0; i < indices.Length; i++) ranked.Add(snapshot[indices[i]]);
            return ranked.AsReadOnly();
        }

        /// <summary>
        /// The candidate studies ranked by descending resolvable variance — the practitioner
        /// table's order. NaN groups sink to the end; ties keep first-appearance order.
        /// </summary>
        /// <returns>A new ranked list.</returns>
        public IReadOnlyList<ValueOfInformationGroup> RankedGroups()
        {
            var snapshot = new ValueOfInformationGroup[Groups.Count];
            for (int i = 0; i < snapshot.Length; i++) snapshot[i] = Groups[i];
            var indices = new int[snapshot.Length];
            for (int i = 0; i < indices.Length; i++) indices[i] = i;
            Array.Sort(indices, (a, b) =>
            {
                int comparison = CompareDescending(snapshot[a].ResolvableVariance, snapshot[b].ResolvableVariance);
                return comparison != 0 ? comparison : a.CompareTo(b);
            });
            var ranked = new List<ValueOfInformationGroup>(snapshot.Length);
            for (int i = 0; i < indices.Length; i++) ranked.Add(snapshot[indices[i]]);
            return ranked.AsReadOnly();
        }

        /// <summary>
        /// Compares two resolvable variances for a descending rank with NaN last.
        /// </summary>
        /// <param name="a">The first value.</param>
        /// <param name="b">The second value.</param>
        /// <returns>The comparison result.</returns>
        private static int CompareDescending(double a, double b)
        {
            bool aNaN = double.IsNaN(a);
            bool bNaN = double.IsNaN(b);
            if (aNaN && bNaN) return 0;
            if (aNaN) return 1;
            if (bNaN) return -1;
            return b.CompareTo(a);
        }
    }
}
