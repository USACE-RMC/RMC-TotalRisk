using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Xml.Linq;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.RiskFunctions;
using RMC.TotalRisk.RiskFunctions.Support;

namespace RMC.TotalRisk.Systems.Components.Graph
{
    /// <summary>
    /// The abstract base of every risk-graph element: persistent identity, display metadata,
    /// canvas position, change notification, the graph name-authority hook, and the shared
    /// serialization and connection helpers.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Mirrors the Hydrologics <c>BasinElement</c> base. Connection attributes follow one uniform
    /// naming rule per connection kind — <c>{kind}ElementId</c> (Guid "D"), <c>{kind}Element</c>
    /// (name fallback), <c>{kind}Port</c> (int, default 0) — with kinds <c>Source</c> (the
    /// primary input), <c>SecondarySource</c> (reserved for bivariate responses), and
    /// <c>HazardSource</c> (the consequence binding override). These names are append-only
    /// serialized contract from day one.
    /// </para>
    /// <para>
    /// Cloning copies identity (clones share the <see cref="Id"/> — a clone is the same logical
    /// element in an isolated graph copy) and deep-copies wrapped functions via the serialization
    /// round-trip, but never copies connections: the cloning graph re-links every clone through
    /// <see cref="ResolveClonedConnections"/> with its original→clone map.
    /// </para>
    /// </remarks>
    public abstract class RiskElementBase : IRiskElement
    {
        #region Members

        /// <summary>
        /// Backing field for <see cref="Id"/>.
        /// </summary>
        private Guid _id = Guid.NewGuid();

        /// <summary>
        /// Backing field for <see cref="Name"/>.
        /// </summary>
        private string _name = string.Empty;

        /// <summary>
        /// Backing field for <see cref="Description"/>.
        /// </summary>
        private string _description = string.Empty;

        /// <summary>
        /// Backing field for <see cref="LeftPosition"/>.
        /// </summary>
        private double _leftPosition;

        /// <summary>
        /// Backing field for <see cref="TopPosition"/>.
        /// </summary>
        private double _topPosition;

        /// <summary>
        /// Descriptions of serialized function references that could not be resolved at
        /// construction. Reported by <see cref="Validate"/>; empty for self-contained forms.
        /// </summary>
        private readonly List<string> _unresolvedFunctionReferences = new List<string>();

        /// <summary>
        /// The name-uniqueness authority the owning graph attaches when the element is added, and
        /// detaches on removal. Null while detached — renames are then unrestricted.
        /// </summary>
        internal IRiskElementNameAuthority? NameAuthority { get; set; }

        /// <inheritdoc/>
        public Guid Id
        {
            get { return _id; }
        }

        /// <inheritdoc/>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the element is attached to a graph and another element already holds the
        /// name. Use the graph's <c>TryRenameElement</c> for a non-throwing rename.
        /// </exception>
        public string Name
        {
            get { return _name; }
            set
            {
                string coerced = value ?? string.Empty;
                if (string.Equals(_name, coerced, StringComparison.Ordinal)) return;
                if (NameAuthority != null && !NameAuthority.IsNameAvailable(coerced, this))
                {
                    throw new InvalidOperationException($"An element named '{coerced}' already exists in the graph.");
                }
                _name = coerced;
                RaisePropertyChange(nameof(Name));
            }
        }

        /// <inheritdoc/>
        public string Description
        {
            get { return _description; }
            set
            {
                string coerced = value ?? string.Empty;
                if (!string.Equals(_description, coerced, StringComparison.Ordinal))
                {
                    _description = coerced;
                    RaisePropertyChange(nameof(Description));
                }
            }
        }

        /// <inheritdoc/>
        public double LeftPosition
        {
            get { return _leftPosition; }
            set
            {
                if (_leftPosition != value)
                {
                    _leftPosition = value;
                    RaisePropertyChange(nameof(LeftPosition));
                }
            }
        }

        /// <inheritdoc/>
        public double TopPosition
        {
            get { return _topPosition; }
            set
            {
                if (_topPosition != value)
                {
                    _topPosition = value;
                    RaisePropertyChange(nameof(TopPosition));
                }
            }
        }

        /// <inheritdoc/>
        public abstract RiskElementType ElementType { get; }

        /// <inheritdoc/>
        public abstract int InputCount { get; }

        /// <inheritdoc/>
        public abstract int OutputCount { get; }

