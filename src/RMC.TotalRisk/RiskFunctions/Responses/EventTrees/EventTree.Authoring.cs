using System;
using System.Collections.Generic;
using System.Linq;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees.Support;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees.Nodes;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees.Support;

namespace RMC.TotalRisk.RiskFunctions.Responses.EventTrees
{
    /// <content>
    /// Controlled fragment, replacement, materialization, pruning, reference-query, and
    /// transactional rollback operations for <see cref="EventTree"/>.
    /// </content>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// </remarks>
    public sealed partial class EventTree
    {
        /// <summary>Copies one authored subtree into an immutable in-memory fragment.</summary>
        /// <param name="nodeId">The non-root subtree identifier.</param>
        /// <returns>The immutable fragment snapshot.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the node is missing, is the initiating root, or contains an unresolved reference.</exception>
        public TreeFragment Copy(Guid nodeId)
        {
            EventNodeBase root = RequireNode(nodeId, "Copy", "source");
            if (ReferenceEquals(root, Root))
                throw MutationError("Copy", root, Root, "the initiating root is a structural container and cannot be copied as a branch subtree");

            EventNodeBase[] nodes = DescendantsAndSelf(root).ToArray();
            var snapshots = new List<EventTreeFragmentNodeSnapshot>(nodes.Length);
            for (int i = 0; i < nodes.Length; i++) snapshots.Add(CreateFragmentSnapshot(nodes[i]));
            var payload = new EventTreeFragmentPayload(this, _ownerResponse, snapshots);
            return new TreeFragment(root.Id, nodes.Select(node => node.Id), payload);
        }

        /// <summary>Pastes a deep clone of a fragment beneath an authored parent.</summary>
        /// <param name="parentId">The destination parent identifier.</param>
        /// <param name="fragment">The immutable fragment.</param>
        /// <param name="beforeSiblingId">An optional destination sibling before which to insert.</param>
        /// <returns>The fresh persistent identifier of the pasted subtree root.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the fragment is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the fragment or destination is invalid.</exception>
        public Guid PasteClone(Guid parentId, TreeFragment fragment, Guid? beforeSiblingId = null)
        {
            if (fragment == null) throw new ArgumentNullException(nameof(fragment));
            EventNodeBase parent = RequireNode(parentId, "PasteClone", "target parent");
            if (parent is EventTreeLinkNode)
                throw MutationError("PasteClone", parent, parent, "an event-tree link cannot own authored children");
            EventNodeBase? before = ResolveDestinationSibling(parent, beforeSiblingId, "PasteClone");
            EventNodeBase cloneRoot = BuildFragmentClone(fragment);
            ValidateRemainderPlacement(parent, cloneRoot, before, "PasteClone");

            int insertionIndex = ResolveInsertionIndex(parent, cloneRoot, before);
            var snapshot = CaptureMutationSnapshot();
            try
            {
                AttachDetachedSubtree(cloneRoot, parent, insertionIndex);
                EnsureExpandedGraphAcyclic();
                NotifyStructureChanged();
                return cloneRoot.Id;
            }
            catch (Exception ex)
            {
                RestoreMutationSnapshot(snapshot);
                if (ex is InvalidOperationException || ex is ArgumentException)
                    throw MutationError("PasteClone", cloneRoot, parent, ex.Message);
                throw;
            }
        }

