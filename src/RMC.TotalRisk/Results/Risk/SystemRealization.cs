using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using RMC.TotalRisk.Results.Support;

namespace RMC.TotalRisk.Results.Risk
{
    /// <summary>
    /// The full results of one Monte Carlo realization: the system-level loss exceedance curves,
    /// the per-component realizations, the observed hazard/consequence extents, and the
    /// integration diagnostics.
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
    /// readable in v1.1; old projects re-run their analyses. The mean, median, and confidence-bound
    /// summary realizations the engine publishes are instances of this type.
    /// </para>
    /// </remarks>
    public class SystemRealization
    {
        /// <summary>
        /// Initializes an empty system realization.
        /// </summary>
        public SystemRealization()
        {
            Curves = new Curves();
            Components = new List<ComponentRealization>();
            MinH = new List<double>();
            MaxH = new List<double>();
            AdditionalCurves = new List<Curves>();
            AdditionalMinN = new List<double>();
            AdditionalMaxN = new List<double>();
        }

        /// <summary>
        /// Initializes a system realization over the given component realizations, with one
        /// hazard-extent slot per component.
        /// </summary>
        /// <param name="componentRealizations">The component realizations, in analysis component order.</param>
        /// <exception cref="ArgumentNullException">Thrown when the list is null.</exception>
        public SystemRealization(List<ComponentRealization> componentRealizations)
        {
            if (componentRealizations == null) throw new ArgumentNullException(nameof(componentRealizations));
            Curves = new Curves();
            Components = componentRealizations;
            MinH = new List<double>(componentRealizations.Count);
            MaxH = new List<double>(componentRealizations.Count);
            for (int i = 0; i < componentRealizations.Count; i++)
            {
                MinH.Add(double.MaxValue);
                MaxH.Add(double.MinValue);
            }
            AdditionalCurves = new List<Curves>();
            AdditionalMinN = new List<double>();
            AdditionalMaxN = new List<double>();
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
        /// The realization's display name (the engine labels the published summary realizations
        /// "Mean", "Median", and the confidence bounds).
        /// </summary>
        public string Name { get; set; } = "System";

        /// <summary>
        /// The per-component realizations, in analysis component order.
        /// </summary>
        public List<ComponentRealization> Components { get; set; }

        /// <summary>
        /// The system-level five loss exceedance curve streams of the primary consequence type.
        /// </summary>
        public Curves Curves { get; set; }

        /// <summary>
        /// The system-level five-stream curve sets of the additional consequence types, in
        /// declared order (entry k − 1 is type k of the analysis's declared
        /// axis). Empty on a single-type analysis.
        /// </summary>
        public List<Curves> AdditionalCurves { get; set; }

        /// <summary>
        /// The smallest consequence observed anywhere in this realization (primary consequence
        /// type).
        /// </summary>
        public double MinN { get; set; } = double.MaxValue;

        /// <summary>
        /// The largest consequence observed anywhere in this realization (primary consequence
        /// type).
        /// </summary>
        public double MaxN { get; set; } = double.MinValue;

        /// <summary>
        /// The smallest consequence observed per additional consequence type, parallel to
        /// <see cref="AdditionalCurves"/>.
        /// </summary>
        public List<double> AdditionalMinN { get; set; }

        /// <summary>
        /// The largest consequence observed per additional consequence type, parallel to
        /// <see cref="AdditionalCurves"/>.
        /// </summary>
        public List<double> AdditionalMaxN { get; set; }

        /// <summary>
        /// The declared consequence type labels, one per type including the primary (entry 0) —
        /// display metadata the engine stamps from the analysis declaration.
        /// </summary>
        public List<string> ConsequenceLabels { get; set; } = new List<string>();

        /// <summary>
        /// The declared consequence unit labels, parallel to <see cref="ConsequenceLabels"/>.
        /// </summary>
        public List<string> ConsequenceUnits { get; set; } = new List<string>();

        /// <summary>
        /// The smallest hazard level observed per component, parallel to <see cref="Components"/>.
        /// </summary>
        public List<double> MinH { get; set; }

        /// <summary>
        /// The largest hazard level observed per component, parallel to <see cref="Components"/>.
        /// </summary>
        public List<double> MaxH { get; set; }

        /// <summary>
        /// The total number of integrand evaluations behind this realization — a genuine
        /// integration diagnostic.
        /// </summary>
        public double FunctionEvaluations { get; set; }

        /// <summary>
        /// The integrator's error estimate — for the Gauss–Kronrod path a true error estimate
        /// (the Kronrod-versus-Gauss difference norm), not the v1.0 Richardson proxy.
        /// </summary>
        public double StandardError { get; set; }

        /// <summary>
        /// The VEGAS chi-squared consistency diagnostic (joint system-risk path only; zero on the
        /// one-dimensional path).
        /// </summary>
        public double ChiSquared { get; set; }

        /// <summary>
        /// Ensures the additional consequence-type slots exist on the system scope and every
        /// component (curve sets and extent slots), creating any missing entries.
        /// </summary>
        /// <param name="count">The number of additional consequence types. Must not be negative.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the count is negative.</exception>
        public void EnsureAdditionalCurves(int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count), "The additional consequence-type count must not be negative.");
            while (AdditionalCurves.Count < count)
            {
                AdditionalCurves.Add(new Curves());
                AdditionalMinN.Add(double.MaxValue);
                AdditionalMaxN.Add(double.MinValue);
            }
            for (int i = 0; i < Components.Count; i++)
            {
                Components[i].EnsureAdditionalCurves(count);
            }
        }

