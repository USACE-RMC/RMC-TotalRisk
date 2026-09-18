using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Numerics.Distributions;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Systems.Components;

namespace RMC.TotalRisk.Results
{
    /// <summary>
    /// Deterministic provenance for one completed analysis run: binary versions, content
    /// fingerprints, canonical component identities, and the effective random streams.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The manifest is append-only result metadata. It deliberately excludes timestamps,
    /// machine names, thread counts, and paths, so identical inputs and seeds produce identical
    /// JSON. A missing manifest identifies a legacy, unverified result payload.
    /// </para>
    /// </remarks>
    public sealed class AnalysisRunManifest
    {
        /// <summary>The result-manifest schema written by this version of the library.</summary>
        public const int CurrentSchemaVersion = 1;

        /// <summary>The component content hashes in canonical order.</summary>
        private readonly string[] _componentContentHashes;

        /// <summary>The component occurrence indices parallel to <see cref="_componentContentHashes"/>.</summary>
        private readonly int[] _componentOccurrenceIndices;

        /// <summary>
        /// Initializes an immutable manifest. This constructor is also the JSON restoration
        /// surface; callers normally obtain manifests from a completed analysis.
        /// </summary>
        /// <param name="resultsSchemaVersion">The result-manifest schema version.</param>
        /// <param name="totalRiskAssemblyVersion">The TotalRisk assembly version.</param>
        /// <param name="numericsAssemblyVersion">The Numerics assembly version.</param>
        /// <param name="analysisContentHash">The deterministic analysis-content SHA-256 hash.</param>
        /// <param name="effectiveOptionsHash">The effective options' canonical SHA-256 hash.</param>
        /// <param name="componentContentHashes">Component canonical hashes in canonical order.</param>
        /// <param name="componentOccurrenceIndices">Occurrence indices parallel to the component hashes.</param>
        /// <param name="prngSeed">The analysis PRNG seed.</param>
        /// <param name="samplerSeedMapHash">The captured sampler-seed-map SHA-256 hash.</param>
        /// <param name="realizationWeightsHash">The SHA-256 hash of the run-input realization weights, or null for an unweighted run (the field is then absent from the serialized manifest — append-only results JSON).</param>
        /// <exception cref="ArgumentNullException">Thrown when a required string or component array is null.</exception>
        /// <exception cref="ArgumentException">Thrown when the component arrays have different lengths.</exception>
        [JsonConstructor]
        public AnalysisRunManifest(int resultsSchemaVersion, string totalRiskAssemblyVersion,
            string numericsAssemblyVersion, string analysisContentHash, string effectiveOptionsHash,
            string[] componentContentHashes, int[] componentOccurrenceIndices, int prngSeed,
            string samplerSeedMapHash, string? realizationWeightsHash = null)
        {
            if (totalRiskAssemblyVersion == null) throw new ArgumentNullException(nameof(totalRiskAssemblyVersion));
            if (numericsAssemblyVersion == null) throw new ArgumentNullException(nameof(numericsAssemblyVersion));
            if (analysisContentHash == null) throw new ArgumentNullException(nameof(analysisContentHash));
            if (effectiveOptionsHash == null) throw new ArgumentNullException(nameof(effectiveOptionsHash));
            if (componentContentHashes == null) throw new ArgumentNullException(nameof(componentContentHashes));
            if (componentOccurrenceIndices == null) throw new ArgumentNullException(nameof(componentOccurrenceIndices));
            if (samplerSeedMapHash == null) throw new ArgumentNullException(nameof(samplerSeedMapHash));
            if (componentContentHashes.Length != componentOccurrenceIndices.Length)
            {
                throw new ArgumentException("The component hash and occurrence-index arrays must have the same length.",
                    nameof(componentOccurrenceIndices));
            }

            ResultsSchemaVersion = resultsSchemaVersion;
            TotalRiskAssemblyVersion = totalRiskAssemblyVersion;
            NumericsAssemblyVersion = numericsAssemblyVersion;
            AnalysisContentHash = analysisContentHash;
            EffectiveOptionsHash = effectiveOptionsHash;
            _componentContentHashes = (string[])componentContentHashes.Clone();
            _componentOccurrenceIndices = (int[])componentOccurrenceIndices.Clone();
            PRNGSeed = prngSeed;
            SamplerSeedMapHash = samplerSeedMapHash;
            RealizationWeightsHash = realizationWeightsHash;
        }

        /// <summary>The result-manifest schema version.</summary>
        public int ResultsSchemaVersion { get; }