        /// <summary>Replaces one authored subtree with a freshly identified fragment clone.</summary>
        /// <param name="nodeId">The subtree root to replace.</param>
        /// <param name="replacement">The replacement fragment.</param>
        /// <param name="referencePolicy">The explicit policy for internal links targeting the removed subtree.</param>
        /// <returns>The fresh persistent identifier of the replacement root.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the replacement is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when replacement would violate an invariant.</exception>
        public Guid ReplaceSubtree(Guid nodeId, TreeFragment replacement,
            TreeDeletePolicy referencePolicy = TreeDeletePolicy.RejectIfReferenced)
        {
            if (replacement == null) throw new ArgumentNullException(nameof(replacement));
            if (!Enum.IsDefined(referencePolicy)) throw new ArgumentOutOfRangeException(nameof(referencePolicy));
            EventNodeBase node = RequireNode(nodeId, "ReplaceSubtree", "source");
            if (ReferenceEquals(node, Root))
                throw MutationError("ReplaceSubtree", node, Root, "the initiating root cannot be replaced");

            EventNodeBase replacementRoot = BuildFragmentClone(replacement);
            var removedIds = new HashSet<Guid>(DescendantsAndSelf(node).Select(item => item.Id));
            EventTreeLinkNode[] incoming = FindInternalIncomingReferences(removedIds);
            if (incoming.Length > 0 && referencePolicy == TreeDeletePolicy.RejectIfReferenced)
                throw MutationError("ReplaceSubtree", node, incoming[0],
                    $"the subtree is referenced by internal link '{incoming[0].Name}'");

            var snapshot = CaptureMutationSnapshot();
            try
            {
                ApplyIncomingReferencePolicy(node, removedIds, referencePolicy);
                EventNodeBase parent = node.Parent!;
                int insertionIndex = parent.MutableChildren.IndexOf(node);
                RemoveSubtree(node);
                ValidateRemainderPlacement(parent, replacementRoot, null, "ReplaceSubtree");
                insertionIndex = Math.Min(insertionIndex, parent.MutableChildren.Count);
                if (replacementRoot is not RemainderNode)
                {
                    int remainderIndex = parent.MutableChildren.FindIndex(child => child is RemainderNode);
                    if (remainderIndex >= 0) insertionIndex = Math.Min(insertionIndex, remainderIndex);
                }
                AttachDetachedSubtree(replacementRoot, parent, insertionIndex);
                EnsureExpandedGraphAcyclic();
                NotifyStructureChanged();
                return replacementRoot.Id;
            }
            catch (Exception ex)
            {
                RestoreMutationSnapshot(snapshot);
                if (ex is InvalidOperationException || ex is ArgumentException)
                    throw MutationError("ReplaceSubtree", node, node.Parent ?? Root, ex.Message);
                throw;
            }
        }

        /// <summary>
        /// Replaces one internal or external link of either mode with an explicit deep clone.
        /// Materializing a shared-logical link deliberately converts it to an independently
        /// authored copy, so its sampled values move away from the source limb's shared draws.
        /// </summary>
        /// <param name="linkNodeId">The authored link identifier.</param>
        /// <returns>The fresh persistent identifier of the materialized subtree root.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the link or its target is invalid.</exception>
        public Guid MaterializeLink(Guid linkNodeId)
        {
            EventNodeBase node = RequireNode(linkNodeId, "MaterializeLink", "source");
            if (node is not EventTreeLinkNode link)
                throw MutationError("MaterializeLink", node, node, "the selected node is not an event-tree link");

            var snapshot = CaptureMutationSnapshot();
            try
            {
                Guid rootId = MaterializeLinkCore(link);
                EnsureExpandedGraphAcyclic();
                NotifyStructureChanged();
                return rootId;
            }
            catch (Exception ex)
            {
                RestoreMutationSnapshot(snapshot);
                if (ex is InvalidOperationException || ex is ArgumentException)
                    throw MutationError("MaterializeLink", link, link, ex.Message);
                throw;
            }
        }

        /// <summary>Gets effective expanded occurrences in deterministic parent-before-child order.</summary>
        /// <returns>
        /// Function-plus-node references in topological order. Independent links repeat their
        /// target occurrences; external occurrences carry their function identity.
        /// </returns>
        /// <exception cref="InvalidOperationException">Thrown when a linked tree has no owner or a target is invalid.</exception>
        public IReadOnlyList<TreeNodeReference> GetTopologicalOrder()
        {
            if (_ownerResponse == null)
            {
                if (_nodes.Any(node => node is EventTreeLinkNode))
                    throw new InvalidOperationException(
                        "An event tree containing links must be owned by an EventTreeResponse before its expanded topological order can be computed.");
                return BreadthFirst().Select(node => new TreeNodeReference(null, node.Id, nodeName: node.Name)).ToArray();
            }

            EventTreeOccurrencePlan plan = _ownerResponse.GetOccurrencePlan();
            var result = new List<TreeNodeReference>(plan.CanonicalPreOrder.Count);
            var queue = new Queue<EventTreeOccurrenceNode>();
            queue.Enqueue(plan.Root);
            while (queue.Count > 0)
            {
                EventTreeOccurrenceNode occurrence = queue.Dequeue();
                bool external = !ReferenceEquals(occurrence.SourceFunction, _ownerResponse);
                result.Add(new TreeNodeReference(external ? occurrence.SourceFunction.Id : null,
                    occurrence.SourceNode.Id, external ? occurrence.SourceFunction.Name : null,
                    occurrence.SourceNode.Name));
                foreach (EventTreeOccurrenceNode child in occurrence.Children
                    .OrderBy(item => item.AuthoredSiblingOrder)
                    .ThenBy(item => item.CanonicalPath, StringComparer.Ordinal))
                    queue.Enqueue(child);
            }
            return result;
        }

