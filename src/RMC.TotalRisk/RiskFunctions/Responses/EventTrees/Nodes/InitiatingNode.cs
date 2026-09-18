using System;

namespace RMC.TotalRisk.RiskFunctions.Responses.EventTrees.Nodes
{
    /// <summary>The single structural root of an <see cref="EventTree"/>.</summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// </remarks>
    public sealed class InitiatingNode : EventNodeBase
    {
        /// <summary>Initializes an initiating node.</summary>
        /// <param name="name">The display name.</param>
        public InitiatingNode(string name = "Initiating Event") : base(name, false)
        {
        }

        /// <summary>Initializes a restored initiating node.</summary>
        /// <param name="id">The persistent id.</param>
        /// <param name="name">The display name.</param>
        /// <param name="description">The display description.</param>
        /// <param name="isFailure">The retained terminal classification.</param>
        /// <param name="outputPort">The retained output port; initiating roots use -1.</param>
        internal InitiatingNode(Guid id, string name, string description, bool isFailure, int outputPort)
            : base(id, name, description, isFailure, outputPort)
        {
        }

        /// <inheritdoc/>
        internal override string SerializedName => nameof(InitiatingNode);
    }
}
