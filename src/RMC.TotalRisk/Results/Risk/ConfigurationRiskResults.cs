using System;
using System.Collections.Generic;
using System.Linq;

namespace RMC.TotalRisk.Results.Risk
{
    /// <summary>
    /// The result of one configuration-risk query: the mean-only baseline and configured
    /// re-quantifications of a system under a set of house-event overrides, as the system row,
    /// one row per component, the echoed consequence-type labels, and the display labels of the
    /// applied overrides.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// A plain query result — never serialized. Both quantifications run mean-only on throwaway
    /// clones, so the numbers are the deterministic "risk right now" answers and the authored
    /// model is never touched.
    /// </para>
    /// </remarks>
    public sealed class ConfigurationRiskResults
    {
        /// <summary>
        /// Initializes the query result.
        /// </summary>
        /// <param name="system">The system-scope row.</param>
        /// <param name="components">The per-component rows in declared component order.</param>
        /// <param name="consequenceLabels">The consequence-type labels, position 0 primary.</param>
        /// <param name="consequenceUnits">The consequence-type units, position 0 primary.</param>
        /// <param name="appliedOverrides">The display labels of the applied overrides, in configuration order.</param>
        /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
        public ConfigurationRiskResults(ConfigurationRiskEntry system,
            IReadOnlyList<ConfigurationRiskEntry> components, IReadOnlyList<string> consequenceLabels,
            IReadOnlyList<string> consequenceUnits, IReadOnlyList<string> appliedOverrides)
        {
            System = system ?? throw new ArgumentNullException(nameof(system));
            if (components == null) throw new ArgumentNullException(nameof(components));
            if (consequenceLabels == null) throw new ArgumentNullException(nameof(consequenceLabels));
            if (consequenceUnits == null) throw new ArgumentNullException(nameof(consequenceUnits));
            if (appliedOverrides == null) throw new ArgumentNullException(nameof(appliedOverrides));
            Components = Array.AsReadOnly(components.ToArray());
            ConsequenceLabels = Array.AsReadOnly(consequenceLabels.ToArray());
            ConsequenceUnits = Array.AsReadOnly(consequenceUnits.ToArray());
            AppliedOverrides = Array.AsReadOnly(appliedOverrides.ToArray());
        }

        /// <summary>The system-scope row.</summary>
        public ConfigurationRiskEntry System { get; }

        /// <summary>The per-component rows in declared component order.</summary>
        public IReadOnlyList<ConfigurationRiskEntry> Components { get; }

        /// <summary>The consequence-type labels, position 0 primary.</summary>
        public IReadOnlyList<string> ConsequenceLabels { get; }

        /// <summary>The consequence-type units, position 0 primary.</summary>
        public IReadOnlyList<string> ConsequenceUnits { get; }

        /// <summary>The display labels of the applied overrides, in configuration order.</summary>
        public IReadOnlyList<string> AppliedOverrides { get; }
    }
}