        /// <summary>Gets authored nodes that are not reachable from the root through children or local link targets.</summary>
        /// <returns>The unreachable nodes in persistent insertion order.</returns>
        public IReadOnlyList<EventNodeBase> GetUnreachableNodes()
        {
            HashSet<Guid> reachable = CollectReachableNodeIds();
            return _nodes.Where(node => !reachable.Contains(node.Id)).ToArray();
        }

        /// <summary>Atomically removes every authored node currently unreachable from the root.</summary>
        /// <returns>The removed persistent identifiers in insertion order, which also serve as the prune preview.</returns>
        public IReadOnlyList<Guid> PruneUnreachable()
        {
            EventNodeBase[] unreachable = GetUnreachableNodes().ToArray();
            if (unreachable.Length == 0) return Array.Empty<Guid>();
            var removedIds = new HashSet<Guid>(unreachable.Select(node => node.Id));
            Guid[] preview = unreachable.Select(node => node.Id).ToArray();
            var snapshot = CaptureMutationSnapshot();
            try
            {
                for (int i = 0; i < _nodes.Count; i++)
                    _nodes[i].MutableChildren.RemoveAll(child => removedIds.Contains(child.Id));
                for (int i = 0; i < unreachable.Length; i++)
                {
                    _byId.Remove(unreachable[i].Id);
                    _nodes.Remove(unreachable[i]);
                    unreachable[i].MutableChildren.Clear();
                    UnsubscribeNode(unreachable[i]);
                    unreachable[i].Detach();
                }
                EnsureExpandedGraphAcyclic();
                NotifyStructureChanged();
                return preview;
            }
            catch (Exception ex)
            {
                RestoreMutationSnapshot(snapshot);
                if (ex is InvalidOperationException || ex is ArgumentException)
                    throw MutationError("PruneUnreachable", unreachable[0], Root, ex.Message);
                throw;
            }
        }

        /// <summary>Gets every authored internal structural reference of either link mode.</summary>
        /// <returns>The internal links in persistent insertion order.</returns>
        public IReadOnlyList<EventTreeLinkNode> GetInternalReferences()
        {
            return _nodes.OfType<EventTreeLinkNode>().Where(link => !link.IsExternal).ToArray();
        }

        /// <summary>Gets every authored external structural reference of either link mode.</summary>
        /// <returns>The external links in persistent insertion order.</returns>
        public IReadOnlyList<EventTreeLinkNode> GetExternalReferences()
        {
            return _nodes.OfType<EventTreeLinkNode>().Where(link => link.IsExternal).ToArray();
        }

        /// <summary>Gets links authored in this tree that resolve to one local node.</summary>
        /// <param name="nodeId">The local target-node identifier.</param>
        /// <returns>Internal links, plus explicit external wrappers back to this response, in insertion order.</returns>
        public IReadOnlyList<EventTreeLinkNode> GetIncomingReferences(Guid nodeId)
        {
            EventNodeBase target = RequireNode(nodeId, "GetIncomingReferences", "target");
            var result = new List<EventTreeLinkNode>();
            foreach (EventTreeLinkNode link in _nodes.OfType<EventTreeLinkNode>())
            {
                EventTree targetTree = link.TargetFunction?.EventTree ?? this;
                if (!ReferenceEquals(targetTree, this)) continue;
                EventNodeBase? resolved = link.ResolveNode(targetTree, out _);
                if (ReferenceEquals(resolved, target)) result.Add(link);
            }
            return result;
        }

        /// <summary>Gets all internal and external links structurally authored in one subtree.</summary>
        /// <param name="nodeId">The source subtree identifier.</param>
        /// <returns>The outgoing links in deterministic structural pre-order.</returns>
        public IReadOnlyList<EventTreeLinkNode> GetOutgoingReferences(Guid nodeId)
        {
            EventNodeBase source = RequireNode(nodeId, "GetOutgoingReferences", "source");
            return DescendantsAndSelf(source).OfType<EventTreeLinkNode>().ToArray();
        }

