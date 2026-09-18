using System;

namespace RMC.TotalRisk.Core.Support
{
    /// <summary>
    /// One labeled knowledge-input column of the sensitivity engine: a display label, a
    /// reader over the input's sampled percentile row — a per-function-dimension
    /// draw or a failure mode's consequence-coupling column — and the candidate-study group
    /// label that ties the column to the function whose uncertainty a study would resolve.
    /// Runtime-only; built by the component's sampler walk so labels and columns can never
    /// drift from the seeded streams.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// </remarks>
    internal sealed class SensitivityInput
    {
        /// <summary>
        /// Initializes a labeled input column.
        /// </summary>
        /// <param name="label">The display label (component/function role naming).</param>
        /// <param name="read">Reads the column's percentile for a realization index.</param>
        /// <param name="groupLabel">
        /// The candidate-study group the column belongs to — the owning function's deduplicated
        /// label, shared by every sampling dimension of one function; a coupling column is its
        /// own singleton group. Null falls back to <paramref name="label"/>.
        /// </param>
        /// <exception cref="ArgumentNullException">Thrown when the label or reader is null.</exception>
        public SensitivityInput(string label, Func<int, double> read, string? groupLabel = null)
        {
            Label = label ?? throw new ArgumentNullException(nameof(label));
            Read = read ?? throw new ArgumentNullException(nameof(read));
            GroupLabel = groupLabel ?? label;
        }

        /// <summary>
        /// The display label.
        /// </summary>
        public string Label { get; }

        /// <summary>
        /// Reads the column's sampled percentile for a realization index.
        /// </summary>
        public Func<int, double> Read { get; }

        /// <summary>
        /// The candidate-study group label: one group per owning function, so a multi-dimension
        /// function's columns roll up to the single study that would resolve them together.
        /// </summary>
        public string GroupLabel { get; }
    }
}