        /// <summary>
        /// Clears every recorded risk point in the realization, across every consequence type —
        /// call only after post-processing.
        /// </summary>
        public void DumpMemory()
        {
            Curves.DumpMemory();
            for (int k = 0; k < AdditionalCurves.Count; k++)
            {
                AdditionalCurves[k].DumpMemory();
            }
            for (int i = 0; i < Components.Count; i++)
            {
                Components[i].DumpMemory();
            }
        }

        /// <summary>
        /// Serializes this realization to its JSON form.
        /// </summary>
        /// <returns>The JSON text.</returns>
        public string ToJson()
        {
            return ResultsJson.ToJson(this);
        }

        /// <summary>
        /// Restores a realization from its JSON form.
        /// </summary>
        /// <param name="json">The JSON text produced by <see cref="ToJson"/>.</param>
        /// <returns>The restored realization.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the JSON text is null.</exception>
        /// <exception cref="JsonException">Thrown when the text is not a serialized realization.</exception>
        /// <exception cref="JsonException">Thrown when the manifest schema is invalid or newer than this library supports.</exception>
        public static SystemRealization FromJson(string json)
        {
            var results = ResultsJson.FromJson<SystemRealization>(json);
            AnalysisRunManifest.ValidateSchema(results.Manifest);
            return results;
        }

        /// <summary>
        /// Serializes this realization to GZip-compressed UTF-8 JSON bytes.
        /// </summary>
        /// <returns>The compressed bytes.</returns>
        public byte[] ToCompressedBytes()
        {
            return ResultsJson.ToCompressedBytes(this);
        }

        /// <summary>
        /// Restores a realization from GZip-compressed UTF-8 JSON bytes.
        /// </summary>
        /// <param name="bytes">The bytes produced by <see cref="ToCompressedBytes"/>.</param>
        /// <returns>The restored realization.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the byte array is null.</exception>
        /// <exception cref="InvalidDataException">Thrown when the bytes are not a GZip stream.</exception>
        /// <exception cref="JsonException">Thrown when the decompressed text is not a serialized realization.</exception>
        /// <exception cref="JsonException">Thrown when the manifest schema is invalid or newer than this library supports.</exception>
        public static SystemRealization FromCompressedBytes(byte[] bytes)
        {
            var results = ResultsJson.FromCompressedBytes<SystemRealization>(bytes);
            AnalysisRunManifest.ValidateSchema(results.Manifest);
            return results;
        }
    }
}