        /// <summary>
        /// Raised when an element property changes. Passive contract — headless callers need not
        /// subscribe.
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        #endregion

        #region IRiskElement Methods

        /// <inheritdoc/>
        public void AssignNewId()
        {
            _id = Guid.NewGuid();
            RaisePropertyChange(nameof(Id));
        }

        /// <inheritdoc/>
        public abstract IEnumerable<RiskConnection> GetInputConnections();

        /// <inheritdoc/>
        public abstract IEnumerable<IRiskFunction> GetFunctions();

        /// <inheritdoc/>
        public abstract bool TryAssignFunction(IRiskFunction function, out string error);

        /// <summary>
        /// The shared mismatch message for <see cref="TryAssignFunction"/>: names the offered
        /// function, the element, and the cluster the element expects.
        /// </summary>
        /// <param name="function">The offered function.</param>
        /// <param name="expectedCluster">The cluster this element accepts, e.g. "a hazard function".</param>
        /// <returns>The message.</returns>
        protected string FunctionMismatchMessage(IRiskFunction function, string expectedCluster)
        {
            return $"'{function.Name}' is a {function.GetType().Name}; the {GetType().Name} '{_name}' takes {expectedCluster}.";
        }

        /// <inheritdoc/>
        public virtual (bool IsValid, List<string> ValidationMessages) Validate()
        {
            var messages = new List<string>();
            if (string.IsNullOrEmpty(_name))
            {
                messages.Add($"Error: The {GetType().Name} does not have a name.");
            }
            for (int i = 0; i < _unresolvedFunctionReferences.Count; i++)
            {
                messages.Add($"Error: The {GetType().Name} '{_name}' references {_unresolvedFunctionReferences[i]}, which was not found.");
            }
            return (messages.FindIndex(m => m.StartsWith("Error:", StringComparison.Ordinal)) < 0, messages);
        }

        /// <inheritdoc/>
        public XElement ToXElement()
        {
            return ToXElement(RiskSerializationMode.SelfContained);
        }

        /// <inheritdoc/>
        public abstract XElement ToXElement(RiskSerializationMode mode);

        /// <summary>
        /// Creates a deep copy of the element: shared <see cref="Id"/>, copied metadata, wrapped
        /// functions deep-copied via the serialization round-trip, connections NOT copied (the
        /// cloning graph re-links via <see cref="ResolveClonedConnections"/>).
        /// </summary>
        /// <returns>The copied element.</returns>
        public abstract object Clone();

        /// <summary>
        /// Resolves this element's pending serialized connection references against the restored
        /// graph. Called by the graph constructor after every element has been constructed. The
        /// base implementation is a no-op (hazard elements have no connections).
        /// </summary>
        /// <param name="resolver">The resolver over the restored graph.</param>
        public virtual void ResolveDeserializedReferences(RiskElementResolver resolver)
        {
        }

        /// <summary>
        /// Re-links this clone's connections from the original element through the original→clone
        /// map. Called by the cloning graph on each clone. A source absent from the map is kept
        /// as the original reference — graph validation then reports it as a dangling connection.
        /// The base implementation is a no-op.
        /// </summary>
        /// <param name="original">The element this instance was cloned from.</param>
        /// <param name="cloneMap">The original→clone map covering the cloned graph.</param>
        public virtual void ResolveClonedConnections(IRiskElement original, IReadOnlyDictionary<IRiskElement, IRiskElement> cloneMap)
        {
        }

        #endregion

        #region Protected Helpers

        /// <summary>
        /// The element name marking a serialized function reference, as opposed to inline function
        /// content. Serialized contract, owned by <see cref="FunctionEntry"/> (the single
        /// implementation shared with function containers outside the graph).
        /// </summary>
        protected const string FunctionReferenceElementName = FunctionEntry.ReferenceElementName;

        /// <summary>
        /// The name of the property holding this element's wrapped function(s) — the property
        /// reported when a wrapped function's own contents change. "Function" for the
        /// single-function elements; overridden by elements that hold several.
        /// </summary>
        protected virtual string FunctionPropertyName
        {
            get { return "Function"; }
        }