        /// <summary>The RMC.TotalRisk assembly version that produced the result.</summary>
        public string TotalRiskAssemblyVersion { get; }

        /// <summary>The Numerics assembly version used by the computation.</summary>
        public string NumericsAssemblyVersion { get; }

        /// <summary>
        /// A deterministic SHA-256 fingerprint over the effective options hash, canonical
        /// component identities, and consequence declarations.
        /// </summary>
        public string AnalysisContentHash { get; }

        /// <summary>The canonical SHA-256 hash of the materialized effective options.</summary>
        public string EffectiveOptionsHash { get; }

        /// <summary>The component canonical hashes in canonical hash/occurrence order.</summary>
        public string[] ComponentContentHashes => (string[])_componentContentHashes.Clone();

        /// <summary>The occurrence indices parallel to <see cref="ComponentContentHashes"/>.</summary>
        public int[] ComponentOccurrenceIndices => (int[])_componentOccurrenceIndices.Clone();

        /// <summary>The analysis PRNG seed.</summary>
        public int PRNGSeed { get; }

        /// <summary>The SHA-256 fingerprint of the captured sampler seed map.</summary>
        public string SamplerSeedMapHash { get; }

        /// <summary>
        /// The SHA-256 fingerprint of the epistemic realization weights the run consumed, or
        /// null for an unweighted run. The manifest records run provenance only — weights
        /// assigned to a result set after its run never appear here, because that run was
        /// truthfully unweighted. Absent from unweighted payloads (append-only results JSON).
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? RealizationWeightsHash { get; }

        /// <summary>Gets whether this manifest uses the schema understood by this library.</summary>
        [JsonIgnore]
        public bool IsCurrentSchema => ResultsSchemaVersion == CurrentSchemaVersion;

        /// <summary>
        /// Builds the deterministic manifest for a successful staged run.
        /// </summary>
        /// <param name="options">The materialized run options.</param>
        /// <param name="components">The immutable run component snapshot.</param>
        /// <param name="componentHashes">The component hashes in analysis order.</param>
        /// <param name="canonicalOrder">Analysis indices sorted by hash and occurrence index.</param>
        /// <param name="primaryConsequence">The primary consequence label.</param>
        /// <param name="primaryUnit">The primary consequence unit.</param>
        /// <param name="additionalConsequences">The additional consequence declarations.</param>
        /// <param name="seedMap">The captured effective sampler seeds.</param>
        /// <param name="realizationWeights">The run-input epistemic realization weights, or null for an unweighted run.</param>
        /// <returns>The immutable run manifest.</returns>
        internal static AnalysisRunManifest Create(RiskAnalysisOptions options,
            IReadOnlyList<SystemComponent> components, IReadOnlyList<byte[]> componentHashes,
            IReadOnlyList<int> canonicalOrder, string primaryConsequence, string primaryUnit,
            IReadOnlyList<ConsequenceTypeDescriptor> additionalConsequences, SamplerSeedMap seedMap,
            double[]? realizationWeights = null)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (components == null) throw new ArgumentNullException(nameof(components));
            if (componentHashes == null) throw new ArgumentNullException(nameof(componentHashes));
            if (canonicalOrder == null) throw new ArgumentNullException(nameof(canonicalOrder));
            if (additionalConsequences == null) throw new ArgumentNullException(nameof(additionalConsequences));
            if (seedMap == null) throw new ArgumentNullException(nameof(seedMap));

            string optionsHash = Convert.ToHexString(options.CanonicalHash());
            var hashes = new string[canonicalOrder.Count];
            var occurrences = new int[canonicalOrder.Count];
            for (int i = 0; i < canonicalOrder.Count; i++)
            {
                int componentIndex = canonicalOrder[i];
                hashes[i] = Convert.ToHexString(componentHashes[componentIndex]);
                occurrences[i] = components[componentIndex].OccurrenceIndex;
            }

            string analysisHash = ComputeAnalysisContentHash(optionsHash, hashes, occurrences,
                primaryConsequence ?? string.Empty, primaryUnit ?? string.Empty, additionalConsequences);
            return new AnalysisRunManifest(CurrentSchemaVersion,
                typeof(AnalysisRunManifest).Assembly.GetName().Version?.ToString() ?? string.Empty,
                typeof(IUnivariateDistribution).Assembly.GetName().Version?.ToString() ?? string.Empty,
                analysisHash, optionsHash, hashes, occurrences, options.PRNGSeed,
                ComputeSeedMapHash(seedMap),
                realizationWeights == null ? null : ComputeRealizationWeightsHash(realizationWeights));
        }

