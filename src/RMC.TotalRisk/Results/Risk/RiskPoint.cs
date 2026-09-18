using System;
using System.Collections.Generic;
using Numerics;

namespace RMC.TotalRisk.Results.Risk
{
    /// <summary>
    /// A single risk evaluation point recorded by the adaptive integrator: the hazard level, its
    /// probability coordinates, and the parallel response-probability / consequence entries.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The parallel entries contribute probability mass
    /// <c>HazardProbabilityMass · ResponseProbabilities[i]</c> at consequence
    /// <c>Consequences[i]</c>. A failure mode records one entry per exposure branch
    /// and a component pathway records one entry per branch combination, so the full set of
    /// <c>(mass, consequence)</c> pairs across all points is the exact discretized loss
    /// distribution the curve is built from. Risk points are runtime working state — never
    /// serialized; realizations clear them via <c>DumpMemory()</c> after post-processing.
    /// The engine's dominant single-entry points use inline storage; the public list surface is
    /// materialized on first inspection, preserving the v1.0-compatible API without two heap
    /// arrays per recorded scalar.
    /// </para>
    /// </remarks>
    public class RiskPoint
    {
        /// <summary>The response-probability entries after materialization.</summary>
        private List<double>? _responseProbabilities;

        /// <summary>The consequence entries after materialization.</summary>
        private List<double>? _consequences;

        /// <summary>Whether this point is using the allocation-free single-entry representation.</summary>
        private bool _inlineStorage;

        /// <summary>Whether the inline representation currently contains its one entry.</summary>
        private bool _hasInlineEntry;

        /// <summary>The inline response probability.</summary>
        private double _inlineResponseProbability;

        /// <summary>The inline consequence.</summary>
        private double _inlineConsequence;

        /// <summary>
        /// Initializes an empty risk point.
        /// </summary>
        public RiskPoint()
        {
            _responseProbabilities = new List<double>();
            _consequences = new List<double>();
        }