        /// <summary>Completes the public delete operation with all three reference policies.</summary>
        /// <param name="nodeId">The subtree root to remove.</param>
        /// <param name="policy">The internal-reference policy.</param>
        private void DeleteTransactional(Guid nodeId, TreeDeletePolicy policy)
        {
            if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
            EventNodeBase node = RequireNode(nodeId, "Delete", "source");
            if (ReferenceEquals(node, Root)) throw MutationError("Delete", node, Root, "the initiating root cannot be deleted");
            var removedIds = new HashSet<Guid>(DescendantsAndSelf(node).Select(item => item.Id));
            EventTreeLinkNode[] incoming = FindInternalIncomingReferences(removedIds);
            if (incoming.Length > 0 && policy == TreeDeletePolicy.RejectIfReferenced)
                throw MutationError("Delete", node, incoming[0],
                    $"the subtree is referenced by internal link '{incoming[0].Name}'");

            var snapshot = CaptureMutationSnapshot();
            try
            {
                ApplyIncomingReferencePolicy(node, removedIds, policy);
                RemoveSubtree(node);
                EnsureExpandedGraphAcyclic();
                NotifyStructureChanged();
            }
            catch (Exception ex)
            {
                RestoreMutationSnapshot(snapshot);
                if (ex is InvalidOperationException || ex is ArgumentException)
                    throw MutationError("Delete", node, node, ex.Message);
                throw;
            }
        }

        /// <summary>Applies cascade or materialization to internal links targeting a removal set.</summary>
        /// <param name="source">The selected removal root used in diagnostics.</param>
        /// <param name="removedIds">The persistent identifiers scheduled for removal.</param>
        /// <param name="policy">The selected reference policy.</param>
        private void ApplyIncomingReferencePolicy(EventNodeBase source, HashSet<Guid> removedIds,
            TreeDeletePolicy policy)
        {
            if (policy == TreeDeletePolicy.RejectIfReferenced) return;
            if (policy == TreeDeletePolicy.CascadeLinks)
            {
                EventTreeLinkNode[] incoming = FindInternalIncomingReferences(removedIds);
                for (int i = 0; i < incoming.Length; i++) RemoveSubtree(incoming[i]);
                return;
            }

            while (true)
            {
                EventTreeLinkNode? incoming = FindInternalIncomingReferences(removedIds).FirstOrDefault();
                if (incoming == null) return;
                try
                {
                    MaterializeLinkCore(incoming);
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException)
                {
                    throw MutationError("Delete", source, incoming,
                        $"the incoming link could not be materialized: {ex.Message}");
                }
            }
        }

        /// <summary>Finds authored internal links outside a removal set whose targets are inside it.</summary>
        /// <param name="removedIds">The selected removal identifiers.</param>
        /// <returns>The incoming links in persistent insertion order.</returns>
        private EventTreeLinkNode[] FindInternalIncomingReferences(HashSet<Guid> removedIds)
        {
            return _nodes.OfType<EventTreeLinkNode>()
                .Where(link => !removedIds.Contains(link.Id)
                    && !link.IsExternal
                    && removedIds.Contains(link.Target.NodeId))
                .ToArray();
        }

        /// <summary>Materializes one resolved link without opening or closing a transaction.</summary>
        /// <param name="link">The attached link.</param>
        /// <returns>The materialized subtree-root identifier.</returns>
        private Guid MaterializeLinkCore(EventTreeLinkNode link)
        {
            (EventTree targetTree, EventNodeBase targetNode) = ResolveLinkTarget(link);
            if (_ownerResponse != null) _ = _ownerResponse.GetBranches();
            TreeFragment fragment = targetTree.Copy(targetNode.Id);
            EventNodeBase cloneRoot = BuildFragmentClone(fragment,
                out IReadOnlyDictionary<Guid, Guid> freshIds);
            RemapMaterializedBranchPaths(link.Id, freshIds);
            if (cloneRoot.IsTerminal)
            {
                cloneRoot.IsFailure = link.IsFailure;
                cloneRoot.Name = link.Name;
                cloneRoot.Description = link.Description;
            }

            EventNodeBase parent = link.Parent
                ?? throw MutationError("MaterializeLink", link, link, "the link has no authored parent");
            int insertionIndex = parent.MutableChildren.IndexOf(link);
            RemoveSubtree(link);
            ValidateRemainderPlacement(parent, cloneRoot, null, "MaterializeLink");
            insertionIndex = Math.Min(insertionIndex, parent.MutableChildren.Count);
            if (cloneRoot is not RemainderNode)
            {
                int remainderIndex = parent.MutableChildren.FindIndex(child => child is RemainderNode);
                if (remainderIndex >= 0) insertionIndex = Math.Min(insertionIndex, remainderIndex);
            }
            AttachDetachedSubtree(cloneRoot, parent, insertionIndex);
            return cloneRoot.Id;
        }

