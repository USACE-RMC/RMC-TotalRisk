using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Nodes;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Support;

namespace RMC.TotalRisk.RiskFunctions.Responses.FaultTrees
{
    /// <summary>
    /// A controlled fault-tree node collection with one top-event gate root. Public views are
    /// read-only and every structural mutation is validated before the authored graph changes.
    /// The authored form is a single-parent tree; all sharing and repetition flows through
    /// <see cref="FaultTreeTransferNode"/> references, whose resolution produces the expanded
    /// directed acyclic logic graph.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// </remarks>
    public sealed partial class FaultTree
    {
        /// <summary>Initializes a fault tree with one top-event Or gate.</summary>
        public FaultTree()
        {
            Root = new FaultTreeGateNode("Top event", FaultTreeGateType.Or);
            AttachNewNode(Root, null, 0);
        }

        /// <summary>Restores a fault tree from its explicit node-and-input serialization.</summary>
        /// <param name="xElement">The serialized fault tree.</param>
        /// <param name="resolver">The optional resolver for response-backed sources and transfers.</param>
        /// <exception cref="ArgumentNullException">Thrown when the element is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the graph is malformed.</exception>
        public FaultTree(XElement xElement, IRiskFunctionResolver? resolver = null)
            : this(xElement, resolver, string.Empty)
        {
        }

        /// <summary>Restores a fault tree with an owning-function name for diagnostics.</summary>
        /// <param name="xElement">The serialized fault tree.</param>
        /// <param name="resolver">The optional function resolver.</param>
        /// <param name="ownerName">The owning function name.</param>
        /// <exception cref="ArgumentNullException">Thrown when the element is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the graph is malformed.</exception>
        internal FaultTree(XElement xElement, IRiskFunctionResolver? resolver, string ownerName)
        {
            if (xElement == null) throw new ArgumentNullException(nameof(xElement));
            FaultTreeReadScope.Enter();
            try
            {
                var nodeContainer = xElement.Element("Nodes")
                    ?? throw new InvalidOperationException("The serialized fault tree has no Nodes container.");
                foreach (XElement nodeElement in nodeContainer.Elements())
                {
                    FaultTreeNodeBase node = ReadNode(nodeElement, resolver, ownerName);
                    if (_byId.ContainsKey(node.Id))
                        throw new InvalidOperationException($"The serialized fault tree contains duplicate node id '{node.Id:D}'.");
                    _nodes.Add(node);
                    _byId.Add(node.Id, node);
                    node.Attach(this, null);
                    SubscribeNode(node);
                }

                Guid rootId = ReadRequiredGuid(xElement, "TopNodeId");
                if (!_byId.TryGetValue(rootId, out var root) || root is not FaultTreeGateNode topGate)
                    throw new InvalidOperationException("The fault-tree top event does not resolve to a gate node.");
                Root = topGate;

                var inputContainer = xElement.Element("Inputs");
                if (inputContainer != null)
                {
                    foreach (var parentGroup in inputContainer.Elements("Input")
                        .Select(input => new
                        {
                            Element = input,
                            Parent = ReadRequiredGuid(input, "ParentNodeId"),
                            Child = ReadRequiredGuid(input, "ChildNodeId"),
                            Order = SerializationUtilities.ReadInt32(input, "Order"),
                        })
                        .GroupBy(input => input.Parent))
                    {
                        if (!_byId.TryGetValue(parentGroup.Key, out var parent))
                            throw new InvalidOperationException($"A fault-tree input has missing parent '{parentGroup.Key:D}'.");
                        if (parent is not FaultTreeGateNode)
                            throw new InvalidOperationException($"Fault-tree node '{parent.Name}' owns inputs but is not a gate.");
                        foreach (var input in parentGroup.OrderBy(item => item.Order))
                        {
                            if (!_byId.TryGetValue(input.Child, out var child))
                                throw new InvalidOperationException($"A fault-tree input has missing child '{input.Child:D}'.");
                            if (child == Root || child.Parent != null)
                                throw new InvalidOperationException($"Fault-tree node '{child.Name}' has an invalid or duplicate parent input.");
                            parent.MutableChildren.Add(child);
                            child.Attach(this, parent);
                        }
                    }
                }

                if (Root.Parent != null) throw new InvalidOperationException("The top-event gate cannot have a parent.");
                if (FindCycle() != null) throw new InvalidOperationException("The serialized fault tree contains a structural cycle.");

                var groupContainer = xElement.Element("CcfGroups");
                if (groupContainer != null)
                {
                    foreach (XElement groupElement in groupContainer.Elements(nameof(FaultTreeCcfGroup)))
                        AddCcfGroup(new FaultTreeCcfGroup(groupElement));
                }
            }
            finally
            {
                FaultTreeReadScope.Exit();
            }
        }

