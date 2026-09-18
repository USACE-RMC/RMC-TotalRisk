using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Nodes
{
    /// <summary>
    /// Base class for authored fault-tree nodes. Persistent identity and display metadata are
    /// intentionally separate from the compute-relevant gate, event, and transfer content; the
    /// fault-tree response is binary, so nodes carry no terminal classification or output port.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// </remarks>
    public abstract class FaultTreeNodeBase : INotifyPropertyChanged
    {
        /// <summary>Initializes a new fault-tree node.</summary>
        /// <param name="name">The display name.</param>
        protected FaultTreeNodeBase(string name)
            : this(Guid.NewGuid(), name, string.Empty)
        {
        }

        /// <summary>Initializes a restored fault-tree node.</summary>
        /// <param name="id">The persistent id.</param>
        /// <param name="name">The display name.</param>
        /// <param name="description">The display description.</param>
        /// <exception cref="ArgumentException">Thrown when the id is empty.</exception>
        internal FaultTreeNodeBase(Guid id, string name, string description)
        {
            if (id == Guid.Empty) throw new ArgumentException("A fault-tree node requires a non-empty id.", nameof(id));
            _id = id;
            _name = name ?? string.Empty;
            _description = description ?? string.Empty;
        }

        /// <summary>The persistent node id.</summary>
        private readonly Guid _id;

        /// <summary>The display name.</summary>
        private string _name;

        /// <summary>The display description.</summary>
        private string _description;

        /// <summary>The owning tree, or null before insertion.</summary>
        private FaultTree? _owner;

        /// <summary>The structural parent, or null at the root or before insertion.</summary>
        private FaultTreeNodeBase? _parent;

        /// <summary>The controlled child list.</summary>
        private readonly List<FaultTreeNodeBase> _children = new List<FaultTreeNodeBase>();

        /// <summary>The read-only child-list view.</summary>
        private ReadOnlyCollection<FaultTreeNodeBase>? _readOnlyChildren;

        /// <summary>The persistent node id.</summary>
        public Guid Id => _id;

        /// <summary>The display name. Names are metadata and never affect canonical identity.</summary>
        public string Name
        {
            get { return _name; }
            set
            {
                value ??= string.Empty;
                if (_name == value) return;
                _name = value;
                RaisePropertyChanged(nameof(Name));
            }
        }

        /// <summary>The display description. Descriptions are metadata and never affect identity.</summary>
        public string Description
        {
            get { return _description; }
            set
            {
                value ??= string.Empty;
                if (_description == value) return;
                _description = value;
                RaisePropertyChanged(nameof(Description));
            }
        }

        /// <summary>The structural parent, or null for the top-event root.</summary>
        public FaultTreeNodeBase? Parent => _parent;

        /// <summary>The controlled, persistent-order child view.</summary>
        public IReadOnlyList<FaultTreeNodeBase> Children => _readOnlyChildren ??= _children.AsReadOnly();

        /// <summary>Whether this node currently has no inputs.</summary>
        public bool IsTerminal => _children.Count == 0;

        /// <summary>Occurs when an authored node property changes.</summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>The serialized element name for the concrete node kind.</summary>
        internal abstract string SerializedName { get; }

        /// <summary>The owning tree, or null before insertion.</summary>
        internal FaultTree? Owner => _owner;

        /// <summary>The mutable controlled child list, for <see cref="FaultTree"/> only.</summary>
        internal List<FaultTreeNodeBase> MutableChildren => _children;

        /// <summary>Assigns structural ownership and parentage.</summary>
        /// <param name="owner">The owning tree.</param>
        /// <param name="parent">The structural parent.</param>
        internal void Attach(FaultTree owner, FaultTreeNodeBase? parent)
        {
            _owner = owner;
            _parent = parent;
        }

        /// <summary>Clears structural ownership and parentage.</summary>
        internal void Detach()
        {
            _owner = null;
            _parent = null;
        }

        /// <summary>Raises a passive node change notification.</summary>
        /// <param name="propertyName">The changed property.</param>
        protected void RaisePropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