        /// <summary>
        /// Swaps the wrapped-function subscription from one function to another and returns the
        /// replacement, so a setter reads
        /// <c>_function = SwapFunctionSubscription(_function, value)</c>.
        /// </summary>
        /// <typeparam name="T">The cluster interface of the wrapped function.</typeparam>
        /// <param name="current">The currently wrapped function; may be null.</param>
        /// <param name="replacement">The replacement; may be null.</param>
        /// <returns>The replacement, for direct assignment to the backing field.</returns>
        /// <remarks>
        /// An element does not own the functions it wraps — a consuming layer may store one
        /// function and use it in several graphs. Forwarding the function's own change
        /// notification is what lets an edit made where the function is stored reach the analyses
        /// that consume it, instead of leaving them silently stale.
        /// </remarks>
        protected T? SwapFunctionSubscription<T>(T? current, T? replacement) where T : class, IRiskFunction
        {
            if (ReferenceEquals(current, replacement)) return replacement;
            if (current != null) current.PropertyChanged -= WrappedFunctionChanged;
            if (replacement != null) replacement.PropertyChanged += WrappedFunctionChanged;
            return replacement;
        }

        /// <summary>
        /// Subscribes to a newly wrapped function without unsubscribing anything — for
        /// construction and cloning paths that assign a backing field directly.
        /// </summary>
        /// <param name="function">The wrapped function; may be null.</param>
        protected void SubscribeFunction(IRiskFunction? function)
        {
            if (function != null) function.PropertyChanged += WrappedFunctionChanged;
        }

        /// <summary>
        /// Unsubscribes from a function this element no longer wraps.
        /// </summary>
        /// <param name="function">The function; may be null.</param>
        protected void UnsubscribeFunction(IRiskFunction? function)
        {
            if (function != null) function.PropertyChanged -= WrappedFunctionChanged;
        }

        /// <summary>
        /// Re-raises a wrapped function's change as a change of this element's function property.
        /// </summary>
        /// <param name="sender">The wrapped function.</param>
        /// <param name="e">The originating change arguments.</param>
        private void WrappedFunctionChanged(object? sender, PropertyChangedEventArgs e)
        {
            RaisePropertyChange(FunctionPropertyName);
        }

        /// <summary>
        /// Serializes one wrapped function as a container child: its inline content under
        /// <see cref="RiskSerializationMode.SelfContained"/>, or a
        /// <see cref="FunctionReferenceElementName"/> marker carrying its id and name under
        /// <see cref="RiskSerializationMode.ByReference"/>.
        /// </summary>
        /// <param name="function">The wrapped function.</param>
        /// <param name="mode">The serialization mode.</param>
        /// <returns>The child to place inside the element's function container.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the function is null.</exception>
        protected static XElement WriteFunctionEntry(IRiskFunction function, RiskSerializationMode mode)
        {
            return FunctionEntry.Write(function, mode);
        }

        /// <summary>
        /// Reads one serialized function container child: resolves a
        /// <see cref="FunctionReferenceElementName"/> marker through the resolver, or reconstructs
        /// inline content through the cluster factory. Inline content always wins when present, so
        /// a self-contained form loads identically whether or not a resolver was supplied.
        /// </summary>
        /// <typeparam name="T">The cluster interface the wrapped function must satisfy.</typeparam>
        /// <param name="child">The container child.</param>
        /// <param name="resolver">The function resolver; null when reading a self-contained form.</param>
        /// <param name="inlineFactory">The cluster factory reconstructing inline content.</param>
        /// <param name="linkDescription">
        /// A short description of the reference used in the unresolved-reference message, e.g.
        /// "consequence function 'Life Loss'".
        /// </param>
        /// <returns>
        /// The wrapped function, or null when a reference could not be resolved (recorded for
        /// <see cref="Validate"/>) — never null for inline content, which throws instead.
        /// </returns>
        /// <exception cref="ArgumentNullException">Thrown when the child or the factory is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when inline content cannot be reconstructed (dropping it would lose model content
        /// on the next save), when a serialized reference id is stale, or when the resolved
        /// function is of the wrong cluster.
        /// </exception>
        protected T? ReadFunctionEntry<T>(XElement child, IRiskFunctionResolver? resolver,
            Func<XElement, IRiskFunction?> inlineFactory, string linkDescription)
            where T : class, IRiskFunction
        {
            return FunctionEntry.Read<T>(child, resolver, inlineFactory, _name,
                $"The {GetType().Name} '{_name}'", linkDescription, _unresolvedFunctionReferences);
        }

        /// <summary>
        /// Whether this element carries a function reference that could not be resolved. Elements
        /// suppress their generic "no function assigned" message when this is true, because
        /// <see cref="Validate"/> already reports the more precise cause.
        /// </summary>
        protected bool HasUnresolvedFunctionReferences
        {
            get { return _unresolvedFunctionReferences.Count > 0; }
        }