        /// <summary>Resolves one internal or external link target with an actionable diagnostic.</summary>
        /// <param name="link">The link to resolve.</param>
        /// <returns>The target tree and subtree root.</returns>
        private (EventTree Tree, EventNodeBase Node) ResolveLinkTarget(EventTreeLinkNode link)
        {
            if (link.UnresolvedReferences.Count > 0)
                throw MutationError("MaterializeLink", link, link,
                    $"the external target is unresolved ({string.Join(", ", link.UnresolvedReferences)})");
            EventTree targetTree = link.TargetFunction?.EventTree ?? this;
            EventNodeBase? target = link.ResolveNode(targetTree, out _);
            if (target == null)
            {
                TreeNodeReference address = link.Target;
                throw MutationError("MaterializeLink", link, link,
                    $"target node '{address.NodeName ?? address.NodeId.ToString("D")}' could not be resolved");
            }
            if (target is InitiatingNode)
                throw MutationError("MaterializeLink", link, target,
                    "an initiating node cannot be materialized as a branch subtree");
            return (targetTree, target);
        }

        /// <summary>Builds an immutable event-tree node snapshot for a fragment.</summary>
        /// <param name="node">The source authored node.</param>
        /// <returns>The owned snapshot.</returns>
        private static EventTreeFragmentNodeSnapshot CreateFragmentSnapshot(EventNodeBase node)
        {
            ProbabilitySource? probabilitySource = node is ChanceNode chance
                ? chance.ProbabilitySource.CloneForFragment()
                : null;
            TreeNodeReference? target = null;
            EventTreeResponse? targetFunction = null;
            TreeLinkMode linkMode = TreeLinkMode.IndependentClone;
            if (node is EventTreeLinkNode link)
            {
                if (link.IsExternal && link.TargetFunction == null)
                    throw new InvalidOperationException(
                        $"EventTree Copy failed: external link '{link.Name}' has no resolved target function.");
                target = link.Target;
                targetFunction = link.TargetFunction;
                linkMode = link.LinkMode;
            }
            return new EventTreeFragmentNodeSnapshot(node.Id, node.SerializedName, node.Name,
                node.Description, node.IsFailure, probabilitySource, target, targetFunction,
                linkMode, node.Children.Select(child => child.Id).ToArray());
        }

        /// <summary>Builds one fresh detached subtree from an event-tree fragment.</summary>
        /// <param name="fragment">The immutable fragment.</param>
        /// <returns>The detached clone root.</returns>
        private EventNodeBase BuildFragmentClone(TreeFragment fragment)
        {
            return BuildFragmentClone(fragment, out _);
        }

        /// <summary>Builds one fresh detached subtree and returns its complete id remap.</summary>
        /// <param name="fragment">The immutable fragment.</param>
        /// <param name="freshIds">The source-to-fresh persistent id map.</param>
        /// <returns>The detached clone root.</returns>
        private EventNodeBase BuildFragmentClone(TreeFragment fragment,
            out IReadOnlyDictionary<Guid, Guid> freshIds)
        {
            EventTreeFragmentPayload payload = fragment.RequirePayload<EventTreeFragmentPayload>();
            freshIds = payload.Nodes.ToDictionary(item => item.SourceId, _ => Guid.NewGuid());
            var clones = new Dictionary<Guid, EventNodeBase>();
            for (int i = 0; i < payload.Nodes.Count; i++)
            {
                EventTreeFragmentNodeSnapshot item = payload.Nodes[i];
                EventNodeBase clone = item.SerializedName switch
                {
                    nameof(ChanceNode) => new ChanceNode(freshIds[item.SourceId], item.Name,
                        item.Description, item.IsFailure, -1,
                        item.ProbabilitySource!.CloneForFragment()),
                    nameof(RemainderNode) => new RemainderNode(freshIds[item.SourceId], item.Name,
                        item.Description, item.IsFailure, -1),
                    nameof(EventTreeLinkNode) => CreateFragmentLinkClone(payload, item, freshIds),
                    nameof(InitiatingNode) => throw new InvalidOperationException(
                        "An initiating node cannot be pasted as an event-tree branch subtree."),
                    _ => throw new InvalidOperationException(
                        $"Unsupported event-tree fragment node '{item.SerializedName}'."),
                };
                clones.Add(item.SourceId, clone);
            }

            for (int i = 0; i < payload.Nodes.Count; i++)
            {
                EventTreeFragmentNodeSnapshot item = payload.Nodes[i];
                EventNodeBase clone = clones[item.SourceId];
                for (int child = 0; child < item.ChildIds.Count; child++)
                    clone.MutableChildren.Add(clones[item.ChildIds[child]]);
            }
            return clones[fragment.SourceRootId];
        }

