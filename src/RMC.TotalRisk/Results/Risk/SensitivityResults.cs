using System;
using System.Collections.Generic;
using RMC.TotalRisk.Core.Enums;

namespace RMC.TotalRisk.Results.Risk
{
    /// <summary>
    /// One sensitivity query's result: the association of every knowledge input
    /// with one risk output — a scalar measure across the stored ensemble, or the risk at a
    /// hazard level — as a labeled entry list in the sampler walk order, with a ranked view for
    /// tornado plots.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// A plain query result — never serialized with the analysis results: sensitivity is
    /// recomputable on demand from the stored ensemble and the model (the inputs re-derive
    /// bit-exactly from the content seeds), so persisting it would only bloat the append-only
    /// results surface.
    /// </para>
    /// </remarks>
    public sealed class SensitivityResults
    {
        /// <summary>
        /// Initializes a sensitivity result.
        /// </summary>
        /// <param name="outputLabel">The output's display label. Null coerces to empty.</param>
        /// <param name="riskType">The risk-type stream the output came from.</param>
        /// <param name="measure">The association measure computed.</param>
        /// <param name="realizations">The number of realization pairs behind the associations.</param>
        /// <param name="entries">The labeled associations, in the sampler walk order.</param>
        /// <exception cref="ArgumentNullException">Thrown when the entry list is null.</exception>
        public SensitivityResults(string? outputLabel, RiskType riskType, SensitivityMeasure measure,
            int realizations, IReadOnlyList<SensitivityEntry> entries)
        {
            OutputLabel = outputLabel ?? string.Empty;
            RiskType = riskType;
            Measure = measure;
            Realizations = realizations;
            if (entries == null) throw new ArgumentNullException(nameof(entries));
            Entries = Array.AsReadOnly(new List<SensitivityEntry>(entries).ToArray());
        }

        /// <summary>
        /// The output's display label (e.g., "Mean — Total — Dam", "Risk at 1,250 — Excess").
        /// </summary>
        public string OutputLabel { get; }

        /// <summary>
        /// The risk-type stream the output came from.
        /// </summary>
        public RiskType RiskType { get; }

        /// <summary>
        /// The association measure computed.
        /// </summary>
        public SensitivityMeasure Measure { get; }

        /// <summary>
        /// The number of realization pairs behind the associations (after NaN filtering).
        /// </summary>
        public int Realizations { get; }

        /// <summary>
        /// The labeled associations, in the sampler walk order (stable across queries).
        /// </summary>
        public IReadOnlyList<SensitivityEntry> Entries { get; }

        /// <summary>
        /// The entries ranked by descending absolute association — the tornado-plot order.
        /// </summary>
        /// <returns>A new ranked list; ties keep the walk order (stable sort).</returns>
        public IReadOnlyList<SensitivityEntry> RankedByMagnitude()
        {
            var ranked = new List<SensitivityEntry>(Entries);
            // A stable insertion-style ordering via index-keyed comparison: OrderBy is stable,
            // but LINQ is avoided in library code — a simple index-paired sort preserves ties.
            var indices = new int[ranked.Count];
            for (int i = 0; i < indices.Length; i++) indices[i] = i;
            var snapshot = ranked.ToArray();
            Array.Sort(indices, (a, b) =>
            {
                int comparison = Math.Abs(snapshot[b].Value).CompareTo(Math.Abs(snapshot[a].Value));
                return comparison != 0 ? comparison : a.CompareTo(b);
            });
            for (int i = 0; i < indices.Length; i++)
            {
                ranked[i] = snapshot[indices[i]];
            }
            return ranked.AsReadOnly();
        }
    }
}