        /// <summary>Rejects a manifest schema that this library cannot interpret.</summary>
        /// <param name="manifest">The manifest, or null for a legacy unverified payload.</param>
        /// <exception cref="JsonException">Thrown when a present schema is invalid or unsupported.</exception>
        internal static void ValidateSchema(AnalysisRunManifest? manifest)
        {
            if (manifest == null) return;
            if (manifest.ResultsSchemaVersion < 1)
            {
                throw new JsonException($"Result manifest schema {manifest.ResultsSchemaVersion} is invalid.");
            }
            if (manifest.ResultsSchemaVersion > CurrentSchemaVersion)
            {
                throw new JsonException(
                    $"Result manifest schema {manifest.ResultsSchemaVersion} is newer than the supported schema {CurrentSchemaVersion}.");
            }
        }

        /// <summary>Hashes the deterministic analysis-content form.</summary>
        /// <param name="optionsHash">The effective options hash.</param>
        /// <param name="componentHashes">The canonical component hashes.</param>
        /// <param name="occurrences">The parallel occurrence indices.</param>
        /// <param name="primaryConsequence">The primary label.</param>
        /// <param name="primaryUnit">The primary unit.</param>
        /// <param name="additionalConsequences">The additional declarations.</param>
        /// <returns>The uppercase hexadecimal SHA-256 digest.</returns>
        private static string ComputeAnalysisContentHash(string optionsHash, IReadOnlyList<string> componentHashes,
            IReadOnlyList<int> occurrences, string primaryConsequence, string primaryUnit,
            IReadOnlyList<ConsequenceTypeDescriptor> additionalConsequences)
        {
            var content = new XElement("AnalysisContent",
                new XAttribute("EffectiveOptionsHash", optionsHash),
                new XElement("PrimaryConsequence",
                    new XAttribute("Label", primaryConsequence),
                    new XAttribute("Unit", primaryUnit)));
            var identities = new XElement("Components");
            for (int i = 0; i < componentHashes.Count; i++)
            {
                identities.Add(new XElement("Component",
                    new XAttribute("Hash", componentHashes[i]),
                    new XAttribute("OccurrenceIndex", occurrences[i])));
            }
            content.Add(identities);
            var declarations = new XElement("AdditionalConsequences");
            for (int i = 0; i < additionalConsequences.Count; i++)
            {
                declarations.Add(additionalConsequences[i].ToXElement());
            }
            content.Add(declarations);
            return Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(content.ToString(SaveOptions.DisableFormatting))));
        }

        /// <summary>Hashes the complete captured seed map without culture-sensitive text.</summary>
        /// <param name="seedMap">The captured seed map.</param>
        /// <returns>The uppercase hexadecimal SHA-256 digest.</returns>
        private static string ComputeSeedMapHash(SamplerSeedMap seedMap)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            AppendInt32(hash, seedMap.ComponentSeeds.Count);
            for (int i = 0; i < seedMap.ComponentSeeds.Count; i++)
            {
                int[] seeds = seedMap.ComponentSeeds[i];
                AppendInt32(hash, seeds.Length);
                for (int j = 0; j < seeds.Length; j++) AppendInt32(hash, seeds[j]);
            }
            AppendInt32(hash, seedMap.JointSeedBase);
            return Convert.ToHexString(hash.GetHashAndReset());
        }

        /// <summary>
        /// Hashes a run-input realization weight vector without culture-sensitive text: the
        /// count followed by each weight's fixed-endian bit pattern.
        /// </summary>
        /// <param name="weights">The realization weights.</param>
        /// <returns>The uppercase hexadecimal SHA-256 digest.</returns>
        private static string ComputeRealizationWeightsHash(double[] weights)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            AppendInt32(hash, weights.Length);
            Span<byte> bytes = stackalloc byte[sizeof(double)];
            for (int i = 0; i < weights.Length; i++)
            {
                BinaryPrimitives.WriteDoubleLittleEndian(bytes, weights[i]);
                hash.AppendData(bytes);
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }

        /// <summary>Appends one fixed-endian integer to a hash stream.</summary>
        /// <param name="hash">The incremental hash.</param>
        /// <param name="value">The integer value.</param>
        private static void AppendInt32(IncrementalHash hash, int value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            hash.AppendData(bytes);
        }
    }
}