        /// <summary>The insertion-ordered authored node list.</summary>
        private readonly List<FaultTreeNodeBase> _nodes = new List<FaultTreeNodeBase>();

        /// <summary>The persistent-id lookup.</summary>
        private readonly Dictionary<Guid, FaultTreeNodeBase> _byId = new Dictionary<Guid, FaultTreeNodeBase>();

        /// <summary>The attached nodes currently observed for direct compute-property edits.</summary>
        private readonly HashSet<FaultTreeNodeBase> _subscribedNodes =
            new HashSet<FaultTreeNodeBase>(ReferenceEqualityComparer.Instance);

        /// <summary>The insertion-ordered parametric common-cause failure groups.</summary>
        private readonly List<FaultTreeCcfGroup> _ccfGroups = new List<FaultTreeCcfGroup>();

        /// <summary>The cached read-only node view.</summary>
        private ReadOnlyCollection<FaultTreeNodeBase>? _readOnlyNodes;

        /// <summary>The owning response, assigned once the authored tree is placed in a function.</summary>
        private FaultTreeResponse? _ownerResponse;

        /// <summary>The single top-event gate.</summary>
        public FaultTreeGateNode Root { get; }

        /// <summary>All authored nodes as a read-only view.</summary>
        public IReadOnlyList<FaultTreeNodeBase> Nodes => _readOnlyNodes ??= _nodes.AsReadOnly();

        /// <summary>Adds a gate input at the end of the parent's input list.</summary>
        /// <param name="parentGateId">The structural parent gate id.</param>
        /// <param name="node">The unattached node to add.</param>
        /// <returns>The added node's stable id.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the node is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the mutation would violate a tree invariant.</exception>
        public Guid Add(Guid parentGateId, FaultTreeNodeBase node)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            FaultTreeNodeBase parent = RequireNode(parentGateId, "Add", "parent");
            ValidateNewChild(parent, node, "Add");
            int index = parent.MutableChildren.Count;
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

        /// <summary>Adds an internal shared-logical transfer to an authored subtree.</summary>
        /// <param name="parentGateId">The structural parent gate receiving the transfer.</param>
        /// <param name="targetNodeId">The target subtree root in this tree.</param>
        /// <param name="name">The transfer display name.</param>
        /// <returns>The added transfer node id.</returns>
        public Guid LinkShared(Guid parentGateId, Guid targetNodeId, string name = "Shared transfer")
        {
            FaultTreeNodeBase target = RequireNode(targetNodeId, "LinkShared", "target");
            var reference = new TreeNodeReference(null, target.Id, nodeName: target.Name);
            return Add(parentGateId, new FaultTreeTransferNode(name, reference));
        }

        /// <summary>Adds an external shared-logical transfer to another fault-tree subtree.</summary>
        /// <param name="parentGateId">The structural parent gate receiving the transfer.</param>
        /// <param name="targetFunction">The live external fault-tree response.</param>
        /// <param name="targetNodeId">The target subtree root in the external function.</param>
        /// <param name="name">The transfer display name.</param>
        /// <returns>The added transfer node id.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the target function is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the target node is missing.</exception>
        public Guid LinkShared(Guid parentGateId, FaultTreeResponse targetFunction,
            Guid targetNodeId, string name = "Shared transfer")
        {
            if (targetFunction == null) throw new ArgumentNullException(nameof(targetFunction));
            FaultTreeNodeBase target = targetFunction.FaultTree.FindById(targetNodeId)
                ?? throw new InvalidOperationException(
                    $"FaultTree LinkShared failed: target node '{targetNodeId:D}' was not found in function '{targetFunction.Name}'.");
            var reference = new TreeNodeReference(targetFunction.Id, target.Id,
                targetFunction.Name, target.Name);
            return Add(parentGateId, new FaultTreeTransferNode(name, reference, targetFunction));
        }

        /// <summary>Adds an internal independent-clone transfer to an authored subtree.</summary>
        /// <param name="parentGateId">The structural parent gate receiving the transfer.</param>
        /// <param name="targetNodeId">The target subtree root in this tree.</param>
        /// <param name="name">The transfer display name.</param>
        /// <returns>The added transfer node id.</returns>
        public Guid LinkIndependent(Guid parentGateId, Guid targetNodeId, string name = "Independent transfer")
        {
            FaultTreeNodeBase target = RequireNode(targetNodeId, "LinkIndependent", "target");
            var reference = new TreeNodeReference(null, target.Id, nodeName: target.Name);
            return Add(parentGateId, new FaultTreeTransferNode(name, reference,
                linkMode: TreeLinkMode.IndependentClone));
        }

