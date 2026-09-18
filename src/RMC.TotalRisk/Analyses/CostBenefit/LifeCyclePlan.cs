using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Analyses.LifeCycle;

namespace RMC.TotalRisk.Analyses.CostBenefit
{
    /// <summary>
    /// A risk-reduction alternative's staged plan: the intervention schedule and any extra
    /// evaluation years it contributes to the study-wide epoch grid. Plans carry no discount
    /// rate and no horizon — the study owns both, so a mismatch is unrepresentable.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The plan is immutable (replace it to edit). Duplicate intervention years are refused —
    /// one entry carries all of a year's actions, the life-cycle definition's rule — while
    /// year bounds against the study horizon are validated at the study level, where the
    /// horizon lives. The plan serializes its interventions inline (house-event and
    /// self-contained hazard-replacement payloads); persisting one never touches a
    /// canonical-hash or seed surface. Element and attribute names are append-only serialized
    /// contract.
    /// </para>
    /// </remarks>
    public sealed class LifeCyclePlan
    {
        #region Construction

        /// <summary>
        /// Initializes a staged plan.
        /// </summary>
        /// <param name="interventions">The intervention schedule; empty is legal (an evaluation-years-only plan).</param>
        /// <param name="evaluationYears">Extra epoch start years the plan contributes; null or empty for none.</param>
        /// <exception cref="ArgumentNullException">Thrown when the intervention list is null.</exception>
        /// <exception cref="ArgumentException">
        /// Thrown when the intervention list contains a null entry or two interventions share
        /// a year.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when an evaluation year is negative.</exception>
        public LifeCyclePlan(IReadOnlyList<LifeCycleIntervention> interventions,
            IReadOnlyList<int>? evaluationYears = null)
        {
            if (interventions == null) throw new ArgumentNullException(nameof(interventions));
            var interventionSnapshot = interventions.ToArray();
            var seenYears = new HashSet<int>();
            for (int i = 0; i < interventionSnapshot.Length; i++)
            {
                var intervention = interventionSnapshot[i]
                    ?? throw new ArgumentException("The intervention list contains a null entry.", nameof(interventions));
                if (!seenYears.Add(intervention.Year))
                    throw new ArgumentException(
                        $"Two interventions share year {intervention.Year}; carry all of a year's actions on one intervention entry.",
                        nameof(interventions));
            }

            var yearSnapshot = evaluationYears == null ? Array.Empty<int>() : evaluationYears.ToArray();
            for (int i = 0; i < yearSnapshot.Length; i++)
            {
                if (yearSnapshot[i] < 0)
                    throw new ArgumentOutOfRangeException(nameof(evaluationYears),
                        "The plan's evaluation years must not be negative.");
            }

            Interventions = Array.AsReadOnly(interventionSnapshot);
            EvaluationYears = Array.AsReadOnly(yearSnapshot);
        }

        /// <summary>
        /// Restores a plan from its serialized form.
        /// </summary>
        /// <param name="xElement">The serialized form produced by <see cref="ToXElement"/>.</param>
        /// <exception cref="ArgumentNullException">Thrown when the element is null.</exception>
        /// <exception cref="ArgumentException">Thrown when the stored payload violates the construction guards.</exception>
        public LifeCyclePlan(XElement xElement)
            : this(ReadInterventions(SerializationUtilities.RequireElement(xElement, nameof(xElement))),
                ReadEvaluationYears(xElement))
        {
        }

        /// <summary>
        /// Reads the interventions from the serialized form.
        /// </summary>
        /// <param name="xElement">The serialized form.</param>
        /// <returns>The interventions.</returns>
        private static IReadOnlyList<LifeCycleIntervention> ReadInterventions(XElement xElement)
        {
            var interventions = new List<LifeCycleIntervention>();
            foreach (XElement child in xElement.Elements(nameof(LifeCycleIntervention)))
            {
                interventions.Add(new LifeCycleIntervention(child));
            }
            return interventions;
        }

        /// <summary>
        /// Reads the pipe-delimited evaluation years from the serialized form.
        /// </summary>
        /// <param name="xElement">The serialized form.</param>
        /// <returns>The evaluation years.</returns>
        private static IReadOnlyList<int> ReadEvaluationYears(XElement xElement)
        {
            string text = SerializationUtilities.ReadString(xElement, nameof(EvaluationYears));
            if (string.IsNullOrEmpty(text)) return Array.Empty<int>();
            string[] parts = text.Split('|', StringSplitOptions.RemoveEmptyEntries);
            var years = new List<int>(parts.Length);
            for (int i = 0; i < parts.Length; i++)
            {
                if (int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int year))
                {
                    years.Add(year);
                }
            }
            return years;
        }

        #endregion

        #region Members

        /// <summary>
        /// The intervention schedule, at most one entry per year.
        /// </summary>
        public IReadOnlyList<LifeCycleIntervention> Interventions { get; }

        /// <summary>
        /// Extra epoch start years the plan contributes to the study-wide grid.
        /// </summary>
        public IReadOnlyList<int> EvaluationYears { get; }

        #endregion

        #region Serialization

        /// <summary>
        /// Serializes the plan: pipe-delimited evaluation years plus one child per
        /// intervention. Element and attribute names are append-only contract.
        /// </summary>
        /// <returns>The serialized form.</returns>
        public XElement ToXElement()
        {
            var element = new XElement(nameof(LifeCyclePlan));
            string[] years = new string[EvaluationYears.Count];
            for (int i = 0; i < EvaluationYears.Count; i++)
            {
                years[i] = EvaluationYears[i].ToString(CultureInfo.InvariantCulture);
            }
            element.SetAttributeValue(nameof(EvaluationYears), string.Join("|", years));
            for (int i = 0; i < Interventions.Count; i++)
            {
                element.Add(Interventions[i].ToXElement());
            }
            return element;
        }

        #endregion
    }
}