        /// <summary>
        /// Initializes an empty risk point with entry-list capacity pre-allocated. A capacity of
        /// one uses inline storage until the public list surface is inspected.
        /// </summary>
        /// <param name="capacity">The expected number of entries. Must not be negative.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the capacity is negative.</exception>
        public RiskPoint(int capacity)
        {
            if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity), "The capacity must not be negative.");
            _inlineStorage = capacity == 1;
            if (!_inlineStorage)
            {
                _responseProbabilities = new List<double>(capacity);
                _consequences = new List<double>(capacity);
            }
        }
        /// <summary>Adopts caller-owned parallel entry lists without creating throwaway lists.</summary>
        /// <param name="responseProbabilities">The response probabilities to adopt.</param>
        /// <param name="consequences">The parallel consequences to adopt.</param>
        internal RiskPoint(List<double> responseProbabilities, List<double> consequences)
        {
            _responseProbabilities = responseProbabilities ?? throw new ArgumentNullException(nameof(responseProbabilities));
            _consequences = consequences ?? throw new ArgumentNullException(nameof(consequences));
        }

        /// <summary>The hazard level where the risk was evaluated.</summary>
        public double HazardLevel { get; set; }

        /// <summary>The hazard level non-exceedance probability, P[X ≤ x].</summary>
        public double HazardProbability { get; set; }

        /// <summary>
        /// The driving hazard's annual exceedance probability at the evaluation, P[X &gt; x] from
        /// the realization's sampled hazard distribution. NaN when the recording path does not
        /// supply it.
        /// </summary>
        public double HazardExceedanceProbability { get; set; } = double.NaN;

        /// <summary>
        /// The hazard probability mass, dF(x) — the quadrature weight this point carries.
        /// </summary>
        public double HazardProbabilityMass { get; set; }

        /// <summary>
        /// The response probabilities P[F|x] of the point's entries, parallel to
        /// <see cref="Consequences"/>. Inline storage materializes into this list on first access.
        /// </summary>
        public List<double> ResponseProbabilities
        {
            get
            {
                if (_inlineStorage) MaterializeEntryLists();
                return _responseProbabilities!;
            }
            set
            {
                if (_inlineStorage) MaterializeEntryLists();
                _responseProbabilities = value;
            }
        }

        /// <summary>
        /// The consequences C(x) of the point's entries, parallel to
        /// <see cref="ResponseProbabilities"/>. Inline storage materializes into this list on first
        /// access.
        /// </summary>
        public List<double> Consequences
        {
            get
            {
                if (_inlineStorage) MaterializeEntryLists();
                return _consequences!;
            }
            set
            {
                if (_inlineStorage) MaterializeEntryLists();
                _consequences = value;
            }
        }

        /// <summary>The number of entries without forcing inline storage to materialize.</summary>
        internal int EntryCount => _inlineStorage
            ? (_hasInlineEntry ? 1 : 0)
            : _responseProbabilities?.Count ?? 0;

        /// <summary>Whether the probability and consequence stores are present and parallel.</summary>
        internal bool HasParallelEntries => _inlineStorage
            || _responseProbabilities != null && _consequences != null
                && _responseProbabilities.Count == _consequences.Count;

        /// <summary>Gets one response probability without materializing inline storage.</summary>
        /// <param name="index">The entry index.</param>
        /// <returns>The response probability.</returns>
        internal double ResponseProbabilityAt(int index)
        {
            if (_inlineStorage)
            {
                if (!_hasInlineEntry || index != 0) throw new ArgumentOutOfRangeException(nameof(index));
                return _inlineResponseProbability;
            }
            return _responseProbabilities![index];
        }

        /// <summary>Gets one consequence without materializing inline storage.</summary>
        /// <param name="index">The entry index.</param>
        /// <returns>The consequence.</returns>
        internal double ConsequenceAt(int index)
        {
            if (_inlineStorage)
            {
                if (!_hasInlineEntry || index != 0) throw new ArgumentOutOfRangeException(nameof(index));
                return _inlineConsequence;
            }
            return _consequences![index];
        }

        /// <summary>Replaces one response probability without materializing inline storage.</summary>
        /// <param name="index">The entry index.</param>
        /// <param name="value">The replacement probability.</param>
        internal void SetResponseProbabilityAt(int index, double value)
        {
            if (_inlineStorage)
            {
                if (!_hasInlineEntry || index != 0) throw new ArgumentOutOfRangeException(nameof(index));
                _inlineResponseProbability = value;
                return;
            }
            _responseProbabilities![index] = value;
        }

        /// <summary>Appends a response-probability / consequence entry.</summary>
        /// <param name="responseProbability">The entry's response probability, P[F|x].</param>
        /// <param name="consequence">The entry's consequence given the response, C(x).</param>
        public void Add(double responseProbability, double consequence)
        {
            double clipped = Tools.Clamp(responseProbability, 0d, 1d);
            if (_inlineStorage && !_hasInlineEntry)
            {
                _inlineResponseProbability = clipped;
                _inlineConsequence = consequence;
                _hasInlineEntry = true;
                return;
            }
            if (_inlineStorage) MaterializeEntryLists();
            _responseProbabilities!.Add(clipped);
            _consequences!.Add(consequence);
        }

        /// <summary>
        /// Computes the point's total probability and expected consequence contributions,
        /// Σ mass·pᵢ and Σ mass·pᵢ·cᵢ over the entries.
        /// </summary>
        /// <param name="totalProbability">Receives the total probability contribution.</param>
        /// <param name="expectedConsequences">Receives the expected consequence contribution.</param>
        public void SummaryStatistics(out double totalProbability, out double expectedConsequences)
        {
            totalProbability = 0d;
            expectedConsequences = 0d;
            for (int i = 0; i < EntryCount; i++)
            {
                double probability = ResponseProbabilityAt(i);
                totalProbability += HazardProbabilityMass * probability;
                expectedConsequences += HazardProbabilityMass * probability * ConsequenceAt(i);
            }
        }

        /// <summary>Materializes the public list surface from the internal single-entry form.</summary>
        private void MaterializeEntryLists()
        {
            var probabilities = new List<double>(1);
            var consequences = new List<double>(1);
            if (_hasInlineEntry)
            {
                probabilities.Add(_inlineResponseProbability);
                consequences.Add(_inlineConsequence);
            }
            _responseProbabilities = probabilities;
            _consequences = consequences;
            _inlineStorage = false;
            _hasInlineEntry = false;
        }
    }
}