        /// <summary>Creates a fresh link node while remapping fragment-internal targets.</summary>
        /// <param name="payload">The event-tree fragment payload.</param>
        /// <param name="item">The link snapshot.</param>
        /// <param name="freshIds">The complete source-to-fresh id map.</param>
        /// <returns>The detached link clone.</returns>
        private EventTreeLinkNode CreateFragmentLinkClone(EventTreeFragmentPayload payload,
            EventTreeFragmentNodeSnapshot item, IReadOnlyDictionary<Guid, Guid> freshIds)
        {
            TreeNodeReference sourceTarget = item.Target
                ?? throw new InvalidOperationException("An event-tree fragment link has no target address.");
            TreeNodeReference target;
            EventTreeResponse? targetFunction;
            if (!sourceTarget.FunctionId.HasValue && string.IsNullOrEmpty(sourceTarget.FunctionName))
            {
                if (freshIds.TryGetValue(sourceTarget.NodeId, out Guid remappedId))
                {
                    string? remappedName = payload.ById[sourceTarget.NodeId].Name;
                    target = new TreeNodeReference(null, remappedId, nodeName: remappedName);
                    targetFunction = null;
                }
                else if (ReferenceEquals(payload.SourceTree, this))
                {
                    if (FindById(sourceTarget.NodeId) == null)
                        throw new InvalidOperationException(
                            $"EventTree PasteClone failed: preserved internal target '{sourceTarget.NodeName ?? sourceTarget.NodeId.ToString("D")}' no longer exists.");
                    target = sourceTarget;
                    targetFunction = null;
                }
                else if (payload.SourceOwner != null)
                {
                    target = new TreeNodeReference(payload.SourceOwner.Id, sourceTarget.NodeId,
                        payload.SourceOwner.Name, sourceTarget.NodeName);
                    targetFunction = payload.SourceOwner;
                }
                else
                {
                    throw new InvalidOperationException(
                        "EventTree PasteClone failed: a cross-tree fragment contains an internal reference outside the fragment, but its source tree has no EventTreeResponse owner.");
                }
            }
            else
            {
                targetFunction = item.TargetFunction
                    ?? throw new InvalidOperationException(
                        $"EventTree PasteClone failed: external target function '{sourceTarget.FunctionName}' is unresolved.");
                target = sourceTarget;
            }

            return new EventTreeLinkNode(freshIds[item.SourceId], item.Name, item.Description,
                item.IsFailure, -1, item.LinkMode, target, targetFunction, Array.Empty<string>());
        }

        /// <summary>Attaches every node of one prebuilt detached subtree in deterministic pre-order.</summary>
        /// <param name="root">The detached subtree root.</param>
        /// <param name="parent">The attached destination parent.</param>
        /// <param name="insertionIndex">The destination child index.</param>
        private void AttachDetachedSubtree(EventNodeBase root, EventNodeBase parent, int insertionIndex)
        {
            EventNodeBase[] nodes = DescendantsAndSelf(root).ToArray();
            if (nodes.Any(node => node is InitiatingNode || node.Owner != null || node.Parent != null))
                throw MutationError("AttachSubtree", root, parent,
                    "the replacement contains an initiating or already attached node");
            if (nodes.Select(node => node.Id).Distinct().Count() != nodes.Length
                || nodes.Any(node => _byId.ContainsKey(node.Id)))
                throw MutationError("AttachSubtree", root, parent,
                    "the replacement contains a duplicate persistent id");

            parent.MutableChildren.Insert(insertionIndex, root);
            var stack = new Stack<(EventNodeBase Node, EventNodeBase Parent)>();
            stack.Push((root, parent));
            while (stack.Count > 0)
            {
                var item = stack.Pop();
                _nodes.Add(item.Node);
                _byId.Add(item.Node.Id, item.Node);
                item.Node.Attach(this, item.Parent);
                SubscribeNode(item.Node);
                if (item.Node.OutputPort < 3) AssignNextOutputPort(item.Node);
                for (int i = item.Node.Children.Count - 1; i >= 0; i--)
                    stack.Push((item.Node.Children[i], item.Node));
            }
        }

        /// <summary>Resolves and validates an optional destination sibling.</summary>
        /// <param name="parent">The selected destination parent.</param>
        /// <param name="beforeSiblingId">The optional sibling id.</param>
        /// <param name="operation">The operation name.</param>
        /// <returns>The resolved sibling, or null.</returns>
        private EventNodeBase? ResolveDestinationSibling(EventNodeBase parent, Guid? beforeSiblingId,
            string operation)
        {
            if (!beforeSiblingId.HasValue) return null;
            EventNodeBase before = RequireNode(beforeSiblingId.Value, operation, "target sibling");
            if (!ReferenceEquals(before.Parent, parent))
                throw MutationError(operation, parent, before,
                    "the target sibling is not a child of the destination parent");
            return before;
        }

