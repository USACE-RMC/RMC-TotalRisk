using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using RMC.TotalRisk.Results.Support;

namespace RMC.TotalRisk.Results.Risk
{
    /// <summary>
    /// The compact per-realization summary ensemble a full-uncertainty run publishes: one
    /// <see cref="SystemRiskResults"/> per realization.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// One of the two persisted results roots
    /// (docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §7.5): serialization is
    /// System.Text.Json through <see cref="ToJson"/>/<see cref="FromJson"/> and the
    /// GZip-compressed byte overloads — the v1.0 BinaryFormatter byte arrays are deliberately not
    /// readable in v1.1; old projects re-run their analyses. Out-of-range indexer reads preserve
    /// the v1.0 null result, while writes fail closed. Use <see cref="TryGetRealization"/> when
    /// probing a possibly absent slot is intentional.
    /// </para>
    /// </remarks>
    public class EnsembleResults
    {
        /// <summary>
        /// Backing field for <see cref="Realizations"/>.
        /// </summary>
        private SystemRiskResults?[] _realizations = Array.Empty<SystemRiskResults?>();

        /// <summary>
        /// Backing field for <see cref="LoadDiagnostics"/>.
        /// </summary>
        private readonly List<string> _loadDiagnostics = new List<string>();

        /// <summary>
        /// The immutable public view over <see cref="_loadDiagnostics"/>.
        /// </summary>
        private readonly ReadOnlyCollection<string> _loadDiagnosticsView;

        /// <summary>
        /// Initializes an empty ensemble.
        /// </summary>
        public EnsembleResults()
        {
            _loadDiagnosticsView = _loadDiagnostics.AsReadOnly();
        }

        /// <summary>
        /// Initializes an ensemble sized for the given realization count.
        /// </summary>
        /// <param name="length">The realization count. Must not be negative.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the length is negative.</exception>
        public EnsembleResults(int length)
            : this()
        {
            SetEnsembleLength(length);
        }

        /// <summary>
        /// The deterministic run provenance. Null only for legacy payloads or manually assembled
        /// result containers, which are explicitly unverified.
        /// </summary>
        public AnalysisRunManifest? Manifest { get; set; }

        /// <summary>Gets whether this result carries a supported provenance manifest.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsProvenanceVerified => Manifest?.IsCurrentSchema == true;

        /// <summary>
        /// The per-realization summaries — the serialized state. Assigning null coerces to empty.
        /// </summary>
        public SystemRiskResults?[] Realizations
        {
            get { return _realizations; }
            set { _realizations = value ?? Array.Empty<SystemRiskResults?>(); }
        }

        /// <summary>
        /// Gets or sets the summary at the given index. Out-of-range reads return null for legacy
        /// compatibility; out-of-range writes throw so a result cannot be silently discarded.
        /// </summary>
        /// <param name="index">The zero-based realization index.</param>
        /// <returns>The summary, or null when absent or out of range.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when a write index is outside the ensemble.</exception>
        public SystemRiskResults? this[int index]
        {
            get
            {
                if (index < 0 || index >= _realizations.Length) return null;
                return _realizations[index];
            }
            set
            {
                if (index < 0 || index >= _realizations.Length)
                    throw new ArgumentOutOfRangeException(nameof(index), "The realization index must be within the ensemble.");
                _realizations[index] = value;
            }
        }

        /// <summary>
        /// Attempts to read a realization slot without relying on the indexer's legacy null result
        /// to distinguish an out-of-range probe from an unwritten in-range slot.
        /// </summary>
        /// <param name="index">The zero-based realization index.</param>
        /// <param name="realization">The slot value when the index is in range; otherwise null.</param>
        /// <returns>True when the index is in range; otherwise false.</returns>
        public bool TryGetRealization(int index, out SystemRiskResults? realization)
        {
            if (index < 0 || index >= _realizations.Length)
            {
                realization = null;
                return false;
            }

            realization = _realizations[index];
            return true;
        }

        /// <summary>
        /// The number of realization slots.
        /// </summary>
        public int Count => _realizations.Length;

