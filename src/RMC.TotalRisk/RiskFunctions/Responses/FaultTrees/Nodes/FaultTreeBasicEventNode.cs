using System;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;

namespace RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Nodes
{
    /// <summary>
    /// A Boolean basic event whose conditional probability comes from a
    /// <see cref="Trees.ProbabilitySource"/>: a fixed scalar, an uncertain table aligned to the
    /// owning tree hazards, or a referenced response function.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// </remarks>
    public sealed class FaultTreeBasicEventNode : FaultTreeNodeBase
    {
        /// <summary>Initializes a basic event.</summary>
        /// <param name="name">The display name.</param>
        /// <param name="probabilitySource">The conditional-probability source.</param>
        /// <exception cref="ArgumentNullException">Thrown when the source is null.</exception>
        public FaultTreeBasicEventNode(string name, ProbabilitySource probabilitySource) : base(name)
        {
            _probabilitySource = probabilitySource ?? throw new ArgumentNullException(nameof(probabilitySource));
        }

        /// <summary>Initializes a restored basic event.</summary>
        /// <param name="id">The persistent id.</param>
        /// <param name="name">The display name.</param>
        /// <param name="description">The display description.</param>
        /// <param name="probabilitySource">The conditional-probability source.</param>
        internal FaultTreeBasicEventNode(Guid id, string name, string description,
            ProbabilitySource probabilitySource)
            : base(id, name, description)
        {
            _probabilitySource = probabilitySource ?? throw new ArgumentNullException(nameof(probabilitySource));
        }

        /// <summary>The probability-source backing field.</summary>
        private ProbabilitySource _probabilitySource;

        /// <summary>The conditional-probability source.</summary>
        /// <exception cref="ArgumentNullException">Thrown when the value is null.</exception>
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
        internal override string SerializedName => nameof(FaultTreeBasicEventNode);
    }
}
