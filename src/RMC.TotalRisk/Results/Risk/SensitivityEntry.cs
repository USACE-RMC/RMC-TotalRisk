using System;

namespace RMC.TotalRisk.Results.Risk
{
    /// <summary>
    /// One input's sensitivity value against an output: the labeled knowledge input
    /// and its association measure — a tornado-plot bar.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// </remarks>
    public sealed class SensitivityEntry
    {
        /// <summary>
        /// Initializes an entry.
        /// </summary>
        /// <param name="label">The input's display label. Null coerces to empty.</param>
        /// <param name="value">The association value (the selected sensitivity measure).</param>
        public SensitivityEntry(string? label, double value)
        {
            Label = label ?? string.Empty;
            Value = value;
        }

        /// <summary>
        /// The input's display label (e.g., "Dam - Breach Fragility").
        /// </summary>
        public string Label { get; }

        /// <summary>
        /// The association value: a correlation in [−1, 1], or a sensitivity index in [0, 1].
        /// A non-finite correlation (a constant input or output) is coerced to zero — the v1.0
        /// convention.
        /// </summary>
        public double Value { get; }
    }
}
