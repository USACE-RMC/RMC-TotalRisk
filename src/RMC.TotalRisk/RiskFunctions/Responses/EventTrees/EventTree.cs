using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;

using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees.Nodes;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees.Support;
namespace RMC.TotalRisk.RiskFunctions.Responses.EventTrees
{
    /// <summary>
    /// A controlled event-tree node collection. Public views are read-only and every structural
    /// mutation is validated before the authored graph changes.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// </remarks>
    public sealed partial class EventTree
    {
        /// <summary>Initializes an event tree with one initiating root.</summary>
        public EventTree()
        {
            Root = new InitiatingNode();
            AttachNewNode(Root, null, 0);
        }

        /// <summary>Restores an event tree from its explicit node-and-edge serialization.</summary>
        /// <param name="xElement">The serialized event tree.</param>
        /// <param name="resolver">The optional resolver for response-backed probability sources.</param>
        /// <exception cref="ArgumentNullException">Thrown when the element is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the graph is malformed.</exception>
        public EventTree(XElement xElement, IRiskFunctionResolver? resolver = null)
            : this(xElement, resolver, string.Empty)
        {
        }

        /// <summary>Restores an event tree with an owning-function name for diagnostics.</summary>
        /// <param name="xElement">The serialized event tree.</param>
        /// <param name="resolver">The optional function resolver.</param>
        /// <param name="ownerName">The owning function name.</param>
        /// <exception cref="ArgumentNullException">Thrown when the element is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the graph is malformed.</exception>
        internal EventTree(XElement xElement, IRiskFunctionResolver? resolver, string ownerName)
        {
            if (xElement == null) throw new ArgumentNullException(nameof(xElement));
            EventTreeReadScope.Enter();
            try
            {
                var nodeContainer = xElement.Element("Nodes")
                    ?? throw new InvalidOperationException("The serialized event tree has no Nodes container.");
                foreach (XElement nodeElement in nodeContainer.Elements())
                {
                    EventNodeBase node = ReadNode(nodeElement, resolver, ownerName);
                    if (_byId.ContainsKey(node.Id))
                        throw new InvalidOperationException($"The serialized event tree contains duplicate node id '{node.Id:D}'.");
                    _nodes.Add(node);
                    _byId.Add(node.Id, node);
                    node.Attach(this, null);
                    SubscribeNode(node);
                }

                Guid rootId = ReadRequiredGuid(xElement, "RootNodeId");
                if (!_byId.TryGetValue(rootId, out var root) || root is not InitiatingNode initiating)
                    throw new InvalidOperationException("The event-tree root does not resolve to an initiating node.");
                Root = initiating;
                if (_nodes.Count(node => node is InitiatingNode) != 1)
                    throw new InvalidOperationException("An event tree must contain exactly one initiating node.");

                var usedPorts = new HashSet<int>();
                foreach (EventNodeBase node in _nodes.Where(node => node is not InitiatingNode))
                {
                    if (node.OutputPort < 3) continue;
                    if (!usedPorts.Add(node.OutputPort))
                        throw new InvalidOperationException($"The serialized event tree contains duplicate output port '{node.OutputPort}'.");
                    _nextOutputPort = Math.Max(_nextOutputPort, node.OutputPort + 1);
                }
                XElement? branchPorts = xElement.Element("BranchPorts");
                if (branchPorts != null)
                {
                    foreach (XElement branchElement in branchPorts.Elements("Branch"))
                    {
                        Guid branchId = ReadRequiredGuid(branchElement, "Id");
                        int outputPort = SerializationUtilities.ReadInt32(branchElement, "OutputPort");
                        if (outputPort < 3)
                            throw new InvalidOperationException("A serialized linked branch output port must be at least three.");
                        if (!_linkedBranchPorts.TryAdd(branchId, outputPort))
                            throw new InvalidOperationException($"The serialized event tree contains duplicate linked branch id '{branchId:D}'.");
                        string? persistencePath = branchElement.Attribute("PersistencePath")?.Value;
                        if (!string.IsNullOrEmpty(persistencePath))
                            _linkedBranchPaths.Add(branchId, persistencePath);
                        if (!usedPorts.Add(outputPort))
                            throw new InvalidOperationException($"The serialized event tree contains duplicate output port '{outputPort}'.");
                        _nextOutputPort = Math.Max(_nextOutputPort, outputPort + 1);
                    }
                }
                foreach (EventNodeBase node in _nodes.Where(node => node is not InitiatingNode && node.OutputPort < 3))
                {
                    AssignNextOutputPort(node);
                }

                var edgeContainer = xElement.Element("Edges");
                if (edgeContainer != null)
                {
                    foreach (var parentGroup in edgeContainer.Elements("Edge")
                        .Select(edge => new
                        {
                            Element = edge,
                            Parent = ReadRequiredGuid(edge, "ParentNodeId"),
                            Child = ReadRequiredGuid(edge, "ChildNodeId"),
                            Order = SerializationUtilities.ReadInt32(edge, "Order"),
                        })
                        .GroupBy(edge => edge.Parent))
                    {
                        if (!_byId.TryGetValue(parentGroup.Key, out var parent))
                            throw new InvalidOperationException($"An event-tree edge has missing parent '{parentGroup.Key:D}'.");
                        foreach (var edge in parentGroup.OrderBy(item => item.Order))
                        {
                            if (!_byId.TryGetValue(edge.Child, out var child))
                                throw new InvalidOperationException($"An event-tree edge has missing child '{edge.Child:D}'.");
                            if (child == Root || child.Parent != null)
                                throw new InvalidOperationException($"Event-tree node '{child.Name}' has an invalid or duplicate parent edge.");
                            parent.MutableChildren.Add(child);
                            child.Attach(this, parent);
                        }
                    }
                }

                if (Root.Parent != null) throw new InvalidOperationException("The initiating event cannot have a parent.");
                if (FindCycle() != null) throw new InvalidOperationException("The serialized event tree contains a structural cycle.");
            }
            finally
            {
                EventTreeReadScope.Exit();
            }
        }

