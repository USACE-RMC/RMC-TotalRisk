using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace RMC.TotalRisk.RiskFunctions.Responses.EventTrees.Nodes
{
    /// <summary>
    /// Base class for authored event-tree nodes. Persistent identity and display metadata are
    /// intentionally separate from the compute-relevant node kind, probability, and terminal
    /// classification.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// </remarks>
    public abstract class EventNodeBase : INotifyPropertyChanged
    {
        /// <summary>Initializes a new event node.</summary>
        /// <param name="name">The display name.</param>
        /// <param name="defaultIsFailure">The terminal-classification default for the node kind.</param>
        protected EventNodeBase(string name, bool defaultIsFailure)
            : this(Guid.NewGuid(), name, string.Empty, defaultIsFailure, -1)
        {
        }

        /// <summary>Initializes a restored event node.</summary>
        /// <param name="id">The persistent id.</param>
        /// <param name="name">The display name.</param>
        /// <param name="description">The display description.</param>
        /// <param name="isFailure">The terminal classification.</param>
        /// <param name="outputPort">The persistent branch output port, or -1 before assignment.</param>
        /// <exception cref="ArgumentException">Thrown when the id is empty.</exception>
        internal EventNodeBase(Guid id, string name, string description, bool isFailure, int outputPort)
        {
            if (id == Guid.Empty) throw new ArgumentException("An event node requires a non-empty id.", nameof(id));
            _id = id;
            _name = name ?? string.Empty;
            _description = description ?? string.Empty;
            _isFailure = isFailure;
            _outputPort = outputPort;
        }

        /// <summary>The persistent node id.</summary>
        private readonly Guid _id;

        /// <summary>The display name.</summary>
        private string _name;

        /// <summary>The display description.</summary>
        private string _description;

        /// <summary>The terminal failure classification.</summary>
        private bool _isFailure;

        /// <summary>The persistent branch output port, or -1 before tree assignment.</summary>
        private int _outputPort;

        /// <summary>The owning tree, or null before insertion.</summary>
        private EventTree? _owner;

        /// <summary>The structural parent, or null at the root or before insertion.</summary>
        private EventNodeBase? _parent;

        /// <summary>The controlled child list.</summary>
        private readonly List<EventNodeBase> _children = new List<EventNodeBase>();

        /// <summary>The read-only child-list view.</summary>
        private ReadOnlyCollection<EventNodeBase>? _readOnlyChildren;

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

        /// <summary>
        /// Whether this node contributes to aggregate failure when it is terminal. The value is
        /// retained but compute-inert while the node has children.
        /// </summary>
        public bool IsFailure
        {
            get { return _isFailure; }
            set
            {
                if (_isFailure == value) return;
                _isFailure = value;
                RaisePropertyChanged(nameof(IsFailure));
            }
        }

        /// <summary>The persistent output port used when this node is a terminal branch.</summary>
        public int OutputPort => _outputPort;

        /// <summary>The structural parent, or null for the initiating root.</summary>
        public EventNodeBase? Parent => _parent;

        /// <summary>The controlled, persistent-order child view.</summary>
        public IReadOnlyList<EventNodeBase> Children => _readOnlyChildren ??= _children.AsReadOnly();

        /// <summary>Whether this node is currently terminal.</summary>
        public bool IsTerminal => _children.Count == 0;

        /// <summary>Occurs when an authored node property changes.</summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>The serialized element name for the concrete node kind.</summary>
        internal abstract string SerializedName { get; }

        /// <summary>The owning tree, or null before insertion.</summary>
        internal EventTree? Owner => _owner;

        /// <summary>The mutable controlled child list, for <see cref="EventTree"/> only.</summary>
        internal List<EventNodeBase> MutableChildren => _children;

        /// <summary>Assigns structural ownership and parentage.</summary>
        /// <summary>Assigns a previously unassigned persistent branch output port.</summary>
        /// <param name="outputPort">The port, reserved above aggregate and implicit ports 0–2.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the port is below three.</exception>
        internal void AssignOutputPort(int outputPort)
        {
            if (outputPort < 3) throw new ArgumentOutOfRangeException(nameof(outputPort));
            _outputPort = outputPort;
        }

        /// <param name="owner">The owning tree.</param>
        /// <param name="parent">The structural parent.</param>
        internal void Attach(EventTree owner, EventNodeBase? parent)
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
