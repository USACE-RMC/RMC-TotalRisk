using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Analyses.Risk;

namespace RMC.TotalRisk.Analyses.LifeCycle
{
    /// <summary>
    /// One scheduled intervention of a life-cycle evaluation: from its year onward the named house
    /// events hold their configured states and the named hazard replacements are in service —
    /// "fix failure mode A now, replace the hazard in year 20."
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Never hashed and never applied to the authored model; the life-cycle query applies the
    /// cumulative schedule to throwaway component clones per epoch. Interventions accumulate: a
    /// state configured at one year persists until a later intervention overrides the same house
    /// event (re-exercise, including reversal, is legal across years; within one intervention
    /// each house event and each replacement target appears at most once). Year zero means
    /// "now" — applied before the first exposure year. The XML form exists for the cost-benefit
    /// study document, whose plans carry these entries; persisting one never touches a
    /// canonical-hash or seed surface, and element and attribute names are append-only
    /// serialized contract.
    /// </para>
    /// <para>
    /// Entries are unconditional exercise plans. A future condition member gating exercise on the
    /// state observed at the intervention's year — the decision-rule reading real-options
    /// valuation needs, estimated by techniques such as least-squares Monte Carlo — is the named
    /// extension seat of this type; until it exists, alternatives are compared as deterministic
    /// schedules.
    /// </para>
    /// </remarks>
    public sealed class LifeCycleIntervention
    {
        /// <summary>
        /// Initializes one scheduled intervention.
        /// </summary>
        /// <param name="year">The year the intervention takes effect (0 = now), an offset from the start of the horizon.</param>
        /// <param name="houseEvents">The house-event states held from the year onward; null or empty for none.</param>
        /// <param name="hazardReplacements">The hazard replacements in service from the year onward; null or empty for none.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the year is negative.</exception>
        /// <exception cref="ArgumentException">
        /// Thrown when no action is supplied, when an action entry is null, when a house event is
        /// addressed more than once, or when a replacement target is addressed more than once.
        /// </exception>
        public LifeCycleIntervention(int year,
            IReadOnlyList<HouseEventState>? houseEvents = null,
            IReadOnlyList<HazardReplacement>? hazardReplacements = null)
        {
            if (year < 0)
                throw new ArgumentOutOfRangeException(nameof(year), "The intervention year must be non-negative.");
            Year = year;

            var houseSnapshot = houseEvents == null ? Array.Empty<HouseEventState>() : houseEvents.ToArray();
            var replacementSnapshot = hazardReplacements == null
                ? Array.Empty<HazardReplacement>()
                : hazardReplacements.ToArray();
            if (houseSnapshot.Length == 0 && replacementSnapshot.Length == 0)
                throw new ArgumentException("The intervention requires at least one action.", nameof(houseEvents));

            var seenHouse = new HashSet<(Guid FunctionId, Guid NodeId)>();
            for (int i = 0; i < houseSnapshot.Length; i++)
            {
                var state = houseSnapshot[i]
                    ?? throw new ArgumentException("The house-event list contains a null entry.", nameof(houseEvents));
                if (!seenHouse.Add((state.FunctionId, state.NodeId)))
                    throw new ArgumentException(
                        $"The intervention addresses house event '{state.NodeId:D}' of function '{state.FunctionId:D}' more than once.",
                        nameof(houseEvents));
            }

            var seenTargets = new HashSet<Guid>();
            for (int i = 0; i < replacementSnapshot.Length; i++)
            {
                var replacement = replacementSnapshot[i]
                    ?? throw new ArgumentException("The hazard-replacement list contains a null entry.", nameof(hazardReplacements));
                if (!seenTargets.Add(replacement.TargetFunctionId))
                    throw new ArgumentException(
                        $"The intervention replaces hazard function '{replacement.TargetFunctionId:D}' more than once.",
                        nameof(hazardReplacements));
            }

            HouseEvents = Array.AsReadOnly(houseSnapshot);
            HazardReplacements = Array.AsReadOnly(replacementSnapshot);
        }

        /// <summary>
        /// Restores an intervention from its serialized form: the year attribute plus the
        /// house-event and hazard-replacement children.
        /// </summary>
        /// <param name="xElement">The serialized form produced by <see cref="ToXElement"/>.</param>
        /// <exception cref="ArgumentNullException">Thrown when the element is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the stored year is negative.</exception>
        /// <exception cref="ArgumentException">Thrown when the stored actions violate the construction guards.</exception>
        public LifeCycleIntervention(XElement xElement)
            : this(SerializationUtilities.ReadInt32(SerializationUtilities.RequireElement(xElement, nameof(xElement)), nameof(Year)),
                ReadHouseEvents(xElement),
                ReadReplacements(xElement))
        {
        }

        /// <summary>
        /// Reads the house-event overrides from the serialized form.
        /// </summary>
        /// <param name="xElement">The serialized form.</param>
        /// <returns>The overrides.</returns>
        private static IReadOnlyList<HouseEventState> ReadHouseEvents(XElement xElement)
        {
            var states = new List<HouseEventState>();
            foreach (XElement child in xElement.Elements(nameof(HouseEventState)))
            {
                states.Add(new HouseEventState(child));
            }
            return states;
        }

        /// <summary>
        /// Reads the hazard-replacement actions from the serialized form.
        /// </summary>
        /// <param name="xElement">The serialized form.</param>
        /// <returns>The actions.</returns>
        private static IReadOnlyList<HazardReplacement> ReadReplacements(XElement xElement)
        {
            var replacements = new List<HazardReplacement>();
            foreach (XElement child in xElement.Elements(nameof(HazardReplacement)))
            {
                replacements.Add(new HazardReplacement(child));
            }
            return replacements;
        }

        /// <summary>
        /// Serializes the intervention: the year attribute plus one child per house-event
        /// override and hazard replacement. Element and attribute names are append-only contract.
        /// </summary>
        /// <returns>The serialized form.</returns>
        public XElement ToXElement()
        {
            var element = new XElement(nameof(LifeCycleIntervention));
            element.SetAttributeValue(nameof(Year), Year);
            for (int i = 0; i < HouseEvents.Count; i++)
            {
                element.Add(HouseEvents[i].ToXElement());
            }
            for (int i = 0; i < HazardReplacements.Count; i++)
            {
                element.Add(HazardReplacements[i].ToXElement());
            }
            return element;
        }

        /// <summary>
        /// The year the intervention takes effect, an offset from the start of the horizon
        /// (0 = now, before the first exposure year).
        /// </summary>
        public int Year { get; }

        /// <summary>
        /// The house-event states held from <see cref="Year"/> onward; empty when the intervention
        /// carries only replacements.
        /// </summary>
        public IReadOnlyList<HouseEventState> HouseEvents { get; }

        /// <summary>
        /// The hazard replacements in service from <see cref="Year"/> onward; empty when the
        /// intervention carries only house events.
        /// </summary>
        public IReadOnlyList<HazardReplacement> HazardReplacements { get; }
    }
}