        /// <summary>
        /// The percentile confidence intervals on every scalar risk measure plus the aggregated
        /// convergence diagnostics, populated by the engine at the end of a
        /// full-uncertainty run and recomputable from any loaded ensemble via
        /// <see cref="ComputeSummary"/>. Null on older payloads and mean-only runs — "not
        /// computed" (append-only results JSON).
        /// </summary>
        public EnsembleSummary? Summary { get; set; }

        /// <summary>
        /// The optional epistemic realization weights, parallel to the realization slots — the
        /// single authoritative copy. Null means every realization carries equal weight and all
        /// reductions follow the unweighted path unchanged. Weights are raw (never normalized in
        /// storage; only relative values carry meaning — reliability semantics), are results-side
        /// annotations that never enter a canonical hash or a sampling seed, and serialize as an
        /// append-only field absent when null, so unweighted payloads are byte-identical to
        /// pre-weight payloads. Assign through <see cref="SetRealizationWeights"/> for eager
        /// validation; this property is the lenient serialization surface.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public double[]? RealizationWeights { get; set; }

        /// <summary>
        /// Integrity messages produced while loading this container from a serialized payload —
        /// runtime-only, never serialized. Empty for a clean load. A non-empty list means the
        /// payload failed an integrity check, the stored results were cleared, and the analysis
        /// must be rerun and its results saved again.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public IReadOnlyList<string> LoadDiagnostics => _loadDiagnosticsView;

        /// <summary>
        /// Assigns, replaces, or clears the epistemic realization weights with eager validation —
        /// the programmatic write surface (consumers such as likelihood re-weighting supply their
        /// posterior weights here, then recompute the summary via <see cref="ComputeSummary"/>).
        /// </summary>
        /// <param name="weights">The weights, one per realization slot; null clears the weights.</param>
        /// <exception cref="ArgumentException">Thrown when the weight count differs from the realization count, or every weight is zero.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when a weight is negative or not finite.</exception>
        public void SetRealizationWeights(IReadOnlyList<double>? weights)
        {
            if (weights == null)
            {
                RealizationWeights = null;
                return;
            }

            string? reason = DescribeInvalidWeights(weights, _realizations.Length, out bool valueOutOfRange);
            if (reason != null)
            {
                if (valueOutOfRange)
                {
                    throw new ArgumentOutOfRangeException(nameof(weights), reason);
                }
                throw new ArgumentException(reason, nameof(weights));
            }

            var copy = new double[weights.Count];
            for (int i = 0; i < weights.Count; i++)
            {
                copy[i] = weights[i];
            }
            RealizationWeights = copy;
        }

        /// <summary>
        /// Describes why a realization weight vector is invalid, or returns null when it is valid.
        /// The single validation rule shared by the programmatic setter, the analysis run gate,
        /// the summary reduction, and the payload readers: the length must equal the realization
        /// count, every weight must be finite and non-negative, and at least one weight must be
        /// positive (the upstream weighted-statistics guard convention).
        /// </summary>
        /// <param name="weights">The candidate weights.</param>
        /// <param name="expectedCount">The realization count the vector must match.</param>
        /// <param name="valueOutOfRange">True when the failure is a non-finite or negative weight value (the argument-out-of-range shape); false for the structural failures.</param>
        /// <returns>The failure description, or null when the vector is valid.</returns>
        internal static string? DescribeInvalidWeights(IReadOnlyList<double> weights, int expectedCount, out bool valueOutOfRange)
        {
            valueOutOfRange = false;
            if (weights.Count != expectedCount)
            {
                return $"The realization weight count ({weights.Count}) must equal the realization count ({expectedCount}).";
            }
            double total = 0d;
            for (int i = 0; i < weights.Count; i++)
            {
                double w = weights[i];
                if (double.IsNaN(w) || double.IsInfinity(w) || w < 0d)
                {
                    valueOutOfRange = true;
                    return "Realization weights must be finite and non-negative.";
                }
                total += w;
            }
            if (total <= 0d)
            {
                return "The realization weights must not all be zero.";
            }
            return null;
        }

