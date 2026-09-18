using System;
using System.Xml.Linq;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.RiskFunctions;

namespace RMC.TotalRisk.Analyses.LifeCycle
{
    /// <summary>
    /// One hazard-replacement action of a life-cycle intervention: from its year onward, every
    /// component hazard element whose assigned function carries the target id evaluates the
    /// replacement function instead — the seat a nonstationary per-epoch hazard fit occupies.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Never hashed and never applied to the authored model; the life-cycle query assigns a
    /// factory clone of the replacement to throwaway component clones, so the authored instance
    /// is never wired into query structures. The replacement must match the replaced hazard's
    /// arity (univariate for univariate, bivariate for bivariate); the query refuses a mismatch
    /// loudly.
    /// </para>
    /// <para>
    /// The XML form exists for the cost-benefit study document, whose plans carry these actions;
    /// the replacement function writes self-contained as the single child, and persisting one
    /// never touches a canonical-hash or seed surface. Element and attribute names are
    /// append-only serialized contract.
    /// </para>
    /// </remarks>
    public sealed class HazardReplacement
    {
        /// <summary>
        /// Initializes one hazard-replacement action.
        /// </summary>
        /// <param name="targetFunctionId">The id of the hazard function being replaced.</param>
        /// <param name="replacement">The already-authored hazard function that takes its place.</param>
        /// <exception cref="ArgumentException">Thrown when the target id is empty.</exception>
        /// <exception cref="ArgumentNullException">Thrown when the replacement is null.</exception>
        public HazardReplacement(Guid targetFunctionId, IHazardFunction replacement)
        {
            if (targetFunctionId == Guid.Empty)
                throw new ArgumentException("The hazard replacement requires a target function id.", nameof(targetFunctionId));
            TargetFunctionId = targetFunctionId;
            Replacement = replacement ?? throw new ArgumentNullException(nameof(replacement));
        }

        /// <summary>
        /// Restores an action from its serialized form: the target id attribute plus the
        /// self-contained replacement function as the single child element.
        /// </summary>
        /// <param name="xElement">The serialized form produced by <see cref="ToXElement"/>.</param>
        /// <exception cref="ArgumentNullException">Thrown when the element is null.</exception>
        /// <exception cref="ArgumentException">
        /// Thrown when the target id is missing or malformed, or when the child names no known
        /// hazard function.
        /// </exception>
        public HazardReplacement(XElement xElement)
            : this(ReadTargetId(SerializationUtilities.RequireElement(xElement, nameof(xElement))),
                ReadReplacement(xElement))
        {
        }

        /// <summary>
        /// Reads the target id; a missing or malformed value reads as empty, which the
        /// construction guards refuse.
        /// </summary>
        /// <param name="xElement">The serialized form.</param>
        /// <returns>The parsed id, or empty.</returns>
        private static Guid ReadTargetId(XElement xElement)
        {
            return Guid.TryParse(SerializationUtilities.ReadString(xElement, nameof(TargetFunctionId)), out Guid id)
                ? id
                : Guid.Empty;
        }

        /// <summary>
        /// Reads the self-contained replacement function from the single child element.
        /// </summary>
        /// <param name="xElement">The serialized form.</param>
        /// <returns>The reconstructed hazard function.</returns>
        /// <exception cref="ArgumentException">Thrown when the child is missing or names no known hazard function.</exception>
        private static IHazardFunction ReadReplacement(XElement xElement)
        {
            XElement? child = null;
            foreach (XElement candidate in xElement.Elements())
            {
                child = candidate;
                break;
            }
            IHazardFunction? replacement = child == null ? null : RiskFunctionFactory.CreateHazardFunction(child);
            return replacement
                ?? throw new ArgumentException(
                    "The hazard replacement's child element names no known hazard function.", nameof(xElement));
        }

        /// <summary>
        /// Serializes the action: the target id attribute plus the replacement function written
        /// self-contained as the single child. Element and attribute names are append-only
        /// contract.
        /// </summary>
        /// <returns>The serialized form.</returns>
        public XElement ToXElement()
        {
            var element = new XElement(nameof(HazardReplacement));
            element.SetAttributeValue(nameof(TargetFunctionId), TargetFunctionId.ToString("D"));
            element.Add(Replacement.ToXElement());
            return element;
        }

        /// <summary>
        /// The id of the hazard function being replaced — matched against the live element
        /// assignment at the intervention's year, so chained replacements target the function in
        /// service, not the original.
        /// </summary>
        public Guid TargetFunctionId { get; }

        /// <summary>
        /// The already-authored hazard function that takes the target's place. Referenced, not
        /// owned: the query clones it per epoch at application.
        /// </summary>
        public IHazardFunction Replacement { get; }
    }
}