        /// <summary>Adds an external independent-clone transfer to another fault-tree subtree.</summary>
        /// <param name="parentGateId">The structural parent gate receiving the transfer.</param>
        /// <param name="targetFunction">The live external fault-tree response.</param>
        /// <param name="targetNodeId">The target subtree root in the external function.</param>
        /// <param name="name">The transfer display name.</param>
        /// <returns>The added transfer node id.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the target function is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the target node is missing.</exception>
        public Guid LinkIndependent(Guid parentGateId, FaultTreeResponse targetFunction,
            Guid targetNodeId, string name = "Independent transfer")
        {
            if (targetFunction == null) throw new ArgumentNullException(nameof(targetFunction));
            FaultTreeNodeBase target = targetFunction.FaultTree.FindById(targetNodeId)
                ?? throw new InvalidOperationException(
                    $"FaultTree LinkIndependent failed: target node '{targetNodeId:D}' was not found in function '{targetFunction.Name}'.");
            var reference = new TreeNodeReference(targetFunction.Id, target.Id,
                targetFunction.Name, target.Name);
            return Add(parentGateId, new FaultTreeTransferNode(name, reference, targetFunction,
                TreeLinkMode.IndependentClone));
        }

        /// <summary>Inserts a gate input before an identified sibling.</summary>
        /// <param name="beforeSiblingId">The sibling before which to insert.</param>
        /// <param name="node">The unattached node.</param>
        /// <returns>The added node's stable id.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the node is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the mutation would violate a tree invariant.</exception>
        public Guid Insert(Guid beforeSiblingId, FaultTreeNodeBase node)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            FaultTreeNodeBase sibling = RequireNode(beforeSiblingId, "Insert", "sibling");
            FaultTreeNodeBase parent = sibling.Parent
                ?? throw MutationError("Insert", node, sibling, "the target sibling has no parent");
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

        /// <summary>Moves an existing subtree transactionally to a new parent gate and position.</summary>
        /// <param name="nodeId">The subtree root to move.</param>
        /// <param name="newParentGateId">The destination parent gate.</param>
        /// <param name="beforeSiblingId">An optional destination sibling before which to insert.</param>
        /// <exception cref="InvalidOperationException">Thrown when the move would violate an invariant.</exception>
        public void Move(Guid nodeId, Guid newParentGateId, Guid? beforeSiblingId = null)
        {
            FaultTreeNodeBase node = RequireNode(nodeId, "Move", "source");
            FaultTreeNodeBase newParent = RequireNode(newParentGateId, "Move", "target parent");
            if (newParent is not FaultTreeGateNode)
                throw MutationError("Move", node, newParent, "only fault-tree gates own inputs");
            if (ReferenceEquals(node, Root)) throw MutationError("Move", node, newParent, "the top-event gate cannot be moved");
            if (ReferenceEquals(node, newParent) || IsReachable(node.Id, newParent.Id))
                throw MutationError("Move", node, newParent, "the move would create a cycle");

            FaultTreeNodeBase? before = null;
            if (beforeSiblingId.HasValue)
            {
                before = RequireNode(beforeSiblingId.Value, "Move", "target sibling");
                if (!ReferenceEquals(before.Parent, newParent))
                    throw MutationError("Move", node, before, "the target sibling is not a child of the destination parent");
                if (ReferenceEquals(before, node)) return;
            }

            var snapshot = CaptureMutationSnapshot();
            FaultTreeNodeBase oldParent = node.Parent!;
            int oldIndex = oldParent.MutableChildren.IndexOf(node);
            int newIndex = before == null ? newParent.MutableChildren.Count : newParent.MutableChildren.IndexOf(before);
            try
            {
                oldParent.MutableChildren.RemoveAt(oldIndex);
                if (ReferenceEquals(oldParent, newParent) && oldIndex < newIndex) newIndex--;
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
        /// <param name="policy">The policy for internal transfers targeting the selected subtree.</param>
        /// <exception cref="InvalidOperationException">Thrown when the root is selected or the policy would leave a dangling transfer.</exception>
        public void Delete(Guid nodeId, TreeDeletePolicy policy = TreeDeletePolicy.RejectIfReferenced)
        {
            DeleteTransactional(nodeId, policy);
        }

        /// <summary>Removes one subtree after reference policy has been resolved.</summary>
        /// <param name="node">The subtree root.</param>
        private void RemoveSubtree(FaultTreeNodeBase node)
        {
            node.Parent!.MutableChildren.Remove(node);
            foreach (FaultTreeNodeBase descendant in DescendantsAndSelf(node).Reverse())
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
        public FaultTreeNodeBase? FindById(Guid id)
        {
            return _byId.TryGetValue(id, out var node) ? node : null;
        }

        /// <summary>Finds nodes whose names match exactly.</summary>
        /// <param name="name">The exact name.</param>
        /// <returns>The matching nodes in persistent pre-order.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the name is null.</exception>
        public IReadOnlyList<FaultTreeNodeBase> FindByName(string name)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            return DepthFirstPreOrder().Where(node => string.Equals(node.Name, name, StringComparison.Ordinal)).ToArray();
        }

        /// <summary>Finds nodes whose names match without regard to case.</summary>
        /// <param name="name">The name to match.</param>
        /// <returns>The matching nodes in persistent pre-order.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the name is null.</exception>
        public IReadOnlyList<FaultTreeNodeBase> FindByNameIgnoreCase(string name)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            return DepthFirstPreOrder().Where(node => string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        /// <summary>Finds nodes satisfying a predicate.</summary>
        /// <param name="predicate">The predicate.</param>
        /// <returns>The matching nodes in persistent pre-order.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the predicate is null.</exception>
        public IReadOnlyList<FaultTreeNodeBase> FindAll(Func<FaultTreeNodeBase, bool> predicate)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));
            return DepthFirstPreOrder().Where(predicate).ToArray();
        }