        /// <summary>Resolves the presentation insertion index while keeping a remainder last.</summary>
        /// <param name="parent">The destination parent.</param>
        /// <param name="node">The prospective child.</param>
        /// <param name="before">The optional target sibling.</param>
        /// <returns>The destination index.</returns>
        private static int ResolveInsertionIndex(EventNodeBase parent, EventNodeBase node,
            EventNodeBase? before)
        {
            if (before != null) return parent.MutableChildren.IndexOf(before);
            if (node is RemainderNode) return parent.MutableChildren.Count;
            int remainderIndex = parent.MutableChildren.FindIndex(child => child is RemainderNode);
            return remainderIndex < 0 ? parent.MutableChildren.Count : remainderIndex;
        }

        /// <summary>Collects authored nodes reachable by structural edges and local link targets.</summary>
        /// <returns>The reachable persistent identifiers.</returns>
        private HashSet<Guid> CollectReachableNodeIds()
        {
            var reachable = new HashSet<Guid>();
            var stack = new Stack<EventNodeBase>();
            stack.Push(Root);
            while (stack.Count > 0)
            {
                EventNodeBase node = stack.Pop();
                if (!reachable.Add(node.Id)) continue;
                for (int i = node.Children.Count - 1; i >= 0; i--) stack.Push(node.Children[i]);
                if (node is not EventTreeLinkNode link) continue;
                EventTree targetTree = link.TargetFunction?.EventTree ?? this;
                if (!ReferenceEquals(targetTree, this)) continue;
                EventNodeBase? target = link.ResolveNode(this, out _);
                if (target != null) stack.Push(target);
            }
            return reachable;
        }

        /// <summary>Captures an opaque complete authoring checkpoint for a graph-level transaction.</summary>
        /// <returns>The opaque checkpoint.</returns>
        internal object CreateMutationCheckpoint()
        {
            return CaptureMutationSnapshot();
        }

        /// <summary>Restores a graph-level authoring checkpoint exactly.</summary>
        /// <param name="checkpoint">The checkpoint returned by <see cref="CreateMutationCheckpoint"/>.</param>
        /// <exception cref="ArgumentException">Thrown when the checkpoint was not created by this tree.</exception>
        internal void RestoreMutationCheckpoint(object checkpoint)
        {
            if (checkpoint is not TreeMutationSnapshot<EventNodeBase> snapshot)
                throw new ArgumentException("The event-tree mutation checkpoint is invalid.", nameof(checkpoint));
            RestoreMutationSnapshot(snapshot);
        }

        /// <summary>Captures every mutable topology field needed for rollback.</summary>
        /// <returns>The complete structural mutation snapshot.</returns>
        private TreeMutationSnapshot<EventNodeBase> CaptureMutationSnapshot()
        {
            return new TreeMutationSnapshot<EventNodeBase>(_nodes, node => node.Parent,
                node => node.Children,
                new EventTreePortState(new Dictionary<Guid, int>(_linkedBranchPorts),
                    new Dictionary<Guid, string>(_linkedBranchPaths), _nextOutputPort),
                _ownerResponse?.CreateCompiledPlanCheckpoint());
        }

        /// <summary>Restores topology, ownership, ids, child order, and output-port allocation after failure.</summary>
        /// <param name="snapshot">The pre-mutation state.</param>
        private void RestoreMutationSnapshot(TreeMutationSnapshot<EventNodeBase> snapshot)
        {
            var original = new HashSet<EventNodeBase>(snapshot.Nodes);
            foreach (EventNodeBase node in _nodes.Where(node => !original.Contains(node)).ToArray())
            {
                node.MutableChildren.Clear();
                node.Detach();
            }
            for (int i = 0; i < snapshot.Nodes.Count; i++) snapshot.Nodes[i].MutableChildren.Clear();
            for (int i = 0; i < snapshot.Nodes.Count; i++)
            {
                EventNodeBase node = snapshot.Nodes[i];
                node.MutableChildren.AddRange(snapshot.Children[node]);
            }

            _nodes.Clear();
            _nodes.AddRange(snapshot.Nodes);
            _byId.Clear();
            for (int i = 0; i < snapshot.Nodes.Count; i++)
            {
                EventNodeBase node = snapshot.Nodes[i];
                _byId.Add(node.Id, node);
                node.Attach(this, snapshot.Parents[node]);
            }
            var portState = (EventTreePortState)snapshot.TreeState!;
            _linkedBranchPorts.Clear();
            foreach (KeyValuePair<Guid, int> branch in portState.LinkedBranchPorts)
                _linkedBranchPorts.Add(branch.Key, branch.Value);
            _linkedBranchPaths.Clear();
            foreach (KeyValuePair<Guid, string> branch in portState.LinkedBranchPaths)
                _linkedBranchPaths.Add(branch.Key, branch.Value);
            _nextOutputPort = portState.NextOutputPort;
            ReconcileNodeSubscriptions();
            if (_ownerResponse != null && snapshot.OwnerCheckpoint != null)
                _ownerResponse.RestoreCompiledPlanCheckpoint(snapshot.OwnerCheckpoint);
        }

