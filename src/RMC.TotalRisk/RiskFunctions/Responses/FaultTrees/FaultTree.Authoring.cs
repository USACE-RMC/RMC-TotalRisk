using System;
using System.Collections.Generic;
using System.Linq;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees.Support;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Nodes;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Support;

namespace RMC.TotalRisk.RiskFunctions.Responses.FaultTrees
{
    /// <content>
    /// Controlled fragment, replacement, materialization, pruning, reference-query, and
    /// transactional rollback operations for <see cref="FaultTree"/>.
    /// </content>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// </remarks>
    public sealed partial class FaultTree
    {
        /// <summary>Copies one authored subtree into an immutable in-memory fragment.</summary>
        /// <param name="nodeId">The subtree root identifier.</param>
        /// <returns>The immutable fragment snapshot.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the node is missing or contains an unresolved reference.</exception>
        public TreeFragment Copy(Guid nodeId)
        {
            FaultTreeNodeBase root = RequireNode(nodeId, "Copy", "source");
            FaultTreeNodeBase[] nodes = DescendantsAndSelf(root).ToArray();
            var snapshots = new List<FaultTreeFragmentNodeSnapshot>(nodes.Length);
            for (int i = 0; i < nodes.Length; i++) snapshots.Add(CreateFragmentSnapshot(nodes[i]));
            var payload = new FaultTreeFragmentPayload(this, _ownerResponse, snapshots);
            return new TreeFragment(root.Id, nodes.Select(node => node.Id), payload);
        }