        /// <summary>
        /// Copies the base identity and metadata onto a clone target: Id (shared), name,
        /// description, and canvas position. Never copies connections or the name authority.
        /// </summary>
        /// <param name="target">The clone target.</param>
        /// <exception cref="ArgumentNullException">Thrown when the target is null.</exception>
        protected void CopyBaseTo(RiskElementBase target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            target._id = _id;
            target._name = _name;
            target._description = _description;
            target._leftPosition = _leftPosition;
            target._topPosition = _topPosition;
        }

        /// <summary>
        /// Reads the base identity attributes from a serialized element: Id (fresh Guid when
        /// missing or unparseable), name, description, and canvas position. Bypasses the name
        /// authority (the element is detached during construction).
        /// </summary>
        /// <param name="xElement">The serialized form.</param>
        /// <exception cref="ArgumentNullException">Thrown when the element is null.</exception>
        protected void ReadBaseFromXElement(XElement xElement)
        {
            if (xElement == null) throw new ArgumentNullException(nameof(xElement));
            _id = RiskElementResolver.ParsePendingId(xElement.Attribute(nameof(Id))?.Value) ?? Guid.NewGuid();
            _name = SerializationUtilities.ReadString(xElement, nameof(Name));
            _description = SerializationUtilities.ReadString(xElement, nameof(Description));
            _leftPosition = SerializationUtilities.ReadDouble(xElement, nameof(LeftPosition));
            _topPosition = SerializationUtilities.ReadDouble(xElement, nameof(TopPosition));
        }

        /// <summary>
        /// Writes the base identity attributes onto a serialized element: Id ("D" format), name,
        /// description, and canvas position (G17 invariant).
        /// </summary>
        /// <param name="xElement">The serialized form under construction.</param>
        /// <exception cref="ArgumentNullException">Thrown when the element is null.</exception>
        protected void AddBaseAttributesToXElement(XElement xElement)
        {
            if (xElement == null) throw new ArgumentNullException(nameof(xElement));
            xElement.SetAttributeValue(nameof(Id), _id.ToString("D"));
            xElement.SetAttributeValue(nameof(Name), _name);
            xElement.SetAttributeValue(nameof(Description), _description);
            xElement.SetAttributeValue(nameof(LeftPosition), SerializationUtilities.FormatDouble(_leftPosition));
            xElement.SetAttributeValue(nameof(TopPosition), SerializationUtilities.FormatDouble(_topPosition));
        }

        /// <summary>
        /// Writes a connection's dual Id + Name + port attributes under a connection kind:
        /// <c>{kind}ElementId</c>, <c>{kind}Element</c>, <c>{kind}Port</c>. No attributes are
        /// written for a null connection.
        /// </summary>
        /// <param name="xElement">The serialized form under construction.</param>
        /// <param name="kind">The connection kind: "Source", "SecondarySource", or "HazardSource".</param>
        /// <param name="connection">The connection to write; may be null.</param>
        protected static void WriteConnection(XElement xElement, string kind, RiskConnection? connection)
        {
            if (connection == null) return;
            xElement.SetAttributeValue(kind + "ElementId", connection.Source.Id.ToString("D"));
            xElement.SetAttributeValue(kind + "Element", connection.Source.Name);
            xElement.SetAttributeValue(kind + "Port", connection.SourcePort);
            if (connection.SourceBranchId.HasValue)
            {
                string? branchName = connection.SourceBranchName;
                if (connection.Source is ResponseElement response)
                    branchName = response.RequireAvailableBranch(connection.SourceBranchId.Value).Name;
                xElement.SetAttributeValue(kind + "BranchId", connection.SourceBranchId.Value.ToString("D"));
                xElement.SetAttributeValue(kind + "Branch", branchName);
            }
        }

        /// <summary>One unresolved serialized graph connection.</summary>
        protected readonly struct PendingConnection
        {
            /// <summary>Initializes a pending graph connection.</summary>
            /// <param name="id">The source-element id.</param>
            /// <param name="name">The source-element name fallback.</param>
            /// <param name="port">The persisted source port.</param>
            /// <param name="branchId">The selected branch id.</param>
            /// <param name="branchName">The selected branch-name fallback.</param>
            internal PendingConnection(Guid? id, string? name, int port, Guid? branchId,
                string? branchName)
            {
                Id = id;
                Name = name;
                Port = port;
                BranchId = branchId;
                BranchName = branchName;
            }

            internal Guid? Id { get; }
            internal string? Name { get; }
            internal int Port { get; }
            internal Guid? BranchId { get; }
            internal string? BranchName { get; }
        }

