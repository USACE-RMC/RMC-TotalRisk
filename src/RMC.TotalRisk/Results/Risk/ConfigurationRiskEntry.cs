using System;
using System.Collections.Generic;
using System.Linq;

namespace RMC.TotalRisk.Results.Risk
{
    /// <summary>
    /// One scope row of a configuration-risk query: the baseline and configured mean annual
    /// failure probabilities and per-consequence-type expected annual consequences of one
    /// component or of the system, with their derived changes and ratio.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// A plain query result — never serialized. The ratio reports the configured value over the
    /// baseline and is NaN when the baseline probability is zero, matching the established
    /// zero-probability ratio convention of the exact importance measures.
    /// </para>
    /// </remarks>
    public sealed class ConfigurationRiskEntry
    {
        /// <summary>
        /// Initializes one scope row.
        /// </summary>
        /// <param name="name">The component or system display label.</param>
        /// <param name="baselineFailureProbability">The baseline mean annual failure probability.</param>
        /// <param name="configuredFailureProbability">The configured mean annual failure probability.</param>
        /// <param name="baselineExpectedConsequences">The baseline expected annual consequences, position 0 primary.</param>
        /// <param name="configuredExpectedConsequences">The configured expected annual consequences, position 0 primary.</param>
        /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
        /// <exception cref="ArgumentException">Thrown when the consequence lists differ in length.</exception>
        public ConfigurationRiskEntry(string name, double baselineFailureProbability,
            double configuredFailureProbability, IReadOnlyList<double> baselineExpectedConsequences,
            IReadOnlyList<double> configuredExpectedConsequences)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            if (baselineExpectedConsequences == null) throw new ArgumentNullException(nameof(baselineExpectedConsequences));
            if (configuredExpectedConsequences == null) throw new ArgumentNullException(nameof(configuredExpectedConsequences));
            if (baselineExpectedConsequences.Count != configuredExpectedConsequences.Count)
                throw new ArgumentException("The baseline and configured consequence lists must align.", nameof(configuredExpectedConsequences));

            BaselineFailureProbability = baselineFailureProbability;
            ConfiguredFailureProbability = configuredFailureProbability;
            BaselineExpectedConsequences = Array.AsReadOnly(baselineExpectedConsequences.ToArray());
            ConfiguredExpectedConsequences = Array.AsReadOnly(configuredExpectedConsequences.ToArray());
            var changes = new double[BaselineExpectedConsequences.Count];
            for (int i = 0; i < changes.Length; i++)
                changes[i] = ConfiguredExpectedConsequences[i] - BaselineExpectedConsequences[i];
            ExpectedConsequenceChanges = Array.AsReadOnly(changes);
        }

        /// <summary>The component or system display label.</summary>
        public string Name { get; }

        /// <summary>The baseline mean annual failure probability.</summary>
        public double BaselineFailureProbability { get; }

        /// <summary>The configured mean annual failure probability.</summary>
        public double ConfiguredFailureProbability { get; }

        /// <summary>The configured-minus-baseline annual failure probability.</summary>
        public double FailureProbabilityChange => ConfiguredFailureProbability - BaselineFailureProbability;

        /// <summary>
        /// The configured-over-baseline failure-probability ratio; NaN when the baseline is zero.
        /// </summary>
        public double FailureProbabilityRatio => BaselineFailureProbability > 0d
            ? ConfiguredFailureProbability / BaselineFailureProbability
            : double.NaN;

        /// <summary>The baseline expected annual consequences, position 0 primary.</summary>
        public IReadOnlyList<double> BaselineExpectedConsequences { get; }

        /// <summary>The configured expected annual consequences, position 0 primary.</summary>
        public IReadOnlyList<double> ConfiguredExpectedConsequences { get; }

        /// <summary>The configured-minus-baseline expected annual consequences per type.</summary>
        public IReadOnlyList<double> ExpectedConsequenceChanges { get; }
    }
}