        /// <summary>Pastes a deep clone of a fragment beneath an authored parent gate.</summary>
        /// <param name="parentGateId">The destination parent gate identifier.</param>
        /// <param name="fragment">The immutable fragment.</param>
        /// <param name="beforeSiblingId">An optional destination sibling before which to insert.</param>
        /// <returns>The fresh persistent identifier of the pasted subtree root.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the fragment is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the fragment or destination is invalid.</exception>
        public Guid PasteClone(Guid parentGateId, TreeFragment fragment, Guid? beforeSiblingId = null)
        {
            if (fragment == null) throw new ArgumentNullException(nameof(fragment));
            FaultTreeNodeBase parent = RequireNode(parentGateId, "PasteClone", "target parent");
            if (parent is not FaultTreeGateNode)
                throw MutationError("PasteClone", parent, parent, "only fault-tree gates own inputs");
            FaultTreeNodeBase? before = ResolveDestinationSibling(parent, beforeSiblingId, "PasteClone");
            FaultTreeNodeBase cloneRoot = BuildFragmentClone(fragment);

            int insertionIndex = before == null ? parent.MutableChildren.Count : parent.MutableChildren.IndexOf(before);
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
        /// <param name="referencePolicy">The explicit policy for internal transfers targeting the removed subtree.</param>
        /// <returns>The fresh persistent identifier of the replacement root.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the replacement is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the policy is undefined.</exception>
        /// <exception cref="InvalidOperationException">Thrown when replacement would violate an invariant.</exception>
        public Guid ReplaceSubtree(Guid nodeId, TreeFragment replacement,
            TreeDeletePolicy referencePolicy = TreeDeletePolicy.RejectIfReferenced)
        {
            if (replacement == null) throw new ArgumentNullException(nameof(replacement));
            if (!Enum.IsDefined(referencePolicy)) throw new ArgumentOutOfRangeException(nameof(referencePolicy));
            FaultTreeNodeBase node = RequireNode(nodeId, "ReplaceSubtree", "source");
            if (ReferenceEquals(node, Root))
                throw MutationError("ReplaceSubtree", node, Root, "the top-event gate cannot be replaced");

            FaultTreeNodeBase replacementRoot = BuildFragmentClone(replacement);
            var removedIds = new HashSet<Guid>(DescendantsAndSelf(node).Select(item => item.Id));
            FaultTreeTransferNode[] incoming = FindInternalIncomingReferences(removedIds);
            if (incoming.Length > 0 && referencePolicy == TreeDeletePolicy.RejectIfReferenced)
                throw MutationError("ReplaceSubtree", node, incoming[0],
                    $"the subtree is referenced by internal transfer '{incoming[0].Name}'");

            var snapshot = CaptureMutationSnapshot();
            try
            {
                ApplyIncomingReferencePolicy(node, removedIds, referencePolicy);
                FaultTreeNodeBase parent = node.Parent!;
                int insertionIndex = parent.MutableChildren.IndexOf(node);
                RemoveSubtree(node);
                insertionIndex = Math.Min(insertionIndex, parent.MutableChildren.Count);
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
        /// Replaces one internal or external transfer with an explicit deep clone. Materializing a
        /// shared-logical transfer converts that occurrence to an independent clone, so its
        /// repeated events stop unifying with the remaining occurrences and the computed
        /// probability may deliberately change.
        /// </summary>
        /// <param name="transferNodeId">The authored transfer identifier.</param>
        /// <returns>The fresh persistent identifier of the materialized subtree root.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the transfer or its target is invalid.</exception>
        public Guid MaterializeLink(Guid transferNodeId)
        {
            FaultTreeNodeBase node = RequireNode(transferNodeId, "MaterializeLink", "source");
            if (node is not FaultTreeTransferNode transfer)
                throw MutationError("MaterializeLink", node, node, "the selected node is not a fault-tree transfer");

            var snapshot = CaptureMutationSnapshot();
            try
            {
                Guid rootId = MaterializeLinkCore(transfer);
                EnsureExpandedGraphAcyclic();
                NotifyStructureChanged();
                return rootId;
            }
            catch (Exception ex)
            {
                RestoreMutationSnapshot(snapshot);
                if (ex is InvalidOperationException || ex is ArgumentException)
                    throw MutationError("MaterializeLink", transfer, transfer, ex.Message);
                throw;
            }
        }

        /// <summary>Gets effective expanded occurrences in deterministic parent-before-child order.</summary>
        /// <returns>
        /// Function-plus-node references in topological order. Transfers repeat their target
        /// occurrences; external occurrences carry their function identity.
        /// </returns>
        /// <exception cref="InvalidOperationException">Thrown when a transfer-bearing tree has no owner or a target is invalid.</exception>
        public IReadOnlyList<TreeNodeReference> GetTopologicalOrder()
        {
            if (_ownerResponse == null)
            {
                if (_nodes.Any(node => node is FaultTreeTransferNode))
                    throw new InvalidOperationException(
                        "A fault tree containing transfers must be owned by a FaultTreeResponse before its expanded topological order can be computed.");
                return BreadthFirst().Select(node => new TreeNodeReference(null, node.Id, nodeName: node.Name)).ToArray();
            }

            FaultTreeOccurrencePlan plan = _ownerResponse.GetOccurrencePlan();
            var result = new List<TreeNodeReference>(plan.CanonicalPreOrder.Count);
            var queue = new Queue<FaultTreeOccurrenceNode>();
            queue.Enqueue(plan.Root);
            while (queue.Count > 0)
            {
                FaultTreeOccurrenceNode occurrence = queue.Dequeue();
                bool external = !ReferenceEquals(occurrence.SourceFunction, _ownerResponse);
                result.Add(new TreeNodeReference(external ? occurrence.SourceFunction.Id : null,
                    occurrence.SourceNode.Id, external ? occurrence.SourceFunction.Name : null,
                    occurrence.SourceNode.Name));
                foreach (FaultTreeOccurrenceNode child in occurrence.Children
                    .OrderBy(item => item.AuthoredSiblingOrder)
                    .ThenBy(item => item.CanonicalPath, StringComparer.Ordinal))
                    queue.Enqueue(child);
            }
            return result;
        }

        /// <summary>Gets authored nodes that are not reachable from the root through inputs or local transfer targets.</summary>
        /// <returns>The unreachable nodes in persistent insertion order.</returns>
        public IReadOnlyList<FaultTreeNodeBase> GetUnreachableNodes()
        {
            HashSet<Guid> reachable = CollectReachableNodeIds();
            return _nodes.Where(node => !reachable.Contains(node.Id)).ToArray();
        }

        /// <summary>Atomically removes every authored node currently unreachable from the root.</summary>
        /// <returns>The removed persistent identifiers in insertion order, which also serve as the prune preview.</returns>
        public IReadOnlyList<Guid> PruneUnreachable()
        {
            FaultTreeNodeBase[] unreachable = GetUnreachableNodes().ToArray();
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

        /// <summary>Gets every authored internal transfer.</summary>
        /// <returns>The internal transfers in persistent insertion order.</returns>
        public IReadOnlyList<FaultTreeTransferNode> GetInternalReferences()
        {
            return _nodes.OfType<FaultTreeTransferNode>().Where(transfer => !transfer.IsExternal).ToArray();
        }

        /// <summary>Gets every authored external transfer.</summary>
        /// <returns>The external transfers in persistent insertion order.</returns>
        public IReadOnlyList<FaultTreeTransferNode> GetExternalReferences()
        {
            return _nodes.OfType<FaultTreeTransferNode>().Where(transfer => transfer.IsExternal).ToArray();
        }

        /// <summary>Gets transfers authored in this tree that resolve to one local node.</summary>
        /// <param name="nodeId">The local target-node identifier.</param>
        /// <returns>Internal transfers, plus explicit external wrappers back to this response, in insertion order.</returns>
        public IReadOnlyList<FaultTreeTransferNode> GetIncomingReferences(Guid nodeId)
        {
            FaultTreeNodeBase target = RequireNode(nodeId, "GetIncomingReferences", "target");
            var result = new List<FaultTreeTransferNode>();
            foreach (FaultTreeTransferNode transfer in _nodes.OfType<FaultTreeTransferNode>())
            {
                FaultTree targetTree = transfer.TargetFunction?.FaultTree ?? this;
                if (!ReferenceEquals(targetTree, this)) continue;
                FaultTreeNodeBase? resolved = transfer.ResolveNode(targetTree, out _);
                if (ReferenceEquals(resolved, target)) result.Add(transfer);
            }
            return result;
        }

        /// <summary>Gets all internal and external transfers structurally authored in one subtree.</summary>
        /// <param name="nodeId">The source subtree identifier.</param>
        /// <returns>The outgoing transfers in deterministic structural pre-order.</returns>
        public IReadOnlyList<FaultTreeTransferNode> GetOutgoingReferences(Guid nodeId)
        {
            FaultTreeNodeBase source = RequireNode(nodeId, "GetOutgoingReferences", "source");
            return DescendantsAndSelf(source).OfType<FaultTreeTransferNode>().ToArray();
        }

        /// <summary>Completes the public delete operation with all three reference policies.</summary>
        /// <param name="nodeId">The subtree root to remove.</param>
        /// <param name="policy">The internal-reference policy.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the policy is undefined.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the mutation would violate an invariant.</exception>
        private void DeleteTransactional(Guid nodeId, TreeDeletePolicy policy)
        {
            if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
            FaultTreeNodeBase node = RequireNode(nodeId, "Delete", "source");
            if (ReferenceEquals(node, Root)) throw MutationError("Delete", node, Root, "the top-event gate cannot be deleted");
            var removedIds = new HashSet<Guid>(DescendantsAndSelf(node).Select(item => item.Id));
            FaultTreeTransferNode[] incoming = FindInternalIncomingReferences(removedIds);
            if (incoming.Length > 0 && policy == TreeDeletePolicy.RejectIfReferenced)
                throw MutationError("Delete", node, incoming[0],
                    $"the subtree is referenced by internal transfer '{incoming[0].Name}'");

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

        /// <summary>Applies cascade or materialization to internal transfers targeting a removal set.</summary>
        /// <param name="source">The selected removal root used in diagnostics.</param>
        /// <param name="removedIds">The persistent identifiers scheduled for removal.</param>
        /// <param name="policy">The selected reference policy.</param>
        private void ApplyIncomingReferencePolicy(FaultTreeNodeBase source, HashSet<Guid> removedIds,
            TreeDeletePolicy policy)
        {
            if (policy == TreeDeletePolicy.RejectIfReferenced) return;
            if (policy == TreeDeletePolicy.CascadeLinks)
            {
                FaultTreeTransferNode[] incoming = FindInternalIncomingReferences(removedIds);
                for (int i = 0; i < incoming.Length; i++) RemoveSubtree(incoming[i]);
                return;
            }

            while (true)
            {
                FaultTreeTransferNode? incoming = FindInternalIncomingReferences(removedIds).FirstOrDefault();
                if (incoming == null) return;
                try
                {
                    MaterializeLinkCore(incoming);
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException)
                {
                    throw MutationError("Delete", source, incoming,
                        $"the incoming transfer could not be materialized: {ex.Message}");
                }
            }
        }

        /// <summary>Finds authored internal transfers outside a removal set whose targets are inside it.</summary>
        /// <param name="removedIds">The selected removal identifiers.</param>
        /// <returns>The incoming transfers in persistent insertion order.</returns>
        private FaultTreeTransferNode[] FindInternalIncomingReferences(HashSet<Guid> removedIds)
        {
            return _nodes.OfType<FaultTreeTransferNode>()
                .Where(transfer => !removedIds.Contains(transfer.Id)
                    && !transfer.IsExternal
                    && removedIds.Contains(transfer.Target.NodeId))
                .ToArray();
        }

        /// <summary>Materializes one resolved transfer without opening or closing a transaction.</summary>
        /// <param name="transfer">The attached transfer.</param>
        /// <returns>The materialized subtree-root identifier.</returns>
        private Guid MaterializeLinkCore(FaultTreeTransferNode transfer)
        {
            (FaultTree targetTree, FaultTreeNodeBase targetNode) = ResolveTransferTarget(transfer);
            TreeFragment fragment = targetTree.Copy(targetNode.Id);
            FaultTreeNodeBase cloneRoot = BuildFragmentClone(fragment);

            FaultTreeNodeBase parent = transfer.Parent
                ?? throw MutationError("MaterializeLink", transfer, transfer, "the transfer has no authored parent");
            int insertionIndex = parent.MutableChildren.IndexOf(transfer);
            RemoveSubtree(transfer);
            insertionIndex = Math.Min(insertionIndex, parent.MutableChildren.Count);
            AttachDetachedSubtree(cloneRoot, parent, insertionIndex);
            return cloneRoot.Id;
        }

        /// <summary>Resolves one internal or external transfer target with an actionable diagnostic.</summary>
        /// <param name="transfer">The transfer to resolve.</param>
        /// <returns>The target tree and subtree root.</returns>
        private (FaultTree Tree, FaultTreeNodeBase Node) ResolveTransferTarget(FaultTreeTransferNode transfer)
        {
            if (transfer.UnresolvedReferences.Count > 0)
                throw MutationError("MaterializeLink", transfer, transfer,
                    $"the external target is unresolved ({string.Join(", ", transfer.UnresolvedReferences)})");
            FaultTree targetTree = transfer.TargetFunction?.FaultTree ?? this;
            FaultTreeNodeBase? target = transfer.ResolveNode(targetTree, out _);
            if (target == null)
            {
                TreeNodeReference address = transfer.Target;
                throw MutationError("MaterializeLink", transfer, transfer,
                    $"target node '{address.NodeName ?? address.NodeId.ToString("D")}' could not be resolved");
            }
            return (targetTree, target);
        }

        /// <summary>Builds an immutable fault-tree node snapshot for a fragment.</summary>
        /// <param name="node">The source authored node.</param>
        /// <returns>The owned snapshot.</returns>
        /// <exception cref="InvalidOperationException">Thrown when an external transfer target is unresolved.</exception>
        private static FaultTreeFragmentNodeSnapshot CreateFragmentSnapshot(FaultTreeNodeBase node)
        {
            ProbabilitySource? probabilitySource = node is FaultTreeBasicEventNode basic
                ? basic.ProbabilitySource.CloneForFragment()
                : null;
            TreeNodeReference? target = null;
            FaultTreeResponse? targetFunction = null;
            TreeLinkMode linkMode = TreeLinkMode.SharedLogicalEvent;
            if (node is FaultTreeTransferNode transfer)
            {
                if (transfer.IsExternal && transfer.TargetFunction == null)
                    throw new InvalidOperationException(
                        $"FaultTree Copy failed: external transfer '{transfer.Name}' has no resolved target function.");
                target = transfer.Target;
                targetFunction = transfer.TargetFunction;
                linkMode = transfer.LinkMode;
            }
            var gate = node as FaultTreeGateNode;
            var house = node as FaultTreeHouseEventNode;
            return new FaultTreeFragmentNodeSnapshot(node.Id, node.SerializedName, node.Name,
                node.Description, gate?.GateType ?? FaultTreeGateType.And, gate?.K ?? 0,
                house?.State ?? false, probabilitySource, target, targetFunction, linkMode,
                node.Children.Select(child => child.Id).ToArray());
        }

        /// <summary>Builds one fresh detached subtree from a fault-tree fragment.</summary>
        /// <param name="fragment">The immutable fragment.</param>
        /// <returns>The detached clone root.</returns>
        private FaultTreeNodeBase BuildFragmentClone(TreeFragment fragment)
        {
            FaultTreeFragmentPayload payload = fragment.RequirePayload<FaultTreeFragmentPayload>();
            Dictionary<Guid, Guid> freshIds = payload.Nodes.ToDictionary(item => item.SourceId, _ => Guid.NewGuid());
            var clones = new Dictionary<Guid, FaultTreeNodeBase>();
            for (int i = 0; i < payload.Nodes.Count; i++)
            {
                FaultTreeFragmentNodeSnapshot item = payload.Nodes[i];
                FaultTreeNodeBase clone = item.SerializedName switch
                {
                    nameof(FaultTreeGateNode) => new FaultTreeGateNode(freshIds[item.SourceId],
                        item.Name, item.Description, item.GateType, item.K),
                    nameof(FaultTreeBasicEventNode) => new FaultTreeBasicEventNode(freshIds[item.SourceId],
                        item.Name, item.Description, item.ProbabilitySource!.CloneForFragment()),
                    nameof(FaultTreeHouseEventNode) => new FaultTreeHouseEventNode(freshIds[item.SourceId],
                        item.Name, item.Description, item.State),
                    nameof(FaultTreeTransferNode) => CreateFragmentTransferClone(payload, item, freshIds),
                    _ => throw new InvalidOperationException(
                        $"Unsupported fault-tree fragment node '{item.SerializedName}'."),
                };
                clones.Add(item.SourceId, clone);
            }

            for (int i = 0; i < payload.Nodes.Count; i++)
            {
                FaultTreeFragmentNodeSnapshot item = payload.Nodes[i];
                FaultTreeNodeBase clone = clones[item.SourceId];
                for (int child = 0; child < item.ChildIds.Count; child++)
                    clone.MutableChildren.Add(clones[item.ChildIds[child]]);
            }
            return clones[fragment.SourceRootId];
        }

        /// <summary>Creates a fresh transfer node while remapping fragment-internal targets.</summary>
        /// <param name="payload">The fault-tree fragment payload.</param>
        /// <param name="item">The transfer snapshot.</param>
        /// <param name="freshIds">The complete source-to-fresh id map.</param>
        /// <returns>The detached transfer clone.</returns>
        private FaultTreeTransferNode CreateFragmentTransferClone(FaultTreeFragmentPayload payload,
            FaultTreeFragmentNodeSnapshot item, IReadOnlyDictionary<Guid, Guid> freshIds)
        {
            TreeNodeReference sourceTarget = item.Target
                ?? throw new InvalidOperationException("A fault-tree fragment transfer has no target address.");
            TreeNodeReference target;
            FaultTreeResponse? targetFunction;
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
                            $"FaultTree PasteClone failed: preserved internal target '{sourceTarget.NodeName ?? sourceTarget.NodeId.ToString("D")}' no longer exists.");
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
                        "FaultTree PasteClone failed: a cross-tree fragment contains an internal reference outside the fragment, but its source tree has no FaultTreeResponse owner.");
                }
            }
            else
            {
                targetFunction = item.TargetFunction
                    ?? throw new InvalidOperationException(
                        $"FaultTree PasteClone failed: external target function '{sourceTarget.FunctionName}' is unresolved.");
                target = sourceTarget;
            }

            return new FaultTreeTransferNode(freshIds[item.SourceId], item.Name, item.Description,
                item.LinkMode, target, targetFunction, Array.Empty<string>());
        }

        /// <summary>Attaches every node of one prebuilt detached subtree in deterministic pre-order.</summary>
        /// <param name="root">The detached subtree root.</param>
        /// <param name="parent">The attached destination parent.</param>
        /// <param name="insertionIndex">The destination input index.</param>
        private void AttachDetachedSubtree(FaultTreeNodeBase root, FaultTreeNodeBase parent, int insertionIndex)
        {
            FaultTreeNodeBase[] nodes = DescendantsAndSelf(root).ToArray();
            if (nodes.Any(node => node.Owner != null || node.Parent != null))
                throw MutationError("AttachSubtree", root, parent,
                    "the replacement contains an already attached node");
            if (nodes.Select(node => node.Id).Distinct().Count() != nodes.Length
                || nodes.Any(node => _byId.ContainsKey(node.Id)))
                throw MutationError("AttachSubtree", root, parent,
                    "the replacement contains a duplicate persistent id");

            parent.MutableChildren.Insert(insertionIndex, root);
            var stack = new Stack<(FaultTreeNodeBase Node, FaultTreeNodeBase Parent)>();
            stack.Push((root, parent));
            while (stack.Count > 0)
            {
                var item = stack.Pop();
                _nodes.Add(item.Node);
                _byId.Add(item.Node.Id, item.Node);
                item.Node.Attach(this, item.Parent);
                SubscribeNode(item.Node);
                for (int i = item.Node.Children.Count - 1; i >= 0; i--)
                    stack.Push((item.Node.Children[i], item.Node));
            }
        }

        /// <summary>Resolves and validates an optional destination sibling.</summary>
        /// <param name="parent">The selected destination parent.</param>
        /// <param name="beforeSiblingId">The optional sibling id.</param>
        /// <param name="operation">The operation name.</param>
        /// <returns>The resolved sibling, or null.</returns>
        private FaultTreeNodeBase? ResolveDestinationSibling(FaultTreeNodeBase parent, Guid? beforeSiblingId,
            string operation)
        {
            if (!beforeSiblingId.HasValue) return null;
            FaultTreeNodeBase before = RequireNode(beforeSiblingId.Value, operation, "target sibling");
            if (!ReferenceEquals(before.Parent, parent))
                throw MutationError(operation, parent, before,
                    "the target sibling is not a child of the destination parent");
            return before;
        }

        /// <summary>Collects authored nodes reachable by structural inputs and local transfer targets.</summary>
        /// <returns>The reachable persistent identifiers.</returns>
        private HashSet<Guid> CollectReachableNodeIds()
        {
            var reachable = new HashSet<Guid>();
            var stack = new Stack<FaultTreeNodeBase>();
            stack.Push(Root);
            while (stack.Count > 0)
            {
                FaultTreeNodeBase node = stack.Pop();
                if (!reachable.Add(node.Id)) continue;
                for (int i = node.Children.Count - 1; i >= 0; i--) stack.Push(node.Children[i]);
                if (node is not FaultTreeTransferNode transfer) continue;
                FaultTree targetTree = transfer.TargetFunction?.FaultTree ?? this;
                if (!ReferenceEquals(targetTree, this)) continue;
                FaultTreeNodeBase? target = transfer.ResolveNode(this, out _);
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
            if (checkpoint is not TreeMutationSnapshot<FaultTreeNodeBase> snapshot)
                throw new ArgumentException("The fault-tree mutation checkpoint is invalid.", nameof(checkpoint));
            RestoreMutationSnapshot(snapshot);
        }

        /// <summary>Captures every mutable topology field needed for rollback.</summary>
        /// <returns>The complete structural mutation snapshot.</returns>
        private TreeMutationSnapshot<FaultTreeNodeBase> CaptureMutationSnapshot()
        {
            return new TreeMutationSnapshot<FaultTreeNodeBase>(_nodes, node => node.Parent,
                node => node.Children, null, _ownerResponse?.CreateCompiledPlanCheckpoint());
        }

        /// <summary>Restores topology, ownership, ids, and child order after failure.</summary>
        /// <param name="snapshot">The pre-mutation state.</param>
        private void RestoreMutationSnapshot(TreeMutationSnapshot<FaultTreeNodeBase> snapshot)
        {
            var original = new HashSet<FaultTreeNodeBase>(snapshot.Nodes);
            foreach (FaultTreeNodeBase node in _nodes.Where(node => !original.Contains(node)).ToArray())
            {
                node.MutableChildren.Clear();
                node.Detach();
            }
            for (int i = 0; i < snapshot.Nodes.Count; i++) snapshot.Nodes[i].MutableChildren.Clear();
            for (int i = 0; i < snapshot.Nodes.Count; i++)
            {
                FaultTreeNodeBase node = snapshot.Nodes[i];
                node.MutableChildren.AddRange(snapshot.Children[node]);
            }

            _nodes.Clear();
            _nodes.AddRange(snapshot.Nodes);
            _byId.Clear();
            for (int i = 0; i < snapshot.Nodes.Count; i++)
            {
                FaultTreeNodeBase node = snapshot.Nodes[i];
                _byId.Add(node.Id, node);
                node.Attach(this, snapshot.Parents[node]);
            }
            ReconcileNodeSubscriptions();
            if (_ownerResponse != null && snapshot.OwnerCheckpoint != null)
                _ownerResponse.RestoreCompiledPlanCheckpoint(snapshot.OwnerCheckpoint);
        }

        /// <summary>The fault-tree-specific immutable payload held by a public fragment.</summary>
        private sealed class FaultTreeFragmentPayload
        {
            /// <summary>Initializes a fragment payload.</summary>
            /// <param name="sourceTree">The source tree.</param>
            /// <param name="sourceOwner">The source response owner, when attached.</param>
            /// <param name="nodes">The immutable local snapshots in source pre-order.</param>
            internal FaultTreeFragmentPayload(FaultTree sourceTree, FaultTreeResponse? sourceOwner,
                IReadOnlyList<FaultTreeFragmentNodeSnapshot> nodes)
            {
                SourceTree = sourceTree;
                SourceOwner = sourceOwner;
                Nodes = nodes;
                ById = nodes.ToDictionary(node => node.SourceId);
            }

            /// <summary>The source tree used to preserve same-tree external-fragment references.</summary>
            internal FaultTree SourceTree { get; }

            /// <summary>The source response used to promote cross-tree external-fragment references.</summary>
            internal FaultTreeResponse? SourceOwner { get; }

            /// <summary>The immutable local snapshots in source pre-order.</summary>
            internal IReadOnlyList<FaultTreeFragmentNodeSnapshot> Nodes { get; }

            /// <summary>The source-id snapshot lookup.</summary>
            internal IReadOnlyDictionary<Guid, FaultTreeFragmentNodeSnapshot> ById { get; }
        }

        /// <summary>One immutable fault-tree node snapshot inside a fragment.</summary>
        private sealed class FaultTreeFragmentNodeSnapshot
        {
            /// <summary>Initializes a node snapshot.</summary>
            /// <param name="sourceId">The source persistent id.</param>
            /// <param name="serializedName">The concrete node kind.</param>
            /// <param name="name">The display name.</param>
            /// <param name="description">The display description.</param>
            /// <param name="gateType">The gate combination, for gate nodes.</param>
            /// <param name="k">The k-of-n threshold, for gate nodes.</param>
            /// <param name="state">The deterministic state, for house events.</param>
            /// <param name="probabilitySource">The owned source snapshot, for basic events.</param>
            /// <param name="target">The transfer target, for transfers.</param>
            /// <param name="targetFunction">The external target function, for transfers.</param>
            /// <param name="linkMode">The transfer semantics, for transfers.</param>
            /// <param name="childIds">The source child ids in persistent order.</param>
            internal FaultTreeFragmentNodeSnapshot(Guid sourceId, string serializedName, string name,
                string description, FaultTreeGateType gateType, int k, bool state,
                ProbabilitySource? probabilitySource, TreeNodeReference? target,
                FaultTreeResponse? targetFunction, TreeLinkMode linkMode, IReadOnlyList<Guid> childIds)
            {
                SourceId = sourceId;
                SerializedName = serializedName;
                Name = name;
                Description = description;
                GateType = gateType;
                K = k;
                State = state;
                ProbabilitySource = probabilitySource;
                Target = target;
                TargetFunction = targetFunction;
                LinkMode = linkMode;
                ChildIds = childIds;
            }

            /// <summary>The source persistent id.</summary>
            internal Guid SourceId { get; }

            /// <summary>The concrete node kind.</summary>
            internal string SerializedName { get; }

            /// <summary>The display name.</summary>
            internal string Name { get; }

            /// <summary>The display description.</summary>
            internal string Description { get; }

            /// <summary>The gate combination, for gate nodes.</summary>
            internal FaultTreeGateType GateType { get; }

            /// <summary>The k-of-n threshold, for gate nodes.</summary>
            internal int K { get; }

            /// <summary>The deterministic state, for house events.</summary>
            internal bool State { get; }

            /// <summary>The owned source snapshot, for basic events.</summary>
            internal ProbabilitySource? ProbabilitySource { get; }

            /// <summary>The transfer target, for transfers.</summary>
            internal TreeNodeReference? Target { get; }

            /// <summary>The external target function, for transfers.</summary>
            internal FaultTreeResponse? TargetFunction { get; }

            /// <summary>The transfer semantics, for transfers.</summary>
            internal TreeLinkMode LinkMode { get; }

            /// <summary>The source child ids in persistent order.</summary>
            internal IReadOnlyList<Guid> ChildIds { get; }
        }
    }
}
