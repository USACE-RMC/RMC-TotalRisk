using System;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;

namespace RMC.TotalRisk.RiskFunctions.Responses.EventTrees.Nodes
{
    /// <summary>An explicit conditional event-tree branch backed by a probability source.</summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// </remarks>
    public sealed class ChanceNode : EventNodeBase
    {
        /// <summary>Initializes a chance node.</summary>
        /// <param name="name">The display name.</param>
        /// <param name="probabilitySource">The raw conditional-probability source.</param>
        /// <exception cref="ArgumentNullException">Thrown when the source is null.</exception>
        public ChanceNode(string name, ProbabilitySource probabilitySource) : base(name, true)
        {
            _probabilitySource = probabilitySource ?? throw new ArgumentNullException(nameof(probabilitySource));
        }

        /// <summary>Initializes a restored chance node.</summary>
        /// <param name="id">The persistent id.</param>
        /// <param name="name">The display name.</param>
        /// <param name="description">The display description.</param>
        /// <param name="isFailure">The terminal classification.</param>
        /// <param name="probabilitySource">The raw probability source.</param>
        /// <param name="outputPort">The persistent branch output port.</param>
        internal ChanceNode(Guid id, string name, string description, bool isFailure, int outputPort, ProbabilitySource probabilitySource)
            : base(id, name, description, isFailure, outputPort)
        {
            _probabilitySource = probabilitySource ?? throw new ArgumentNullException(nameof(probabilitySource));
        }

        /// <summary>The probability-source backing field.</summary>
        private ProbabilitySource _probabilitySource;

        /// <summary>The raw conditional-probability source.</summary>
        public ProbabilitySource ProbabilitySource
        {
            get { return _probabilitySource; }
            set
            {
                if (value == null) throw new ArgumentNullException(nameof(value));
                if (ReferenceEquals(_probabilitySource, value)) return;
                _probabilitySource = value;
                RaisePropertyChanged(nameof(ProbabilitySource));
            }
        }

        /// <inheritdoc/>
        internal override string SerializedName => nameof(ChanceNode);
    }
}