        /// <summary>
        /// Applies the payload integrity checks after deserialization. An invalid stored weight
        /// vector does not throw: the stored results are cleared (realizations, summary, and
        /// weights), the manifest is kept for provenance, and a load diagnostic records that the
        /// analysis must be rerun and its results saved again.
        /// </summary>
        private void ApplyLoadIntegrityChecks()
        {
            if (RealizationWeights == null) return;
            string? reason = DescribeInvalidWeights(RealizationWeights, _realizations.Length, out _);
            if (reason == null) return;

            _realizations = Array.Empty<SystemRiskResults?>();
            Summary = null;
            RealizationWeights = null;
            _loadDiagnostics.Add(
                $"Error: The stored realization weights are invalid. {reason} The stored results were cleared; rerun the analysis and save its results again.");
        }

        /// <summary>
        /// Computes the scalar-measure percentile summary and convergence diagnostics from the
        /// stored realizations — the same reduction the engine runs, available on any loaded
        /// ensemble (the stored <see cref="Summary"/> is not modified).
        /// </summary>
        /// <param name="confidenceIntervalWidth">The confidence-interval width, in (0, 1).</param>
        /// <returns>The summary, or null when the ensemble holds no realizations.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the width is outside (0, 1).</exception>
        public EnsembleSummary? ComputeSummary(double confidenceIntervalWidth)
        {
            return EnsembleSummary.Compute(this, confidenceIntervalWidth);
        }

        /// <summary>
        /// Resizes the ensemble, clearing all current summaries.
        /// </summary>
        /// <param name="length">The realization count. Must not be negative.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the length is negative.</exception>
        public void SetEnsembleLength(int length)
        {
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length), "The ensemble length must not be negative.");
            _realizations = new SystemRiskResults?[length];
        }

        /// <summary>
        /// Serializes this ensemble to its JSON form.
        /// </summary>
        /// <returns>The JSON text.</returns>
        public string ToJson()
        {
            return ResultsJson.ToJson(this);
        }

        /// <summary>
        /// Restores an ensemble from its JSON form.
        /// </summary>
        /// <param name="json">The JSON text produced by <see cref="ToJson"/>.</param>
        /// <returns>The restored ensemble. A payload whose stored realization weights fail the
        /// integrity checks loads without throwing, with its results cleared and the failure
        /// recorded in <see cref="LoadDiagnostics"/> — the analysis must be rerun.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the JSON text is null.</exception>
        /// <exception cref="JsonException">Thrown when the text is not a serialized ensemble.</exception>
        /// <exception cref="JsonException">Thrown when the manifest schema is invalid or newer than this library supports.</exception>
        public static EnsembleResults FromJson(string json)
        {
            var results = ResultsJson.FromJson<EnsembleResults>(json);
            AnalysisRunManifest.ValidateSchema(results.Manifest);
            results.ApplyLoadIntegrityChecks();
            return results;
        }

        /// <summary>
        /// Serializes this ensemble to GZip-compressed UTF-8 JSON bytes.
        /// </summary>
        /// <returns>The compressed bytes.</returns>
        public byte[] ToCompressedBytes()
        {
            return ResultsJson.ToCompressedBytes(this);
        }

        /// <summary>
        /// Restores an ensemble from GZip-compressed UTF-8 JSON bytes.
        /// </summary>
        /// <param name="bytes">The bytes produced by <see cref="ToCompressedBytes"/>.</param>
        /// <returns>The restored ensemble. A payload whose stored realization weights fail the
        /// integrity checks loads without throwing, with its results cleared and the failure
        /// recorded in <see cref="LoadDiagnostics"/> — the analysis must be rerun.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the byte array is null.</exception>
        /// <exception cref="InvalidDataException">Thrown when the bytes are not a GZip stream.</exception>
        /// <exception cref="JsonException">Thrown when the decompressed text is not a serialized ensemble.</exception>
        /// <exception cref="JsonException">Thrown when the manifest schema is invalid or newer than this library supports.</exception>
        public static EnsembleResults FromCompressedBytes(byte[] bytes)
        {
            var results = ResultsJson.FromCompressedBytes<EnsembleResults>(bytes);
            AnalysisRunManifest.ValidateSchema(results.Manifest);
            results.ApplyLoadIntegrityChecks();
            return results;
        }
    }
}