        /// <summary>
        /// Reads a connection's pending reference from its kind-named attributes. Null when
        /// neither the Id nor the Name attribute is present.
        /// </summary>
        /// <param name="xElement">The serialized form.</param>
        /// <param name="kind">The connection kind: "Source", "SecondarySource", or "HazardSource".</param>
        /// <returns>The pending (Id, Name, Port) triple, or null when the connection was not serialized.</returns>
        protected static PendingConnection? ReadPendingConnection(XElement xElement, string kind)
        {
            string? idText = xElement.Attribute(kind + "ElementId")?.Value;
            string? nameText = xElement.Attribute(kind + "Element")?.Value;
            if (idText == null && nameText == null) return null;
            string? branchIdText = xElement.Attribute(kind + "BranchId")?.Value;
            return new PendingConnection(RiskElementResolver.ParsePendingId(idText), nameText,
                SerializationUtilities.ReadInt32(xElement, kind + "Port"),
                RiskElementResolver.ParsePendingId(branchIdText),
                xElement.Attribute(kind + "Branch")?.Value);
        }

        /// <summary>
        /// Resolves a pending connection triple to a live connection through the resolver.
        /// </summary>
        /// <param name="pending">The pending triple captured at construction; may be null.</param>
        /// <param name="resolver">The resolver over the restored graph.</param>
        /// <param name="linkDescription">A short description of the link for the stale-Id message.</param>
        /// <returns>The resolved connection, or null when nothing was serialized or the name fallback missed.</returns>
        protected static RiskConnection? ResolveConnection(PendingConnection? pending,
            RiskElementResolver resolver, string linkDescription)
        {
            if (pending == null) return null;
            var source = resolver.Resolve(pending.Value.Id, pending.Value.Name, linkDescription);
            if (source == null) return null;
            if (source is ResponseElement response
                && (response.ExpandBranchOutputs || pending.Value.BranchId.HasValue
                    || !string.IsNullOrEmpty(pending.Value.BranchName)))
            {
                return response.ResolveBranchConnection(pending.Value.BranchId,
                    pending.Value.BranchName, pending.Value.Port, linkDescription);
            }
            return new RiskConnection(source, pending.Value.Port);
        }

        /// <summary>
        /// Re-maps a connection through an original→clone map: the mapped clone when present,
        /// otherwise the original source (graph validation reports the dangling link).
        /// </summary>
        /// <param name="connection">The original's connection; may be null.</param>
        /// <param name="cloneMap">The original→clone map.</param>
        /// <returns>The re-mapped connection, or null when the original had none.</returns>
        protected static RiskConnection? RemapConnection(RiskConnection? connection,
            IReadOnlyDictionary<IRiskElement, IRiskElement> cloneMap)
        {
            if (connection == null) return null;
            return cloneMap.TryGetValue(connection.Source, out var clone)
                ? new RiskConnection(clone, connection.SourcePort,
                    connection.SourceBranchId, connection.SourceBranchName)
                : connection;
        }

        /// <summary>
        /// Aggregates a wrapped function's validation messages with this element's name as
        /// context, preserving the Error/Warning prefixes.
        /// </summary>
        /// <param name="messages">The message sink.</param>
        /// <param name="functionMessages">The wrapped function's validation messages.</param>
        protected void AggregateWithContext(List<string> messages, IEnumerable<string> functionMessages)
        {
            foreach (string message in functionMessages)
            {
                if (message.StartsWith("Error: ", StringComparison.Ordinal))
                {
                    messages.Add($"Error: Element '{_name}': {message.Substring(7)}");
                }
                else if (message.StartsWith("Warning: ", StringComparison.Ordinal))
                {
                    messages.Add($"Warning: Element '{_name}': {message.Substring(9)}");
                }
                else
                {
                    messages.Add(message);
                }
            }
        }

        /// <summary>
        /// Raises the <see cref="PropertyChanged"/> event.
        /// </summary>
        /// <param name="propertyName">The name of the property that changed.</param>
        protected void RaisePropertyChange(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion
    }
}
