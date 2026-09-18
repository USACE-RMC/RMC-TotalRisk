using System;
using System.Collections.Generic;
using System.Linq;

namespace RMC.TotalRisk.Results.LifeCycle
{
    /// <summary>
    /// One scope row of a life-cycle epoch — the system or one component: the epoch's annualized
    /// failure probability and its per-type expected annual consequences on the Total stream,
    /// with the Excess- and Fail-stream values alongside so a benefit stream can be selected
    /// without re-deriving the epoch.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// A plain query result — never serialized. Values are the deterministic mean-only answers of
    /// the epoch's configured quantification; consequence position 0 is the primary declared type.
    /// </para>
    /// </remarks>
    public sealed class LifeCycleEpochEntry
    {
        /// <summary>
        /// Initializes one scope row.
        /// </summary>
        /// <param name="name">The scope display label.</param>
        /// <param name="failureProbability">The epoch's annualized failure probability.</param>
        /// <param name="expectedConsequences">The per-type expected annual consequences (position 0 = primary).</param>
        /// <param name="excessExpectedConsequences">The Excess-stream per-type expected annual consequences; null for none.</param>
        /// <param name="failExpectedConsequences">The Fail-stream per-type expected annual consequences; null for none.</param>
        /// <exception cref="ArgumentNullException">Thrown when the name or the consequence list is null.</exception>
        /// <exception cref="ArgumentException">Thrown when a supplied stream list misaligns with the Total-stream list.</exception>
        public LifeCycleEpochEntry(string name, double failureProbability,
            IReadOnlyList<double> expectedConsequences,
            IReadOnlyList<double>? excessExpectedConsequences = null,
            IReadOnlyList<double>? failExpectedConsequences = null)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            FailureProbability = failureProbability;
            if (expectedConsequences == null) throw new ArgumentNullException(nameof(expectedConsequences));
            ExpectedConsequences = Array.AsReadOnly(expectedConsequences.ToArray());
            if (excessExpectedConsequences != null && excessExpectedConsequences.Count != ExpectedConsequences.Count)
                throw new ArgumentException("The Excess-stream consequence list must align with the Total-stream list.",
                    nameof(excessExpectedConsequences));
            if (failExpectedConsequences != null && failExpectedConsequences.Count != ExpectedConsequences.Count)
                throw new ArgumentException("The Fail-stream consequence list must align with the Total-stream list.",
                    nameof(failExpectedConsequences));
            ExcessExpectedConsequences = excessExpectedConsequences == null
                ? Array.AsReadOnly(Array.Empty<double>())
                : Array.AsReadOnly(excessExpectedConsequences.ToArray());
            FailExpectedConsequences = failExpectedConsequences == null
                ? Array.AsReadOnly(Array.Empty<double>())
                : Array.AsReadOnly(failExpectedConsequences.ToArray());
        }

        /// <summary>
        /// The scope display label — "System" or the component's name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// The epoch's annualized failure probability at this scope.
        /// </summary>
        public double FailureProbability { get; }

        /// <summary>
        /// The per-type expected annual consequences at this scope (position 0 = the primary
        /// declared type).
        /// </summary>
        public IReadOnlyList<double> ExpectedConsequences { get; }

        /// <summary>
        /// The Excess-stream per-type expected annual consequences at this scope — the
        /// incremental dam- and levee-safety measure; empty when the row does not carry the
        /// stream axis.
        /// </summary>
        public IReadOnlyList<double> ExcessExpectedConsequences { get; }

        /// <summary>
        /// The Fail-stream per-type expected annual consequences at this scope — the
        /// failure-path contribution; empty when the row does not carry the stream axis.
        /// </summary>
        public IReadOnlyList<double> FailExpectedConsequences { get; }
    }
}