        /// <summary>Traverses the authored tree depth-first, parent before children.</summary>
        /// <returns>The persistent pre-order sequence.</returns>
        public IReadOnlyList<FaultTreeNodeBase> DepthFirstPreOrder()
        {
            var result = new List<FaultTreeNodeBase>();
            var stack = new Stack<FaultTreeNodeBase>();
            stack.Push(Root);
            while (stack.Count > 0)
            {
                FaultTreeNodeBase node = stack.Pop();
                result.Add(node);
                for (int i = node.Children.Count - 1; i >= 0; i--) stack.Push(node.Children[i]);
            }
            return result;
        }

        /// <summary>Traverses the authored tree depth-first, children before parent.</summary>
        /// <returns>The persistent post-order sequence.</returns>
        public IReadOnlyList<FaultTreeNodeBase> DepthFirstPostOrder()
        {
            var result = new List<FaultTreeNodeBase>();
            var stack = new Stack<(FaultTreeNodeBase Node, bool Expanded)>();
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
        public IReadOnlyList<FaultTreeNodeBase> BreadthFirst()
        {
            var result = new List<FaultTreeNodeBase>();
            var queue = new Queue<FaultTreeNodeBase>();
            queue.Enqueue(Root);
            while (queue.Count > 0)
            {
                FaultTreeNodeBase node = queue.Dequeue();
                result.Add(node);
                for (int i = 0; i < node.Children.Count; i++) queue.Enqueue(node.Children[i]);
            }
            return result;
        }

        /// <summary>Gets ancestors from immediate parent through the root.</summary>
        /// <param name="nodeId">The node id.</param>
        /// <returns>The ancestors nearest-first.</returns>
        public IReadOnlyList<FaultTreeNodeBase> GetAncestors(Guid nodeId)
        {
            FaultTreeNodeBase node = RequireNode(nodeId, "GetAncestors", "source");
            var result = new List<FaultTreeNodeBase>();
            for (FaultTreeNodeBase? parent = node.Parent; parent != null; parent = parent.Parent) result.Add(parent);
            return result;
        }

        /// <summary>Gets all structural descendants in persistent pre-order.</summary>
        /// <param name="nodeId">The subtree root.</param>
        /// <returns>The descendants, excluding the selected root.</returns>
        public IReadOnlyList<FaultTreeNodeBase> GetDescendants(Guid nodeId)
        {
            FaultTreeNodeBase node = RequireNode(nodeId, "GetDescendants", "source");
            return DescendantsAndSelf(node).Skip(1).ToArray();
        }

        /// <summary>Determines whether one node is structurally reachable from another.</summary>
        /// <param name="sourceId">The source node.</param>
        /// <param name="targetId">The prospective descendant.</param>
        /// <returns>True when the target is the source or one of its descendants.</returns>
        public bool IsReachable(Guid sourceId, Guid targetId)
        {
            FaultTreeNodeBase source = RequireNode(sourceId, "IsReachable", "source");
            RequireNode(targetId, "IsReachable", "target");
            return DescendantsAndSelf(source).Any(node => node.Id == targetId);
        }

        /// <summary>Enumerates authored terminal nodes in persistent pre-order.</summary>
        /// <returns>The terminal nodes.</returns>
        public IReadOnlyList<FaultTreeNodeBase> GetLeaves()
        {
            return DepthFirstPreOrder().Where(node => node.IsTerminal).ToArray();
        }

        /// <summary>Computes the metadata-free canonical hash of one authored subtree.</summary>
        /// <param name="nodeId">The subtree root.</param>
        /// <returns>The subtree SHA-256 hash.</returns>
        /// <exception cref="InvalidOperationException">Thrown when a transfer-bearing tree has no owner.</exception>
        public byte[] SubtreeCanonicalHash(Guid nodeId)
        {
            FaultTreeNodeBase node = RequireNode(nodeId, "SubtreeCanonicalHash", "source");
            if (_ownerResponse != null)
            {
                FaultTreeOccurrencePlan plan = ReferenceEquals(node, Root)
                    ? _ownerResponse.GetOccurrencePlan()
                    : FaultTreeOccurrencePlan.Compile(_ownerResponse, node);
                return CanonicalContentHasher.Hash(plan.Identity, CanonicalizationRules.ModelRules);
            }
            if (DescendantsAndSelf(node).Any(item => item is FaultTreeTransferNode))
                throw new InvalidOperationException("A fault tree containing transfers must be owned by a FaultTreeResponse before its subtree identity can be computed.");
            return CanonicalContentHasher.Hash(
                FaultTreeOccurrencePlan.BuildUnownedIdentity(node), CanonicalizationRules.ModelRules);
        }

        /// <summary>Compares two fault trees by compute-relevant structure and content.</summary>
        /// <param name="other">The other tree.</param>
        /// <returns>True when their projected canonical identities are byte-identical.</returns>
        public bool StructuralEquals(FaultTree? other)
        {
            return other != null && SubtreeCanonicalHash(Root.Id).AsSpan().SequenceEqual(other.SubtreeCanonicalHash(other.Root.Id));
        }

        /// <summary>Serializes the tree self-contained.</summary>
        /// <returns>The explicit node-and-input representation.</returns>
        public XElement ToXElement()
        {
            return ToXElement(RiskSerializationMode.SelfContained);
        }

        /// <summary>Serializes the tree in the selected nested-function mode.</summary>
        /// <param name="mode">The serialization mode.</param>
        /// <returns>The explicit node-and-input representation.</returns>
        public XElement ToXElement(RiskSerializationMode mode)
        {
            var element = new XElement(nameof(FaultTree));
            element.SetAttributeValue("TopNodeId", Root.Id.ToString("D"));
            var nodes = new XElement("Nodes");
            foreach (FaultTreeNodeBase node in _nodes)
            {
                var serialized = new XElement(node.SerializedName);
                serialized.SetAttributeValue("Id", node.Id.ToString("D"));
                serialized.SetAttributeValue("Name", node.Name);
                serialized.SetAttributeValue("Description", node.Description);
                if (node is FaultTreeGateNode gate)
                {
                    serialized.SetAttributeValue(nameof(FaultTreeGateNode.GateType), gate.GateType.ToString());
                    if (gate.GateType == FaultTreeGateType.KOfN)
                        serialized.SetAttributeValue(nameof(FaultTreeGateNode.K), gate.K.ToString(CultureInfo.InvariantCulture));
                }
                if (node is FaultTreeHouseEventNode house)
                    serialized.SetAttributeValue(nameof(FaultTreeHouseEventNode.State), house.State);
                if (node is FaultTreeBasicEventNode basic) serialized.Add(basic.ProbabilitySource.ToXElement(mode));
                if (node is FaultTreeTransferNode transfer) transfer.WriteContent(serialized, mode);
                nodes.Add(serialized);
            }
            element.Add(nodes);

            var inputs = new XElement("Inputs");
            foreach (FaultTreeNodeBase parent in _nodes)
            {
                for (int i = 0; i < parent.Children.Count; i++)
                {
                    var input = new XElement("Input");
                    input.SetAttributeValue("ParentNodeId", parent.Id.ToString("D"));
                    input.SetAttributeValue("ChildNodeId", parent.Children[i].Id.ToString("D"));
                    input.SetAttributeValue("Order", i.ToString(CultureInfo.InvariantCulture));
                    inputs.Add(input);
                }
            }
            element.Add(inputs);

            // Conditional presence: the group container exists only when groups are configured,
            // so every group-free tree keeps its byte-identical serialized form.
            if (_ccfGroups.Count > 0)
            {
                var groups = new XElement("CcfGroups");
                for (int i = 0; i < _ccfGroups.Count; i++) groups.Add(_ccfGroups[i].ToXElement());
                element.Add(groups);
            }
            return element;
        }

        /// <summary>Validates authored structure; gate arity and sources are validated on the expanded plan.</summary>
        /// <returns>Deterministically ordered diagnostics.</returns>
        internal List<string> Validate()
        {
            var messages = new List<string>();
            if (_nodes.Any(node => node.Owner != this)) messages.Add("Error: The fault tree contains a node with inconsistent ownership.");
            if (FindCycle() != null) messages.Add("Error: The fault tree contains a structural cycle.");
            foreach (FaultTreeNodeBase parent in DepthFirstPreOrder())
            {
                if (parent is not FaultTreeGateNode && parent.Children.Count != 0)
                    messages.Add($"Error: Fault-tree node '{parent.Name}' owns inputs but is not a gate.");
            }

            var claimed = new Dictionary<Guid, FaultTreeCcfGroup>();
            for (int i = 0; i < _ccfGroups.Count; i++)
            {
                FaultTreeCcfGroup group = _ccfGroups[i];
                messages.AddRange(group.GetConfigurationErrors(this));
                foreach (Guid memberId in group.MemberNodeIds)
                {
                    if (claimed.TryGetValue(memberId, out FaultTreeCcfGroup? first))
                    {
                        if (!ReferenceEquals(first, group))
                            messages.Add($"Error: Fault-tree CCF groups '{first.Name}' and '{group.Name}' both claim member node '{memberId:D}'; a basic event can belong to at most one group.");
                    }
                    else
                    {
                        claimed.Add(memberId, group);
                    }
                }
            }
            return messages;
        }

        /// <summary>The configured parametric common-cause failure groups, in insertion order.</summary>
        public IReadOnlyList<FaultTreeCcfGroup> CcfGroups => _ccfGroups;

        /// <summary>
        /// Adds one parametric common-cause failure group. Configuring a group is deliberate
        /// compute content: the expanded plan replaces each member basic event with its derived
        /// independent and common-cause events, moving the tree's canonical identity.
        /// </summary>
        /// <param name="group">The group.</param>
        /// <exception cref="ArgumentNullException">Thrown when the group is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the same group instance is already attached.</exception>
        public void AddCcfGroup(FaultTreeCcfGroup group)
        {
            if (group == null) throw new ArgumentNullException(nameof(group));
            if (_ccfGroups.Any(existing => ReferenceEquals(existing, group)))
                throw new InvalidOperationException($"Fault-tree CCF group '{group.Name}' is already attached to this tree.");
            _ccfGroups.Add(group);
            group.PropertyChanged += CcfGroupPropertyChanged;
            NotifyStructureChanged();
        }

        /// <summary>Removes one attached parametric common-cause failure group.</summary>
        /// <param name="group">The attached group.</param>
        /// <returns>True when the group was attached and removed.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the group is null.</exception>
        public bool RemoveCcfGroup(FaultTreeCcfGroup group)
        {
            if (group == null) throw new ArgumentNullException(nameof(group));
            int index = _ccfGroups.FindIndex(existing => ReferenceEquals(existing, group));
            if (index < 0) return false;
            _ccfGroups.RemoveAt(index);
            group.PropertyChanged -= CcfGroupPropertyChanged;
            NotifyStructureChanged();
            return true;
        }

        /// <summary>Finds the group claiming one basic event, with the event's member position.</summary>
        /// <param name="node">The basic event.</param>
        /// <param name="memberIndex">The event's position in the group's member order.</param>
        /// <returns>The claiming group, or null when the event is ungrouped.</returns>
        internal FaultTreeCcfGroup? FindCcfGroup(FaultTreeBasicEventNode node, out int memberIndex)
        {
            for (int i = 0; i < _ccfGroups.Count; i++)
            {
                IReadOnlyList<Guid> members = _ccfGroups[i].MemberNodeIds;
                for (int j = 0; j < members.Count; j++)
                {
                    if (members[j] == node.Id)
                    {
                        memberIndex = j;
                        return _ccfGroups[i];
                    }
                }
            }
            memberIndex = -1;
            return null;
        }

        /// <summary>Resolves one authored node by its persistent id.</summary>
        /// <param name="id">The persistent node id.</param>
        /// <returns>The node, or null when the id is unknown.</returns>
        internal FaultTreeNodeBase? FindNode(Guid id)
        {
            return _byId.TryGetValue(id, out FaultTreeNodeBase? node) ? node : null;
        }

        /// <summary>Invalidates the owner plan after a compute-relevant group edit.</summary>
        /// <param name="sender">The edited group.</param>
        /// <param name="e">The property-change payload.</param>
        private void CcfGroupPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(FaultTreeCcfGroup.Name)
                || e.PropertyName == nameof(FaultTreeCcfGroup.Description)) return;
            _ownerResponse?.NotifyTreeComputeChanged();
        }

