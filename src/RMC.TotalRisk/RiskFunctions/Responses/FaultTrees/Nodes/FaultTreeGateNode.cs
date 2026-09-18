using System;

namespace RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Nodes
{
    /// <summary>
    /// A Boolean fault-tree gate whose output combines its ordered inputs. The gate type and the
    /// k-of-n threshold are compute-relevant; input presentation order is metadata because every
    /// supported gate is commutative.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// </remarks>
    public sealed class FaultTreeGateNode : FaultTreeNodeBase
    {
        /// <summary>Initializes a gate node.</summary>
        /// <param name="name">The display name.</param>
        /// <param name="gateType">The Boolean combination.</param>
        /// <param name="k">The k-of-n threshold, used only when the type is <see cref="FaultTreeGateType.KOfN"/>.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the gate type is undefined.</exception>
        public FaultTreeGateNode(string name, FaultTreeGateType gateType, int k = 0) : base(name)
        {
            if (!Enum.IsDefined(gateType)) throw new ArgumentOutOfRangeException(nameof(gateType));
            _gateType = gateType;
            _k = k;
        }

        /// <summary>Initializes a restored gate node.</summary>
        /// <param name="id">The persistent id.</param>
        /// <param name="name">The display name.</param>
        /// <param name="description">The display description.</param>
        /// <param name="gateType">The Boolean combination.</param>
        /// <param name="k">The k-of-n threshold.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the gate type is undefined.</exception>
        internal FaultTreeGateNode(Guid id, string name, string description,
            FaultTreeGateType gateType, int k)
            : base(id, name, description)
        {
            if (!Enum.IsDefined(gateType)) throw new ArgumentOutOfRangeException(nameof(gateType));
            _gateType = gateType;
            _k = k;
        }

        /// <summary>The Boolean combination backing field.</summary>
        private FaultTreeGateType _gateType;

        /// <summary>The k-of-n threshold backing field.</summary>
        private int _k;

        /// <summary>The Boolean combination applied to the gate inputs.</summary>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is undefined.</exception>
        public FaultTreeGateType GateType
        {
            get { return _gateType; }
            set
            {
                if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
                if (_gateType == value) return;
                _gateType = value;
                RaisePropertyChanged(nameof(GateType));
            }
        }

        /// <summary>
        /// The k-of-n threshold. Read only when <see cref="GateType"/> is
        /// <see cref="FaultTreeGateType.KOfN"/>; validation requires it to lie between one and the
        /// input count.
        /// </summary>
        public int K
        {
            get { return _k; }
            set
            {
                if (_k == value) return;
                _k = value;
                RaisePropertyChanged(nameof(K));
            }
        }

        /// <inheritdoc/>
        internal override string SerializedName => nameof(FaultTreeGateNode);
    }
}