        /// <summary>The insertion-ordered authored node list.</summary>
        private readonly List<EventNodeBase> _nodes = new List<EventNodeBase>();

        /// <summary>The persistent-id lookup.</summary>
        private readonly Dictionary<Guid, EventNodeBase> _byId = new Dictionary<Guid, EventNodeBase>();

        /// <summary>Append-only output-port assignments for expanded linked-leaf branches.</summary>
        private readonly Dictionary<Guid, int> _linkedBranchPorts = new Dictionary<Guid, int>();

        /// <summary>The attached nodes currently observed for direct compute-property edits.</summary>
        private readonly HashSet<EventNodeBase> _subscribedNodes =
            new HashSet<EventNodeBase>(ReferenceEqualityComparer.Instance);

        /// <summary>Current occurrence paths for linked or materialized branch addresses.</summary>
        private readonly Dictionary<Guid, string> _linkedBranchPaths = new Dictionary<Guid, string>();

        /// <summary>The cached read-only node view.</summary>
        private ReadOnlyCollection<EventNodeBase>? _readOnlyNodes;

        /// <summary>The owning response, assigned once the authored tree is placed in a function.</summary>
        private EventTreeResponse? _ownerResponse;

        /// <summary>The next persistent terminal-branch port; 0/1 are aggregate and 2 is implicit.</summary>
        private int _nextOutputPort = 3;


        /// <summary>The single initiating root.</summary>
        public InitiatingNode Root { get; }

        /// <summary>All authored nodes as a read-only view.</summary>
        public IReadOnlyList<EventNodeBase> Nodes => _readOnlyNodes ??= _nodes.AsReadOnly();

        /// <summary>Adds a child, placing explicit branches before an existing remainder.</summary>
        /// <param name="parentId">The structural parent id.</param>
        /// <param name="node">The unattached node to add.</param>
        /// <returns>The added node's stable id.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the node is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the mutation would violate a tree invariant.</exception>
        public Guid Add(Guid parentId, EventNodeBase node)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            EventNodeBase parent = RequireNode(parentId, "Add", "parent");
            ValidateNewChild(parent, node, "Add");
            int index = node is RemainderNode
                ? parent.MutableChildren.Count
                : parent.MutableChildren.FindIndex(child => child is RemainderNode);
            if (index < 0) index = parent.MutableChildren.Count;
            var snapshot = CaptureMutationSnapshot();
            try
            {
                AttachNewNode(node, parent, index);
                EnsureExpandedGraphAcyclic();
                NotifyStructureChanged();
            }
            catch
            {
                RestoreMutationSnapshot(snapshot);
                throw;
            }
            return node.Id;
        }

        /// <summary>Adds an internal independent-clone link to an authored subtree.</summary>
        /// <param name="parentId">The structural parent receiving the link occurrence.</param>
        /// <param name="targetNodeId">The target subtree root in this tree.</param>
        /// <param name="name">The link occurrence display name.</param>
        /// <returns>The added link node id.</returns>
        public Guid LinkIndependent(Guid parentId, Guid targetNodeId, string name = "Independent link")
        {
            EventNodeBase target = RequireNode(targetNodeId, "LinkIndependent", "target");
            var reference = new TreeNodeReference(null, target.Id, nodeName: target.Name);
            return Add(parentId, new EventTreeLinkNode(name, reference));
        }

        /// <summary>Adds an external independent-clone link to another event-tree subtree.</summary>
        /// <param name="parentId">The structural parent receiving the link occurrence.</param>
        /// <param name="targetFunction">The live external event-tree response.</param>
        /// <param name="targetNodeId">The target subtree root in the external function.</param>
        /// <param name="name">The link occurrence display name.</param>
        /// <returns>The added link node id.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the target function is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the target node is missing.</exception>
        public Guid LinkIndependent(Guid parentId, EventTreeResponse targetFunction,
            Guid targetNodeId, string name = "Independent link")
        {
            if (targetFunction == null) throw new ArgumentNullException(nameof(targetFunction));
            EventNodeBase target = targetFunction.EventTree.FindById(targetNodeId)
                ?? throw new InvalidOperationException(
                    $"EventTree LinkIndependent failed: target node '{targetNodeId:D}' was not found in function '{targetFunction.Name}'.");
            var reference = new TreeNodeReference(targetFunction.Id, target.Id,
                targetFunction.Name, target.Name);
            return Add(parentId, new EventTreeLinkNode(name, reference, targetFunction));
        }

        /// <summary>
        /// Adds an internal shared-logical link to an authored subtree. Every occurrence of the
        /// referenced limb reached in the same independent context samples once per realization
        /// and computes identical probabilities.
        /// </summary>
        /// <param name="parentId">The structural parent receiving the link occurrence.</param>
        /// <param name="targetNodeId">The target subtree root in this tree.</param>
        /// <param name="name">The link occurrence display name.</param>
        /// <returns>The added link node id.</returns>
        public Guid LinkShared(Guid parentId, Guid targetNodeId, string name = "Shared link")
        {
            EventNodeBase target = RequireNode(targetNodeId, "LinkShared", "target");
            var reference = new TreeNodeReference(null, target.Id, nodeName: target.Name);
            return Add(parentId, new EventTreeLinkNode(name, reference,
                linkMode: TreeLinkMode.SharedLogicalEvent));
        }

