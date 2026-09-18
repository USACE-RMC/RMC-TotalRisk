using System;

namespace RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Nodes
{
    /// <summary>
    /// A deterministic house event: a Boolean constant folded into the compiled diagram before
    /// any expansion. The state is compute-relevant.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// </remarks>
    public sealed class FaultTreeHouseEventNode : FaultTreeNodeBase
    {
        /// <summary>Initializes a house event.</summary>
        /// <param name="name">The display name.</param>
        /// <param name="state">The deterministic Boolean state.</param>
        public FaultTreeHouseEventNode(string name, bool state) : base(name)
        {
            _state = state;
        }

        /// <summary>Initializes a restored house event.</summary>
        /// <param name="id">The persistent id.</param>
        /// <param name="name">The display name.</param>
        /// <param name="description">The display description.</param>
        /// <param name="state">The deterministic Boolean state.</param>
        internal FaultTreeHouseEventNode(Guid id, string name, string description, bool state)
            : base(id, name, description)
        {
            _state = state;
        }

        /// <summary>The deterministic-state backing field.</summary>
        private bool _state;

        /// <summary>The deterministic Boolean state.</summary>
        public bool State
        {
            get { return _state; }
            set
            {
                if (_state == value) return;
                _state = value;
                RaisePropertyChanged(nameof(State));
            }
        }

        /// <inheritdoc/>
        internal override string SerializedName => nameof(FaultTreeHouseEventNode);
    }
}
