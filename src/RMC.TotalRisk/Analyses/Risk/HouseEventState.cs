using System;
using System.Xml.Linq;
using RMC.TotalRisk.Core;

namespace RMC.TotalRisk.Analyses.Risk
{
    /// <summary>
    /// One house-event override of a configuration-risk query: the fault-tree response function,
    /// the house-event node inside it, and the deterministic state the queried configuration
    /// holds it at. Never hashed and never applied to the authored model; the query flips the
    /// state on throwaway clones.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The XML form exists for the cost-benefit study document, whose plans carry these
    /// overrides; persisting one never touches a canonical-hash or seed surface. Element and
    /// attribute names are append-only serialized contract.
    /// </para>
    /// </remarks>
    public sealed class HouseEventState
    {
        /// <summary>
        /// Initializes a house-event override.
        /// </summary>
        /// <param name="functionId">The <see cref="Core.Interfaces.IRiskFunction.Id"/> of the fault-tree response owning the house event.</param>
        /// <param name="nodeId">The persistent id of the house-event node inside that function.</param>
        /// <param name="state">The deterministic Boolean state of the queried configuration.</param>
        /// <exception cref="ArgumentException">Thrown when either id is empty.</exception>
        public HouseEventState(Guid functionId, Guid nodeId, bool state)
        {
            if (functionId == Guid.Empty)
                throw new ArgumentException("The house-event override requires a function id.", nameof(functionId));
            if (nodeId == Guid.Empty)
                throw new ArgumentException("The house-event override requires a node id.", nameof(nodeId));
            FunctionId = functionId;
            NodeId = nodeId;
            State = state;
        }

        /// <summary>
        /// Restores an override from its serialized form.
        /// </summary>
        /// <param name="xElement">The serialized form produced by <see cref="ToXElement"/>.</param>
        /// <exception cref="ArgumentNullException">Thrown when the element is null.</exception>
        /// <exception cref="ArgumentException">Thrown when a stored id is missing or malformed.</exception>
        public HouseEventState(XElement xElement)
            : this(ReadGuid(SerializationUtilities.RequireElement(xElement, nameof(xElement)), nameof(FunctionId)),
                ReadGuid(xElement, nameof(NodeId)),
                SerializationUtilities.ReadBoolean(xElement, nameof(State)))
        {
        }

        /// <summary>
        /// Reads a Guid attribute; a missing or malformed value reads as empty, which the
        /// construction guards refuse.
        /// </summary>
        /// <param name="xElement">The serialized form.</param>
        /// <param name="attributeName">The attribute name.</param>
        /// <returns>The parsed id, or empty.</returns>
        private static Guid ReadGuid(XElement xElement, string attributeName)
        {
            return Guid.TryParse(SerializationUtilities.ReadString(xElement, attributeName), out Guid id)
                ? id
                : Guid.Empty;
        }

        /// <summary>
        /// Serializes the override. Element and attribute names are append-only contract.
        /// </summary>
        /// <returns>The serialized form.</returns>
        public XElement ToXElement()
        {
            var element = new XElement(nameof(HouseEventState));
            element.SetAttributeValue(nameof(FunctionId), FunctionId.ToString("D"));
            element.SetAttributeValue(nameof(NodeId), NodeId.ToString("D"));
            element.SetAttributeValue(nameof(State), State);
            return element;
        }

        /// <summary>
        /// The id of the fault-tree response function owning the house event — rename-proof and
        /// clone-stable.
        /// </summary>
        public Guid FunctionId { get; }

        /// <summary>The persistent id of the house-event node inside the function.</summary>
        public Guid NodeId { get; }

        /// <summary>The deterministic Boolean state the queried configuration holds the event at.</summary>
        public bool State { get; }
    }
}