        /// <summary>
        /// Adds an external shared-logical link to another event-tree subtree. Every occurrence of
        /// the referenced limb reached in the same independent context samples once per
        /// realization and computes identical probabilities.
        /// </summary>
        /// <param name="parentId">The structural parent receiving the link occurrence.</param>
        /// <param name="targetFunction">The live external event-tree response.</param>
        /// <param name="targetNodeId">The target subtree root in the external function.</param>
        /// <param name="name">The link occurrence display name.</param>
        /// <returns>The added link node id.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the target function is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the target node is missing.</exception>
        public Guid LinkShared(Guid parentId, EventTreeResponse targetFunction,
            Guid targetNodeId, string name = "Shared link")
        {
            if (targetFunction == null) throw new ArgumentNullException(nameof(targetFunction));
            EventNodeBase target = targetFunction.EventTree.FindById(targetNodeId)
                ?? throw new InvalidOperationException(
                    $"EventTree LinkShared failed: target node '{targetNodeId:D}' was not found in function '{targetFunction.Name}'.");
            var reference = new TreeNodeReference(targetFunction.Id, target.Id,
                targetFunction.Name, target.Name);
            return Add(parentId, new EventTreeLinkNode(name, reference, targetFunction,
                TreeLinkMode.SharedLogicalEvent));
        }