        /// <summary>Builds the entire tree's projected identity.</summary>
        /// <returns>The metadata-free projected tree.</returns>
        /// <exception cref="InvalidOperationException">Thrown when a transfer-bearing tree has no owner.</exception>
        internal XElement ToIdentityXElement()
        {
            if (_ownerResponse != null) return _ownerResponse.GetOccurrencePlan().Identity;
            if (_nodes.Any(node => node is FaultTreeTransferNode))
                throw new InvalidOperationException("A fault tree containing transfers must be owned by a FaultTreeResponse before its identity can be computed.");
            return new XElement(nameof(FaultTree), FaultTreeOccurrencePlan.BuildUnownedIdentity(Root));
        }

        /// <summary>Associates this controlled tree with its sole response-function owner.</summary>
        /// <param name="owner">The owning response.</param>
        /// <exception cref="ArgumentNullException">Thrown when the owner is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when a different owner is already attached.</exception>
        internal void AttachOwner(FaultTreeResponse owner)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            if (_ownerResponse != null && !ReferenceEquals(_ownerResponse, owner))
                throw new InvalidOperationException("A FaultTree cannot be owned by more than one FaultTreeResponse.");
            _ownerResponse = owner;
        }

        /// <summary>Observes one attached node for compute-relevant property replacement.</summary>
        /// <param name="node">The attached node.</param>
        private void SubscribeNode(FaultTreeNodeBase node)
        {
            if (_subscribedNodes.Add(node)) node.PropertyChanged += NodePropertyChanged;
        }

        /// <summary>Stops observing one detached node.</summary>
        /// <param name="node">The detached node.</param>
        private void UnsubscribeNode(FaultTreeNodeBase node)
        {
            if (_subscribedNodes.Remove(node)) node.PropertyChanged -= NodePropertyChanged;
        }

        /// <summary>Reconciles node subscriptions after transactional topology restoration.</summary>
        private void ReconcileNodeSubscriptions()
        {
            var current = new HashSet<FaultTreeNodeBase>(_nodes, ReferenceEqualityComparer.Instance);
            foreach (FaultTreeNodeBase node in _subscribedNodes.Where(node => !current.Contains(node)).ToArray())
                UnsubscribeNode(node);
            for (int i = 0; i < _nodes.Count; i++) SubscribeNode(_nodes[i]);
        }

        /// <summary>Invalidates the owner plan after a direct compute-relevant node edit.</summary>
        /// <param name="sender">The edited node.</param>
        /// <param name="e">The property-change payload.</param>
        private void NodePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(FaultTreeNodeBase.Name)
                || e.PropertyName == nameof(FaultTreeNodeBase.Description)) return;
            _ownerResponse?.NotifyTreeComputeChanged();
        }

        /// <summary>Invalidates the owner plan after one validated structural mutation.</summary>
        private void NotifyStructureChanged()
        {
            _ownerResponse?.NotifyTreeComputeChanged();
        }

        /// <summary>Attaches one new node after all mutation checks have passed.</summary>
        /// <param name="node">The new node.</param>
        /// <param name="parent">The parent gate, or null for initial root construction.</param>
        /// <param name="childIndex">The input insertion index.</param>
        private void AttachNewNode(FaultTreeNodeBase node, FaultTreeNodeBase? parent, int childIndex)
        {
            _nodes.Add(node);
            _byId.Add(node.Id, node);
            node.Attach(this, parent);
            SubscribeNode(node);
            if (parent != null) parent.MutableChildren.Insert(childIndex, node);
        }

        /// <summary>Validates a detached child before adding it to the selected parent.</summary>
        /// <param name="parent">The selected parent.</param>
        /// <param name="node">The new child.</param>
        /// <param name="operation">The operation name.</param>
        private void ValidateNewChild(FaultTreeNodeBase parent, FaultTreeNodeBase node, string operation)
        {
            if (parent is not FaultTreeGateNode) throw MutationError(operation, node, parent, "only fault-tree gates own inputs");
            if (node.Owner != null || node.Parent != null) throw MutationError(operation, node, parent, "the source node is already attached");
            if (_byId.ContainsKey(node.Id)) throw MutationError(operation, node, parent, "the source id already exists in the tree");
        }

        /// <summary>Requires a node id and produces a deterministic operation diagnostic when absent.</summary>
        /// <param name="id">The required id.</param>
        /// <param name="operation">The operation name.</param>
        /// <param name="role">The node role.</param>
        /// <returns>The resolved node.</returns>
        private FaultTreeNodeBase RequireNode(Guid id, string operation, string role)
        {
            if (_byId.TryGetValue(id, out var node)) return node;
            throw new InvalidOperationException($"FaultTree {operation} failed: {role} node '{id:D}' was not found.");
        }

        /// <summary>Builds a deterministic transactional-mutation exception.</summary>
        /// <param name="operation">The operation name.</param>
        /// <param name="source">The source node.</param>
        /// <param name="target">The target node.</param>
        /// <param name="reason">The rejection reason.</param>
        /// <returns>The exception.</returns>
        private static InvalidOperationException MutationError(string operation, FaultTreeNodeBase source, FaultTreeNodeBase target, string reason)
        {
            return new InvalidOperationException(
                $"FaultTree {operation} failed: source '{source.Name}' ({source.Id:D}), target '{target.Name}' ({target.Id:D}); {reason}.");
        }

        /// <summary>Enumerates a subtree iteratively, root first.</summary>
        /// <param name="root">The subtree root.</param>
        /// <returns>The pre-order sequence.</returns>
        private static IEnumerable<FaultTreeNodeBase> DescendantsAndSelf(FaultTreeNodeBase root)
        {
            var stack = new Stack<FaultTreeNodeBase>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                FaultTreeNodeBase node = stack.Pop();
                yield return node;
                for (int i = node.Children.Count - 1; i >= 0; i--) stack.Push(node.Children[i]);
            }
        }

        /// <summary>Finds a structural cycle, defensively guarding deserialized or corrupted state.</summary>
        /// <returns>A cycle-closing node, or null.</returns>
        private FaultTreeNodeBase? FindCycle()
        {
            var active = new HashSet<Guid>();
            var complete = new HashSet<Guid>();
            var stack = new Stack<(FaultTreeNodeBase Node, int ChildIndex)>();
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
                FaultTreeNodeBase child = frame.Node.Children[frame.ChildIndex];
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
        /// <exception cref="InvalidOperationException">Thrown when the node kind or content is malformed.</exception>
        private static FaultTreeNodeBase ReadNode(XElement element, IRiskFunctionResolver? resolver, string ownerName)
        {
            Guid id = ReadRequiredGuid(element, "Id");
            string name = SerializationUtilities.ReadString(element, "Name");
            string description = SerializationUtilities.ReadString(element, "Description");
            switch (element.Name.LocalName)
            {
                case nameof(FaultTreeGateNode):
                    return new FaultTreeGateNode(id, name, description,
                        SerializationUtilities.ReadEnum(element, nameof(FaultTreeGateNode.GateType), FaultTreeGateType.And),
                        element.Attribute(nameof(FaultTreeGateNode.K)) == null
                            ? 0 : SerializationUtilities.ReadInt32(element, nameof(FaultTreeGateNode.K)));
                case nameof(FaultTreeBasicEventNode):
                    return new FaultTreeBasicEventNode(id, name, description,
                        new ProbabilitySource(element.Element(nameof(ProbabilitySource))
                            ?? throw new InvalidOperationException($"Serialized basic event '{name}' has no probability source."), resolver, ownerName, "fault-tree"));
                case nameof(FaultTreeHouseEventNode):
                    return new FaultTreeHouseEventNode(id, name, description,
                        SerializationUtilities.ReadBoolean(element, nameof(FaultTreeHouseEventNode.State)));
                case nameof(FaultTreeTransferNode):
                {
                    var content = FaultTreeTransferNode.ReadContent(element, resolver, ownerName);
                    return new FaultTreeTransferNode(id, name, description, content.Mode,
                        content.Target, content.Function, content.Unresolved);
                }
                default:
                    throw new InvalidOperationException($"Unsupported serialized fault-tree node '{element.Name.LocalName}'.");
            }
        }

        /// <summary>Checks the expanded reference graph after a mutation when this tree has an owner.</summary>
        private void EnsureExpandedGraphAcyclic()
        {
            if (_ownerResponse != null) FaultTreeOccurrencePlan.Compile(_ownerResponse);
        }

        /// <summary>Reads a required persistent Guid attribute.</summary>
        /// <param name="element">The containing element.</param>
        /// <param name="attributeName">The attribute name.</param>
        /// <returns>The parsed id.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the attribute is missing or empty.</exception>
        private static Guid ReadRequiredGuid(XElement element, string attributeName)
        {
            if (Guid.TryParse(element.Attribute(attributeName)?.Value, out Guid id) && id != Guid.Empty) return id;
            throw new InvalidOperationException($"Serialized element '{element.Name.LocalName}' has no valid {attributeName}.");
        }
    }
}
