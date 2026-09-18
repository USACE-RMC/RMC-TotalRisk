using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RMC.TotalRisk.Results.Support
{
    /// <summary>
    /// The shared System.Text.Json configuration and compression helpers behind the results
    /// containers' <c>ToJson()</c>/<c>FromJson()</c> and compressed-bytes surfaces.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Results are JSON, in-memory only
    /// (docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §7.5): model <i>definition</i> types keep
    /// <c>ToXElement()</c> as the canonical-hash identity surface, while results replace the v1.0
    /// BinaryFormatter BLOBs — which are deliberately not readable in v1.1; old projects re-run
    /// their analyses. System.Text.Json's default double formatting is the shortest string that
    /// round-trips exactly (the "R" equivalent), so serialized results are bit-faithful;
    /// <see cref="JsonNumberHandling.AllowNamedFloatingPointLiterals"/> lets NaN and ±Infinity —
    /// legitimate values for unpopulated risk measures — round-trip as quoted literals.
    /// </para>
    /// <para>
    /// Compression is GZip (<see cref="GZipStream"/>, optimal level): ubiquitous, streaming-friendly,
    /// and fast at the megabyte scale of realization ensembles. Callers comparing compressed
    /// payloads must compare the decompressed bytes — the GZip header embeds fields that are not
    /// content.
    /// </para>
    /// </remarks>
    internal static class ResultsJson
    {
        /// <summary>
        /// The single shared serializer configuration for every results container: named
        /// floating-point literals allowed, compact output.
        /// </summary>
        internal static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            WriteIndented = false,
        };

        /// <summary>
        /// Serializes a results container to its JSON form.
        /// </summary>
        /// <typeparam name="T">The container type.</typeparam>
        /// <param name="value">The container to serialize.</param>
        /// <returns>The JSON text.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the container is null.</exception>
        internal static string ToJson<T>(T value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            return JsonSerializer.Serialize(value, Options);
        }

        /// <summary>
        /// Restores a results container from its JSON form.
        /// </summary>
        /// <typeparam name="T">The container type.</typeparam>
        /// <param name="json">The JSON text produced by <see cref="ToJson{T}"/>.</param>
        /// <returns>The restored container.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the JSON text is null.</exception>
        /// <exception cref="JsonException">Thrown when the text is not a valid serialized container.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the text deserializes to null.</exception>
        internal static T FromJson<T>(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            var value = JsonSerializer.Deserialize<T>(json, Options);
            if (value == null) throw new InvalidOperationException($"The JSON text did not contain a serialized {typeof(T).Name}.");
            return value;
        }

        /// <summary>
        /// Serializes a results container to GZip-compressed UTF-8 JSON bytes.
        /// </summary>
        /// <typeparam name="T">The container type.</typeparam>
        /// <param name="value">The container to serialize.</param>
        /// <returns>The compressed bytes.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the container is null.</exception>
        internal static byte[] ToCompressedBytes<T>(T value)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(ToJson(value));
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                gzip.Write(utf8, 0, utf8.Length);
            }
            return output.ToArray();
        }

        /// <summary>
        /// Restores a results container from GZip-compressed UTF-8 JSON bytes.
        /// </summary>
        /// <typeparam name="T">The container type.</typeparam>
        /// <param name="bytes">The compressed bytes produced by <see cref="ToCompressedBytes{T}"/>.</param>
        /// <returns>The restored container.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the byte array is null.</exception>
        /// <exception cref="InvalidDataException">Thrown when the bytes are not a GZip stream.</exception>
        /// <exception cref="JsonException">Thrown when the decompressed text is not a valid serialized container.</exception>
        internal static T FromCompressedBytes<T>(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            using var input = new MemoryStream(bytes);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            return FromJson<T>(reader.ReadToEnd());
        }
    }
}