        /// <summary>Inserts an explicit child before an identified sibling.</summary>
        /// <param name="beforeSiblingId">The sibling before which to insert.</param>
        /// <param name="node">The unattached node.</param>
        /// <returns>The added node's stable id.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the node is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the mutation would violate a tree invariant.</exception>
        public Guid Insert(Guid beforeSiblingId, EventNodeBase node)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            EventNodeBase sibling = RequireNode(beforeSiblingId, "Insert", "sibling");
            EventNodeBase parent = sibling.Parent
                ?? throw MutationError("Insert", node, sibling, "the target sibling has no parent");
            if (node is RemainderNode)
                throw MutationError("Insert", node, sibling, "a remainder branch must be the final presented sibling");
            ValidateNewChild(parent, node, "Insert");
            int index = parent.MutableChildren.IndexOf(sibling);
            var snapshot = CaptureMutationSnapshot();
            try
            {
                AttachNewNode(node, parent, index);
                EnsureExpandedGraphAcyclic();
                NotifyStructureChanged();
            }
            catch
            {
                RestoreMutationSnapshot(snapshot);
                throw;
            }
            return node.Id;
        }

        /// <summary>Moves an existing subtree transactionally to a new parent and position.</summary>
        /// <param name="nodeId">The subtree root to move.</param>
        /// <param name="newParentId">The destination parent.</param>
        /// <param name="beforeSiblingId">An optional destination sibling before which to insert.</param>
        /// <exception cref="InvalidOperationException">Thrown when the move would violate an invariant.</exception>
        public void Move(Guid nodeId, Guid newParentId, Guid? beforeSiblingId = null)
        {
            EventNodeBase node = RequireNode(nodeId, "Move", "source");
            EventNodeBase newParent = RequireNode(newParentId, "Move", "target parent");
            if (newParent is EventTreeLinkNode)
                throw MutationError("Move", node, newParent, "an event-tree link cannot own authored children");
            if (ReferenceEquals(node, Root)) throw MutationError("Move", node, newParent, "the initiating root cannot be moved");
            if (ReferenceEquals(node, newParent) || IsReachable(node.Id, newParent.Id))
                throw MutationError("Move", node, newParent, "the move would create a cycle");

            EventNodeBase? before = null;
            if (beforeSiblingId.HasValue)
            {
                before = RequireNode(beforeSiblingId.Value, "Move", "target sibling");
                if (!ReferenceEquals(before.Parent, newParent))
                    throw MutationError("Move", node, before, "the target sibling is not a child of the destination parent");
                if (ReferenceEquals(before, node)) return;
            }
            ValidateRemainderPlacement(newParent, node, before, "Move");

            var snapshot = CaptureMutationSnapshot();
            EventNodeBase oldParent = node.Parent!;
            int oldIndex = oldParent.MutableChildren.IndexOf(node);
            int newIndex = before == null ? newParent.MutableChildren.Count : newParent.MutableChildren.IndexOf(before);
            try
            {
                oldParent.MutableChildren.RemoveAt(oldIndex);
                if (ReferenceEquals(oldParent, newParent) && oldIndex < newIndex) newIndex--;
                if (node is not RemainderNode && before == null)
                {
                    int remainderIndex = newParent.MutableChildren.FindIndex(child => child is RemainderNode);
                    if (remainderIndex >= 0) newIndex = remainderIndex;
                }
                newParent.MutableChildren.Insert(newIndex, node);
                node.Attach(this, newParent);
                EnsureExpandedGraphAcyclic();
                NotifyStructureChanged();
            }
            catch
            {
                RestoreMutationSnapshot(snapshot);
                throw;
            }
        }

        /// <summary>Deletes an authored subtree without permitting dangling references.</summary>
        /// <param name="nodeId">The subtree root to delete.</param>
        /// <param name="policy">The policy for internal links targeting the selected subtree.</param>
        /// <exception cref="InvalidOperationException">Thrown when the root is selected or the policy would leave a dangling link.</exception>
        public void Delete(Guid nodeId, TreeDeletePolicy policy = TreeDeletePolicy.RejectIfReferenced)
        {
            DeleteTransactional(nodeId, policy);
        }

        /// <summary>Removes one subtree after reference policy has been resolved.</summary>
        private void RemoveSubtree(EventNodeBase node)
        {
            node.Parent!.MutableChildren.Remove(node);
            foreach (EventNodeBase descendant in DescendantsAndSelf(node).Reverse())
            {
                _byId.Remove(descendant.Id);
                _nodes.Remove(descendant);
                descendant.MutableChildren.Clear();
                UnsubscribeNode(descendant);
                descendant.Detach();
            }
        }

        /// <summary>Finds a node by persistent id.</summary>
        /// <param name="id">The node id.</param>
        /// <returns>The node, or null.</returns>
        public EventNodeBase? FindById(Guid id)
        {
            return _byId.TryGetValue(id, out var node) ? node : null;
        }

        /// <summary>Finds nodes whose names match exactly.</summary>
        /// <param name="name">The exact name.</param>
        /// <returns>The matching nodes in persistent pre-order.</returns>
        public IReadOnlyList<EventNodeBase> FindByName(string name)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            return DepthFirstPreOrder().Where(node => string.Equals(node.Name, name, StringComparison.Ordinal)).ToArray();
        }

        /// <summary>Finds nodes whose names match without regard to case.</summary>
        /// <param name="name">The name to match.</param>
        /// <returns>The matching nodes in persistent pre-order.</returns>
        public IReadOnlyList<EventNodeBase> FindByNameIgnoreCase(string name)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            return DepthFirstPreOrder().Where(node => string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        /// <summary>Finds nodes satisfying a predicate.</summary>
        /// <param name="predicate">The predicate.</param>
        /// <returns>The matching nodes in persistent pre-order.</returns>
        public IReadOnlyList<EventNodeBase> FindAll(Func<EventNodeBase, bool> predicate)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));
            return DepthFirstPreOrder().Where(predicate).ToArray();
        }

        /// <summary>Traverses the authored tree depth-first, parent before children.</summary>
        /// <returns>The persistent pre-order sequence.</returns>
        public IReadOnlyList<EventNodeBase> DepthFirstPreOrder()
        {
            var result = new List<EventNodeBase>();
            var stack = new Stack<EventNodeBase>();
            stack.Push(Root);
            while (stack.Count > 0)
            {
                EventNodeBase node = stack.Pop();
                result.Add(node);
                for (int i = node.Children.Count - 1; i >= 0; i--) stack.Push(node.Children[i]);
            }
            return result;
        }

        /// <summary>Traverses the authored tree depth-first, children before parent.</summary>
        /// <returns>The persistent post-order sequence.</returns>
        public IReadOnlyList<EventNodeBase> DepthFirstPostOrder()
        {
            var result = new List<EventNodeBase>();
            var stack = new Stack<(EventNodeBase Node, bool Expanded)>();
            stack.Push((Root, false));
            while (stack.Count > 0)
            {
                var item = stack.Pop();
                if (item.Expanded)
                {
                    result.Add(item.Node);
                    continue;
                }
                stack.Push((item.Node, true));
                for (int i = item.Node.Children.Count - 1; i >= 0; i--)
                    stack.Push((item.Node.Children[i], false));
            }
            return result;
        }

        /// <summary>Traverses the authored tree breadth-first.</summary>
        /// <returns>The persistent breadth-first sequence.</returns>
        public IReadOnlyList<EventNodeBase> BreadthFirst()
        {
            var result = new List<EventNodeBase>();
            var queue = new Queue<EventNodeBase>();
            queue.Enqueue(Root);
            while (queue.Count > 0)
            {
                EventNodeBase node = queue.Dequeue();
                result.Add(node);
                for (int i = 0; i < node.Children.Count; i++) queue.Enqueue(node.Children[i]);
            }
            return result;
        }

        /// <summary>Gets ancestors from immediate parent through the root.</summary>
        /// <param name="nodeId">The node id.</param>
        /// <returns>The ancestors nearest-first.</returns>
        public IReadOnlyList<EventNodeBase> GetAncestors(Guid nodeId)
        {
            EventNodeBase node = RequireNode(nodeId, "GetAncestors", "source");
            var result = new List<EventNodeBase>();
            for (EventNodeBase? parent = node.Parent; parent != null; parent = parent.Parent) result.Add(parent);
            return result;
        }

        /// <summary>Gets all structural descendants in persistent pre-order.</summary>
        /// <param name="nodeId">The subtree root.</param>
        /// <returns>The descendants, excluding the selected root.</returns>
        public IReadOnlyList<EventNodeBase> GetDescendants(Guid nodeId)
        {
            EventNodeBase node = RequireNode(nodeId, "GetDescendants", "source");
            return DescendantsAndSelf(node).Skip(1).ToArray();
        }

        /// <summary>Determines whether one node is structurally reachable from another.</summary>
        /// <param name="sourceId">The source node.</param>
        /// <param name="targetId">The prospective descendant.</param>
        /// <returns>True when the target is the source or one of its descendants.</returns>
        public bool IsReachable(Guid sourceId, Guid targetId)
        {
            EventNodeBase source = RequireNode(sourceId, "IsReachable", "source");
            RequireNode(targetId, "IsReachable", "target");
            return DescendantsAndSelf(source).Any(node => node.Id == targetId);
        }

        /// <summary>Enumerates authored terminal end states in persistent pre-order.</summary>
        /// <returns>The terminal nodes.</returns>
        public IReadOnlyList<EventNodeBase> GetLeaves()
        {
            return DepthFirstPreOrder().Where(node => node.IsTerminal && node is not InitiatingNode).ToArray();
        }

        /// <summary>Computes the metadata-free canonical hash of one authored subtree.</summary>
        /// <param name="nodeId">The subtree root.</param>
        /// <returns>The subtree SHA-256 hash.</returns>
        public byte[] SubtreeCanonicalHash(Guid nodeId)
        {
            EventNodeBase node = RequireNode(nodeId, "SubtreeCanonicalHash", "source");
            if (_ownerResponse != null)
            {
                EventTreeOccurrencePlan plan = ReferenceEquals(node, Root)
                    ? _ownerResponse.GetOccurrencePlan()
                    : EventTreeOccurrencePlan.Compile(_ownerResponse, node);
                return CanonicalContentHasher.Hash(
                    plan.Identity,
                    CanonicalizationRules.ModelRules);
            }
            if (DescendantsAndSelf(node).Any(item => item is EventTreeLinkNode))
                throw new InvalidOperationException("An event tree containing links must be owned by an EventTreeResponse before its subtree identity can be computed.");
            return CanonicalContentHasher.Hash(BuildIdentity(node), CanonicalizationRules.ModelRules);
        }

        /// <summary>Compares two event trees by compute-relevant structure and content.</summary>
        /// <param name="other">The other tree.</param>
        /// <returns>True when their projected canonical identities are byte-identical.</returns>
        public bool StructuralEquals(EventTree? other)
        {
            return other != null && SubtreeCanonicalHash(Root.Id).AsSpan().SequenceEqual(other.SubtreeCanonicalHash(other.Root.Id));
        }

        /// <summary>Serializes the tree self-contained.</summary>
        /// <returns>The explicit node-and-edge representation.</returns>
        public XElement ToXElement()
        {
            return ToXElement(RiskSerializationMode.SelfContained);
        }

        /// <summary>Serializes the tree in the selected nested-function mode.</summary>
        /// <param name="mode">The serialization mode.</param>
        /// <returns>The explicit node-and-edge representation.</returns>
        public XElement ToXElement(RiskSerializationMode mode)
        {
            var element = new XElement(nameof(EventTree));
            element.SetAttributeValue("RootNodeId", Root.Id.ToString("D"));
            var nodes = new XElement("Nodes");
            foreach (EventNodeBase node in _nodes)
            {
                var serialized = new XElement(node.SerializedName);
                serialized.SetAttributeValue("Id", node.Id.ToString("D"));
                serialized.SetAttributeValue("Name", node.Name);
                serialized.SetAttributeValue("Description", node.Description);
                serialized.SetAttributeValue("IsFailure", node.IsFailure);
                serialized.SetAttributeValue("OutputPort", node.OutputPort.ToString(CultureInfo.InvariantCulture));
                if (node is ChanceNode chance) serialized.Add(chance.ProbabilitySource.ToXElement(mode));
                if (node is EventTreeLinkNode link) link.WriteContent(serialized, mode);
                nodes.Add(serialized);
            }
            element.Add(nodes);

            var branchPorts = new XElement("BranchPorts");
            foreach (KeyValuePair<Guid, int> branch in _linkedBranchPorts.OrderBy(item => item.Value))
            {
                var serialized = new XElement("Branch");
                serialized.SetAttributeValue("Id", branch.Key.ToString("D"));
                serialized.SetAttributeValue("OutputPort", branch.Value.ToString(CultureInfo.InvariantCulture));
                if (_linkedBranchPaths.TryGetValue(branch.Key, out string? persistencePath))
                    serialized.SetAttributeValue("PersistencePath", persistencePath);
                branchPorts.Add(serialized);
            }
            element.Add(branchPorts);

            var edges = new XElement("Edges");
            foreach (EventNodeBase parent in _nodes)
            {
                for (int i = 0; i < parent.Children.Count; i++)
                {
                    var edge = new XElement("Edge");
                    edge.SetAttributeValue("ParentNodeId", parent.Id.ToString("D"));
                    edge.SetAttributeValue("ChildNodeId", parent.Children[i].Id.ToString("D"));
                    edge.SetAttributeValue("Order", i.ToString(CultureInfo.InvariantCulture));
                    edges.Add(edge);
                }
            }
            element.Add(edges);
            return element;
        }

        /// <summary>Validates structure and probability sources against an owning hazard axis.</summary>
        /// <param name="hazards">The event-tree hazard levels.</param>
        /// <returns>Deterministically ordered diagnostics.</returns>
        internal List<string> Validate(IReadOnlyList<double> hazards)
        {
            var messages = new List<string>();
            if (Root.Children.Count == 0) messages.Add("Error: The event tree initiating node has no branches.");
            if (_nodes.Any(node => node.Owner != this)) messages.Add("Error: The event tree contains a node with inconsistent ownership.");
            if (FindCycle() != null) messages.Add("Error: The event tree contains a structural cycle.");
            foreach (EventNodeBase parent in DepthFirstPreOrder())
            {
                int remainderCount = parent.Children.Count(child => child is RemainderNode);
                if (remainderCount > 1) messages.Add($"Error: Event-tree node '{parent.Name}' has more than one remainder branch.");
                if (remainderCount == 1 && parent.Children[parent.Children.Count - 1] is not RemainderNode)
                    messages.Add($"Error: Event-tree node '{parent.Name}' does not present its remainder branch last.");
                if (parent is EventTreeLinkNode && parent.Children.Count != 0)
                    messages.Add($"Error: Event-tree link '{parent.Name}' cannot own authored children.");
            }
            return messages;
        }

        /// <summary>Gets nodes in metadata-inert canonical pre-order.</summary>
        /// <returns>The canonical occurrence order.</returns>
        internal IReadOnlyList<EventNodeBase> CanonicalPreOrder()
        {
            var result = new List<EventNodeBase>();
            var stack = new Stack<EventNodeBase>();
            stack.Push(Root);
            while (stack.Count > 0)
            {
                EventNodeBase node = stack.Pop();
                result.Add(node);
                var children = CanonicalChildren(node);
                for (int i = children.Count - 1; i >= 0; i--) stack.Push(children[i]);
            }
            return result;
        }

        /// <summary>Builds the entire tree's projected identity.</summary>
        /// <returns>The metadata-free projected tree.</returns>
        internal XElement ToIdentityXElement()
        {
            if (_ownerResponse != null) return _ownerResponse.GetOccurrencePlan().Identity;
            if (_nodes.Any(node => node is EventTreeLinkNode))
                throw new InvalidOperationException("An event tree containing links must be owned by an EventTreeResponse before its identity can be computed.");
            return new XElement(nameof(EventTree), BuildIdentity(Root));
        }

        /// <summary>Associates this controlled tree with its sole response-function owner.</summary>
        /// <param name="owner">The owning response.</param>
        internal void AttachOwner(EventTreeResponse owner)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            if (_ownerResponse != null && !ReferenceEquals(_ownerResponse, owner))
                throw new InvalidOperationException("An EventTree cannot be owned by more than one EventTreeResponse.");
            _ownerResponse = owner;
        }

        /// <summary>Observes one attached node for compute-relevant property replacement.</summary>
        /// <param name="node">The attached node.</param>
        private void SubscribeNode(EventNodeBase node)
        {
            if (_subscribedNodes.Add(node)) node.PropertyChanged += NodePropertyChanged;
        }

        /// <summary>Stops observing one detached node.</summary>
        /// <param name="node">The detached node.</param>
        private void UnsubscribeNode(EventNodeBase node)
        {
            if (_subscribedNodes.Remove(node)) node.PropertyChanged -= NodePropertyChanged;
        }

        /// <summary>Reconciles node subscriptions after transactional topology restoration.</summary>
        private void ReconcileNodeSubscriptions()
        {
            var current = new HashSet<EventNodeBase>(_nodes, ReferenceEqualityComparer.Instance);
            foreach (EventNodeBase node in _subscribedNodes.Where(node => !current.Contains(node)).ToArray())
                UnsubscribeNode(node);
            for (int i = 0; i < _nodes.Count; i++) SubscribeNode(_nodes[i]);
        }

        /// <summary>Invalidates the owner plan after a direct compute-relevant node edit.</summary>
        private void NodePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not EventNodeBase node) return;
            if (e.PropertyName == nameof(EventNodeBase.Name)
                || e.PropertyName == nameof(EventNodeBase.Description)) return;
            if (e.PropertyName == nameof(EventNodeBase.IsFailure)
                && !node.IsTerminal && node is not EventTreeLinkNode) return;
            _ownerResponse?.NotifyTreeComputeChanged();
        }

        /// <summary>Invalidates the owner plan after one validated structural mutation.</summary>
        private void NotifyStructureChanged()
        {
            _ownerResponse?.NotifyTreeComputeChanged();
        }

        /// <summary>Attaches one new node after all mutation checks have passed.</summary>
        /// <param name="node">The new node.</param>
        /// <param name="parent">The parent, or null for initial root construction.</param>
        /// <param name="childIndex">The child insertion index.</param>
        private void AttachNewNode(EventNodeBase node, EventNodeBase? parent, int childIndex)
        {
            _nodes.Add(node);
            _byId.Add(node.Id, node);
            node.Attach(this, parent);
            SubscribeNode(node);
            if (node is not InitiatingNode && node.OutputPort < 3) AssignNextOutputPort(node);
            if (parent != null) parent.MutableChildren.Insert(childIndex, node);
        }

        /// <summary>Assigns the next unused persistent output port to a new or migrated node.</summary>
        /// <param name="node">The node receiving the port.</param>
        private void AssignNextOutputPort(EventNodeBase node)
        {
            while (_nodes.Any(existing => existing.OutputPort == _nextOutputPort)
                || _linkedBranchPorts.ContainsValue(_nextOutputPort)) _nextOutputPort++;
            node.AssignOutputPort(_nextOutputPort);
            _nextOutputPort++;
        }

        /// <summary>Gets or append-only assigns one linked occurrence's stable output port and records its current path.</summary>
        /// <param name="branchId">The linked occurrence branch id.</param>
        /// <param name="persistencePath">The current persistent-id occurrence path.</param>
        /// <returns>The stable output port.</returns>
        internal int GetOrAssignLinkedBranchPort(Guid branchId, string persistencePath)
        {
            if (_linkedBranchPorts.TryGetValue(branchId, out int outputPort))
            {
                _linkedBranchPaths[branchId] = persistencePath;
                return outputPort;
            }
            while (_nodes.Any(existing => existing.OutputPort == _nextOutputPort)
                || _linkedBranchPorts.ContainsValue(_nextOutputPort)) _nextOutputPort++;
            outputPort = _nextOutputPort++;
            _linkedBranchPorts.Add(branchId, outputPort);
            _linkedBranchPaths.Add(branchId, persistencePath);
            return outputPort;
        }

        /// <summary>Finds a preserved linked/materialized address for one current occurrence path.</summary>
        /// <param name="persistencePath">The current persistent-id occurrence path.</param>
        /// <param name="branchId">The preserved branch id.</param>
        /// <param name="outputPort">The preserved output port.</param>
        /// <returns>True when the path has a preserved address.</returns>
        internal bool TryGetPreservedBranchAddress(string persistencePath, out Guid branchId,
            out int outputPort)
        {
            foreach (KeyValuePair<Guid, string> item in _linkedBranchPaths)
            {
                if (!string.Equals(item.Value, persistencePath, StringComparison.Ordinal)) continue;
                branchId = item.Key;
                outputPort = _linkedBranchPorts[item.Key];
                return true;
            }
            branchId = Guid.Empty;
            outputPort = -1;
            return false;
        }

        /// <summary>Readdresses every expanded occurrence carried through one materialized link.</summary>
        /// <param name="linkId">The materialized authored link id.</param>
        /// <param name="freshIds">The copied target-subtree source-to-fresh id map.</param>
        internal void RemapMaterializedBranchPaths(Guid linkId,
            IReadOnlyDictionary<Guid, Guid> freshIds)
        {
            string linkToken = linkId.ToString("N");
            foreach (Guid branchId in _linkedBranchPaths.Keys.ToArray())
            {
                string[] tokens = _linkedBranchPaths[branchId].Split('/');
                int linkIndex = Array.FindIndex(tokens,
                    token => string.Equals(token, linkToken, StringComparison.OrdinalIgnoreCase));
                if (linkIndex < 0) continue;

                var remapped = new List<string>(tokens.Length - 1);
                for (int i = 0; i < tokens.Length; i++)
                {
                    if (i == linkIndex) continue;
                    if (i > linkIndex && Guid.TryParseExact(tokens[i], "N", out Guid sourceId)
                        && freshIds.TryGetValue(sourceId, out Guid freshId))
                    {
                        remapped.Add(freshId.ToString("N"));
                    }
                    else
                    {
                        remapped.Add(tokens[i]);
                    }
                }
                _linkedBranchPaths[branchId] = string.Join("/", remapped);
            }
        }

        /// <summary>Validates a detached child before adding it to the selected parent.</summary>
        /// <param name="parent">The selected parent.</param>
        /// <param name="node">The new child.</param>
        /// <param name="operation">The operation name.</param>
        private void ValidateNewChild(EventNodeBase parent, EventNodeBase node, string operation)
        {
            if (node is InitiatingNode) throw MutationError(operation, node, parent, "an initiating node can only be the root");
            if (parent is EventTreeLinkNode) throw MutationError(operation, node, parent, "an event-tree link cannot own authored children");
            if (node.Owner != null || node.Parent != null) throw MutationError(operation, node, parent, "the source node is already attached");
            if (_byId.ContainsKey(node.Id)) throw MutationError(operation, node, parent, "the source id already exists in the tree");
            ValidateRemainderPlacement(parent, node, null, operation);
        }

        /// <summary>Validates remainder uniqueness and presentation placement before mutation.</summary>
        /// <param name="parent">The destination parent.</param>
        /// <param name="node">The child being inserted or moved.</param>
        /// <param name="before">The optional target sibling.</param>
        /// <param name="operation">The operation name.</param>
        private static void ValidateRemainderPlacement(EventNodeBase parent, EventNodeBase node, EventNodeBase? before, string operation)
        {
            var existing = parent.Children.FirstOrDefault(child => child is RemainderNode && !ReferenceEquals(child, node));
            if (node is RemainderNode && existing != null)
                throw MutationError(operation, node, parent, "the destination already has a remainder branch");
            if (node is RemainderNode && before != null)
                throw MutationError(operation, node, before, "a remainder branch must be the final presented sibling");
        }

        /// <summary>Requires a node id and produces a deterministic operation diagnostic when absent.</summary>
        /// <param name="id">The required id.</param>
        /// <param name="operation">The operation name.</param>
        /// <param name="role">The node role.</param>
        /// <returns>The resolved node.</returns>
        private EventNodeBase RequireNode(Guid id, string operation, string role)
        {
            if (_byId.TryGetValue(id, out var node)) return node;
            throw new InvalidOperationException($"EventTree {operation} failed: {role} node '{id:D}' was not found.");
        }

        /// <summary>Builds a deterministic transactional-mutation exception.</summary>
        /// <param name="operation">The operation name.</param>
        /// <param name="source">The source node.</param>
        /// <param name="target">The target node.</param>
        /// <param name="reason">The rejection reason.</param>
        /// <returns>The exception.</returns>
        private static InvalidOperationException MutationError(string operation, EventNodeBase source, EventNodeBase target, string reason)
        {
            return new InvalidOperationException(
                $"EventTree {operation} failed: source '{source.Name}' ({source.Id:D}), target '{target.Name}' ({target.Id:D}); {reason}.");
        }

        /// <summary>Enumerates a subtree iteratively, root first.</summary>
        /// <param name="root">The subtree root.</param>
        /// <returns>The pre-order sequence.</returns>
        private static IEnumerable<EventNodeBase> DescendantsAndSelf(EventNodeBase root)
        {
            var stack = new Stack<EventNodeBase>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                EventNodeBase node = stack.Pop();
                yield return node;
                for (int i = node.Children.Count - 1; i >= 0; i--) stack.Push(node.Children[i]);
            }
        }

        /// <summary>Finds a structural cycle, defensively guarding deserialized or corrupted state.</summary>
        /// <returns>A cycle-closing node, or null.</returns>
        private EventNodeBase? FindCycle()
        {
            var active = new HashSet<Guid>();
            var complete = new HashSet<Guid>();
            var stack = new Stack<(EventNodeBase Node, int ChildIndex)>();
            stack.Push((Root, 0));
            active.Add(Root.Id);
            while (stack.Count > 0)
            {
                var frame = stack.Pop();
                if (frame.ChildIndex >= frame.Node.Children.Count)
                {
                    active.Remove(frame.Node.Id);
                    complete.Add(frame.Node.Id);
                    continue;
                }
                stack.Push((frame.Node, frame.ChildIndex + 1));
                EventNodeBase child = frame.Node.Children[frame.ChildIndex];
                if (active.Contains(child.Id)) return child;
                if (complete.Add(child.Id))
                {
                    complete.Remove(child.Id);
                    active.Add(child.Id);
                    stack.Push((child, 0));
                }
            }
            return null;
        }

        /// <summary>Reads one serialized concrete node.</summary>
        /// <param name="element">The node element.</param>
        /// <param name="resolver">The optional function resolver.</param>
        /// <param name="ownerName">The owning function name.</param>
        /// <returns>The restored node.</returns>
        private static EventNodeBase ReadNode(XElement element, IRiskFunctionResolver? resolver, string ownerName)
        {
            Guid id = ReadRequiredGuid(element, "Id");
            string name = SerializationUtilities.ReadString(element, "Name");
            string description = SerializationUtilities.ReadString(element, "Description");
            bool isFailure = SerializationUtilities.ReadBoolean(element, "IsFailure");
            int outputPort = element.Attribute("OutputPort") == null ? -1 : SerializationUtilities.ReadInt32(element, "OutputPort");
            return element.Name.LocalName switch
            {
                nameof(InitiatingNode) => new InitiatingNode(id, name, description, isFailure, outputPort),
                nameof(ChanceNode) => new ChanceNode(id, name, description, isFailure, outputPort,
                    new ProbabilitySource(element.Element(nameof(ProbabilitySource))
                        ?? throw new InvalidOperationException($"Serialized chance node '{name}' has no probability source."), resolver, ownerName, "event-tree")),
                nameof(RemainderNode) => new RemainderNode(id, name, description, isFailure, outputPort),
                nameof(EventTreeLinkNode) => ReadLinkNode(element, resolver, ownerName,
                    id, name, description, isFailure, outputPort),
                _ => throw new InvalidOperationException($"Unsupported serialized event-tree node '{element.Name.LocalName}'."),
            };
        }

        /// <summary>Reads one serialized link node after common node attributes are parsed.</summary>
        private static EventTreeLinkNode ReadLinkNode(XElement element, IRiskFunctionResolver? resolver,
            string ownerName, Guid id, string name, string description, bool isFailure, int outputPort)
        {
            var content = EventTreeLinkNode.ReadContent(element, resolver, ownerName);
            return new EventTreeLinkNode(id, name, description, isFailure, outputPort,
                content.Mode, content.Target, content.Function, content.Unresolved);
        }

        /// <summary>Checks the expanded reference graph after a mutation when this tree has an owner.</summary>
        private void EnsureExpandedGraphAcyclic()
        {
            if (_ownerResponse != null) EventTreeOccurrencePlan.Compile(_ownerResponse);
        }

        /// <summary>Reads a required persistent Guid attribute.</summary>
        /// <param name="element">The containing element.</param>
        /// <param name="attributeName">The attribute name.</param>
        /// <returns>The parsed id.</returns>
        private static Guid ReadRequiredGuid(XElement element, string attributeName)
        {
            if (Guid.TryParse(element.Attribute(attributeName)?.Value, out Guid id) && id != Guid.Empty) return id;
            throw new InvalidOperationException($"Serialized element '{element.Name.LocalName}' has no valid {attributeName}.");
        }

        /// <summary>Gets one node's children sorted by compute identity, not metadata or Guid.</summary>
        /// <param name="node">The parent.</param>
        /// <returns>The canonical child sequence.</returns>
        private static IReadOnlyList<EventNodeBase> CanonicalChildren(EventNodeBase node)
        {
            return node.Children
                .Select((child, index) => new { Child = child, Index = index, Token = IdentityToken(child) })
                .OrderBy(item => item.Token, StringComparer.Ordinal)
                .ThenBy(item => item.Index)
                .Select(item => item.Child)
                .ToArray();
        }

        /// <summary>Builds one subtree's projected compute identity.</summary>
        /// <param name="node">The subtree root.</param>
        /// <returns>The identity element.</returns>
        private static XElement BuildIdentity(EventNodeBase node)
        {
            var identity = new XElement("Node");
            identity.SetAttributeValue("Type", node.SerializedName);
            if (node.IsTerminal) identity.SetAttributeValue("IsFailure", node.IsFailure);
            if (node is ChanceNode chance) identity.Add(chance.ProbabilitySource.ToIdentityXElement());
            foreach (EventNodeBase child in CanonicalChildren(node)) identity.Add(BuildIdentity(child));
            return identity;
        }

        /// <summary>Returns the metadata-free canonical token of one subtree.</summary>
        /// <param name="node">The subtree root.</param>
        /// <returns>The uppercase SHA-256 token.</returns>
        private static string IdentityToken(EventNodeBase node)
        {
            return CanonicalContentHasher.ToTokenHex(
                CanonicalContentHasher.Hash(BuildIdentity(node), CanonicalizationRules.ModelRules));
        }
    }
}