        /// <summary>The event-tree-specific immutable payload held by a public fragment.</summary>
        private sealed class EventTreeFragmentPayload
        {
            /// <summary>Initializes a fragment payload.</summary>
            internal EventTreeFragmentPayload(EventTree sourceTree, EventTreeResponse? sourceOwner,
                IReadOnlyList<EventTreeFragmentNodeSnapshot> nodes)
            {
                SourceTree = sourceTree;
                SourceOwner = sourceOwner;
                Nodes = nodes;
                ById = nodes.ToDictionary(node => node.SourceId);
            }

            /// <summary>The source tree used to preserve same-tree external-fragment references.</summary>
            internal EventTree SourceTree { get; }

            /// <summary>The source response used to promote cross-tree external-fragment references.</summary>
            internal EventTreeResponse? SourceOwner { get; }

            /// <summary>The immutable local snapshots in source pre-order.</summary>
            internal IReadOnlyList<EventTreeFragmentNodeSnapshot> Nodes { get; }

            /// <summary>The source-id snapshot lookup.</summary>
            internal IReadOnlyDictionary<Guid, EventTreeFragmentNodeSnapshot> ById { get; }
        }

        /// <summary>One immutable event-tree node snapshot inside a fragment.</summary>
        private sealed class EventTreeFragmentNodeSnapshot
        {
            /// <summary>Initializes a node snapshot.</summary>
            internal EventTreeFragmentNodeSnapshot(Guid sourceId, string serializedName, string name,
                string description, bool isFailure, ProbabilitySource? probabilitySource,
                TreeNodeReference? target, EventTreeResponse? targetFunction, TreeLinkMode linkMode,
                IReadOnlyList<Guid> childIds)
            {
                SourceId = sourceId;
                SerializedName = serializedName;
                Name = name;
                Description = description;
                IsFailure = isFailure;
                ProbabilitySource = probabilitySource;
                Target = target;
                TargetFunction = targetFunction;
                LinkMode = linkMode;
                ChildIds = childIds;
            }

            internal Guid SourceId { get; }

            internal string SerializedName { get; }

            internal string Name { get; }

            internal string Description { get; }

            internal bool IsFailure { get; }

            internal ProbabilitySource? ProbabilitySource { get; }

            internal TreeNodeReference? Target { get; }

            internal EventTreeResponse? TargetFunction { get; }

            internal TreeLinkMode LinkMode { get; }

            internal IReadOnlyList<Guid> ChildIds { get; }
        }

        /// <summary>The event-branch-port allocator state carried inside a mutation snapshot.</summary>
        private sealed class EventTreePortState
        {
            /// <summary>Captures the copied port allocator state.</summary>
            /// <param name="linkedBranchPorts">The copied linked-branch port map.</param>
            /// <param name="linkedBranchPaths">The copied linked-branch path map.</param>
            /// <param name="nextOutputPort">The next append-only output port.</param>
            internal EventTreePortState(Dictionary<Guid, int> linkedBranchPorts,
                Dictionary<Guid, string> linkedBranchPaths, int nextOutputPort)
            {
                LinkedBranchPorts = linkedBranchPorts;
                LinkedBranchPaths = linkedBranchPaths;
                NextOutputPort = nextOutputPort;
            }

            /// <summary>The captured linked-branch port map.</summary>
            internal IReadOnlyDictionary<Guid, int> LinkedBranchPorts { get; }

            /// <summary>The captured linked-branch path map.</summary>
            internal IReadOnlyDictionary<Guid, string> LinkedBranchPaths { get; }

            /// <summary>The captured next append-only output port.</summary>
            internal int NextOutputPort { get; }
        }
    }
}
