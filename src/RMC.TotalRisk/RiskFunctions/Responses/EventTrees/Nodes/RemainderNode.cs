using System;

namespace RMC.TotalRisk.RiskFunctions.Responses.EventTrees.Nodes
{
    /// <summary>
    /// The residual sibling branch whose probability is one minus the normalized explicit sibling
    /// total. At most one remainder may exist under a parent.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// </remarks>
    public sealed class RemainderNode : EventNodeBase
    {
        /// <summary>Initializes a remainder node.</summary>
        /// <param name="name">The display name.</param>
        public RemainderNode(string name = "Remainder") : base(name, false)
        {
        }

        /// <summary>Initializes a restored remainder node.</summary>
        /// <param name="id">The persistent id.</param>
        /// <param name="name">The display name.</param>
        /// <param name="description">The display description.</param>
        /// <param name="isFailure">The terminal classification.</param>
        /// <param name="outputPort">The persistent branch output port.</param>
        internal RemainderNode(Guid id, string name, string description, bool isFailure, int outputPort)
            : base(id, name, description, isFailure, outputPort)
        {
        }

        /// <inheritdoc/>
        internal override string SerializedName => nameof(RemainderNode);
    }
}
